using System.Numerics;

namespace PromptFlowStudio.Services.SpatialHaptics;

/// <summary>
/// [PromptFlow Studio v17.24.0] 🛡 듀얼 레이트 결합 안정성 &amp; 모터 버징 방어 가드(<see cref="Process"/> · <see cref="Commit"/> 무할당).
///   ▸ 수동성 관측기(PO): 포트 에너지 W ← W·(1 − Δt/τ) − F·Δx(환경 흡수 에너지 · 실제 디바이스 변위 · 0.25 s 누설 저장소) — W &lt; 0 이면 환경이 에너지를 생성(1 샘플 지연 · 60 Hz 장면 계단).
///   ▸ 수동성 제어기(PC): α = min((−W + F·v·Δt) / (Δt·|v|²), αmax) 가변 댐핑 F′ = F − α·v → 다음 W ≥ 0 예측 복원(Hannaford–Ryu POPC · 최종 단).
///   ▸ 듀얼 레이트: 60 Hz 장면을 1 kHz 서브사이클에서 1차 홀드(FOH) 보간 → 프레임 경계 계단 침투(에너지 주입) 제거.
///   ▸ 모터 보호(성형 → PC → 하드 클램프): 슬루 제한(N/ms) · 소프트-니 포화(tanh · Fmax) · 버징(고역 RMS + dF 부호 반전율) 시 가상 커플링 댐핑 6 N·s/m.
///   ▸ 가드 OFF 에서도 관측기는 동작 — 생성 에너지 · 포화 · 버징 지표를 비교용으로 집계.
/// </summary>
public sealed class DualRatePassivityAntiBuzzingGuard
{
    public const int Ports = RealtimeKinestheticForceFeedbackEngine.Hips;
    public const float AlphaMax = 30f, BuzzRmsThreshold = 0.3f, BuzzRevRate = 0.06f, BuzzDamping = 6f, HpHz = 40f, RmsTau = 0.02f, LeakTau = 0.25f;

    private readonly float[] _w = new float[Ports], _alpha = new float[Ports], _hpRms = new float[Ports], _hpPrevIn = new float[Ports], _hpOut = new float[Ports];
    private readonly float[] _dPrev = new float[Ports], _fmPrev = new float[Ports], _rev = new float[Ports];
    private readonly Vector3[] _prev = new Vector3[Ports];
    private readonly bool[] _buzz = new bool[Ports];

    public bool Enabled { get; set; } = true;
    public bool FirstOrderHold { get; set; } = true;
    public bool PassivityControl { get; set; } = true;
    public float ForceMax { get; set; } = 8f;
    public float SlewMax { get; set; } = 2f;     // N/ms
    public long Saturations { get; private set; }
    public long SlewLimited { get; private set; }
    public long BuzzEvents { get; private set; }
    public long BuzzSamples { get; private set; }
    public long PcActivations { get; private set; }
    public double DissipatedJ { get; private set; }
    /// <summary>모터 출력 기준 누적 생성(활성) 에너지(J) — 관측기 W 가 0 아래로 내려간 총량(실제 변위 기준).</summary>
    public double GeneratedJ { get; private set; }
    public float MaxRawForce { get; private set; }
    public float MaxOutForce { get; private set; }
    public float AlphaLast { get; private set; }
    public float BuzzLevel { get; private set; }
    public float Energy(int port) => _w[port];
    public float Alpha(int port) => _alpha[port];
    public bool Buzzing(int port) => _buzz[port];

    public void Reset()
    {
        Array.Clear(_w); Array.Clear(_alpha); Array.Clear(_hpRms); Array.Clear(_hpPrevIn); Array.Clear(_hpOut);
        Array.Clear(_dPrev); Array.Clear(_fmPrev); Array.Clear(_rev); Array.Clear(_prev); Array.Clear(_buzz);
        Saturations = SlewLimited = BuzzEvents = BuzzSamples = PcActivations = 0;
        DissipatedJ = GeneratedJ = 0; MaxRawForce = MaxOutForce = AlphaLast = BuzzLevel = 0f;
    }

    /// <summary>모터 포트 1 샘플 처리: 버징 검출 → 슬루 → 포화 → 버징 댐핑 → POPC → 하드 클램프.</summary>
    public Vector3 Process(int port, Vector3 f, Vector3 v, float dt)
    {
        var fm = f.Length();
        if (fm > MaxRawForce) MaxRawForce = fm;

        // 버징 검출: 힘 크기 고역 RMS + dF 부호 반전율(지속 진동 vs 접촉 순간 단발 과도)
        var a = 1f / (1f + 2f * MathF.PI * HpHz * dt);
        var hp = a * (_hpOut[port] + fm - _hpPrevIn[port]);
        _hpPrevIn[port] = fm; _hpOut[port] = hp;
        var kr = dt / RmsTau;
        _hpRms[port] += kr * (hp * hp - _hpRms[port]);
        var d = fm - _fmPrev[port];
        _fmPrev[port] = fm;
        var flip = d * _dPrev[port] < 0f && MathF.Abs(d) > 1e-3f ? 1f : 0f;
        if (d != 0f) _dPrev[port] = d;
        _rev[port] += kr * (flip - _rev[port]);
        var rms = MathF.Sqrt(_hpRms[port]);
        var buzz = rms > BuzzRmsThreshold && _rev[port] > BuzzRevRate;
        if (buzz && !_buzz[port]) BuzzEvents++;
        if (buzz) BuzzSamples++;
        _buzz[port] = buzz;
        if (rms > BuzzLevel) BuzzLevel = rms;

        if (!Enabled)
        {
            _alpha[port] = 0f;
            _prev[port] = f;
            if (fm > MaxOutForce) MaxOutForce = fm;
            if (fm > ForceMax) Saturations++;
            return f;
        }

        // 슬루 제한
        var df = f - _prev[port];
        var dl = df.Length();
        var smax = SlewMax * dt * 1000f;
        if (dl > smax) { f = _prev[port] + df * (smax / dl); SlewLimited++; }

        // 소프트-니 포화
        fm = f.Length();
        var knee = 0.75f * ForceMax;
        if (fm > knee)
        {
            var m = knee + (ForceMax - knee) * MathF.Tanh((fm - knee) / (ForceMax - knee));
            f *= m / fm;
            Saturations++;
        }

        _prev[port] = f;
        // 버징 시 가상 커플링 댐핑(저역 통과는 루프 위상 지연 → 발산 가속이므로 미사용)
        if (buzz) f -= BuzzDamping * v;

        // POPC(최종 단): 예측 에너지 W − F·v·Δt 가 음이면 가변 댐핑
        var v2 = v.LengthSquared();
        var wp = _w[port] - Vector3.Dot(f, v) * dt;
        var alpha = 0f;
        if (PassivityControl && wp < 0f && v2 > 1e-10f)
        {
            alpha = MathF.Min(-wp / (dt * v2), AlphaMax);
            f -= alpha * v;
            DissipatedJ += alpha * v2 * dt;
            PcActivations++;
        }
        _alpha[port] = alpha;
        AlphaLast = alpha;

        // 하드 클램프(댐핑 항 포함 모터 한계)
        var om = f.Length();
        if (om > ForceMax) { f *= ForceMax / om; om = ForceMax; }
        if (om > MaxOutForce) MaxOutForce = om;
        return f;
    }

    /// <summary>디바이스 적분 후 실제 변위로 관측기 갱신: W += −F·Δx.</summary>
    public void Commit(int port, Vector3 f, Vector3 dx)
    {
        var w = _w[port] * (1f - RealtimeKinestheticForceFeedbackEngine.Dt / LeakTau) - Vector3.Dot(f, dx);
        if (w < 0f) { GeneratedJ -= w; w = 0f; }
        _w[port] = w;
    }
}
