using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.DexterousManipulation;

public enum ManipulationMode : byte { Handover = 0, Carry }

public enum TakeoverPhase : byte { Hold = 0, Offer, Reach, Lock, Transfer, Release, Retract, Carry, Dropped }

/// <summary>
/// [PromptFlow Studio v17.36.0] 🤝 다자간 소품 인계 &amp; 폐쇄 기구학 협동 조작 합성기(<see cref="Step"/> 무할당).
///   ▸ 액터 2(마주 보기 · 어깨 높이 1.42/1.36 m · 상완 0.29 · 전완 0.26 m) · 손 슬롯 0 A 오른손 · 1 B 오른손 · 2 A 왼손 · 3 B 왼손 · 팔 = 2-본 IK(팔꿈치 극 = 아래 · 바깥).
///   ▸ 인계(4 s 주기 · 주는 쪽 교대): 제시 0–1 s(소품 = 최소 저크 H_g → M) · 수신 손 위상 잠금(0.4–1 s · 같은 위상 변수 φ 로 손목 = rest ↔ 파지 손목 최소 저크 · 손가락 마지막 40 % 닫힘
///     → φ = 1 에서 위치 · 속도 동시 일치) · 이양 1–1.8 s(소유권 α 스무스스텝 = 하중 분담 (1 − α)/α) · 해제 1.8–2.2 s(손가락 열림 · 접근 반대 6 cm 후퇴) · 회수 2.2–3.2 s.
///   ▸ 폐쇄 체인: 파지 중 손 = 소품 로컬 템플릿 고정 → 손목 목표 · 팔 도달(0.55 m × 0.995) 초과 잔차 → 소품 위치 평균 보정 + 회전 보정(Σ rₖ × vₖ / Σ|rₖ|²) 6회 반복 → 폐쇄 오차.
///   ▸ 협동 운반(상자 12 kg · COM +6 cm 편심): 상자 경로 z = 0.9·sin(2πt/6) · 액터 보행 반위상 잠금(1.7 Hz · ±2.5 cm 상하 · ±1.5 cm 횡) · 4 손 폐쇄 체인 ·
///     하중 분담 = 최소 노름 [1;x;z]f = [m(g + a_y);0;0] 의사역(3×3 해석 역) → 손별 수직 지지력.
///   ▸ 링버퍼 240: 소유권 · 폐쇄 오차 · 손별 하중.
/// </summary>
public sealed class MultiActorPropHandoverSynthesizer
{
    public const int Slots = RealtimeDexterousGraspSynthesisEngine.MaxSlots, Series = 240, ChainIter = 6;
    public const float Cycle = 4f, Upper = BipedSkeleton.UpperArm, Fore = BipedSkeleton.Forearm, Reach = Upper + Fore, G = 9.81f;

    private static readonly Vector3 Meet = new(0f, 1.12f, 0f);
    private readonly Vector3[] _root = { new(-0.40f, 0f, 0f), new(0.40f, 0f, 0f) };
    private readonly float[] _yaw = { MathF.PI / 2f, -MathF.PI / 2f };
    private readonly float[] _shoulderH = { 1.42f, 1.36f };
    private readonly Vector3[] _sh = new Vector3[Slots], _el = new Vector3[Slots], _wr = new Vector3[Slots], _wrTarget = new Vector3[Slots], _load = new Vector3[Slots];
    private readonly float[] _static = new float[Slots], _share = new float[Slots];
    private readonly bool[] _hold = new bool[Slots];
    private readonly float[] _ownS = new float[Series], _closeS = new float[Series], _loadS = new float[Series * Slots];
    private Vector3 _pPrev, _pPrev2, _lockWristPrev, _objVelPrev;
    private float _t, _relVel;
    private int _seriesHead, _seriesCount, _cycle = -1;
    private bool _wasReach;
    private Vector3 _dropP, _dropV, _path;

    public ManipulationMode Mode { get; private set; } = ManipulationMode.Handover;
    public TakeoverPhase Phase { get; private set; }
    public int Giver { get; private set; }
    public int Receiver => 1 - Giver;
    /// <summary>수신자 소유권 α(0 = 주는 쪽 · 1 = 받는 쪽).</summary>
    public float Ownership { get; private set; }
    public float PhaseValue { get; private set; }
    public float Time => _t;
    public bool Dropped { get; private set; }
    public float LockErrorMm { get; private set; }
    public float LockRelVelMmS { get; private set; }
    public float ClosureErrorMm { get; private set; }
    public float MaxClosureErrorMm { get; private set; }
    public float ChainCorrectionCm { get; private set; }
    public float TiltDeg { get; private set; }
    public long Transfers { get; private set; }
    public long Drops { get; private set; }
    public long Locks { get; private set; }
    public float MinOwnership { get; private set; } = 1f;
    public float MaxOwnership { get; private set; }
    public Vector3 ObjectAcceleration { get; private set; }
    public bool Holding(int slot) => _hold[slot];
    public float Share(int slot) => _share[slot];
    public Vector3 Load(int slot) => _load[slot];
    public float StaticLoad(int slot) => _static[slot];
    public Vector3 Shoulder(int slot) => _sh[slot];
    public Vector3 Elbow(int slot) => _el[slot];
    public Vector3 Wrist(int slot) => _wr[slot];
    public Vector3 ActorRoot(int a) => _root[a];
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + Series * 2) % Series;
    public float OwnershipSeries(int i) => _ownS[Idx(i)];
    public float ClosureSeries(int i) => _closeS[Idx(i)];
    public float LoadSeries(int i, int slot) => _loadS[Idx(i) * Slots + slot];

    public static string PhaseName(TakeoverPhase p) => p switch
    {
        TakeoverPhase.Offer => "제시", TakeoverPhase.Reach => "위상 잠금 접근", TakeoverPhase.Lock => "잠금", TakeoverPhase.Transfer => "소유권 이양", TakeoverPhase.Release => "해제",
        TakeoverPhase.Retract => "회수", TakeoverPhase.Carry => "협동 운반", TakeoverPhase.Dropped => "낙하", _ => "유지",
    };

    public void Begin(ManipulationMode mode)
    {
        Mode = mode; _t = 0f; _cycle = -1; Giver = 0; Dropped = false; Ownership = 0f; _wasReach = false;
        _root[0] = new Vector3(-0.40f, 0f, 0f); _root[1] = new Vector3(0.40f, 0f, 0f);
        Array.Clear(_hold);
    }

    public void ResetMetrics()
    {
        Transfers = Drops = Locks = 0; MaxClosureErrorMm = 0f; LockErrorMm = LockRelVelMmS = 0f; MinOwnership = 1f; MaxOwnership = 0f; _seriesCount = 0;
    }

    private static float MinJerk(float u) { u = Math.Clamp(u, 0f, 1f); return u * u * u * (10f + u * (-15f + 6f * u)); }
    private static float Smooth(float u) { u = Math.Clamp(u, 0f, 1f); return u * u * (3f - 2f * u); }

    private int Actor(int slot) => slot & 1;
    private bool Left(int slot) => slot >= 2;

    private Vector3 ShoulderPos(int slot, float bob, float sway)
    {
        var a = Actor(slot);
        var r = BipedSkeleton.Right(_yaw[a]);
        return _root[a] + new Vector3(0f, _shoulderH[a] + bob, 0f) + r * ((Left(slot) ? -1f : 1f) * BipedSkeleton.ShoulderW + sway);
    }

    private Vector3 RestWrist(int slot) => _sh[slot] + BipedSkeleton.Forward(_yaw[Actor(slot)]) * 0.12f + new Vector3(0f, -0.5f, 0f);

    private Vector3 HoldPos(int actor) => _root[actor] + BipedSkeleton.Forward(_yaw[actor]) * 0.24f + new Vector3(0f, 1.08f, actor == 0 ? -0.06f : 0.06f);

    /// <summary>2-본 팔 IK(도달 클램프) → 팔꿈치 · 실제 손목.</summary>
    private Vector3 Arm(int slot, Vector3 target)
    {
        var s = _sh[slot];
        var d = target - s;
        var l = d.Length();
        var reached = l > Reach * 0.995f ? s + d / l * (Reach * 0.995f) : target;
        var a = Actor(slot);
        var pole = new Vector3(0f, -1f, 0f) + BipedSkeleton.Right(_yaw[a]) * (Left(slot) ? -0.6f : 0.6f);
        _el[slot] = BipedSkeleton.TwoBone(s, reached, pole, Upper, Fore);
        _wr[slot] = reached;
        return reached;
    }

    /// <summary>
    /// 프레임 진행: 소품 자세 · 손 슬롯 활성/닫힘 · 템플릿 파지 · 폐쇄 체인 · 하중(무할당). impulse = 이번 프레임 외력(N).
    /// </summary>
    public void Step(DexProp prop, RealtimeDexterousGraspSynthesisEngine eng, FrictionConeSlipPreventionGuard guard, Vector3 impulse, float dt)
    {
        _t += dt;
        if (Mode == ManipulationMode.Carry) StepCarry(prop, eng, impulse, dt);
        else StepHandover(prop, eng, guard, impulse, dt);
        // 소품 가속 = 계획 경로 2차 차분(폐쇄 체인 보정 · 미끄럼 피드백 배제) · ±30 m/s²
        var acc = (_path - 2f * _pPrev + _pPrev2) / (dt * dt);
        _pPrev2 = _pPrev; _pPrev = _path;
        var al = acc.Length();
        ObjectAcceleration = _seriesCount < 3 ? Vector3.Zero : al > 30f ? acc * (30f / al) : acc;
        var h = _seriesHead;
        _ownS[h] = Ownership; _closeS[h] = ClosureErrorMm;
        for (var s = 0; s < Slots; s++) _loadS[h * Slots + s] = _hold[s] ? _load[s].Length() : 0f;
        _seriesHead = (h + 1) % Series;
        if (_seriesCount < Series) _seriesCount++;
    }

    private void StepHandover(DexProp prop, RealtimeDexterousGraspSynthesisEngine eng, FrictionConeSlipPreventionGuard guard, Vector3 impulse, float dt)
    {
        var cyc = (int)(_t / Cycle);
        var tau = _t - cyc * Cycle;
        if (cyc != _cycle)
        {
            _cycle = cyc; Giver = cyc & 1; Dropped = false;
            for (var s = 0; s < Slots; s++) { guard.ResetSlot(s); eng.SetSlipOffset(s, Vector3.Zero); }
        }
        int g = Giver, r = Receiver;
        for (var s = 0; s < Slots; s++) _sh[s] = ShoulderPos(s, 0f, 0f);
        eng.SetActive(2, false); eng.SetActive(3, false); _hold[2] = _hold[3] = false;
        eng.SetActive(0, true); eng.SetActive(1, true);
        prop.Rotation = Quaternion.Identity;

        // 소품 경로 · 소유권
        Vector3 pos; float alpha;
        if (tau < 1f) { pos = Vector3.Lerp(HoldPos(g), Meet, MinJerk(tau)); alpha = 0f; Phase = tau < 0.4f ? TakeoverPhase.Offer : TakeoverPhase.Reach; }
        else if (tau < 1.8f) { pos = Meet; alpha = Smooth((tau - 1f) / 0.8f); Phase = tau < 1.1f ? TakeoverPhase.Lock : TakeoverPhase.Transfer; }
        else if (tau < 2.2f) { pos = Meet; alpha = 1f; Phase = TakeoverPhase.Release; }
        else if (tau < 3.2f) { pos = Vector3.Lerp(Meet, HoldPos(r), MinJerk((tau - 2.2f) / 1f)); alpha = 1f; Phase = TakeoverPhase.Retract; }
        else { pos = HoldPos(r); alpha = 1f; Phase = TakeoverPhase.Hold; }
        PhaseValue = Math.Clamp(tau, 0f, 1f);
        if (alpha >= 1f && Ownership < 1f && tau < 2.2f) Transfers++;
        Ownership = alpha;
        if (tau >= 1.0f && tau < 2.2f) { MinOwnership = MathF.Min(MinOwnership, alpha); MaxOwnership = MathF.Max(MaxOwnership, alpha); }
        if (Dropped)
        {
            // 낙하(탄도 · 바닥 정지) — 주기 종료 시 주는 쪽 손에서 재시작
            _dropV.Y -= G * dt; _dropP += _dropV * dt;
            if (_dropP.Y < 0.05f) { _dropP.Y = 0.05f; _dropV = Vector3.Zero; }
            pos = _dropP; Phase = TakeoverPhase.Dropped;
        }
        prop.Position = pos; _path = pos;

        // 파지 슬롯(사이트: 액터 A = 0 · B = 1) · 닫힘 블렌드
        var gs = g; var rs = r;
        eng.Synthesize(gs, prop, g, true);
        eng.Synthesize(rs, prop, r, true);
        var gBlend = tau < 1.8f ? 1f : 1f - Smooth((tau - 1.8f) / 0.3f);
        var reachU = Math.Clamp((tau - 0.4f) / 0.6f, 0f, 1f);
        var rBlend = tau < 1f ? Smooth((reachU - 0.6f) / 0.4f) : 1f;
        eng.SetCloseBlend(gs, gBlend); eng.SetCloseBlend(rs, rBlend);
        _hold[gs] = tau < 1.95f && !Dropped; _hold[rs] = tau >= 1f && !Dropped;
        _share[gs] = _hold[gs] ? 1f - alpha : 0f; _share[rs] = _hold[rs] ? alpha : 0f;
        if (!_hold[rs] && tau < 1f) _share[gs] = 1f;

        // 손목 목표(파지 = 템플릿 · 접근/해제 = 보간)
        eng.WristWorld(gs, prop, out var gGrasp, out _);
        eng.WristWorld(rs, prop, out var rGrasp, out _);
        var gT = gGrasp;
        if (tau >= 1.8f)
        {
            var back = prop.DirToWorld(prop.Site(g).Approach) * 0.06f;
            gT = tau < 2.2f ? gGrasp + back * Smooth((tau - 1.8f) / 0.4f) : Vector3.Lerp(gGrasp + back, RestWrist(gs), MinJerk((tau - 2.2f) / 1f));
        }
        var rT = tau < 1f ? Vector3.Lerp(RestWrist(rs), rGrasp, MinJerk(reachU)) : rGrasp;
        if (tau < 0.4f) rT = RestWrist(rs);
        _wrTarget[gs] = gT; _wrTarget[rs] = rT;

        // 위상 잠금 지표(φ → 1 순간 손목 ↔ 파지 위치 · 상대 속도)
        var inReach = tau >= 0.4f && tau < 1f;
        if (inReach) { _objVelPrev = (pos - _pPrev) / dt; _lockWristPrev = rT; }
        if (_wasReach && !inReach)
        {
            Locks++;
            LockErrorMm = (rT - rGrasp).Length() * 1000f;
            LockRelVelMmS = _relVel * 1000f;
        }
        if (inReach) _relVel = ((rT - _lockWristPrev) / dt - _objVelPrev).Length();
        _wasReach = inReach;

        Chain(prop, eng);
        for (var s = 0; s < 2; s++) if (!_hold[s]) Arm(s, _wrTarget[s]);
        Loads(prop, impulse);
    }

    private void StepCarry(DexProp prop, RealtimeDexterousGraspSynthesisEngine eng, Vector3 impulse, float dt)
    {
        Phase = TakeoverPhase.Carry; Ownership = 0.5f; PhaseValue = (_t * 1.7f) % 1f;
        var z = 0.9f * MathF.Sin(MathF.Tau * _t / 6f);
        var gait = MathF.Tau * 1.7f * _t;
        for (var s = 0; s < Slots; s++)
        {
            var a = Actor(s);
            var ph = gait + (a == 0 ? 0f : MathF.PI);   // 반위상 잠금
            var basePos = new Vector3(a == 0 ? -0.67f : 0.67f, 0f, z);
            _root[a] = basePos;   // 상자 바 ±0.5 m
            _sh[s] = ShoulderPos(s, 0.025f * MathF.Sin(2f * ph), 0.015f * MathF.Sin(ph));
        }
        prop.Position = new Vector3(0f, 0.80f, z); _path = prop.Position;
        prop.Rotation = Quaternion.Identity;
        for (var s = 0; s < Slots; s++)
        {
            eng.SetActive(s, true);
            eng.Synthesize(s, prop, s, s < 2);
            eng.SetCloseBlend(s, 1f);
            _hold[s] = true;
        }
        Chain(prop, eng);
        // 하중 분담(최소 노름 의사역)
        var com = prop.CenterOfMass;
        var fy = prop.Mass * (G + ObjectAcceleration.Y);
        float s1 = 0, sx = 0, sz = 0, sxx = 0, szz = 0, sxz = 0;
        Span<float> xs = stackalloc float[Slots], zs = stackalloc float[Slots];
        for (var s = 0; s < Slots; s++)
        {
            xs[s] = _wr[s].X - com.X; zs[s] = _wr[s].Z - com.Z;
            s1 += 1; sx += xs[s]; sz += zs[s]; sxx += xs[s] * xs[s]; szz += zs[s] * zs[s]; sxz += xs[s] * zs[s];
        }
        // M = A Aᵀ = [[s1,sx,sz],[sx,sxx,sxz],[sz,sxz,szz]] · y = M⁻¹ [fy,0,0] → 첫 열 = 여인수/행렬식
        var det = s1 * (sxx * szz - sxz * sxz) - sx * (sx * szz - sxz * sz) + sz * (sx * sxz - sxx * sz);
        var y0 = (sxx * szz - sxz * sxz) / det * fy; var y1 = -(sx * szz - sz * sxz) / det * fy; var y2 = (sx * sxz - sz * sxx) / det * fy;
        for (var s = 0; s < Slots; s++)
        {
            var f = y0 + y1 * xs[s] + y2 * zs[s];
            _share[s] = f / MathF.Max(fy, 1e-3f);
        }
        Loads(prop, impulse);
    }

    /// <summary>폐쇄 체인: 파지 손목(템플릿) 도달 잔차 → 소품 위치/회전 보정 반복 → 팔 IK · 폐쇄 오차.</summary>
    private void Chain(DexProp prop, RealtimeDexterousGraspSynthesisEngine eng)
    {
        var p0 = prop.Position;
        for (var it = 0; it < ChainIter; it++)
        {
            var dp = Vector3.Zero; var w = Vector3.Zero; var rr = 0f; var n = 0;
            for (var s = 0; s < Slots; s++)
            {
                if (!_hold[s]) continue;
                eng.WristWorld(s, prop, out var t, out _);
                var d = t - _sh[s]; var l = d.Length();
                if (l <= Reach * 0.99f) { n++; continue; }
                var v = d / l * (Reach * 0.99f) - d;   // 도달 잔차(손목 → 도달 구)
                var r = t - prop.Position;
                dp += v; w += Vector3.Cross(r, v); rr += r.LengthSquared(); n++;
            }
            if (n == 0 || (dp.LengthSquared() < 1e-10f && w.LengthSquared() < 1e-12f)) break;
            prop.Position += dp / n;
            if (Mode == ManipulationMode.Carry && rr > 1e-6f)
            {
                var om = w / rr;
                var ang = om.Length();
                if (ang > 1e-7f) prop.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(om / ang, ang) * prop.Rotation);
            }
        }
        ChainCorrectionCm = (prop.Position - p0).Length() * 100f;
        var up = Vector3.Transform(Vector3.UnitY, prop.Rotation);
        TiltDeg = MathF.Acos(Math.Clamp(up.Y, -1f, 1f)) * 57.29578f;
        var err = 0f;
        for (var s = 0; s < Slots; s++)
        {
            if (!_hold[s]) continue;
            eng.WristWorld(s, prop, out var t, out _);
            _wrTarget[s] = t;
            var got = Arm(s, t);
            err = MathF.Max(err, (got - t).Length());
        }
        ClosureErrorMm = err * 1000f;
        if (ClosureErrorMm > MaxClosureErrorMm) MaxClosureErrorMm = ClosureErrorMm;
    }

    private void Loads(DexProp prop, Vector3 impulse)
    {
        var need = prop.Mass * (ObjectAcceleration + new Vector3(0f, G, 0f));
        for (var s = 0; s < Slots; s++)
        {
            _load[s] = _hold[s] ? _share[s] * (need - impulse) : Vector3.Zero;   // 손 → 소품 힘 = m(a − g) − 외력
            _static[s] = _hold[s] ? MathF.Max(_share[s], 0.05f) * prop.Mass * G : 0f;
        }
    }

    /// <summary>미끄럼 이탈 → 낙하(현재 위치 · 속도에서 탄도).</summary>
    public void MarkDropped(DexProp prop, float dt)
    {
        if (Dropped) return;
        Dropped = true; Drops++; Phase = TakeoverPhase.Dropped;
        _dropP = prop.Position; _dropV = (prop.Position - _pPrev2) / (2f * dt);
        Array.Clear(_hold);
    }
}
