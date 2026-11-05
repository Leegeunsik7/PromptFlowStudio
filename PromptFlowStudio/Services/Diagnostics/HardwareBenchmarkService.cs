using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Diagnostics;

/// <summary>⚡ 벤치마크 권장 렌더 프로필.</summary>
public enum RenderProfileTier : byte { Eco, Balanced, Ultra }

/// <summary>벤치마크 결과 — <see cref="SpeedRatio"/> = FPS_encode / 30.</summary>
public sealed record HardwareBenchmarkResult(bool Ok, GpuEncoderVendor Encoder, double EncodeFps, double SpeedRatio, RenderProfileTier Profile, string Message)
{
    public string ProfileLabel => HardwareBenchmarkService.ProfileLabel(Profile);
    public StockSuiteResolution Resolution => Profile == RenderProfileTier.Ultra ? StockSuiteResolution.Uhd4K : StockSuiteResolution.Fhd1080;
    public bool Smooth60 => Profile != RenderProfileTier.Eco;
}

/// <summary>
/// ⚡ 1초 합성 1080p 인코드 벤치마크(<see cref="IProcessRunner"/> · NVENC/QSV/AMF → CPU 폴백) → 처리량 비율로 렌더 프로필 자동 추천.
/// ≥3.0× Ultra · 1.5~3.0× Balanced · &lt;1.5× Eco. ffmpeg 부재/실패 → Eco 안전 폴백(throw 없음).
/// </summary>
public sealed class HardwareBenchmarkService
{
    public const int BaselineFps = 30;
    public const int TestFrames = 30;   // 1초 @ 30fps
    public const double UltraThreshold = 3.0;
    public const double BalancedThreshold = 1.5;

    private readonly IProcessRunner _runner;
    private readonly Func<string?> _resolveFfmpeg;

    public HardwareAccelDetector? Accel { get; set; }

    public HardwareBenchmarkService() : this(null, null) { }

    public HardwareBenchmarkService(IProcessRunner? runner, Func<string?>? resolveFfmpeg = null, HardwareAccelDetector? accel = null)
    {
        _runner = runner ?? SystemProcessRunner.Shared;
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        Accel = accel;
    }

    public static string ProfileLabel(RenderProfileTier p) => p switch
    {
        RenderProfileTier.Ultra => "Ultra (4K UHD @ 60fps)",
        RenderProfileTier.Balanced => "Balanced (1080p @ 60fps)",
        _ => "Eco (1080p @ 30fps)",
    };

    /// <summary>임계 분류 — NaN/음수 → Eco.</summary>
    public static RenderProfileTier Classify(double speedRatio)
        => speedRatio >= UltraThreshold ? RenderProfileTier.Ultra
         : speedRatio >= BalancedThreshold ? RenderProfileTier.Balanced
         : RenderProfileTier.Eco;

    /// <summary>FPS_encode = frames / elapsed — 0 이하 입력 → 0.</summary>
    public static double ComputeEncodeFps(int frames, double elapsedSec)
        => frames <= 0 || !(elapsedSec > 0) ? 0 : frames / elapsedSec;

    public static double ComputeSpeedRatio(double encodeFps) => encodeFps > 0 ? encodeFps / BaselineFps : 0;

    /// <summary>ffmpeg 진행 라인의 마지막 <c>speed=3.25x</c> 추출(없음 → null).</summary>
    public static double? ParseSpeed(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
        var i = stderr.LastIndexOf("speed=", StringComparison.Ordinal);
        if (i < 0) return null;
        var s = i + 6;
        while (s < stderr.Length && stderr[s] == ' ') s++;
        var e = s;
        while (e < stderr.Length && (char.IsDigit(stderr[e]) || stderr[e] == '.')) e++;
        return e > s && double.TryParse(stderr.AsSpan(s, e - s), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    public static string[] BuildBenchmarkArgs(GpuEncoderVendor vendor)
    {
        var a = new List<string>(24)
        {
            "-hide_banner", "-nostdin", "-stats", "-f", "lavfi",
            "-i", "testsrc2=size=1920x1080:rate=" + BaselineFps.ToString(CultureInfo.InvariantCulture),
            "-frames:v", TestFrames.ToString(CultureInfo.InvariantCulture),
        };
        if (vendor == GpuEncoderVendor.Cpu) { a.Add("-c:v"); a.Add("libx264"); a.Add("-preset"); a.Add("medium"); }
        else a.AddRange(HardwareAccelDetector.VideoCodecArgs(vendor));
        a.Add("-pix_fmt"); a.Add("yuv420p");
        a.Add("-f"); a.Add("null"); a.Add("-");
        return a.ToArray();
    }

    public async Task<HardwareBenchmarkResult> RunAsync(CancellationToken ct = default)
    {
        string? ffmpeg;
        try { ffmpeg = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("Benchmark.Ffmpeg", ex); ffmpeg = null; }
        if (string.IsNullOrWhiteSpace(ffmpeg))
            return new(false, GpuEncoderVendor.Cpu, 0, 0, RenderProfileTier.Eco, "ffmpeg 미탐지 — Eco 프로필로 안전 폴백");

        var vendor = GpuEncoderVendor.Cpu;
        if (Accel is { } d)
        {
            try { vendor = await d.DetectAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Benchmark.HwAccel", ex); }
        }

        var r = await MeasureAsync(ffmpeg, vendor, ct).ConfigureAwait(false);
        if (r is null && vendor != GpuEncoderVendor.Cpu)
        {
            vendor = GpuEncoderVendor.Cpu;   // GPU 실패 → CPU 무중단 폴백
            r = await MeasureAsync(ffmpeg, vendor, ct).ConfigureAwait(false);
        }
        if (r is not { } m)
            return new(false, vendor, 0, 0, RenderProfileTier.Eco, "벤치마크 인코드 실패 — Eco 프로필로 안전 폴백");

        var profile = Classify(m.Ratio);
        var msg = "⚡ " + HardwareAccelDetector.Label(vendor) + " · " + m.Fps.ToString("0.0", CultureInfo.InvariantCulture) + "fps · "
            + m.Ratio.ToString("0.00", CultureInfo.InvariantCulture) + "× → " + ProfileLabel(profile);
        return new(true, vendor, m.Fps, m.Ratio, profile, msg);
    }

    private async Task<(double Fps, double Ratio)?> MeasureAsync(string ffmpeg, GpuEncoderVendor vendor, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ProcessResult res;
        try { res = await _runner.RunAsync(ffmpeg, BuildBenchmarkArgs(vendor), ProcessCategory.HeavyEncoding, ct: ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Benchmark.Run", ex); return null; }
        sw.Stop();
        if (!res.Ok) return null;
        // ffmpeg 자체 speed= 우선(프로세스 기동 비용 제외) → 부재 시 벽시계 측정.
        var ratio = ParseSpeed(res.StdErr) ?? ComputeSpeedRatio(ComputeEncodeFps(TestFrames, sw.Elapsed.TotalSeconds));
        return ratio > 0 ? (ratio * BaselineFps, ratio) : null;
    }
}
