using System.Numerics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.Garment;

/// <summary>
/// [PromptFlow Studio v17.16.0] 👗 AI 다층 의상 동역학 &amp; 드레이핑 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// v16.75 합성 연기자 + 8 초 시나리오(0–2 s 양팔 이두 컬 · 2–4 s 양팔 거상 · 4–6 s 몸통 비틀기/숙이기 · 6–8 s 스쿼트 점프) →
/// v17.14 근육 팽창 피부 → 다층 의상 PBD(고정 · 피부/레이어/교차 튜브 가드) → 직물 변형률/주름 → v17.15 긴 머리 PBD → 헤어 ↔ 코트 상호 충돌 → 소프트웨어 렌더(BMP).
/// </summary>
public sealed class GarmentDynamicsStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, J = MocapSkeleton.Body, ImgW = 300, ImgH = 420;
    public const float Dt = 1f / Hz, Cycle = 8f, JumpHeight = 0.2f;
    private const float D = MathF.PI / 180f;

    private readonly object _gate = new();
    private readonly Vector3[] _kp = new Vector3[MocapSkeleton.Keypoints], _pos = new Vector3[J], _offsets = new Vector3[J];
    private readonly float[] _face = new float[FaceShapes.Count];
    private readonly Quaternion[] _local = new Quaternion[J], _global = new Quaternion[J];
    private readonly float[] _rootHist = new float[3];
    private readonly float[] _msS = new float[SeriesLen], _penS = new float[SeriesLen], _layS = new float[SeriesLen], _tenS = new float[SeriesLen], _foldS = new float[SeriesLen], _hairS = new float[SeriesLen];
    private readonly float[] _img = new float[ImgW * ImgH * 3], _depth = new float[ImgW * ImgH];
    private readonly byte[] _bmp = new byte[54 + ImgW * ImgH * 3];
    private readonly float _restAnkleY;
    private int _seriesHead, _seriesCount, _rootCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public GarmentDynamicsStudioRunner(IServiceProvider sp) : this(new NeuralAnatomicalMuscleFasciaEngine()) { }

    public GarmentDynamicsStudioRunner(NeuralAnatomicalMuscleFasciaEngine skin, int hairGuides = 64)
    {
        Skin = skin;
        Engine = new MultiLayerGarmentPbdDynamicsEngine(skin);
        Guard = new MultiLayerGarmentContactSelfIntersectionGuard(Engine);
        Draping = new AnisotropicFabricDrapingWrinkleSynthesizer(Engine, Guard);
        Engine.RestPlace();
        Engine.RecomputeRest();
        Guard.UpdateBody(true);
        _restAnkleY = skin.RestJoint(MocapSkeleton.LAnkle).Y;
        Hair = new StrandHairPbdDynamicsEngine(hairGuides, 8) { Length = 0.42f, ChildrenEnabled = false };
        HairGuard = new ScalpRootAnchorBodyPenetrationGuard { ClothThickness = 0.03f };
        Hair.Groom();
        SettleHair();
        WarmUp();
        LastAction = "📋 대기 · 의상 입자 " + Engine.P + " · 엣지 " + Engine.Edges + "(색 " + Engine.Colors + ") · 레이어 3 · 헤어 가이드 " + Hair.G + " · " + Engine.EffectivePath;
    }

    public event Action? OnChange;

    public NeuralAnatomicalMuscleFasciaEngine Skin { get; }
    public MultiLayerGarmentPbdDynamicsEngine Engine { get; }
    public MultiLayerGarmentContactSelfIntersectionGuard Guard { get; }
    public AnisotropicFabricDrapingWrinkleSynthesizer Draping { get; }
    public StrandHairPbdDynamicsEngine Hair { get; }
    public ScalpRootAnchorBodyPenetrationGuard HairGuard { get; }
    public bool Motion { get; set; } = true;
    public bool ScenarioEnabled { get; set; } = true;
    public bool HairEnabled { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public string Phase { get; private set; } = "대기";
    public float LandingAccel { get; private set; }
    public float CameraYawDeg { get; set; } = 30f;
    /// <summary>0 솔리드(재질 · 미세 주름 법선) · 1 장력 히트맵 · 2 와이어프레임.</summary>
    public int ViewMode { get; set; }
    public bool[] LayerVisible { get; } = { true, true, true };
    public bool ShowBody { get; set; } = true;
    /// <summary>입자별 솔리드 색 배율(v17.19 습윤 암전 · null = 미적용).</summary>
    public float[]? ShadeScale { get; set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float PenetrationSeries(int i) => _penS[Idx(i)];
    public float LayerViolationSeries(int i) => _layS[Idx(i)];
    public float TensionSeries(int i) => _tenS[Idx(i)];
    public float FoldSeries(int i) => _foldS[Idx(i)];
    public float HairSeries(int i) => _hairS[Idx(i)];

    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // ── 제어 평면 ───────────────────────────────────────────

    /// <summary>T-포즈 착장 → 팔 내림(60 프레임 보간) + 30 프레임 안착(헤어 포함) → 주름 기억 초기화.</summary>
    private void WarmUp()
    {
        for (var f = 0; f < 90; f++)
        {
            Pose(0f, false, MathF.Min(1f, f / 60f));
            Skin.Update(_pos, _global, 0f, Dt);
            Guard.UpdateBody();
            Engine.Step(Dt, Guard);
            Draping.Update();
            var hr = _global[MocapSkeleton.Head];
            Hair.SetHeadPose(_pos[MocapSkeleton.Head], hr);
            HairGuard.UpdateBody(_pos, hr);
            HairGuard.BeginFrame();
            Hair.Step(Dt, HairGuard);
            Guard.HairContact(Hair);
        }
        Draping.ResetMemory();
        _rootCount = 0;
    }

    private void SettleHair()
    {
        Pose(0f, false);
        var head = _pos[MocapSkeleton.Head]; var hr = _global[MocapSkeleton.Head];
        Hair.PlaceFromRest(head, hr);
        HairGuard.UpdateBody(_pos, hr);
        var shape = Hair.ShapeStiffness;
        Hair.ShapeStiffness = 0f;
        for (var i = 0; i < 90; i++) { Hair.SetHeadPose(head, hr); HairGuard.BeginFrame(); Hair.Step(Dt, HairGuard); }
        Hair.CaptureRest();
        Hair.ShapeStiffness = shape;
        Hair.PlaceFromRest(head, hr);
    }

    public void SetFabric(GarmentLayer layer, FabricPreset preset)
    {
        lock (_gate) { Draping.SetLayer(layer, preset); Draping.ResetMemory(); }
        LastAction = "🧵 " + LayerName(layer) + " → " + AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)preset].Name + " (경사 " + F2(AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)preset].Warp) + " · 굽힘 " + F2(AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)preset].BendWarp) + ")";
        Raise();
    }

    public static string LayerName(GarmentLayer l) => l switch { GarmentLayer.Base => "베이스(이너)", GarmentLayer.Mid => "미드(셔츠/팬츠)", _ => "아우터(코트)" };

    public void SetLayerGuard(bool on) { lock (_gate) Guard.LayerGuard = on; LastAction = on ? "🧅 레이어 간 자가 교차 가드 ON" : "🧅 레이어 가드 OFF — 레이어 관통 노출"; Raise(); }
    public void SetBodyCollision(bool on) { lock (_gate) Guard.BodyCollision = on; LastAction = on ? "🛡 피부 충돌 ON" : "🛡 피부 충돌 OFF"; Raise(); }
    public void SetCrossTube(bool on) { lock (_gate) Guard.CrossTube = on; LastAction = on ? "🔀 몸통/팔/다리 교차 충돌 ON" : "🔀 교차 충돌 OFF"; Raise(); }
    public void SetHairContact(bool on) { lock (_gate) Guard.HairContactEnabled = on; LastAction = on ? "💇 헤어 ↔ 코트 상호 충돌 ON" : "💇 헤어 ↔ 코트 충돌 OFF"; Raise(); }
    public void SetWrinkleGain(float g) { lock (_gate) Draping.WrinkleGain = Math.Clamp(g, 0f, 3f); LastAction = "〽 미세 주름 게인 × " + F2(Draping.WrinkleGain); Raise(); }
    public void SetMuscleGain(float g) { lock (_gate) Skin.MuscleGain = Math.Clamp(g, 0f, 2.5f); LastAction = "💪 근육 팽창 게인 × " + F2(Skin.MuscleGain); Raise(); }

    // ── 시나리오 ────────────────────────────────────────────

    private static float Bump(float t, float a, float b) => t <= a || t >= b ? 0f : MathF.Pow(MathF.Sin(MathF.PI * (t - a) / (b - a)), 2f);
    private static float Ramp(float t, float a, float b) { var u = Math.Clamp((t - a) / (b - a), 0f, 1f); return u * u * (3f - 2f * u); }

    private void Pose(float t, bool scenario, float armBlend = 1f)
    {
        SyntheticPerformer.Pose(t, 1f, _kp, _face, _local, _global, _offsets);
        var root = _kp[0];
        var c = t % Cycle;
        float air = 0f;
        if (!scenario)
        {
            for (var j = 0; j < J; j++) _local[j] = Quaternion.Identity;
            _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -60f * armBlend * D);
            _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 60f * armBlend * D);
            _local[MocapSkeleton.LElbow] = Quaternion.CreateFromAxisAngle(-Vector3.UnitY, 20f * armBlend * D);
            _local[MocapSkeleton.RElbow] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20f * armBlend * D);
            root = MocapSkeleton.RestOffset[0];
            Phase = "레스트";
        }
        else
        {
            var lower = MathF.Max(0f, 1f - Bump(c, 2f, 4f));
            _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (-60f * lower + 55f * Bump(c, 2f, 4f)) * D);
            _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (60f * lower - 55f * Bump(c, 2f, 4f)) * D);
            var curl = Bump(c, 0f, 2f);
            _local[MocapSkeleton.LElbow] = Quaternion.CreateFromAxisAngle(-Vector3.UnitY, (20f + 115f * curl) * D);
            _local[MocapSkeleton.RElbow] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (20f + 115f * curl) * D);
            var tw = Bump(c, 4f, 6f);
            if (tw > 0)
            {
                _local[MocapSkeleton.Spine2] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * MathF.Sin(MathF.Tau * (c - 4f) / 2f) * tw * D);
                _local[MocapSkeleton.Spine1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 22f * tw * D);
            }
            float squat = 0f;
            if (c is >= 6f and < 8f)
            {
                if (c < 6.5f) squat = Ramp(c, 6f, 6.5f);
                else if (c < 6.65f) squat = 1f - Ramp(c, 6.5f, 6.65f);
                else if (c < 7.05f) { var u = (c - 6.65f) / 0.4f; air = 4f * JumpHeight * u * (1f - u); }
                else if (c < 7.2f) squat = 0.7f * Ramp(c, 7.05f, 7.2f);
                else squat = 0.7f * (1f - Ramp(c, 7.2f, 8f));
                for (var s = 0; s < 2; s++)
                {
                    int hip = s == 0 ? MocapSkeleton.LHip : MocapSkeleton.RHip, kn = s == 0 ? MocapSkeleton.LKnee : MocapSkeleton.RKnee, an = s == 0 ? MocapSkeleton.LAnkle : MocapSkeleton.RAnkle;
                    _local[hip] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -60f * squat * D);
                    _local[kn] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 95f * squat * D);
                    _local[an] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -25f * squat * D);
                }
            }
            Phase = c < 2f ? "이두 컬" : c < 4f ? "양팔 거상" : c < 6f ? "비틀기 · 숙이기" : air > 0 ? "점프 체공" : "스쿼트 · 착지";
        }
        MocapSkeleton.ForwardKinematics(_offsets, _local, root, _pos, _global);
        var dy = _restAnkleY - MathF.Min(_pos[MocapSkeleton.LAnkle].Y, _pos[MocapSkeleton.RAnkle].Y) + air;
        root.Y += dy;
        MocapSkeleton.ForwardKinematics(_offsets, _local, root, _pos, _global);
        _rootHist[0] = _rootHist[1]; _rootHist[1] = _rootHist[2]; _rootHist[2] = root.Y;
        if (_rootCount < 3) _rootCount++;
        LandingAccel = _rootCount >= 3 ? MathF.Max(0f, (_rootHist[2] - 2f * _rootHist[1] + _rootHist[0]) / (Dt * Dt)) : 0f;
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("Garment.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            Pose(_time, ScenarioEnabled);
            Skin.Update(_pos, _global, LandingAccel, Dt);
            Guard.UpdateBody();
            Engine.Step(Dt, Guard);
            Guard.MeasureResidual();
            Draping.Update();
            if (HairEnabled)
            {
                var hr = _global[MocapSkeleton.Head];
                Hair.SetHeadPose(_pos[MocapSkeleton.Head], hr);
                HairGuard.UpdateBody(_pos, hr);
                HairGuard.BeginFrame();
                Hair.Step(Dt, HairGuard);
                Guard.HairContact(Hair);
            }
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _penS[h] = Guard.BodyPenetrationBeforeMm; _layS[h] = Guard.LayerViolationsBefore; _tenS[h] = Draping.LayerMaxTension[1]; _foldS[h] = Draping.Folds; _hairS[h] = Guard.HairInsideBefore;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    // ── 소프트웨어 렌더(삼각형 깊이 버퍼 · BMP) ─────────────

    private Vector3 _camR, _camU, _camF, _center;
    private const float Zoom = 300f;

    private void Project(Vector3 w, out float sx, out float sy, out float z)
    {
        var p = w - _center;
        sx = ImgW * 0.5f + Vector3.Dot(p, _camR) * Zoom;
        sy = ImgH * 0.5f - Vector3.Dot(p, _camU) * Zoom;
        z = Vector3.Dot(p, _camF);
    }

    private void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 col)
    {
        Project(a, out var ax, out var ay, out var az); Project(b, out var bx, out var by, out var bz); Project(c, out var cx, out var cy, out var cz);
        var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(0, (int)MathF.Min(ax, MathF.Min(bx, cx))), x1 = Math.Min(ImgW - 1, (int)MathF.Max(ax, MathF.Max(bx, cx)));
        int y0 = Math.Max(0, (int)MathF.Min(ay, MathF.Min(by, cy))), y1 = Math.Min(ImgH - 1, (int)MathF.Max(ay, MathF.Max(by, cy)));
        if (x1 - x0 > 200 || y1 - y0 > 200) return;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                var w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = w0 * az + w1 * bz + w2 * cz;
                var p = y * ImgW + x;
                if (z >= _depth[p]) continue;
                _depth[p] = z;
                _img[p * 3] = col.X; _img[p * 3 + 1] = col.Y; _img[p * 3 + 2] = col.Z;
            }
    }

    private void Line(Vector3 a, Vector3 b, Vector3 col)
    {
        Project(a, out var ax, out var ay, out var az); Project(b, out var bx, out var by, out var bz);
        var n = (int)MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay)) + 1;
        if (n > 300) return;
        for (var s = 0; s <= n; s++)
        {
            var u = s / (float)n;
            int x = (int)(ax + (bx - ax) * u), y = (int)(ay + (by - ay) * u);
            if ((uint)x >= ImgW || (uint)y >= ImgH) continue;
            var p = y * ImgW + x;
            var z = az + (bz - az) * u - 0.003f;
            if (z > _depth[p]) continue;
            _img[p * 3] = col.X; _img[p * 3 + 1] = col.Y; _img[p * 3 + 2] = col.Z;
        }
    }

    private static Vector3 Heat(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Vector3(Math.Clamp(1.5f - MathF.Abs(4f * t - 3f), 0f, 1f), Math.Clamp(1.5f - MathF.Abs(4f * t - 2f), 0f, 1f), Math.Clamp(1.5f - MathF.Abs(4f * t - 1f), 0f, 1f));
    }

    private Vector3 Lit(Vector3 baseCol, Vector3 n, Vector3 light)
    {
        if (Vector3.Dot(n, _camF) > 0f) n = -n;
        var d = MathF.Max(0f, Vector3.Dot(n, light));
        return baseCol * (0.28f + 0.72f * d);
    }

    /// <summary>뷰포트 BMP: 피부(v17.14) · 레이어별 의상(솔리드/장력/와이어) · 헤어 가이드.</summary>
    public string RenderBase64()
    {
        lock (_gate)
        {
            var yaw = CameraYawDeg * D;
            _camF = new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
            _camU = Vector3.UnitY;
            _camR = Vector3.Cross(_camF, _camU);
            _center = new Vector3(_pos[0].X, 1.0f, _pos[0].Z);
            for (var p = 0; p < ImgW * ImgH; p++)
            {
                var g = 0.05f + 0.05f * (1f - p / ImgW / (float)ImgH);
                _img[p * 3] = g; _img[p * 3 + 1] = g * 1.05f; _img[p * 3 + 2] = g * 1.3f;
                _depth[p] = float.MaxValue;
            }
            var light = Vector3.Normalize(new Vector3(0.4f, 0.7f, 0.6f));
            const int S = NeuralAnatomicalMuscleFasciaEngine.Segs, R = NeuralAnatomicalMuscleFasciaEngine.Rings;
            if (ShowBody)
                for (var t = 0; t < NeuralAnatomicalMuscleFasciaEngine.Tubes; t++)
                    for (var r = 0; r < R - 1; r++)
                        for (var s = 0; s < S; s++)
                        {
                            int a = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, r, s), b = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, r, (s + 1) % S), c = a + S, d = b + S;
                            var col = Lit(new Vector3(0.85f, 0.64f, 0.53f), Skin.Normal(a), light);
                            Tri(Skin.Position(a), Skin.Position(b), Skin.Position(d), col); Tri(Skin.Position(a), Skin.Position(d), Skin.Position(c), col);
                        }
            var e = Engine;
            for (var l = 0; l < MultiLayerGarmentPbdDynamicsEngine.Layers; l++)
            {
                if (!LayerVisible[l]) continue;
                var mat = Draping.LayerMaterial((GarmentLayer)l);
                for (var i = 0; i < e.P; i++)
                {
                    if (e.LayerOf[i] != l) continue;
                    int tube = e.TubeOf[i], ring = e.RingOf[i], seg = e.SegOf[i];
                    int b = e.Map(l, tube, ring, seg + 1), c = e.Map(l, tube, ring + 1, seg), d = e.Map(l, tube, ring + 1, seg + 1);
                    if (b < 0 || c < 0 || d < 0) continue;
                    if (ViewMode == 2)
                    {
                        var wc = l == 0 ? new Vector3(0.9f, 0.9f, 0.95f) : l == 1 ? new Vector3(0.4f, 0.8f, 1f) : new Vector3(1f, 0.6f, 0.3f);
                        Line(e.Pos(i), e.Pos(b), wc); Line(e.Pos(i), e.Pos(c), wc);
                        continue;
                    }
                    Vector3 col;
                    if (ViewMode == 1) col = Heat(Draping.Tension[i] / 0.08f) * 0.9f + new Vector3(0.05f);
                    else col = Lit(mat.Color, new Vector3(Draping.MicroX[i], Draping.MicroY[i], Draping.MicroZ[i]), light);
                    if (ViewMode != 1 && ShadeScale is { } ss && i < ss.Length) col *= ss[i];   // v17.19 젖음 암전
                    Tri(e.Pos(i), e.Pos(b), e.Pos(d), col); Tri(e.Pos(i), e.Pos(d), e.Pos(c), col);
                }
            }
            if (HairEnabled)
                for (var g = 0; g < Hair.G; g++)
                    for (var i = 0; i < StrandHairPbdDynamicsEngine.N - 1; i++)
                        Line(Hair.P(i, g), Hair.P(i + 1, g), new Vector3(0.25f, 0.16f, 0.1f));
            var dd = _bmp.AsSpan();
            dd.Slice(0, 54).Clear();
            dd[0] = (byte)'B'; dd[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dd.Slice(2), _bmp.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dd.Slice(10), 54);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dd.Slice(14), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dd.Slice(18), ImgW);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dd.Slice(22), -ImgH);
            dd[26] = 1; dd[28] = 24;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dd.Slice(34), _bmp.Length - 54);
            for (var p = 0; p < ImgW * ImgH; p++)
            {
                var o = 54 + p * 3;
                dd[o] = B8(_img[p * 3 + 2]); dd[o + 1] = B8(_img[p * 3 + 1]); dd[o + 2] = B8(_img[p * 3]);
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
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Garment" };
            _loop.Start();
            LastAction = "▶ 다층 의상 가동 · 60 Hz · 근육 피부 → 3 레이어 PBD → 레이어/신체 가드 → 주름 → 헤어 상호 충돌";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Garment.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Garment.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
