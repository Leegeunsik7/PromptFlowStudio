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
/// ⏱️ Step 36 — YouTube Data API v3 쿼터 인지형 주기 자동 동기화: 수동 시계(<see cref="TimeProvider"/>)로 결정적 발화 · 단일 실행 합류 · 수동 성공 후 재예약 ·
/// quotaExceeded 영속 정지 · 예비 쿼터 · 지수 백오프 · 무네트워크 격리 · 자체 추가(재시작 폭주 · 인증 정지 · 연결 해제 취소 · 구독자 격리).
/// </summary>
[Collection("ProcessEnvironment")]   // PROMPTFLOW_DISABLE_YOUTUBE_API 변경
public sealed class PlutoYouTubeApiAutoPollerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ytauto_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);   // 태평양 11-20 01:00
    private static readonly string KoChannel = "UC" + new string('K', 22);
    private const string ApiKey = "AIzaSyTESTKEY-auto-0123456789ab";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = PlutoYouTubeApiSyncService.DefaultAutoSyncInterval;
    private static readonly TimeSpan Grace = PlutoYouTubeApiSyncService.AutoSyncStartupGrace;
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoYouTubeApiAutoPollerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Sub(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Vid(char prefix, int n) => prefix + n.ToString("D10", CultureInfo.InvariantCulture);

    private static PlutoRenderedClipRecord Clip(string id, string title)
        => new(id, title, "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "autumn" }, "", T0, false);

    private static byte[] StudioCsv(string ytId, string title, long views, double avgPct, double ctr, double stayed)
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + ytId + "," + title + ",\"Nov 21, 2026\"," + views.ToString(CultureInfo.InvariantCulture) + ","
            + avgPct.ToString(CultureInfo.InvariantCulture) + ",5000," + ctr.ToString(CultureInfo.InvariantCulture) + "," + stayed.ToString(CultureInfo.InvariantCulture) + ",10\n");

    // ── 수동 시계 — Advance 가 예정 시각에 도달한 단발 타이머만 순서대로 발화 ──

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now;
        public int CreatedTimers;

        public ManualClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state);
            lock (_gate) { _timers.Add(t); CreatedTimers++; }
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

        public void AdvanceTo(DateTimeOffset target) => Advance(target - GetUtcNow());

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
                    next.Due = null;   // 단발
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

    // ── 가짜 YouTube Data API v3 ──

    private sealed record FakeVideo(string Id, string Title, long Views);

    private sealed class FakeYouTube : HttpMessageHandler
    {
        private readonly object _lock = new();
        public readonly List<FakeVideo> Uploads = new();
        private readonly List<string> _urls = new();
        public readonly List<(string PathPrefix, HttpStatusCode Code, string Body)> Overrides = new();
        /// <summary>설정 시 videos.list 가 이 게이트를 기다림(수동 실행을 게이트 안에 붙잡기).</summary>
        public TaskCompletionSource? VideosGate;
        public TaskCompletionSource VideosEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get { lock (_lock) return _urls.Count; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_lock) _urls.Add(url);
            if (!url.StartsWith(PlutoYouTubeApiSyncService.ApiBase, StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK);
            var path = url[PlutoYouTubeApiSyncService.ApiBase.Length..];
            lock (_lock)
                for (var i = 0; i < Overrides.Count; i++)
                    if (path.StartsWith(Overrides[i].PathPrefix, StringComparison.Ordinal))
                    {
                        var o = Overrides[i];
                        Overrides.RemoveAt(i);
                        return Json(o.Body, o.Code);
                    }
            if (path.StartsWith("channels", StringComparison.Ordinal))
                return Json("{\"items\":[{\"id\":\"" + KoChannel + "\",\"contentDetails\":{\"relatedPlaylists\":{\"uploads\":\"UU" + KoChannel[2..] + "\"}}}]}");
            List<FakeVideo> snapshot;
            lock (_lock) snapshot = new List<FakeVideo>(Uploads);
            if (path.StartsWith("playlistItems", StringComparison.Ordinal))
            {
                var sb = new StringBuilder("{\"items\":[");
                for (var i = 0; i < snapshot.Count; i++)
                    sb.Append(i > 0 ? "," : "").Append("{\"contentDetails\":{\"videoId\":\"").Append(snapshot[i].Id).Append("\"}}");
                return Json(sb.Append("]}").ToString());
            }
            if (path.StartsWith("videos", StringComparison.Ordinal))
            {
                VideosEntered.TrySetResult();
                if (VideosGate is { } g) await g.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                var sb = new StringBuilder("{\"items\":[");
                for (var i = 0; i < snapshot.Count; i++)
                {
                    var v = snapshot[i];
                    sb.Append(i > 0 ? "," : "").Append("{\"id\":\"").Append(v.Id).Append("\",\"snippet\":{\"title\":").Append(System.Text.Json.JsonSerializer.Serialize(v.Title))
                      .Append(",\"publishedAt\":\"2026-11-21T03:00:00Z\",\"channelId\":\"").Append(KoChannel).Append("\"},\"statistics\":{\"viewCount\":\"")
                      .Append(v.Views.ToString(CultureInfo.InvariantCulture)).Append("\",\"likeCount\":\"3\",\"commentCount\":\"1\"}}");
                }
                return Json(sb.Append("]}").ToString());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        public void Fail(string prefix, HttpStatusCode code, string reason, int times = 1)
        {
            lock (_lock)
                for (var i = 0; i < times; i++)
                    Overrides.Add((prefix, code, "{\"error\":{\"code\":" + ((int)code).ToString(CultureInfo.InvariantCulture) + ",\"errors\":[{\"reason\":\"" + reason + "\"}]}}"));
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private PlutoYouTubeApiSyncService Service(FakeYouTube? handler, ManualClock clock, string store, IPlutoTelemetryBatchImporter? importer = null,
        int budget = PlutoYouTubeApiSyncService.DefaultDailyUnitBudget, int reserve = PlutoYouTubeApiSyncService.DefaultAutoSyncReserveUnits)
        => new(importer, handler, Sub(store))
        {
            TimeProvider = clock,
            Delay = static (_, _) => Task.CompletedTask,   // 요청 내 재시도 대기는 즉시
            DailyUnitBudget = budget,
            AutoSyncReserveUnits = reserve,
        };

    private static async Task<PlutoYouTubeApiSyncService> Configured(PlutoYouTubeApiSyncService svc)
    {
        var save = await svc.SaveCredentialsAsync(new PlutoYouTubeApiCredentials { ApiKey = ApiKey, Channels = new[] { new PlutoYouTubeApiChannel(KoChannel, "ko-KR") } });
        Assert.True(save.Ok, save.MessageKorean);
        return svc;
    }

    /// <summary>다음 자동 실행 완료 신호(상한 있는 대기).</summary>
    private static Task<PlutoYouTubeApiAutoSyncEvent> NextTick(PlutoYouTubeApiSyncService svc)
    {
        var tcs = new TaskCompletionSource<PlutoYouTubeApiAutoSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PlutoYouTubeApiAutoSyncEvent>? h = null;
        h = e => { svc.AutoSyncCompleted -= h; tcs.TrySetResult(e); };
        svc.AutoSyncCompleted += h;
        return tcs.Task.WaitAsync(Wait);
    }

    private static async Task<PlutoYouTubeApiAutoSyncEvent> AdvanceAndTick(PlutoYouTubeApiSyncService svc, ManualClock clock, TimeSpan by)
    {
        var tick = NextTick(svc);
        clock.Advance(by);
        return await tick;
    }

    // ── 1. 주기 발화 → 새 클립 연결 · A/B 제목 귀속 · 대기열 정밀 지표 회수(전부 자동) ──

    [Fact]
    public async Task BackgroundPoller_FiresPeriodically_LinksClips_AttributesAbTitle_AndRecoversStrandedStudioRows()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "periodic_catalog.json"));
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "periodic_nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, null);
        using var queue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "periodic_queue.json"), catalog, loop);
        var importer = new PlutoTelemetryBatchImporter(catalog, loop, queue);
        await catalog.RecordRenderedClipAsync(Clip("stranded", "별이 쏟아지는 캠핑 밤"));
        await catalog.RecordRenderedClipAsync(Clip("ab_master", "가을 숲 산책 1/3편"));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await catalog.TryBindYouTubeVideoIdAsync("ab_master", Vid('k', 0)));
        await catalog.RecordPublishedTitleAsync("ab_master", new PlutoPublishedTitleSelection("A", PlutoTitleFramings.Unknown, "가을 숲 산책 1/3편", PlutoTitleFramings.Unknown, "끝까지 보면 보이는 가을 숲의 비밀 1/3편"));
        // Studio 내보내기 — 게시 직전 제목 변경으로 자동 연결 실패 → 정밀 지표가 대기열에 묶임
        var studio = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('d', 1), "zzz 전혀 다른 표제", 700, 52, 6.6, 64), "Table data.csv");
        Assert.Equal(1, studio.QueuedUnmatchedCount);

        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('d', 1), "별이 쏟아지는 캠핑 밤", 760));
        fake.Uploads.Add(new(Vid('k', 0), "끝까지 보면 보이는 가을 숲의 비밀 1/3편", 5000));
        var clock = new ManualClock(T0);
        using var svc = await Configured(Service(fake, clock, "periodic", importer));

        Assert.True(svc.StartAutoSync());
        Assert.True(svc.StartAutoSync());   // 멱등
        Assert.Equal(1, clock.ActiveTimers);
        Assert.Equal(T0 + Grace, svc.GetAutoSyncStatus().NextDueUtc);
        Assert.Equal(0, fake.RequestCount);   // 기동 즉시 요청하지 않음

        var e1 = await AdvanceAndTick(svc, clock, Grace);
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, e1.Result.State);
        Assert.True(e1.HasImpact);
        Assert.Equal(1, e1.QueueRecoveredCount);
        Assert.Contains("대기열 정밀 지표 회수", e1.ToastKorean, StringComparison.Ordinal);
        Assert.Equal(0, queue.Count);
        var stranded = await catalog.FindByYouTubeVideoIdAsync(Vid('d', 1));
        Assert.Equal("stranded", stranded!.VideoId);
        Assert.Equal(64, stranded.YouTubeWatermark!.HookPct, 3);
        var master = await catalog.FindByYouTubeVideoIdAsync(Vid('k', 0));
        Assert.Equal("B", master!.SelectedTitleVariant);   // 실제 게시 제목 = B → 교정 귀속
        Assert.Equal(PlutoTitleAttributionStates.Confirmed, master.TitleAttribution);
        Assert.Equal(5000, master.YouTubeApiStats!.Views);
        var st = svc.GetAutoSyncStatus();
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Scheduled, st.Phase);
        Assert.Equal(T0 + Grace + Interval, st.NextDueUtc);

        // 30분 뒤 — 새로 렌더 · 게시된 클립을 다음 주기가 스스로 연결
        await catalog.RecordRenderedClipAsync(Clip("fresh", "첫눈 오는 골목 산책"));
        fake.Uploads.Add(new(Vid('f', 1), "첫눈 오는 골목 산책", 1200));
        var before = fake.RequestCount;
        clock.Advance(Interval - TimeSpan.FromMinutes(1));
        Assert.Equal(before, fake.RequestCount);   // 예정 전 → 0
        var e2 = await AdvanceAndTick(svc, clock, TimeSpan.FromMinutes(1));
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, e2.Result.State);
        Assert.True(e2.NewlyLinkedCount >= 1);
        Assert.Equal("fresh", (await catalog.FindByYouTubeVideoIdAsync(Vid('f', 1)))!.VideoId);
        Assert.Equal(2, svc.AutoSyncTickCount);
        Assert.Equal(1, clock.ActiveTimers);
    }

    // ── 2. 단일 실행 합류 + 수동 성공 후 타이머 재설정 ──

    [Fact]
    public async Task ManualAndScheduledTicks_NeverOverlap_AndManualSuccessResetsNextDue()
    {
        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('m', 1), "수동 자동 경합 영상", 300));
        var clock = new ManualClock(T0);
        using var svc = await Configured(Service(fake, clock, "flight"));
        Assert.True(svc.StartAutoSync());
        Assert.Equal(T0 + Grace, svc.GetAutoSyncStatus().NextDueUtc);

        // ① 예정 전에 수동 성공 → 다음 자동은 지금부터 한 주기 뒤(같은 지표를 2분 뒤 또 받지 않음)
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, (await svc.SyncNowAsync()).State);
        Assert.Equal(T0 + Interval, svc.GetAutoSyncStatus().NextDueUtc);
        var afterManual = fake.RequestCount;
        clock.Advance(Grace);
        Assert.Equal(afterManual, fake.RequestCount);
        Assert.Equal(0, svc.AutoSyncTickCount);

        // ② 수동이 게이트를 잡은 채 대기 중 → 예정 발화는 Busy 로 합류(요청 0 · 겹침 0)
        fake.VideosEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.VideosGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = svc.SyncNowAsync();
        await fake.VideosEntered.Task.WaitAsync(Wait);
        var held = fake.RequestCount;
        var busy = await AdvanceAndTick(svc, clock, Interval - Grace);   // T0+30m
        Assert.Equal(PlutoYouTubeApiSyncState.Busy, busy.Result.State);
        Assert.False(busy.HasImpact);
        Assert.Equal(held, fake.RequestCount);
        Assert.Equal(0, svc.AutoSyncTickCount);
        Assert.Equal(T0 + Interval + Interval, svc.GetAutoSyncStatus().NextDueUtc);

        // 수동 완료(T0+31m) → 다시 한 주기 뒤로 재설정
        clock.Advance(TimeSpan.FromMinutes(1));
        fake.VideosGate.TrySetResult();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, (await manual.WaitAsync(Wait)).State);
        Assert.Equal(T0 + TimeSpan.FromMinutes(31) + Interval, svc.GetAutoSyncStatus().NextDueUtc);
        Assert.True(svc.IsAutoSyncArmed);
        clock.Advance(TimeSpan.FromMinutes(29));   // 예정(T0+61m) 직전
        Assert.Equal(0, svc.AutoSyncTickCount);
        var ok = await AdvanceAndTick(svc, clock, TimeSpan.FromMinutes(1));
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, ok.Result.State);
        Assert.Equal(1, svc.AutoSyncTickCount);
    }

    // ── 3-a. quotaExceeded → 태평양 자정까지 정지(재시작 후에도) ──

    [Fact]
    public async Task QuotaExceeded_PausesUntilPacificMidnight_AndSurvivesServiceRestart()
    {
        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('q', 1), "쿼터 영상", 100));
        fake.Fail("videos", HttpStatusCode.Forbidden, "quotaExceeded");
        var clock = new ManualClock(T0);
        var resume = PlutoYouTubeApiSyncService.NextQuotaResetUtc(T0 + Grace) + PlutoYouTubeApiSyncService.AutoResumeSlack;
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 8, 1, 0, TimeSpan.Zero), resume);
        using (var svc = await Configured(Service(fake, clock, "quota")))
        {
            Assert.True(svc.StartAutoSync());
            var e = await AdvanceAndTick(svc, clock, Grace);
            Assert.Equal(PlutoYouTubeApiSyncState.QuotaCooldown, e.Result.State);
            var st = svc.GetAutoSyncStatus();
            Assert.Equal(PlutoYouTubeApiAutoSyncPhase.QuotaPaused, st.Phase);
            Assert.Equal(resume, st.NextDueUtc);
            Assert.Contains("쿼터 소진", st.StatusKorean, StringComparison.Ordinal);
        }

        // 🔁 재시작 — 영속 냉각을 존중(기동 유예 2분 뒤가 아니라 자정 뒤)
        var requests = fake.RequestCount;
        using var svc2 = Service(fake, clock, "quota");
        Assert.True(svc2.StartAutoSync());
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.QuotaPaused, svc2.GetAutoSyncStatus().Phase);
        Assert.Equal(resume, svc2.GetAutoSyncStatus().NextDueUtc);
        clock.AdvanceTo(resume - TimeSpan.FromMinutes(2));
        Assert.Equal(requests, fake.RequestCount);   // 냉각 중 요청 0
        var after = NextTick(svc2);
        clock.AdvanceTo(resume);
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, (await after).Result.State);
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Scheduled, svc2.GetAutoSyncStatus().Phase);
    }

    // ── 3-b. 예비 쿼터 미만 → 자동만 정지(수동 가능) · 429/5xx → 실행 간 지수 백오프 ──

    [Fact]
    public async Task ReserveBuffer_PausesOnlyBackgroundPolling_AndTransientErrorsBackOffExponentially()
    {
        // 💸 예산 102 · 예비 100 — 첫 실행(3 unit) 뒤 남은 99 < 100 → 자동은 태평양 자정까지 정지
        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('r', 1), "예비 쿼터 영상", 100));
        var clock = new ManualClock(T0);
        using (var svc = await Configured(Service(fake, clock, "reserve", budget: 102, reserve: 100)))
        {
            Assert.True(svc.StartAutoSync());
            var e = await AdvanceAndTick(svc, clock, Grace);
            Assert.Equal(PlutoYouTubeApiSyncState.Synced, e.Result.State);
            Assert.Equal(3, e.Result.QuotaUnitsUsed);
            var st = svc.GetAutoSyncStatus();
            Assert.Equal(PlutoYouTubeApiAutoSyncPhase.ReservePaused, st.Phase);
            Assert.Equal(99, st.QuotaRemaining);
            var midnight = PlutoYouTubeApiSyncService.NextQuotaResetUtc(clock.GetUtcNow()) + PlutoYouTubeApiSyncService.AutoResumeSlack;
            Assert.Equal(midnight, st.NextDueUtc);
            Assert.Equal(PlutoYouTubeApiSyncState.Synced, (await svc.SyncNowAsync()).State);   // 수동은 예비분을 쓸 수 있음
            Assert.Equal(midnight, svc.GetAutoSyncStatus().NextDueUtc);   // 수동 성공 재설정도 예비 정지를 넘지 않음
            var req = fake.RequestCount;
            clock.Advance(TimeSpan.FromHours(6));
            Assert.Equal(req, fake.RequestCount);
            Assert.Equal(1, svc.AutoSyncTickCount);
        }

        // ⏳ 503 ×3(요청 내 재시도 소진) → 5분 · 429 ×3 → 10분 · 성공 → 30분 주기 복귀
        Assert.Equal(TimeSpan.FromMinutes(5), PlutoYouTubeApiSyncService.AutoBackoffDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(10), PlutoYouTubeApiSyncService.AutoBackoffDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(160), PlutoYouTubeApiSyncService.AutoBackoffDelay(6));
        Assert.Equal(PlutoYouTubeApiSyncService.MaxAutoBackoff, PlutoYouTubeApiSyncService.AutoBackoffDelay(8));
        Assert.Equal(PlutoYouTubeApiSyncService.MaxAutoBackoff, PlutoYouTubeApiSyncService.AutoBackoffDelay(int.MaxValue));
        var flaky = new FakeYouTube();
        flaky.Uploads.Add(new(Vid('b', 1), "백오프 영상", 100));
        flaky.Fail("channels", HttpStatusCode.ServiceUnavailable, "backendError", PlutoYouTubeApiSyncService.MaxAttempts);
        var c2 = new ManualClock(T0);
        using var svc2 = await Configured(Service(flaky, c2, "backoff"));
        Assert.True(svc2.StartAutoSync());
        var f1 = await AdvanceAndTick(svc2, c2, Grace);
        Assert.Equal(PlutoYouTubeApiSyncState.Failed, f1.Result.State);
        var s1 = svc2.GetAutoSyncStatus();
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Backoff, s1.Phase);
        Assert.Equal(1, s1.ConsecutiveFailures);
        Assert.Equal(T0 + Grace + TimeSpan.FromMinutes(5), s1.NextDueUtc);

        flaky.Fail("channels", (HttpStatusCode)429, "rateLimitExceeded", PlutoYouTubeApiSyncService.MaxAttempts);
        var f2 = await AdvanceAndTick(svc2, c2, TimeSpan.FromMinutes(5));
        Assert.Equal(PlutoYouTubeApiSyncState.Failed, f2.Result.State);
        Assert.Equal(2, svc2.GetAutoSyncStatus().ConsecutiveFailures);
        Assert.Equal(c2.GetUtcNow() + TimeSpan.FromMinutes(10), svc2.GetAutoSyncStatus().NextDueUtc);

        var ok = await AdvanceAndTick(svc2, c2, TimeSpan.FromMinutes(10));
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, ok.Result.State);
        var s3 = svc2.GetAutoSyncStatus();
        Assert.Equal(0, s3.ConsecutiveFailures);
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Scheduled, s3.Phase);
        Assert.Equal(c2.GetUtcNow() + Interval, s3.NextDueUtc);
    }

    // ── 4. 미설정 · PROMPTFLOW_PLUTO_DRIVE=off(실 네트워크) · 킬 스위치 → 타이머 0 · 요청 0 ──

    [Fact]
    public async Task Unconfigured_IsolatedOrKillSwitched_StartsNoTimer_AndMakesZeroNetworkCalls()
    {
        var clock = new ManualClock(T0);
        var idle = new FakeYouTube();
        using (var unset = Service(idle, clock, "unset"))
        {
            Assert.False(unset.StartAutoSync());
            await unset.SetAutoSyncEnabledAsync(true);
            Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Unavailable, unset.GetAutoSyncStatus().Phase);
            Assert.Equal(PlutoYouTubeApiSyncState.NotConfigured, (await unset.RunAutoSyncTickAsync())!.State);   // 직접 호출돼도 사전 점검에서 차단
            clock.Advance(TimeSpan.FromHours(3));
            Assert.Equal(0, clock.CreatedTimers);
            Assert.Equal(0, unset.NetworkAttemptCount);
            Assert.Equal(0, idle.RequestCount);
        }

        // 자격 증명이 저장된 폴더 + 실 네트워크(주입 전송 없음) + 하네스 격리 → 타이머 자체를 만들지 않음
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        using (var seed = await Configured(Service(new FakeYouTube(), clock, "isolated"))) { }
        using (var real = new PlutoYouTubeApiSyncService(null, null, Sub("isolated")) { TimeProvider = clock })
        {
            Assert.True(real.IsConfigured);
            Assert.False(real.StartAutoSync());
            await real.SetAutoSyncEnabledAsync(true);
            Assert.False(real.IsAutoSyncArmed);
            Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Unavailable, real.GetAutoSyncStatus().Phase);
            clock.Advance(TimeSpan.FromHours(3));
            Assert.Equal(0, clock.CreatedTimers);
            Assert.Equal(0, real.NetworkAttemptCount);
        }

        // 킬 스위치 — 기동 뒤 켜져도 다음 발화가 요청 0 으로 타이머 해제 · 이후 기동 거부
        var killed = new FakeYouTube();
        using var svc = await Configured(Service(killed, clock, "kill"));
        Assert.True(svc.StartAutoSync());
        Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, "1");
        try
        {
            var e = await AdvanceAndTick(svc, clock, Grace);
            Assert.Equal(PlutoYouTubeApiSyncState.Disabled, e.Result.State);
            Assert.False(svc.IsAutoSyncArmed);
            Assert.Equal(0, clock.ActiveTimers);
            Assert.False(svc.StartAutoSync());
            clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(0, killed.RequestCount);
            Assert.Equal(0, svc.NetworkAttemptCount);
        }
        finally { Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, null); }
    }

    // ── 5. 🛡️ 자체 추가 A — 재시작 폭주 차단(영속 예정 · 기동 유예) · 조기 발화 재무장 · 간격 하한 ──

    [Fact]
    public async Task SelfAddition_RestartStormGuard_RespectsPersistedSchedule_AndEarlyFireRearmsWithoutNetwork()
    {
        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('s', 1), "재시작 영상", 100));
        var clock = new ManualClock(T0);
        using (var first = await Configured(Service(fake, clock, "storm")))
            Assert.Equal(PlutoYouTubeApiSyncState.Synced, (await first.SyncNowAsync()).State);   // 수동 성공 → 예정 T0+30m 영속(타이머 미기동이어도)
        var baseline = fake.RequestCount;

        // 개발 중 잦은 재시작 5회 — 매번 기동해도 요청 0, 예정은 그대로
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            using var again = Service(fake, clock, "storm");
            Assert.True(again.StartAutoSync());
            Assert.Equal(T0 + Interval, again.GetAutoSyncStatus().NextDueUtc);
        }
        Assert.Equal(baseline, fake.RequestCount);

        // 조기 발화(시계 변경 · 재예약 경합) → 실행하지 않고 남은 시간으로 재무장
        using var svc = Service(fake, clock, "storm");
        Assert.True(svc.StartAutoSync());
        Assert.Null(await svc.RunAutoSyncTickAsync());
        Assert.Equal(baseline, fake.RequestCount);
        Assert.True(svc.IsAutoSyncArmed);
        Assert.Equal(T0 + Interval, svc.GetAutoSyncStatus().NextDueUtc);
        var e = await AdvanceAndTick(svc, clock, T0 + Interval - clock.GetUtcNow());
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, e.Result.State);

        // 새 폴더(이력 없음) → 즉시가 아니라 기동 유예 뒤 · 간격 하한(1초 → 5분)
        using var fresh = await Configured(new PlutoYouTubeApiSyncService(null, new FakeYouTube(), Sub("storm_fresh")) { TimeProvider = clock, AutoSyncInterval = TimeSpan.FromSeconds(1) });
        Assert.Equal(PlutoYouTubeApiSyncService.MinAutoSyncInterval, fresh.AutoSyncInterval);
        Assert.True(fresh.StartAutoSync());
        Assert.Equal(clock.GetUtcNow() + Grace, fresh.GetAutoSyncStatus().NextDueUtc);
    }

    // ── 6. 🛡️ 자체 추가 B — 인증 실패 정지(재시작 후에도) · 자격 증명 재저장 시 해제 · 재무장 ──

    [Fact]
    public async Task SelfAddition_AuthFailureLatchesPoller_UntilCredentialsAreResaved()
    {
        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('a', 1), "인증 영상", 100));
        fake.Fail("channels", HttpStatusCode.BadRequest, "keyInvalid");
        var clock = new ManualClock(T0);
        using (var svc = await Configured(Service(fake, clock, "auth")))
        {
            Assert.True(svc.StartAutoSync());
            var e = await AdvanceAndTick(svc, clock, Grace);
            Assert.Equal(PlutoYouTubeApiSyncState.AuthFailed, e.Result.State);
            Assert.False(svc.IsAutoSyncArmed);
            Assert.Equal(PlutoYouTubeApiAutoSyncPhase.AuthPaused, svc.GetAutoSyncStatus().Phase);
            var req = fake.RequestCount;
            clock.Advance(TimeSpan.FromHours(24));   // 무효 키로 주기적으로 두드리지 않음
            Assert.Equal(req, fake.RequestCount);
        }

        var before = fake.RequestCount;
        using var svc2 = Service(fake, clock, "auth");
        Assert.False(svc2.StartAutoSync());   // 영속 정지
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.AuthPaused, svc2.GetAutoSyncStatus().Phase);
        Assert.Equal(before, fake.RequestCount);

        // 사용자가 새 키 저장 → 정지 해제 · 기동 요청이 남아 있으므로 유예 뒤 자동 재무장
        await Configured(svc2);
        Assert.True(svc2.IsAutoSyncArmed);
        Assert.Equal(clock.GetUtcNow() + Grace, svc2.GetAutoSyncStatus().NextDueUtc);
        var ok = await AdvanceAndTick(svc2, clock, Grace);
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, ok.Result.State);
    }

    // ── 7. 🛡️ 자체 추가 C — 연결 해제 · 끄기가 진행 중 자동 실행을 취소하고 다시 깨어나지 않음 ──

    [Fact]
    public async Task SelfAddition_DisconnectCancelsInFlightTick_AndNoTimerSurvives()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "cancel_catalog.json"));
        await catalog.RecordRenderedClipAsync(Clip("cancel_clip", "해 질 녘 바닷가 산책"));
        var importer = new PlutoTelemetryBatchImporter(catalog, new PlutoClosedLoopReinforcementService(null, new PlutoMistakeNotebookService(Path.Combine(_dir, "cancel_nb.json")), null));
        var fake = new FakeYouTube { VideosGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        fake.Uploads.Add(new(Vid('c', 1), "해 질 녘 바닷가 산책", 400));
        var clock = new ManualClock(T0);
        using var svc = await Configured(Service(fake, clock, "cancel", importer));
        Assert.True(svc.StartAutoSync());
        clock.Advance(Grace);
        await fake.VideosEntered.Task.WaitAsync(Wait);
        Assert.True(svc.IsAutoSyncRunning);
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Running, svc.GetAutoSyncStatus().Phase);
        var manual = await svc.SyncNowAsync();   // 자동 실행 중 수동 → 합류 안내(겹침 0)
        Assert.Equal(PlutoYouTubeApiSyncState.Busy, manual.State);
        Assert.Contains("백그라운드", manual.SummaryKorean, StringComparison.Ordinal);

        await svc.DisconnectAsync();
        Assert.True(SpinWait.SpinUntil(() => !svc.IsAutoSyncRunning, 10_000));
        Assert.False(svc.IsAutoSyncArmed);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Null(await catalog.FindByYouTubeVideoIdAsync(Vid('c', 1)));   // 지워진 자격 증명으로 받은 결과를 반영하지 않음
        var req = fake.RequestCount;
        fake.VideosGate.TrySetResult();
        clock.Advance(TimeSpan.FromHours(4));
        Assert.Equal(req, fake.RequestCount);
        Assert.Equal(0, svc.AutoSyncTickCount);

        // 끄기 영속 → 재연결 · 재시작해도 꺼진 채(타이머 0)
        await Configured(svc);
        await svc.SetAutoSyncEnabledAsync(false);
        Assert.False(svc.IsAutoSyncArmed);
        using var restarted = Service(fake, clock, "cancel");
        Assert.False(restarted.AutoSyncEnabled);
        Assert.False(restarted.StartAutoSync());
        Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Off, restarted.GetAutoSyncStatus().Phase);
        Assert.Contains("꺼짐", restarted.GetAutoSyncStatus().StatusKorean, StringComparison.Ordinal);
    }

    // ── 8. 🛡️ 자체 추가 D — 끊긴 회로 구독자가 던져도 다른 구독자 · 다음 주기는 계속 ──

    [Fact]
    public async Task SelfAddition_ThrowingSubscriberIsIsolated_AndScheduleContinues()
    {
        var fake = new FakeYouTube();
        fake.Uploads.Add(new(Vid('i', 1), "구독자 격리 영상", 100));
        var clock = new ManualClock(T0);
        using var svc = await Configured(Service(fake, clock, "isolate"));
        svc.AutoSyncCompleted += static _ => throw new ObjectDisposedException("circuit");
        svc.Changed += static () => throw new InvalidOperationException("dispatcher gone");
        var changedAfterThrow = 0;
        svc.Changed += () => Interlocked.Increment(ref changedAfterThrow);
        Assert.True(svc.StartAutoSync());

        var e1 = await AdvanceAndTick(svc, clock, Grace);
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, e1.Result.State);
        Assert.True(Volatile.Read(ref changedAfterThrow) > 0);
        Assert.True(svc.IsAutoSyncArmed);
        var e2 = await AdvanceAndTick(svc, clock, Interval);
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, e2.Result.State);
        Assert.Equal(2, svc.AutoSyncTickCount);
        Assert.False(e2.HasImpact);   // 조회수 갱신만 → 토스트 없음
    }
}
