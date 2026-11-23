using System.Globalization;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🚀 Step 37 — 호스트 기동 결과 1건.</summary>
public sealed record PlutoAutonomyBootReport(bool WatcherRunning, bool ApiAutoSyncArmed, bool BriefOutcomesStarted, bool FeedWired,
    string? WatcherSkipReason, string? ApiSkipReason, DateTimeOffset AtUtc)
{
    public string SummaryKorean
        => "🚀 백그라운드 자율 기동 · 📥 Downloads 감시 " + (WatcherRunning ? "가동" : "대기(" + (WatcherSkipReason ?? "—") + ")")
         + " · ⏱️ API 주기 동기화 " + (ApiAutoSyncArmed ? "예약" : "대기(" + (ApiSkipReason ?? "—") + ")")
         + " · 🎯 기획 채점 " + (BriefOutcomesStarted ? "가동" : "없음")
         + " · 🛰️ 활동 피드 " + (FeedWired ? "연결" : "없음");
}

/// <summary>
/// 🚀 Step 37 — 앱 기동 시(Workspace 진입 전) Downloads 감시 · YouTube API 주기 동기화 · 기획 채점을 1회 멱등 기동하고
/// 자율 활동 피드 브리지를 호스트 수명으로 <b>정확히 1회</b> 구독한다. 이후 Workspace 재진입은 같은 데몬을 재사용(타이머 재설정 없음).
/// </summary>
public interface IPlutoBackgroundAutonomyBootstrapper : IDisposable, IAsyncDisposable
{
    /// <summary>기동 완료.</summary>
    bool IsStarted { get; }
    /// <summary>실제 기동 본체 실행 수(멱등 → 최대 1).</summary>
    int BootCount { get; }
    PlutoAutonomyBootReport? LastReport { get; }
    /// <summary>단일 비행 — 동시 호출 · 재호출은 같은 작업에 합류(예외 비전파).</summary>
    Task<PlutoAutonomyBootReport> StartAsync(CancellationToken ct = default);
    /// <summary>호스트용 발사 후 망각(UI 스레드 무차단 · 기동 유예 후 스레드 풀에서 실행).</summary>
    void StartInBackground();
}

/// <summary>🚀 <see cref="IPlutoBackgroundAutonomyBootstrapper"/> 구현(순수 BCL).</summary>
public sealed class PlutoBackgroundAutonomyBootstrapper : IPlutoBackgroundAutonomyBootstrapper
{
    /// <summary>호스트 기동 유예 — 첫 화면 페인트 · DI 워밍업과 디스크 I/O(장부 적재 · 시작 스윕)가 경합하지 않게.</summary>
    public static readonly TimeSpan DefaultBootDelay = TimeSpan.FromSeconds(3);

    private readonly IYouTubeDownloadsAutoWatcherService? _watcher;
    private readonly IPlutoYouTubeApiSyncService? _api;
    private readonly IPlutoBriefOutcomeAttributionService? _brief;
    private readonly IPlutoAutonomousActivityFeedService? _feed;
    private readonly Func<string, string?> _env;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _life = new();
    private Task<PlutoAutonomyBootReport>? _boot;
    private bool _bridged, _disposed;
    private int _bootCount;

    /// <summary>기동 유예(테스트 = 0).</summary>
    public TimeSpan BootDelay { get; init; } = DefaultBootDelay;

    public PlutoBackgroundAutonomyBootstrapper(IYouTubeDownloadsAutoWatcherService? watcher, IPlutoYouTubeApiSyncService? api,
        IPlutoBriefOutcomeAttributionService? briefOutcomes, IPlutoAutonomousActivityFeedService? feed,
        Func<string, string?>? environment = null, TimeProvider? timeProvider = null)
    {
        _watcher = watcher;
        _api = api;
        _brief = briefOutcomes;
        _feed = feed;
        _env = environment ?? Environment.GetEnvironmentVariable;
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsStarted { get { lock (_sync) return _boot is { IsCompletedSuccessfully: true }; } }
    public int BootCount => Volatile.Read(ref _bootCount);
    public PlutoAutonomyBootReport? LastReport { get; private set; }

    // ── 🔒 킬 스위치(기동 시점 재평가) ──

    private bool IsOn(string name, params string[] truthy)
    {
        var v = _env(name)?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        foreach (var t in truthy) if (v.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary><c>PROMPTFLOW_PLUTO_DRIVE=off</c> — 오프라인 · 테스트 하네스(실 Downloads · 네트워크 전부 무접촉).</summary>
    public bool OfflineHarness => IsOn(PlutoDriveStorageManager.DriveEnvVar, "off");
    public bool WatcherDisabledByEnv => OfflineHarness || IsOn(YouTubeDownloadsAutoWatcherService.DisableEnvVar, "1", "true");
    public bool ApiDisabledByEnv => OfflineHarness || IsOn(PlutoYouTubeApiSyncService.DisableEnvVar, "1", "true", "on");

    // ── 기동 ──

    public void StartInBackground()
    {
        _ = StartAsync();   // 결과 작업은 실패하지 않는다(각 단계 예외 격리)
    }

    public Task<PlutoAutonomyBootReport> StartAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_disposed) return Task.FromResult(Skipped("종료됨"));
            // 🛡️ 단일 비행 — 호스트 기동 · Workspace 진입 · 재진입이 겹쳐도 본체 1회(구독 · 타이머 중복 없음)
            return _boot ??= Task.Run(() => BootCoreAsync(_life.Token));
        }
    }

    private PlutoAutonomyBootReport Skipped(string why) => new(false, false, false, false, why, why, _time.GetUtcNow());

    private async Task<PlutoAutonomyBootReport> BootCoreAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _bootCount);
        try
        {
            if (BootDelay > TimeSpan.Zero) await Task.Delay(BootDelay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return LastReport = Skipped("종료됨"); }

        var feedWired = WireFeedOnce();

        // 🎯 기획 채점 — 로컬 카탈로그만 읽음(네트워크 · Downloads 무접촉) → 이전 세션 미채점분 · 다른 PC 병합본 즉시 채점
        var briefStarted = false;
        if (!token.IsCancellationRequested && _brief is not null)
        {
            try { _brief.Start(); briefStarted = true; }
            catch (Exception ex) { Log("Pluto.Boot.BriefOutcomes", ex); }
        }

        // 📥 Downloads 감시
        var watcherRunning = false;
        string? watcherWhy = null;
        if (_watcher is null) watcherWhy = "미등록";
        else if (WatcherDisabledByEnv) watcherWhy = "격리 스위치";
        else if (token.IsCancellationRequested) watcherWhy = "종료됨";
        else
        {
            try
            {
                if (!_watcher.IsEnabled) watcherWhy = "꺼짐";
                else if (_watcher.IsRunning) watcherRunning = true;   // 멱등 — 이미 도는 감시기는 건드리지 않음
                else
                {
                    watcherRunning = _watcher.Start();
                    if (!watcherRunning) watcherWhy = _watcher.IsIsolationBlocked ? "격리 스위치" : "폴더 없음";
                }
            }
            catch (Exception ex) { Log("Pluto.Boot.DownloadsWatcher", ex); watcherWhy = ex.GetType().Name; }
        }

        // ⏱️ YouTube API 주기 동기화
        var apiArmed = false;
        string? apiWhy = null;
        if (_api is null) apiWhy = "미등록";
        else if (ApiDisabledByEnv) apiWhy = "격리 스위치";
        else if (token.IsCancellationRequested) apiWhy = "종료됨";
        else
        {
            try
            {
                if (!_api.IsConfigured) apiWhy = "자격 증명 · 채널 미설정";
                else if (!_api.AutoSyncEnabled) apiWhy = "꺼짐";
                else
                {
                    apiArmed = _api.StartAutoSync();   // 멱등 — 이미 예약돼 있으면 예정 시각 유지(재설정 없음)
                    if (!apiArmed) apiWhy = _api.IsNetworkAllowed ? "인증 정지" : "네트워크 차단";
                }
            }
            catch (Exception ex) { Log("Pluto.Boot.YouTubeApi", ex); apiWhy = ex.GetType().Name; }
        }

        var report = new PlutoAutonomyBootReport(watcherRunning, apiArmed, briefStarted, feedWired, watcherWhy, apiWhy, _time.GetUtcNow());
        LastReport = report;
        System.Diagnostics.Debug.WriteLine("[Pluto.Boot] " + report.SummaryKorean);
        return report;
    }

    // ── 🛰️ 활동 피드 브리지(호스트 수명 · 정확히 1회 구독) ──

    private bool WireFeedOnce()
    {
        if (_feed is null) return false;
        lock (_sync)
        {
            if (_disposed) return false;
            if (_bridged) return true;
            _bridged = true;
            if (_watcher is not null) _watcher.OnExportAutoIngested += OnIngested;
            if (_api is not null) _api.AutoSyncCompleted += OnApiAutoSynced;
            if (_brief is not null) _brief.Changed += OnBriefChanged;
        }
        return true;
    }

    private void OnIngested(PlutoDownloadsAutoIngestEvent e)
    {
        try { _feed?.RecordDownloadsIngest(e); }   // 메모리 추가만 — 감시기 수집 게이트를 디스크 I/O 로 막지 않음
        catch (Exception ex) { Log("Pluto.Feed.Ingest", ex); }
    }

    private void OnApiAutoSynced(PlutoYouTubeApiAutoSyncEvent e)
    {
        try { _feed?.RecordApiSync(e.Result, background: true); }
        catch (Exception ex) { Log("Pluto.Feed.ApiSync", ex); }
    }

    private void OnBriefChanged()
    {
        try { _feed?.RecordBriefSweep(_brief?.LastSweep); }
        catch (Exception ex) { Log("Pluto.Feed.Brief", ex); }
    }

    private void Unbridge()
    {
        if (!_bridged) return;
        _bridged = false;
        if (_watcher is not null) _watcher.OnExportAutoIngested -= OnIngested;
        if (_api is not null) _api.AutoSyncCompleted -= OnApiAutoSynced;
        if (_brief is not null) _brief.Changed -= OnBriefChanged;
    }

    private static void Log(string tag, Exception ex)
    {
        try { Program.Log(tag, ex); }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("[" + tag + "] " + ex.GetType().Name); }
    }

    /// <summary>진단용 1줄(불변 문화권).</summary>
    public override string ToString() => "PlutoBackgroundAutonomyBootstrapper { Boots = " + BootCount.ToString(CultureInfo.InvariantCulture) + ", Started = " + (IsStarted ? "true" : "false") + " }";

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Unbridge();   // 서비스 자체는 DI 컨테이너가 폐기(타이머 · 감시기 정지는 각 서비스 Dispose)
        }
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        _life.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
