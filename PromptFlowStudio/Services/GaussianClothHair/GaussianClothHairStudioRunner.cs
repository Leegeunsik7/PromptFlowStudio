using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Microclimate;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.GaussianClothHair;

/// <summary>
/// [PromptFlow Studio v17.59.0] 👗 가우시안 클로스 &amp; 헤어 피직스 스튜디오 러너(60 Hz · 320×192).
/// 프레임 = v17.57 FK 모션(+ 회전 · 원형 이동 속도) → DQS 전신 → 캡슐 프록시/바람(v17.28 기상 연동) → PBD 직물 · 헤어 → 신장/CCD 가드 → 공분산 정렬 스플랫 → v17.55 래스터라이저. 연산 구간 무할당.
/// 글리치(고속 회전 + 프레임 저하 히치): 30 프레임마다 100 ms 프레임 드롭 + 순간 회전 1.2 rad(≈ 12 rad/s) → 캡슐 도약 터널링 · 과신장 유발.
/// </summary>
public sealed class GaussianClothHairStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, GlitchEvery = 30, BodyPoints = 20_000, EnvSplats = 20_000;
    public const float Dt = 1f / Hz, GlitchDt = 0.1f, GlitchSnap = 1.2f, PathRadius = 2.2f, Aspect = (float)RealtimeGaussianSplatRasterizerEngine.W / RealtimeGaussianSplatRasterizerEngine.H;
    public const int PreviewBytes = 54 + RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 3;
    public static readonly AvatarMotion[] Motions = { AvatarMotion.ArmsRaise, AvatarMotion.Fight, AvatarMotion.Jump, AvatarMotion.TwistStress };

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _strainS = new float[Hist], _tunS = new float[Hist];
    private readonly AtmosphericWeatherStudioRunner? _weather;
    private GaussianSplatScene? _scene;
    private RealtimeDualQuaternionGaussianSkinningEngine? _body;
    private RealtimeGaussianSplatRasterizerEngine? _raster;
    private int _sHead, _sN, _glitchN, _iters = RealtimeGaussianClothHairPhysicsEngine.DefaultIterations;
    private float _t, _spinAcc, _pathA;
    private SplatCamera _cam;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public GaussianClothHairStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(AtmosphericWeatherStudioRunner)) as AtmosphericWeatherStudioRunner) { }

    public GaussianClothHairStudioRunner(AtmosphericWeatherStudioRunner? weather = null) => _weather = weather;

    public event Action? OnChange;
    public AvatarFkAnimator Animator { get; } = new();
    public VolumetricDeterminantClampingGuard BodyGuard { get; } = new();
    public RealtimeGaussianClothHairPhysicsEngine Physics { get; } = new();
    public AerodynamicWindBodyCollisionSynthesizer Aero { get; } = new();
    public StrainLimitPenetrationGuard Guard { get; } = new();
    public SplatFrustumCullingVramBudgetGuard CullGuard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new() { Degree = 0 };
    public RealtimeGaussianSplatRasterizerEngine? Raster => _raster;
    public GaussianSplatScene? Scene => _scene;
    public AvatarMotion Motion { get; private set; } = AvatarMotion.ArmsRaise;
    public float SpinRate { get; private set; }
    public float MoveSpeed { get; private set; }
    public int Iterations => _iters;
    public bool Glitch { get; private set; }
    public bool LastHitch { get; private set; }
    public bool UseWeather { get; private set; }
    public bool WeatherAvailable => _weather is not null;
    public int Points => RealtimeGaussianClothHairPhysicsEngine.Splats;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float StrainSeries(int i) => _strainS[Idx(i)];
    public float TunnelSeries(int i) => _tunS[Idx(i)];

    public static string MotionName(AvatarMotion m) => m switch
    {
        AvatarMotion.Fight => "🥊 격투 콤보", AvatarMotion.Jump => "🦘 도약", AvatarMotion.TwistStress => "🌀 비틀기", _ => "🙆 팔 들기",
    };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureRig()
    {
        if (_scene is not null) return;
        _scene = new GaussianSplatScene(EnvSplats + BodyPoints + RealtimeGaussianClothHairPhysicsEngine.Splats);
        _scene.Generate(EnvSplats, 0xC759u, centerpiece: false);
        var bs = _scene.Reserve(BodyPoints);
        var weights = new BoneWeights4[BodyPoints]; var normals = new Vector3[BodyPoints];
        var nb = GaussianAvatarRig.Build(_scene, bs, BodyPoints, weights, normals);
        _body?.Dispose(); _raster?.Dispose();
        _body = new RealtimeDualQuaternionGaussianSkinningEngine(BodyPoints);
        _body.Bind(_scene, bs, nb, weights);
        var cs = _scene.Reserve(RealtimeGaussianClothHairPhysicsEngine.ClothSplats);
        var hs = _scene.Reserve(RealtimeGaussianClothHairPhysicsEngine.HairSplats);
        Physics.Bind(_scene, cs, hs);
        _raster = new RealtimeGaussianSplatRasterizerEngine();
    }

    public void Prepare() { lock (_gate) EnsureRig(); }

    private void ResetSim() { Animator.Evaluate(Motion, _t, _body!); Physics.Place(Animator); Aero.ResetBody(); Guard.Reset(); }

    public void SetMotion(AvatarMotion m) { lock (_gate) Motion = m; LastAction = "🏃 모션 → " + MotionName(m); Raise(); }

    public void SetFabric(FabricPreset p) { lock (_gate) { EnsureRig(); Physics.SetFabric(p, _scene); } LastAction = "👗 원단 → " + RealtimeGaussianClothHairPhysicsEngine.FabricName(p); Raise(); }

    public void SetHairStiffness(float k) { lock (_gate) Physics.HairStiffness = Math.Clamp(k, 0f, 1f); LastAction = "💇 헤어 강성 → " + F2(Physics.HairStiffness); Raise(); }

    public void SetWind(float ms) { lock (_gate) Aero.WindSpeed = Math.Clamp(ms, 0f, 25f); LastAction = "🌬 풍속 → " + F1(Aero.WindSpeed) + " m/s"; Raise(); }

    public void SetWindDir(float deg) { lock (_gate) Aero.WindDirDeg = (deg % 360f + 360f) % 360f; LastAction = "🧭 풍향 → " + F1(Aero.WindDirDeg) + "°"; Raise(); }

    public void SetTurbulence(float t) { lock (_gate) Aero.Turbulence = Math.Clamp(t, 0f, 1f); LastAction = "🌪 난류 → " + F2(Aero.Turbulence); Raise(); }

    public void SetSpin(float radS) { lock (_gate) SpinRate = Math.Clamp(radS, -20f, 20f); LastAction = "🔄 회전 → " + F1(SpinRate) + " rad/s"; Raise(); }

    public void SetMoveSpeed(float ms) { lock (_gate) MoveSpeed = Math.Clamp(ms, 0f, 8f); LastAction = "🏃 이동 속도 → " + F1(MoveSpeed) + " m/s"; Raise(); }

    public void SetIterations(int n) { lock (_gate) _iters = Math.Clamp(n, 1, 32); LastAction = "🔁 PBD 반복 → " + _iters; Raise(); }

    public void SetGlitch(bool on) { lock (_gate) { EnsureRig(); Glitch = on; _glitchN = 0; } LastAction = on ? "💥 고속 회전 + 프레임 저하 히치(30 프레임마다 100 ms · 1.2 rad) 주입 ON" : "💥 주입 OFF"; Raise(); }

    public void SetStrainGuard(bool on) { lock (_gate) Guard.StrainEnabled = on; LastAction = on ? "🛡 신장률 110 % 클램프 ON" : "⛔ 신장 클램프 OFF(비교)"; Raise(); }

    public void SetCcdGuard(bool on) { lock (_gate) Guard.CcdEnabled = on; LastAction = on ? "🛡 스윕 구 CCD 터널링 방어 ON" : "⛔ CCD OFF(비교)"; Raise(); }

    public void SetCollision(bool on) { lock (_gate) Aero.CollisionEnabled = on; LastAction = on ? "🧍 신체 캡슐 충돌 ON" : "🧍 신체 충돌 OFF"; Raise(); }

    public void SetWeather(bool on)
    {
        if (on && _weather is null) return;
        lock (_gate) UseWeather = on;
        LastAction = on ? "⛈ v17.28 지균풍 · 난류 연동" : "🌬 수동 바람";
        Raise();
    }

    public void ResetCloth() { lock (_gate) { EnsureRig(); ResetSim(); Guard.ResetMetrics(); } LastAction = "↺ 의상 · 헤어 휴지 배치"; Raise(); }

    // ══════════════════════ 프레임 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureRig();
            using var zg = ZeroGcRegionScope.Enter("ClothHair.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        var hitch = Glitch && ++_glitchN % GlitchEvery == 0;
        LastHitch = hitch;
        var dt = hitch ? GlitchDt : Dt;
        _t += dt;
        var body = _body!; var sc = _scene!;
        // ① 모션 · 회전 · 원형 경로 이동
        _spinAcc += SpinRate * dt + (hitch ? GlitchSnap : 0f);
        var heading = 0f;
        if (MoveSpeed > 0f)
        {
            _pathA += MoveSpeed / PathRadius * dt;
            Animator.Root = new Vector3(PathRadius * (MathF.Cos(_pathA) - 1f), 0f, PathRadius * MathF.Sin(_pathA));
            heading = -_pathA;
        }
        Animator.Yaw = heading + _spinAcc;
        Animator.Evaluate(Motion, _t, body);
        body.Skin(BodyGuard);
        // ② 바람 · 캡슐 · PBD · 가드
        if (UseWeather && _weather is not null) Aero.LinkWeather(_weather.Engine.WindMs, _weather.Engine.WindDirDeg, _weather.Engine.Turbulence);
        Aero.UpdateBody(Animator, dt);
        Physics.Iterations = _iters;
        Physics.Step(dt, Animator, Aero);
        Guard.Apply(Physics, Aero);
        Physics.WriteSplats(sc, Animator.Global((int)RagdollJoint.Head));
        // ③ 카메라 · 렌더
        var pel = Animator.Positions[(int)RagdollJoint.Pelvis];
        var target = pel + new Vector3(0f, 0.25f, 0f);
        var orbit = 0.4f * _t;
        _cam = SplatCamera.LookAt(target + new Vector3(2.3f * MathF.Sin(orbit), 0.35f, 2.3f * MathF.Cos(orbit)), target, 45f, Aspect, 0.05f, 120f);
        _raster!.Render(sc, in _cam, CullGuard, Sh);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        _msS[_sHead] = (float)ms; _strainS[_sHead] = (Guard.FramePeakStrain - 1f) * 100f; _tunS[_sHead] = Guard.FrameTunnels;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    /// <summary>월드 점 → 프리뷰(320×192) 좌표.</summary>
    public bool Project(Vector3 p, out float x, out float y)
    {
        var d = p - _cam.Position;
        var z = Vector3.Dot(d, _cam.Forward);
        x = y = 0f;
        if (z < 0.05f) return false;
        x = RealtimeGaussianSplatRasterizerEngine.W * 0.5f * (1f + Vector3.Dot(d, _cam.Right) / (z * _cam.TanHalfH));
        y = RealtimeGaussianSplatRasterizerEngine.H * 0.5f * (1f - Vector3.Dot(d, _cam.Up) / (z * _cam.TanHalfV));
        return true;
    }

    private static string F1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public string Summary()
    {
        var g = Guard;
        return "과신장 " + g.FrameOverStretch.ToString(CultureInfo.InvariantCulture) + " · 최대 " + F1((g.FramePeakStrain - 1f) * 100f) + " % · 터널 " + g.Tunnels.ToString(CultureInfo.InvariantCulture) + " · 침투 " + g.Penetrations.ToString(CultureInfo.InvariantCulture) + " · 잔여 " + g.Defects.ToString(CultureInfo.InvariantCulture);
    }

    public void WritePreviewBmp(byte[] dst) { lock (_gate) _raster?.WriteBmp(dst, false); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.ClothHair" };
            _loop.Start();
        }
        LastAction = "▶ 가우시안 클로스 & 헤어 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("ClothHair.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ClothHair.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) { _body?.Dispose(); _raster?.Dispose(); }
    }
}
