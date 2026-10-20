using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
namespace PromptFlowStudio.Services.AvatarActing;

/// <summary>관절 가동 범위(라디안) — 비틀림(본 축) · 스윙 성분 1(b1) · 스윙 성분 2(b2) · 스윙 총량 상한.</summary>
public readonly record struct JointLimit(float TwistMin, float TwistMax, float S1Min, float S1Max, float S2Min, float S2Max, float SwingMax);

/// <summary>
/// [PromptFlow Studio v17.60.0] 🦴 해부학적 관절 한계 클램프 &amp; 기괴 모션 방어 가드(<see cref="Apply"/> 무할당).
/// 대상 = 상체 · 머리 13 관절(척추 · 흉추 · 목 · 머리 · 쇄골 · 어깨 · 팔꿈치 · 손목 × 좌우)의 로컬 회전(v17.57 FK 규약 · 휴지 프레임 기준).
/// 쿼터니언 스윙-트위스트 분해 q = S·T(T = 휴지 본 축 a 비틀림 · S = a 에 수직인 회전 벡터 s) → 비틀림 각 · s·b1 · s·b2 성분 박스 + 스윙 총량 클램프 → 재합성(항상 단위 · 재분해 시 한계 내 정확).
/// 팔꿈치 1DoF(굴곡 0~150° · 측방 ±4° · 회내/회외 ±80°) · 손목(굴곡/신전 ±70° · 요/척 편위 · 비틀림 ±10°) · 목+머리 회전 합 ≤ 90°(엑소시스트 차단) ·
/// 비유한/영 쿼터니언 → 직전 안전 회전 · 각속도 상한 14 rad/s 슬루 · 이탈 계측(클램프 전 최대 초과각 · 클램프 후 잔여 0).
/// </summary>
public sealed class BiomechanicalJointLimitSafetyGuard
{
    public const int N = RagdollSkeleton.Count;
    public const float MaxOmega = 14f, Eps = 1e-4f, Deg = 57.29578f;
    private static readonly int[] Targets =
    {
        (int)RagdollJoint.Spine, (int)RagdollJoint.Chest, (int)RagdollJoint.Neck, (int)RagdollJoint.Head,
        (int)RagdollJoint.LClavicle, (int)RagdollJoint.LShoulder, (int)RagdollJoint.LElbow, (int)RagdollJoint.LWrist,
        (int)RagdollJoint.RClavicle, (int)RagdollJoint.RShoulder, (int)RagdollJoint.RElbow, (int)RagdollJoint.RWrist,
    };
    private readonly JointLimit[] _lim = new JointLimit[N];
    private readonly Vector3[] _a = new Vector3[N], _b1 = new Vector3[N], _b2 = new Vector3[N];
    private readonly bool[] _on = new bool[N];
    private readonly Quaternion[] _safe = new Quaternion[N];
    private readonly float[] _exc = new float[N];
    private readonly long[] _hits = new long[N];
    private bool _hasSafe;

    public BiomechanicalJointLimitSafetyGuard()
    {
        var rest = RagdollSkeleton.Rest;
        Span<int> child = stackalloc int[N]; child.Fill(-1);
        for (var c = 1; c < N; c++) { var p = RagdollSkeleton.Parent[c]; if (child[p] < 0) child[p] = c; }
        foreach (var j in Targets)
        {
            var a = Vector3.Normalize(rest[child[j]] - rest[j]);
            var r = MathF.Abs(Vector3.Dot(a, Vector3.UnitX)) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
            var b1 = Vector3.Normalize(r - a * Vector3.Dot(r, a));
            _a[j] = a; _b1[j] = b1; _b2[j] = Vector3.Cross(a, b1); _on[j] = true;
        }
        const float D = 1f / Deg;
        Set(RagdollJoint.Spine, new(-26 * D, 26 * D, -20 * D, 34 * D, -23 * D, 23 * D, 40 * D));
        Set(RagdollJoint.Chest, new(-26 * D, 26 * D, -17 * D, 23 * D, -17 * D, 17 * D, 30 * D));
        Set(RagdollJoint.Neck, new(-70 * D, 70 * D, -35 * D, 45 * D, -34 * D, 34 * D, 50 * D));
        Set(RagdollJoint.Head, new(-20 * D, 20 * D, -23 * D, 28 * D, -20 * D, 20 * D, 32 * D));
        for (var s = 0; s < 2; s++)
        {
            var m = s == 0 ? 1f : -1f;   // 좌(−X 쪽) 외전 = Z 축 음의 회전 → 좌우 거울
            Set(s == 0 ? RagdollJoint.LClavicle : RagdollJoint.RClavicle, new(-12 * D, 12 * D, -15 * D, 15 * D, -15 * D, 15 * D, 20 * D));
            // 어깨: b1(X) 굴곡(전방 거상 = 음) −170°~+50° · b2 외전(좌 = 음) · 비틀림 ±85°
            var sh = s == 0 ? RagdollJoint.LShoulder : RagdollJoint.RShoulder;
            var ab = Vector3.Dot(_b2[(int)sh], Vector3.UnitZ) >= 0f ? 1f : -1f;
            var aMin = (m > 0 ? -170f : -35f) * D * ab; var aMax = (m > 0 ? 35f : 170f) * D * ab;
            Set(sh, new(-85 * D, 85 * D, -170 * D, 50 * D, MathF.Min(aMin, aMax), MathF.Max(aMin, aMax), 175 * D));
            // 팔꿈치: 휴지 전완 = 11.5° 굴곡 → 로컬 X 굴곡(음) [−(150 − 11.5)°, +11.5°] · 측방 ±4° · 회내/회외 ±80°
            Set(s == 0 ? RagdollJoint.LElbow : RagdollJoint.RElbow, new(-80 * D, 80 * D, -(150f - RestElbowDeg) * D, RestElbowDeg * D, -4 * D, 4 * D, 150 * D));
            Set(s == 0 ? RagdollJoint.LWrist : RagdollJoint.RWrist, new(-10 * D, 10 * D, -70 * D, 70 * D, -25 * D, 25 * D, 75 * D));
        }
    }

    /// <summary>v17.33 이족 휴지 전완 사전 굴곡(0.2 rad).</summary>
    public const float RestElbowDeg = 0.2f * Deg;

    private void Set(RagdollJoint j, JointLimit l) => _lim[(int)j] = l;

    public bool Enabled { get; set; } = true;
    public bool SlewEnabled { get; set; } = true;
    public static ReadOnlySpan<int> Joints => Targets;
    public JointLimit Limit(int j) => _lim[j];
    public bool Guarded(int j) => _on[j];
    public Vector3 TwistAxis(int j) => _a[j];
    public Vector3 SwingAxis1(int j) => _b1[j];
    public Vector3 SwingAxis2(int j) => _b2[j];
    public float Excess(int j) => _exc[j];
    public long Hits(int j) => _hits[j];
    public int FrameClamps { get; private set; }
    public long Clamps { get; private set; }
    public long SlewClamps { get; private set; }
    public long NanFixes { get; private set; }
    public float FrameMaxExcessDeg { get; private set; }
    public float PeakExcessDeg { get; private set; }
    /// <summary>가드 후 잔여 ROM 이탈(°) — 0 이어야 함.</summary>
    public float ResidualDeg { get; private set; }
    public int ResidualJoints { get; private set; }
    public double LastGuardUs { get; private set; }

    public void ResetMetrics() { Clamps = SlewClamps = NanFixes = 0; PeakExcessDeg = 0f; Array.Clear(_hits); }
    public void ResetSafe() => _hasSafe = false;

    /// <summary>스윙-트위스트 분해(q 단위 · w ≥ 0 정규화) — 비틀림 각 τ ∈ [−π, π] · 스윙 회전 벡터 s(⊥ a).</summary>
    public static void Decompose(Quaternion q, Vector3 a, out float twist, out Vector3 swing)
    {
        if (q.W < 0f) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        var v = new Vector3(q.X, q.Y, q.Z);
        var p = Vector3.Dot(v, a);
        var tl = MathF.Sqrt(p * p + q.W * q.W);
        Quaternion tw;
        if (tl < 1e-9f) { twist = MathF.PI; tw = new Quaternion(a, 0f); }
        else { tw = new Quaternion(a * (p / tl), q.W / tl); twist = 2f * MathF.Atan2(p, q.W); }
        var sw = q * Quaternion.Conjugate(tw);
        if (sw.W < 0f) sw = new Quaternion(-sw.X, -sw.Y, -sw.Z, -sw.W);
        var sv = new Vector3(sw.X, sw.Y, sw.Z);
        sv -= a * Vector3.Dot(sv, a);
        var sl = sv.Length();
        swing = sl < 1e-9f ? Vector3.Zero : sv * (2f * MathF.Atan2(sl, sw.W) / sl);
        if (twist > MathF.PI) twist -= MathF.Tau; else if (twist < -MathF.PI) twist += MathF.Tau;
    }

    public static Quaternion Compose(Vector3 a, float twist, Vector3 swing)
    {
        var sa = swing.Length();
        var s = sa < 1e-9f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(swing / sa, sa);
        return Quaternion.Normalize(s * Quaternion.CreateFromAxisAngle(a, twist));
    }

    /// <summary>관절 j 로컬 회전의 한계 초과각(rad · 0 = 범위 내).</summary>
    public float Measure(int j, Quaternion q)
    {
        if (!_on[j]) return 0f;
        Decompose(q, _a[j], out var tw, out var sw);
        var l = _lim[j];
        float s1 = Vector3.Dot(sw, _b1[j]), s2 = Vector3.Dot(sw, _b2[j]);
        var e = MathF.Max(MathF.Max(tw - l.TwistMax, l.TwistMin - tw), 0f);
        e = MathF.Max(e, MathF.Max(s1 - l.S1Max, l.S1Min - s1));
        e = MathF.Max(e, MathF.Max(s2 - l.S2Max, l.S2Min - s2));
        e = MathF.Max(e, sw.Length() - l.SwingMax);
        return MathF.Max(e, 0f);
    }

    /// <summary>관절 j 클램프(무할당) — 범위 내면 원본 그대로.</summary>
    public Quaternion Clamp(int j, Quaternion q, out bool clamped)
    {
        clamped = false;
        if (!_on[j]) return q;
        Decompose(q, _a[j], out var tw, out var sw);
        var l = _lim[j];
        float s1 = Vector3.Dot(sw, _b1[j]), s2 = Vector3.Dot(sw, _b2[j]);
        float tc = Math.Clamp(tw, l.TwistMin, l.TwistMax), c1 = Math.Clamp(s1, l.S1Min, l.S1Max), c2 = Math.Clamp(s2, l.S2Min, l.S2Max);
        var mag = MathF.Sqrt(c1 * c1 + c2 * c2);
        if (mag > l.SwingMax) { c1 *= l.SwingMax / mag; c2 *= l.SwingMax / mag; }
        if (tc == tw && c1 == s1 && c2 == s2) return q;
        clamped = true;
        return Compose(_a[j], tc, _b1[j] * c1 + _b2[j] * c2);
    }

    /// <summary>로컬 회전 배열(24) 가드 — 비유한 복구 → 각속도 슬루 → ROM 클램프 → 잔여 계측 · 안전 회전 저장.</summary>
    public void Apply(Span<Quaternion> local, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var frame = 0; var peak = 0f; var res = 0f; var rj = 0;
        var maxStep = MaxOmega * MathF.Max(dt, 1e-4f);
        for (var j = 0; j < N; j++)
        {
            var q = local[j];
            var n = q.Length();
            if (!(n > 1e-6f) || !float.IsFinite(n)) { q = _hasSafe ? _safe[j] : Quaternion.Identity; NanFixes++; }
            else q *= 1f / n;
            if (!_on[j]) { local[j] = q; _safe[j] = q; continue; }
            var e = Measure(j, q);
            _exc[j] = e;
            if (e > peak) peak = e;
            if (Enabled)
            {
                if (e > Eps) { q = Clamp(j, q, out _); frame++; _hits[j]++; }
                if (SlewEnabled && _hasSafe)
                {
                    var d = Quaternion.Dot(_safe[j], q);
                    var ang = 2f * MathF.Acos(MathF.Min(MathF.Abs(d), 1f));
                    if (ang > maxStep) { q = Clamp(j, Quaternion.Slerp(_safe[j], q, maxStep / ang), out _); SlewClamps++; }
                }
            }
            var r = Measure(j, q);
            if (r > Eps) { rj++; if (r > res) res = r; }
            local[j] = q; _safe[j] = q;
        }
        _hasSafe = true;
        FrameClamps = frame; Clamps += frame;
        FrameMaxExcessDeg = peak * Deg; if (FrameMaxExcessDeg > PeakExcessDeg) PeakExcessDeg = FrameMaxExcessDeg;
        ResidualDeg = res * Deg; ResidualJoints = rj;
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>팔꿈치 해부학 굴곡각(°) = 휴지 11.5° − 로컬 X 스윙.</summary>
    public float ElbowFlexDeg(int j, Quaternion q)
    {
        Decompose(q, _a[j], out _, out var sw);
        return RestElbowDeg - Vector3.Dot(sw, _b1[j]) * Deg;
    }

    public static string JointName(int j) => (RagdollJoint)j switch
    {
        RagdollJoint.Spine => "요추", RagdollJoint.Chest => "흉추", RagdollJoint.Neck => "목", RagdollJoint.Head => "머리",
        RagdollJoint.LClavicle => "L 쇄골", RagdollJoint.LShoulder => "L 어깨", RagdollJoint.LElbow => "L 팔꿈치", RagdollJoint.LWrist => "L 손목",
        RagdollJoint.RClavicle => "R 쇄골", RagdollJoint.RShoulder => "R 어깨", RagdollJoint.RElbow => "R 팔꿈치", RagdollJoint.RWrist => "R 손목",
        _ => ((RagdollJoint)j).ToString(),
    };
}
