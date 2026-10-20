using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.CinematicMastering;

/// <summary>트래커 6DoF 샘플(타임스탬프 = <see cref="TimeSpan"/> 틱 · 100 ns).</summary>
public readonly record struct TrackerSample(long Ticks, Vector3 Position, Quaternion Orientation);

/// <summary>LED 월 평면: 좌하단 원점 · 우/상 단위축 · 크기(m). 법선 = Right × Up(무대 쪽).</summary>
public readonly record struct LedWallPlane(Vector3 Origin, Vector3 Right, Vector3 Up, float WidthM, float HeightM)
{
    public Vector3 Normal => Vector3.Cross(Right, Up);
    public Vector3 Center => Origin + Right * (WidthM * 0.5f) + Up * (HeightM * 0.5f);
    public Vector3 At(float u, float v) => Origin + Right * (u * WidthM) + Up * (v * HeightM);
}

/// <summary>
/// [PromptFlow Studio v16.80.0] 🎥 실시간 ICVFX LED 월 카메라 트래킹 &amp; 시점 동기화 엔진(무할당 · 서브 ms).
/// 트래커 6DoF 링(64) → α-β 필터(위치/속도) + 쿼터니언 슬러프(각속도) → 파이프라인 지연 보상 예측 포즈 →
/// Kooima 일반화 원근(오프-액시스) 투영 · 인너 프러스텀 월 UV 사각형(오버스캔) · 깊이 레이어별 시차(Parallax) 시프트 · 3DGS 스플랫 월 투영.
/// </summary>
public sealed class RealtimeIcvfxLedWallCameraTrackingEngine
{
    public const int History = 64, MaxLayers = 8;

    private readonly TrackerSample[] _ring = new TrackerSample[History];
    private readonly float[] _layerDepth = new float[MaxLayers];
    private readonly Vector2[] _layerShift = new Vector2[MaxLayers];
    private readonly Vector2[] _inner = new Vector2[4];
    private int _head, _count, _layers;
    private long _lastTicks;
    private Vector3 _pos, _vel, _angVel;
    private Quaternion _rot = Quaternion.Identity;
    private bool _primed;

    public LedWallPlane Wall { get; private set; } = new(new Vector3(-6f, 0f, 0f), Vector3.UnitX, Vector3.UnitY, 12f, 5f);
    /// <summary>시차 기준(공칭) 시점 — 아우터 프러스텀 렌더 시점.</summary>
    public Vector3 NominalEye { get; set; } = new(0f, 1.6f, 5f);
    public float Alpha { get; set; } = 0.55f;
    public float Beta { get; set; } = 0.12f;
    public float RotAlpha { get; set; } = 0.6f;
    /// <summary>트래커 → LED 출력 파이프라인 지연(예측 리드 타임).</summary>
    public double LatencyCompensationSec { get; set; } = 0.035;
    public bool Prediction { get; set; } = true;
    public float SensorWidthMm { get; set; } = 36f;
    public float FocalMm { get; set; } = 35f;
    public float Aspect { get; set; } = 16f / 9f;
    public float InnerOverscan { get; set; } = 1.15f;
    public float Near { get; set; } = 0.05f;
    public float Far { get; set; } = 500f;

    // ── 해 ──
    public Vector3 EyePosition { get; private set; }
    public Quaternion EyeOrientation { get; private set; } = Quaternion.Identity;
    public Matrix4x4 View { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;
    public float FrustumLeft { get; private set; }
    public float FrustumRight { get; private set; }
    public float FrustumBottom { get; private set; }
    public float FrustumTop { get; private set; }
    public bool InnerValid { get; private set; }
    /// <summary>인너 프러스텀 월 점유율(바운딩 박스 ∩ 월 · 0..1).</summary>
    public float InnerCoverage { get; private set; }
    public ReadOnlySpan<Vector2> InnerQuad => _inner;
    public int LayerCount => _layers;
    public float LayerDepth(int i) => _layerDepth[i];
    public Vector2 LayerShift(int i) => _layerShift[i];

    // ── 계측 ──
    public long Samples { get; private set; }
    public long Solves { get; private set; }
    public double LastSolveMicros { get; private set; }
    public double MaxSolveMicros { get; private set; }
    /// <summary>예측 오차(새 샘플 vs 직전 상태 외삽 · EMA · mm).</summary>
    public float PredictionErrorMm { get; private set; }
    public float JitterMm { get; private set; }
    public string? LastAction { get; private set; }

    public void Configure(LedWallPlane wall)
    {
        Wall = wall;
        LastAction = "🧱 LED 월 " + wall.WidthM.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×" + wall.HeightM.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m";
    }

    public void Reset()
    {
        _head = 0; _count = 0; _primed = false; _vel = _angVel = Vector3.Zero; _rot = Quaternion.Identity;
        Samples = Solves = 0; LastSolveMicros = MaxSolveMicros = 0; PredictionErrorMm = JitterMm = 0;
    }

    /// <summary>깊이 레이어(월 뒤 거리 m · ∞ = <see cref="float.PositiveInfinity"/>) 등록.</summary>
    public void SetLayers(ReadOnlySpan<float> depths)
    {
        _layers = Math.Min(depths.Length, MaxLayers);
        depths[.._layers].CopyTo(_layerDepth);
    }

    public TrackerSample Sample(int back) => _ring[(_head - 1 - back + History * 2) % History];
    public int SampleCount => _count;

    /// <summary>핫패스: 트래커 샘플 수신(무할당) — α-β 위치 필터 · 쿼터니언 보정 · 각속도 추정.</summary>
    public void Push(in TrackerSample s)
    {
        _ring[_head] = s; _head = (_head + 1) % History; if (_count < History) _count++;
        Samples++;
        if (!_primed) { _pos = s.Position; _rot = Quaternion.Normalize(s.Orientation); _lastTicks = s.Ticks; _primed = true; return; }
        var dt = (float)((s.Ticks - _lastTicks) / (double)TimeSpan.TicksPerSecond);
        if (dt <= 1e-6f) return;
        _lastTicks = s.Ticks;

        var pred = _pos + _vel * dt;
        var r = s.Position - pred;
        var errMm = r.Length() * 1000f;
        PredictionErrorMm += (errMm - PredictionErrorMm) * 0.1f;
        _pos = pred + r * Alpha;
        _vel += r * (Beta / dt);
        var res = (s.Position - _pos).Length() * 1000f;
        JitterMm += (res - JitterMm) * 0.1f;

        var meas = Quaternion.Normalize(s.Orientation);
        var predRot = Integrate(_rot, _angVel, dt);
        var nr = Quaternion.Normalize(Quaternion.Slerp(predRot, meas, RotAlpha));
        var w = AngularVelocity(_rot, nr, dt);
        _angVel += (w - _angVel) * 0.35f;
        _rot = nr;
    }

    /// <summary>각속도(rad/s · 월드축) 적분 회전.</summary>
    public static Quaternion Integrate(Quaternion q, Vector3 w, float dt)
    {
        var ang = w.Length() * dt;
        if (ang < 1e-7f) return q;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(w / w.Length(), ang) * q);
    }

    /// <summary>두 자세 사이 각속도(rad/s · 최단 경로).</summary>
    public static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt)
    {
        var d = to * Quaternion.Conjugate(from);
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
        var s = MathF.Sqrt(MathF.Max(0f, 1f - d.W * d.W));
        if (s < 1e-6f) return Vector3.Zero;
        var ang = 2f * MathF.Acos(Math.Clamp(d.W, -1f, 1f));
        return new Vector3(d.X, d.Y, d.Z) / s * (ang / dt);
    }

    /// <summary>핫패스: 렌더 시각 기준 예측 포즈 → 오프-액시스 투영 · 인너 프러스텀 · 레이어 시차(무할당).</summary>
    public void Solve(long renderTicks)
    {
        using var guard = ZeroGcRegionScope.Enter("Icvfx.Track.Solve");
        var t0 = Stopwatch.GetTimestamp();
        var lead = Prediction ? (float)((renderTicks - _lastTicks) / (double)TimeSpan.TicksPerSecond + LatencyCompensationSec) : 0f;
        lead = Math.Clamp(lead, 0f, 0.2f);
        EyePosition = _pos + _vel * lead;
        EyeOrientation = Prediction ? Integrate(_rot, _angVel, lead) : _rot;

        OffAxis(Wall, EyePosition, Near, Far, out var view, out var proj, out var l, out var r, out var b, out var t);
        View = view; Projection = proj; ViewProjection = view * proj;
        FrustumLeft = l; FrustumRight = r; FrustumBottom = b; FrustumTop = t;

        InnerValid = InnerFrustum(Wall, EyePosition, EyeOrientation, SensorWidthMm, FocalMm, Aspect, InnerOverscan, _inner);
        InnerCoverage = InnerValid ? Coverage(_inner) : 0f;

        for (var i = 0; i < _layers; i++) _layerShift[i] = ParallaxShift(Wall, NominalEye, EyePosition, _layerDepth[i]);

        Solves++;
        LastSolveMicros = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
        if (LastSolveMicros > MaxSolveMicros) MaxSolveMicros = LastSolveMicros;
    }

    /// <summary>Kooima 일반화 원근 투영(행벡터 규약 · System.Numerics). 월 4모서리 → NDC ±1.</summary>
    public static void OffAxis(in LedWallPlane wall, Vector3 eye, float near, float far, out Matrix4x4 view, out Matrix4x4 proj, out float l, out float r, out float b, out float t)
    {
        var vr = wall.Right; var vu = wall.Up; var vn = wall.Normal;
        var va = wall.Origin - eye;
        var vb = wall.Origin + vr * wall.WidthM - eye;
        var vc = wall.Origin + vu * wall.HeightM - eye;
        var d = MathF.Max(-Vector3.Dot(va, vn), 1e-4f);
        var k = near / d;
        l = Vector3.Dot(vr, va) * k; r = Vector3.Dot(vr, vb) * k;
        b = Vector3.Dot(vu, va) * k; t = Vector3.Dot(vu, vc) * k;
        proj = Matrix4x4.CreatePerspectiveOffCenter(l, r, b, t, near, far);
        var basis = new Matrix4x4(vr.X, vu.X, vn.X, 0, vr.Y, vu.Y, vn.Y, 0, vr.Z, vu.Z, vn.Z, 0, 0, 0, 0, 1);
        view = Matrix4x4.CreateTranslation(-eye) * basis;
    }

    /// <summary>시점 → 월 평면 교차 UV(뒤/평행이면 false).</summary>
    public static bool RayToWall(in LedWallPlane wall, Vector3 origin, Vector3 dir, out Vector2 uv)
    {
        var n = wall.Normal;
        var den = Vector3.Dot(dir, n);
        uv = default;
        if (den > -1e-6f) return false;
        var tt = Vector3.Dot(wall.Origin - origin, n) / den;
        if (tt <= 0f) return false;
        var hit = origin + dir * tt - wall.Origin;
        uv = new Vector2(Vector3.Dot(hit, wall.Right) / wall.WidthM, Vector3.Dot(hit, wall.Up) / wall.HeightM);
        return true;
    }

    /// <summary>카메라 프러스텀(센서 · 초점 · 오버스캔) 4모서리 광선 → 월 UV 사각형(BL · BR · TR · TL).</summary>
    public static bool InnerFrustum(in LedWallPlane wall, Vector3 eye, Quaternion q, float sensorMm, float focalMm, float aspect, float overscan, Span<Vector2> quad)
    {
        var f = Vector3.Transform(-Vector3.UnitZ, q); var rt = Vector3.Transform(Vector3.UnitX, q); var up = Vector3.Transform(Vector3.UnitY, q);
        var th = sensorMm * 0.5f / focalMm * overscan; var tv = th / aspect;
        var ok = RayToWall(wall, eye, f - rt * th - up * tv, out quad[0]);
        ok &= RayToWall(wall, eye, f + rt * th - up * tv, out quad[1]);
        ok &= RayToWall(wall, eye, f + rt * th + up * tv, out quad[2]);
        ok &= RayToWall(wall, eye, f - rt * th + up * tv, out quad[3]);
        return ok;
    }

    private static float Coverage(ReadOnlySpan<Vector2> q)
    {
        float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
        for (var i = 0; i < 4; i++) { x0 = MathF.Min(x0, q[i].X); x1 = MathF.Max(x1, q[i].X); y0 = MathF.Min(y0, q[i].Y); y1 = MathF.Max(y1, q[i].Y); }
        x0 = Math.Clamp(x0, 0f, 1f); x1 = Math.Clamp(x1, 0f, 1f); y0 = Math.Clamp(y0, 0f, 1f); y1 = Math.Clamp(y1, 0f, 1f);
        return MathF.Max(0f, x1 - x0) * MathF.Max(0f, y1 - y0);
    }

    /// <summary>
    /// 깊이 D(월 뒤) 레이어의 시차 시프트(UV): 공칭 시점에서 월 중심을 지나 D 만큼 들어간 점 P 를 현재 시점에서 재투영한 변위.
    /// D=0 → 0(월 위) · D→∞ → 시점 평행 이동을 그대로 추종.
    /// </summary>
    public static Vector2 ParallaxShift(in LedWallPlane wall, Vector3 nominalEye, Vector3 eye, float depth)
    {
        var n = wall.Normal; var c = wall.Center;
        var h0 = MathF.Max(Vector3.Dot(nominalEye - c, n), 1e-3f);
        Vector3 dir;
        if (float.IsPositiveInfinity(depth)) dir = c - nominalEye; // 무한 원경: 방향만 유지
        else
        {
            var p = c + (c - nominalEye) * (depth / h0);
            dir = p - eye;
        }
        if (!RayToWall(wall, eye, dir, out var uv)) return Vector2.Zero;
        return uv - new Vector2(0.5f, 0.5f);
    }

    /// <summary>핫패스: 3DGS 스플랫 중심(월드) → 현재 시점 월 UV(무할당). 반환 = 유효 투영 수, 무효는 NaN.</summary>
    public int ProjectToWall(ReadOnlySpan<Vector3> points, Span<Vector2> uv)
    {
        var wall = Wall; var eye = EyePosition; var n = wall.Normal;
        var plane = Vector3.Dot(wall.Origin - eye, n);
        float iw = 1f / wall.WidthM, ih = 1f / wall.HeightM;
        var ok = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var d = points[i] - eye;
            var den = Vector3.Dot(d, n);
            if (den > -1e-6f || plane / den <= 0f) { uv[i] = new Vector2(float.NaN, float.NaN); continue; }
            var hit = eye + d * (plane / den) - wall.Origin;
            uv[i] = new Vector2(Vector3.Dot(hit, wall.Right) * iw, Vector3.Dot(hit, wall.Up) * ih);
            ok++;
        }
        return ok;
    }

    /// <summary>점-사각형(볼록 · 반시계/시계 무관) 포함 판정.</summary>
    public static bool InQuad(ReadOnlySpan<Vector2> q, Vector2 p)
    {
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) & 3];
            var c = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var s = c > 0 ? 1 : c < 0 ? -1 : 0;
            if (s == 0) continue;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }
}
