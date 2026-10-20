using System;
using System.Globalization;
using System.IO;
using PromptFlowStudio.Services.Media;   // BabyPngWriter (순수 BCL RGBA PNG)

namespace PromptFlowStudio.Services.Mobile;

/// <summary>알파 매트 — 폭/높이 + 픽셀당 1바이트 알파(0 투명 배경 ~ 255 불투명 전경).</summary>
public sealed record AlphaMatte(int Width, int Height, byte[] Alpha);

/// <summary>
/// [PromptFlowStudio] v4.0.0 자율 발상 — 모바일 온디바이스 배경 분리 헬퍼(Task 5 · 순수 BCL + FFmpegKit 필터).
///   ▸ 갤럭시 S26 촬영 사진의 배경을 <b>코너 샘플로 추정한 배경색과의 색거리</b>로 <b>1초 알파 매트</b> 분리(순수 BCL · GPU 불필요).
///   ▸ 결과를 <b>RGBA 컷아웃 PNG</b>(<see cref="BabyPngWriter"/>)로 즉시 저장 → 버추얼 스튜디오/기념 카드 합성에 재사용.
///   ▸ 고품질 경로는 FFmpegKit <c>chromakey</c>/<c>colorkey</c> 필터(<see cref="BuildChromakeyFilter"/>)로 모바일 GPU 가속.
/// </summary>
public static class MobileBackgroundSeparator
{
    /// <summary>4개 코너 픽셀 평균으로 배경색을 추정한다.</summary>
    public static (int R, int G, int B) EstimateBackgroundColor(byte[] rgb, int w, int h)
    {
        if (rgb is null || w <= 0 || h <= 0) return (0, 0, 0);
        int[] xs = { 0, w - 1, 0, w - 1 }, ys = { 0, 0, h - 1, h - 1 };
        long r = 0, g = 0, b = 0; var n = 0;
        for (var i = 0; i < 4; i++)
        {
            var idx = (ys[i] * w + xs[i]) * 3;
            if (idx + 2 >= rgb.Length) continue;
            r += rgb[idx]; g += rgb[idx + 1]; b += rgb[idx + 2]; n++;
        }
        if (n == 0) return (0, 0, 0);
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>
    /// 배경색과의 색거리로 알파 매트를 만든다(순수 BCL).
    ///   거리 &lt; tolerance → 배경(알파 0), 거리 &gt; tolerance+soft → 전경(알파 255), 그 사이는 소프트 램프(안티엘리어싱).
    /// </summary>
    public static AlphaMatte BuildAlphaMatte(byte[] rgb, int w, int h, (int R, int G, int B) bg, int tolerance = 60, int soft = 30)
    {
        var alpha = new byte[Math.Max(0, w * h)];
        var tol = Math.Max(1, tolerance);
        var sf = Math.Max(1, soft);
        for (var i = 0; i < w * h; i++)
        {
            var p = i * 3;
            if (p + 2 >= rgb.Length) { alpha[i] = 255; continue; }
            double dr = rgb[p] - bg.R, dg = rgb[p + 1] - bg.G, db = rgb[p + 2] - bg.B;
            var dist = Math.Sqrt(dr * dr + dg * dg + db * db);
            if (dist <= tol) alpha[i] = 0;
            else if (dist >= tol + sf) alpha[i] = 255;
            else alpha[i] = (byte)Math.Clamp((dist - tol) / sf * 255.0, 0, 255);
        }
        return new AlphaMatte(w, h, alpha);
    }

    /// <summary>RGB(w×h×3) + 알파 매트 → RGBA(w×h×4).</summary>
    public static byte[] CompositeRgba(byte[] rgb, AlphaMatte matte)
    {
        var w = matte.Width; var h = matte.Height;
        var outp = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var s = i * 3; var d = i * 4;
            if (s + 2 < rgb.Length) { outp[d] = rgb[s]; outp[d + 1] = rgb[s + 1]; outp[d + 2] = rgb[s + 2]; }
            outp[d + 3] = i < matte.Alpha.Length ? matte.Alpha[i] : (byte)255;
        }
        return outp;
    }

    /// <summary>전경 픽셀 수(알파&gt;127).</summary>
    public static int ForegroundCount(AlphaMatte matte)
    {
        var c = 0;
        foreach (var a in matte.Alpha) if (a > 127) c++;
        return c;
    }

    /// <summary>배경 자동 분리 컷아웃을 RGBA PNG 로 저장한다(성공 여부).</summary>
    public static bool WriteCutoutPng(string path, byte[] rgb, int w, int h, int tolerance = 60)
    {
        try
        {
            var bg = EstimateBackgroundColor(rgb, w, h);
            var matte = BuildAlphaMatte(rgb, w, h, bg, tolerance);
            BabyPngWriter.WriteRgba(path, w, h, CompositeRgba(rgb, matte));
            return File.Exists(path);
        }
        catch (Exception ex) { Program.Log("MobileBgSeparator.WriteCutout", ex); return false; }
    }

    // ══════════════════════ FFmpegKit 고품질 경로(모바일 GPU) ══════════════════════

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>그린/블루 스크린 chromakey 알파 필터(FFmpegKit/CLI 공통).</summary>
    public static string BuildChromakeyFilter(string color = "0x00FF00", double similarity = 0.16, double blend = 0.05)
        => $"chromakey=color={color}:similarity={F(Math.Clamp(similarity, 0.01, 1.0))}:blend={F(Math.Clamp(blend, 0.0, 1.0))},format=rgba";

    /// <summary>추정 배경색 기반 colorkey 알파 필터(임의 단색 배경).</summary>
    public static string BuildColorkeyAlphaFilter((int R, int G, int B) bg, double similarity = 0.14)
    {
        var hex = $"0x{bg.R:X2}{bg.G:X2}{bg.B:X2}";
        return $"colorkey=color={hex}:similarity={F(Math.Clamp(similarity, 0.01, 1.0))}:blend=0.1,format=rgba";
    }
}
