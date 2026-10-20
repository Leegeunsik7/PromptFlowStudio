using System.Numerics;

namespace PromptFlowStudio.Services.ProceduralFoley;

public enum FoleyMaterial : byte { Wood, Metal, Glass, Concrete, Flesh, Cloth }

/// <summary>재질 물성(헤르츠 접촉 · 모달 공진) — 문헌 대표값 근사.</summary>
public readonly record struct FoleyMaterialSpec(string Name, float YoungPa, float Poisson, float Density, float BaseHz, float Alpha, float Beta, float Radiation);

/// <summary>재질 표(물성 · 모드 주파수 비 · 모드 진폭).</summary>
public static class FoleyMaterials
{
    public const int Count = 6, Modes = RealtimeModalContactFoleyEngine.Modes;

    private static readonly FoleyMaterialSpec[] Specs =
    {
        //            이름          E(Pa)    ν      ρ       f₀(Hz) α(1/s)  β(s)      복사 효율
        new("목재(Wood)",        11e9f,  0.35f, 650f,   380f,  22f,   2.4e-6f,  0.55f),
        new("금속(Metal)",       200e9f, 0.30f, 7850f,  520f,  1.2f,  1.2e-8f,  1.00f),
        new("유리(Glass)",       70e9f,  0.22f, 2500f,  1450f, 3.0f,  4.0e-8f,  0.80f),
        new("콘크리트(Concrete)", 30e9f,  0.20f, 2400f,  210f,  65f,   6.0e-6f,  0.35f),
        new("신체(Flesh)",       0.8e6f, 0.45f, 1060f,  85f,   180f,  4.0e-5f,  0.60f),
        new("직물(Cloth)",       0.2e6f, 0.40f, 300f,   160f,  420f,  9.0e-5f,  0.20f),
    };

    /// <summary>모드 주파수 비(재질당 16) — 목재/콘크리트 = 자유 보 · 금속 = 판 · 유리 = 종 · 신체/직물 = 고감쇠 밀집.</summary>
    private static readonly float[] Ratio =
    {
        1f, 2.756f, 5.404f, 8.933f, 13.34f, 18.64f, 24.81f, 31.86f, 1.41f, 3.89f, 7.02f, 11.1f, 15.9f, 21.6f, 28.2f, 35.7f,
        1f, 1.594f, 2.136f, 2.296f, 2.653f, 2.918f, 3.156f, 3.501f, 4.06f, 4.48f, 5.12f, 5.81f, 6.43f, 7.27f, 8.04f, 9.13f,
        1f, 1.18f, 2.0f, 2.81f, 3.42f, 4.31f, 5.26f, 6.22f, 7.39f, 8.66f, 9.53f, 10.9f, 12.4f, 13.7f, 15.2f, 16.8f,
        1f, 1.37f, 1.83f, 2.41f, 2.96f, 3.62f, 4.27f, 5.14f, 6.05f, 7.11f, 8.23f, 9.4f, 10.8f, 12.3f, 13.9f, 15.6f,
        1f, 1.52f, 2.11f, 2.74f, 3.42f, 4.18f, 5.01f, 5.93f, 6.92f, 8.0f, 9.2f, 10.5f, 11.9f, 13.4f, 15.0f, 16.7f,
        1f, 1.71f, 2.53f, 3.48f, 4.61f, 5.92f, 7.4f, 9.1f, 11.0f, 13.2f, 15.6f, 18.3f, 21.2f, 24.4f, 27.9f, 31.7f,
    };

    private static readonly float[] Amp = BuildAmp();

    public static FoleyMaterialSpec Spec(FoleyMaterial m) => Specs[(int)m];
    public static string Name(FoleyMaterial m) => Specs[(int)m].Name;
    public static float ModeHz(FoleyMaterial m, int k) => Specs[(int)m].BaseHz * Ratio[(int)m * Modes + k];
    public static float ModeAmp(FoleyMaterial m, int k) => Amp[(int)m * Modes + k];

    /// <summary>Rayleigh 감쇠 → 지수 감쇠율 σ = ½(α + β ω²)(1/s).</summary>
    public static float DecayRate(FoleyMaterial m, float hz)
    {
        var s = Specs[(int)m];
        var w = 2f * MathF.PI * hz;
        return 0.5f * (s.Alpha + s.Beta * w * w);
    }

    /// <summary>T60(s) = ln(1000)/σ.</summary>
    public static float T60(FoleyMaterial m, float hz) => 6.9078f / MathF.Max(DecayRate(m, hz), 1e-3f);

    private static float[] BuildAmp()
    {
        var a = new float[Count * Modes];
        for (var m = 0; m < Count; m++)
            for (var k = 0; k < Modes; k++)
                a[m * Modes + k] = 1f / MathF.Sqrt(Ratio[m * Modes + k]) * (k % 3 == 2 ? 0.6f : 1f);
        return a;
    }
}

/// <summary>접촉 이벤트(물리 단계 → 오디오) — 위치는 v17.23 공간 음향 공급용.</summary>
public readonly record struct FoleyContact(FoleyMaterial Surface, FoleyMaterial Impactor, float ImpactorMassKg, float RadiusM, float SpeedMs, Vector3 Position, byte Source);

/// <summary>
/// [PromptFlow Studio v17.38.0] 🔔 실시간 물리 접촉 모달 진동음 &amp; 충격 폴리 엔진(<see cref="Render"/> 무할당 · 48 kHz · 블록 800 = 60 fps).
///   ▸ 헤르츠 접촉: E* = 1/((1−ν₁²)/E₁ + (1−ν₂²)/E₂) · m*(충격체 · 표면 무한 질량) · 최대 압입 δ = (15 m* v² / (16 E* √R))^(2/5) · F_max = 4/3 E* √R δ^(3/2) ·
///     접촉 시간 τ = 2.94 δ / v · 접촉 반경 a = √(R δ) — 단단한 재질일수록 τ 가 짧아 고역 모드까지 여기(밝은 음색).
///   ▸ 모드 여기: 반정현 힘 펄스 F(t) = F_max sin(πt/τ) 의 스펙트럼 |P(ω)| = 2F_max τ/π · |cos(ωτ/2)| / |1 − (ωτ/π)²| 를 모드별 임펄스로(위상 무시 = 짧은 펄스 근사) ·
///     복사 효율 × 접촉 면적 비 √(a/1 cm) 가중 · 표면 + 충격체 두 물체 각각 보이스(가벼운 충격체 &lt; 0.5 kg 는 감쇠 스케일).
///   ▸ 공진기 뱅크: 2-극 바이쿼드 y[n] = 2r cos ω · y[n−1] − r² y[n−2] + b·x[n] · 보이스당 16 모드 SoA · Vector&lt;float&gt; 모드 병렬(버스별 벡터 누산 · 수평 합 블록당 1 회) ·
///     서브 블록 샘플 정확 시작(이벤트 시각 → 오프셋 · 1/48 ms 분해능) · 모드 상한 20 kHz(나이퀴스트 여유) 제외.
///   ▸ 보이스: 풀 256 · 최약 보이스 스틸 · −96 dB 이하 해제 · 소스 버스 2(0 = 신체/발 · 1 = 소품/손) — v17.23 바이노럴 합성기 모노 입력으로 직접 공급.
/// </summary>
public sealed class RealtimeModalContactFoleyEngine
{
    public const int Fs = 48000, Block = 800, Modes = 16, MaxVoices = 256, Buses = 2, Series = 240;
    public const float Nyquist = 20000f, KillLevel = 1.6e-5f, OutputScale = 0.02f, RefRadius = 0.01f;

    private readonly float[] _y1 = new float[MaxVoices * Modes], _y2 = new float[MaxVoices * Modes], _a1 = new float[MaxVoices * Modes], _a2 = new float[MaxVoices * Modes], _c = new float[MaxVoices * Modes];
    private readonly float[] _x0 = new float[MaxVoices * Modes];
    private readonly int[] _start = new int[MaxVoices], _free = new int[MaxVoices];
    private readonly byte[] _bus = new byte[MaxVoices];
    private readonly bool[] _active = new bool[MaxVoices];
    private readonly FoleyMaterial[] _mat = new FoleyMaterial[MaxVoices];
    private readonly float[] _level = new float[MaxVoices];
    private readonly Vector<float>[][] _vacc = { new Vector<float>[Block], new Vector<float>[Block] };
    private readonly bool[] _accUsed = new bool[Buses];
    private readonly float[] _spec = new float[FoleyMaterials.Count * Modes];
    private readonly float[] _energyS = new float[Series], _peakS = new float[Series];
    private int _freeN, _activeN, _sHead, _sCount;

    public RealtimeModalContactFoleyEngine()
    {
        for (var v = MaxVoices - 1; v >= 0; v--) _free[_freeN++] = v;
    }

    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    public float Gain { get; set; } = 1f;
    public int ActiveVoices => _activeN;
    public long Contacts { get; private set; }
    public long Steals { get; private set; }
    public long Released { get; private set; }
    public float LastEnergyJ { get; private set; }
    public float MaxEnergyJ { get; private set; }
    public float LastContactUs { get; private set; }
    public float LastForceN { get; private set; }
    public float LastAreaMm2 { get; private set; }
    public float LastTauMs { get; private set; }
    public FoleyMaterial LastSurface { get; private set; }
    public float LastPeakDbfs { get; private set; } = -120f;
    public double LastRenderUs { get; private set; }
    public double MaxRenderUs { get; private set; }
    public int SeriesCount => _sCount;
    private int Idx(int i) => (_sHead - 1 - i + Series * 2) % Series;
    public float EnergySeries(int i) => _energyS[Idx(i)];
    public float PeakSeries(int i) => _peakS[Idx(i)];
    /// <summary>재질별 최근 여기 모드 에너지(스펙트럼 뷰어).</summary>
    public float SpectrumLevel(FoleyMaterial m, int k) => _spec[(int)m * Modes + k];

    public void ResetMetrics() { Contacts = Steals = Released = 0; MaxEnergyJ = 0f; MaxRenderUs = 0; }

    public void Clear()
    {
        for (var v = 0; v < MaxVoices; v++) if (_active[v]) Kill(v);
    }

    // ══════════════════════ 헤르츠 접촉(무할당) ══════════════════════

    /// <summary>헤르츠 접촉 해석 — F_max(N) · τ(s) · 접촉 반경 a(m) · 에너지(J).</summary>
    public static void Hertz(FoleyMaterial surface, FoleyMaterial impactor, float massKg, float radiusM, float speed, out float fmax, out float tau, out float a, out float energy)
    {
        var s1 = FoleyMaterials.Spec(surface); var s2 = FoleyMaterials.Spec(impactor);
        var estar = 1f / ((1f - s1.Poisson * s1.Poisson) / s1.YoungPa + (1f - s2.Poisson * s2.Poisson) / s2.YoungPa);
        var r = MathF.Max(radiusM, 1e-4f); var m = MathF.Max(massKg, 1e-4f); var v = MathF.Max(speed, 1e-4f);
        var delta = MathF.Pow(15f * m * v * v / (16f * estar * MathF.Sqrt(r)), 0.4f);
        fmax = 4f / 3f * estar * MathF.Sqrt(r) * MathF.Pow(delta, 1.5f);
        tau = 2.94f * delta / v;
        a = MathF.Sqrt(r * delta);
        energy = 0.5f * m * v * v;
    }

    /// <summary>반정현 펄스 스펙트럼 크기 |P(ω)|.</summary>
    public static float PulseSpectrum(float fmax, float tau, float hz)
    {
        var w = 2f * MathF.PI * hz;
        var x = w * tau / MathF.PI;
        var den = 1f - x * x;
        var c = MathF.Abs(MathF.Cos(0.5f * w * tau));
        // x → 1 극한(0/0) = π/4
        var ratio = MathF.Abs(den) < 1e-3f ? MathF.PI / 4f : c / MathF.Abs(den);
        return 2f * fmax * tau / MathF.PI * ratio;
    }

    /// <summary>접촉 발음(블록 내 샘플 오프셋 0..799) — 표면 + 충격체 보이스.</summary>
    public void Strike(in FoleyContact c, int sampleOffset)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!(c.SpeedMs > 1e-3f) || !float.IsFinite(c.SpeedMs)) return;
        Hertz(c.Surface, c.Impactor, c.ImpactorMassKg, c.RadiusM, c.SpeedMs, out var f, out var tau, out var a, out var e);
        var areaW = MathF.Sqrt(MathF.Max(a, 1e-5f) / RefRadius);
        Excite(c.Surface, f, tau, areaW, c.Source, sampleOffset);
        var imp = c.ImpactorMassKg < 0.5f ? 0.5f : 0.25f;
        if (c.Impactor != c.Surface) Excite(c.Impactor, f * imp, tau, areaW, c.Source, sampleOffset);
        Contacts++;
        LastEnergyJ = e; if (e > MaxEnergyJ) MaxEnergyJ = e;
        LastForceN = f; LastTauMs = tau * 1000f; LastAreaMm2 = MathF.PI * a * a * 1e6f; LastSurface = c.Surface;
        LastContactUs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency);
    }

    private void Excite(FoleyMaterial mat, float fmax, float tau, float areaW, byte bus, int offset)
    {
        var v = Allocate(fmax);
        if (v < 0) return;
        var spec = FoleyMaterials.Spec(mat);
        var b = v * Modes;
        var lvl = 0f;
        for (var k = 0; k < Modes; k++)
        {
            var hz = FoleyMaterials.ModeHz(mat, k);
            var i = b + k;
            _y1[i] = _y2[i] = 0f;
            if (hz >= Nyquist) { _a1[i] = _a2[i] = _c[i] = _x0[i] = 0f; continue; }
            var w = 2f * MathF.PI * hz / Fs;
            var r = MathF.Exp(-FoleyMaterials.DecayRate(mat, hz) / Fs);
            _a1[i] = 2f * r * MathF.Cos(w); _a2[i] = -r * r;
            // 임펄스 응답 = r^n sin((n+1)w)/sin w → 출력 계수 sin w 로 정규화(모드 진폭 = 여기 크기)
            _c[i] = MathF.Sin(w);
            var ex = FoleyMaterials.ModeAmp(mat, k) * PulseSpectrum(fmax, tau, hz) * spec.Radiation * areaW * OutputScale;
            _x0[i] = ex;
            lvl = MathF.Max(lvl, ex);
            _spec[(int)mat * Modes + k] = MathF.Max(_spec[(int)mat * Modes + k] * 0.5f, ex);
        }
        _mat[v] = mat; _bus[v] = bus; _start[v] = Math.Clamp(offset, 0, Block - 1); _level[v] = lvl;
    }

    private int Allocate(float force)
    {
        if (_freeN > 0) { var v = _free[--_freeN]; _active[v] = true; _activeN++; return v; }
        // 스틸: 현재 레벨 최소 보이스(새 이벤트보다 약할 때만)
        var best = -1; var bl = float.PositiveInfinity;
        for (var v = 0; v < MaxVoices; v++) if (_level[v] < bl) { bl = _level[v]; best = v; }
        if (best < 0) return -1;
        Steals++;
        return best;
    }

    private void Kill(int v)
    {
        _active[v] = false; _activeN--; _free[_freeN++] = v; _level[v] = 0f;
        var b = v * Modes;
        for (var k = 0; k < Modes; k++) _y1[b + k] = _y2[b + k] = _x0[b + k] = 0f;
        Released++;
    }

    // ══════════════════════ 렌더(무할당 · SIMD 모드 병렬) ══════════════════════

    /// <summary>블록 합성 — bus0/bus1 에 가산(호출 측 클리어).</summary>
    public void Render(Span<float> bus0, Span<float> bus1)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var vc = Vector<float>.Count;
        var simd = UseSimd && Modes % vc == 0;
        _accUsed[0] = _accUsed[1] = false;
        var g = Gain;
        for (var v = 0; v < MaxVoices; v++)
        {
            if (!_active[v]) continue;
            var dst = _bus[v] == 0 ? bus0 : bus1;
            var b = v * Modes;
            var s0 = _start[v];
            if (simd)
            {
                for (var k = 0; k < Modes; k += vc)
                {
                    var i = b + k;
                    var y1 = new Vector<float>(_y1, i); var y2 = new Vector<float>(_y2, i);
                    var a1 = new Vector<float>(_a1, i); var a2 = new Vector<float>(_a2, i);
                    var c = new Vector<float>(_c, i) * g;
                    // 시작 샘플 s0 에 임펄스 주입(기존 보이스는 x = 0 · s0 = 0) · 버스별 벡터 누산(수평 합은 블록 끝 1 회)
                    var acc = _vacc[_bus[v]];
                    var y = a1 * y1 + a2 * y2 + new Vector<float>(_x0, i);
                    acc[s0] += c * y;
                    y2 = y1; y1 = y;
                    for (var n = s0 + 1; n < Block; n++)
                    {
                        y = a1 * y1 + a2 * y2;
                        acc[n] += c * y;
                        y2 = y1; y1 = y;
                    }
                    _accUsed[_bus[v]] = true;
                    y1.CopyTo(_y1, i); y2.CopyTo(_y2, i);
                }
            }
            else
            {
                for (var k = 0; k < Modes; k++)
                {
                    var i = b + k;
                    float y1 = _y1[i], y2 = _y2[i], a1 = _a1[i], a2 = _a2[i], c = _c[i] * g, x = _x0[i];
                    if (c == 0f) continue;
                    for (var n = s0; n < Block; n++)
                    {
                        var y = a1 * y1 + a2 * y2 + (n == s0 ? x : 0f);
                        dst[n] += c * y;
                        y2 = y1; y1 = y;
                    }
                    _y1[i] = y1; _y2[i] = y2;
                }
            }
            for (var k = 0; k < Modes; k++) _x0[b + k] = 0f;
            _start[v] = 0;
            // 레벨 추정(모드 진폭 상한 √(y1² + y2²) 근사) · 해제
            var lv = 0f;
            for (var k = 0; k < Modes; k++) { var i = b + k; lv = MathF.Max(lv, MathF.Abs(_c[i]) * MathF.Sqrt(_y1[i] * _y1[i] + _y2[i] * _y2[i]) * 1.5f); }
            _level[v] = lv;
            if (lv < KillLevel || !float.IsFinite(lv)) Kill(v);
        }
        for (var bi = 0; bi < Buses; bi++)
        {
            if (!_accUsed[bi]) continue;
            var acc = _vacc[bi]; var dst = bi == 0 ? bus0 : bus1;
            for (var n = 0; n < Block; n++) { dst[n] += Vector.Sum(acc[n]); acc[n] = Vector<float>.Zero; }
        }
        var pk = 0f;
        for (var n = 0; n < Block; n++) { var a = MathF.Abs(bus0[n] + bus1[n]); if (a > pk) pk = a; }
        LastPeakDbfs = 20f * MathF.Log10(MathF.Max(pk, 1e-6f));
        _energyS[_sHead] = LastEnergyJ; _peakS[_sHead] = LastPeakDbfs;
        _sHead = (_sHead + 1) % Series; if (_sCount < Series) _sCount++;
        LastEnergyJ = 0f;
        for (var i = 0; i < _spec.Length; i++) _spec[i] *= 0.92f;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (LastRenderUs > MaxRenderUs) MaxRenderUs = LastRenderUs;
    }
}
