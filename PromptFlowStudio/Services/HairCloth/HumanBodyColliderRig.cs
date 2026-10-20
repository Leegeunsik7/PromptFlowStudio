using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.HairCloth;

/// <summary>
/// [PromptFlow Studio v16.83.0] 디지털 휴먼 바디 충돌 리그(헤어 · 의상 공유 · 무할당).
/// 머리 = v16.82 얼굴 메시와 같은 머리 좌표 타원체(중심 원점 · +Z 정면 · +Y 위) · 몸 = 캡슐 13(목 · 어깨 · 몸통 2 · 골반 · 팔 4 · 다리 4).
/// 포즈 = 몸 요(Y축) + 이동 · 머리 피치/요(목 피벗) · 다리 스윙(골반 피벗 X축).
/// </summary>
public sealed class HumanBodyColliderRig
{
    public const int CapsuleCount = 13;
    /// <summary>충돌 타원체 반경(두피 + 3 mm) · 두피(모근) 반경.</summary>
    public static readonly Vector3 HeadRadii = new(0.083f, 0.117f, 0.1f), ScalpRadii = new(0.08f, 0.114f, 0.097f);
    public static readonly Vector3 NeckPivot = new(0f, -0.1f, -0.01f);

    private static readonly Vector3[] LocalA =
    {
        new(0f, -0.06f, -0.01f), new(-0.17f, -0.25f, -0.01f), new(-0.06f, -0.3f, 0f), new(0.06f, -0.3f, 0f), new(-0.1f, -0.85f, 0f),
        new(-0.245f, -0.27f, -0.01f), new(0.245f, -0.27f, -0.01f), new(-0.275f, -0.55f, 0f), new(0.275f, -0.55f, 0f),
        new(-0.09f, -0.9f, 0f), new(0.09f, -0.9f, 0f), new(-0.09f, -1.25f, 0f), new(0.09f, -1.25f, 0f),
    };
    private static readonly Vector3[] LocalB =
    {
        new(0f, -0.2f, -0.01f), new(0.17f, -0.25f, -0.01f), new(-0.06f, -0.8f, 0f), new(0.06f, -0.8f, 0f), new(0.1f, -0.85f, 0f),
        new(-0.275f, -0.55f, 0f), new(0.275f, -0.55f, 0f), new(-0.29f, -0.8f, 0.02f), new(0.29f, -0.8f, 0.02f),
        new(-0.09f, -1.25f, 0f), new(0.09f, -1.25f, 0f), new(-0.09f, -1.62f, 0f), new(0.09f, -1.62f, 0f),
    };
    private static readonly float[] Radii = { 0.05f, 0.065f, 0.11f, 0.11f, 0.11f, 0.045f, 0.045f, 0.04f, 0.04f, 0.075f, 0.075f, 0.055f, 0.055f };
    /// <summary>다리 스윙 부호(왼 +1 · 오른 −1 · 비다리 0).</summary>
    private static readonly sbyte[] Leg = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, -1, 1, -1 };
    /// <summary>헤어 검사 대상(목 · 어깨 · 몸통 · 상완).</summary>
    private static readonly bool[] HairCap = { true, true, true, true, false, true, true, false, false, false, false, false, false };

    private readonly Vector3[] _wa = new Vector3[CapsuleCount], _wb = new Vector3[CapsuleCount];
    private readonly float[] _hax = new float[CapsuleCount], _hay = new float[CapsuleCount], _haz = new float[CapsuleCount];
    private readonly float[] _hdx = new float[CapsuleCount], _hdy = new float[CapsuleCount], _hdz = new float[CapsuleCount], _hinv = new float[CapsuleCount], _hr = new float[CapsuleCount];
    private int _hn;

    public HumanBodyColliderRig() => SetPose(0f, Vector3.Zero, 0f, 0f, 0f);

    public Matrix4x4 BodyPose { get; private set; }
    public Matrix4x4 HeadPose { get; private set; }
    public Matrix4x4 HeadInverse { get; private set; }
    public float BodyYaw { get; private set; }
    public Vector3 Offset { get; private set; }
    public float HeadPitch { get; private set; }
    public float HeadYaw { get; private set; }
    public float LegSwing { get; private set; }
    public long Version { get; private set; }

    public Vector3 CapsuleA(int k) => _wa[k];
    public Vector3 CapsuleB(int k) => _wb[k];
    public static float CapsuleRadius(int k) => Radii[k];
    public static bool IsHairCapsule(int k) => HairCap[k];

    /// <summary>포즈 설정(라디안 · m) → 머리 행렬/역행렬 + 월드 캡슐 갱신.</summary>
    public void SetPose(float bodyYaw, Vector3 offset, float headPitch, float headYaw, float legSwing)
    {
        BodyYaw = bodyYaw; Offset = offset; HeadPitch = headPitch; HeadYaw = headYaw; LegSwing = legSwing;
        var body = Matrix4x4.CreateRotationY(bodyYaw) * Matrix4x4.CreateTranslation(offset);
        BodyPose = body;
        var head = Matrix4x4.CreateTranslation(-NeckPivot) * Matrix4x4.CreateRotationX(headPitch) * Matrix4x4.CreateRotationY(headYaw) * Matrix4x4.CreateTranslation(NeckPivot) * body;
        HeadPose = head;
        Matrix4x4.Invert(head, out var inv);
        HeadInverse = inv;
        var hip = new Vector3(0f, -0.85f, 0f);
        for (var k = 0; k < CapsuleCount; k++)
        {
            Vector3 a = LocalA[k], b = LocalB[k];
            if (Leg[k] != 0)
            {
                var r = Matrix4x4.CreateRotationX(Leg[k] * legSwing);
                var pivot = new Vector3(a.X, hip.Y, 0f);
                a = Vector3.Transform(a - pivot, r) + pivot; b = Vector3.Transform(b - pivot, r) + pivot;
            }
            _wa[k] = Vector3.Transform(a, body); _wb[k] = Vector3.Transform(b, body);
        }
        _hn = 0;
        for (var k = 0; k < CapsuleCount; k++)
        {
            if (!HairCap[k]) continue;
            var d = _wb[k] - _wa[k];
            _hax[_hn] = _wa[k].X; _hay[_hn] = _wa[k].Y; _haz[_hn] = _wa[k].Z;
            _hdx[_hn] = d.X; _hdy[_hn] = d.Y; _hdz[_hn] = d.Z; _hinv[_hn] = 1f / MathF.Max(d.LengthSquared(), 1e-12f); _hr[_hn] = Radii[k];
            _hn++;
        }
        Version++;
    }

    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; var t = Vector3.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f);
        return a + ab * Math.Clamp(t, 0f, 1f);
    }

    /// <summary>머리 타원체 밀어내기(머리 좌표 정규화 반경 · 근사 최근접).</summary>
    public bool ResolveHead(ref Vector3 p, float margin, out Vector3 normal)
    {
        normal = default;
        var hp = HeadPose; var c = new Vector3(hp.M41, hp.M42, hp.M43);
        var rm = HeadRadii.Y + margin;
        if (Vector3.DistanceSquared(p, c) >= rm * rm) return false; // 외접구 조기 기각
        var q = Vector3.Transform(p, HeadInverse);
        var r = HeadRadii + new Vector3(margin);
        var s = q / r; var l2 = s.LengthSquared();
        if (l2 >= 1f) return false;
        var l = MathF.Sqrt(l2);
        s = l > 1e-6f ? s / l : Vector3.UnitY;
        var qn = s * r;
        var n = Vector3.Normalize(qn / (r * r));
        p = Vector3.Transform(qn, HeadPose);
        normal = Vector3.TransformNormal(n, HeadPose);
        return true;
    }

    /// <summary>머리 + 캡슐 밀어내기. hairOnly = 헤어 관련 캡슐만 · belowY 위 점은 캡슐 생략.</summary>
    public bool Resolve(ref Vector3 p, float margin, bool hairOnly, out Vector3 normal)
    {
        var hit = ResolveHead(ref p, margin, out normal);
        if (hairOnly && p.Y > Offset.Y) return hit;
        for (var k = 0; k < CapsuleCount; k++)
        {
            if (hairOnly && !HairCap[k]) continue;
            var rr = Radii[k] + margin;
            Vector3 a = _wa[k], b = _wb[k];
            // AABB 조기 기각
            if (p.Y > MathF.Max(a.Y, b.Y) + rr || p.Y < MathF.Min(a.Y, b.Y) - rr) continue;
            var c = Closest(p, a, b); var d = p - c; var d2 = d.LengthSquared();
            if (d2 >= rr * rr) continue;
            var len = MathF.Sqrt(d2);
            var n = len > 1e-7f ? d / len : Vector3.Normalize(new Vector3(p.X - Offset.X + 1e-4f, 0f, p.Z - Offset.Z));
            p = c + n * rr; normal = n; hit = true;
            // 머리-목 겹침 영역: 캡슐 밀어내기 후 머리 재검사(교대 1회)
            if (ResolveHead(ref p, margin, out var hn)) normal = hn;
        }
        return hit;
    }

    /// <summary>헤어 핫패스 조기 기각: 머리 타원체 외부 + 캡슐 검사 높이 위 → 충돌 없음(행렬 1회 · 호출 없음).</summary>
    public bool MayCollideHair(float x, float y, float z, float margin)
    {
        if (y <= Offset.Y)
        {
            // 헤어 캡슐 정밀 거리(인라인 · 캐시된 선분)
            for (var k = 0; k < _hn; k++)
            {
                float px = x - _hax[k], py = y - _hay[k], pz = z - _haz[k];
                var t = Math.Clamp((px * _hdx[k] + py * _hdy[k] + pz * _hdz[k]) * _hinv[k], 0f, 1f);
                px -= _hdx[k] * t; py -= _hdy[k] * t; pz -= _hdz[k] * t;
                var rr = _hr[k] + margin;
                if (px * px + py * py + pz * pz < rr * rr) return true;
            }
        }
        var m = HeadInverse;
        float qx = x * m.M11 + y * m.M21 + z * m.M31 + m.M41, qy = x * m.M12 + y * m.M22 + z * m.M32 + m.M42, qz = x * m.M13 + y * m.M23 + z * m.M33 + m.M43;
        float rx = HeadRadii.X + margin, ry = HeadRadii.Y + margin, rz = HeadRadii.Z + margin;
        qx /= rx; qy /= ry; qz /= rz;
        return qx * qx + qy * qy + qz * qz < 1f;
    }

    /// <summary>최대 관통 깊이(m · 0 = 외부) — 진단/테스트.</summary>
    public float Penetration(Vector3 p, bool hairOnly)
    {
        var q = Vector3.Transform(p, HeadInverse) / HeadRadii; var l = q.Length();
        var pen = l < 1f ? (1f - l) * HeadRadii.X : 0f;
        for (var k = 0; k < CapsuleCount; k++)
        {
            if (hairOnly && !HairCap[k]) continue;
            var d = Vector3.Distance(p, Closest(p, _wa[k], _wb[k]));
            if (d < Radii[k]) pen = MathF.Max(pen, Radii[k] - d);
        }
        return pen;
    }
}

/// <summary>헤어 · 의상 공용 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
public static class StrandSimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>p = x + v·dt.</summary>
    public static void Predict(ReadOnlySpan<float> x, ReadOnlySpan<float> v, Span<float> p, float dt)
    {
        var n = p.Length; var m = Body(n);
        if (m > 0)
        {
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..m]); var vv = MemoryMarshal.Cast<float, Vector<float>>(v[..m]); var vp = MemoryMarshal.Cast<float, Vector<float>>(p[..m]);
            var d = new Vector<float>(dt);
            for (var k = 0; k < vp.Length; k++) vp[k] = vx[k] + vv[k] * d;
        }
        for (var i = m; i < n; i++) p[i] = x[i] + v[i] * dt;
    }

    public static void PredictScalar(ReadOnlySpan<float> x, ReadOnlySpan<float> v, Span<float> p, float dt)
    {
        for (var i = 0; i < p.Length; i++) p[i] = x[i] + v[i] * dt;
    }

    /// <summary>p += k·(t − p) (노드별 강성).</summary>
    public static void Relax(Span<float> p, ReadOnlySpan<float> t, ReadOnlySpan<float> k)
    {
        var n = p.Length; var m = Body(n);
        if (m > 0)
        {
            var vp = MemoryMarshal.Cast<float, Vector<float>>(p[..m]); var vt = MemoryMarshal.Cast<float, Vector<float>>(t[..m]); var vk = MemoryMarshal.Cast<float, Vector<float>>(k[..m]);
            for (var j = 0; j < vp.Length; j++) vp[j] = vp[j] + vk[j] * (vt[j] - vp[j]);
        }
        for (var i = m; i < n; i++) p[i] = p[i] + k[i] * (t[i] - p[i]);
    }

    public static void RelaxScalar(Span<float> p, ReadOnlySpan<float> t, ReadOnlySpan<float> k)
    {
        for (var i = 0; i < p.Length; i++) p[i] = p[i] + k[i] * (t[i] - p[i]);
    }

    /// <summary>v = (p − x)·invDt.</summary>
    public static void VelocityFrom(Span<float> v, ReadOnlySpan<float> p, ReadOnlySpan<float> x, float invDt)
    {
        var n = v.Length; var m = Body(n);
        if (m > 0)
        {
            var vv = MemoryMarshal.Cast<float, Vector<float>>(v[..m]); var vp = MemoryMarshal.Cast<float, Vector<float>>(p[..m]); var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..m]);
            var s = new Vector<float>(invDt);
            for (var k = 0; k < vv.Length; k++) vv[k] = (vp[k] - vx[k]) * s;
        }
        for (var i = m; i < n; i++) v[i] = (p[i] - x[i]) * invDt;
    }

    public static void VelocityFromScalar(Span<float> v, ReadOnlySpan<float> p, ReadOnlySpan<float> x, float invDt)
    {
        for (var i = 0; i < v.Length; i++) v[i] = (p[i] - x[i]) * invDt;
    }

    /// <summary>dst += src·s.</summary>
    public static void AddScaled(Span<float> dst, ReadOnlySpan<float> src, float s)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var vd = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var vs = MemoryMarshal.Cast<float, Vector<float>>(src[..m]);
            var c = new Vector<float>(s);
            for (var k = 0; k < vd.Length; k++) vd[k] = vd[k] + vs[k] * c;
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] + src[i] * s;
    }

    public static void AddScaledScalar(Span<float> dst, ReadOnlySpan<float> src, float s)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] + src[i] * s;
    }

    /// <summary>dst = dst·s + c (감쇠 + 상수 가속).</summary>
    public static void ScaleAdd(Span<float> dst, float s, float c)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var vd = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            Vector<float> vs = new(s), vc = new(c);
            for (var k = 0; k < vd.Length; k++) vd[k] = vd[k] * vs + vc;
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] * s + c;
    }

    public static void ScaleAddScalar(Span<float> dst, float s, float c)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] * s + c;
    }

    /// <summary>o = rx·a + ry·b + rz·c + d (아핀 변환 1행).</summary>
    public static void Affine(ReadOnlySpan<float> rx, ReadOnlySpan<float> ry, ReadOnlySpan<float> rz, float a, float b, float c, float d, Span<float> o)
    {
        var n = o.Length; var m = Body(n);
        if (m > 0)
        {
            var vx = MemoryMarshal.Cast<float, Vector<float>>(rx[..m]); var vy = MemoryMarshal.Cast<float, Vector<float>>(ry[..m]); var vz = MemoryMarshal.Cast<float, Vector<float>>(rz[..m]);
            var vo = MemoryMarshal.Cast<float, Vector<float>>(o[..m]);
            Vector<float> va = new(a), vb = new(b), vc = new(c), vd = new(d);
            for (var k = 0; k < vo.Length; k++) vo[k] = vx[k] * va + vy[k] * vb + vz[k] * vc + vd;
        }
        for (var i = m; i < n; i++) o[i] = rx[i] * a + ry[i] * b + rz[i] * c + d;
    }

    public static void AffineScalar(ReadOnlySpan<float> rx, ReadOnlySpan<float> ry, ReadOnlySpan<float> rz, float a, float b, float c, float d, Span<float> o)
    {
        for (var i = 0; i < o.Length; i++) o[i] = rx[i] * a + ry[i] * b + rz[i] * c + d;
    }
}
