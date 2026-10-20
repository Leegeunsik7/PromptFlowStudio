using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;       // EngineConfigService · EngineKeys
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService
using PromptFlowStudio.Services.Sound;         // AiGenerativeFoleySoundscapeEngine.RunAsync
using PromptFlowStudio.Services.Spatial;       // SpatialImageCodec

namespace PromptFlowStudio.Services.Vfx;

public enum SrTarget : byte { Uhd4k, Uhd8k }

public enum SrModel : byte { RealEsrganX4Plus, RealEsrGeneralCompact, RealEsrganAnime }

/// <summary>업스케일 설정 — 목표 · 모델 · 샤프니스(0~2) · 디노이즈(0~1) · 디블록(아티팩트 제거) · 품질(CRF).</summary>
public readonly record struct UpscaleSettings(SrTarget Target, SrModel Model, float Sharpness, float Denoise, bool Deblock, int Crf)
{
    public static readonly UpscaleSettings Default = new(SrTarget.Uhd8k, SrModel.RealEsrGeneralCompact, 0.6f, 0.35f, true, 16);

    public UpscaleSettings Clamp() => this with { Sharpness = Math.Clamp(Sharpness, 0f, 2f), Denoise = Math.Clamp(Denoise, 0f, 1f), Crf = Math.Clamp(Crf, 10, 30) };
}

public sealed record UpscalePreview(int SrcWidth, int SrcHeight, int Width, int Height, string BeforeDataUrl, string AfterDataUrl, double Milliseconds);

/// <summary>
/// [PromptFlow Studio v16.34.0] 8K 초고화질 AI 업스케일러 &amp; 노이즈 정제기.
///   ▸ AI 경로: Real-ESRGAN ncnn(엔진 설정 RealEsrgan 또는 PFS_REALESRGAN) — realesrgan-x4plus / realesr-general-x4v3(Compact) / anime 모델
///     · 프레임 PNG 추출 → x4 패스 → (8K 미달 시 2차 패스) → ffmpeg lanczos 정확 해상도 + 샤프 · 원본 오디오 · 임시 폴더 정리.
///   ▸ BCL/ffmpeg 폴백: hqdn3d 디노이즈 → (디블록 pp7) → lanczos 업스케일 → unsharp 샤프니스 · CRF 인코드(AI 부재/실패 즉시 폴백 [부분 구제]).
///   ▸ BCL 커널(프리뷰/테스트): 3×3 엣지 보존 디노이즈 · 8×8 블록 경계 디블록 · Catmull-Rom 바이큐빅 업스케일 · 언샤프 마스크(무할당 Span).
///   ▸ 목표 해상도: 종횡비 유지 7680×4320(8K)/3840×2160(4K) 박스 맞춤 · 짝수.
/// </summary>
public sealed class SuperResolution8kUpscalerEngine
{
    public const int PreviewWidth = 320;

    private readonly Func<string?> _ffmpeg;
    private readonly Func<string?> _esrgan;
    private int _busy;
    private long _jobs;

    public SuperResolution8kUpscalerEngine(IVideoMergeService merge, EngineConfigService? engines = null)
        : this(() => merge.ResolvedFfmpegPath, () => engines?.Resolve(EngineKeys.RealEsrgan) ?? s_esrgan.Value) { }

    public SuperResolution8kUpscalerEngine(Func<string?> ffmpeg, Func<string?>? esrgan)
    {
        _ffmpeg = ffmpeg;
        _esrgan = esrgan ?? (() => null);
    }

    private static readonly Lazy<string?> s_esrgan = new(() => VfxProcess.ResolveCli("PFS_REALESRGAN", "realesrgan-ncnn-vulkan.exe", "realesrgan-ncnn-vulkan"));

    public event Action? OnChange;

    public UpscaleSettings Settings { get; set; } = UpscaleSettings.Default;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public bool AiAvailable => _esrgan() is not null;
    public long Jobs => Interlocked.Read(ref _jobs);
    public double Progress { get; private set; }
    public UpscalePreview? LastPreview { get; private set; }
    public VfxVideoResult? LastResult { get; private set; }
    public string? LastAction { get; private set; }

    public static string ModelName(SrModel m) => m switch { SrModel.RealEsrganX4Plus => "realesrgan-x4plus", SrModel.RealEsrganAnime => "realesrgan-x4plus-anime", _ => "realesr-general-x4v3" };

    /// <summary>종횡비 유지 박스 맞춤(짝수). 원본이 이미 크면 원본 유지.</summary>
    public static (int W, int H) TargetSize(int srcW, int srcH, SrTarget t)
    {
        if (srcW <= 0 || srcH <= 0) return (0, 0);
        var (bw, bh) = t == SrTarget.Uhd8k ? (7680, 4320) : (3840, 2160);
        if (srcH > srcW) (bw, bh) = (bh, bw);   // 세로 영상
        var s = Math.Min((double)bw / srcW, (double)bh / srcH);
        if (s <= 1) return (srcW & ~1, srcH & ~1);
        return ((int)Math.Round(srcW * s) & ~1, (int)Math.Round(srcH * s) & ~1);
    }

    /// <summary>x4 AI 패스 수(목표 배율 도달까지 · 최대 2).</summary>
    public static int AiPasses(int srcW, int dstW) => dstW <= srcW ? 0 : dstW <= srcW * 4 ? 1 : 2;

    // ══════════════════════ BCL 커널 ══════════════════════

    /// <summary>3×3 엣지 보존 디노이즈(값 차 가중 · strength 0~1). 무할당.</summary>
    public static void Denoise(ReadOnlySpan<byte> src, int w, int h, float strength, Span<byte> dst)
    {
        var n = w * h * 3;
        if (src.Length < n || dst.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        if (strength <= 0) { src.Slice(0, n).CopyTo(dst); return; }
        var sigma = 8f + 40f * Math.Clamp(strength, 0f, 1f);
        var inv = 1f / (2 * sigma * sigma);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    float center = src[o + c], sum = 0, ws = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var yy = Math.Clamp(y + dy, 0, h - 1);
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var v = src[(yy * w + Math.Clamp(x + dx, 0, w - 1)) * 3 + c];
                            var d = v - center;
                            var wt = MathF.Exp(-d * d * inv) * (dx == 0 && dy == 0 ? 1f : 0.6f);
                            sum += v * wt; ws += wt;
                        }
                    }
                    dst[o + c] = (byte)Math.Clamp(sum / ws + 0.5f, 0f, 255f);
                }
            }
    }

    /// <summary>8×8 블록 경계 디블록 — 경계 단차가 작을 때(블로킹)만 양쪽 2px 평활(실제 엣지 보존). 제자리 · 무할당.</summary>
    public static int Deblock(Span<byte> img, int w, int h, int threshold = 24)
    {
        var fixedEdges = 0;
        for (var y = 0; y < h; y++)
            for (var bx = 8; bx < w - 1; bx += 8)
                for (var c = 0; c < 3; c++)
                    if (Smooth(img, (y * w + bx - 2) * 3 + c, 3, threshold)) fixedEdges++;
        for (var by = 8; by < h - 1; by += 8)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                    if (Smooth(img, ((by - 2) * w + x) * 3 + c, w * 3, threshold)) fixedEdges++;
        return fixedEdges;
    }

    private static bool Smooth(Span<byte> p, int i0, int stride, int thr)
    {
        if (i0 + 3 * stride >= p.Length || i0 < 0) return false;
        int a = p[i0], b = p[i0 + stride], c = p[i0 + 2 * stride], d = p[i0 + 3 * stride];
        var step = Math.Abs(c - b);
        if (step == 0 || step > thr || Math.Abs(b - a) > thr / 2 || Math.Abs(d - c) > thr / 2) return false;
        p[i0 + stride] = (byte)((a + 2 * b + c + 2) >> 2);
        p[i0 + 2 * stride] = (byte)((b + 2 * c + d + 2) >> 2);
        return true;
    }

    private static float CatRom(float t, float p0, float p1, float p2, float p3)
        => 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);

    /// <summary>Catmull-Rom 바이큐빅 리사이즈(rgb24 · 무할당).</summary>
    public static void ResizeBicubic(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        if (src.Length < sw * sh * 3 || dst.Length < dw * dh * 3) throw new ArgumentException("버퍼 크기 불일치");
        float sx = (float)sw / dw, sy = (float)sh / dh;
        for (var y = 0; y < dh; y++)
        {
            var fy = (y + 0.5f) * sy - 0.5f;
            var iy = (int)MathF.Floor(fy); var ty = fy - iy;
            for (var x = 0; x < dw; x++)
            {
                var fx = (x + 0.5f) * sx - 0.5f;
                var ix = (int)MathF.Floor(fx); var tx = fx - ix;
                for (var c = 0; c < 3; c++)
                {
                    float r0 = Row(src, sw, sh, ix, iy - 1, c, tx), r1 = Row(src, sw, sh, ix, iy, c, tx), r2 = Row(src, sw, sh, ix, iy + 1, c, tx), r3 = Row(src, sw, sh, ix, iy + 2, c, tx);
                    dst[(y * dw + x) * 3 + c] = (byte)Math.Clamp(CatRom(ty, r0, r1, r2, r3) + 0.5f, 0f, 255f);
                }
            }
        }
    }

    private static float Row(ReadOnlySpan<byte> s, int w, int h, int ix, int y, int c, float t)
    {
        var yy = Math.Clamp(y, 0, h - 1) * w;
        return CatRom(t, s[(yy + Math.Clamp(ix - 1, 0, w - 1)) * 3 + c], s[(yy + Math.Clamp(ix, 0, w - 1)) * 3 + c], s[(yy + Math.Clamp(ix + 1, 0, w - 1)) * 3 + c], s[(yy + Math.Clamp(ix + 2, 0, w - 1)) * 3 + c]);
    }

    /// <summary>언샤프 마스크(3×3 가우시안 근사 · amount 0~2 · 제자리 · tmp ≥ n). 무할당.</summary>
    public static void Sharpen(Span<byte> img, int w, int h, float amount, Span<byte> tmp)
    {
        var n = w * h * 3;
        if (amount <= 0) return;
        if (tmp.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        img.Slice(0, n).CopyTo(tmp);
        for (var y = 0; y < h; y++)
        {
            int ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1);
            for (var x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
                for (var c = 0; c < 3; c++)
                {
                    float blur = (tmp[(ym * w + xm) * 3 + c] + 2 * tmp[(ym * w + x) * 3 + c] + tmp[(ym * w + xp) * 3 + c]
                                + 2 * tmp[(y * w + xm) * 3 + c] + 4 * tmp[(y * w + x) * 3 + c] + 2 * tmp[(y * w + xp) * 3 + c]
                                + tmp[(yp * w + xm) * 3 + c] + 2 * tmp[(yp * w + x) * 3 + c] + tmp[(yp * w + xp) * 3 + c]) * (1f / 16f);
                    var v = tmp[(y * w + x) * 3 + c];
                    img[(y * w + x) * 3 + c] = (byte)Math.Clamp(v + (v - blur) * amount + 0.5f, 0f, 255f);
                }
            }
        }
    }

    /// <summary>BCL 업스케일 체인(디노이즈 → 디블록 → 바이큐빅 → 샤프) — 프리뷰/테스트.</summary>
    public static byte[] UpscaleBcl(byte[] src, int sw, int sh, int dw, int dh, in UpscaleSettings s)
    {
        var st = s.Clamp();
        var clean = new byte[sw * sh * 3];
        Denoise(src, sw, sh, st.Denoise, clean);
        if (st.Deblock) Deblock(clean, sw, sh);
        var dst = new byte[dw * dh * 3];
        ResizeBicubic(clean, sw, sh, dst, dw, dh);
        Sharpen(dst, dw, dh, st.Sharpness, new byte[dw * dh * 3]);
        return dst;
    }

    /// <summary>프리뷰 — 원본 크롭(최대 160px)을 4배 확대(이전=최근접 · 이후=정제 체인).</summary>
    public UpscalePreview SetPreview(byte[] src, int w, int h)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int cw = Math.Min(160, w) & ~1, ch = Math.Min(90, h) & ~1;
        int ox = (w - cw) / 2, oy = (h - ch) / 2;
        var crop = new byte[cw * ch * 3];
        for (var y = 0; y < ch; y++) src.AsSpan(((oy + y) * w + ox) * 3, cw * 3).CopyTo(crop.AsSpan(y * cw * 3));
        int dw = cw * 4, dh = ch * 4;
        var after = UpscaleBcl(crop, cw, ch, dw, dh, Settings);
        var before = new byte[dw * dh * 3];
        for (var y = 0; y < dh; y++) for (var x = 0; x < dw; x++) crop.AsSpan(((y / 4) * cw + x / 4) * 3, 3).CopyTo(before.AsSpan((y * dw + x) * 3));
        var p = new UpscalePreview(cw, ch, dw, dh, SpatialImageCodec.BmpDataUrl(before, dw, dh), SpatialImageCodec.BmpDataUrl(after, dw, dh), System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        LastPreview = p;
        LastAction = "🔎 업스케일 프리뷰 · 샤프 " + Settings.Sharpness.ToString("0.0", CultureInfo.InvariantCulture);
        VfxProcess.Raise(OnChange, "Upscale.OnChange");
        return p;
    }

    public async Task<UpscalePreview?> PreviewAsync(string path, double seconds, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(path)) return null;
        var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, path, ct).ConfigureAwait(false);
        if (probe is null) return null;
        int w = Math.Min(probe.Value.W, 1920) & ~1, h = Math.Max(2, (int)Math.Round(w * (double)probe.Value.H / probe.Value.W) & ~1);
        var f = await AiNeuralVfxRelightingEngine.GrabFrameAsync(ffmpeg, path, Math.Max(0, seconds), w, h, ct).ConfigureAwait(false);
        return f is null ? null : SetPreview(f, w, h);
    }

    // ══════════════════════ 인자 빌더 ══════════════════════

    public static string BuildFilter(int dw, int dh, in UpscaleSettings s, bool aiPrescaled)
    {
        var st = s.Clamp();
        var ci = CultureInfo.InvariantCulture;
        var parts = new List<string>(4);
        if (!aiPrescaled)
        {
            if (st.Denoise > 0) parts.Add("hqdn3d=" + (1.5 + 4 * st.Denoise).ToString("0.##", ci) + ":" + (1.0 + 3 * st.Denoise).ToString("0.##", ci) + ":" + (3 + 5 * st.Denoise).ToString("0.##", ci) + ":" + (2.5 + 4 * st.Denoise).ToString("0.##", ci));
            if (st.Deblock) parts.Add("pp7=qp=4:mode=medium");
        }
        parts.Add("scale=" + dw.ToString(ci) + ":" + dh.ToString(ci) + ":flags=lanczos");
        if (st.Sharpness > 0) parts.Add("unsharp=5:5:" + (st.Sharpness * (aiPrescaled ? 0.4f : 1f)).ToString("0.##", ci) + ":5:5:0");
        return string.Join(",", parts);
    }

    public static List<string> BuildFfmpegArgs(string input, string output, int dw, int dh, in UpscaleSettings s, bool aiPrescaled, double? framesFps = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-v", "error", "-y" };
        if (framesFps is { } fps) a.AddRange(new[] { "-framerate", fps.ToString("0.###", ci), "-i", Path.Combine(input, "%08d.png") });
        else a.AddRange(new[] { "-i", input });
        a.AddRange(new[] { "-vf", BuildFilter(dw, dh, s, aiPrescaled) });
        var heavy = (long)dw * dh > 3840L * 2160;
        a.AddRange(new[] { "-c:v", heavy ? "libx265" : "libx264", "-crf", s.Clamp().Crf.ToString(ci), "-preset", "medium", "-pix_fmt", "yuv420p" });
        if (heavy) a.AddRange(new[] { "-tag:v", "hvc1", "-x265-params", "log-level=error" });
        return a;
    }

    public static List<string> BuildEsrganArgs(string inDir, string outDir, SrModel model) => new() { "-i", inDir, "-o", outDir, "-s", "4", "-n", ModelName(model), "-f", "png" };

    // ══════════════════════ 영상 ══════════════════════

    public async Task<VfxVideoResult> UpscaleVideoAsync(string input, string output, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(input)) return Done(new(false, null, 0, "—", "ffmpeg 또는 입력 파일이 없습니다."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, 0, "—", "다른 업스케일이 진행 중입니다.");
        Progress = 0;
        var s = Settings;
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_sr_" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, input, ct).ConfigureAwait(false);
            if (probe is null) return Done(new(false, null, 0, "—", "해상도를 읽지 못했습니다."));
            var (dw, dh) = TargetSize(probe.Value.W, probe.Value.H, s.Target);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var note = "";
            if (_esrgan() is { } esr && AiPasses(probe.Value.W, dw) > 0)
            {
                Step(0.05, "🧠 Real-ESRGAN 프레임 추출");
                var fps = await AiNeuralVfxRelightingEngine.ProbeFpsAsync(ffmpeg, input, ct).ConfigureAwait(false);
                var a = Path.Combine(tmp, "a"); var b = Path.Combine(tmp, "b");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                var ok = await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, new() { "-hide_banner", "-v", "error", "-i", input, "-vf", BuildFilter(probe.Value.W, probe.Value.H, s with { Sharpness = 0 }, false), Path.Combine(a, "%08d.png") }, ct).ConfigureAwait(false) == 0;
                var passes = AiPasses(probe.Value.W, dw);
                for (var p = 0; ok && p < passes; p++)
                {
                    Step(0.15 + 0.35 * p, "🧠 Real-ESRGAN x4 패스 " + (p + 1).ToString(CultureInfo.InvariantCulture) + "/" + passes.ToString(CultureInfo.InvariantCulture));
                    ok = await AiGenerativeFoleySoundscapeEngine.RunAsync(esr, BuildEsrganArgs(a, b, s.Model), ct).ConfigureAwait(false) == 0 && Directory.GetFiles(b, "*.png").Length > 0;
                    if (ok) { VfxProcess.DeleteDirQuiet(a); Directory.Move(b, a); Directory.CreateDirectory(b); }
                }
                if (ok)
                {
                    Step(0.85, "🎞 8K 인코드");
                    var args = BuildFfmpegArgs(a, output, dw, dh, s, true, fps);
                    args.InsertRange(args.IndexOf("-vf"), new[] { "-i", input, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "192k" });
                    args.Add(output);
                    if (await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, args, ct).ConfigureAwait(false) == 0 && File.Exists(output))
                        return Ok(output, dw, dh, "Real-ESRGAN " + ModelName(s.Model), "");
                }
                note = "[부분 구제] Real-ESRGAN 실패 → ffmpeg 정제 폴백. ";
                VfxProcess.DeleteQuiet(output);
            }
            Step(0.4, "🎞 정제 업스케일(ffmpeg)");
            var fa = BuildFfmpegArgs(input, output, dw, dh, s, false);
            fa.AddRange(new[] { "-c:a", "copy", output });
            if (await AiGenerativeFoleySoundscapeEngine.RunAsync(ffmpeg, fa, ct).ConfigureAwait(false) == 0 && File.Exists(output))
                return Ok(output, dw, dh, "ffmpeg lanczos+hqdn3d", note);
            VfxProcess.DeleteQuiet(output);
            return Done(new(false, null, 0, "—", note + "업스케일 실패(인코더/디스크 확인)"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { VfxProcess.DeleteQuiet(output); return Done(new(false, null, 0, "—", "취소됨")); }
        catch (Exception ex) { Program.Log("Upscale.Video", ex); VfxProcess.DeleteQuiet(output); return Done(new(false, null, 0, "—", "업스케일 실패: " + ex.Message)); }
        finally
        {
            VfxProcess.DeleteDirQuiet(tmp);
            Progress = 1;
            Volatile.Write(ref _busy, 0);
            VfxProcess.Raise(OnChange, "Upscale.OnChange");
        }
    }

    private VfxVideoResult Ok(string output, int w, int h, string engine, string note)
    {
        Interlocked.Increment(ref _jobs);
        var msg = (note + w.ToString(CultureInfo.InvariantCulture) + "×" + h.ToString(CultureInfo.InvariantCulture) + " 업스케일 완료").Trim();
        LastAction = "✨ " + engine + " · " + w.ToString(CultureInfo.InvariantCulture) + "×" + h.ToString(CultureInfo.InvariantCulture);
        return Done(new(true, output, 0, engine, msg));
    }

    private void Step(double p, string a) { Progress = p; LastAction = a; VfxProcess.Raise(OnChange, "Upscale.OnChange"); }

    private VfxVideoResult Done(VfxVideoResult r)
    {
        LastResult = r;
        if (!r.Ok) LastAction = "⚠ 업스케일 " + r.Message;
        return r;
    }
}
