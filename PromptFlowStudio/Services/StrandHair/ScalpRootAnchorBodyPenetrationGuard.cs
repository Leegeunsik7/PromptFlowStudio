using System.Numerics;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.StrandHair;

/// <summary>신체 SDF 캡슐(A = B 면 구) · 의상 여부(의상 두께 가산).</summary>
public struct BodyCapsule
{
    public Vector3 A, B;
    public float R;
    public bool Cloth;
    public string Name;
}

/// <summary>
/// [PromptFlow Studio v17.15.0] 🛡️ 두피 모근 앵커링 &amp; 신체 메쉬 상호 관통 방지 가드(무할당 · <see cref="IStrandCollider"/>).
///   ▸ 운동학 락: v17.11/v17.14 SMPL 골격 머리 관절 강체 변환(두피)을 서브스텝마다 보간(위치 선형 · 회전 slerp)해 모근 2점(뿌리 · 뿌리 방향)을 정확 고정 → 모근 오차 0.
///   ▸ SDF: 두개(구) · 목 · 상흉부(쇄골 간) · 흉곽 · 어깨 2 · 상완 2 캡슐(의상 = 흉곽/어깨/상완 + 의상 두께) · 모발 반경 1.5 mm.
///     가이드 입자(i ≥ 2): PBD 반복마다 d &lt; 0 → 기울기(최근접 축점 → 입자) 방향 표면 투영(겹친 캡슐 합집합 ≤ 3 회) + 접선 마찰(직전 위치 대비 접선 이동 μ 감쇠 · 재관통 시 생략).
///   ▸ 자식 스트랜드: 가이드 점이 몸체 확장 경계(최대 자식 오프셋 + 모발 반경) 안인 (가이드, 입자) 칸만 1.64 M 정점 중 후보 검사 → 투영.
///   ▸ 계측: 프레임 원시 관통(각 서브스텝 첫 충돌 패스) · 잔여 관통(스텝 후) · 자식 관통 전/후 · 모근 오차.
/// </summary>
public sealed class ScalpRootAnchorBodyPenetrationGuard : IStrandCollider
{
    public const int Bodies = 9;
    public const float HairRadius = 0.0015f;
    private readonly BodyCapsule[] _b = new BodyCapsule[Bodies];

    public ScalpRootAnchorBodyPenetrationGuard()
    {
        string[] names = { "두개", "목", "상흉부", "흉곽", "어깨(좌)", "어깨(우)", "상완(좌)", "상완(우)", "턱" };
        for (var i = 0; i < Bodies; i++) _b[i].Name = names[i];
        Span<Vector3> j = stackalloc Vector3[MocapSkeleton.Body];
        MocapSkeleton.RestPositions(MocapSkeleton.RestOffset, j);
        UpdateBody(j, Quaternion.Identity);
    }

    public bool CollisionEnabled { get; set; } = true;
    public bool ChildCollision { get; set; } = true;
    public bool InterpolateAnchor { get; set; } = true;
    public float ClothThickness { get; set; } = 0.005f;
    public float Friction { get; set; } = 0.3f;
    public BodyCapsule Body(int i) => _b[i];
    public float BodyRadius(int i) => _b[i].R + (_b[i].Cloth ? ClothThickness : 0f);
    public float RawPenetrationMm { get; private set; }
    public float ResidualPenetrationMm { get; private set; }
    public int Contacts { get; private set; }
    public int ChildPenetrationsBefore { get; private set; }
    public int ChildPenetrationsAfter { get; private set; }
    public int ChildCandidates { get; private set; }
    public float RootErrorMm { get; private set; }
    public double LastCollideUs { get; private set; }
    public double LastChildUs { get; private set; }

    /// <summary>골격 관절(월드) · 머리 전역 회전 → 신체 캡슐 갱신.</summary>
    public void UpdateBody(ReadOnlySpan<Vector3> j, Quaternion headRot)
    {
        var head = j[MocapSkeleton.Head];
        var hc = head + Vector3.Transform(StrandHairPbdDynamicsEngine.HeadCenterLocal, headRot);
        Set(0, hc, hc, StrandHairPbdDynamicsEngine.HeadRadius, false);
        Set(1, j[MocapSkeleton.Neck], head, 0.052f, false);
        var dn = new Vector3(0f, -0.04f, 0f);
        Set(2, j[MocapSkeleton.LCollar] + dn, j[MocapSkeleton.RCollar] + dn, 0.07f, true);
        Set(3, j[MocapSkeleton.Spine2], j[MocapSkeleton.Spine3], 0.1f, true);
        Set(4, j[MocapSkeleton.LCollar], j[MocapSkeleton.LShoulder], 0.055f, true);
        Set(5, j[MocapSkeleton.RCollar], j[MocapSkeleton.RShoulder], 0.055f, true);
        Set(6, j[MocapSkeleton.LShoulder], j[MocapSkeleton.LElbow], 0.045f, true);
        Set(7, j[MocapSkeleton.RShoulder], j[MocapSkeleton.RElbow], 0.045f, true);
        var jaw = head + Vector3.Transform(new Vector3(0f, -0.04f, 0.04f), headRot);
        Set(8, jaw, jaw, 0.06f, false);
    }

    private void Set(int i, Vector3 a, Vector3 b, float r, bool cloth) { _b[i].A = a; _b[i].B = b; _b[i].R = r; _b[i].Cloth = cloth; }

    /// <summary>캡슐 부호 거리(반경 · 의상 포함) · 외향 단위 기울기.</summary>
    public float Distance(int i, Vector3 x, out Vector3 n)
    {
        ref readonly var c = ref _b[i];
        var ab = c.B - c.A;
        var l2 = ab.LengthSquared();
        var t = l2 > 1e-12f ? Math.Clamp(Vector3.Dot(x - c.A, ab) / l2, 0f, 1f) : 0f;
        var q = x - (c.A + ab * t);
        var d = q.Length();
        n = d > 1e-9f ? q / d : Vector3.UnitY;
        return d - BodyRadius(i);
    }

    /// <summary>전 신체 최소 부호 거리(모발 반경 차감).</summary>
    public float Sdf(Vector3 x, out Vector3 n, out int body)
    {
        var best = float.MaxValue; n = Vector3.UnitY; body = -1;
        for (var i = 0; i < Bodies; i++)
        {
            var d = Distance(i, x, out var ni) - HairRadius;
            if (d < best) { best = d; n = ni; body = i; }
        }
        return best;
    }

    public void BeginFrame()
    {
        RawPenetrationMm = 0f; Contacts = 0;
        LastCollideUs = 0;
    }

    public void Anchor(StrandHairPbdDynamicsEngine e, float alpha)
    {
        if (!InterpolateAnchor) { e.AnchorTo(e.HeadPos, e.HeadRot); return; }
        e.AnchorTo(Vector3.Lerp(e.PrevHeadPos, e.HeadPos, alpha), Quaternion.Normalize(Quaternion.Slerp(e.PrevHeadRot, e.HeadRot, alpha)));
    }

    public void Collide(StrandHairPbdDynamicsEngine e, int iteration, int substep)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var measure = iteration == 0;
        if (!CollisionEnabled && !measure) return;
        int G = e.G, N = StrandHairPbdDynamicsEngine.N;
        var raw = RawPenetrationMm * 0.001f;
        var contacts = 0;
        for (var k = 2 * G; k < N * G; k++)
        {
            var x = new Vector3(e.X[k], e.Y[k], e.Z[k]);
            var d = Sdf(x, out var n, out _);
            if (d >= 0f) continue;
            if (measure && -d > raw) raw = -d;
            if (!CollisionEnabled) continue;
            contacts++;
            // 겹친 캡슐 합집합: 최근접 표면 투영 반복(≤ 3)
            for (var pass = 0; pass < 3 && d < 0f; pass++) { x -= n * d; d = Sdf(x, out n, out _); }
            var p = new Vector3(e.PX[k], e.PY[k], e.PZ[k]);
            var mv = x - p;
            var tang = mv - n * Vector3.Dot(mv, n);
            var xf = x - tang * Friction;
            if (Sdf(xf, out _, out _) >= 0f) x = xf;
            e.X[k] = x.X; e.Y[k] = x.Y; e.Z[k] = x.Z;
        }
        RawPenetrationMm = raw * 1000f;
        if (iteration == e.Iterations) Contacts = contacts;
        LastCollideUs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>스텝 후: 가이드 잔여 관통 · 모근 오차(두피 자세 대비).</summary>
    public void MeasureResidual(StrandHairPbdDynamicsEngine e)
    {
        int G = e.G, N = StrandHairPbdDynamicsEngine.N;
        var res = 0f;
        for (var k = 2 * G; k < N * G; k++)
        {
            var d = Sdf(new Vector3(e.X[k], e.Y[k], e.Z[k]), out _, out _);
            if (-d > res) res = -d;
        }
        ResidualPenetrationMm = res * 1000f;
        var err = 0f;
        for (var i = 0; i < 2; i++)
            for (var g = 0; g < G; g++)
            {
                var k = i * G + g;
                var w = e.HeadPos + Vector3.Transform(new Vector3(e.RLx[k], e.RLy[k], e.RLz[k]), e.HeadRot);
                err = MathF.Max(err, Vector3.Distance(w, new Vector3(e.X[k], e.Y[k], e.Z[k])));
            }
        RootErrorMm = err * 1000f;
    }


    /// <summary>자식 레인 × 캡슐 커널: 내부 정점 수 누적 · push 시 표면(반경 R) 투영.</summary>
    private static TV ChildBodyK<TV, L>(StrandHairPbdDynamicsEngine e, int o, Vector3 a, Vector3 b, float r, float rPush, bool push, TV count) where TV : struct where L : struct, IHairLane<TV>
    {
        var ab = b - a;
        var l2 = ab.LengthSquared();
        var inv = l2 > 1e-12f ? 1f / l2 : 0f;
        TV ax = L.S(a.X), ay = L.S(a.Y), az = L.S(a.Z), bx = L.S(ab.X), by = L.S(ab.Y), bz = L.S(ab.Z), il = L.S(inv), R = L.S(r), RP = L.S(rPush);
        TV zero = L.S(0f), one = L.S(1f), eps = L.S(1e-9f);
        for (var c = 0; c < e.C; c += L.Width)
        {
            var k = o + c;
            TV x = L.Load(e.CX, k), y = L.Load(e.CY, k), z = L.Load(e.CZ, k);
            var t = L.Max(zero, L.Min(one, L.Mul(L.Add(L.Add(L.Mul(L.Sub(x, ax), bx), L.Mul(L.Sub(y, ay), by)), L.Mul(L.Sub(z, az), bz)), il)));
            TV cx = L.Add(ax, L.Mul(bx, t)), cy = L.Add(ay, L.Mul(by, t)), cz = L.Add(az, L.Mul(bz, t));
            TV qx = L.Sub(x, cx), qy = L.Sub(y, cy), qz = L.Sub(z, cz);
            var ql = L.Sqrt(L.Add(L.Add(L.Mul(qx, qx), L.Mul(qy, qy)), L.Mul(qz, qz)));
            count = L.Add(count, L.Gt(R, ql, one, zero));
            if (!push) continue;
            var f = L.Div(RP, L.Max(ql, eps));
            L.Store(e.CX, k, L.Gt(RP, ql, L.Add(cx, L.Mul(qx, f)), x));
            L.Store(e.CY, k, L.Gt(RP, ql, L.Add(cy, L.Mul(qy, f)), y));
            L.Store(e.CZ, k, L.Gt(RP, ql, L.Add(cz, L.Mul(qz, f)), z));
        }
        return count;
    }

    private float ChildPass(StrandHairPbdDynamicsEngine e, int o, int mask, bool push)
    {
        float total = 0f;
        for (var b = 0; b < Bodies; b++)
        {
            if ((mask & (1 << b)) == 0) continue;
            ref readonly var c = ref _b[b];
            var r = BodyRadius(b) + HairRadius - 2e-5f; var rp = BodyRadius(b) + HairRadius + 2e-5f;
            switch (e.EffectivePath)
            {
                case HairSimdPath.V256:
                {
                    var v = ChildBodyK<Vector256<float>, V256Lane>(e, o, c.A, c.B, r, rp, push, default);
                    for (var i = 0; i < 8; i++) total += v.GetElement(i);
                    break;
                }
                case HairSimdPath.V128:
                {
                    var v = ChildBodyK<Vector128<float>, V128Lane>(e, o, c.A, c.B, r, rp, push, default);
                    for (var i = 0; i < 4; i++) total += v.GetElement(i);
                    break;
                }
                default: total += ChildBodyK<float, ScalarLane>(e, o, c.A, c.B, r, rp, push, 0f); break;
            }
        }
        return total;
    }

    /// <summary>자식 정점 관통 검사/투영(후보 칸만 · 자식 = SIMD 레인 · 무할당): 투영 2 패스 → 검사 패스.</summary>
    public void CollideChildren(StrandHairPbdDynamicsEngine e)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int G = e.G, C = e.C, N = StrandHairPbdDynamicsEngine.N;
        var margin = e.MaxChildOffset + HairRadius;
        float before = 0, after = 0; var cand = 0;
        for (var g = 0; g < G; g++)
            for (var i = 2; i < N; i++)
            {
                var gp = e.P(i, g);
                var mask = 0;
                for (var b = 0; b < Bodies; b++) if (Distance(b, gp, out _) < margin) mask |= 1 << b;
                if (mask == 0) continue;
                var o = (g * N + i) * C;
                cand += C;
                before += ChildPass(e, o, mask, ChildCollision);
                if (ChildCollision) ChildPass(e, o, mask, true);
                after += ChildPass(e, o, mask, false);
            }
        ChildPenetrationsBefore = (int)before; ChildPenetrationsAfter = (int)after; ChildCandidates = cand;
        LastChildUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
