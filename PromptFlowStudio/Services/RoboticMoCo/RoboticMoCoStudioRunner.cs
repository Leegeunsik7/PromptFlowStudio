using System.Numerics;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.Cinematography;
using PromptFlowStudio.Services.GenerativeVfx;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.RoboticMoCo;

/// <summary>
/// [PromptFlow Studio v17.01.0] 🤖 AI 로보틱 MoCo &amp; 물리 스테이지 HIL 스튜디오 러너(100 Hz 전용 스레드 · 시뮬레이션 시각 결정적).
/// MoCo 패스(반복 가능 프로그램 아크 또는 v16.85 카메라 궤적을 로봇 도달 구 안으로 축소 매핑) → IK 목표 → HIL 가드(하트비트 · SSM · SLAM 평면 · 침범 불가 구역) →
/// 저크 제한 추종 또는 동기 정지 → DMX 40 Hz(v16.84/v16.81 결선 · E-Stop 안전 상태 · 턴테이블 = 패스 동기).
/// 배우(피사체) · 스태프(진입 시나리오)는 인프로세스 시뮬레이션 · 실물 로봇/DMX 노드 없음(Art-Net 루프백).
/// </summary>
public sealed class RoboticMoCoStudioRunner : IDisposable
{
    public const int Hz = 100, DmxHz = 40, SeriesLen = 200, PassSamples = 400;
    public static readonly Vector3 ActorHome = new(2.3f, 0f, 0.2f);
    public static readonly Vector3 StaffHome = new(-1.6f, 0f, 2.2f);

    private readonly object _gate = new();
    private readonly NeuralCameraTrajectoryEngine? _cam;
    private readonly SpatialSlamStudioRunner? _slam;
    private readonly CameraPose[] _pass = new CameraPose[PassSamples];
    private readonly float[] _sep = new float[SeriesLen], _ov = new float[SeriesLen], _tcp = new float[SeriesLen], _load = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private double _now;
    private float _dmxAcc, _staffT = -1f, _actorT = -1f;
    private Vector3 _actor = ActorHome, _staff = StaffHome, _actorPrev = ActorHome, _staffPrev = StaffHome;

    public RoboticMoCoStudioRunner(IServiceProvider sp)
        : this((RoboticMoCoInverseKinematicsEngine?)sp.GetService(typeof(RoboticMoCoInverseKinematicsEngine)) ?? new RoboticMoCoInverseKinematicsEngine(),
               (PhysicalStageDmxActuatorSynthesizer?)sp.GetService(typeof(PhysicalStageDmxActuatorSynthesizer)) ?? new PhysicalStageDmxActuatorSynthesizer(),
               (HardwareInTheLoopSafetyEStopGuard?)sp.GetService(typeof(HardwareInTheLoopSafetyEStopGuard)) ?? new HardwareInTheLoopSafetyEStopGuard(),
               sp.GetService(typeof(NeuralCameraTrajectoryEngine)) as NeuralCameraTrajectoryEngine,
               sp.GetService(typeof(SpatialSlamStudioRunner)) as SpatialSlamStudioRunner)
    {
        Stage.Bind(sp.GetService(typeof(AtmosphericScatteringLightingEngine)) as AtmosphericScatteringLightingEngine,
                   sp.GetService(typeof(DynamicMicroWeatherSimulatorEngine)) as DynamicMicroWeatherSimulatorEngine,
                   sp.GetService(typeof(InteractiveVfxCollisionSplashGuard)) as InteractiveVfxCollisionSplashGuard);
    }

    public RoboticMoCoStudioRunner(RoboticMoCoInverseKinematicsEngine robot, PhysicalStageDmxActuatorSynthesizer stage, HardwareInTheLoopSafetyEStopGuard guard,
                                   NeuralCameraTrajectoryEngine? cam = null, SpatialSlamStudioRunner? slam = null)
    {
        Robot = robot; Stage = stage; Guard = guard; _cam = cam; _slam = slam;
        Build();
    }

    public event Action? OnChange;

    public RoboticMoCoInverseKinematicsEngine Robot { get; }
    public PhysicalStageDmxActuatorSynthesizer Stage { get; }
    public HardwareInTheLoopSafetyEStopGuard Guard { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double Now => _now;
    public double LastTickMs { get; private set; }
    public bool UseCameraEngine { get; set; }
    public bool CameraBound => _cam is not null;
    public bool HeartbeatFault { get; set; }
    public float TurntableRpm { get; set; } = 2f;
    public bool SlamPlanes { get; private set; }
    public string? LastAction { get; private set; }
    public Vector3 Actor => _actor;
    public Vector3 Staff => _staff;
    public int SeriesCount => _seriesCount;
    public float SeparationSeries(int i) => _sep[Idx(i)];
    public float OverrideSeries(int i) => _ov[Idx(i)];
    public float TcpSeries(int i) => _tcp[Idx(i)];
    public float LoadSeries(int i) => _load[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    private void Build()
    {
        var s = Stage;
        s.Patch("키 무빙헤드 L", FixtureKind.MovingHead, 0, 1, new Vector3(1.0f, 3.2f, 2.0f));
        s.Patch("키 무빙헤드 R", FixtureKind.MovingHead, 0, 13, new Vector3(3.5f, 3.2f, 1.8f));
        for (var i = 0; i < 4; i++) s.Patch("필 파", FixtureKind.ParRgbw, 0, 25 + i * 6, new Vector3(0.5f + i * 0.9f, 2.8f, 2.6f));
        for (var i = 0; i < 4; i++) s.Patch("사이클로라마 파", FixtureKind.ParRgbw, 1, 1 + i * 6, new Vector3(-0.5f + i * 1.2f, 0.2f, -2.2f));
        s.Patch("번개 스트로브", FixtureKind.Strobe, 1, 41, new Vector3(2f, 3.4f, 0f));
        s.Patch("포그 머신", FixtureKind.FogMachine, 2, 1, new Vector3(3.5f, 0f, -1.5f));
        s.Patch("윈드 팬", FixtureKind.WindFan, 2, 5, new Vector3(4f, 0.8f, 0.5f));
        s.Patch("CO2 제트", FixtureKind.Co2Jet, 2, 9, new Vector3(3.2f, 0f, 1.5f));
        s.Patch("턴테이블", FixtureKind.Turntable, 2, 17, ActorHome);
        s.Patch("워크 라이트", FixtureKind.WorkLight, 3, 1, new Vector3(0f, 3.5f, 0f));
        LoadPlanes();
        Guard.AddKeepOut(new Vector3(2.0f, 0f, -0.15f), new Vector3(2.6f, 2.0f, 0.55f));   // 배우 마크
        Guard.AddKeepOut(new Vector3(-1.2f, 0f, -1.6f), new Vector3(-0.6f, 2.2f, -1.0f));  // 모니터 크루석
        Guard.SetPerson(0, _actor, Vector3.Zero, 0.25f, 1.8f, PersonRole.Actor);
        Guard.SetPerson(1, _staff, Vector3.Zero, 0.25f, 1.8f, PersonRole.Staff);
        BuildPass();
        Robot.AnalyzePass(_pass, 1f / Hz);
        Span<float> home = stackalloc float[RoboticMoCoInverseKinematicsEngine.Joints];
        Robot.Solve(_pass[0], stackalloc float[RoboticMoCoInverseKinematicsEngine.Joints], home);
        Robot.Teleport(home);
        LastAction = "📋 패스 사전 검증 " + (Robot.PassFeasible ? "통과" : "주의");
    }

    /// <summary>v16.98 SLAM 바닥이 있으면 가져오고 없으면 기본 무대(바닥 · 뒷벽 · 천장).</summary>
    public void LoadPlanes()
    {
        if (_slam is { } sr && sr.Guard.FindKind(PlaneKind.Floor) >= 0 && Guard.ImportSlamPlanes(sr.Guard) > 0) { SlamPlanes = true; return; }
        Guard.ClearPlanes();
        Guard.AddPlane(Vector3.UnitY, 0f, PlaneKind.Floor);
        Guard.AddPlane(Vector3.UnitZ, 2.5f, PlaneKind.Wall);
        Guard.AddPlane(-Vector3.UnitY, 3.6f, PlaneKind.Ceiling);
        SlamPlanes = false;
    }

    /// <summary>반복 가능 MoCo 프로그램 아크(4 s 주기) — 배우 머리 조준.</summary>
    public static CameraPose ProgramPose(double t, Vector3 actor)
    {
        var w = (float)(t * MathF.Tau / 4.0);
        var eye = new Vector3(0.9f + 0.15f * MathF.Sin(w), 1.1f + 0.25f * MathF.Sin(0.5f * w), 0.55f * MathF.Cos(w));
        return RoboticMoCoInverseKinematicsEngine.LookAt(eye, actor + new Vector3(0f, 1.55f, 0f), 4f * MathF.Sin(w));
    }

    private void BuildPass()
    {
        for (var i = 0; i < PassSamples; i++) _pass[i] = ProgramPose(i / (double)Hz, ActorHome);
    }

    /// <summary>v16.85 궤적 → 로봇 도달 구: 피사체 상대 방향을 로봇 쪽 반구로 접고 반경 1.3 m로 축소(미니어처 MoCo).</summary>
    private CameraPose CameraEnginePose(float dt)
    {
        var cam = _cam!;
        cam.Step(dt, _actor, (_actor - _actorPrev) * Hz, _actor + new Vector3(0f, 1.5f, 0f));
        var p = cam.Pose;
        var head = _actor + new Vector3(0f, 1.55f, 0f);
        var rel = p.Position - head;
        if (rel.X > 0f) rel.X = -rel.X;
        if (rel.LengthSquared() < 1e-6f) rel = -Vector3.UnitX;
        rel = Vector3.Normalize(rel) * 1.3f;
        rel.Y = Math.Clamp(rel.Y, -0.5f, 0.3f);
        return RoboticMoCoInverseKinematicsEngine.LookAt(head + rel, head, p.RollDeg);
    }

    public void StaffEnter() { _staffT = 0f; LastAction = "🚶 스태프 작업 셀 진입"; }
    public void ActorApproach() { _actorT = 0f; LastAction = "🎭 배우 카메라 접근"; }
    public void Lightning() { Stage.Pulse(1f, false); LastAction = "⚡ 번개 플래시(스트로브)"; }
    public void Explosion() { Stage.Pulse(0.9f, true); LastAction = "💥 VFX 폭발 → CO2 펄스"; }

    public void EStop()
    {
        lock (_gate) Guard.ManualStop(Robot);
        LastAction = "🛑 수동 E-Stop";
        Raise();
    }

    public bool Reset()
    {
        bool ok;
        lock (_gate) ok = Guard.Reset(Robot, _now);
        LastAction = ok ? "🔓 E-Stop 리셋 · 추종 재개" : "⛔ 리셋 거부(위반/고장/정지 미완료)";
        Raise();
        return ok;
    }

    /// <summary>1틱(무할당 · 10 ms).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            _now += dt;
            // ── 인원 ──
            _actorPrev = _actor; _staffPrev = _staff;
            var sway = new Vector3(0.05f * MathF.Sin((float)_now * 0.7f), 0f, 0.08f * MathF.Sin((float)_now * 0.45f));
            if (_actorT >= 0f)
            {
                _actorT += dt;
                var k = _actorT < 3f ? _actorT / 3f : _actorT < 6f ? 1f - (_actorT - 3f) / 3f : 0f;
                _actor = ActorHome + sway + new Vector3(-0.75f * k, 0f, 0f);
                if (_actorT >= 6f) _actorT = -1f;
            }
            else _actor = ActorHome + sway;
            if (_staffT >= 0f)
            {
                _staffT += dt;
                var a = new Vector3(0.45f, 0f, 0.4f);   // 로봇 페데스탈 옆 작업 지점
                var k = _staffT < 2.5f ? _staffT / 2.5f : _staffT < 5f ? 1f : _staffT < 7.5f ? 1f - (_staffT - 5f) / 2.5f : 0f;
                _staff = Vector3.Lerp(StaffHome, a, k);
                if (_staffT >= 7.5f) _staffT = -1f;
            }
            Guard.SetPerson(0, _actor, (_actor - _actorPrev) * Hz, 0.25f, 1.8f, PersonRole.Actor);
            Guard.SetPerson(1, _staff, (_staff - _staffPrev) * Hz, 0.25f, 1.8f, PersonRole.Staff);
            // ── MoCo 목표 ──
            var pose = UseCameraEngine && _cam is not null ? CameraEnginePose(dt) : ProgramPose(_now, _actor);
            Robot.SetTarget(pose);
            // ── HIL 가드 → 모션 ──
            if (!HeartbeatFault) Guard.Heartbeat(_now);
            var prevState = Guard.State;
            Guard.Evaluate(Robot, _now);
            if (Guard.StopActive) Robot.ExecuteStop(Guard.Decel, dt);
            else { Robot.SpeedOverride = Guard.Override; Robot.Step(dt); }
            if (Guard.State != prevState && Guard.StopActive) _stopNote = true;
            // ── DMX 40 Hz ──
            _dmxAcc += dt;
            if (_dmxAcc >= 1f / DmxHz)
            {
                var fdt = _dmxAcc; _dmxAcc = 0f;
                Stage.SafeState = Guard.State == SafetyState.EmergencyStop;
                Stage.PullFromEngines();
                var cue = Stage.Cue;
                cue.AimTarget = _actor + new Vector3(0f, 1.4f, 0f);
                cue.TurntableRpm = Guard.StopActive ? 0f : TurntableRpm * Guard.Override;
                Stage.SetCue(cue);
                Stage.Render(fdt, (float)_now);
            }
            // ── 계측 ──
            var load = 0f;
            for (var j = 0; j < RoboticMoCoInverseKinematicsEngine.Joints; j++) load = MathF.Max(load, Robot.Load(j));
            _sep[_seriesHead] = float.IsInfinity(Guard.MinSeparation) ? 5f : Guard.MinSeparation;
            _ov[_seriesHead] = Guard.Override; _tcp[_seriesHead] = Robot.TcpSpeed; _load[_seriesHead] = load;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private bool _stopNote;

    /// <summary>최근 동작(정지 전이는 틱에서 플래그만 → 조회 시 지연 포맷).</summary>
    public string? Action
    {
        get
        {
            if (_stopNote)
            {
                _stopNote = false;
                LastAction = "🛑 " + HardwareInTheLoopSafetyEStopGuard.StateNames[(int)Guard.State] + " · " + HardwareInTheLoopSafetyEStopGuard.CauseNames[(int)Guard.Cause];
            }
            return LastAction;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.MoCo" };
            _loop.Start();
            LastAction = "▶ MoCo 리그 가동 · " + Hz + " Hz · DMX " + DmxHz + " Hz";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ MoCo 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("MoCo.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            if (n % 10 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MoCo.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
