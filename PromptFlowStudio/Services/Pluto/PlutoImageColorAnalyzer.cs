using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎨 사진 1장의 노출 · 색 통계 — 휘도/표준편차는 [0,1] · <see cref="WarmCoolBias"/> = mean(R − B)/255 ∈ [−1, 1].</summary>
public sealed record PlutoImageColorMetrics(double MeanLuminance, double ContrastStdDev, double WarmCoolBias, bool IsLowLight, bool IsBacklit)
{
    /// <summary>Rec.709 Y &lt; 0.08 픽셀 비율(섀도 크러시).</summary>
    public double ShadowCrushRatio { get; init; }
    /// <summary>Rec.709 Y &gt; 0.90 픽셀 비율(하이라이트 클리핑).</summary>
    public double HighlightClipRatio { get; init; }
    /// <summary>디코드 실패/비Windows → 중립 기준값.</summary>
    public bool IsFallback { get; init; }

    /// <summary>중립 기준(목표 휘도 0.54 · 표준 대비 · 색온도 중립).</summary>
    public static readonly PlutoImageColorMetrics Neutral = new(PlutoImageColorAnalyzer.TargetLuminance, PlutoImageColorAnalyzer.TargetContrast, 0.0, false, false) { IsFallback = true };
}

/// <summary>🎨 클립 1개의 색보정 파라미터 — FFmpeg <c>eq</c>/<c>colorbalance</c>/<c>vignette</c> 로 컴파일(<see cref="PlutoColorGradingCompiler"/>).</summary>
public sealed record PlutoColorGradeProfile(string PresetName, double BrightnessOffset, double ContrastMultiplier, double SaturationMultiplier, double RedShadowShift, double BlueHighlightShift, bool EnableVignette);

/// <summary>🎨 사진 노출/색 분석 + 다중 클립 노출 정합 색보정 프로파일 생성.</summary>
public interface IPlutoImageColorAnalyzer
{
    /// <summary>≤128×128 그리드 서브샘플 분석 — 판독 불가/비Windows → <see cref="PlutoImageColorMetrics.Neutral"/>.</summary>
    PlutoImageColorMetrics AnalyzeImage(string imagePath);

    /// <summary>목표 휘도 0.54 로 노출 정합(밝기 [−0.12, +0.15]) + 시네마틱 룩 적용 · 오답 노트 차단 룩 → <see cref="PlutoColorGradingCompiler.NaturalPastel"/>.</summary>
    IReadOnlyList<PlutoColorGradeProfile> BuildHarmonizedProfiles(IReadOnlyList<PlutoImageColorMetrics> metrics, string preferredLook, IPlutoMistakeNotebookService? mistakeNotebook = null);
}

/// <summary>
/// 🎨 Pluto 이미지 색 분석기 — Rec.709 평균 휘도 · 대비(표준편차) · 섀도 크러시 · 역광(섀도/하이라이트 양봉) · 웜/쿨 편향(R − B).
/// 디코드는 <c>#if WINDOWS</c> System.Drawing(썸네일 축소 → LockBits 1회 복사) · 그 외 결정론 중립 폴백.
/// </summary>
public sealed class PlutoImageColorAnalyzer : IPlutoImageColorAnalyzer
{
    public static readonly PlutoImageColorAnalyzer Shared = new();

    public const int MaxSampleSize = 128;
    public const double TargetLuminance = 0.54, TargetContrast = 0.22;
    public const double MinBrightnessOffset = -0.12, MaxBrightnessOffset = 0.15;
    /// <summary>노출 오차 → 밝기 보정 이득(전량 보정은 노이즈/밴딩 유발 → 60%).</summary>
    public const double ExposureGain = 0.6;
    /// <summary>저조도/역광 섀도 리프트(밝기 가산 · 대비 감산).</summary>
    public const double ShadowLiftBrightness = 0.03, ShadowLiftContrast = 0.06;
    public const double LowLightLuminance = 0.30, CrushThreshold = 0.08, ClipThreshold = 0.90;

    public PlutoImageColorMetrics AnalyzeImage(string imagePath)
    {
#if WINDOWS
        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath)) return PlutoImageColorMetrics.Neutral;
        try
        {
            using var src = new System.Drawing.Bitmap(imagePath);
            int w = Math.Max(1, src.Width), h = Math.Max(1, src.Height);
            var scale = Math.Min(1.0, (double)MaxSampleSize / Math.Max(w, h));
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
                var px = new int[sw * sh];
                for (var y = 0; y < sh; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * sw, sw);
                return AnalyzePixels(px);
            }
            finally { thumb.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ColorAnalyzer] " + ex.Message);   // [부분 구제] 중립 기준
            return PlutoImageColorMetrics.Neutral;
        }
#else
        _ = imagePath;
        return PlutoImageColorMetrics.Neutral;
#endif
    }

    /// <summary>🧮 ARGB(0xAARRGGBB) 픽셀 → 통계(무할당 · 1패스). 빈 입력 → 중립.</summary>
    public static PlutoImageColorMetrics AnalyzePixels(ReadOnlySpan<int> argb)
    {
        if (argb.IsEmpty) return PlutoImageColorMetrics.Neutral;
        double sum = 0, sumSq = 0, rb = 0;
        int crush = 0, clip = 0;
        for (var i = 0; i < argb.Length; i++)
        {
            var p = argb[i];
            int r = (p >> 16) & 0xFF, gch = (p >> 8) & 0xFF, b = p & 0xFF;
            var yl = (0.2126 * r + 0.7152 * gch + 0.0722 * b) / 255.0;
            sum += yl;
            sumSq += yl * yl;
            rb += r - b;
            if (yl < CrushThreshold) crush++;
            else if (yl > ClipThreshold) clip++;
        }
        var n = (double)argb.Length;
        var mean = sum / n;
        var std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        double crushRatio = crush / n, clipRatio = clip / n;
        var lowLight = mean < LowLightLuminance || crushRatio > 0.45;
        var backlit = crushRatio > 0.18 && clipRatio > 0.18;   // 피사체 섀도 + 배경 하이라이트 양봉
        return new PlutoImageColorMetrics(mean, std, Math.Clamp(rb / n / 255.0, -1, 1), lowLight, backlit)
        {
            ShadowCrushRatio = crushRatio,
            HighlightClipRatio = clipRatio,
        };
    }

    public IReadOnlyList<PlutoColorGradeProfile> BuildHarmonizedProfiles(IReadOnlyList<PlutoImageColorMetrics> metrics, string preferredLook, IPlutoMistakeNotebookService? mistakeNotebook = null)
    {
        if (metrics is null || metrics.Count == 0) return Array.Empty<PlutoColorGradeProfile>();
        var look = PlutoColorGradingCompiler.ResolveLook(preferredLook, mistakeNotebook);
        var result = new PlutoColorGradeProfile[metrics.Count];
        for (var i = 0; i < result.Length; i++) result[i] = Harmonize(metrics[i] ?? PlutoImageColorMetrics.Neutral, look);
        return result;
    }

    /// <summary>🎨 단일 클립 정합 — 노출 오차 × 이득(클램프) · 대비 표준화 · 저조도/역광 섀도 리프트 · 색온도 편향 30% 중화.</summary>
    public static PlutoColorGradeProfile Harmonize(PlutoImageColorMetrics m, PlutoColorGradeProfile look)
    {
        var mean = double.IsFinite(m.MeanLuminance) ? Math.Clamp(m.MeanLuminance, 0, 1) : TargetLuminance;
        var std = double.IsFinite(m.ContrastStdDev) ? Math.Clamp(m.ContrastStdDev, 0, 0.5) : TargetContrast;
        var bias = double.IsFinite(m.WarmCoolBias) ? Math.Clamp(m.WarmCoolBias, -1, 1) : 0;
        var lift = m.IsLowLight || m.IsBacklit;

        var brightness = (TargetLuminance - mean) * ExposureGain + look.BrightnessOffset + (lift ? ShadowLiftBrightness : 0);
        var contrast = look.ContrastMultiplier * Math.Clamp(1 + (TargetContrast - std) * 0.8, 0.85, 1.20) - (lift ? ShadowLiftContrast : 0);
        return look with
        {
            BrightnessOffset = Math.Clamp(brightness, MinBrightnessOffset, MaxBrightnessOffset),
            ContrastMultiplier = Math.Clamp(contrast, 0.75, 1.35),
            SaturationMultiplier = Math.Clamp(look.SaturationMultiplier, 0.5, 1.6),
            RedShadowShift = Math.Clamp(look.RedShadowShift - bias * 0.3, -0.3, 0.3),
            BlueHighlightShift = Math.Clamp(look.BlueHighlightShift + bias * 0.3, -0.3, 0.3),
        };
    }
}
