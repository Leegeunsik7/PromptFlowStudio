using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🚀 Step 37 — 호스트 기동 백그라운드 자율 데몬(<see cref="PlutoBackgroundAutonomyBootstrapper"/>) · 통합 자율 활동 피드(<see cref="PlutoAutonomousActivityFeedService"/>).
/// 환경변수는 주입 읽기 함수로 대체(프로세스 환경 무변경 → 병렬 안전) · 시계는 수동 <see cref="TimeProvider"/>(벽시계 대기 0).
/// </summary>
public sealed class PlutoStartupBootstrapperAndActivityFeedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_boot37_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);   // 태평양 11-20 01:00 · 게시(11-21) > 렌더
    private static readonly string KoChannel = "UC" + new string('B', 22);
    private const string ApiKey = "AIzaSyBOOTKEY-step37-0123456789";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Grace = PlutoYouTubeApiSyncService.AutoSyncStartupGrace;
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoStartupBootstrapperAndActivityFeedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Sub(params string[] parts)
    {
        var p = _dir;
        foreach (var s in parts) p = Path.Combine(p, s);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>킬 스위치 없는 깨끗한 환경(주입).</summary>
    private static string? CleanEnv(string _) => null;

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) => name =>
    {
        foreach (var (n, v) in vars) if (string.Equals(n, name, StringComparison.Ordinal)) return v;
        return null;
    };

    private static byte[] Export(string id, long views)
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + id + ",가을 산책 브이로그 #shorts,\"Nov 22, 2026\"," + views.ToString(CultureInfo.InvariantCulture) + ",60,5000,5.0,66.0,10\n");

    private static string Vid(char prefix, int n) => prefix + n.ToString("D10", CultureInfo.InvariantCulture);

    private static PlutoRenderedClipRecord Clip(string id, string title)
        => new(id, title, "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "autumn" }, "", T0, false);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ── 가짜 수집기 · 감시기 옵션 ──

    private sealed class ScriptedImporter : IPlutoTelemetryBatchImporter
    {
        public int Calls;
        public async Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            return new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>()) { IsYouTubeStudioReport = true };
        }

        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private YouTubeDownloadsWatcherOptions WatchOpt(string name) => new()
    {
        Directory = Sub(name, "Downloads"),
        StatePath = Path.Combine(_dir, name, "state", "downloads_watcher.json"),
        Debounce = TimeSpan.FromMilliseconds(60),
        SweepInterval = TimeSpan.Zero,
        ReadinessRetries = 30,
        ReadinessRetryDelay = TimeSpan.FromMilliseconds(30),
        StabilityProbe = TimeSpan.FromMilliseconds(15),
        UseFileSystemWatcher = false,
    };

    // ── 수동 시계 — Advance 가 예정 시각에 도달한 단발 타이머만 발화 ──

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now;

        public ManualClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(t);
            t.Change(dueTime, period);
            return t;
        }

        public int ActiveTimers
        {
            get
            {
                lock (_gate)
                {
                    var n = 0;
                    foreach (var t in _timers) if (!t.Disposed && t.Due is not null) n++;
                    return n;
                }
            }
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_gate) target = _now + by;
            while (true)
            {
                ManualTimer? next = null;
                lock (_gate)
                {
                    foreach (var t in _timers)
                        if (!t.Disposed && t.Due is { } d && d <= target && (next is null || d < next.Due!.Value)) next = t;
                    if (next is null) { _now = target; return; }
                    if (next.Due!.Value > _now) _now = next.Due.Value;
                    next.Due = null;
                }
                next.Callback(next.State);
            }
        }

        public sealed class ManualTimer : ITimer
        {
            private readonly ManualClock _clock;
            public readonly TimerCallback Callback;
            public readonly object? State;
            public DateTimeOffset? Due;
            public bool Disposed;

            public ManualTimer(ManualClock clock, TimerCallback cb, object? state) { _clock = clock; Callback = cb; State = state; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_clock._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._now + dueTime;
                    return true;
                }
            }

            public void Dispose() { lock (_clock._gate) { Disposed = true; Due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    // ── 가짜 YouTube Data API v3(업로드 1건) ──

    private sealed class FakeYouTube : HttpMessageHandler
    {
        private int _requests;
        public readonly List<(string Id, string Title, long Views)> Uploads = new();
        public int RequestCount => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var url = request.RequestUri!.ToString();
            if (!url.StartsWith(PlutoYouTubeApiSyncService.ApiBase, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            var path = url[PlutoYouTubeApiSyncService.ApiBase.Length..];
            if (path.StartsWith("channels", StringComparison.Ordinal))
                return Json("{\"items\":[{\"id\":\"" + KoChannel + "\",\"contentDetails\":{\"relatedPlaylists\":{\"uploads\":\"UU" + KoChannel[2..] + "\"}}}]}");
            if (path.StartsWith("playlistItems", StringComparison.Ordinal))
            {
                var sb = new StringBuilder("{\"items\":[");
                for (var i = 0; i < Uploads.Count; i++) sb.Append(i > 0 ? "," : "").Append("{\"contentDetails\":{\"videoId\":\"").Append(Uploads[i].Id).Append("\"}}");
                return Json(sb.Append("]}").ToString());
            }
            if (path.StartsWith("videos", StringComparison.Ordinal))
            {
                var sb = new StringBuilder("{\"items\":[");
                for (var i = 0; i < Uploads.Count; i++)
                {
                    var v = Uploads[i];
                    sb.Append(i > 0 ? "," : "").Append("{\"id\":\"").Append(v.Id).Append("\",\"snippet\":{\"title\":").Append(System.Text.Json.JsonSerializer.Serialize(v.Title))
                      .Append(",\"publishedAt\":\"2026-11-21T03:00:00Z\",\"channelId\":\"").Append(KoChannel).Append("\"},\"statistics\":{\"viewCount\":\"")
                      .Append(v.Views.ToString(CultureInfo.InvariantCulture)).Append("\",\"likeCount\":\"3\",\"commentCount\":\"1\"}}");
                }
                return Json(sb.Append("]}").ToString());
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private PlutoYouTubeApiSyncService Api(FakeYouTube handler, ManualClock clock, string store, IPlutoTelemetryBatchImporter? importer = null)
        => new(importer, handler, Sub(store)) { TimeProvider = clock, Delay = static (_, _) => Task.CompletedTask };

    private static async Task<PlutoYouTubeApiSyncService> Configured(PlutoYouTubeApiSyncService svc)
    {
        var save = await svc.SaveCredentialsAsync(new PlutoYouTubeApiCredentials { ApiKey = ApiKey, Channels = new[] { new PlutoYouTubeApiChannel(KoChannel, "ko-KR") } });
        Assert.True(save.Ok, save.MessageKorean);
        return svc;
    }

    private PlutoAutonomousActivityFeedService Feed(string name, TimeProvider? clock = null) => new(Path.Combine(Sub(name), PlutoAutonomousActivityFeedService.FeedFileName), clock);

    private static PlutoYouTubeApiSyncResult ApiResult(PlutoYouTubeApiSyncState state, int linked = 0, int recovered = 0, int observed = 0)
        => new(state, "요약") { Import = new PlutoTelemetryBatchImportResult(observed, recovered, 0, 0, Array.Empty<string>()) { NewlyLinkedCount = linked, QueueRecoveredCount = recovered, ApiObservedCount = observed, IsYouTubeApiSync = true } };

    // ── ① 호스트 기동 — 감시 · API 주기 동기화 1회 멱등 기동 · 기존 예정 유지 · 피드 브리지 1회 ──

    [Fact]
    public async Task Bootstrapper_StartsWatcherAndApiPollerOnceAtHostBoot_JoinsConcurrentStarts_AndPreservesExistingApiSchedule()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "boot_catalog.json"));
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "boot_nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, null);
        using var queue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "boot_queue.json"), catalog, loop);
        var importer = new PlutoTelemetryBatchImporter(catalog, loop, queue);
        await catalog.RecordRenderedClipAsync(Clip("boot_clip", "첫눈 오는 골목 산책"));
        var fake = new FakeYouTube();
        fake.Uploads.Add((Vid('b', 1), "첫눈 오는 골목 산책", 900));
        var clock = new ManualClock(T0);
        using var api = await Configured(Api(fake, clock, "boot_api", importer));

        // 이전 Workspace 진입이 이미 예약해 둔 상태 → 1분 뒤 호스트 기동
        Assert.True(api.StartAutoSync());
        var due = api.GetAutoSyncStatus().NextDueUtc;
        Assert.Equal(T0 + Grace, due);
        clock.Advance(TimeSpan.FromMinutes(1));

        var opt = WatchOpt("boot");
        await using var watcher = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), opt);
        Assert.False(watcher.IsRunning);
        using var feed = Feed("boot_feed");
        using var boot = new PlutoBackgroundAutonomyBootstrapper(watcher, api, null, feed, CleanEnv, clock) { BootDelay = TimeSpan.Zero };

        var starts = new Task<PlutoAutonomyBootReport>[6];
        for (var i = 0; i < starts.Length; i++) starts[i] = boot.StartAsync();   // 호스트 OnShown · Workspace 진입 · 재진입이 겹침
        var reports = await Task.WhenAll(starts).WaitAsync(Wait);
        foreach (var r in reports) Assert.Same(reports[0], r);
        Assert.Equal(1, boot.BootCount);
        Assert.True(boot.IsStarted);
        var rep = reports[0];
        Assert.True(rep.WatcherRunning, rep.SummaryKorean);
        Assert.True(rep.ApiAutoSyncArmed, rep.SummaryKorean);
        Assert.True(rep.FeedWired);
        Assert.True(watcher.IsRunning);
        Assert.Equal(due, api.GetAutoSyncStatus().NextDueUtc);   // ⏱️ 예정 재설정 없음
        Assert.Equal(1, clock.ActiveTimers);
        Assert.Equal(0, fake.RequestCount);                       // 기동 즉시 요청 없음

        // Workspace 재진입(기존 결선) — 같은 데몬 재사용
        Assert.True(watcher.Start());
        Assert.True(api.StartAutoSync());
        Assert.Same(rep, await boot.StartAsync());
        Assert.Equal(1, boot.BootCount);
        Assert.Equal(due, api.GetAutoSyncStatus().NextDueUtc);
        Assert.Equal(1, clock.ActiveTimers);

        // 백그라운드 수집 → 피드 1건(구독 1회 → 중복 없음)
        File.WriteAllBytes(Path.Combine(opt.Directory!, "Content 2026-11-23.csv"), Export("ytBOOTBOOT1", 400));
        Assert.Equal(1, await watcher.SweepNowAsync());
        Assert.Equal(1, feed.Count);
        Assert.Equal(PlutoActivityKind.DownloadsIngest, feed.Latest!.Kind);
        Assert.Contains("Content 2026-11-23.csv", feed.Latest.MessageKorean, StringComparison.Ordinal);

        // 예정 시각 도달 → API 자동 동기화가 Workspace 없이 새 클립 연결 → 피드 기록
        var tick = new TaskCompletionSource<PlutoYouTubeApiAutoSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.AutoSyncCompleted += e => tick.TrySetResult(e);
        clock.Advance(Grace - TimeSpan.FromMinutes(1));
        var ev = await tick.Task.WaitAsync(Wait);
        Assert.True(ev.NewlyLinkedCount >= 1);
        Assert.Equal(2, feed.Count);
        Assert.Equal(PlutoActivityKind.ApiSync, feed.Latest!.Kind);
        Assert.Contains("API 동기화(자동)", feed.Latest.MessageKorean, StringComparison.Ordinal);
        Assert.Contains("새 연결", feed.Latest.MessageKorean, StringComparison.Ordinal);
        Assert.Equal("boot_clip", (await catalog.FindByYouTubeVideoIdAsync(Vid('b', 1)))!.VideoId);
    }

    // ── ② 킬 스위치 — PROMPTFLOW_PLUTO_DRIVE=off · 개별 차단 · 미설정/꺼짐 ──

    [Fact]
    public async Task Bootstrapper_HonorsOfflineAndPerServiceKillSwitches_NeverTouchingDownloadsOrNetwork()
    {
        var fake = new FakeYouTube();
        var clock = new ManualClock(T0);

        async Task<(PlutoAutonomyBootReport Rep, bool WatchRunning, bool Armed)> Boot(string name, Func<string, string?> env)
        {
            using var api = await Configured(Api(fake, clock, name + "_api"));
            await using var w = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), WatchOpt(name));
            using var b = new PlutoBackgroundAutonomyBootstrapper(w, api, null, null, env, clock) { BootDelay = TimeSpan.Zero };
            var r = await b.StartAsync().WaitAsync(Wait);
            return (r, w.IsRunning, api.IsAutoSyncArmed);
        }

        var off = await Boot("off", Env((PlutoDriveStorageManager.DriveEnvVar, " OFF ")));
        Assert.False(off.WatchRunning);
        Assert.False(off.Armed);
        Assert.Equal("격리 스위치", off.Rep.WatcherSkipReason);
        Assert.Equal("격리 스위치", off.Rep.ApiSkipReason);

        var noWatch = await Boot("nowatch", Env((YouTubeDownloadsAutoWatcherService.DisableEnvVar, "1")));
        Assert.False(noWatch.WatchRunning);
        Assert.True(noWatch.Armed);

        var noApi = await Boot("noapi", Env((PlutoYouTubeApiSyncService.DisableEnvVar, "true")));
        Assert.True(noApi.WatchRunning);
        Assert.False(noApi.Armed);
        Assert.Equal("격리 스위치", noApi.Rep.ApiSkipReason);
        Assert.Equal(0, fake.RequestCount);

        // 실제 테스트 프로세스 환경(모듈 초기화 PROMPTFLOW_PLUTO_DRIVE=off) — 기본 읽기 함수도 둘 다 차단
        using (var api = await Configured(Api(fake, clock, "proc_api")))
        await using (var w = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), WatchOpt("proc")))
        using (var b = new PlutoBackgroundAutonomyBootstrapper(w, api, null, null, timeProvider: clock) { BootDelay = TimeSpan.Zero })
        {
            Assert.True(b.OfflineHarness);
            var r = await b.StartAsync().WaitAsync(Wait);
            Assert.False(r.WatcherRunning);
            Assert.False(r.ApiAutoSyncArmed);
            Assert.False(w.IsRunning);
            Assert.False(api.IsAutoSyncArmed);
        }

        // 미설정 · 꺼짐 · 감시 토글 OFF → 시작하지 않음(이유 표기)
        using (var unset = Api(fake, clock, "unset_api"))
        {
            var w = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), WatchOpt("toggled"));
            await w.SetEnabledAsync(false);
            using var b = new PlutoBackgroundAutonomyBootstrapper(w, unset, null, null, CleanEnv, clock) { BootDelay = TimeSpan.Zero };
            var r = await b.StartAsync().WaitAsync(Wait);
            Assert.Equal("자격 증명 · 채널 미설정", r.ApiSkipReason);
            Assert.Equal("꺼짐", r.WatcherSkipReason);
            Assert.False(w.IsRunning);
            await w.DisposeAsync();
        }
        using (var disabled = await Configured(Api(fake, clock, "disabled_api")))
        {
            await disabled.SetAutoSyncEnabledAsync(false);
            using var b = new PlutoBackgroundAutonomyBootstrapper(null, disabled, null, null, CleanEnv, clock) { BootDelay = TimeSpan.Zero };
            var r = await b.StartAsync().WaitAsync(Wait);
            Assert.Equal("꺼짐", r.ApiSkipReason);
            Assert.Equal("미등록", r.WatcherSkipReason);
            Assert.False(disabled.IsAutoSyncArmed);
        }
        Assert.Equal(0, fake.RequestCount);
    }

    // ── ③ 피드 — 4종 기록 · 30건 유계 롤링 · 재시작 복원 · 경로/비밀값 가림 ──

    [Fact]
    public async Task ActivityFeed_RecordsAllAutonomousKinds_BoundedRollingAcrossRestarts_WithFullRedaction()
    {
        var clock = new ManualClock(T0);
        var path = Path.Combine(Sub("feed"), PlutoAutonomousActivityFeedService.FeedFileName);
        using (var feed = new PlutoAutonomousActivityFeedService(path, clock))
        {
            // API — 새 연결 · 대기열 회수만 · 조회수 갱신만은 무시 · 정지는 하루 1건
            Assert.True(feed.RecordApiSync(ApiResult(PlutoYouTubeApiSyncState.Synced, linked: 2, recovered: 1, observed: 9), background: true));
            Assert.False(feed.RecordApiSync(ApiResult(PlutoYouTubeApiSyncState.Synced, observed: 9), background: true));
            Assert.True(feed.RecordApiSync(new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.QuotaCooldown, "q"), background: true));
            Assert.False(feed.RecordApiSync(new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.QuotaCooldown, "q"), background: false));
            Assert.False(feed.RecordApiSync(new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Busy, "b"), background: false));
            // Downloads — 정리 표기
            var imp = new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>()) { MaturityDeltaCount = 2 };
            Assert.True(feed.RecordDownloadsIngest(new PlutoDownloadsAutoIngestEvent("Content 2026-11-23.csv", new string('A', 64), imp, T0) { Archived = true }));
            // 기획 채점 · 사후 분석(−12) · 유기적 복권(+8)
            var severe = new PlutoBriefVerdict("clip_bad", "brief-7", 50, 30, -20, false, true) { PostMortemRecorded = true, PostMortemKeys = new[] { "hook:pov-walk", "pair:pov-walk|x" }, FailureDimension = "hook" };
            var hit = new PlutoBriefVerdict("clip_good", "brief-8", 40, 55, 15, true, false);
            var sweep = new PlutoBriefSweepResult(2, 1, 1, new[] { severe, hit }) { RehabilitatedKeys = new[] { "look:Vintage" } };
            Assert.Equal(3, feed.RecordBriefSweep(sweep));
            Assert.Equal(0, feed.RecordBriefSweep(sweep));   // 같은 결과 재통지

            var recent = feed.GetRecent();
            Assert.Equal(6, recent.Count);
            Assert.Equal(PlutoActivityKind.OrganicRehabilitation, recent[0].Kind);   // 최신 순
            Assert.Contains("+8pt", recent[0].MessageKorean, StringComparison.Ordinal);
            Assert.Contains(recent, e => e.Kind == PlutoActivityKind.BriefPostMortem && e.MessageKorean.Contains("-12pt", StringComparison.Ordinal) && e.MessageKorean.Contains("오답 노트 벌점 2개", StringComparison.Ordinal));
            Assert.Contains(recent, e => e.Kind == PlutoActivityKind.BriefGrading && e.MessageKorean.Contains("적중 1건", StringComparison.Ordinal));
            Assert.Contains(recent, e => e.Kind == PlutoActivityKind.DownloadsIngest && e.MessageKorean.Contains("Pluto_Ingested/", StringComparison.Ordinal));
            Assert.Contains(recent, e => e.Kind == PlutoActivityKind.ApiPause && e.MessageKorean.Contains("quotaExceeded", StringComparison.Ordinal));
            Assert.Contains(recent, e => e.Kind == PlutoActivityKind.ApiSync && e.MessageKorean.Contains("새 연결 2건", StringComparison.Ordinal) && e.MessageKorean.Contains("회수 1건", StringComparison.Ordinal));

            // 비밀값 · 절대 경로가 섞인 메시지
            Assert.True(feed.Record(PlutoActivityKind.DownloadsIngest, "수집 C:\\Users\\홍 길동\\Downloads\\Table data.csv key=" + ApiKey + " Bearer ya29.a0AfH6SMBxSECRET refresh 1//0gSECRETREFRESHTOKEN"));
            Assert.True(feed.Record(PlutoActivityKind.ApiSync, "실패 \\\\nas01\\share\\creator\\x.json · /home/alice/pluto/y.json · file:///C:/Users/bob/z.csv · GOCSPX-abcdefSECRET"));
            for (var i = 0; i < 40; i++) { clock.Advance(TimeSpan.FromSeconds(1)); feed.Record(PlutoActivityKind.BriefGrading, "롤링 " + i.ToString(CultureInfo.InvariantCulture)); }
            Assert.Equal(PlutoAutonomousActivityFeedService.Capacity, feed.Count);
            Assert.Equal("롤링 39", feed.Latest!.MessageKorean);
            Assert.Equal("롤링 10", feed.GetRecent()[^1].MessageKorean);   // 가장 오래된 10건(+8건)이 밀려남
            await feed.FlushAsync().WaitAsync(Wait);
        }
        using (var restarted = new PlutoAutonomousActivityFeedService(path, clock))
        {
            Assert.Equal(PlutoAutonomousActivityFeedService.Capacity, restarted.Count);
            Assert.Equal("롤링 39", restarted.Latest!.MessageKorean);
            var seq = restarted.Latest.Seq;
            Assert.True(restarted.Record(PlutoActivityKind.ApiSync, "재시작 후"));
            Assert.Equal(seq + 1, restarted.Latest!.Seq);   // 단조 증가 Seq 이어짐
            await restarted.FlushAsync().WaitAsync(Wait);
        }

        // 가림 — 메모리 · 디스크 · 표시 모두
        using var probe = new PlutoAutonomousActivityFeedService(Path.Combine(Sub("probe"), PlutoAutonomousActivityFeedService.FeedFileName), clock);
        probe.Record(PlutoActivityKind.DownloadsIngest, "수집 C:\\Users\\홍 길동\\Downloads\\Table data.csv key=" + ApiKey + " Bearer ya29.a0AfH6SMBxSECRET refresh 1//0gSECRETREFRESHTOKEN");
        probe.Record(PlutoActivityKind.ApiSync, "실패 \\\\nas01\\share\\creator\\x.json · /home/alice/pluto/y.json · file:///C:/Users/bob/z.csv · GOCSPX-abcdefSECRET");
        await probe.FlushAsync().WaitAsync(Wait);
        var disk = File.ReadAllText(probe.StorePath!);
        var lines = new List<string>();
        foreach (var e in probe.GetRecent()) lines.Add(e.ToLine());
        var shown = string.Join("\n", lines);
        foreach (var leaked in new[] { "홍 길동", "Users", ApiKey, "ya29.a0", "SECRET", "1//0g", "nas01", "alice", "bob", "C:\\", "C:/" })
        {
            Assert.DoesNotContain(leaked, shown, StringComparison.Ordinal);
            Assert.DoesNotContain(leaked, disk, StringComparison.Ordinal);
        }
        Assert.Contains("Table data.csv", shown, StringComparison.Ordinal);   // 파일 이름은 유지
        Assert.Contains("‹경로›", shown, StringComparison.Ordinal);
    }

    // ── ④ Workspace 상단 3행 헤더 불변 · 두뇌 리포트 본문 최근 활동 · Settings 피드 패널 · DI 양쪽 · 허브 불변식 ──

    [Fact]
    public void WorkspaceHeaderInvariant_BrainReportActivityLine_SettingsFeedPanel_DualHostWiring_AndHubInvariantsUntouched()
    {
        var root = RepoRoot();
        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        Assert.Equal(1, CountOf(ws, "data-brain-activity="));
        Assert.True(open >= 0 && body.IndexOf("@if (_activityLatest is { } act)", StringComparison.Ordinal) > open);
        Assert.True(body.IndexOf("data-brain-activity=", StringComparison.Ordinal) > open);
        foreach (var keyword in new[] { "자율 활동", "data-brain-activity", "ActivityFeed", "_activityLatest", "Bootstrapper" })
            Assert.DoesNotContain(keyword, ws.Substring(0, strip), StringComparison.Ordinal);
        Assert.Contains("IPlutoAutonomousActivityFeedService)?.RecordApiSync(r, background: false);", ws, StringComparison.Ordinal);
        Assert.Contains("IPlutoBackgroundAutonomyBootstrapper)?.StartInBackground();", ws, StringComparison.Ordinal);
        // 피드 기록은 호스트 브리지 1곳 — 페이지는 백그라운드 이벤트를 피드로 다시 기록하지 않음(이중 기록 차단)
        Assert.Equal(0, CountOf(ws, "RecordDownloadsIngest("));
        Assert.Equal(0, CountOf(ws, "RecordBriefSweep("));
        Assert.Equal(0, CountOf(ws, "background: true"));

        var st = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        Assert.Contains("🛰️ 최근 자율 활동 피드", st, StringComparison.Ordinal);
        Assert.Contains("[🧹 활동 로그 비우기]", st, StringComparison.Ordinal);
        Assert.Contains("data-activity-feed=", st, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(st, "ActivityFeed.Changed += OnActivityChanged"));
        Assert.Equal(1, CountOf(st, "ActivityFeed.Changed -= OnActivityChanged"));
        Assert.Contains("InvokeAsync(() => { if (_disposed) return; RefreshActivity(); StateHasChanged(); })", st, StringComparison.Ordinal);

        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(root, file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoAutonomousActivityFeedService>("));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoBackgroundAutonomyBootstrapper>("));
            Assert.Equal(1, CountOf(di, "IPlutoBackgroundAutonomyBootstrapper)?.StartInBackground();") + CountOf(di, "IPlutoBackgroundAutonomyBootstrapper>()?.StartInBackground();"));
        }

        // 로컬 전용 — Drive 허브 · 병합 허브 · 동반 동기화 파일에 포함되지 않음
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(PlutoDriveHealthService.CorePlutoFileCount, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoDriveHealthService.CompanionSyncFiles.Length);
        Assert.DoesNotContain(PlutoAutonomousActivityFeedService.FeedFileName, PlutoDriveHealthService.PlutoFiles);
        Assert.DoesNotContain(PlutoAutonomousActivityFeedService.FeedFileName, PlutoDriveHealthService.CompanionSyncFiles);
        Assert.DoesNotContain(PlutoAutonomousActivityFeedService.FeedFileName, PlutoConflictMergeService.MergeableHubs);
        Assert.Contains(PlutoYouTubeApiSyncService.LocalOnlyFolderName, PlutoAutonomousActivityFeedService.DefaultStorePath(), StringComparison.Ordinal);
    }

    // ── ⑤ 🛡️ 자체 추가 A — 호스트 단일 구독 + 영속 중복 키(이중 구독 · 재발행 · 재시작 재생 차단) ──

    [Fact]
    public async Task SelfAddition_HostOwnedSingleBridge_AndPersistedDedupeKeys_PreventDuplicateFeedEntries()
    {
        var opt = WatchOpt("dedupe");
        await using var watcher = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), opt);
        var path = Path.Combine(Sub("dedupe_feed"), PlutoAutonomousActivityFeedService.FeedFileName);
        using (var feed = new PlutoAutonomousActivityFeedService(path))
        {
            using var boot = new PlutoBackgroundAutonomyBootstrapper(watcher, null, null, feed, CleanEnv) { BootDelay = TimeSpan.Zero };
            for (var i = 0; i < 5; i++) await boot.StartAsync().WaitAsync(Wait);
            boot.StartInBackground();
            await boot.StartAsync().WaitAsync(Wait);
            Assert.Equal(1, boot.BootCount);

            File.WriteAllBytes(Path.Combine(opt.Directory!, "Content A.csv"), Export("ytDEDUPE001", 300));
            File.WriteAllBytes(Path.Combine(opt.Directory!, "Content B.csv"), Export("ytDEDUPE002", 300));
            Assert.Equal(2, await watcher.SweepNowAsync());
            Assert.Equal(2, feed.Count);   // 구독 1회 → 파일당 정확히 1건

            // 같은 해시 재발행(이중 경로 · 재통지) → 무시
            var bytes = File.Exists(Path.Combine(opt.Directory!, "Content A.csv")) ? File.ReadAllBytes(Path.Combine(opt.Directory!, "Content A.csv")) : Export("ytDEDUPE001", 300);
            var sha = YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(bytes);
            var imp = new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>());
            Assert.False(feed.RecordDownloadsIngest(new PlutoDownloadsAutoIngestEvent("Content A.csv", sha, imp, T0)));
            Assert.Equal(2, feed.Count);
            await feed.FlushAsync().WaitAsync(Wait);
        }
        using var restarted = new PlutoAutonomousActivityFeedService(path);
        var sha2 = YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(Export("ytDEDUPE002", 300));
        Assert.False(restarted.RecordDownloadsIngest(new PlutoDownloadsAutoIngestEvent("Content B.csv", sha2, new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>()), T0)));   // 재시작 후에도 키 유지
        Assert.Equal(2, restarted.Count);
        Assert.DoesNotContain(sha2, File.ReadAllText(path).Replace("dl:" + sha2, "", StringComparison.Ordinal), StringComparison.Ordinal);   // 해시는 키로만 · 메시지엔 없음

        // 폐기 후 브리지 해제 — 더 이상 기록하지 않음
        var fresh = new PlutoAutonomousActivityFeedService(Path.Combine(Sub("dispose_feed"), PlutoAutonomousActivityFeedService.FeedFileName));
        var opt2 = WatchOpt("dispose");
        await using var w2 = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), opt2);
        var b2 = new PlutoBackgroundAutonomyBootstrapper(w2, null, null, fresh, CleanEnv) { BootDelay = TimeSpan.Zero };
        await b2.StartAsync().WaitAsync(Wait);
        b2.Dispose();
        File.WriteAllBytes(Path.Combine(opt2.Directory!, "Content C.csv"), Export("ytDEDUPE003", 300));
        Assert.Equal(1, await w2.SweepNowAsync());
        Assert.Equal(0, fresh.Count);
        fresh.Dispose();
    }

    // ── ⑥ 🛡️ 자체 추가 B — 종료 경합: 기동 유예 중 폐기 → 타이머 · 감시 · 구독 부활 없음 ──

    [Fact]
    public async Task SelfAddition_DisposeDuringBootGrace_CancelsBoot_WithoutResurrectingDaemonsOrBridges()
    {
        var clock = new ManualClock(T0);
        var fake = new FakeYouTube();
        using var api = await Configured(Api(fake, clock, "race_api"));
        await using var watcher = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), WatchOpt("race"));
        using var feed = Feed("race_feed");
        var boot = new PlutoBackgroundAutonomyBootstrapper(watcher, api, null, feed, CleanEnv, clock) { BootDelay = TimeSpan.FromSeconds(3) };
        var pending = boot.StartAsync();
        Assert.True(SpinWait.SpinUntil(() => clock.ActiveTimers == 1, 5000));   // 유예 타이머만(UI 스레드 무차단 · 디스크 I/O 미시작)
        Assert.False(pending.IsCompleted);
        Assert.False(watcher.IsRunning);

        boot.Dispose();
        var rep = await pending.WaitAsync(Wait);
        Assert.Equal("종료됨", rep.WatcherSkipReason);
        Assert.False(rep.FeedWired);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(watcher.IsRunning);
        Assert.False(api.IsAutoSyncArmed);
        Assert.Equal(0, fake.RequestCount);
        Assert.Equal("종료됨", (await boot.StartAsync()).WatcherSkipReason);   // 폐기 후 재기동 불가
        Assert.Equal(0, feed.Count);
    }

    // ── ⑦ 🛡️ 자체 추가 C — 비차단 기록 · 영속 실패 격리 · 클라우드 경로 거부 · 구독자 격리 ──

    [Fact]
    public async Task SelfAddition_NonBlockingRecording_IsolatesPersistFailure_RejectsCloudStore_AndThrowingSubscribers()
    {
        // 영속 대상 상위가 '파일' → 쓰기 실패해도 기록 · 표시는 계속([부분 구제])
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        using (var broken = new PlutoAutonomousActivityFeedService(Path.Combine(blocker, "sub", PlutoAutonomousActivityFeedService.FeedFileName)))
        {
            var ok = 0;
            broken.Changed += static () => throw new ObjectDisposedException("circuit");   // 끊긴 Blazor 회로
            broken.Changed += () => Interlocked.Increment(ref ok);
            Assert.True(broken.Record(PlutoActivityKind.ApiSync, "기록 1"));
            Assert.True(broken.Record(PlutoActivityKind.ApiSync, "기록 2"));
            await broken.FlushAsync().WaitAsync(Wait);
            Assert.Equal(2, broken.Count);
            Assert.Equal(2, Volatile.Read(ref ok));
            Assert.NotNull(broken.LastPersistError);
        }

        // ☁️ 클라우드 동기화 폴더 → 저장 거부(메모리 전용 · 파일 생성 0)
        var cloud = Path.Combine(Sub("OneDrive", "PromptFlowStudio"), PlutoAutonomousActivityFeedService.FeedFileName);
        using (var rejected = new PlutoAutonomousActivityFeedService(cloud))
        {
            Assert.True(rejected.StoreRejected);
            Assert.Null(rejected.StorePath);
            Assert.True(rejected.Record(PlutoActivityKind.DownloadsIngest, "메모리 전용"));
            await rejected.FlushAsync().WaitAsync(Wait);
            Assert.False(File.Exists(cloud));
        }

        // 🔒 수정/구버전 파일의 원문 경로 · 비밀값 → 적재 시 재가림 · 손상 항목 건너뜀 · 🧹 비우기 영속
        var path = Path.Combine(Sub("tampered"), PlutoAutonomousActivityFeedService.FeedFileName);
        File.WriteAllText(path, "{\"V\":1,\"NextSeq\":5,\"Entries\":[{\"Seq\":3,\"At\":\"2026-11-22T00:00:00+00:00\",\"Kind\":\"ApiSync\",\"Msg\":\"동기화 D:\\\\Creators\\\\kim\\\\token.json key=" + ApiKey + "\"},{\"Seq\":4,\"At\":\"2026-11-22T00:00:00+00:00\",\"Kind\":\"NotAKind\",\"Msg\":\"x\"}]}");
        using (var loaded = new PlutoAutonomousActivityFeedService(path))
        {
            Assert.Equal(1, loaded.Count);
            var msg = loaded.Latest!.MessageKorean;
            Assert.DoesNotContain("kim", msg, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiKey, msg, StringComparison.Ordinal);
            Assert.Contains("token.json", msg, StringComparison.Ordinal);
            Assert.True(loaded.Record(PlutoActivityKind.ApiSync, "다음"));
            Assert.Equal(5, loaded.Latest!.Seq);
            await loaded.ClearAsync().WaitAsync(Wait);
            Assert.Equal(0, loaded.Count);
            Assert.Null(loaded.Latest);
        }
        using var afterClear = new PlutoAutonomousActivityFeedService(path);
        Assert.Equal(0, afterClear.Count);
    }
}
