using System;

namespace PromptFlowStudio.Services.Export;

/// <summary>HTS HUD 3대 테마 프리셋.</summary>
public enum HtsHudTheme
{
    /// <summary>OLED 딥블랙 — StockSuite 기본(초저조도 · 네온 그린/레드 캔들).</summary>
    OledBlack = 0,
    /// <summary>네이비 퀀트 — 전통 HTS 스타일(딥네이비 · 10단계 호가창).</summary>
    NavyQuant = 1,
    /// <summary>모던 미니멀 — 핀테크 스타일(심플 라인 차트 · 볼륨 바).</summary>
    ModernMinimal = 2,
}

/// <summary>가독성 진단 등급.</summary>
public enum HtsReadability { Unknown = 0, Optimal = 1, Caution = 2 }

/// <summary>조도 검사 결과 — 실효 배경 휘도 · 대비율 · 뱃지 · 권장 Dimmer(%).</summary>
public sealed record HtsLuminanceVerdict(HtsReadability Level, double EffectiveLuma, double ContrastRatio, int RecommendedDimmer, string Badge)
{
    public string CssClass => Level switch { HtsReadability.Optimal => "ok", HtsReadability.Caution => "warn", _ => "na" };
}

/// <summary>
/// [PromptFlowStudio] HTS HUD 테마 사양 · 조도(Luminance) 가독성 판정 — 순수 BCL · 할당 최소(정적 문자열 테이블).
///   ▸ 실효 배경 휘도 = 영상 중앙 평균 휘도 × (1 − Dimmer) — 테마 패널 텍스트 휘도와 WCAG 대비율 산출.
///   ▸ 대비율 ≥ 테마 임계(기본 4.5:1) 이면 🟢 최적, 미만이면 🟡 주의 + 임계를 만족하는 최소 Dimmer(%) 권장(10% 단위 · 하한 30%).
/// </summary>
public static class HtsHudThemes
{
    public static readonly HtsHudTheme[] All = [HtsHudTheme.OledBlack, HtsHudTheme.NavyQuant, HtsHudTheme.ModernMinimal];
    public const double MinContrast = 4.5;
    public const int CautionDimmerFloor = 30;

    public static string CssClass(HtsHudTheme t) => t switch
    {
        HtsHudTheme.NavyQuant => "hts-t-navy",
        HtsHudTheme.ModernMinimal => "hts-t-mini",
        _ => "hts-t-oled",
    };

    public static string Label(HtsHudTheme t) => t switch
    {
        HtsHudTheme.NavyQuant => "네이비 퀀트",
        HtsHudTheme.ModernMinimal => "모던 미니멀",
        _ => "OLED 딥블랙",
    };

    /// <summary>호가창 단계 수(네이비 퀀트 10단 · 그 외 5단 압축).</summary>
    public static int BookDepth(HtsHudTheme t) => t == HtsHudTheme.NavyQuant ? 10 : 5;

    /// <summary>캔들(OLED·네이비) vs 라인+볼륨(미니멀).</summary>
    public static bool UsesLineChart(HtsHudTheme t) => t == HtsHudTheme.ModernMinimal;

    /// <summary>테마 패널 텍스트 상대휘도(WCAG) — 대비 계산 기준.</summary>
    public static double TextLuminance(HtsHudTheme t) => t switch
    {
        HtsHudTheme.NavyQuant => 0.78,      // #dfe6f0 계열
        HtsHudTheme.ModernMinimal => 0.90,  // #f2f4f6
        _ => 0.86,                          // 네온 화이트
    };

    /// <summary>문자열 → 테마(대소문자 무시 · 별칭 oled/navy/minimal · 숫자) — 미상은 OLED.</summary>
    public static HtsHudTheme Parse(string? s)
    {
        var v = s?.Trim();
        if (string.IsNullOrEmpty(v)) return HtsHudTheme.OledBlack;
        if (string.Equals(v, "navy", StringComparison.OrdinalIgnoreCase)) return HtsHudTheme.NavyQuant;
        if (string.Equals(v, "minimal", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "mini", StringComparison.OrdinalIgnoreCase)) return HtsHudTheme.ModernMinimal;
        return Enum.TryParse<HtsHudTheme>(v, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : HtsHudTheme.OledBlack;
    }

    /// <summary>
    /// 조도 판정 — <paramref name="centerLuma"/>(0~1, null = 측정 불가) · <paramref name="dimmerPercent"/>(0~100).
    /// 실효 배경 = luma×(1−dim)(반투명 HUD 아래 비치는 영상) → 테마 텍스트 대비율.
    /// </summary>
    public static HtsLuminanceVerdict Evaluate(double? centerLuma, int dimmerPercent, HtsHudTheme theme)
    {
        if (centerLuma is not { } l || double.IsNaN(l))
            return new(HtsReadability.Unknown, 0, 0, dimmerPercent, "⚪ 조도 측정 불가 (로컬 영상·ffmpeg 필요)");
        var luma = Math.Clamp(l, 0d, 1d);
        var ratio = Contrast(luma, dimmerPercent, theme, out var eff);
        if (ratio >= MinContrast)
            return new(HtsReadability.Optimal, eff, ratio, dimmerPercent, "🟢 차트 가독성 최적 (대비 충분)");

        var rec = CautionDimmerFloor;
        while (rec < 90 && Contrast(luma, rec, theme, out _) < MinContrast) rec += 10;
        if (rec <= dimmerPercent) rec = Math.Min(90, dimmerPercent + 10);
        return new(HtsReadability.Caution, eff, ratio, rec,
            "🟡 주의: 배경이 다소 밝음 (Dimmer " + rec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "% 권장)");
    }

    private static double Contrast(double luma, int dimmerPercent, HtsHudTheme theme, out double effective)
    {
        var dim = Math.Clamp(dimmerPercent, 0, 100) / 100d;
        effective = luma * (1 - dim);
        // 영상 휘도(감마 인코딩 YAVG) → 선형 근사(γ 2.2)
        var lin = Math.Pow(effective, 2.2);
        var fg = TextLuminance(theme);
        return (Math.Max(fg, lin) + 0.05) / (Math.Min(fg, lin) + 0.05);
    }
}
