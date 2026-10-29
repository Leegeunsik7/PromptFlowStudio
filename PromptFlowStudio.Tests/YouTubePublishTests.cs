using System.Net;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.Publishing;

namespace PromptFlowStudio.Tests;

/// <summary>🚀 YouTube Shorts 발행 — 훅 메타데이터 · resumable 업로드 · captions.insert 바인딩 · 401 토큰 갱신 · 모의 폴백.</summary>
public class YouTubePublishTests
{
    private static readonly SnsCredentials Creds = new("cid", "secret", "refresh", "", "", "");

    /// <summary>가짜 Google 엔드포인트 — 요청(메서드·URL·본문) 기록.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body, string? Auth)> Calls = new();
        public int InitUnauthorizedOnce;
        public int TokenCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            var body = req.Content is null || url.StartsWith("https://upload.session", StringComparison.Ordinal) ? "" : await req.Content.ReadAsStringAsync(ct);
            if (url.StartsWith("https://upload.session", StringComparison.Ordinal) && req.Content is not null)
                await req.Content.CopyToAsync(Stream.Null, ct);   // 스트리밍 PUT 소비(진행률 펌프)
            Calls.Add((req.Method, url, body, req.Headers.Authorization?.Parameter));

            if (url == YouTubeShortsPublisher.TokenEndpoint)
            {
                TokenCalls++;
                return Json("{\"access_token\":\"tok" + TokenCalls + "\",\"expires_in\":3600}");
            }
            if (url == YouTubeShortsPublisher.UploadEndpoint)
            {
                if (InitUnauthorizedOnce-- > 0) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Location = new Uri("https://upload.session/abc");
                return r;
            }
            if (url.StartsWith("https://upload.session", StringComparison.Ordinal)) return Json("{\"id\":\"vid123\"}");
            if (url == YouTubeShortsPublisher.CaptionEndpoint) return Json("{\"id\":\"cap1\"}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-yt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task YouTubePublish_FormatsShortsMetadata_AndUploadsVideo()
    {
        var beats = new[] { new ScriptBeat("편의점 신상 디저트, 진짜 맛있을까? 오늘 확인해 봅니다.", 3), new ScriptBeat("한 입 먹자마자 놀라운 반전!", 4.5), new ScriptBeat("결론은 댓글로!", 5) };
        var meta = YouTubeShortsPublisher.BuildMetadata("편의점 신상 디저트, 진짜 맛있을까? 오늘 확인해 봅니다.", beats, "PUBLIC");

        Assert.EndsWith(" #Shorts #AI", meta.Title);
        Assert.StartsWith("편의점 신상 디저트, 진짜 맛있을까?", meta.Title);
        Assert.True(meta.Title.Length <= 100);
        Assert.Equal("public", meta.PrivacyStatus);
        Assert.Equal("Shorts", meta.Tags[0]);
        Assert.Contains("AI", meta.Tags);
        Assert.Contains("0:00 편의점 신상 디저트, 진짜 맛있을까?", meta.Description);
        Assert.Contains("0:03 한 입 먹자마자 놀라운 반전!", meta.Description);
        Assert.Contains("0:07 결론은 댓글로!", meta.Description);
        Assert.Contains("#Shorts", meta.Description);

        // 긴 훅 → 100자 이내 절단 + 접미 유지
        var longTitle = SnsPublishFormatter.BuildTitle(new string('가', 300));
        Assert.Equal(100, longTitle.Length);
        Assert.EndsWith(" #Shorts #AI", longTitle);
        Assert.Equal("private", SnsPublishFormatter.NormalizePrivacy("weird"));

        var dir = TempDir();
        try
        {
            var video = Path.Combine(dir, "demo_full.mp4");
            File.WriteAllBytes(video, new byte[300_000]);
            var g = new FakeGoogle();
            using var yt = new YouTubeShortsPublisher(() => Creds, "22", g) { BackoffBaseSeconds = 0 };
            Assert.True(yt.Enabled);

            var reported = new List<double>();
            var r = await yt.PublishAsync(video, meta with { PrivacyStatus = "unlisted" }, progress: new SyncProgress(reported.Add));

            Assert.True(r.Ok, r.Message);
            Assert.False(r.IsMock);
            Assert.Equal("vid123", r.VideoId);
            Assert.Equal("https://youtube.com/shorts/vid123", r.Url);
            Assert.False(r.CaptionUploaded);   // srt 없음 → 캡션 요청 0
            Assert.DoesNotContain(g.Calls, c => c.Url == YouTubeShortsPublisher.CaptionEndpoint);
            Assert.Equal(1.0, reported[^1]);

            var token = g.Calls.Single(c => c.Url == YouTubeShortsPublisher.TokenEndpoint);
            Assert.Contains("grant_type=refresh_token", token.Body);
            Assert.Contains("refresh_token=refresh", token.Body);

            var init = g.Calls.Single(c => c.Url == YouTubeShortsPublisher.UploadEndpoint);
            Assert.Equal("tok1", init.Auth);
            using var doc = JsonDocument.Parse(init.Body);
            var sn = doc.RootElement.GetProperty("snippet");
            Assert.Equal(meta.Title, sn.GetProperty("title").GetString());
            Assert.Equal("22", sn.GetProperty("categoryId").GetString());
            Assert.Contains(sn.GetProperty("tags").EnumerateArray(), t => t.GetString() == "Shorts");
            Assert.Equal("unlisted", doc.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString());

            // 토큰 캐시 재사용 — 두 번째 업로드는 토큰 재발급 없음
            var r2 = await yt.PublishAsync(video, meta);
            Assert.True(r2.Ok);
            Assert.Equal(1, g.TokenCalls);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task YouTubePublish_UploadsSubtitles_WhenSrtExists()
    {
        var dir = TempDir();
        try
        {
            var video = Path.Combine(dir, "baby_full.mp4");
            File.WriteAllBytes(video, new byte[4096]);
            File.WriteAllText(Path.Combine(dir, "baby_full.srt"), "1\n00:00:00,000 --> 00:00:03,000\n안녕하세요 아기입니다\n");
            Assert.Equal(Path.Combine(dir, "baby_full.srt"), YouTubeShortsPublisher.ResolveSrtPath(video));

            var g = new FakeGoogle { InitUnauthorizedOnce = 1 };   // 첫 세션 401 → 토큰 강제 갱신 재시도
            using var yt = new YouTubeShortsPublisher(() => Creds, "22", g) { BackoffBaseSeconds = 0 };
            var meta = YouTubeShortsPublisher.BuildMetadata("아기 첫 웃음", null, "private", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
            var r = await yt.PublishAsync(video, meta);

            Assert.True(r.Ok, r.Message);
            Assert.True(r.CaptionUploaded);
            Assert.Equal(2, g.TokenCalls);

            var init = g.Calls.Last(c => c.Url == YouTubeShortsPublisher.UploadEndpoint);
            using (var doc = JsonDocument.Parse(init.Body))
            {
                var st = doc.RootElement.GetProperty("status");
                Assert.Equal("private", st.GetProperty("privacyStatus").GetString());
                Assert.Equal("2026-11-01T09:00:00Z", st.GetProperty("publishAt").GetString());
            }

            var cap = g.Calls.Single(c => c.Url == YouTubeShortsPublisher.CaptionEndpoint);
            Assert.Equal(HttpMethod.Post, cap.Method);
            Assert.Equal("tok2", cap.Auth);
            Assert.Contains("\"videoId\":\"vid123\"", cap.Body);
            Assert.Contains("\"language\":\"ko\"", cap.Body);
            Assert.Contains("안녕하세요 아기입니다", cap.Body);
            Assert.Equal("en", YouTubeShortsPublisher.DetectCaptionLanguage("Hello world"));

            // _full 접미 제거 폴백: {project}.srt
            File.Delete(Path.Combine(dir, "baby_full.srt"));
            File.WriteAllText(Path.Combine(dir, "baby.srt"), "x");
            Assert.Equal(Path.Combine(dir, "baby.srt"), YouTubeShortsPublisher.ResolveSrtPath(video));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task YouTubePublish_FallsBackToMock_WhenNotConfigured()
    {
        var dir = TempDir();
        try
        {
            var video = Path.Combine(dir, "v.mp4");
            File.WriteAllBytes(video, new byte[16]);
            var g = new FakeGoogle();
            using var yt = new YouTubeShortsPublisher(() => SnsCredentials.Empty, "22", g);
            Assert.False(yt.Enabled);
            var r = await yt.PublishAsync(video, YouTubeShortsPublisher.BuildMetadata("hook", null));
            Assert.True(r.Ok);
            Assert.True(r.IsMock);
            Assert.StartsWith("mock-", r.VideoId);
            Assert.Empty(g.Calls);   // 네트워크 0
            Assert.False(await yt.TestConnectionAsync());

            var missing = await yt.PublishAsync(Path.Combine(dir, "none.mp4"), YouTubeShortsPublisher.BuildMetadata("hook", null));
            Assert.False(missing.Ok);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private sealed class SyncProgress(Action<double> a) : IProgress<double> { public void Report(double v) => a(v); }
}
