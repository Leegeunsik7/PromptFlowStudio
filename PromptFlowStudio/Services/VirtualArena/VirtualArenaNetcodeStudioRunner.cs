namespace PromptFlowStudio.Services.VirtualArena;

/// <summary>
/// [PromptFlow Studio v17.03.0] 🏟️ 가상 아레나 &amp; 롤백 넷코드 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 시각 결정적).
/// 롤백: 로컬 피어(P0) + 원격 3 피어 봇 입력 → 지연/지터/손실 UDP 모사(미확인 입력 중복 재전송) → 늦은 입력 롤백 · 재시뮬 →
/// 권위 기준 엔진(확정 입력만 실행)과 확정 프레임 체크섬 대조(디싱크 검출).
/// AOI: 20 Hz · 시청자 0 = 로컬 플레이어 시점(롤백 아레나 → 무대 중앙 매핑). 클록/모션 가드: 같은 네트워크 조건으로 16 원격 아바타.
/// </summary>
public sealed class VirtualArenaNetcodeStudioRunner : IDisposable
{
    public const int Hz = 60, AoiEvery = 3, SeriesLen = 240, MaxPackets = 1024;

    private struct InputPacket { public double At; public int Player, From, To; }

    private readonly object _gate = new();
    private readonly InputPacket[] _flight = new InputPacket[MaxPackets];
    private readonly float[] _depth = new float[SeriesLen], _rtt = new float[SeriesLen], _clock = new float[SeriesLen], _kbps = new float[SeriesLen], _pred = new float[SeriesLen];
    private int _flightN, _seriesHead, _seriesCount, _localFed = -1, _lastChecked = -1;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private double _spikeUntil = -1, _stormUntil = -1;
    private ulong _rng = 0x9FB21C651E98DF25UL;
    private bool _desyncNote;

    public VirtualArenaNetcodeStudioRunner(IServiceProvider sp)
        : this((DeterministicRollbackNetcodeEngine?)sp.GetService(typeof(DeterministicRollbackNetcodeEngine)) ?? new DeterministicRollbackNetcodeEngine(),
               (MassiveSpatialInterestManagementSynthesizer?)sp.GetService(typeof(MassiveSpatialInterestManagementSynthesizer)) ?? new MassiveSpatialInterestManagementSynthesizer(),
               (ClientSpatialPredictionClockSyncGuard?)sp.GetService(typeof(ClientSpatialPredictionClockSyncGuard)) ?? new ClientSpatialPredictionClockSyncGuard())
    {
    }

    public VirtualArenaNetcodeStudioRunner(DeterministicRollbackNetcodeEngine rollback, MassiveSpatialInterestManagementSynthesizer aoi, ClientSpatialPredictionClockSyncGuard guard)
    {
        Rollback = rollback; Aoi = aoi; Guard = guard;
        Reference = new DeterministicRollbackNetcodeEngine(-1, rollback.Players) { PredictionLimit = 0 };
        LastAction = "📋 대기 · 4 피어 롤백 · AOI " + aoi.Count + " 엔티티";
    }

    public event Action? OnChange;

    public DeterministicRollbackNetcodeEngine Rollback { get; }
    /// <summary>권위 기준(확정 입력만 실행) — 디싱크 대조용.</summary>
    public DeterministicRollbackNetcodeEngine Reference { get; }
    public MassiveSpatialInterestManagementSynthesizer Aoi { get; }
    public ClientSpatialPredictionClockSyncGuard Guard { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double Now => _tick / (double)Hz;
    public double LastTickMs { get; private set; }
    public float RttMs { get; set; } = 80f;
    public float JitterMs { get; set; } = 6f;
    public float LossPct { get; set; } = 2f;
    public bool SpikeActive => Now < _spikeUntil;
    public bool StormActive => Now < _stormUntil;
    public float EffectiveRttMs => RttMs + (SpikeActive ? 220f : 0f);
    public float EffectiveLoss => Math.Clamp(LossPct / 100f + (StormActive ? 0.3f : 0f), 0f, 0.9f);
    public long SyncChecks { get; private set; }
    public long Desyncs { get; private set; }
    public long InputPacketsSent { get; private set; }
    public long InputPacketsLost { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    public float DepthSeries(int i) => _depth[Idx(i)];
    public float RttSeries(int i) => _rtt[Idx(i)];
    public float ClockSeries(int i) => _clock[Idx(i)];
    public float KbpsSeries(int i) => _kbps[Idx(i)];
    public float PredictedSeries(int i) => _pred[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    public void Spike() { _spikeUntil = Now + 3.0; LastAction = "📈 지연 스파이크 +220 ms · 3 s"; Raise(); }
    public void Storm() { _stormUntil = Now + 3.0; LastAction = "🌩 패킷 드롭 폭풍 +30% · 3 s"; Raise(); }
    public void FlashMob() { lock (_gate) Aoi.Rush(MassiveSpatialInterestManagementSynthesizer.Center + 30f, MassiveSpatialInterestManagementSynthesizer.Center - 20f); LastAction = "👥 플래시몹 · 관객 군집 이동"; Raise(); }
    public void ClockJump() { lock (_gate) Guard.ClockJump(0.12); LastAction = "⏰ 클라이언트 클록 +120 ms 점프"; Raise(); }
    public void SetInputDelay(int frames) { lock (_gate) Rollback.InputDelay = frames; LastAction = "⌨ 입력 지연 " + Rollback.InputDelay + " 프레임"; Raise(); }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    private double OneWay()
    {
        var u1 = Math.Max(Next01(), 1e-7f); var u2 = Next01();
        var g = Math.Abs(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(Math.Tau * u2));
        return (EffectiveRttMs * 0.5 + JitterMs * g) / 1000.0;
    }

    /// <summary>1틱(무할당 · 16.7 ms).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            var now = (_tick + 1) / (double)Hz;
            var rb = Rollback; var rf = Reference;
            // ── 원격 입력 수신(순서 수락 · 공백 패킷 폐기) ──
            for (var k = 0; k < _flightN;)
            {
                if (_flight[k].At > now) { k++; continue; }
                var pk = _flight[k];
                _flight[k] = _flight[--_flightN];
                for (var f = Math.Max(pk.From, rb.ReceivedFrame(pk.Player) + 1); f <= pk.To; f++)
                {
                    var input = DeterministicRollbackNetcodeEngine.Bot(pk.Player, f);
                    if (!rb.AddRemoteInput(pk.Player, f, input)) break;
                    rf.AddRemoteInput(pk.Player, f, input);
                }
            }
            // ── 로컬 입력 ──
            rb.AddLocalInput(DeterministicRollbackNetcodeEngine.Bot(0, rb.Current + rb.InputDelay));
            for (var f = _localFed + 1; f <= rb.ReceivedFrame(0); f++) rf.AddRemoteInput(0, f, rb.ConfirmedInput(0, f));
            _localFed = rb.ReceivedFrame(0);
            // ── 원격 피어 송신(미확인 구간 중복 · 피어 프레임 = 로컬 프레임) ──
            var loss = EffectiveLoss;
            for (var p = 1; p < rb.Players; p++)
            {
                InputPacketsSent++;
                if (Next01() < loss) { InputPacketsLost++; continue; }
                if (_flightN < MaxPackets)
                    _flight[_flightN++] = new InputPacket { At = now + OneWay(), Player = p, From = Math.Max(rb.ReceivedFrame(p) + 1, rb.Current - 30), To = rb.Current };
            }
            rb.Advance();
            // ── 권위 기준 진행 · 확정 체크섬 대조 ──
            while (rf.CanAdvance) rf.Advance();
            for (var f = Math.Max(_lastChecked + 1, rf.Current - 40); f < rf.Current; f++)
            {
                if (!rb.TryChecksum(f, out var ls, out var fin) || !fin) break;
                if (!rf.TryChecksum(f, out var rs, out _)) continue;
                SyncChecks++;
                if (ls != rs) { Desyncs++; _desyncNote = true; }
                _lastChecked = f;
            }
            // ── AOI · 시청자 0 = 로컬 플레이어 ──
            const float scale = 2f, off = MassiveSpatialInterestManagementSynthesizer.Center - 16f * scale;
            var yaw = MathF.Atan2(MassiveSpatialInterestManagementSynthesizer.Center - (off + rb.PlayerY(0) * scale), MassiveSpatialInterestManagementSynthesizer.Center - (off + rb.PlayerX(0) * scale));
            Aoi.SetViewer(0, off + rb.PlayerX(0) * scale, off + rb.PlayerY(0) * scale, yaw, 90f, 80f);
            if (_tick % AoiEvery == 0) Aoi.Update(AoiEvery * dt);
            // ── 클록 · 모션 가드 ──
            Guard.Step(dt, now, EffectiveRttMs, JitterMs, loss);
            // ── 계측 ──
            _depth[_seriesHead] = rb.LastRollbackDepth; _rtt[_seriesHead] = (float)Guard.RttEstimateMs; _clock[_seriesHead] = (float)Guard.ClockErrorMs;
            _kbps[_seriesHead] = (float)Aoi.TotalKbps; _pred[_seriesHead] = rb.PredictedFrames;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>최근 동작(디싱크는 틱에서 플래그만 → 조회 시 지연 포맷).</summary>
    public string? Action
    {
        get
        {
            if (_desyncNote) { _desyncNote = false; LastAction = "⚠ 디싱크 검출 · " + Desyncs + "회"; }
            return LastAction;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Arena" };
            _loop.Start();
            LastAction = "▶ 아레나 가동 · " + Hz + " Hz 롤백 · AOI " + (Hz / AoiEvery) + " Hz";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 아레나 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Arena.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Arena.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
