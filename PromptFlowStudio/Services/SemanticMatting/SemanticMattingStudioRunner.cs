using System.Numerics;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.SemanticMatting;

/// <summary>
/// [PromptFlow Studio v17.08.0] 실사 스튜디오 플레이트 시뮬레이터(그린스크린 없음 · 결정적).
/// 일반 실내 배경(줄무늬 벽지 · 책장 · 창문 · 마룻바닥) 앞에서 좌우로 흔들리는 인물(머리 · 머리카락 덩어리 + 서브픽셀 가닥 ·
/// 셔츠/팔/다리) + 손에 든 반투명 유리(α 0.3 · 시안 틴트). 정답 α · 전경색 · 클린 플레이트 보유.
/// 세그멘테이션 네트워크 시뮬레이션 = 4×4 블록 평균 → 이진화 → 쌍선형 확대 + 프레임별 ±1 px 지터(가닥 미검출) · 클래스 맵.
/// </summary>
public sealed class StudioPlateSimulator
{
    public const int Strands = 24;
    public static readonly Vector3 GlassTint = new(0.5f, 0.82f, 0.92f);
    public const float GlassAlphaTrue = 0.3f;
    private static readonly Vector3 Skin = new(0.86f, 0.64f, 0.5f), HairCol = new(0.2f, 0.13f, 0.08f), Shirt = new(0.78f, 0.2f, 0.26f), Pants = new(0.16f, 0.2f, 0.38f);

    private readonly int _w, _h, _n;
    private readonly float[] _pr, _pg, _pb, _ir, _ig, _ib, _ta, _tr, _tg, _tb, _obj, _prior, _low;
    private readonly byte[] _cls;
    private readonly float[] _sx = new float[Strands * 5], _sy = new float[Strands * 5];
    private ulong _rng = 0x9E37_79B9_7F4A_7C15UL;

    public StudioPlateSimulator(int width = RealtimeSemanticAlphaMattingEngine.DefaultWidth, int height = RealtimeSemanticAlphaMattingEngine.DefaultHeight)
    {
        _w = width; _h = height; _n = width * height;
        _pr = new float[_n]; _pg = new float[_n]; _pb = new float[_n];
        _ir = new float[_n]; _ig = new float[_n]; _ib = new float[_n];
        _ta = new float[_n]; _tr = new float[_n]; _tg = new float[_n]; _tb = new float[_n];
        _obj = new float[_n]; _prior = new float[_n]; _low = new float[(width / 4) * (height / 4)];
        _cls = new byte[_n];
    }

    public float Time { get; private set; }
    public float SubjectX { get; private set; }
    public float Noise { get; set; } = 0.006f;
    /// <summary>실조명 세기(운영자 감광 주입 → 클린 플레이트 적응 검증).</summary>
    public float Light { get; set; } = 1f;
    public bool Sway { get; set; } = true;
    public bool SubjectPresent { get; set; } = true;
    public ReadOnlySpan<float> R => _ir;
    public ReadOnlySpan<float> G => _ig;
    public ReadOnlySpan<float> B => _ib;
    public ReadOnlySpan<float> TrueAlpha => _ta;
    public ReadOnlySpan<float> TrueR => _tr;
    public ReadOnlySpan<float> TrueG => _tg;
    public ReadOnlySpan<float> TrueB => _tb;
    public ReadOnlySpan<float> Prior => _prior;
    public ReadOnlySpan<byte> Classes => _cls;

    private float Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 40) / 16777216f * 2f - 1f; }

    private Vector3 Plate(int x, int y)
    {
        Vector3 c;
        if (y >= 70)
        {   // 마룻바닥
            var plank = ((y - 70) / 5 & 1) == 0 ? 1f : 0.88f;
            c = new Vector3(0.46f, 0.33f, 0.21f) * plank * (0.92f + 0.08f * MathF.Sin(x * 0.7f + y * 1.3f));
        }
        else if (x >= 8 && x < 40 && y >= 18 && y < 62)
        {   // 책장
            var shelf = (y - 18) % 11 == 0;
            var book = (x * 7 / 3) % 5;
            c = shelf ? new Vector3(0.3f, 0.2f, 0.12f) : book switch { 0 => new Vector3(0.6f, 0.15f, 0.12f), 1 => new Vector3(0.15f, 0.35f, 0.5f), 2 => new Vector3(0.8f, 0.7f, 0.3f), 3 => new Vector3(0.2f, 0.45f, 0.25f), _ => new Vector3(0.12f, 0.1f, 0.1f) };
        }
        else if (x >= 122 && x < 152 && y >= 8 && y < 40)
            c = (x - 122) % 15 == 0 || (y - 8) % 16 == 0 ? new Vector3(0.9f, 0.9f, 0.88f) : new Vector3(0.82f, 0.9f, 0.98f);
        else
        {   // 줄무늬 벽지 · 몰딩
            var stripe = 0.07f * MathF.Sin(x * (2f * MathF.PI / 7f));
            c = y is >= 58 and < 61 ? new Vector3(0.92f, 0.9f, 0.86f) : new Vector3(0.8f + stripe, 0.75f + stripe, 0.64f + stripe * 0.6f);
        }
        return c;
    }

    private static float SdBox(float px, float py, float cx, float cy, float hx, float hy, float r)
    {
        float qx = MathF.Abs(px - cx) - hx + r, qy = MathF.Abs(py - cy) - hy + r;
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    private static float SegDist(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy + 1e-6f), 0f, 1f);
        float ex = px - ax - t * dx, ey = py - ay - t * dy;
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    public float HeadX => SubjectX;
    public const float HeadY = 21f, HeadR = 8f;

    /// <summary>1프레임 합성(무할당).</summary>
    public void Render(float dt)
    {
        Time += dt;
        var cx = SubjectX = Sway ? 72f + 16f * MathF.Sin(0.35f * Time) : 72f;
        for (var k = 0; k < Strands; k++)
        {   // 머리카락 가닥: 머리 상단 반원에서 바깥으로 3~5.5 px · 시간 흔들림.
            var th = (-160f + 140f * k / (Strands - 1)) * MathF.PI / 180f;
            var len = 3f + 2.5f * (0.5f + 0.5f * MathF.Sin(k * 2.3f));
            float ox = cx + MathF.Cos(th) * (HeadR - 0.8f), oy = HeadY + MathF.Sin(th) * (HeadR - 0.8f);
            for (var s = 0; s < 5; s++)
            {
                var f = s / 4f;
                var bend = MathF.Sin(Time * 2.1f + k) * 0.9f * f * f;
                _sx[k * 5 + s] = ox + MathF.Cos(th) * len * f + bend;
                _sy[k * 5 + s] = oy + MathF.Sin(th) * len * f;
            }
        }
        var lt = Light * (1f + 0.004f * MathF.Sin(7f * Time));
        float gx0 = cx + 19f, gx1 = cx + 27f, gy0 = 32f, gy1 = 48f;
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                var i = y * _w + x;
                float px = x + 0.5f, py = y + 0.5f;
                var bg = Plate(x, y) * lt;
                _pr[i] = bg.X; _pg[i] = bg.Y; _pb[i] = bg.Z;
                float a = 0f; var f = Vector3.Zero; float obj = 0f; byte cls = 0;
                if (SubjectPresent)
                {
                    var dHead = MathF.Sqrt((px - cx) * (px - cx) + (py - HeadY) * (py - HeadY)) - HeadR;
                    var d = dHead; var col = py < HeadY - 2f ? HairCol : Skin;
                    var dn = SdBox(px, py, cx, 30f, 3f, 2.5f, 0.5f); if (dn < d) { d = dn; col = Skin; }
                    var dt2 = SdBox(px, py, cx, 44f, 12f, 14f, 4f); if (dt2 < d) { d = dt2; col = Shirt * (0.85f + 0.15f * (py - 30f) / 28f); }
                    var dl = SdBox(px, py, cx - 15f, 44f, 3f, 12f, 2f); if (dl < d) { d = dl; col = Shirt * 0.9f; }
                    var dr = SdBox(px, py, cx + 15f, 44f, 3f, 12f, 2f); if (dr < d) { d = dr; col = Shirt * 0.9f; }
                    var dg1 = SdBox(px, py, cx - 5f, 74f, 4f, 15f, 1f); if (dg1 < d) { d = dg1; col = Pants; }
                    var dg2 = SdBox(px, py, cx + 5f, 74f, 4f, 15f, 1f); if (dg2 < d) { d = dg2; col = Pants; }
                    a = Math.Clamp(0.5f - d, 0f, 1f);
                    f = col;
                    obj = a;
                    if (a < 1f && py < HeadY && MathF.Abs(px - cx) < HeadR + 7f)
                    {
                        var ds = 9f;
                        for (var k = 0; k < Strands; k++)
                            for (var s = 0; s < 4; s++)
                                ds = MathF.Min(ds, SegDist(px, py, _sx[k * 5 + s], _sy[k * 5 + s], _sx[k * 5 + s + 1], _sy[k * 5 + s + 1]));
                        var ah = 0.85f * Math.Clamp(0.7f - ds, 0f, 1f);
                        if (ah > a) { if (a <= 0f) f = HairCol; else f = (f * a + HairCol * (ah - a)) / ah; a = ah; }
                    }
                    if (px >= gx0 && px <= gx1 && py >= gy0 && py <= gy1 && a <= 0f) { a = GlassAlphaTrue; f = GlassTint; obj = 1f; }
                    var near = (px - cx) * (px - cx) + (py - HeadY) * (py - HeadY) < (HeadR + 7f) * (HeadR + 7f) && py < HeadY + 1f;
                    cls = px >= gx0 + 1 && px <= gx1 - 1 && py >= gy0 + 1 && py <= gy1 - 1 ? (byte)MatteClass.Transparent : near ? (byte)MatteClass.Hair : d < 3f ? (byte)MatteClass.Person : (byte)0;
                }
                var fl = f * lt;
                _ta[i] = a; _tr[i] = fl.X; _tg[i] = fl.Y; _tb[i] = fl.Z;
                _obj[i] = obj; _cls[i] = cls;
                var c = fl * a + bg * (1f - a);
                _ir[i] = Math.Clamp(c.X + Rand() * Noise, 0f, 1f); _ig[i] = Math.Clamp(c.Y + Rand() * Noise, 0f, 1f); _ib[i] = Math.Clamp(c.Z + Rand() * Noise, 0f, 1f);
            }
        Segment();
    }

    /// <summary>세그멘테이션 네트워크 시뮬레이션(저해상 이진 마스크 → 쌍선형 확대 → 프레임별 지터).</summary>
    private void Segment()
    {
        int lw = _w / 4, lh = _h / 4;
        for (var by = 0; by < lh; by++)
            for (var bx = 0; bx < lw; bx++)
            {
                float s = 0;
                for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++) s += _obj[(by * 4 + y) * _w + bx * 4 + x];
                _low[by * lw + bx] = s >= 8f ? 1f : 0f;
            }
        int jx = (int)MathF.Round(Rand()), jy = (int)MathF.Round(Rand());
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                float u = (x - jx + 0.5f) / 4f - 0.5f, v = (y - jy + 0.5f) / 4f - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(u), 0, lw - 1), y0 = Math.Clamp((int)MathF.Floor(v), 0, lh - 1);
                int x1 = Math.Min(lw - 1, x0 + 1), y1 = Math.Min(lh - 1, y0 + 1);
                float fx = Math.Clamp(u - x0, 0f, 1f), fy = Math.Clamp(v - y0, 0f, 1f);
                var top = _low[y0 * lw + x0] + fx * (_low[y0 * lw + x1] - _low[y0 * lw + x0]);
                var bot = _low[y1 * lw + x0] + fx * (_low[y1 * lw + x1] - _low[y1 * lw + x0]);
                var p = top + fy * (bot - top);
                var i = y * _w + x;
                _prior[i] = p;
                if (_cls[i] == 0 && p >= 0.5f) _cls[i] = (byte)MatteClass.Person;
            }
    }

    /// <summary>피사체 없는 플레이트(클린 플레이트 취득용) 렌더.</summary>
    public void RenderEmpty()
    {
        var on = SubjectPresent; SubjectPresent = false;
        Render(0f);
        SubjectPresent = on;
    }
}

/// <summary>
/// [PromptFlow Studio v17.08.0] 🎭 시맨틱 매팅 &amp; 가상 세트 융합 스튜디오 러너(30 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 실사 플레이트 → 시맨틱 알파 매팅 → SLAM 바닥 앵커 · OpenUSD 가상 세트 Z-Depth 합성(같은 프레임) → HDRI 리라이팅 · 라이트 랩 → 정답 대비 오차.
/// </summary>
public sealed class SemanticMattingStudioRunner : IDisposable
{
    public const int Hz = 30, SeriesLen = 240, ScanPoints = 5000;
    public const float CamHeight = 0.9f;

    private readonly object _gate = new();
    private readonly float[] _err = new float[SeriesLen], _perr = new float[SeriesLen], _mis = new float[SeriesLen], _ms = new float[SeriesLen];
    private readonly float[] _sx = new float[ScanPoints], _sy = new float[ScanPoints], _sz = new float[ScanPoints], _snx = new float[ScanPoints], _sny = new float[ScanPoints], _snz = new float[ScanPoints];
    private readonly byte[] _bmp, _hdriBmp;
    private int _seriesHead, _seriesCount, _orb;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick, _frame;
    private ulong _rng = 0xD1B5_4A32_D192_ED03UL;

    public SemanticMattingStudioRunner(IServiceProvider sp)
        : this((RealtimeSemanticAlphaMattingEngine?)sp.GetService(typeof(RealtimeSemanticAlphaMattingEngine)) ?? new RealtimeSemanticAlphaMattingEngine(),
               (GenerativeVirtualSetZDepthSynthesizer?)sp.GetService(typeof(GenerativeVirtualSetZDepthSynthesizer)) ?? new GenerativeVirtualSetZDepthSynthesizer(),
               (DynamicEnvironmentRelightingGuard?)sp.GetService(typeof(DynamicEnvironmentRelightingGuard)) ?? new DynamicEnvironmentRelightingGuard())
    {
    }

    public SemanticMattingStudioRunner(RealtimeSemanticAlphaMattingEngine matting, GenerativeVirtualSetZDepthSynthesizer set, DynamicEnvironmentRelightingGuard light)
    {
        Matting = matting; Set = set; Light = light;
        Sim = new StudioPlateSimulator(matting.Width, matting.Height);
        Planes = new SpatialPlaneDetectionAnchorGuard();
        _bmp = new byte[54 + matting.Width * matting.Height * 3];
        _hdriBmp = new byte[54 + HdriEnvironment.W * HdriEnvironment.H * 3];
        ScanFloor();
        var key = Set.KeyIntensityFromUsd();
        if (!float.IsNaN(key)) Light.Env.SetKey(Light.Env.KeyAzimuth, Light.Env.KeyElevation, key, Light.Env.KeyKelvin);
        Light.SnapEnvironment();
        _orb = Set.FindObject("Orb");
        Sim.RenderEmpty();
        Matting.CapturePlate(Sim.R, Sim.G, Sim.B);
        LastAction = "📋 대기 · 클린 플레이트 취득 · USD 프림 " + Set.ObjectCount + " · 바닥 " + (FloorPlane >= 0 ? "SLAM 검출" : "기본");
    }

    public event Action? OnChange;

    public RealtimeSemanticAlphaMattingEngine Matting { get; }
    public GenerativeVirtualSetZDepthSynthesizer Set { get; }
    public DynamicEnvironmentRelightingGuard Light { get; }
    public StudioPlateSimulator Sim { get; }
    public SpatialPlaneDetectionAnchorGuard Planes { get; }
    public int FloorPlane { get; private set; } = -1;
    public float FloorTiltDeg { get; set; } = 0.4f;
    public bool OrbitOrb { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public long Frame => _frame;
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>정답 대비 평균 |α − α*|(전체) · 경계 대역(미지/반투명/정답 분수) 엔진 vs prior · 머리카락 클래스 · 전경색 오차.</summary>
    public float AlphaError { get; private set; }
    public float EdgeError { get; private set; }
    public float PriorEdgeError { get; private set; }
    public float HairError { get; private set; }
    public float PriorHairError { get; private set; }
    public float ForegroundError { get; private set; }
    public float GlassAlphaError { get; private set; }
    public float GlassTintError { get; private set; }
    /// <summary>피사체/세트 노출 불일치(stops) — 원본 전경 vs 리라이팅 후.</summary>
    public float RawMismatch { get; private set; }
    public float Mismatch { get; private set; }
    public int SeriesCount => _seriesCount;
    public float ErrorSeries(int i) => _err[Idx(i)];
    public float PriorSeries(int i) => _perr[Idx(i)];
    public float MismatchSeries(int i) => _mis[Idx(i)];
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    private float Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 40) / 16777216f * 2f - 1f; }

    /// <summary>실 스튜디오 바닥 LiDAR 스캔(경사 · 잡음 · 벽 클러터) → v16.98 SLAM RANSAC 바닥 → 가상 세트 바닥/프림 정렬.</summary>
    public void ScanFloor()
    {
        lock (_gate)
        {
            var t = FloorTiltDeg * MathF.PI / 180f;
            var n = Vector3.Normalize(new Vector3(0f, MathF.Cos(t), MathF.Sin(t)));
            var d = -Vector3.Dot(n, new Vector3(0f, -CamHeight, 3f));
            for (var i = 0; i < ScanPoints; i++)
            {
                float x, y, z, nx, ny, nz;
                if (i % 8 == 7) { z = 6f; x = Noise() * 3f; y = -CamHeight + 0.2f + (Noise() * 0.5f + 0.5f) * 2f; nx = 0; ny = 0; nz = -1; }
                else
                {
                    x = Noise() * 3f; z = 1f + (Noise() * 0.5f + 0.5f) * 5f;
                    y = -(n.X * x + n.Z * z + d) / n.Y + Noise() * 0.006f;
                    nx = n.X + Noise() * 0.02f; ny = n.Y; nz = n.Z + Noise() * 0.02f;
                }
                _sx[i] = x; _sy[i] = y; _sz[i] = z; _snx[i] = nx; _sny[i] = ny; _snz[i] = nz;
            }
            Planes.MinArea = 1f;
            Planes.Detect(_sx, _sy, _sz, _snx, _sny, _snz);
            FloorPlane = Planes.FindKind(PlaneKind.Floor);
            if (FloorPlane >= 0) { ref readonly var p = ref Planes.Plane(FloorPlane); Set.SetFloor(p.Normal, p.D); }
        }
    }

    // ── 운영자 조작 ───────────────────────────────────────

    public void SetPreset(HdriPreset p) { lock (_gate) Light.Env.SetPreset(p); LastAction = "🌐 HDRI = " + p + " · SH9 슬루 전환"; Raise(); }
    public void SetKey(float az, float el, float intensity, float kelvin) { lock (_gate) Light.Env.SetKey(az, el, intensity, kelvin); Raise(); }
    public void SetDepthOffset(float m) { lock (_gate) Set.DepthOffset = Math.Clamp(m, -2.5f, 5f); Raise(); }
    public void ToggleObject(int k)
    {
        lock (_gate) Set.SetObjectVisible(k, !Set.ObjectVisible(k));
        LastAction = (Set.ObjectVisible(k) ? "👁 " : "🚫 ") + Set.ObjectName(k);
        Raise();
    }

    public void CapturePlate()
    {
        lock (_gate) { Sim.RenderEmpty(); Matting.CapturePlate(Sim.R, Sim.G, Sim.B); }
        LastAction = "📸 클린 플레이트 재취득";
        Raise();
    }

    public void DimStudio()
    {
        lock (_gate) Sim.Light = Sim.Light > 0.9f ? 0.82f : 1f;
        LastAction = "💡 실조명 " + (Sim.Light < 1f ? "감광 82% — 플레이트 적응 추적" : "복귀 100%");
        Raise();
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var frame = ++_frame;
            Sim.Render(1f / Hz);
            Matting.Process(Sim.R, Sim.G, Sim.B, Sim.Prior, Sim.Classes);
            Set.PlaceSubject(Matting.Alpha, frame);
            if (OrbitOrb && _orb >= 0 && Set.SubjectFound)
            {
                var a = Sim.Time * 1.1f;
                Set.MoveObject(_orb, Set.Anchor.X + 0.75f * MathF.Cos(a), 1.05f, Set.Anchor.Z + 0.75f * MathF.Sin(a));
            }
            Set.Exposure = Light.Exposure;
            Set.RenderSet(Light.Env, frame);
            Light.Apply(Matting.ForeR, Matting.ForeG, Matting.ForeB, Matting.Alpha);
            Set.Composite(Light.R, Light.G, Light.B, Matting.Alpha);
            Light.LightWrap(Set.OutR, Set.OutG, Set.OutB, Set.Visibility, Set.SetR, Set.SetG, Set.SetB);
            Measure();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _err[_seriesHead] = EdgeError; _perr[_seriesHead] = PriorEdgeError; _mis[_seriesHead] = Mismatch; _ms[_seriesHead] = ms;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    private static float Lum(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    private void Measure()
    {
        var a = Matting.Alpha; var ta = Sim.TrueAlpha; var pr = Sim.Prior; var tri = Matting.Tri; var cls = Sim.Classes;
        double all = 0, edge = 0, pedge = 0, hair = 0, phair = 0, fe = 0, few = 0, sl = 0, rl = 0, bl = 0;
        int ne = 0, nh = 0, ns = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var e = MathF.Abs(a[i] - ta[i]);
            all += e;
            var t = tri[i];
            var band = t == Trimap.Unknown || t == Trimap.Translucent || (ta[i] > 0f && ta[i] < 1f);
            if (band) { edge += e; pedge += MathF.Abs(pr[i] - ta[i]); ne++; }
            if (cls[i] == (byte)MatteClass.Hair && t != Trimap.Fg) { hair += e; phair += MathF.Abs(pr[i] - ta[i]); nh++; }
            if (ta[i] > 0.1f && t == Trimap.Unknown)
            {
                fe += ta[i] * (MathF.Abs(Matting.ForeR[i] - Sim.TrueR[i]) + MathF.Abs(Matting.ForeG[i] - Sim.TrueG[i]) + MathF.Abs(Matting.ForeB[i] - Sim.TrueB[i])) / 3f;
                few += ta[i];
            }
            if (Set.Visibility[i] > 0.95f)
            {
                sl += Lum(Light.R[i], Light.G[i], Light.B[i]);
                rl += Lum(Matting.ForeR[i], Matting.ForeG[i], Matting.ForeB[i]);
                ns++;
            }
            else if (Set.Visibility[i] <= 0f && Set.ObjectId[i] != -2) { bl += Lum(Set.SetR[i], Set.SetG[i], Set.SetB[i]); nb++; }
        }
        AlphaError = (float)(all / a.Length);
        EdgeError = ne == 0 ? 0 : (float)(edge / ne); PriorEdgeError = ne == 0 ? 0 : (float)(pedge / ne);
        HairError = nh == 0 ? 0 : (float)(hair / nh); PriorHairError = nh == 0 ? 0 : (float)(phair / nh);
        ForegroundError = few <= 0 ? 0 : (float)(fe / few);
        GlassAlphaError = Matting.TranslucentValid ? MathF.Abs(Matting.GlassAlpha - StudioPlateSimulator.GlassAlphaTrue) : 1f;
        GlassTintError = Matting.TranslucentValid ? (MathF.Abs(Matting.GlassR - StudioPlateSimulator.GlassTint.X * Sim.Light) + MathF.Abs(Matting.GlassG - StudioPlateSimulator.GlassTint.Y * Sim.Light) + MathF.Abs(Matting.GlassB - StudioPlateSimulator.GlassTint.Z * Sim.Light)) / 3f : 1f;
        if (ns > 0 && nb > 0)
        {
            var setL = Math.Max(1e-4, bl / nb);
            Mismatch = (float)Math.Abs(Math.Log2(Math.Max(1e-4, sl / ns) / setL));
            RawMismatch = (float)Math.Abs(Math.Log2(Math.Max(1e-4, rl / ns) / setL));
        }
    }

    // ── 모니터 ──────────────────────────────────────────────

    private static void Header(Span<byte> d, int w, int h)
    {
        d.Slice(0, 54).Clear();
        d[0] = (byte)'B'; d[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), d.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -h);
        d[26] = 1; d[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), d.Length - 54);
    }

    private static byte B8(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>UI 모니터 BMP(0 합성 · 1 카메라 원본 · 2 알파 매트 · 3 트라이맵 · 4 Z-Depth 오클루전 디버그 · 5 전경 프리멀티(체커) · 6 α 오차 히트맵 · 7 리라이팅 전경).</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            int w = Matting.Width, h = Matting.Height;
            var d = _bmp.AsSpan();
            Header(d, w, h);
            var a = Matting.Alpha; var tri = Matting.Tri;
            for (int p = 0, o = 54; p < w * h; p++, o += 3)
            {
                float r, g, b;
                switch (which)
                {
                    case 1: r = Sim.R[p]; g = Sim.G[p]; b = Sim.B[p]; break;
                    case 2: r = g = b = a[p]; break;
                    case 3:
                        (r, g, b) = tri[p] switch { Trimap.Fg => (1f, 1f, 1f), Trimap.Unknown => (0.95f, 0.55f, 0.1f), Trimap.Translucent => (0.2f, 0.85f, 0.95f), _ => (0f, 0f, 0f) };
                        break;
                    case 4:
                        {
                            var z = Set.SetZ[p];
                            var t = Math.Clamp((z - 1f) / (GenerativeVirtualSetZDepthSynthesizer.WallZ - 1f), 0f, 1f);
                            r = 0.9f - 0.8f * t; g = 0.35f + 0.2f * (1f - MathF.Abs(t - 0.5f) * 2f); b = 0.2f + 0.75f * t;
                            if (a[p] >= 0.5f)
                            {
                                if (Set.Visibility[p] >= 0.5f * a[p]) { r = 0.15f; g = 0.9f; b = 0.35f; } else { r = 1f; g = 0.1f; b = 0.85f; }
                            }
                            break;
                        }
                    case 5:
                        {
                            var chk = ((p % w) / 6 + (p / w) / 6 & 1) == 0 ? 0.35f : 0.55f;
                            var k = a[p];
                            r = Matting.ForeR[p] * k + chk * (1 - k); g = Matting.ForeG[p] * k + chk * (1 - k); b = Matting.ForeB[p] * k + chk * (1 - k);
                            break;
                        }
                    case 6:
                        {
                            var e = Math.Clamp(MathF.Abs(a[p] - Sim.TrueAlpha[p]) * 4f, 0f, 1f);
                            r = e; g = e * 0.3f; b = 0.08f + 0.2f * Sim.TrueAlpha[p];
                            break;
                        }
                    case 7:
                        {
                            var k = a[p];
                            r = Light.R[p] + 0.25f * (1 - k); g = Light.G[p] + 0.25f * (1 - k); b = Light.B[p] + 0.25f * (1 - k);
                            break;
                        }
                    default: r = Set.OutR[p]; g = Set.OutG[p]; b = Set.OutB[p]; break;
                }
                d[o] = B8(b); d[o + 1] = B8(g); d[o + 2] = B8(r);
            }
            return Convert.ToBase64String(_bmp);
        }
    }

    /// <summary>HDRI 등장방형 미리보기(Reinhard 톤매핑).</summary>
    public string HdriBase64()
    {
        lock (_gate)
        {
            var d = _hdriBmp.AsSpan();
            Header(d, HdriEnvironment.W, HdriEnvironment.H);
            var env = Light.Env;
            for (int p = 0, o = 54; p < HdriEnvironment.W * HdriEnvironment.H; p++, o += 3)
            {
                float r = env.Texel(0, p), g = env.Texel(1, p), b = env.Texel(2, p);
                d[o] = B8(b / (1 + b)); d[o + 1] = B8(g / (1 + g)); d[o + 2] = B8(r / (1 + r));
            }
            return Convert.ToBase64String(_hdriBmp);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SemanticMatting" };
            _loop.Start();
            LastAction = "▶ 매팅 가동 · " + Hz + " Hz · 시맨틱 트라이맵 → α/전경 언믹스 → SLAM 앵커 Z-Depth 합성 → HDRI 리라이팅 · 라이트 랩";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 매팅 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SemanticMatting.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 3 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SemanticMatting.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
