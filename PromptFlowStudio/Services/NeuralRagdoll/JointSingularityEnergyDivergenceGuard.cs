using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.NeuralRagdoll;

/// <summary>
/// [PromptFlow Studio v17.37.0] 🛡 관절 특이점 꼬임 &amp; 각속도 폭주 방어 가드(무할당 · 엔진 서브스텝 훅).
///   ▸ 관절 한계(<see cref="ProjectLimits"/> · 서브스텝당 1 회 · 속도 수준 비탄성 + 초과각 × 6 /s 복원 ≤ 4 rad/s): 원뿔 16(척추 35° · 흉추 25° · 목 50° · 두개 35° · 어깨 135° · 팔꿈치/무릎 굴곡 ≤ 150° · 손목 95°(푸시업 손바닥 짚기) · 고관절 = 골반 하방 45° 전방 축 95°(깊은 굴곡 140°) ·
///     발목 10~160°(발가락 접지 포함)) — 초과 시 자식 서브트리 상대 각속도 교정 · 축 반평행(특이점)이면 골반 전방/우측 안정 축으로 수직 기저 대체.
///   ▸ 힌지 특이점: 무릎(발끝 방향 기준) 굽힘 평면 뒤집힘(완전 신전 통과 · 과신전) → 2-본 IK 해부학 쪽 해로 복귀 속도 · 척추 비틀림(견갑대 ↔ 골반선) ±45° 초과 시 흉곽 서브트리 비틀림 각속도 교정.
///   ▸ 각속도(<see cref="ClampVelocities"/>): 본별 ω = r × v_rel ÷ |r|² — 해부학 상한(체간 20 · 목/머리 25 · 사지 35 · 손/발 45 rad/s) · 프레임당 가속 변화율 ≤ 25 rad/s(1500 rad/s² · 감속은 비제한) ·
///     선속도 ≤ 20 m/s.
///   ▸ 해밀토니안(<see cref="EndFrame"/>): H = Σ½mv² + Σmgy ≤ H_prev + 주입 충격 에너지 + 모터 파워 2 kW·dt + 1 J — 초과분만큼 동적 관절 상대 속도 감쇠 · 비유한/골반 3 m 이탈(분해) = 롤백.
///   ▸ 관측(가드 OFF 에도 측정): 한계 초과각 · 뒤집힌 힌지 · 최대 ω · H 비 · 비유한 → ON/OFF 비교.
/// </summary>
public sealed class JointSingularityEnergyDivergenceGuard
{
    public const int N = RagdollSkeleton.Count;
    public const float RateMax = 1500f, VMax = 20f, MotorPowerW = 2000f, FlipTol = 0.01f, TwistMaxDeg = 45f, ExplodeDist = 3f, Restore = 20f, RestoreMax = 12f;

    private enum Ref : byte { Parent, Hip }
    private readonly record struct Limit(byte Pivot, byte Child, Ref Axis, float MinDeg, float MaxDeg, string Name);

    private static readonly Limit[] Limits =
    {
        new(1, 2, Ref.Parent, 0f, 35f, "척추"), new(2, 3, Ref.Parent, 0f, 25f, "흉추"), new(3, 4, Ref.Parent, 0f, 50f, "목"), new(4, 5, Ref.Parent, 0f, 35f, "두개"),
        new(7, 8, Ref.Parent, 0f, 135f, "L 어깨"), new(12, 13, Ref.Parent, 0f, 135f, "R 어깨"),
        new(8, 9, Ref.Parent, 0f, 150f, "L 팔꿈치"), new(13, 14, Ref.Parent, 0f, 150f, "R 팔꿈치"),
        new(9, 10, Ref.Parent, 0f, 95f, "L 손목"), new(14, 15, Ref.Parent, 0f, 95f, "R 손목"),
        new(16, 17, Ref.Hip, 0f, 95f, "L 고관절"), new(20, 21, Ref.Hip, 0f, 95f, "R 고관절"),
        new(17, 18, Ref.Parent, 0f, 150f, "L 무릎"), new(21, 22, Ref.Parent, 0f, 150f, "R 무릎"),
        new(18, 19, Ref.Parent, 10f, 160f, "L 발목"), new(22, 23, Ref.Parent, 10f, 160f, "R 발목"),
    };
    /// <summary>무릎 힌지(중간 · 루트 · 끝) — 해부학 전방 기준 = 발 방향(발목→발끝): 무릎은 발끝 쪽으로 굽는다.</summary>
    private static readonly byte[] HingeMid = { 17, 21 }, HingeRoot = { 16, 20 }, HingeEnd = { 18, 22 };
    private static readonly float[] OmegaMax = BuildOmegaMax();

    private readonly Vector3[] _omPrev = new Vector3[N];
    private uint _dynMask;
    private float _hPrev;
    private bool _hInit;

    public bool Enabled { get; set; } = true;
    /// <summary>세부 단계 토글(진단 · 비교).</summary>
    public bool LimitStage { get; set; } = true;
    public bool TwistStage { get; set; } = true;
    public bool VelocityStage { get; set; } = true;
    public bool EnergyStage { get; set; } = true;
    public static int LimitCount => Limits.Length;
    public static string LimitName(int i) => Limits[i].Name;
    public static float LimitMaxDeg(int i) => Limits[i].MaxDeg;
    public static float OmegaLimit(int j) => OmegaMax[j];

    public long LimitClamps { get; private set; }
    public long HingeFlips { get; private set; }
    public long TwistClamps { get; private set; }
    public long OmegaClamps { get; private set; }
    public long RateClamps { get; private set; }
    public long EnergyClamps { get; private set; }
    public long Rollbacks { get; private set; }
    /// <summary>관측(프레임 끝) — 가드 OFF 에도 측정.</summary>
    public float LimitExcessDeg { get; private set; }
    public float MaxLimitExcessDeg { get; private set; }
    public int Flipped { get; private set; }
    public int MaxFlipped { get; private set; }
    public float TwistDeg { get; private set; }
    public float MaxTwistDeg { get; private set; }
    public float PeakOmega { get; private set; }
    public float MaxOmega { get; private set; }
    public float EnergyRatio { get; private set; }
    public float MaxEnergyRatio { get; private set; }
    public float RemovedJ { get; private set; }
    public bool NonFinite { get; private set; }
    public long NonFiniteFrames { get; private set; }
    private readonly float[] _angle = new float[16];
    public float Angle(int i) => _angle[i];

    public void ResetMetrics()
    {
        LimitClamps = HingeFlips = TwistClamps = OmegaClamps = RateClamps = EnergyClamps = Rollbacks = NonFiniteFrames = 0;
        MaxLimitExcessDeg = MaxTwistDeg = MaxOmega = MaxEnergyRatio = RemovedJ = 0f; MaxFlipped = 0;
    }

    private static float[] BuildOmegaMax()
    {
        var o = new float[N];
        for (var j = 0; j < N; j++)
            o[j] = j <= 2 || j == 16 || j == 20 ? 20f : j <= 5 ? 25f : j is 10 or 15 or 19 or 23 ? 45f : 35f;
        return o;
    }

    // ══════════════════════ 엔진 훅(무할당) ══════════════════════

    /// <summary>충격 직후 — 각속도 · 선속도 상한(첫 서브스텝 전).</summary>
    public void AfterImpulse(RealtimeHybridNeuralRagdollEngine e)
    {
        if (!Enabled) return;
        ClampCore(e, float.PositiveInfinity, false);
    }

    public void BeginFrame(RealtimeHybridNeuralRagdollEngine e, float dt)
    {
        for (var j = 1; j < N; j++) _omPrev[j] = BoneOmega(e, j);
        if (Enabled && VelocityStage) ClampCore(e, RateMax * dt, true);
    }

    /// <summary>
    /// 관절 한계 원뿔 · 힌지 뒤집힘 · 척추 비틀림(서브스텝당 1 회 · 속도 수준) — 위치 순간 투영은 PBD 에서 속도 킥/에너지 폭주를 만들므로
    /// 위반 관절의 자식 서브트리 상대 각속도에서 한계 바깥 성분을 제거하고 초과각 비례 복원 각속도(≤ 4 rad/s)를 부여(비탄성 한계 + 바움가르테).
    /// 변위(x − x_prev) 수정 = 다음 적분부터 반영 · 위치 불연속 없음.
    /// </summary>
    public void ProjectLimits(RealtimeHybridNeuralRagdollEngine e, float h)
    {
        if (!Enabled) return;
        Frame(e, out var fwd, out var up, out var right);
        for (var i = 0; i < (LimitStage ? Limits.Length : 0); i++)
        {
            ref readonly var L = ref Limits[i];
            int pv = L.Pivot, ch = L.Child;
            if (e.IM[ch] == 0f) continue;
            var axis = Axis(e, in L, fwd, up);
            var jp = e.Position(pv);
            var c = e.Position(ch) - jp;
            var len = c.Length();
            if (len < 1e-6f) continue;
            var cd = c / len;
            var cos = Math.Clamp(Vector3.Dot(axis, cd), -1f, 1f);
            var ang = MathF.Acos(cos) * 57.29578f;
            float ex, sign;
            if (ang > L.MaxDeg) { ex = ang - L.MaxDeg; sign = 1f; }
            else if (ang < L.MinDeg) { ex = L.MinDeg - ang; sign = -1f; }
            else continue;
            var perp = cd - axis * cos;
            var pl = perp.Length();
            if (pl < 1e-4f)
            {
                // 특이점(축 평행/반평행) — 안정 기준축으로 수직 기저 대체
                perp = fwd - axis * Vector3.Dot(axis, fwd);
                if (perp.LengthSquared() < 1e-6f) perp = right - axis * Vector3.Dot(axis, right);
                pl = perp.Length();
            }
            perp /= pl;
            var k = Vector3.Cross(axis, perp);                 // +회전 = 각 증가
            var t = Vector3.Cross(k, cd);                       // 자식 위치 각 증가 접선
            var rel = (e.Position(ch) - e.Prev(ch)) - (jp - e.Prev(pv));
            var om = Vector3.Dot(rel, t) / (len * h) * sign;    // 바깥(+) 각속도 rad/s
            var target = -MathF.Min(ex / 57.29578f * Restore, RestoreMax);
            if (om <= target) continue;
            ApplyAngular(e, ch, jp, k * ((om - target) * sign), h);
            LimitClamps++;
        }
        for (var n = 0; n < HingeMid.Length; n++)
        {
            int m = HingeMid[n], a = HingeRoot[n], b = HingeEnd[n];
            if (e.IM[m] == 0f) continue;
            var pa = e.Position(a); var pb = e.Position(b); var pm = e.Position(m);
            var refDir = BipedSkeleton.Dir(e.Position(b + 1) - pb, fwd);
            var off = pm - 0.5f * (pa + pb);
            var ab = pb - pa; var abl = ab.Length();
            if (abl < 1e-5f) continue;
            var abn = ab / abl;
            var rp = refDir - abn * Vector3.Dot(abn, refDir);
            if (rp.LengthSquared() < 1e-4f) continue;
            if (Vector3.Dot(off, rp) >= -FlipTol) continue;
            // 굽힘 평면 뒤집힘(특이점 통과) — 해부학 쪽 해로 향하는 상대 속도(≤ 1.5 m/s)
            var fixedMid = BipedSkeleton.TwoBone(pa, pb, rp, (pm - pa).Length(), (pb - pm).Length());
            var d = fixedMid - pm; var dl = d.Length();
            if (dl < 1e-5f) continue;
            var want = d / dl * MathF.Min(dl / h * 0.5f, 1.5f) + (e.Position(a) - e.Prev(a)) / h * 0.5f + (e.Position(b) - e.Prev(b)) / h * 0.5f;
            e.SetPrev(m, e.Position(m) - want * h);
            HingeFlips++;
        }
        var tw = Twist(e, out var spineAxis);
        var excess = MathF.Abs(tw) - TwistMaxDeg;
        if (TwistStage && excess > 0f)
        {
            // 흉곽 서브트리(흉추 이하 상체) 척추축 상대 비틀림 각속도 — 바깥 성분 제거 + 복원
            var sg = MathF.Sign(tw);
            var pivot = e.Position(1);
            var sh = e.Position(12) - e.Position(7);
            var shd = sh - (e.Prev(12) - e.Prev(7));
            var r2 = sh.LengthSquared();
            if (r2 > 1e-6f)
            {
                var om = Vector3.Dot(Vector3.Cross(sh, shd), spineAxis) / (r2 * h) * sg;
                var target = -MathF.Min(excess / 57.29578f * Restore, RestoreMax);
                if (om > target) { ApplyAngular(e, 2, pivot, spineAxis * ((om - target) * sg), h); TwistClamps++; }
            }
        }
    }

    /// <summary>서브트리(child 포함) 강체 상대 각속도 −Δω 적용(변위 수정 · 위치 불변).</summary>
    private static void ApplyAngular(RealtimeHybridNeuralRagdollEngine e, int child, Vector3 pivot, Vector3 dOmega, float h)
    {
        for (var j = child; j < N; j++)
        {
            if (j != child && !InSubtree(child, j)) continue;
            if (e.IM[j] == 0f) continue;
            e.SetPrev(j, e.Prev(j) + Vector3.Cross(dOmega, e.Position(j) - pivot) * h);
        }
    }

    /// <summary>j 가 root 의 자손인지(부모 인덱스 &lt; 자식 위상 정렬).</summary>
    private static bool InSubtree(int root, int j)
    {
        var p = (int)RagdollSkeleton.Parent[j];
        while (p > root) p = RagdollSkeleton.Parent[p];
        return p == root;
    }

    /// <summary>본 각속도 · 프레임 변화율 · 선속도 상한(서브스텝 속도 갱신 후).</summary>
    public void ClampVelocities(RealtimeHybridNeuralRagdollEngine e, float dt)
    {
        if (!Enabled || !VelocityStage) return;
        ClampCore(e, RateMax * dt, true);
    }

    private void ClampCore(RealtimeHybridNeuralRagdollEngine e, float rate, bool useRate)
    {
        for (var j = 0; j < N; j++)
        {
            if (e.IM[j] == 0f) continue;
            var v = e.Velocity(j);
            var s = v.Length();
            if (s > VMax || !float.IsFinite(s)) { e.SetVelocity(j, float.IsFinite(s) ? v * (VMax / s) : Vector3.Zero); OmegaClamps++; }
        }
        for (var j = 1; j < N; j++)
        {
            if (e.IM[j] == 0f) continue;
            var p = RagdollSkeleton.Parent[j];
            var r = e.Position(j) - e.Position(p);
            var r2 = r.LengthSquared();
            if (r2 < 1e-8f) continue;
            var vp = e.Velocity(p);
            var vrel = e.Velocity(j) - vp;
            var om = Vector3.Cross(r, vrel) / r2;
            var mag = om.Length();
            var changed = false;
            if (mag > OmegaMax[j]) { om *= OmegaMax[j] / mag; changed = true; OmegaClamps++; }
            if (useRate)
            {
                var d = om - _omPrev[j];
                var dl = d.Length();
                // 가속(|ω| 증가)만 제한 — 감속 제한은 에너지 주입이 됨
                if (dl > rate && om.LengthSquared() > _omPrev[j].LengthSquared()) { om = _omPrev[j] + d * (rate / dl); changed = true; RateClamps++; }
            }
            if (!changed) continue;
            var vpar = r * (Vector3.Dot(r, vrel) / r2);
            e.SetVelocity(j, vp + vpar + Vector3.Cross(om, r));
        }
    }

    /// <summary>해밀토니안 예산 · 비유한 롤백 · 관측.</summary>
    public void EndFrame(RealtimeHybridNeuralRagdollEngine e, float dt)
    {
        uint mask = 0; float ke = 0f, pe = 0f, keDyn = 0f; var finite = true;
        var pelvis = e.Position(0);
        for (var j = 0; j < N; j++)
        {
            var m = RagdollSkeleton.Mass[j];
            var v = e.Velocity(j); var p = e.Position(j);
            var k = 0.5f * m * v.LengthSquared();
            ke += k; pe += m * RealtimeHybridNeuralRagdollEngine.G * p.Y;
            if (e.IM[j] > 0f) { mask |= 1u << j; keDyn += k; }
            if (!float.IsFinite(p.X + p.Y + p.Z + v.X + v.Y + v.Z) || (p - pelvis).LengthSquared() > ExplodeDist * ExplodeDist) finite = false;
        }
        NonFinite = !finite;
        if (!finite)
        {
            NonFiniteFrames++;
            if (Enabled) { e.Rollback(); Rollbacks++; _hInit = false; Observe(e); return; }
        }
        var h = ke + pe;
        if (!_hInit || mask != _dynMask || mask == 0) { _hPrev = h; _hInit = true; _dynMask = mask; EnergyRatio = 1f; Observe(e); return; }
        var budget = _hPrev + e.InjectedJ + MotorPowerW * dt + 1f;
        var span = MathF.Max(1f, budget - pe);
        EnergyRatio = finite ? MathF.Max(0f, h - pe) / span : float.PositiveInfinity;
        if (finite && EnergyRatio > MaxEnergyRatio) MaxEnergyRatio = EnergyRatio;
        if (Enabled && EnergyStage && finite && h > budget && keDyn > 1e-6f)
        {
            var excess = h - budget;
            var target = MathF.Max(0f, keDyn - excess);
            var s = MathF.Sqrt(target / keDyn);
            for (var j = 0; j < N; j++) if (e.IM[j] > 0f) e.SetVelocity(j, e.Velocity(j) * s);
            RemovedJ += keDyn - target;
            EnergyClamps++;
            h = budget;
        }
        _hPrev = h;
        Observe(e);
    }

    // ══════════════════════ 관측 ══════════════════════

    private void Observe(RealtimeHybridNeuralRagdollEngine e)
    {
        Frame(e, out var fwd, out var up, out _);
        var worst = 0f;
        for (var i = 0; i < Limits.Length; i++)
        {
            ref readonly var L = ref Limits[i];
            var c = e.Position(L.Child) - e.Position(L.Pivot);
            var len = c.Length();
            if (len < 1e-6f || !float.IsFinite(len)) { _angle[i] = 0f; continue; }
            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(Axis(e, in L, fwd, up), c / len), -1f, 1f)) * 57.29578f;
            _angle[i] = ang;
            if (e.IM[L.Child] == 0f) continue;
            var ex = MathF.Max(ang - L.MaxDeg, L.MinDeg - ang);
            if (ex > worst) worst = ex;
        }
        var flipped = 0;
        for (var k = 0; k < HingeMid.Length; k++)
        {
            int m = HingeMid[k];
            if (e.IM[m] == 0f) continue;
            var pa = e.Position(HingeRoot[k]); var pb = e.Position(HingeEnd[k]);
            var ab = pb - pa; var abl = ab.Length();
            if (abl < 1e-5f) continue;
            var abn = ab / abl; var refDir = BipedSkeleton.Dir(e.Position(HingeEnd[k] + 1) - pb, fwd);
            var rp = refDir - abn * Vector3.Dot(abn, refDir);
            if (rp.LengthSquared() < 1e-4f) continue;
            if (Vector3.Dot(e.Position(m) - 0.5f * (pa + pb), rp) < -FlipTol * 2f) flipped++;
        }
        var tw = MathF.Abs(Twist(e, out _));
        if (tw - TwistMaxDeg > worst && (e.IM[7] > 0f || e.IM[12] > 0f)) worst = tw - TwistMaxDeg;
        var peak = 0f;
        for (var j = 1; j < N; j++)
        {
            if (e.IM[j] == 0f) continue;
            var o = BoneOmega(e, j).Length();
            if (o > peak || !float.IsFinite(o)) peak = o;
        }
        LimitExcessDeg = MathF.Max(0f, worst); Flipped = flipped; TwistDeg = tw; PeakOmega = peak;
        if (LimitExcessDeg > MaxLimitExcessDeg || !float.IsFinite(LimitExcessDeg)) MaxLimitExcessDeg = LimitExcessDeg;
        if (flipped > MaxFlipped) MaxFlipped = flipped;
        if (tw > MaxTwistDeg) MaxTwistDeg = tw;
        if (peak > MaxOmega || !float.IsFinite(peak)) MaxOmega = peak;
    }

    private static Vector3 BoneOmega(RealtimeHybridNeuralRagdollEngine e, int j)
    {
        var p = RagdollSkeleton.Parent[j];
        var r = e.Position(j) - e.Position(p);
        var r2 = r.LengthSquared();
        return r2 < 1e-8f ? Vector3.Zero : Vector3.Cross(r, e.Velocity(j) - e.Velocity(p)) / r2;
    }

    private static Vector3 Axis(RealtimeHybridNeuralRagdollEngine e, in Limit L, Vector3 fwd, Vector3 up)
    {
        if (L.Axis == Ref.Hip) return Vector3.Normalize(-up * 0.7071068f + fwd * 0.7071068f);
        var u = e.Position(L.Pivot) - e.Position(RagdollSkeleton.Parent[L.Pivot]);
        var l = u.Length();
        return l > 1e-6f ? u / l : -up;
    }

    private static void Frame(RealtimeHybridNeuralRagdollEngine e, out Vector3 fwd, out Vector3 up, out Vector3 right)
    {
        var q = RagdollSkeleton.PelvisFrame(e.Position(0), e.Position(1), e.Position(16), e.Position(20), e.PelvisRotation);
        fwd = Vector3.Transform(Vector3.UnitZ, q); up = Vector3.Transform(Vector3.UnitY, q); right = Vector3.Transform(Vector3.UnitX, q);
    }

    /// <summary>척추 비틀림(°) — 골반 고관절선 ↔ 견갑대(어깨선)를 척추축 수직면에 투영한 부호 각.</summary>
    private static float Twist(RealtimeHybridNeuralRagdollEngine e, out Vector3 axis)
    {
        axis = BipedSkeleton.Dir(e.Position(2) - e.Position(0), Vector3.UnitY);
        var hip = e.Position(20) - e.Position(16); hip -= axis * Vector3.Dot(axis, hip);
        var sh = e.Position(12) - e.Position(7); sh -= axis * Vector3.Dot(axis, sh);
        if (hip.LengthSquared() < 1e-8f || sh.LengthSquared() < 1e-8f) return 0f;
        return MathF.Atan2(Vector3.Dot(Vector3.Cross(hip, sh), axis), Vector3.Dot(hip, sh)) * 57.29578f;
    }
}
