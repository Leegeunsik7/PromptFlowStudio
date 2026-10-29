using System.Net;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.SocialMedia;

namespace PromptFlowStudio.Tests;

/// <summary>🚀 Instagram Reels 발행 — 컨테이너 페이로드 · 캡션 포맷 · Graph 위임 · 웹훅 브릿지 폴백 · 모의.</summary>
public class InstagramPublishTests
{
    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Calls = new();
        public Func<string, HttpResponseMessage>? Route;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Calls.Add((req.Method, url, req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct)));
            return Route?.Invoke(url) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    private static string TempVideo(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "pfs-ig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var v = Path.Combine(dir, "reel_full.mp4");
        File.WriteAllBytes(v, new byte[64]);
        return v;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task InstagramPublish_GeneratesReelsContainerPayload()
    {
        var beats = new[] { new ScriptBeat("#스포 금지! 이 디저트의 정체는?", 3), new ScriptBeat("겉바속촉 그 자체", 3) };
        var tags = SnsPublishFormatter.BuildTags("편의점 디저트 리뷰", new[] { "먹스타그램", "#디저트" });
        var caption = SnsPublishFormatter.BuildReelsCaption("편의점 디저트 리뷰", beats, tags);

        Assert.StartsWith("편의점 디저트 리뷰\n▸ 스포 금지!", caption);   // 본문 '#' 제거(해시태그 한도 보호)
        Assert.Contains("▸ 겉바속촉 그 자체", caption);
        Assert.EndsWith("#Shorts #AI #먹스타그램 #디저트 #편의점 #리뷰", caption);
        Assert.Null(InstagramReelsService.ValidateCaption(caption));

        // 한도: 2200자 · 해시태그 30개
        var huge = SnsPublishFormatter.BuildReelsCaption(new string('a', 5000), null, Enumerable.Range(0, 50).Select(i => "t" + i).ToList());
        Assert.True(huge.Length <= SnsPublishFormatter.MaxCaption);
        Assert.Null(InstagramReelsService.ValidateCaption(huge));

        var form = InstagramReelsPublisher.BuildContainerPayload("https://cdn.example.com/r.mp4", caption);
        Assert.Equal("REELS", form["media_type"]);
        Assert.Equal("https://cdn.example.com/r.mp4", form["video_url"]);
        Assert.Equal("true", form["share_to_feed"]);
        Assert.Equal(caption, form["caption"]);

        // Graph 경로 — 설정 저장소 자격이 InstagramReelsService 로 주입되어 컨테이너 → 폴링 → media_publish
        var video = TempVideo(out var dir);
        try
        {
            var g = new FakeHttp
            {
                Route = url => url.EndsWith("/1789/media", StringComparison.Ordinal) ? Json("{\"id\":\"c1\"}")
                    : url.Contains("/c1?fields=", StringComparison.Ordinal) ? Json("{\"status_code\":\"FINISHED\"}")
                    : url.EndsWith("/media_publish", StringComparison.Ordinal) ? Json("{\"id\":\"m9\"}")
                    : new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            using var reels = new InstagramReelsService(handler: g) { PollInterval = TimeSpan.Zero, BackoffBaseSeconds = 0 };
            using var pub = new InstagramReelsPublisher(reels, () => new SnsCredentials("", "", "", "1789", "tokIG", ""));
            Assert.True(pub.Enabled);

            var r = await pub.PublishAsync(video, "https://cdn.example.com/r.mp4", caption, publicConsent: false);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(ReelsRoute.GraphApi, r.Route);
            Assert.Equal("m9", r.MediaId);
            var create = g.Calls.First(c => c.Url.EndsWith("/1789/media", StringComparison.Ordinal));
            Assert.Contains("media_type=REELS", create.Body);
            Assert.Contains("video_url=https%3A%2F%2Fcdn.example.com%2Fr.mp4", create.Body);
            Assert.Contains(g.Calls, c => c.Url.EndsWith("/media_publish", StringComparison.Ordinal) && c.Body.Contains("creation_id=c1"));

            // 로컬 파일 + 동의 없음 → 차단(공개 게시 명시 동의)
            var noConsent = await pub.PublishAsync(video, null, caption, publicConsent: false);
            Assert.False(noConsent.Ok);
            Assert.Equal(ReelsRoute.LocalBridge, noConsent.Route);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task InstagramPublish_DispatchesWebhookBridge_WithoutToken()
    {
        var video = TempVideo(out var dir);
        try
        {
            var calls = 0;
            var hook = new FakeHttp { Route = _ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK) };
            using var reels = new InstagramReelsService(handler: new FakeHttp());
            using var pub = new InstagramReelsPublisher(reels, () => new SnsCredentials("", "", "", "1789", "SECRET_TOKEN", "https://hooks.zapier.com/x"), hook) { BackoffBaseSeconds = 0 };

            var r = await pub.PublishAsync(video, null, "캡션 #AI", publicConsent: false);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(ReelsRoute.Webhook, r.Route);
            Assert.Equal(2, hook.Calls.Count);   // 503 → 백오프 재시도 → 200
            using var doc = JsonDocument.Parse(hook.Calls[^1].Body);
            Assert.Equal(InstagramReelsPublisher.WebhookEvent, doc.RootElement.GetProperty("event").GetString());
            Assert.Equal("REELS", doc.RootElement.GetProperty("media_type").GetString());
            Assert.Equal("reel_full.mp4", doc.RootElement.GetProperty("file_name").GetString());
            Assert.Equal("캡션 #AI", doc.RootElement.GetProperty("caption").GetString());
            Assert.DoesNotContain("SECRET_TOKEN", hook.Calls[^1].Body);

            Assert.False(InstagramReelsPublisher.IsValidWebhookUrl("ftp://x"));
            Assert.False(InstagramReelsPublisher.IsValidWebhookUrl("not a url"));

            // 미구성 → 모의(네트워크 0)
            var none = new FakeHttp();
            using var reels2 = new InstagramReelsService(handler: none);
            using var mock = new InstagramReelsPublisher(reels2, () => SnsCredentials.Empty, none);
            if (!reels2.IsConfigured)   // 개발 PC 환경변수(PFS_IG_*) 가 있으면 모의 경로가 아니므로 건너뜀
            {
                var m = await mock.PublishAsync(video, null, "c", false);
                Assert.True(m.Ok);
                Assert.True(m.IsMock);
                Assert.Equal(ReelsRoute.Mock, m.Route);
                Assert.Empty(none.Calls);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
