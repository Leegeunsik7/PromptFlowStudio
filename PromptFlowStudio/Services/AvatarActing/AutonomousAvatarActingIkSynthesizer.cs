using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;
using PromptFlowStudio.Services.NeuralRagdoll;
namespace PromptFlowStudio.Services.AvatarActing;

/// <summary>
/// [PromptFlow Studio v17.60.0] 🦾 아바타 자율 연기 IK &amp; 감성 레이어 블렌딩 합성기(<see cref="Synthesize"/> 무할당 · 60 fps).
/// ① 이동 레이어: v17.33 모션 매칭 18 관절 월드 자세 → v17.37 24 관절 매핑 → 골반 프레임 · 다리/팔 힌지 정렬(굴곡 평면 → 휴지 힌지 축) · 몸통/머리 최소 비틀림 로컬 회전.
/// ② 연기 레이어: 트랜스포머 프레임(척추 굽힘/비틀림 · 머리 끄덕임/고개 · 어깨 으쓱) 가산 → 부분 FK → 가슴 로컬 손 목표 월드화 →
/// 팔당 4 관절 3 본(어깨 → 팔꿈치 → 손목 → 손) FABRIK(말단 방향 구속 · 팔꿈치 폴 평면 회전 · 1 mm 수렴 · 최대 12 회) → 힌지 정렬 로컬 회전.
/// ③ 상/하체 분리: 하체 = 이동 레이어 그대로 · 팔 = 이동 ↔ 연기 Slerp(제스처 가중치) · 척추/머리 = 이동 + 연기 가산.
/// </summary>
public sealed class AutonomousAvatarActingIkSynthesizer
{
    public const int N = RagdollSkeleton.Count, MaxIter = 12;
    public const float Tolerance = 0.001f;
    private static readonly int[] Child = BuildChild();
    private readonly Quaternion[] _loco = new Quaternion[N], _local = new Quaternion[N], _g = new Quaternion[N];
    private readonly Vector3[] _p = new Vector3[N], _m = new Vector3[N], _bw = new Vector3[BipedSkeleton.Count];
    private readonly Vector3[] _chain = new Vector3[8];
    private readonly Vector3[] _target = new Vector3[2], _hand = new Vector3[2];
    private readonly float[] _err = new float[2];
    private readonly int[] _iters = new int[2];
    private Vector3 _pelvis;
    private float _yaw;

    public float ActingWeight { get; set; } = 1f;
    public bool IkEnabled { get; set; } = true;
    public ReadOnlySpan<Quaternion> Local => _local;
    public Span<Quaternion> LocalMutable => _local;
    public ReadOnlySpan<Quaternion> Locomotion => _loco;
    public Vector3 Pelvis => _pelvis;
    public float Yaw => _yaw;
    public Vector3 Target(int side) => _target[side];
    public Vector3 IkHand(int side) => _hand[side];
    public float IkError(int side) => _err[side];
    public int IkIterations(int side) => _iters[side];
    public double LastIkUs { get; private set; }
    public double MaxIkUs { get; private set; }
    public double LastSynthUs { get; private set; }
    public long Solves { get; private set; }
    public long Unreachable { get; private set; }
    public static int Shoulder(int s) => s == 0 ? (int)RagdollJoint.LShoulder : (int)RagdollJoint.RShoulder;

    private static int[] BuildChild()
    {
        var c = new int[N]; Array.Fill(c, -1);
        for (var j = 1; j < N; j++) { var p = RagdollSkeleton.Parent[j]; if (c[p] < 0) c[p] = j; }
        return c;
    }

    private static Vector3 RestDir(int j) => Vector3.Normalize(RagdollSkeleton.Rest[Child[j]] - RagdollSkeleton.Rest[j]);

    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d < -0.999999f)
        {
            var ax = Vector3.Cross(Vector3.UnitX, a);
            if (ax.LengthSquared() < 1e-6f) ax = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1f + d));
    }

    /// <summary>(a0, b0) → (a1, b1) 프레임 정렬 회전(a 우선 · b 는 a 수직 성분).</summary>
    public static Quaternion FrameMap(Vector3 a0, Vector3 b0, Vector3 a1, Vector3 b1)
    {
        var q1 = FromTo(a0, a1);
        var bb = Vector3.Transform(b0, q1); bb -= a1 * Vector3.Dot(bb, a1);
        var bt = b1 - a1 * Vector3.Dot(b1, a1);
        if (bb.LengthSquared() < 1e-10f || bt.LengthSquared() < 1e-10f) return q1;
        return Quaternion.Normalize(FromTo(Vector3.Normalize(bb), Vector3.Normalize(bt)) * q1);
    }

    private static Vector3 Dir(Vector3 v, Vector3 fb) { var l = v.Length(); return l > 1e-7f && float.IsFinite(l) ? v / l : fb; }

    // ══════════════════════ 이동 레이어(무할당) ══════════════════════
    /// <summary>v17.33 18 관절 루트 로컬 자세 · 루트 위치 · 진행 방위 → 월드 → 24 관절 로컬 회전.</summary>
    public void SetLocomotion(ReadOnlySpan<Vector3> localPose, Vector3 root, float heading)
    {
        for (var j = 0; j < BipedSkeleton.Count; j++) _bw[j] = BipedSkeleton.ToWorld(localPose[j], root, heading);
        RagdollSkeleton.MapBiped(_bw, _m);
        var rest = RagdollSkeleton.Rest;
        _yaw = heading; _pelvis = _m[0];
        var rq = Quaternion.CreateFromAxisAngle(Vector3.UnitY, heading);
        Vector3 rs = Vector3.Normalize(rest[(int)RagdollJoint.RHip] - rest[(int)RagdollJoint.LHip]), ru = Vector3.Normalize(rest[(int)RagdollJoint.Chest] - rest[0]);
        Vector3 cs = Dir(_m[(int)RagdollJoint.RHip] - _m[(int)RagdollJoint.LHip], Vector3.Transform(rs, rq)), cu = Dir(_m[(int)RagdollJoint.Spine] - _m[0], Vector3.UnitY);
        var g0 = FrameMap(rs, ru, cs, cu);
        _g[0] = g0; _loco[0] = Quaternion.Conjugate(rq) * g0;
        for (var j = 1; j < N; j++) _loco[j] = Quaternion.Identity;
        // 몸통 · 목 · 머리 · 쇄골 · 발 · 발끝 = 최소 비틀림 / 팔 · 다리 = 힌지 정렬
        Minimal((int)RagdollJoint.Spine, _m); Minimal((int)RagdollJoint.Chest, _m); Minimal((int)RagdollJoint.Neck, _m); Minimal((int)RagdollJoint.Head, _m);
        for (var s = 0; s < 2; s++)
        {
            int cl = s == 0 ? (int)RagdollJoint.LClavicle : (int)RagdollJoint.RClavicle, hip = s == 0 ? (int)RagdollJoint.LHip : (int)RagdollJoint.RHip;
            Minimal(cl, _m);
            Hinge(cl + 1, _m[cl + 1], _m[cl + 2], _m[cl + 3], -1f, _loco);
            Minimal(cl + 3, _m);
            Hinge(hip, _m[hip], _m[hip + 1], _m[hip + 2], 1f, _loco);
            Minimal(hip + 2, _m);
        }
    }

    /// <summary>이동 자세 없음 — 제자리 직립(골반 위치 · 방위).</summary>
    public void SetStanding(Vector3 pelvis, float heading)
    {
        _yaw = heading; _pelvis = pelvis;
        for (var j = 0; j < N; j++) _loco[j] = Quaternion.Identity;
    }

    private Quaternion ParentG(int j) { var pa = RagdollSkeleton.Parent[j]; return pa < 0 ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw) : _g[pa]; }

    private void Minimal(int j, ReadOnlySpan<Vector3> p)
    {
        var gp = ParentG(j);
        var d0 = Vector3.Transform(RestDir(j), gp);
        var d1 = Dir(p[Child[j]] - p[j], d0);
        var gj = Quaternion.Normalize(FromTo(d0, d1) * gp);
        _g[j] = gj; _loco[j] = Quaternion.Normalize(Quaternion.Conjugate(gp) * gj);
    }

    /// <summary>2 분절 힌지 — 근위 관절 j 전역 회전 = (휴지 근위 방향, 휴지 X 힌지) → (현재 근위, ±굴곡 평면 법선) · 원위 = 순수 힌지.</summary>
    private void Hinge(int j, Vector3 a, Vector3 b, Vector3 c, float sign, Quaternion[] dst)
    {
        var gp = ParentG(j);
        var mid = Child[j];
        var u = Dir(b - a, Vector3.Transform(RestDir(j), gp)); var f = Dir(c - b, u);
        var n = Vector3.Cross(u, f);
        var h = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) * sign : Vector3.Transform(Vector3.UnitX, gp);
        var gj = FrameMap(RestDir(j), Vector3.UnitX, u, h);
        var gm = Quaternion.Normalize(FromTo(Vector3.Transform(RestDir(mid), gj), f) * gj);
        _g[j] = gj; _g[mid] = gm;
        dst[j] = Quaternion.Normalize(Quaternion.Conjugate(gp) * gj);
        dst[mid] = Quaternion.Normalize(Quaternion.Conjugate(gj) * gm);
    }

    // ══════════════════════ FK(무할당 · v17.57 규약) ══════════════════════
    public void Forward(ReadOnlySpan<Quaternion> local, Span<Vector3> pos, Span<Quaternion> glob)
    {
        var rest = RagdollSkeleton.Rest;
        var root = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw);
        glob[0] = root * local[0]; pos[0] = _pelvis;
        for (var j = 1; j < N; j++)
        {
            var pa = RagdollSkeleton.Parent[j];
            glob[j] = glob[pa] * local[j];
            pos[j] = pos[pa] + Vector3.Transform(rest[j] - rest[pa], glob[pa]);
        }
    }

    /// <summary>애니메이터 루트 = 골반 − R(yaw)·휴지 골반.</summary>
    public Vector3 AnimatorRoot => _pelvis - Vector3.Transform(RagdollSkeleton.Rest[0], Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw));

    // ══════════════════════ 연기 레이어(무할당) ══════════════════════
    public void Synthesize(in GestureFrame f)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _loco.AsSpan().CopyTo(_local);
        var aw = Math.Clamp(ActingWeight, 0f, 1f);
        var ux = Vector3.UnitX; var uy = Vector3.UnitY; var uz = Vector3.UnitZ;
        Quaternion R(Vector3 ax, float a) => Quaternion.CreateFromAxisAngle(ax, a * aw);
        _local[(int)RagdollJoint.Spine] = _local[(int)RagdollJoint.Spine] * R(ux, f.SpineFlex * 0.55f) * R(uy, f.SpineTwist * 0.5f);
        _local[(int)RagdollJoint.Chest] = _local[(int)RagdollJoint.Chest] * R(ux, f.SpineFlex * 0.45f) * R(uy, f.SpineTwist * 0.5f);
        _local[(int)RagdollJoint.Neck] = _local[(int)RagdollJoint.Neck] * R(uy, f.HeadYaw * 0.6f) * R(ux, f.HeadPitch * 0.55f) * R(uz, -f.HeadRoll * 0.5f);
        _local[(int)RagdollJoint.Head] = _local[(int)RagdollJoint.Head] * R(uy, f.HeadYaw * 0.4f) * R(ux, f.HeadPitch * 0.45f) * R(uz, -f.HeadRoll * 0.5f);
        _local[(int)RagdollJoint.LClavicle] = _local[(int)RagdollJoint.LClavicle] * R(uz, -0.25f * f.Shrug);
        _local[(int)RagdollJoint.RClavicle] = _local[(int)RagdollJoint.RClavicle] * R(uz, 0.25f * f.Shrug);
        Forward(_local, _p, _g);
        var ik0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var chest = (int)RagdollJoint.Chest;
        for (var s = 0; s < 2; s++)
        {
            var tl = s == 0 ? f.LeftTarget : f.RightTarget;
            var w = Math.Clamp(s == 0 ? f.LeftWeight : f.RightWeight, 0f, 1f) * aw;
            var T = _p[chest] + Vector3.Transform(tl, _g[chest]);
            if (!float.IsFinite(T.X + T.Y + T.Z)) T = _p[chest];
            _target[s] = T;
            if (!IkEnabled || w <= 1e-3f) { _err[s] = 0f; _iters[s] = 0; _hand[s] = _p[Shoulder(s) + 3]; continue; }
            SolveArm(s, T, w, f);
            Solves++;
        }
        LastIkUs = (System.Diagnostics.Stopwatch.GetTimestamp() - ik0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (LastIkUs > MaxIkUs && Solves > 4) MaxIkUs = LastIkUs;
        LastSynthUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>FABRIK(어깨 고정 · 말단 방향 · 폴) → 힌지 정렬 로컬 → 이동 레이어와 Slerp.</summary>
    private void SolveArm(int s, Vector3 T, float w, in GestureFrame f)
    {
        var rest = RagdollSkeleton.Rest;
        int sh = Shoulder(s), el = sh + 1, wr = sh + 2, hd = sh + 3;
        float l1 = (rest[el] - rest[sh]).Length(), l2 = (rest[wr] - rest[el]).Length(), l3 = (rest[hd] - rest[wr]).Length();
        var c = _chain.AsSpan(s * 4, 4);
        c[0] = _p[sh]; c[1] = _p[el]; c[2] = _p[wr]; c[3] = _p[hd];
        var S = c[0];
        var gc = _g[(int)RagdollJoint.Chest];
        var side = s == 0 ? -1f : 1f;
        var pole = S + Vector3.Transform(new Vector3(0.35f * side, -0.55f, -0.30f), gc);
        var down = Vector3.Transform(new Vector3(0.1f * side, -1f, 0.35f), gc);
        var dh = Dir(Vector3.Lerp(Dir(T - S, down), Dir(down, -Vector3.UnitY), 0.35f), down);
        if (Vector3.Distance(S, T) > l1 + l2 + l3) Unreachable++;
        // 초기 팔꿈치 = 폴 쪽
        c[1] = S + Dir(Vector3.Lerp(c[1] - S, pole - S, 0.5f), down) * l1;
        var it = 0; var err = float.MaxValue;
        for (; it < MaxIter; it++)
        {
            // 후방: 말단 → 어깨
            // 말단 방향 = 전완 연장(손목 중립) 70 % + 자세 기본 방향 30 %
            dh = Dir(Vector3.Lerp(Dir(T - c[1], dh), Dir(down, dh), 0.3f), dh);
            c[3] = T; c[2] = T - dh * l3;
            c[1] = c[2] + Dir(c[1] - c[2], -dh) * l2;
            // 전방: 어깨 → 말단(근위 고정)
            c[0] = S;
            c[1] = S + Dir(c[1] - S, down) * l1;
            c[2] = c[1] + Dir(c[2] - c[1], down) * l2;
            // 폴 평면 회전(S-W 축 · 길이 보존)
            var axis = c[2] - S; var al = axis.Length();
            if (al > 1e-5f)
            {
                axis /= al;
                var foot = S + axis * Vector3.Dot(c[1] - S, axis);
                var r = c[1] - foot; var rl = r.Length();
                var pp = pole - S; pp -= axis * Vector3.Dot(pp, axis);
                if (rl > 1e-6f && pp.LengthSquared() > 1e-8f) c[1] = foot + Vector3.Normalize(pp) * rl;
            }
            c[3] = c[2] + Dir(T - c[2], dh) * l3;
            err = Vector3.Distance(c[3], T);
            if (err < Tolerance) { it++; break; }
        }
        _err[s] = err; _iters[s] = it; _hand[s] = c[3];
        // 로컬 회전(어깨 · 팔꿈치 힌지 + 손목) — 연기 버퍼
        Span<Quaternion> act = stackalloc Quaternion[3];
        var gp = _g[sh - 1];
        var u = Dir(c[1] - c[0], down); var fo = Dir(c[2] - c[1], u);
        var n = Vector3.Cross(u, fo);
        var h = n.LengthSquared() > 1e-8f ? -Vector3.Normalize(n) : Vector3.Transform(Vector3.UnitX, gp);
        var gs = FrameMap(RestDir(sh), Vector3.UnitX, u, h);
        var ge = Quaternion.Normalize(FromTo(Vector3.Transform(RestDir(el), gs), fo) * gs);
        var gw = Quaternion.Normalize(FromTo(Vector3.Transform(RestDir(wr), ge), Dir(c[3] - c[2], fo)) * ge);
        act[0] = Quaternion.Normalize(Quaternion.Conjugate(gp) * gs);
        act[1] = Quaternion.Normalize(Quaternion.Conjugate(gs) * ge) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, f.ElbowHyper);
        act[2] = Quaternion.Normalize(Quaternion.Conjugate(ge) * gw) * Quaternion.CreateFromAxisAngle(RestDir(wr), s == 0 ? f.WristTwistL : f.WristTwistR);
        for (var k = 0; k < 3; k++) _local[sh + k] = Quaternion.Slerp(_local[sh + k], act[k], w);
    }
}
