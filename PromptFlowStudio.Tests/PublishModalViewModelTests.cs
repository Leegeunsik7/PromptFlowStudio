using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🚀 SNS 발행 모달 뷰모델 — 자동 메타데이터 · 기본 비공개 · 채널 선택 · 예약 · 실패 격리 · 진행률.</summary>
public class PublishModalViewModelTests
{
    private sealed class FakeYt : IYouTubeShortsPublisher
    {
        public bool Enabled => true;
        public ShortsMetadata? LastMeta;
        public string? LastSrt;
        public int Calls;
        public bool Fail;

        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            LastMeta = meta;
            LastSrt = srtPath;
            progress?.Report(0.5);
            return Task.FromResult(Fail ? new ShortsPublishResult(false, false, null, null, false, "HTTP 500")
                                        : new ShortsPublishResult(true, false, "v1", "https://youtube.com/shorts/v1", srtPath is not null, "ok"));
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public string? LastCaption;
        public bool LastConsent;
        public int Calls;

        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            LastCaption = caption;
            LastConsent = publicConsent;
            progress?.Report(1);
            return Task.FromResult(new ReelsPublishOutcome(true, true, ReelsRoute.Mock, "mock-1", "mock"));
        }

        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static string TempVideo(out string dir, bool withSrt)
    {
        dir = Path.Combine(Path.GetTempPath(), "pfs-pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var v = Path.Combine(dir, "shorts_full.mp4");
        File.WriteAllBytes(v, new byte[32]);
        if (withSrt) File.WriteAllText(Path.ChangeExtension(v, ".srt"), "1\n00:00:00,000 --> 00:00:01,000\n안녕\n");
        return v;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PublishModal_OpensWithAutoMetadata_AndPublishesBothChannels()
    {
        var video = TempVideo(out var dir, withSrt: true);
        try
        {
            var yt = new FakeYt();
            var ig = new FakeIg();
            using var vm = new PublishViewModel(yt, ig);
            var changes = 0;
            vm.OnChange += () => changes++;
            vm.OnChange += () => throw new InvalidOperationException("구독자 예외 격리");

            vm.Open(video, "편의점 신상 디저트 리뷰! 끝까지 보세요", new[] { new ScriptBeat("첫 컷 훅", 3), new ScriptBeat("두 번째 컷", 3) });
            Assert.True(vm.IsOpen);
            Assert.Equal("편의점 신상 디저트 리뷰! #Shorts #AI", vm.Title);
            Assert.Contains("0:03 두 번째 컷", vm.Description);
            Assert.StartsWith("Shorts, AI", vm.TagsText);
            Assert.Contains("#Shorts", vm.Caption);
            Assert.Equal("private", vm.Privacy);   // 비공개 권장 기본값
            Assert.Equal(Path.ChangeExtension(video, ".srt"), vm.SrtPath);
            Assert.True(vm.CanStart);

            vm.TagsText = "Shorts, 디저트, #리뷰";
            vm.InstagramConsent = true;
            Assert.True(await vm.StartAsync());

            Assert.False(vm.IsBusy);
            Assert.Equal(1, yt.Calls);
            Assert.Equal(1, ig.Calls);
            Assert.Equal(vm.SrtPath, yt.LastSrt);
            Assert.Equal("private", yt.LastMeta!.PrivacyStatus);
            Assert.Null(yt.LastMeta.PublishAtUtc);
            Assert.Equal(new[] { "Shorts", "AI", "디저트", "리뷰" }, yt.LastMeta.Tags);
            Assert.True(ig.LastConsent);
            Assert.Equal(vm.Caption, ig.LastCaption);
            Assert.True(vm.YouTubeResult!.CaptionUploaded);
            Assert.True(vm.InstagramResult!.IsMock);
            Assert.Equal(1.0, vm.OverallProgress);
            Assert.True(changes > 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PublishModal_ScheduleForcesPrivate_AndIsolatesChannelFailure()
    {
        var video = TempVideo(out var dir, withSrt: false);
        try
        {
            var yt = new FakeYt { Fail = true };
            var ig = new FakeIg();
            using var vm = new PublishViewModel(yt, ig);
            vm.Open(video, "hook", null);
            Assert.Null(vm.SrtPath);

            vm.Privacy = "public";
            vm.Schedule = true;
            vm.ScheduleLocal = new DateTime(2026, 12, 25, 18, 0, 0, DateTimeKind.Local);
            Assert.False(await vm.StartAsync());   // YouTube 실패 → 전체 false

            Assert.Equal("private", yt.LastMeta!.PrivacyStatus);   // 예약 → private 강제
            Assert.Equal(vm.ScheduleLocal.ToUniversalTime(), yt.LastMeta.PublishAtUtc);
            Assert.Equal("HTTP 500", vm.YouTubeStatus);
            Assert.Equal(1, ig.Calls);                               // 한 채널 실패가 다른 채널을 막지 않음
            Assert.True(vm.InstagramResult!.Ok);

            // 채널 선택 해제 → 시작 불가 · 제목 비면 시작 불가
            vm.ToYouTube = false; vm.ToInstagram = false;
            Assert.False(vm.CanStart);
            vm.ToYouTube = true; vm.Title = "  ";
            Assert.False(vm.CanStart);
            Assert.False(await vm.StartAsync());

            vm.Close();
            Assert.False(vm.IsOpen);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
