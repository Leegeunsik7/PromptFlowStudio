using System;
using System.Globalization;
using System.IO;
#if WINDOWS
using System.Runtime.InteropServices;
#endif
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Export;

/// <summary>📸 단일 프레임 스냅샷 결과 — 성공 시 PNG 절대 경로 · 추출 시각(초).</summary>
public sealed record FrameSnapshotResult(bool Ok, string? Path, double Seconds, string Message);

/// <summary>
/// 👁️ 1프레임 번인 미리보기 옵션 — 단일 내보내기와 동일한 합성 체인(크롭/스케일 → LUT → 워터마크 → 티커 배지).
/// 60fps 보간은 단일 프레임에 무의미하므로 제외.
/// </summary>
public sealed record ExportOptions
{
    public bool Sharpen { get; init; }
    public StockSuiteResolution Resolution { get; init; } = StockSuiteResolution.Fhd1080;
    public bool Watermark { get; init; }
    public LutProfile Lut { get; init; } = LutProfile.None;
    /// <summary>📊 티커 배지 데이터(null/빈 티커 = 배지 생략).</summary>
    public TickerBadgeData? TickerBadge { get; init; }
    /// <summary>추출 시각(초) — 기본 1초 · 원본이 더 짧으면 0초 재시도.</summary>
    public double AtSeconds { get; init; } = 1.0;
    /// <summary>출력 폴더(null = <c>%TEMP%\pfs_preview</c>).</summary>
    public string? OutputDirectory { get; init; }
}

public sealed partial class StockSuiteExporter
{
    /// <summary>스냅샷 기본 FPS(프로브 실패 시).</summary>
    public const double DefaultScrubFps = 30.0;

    /// <summary>FPS 정규화 — 무효/비정상 값은 30fps, 1~240 범위 클램프.</summary>
    public static double NormalizeFps(double? fps)
        => fps is { } f && double.IsFinite(f) && f > 0 ? Math.Clamp(f, 1.0, 240.0) : DefaultScrubFps;

    /// <summary>
    /// 1프레임 스텝 — 현재 시각을 프레임 격자에 스냅한 뒤 ±1/fps 이동(0 ~ duration 클램프 · duration ≤ 0 이면 상한 없음).
    /// </summary>
    public static double StepFrame(double currentSeconds, int frames, double? fps, double durationSeconds = 0)
    {
        var f = NormalizeFps(fps);
        var cur = double.IsFinite(currentSeconds) && currentSeconds > 0 ? currentSeconds : 0;
        var idx = Math.Round(cur * f) + frames;   // 프레임 인덱스 격자
        var t = Math.Max(0, idx) / f;
        if (durationSeconds > 0 && double.IsFinite(durationSeconds)) t = Math.Min(t, Math.Max(0, durationSeconds - 1.0 / f));
        return t;
    }

    /// <summary>FFmpeg 시각 표기(InvariantCulture · ms 정밀도 · 음수/NaN = 0).</summary>
    public static string FormatSeekSeconds(double seconds)
        => (double.IsFinite(seconds) && seconds > 0 ? seconds : 0).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// 단일 프레임 추출 인자 — <c>ffmpeg -ss {t} -i {video} -vframes 1 -q:v 2 {out.png}</c> (입력 앞 -ss = 빠른 정밀 탐색 · PNG 무손실).
    /// </summary>
    public static string[] BuildSnapshotArgs(string videoPath, double seconds, string outputPng) => new[]
    {
        "-hide_banner", "-y",
        "-ss", FormatSeekSeconds(seconds),
        "-i", videoPath,
        "-vframes", "1",
        "-q:v", "2",
        outputPng,
    };

    /// <summary>스냅샷 파일명 — <c>snap_{원본명}_{mm-ss-fff}.png</c>(파일명 안전 문자만).</summary>
    public static string SnapshotFileName(string videoPath, double seconds)
    {
        var stem = Path.GetFileNameWithoutExtension(videoPath ?? "");
        var sb = new System.Text.StringBuilder(48);
        foreach (var c in stem)
        {
            if (sb.Length >= 40) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }
        if (sb.Length == 0) sb.Append("frame");
        var ts = TimeSpan.FromMilliseconds(Math.Round((double.IsFinite(seconds) && seconds > 0 ? seconds : 0) * 1000));
        return "snap_" + sb + "_" + ((int)ts.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + "-"
            + ts.Seconds.ToString("00", CultureInfo.InvariantCulture) + "-" + ts.Milliseconds.ToString("000", CultureInfo.InvariantCulture) + ".png";
    }

    /// <summary>📸 현재 시각 1프레임 PNG 추출 — <paramref name="outputDirectory"/> 에 저장. 예외 없이 결과 반환.</summary>
    public async Task<FrameSnapshotResult> ExtractSnapshotAsync(string? videoPath, double seconds, string? outputDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return new(false, null, seconds, "원본 영상을 찾을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return new(false, null, seconds, "출력 경로가 비어 있습니다.");
        string? ffmpeg = null;
        try { ffmpeg = _resolveFfmpeg(); } catch (Exception ex) { Program.Log("StockSuite.Snapshot.Ffmpeg", ex); }
        if (string.IsNullOrWhiteSpace(ffmpeg)) return new(false, null, seconds, "FFmpeg 를 찾을 수 없습니다(환경설정 → FFmpeg 경로).");
        var t = double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
        string? outPath = null;
        try
        {
            var dir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(dir);
            outPath = Path.Combine(dir, SnapshotFileName(videoPath, t));
            var ok = await _run(ffmpeg, BuildSnapshotArgs(Path.GetFullPath(videoPath), t, outPath), ct).ConfigureAwait(false);
            if (ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                return new(true, outPath, t, "📸 스냅샷 추출 완료 (" + FormatSeekSeconds(t) + "s)");
            TryDelete(outPath);
            return new(false, null, t, "스냅샷 추출 실패(FFmpeg 종료 코드 확인)");
        }
        catch (OperationCanceledException) { if (outPath is not null) TryDelete(outPath); return new(false, null, t, "취소됨"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Snapshot", ex);
            if (outPath is not null) TryDelete(outPath);
            return new(false, null, t, "스냅샷 추출 실패: " + ex.Message);
        }
    }

    /// <summary>👁️ 미리보기 합성 필터 — <see cref="BuildVideoFilter(bool,bool,StockSuiteResolution,bool,string?,bool,LutProfile)"/>(60fps 제외) + 티커 배지. 전부 OFF 면 null.</summary>
    public static string? BuildPreviewFilter(ExportOptions options, string? watermarkFont, string? badgeFont)
    {
        ArgumentNullException.ThrowIfNull(options);
        var f = BuildVideoFilter(options.Sharpen, false, options.Resolution, options.Watermark, watermarkFont, lut: options.Lut);
        return BuildTickerBadgeFilter(options.TickerBadge, null, options.Resolution) is null ? f : AppendTickerBadge(f, options.TickerBadge, badgeFont, options.Resolution);
    }

    /// <summary>
    /// 👁️ 1프레임 번인 미리보기 인자 — <c>-ss {t} -i {src} -vframes 1 [-vf chain] -q:v 3 {out.jpg}</c>.
    /// <c>-vframes 1</c> → <see cref="Engines.ProcessRunner.Classify"/> 가 경량 프로브 풀로 분류(장중 중량 게이트 우회).
    /// </summary>
    public static string[] BuildPreviewFrameArgs(string sourceVideoPath, string outputPath, string? videoFilter, double seconds)
        => videoFilter is null
            ? ["-hide_banner", "-y", "-loglevel", "error", "-ss", FormatSeekSeconds(seconds), "-i", sourceVideoPath, "-vframes", "1", "-q:v", "3", outputPath]
            : ["-hide_banner", "-y", "-loglevel", "error", "-ss", FormatSeekSeconds(seconds), "-i", sourceVideoPath, "-vframes", "1", "-vf", videoFilter, "-q:v", "3", outputPath];

    /// <summary>미리보기 파일명 — <c>preview_{원본명}.jpg</c>(원본별 1장 덮어쓰기 · 임시 폴더 누적 방지).</summary>
    public static string PreviewFrameFileName(string sourceVideoPath)
        => "preview_" + Path.GetFileNameWithoutExtension(SnapshotFileName(sourceVideoPath, 0)).Replace("snap_", "", StringComparison.Ordinal) + ".jpg";

    /// <summary>
    /// 👁️ 1프레임 번인 미리보기 — 전체 합성 체인을 1초 지점 1프레임에만 적용해 경량 JPEG 생성.
    /// 성공 시 절대 경로, 실패 시 빈 문자열(예외 없음 · 사유는 <see cref="LastPreviewError"/>).
    /// </summary>
    public async Task<string> GeneratePreviewFrameAsync(string sourceVideoPath, ExportOptions options, CancellationToken ct = default)
    {
        LastPreviewError = null;
        if (options is null) { LastPreviewError = "옵션이 없습니다."; return string.Empty; }
        if (string.IsNullOrWhiteSpace(sourceVideoPath) || !File.Exists(sourceVideoPath)) { LastPreviewError = "원본 영상을 찾을 수 없습니다."; return string.Empty; }
        string? ffmpeg = null;
        try { ffmpeg = _resolveFfmpeg(); } catch (Exception ex) { Program.Log("StockSuite.Preview.Ffmpeg", ex); }
        if (string.IsNullOrWhiteSpace(ffmpeg)) { LastPreviewError = "FFmpeg 를 찾을 수 없습니다(환경설정 → FFmpeg 경로)."; return string.Empty; }
        string? outPath = null;
        try
        {
            var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.Combine(Path.GetTempPath(), "pfs_preview") : options.OutputDirectory);
            Directory.CreateDirectory(dir);
            outPath = Path.Combine(dir, PreviewFrameFileName(sourceVideoPath));
            TryDelete(outPath);   // 직전 미리보기 잔존 시 성공 오판 방지
            var src = Path.GetFullPath(sourceVideoPath);
            var filter = BuildPreviewFilter(options, options.Watermark ? ResolveWatermarkFont() : null, ResolveTickerBadgeFont());
            var t = double.IsFinite(options.AtSeconds) && options.AtSeconds > 0 ? options.AtSeconds : 0;
            if (await TryPreviewAsync(ffmpeg, src, outPath, filter, t, ct).ConfigureAwait(false)) return outPath;
            // 원본이 1초보다 짧음 → 0초 1회 재시도.
            if (t > 0 && await TryPreviewAsync(ffmpeg, src, outPath, filter, 0, ct).ConfigureAwait(false)) return outPath;
            TryDelete(outPath);
            LastPreviewError = "미리보기 프레임 생성 실패(FFmpeg 종료 코드 확인)";
            return string.Empty;
        }
        catch (OperationCanceledException) { if (outPath is not null) TryDelete(outPath); LastPreviewError = "취소됨"; return string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Preview", ex);
            if (outPath is not null) TryDelete(outPath);
            LastPreviewError = "미리보기 프레임 생성 실패: " + ex.Message;
            return string.Empty;
        }
    }

    /// <summary>직전 <see cref="GeneratePreviewFrameAsync"/> 실패 사유(성공 = null).</summary>
    public string? LastPreviewError { get; private set; }

    private async Task<bool> TryPreviewAsync(string ffmpeg, string src, string outPath, string? filter, double t, CancellationToken ct)
    {
        var ok = await _run(ffmpeg, BuildPreviewFrameArgs(src, outPath, filter, t), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0;
    }

    /// <summary>🖼️ 윈도우 바탕화면 배경 설정(Windows 전용 · 그 외 false).</summary>
    public static bool TrySetDesktopWallpaper(string? imagePath)
    {
        if (!PromptFlowStudio.Services.Platform.NativePlatformGuard.IsWindows) return false;   // 🛡️ 런타임 OS 가드(비 Windows = no-op)
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return false;
#if WINDOWS
        try
        {
            return NativeWallpaper.SystemParametersInfo(NativeWallpaper.SpiSetDeskWallpaper, 0, Path.GetFullPath(imagePath),
                NativeWallpaper.SpifUpdateIniFile | NativeWallpaper.SpifSendChange);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Program.Log("StockSuite.Wallpaper", ex);
            return false;
        }
#else
        return false;
#endif
    }

#if WINDOWS
    private static class NativeWallpaper
    {
        public const uint SpiSetDeskWallpaper = 0x0014, SpifUpdateIniFile = 0x01, SpifSendChange = 0x02;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
    }
#endif
}
