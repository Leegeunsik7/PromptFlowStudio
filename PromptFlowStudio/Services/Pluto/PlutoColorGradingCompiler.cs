using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎨 Pluto 시네마틱 색보정 컴파일러 — 룩 프리셋(<see cref="GoldenHourWarm"/> · <see cref="VintageFilm16mm"/> · <see cref="NaturalPastel"/>) 선택 +
/// <see cref="PlutoColorGradeProfile"/> → FFmpeg <c>eq=…,colorbalance=…[,vignette=…]</c>(InvariantCulture · 소수 3자리 결정론).
/// </summary>
public static class PlutoColorGradingCompiler
{
    public const string GoldenHourWarmName = "GoldenHourWarm", VintageFilm16mmName = "VintageFilm16mm", NaturalPastelName = "NaturalPastel";
    public const string VignetteFilter = "vignette=angle=PI/5";
    /// <summary>섀도 블루 = 섀도 레드의 보색 반대(−0.8배) — 웜 섀도 시 청색 섀도를 눌러 색 순도 유지.</summary>
    public const double ShadowBlueCounterRatio = -0.8;

    public static readonly PlutoColorGradeProfile GoldenHourWarm = new(GoldenHourWarmName, 0.0, 1.06, 1.12, 0.05, -0.03, true);
    public static readonly PlutoColorGradeProfile VintageFilm16mm = new(VintageFilm16mmName, 0.02, 0.94, 0.82, 0.04, -0.06, true);
    public static readonly PlutoColorGradeProfile NaturalPastel = new(NaturalPastelName, 0.02, 0.96, 0.92, 0.0, 0.02, false);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>룩 이름(대소문자 무시 · 한국어/공백 별칭 허용) → 프리셋. 미지 → null.</summary>
    public static PlutoColorGradeProfile? TryGetPreset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        if (Has(n, GoldenHourWarmName) || Has(n, "golden") || Has(n, "골든") || Has(n, "노을")) return GoldenHourWarm;
        if (Has(n, VintageFilm16mmName) || Has(n, "vintage") || Has(n, "16mm") || Has(n, "빈티지") || Has(n, "필름")) return VintageFilm16mm;
        if (Has(n, NaturalPastelName) || Has(n, "pastel") || Has(n, "파스텔")) return NaturalPastel;
        return null;
    }

    private static bool Has(string s, string token) => s.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>🛡️ 후보 룩이 오답 노트에서 차단(프리셋명 또는 별칭 토큰)됐는지.</summary>
    public static bool IsSuppressed(PlutoColorGradeProfile look, IPlutoMistakeNotebookService? notebook)
    {
        if (notebook is null || look is null) return false;
        IReadOnlySet<string> blocked;
        try { blocked = notebook.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ColorGrade] notebook: " + ex.Message); return false; }   // [부분 구제] 차단 없음
        if (blocked is null || blocked.Count == 0) return false;
        foreach (var t in blocked)
            if (!string.IsNullOrWhiteSpace(t) && ReferenceEquals(TryGetPreset(t), look)) return true;
        return false;
    }

    /// <summary>선호 룩 해석 — 미지/차단 → <see cref="NaturalPastel"/>.</summary>
    public static PlutoColorGradeProfile ResolveLook(string? preferredLook, IPlutoMistakeNotebookService? notebook)
    {
        var look = TryGetPreset(preferredLook) ?? NaturalPastel;
        return ReferenceEquals(look, NaturalPastel) || !IsSuppressed(look, notebook) ? look : NaturalPastel;
    }

    /// <summary>
    /// 🎬 활성 바이럴 태그 · 디렉터 랭크 → 후보 룩 이름 — 노을/골든/웜 태그 → GoldenHourWarm · 빈티지/필름/레트로 → VintageFilm16mm ·
    /// 태그 무신호 시 디렉터 레벨 ≥ 3 → VintageFilm16mm · ≥ 2 → GoldenHourWarm · 그 외 NaturalPastel.
    /// </summary>
    public static string SelectLook(IReadOnlyList<string>? viralTags, int directorLevel)
    {
        if (viralTags is not null)
            for (var i = 0; i < viralTags.Count; i++)
            {
                var t = viralTags[i];
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (Has(t, "golden") || Has(t, "sunset") || Has(t, "warm") || Has(t, "노을") || Has(t, "골든") || Has(t, "따뜻")) return GoldenHourWarmName;
                if (Has(t, "vintage") || Has(t, "film") || Has(t, "retro") || Has(t, "빈티지") || Has(t, "필름") || Has(t, "레트로")) return VintageFilm16mmName;
                if (Has(t, "pastel") || Has(t, "파스텔")) return NaturalPastelName;
            }
        return directorLevel >= 3 ? VintageFilm16mmName : directorLevel >= 2 ? GoldenHourWarmName : NaturalPastelName;
    }

    /// <summary>
    /// 🎞️ 프로파일 → FFmpeg 필터 세그먼트(예: <c>eq=brightness=0.040:contrast=1.060:saturation=1.120,colorbalance=rs=0.050:bs=-0.040:bh=-0.030,vignette=angle=PI/5</c>).
    /// 비유한 값 → 중립 · FFmpeg 허용 범위 클램프.
    /// </summary>
    public static string Compile(PlutoColorGradeProfile profile)
    {
        profile ??= NaturalPastel;
        var sb = new StringBuilder(128);
        sb.Append("eq=brightness=").Append(F(Clamp(profile.BrightnessOffset, -1, 1, 0)))
          .Append(":contrast=").Append(F(Clamp(profile.ContrastMultiplier, 0, 2, 1)))
          .Append(":saturation=").Append(F(Clamp(profile.SaturationMultiplier, 0, 3, 1)));
        var rs = Clamp(profile.RedShadowShift, -1, 1, 0);
        sb.Append(",colorbalance=rs=").Append(F(rs))
          .Append(":bs=").Append(F(Math.Clamp(rs * ShadowBlueCounterRatio, -1, 1)))
          .Append(":bh=").Append(F(Clamp(profile.BlueHighlightShift, -1, 1, 0)));
        if (profile.EnableVignette) sb.Append(',').Append(VignetteFilter);
        return sb.ToString();
    }

    private static double Clamp(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>소수 3자리 고정 · −0.000 → 0.000.</summary>
    private static string F(double v)
    {
        var r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
        return (r == 0 ? 0.0 : r).ToString("0.000", Inv);
    }
}
