using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.ColorScience;

public enum AcesSpace : byte { Aces2065_1, AcesCg, AcesCc, AcesCct, Rec709, Rec2020 }

/// <summary>[PromptFlow Studio v17.10.0] 3×3 색 행렬(행 우선 · float). 생성/역행렬은 double 로 계산 후 반올림.</summary>
public readonly struct ColorMatrix3
{
    public readonly float M00, M01, M02, M10, M11, M12, M20, M21, M22;

    public ColorMatrix3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
    {
        M00 = (float)m00; M01 = (float)m01; M02 = (float)m02;
        M10 = (float)m10; M11 = (float)m11; M12 = (float)m12;
        M20 = (float)m20; M21 = (float)m21; M22 = (float)m22;
    }

    public static ColorMatrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public float this[int r, int c] => (r * 3 + c) switch { 0 => M00, 1 => M01, 2 => M02, 3 => M10, 4 => M11, 5 => M12, 6 => M20, 7 => M21, _ => M22 };

    public static ColorMatrix3 operator *(in ColorMatrix3 a, in ColorMatrix3 b) => new(
        (double)a.M00 * b.M00 + (double)a.M01 * b.M10 + (double)a.M02 * b.M20, (double)a.M00 * b.M01 + (double)a.M01 * b.M11 + (double)a.M02 * b.M21, (double)a.M00 * b.M02 + (double)a.M01 * b.M12 + (double)a.M02 * b.M22,
        (double)a.M10 * b.M00 + (double)a.M11 * b.M10 + (double)a.M12 * b.M20, (double)a.M10 * b.M01 + (double)a.M11 * b.M11 + (double)a.M12 * b.M21, (double)a.M10 * b.M02 + (double)a.M11 * b.M12 + (double)a.M12 * b.M22,
        (double)a.M20 * b.M00 + (double)a.M21 * b.M10 + (double)a.M22 * b.M20, (double)a.M20 * b.M01 + (double)a.M21 * b.M11 + (double)a.M22 * b.M21, (double)a.M20 * b.M02 + (double)a.M21 * b.M12 + (double)a.M22 * b.M22);

    public ColorMatrix3 Inverse()
    {
        double a = M00, b = M01, c = M02, d = M10, e = M11, f = M12, g = M20, h = M21, i = M22;
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        var det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-12) return Identity;
        var k = 1.0 / det;
        return new(A * k, -(b * i - c * h) * k, (b * f - c * e) * k, B * k, (a * i - c * g) * k, -(a * f - c * d) * k, C * k, -(a * h - b * g) * k, (a * e - b * d) * k);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Transform(ref float r, ref float g, ref float b)
    {
        float x = r, y = g, z = b;
        r = M00 * x + M01 * y + M02 * z;
        g = M10 * x + M11 * y + M12 * z;
        b = M20 * x + M21 * y + M22 * z;
    }
}

/// <summary>
/// [PromptFlow Studio v17.10.0] ACES 색 수학(BCL 전용).
/// · 원색: AP0(ACES2065-1) · AP1(ACEScg/cc/cct) · Rec.709 · Rec.2020 → RGB↔XYZ 는 원색+백색점에서 유도, D60↔D65 는 Bradford CAT.
/// · ACEScc(S-2014-003) · ACEScct(S-2016-001) · BT.709 OETF · BT.1886 역(γ 2.4).
/// · ACES 2.0 톤스케일: Daniele Evo(피크 100 nit · 18% → 10 nit · 플레어 t₁ 0.04). 정식 ACES 2.0 은 JMh 공간 적용 — 여기서는 max(RGB) 노름 비율 보존 근사.
/// </summary>
public static class AcesColorMath
{
    public const float CcMin = (-16f + 9.72f) / 17.52f;
    public const float CctXBrk = 0.0078125f, CctYBrk = 0.155251141552511f, CctA = 10.5402377416545f, CctB = 0.0729055341958355f;

    private static readonly float TsM2, TsS2, TsG = 1.15f, TsT1 = 0.04f;

    static AcesColorMath()
    {
        // Daniele Evo · n = 100 nit (n_r = 100) → r_hit = 128.
        const double n = 100, nr = 100, g = 1.15, c = 0.18, cd = 10.013, wg = 0.14, t1 = 0.04, rHitMin = 128, rHitMax = 896;
        var rHit = rHitMin + (rHitMax - rHitMin) * (Math.Log(n / nr) / Math.Log(10000.0 / 100.0));
        var m0 = n / nr;
        var m1 = 0.5 * (m0 + Math.Sqrt(m0 * (m0 + 4 * t1)));
        var u = Math.Pow(rHit / m1 / (rHit / m1 + 1), g);
        var m = m1 / u;
        var wi = Math.Log(n / 100) / Math.Log(2);
        var ct = cd / nr * (1 + wi * wg);
        var gip = 0.5 * (ct + Math.Sqrt(ct * (ct + 4 * t1)));
        var q = Math.Pow(gip / m, 1 / g);
        var gipp2 = -(m1 * q) / (q - 1);
        var w2 = c / gipp2;
        TsS2 = (float)(w2 * m1);
        var u2 = Math.Pow(rHit / m1 / (rHit / m1 + w2), g);
        TsM2 = (float)(m1 / u2);
    }

    // ── 원색 → 행렬 ──────────────────────────────────────────

    private static void Primaries(AcesSpace s, out double rx, out double ry, out double gx, out double gy, out double bx, out double by, out double wx, out double wy)
    {
        switch (s)
        {
            case AcesSpace.Aces2065_1: rx = 0.7347; ry = 0.2653; gx = 0; gy = 1; bx = 0.0001; by = -0.077; wx = 0.32168; wy = 0.33767; break;
            case AcesSpace.Rec709: rx = 0.64; ry = 0.33; gx = 0.30; gy = 0.60; bx = 0.15; by = 0.06; wx = 0.3127; wy = 0.3290; break;
            case AcesSpace.Rec2020: rx = 0.708; ry = 0.292; gx = 0.170; gy = 0.797; bx = 0.131; by = 0.046; wx = 0.3127; wy = 0.3290; break;
            default: rx = 0.713; ry = 0.293; gx = 0.165; gy = 0.830; bx = 0.128; by = 0.044; wx = 0.32168; wy = 0.33767; break;
        }
    }

    public static void WhitePoint(AcesSpace s, out double wx, out double wy) => Primaries(s, out _, out _, out _, out _, out _, out _, out wx, out wy);

    /// <summary>선형 RGB → XYZ(Y_white = 1 · 자체 백색점).</summary>
    public static ColorMatrix3 RgbToXyz(AcesSpace s)
    {
        Primaries(s, out var rx, out var ry, out var gx, out var gy, out var bx, out var by, out var wx, out var wy);
        var p = new ColorMatrix3(rx / ry, gx / gy, bx / by, 1, 1, 1, (1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by);
        var inv = p.Inverse();
        double Xw = wx / wy, Zw = (1 - wx - wy) / wy;
        double sr = inv.M00 * Xw + inv.M01 + inv.M02 * Zw, sg = inv.M10 * Xw + inv.M11 + inv.M12 * Zw, sb = inv.M20 * Xw + inv.M21 + inv.M22 * Zw;
        return new(p.M00 * sr, p.M01 * sg, p.M02 * sb, p.M10 * sr, p.M11 * sg, p.M12 * sb, p.M20 * sr, p.M21 * sg, p.M22 * sb);
    }

    /// <summary>Bradford 색순응(XYZ · 백색점 xy → xy).</summary>
    public static ColorMatrix3 Bradford(double sx, double sy, double dx, double dy)
    {
        var ma = new ColorMatrix3(0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296);
        double sX = sx / sy, sZ = (1 - sx - sy) / sy, dX = dx / dy, dZ = (1 - dx - dy) / dy;
        double sr = ma.M00 * sX + ma.M01 + ma.M02 * sZ, sg = ma.M10 * sX + ma.M11 + ma.M12 * sZ, sb = ma.M20 * sX + ma.M21 + ma.M22 * sZ;
        double dr = ma.M00 * dX + ma.M01 + ma.M02 * dZ, dg = ma.M10 * dX + ma.M11 + ma.M12 * dZ, db = ma.M20 * dX + ma.M21 + ma.M22 * dZ;
        var d = new ColorMatrix3(dr / sr, 0, 0, 0, dg / sg, 0, 0, 0, db / sb);
        return ma.Inverse() * (d * ma);
    }

    /// <summary>선형 원색 간 변환(백색점이 다르면 Bradford).</summary>
    public static ColorMatrix3 LinearMatrix(AcesSpace from, AcesSpace to)
    {
        WhitePoint(from, out var fx, out var fy);
        WhitePoint(to, out var tx, out var ty);
        var m = RgbToXyz(from);
        if (Math.Abs(fx - tx) > 1e-9 || Math.Abs(fy - ty) > 1e-9) m = Bradford(fx, fy, tx, ty) * m;
        return RgbToXyz(to).Inverse() * m;
    }

    // ── 전달 함수 ───────────────────────────────────────────

    public static float AcesCcEncode(float lin)
    {
        if (lin <= 0f) return CcMin;
        if (lin < 3.0517578e-05f) return (MathF.Log2(1.5258789e-05f + lin * 0.5f) + 9.72f) / 17.52f;
        return (MathF.Log2(lin) + 9.72f) / 17.52f;
    }

    public static float AcesCcDecode(float cc)
    {
        if (cc < (9.72f - 15f) / 17.52f) return (MathF.Pow(2f, cc * 17.52f - 9.72f) - 1.5258789e-05f) * 2f;
        if (cc < (15.999120f + 9.72f) / 17.52f) return MathF.Pow(2f, cc * 17.52f - 9.72f);
        return 65504f;
    }

    public static float AcesCctEncode(float lin) => lin <= CctXBrk ? CctA * lin + CctB : (MathF.Log2(lin) + 9.72f) / 17.52f;
    public static float AcesCctDecode(float cct) => cct <= CctYBrk ? (cct - CctB) / CctA : MathF.Pow(2f, cct * 17.52f - 9.72f);

    public static float Bt709Oetf(float l) => l < 0.018f ? 4.5f * l : 1.099f * MathF.Pow(l, 0.45f) - 0.099f;
    public static float Bt709InverseOetf(float v) => v < 0.081f ? v / 4.5f : MathF.Pow((v + 0.099f) / 1.099f, 1f / 0.45f);
    public static float Bt1886Inverse(float l) => l <= 0f ? 0f : l >= 1f ? 1f : MathF.Pow(l, 1f / 2.4f);

    public static float Encode(AcesSpace s, float lin) => s switch
    {
        AcesSpace.AcesCc => AcesCcEncode(lin),
        AcesSpace.AcesCct => AcesCctEncode(lin),
        AcesSpace.Rec709 => Bt709Oetf(lin),
        _ => lin,
    };

    public static float Decode(AcesSpace s, float v) => s switch
    {
        AcesSpace.AcesCc => AcesCcDecode(v),
        AcesSpace.AcesCct => AcesCctDecode(v),
        AcesSpace.Rec709 => Bt709InverseOetf(v),
        _ => v,
    };

    public static AcesSpace LinearOf(AcesSpace s) => s is AcesSpace.AcesCc or AcesSpace.AcesCct ? AcesSpace.AcesCg : s;

    /// <summary>ACES 2.0 Daniele Evo 톤스케일(장면 선형 → 디스플레이 선형 · 1 = 100 nit).</summary>
    public static float Tonescale(float x)
    {
        if (x <= 0f) return 0f;
        var f = TsM2 * MathF.Pow(x / (x + TsS2), TsG);
        return MathF.Max(0f, f * f / (f + TsT1));
    }
}

/// <summary>[PromptFlow Studio v17.10.0] 평면(planar) RGB 3×3 행렬 SIMD(Vector256 → Vector128 → 스칼라 · 모든 경로 비트 동일 · FMA 미사용).</summary>
public static class AcesSimd
{
    public static void Apply(in ColorMatrix3 m, Span<float> r, Span<float> g, Span<float> b)
    {
        var n = r.Length;
        if (g.Length < n || b.Length < n) throw new ArgumentException("planar length mismatch");
        var i = 0;
        ref var rr = ref MemoryMarshal.GetReference(r);
        ref var gg = ref MemoryMarshal.GetReference(g);
        ref var bb = ref MemoryMarshal.GetReference(b);
        if (BitConverter.IsLittleEndian && Vector256.IsHardwareAccelerated && n >= Vector256<float>.Count)
        {
            Vector256<float> a00 = Vector256.Create(m.M00), a01 = Vector256.Create(m.M01), a02 = Vector256.Create(m.M02);
            Vector256<float> a10 = Vector256.Create(m.M10), a11 = Vector256.Create(m.M11), a12 = Vector256.Create(m.M12);
            Vector256<float> a20 = Vector256.Create(m.M20), a21 = Vector256.Create(m.M21), a22 = Vector256.Create(m.M22);
            for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                var x = Vector256.LoadUnsafe(ref rr, (nuint)i);
                var y = Vector256.LoadUnsafe(ref gg, (nuint)i);
                var z = Vector256.LoadUnsafe(ref bb, (nuint)i);
                (a00 * x + a01 * y + a02 * z).StoreUnsafe(ref rr, (nuint)i);
                (a10 * x + a11 * y + a12 * z).StoreUnsafe(ref gg, (nuint)i);
                (a20 * x + a21 * y + a22 * z).StoreUnsafe(ref bb, (nuint)i);
            }
        }
        if (BitConverter.IsLittleEndian && Vector128.IsHardwareAccelerated && n - i >= Vector128<float>.Count)
        {
            Vector128<float> a00 = Vector128.Create(m.M00), a01 = Vector128.Create(m.M01), a02 = Vector128.Create(m.M02);
            Vector128<float> a10 = Vector128.Create(m.M10), a11 = Vector128.Create(m.M11), a12 = Vector128.Create(m.M12);
            Vector128<float> a20 = Vector128.Create(m.M20), a21 = Vector128.Create(m.M21), a22 = Vector128.Create(m.M22);
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                var x = Vector128.LoadUnsafe(ref rr, (nuint)i);
                var y = Vector128.LoadUnsafe(ref gg, (nuint)i);
                var z = Vector128.LoadUnsafe(ref bb, (nuint)i);
                (a00 * x + a01 * y + a02 * z).StoreUnsafe(ref rr, (nuint)i);
                (a10 * x + a11 * y + a12 * z).StoreUnsafe(ref gg, (nuint)i);
                (a20 * x + a21 * y + a22 * z).StoreUnsafe(ref bb, (nuint)i);
            }
        }
        ApplyScalar(m, r.Slice(i), g.Slice(i, n - i), b.Slice(i, n - i));
    }

    public static void ApplyScalar(in ColorMatrix3 m, Span<float> r, Span<float> g, Span<float> b)
    {
        for (var i = 0; i < r.Length; i++)
        {
            float x = r[i], y = g[i], z = b[i];
            r[i] = m.M00 * x + m.M01 * y + m.M02 * z;
            g[i] = m.M10 * x + m.M11 * y + m.M12 * z;
            b[i] = m.M20 * x + m.M21 * y + m.M22 * z;
        }
    }
}

/// <summary>3D LUT 베이크 함수(입력 0–1 격자 좌표 → 출력).</summary>
public delegate void LutFunction(float r, float g, float b, out float or, out float og, out float ob);

/// <summary>
/// [PromptFlow Studio v17.10.0] 🎨 실시간 ACES 2.0 색공간 변환 &amp; 사면체 3D LUT 엔진(무할당 핫패스).
/// · Convert: 디코드(ACEScc/cct/709) → SIMD 3×3(AP0/AP1/709/2020 · Bradford) → 인코드 · 행렬 캐시(6×6).
/// · 3D LUT 33³/65³(r 최속 · RGB 인터리브 float) · 사면체 보간(6 사면체 · 아핀 함수 정확 재현) · 셰이퍼 = ACEScct.
/// · 디스플레이(ACES 2.0 SDR): AP1 선형 → 모니터 원색(709/2020) SIMD → 색역 압축 가드 → Daniele 톤스케일(max RGB 노름) → BT.1886 역 γ2.4.
/// </summary>
public sealed class RealtimeAcesColorTransformEngine
{
    public const int Lut33 = 33, Lut65 = 65;

    private readonly ColorMatrix3[] _mats = new ColorMatrix3[36];
    private float[] _lut = Array.Empty<float>();

    public RealtimeAcesColorTransformEngine()
    {
        for (var f = 0; f < 6; f++)
            for (var t = 0; t < 6; t++)
                _mats[f * 6 + t] = AcesColorMath.LinearMatrix(AcesColorMath.LinearOf((AcesSpace)f), AcesColorMath.LinearOf((AcesSpace)t));
    }

    public int LutSize { get; private set; }
    public bool LutReady => LutSize > 0;
    /// <summary>모니터 원색(Rec709 · Rec2020).</summary>
    public AcesSpace Monitor { get; private set; } = AcesSpace.Rec709;
    public double LastConvertUs { get; private set; }
    public double LastLutUs { get; private set; }
    public double LastDisplayUs { get; private set; }
    public double LastBakeMs { get; private set; }

    public ref readonly ColorMatrix3 Matrix(AcesSpace from, AcesSpace to) => ref _mats[(int)from * 6 + (int)to];

    public void SetMonitor(AcesSpace s) => Monitor = s == AcesSpace.Rec2020 ? AcesSpace.Rec2020 : AcesSpace.Rec709;

    /// <summary>평면 버퍼 제자리 변환(무할당).</summary>
    public void Convert(AcesSpace from, AcesSpace to, Span<float> r, Span<float> g, Span<float> b)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = r.Length;
        if (from is not (AcesSpace.Aces2065_1 or AcesSpace.AcesCg or AcesSpace.Rec2020))
            for (var i = 0; i < n; i++) { r[i] = AcesColorMath.Decode(from, r[i]); g[i] = AcesColorMath.Decode(from, g[i]); b[i] = AcesColorMath.Decode(from, b[i]); }
        if (AcesColorMath.LinearOf(from) != AcesColorMath.LinearOf(to)) AcesSimd.Apply(Matrix(from, to), r, g, b);
        if (to is not (AcesSpace.Aces2065_1 or AcesSpace.AcesCg or AcesSpace.Rec2020))
            for (var i = 0; i < n; i++) { r[i] = AcesColorMath.Encode(to, r[i]); g[i] = AcesColorMath.Encode(to, g[i]); b[i] = AcesColorMath.Encode(to, b[i]); }
        LastConvertUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── 디스플레이 변환 ─────────────────────────────────────

    /// <summary>모니터 선형(색역 변환 후) → 표시 코드값(가드 → 톤스케일 → γ). 스칼라 기준 경로 — 베이크/분석 경로 공용.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DisplayEncode(ref float r, ref float g, ref float b, in GamutCompressionParams p, bool guard)
    {
        if (guard) LedSpectralMetamerismGamutClippingGuard.Compress(ref r, ref g, ref b, p);
        var mx = MathF.Max(r, MathF.Max(g, b));
        if (mx > 0f)
        {
            var k = AcesColorMath.Tonescale(mx) / mx;
            r *= k; g *= k; b *= k;
        }
        r = AcesColorMath.Bt1886Inverse(r); g = AcesColorMath.Bt1886Inverse(g); b = AcesColorMath.Bt1886Inverse(b);
    }

    /// <summary>ACEScct 코드값 1 화소 → 표시 코드값(스칼라 기준).</summary>
    public void DisplayFromCct(float r, float g, float b, in GamutCompressionParams p, bool guard, out float or, out float og, out float ob)
    {
        r = AcesColorMath.AcesCctDecode(r); g = AcesColorMath.AcesCctDecode(g); b = AcesColorMath.AcesCctDecode(b);
        Matrix(AcesSpace.AcesCg, Monitor).Transform(ref r, ref g, ref b);
        DisplayEncode(ref r, ref g, ref b, p, guard);
        or = r; og = g; ob = b;
    }

    /// <summary>분석 경로: ACEScct 평면 → 표시 코드값(제자리 · 디코드 → SIMD 행렬 → 가드 계수 → 톤스케일 → γ).</summary>
    public void DisplayAnalytic(Span<float> r, Span<float> g, Span<float> b, LedSpectralMetamerismGamutClippingGuard guard)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = r.Length;
        for (var i = 0; i < n; i++) { r[i] = AcesColorMath.AcesCctDecode(r[i]); g[i] = AcesColorMath.AcesCctDecode(g[i]); b[i] = AcesColorMath.AcesCctDecode(b[i]); }
        AcesSimd.Apply(Matrix(AcesSpace.AcesCg, Monitor), r, g, b);
        guard.Process(r, g, b);
        var p = guard.Params;
        for (var i = 0; i < n; i++)
        {
            float x = r[i], y = g[i], z = b[i];
            DisplayEncode(ref x, ref y, ref z, p, false);
            r[i] = x; g[i] = y; b[i] = z;
        }
        LastDisplayUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── 3D LUT ──────────────────────────────────────────────

    /// <summary>임의 함수 베이크(비 핫패스 · 크기 변경 시에만 재할당).</summary>
    public void BakeLut(int size, LutFunction f)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        size = size >= Lut65 ? Lut65 : size <= 2 ? 2 : size;
        var len = size * size * size * 3;
        if (_lut.Length != len) _lut = new float[len];
        var inv = 1f / (size - 1);
        for (int bi = 0, o = 0; bi < size; bi++)
            for (var gi = 0; gi < size; gi++)
                for (var ri = 0; ri < size; ri++, o += 3)
                {
                    f(ri * inv, gi * inv, bi * inv, out var x, out var y, out var z);
                    _lut[o] = x; _lut[o + 1] = y; _lut[o + 2] = z;
                }
        LutSize = size;
        LastBakeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>ACEScct → 표시(ACES 2.0 SDR · 가드 포함) LUT 베이크.</summary>
    public void BakeDisplayLut(int size, GamutCompressionParams p, bool guard) =>
        BakeLut(size, (float r, float g, float b, out float or, out float og, out float ob) => DisplayFromCct(r, g, b, p, guard, out or, out og, out ob));

    public float LutValue(int index) => _lut[index];

    /// <summary>사면체 보간 1 샘플(입력 0–1 클램프).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SampleTetrahedral(float r, float g, float b, out float or, out float og, out float ob)
    {
        var n = LutSize;
        var s = n - 1;
        float fr = Math.Clamp(r, 0f, 1f) * s, fg = Math.Clamp(g, 0f, 1f) * s, fb = Math.Clamp(b, 0f, 1f) * s;
        int ir = Math.Min((int)fr, s - 1), ig = Math.Min((int)fg, s - 1), ib = Math.Min((int)fb, s - 1);
        float dr = fr - ir, dg = fg - ig, db = fb - ib;
        int sr = 3, sg = 3 * n, sb = 3 * n * n;
        var c000 = ((ib * n + ig) * n + ir) * 3;
        int a, c;   // 경로 꼭짓점 2개(c000 → a → c → c111)
        float w0, w1, w2;
        if (dr > dg)
        {
            if (dg > db) { a = c000 + sr; c = a + sg; w0 = dr; w1 = dg; w2 = db; }
            else if (dr > db) { a = c000 + sr; c = a + sb; w0 = dr; w1 = db; w2 = dg; }
            else { a = c000 + sb; c = a + sr; w0 = db; w1 = dr; w2 = dg; }
        }
        else
        {
            if (db > dg) { a = c000 + sb; c = a + sg; w0 = db; w1 = dg; w2 = dr; }
            else if (db > dr) { a = c000 + sg; c = a + sb; w0 = dg; w1 = db; w2 = dr; }
            else { a = c000 + sg; c = a + sr; w0 = dg; w1 = dr; w2 = db; }
        }
        var d = c000 + sr + sg + sb;
        var l = _lut;
        or = l[c000] + w0 * (l[a] - l[c000]) + w1 * (l[c] - l[a]) + w2 * (l[d] - l[c]);
        og = l[c000 + 1] + w0 * (l[a + 1] - l[c000 + 1]) + w1 * (l[c + 1] - l[a + 1]) + w2 * (l[d + 1] - l[c + 1]);
        ob = l[c000 + 2] + w0 * (l[a + 2] - l[c000 + 2]) + w1 * (l[c + 2] - l[a + 2]) + w2 * (l[d + 2] - l[c + 2]);
    }

    /// <summary>평면 버퍼 제자리 LUT 적용(무할당).</summary>
    public void ApplyLut(Span<float> r, Span<float> g, Span<float> b)
    {
        if (!LutReady) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < r.Length; i++)
        {
            SampleTetrahedral(r[i], g[i], b[i], out var x, out var y, out var z);
            r[i] = x; g[i] = y; b[i] = z;
        }
        LastLutUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
