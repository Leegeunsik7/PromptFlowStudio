using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🪐 플루토 자율 AI 디렉터 — 생성형 트렌드 · 프롬프트 DNA 학습 · 코파일럿 3막 시나리오 · 소셜 카피라이터.</summary>
public class PlutoAutonomousCoreTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-dna-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoGenerativeEngine_SynthesizesDynamicTrends_WithoutHardcodedStrings()
    {
        var dir = TempDir();
        try
        {
            using var lex = new PlutoPreferenceLexicon(Path.Combine(dir, PlutoPreferenceLexicon.FileName));
            // 학습된 DNA: "nostalgic grain"(분위기) · "slow orbit"(카메라) 강화
            for (var i = 0; i < 5; i++) await lex.ReinforceAsync("toddler giggling, slow orbit, nostalgic grain, cinematic backlight");

            var gen = new PlutoGenerativeEngine(lex, new Random(7));
            var catalog = new PlutoTrendEngine().All;
            var catalogPrompts = new HashSet<string>(StringComparer.Ordinal);
            var catalogIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in catalog) { catalogPrompts.Add(c.VideoPrompt); catalogIds.Add(c.Id); }

            var now = new DateTime(2026, 11, 9, 17, 30, 0);
            var a = await gen.SynthesizeAsync(StudioThemeCategory.BabyParenting, 4, now);
            var b = await gen.SynthesizeAsync(StudioThemeCategory.BabyParenting, 4, now);
            Assert.Equal(4, a.Count);

            var prompts = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var usesDna = 0;
            foreach (var t in a.Concat(b))
            {
                Assert.StartsWith(PlutoGenerativeEngine.IdPrefix, t.Id, StringComparison.Ordinal);
                Assert.DoesNotContain(t.Id, catalogIds);
                Assert.DoesNotContain(t.VideoPrompt, catalogPrompts);
                Assert.True(ids.Add(t.Id), "중복 Id: " + t.Id);
                prompts.Add(t.VideoPrompt);
                // 바이럴 3막 구조
                Assert.Contains("Hook:", t.VideoPrompt, StringComparison.Ordinal);
                Assert.Contains("Core emotion:", t.VideoPrompt, StringComparison.Ordinal);
                Assert.Contains("Lingering outro:", t.VideoPrompt, StringComparison.Ordinal);
                // 컨텍스트 시드(가을)
                Assert.Contains("autumn", t.VideoPrompt, StringComparison.Ordinal);
                Assert.Contains("#Shorts", t.YouTubeHashtags);
                Assert.Contains("#Reels", t.InstagramHashtags);
                if (t.VideoPrompt.Contains("nostalgic grain", StringComparison.Ordinal) || t.CameraMotion == "slow orbit"
                    || t.VideoPrompt.Contains("cinematic backlight", StringComparison.Ordinal)) usesDna++;
            }
            Assert.True(prompts.Count >= 6, "다양성 부족: " + prompts.Count);
            Assert.True(usesDna >= 4, "DNA 토큰 활용 부족: " + usesDna);

            // 엔진 결선 → 발굴 + Find(생성 카드 조회)
            using var learning = new PlutoLearningService(Path.Combine(dir, "trend.json"), new Random(1));
            var engine = new PlutoTrendEngine(learning, gen);
            Assert.True(engine.CanDiscover);
            var fresh = await engine.DiscoverFreshTrendsAsync(StudioThemeCategory.PetsCompanions);
            Assert.Equal(4, fresh.Count);
            Assert.All(fresh, t => Assert.Equal(StudioThemeCategory.PetsCompanions, t.Category));
            Assert.Same(fresh[0], engine.Find(fresh[0].Id));

            // 생성기 미결선 → 고정 카탈로그 폴백
            var bare = new PlutoTrendEngine();
            Assert.False(bare.CanDiscover);
            var fb = await bare.DiscoverFreshTrendsAsync(StudioThemeCategory.HealingNature);
            Assert.All(fb, t => Assert.Contains(t.Id, catalogIds));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoPreferenceLexicon_ExtractsAndReinforcesTokens_OnExportSuccess()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, PlutoPreferenceLexicon.FileName);
        try
        {
            const string prompt = "baby rolling over in golden hour, eye-level dolly then macro close-up, pastel tone with warm bokeh, soft morning rays";
            var tokens = PlutoPreferenceLexicon.Extract(prompt);
            bool Has(DnaBucket b, string p) => tokens.Any(t => t.Bucket == b && t.Phrase == p);
            Assert.True(Has(DnaBucket.Lighting, "golden hour"));
            Assert.True(Has(DnaBucket.Lighting, "soft morning rays"));
            Assert.True(Has(DnaBucket.Camera, "eye-level dolly"));
            Assert.True(Has(DnaBucket.Camera, "macro close-up"));
            Assert.True(Has(DnaBucket.Atmosphere, "pastel tone"));
            Assert.True(Has(DnaBucket.Atmosphere, "warm bokeh"));
            Assert.False(Has(DnaBucket.Camera, "close-up"));   // 긴 시드 우선 — 부분 중복 억제
            Assert.DoesNotContain(tokens, t => t.Phrase.Contains("baby", StringComparison.Ordinal));   // 피사체는 스타일 토큰 아님
            // 미등록 어휘 꼬리 패턴 학습
            Assert.Contains(PlutoPreferenceLexicon.Extract("shot on anamorphic lens, teal palette"), t => t.Bucket == DnaBucket.Camera && t.Phrase == "anamorphic lens");

            // 내보내기 성공 루프: PublishViewModel → 렉시콘 강화 → pluto_dna_memory.json
            using (var lex = new PlutoPreferenceLexicon(path))
            using (var vm = new PublishViewModel(new NullYt(), new NullIg(), lexicon: lex))
            {
                vm.OpenForMedia(Path.Combine(dir, "clip.mp4"), "clip", null, prompt);
                Assert.False(await vm.RecordPlutoExportAsync());   // 트렌드 Id 없음(수동 프롬프트) → 트렌드 보상 없음, DNA 는 강화
                Assert.True(vm.LastDnaTokenCount >= 6);
                Assert.Equal(1, lex.GetFrequency(DnaBucket.Camera, "eye-level dolly"));
                Assert.Equal(0, await vm.ReinforcePlutoDnaAsync());   // 동일 프롬프트 연속 중복 보상 차단
                Assert.Equal(1, lex.GetFrequency(DnaBucket.Camera, "eye-level dolly"));
                await lex.ReinforceAsync("golden hour glow, slow orbit");
                Assert.Equal(2, lex.GetFrequency(DnaBucket.Lighting, "golden hour"));
                Assert.Equal(2 * PlutoPreferenceLexicon.ExportReward, lex.GetWeight(DnaBucket.Lighting, "golden hour"), 6);
                Assert.Equal("golden hour", lex.TopTokens(DnaBucket.Lighting, 1)[0]);
            }

            Assert.True(File.Exists(path));
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("eye-level dolly", json, StringComparison.Ordinal);

            using var reload = new PlutoPreferenceLexicon(path);
            await reload.EnsureLoadedAsync();
            Assert.Equal(2, reload.GetFrequency(DnaBucket.Lighting, "golden hour"));
            Assert.Equal(1, reload.GetFrequency(DnaBucket.Atmosphere, "pastel tone"));

            // 손상 파일 → 빈 사전으로 복구
            await File.WriteAllTextAsync(path, "{not json");
            using var broken = new PlutoPreferenceLexicon(path);
            await broken.EnsureLoadedAsync();
            Assert.Equal(0, broken.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoCopilot_GeneratesThreeActScript_FromUserMemo()
    {
        var copilot = new PlutoCopilot();
        var sc = copilot.Draft("오늘 아기가 거실에서 처음으로 뒤집기 성공하고 방긋 웃었어", new DateTime(2026, 11, 9, 9, 0, 0));
        Assert.Equal(StudioThemeCategory.BabyParenting, sc.Category);
        Assert.Equal("첫 뒤집기", sc.MilestoneKo);
        Assert.False(string.IsNullOrWhiteSpace(sc.HookTitle));
        Assert.Equal(3, sc.Acts.Count);
        Assert.Equal(0, sc.Acts[0].StartSec);
        Assert.Equal(PlutoCopilot.TotalSeconds, sc.Acts[2].EndSec);
        for (var i = 1; i < 3; i++) Assert.Equal(sc.Acts[i - 1].EndSec, sc.Acts[i].StartSec);
        Assert.Contains("roll over", sc.Acts[0].PromptEn, StringComparison.Ordinal);
        Assert.Contains("living room", sc.Acts[0].PromptEn, StringComparison.Ordinal);
        Assert.Contains("smile", sc.Acts[2].PromptEn, StringComparison.Ordinal);
        Assert.Contains("Act 1 (0-5s)", sc.VideoPrompt, StringComparison.Ordinal);
        Assert.Contains("Act 3 (10-15s)", sc.VideoPrompt, StringComparison.Ordinal);
        Assert.Contains("dolly", sc.CameraMotion, StringComparison.Ordinal);
        Assert.Equal(BabyBgmTrack.MusicBox, sc.BgmTrack);
        Assert.False(string.IsNullOrWhiteSpace(sc.BgmLabel));
        Assert.Contains("camera:", sc.BuildStudioPrompt(), StringComparison.Ordinal);

        var hundred = copilot.Draft("100일 된 우리 아기 첫 뒤집기");
        Assert.StartsWith("100일 만에 일어난 작은 기적", hundred.HookTitle, StringComparison.Ordinal);

        var pet = copilot.Draft("강아지랑 공원 산책 갔는데 너무 신나했어");
        Assert.Equal(StudioThemeCategory.PetsCompanions, pet.Category);
        Assert.Contains("park", pet.Acts[0].PromptEn, StringComparison.Ordinal);

        Assert.Equal("오늘의 작은 순간", copilot.Draft("평범한 하루").MilestoneKo);   // 미인식 → 기본 시나리오
        Assert.Throws<ArgumentException>(() => copilot.Draft("  "));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoSocialCopywriter_ComposesPost_WithTargetedHashtags()
    {
        var post = PlutoSocialCopywriter.Compose("100일 만에 일어난 작은 기적 🍼", "baby rolling over", SocialPlatform.Both);
        Assert.InRange(post.Hashtags.Count, PlutoSocialCopywriter.MinTags, PlutoSocialCopywriter.MaxTags);
        Assert.Contains("#Shorts", post.Hashtags);
        Assert.Contains("#Reels", post.Hashtags);
        Assert.Contains("#육아스타그램", post.Hashtags);
        Assert.Equal(post.Hashtags.Count, post.Hashtags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(post.StoryLines.Count, 2, 3);
        Assert.False(string.IsNullOrWhiteSpace(post.PinnedComment));
        Assert.Contains("📌", post.FullText, StringComparison.Ordinal);

        var reels = PlutoSocialCopywriter.Compose(null, "강아지 산책", SocialPlatform.InstagramReels);
        Assert.DoesNotContain("#Shorts", reels.Hashtags);
        Assert.Equal("", reels.PinnedComment);
        Assert.Contains("#댕댕이", reels.Hashtags);

        using var vm = new PublishViewModel(new NullYt(), new NullIg());
        vm.OpenForMedia("x.mp4", "x", null, "newborn sleeping");
        vm.ApplySocialPreset(SocialPresetKind.YouTubeShorts);   // 프리셋 → 자동 초안
        Assert.NotNull(vm.PlutoSocialDraft);
        Assert.Equal(SocialPlatform.YouTubeShorts, vm.PlutoSocialDraft!.Platform);
        vm.ToYouTube = vm.ToInstagram = false;
        Assert.Null(vm.GeneratePlutoSocialDraft());
        Assert.False(vm.CopyPlutoSocialDraft());   // 초안 없음/클립보드 미주입
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
