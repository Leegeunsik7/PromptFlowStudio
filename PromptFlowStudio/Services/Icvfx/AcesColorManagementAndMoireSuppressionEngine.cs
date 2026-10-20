using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace PromptFlowStudio.Services.Icvfx;

/// <summary>색 공간(OCIO ACES studio-config 명칭 대응).</summary>
public enum ColorSpace : byte
{
    ACES2065_1, ACEScg, ACEScct, ACEScc, LinearRec709, SRgbTexture, Rec1886Rec709, LinearRec2020, Rec2100Pq, SRgbDisplayAces, Rec709DisplayAces,
}

/// <summary>
/// 3×3 행렬(행 우선 · 열벡터 곱 c' = M·c) — 색 공간 원색 변환.
/// </summary>
public readonly struct Mat3
{
    public readonly float M00, M01, M02, M10, M11, M12, M20, M21, M22;
    public Mat3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
    { M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22; }

    public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    public Vector3 Mul(Vector3 c) => new(M00 * c.X + M01 * c.Y + M02 * c.Z, M10 * c.X + M11 * c.Y + M12 * c.Z, M20 * c.X + M21 * c.Y + M22 * c.Z);
    public static Mat3 operator *(Mat3 a, Mat3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public Mat3 Inverse()
    {
        double a = M00, b = M01, c = M02, d = M10, e = M11, f = M12, g = M20, h = M21, i = M22;
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        var det = a * A + b * B + c * C;
        var inv = 1.0 / det;
        return new Mat3((float)(A * inv), (float)(-(b * i - c * h) * inv), (float)((b * f - c * e) * inv),
            (float)(B * inv), (float)((a * i - c * g) * inv), (float)(-(a * f - c * d) * inv),
            (float)(C * inv), (float)(-(a * h - b * g) * inv), (float)((a * e - b * d) * inv));
    }

    public float this[int r, int c] => (r * 3 + c) switch { 0 => M00, 1 => M01, 2 => M02, 3 => M10, 4 => M11, 5 => M12, 6 => M20, 7 => M21, _ => M22 };
}

/// <summary>무아레 분석 결과 — 예측 맥놀이 주파수(사이클/카메라 픽셀) · 위험도 · 권장 조치.</summary>
public readonly record struct MoireAnalysis(float ImagedPitchPx, float BeatFx, float BeatPeriodPx, float Risk, float DefocusBlurPx, float SafeDistanceM, string Advice);

/// <summary>
/// [PromptFlow Studio v16.59.0] 🎨 ACES/OCIO 색상 관리 &amp; 카메라 무아레 억제 엔진(Pure BCL · 픽셀 경로 무할당).
///   ▸ 원색: Rec.709/sRGB · Rec.2020 · ACES AP0(2065-1) · AP1(ACEScg) — XYZ 경유 + Bradford D65↔D60 적응(행렬 자동 유도).
///   ▸ 전달 함수: sRGB · BT.1886(γ2.4) · ACEScct · ACEScc · SMPTE ST 2084 PQ(LED 월 피크 니트 스케일).
///   ▸ 출력 변환: ACES 1.x SDR 근사(RRT_SAT → Hill RRT+ODT 피팅 → ODT_SAT) — sRGB/Rec.709 디스플레이. LED 월은 장면 선형 × 니트 → Rec.2100 PQ(톤매핑 없음).
///   ▸ OCIO: ACES studio-config 명칭/별칭 조회 · 프로세서(src→dst) · 33³ 3D LUT 베이크(사면체 보간) · Resolve/OCIO 호환 .cube 입출력.
///   ▸ 무아레: LED 피치 · 거리 · 초점거리 · 센서 피치 → 결상 피치 · 맥놀이 주파수/위험도/피사계 심도 블러 예측 · 콘텐츠 측 저역 프리필터 ·
///     촬영본 측 위상 이동 상쇄(예측 주파수 ±20% 정밀 탐색 → 국소 직교 복조(cos/sin) 진폭·위상 추정 → 역위상 합성 감산) · 카메라 촬영 시뮬레이터.
/// </summary>
public sealed class AcesColorManagementAndMoireSuppressionEngine
{
    public const int LutSize = 33;

    // ── 원색 → XYZ ──
    public static readonly Mat3 Rec709ToXyz = new(0.4123908f, 0.3575843f, 0.1804808f, 0.2126390f, 0.7151687f, 0.0721923f, 0.0193308f, 0.1191948f, 0.9505322f);
    public static readonly Mat3 Rec2020ToXyz = new(0.6369580f, 0.1446169f, 0.1688810f, 0.2627002f, 0.6779981f, 0.0593017f, 0f, 0.0280727f, 1.0609851f);
    public static readonly Mat3 Ap1ToXyz = new(0.6624541811f, 0.1340042065f, 0.1561876870f, 0.2722287168f, 0.6740817658f, 0.0536895174f, -0.0055746495f, 0.0040607335f, 1.0103391003f);
    public static readonly Mat3 Ap0ToXyz = new(0.9525523959f, 0f, 0.0000936786f, 0.3439664498f, 0.7281660966f, -0.0721325464f, 0f, 0f, 1.0088251844f);
    private static readonly Mat3 Bradford = new(0.8951f, 0.2664f, -0.1614f, -0.7502f, 1.7135f, 0.0367f, 0.0389f, -0.0685f, 1.0296f);
    private static readonly Vector3 WhiteD65 = new(0.95047f, 1f, 1.08883f), WhiteD60 = new(0.95264607f, 1f, 1.00882518f);
    private static readonly Mat3 RrtSat = new(0.970889f, 0.026963f, 0.002148f, 0.010889f, 0.986963f, 0.002148f, 0.010889f, 0.026963f, 0.962148f);
    private static readonly Mat3 OdtSat = new(0.949056f, 0.047185f, 0.003759f, 0.019056f, 0.977185f, 0.003759f, 0.019056f, 0.047185f, 0.933759f);

    public static readonly Mat3 D65ToD60 = Adapt(WhiteD65, WhiteD60), D60ToD65 = Adapt(WhiteD60, WhiteD65);
    public static readonly Mat3 Rec709ToAp1 = Ap1ToXyz.Inverse() * D65ToD60 * Rec709ToXyz;
    public static readonly Mat3 Ap1ToRec709 = Rec709ToAp1.Inverse();
    public static readonly Mat3 Ap0ToAp1 = Ap1ToXyz.Inverse() * Ap0ToXyz;
    public static readonly Mat3 Ap1ToAp0 = Ap0ToAp1.Inverse();
    public static readonly Mat3 Rec2020ToAp1 = Ap1ToXyz.Inverse() * D65ToD60 * Rec2020ToXyz;
    public static readonly Mat3 Ap1ToRec2020 = Rec2020ToAp1.Inverse();

    private static Mat3 Adapt(Vector3 src, Vector3 dst)
    {
        var s = Bradford.Mul(src); var d = Bradford.Mul(dst);
        var scale = new Mat3(d.X / s.X, 0, 0, 0, d.Y / s.Y, 0, 0, 0, d.Z / s.Z);
        return Bradford.Inverse() * scale * Bradford;
    }

    // ── 전달 함수 ──
    public static float SrgbEncode(float x) => x <= 0.0031308f ? 12.92f * MathF.Max(x, 0f) : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    public static float SrgbDecode(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    public static float Bt1886Encode(float x) => MathF.Pow(Math.Clamp(x, 0f, 1f), 1f / 2.4f);
    public static float Bt1886Decode(float v) => MathF.Pow(Math.Clamp(v, 0f, 1f), 2.4f);
    public static float AcesCctEncode(float x) => x <= 0.0078125f ? 10.5402377416545f * x + 0.0729055341958355f : (MathF.Log2(x) + 9.72f) / 17.52f;
    public static float AcesCctDecode(float v) => v <= 0.155251141552511f ? (v - 0.0729055341958355f) / 10.5402377416545f : MathF.Pow(2f, v * 17.52f - 9.72f);
    public static float AcesCcEncode(float x) => x <= 0f ? (MathF.Log2(MathF.Pow(2f, -16f)) + 9.72f) / 17.52f : x < MathF.Pow(2f, -15f) ? (MathF.Log2(MathF.Pow(2f, -16f) + x * 0.5f) + 9.72f) / 17.52f : (MathF.Log2(x) + 9.72f) / 17.52f;
    public static float AcesCcDecode(float v) => v < (9.72f - 15f) / 17.52f ? (MathF.Pow(2f, v * 17.52f - 9.72f) - MathF.Pow(2f, -16f)) * 2f : MathF.Pow(2f, v * 17.52f - 9.72f);

    private const float PqM1 = 2610f / 16384f, PqM2 = 2523f / 4096f * 128f, PqC1 = 3424f / 4096f, PqC2 = 2413f / 4096f * 32f, PqC3 = 2392f / 4096f * 32f;
    /// <summary>절대 휘도(cd/m²) → PQ 코드값(0..1).</summary>
    public static float PqEncode(float nits) { var y = MathF.Pow(Math.Clamp(nits / 10000f, 0f, 1f), PqM1); return MathF.Pow((PqC1 + PqC2 * y) / (1f + PqC3 * y), PqM2); }
    public static float PqDecode(float v) { var p = MathF.Pow(Math.Clamp(v, 0f, 1f), 1f / PqM2); return 10000f * MathF.Pow(MathF.Max(p - PqC1, 0f) / (PqC2 - PqC3 * p), 1f / PqM1); }

    private static float RrtOdtFit(float v) => (v * (v + 0.0245786f) - 0.000090537f) / (v * (0.983729f * v + 0.4329510f) + 0.238081f);

    /// <summary>ACEScg → ACES 1.x SDR 디스플레이 선형 Rec.709(0..1) 근사.</summary>
    public static Vector3 AcesSdrOutput(Vector3 ap1)
    {
        var v = RrtSat.Mul(ap1);
        v = new Vector3(RrtOdtFit(v.X), RrtOdtFit(v.Y), RrtOdtFit(v.Z));
        v = OdtSat.Mul(v);
        return Vector3.Clamp(Ap1ToRec709.Mul(v), Vector3.Zero, Vector3.One);
    }

    /// <summary>OCIO 명칭/별칭 → 색 공간.</summary>
    public static bool TryParse(string name, out ColorSpace cs)
    {
        var n = name.Trim().ToLowerInvariant();
        cs = n switch
        {
            "aces2065-1" or "aces - aces2065-1" or "aces" or "ap0" or "lin_ap0" => ColorSpace.ACES2065_1,
            "acescg" or "aces - acescg" or "lin_ap1" or "ap1" => ColorSpace.ACEScg,
            "acescct" or "aces - acescct" => ColorSpace.ACEScct,
            "acescc" or "aces - acescc" => ColorSpace.ACEScc,
            "linear rec.709 (srgb)" or "lin_rec709" or "lin_srgb" or "utility - linear - srgb" => ColorSpace.LinearRec709,
            "srgb - texture" or "srgb_tx" or "srgb" or "utility - srgb - texture" => ColorSpace.SRgbTexture,
            "rec.1886 rec.709 - display" or "rec709" or "rec.709" => ColorSpace.Rec1886Rec709,
            "linear rec.2020" or "lin_rec2020" => ColorSpace.LinearRec2020,
            "rec.2100-pq - display" or "rec2100-pq" or "pq" or "st2084" => ColorSpace.Rec2100Pq,
            "srgb - display" or "srgb display (aces)" => ColorSpace.SRgbDisplayAces,
            "rec.709 - display (aces)" => ColorSpace.Rec709DisplayAces,
            _ => (ColorSpace)255,
        };
        return (byte)cs != 255;
    }

    public static string OcioName(ColorSpace cs) => cs switch
    {
        ColorSpace.ACES2065_1 => "ACES2065-1", ColorSpace.ACEScg => "ACEScg", ColorSpace.ACEScct => "ACEScct", ColorSpace.ACEScc => "ACEScc",
        ColorSpace.LinearRec709 => "Linear Rec.709 (sRGB)", ColorSpace.SRgbTexture => "sRGB - Texture", ColorSpace.Rec1886Rec709 => "Rec.1886 Rec.709 - Display",
        ColorSpace.LinearRec2020 => "Linear Rec.2020", ColorSpace.Rec2100Pq => "Rec.2100-PQ - Display", ColorSpace.SRgbDisplayAces => "sRGB - Display (ACES SDR)",
        _ => "Rec.709 - Display (ACES SDR)",
    };

    /// <summary>LED 월 피크 휘도(니트) — 장면 선형 1.0 = 18% 회색 기준 노출 스케일과 결합.</summary>
    public float WallPeakNits { get; set; } = 1500f;
    /// <summary>장면 선형 1.0 이 매핑되는 니트(LED 노출).</summary>
    public float WallNitsPerUnit { get; set; } = 400f;
    /// <summary>ACES 파이프라인 활성(끄면 sRGB 직접 클램프 — 비교용).</summary>
    public bool AcesEnabled { get; set; } = true;
    /// <summary>무아레 상쇄 강도(0..1).</summary>
    public float MoireStrength { get; set; } = 1f;
    /// <summary>콘텐츠 측 저역 프리필터(0..1 · LED 콘텐츠 고주파 감쇠).</summary>
    public float ContentPrefilter { get; set; } = 0.3f;

    private long _converted, _moireFrames;
    private float _lastMoireBefore, _lastMoireAfter, _lastBeat;
    private double _moireMs;

    public event Action? OnChange;

    public long PixelsConverted => Interlocked.Read(ref _converted);
    public long MoireFrames => Interlocked.Read(ref _moireFrames);
    public float LastMoireContrastBefore => Volatile.Read(ref _lastMoireBefore);
    public float LastMoireContrastAfter => Volatile.Read(ref _lastMoireAfter);
    public float LastBeatFrequency => Volatile.Read(ref _lastBeat);
    public double LastMoireMs => Volatile.Read(ref _moireMs);
    public string? LastAction { get; private set; }

    /// <summary>임의 색 공간 → ACEScg 선형.</summary>
    public Vector3 ToAcesCg(Vector3 c, ColorSpace src) => src switch
    {
        ColorSpace.ACEScg => c,
        ColorSpace.ACES2065_1 => Ap0ToAp1.Mul(c),
        ColorSpace.ACEScct => new Vector3(AcesCctDecode(c.X), AcesCctDecode(c.Y), AcesCctDecode(c.Z)),
        ColorSpace.ACEScc => new Vector3(AcesCcDecode(c.X), AcesCcDecode(c.Y), AcesCcDecode(c.Z)),
        ColorSpace.LinearRec709 => Rec709ToAp1.Mul(c),
        ColorSpace.SRgbTexture => Rec709ToAp1.Mul(new Vector3(SrgbDecode(c.X), SrgbDecode(c.Y), SrgbDecode(c.Z))),
        ColorSpace.Rec1886Rec709 => Rec709ToAp1.Mul(new Vector3(Bt1886Decode(c.X), Bt1886Decode(c.Y), Bt1886Decode(c.Z))),
        ColorSpace.LinearRec2020 => Rec2020ToAp1.Mul(c),
        ColorSpace.Rec2100Pq => Rec2020ToAp1.Mul(new Vector3(PqDecode(c.X), PqDecode(c.Y), PqDecode(c.Z)) / MathF.Max(1f, WallNitsPerUnit)),
        _ => throw new NotSupportedException("디스플레이 출력 변환은 역변환 미지원: " + OcioName(src)),
    };

    /// <summary>ACEScg 선형 → 임의 색 공간.</summary>
    public Vector3 FromAcesCg(Vector3 c, ColorSpace dst)
    {
        switch (dst)
        {
            case ColorSpace.ACEScg: return c;
            case ColorSpace.ACES2065_1: return Ap1ToAp0.Mul(c);
            case ColorSpace.ACEScct: return new Vector3(AcesCctEncode(c.X), AcesCctEncode(c.Y), AcesCctEncode(c.Z));
            case ColorSpace.ACEScc: return new Vector3(AcesCcEncode(c.X), AcesCcEncode(c.Y), AcesCcEncode(c.Z));
            case ColorSpace.LinearRec709: return Ap1ToRec709.Mul(c);
            case ColorSpace.SRgbTexture: { var l = Ap1ToRec709.Mul(c); return new Vector3(SrgbEncode(Math.Clamp(l.X, 0f, 1f)), SrgbEncode(Math.Clamp(l.Y, 0f, 1f)), SrgbEncode(Math.Clamp(l.Z, 0f, 1f))); }
            case ColorSpace.Rec1886Rec709: { var l = Ap1ToRec709.Mul(c); return new Vector3(Bt1886Encode(l.X), Bt1886Encode(l.Y), Bt1886Encode(l.Z)); }
            case ColorSpace.LinearRec2020: return Ap1ToRec2020.Mul(c);
            case ColorSpace.Rec2100Pq:
            {
                var l = Ap1ToRec2020.Mul(c) * WallNitsPerUnit;
                var peak = WallPeakNits;
                return new Vector3(PqEncode(Math.Clamp(l.X, 0f, peak)), PqEncode(Math.Clamp(l.Y, 0f, peak)), PqEncode(Math.Clamp(l.Z, 0f, peak)));
            }
            case ColorSpace.SRgbDisplayAces:
            {
                if (!AcesEnabled) goto case ColorSpace.SRgbTexture;
                var l = AcesSdrOutput(c);
                return new Vector3(SrgbEncode(l.X), SrgbEncode(l.Y), SrgbEncode(l.Z));
            }
            default:
            {
                if (!AcesEnabled) goto case ColorSpace.Rec1886Rec709;
                var l = AcesSdrOutput(c);
                return new Vector3(Bt1886Encode(l.X), Bt1886Encode(l.Y), Bt1886Encode(l.Z));
            }
        }
    }

    public Vector3 Convert(Vector3 c, ColorSpace src, ColorSpace dst) => src == dst ? c : FromAcesCg(ToAcesCg(c, src), dst);

    /// <summary>선형 ACEScg float3 버퍼 → 8bit 출력(무할당).</summary>
    public void Encode(ReadOnlySpan<float> ap1, Span<byte> rgb, ColorSpace dst)
    {
        var n = Math.Min(ap1.Length / 3, rgb.Length / 3);
        for (var i = 0; i < n; i++)
        {
            var o = FromAcesCg(new Vector3(ap1[i * 3], ap1[i * 3 + 1], ap1[i * 3 + 2]), dst);
            rgb[i * 3] = (byte)Math.Clamp((int)(o.X * 255f + 0.5f), 0, 255);
            rgb[i * 3 + 1] = (byte)Math.Clamp((int)(o.Y * 255f + 0.5f), 0, 255);
            rgb[i * 3 + 2] = (byte)Math.Clamp((int)(o.Z * 255f + 0.5f), 0, 255);
        }
        Interlocked.Add(ref _converted, n);
    }

    // ══════════════════════ 3D LUT · .cube ══════════════════════

    /// <summary>src→dst 프로세서를 size³ LUT 로 베이크(입력 도메인 0..1 · ACEScct 등 로그 입력 권장).</summary>
    public float[] BakeLut(ColorSpace src, ColorSpace dst, int size = LutSize)
    {
        var lut = new float[size * size * size * 3];
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    var o = Convert(new Vector3(r, g, b) / (size - 1), src, dst);
                    var k = ((b * size + g) * size + r) * 3;
                    lut[k] = o.X; lut[k + 1] = o.Y; lut[k + 2] = o.Z;
                }
        return lut;
    }

    /// <summary>사면체 보간 LUT 적용(무할당 · 입력 0..1 클램프).</summary>
    public static Vector3 ApplyLut(ReadOnlySpan<float> lut, int size, Vector3 c)
    {
        var f = Vector3.Clamp(c, Vector3.Zero, Vector3.One) * (size - 1);
        int r0 = Math.Min((int)f.X, size - 2), g0 = Math.Min((int)f.Y, size - 2), b0 = Math.Min((int)f.Z, size - 2);
        float fr = f.X - r0, fg = f.Y - g0, fb = f.Z - b0;
        var c000 = Lt(lut, size, r0, g0, b0); var c111 = Lt(lut, size, r0 + 1, g0 + 1, b0 + 1);
        if (fr > fg)
        {
            if (fg > fb) { var c100 = Lt(lut, size, r0 + 1, g0, b0); var c110 = Lt(lut, size, r0 + 1, g0 + 1, b0); return c000 + (c100 - c000) * fr + (c110 - c100) * fg + (c111 - c110) * fb; }
            if (fr > fb) { var c100 = Lt(lut, size, r0 + 1, g0, b0); var c101 = Lt(lut, size, r0 + 1, g0, b0 + 1); return c000 + (c100 - c000) * fr + (c101 - c100) * fb + (c111 - c101) * fg; }
            { var c001 = Lt(lut, size, r0, g0, b0 + 1); var c101 = Lt(lut, size, r0 + 1, g0, b0 + 1); return c000 + (c001 - c000) * fb + (c101 - c001) * fr + (c111 - c101) * fg; }
        }
        if (fb > fg) { var c001 = Lt(lut, size, r0, g0, b0 + 1); var c011 = Lt(lut, size, r0, g0 + 1, b0 + 1); return c000 + (c001 - c000) * fb + (c011 - c001) * fg + (c111 - c011) * fr; }
        if (fb > fr) { var c010 = Lt(lut, size, r0, g0 + 1, b0); var c011 = Lt(lut, size, r0, g0 + 1, b0 + 1); return c000 + (c010 - c000) * fg + (c011 - c010) * fb + (c111 - c011) * fr; }
        { var c010 = Lt(lut, size, r0, g0 + 1, b0); var c110 = Lt(lut, size, r0 + 1, g0 + 1, b0); return c000 + (c010 - c000) * fg + (c110 - c010) * fr + (c111 - c110) * fb; }
    }

    private static Vector3 Lt(ReadOnlySpan<float> lut, int size, int r, int g, int b) { var k = ((b * size + g) * size + r) * 3; return new Vector3(lut[k], lut[k + 1], lut[k + 2]); }

    /// <summary>Resolve/OCIO 호환 .cube 저장(R 최속 순서).</summary>
    public static void WriteCube(string path, ReadOnlySpan<float> lut, int size, string title)
    {
        var sb = new StringBuilder(size * size * size * 30);
        sb.Append("TITLE \"").Append(title.Replace('"', '\'')).Append("\"\nLUT_3D_SIZE ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n");
        for (var i = 0; i < size * size * size; i++)
            sb.Append(lut[i * 3].ToString("0.000000", CultureInfo.InvariantCulture)).Append(' ')
              .Append(lut[i * 3 + 1].ToString("0.000000", CultureInfo.InvariantCulture)).Append(' ')
              .Append(lut[i * 3 + 2].ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>.cube 로드(3D · DOMAIN 0..1 가정). 반환 = (LUT, size) 또는 null.</summary>
    public static (float[] Lut, int Size)? ReadCube(string path)
    {
        var size = 0;
        float[]? lut = null;
        var k = 0;
        Span<Range> parts = stackalloc Range[4];
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#') continue;
            if (line.StartsWith("LUT_3D_SIZE"))
            {
                if (!int.TryParse(line[11..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 2 || size > 256) return null;
                lut = new float[size * size * size * 3];
                continue;
            }
            if (char.IsLetter(line[0])) continue;
            if (lut is null || k >= lut.Length) return null;
            if (line.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries) != 3) return null;
            for (var c = 0; c < 3; c++)
                if (!float.TryParse(line[parts[c]], NumberStyles.Float, CultureInfo.InvariantCulture, out lut[k++])) return null;
        }
        return lut is not null && k == lut.Length ? (lut, size) : null;
    }

    // ══════════════════════ 무아레 ══════════════════════

    /// <summary>
    /// 무아레 예측 — LED 피치(mm) · 카메라-월 거리(m) · 초점거리(mm) · 센서 픽셀 피치(µm) · 초점 거리/조리개(피사계 심도 블러).
    /// </summary>
    public static MoireAnalysis Analyze(float pitchMm, float distanceM, float focalMm, float sensorPixelUm, float focusDistanceM, float fStop)
    {
        var imagedUm = pitchMm * 1000f * focalMm / MathF.Max(1f, distanceM * 1000f - focalMm);
        var imagedPx = imagedUm / MathF.Max(0.1f, sensorPixelUm);
        var f = 1f / MathF.Max(1e-3f, imagedPx);                 // 사이클/픽셀
        var alias = MathF.Abs(f - MathF.Round(f));               // 샘플링 앨리어스(맥놀이)
        // 피사계 심도 착란원(월 위치 · 센서 µm) → 픽셀
        var s = focusDistanceM * 1000f; var d = distanceM * 1000f; var fl = focalMm;
        var coc = MathF.Abs(fl * fl * (d - s) / (fStop * d * MathF.Max(1f, s - fl)));   // mm
        var blurPx = coc * 1000f / MathF.Max(0.1f, sensorPixelUm);
        // 위험: LED 격자가 센서에서 분해(결상 피치 < ~6px)되고 블러가 피치보다 작을 때
        var resolve = Math.Clamp((6f - imagedPx) / 4f + 1f, 0f, 1f) * (imagedPx > 1.05f || alias > 0.02f ? 1f : 0.4f);
        var blurAtten = MathF.Exp(-MathF.Pow(blurPx / MathF.Max(0.5f, imagedPx), 2f) * 2f);
        var risk = Math.Clamp(resolve * blurAtten, 0f, 1f);
        var safe = pitchMm * 1000f * focalMm / (sensorPixelUm * 0.7f) / 1000f + focalMm / 1000f;   // 결상 피치 0.7px 미만 거리
        var advice = risk < 0.25f ? "안전 — 조치 불필요"
            : blurPx < imagedPx ? "월에서 초점 이탈(조리개 개방/초점 전경) 권장 · 위상 상쇄 필터 적용"
            : "카메라-월 거리 확대 또는 각도 변경 권장 · 위상 상쇄 필터 적용";
        return new MoireAnalysis(imagedPx, alias, alias > 1e-4f ? 1f / alias : float.PositiveInfinity, risk, blurPx, safe, advice);
    }

    /// <summary>
    /// 카메라 촬영 시뮬레이터(무할당) — LED 월(피치 pitchPx 센서 픽셀 · 발광 충전율 fill · 콘텐츠 색) 을 센서 픽셀 면적 적분(ss×ss 슈퍼샘플)으로 촬영.
    /// content(u,v) 는 월 좌표 0..1 콘텐츠 휘도(선형) — out 은 단채널 휘도.
    /// </summary>
    public static void SimulateCapture(Span<float> dst, int w, int h, float pitchPx, float fill, float angleDeg, Func<float, float, float>? content = null, int ss = 4)
    {
        if (angleDeg == 0f) { CaptureAxisAligned(dst, w, h, pitchPx, fill, content); return; }
        var ca = MathF.Cos(angleDeg * MathF.PI / 180f); var sa = MathF.Sin(angleDeg * MathF.PI / 180f);
        var inv = 1f / pitchPx;
        var half = fill * 0.5f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var acc = 0f;
                for (var j = 0; j < ss; j++)
                    for (var i = 0; i < ss; i++)
                    {
                        float sx = x + (i + 0.5f) / ss, sy = y + (j + 0.5f) / ss;
                        float wx = (sx * ca + sy * sa) * inv, wy = (-sx * sa + sy * ca) * inv;
                        float fx = wx - MathF.Floor(wx) - 0.5f, fy = wy - MathF.Floor(wy) - 0.5f;
                        if (MathF.Abs(fx) <= half && MathF.Abs(fy) <= half)
                            acc += content is null ? 1f : content((MathF.Floor(wx) + 0.5f) * pitchPx / w, (MathF.Floor(wy) + 0.5f) * pitchPx / h);
                    }
                dst[y * w + x] = acc / (ss * ss * fill * fill);
            }
    }

    /// <summary>축 정렬 월 — 픽셀 정사각형 × LED 발광 정사각형 겹침 면적 해석 적분(앨리어싱 없는 정확 시뮬레이션).</summary>
    private static void CaptureAxisAligned(Span<float> dst, int w, int h, float p, float fill, Func<float, float, float>? content)
    {
        var half = fill * p * 0.5f;
        var norm = 1f / (fill * fill);
        for (var y = 0; y < h; y++)
        {
            int ky0 = (int)MathF.Floor((y - half) / p) - 1, ky1 = (int)MathF.Ceiling((y + 1 + half) / p) + 1;
            for (var x = 0; x < w; x++)
            {
                int kx0 = (int)MathF.Floor((x - half) / p) - 1, kx1 = (int)MathF.Ceiling((x + 1 + half) / p) + 1;
                var acc = 0f;
                for (var ky = ky0; ky <= ky1; ky++)
                {
                    var cy = (ky + 0.5f) * p;
                    var oy = MathF.Max(0f, MathF.Min(y + 1f, cy + half) - MathF.Max(y, cy - half));
                    if (oy <= 0f) continue;
                    for (var kx = kx0; kx <= kx1; kx++)
                    {
                        var cx = (kx + 0.5f) * p;
                        var ox = MathF.Max(0f, MathF.Min(x + 1f, cx + half) - MathF.Max(x, cx - half));
                        if (ox <= 0f) continue;
                        acc += ox * oy * (content is null ? 1f : content(cx / w, cy / h));
                    }
                }
                dst[y * w + x] = acc * norm / (p * p);
            }
        }
    }

    private float[] _cosRow = Array.Empty<float>(), _sinRow = Array.Empty<float>(), _tmpA = Array.Empty<float>(), _tmpB = Array.Empty<float>(), _accA = Array.Empty<float>(), _accB = Array.Empty<float>();

    private float[] _tmpV = Array.Empty<float>(), _accV = Array.Empty<float>();
    private double[] _mc = Array.Empty<double>(), _ms = Array.Empty<double>(), _mcc = Array.Empty<double>(), _mcs = Array.Empty<double>(), _mss = Array.Empty<double>();

    private void EnsureScratch(int n, int len)
    {
        if (_tmpA.Length < n)
        {
            _cosRow = new float[n]; _sinRow = new float[n]; _tmpA = new float[n]; _tmpB = new float[n]; _accA = new float[n]; _accB = new float[n];
            _tmpV = new float[n]; _accV = new float[n];
        }
        if (_mc.Length < len) { _mc = new double[len]; _ms = new double[len]; _mcc = new double[len]; _mcs = new double[len]; _mss = new double[len]; }
    }

    /// <summary>분리형 박스 블러(반경 r · 무할당 · src→dst · tmp 스크래치).</summary>
    private static void BoxBlur(ReadOnlySpan<float> src, Span<float> dst, Span<float> tmp, int w, int h, int r)
    {
        // 경계 절단 창(실제 표본 수로 정규화 · 패딩 왜곡 없음)
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var s = 0f;
            for (var k = 0; k <= Math.Min(r, w - 1); k++) s += src[row + k];
            for (var x = 0; x < w; x++)
            {
                var lo = Math.Max(0, x - r); var hi = Math.Min(w - 1, x + r);
                tmp[row + x] = s / (hi - lo + 1);
                if (x + r + 1 < w) s += src[row + x + r + 1];
                if (x - r >= 0) s -= src[row + x - r];
            }
        }
        for (var x = 0; x < w; x++)
        {
            var s = 0f;
            for (var k = 0; k <= Math.Min(r, h - 1); k++) s += tmp[k * w + x];
            for (var y = 0; y < h; y++)
            {
                var lo = Math.Max(0, y - r); var hi = Math.Min(h - 1, y + r);
                dst[y * w + x] = s / (hi - lo + 1);
                if (y + r + 1 < h) s += tmp[(y + r + 1) * w + x];
                if (y - r >= 0) s -= tmp[(y - r) * w + x];
            }
        }
    }

    /// <summary>방향별 맥놀이 에너지(행/열 평균 프로파일 직교 복조).</summary>
    private static float ProfileEnergy(ReadOnlySpan<float> profile, float f)
    {
        double a = 0, b = 0;
        for (var i = 0; i < profile.Length; i++) { var ph = 2 * Math.PI * f * i; a += profile[i] * Math.Cos(ph); b += profile[i] * Math.Sin(ph); }
        return (float)((a * a + b * b) / (profile.Length * (double)profile.Length));
    }

    /// <summary>
    /// 위상 이동 무아레 상쇄(인-플레이스 · 무할당 · 스크래치는 해상도 증가 시만 확장).
    /// fxHint/fyHint = 예측 맥놀이 주파수(사이클/픽셀 · 0 = 해당 축 탐색 생략). 반환 = 상쇄 후/전 대비 비.
    /// </summary>
    public float SuppressMoire(Span<float> img, int w, int h, float fxHint, float fyHint)
    {
        var t0 = Stopwatch.GetTimestamp();
        var n = w * h;
        EnsureScratch(Math.Max(n, Math.Max(w, h)), Math.Max(w, h));
        var before = Contrast(img, w, h);
        // 축별 정밀 탐색 → 국소 복조 → 감산 (x 축 · y 축 순차)
        for (var axis = 0; axis < 2; axis++)
        {
            var hint = axis == 0 ? fxHint : fyHint;
            if (hint <= 1e-4f) continue;
            var len = axis == 0 ? w : h;
            var prof = _cosRow.AsSpan(0, len);
            prof.Clear();
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++) prof[axis == 0 ? x : y] += img[y * w + x];
            var mean = 0f;
            for (var i = 0; i < len; i++) { prof[i] /= axis == 0 ? h : w; mean += prof[i]; }
            mean /= len;
            for (var i = 0; i < len; i++) prof[i] -= mean;
            float best = hint, bestE = -1f;
            for (var k = -20; k <= 20; k++)
            {
                var f = hint * (1f + k * 0.01f);
                if (f <= 0f || f >= 0.5f) continue;
                var e = ProfileEnergy(prof, f);
                if (e > bestE) { bestE = e; best = f; }
            }
            if (axis == 0) Volatile.Write(ref _lastBeat, best);
            // 국소 최소제곱 직교 복조: 창 평균 [1, cos, sin] 회귀 → 진폭 a · b(위상) → 역위상 감산(경계에서도 편향 없음)
            var r = Math.Clamp((int)MathF.Round(1f / best), 2, Math.Max(2, len / 4));
            for (var harm = 1; harm <= 2; harm++)
            {
                var fh = best * harm;
                if (fh >= 0.5f) break;
                var ia = _tmpA.AsSpan(0, n); var ib = _tmpB.AsSpan(0, n); var iv = _tmpV.AsSpan(0, n);
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var ph = MathF.Tau * fh * (axis == 0 ? x : y);
                        var v = img[y * w + x];
                        var k = y * w + x;
                        ia[k] = v * MathF.Cos(ph); ib[k] = v * MathF.Sin(ph); iv[k] = v;
                    }
                var la = _accA.AsSpan(0, n); var lb = _accB.AsSpan(0, n); var lv = _accV.AsSpan(0, n);
                var scratch = _sinRow.AsSpan(0, n);
                BoxBlur(ia, la, scratch, w, h, r);
                BoxBlur(ib, lb, scratch, w, h, r);
                BoxBlur(iv, lv, scratch, w, h, r);
                for (var i = 0; i < len; i++)
                {
                    int lo = Math.Max(0, i - r), hi = Math.Min(len - 1, i + r);
                    double sc = 0, ss = 0, scc = 0, scs = 0, sss = 0;
                    for (var q = lo; q <= hi; q++)
                    {
                        var ph = 2 * Math.PI * fh * q;
                        double c = Math.Cos(ph), sn = Math.Sin(ph);
                        sc += c; ss += sn; scc += c * c; scs += c * sn; sss += sn * sn;
                    }
                    var cntq = hi - lo + 1;
                    _mc[i] = sc / cntq; _ms[i] = ss / cntq; _mcc[i] = scc / cntq; _mcs[i] = scs / cntq; _mss[i] = sss / cntq;
                }
                var s = MoireStrength;
                var axisBefore = Contrast(img, w, h);
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var q = axis == 0 ? x : y;
                        var k = y * w + x;
                        double mc = _mc[q], ms = _ms[q], mcc = _mcc[q], mcs = _mcs[q], mss = _mss[q];
                        double v0 = lv[k], v1 = la[k], v2 = lb[k];
                        // [1 mc ms; mc mcc mcs; ms mcs mss] [m a b]ᵀ = [v0 v1 v2]ᵀ — 크래머
                        var det = 1 * (mcc * mss - mcs * mcs) - mc * (mc * mss - mcs * ms) + ms * (mc * mcs - mcc * ms);
                        if (Math.Abs(det) < 1e-9) { ia[k] = 0; continue; }
                        var a = (1 * (v1 * mss - mcs * v2) - v0 * (mc * mss - mcs * ms) + ms * (mc * v2 - v1 * ms)) / det;
                        var b = (1 * (mcc * v2 - v1 * mcs) - mc * (mc * v2 - v1 * ms) + v0 * (mc * mcs - mcc * ms)) / det;
                        var ph = 2 * Math.PI * fh * q;
                        ia[k] = (float)(a * Math.Cos(ph) + b * Math.Sin(ph));
                    }
                for (var k = 0; k < n; k++) img[k] -= s * ia[k];
                if (Contrast(img, w, h) > axisBefore)   // 악화 방지: 해당 축 상쇄 되돌림
                    for (var k = 0; k < n; k++) img[k] += s * ia[k];
            }
        }
        var after = Contrast(img, w, h);
        Volatile.Write(ref _lastMoireBefore, before);
        Volatile.Write(ref _lastMoireAfter, after);
        Volatile.Write(ref _moireMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        if ((Interlocked.Increment(ref _moireFrames) & 7) == 1) { LastAction = "〰 무아레 " + before.ToString("0.000", CultureInfo.InvariantCulture) + " → " + after.ToString("0.000", CultureInfo.InvariantCulture); Raise(); }
        return before > 1e-6f ? after / before : 1f;
    }

    /// <summary>변동 계수(표준편차/평균) — 균일 월 촬영본의 무아레 대비.</summary>
    public static float Contrast(ReadOnlySpan<float> img, int w, int h)
    {
        double s = 0, s2 = 0;
        var n = w * h;
        for (var i = 0; i < n; i++) { s += img[i]; s2 += img[i] * img[i]; }
        var m = s / n;
        return m <= 1e-9 ? 0f : (float)(Math.Sqrt(Math.Max(0, s2 / n - m * m)) / m);
    }

    /// <summary>콘텐츠 측 저역 프리필터(LED 선형 버퍼 3채널 · 3탭 가중 · 강도 ContentPrefilter · 무할당).</summary>
    public void PrefilterContent(Span<float> rgb, int w, int h)
    {
        var a = Math.Clamp(ContentPrefilter, 0f, 1f) * 0.25f;
        if (a <= 0f) return;
        if (PromptFlowStudio.Services.Synergy.EngineHyperAccelerationOptimizer.Enabled) { PromptFlowStudio.Services.Synergy.EngineHyperAccelerationOptimizer.Prefilter3Tap(rgb, w, h, a); return; } // v16.62.0 행 우선 SIMD(비트 동일)
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < h; y++)
            {
                var prev = rgb[(y * w) * 3 + c];
                for (var x = 0; x < w; x++)
                {
                    var k = (y * w + x) * 3 + c;
                    var cur = rgb[k];
                    var next = x + 1 < w ? rgb[k + 3] : cur;
                    rgb[k] = cur * (1 - 2 * a) + (prev + next) * a;
                    prev = cur;
                }
            }
            for (var x = 0; x < w; x++)
            {
                var prev = rgb[x * 3 + c];
                for (var y = 0; y < h; y++)
                {
                    var k = (y * w + x) * 3 + c;
                    var cur = rgb[k];
                    var next = y + 1 < h ? rgb[k + w * 3] : cur;
                    rgb[k] = cur * (1 - 2 * a) + (prev + next) * a;
                    prev = cur;
                }
            }
        }
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Icvfx.Color.OnChange", ex); }
    }
}
