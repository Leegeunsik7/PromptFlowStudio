using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Operations;

public enum PackagePlatform { WindowsDesktop, Android, Ios, SharedNuGet }

/// <summary>패키징 완료 1건 — 플랫폼/산출 zip 경로/크기/포함 파일 수.</summary>
public sealed record PackagedArtifact(PackagePlatform Platform, string DisplayName, string ArchivePath, long Bytes, int FileCount, DateTimeOffset PackagedAt);

/// <summary>전체 패키징 리포트 — 성공분/스킵 사유(Graceful)/소요시간/산출 루트.</summary>
public sealed record PackagingReport(bool AnyPackaged, IReadOnlyList<PackagedArtifact> Packaged, IReadOnlyList<string> Skipped, double ElapsedMs, string DistRoot);

/// <summary>합본 릴리스 zip에 포함된 플랫폼 슬롯 1건.</summary>
public sealed record ReleaseZipSlot(PackagePlatform Platform, string DisplayName, int FileCount);

/// <summary>v10.7.0 합본 릴리스 zip 결과 — 슬롯별 개별 zip(<see cref="PackagingReport"/>)과 달리 하나의 파일.</summary>
public sealed record ReleaseZipReport(bool AnyIncluded, string ZipPath, long Bytes, IReadOnlyList<ReleaseZipSlot> Included, IReadOnlyList<string> Skipped, double ElapsedMs);

/// <summary>
/// v10.3.1 프로덕션 아티팩트 패키저 — Desktop(현재 실행 중인 빌드 출력 자신)/Android/iOS/Shared RCL 산출물을
/// 플랫폼별 zip(<see cref="ZipFile"/>, 순수 BCL)으로 번들링한다. 모바일/Shared 산출물은 조상 디렉터리에서 형제
/// 프로젝트 폴더를 탐색해 있으면 패키징, 없으면(워크로드/빌드 미실행) 사유를 남기고 Graceful 스킵 — 부분 성공을
/// 실패로 취급하지 않는다. 산출물은 소스 트리 밖(<c>%LocalAppData%\PromptFlowStudio\Dist</c>)에 저장해 자기 자신을
/// 재귀 포함하는 사고를 원천 차단한다. 하드웨어 비의존 · 데스크톱/모바일 공용(개념상 데스크톱 릴리스 엔지니어링 도구).
/// </summary>
public sealed class ProductionArtifactPackager
{
    /// <summary>패키징 산출 루트(소스 트리 밖 · <see cref="Media.LocalMediaService.MediaRoot"/> 와 동일 규약의 형제 폴더).</summary>
    public static string DistRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Dist");

    /// <summary>v10.7.0 합본 릴리스 zip 산출 루트 — DistRoot(플랫폼별 개별 zip)와 별개, 최종 배포 아티팩트 전용.</summary>
    public static string ArtifactsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Artifacts");

    private readonly object _gate = new();
    private long _packagedCount, _skippedCount;
    private PackagingReport? _last;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public PackagingReport? LastReport { get { lock (_gate) return _last; } }
    public long PackagedCount => Interlocked.Read(ref _packagedCount);
    public long SkippedCount => Interlocked.Read(ref _skippedCount);

    /// <summary>4개 플랫폼 슬롯(Windows/Android/iOS/SharedNuGet)을 순회 패키징 — 존재하는 산출물만 zip화.</summary>
    public async Task<PackagingReport> PackageAllAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(DistRoot);
        var packaged = new List<PackagedArtifact>();
        var skipped = new List<string>();

        // ① Windows Desktop — 현재 프로세스가 실행 중인 실 빌드 출력 그 자체(자기완결적·항상 존재).
        await TryPackageAsync(PackagePlatform.WindowsDesktop, "Windows Desktop", AppContext.BaseDirectory, packaged, skipped, ct).ConfigureAwait(false);

        // ②~④ 형제 프로젝트 산출물(없으면 Graceful 스킵 — 워크로드 미설치/미빌드 환경에서도 부분 성공 인정).
        await TryPackageSiblingAsync(PackagePlatform.Android, "Android", "PromptFlowStudio.Mobile", Path.Combine("bin", "Release", "net8.0-android"), packaged, skipped, ct).ConfigureAwait(false);
        await TryPackageSiblingAsync(PackagePlatform.Ios, "iOS", "PromptFlowStudio.Mobile", Path.Combine("bin", "Release", "net8.0-ios"), packaged, skipped, ct).ConfigureAwait(false);
        await TryPackageSiblingAsync(PackagePlatform.SharedNuGet, "Shared RCL", "PromptFlowStudio.Shared", Path.Combine("bin", "Release", "net8.0"), packaged, skipped, ct).ConfigureAwait(false);

        sw.Stop();
        var report = new PackagingReport(packaged.Count > 0, packaged, skipped, sw.Elapsed.TotalMilliseconds, DistRoot);
        lock (_gate) { _last = report; LastAction = $"패키징 완료 · 성공 {packaged.Count} · 스킵 {skipped.Count}"; }
        OnChange?.Invoke();
        return report;
    }

    /// <summary>
    /// v10.7.0-LTS: 4개 플랫폼 슬롯을 <b>하나의</b> 배포용 zip(버전 태그 파일명)으로 합본 — 각 슬롯은 zip 내부
    ///   서브폴더(<c>windows/</c>·<c>android/</c>·<c>ios/</c>·<c>shared/</c>)로 평탄화해 담아, 최종 zip을 열어보는
    ///   것만으로 각 슬롯의 DLL 존재를 바로 확인할 수 있게 한다. <see cref="PackageAllAsync"/>(슬롯별 개별 zip,
    ///   <see cref="DistRoot"/>)와는 별개 산출물 — 기존 메서드/동작은 무변경(재사용: <see cref="FindSiblingOutput"/>).
    ///   ⚠ Windows 슬롯은 기본적으로 <see cref="AppContext.BaseDirectory"/>(호출 프로세스 자신의 빌드 출력)를 담는다 —
    ///   릴리스 엔지니어링 스크립트 등 "실행 중이 아닌 다른 빌드"를 패키징해야 하는 호출측을 위해
    ///   <paramref name="windowsSourceDirOverride"/> 로 명시적 재정의를 허용한다(기본값 null 이면 기존과 동일 동작).
    /// </summary>
    private long _excludedFiles;
    /// <summary>v14.0.0: 배포 제외 규칙으로 걸러낸 누적 파일 수(pdb·publish 중간산출·최상위 RID 폴더).</summary>
    public long ExcludedFileCount => Interlocked.Read(ref _excludedFiles);

    private static readonly string[] RidFolders = { "win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" };

    /// <summary>
    /// v14.0.0: 빌드 출력 폴더의 파일이 배포 번들에 들어가도 되는지(상대경로 기준). 이전엔 필터가 없어 폴더를 통째로 담았고,
    ///   그 결과 디버그 심볼(*.pdb)과 과거 <c>dotnet publish -r win-x64</c> 진단이 출력 폴더 안에 남긴 <c>win-x64/</c>·<c>publish/</c>
    ///   중간산출이 릴리스 zip 에 섞였다.
    ///   ▸ 제외: *.pdb · 경로 어디든 'publish' 세그먼트 · 최상위 RID 폴더(win-x64 등).
    ///   ▸ 유지: runtimes/&lt;rid&gt;/native 같은 정상 런타임 자산(최상위가 아니므로), wwwroot 등 나머지 전부.
    /// </summary>
    internal static bool IsShippable(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = rel.Split('/');
        if (segments.Length > 1 && Array.Exists(RidFolders, r => r.Equals(segments[0], StringComparison.OrdinalIgnoreCase))) return false;
        for (var i = 0; i < segments.Length - 1; i++)
            if (segments[i].Equals("publish", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>소스 폴더에서 배포 가능한 파일만(상대경로, 절대경로) 열거 - 제외 수는 누적 카운터에 반영.</summary>
    private List<(string Rel, string Full)> ShippableFiles(string sourceDir)
    {
        var list = new List<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, f).Replace('\\', '/');
            if (IsShippable(rel)) list.Add((rel, f)); else Interlocked.Increment(ref _excludedFiles);
        }
        return list;
    }

    public async Task<ReleaseZipReport> PackageReleaseZipAsync(string version, string? windowsSourceDirOverride = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(ArtifactsRoot);
        var zipPath = Path.Combine(ArtifactsRoot, $"{version}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        var included = new List<ReleaseZipSlot>();
        var skipped = new List<string>();
        var slots = new (PackagePlatform Platform, string DisplayName, string SubFolder, string? SourceDir)[]
        {
            (PackagePlatform.WindowsDesktop, "Windows Desktop", "windows", windowsSourceDirOverride ?? AppContext.BaseDirectory),
            (PackagePlatform.Android, "Android", "android", FindSiblingOutput("PromptFlowStudio.Mobile", Path.Combine("bin", "Release", "net8.0-android"))),
            (PackagePlatform.Ios, "iOS", "ios", FindSiblingOutput("PromptFlowStudio.Mobile", Path.Combine("bin", "Release", "net8.0-ios"))),
            (PackagePlatform.SharedNuGet, "Shared RCL", "shared", FindSiblingOutput("PromptFlowStudio.Shared", Path.Combine("bin", "Release", "net8.0"))),
        };

        await Task.Run(() =>
        {
            using var fs = File.Create(zipPath);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var slot in slots)
            {
                ct.ThrowIfCancellationRequested();
                if (slot.SourceDir is null || !Directory.Exists(slot.SourceDir))
                {
                    skipped.Add($"{slot.DisplayName}: 산출물 없음(워크로드/빌드 미실행)");
                    Interlocked.Increment(ref _skippedCount);
                    continue;
                }
                var files = ShippableFiles(slot.SourceDir);
                if (files.Count == 0) { skipped.Add($"{slot.DisplayName}: 빈 폴더"); Interlocked.Increment(ref _skippedCount); continue; }
                foreach (var (rel, full) in files)
                    archive.CreateEntryFromFile(full, $"{slot.SubFolder}/{rel}", CompressionLevel.Optimal);
                included.Add(new ReleaseZipSlot(slot.Platform, slot.DisplayName, files.Count));
                Interlocked.Increment(ref _packagedCount);
            }
        }, ct).ConfigureAwait(false);

        sw.Stop();
        var bytes = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;
        var report = new ReleaseZipReport(included.Count > 0, zipPath, bytes, included, skipped, sw.Elapsed.TotalMilliseconds);
        lock (_gate) LastAction = $"릴리스 zip 생성 · {included.Count}슬롯 포함 · 스킵 {skipped.Count} · 배포 제외 파일 누적 {ExcludedFileCount} · {zipPath}";
        OnChange?.Invoke();
        return report;
    }

    private async Task TryPackageSiblingAsync(PackagePlatform platform, string displayName, string siblingProjectFolder, string outputSubPath,
        List<PackagedArtifact> packaged, List<string> skipped, CancellationToken ct)
    {
        var dir = FindSiblingOutput(siblingProjectFolder, outputSubPath);
        if (dir is null) { skipped.Add($"{displayName}: {siblingProjectFolder}/{outputSubPath.Replace('\\', '/')} 산출물 없음(워크로드/빌드 미실행)"); Interlocked.Increment(ref _skippedCount); return; }
        await TryPackageAsync(platform, displayName, dir, packaged, skipped, ct).ConfigureAwait(false);
    }

    private async Task TryPackageAsync(PackagePlatform platform, string displayName, string sourceDir, List<PackagedArtifact> packaged, List<string> skipped, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) { skipped.Add($"{displayName}: 소스 폴더 없음({sourceDir})"); Interlocked.Increment(ref _skippedCount); return; }
            var files = ShippableFiles(sourceDir);
            if (files.Count == 0) { skipped.Add($"{displayName}: 빈 폴더"); Interlocked.Increment(ref _skippedCount); return; }

            var archivePath = Path.Combine(DistRoot, $"{platform.ToString().ToLowerInvariant()}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(DistRoot);
                if (File.Exists(archivePath)) File.Delete(archivePath);
                using var fs = File.Create(archivePath);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var (rel, full) in files) archive.CreateEntryFromFile(full, rel, CompressionLevel.Optimal);   // v14.0.0: 동일 배포 제외 규칙 적용
            }, ct).ConfigureAwait(false);

            var bytes = new FileInfo(archivePath).Length;
            packaged.Add(new PackagedArtifact(platform, displayName, archivePath, bytes, files.Count, DateTimeOffset.UtcNow));
            Interlocked.Increment(ref _packagedCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { skipped.Add($"{displayName}: 실패({ex.Message})"); Interlocked.Increment(ref _skippedCount); }
    }

    // 형제 프로젝트 폴더를 조상 디렉터리에서 최대 6단계 상향 탐색 후 지정 출력 서브경로 존재 확인(리포 배치 편차에 견고).
    private static string? FindSiblingOutput(string siblingProjectFolder, string outputSubPath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidateProj = Path.Combine(dir.FullName, siblingProjectFolder);
            if (!Directory.Exists(candidateProj)) continue;
            var full = Path.Combine(candidateProj, outputSubPath);
            var resolved = ResolveActualOutputDir(full);
            if (resolved is not null) return resolved;
        }
        return null;
    }

    /// <summary>
    /// v11.0.1-Mobile-MultiPlatform: MAUI RID 게시 레이아웃 보강 — 지정 TFM 폴더(예: <c>net8.0-android</c>) 직속에
    ///   파일이 없어도, 하위에 RID 서브폴더(예: <c>android-arm64/</c>)가 정확히 1개뿐이고 그 안에 실 산출물이 있으면
    ///   그 폴더까지 1단 자동 하강한다(csproj 의 RuntimeIdentifier(s) 설정과 일치하는 흔한 레이아웃). 애매하면(하위
    ///   폴더 0개 또는 2개 이상) 원래 경로를 그대로 반환해 호출측의 기존 "빈 폴더" Graceful 스킵 동작을 보존한다.
    /// </summary>
    internal static string? ResolveActualOutputDir(string candidate)
    {
        if (!Directory.Exists(candidate)) return null;
        if (Directory.EnumerateFiles(candidate).Any()) return candidate;
        var subdirs = Directory.GetDirectories(candidate);
        if (subdirs.Length == 1 && Directory.EnumerateFiles(subdirs[0], "*", SearchOption.AllDirectories).Any())
            return subdirs[0];
        return candidate;
    }

    public void ResetAll()
    {
        lock (_gate) { _last = null; _packagedCount = 0; _skippedCount = 0; LastAction = "패키저 초기화"; }
        OnChange?.Invoke();
    }
}
