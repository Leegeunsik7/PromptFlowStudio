using System.Numerics;
using PromptFlowStudio.Services.GenerativeVfx;
using PromptFlowStudio.Services.OpenUsd;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.DigitalTwinWorld;

/// <summary>
/// [PromptFlow Studio v16.99.0] 🌐 AI 실시간 디지털 트윈 &amp; 옴니버스 월드 스튜디오 러너(30 Hz 전용 스레드).
/// 실물 대역(시뮬레이션 공장 셀): 로봇 엔드이펙터 궤도 · 컨베이어 상자(가상 명령 추종 1차 지연) · 카메라(v16.98 SLAM 자세 또는 궤도) · 온도 센서 · 고정 마커(지터) —
/// 센서 프레임(월드 대비 요 30° · 이동 오프셋)으로 측정 + 가우시안 잡음.
/// 틱: 실물 → 동기화(캘리브레이션) → 로봇 = 운동학 강체 → 멀티피직스 → 강체/로봇/상자 → USD → 앵커 가드(마커 = 센서 · 소품 = 물리) → Nucleus 델타 발행 → 세션 펌프.
/// </summary>
public sealed class DigitalTwinWorldStudioRunner : IDisposable
{
    public const int Hz = 30, SeriesLen = 120, Balls = 8;
    public static readonly SlamPose SensorToWorldTruth = new(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * MathF.PI / 180f), new Vector3(0.6f, 0f, -0.4f));
    public static readonly Vector3[] Fiducials = { new(1, 0, 0), new(0, 1.2f, 0), new(0, 0, 1), new(1.5f, 0.5f, -1), new(-1, 0.8f, 0.5f) };

    private readonly object _gate = new();
    private readonly SpatialSlamStudioRunner? _slam;
    private readonly float[] _series = new float[SeriesLen * UnifiedMultiPhysicsWorldDynamicsSynthesizer.Stages];
    private readonly float[] _errSeries = new float[SeriesLen];
    private readonly int[] _ballBody = new int[Balls], _ballPrim = new int[Balls];
    private readonly Vector3[] _fidSensor = new Vector3[Fiducials.Length];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private ulong _rng = 0x1699_7A1Eu;
    private Vector3 _conveyorPhys = new(-1.2f, 0.25f, 1.2f);
    private int _robotBody, _robotPrim, _boxPrim, _camPrim, _tempPrim, _markerPrim, _propPrim, _propBody;
    private int _nRobot, _nBox, _nCam, _nTemp, _anchorMarker, _anchorProp;
    private float _time;

    public DigitalTwinWorldStudioRunner(IServiceProvider sp)
        : this((RealtimeDigitalTwinSpatialSyncEngine?)sp.GetService(typeof(RealtimeDigitalTwinSpatialSyncEngine)) ?? new RealtimeDigitalTwinSpatialSyncEngine(),
               (UnifiedMultiPhysicsWorldDynamicsSynthesizer?)sp.GetService(typeof(UnifiedMultiPhysicsWorldDynamicsSynthesizer)) ?? new UnifiedMultiPhysicsWorldDynamicsSynthesizer(),
               sp.GetService(typeof(SpatialSlamStudioRunner)) as SpatialSlamStudioRunner) { }

    public DigitalTwinWorldStudioRunner(RealtimeDigitalTwinSpatialSyncEngine sync, UnifiedMultiPhysicsWorldDynamicsSynthesizer physics, SpatialSlamStudioRunner? slam = null)
    {
        Sync = sync; Physics = physics; _slam = slam;
        Layer = new UsdLayer("twin.usda");
        Live = new OmniverseLiveUsdSyncAnchorGuard(Layer);
        Sync.Layer = Layer;
        Build();
    }

    public event Action? OnChange;

    public RealtimeDigitalTwinSpatialSyncEngine Sync { get; }
    public UnifiedMultiPhysicsWorldDynamicsSynthesizer Physics { get; }
    public OmniverseLiveUsdSyncAnchorGuard Live { get; }
    public UsdLayer Layer { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public float SensorNoiseMm { get; set; } = 2f;
    public float MarkerJitterMm { get; set; } = 4f;
    public bool LiveSession { get; private set; }
    public bool UseSlam { get; set; } = true;
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    public int RobotNode => _nRobot;
    public int BoxNode => _nBox;
    public int CameraNode => _nCam;
    public int TempNode => _nTemp;
    public int MarkerAnchor => _anchorMarker;
    public int PropAnchor => _anchorProp;
    public int BallBody(int k) => _ballBody[k];
    public Vector3 ConveyorPhysical => _conveyorPhys;
    public float StageSeries(int i, PhysicsStage s) => _series[((_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen) * UnifiedMultiPhysicsWorldDynamicsSynthesizer.Stages + (int)s];
    public float ErrorSeries(int i) => _errSeries[(_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen];

    private float Gauss()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; var u1 = ((_rng >> 40) + 0.5f) / (1 << 24);
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; var u2 = ((_rng >> 40) + 0.5f) / (1 << 24);
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2);
    }

    private Vector3 Noise(float mm) => new Vector3(Gauss(), Gauss(), Gauss()) * (mm * 0.001f);

    private void Build()
    {
        var l = Layer;
        l.AddPath("/World", UsdSpec.Def, UsdPrimType.Xform);
        _robotPrim = l.AddPath("/World/Robot", UsdSpec.Def, UsdPrimType.Sphere); l.SetRadius(_robotPrim, 0.15f); l.SetColor(_robotPrim, 0.9f, 0.5f, 0.1f);
        _boxPrim = l.AddPath("/World/Conveyor", UsdSpec.Def, UsdPrimType.Cube); l.SetSize(_boxPrim, 0.5f); l.SetColor(_boxPrim, 0.3f, 0.6f, 0.9f);
        _camPrim = l.AddPath("/World/Camera", UsdSpec.Def, UsdPrimType.Xform);
        _tempPrim = l.AddPath("/World/TempSensor", UsdSpec.Def, UsdPrimType.Xform);
        _markerPrim = l.AddPath("/World/Anchor_Marker", UsdSpec.Def, UsdPrimType.Cube); l.SetSize(_markerPrim, 0.1f); l.SetTranslate(_markerPrim, 1.6f, 0.05f, 1.4f);
        _propPrim = l.AddPath("/World/Anchor_Prop", UsdSpec.Def, UsdPrimType.Sphere); l.SetRadius(_propPrim, 0.2f); l.SetTranslate(_propPrim, -1.4f, 0.2f, -1.2f);
        _nRobot = Sync.AddNode("로봇 엔드이펙터", TwinAuthority.Physical, _robotPrim);
        _nBox = Sync.AddNode("컨베이어 상자", TwinAuthority.Virtual, _boxPrim);
        _nCam = Sync.AddNode("현장 카메라(SLAM)", TwinAuthority.Bidirectional, _camPrim);
        _nTemp = Sync.AddNode("온도 센서", TwinAuthority.Physical, _tempPrim);
        _robotBody = Physics.AddBody(new Vector3(0.8f, 0.6f, 0f), 0.15f, 0, default, kinematic: true);
        _propBody = Physics.AddBody(new Vector3(-1.4f, 0.2f, -1.2f), 0.2f, 3f);
        for (var k = 0; k < Balls; k++)
        {
            var p = new Vector3(-0.6f + 0.18f * k, 1.6f + 0.25f * k, 0.3f * MathF.Sin(k));
            _ballBody[k] = Physics.AddBody(p, 0.09f, 0.4f);
            _ballPrim[k] = l.AddPath("/World/Ball_" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), UsdSpec.Def, UsdPrimType.Sphere);
            l.SetRadius(_ballPrim[k], 0.09f); l.SetMass(_ballPrim[k], 0.4f); l.SetRigidBody(_ballPrim[k], true);
        }
        _anchorMarker = Live.AddAnchor(_markerPrim);
        _anchorProp = Live.AddAnchor(_propPrim);
        Sync.EditTwin(_nBox, new Vector3(-1.2f, 0.25f, 1.2f), Quaternion.Identity, 1);
        Calibrate();
        Live.Join("Omniverse USD Composer");
        Live.Join("Isaac Sim 작업자");
        LiveSession = true;
    }

    /// <summary>피두셜 5점(센서 측정 + 잡음) → Horn 캘리브레이션.</summary>
    public float Calibrate()
    {
        var inv = SensorToWorldTruth.Inverse();
        for (var i = 0; i < Fiducials.Length; i++) _fidSensor[i] = inv.Apply(Fiducials[i]) + Noise(SensorNoiseMm);
        var rms = Sync.Calibrate(_fidSensor, Fiducials);
        LastAction = "📐 캘리브레이션 RMS " + rms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " mm";
        return rms;
    }

    /// <summary>실물 로봇 궤적(월드 진실).</summary>
    public static Vector3 RobotTruth(float t) => new(0.8f * MathF.Cos(t * 0.9f), 0.45f + 0.25f * MathF.Sin(t * 1.7f), 0.8f * MathF.Sin(t * 0.9f));

    public void SetLive(bool on)
    {
        for (var i = 0; i < OmniverseLiveUsdSyncAnchorGuard.MaxSessions; i++) if (Live.Session(i) is not null) Live.SetConnected(i, on);
        LiveSession = on;
        LastAction = on ? "🟢 옴니버스 라이브 세션 연결(재동기화)" : "⚪ 라이브 세션 일시 중단";
        Raise();
    }

    /// <summary>가상 → 실물: 컨베이어 목표 이동(트윈 편집).</summary>
    public void CommandConveyor(Vector3 world)
    {
        Sync.EditTwin(_nBox, world, Quaternion.Identity, NowUs());
        LastAction = "🎯 컨베이어 목표 → (" + world.X.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ", " + world.Z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>강체 공 재투하.</summary>
    public void DropBalls()
    {
        lock (_gate)
        {
            Physics.ClearBodies();
            _robotBody = Physics.AddBody(RobotTruth(_time), 0.15f, 0, default, kinematic: true);
            _propBody = Physics.AddBody(Live.AnchorLocked(_anchorProp), 0.2f, 3f);
            for (var k = 0; k < Balls; k++) _ballBody[k] = Physics.AddBody(new Vector3(-0.6f + 0.18f * k, 1.6f + 0.25f * k, 0.3f * MathF.Sin(k)), 0.09f, 0.4f);
            LastAction = "⚪ 강체 재투하";
        }
    }

    /// <summary>유체 분사(노즐 · v16.81).</summary>
    public int SprayFluid(int count = 256) => Physics.Fluid.EmitNozzle(new Vector3(-1.5f, 1.2f, 0f), Vector3.Normalize(new Vector3(1f, 0.3f, 0f)), 0.06f, 3.5f, count, FluidPhase.Liquid, 6f);

    private static long NowUs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e6 / System.Diagnostics.Stopwatch.Frequency)); // 정수 곱 오버플로 회피

    /// <summary>1틱(무할당 · 세션 재동기화 제외).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            _time += dt;
            var now = NowUs();
            var toSensor = SensorToWorldTruth.Inverse();
            // ── 실물 센서 ──
            var robotW = RobotTruth(_time);
            Sync.SubmitPhysical(_nRobot, toSensor.Apply(robotW) + Noise(SensorNoiseMm), Quaternion.Identity, 0, now);
            SlamPose cam;
            if (UseSlam && _slam is { Frame: > 0 } sr) cam = sr.Slam.Pose;
            else cam = SlamPose.LookAt(new Vector3(2.5f * MathF.Cos(_time * 0.2f), 2.2f, 2.5f * MathF.Sin(_time * 0.2f)), Vector3.Zero);
            Sync.SubmitPhysical(_nCam, toSensor.Apply(cam.T) + Noise(SensorNoiseMm), Quaternion.Normalize(Quaternion.Conjugate(toSensor.Q) * cam.Q), 0, now);
            var temp = Physics.Weather.State.Temperature + 0.1f * Gauss();
            Sync.SubmitPhysical(_nTemp, toSensor.Apply(new Vector3(2f, 2f, 2f)), Quaternion.Identity, temp, now);
            // 컨베이어 실물: 액추에이터 명령 추종(1차 지연 τ = 0.5 s) → 센서 위치 보고는 하지 않음(가상 우선)
            if (Sync.TryTakeCommand(_nBox, out var cmd)) _conveyorTarget = SensorToWorldTruth.Apply(cmd);
            _conveyorPhys += (_conveyorTarget - _conveyorPhys) * Math.Clamp(dt / 0.5f, 0f, 1f);
            // ── 동기화 ──
            Sync.Sync();
            // ── 물리(로봇 트윈 = 운동학 강체) ──
            Physics.MoveKinematic(_robotBody, Sync.Twin(_nRobot), dt);
            Physics.Step(dt);
            for (var k = 0; k < Balls; k++)
            {
                var p = Physics.BodyPosition(_ballBody[k]);
                Layer.SetTranslate(_ballPrim[k], p.X, p.Y, p.Z);
            }
            // ── 앵커 락 ──
            var marker = Live.AnchorLocked(_anchorMarker) + Noise(MarkerJitterMm);
            Live.Propose(_anchorMarker, marker, AnchorSource.Sensor, out _);
            Live.Propose(_anchorProp, Physics.BodyPosition(_propBody), AnchorSource.Physics, out var propLocked);
            Physics.MoveKinematicIfDrifted(_propBody, propLocked);
            // ── 라이브 동기화 ──
            Live.Publish();
            Live.Pump();
            // ── 계측 ──
            var b = _seriesHead * UnifiedMultiPhysicsWorldDynamicsSynthesizer.Stages;
            for (var s = 0; s < UnifiedMultiPhysicsWorldDynamicsSynthesizer.Stages; s++) _series[b + s] = (float)Physics.StageUs((PhysicsStage)s);
            _errSeries[_seriesHead] = Sync.MeanErrorMm;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private Vector3 _conveyorTarget = new(-1.2f, 0.25f, 1.2f);

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Twin" };
            _loop.Start();
            LastAction = "▶ 디지털 트윈 가동 · " + Hz + " Hz";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 디지털 트윈 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Twin.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            if (n % 3 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Twin.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
