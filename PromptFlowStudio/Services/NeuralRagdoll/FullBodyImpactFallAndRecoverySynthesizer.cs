using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.NeuralRagdoll;

public enum RecoveryState : byte { Locomotion, HitReaction, Falling, Roll, GetUp, BlendOut }
public enum FallPosture : byte { None, FaceUp, FaceDown, SideLeft, SideRight }

/// <summary>기립 키포즈(루트 로컬 · +Z = 기립 후 정면) — 골반 · 체간/목 피치 · 발목/발끝/손목 목표 · 무릎/팔꿈치 극 벡터.</summary>
public struct GetUpKey
{
    public Vector3 Pelvis, LAnkle, RAnkle, LToe, RToe, LWrist, RWrist, KneePole, ElbowPole;
    public float TorsoDeg, NeckDeg, Seconds;
    /// <summary>사지 끌기(들지 않고 지면 접촉 유지 이동).</summary>
    public bool Drag;
    public string Label;
}

/// <summary>
/// [PromptFlow Studio v17.37.0] 🧎 전신 충격 분산 &amp; 절차적 기립 복원 합성기(<see cref="Update"/> 무할당).
///   ▸ 상태 머신: 보행 → 피격 반응(부분 래그돌 · 월드 목표 복귀) → 전도(전신 래그돌 · 내부 토크 자세 톤) → [누움/측와위 통나무 굴림] → 기립 키포즈 시퀀스 → 보행 블렌드(v17.33 루트 순간 이동).
///   ▸ 전도 판정: 섭동 DCM Δξ = (c_sim − c_kin) + (v_sim − v_kin)/ω₀ 를 명목 CoM 에 더한 점이 발 지지 다각형 밖 12 cm 초과(발 폭 · 보정 스텝 여유) 또는 충격 에너지 ≥ 150 J.
///   ▸ 자세 판독(µs): 골반 쿼터니언 전방축 · 지면 노멀 내적 d — d &gt; 0.5 누움(FaceUp) · d &lt; −0.5 엎드림(FaceDown) · 그 외 좌/우 측와위(우측축 부호).
///     누움/측와위 → 체간축 통나무 굴림(전반부 일자 자세 정렬 · 최단 방향 · 각도 비례 시간 · 프레임별 지면 들림 · 0.25 s 안착)으로 엎드림 정렬.
///   ▸ 기립(엎드림): 손 짚기 준비(현 자세 유지 · 손만 어깨 밑) → 무릎 푸시업 → 네발 → 오른발 전진 → 한쪽 무릎 → 앞발 딛고 일어서기(뒷발 끌기) → 기립 —
///     키포즈 매개변수 최소 저크 보간 · 이동 &gt; 10 cm 사지는 지연 스윙(들림) · 양측 동시 이동 시 좌 → 우 순차 창(= 순차 지지) · 2-본 IK 재구성 · 첫 구간 시작 오프셋 감쇠(무팝).
///   ▸ 지지 반발력: 목표 자세 지면 접촉(손/무릎/발목·발끝 = 발 폭 8 cm/골반/흉부) · ZMP = c − c_y/(g + a_y)·a 를 만족하는 비음수 최소 노름 수직력(능동 집합) · ZMP 지지 다각형 내 비율.
/// </summary>
public sealed class FullBodyImpactFallAndRecoverySynthesizer
{
    public const int N = RagdollSkeleton.Count, SupportCount = 14, MaxPts = 32, MaxKeys = 8;
    public const float Omega0 = 3.2f, FallMargin = 0.12f, FallImpactJ = 150f, SettleKe = 4f, SettleHold = 0.25f, MinFall = 0.6f, MaxFall = 4f;
    public const float RollSeconds = 0.6f, RollSettle = 0.25f, BlendSeconds = 0.4f, GetUpFloor = 0.4f, Delay = 0.35f, SwingDist = 0.1f, ContactTol = 0.03f, UprightPelvis = 0.65f;
    public const float ProneBack = 0.8f, FootHalfWidth = 0.04f, PlaceSeconds = 0.6f;

    private static readonly byte[] SupportJ = { 10, 15, 17, 21, 18, 22, 19, 23, 0, 2, 16, 20, 7, 12 };
    private static readonly string[] SupportNames = { "L 손", "R 손", "L 무릎", "R 무릎", "L 발목", "R 발목", "L 발끝", "R 발끝", "골반", "흉부", "L 엉덩이", "R 엉덩이", "L 어깨", "R 어깨" };

    private static readonly GetUpKey[] Prone =
    {
        Key("🤲 무릎 푸시업", 0.8f, new(0f, 0.30f, -0.81f), 59f, -20f, new(-0.1f, 0.07f, -1.64f), new(-0.1f, 0.02f, -1.75f), new(0.1f, 0.07f, -1.64f), new(0.1f, 0.02f, -1.75f), new(-0.19f, 0.06f, -0.30f), new(0.19f, 0.06f, -0.30f), new(0f, -1f, 0.3f), new(0f, 0f, -1f)),
        Key("🐾 네발 무릎 꿇기", 0.8f, new(0f, 0.50f, -0.75f), 80f, -35f, new(-0.1f, 0.07f, -1.19f), new(-0.1f, 0.02f, -1.30f), new(0.1f, 0.07f, -1.19f), new(0.1f, 0.02f, -1.30f), new(-0.19f, 0.06f, -0.30f), new(0.19f, 0.06f, -0.30f), new(0f, 0.2f, 1f), new(0f, 0f, -1f)),
        Key("🦶 오른발 전진 디딤", 0.6f, new(-0.04f, 0.48f, -0.70f), 78f, -30f, new(-0.1f, 0.07f, -1.19f), new(-0.1f, 0.02f, -1.30f), new(0.08f, 0.08f, -0.40f), new(0.08f, 0.02f, -0.26f), new(-0.19f, 0.06f, -0.30f), new(0.19f, 0.06f, -0.30f), new(0f, 0.2f, 1f), new(0f, 0f, -1f)),
        Key("🧎 한쪽 무릎 세우기", 0.9f, new(-0.02f, 0.52f, -0.70f), 15f, 0f, new(-0.1f, 0.12f, -1.19f), new(-0.1f, 0.02f, -1.10f), new(0.08f, 0.08f, -0.40f), new(0.08f, 0.02f, -0.26f), new(-0.25f, 0.55f, -0.62f), new(0.2f, 0.62f, -0.38f), new(0f, 0.2f, 1f), new(0f, 0f, -1f)),
        Key("🦵 앞발 딛고 일어서기", 1.1f, new(0.06f, 0.88f, -0.36f), 12f, 0f, new(-0.1f, 0.08f, -0.62f), new(-0.1f, 0.02f, -0.48f), new(0.08f, 0.08f, -0.40f), new(0.08f, 0.02f, -0.26f), new(-0.15f, 0.82f, -0.22f), new(0.27f, 0.82f, -0.22f), new(0f, 0.2f, 1f), new(0f, 0f, -1f), drag: true),
        Key("🧍 기립", 0.8f, new(0.05f, BipedSkeleton.StandPelvis, -0.36f), 0f, 0f, new(-0.1f, 0.08f, -0.40f), new(-0.1f, 0.02f, -0.26f), new(0.08f, 0.08f, -0.40f), new(0.08f, 0.02f, -0.26f), new(-0.14f, 0.91f, -0.308f), new(0.24f, 0.91f, -0.308f), new(0f, 0f, 1f), new(0f, 0f, -1f)),
    };

    private readonly Vector3[] _target = new Vector3[N], _p0 = new Vector3[N], _roll = new Vector3[N], _tuck = new Vector3[N], _offset = new Vector3[N], _stand = new Vector3[N];
    private readonly float[] _force = new float[SupportCount];
    private readonly byte[] _order = new byte[SupportCount];
    private readonly Vector2[] _hull = new Vector2[MaxPts];
    private readonly GetUpKey[] _keys = new GetUpKey[MaxKeys];
    private GetUpKey _from;
    private int _keyN = Prone.Length + 1;
    private Vector3 _origin, _rollPivot, _cKinPrev, _cPrev, _cPrev2, _acc, _teleOrigin;
    private float _rollSeconds = RollSeconds;
    private Quaternion _rollQ = Quaternion.Identity;
    private float _yaw, _t, _settle, _fallClock, _teleYaw;
    private int _phase, _hullN, _orderN, _cN;
    private bool _kinInit, _teleport;
    private uint _supportMask, _everMask;

    public FullBodyImpactFallAndRecoverySynthesizer() => RagdollSkeleton.Rest.AsSpan().CopyTo(_target);

    public Vector3 GroundNormal { get; set; } = Vector3.UnitY;
    public RecoveryState State { get; private set; }
    public FallPosture Posture { get; private set; }
    public FallPosture LastPosture { get; private set; }
    public float StateTime => _t;
    public int PhaseIndex => _phase;
    public int PhaseCount => _keyN;
    public string PhaseLabel => State == RecoveryState.GetUp && _phase < _keyN ? _keys[_phase].Label ?? "" : State == RecoveryState.Roll ? "🔄 통나무 굴림" : StateName(State);
    public float RollDeg { get; private set; }
    public static int SequenceLength => Prone.Length + 1;
    public static string SequenceLabel(int i) => i == 0 ? "✋ 손 짚기 준비" : Prone[i - 1].Label;
    public float Progress { get; private set; }
    public ReadOnlySpan<Vector3> Target => _target;
    public float DcmOutsideCm { get; private set; }
    public float MaxDcmOutsideCm { get; private set; }
    public Vector2 Zmp { get; private set; }
    public Vector3 TargetCom { get; private set; }
    public bool ZmpInside { get; private set; }
    public float SupportForce(int i) => _force[i];
    public bool SupportActive(int i) => (_supportMask & (1u << i)) != 0;
    public static string SupportName(int i) => SupportNames[i];
    public static int SupportJoint(int i) => SupportJ[i];
    public int OrderCount => _orderN;
    public int OrderAt(int i) => _order[i];
    public int HullCount => _hullN;
    public Vector2 HullPoint(int i) => _hull[i];

    public long Falls { get; private set; }
    public long GetUps { get; private set; }
    public long HitReactions { get; private set; }
    public long FaceUps { get; private set; }
    public long FaceDowns { get; private set; }
    public long Sides { get; private set; }
    public long Rolls { get; private set; }
    public long UprightLandings { get; private set; }
    public double LastClassifyUs { get; private set; }
    public double MaxClassifyUs { get; private set; }
    public long GetUpFrames { get; private set; }
    public long StableFrames { get; private set; }
    public float StablePct => GetUpFrames == 0 ? 100f : StableFrames * 100f / GetUpFrames;
    public float MaxSupportN { get; private set; }
    public float SupportResidualN { get; private set; }
    public float LastGetUpSeconds { get; private set; }
    public float MaxGetUpSeconds { get; private set; }

    public static string StateName(RecoveryState s) => s switch
    {
        RecoveryState.Locomotion => "🚶 보행(키네마틱)", RecoveryState.HitReaction => "💢 피격 반응", RecoveryState.Falling => "🪂 전도(래그돌)",
        RecoveryState.Roll => "🔄 굴림", RecoveryState.GetUp => "🧎 기립", _ => "〰 보행 블렌드",
    };

    public static string PostureName(FallPosture p) => p switch
    {
        FallPosture.FaceUp => "누움(Face-up)", FallPosture.FaceDown => "엎드림(Face-down)", FallPosture.SideLeft => "좌측와위", FallPosture.SideRight => "우측와위", _ => "—",
    };

    public void ResetMetrics()
    {
        Falls = GetUps = HitReactions = FaceUps = FaceDowns = Sides = Rolls = UprightLandings = GetUpFrames = StableFrames = 0;
        MaxClassifyUs = 0; MaxSupportN = 0f; MaxGetUpSeconds = 0f; MaxDcmOutsideCm = 0f;
    }

    public void Reset()
    {
        State = RecoveryState.Locomotion; Posture = FallPosture.None; _t = 0f; _teleport = false; _kinInit = false; _cN = 0;
        Array.Clear(_force); _supportMask = 0; _orderN = 0; Progress = 0f;
    }

    /// <summary>기립 완료 → v17.33 모션 루트 순간 이동 요청(1 회 소비).</summary>
    public bool ConsumeTeleport(out Vector3 origin, out float yaw)
    {
        origin = _teleOrigin; yaw = _teleYaw;
        if (!_teleport) return false;
        _teleport = false;
        return true;
    }

    /// <summary>충격 주입 알림 — 보행 중이면 피격 반응 진입 · 고에너지 즉시 전도.</summary>
    public void OnImpulse(RealtimeHybridNeuralRagdollEngine e, float energyJ)
    {
        if (State == RecoveryState.Locomotion) { State = RecoveryState.HitReaction; _t = 0f; HitReactions++; }
        if (energyJ >= FallImpactJ && State is RecoveryState.HitReaction or RecoveryState.BlendOut) BeginFall();
        else if (State is RecoveryState.GetUp or RecoveryState.Roll && energyJ >= FallImpactJ * 0.3f) BeginFall();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    /// <summary>엔진 제어(하한 · 추종 이득) · 목표 자세 산출 — kin = v17.33 모션 24 관절(월드).</summary>
    public void Update(RealtimeHybridNeuralRagdollEngine e, ReadOnlySpan<Vector3> kin, float dt)
    {
        _t += dt;
        if (State != RecoveryState.Locomotion) _fallClock += dt;
        var cKin = RagdollSkeleton.CenterOfMass(kin);
        var vKin = _kinInit ? (cKin - _cKinPrev) / dt : Vector3.Zero;
        _cKinPrev = cKin; _kinInit = true;
        switch (State)
        {
            case RecoveryState.Locomotion:
                e.Floor = 0f; e.FollowGain = 1f;
                kin.CopyTo(_target);
                DcmOutsideCm = 0f;
                if (e.MaxWeight > 0.02f) { State = RecoveryState.HitReaction; _t = 0f; HitReactions++; }
                _fallClock = 0f;
                break;
            case RecoveryState.HitReaction:
            {
                e.Floor = 0f; e.FollowGain = 0f;
                kin.CopyTo(_target);
                var dxi = (e.CenterOfMass - cKin) + (e.ComVelocity - vKin) / Omega0;
                var p = new Vector2(cKin.X + dxi.X, cKin.Z + dxi.Z);
                Span<Vector2> pts = stackalloc Vector2[4];
                var n = 0;
                for (var k = 0; k < 4; k++)
                {
                    var j = k switch { 0 => 18, 1 => 19, 2 => 22, _ => 23 };
                    if (kin[j].Y < 0.15f) pts[n++] = new Vector2(kin[j].X, kin[j].Z);
                }
                Span<Vector2> hull = stackalloc Vector2[4];
                var hn = ConvexHull(pts[..n], hull);
                var outside = hn == 0 ? 1f : OutsideDistance(p, hull[..hn]);
                DcmOutsideCm = outside * 100f;
                if (DcmOutsideCm > MaxDcmOutsideCm) MaxDcmOutsideCm = DcmOutsideCm;
                if (outside > FallMargin) { BeginFall(); break; }
                if (e.MaxWeight < 0.01f && _t > 0.2f) { State = RecoveryState.Locomotion; _t = 0f; _fallClock = 0f; }
                break;
            }
            case RecoveryState.Falling:
                e.Floor = 1f; e.FollowGain = 1f;
                kin.CopyTo(_target);
                _settle = e.KineticEnergy < SettleKe ? _settle + dt : 0f;
                if ((_t > MinFall && _settle > SettleHold) || _t > MaxFall) Classify(e);
                break;
            case RecoveryState.Roll:
            {
                e.Floor = 0.7f; e.FollowGain = 0f;
                var s = MinJerk(_t / _rollSeconds);
                var q = Quaternion.Slerp(Quaternion.Identity, _rollQ, s);
                var lift = 0f;
                for (var j = 0; j < N; j++)
                {
                    // 굴림 전반부에 일자(통나무) 자세로 정렬 — 팔 몸통 옆 · 다리 신전(몸 밑 끼임/버팀 방지)
                    var src = Vector3.Lerp(_p0[j], _tuck[j], MinJerk(_t / (0.5f * _rollSeconds)));
                    _target[j] = _rollPivot + Vector3.Transform(src - _rollPivot, q);
                    lift = MathF.Max(lift, RagdollSkeleton.ParticleRadius[j] - _target[j].Y);
                }
                for (var j = 0; j < N; j++) _target[j].Y += lift;
                Support(dt);
                if (_t >= _rollSeconds + RollSettle)
                {
                    // 굴림 종료 = 지면에 안착한 시뮬 자세에서 기립 시작
                    for (var j = 0; j < N; j++) _p0[j] = e.Position(j);
                    BeginGetUp(_p0);
                }
                break;
            }
            case RecoveryState.GetUp:
            {
                var total = 0f; var done = 0f;
                for (var k = 0; k < _keyN; k++) { total += _keys[k].Seconds; if (k < _phase) done += _keys[k].Seconds; }
                ref readonly var b = ref _keys[_phase];
                var u = Math.Clamp(_t / b.Seconds, 0f, 1f);
                Progress = (done + u * b.Seconds) / total;
                e.Floor = GetUpFloor * (1f - Progress); e.FollowGain = 0f;
                Interpolate(_phase == 0 ? _from : _keys[_phase - 1], b, u, out var key);
                Build(in key, _origin, _yaw, _target);
                if (_phase == 0)
                {
                    var w = 1f - MinJerk(u);
                    for (var j = 0; j < N; j++) _target[j] += _offset[j] * w;
                }
                Support(dt);
                if (_t >= b.Seconds)
                {
                    _phase++; _t = 0f;
                    if (_phase >= _keyN)
                    {
                        _phase = _keyN - 1;
                        _target.AsSpan().CopyTo(_stand);
                        ref readonly var last = ref _keys[_keyN - 1];
                        _teleOrigin = BipedSkeleton.ToWorld(new Vector3(0.5f * (last.LAnkle.X + last.RAnkle.X), 0f, 0.5f * (last.LAnkle.Z + last.RAnkle.Z)), _origin, _yaw);
                        _teleYaw = _yaw; _teleport = true;
                        State = RecoveryState.BlendOut; Progress = 1f;
                    }
                }
                break;
            }
            case RecoveryState.BlendOut:
            {
                e.Floor = 0f; e.FollowGain = 0f;
                var s = MinJerk(_t / BlendSeconds);
                for (var j = 0; j < N; j++) _target[j] = _stand[j] + (kin[j] - _stand[j]) * s;
                if (_t >= BlendSeconds)
                {
                    State = RecoveryState.Locomotion; _t = 0f; GetUps++;
                    LastGetUpSeconds = _fallClock;
                    if (_fallClock > MaxGetUpSeconds) MaxGetUpSeconds = _fallClock;
                    _fallClock = 0f; Array.Clear(_force); _supportMask = 0;
                }
                break;
            }
        }
    }

    private void BeginFall()
    {
        State = RecoveryState.Falling; _t = 0f; _settle = 0f; Falls++; Posture = FallPosture.None; Progress = 0f;
    }

    /// <summary>자세 판독(골반 쿼터니언 · 지면 노멀) → 굴림/기립 시퀀스 선택.</summary>
    private void Classify(RealtimeHybridNeuralRagdollEngine e)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var j = 0; j < N; j++) _p0[j] = e.Position(j);
        var q = e.PelvisRotation;
        var n = GroundNormal;
        var fwd = Vector3.Transform(Vector3.UnitZ, q);
        var d = Vector3.Dot(fwd, n);
        var side = Vector3.Dot(Vector3.Transform(Vector3.UnitX, q), n);
        Posture = d > 0.5f ? FallPosture.FaceUp : d < -0.5f ? FallPosture.FaceDown : side > 0f ? FallPosture.SideLeft : FallPosture.SideRight;
        LastClassifyUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (LastClassifyUs > MaxClassifyUs) MaxClassifyUs = LastClassifyUs;
        LastPosture = Posture;
        if (Posture == FallPosture.FaceUp) FaceUps++; else if (Posture == FallPosture.FaceDown) FaceDowns++; else Sides++;
        _orderN = 0; _everMask = 0; _cN = 0;
        if (_p0[0].Y > UprightPelvis)
        {
            // 직립 착지(비틀거림 회복) — 기립 시퀀스 생략
            UprightLandings++;
            _p0.AsSpan().CopyTo(_stand);
            var f = new Vector3(fwd.X, 0f, fwd.Z);
            _teleYaw = f.LengthSquared() > 1e-6f ? MathF.Atan2(f.X, f.Z) : 0f;
            _teleOrigin = new Vector3(_p0[0].X, 0f, _p0[0].Z); _teleport = true;
            State = RecoveryState.BlendOut; _t = 0f; Progress = 1f;
            return;
        }
        var axis = BipedSkeleton.Dir(_p0[3] - _p0[0], Vector3.UnitZ);
        var target = -n;
        var fp = fwd - axis * Vector3.Dot(axis, fwd);
        var tp = target - axis * Vector3.Dot(axis, target);
        var ang = fp.LengthSquared() > 1e-6f && tp.LengthSquared() > 1e-6f ? MathF.Atan2(Vector3.Dot(Vector3.Cross(fp, tp), axis), Vector3.Dot(fp, tp)) : 0f;
        RollDeg = ang * 57.29578f;
        if (MathF.Abs(ang) > 20f / 57.29578f)
        {
            _rollQ = Quaternion.CreateFromAxisAngle(axis, ang);
            LogPose(_p0, axis, _tuck);
            _rollSeconds = 0.3f + RollSeconds * MathF.Abs(ang) / MathF.PI;
            _rollPivot = _p0[0];
            var lift = 0f;
            for (var j = 0; j < N; j++)
            {
                _roll[j] = _rollPivot + Vector3.Transform(_p0[j] - _rollPivot, _rollQ);
                lift = MathF.Max(lift, RagdollSkeleton.ParticleRadius[j] - _roll[j].Y);
            }
            for (var j = 0; j < N; j++) _roll[j].Y += lift;
            Rolls++;
            State = RecoveryState.Roll; _t = 0f;
            return;
        }
        BeginGetUp(_p0);
    }

    /// <summary>통나무 자세(골반 기준 체간축 일자 · 팔 몸통 옆 · 다리 신전 · 발등 펴짐) — 굴림 정렬 목표.</summary>
    private static void LogPose(ReadOnlySpan<Vector3> p0, Vector3 up, Span<Vector3> o)
    {
        var lat = p0[20] - p0[16];
        lat -= up * Vector3.Dot(up, lat);
        lat = BipedSkeleton.Dir(lat, Vector3.Cross(up, Vector3.UnitY));
        var front = Vector3.Cross(lat, up);
        var pel = p0[0];
        o[0] = pel; o[1] = pel + up * 0.18f; o[2] = pel + up * 0.40f; o[3] = pel + up * 0.49f; o[4] = o[3] + up * 0.16f; o[5] = o[4] + up * RagdollSkeleton.HeadTopLen;
        for (var sd = 0; sd < 2; sd++)
        {
            var sx = sd == 0 ? -1f : 1f;
            int a = sd == 0 ? 6 : 11, l = sd == 0 ? 16 : 20;
            o[a] = o[3] + lat * (sx * 0.07f);
            o[a + 1] = o[3] + lat * (sx * BipedSkeleton.ShoulderW);
            o[a + 2] = o[a + 1] + lat * (sx * 0.04f) - up * BipedSkeleton.UpperArm;
            o[a + 3] = o[a + 2] - up * BipedSkeleton.Forearm;
            o[a + 4] = o[a + 3] - up * RagdollSkeleton.HandLen;
            o[l] = pel + lat * (sx * BipedSkeleton.HipW);
            o[l + 1] = o[l] - up * BipedSkeleton.Thigh;
            o[l + 2] = o[l + 1] - up * BipedSkeleton.Shin;
            o[l + 3] = o[l + 2] - up * (BipedSkeleton.FootLen * 0.9f) + front * (BipedSkeleton.FootLen * 0.44f);
        }
    }

    private void BeginGetUp(Span<Vector3> pose)
    {
        // 시작 자세 접지(최저 캡슐이 지면에 닿도록 하강 — 굴림/충돌 후 공중 잔류 제거)
        var drop = float.PositiveInfinity;
        for (var j = 0; j < N; j++) drop = MathF.Min(drop, pose[j].Y - RagdollSkeleton.ParticleRadius[j]);
        if (drop > 0f) for (var j = 0; j < N; j++) pose[j].Y -= drop;
        var ax = pose[3] - pose[0];
        var h = new Vector3(ax.X, 0f, ax.Z);
        var facing = h.LengthSquared() > 1e-6f ? Vector3.Normalize(h) : Vector3.UnitZ;
        _yaw = MathF.Atan2(facing.X, facing.Z);
        _origin = new Vector3(pose[0].X, 0f, pose[0].Z) + facing * ProneBack;
        _from = Fit(pose, _origin, _yaw);
        // 손 짚기 준비 = 현 자세 유지 · 손목만 첫 키(어깨 밑)
        _keys[0] = _from; _keys[0].LWrist = Prone[0].LWrist; _keys[0].RWrist = Prone[0].RWrist; _keys[0].Seconds = PlaceSeconds; _keys[0].Label = "✋ 손 짚기 준비"; _keys[0].Drag = false;
        for (var k = 0; k < Prone.Length; k++) _keys[k + 1] = Prone[k];
        _keyN = Prone.Length + 1;
        Span<Vector3> built = stackalloc Vector3[N];
        Build(in _from, _origin, _yaw, built);
        for (var j = 0; j < N; j++) _offset[j] = pose[j] - built[j];
        _phase = 0; _t = 0f; State = RecoveryState.GetUp;
    }

    /// <summary>임의 자세 → 시작 키 매개변수(루트 로컬).</summary>
    private static GetUpKey Fit(ReadOnlySpan<Vector3> pose, Vector3 origin, float yaw)
    {
        Span<Vector3> l = stackalloc Vector3[N];
        for (var j = 0; j < N; j++) l[j] = BipedSkeleton.ToLocal(pose[j], origin, yaw);
        var tv = l[3] - l[0]; var hv = l[4] - l[3];
        var torso = MathF.Atan2(tv.Z, tv.Y) * 57.29578f;
        var neck = BipedSkeleton.WrapPi((MathF.Atan2(hv.Z, hv.Y) * 57.29578f - torso) / 57.29578f) * 57.29578f;
        return new GetUpKey
        {
            Pelvis = l[0], TorsoDeg = torso, NeckDeg = neck,
            LAnkle = l[18], LToe = l[19], RAnkle = l[22], RToe = l[23], LWrist = l[9], RWrist = l[14],
            KneePole = new Vector3(0f, -1f, 0.3f), ElbowPole = -Vector3.UnitZ, Label = "",
        };
    }

    /// <summary>키 보간(최소 저크 · 이동 사지 지연 스윙 + 들림).</summary>
    private static void Interpolate(in GetUpKey a, in GetUpKey b, float u, out GetUpKey k)
    {
        var s = MinJerk(u);
        k = b;
        k.Pelvis = Vector3.Lerp(a.Pelvis, b.Pelvis, s);
        k.TorsoDeg = a.TorsoDeg + (b.TorsoDeg - a.TorsoDeg) * s;
        k.NeckDeg = a.NeckDeg + (b.NeckDeg - a.NeckDeg) * s;
        k.KneePole = BipedSkeleton.Dir(Vector3.Lerp(a.KneePole, b.KneePole, s), b.KneePole);
        k.ElbowPole = BipedSkeleton.Dir(Vector3.Lerp(a.ElbowPole, b.ElbowPole, s), b.ElbowPole);
        // 스윙 사지(이동 > 10 cm · 끌기 아님): 양측 동시면 좌 → 우 순차 창 — 한 번에 한 사지만 지지 이탈
        var drag = b.Drag;
        bool ls = !drag && Swing(a.LAnkle, b.LAnkle), rs = !drag && Swing(a.RAnkle, b.RAnkle), lw = !drag && Swing(a.LWrist, b.LWrist), rw = !drag && Swing(a.RWrist, b.RWrist);
        const float mid = Delay + (1f - Delay) * 0.5f;
        Limb(a.LAnkle, b.LAnkle, a.LToe, b.LToe, u, s, ls, Delay, rs ? mid : 1f, out k.LAnkle, out k.LToe);
        Limb(a.RAnkle, b.RAnkle, a.RToe, b.RToe, u, s, rs, ls ? mid : Delay, 1f, out k.RAnkle, out k.RToe);
        Limb(a.LWrist, b.LWrist, a.LWrist, b.LWrist, u, s, lw, Delay, rw ? mid : 1f, out k.LWrist, out _);
        Limb(a.RWrist, b.RWrist, a.RWrist, b.RWrist, u, s, rw, lw ? mid : Delay, 1f, out k.RWrist, out _);
    }

    private static bool Swing(Vector3 a, Vector3 b) => new Vector2(b.X - a.X, b.Z - a.Z).Length() > SwingDist;

    private static void Limb(Vector3 a0, Vector3 b0, Vector3 a1, Vector3 b1, float u, float s, bool swing, float w0, float w1, out Vector3 o0, out Vector3 o1)
    {
        var lift = 0f;
        if (swing)
        {
            var dh = new Vector2(b0.X - a0.X, b0.Z - a0.Z).Length();
            s = MinJerk(Math.Clamp((u - w0) / (w1 - w0), 0f, 1f));
            lift = 4f * s * (1f - s) * MathF.Min(0.15f, 0.5f * dh);
        }
        var up = new Vector3(0f, lift, 0f);
        o0 = Vector3.Lerp(a0, b0, s) + up; o1 = Vector3.Lerp(a1, b1, s) + up;
    }

    /// <summary>키 → 24 관절(월드) — 체간 피치 · 2-본 IK 팔/다리 · 도달 클램프.</summary>
    public static void Build(in GetUpKey k, Vector3 origin, float yaw, Span<Vector3> o)
    {
        Span<Vector3> p = stackalloc Vector3[N];
        var th = k.TorsoDeg / 57.29578f; var nh = (k.TorsoDeg + k.NeckDeg) / 57.29578f;
        var up = new Vector3(0f, MathF.Cos(th), MathF.Sin(th));
        var hd = new Vector3(0f, MathF.Cos(nh), MathF.Sin(nh));
        var pel = k.Pelvis;
        p[0] = pel; p[1] = pel + up * 0.18f; p[2] = pel + up * 0.40f; p[3] = pel + up * 0.49f;
        p[4] = p[3] + hd * 0.16f; p[5] = p[4] + hd * RagdollSkeleton.HeadTopLen;
        const float reachArm = BipedSkeleton.UpperArm + BipedSkeleton.Forearm - 0.001f, reachLeg = BipedSkeleton.Thigh + BipedSkeleton.Shin - 0.001f;
        for (var s = 0; s < 2; s++)
        {
            var sx = s == 0 ? -1f : 1f;
            int a = s == 0 ? 6 : 11, l = s == 0 ? 16 : 20;
            var sh = p[3] + new Vector3(sx * BipedSkeleton.ShoulderW, 0f, 0f);
            p[a] = p[3] + (sh - p[3]) * RagdollSkeleton.ClavT; p[a + 1] = sh;
            var w = s == 0 ? k.LWrist : k.RWrist;
            var dw = w - sh; var lw = dw.Length();
            if (lw > reachArm) w = sh + dw * (reachArm / lw);
            var el = BipedSkeleton.TwoBone(sh, w, k.ElbowPole + new Vector3(sx * 0.3f, 0f, 0f), BipedSkeleton.UpperArm, BipedSkeleton.Forearm);
            p[a + 2] = el; p[a + 3] = w;
            var f = BipedSkeleton.Dir(w - el, -Vector3.UnitY);
            if (w.Y < 0.12f) { var fh = new Vector3(f.X, 0f, f.Z); if (fh.LengthSquared() > 1e-4f) f = Vector3.Normalize(fh); }
            p[a + 4] = w + f * RagdollSkeleton.HandLen;
            var hip = pel + new Vector3(sx * BipedSkeleton.HipW, 0f, 0f);
            p[l] = hip;
            var an = s == 0 ? k.LAnkle : k.RAnkle;
            var da = an - hip; var la = da.Length();
            if (la > reachLeg) an = hip + da * (reachLeg / la);
            p[l + 1] = BipedSkeleton.TwoBone(hip, an, k.KneePole, BipedSkeleton.Thigh, BipedSkeleton.Shin);
            p[l + 2] = an;
            p[l + 3] = an + BipedSkeleton.Dir((s == 0 ? k.LToe : k.RToe) - an, Vector3.UnitZ) * BipedSkeleton.FootLen;
        }
        for (var j = 0; j < N; j++) o[j] = BipedSkeleton.ToWorld(p[j], origin, yaw);
    }

    /// <summary>지지 접촉 · ZMP · 비음수 최소 노름 수직 반발력(능동 집합) · 순차 지지 순서.</summary>
    private void Support(float dt)
    {
        var c = RagdollSkeleton.CenterOfMass(_target);
        TargetCom = c;
        if (_cN >= 2)
        {
            var a = (c - 2f * _cPrev + _cPrev2) / (dt * dt);
            _acc = Vector3.Lerp(_acc, a, 0.3f);
        }
        else _acc = Vector3.Zero;
        _cPrev2 = _cPrev; _cPrev = c; if (_cN < 2) _cN++;
        var g = RealtimeHybridNeuralRagdollEngine.G;
        var ay = MathF.Max(_acc.Y, -0.8f * g);
        var fz = RagdollSkeleton.TotalMass * (g + ay);
        var zmp = new Vector2(c.X - c.Y / (g + ay) * _acc.X, c.Z - c.Y / (g + ay) * _acc.Z);
        Zmp = zmp;
        Span<Vector2> pts = stackalloc Vector2[MaxPts];
        Span<int> idx = stackalloc int[MaxPts];
        var n = 0; _supportMask = 0;
        for (var i = 0; i < SupportCount; i++)
        {
            _force[i] = 0f;
            var j = SupportJ[i];
            if (_target[j].Y - RagdollSkeleton.ParticleRadius[j] > ContactTol) continue;
            _supportMask |= 1u << i;
            var pj = new Vector2(_target[j].X, _target[j].Z);
            if (j is 18 or 19 or 22 or 23)
            {
                // 발 폭(발목→발끝 축 수직 ±4 cm)
                var an = j is 18 or 19 ? 18 : 22;
                var fd = new Vector2(_target[an + 1].X - _target[an].X, _target[an + 1].Z - _target[an].Z);
                var fl = fd.Length();
                var lat = fl > 1e-4f ? new Vector2(-fd.Y, fd.X) * (FootHalfWidth / fl) : new Vector2(FootHalfWidth, 0f);
                pts[n] = pj + lat; idx[n] = i; n++;
                pts[n] = pj - lat; idx[n] = i; n++;
            }
            else { pts[n] = pj; idx[n] = i; n++; }
        }
        _hullN = ConvexHull(pts[..n], _hull);
        _hullN = Math.Min(_hullN, _hull.Length);
        ZmpInside = _hullN >= 3 ? OutsideDistance(zmp, _hull.AsSpan(0, _hullN)) <= 0.005f : _hullN > 0 && OutsideDistance(zmp, _hull.AsSpan(0, _hullN)) <= 0.02f;
        if (State == RecoveryState.GetUp) { GetUpFrames++; if (ZmpInside) StableFrames++; }
        if (n == 0) { SupportResidualN = fz; return; }
        Span<bool> act = stackalloc bool[MaxPts];
        Span<float> f = stackalloc float[MaxPts];
        for (var i = 0; i < n; i++) act[i] = true;
        for (var iter = 0; iter < n; iter++)
        {
            double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            for (var i = 0; i < n; i++)
            {
                if (!act[i]) continue;
                double x = pts[i].X - zmp.X, z = pts[i].Y - zmp.Y;
                m00 += 1; m01 += x; m02 += z; m11 += x * x; m12 += x * z; m22 += z * z;
            }
            var reg = 1e-6 * (m00 + m11 + m22) + 1e-9;
            m00 += reg; m11 += reg; m22 += reg;
            // M λ = (F, 0, 0)(ZMP 원점 모멘트 0)
            var det = m00 * (m11 * m22 - m12 * m12) - m01 * (m01 * m22 - m12 * m02) + m02 * (m01 * m12 - m11 * m02);
            if (Math.Abs(det) < 1e-18) break;
            var l0 = fz * (m11 * m22 - m12 * m12) / det;
            var l1 = -fz * (m01 * m22 - m02 * m12) / det;
            var l2 = fz * (m01 * m12 - m02 * m11) / det;
            var neg = false;
            for (var i = 0; i < n; i++)
            {
                if (!act[i]) { f[i] = 0f; continue; }
                f[i] = (float)(l0 + l1 * (pts[i].X - zmp.X) + l2 * (pts[i].Y - zmp.Y));
                if (f[i] < 0f) { act[i] = false; f[i] = 0f; neg = true; }
            }
            if (!neg) break;
        }
        float sum = 0f, mx = 0f, my = 0f;
        for (var i = 0; i < n; i++)
        {
            _force[idx[i]] += f[i];
            sum += f[i]; mx += f[i] * (pts[i].X - zmp.X); my += f[i] * (pts[i].Y - zmp.Y);
        }
        for (var i = 0; i < SupportCount; i++)
        {
            var fi = _force[i];
            if (fi > MaxSupportN) MaxSupportN = fi;
            var bit = 1u << i;
            if (fi > 0.05f * fz && (_everMask & bit) == 0 && _orderN < SupportCount) { _everMask |= bit; _order[_orderN++] = (byte)i; }
        }
        SupportResidualN = MathF.Abs(sum - fz) + MathF.Sqrt(mx * mx + my * my) / 0.1f;
    }

    // ══════════════════════ 기하 유틸(무할당) ══════════════════════

    public static float MinJerk(float u) { u = Math.Clamp(u, 0f, 1f); return u * u * u * (10f + u * (-15f + 6f * u)); }

    /// <summary>2D 볼록 껍질(단조 사슬 · 반시계) — n ≤ 24.</summary>
    public static int ConvexHull(ReadOnlySpan<Vector2> src, Span<Vector2> hull)
    {
        var n = src.Length;
        if (n == 0) return 0;
        Span<Vector2> p = stackalloc Vector2[MaxPts];
        n = Math.Min(n, MaxPts);
        for (var i = 0; i < n; i++)
        {
            var v = src[i]; var k = i;
            while (k > 0 && (p[k - 1].X > v.X || (p[k - 1].X == v.X && p[k - 1].Y > v.Y))) { p[k] = p[k - 1]; k--; }
            p[k] = v;
        }
        if (n < 3) { for (var i = 0; i < n; i++) hull[i] = p[i]; return n; }
        Span<Vector2> h = stackalloc Vector2[MaxPts * 2];
        var m = 0;
        for (var i = 0; i < n; i++) { while (m >= 2 && Cross(h[m - 2], h[m - 1], p[i]) <= 1e-9f) m--; h[m++] = p[i]; }
        for (int i = n - 2, lo = m + 1; i >= 0; i--) { while (m >= lo && Cross(h[m - 2], h[m - 1], p[i]) <= 1e-9f) m--; h[m++] = p[i]; }
        m = Math.Max(1, m - 1);
        var cnt = Math.Min(m, hull.Length);
        for (var i = 0; i < cnt; i++) hull[i] = h[i];
        return cnt;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>다각형(반시계) 밖 거리(내부 0) — 점/선분 퇴화 포함.</summary>
    public static float OutsideDistance(Vector2 p, ReadOnlySpan<Vector2> hull)
    {
        var n = hull.Length;
        if (n == 0) return float.PositiveInfinity;
        if (n == 1) return (p - hull[0]).Length();
        var inside = n >= 3;
        var best = float.PositiveInfinity;
        for (var i = 0; i < n; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % n];
            if (n >= 3 && Cross(a, b, p) < 0f) inside = false;
            var ab = b - a; var l2 = ab.LengthSquared();
            var t = l2 > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
            var d = (p - (a + ab * t)).Length();
            if (d < best) best = d;
            if (n == 2) break;
        }
        return inside ? 0f : best;
    }

    private static GetUpKey Key(string label, float sec, Vector3 pelvis, float torso, float neck, Vector3 la, Vector3 lt, Vector3 ra, Vector3 rt, Vector3 lw, Vector3 rw, Vector3 kneePole, Vector3 elbowPole, bool drag = false) => new()
    {
        Label = label, Drag = drag, Seconds = sec, Pelvis = pelvis, TorsoDeg = torso, NeckDeg = neck, LAnkle = la, LToe = lt, RAnkle = ra, RToe = rt, LWrist = lw, RWrist = rw, KneePole = kneePole, ElbowPole = elbowPole,
    };
}
