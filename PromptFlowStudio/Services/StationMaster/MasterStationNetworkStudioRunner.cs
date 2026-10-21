using System.Globalization;
using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.LiveToReels;
using PromptFlowStudio.Services.Showrunner;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.StreamGateway;

namespace PromptFlowStudio.Services.StationMaster;

/// <summary>v17.70.0 세그먼트 대본/애드리브 공급(LLM · 로컬 템플릿) — 호출자가 1 s 내 템플릿 폴백.</summary>
public interface IStationScriptProvider
{
    Task<string?> NextLineAsync(StationProgramKind kind, float speechRate, CancellationToken ct);
}

/// <summary>v17.70.0 로컬 BCL 템플릿 대본(외부 LLM 부재/지연 폴백 · 지연 주입 = 시뮬레이션).</summary>
public sealed class LocalTemplateScriptProvider : IStationScriptProvider
{
    private int _k;
    public int SimulatedLatencyMs { get; set; }

    public static string Template(StationProgramKind kind, int k) => kind switch
    {
        StationProgramKind.LiveCommerce => (k & 1) == 0 ? "지금부터 라이브 커머스! 오늘만의 특가 상품을 소개해 드릴게요." : "재고가 빠르게 줄고 있어요, 놓치지 마세요!",
        StationProgramKind.RhythmConcert => (k & 1) == 0 ? "리듬 액션 콘서트 시작합니다, 비트에 몸을 맡겨 주세요!" : "다음 곡도 신나게 달려 볼까요?",
        StationProgramKind.ReelsReview => (k & 1) == 0 ? "오늘의 바이럴 릴스 하이라이트를 함께 돌아볼게요." : "이 장면, 조회수가 정말 폭발했어요!",
        StationProgramKind.Ambient => "편안한 앰비언트 라운지로 잠시 쉬어 가세요.",
        _ => (k & 1) == 0 ? "다시 토크쇼로 돌아왔습니다, 오늘의 이야기를 이어 가 볼게요." : "채팅창 반응 정말 뜨겁네요, 감사합니다!",
    };

    public async Task<string?> NextLineAsync(StationProgramKind kind, float speechRate, CancellationToken ct)
    {
        if (SimulatedLatencyMs > 0) await Task.Delay(SimulatedLatencyMs, ct).ConfigureAwait(false);
        return Template(kind, Interlocked.Increment(ref _k));
    }
}

/// <summary>
/// [PromptFlow Studio v17.70.0] 📡 24/7 방송국 마스터 편성 센터 러너 — v17.66 MCR 장면 레이어(<see cref="IMcrSceneLayer"/>) + 제어 스레드(20 Hz).
///   ▸ MCR 틱(무할당): 페일오버 하트비트 → EPG 시계 진행(배속) → 프리롤 = PVW 프리셋 · 경계 = Mix 디졸브 Take(0 프레임 블랙) · 콘서트 2 박 비트 컷 ·
///     쇼러너 물리 개입(자가진화 주기) · 디졸브 구간 PGM 휘도 감시(블랙 프레임 계수).
///   ▸ 제어 스레드: 프로그램 활성/해제(v17.65 토크쇼 · v17.68 커머스 · v17.53/54 비트 콘서트 · v17.69 릴스 리뷰) · LLM 대본(500 ms 감시 · 1 s 템플릿 폴백) ·
///     v17.69 릴 배포 → 성과 섭취 → 발화 속도/BGM/물리/타임세일 적응 · 네트워크 프로브.
/// </summary>
public sealed class MasterStationNetworkStudioRunner : IMcrSceneLayer, IDisposable
{
    public const int Hist = 240, TransitionFrames = 30, ArmRing = 32;
    public const float BlackLuma = 8f;

    private readonly BroadcastMcrStudioRunner _mcr;
    private readonly bool _ownMcr;
    private readonly LiveCommerceStudioRunner? _commerce;
    private readonly InteractiveShowrunnerStudioRunner? _showrunner;
    private readonly BeatSyncAudioMixStudioRunner? _beat;
    private readonly LiveToReelsFactoryStudioRunner? _reels;
    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly float[] _trafficS = new float[Hist], _lumaS = new float[Hist];
    private readonly int[] _armId = new int[ArmRing];
    private readonly EvoArms[] _armSnap = new EvoArms[ArmRing];
    private readonly bool[] _armDone = new bool[ArmRing];
    private readonly Func<bool> _netProbe;
    private readonly object _ctl = new();
    private int _sHead, _sN, _histTick, _armHead, _lastReelId;
    private volatile int _prerollReq = -1, _takeReq = -1;
    private StationProgramKind _active = StationProgramKind.Ambient, _outgoing = StationProgramKind.Ambient;
    private bool _deactivatePending, _netWasUp, _attached;
    private long _layerFrames, _transStartTakes, _beatFrame, _lastPhysics, _netRestoreAt;
    private int _transWatch, _stallFrames;
    private double _stallMs, _preLuma = -1;
    private string? _nextLine;
    private long _lastAdlib, _lastSale, _lastApply;
    private Thread? _control;
    private volatile bool _running;
    private CancellationTokenSource? _llmCts;

    public MasterStationNetworkStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(BroadcastMcrStudioRunner)) as BroadcastMcrStudioRunner,
        sp.GetService(typeof(LiveCommerceStudioRunner)) as LiveCommerceStudioRunner,
        sp.GetService(typeof(InteractiveShowrunnerStudioRunner)) as InteractiveShowrunnerStudioRunner,
        sp.GetService(typeof(BeatSyncAudioMixStudioRunner)) as BeatSyncAudioMixStudioRunner,
        sp.GetService(typeof(LiveToReelsFactoryStudioRunner)) as LiveToReelsFactoryStudioRunner,
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner) { }

    public MasterStationNetworkStudioRunner(BroadcastMcrStudioRunner? mcr = null, LiveCommerceStudioRunner? commerce = null, InteractiveShowrunnerStudioRunner? showrunner = null,
        BeatSyncAudioMixStudioRunner? beat = null, LiveToReelsFactoryStudioRunner? reels = null, StreamGatewayClipperStudioRunner? gateway = null)
    {
        _ownMcr = mcr is null;
        _mcr = mcr ?? new BroadcastMcrStudioRunner();
        _commerce = commerce; _showrunner = showrunner; _beat = beat; _reels = reels; _gateway = gateway;
        _netProbe = NetUp;
        Epg.LoadDefaultDay(DateTime.Now.Hour * 60 + DateTime.Now.Minute);
        _active = Epg.OnAir;
    }

    public event Action? OnChange;
    public BroadcastMcrStudioRunner Mcr => _mcr;
    public MasterStationEpgSchedulerEngine Epg { get; } = new();
    public ClosedLoopViralFeedbackSelfEvolutionSynthesizer Evolution { get; } = new();
    public ZeroDowntimeFailoverDisasterGuard Failover { get; } = new();
    public SimulatedReelAudienceModel Audience { get; } = new();
    public LocalTemplateScriptProvider Templates { get; } = new();
    public IStationScriptProvider? Script { get; set; }
    public bool CommerceAvailable => _commerce is not null;
    public bool ShowrunnerAvailable => _showrunner is not null;
    public bool BeatAvailable => _beat is not null;
    public bool ReelsAvailable => _reels is not null;
    public bool GatewayAvailable => _gateway is not null;
    public bool IsRunning => _running;
    public bool Attached => _attached;
    public StationProgramKind Active => _active;
    /// <summary>방송국 시계 배속(1 = 실시간 · 데모 60~1440).</summary>
    public double Speed { get; private set; } = 1;
    /// <summary>시청자 트래픽 수동 지정(NaN = 일주기 + 채팅 모델).</summary>
    public float TrafficOverride { get; private set; } = float.NaN;
    public float Traffic { get; private set; }
    public long SeamlessTakes { get; private set; }
    public long BlackFrames { get; private set; }
    public float MinTransitionLuma { get; private set; } = float.MaxValue;
    public float LastLuma { get; private set; }
    public long BeatCuts { get; private set; }
    public long PhysicsDrops { get; private set; }
    public long TimeSales { get; private set; }
    public long Adlibs { get; private set; }
    public long LlmFallbacks { get; private set; }
    public long FeedbackIngested => Evolution.Ingested;
    public long LayerFrames => _layerFrames;
    public double LastLayerUs { get; private set; }
    public double MaxLayerUs { get; private set; }
    public string? LastLine { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    public float TrafficSeries(int i) => _trafficS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float LumaSeries(int i) => _lumaS[(_sHead - 1 - i + 2 * Hist) % Hist];

    public static int PreferredSource(StationProgramKind k) => k switch
    {
        StationProgramKind.LiveCommerce => 4, StationProgramKind.RhythmConcert => 3, StationProgramKind.ReelsReview => 1, StationProgramKind.Ambient => 0, _ => 0,
    };

    private bool NetUp()
    {
        if (_netRestoreAt != 0) return false;
        var g = _gateway;
        if (g is null) return true;
        var any = false;
        for (var i = 0; i < 3; i++) if (g.Gateway.Session((ChatPlatform)i).State == GatewayLinkState.Connected) { any = true; break; }
        if (any) { _netWasUp = true; return true; }
        return !_netWasUp;   // 한 번도 연결된 적 없으면 순단 아님
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    /// <summary>MCR 레이어 · 스탠바이 버스 결선 + 예비 루프 베이크.</summary>
    public void Attach()
    {
        if (!Failover.Baked) Failover.Bake();
        _mcr.AttachLayer(this);
        _mcr.Mcr.Standby = Failover;
        Failover.NetworkProbe = _netProbe;
        _attached = true;
        LastAction = "🔗 MCR 편성 레이어 · 스탠바이 버스 결선 · 예비 3DGS 루프 " + (Failover.BakeFallback ? "(BCL 폴백)" : "48 f") + " " + Failover.BakeMs.ToString("0", CultureInfo.InvariantCulture) + " ms";
        Raise();
    }

    public void Detach()
    {
        _mcr.DetachLayer(this);
        if (ReferenceEquals(_mcr.Mcr.Standby, Failover)) _mcr.Mcr.Standby = null;
        Failover.NetworkProbe = null;
        _attached = false;
    }

    public void Start()
    {
        if (_running) return;
        if (!_attached) Attach();
        _running = true;
        ActivateProgram(Epg.OnAir);
        _mcr.Start();
        Failover.StartWatchdog();
        _control = new Thread(ControlLoop) { IsBackground = true, Name = "PF.Station.Control" };
        _control.Start();
        LastAction = "▶ 24/7 편성 송출 · " + MasterStationEpgSchedulerEngine.KindName(Epg.OnAir); Raise();
    }

    public void Stop()
    {
        _running = false;
        Failover.StopWatchdog();
        var t = _control; _control = null;
        if (t is not null && t != Thread.CurrentThread) t.Join(2000);
        _mcr.Stop();
        LastAction = "⏸ 편성 정지"; Raise();
    }

    /// <summary>MCR 프레임 1 개 + 제어 1 회(정지 상태 · 테스트).</summary>
    public void Step() { if (!_attached) Attach(); _mcr.Step(); ControlOnce(); }

    public void SetSpeed(double x) { Speed = Math.Clamp(x, 1, 3600); LastAction = "⏩ 방송국 시계 ×" + Speed.ToString("0", CultureInfo.InvariantCulture); Raise(); }
    public void SetTrafficOverride(float v) { TrafficOverride = float.IsNaN(v) ? float.NaN : Math.Clamp(v, 0f, 1f); Raise(); }
    public void SeekMinute(int minuteOfDay) { lock (_ctl) { Epg.Seek(minuteOfDay); } _takeReq = (int)Epg.OnAir; LastAction = "🕒 편성 이동 " + Hhmm(Epg.MinuteOfDay); Raise(); }
    /// <summary>다음 슬롯 경계 직전으로 시계 이동(데모 · 회귀).</summary>
    public void SkipToNextBoundary(long beforeMs = 2_500) { lock (_ctl) Epg.SetClockMs(Epg.Current.EndMin * 60_000 - beforeMs); LastAction = "⏭ 다음 편성 " + (beforeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s 전"; Raise(); }
    public void SimulateLlmLatency(int ms) { Templates.SimulatedLatencyMs = Math.Clamp(ms, 0, 5_000); LastAction = ms > 0 ? "🧪 LLM 지연 " + ms.ToString(CultureInfo.InvariantCulture) + " ms 주입" : "🧪 LLM 지연 해제"; Raise(); }
    public void SimulateNetworkOutage(double seconds = 3) { _netRestoreAt = Environment.TickCount64 + (long)(seconds * 1000); Failover.ReportNetwork(false); LastAction = "🧪 네트워크 순단 " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"; Raise(); }
    public void SimulateRenderStall(int frames = 4, double ms = 45) { _stallMs = Math.Clamp(ms, 0, 200); Volatile.Write(ref _stallFrames, Math.Clamp(frames, 0, 600)); LastAction = "🧪 렌더 과부하 " + frames.ToString(CultureInfo.InvariantCulture) + " f × " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms"; Raise(); }
    public void SetManualStandby(bool on) { Failover.SetManual(on); LastAction = on ? "🛟 수동 스탠바이" : "↩ 수동 스탠바이 해제"; Raise(); }

    /// <summary>시뮬레이션 릴 성과 n 건(현재 팔 · 데모).</summary>
    public void SimulateFeedback(int n = 10)
    {
        for (var i = 0; i < n; i++)
        {
            var arms = Evolution.Arms;
            var m = Audience.Respond(-1, in arms);
            Evolution.Ingest(in m, in arms);
        }
        ApplyEvolution();
        LastAction = "🧬 성과 " + n.ToString(CultureInfo.InvariantCulture) + " 건 섭취 · 성공 계수 " + Evolution.MeanSuccess.ToString("0.00", CultureInfo.InvariantCulture); Raise();
    }

    public static string Hhmm(int minuteOfDay) => (minuteOfDay / 60 % 24).ToString("00", CultureInfo.InvariantCulture) + ":" + (minuteOfDay % 60).ToString("00", CultureInfo.InvariantCulture);

    // ══════════════════════ IMcrSceneLayer(MCR 틱 · 무할당) ══════════════════════
    public void Step(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera pgm, double nowMs, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _layerFrames++;
        Failover.Heartbeat();
        var mcr = _mcr.Mcr;
        // 렌더 과부하 주입(스탠바이 중엔 주 렌더 생략 = 부하 소거)
        var stalled = _stallFrames > 0 && !Failover.StandbyEngaged;
        if (stalled)
        {
            _stallFrames--;
            var until = t0 + (long)(_stallMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(64);
        }
        // 직전 PGM 출력 휘도(디졸브 0 블랙 감시)
        var luma = Luma(mcr.ProgramOut);
        LastLuma = luma;
        if (_transWatch > 0)
        {
            _transWatch--;
            if (!Failover.StandbyEngaged && mcr.StandbyWeight == 0)
            {
                if (luma < MinTransitionLuma) MinTransitionLuma = luma;
                if (luma < BlackLuma) BlackFrames++;   // 3DGS 배경만으로도 ≈ 36 → 8 미만 = 블랙/무신호
            }
            if (_transWatch == 0 && mcr.Takes > _transStartTakes) SeamlessTakes++;
        }
        // EPG(배속 · 트래픽)
        var tr = float.IsNaN(TrafficOverride) ? Traffic : TrafficOverride;
        EpgEvent ev;
        lock (_ctl) ev = Epg.Step((long)(dt * 1000.0 * Speed), tr);
        if (ev == EpgEvent.Preroll)
        {
            _prerollReq = (int)Epg.PrerollKind;
            if (!mcr.Transitioning) mcr.SetPreview(PreferredSource(Epg.PrerollKind));
        }
        else if (ev == EpgEvent.Take)
        {
            if (Failover.StandbyEngaged) { _takeReq = (int)Epg.OnAir; }
            else
            {
                mcr.AbortTransition();
                var src = PreferredSource(Epg.OnAir);
                if (src == mcr.Program) src = (src + 1) % 4;   // 같은 소스 디졸브 = 무의미 → 인접 캠
                mcr.SetPreview(src);
                _preLuma = luma; _transStartTakes = mcr.Takes;
                mcr.Take(McrTransition.Mix, TransitionFrames);
                _transWatch = TransitionFrames + 2;
                _takeReq = (int)Epg.OnAir;
            }
        }
        // 콘서트 비트 컷(2 박)
        if (_active == StationProgramKind.RhythmConcert && !mcr.Transitioning && !Failover.StandbyEngaged)
        {
            var bpm = Evolution.BgmBpm;
            var every = Math.Max(12, (int)(2 * 60.0 / bpm * BroadcastMcrStudioRunner.Hz));
            if (++_beatFrame % every == 0) { mcr.SetPreview((mcr.Program + 1) % 4); if (mcr.Take(McrTransition.Cut)) BeatCuts++; }
        }
        // 쇼러너 물리 개입(자가진화 주기)
        if (_showrunner is not null && _active is StationProgramKind.Talkshow or StationProgramKind.RhythmConcert && !Failover.StandbyEngaged)
        {
            var every = (long)(Evolution.PhysicsIntervalSec * BroadcastMcrStudioRunner.Hz);
            if (_layerFrames - _lastPhysics >= every)
            {
                _lastPhysics = _layerFrames;
                if (_showrunner.Spawner.Spawn((PhysicsDrops & 3) == 3 ? DropKind.Confetti : DropKind.Ball, -1, (int)(PhysicsDrops * 2654435761u)) > 0) PhysicsDrops++;
            }
        }
        if (++_histTick >= 15) { _histTick = 0; _trafficS[_sHead] = tr; _lumaS[_sHead] = luma; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++; }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastLayerUs = us; if (us > MaxLayerUs && _layerFrames > 3 && !stalled) MaxLayerUs = us;   // 주입 과부하 제외
    }

    /// <summary>RGB24 평균 휘도(16 픽셀 간격 표본 · 무할당).</summary>
    public static float Luma(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length < 3) return 0;
        long sum = 0; var n = 0;
        for (var o = 0; o + 2 < rgb.Length; o += 48) { sum += 77 * rgb[o] + 150 * rgb[o + 1] + 29 * rgb[o + 2]; n++; }
        return n > 0 ? sum / (256f * n) : 0;
    }

    // ══════════════════════ 제어 스레드(20 Hz) ══════════════════════
    private void ControlLoop()
    {
        while (_running)
        {
            try { ControlOnce(); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Station.Control", ex); }
            Thread.Sleep(50);
        }
    }

    /// <summary>제어 1 회 — 프로그램 활성/해제 · LLM · 피드백 · 적응 · 트래픽.</summary>
    public void ControlOnce()
    {
        var now = Environment.TickCount64;
        Traffic = ModelTraffic();
        if (_netRestoreAt != 0 && now >= _netRestoreAt) { _netRestoreAt = 0; Failover.ReportNetwork(true); }
        var pre = Interlocked.Exchange(ref _prerollReq, -1);
        if (pre >= 0) { var k = (StationProgramKind)pre; if (k != _active) PrepareProgram(k); RequestLine(k); }
        var take = Interlocked.Exchange(ref _takeReq, -1);
        if (take >= 0)
        {
            var k = (StationProgramKind)take;
            if (k != _active) { _outgoing = _active; _deactivatePending = true; ActivateProgram(k); }
            if (_nextLine is { } line && k != StationProgramKind.Ambient) { _nextLine = null; Say(line); }
            LastAction = "🎬 " + MasterStationEpgSchedulerEngine.KindIcon(k) + " " + MasterStationEpgSchedulerEngine.KindName(k) + " · " + MasterStationEpgSchedulerEngine.Titles[Math.Clamp((int)Epg.Current.Title, 0, MasterStationEpgSchedulerEngine.Titles.Length - 1)] + " · Mix " + TransitionFrames.ToString(CultureInfo.InvariantCulture) + " f";
            Raise();
        }
        if (_deactivatePending && !_mcr.Mcr.Transitioning) { _deactivatePending = false; DeactivateProgram(_outgoing, _active); }
        // 세그먼트 중 주기 애드리브(발화 속도 반영)
        if (_active is StationProgramKind.Talkshow or StationProgramKind.ReelsReview && now - _lastAdlib > 25_000 / Math.Max(0.6f, Evolution.SpeechRate)) { _lastAdlib = now; RequestLine(_active); }
        if (_nextLine is { } l2 && _active is StationProgramKind.Talkshow or StationProgramKind.ReelsReview && pre < 0) { _nextLine = null; Say(l2); }
        // 타임세일 주기(자가진화)
        if (_active == StationProgramKind.LiveCommerce && _commerce is not null && now - _lastSale > Evolution.TimeSaleIntervalSec * 1000) { _lastSale = now; _commerce.StartTimeSale(90); TimeSales++; }
        PollReels();
        if (now - _lastApply > 1000) { _lastApply = now; ApplyEvolution(); }
    }

    private float ModelTraffic()
    {
        var m = Epg.MinuteOfDay;
        var diurnal = 0.12f + 0.78f * MathF.Max(0f, MathF.Sin(MathF.PI * ((m - 360 + DayWrap(m)) % 1440) / 1080f));
        return Math.Clamp(diurnal, 0f, 1f);
    }

    private static int DayWrap(int m) => m < 360 ? 1440 : 0;

    private void Say(string line)
    {
        LastLine = line;
        if (_mcr.Talkshow.InjectAdlib(0, line)) Adlibs++;
    }

    private void RequestLine(StationProgramKind kind)
    {
        var p = Script ?? Templates;
        var rate = Evolution.SpeechRate;
        var cts = new CancellationTokenSource(1000);   // 외부 실패 1 s 내 템플릿 폴백
        var old = Interlocked.Exchange(ref _llmCts, cts);
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        Failover.LlmBegin();
        _ = RunLineAsync(p, kind, rate, cts);
    }

    private async Task RunLineAsync(IStationScriptProvider p, StationProgramKind kind, float rate, CancellationTokenSource cts)
    {
        string? line = null;
        try { line = await p.NextLineAsync(kind, rate, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException) { PromptFlowStudio.Program.Log("Station.Llm", ex); }
        if (string.IsNullOrWhiteSpace(line)) { line = LocalTemplateScriptProvider.Template(kind, (int)LlmFallbacks); LlmFallbacks++; }
        _nextLine = line;
        if (Interlocked.CompareExchange(ref _llmCts, null, cts) == cts) Failover.LlmEnd();
        cts.Dispose();
    }

    private void PrepareProgram(StationProgramKind k)
    {
        // 프리롤: 다음 프로그램 사전 준비(디졸브 대상 장면이 PVW 에 먼저 존재)
        if (k == StationProgramKind.LiveCommerce && _commerce is { Attached: false } c) c.Attach();
        if (k == StationProgramKind.RhythmConcert && _beat is not null) _beat.SetTempo(Evolution.BgmBpm);
    }

    private void ActivateProgram(StationProgramKind k)
    {
        _active = k;
        switch (k)
        {
            case StationProgramKind.LiveCommerce:
                if (_commerce is not null) { if (!_commerce.Attached) _commerce.Attach(); _commerce.StartPitch(); _lastSale = Environment.TickCount64; }
                _mcr.SetAutoDirect(true);
                break;
            case StationProgramKind.RhythmConcert:
                if (_beat is not null) { _beat.SetTempo(Evolution.BgmBpm); _beat.Bgm.Level = Evolution.BgmEnergy; }
                _mcr.SetAutoDirect(false);
                break;
            default:
                _mcr.SetAutoDirect(true);
                break;
        }
    }

    private void DeactivateProgram(StationProgramKind k, StationProgramKind next)
    {
        if (k == StationProgramKind.LiveCommerce && next != StationProgramKind.LiveCommerce && _commerce is { Attached: true } c) c.Detach();
    }

    private void ApplyEvolution()
    {
        var rate = Evolution.SpeechRate;
        var talk = _mcr.Talkshow;
        for (var a = 0; a < talk.Active; a++) talk.Gesture(a).RateScale = rate;
        if (_beat is not null) _beat.Bgm.Level = Evolution.BgmEnergy;
    }

    private void PollReels()
    {
        var r = _reels;
        if (r is null) return;
        var n = r.ReelCount;
        for (var i = n - 1; i >= 0; i--)
        {
            var reel = r.Reel(i);
            if (reel is null || reel.Id <= _lastReelId) continue;
            _lastReelId = reel.Id;
            _armId[_armHead] = reel.Id; _armSnap[_armHead] = Evolution.Arms; _armDone[_armHead] = false;
            _armHead = (_armHead + 1) % ArmRing;
        }
        for (var k = 0; k < ArmRing; k++)
        {
            if (_armDone[k] || _armId[k] == 0) continue;
            var reel = r.FindReel(_armId[k]);
            if (reel is null) { _armDone[k] = true; continue; }
            if (reel.Phase != FactoryReelPhase.Published) continue;
            _armDone[k] = true;
            var m = Audience.Respond(reel.Id, in _armSnap[k], reel.Request.Peak);
            Evolution.Ingest(in m, in _armSnap[k]);
            LastAction = "🧬 릴 #" + reel.Id.ToString(CultureInfo.InvariantCulture) + " 성과 섭취 · 완청 " + (m.Completion * 100).ToString("0", CultureInfo.InvariantCulture) + "% · 성공 " + Evolution.LastSuccess.ToString("0.00", CultureInfo.InvariantCulture);
            Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Station.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Detach();
        Failover.Dispose();
        try { Interlocked.Exchange(ref _llmCts, null)?.Cancel(); } catch (ObjectDisposedException) { }
        if (_ownMcr) _mcr.Dispose();
        OnChange = null;
    }

    public string Summary() =>
        MasterStationEpgSchedulerEngine.KindName(_active) + " · " + Hhmm(Epg.MinuteOfDay) + " · 테이크 " + Epg.Takes.ToString(CultureInfo.InvariantCulture) + " · 블랙 " + BlackFrames.ToString(CultureInfo.InvariantCulture)
        + " · WPM " + Evolution.Wpm.ToString("0", CultureInfo.InvariantCulture) + " · " + ZeroDowntimeFailoverDisasterGuard.StateName(Failover.State) + " · 전환 " + Failover.OnTimeSwitches.ToString(CultureInfo.InvariantCulture) + "/" + Failover.Switches.ToString(CultureInfo.InvariantCulture);
}
