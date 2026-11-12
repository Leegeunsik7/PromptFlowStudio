using System.Net;
using System.Net.Http;
using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🌐 Google Trends RSS 실시간 키워드 수집(안전 파싱 · 실패 → 빈 목록) + 수확기 결선 + ☁️ SNS 레이더 Drive 볼트 이관.</summary>
public class PlutoWebTrendFetcherTests
{
    private const string SampleRss =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<rss version=\"2.0\" xmlns:ht=\"https://trends.google.com/trending/rss\"><channel><title>Daily Search Trends</title>" +
        "<item><title>첫눈</title><ht:approx_traffic>20,000+</ht:approx_traffic></item>" +
        "<item><title>  두바이   쫀득 쿠키 </title><ht:approx_traffic>50K+</ht:approx_traffic></item>" +
        "<item><title>첫눈</title><ht:approx_traffic>1,000+</ht:approx_traffic></item>" +
        "<item><title>강아지 챌린지</title><ht:approx_traffic>500+</ht:approx_traffic></item>" +
        "<item><title></title><ht:approx_traffic>9,999,999+</ht:approx_traffic></item>" +
        "<item><title>https://spam.example</title><ht:approx_traffic>100+</ht:approx_traffic></item>" +
        "<item><title>야구 결승</title></item>" +
        "</channel></rss>";

    private static string TempRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Respond { get; set; }
        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(Respond!());
        }
    }

    private sealed class FakeFetcher : IPlutoWebTrendFetcher
    {
        public IReadOnlyList<PlutoLiveTrendKeyword> Result { get; set; } = Array.Empty<PlutoLiveTrendKeyword>();
        public bool Boom { get; set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PlutoLiveTrendKeyword>> FetchLiveTrendingKeywordsAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Boom) throw new InvalidOperationException("boom");
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public void Parse_ExtractsCleanDedupedKeywords_SortedByTraffic()
    {
        var list = PlutoWebTrendFetcher.Parse(SampleRss);
        Assert.Equal(4, list.Count);
        Assert.Equal("두바이 쫀득 쿠키", list[0].Keyword);
        Assert.Equal(50_000, list[0].ApproxTraffic);
        Assert.Equal("첫눈", list[1].Keyword);
        Assert.Equal(20_000, list[1].ApproxTraffic);
        Assert.Equal("강아지 챌린지", list[2].Keyword);
        Assert.Equal("야구 결승", list[3].Keyword);
        Assert.Equal(0, list[3].ApproxTraffic);

        Assert.Equal(2, PlutoWebTrendFetcher.Parse(SampleRss, 2).Count);
        Assert.Empty(PlutoWebTrendFetcher.Parse("<rss><channel><item>"));                 // 손상 XML
        Assert.Empty(PlutoWebTrendFetcher.Parse("<!DOCTYPE x [<!ENTITY a \"b\">]><rss/>"));   // DTD 금지
        Assert.Empty(PlutoWebTrendFetcher.Parse(null));
    }

    [Theory]
    [InlineData("20,000+", 20_000)]
    [InlineData("2K+", 2_000)]
    [InlineData("1.5M+", 1_500_000)]
    [InlineData("500+", 500)]
    [InlineData("", 0)]
    [InlineData("n/a", 0)]
    public void ParseTraffic_HandlesSuffixes(string input, long expected)
        => Assert.Equal(expected, PlutoWebTrendFetcher.ParseTraffic(input));

    [Fact]
    public async Task FetchLiveTrendingKeywordsAsync_ReturnsEmpty_OnHttpErrorOrBadXmlOrNetworkFailure()
    {
        var handler = new StubHandler { Respond = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleRss) } };
        using (var http = new HttpClient(handler))
        using (var ok = new PlutoWebTrendFetcher(http, "https://example.invalid/rss"))
        {
            var list = await ok.FetchLiveTrendingKeywordsAsync();
            Assert.Equal(4, list.Count);
            Assert.NotNull(ok.LastSuccessUtc);

            handler.Respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
            Assert.Empty(await ok.FetchLiveTrendingKeywordsAsync());

            handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not rss") };
            Assert.Empty(await ok.FetchLiveTrendingKeywordsAsync());

            handler.Throw = new HttpRequestException("dns");
            Assert.Empty(await ok.FetchLiveTrendingKeywordsAsync());
        }
    }

    [Fact]
    public void BuildLiveTrendSamples_MapsKeywordsToNicheHooks()
    {
        Assert.Equal(PlutoTrendAnalyzer.LiveTrendKind.Weather, PlutoTrendAnalyzer.ClassifyLiveKeyword("첫눈"));
        Assert.Equal(PlutoTrendAnalyzer.LiveTrendKind.Weather, PlutoTrendAnalyzer.ClassifyLiveKeyword("내일 비"));
        Assert.Equal(PlutoTrendAnalyzer.LiveTrendKind.Snack, PlutoTrendAnalyzer.ClassifyLiveKeyword("두바이 쫀득 쿠키"));
        Assert.Equal(PlutoTrendAnalyzer.LiveTrendKind.Meme, PlutoTrendAnalyzer.ClassifyLiveKeyword("강아지 챌린지"));
        Assert.Equal(PlutoTrendAnalyzer.LiveTrendKind.Lifestyle, PlutoTrendAnalyzer.ClassifyLiveKeyword("비트코인"));   // 단음절 "비" 부분 일치 배제

        var kws = PlutoWebTrendFetcher.Parse(SampleRss);
        var samples = PlutoTrendAnalyzer.BuildLiveTrendSamples(StudioThemeCategory.PetsCompanions, kws);
        Assert.Equal(kws.Count, samples.Count);
        Assert.All(samples, s => Assert.Equal(StudioThemeCategory.PetsCompanions, s.Category));
        Assert.Contains("#반려동물", samples[0].YouTubeHashtags);
        Assert.Contains("POV", samples[2].ViralHook, StringComparison.Ordinal);
        Assert.Empty(PlutoTrendAnalyzer.BuildLiveTrendSamples(StudioThemeCategory.BabyParenting, null));

        // 날씨 키워드 표본 → 골든아워 적중 보너스
        var weather = PlutoTrendAnalyzer.BuildLiveTrendSamples(StudioThemeCategory.BabyParenting, new[] { new PlutoLiveTrendKeyword("첫눈", 1) });
        var baseline = PlutoTrendAnalyzer.Analyze(StudioThemeCategory.BabyParenting);
        var boosted = PlutoTrendAnalyzer.Analyze(StudioThemeCategory.BabyParenting, weather);
        double Score(ViralTrendAnalysis a) { foreach (var t in a.Tokens) if (t.Tag == PlutoTrendAnalyzer.GoldenHourGaze) return t.Score; return 0; }
        Assert.True(Score(boosted) > Score(baseline));
    }

    [Fact]
    public async Task Harvester_UsesLiveKeywords_AndSurvivesFetcherFailure()
    {
        var root = TempRoot();
        try
        {
            var sm = new PlutoDriveStorageManager(Path.Combine(root, "G", "DATA", "Pluto"), Path.Combine(root, "Local"));
            using var learning = new PlutoLearningService(sm, new Random(1));
            var fake = new FakeFetcher { Result = new[] { new PlutoLiveTrendKeyword("첫눈", 20_000), new PlutoLiveTrendKeyword("강아지 챌린지", 500) } };
            using (var harvester = new PlutoAutonomousTrendHarvester(learning, sm, null, webFetcher: fake))
            {
                var r = await harvester.TryHarvestAsync(force: true);
                Assert.True(r.Harvested);
                Assert.Equal(1, fake.Calls);
                Assert.Equal(2, harvester.LastLiveKeywords.Count);
                var json = await sm.ReadAllTextAsync(PlutoAutonomousTrendHarvester.PatternsFileName);
                Assert.Contains("LiveKeywords", json, StringComparison.Ordinal);
                Assert.True(learning.GetActiveViralTrends().Count > 0);
            }

            fake.Boom = true;
            using (var harvester = new PlutoAutonomousTrendHarvester(learning, sm, null, webFetcher: fake))
            {
                var r = await harvester.TryHarvestAsync(force: true);   // 수집기 예외 → 빈 목록 폴백 · 수확 지속
                Assert.True(r.Harvested);
                Assert.Empty(harvester.LastLiveKeywords);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SocialRadar_PersistsViaStorageManager_AndSeedsFromLegacy()
    {
        var root = TempRoot();
        try
        {
            var local = Path.Combine(root, "Local");
            var legacy = Path.Combine(root, "Legacy");
            Directory.CreateDirectory(legacy);
            await File.WriteAllTextAsync(Path.Combine(legacy, PlutoSocialRadarEngine.FileName), "{\"srl-yt-01\":{\"Weight\":3,\"Learned\":1}}");
            var sm = new PlutoDriveStorageManager(Path.Combine(root, "G", "DATA", "Pluto"), local, legacy);

            using (var radar = new PlutoSocialRadarEngine(sm))
            {
                Assert.Equal(Path.Combine(local, PlutoSocialRadarEngine.FileName), radar.StoragePath);
                await radar.EnsureLoadedAsync();
                Assert.Equal(3.0, radar.GetWeight("srl-yt-01"));   // 레거시 1회 시드
                Assert.Equal(4.0, await radar.LearnChannelStyleAsync("srl-yt-01"));
            }
            Assert.True(File.Exists(Path.Combine(legacy, PlutoSocialRadarEngine.FileName)));   // 원본 유지

            using var reloaded = new PlutoSocialRadarEngine(sm);
            await reloaded.EnsureLoadedAsync();
            Assert.Equal(4.0, reloaded.GetWeight("srl-yt-01"));
            Assert.Equal(2, reloaded.GetLearnedCount("srl-yt-01"));
        }
        finally { Cleanup(root); }
    }
}
