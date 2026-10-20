using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// v16.7.0 앵커 사진 경량 비전 특징 — 색감(밝기·채도·온도·녹/청/분홍 비율·색 다양도) + 구도(가로/세로 · 중앙 대비).
///   객체 검출은 하지 않는다(외부 모델 0) — 색 분포가 장면(잔디·하늘/물·실내 조명·야간·알록달록 놀이공간)을 대리 표현.
/// </summary>
/// <param name="Brightness">평균 밝기 0~1.</param>
/// <param name="Saturation">평균 채도 0~1.</param>
/// <param name="Warmth">색온도 -1(차가움)~+1(따뜻함) — (R-B) 평균.</param>
/// <param name="GreenRatio">녹색 우세 픽셀 비율 0~1(잔디·숲).</param>
/// <param name="BlueRatio">청색 우세 픽셀 비율 0~1(하늘·물).</param>
/// <param name="PinkRatio">분홍/자주 픽셀 비율 0~1(파스텔 실내·솜사탕).</param>
/// <param name="Colorfulness">12 색상 버킷 중 유의미하게 쓰인 비율 0~1(알록달록함).</param>
/// <param name="Portrait">세로 구도 여부.</param>
/// <param name="CenterFocus">중앙 영역 밝기 - 외곽 밝기(피사체 집중도 근사 · -1~1).</param>
public readonly record struct PhotoFeatures(
    double Brightness, double Saturation, double Warmth, double GreenRatio, double BlueRatio,
    double PinkRatio, double Colorfulness, bool Portrait, double CenterFocus)
{
    private const int HueBuckets = 12;

    /// <summary>
    /// 0xRRGGBB 픽셀 배열(행 우선 · w×h) → 특징. 할당 0(스택 버킷) · 단일 패스. 빈 입력이면 중립값.
    /// </summary>
    public static PhotoFeatures FromRgb(ReadOnlySpan<int> rgb, int width, int height)
    {
        if (rgb.IsEmpty || width <= 0 || height <= 0 || rgb.Length < width * height)
            return new(0.5, 0.3, 0, 0, 0, 0, 0, height > width, 0);

        Span<int> hues = stackalloc int[HueBuckets];
        double sumL = 0, sumS = 0, sumW = 0, cSum = 0, eSum = 0;
        int green = 0, blue = 0, pink = 0, colored = 0, cN = 0, eN = 0;
        int x0 = width / 4, x1 = width - width / 4, y0 = height / 4, y1 = height - height / 4;
        var n = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = rgb[y * width + x];
                double r = ((p >> 16) & 0xFF) / 255.0, g = ((p >> 8) & 0xFF) / 255.0, b = (p & 0xFF) / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                var l = 0.299 * r + 0.587 * g + 0.114 * b;
                var s = max <= 0 ? 0 : (max - min) / max;
                sumL += l; sumS += s; sumW += r - b;
                if (x >= x0 && x < x1 && y >= y0 && y < y1) { cSum += l; cN++; } else { eSum += l; eN++; }

                if (s < 0.18 || max < 0.12) continue;   // 무채색·암부는 색상 통계 제외
                colored++;
                var h = Hue(r, g, b, max, min);
                hues[Math.Min(HueBuckets - 1, (int)(h / 30.0))]++;
                if (h is >= 75 and < 165) green++;
                else if (h is >= 180 and < 255) blue++;
                else if (h is >= 290 or < 10 && l > 0.55) pink++;
            }
        }

        var used = 0;
        var thresh = Math.Max(1, colored / 25);   // 버킷 점유 4% 이상만 "쓰인 색"
        for (var i = 0; i < HueBuckets; i++) if (hues[i] >= thresh && colored > 0) used++;

        return new(
            sumL / n, sumS / n, Math.Clamp(sumW / n * 2.5, -1, 1),
            (double)green / n, (double)blue / n, (double)pink / n,
            (double)used / HueBuckets, height > width,
            Math.Clamp((cN == 0 ? 0 : cSum / cN) - (eN == 0 ? 0 : eSum / eN), -1, 1));
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        var d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return h < 0 ? h + 360 : h;
    }
}

/// <summary>v16.7.0 테마 추천 결과 — 키·점수·근거.</summary>
public sealed record ThemeMatch(string Key, string Emoji, string Title, double Score, string Reason);

/// <summary>
/// [PromptFlowStudio] v16.7.0 💡 비전 기반 아기 사진 ↔ 테마 스마트 매처 — 순수 BCL 경량 색감/구도 인덱서(외부 모델·NuGet 0).
///   ▸ 앵커 사진 → <see cref="PhotoFeatures"/>(32×32 다운샘플) → 백과사전 전 테마의 시네마틱 조명·카테고리·Shot 장면 키워드와 친화도 스코어.
///   ▸ 상위 3개(카테고리 다양성 보장 — 같은 카테고리는 1개씩) 반환. 디코드는 <c>#if WINDOWS</c> System.Drawing, 모바일/실패 시 빈 목록(칩 비노출).
///   ▸ 같은 경로 재요청은 마지막 결과 캐시(파일 수정 시각 비교) — 렌더마다 재디코드 0.
/// </summary>
public sealed class SmartThemeMatcherService
{
    private const int SampleSize = 32;
    private readonly BabyThemeDatabase _db;
    private readonly object _gate = new();
    private string? _lastPath;
    private DateTime _lastStamp;
    private IReadOnlyList<ThemeMatch> _last = Array.Empty<ThemeMatch>();

    public SmartThemeMatcherService(BabyThemeDatabase db) => _db = db;

    /// <summary>앵커 이미지 파일 → 상위 <paramref name="top"/> 추천(디코드 불가/모바일이면 빈 목록).</summary>
    public IReadOnlyList<ThemeMatch> RecommendForImage(string? path, int top = 3)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return Array.Empty<ThemeMatch>();
        DateTime stamp;
        try { stamp = System.IO.File.GetLastWriteTimeUtc(path); } catch { return Array.Empty<ThemeMatch>(); }
        lock (_gate)
            if (string.Equals(_lastPath, path, StringComparison.OrdinalIgnoreCase) && _lastStamp == stamp) return _last;

        var result = TryExtract(path, out var f) ? Recommend(f, top) : Array.Empty<ThemeMatch>();
        lock (_gate) { _lastPath = path; _lastStamp = stamp; _last = result; }
        return result;
    }

    /// <summary>특징 → 상위 <paramref name="top"/> 추천(카테고리당 1개 · 점수 내림차순 · 동점은 키 사전순).</summary>
    public IReadOnlyList<ThemeMatch> Recommend(in PhotoFeatures f, int top = 3)
    {
        top = Math.Clamp(top, 1, 10);
        var ranked = new List<(BabyThemeDef T, double S, string Why)>();
        foreach (var cat in _db.Categories)
            foreach (var t in cat.Themes)
            {
                var (s, why) = Score(t, f);
                ranked.Add((t, s, why));
            }
        ranked.Sort(static (a, b) => b.S != a.S ? b.S.CompareTo(a.S) : string.CompareOrdinal(a.T.Key, b.T.Key));

        var outp = new List<ThemeMatch>(top);
        var usedCats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (t, s, why) in ranked)
        {
            if (!usedCats.Add(t.CategoryKey)) continue;
            outp.Add(new ThemeMatch(t.Key, t.Emoji, t.Title, Math.Round(s, 3), why));
            if (outp.Count == top) break;
        }
        return outp;
    }

    // 테마 친화도 = 조명 적합 + 장면 키워드 적합 + 모션 강도 적합(채도↑ = 활동적).
    private static (double Score, string Why) Score(BabyThemeDef t, in PhotoFeatures f)
    {
        var c = t.Cinematics;
        double light = c.Lighting switch
        {
            ThemeLighting.GoldenHour => Pos(f.Warmth) * 0.7 + Mid(f.Brightness, 0.55) * 0.3,
            ThemeLighting.StudioSoft => (1 - f.Saturation) * 0.5 + Mid(f.Brightness, 0.65) * 0.3 + f.PinkRatio * 2 * 0.2,
            ThemeLighting.NaturalDaylight => f.Brightness * 0.5 + (f.GreenRatio + f.BlueRatio) * 0.5,
            ThemeLighting.MoonlightBlue => (1 - f.Brightness) * 0.6 + Pos(-f.Warmth) * 0.4,
            ThemeLighting.WarmTungsten => Pos(f.Warmth) * 0.6 + (1 - f.Brightness) * 0.4,
            _ => f.Colorfulness * 0.6 + f.Saturation * 0.4,
        };

        double scene = 0;
        string? why = null;
        if (f.GreenRatio > 0.12 && HasAny(t, "grass", "park", "garden", "forest", "meadow", "tree"))
        { scene += f.GreenRatio * 2.2; why = "🌿 녹지 색감"; }
        if (f.BlueRatio > 0.12 && HasAny(t, "sea", "beach", "water", "pool", "sky", "fountain", "splash"))
        { scene += f.BlueRatio * 2.2; why ??= "💧 하늘·물빛 색감"; }
        if (f.Colorfulness > 0.45 && HasAny(t, "colorful", "rainbow", "ball pit", "carousel", "parade", "balloon", "pastel"))
        { scene += f.Colorfulness * 1.2; why ??= "🎨 알록달록한 색감"; }
        if (f.Brightness < 0.32 && HasAny(t, "moon", "night", "sleep", "star", "dim"))
        { scene += (1 - f.Brightness) * 1.0; why ??= "🌙 어두운 톤"; }
        if (f.PinkRatio > 0.08 && HasAny(t, "pink", "pastel", "cotton candy", "flower"))
        { scene += f.PinkRatio * 2.0; why ??= "🌸 핑크·파스텔 톤"; }
        if (f.CenterFocus > 0.08 && HasAny(t, "close-up", "macro", "portrait"))
        { scene += f.CenterFocus; why ??= "🎯 중앙 피사체 구도"; }

        var energy = f.Saturation * 0.6 + f.Colorfulness * 0.4;           // 0~1
        var want = (c.MotionIntensity - ThemeCinematics.MinIntensity) / 2.0;   // 0~1
        var motion = 1 - Math.Abs(energy - want);

        return (light * 0.45 + scene * 0.4 + motion * 0.15, why ?? c.LightingLabel + " 조명 톤");
    }

    private static double Pos(double v) => Math.Clamp(v, 0, 1);
    private static double Mid(double v, double center) => 1 - Math.Min(1, Math.Abs(v - center) * 2);

    private static bool HasAny(BabyThemeDef t, params string[] keys)
    {
        for (var i = 0; i < t.Beats.Count; i++)
        {
            var s = t.Beats[i].Shot.AsSpan();
            for (var k = 0; k < keys.Length; k++)
                if (s.IndexOf(keys[k], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>이미지 파일 → 특징(데스크톱 System.Drawing · 32×32 샘플). 모바일/손상 파일은 false.</summary>
    public static bool TryExtract(string? path, out PhotoFeatures features)
    {
        features = default;
#if WINDOWS
        try
        {
            using var bmp = new System.Drawing.Bitmap(path!);
            int w = Math.Max(1, bmp.Width), h = Math.Max(1, bmp.Height);
            int sw = Math.Min(w, SampleSize), sh = Math.Min(h, SampleSize);
            Span<int> rgb = stackalloc int[SampleSize * SampleSize];
            for (var y = 0; y < sh; y++)
                for (var x = 0; x < sw; x++)
                {
                    var px = bmp.GetPixel(x * w / sw, y * h / sh);
                    rgb[y * sw + x] = (px.R << 16) | (px.G << 8) | px.B;
                }
            // 구도(세로/가로)는 원본 비율로 판정.
            var f = PhotoFeatures.FromRgb(rgb[..(sw * sh)], sw, sh);
            features = f with { Portrait = h > w };
            return true;
        }
        catch { return false; }   // 손상/미지원 포맷 — Graceful(칩 비노출)
#else
        _ = path;
        return false;
#endif
    }
}
