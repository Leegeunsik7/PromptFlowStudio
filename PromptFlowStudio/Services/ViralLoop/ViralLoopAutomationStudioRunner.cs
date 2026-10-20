using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.VerticalTranscode;
using PromptFlowStudio.Services.ViralReels;
namespace PromptFlowStudio.Services.ViralLoop;

/// <summary>
/// [PromptFlow Studio v17.50.0] 🚀 AI 24/7 바이럴 오토메이션 스튜디오 러너(10 Hz 전용 스레드).
/// 매 틱: 시계 진행(시뮬레이션 = 틱당 가속 분 · 실 모드 = UtcNow) → 큐레이션 갱신(주기) → 자율 트리거(오토 ON · 비동결 · 일일 한도 여유 시 최고 점수 미사용 항목 적재) →
/// 오케스트레이션 틱(v17.47 → v17.49 → v17.48) → 1/6/24 h 인사이트 피드백. 시뮬레이션 모드에서는 v17.48 시뮬레이터에 인게이지먼트 모델
/// (후킹/스타일별 고정 진짜 완청률 · 조회 성장 곡선 · 선택적 섀도우밴 주입)을 연결해 자가 튜닝을 검증한다.
/// </summary>
public sealed class ViralLoopAutomationStudioRunner : IDisposable
{
    public const int Hz = 10, Hist = 288;
    public static readonly long SimEpoch = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly float[] TrueHook = { 0.42f, 0.55f, 0.38f, 0.47f, 0.33f, 0.61f };
    private static readonly float[] TrueStyle = { 0.05f, 0.15f, 0.02f, 0.08f };
    private readonly object _gate = new();
    private readonly float[] _rewardS = new float[Hist], _ratioS = new float[Hist];
    private readonly ViralReelsStudioRunner _reels;
    private readonly VerticalTranscoderStudioRunner _trans;
    private readonly GraphApiBridgeStudioRunner _bridge;
    private readonly Func<ulong, GraphMediaInsights> _model;
    private long _now, _nextCurationAt, _tick;
    private int _head, _series;
    private Thread? _loop;
    private volatile bool _running;

    public ViralLoopAutomationStudioRunner(IServiceProvider sp) : this(
        (ViralReelsStudioRunner)sp.GetService(typeof(ViralReelsStudioRunner))!,
        (VerticalTranscoderStudioRunner)sp.GetService(typeof(VerticalTranscoderStudioRunner))!,
        (GraphApiBridgeStudioRunner)sp.GetService(typeof(GraphApiBridgeStudioRunner))!) { }

    public ViralLoopAutomationStudioRunner(ViralReelsStudioRunner reels, VerticalTranscoderStudioRunner trans, GraphApiBridgeStudioRunner bridge)
    {
        _reels = reels; _trans = trans; _bridge = bridge;
        Scheduler = new Autonomous247ReelsSchedulerSynthesizer(reels, trans, bridge, Curation, Feedback);
        _model = Insights;
        _now = SimEpoch;
        AttachModel();
    }

    public event Action? OnChange;
    public RealtimeViralTrendCurationEngine Curation { get; } = new();
    public EngagementFeedbackSelfTuningGuard Feedback { get; } = new();
    public Autonomous247ReelsSchedulerSynthesizer Scheduler { get; }
    public bool Auto { get; private set; }
    public bool RealClock { get; private set; }
    public double SimMinutesPerTick { get; set; } = 1;
    public double CurationIntervalMin { get; set; } = 30;
    public int PreferredCategory { get; set; } = -1;
    public LoopRegion Region { get; private set; } = LoopRegion.Korea;
    /// <summary>시뮬레이션 섀도우밴 주입 시각(UTC 틱 · 0 = 없음) — 이후 게시물 조회 × 0.05.</summary>
    public long ShadowbanFromTicks { get; set; }
    public long NowTicks => _now;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float RewardSeries(int i) => _rewardS[(_head - 1 - i + 2 * Hist) % Hist];
    public float RatioSeries(int i) => _ratioS[(_head - 1 - i + 2 * Hist) % Hist];
    public static string Clock(long ticks, int offsetH) => new DateTime(ticks + offsetH * TimeSpan.TicksPerHour, DateTimeKind.Utc).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private void AttachModel() { if (!_bridge.Live) _bridge.Sim.InsightsModel = _model; }

    /// <summary>시뮬레이션 인게이지먼트 모델(무할당 · Func 1 회 생성).</summary>
    private GraphMediaInsights Insights(ulong mediaId)
    {
        GraphMediaInsights m = default; m.Clear(mediaId);
        var j = Scheduler.FindByMedia(mediaId);
        if (j < 0) { m.Views = 0; m.AvgWatchMs = 0; m.Saved = 0; m.Comments = 0; m.Shares = 0; m.Reach = 0; m.Interactions = 0; return m; }
        ref readonly var job = ref Scheduler.Job(j);
        var hours = Math.Max((_now - job.PublishedTicks) / (double)TimeSpan.TicksPerHour, 0);
        var noise = (float)((mediaId * 2654435761UL >> 16) % 1000) / 1000f - 0.5f;
        var comp = Math.Clamp(TrueHook[job.Hook] + TrueStyle[job.Style] + 0.06f * noise, 0.05f, 0.98f);
        var shadow = ShadowbanFromTicks > 0 && job.PublishedTicks >= ShadowbanFromTicks ? 0.05 : 1.0;
        var views = (long)(20000 * (0.4 + comp) * (1 - Math.Exp(-hours / 6.0)) * shadow) + 1;
        m.Views = views; m.Reach = (long)(views * 0.8);
        m.AvgWatchMs = (long)(comp * job.DurationSec * 1000);
        m.Saved = (long)(views * 0.012 * comp); m.Comments = (long)(views * 0.004); m.Shares = (long)(views * 0.006 * comp);
        m.Interactions = m.Saved + m.Comments + m.Shares;
        return m;
    }

    // ══════════════════════ 제어(할당 허용) ══════════════════════
    public void SetAuto(bool on) { lock (_gate) Auto = on; LastAction = on ? "🤖 24/7 자율 모드 ON" : "🤖 자율 모드 OFF(원클릭만)"; Raise(); }
    public void SetRegion(LoopRegion r) { lock (_gate) { Region = r; Scheduler.DefaultRegion = r; } LastAction = "🌏 타깃 " + Autonomous247ReelsSchedulerSynthesizer.RegionNames[(int)r]; Raise(); }
    public void SetRealClock(bool on) { lock (_gate) { RealClock = on; _now = on ? DateTime.UtcNow.Ticks : Math.Max(_now, SimEpoch); } LastAction = on ? "🕒 실시간 시계" : "⏩ 가속 시계"; Raise(); }

    /// <summary>원클릭 [⚡ 리메이크 &amp; 발행] — 즉시 발행 슬롯(골든아워 무시 · 간격/한도 준수).</summary>
    public bool RemakeAndPublish(int curationSlot)
    {
        int j;
        lock (_gate) j = Scheduler.Enqueue(curationSlot, true, Region, _now);
        LastAction = j >= 0 ? "⚡ 리메이크 & 발행 적재 · 슬롯 " + curationSlot.ToString(CultureInfo.InvariantCulture) : Feedback.IsFrozen ? "⛔ 동결 중 — 적재 불가" : "⚠ 적재 실패(이미 사용/큐 가득)";
        Raise();
        return j >= 0;
    }

    public void CancelJob(int j) { lock (_gate) Scheduler.Cancel(j); LastAction = "⏹ 작업 취소"; Raise(); }

    /// <summary>긴급 정지 — 가드 수동 동결 · 큐 정지.</summary>
    public void EmergencyStop()
    {
        lock (_gate) { Feedback.Freeze(-1, -1, _now); Scheduler.FreezeAll(); Auto = false; }
        LastAction = "🛑 긴급 정지 — 48 h 쿨다운 후 수동 재개";
        Raise();
    }

    public bool Resume()
    {
        bool ok;
        lock (_gate) ok = Feedback.Resume(_now);
        LastAction = ok ? "▶ 수동 재개 — 기준선 재학습" : "⏳ 쿨다운 미경과(" + Clock(Feedback.CooldownUntilTicks, 9) + " KST 이후)";
        Raise();
        return ok;
    }

    public void RefreshCuration() { lock (_gate) _nextCurationAt = _now; }

    // ══════════════════════ 틱 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("ViralLoop.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _now = RealClock ? DateTime.UtcNow.Ticks : _now + (long)(SimMinutesPerTick * TimeSpan.TicksPerMinute);
            if (!RealClock && !_bridge.IsRunning) _bridge.AdvanceClock(SimMinutesPerTick * 60 - 1.0); // 가속 시계 결합(브릿지 Step 의 1/60 s 진행분 제외 근사)
            if (_now >= _nextCurationAt)
            {
                if (!_bridge.IsRunning) { _bridge.IngestNow(); for (var i = 0; i < 4; i++) _bridge.Step(); }
                Curation.Refresh(_reels.Ingest, _bridge.Ingest, _now);
                _nextCurationAt = _now + (long)(CurationIntervalMin * TimeSpan.TicksPerMinute);
            }
            if (Auto && !Feedback.IsFrozen && Scheduler.Pending < Autonomous247ReelsSchedulerSynthesizer.MaxHeldStreams && Scheduler.PublishedSince(_now - TimeSpan.TicksPerDay) + Scheduler.Pending < Scheduler.DailyCap)
            {
                var s = Curation.BestUnclaimed(PreferredCategory);
                if (s >= 0) Scheduler.Enqueue(s, false, Region, _now);
            }
            var hist = Feedback.HistoryCount;
            Scheduler.Tick(_now);
            if (Feedback.HistoryCount != hist)
            {
                _rewardS[_head] = Feedback.LastReward; _ratioS[_head] = Feedback.LastRatio;
                _head = (_head + 1) % Hist;
                if (_series < Hist) _series++;
            }
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            AttachModel();
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.ViralLoop" };
            _loop.Start();
        }
        LastAction = "▶ 바이럴 루프 " + (RealClock ? "실시간" : "가속 ×" + (SimMinutesPerTick * 60 * Hz).ToString("0", CultureInfo.InvariantCulture));
        Raise();
    }
    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(10000);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or ObjectDisposedException) { PromptFlowStudio.Program.Log("ViralLoop.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -2000) { sw.Restart(); n = 0; }
            if (n % 3 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ViralLoop.Runner.OnChange", ex); }
    }
    public void Dispose()
    {
        Stop();
        lock (_gate) { Scheduler.FreezeAll(); Scheduler.DisposeAll(); }
        if (_bridge.Sim.InsightsModel == _model) _bridge.Sim.InsightsModel = null;
    }
}
