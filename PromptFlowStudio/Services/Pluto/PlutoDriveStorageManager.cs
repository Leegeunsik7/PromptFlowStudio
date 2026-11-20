using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>☁️ Pluto 영속 JSON 저장소 — Google Drive 볼트 우선 · 로컬 폴백 · 원자적 기록.</summary>
public interface IPlutoStorageManager
{
    /// <summary>현재 활성 루트(Drive 또는 로컬 폴백) 기준 파일 경로 — 디렉터리 자동 생성 · 미존재 시 레거시/폴백 사본 1회 시드.</summary>
    string ResolveDataPath(string filename);

    /// <summary>텍스트 읽기 — 파일 없음/IO 실패 → 빈 문자열(예외 미전파 · 취소만 전파).</summary>
    Task<string> ReadAllTextAsync(string filename, CancellationToken ct = default);

    /// <summary>원자적 기록(<c>.tmp</c> → 교체) — Drive 실패 시 로컬 폴백 재기록. 반환 = 어느 한 곳이라도 성공.</summary>
    Task<bool> WriteAllTextAtomicAsync(string filename, string content, CancellationToken ct = default);

    /// <summary>Drive 볼트 활성 여부.</summary>
    bool IsCloudSynced { get; }

    /// <summary>현재 활성 루트 디렉터리.</summary>
    string ActiveRoot { get; }

    /// <summary>UI 배지 문구(☁️ 연동됨 / 💾 로컬 폴백).</summary>
    string StatusLabel { get; }

    /// <summary>활성 루트 전환(Drive ↔ 로컬) 통지.</summary>
    event Action? Changed;
}

/// <summary>
/// ☁️ <c>G:\내 드라이브\DATA\Pluto</c> 볼트 — Drive 클라이언트 미마운트/오프라인(<c>G:\내 드라이브\DATA</c> 부재) 또는 기록 실패 시
/// <c>%LOCALAPPDATA%\PromptFlowStudio\Data\Pluto</c> 로 IO 예외 없이 폴백. 가용성 판정은 <see cref="ProbeInterval"/> 캐시(핫패스 디스크 프로브 억제).
/// 첫 해석 시 대상 파일이 없으면 폴백 → 레거시(<c>%LOCALAPPDATA%\PromptFlowStudio</c>) 사본을 복사 시드(누적 XP/프로필 보존 · 원본 삭제 안 함).
/// 비 Windows(모바일) → Drive 경로 비활성.
/// </summary>
public sealed class PlutoDriveStorageManager : IPlutoStorageManager
{
    public const string DriveVaultRoot = @"G:\내 드라이브\DATA";
    public const string SubFolder = "Pluto";
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private readonly string? _primary;
    private readonly string _fallback;
    private readonly string? _legacy;
    private readonly object _sync = new();
    private long _nextProbeTick;
    private long _primaryDownUntilTick;
    private bool _primaryOk;
    private bool _probed;

    public PlutoDriveStorageManager() : this(DefaultPrimaryRoot(), null, DefaultLegacyRoot()) { }

    /// <param name="primaryRoot">Drive 측 Pluto 폴더(부모 폴더가 있어야 활성) · null → Drive 비활성.</param>
    /// <param name="fallbackRoot">로컬 폴백 폴더 · null → <c>%LOCALAPPDATA%\PromptFlowStudio\Data\Pluto</c>.</param>
    /// <param name="legacyRoot">구 저장 위치(시드 원천) · null → 시드 안 함.</param>
    public PlutoDriveStorageManager(string? primaryRoot, string? fallbackRoot = null, string? legacyRoot = null)
    {
        _primary = string.IsNullOrWhiteSpace(primaryRoot) ? null : primaryRoot;
        _fallback = string.IsNullOrWhiteSpace(fallbackRoot)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", "Data", SubFolder)
            : fallbackRoot;
        _legacy = string.IsNullOrWhiteSpace(legacyRoot) ? null : legacyRoot;
    }

    /// <summary>Drive 볼트 비활성 스위치 — 값이 "off"면 Drive 미사용(테스트 하네스가 실 백업 접근 차단용으로 설정).</summary>
    public const string DriveEnvVar = "PROMPTFLOW_PLUTO_DRIVE";

    public static string? DefaultPrimaryRoot()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(DriveEnvVar)?.Trim(), "off", StringComparison.OrdinalIgnoreCase)) return null;
        return OperatingSystem.IsWindows() ? Path.Combine(DriveVaultRoot, SubFolder) : null;
    }

    public static string DefaultLegacyRoot() => Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio");

    public string? PrimaryRoot => _primary;
    public string FallbackRoot => _fallback;

    public event Action? Changed;

    public bool IsCloudSynced => PrimaryAvailable();

    public string ActiveRoot => PrimaryAvailable() ? _primary! : EnsureDir(_fallback);

    /// <summary>UI 배지 문구.</summary>
    public string StatusLabel => IsCloudSynced ? "☁️ " + _primary + " 연동됨" : "💾 로컬 폴백";

    public string ResolveDataPath(string filename)
    {
        ValidateName(filename);
        var root = ActiveRoot;
        var path = Path.Combine(root, filename);
        if (!File.Exists(path)) Seed(path, filename, root);
        return path;
    }

    public async Task<string> ReadAllTextAsync(string filename, CancellationToken ct = default)
    {
        var path = ResolveDataPath(filename);
        var text = await TryReadAsync(path, ct).ConfigureAwait(false);
        if (text is not null) return text;
        // Drive 읽기 실패(오프라인 전환 직후 등) → 로컬 사본
        var local = Path.Combine(_fallback, filename);
        if (!string.Equals(local, path, StringComparison.OrdinalIgnoreCase))
            return await TryReadAsync(local, ct).ConfigureAwait(false) ?? string.Empty;
        return string.Empty;
    }

    public async Task<bool> WriteAllTextAtomicAsync(string filename, string content, CancellationToken ct = default)
    {
        ValidateName(filename);
        ArgumentNullException.ThrowIfNull(content);
        if (PrimaryAvailable())
        {
            if (await PlutoJsonStore.WriteAtomicAsync(Path.Combine(_primary!, filename), content, "Pluto.Drive", ct).ConfigureAwait(false))
                return true;
            MarkPrimaryDown();   // 클라우드 동기화 잠금·오프라인 → 로컬 폴백
        }
        return await PlutoJsonStore.WriteAtomicAsync(Path.Combine(EnsureDir(_fallback), filename), content, "Pluto.Local", ct).ConfigureAwait(false);
    }

    private bool PrimaryAvailable()
    {
        if (_primary is null) return false;
        bool changed, ok;
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (_probed && now < _nextProbeTick) return _primaryOk;
            var prev = _primaryOk;
            ok = now >= _primaryDownUntilTick && Probe(_primary);
            changed = _probed && prev != ok;
            _primaryOk = ok;
            _probed = true;
            _nextProbeTick = now + (long)ProbeInterval.TotalMilliseconds;
        }
        if (changed) RaiseChanged();
        return ok;
    }

    private void MarkPrimaryDown()
    {
        bool changed;
        lock (_sync)
        {
            var now = Environment.TickCount64;
            changed = _primaryOk;
            _primaryOk = false;
            _probed = true;
            _primaryDownUntilTick = now + (long)ProbeInterval.TotalMilliseconds;
            _nextProbeTick = _primaryDownUntilTick;
        }
        if (changed) RaiseChanged();
    }

    /// <summary>부모(볼트 루트) 존재 시에만 하위 Pluto 폴더 생성 — 미마운트 드라이브에 임의 폴더 생성 방지.</summary>
    private static bool Probe(string primary)
    {
        try
        {
            var parent = Path.GetDirectoryName(primary);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return false;
            Directory.CreateDirectory(primary);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] probe failed: " + ex.Message);
            return false;
        }
    }

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mkdir failed: " + ex.Message); }
        return dir;
    }

    /// <summary>대상 부재 → (Drive 활성 시) 로컬 폴백 사본 → 레거시 사본 순으로 복사 시드. 실패 무시(빈 상태로 시작).</summary>
    private void Seed(string target, string filename, string root)
    {
        if (!string.Equals(root, _fallback, StringComparison.OrdinalIgnoreCase) && TryCopy(Path.Combine(_fallback, filename), target)) return;
        if (_legacy is not null) TryCopy(Path.Combine(_legacy, filename), target);
    }

    private static bool TryCopy(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return false;
            File.Copy(src, dst, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] seed failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 🔁 핵심 허브 복원 — Drive 활성 시 Drive 에 있고 로컬 폴백에 없는 파일을 로컬로 복사(실패 무시).
    /// 🪞 Step 17: 로컬 사본이 이미 있어도 Drive 사본이 확실히 더 새로우면(<see cref="IsMirrorStale"/>) 로컬 미러를 갱신한다 —
    /// 첫 복원 이후 미러가 그 시점에 얼어붙어, 오프라인 전환 시 몇 주 전 학습 상태로 조용히 되돌아가던 문제 차단. 더 새로운 로컬 사본은 절대 덮어쓰지 않는다.
    /// 새 PC 첫 실행/오프라인 전환 대비. Drive 비활성(<c>PROMPTFLOW_PLUTO_DRIVE=off</c> · 미마운트 · 비 Windows) → 0.
    /// </summary>
    /// <returns>복원 + 갱신된 파일 수.</returns>
    public int RestoreCoreHubsToLocal(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!PrimaryAvailable()) return 0;
        var local = EnsureDir(_fallback);
        if (string.Equals(local, _primary, StringComparison.OrdinalIgnoreCase)) return 0;
        var restored = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            try { ValidateName(f); }
            catch (ArgumentException) { continue; }
            var dst = Path.Combine(local, f);
            var src = Path.Combine(_primary!, f);
            if (!File.Exists(dst)) { if (TryCopy(src, dst)) restored++; continue; }
            if (IsMirrorStale(src, dst) && TryRefreshMirror(src, dst)) restored++;
        }
        return restored;
    }

    /// <summary>🪞 로컬 미러 신선도 허용 오차 — 파일 시스템 타임스탬프 해상도(FAT 2초 · Drive 가상 FS) 흡수.</summary>
    public static readonly TimeSpan MirrorStalenessTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 🪞 로컬 미러가 낡았는지(순수 판정 · IO 실패 → false) — Drive 사본이 <see cref="MirrorStalenessTolerance"/> 넘게 더 새롭고 · 비어 있지 않고 · 유효한 JSON 일 때만 true.
    /// 로컬이 같거나 더 새로우면(오프라인 중 기록 포함) 절대 덮어쓰지 않는다 · 동기화 도중의 손상/빈 Drive 사본도 정상 로컬 미러를 대체하지 못한다.
    /// </summary>
    public static bool IsMirrorStale(string drivePath, string localPath)
    {
        try
        {
            var src = new FileInfo(drivePath);
            var dst = new FileInfo(localPath);
            if (!src.Exists || !dst.Exists || src.Length == 0) return false;
            if (src.LastWriteTimeUtc - dst.LastWriteTimeUtc <= MirrorStalenessTolerance) return false;
            using var fs = new FileStream(drivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mirror probe: " + ex.Message);
            return false;
        }
    }

    /// <summary>🪞 Drive → 로컬 미러 갱신(임시 파일 → 원자 교체 · 수정 시각 보존 → 멱등) — 실패 시 기존 로컬 사본 유지.</summary>
    private static bool TryRefreshMirror(string src, string dst)
    {
        var tmp = dst + ".mirror.tmp";
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mirror refresh failed: " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mirror tmp cleanup: " + ex2.Message); }
            return false;
        }
    }

    private static async Task<string?> TryReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= PlutoJsonStore.MaxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine("[Pluto.Drive] read failed: " + ex.Message);
                    return null;
                }
                await Task.Delay(PlutoJsonStore.RetryDelay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateName(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("invalid file name", nameof(filename));
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] Changed handler: " + ex.Message); }
        }
    }
}
