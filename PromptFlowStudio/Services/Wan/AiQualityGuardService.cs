using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // CliPathResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>퀄리티 감수 결과 — 통과 여부 + 최소 SSIM · 선명도 · 손상/뭉개짐 플래그 + 사유 + 샘플 수.</summary>
public sealed record QualityVerdict(
    bool Ok, double MinSsim, double MedianSharpness, double WorstSharpness,
    bool StructuralBreak, bool FaceMush, string Reason, int Samples);

/// <summary>
/// [PromptFlowStudio] AI 비주얼 퀄리티 감수 &amp; 오토 재시도 가드(v2.6.0) — 순수 C# BCL 비트맵 연산 + FFmpeg 디코드(외부 ML 라이브러리 0).
///   ▸ Wan 2.1 생성 직후 컷 비디오의 키프레임을 <c>gray rawvideo</c>(96×96)로 다중 샘플링해 <b>바이트 수준</b>으로 감수한다.
///   ▸ <b>구조적 유사도(SSIM)</b> 급락(프레임 손상·찢김) + <b>라플라시안 분산 선명도</b> 급감(안면/디테일 뭉개짐)을 추론.
///   ▸ 임계값 미달 시 <see cref="QualityVerdict.Ok"/>=false → 호출부가 <see cref="QuotaRateGuardService"/>와 연동해 쿨다운 후 해당 컷을 자동 재발주(Retry).
///   ▸ ffmpeg 부재/디코드 실패는 <b>Ok=true</b>로 통과(감수를 이유로 정상 생성을 막지 않음 · Graceful).
/// </summary>
public sealed class AiQualityGuardService
{
    private const int Dim = 96;               // 샘플 프레임 정사각 크기(연산량↓·구조 충분)
    private const int SampleFps = 5;          // 초당 샘플 수
    private const int MaxFrames = 48;          // 분석 상한(균등 다운샘플)
    private const double SharpnessFloor = 6.0; // 절대 선명도 바닥(이하는 전면 소프트)

    private readonly QuotaRateGuardService _quota;
    public AiQualityGuardService(QuotaRateGuardService quota) { _quota = quota; }

    /// <summary>컷 비디오를 감수한다. threshold=프레임 간 최소 허용 SSIM. ffmpeg/디코드 실패는 Ok=true(통과).</summary>
    public async Task<QualityVerdict> AnalyzeAsync(string videoPath, double threshold, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Pass("파일 없음(통과)");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return Pass("ffmpeg 없음(통과)");

        try
        {
            var frames = await DecodeGrayFramesAsync(ffmpeg, videoPath, ct).ConfigureAwait(false);
            if (frames.Count < 2) return Pass("샘플 부족(통과)");
            return Evaluate(frames, Dim, Dim, Math.Clamp(threshold <= 0 ? 0.62 : threshold, 0.1, 0.99));
        }
        catch (OperationCanceledException) { return Pass("취소(통과)"); }
        catch (Exception ex) { Program.Log("AiQualityGuard.Analyze", ex); return Pass("감수 오류(통과)"); }
    }

    /// <summary>재발주 여부 — 감수 실패 + 잔여 시도. 임계 미달 컷을 재생성해야 하는지.</summary>
    public static bool ShouldRetry(QualityVerdict v, int attemptsUsed, int retryLimit)
        => v is not null && !v.Ok && attemptsUsed < Math.Max(0, retryLimit);

    /// <summary>재발주 직전 API 쿨다운을 존중해 대기한다(Rate Limit 중이면 함께 물러섬).</summary>
    public Task PrepareRetryAsync(CancellationToken ct = default) => _quota.WaitIfCoolingAsync(ct);

    // ══════════════════════ 순수 BCL 감수 연산 ══════════════════════

    /// <summary>샘플 프레임(그레이스케일)들로 SSIM 급락/선명도 급감을 판정한다.</summary>
    public static QualityVerdict Evaluate(IReadOnlyList<byte[]> frames, int w, int h, double threshold)
    {
        if (frames.Count < 2) return new(true, 1, 0, 0, false, false, "샘플 부족(통과)", frames.Count);

        // 프레임별 선명도(라플라시안 분산).
        var sharp = frames.Select(f => LaplacianVariance(f, w, h)).ToArray();
        var median = Median(sharp);
        var worst = sharp.Min();

        // 연속 프레임 SSIM 최소값(구조 급락 = 손상/찢김).
        double minSsim = 1.0;
        for (var i = 1; i < frames.Count; i++)
        {
            var s = Ssim(frames[i - 1], frames[i], w, h);
            if (s < minSsim) minSsim = s;
        }

        var structuralBreak = minSsim < threshold;
        // 뭉개짐: 특정 프레임 선명도가 중앙값 대비 급감(<35%) 하며 절대 바닥 미만, 또는 전체가 바닥.
        var faceMush = (median > 0 && worst < median * 0.35 && worst < SharpnessFloor * 2)
                       || median < SharpnessFloor;

        var ok = !structuralBreak && !faceMush;
        var reason = ok
            ? $"정상(minSSIM={minSsim:0.00} · 선명도중앙={median:0.0})"
            : structuralBreak
                ? $"프레임 손상 감지(minSSIM={minSsim:0.00} < {threshold:0.00})"
                : $"안면/디테일 뭉개짐(최저선명도={worst:0.0} · 중앙={median:0.0})";
        return new(ok, minSsim, median, worst, structuralBreak, faceMush, reason, frames.Count);
    }

    /// <summary>두 그레이 프레임의 전역 SSIM(단일 스케일 근사 · 0~1).</summary>
    public static double Ssim(byte[] a, byte[] b, int w, int h)
    {
        long n = (long)w * h;
        if (a.Length < n || b.Length < n || n == 0) return 1;
        double ma = 0, mb = 0;
        for (var i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double va = 0, vb = 0, cov = 0;
        for (var i = 0; i < n; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            va += da * da; vb += db * db; cov += da * db;
        }
        va /= n - 1; vb /= n - 1; cov /= n - 1;
        const double c1 = 6.5025, c2 = 58.5225;   // (0.01*255)^2, (0.03*255)^2
        return ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
    }

    /// <summary>라플라시안 분산(엣지 에너지) — 높을수록 선명, 낮을수록 흐림/뭉개짐.</summary>
    public static double LaplacianVariance(byte[] g, int w, int h)
    {
        if (g.Length < (long)w * h || w < 3 || h < 3) return 0;
        double sum = 0, sumSq = 0; long cnt = 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                double lap = 4 * g[i] - g[i - 1] - g[i + 1] - g[i - w] - g[i + w];
                sum += lap; sumSq += lap * lap; cnt++;
            }
        if (cnt == 0) return 0;
        var mean = sum / cnt;
        return sumSq / cnt - mean * mean;
    }

    private static double Median(double[] xs)
    {
        if (xs.Length == 0) return 0;
        var s = (double[])xs.Clone();
        Array.Sort(s);
        var m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }

    private static QualityVerdict Pass(string reason) => new(true, 1, 0, 0, false, false, reason, 0);

    // ── FFmpeg: gray rawvideo 다중 프레임 디코드 ──
    private static async Task<List<byte[]>> DecodeGrayFramesAsync(string ffmpeg, string video, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-y", "-i", video, "-vf", $"fps={SampleFps},scale={Dim}:{Dim}", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var list = new List<byte[]>();
        if (!proc.Start()) return list;

        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, 81920, ct);
        var drain = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, drain).ConfigureAwait(false);
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(true); } catch { } throw; }

        var bytes = ms.ToArray();
        var frameSize = Dim * Dim;
        var count = bytes.Length / frameSize;
        if (count == 0) return list;

        // 균등 다운샘플(최대 MaxFrames).
        var step = Math.Max(1, count / MaxFrames);
        for (var f = 0; f < count && list.Count < MaxFrames; f += step)
        {
            var buf = new byte[frameSize];
            Buffer.BlockCopy(bytes, f * frameSize, buf, 0, frameSize);
            list.Add(buf);
        }
        return list;
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
