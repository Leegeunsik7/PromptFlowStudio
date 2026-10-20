using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.StrandHair;

/// <summary>
/// [PromptFlow Studio v17.15.0] 💇 AI 헤어 동역학 &amp; 뉴럴 그루밍 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// v16.75 합성 연기자(SMPL 24) + 8 초 시나리오(0–2 s 대기 흔들림 · 2–3.5 s 고속 고개 회전 ±70° · 3.5–5 s 점프 + 끄덕임 · 5–6.5 s 어깨 으쓱 · 6.5–8 s 앞으로 숙이기) →
/// 머리 자세 → v16.84 기류/정전기/습도(외력 · 뭉침) → PBD(앵커 · 충돌 가드) → 자식 생성 → 자식 관통 투영 → 계측 · Marschner 소프트웨어 렌더(BMP).
/// </summary>
public sealed class HairGroomingStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, J = MocapSkeleton.Body, ImgW = 280, ImgH = 340;
    public const float Dt = 1f / Hz, Cycle = 8f;
    private const float D = MathF.PI / 180f;

    private readonly object _gate = new();
    private readonly Vector3[] _kp = new Vector3[MocapSkeleton.Keypoints], _pos = new Vector3[J], _offsets = new Vector3[J];
    private readonly float[] _face = new float[FaceShapes.Count];
    private readonly Quaternion[] _local = new Quaternion[J], _global = new Quaternion[J];
    private readonly float[] _msS = new float[SeriesLen], _rawS = new float[SeriesLen], _resS = new float[SeriesLen], _windS = new float[SeriesLen], _tipS = new float[SeriesLen], _clumpS = new float[SeriesLen];
    private readonly float[] _img = new float[ImgW * ImgH * 3], _depth = new float[ImgW * ImgH];
    private readonly byte[] _bmp = new byte[54 + ImgW * ImgH * 3];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;
    private Vector3 _restTip;

    public HairGroomingStudioRunner(IServiceProvider sp)
        : this((StrandHairPbdDynamicsEngine?)sp.GetService(typeof(StrandHairPbdDynamicsEngine)) ?? new StrandHairPbdDynamicsEngine())
    {
    }

    public HairGroomingStudioRunner(StrandHairPbdDynamicsEngine engine)
    {
        Engine = engine;
        Guard = new ScalpRootAnchorBodyPenetrationGuard();
        Shader = new MarschnerHairScatteringAerodynamicsSynthesizer();
        Regroom();
        LastAction = "📋 대기 · 가이드 " + Engine.G + " × 자식 " + Engine.C + " = " + Engine.StrandCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " 스트랜드 · " + Engine.Path;
    }

    public event Action? OnChange;

    public StrandHairPbdDynamicsEngine Engine { get; }
    public ScalpRootAnchorBodyPenetrationGuard Guard { get; }
    public MarschnerHairScatteringAerodynamicsSynthesizer Shader { get; }
    public bool Motion { get; set; } = true;
    public bool ScenarioEnabled { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public string Phase { get; private set; } = "대기";
    public float HeadAngularSpeedDeg { get; private set; }
    public float TipLagMm { get; private set; }
    public float CameraYawDeg { get; set; } = 35f;
    public float LightYawDeg { get; set; } = 40f;
    public int DrawnStrands { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float RawPenSeries(int i) => _rawS[Idx(i)];
    public float ResidualPenSeries(int i) => _resS[Idx(i)];
    public float WindSeries(int i) => _windS[Idx(i)];
    public float TipLagSeries(int i) => _tipS[Idx(i)];
    public float ClumpSeries(int i) => _clumpS[Idx(i)];
    public Vector3 Joint(int j) => _pos[j];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // ── 그루밍(제어 평면) ───────────────────────────────────

    /// <summary>재그루밍: 레스트 자세 배치 → 충돌 가드 안착 90 프레임(형상 정합 0 · 자식 생략) → 레스트 캡처 → 이웃/지연 테이블.</summary>
    private void Regroom()
    {
        _time = 0f;
        Pose(0f, false);
        var head = _pos[MocapSkeleton.Head]; var hr = _global[MocapSkeleton.Head];
        Engine.Groom();
        Engine.PlaceFromRest(head, hr);
        Guard.UpdateBody(_pos, hr);
        var shape = Engine.ShapeStiffness; var children = Engine.ChildrenEnabled;
        Engine.ShapeStiffness = 0f; Engine.ChildrenEnabled = false;
        Array.Clear(Engine.FX); Array.Clear(Engine.FY); Array.Clear(Engine.FZ);
        for (var i = 0; i < 90; i++) { Engine.SetHeadPose(head, hr); Guard.BeginFrame(); Engine.Step(Dt, Guard); }
        Engine.CaptureRest();
        Engine.ShapeStiffness = shape; Engine.ChildrenEnabled = children;
        Engine.PlaceFromRest(head, hr);
        Shader.Bind(Engine);
        Engine.Frames();
        if (Engine.ChildrenEnabled) Engine.GenerateChildren();
        _restTip = Vector3.Transform(Engine.MeanTip() - head, Quaternion.Conjugate(hr));
    }

    public void SetLength(float m) { lock (_gate) { Engine.Length = Math.Clamp(m, 0.08f, 0.45f); Regroom(); } LastAction = "✂ 길이 " + F2(Engine.Length) + " m 재그루밍(안착 90 프레임)"; Raise(); }
    public void SetComb(float c) { lock (_gate) { Engine.Comb = Math.Clamp(c, 0f, 1.5f); Regroom(); } LastAction = "🪮 빗질 강도 " + F2(Engine.Comb) + " 재그루밍"; Raise(); }
    public void SetClumpCount(int k) { lock (_gate) Engine.SetClumpCount(k); LastAction = "🧶 클럼프 밀도 " + Engine.ClumpCount + " / 가이드"; Raise(); }
    public void SetCurl(float mm) { lock (_gate) { Engine.CurlAmp = Math.Clamp(mm, 0f, 15f) * 0.001f; Engine.UpdateChildProfiles(); } LastAction = "➰ 컬 진폭 " + F1(Engine.CurlAmp * 1000f) + " mm"; Raise(); }
    public void SetStiffness(float bend, float torsion) { lock (_gate) { Engine.BendStiffness = Math.Clamp(bend, 0f, 1f); Engine.TorsionStiffness = Math.Clamp(torsion, 0f, 1f); } LastAction = "🌀 굽힘 " + F2(Engine.BendStiffness) + " · 비틀림 " + F2(Engine.TorsionStiffness); Raise(); }
    public void SetWeather(float speed, float yaw, float humidity) { lock (_gate) Shader.SetWeather(speed, yaw, humidity); LastAction = "🌬 v16.84 기류 " + F1(speed) + " m/s · " + F1(yaw) + "° · 습도 " + F2(humidity); Raise(); }
    public void SetCollision(bool on) { lock (_gate) Guard.CollisionEnabled = on; LastAction = on ? "🛡 신체 SDF 충돌 ON" : "🛡 충돌 OFF — 관통 노출"; Raise(); }
    public void SetChildCollision(bool on) { lock (_gate) Guard.ChildCollision = on; LastAction = on ? "🛡 자식 스트랜드 관통 투영 ON" : "자식 관통 투영 OFF"; Raise(); }
    public void SetChildren(bool on) { lock (_gate) Engine.ChildrenEnabled = on; LastAction = on ? "👶 자식 스트랜드 생성 ON" : "👶 가이드만"; Raise(); }

    // ── 시나리오 ────────────────────────────────────────────

    private static float Ramp(float t, float a, float b) { var u = Math.Clamp((t - a) / (b - a), 0f, 1f); return u * u * (3f - 2f * u); }
    private static float Bump(float t, float a, float b) => t <= a || t >= b ? 0f : MathF.Pow(MathF.Sin(MathF.PI * (t - a) / (b - a)), 2f);

    private void Pose(float t, bool scenario)
    {
        SyntheticPerformer.Pose(t, 1f, _kp, _face, _local, _global, _offsets);
        var root = _kp[0];
        var c = t % Cycle;
        float yaw = 8f * MathF.Sin(t * 0.9f), pitch = 4f * MathF.Sin(t * 0.6f), spine = 0f, shrug = 0f, jump = 0f;
        if (scenario)
        {
            if (c is >= 2f and < 3.5f)
            {
                var k = (int)((c - 2f) / 0.375f);
                var u = Ramp((c - 2f) - k * 0.375f, 0f, 0.15f);
                float from = k == 0 ? 0f : (k % 2 == 1 ? 70f : -70f), to = k % 2 == 0 ? 70f : -70f;
                if (k >= 3) to = 0f;
                yaw = from + (to - from) * u;
            }
            if (c is >= 3.5f and < 5f)
            {
                var u = (c - 3.6f) / 0.5f;
                if (u is > 0f and < 1f) jump = 4f * 0.25f * u * (1f - u);
                pitch = 25f * Bump(c, 3.9f, 4.8f);
            }
            shrug = Bump(c, 5f, 6.5f);
            spine = 40f * Bump(c, 6.5f, 8f);
            Phase = c < 2f ? "대기 흔들림" : c < 3.5f ? "고속 고개 회전" : c < 5f ? (jump > 0 ? "점프" : "착지 · 끄덕임") : c < 6.5f ? "어깨 으쓱" : "앞으로 숙이기";
        }
        else Phase = "레스트";
        _local[MocapSkeleton.Neck] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * D) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch * D);
        _local[MocapSkeleton.Head] = Quaternion.Identity;
        if (!scenario)
        {
            for (var j = 0; j < J; j++) _local[j] = Quaternion.Identity;
            _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -65f * D);
            _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 65f * D);
            root = MocapSkeleton.RestOffset[0];
        }
        if (spine > 0) { _local[MocapSkeleton.Spine2] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, spine * 0.5f * D); _local[MocapSkeleton.Spine3] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, spine * 0.5f * D); }
        if (shrug > 0)
        {
            _local[MocapSkeleton.LCollar] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 22f * shrug * D);
            _local[MocapSkeleton.RCollar] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -22f * shrug * D);
        }
        // 팔은 머리카락 아래로(대기 흔들림만)
        if (scenario)
        {
            _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (-62f + 6f * MathF.Sin(t)) * D);
            _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (62f - 6f * MathF.Sin(t * 1.1f)) * D);
        }
        root.Y += jump;
        MocapSkeleton.ForwardKinematics(_offsets, _local, root, _pos, _global);
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("StrandHair.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            var prevRot = Engine.HeadRot;
            Pose(_time, ScenarioEnabled);
            var head = _pos[MocapSkeleton.Head]; var hr = _global[MocapSkeleton.Head];
            Engine.SetHeadPose(head, hr);
            var dq = Quaternion.Normalize(hr * Quaternion.Conjugate(prevRot));
            HeadAngularSpeedDeg = 2f * MathF.Acos(Math.Clamp(MathF.Abs(dq.W), 0f, 1f)) / D / Dt;
            Guard.UpdateBody(_pos, hr);
            Shader.Apply(Engine, Dt);
            Guard.BeginFrame();
            Engine.Step(Dt, Guard);
            if (Engine.ChildrenEnabled) Guard.CollideChildren(Engine);
            Guard.MeasureResidual(Engine);
            var expect = head + Vector3.Transform(_restTip, hr);
            TipLagMm = Vector3.Distance(Engine.MeanTip(), expect) * 1000f;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _rawS[h] = Guard.RawPenetrationMm; _resS[h] = Guard.ResidualPenetrationMm; _windS[h] = Shader.WindAtHead.Length(); _tipS[h] = TipLagMm; _clumpS[h] = Engine.ClumpStrength;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    // ── 소프트웨어 렌더(Marschner · 깊이 버퍼 · BMP) ──────────

    private Vector3 _camR, _camU, _camF;
    private Vector3 _center;
    private const float Zoom = 620f;

    private bool Project(Vector3 w, out float sx, out float sy, out float depth)
    {
        var p = w - _center;
        sx = ImgW * 0.5f + Vector3.Dot(p, _camR) * Zoom;
        sy = ImgH * 0.42f - Vector3.Dot(p, _camU) * Zoom;
        depth = Vector3.Dot(p, _camF);
        return sx > -50 && sy > -50 && sx < ImgW + 50 && sy < ImgH + 50;
    }

    private void BodyDisc(Vector3 c, float r, Vector3 col)
    {
        if (!Project(c, out var sx, out var sy, out var dz)) return;
        var rp = r * Zoom;
        int x0 = Math.Max(0, (int)(sx - rp)), x1 = Math.Min(ImgW - 1, (int)(sx + rp)), y0 = Math.Max(0, (int)(sy - rp)), y1 = Math.Min(ImgH - 1, (int)(sy + rp));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float dx = (x - sx) / rp, dy = (y - sy) / rp;
                var q = dx * dx + dy * dy;
                if (q > 1f) continue;
                var z = dz - MathF.Sqrt(1f - q) * r;
                var p = y * ImgW + x;
                if (z >= _depth[p]) continue;
                _depth[p] = z;
                var shade = 0.35f + 0.65f * MathF.Max(0f, -0.4f * dx - 0.5f * dy + 0.75f * MathF.Sqrt(1f - q));
                _img[p * 3] = col.X * shade; _img[p * 3 + 1] = col.Y * shade; _img[p * 3 + 2] = col.Z * shade;
            }
    }

    private void Line(Vector3 a, Vector3 b, Vector3 col, float alpha)
    {
        Project(a, out var ax, out var ay, out var az);
        Project(b, out var bx, out var by, out var bz);
        var n = (int)MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay)) + 1;
        if (n > 400) return;
        for (var s = 0; s <= n; s++)
        {
            var u = s / (float)n;
            int x = (int)(ax + (bx - ax) * u), y = (int)(ay + (by - ay) * u);
            if ((uint)x >= ImgW || (uint)y >= ImgH) continue;
            var p = y * ImgW + x;
            if (az + (bz - az) * u > _depth[p] + 0.004f) continue;
            _img[p * 3] += (col.X - _img[p * 3]) * alpha; _img[p * 3 + 1] += (col.Y - _img[p * 3 + 1]) * alpha; _img[p * 3 + 2] += (col.Z - _img[p * 3 + 2]) * alpha;
        }
    }

    /// <summary>뷰포트 BMP(머리/어깨 · 가이드 + 자식 표본 Marschner 쉐이딩).</summary>
    public string RenderBase64(int maxStrands = 3000)
    {
        lock (_gate)
        {
            var yaw = CameraYawDeg * D;
            _camF = new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));   // 카메라 → 장면
            _camU = Vector3.UnitY;
            _camR = Vector3.Cross(_camF, _camU);
            _center = _pos[MocapSkeleton.Neck] + new Vector3(0f, 0.08f, 0f);
            for (var p = 0; p < ImgW * ImgH; p++)
            {
                var g = 0.05f + 0.06f * (1f - p / ImgW / (float)ImgH);
                _img[p * 3] = g * 0.8f; _img[p * 3 + 1] = g * 0.85f; _img[p * 3 + 2] = g * 1.1f;
                _depth[p] = float.MaxValue;
            }
            var skin = new Vector3(0.86f, 0.66f, 0.55f); var cloth = new Vector3(0.22f, 0.32f, 0.5f);
            for (var b = 0; b < ScalpRootAnchorBodyPenetrationGuard.Bodies; b++)
            {
                var c = Guard.Body(b);
                var r = Guard.BodyRadius(b);
                var len = Vector3.Distance(c.A, c.B);
                var steps = Math.Max(1, (int)(len / (r * 0.35f)));
                for (var s = 0; s <= steps; s++) BodyDisc(Vector3.Lerp(c.A, c.B, s / (float)steps), r, c.Cloth ? cloth : skin);
            }
            var e = Engine;
            int N = StrandHairPbdDynamicsEngine.N;
            var lightDir = Vector3.Normalize(new Vector3(MathF.Sin(LightYawDeg * D), 0.55f, MathF.Cos(LightYawDeg * D)));
            var view = -_camF;
            var baseCol = Shader.BaseColor;
            var lightCol = new Vector3(1.6f);
            var total = e.ChildrenEnabled ? e.G * e.C : 0;
            var stride = Math.Max(1, total / Math.Max(1, maxStrands));
            var drawn = 0;
            for (var k = 0; k < total; k += stride)
            {
                int g = k / e.C, c = k % e.C;
                for (var i = 0; i < N - 1; i++)
                {
                    var a = e.Child(g, c, i); var b = e.Child(g, c, i + 1);
                    var t = b - a; var tl = t.Length();
                    if (tl < 1e-6f) continue;
                    t /= tl;
                    var col = Shader.Shade(t, lightDir, view, lightCol) + baseCol * 0.18f;
                    col = col / (Vector3.One + col);
                    Line(a, b, col, 0.32f);
                }
                drawn++;
            }
            for (var g = 0; g < e.G; g++)
                for (var i = 0; i < N - 1; i++)
                {
                    var a = e.P(i, g); var b = e.P(i + 1, g);
                    var t = Vector3.Normalize(b - a);
                    var col = Shader.Shade(t, lightDir, view, lightCol) + baseCol * 0.18f;
                    col = col / (Vector3.One + col);
                    Line(a, b, col, total > 0 ? 0.25f : 0.6f);
                }
            DrawnStrands = drawn + e.G;
            var d = _bmp.AsSpan();
            d.Slice(0, 54).Clear();
            d[0] = (byte)'B'; d[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), _bmp.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), ImgW);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -ImgH);
            d[26] = 1; d[28] = 24;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), _bmp.Length - 54);
            for (var p = 0; p < ImgW * ImgH; p++)
            {
                var o = 54 + p * 3;
                d[o] = B8(_img[p * 3 + 2]); d[o + 1] = B8(_img[p * 3 + 1]); d[o + 2] = B8(_img[p * 3]);
            }
            return Convert.ToBase64String(_bmp);
        }
    }

    private static byte B8(float v) => (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(v, 0f, 1f), 1f / 2.2f) * 255f + 0.5f), 0, 255);

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.StrandHair" };
            _loop.Start();
            LastAction = "▶ 헤어 동역학 가동 · 60 Hz · " + Engine.StrandCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " 스트랜드 · PBD → SDF 가드 → 자식 생성";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("StrandHair.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("StrandHair.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
