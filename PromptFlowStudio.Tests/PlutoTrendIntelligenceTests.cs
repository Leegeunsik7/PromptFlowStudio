using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🪐 플루토 트렌드 랩 — 4대 테마 큐레이션 · 자가 학습 영속 · SNS 패스트트랙 프리셋.</summary>
public class PlutoTrendIntelligenceTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoTrendEngine_ReturnsMultiThemeTrends_WithAccuratePlatformTags()
    {
        var engine = new PlutoTrendEngine();
        Assert.Equal(4, PlutoTrendEngine.Categories.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cat in PlutoTrendEngine.Categories)
        {
            var trends = await engine.GetCuratedTrendsAsync(cat);
            Assert.InRange(trends.Count, 3, 4);
            foreach (var t in trends)
            {
                Assert.Equal(cat, t.Category);
                Assert.True(ids.Add(t.Id), "중복 트렌드 Id: " + t.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.Title));
                Assert.False(string.IsNullOrWhiteSpace(t.ViralHook));
                Assert.True(t.VideoPrompt.Length >= 20, "프롬프트가 너무 짧음: " + t.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.CameraMotion));
                Assert.False(string.IsNullOrWhiteSpace(t.BgmStyle));
                Assert.NotEqual(SocialPlatform.None, t.Platforms);
                Assert.Contains("#Shorts", t.YouTubeHashtags);
                Assert.Contains("#Reels", t.InstagramHashtags);
                Assert.All(t.YouTubeHashtags, h => Assert.StartsWith("#", h, StringComparison.Ordinal));
                Assert.All(t.InstagramHashtags, h => Assert.StartsWith("#", h, StringComparison.Ordinal));

                var copy = t.BuildCopyText();
                Assert.StartsWith(t.ViralHook, copy, StringComparison.Ordinal);
                if ((t.Platforms & SocialPlatform.YouTubeShorts) != 0) Assert.Contains("#Shorts", copy, StringComparison.Ordinal);
                else Assert.DoesNotContain("#Shorts", copy, StringComparison.Ordinal);
                if ((t.Platforms & SocialPlatform.InstagramReels) != 0) Assert.Contains("#Reels", copy, StringComparison.Ordinal);
                else Assert.DoesNotContain("#Reels", copy, StringComparison.Ordinal);
            }
        }
        Assert.False(string.IsNullOrEmpty(PlutoTrendEngine.Label(StudioThemeCategory.PetsCompanions)));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoLearningService_AdaptsWeights_BasedOnUserInteractions()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, PlutoLearningService.FileName);
            const string id = "pet-sleepy-cat";
            using (var svc = new PlutoLearningService(path, new Random(1)))
            {
                Assert.Equal(0, svc.GetWeight(id));
                var w1 = await svc.RecordInteractionAsync(id, InteractionType.Adopted);
                Assert.Equal(0.05, w1, 6);
                var w2 = await svc.RecordInteractionAsync(id, InteractionType.ExportCompleted);
                Assert.Equal(0.20, w2, 6);
                var w3 = await svc.RecordInteractionAsync("pet-treat-begging", InteractionType.ThumbsDown);
                Assert.Equal(-0.10, w3, 6);
                Assert.Equal(2, svc.GetInteractions(id));
            }
            Assert.True(File.Exists(path), "pluto_trend_memory.json 미생성");

            // 재시작 후 영속 복원 + 80% 활용 순위(가중치 최고 카드 1위 · 감점 카드 최하위 근처)
            using var reload = new PlutoLearningService(path, new Random(1));
            await reload.EnsureLoadedAsync();
            Assert.Equal(0.20, reload.GetWeight(id), 6);
            Assert.Equal(-0.10, reload.GetWeight("pet-treat-begging"), 6);

            var engine = new PlutoTrendEngine(reload);
            var exploitFirst = 0;
            for (var i = 0; i < 50; i++)
            {
                var ranked = await engine.GetCuratedTrendsAsync(StudioThemeCategory.PetsCompanions);
                Assert.Equal(4, ranked.Count);
                if (ranked[0].Id == id) exploitFirst++;
            }
            Assert.Equal(50, exploitFirst);   // 탐색은 2번째 슬롯만 교체 — 1위는 항상 활용

            // 탐색 확률 ≈ 20%: 최소 상호작용 카드가 2번째 슬롯으로 승격되는 비율
            var list = await new PlutoTrendEngine().GetCuratedTrendsAsync(StudioThemeCategory.PetsCompanions);
            var explored = 0;
            for (var i = 0; i < 2000; i++)
            {
                var r = reload.Rank(list);
                Assert.Equal(id, r[0].Id);
                if (r[1].Id == "pet-sunlight-nap") explored++;   // 활용 2위 = zoomies · 탐색 = 미노출 sunlight-nap
            }
            Assert.InRange(explored, 250, 550);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PublishModal_AppliesSocialPresets_YouTubeAndInstagram()
    {
        var dir = TempDir();
        try
        {
            var v = Path.Combine(dir, "pluto_full.mp4");
            File.WriteAllBytes(v, new byte[32]);
            using var vm = new PublishViewModel(new NullYt(), new NullIg());
            vm.OpenForMedia(v, "sleepy cat", null, "졸린 고양이 브이로그");

            vm.ApplySocialPreset(SocialPresetKind.YouTubeShorts);
            Assert.Equal(1080, vm.OutputWidth);
            Assert.Equal(1920, vm.OutputHeight);
            Assert.True(vm.ActiveSocialPreset!.IsVertical916);
            Assert.Equal(60, vm.OutputFps);
            Assert.Equal(-14.0, vm.TargetLoudnessLufs);
            Assert.True(vm.StockSuiteSmooth60);
            Assert.True(vm.ToYouTube);
            Assert.False(vm.ToInstagram);
            Assert.Contains("#Shorts", vm.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Shorts", vm.TagsText, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(vm.Title.Length, 1, PromptFlowStudio.Services.AI.GeminiPromptService.MaxSnsTitle);

            vm.ApplySocialPreset(SocialPresetKind.YouTubeShorts);   // 재적용 — 해시태그 중복 금지
            Assert.Equal(vm.Title.IndexOf("#Shorts", StringComparison.Ordinal), vm.Title.LastIndexOf("#Shorts", StringComparison.Ordinal));

            vm.ApplySocialPreset(SocialPresetKind.InstagramReels);
            Assert.Equal(1080, vm.OutputWidth);
            Assert.Equal(1920, vm.OutputHeight);
            Assert.Equal(30, vm.OutputFps);
            Assert.Null(vm.TargetLoudnessLufs);
            Assert.True(vm.SoftFilmGrain);
            Assert.False(vm.ToYouTube);
            Assert.True(vm.ToInstagram);
            Assert.Contains("Reels", vm.TagsText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("#Reels", vm.Caption, StringComparison.Ordinal);

            var copy = vm.BuildTitleAndTagsCopyText();
            Assert.StartsWith(vm.Title, copy, StringComparison.Ordinal);
            Assert.Contains("#Reels", copy, StringComparison.OrdinalIgnoreCase);
            Assert.False(vm.CopyTitleAndTags());   // 클립보드 미주입 → false(예외 없음)
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoLearningService_RecordsExportCompleted_WhenExportSucceedsWithActiveTrend()
    {
        var dir = TempDir();
        try
        {
            const string id = "baby-first-steps";
            using var svc = new PlutoLearningService(Path.Combine(dir, PlutoLearningService.FileName), new Random(3));
            using var vm = new PublishViewModel(new NullYt(), new NullIg(), learning: svc);
            Assert.False(await vm.RecordPlutoExportAsync());   // 활성 트렌드 없음 → 기록 없음
            Assert.Equal(0, svc.CurrentXP);

            vm.ActivePlutoTrendId = id;
            Assert.True(await vm.RecordPlutoExportAsync());
            Assert.Equal(0.15, svc.GetWeight(id), 6);
            Assert.Equal(PlutoLearningService.ExportXp, svc.CurrentXP);
            Assert.Null(vm.ActivePlutoTrendId);   // 소거 → 중복 보상 차단
            Assert.False(await vm.RecordPlutoExportAsync());
            Assert.Equal(0.15, svc.GetWeight(id), 6);

            using var vm2 = new PublishViewModel(new NullYt(), new NullIg());   // 학습 서비스 미주입 → 안전 no-op
            vm2.ActivePlutoTrendId = id;
            Assert.False(await vm2.RecordPlutoExportAsync());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void StockSuiteExporter_AppliesSocialFilters_YouTubeLoudnormAndReelsGrain()
    {
        var src = Path.Combine(Path.GetTempPath(), "pfs-src.mp4");
        var yt = PromptFlowStudio.Services.Export.StockSuiteExporter.BuildSocialArgs(src, "out_shorts.mp4", SocialExportPreset.YouTubeShorts);
        var yaf = Array.IndexOf(yt, "-af");
        Assert.True(yaf > 0);
        Assert.Equal("loudnorm=I=-14:LRA=7:TP=-1.5", yt[yaf + 1]);
        Assert.DoesNotContain(yt, a => a.Contains("noise=", StringComparison.Ordinal));
        Assert.Equal("60", yt[Array.IndexOf(yt, "-r") + 1]);

        var ig = PromptFlowStudio.Services.Export.StockSuiteExporter.BuildSocialArgs(src, "out_reels.mp4", SocialExportPreset.InstagramReels);
        Assert.EndsWith(",noise=alls=8:allf=t+u", ig[Array.IndexOf(ig, "-vf") + 1], StringComparison.Ordinal);
        Assert.Equal("30", ig[Array.IndexOf(ig, "-r") + 1]);
        Assert.DoesNotContain(ig, a => a.Contains("loudnorm", StringComparison.Ordinal));

        // 📦 멀티 패키지 쇼츠 규격에도 동일 실필터 결합
        var opts = new PromptFlowStudio.Services.Export.MultiExportOptions { Social = SocialExportPreset.YouTubeShorts, DurationSec = 5 };
        var shorts = PromptFlowStudio.Services.Export.StockSuiteExporter.BuildMultiTargetProfiles(src, opts, Path.GetTempPath(), "bgm.wav")[2].Args;
        Assert.Contains(shorts, a => a.StartsWith("afade=", StringComparison.Ordinal) && a.EndsWith(",loudnorm=I=-14:LRA=7:TP=-1.5", StringComparison.Ordinal));
        var reels = PromptFlowStudio.Services.Export.StockSuiteExporter.BuildMultiTargetProfiles(src, opts with { Social = SocialExportPreset.InstagramReels }, Path.GetTempPath(), null)[2].Args;
        Assert.Contains(reels, a => a.EndsWith(",noise=alls=8:allf=t+u", StringComparison.Ordinal));
        Assert.Equal("30", reels[Array.IndexOf(reels, "-r") + 1]);
        var plain = PromptFlowStudio.Services.Export.StockSuiteExporter.BuildMultiTargetProfiles(src, opts with { Social = null }, Path.GetTempPath(), "bgm.wav")[2].Args;
        Assert.DoesNotContain(plain, a => a.Contains("loudnorm", StringComparison.Ordinal) || a.Contains("noise=", StringComparison.Ordinal));
        Assert.DoesNotContain("-r", plain);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoLearningService_CalculatesAccurateLevelAndXp()
    {
        Assert.Equal(0, PlutoLearningService.ComputeXp(0, 0, 0));
        Assert.Equal(1 + 9 + 2, PlutoLearningService.ComputeXp(1, 3, 2));
        Assert.Equal(1, PlutoLearningService.LevelFor(0));
        Assert.Equal(1, PlutoLearningService.LevelFor(9));
        Assert.Equal(2, PlutoLearningService.LevelFor(10));
        Assert.Equal(4, PlutoLearningService.LevelFor(35));

        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, PlutoLearningService.FileName);
            using (var svc = new PlutoLearningService(path, new Random(5)))
            {
                await svc.EnsureLoadedAsync();
                Assert.Equal(1, svc.CurrentLevel);
                Assert.Equal(10, svc.NextLevelXP);
                await svc.RecordInteractionAsync("a", InteractionType.Adopted);          // +1
                for (var i = 0; i < 3; i++) await svc.RecordInteractionAsync("a", InteractionType.ExportCompleted);   // +9
                await svc.RecordInteractionAsync("b", InteractionType.ThumbsUp);         // +1
                await svc.RecordInteractionAsync("b", InteractionType.ThumbsDown);       // +0
                Assert.Equal(11, svc.CurrentXP);
                Assert.Equal(2, svc.CurrentLevel);
                Assert.Equal(20, svc.NextLevelXP);
                Assert.Equal(6, svc.TotalInteractions);
            }
            using (var reload = new PlutoLearningService(path, new Random(5)))   // 영속 → 재시작 후 동일 레벨
            {
                await reload.EnsureLoadedAsync();
                Assert.Equal(11, reload.CurrentXP);
                Assert.Equal(2, reload.CurrentLevel);
                Assert.Equal(6, reload.TotalInteractions);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed class NullYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, null, "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }
}
