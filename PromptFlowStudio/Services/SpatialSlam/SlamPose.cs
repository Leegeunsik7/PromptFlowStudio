using System.Numerics;

namespace PromptFlowStudio.Services.SpatialSlam;

/// <summary>강체 변환(카메라→월드: p_w = Q·p_c + T). 카메라 규약 = OpenGL(x 우 · y 상 · −z 전방).</summary>
public readonly record struct SlamPose(Quaternion Q, Vector3 T)
{
    public static readonly SlamPose Identity = new(Quaternion.Identity, Vector3.Zero);

    public Vector3 Apply(Vector3 p) => Vector3.Transform(p, Q) + T;
    public Vector3 Rotate(Vector3 v) => Vector3.Transform(v, Q);

    public SlamPose Inverse()
    {
        var qi = Quaternion.Conjugate(Q);
        return new SlamPose(qi, -Vector3.Transform(T, qi));
    }

    /// <summary>a ∘ b (b 먼저 적용).</summary>
    public static SlamPose operator *(SlamPose a, SlamPose b) => new(Quaternion.Normalize(a.Q * b.Q), a.Apply(b.T));

    public static SlamPose LookAt(Vector3 eye, Vector3 target)
    {
        var f = Vector3.Normalize(target - eye);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var u = Vector3.Cross(r, f);
        var m = new Matrix4x4(r.X, r.Y, r.Z, 0, u.X, u.Y, u.Z, 0, -f.X, -f.Y, -f.Z, 0, 0, 0, 0, 1);
        return new SlamPose(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m)), eye);
    }

    /// <summary>항등 → this 보간(회전 slerp · 이동 선형).</summary>
    public SlamPose Fraction(float f) => new(Quaternion.Slerp(Quaternion.Identity, Q, f), T * f);

    public float AngleDeg() => 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Q.W))) * (180f / MathF.PI);

    public static float AngleBetweenDeg(SlamPose a, SlamPose b) => (a.Inverse() * b).AngleDeg();
}

/// <summary>
/// 합성 실내 장면(시뮬레이션 센서 대역): 방 내부 상자 + 가구 상자 · 해석적 광선 교차 · 부호 거리.
/// 깊이 카메라(z-깊이 · 결정적 가우시안 잡음) · 회전 라이다(빔 × 방위각 · 센서 프레임 점) 생성.
/// </summary>
public static class SlamSyntheticRoom
{
    public static readonly Vector3 RoomMin = new(-3f, 0f, -2.5f), RoomMax = new(3f, 3f, 2.5f);
    public const float TableTop = 0.78f;

    /// <summary>가구 상자(min, max) — 테이블 상판/받침 · 수납장 · 선반 · 기둥 · 소파 · 벽 액자/TV · 책장 · 서랍장 · 협탁 · 벽 패널.</summary>
    public static readonly (Vector3 Min, Vector3 Max)[] Boxes =
    {
        (new(0.6f, 0.72f, -0.5f), new(1.8f, TableTop, 0.4f)),      // 테이블 상판
        (new(1.1f, 0f, -0.15f), new(1.3f, 0.72f, 0.05f)),          // 테이블 받침
        (new(-2.95f, 0f, 1.4f), new(-2.3f, 1.8f, 2.45f)),          // 수납장
        (new(2.6f, 1.1f, -2.0f), new(2.95f, 1.2f, -0.6f)),         // 벽 선반
        (new(-0.55f, 0f, -2.45f), new(-0.15f, 3f, -2.05f)),        // 기둥
        (new(-2.2f, 0f, -2.45f), new(-1.0f, 0.45f, -1.6f)),        // 소파 좌석
        (new(1.2f, 1.3f, 2.38f), new(2.2f, 2.0f, 2.5f)),           // 벽 액자
        (new(-2.95f, 0.9f, -1.2f), new(-2.75f, 1.6f, 0.2f)),       // 벽 TV
        (new(-1.0f, 0f, 2.1f), new(-0.2f, 1.4f, 2.45f)),          // 책장
        (new(0.6f, 0f, -2.45f), new(1.6f, 0.9f, -2.0f)),          // 서랍장
        (new(2.6f, 0f, 0.8f), new(2.95f, 1.0f, 1.6f)),            // 협탁
        (new(0.3f, 1.6f, 2.4f), new(0.8f, 2.4f, 2.5f)),           // 벽 시계/패널
    };

    /// <summary>광선 최근접 교차 거리(없음 = MaxValue).</summary>
    public static float Raycast(Vector3 o, Vector3 d)
    {
        var inv = new Vector3(1f / d.X, 1f / d.Y, 1f / d.Z);
        // 방 내부: 출구 거리
        var t0 = (RoomMin - o) * inv; var t1 = (RoomMax - o) * inv;
        var tmax = Vector3.Max(t0, t1);
        var best = MathF.Min(tmax.X, MathF.Min(tmax.Y, tmax.Z));
        foreach (var (mn, mx) in Boxes)
        {
            var a = (mn - o) * inv; var b = (mx - o) * inv;
            var lo = Vector3.Min(a, b); var hi = Vector3.Max(a, b);
            var tn = MathF.Max(lo.X, MathF.Max(lo.Y, lo.Z)); var tf = MathF.Min(hi.X, MathF.Min(hi.Y, hi.Z));
            if (tn <= tf && tn > 1e-4f && tn < best) best = tn;
        }
        return best;
    }

    /// <summary>부호 거리(자유 공간 = +).</summary>
    public static float Distance(Vector3 p)
    {
        var d = MathF.Min(MathF.Min(p.X - RoomMin.X, RoomMax.X - p.X), MathF.Min(MathF.Min(p.Y - RoomMin.Y, RoomMax.Y - p.Y), MathF.Min(p.Z - RoomMin.Z, RoomMax.Z - p.Z)));
        foreach (var (mn, mx) in Boxes)
        {
            var c = (mn + mx) * 0.5f; var e = (mx - mn) * 0.5f;
            var q = Vector3.Abs(p - c) - e;
            var bd = Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
            d = MathF.Min(d, bd);
        }
        return d;
    }

    /// <summary>깊이 카메라 렌더(z-깊이 m · 0 = 무효) — 잡음 σ = noise·z² (구조광/ToF 근사).</summary>
    public static void RenderDepth(SlamPose camToWorld, int w, int h, float fx, float fy, float cx, float cy, float maxDepth, float noise, ulong seed, Span<float> depth)
    {
        var s = seed | 1;
        for (var v = 0; v < h; v++)
            for (var u = 0; u < w; u++)
            {
                var dc = new Vector3((u + 0.5f - cx) / fx, -(v + 0.5f - cy) / fy, -1f);
                var dw = camToWorld.Rotate(Vector3.Normalize(dc));
                var t = Raycast(camToWorld.T, dw);
                var z = t * Vector3.Normalize(dc).Z * -1f;
                if (t >= float.MaxValue / 2 || z > maxDepth || z < 0.1f) { depth[v * w + u] = 0; continue; }
                if (noise > 0) z += noise * z * z * Gauss(ref s);
                depth[v * w + u] = z;
            }
    }

    /// <summary>회전 라이다 스윕(센서 프레임 xyz SoA) → 점 수. beams = 수직 빔 · ±15° · az = 방위 표본.</summary>
    public static int LidarSweep(SlamPose sensorToWorld, int beams, int az, float maxRange, float noise, ulong seed, Span<float> x, Span<float> y, Span<float> z)
    {
        var s = seed | 1; var n = 0;
        for (var b = 0; b < beams; b++)
        {
            var el = (-15f + 30f * b / Math.Max(1, beams - 1)) * MathF.PI / 180f;
            for (var a = 0; a < az && n < x.Length; a++)
            {
                var th = 2f * MathF.PI * a / az;
                var dl = new Vector3(MathF.Cos(el) * MathF.Sin(th), MathF.Sin(el), -MathF.Cos(el) * MathF.Cos(th));
                var t = Raycast(sensorToWorld.T, sensorToWorld.Rotate(dl));
                if (t > maxRange || t < 0.1f) continue;
                if (noise > 0) t += noise * Gauss(ref s);
                x[n] = dl.X * t; y[n] = dl.Y * t; z[n] = dl.Z * t; n++;
            }
        }
        return n;
    }

    private static float Gauss(ref ulong s) => MathF.Sqrt(-2f * MathF.Log(Uniform(ref s))) * MathF.Cos(2f * MathF.PI * Uniform(ref s));

    private static float Uniform(ref ulong s) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return ((s >> 40) + 0.5f) / (1 << 24); }

    /// <summary>루프 궤적(원 반지름 r · 중심 c · 접선 쪽 사선 시선 · 높이 흔들림) — t ∈ [0,1) 1바퀴.</summary>
    public static SlamPose Trajectory(double t, Vector3 c, float r)
    {
        var a = (float)(t * 2 * Math.PI);
        var eye = c + new Vector3(r * MathF.Cos(a), 0.08f * MathF.Sin(a * 2), r * MathF.Sin(a));
        var look = eye + new Vector3(MathF.Cos(a + 1.0f), -0.25f, MathF.Sin(a + 1.0f));
        return SlamPose.LookAt(eye, look);
    }
}
