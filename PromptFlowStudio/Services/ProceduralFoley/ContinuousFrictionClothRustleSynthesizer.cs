namespace PromptFlowStudio.Services.ProceduralFoley;

public enum FabricKind : byte { Silk, Cotton, Leather }

/// <summary>
/// [PromptFlow Studio v17.38.0] 🧣 연속 마찰 슬립 &amp; 의상 스침 절차적 합성기(<see cref="Render"/> 무할당 · 48 kHz · 60 fps 매개변수 갱신).
///   ▸ 마찰(샷 노이즈): 접선 미끄럼 속도 v · 법선력 N · 재질 요철 간격 d → 요철 충돌률 λ = v/d · 샘플당 베르누이(λ/Fs) 임펄스 ·
///     진폭 ∝ N·√v·(0.5 + U) → 대역통과(중심 = clamp(λ, 200, 8 kHz) · Q 1.6) + 표면 최저 2 모드 공진(재질 음색) · v = 0 이면 무음(정지 마찰).
///   ▸ 의상 스침: 구동 D = |의상 스트레인 변화율|(v17.16) × 40 + |관절 각가속도|(v17.37) / 400 → 크래클 사건률 D × 직물 기준률 ·
///     진폭 파레토(α 1.6 · 구김 통계) · 직물 필터(실크 5.2 kHz 광대역 · 면 2.4 kHz · 가죽 1.1 kHz 고 Q 삐걱) + D 비례 공기 스윕 잡음.
///   ▸ 프레임 간 매개변수 선형 램프(지퍼 잡음 없음) · 채널 4(손 0–3 슬립 · 지면 스크레이프) + 의상 1 · 출력 링버퍼 4096(스코프).
/// </summary>
public sealed class ContinuousFrictionClothRustleSynthesizer
{
    public const int Fs = RealtimeModalContactFoleyEngine.Fs, Block = RealtimeModalContactFoleyEngine.Block, Channels = 4, Ring = 4096, RingMask = Ring - 1;

    /// <summary>재질 요철 간격(m) · 마찰 이득.</summary>
    private static readonly float[] Spacing = { 0.0005f, 0.0002f, 0.0001f, 0.0015f, 0.002f, 0.0008f };
    private static readonly float[] FricGain = { 0.010f, 0.012f, 0.008f, 0.016f, 0.004f, 0.003f };
    private static readonly float[] FabricHz = { 5200f, 2400f, 1100f }, FabricQ = { 0.7f, 0.8f, 3.2f }, FabricRate = { 2600f, 1400f, 260f }, FabricAmp = { 0.010f, 0.016f, 0.045f };

    private readonly float[] _v = new float[Channels], _n = new float[Channels], _v0 = new float[Channels], _n0 = new float[Channels];
    private readonly FoleyMaterial[] _mat = new FoleyMaterial[Channels];
    private readonly byte[] _busOf = new byte[Channels];
    private readonly float[] _bp1 = new float[Channels], _bp2 = new float[Channels], _m1a = new float[Channels], _m1b = new float[Channels], _m2a = new float[Channels], _m2b = new float[Channels];
    private readonly float[] _ring = new float[Ring];
    private float _drive, _drive0, _cb1, _cb2, _air;
    private uint _rng = 0x2545F491u;
    private int _ringHead;

    public bool FrictionEnabled { get; set; } = true;
    public bool ClothEnabled { get; set; } = true;
    public FabricKind Fabric { get; set; } = FabricKind.Cotton;
    public float Gain { get; set; } = 1f;
    public float SlipSpeed(int ch) => _v[ch];
    public float NormalForce(int ch) => _n[ch];
    public float ClothDrive => _drive;
    public long Shots { get; private set; }
    public long Crackles { get; private set; }
    public float FrictionRmsDb { get; private set; } = -120f;
    public float ClothRmsDb { get; private set; } = -120f;
    public double LastRenderUs { get; private set; }

    public void CopyRing(Span<float> dst)
    {
        var n = Math.Min(dst.Length, Ring);
        for (var i = 0; i < n; i++) dst[i] = _ring[(_ringHead - n + i) & RingMask];
    }

    public void ResetMetrics() { Shots = Crackles = 0; }

    /// <summary>슬립 채널 매개변수(프레임) — 속도 m/s · 법선력 N · 표면 재질 · 출력 버스.</summary>
    public void SetSlip(int ch, float speed, float normalN, FoleyMaterial surface, byte bus)
    {
        _v[ch] = float.IsFinite(speed) ? Math.Clamp(speed, 0f, 5f) : 0f;
        _n[ch] = float.IsFinite(normalN) ? Math.Clamp(normalN, 0f, 2000f) : 0f;
        _mat[ch] = surface; _busOf[ch] = bus;
    }

    /// <summary>의상 구동(프레임) — 스트레인 변화율(1/s) · 관절 각가속도(rad/s²).</summary>
    public void SetCloth(float strainRate, float angularAccel)
    {
        var d = MathF.Abs(strainRate) * 40f + MathF.Abs(angularAccel) / 400f;
        _drive = float.IsFinite(d) ? Math.Clamp(d, 0f, 4f) : 0f;
    }

    private float Uniform()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    // ══════════════════════ 렌더(무할당) ══════════════════════

    /// <summary>블록 합성 — bus0(신체) / bus1(소품)에 가산 · 의상은 bus0.</summary>
    public void Render(Span<float> bus0, Span<float> bus1)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double fr = 0, cl = 0;
        const float invB = 1f / Block;
        for (var ch = 0; ch < Channels; ch++)
        {
            float va = _v0[ch], vb = FrictionEnabled ? _v[ch] : 0f, na = _n0[ch], nb = _n[ch];
            _v0[ch] = vb; _n0[ch] = nb;
            if (va <= 1e-4f && vb <= 1e-4f && MathF.Abs(_bp1[ch]) + MathF.Abs(_m1a[ch]) + MathF.Abs(_m2a[ch]) < 1e-7f) continue;
            var mat = _mat[ch]; var dst = _busOf[ch] == 0 ? bus0 : bus1;
            var d = Spacing[(int)mat]; var kg = FricGain[(int)mat] * Gain;
            // 표면 최저 2 모드(재질 음색 · 고정 공진)
            Resonator(FoleyMaterials.ModeHz(mat, 0), FoleyMaterials.DecayRate(mat, FoleyMaterials.ModeHz(mat, 0)) * 3f, out var ma1, out var ma2);
            Resonator(FoleyMaterials.ModeHz(mat, 1), FoleyMaterials.DecayRate(mat, FoleyMaterials.ModeHz(mat, 1)) * 3f, out var mb1, out var mb2);
            float bp1 = _bp1[ch], bp2 = _bp2[ch], m1a = _m1a[ch], m1b = _m1b[ch], m2a = _m2a[ch], m2b = _m2b[ch];
            for (var i = 0; i < Block; i++)
            {
                var t = i * invB;
                var v = va + (vb - va) * t; var nn = na + (nb - na) * t;
                var lam = v / d;
                var x = 0f;
                if (v > 1e-4f && Uniform() < MathF.Min(1f, lam / Fs))
                {
                    x = kg * nn * MathF.Sqrt(v) * (0.5f + Uniform()) * (Uniform() < 0.5f ? -1f : 1f);
                    Shots++;
                }
                // 대역통과(중심 λ · Q 1.6) — 2차 SVF 근사(샘플별 계수)
                var fc = Math.Clamp(lam, 200f, 8000f);
                var g = MathF.Tan(MathF.PI * fc / Fs); var k = 1f / 1.6f;
                var hp = (x - (k + g) * bp1 - bp2) / (1f + g * (g + k));
                var bp = g * hp + bp1; bp1 = g * hp + bp; var lp = g * bp + bp2; bp2 = g * bp + lp;
                var ya = ma1 * m1a + ma2 * m1b + 0.15f * x; m1b = m1a; m1a = ya;
                var yb = mb1 * m2a + mb2 * m2b + 0.08f * x; m2b = m2a; m2a = yb;
                var y = bp + 0.05f * (ya + yb);
                dst[i] += y; fr += y * y;
                _ring[(_ringHead + i) & RingMask] += y;
            }
            if (!float.IsFinite(bp1 + bp2 + m1a + m1b + m2a + m2b)) bp1 = bp2 = m1a = m1b = m2a = m2b = 0f;
            _bp1[ch] = bp1; _bp2[ch] = bp2; _m1a[ch] = m1a; _m1b[ch] = m1b; _m2a[ch] = m2a; _m2b[ch] = m2b;
        }
        // 의상 스침
        float da = _drive0, db = ClothEnabled ? _drive : 0f;
        _drive0 = db;
        if (da > 1e-4f || db > 1e-4f || MathF.Abs(_cb1) + MathF.Abs(_cb2) > 1e-7f)
        {
            var f = (int)Fabric;
            var g = MathF.Tan(MathF.PI * FabricHz[f] / Fs); var k = 1f / FabricQ[f];
            var rate = FabricRate[f]; var amp = FabricAmp[f] * Gain;
            float cb1 = _cb1, cb2 = _cb2, air = _air;
            for (var i = 0; i < Block; i++)
            {
                var dr = da + (db - da) * (i * invB);
                var x = 0f;
                if (dr > 1e-4f && Uniform() < MathF.Min(1f, dr * rate / Fs))
                {
                    var u = MathF.Max(Uniform(), 1e-3f);
                    var pareto = MathF.Min(MathF.Pow(u, -1f / 1.6f), 20f);
                    x = amp * MathF.Min(dr, 1.5f) * pareto * (Uniform() < 0.5f ? -1f : 1f) * 0.25f;
                    Crackles++;
                }
                // 공기 스윕(저역 잡음 · 구동 비례)
                air += 0.12f * ((Uniform() * 2f - 1f) * dr * amp * 0.3f - air);
                var hp = (x + air - (k + g) * cb1 - cb2) / (1f + g * (g + k));
                var bp = g * hp + cb1; cb1 = g * hp + bp; var lp = g * bp + cb2; cb2 = g * bp + lp;
                bus0[i] += bp; cl += bp * bp;
                _ring[(_ringHead + i) & RingMask] += bp;
            }
            if (!float.IsFinite(cb1 + cb2 + air)) cb1 = cb2 = air = 0f;
            _cb1 = cb1; _cb2 = cb2; _air = air;
        }
        FrictionRmsDb = 10f * MathF.Log10((float)(fr / Block) + 1e-12f);
        ClothRmsDb = 10f * MathF.Log10((float)(cl / Block) + 1e-12f);
        // 링 다음 블록 영역 비움
        _ringHead = (_ringHead + Block) & RingMask;
        for (var i = 0; i < Block; i++) _ring[(_ringHead + i) & RingMask] = 0f;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void Resonator(float hz, float sigma, out float a1, out float a2)
    {
        if (hz >= 0.45f * Fs) { a1 = a2 = 0f; return; }
        var r = MathF.Exp(-sigma / Fs);
        a1 = 2f * r * MathF.Cos(2f * MathF.PI * hz / Fs); a2 = -r * r;
    }
}
