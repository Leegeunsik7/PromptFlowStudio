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
/// ☁️ Step 38 — 기동 멀티 PC Drive · 미매칭 대기열 선조정(<see cref="PlutoStartupDriveSyncService"/> → <see cref="PlutoBackgroundAutonomyBootstrapper"/>) ·
/// 미해결 충돌 피드 알림 · 블랙박스 진단서(용어집 요약 · 최근 자율 활동).
/// 환경변수는 주입 읽기 함수(프로세스 환경 무변경 → 병렬 안전) · 시계는 수동 <see cref="TimeProvider"/>(벽시계 대기 0) · Drive 는 임시 폴더 2개로 모사.
/// </summary>
public sealed class PlutoStartupDriveSyncAndBlackBoxDiagnosticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_sync38_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly string KoChannel = "UC" + new string('C', 22);
    private const string ApiKey = "AIzaSyBOOTKEY-step38-0123456789";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    public PlutoStartupDriveSyncAndBlackBoxDiagnosticTests() => Directory.CreateDirectory(_dir);

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

    private static string? CleanEnv(string _) => null;

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) => name =>
    {
        foreach (var (n, v) in vars) if (string.Equals(n, name, StringComparison.Ordinal)) return v;
        return null;
    };

    /// <summary>☁️ 모사 Drive — Pluto 폴더의 부모가 있어야 활성 · 로컬 폴백은 별도 폴더.</summary>
    private (PlutoDriveStorageManager Storage, string Primary, string Local) Drive(string name)
    {
        var primary = Path.Combine(Sub(name, "gdrive"), "Pluto");
        Directory.CreateDirectory(primary);
        var local = Sub(name, "local");
        return (new PlutoDriveStorageManager(primary, local), primary, local);
    }

    private static PlutoRenderedClipRecord Clip(string id, string title)
        => new(id, title, "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "autumn" }, "", T0, false);

    private static PlutoUnmatchedYouTubeRow Pending(string ytId, string title, long views)
        => new(new YouTubeAnalyticsRow(ytId, title, T0.AddDays(1), views, 66, false, 48, 48, 5.0, false), "unmatched", "Table data.csv", T0);

    private PlutoAutonomousActivityFeedService Feed(string name, TimeProvider? clock = null) => new(Path.Combine(Sub(name), PlutoAutonomousActivityFeedService.FeedFileName), clock);

    private static int CountKind(IPlutoAutonomousActivityFeedService feed, PlutoActivityKind kind)
    {
        var n = 0;
        foreach (var e in feed.GetRecent()) if (e.Kind == kind) n++;
        return n;
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
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

    // ── 가짜 ──

    private sealed class ScriptedImporter : IPlutoTelemetryBatchImporter
    {
        public async Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
        {
            await Task.Yield();
            return new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>()) { IsYouTubeStudioReport = true };
        }

        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    /// <summary>재적재 횟수만 세는 허브.</summary>
    private sealed class CountingHub : IPlutoHotReloadable
    {
        public CountingHub(string file) => HubFileName = file;
        public string HubFileName { get; }
        public int Reloads;
        public bool HasUnappliedDiskUpdate => false;
        public Task<bool> ReloadFromDiskAsync(CancellationToken ct = default) { Interlocked.Increment(ref Reloads); return Task.FromResult(true); }
    }

    /// <summary>미해결 충돌 목록만 제공(나머지 = 기본 구현).</summary>
    private sealed class FakeHealth : IPlutoDriveHealthService
    {
        public List<PlutoConflictCopy> Conflicts = new();
        public int ListCalls;
        public PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null) => throw new NotSupportedException();
        public Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<PlutoConflictCopy> ListConflicts() { Interlocked.Increment(ref ListCalls); return Conflicts.ToArray(); }
    }

    /// <summary>테스트가 완료 시점을 통제하는 기동 조정.</summary>
    private sealed class GatedSync : IPlutoStartupDriveSyncService
    {
        public readonly TaskCompletionSource<PlutoStartupDriveSyncReport> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public PlutoStartupDriveSyncReport? LastReport => Gate.Task.IsCompletedSuccessfully ? Gate.Task.Result : null;
        public int RunCount => Volatile.Read(ref Calls);
        public Task<PlutoStartupDriveSyncReport> RunOnceAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult();
            return Gate.Task;
        }
    }

    private sealed class FakeYouTube : HttpMessageHandler
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json") });
        }
    }

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

    private async Task<PlutoYouTubeApiSyncService> ConfiguredApi(FakeYouTube handler, ManualClock clock, string store)
    {
        var svc = new PlutoYouTubeApiSyncService(null, handler, Sub(store)) { TimeProvider = clock, Delay = static (_, _) => Task.CompletedTask };
        var save = await svc.SaveCredentialsAsync(new PlutoYouTubeApiCredentials { ApiKey = ApiKey, Channels = new[] { new PlutoYouTubeApiChannel(KoChannel, "ko-KR") } });
        Assert.True(save.Ok, save.MessageKorean);
        return svc;
    }

    private static PlutoConflictCopy Conflict(string hub, int minute, long size, string fullPath = @"C:\Users\creator\AppData\Local\PromptFlowStudio\Data\Pluto\conflicts\x.json")
        => new(hub, fullPath, new DateTime(2026, 11, 22, 8, minute, 0, DateTimeKind.Utc), size, true, true);

    // ── ① 감시 · 폴러 기동 전 Drive 조정 + 다른 PC 연결분 대기열 정리 ──

    [Fact]
    public async Task Bootstrapper_RunsDriveSyncAndUnmatchedReconcile_BeforeWatcherAndApiPoller_PruningIdsLinkedOnAnotherPc()
    {
        var (storage, primary, local) = Drive("order");

        // PC B 가 Drive 카탈로그에 YouTube ID 를 이미 연결해 둠(이 PC 메모리 카탈로그는 모름)
        using (var peer = new PlutoRenderCatalogService(Path.Combine(primary, PlutoRenderCatalogService.FileName)))
        {
            await peer.RecordRenderedClipAsync(Clip("clip_pc_b", "첫눈 오는 골목 산책"));
            Assert.Equal(PlutoYouTubeBindResult.Bound, await peer.TryBindYouTubeVideoIdAsync("clip_pc_b", "LinkedOnB01"));
        }
        // 이 PC 의 오프라인 학습분(로컬 폴백에만 있는 실험 장부) → 기동 시 Drive 로 쓰기 반환 → 실행 중 허브 즉시 재적재
        File.WriteAllText(Path.Combine(local, PlutoExperimentLedgerService.FileName), "{\"Experiments\":[]}");

        var mine = new PlutoRenderCatalogService(Path.Combine(Sub("order", "pc_a"), PlutoRenderCatalogService.FileName));
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "order_nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, null);
        using var queue = new PlutoYouTubeUnmatchedQueueService(storage, mine, loop);
        Assert.Equal(2, await queue.EnqueueAsync(new[] { Pending("LinkedOnB01", "첫눈 오는 골목 산책", 300), Pending("RowKeep0001", "아직 미연결 영상", 120) }));

        var clock = new ManualClock(T0);
        var fake = new FakeYouTube();
        using var api = await ConfiguredApi(fake, clock, "order_api");
        await using var watcher = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), WatchOpt("order"));
        using var feed = Feed("order_feed");
        var ledgerHub = new CountingHub(PlutoExperimentLedgerService.FileName);
        var queueResolves = 0;
        using var sync = new PlutoStartupDriveSyncService(storage, () => { Interlocked.Increment(ref queueResolves); return queue; }, null, () => new IPlutoHotReloadable[] { ledgerHub }, CleanEnv, clock);

        // 정리 시점의 감시기 · 폴러 상태 포착(대기열 Changed 는 조정 경로 안에서 동기 발생)
        bool? watcherAtPrune = null, apiAtPrune = null;
        queue.Changed += () => { watcherAtPrune ??= watcher.IsRunning; apiAtPrune ??= api.IsAutoSyncArmed; };

        using var boot = new PlutoBackgroundAutonomyBootstrapper(watcher, api, null, feed, CleanEnv, clock) { BootDelay = TimeSpan.Zero, StartupSync = sync };
        var rep = await boot.StartAsync().WaitAsync(Wait);

        Assert.False(watcherAtPrune);   // 📥 감시기는 아직 정지
        Assert.False(apiAtPrune);       // ⏱️ 폴러는 아직 미예약
        Assert.True(rep.WatcherRunning, rep.SummaryKorean);
        Assert.True(rep.ApiAutoSyncArmed, rep.SummaryKorean);
        Assert.Equal(0, fake.RequestCount);

        var ss = rep.StartupSync;
        Assert.NotNull(ss);
        Assert.True(ss!.Attempted);
        Assert.Null(ss.DriveError);
        Assert.True(ss.PushedToDrive >= 1);
        Assert.True(File.Exists(Path.Combine(primary, PlutoExperimentLedgerService.FileName)));
        Assert.Contains(PlutoExperimentLedgerService.FileName, ss.ReloadedHubs);
        Assert.Equal(1, ledgerHub.Reloads);
        Assert.Equal(1, ss.Unmatched!.PrunedLinked);
        Assert.Equal(1, ss.Unmatched.Remaining);
        Assert.Equal(1, queueResolves);
        var pending = queue.GetPending();
        Assert.Single(pending);
        Assert.Equal("RowKeep0001", pending[0].YouTubeVideoId);
        Assert.True(queue.IsResolved("LinkedOnB01"));

        // 🛰️ 피드 — 기동 조정 1건(쓰기 반환 · 재적재 · 대기열 정리) · 충돌 없음 → 충돌 알림 없음
        Assert.Equal(1, CountKind(feed, PlutoActivityKind.DriveSync));
        Assert.Equal(0, CountKind(feed, PlutoActivityKind.DriveConflict));
        var msg = feed.Latest!.MessageKorean;
        Assert.Contains("쓰기 반환", msg, StringComparison.Ordinal);
        Assert.Contains("대기열 정리 1건", msg, StringComparison.Ordinal);
        Assert.Contains(PlutoExperimentLedgerService.FileName, msg, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir, msg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("☁️", feed.Latest.Icon);

        // 재진입 — 같은 조정에 합류(재실행 · 중복 기록 없음)
        Assert.Same(rep, await boot.StartAsync());
        Assert.Same(ss, await sync.RunOnceAsync());
        Assert.Equal(1, sync.RunCount);
        Assert.Equal(1, CountKind(feed, PlutoActivityKind.DriveSync));
    }

    // ── ② 미해결 충돌 알림(조용한 재시작 중복 없음) · PROMPTFLOW_PLUTO_DRIVE=off 완전 무접촉 ──

    [Fact]
    public async Task UnresolvedConflicts_AlertOncePerConflictSet_QuietRestartsDoNotSpam_AndOfflineHarnessIsZeroTouch()
    {
        var (storage, _, _) = Drive("conf");
        var health = new FakeHealth();
        health.Conflicts.Add(Conflict(PlutoMistakeNotebookService.FileName, 1, 300));
        health.Conflicts.Add(Conflict(PlutoRenderCatalogService.FileName, 2, 900));
        var feedPath = Path.Combine(Sub("conf_feed"), PlutoAutonomousActivityFeedService.FeedFileName);

        async Task<(PlutoAutonomyBootReport Rep, int Feed, int Conflicts, int Syncs)> BootOnce()
        {
            using var feed = new PlutoAutonomousActivityFeedService(feedPath);
            using var sync = new PlutoStartupDriveSyncService(storage, null, () => health, null, CleanEnv);
            using var boot = new PlutoBackgroundAutonomyBootstrapper(null, null, null, feed, CleanEnv) { BootDelay = TimeSpan.Zero, StartupSync = sync };
            var rep = await boot.StartAsync().WaitAsync(Wait);
            await feed.FlushAsync().WaitAsync(Wait);
            return (rep, feed.Count, CountKind(feed, PlutoActivityKind.DriveConflict), CountKind(feed, PlutoActivityKind.DriveSync));
        }

        // 1차 기동 — 충돌 2건 알림 1건 · 변경 없는 Drive → 조정 기록 없음
        var first = await BootOnce();
        Assert.Equal(2, first.Rep.StartupSync!.UnresolvedConflicts);
        Assert.Equal(1, first.Conflicts);
        Assert.Equal(0, first.Syncs);
        Assert.Equal(1, first.Feed);

        // 조용한 재시작 ×2 — 같은 충돌 집합(영속 중복 키) → 추가 기록 0
        Assert.Equal(1, (await BootOnce()).Feed);
        Assert.Equal(1, (await BootOnce()).Feed);

        // 새 충돌 사본 → 집합 지문 변경 → 다시 1회 알림
        health.Conflicts.Add(Conflict(PlutoSeriesArcMemoryService.FileName, 3, 50));
        var third = await BootOnce();
        Assert.Equal(2, third.Conflicts);
        Assert.Equal(2, third.Feed);
        using (var reopened = new PlutoAutonomousActivityFeedService(feedPath))
        {
            Assert.Equal(PlutoActivityKind.DriveConflict, reopened.Latest!.Kind);
            Assert.Contains("미해결 Drive 충돌 3건", reopened.Latest.MessageKorean, StringComparison.Ordinal);
            Assert.Contains("설정", reopened.Latest.MessageKorean, StringComparison.Ordinal);
            Assert.DoesNotContain("creator", reopened.Latest.MessageKorean, StringComparison.Ordinal);   // 충돌 사본 경로 비노출
        }

        // 모두 해결 → 조용한 기동(기록 0)
        health.Conflicts.Clear();
        Assert.Equal(2, (await BootOnce()).Feed);

        // 🔒 PROMPTFLOW_PLUTO_DRIVE=off — 부트스트래퍼는 조정 서비스를 호출조차 하지 않음
        var off = Env((PlutoDriveStorageManager.DriveEnvVar, "off"));
        var gated = new GatedSync();
        using (var feed = Feed("off_feed"))
        using (var boot = new PlutoBackgroundAutonomyBootstrapper(null, null, null, feed, off) { BootDelay = TimeSpan.Zero, StartupSync = gated })
        {
            var rep = await boot.StartAsync().WaitAsync(Wait);
            Assert.Equal(0, gated.RunCount);
            Assert.Null(rep.StartupSync);
            Assert.False(rep.StartupSyncTimedOut);
            Assert.Equal(0, feed.Count);
        }
        // 조정 서비스 자체도 격리 스위치 → 저장소 · 대기열 · 충돌 원천 무접촉
        var resolves = 0;
        health.Conflicts.Add(Conflict(PlutoMistakeNotebookService.FileName, 9, 1));
        var listBefore = health.ListCalls;
        using (var sync = new PlutoStartupDriveSyncService(storage, () => { resolves++; return null; }, () => { resolves++; return health; }, () => { resolves++; return Array.Empty<IPlutoHotReloadable>(); }, off))
        {
            var r = await sync.RunOnceAsync().WaitAsync(Wait);
            Assert.False(r.Attempted);
            Assert.Equal("격리 스위치", r.DriveSkipReason);
            Assert.False(r.HasMeaningfulChange);
        }
        Assert.Equal(0, resolves);
        Assert.Equal(listBefore, health.ListCalls);
    }

    // ── ③ 블랙박스 진단서 — 용어집 요약 · 최근 자율 활동(가림 유지 · Markdown 이스케이프) ──

    [Fact]
    public async Task DiagnosticSnapshot_IncludesRedactedRecentActivityAndEscapedGlossarySummary_InClipboardAndFileExport()
    {
        var arc = new PlutoSeriesArcMemoryService(Path.Combine(Sub("diag"), PlutoSeriesArcMemoryService.FileName));
        Assert.True(await arc.UpsertGlossaryEntryAsync("카페 일기 1/3편", new PlutoGlossaryEntry("민준", "Minjun", "ミンジュン", null)));
        Assert.True(await arc.UpsertGlossaryEntryAsync("카페 일기", new PlutoGlossaryEntry("서연", "Seoyeon", null, null)));
        Assert.True(await arc.UpsertGlossaryEntryAsync("겨울 여행 2/4편", new PlutoGlossaryEntry("하늘역", "Haneul Station", null, null)));
        Assert.True(await arc.UpsertGlossaryEntryAsync(null, new PlutoGlossaryEntry("플루토", "Pluto", "プルート", "Plutón")));   // 전역
        Assert.True(await arc.RemoveGlossaryEntryAsync("겨울 여행", "하늘역"));   // 삭제 표식 → 집계 제외
        var sum = arc.GetGlossarySummary();
        Assert.Equal(1, sum.ActiveSeriesCount);
        Assert.Equal(1, sum.GlobalTermCount);
        Assert.Equal(3, sum.TotalProtectedTerms);

        var clock = new ManualClock(T0);
        using var feed = Feed("diag_feed", clock);
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(feed.Record(PlutoActivityKind.DownloadsIngest, "Downloads 자동 수집 · 오래된 " + i.ToString(CultureInfo.InvariantCulture)));
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(feed.Record(PlutoActivityKind.ApiPause, @"API 정지 key=AIzaSyLEAKLEAK0123456789abcd C:\Users\creator\Videos\clip|final.mp4"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(feed.Record(PlutoActivityKind.DriveConflict, "미해결 Drive 충돌 2건 — 설정 › ☁️ Drive 백업"));

        var svc = new PlutoSystemDiagnosticService(null, null, null, null, () => new DateTime(2026, 11, 23, 1, 2, 3, DateTimeKind.Utc)) { SeriesArc = arc, ActivityFeed = feed };
        var md = await svc.BuildSnapshotMarkdownAsync();

        Assert.Contains(PlutoSystemDiagnosticService.GlossaryHeading, md, StringComparison.Ordinal);
        Assert.Contains("- 활성 시리즈 용어집 1개 · 보호 용어 3개(전역 1개 포함)", md, StringComparison.Ordinal);
        Assert.Contains("| 🌐 전역 | 1 |", md, StringComparison.Ordinal);
        Assert.True(md.IndexOf(PlutoSystemDiagnosticService.GlossaryHeading, StringComparison.Ordinal) < md.IndexOf(PlutoSystemDiagnosticService.ActivityHeading, StringComparison.Ordinal));

        var act = md[md.IndexOf(PlutoSystemDiagnosticService.ActivityHeading, StringComparison.Ordinal)..];
        Assert.Equal(PlutoSystemDiagnosticService.MaxActivityRows, CountOf(act, "\n| 2026-"));   // 최근 10건만
        Assert.True(act.IndexOf("DriveConflict", StringComparison.Ordinal) < act.IndexOf("ApiPause", StringComparison.Ordinal));   // 최신 순
        Assert.Contains("오래된 11", act, StringComparison.Ordinal);
        Assert.DoesNotContain("오래된 1 ", act, StringComparison.Ordinal);
        Assert.DoesNotContain("오래된 0", act, StringComparison.Ordinal);
        Assert.DoesNotContain("AIzaSyLEAK", md, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creator", md, StringComparison.Ordinal);
        Assert.Contains(@"clip\|final.mp4", act, StringComparison.Ordinal);   // 표 셀 '|' 이스케이프
        Assert.Contains("| 2026-11-20 09:14 | ⚔️ DriveConflict |", act, StringComparison.Ordinal);

        // 순수 렌더러 — 범위 이름의 '|' · 개행 · 경로 · 토큰 이스케이프/가림 · 미연결 섹션
        var crafted = PlutoSystemDiagnosticService.BuildMarkdown(new PlutoDiagnosticInputs(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc))
        {
            Glossary = new PlutoGlossarySummary(1, 0, 4, new[] { new PlutoGlossaryScopeCount("a|b\nc ya29.SECRETTOKEN D:\\Vault\\시리즈.json", 4) }),
        });
        Assert.Contains(@"| a\|b c ya29.*** ‹path›/시리즈.json | 4 |", crafted, StringComparison.Ordinal);
        Assert.Contains(PlutoSystemDiagnosticService.ActivityHeading + "\n\n- 미연결\n", crafted, StringComparison.Ordinal);

        // 💾 .md 파일 내보내기도 같은 블랙박스 섹션 포함
        var export = await svc.ExportSnapshotMarkdownFileAsync(Sub("diag_out"));
        Assert.True(export.Ok, export.MessageKorean);
        var text = File.ReadAllText(export.FullPath!, Encoding.UTF8);
        Assert.Contains(PlutoSystemDiagnosticService.ActivityHeading, text, StringComparison.Ordinal);
        Assert.Contains("보호 용어 3개", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AIzaSyLEAK", text, StringComparison.Ordinal);
    }

    // ── ④ 자체 추가 #1 — 호스트 초기화(MainForm) · 데몬 동시 조정 단일 비행(이중 쓰기 반환 · 이중 재적재 · 이중 기록 없음) ──

    [Fact]
    public async Task StartupSync_IsSingleFlight_HostInitAndDaemonJoinTheSameRun_NoDoubleWriteBackOrReload()
    {
        var (storage, primary, local) = Drive("single");
        File.WriteAllText(Path.Combine(local, PlutoMistakeNotebookService.FileName), "{\"Rules\":[]}");
        var hub = new CountingHub(PlutoMistakeNotebookService.FileName);
        var hubResolves = 0;
        using var sync = new PlutoStartupDriveSyncService(storage, null, null, () => { Interlocked.Increment(ref hubResolves); return new IPlutoHotReloadable[] { hub }; }, CleanEnv);

        var runs = new Task<PlutoStartupDriveSyncReport>[8];
        for (var i = 0; i < runs.Length; i++) runs[i] = sync.RunOnceAsync();   // MainForm 즉시 호출 + 데몬 + Workspace 진입이 겹침
        var reports = await Task.WhenAll(runs).WaitAsync(Wait);
        foreach (var r in reports) Assert.Same(reports[0], r);
        Assert.Equal(1, sync.RunCount);
        Assert.Equal(1, reports[0].PushedToDrive);
        Assert.Equal(1, hub.Reloads);
        Assert.Equal(1, hubResolves);
        Assert.True(File.Exists(Path.Combine(primary, PlutoMistakeNotebookService.FileName)));

        // 데몬이 나중에 합류 — 같은 결과 · 피드 1건
        using var feed = Feed("single_feed");
        using var boot = new PlutoBackgroundAutonomyBootstrapper(null, null, null, feed, CleanEnv) { BootDelay = TimeSpan.Zero, StartupSync = sync };
        var rep = await boot.StartAsync().WaitAsync(Wait);
        Assert.Same(reports[0], rep.StartupSync);
        Assert.Equal(1, sync.RunCount);
        Assert.Equal(1, CountKind(feed, PlutoActivityKind.DriveSync));

        // 폐기 후 호출 → 무접촉 생략 결과(본체 재실행 없음)
        sync.Dispose();
        var after = await sync.RunOnceAsync().WaitAsync(Wait);
        Assert.False(after.Attempted);
        Assert.Equal("종료됨", after.DriveSkipReason);
        Assert.Equal(1, sync.RunCount);
    }

    // ── ⑤ 자체 추가 #2 — 멈춘 Drive 마운트가 감시 · 폴러 기동을 무기한 막지 않음(상한 후 진행 · 늦은 결과도 피드 기록) ──

    [Fact]
    public async Task HungDriveMount_DoesNotBlockWatcherAndPoller_AfterTimeout_AndLateOutcomeIsStillRecorded()
    {
        var clock = new ManualClock(T0);
        var fake = new FakeYouTube();
        using var api = await ConfiguredApi(fake, clock, "hung_api");
        await using var watcher = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), WatchOpt("hung"));
        using var feed = Feed("hung_feed", clock);
        var gated = new GatedSync();
        using var boot = new PlutoBackgroundAutonomyBootstrapper(watcher, api, null, feed, CleanEnv, clock)
        { BootDelay = TimeSpan.Zero, StartupSync = gated, StartupSyncTimeout = TimeSpan.FromSeconds(45) };

        var start = boot.StartAsync();
        await gated.Called.Task.WaitAsync(Wait);
        Assert.True(SpinWait.SpinUntil(() => clock.ActiveTimers >= 1, 5000), "조정 대기 상한 타이머 미무장");

        // 상한 직전 — 아직 대기(감시 · 폴러 미기동)
        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.False(start.IsCompleted);
        Assert.False(watcher.IsRunning);
        Assert.False(api.IsAutoSyncArmed);

        // 상한 도달 → 기다리지 않고 기동
        clock.Advance(TimeSpan.FromSeconds(1));
        var rep = await start.WaitAsync(Wait);
        Assert.True(rep.StartupSyncTimedOut);
        Assert.Null(rep.StartupSync);
        Assert.True(rep.WatcherRunning, rep.SummaryKorean);
        Assert.True(rep.ApiAutoSyncArmed, rep.SummaryKorean);
        Assert.Contains("지연", rep.SummaryKorean, StringComparison.Ordinal);
        Assert.Equal(0, feed.Count);

        // 조정이 늦게 끝남 → 그때 피드 기록(대기열 정리 2건)
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Changed += () => changed.TrySetResult();
        gated.Gate.SetResult(new PlutoStartupDriveSyncReport(true, null, 0, 3, 0, Array.Empty<string>(), 0, null, new PlutoUnmatchedReconcileResult(0, 2, 0, 5), T0));
        await changed.Task.WaitAsync(Wait);
        Assert.Equal(1, CountKind(feed, PlutoActivityKind.DriveSync));
        Assert.Contains("대기열 정리 2건", feed.Latest!.MessageKorean, StringComparison.Ordinal);
        Assert.Equal(1, gated.RunCount);

        // 폐기 후 늦게 끝난 조정은 기록하지 않음
        var clock2 = new ManualClock(T0);
        var gated2 = new GatedSync();
        using var feed2 = Feed("hung_feed2", clock2);
        var boot2 = new PlutoBackgroundAutonomyBootstrapper(null, null, null, feed2, CleanEnv, clock2) { BootDelay = TimeSpan.Zero, StartupSync = gated2, StartupSyncTimeout = TimeSpan.FromSeconds(5) };
        var start2 = boot2.StartAsync();
        await gated2.Called.Task.WaitAsync(Wait);
        Assert.True(SpinWait.SpinUntil(() => clock2.ActiveTimers >= 1, 5000));
        clock2.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await start2.WaitAsync(Wait)).StartupSyncTimedOut);
        boot2.Dispose();
        gated2.Gate.SetResult(new PlutoStartupDriveSyncReport(true, null, 4, 0, 0, Array.Empty<string>(), 0, null, null, T0));
        await Task.Delay(100);   // '발생하지 않아야 함' 검증만 짧은 고정 지연
        Assert.Equal(0, feed2.Count);
    }

    // ── ⑥ 자체 추가 #3 — 일상 미러 갱신은 피드 소음이 아님 · 충돌 지문은 순서 무관 · 경로 무관 · 집합 변화에 민감 ──

    [Fact]
    public void FeedNoise_MirrorRefreshOnlyStartupIsSilent_AndConflictFingerprintIsOrderAndPathIndependent()
    {
        using var feed = Feed("noise_feed");
        // 이 PC 가 지난 세션 Drive 에 쓴 허브 9종 + 동반 2종을 미러로 내려받기만 한 기동 → 0건
        var mirrorOnly = new PlutoStartupDriveSyncReport(true, null, 0, PlutoDriveHealthService.SyncFiles.Length, 0, Array.Empty<string>(), 0, null, new PlutoUnmatchedReconcileResult(0, 0, 0, 7), T0);
        Assert.False(mirrorOnly.HasMeaningfulChange);
        for (var i = 0; i < 5; i++) Assert.Equal(0, feed.RecordStartupDriveSync(mirrorOnly));
        Assert.Equal(0, feed.RecordStartupDriveSync(PlutoStartupDriveSyncReport.Skipped("Drive 미연결", T0)));
        Assert.Equal(0, feed.RecordStartupDriveSync(null));
        Assert.Equal(0, feed.Count);

        // 같은 의미 변화는 기동마다 실제 사건 → 기록(미러 수는 부가 정보로만)
        var pushed = mirrorOnly with { PushedToDrive = 2 };
        Assert.Equal(1, feed.RecordStartupDriveSync(pushed));
        Assert.Contains("☁️⬇️ 미러 11건", feed.Latest!.MessageKorean, StringComparison.Ordinal);

        var a = Conflict(PlutoMistakeNotebookService.FileName, 1, 300);
        var b = Conflict(PlutoRenderCatalogService.FileName, 2, 900);
        var c = Conflict(PlutoSeriesArcMemoryService.FileName, 3, 50);
        var fp = PlutoStartupDriveSyncService.ConflictFingerprintOf(new[] { a, b });
        Assert.NotNull(fp);
        Assert.Equal(16, fp!.Length);
        Assert.Equal(fp, PlutoStartupDriveSyncService.ConflictFingerprintOf(new[] { b, a }));                                     // 순서 무관
        Assert.Equal(fp, PlutoStartupDriveSyncService.ConflictFingerprintOf(new[] { a with { FullPath = @"\\nas\other\pc\x.json" }, b }));   // 경로 무관(PC 마다 다른 사용자 폴더)
        Assert.NotEqual(fp, PlutoStartupDriveSyncService.ConflictFingerprintOf(new[] { a, b, c }));                                // 새 충돌
        Assert.NotEqual(fp, PlutoStartupDriveSyncService.ConflictFingerprintOf(new[] { a }));                                      // 일부 해결
        Assert.Null(PlutoStartupDriveSyncService.ConflictFingerprintOf(Array.Empty<PlutoConflictCopy>()));
        Assert.Null(PlutoStartupDriveSyncService.ConflictFingerprintOf(null));

        // 지문 없는 충돌 수(원천 미연결)는 알림하지 않음 · 같은 지문 → 1회
        Assert.Equal(0, feed.RecordStartupDriveSync(mirrorOnly with { UnresolvedConflicts = 2 }));
        var withConflicts = mirrorOnly with { UnresolvedConflicts = 2, ConflictFingerprint = fp };
        Assert.Equal(1, feed.RecordStartupDriveSync(withConflicts));
        Assert.Equal(0, feed.RecordStartupDriveSync(withConflicts));
        Assert.Equal(1, CountKind(feed, PlutoActivityKind.DriveConflict));

        // 불변식 — 핵심 허브 9 · 병합 가능 허브 6 · 동반 동기화 2
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(9, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoDriveHealthService.CompanionSyncFiles.Length);
    }

    // ── ⑦ 자체 추가 #4 — 진단서 '수집 실패' 예외 메시지의 비밀값 가림(경로 가림만으로는 토큰 유출) ──

    private sealed class ThrowingArc : IPlutoSeriesArcMemoryService
    {
        public Task EnsureLoadedAsync(CancellationToken ct = default)
            => throw new InvalidOperationException(@"refresh failed access_token=ya29.a0SECRETSECRET Bearer abc.DEF123 at C:\Users\creator\AppData\Local\vault.json client GOCSPX-leakleak");
        public Task<int> SyncFromCatalogAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> RecordEpisodePerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyList<PlutoSeriesArcReport> GetSeriesArcs(int maxCount = 8) => throw new NotSupportedException();
        public PlutoSeriesContinuityDirective GetContinuityDirective(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber) => throw new NotSupportedException();
        public Task<PlutoSeriesContinuityDirective> GetContinuityDirectiveAsync(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber, CancellationToken ct = default) => throw new NotSupportedException();
        public PlutoSeriesArcStatus GetStatus() => throw new NotSupportedException();
        public event Action? Changed { add { } remove { } }
    }

    private sealed class ThrowingFeed : IPlutoAutonomousActivityFeedService
    {
        public int Count => 0;
        public PlutoActivityEntry? Latest => null;
        public event Action? Changed { add { } remove { } }
        public IReadOnlyList<PlutoActivityEntry> GetRecent(int max = PlutoAutonomousActivityFeedService.Capacity) => throw new IOException("feed locked key=AIzaSyFEEDLEAK0123456789xyz");
        public bool Record(PlutoActivityKind kind, string? message, string? dedupeKey = null) => false;
        public bool RecordApiSync(PlutoYouTubeApiSyncResult? result, bool background) => false;
        public bool RecordDownloadsIngest(PlutoDownloadsAutoIngestEvent? evt) => false;
        public int RecordBriefSweep(PlutoBriefSweepResult? sweep) => 0;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>가림을 건너뛴 원문을 그대로 돌려주는 피드(타 구현 · 구버전 방어 검증).</summary>
    private sealed class RawFeed : IPlutoAutonomousActivityFeedService
    {
        public int Count => 1;
        public PlutoActivityEntry? Latest => null;
        public event Action? Changed { add { } remove { } }
        public IReadOnlyList<PlutoActivityEntry> GetRecent(int max = PlutoAutonomousActivityFeedService.Capacity)
            => new[] { new PlutoActivityEntry(1, T0, PlutoActivityKind.ApiSync, "sync ok\n| injected | row | Bearer zzTOKENzz /Users/creator/secret.json") };
        public bool Record(PlutoActivityKind kind, string? message, string? dedupeKey = null) => false;
        public bool RecordApiSync(PlutoYouTubeApiSyncResult? result, bool background) => false;
        public bool RecordDownloadsIngest(PlutoDownloadsAutoIngestEvent? evt) => false;
        public int RecordBriefSweep(PlutoBriefSweepResult? sweep) => 0;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task DiagnosticSnapshot_RedactsSecretsInSectionFailureMessages_AndReRedactsUnsanitizedFeedEntries()
    {
        var failing = new PlutoSystemDiagnosticService(null, null, null, null) { SeriesArc = new ThrowingArc(), ActivityFeed = new ThrowingFeed() };
        var md = await failing.BuildSnapshotMarkdownAsync();
        Assert.Contains(PlutoSystemDiagnosticService.GlossaryHeading + "\n\n- ⚠️ 수집 실패: ", md, StringComparison.Ordinal);
        Assert.Contains(PlutoSystemDiagnosticService.ActivityHeading + "\n\n- ⚠️ 수집 실패: ", md, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETSECRET", md, StringComparison.Ordinal);
        Assert.DoesNotContain("DEF123", md, StringComparison.Ordinal);
        Assert.DoesNotContain("leakleak", md, StringComparison.Ordinal);
        Assert.DoesNotContain("FEEDLEAK", md, StringComparison.Ordinal);
        Assert.DoesNotContain("creator", md, StringComparison.Ordinal);
        Assert.Contains("access_token=***", md, StringComparison.Ordinal);
        Assert.Contains("‹path›/vault.json", md, StringComparison.Ordinal);
        Assert.Contains("key=***", md, StringComparison.Ordinal);

        // 기존 섹션 오류 경로도 같은 규칙(경로 가림은 유지)
        var legacy = PlutoSystemDiagnosticService.BuildMarkdown(new PlutoDiagnosticInputs(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc))
        {
            Errors = new Dictionary<string, string> { [PlutoSystemDiagnosticService.SectionDrive] = @"lock on G:\내 드라이브\DATA\Pluto\pluto_render_catalog.json sig=abcdef123" },
        });
        Assert.Contains("‹path›/pluto_render_catalog.json", legacy, StringComparison.Ordinal);
        Assert.Contains("sig=***", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123", legacy, StringComparison.Ordinal);

        // 가림되지 않은 피드 항목도 진단서에선 재가림 · 개행/파이프로 표를 깨지 못함
        var raw = await new PlutoSystemDiagnosticService(null, null, null, null) { ActivityFeed = new RawFeed() }.BuildSnapshotMarkdownAsync();
        var act = raw[raw.IndexOf(PlutoSystemDiagnosticService.ActivityHeading, StringComparison.Ordinal)..];
        Assert.DoesNotContain("zzTOKENzz", act, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/creator", act, StringComparison.Ordinal);
        Assert.Contains(@"sync ok \| injected \| row \| Bearer ***", act, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(act, "\n| 2026-"));
        foreach (var line in act.Split('\n'))
            if (line.StartsWith("| 2026-", StringComparison.Ordinal)) Assert.Equal(4, CountOf(line.Replace(@"\|", "", StringComparison.Ordinal), "|"));
    }
}

/// <summary>
/// ☁️ Step 38 — 기동 조정 결선 가드(공용): 호스트(MainForm)는 단일 비행 서비스에 위임만 하고(인라인 이중 조정 없음),
/// 서비스는 기존 순서(기준선 → 쓰기 반환 → 미러 복원 · 핵심 허브 9종 → 동반 2종)를 그대로 보존한다.
/// </summary>
internal static class PlutoStartupSyncWiring
{
    public static void AssertHostDelegatesToSingleFlightStartupSync(string repoRoot)
    {
        var mainForm = File.ReadAllText(Path.Combine(repoRoot, "PromptFlowStudio", "MainForm.cs"));
        Assert.Equal(1, Count(mainForm, "IPlutoStartupDriveSyncService>()?.RunOnceAsync()"));
        Assert.Equal(0, Count(mainForm, "plutoStore.WriteBackNewerLocalToDrive("));   // 인라인 Task.Run 조정 제거 → 데몬과 경합하는 두 번째 경로 없음
        foreach (var host in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(repoRoot, host));
            Assert.Equal(1, Count(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoStartupDriveSyncService>("));
            Assert.Equal(1, Count(di, "StartupSync = sp.GetService(typeof(PromptFlowStudio.Services.Pluto.IPlutoStartupDriveSyncService))"));
        }

        var svc = File.ReadAllText(Path.Combine(repoRoot, "PromptFlowStudio", "Services", "Pluto", "PlutoStartupDriveSyncService.cs"));
        var order = new[]
        {
            "dm.BootstrapSyncManifest(PlutoDriveHealthService.PlutoFiles);",
            "pushed += dm.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles);",
            "mirrored += dm.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles);",
            "dm.BootstrapSyncManifest(PlutoDriveHealthService.CompanionSyncFiles);",
            "pushed += dm.WriteBackNewerLocalToDrive(PlutoDriveHealthService.CompanionSyncFiles);",
            "mirrored += dm.RestoreCoreHubsToLocal(PlutoDriveHealthService.CompanionSyncFiles);",
            "q.ReconcileAsync(null, token)",   // 📌 대기열 조정은 미러 복원 뒤
        };
        var at = -1;
        foreach (var step in order)
        {
            var i = svc.IndexOf(step, StringComparison.Ordinal);
            Assert.True(i > at, "순서 위반/누락: " + step);
            at = i;
        }
    }

    private static int Count(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
