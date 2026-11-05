using System;

namespace PromptFlowStudio.Services.Export;

/// <summary>🎨 HTS 시네마틱 4대 핀테크 LUT 프로필.</summary>
public enum LutProfile { None, PureOledBlack, MatrixNeon, QuantMono, WarmAmber }

/// <summary>
/// 🎨 HTS 4대 LUT 컬러 그레이더 — 프로필 → FFmpeg <c>eq</c> 필터(번인) · CSS <c>filter</c>(캔버스 실시간 미리보기).
///   ▸ 문자열은 전부 정적 상수(할당 0) · 수치 InvariantCulture 고정 표기.
///   ▸ CSS 는 근사치(채널별 gamma 미지원 → sepia/hue-rotate 로 근사).
/// </summary>
public static class ColorGradingService
{
    public const string PureOledBlackEq = "eq=contrast=1.2:brightness=-0.08:saturation=1.1";
    public const string MatrixNeonEq = "eq=contrast=1.15:saturation=1.35:gamma_g=1.1";
    public const string QuantMonoEq = "eq=saturation=0.5:contrast=1.1";
    public const string WarmAmberEq = "eq=gamma_r=1.15:gamma_b=0.85:contrast=1.05";

    private static readonly LutProfile[] s_all = [LutProfile.None, LutProfile.PureOledBlack, LutProfile.MatrixNeon, LutProfile.QuantMono, LutProfile.WarmAmber];

    /// <summary>드롭다운 열거 순서(None 포함).</summary>
    public static ReadOnlySpan<LutProfile> All => s_all;

    /// <summary>FFmpeg <c>eq=...</c> 필터 — None 이면 null.</summary>
    public static string? BuildFfmpegEqFilter(LutProfile p) => p switch
    {
        LutProfile.PureOledBlack => PureOledBlackEq,
        LutProfile.MatrixNeon => MatrixNeonEq,
        LutProfile.QuantMono => QuantMonoEq,
        LutProfile.WarmAmber => WarmAmberEq,
        _ => null,
    };

    /// <summary>기존 <c>-vf</c> 체인 끝에 LUT 결합(색감은 스케일·보간 후 · 워터마크는 원색 유지 위해 호출측 순서 책임).</summary>
    public static string? AppendTo(string? chain, LutProfile p)
    {
        var eq = BuildFfmpegEqFilter(p);
        if (eq is null) return chain;
        return string.IsNullOrEmpty(chain) ? eq : chain + "," + eq;
    }

    /// <summary>캔버스 CSS filter — None 이면 "none".</summary>
    public static string CssFilter(LutProfile p) => p switch
    {
        LutProfile.PureOledBlack => "contrast(1.2) brightness(0.92) saturate(1.1)",
        LutProfile.MatrixNeon => "contrast(1.15) saturate(1.35) hue-rotate(-12deg)",
        LutProfile.QuantMono => "saturate(0.5) contrast(1.1)",
        LutProfile.WarmAmber => "sepia(0.3) contrast(1.05) saturate(1.05)",
        _ => "none",
    };

    public static string Label(LutProfile p) => p switch
    {
        LutProfile.PureOledBlack => "Pure OLED Black",
        LutProfile.MatrixNeon => "Matrix Neon",
        LutProfile.QuantMono => "Quant Mono",
        LutProfile.WarmAmber => "Warm Amber",
        _ => "원본(LUT 없음)",
    };
}
