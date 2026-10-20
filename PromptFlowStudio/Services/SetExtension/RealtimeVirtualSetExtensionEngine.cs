using System.Numerics;
using PromptFlowStudio.Services.GenlockFrustum;
using PromptFlowStudio.Services.WorldExpansion;
namespace PromptFlowStudio.Services.SetExtension;

/// <summary>세트 카메라 포즈 — 위치(m) · 팬/틸트/롤(rad, v17.44/v17.45 규약).</summary>
public struct SetCameraPose
{
    public Vector3 Position;
    public float Pan, Tilt, Roll;

    public readonly void Basis(out Vector3 fwd, out Vector3 right, out Vector3 up)
    {
        var cp = MathF.Cos(Tilt);
        fwd = new Vector3(MathF.Sin(Pan) * cp, MathF.Sin(Tilt), -MathF.Cos(Pan) * cp);
        var r0 = new Vector3(MathF.Cos(Pan), 0f, MathF.Sin(Pan));
        var u0 = Vector3.Cross(r0, fwd);
        right = r0 * MathF.Cos(Roll) + u0 * MathF.Sin(Roll);
        up = u0 * MathF.Cos(Roll) - r0 * MathF.Sin(Roll);
    }
}

public enum SetRegion : byte { Led, Ceiling, Floor, Open }

/// <summary>
/// [PromptFlow Studio v17.46.0] 🏛️ 실시간 볼륨 경계 오버런 &amp; 3D 가상 세트 확장 엔진(<see cref="Compute"/> · <see cref="RenderExtension"/> 60 Hz 무할당).
/// v17.45 트래킹 카메라(수평 60°, 224×126 뷰포트)의 시야 절두체로 v17.44 곡면 LED 볼륨(R 6 m · 240° · 24×4 패널)의 각 패널 사각형을
/// 카메라 공간에서 Sutherland-Hodgman 으로 근평면 + 좌/우/상/하 4 평면에 순차 클리핑(stackalloc 16 정점)하고 신발끈 면적으로
/// LED 커버리지 · 볼륨 경계 탈출(Overrun %) 을 산출한다. 픽셀별 광선(카메라 공간 SoA 고정 → 월드 회전 Vector&lt;float&gt; 커널 · 스칼라 비트 동일)으로
/// LED / 천장 · 바닥 · 측면 크로마 영역을 분류하고, LED 경계까지의 체임퍼 거리장(px)을 만든 뒤 LED 밖으로 노출된 뷰포트에
/// v17.27 월드 스타일(지면 · 악센트 팔레트) 방향 함수 가상 월드(하늘 · 태양 · 산맥 실루엣 · 원거리 지면 그리드)를 옥타헤드럴 LUT 로 베이크해 연속 투영한다.
/// </summary>
public sealed class RealtimeVirtualSetExtensionEngine
{
    public const int W = 224, H = 126, Px = W * H, MaxClip = 16, LutSize = 512, Panels = RealtimeOffAxisFrustumProjectionEngine.Panels;
    public const float HFovDeg = 60f, NearM = 0.05f, GroundDepthM = 20f;
    public static readonly float FocalPx = 0.5f * W / MathF.Tan(HFovDeg * MathF.PI / 360f);
    public static readonly float TanH = 0.5f * W / FocalPx, TanV = 0.5f * H / FocalPx;
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.35f, 0.42f, -0.84f));
    private readonly float[] _cx = new float[Px], _cy = new float[Px], _cz = new float[Px];
    private readonly float[] _dx = new float[Px], _dy = new float[Px], _dz = new float[Px], _ex = new float[Px], _ey = new float[Px], _ez = new float[Px];
    private readonly float[] _tr = new float[Px], _tg = new float[Px], _tb = new float[Px], _xr = new float[Px], _xg = new float[Px], _xb = new float[Px];
    private readonly float[] _dist = new float[Px], _clip = new float[Panels * MaxClip * 2];
    private readonly byte[] _region = new byte[Px];
    private readonly float[] _lr = new float[LutSize * LutSize], _lg = new float[LutSize * LutSize], _lb = new float[LutSize * LutSize];
    private readonly int[] _clipN = new int[Panels];
    private SetCameraPose _pose;
    private Vector3 _ground, _accent;
    private int _style;

    public RealtimeVirtualSetExtensionEngine()
    {
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var v = Vector3.Normalize(new Vector3((x + 0.5f - 0.5f * W) / FocalPx, (0.5f * H - y - 0.5f) / FocalPx, 1f));
                var i = y * W + x;
                _cx[i] = v.X; _cy[i] = v.Y; _cz[i] = v.Z;
            }
        StyleIndex = 0;
    }

    public bool UseSimd { get; set; } = true;
    /// <summary>v17.55 확장 텍스처 외부 장면(3DGS 등) — 커버리지만큼 LUT 월드 위에 합성.</summary>
    public PromptFlowStudio.Services.GenlockFrustum.IDirectionalSceneSampler? ExtensionScene { get; set; }
    public long ExtensionSceneHits { get; private set; }
    /// <summary>v17.27 월드 스타일(숲 · 사막 · 설원 · 화산 · 도시).</summary>
    public int StyleIndex
    {
        get => _style;
        set
        {
            _style = Math.Clamp(value, 0, GenerativeNeuralWorldExpansionEngine.Styles.Length - 1);
            _ground = GenerativeNeuralWorldExpansionEngine.Styles[_style].Ground;
            _accent = GenerativeNeuralWorldExpansionEngine.Styles[_style].Accent;
            BakeLut();
        }
    }
    public string StyleName => GenerativeNeuralWorldExpansionEngine.Styles[_style].Name;
    public ref readonly SetCameraPose Pose => ref _pose;
    public ReadOnlySpan<float> DirX => _dx;
    public ReadOnlySpan<float> DirY => _dy;
    public ReadOnlySpan<float> DirZ => _dz;
    public ReadOnlySpan<float> TrueR => _tr;
    public ReadOnlySpan<float> TrueG => _tg;
    public ReadOnlySpan<float> TrueB => _tb;
    public ReadOnlySpan<float> ExtR => _xr;
    public ReadOnlySpan<float> ExtG => _xg;
    public ReadOnlySpan<float> ExtB => _xb;
    public ReadOnlySpan<float> Dist => _dist;
    public ReadOnlySpan<byte> Regions => _region;
    public SetRegion Region(int i) => (SetRegion)_region[i];
    public int ClipCount(int panel) => _clipN[panel];
    public Vector2 ClipVertex(int panel, int k) { var o = (panel * MaxClip + k) * 2; return new(_clip[o], _clip[o + 1]); }
    public int VisiblePanels { get; private set; }
    public float LedCoverage { get; private set; }
    public float OverrunPct => 100f * (1f - LedCoverage);
    public float PixelLedFraction { get; private set; }
    public float CeilingFraction { get; private set; }
    public float FloorFraction { get; private set; }
    public float OpenFraction { get; private set; }
    public float MaxDist { get; private set; }
    public double LastComputeUs { get; private set; }
    public double LastExtUs { get; private set; }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Compute(in SetCameraPose pose)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _pose = pose;
        pose.Basis(out var f, out var r, out var u);
        Rotate(f, r, u, _dx, _dy, _dz);
        Classify(pose.Position);
        ClipPanels(pose.Position, f, r, u);
        DistanceField();
        Shade(_dx, _dy, _dz, _tr, _tg, _tb, true);
        LastComputeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>세트 확장 레이어 — 확장 렌더러가 받은(지연/예측) 포즈로 전 화면 가상 월드 투영.</summary>
    public void RenderExtension(in SetCameraPose extPose)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        extPose.Basis(out var f, out var r, out var u);
        Rotate(f, r, u, _ex, _ey, _ez);
        Shade(_ex, _ey, _ez, _xr, _xg, _xb, false);
        if (ExtensionScene is { } scene)
        {
            var eye = extPose.Position;
            for (var i = 0; i < Px; i++)
            {
                var cov = scene.Sample(eye, new Vector3(_ex[i], _ey[i], _ez[i]), out var c);
                if (!(cov > 0f)) continue;
                var k = 1f - cov;
                _xr[i] = c.X + _xr[i] * k; _xg[i] = c.Y + _xg[i] * k; _xb[i] = c.Z + _xb[i] * k;
                ExtensionSceneHits++;
            }
        }
        LastExtUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>d = right·cx + up·cy + fwd·cz (곱·합 순서 고정 — SIMD/스칼라 비트 동일).</summary>
    private void Rotate(Vector3 f, Vector3 r, Vector3 u, Span<float> ox, Span<float> oy, Span<float> oz)
    {
        var i = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var n = Vector<float>.Count;
            Vector<float> rx = new(r.X), ry = new(r.Y), rz = new(r.Z), ux = new(u.X), uy = new(u.Y), uz = new(u.Z), fx = new(f.X), fy = new(f.Y), fz = new(f.Z);
            ReadOnlySpan<float> cxs = _cx, cys = _cy, czs = _cz;
            for (; i <= Px - n; i += n)
            {
                var a = new Vector<float>(cxs.Slice(i, n)); var b = new Vector<float>(cys.Slice(i, n)); var c = new Vector<float>(czs.Slice(i, n));
                (rx * a + ux * b + fx * c).CopyTo(ox.Slice(i, n));
                (ry * a + uy * b + fy * c).CopyTo(oy.Slice(i, n));
                (rz * a + uz * b + fz * c).CopyTo(oz.Slice(i, n));
            }
        }
        for (; i < Px; i++)
        {
            float a = _cx[i], b = _cy[i], c = _cz[i];
            ox[i] = r.X * a + u.X * b + f.X * c;
            oy[i] = r.Y * a + u.Y * b + f.Y * c;
            oz[i] = r.Z * a + u.Z * b + f.Z * c;
        }
    }

    /// <summary>원통(R 6 m) 교차 → 높이/호 범위로 LED · 천장 · 바닥 · 측면 크로마 분류.</summary>
    private void Classify(Vector3 o)
    {
        const float R = RealtimeOffAxisFrustumProjectionEngine.RadiusM, Hw = RealtimeOffAxisFrustumProjectionEngine.WallHeightM;
        var cosHalf = R * MathF.Cos(RealtimeOffAxisFrustumProjectionEngine.HalfArcRad);
        var c = o.X * o.X + o.Z * o.Z - R * R;
        int led = 0, ceil = 0, floor = 0, open = 0;
        for (var i = 0; i < Px; i++)
        {
            float dx = _dx[i], dy = _dy[i], dz = _dz[i];
            var a = dx * dx + dz * dz;
            SetRegion k;
            if (a < 1e-9f) k = dy > 0f ? SetRegion.Ceiling : SetRegion.Floor;
            else
            {
                var b = o.X * dx + o.Z * dz;
                var disc = b * b - a * c;
                var t = (-b + MathF.Sqrt(MathF.Max(disc, 0f))) / a;
                var hy = o.Y + t * dy;
                if (hy > Hw) k = SetRegion.Ceiling;
                else if (hy < 0f) k = SetRegion.Floor;
                else
                {
                    // |θ| ≤ 120° ⇔ cos θ = −z/R ≥ cos(120°)
                    k = -(o.Z + t * dz) >= cosHalf ? SetRegion.Led : SetRegion.Open;
                }
            }
            _region[i] = (byte)k;
            switch (k) { case SetRegion.Led: led++; break; case SetRegion.Ceiling: ceil++; break; case SetRegion.Floor: floor++; break; default: open++; break; }
        }
        PixelLedFraction = led / (float)Px; CeilingFraction = ceil / (float)Px; FloorFraction = floor / (float)Px; OpenFraction = open / (float)Px;
    }

    /// <summary>패널 사각형 → 카메라 공간 → 근/좌/우/하/상 Sutherland-Hodgman → 픽셀 투영 · 신발끈 면적.</summary>
    private void ClipPanels(Vector3 o, Vector3 f, Vector3 r, Vector3 u)
    {
        Span<Vector3> a = stackalloc Vector3[MaxClip];
        Span<Vector3> b = stackalloc Vector3[MaxClip];
        var step = RealtimeOffAxisFrustumProjectionEngine.ColStepRad;
        var half = RealtimeOffAxisFrustumProjectionEngine.HalfArcRad;
        const float Ph = RealtimeOffAxisFrustumProjectionEngine.PanelHeightM;
        var area = 0f;
        var vis = 0;
        for (var p = 0; p < Panels; p++)
        {
            var row = p / RealtimeOffAxisFrustumProjectionEngine.Cols; var col = p % RealtimeOffAxisFrustumProjectionEngine.Cols;
            float t0 = -half + col * step, t1 = t0 + step, y0 = row * Ph, y1 = y0 + Ph;
            a[0] = ToCam(RealtimeOffAxisFrustumProjectionEngine.WallPoint(t0, y0) - o, f, r, u);
            a[1] = ToCam(RealtimeOffAxisFrustumProjectionEngine.WallPoint(t1, y0) - o, f, r, u);
            a[2] = ToCam(RealtimeOffAxisFrustumProjectionEngine.WallPoint(t1, y1) - o, f, r, u);
            a[3] = ToCam(RealtimeOffAxisFrustumProjectionEngine.WallPoint(t0, y1) - o, f, r, u);
            var n = 4;
            n = ClipPolygon(a, n, new Vector4(0f, 0f, 1f, -NearM), b);
            n = ClipPolygon(b, n, new Vector4(1f, 0f, TanH, 0f), a);
            n = ClipPolygon(a, n, new Vector4(-1f, 0f, TanH, 0f), b);
            n = ClipPolygon(b, n, new Vector4(0f, 1f, TanV, 0f), a);
            n = ClipPolygon(a, n, new Vector4(0f, -1f, TanV, 0f), b);
            _clipN[p] = n;
            if (n < 3) { _clipN[p] = 0; continue; }
            var s = 0f;
            var o2 = p * MaxClip * 2;
            for (var k = 0; k < n; k++)
            {
                var v = b[k];
                _clip[o2 + 2 * k] = 0.5f * W + v.X / v.Z * FocalPx;
                _clip[o2 + 2 * k + 1] = 0.5f * H - v.Y / v.Z * FocalPx;
            }
            for (var k = 0; k < n; k++)
            {
                var j = (k + 1) % n;
                s += _clip[o2 + 2 * k] * _clip[o2 + 2 * j + 1] - _clip[o2 + 2 * j] * _clip[o2 + 2 * k + 1];
            }
            area += 0.5f * MathF.Abs(s);
            vis++;
        }
        VisiblePanels = vis;
        LedCoverage = Math.Clamp(area / (W * H), 0f, 1f);
    }

    private static Vector3 ToCam(Vector3 d, Vector3 f, Vector3 r, Vector3 u) => new(Vector3.Dot(d, r), Vector3.Dot(d, u), Vector3.Dot(d, f));

    /// <summary>Sutherland-Hodgman 단일 평면 클리핑(내부: n·v + w ≥ 0). 반환 정점 수(≤ MaxClip).</summary>
    public static int ClipPolygon(ReadOnlySpan<Vector3> src, int n, Vector4 plane, Span<Vector3> dst)
    {
        if (n == 0) return 0;
        var m = 0;
        var pn = new Vector3(plane.X, plane.Y, plane.Z);
        var prev = src[n - 1];
        var dp = Vector3.Dot(pn, prev) + plane.W;
        for (var k = 0; k < n; k++)
        {
            var cur = src[k];
            var dc = Vector3.Dot(pn, cur) + plane.W;
            if (dc >= 0f)
            {
                if (dp < 0f && m < dst.Length) dst[m++] = Vector3.Lerp(prev, cur, dp / (dp - dc));
                if (m < dst.Length) dst[m++] = cur;
            }
            else if (dp >= 0f && m < dst.Length) dst[m++] = Vector3.Lerp(prev, cur, dp / (dp - dc));
            prev = cur; dp = dc;
        }
        return m;
    }

    /// <summary>LED 픽셀 → 최근접 비 LED 픽셀 거리(체임퍼 1 / √2 2-패스).</summary>
    private void DistanceField()
    {
        const float Inf = 1e6f, D = 1.41421356f;
        for (var i = 0; i < Px; i++) _dist[i] = _region[i] == (byte)SetRegion.Led ? Inf : 0f;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                var v = _dist[i];
                if (v == 0f) continue;
                if (x > 0) v = MathF.Min(v, _dist[i - 1] + 1f);
                if (y > 0)
                {
                    v = MathF.Min(v, _dist[i - W] + 1f);
                    if (x > 0) v = MathF.Min(v, _dist[i - W - 1] + D);
                    if (x < W - 1) v = MathF.Min(v, _dist[i - W + 1] + D);
                }
                _dist[i] = v;
            }
        var mx = 0f;
        for (var y = H - 1; y >= 0; y--)
            for (var x = W - 1; x >= 0; x--)
            {
                var i = y * W + x;
                var v = _dist[i];
                if (v == 0f) continue;
                if (x < W - 1) v = MathF.Min(v, _dist[i + 1] + 1f);
                if (y < H - 1)
                {
                    v = MathF.Min(v, _dist[i + W] + 1f);
                    if (x < W - 1) v = MathF.Min(v, _dist[i + W + 1] + D);
                    if (x > 0) v = MathF.Min(v, _dist[i + W - 1] + D);
                }
                _dist[i] = v;
                if (v < Inf && v > mx) mx = v;
            }
        MaxDist = mx;
    }

    /// <summary>옥타헤드럴 LUT(512², 스타일 변경 시 1 회 베이크) 쌍선형 표본화 — 픽셀당 삼각함수 0.</summary>
    /// <param name="ledOnly">실제 월드: LED 픽셀 + 4-이웃 경계(이음새 기준)만 — 크로마 내부는 0.</param>
    private void Shade(ReadOnlySpan<float> dx, ReadOnlySpan<float> dy, ReadOnlySpan<float> dz, Span<float> or, Span<float> og, Span<float> ob, bool ledOnly)
    {
        const int L = LutSize;
        const float Hl = 0.5f * (L - 1);
        float[] lr = _lr, lg = _lg, lb = _lb;
        for (var i = 0; i < Px; i++)
        {
            if (ledOnly && _region[i] != (byte)SetRegion.Led && !NearLed(i)) { or[i] = 0f; og[i] = 0f; ob[i] = 0f; continue; }
            float x = dx[i], y = dy[i], z = dz[i];
            var inv = 1f / (MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z));
            float u = x * inv, v = z * inv;
            if (y < 0f)
            {
                var ou = (1f - MathF.Abs(v)) * (u >= 0f ? 1f : -1f);
                v = (1f - MathF.Abs(u)) * (v >= 0f ? 1f : -1f);
                u = ou;
            }
            var fx = (u + 1f) * Hl; var fy = (v + 1f) * Hl;
            var x0 = Math.Min((int)fx, L - 2); var y0 = Math.Min((int)fy, L - 2);
            float ax = fx - x0, ay = fy - y0;
            int a = y0 * L + x0, b = a + L;
            float w00 = (1f - ax) * (1f - ay), w10 = ax * (1f - ay), w01 = (1f - ax) * ay, w11 = ax * ay;
            or[i] = lr[a] * w00 + lr[a + 1] * w10 + lr[b] * w01 + lr[b + 1] * w11;
            og[i] = lg[a] * w00 + lg[a + 1] * w10 + lg[b] * w01 + lg[b + 1] * w11;
            ob[i] = lb[a] * w00 + lb[a + 1] * w10 + lb[b] * w01 + lb[b + 1] * w11;
        }
    }

    private bool NearLed(int i)
    {
        int x = i % W, y = i / W;
        const byte Led = (byte)SetRegion.Led;
        return (x > 0 && _region[i - 1] == Led) || (x < W - 1 && _region[i + 1] == Led) || (y > 0 && _region[i - W] == Led) || (y < H - 1 && _region[i + W] == Led);
    }

    private void BakeLut()
    {
        const int L = LutSize;
        for (var j = 0; j < L; j++)
            for (var i = 0; i < L; i++)
            {
                float u = 2f * i / (L - 1) - 1f, v = 2f * j / (L - 1) - 1f;
                var y = 1f - MathF.Abs(u) - MathF.Abs(v);
                float x = u, z = v;
                if (y < 0f)
                {
                    x = (1f - MathF.Abs(v)) * (u >= 0f ? 1f : -1f);
                    z = (1f - MathF.Abs(u)) * (v >= 0f ? 1f : -1f);
                }
                var c = World(Vector3.Normalize(new Vector3(x, y, z)));
                var k = j * L + i;
                _lr[k] = c.X; _lg[k] = c.Y; _lb[k] = c.Z;
            }
    }

    /// <summary>방향 전용 가상 월드(무한 원거리 — 위치 시차 0 · 1 px 각폭 안티에일리어싱): 하늘 · 태양 · 산맥 실루엣 · 지면 그리드 · 대기 원근.</summary>
    public Vector3 World(Vector3 d)
    {
        var horizon = new Vector3(0.62f, 0.72f, 0.86f);
        var aa = 1.5f / FocalPx;
        if (d.Y >= 0f)
        {
            var az = MathF.Atan2(d.X, -d.Z);
            var ridge = 0.07f + 0.04f * MathF.Sin(3f * az + 0.4f) + 0.025f * MathF.Sin(7f * az + 1.3f) + 0.012f * MathF.Sin(17f * az);
            var sky = Vector3.Lerp(horizon, new Vector3(0.18f, 0.36f, 0.78f), MathF.Sqrt(d.Y));
            var s = MathF.Max(Vector3.Dot(d, Sun), 0f);
            var s2 = s * s; var s4 = s2 * s2; var s8 = s4 * s4; var s16 = s8 * s8;
            sky += new Vector3(1f, 0.92f, 0.75f) * (0.9f * s16 * s16 + 0.15f * s8);
            var k = Math.Clamp(d.Y / ridge, 0f, 1f);
            var rock = Vector3.Lerp(horizon, Vector3.Lerp(_accent, horizon, 0.35f * (1f - k)) * (0.85f + 0.15f * MathF.Sin(11f * az + 9f * d.Y)), Smooth(0f, 0.03f, d.Y));
            return Vector3.Lerp(rock, sky, Smooth(ridge - aa, ridge + aa, d.Y));
        }
        var t = GroundDepthM / -d.Y;
        float gx = d.X * t, gz = d.Z * t;
        var cx = gx * 0.5f - MathF.Floor(gx * 0.5f); var cz = gz * 0.5f - MathF.Floor(gz * 0.5f);
        var e = MathF.Min(MathF.Min(cx, 1f - cx), MathF.Min(cz, 1f - cz));
        var pw = MathF.Min(0.25f, 0.5f * t * aa / MathF.Max(-d.Y, 0.02f));
        var line = 1f - 0.3f * (1f - Smooth(0.06f - pw, 0.06f + pw, e));
        var g = _ground * line;
        return Vector3.Lerp(horizon, g, 1f - MathF.Exp(d.Y * 12f));
    }

    private static float Smooth(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
