using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🔄 Step 33 — YouTube Data API v3 다채널 동기화: 봉인 자격 증명 · 토큰 자동 갱신 · 50개 묶음 videos.list · 로캘 변형 연결 · A/B 제목 귀속 ·
/// Studio CSV/ZIP 와의 비파괴 융합 · 429/quotaExceeded 백오프 · 무네트워크 격리 · 자체 추가 안전장치(가짜 지표 차단 · 워터마크 분리 · API 확인 성숙 · 대기열 회수 · 비밀 누출 차단).
/// </summary>
[Collection("ProcessEnvironment")]   // Step 35: 프로세스 환경 변수 변경
public sealed class PlutoYouTubeApiSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ytapi_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly string KoChannel = "UC" + new string('K', 22);
    private static readonly string JaChannel = "UC" + new string('J', 22);
    private static readonly string EsChannel = "UC" + new string('E', 22);
    private const string ApiKey = "AIzaSyTESTKEY-0123456789abcdef";
    private const string ClientSecret = "GOCSPX-test-client-secret-42";
    private const string RefreshToken = "1//0refresh-token-abcdefghijkl";

    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoYouTubeApiSyncTests() => Directory.CreateDirectory(_dir);

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

    private PlutoRenderCatalogService Catalog(string name) => new(Path.Combine(_dir, name + "_catalog.json"));

    private static PlutoRenderedClipRecord Clip(string id, string title, DateTimeOffset? at = null)
        => new(id, title, "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "autumn" }, "", at ?? T0, false);

    private static string Vid(char prefix, int n) => prefix + n.ToString("D10", CultureInfo.InvariantCulture);

    private static byte[] StudioCsv(string ytId, string title, long views, double avgPct, double ctr, double stayed)
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + ytId + "," + title + ",\"Nov 21, 2026\"," + views.ToString(CultureInfo.InvariantCulture) + ","
            + avgPct.ToString(CultureInfo.InvariantCulture) + ",5000," + ctr.ToString(CultureInfo.InvariantCulture) + "," + stayed.ToString(CultureInfo.InvariantCulture) + ",10\n");

    // ── 가짜 YouTube Data API v3 서버 ──

    private sealed record FakeVideo(string Id, string ChannelId, string Title, long Views, long Likes = 5, long Comments = 1);

    private sealed class FakeYouTube : HttpMessageHandler
    {
        public readonly Dictionary<string, List<FakeVideo>> Uploads = new(StringComparer.Ordinal);   // channelId → 업로드
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Urls = new();
        public int TokenPosts;
        public string FreshToken = "ya29.fresh-access-token";
        /// <summary>요청 URL 접두 → 남은 대체 응답(먼저 소비).</summary>
        public readonly List<(string PathPrefix, Func<HttpResponseMessage> Make)> Overrides = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (url.StartsWith(PlutoYouTubeApiSyncService.TokenEndpoint, StringComparison.Ordinal))
            {
                TokenPosts++;
                return Task.FromResult(Json("{\"access_token\":\"" + FreshToken + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"));
            }
            if (!url.StartsWith(PlutoYouTubeApiSyncService.ApiBase, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));   // revoke 등
            var path = url[PlutoYouTubeApiSyncService.ApiBase.Length..];
            for (var i = 0; i < Overrides.Count; i++)
                if (path.StartsWith(Overrides[i].PathPrefix, StringComparison.Ordinal))
                {
                    var make = Overrides[i].Make;
                    Overrides.RemoveAt(i);
                    return Task.FromResult(make());
                }
            var q = Query(url);
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
                var ids = q["id"].Split(',');
                if (ids.Length > PlutoYouTubeApiSyncService.MaxIdsPerRequest) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                var sb = new StringBuilder("{\"items\":[");
                var first = true;
                foreach (var id in ids)
                    foreach (var list in Uploads.Values)
                        foreach (var v in list)
                        {
                            if (v.Id != id) continue;
                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append("{\"id\":\"").Append(v.Id).Append("\",\"snippet\":{\"title\":").Append(System.Text.Json.JsonSerializer.Serialize(v.Title))
                              .Append(",\"publishedAt\":\"2026-11-21T03:00:00Z\",\"channelId\":\"").Append(v.ChannelId).Append("\"},\"statistics\":{\"viewCount\":\"")
                              .Append(v.Views.ToString(CultureInfo.InvariantCulture)).Append("\",\"likeCount\":\"").Append(v.Likes.ToString(CultureInfo.InvariantCulture))
                              .Append("\",\"commentCount\":\"").Append(v.Comments.ToString(CultureInfo.InvariantCulture)).Append("\"},\"contentDetails\":{\"duration\":\"PT45S\"}}");
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

        public int CountPath(string prefix)
        {
            var n = 0;
            foreach (var u in Urls) if (u.StartsWith(PlutoYouTubeApiSyncService.ApiBase + prefix, StringComparison.Ordinal)) n++;
            return n;
        }
    }

    /// <summary>폐루프 호출 계수기(가짜 지표 학습 감지).</summary>
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
            return Task.FromResult(LastOutcome = new PlutoReinforcementOutcome(false, false, 0, 1.0, "maturity " + stage.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static PlutoYouTubeApiCredentials KeyCreds(params PlutoYouTubeApiChannel[] channels) => new() { ApiKey = ApiKey, Channels = channels };

    private PlutoYouTubeApiSyncService Service(FakeYouTube handler, IPlutoTelemetryBatchImporter? importer, string store, List<TimeSpan>? delays = null, int budget = PlutoYouTubeApiSyncService.DefaultDailyUnitBudget)
        => new(importer, handler, Sub(store))
        {
            UtcNow = () => T0.AddDays(1),
            Delay = (d, _) => { delays?.Add(d); return Task.CompletedTask; },
            DailyUnitBudget = budget,
        };

    private static bool Contains(byte[] haystack, string needle)
        => Encoding.Latin1.GetString(haystack).Contains(needle, StringComparison.Ordinal) || Encoding.UTF8.GetString(haystack).Contains(needle, StringComparison.Ordinal);

    // ── 1. 봉인 저장 · Drive 경로 거부 · 만료 토큰 자동 갱신 ──

    [Fact]
    public async Task Credentials_AreSealedLocallyNeverInDrive_AndExpiredAccessTokenRefreshesOnce()
    {
        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo>();
        var store = "creds";
        using (var svc = Service(fake, null, store))
        {
            var save = await svc.SaveCredentialsAsync(new PlutoYouTubeApiCredentials
            {
                ApiKey = ApiKey, ClientId = "123-abc.apps.googleusercontent.com", ClientSecret = ClientSecret, RefreshToken = RefreshToken,
                AccessToken = "ya29.stale-token", AccessTokenExpiresUtc = T0.AddDays(1).AddMinutes(-5),
                Channels = new[] { new PlutoYouTubeApiChannel(KoChannel, "ko-KR") },
            });
            Assert.True(save.Ok, save.MessageKorean);
            var bytes = File.ReadAllBytes(svc.CredentialStorePath);
            Assert.Equal("PFYT", Encoding.ASCII.GetString(bytes, 0, 4));
            foreach (var secret in new[] { ApiKey, ClientSecret, RefreshToken, "ya29.stale-token", KoChannel })
                Assert.False(Contains(bytes, secret), "평문 노출: " + secret);
            Assert.False(PlutoYouTubeApiSyncService.IsCloudSyncedPath(svc.CredentialStorePath));
            var status = svc.GetStatus();
            Assert.Equal("OAuth", status.AuthMode);
            Assert.DoesNotContain("123-abc", status.MaskedCredential, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientSecret, svc.GetStatus().ToString(), StringComparison.Ordinal);
        }

        // 🔁 재시작 → 봉인 개봉 · 만료(5분 전) 토큰 → 갱신 1회 → Bearer 새 토큰 · API 키는 OAuth 우선 시 미전송
        using (var svc2 = Service(fake, null, store))
        {
            Assert.True(svc2.IsConfigured);
            var r = await svc2.TestConnectionAsync();
            Assert.Equal(PlutoYouTubeApiSyncState.Synced, r.State);
            Assert.Equal(1, fake.TokenPosts);
            var apiReq = fake.Requests[^1];
            Assert.Equal("Bearer", apiReq.Headers.Authorization?.Scheme);
            Assert.Equal(fake.FreshToken, apiReq.Headers.Authorization?.Parameter);
            Assert.False(apiReq.Headers.Contains("X-Goog-Api-Key"));
            var sealedAfter = File.ReadAllBytes(svc2.CredentialStorePath);
            Assert.False(Contains(sealedAfter, fake.FreshToken), "갱신 토큰 평문 저장");
        }
        using (var svc3 = Service(fake, null, store))
        {
            await svc3.TestConnectionAsync();
            Assert.Equal(1, fake.TokenPosts);   // 갱신된 토큰이 봉인 저장되어 재시작 후에도 재사용
        }

        // ☁️ Drive · 클라우드 폴더 지정 → 거부되고 로컬 앱 데이터로 대체
        using var drive = new PlutoYouTubeApiSyncService(null, fake, Path.Combine(PlutoDriveStorageManager.DriveVaultRoot, "Pluto"));
        Assert.True(drive.StoreDirectoryRejected);
        Assert.StartsWith(AppDataPaths.LocalAppData, drive.CredentialStorePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(PlutoYouTubeApiSyncService.IsCloudSyncedPath(@"C:\Users\me\My Drive\keys"));
        Assert.True(PlutoYouTubeApiSyncService.IsCloudSyncedPath(@"D:\OneDrive\pfs"));
        Assert.False(PlutoYouTubeApiSyncService.IsCloudSyncedPath(Sub("plain")));

        // 🗑️ 연결 해제 → 파일 삭제 · 미설정
        using var svc4 = Service(fake, null, store);
        await svc4.DisconnectAsync();
        Assert.False(File.Exists(svc4.CredentialStorePath));
        Assert.False(svc4.IsConfigured);
    }

    // ── 2. 다채널 50개 묶음 · ko/ja/es 변형 연결 · A/B 귀속 · API 확인 성숙 증분 ──

    [Fact]
    public async Task MultiChannelSync_BatchesFiftyIds_LinksLocaleVariants_AttributesAbTitle_AndConvergesWithMaturityDelta()
    {
        var catalog = Catalog("multi");
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        await catalog.RecordRenderedClipAsync(Clip("clip_a", "가을 숲 산책 1/3편"));
        var ja = await catalog.BranchLocaleVariantAsync("clip_a", "ja-JP", "秋の森の散歩 第1/3話");
        var es = await catalog.BranchLocaleVariantAsync("clip_a", "es-ES", "Paseo por el bosque de otoño Episodio 1/3");
        Assert.NotNull(ja);
        Assert.NotNull(es);
        // KO 마스터 — 이미 Studio 로 학습된 상태(워터마크 400회) · A/B 제목 패키지 복사 기록
        Assert.Equal(PlutoYouTubeBindResult.Bound, await catalog.TryBindYouTubeVideoIdAsync("clip_a", Vid('k', 0)));
        await catalog.UpdateYouTubeWatermarkAsync("clip_a", new PlutoYouTubeIngestWatermark(400, 0, 50, 40, 5.0, T0));
        await catalog.RecordPublishedTitleAsync("clip_a", new PlutoPublishedTitleSelection("A", PlutoTitleFramings.Unknown, "가을 숲 산책 1/3편", PlutoTitleFramings.Unknown, "끝까지 보면 보이는 가을 숲의 비밀 1/3편"));

        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('k', 0), KoChannel, "끝까지 보면 보이는 가을 숲의 비밀 1/3편", 5000, 300, 40) };
        fake.Uploads[JaChannel] = new List<FakeVideo> { new(Vid('j', 0), JaChannel, "秋の森の散歩 第1/3話", 2200) };
        fake.Uploads[EsChannel] = new List<FakeVideo> { new(Vid('e', 0), EsChannel, "Paseo por el bosque de otoño Episodio 1/3", 900) };
        for (var i = 1; i < 40; i++)
        {
            fake.Uploads[KoChannel].Add(new(Vid('k', i), KoChannel, "zzz unrelated vlog " + i.ToString(CultureInfo.InvariantCulture), 20));
            fake.Uploads[JaChannel].Add(new(Vid('j', i), JaChannel, "qqq other upload " + i.ToString(CultureInfo.InvariantCulture), 20));
            fake.Uploads[EsChannel].Add(new(Vid('e', i), EsChannel, "www misc clip " + i.ToString(CultureInfo.InvariantCulture), 20));
        }
        using var svc = Service(fake, importer, "multi");
        Assert.True((await svc.SaveCredentialsAsync(KeyCreds(new(KoChannel, "ko-KR"), new(JaChannel, "ja-JP"), new(EsChannel, "es-ES")))).Ok);

        var r = await svc.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, r.State);
        Assert.Equal(3, r.ChannelsSynced);
        Assert.Equal(120, r.VideosFetched);
        // 💸 1 channels(3채널 1요청) + 3 playlistItems + ⌈120/50⌉ = 3 videos = 7 unit
        Assert.Equal(1, fake.CountPath("channels"));
        Assert.Equal(3, fake.CountPath("playlistItems"));
        Assert.Equal(3, fake.CountPath("videos"));
        Assert.Equal(7, r.QuotaUnitsUsed);
        foreach (var u in fake.Urls)
        {
            Assert.DoesNotContain(ApiKey, u, StringComparison.Ordinal);   // 🔐 키는 헤더로만
            Assert.Contains("part=", u, StringComparison.Ordinal);
        }
        Assert.All(fake.Requests, q => Assert.True(q.Headers.Contains("X-Goog-Api-Key")));

        Assert.NotNull(r.Import);
        Assert.Equal(3, r.Import!.ApiObservedCount);
        Assert.Equal(0, loop.PerformanceCalls + loop.MaturityCalls);   // 관측 전용 — 학습 0
        var jaClip = await catalog.FindByYouTubeVideoIdAsync(Vid('j', 0));
        var esClip = await catalog.FindByYouTubeVideoIdAsync(Vid('e', 0));
        Assert.Equal(ja!.VideoId, jaClip?.VideoId);
        Assert.Equal(es!.VideoId, esClip?.VideoId);
        Assert.Equal(2200, jaClip!.YouTubeApiStats?.Views);
        Assert.Null(jaClip.YouTubeWatermark);
        Assert.False(jaClip.TelemetryLogged);

        var master = await catalog.FindByYouTubeVideoIdAsync(Vid('k', 0));
        Assert.Equal("B", master!.SelectedTitleVariant);   // 실제 게시 제목 = 반대 변형 → 교정 귀속
        Assert.Equal(PlutoTitleAttributionStates.Confirmed, master.TitleAttribution);
        Assert.Equal(5000, master.YouTubeApiStats!.Views);
        Assert.Equal(300, master.YouTubeApiStats.Likes);
        Assert.Equal(40, master.YouTubeApiStats.Comments);
        Assert.Equal(400, master.YouTubeWatermark!.Views);   // Studio 워터마크 불변

        // 📈 지연된 Studio 내보내기(조회수 600 → 400 대비 비성숙) 도착 → 신선한 API 5000회로 성숙 판정 → 1단계 증분
        var studio = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('k', 0), "끝까지 보면 보이는 가을 숲의 비밀 1/3편", 600, 62, 7.4, 71), "Table data.csv");
        Assert.Equal(1, studio.MaturityDeltaCount);
        Assert.Equal(1, loop.MaturityCalls);
        var after = await catalog.FindByYouTubeVideoIdAsync(Vid('k', 0));
        Assert.Equal(1, after!.YouTubeWatermark!.Stage);
        Assert.Equal(5000, after.YouTubeWatermark.Views);
        Assert.Equal(71, after.YouTubeWatermark.HookPct, 3);
    }

    // ── 3. 비파괴 융합 — API 는 Studio 유지율/CTR 을 지우지 않고, Studio 는 API 연결 클립을 정밀 지표로 보강 ──

    [Fact]
    public async Task ApiAndStudioFusion_IsNonDestructiveInBothDirections()
    {
        var catalog = Catalog("fusion");
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        await catalog.RecordRenderedClipAsync(Clip("studio_first", "단풍 터널 드라이브"));
        await catalog.RecordRenderedClipAsync(Clip("api_first", "첫눈 오는 골목 산책"));

        // ① Studio 먼저 — 정밀 지표 학습(3초 유지 61.5 · CTR 7.2)
        var s1 = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('s', 1), "단풍 터널 드라이브", 800, 55, 7.2, 61.5), "Table data.csv");
        Assert.Equal(1, s1.MatchedAndLearnedCount);
        var before = await catalog.FindByYouTubeVideoIdAsync(Vid('s', 1));
        Assert.NotNull(before!.YouTubeWatermark);

        // ② API 동기화 — 조회수만 갱신 · 워터마크(61.5/7.2) · 학습 표시 그대로 · 추가 학습 없음
        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('s', 1), KoChannel, "단풍 터널 드라이브", 950, 70, 9), new(Vid('a', 1), KoChannel, "첫눈 오는 골목 산책", 1200, 88, 12) };
        using var svc = Service(fake, importer, "fusion");
        Assert.True((await svc.SaveCredentialsAsync(KeyCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var callsBefore = loop.PerformanceCalls;
        var r = await svc.SyncNowAsync();
        Assert.Equal(2, r.Import!.ApiObservedCount);
        Assert.Equal(callsBefore, loop.PerformanceCalls);
        var fused = await catalog.FindByYouTubeVideoIdAsync(Vid('s', 1));
        Assert.Equal(before.YouTubeWatermark, fused!.YouTubeWatermark);
        Assert.Equal(61.5, fused.YouTubeWatermark!.HookPct, 3);
        Assert.Equal(7.2, fused.YouTubeWatermark.CtrPct, 3);
        Assert.True(fused.TelemetryLogged);
        Assert.Equal(950, fused.YouTubeApiStats!.Views);

        // API 전용 클립 — 연결 · 관측만(워터마크 없음 · 미학습 유지 → 정밀 지표 대기)
        var apiOnly = await catalog.FindByYouTubeVideoIdAsync(Vid('a', 1));
        Assert.Equal("api_first", apiOnly!.VideoId);
        Assert.Null(apiOnly.YouTubeWatermark);
        Assert.False(apiOnly.TelemetryLogged);

        // ③ 나중에 Studio 내보내기 — 제목이 바뀌었어도 API 가 영속한 ID 로 Tier 1 연결 → 정밀 지표 최초 학습 · API 관측 보존
        var s2 = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('a', 1), "완전히 다른 제목으로 수정됨", 1300, 48, 6.1, 58), "Table data.csv");
        Assert.Equal(1, s2.MatchedAndLearnedCount);
        var enriched = await catalog.FindByYouTubeVideoIdAsync(Vid('a', 1));
        Assert.Equal(58, enriched!.YouTubeWatermark!.HookPct, 3);
        Assert.Equal(6.1, enriched.YouTubeWatermark.CtrPct, 3);
        Assert.True(enriched.TelemetryLogged);
        Assert.Equal(1200, enriched.YouTubeApiStats!.Views);

        // 디스크 왕복 — API 관측 차선 영속
        var reloaded = await new PlutoRenderCatalogService(Path.Combine(_dir, "fusion_catalog.json")).FindByYouTubeVideoIdAsync(Vid('a', 1));
        Assert.Equal(1200, reloaded!.YouTubeApiStats!.Views);
        Assert.Equal(88, reloaded.YouTubeApiStats.Likes);
    }

    // ── 4. 429 Retry-After · quotaExceeded 냉각 · 무네트워크 격리 ──

    [Fact]
    public async Task RateLimitAndQuota_BackOffCleanly_AndUnconfiguredOrIsolatedMakesZeroNetworkCalls()
    {
        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('q', 1), KoChannel, "쿼터 테스트 영상", 100) };
        fake.Overrides.Add(("playlistItems", () =>
        {
            var m = FakeYouTube.Json("{\"error\":{\"code\":429,\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}", (HttpStatusCode)429);
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return m;
        }));
        fake.Overrides.Add(("videos", () => FakeYouTube.Json("{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"quotaExceeded\",\"domain\":\"youtube.quota\"}]}}", HttpStatusCode.Forbidden)));
        var delays = new List<TimeSpan>();
        using var svc = Service(fake, null, "quota", delays);
        Assert.True((await svc.SaveCredentialsAsync(KeyCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);

        var r = await svc.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.QuotaCooldown, r.State);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, delays);   // Retry-After 준수 · quotaExceeded 는 재시도하지 않음
        Assert.Equal(1, r.Retries);
        var status = svc.GetStatus();
        Assert.Equal(PlutoYouTubeApiSyncService.NextQuotaResetUtc(T0.AddDays(1)), status.CooldownUntilUtc);
        var attempts = svc.NetworkAttemptCount;
        var again = await svc.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.QuotaCooldown, again.State);
        Assert.Equal(attempts, svc.NetworkAttemptCount);   // 냉각 중 → 요청 0

        // 미설정 → 0
        var idle = new FakeYouTube();
        using var unset = Service(idle, null, "unset");
        Assert.Equal(PlutoYouTubeApiSyncState.NotConfigured, (await unset.SyncNowAsync()).State);
        Assert.Equal(0, unset.NetworkAttemptCount);
        Assert.Empty(idle.Requests);

        // 실 네트워크(주입 전송 없음) + 하네스 PROMPTFLOW_PLUTO_DRIVE=off → 0
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        using var real = new PlutoYouTubeApiSyncService(null, null, Sub("quota"));
        Assert.True(real.IsConfigured);
        Assert.False(real.IsNetworkAllowed);
        Assert.Equal(PlutoYouTubeApiSyncState.Disabled, (await real.SyncNowAsync()).State);
        Assert.Equal(PlutoYouTubeApiSyncState.Disabled, (await real.TestConnectionAsync()).State);
        Assert.Equal(0, real.NetworkAttemptCount);

        // 킬 스위치 → 주입 전송까지 차단
        var killed = new FakeYouTube();
        using var svcK = Service(killed, null, "kill");
        Assert.True((await svcK.SaveCredentialsAsync(KeyCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, "1");
        try
        {
            Assert.Equal(PlutoYouTubeApiSyncState.Disabled, (await svcK.SyncNowAsync()).State);
            Assert.Empty(killed.Requests);
        }
        finally { Environment.SetEnvironmentVariable(PlutoYouTubeApiSyncService.DisableEnvVar, null); }
    }

    // ── 5. 🛡️ 자체 추가 A — 가짜 지표 차단(API 행은 폐루프 · 워터마크 · 미매칭 대기열에 절대 진입 금지) ──

    [Fact]
    public async Task SelfAddition_PhantomMetricGuard_ApiRowsNeverReachLoopWatermarkOrUnmatchedQueue()
    {
        var catalog = Catalog("phantom");
        var loop = new CountingLoop();
        await catalog.RecordRenderedClipAsync(Clip("ph", "비 오는 날 카페 창가"));
        var clip = (await catalog.FindByVideoIdOrTitleAsync("ph"))!;
        var apiRow = new YouTubeAnalyticsRow(Vid('p', 1), "비 오는 날 카페 창가", T0, 5000, 0, true, 0, 0, 0, true) { HasPrecisionMetrics = false, Likes = 10 };

        var applied = await PlutoTelemetryBatchImporter.ApplyYouTubeRowAsync(catalog, loop, clip, apiRow, "YouTube Shorts", default);
        Assert.Equal(PlutoYouTubeRowApplyKind.Observed, applied.Kind);
        Assert.Equal(0, loop.PerformanceCalls);
        var after = (await catalog.FindByVideoIdOrTitleAsync("ph"))!;
        Assert.Null(after.YouTubeWatermark);
        Assert.False(after.TelemetryLogged);
        Assert.Equal(5000, after.YouTubeApiStats!.Views);

        // 호출자가 플래그를 빠뜨려도 API 경로는 관측 전용으로 강제
        var importer = new PlutoTelemetryBatchImporter(catalog, loop);
        var forced = await importer.ImportYouTubeApiRowsAsync(new[] { apiRow with { HasPrecisionMetrics = true, Views = 6000 } });
        Assert.Equal(1, forced.ApiObservedCount);
        Assert.Equal(0, loop.PerformanceCalls);

        // 미매칭 대기열 — 디스크 왕복 시 플래그가 사라지므로 API 행 보존 자체를 거부
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "phantom_nb.json"));
        using var realLoop = new PlutoClosedLoopReinforcementService(null, nb, null);
        using var queue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "phantom_queue.json"), catalog, realLoop);
        var orphan = new YouTubeAnalyticsRow(Vid('o', 1), "카탈로그에 없는 영상", T0, 900, 0, true, 0, 0, 0, true) { HasPrecisionMetrics = false };
        Assert.Equal(0, await queue.EnqueueAsync(new[] { new PlutoUnmatchedYouTubeRow(orphan, "test", "api", T0) }));
        Assert.Equal(0, queue.Count);

        // API 동기화의 미매칭 행도 대기열로 새지 않음
        var withQueue = new PlutoTelemetryBatchImporter(catalog, loop, queue);
        var r = await withQueue.ImportYouTubeApiRowsAsync(new[] { orphan });
        Assert.Equal(1, r.UnmatchedCount);
        Assert.Equal(0, queue.Count);
    }

    // ── 6. 🛡️ 자체 추가 B — 워터마크 오염 방지 + API 확인 성숙(지연된 Studio 조회수 보정) 순수 규칙 ──

    [Fact]
    public async Task SelfAddition_ApiViewsNeverAdvanceStudioWatermark_AndOnlyFreshApiConfirmsMaturity()
    {
        var now = T0;
        var fresh = new PlutoYouTubeApiStats(5000, 1, 1, T0, now.AddHours(-10));
        var stale = fresh with { FetchedUtc = now.AddHours(-(PlutoTelemetryBatchImporter.ApiMaturityFreshnessHours + 1)) };
        var future = fresh with { FetchedUtc = now.AddHours(5) };
        Assert.Equal(5000, PlutoTelemetryBatchImporter.EffectiveMaturityViews(600, fresh, now));
        Assert.Equal(600, PlutoTelemetryBatchImporter.EffectiveMaturityViews(600, stale, now));
        Assert.Equal(600, PlutoTelemetryBatchImporter.EffectiveMaturityViews(600, future, now));
        Assert.Equal(7000, PlutoTelemetryBatchImporter.EffectiveMaturityViews(7000, fresh, now));   // Studio 가 더 크면 Studio
        Assert.Equal(600, PlutoTelemetryBatchImporter.EffectiveMaturityViews(600, null, now));
        Assert.False(PlutoTelemetryBatchImporter.IsMaterialMaturity(400, 600));   // API 없으면 이 Studio 스냅샷은 영영 생략됨

        // API 관측 차선은 단조(되감기 거부) · 워터마크와 독립
        var catalog = Catalog("lane");
        await catalog.RecordRenderedClipAsync(Clip("lane", "노을 지는 한강 공원"));
        await catalog.UpdateYouTubeWatermarkAsync("lane", new PlutoYouTubeIngestWatermark(800, 0, 60, 45, 6, T0));
        Assert.True(await catalog.UpdateYouTubeApiStatsAsync("lane", new PlutoYouTubeApiStats(90_000, 10, 2, T0, T0)));
        Assert.False(await catalog.UpdateYouTubeApiStatsAsync("lane", new PlutoYouTubeApiStats(1_000, 10, 2, T0, T0.AddHours(1))));
        var c = (await catalog.FindByVideoIdOrTitleAsync("lane"))!;
        Assert.Equal(90_000, c.YouTubeApiStats!.Views);
        Assert.Equal(800, c.YouTubeWatermark!.Views);   // 9만 회 API 관측도 Studio 워터마크를 앞당기지 않음 → 다음 Studio 스냅샷이 '되감기'로 거부되지 않음
        Assert.True(PlutoRenderCatalogService.WatermarkSupersedes(new PlutoYouTubeIngestWatermark(1_700, 1, 0, 0, 0, T0), c.YouTubeWatermark));

        // 멀티 PC 병합 — API 관측은 앞선 쪽 채택(워터마크와 별개 규칙)
        var left = "{\"Clips\":[{\"VideoId\":\"lane\",\"RenderedAtUtc\":\"2026-11-20T09:00:00+00:00\",\"YouTubeApiStats\":{\"Views\":500,\"Likes\":1,\"Comments\":0,\"FetchedUtc\":\"2026-11-20T09:00:00+00:00\"}}]}";
        var right = "{\"Clips\":[{\"VideoId\":\"lane\",\"RenderedAtUtc\":\"2026-11-21T09:00:00+00:00\"}]}";
        var merged = PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, left, right);
        Assert.NotNull(merged);
        Assert.Contains("\"Views\":500", merged!, StringComparison.Ordinal);
    }

    // ── 7. 🛡️ 자체 추가 C — API 연결 즉시 미매칭 대기열의 Studio 정밀 지표 회수 ──

    [Fact]
    public async Task SelfAddition_ApiBindingDrainsStrandedStudioRowFromUnmatchedQueue()
    {
        var catalog = Catalog("drain");
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "drain_nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, null);
        using var queue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "drain_queue.json"), catalog, loop);
        var importer = new PlutoTelemetryBatchImporter(catalog, loop, queue);
        await catalog.RecordRenderedClipAsync(Clip("drain_clip", "별이 쏟아지는 캠핑 밤", T0));

        // Studio 내보내기 — 크리에이터가 게시 직전 제목을 완전히 바꿔 자동 연결 실패 → 정밀 지표가 대기열에 묶임
        var studio = await importer.ImportYouTubeStudioFileAsync(StudioCsv(Vid('d', 1), "zzz 전혀 다른 표제", 700, 52, 6.6, 64), "Table data.csv");
        Assert.Equal(1, studio.QueuedUnmatchedCount);
        Assert.Equal(1, queue.Count);

        // API — 같은 영상의 '현재' 제목(카탈로그 제목으로 되돌림) → 정확 연결 → 대기열 정밀 행 즉시 회수 · 학습
        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo> { new(Vid('d', 1), KoChannel, "별이 쏟아지는 캠핑 밤", 760) };
        using var svc = Service(fake, importer, "drain");
        Assert.True((await svc.SaveCredentialsAsync(KeyCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var r = await svc.SyncNowAsync();
        Assert.Equal(1, r.Import!.QueueRecoveredCount);
        Assert.Equal(1, r.Import.MatchedAndLearnedCount);
        Assert.Equal(0, queue.Count);
        var clip = await catalog.FindByYouTubeVideoIdAsync(Vid('d', 1));
        Assert.Equal("drain_clip", clip!.VideoId);
        Assert.Equal(64, clip.YouTubeWatermark!.HookPct, 3);   // Studio 정밀 지표로 학습
        Assert.Equal(6.6, clip.YouTubeWatermark.CtrPct, 3);
        Assert.True(clip.TelemetryLogged);
        Assert.Contains("대기열 정밀 지표 회수", r.Import.SummaryKorean, StringComparison.Ordinal);
    }

    // ── 8. 🛡️ 자체 추가 D — 비밀 누출 · 타 채널 오연결 · 예산 · 단일 실행 ──

    [Fact]
    public async Task SelfAddition_SecretsRedacted_ForeignChannelVideosDropped_BudgetCapsMidSync_SingleFlight()
    {
        // 비밀 가림
        var creds = new PlutoYouTubeApiCredentials { ApiKey = ApiKey, ClientSecret = ClientSecret, RefreshToken = RefreshToken, Channels = new[] { new PlutoYouTubeApiChannel(KoChannel) } };
        var red = PlutoYouTubeApiSyncService.Redact("failed GET ?key=" + ApiKey + " secret " + ClientSecret + " rt " + RefreshToken, creds);
        Assert.DoesNotContain(ApiKey, red, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientSecret, red, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, red, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, creds.ToString(), StringComparison.Ordinal);
        Assert.Equal("••••cdef", PlutoYouTubeApiSyncService.Mask(ApiKey));

        // 채널 입력 스키마 즉시 검증
        var invalid = new List<string>();
        var parsed = PlutoYouTubeApiSyncService.ParseChannelList(KoChannel + "=ko-KR\nUCshort\n" + JaChannel + "=日本\n" + KoChannel, invalid);
        Assert.Single(parsed);
        Assert.Equal(2, invalid.Count);

        // 타 채널 영상(재생목록 오설정) → 소유 채널 가드로 제외
        using (var doc = System.Text.Json.JsonDocument.Parse("{\"items\":[{\"id\":\"" + Vid('x', 1) + "\",\"snippet\":{\"title\":\"남의 영상\",\"channelId\":\"" + EsChannel + "\"},\"statistics\":{\"viewCount\":\"9\"}},"
            + "{\"id\":\"" + Vid('x', 2) + "\",\"snippet\":{\"title\":\"내 영상\",\"channelId\":\"" + KoChannel + "\"},\"statistics\":{\"viewCount\":\"12\"}},"
            + "{\"id\":\"bad\",\"snippet\":{\"title\":\"형식 오류\"},\"statistics\":{\"viewCount\":\"1\"}}]}"))
        {
            var rows = PlutoYouTubeApiSyncService.ParseVideoRows(doc, null, new HashSet<string> { KoChannel });
            Assert.Single(rows);
            Assert.Equal(Vid('x', 2), rows[0].YouTubeVideoId);
            Assert.False(rows[0].HasPrecisionMetrics);
            Assert.Equal(-1, rows[0].Likes);   // 비공개 → -1(0 으로 왜곡 금지)
        }
        using (var bad = System.Text.Json.JsonDocument.Parse("{\"kind\":\"youtube#videoListResponse\"}"))
            Assert.Throws<InvalidDataException>(() => PlutoYouTubeApiSyncService.ParseVideoRows(bad));

        // 예산 3 unit — channels + playlistItems + videos(1) 이후 두 번째 videos 묶음 전에 중단 · 받은 행은 [부분 구제]
        var catalog = Catalog("budget");
        var importer = new PlutoTelemetryBatchImporter(catalog, new CountingLoop());
        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo>();
        for (var i = 0; i < 50; i++) fake.Uploads[KoChannel].Add(new(Vid('b', i), KoChannel, "budget clip " + i.ToString(CultureInfo.InvariantCulture), 30));
        fake.Uploads[JaChannel] = new List<FakeVideo> { new(Vid('b', 99), JaChannel, "budget ja", 30) };
        using var svc = Service(fake, importer, "budget", budget: 4);
        Assert.True((await svc.SaveCredentialsAsync(KeyCreds(new(KoChannel), new(JaChannel)))).Ok);
        var r = await svc.SyncNowAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.BudgetExhausted, r.State);
        Assert.Equal(4, r.QuotaUnitsUsed);
        Assert.Equal(50, r.VideosFetched);
        Assert.Contains("[부분 구제]", r.SummaryKorean, StringComparison.Ordinal);
        Assert.Equal(PlutoYouTubeApiSyncState.BudgetExhausted, (await svc.SyncNowAsync()).State);   // 같은 태평양 일 → 즉시 거부

        // 단일 실행 — 진행 중 두 번째 호출은 Busy(쿼터 이중 소모 차단)
        using var gate = new ManualResetEventSlim(false);
        var slow = new FakeYouTube();
        slow.Uploads[KoChannel] = new List<FakeVideo>();
        slow.Overrides.Add(("channels", () => { gate.Wait(TimeSpan.FromSeconds(10)); return FakeYouTube.Json("{\"items\":[]}"); }));
        using var svcS = Service(slow, null, "single");
        Assert.True((await svcS.SaveCredentialsAsync(KeyCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        var first = Task.Run(() => svcS.SyncNowAsync());
        var spin = DateTime.UtcNow;
        while (slow.Requests.Count == 0 && DateTime.UtcNow - spin < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        var second = await svcS.SyncNowAsync();
        gate.Set();
        await first;
        Assert.Equal(PlutoYouTubeApiSyncState.Busy, second.State);
    }

    // ── 9. 쿼터 일 경계 · 빈 칸 저장 병합 ──

    [Fact]
    public async Task QuotaDayRollsAtPacificMidnight_AndBlankFieldsKeepStoredSecrets()
    {
        var lateUtc = new DateTimeOffset(2026, 11, 23, 7, 59, 0, TimeSpan.Zero);   // PST 23:59
        Assert.Equal("2026-11-22", PlutoYouTubeApiSyncService.QuotaDayKey(lateUtc));
        Assert.Equal("2026-11-23", PlutoYouTubeApiSyncService.QuotaDayKey(lateUtc.AddMinutes(2)));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero), PlutoYouTubeApiSyncService.NextQuotaResetUtc(lateUtc));
        Assert.Equal(TimeSpan.FromSeconds(30), PlutoYouTubeApiSyncService.BackoffDelay(null, 9, T0));   // 지수 상한
        Assert.Equal("quotaExceeded", PlutoYouTubeApiSyncService.ErrorReason("{\"error\":{\"errors\":[{\"reason\":\"quotaExceeded\"}]}}"));

        var fake = new FakeYouTube();
        fake.Uploads[KoChannel] = new List<FakeVideo>();
        fake.Uploads[JaChannel] = new List<FakeVideo>();
        using var svc = Service(fake, null, "merge");
        Assert.True((await svc.SaveCredentialsAsync(KeyCreds(new PlutoYouTubeApiChannel(KoChannel)))).Ok);
        // 설정 화면은 비밀값을 되읽지 않으므로 빈 칸 = 기존 키 유지 · 채널만 교체
        Assert.True((await svc.SaveCredentialsAsync(new PlutoYouTubeApiCredentials { Channels = new[] { new PlutoYouTubeApiChannel(JaChannel, "ja-JP") } })).Ok);
        var r = await svc.TestConnectionAsync();
        Assert.Equal(PlutoYouTubeApiSyncState.Synced, r.State);
        Assert.Equal(1, r.ChannelsSynced);
        Assert.Contains(JaChannel, fake.Urls[^1], StringComparison.Ordinal);
        Assert.Contains(ApiKey, fake.Requests[^1].Headers.GetValues("X-Goog-Api-Key"));
    }
}
