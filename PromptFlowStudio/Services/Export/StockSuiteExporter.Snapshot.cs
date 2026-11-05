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
