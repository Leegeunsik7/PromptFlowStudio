using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎯 사진별 초점(정규화 0~1 · 좌상단 원점) 검출 — 로컬 시각 돌출도 + 선택적 Gemini Vision 보정.</summary>
public interface IPlutoImageFocalDetector
{
    (double X, double Y) DetectLocalFocalPoint(string imagePath);

    Task<IReadOnlyList<(double X, double Y)>> ExtractBatchFocalPointsAsync(IReadOnlyList<string> imagePaths, PlutoVisionStorySynthesizer? visionSynthesizer = null, CancellationToken ct = default);
}

/// <summary>
/// 🎯 Pluto 초점 검출기 — 최대 96×96 썸네일에서 픽셀 돌출도 S = (피부톤 2.5 + 1.5·|ΔY|) × 중앙 가우시안 사전(μ = (0.5, 0.42) · σ = 0.28) 가중 중심.
///   ▸ 결과는 [0.20, 0.80]² 로 클램프(카메라 줌 시 피사체 이탈 · 블랙 보더 차단). 판독 불가 · 비 Windows → <see cref="Fallback"/>.
///   ▸ 배치: 로컬 기준점 전량 산출 후 비전 설정 시 병렬 보정(동시 3 · 사진당 4초) — 실패/시간 초과 사진은 로컬 값 보존([부분 구제]).
/// </summary>
public sealed class PlutoImageFocalDetector : IPlutoImageFocalDetector
{
    public static readonly PlutoImageFocalDetector Shared = new();

    public const int GridSize = 96;
    public const double MinCoord = 0.20, MaxCoord = 0.80;
    public const double SkinWeight = 2.5, GradientWeight = 1.5;
    public const double PriorX = 0.5, PriorY = 0.42, PriorSigma = 0.28;
    public const int VisionConcurrency = 3;
    public static readonly (double X, double Y) Fallback = (0.50, 0.42);
    public static readonly TimeSpan VisionTimeout = TimeSpan.FromSeconds(4);

    public (double X, double Y) DetectLocalFocalPoint(string imagePath)
    {
#if WINDOWS
        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath)) return Fallback;
        int[]? px = null;
        try
        {
            using var src = new System.Drawing.Bitmap(imagePath);
            int w = Math.Max(1, src.Width), h = Math.Max(1, src.Height);
            var scale = Math.Min(1.0, (double)GridSize / Math.Max(w, h));
            int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
            using var thumb = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(src, 0, 0, sw, sh);
            }
            var data = thumb.LockBits(new System.Drawing.Rectangle(0, 0, sw, sh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                px = System.Buffers.ArrayPool<int>.Shared.Rent(sw * sh);
                for (var y = 0; y < sh; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * sw, sw);
                return DetectFromPixels(px.AsSpan(0, sw * sh), sw, sh);
            }
            finally { thumb.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.FocalDetector] " + ex.Message);   // [부분 구제] 기본 초점
            return Fallback;
        }
        finally
        {
            if (px is not null) System.Buffers.ArrayPool<int>.Shared.Return(px);
        }
#else
        _ = imagePath;
        return Fallback;
#endif
    }

    /// <summary>🧮 ARGB(0xAARRGGBB) 격자 → 돌출도 가중 중심(무할당 · 1패스). 빈 입력/무가중 → <see cref="Fallback"/>.</summary>
    public static (double X, double Y) DetectFromPixels(ReadOnlySpan<int> argb, int width, int height)
    {
        if (width <= 0 || height <= 0 || argb.Length < width * height) return Fallback;
        const double inv2Sigma2 = 1.0 / (2.0 * PriorSigma * PriorSigma);
        double sw = 0, sx = 0, sy = 0;
        var du = width > 1 ? 1.0 / (width - 1) : 0.0;
        var dv = height > 1 ? 1.0 / (height - 1) : 0.0;
        for (var y = 0; y < height; y++)
        {
            var v = height > 1 ? y * dv : 0.5;
            var dyv = v - PriorY;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var p = argb[row + x];
                int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                var s = 0.0;
                if (r > 95 && g > 40 && b > 20 && r > g && r > b && Math.Abs(r - g) > 15) s += SkinWeight;

                // |ΔY| — 중앙 차분(가장자리는 단측) 수평·수직 합(Y 정규화 0~1).
                var yl = Luma(p);
                var xl = x > 0 ? Luma(argb[row + x - 1]) : yl;
                var xr = x < width - 1 ? Luma(argb[row + x + 1]) : yl;
                var yu = y > 0 ? Luma(argb[row - width + x]) : yl;
                var yd = y < height - 1 ? Luma(argb[row + width + x]) : yl;
                var grad = Math.Abs(xr - xl) * 0.5 + Math.Abs(yd - yu) * 0.5;
                s += GradientWeight * grad;
                if (s <= 0) continue;

                var u = width > 1 ? x * du : 0.5;
                var dux = u - PriorX;
                s *= Math.Exp(-(dux * dux + dyv * dyv) * inv2Sigma2);
                sw += s;
                sx += s * u;
                sy += s * v;
            }
        }
        if (!(sw > 1e-9)) return Fallback;
        return (Math.Clamp(sx / sw, MinCoord, MaxCoord), Math.Clamp(sy / sw, MinCoord, MaxCoord));
    }

    private static double Luma(int p) => (0.299 * ((p >> 16) & 0xFF) + 0.587 * ((p >> 8) & 0xFF) + 0.114 * (p & 0xFF)) / 255.0;

    /// <summary>[0.20, 0.80]² 클램프(NaN/∞ → 폴백 축).</summary>
    public static (double X, double Y) ClampFocal((double X, double Y) f)
        => (double.IsFinite(f.X) ? Math.Clamp(f.X, MinCoord, MaxCoord) : Fallback.X, double.IsFinite(f.Y) ? Math.Clamp(f.Y, MinCoord, MaxCoord) : Fallback.Y);

    public async Task<IReadOnlyList<(double X, double Y)>> ExtractBatchFocalPointsAsync(IReadOnlyList<string> imagePaths, PlutoVisionStorySynthesizer? visionSynthesizer = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (imagePaths is null || imagePaths.Count == 0) return Array.Empty<(double X, double Y)>();
        var paths = new string[imagePaths.Count];
        for (var i = 0; i < paths.Length; i++) paths[i] = imagePaths[i] ?? "";

        // ① ⚡ 로컬 돌출도(백그라운드 · UI 스레드 차단 금지)
        var result = await Task.Run(() =>
        {
            var r = new (double X, double Y)[paths.Length];
            for (var i = 0; i < r.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                r[i] = DetectLocalFocalPoint(paths[i]);
            }
            return r;
        }, ct).ConfigureAwait(false);

        // ② 👁 Gemini Vision 병렬 보정(설정 시만 · 동시 3 · 사진당 4초 · 실패 → 로컬 유지)
        if (visionSynthesizer is null || !visionSynthesizer.IsVisionConfigured) return result;
        using var gate = new SemaphoreSlim(VisionConcurrency, VisionConcurrency);
        var tasks = new Task[paths.Length];
        for (var i = 0; i < paths.Length; i++)
            tasks[i] = RefineAsync(visionSynthesizer, gate, paths[i], i, result, ct);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task RefineAsync(PlutoVisionStorySynthesizer synth, SemaphoreSlim gate, string path, int index, (double X, double Y)[] result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(VisionTimeout);
            var plan = await synth.SynthesizeFromImageAsync(path, null, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            if (plan.Source == PlutoVisionSource.Vision) result[index] = ClampFocal(plan.DetectedFocalPoint);
        }
        catch (OperationCanceledException) { /* 시간 초과/취소 → 로컬 유지(호출자 취소는 상위에서 재검사) */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.FocalDetector.Vision] " + ex.Message);   // [부분 구제] 로컬 유지
        }
        finally { gate.Release(); }
    }
}
