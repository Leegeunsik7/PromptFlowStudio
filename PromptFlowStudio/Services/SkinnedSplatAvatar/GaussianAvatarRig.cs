using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SkinnedSplatAvatar;

public enum AvatarMotion : byte { Fight, Jump, ArmsRaise, TwistStress, MotionMatching, Ragdoll }

/// <summary>
/// v17.57 가우시안 아바타 리그 — v17.37 래그돌 24 관절 골격 · 분절 b(부모 관절 → b) 캡슐(<see cref="RagdollSkeleton.BoneRadius"/>) 표면 균일 스플랫 바인드 ·
/// 4 본 가중치(소속 + 부모 분절(시작 30 %) + 첫 자식 분절(끝 30 %) 스무스스텝) · FK 애니메이터 · 위치 → 분절 회전/신장 해석(위치 기반 소스).
/// </summary>
public static class GaussianAvatarRig
{
    public const int Joints = RagdollSkeleton.Count, Bones = Joints;
    public const float Blend = 0.3f;
    public static readonly Vector3 Skin = new(0.80f, 0.60f, 0.50f), Shirt = new(0.22f, 0.36f, 0.62f), Pants = new(0.20f, 0.20f, 0.26f), Shoe = new(0.10f, 0.08f, 0.07f);
    private static readonly int[] FirstChild = BuildFirstChild();
    /// <summary>분절 비틀림 기준 벡터 선택(바인드 방향과 가장 덜 평행한 것 · 0 = 골반 좌→우 · 1 = 척추 위 · 2 = 전방).</summary>
    private static readonly byte[] RefKind = BuildRefKind();

    private static int[] BuildFirstChild()
    {
        var f = new int[Joints];
        Array.Fill(f, -1);
        for (var c = 1; c < Joints; c++) { var p = RagdollSkeleton.Parent[c]; if (f[p] < 0) f[p] = c; }
        return f;
    }

    private static void Refs(ReadOnlySpan<Vector3> p, out Vector3 side, out Vector3 up, out Vector3 fwd)
    {
        side = p[(int)RagdollJoint.RHip] - p[(int)RagdollJoint.LHip];
        side = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : Vector3.UnitX;
        up = p[(int)RagdollJoint.Chest] - p[(int)RagdollJoint.Pelvis];
        up = up.LengthSquared() > 1e-8f ? Vector3.Normalize(up) : Vector3.UnitY;
        fwd = Vector3.Cross(side, up);
        fwd = fwd.LengthSquared() > 1e-8f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
    }

    private static byte[] BuildRefKind()
    {
        var k = new byte[Joints];
        Refs(RagdollSkeleton.Rest, out var side, out var up, out var fwd);
        for (var b = 1; b < Joints; b++)
        {
            var d = Vector3.Normalize(RagdollSkeleton.Rest[b] - RagdollSkeleton.Rest[RagdollSkeleton.Parent[b]]);
            float a0 = MathF.Abs(Vector3.Dot(d, side)), a1 = MathF.Abs(Vector3.Dot(d, up)), a2 = MathF.Abs(Vector3.Dot(d, fwd));
            k[b] = (byte)(a0 <= a1 && a0 <= a2 ? 0 : a1 <= a2 ? 1 : 2);
        }
        return k;
    }

    public static Vector3 SegmentColor(int b) => b switch
    {
        3 or 4 or 5 or 10 or 15 => Skin,
        19 or 23 => Shoe,
        >= 16 => Pants,
        _ => Shirt,
    };

    /// <summary>캡슐 표면 스플랫 바인드(비핫패스) — 장면 슬롯 [start, start+n) 에 휴지 자세로 기록 · 가중치/소속 분절/휴지 법선 반환.</summary>
    public static int Build(GaussianSplatScene scene, int start, int target, BoneWeights4[] weights, Vector3[] restNormal, uint seed = 0xA17A5u)
    {
        var rest = RagdollSkeleton.Rest;
        Span<float> area = stackalloc float[Bones];
        var total = 0f;
        for (var b = 1; b < Bones; b++)
        {
            var r = RagdollSkeleton.BoneRadius[b]; var L = (rest[b] - rest[RagdollSkeleton.Parent[b]]).Length();
            area[b] = 2f * MathF.PI * r * L + 4f * MathF.PI * r * r; total += area[b];
        }
        var rng = seed == 0 ? 1u : seed;
        var n = 0;
        for (var b = 1; b < Bones && n < target; b++)
        {
            var cnt = b == Bones - 1 ? target - n : (int)(target * area[b] / total);
            var A = rest[RagdollSkeleton.Parent[b]]; var B = rest[b];
            var axis = B - A; var L = axis.Length(); axis /= MathF.Max(L, 1e-6f);
            var r = RagdollSkeleton.BoneRadius[b];
            var t1 = Vector3.Normalize(Vector3.Cross(MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, axis)); var t2 = Vector3.Cross(axis, t1);
            var disc = MathF.Sqrt(area[b] / MathF.Max(cnt, 1)) * 0.75f;
            var capFrac = 4f * MathF.PI * r * r / area[b];
            for (var k = 0; k < cnt && n < target; k++)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; var u1 = (rng >> 8) * (1f / 16777216f);
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; var u2 = (rng >> 8) * (1f / 16777216f);
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; var u3 = (rng >> 8) * (1f / 16777216f);
                Vector3 p, nrm; float t;
                if (u3 < capFrac)
                {
                    // 반구 캡(양 끝) — 균일 구면 표본의 반쪽
                    var z = 2f * u1 - 1f; var a = MathF.Tau * u2; var rr = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
                    var dir = t1 * (rr * MathF.Cos(a)) + t2 * (rr * MathF.Sin(a)) + axis * z;
                    var c = z >= 0f ? B : A;
                    p = c + dir * r; nrm = dir; t = z >= 0f ? 1f : 0f;
                }
                else
                {
                    var a = MathF.Tau * u2; nrm = t1 * MathF.Cos(a) + t2 * MathF.Sin(a);
                    t = u1; p = A + axis * (L * t) + nrm * r;
                }
                Frame(nrm, out var ta, out var tb);
                var i = start + n;
                scene.Write(i, p, ta, tb, nrm, new Vector3(disc, disc, disc * 0.18f), 0.95f, SegmentColor(b), 6);
                restNormal[n] = nrm;
                weights[n] = Weights(b, t);
                n++;
            }
        }
        return n;
    }

    private static void Frame(Vector3 n, out Vector3 t, out Vector3 b)
    {
        t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, n));
        b = Vector3.Cross(n, t);
    }

    private static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    /// <summary>분절 b 의 위치 t(0 = 부모 관절 · 1 = 관절 b) 가중치.</summary>
    public static BoneWeights4 Weights(int b, float t)
    {
        var w = new BoneWeights4 { B0 = (byte)b, W0 = 1f };
        var parentSeg = RagdollSkeleton.Parent[b];
        var wp = parentSeg >= 0 ? 0.5f * Smooth(1f - t / Blend) : 0f;
        var child = FirstChild[b];
        var wc = child > 0 ? 0.5f * Smooth((t - (1f - Blend)) / Blend) : 0f;
        w.W0 = 1f - wp - wc;
        if (wp > 0f) { w.B1 = (byte)parentSeg; w.W1 = wp; }
        if (wc > 0f) { w.B2 = (byte)child; w.W2 = wc; }
        return w;
    }

    /// <summary>최단 호 회전 a → b(단위 벡터).</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var ax = Vector3.Cross(MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1f + d));
    }

    /// <summary>
    /// 위치 기반 포즈(24 관절 · 월드) → 분절 본 변환(무할당): 방향 최단 호 + 기준 벡터 비틀림 정렬 · 신장 = 현재/휴지 길이.
    /// 루트(0) = 골반 프레임(좌→우 · 위).
    /// </summary>
    public static void SolveFromPositions(ReadOnlySpan<Vector3> p, RealtimeDualQuaternionGaussianSkinningEngine eng)
    {
        var rest = RagdollSkeleton.Rest;
        Refs(rest, out var rs, out var ru, out var rf);
        Refs(p, out var cs, out var cu, out var cf);
        // 루트: 휴지(side, up) → 현재(side, up)
        var q0 = FromTo(rs, cs);
        var u0 = Vector3.Transform(ru, q0);
        var uP = cu - cs * Vector3.Dot(cu, cs);
        if (uP.LengthSquared() > 1e-8f) q0 = FromTo(Vector3.Normalize(u0 - cs * Vector3.Dot(u0, cs)), Vector3.Normalize(uP)) * q0;
        eng.SetBone(0, q0, rest[0], p[0]);
        for (var b = 1; b < Bones; b++)
        {
            var pa = RagdollSkeleton.Parent[b];
            var d0 = rest[b] - rest[pa]; var d1 = p[b] - p[pa];
            float l0 = d0.Length(), l1 = d1.Length();
            if (!(l1 > 1e-6f) || !float.IsFinite(l1)) { eng.SetBone(b, eng.BoneRotation(pa), rest[pa], p[pa]); continue; }
            d0 /= l0; d1 /= l1;
            var q = FromTo(d0, d1);
            Vector3 r0 = RefKind[b] == 0 ? rs : RefKind[b] == 1 ? ru : rf, r1 = RefKind[b] == 0 ? cs : RefKind[b] == 1 ? cu : cf;
            var a = Vector3.Transform(r0, q); a -= d1 * Vector3.Dot(a, d1);
            var c = r1 - d1 * Vector3.Dot(r1, d1);
            if (a.LengthSquared() > 1e-8f && c.LengthSquared() > 1e-8f) q = FromTo(Vector3.Normalize(a), Vector3.Normalize(c)) * q;
            eng.SetBone(b, q, rest[pa], p[pa], l1 / l0, rest[b] - rest[pa]);
        }
    }
}

/// <summary>
/// 내장 FK 애니메이터(무할당) — 관절 로컬 회전(휴지 프레임 기준) → 전역 회전 G · 관절 위치 P · 분절 본 변환(분절 b = G[부모] · 피벗 부모 관절).
/// 격투 콤보(몸통 비틀기 · 원투 펀치 · 전완 비틀림 · 킥) · 점프(웅크림 → 도약 → 착지) · 팔 들기(겨드랑이) · 전완 비틀림 스트레스(±170°).
/// </summary>
public sealed class AvatarFkAnimator
{
    public const int N = GaussianAvatarRig.Joints;
    private readonly Quaternion[] _local = new Quaternion[N], _g = new Quaternion[N];
    private readonly Vector3[] _p = new Vector3[N];

    public ReadOnlySpan<Vector3> Positions => _p;
    public Quaternion Global(int j) => _g[j];
    public Vector3 Root { get; set; }
    public float Yaw { get; set; }

    private static Quaternion Ax(Vector3 axis, float a) => Quaternion.CreateFromAxisAngle(axis, a);
    private static Vector3 RestDir(RagdollJoint a, RagdollJoint b) => Vector3.Normalize(RagdollSkeleton.Rest[(int)b] - RagdollSkeleton.Rest[(int)a]);

    /// <summary>모션 t(초) 평가 → 위치 · 본 변환 기록.</summary>
    public void Evaluate(AvatarMotion m, float t, RealtimeDualQuaternionGaussianSkinningEngine eng)
    {
        Array.Fill(_local, Quaternion.Identity);
        var lift = 0f;
        switch (m)
        {
            case AvatarMotion.Fight:
            {
                var ph = t % 2.4f;
                var punchR = MathF.Max(0f, MathF.Sin(MathF.PI * Math.Clamp((ph - 0.0f) / 0.5f, 0f, 1f)));
                var punchL = MathF.Max(0f, MathF.Sin(MathF.PI * Math.Clamp((ph - 0.6f) / 0.5f, 0f, 1f)));
                var kick = MathF.Max(0f, MathF.Sin(MathF.PI * Math.Clamp((ph - 1.3f) / 0.8f, 0f, 1f)));
                _local[(int)RagdollJoint.Chest] = Ax(Vector3.UnitY, 0.55f * (punchL - punchR));
                Guard(RagdollJoint.RShoulder, RagdollJoint.RElbow, RagdollJoint.RWrist, punchR, +1f);
                Guard(RagdollJoint.LShoulder, RagdollJoint.LElbow, RagdollJoint.LWrist, punchL, -1f);
                _local[(int)RagdollJoint.RHip] = Ax(Vector3.UnitX, -1.4f * kick);
                _local[(int)RagdollJoint.RKnee] = Ax(Vector3.UnitX, 1.5f * kick * (1f - kick));
                _local[(int)RagdollJoint.LKnee] = Ax(Vector3.UnitX, 0.25f);
                _local[(int)RagdollJoint.LHip] = Ax(Vector3.UnitX, -0.15f);
                break;
            }
            case AvatarMotion.Jump:
            {
                var ph = t % 1.8f;
                var crouch = ph < 0.5f ? MathF.Sin(MathF.PI * ph / 0.5f * 0.5f) : ph > 1.4f ? MathF.Sin(MathF.PI * (1.8f - ph) / 0.4f * 0.5f) : 0f;
                var air = ph is > 0.5f and < 1.4f ? MathF.Sin(MathF.PI * (ph - 0.5f) / 0.9f) : 0f;
                lift = 0.55f * air - 0.28f * crouch;
                for (var s = 0; s < 2; s++)
                {
                    int hip = s == 0 ? (int)RagdollJoint.LHip : (int)RagdollJoint.RHip;
                    _local[hip] = Ax(Vector3.UnitX, -1.1f * crouch - 0.6f * air);
                    _local[hip + 1] = Ax(Vector3.UnitX, 2.0f * crouch + 0.9f * air);
                    _local[hip + 2] = Ax(Vector3.UnitX, -0.9f * crouch - 0.3f * air);
                    int sh = s == 0 ? (int)RagdollJoint.LShoulder : (int)RagdollJoint.RShoulder;
                    _local[sh] = Ax(Vector3.UnitX, -2.6f * air + 0.6f * crouch);
                }
                _local[(int)RagdollJoint.Spine] = Ax(Vector3.UnitX, 0.45f * crouch);
                break;
            }
            case AvatarMotion.ArmsRaise:
            {
                var a = 0.5f + 0.5f * MathF.Sin(t * 1.2f);
                _local[(int)RagdollJoint.LShoulder] = Ax(Vector3.UnitZ, -1.45f * a);
                _local[(int)RagdollJoint.RShoulder] = Ax(Vector3.UnitZ, 1.45f * a);
                _local[(int)RagdollJoint.Neck] = Ax(Vector3.UnitX, 0.25f);
                break;
            }
            case AvatarMotion.TwistStress:
            {
                var tw = 2.97f * MathF.Sin(t * 1.5f);   // ±170°
                _local[(int)RagdollJoint.LShoulder] = Ax(Vector3.UnitZ, -1.3f);
                _local[(int)RagdollJoint.RShoulder] = Ax(Vector3.UnitZ, 1.3f);
                _local[(int)RagdollJoint.LElbow] = Ax(RestDir(RagdollJoint.LElbow, RagdollJoint.LWrist), tw);
                _local[(int)RagdollJoint.RElbow] = Ax(RestDir(RagdollJoint.RElbow, RagdollJoint.RWrist), -tw);
                break;
            }
        }
        Solve(lift, eng);
    }

    /// <summary>원투 펀치 — 어깨 전방 굴곡 · 팔꿈치 신전 · 전완 회내 비틀림(주먹 회전).</summary>
    private void Guard(RagdollJoint sh, RagdollJoint el, RagdollJoint wr, float p, float side)
    {
        _local[(int)sh] = Ax(Vector3.UnitX, -0.9f - 0.7f * p) * Ax(Vector3.UnitZ, side * 0.25f * (1f - p));
        _local[(int)el] = Ax(Vector3.UnitX, -1.9f * (1f - p)) * Ax(RestDir(el, wr), side * 1.6f * p);
    }

    /// <summary>로컬 회전(테스트 · 외부 주입) 설정 후 <see cref="Solve"/>.</summary>
    public void SetLocal(int j, Quaternion q) { if ((uint)j < N) _local[j] = q; }
    public void ClearLocal() => Array.Fill(_local, Quaternion.Identity);

    /// <summary>FK → 위치 · 본 변환 기록(무할당).</summary>
    public void Solve(float lift, RealtimeDualQuaternionGaussianSkinningEngine eng)
    {
        var rest = RagdollSkeleton.Rest;
        var rootRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw);
        _g[0] = rootRot * _local[0];
        _p[0] = Root + Vector3.Transform(rest[0], rootRot) + new Vector3(0f, lift, 0f);
        for (var j = 1; j < N; j++)
        {
            var pa = RagdollSkeleton.Parent[j];
            _g[j] = _g[pa] * _local[j];
            _p[j] = _p[pa] + Vector3.Transform(rest[j] - rest[pa], _g[pa]);
        }
        eng.SetBone(0, _g[0], rest[0], _p[0]);
        for (var b = 1; b < N; b++) { var pa = RagdollSkeleton.Parent[b]; eng.SetBone(b, _g[pa], rest[pa], _p[pa], 1f, rest[b] - rest[pa]); }
    }
}
