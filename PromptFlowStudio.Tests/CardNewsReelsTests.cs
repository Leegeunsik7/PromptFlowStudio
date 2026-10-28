using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.SocialMedia;

namespace PromptFlowStudio.Tests;

/// <summary>v17.30.1: 카드뉴스 9:16 릴스 생성기 + Instagram Reels 발행 — 레이아웃 인자 · HTML 폴백 · Graph API 3단계 · 재시도 회귀 잠금.</summary>
[Trait("Category", "Integration")]
public class CardNewsReelsTests
{
    private static CardNewsDeck Deck(string output, int bodies = 3) => new(
        "AI 가 바꾸는 콘텐츠 제작", "3분 요약",
        Enumerable.Range(1, bodies).Select(i => new CardNewsSlide(CardNewsSlideKind.Body, "포인트 " + i, "본문 내용 " + i)).ToArray(),
        "지금 시작하세요", "저장하고 팔로우", output);

    [Fact]
    public void Validate_RequiresExactlyThreeBodies_AndHexColors()
    {
        Assert.Null(CardNewsReelsGenerator.Validate(Deck("o.mp4")));
        Assert.NotNull(CardNewsReelsGenerator.Validate(Deck("o.mp4", 2)));
        Assert.NotNull(CardNewsReelsGenerator.Validate(Deck("o.mp4") with { Hook = " " }));
        Assert.NotNull(CardNewsReelsGenerator.Validate(Deck("o.mp4") with { Accent = "red" }));
    }

    [Fact]
    public void BuildSlides_HookBodiesCta_Order()
    {
        var s = CardNewsReelsGenerator.BuildSlides(Deck("o.mp4"));
        Assert.Equal(5, s.Count);
        Assert.Equal(CardNewsSlideKind.Hook, s[0].Kind);
        Assert.All(s.Skip(1).Take(3), x => Assert.Equal(CardNewsSlideKind.Body, x.Kind));
        Assert.Equal(CardNewsSlideKind.Cta, s[4].Kind);
        Assert.Equal(20.0, CardNewsReelsGenerator.TotalSeconds(Deck("o.mp4")));
    }

    [Fact]
    public void Wrap_RespectsWidthUnits_AndEllipsizes()
    {
        // 한글 12자 = 24단위 → 22단위 폭이면 2줄
        var w = CardNewsReelsGenerator.Wrap("가나다라마바사아자차카타", 22, 4);
        Assert.Equal(2, w.Split('\n').Length);
        Assert.Equal("aaa bbb\nccc", CardNewsReelsGenerator.Wrap("aaa bbb ccc", 7, 4));
        var cut = CardNewsReelsGenerator.Wrap("a b c d e", 1, 2);
        Assert.Equal("a\nb…", cut);
        Assert.Equal("", CardNewsReelsGenerator.Wrap("  ", 10, 2));
    }

    [Fact]
    public void BuildArgs_Vertical1080x1920_ConcatWithSilentAudio()
    {
        var files = Enumerable.Range(0, 5).Select(i => ($"C:/t{i}.txt", $"C:/b{i}.txt", $"C:/p{i}.txt")).ToArray();
        var args = CardNewsReelsGenerator.BuildArgs(Deck("out.mp4"), files, null);
        Assert.Equal(5, args.Count(a => a.StartsWith("color=c=0x0F172A:s=1080x1920:d=4", StringComparison.Ordinal)));
        Assert.Contains("anullsrc=r=44100:cl=stereo", args);
        var fc = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("concat=n=5:v=1:a=0[out]", fc);
        Assert.Contains("expansion=none", fc);
        Assert.Contains("textfile=C\\:/t0.txt", fc);
        Assert.Contains("+faststart", args);
        Assert.Equal("5:a", args[args.LastIndexOf("-map") + 1]);
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void RenderHtml_EncodesText_AndHasFiveCards()
    {
        var html = CardNewsReelsGenerator.RenderHtml(Deck("o.mp4") with { Hook = "<script>x</script>" });
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Equal(5, html.Split("<section class=\"card").Length - 1);
        Assert.Contains("width:1080px;height:1920px", html);
    }

    [Fact]
    public async Task Generate_WithoutFfmpeg_FallsBackToHtml()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs_cardnews_" + Guid.NewGuid().ToString("N"));
        try
        {
            var gen = new CardNewsReelsGenerator(() => null);
            Assert.False(gen.IsAvailable);
            var r = await gen.GenerateAsync(Deck(Path.Combine(dir, "r.mp4")));
            Assert.False(r.Ok);
            Assert.Null(r.VideoPath);
            Assert.NotNull(r.HtmlPath);
            Assert.True(File.Exists(r.HtmlPath));
            var invalid = await gen.GenerateAsync(Deck(Path.Combine(dir, "x.mp4"), 1));
            Assert.Null(invalid.HtmlPath);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── Instagram Reels ──

    private sealed class FakeGraph : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Auth, string Body)> Calls = new();
        public Func<HttpRequestMessage, int, HttpResponseMessage> Respond = (_, _) => new(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return Respond(request, Calls.Count);
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static InstagramReelsService Svc(FakeGraph g)
    {
        var s = new InstagramReelsService(handler: g) { PollInterval = TimeSpan.Zero, BackoffBaseSeconds = 0, MaxPolls = 5 };
        s.UseOptions(new InstagramReelsOptions("1789", "SECRET_TOKEN"));
        return s;
    }

    [Fact]
    public void Options_MaskToken_AndFormFields()
    {
        var o = new InstagramReelsOptions("u", "SECRET_TOKEN");
        Assert.True(o.IsConfigured);
        Assert.DoesNotContain("SECRET_TOKEN", o.ToString());
        Assert.False(new InstagramReelsOptions("", "t").IsConfigured);

        var f = InstagramReelsService.BuildContainerForm("https://cdn.example.com/a.mp4", "cap", true);
        Assert.Equal("REELS", f["media_type"]);
        Assert.Equal("https://cdn.example.com/a.mp4", f["video_url"]);
        Assert.Equal("true", f["share_to_feed"]);
        Assert.Equal("cap", f["caption"]);
        Assert.Contains("/v21.0/1789/media_publish", InstagramReelsService.PublishEndpoint(o with { UserId = "1789" }));
    }

    [Fact]
    public void Options_ResolveFromConfig_WhenEnvAbsent()
    {
        if (Environment.GetEnvironmentVariable("PFS_IG_USER_ID") is not null || Environment.GetEnvironmentVariable("IG_USER_ID") is not null) return;
        if (Environment.GetEnvironmentVariable("PFS_IG_ACCESS_TOKEN") is not null || Environment.GetEnvironmentVariable("IG_ACCESS_TOKEN") is not null) return;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Instagram:UserId"] = "42", ["Instagram:AccessToken"] = "tok",
        }).Build();
        var o = InstagramReelsOptions.Resolve(cfg);
        Assert.Equal("42", o.UserId);
        Assert.Equal("tok", o.AccessToken);
    }

    [Fact]
    public void Caption_And_Url_Validation()
    {
        Assert.Null(InstagramReelsService.ValidateCaption("hello #a #b"));
        Assert.NotNull(InstagramReelsService.ValidateCaption(new string('x', 2201)));
        Assert.NotNull(InstagramReelsService.ValidateCaption(string.Concat(Enumerable.Repeat("#t ", 31))));
        Assert.True(InstagramReelsService.IsPublicVideoUrl("https://cdn.example.com/v.mp4"));
        Assert.False(InstagramReelsService.IsPublicVideoUrl("http://cdn.example.com/v.mp4"));
        Assert.False(InstagramReelsService.IsPublicVideoUrl("https://localhost/v.mp4"));
        Assert.False(InstagramReelsService.IsPublicVideoUrl("C:\\v.mp4"));
    }

    [Fact]
    public async Task PublishFromUrl_ContainerPollPublish_HappyPath()
    {
        var g = new FakeGraph();
        g.Respond = (req, n) => n switch
        {
            1 => FakeGraph.Json("{\"id\":\"c1\"}"),
            2 => FakeGraph.Json("{\"status_code\":\"IN_PROGRESS\"}"),
            3 => FakeGraph.Json("{\"status_code\":\"FINISHED\"}"),
            _ => FakeGraph.Json("{\"id\":\"m1\"}"),
        };
        using var s = Svc(g);
        var stages = new List<ReelsPublishStage>();
        s.OnChange += () => stages.Add(s.Stage);
        s.OnChange += () => throw new InvalidOperationException("구독자 예외 격리");

        var r = await s.PublishFromUrlAsync("https://cdn.example.com/a.mp4", "캡션 #ai");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("c1", r.ContainerId);
        Assert.Equal("m1", r.MediaId);
        Assert.Equal(ReelsPublishStage.Published, s.Stage);
        Assert.Equal(1.0, s.Progress);
        Assert.Equal(4, g.Calls.Count);
        Assert.Equal(HttpMethod.Post, g.Calls[0].Method);
        Assert.EndsWith("/v21.0/1789/media", g.Calls[0].Url);
        Assert.Contains("media_type=REELS", g.Calls[0].Body);
        Assert.Contains("/c1?fields=status_code", g.Calls[1].Url);
        Assert.EndsWith("/1789/media_publish", g.Calls[3].Url);
        Assert.Contains("creation_id=c1", g.Calls[3].Body);
        Assert.All(g.Calls, c =>
        {
            Assert.Equal("Bearer SECRET_TOKEN", c.Auth);
            Assert.DoesNotContain("SECRET_TOKEN", c.Url);
        });
        Assert.Contains(ReelsPublishStage.CreatingContainer, stages);
        Assert.Contains(ReelsPublishStage.Polling, stages);
        Assert.Contains(ReelsPublishStage.Publishing, stages);
    }

    [Fact]
    public async Task PublishFromUrl_ErrorStatus_FailsWithoutPublish()
    {
        var g = new FakeGraph();
        g.Respond = (_, n) => n == 1 ? FakeGraph.Json("{\"id\":\"c9\"}") : FakeGraph.Json("{\"status_code\":\"ERROR\",\"status\":\"bad codec\"}");
        using var s = Svc(g);
        var r = await s.PublishFromUrlAsync("https://cdn.example.com/a.mp4", null);
        Assert.False(r.Ok);
        Assert.Equal(ReelsPublishStage.Failed, r.Stage);
        Assert.Contains("bad codec", r.Message);
        Assert.Equal(2, g.Calls.Count);
    }

    [Fact]
    public async Task PublishFromUrl_RetriesOn429_ThenSucceeds()
    {
        var g = new FakeGraph();
        g.Respond = (_, n) => n switch
        {
            1 => FakeGraph.Json("{\"error\":{\"message\":\"rate\"}}", (HttpStatusCode)429),
            2 => FakeGraph.Json("{\"id\":\"c2\"}"),
            3 => FakeGraph.Json("{\"status_code\":\"FINISHED\"}"),
            _ => FakeGraph.Json("{\"id\":\"m2\"}"),
        };
        using var s = Svc(g);
        var r = await s.PublishFromUrlAsync("https://cdn.example.com/a.mp4", null);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(4, g.Calls.Count);
    }

    [Fact]
    public async Task PublishFromUrl_GraphError_SurfacesMessage()
    {
        var g = new FakeGraph();
        g.Respond = (_, _) => FakeGraph.Json("{\"error\":{\"message\":\"Invalid OAuth access token\"}}", HttpStatusCode.BadRequest);
        using var s = Svc(g);
        var r = await s.PublishFromUrlAsync("https://cdn.example.com/a.mp4", null);
        Assert.False(r.Ok);
        Assert.Contains("Invalid OAuth access token", r.Message);
        Assert.Single(g.Calls);
    }

    [Fact]
    public async Task Publish_Guards_NoHttpWhenUnconfiguredOrInvalid()
    {
        var g = new FakeGraph();
        using var s = new InstagramReelsService(handler: g);
        s.UseOptions(new InstagramReelsOptions("", ""));
        Assert.False((await s.PublishFromUrlAsync("https://cdn.example.com/a.mp4", null)).Ok);
        s.UseOptions(new InstagramReelsOptions("u", "t"));
        Assert.False((await s.PublishFromUrlAsync("http://insecure/a.mp4", null)).Ok);
        Assert.False((await s.PublishFileAsync("Z:/none.mp4", null, true)).Ok);
        Assert.Empty(g.Calls);
        Assert.Equal(ReelsPublishStage.Failed, s.Stage);
    }
}
