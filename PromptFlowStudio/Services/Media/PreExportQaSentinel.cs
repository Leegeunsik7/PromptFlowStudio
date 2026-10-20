using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // CliPathResolver

namespace PromptFlowStudio.Services.Media;

/// <summary>배포 전 QA 리포트 — 통과 여부 + 오디오 피크/클리핑 + 블랙 프레임 + 이슈 목록.</summary>
public sealed record QaReport(
    bool Pass, double PeakDbfs, double ClipRatio, int BlackFrames, int SampleFrames,
    bool AudioClip, bool HasBlack, bool Silent, string Message, IReadOnlyList<string> Issues);

/// <summary>
/// [PromptFlowStudio] 전 플랫폼 배포 전 자동 QA 센티널(v3.3.0) — 순수 C# BCL 바이트 검사 + FFmpeg 디코드(외부 라이브러리 0).
///   ▸ 최종 영상의 <b>오디오 PCM(s16le)</b>을 스캔해 0dBFS 초과 <b>클리핑</b>·피크 dBFS·무음(RMS≈0)을 검출.
///   ▸ 비디오 키프레임을 <b>gray rawvideo</b>로 샘플링해 평균 휘도 기반 <b>블랙 프레임</b>을 전수 검사.
///   ▸ 감수 통과 시에만 멀티 플랫폼 내보내기 큐를 승인(<see cref="QaReport.Pass"/>). ffmpeg 부재/디코드 실패는 <b>통과(경고)</b> — 검수를 이유로 배포를 막지 않음(Graceful).
/// </summary>
public sealed class PreExportQaSentinel
{
    private const int Dim = 64;
    private const int SampleFps = 4;
    private const int MaxFrames = 60;

    // 임계값.
    private const double ClipRatioLimit = 0.003;   // 0.3% 이상 풀스케일 샘플 → 클리핑
    private const double PeakDbfsLimit = -0.1;      // 피크 -0.1dBFS 이상 → 클리핑 위험
    private const int BlackMeanLimit = 16;          // 평균 휘도 < 16 → 블랙 프레임
    private const double SilentRmsLimit = 30.0;     // RMS < 30(16bit) → 무음 의심

    /// <summary>최종 영상을 배포 전 감수한다. ffmpeg 부재/실패는 통과(경고).</summary>
    public async Task<QaReport> AnalyzeAsync(string videoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Pass("파일 없음(검수 생략)");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return Pass("ffmpeg 없음 — 검수 생략(통과)");

        try
        {
            var pcm = await DecodePcmAsync(ffmpeg, videoPath, ct).ConfigureAwait(false);
            var frames = await DecodeGrayFramesAsync(ffmpeg, videoPath, ct).ConfigureAwait(false);
            return Evaluate(pcm, frames, Dim, Dim);
        }
        catch (OperationCanceledException) { return Pass("취소(통과)"); }
        catch (Exception ex) { Program.Log("QaSentinel.Analyze", ex); return Pass("검수 오류(통과)"); }
    }

    /// <summary>디코드된 PCM + 프레임으로 QA 판정(순수 · 테스트 대상).</summary>
    public static QaReport Evaluate(short[] pcm, IReadOnlyList<byte[]> frames, int w, int h)
    {
        var issues = new List<string>();

        var (peakDbfs, clipRatio) = DetectClipping(pcm);
        var rms = Rms(pcm);
        var audioClip = pcm.Length > 0 && (clipRatio > ClipRatioLimit || peakDbfs >= PeakDbfsLimit);
        var silent = pcm.Length > 0 && rms < SilentRmsLimit;
        if (audioClip) issues.Add($"오디오 클리핑 감지(피크 {peakDbfs:0.0}dBFS · 클립 {clipRatio * 100:0.00}%)");
        if (silent) issues.Add($"무음/저음량 의심(RMS {rms:0})");

        var black = frames.Count(f => IsBlackFrame(f, BlackMeanLimit));
        var hasBlack = frames.Count > 0 && black > Math.Max(1, frames.Count * 0.1);
        if (hasBlack) issues.Add($"블랙 프레임 {black}/{frames.Count} 감지");

        var pass = !audioClip && !hasBlack;
        var msg = pass
            ? $"✅ QA 통과 · 피크 {peakDbfs:0.0}dBFS · 블랙 {black}/{frames.Count}"
            : "⛔ QA 차단 · " + string.Join(" / ", issues);
        return new QaReport(pass, Math.Round(peakDbfs, 1), Math.Round(clipRatio, 5), black, frames.Count, audioClip, hasBlack, silent, msg, issues);
    }

    /// <summary>PCM(s16le) 클리핑 검출 → (피크 dBFS, 풀스케일 샘플 비율).</summary>
    public static (double PeakDbfs, double ClipRatio) DetectClipping(short[] pcm)
    {
        if (pcm is null || pcm.Length == 0) return (-120, 0);
        long clipped = 0; int peak = 0;
        foreach (var s in pcm)
        {
            var a = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (a > peak) peak = a;
            if (a >= 32760) clipped++;
        }
        var peakDbfs = peak <= 0 ? -120 : 20.0 * Math.Log10(peak / 32768.0);
        return (peakDbfs, clipped / (double)pcm.Length);
    }

    /// <summary>PCM RMS(16bit 스케일).</summary>
    public static double Rms(short[] pcm)
    {
        if (pcm is null || pcm.Length == 0) return 0;
        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        return Math.Sqrt(sum / pcm.Length);
    }

    /// <summary>그레이 프레임 블랙 판정(평균 휘도 &lt; 임계).</summary>
    public static bool IsBlackFrame(byte[] gray, int meanLimit)
    {
        if (gray is null || gray.Length == 0) return false;
        long sum = 0;
        foreach (var b in gray) sum += b;
        return sum / (double)gray.Length < meanLimit;
    }

    private static QaReport Pass(string reason) => new(true, -120, 0, 0, 0, false, false, false, "✅ " + reason, Array.Empty<string>());

    // ── FFmpeg 디코드 ──
    private static async Task<short[]> DecodePcmAsync(string ffmpeg, string video, CancellationToken ct)
    {
        var bytes = await RunBinaryAsync(ffmpeg, new[] { "-i", video, "-vn", "-ac", "1", "-ar", "44100", "-f", "s16le", "pipe:1" }, ct).ConfigureAwait(false);
        if (bytes.Length < 2) return Array.Empty<short>();
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
        return pcm;
    }

    private static async Task<List<byte[]>> DecodeGrayFramesAsync(string ffmpeg, string video, CancellationToken ct)
    {
        var bytes = await RunBinaryAsync(ffmpeg, new[] { "-i", video, "-vf", $"fps={SampleFps},scale={Dim}:{Dim}", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1" }, ct).ConfigureAwait(false);
        var list = new List<byte[]>();
        var fs = Dim * Dim;
        var count = bytes.Length / fs;
        var step = Math.Max(1, count / MaxFrames);
        for (var f = 0; f < count && list.Count < MaxFrames; f += step)
        {
            var buf = new byte[fs];
            Buffer.BlockCopy(bytes, f * fs, buf, 0, fs);
            list.Add(buf);
        }
        return list;
    }

    private static async Task<byte[]> RunBinaryAsync(string ffmpeg, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, 81920, ct);
        var drain = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, drain).ConfigureAwait(false);
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(true); } catch { } throw; }
        return ms.ToArray();
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
