using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Export;

/// <summary>StockSuite 내보내기 결과 — NeedsPathSetup=true 면 대상 경로 미설정/무효(환경설정 안내).</summary>
public sealed record StockSuiteExportResult(bool Ok, string? TargetPath, bool NeedsPathSetup, string Message);

/// <summary>💼 StockSuite 팩 일괄 내보내기 결과 — Exported = 넘버링된 대상 경로(순서 보존).</summary>
public sealed record StockSuiteBatchPackResult(IReadOnlyList<string> Exported, int Failed, bool NeedsPathSetup, string Message)
{
    public bool Ok => Exported.Count > 0;
}

/// <summary>StockSuite 내보내기 포맷 —MP4(기본 복사) · WebM VP9(HTS 저사양 최적화 트랜스코딩).</summary>
public enum StockSuiteExportFormat { Mp4, WebMVp9 }

/// <summary>StockSuite 실행 파일 경로 검증 상태.</summary>
public enum StockSuiteExeStatus { NotConfigured, NotFound, NotExecutable, Ok }

/// <summary>🚀 StockSuite 실행 결과 — AlreadyRunning=true 면 재실행 확인 필요.</summary>
public sealed record StockSuiteLaunchResult(bool Ok, bool AlreadyRunning, string Message);

/// <summary>
/// [PromptFlowStudio] 💼 StockSuite 원클릭 에셋 내보내기 — 선택 영상을 HTS/StockSuite 에셋 폴더에 표준 명칭으로 원자적 덮어쓰기.
///   ▸ MP4: ffmpeg <c>-c:v copy -an</c> 제로-오디오 스트립(부재/실패 시 원본 복사) → 대상 폴더 내 임시 파일(<c>.~pfs</c>) → <see cref="File.Move(string,string,bool)"/> 교체(동일 볼륨 원자적) → <c>bg_ambient.mp4</c>.
///   ▸ WebM: <c>ffmpeg -i in -c:v libvpx-vp9 -crf 32 -b:v 0 -an</c> → <c>bg_ambient.webm</c>. ffmpeg 부재/실패 시 MP4 복사로 폴백.
///   ▸ 적용 상태: 대상 옆 출처 마커(<c>.pfs-src</c>) 또는 MP4 SHA-256 일치 → <see cref="IsApplied"/>.
///   ▸ 경로·포맷은 환경설정 공급자 · 명시 인자 우선.
/// </summary>
public sealed class StockSuiteExporter
{
    public const string DefaultAssetName = "bg_ambient.mp4";
    public const string WebMAssetName = "bg_ambient.webm";
    public const string SourceMarkerSuffix = ".pfs-src";
    private const string TempExt = ".~pfs";

    private readonly Func<string?> _targetDir;
    private readonly Func<string?> _format;
    private readonly Func<string?> _resolveFfmpeg;
    private readonly FfmpegRunner _run;
    private readonly object _hashLock = new();
    private readonly Dictionary<string, bool> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?> _exePath;
    private readonly Func<bool> _sharpen;

    /// <summary>HTS 대화면 고선명 필터 — 1080p Lanczos 업스케일 + 언샵 마스크 샤프닝.</summary>
    public const string SharpenFilter = "scale=1920:1080:flags=lanczos,unsharp=5:5:0.8:3:3:0.4";

    public StockSuiteExporter() : this(null) { }

    /// <param name="targetDirProvider">설정된 StockSuite 에셋 경로 공급자(null/빈 값 = 미설정).</param>
    /// <param name="formatProvider">기본 포맷 공급자(<c>mp4</c>|<c>webm</c> · null = mp4).</param>
    /// <param name="resolveFfmpeg">ffmpeg 경로 조회(null = <see cref="FfmpegLocator"/>).</param>
    /// <param name="runner">ffmpeg 실행기(테스트 주입 · null = <see cref="Engines.ProcessRunner"/>).</param>
    /// <param name="executableProvider">StockSuite.exe 경로 공급자(🚀 원클릭 실행 · null = 미설정).</param>
    /// <param name="sharpenProvider">고선명 샤프닝 기본값 공급자(null = OFF · 앱 DI 는 환경설정 기본 ON).</param>
    public StockSuiteExporter(Func<string?>? targetDirProvider, Func<string?>? formatProvider = null, Func<string?>? resolveFfmpeg = null, FfmpegRunner? runner = null, Func<string?>? executableProvider = null, Func<bool>? sharpenProvider = null)
    {
        _exePath = executableProvider ?? (static () => null);
        _sharpen = sharpenProvider ?? (static () => false);
        _targetDir = targetDirProvider ?? (static () => null);
        _format = formatProvider ?? (static () => null);
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        _run = runner ?? DefaultRunAsync;
    }

    public string? ConfiguredDirectory
    {
        get
        {
            try { var d = _targetDir(); return string.IsNullOrWhiteSpace(d) ? null : d.Trim().Trim('"'); }
            catch (Exception ex) { Program.Log("StockSuite.Path", ex); return null; }
        }
    }

    public bool IsConfigured => ConfiguredDirectory is not null;

    /// <summary>환경설정 기본 포맷 — <c>webm</c> 이면 VP9, 그 외 MP4.</summary>
    public StockSuiteExportFormat DefaultFormat
    {
        get
        {
            try { return ParseFormat(_format()); }
            catch (Exception ex) { Program.Log("StockSuite.Format", ex); return StockSuiteExportFormat.Mp4; }
        }
    }

    public static StockSuiteExportFormat ParseFormat(string? value)
        => string.Equals(value?.Trim(), "webm", StringComparison.OrdinalIgnoreCase) ? StockSuiteExportFormat.WebMVp9 : StockSuiteExportFormat.Mp4;

    public static string FormatLabel(StockSuiteExportFormat f) => f == StockSuiteExportFormat.WebMVp9 ? "WebM VP9" : "MP4";

    /// <summary>환경설정 고선명 샤프닝 기본값(공급자 예외 → OFF).</summary>
    public bool DefaultSharpen
    {
        get
        {
            try { return _sharpen(); }
            catch (Exception ex) { Program.Log("StockSuite.Sharpen", ex); return false; }
        }
    }

    /// <summary>원본 → <c>{targetDir}\{assetName}</c> 원자적 덮어쓰기. 대상 폴더는 상위가 존재할 때만 생성. <paramref name="format"/>·<paramref name="sharpen"/> null = 환경설정 기본.</summary>
    public async Task<StockSuiteExportResult> ExportAsync(string? sourcePath, string? targetDir = null, string? assetName = null, CancellationToken ct = default, StockSuiteExportFormat? format = null, bool? sharpen = null)
    {
        var r = await ExportCoreAsync(sourcePath, targetDir, assetName, ct, format, sharpen).ConfigureAwait(false);
        // 🔁 라이브 핫리로드 — 실제 교체가 일어난 경우에만 신호(동일 위치 no-op 제외).
        if (r.Ok && r.TargetPath is { } t && !string.Equals(Path.GetFullPath(sourcePath!), t, StringComparison.OrdinalIgnoreCase))
            WriteHotReloadTrigger(t, UtcNow());
        return r;
    }

    // ══════════════════════ 🔁 원자적 세이프 스왑 & 핫리로드 브릿지 ══════════════════════

    /// <summary>StockSuite FileSystemWatcher 감시 대상 신호 파일 — <c>{ "updatedAt": ISO-8601 UTC, "file": "bg_ambient.mp4" }</c>.</summary>
    public const string HotReloadTriggerName = "hotreload.trigger";
    /// <summary>잠금 시 지연 재시도 횟수(최초 시도 제외).</summary>
    public const int SwapRetryCount = 3;
    /// <summary>재시도 기본 지연(선형 증가: 1×, 2×, 3×) — 테스트 단축 주입.</summary>
    public TimeSpan SwapRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>트리거 시각 공급자(테스트 주입).</summary>
    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>
    /// 임시 파일 → 대상 원자적 교체. StockSuite 가 대상을 잠그고 있으면(공유 위반) 즉시 실패하지 않고
    /// <see cref="SwapRetryCount"/>회 지연 재시도. 최종 실패 시 임시 파일은 호출부 catch 가 정리.
    /// </summary>
    private async Task SwapIntoPlaceAsync(string temp, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < SwapRetryCount && ex is IOException or UnauthorizedAccessException)
            {
                Program.Log("StockSuite.SwapRetry", ex);
                await Task.Delay(SwapRetryDelay * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 재시도 소진 — 잠금(공유 위반)을 경로 설정 오류로 오인하지 않도록 IOException 으로 정규화.
                throw new IOException("대상 파일이 다른 프로세스(StockSuite)에 의해 잠겨 있습니다: " + Path.GetFileName(target), ex);
            }
        }
    }

    /// <summary>핫리로드 트리거 JSON 페이로드(정규 · camelCase · UTC ISO-8601).</summary>
    public static string BuildHotReloadTriggerJson(string fileName, DateTimeOffset updatedAt)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new System.Text.Json.Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("updatedAt", updatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("file", fileName);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>대상 폴더에 <see cref="HotReloadTriggerName"/> 원자적 기록(임시 → 교체). 실패는 격리(내보내기 결과 불변).</summary>
    private static void WriteHotReloadTrigger(string target, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir)) return;
        var path = Path.Combine(dir, HotReloadTriggerName);
        var tmp = path + TempExt;
        try
        {
            File.WriteAllText(tmp, BuildHotReloadTriggerJson(Path.GetFileName(target), now));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.HotReload", ex);
            TryDelete(tmp);
        }
    }

    private async Task<StockSuiteExportResult> ExportCoreAsync(string? sourcePath, string? targetDir, string? assetName, CancellationToken ct, StockSuiteExportFormat? format, bool? sharpen)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new(false, null, false, "원본 영상을 찾을 수 없습니다.");
        var dir = string.IsNullOrWhiteSpace(targetDir) ? ConfiguredDirectory : targetDir.Trim().Trim('"');
        if (dir is null) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");

        var fmt = format ?? DefaultFormat;
        var webm = fmt == StockSuiteExportFormat.WebMVp9;
        var name = string.IsNullOrWhiteSpace(assetName) ? (webm ? WebMAssetName : DefaultAssetName) : Path.GetFileName(assetName.Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = webm ? WebMAssetName : DefaultAssetName;
        if (webm) name = Path.ChangeExtension(name, ".webm");

        string temp = "";
        try
        {
            var fullDir = Path.GetFullPath(dir);
            if (!Directory.Exists(fullDir))
            {
                var parent = Path.GetDirectoryName(fullDir);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    return new(false, null, true, "StockSuite 에셋 경로가 존재하지 않습니다: " + fullDir + " — 환경설정에서 경로를 재설정하세요.");
                Directory.CreateDirectory(fullDir);
            }
            var target = Path.Combine(fullDir, name);
            if (string.Equals(Path.GetFullPath(sourcePath), target, StringComparison.OrdinalIgnoreCase))
                return new(true, target, false, "이미 StockSuite 에셋 위치의 파일입니다.");

            var sharpenOn = sharpen ?? DefaultSharpen;
            if (webm && (sharpenOn || !Path.GetExtension(sourcePath).Equals(".webm", StringComparison.OrdinalIgnoreCase)))
            {
                temp = Path.Combine(fullDir, Path.GetFileNameWithoutExtension(name) + TempExt + ".webm");
                // 🔎 WebM 고선명: 1080p Lanczos + Unsharp(VP9 · 무음). 실패 → 일반 VP9 → MP4 복사 순 폴백.
                if (sharpenOn)
                {
                    var sharpenedWebm = await TryFfmpegAsync(BuildWebMArgs(sourcePath, temp, sharpen: true), temp, ct).ConfigureAwait(false);
                    if (sharpenedWebm is null)
                    {
                        await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                        WriteSourceMarker(target, sourcePath);
                        return new(true, target, false, "💼 StockSuite WebM(VP9) 내보내기 완료(🔎 1080p 고선명 · 🔇 무음) → " + target);
                    }
                    TryDelete(temp);
                }
                var transcoded = await TryTranscodeWebMAsync(sourcePath, temp, ct).ConfigureAwait(false);
                if (transcoded is null)
                {
                    await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                    WriteSourceMarker(target, sourcePath);
                    return new(true, target, false, "💼 StockSuite WebM(VP9) 내보내기 완료 → " + target);
                }
                // [폴백] ffmpeg 부재/실패 → MP4 원본 복사로 대체 보존(동일 기본 이름 · .mp4)
                TryDelete(temp);
                var fb = await CopyAsync(sourcePath, Path.Combine(fullDir, Path.ChangeExtension(name, ".mp4")), ct).ConfigureAwait(false);
                return fb.Ok ? fb with { Message = "⚠ WebM 변환 실패(" + transcoded + ") — MP4 로 대체: " + fb.TargetPath } : fb;
            }

            // 🔇 HTS 제로-오디오: 비디오 스트림 무손실 copy + -an(오디오 드롭). ffmpeg 부재/실패 → 원본 복사 폴백.
            temp = Path.Combine(fullDir, Path.GetFileNameWithoutExtension(name) + TempExt + Path.GetExtension(name));
            // 🔎 HTS 대화면 고선명: 1080p Lanczos + Unsharp 재인코딩(무음). 실패 → 제로-오디오 스트림 복사 → 원본 복사 순 폴백.
            if (!webm && sharpenOn)
            {
                var sharpened = await TryFfmpegAsync(BuildSharpenArgs(sourcePath, temp), temp, ct).ConfigureAwait(false);
                if (sharpened is null)
                {
                    await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                    WriteSourceMarker(target, sourcePath);
                    return new(true, target, false, "💼 StockSuite 내보내기 완료(🔎 1080p 고선명 · 🔇 무음) → " + target);
                }
                TryDelete(temp);
            }
            var stripped =await TryFfmpegAsync(BuildZeroAudioArgs(sourcePath, temp), temp, ct).ConfigureAwait(false);
            if (stripped is null)
            {
                await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
                WriteSourceMarker(target, sourcePath);
                return new(true, target, false, "💼 StockSuite 내보내기 완료(🔇 무음 HTS 최적화) → " + target);
            }
            TryDelete(temp);
            temp = "";
            return await CopyAsync(sourcePath, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return new(false, null, false, "취소됨");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Export", ex);
            TryDelete(temp);
            return new(false, null, ex is UnauthorizedAccessException or ArgumentException or NotSupportedException, "StockSuite 내보내기 실패: " + ex.Message + " — 경로를 확인하세요.");
        }
    }

    private async Task<StockSuiteExportResult> CopyAsync(string sourcePath, string target, CancellationToken ct)
    {
        var temp = target + TempExt;
        try
        {
            await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            await SwapIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        var name = Path.GetFileName(target);
        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(sourcePath).Length)
            return new(false, null, false, "복사 검증 실패: " + name);
        WriteSourceMarker(target, sourcePath);
        return new(true, target, false, "💼 StockSuite 내보내기 완료 → " + target);
    }

    /// <summary>WebM 변환 — 성공 시 null, 실패 시 사유 문자열.</summary>
    private Task<string?> TryTranscodeWebMAsync(string source, string output, CancellationToken ct)
        => TryFfmpegAsync(BuildWebMArgs(source, output), output, ct);

    /// <summary>ffmpeg 실행 + 출력 무결성 검증 — 성공 시 null, 실패 시 사유 문자열.</summary>
    private async Task<string?> TryFfmpegAsync(string[] args, string output, CancellationToken ct)
    {
        string? ffmpeg;
        try { ffmpeg = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("StockSuite.Ffmpeg", ex); ffmpeg = null; }
        if (string.IsNullOrWhiteSpace(ffmpeg)) return "ffmpeg 미탐지";
        var ok = await _run(ffmpeg, args, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ok) return "ffmpeg 비정상 종료";
        return AmbientLoopService.IsValidOutput(output) ? null : "출력 파일 없음/손상";
    }

    /// <summary>HTS 배경용 VP9 인자 — <see cref="AmbientLoopService.BuildWebMArgs"/> 위임.</summary>
    public static string[] BuildWebMArgs(string inputPath, string outputPath) => AmbientLoopService.BuildWebMArgs(inputPath, outputPath);

    /// <summary>VP9 인자 — <paramref name="sharpen"/>=true 면 <see cref="SharpenFilter"/>(1080p Lanczos + Unsharp) 주입 · <c>-an</c> 무음 유지.</summary>
    public static string[] BuildWebMArgs(string inputPath, string outputPath, bool sharpen)
        => sharpen
            ? ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", SharpenFilter, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-an", "-sn", "-dn", outputPath]
            : AmbientLoopService.BuildWebMArgs(inputPath, outputPath);

    /// <summary>팩 넘버링 이름 — <c>bg_ambient_01.mp4</c> / <c>.webm</c>(1-기반, 최소 2자리).</summary>
    public static string BatchPackAssetName(int index, StockSuiteExportFormat format)
        => "bg_ambient_" + index.ToString("00", CultureInfo.InvariantCulture) + (format == StockSuiteExportFormat.WebMVp9 ? ".webm" : ".mp4");

    /// <summary>
    /// 💼 대량생산 StockSuite 팩 일괄 내보내기 — 유효 영상을 입력 순서대로 <c>bg_ambient_01</c>, <c>02</c>… 로 정규화 변환/복사.
    /// 개별 실패는 격리([부분 구제] — 성공분 보존), 중복 경로는 1회만. <paramref name="targetDirectory"/> 빈 값 = 환경설정 경로.
    /// </summary>
    public async Task<StockSuiteBatchPackResult> ExportBatchPackAsync(IEnumerable<string> videoPaths, string targetDirectory, bool sharpen, CancellationToken ct = default, StockSuiteExportFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(videoPaths);
        var fmt = format ?? DefaultFormat;
        var exported = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = 0;
        var index = 0;
        string? lastError = null;
        foreach (var p in videoPaths)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { failed++; continue; }
            string full;
            try { full = Path.GetFullPath(p); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { failed++; continue; }
            if (!seen.Add(full)) continue;   // alreadyRegistered 중복 차단
            index++;
            var r = await ExportAsync(full, targetDirectory, BatchPackAssetName(index, fmt), ct, fmt, sharpen).ConfigureAwait(false);
            if (r.Ok && r.TargetPath is not null) exported.Add(r.TargetPath);
            else
            {
                failed++;
                lastError = r.Message;
                if (r.NeedsPathSetup) return new(exported, failed, true, r.Message);
            }
        }
        if (ct.IsCancellationRequested) return new(exported, failed, false, "취소됨 — " + exported.Count.ToString(CultureInfo.InvariantCulture) + "개 보존");
        var msg = exported.Count == 0
            ? "⚠ 내보낸 영상이 없습니다" + (lastError is null ? "." : " — " + lastError)
            : "💼 StockSuite 팩 " + exported.Count.ToString(CultureInfo.InvariantCulture) + "개 내보내기 완료" + (failed > 0 ? " (실패 " + failed.ToString(CultureInfo.InvariantCulture) + ")" : "") + " → " + Path.GetDirectoryName(exported[0]);
        return new(exported, failed, false, msg);
    }

    /// <summary>
    /// HTS 제로-오디오 인자 — 첫 비디오 스트림만 무손실 copy, 오디오/자막/데이터 스트림 드롭(<c>-an -sn -dn</c>).
    /// HTS 백그라운드 상시 재생 시 오디오 디코더 구동·사운드 장치 점유를 원천 차단. MP4 는 <c>+faststart</c>.
    /// </summary>
    public static string[] BuildZeroAudioArgs(string inputPath, string outputPath)
        => outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-c:v", "copy", "-an", "-sn", "-dn", "-movflags", "+faststart", outputPath]
            : ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-c:v", "copy", "-an", "-sn", "-dn", outputPath];

    /// <summary>
    /// HTS 고선명 인자 — <see cref="SharpenFilter"/>(1080p Lanczos + Unsharp) 적용 후 H.264 재인코딩, 오디오/자막/데이터 드롭(<c>-an -sn -dn</c>).
    /// MP4 는 <c>+faststart</c>.
    /// </summary>
    public static string[] BuildSharpenArgs(string inputPath, string outputPath)
        => outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", SharpenFilter, "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-an", "-sn", "-dn", "-movflags", "+faststart", outputPath]
            : ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-vf", SharpenFilter, "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-an", "-sn", "-dn", outputPath];

    /// <summary>
    /// 🟢 StockSuite 적용 중 판정 — 설정 폴더의 <c>bg_ambient.mp4/.webm</c> 출처 마커가 원본(경로·크기·수정시각)과 일치하거나,
    /// MP4 대상이 동일 크기 + SHA-256 일치. 결과는 (경로·크기·시각) 키로 캐시.
    /// </summary>
    public bool IsApplied(string? sourcePath)
    {
        var dir = ConfiguredDirectory;
        if (dir is null || string.IsNullOrWhiteSpace(sourcePath)) return false;
        try
        {
            var src = new FileInfo(sourcePath);
            if (!src.Exists) return false;
            var fullDir = Path.GetFullPath(dir);
            if (!Directory.Exists(fullDir)) return false;
            var mp4 = Path.Combine(fullDir, DefaultAssetName);
            var webm = Path.Combine(fullDir, WebMAssetName);
            if (MarkerMatches(mp4, src) || MarkerMatches(webm, src)) return true;
            return HashMatches(src, new FileInfo(mp4));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string MarkerPayload(FileInfo src)
        => src.FullName + "|" + src.Length.ToString(CultureInfo.InvariantCulture) + "|" + src.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    private static void WriteSourceMarker(string target, string sourcePath)
    {
        try { File.WriteAllText(target + SourceMarkerSuffix, MarkerPayload(new FileInfo(Path.GetFullPath(sourcePath)))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("StockSuite.Marker", ex); }
    }

    private static bool MarkerMatches(string target, FileInfo src)
    {
        var marker = target + SourceMarkerSuffix;
        if (!File.Exists(target) || !File.Exists(marker)) return false;
        // 마커가 대상보다 오래되면(외부에서 대상 교체) 무효
        if (File.GetLastWriteTimeUtc(marker) < File.GetLastWriteTimeUtc(target).AddSeconds(-2)) return false;
        return string.Equals(File.ReadAllText(marker).Trim(), MarkerPayload(src), StringComparison.OrdinalIgnoreCase);
    }

    private bool HashMatches(FileInfo src, FileInfo target)
    {
        if (!target.Exists || target.Length != src.Length) return false;
        var key = MarkerPayload(src) + "#" + target.Length.ToString(CultureInfo.InvariantCulture) + "|" + target.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        lock (_hashLock) if (_hashCache.TryGetValue(key, out var cached)) return cached;
        bool eq;
        using (var a = new FileStream(src.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
        using (var b = new FileStream(target.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
            eq = CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));
        lock (_hashLock)
        {
            if (_hashCache.Count > 256) _hashCache.Clear();
            _hashCache[key] = eq;
        }
        return eq;
    }

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await Engines.ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ══════════════════════ 🚀 StockSuite 원클릭 런처 ══════════════════════

    /// <summary>실행 중 프로세스 수 조회(테스트 주입 · 기본 = 프로세스 이름 일치).</summary>
    public Func<string, int> RunningProbe { get; set; } = CountRunning;
    /// <summary>프로세스 시작기(테스트 주입 · 기본 = <see cref="Process.Start(ProcessStartInfo)"/>).</summary>
    public Func<ProcessStartInfo, bool> Starter { get; set; } = DefaultStart;
    /// <summary>기존 프로세스 종료기(재실행 · 테스트 주입). 반환 = 종료 성공 여부.</summary>
    public Func<string, bool> Terminator { get; set; } = TerminateAll;

    public string? ConfiguredExecutable
    {
        get
        {
            try { var p = _exePath(); return string.IsNullOrWhiteSpace(p) ? null : p.Trim().Trim('"'); }
            catch (Exception ex) { Program.Log("StockSuite.Exe", ex); return null; }
        }
    }

    public bool CanLaunch => ValidateExecutablePath(ConfiguredExecutable) == StockSuiteExeStatus.Ok;

    /// <summary>실행 파일 경로 검증 — 비어 있음 / 부재 / .exe 아님 / OK.</summary>
    public static StockSuiteExeStatus ValidateExecutablePath(string? path)
    {
        var p = (path ?? "").Trim().Trim('"');
        if (p.Length == 0) return StockSuiteExeStatus.NotConfigured;
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return StockSuiteExeStatus.NotFound;
        if (!Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return StockSuiteExeStatus.NotExecutable;
        return File.Exists(p) ? StockSuiteExeStatus.Ok : StockSuiteExeStatus.NotFound;
    }

    public static string DescribeExeStatus(StockSuiteExeStatus s) => s switch
    {
        StockSuiteExeStatus.Ok => "✅ 실행 파일을 확인했습니다.",
        StockSuiteExeStatus.NotExecutable => "❌ .exe 실행 파일이 아닙니다.",
        StockSuiteExeStatus.NotFound => "❌ 실행 파일이 존재하지 않습니다.",
        _ => "ℹ 비어 있음 — 환경설정에서 StockSuite.exe 경로를 지정하세요.",
    };

    /// <summary>
    /// 🚀 StockSuite 실행. 이미 실행 중이면 <paramref name="restartIfRunning"/>=false → AlreadyRunning(경고),
    /// true → 기존 프로세스 종료 후 재실행. 절대 throw 하지 않음.
    /// </summary>
    public StockSuiteLaunchResult Launch(bool restartIfRunning = false)
    {
        var exe = ConfiguredExecutable;
        var st = ValidateExecutablePath(exe);
        if (st != StockSuiteExeStatus.Ok) return new(false, false, DescribeExeStatus(st));
        var full = Path.GetFullPath(exe!);
        var name = Path.GetFileNameWithoutExtension(full);
        try
        {
            if (RunningProbe(name) > 0)
            {
                if (!restartIfRunning) return new(false, true, "⚠ StockSuite 가 이미 실행 중입니다 — 재실행하면 변경된 에셋이 즉시 반영됩니다.");
                if (!Terminator(name)) return new(false, true, "⚠ 실행 중인 StockSuite 를 종료하지 못했습니다 — 수동으로 닫은 뒤 다시 시도하세요.");
            }
            var psi = new ProcessStartInfo(full) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full) ?? "" };
            return Starter(psi)
                ? new(true, false, restartIfRunning ? "🔄 StockSuite 재실행 완료" : "🚀 StockSuite 실행 완료")
                : new(false, false, "⚠ StockSuite 실행에 실패했습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.Log("StockSuite.Launch", ex);
            return new(false, false, "⚠ StockSuite 실행 실패: " + ex.Message);
        }
    }

    private static int CountRunning(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        foreach (var p in ps) p.Dispose();
        return ps.Length;
    }

    private static bool DefaultStart(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi);
        return p is not null || psi.UseShellExecute;
    }

    private static bool TerminateAll(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        var ok = true;
        foreach (var p in ps)
        {
            try
            {
                if (!p.HasExited && (!p.CloseMainWindow() || !p.WaitForExit(3000))) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                ok &= p.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                Program.Log("StockSuite.Terminate", ex);
                ok = false;
            }
            finally { p.Dispose(); }
        }
        return ok;
    }
}
