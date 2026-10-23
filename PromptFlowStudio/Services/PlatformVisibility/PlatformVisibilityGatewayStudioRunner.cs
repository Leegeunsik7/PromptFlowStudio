using System.Globalization;
using PromptFlowStudio.Services.LivePublish;
using PromptFlowStudio.Services.PostPublishSplitTest;

namespace PromptFlowStudio.Services.PlatformVisibility;

/// <summary>
/// [PromptFlow Studio v17.94.0] 🌐 플랫폼 비지빌리티 게이트웨이 &amp; 실시간 텔레메트리 러너(HUD <c>PvgRun</c>).
///   ▸ v17.79 라이브 퍼블리셔(OAuth2 볼트)를 DI 로 주입받으면 LIVE 자격증명 플랫폼은 볼트 토큰, 그 외는 결정적 시뮬 토큰 사용.
///   ▸ 체인: <see cref="RealtimeTimeseriesMetricPollingEngine"/> 5 분 폴링 → v17.93 <see cref="PostPublishSplitTestingEngine"/> 판정 →
///     <see cref="LowPerformanceAutoPrunerEngine"/> → <see cref="ProductionPlatformVisibilityGatewayEngine"/>(IVideoVisibilityGateway 실체) 비공개/삭제.
///   ▸ 시뮬레이션 모드는 가상 시계(5 분 단위 전진), LIVE 모드(<c>PROMPTFLOW_PVG_LIVE=1</c>)는 벽시계.
/// </summary>
public sealed class PlatformVisibilityGatewayStudioRunner : IDisposable
{
    public const int LogSize = 32;
    public const long SimEpoch = 1_800_000_000;

    private readonly string[] _log = new string[LogSize];
    private int _logHead, _logCount, _content = 9400;
    private bool _bWins;

    public PlatformVisibilityGatewayStudioRunner(IServiceProvider sp)
        : this(sp.GetService(typeof(ProductionLivePublisherStudioRunner)) as ProductionLivePublisherStudioRunner) { }

    public PlatformVisibilityGatewayStudioRunner(ProductionLivePublisherStudioRunner? live = null, bool? liveEndpoints = null, GraphBridge.IGraphTransport? liveTransport = null, Func<string, string?>? env = null)
    {
        LiveInjected = live is not null;
        SimTokens = new SimulatedPlatformTokenSource();
        Guard = new TokenLifecycleRateLimitBackoffGuard(live is null ? SimTokens : new VaultPlatformTokenSource(live.Vault, SimTokens));
        Gateway = new ProductionPlatformVisibilityGatewayEngine(Guard, Now, liveEndpoints, liveTransport, env);
        // 시뮬레이션 백오프 = 가상 시계 전진(동기 완료) · LIVE = 실제 Task.Delay
        if (!Gateway.LiveEndpoints) Guard.Delay = (sec, _) => { SimNow += sec; return default; };
        Poller = new RealtimeTimeseriesMetricPollingEngine(Gateway) { Sink = Split };
        Pruner = new LowPerformanceAutoPrunerEngine(Gateway);
    }

    public event Action? OnChange;
    public SimulatedPlatformTokenSource SimTokens { get; }
    public TokenLifecycleRateLimitBackoffGuard Guard { get; }
    public ProductionPlatformVisibilityGatewayEngine Gateway { get; }
    public RealtimeTimeseriesMetricPollingEngine Poller { get; }
    public PostPublishSplitTestingEngine Split { get; } = new();
    public LowPerformanceAutoPrunerEngine Pruner { get; }
    public bool LiveInjected { get; }
    public double SimNow { get; private set; } = SimEpoch;
    public long Ticks { get; private set; }
    public bool BWinsScenario => _bWins;
    public bool DeleteMode => Pruner.Mode == PruneAction.Delete;
    public string? LastAction { get; private set; }
    public int LogCount => _logCount;
    public int SlotA { get; private set; } = -1;
    public int SlotB { get; private set; } = -1;
    public int SlotIg { get; private set; } = -1;
    public long NowUnix => (long)Now();
    public long ElapsedSec => Split.Phase == SplitTestPhase.Idle ? 0 : Math.Max(0, NowUnix - Split.StartUnix);
    public long RemainingSec => Split.RemainingSec(NowUnix);

    private double Now() => Gateway is { LiveEndpoints: true } ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 : SimNow;

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    public string ConnectionText(LiveProvider p)
    {
        var now = Now();
        if (!Guard.HasToken(p)) return Gateway.ModeName + " · 토큰 대기";
        if (Guard.Throttled(p)) return Gateway.ModeName + " · 쿼터 안전 정지";
        if (now < Guard.BlockedUntil(p)) return Gateway.ModeName + " · 백오프 " + (Guard.BlockedUntil(p) - now).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        return Gateway.ModeName + " · 연결 · 토큰 " + Mmss((long)Guard.SecondsLeft(p, now));
    }

    public bool Connected(LiveProvider p) => Guard.HasToken(p) && Guard.SecondsLeft(p, Now()) > 0 && !Guard.Throttled(p);

    // ══════════════════════ 조작(비핫패스 · 시뮬 경로 동기 완료) ══════════════════════

    /// <summary>A/B(YouTube 2) + Instagram 릴스 1 배포 · 바인딩 · 폴링 등록 · 스플릿 추적 시작.</summary>
    public async Task<int> DeployAsync()
    {
        var id = ++_content;
        var now = (long)Now();
        Gateway.Clear(); Poller.Clear(); Gateway.Simulator.Reset();
        var ytA = "PF94A" + id.ToString("000000", CultureInfo.InvariantCulture);
        var ytB = "PF94B" + id.ToString("000000", CultureInfo.InvariantCulture);
        var ig = "1794" + id.ToString("00000000000", CultureInfo.InvariantCulture);
        Gateway.Simulator.Seed(LiveProvider.YouTube, ytA, now, !_bWins);
        Gateway.Simulator.Seed(LiveProvider.YouTube, ytB, now, _bWins);
        Gateway.Simulator.Seed(LiveProvider.Instagram, ig, now, true);
        SlotA = Gateway.Bind(LiveProvider.YouTube, ytA, id, SplitVariant.A, 0);
        SlotB = Gateway.Bind(LiveProvider.YouTube, ytB, id, SplitVariant.B, 1);
        SlotIg = Gateway.Bind(LiveProvider.Instagram, ig, id + 100_000, SplitVariant.A, 2);
        Split.Deploy(id, "훅 A · " + ytA, 0, 0, "훅 B · " + ytB, 1, 1, now, 0);
        Poller.Add(SlotA, now, true);
        Poller.Add(SlotB, now, true);
        Poller.Add(SlotIg, now, false);
        Guard.EnsureToken(LiveProvider.YouTube, now);
        Guard.EnsureToken(LiveProvider.Instagram, now);
        Push("🚀 #" + Id(id) + " 배포 — YouTube A " + ytA + " · B " + ytB + " · IG " + ig);
        var a = await Gateway.RefreshStatusAsync(SlotA).ConfigureAwait(false);
        var b = await Gateway.RefreshStatusAsync(SlotB).ConfigureAwait(false);
        var c = await Gateway.RefreshStatusAsync(SlotIg).ConfigureAwait(false);
        Push("🔎 상태 확인 HTTP " + Id(a) + "/" + Id(b) + "/" + Id(c) + " — " + ProductionPlatformVisibilityGatewayEngine.VisibilityName(Gateway.Binding(SlotA).Visibility));
        LastAction = "🚀 #" + Id(id) + " 배포 · 5 분 폴링 시작";
        Raise();
        return id;
    }

    /// <summary>5 분 단위 전진 — 토큰 틱 · 폴링 · 판정 · 회수. LIVE 는 시계 전진 없이 1 회 폴링. 반환 = 기록 표본 수.</summary>
    public async Task<int> AdvanceAsync(int minutes)
    {
        if (Split.Phase == SplitTestPhase.Idle) await DeployAsync().ConfigureAwait(false);
        var steps = Gateway.LiveEndpoints ? 1 : Math.Max(1, minutes / 5);
        var recorded = 0;
        for (var k = 0; k < steps; k++)
        {
            if (!Gateway.LiveEndpoints) SimNow += RealtimeTimeseriesMetricPollingEngine.IntervalSec;
            Ticks++;
            var now = (long)Now();
            var before = Guard.BackgroundRefreshes;
            Guard.Tick(now);
            if (Guard.BackgroundRefreshes > before) Push("🔑 토큰 만료 5 분 전 자동 갱신 — 누적 " + Guard.Refreshes.ToString(CultureInfo.InvariantCulture) + "회");
            var hits = Guard.RateLimitHits;
            var n = await Poller.PollDueAsync(now).ConfigureAwait(false);
            recorded += n;
            if (Guard.RateLimitHits > hits) Push("⏳ 429 감지 — 지수 백오프 " + Guard.LastBackoffSec.ToString("0.0", CultureInfo.InvariantCulture) + "s 후 재시도");
            now = (long)Now();
            if (Split.Phase is SplitTestPhase.Tracking or SplitTestPhase.Decided && Split.Evaluate(now))
                Push("🏆 승자 " + PostPublishSplitTestingEngine.VariantName(Split.Winner) + " 확정 — 신뢰도 " + Pct(Split.Confidence));
            PruneIfDue(now);
            if (Split.Phase == SplitTestPhase.Closed && Gateway.LiveEndpoints) break;
        }
        LastAction = "⏩ 폴링 " + Id(steps) + "회 · 표본 " + Id(recorded) + " · " + PostPublishSplitTestingEngine.PhaseName(Split.Phase) + " · 쿼터 YT " + Pct(Guard.QuotaRatio(LiveProvider.YouTube));
        Raise();
        return recorded;
    }

    /// <summary>골든타임 경과 시 v17.93 회수기 → 프로덕션 게이트웨이(동기 계약) 비공개/삭제 · 감사 로그.</summary>
    private void PruneIfDue(long now)
    {
        var before = Pruner.AuditCount;
        Pruner.Evaluate(Split, now);
        for (var i = Pruner.AuditCount - before - 1; i >= 0; i--)
        {
            ref readonly var a = ref Pruner.Audit(i);
            Push((a.Ok ? "🗑️ 자동 회수 " : "⚠️ 회수 실패 ") + PostPublishSplitTestingEngine.VariantName(a.Variant) + " → " + LowPerformanceAutoPrunerEngine.ActionName(a.Action) + " · " +
                 LowPerformanceAutoPrunerEngine.ReasonText(a.Reason) + " · HTTP " + Id(a.Status));
        }
    }

    public async Task RunToGoldenEndAsync()
    {
        if (Split.Phase == SplitTestPhase.Idle) await DeployAsync().ConfigureAwait(false);
        var guard = 0;
        while (Split.Phase != SplitTestPhase.Closed && guard++ < PostPublishSplitTestingEngine.Capacity * 2) await AdvanceAsync(5).ConfigureAwait(false);
    }

    /// <summary>수동 가시성 전환.</summary>
    public async Task<int> SetVisibilityAsync(int slot, VideoVisibility target)
    {
        if (slot < 0 || !Gateway.Binding(slot).InUse) { LastAction = "⚠️ 배포 전 — 먼저 「🚀 A/B + 릴스 배포」"; Raise(); return 404; }
        var b = Gateway.Binding(slot);
        var st = await Gateway.ApplyAsync(slot, target).ConfigureAwait(false);
        Push((st is >= 200 and < 300 ? "👆 " : "⚠️ ") + ProductionPlatformVisibilityGatewayEngine.ProviderName(b.Provider) + " " + b.VideoId + " → " + ProductionPlatformVisibilityGatewayEngine.VisibilityName(target) + " · HTTP " + Id(st) + " · " + ProductionPlatformVisibilityGatewayEngine.VisibilityName(b.Visibility));
        LastAction = "👆 " + b.VideoId + " " + ProductionPlatformVisibilityGatewayEngine.VisibilityName(b.Visibility) + " (HTTP " + Id(st) + ")";
        Raise();
        return st;
    }

    public async Task<int> RefreshStatusAsync(int slot)
    {
        if (slot < 0 || !Gateway.Binding(slot).InUse) return 404;
        var st = await Gateway.RefreshStatusAsync(slot).ConfigureAwait(false);
        var b = Gateway.Binding(slot);
        LastAction = "🔎 " + b.VideoId + " — " + ProductionPlatformVisibilityGatewayEngine.VisibilityName(b.Visibility) + " · HTTP " + Id(st);
        Raise();
        return st;
    }

    public void InjectRateLimit()
    {
        Gateway.Simulator.InjectRateLimit(2);
        LastAction = "🧪 다음 2 회 요청 429 주입 — 지수 백오프 관찰";
        Raise();
    }

    public void InjectQuotaExceeded()
    {
        Gateway.Simulator.InjectQuotaExceeded(1);
        LastAction = "🧪 다음 요청 quotaExceeded 주입 — 당일 안전 정지";
        Raise();
    }

    /// <summary>두 플랫폼 토큰 만료를 2 분 뒤로 당겨 다음 틱 자동 갱신 유도.</summary>
    public void ExpireTokensSoon()
    {
        var now = Now();
        Guard.ForceExpiry(LiveProvider.YouTube, now + 120);
        Guard.ForceExpiry(LiveProvider.Instagram, now + 120);
        LastAction = "⌛ 토큰 만료 2 분 전으로 조정 — 다음 틱 자동 갱신";
        Raise();
    }

    public void ToggleBWins()
    {
        _bWins = !_bWins;
        LastAction = "🧪 시나리오: " + (_bWins ? "B 우세" : "A 우세") + " — 다음 배포부터 적용";
        Raise();
    }

    public void ToggleDeleteMode()
    {
        Pruner.Mode = DeleteMode ? PruneAction.Private : PruneAction.Delete;
        LastAction = "🗑️ 회수 방식: " + LowPerformanceAutoPrunerEngine.ActionName(Pruner.Mode);
        Raise();
    }

    public void Reset()
    {
        Split.Reset(); Poller.Clear(); Gateway.Clear(); Gateway.Simulator.Reset(); Pruner.ClearAudit();
        SlotA = SlotB = SlotIg = -1;
        Array.Clear(_log);
        _logHead = _logCount = 0;
        Ticks = 0;
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 로그 · 포맷 ══════════════════════

    private void Push(string line)
    {
        lock (_log)
        {
            _log[_logHead] = "[" + Mmss(ElapsedSec) + "] " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
    }

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Pct(float v) => (v * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public static string Mmss(long sec) => (sec / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (sec % 60).ToString("00", CultureInfo.InvariantCulture);

    public string Summary() =>
        Gateway.ModeName + " · 호출 " + Id(Gateway.Calls) + " · 재시도 " + Id(Gateway.Retries) + " · 백오프 " + Id(Gateway.Backoffs) + " · 갱신 " + Id(Guard.Refreshes) +
        " · 표본 " + Id(Poller.Samples) + " · 피딩 " + Id(Poller.Fed) + " · 볼트 " + (LiveInjected ? "Injected" : "Local");

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("PlatformVisibility.OnChange", ex); }
    }

    public void Dispose()
    {
        Gateway.Dispose();
        Guard.Dispose();
        OnChange = null;
    }
}
