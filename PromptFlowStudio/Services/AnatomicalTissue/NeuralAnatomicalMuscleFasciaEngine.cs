using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.AnatomicalTissue;

public enum TissueSimdPath : byte { Scalar = 0, V128, V256 }

public enum TissueTube : byte { Torso = 0, ArmL, ArmR, LegL, LegR }

/// <summary>기저 스키닝: LBS(관절 체적 붕괴 재현) · 관절 중심 회전 블렌드(DQS 등가 · 붕괴 없음).</summary>
public enum TissueSkinning : byte { JointSlerp = 0, Lbs }

/// <summary>근육 구동 지표: 관절 각(정규화) · 각속도 부호 성분 · 착지 감속.</summary>
public enum MuscleDrive : byte { ElbowL = 0, ElbowR, KneeL, KneeR, ShoulderL, ShoulderR, Spine, AnkleL, AnkleR }

/// <summary>근육 정의: 기시/정지(뼈 부착 레스트 월드 점) · 피부 영향(튜브 · 축 범위 · 둘레 중심/폭) · 활성 구동.</summary>
public readonly record struct MuscleDef(string Name, TissueTube Tube, float S0, float S1, float ThetaC, float Width, int OriginBone, Vector3 OriginRest, int InsertBone, Vector3 InsertRest,
    MuscleDrive Drive, float AngleGain, float VelGain, float LandingGain, float Radius);

/// <summary>
/// [PromptFlow Studio v17.14.0] 💪 실시간 해부학 근육 팽창 &amp; 근막 슬라이딩 엔진(<see cref="Update"/> 무할당).
///   ▸ 피부 = v17.11 SMPL 골격 5 튜브(몸통 · 양팔 · 양다리) × 링 24 × 둘레 16 = 1,920 정점 · 타원/테이퍼 해부 단면 · 관절 블렌드 구간 스키닝 = 관절 중심 회전 블렌드(DQS 등가 · 기본) 또는 LBS(고전적 체적 붕괴 재현 · 가드 검증용).
///   ▸ 근육 13(이두 · 삼두 · 대퇴사두 · 햄스트링 · 비복근 × 좌우 · 승모근 × 좌우 · 복직근): 기시/정지 뼈 부착점 길이 L → 등체적 방추형 수동 반경 비 √(L₀/L) − 1 +
///     활성 a(관절 각 · 각속도 · 착지 감속 → 1차 동역학 τ 40/80 ms) 능동 경직 팽창 · 응력 σ = a·L/L₀ + 2·max(0, L/L₀ − 1).
///   ▸ 근막 슬라이딩: 피부가 근육 수축량 × 0.35 만큼 축 방향으로 미끄러짐(넓은 영향 로브).
///   ▸ SIMD: 정점 변위 = Σₘ 영향[m][v]·계수ₘ (정점 = 레인 · V256 → V128 → 스칼라 · 근육 오름차순 누적 → 비트 동일).
/// </summary>
public sealed class NeuralAnatomicalMuscleFasciaEngine
{
    public const int Tubes = 5, Rings = 24, Segs = 16, PerTube = Rings * Segs, V = Tubes * PerTube, RingCount = Tubes * Rings, J = MocapSkeleton.Body;
    public const float TauAct = 0.04f, TauDeact = 0.08f, SlideGain = 0.35f;

    public static readonly int[][] Chains =
    {
        new[] { MocapSkeleton.Pelvis, MocapSkeleton.Spine1, MocapSkeleton.Spine2, MocapSkeleton.Spine3, MocapSkeleton.Neck },
        new[] { MocapSkeleton.LShoulder, MocapSkeleton.LElbow, MocapSkeleton.LWrist },
        new[] { MocapSkeleton.RShoulder, MocapSkeleton.RElbow, MocapSkeleton.RWrist },
        new[] { MocapSkeleton.LHip, MocapSkeleton.LKnee, MocapSkeleton.LAnkle },
        new[] { MocapSkeleton.RHip, MocapSkeleton.RKnee, MocapSkeleton.RAnkle },
    };
    public static readonly string[] TubeNames = { "몸통", "왼팔", "오른팔", "왼다리", "오른다리" };
    private static readonly float[] BlendHalf = { 0.065f, 0.09f, 0.09f, 0.11f, 0.11f };

    public static readonly MuscleDef[] Muscles = BuildMuscles();
    public static int MuscleCount => Muscles.Length;

    // 레스트
    private readonly Vector3[] _restJoint = new Vector3[J];
    private readonly Vector3[] _restV = new Vector3[V], _restN = new Vector3[V], _restT = new Vector3[V], _restC = new Vector3[RingCount];
    private readonly float[] _restRadius = new float[V], _ringArc = new float[RingCount], _ringS = new float[RingCount];
    private readonly int[] _ringBoneA = new int[RingCount], _ringBoneB = new int[RingCount];
    private readonly float[] _ringW = new float[RingCount];
    private readonly int[] _ringJoint = new int[RingCount];
    private readonly float[] _infl, _slideInfl;
    // 변형 상태(SoA)
    public readonly float[] Px = new float[V], Py = new float[V], Pz = new float[V];
    public readonly float[] Bx = new float[V], By = new float[V], Bz = new float[V];
    public readonly float[] Nx = new float[V], Ny = new float[V], Nz = new float[V];
    public readonly float[] Tx = new float[V], Ty = new float[V], Tz = new float[V];
    public readonly float[] Radial = new float[V], Slide = new float[V];
    public readonly Vector3[] Center = new Vector3[RingCount];
    private readonly Vector3[] _jp = new Vector3[J];
    private readonly Quaternion[] _jq = new Quaternion[J];
    // 근육 상태
    private readonly float[] _len0, _len, _act, _stress, _bulge, _slide, _coefR, _coefS, _drive = new float[9], _drivePrev = new float[9], _driveVel = new float[9];
    private readonly Vector3[] _origin, _insert;
    private bool _hasPrev;

    public NeuralAnatomicalMuscleFasciaEngine()
    {
        MocapSkeleton.RestPositions(MocapSkeleton.RestOffset, _restJoint);
        BuildMesh();
        var m = Muscles.Length;
        _infl = new float[m * V]; _slideInfl = new float[m * V];
        _len0 = new float[m]; _len = new float[m]; _act = new float[m]; _stress = new float[m]; _bulge = new float[m]; _slide = new float[m]; _coefR = new float[m]; _coefS = new float[m];
        _origin = new Vector3[m]; _insert = new Vector3[m];
        for (var k = 0; k < m; k++)
        {
            ref readonly var d = ref Muscles[k];
            _len0[k] = Vector3.Distance(d.OriginRest, d.InsertRest);
            for (var v = 0; v < V; v++)
            {
                var tube = v / PerTube;
                if (tube != (int)d.Tube) continue;
                var ring = v / Segs;
                var s = _ringS[ring];
                var th = MathF.Tau * (v % Segs) / Segs;
                var prof = s <= d.S0 || s >= d.S1 ? 0f : MathF.Pow(MathF.Sin(MathF.PI * (s - d.S0) / (d.S1 - d.S0)), 2f);
                var dth = MathF.Abs(MathF.IEEERemainder(th - d.ThetaC, MathF.Tau));
                var ang = dth >= d.Width ? 0f : MathF.Pow(MathF.Cos(dth / d.Width * MathF.PI * 0.5f), 2f);
                _infl[k * V + v] = prof * ang;
                _slideInfl[k * V + v] = prof * (0.4f + 0.6f * MathF.Max(0f, MathF.Cos(MathF.Min(MathF.PI, dth / (d.Width * 1.6f) * MathF.PI * 0.5f))));
            }
        }
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? TissueSimdPath.V256 : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? TissueSimdPath.V128 : TissueSimdPath.Scalar;
        Span<Quaternion> id = stackalloc Quaternion[J];
        id.Fill(Quaternion.Identity);
        Update(_restJoint, id, 0f, 0f);
    }

    public TissueSimdPath Path { get; set; }
    public TissueSkinning Skinning { get; set; } = TissueSkinning.JointSlerp;
    public float MuscleGain { get; set; } = 1f;
    public bool FasciaSliding { get; set; } = true;
    public double LastUpdateUs { get; private set; }
    public double LastSimdUs { get; private set; }
    public float Activation(int m) => _act[m];
    public float Stress(int m) => _stress[m];
    public float LengthRatio(int m) => _len[m] / _len0[m];
    public float BulgeMm(int m) => _bulge[m] * 1000f;
    public float SlideMm(int m) => _slide[m] * 1000f;
    public Vector3 Origin(int m) => _origin[m];
    public Vector3 Insertion(int m) => _insert[m];
    public float Influence(int m, int v) => _infl[m * V + v];
    public float DriveValue(MuscleDrive d) => _drive[(int)d];
    public Vector3 Joint(int j) => _jp[j];
    public Quaternion JointRotation(int j) => _jq[j];
    public Vector3 RestJoint(int j) => _restJoint[j];
    public Vector3 RestVertex(int v) => _restV[v];
    public float RestRadius(int v) => _restRadius[v];
    public Vector3 RestCenter(int ring) => _restC[ring];
    public float RingArc(int ring) => _ringArc[ring];
    public float RingS(int ring) => _ringS[ring];
    public Vector3 Position(int v) => new(Px[v], Py[v], Pz[v]);
    public Vector3 BasePosition(int v) => new(Bx[v], By[v], Bz[v]);
    public Vector3 Normal(int v) => new(Nx[v], Ny[v], Nz[v]);
    public Vector3 Tangent(int v) => new(Tx[v], Ty[v], Tz[v]);
    public static int Vertex(int tube, int ring, int seg) => (tube * Rings + ring) * Segs + seg;
    public static int RingOf(int tube, int ring) => tube * Rings + ring;
    /// <summary>최대 응력 근육 지수.</summary>
    public int PeakMuscle { get; private set; }
    public float PeakStress { get; private set; }

    // ══════════════════════ 메쉬 ══════════════════════

    private static readonly float[] TorsoX = { 0f, 0.3f, 0.6f, 1f }, TorsoSide = { 0.15f, 0.14f, 0.155f, 0.11f }, TorsoFront = { 0.11f, 0.10f, 0.11f, 0.07f };
    private static readonly float[] ArmX = { 0f, 0.25f, 0.5f, 0.7f, 1f }, ArmR = { 0.052f, 0.046f, 0.037f, 0.040f, 0.028f };
    private static readonly float[] LegX = { 0f, 0.3f, 0.5f, 0.7f, 1f }, LegR = { 0.085f, 0.07f, 0.05f, 0.055f, 0.036f };

    /// <summary>해부 단면 반경 프로파일(s = 0..1 · side = 측방 / 아니면 전후).</summary>
    public static float Profile(TissueTube t, float s, bool side)
    {
        float[] xs, ys;
        switch (t)
        {
            case TissueTube.Torso: xs = TorsoX; ys = side ? TorsoSide : TorsoFront; break;
            case TissueTube.ArmL: case TissueTube.ArmR: xs = ArmX; ys = ArmR; break;
            default: xs = LegX; ys = LegR; break;
        }
        for (var i = 1; i < xs.Length; i++)
            if (s <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (s - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[^1];
    }

    private void BuildMesh()
    {
        for (var t = 0; t < Tubes; t++)
        {
            var ch = Chains[t];
            var total = 0f;
            for (var k = 0; k + 1 < ch.Length; k++) total += Vector3.Distance(_restJoint[ch[k]], _restJoint[ch[k + 1]]);
            for (var i = 0; i < Rings; i++)
            {
                var ring = t * Rings + i;
                var arc = total * i / (Rings - 1);
                var s = arc / total;
                // 세그먼트 탐색
                float acc = 0; var seg = 0;
                for (; seg < ch.Length - 2; seg++)
                {
                    var l = Vector3.Distance(_restJoint[ch[seg]], _restJoint[ch[seg + 1]]);
                    if (arc <= acc + l) break;
                    acc += l;
                }
                var a = _restJoint[ch[seg]]; var b = _restJoint[ch[seg + 1]];
                var sl = Vector3.Distance(a, b);
                var u = Math.Clamp((arc - acc) / sl, 0f, 1f);
                var c = Vector3.Lerp(a, b, u);
                var tan = Vector3.Normalize(b - a);
                // 블렌드: 근위/원위 관절과의 호 거리
                int boneA = ch[seg], boneB = ch[seg];
                var w = 0f;
                var hw = BlendHalf[t];
                if (seg > 0 && arc - acc < hw) { boneA = ch[seg - 1]; boneB = ch[seg]; w = Smooth(-hw, hw, arc - acc); }
                else if (seg + 1 < ch.Length - 1 && acc + sl - arc < hw) { boneA = ch[seg]; boneB = ch[seg + 1]; w = Smooth(-hw, hw, arc - (acc + sl)); }
                _ringBoneA[ring] = boneA; _ringBoneB[ring] = boneB; _ringW[ring] = w; _ringJoint[ring] = boneB;
                _restC[ring] = c; _ringArc[ring] = arc; _ringS[ring] = s;
                var n0 = Vector3.UnitZ - tan * Vector3.Dot(Vector3.UnitZ, tan);
                n0 = n0.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(n0);
                var n1 = Vector3.Cross(tan, n0);
                float rf = Profile((TissueTube)t, s, false), rs = Profile((TissueTube)t, s, true);
                for (var j = 0; j < Segs; j++)
                {
                    var th = MathF.Tau * j / Segs;
                    var v = ring * Segs + j;
                    var off = rf * MathF.Cos(th) * n0 + rs * MathF.Sin(th) * n1;
                    _restV[v] = c + off;
                    _restN[v] = Vector3.Normalize(rs * MathF.Cos(th) * n0 + rf * MathF.Sin(th) * n1);
                    _restT[v] = tan;
                    _restRadius[v] = off.Length();
                }
            }
        }
    }

    private static float Smooth(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static MuscleDef[] BuildMuscles()
    {
        Span<Vector3> r = stackalloc Vector3[J];
        MocapSkeleton.RestPositions(MocapSkeleton.RestOffset, r);
        var list = new MuscleDef[13];
        for (var s = 0; s < 2; s++)
        {
            var L = s == 0;
            float sx = L ? 1f : -1f;
            int sh = L ? MocapSkeleton.LShoulder : MocapSkeleton.RShoulder, el = L ? MocapSkeleton.LElbow : MocapSkeleton.RElbow;
            int hip = L ? MocapSkeleton.LHip : MocapSkeleton.RHip, kn = L ? MocapSkeleton.LKnee : MocapSkeleton.RKnee, an = L ? MocapSkeleton.LAnkle : MocapSkeleton.RAnkle;
            var armT = L ? TissueTube.ArmL : TissueTube.ArmR; var legT = L ? TissueTube.LegL : TissueTube.LegR;
            var tag = L ? "(좌)" : "(우)";
            list[s] = new MuscleDef("이두근" + tag, armT, 0.06f, 0.5f, 0f, 1.3f, sh, r[sh] + new Vector3(sx * 0.03f, 0, 0.025f), el, r[el] + new Vector3(sx * 0.045f, 0, 0.02f),
                L ? MuscleDrive.ElbowL : MuscleDrive.ElbowR, 0.75f, 0.15f, 0f, 0.042f);
            list[2 + s] = new MuscleDef("삼두근" + tag, armT, 0.06f, 0.5f, MathF.PI, 1.3f, sh, r[sh] + new Vector3(sx * 0.03f, 0, -0.025f), el, r[el] + new Vector3(sx * -0.02f, 0, -0.02f),
                L ? MuscleDrive.ElbowL : MuscleDrive.ElbowR, 0.1f, -0.25f, 0f, 0.042f);
            list[4 + s] = new MuscleDef("대퇴사두근" + tag, legT, 0.04f, 0.47f, 0f, 1.5f, hip, r[hip] + new Vector3(0, -0.08f, 0.04f), kn, r[kn] + new Vector3(0, -0.06f, 0.035f),
                L ? MuscleDrive.KneeL : MuscleDrive.KneeR, 0.35f, 0f, 0.9f, 0.07f);
            list[6 + s] = new MuscleDef("햄스트링" + tag, legT, 0.06f, 0.47f, MathF.PI, 1.3f, hip, r[hip] + new Vector3(0, -0.06f, -0.05f), kn, r[kn] + new Vector3(0, -0.04f, -0.035f),
                L ? MuscleDrive.KneeL : MuscleDrive.KneeR, 0.2f, 0.2f, 0.3f, 0.065f);
            list[8 + s] = new MuscleDef("비복근" + tag, legT, 0.53f, 0.82f, MathF.PI, 1.4f, hip, r[kn] + new Vector3(0, 0.02f, -0.035f), an, r[an] + new Vector3(0, -0.02f, -0.045f),
                L ? MuscleDrive.AnkleL : MuscleDrive.AnkleR, 0.3f, 0f, 1f, 0.05f);
            list[10 + s] = new MuscleDef("승모근" + tag, TissueTube.Torso, 0.68f, 1.01f, L ? MathF.PI - 0.75f : MathF.PI + 0.75f, 0.9f, MocapSkeleton.Spine3, r[MocapSkeleton.Neck] + new Vector3(0, 0, -0.04f),
                L ? MocapSkeleton.LCollar : MocapSkeleton.RCollar, r[sh] + new Vector3(0, 0.03f, -0.01f), L ? MuscleDrive.ShoulderL : MuscleDrive.ShoulderR, 0.9f, 0f, 0f, 0.09f);
        }
        list[12] = new MuscleDef("복직근", TissueTube.Torso, 0.08f, 0.62f, 0f, 0.75f, MocapSkeleton.Pelvis, r[MocapSkeleton.Pelvis] + new Vector3(0, 0.05f, 0.11f),
            MocapSkeleton.Spine2, r[MocapSkeleton.Spine3] + new Vector3(0, 0, 0.1f), MuscleDrive.Spine, 0.9f, 0.1f, 0.15f, 0.035f);
        return list;
    }

    // ══════════════════════ SIMD 누적 ══════════════════════

    /// <summary>dst[v] = Σₘ w[m·n + v]·c[m] (m 오름차순 · 경로 간 비트 동일). dst 선 초기화 안 함(덮어씀).</summary>
    public static void Accumulate(TissueSimdPath path, ReadOnlySpan<float> w, ReadOnlySpan<float> c, int m, int n, Span<float> dst)
    {
        var i = 0;
        if (path == TissueSimdPath.V256 && Vector256.IsHardwareAccelerated)
        {
            ref var wr = ref MemoryMarshal.GetReference(w);
            for (; i + 8 <= n; i += 8)
            {
                var acc = Vector256<float>.Zero;
                for (var k = 0; k < m; k++) acc += Vector256.LoadUnsafe(ref wr, (nuint)(k * n + i)) * Vector256.Create(c[k]);
                acc.StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)i);
            }
        }
        if (path != TissueSimdPath.Scalar && Vector128.IsHardwareAccelerated)
        {
            ref var wr = ref MemoryMarshal.GetReference(w);
            for (; i + 4 <= n; i += 4)
            {
                var acc = Vector128<float>.Zero;
                for (var k = 0; k < m; k++) acc += Vector128.LoadUnsafe(ref wr, (nuint)(k * n + i)) * Vector128.Create(c[k]);
                acc.StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            var acc = 0f;
            for (var k = 0; k < m; k++) acc += w[k * n + i] * c[k];
            dst[i] = acc;
        }
    }

    // ══════════════════════ 갱신 ══════════════════════

    private Vector3 Attach(int bone, Vector3 rest) => _jp[bone] + Vector3.Transform(rest - _restJoint[bone], _jq[bone]);

    private static float AngleBetween(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b));
        return MathF.Acos(Math.Clamp(d, -1f, 1f)) * 180f / MathF.PI;
    }

    private void Drives(float dt)
    {
        var p = _jp;
        _drive[(int)MuscleDrive.ElbowL] = AngleBetween(p[MocapSkeleton.LElbow] - p[MocapSkeleton.LShoulder], p[MocapSkeleton.LWrist] - p[MocapSkeleton.LElbow]) / 140f;
        _drive[(int)MuscleDrive.ElbowR] = AngleBetween(p[MocapSkeleton.RElbow] - p[MocapSkeleton.RShoulder], p[MocapSkeleton.RWrist] - p[MocapSkeleton.RElbow]) / 140f;
        _drive[(int)MuscleDrive.KneeL] = AngleBetween(p[MocapSkeleton.LKnee] - p[MocapSkeleton.LHip], p[MocapSkeleton.LAnkle] - p[MocapSkeleton.LKnee]) / 120f;
        _drive[(int)MuscleDrive.KneeR] = AngleBetween(p[MocapSkeleton.RKnee] - p[MocapSkeleton.RHip], p[MocapSkeleton.RAnkle] - p[MocapSkeleton.RKnee]) / 120f;
        _drive[(int)MuscleDrive.ShoulderL] = MathF.Max(0f, AngleBetween(p[MocapSkeleton.LElbow] - p[MocapSkeleton.LShoulder], -Vector3.UnitY) - 30f) / 120f;
        _drive[(int)MuscleDrive.ShoulderR] = MathF.Max(0f, AngleBetween(p[MocapSkeleton.RElbow] - p[MocapSkeleton.RShoulder], -Vector3.UnitY) - 30f) / 120f;
        _drive[(int)MuscleDrive.Spine] = MathF.Max(0f, Vector3.Dot(Vector3.Normalize(p[MocapSkeleton.Neck] - p[MocapSkeleton.Spine2]), Vector3.UnitZ)) / 0.6f;
        _drive[(int)MuscleDrive.AnkleL] = AngleBetween(p[MocapSkeleton.LAnkle] - p[MocapSkeleton.LKnee], p[MocapSkeleton.LFoot] - p[MocapSkeleton.LAnkle]) / 120f;
        _drive[(int)MuscleDrive.AnkleR] = AngleBetween(p[MocapSkeleton.RAnkle] - p[MocapSkeleton.RKnee], p[MocapSkeleton.RFoot] - p[MocapSkeleton.RAnkle]) / 120f;
        for (var i = 0; i < _drive.Length; i++)
        {
            _driveVel[i] = _hasPrev && dt > 0 ? (_drive[i] - _drivePrev[i]) / dt : 0f;
            _drivePrev[i] = _drive[i];
        }
        _hasPrev = dt > 0;
    }

    /// <summary>
    /// 관절 위치/전역 회전(레스트 = 항등) + 착지 감속(m/s², 위 방향 +) → LBS · 근육 활성/팽창 · 근막 슬라이딩 → 정점 위치(무할당).
    /// </summary>
    public void Update(ReadOnlySpan<Vector3> joints, ReadOnlySpan<Quaternion> global, float landingAccel, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        joints.Slice(0, J).CopyTo(_jp);
        global.Slice(0, J).CopyTo(_jq);
        Drives(dt);

        // ① 근육
        var land = Math.Clamp(landingAccel / 30f, 0f, 1f);
        float ka = dt > 0 ? 1f - MathF.Exp(-dt / TauAct) : 1f, kd = dt > 0 ? 1f - MathF.Exp(-dt / TauDeact) : 1f;
        var peak = 0f; var pm = 0;
        for (var m = 0; m < Muscles.Length; m++)
        {
            ref readonly var d = ref Muscles[m];
            _origin[m] = Attach(d.OriginBone, d.OriginRest);
            _insert[m] = Attach(d.InsertBone, d.InsertRest);
            var L = MathF.Max(1e-4f, Vector3.Distance(_origin[m], _insert[m]));
            _len[m] = L;
            var vel = _driveVel[(int)d.Drive];
            var u = Math.Clamp(d.AngleGain * _drive[(int)d.Drive] + MathF.Max(0f, d.VelGain * vel) * 0.5f + d.LandingGain * land, 0f, 1f);
            if (dt <= 0) u = 0f;
            _act[m] += (u - _act[m]) * (u > _act[m] ? ka : kd);
            var ratio = L / _len0[m];
            var passive = MathF.Sqrt(1f / ratio) - 1f;
            _bulge[m] = MuscleGain * d.Radius * (passive + 0.3f * _act[m]);
            _slide[m] = FasciaSliding ? SlideGain * (_len0[m] - L) : 0f;
            _stress[m] = _act[m] * ratio + 2f * MathF.Max(0f, ratio - 1f);
            _coefR[m] = _bulge[m]; _coefS[m] = _slide[m];
            if (_stress[m] > peak) { peak = _stress[m]; pm = m; }
        }
        PeakMuscle = pm; PeakStress = peak;

        // ② 스키닝 링 → 정점 기저 · 방향
        for (var ring = 0; ring < RingCount; ring++)
        {
            int a = _ringBoneA[ring], b = _ringBoneB[ring];
            var w = _ringW[ring];
            Quaternion qa = _jq[a], qb = _jq[b];
            var qr = w <= 0f ? qa : w >= 1f ? qb : Quaternion.Normalize(Quaternion.Slerp(qa, qb, w));
            Vector3 pa = _jp[a] - Vector3.Transform(_restJoint[a], qa), pb = _jp[b] - Vector3.Transform(_restJoint[b], qb);
            var rc = _restC[ring];
            // 관절 중심 회전 블렌드: 공유 관절 E 는 두 뼈 변환에서 동일 → x = E + slerp(Ra, Rb, w)·(x₀ − E₀)
            var slerpMode = Skinning == TissueSkinning.JointSlerp && w > 0f && w < 1f;
            var ej = _ringJoint[ring];
            Vector3 E = _jp[ej], E0 = _restJoint[ej];
            Center[ring] = slerpMode ? E + Vector3.Transform(rc - E0, qr) : (1f - w) * (pa + Vector3.Transform(rc, qa)) + w * (pb + Vector3.Transform(rc, qb));
            for (var j = 0; j < Segs; j++)
            {
                var v = ring * Segs + j;
                var rv = _restV[v];
                var x = slerpMode ? E + Vector3.Transform(rv - E0, qr) : (1f - w) * (pa + Vector3.Transform(rv, qa)) + w * (pb + Vector3.Transform(rv, qb));
                Bx[v] = x.X; By[v] = x.Y; Bz[v] = x.Z;
                var n = Vector3.Transform(_restN[v], qr); var tt = Vector3.Transform(_restT[v], qr);
                Nx[v] = n.X; Ny[v] = n.Y; Nz[v] = n.Z; Tx[v] = tt.X; Ty[v] = tt.Y; Tz[v] = tt.Z;
            }
        }

        // ③ SIMD 근육 팽창 · 근막 슬라이딩
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        Accumulate(Path, _infl, _coefR, Muscles.Length, V, Radial);
        Accumulate(Path, _slideInfl, _coefS, Muscles.Length, V, Slide);
        LastSimdUs = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        for (var v = 0; v < V; v++)
        {
            float r = Radial[v], s = Slide[v];
            Px[v] = Bx[v] + Nx[v] * r + Tx[v] * s;
            Py[v] = By[v] + Ny[v] * r + Ty[v] * s;
            Pz[v] = Bz[v] + Nz[v] * r + Tz[v] * s;
        }
        LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
