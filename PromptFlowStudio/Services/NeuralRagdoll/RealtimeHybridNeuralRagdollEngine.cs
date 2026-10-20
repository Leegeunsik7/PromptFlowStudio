using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.NeuralRagdoll;

public enum RagdollJoint : byte
{
    Pelvis, Spine, Chest, Neck, Head, HeadTop,
    LClavicle, LShoulder, LElbow, LWrist, LHand,
    RClavicle, RShoulder, RElbow, RWrist, RHand,
    LHip, LKnee, LAnkle, LToe, RHip, RKnee, RAnkle, RToe,
}

/// <summary>래그돌 골격(24 관절 · 23 캡슐 본 · 질량 70 kg) — v17.33 이족 18 관절 매핑(목 · 정수리 · 쇄골 · 손 보간).</summary>
public static class RagdollSkeleton
{
    public const int Count = 24, Pad = 32;
    public const float HandLen = 0.09f, HeadTopLen = 0.12f, NeckT = 0.36f, ClavT = 0.37f;

    public static readonly sbyte[] Parent = { -1, 0, 1, 2, 3, 4, 2, 6, 7, 8, 9, 2, 11, 12, 13, 14, 0, 16, 17, 18, 0, 20, 21, 22 };
    /// <summary>본(자식 관절 기준 · 부모→자식) 캡슐 반경 m.</summary>
    public static readonly float[] BoneRadius = { 0f, 0.12f, 0.13f, 0.05f, 0.055f, 0.1f, 0.045f, 0.05f, 0.05f, 0.04f, 0.035f, 0.045f, 0.05f, 0.05f, 0.04f, 0.035f, 0.09f, 0.07f, 0.055f, 0.045f, 0.09f, 0.07f, 0.055f, 0.045f };
    private static readonly float[] MassW = { 8f, 7f, 10f, 1.5f, 3f, 1.5f, 1f, 1.5f, 1.2f, 0.6f, 0.3f, 1f, 1.5f, 1.2f, 0.6f, 0.3f, 3.5f, 3.5f, 1.5f, 0.5f, 3.5f, 3.5f, 1.5f, 0.5f };
    /// <summary>강체 보강 거리 구속(골반 삼각 · 견갑대) — 본 23 + 보강 9.</summary>
    private static readonly byte[] BraceA = { 16, 16, 20, 6, 7, 6, 11, 7, 12 }, BraceB = { 20, 1, 1, 11, 12, 3, 3, 2, 2 };
    /// <summary>자기 충돌 캡슐 쌍(비인접 본 · 자식 관절 기준).</summary>
    private static readonly byte[] SelfA = { 9, 9, 14, 14, 17, 18, 17, 21, 9, 14, 9, 14 }, SelfB = { 1, 2, 1, 2, 21, 22, 22, 18, 17, 21, 5, 5 };

    public static readonly float[] Mass = new float[Count], InvMass = new float[Count], ParticleRadius = new float[Count];
    /// <summary>추종 모터 질량 보정 √(8 kg / m) ∈ [1, 3] — 가벼운 말단(팔꿈치/손목)이 체중 지지 시 좌굴하지 않도록(가속 PD 는 질량 비례 힘).</summary>
    public static readonly float[] MotorScale = new float[Count];
    public static readonly Vector3[] Rest = new Vector3[Count];
    public static readonly byte[] ConA, ConB;
    public static readonly float[] ConLen;
    public static readonly byte[] GraphDist = new byte[Count * Count];
    public static ReadOnlySpan<byte> SelfPairA => SelfA;
    public static ReadOnlySpan<byte> SelfPairB => SelfB;
    public static float TotalMass { get; }

    static RagdollSkeleton()
    {
        var sum = 0f;
        for (var j = 0; j < Count; j++) sum += MassW[j];
        TotalMass = BipedSkeleton.BodyMass;
        for (var j = 0; j < Count; j++) { Mass[j] = MassW[j] * TotalMass / sum; InvMass[j] = 1f / Mass[j]; MotorScale[j] = Math.Clamp(MathF.Sqrt(8f / Mass[j]), 1f, 3f); }
        for (var j = 0; j < Count; j++)
        {
            var r = BoneRadius[j];
            for (var c = 1; c < Count; c++) if (Parent[c] == j) r = MathF.Max(r, BoneRadius[c]);
            ParticleRadius[j] = r;
        }
        Span<Vector3> b = stackalloc Vector3[BipedSkeleton.Count];
        BipedSkeleton.Rest(b);
        MapBiped(b, Rest);
        var n = Count - 1 + BraceA.Length;
        ConA = new byte[n]; ConB = new byte[n]; ConLen = new float[n];
        for (var j = 1; j < Count; j++) { ConA[j - 1] = (byte)Parent[j]; ConB[j - 1] = (byte)j; }
        for (var i = 0; i < BraceA.Length; i++) { ConA[Count - 1 + i] = BraceA[i]; ConB[Count - 1 + i] = BraceB[i]; }
        for (var i = 0; i < n; i++) ConLen[i] = (Rest[ConA[i]] - Rest[ConB[i]]).Length();
        // 그래프 거리(본 인접 BFS)
        Span<int> q = stackalloc int[Count];
        for (var s = 0; s < Count; s++)
        {
            for (var j = 0; j < Count; j++) GraphDist[s * Count + j] = 255;
            GraphDist[s * Count + s] = 0;
            int h = 0, t = 0; q[t++] = s;
            while (h < t)
            {
                var u = q[h++];
                for (var v = 0; v < Count; v++)
                {
                    if (GraphDist[s * Count + v] != 255 || (Parent[v] != u && Parent[u] != v)) continue;
                    GraphDist[s * Count + v] = (byte)(GraphDist[s * Count + u] + 1);
                    q[t++] = v;
                }
            }
        }
    }

    public static float BoneLength(int j) => j <= 0 ? 0f : (Rest[j] - Rest[Parent[j]]).Length();

    /// <summary>v17.33 이족 18 관절 → 래그돌 24 관절(목 = 흉추→머리 36 % · 쇄골 = 목→어깨 37 % · 정수리/손 = 진행 방향 연장).</summary>
    public static void MapBiped(ReadOnlySpan<Vector3> b, Span<Vector3> r)
    {
        r[0] = b[0]; r[1] = b[1]; r[2] = b[2];
        var neck = b[2] + (b[3] - b[2]) * NeckT;
        r[3] = neck; r[4] = b[3]; r[5] = b[3] + BipedSkeleton.Dir(b[3] - neck, Vector3.UnitY) * HeadTopLen;
        for (var s = 0; s < 2; s++)
        {
            int a18 = s == 0 ? 12 : 15, a24 = s == 0 ? 6 : 11, l18 = s == 0 ? 4 : 8, l24 = s == 0 ? 16 : 20;
            r[a24] = neck + (b[a18] - neck) * ClavT;
            r[a24 + 1] = b[a18]; r[a24 + 2] = b[a18 + 1]; r[a24 + 3] = b[a18 + 2];
            r[a24 + 4] = b[a18 + 2] + BipedSkeleton.Dir(b[a18 + 2] - b[a18 + 1], -Vector3.UnitY) * HandLen;
            for (var k = 0; k < 4; k++) r[l24 + k] = b[l18 + k];
        }
    }

    public static Vector3 CenterOfMass(ReadOnlySpan<Vector3> p)
    {
        var c = Vector3.Zero;
        for (var j = 0; j < Count; j++) c += p[j] * Mass[j];
        return c / TotalMass;
    }

    /// <summary>골반 좌표계(X = 좌→우 고관절 · Y = 골반→요추 직교화 · Z = X×Y 전방) — 특이(축 평행) 시 fallback.</summary>
    public static Quaternion PelvisFrame(Vector3 pelvis, Vector3 spine, Vector3 lHip, Vector3 rHip, Quaternion fallback)
    {
        var x = rHip - lHip;
        var lx = x.Length();
        if (lx < 1e-6f || !float.IsFinite(lx)) return fallback;
        x /= lx;
        var y = spine - pelvis;
        y -= x * Vector3.Dot(x, y);
        var ly = y.Length();
        if (ly < 1e-5f || !float.IsFinite(ly)) return fallback;
        y /= ly;
        var z = Vector3.Cross(x, y);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    public static string Name(RagdollJoint j) => j switch
    {
        RagdollJoint.Pelvis => "골반", RagdollJoint.Spine => "요추", RagdollJoint.Chest => "흉추", RagdollJoint.Neck => "목", RagdollJoint.Head => "머리", RagdollJoint.HeadTop => "정수리",
        RagdollJoint.LClavicle => "L 쇄골", RagdollJoint.LShoulder => "L 어깨", RagdollJoint.LElbow => "L 팔꿈치", RagdollJoint.LWrist => "L 손목", RagdollJoint.LHand => "L 손",
        RagdollJoint.RClavicle => "R 쇄골", RagdollJoint.RShoulder => "R 어깨", RagdollJoint.RElbow => "R 팔꿈치", RagdollJoint.RWrist => "R 손목", RagdollJoint.RHand => "R 손",
        RagdollJoint.LHip => "L 고관절", RagdollJoint.LKnee => "L 무릎", RagdollJoint.LAnkle => "L 발목", RagdollJoint.LToe => "L 발끝",
        RagdollJoint.RHip => "R 고관절", RagdollJoint.RKnee => "R 무릎", RagdollJoint.RAnkle => "R 발목", _ => "R 발끝",
    };
}

/// <summary>
/// [PromptFlow Studio v17.37.0] 🤸 실시간 하이브리드 액티브 래그돌 &amp; 관절 토크 물리 엔진(<see cref="Step"/> 무할당 · 60 fps).
///   ▸ 물리: 24 질점(관절) · 캡슐 본 23 + 보강 9 거리 구속 · XPBD 서브스텝 4 × 반복 4 · 지면(캡슐 반경 · 쿨롱 μ 0.8) · 비인접 캡슐 자기 충돌 12 쌍(전완–체간/대퇴/머리 · 다리–다리).
///   ▸ 모터: 관절별 PD(임계 감쇠) 암시적 적분 v' = (v + h(ω²(x* − x) + 2ω v* − g)) ÷ (1 + 2ωh + ω²h²) — 무조건 안정 · ω = 강성 × (톤 6 + (추종 40 − 톤) × (1 − w)).
///     목표 x* = 관절 공간(시뮬 부모 + 목표 본 벡터 75 %) ↔ 월드 목표 · 자세 추종(골반 회전 q 를 w × 추종 이득만큼 slerp 해 본 벡터 회전) · 추종분 모터 순 운동량 제거(내부 토크 근사).
///   ▸ 전환 가중 w(관절별 0 = 키네마틱 · 1 = 물리): 충격 에너지 E = |J|²/(2 m_group) → w = E/25 J · exp(−그래프 거리/2.5) · 반감기 0.35 s 감쇠 · 회복 합성기 하한(Floor) ·
///     w = 0 관절은 무한 질량 키네마틱(목표 스냅 · 목표 속도).
///   ▸ SIMD: SoA(32 패딩) Vector&lt;float&gt; — 가중 감쇠 · 모터 적분 · 속도 갱신 · 출력 블렌드(목표 + w(시뮬 − 목표)).
///   ▸ 신경망 결선점: 전환 가중/모터 이득은 절차 정책(학습 모델 아님) — 학습 정책 교체 지점 = <see cref="UpdateWeights"/>.
/// </summary>
public sealed class RealtimeHybridNeuralRagdollEngine
{
    public const int N = RagdollSkeleton.Count, P = RagdollSkeleton.Pad, Substeps = 4, Iterations = 4;
    public const float G = 9.81f, Friction = 0.8f, TrackOmega = 40f, ToneOmega = 6f, ImpactFullJ = 25f, Falloff = 2.5f, WeightHalfLife = 0.35f, AirDamp = 0.05f, CutDist = 0.25f, LocalMix = 0.75f;

    internal readonly float[] X = new float[P], Y = new float[P], Z = new float[P], VX = new float[P], VY = new float[P], VZ = new float[P];
    internal readonly float[] QX = new float[P], QY = new float[P], QZ = new float[P];
    private readonly float[] _t0x = new float[P], _t0y = new float[P], _t0z = new float[P], _t1x = new float[P], _t1y = new float[P], _t1z = new float[P];
    private readonly float[] _tvx = new float[P], _tvy = new float[P], _tvz = new float[P], _dx = new float[P], _dy = new float[P], _dz = new float[P];
    private readonly float[] _evx = new float[P], _evy = new float[P], _evz = new float[P];
    private readonly float[] _w = new float[P], _imp = new float[P], _om = new float[P], _fo = new float[P];
    private readonly Quaternion[] _rq = new Quaternion[N];
    private readonly float[] _mvx = new float[P], _mvy = new float[P], _mvz = new float[P];
    internal readonly float[] IM = new float[P];
    private readonly float[] _ox = new float[P], _oy = new float[P], _oz = new float[P];
    private readonly bool[] _ground = new bool[N];
    private Quaternion _pelvisRot = Quaternion.Identity, _kinRot = Quaternion.Identity;
    private bool _init;

    public RealtimeHybridNeuralRagdollEngine() => Reset(RagdollSkeleton.Rest);

    /// <summary>전신 모터 강성 배율(0.2~2).</summary>
    public float Stiffness { get; set; } = 1f;
    /// <summary>래그돌 가중 하한(회복 합성기 — 전도 1 · 기립 감쇠).</summary>
    public float Floor { get; set; }
    /// <summary>자세 추종 이득(0 = 월드 목표 추적 · 1 = 시뮬 골반 좌표계 자세 유지).</summary>
    public float FollowGain { get; set; } = 1f;
    public JointSingularityEnergyDivergenceGuard? Guard { get; set; }

    public Vector3 Position(int j) => new(X[j], Y[j], Z[j]);
    public Vector3 Velocity(int j) => new(VX[j], VY[j], VZ[j]);
    public Vector3 Output(int j) => new(_ox[j], _oy[j], _oz[j]);
    public Vector3 Target(int j) => new(_t1x[j], _t1y[j], _t1z[j]);
    public Vector3 TargetVelocity(int j) => new(_tvx[j], _tvy[j], _tvz[j]);
    public float Weight(int j) => _w[j];
    public float MotorOmega(int j) => _om[j];
    public bool Dynamic(int j) => IM[j] > 0f;
    public bool Grounded(int j) => _ground[j];
    public Quaternion PelvisRotation => _pelvisRot;
    public Vector3 PelvisForward => Vector3.Transform(Vector3.UnitZ, _pelvisRot);
    public Vector3 PelvisUp => Vector3.Transform(Vector3.UnitY, _pelvisRot);
    public Vector3 PelvisRight => Vector3.Transform(Vector3.UnitX, _pelvisRot);
    public Vector3 CenterOfMass { get; private set; }
    public Vector3 ComVelocity { get; private set; }
    public float KineticEnergy { get; private set; }
    public float PotentialEnergy { get; private set; }
    public float Hamiltonian => KineticEnergy + PotentialEnergy;
    public float MeanWeight { get; private set; }
    public float MaxWeight { get; private set; }
    public int DynamicCount { get; private set; }
    public int GroundContacts { get; private set; }
    public float LastImpactJ { get; private set; }
    public float MaxImpactJ { get; private set; }
    /// <summary>이번 프레임 충격 주입 에너지(J) — 해밀토니안 가드 예산.</summary>
    public float InjectedJ { get; private set; }
    public float StretchPct { get; private set; }
    public float MaxStretchPct { get; private set; }
    public long Impulses { get; private set; }
    public long TargetCuts { get; private set; }
    public long Steps { get; private set; }
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }

    public void CopyOutput(Span<Vector3> dst) { for (var j = 0; j < N; j++) dst[j] = new Vector3(_ox[j], _oy[j], _oz[j]); }

    public void ResetMetrics() { MaxImpactJ = 0f; MaxStretchPct = 0f; Impulses = 0; MaxUs = 0; }

    /// <summary>전 관절 키네마틱 스냅(속도 0 · 가중 0).</summary>
    public void Reset(ReadOnlySpan<Vector3> pose)
    {
        for (var j = 0; j < P; j++)
        {
            var p = j < N ? pose[j] : Vector3.Zero;
            X[j] = QX[j] = _t0x[j] = _t1x[j] = _ox[j] = p.X; Y[j] = QY[j] = _t0y[j] = _t1y[j] = _oy[j] = p.Y; Z[j] = QZ[j] = _t0z[j] = _t1z[j] = _oz[j] = p.Z;
            VX[j] = VY[j] = VZ[j] = _tvx[j] = _tvy[j] = _tvz[j] = _dx[j] = _dy[j] = _dz[j] = _evx[j] = _evy[j] = _evz[j] = 0f;
            _w[j] = _imp[j] = IM[j] = _om[j] = 0f;
        }
        _init = true;
        _pelvisRot = _kinRot = RagdollSkeleton.PelvisFrame(pose[0], pose[1], pose[16], pose[20], Quaternion.Identity);
        Measure();
    }

    /// <summary>프레임 목표(키네마틱 또는 기립 키포즈) — 목표 속도 = 차분 · 프레임당 25 cm 초과 도약(상태 전환) = 절단(보간/속도 0).</summary>
    public void SetTarget(ReadOnlySpan<Vector3> pose, float dt)
    {
        var inv = 1f / dt;
        for (var j = 0; j < N; j++)
        {
            var p = pose[j];
            _t0x[j] = _t1x[j]; _t0y[j] = _t1y[j]; _t0z[j] = _t1z[j];
            _t1x[j] = p.X; _t1y[j] = p.Y; _t1z[j] = p.Z;
            var jx = _t1x[j] - _t0x[j]; var jy = _t1y[j] - _t0y[j]; var jz = _t1z[j] - _t0z[j];
            if (!_init || jx * jx + jy * jy + jz * jz > CutDist * CutDist) { _t0x[j] = p.X; _t0y[j] = p.Y; _t0z[j] = p.Z; TargetCuts++; }
            _tvx[j] = (_t1x[j] - _t0x[j]) * inv; _tvy[j] = (_t1y[j] - _t0y[j]) * inv; _tvz[j] = (_t1z[j] - _t0z[j]) * inv;
        }
        _init = true;
    }

    /// <summary>
    /// 외력 충격(N·s) — 피격 관절 60 % · 부모 25 % · 자식 15 % 분배 · 그래프 거리 감쇠 전환 가중 · 반환 = 충격 에너지(J).
    /// </summary>
    public float ApplyImpulse(int joint, Vector3 impulse)
    {
        joint = Math.Clamp(joint, 0, N - 1);
        var parent = RagdollSkeleton.Parent[joint];
        var nChild = 0; var mGroup = RagdollSkeleton.Mass[joint];
        if (parent >= 0) mGroup += RagdollSkeleton.Mass[parent];
        for (var c = 1; c < N; c++) if (RagdollSkeleton.Parent[c] == joint) { nChild++; mGroup += RagdollSkeleton.Mass[c]; }
        float sHit = 0.6f, sPar = parent >= 0 ? 0.25f : 0f, sCh = nChild > 0 ? 0.15f : 0f;
        var norm = 1f / (sHit + sPar + sCh);
        sHit *= norm; sPar *= norm; sCh = nChild > 0 ? sCh * norm / nChild : 0f;
        var e = impulse.LengthSquared() / (2f * mGroup);
        if (!float.IsFinite(e)) return 0f;
        var act = false;
        for (var j = 0; j < N; j++)
        {
            var w = Math.Clamp(e / ImpactFullJ * MathF.Exp(-RagdollSkeleton.GraphDist[joint * N + j] / Falloff), 0f, 1f);
            if (RagdollSkeleton.GraphDist[joint * N + j] <= 1) w = MathF.Max(w, 0.05f);
            if (w > _imp[j]) _imp[j] = w;
            if (_imp[j] > _w[j]) _w[j] = _imp[j];
            if (_w[j] > 1e-3f && IM[j] == 0f) { IM[j] = RagdollSkeleton.InvMass[j]; VX[j] = _tvx[j]; VY[j] = _tvy[j]; VZ[j] = _tvz[j]; act = true; }
        }
        if (act) Activate();
        var ke = 0f;
        for (var j = 0; j < N; j++)
        {
            var s = j == joint ? sHit : j == parent ? sPar : RagdollSkeleton.Parent[j] == joint ? sCh : 0f;
            if (s == 0f) continue;
            var dv = impulse * (s * RagdollSkeleton.InvMass[j]);
            var v0 = Velocity(j); var v1 = v0 + dv;
            ke += 0.5f * RagdollSkeleton.Mass[j] * (v1.LengthSquared() - v0.LengthSquared());
            VX[j] = v1.X; VY[j] = v1.Y; VZ[j] = v1.Z;
        }
        InjectedJ += MathF.Max(0f, ke);
        LastImpactJ = e;
        if (e > MaxImpactJ) MaxImpactJ = e;
        Impulses++;
        Guard?.AfterImpulse(this);
        return e;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateWeights(dt);
        PrepareMotors();
        var guard = Guard;
        guard?.BeginFrame(this, dt);
        var h = dt / Substeps;
        for (var s = 0; s < Substeps; s++)
        {
            LocalTargets((s + 1f) / Substeps);
            Integrate(h, (s + 1f) / Substeps);
            InternalizeMotors(h);
            // 마찰은 적분 변위에만(구속 보정 후 마찰 복귀는 접지된 본의 길이 보정을 되돌려 압축이 잔류)
            SolveGround(true);
            for (var it = 0; it < Iterations; it++)
            {
                SolveDistance();
                SolveSelf();
                SolveGround(false);
            }
            guard?.ProjectLimits(this, h);
            UpdateVelocities(h);
            guard?.ClampVelocities(this, dt);
        }
        guard?.EndFrame(this, dt);
        Blend();
        Measure();
        InjectedJ = 0f;
        Steps++;
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (LastUs > MaxUs && Steps > 10) MaxUs = LastUs;
    }

    /// <summary>전환 가중(SIMD) — 충격 가중 감쇠 · 하한 · 키네마틱 마스크(역질량).</summary>
    private void UpdateWeights(float dt)
    {
        var decay = new Vector<float>(MathF.Exp(-dt * 0.6931472f / WeightHalfLife));
        var floor = new Vector<float>(Math.Clamp(Floor, 0f, 1f));
        var one = Vector<float>.One;
        var vc = Vector<float>.Count;
        for (var i = 0; i < P; i += vc)
        {
            var imp = new Vector<float>(_imp, i) * decay;
            imp.CopyTo(_imp, i);
            Vector.Min(Vector.Max(floor, imp), one).CopyTo(_w, i);
        }
        float sum = 0f, mx = 0f; var dyn = 0; var activated = false;
        for (var j = 0; j < N; j++)
        {
            var w = _w[j];
            sum += w; if (w > mx) mx = w;
            var was = IM[j] > 0f;
            IM[j] = w > 1e-3f ? RagdollSkeleton.InvMass[j] : 0f;
            if (IM[j] > 0f) { dyn++; if (!was) { VX[j] = _tvx[j]; VY[j] = _tvy[j]; VZ[j] = _tvz[j]; activated = true; } }
        }
        for (var j = N; j < P; j++) { _w[j] = 0f; IM[j] = 0f; }
        MeanWeight = sum / N; MaxWeight = mx; DynamicCount = dyn;
        if (activated) Activate();
    }

    /// <summary>키네마틱 → 물리 활성화 프레임: 애니 자세의 지면/캡슐 겹침을 적분 전에 투영(보정이 속도로 변환되는 폭발 방지).</summary>
    private void Activate()
    {
        for (var it = 0; it < 3; it++) { SolveSelf(); SolveGround(false); }
    }

    /// <summary>모터 ω · 자세 추종 목표 오프셋(시뮬 골반 좌표계에 실린 키네마틱 자세).</summary>
    private void PrepareMotors()
    {
        var sp = Position(0);
        _pelvisRot = RagdollSkeleton.PelvisFrame(sp, Position(1), Position(16), Position(20), _pelvisRot);
        var kp = Target(0);
        _kinRot = RagdollSkeleton.PelvisFrame(kp, Target(1), Target(16), Target(20), _kinRot);
        var q = Quaternion.Normalize(_pelvisRot * Quaternion.Conjugate(_kinRot));
        var stiff = Math.Clamp(Stiffness, 0.05f, 4f);
        var follow = Math.Clamp(FollowGain, 0f, 1f);
        for (var j = 0; j < N; j++)
        {
            var w = _w[j];
            _om[j] = stiff * (ToneOmega + (TrackOmega * RagdollSkeleton.MotorScale[j] - ToneOmega) * (1f - w));
            var f = follow * w;
            _fo[j] = f;
            _rq[j] = f > 0f ? Quaternion.Slerp(Quaternion.Identity, q, f) : Quaternion.Identity;
        }
    }

    /// <summary>
    /// 서브스텝 관절 공간 목표(스칼라 24) — 비루트 관절 목표 = 시뮬 부모 위치 + (회전된) 목표 본 벡터(LocalMix) ↔ 월드/골반 고정 목표.
    /// 지연된 부모에 대해 본 방향을 맞추므로 손목/발목 같은 말단이 월드 목표에 끌려 꺾이지 않는다(관절 토크 PD 근사).
    /// </summary>
    private void LocalTargets(float alpha)
    {
        var x0 = Position(0); var v0 = Velocity(0);
        var tr = Lerp3(_t0x, _t0y, _t0z, _t1x, _t1y, _t1z, 0, alpha);
        for (var j = 0; j < N; j++)
        {
            if (IM[j] == 0f) { _dx[j] = _dy[j] = _dz[j] = 0f; _evx[j] = _tvx[j]; _evy[j] = _tvy[j]; _evz[j] = _tvz[j]; continue; }
            var f = _fo[j];
            var tj = Lerp3(_t0x, _t0y, _t0z, _t1x, _t1y, _t1z, j, alpha);
            var tvj = TargetVelocity(j);
            Vector3 eff, tv;
            if (j == 0) { eff = tj + (x0 - tj) * f; tv = tvj * (1f - f) + v0 * f; }
            else
            {
                var p = RagdollSkeleton.Parent[j];
                var tp = Lerp3(_t0x, _t0y, _t0z, _t1x, _t1y, _t1z, p, alpha);
                var world = f > 0f ? tj + (x0 + Vector3.Transform(tj - tr, _rq[j]) - tj) * f : tj;
                var local = Position(p) + Vector3.Transform(tj - tp, _rq[j]);
                eff = world + (local - world) * LocalMix;
                var tvw = tvj * (1f - f) + v0 * f;
                var tvl = Velocity(p) + (tvj - TargetVelocity(p)) * (1f - f);
                tv = tvw + (tvl - tvw) * LocalMix;
            }
            var d = eff - tj;
            _dx[j] = d.X; _dy[j] = d.Y; _dz[j] = d.Z;
            _evx[j] = tv.X; _evy[j] = tv.Y; _evz[j] = tv.Z;
        }
    }

    private static Vector3 Lerp3(float[] ax, float[] ay, float[] az, float[] bx, float[] by, float[] bz, int j, float t) =>
        new(ax[j] + (bx[j] - ax[j]) * t, ay[j] + (by[j] - ay[j]) * t, az[j] + (bz[j] - az[j]) * t);

    /// <summary>서브스텝 모터 암시적 PD + 중력 → 예측 위치(SIMD · 키네마틱 = 목표 스냅).</summary>
    private void Integrate(float h, float alpha)
    {
        var vc = Vector<float>.Count;
        var hv = new Vector<float>(h); var av = new Vector<float>(alpha); var g = new Vector<float>(G);
        var one = Vector<float>.One; var two = new Vector<float>(2f); var zero = Vector<float>.Zero;
        var damp = new Vector<float>(1f - AirDamp * h);
        for (var i = 0; i < P; i += vc)
        {
            var dyn = Vector.GreaterThan(new Vector<float>(IM, i), zero);
            var x = new Vector<float>(X, i); var y = new Vector<float>(Y, i); var z = new Vector<float>(Z, i);
            var t0 = new Vector<float>(_t0x, i);
            var tx = t0 + (new Vector<float>(_t1x, i) - t0) * av + new Vector<float>(_dx, i);
            t0 = new Vector<float>(_t0y, i);
            var ty = t0 + (new Vector<float>(_t1y, i) - t0) * av + new Vector<float>(_dy, i);
            t0 = new Vector<float>(_t0z, i);
            var tz = t0 + (new Vector<float>(_t1z, i) - t0) * av + new Vector<float>(_dz, i);
            var tvx = new Vector<float>(_evx, i); var tvy = new Vector<float>(_evy, i); var tvz = new Vector<float>(_evz, i);
            var om = new Vector<float>(_om, i);
            var kp = om * om; var kd = two * om;
            var inv = one / (one + hv * kd + hv * hv * kp);
            var v0x = new Vector<float>(VX, i); var v0y = new Vector<float>(VY, i) - hv * g; var v0z = new Vector<float>(VZ, i);
            var vx = (v0x + hv * (kp * (tx - x) + kd * tvx)) * inv * damp;
            var vy = (v0y + hv * (kp * (ty - y) + kd * tvy)) * inv * damp;
            var vz = (v0z + hv * (kp * (tz - z) + kd * tvz)) * inv * damp;
            var fo = new Vector<float>(_fo, i);
            ((vx - v0x) * fo).CopyTo(_mvx, i); ((vy - v0y) * fo).CopyTo(_mvy, i); ((vz - v0z) * fo).CopyTo(_mvz, i);
            x.CopyTo(QX, i); y.CopyTo(QY, i); z.CopyTo(QZ, i);
            Vector.ConditionalSelect(dyn, x + hv * vx, tx).CopyTo(X, i);
            Vector.ConditionalSelect(dyn, y + hv * vy, ty).CopyTo(Y, i);
            Vector.ConditionalSelect(dyn, z + hv * vz, tz).CopyTo(Z, i);
            Vector.ConditionalSelect(dyn, vx, tvx).CopyTo(VX, i);
            Vector.ConditionalSelect(dyn, vy, tvy).CopyTo(VY, i);
            Vector.ConditionalSelect(dyn, vz, tvz).CopyTo(VZ, i);
        }
    }

    /// <summary>자세 추종 모터 = 내부 토크 근사 — 추종 비율만큼 모터 Δv 의 순 운동량을 제거(외력 0 · 공중 부양/자가 추진 방지).</summary>
    private void InternalizeMotors(float h)
    {
        var p = Vector3.Zero; var md = 0f;
        for (var j = 0; j < N; j++)
        {
            if (IM[j] == 0f) continue;
            var m = RagdollSkeleton.Mass[j];
            p += new Vector3(_mvx[j], _mvy[j], _mvz[j]) * m; md += m;
        }
        if (md <= 0f || p.LengthSquared() < 1e-12f) return;
        var c = p / md;
        for (var j = 0; j < N; j++)
        {
            if (IM[j] == 0f) continue;
            VX[j] -= c.X; VY[j] -= c.Y; VZ[j] -= c.Z;
            X[j] -= c.X * h; Y[j] -= c.Y * h; Z[j] -= c.Z * h;
        }
    }

    private void UpdateVelocities(float h)
    {
        var vc = Vector<float>.Count;
        var ih = new Vector<float>(1f / h); var zero = Vector<float>.Zero;
        for (var i = 0; i < P; i += vc)
        {
            var dyn = Vector.GreaterThan(new Vector<float>(IM, i), zero);
            Vector.ConditionalSelect(dyn, (new Vector<float>(X, i) - new Vector<float>(QX, i)) * ih, new Vector<float>(_evx, i)).CopyTo(VX, i);
            Vector.ConditionalSelect(dyn, (new Vector<float>(Y, i) - new Vector<float>(QY, i)) * ih, new Vector<float>(_evy, i)).CopyTo(VY, i);
            Vector.ConditionalSelect(dyn, (new Vector<float>(Z, i) - new Vector<float>(QZ, i)) * ih, new Vector<float>(_evz, i)).CopyTo(VZ, i);
        }
    }

    private void SolveDistance()
    {
        var ca = RagdollSkeleton.ConA; var cb = RagdollSkeleton.ConB; var cl = RagdollSkeleton.ConLen;
        for (var i = 0; i < ca.Length; i++)
        {
            int a = ca[i], b = cb[i];
            float wa = IM[a], wb = IM[b], ws = wa + wb;
            if (ws <= 0f) continue;
            float dx = X[b] - X[a], dy = Y[b] - Y[a], dz = Z[b] - Z[a];
            var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-7f) continue;
            var k = (len - cl[i]) / (len * ws);
            X[a] += wa * k * dx; Y[a] += wa * k * dy; Z[a] += wa * k * dz;
            X[b] -= wb * k * dx; Y[b] -= wb * k * dy; Z[b] -= wb * k * dz;
        }
    }

    private void SolveSelf()
    {
        var sa = RagdollSkeleton.SelfPairA; var sb = RagdollSkeleton.SelfPairB;
        for (var i = 0; i < sa.Length; i++)
        {
            int a1 = sa[i], a0 = RagdollSkeleton.Parent[a1], b1 = sb[i], b0 = RagdollSkeleton.Parent[b1];
            float wa0 = IM[a0], wa1 = IM[a1], wb0 = IM[b0], wb1 = IM[b1];
            if (wa0 + wa1 + wb0 + wb1 <= 0f) continue;
            Vector3 pa0 = Position(a0), pa1 = Position(a1), pb0 = Position(b0), pb1 = Position(b1);
            ClosestSegments(pa0, pa1, pb0, pb1, out var s, out var t);
            var ca = pa0 + (pa1 - pa0) * s; var cb = pb0 + (pb1 - pb0) * t;
            var d = cb - ca; var dist = d.Length();
            var r = RagdollSkeleton.BoneRadius[a1] + RagdollSkeleton.BoneRadius[b1];
            if (dist >= r || dist < 1e-6f) continue;
            var n = d / dist;
            float ka0 = (1f - s) * wa0, ka1 = s * wa1, kb0 = (1f - t) * wb0, kb1 = t * wb1;
            var wsum = (1f - s) * ka0 + s * ka1 + (1f - t) * kb0 + t * kb1;
            if (wsum <= 1e-9f) continue;
            var lam = (r - dist) / wsum;
            Move(a0, -n * (lam * ka0)); Move(a1, -n * (lam * ka1)); Move(b0, n * (lam * kb0)); Move(b1, n * (lam * kb1));
        }
    }

    private void Move(int j, Vector3 d) { X[j] += d.X; Y[j] += d.Y; Z[j] += d.Z; }

    private void SolveGround(bool friction)
    {
        var pr = RagdollSkeleton.ParticleRadius;
        for (var j = 0; j < N; j++)
        {
            if (IM[j] == 0f) continue;
            var r = pr[j];
            if (Y[j] >= r) continue;
            var d = r - Y[j];
            Y[j] = r;
            if (!friction) continue;
            float dx = X[j] - QX[j], dz = Z[j] - QZ[j];
            var t = MathF.Sqrt(dx * dx + dz * dz);
            var lim = Friction * d + 0.0002f;
            if (t <= lim) { X[j] = QX[j]; Z[j] = QZ[j]; }
            else { var k = lim / t; X[j] -= dx * k; Z[j] -= dz * k; }
        }
    }

    /// <summary>출력 = 목표 + w(시뮬 − 목표)(SIMD).</summary>
    private void Blend()
    {
        var vc = Vector<float>.Count;
        for (var i = 0; i < P; i += vc)
        {
            var w = new Vector<float>(_w, i);
            var t = new Vector<float>(_t1x, i); (t + w * (new Vector<float>(X, i) - t)).CopyTo(_ox, i);
            t = new Vector<float>(_t1y, i); (t + w * (new Vector<float>(Y, i) - t)).CopyTo(_oy, i);
            t = new Vector<float>(_t1z, i); (t + w * (new Vector<float>(Z, i) - t)).CopyTo(_oz, i);
        }
    }

    private void Measure()
    {
        float ke = 0f, pe = 0f; Vector3 c = Vector3.Zero, cv = Vector3.Zero; var gc = 0;
        var pr = RagdollSkeleton.ParticleRadius;
        for (var j = 0; j < N; j++)
        {
            var m = RagdollSkeleton.Mass[j];
            var v = Velocity(j);
            ke += 0.5f * m * v.LengthSquared(); pe += m * G * Y[j];
            c += Position(j) * m; cv += v * m;
            _ground[j] = IM[j] > 0f && Y[j] <= pr[j] + 0.005f;
            if (_ground[j]) gc++;
        }
        KineticEnergy = ke; PotentialEnergy = pe; CenterOfMass = c / RagdollSkeleton.TotalMass; ComVelocity = cv / RagdollSkeleton.TotalMass; GroundContacts = gc;
        var st = 0f;
        for (var j = 1; j < N; j++)
        {
            if (IM[j] == 0f && IM[RagdollSkeleton.Parent[j]] == 0f) continue;
            var l = (Position(j) - Position(RagdollSkeleton.Parent[j])).Length();
            var e = MathF.Abs(l - RagdollSkeleton.ConLen[j - 1]) / RagdollSkeleton.ConLen[j - 1] * 100f;
            if (e > st || !float.IsFinite(e)) st = e;
        }
        StretchPct = st;
        if (st > MaxStretchPct || !float.IsFinite(st)) MaxStretchPct = st;
    }

    /// <summary>선분-선분 최근접 매개변수(Ericson).</summary>
    public static void ClosestSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float t)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        if (a <= 1e-9f && e <= 1e-9f) { s = t = 0f; return; }
        if (a <= 1e-9f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); return; }
        var c = Vector3.Dot(d1, r);
        if (e <= 1e-9f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); return; }
        var b = Vector3.Dot(d1, d2);
        var den = a * e - b * b;
        s = den > 1e-9f ? Math.Clamp((b * f - c * e) / den, 0f, 1f) : 0f;
        t = (b * s + f) / e;
        if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
        else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
    }

    // ══════════════════════ 가드 접근(동일 어셈블리) ══════════════════════

    internal void SetPosition(int j, Vector3 p) { X[j] = p.X; Y[j] = p.Y; Z[j] = p.Z; }
    internal void SetVelocity(int j, Vector3 v) { VX[j] = v.X; VY[j] = v.Y; VZ[j] = v.Z; }
    internal void SetPrev(int j, Vector3 p) { QX[j] = p.X; QY[j] = p.Y; QZ[j] = p.Z; }
    internal Vector3 Prev(int j) => new(QX[j], QY[j], QZ[j]);

    /// <summary>폭발 롤백 — 동적 관절을 목표/목표 속도로 복원.</summary>
    internal void Rollback()
    {
        for (var j = 0; j < N; j++)
        {
            X[j] = QX[j] = _t1x[j]; Y[j] = QY[j] = MathF.Max(_t1y[j], RagdollSkeleton.ParticleRadius[j]); Z[j] = QZ[j] = _t1z[j];
            VX[j] = _tvx[j]; VY[j] = _tvy[j]; VZ[j] = _tvz[j];
        }
    }
}
