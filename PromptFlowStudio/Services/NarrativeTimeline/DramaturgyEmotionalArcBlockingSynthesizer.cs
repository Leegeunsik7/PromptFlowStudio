using System.Numerics;

namespace PromptFlowStudio.Services.NarrativeTimeline;

public enum ProxemicZone : byte { Intimate, Personal, Social, Public }
public enum ArcPhase : byte { Steady, Rising, Climax, Falling }

/// <summary>
/// [PromptFlow Studio v17.40.0] 🎭 극적 긴장도 아크 &amp; 무대 공간 블로킹 합성기(<see cref="Step"/> 무할당 · 고정 링버퍼).
///   ▸ 긴장도: 목표 = 비트 기준(T 태그) × 0.8 + 대사 펄스(갈등 어휘 · 감탄 · 단문, τ 2.5 s 감쇠) × 0.35 → 임계 감쇠 스프링(ω 2.6) — 계단 입력에도 연속.
///   ▸ 파생: 배우별 표정 강도(v17.12 · 화자 가중 · 1.2/s 율 제한) · 발화 템포 0.88~1.22 · 조명 무드(친밀 = 웜 3400 K / 적대 = 쿨 7500 K · 키/필 비 1.5~8).
///   ▸ 프록세믹스(Hall): 친밀 &lt; 0.45 m · 개인 &lt; 1.2 m · 사회 &lt; 3.6 m · 공적 — 목표 간격 = 사회 2.4 → 개인 0.9(긴장) · 친밀도 &gt; 0 → 0.4 m 수렴 ·
///     적대 · 긴장 &gt; 0.5 → 대치 0.65 m · 모션 큐(접근 −0.7 / 후퇴 +1.3 m · 1.5 m/s 율) — 큐 배우만 이동(상대는 자리 고수).
///   ▸ 블로킹 궤적: 속도 상한 0.8 + 0.9·T m/s · 가속 상한 3 m/s² · 씬 시작 마크 스냅(편집 컷) · 10 Hz 이력 600 / 궤적 240.
/// </summary>
public sealed class DramaturgyEmotionalArcBlockingSynthesizer
{
    public const int Channels = 12, Series = 600, Trail = 240, Decim = 6;
    public const float Intimate = 0.45f, Personal = 1.2f, Social = 3.6f, MinGap = 0.3f, MaxGap = 4.5f;
    public const float Omega = 2.6f, PulseTau = 2.5f, AffinityRate = 0.6f, ExprRate = 1.2f, CueRate = 1.5f, TurnRate = 1.5f, AccelMax = 3f;

    private readonly float[] _tS = new float[Series], _goalS = new float[Series], _dS = new float[Series];
    private readonly Vector2[] _trail = new Vector2[Trail * 2];
    private int _sHead, _sCount, _trHead, _trCount, _frame;
    private float _cueTarget, _moverT, _yawBase0, _yawBase1 = MathF.PI, _turn0, _turn1;
    private Vector2 _v0, _v1, _cTarget;

    public float Tension { get; private set; } = 0.25f;
    public float TensionVelocity { get; private set; }
    public float TensionGoal { get; private set; } = 0.25f;
    public float BeatTension { get; private set; } = 0.25f;
    public float Pulse { get; private set; }
    public float Affinity { get; private set; }
    public float AffinityGoal { get; private set; }
    public int Speaker { get; private set; } = -1;
    public int Mover { get; private set; } = -1;
    public float CueOffset { get; private set; }
    public Vector2 P0 { get; private set; } = new(-1.2f, 0f);
    public Vector2 P1 { get; private set; } = new(1.2f, 0f);
    public Vector2 Center { get; private set; }
    public float Yaw0 { get; private set; }
    public float Yaw1 { get; private set; } = MathF.PI;
    public float Expr0 { get; private set; } = 0.15f;
    public float Expr1 { get; private set; } = 0.15f;
    public float Tempo { get; private set; } = 1f;
    public float LightK { get; private set; } = 5600f;
    public float KeyFill { get; private set; } = 2f;
    public float GoalDistance { get; private set; } = 2.4f;
    public float Distance => Vector2.Distance(P0, P1);
    public ProxemicZone Zone => ZoneOf(Distance);
    public ArcPhase Phase => Tension >= 0.8f ? ArcPhase.Climax : TensionVelocity > 0.03f ? ArcPhase.Rising : TensionVelocity < -0.03f ? ArcPhase.Falling : ArcPhase.Steady;
    public float PeakTension { get; private set; }
    public int Scenes { get; private set; }
    public int Cues { get; private set; }
    public double LastStepUs { get; private set; }

    public int SeriesCount => _sCount;
    public float TensionSeries(int i) => _tS[Idx(i, _sHead, Series)];
    public float GoalSeries(int i) => _goalS[Idx(i, _sHead, Series)];
    public float DistanceSeries(int i) => _dS[Idx(i, _sHead, Series)];
    public int TrailCount => _trCount;
    public Vector2 TrailPoint(int actor, int i) => _trail[actor * Trail + Idx(i, _trHead, Trail)];
    private static int Idx(int i, int head, int n) => (head - 1 - i + 2 * n) % n;

    public static ProxemicZone ZoneOf(float d) => d < Intimate ? ProxemicZone.Intimate : d < Personal ? ProxemicZone.Personal : d < Social ? ProxemicZone.Social : ProxemicZone.Public;
    public static string ZoneName(ProxemicZone z) => z switch { ProxemicZone.Intimate => "친밀", ProxemicZone.Personal => "개인", ProxemicZone.Social => "사회", _ => "공적" };
    public static string PhaseName(ArcPhase p) => p switch { ArcPhase.Rising => "상승", ArcPhase.Climax => "절정", ArcPhase.Falling => "하강", _ => "유지" };

    // ══════════════════════ 입력(시퀀서 이벤트) ══════════════════════

    /// <summary>비트 태그(T 미지정 = 유지 · A −1 적대 ~ +1 친밀).</summary>
    public void SetBeat(float tension, float affinity)
    {
        if (float.IsFinite(tension)) BeatTension = Math.Clamp(tension, 0f, 1f);
        if (float.IsFinite(affinity)) AffinityGoal = Math.Clamp(affinity, -1f, 1f);
    }

    /// <summary>대사 펄스(강도 0..1 · 화자).</summary>
    public void PulseLine(float intensity, int speaker)
    {
        intensity = Math.Clamp(intensity, 0f, 1f);
        Pulse = MathF.Max(Pulse * 0.6f + intensity * 0.6f, intensity);
        Speaker = speaker;
    }

    public void MotionCue(int actor, ActorMotion m)
    {
        if (actor is < 0 or > 1 || m == ActorMotion.Hold) return;
        Cues++;
        Mover = actor; _moverT = 4f;
        switch (m)
        {
            case ActorMotion.Approach: _cueTarget = -0.7f; break;
            case ActorMotion.Retreat: _cueTarget = 1.3f; break;
            case ActorMotion.Cross: _cTarget = new Vector2(Math.Clamp(_cTarget.X + (actor == 0 ? 1.2f : -1.2f), -2.2f, 2.2f), _cTarget.Y); break;
        }
    }

    /// <summary>씬 시작 — 씬별 마크로 즉시 배치(편집 컷 · 분기 시엔 가드가 관성 보간).</summary>
    public void BeginScene(int scene)
    {
        Scenes++;
        var c = new Vector2(((scene * 37) % 5 - 2) * 0.9f, ((scene * 53) % 3 - 1) * 1.1f);
        Center = _cTarget = c;
        _cueTarget = CueOffset = _turn0 = _turn1 = 0f; Mover = -1; _moverT = 0f;
        var d = GoalDistance;
        P0 = c - new Vector2(d * 0.5f, 0f); P1 = c + new Vector2(d * 0.5f, 0f);
        _v0 = _v1 = Vector2.Zero;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Pulse *= MathF.Exp(-dt / PulseTau);
        TensionGoal = Math.Clamp(BeatTension * 0.8f + Pulse * 0.35f, 0f, 1f);
        var vel = TensionVelocity + (Omega * Omega * (TensionGoal - Tension) - 2f * Omega * TensionVelocity) * dt;
        TensionVelocity = vel;
        Tension = Math.Clamp(Tension + vel * dt, 0f, 1f);
        if (Tension > PeakTension) PeakTension = Tension;
        var T = Tension;
        Affinity += Math.Clamp(AffinityGoal - Affinity, -AffinityRate * dt, AffinityRate * dt);
        var aff = Affinity;

        // 목표 간격(프록세믹스)
        var d = 2.4f + (0.9f - 2.4f) * T;
        if (aff > 0f) d += (0.4f - d) * aff * T * T * (3f - 2f * T);
        else if (T > 0.5f) d += (0.65f - d) * MathF.Min(1f, (T - 0.5f) / 0.3f) * -aff;
        _moverT -= dt;
        if (_moverT <= 0f) { _cueTarget *= MathF.Exp(-dt / 8f); if (_moverT < -6f) Mover = -1; }
        CueOffset += Math.Clamp(_cueTarget - CueOffset, -CueRate * dt, CueRate * dt);
        d = Math.Clamp(d + CueOffset, MinGap, MaxGap);
        GoalDistance = d;
        var cStep = 0.6f * dt;
        var dc = _cTarget - Center;
        var dcl = dc.Length();
        Center = dcl <= cStep ? _cTarget : Center + dc * (cStep / dcl);

        Vector2 g0, g1;
        var axis = new Vector2(1f, 0f);
        if (Mover == 0 && _moverT > 0f) { g1 = P1; g0 = P1 - axis * d; }
        else if (Mover == 1 && _moverT > 0f) { g0 = P0; g1 = P0 + axis * d; }
        else { g0 = Center - axis * (d * 0.5f); g1 = Center + axis * (d * 0.5f); }
        var vmax = 0.8f + 0.9f * T;
        P0 = Move(P0, ref _v0, g0, vmax, dt);
        P1 = Move(P1, ref _v1, g1, vmax, dt);
        if (P1.X - P0.X < MinGap) { var m = 0.5f * (P0.X + P1.X); P0 = new Vector2(m - MinGap * 0.5f, P0.Y); P1 = new Vector2(m + MinGap * 0.5f, P1.Y); }

        // 시선(연속 언랩) · 후퇴 배우 고개 돌림
        var dir = P1 - P0;
        var look = MathF.Atan2(dir.Y, dir.X);
        var turn = 0.5f * Math.Clamp(CueOffset / 1.3f, 0f, 1f);
        _turn0 += Math.Clamp((Mover == 0 ? turn : 0f) - _turn0, -TurnRate * dt, TurnRate * dt);
        _turn1 += Math.Clamp((Mover == 1 ? turn : 0f) - _turn1, -TurnRate * dt, TurnRate * dt);
        _yawBase0 = Unwrap(_yawBase0, look + _turn0);
        _yawBase1 = Unwrap(_yawBase1, look + MathF.PI - _turn1);
        Yaw0 = _yawBase0; Yaw1 = _yawBase1;

        // 표정 · 템포 · 조명 무드
        var e0 = Math.Clamp(0.12f + 0.88f * T * (Speaker == 0 ? 1f : 0.75f), 0f, 1f);
        var e1 = Math.Clamp(0.12f + 0.88f * T * (Speaker == 1 ? 1f : 0.75f), 0f, 1f);
        Expr0 += Math.Clamp(e0 - Expr0, -ExprRate * dt, ExprRate * dt);
        Expr1 += Math.Clamp(e1 - Expr1, -ExprRate * dt, ExprRate * dt);
        Tempo = 0.88f + 0.34f * T;
        LightK = 5600f + (aff > 0f ? -2200f * aff : 1900f * -aff) * (0.4f + 0.6f * T);
        KeyFill = 1.5f + 6.5f * T;

        if (++_frame % Decim == 0)
        {
            _tS[_sHead] = T; _goalS[_sHead] = TensionGoal; _dS[_sHead] = Distance;
            _sHead = (_sHead + 1) % Series; if (_sCount < Series) _sCount++;
            _trail[_trHead] = P0; _trail[Trail + _trHead] = P1;
            _trHead = (_trHead + 1) % Trail; if (_trCount < Trail) _trCount++;
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static Vector2 Move(Vector2 p, ref Vector2 v, Vector2 goal, float vmax, float dt)
    {
        var want = (goal - p) * 2.5f;
        var wl = want.Length();
        if (wl > vmax) want *= vmax / wl;
        var dv = want - v;
        var dvl = dv.Length();
        var amax = AccelMax * dt;
        if (dvl > amax) dv *= amax / dvl;
        v += dv;
        return p + v * dt;
    }

    private static float Unwrap(float prev, float a)
    {
        var d = a - prev;
        d -= MathF.Tau * MathF.Round(d / MathF.Tau);
        return prev + d;
    }

    /// <summary>가드 입력 채널(포즈 4 · 시선 2 · 표정 2 · 템포 · 조명 K/10⁴ · 키/필/10 · 긴장).</summary>
    public void WriteChannels(Span<float> ch)
    {
        ch[0] = P0.X; ch[1] = P0.Y; ch[2] = P1.X; ch[3] = P1.Y;
        ch[4] = Yaw0; ch[5] = Yaw1; ch[6] = Expr0; ch[7] = Expr1;
        ch[8] = Tempo; ch[9] = LightK * 1e-4f; ch[10] = KeyFill * 0.1f; ch[11] = Tension;
    }

    public void ResetMetrics() { PeakTension = Tension; Scenes = Cues = 0; }
}
