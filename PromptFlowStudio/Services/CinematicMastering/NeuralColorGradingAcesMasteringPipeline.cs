using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Icvfx;

namespace PromptFlowStudio.Services.CinematicMastering;

public enum MasteringTarget : byte { SdrRec709, HdrPq1000 }

/// <summary>ASC CDL 룩(ACEScct 도메인) + 노출(stop).</summary>
public readonly record struct GradeLook(float Exposure, Vector3 Slope, Vector3 Offset, Vector3 Power, float Saturation)
{
    public static readonly GradeLook Neutral = new(0f, Vector3.One, Vector3.Zero, Vector3.One, 1f);
    public static GradeLook Lerp(in GradeLook a, in GradeLook b, float t) => new(
        a.Exposure + (b.Exposure - a.Exposure) * t, Vector3.Lerp(a.Slope, b.Slope, t), Vector3.Lerp(a.Offset, b.Offset, t),
        Vector3.Lerp(a.Power, b.Power, t), a.Saturation + (b.Saturation - a.Saturation) * t);
}

/// <summary>장면 감정선(Valence −1..1 · Arousal 0..1 · Tension 0..1).</summary>
public readonly record struct SceneEmotion(float Valence, float Arousal, float Tension);

/// <summary>
/// [PromptFlow Studio v16.80.0] 🎨 신경망 실시간 컬러 그레이딩 &amp; ACES 시네마틱 마스터링 파이프라인.
/// ACES2065-1(AP0) → ACEScg(AP1 · 노출 접힘) [SIMD 평면 행렬] → ACEScct 33³ 3D LUT(감정 MLP 3-4-10 룩 ⊕ 프리셋 블렌드 · ASC CDL 베이크 · 삼선형) →
/// SDR: RRT_SAT [SIMD] → RRT+ODT 유리 근사 [SIMD] → ODT_SAT·AP1→Rec.709 [SIMD] → BT.1886 · HDR: AP1→Rec.2020 [SIMD] → 1000 nit 하이라이트 롤오프 [SIMD] → PQ.
/// 웨이브폼(64열 × 64빈) · 클리핑/크러시 · APL · 최대 휘도 스코프. 프레임 루프 무할당.
/// </summary>
public sealed class NeuralColorGradingAcesMasteringPipeline
{
    public const int LutN = 33, WfCols = 64, WfBins = 64;
    public const float HdrPeakNits = 1000f, SdrWhiteNits = 100f;

    private static readonly Mat3 RrtSat = new(0.970889f, 0.026963f, 0.002148f, 0.010889f, 0.986963f, 0.002148f, 0.010889f, 0.026963f, 0.962148f);
    private static readonly Mat3 OdtSat = new(0.949056f, 0.047185f, 0.003759f, 0.019056f, 0.977185f, 0.003759f, 0.019056f, 0.047185f, 0.933759f);
    public static readonly Mat3 OdtToRec709 = AcesColorManagementAndMoireSuppressionEngine.Ap1ToRec709 * OdtSat;

    // ── 감정 → 룩 MLP(고정 가중치 3-4-10 · tanh) ──
    private static readonly float[] W1 = { 1.4f, 0f, 0f, 0f, 1.6f, 0f, 0f, 0f, 1.8f, 0.6f, 0.9f, -0.8f };
    private static readonly float[] B1 = { 0f, -0.6f, -0.7f, 0f };
    private static readonly float[] W2 =
    {
        // h0 따뜻함 · h1 각성 · h2 긴장 · h3 경쾌함
         0.55f,  0.10f, -0.20f,  0.20f,   // slope R
         0.15f,  0.05f, -0.05f,  0.15f,   // slope G
        -0.45f,  0.05f,  0.35f, -0.10f,   // slope B
         0.20f,  0.00f, -0.30f,  0.05f,   // offset R
         0.05f,  0.00f,  0.10f,  0.05f,   // offset G
        -0.25f,  0.00f,  0.45f,  0.00f,   // offset B
        -0.10f,  0.30f,  0.45f, -0.15f,   // power R
        -0.05f,  0.30f,  0.45f, -0.15f,   // power G
         0.05f,  0.30f,  0.40f, -0.15f,   // power B
         0.25f,  0.55f, -0.70f,  0.35f,   // saturation
    };
    private static readonly float[] B2 = new float[10];

    public static readonly string[] PresetNames = { "뉴트럴", "틸 & 오렌지", "블리치 바이패스", "누아르", "골든 아워" };
    public static readonly GradeLook[] Presets =
    {
        GradeLook.Neutral,
        new(0f, new(1.08f, 1.0f, 0.92f), new(0.01f, -0.003f, -0.012f), new(0.96f, 1f, 1.06f), 1.15f),
        new(-0.2f, new(1.05f, 1.05f, 1.05f), new(-0.01f, -0.01f, -0.01f), new(1.2f, 1.2f, 1.2f), 0.55f),
        new(-0.4f, new(0.98f, 1.0f, 1.04f), new(-0.02f, -0.015f, 0.0f), new(1.3f, 1.3f, 1.25f), 0.12f),
        new(0.25f, new(1.12f, 1.03f, 0.85f), new(0.015f, 0.005f, -0.02f), new(0.95f, 0.98f, 1.05f), 1.1f),
    };

    private readonly float[] _lut = new float[LutN * LutN * LutN * 3];
    private readonly int[] _wf = new int[WfCols * WfBins];
    private float[] _r = Array.Empty<float>(), _g = Array.Empty<float>(), _b = Array.Empty<float>();
    private float[] _or = Array.Empty<float>(), _og = Array.Empty<float>(), _ob = Array.Empty<float>();
    private int _w, _h;

    public int Width => _w;
    public int Height => _h;
    public MasteringTarget Target { get; set; } = MasteringTarget.SdrRec709;
    public int Preset { get; private set; } = 1;
    /// <summary>신경망 룩 혼합(0 = 프리셋 · 1 = 감정 MLP).</summary>
    public float NeuralMix { get; private set; } = 0.6f;
    public SceneEmotion Emotion { get; private set; } = new(0.2f, 0.3f, 0.2f);
    public GradeLook NeuralLook { get; private set; } = GradeLook.Neutral;
    public GradeLook Look { get; private set; } = GradeLook.Neutral;
    public bool LutDirty { get; private set; } = true;

    // ── 스코프/계측 ──
    public ReadOnlySpan<int> Waveform => _wf;
    public int WaveformMax { get; private set; }
    public float Apl { get; private set; }
    public float MaxNits { get; private set; }
    public int ClipPixels { get; private set; }
    public int CrushPixels { get; private set; }
    public Vector3 MeanCode { get; private set; }
    public long Frames { get; private set; }
    public long LutBakes { get; private set; }
    public double LastFrameMicros { get; private set; }
    public double LastBakeMicros { get; private set; }
    public string? LastAction { get; private set; }

    public ReadOnlySpan<float> OutR => _or;
    public ReadOnlySpan<float> OutG => _og;
    public ReadOnlySpan<float> OutB => _ob;

    /// <summary>제어 평면: 해상도 확정(작업/출력 평면 버퍼 1회 할당).</summary>
    public void Configure(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _w = width; _h = height;
        var n = width * height;
        _r = new float[n]; _g = new float[n]; _b = new float[n];
        _or = new float[n]; _og = new float[n]; _ob = new float[n];
        LutDirty = true;
    }

    public void SetPreset(int index) { Preset = Math.Clamp(index, 0, Presets.Length - 1); LutDirty = true; LastAction = "🎨 프리셋 " + PresetNames[Preset]; }
    public void SetNeuralMix(float mix) { NeuralMix = Math.Clamp(mix, 0f, 1f); LutDirty = true; }

    /// <summary>감정선 갱신 → MLP 룩 추론(무할당). 룩 변화가 문턱 이상이면 LUT 재베이크 예약.</summary>
    public void SetEmotion(SceneEmotion e)
    {
        Emotion = e;
        var nl = InferLook(e);
        if (LookDelta(nl, NeuralLook) > 0.004f) { NeuralLook = nl; LutDirty = true; }
    }

    /// <summary>감정 → CDL 룩 MLP(3-4-10 · tanh · 고정 가중치).</summary>
    public static GradeLook InferLook(SceneEmotion e)
    {
        Span<float> x = stackalloc float[3] { Math.Clamp(e.Valence, -1f, 1f), Math.Clamp(e.Arousal, 0f, 1f), Math.Clamp(e.Tension, 0f, 1f) };
        Span<float> h = stackalloc float[4];
        for (var j = 0; j < 4; j++) h[j] = MathF.Tanh(W1[j * 3] * x[0] + W1[j * 3 + 1] * x[1] + W1[j * 3 + 2] * x[2] + B1[j]);
        Span<float> o = stackalloc float[10];
        for (var k = 0; k < 10; k++) o[k] = MathF.Tanh(W2[k * 4] * h[0] + W2[k * 4 + 1] * h[1] + W2[k * 4 + 2] * h[2] + W2[k * 4 + 3] * h[3] + B2[k]);
        return new GradeLook(0.25f * (h[3] - h[2]),
            new Vector3(1f + 0.25f * o[0], 1f + 0.25f * o[1], 1f + 0.25f * o[2]),
            new Vector3(0.04f * o[3], 0.04f * o[4], 0.04f * o[5]),
            new Vector3(1f + 0.3f * o[6], 1f + 0.3f * o[7], 1f + 0.3f * o[8]),
            1f + 0.45f * o[9]);
    }

    private static float LookDelta(in GradeLook a, in GradeLook b) =>
        MathF.Abs(a.Exposure - b.Exposure) + (a.Slope - b.Slope).Length() + (a.Offset - b.Offset).Length() * 4f + (a.Power - b.Power).Length() + MathF.Abs(a.Saturation - b.Saturation);

    /// <summary>ASC CDL v1.2(ACEScct 코드값 → 코드값): (in·slope + offset)^power · Rec.709 루마 기준 채도.</summary>
    public static Vector3 ApplyCdl(Vector3 cct, in GradeLook look)
    {
        var v = cct * look.Slope + look.Offset;
        v = new Vector3(MathF.Pow(MathF.Max(v.X, 0f), look.Power.X), MathF.Pow(MathF.Max(v.Y, 0f), look.Power.Y), MathF.Pow(MathF.Max(v.Z, 0f), look.Power.Z));
        var luma = 0.2126f * v.X + 0.7152f * v.Y + 0.0722f * v.Z;
        return new Vector3(luma) + (v - new Vector3(luma)) * look.Saturation;
    }

    /// <summary>LUT 베이크(33³ · ACEScct 격자 → CDL → 선형 ACEScg). 프레임당 최대 1회, 무할당.</summary>
    public void BakeLut()
    {
        var t0 = Stopwatch.GetTimestamp();
        var look = GradeLook.Lerp(Presets[Preset], NeuralLook, NeuralMix);
        Look = look;
        const float inv = 1f / (LutN - 1);
        var i = 0;
        for (var bi = 0; bi < LutN; bi++)
            for (var gi = 0; gi < LutN; gi++)
                for (var ri = 0; ri < LutN; ri++)
                {
                    var o = ApplyCdl(new Vector3(ri * inv, gi * inv, bi * inv), look);
                    _lut[i++] = AcesColorManagementAndMoireSuppressionEngine.AcesCctDecode(o.X);
                    _lut[i++] = AcesColorManagementAndMoireSuppressionEngine.AcesCctDecode(o.Y);
                    _lut[i++] = AcesColorManagementAndMoireSuppressionEngine.AcesCctDecode(o.Z);
                }
        LutDirty = false; LutBakes++;
        LastBakeMicros = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
    }

    /// <summary>ACEScct 코드값(0..1) → 3D LUT 삼선형 보간(선형 ACEScg).</summary>
    public Vector3 SampleLut(Vector3 cct)
    {
        const float s = LutN - 1;
        float fx = Math.Clamp(cct.X, 0f, 1f) * s, fy = Math.Clamp(cct.Y, 0f, 1f) * s, fz = Math.Clamp(cct.Z, 0f, 1f) * s;
        int x0 = Math.Min((int)fx, LutN - 2), y0 = Math.Min((int)fy, LutN - 2), z0 = Math.Min((int)fz, LutN - 2);
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        var c000 = L(x0, y0, z0); var c100 = L(x0 + 1, y0, z0); var c010 = L(x0, y0 + 1, z0); var c110 = L(x0 + 1, y0 + 1, z0);
        var c001 = L(x0, y0, z0 + 1); var c101 = L(x0 + 1, y0, z0 + 1); var c011 = L(x0, y0 + 1, z0 + 1); var c111 = L(x0 + 1, y0 + 1, z0 + 1);
        var c00 = Vector3.Lerp(c000, c100, tx); var c10 = Vector3.Lerp(c010, c110, tx); var c01 = Vector3.Lerp(c001, c101, tx); var c11 = Vector3.Lerp(c011, c111, tx);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, ty), Vector3.Lerp(c01, c11, ty), tz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 L(int x, int y, int z) { var k = ((z * LutN + y) * LutN + x) * 3; return new Vector3(_lut[k], _lut[k + 1], _lut[k + 2]); }

    /// <summary>
    /// 핫패스: AP0 평면(선형 ACES2065-1) → 그레이딩 → 디스플레이 코드값 평면 + 스코프(무할당).
    /// </summary>
    public void Process(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b)
    {
        var n = _w * _h;
        if (r.Length < n || g.Length < n || b.Length < n) throw new ArgumentException("입력 평면 크기 부족", nameof(r));
        using var guard = ZeroGcRegionScope.Enter("Mastering.Process");
        var t0 = Stopwatch.GetTimestamp();
        if (LutDirty) BakeLut();

        // 1) AP0 → AP1(노출 접힘) — SIMD
        var e = MathF.Pow(2f, Look.Exposure);
        var m = AcesColorManagementAndMoireSuppressionEngine.Ap0ToAp1;
        var me = new Mat3(m.M00 * e, m.M01 * e, m.M02 * e, m.M10 * e, m.M11 * e, m.M12 * e, m.M20 * e, m.M21 * e, m.M22 * e);
        MatrixPlanar(me, r[..n], g[..n], b[..n], _r.AsSpan(0, n), _g.AsSpan(0, n), _b.AsSpan(0, n));

        // 2) ACEScct LUT(룩) — 스칼라 삼선형
        for (var i = 0; i < n; i++)
        {
            var c = new Vector3(AcesColorManagementAndMoireSuppressionEngine.AcesCctEncode(MathF.Max(_r[i], 0f)),
                                AcesColorManagementAndMoireSuppressionEngine.AcesCctEncode(MathF.Max(_g[i], 0f)),
                                AcesColorManagementAndMoireSuppressionEngine.AcesCctEncode(MathF.Max(_b[i], 0f)));
            var o = SampleLut(c);
            _r[i] = o.X; _g[i] = o.Y; _b[i] = o.Z;
        }

        // 3) 출력 변환 — SIMD 행렬/톤 곡선 + 스칼라 전달 함수
        Span<float> or = _or.AsSpan(0, n), og = _og.AsSpan(0, n), ob = _ob.AsSpan(0, n);
        if (Target == MasteringTarget.SdrRec709)
        {
            MatrixPlanar(RrtSat, _r.AsSpan(0, n), _g.AsSpan(0, n), _b.AsSpan(0, n), or, og, ob);
            ToneFitPlanar(or); ToneFitPlanar(og); ToneFitPlanar(ob);
            MatrixPlanar(OdtToRec709, or, og, ob, _r.AsSpan(0, n), _g.AsSpan(0, n), _b.AsSpan(0, n));
            for (var i = 0; i < n; i++)
            {
                or[i] = AcesColorManagementAndMoireSuppressionEngine.Bt1886Encode(_r[i]);
                og[i] = AcesColorManagementAndMoireSuppressionEngine.Bt1886Encode(_g[i]);
                ob[i] = AcesColorManagementAndMoireSuppressionEngine.Bt1886Encode(_b[i]);
            }
        }
        else
        {
            MatrixPlanar(AcesColorManagementAndMoireSuppressionEngine.Ap1ToRec2020, _r.AsSpan(0, n), _g.AsSpan(0, n), _b.AsSpan(0, n), or, og, ob);
            HdrRolloffPlanar(or); HdrRolloffPlanar(og); HdrRolloffPlanar(ob);
            for (var i = 0; i < n; i++)
            {
                or[i] = AcesColorManagementAndMoireSuppressionEngine.PqEncode(or[i] * HdrPeakNits);
                og[i] = AcesColorManagementAndMoireSuppressionEngine.PqEncode(og[i] * HdrPeakNits);
                ob[i] = AcesColorManagementAndMoireSuppressionEngine.PqEncode(ob[i] * HdrPeakNits);
            }
        }

        Scopes(or, og, ob);
        Frames++;
        LastFrameMicros = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
    }

    private void Scopes(ReadOnlySpan<float> or, ReadOnlySpan<float> og, ReadOnlySpan<float> ob)
    {
        Array.Clear(_wf);
        var hdr = Target == MasteringTarget.HdrPq1000;
        int clip = 0, crush = 0; double sum = 0, sr = 0, sg = 0, sb = 0; var maxCode = 0f;
        for (var y = 0; y < _h; y++)
        {
            var row = y * _w;
            for (var x = 0; x < _w; x++)
            {
                var i = row + x;
                float cr = or[i], cg = og[i], cb = ob[i];
                var luma = hdr ? 0.2627f * cr + 0.6780f * cg + 0.0593f * cb : 0.2126f * cr + 0.7152f * cg + 0.0722f * cb;
                var bin = Math.Clamp((int)(luma * WfBins), 0, WfBins - 1);
                _wf[bin * WfCols + x * WfCols / _w]++;
                if (cr >= 0.999f || cg >= 0.999f || cb >= 0.999f) clip++;
                if (luma <= 0.002f) crush++;
                if (luma > maxCode) maxCode = luma;
                sum += luma; sr += cr; sg += cg; sb += cb;
            }
        }
        var max = 0; foreach (var v in _wf) if (v > max) max = v;
        var n = (float)(_w * _h);
        WaveformMax = max; ClipPixels = clip; CrushPixels = crush;
        Apl = (float)(sum / n);
        MeanCode = new Vector3((float)(sr / n), (float)(sg / n), (float)(sb / n));
        MaxNits = hdr ? AcesColorManagementAndMoireSuppressionEngine.PqDecode(maxCode) : AcesColorManagementAndMoireSuppressionEngine.Bt1886Decode(maxCode) * SdrWhiteNits;
    }

    // ── SIMD 커널(Vector<float> · 꼬리 스칼라) — 스칼라 레퍼런스와 연산 순서 동일(비트 일치) ──

    /// <summary>평면 3×3 행렬: o = M·(r,g,b). ((m0·r + m1·g) + m2·b) 순서.</summary>
    public static void MatrixPlanar(in Mat3 m, ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<float> or, Span<float> og, Span<float> ob)
    {
        var n = r.Length;
        var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var w = Vector<float>.Count;
            Vector<float> a0 = new(m.M00), a1 = new(m.M01), a2 = new(m.M02), b0 = new(m.M10), b1 = new(m.M11), b2 = new(m.M12), c0 = new(m.M20), c1 = new(m.M21), c2 = new(m.M22);
            ref var rr = ref MemoryMarshal.GetReference(r); ref var gg = ref MemoryMarshal.GetReference(g); ref var bb = ref MemoryMarshal.GetReference(b);
            ref var o0 = ref MemoryMarshal.GetReference(or); ref var o1 = ref MemoryMarshal.GetReference(og); ref var o2 = ref MemoryMarshal.GetReference(ob);
            for (; i <= n - w; i += w)
            {
                var vr = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref rr, i)));
                var vg = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref gg, i)));
                var vb = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref bb, i)));
                Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref o0, i)), a0 * vr + a1 * vg + a2 * vb);
                Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref o1, i)), b0 * vr + b1 * vg + b2 * vb);
                Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref o2, i)), c0 * vr + c1 * vg + c2 * vb);
            }
        }
        MatrixPlanarScalar(m, r[i..], g[i..], b[i..], or[i..], og[i..], ob[i..]);
    }

    public static void MatrixPlanarScalar(in Mat3 m, ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<float> or, Span<float> og, Span<float> ob)
    {
        for (var i = 0; i < r.Length; i++)
        {
            float vr = r[i], vg = g[i], vb = b[i];
            or[i] = m.M00 * vr + m.M01 * vg + m.M02 * vb;
            og[i] = m.M10 * vr + m.M11 * vg + m.M12 * vb;
            ob[i] = m.M20 * vr + m.M21 * vg + m.M22 * vb;
        }
    }

    /// <summary>RRT+ODT 유리 근사(제자리): (v(v+a) − b) / (v(c·v + d) + e).</summary>
    public static void ToneFitPlanar(Span<float> v)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && v.Length >= Vector<float>.Count)
        {
            var w = Vector<float>.Count;
            Vector<float> a = new(0.0245786f), bq = new(0.000090537f), c = new(0.983729f), d = new(0.4329510f), e = new(0.238081f);
            ref var p = ref MemoryMarshal.GetReference(v);
            for (; i <= v.Length - w; i += w)
            {
                ref var at = ref Unsafe.As<float, byte>(ref Unsafe.Add(ref p, i));
                var x = Unsafe.ReadUnaligned<Vector<float>>(ref at);
                Unsafe.WriteUnaligned(ref at, (x * (x + a) - bq) / (x * (c * x + d) + e));
            }
        }
        ToneFitScalar(v[i..]);
    }

    public static void ToneFitScalar(Span<float> v)
    {
        for (var i = 0; i < v.Length; i++) { var x = v[i]; v[i] = (x * (x + 0.0245786f) - 0.000090537f) / (x * (0.983729f * x + 0.4329510f) + 0.238081f); }
    }

    /// <summary>HDR 하이라이트 롤오프(제자리 · 장면 선형 → 피크 대비 0..1): max(x,0) / (max(x,0) + K) · K = 중간 회색 0.18 → 15 nit.</summary>
    public const float HdrK = 0.18f * (HdrPeakNits / 15f) - 0.18f;

    public static void HdrRolloffPlanar(Span<float> v)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && v.Length >= Vector<float>.Count)
        {
            var w = Vector<float>.Count;
            Vector<float> k = new(HdrK);
            ref var p = ref MemoryMarshal.GetReference(v);
            for (; i <= v.Length - w; i += w)
            {
                ref var at = ref Unsafe.As<float, byte>(ref Unsafe.Add(ref p, i));
                var x = Vector.Max(Unsafe.ReadUnaligned<Vector<float>>(ref at), Vector<float>.Zero);
                Unsafe.WriteUnaligned(ref at, x / (x + k));
            }
        }
        HdrRolloffScalar(v[i..]);
    }

    public static void HdrRolloffScalar(Span<float> v)
    {
        for (var i = 0; i < v.Length; i++) { var x = MathF.Max(v[i], 0f); v[i] = x / (x + HdrK); }
    }
}
