using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService
using PromptFlowStudio.Services.Sound;         // RunAsync
using PromptFlowStudio.Services.Spatial;       // SpatialImageCodec

namespace PromptFlowStudio.Services.Vfx;

public enum InterpEngine : byte { Auto, Rife, Flavr, FfmpegMci }

/// <summary>슬로모션 구간(초) · 배율(2~8).</summary>
public readonly record struct SlowMoSegment(double Start, double End, int Factor);

/// <summary>블록 광학 흐름 필드(블록 크기 · 블록 격자 · 블록당 (dx,dy) 픽셀 · SAD 신뢰도).</summary>
public sealed class FlowField
{
    public FlowField(int w, int h, int block)
    {
        Block = block; Bw = (w + block - 1) / block; Bh = (h + block - 1) / block;
        Dx = new float[Bw * Bh]; Dy = new float[Bw * Bh]; Cost = new float[Bw * Bh];
    }
    public int Block { get; }
    public int Bw { get; }
    public int Bh { get; }
    public float[] Dx { get; }
    public float[] Dy { get; }
    public float[] Cost { get; }
}

public sealed record InterpPreview(int Width, int Height, string FrameADataUrl, string MidDataUrl, string FrameBDataUrl, string FlowDataUrl, float MeanMotionPx);

/// <summary>
/// [PromptFlow Studio v16.34.0] 120FPS AI 프레임 보간 &amp; 초슬로모션 엔진.
///   ▸ AI 경로: RIFE ncnn(PFS_RIFE · rife-ncnn-vulkan · rife-v4.6) / FLAVR(PFS_FLAVR 파이썬 래퍼 · 8x) — 프레임 PNG 시퀀스 → 목표 프레임 수 보간 → 인코드(원본 오디오).
///   ▸ 폴백: ffmpeg minterpolate(mci · aobmc · 양방향 ME · vsbmc) — AI 부재/실패 즉시 [부분 구제].
///   ▸ 슬로모션: 극적 구간을 배율×fps 로 보간 후 setpts 배율 늘림 + atempo 체인(0.5 단위)으로 오디오 동기 · 전/후 구간 concat.
///   ▸ BCL 광학 흐름(프리뷰/테스트): 8px 블록 · ±R 전탐색 SAD(조기 중단) · 저신뢰 블록 3×3 메디안 정규화 → 양방향 워프 블렌드 중간 프레임(무할당 Span).
/// </summary>
public sealed class MotionFrameInterpolationEngine
{
    public const int FlowBlock = 8;
    public const int FlowRadius = 8;
    public const int MaxSlowFactor = 8;
    public const int PreviewWidth = 320;
    public const float OutlierCost = 6f;

    private readonly Func<string?> _ffmpeg;
    private readonly Func<string?> _rife;
    private readonly Func<string?> _flavr;
    private int _busy;
    private long _jobs;

    public MotionFrameInterpolationEngine(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath, () => s_rife.Value, () => s_flavr.Value) { }

    public MotionFrameInterpolationEngine(Func<string?> ffmpeg, Func<string?>? rife, Func<string?>? flavr)
    {
        _ffmpeg = ffmpeg;
        _rife = rife ?? (() => null);
        _flavr = flavr ?? (() => null);
    }

    private static readonly Lazy<string?> s_rife = new(() => VfxProcess.ResolveCli("PFS_RIFE", "rife-ncnn-vulkan.exe", "rife-ncnn-vulkan"));
    private static readonly Lazy<string?> s_flavr = new(() => VfxProcess.ResolveCli("PFS_FLAVR", "flavr.exe", "flavr.cmd", "flavr"));

    public event Action? OnChange;

    public int TargetFps { get; set; } = 120;
    public InterpEngine Engine { get; set; } = InterpEngine.Auto;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public bool RifeAvailable => _rife() is not null;
    public bool FlavrAvailable => _flavr() is not null;
    public long Jobs => Interlocked.Read(ref _jobs);
    public double Progress { get; private set; }
    public InterpPreview? LastPreview { get; private set; }
    public VfxVideoResult? LastResult { get; private set; }
    public string? LastAction { get; private set; }

    // ══════════════════════ BCL 광학 흐름 ══════════════════════

    /// <summary>블록 매칭 흐름(a→b: b(x+d) ≈ a(x)) · 저신뢰 블록 메디안 정규화. 반환 = 평균 이동량(px).</summary>
    public static float EstimateFlow(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int w, int h, int radius, FlowField field)
    {
        if (a.Length < w * h || b.Length < w * h) throw new ArgumentException("버퍼 크기 불일치");
        var B = field.Block;
        double sumMag = 0;
        for (var by = 0; by < field.Bh; by++)
            for (var bx = 0; bx < field.Bw; bx++)
            {
                int x0 = bx * B, y0 = by * B, x1 = Math.Min(w, x0 + B), y1 = Math.Min(h, y0 + B);
                long best = long.MaxValue; int bdx = 0, bdy = 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    if (y0 + dy < 0 || y1 + dy > h) continue;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        if (x0 + dx < 0 || x1 + dx > w) continue;
                        long sad = 0;
                        for (var y = y0; y < y1 && sad <= best; y++)   // 초과 시 조기 중단(동점은 완주한 경우만)
                        {
                            var ra = y * w; var rb = (y + dy) * w + dx;
                            for (var x = x0; x < x1; x++) sad += Math.Abs(a[ra + x] - b[rb + x]);
                        }
                        if (sad < best || (sad == best && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bdx) + Math.Abs(bdy))) { best = sad; bdx = dx; bdy = dy; }
                    }
                }
                var i = by * field.Bw + bx;
                field.Dx[i] = bdx; field.Dy[i] = bdy; field.Cost[i] = best / (float)Math.Max(1, (x1 - x0) * (y1 - y0));
            }
        MedianFilter(field.Dx, field.Cost, field.Bw, field.Bh);
        MedianFilter(field.Dy, field.Cost, field.Bw, field.Bh);
        for (var i = 0; i < field.Dx.Length; i++) sumMag += MathF.Sqrt(field.Dx[i] * field.Dx[i] + field.Dy[i] * field.Dy[i]);
        return (float)(sumMag / Math.Max(1, field.Dx.Length));
    }

    /// <summary>신뢰도 낮은 블록(평균 SAD &gt; 6)만 3×3 메디안으로 교체 — 작은 이동 물체 보존 · 오정합 제거.</summary>
    private static void MedianFilter(float[] v, float[] cost, int bw, int bh)
    {
        if (bw < 3 || bh < 3) return;
        Span<float> win = stackalloc float[9];
        var copy = System.Buffers.ArrayPool<float>.Shared.Rent(v.Length);
        try
        {
            Array.Copy(v, copy, v.Length);
            for (var y = 1; y < bh - 1; y++)
                for (var x = 1; x < bw - 1; x++)
                {
                    if (cost[y * bw + x] <= OutlierCost) continue;
                    var k = 0;
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) win[k++] = copy[(y + dy) * bw + x + dx];
                    win.Sort();
                    v[y * bw + x] = win[4];
                }
        }
        finally { System.Buffers.ArrayPool<float>.Shared.Return(copy); }
    }

    /// <summary>흐름 이중선형 샘플(픽셀 위치 → 블록 중심 보간).</summary>
    private static void FlowAt(FlowField f, float x, float y, out float dx, out float dy)
    {
        var B = f.Block;
        var gx = Math.Clamp(x / B - 0.5f, 0, f.Bw - 1); var gy = Math.Clamp(y / B - 0.5f, 0, f.Bh - 1);
        int x0 = (int)gx, y0 = (int)gy, x1 = Math.Min(x0 + 1, f.Bw - 1), y1 = Math.Min(y0 + 1, f.Bh - 1);
        float tx = gx - x0, ty = gy - y0;
        var bw = f.Bw;
        float[] X = f.Dx, Y = f.Dy;
        dx = (X[y0 * bw + x0] * (1 - tx) + X[y0 * bw + x1] * tx) * (1 - ty) + (X[y1 * bw + x0] * (1 - tx) + X[y1 * bw + x1] * tx) * ty;
        dy = (Y[y0 * bw + x0] * (1 - tx) + Y[y0 * bw + x1] * tx) * (1 - ty) + (Y[y1 * bw + x0] * (1 - tx) + Y[y1 * bw + x1] * tx) * ty;
    }

    /// <summary>
    /// 양방향 워프 블렌드 — 중간 시각 t(0~1) 픽셀 p: A(p − t·d) 와 B(p + (1−t)·d) 를 (1−t, t) 가중(역방향 근사 · rgb24 · 무할당).
    /// </summary>
    public static void Interpolate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int w, int h, FlowField flow, float t, Span<byte> dst)
    {
        var n = w * h * 3;
        if (a.Length < n || b.Length < n || dst.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        t = Math.Clamp(t, 0f, 1f);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                FlowAt(flow, x, y, out var dx, out var dy);
                float ax = x - t * dx, ay = y - t * dy, bx = x + (1 - t) * dx, by = y + (1 - t) * dy;
                var o = (y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                    dst[o + c] = (byte)Math.Clamp(Bilinear(a, w, h, ax, ay, c) * (1 - t) + Bilinear(b, w, h, bx, by, c) * t + 0.5f, 0f, 255f);
            }
    }

    private static float Bilinear(ReadOnlySpan<byte> s, int w, int h, float x, float y, int c)
    {
        x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = x - x0, ty = y - y0;
        float p00 = s[(y0 * w + x0) * 3 + c], p10 = s[(y0 * w + x1) * 3 + c], p01 = s[(y1 * w + x0) * 3 + c], p11 = s[(y1 * w + x1) * 3 + c];
        return (p00 * (1 - tx) + p10 * tx) * (1 - ty) + (p01 * (1 - tx) + p11 * tx) * ty;
    }

    public static void ToLuma(ReadOnlySpan<byte> rgb, Span<byte> luma)
    {
        for (int i = 0, j = 0; i < luma.Length; i++, j += 3) luma[i] = (byte)((77 * rgb[j] + 150 * rgb[j + 1] + 29 * rgb[j + 2]) >> 8);
    }

    /// <summary>프리뷰 — 두 프레임 → 흐름 + 중간 프레임 + 흐름 시각화(HSV 근사).</summary>
    public InterpPreview SetPreview(byte[] a, byte[] b, int w, int h)
    {
        var la = new byte[w * h]; var lb = new byte[w * h];
        ToLuma(a, la); ToLuma(b, lb);
        var flow = new FlowField(w, h, FlowBlock);
        var mean = EstimateFlow(la, lb, w, h, FlowRadius, flow);
        var mid = new byte[w * h * 3];
        Interpolate(a, b, w, h, flow, 0.5f, mid);
        var vis = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                FlowAt(flow, x, y, out var dx, out var dy);
                var mag = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / FlowRadius, 0f, 1f);
                var ang = MathF.Atan2(dy, dx);
                var o = (y * w + x) * 3;
                vis[o] = (byte)(255 * mag * (0.5f + 0.5f * MathF.Cos(ang)));
                vis[o + 1] = (byte)(255 * mag * (0.5f + 0.5f * MathF.Cos(ang - 2.094f)));
                vis[o + 2] = (byte)(255 * mag * (0.5f + 0.5f * MathF.Cos(ang + 2.094f)));
            }
        var p = new InterpPreview(w, h, SpatialImageCodec.BmpDataUrl(a, w, h), SpatialImageCodec.BmpDataUrl(mid, w, h), SpatialImageCodec.BmpDataUrl(b, w, h), SpatialImageCodec.BmpDataUrl(vis, w, h), mean);
        LastPreview = p;
        LastAction = "🌊 광학 흐름 · 평균 " + mean.ToString("0.0", CultureInfo.InvariantCulture) + "px";
        VfxProcess.Raise(OnChange, "Interp.OnChange");
        return p;
    }

    public async Task<InterpPreview?> PreviewAsync(string path, double seconds, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(path)) return null;
        var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, path, ct).ConfigureAwait(false);
        if (probe is null) return null;
        var fps = await AiNeuralVfxRelightingEngine.ProbeFpsAsync(ffmpeg, path, ct).ConfigureAwait(false);
        int w = PreviewWidth, h = Math.Max(16, (int)Math.Round(PreviewWidth * (double)probe.Value.H / probe.Value.W) & ~1);
        var a = await AiNeuralVfxRelightingEngine.GrabFrameAsync(ffmpeg, path, Math.Max(0, seconds), w, h, ct).ConfigureAwait(false);
        var b = await AiNeuralVfxRelightingEngine.GrabFrameAsync(ffmpeg, path, Math.Max(0, seconds) + 1.0 / fps, w, h, ct).ConfigureAwait(false);
        return a is null || b is null ? null : SetPreview(a, b, w, h);
    }

    // ══════════════════════ 인자 빌더 ══════════════════════

    public static string MinterpolateFilter(double fps) => "minterpolate=fps=" + fps.ToString("0.###", CultureInfo.InvariantCulture) + ":mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1";

    /// <summary>오디오 속도 배율(0.5 미만은 0.5 체인 · 예: 1/8 → 0.5,0.5,0.5).</summary>
    public static string AtempoChain(double speed)
    {
        speed = Math.Clamp(speed, 1.0 / 64, 2);
        var parts = new List<string>();
        while (speed < 0.5 - 1e-9) { parts.Add("atempo=0.5"); speed /= 0.5; }
        parts.Add("atempo=" + speed.ToString("0.######", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    public static SlowMoSegment Normalize(SlowMoSegment s, double duration)
    {
        var st = Math.Clamp(s.Start, 0, Math.Max(0, duration));
        var en = Math.Clamp(s.End, st, Math.Max(st, duration));
        return new(st, en, Math.Clamp(s.Factor, 2, MaxSlowFactor));
    }

    /// <summary>
    /// 전체 보간 + 슬로모션 filter_complex — [0:v] 를 (전/구간/후)로 trim → 구간은 fps×배율 보간 후 setpts×배율 → 전 구간 목표 fps 보간 → concat.
    /// hasAudio 면 오디오도 동일 분할 + atempo.
    /// </summary>
    public static string BuildSlowMoFilter(double srcFps, int targetFps, SlowMoSegment seg, double duration, bool hasAudio)
    {
        var ci = CultureInfo.InvariantCulture;
        var s = Normalize(seg, duration);
        string F(double v) => v.ToString("0.###", ci);
        var slowFps = Math.Max(targetFps, srcFps * s.Factor);
        var sb = new System.Text.StringBuilder(512);
        sb.Append("[0:v]split=3[v0][v1][v2];");
        sb.Append("[v0]trim=start=0:end=").Append(F(s.Start)).Append(",setpts=PTS-STARTPTS,").Append(MinterpolateFilter(targetFps)).Append("[a0v];");
        sb.Append("[v1]trim=start=").Append(F(s.Start)).Append(":end=").Append(F(s.End)).Append(",setpts=PTS-STARTPTS,").Append(MinterpolateFilter(slowFps))
          .Append(",setpts=").Append(s.Factor.ToString(ci)).Append("*PTS,fps=").Append(targetFps.ToString(ci)).Append("[a1v];");
        sb.Append("[v2]trim=start=").Append(F(s.End)).Append(",setpts=PTS-STARTPTS,").Append(MinterpolateFilter(targetFps)).Append("[a2v];");
        if (hasAudio)
        {
            sb.Append("[0:a]asplit=3[x0][x1][x2];");
            sb.Append("[x0]atrim=start=0:end=").Append(F(s.Start)).Append(",asetpts=PTS-STARTPTS[a0a];");
            sb.Append("[x1]atrim=start=").Append(F(s.Start)).Append(":end=").Append(F(s.End)).Append(",asetpts=PTS-STARTPTS,").Append(AtempoChain(1.0 / s.Factor)).Append("[a1a];");
            sb.Append("[x2]atrim=start=").Append(F(s.End)).Append(",asetpts=PTS-STARTPTS[a2a];");
            sb.Append("[a0v][a0a][a1v][a1a][a2v][a2a]concat=n=3:v=1:a=1[outv][outa]");
        }
        else sb.Append("[a0v][a1v][a2v]concat=n=3:v=1:a=0[outv]");
        return sb.ToString();
    }

    public static List<string> BuildRifeArgs(string inDir, string outDir, int targetFrames) => new() { "-i", inDir, "-o", outDir, "-n", targetFrames.ToString(CultureInfo.InvariantCulture), "-m", "rife-v4.6", "-f", "%08d.png" };

    public static List<string> BuildFlavrArgs(string inDir, string outDir, int factor) => new() { "--input", inDir, "--output", outDir, "--factor", Math.Clamp(factor, 2, 8).ToString(CultureInfo.InvariantCulture) };

    /// <summary>목표 프레임 수 = ceil(원본 프레임 × 목표fps/원본fps).</summary>
    public static int TargetFrames(int srcFrames, double srcFps, int targetFps) => srcFps <= 0 ? srcFrames : (int)Math.Ceiling(srcFrames * (targetFps / srcFps));

    // ══════════════════════ 영상 ══════════════════════

    /// <summary>목표 fps 보간(선택 슬로모션 구간 · 원본 오디오 유지/구간 atempo).</summary>
    public async Task<VfxVideoResult> InterpolateVideoAsync(string input, string output, SlowMoSegment? slow = null, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(input)) return Done(new(false, null, 0, "—", "ffmpeg 또는 입력 파일이 없습니다."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, 0, "—", "다른 보간이 진행 중입니다.");
        Progress = 0;
        var target = TargetFps is 60 or 120 ? TargetFps : Math.Clamp(TargetFps, 24, 240);
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_interp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, input, ct).ConfigureAwait(false);
            if (probe is null) return Done(new(false, null, 0, "—", "영상 정보를 읽지 못했습니다."));
            var srcFps = await AiNeuralVfxRelightingEngine.ProbeFpsAsync(ffmpeg, input, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var note = "";
            var useAi = slow is null && Engine != InterpEngine.FfmpegMci && (RifeAvailable || FlavrAvailable);
            if (useAi)
            {
                var pickFlavr = Engine == InterpEngine.Flavr ? FlavrAvailable : !RifeAvailable;
                var a = Path.Combine(tmp, "in"); var b = Path.Combine(tmp, "out");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                Step(0.05, "🧠 프레임 추출");
                var ok = await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, new() { "-hide_banner", "-v", "error", "-i", input, Path.Combine(a, "%08d.png") }, ct).ConfigureAwait(false) == 0;
                var srcFrames = ok ? Directory.GetFiles(a, "*.png").Length : 0;
                if (srcFrames >= 2)
                {
                    Step(0.2, pickFlavr ? "🧠 FLAVR 보간" : "🧠 RIFE 보간");
                    var factor = (int)Math.Clamp(Math.Round(target / srcFps), 2, 8);
                    var code = pickFlavr
                        ? await AiGenerativeFoleySoundscapeEngine.RunAsync(_flavr()!, BuildFlavrArgs(a, b, factor), ct).ConfigureAwait(false)
                        : await AiGenerativeFoleySoundscapeEngine.RunAsync(_rife()!, BuildRifeArgs(a, b, TargetFrames(srcFrames, srcFps, target)), ct).ConfigureAwait(false);
                    var outFrames = code == 0 ? Directory.GetFiles(b, "*.png").Length : 0;
                    if (outFrames > srcFrames)
                    {
                        Step(0.85, "🎞 인코드");
                        var outFps = srcFps * outFrames / srcFrames;
                        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-framerate", outFps.ToString("0.###", CultureInfo.InvariantCulture), "-i", Path.Combine(b, "%08d.png"),
                            "-i", input, "-map", "0:v:0", "-map", "1:a:0?", "-vf", "fps=" + target.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-crf", "17", "-preset", "medium", "-pix_fmt", "yuv420p",
                            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output };
                        if (await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, args, ct).ConfigureAwait(false) == 0 && File.Exists(output))
                            return Ok(output, outFrames, pickFlavr ? "FLAVR" : "RIFE v4.6", target, "");
                    }
                }
                note = "[부분 구제] AI 보간 실패 → ffmpeg MCI 폴백. ";
                VfxProcess.DeleteQuiet(output);
            }
            Step(0.3, slow is null ? "🌊 MCI 보간" : "🐢 슬로모션 보간");
            List<string> fa;
            if (slow is { } sl)
            {
                var hasAudio = await AudioPresentAsync(ffmpeg, input, ct).ConfigureAwait(false);
                fa = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", input, "-filter_complex", BuildSlowMoFilter(srcFps, target, sl, probe.Value.Duration, hasAudio), "-map", "[outv]" };
                if (hasAudio) fa.AddRange(new[] { "-map", "[outa]", "-c:a", "aac", "-b:a", "192k" });
            }
            else fa = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", input, "-vf", MinterpolateFilter(target), "-c:a", "copy" };
            fa.AddRange(new[] { "-c:v", "libx264", "-crf", "17", "-preset", "medium", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output });
            if (await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, fa, ct).ConfigureAwait(false) == 0 && File.Exists(output))
                return Ok(output, 0, "ffmpeg minterpolate(MCI)" + (slow is { } s2 ? " · " + Math.Clamp(s2.Factor, 2, 8).ToString(CultureInfo.InvariantCulture) + "x 슬로모션" : ""), target, note);
            VfxProcess.DeleteQuiet(output);
            return Done(new(false, null, 0, "—", note + "보간 실패(ffmpeg 확인)"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { VfxProcess.DeleteQuiet(output); return Done(new(false, null, 0, "—", "취소됨")); }
        catch (Exception ex) { Program.Log("Interp.Video", ex); VfxProcess.DeleteQuiet(output); return Done(new(false, null, 0, "—", "보간 실패: " + ex.Message)); }
        finally
        {
            VfxProcess.DeleteDirQuiet(tmp);
            Progress = 1;
            Volatile.Write(ref _busy, 0);
            VfxProcess.Raise(OnChange, "Interp.OnChange");
        }
    }

    private static async Task<bool> AudioPresentAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input);
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        if (!p.Start()) return false;
        try { return (await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Contains("Audio:", StringComparison.Ordinal); }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
    }

    private VfxVideoResult Ok(string output, int frames, string engine, int fps, string note)
    {
        Interlocked.Increment(ref _jobs);
        LastAction = "🌊 " + engine + " · " + fps.ToString(CultureInfo.InvariantCulture) + "fps";
        return Done(new(true, output, frames, engine, (note + fps.ToString(CultureInfo.InvariantCulture) + "fps 보간 완료").Trim()));
    }

    private void Step(double p, string a) { Progress = p; LastAction = a; VfxProcess.Raise(OnChange, "Interp.OnChange"); }

    private VfxVideoResult Done(VfxVideoResult r)
    {
        LastResult = r;
        if (!r.Ok) LastAction = "⚠ 보간 " + r.Message;
        return r;
    }
}
