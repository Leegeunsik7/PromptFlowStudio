using System.Diagnostics;

namespace PromptFlowStudio.Services.Empire;

/// <summary>
/// [PromptFlow Studio v16.50.0] ▶ 1-Click 미디어 제국 가동 러너.
///   ▸ 프랜차이즈 출시 → 옴니채널 분배(포맷별 패키지 · 라이브 포맷은 라이브 송출) → 송출 성공 채널 성과 시뮬레이션(조회 · 참여 · eCPM 광고 · 구독/후원/커머스/라이선스) → 수익 매트릭스 · 프랜차이즈 적합도 피드백.
///   ▸ 10Hz 틱: 프랜차이즈 진화 · 라이브 하트비트/정지 재연결 · 50 시스템 하트비트(데모 장애 주입 → 자율 복구) · 메모리 샘플 · 1분 주기 정산.
///   ▸ 전용 스레드 · 틱 예외 격리 · 분배 in-flight 상한 · 정지 시 Join.
/// </summary>
public sealed class MediaEmpireRunner : IDisposable
{
    public const int Hz = 10, MaxInFlight = 4;

    private readonly OmnichannelGlobalBroadcastDispatcher _omni;
    private readonly SelfEvolvingMediaEmpireFranchiseEngine _franchise;
    private readonly ExecutiveEmpireTelemetryAndRevenueMatrix _matrix;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng = new(1650);
    private readonly bool[] _faulty;
    private readonly object _liveGate = new();
    private readonly List<(int Node, int Slot)> _liveLinks = new(256);
    private Thread? _thread;
    private volatile bool _run;
    private int _inFlight;
    private double _nextSettle, _nextFault, _nextPerf;
    private long _dropped;

    public MediaEmpireRunner(IServiceProvider sp)
        : this((OmnichannelGlobalBroadcastDispatcher)sp.GetService(typeof(OmnichannelGlobalBroadcastDispatcher))!,
               (SelfEvolvingMediaEmpireFranchiseEngine)sp.GetService(typeof(SelfEvolvingMediaEmpireFranchiseEngine))!,
               (ExecutiveEmpireTelemetryAndRevenueMatrix)sp.GetService(typeof(ExecutiveEmpireTelemetryAndRevenueMatrix))!) { Guard = sp.GetService(typeof(PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard)) as PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard; Guard?.SetBudget(GuardSite, 32 * 1024); }   // v16.51.0 루프 프로브(이벤트성 발화/토스트 할당 허용 예산)

    /// <summary>v16.51.0 핫패스 프로파일링 가드(선택) · 사이트 인덱스.</summary>
    public PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard? Guard { get; set; }
    public const int GuardSite = 50;

    public MediaEmpireRunner(OmnichannelGlobalBroadcastDispatcher omni, SelfEvolvingMediaEmpireFranchiseEngine franchise, ExecutiveEmpireTelemetryAndRevenueMatrix matrix)
    {
        _omni = omni; _franchise = franchise; _matrix = matrix;
        _faulty = new bool[matrix.SystemCount];
        for (var i = 0; i < _faulty.Length; i++) { var k = i; matrix.System(i).Healer = () => { _faulty[k] = false; return true; }; }
        _franchise.OnReleased += OnReleased;
        _omni.OnDelivered += OnDelivered;
    }

    public bool IsLive => _thread is not null;
    public bool InjectFaults { get; set; } = true;
    public double SettleEveryMs { get; set; } = 60000;
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public long DroppedDispatches => Interlocked.Read(ref _dropped);
    public Task? PendingDispatch { get; private set; }

    public static readonly (string Title, string Logline)[] DemoIps =
    {
        ("네온 하늘의 고양이 기사단", "2099년 서울, 하늘섬을 지키는 고양이 기사들이 잃어버린 별의 조각을 찾아 나선다"),
        ("바다 밑 도서관", "심해 도서관 사서 소녀와 AI 문어가 사라진 이야기를 복원하는 힐링 판타지"),
    };

    /// <summary>가동 준비: 원천 IP 없으면 데모 시드 · 프랜차이즈 시작.</summary>
    public void Prepare(double nowMs)
    {
        if (_franchise.Count == 0) foreach (var (t, l) in DemoIps) _franchise.SeedIp(t, l, nowMs);
        _franchise.Start(nowMs);
        _nextSettle = nowMs + SettleEveryMs; _nextFault = nowMs + 5000; _nextPerf = nowMs + 1000;
    }

    public void Start()
    {
        if (_thread is not null) return;
        Prepare(NowMs);
        _run = true;
        var th = new Thread(Loop) { IsBackground = true, Name = "PFS.MediaEmpire" };
        _thread = th;
        th.Start();
    }

    public void Stop()
    {
        var th = Interlocked.Exchange(ref _thread, null);
        if (th is null) return;
        _run = false;
        th.Join(1500);
        _franchise.Stop();
        _omni.StopLive();
        _matrix.Settle(DateTimeOffset.UtcNow);
    }

    public static DispatchPackage PackageFor(IpNode n)
    {
        var (dur, live) = n.Format switch
        {
            IpFormat.ShortForm => (45.0, false), IpFormat.LongForm => (5400.0, false), IpFormat.Podcast => (3000.0, false),
            IpFormat.LiveCommerce => (3600.0, true), IpFormat.VTuber => (7200.0, true), _ => (600.0, false),
        };
        return new DispatchPackage("ip-" + n.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), n.Title, n.Logline,
            new[] { "PromptFlowStudio", SelfEvolvingMediaEmpireFranchiseEngine.FormatLabel(n.Format), n.Title, "AI콘텐츠" }, dur, "ko", live);
    }

    private void OnReleased(IpNode n)
    {
        if (Interlocked.Increment(ref _inFlight) > MaxInFlight) { Interlocked.Decrement(ref _inFlight); Interlocked.Increment(ref _dropped); return; }
        PendingDispatch = DispatchAsync(n);
    }

    private async Task DispatchAsync(IpNode n)
    {
        try { await _omni.DispatchAsync(PackageFor(n), DateTimeOffset.UtcNow, NowMs).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PromptFlowStudio.Program.Log("Empire.Dispatch", ex); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private void OnDelivered(DispatchPackage pkg, DispatchResult r)
    {
        if (!int.TryParse(pkg.AssetId.AsSpan(3), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var node)) return;
        lock (_liveGate) { _liveLinks.Add((node, r.Slot)); if (_liveLinks.Count > 1024) _liveLinks.RemoveAt(0); }
    }

    /// <summary>러너 1틱(테스트/수동 구동 가능).</summary>
    public void Step(double nowMs)
    {
        _franchise.Tick(nowMs);
        for (var i = 0; i < _omni.PlatformCount; i++)
            if (_omni.State(i) == ChannelState.Live) _omni.Heartbeat(i, nowMs, _omni.Platform(i).MaxKbps * (0.85 + 0.1 * _rng.NextDouble()), _omni.Platform(i).MaxKbps * 125 / Hz);
        if (InjectFaults && nowMs >= _nextFault)
        {
            _nextFault = nowMs + 7000;
            _faulty[_rng.Next(_faulty.Length)] = true;
            var live = _rng.Next(_omni.PlatformCount);
            if (_omni.State(live) == ChannelState.Live) _omni.Heartbeat(live, nowMs - _omni.StallMs * 2, 0, 0);   // 라이브 정지 주입
        }
        for (var i = 0; i < _faulty.Length; i++) if (!_faulty[i]) _matrix.Beat(i, nowMs);
        _matrix.Evaluate(nowMs);
        _omni.Tick(nowMs);
        if (nowMs >= _nextPerf) { _nextPerf = nowMs + 1000; SimulatePerformance(nowMs); _matrix.SampleMemory(nowMs); }
        if (nowMs >= _nextSettle) { _nextSettle = nowMs + SettleEveryMs; _matrix.Settle(DateTimeOffset.UtcNow); }
    }

    /// <summary>송출 채널 성과 시뮬레이션(1초): 조회 → 광고(eCPM) · 포맷별 부가 수익 → 매트릭스 + 프랜차이즈 피드백.</summary>
    public void SimulatePerformance(double nowMs)
    {
        lock (_liveGate)
        {
            foreach (var (node, slot) in _liveLinks)
            {
                if ((uint)node >= (uint)_franchise.Count) continue;
                var n = _franchise.Node(node);
                if (n.Status == IpStatus.Retired) continue;
                var p = _omni.Platform(slot);
                var quality = 0.4 + 0.6 * ((uint)(node * 7919 + slot * 104729) % 1000) / 1000.0;          // IP×플랫폼 궁합(결정적)
                var views = (long)(quality * quality * (p.Kind == PlatformKind.ShortForm ? 900 : 260) * (0.6 + 0.8 * _rng.NextDouble()));
                var ads = (long)(views * p.BaseEcpmUsd * (0.8 + 0.4 * _rng.NextDouble()) / 1000 * 1e6);
                _matrix.Record(slot, RevenueStream.Ads, ads, views, nowMs);
                long extra = 0;
                switch (n.Format)
                {
                    case IpFormat.LiveCommerce: extra = views * 2_000; _matrix.Record(slot, RevenueStream.Commerce, extra, 0, nowMs); break;
                    case IpFormat.VTuber: extra = views * 600; _matrix.Record(slot, RevenueStream.Donation, extra, 0, nowMs); break;
                    case IpFormat.LongForm when p.Kind is PlatformKind.Ott or PlatformKind.FastTv: extra = views * 1_500; _matrix.Record(slot, RevenueStream.Licensing, extra, 0, nowMs); break;
                    case IpFormat.Podcast: extra = views * 300; _matrix.Record(slot, RevenueStream.Subscription, extra, 0, nowMs); break;
                }
                _franchise.ReportPerformance(node, views, Math.Clamp(quality * 0.9, 0, 1), ads + extra);
            }
        }
    }

    private void Loop()
    {
        var period = 1000.0 / Hz;
        while (_run)
        {
            var now = NowMs;
            try { var probe = Guard?.Begin(GuardSite); Step(now); if (probe is { } pr) Guard!.End(pr); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Empire.Tick", ex); }
            var wait = period - (NowMs - now);
            if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    public void Dispose()
    {
        Stop();
        _franchise.OnReleased -= OnReleased;
        _omni.OnDelivered -= OnDelivered;
    }
}
