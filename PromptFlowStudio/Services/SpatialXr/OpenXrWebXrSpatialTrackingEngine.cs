using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.SpatialXr;

/// <summary>헤드셋 프로파일 — 탄젠트 FOV는 좌안 기준(OpenXR XrFovf: 좌/우/상/하 · 우안은 수평 미러), 해상도는 스트림 프리뷰용 축소값.</summary>
public readonly record struct XrDeviceProfile(string Name, float IpdMm, float RefreshHz, float TanLeft, float TanRight, float TanUp, float TanDown, bool EyeTracking, bool HandTracking)
{
    public static readonly XrDeviceProfile[] All =
    {
        new("Apple Vision Pro", 63f, 90f, -1.19f, 0.96f, 0.93f, -1.15f, true, true),
        new("Meta Quest 3", 64f, 120f, -1.22f, 0.92f, 0.96f, -1.19f, false, true),
        new("WebXR 표준 헤드셋", 63f, 72f, -1.0f, 1.0f, 1.0f, -1.0f, false, false),
        new("브라우저 WebXR 세션", 63f, 60f, -1.0f, 1.0f, 1.0f, -1.0f, false, false),
    };
}

/// <summary>안구 뷰(좌 = 0 · 우 = 1) — 행 벡터 규약(System.Numerics): clip = p · View · Projection.</summary>
public struct XrEyeView
{
    public Vector3 Position;
    public Quaternion Orientation;
    public Matrix4x4 View, Projection, ViewProjection;
    public float TanL, TanR, TanU, TanD;
}

/// <summary>
/// [PromptFlow Studio v16.97.0] 🥽 실시간 OpenXR/WebXR 양안 6DoF 트래킹 엔진.
/// · 포즈 링(128 · SoA): SubmitPose(µs, 위치, 회전) — 역순/NaN 거부 · 직전 상태로 해당 시각을 예측해 예측 오차(mm/°) EMA 실측 · 선속도/각속도 EMA.
/// · Predict(표시 시각): p + v·dt · q ← exp(ω·dt)·q(월드 각속도) · 최대 예측 100 ms.
/// · UpdateViews: 예측 머리 포즈 → IPD/2 좌우 안구 → 역행렬 뷰 + 비대칭 오프센터 투영(XrFovf) · 서브 ms 실측(무할당).
/// · ProjectPoints: 월드 SoA 점 → 안구 픽셀/깊이 Vector&lt;float&gt; 일괄 투영(스테레오 프리뷰 · 충돌 시각화).
/// </summary>
public sealed class OpenXrWebXrSpatialTrackingEngine
{
    public const int Cap = 128;
    public const double MaxPredictionSec = 0.1;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly long[] _ts = new long[Cap];
    private readonly Vector3[] _p = new Vector3[Cap];
    private readonly Quaternion[] _q = new Quaternion[Cap];
    private readonly XrEyeView[] _eyes = new XrEyeView[2];
    private int _head, _count;
    private Vector3 _vel, _ang;
    private long _samples, _rejected, _updates;
    private XrDeviceProfile _profile = XrDeviceProfile.All[0];

    public OpenXrWebXrSpatialTrackingEngine() { IpdMm = _profile.IpdMm; UpdateViews(0); }

    public XrDeviceProfile Profile { get => _profile; set { _profile = value; IpdMm = value.IpdMm; } }
    public float IpdMm { get; set; }
    public float Near { get; set; } = 0.05f;
    public float Far { get; set; } = 100f;
    public float VelocityAlpha { get; set; } = 0.5f;
    public bool PredictionEnabled { get; set; } = true;

    public long Samples => Interlocked.Read(ref _samples);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long Updates => Interlocked.Read(ref _updates);
    public int Count { get { lock (_gate) return _count; } }
    public double LastUpdateUs { get; private set; }
    public double PollHz { get; private set; }
    public float PredErrorMm { get; private set; }
    public float PredErrorDeg { get; private set; }
    public long DisplayTimeUs { get; private set; }
    public Vector3 HeadPosition { get; private set; }
    public Quaternion HeadOrientation { get; private set; } = Quaternion.Identity;
    public Vector3 LinearVelocity { get { lock (_gate) return _vel; } }
    public Vector3 AngularVelocity { get { lock (_gate) return _ang; } }
    public long LatestTimeUs { get { lock (_gate) return _count == 0 ? 0 : _ts[(_head - 1 + Cap) % Cap]; } }

    public ref readonly XrEyeView Eye(int e) => ref _eyes[e];

    public static long NowUs => (long)(Stopwatch.GetTimestamp() * TickUs);

    public void Reset()
    {
        lock (_gate) { _head = 0; _count = 0; _vel = default; _ang = default; PredErrorMm = 0; PredErrorDeg = 0; PollHz = 0; }
    }

    /// <summary>포즈 표본 수신(무할당) — 역순/중복/NaN = 거부.</summary>
    public bool SubmitPose(long tsUs, Vector3 p, Quaternion q)
    {
        if (!float.IsFinite(p.X + p.Y + p.Z + q.X + q.Y + q.Z + q.W) || q.LengthSquared() < 1e-6f) { Interlocked.Increment(ref _rejected); return false; }
        q = Quaternion.Normalize(q);
        lock (_gate)
        {
            if (_count > 0)
            {
                var li = (_head - 1 + Cap) % Cap;
                var dt = (tsUs - _ts[li]) * 1e-6;
                if (dt <= 0) { Interlocked.Increment(ref _rejected); return false; }
                // 예측 오차(직전 상태 → 이번 시각)
                PredictFrom(li, dt, out var pp, out var pq);
                var a = 0.1f;
                PredErrorMm += ((pp - p).Length() * 1000f - PredErrorMm) * a;
                PredErrorDeg += (AngleDeg(pq, q) - PredErrorDeg) * a;
                // 속도(EMA)
                var vInst = (p - _p[li]) / (float)dt;
                var dq = q * Quaternion.Conjugate(_q[li]);
                if (dq.W < 0) dq = Quaternion.Negate(dq);
                var s = MathF.Sqrt(Math.Max(0f, 1f - dq.W * dq.W));
                var ang = 2f * MathF.Atan2(s, dq.W);
                var wInst = s > 1e-7f ? new Vector3(dq.X, dq.Y, dq.Z) / s * (ang / (float)dt) : Vector3.Zero;
                var va = Math.Clamp(VelocityAlpha, 0.01f, 1f);
                _vel += (vInst - _vel) * va;
                _ang += (wInst - _ang) * va;
                PollHz = PollHz <= 0 ? 1 / dt : PollHz + (1 / dt - PollHz) * 0.1;
            }
            _ts[_head] = tsUs; _p[_head] = p; _q[_head] = q;
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
        }
        Interlocked.Increment(ref _samples);
        return true;
    }

    private void PredictFrom(int i, double dt, out Vector3 p, out Quaternion q)
    {
        if (!PredictionEnabled) { p = _p[i]; q = _q[i]; return; }
        var d = (float)Math.Clamp(dt, 0, MaxPredictionSec);
        p = _p[i] + _vel * d;
        q = Integrate(_q[i], _ang * d);
    }

    /// <summary>월드 회전 벡터 적분: q' = exp(r)·q.</summary>
    public static Quaternion Integrate(Quaternion q, Vector3 rotVec)
    {
        var a = rotVec.Length();
        if (a < 1e-9f) return q;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(rotVec / a, a) * q);
    }

    public static float AngleDeg(Quaternion a, Quaternion b)
    {
        var d = MathF.Abs(Quaternion.Dot(a, b));
        return 2f * MathF.Acos(Math.Min(1f, d)) * (180f / MathF.PI);
    }

    /// <summary>표시 시각 머리 포즈 예측(표본 없음 = 원점).</summary>
    public void Predict(long targetUs, out Vector3 p, out Quaternion q)
    {
        lock (_gate)
        {
            if (_count == 0) { p = Vector3.Zero; q = Quaternion.Identity; return; }
            var li = (_head - 1 + Cap) % Cap;
            PredictFrom(li, (targetUs - _ts[li]) * 1e-6, out p, out q);
        }
    }

    /// <summary>양안 뷰/투영 갱신(무할당 · 실측 µs).</summary>
    public void UpdateViews(long displayUs)
    {
        var t0 = Stopwatch.GetTimestamp();
        Predict(displayUs, out var p, out var q);
        ComputeEyes(p, q, IpdMm, _profile, Near, Far, _eyes);
        HeadPosition = p; HeadOrientation = q; DisplayTimeUs = displayUs;
        Interlocked.Increment(ref _updates);
        LastUpdateUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    /// <summary>머리 포즈 → 좌/우 안구 뷰(우안 FOV 수평 미러).</summary>
    public static void ComputeEyes(Vector3 head, Quaternion q, float ipdMm, in XrDeviceProfile prof, float near, float far, Span<XrEyeView> eyes)
    {
        var right = Vector3.Transform(Vector3.UnitX, q);
        var rot = Matrix4x4.CreateFromQuaternion(q);
        for (var e = 0; e < 2; e++)
        {
            ref var v = ref eyes[e];
            v.Position = head + right * ((e == 0 ? -0.5f : 0.5f) * ipdMm * 0.001f);
            v.Orientation = q;
            if (e == 0) { v.TanL = prof.TanLeft; v.TanR = prof.TanRight; }
            else { v.TanL = -prof.TanRight; v.TanR = -prof.TanLeft; }
            v.TanU = prof.TanUp; v.TanD = prof.TanDown;
            var world = rot; world.Translation = v.Position;
            Matrix4x4.Invert(world, out v.View);
            v.Projection = Matrix4x4.CreatePerspectiveOffCenter(v.TanL * near, v.TanR * near, v.TanD * near, v.TanU * near, near, far);
            v.ViewProjection = v.View * v.Projection;
        }
    }

    /// <summary>
    /// 월드 SoA 점 → 안구 픽셀(u,v) · 깊이(뷰 공간 −z, 근평면 뒤 = −1) SIMD 일괄 투영 → 가시 점 수(무할당).
    /// </summary>
    public static int ProjectPoints(in XrEyeView eye, ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, Span<float> u, Span<float> v, Span<float> depth, int width, int height, float near)
    {
        var m = eye.ViewProjection;
        var n = x.Length; var i = 0; var vis = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x); var vy = MemoryMarshal.Cast<float, Vector<float>>(y); var vz = MemoryMarshal.Cast<float, Vector<float>>(z);
            var vu = MemoryMarshal.Cast<float, Vector<float>>(u); var vv = MemoryMarshal.Cast<float, Vector<float>>(v); var vd = MemoryMarshal.Cast<float, Vector<float>>(depth);
            Vector<float> m11 = new(m.M11), m21 = new(m.M21), m31 = new(m.M31), m41 = new(m.M41);
            Vector<float> m12 = new(m.M12), m22 = new(m.M22), m32 = new(m.M32), m42 = new(m.M42);
            Vector<float> m14 = new(m.M14), m24 = new(m.M24), m34 = new(m.M34), m44 = new(m.M44);
            Vector<float> hw = new(0.5f * width), hh = new(0.5f * height), vn = new(near), neg = new(-1f);
            for (var k = 0; k < vx.Length; k++)
            {
                var cx = vx[k] * m11 + vy[k] * m21 + vz[k] * m31 + m41;
                var cy = vx[k] * m12 + vy[k] * m22 + vz[k] * m32 + m42;
                var w = vx[k] * m14 + vy[k] * m24 + vz[k] * m34 + m44;
                var front = Vector.GreaterThanOrEqual(w, vn);
                var iw = Vector<float>.One / Vector.ConditionalSelect(front, w, Vector<float>.One);
                vu[k] = (cx * iw + Vector<float>.One) * hw;
                vv[k] = (Vector<float>.One - cy * iw) * hh;
                vd[k] = Vector.ConditionalSelect(front, w, neg);
                vis -= Vector.Sum(front);
            }
            i = vx.Length * Vector<float>.Count;
        }
        for (; i < n; i++)
        {
            var cx = x[i] * m.M11 + y[i] * m.M21 + z[i] * m.M31 + m.M41;
            var cy = x[i] * m.M12 + y[i] * m.M22 + z[i] * m.M32 + m.M42;
            var w = x[i] * m.M14 + y[i] * m.M24 + z[i] * m.M34 + m.M44;
            if (w < near) { u[i] = v[i] = 0; depth[i] = -1; continue; }
            u[i] = (cx / w + 1) * 0.5f * width; v[i] = (1 - cy / w) * 0.5f * height; depth[i] = w;
            vis++;
        }
        return vis;
    }

    /// <summary>안구 픽셀 중심 → 월드 광선 방향(정규화).</summary>
    public static Vector3 PixelRay(in XrEyeView eye, float px, float py, int width, int height)
    {
        var tx = eye.TanL + (eye.TanR - eye.TanL) * px / width;
        var ty = eye.TanU - (eye.TanU - eye.TanD) * py / height;
        return Vector3.Normalize(Vector3.Transform(new Vector3(tx, ty, -1f), eye.Orientation));
    }
}
