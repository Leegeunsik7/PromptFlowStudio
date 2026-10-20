using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.Virtual;

namespace PromptFlowStudio.Services.Icvfx;

/// <summary>
/// LED 월 평면 세그먼트(스테이지 좌표 m · Y 상향) — 좌하단 모서리 + 우/상 단위 벡터 + 크기 · 렌더 해상도 · 물리 LED 피치(mm).
/// 법선 = 상 × 우(관객/카메라 방향).
/// </summary>
public sealed class LedWallSegment
{
    public LedWallSegment(string name, Vector3 lowerLeft, Vector3 right, Vector3 up, float width, float height, int resX, int resY, float pitchMm)
    {
        Name = name; LowerLeft = lowerLeft; Right = Vector3.Normalize(right); Up = Vector3.Normalize(up);
        Width = width; Height = height; ResX = Math.Max(2, resX); ResY = Math.Max(2, resY); PitchMm = pitchMm;
        Normal = Vector3.Normalize(Vector3.Cross(Up, Right));
        Linear = new float[ResX * ResY * 3];
        Mask = new float[ResX * ResY];
    }

    public string Name { get; }
    public Vector3 LowerLeft { get; }
    public Vector3 Right { get; }
    public Vector3 Up { get; }
    public Vector3 Normal { get; }
    public float Width { get; }
    public float Height { get; }
    public int ResX { get; }
    public int ResY { get; }
    public float PitchMm { get; }
    /// <summary>선형 장면 광(ACEScg 작업 공간) RGB · 행 0 = 상단.</summary>
    public float[] Linear { get; }
    /// <summary>이너 프러스텀 가중치(0 = 아우터 · 1 = 이너).</summary>
    public float[] Mask { get; }

    public Vector3 LowerRight => LowerLeft + Right * Width;
    public Vector3 UpperLeft => LowerLeft + Up * Height;

    /// <summary>픽셀 중심 월드 좌표(행 0 = 상단).</summary>
    public Vector3 PixelCenter(int x, int y) => LowerLeft + Right * ((x + 0.5f) / ResX * Width) + Up * ((1f - (y + 0.5f) / ResY) * Height);

    /// <summary>광선-세그먼트 교차 → (t, u 0..1, v 0..1 상향). 실패 시 false.</summary>
    public bool Intersect(Vector3 o, Vector3 d, out float t, out float u, out float v)
    {
        t = u = v = 0;
        var den = Vector3.Dot(d, Normal);
        if (MathF.Abs(den) < 1e-7f) return false;
        t = Vector3.Dot(LowerLeft - o, Normal) / den;
        if (t <= 1e-4f) return false;
        var q = o + d * t - LowerLeft;
        u = Vector3.Dot(q, Right) / Width;
        v = Vector3.Dot(q, Up) / Height;
        return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
    }

    /// <summary>선형 버퍼 쌍선형 샘플(u 0..1 좌→우 · v 0..1 하→상).</summary>
    public Vector3 SampleLinear(float u, float v)
    {
        var fx = u * ResX - 0.5f; var fy = (1f - v) * ResY - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, ResX - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, ResY - 1);
        int x1 = Math.Min(x0 + 1, ResX - 1), y1 = Math.Min(y0 + 1, ResY - 1);
        float ax = Math.Clamp(fx - x0, 0f, 1f), ay = Math.Clamp(fy - y0, 0f, 1f);
        var l = Linear;
        Vector3 P(int x, int y) { var o = (y * ResX + x) * 3; return new Vector3(l[o], l[o + 1], l[o + 2]); }
        return Vector3.Lerp(Vector3.Lerp(P(x0, y0), P(x1, y0), ax), Vector3.Lerp(P(x0, y1), P(x1, y1), ax), ay);
    }

    /// <summary>
    /// 일반화 원근(Kooima) 오프액시스 뷰-투영 행렬(System.Numerics 행벡터 규약 · OpenGL NDC) — 외부 GPU/nDisplay 결선용.
    /// 세그먼트 위 점은 눈 위치와 무관하게 NDC x = 2u−1 · y = 2v−1 로 사상된다.
    /// </summary>
    public Matrix4x4 OffAxisViewProjection(Vector3 eye, float near, float far)
    {
        var vr = Right; var vu = Up; var vn = Normal;
        var va = LowerLeft - eye; var vb = LowerRight - eye; var vc = UpperLeft - eye;
        var d = -Vector3.Dot(va, vn);
        var l = Vector3.Dot(vr, va) * near / d; var r = Vector3.Dot(vr, vb) * near / d;
        var b = Vector3.Dot(vu, va) * near / d; var t = Vector3.Dot(vu, vc) * near / d;
        var basis = new Matrix4x4(vr.X, vu.X, vn.X, 0, vr.Y, vu.Y, vn.Y, 0, vr.Z, vu.Z, vn.Z, 0, -Vector3.Dot(eye, vr), -Vector3.Dot(eye, vu), -Vector3.Dot(eye, vn), 1);
        var proj = new Matrix4x4(
            2 * near / (r - l), 0, 0, 0,
            0, 2 * near / (t - b), 0, 0,
            (r + l) / (r - l), (t + b) / (t - b), -(far + near) / (far - near), -1,
            0, 0, -2 * far * near / (far - near), 0);
        return basis * proj;
    }
}

/// <summary>LED 볼륨 — 세그먼트 집합(곡면 월은 평면 분할 근사 · 천장 선택).</summary>
public sealed class LedVolume
{
    private readonly List<LedWallSegment> _segments = new();
    public IReadOnlyList<LedWallSegment> Segments => _segments;
    public int Count => _segments.Count;
    public LedWallSegment this[int i] => _segments[i];

    public void Add(LedWallSegment s) => _segments.Add(s);

    /// <summary>
    /// 곡면 월(원호) — 반경 radius(m) · 원호 각 arcDeg · 세그먼트 n · 높이 height · 중심 (0, 0, 0) 기준 +Z 방향 정면.
    /// 세그먼트당 해상도는 resPerMeter(렌더 해상도) · pitchMm 은 물리 피치(무아레 분석용).
    /// </summary>
    public static LedVolume Curved(float radius = 6f, float arcDeg = 120f, int segments = 6, float height = 4.5f, float floorY = 0f, float resPerMeter = 24f, float pitchMm = 2.6f, bool ceiling = false)
    {
        var v = new LedVolume();
        var half = arcDeg * MathF.PI / 360f;
        for (var i = 0; i < segments; i++)
        {
            float a0 = -half + 2 * half * i / segments, a1 = -half + 2 * half * (i + 1) / segments;
            var p0 = new Vector3(MathF.Sin(a0) * radius, floorY, MathF.Cos(a0) * radius);
            var p1 = new Vector3(MathF.Sin(a1) * radius, floorY, MathF.Cos(a1) * radius);
            var w = Vector3.Distance(p0, p1);
            // 카메라(원점 쪽)에서 보았을 때 좌→우 = −X → +X 이므로 p0 → p1
            v.Add(new LedWallSegment("WALL-" + (i + 1).ToString(CultureInfo.InvariantCulture), p0, p1 - p0, Vector3.UnitY, w, height, Math.Max(8, (int)(w * resPerMeter)), Math.Max(8, (int)(height * resPerMeter)), pitchMm));
        }
        if (ceiling)
        {
            var cw = radius * 1.4f;
            v.Add(new LedWallSegment("CEILING", new Vector3(-cw / 2, floorY + height, radius * 0.95f), Vector3.UnitX, -Vector3.UnitZ, cw, radius * 0.9f,
                (int)(cw * resPerMeter * 0.5f), (int)(radius * 0.9f * resPerMeter * 0.5f), pitchMm * 1.5f));
        }
        return v;
    }

    /// <summary>평면 월(정면 z = distance).</summary>
    public static LedVolume Flat(float width = 8f, float height = 4.5f, float distance = 5f, float resPerMeter = 24f, float pitchMm = 2.6f)
    {
        var v = new LedVolume();
        v.Add(new LedWallSegment("WALL", new Vector3(-width / 2, 0, distance), Vector3.UnitX, Vector3.UnitY, width, height, (int)(width * resPerMeter), (int)(height * resPerMeter), pitchMm));
        return v;
    }

    /// <summary>최근접 교차 세그먼트.</summary>
    public bool Raycast(Vector3 o, Vector3 d, out int seg, out float u, out float v, out float t)
    {
        seg = -1; u = v = 0; t = float.MaxValue;
        for (var i = 0; i < _segments.Count; i++)
            if (_segments[i].Intersect(o, d, out var ti, out var ui, out var vi) && ti < t) { t = ti; seg = i; u = ui; v = vi; }
        return seg >= 0;
    }
}

/// <summary>가상 세트 — 원점/방향 광선 → 선형 장면 광(ACEScg). 구현은 스레드 안전 읽기 전용이어야 한다.</summary>
public interface IVirtualSet
{
    Vector3 Sample(Vector3 origin, Vector3 dir);
}

/// <summary>
/// 절차 가상 세트(해석 레이캐스트 · 무할당) — 하늘 그라데이션 + 태양 원반 · 원경 산맥(방위 함수) · 체커 지면 + 구 그림자 · 기둥/구 · 거리 안개.
/// VFX 파라미터: 태양 방위/고도 · 안개 · 번개 플래시 · 시간대(색온도).
/// </summary>
public sealed class ProceduralVirtualSet : IVirtualSet
{
    private readonly Vector4[] _spheres =
    {
        new(-6f, 1.6f, 18f, 1.6f), new(5f, 2.4f, 26f, 2.4f), new(0.5f, 1.0f, 12f, 1.0f),
    };
    private readonly Vector3[] _sphereColor = { new(0.75f, 0.25f, 0.18f), new(0.2f, 0.35f, 0.8f), new(0.85f, 0.8f, 0.7f) };
    private readonly Vector4[] _pillars = { new(-12f, 30f, 0.8f, 9f), new(12f, 34f, 0.8f, 11f), new(-3f, 45f, 1.2f, 14f), new(7f, 22f, 0.6f, 6f) };   // x, z, r, h

    public float SunAzimuthDeg { get; set; } = 25f;
    public float SunElevationDeg { get; set; } = 28f;
    public float SunIntensity { get; set; } = 6f;
    public float SkyIntensity { get; set; } = 1f;
    public float FogDensity { get; set; } = 0.012f;
    public float Flash { get; set; }
    public Vector3 SunColor { get; set; } = new(1f, 0.92f, 0.8f);

    public Vector3 SunDirection
    {
        get
        {
            float az = SunAzimuthDeg * MathF.PI / 180f, el = SunElevationDeg * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el)));
        }
    }

    private Vector3 Sky(Vector3 d, Vector3 sun)
    {
        var up = MathF.Max(0f, d.Y);
        var day = Math.Clamp(SunElevationDeg / 30f + 0.3f, 0.05f, 1f);
        var zenith = new Vector3(0.18f, 0.32f, 0.75f) * day;
        var horizon = Vector3.Lerp(new Vector3(0.9f, 0.55f, 0.35f), new Vector3(0.7f, 0.8f, 0.95f), day) * MathF.Max(0.15f, day);
        var c = Vector3.Lerp(horizon, zenith, MathF.Pow(up, 0.5f)) * SkyIntensity;
        var sd = Vector3.Dot(d, sun);
        if (sd > 0.9995f) c += SunColor * SunIntensity * 8f;
        else if (sd > 0.95f) c += SunColor * SunIntensity * 0.06f * (sd - 0.95f) / 0.05f;
        return c * (1f + Flash * 3f);
    }

    private static float Mountain(float azimuth) => 0.035f + 0.03f * MathF.Sin(azimuth * 3.1f + 1.3f) + 0.02f * MathF.Sin(azimuth * 7.7f) + 0.01f * MathF.Sin(azimuth * 17.3f + 0.7f);

    private bool HitSphere(Vector3 o, Vector3 d, int i, out float t)
    {
        var s = _spheres[i];
        var oc = o - new Vector3(s.X, s.Y, s.Z);
        var b = Vector3.Dot(oc, d);
        var c = oc.LengthSquared() - s.W * s.W;
        var h = b * b - c;
        t = 0;
        if (h < 0) return false;
        t = -b - MathF.Sqrt(h);
        return t > 1e-3f;
    }

    private bool HitPillar(Vector3 o, Vector3 d, int i, out float t)
    {
        var p = _pillars[i];
        float ox = o.X - p.X, oz = o.Z - p.Y;
        float a = d.X * d.X + d.Z * d.Z, b = ox * d.X + oz * d.Z, c = ox * ox + oz * oz - p.Z * p.Z;
        t = 0;
        if (a < 1e-9f) return false;
        var h = b * b - a * c;
        if (h < 0) return false;
        t = (-b - MathF.Sqrt(h)) / a;
        if (t <= 1e-3f) return false;
        var y = o.Y + d.Y * t;
        return y >= 0f && y <= p.W;
    }

    private bool Occluded(Vector3 p, Vector3 l)
    {
        for (var i = 0; i < _spheres.Length; i++) if (HitSphere(p, l, i, out _)) return true;
        for (var i = 0; i < _pillars.Length; i++) if (HitPillar(p, l, i, out _)) return true;
        return false;
    }

    public Vector3 Sample(Vector3 o, Vector3 d)
    {
        var sun = SunDirection;
        var tBest = float.MaxValue; var kind = 0; var idx = -1;
        for (var i = 0; i < _spheres.Length; i++) if (HitSphere(o, d, i, out var t) && t < tBest) { tBest = t; kind = 1; idx = i; }
        for (var i = 0; i < _pillars.Length; i++) if (HitPillar(o, d, i, out var t) && t < tBest) { tBest = t; kind = 2; idx = i; }
        if (d.Y < -1e-5f) { var tg = -o.Y / d.Y; if (tg > 1e-3f && tg < tBest) { tBest = tg; kind = 3; } }
        var sunLight = SunColor * (SunIntensity * 0.12f) * Math.Clamp(sun.Y * 3f, 0f, 1f);
        var ambient = new Vector3(0.18f, 0.22f, 0.3f) * SkyIntensity * (1f + Flash * 3f);
        Vector3 col;
        if (kind == 0)
        {
            var az = MathF.Atan2(d.X, d.Z);
            if (d.Y >= 0 && d.Y < Mountain(az)) return Vector3.Lerp(new Vector3(0.12f, 0.14f, 0.18f) * (0.6f + 0.4f * MathF.Max(0f, sun.Y)), Sky(d, sun), 0.55f);
            return Sky(d, sun);
        }
        var p = o + d * tBest;
        Vector3 n, albedo;
        if (kind == 1) { var s = _spheres[idx]; n = Vector3.Normalize(p - new Vector3(s.X, s.Y, s.Z)); albedo = _sphereColor[idx]; }
        else if (kind == 2) { var pl = _pillars[idx]; n = Vector3.Normalize(new Vector3(p.X - pl.X, 0, p.Z - pl.Y)); albedo = new Vector3(0.55f, 0.52f, 0.48f); }
        else
        {
            n = Vector3.UnitY;
            var chk = (((int)MathF.Floor(p.X) + (int)MathF.Floor(p.Z)) & 1) == 0;
            albedo = chk ? new Vector3(0.42f, 0.4f, 0.36f) : new Vector3(0.3f, 0.29f, 0.27f);
            var gx = p.X - MathF.Round(p.X); var gz = p.Z - MathF.Round(p.Z);
            if (MathF.Abs(gx) < 0.03f || MathF.Abs(gz) < 0.03f) albedo *= 0.6f;
        }
        var ndl = MathF.Max(0f, Vector3.Dot(n, sun));
        if (ndl > 0 && Occluded(p + n * 0.01f, sun)) ndl = 0;
        col = albedo * (ambient + sunLight * ndl);
        var fog = MathF.Exp(-tBest * FogDensity);
        return Vector3.Lerp(Sky(new Vector3(d.X, MathF.Max(0.02f, d.Y * 0.2f), d.Z), sun) * 0.9f, col, fog);
    }
}

/// <summary>시네마 카메라 추적 상태(센서 · 렌즈 · 포즈 · 등속 예측).</summary>
public readonly record struct CineLens(float FocalMm = 35f, float SensorWidthMm = 36f, float SensorHeightMm = 20.25f, float FocusDistanceM = 4f, float FStop = 2.8f)
{
    public float HfovDeg => 2f * MathF.Atan(SensorWidthMm * 0.5f / MathF.Max(1f, FocalMm)) * 180f / MathF.PI;
}

/// <summary>
/// [PromptFlow Studio v16.59.0] 🧱 ICVFX LED 월 프러스텀 렌더러 &amp; 실시간 시점 투영 엔진(Pure BCL · 프레임 무할당).
///   ▸ 추적: 시네마 카메라 포즈(위치 · 요/피치/롤 · 렌즈 초점거리/센서 → 화각) + 등속 예측으로 시스템 지연(트래킹→LED 발광) 보상 · v16.32 UE Live Link/OSC 샘플 어댑터.
///   ▸ 이너 프러스텀: LED 픽셀마다 카메라 눈 → 픽셀 광선으로 가상 세트를 샘플(시차 정확) · 오버스캔 + 소프트 에지 블렌드 · 크로마키 그린 옵션.
///   ▸ 아우터 프러스텀: 고정 스테이지 시점(볼륨 중심)에서 렌더 → 배우 반사/환경광 일관 · 노출 배율.
///   ▸ 오프액시스: 세그먼트별 Kooima 일반화 원근 뷰-투영 행렬(외부 GPU/nDisplay 결선) · 커버리지(카메라 시야의 월 점유율) · 인-카메라 합성 시뮬레이션.
///   ▸ VFX 레이어: LED 공간 비(빗줄기) 오버레이 · 번개 플래시(세트 하늘).
/// </summary>
public sealed class IcvfxLedWallFrustumRendererEngine
{
    private readonly object _gate = new();
    private LedVolume _volume;
    private IVirtualSet _set;
    private Vector3 _pos, _vel;
    private float _yaw, _pitch, _roll, _yawRate, _pitchRate;
    private long _lastMicros, _prevMicros;
    private CineLens _lens = new(FocalMm: 35f);
    private long _frames, _innerPixels;
    private double _renderMs;
    private float _coverage;

    public IcvfxLedWallFrustumRendererEngine() : this(LedVolume.Curved(), new ProceduralVirtualSet()) { }

    public IcvfxLedWallFrustumRendererEngine(LedVolume volume, IVirtualSet set)
    {
        _volume = volume; _set = set;
        _pos = new Vector3(0f, 1.6f, 1.5f);
    }

    public event Action? OnChange;

    public LedVolume Volume => _volume;
    public IVirtualSet Set => _set;
    public ProceduralVirtualSet? Procedural => _set as ProceduralVirtualSet;
    public CineLens Lens => _lens;
    /// <summary>트래킹 → LED 발광 지연(ms) — 등속 예측 보상량.</summary>
    public float LatencyMs { get; set; } = 30f;
    public bool LatencyCompensation { get; set; } = true;
    public float Overscan { get; set; } = 0.15f;
    public float EdgeSoftness { get; set; } = 0.08f;
    public bool ChromaKeyInner { get; set; }
    public Vector3 KeyGreen { get; set; } = new(0.05f, 0.6f, 0.08f);
    public Vector3 StageEye { get; set; } = new(0f, 1.7f, 0.5f);
    public float OuterExposure { get; set; } = 0.85f;
    public float Rain { get; set; }
    public double TimeSec { get; set; }
    public long Frames => Interlocked.Read(ref _frames);
    public double LastRenderMs => Volatile.Read(ref _renderMs);
    public long LastInnerPixels => Interlocked.Read(ref _innerPixels);
    public float LastCoverage => Volatile.Read(ref _coverage);
    public Vector3 TrackedPosition => _pos;
    public float TrackedYawDeg => _yaw * 180f / MathF.PI;
    public float TrackedPitchDeg => _pitch * 180f / MathF.PI;
    public string? LastAction { get; private set; }

    public void SetVolume(LedVolume v) { lock (_gate) _volume = v; }
    public void SetVirtualSet(IVirtualSet s) { lock (_gate) _set = s; }
    public void SetLens(in CineLens lens) { lock (_gate) _lens = lens; }

    /// <summary>트래킹 샘플 갱신(라디안 · µs) — 속도/각속도 추정(EMA).</summary>
    public void UpdateTracking(Vector3 position, float yaw, float pitch, float roll, long tMicros)
    {
        lock (_gate)
        {
            if (_lastMicros > 0 && tMicros > _lastMicros)
            {
                var dt = (tMicros - _lastMicros) / 1e6f;
                var v = (position - _pos) / dt;
                _vel = _prevMicros == 0 ? v : Vector3.Lerp(_vel, v, 0.6f);
                var dy = yaw - _yaw; dy -= MathF.Tau * MathF.Round(dy / MathF.Tau);
                _yawRate = _prevMicros == 0 ? dy / dt : _yawRate + (dy / dt - _yawRate) * 0.6f;
                _pitchRate = _prevMicros == 0 ? (pitch - _pitch) / dt : _pitchRate + ((pitch - _pitch) / dt - _pitchRate) * 0.6f;
                _prevMicros = _lastMicros;
            }
            _pos = position; _yaw = yaw; _pitch = pitch; _roll = roll;
            _lastMicros = tMicros;
        }
    }

    /// <summary>v16.32 UE Live Link/OSC 샘플(UE: X 전방 · Y 우 · Z 상 · m · 도) → 스테이지 좌표.</summary>
    public void UpdateFromUnreal(in CameraTrackingSample s, long tMicros)
    {
        UpdateTracking(new Vector3(s.Y, s.Z, s.X), s.Pan * MathF.PI / 180f, s.Tilt * MathF.PI / 180f, s.Roll * MathF.PI / 180f, tMicros);
        if (s.Fov > 1f) lock (_gate) _lens = _lens with { FocalMm = _lens.SensorWidthMm * 0.5f / MathF.Tan(s.Fov * MathF.PI / 360f) };
    }

    /// <summary>렌더 시점 카메라(지연 보상 예측 포함).</summary>
    public SplatCamera RenderCamera(int width, int height)
    {
        var ahead = LatencyCompensation && _prevMicros > 0 ? LatencyMs * 0.001f : 0f;
        var p = _pos + _vel * ahead;
        return SplatCamera.FromYawPitch(p, _yaw + _yawRate * ahead, _pitch + _pitchRate * ahead, _roll, _lens.HfovDeg, width, height);
    }

    /// <summary>현재 추적(예측 없음) 카메라.</summary>
    public SplatCamera TrackedCamera(int width, int height) => SplatCamera.FromYawPitch(_pos, _yaw, _pitch, _roll, _lens.HfovDeg, width, height);

    /// <summary>1프레임 LED 렌더(무할당) — 이너/아우터 프러스텀 · 소프트 에지 · VFX. 반환 = 이너 픽셀 수.</summary>
    public int RenderFrame()
    {
        var t0 = Stopwatch.GetTimestamp();
        long inner = 0;
        lock (_gate)
        {
            var cam = RenderCamera(1920, 1080);
            var eye = cam.Position;
            var sx = 1f + Overscan;
            var edge = MathF.Max(1e-3f, EdgeSoftness);
            var stage = StageEye;
            var rain = Rain;
            var time = (float)TimeSec;
            for (var s = 0; s < _volume.Count; s++)
            {
                var seg = _volume[s];
                var lin = seg.Linear; var mask = seg.Mask;
                for (var y = 0; y < seg.ResY; y++)
                {
                    for (var x = 0; x < seg.ResX; x++)
                    {
                        var p = seg.PixelCenter(x, y);
                        var c = cam.ToCamera(p);
                        var w = 0f;
                        if (c.Z > 0.05f)
                        {
                            var uv = cam.Project(c);
                            var nx = MathF.Abs((uv.X - cam.Cx) / (cam.Width * 0.5f * sx));
                            var ny = MathF.Abs((uv.Y - cam.Cy) / (cam.Height * 0.5f * sx));
                            w = Math.Clamp((1f - MathF.Max(nx, ny)) / edge, 0f, 1f);
                        }
                        Vector3 col;
                        if (w >= 1f) col = ChromaKeyInner ? KeyGreen : _set.Sample(eye, Vector3.Normalize(p - eye));
                        else
                        {
                            var outer = _set.Sample(stage, Vector3.Normalize(p - stage)) * OuterExposure;
                            if (w <= 0f) col = outer;
                            else col = Vector3.Lerp(outer, ChromaKeyInner ? KeyGreen : _set.Sample(eye, Vector3.Normalize(p - eye)), w);
                        }
                        if (rain > 0f)
                        {
                            var h = (uint)(x * 73856093) ^ (uint)(s * 19349663);
                            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                            var speed = 2.5f + (h & 7) * 0.4f;
                            var phase = ((h >> 8) & 1023) / 1023f;
                            var yy = (y / (float)seg.ResY + time * speed + phase) % 1f;
                            if ((h & 15) < 3 && yy < 0.06f) col += new Vector3(0.25f, 0.28f, 0.32f) * rain;
                            col *= 1f - 0.25f * rain;
                        }
                        var o = (y * seg.ResX + x) * 3;
                        lin[o] = col.X; lin[o + 1] = col.Y; lin[o + 2] = col.Z;
                        mask[y * seg.ResX + x] = w;
                        if (w > 0.5f) inner++;
                    }
                }
            }
            Volatile.Write(ref _coverage, CoverageLocked(cam));
        }
        Interlocked.Exchange(ref _innerPixels, inner);
        Volatile.Write(ref _renderMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        if ((Interlocked.Increment(ref _frames) & 15) == 1) { LastAction = "🧱 프러스텀 " + inner.ToString(CultureInfo.InvariantCulture) + "px · 커버리지 " + (LastCoverage * 100).ToString("0", CultureInfo.InvariantCulture) + "%"; Raise(); }
        return (int)inner;
    }

    private float CoverageLocked(in SplatCamera cam)
    {
        const int G = 12;
        var hit = 0;
        for (var j = 0; j < G; j++)
            for (var i = 0; i < G; i++)
            {
                var px = (i + 0.5f) / G * cam.Width; var py = (j + 0.5f) / G * cam.Height;
                var d = Vector3.Normalize(cam.Unproject(px, py, 1f) - cam.Position);
                if (_volume.Raycast(cam.Position, d, out _, out _, out _, out _) || HitsFloorFirst(cam.Position, d)) hit++;
            }
        return hit / (float)(G * G);
    }

    /// <summary>실물 스테이지 바닥(y = FloorY)에 월보다 먼저 닿는 광선 — 프랙티컬 세트로 간주(커버리지 포함).</summary>
    private bool HitsFloorFirst(Vector3 o, Vector3 d)
    {
        if (d.Y >= -1e-5f) return false;
        var tf = (FloorY - o.Y) / d.Y;
        if (tf <= 0f) return false;
        return !_volume.Raycast(o, d, out _, out _, out _, out var tw) || tf < tw;
    }

    /// <summary>실물 스테이지 바닥 높이(m) · 인-카메라 바닥 색(선형).</summary>
    public float FloorY { get; set; }
    public Vector3 StageFloorColor { get; set; } = new(0.06f, 0.055f, 0.05f);

    /// <summary>임의 카메라 커버리지(카메라 에이전트 질의용 · 0..1 · 바닥 = 프랙티컬).</summary>
    public float Coverage(in SplatCamera cam) { lock (_gate) return CoverageLocked(cam); }

    /// <summary>인-카메라 합성(카메라가 실제로 촬영하는 LED 발광 + 실물 바닥 · 선형 RGB float3). 반환 = 커버리지(월 + 바닥).</summary>
    public float CaptureInCamera(in SplatCamera cam, Span<float> rgbLinear)
    {
        var hit = 0;
        lock (_gate)
        {
            for (var py = 0; py < cam.Height; py++)
                for (var px = 0; px < cam.Width; px++)
                {
                    var d = Vector3.Normalize(cam.Unproject(px + 0.5f, py + 0.5f, 1f) - cam.Position);
                    var o = (py * cam.Width + px) * 3;
                    var wall = _volume.Raycast(cam.Position, d, out var s, out var u, out var v, out var tw);
                    if (d.Y < -1e-5f && (FloorY - cam.Position.Y) / d.Y is var tf && tf > 0f && (!wall || tf < tw))
                    {
                        var fc = StageFloorColor;
                        rgbLinear[o] = fc.X; rgbLinear[o + 1] = fc.Y; rgbLinear[o + 2] = fc.Z;
                        hit++;
                    }
                    else if (wall)
                    {
                        var c = _volume[s].SampleLinear(u, v);
                        rgbLinear[o] = c.X; rgbLinear[o + 1] = c.Y; rgbLinear[o + 2] = c.Z;
                        hit++;
                    }
                    else { rgbLinear[o] = 0; rgbLinear[o + 1] = 0; rgbLinear[o + 2] = 0; }
                }
        }
        return hit / (float)(cam.Width * cam.Height);
    }

    /// <summary>기준 렌더 — 카메라에서 가상 세트 직접 관측(LED 없이 이상적 합성).</summary>
    public void RenderReference(in SplatCamera cam, Span<float> rgbLinear)
    {
        lock (_gate)
            for (var py = 0; py < cam.Height; py++)
                for (var px = 0; px < cam.Width; px++)
                {
                    var c = _set.Sample(cam.Position, Vector3.Normalize(cam.Unproject(px + 0.5f, py + 0.5f, 1f) - cam.Position));
                    var o = (py * cam.Width + px) * 3;
                    rgbLinear[o] = c.X; rgbLinear[o + 1] = c.Y; rgbLinear[o + 2] = c.Z;
                }
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Icvfx.Frustum.OnChange", ex); }
    }
}
