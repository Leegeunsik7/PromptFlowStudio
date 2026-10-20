using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Twin;

/// <summary>
/// [PromptFlow Studio v16.73.0] ♊ AI 디지털 트윈 &amp; 합성 데이터 스튜디오 러너(30Hz · <see cref="TickOnce"/> 무할당).
/// 데모 물리 현장(지게차 원궤도 · 드론 8자 · 추종 로봇 · 환경 스테이션)이 물리 좌표계로 TWN1 패킷 송신 → 손실/지연/역순 네트워크 모델 →
/// 트윈 수신 · 데드레커닝 → USD 레이어(twin.usda over factory.usda 합성) · 3DGS 바인딩 · 합성 장면 →
/// 가상 센서(라이다/조도/거리/IMU) → PID(램프 조광 · 로봇 추종) → 트윈 명령 패킷 → 물리 로봇 이동(폐루프) · 합성 데이터 큐 3프레임당 1장.
/// </summary>
public sealed class DigitalTwinStudioRunner : IDisposable
{
    public const int Hz = 30, NetCapacity = 128;
    public const ushort ForkliftId = 101, DroneId = 102, RobotId = 103, StationId = 104;

    public const string FactoryUsda = """
#usda 1.0
(
    defaultPrim = "Factory"
    upAxis = "Y"
)

def Xform "Factory"
{
    def Cube "RackA"
    {
        double size = 2
        color3f[] primvars:displayColor = [(0.45, 0.47, 0.52)]
        double3 xformOp:translate = (-7, 1, -7)
    }
    def Cube "RackB"
    {
        double size = 2
        color3f[] primvars:displayColor = [(0.45, 0.47, 0.52)]
        double3 xformOp:translate = (7, 1, -7)
    }
    def Cube "Pallet"
    {
        double size = 0.8
        color3f[] primvars:displayColor = [(0.62, 0.45, 0.25)]
        double3 xformOp:translate = (6, 0.4, 6)
    }
}
""";

    private readonly object _gate = new();
    private readonly RealtimeDigitalTwinPhysicalSyncEngine _twin;
    private readonly NeuralSyntheticDataGeneratorEngine _gen;
    private readonly AutonomousSensorFeedbackLoopSimulator _sens;
    private readonly SyntheticScene _scene = new();
    private readonly UsdLayer _layer = new("twin.usda"), _factory = new("factory.usda");
    private readonly OpenUsdSpatialAssetCompositorEngine _usd = new();
    private readonly GaussianScene _splats = new(512);
    private readonly byte[] _net = new byte[NetCapacity * RealtimeDigitalTwinPhysicalSyncEngine.PacketSize];
    private readonly long[] _netRelease = new long[NetCapacity];
    private readonly uint[] _seq = new uint[4];
    private readonly int[] _node = new int[4];
    private int _netCount;
    private CancellationTokenSource? _cts;
    private long _frame, _sentPackets, _lostPackets, _cmdReceived;
    private ulong _rng = 0x7A1B_1673UL;
    private Vector3 _fork, _drone, _robot, _robotTarget, _station, _forkCenter, _droneCenter;
    private bool _robotHasTarget, _robotFollow = true;
    private SimLight _light;
    private int _lidar, _lux, _range, _imu, _lampLoop, _followLoop;
    private FolderDatasetSink? _sinkOut;

    public DigitalTwinStudioRunner(IServiceProvider sp)
        : this((RealtimeDigitalTwinPhysicalSyncEngine?)sp.GetService(typeof(RealtimeDigitalTwinPhysicalSyncEngine)) ?? new RealtimeDigitalTwinPhysicalSyncEngine(),
               (NeuralSyntheticDataGeneratorEngine?)sp.GetService(typeof(NeuralSyntheticDataGeneratorEngine)) ?? new NeuralSyntheticDataGeneratorEngine(),
               (AutonomousSensorFeedbackLoopSimulator?)sp.GetService(typeof(AutonomousSensorFeedbackLoopSimulator)) ?? new AutonomousSensorFeedbackLoopSimulator()) { }

    public DigitalTwinStudioRunner(RealtimeDigitalTwinPhysicalSyncEngine twin, NeuralSyntheticDataGeneratorEngine gen, AutonomousSensorFeedbackLoopSimulator sens)
    {
        _twin = twin; _gen = gen; _sens = sens;
        Setup();
    }

    public event Action? OnChange;

    public RealtimeDigitalTwinPhysicalSyncEngine Twin => _twin;
    public NeuralSyntheticDataGeneratorEngine Generator => _gen;
    public AutonomousSensorFeedbackLoopSimulator Sensors => _sens;
    public SyntheticScene Scene => _scene;
    public OpenUsdSpatialAssetCompositorEngine Compositor => _usd;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public long NowMicros => Frame * 1_000_000 / Hz;
    public long SentPackets => Interlocked.Read(ref _sentPackets);
    public long LostPackets => Interlocked.Read(ref _lostPackets);
    public long CommandsReceived => Interlocked.Read(ref _cmdReceived);
    public float LossRate { get; set; } = 0.03f;
    public int MaxDelayFrames { get; set; } = 3;
    public bool RobotFollow { get { lock (_gate) return _robotFollow; } set { lock (_gate) { _robotFollow = value; _sens.SetLoopEnabled(_followLoop, value); } } }
    public bool Exporting => _sinkOut is not null;
    public string? ExportRoot => _sinkOut?.Root;
    public int LidarSensor => _lidar;
    public int LuxSensor => _lux;
    public int RangeSensor => _range;
    public int ImuSensor => _imu;
    public int LampLoop => _lampLoop;
    public int FollowLoop => _followLoop;
    public float LampLux1m { get { lock (_gate) return _light.LampLux1m; } }
    public Vector3 LampPosition => _light.LampPos;
    public int NodeIndex(ushort id) => _twin.IndexOf(id);
    /// <summary>물리 현장 참값(가상 좌표 · 검증/UI 비교용).</summary>
    public Vector3 TruthVirtual(ushort id)
    {
        lock (_gate)
            return _twin.ToVirtual(id switch { ForkliftId => _fork, DroneId => _drone, RobotId => _robot, _ => _station });
    }
    public string? LastAction { get; private set; }

    private void Setup()
    {
        _twin.SetCalibration(90f, 1f, new Vector3(2f, 0f, -3f));
        // 합성 장면: 트윈 노드 + 고정 설비
        var fork = _scene.Add(SimShape.Box, 2, Vector3.Zero, new Vector3(0.9f, 0.6f, 0.9f), new Vector3(0.95f, 0.7f, 0.1f));
        var drone = _scene.Add(SimShape.Sphere, 4, Vector3.Zero, new Vector3(0.35f), new Vector3(0.2f, 0.85f, 0.95f));
        var robot = _scene.Add(SimShape.Box, 3, Vector3.Zero, new Vector3(0.45f, 0.5f, 0.45f), new Vector3(0.85f, 0.25f, 0.3f));
        var station = _scene.Add(SimShape.Box, 7, Vector3.Zero, new Vector3(0.3f, 0.75f, 0.3f), new Vector3(0.6f, 0.6f, 0.95f));
        _scene.Add(SimShape.Box, 6, new Vector3(-7, 1, -7), new Vector3(1f), new Vector3(0.45f, 0.47f, 0.52f));
        _scene.Add(SimShape.Box, 6, new Vector3(7, 1, -7), new Vector3(1f), new Vector3(0.45f, 0.47f, 0.52f));
        _scene.Add(SimShape.Box, 5, new Vector3(6, 0.4f, 6), new Vector3(0.4f), new Vector3(0.62f, 0.45f, 0.25f));
        _scene.Add(SimShape.Sphere, 1, new Vector3(-3, 0.9f, 4), new Vector3(0.45f), new Vector3(0.9f, 0.75f, 0.6f));

        _node[0] = _twin.RegisterNode(ForkliftId, "Forklift", "지게차 AGV", SimShape.Box, 2, new Vector3(0.9f, 0.6f, 0.9f), new Vector3(0.95f, 0.7f, 0.1f));
        _node[1] = _twin.RegisterNode(DroneId, "Drone", "점검 드론", SimShape.Sphere, 4, new Vector3(0.35f), new Vector3(0.2f, 0.85f, 0.95f));
        _node[2] = _twin.RegisterNode(RobotId, "Robot", "추종 로봇", SimShape.Box, 3, new Vector3(0.45f, 0.5f, 0.45f), new Vector3(0.85f, 0.25f, 0.3f));
        _node[3] = _twin.RegisterNode(StationId, "EnvStation", "환경 스테이션", SimShape.Box, 7, new Vector3(0.3f, 0.75f, 0.3f), new Vector3(0.6f, 0.6f, 0.95f));
        _twin.BindSceneObject(_node[0], _scene, fork);
        _twin.BindSceneObject(_node[1], _scene, drone);
        _twin.BindSceneObject(_node[2], _scene, robot);
        _twin.BindSceneObject(_node[3], _scene, station);
        _twin.BindUsd(_layer, _layer.AddPrim(-1, "Twin", UsdSpec.Def, UsdPrimType.Xform));
        UsdaReader.Parse(Encoding.UTF8.GetBytes(FactoryUsda), _factory);
        _usd.SetLayer(1, _factory);
        _usd.SetLayer(0, _layer);

        // 물리 현장(물리 좌표계)
        _forkCenter = _twin.ToPhysical(new Vector3(0, 0.6f, 0));
        _droneCenter = _twin.ToPhysical(new Vector3(0, 4f, 0));
        _robot = _twin.ToPhysical(new Vector3(-9, 0.5f, 8));
        _station = _twin.ToPhysical(new Vector3(9, 0.75f, 9));
        _fork = _forkCenter + new Vector3(6, 0, 0);
        _drone = _droneCenter;
        _scene.SetPosition(fork, _twin.ToVirtual(_fork)); _scene.SetPosition(drone, _twin.ToVirtual(_drone));
        _scene.SetPosition(robot, _twin.ToVirtual(_robot)); _scene.SetPosition(station, _twin.ToVirtual(_station));

        _light = new SimLight { SunDir = Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), SunLux = 0, AmbientLux = 40, LampPos = new Vector3(0, 6, 0), LampLux1m = 4000 };
        _lidar = _sens.AddSensor("로봇 라이다", SensorKind.Lidar, robot, new Vector3(0, 0.6f, 0), noiseSigma: 0.02f, latencyMs: 33, range: 20);
        _lux = _sens.AddSensor("작업대 조도계", SensorKind.Lux, -1, new Vector3(2, 1, 2), noiseSigma: 5f, latencyMs: 100);
        _range = _sens.AddSensor("로봇→지게차 거리", SensorKind.Range, robot, Vector3.Zero, noiseSigma: 0.03f, latencyMs: 66, dropout: 0.02f, target: fork);
        _imu = _sens.AddSensor("드론 IMU", SensorKind.Imu, drone, Vector3.Zero, noiseSigma: 0.05f);
        _lampLoop = _sens.AddLoop("램프 조광 → 500 lux", _lux, 500, 0.5f, 25f, 0, 0, 20000, 15);
        _followLoop = _sens.AddLoop("로봇 추종 → 3.0 m", _range, 3f, -0.8f, -0.05f, 0, -1.5f, 1.5f, 0.3f);

        _gen.Enqueue("bootstrap", 48, 1673, RandomizationRanges.Default);
    }

    /// <summary>1프레임: 현장 → 네트워크 → 트윈 → USD/3DGS → 센서/PID → 명령 → 합성 데이터.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            const float dt = 1f / Hz;
            var f = _frame;
            var now = f * 1_000_000 / Hz;
            using (ZeroGcRegionScope.Enter("Twin.Runner.Tick"))
            {
                // 1) 물리 현장
                var t = f * dt;
                _fork = _forkCenter + new Vector3(6 * MathF.Cos(t * 0.15f), 0, 6 * MathF.Sin(t * 0.15f));
                _drone = _droneCenter + new Vector3(4 * MathF.Sin(t * 0.3f), 0.5f * MathF.Sin(t * 0.9f), 2.5f * MathF.Sin(t * 0.6f));
                if (_robotHasTarget)
                {
                    var to = _robotTarget - _robot; to.Y = 0;
                    var d = to.Length();
                    var step = 1.5f * dt;
                    _robot += d <= step ? to : to / d * step;
                }
                // 2) 송신(포즈 30Hz · 환경 스테이션 포즈+환경 2Hz)
                Span<byte> pkt = stackalloc byte[RealtimeDigitalTwinPhysicalSyncEngine.PacketSize];
                Send(0, ForkliftId, TwinPacketKind.Pose, _fork, now, pkt);
                Send(1, DroneId, TwinPacketKind.Pose, _drone, now, pkt);
                Send(2, RobotId, TwinPacketKind.Pose, _robot, now, pkt);
                if (f % 15 == 0) Send(3, StationId, TwinPacketKind.Pose, _station, now, pkt);
                // 3) 네트워크 배달
                for (var k = 0; k < _netCount;)
                {
                    if (_netRelease[k] > f) { k++; continue; }
                    _twin.Ingest(_net.AsSpan(k * RealtimeDigitalTwinPhysicalSyncEngine.PacketSize, RealtimeDigitalTwinPhysicalSyncEngine.PacketSize), now);
                    var last = --_netCount;
                    if (k != last)
                    {
                        _net.AsSpan(last * RealtimeDigitalTwinPhysicalSyncEngine.PacketSize, RealtimeDigitalTwinPhysicalSyncEngine.PacketSize).CopyTo(_net.AsSpan(k * RealtimeDigitalTwinPhysicalSyncEngine.PacketSize));
                        _netRelease[k] = _netRelease[last];
                    }
                }
                // 4) 가상 편집 → 명령 · 5) 트윈 동기화 → USD/3DGS/장면
                _twin.SyncFromUsd(now);
                _twin.Tick(now);
                _usd.Compose();
                _usd.BindToGaussians(_splats);
                // 6) 센서 · PID
                _sens.Step(_scene, _light, dt, now);
                _light.LampLux1m = _sens.Output(_lampLoop);
                if (_robotFollow && f % 6 == 0 && _sens.HasValue(_range))
                {
                    var rn = _node[2];
                    var rv = _twin.Filtered(rn);
                    var to = _twin.Filtered(_node[0]) - rv; to.Y = 0;
                    var len = to.Length();
                    if (len > 1e-3f) _twin.Command(rn, rv + to / len * (_sens.Output(_followLoop) * 0.4f), now);
                }
                // 7) 명령 수신(물리 현장)
                while (_twin.TryDequeueCommand(pkt))
                {
                    if (!RealtimeDigitalTwinPhysicalSyncEngine.TryDecode(pkt, out var cmd)) continue;
                    _cmdReceived++;
                    if (cmd.NodeId == RobotId) { _robotTarget = cmd.Position; _robotHasTarget = true; }
                }
            }
            // 8) 합성 데이터(싱크 기록은 가드 밖)
            if (f % 3 == 0) _gen.ProcessNext(_scene, 1);
            _frame = f + 1;
        }
    }

    private void Send(int slot, ushort id, TwinPacketKind kind, Vector3 pos, long now, Span<byte> pkt)
    {
        var env = id == StationId;
        var tt = now / 1e6f;
        RealtimeDigitalTwinPhysicalSyncEngine.Encode(new TwinPacket(kind, id, ++_seq[slot], now, pos, Quaternion.Identity,
            env ? 22f + 1.5f * MathF.Sin(tt * 0.05f) : float.NaN, env ? 45f + 5f * MathF.Sin(tt * 0.03f) : float.NaN, env ? 300f + 50f * MathF.Sin(tt * 0.2f) : float.NaN), pkt);
        _sentPackets++;
        if (Rand() < LossRate || _netCount >= NetCapacity) { _lostPackets++; return; }
        pkt.CopyTo(_net.AsSpan(_netCount * RealtimeDigitalTwinPhysicalSyncEngine.PacketSize));
        _netRelease[_netCount] = _frame + 1 + (long)(Rand() * Math.Max(0, MaxDelayFrames));
        _netCount++;
    }

    private float Rand()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; _rng = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    // ══════════════════════ 제어 평면 ══════════════════════

    /// <summary>가상(USD) 편집 시뮬레이션: 노드 프림 translate 를 이동 → 다음 틱에 편집 감지 → 물리 명령.</summary>
    public void NudgeVirtual(ushort id, float dx, float dz)
    {
        lock (_gate)
        {
            var n = _twin.IndexOf(id);
            if (n < 0 || _twin.PrimOf(n) < 0) return;
            var (x, y, z) = _layer.Translate(_twin.PrimOf(n));
            _layer.SetTranslate(_twin.PrimOf(n), x + dx, y, z + dz);
            if (id == RobotId) { _robotFollow = false; _sens.SetLoopEnabled(_followLoop, false); }
        }
        LastAction = "✋ 가상 편집 → 물리 명령 · " + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public int EnqueueJob(string name, int count, ulong seed, byte weatherMask)
    {
        var r = RandomizationRanges.Default;
        r.WeatherMask = weatherMask;
        var j = _gen.Enqueue(name, count, seed, r);
        LastAction = j >= 0 ? _gen.LastAction : "⚠ 렌더 큐 가득";
        Raise();
        return j;
    }

    /// <summary>데이터셋 파일 출력 토글(%LOCALAPPDATA%\PromptFlowStudio\datasets\{시각}).</summary>
    public void SetExport(bool on)
    {
        lock (_gate)
        {
            if (on && _sinkOut is null)
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "datasets",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture));
                _sinkOut = new FolderDatasetSink(root);
                _gen.Sink = _sinkOut;
                LastAction = "💾 데이터셋 기록 · " + root;
            }
            else if (!on && _sinkOut is not null)
            {
                _gen.Sink = null;
                _sinkOut.Dispose();
                LastAction = "💾 기록 종료 · " + _sinkOut.Written.ToString(System.Globalization.CultureInfo.InvariantCulture) + "장";
                _sinkOut = null;
            }
        }
        Raise();
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 트윈 루프 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 트윈 루프 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Twin.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Twin.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        SetExport(false);
    }
}
