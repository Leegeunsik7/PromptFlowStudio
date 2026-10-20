using System.Numerics;
using PromptFlowStudio.Services.ArGraphics;

namespace PromptFlowStudio.Services.LedVolume;

/// <summary>
/// [PromptFlow Studio v17.09.0] 곡면 LED 볼륨 토폴로지 — 스테이지 원점 기준 반경 R 원호(정면 = +z · 각도 증가 = +x) 를 평면 패널(현)로 근사.
/// 패널 i = 각도 [θi, θi+1] 현 · 높이 H. 콘텐츠 맵 = 패널당 ColsPerPanel 열 × Rows 행(행 0 = 상단). 물리 LED 좌표 = 현 길이 / 피치.
/// SoA 배열은 최대 용량(MaxPanels · 16 배수 패딩)으로 1회 할당 — 토폴로지 변경 시 재계산만.
/// </summary>
public sealed class LedVolumeTopology
{
    public const int MaxPanels = 64, ColsPerPanel = 8, Rows = 48, MaxMap = MaxPanels * ColsPerPanel * Rows;

    public readonly float[] Ax = new float[MaxPanels], Ay = new float[MaxPanels], Az = new float[MaxPanels];
    public readonly float[] Bx = new float[MaxPanels], By = new float[MaxPanels], Bz = new float[MaxPanels];
    public readonly float[] Cx = new float[MaxPanels], Cy = new float[MaxPanels], Cz = new float[MaxPanels];
    public readonly float[] Rx = new float[MaxPanels], Ry = new float[MaxPanels], Rz = new float[MaxPanels];
    public readonly float[] Ux = new float[MaxPanels], Uy = new float[MaxPanels], Uz = new float[MaxPanels];
    public readonly float[] Nx = new float[MaxPanels], Ny = new float[MaxPanels], Nz = new float[MaxPanels];
    public readonly float[] Px = new float[MaxMap], Py = new float[MaxMap], Pz = new float[MaxMap];

    public LedVolumeTopology() => Rebuild();

    public float RadiusM { get; private set; } = 6f;
    public float ArcDeg { get; private set; } = 160f;
    public float HeightM { get; private set; } = 4.5f;
    public float PanelWidthM { get; private set; } = 0.5f;
    public float PitchMm { get; private set; } = 2.6f;
    public int Panels { get; private set; }
    public int MapW => Panels * ColsPerPanel;
    public int MapH => Rows;
    public int MapCount => MapW * Rows;
    public float DTheta { get; private set; }
    public float Chord { get; private set; }
    public long Version { get; private set; }
    public long LedColumns => (long)(Panels * Chord / (PitchMm * 1e-3f));
    public long LedRows => (long)(HeightM / (PitchMm * 1e-3f));

    public void Set(float radiusM, float arcDeg, float heightM, float panelWidthM, float pitchMm)
    {
        RadiusM = Math.Clamp(radiusM, 2f, 20f); ArcDeg = Math.Clamp(arcDeg, 20f, 300f); HeightM = Math.Clamp(heightM, 1f, 10f);
        PanelWidthM = Math.Clamp(panelWidthM, 0.25f, 2f); PitchMm = Math.Clamp(pitchMm, 0.9f, 12f);
        Rebuild();
    }

    public Vector3 Point(float theta, float h) => new(RadiusM * MathF.Sin(theta), h, RadiusM * MathF.Cos(theta));
    public float Theta0(int i) => -ArcDeg * MathF.PI / 360f + i * DTheta;

    private void Rebuild()
    {
        var arc = ArcDeg * MathF.PI / 180f;
        Panels = Math.Clamp((int)MathF.Round(arc * RadiusM / PanelWidthM), 4, MaxPanels);
        DTheta = arc / Panels;
        Chord = 2f * RadiusM * MathF.Sin(DTheta * 0.5f);
        for (var i = 0; i < MaxPanels; i++)
        {
            if (i >= Panels) { Ax[i] = Ay[i] = Az[i] = Bx[i] = By[i] = Bz[i] = Cx[i] = Cy[i] = Cz[i] = Rx[i] = Ry[i] = Rz[i] = Ux[i] = Uy[i] = Uz[i] = Nx[i] = Ny[i] = 0; Nz[i] = 1; continue; }
            float t0 = Theta0(i), t1 = t0 + DTheta;
            // Kooima: pa = 좌하(내부 시점 기준) · pb = 우하 · pc = 좌상 · vn = 시점 방향(원점 쪽).
            Vector3 pa = Point(t1, 0), pb = Point(t0, 0), pc = Point(t1, HeightM);
            var vr = Vector3.Normalize(pb - pa); var vu = Vector3.UnitY; var vn = Vector3.Cross(vr, vu);
            Ax[i] = pa.X; Ay[i] = pa.Y; Az[i] = pa.Z; Bx[i] = pb.X; By[i] = pb.Y; Bz[i] = pb.Z; Cx[i] = pc.X; Cy[i] = pc.Y; Cz[i] = pc.Z;
            Rx[i] = vr.X; Ry[i] = vr.Y; Rz[i] = vr.Z; Ux[i] = vu.X; Uy[i] = vu.Y; Uz[i] = vu.Z; Nx[i] = vn.X; Ny[i] = vn.Y; Nz[i] = vn.Z;
        }
        int mw = MapW;
        for (var r = 0; r < Rows; r++)
        {
            var h = HeightM * (1f - (r + 0.5f) / Rows);
            for (var c = 0; c < mw; c++)
            {
                var i = c / ColsPerPanel;
                var f = (c - i * ColsPerPanel + 0.5f) / ColsPerPanel;
                var t0 = Theta0(i);
                var p = Vector3.Lerp(Point(t0, h), Point(t0 + DTheta, h), f);
                var k = r * mw + c;
                Px[k] = p.X; Py[k] = p.Y; Pz[k] = p.Z;
            }
        }
        for (var k = MapCount; k < MaxMap; k++) { Px[k] = 0; Py[k] = -100f; Pz[k] = 0; }
        Version++;
    }

    /// <summary>광선(내부 시점) ∩ 패널 현 — panel · f(각도 증가 방향 0..1) · 높이 h · 거리 t.</summary>
    public bool Intersect(Vector3 o, Vector3 d, out int panel, out float f, out float h, out float t)
    {
        panel = -1; f = h = t = 0;
        float a = d.X * d.X + d.Z * d.Z;
        if (a < 1e-9f) return false;
        float b = 2f * (o.X * d.X + o.Z * d.Z), c = o.X * o.X + o.Z * o.Z - RadiusM * RadiusM;
        var disc = b * b - 4f * a * c;
        if (disc < 0) return false;
        var tc = (-b + MathF.Sqrt(disc)) / (2f * a);
        if (tc <= 0) return false;
        float px = o.X + d.X * tc, pz = o.Z + d.Z * tc;
        var half = ArcDeg * MathF.PI / 360f;
        var idx = (int)MathF.Floor((MathF.Atan2(px, pz) + half) / DTheta);
        if (idx < 0 || idx >= Panels) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            float t0 = Theta0(idx), tm = t0 + DTheta * 0.5f;
            var p0 = Point(t0, 0); var p1 = Point(t0 + DTheta, 0);
            var n = new Vector3(-MathF.Sin(tm), 0, -MathF.Cos(tm));
            var den = Vector3.Dot(d, n);
            if (MathF.Abs(den) < 1e-7f) return false;
            t = Vector3.Dot(p0 - o, n) / den;
            var hit = o + d * t;
            var e = p1 - p0;
            f = Vector3.Dot(hit - p0, e) / e.LengthSquared();
            h = hit.Y;
            if (f < 0 && idx > 0) { idx--; continue; }
            if (f > 1 && idx < Panels - 1) { idx++; continue; }
            break;
        }
        panel = idx;
        return t > 0 && f >= 0 && f <= 1 && h >= 0 && h <= HeightM;
    }
}

/// <summary>[PromptFlow Studio v17.09.0] 내부 프러스텀 상태(렌더 시점 · 기저 · 왜곡 역보정 반각 탄젠트 · 페더).</summary>
public struct FrustumState
{
    public Vector3 Eye, F, R, U;
    public float TanX, TanY, Feather, HFovDeg, K1;
    public long Frame;
}

/// <summary>
/// [PromptFlow Studio v17.09.0] 오프액시스 SIMD 커널(스칼라 쌍둥이와 비트 동일 · FMA 축약 없음).
/// Extents = Kooima 일반화 원근: va = pa − e · d = −va·vn · (l, r, b, t) = (vr·va, vr·vb, vu·va, vu·vc) · near/d.
/// InnerMask = LED 맵 픽셀별 내부 프러스텀 가중(카메라 정규 좌표 |x|,|y| 대 반각 탄젠트 · 선형 페더).
/// </summary>
public static class OffAxisSimd
{
    public const float MinDepth = 0.05f;

    public static void Extents(LedVolumeTopology p, int count, Vector3 eye, float near, Span<float> l, Span<float> r, Span<float> b, Span<float> t, Span<float> dist)
    {
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && count >= w)
        {
            Vector<float> ex = new(eye.X), ey = new(eye.Y), ez = new(eye.Z), vnear = new(near);
            for (; i <= count - w; i += w)
            {
                var vax = new Vector<float>(p.Ax, i) - ex; var vay = new Vector<float>(p.Ay, i) - ey; var vaz = new Vector<float>(p.Az, i) - ez;
                var vbx = new Vector<float>(p.Bx, i) - ex; var vby = new Vector<float>(p.By, i) - ey; var vbz = new Vector<float>(p.Bz, i) - ez;
                var vcx = new Vector<float>(p.Cx, i) - ex; var vcy = new Vector<float>(p.Cy, i) - ey; var vcz = new Vector<float>(p.Cz, i) - ez;
                Vector<float> rx = new(p.Rx, i), ry = new(p.Ry, i), rz = new(p.Rz, i), ux = new(p.Ux, i), uy = new(p.Uy, i), uz = new(p.Uz, i);
                var d = -(vax * new Vector<float>(p.Nx, i) + vay * new Vector<float>(p.Ny, i) + vaz * new Vector<float>(p.Nz, i));
                var s = vnear / d;
                ((rx * vax + ry * vay + rz * vaz) * s).CopyTo(l.Slice(i));
                ((rx * vbx + ry * vby + rz * vbz) * s).CopyTo(r.Slice(i));
                ((ux * vax + uy * vay + uz * vaz) * s).CopyTo(b.Slice(i));
                ((ux * vcx + uy * vcy + uz * vcz) * s).CopyTo(t.Slice(i));
                d.CopyTo(dist.Slice(i));
            }
        }
        for (; i < count; i++) ExtentOne(p, i, eye, near, l, r, b, t, dist);
    }

    public static void ExtentsScalar(LedVolumeTopology p, int count, Vector3 eye, float near, Span<float> l, Span<float> r, Span<float> b, Span<float> t, Span<float> dist)
    {
        for (var i = 0; i < count; i++) ExtentOne(p, i, eye, near, l, r, b, t, dist);
    }

    private static void ExtentOne(LedVolumeTopology p, int i, Vector3 e, float near, Span<float> l, Span<float> r, Span<float> b, Span<float> t, Span<float> dist)
    {
        float vax = p.Ax[i] - e.X, vay = p.Ay[i] - e.Y, vaz = p.Az[i] - e.Z;
        float vbx = p.Bx[i] - e.X, vby = p.By[i] - e.Y, vbz = p.Bz[i] - e.Z;
        float vcx = p.Cx[i] - e.X, vcy = p.Cy[i] - e.Y, vcz = p.Cz[i] - e.Z;
        var d = -(vax * p.Nx[i] + vay * p.Ny[i] + vaz * p.Nz[i]);
        var s = near / d;
        l[i] = (p.Rx[i] * vax + p.Ry[i] * vay + p.Rz[i] * vaz) * s;
        r[i] = (p.Rx[i] * vbx + p.Ry[i] * vby + p.Rz[i] * vbz) * s;
        b[i] = (p.Ux[i] * vax + p.Uy[i] * vay + p.Uz[i] * vaz) * s;
        t[i] = (p.Ux[i] * vcx + p.Uy[i] * vcy + p.Uz[i] * vcz) * s;
        dist[i] = d;
    }

    public static void InnerMask(ReadOnlySpan<float> px, ReadOnlySpan<float> py, ReadOnlySpan<float> pz, in FrustumState s, Span<float> w)
    {
        var n = w.Length;
        var i = 0;
        var vw = Vector<float>.Count;
        var ife = 1f / MathF.Max(1e-5f, s.Feather);
        if (Vector.IsHardwareAccelerated && n >= vw)
        {
            Vector<float> ex = new(s.Eye.X), ey = new(s.Eye.Y), ez = new(s.Eye.Z), fx = new(s.F.X), fy = new(s.F.Y), fz = new(s.F.Z);
            Vector<float> rx = new(s.R.X), ry = new(s.R.Y), rz = new(s.R.Z), ux = new(s.U.X), uy = new(s.U.Y), uz = new(s.U.Z);
            Vector<float> tx = new(s.TanX), ty = new(s.TanY), vi = new(ife), eps = new(MinDepth), one = Vector<float>.One, zero = Vector<float>.Zero;
            for (; i <= n - vw; i += vw)
            {
                var dx = new Vector<float>(px.Slice(i)) - ex; var dy = new Vector<float>(py.Slice(i)) - ey; var dz = new Vector<float>(pz.Slice(i)) - ez;
                var z = dx * fx + dy * fy + dz * fz;
                var iz = one / Vector.Max(z, eps);
                var x = Vector.Abs((dx * rx + dy * ry + dz * rz) * iz);
                var y = Vector.Abs((dx * ux + dy * uy + dz * uz) * iz);
                var wx = Vector.Min(one, Vector.Max(zero, one - (x - tx) * vi));
                var wy = Vector.Min(one, Vector.Max(zero, one - (y - ty) * vi));
                Vector.ConditionalSelect(Vector.GreaterThan(z, eps), wx * wy, zero).CopyTo(w.Slice(i));
            }
        }
        for (; i < n; i++) w[i] = MaskOne(px[i], py[i], pz[i], s, ife);
    }

    public static void InnerMaskScalar(ReadOnlySpan<float> px, ReadOnlySpan<float> py, ReadOnlySpan<float> pz, in FrustumState s, Span<float> w)
    {
        var ife = 1f / MathF.Max(1e-5f, s.Feather);
        for (var i = 0; i < w.Length; i++) w[i] = MaskOne(px[i], py[i], pz[i], s, ife);
    }

    private static float MaskOne(float px, float py, float pz, in FrustumState s, float ife)
    {
        float dx = px - s.Eye.X, dy = py - s.Eye.Y, dz = pz - s.Eye.Z;
        var z = dx * s.F.X + dy * s.F.Y + dz * s.F.Z;
        var iz = 1f / MathF.Max(z, MinDepth);
        var x = MathF.Abs((dx * s.R.X + dy * s.R.Y + dz * s.R.Z) * iz);
        var y = MathF.Abs((dx * s.U.X + dy * s.U.Y + dz * s.U.Z) * iz);
        var wx = MathF.Min(1f, MathF.Max(0f, 1f - (x - s.TanX) * ife));
        var wy = MathF.Min(1f, MathF.Max(0f, 1f - (y - s.TanY) * ife));
        return z > MinDepth ? wx * wy : 0f;
    }
}

/// <summary>
/// [PromptFlow Studio v17.09.0] 절차적 가상 환경(LED 월 너머) — 하늘 그라디언트 · 태양 · 산 능선 · 바닥 체커 · 석주 4개(시차 랜드마크).
/// </summary>
public static class VirtualWorld
{
    public static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(-0.3f, 0.45f, 0.85f));
    private static readonly Vector4[] Pillars = { new(-5f, 14f, 0.7f, 6f), new(3.5f, 11f, 0.6f, 5f), new(9f, 18f, 0.9f, 7f), new(-11f, 20f, 1f, 8f) };
    private static readonly Vector3[] Ground = { new(-3f, 0f, 10f), new(2f, 0f, 9f), new(6f, 0f, 12f), new(-1f, 0f, 16f) };
    public const int Landmarks = 8;

    public static Vector3 Landmark(int i) => i < 4 ? new Vector3(Pillars[i].X, Pillars[i].W * 0.8f, Pillars[i].Y) : Ground[i - 4];

    public static Vector3 Shade(Vector3 o, Vector3 dir)
    {
        var d = Vector3.Normalize(dir);
        var best = 1e9f; var hit = -1; var hp = Vector3.Zero;
        for (var k = 0; k < Pillars.Length; k++)
        {
            var pl = Pillars[k];
            float dx = o.X - pl.X, dz = o.Z - pl.Y, a = d.X * d.X + d.Z * d.Z;
            if (a < 1e-8f) continue;
            float b = 2f * (dx * d.X + dz * d.Z), c = dx * dx + dz * dz - pl.Z * pl.Z, disc = b * b - 4f * a * c;
            if (disc < 0) continue;
            var t = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (t <= 0.01f || t >= best) continue;
            var y = o.Y + d.Y * t;
            if (y < 0 || y > pl.W) continue;
            best = t; hit = k; hp = o + d * t;
        }
        if (d.Y < -1e-4f)
        {
            var t = -o.Y / d.Y;
            if (t > 0 && t < best) { best = t; hit = -2; hp = o + d * t; }
        }
        Vector3 horizon = new(0.85f, 0.78f, 0.7f);
        if (hit >= 0)
        {
            var pl = Pillars[hit];
            var n = Vector3.Normalize(new Vector3(hp.X - pl.X, 0, hp.Z - pl.Y));
            var diff = MathF.Max(0f, Vector3.Dot(n, SunDir)) * 0.8f + 0.25f;
            var ring = hp.Y - MathF.Floor(hp.Y) < 0.06f ? 0.6f : 1f;
            return new Vector3(0.62f, 0.58f, 0.52f) * (diff * ring);
        }
        if (hit == -2)
        {
            var chk = (((int)MathF.Floor(hp.X * 0.5f) + (int)MathF.Floor(hp.Z * 0.5f)) & 1) == 0;
            var g = chk ? new Vector3(0.36f, 0.45f, 0.25f) : new Vector3(0.3f, 0.38f, 0.2f);
            return Vector3.Lerp(g, horizon, 1f - MathF.Exp(-best / 60f));
        }
        var el = d.Y;
        var az = MathF.Atan2(d.X, d.Z);
        var mh = 0.06f + 0.035f * MathF.Sin(3f * az + 0.4f) + 0.018f * MathF.Sin(9f * az + 1.3f);
        if (el < mh) return Vector3.Lerp(new Vector3(0.24f, 0.28f, 0.36f), new Vector3(0.4f, 0.45f, 0.55f), Math.Clamp(el / mh, 0f, 1f));
        var sky = Vector3.Lerp(horizon, new Vector3(0.25f, 0.45f, 0.85f), MathF.Sqrt(Math.Clamp(el, 0f, 1f)));
        var sd = MathF.Max(0f, Vector3.Dot(d, SunDir));
        return sky + new Vector3(1f, 0.9f, 0.7f) * (MathF.Pow(sd, 400f) * 3f + MathF.Pow(sd, 12f) * 0.25f);
    }
}

/// <summary>
/// [PromptFlow Studio v17.09.0] 🎯 실시간 내부 프러스텀 오프액시스 투영 엔진(무할당).
/// FreeD D1 수신(체크섬) → 자세 링 → 2차 예측(LED 파이프라인 지연 보상) → 추적 카메라(줌 → FOV · K1/K2 · ST 맵) →
/// 센서 경계 ST 맵 노드의 역왜곡 최대 탄젠트로 반각 산출(배럴 왜곡 모서리까지 내부 프러스텀이 덮음) + 오버스캔 →
/// 패널별 Kooima 비대칭 프러스텀(SIMD) · LED 맵 내부 가중(SIMD) · 패널별 고해상 렌더 뷰포트(LED 픽셀 사각형).
/// </summary>
public sealed class InnerFrustumOffAxisProjectionEngine
{
    public const int Ring = 64, FeedW = 160, FeedH = 90;

    private readonly FreeDPose[] _ring = new FreeDPose[Ring];
    private readonly long[] _ringFrame = new long[Ring];
    private readonly float[] _l = new float[LedVolumeTopology.MaxPanels], _r = new float[LedVolumeTopology.MaxPanels], _b = new float[LedVolumeTopology.MaxPanels], _t = new float[LedVolumeTopology.MaxPanels], _d = new float[LedVolumeTopology.MaxPanels];
    private readonly int[] _vx0 = new int[LedVolumeTopology.MaxPanels], _vx1 = new int[LedVolumeTopology.MaxPanels], _vy0 = new int[LedVolumeTopology.MaxPanels], _vy1 = new int[LedVolumeTopology.MaxPanels];
    private readonly float[] _w = new float[LedVolumeTopology.MaxMap];
    private long _latest = -1;
    private FrustumState _state;

    public InnerFrustumOffAxisProjectionEngine() : this(new LedVolumeTopology()) { }

    public InnerFrustumOffAxisProjectionEngine(LedVolumeTopology topology)
    {
        Topology = topology;
        Camera = new LensCamera(FeedW, FeedH);
        Camera.Set(0, 0, 0, new Vector3(0, 1.6f, 0), 40f, K1ForFov(40f), 0f);
    }

    public LedVolumeTopology Topology { get; }
    /// <summary>추적 카메라(예측 자세 · 렌즈).</summary>
    public LensCamera Camera { get; }
    public float OverscanPct { get; set; } = 10f;
    public float FeatherPct { get; set; } = 4f;
    public bool DistortionCompensation { get; set; } = true;
    public bool Predict { get; set; } = true;
    /// <summary>추적 → LED 발광까지 파이프라인 지연(프레임) = 예측 지평.</summary>
    public int LatencyFrames { get; set; } = 3;
    public float Near { get; set; } = 0.1f;
    public long Packets { get; private set; }
    public long BadPackets { get; private set; }
    public FreeDPose Latest { get; private set; }
    public FreeDPose Predicted { get; private set; }
    public ref readonly FrustumState State => ref _state;
    public int VisiblePanels { get; private set; }
    public long InnerLedPixels { get; private set; }
    public float CompensationGainPct { get; private set; }
    public double KooimaUs { get; private set; }
    public double MaskUs { get; private set; }
    public ReadOnlySpan<float> Weights => _w.AsSpan(0, Topology.MapCount);
    public float Left(int i) => _l[i];
    public float RightE(int i) => _r[i];
    public float Bottom(int i) => _b[i];
    public float Top(int i) => _t[i];
    public float Distance(int i) => _d[i];
    public bool PanelVisible(int i) => _vx1[i] > _vx0[i];
    public (int X0, int Y0, int X1, int Y1) Viewport(int i) => (_vx0[i], _vy0[i], _vx1[i], _vy1[i]);

    // ── 렌즈 모델(줌 엔코더 ↔ FOV · 배럴 왜곡) ──
    public static float FovForZoom(int zoom) => 70f - 60f * Math.Clamp(zoom, 0, 65535) / 65535f;
    public static int ZoomForFov(float fov) => (int)MathF.Round((70f - Math.Clamp(fov, 10f, 70f)) / 60f * 65535f);
    public static float K1ForFov(float fov) => -0.02f - 0.13f * Math.Clamp((fov - 10f) / 60f, 0f, 1f);
    public static float K2ForFov(float fov) => 0.012f * Math.Clamp((fov - 10f) / 60f, 0f, 1f);

    /// <summary>FreeD 패킷 수신(무할당 · 체크섬 불량 폐기).</summary>
    public bool Ingest(ReadOnlySpan<byte> pkt, long frame)
    {
        if (!FreeD.Decode(pkt, out var pose)) { BadPackets++; return false; }
        _ring[frame % Ring] = pose; _ringFrame[frame % Ring] = frame;
        _latest = frame; Latest = pose; Packets++;
        return true;
    }

    private bool Pose(long frame, out FreeDPose p)
    {
        p = default;
        if (frame < 0 || _ringFrame[frame % Ring] != frame) return false;
        p = _ring[frame % Ring];
        return true;
    }

    /// <summary>예측 자세 → 추적 카메라 → 프러스텀 · Kooima · 내부 마스크 · 뷰포트.</summary>
    public void Update()
    {
        if (_latest < 0) return;
        var p = Latest;
        if (Predict && LatencyFrames > 0 && Pose(_latest - 1, out var p1) && Pose(_latest - 2, out var p2))
        {
            float h = LatencyFrames;
            p.X = Q(p.X, p1.X, p2.X, h); p.Y = Q(p.Y, p1.Y, p2.Y, h); p.Z = Q(p.Z, p1.Z, p2.Z, h);
            p.Pan = Q(p.Pan, p1.Pan, p2.Pan, h); p.Tilt = Q(p.Tilt, p1.Tilt, p2.Tilt, h); p.Roll = Q(p.Roll, p1.Roll, p2.Roll, h);
            p.Zoom = Math.Clamp((int)MathF.Round(Q(p.Zoom, p1.Zoom, p2.Zoom, h)), 0, 65535);
        }
        Predicted = p;
        var fov = FovForZoom(p.Zoom);
        Camera.Set(p.Pan, p.Tilt, p.Roll, new Vector3(p.X, p.Z, p.Y), fov, K1ForFov(fov), K2ForFov(fov));
        float mx, my;
        if (DistortionCompensation)
        {
            mx = 0; my = 0;
            for (var j = 0; j < LensCamera.GridH; j++)
                for (var i = 0; i < LensCamera.GridW; i++)
                {
                    if (i != 0 && j != 0 && i != LensCamera.GridW - 1 && j != LensCamera.GridH - 1) continue;
                    var k = j * LensCamera.GridW + i;
                    mx = MathF.Max(mx, MathF.Abs(Camera.GridXu(k))); my = MathF.Max(my, MathF.Abs(Camera.GridYu(k)));
                }
        }
        else { mx = Camera.Cx / Camera.Fx; my = Camera.Cy / Camera.Fx; }
        CompensationGainPct = (mx / (Camera.Cx / Camera.Fx) - 1f) * 100f;
        var os = 1f + OverscanPct * 0.01f;
        _state = new FrustumState
        {
            Eye = Camera.Position, F = Camera.Forward, R = Camera.Right, U = Camera.Up,
            TanX = mx * os * 1.0005f, TanY = my * os * 1.0005f, Feather = FeatherPct * 0.01f * mx, HFovDeg = fov, K1 = Camera.K1, Frame = _latest,
        };
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        OffAxisSimd.Extents(Topology, Topology.Panels, _state.Eye, Near, _l, _r, _b, _t, _d);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Mask(_state);
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        KooimaUs = (t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        MaskUs = (t2 - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>등간격 3점 2차 외삽(h 프레임 앞).</summary>
    private static float Q(float y0, float y1, float y2, float h)
    {
        float v = (3f * y0 - 4f * y1 + y2) * 0.5f, a = y0 - 2f * y1 + y2;
        return y0 + v * h + 0.5f * a * h * h;
    }

    /// <summary>LED 맵 내부 가중(SIMD) + 패널 뷰포트(물리 LED 픽셀).</summary>
    public void Mask(in FrustumState s)
    {
        var topo = Topology;
        int n = topo.MapCount, mw = topo.MapW;
        OffAxisSimd.InnerMask(topo.Px.AsSpan(0, n), topo.Py.AsSpan(0, n), topo.Pz.AsSpan(0, n), s, _w.AsSpan(0, n));
        float colPx = topo.Chord / (topo.PitchMm * 1e-3f) / LedVolumeTopology.ColsPerPanel, rowPx = topo.HeightM / (topo.PitchMm * 1e-3f) / LedVolumeTopology.Rows;
        int vis = 0; long area = 0;
        for (var i = 0; i < topo.Panels; i++)
        {
            int c0 = int.MaxValue, c1 = -1, r0 = int.MaxValue, r1 = -1;
            for (var r = 0; r < LedVolumeTopology.Rows; r++)
                for (var c = i * LedVolumeTopology.ColsPerPanel; c < (i + 1) * LedVolumeTopology.ColsPerPanel; c++)
                {
                    if (_w[r * mw + c] <= 0f) continue;
                    if (c < c0) c0 = c; if (c > c1) c1 = c; if (r < r0) r0 = r; if (r > r1) r1 = r;
                }
            if (c1 < 0) { _vx0[i] = _vx1[i] = _vy0[i] = _vy1[i] = 0; continue; }
            var lc = i * LedVolumeTopology.ColsPerPanel;
            _vx0[i] = (int)((c0 - lc) * colPx); _vx1[i] = (int)MathF.Ceiling((c1 + 1 - lc) * colPx);
            _vy0[i] = (int)(r0 * rowPx); _vy1[i] = (int)MathF.Ceiling((r1 + 1) * rowPx);
            vis++; area += (long)(_vx1[i] - _vx0[i]) * (_vy1[i] - _vy0[i]);
        }
        VisiblePanels = vis; InnerLedPixels = area;
    }

    /// <summary>패널 i 오프액시스 투영 행렬(행 벡터 · 우수 좌표 · 뷰 × 원근).</summary>
    public Matrix4x4 Projection(int i, float far = 1000f)
    {
        var t = Topology;
        var e = _state.Eye;
        var view = Matrix4x4.CreateTranslation(-e) * new Matrix4x4(t.Rx[i], t.Ux[i], t.Nx[i], 0, t.Ry[i], t.Uy[i], t.Ny[i], 0, t.Rz[i], t.Uz[i], t.Nz[i], 0, 0, 0, 0, 1);
        return view * Matrix4x4.CreatePerspectiveOffCenter(_l[i], _r[i], _b[i], _t[i], Near, far);
    }

    /// <summary>특정 상태의 시점 기준 내부 프러스텀 판정(정확 · UI/검증용).</summary>
    public static bool Inside(in FrustumState s, Vector3 w)
    {
        var d = w - s.Eye;
        var z = Vector3.Dot(d, s.F);
        if (z <= OffAxisSimd.MinDepth) return false;
        return MathF.Abs(Vector3.Dot(d, s.R) / z) <= s.TanX && MathF.Abs(Vector3.Dot(d, s.U) / z) <= s.TanY;
    }
}
