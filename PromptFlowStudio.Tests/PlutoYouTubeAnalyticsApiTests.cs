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
/// 📊 Step 45 — YouTube Analytics API v2(<c>reports.query</c>) 정밀 CTR · 유지율 커넥터: OAuth 정밀 학습 · A/B 귀속 · 기획 채점 · 유기적 복권 ·
/// API 키/403 관측 전용 강등 · 무네트워크 격리 · 자체 추가(보고 지연 가드 · 채널 단위 범위 래치 · Studio 우선 순서/3초 유지 승계 · 스키마 격리).
/// </summary>
[Collection("ProcessEnvironment")]   // 킬 스위치 환경 변수 변경
public sealed class PlutoYouTubeAnalyticsApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ytanalytics_" + Guid.NewGuid().ToString("N"));
    /// <summary>서비스 시계 — 태평양 11-24 04:00 → 조회 종료일 11-22.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private const string MaturePublished = "2026-11-15T03:00:00Z";
    private const string FreshPublished = "2026-11-23T20:00:00Z";
    private static readonly DateTime NbT0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string KoChannel = "UC" + new string('K', 22);
    private static readonly string JaChannel = "UC" + new string('J', 22);
    private const string ApiKey = "AIzaSyTESTKEY-0123456789abcdef";
    private const string ClientSecret = "GOCSPX-test-client-secret-42";
    private const string RefreshToken = "1//0refresh-token-abcdefghijkl";
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoYouTubeAnalyticsApiTests() => Directory.CreateDirectory(_dir);

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

    private static PlutoRenderedClipRecord Clip(string id, string title, string hook, string look, DateTimeOffset at)
        => new(id, title, "몽타주", hook, look, "Lullaby", new[] { "autumn" }, "", at, false);

    private static byte[] StudioCsv(string ytId, string title, long views, double avgPct, double ctr, double stayed)
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + ytId + "," + title + ",\"Nov 15, 2026\"," + views.ToString(CultureInfo.InvariantCulture) + ","
            + avgPct.ToString(CultureInfo.InvariantCulture) + ",5000," + ctr.ToString(CultureInfo.InvariantCulture) + "," + stayed.ToString(CultureInfo.InvariantCulture) + ",10\n");

    // ── 가짜 YouTube Data API v3 + Analytics API v2 ──

    private sealed record FakeVideo(string Id, string ChannelId, string Title, long Views, string PublishedAt = MaturePublished);

    private sealed record FakeMetrics(long Views, double AvgPct, double Ctr);

    private sealed class FakeGoogle : HttpMessageHandler
    {
        public readonly Dictionary<string, List<FakeVideo>> Uploads = new(StringComparer.Ordinal);
        public readonly Dictionary<string, FakeMetrics> Analytics = new(StringComparer.Ordinal);
        public readonly HashSet<string> ForbiddenChannels = new(StringComparer.Ordinal);
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Urls = new();
        public readonly List<string> AnalyticsUrls = new();
        public string? AnalyticsBodyOverride;
        public int TokenPosts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (url.StartsWith(PlutoYouTubeApiSyncService.TokenEndpoint, StringComparison.Ordinal))
            {
                TokenPosts++;
                return Task.FromResult(Json("{\"access_token\":\"ya29.analytics-token\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"));
            }
            var q = Query(url);
            if (url.StartsWith(PlutoYouTubeApiSyncService.AnalyticsReportsEndpoint, StringComparison.Ordinal))
            {
                AnalyticsUrls.Add(url);
                var ch = q["ids"]["channel==".Length..];
                if (ForbiddenChannels.Contains(ch))
                    return Task.FromResult(Json("{\"error\":{\"code\":403,\"message\":\"Insufficient permission\",\"errors\":[{\"reason\":\"insufficientPermissions\"}]}}", HttpStatusCode.Forbidden));
                if (AnalyticsBodyOverride is { } body) return Task.FromResult(Json(body));
                var sb = new StringBuilder("{\"kind\":\"youtubeAnalytics#resultTable\",\"columnHeaders\":[{\"name\":\"video\",\"columnType\":\"DIMENSION\",\"dataType\":\"STRING\"},"
                    + "{\"name\":\"views\",\"columnType\":\"METRIC\",\"dataType\":\"INTEGER\"},{\"name\":\"estimatedMinutesWatched\",\"columnType\":\"METRIC\",\"dataType\":\"INTEGER\"},"
                    + "{\"name\":\"averageViewPercentage\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"},{\"name\":\"impressionsClickThroughRate\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"}],\"rows\":[");
                var first = true;
                foreach (var id in q["filters"]["video==".Length..].Split(','))
                {
                    if (!Analytics.TryGetValue(id, out var m) || !Uploads.TryGetValue(ch, out var list) || !list.Exists(v => v.Id == id)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("[\"").Append(id).Append("\",").Append(m.Views.ToString(CultureInfo.InvariantCulture)).Append(",120,")
                      .Append(m.AvgPct.ToString(CultureInfo.InvariantCulture)).Append(',').Append(m.Ctr.ToString(CultureInfo.InvariantCulture)).Append(']');
                }
                return Task.FromResult(Json(sb.Append("]}").ToString()));
            }
            if (!url.StartsWith(PlutoYouTubeApiSyncService.ApiBase, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            var path = url[PlutoYouTubeApiSyncService.ApiBase.Length..];
            if (path.StartsWith("channels", StringComparison.Ordinal))
            {
                var sb = new StringBuilder("{\"items\":[");
                var first = true;
                foreach (var id in q["id"].Split(','))
                {
                    if (!Uploads.ContainsKey(id)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":\"").Append(id).Append("\",\"contentDetails\":{\"relatedPlaylists\":{\"uploads\":\"UU").Append(id[2..]).Append("\"}}}");
                }
                return Task.FromResult(Json(sb.Append("]}").ToString()));
            }
            if (path.StartsWith("playlistItems", StringComparison.Ordinal))
            {
                var ch = "UC" + q["playlistId"][2..];
                var sb = new StringBuilder("{\"items\":[");
                var first = true;
                foreach (var v in Uploads[ch])
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"contentDetails\":{\"videoId\":\"").Append(v.Id).Append("\"}}");
                }
                return Task.FromResult(Json(sb.Append("]}").ToString()));
            }
            if (path.StartsWith("videos", StringComparison.Ordinal))
            {
                var sb = new StringBuilder("{\"items\":[");
                var first = true;
                foreach (var id in q["id"].Split(','))
                    foreach (var list in Uploads.Values)
                        foreach (var v in list)
                        {
                            if (v.Id != id) continue;
                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append("{\"id\":\"").Append(v.Id).Append("\",\"snippet\":{\"title\":").Append(System.Text.Json.JsonSerializer.Serialize(v.Title))
                              .Append(",\"publishedAt\":\"").Append(v.PublishedAt).Append("\",\"channelId\":\"").Append(v.ChannelId).Append("\"},\"statistics\":{\"viewCount\":\"")
                              .Append(v.Views.ToString(CultureInfo.InvariantCulture)).Append("\",\"likeCount\":\"7\",\"commentCount\":\"2\"}}");
                        }
                return Task.FromResult(Json(sb.Append("]}").ToString()));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static Dictionary<string, string> Query(string url)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var qi = url.IndexOf('?');
            if (qi < 0) return d;
            foreach (var kv in url[(qi + 1)..].Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq > 0) d[kv[..eq]] = Uri.UnescapeDataString(kv[(eq + 1)..]);
            }
            return d;
        }

        public int AnalyticsCallsFor(string channelId)
        {
            var n = 0;
            foreach (var u in AnalyticsUrls) if (u.Contains(Uri.EscapeDataString("channel==" + channelId), StringComparison.Ordinal)) n++;
            return n;
        }
    }

    /// <summary>폐루프 호출 계수기.</summary>
    private sealed class CountingLoop : IPlutoClosedLoopReinforcementService
    {
        public int PerformanceCalls, MaturityCalls;
        public readonly List<PlutoVideoPerformanceTelemetry> Seen = new();
        public PlutoReinforcementOutcome? LastOutcome { get; private set; }
        public event Action? Changed { add { } remove { } }

        public Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default)
        {
            PerformanceCalls++;
            Seen.Add(telemetry);
            return Task.FromResult(LastOutcome = new PlutoReinforcementOutcome(false, false, 0, 1.0, "counted"));
        }

        public Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "n/a"));

        public Task<PlutoReinforcementOutcome> IngestMaturityDeltaAsync(PlutoVideoPerformanceTelemetry matured, PlutoVideoPerformanceTelemetry previous, int stage, double weight, CancellationToken ct = default)
        {
            MaturityCalls++;
            Seen.Add(matured);
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "maturity"));
        }
    }

    private static PlutoYouTubeApiCredentials OAuthCreds(params PlutoYouTubeApiChannel[] channels) => new()
    {
        ClientId = "123-abc.apps.googleusercontent.com", ClientSecret = ClientSecret, RefreshToken = RefreshToken, Channels = channels,
    };

    private PlutoYouTubeApiSyncService Service(FakeGoogle handler, IPlutoTelemetryBatchImporter? importer, string store, Func<DateTimeOffset>? clock = null, IPlutoBriefOutcomeAttributionService? briefs = null)
        => new(importer, handler, Sub(store))
        {
            UtcNow = clock ?? (() => Now),
            Delay = (_, _) => Task.CompletedTask,
            BriefOutcomes = briefs,
        };

    private static async Task Put(PlutoRenderCatalogService catalog, PlutoRenderedClipRecord rec, string ytId, PlutoYouTubeIngestWatermark wm)
    {
        await catalog.RecordRenderedClipAsync(rec with { YouTubeVideoId = ytId });
        Assert.True(await catalog.UpdateYouTubeWatermarkAsync(rec.VideoId, wm));
    }

    // ── 1. E2E — OAuth Analytics v2 → 정밀 학습 · A/B 귀속 · 기획 채점(사후 분석) · 유기적 복권 · 멱등 재동기화 · 피드 ──

    [Fact]
    public async Task OAuthAnalytics_PromotesPrecision_IntoLearningAbAttributionBriefGradingAndOrganicRehabilitation()
    {
        const string hook = "pov-walk";
        var golden = PlutoColorGradingCompiler.GoldenHourWarmName;
        var pastel = PlutoColorGradingCompiler.NaturalPastelName;
        var nbNow = NbT0;
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "e2e_nb.json"), () => nbNow);
        var pair = PlutoPairAttribution.PairKey(hook, golden)!;
        await nb.RecordPostMortemMistakeAsync(new[] { pair }, PlutoBriefOutcomeAttributionService.PostMortemPenalty, PlutoMistakeNotebookService.DimensionCompletionDrop, "brief-postmortem: B0 Δ-20.0p");
        Assert.Contains(pair, nb.GetSuppressedCompositeKeys());
        nbNow = NbT0.AddDays(2);

        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "e2e_catalog.json"));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        // 측정 완료 기준선 2건(합성 28)
        await Put(catalog, Clip("base-1", "base one", "other hook", pastel, new DateTimeOffset(NbT0.AddDays(-5))), "ytBASEBASE1", new(1000, 0, 40, 20, 2.0, new DateTimeOffset(NbT0)));
        await Put(catalog, Clip("base-2", "base two", "other hook", pastel, new DateTimeOffset(NbT0.AddDays(-4))), "ytBASEBASE2", new(1000, 0, 40, 20, 2.0, new DateTimeOffset(NbT0)));
        // 🎯 기획 적용 클립(기대 합성 50) — A/B 제목 패키지 복사 기록 · 업로드는 반대 변형 제목
        var briefClip = Clip("brief-a", "가을 숲 산책 1편", hook, pastel, new DateTimeOffset(NbT0.AddDays(1))) with
        {
            AppliedBriefId = "brief-1", AppliedBriefFormat = "single", AppliedBriefExpectedRetention = 50.0, AppliedBriefKind = "explore", AppliedBriefHookStyle = hook,
            AppliedBriefColorLookId = pastel, AppliedBriefBgmMood = "Lullaby", AppliedBriefTheme = "가을 숲 산책", AppliedBriefTitleFraming = PlutoTitleFramings.Unknown,
            AppliedBriefAppliedAtUtc = new DateTimeOffset(NbT0.AddDays(1)), YouTubeVideoId = Vid('a', 1),
        };
        await catalog.RecordRenderedClipAsync(briefClip);
        await catalog.RecordPublishedTitleAsync("brief-a", new PlutoPublishedTitleSelection("A", PlutoTitleFramings.Unknown, "가을 숲 산책 1편", PlutoTitleFramings.Unknown, "끝까지 보면 보이는 가을 숲의 비밀"));
        // 🌱 벌점 조합(pov-walk × Golden)으로 벌점 이후 렌더한 커스텀 클립
        await catalog.RecordRenderedClipAsync(Clip("comeback", "노을 캠핑 브이로그", hook, golden, new DateTimeOffset(NbT0.AddDays(1))) with { YouTubeVideoId = Vid('a', 2) });

        var fake = new FakeGoogle();
        fake.Uploads[KoChannel] = new List<FakeVideo>
        {
            new(Vid('a', 1), KoChannel, "끝까지 보면 보이는 가을 숲의 비밀", 820),
            new(Vid('a', 2), KoChannel, "노을 캠핑 브이로그", 900),
        };
        fake.Analytics[Vid('a', 1)] = new FakeMetrics(800, 15.0, 1.0);    // 합성 14 → 기대 50 대비 −36 심각 미달
        fake.Analytics[Vid('a', 2)] = new FakeMetrics(880, 65.0, 7.0);    // 합성 66 → 기준선 대비 대폭 초과 → 복권
        using var briefs = new PlutoBriefOutcomeAttributionService(catalog, nb);
        using var svc = Service(fake, importer, "e2e", briefs: briefs);
        Assert.True((await svc.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel, "ko-KR")))).Ok);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, svc.GetAnalyticsPrecisionStatus().Mode);

        var r = await svc.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, r.State);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, r.AnalyticsMode);
        Assert.Equal(2, r.AnalyticsPromotedCount);
        Assert.Equal(0, r.AnalyticsDeferredCount);
        // 📡 reports.query 형식 — 채널 ID · 영상 차원 · 4지표 · 안전 창(종료일 = 태평양 오늘 − 2) · Bearer · API 키 미전송
        var aUrl = Assert.Single(fake.AnalyticsUrls);
        Assert.Contains("ids=" + Uri.EscapeDataString("channel==" + KoChannel), aUrl, StringComparison.Ordinal);
        Assert.Contains("dimensions=video", aUrl, StringComparison.Ordinal);
        Assert.Contains("metrics=" + Uri.EscapeDataString(PlutoYouTubeApiSyncService.AnalyticsMetrics), aUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("averageViewPercentage,videoThumbnailImpressionsClickRate"), aUrl, StringComparison.Ordinal);   // 공식 v2 CTR 지표명으로 요청
        Assert.DoesNotContain("impressionsClickThroughRate", aUrl, StringComparison.Ordinal);
        Assert.Contains("endDate=2026-11-22", aUrl, StringComparison.Ordinal);
        Assert.Contains("startDate=2026-11-14", aUrl, StringComparison.Ordinal);
        var aReq = fake.Requests.Find(q => q.RequestUri!.ToString() == aUrl)!;
        Assert.Equal("Bearer", aReq.Headers.Authorization?.Scheme);
        Assert.False(aReq.Headers.Contains("X-Goog-Api-Key"));
        Assert.Equal(4, r.QuotaUnitsUsed);   // 💸 channels + playlistItems + videos + reports(1 묶음) = 4 unit(토큰 갱신은 쿼터 아님)
        // 🧠 정밀 학습(관측 차선이 아닌 폐루프) — CTR · 유지율 실측 그대로
        Assert.Equal(2, loop.PerformanceCalls);
        Assert.Equal(2, r.PrecisionImport!.MatchedAndLearnedCount);
        Assert.True(r.PrecisionImport.IsYouTubeAnalyticsPrecision);
        Assert.Contains(loop.Seen, t => Math.Abs(t.ClickThroughRatePct - 7.0) < 1e-9 && Math.Abs(t.AverageCompletionPct - 65.0) < 1e-9);
        var comeback = await catalog.FindByYouTubeVideoIdAsync(Vid('a', 2));
        Assert.True(comeback!.TelemetryLogged);
        Assert.Equal(880, comeback.YouTubeWatermark!.Views);
        Assert.Equal(7.0, comeback.YouTubeWatermark.CtrPct, 6);
        Assert.Equal(65.0, comeback.YouTubeWatermark.CompletionPct, 6);
        Assert.Equal(900, comeback.YouTubeApiStats!.Views);   // 관측 차선(실시간)은 별도 유지
        // 🅰️🅱️ 게시 제목 = 반대 변형 → B 귀속
        var briefAfter = await catalog.FindByYouTubeVideoIdAsync(Vid('a', 1));
        Assert.Equal("B", briefAfter!.SelectedTitleVariant);
        Assert.Equal(PlutoTitleAttributionStates.Confirmed, briefAfter.TitleAttribution);
        // 🎯 기획 채점 · 🩻 사후 분석 · 🌱 유기적 복권 — 수동 CSV 없이 즉시
        Assert.NotNull(r.BriefSweep);
        Assert.Equal(1, r.BriefSweep!.EvaluatedCount);
        Assert.Equal(1, r.BriefSweep.PostMortemCount);
        Assert.True(Assert.Single(r.BriefSweep.Verdicts).IsSevere);
        Assert.Contains(pair, r.BriefSweep.RehabilitatedKeys);
        Assert.True(nb.GetEffectivePenalty(pair) < PlutoBriefOutcomeAttributionService.PostMortemPenalty * Math.Pow(0.5, 2.0 / 30.0) - 1e-6);
        Assert.Contains(PlutoPairAttribution.PairKey(hook, pastel)!, nb.GetSuppressedCompositeKeys());
        Assert.Contains("📊 정밀 2건", r.SummaryKorean, StringComparison.Ordinal);

        // 🛰️ 피드 — 정밀 학습 1건 기록
        using var feed = new PlutoAutonomousActivityFeedService(Path.Combine(Sub("feed"), PlutoAutonomousActivityFeedService.FeedFileName));
        Assert.True(feed.RecordApiSync(r, background: true));
        Assert.Equal(PlutoActivityKind.AnalyticsPrecision, feed.Latest!.Kind);
        Assert.Contains("CTR · 유지율 ✅ 학습 2건", feed.Latest.MessageKorean, StringComparison.Ordinal);
        Assert.Contains("🎯 기획 채점 1건", feed.Latest.MessageKorean, StringComparison.Ordinal);

        // 🔁 같은 스냅샷 재동기화 → 0건 강화 · 채점 재실행 없음 · 피드 소음 없음
        var again = await svc.SyncNowAsync();
        Assert.Equal(2, again.PrecisionImport!.AlreadyRegisteredCount);
        Assert.Equal(2, loop.PerformanceCalls);
        Assert.Equal(0, loop.MaturityCalls);
        Assert.Null(again.BriefSweep);
        Assert.False(feed.RecordApiSync(again, background: true));
        var status = svc.GetAnalyticsPrecisionStatus();
        Assert.Equal(Now, status.LastPrecisionUtc);
        Assert.Equal(2, status.LastPromotedCount);
        Assert.Contains("정밀 동기화 활성", status.BadgeKorean, StringComparison.Ordinal);
    }

    // ── 2. 관측 전용 강등 — API 키만 · Analytics 403(범위 부족) ──

    [Fact]
    public async Task ApiKeyOnlyOrAnalytics403_FallsBackToObserveOnly_WithoutBreakingDataApiSync()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "fb_catalog.json"));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        await catalog.RecordRenderedClipAsync(Clip("fb_clip", "바다 일출 타임랩스", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, Now.AddDays(-12)) with { YouTubeVideoId = Vid('f', 1) });
        var fake = new FakeGoogle();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('f', 1), KoChannel, "바다 일출 타임랩스", 1500) };
        fake.Analytics[Vid('f', 1)] = new FakeMetrics(1400, 55.0, 6.0);

        // (a) API 키만 → Analytics 호출 0 · Step 33 관측 경로 그대로
        using (var keyOnly = Service(fake, importer, "fb_key"))
        {
            Assert.True((await keyOnly.SaveCredentialsAsync(new PlutoYouTubeApiCredentials { ApiKey = ApiKey, Channels = new[] { new PlutoYouTubeApiChannel(KoChannel) } })).Ok);
            Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.ApiKeyObserveOnly, keyOnly.GetAnalyticsPrecisionStatus().Mode);
            var r = await keyOnly.SyncNowAsync();
            Assert.Equal(PlutoYouTubeApiSyncState.Synced, r.State);
            Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.ApiKeyObserveOnly, r.AnalyticsMode);
            Assert.Empty(fake.AnalyticsUrls);
            Assert.Null(r.PrecisionImport);
            Assert.Equal(1, r.Import!.ApiObservedCount);
            Assert.Equal(0, loop.PerformanceCalls + loop.MaturityCalls);
            Assert.Null((await catalog.FindByYouTubeVideoIdAsync(Vid('f', 1)))!.YouTubeWatermark);
            Assert.Contains("API 키 관측 전용", keyOnly.GetAnalyticsPrecisionStatus().BadgeKorean, StringComparison.Ordinal);
        }

        // (b) OAuth 이지만 yt-analytics.readonly 없음 → 403 → 관측 전용 강등 · Data API 결과 Synced · 자동 동기화 인증 정지 없음
        fake.ForbiddenChannels.Add(KoChannel);
        using var noScope = Service(fake, importer, "fb_scope");
        Assert.True((await noScope.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var s = await noScope.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, s.State);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope, s.AnalyticsMode);
        Assert.Equal(1, s.AnalyticsScopeMissingChannels);
        Assert.Equal(1, s.Import!.ApiObservedCount);
        Assert.Equal(0, loop.PerformanceCalls + loop.MaturityCalls);
        Assert.Contains("범위 대기", s.SummaryKorean, StringComparison.Ordinal);
        var st = noScope.GetAnalyticsPrecisionStatus();
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope, st.Mode);
        Assert.Contains("yt-analytics.readonly", st.BadgeKorean, StringComparison.Ordinal);
        Assert.Equal(Now + PlutoYouTubeApiSyncService.AnalyticsScopeRetryInterval, st.ScopeRetryUtc);
        var tick = await noScope.RunAutoSyncTickAsync(force: true);
        Assert.NotNull(tick);
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, tick!.State);
        Assert.NotEqual(PlutoYouTubeApiAutoSyncPhase.AuthPaused, noScope.GetAutoSyncStatus().Phase);
        noScope.StopAutoSync();
    }

    // ── 3. 무네트워크 격리 — 미설정 · PROMPTFLOW_PLUTO_DRIVE=off · PROMPTFLOW_DISABLE_YOUTUBE_API=1 · 사용자 끔 ──

    [Fact]
    public async Task UnconfiguredIsolatedKillSwitchOrToggleOff_MakesZeroAnalyticsCalls()
    {
        var idle = new FakeGoogle();
        using (var unset = Service(idle, null, "iso_unset"))
        {
            Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.NotConfigured, unset.GetAnalyticsPrecisionStatus().Mode);
            Assert.Equal(PlutoYouTubeApiSyncState.NotConfigured, (await unset.SyncNowAsync()).State);
            Assert.Equal(0, unset.NetworkAttemptCount);
        }
        Assert.Empty(idle.Requests);

        // 실 전송(주입 없음) + 하네스 PROMPTFLOW_PLUTO_DRIVE=off → Disabled · 0 요청
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        var store = Sub("iso_real");
        using (var seed = new PlutoYouTubeApiSyncService(null, new FakeGoogle(), store))
            Assert.True((await seed.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        using (var real = new PlutoYouTubeApiSyncService(null, null, store))
        {
            Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Disabled, real.GetAnalyticsPrecisionStatus().Mode);
            Assert.Equal(PlutoYouTubeApiSyncState.Disabled, (await real.SyncNowAsync()).State);
            Assert.Equal(0, real.NetworkAttemptCount);
        }

        // 킬 스위치 → 주입 전송까지 차단
        var killed = new FakeGoogle();
        killed.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('z', 1), KoChannel, "킬 스위치 영상", 500) };
        killed.Analytics[Vid('z', 1)] = new FakeMetrics(480, 50, 5);
        using var svcK = Service(killed, null, "iso_kill");
        Assert.True((await svcK.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, "1");
        try
        {
            Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Disabled, svcK.GetAnalyticsPrecisionStatus().Mode);
            Assert.Equal(PlutoYouTubeApiSyncState.Disabled, (await svcK.SyncNowAsync()).State);
            Assert.Empty(killed.Requests);
        }
        finally { Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, null); }

        // 사용자 끔(영속) → Data API 만 · Analytics 0 · 재시작 후에도 유지
        await svcK.SetAnalyticsPrecisionEnabledAsync(false);
        var off = await svcK.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, off.State);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Disabled, off.AnalyticsMode);
        Assert.Empty(killed.AnalyticsUrls);
        using var restarted = Service(killed, null, "iso_kill");
        var rs = restarted.GetAnalyticsPrecisionStatus();
        Assert.False(rs.Enabled);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Disabled, rs.Mode);
        await restarted.SetAnalyticsPrecisionEnabledAsync(true);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, restarted.GetAnalyticsPrecisionStatus().Mode);
    }

    // ── 3b. 🔎 24시간 403 래치 만료 → 새 승인 · 성공 조회 전까지 '재확인'(활성 승격 금지) · 킬 스위치 시 토글 실효 OFF ──

    [Fact]
    public async Task ScopeLatchExpiry_StaysRechecking_UntilSuccessfulQuery_AndKillSwitchMakesTogglesEffectivelyOff()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "recheck_catalog.json"));
        var importer = new PlutoTelemetryBatchImporter(catalog, new CountingLoop());
        await catalog.RecordRenderedClipAsync(Clip("recheck_clip", "재확인 영상", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, Now.AddDays(-12)) with { YouTubeVideoId = Vid('r', 1) });
        var fake = new FakeGoogle();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('r', 1), KoChannel, "재확인 영상", 1200) };
        fake.Analytics[Vid('r', 1)] = new FakeMetrics(1100, 50.0, 5.0);
        fake.ForbiddenChannels.Add(KoChannel);
        var clock = Now;
        using var svc = Service(fake, importer, "recheck", () => clock);
        Assert.True((await svc.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope, (await svc.SyncNowAsync()).AnalyticsMode);

        // 24시간 경과 · 승인 갱신 없음 → '활성' 이 아니라 재확인(관찰)
        clock = Now + PlutoYouTubeApiSyncService.AnalyticsScopeRetryInterval + TimeSpan.FromMinutes(1);
        var st = svc.GetAnalyticsPrecisionStatus();
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.RecheckingScope, st.Mode);
        Assert.NotEqual(0, st.Severity);
        Assert.Contains("재확인", st.BadgeKorean, StringComparison.Ordinal);
        Assert.DoesNotContain("활성", st.BadgeKorean, StringComparison.Ordinal);

        // 재확인 동기화가 다시 403 → 대기로 복귀(다시 24시간 래치)
        var calls = fake.AnalyticsCallsFor(KoChannel);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope, (await svc.SyncNowAsync()).AnalyticsMode);
        Assert.Equal(calls + 1, fake.AnalyticsCallsFor(KoChannel));

        // 다음 만료 후 범위 확보 → 성공 조회로만 활성 승격 · 래치 해제
        fake.ForbiddenChannels.Clear();
        clock += PlutoYouTubeApiSyncService.AnalyticsScopeRetryInterval + TimeSpan.FromMinutes(1);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.RecheckingScope, svc.GetAnalyticsPrecisionStatus().Mode);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, (await svc.SyncNowAsync()).AnalyticsMode);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, svc.GetAnalyticsPrecisionStatus().Mode);

        // 킬 스위치 → 사용자 토글은 켜져 있어도 실효 OFF('ON' 옆 '꺼짐' 배지 모순 없음)
        Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, "1");
        try
        {
            var p = svc.GetAnalyticsPrecisionStatus();
            Assert.True(p.Enabled);
            Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Disabled, p.Mode);
            Assert.False(p.EffectiveOn);
            Assert.NotEqual("ON", p.ToggleStateKorean);
            var a = svc.GetAutoSyncStatus();
            Assert.True(a.Enabled);
            Assert.Equal(PlutoYouTubeApiAutoSyncPhase.Unavailable, a.Phase);
            Assert.False(a.EffectiveOn);
            Assert.NotEqual("ON", a.ToggleStateKorean);
        }
        finally { Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, null); }
        Assert.True(svc.GetAnalyticsPrecisionStatus().EffectiveOn);
        Assert.Equal("ON", svc.GetAnalyticsPrecisionStatus().ToggleStateKorean);
    }

    // ── 4. 🛡️ 자체 추가 A — 🕒 보고 지연 가드(최근 게시 · 0 CTR · 저조회 → 관측 전용 유지) + 스키마 위반 격리 ──

    [Fact]
    public async Task SelfAddition_ReportingLagGuard_DefersImmatureAndZeroMetricRows_AndSchemaViolationKeepsDataSync()
    {
        // 순수 판정 — 종료일 11-22(태평양) · 게시 11-21 이전만 성숙
        Assert.Equal(new DateOnly(2026, 11, 22), PlutoYouTubeApiSyncService.AnalyticsEndDate(Now));
        Assert.True(PlutoYouTubeApiSyncService.IsAnalyticsReportMature(DateTimeOffset.Parse(MaturePublished, CultureInfo.InvariantCulture), Now));
        Assert.False(PlutoYouTubeApiSyncService.IsAnalyticsReportMature(DateTimeOffset.Parse(FreshPublished, CultureInfo.InvariantCulture), Now));
        Assert.False(PlutoYouTubeApiSyncService.IsAnalyticsReportMature(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero), Now));   // 종료일 당일 게시 → 집계 1일 미만
        Assert.False(PlutoYouTubeApiSyncService.IsAnalyticsReportMature(null, Now));
        var observed = new YouTubeAnalyticsRow(Vid('l', 0), "t", Now.AddDays(-9), 100, 0, true, 0, 0, 0, true) { HasPrecisionMetrics = false };
        Assert.False(PlutoYouTubeApiSyncService.TryBuildPrecisionRow(observed, new(500, 10, 40.0, 0.0), out _));   // 노출 미집계(CTR 0)
        Assert.False(PlutoYouTubeApiSyncService.TryBuildPrecisionRow(observed, new(500, 10, 0.0, 4.0), out _));    // 유지율 미집계
        Assert.False(PlutoYouTubeApiSyncService.TryBuildPrecisionRow(observed, new(9, 10, 40.0, 4.0), out _));     // 저조회 노이즈
        Assert.True(PlutoYouTubeApiSyncService.TryBuildPrecisionRow(observed, new(500, 10, 130.0, 4.0), out var looped));
        Assert.True(looped.HasPrecisionMetrics);
        Assert.Equal(YouTubeStudioAnalyticsNormalizer.SaturateCompletion(130.0), looped.CompletionRate, 9);   // Shorts 반복 재생 → Studio 와 같은 포화
        Assert.True(looped.CompletionRate < 100.0);
        Assert.True(looped.HookEstimated);

        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "lag_catalog.json"));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        var look = PlutoColorGradingCompiler.NaturalPastelName;
        await catalog.RecordRenderedClipAsync(Clip("lag_mature", "성숙 영상", "pov-walk", look, Now.AddDays(-12)) with { YouTubeVideoId = Vid('l', 1) });
        await catalog.RecordRenderedClipAsync(Clip("lag_fresh", "방금 올린 영상", "pov-walk", look, Now.AddDays(-2)) with { YouTubeVideoId = Vid('l', 2) });
        await catalog.RecordRenderedClipAsync(Clip("lag_zero", "노출 미집계 영상", "pov-walk", look, Now.AddDays(-12)) with { YouTubeVideoId = Vid('l', 3) });
        var fake = new FakeGoogle();
        fake.Uploads[KoChannel] = new List<FakeVideo>
        {
            new(Vid('l', 1), KoChannel, "성숙 영상", 700),
            new(Vid('l', 2), KoChannel, "방금 올린 영상", 300, FreshPublished),
            new(Vid('l', 3), KoChannel, "노출 미집계 영상", 400),
        };
        fake.Analytics[Vid('l', 1)] = new FakeMetrics(650, 48.0, 5.5);
        fake.Analytics[Vid('l', 2)] = new FakeMetrics(250, 20.0, 0.4);   // 집계 중 부분값 — 요청조차 하지 않아야 함
        fake.Analytics[Vid('l', 3)] = new FakeMetrics(380, 44.0, 0.0);
        using var svc = Service(fake, importer, "lag");
        Assert.True((await svc.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var r = await svc.SyncNowAsync();
        Assert.Equal(1, r.AnalyticsPromotedCount);
        Assert.Equal(2, r.AnalyticsDeferredCount);
        var aUrl = Assert.Single(fake.AnalyticsUrls);
        Assert.DoesNotContain(Vid('l', 2), aUrl, StringComparison.Ordinal);
        Assert.Equal(1, loop.PerformanceCalls);
        Assert.NotNull((await catalog.FindByYouTubeVideoIdAsync(Vid('l', 1)))!.YouTubeWatermark);
        Assert.Null((await catalog.FindByYouTubeVideoIdAsync(Vid('l', 2)))!.YouTubeWatermark);   // 기준선 오염 없음
        Assert.Null((await catalog.FindByYouTubeVideoIdAsync(Vid('l', 3)))!.YouTubeWatermark);
        Assert.Equal(400, (await catalog.FindByYouTubeVideoIdAsync(Vid('l', 3)))!.YouTubeApiStats!.Views);   // 관측은 계속
        Assert.Contains("보고 지연 보류 2건", r.SummaryKorean, StringComparison.Ordinal);

        // 📐 스키마 위반(CTR 열 없음) → 정밀만 격리 · Data API 동기화는 Synced · 학습 0
        var bad = new FakeGoogle { AnalyticsBodyOverride = "{\"columnHeaders\":[{\"name\":\"video\"},{\"name\":\"views\"},{\"name\":\"averageViewPercentage\"}],\"rows\":[[\"" + Vid('m', 1) + "\",900,50.0]]}" };
        bad.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('m', 1), KoChannel, "스키마 영상", 950) };
        await catalog.RecordRenderedClipAsync(Clip("schema_clip", "스키마 영상", "pov-walk", look, Now.AddDays(-12)) with { YouTubeVideoId = Vid('m', 1) });
        using var svcBad = Service(bad, importer, "lag_schema");
        Assert.True((await svcBad.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var b = await svcBad.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, b.State);
        Assert.Equal(1, b.Import!.ApiObservedCount);
        Assert.Contains("정밀 지표 조회 실패(관측 유지)", b.SummaryKorean, StringComparison.Ordinal);
        Assert.Equal(1, loop.PerformanceCalls);
        Assert.Throws<InvalidDataException>(() => PlutoYouTubeApiSyncService.ParseAnalyticsReport(System.Text.Json.JsonDocument.Parse("{\"rows\":[]}")));
        Assert.Empty(PlutoYouTubeApiSyncService.ParseAnalyticsReport(System.Text.Json.JsonDocument.Parse(
            "{\"columnHeaders\":[{\"name\":\"video\"},{\"name\":\"views\"},{\"name\":\"averageViewPercentage\"},{\"name\":\"impressionsClickThroughRate\"}]}")));   // rows 없음 = 데이터 없음
    }

    // ── 5. 🛡️ 자체 추가 B — 🔐 채널 단위 범위 래치(403 채널만 24시간 정지 · 다른 채널 학습 · 재저장 즉시 해제 · 피드 하루 1건) ──

    [Fact]
    public async Task SelfAddition_PerChannelScopeLatch_IsolatesForbiddenChannel_SavesQuota_AndClearsOnResave()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "latch_catalog.json"));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        var look = PlutoColorGradingCompiler.NaturalPastelName;
        await catalog.RecordRenderedClipAsync(Clip("latch_ko", "한국 채널 영상", "pov-walk", look, Now.AddDays(-12)) with { YouTubeVideoId = Vid('k', 1) });
        await catalog.RecordRenderedClipAsync(Clip("latch_ja", "日本チャンネル動画", "pov-walk", look, Now.AddDays(-12)) with { YouTubeVideoId = Vid('j', 1) });
        var fake = new FakeGoogle();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('k', 1), KoChannel, "한국 채널 영상", 900) };
        fake.Uploads[JaChannel] = new List<FakeVideo> { new(Vid('j', 1), JaChannel, "日本チャンネル動画", 800) };
        fake.Analytics[Vid('k', 1)] = new FakeMetrics(850, 50.0, 5.0);
        fake.Analytics[Vid('j', 1)] = new FakeMetrics(760, 45.0, 4.5);
        fake.ForbiddenChannels.Add(JaChannel);   // 이 OAuth 계정이 관리하지 않는 브랜드 채널
        var clock = Now;
        using var svc = Service(fake, importer, "latch", () => clock);
        Assert.True((await svc.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel, "ko-KR"), new PlutoYouTubeApiChannel(JaChannel, "ja-JP")))).Ok);

        var r1 = await svc.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, r1.State);
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, r1.AnalyticsMode);   // 한 채널이라도 범위 OK → 활성(부분)
        Assert.Equal(1, r1.AnalyticsScopeMissingChannels);
        Assert.Equal(1, r1.AnalyticsPromotedCount);
        Assert.Equal(1, loop.PerformanceCalls);
        Assert.NotNull((await catalog.FindByYouTubeVideoIdAsync(Vid('k', 1)))!.YouTubeWatermark);
        Assert.Null((await catalog.FindByYouTubeVideoIdAsync(Vid('j', 1)))!.YouTubeWatermark);
        Assert.Contains("채널 1/2", svc.GetAnalyticsPrecisionStatus().BadgeKorean, StringComparison.Ordinal);

        // ⏱️ 30분 뒤 재동기화 → 래치된 채널은 요청 0(쿼터 절약)
        clock = Now.AddMinutes(30);
        var jaBefore = fake.AnalyticsCallsFor(JaChannel);
        var r2 = await svc.SyncNowAsync();
        Assert.Equal(jaBefore, fake.AnalyticsCallsFor(JaChannel));
        Assert.Equal(2, fake.AnalyticsCallsFor(KoChannel));
        Assert.Equal(1, r2.AnalyticsScopeMissingChannels);

        // 🛰️ 같은 날 범위 대기 피드는 1건만
        using var feed = new PlutoAutonomousActivityFeedService(Path.Combine(Sub("latch_feed"), PlutoAutonomousActivityFeedService.FeedFileName));
        feed.RecordApiSync(r1, background: true);
        feed.RecordApiSync(r2, background: true);
        var scopeEntries = 0;
        foreach (var e in feed.GetRecent()) if (e.MessageKorean.Contains("범위 대기", StringComparison.Ordinal)) scopeEntries++;
        Assert.Equal(1, scopeEntries);

        // 🔐 재승인 후 자격 증명 재저장 → 래치 즉시 해제 → 재확인 · 범위 OK → 학습
        fake.ForbiddenChannels.Clear();
        Assert.True((await svc.SaveCredentialsAsync(new PlutoYouTubeApiCredentials { RefreshToken = "1//0refresh-token-with-analytics-scope" })).Ok);
        Assert.Equal(0, svc.GetAnalyticsPrecisionStatus().ScopeMissingChannels);
        var r3 = await svc.SyncNowAsync();
        Assert.Equal(jaBefore + 1, fake.AnalyticsCallsFor(JaChannel));
        Assert.Equal(0, r3.AnalyticsScopeMissingChannels);
        Assert.Equal(2, loop.PerformanceCalls);
        Assert.NotNull((await catalog.FindByYouTubeVideoIdAsync(Vid('j', 1)))!.YouTubeWatermark);

        // ⏳ 래치는 24시간 뒤 자동 재확인
        fake.ForbiddenChannels.Add(KoChannel);
        clock = Now.AddHours(1);
        await svc.SyncNowAsync();
        var koAfterLatch = fake.AnalyticsCallsFor(KoChannel);
        clock = Now.AddHours(2);
        await svc.SyncNowAsync();
        Assert.Equal(koAfterLatch, fake.AnalyticsCallsFor(KoChannel));
        clock = Now.AddHours(1) + PlutoYouTubeApiSyncService.AnalyticsScopeRetryInterval + TimeSpan.FromMinutes(1);
        await svc.SyncNowAsync();
        Assert.Equal(koAfterLatch + 1, fake.AnalyticsCallsFor(KoChannel));
    }

    // ── 6. 🛡️ 자체 추가 C — 🥇 Studio 우선(대기열 Studio 실측이 먼저 기준선 · 3초 유지 실측 승계 · 지연 Studio 재수집 멱등) ──

    [Fact]
    public async Task SelfAddition_StudioPrecedence_QueueRecoveryRunsFirst_AndMeasuredHookIsCarriedIntoAnalyticsMaturity()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "prec_catalog.json"));
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "prec_nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, null);
        using var queue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "prec_queue.json"), catalog, loop);
        var importer = new PlutoTelemetryBatchImporter(catalog, loop, queue);
        await catalog.RecordRenderedClipAsync(Clip("prec_clip", "별이 쏟아지는 캠핑 밤", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, Now.AddDays(-12)));

        // Studio 내보내기 — 제목이 달라 자동 연결 실패 → 실측(3초 유지 64 · CTR 6.6)이 대기열에 묶임
        var studio = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('p', 1), "zzz 전혀 다른 표제", 700, 52, 6.6, 64), "Table data.csv");
        Assert.Equal(1, studio.QueuedUnmatchedCount);

        var fake = new FakeGoogle();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('p', 1), KoChannel, "별이 쏟아지는 캠핑 밤", 760) };
        fake.Analytics[Vid('p', 1)] = new FakeMetrics(720, 30.0, 3.0);   // 같은 시기 스냅샷 — Studio 와 다른 값
        using var svc = Service(fake, importer, "prec");
        Assert.True((await svc.SaveCredentialsAsync(OAuthCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var r = await svc.SyncNowAsync();
        // ① 대기열 Studio 행이 먼저 회수 · 학습 → Analytics 같은 스냅샷은 기반영(0건 강화)
        Assert.Equal(1, r.Import!.QueueRecoveredCount);
        Assert.Equal(0, queue.Count);
        Assert.Equal(1, r.PrecisionImport!.AlreadyRegisteredCount);
        Assert.Equal(0, r.PrecisionImport.MatchedAndLearnedCount);
        Assert.Equal(1, r.PrecisionImport.StudioHookCarriedCount);
        var clip = await catalog.FindByYouTubeVideoIdAsync(Vid('p', 1));
        Assert.Equal(64, clip!.YouTubeWatermark!.HookPct, 3);
        Assert.Equal(6.6, clip.YouTubeWatermark.CtrPct, 3);   // Studio 실측 유지
        Assert.Equal(0, clip.YouTubeWatermark.Stage);

        // ② 실질 성숙(5000회) → Analytics 성숙 증분 · 3초 유지는 추정(완주율 기반 40)이 아니라 Studio 실측 64 승계
        fake.Uploads[KoChannel][0] = fake.Uploads[KoChannel][0] with { Views = 5200 };
        fake.Analytics[Vid('p', 1)] = new FakeMetrics(5000, 40.0, 5.1);
        var m = await svc.SyncNowAsync();
        Assert.Equal(1, m.PrecisionImport!.MaturityDeltaCount);
        clip = await catalog.FindByYouTubeVideoIdAsync(Vid('p', 1));
        Assert.Equal(1, clip!.YouTubeWatermark!.Stage);
        Assert.Equal(64, clip.YouTubeWatermark.HookPct, 3);
        Assert.Equal(5.1, clip.YouTubeWatermark.CtrPct, 3);
        Assert.Equal(40.0, clip.YouTubeWatermark.CompletionPct, 3);

        // ③ 뒤늦게 도착한 지연 Studio 내보내기(조회 1800 < 워터마크 5000) → 이중 반영 없음
        var late = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('p', 1), "별이 쏟아지는 캠핑 밤", 1800, 41, 5.0, 66), "Table data (1).csv");
        Assert.Equal(0, late.MatchedAndLearnedCount + late.MaturityDeltaCount);
        Assert.Equal(1, (await catalog.FindByYouTubeVideoIdAsync(Vid('p', 1)))!.YouTubeWatermark!.Stage);
    }

    // ── 7. 🛡️ 가짜 지표 방어 · 기본 인터페이스 구현 — 정밀 경로는 플래그 없는 행 · 미연결 행을 학습하지 않음 ──

    [Fact]
    public async Task PrecisionImporter_RejectsObserveOnlyAndUnboundRows_AndDefaultInterfaceIsNoLearning()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "guard_catalog.json"));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        await catalog.RecordRenderedClipAsync(Clip("guard_clip", "가드 영상", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, Now.AddDays(-12)) with { YouTubeVideoId = Vid('g', 1) });
        var observeOnly = new YouTubeAnalyticsRow(Vid('g', 1), "가드 영상", Now.AddDays(-9), 500, 0, true, 0, 0, 0, true) { HasPrecisionMetrics = false };
        var unbound = new YouTubeAnalyticsRow(Vid('g', 9), "가드 영상", Now.AddDays(-9), 500, 40, true, 40, 40, 4, false);
        var r = await importer.ImportYouTubeAnalyticsPrecisionRowsAsync(new[] { observeOnly, unbound });
        Assert.Equal(2, r.UnmatchedCount);
        Assert.Equal(0, loop.PerformanceCalls + loop.MaturityCalls);
        Assert.Null((await catalog.FindByYouTubeVideoIdAsync(Vid('g', 1)))!.YouTubeWatermark);
        Assert.Null(await catalog.FindByYouTubeVideoIdAsync(Vid('g', 9)));   // 정밀 경로는 제목 추정 연결을 하지 않음

        IPlutoTelemetryBatchImporter legacy = new LegacyImporter();
        var d = await legacy.ImportYouTubeAnalyticsPrecisionRowsAsync(new[] { unbound });
        Assert.True(d.IsYouTubeAnalyticsPrecision);
        Assert.Equal(0, d.MatchedAndLearnedCount);
        IPlutoYouTubeApiSyncService legacySync = new PlutoYouTubeApiSyncService(null, new FakeGoogle(), Sub("guard_iface"));
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.NotConfigured, legacySync.GetAnalyticsPrecisionStatus().Mode);
        ((IDisposable)legacySync).Dispose();
    }

    private sealed class LegacyImporter : IPlutoTelemetryBatchImporter
    {
        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
    }
}
