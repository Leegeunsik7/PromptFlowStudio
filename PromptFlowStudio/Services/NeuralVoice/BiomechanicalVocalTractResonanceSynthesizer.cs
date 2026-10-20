using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.NeuralVoice;

/// <summary>
/// [PromptFlow Studio v17.18.0] 🫁 생체역학 성도(Vocal Tract) 공명 &amp; 조음 역학 합성기(<see cref="Process"/> 무할당).
///   ▸ 성도 = 켈리-록바움 1D 디지털 도파관: 2× 과표본(96 kHz · 짝/홀 격자 분리 영상 억제 → 2 반복 평균 데시메이션) · 구간 길이 c/(2fs) = 3.65 mm ·
///     최대 52 구간(화자 성도 길이 = 활성 구간 수 · 성인 남 48 ≈ 17.5 cm).
///     접합 반사 kᵢ = (Aᵢ − Aᵢ₊₁)/(Aᵢ + Aᵢ₊₁) · 전력 정규화 산란 f′ᵢ₊₁ = c·fᵢ − k·bᵢ₊₁ · b′ᵢ = k·fᵢ + c·bᵢ₊₁(c = √(1−k²) · 시변 조음에도 에너지 보존) ·
///     벽 손실 + 협착 점성 손실(면적 &lt; 0.5 cm²) · 접합 = SIMD 레인(v17.15 레인 커널 · 비트 동일).
///   ▸ 성문: 로젠버그 유량 펄스(개방률 OQ · 속도비) × 폐 압력(유성/폐압 τ 8 ms 평활) · 지터/심머 · 성문 반사 0.75 · 기식 잡음(개방기 변조) · 들숨/날숨 호흡 잡음(대역 통과).
///   ▸ 비강: 연구개(구간 22) 3-포트 압력 접합 P_J = 2Σ(Aᵢ·입사파)/ΣA → 32 구간 비강관 → 콧구멍 방사.
///   ▸ 입술 방사: 저역 통과 반사(r ≈ −0.85) · 출력 = 입술 + 코 투과파 1차 미분(방사 임피던스).
///   ▸ 마찰/파열: 협착 구간 난류 잡음 주입(유량 ∝ 진폭) · 폐쇄(면적 → 0 · k → 1) 후 개방 버스트.
///   ▸ 해석 포먼트: 무손실 연쇄 행렬 [P_g; U_g] = Π Mᵢ [P_l; U_l] · 입술 P = 0 → K₂₂(f) 영점(F1–F4 · 5 Hz 격자 + 선형 보간).
/// </summary>
public sealed class BiomechanicalVocalTractResonanceSynthesizer
{
    public const int Fs = 48000, Oversample = 2, MaxSections = 52, NasalSections = 32, VelumIndex = 22, Pad = 64;
    public const float C = 350f, SectionCm = C * 100f / (Fs * Oversample), RhoC = 1.0f;

    public readonly float[] Area = new float[MaxSections], TargetArea = new float[MaxSections];
    private readonly float[] _k = new float[Pad], _c = new float[Pad], _loss = new float[Pad], _f = new float[Pad], _b = new float[Pad], _fn = new float[Pad], _bn = new float[Pad];
    private readonly float[] _nasArea = new float[NasalSections], _nk = new float[NasalSections], _nc = new float[NasalSections], _nf = new float[NasalSections], _nb = new float[NasalSections], _nfn = new float[NasalSections], _nbn = new float[NasalSections];
    private readonly float[] _formants = new float[4];
    private uint _rng = 0x1718u;
    private const float SmoothK = 1f / 384f;
    private float _voiceSm, _lungSm = 1f;
    private float _phase, _lipLp, _noseLp, _prevOut, _breathLp1, _breathLp2, _aspLp, _period = 1f / 120f, _jit, _shim = 1f;

    public BiomechanicalVocalTractResonanceSynthesizer()
    {
        for (var i = 0; i < MaxSections; i++) Area[i] = TargetArea[i] = 3f;
        for (var i = 0; i < NasalSections; i++) { var t = i / (NasalSections - 1f); _nasArea[i] = 1.2f + 2.2f * MathF.Sin(MathF.PI * MathF.Min(1f, t * 1.15f)); }
        _nasArea[NasalSections - 1] = 0.9f;
        for (var i = 0; i < NasalSections - 1; i++) { _nk[i] = (_nasArea[i] - _nasArea[i + 1]) / (_nasArea[i] + _nasArea[i + 1]); _nc[i] = MathF.Sqrt(1f - _nk[i] * _nk[i]); }
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V256 : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V128 : HairSimdPath.Scalar;
        UpdateCoefficients();
    }

    public HairSimdPath Path { get; set; }
    /// <summary>활성 구간 수(성도 길이 = Sections × 7.29 mm).</summary>
    public int Sections { get; set; } = 48;
    public float TractLengthCm => Sections * SectionCm;
    // 제어(조음기 → 매 샘플 사용)
    public float F0 { get; set; } = 120f;
    public float Voicing { get; set; } = 1f;
    public float LungPressure { get; set; } = 1f;
    public float OpenQuotient { get; set; } = 0.6f;
    public float Aspiration { get; set; } = 0.02f;
    public float Jitter { get; set; } = 0.005f;
    public float Shimmer { get; set; } = 0.03f;
    public float VelumArea { get; set; }
    public float TargetVelum { get; set; }
    public int FricationSection { get; set; } = -1;
    public float FricationAmp { get; set; }
    public float BurstAmp { get; set; }
    /// <summary>호흡 잡음(+ 날숨 · − 들숨 · 0 없음).</summary>
    public float Breath { get; set; }
    public float ArticulationTauMs { get; set; } = 20f;
    public float WallLoss { get; set; } = 0.9985f;
    /// <summary>협착(면적 &lt; 0.5 cm²) 점성 손실 계수(구간·스텝당).</summary>
    public float ConstrictionLoss { get; set; } = 0.03f;
    /// <summary>비강 점막 손실(스텝당 · 구강보다 큼 → 비음 대역폭 넓음).</summary>
    public float NasalLoss { get; set; } = 0.99f;
    /// <summary>폐쇄 시 공동 벽 항복 손실(스텝당).</summary>
    public float YieldingLoss { get; set; } = 0.004f;
    public float ClosureDepth { get; private set; }
    public float GlottalReflection { get; set; } = 0.6f;
    public float LipReflection { get; set; } = -0.85f;
    public float OutputGain { get; set; } = 1f;
    /// <summary>성문 유량 → 성도 입사 이득(유성음 레벨 · 기식/마찰 잡음 대비).</summary>
    public float GlottalGain { get; set; } = 3f;
    /// <summary>기식 잡음 → 유량 상대 배율(방사 미분 후 잡음 우세 방지).</summary>
    public float AspirationScale { get; set; } = 0.04f;
    public float Glottal { get; private set; }
    public float LastOutput { get; private set; }
    public double LastProcessUs { get; private set; }
    public long Samples { get; private set; }
    public float Formant(int i) => _formants[i];
    public float Reflection(int i) => _k[i];

    private float Noise()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (2f / 16777216f) - 1f;
    }

    /// <summary>현재 면적 → 반사 계수(활성 구간).</summary>
    public void UpdateCoefficients()
    {
        var n = Math.Clamp(Sections, 16, MaxSections);
        for (var i = 0; i < Pad; i++) { _k[i] = 0f; _c[i] = 1f; _loss[i] = WallLoss; }
        for (var i = 0; i < n - 1; i++)
        {
            float a = MathF.Max(0.0003f, Area[i]), b = MathF.Max(0.0003f, Area[i + 1]);
            _k[i] = (a - b) / (a + b);
            _c[i] = MathF.Sqrt(MathF.Max(0f, 1f - _k[i] * _k[i]));
            // 협착 점성 손실(R ∝ 1/A²) — 좁은 구간 파 감쇠(폐쇄 뒤 축적 에너지 · 측지 공명 Q 제한)
            var m = MathF.Min(a, b);
            _loss[i] = WallLoss * (1f - ConstrictionLoss * Math.Clamp((0.5f - m) / 0.5f, 0f, 1f));
        }
        // 구강 폐쇄(최소 면적 &lt; 0.15 cm²) → 폐쇄 뒤 공동 벽 항복 흡수(전 구간 추가 손실 · 압력 축적/개방 과도 억제)
        var minA = float.MaxValue; var at = -1;
        for (var i = VelumIndex + 1; i < n; i++) if (Area[i] < minA) { minA = Area[i]; at = i; }
        ClosureDepth = Math.Clamp((0.15f - minA) / 0.15f, 0f, 1f);
        if (ClosureDepth > 0f)
            for (var i = 0; i < at; i++) _loss[i] *= 1f - YieldingLoss * ClosureDepth;
    }

    // ── SIMD 산란 커널 ──────────────────────────────────────

    /// <summary>전력 정규화 격자 산란(직교 회전 · 시변 면적에서도 에너지 보존): f′ᵢ₊₁ = c·fᵢ − k·bᵢ₊₁ · b′ᵢ = k·fᵢ + c·bᵢ₊₁ · c = √(1−k²).</summary>
    private static void ScatterK<TV, L>(float[] k, float[] cc, float[] f, float[] b, float[] fn, float[] bn, int from, int to, float[] loss) where TV : struct where L : struct, IHairLane<TV>
    {
        var i = from;
        for (; i + L.Width <= to; i += L.Width)
        {
            TV kk = L.Load(k, i), c = L.Load(cc, i), fi = L.Load(f, i), bi = L.Load(b, i + 1), ls = L.Load(loss, i);
            L.Store(fn, i + 1, L.Mul(L.Sub(L.Mul(c, fi), L.Mul(kk, bi)), ls));
            L.Store(bn, i, L.Mul(L.Add(L.Mul(kk, fi), L.Mul(c, bi)), ls));
        }
        for (; i < to; i++)
        {
            float kk = k[i], c = cc[i], fi = f[i], bi = b[i + 1], ls = loss[i];
            fn[i + 1] = (c * fi - kk * bi) * ls;
            bn[i] = (kk * fi + c * bi) * ls;
        }
    }

    private void Scatter(int junctions)
    {
        switch (Path)
        {
            case HairSimdPath.V256 when Vector256.IsHardwareAccelerated: ScatterK<Vector256<float>, V256Lane>(_k, _c, _f, _b, _fn, _bn, 0, junctions, _loss); break;
            case HairSimdPath.V256 or HairSimdPath.V128 when Vector128.IsHardwareAccelerated: ScatterK<Vector128<float>, V128Lane>(_k, _c, _f, _b, _fn, _bn, 0, junctions, _loss); break;
            default: ScatterK<float, ScalarLane>(_k, _c, _f, _b, _fn, _bn, 0, junctions, _loss); break;
        }
    }

    /// <summary>로젠버그 성문 유량(위상 0..1).</summary>
    public static float RosenbergFlow(float ph, float oq)
    {
        var tp = oq * 0.62f; var tn = oq - tp;
        if (ph < tp) return 0.5f * (1f - MathF.Cos(MathF.PI * ph / tp));
        if (ph < oq) return MathF.Cos(MathF.PI * 0.5f * (ph - tp) / tn);
        return 0f;
    }

    /// <summary>샘플 블록 합성(조음 1차 접근 · 성문 · 도파관 · 비강 · 방사 · 무할당).</summary>
    public void Process(Span<float> dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = Math.Clamp(Sections, 16, MaxSections);
        var ka = 1f - MathF.Exp(-1000f / (Fs * MathF.Max(0.5f, ArticulationTauMs)));
        for (var s = 0; s < dst.Length; s++)
        {
            // 조음: 2 샘플마다 면적 접근 + 계수 갱신(정규화 파 재해석 계단 → 방사 미분 스파이크 억제)
            if ((Samples & 1) == 0)
            {
                var k8 = 1f - MathF.Pow(1f - ka, 2f);
                for (var i = 0; i < n; i++) Area[i] += (TargetArea[i] - Area[i]) * k8;
                VelumArea += (TargetVelum - VelumArea) * k8;
                UpdateCoefficients();
            }
            // 성문
            _phase += F0 * (1f + _jit) / Fs;
            if (_phase >= 1f)
            {
                _phase -= 1f;
                _jit = Jitter * Noise();
                _shim = 1f + Shimmer * Noise();
                _period = 1f / MathF.Max(40f, F0);
            }
            // 성문 구동 평활(τ ≈ 8 ms · 유성/폐압 계단 → 유량 DC 계단 과도 차단)
            _voiceSm += (Voicing - _voiceSm) * SmoothK;
            _lungSm += (LungPressure - _lungSm) * SmoothK;
            var flow = RosenbergFlow(_phase, Math.Clamp(OpenQuotient, 0.3f, 0.9f)) * _voiceSm * _shim;
            var open = _phase < OpenQuotient ? 1f : 0.25f;
            _aspLp += 0.35f * (Noise() - _aspLp);
            var asp = Aspiration * _aspLp * open * (0.3f + _voiceSm);
            var g = _lungSm * GlottalGain * (flow + AspirationScale * asp);
            Glottal = g;
            var o = 0f;
            for (var os = 0; os < Oversample; os++) o += Lattice(n, g);
            o *= 1f / Oversample;
            // 호흡 잡음(대역 통과 · 날숨/들숨)
            var br = 0f;
            if (Breath != 0f)
            {
                _breathLp1 += 0.25f * (Noise() - _breathLp1);
                _breathLp2 += 0.05f * (_breathLp1 - _breathLp2);
                br = (_breathLp1 - _breathLp2) * MathF.Abs(Breath) * (Breath > 0f ? 0.05f : 0.035f);
            }
            var y = (o - _prevOut) * 6f * OutputGain + br;
            _prevOut = o;
            dst[s] = y;
            Samples++;
        }
        LastOutput = dst.Length > 0 ? dst[^1] : 0f;
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>도파관 1 반복(2× 과표본 · 구간 길이 c/(2fs)) → 입술 + 코 투과파.</summary>
    private float Lattice(int n, float g)
    {
        Scatter(n - 1);
        // 정규화 파: 유량 원 U → f = U/√A₀ · 방사 유량 ∝ (f − b)·√A_lip
        _fn[0] = (g / MathF.Sqrt(MathF.Max(0.0003f, Area[0])) + GlottalReflection * _b[0]) * WallLoss;
        var fl = _f[n - 1];
        var refl = LipReflection * (0.5f * fl + 0.5f * _lipLp);
        _lipLp = fl;
        _bn[n - 1] = refl;
        var lipOut = (fl - refl) * MathF.Sqrt(MathF.Max(0.0003f, Area[n - 1]));
        // 비강 3-포트(연구개)
        var noseOut = 0f;
        if (VelumArea > 0.01f)
        {
            var v = VelumIndex;
            float a1 = MathF.Max(0.0003f, Area[v]), a2 = MathF.Max(0.0003f, Area[v + 1]), a3 = MathF.Max(0.0003f, VelumArea);
            float s1 = MathF.Sqrt(a1), s2 = MathF.Sqrt(a2), s3 = MathF.Sqrt(a3);
            // 정규화 → 압력파(p = f/√A) → 3-포트 압력 접합 → 정규화
            var pj = 2f * (s1 * _f[v] + s2 * _b[v + 1] + s3 * _nb[0]) / (a1 + a2 + a3);
            _fn[v + 1] = (pj * s2 - _b[v + 1]) * WallLoss;
            _bn[v] = (pj * s1 - _f[v]) * WallLoss;
            _nfn[0] = (pj * s3 - _nb[0]) * WallLoss;
            for (var i = 0; i < NasalSections - 1; i++)
            {
                float kk = _nk[i], c = _nc[i], fi = _nf[i], bi = _nb[i + 1];
                _nfn[i + 1] = (c * fi - kk * bi) * NasalLoss;
                _nbn[i] = (kk * fi + c * bi) * NasalLoss;
            }
            var nl = _nf[NasalSections - 1];
            var nr = -0.8f * (0.5f * nl + 0.5f * _noseLp);
            _noseLp = nl;
            _nbn[NasalSections - 1] = nr;
            noseOut = (nl - nr) * MathF.Sqrt(_nasArea[NasalSections - 1]);
            for (var i = 0; i < NasalSections; i++) { _nf[i] = _nfn[i]; _nb[i] = _nbn[i]; }
        }
        else
        {
            for (var i = 0; i < NasalSections; i++) { _nf[i] *= 0.9f; _nb[i] *= 0.9f; }
        }
        // 마찰 · 버스트 잡음(협착 하류 주입)
        if (FricationSection >= 0 && FricationSection < n - 1 && (FricationAmp > 0f || BurstAmp > 0f))
        {
            var nz = Noise() * (FricationAmp + BurstAmp) * _lungSm;
            _fn[FricationSection + 1] += nz;
        }
        for (var i = 0; i < n; i++) { _f[i] = _fn[i]; _b[i] = _bn[i]; }
        return lipOut + noseOut;
    }

    public void Reset()
    {
        Array.Clear(_f); Array.Clear(_b); Array.Clear(_fn); Array.Clear(_bn); Array.Clear(_nf); Array.Clear(_nb);
        _voiceSm = 0f; _lungSm = 1f;
        _phase = 0f; _lipLp = _noseLp = _prevOut = _breathLp1 = _breathLp2 = _aspLp = 0f; _rng = 0x1718u; Samples = 0;
    }

    // ══════════════════════ 해석 포먼트 ══════════════════════

    /// <summary>면적 함수(구간 n) → 무손실 연쇄 행렬 K₂₂(f) 영점 F1–F4(Hz · dst 길이 4 · 무할당).</summary>
    public static int Formants(ReadOnlySpan<float> area, int n, Span<float> dst)
    {
        dst.Clear();
        var found = 0;
        var prev = 0f; var prevF = 0f;
        for (var f = 50f; f <= 5000f && found < dst.Length; f += 5f)
        {
            var th = 2f * MathF.PI * f / C * (SectionCm / 100f);
            float cs = MathF.Cos(th), sn = MathF.Sin(th);
            // [[a, j b], [j c, d]]
            float a = 1f, b = 0f, c = 0f, d = 1f;
            for (var i = 0; i < n; i++)
            {
                var z = RhoC / MathF.Max(0.0003f, area[i]);
                float ma = cs, mb = z * sn, mc = sn / z, md = cs;
                float na = a * ma - b * mc, nb = a * mb + b * md, nc = c * ma + d * mc, nd = -c * mb + d * md;
                a = na; b = nb; c = nc; d = nd;
            }
            if (f > 50f && MathF.Sign(d) != MathF.Sign(prev) && prev != 0f)
            {
                var t = prev / (prev - d);
                dst[found++] = prevF + (f - prevF) * t;
            }
            prev = d; prevF = f;
        }
        return found;
    }

    /// <summary>현재 면적의 F1–F4 갱신.</summary>
    public void UpdateFormants() => Formants(Area, Math.Clamp(Sections, 16, MaxSections), _formants);
}
