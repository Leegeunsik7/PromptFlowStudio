using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // CliPathResolver

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>가변 얼굴 탐지 결과 — 피사체 무게중심(정규화 0~1) + 피부톤 픽셀 비율 + 방식.</summary>
public sealed record FaceCenterResult(bool Ok, double CenterXNorm, double CenterYNorm, double SkinFraction, string Method);

/// <summary>
/// [PromptFlowStudio] 오프라인 피부톤/휘도(Luma) 기반 가변 얼굴 탐지기 — <b>외부 ML 라이브러리 0</b> · 순수 C# BCL 바이트 배열 연산 + FFmpeg CLI 디코드.
///   ▸ FFmpeg 로 이미지를 축소 <c>rgb24 rawvideo</c>(폭 80px)로 디코드해 <b>픽셀 바이트 배열</b>을 받는다.
///   ▸ 각 픽셀 RGB→<b>YCbCr</b> 변환 후 피부톤 임계값(Cb∈[77,127]·Cr∈[133,173]·Y&gt;60)을 통과한 픽셀의 <b>휘도 가중 무게중심(Center of Mass)</b> Y좌표를 산출.
///   ▸ 이 Y를 <see cref="FaceCenterAutoCropService"/>의 동적 crop Y-Bias 로 주입 → 얼굴이 어디에 있든 정중앙 1:1 크롭.
///   ▸ 피부 픽셀이 너무 적으면 Ok=false(호출부가 상단 고정 0.35 로 Graceful 폴백).
/// </summary>
public sealed class LumaFaceDetector
{
    private const int SampleWidth = 80;        // 디코드 축소 폭(연산량↓ · 정확도 충분)
    private const double MinSkinFraction = 0.03;   // 피부 픽셀 최소 비율(미만이면 탐지 실패)

    /// <summary>이미지의 피사체(얼굴) 무게중심을 추론한다. ffmpeg 부재/실패/저신뢰는 Ok=false(폴백).</summary>
    public async Task<FaceCenterResult> DetectAsync(string imagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return new(false, 0.5, 0.35, 0, "none");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(false, 0.5, 0.35, 0, "none");

        try
        {
            var (rgb, w, h) = await DecodeRgb24Async(ffmpeg, imagePath, ct).ConfigureAwait(false);
            if (rgb is null || w <= 0 || h <= 0) return new(false, 0.5, 0.35, 0, "none");

            var (pts, skinFrac) = CollectSkin(rgb, w, h);
            if (skinFrac < MinSkinFraction) return new(false, 0.5, 0.35, skinFrac, "low-confidence");

            // 다중 클러스터(DBSCAN)로 배경 잡음(가구/조명 따뜻한 색) 제거 → 메인 안면 클러스터 채택.
            var cluster = MultiClusterSkinDetector.MainCluster(pts, w, h);
            if (cluster is not null) return new(true, cluster.CxNorm, cluster.CyNorm, skinFrac, "dbscan-cluster");

            var (gcx, gcy, _) = CenterOfMass(rgb, w, h);   // 폴백: 글로벌 무게중심
            return new(true, gcx, gcy, skinFrac, "ycbcr-luma");
        }
        catch (OperationCanceledException) { return new(false, 0.5, 0.35, 0, "canceled"); }
        catch (Exception ex) { Program.Log("LumaFaceDetector.Detect", ex); return new(false, 0.5, 0.35, 0, "error"); }
    }

    // ── 순수 BCL: 픽셀 바이트 순회 → YCbCr 피부톤 + 휘도 가중 무게중심 ──

    /// <summary>rgb24 바이트 배열(폭·높이)에서 피부톤 픽셀의 휘도 가중 무게중심(정규화 0~1)과 피부 비율을 계산한다.</summary>
    public static (double CxNorm, double CyNorm, double SkinFraction) CenterOfMass(byte[] rgb24, int width, int height)
    {
        if (rgb24 is null || width <= 0 || height <= 0) return (0.5, 0.35, 0);
        long need = (long)width * height * 3;
        if (rgb24.Length < need) return (0.5, 0.35, 0);

        double sumX = 0, sumY = 0, sumW = 0;
        long skin = 0;
        var idx = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte r = rgb24[idx], g = rgb24[idx + 1], b = rgb24[idx + 2];
                idx += 3;
                var (yy, cb, cr) = RgbToYCbCr(r, g, b);
                if (!IsSkin(yy, cb, cr)) continue;
                skin++;
                var wgt = yy;                 // 밝은 피부일수록 가중(그림자/배경 잡음 억제)
                sumX += x * wgt; sumY += y * wgt; sumW += wgt;
            }
        }

        var total = (double)width * height;
        var skinFrac = skin / total;
        if (sumW <= 0) return (0.5, 0.35, skinFrac);
        var cxNorm = Math.Clamp(sumX / sumW / width, 0, 1);
        var cyNorm = Math.Clamp(sumY / sumW / height, 0, 1);
        return (cxNorm, cyNorm, skinFrac);
    }

    /// <summary>피부톤 픽셀 좌표를 수집한다(과다 시 균일 샘플링으로 상한). DBSCAN 클러스터링 입력용.</summary>
    public static (List<(int X, int Y)> Points, double SkinFraction) CollectSkin(byte[] rgb24, int width, int height, int maxPoints = 1600)
    {
        var pts = new List<(int, int)>();
        if (rgb24 is null || width <= 0 || height <= 0 || rgb24.Length < (long)width * height * 3) return (pts, 0);

        long skin = 0;
        var idx = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++, idx += 3)
            {
                var (yy, cb, cr) = RgbToYCbCr(rgb24[idx], rgb24[idx + 1], rgb24[idx + 2]);
                if (!IsSkin(yy, cb, cr)) continue;
                skin++;
                pts.Add((x, y));
            }

        if (pts.Count > maxPoints)   // 연산 상한(균일 샘플링)
        {
            var stride = pts.Count / maxPoints;
            var capped = new List<(int, int)>(maxPoints);
            for (var k = 0; k < pts.Count; k += stride) capped.Add(pts[k]);
            pts = capped;
        }
        return (pts, skin / ((double)width * height));
    }

    /// <summary>RGB→YCbCr(BT.601). Y·Cb·Cr 각 0~255 범위.</summary>
    public static (double Y, double Cb, double Cr) RgbToYCbCr(byte r, byte g, byte b)
    {
        double y = 0.299 * r + 0.587 * g + 0.114 * b;
        double cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
        double cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
        return (y, cb, cr);
    }

    /// <summary>피부톤 판정 — 고전 YCbCr 범위(Cb 77~127 · Cr 133~173) + 최소 휘도(그림자 제외).</summary>
    public static bool IsSkin(double y, double cb, double cr)
        => y > 60 && cb >= 77 && cb <= 127 && cr >= 133 && cr <= 173;

    // ── FFmpeg 로 rgb24 rawvideo 디코드(바이너리 stdout) ──
    private static async Task<(byte[]? Rgb, int W, int H)> DecodeRgb24Async(string ffmpeg, string imagePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-y", "-i", imagePath, "-vf", $"scale={SampleWidth}:-2", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return (null, 0, 0);

        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, 81920, ct);
        var drain = proc.StandardError.ReadToEndAsync(ct);   // stderr 드레인(데드락 방지)
        await Task.WhenAll(copy, drain).ConfigureAwait(false);
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(true); } catch { } throw; }

        var bytes = ms.ToArray();
        if (bytes.Length < SampleWidth * 3) return (null, 0, 0);
        var h = bytes.Length / (SampleWidth * 3);           // rawvideo 헤더 없음 → 높이 역산
        return (bytes, SampleWidth, h);
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
