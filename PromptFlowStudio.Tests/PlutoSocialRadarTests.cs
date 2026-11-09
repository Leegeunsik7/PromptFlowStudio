using System.Text.Json;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Tests;

/// <summary>📈 Pluto Social Trend Radar — YouTube Shorts/Instagram Reels 채널 공식 추출 · 오프라인 폴백 · 채널 학습 영속 · XP · 트렌드 랩 UI.</summary>
public class PlutoSocialRadarTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-radar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string? Response { get; init; }
        public Exception? Throw { get; init; }
        public TaskCompletionSource<string?>? Gate { get; init; }
        public int Calls;
        public string LastSystem = "";
        public string LastUser = "";

        public async Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastSystem = system; LastUser = user;
            if (Gate is not null) return await Gate.Task.ConfigureAwait(false);
            if (Throw is not null) throw Throw;
            return Response;
        }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoSocialRadar_ReturnsPlatformInsights_WithFallbackIntegrity()
    {
        var dir = TempDir();
        try
        {
            // ① 오프라인(LLM 없음) — 12개 벤치마크 · 플랫폼 필터 · 구조 무결성
            Assert.Equal(12, PlutoSocialRadarEngine.Benchmarks.Count);
            Assert.Equal(12, PlutoSocialRadarEngine.Benchmarks.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, PlutoSocialRadarEngine.Benchmarks.Select(b => b.ChannelCategory).Distinct().Count());
            using var offline = new PlutoSocialRadarEngine(Path.Combine(dir, "a.json"));
            var yt = await offline.GetTrendingSocialInsightsAsync(SocialPlatform.YouTubeShorts);
            var ig = await offline.GetTrendingSocialInsightsAsync(SocialPlatform.InstagramReels);
            Assert.False(offline.LastUsedLlm);
            Assert.Equal(6, yt.Count);
            Assert.Equal(6, ig.Count);
            Assert.All(yt, s => Assert.Equal(SocialPlatform.YouTubeShorts, s.Platform));
            Assert.All(ig, s => Assert.Equal(SocialPlatform.InstagramReels, s.Platform));
            Assert.Equal(12, (await offline.GetTrendingSocialInsightsAsync(SocialPlatform.Both)).Count);
            foreach (var s in yt.Concat(ig))
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Title));
                Assert.False(string.IsNullOrWhiteSpace(s.ViralMechanism));
                Assert.Contains("s", s.PacingGuideline, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(s.RecommendedPromptTags.Camera));
                Assert.False(string.IsNullOrWhiteSpace(s.RecommendedPromptTags.Lighting));
                Assert.False(string.IsNullOrWhiteSpace(s.RecommendedPromptTags.SubjectAction));
                Assert.True(s.ViralHashtagSet.Count >= 4);
                Assert.All(s.ViralHashtagSet, t => Assert.StartsWith("#", t, StringComparison.Ordinal));
                Assert.Contains(s.Platform == SocialPlatform.YouTubeShorts ? "#Shorts" : "#Reels", s.ViralHashtagSet);
                var prompt = s.BuildStudioPrompt();
                Assert.Contains(s.PacingGuideline, prompt, StringComparison.Ordinal);
                Assert.Contains(s.RecommendedPromptTags.Camera, prompt, StringComparison.Ordinal);
                Assert.False(s.FromLlm);
            }
            Assert.Contains(yt, s => s.Title == "1-Second Hook Toddler Zoom" && s.ViralMechanism.Contains("15s seamless audio loop", StringComparison.Ordinal));
            Assert.Contains(ig, s => s.Title == "Warm Sunlight Nostalgia Reel");

            // ② Gemini 경로 — 스키마 검증 · 중복/누락 제외 · 부족분 벤치마크 보충([부분 구제])
            const string json = "```json\n{\"insights\":[" +
                "{\"title\":\"Mirror Reveal Toddler Cut\",\"category\":\"babymilestones\",\"mechanism\":\"0-1s mirror reveal + 12s loop\",\"camera\":\"whip pan\",\"lighting\":\"soft daylight\",\"action\":\"toddler waving at a mirror\",\"pacing\":\"0.7s - 1.0s rapid pacing\",\"bgm\":\"musicbox\",\"hashtags\":[\"아기쇼츠\",\"#babyshorts\",\"#아기쇼츠\"]}," +
                "{\"title\":\"Mirror Reveal Toddler Cut\",\"mechanism\":\"duplicate title entry\",\"pacing\":\"1s\"}," +
                "{\"title\":\"Missing Pacing\",\"mechanism\":\"no pacing given here\"}]}\n```";
            var llm = new FakeLlm { Response = json };
            using var online = new PlutoSocialRadarEngine(Path.Combine(dir, "b.json"), llm);
            var live = await online.GetTrendingSocialInsightsAsync(SocialPlatform.YouTubeShorts);
            Assert.Equal(1, llm.Calls);
            Assert.True(online.LastUsedLlm);
            Assert.Contains("YouTube Shorts", llm.LastUser, StringComparison.Ordinal);
            Assert.Contains("family/pet/lifestyle", llm.LastSystem, StringComparison.Ordinal);
            Assert.Equal(6, live.Count);
            Assert.True(live[0].FromLlm);
            Assert.Equal(SocialChannelCategory.BabyMilestones, live[0].ChannelCategory);
            Assert.Equal(SocialBgmStyle.MusicBoxReverb, live[0].TrendingBgmStyle);
            Assert.Equal(BabyBgmTrack.MusicBox, live[0].BgmTrack);
            Assert.Equal(new[] { "#Shorts", "#아기쇼츠", "#babyshorts" }, live[0].ViralHashtagSet);
            Assert.StartsWith(PlutoSocialRadarEngine.IdPrefix + "llm-yt-", live[0].Id, StringComparison.Ordinal);
            Assert.False(live[1].FromLlm);   // 보충분 = 벤치마크
            Assert.Same(live[0], online.Find(live[0].Id));

            // ③ 예외 · 스키마 불일치 · 미설정(호출 0) · 3.5초 예산 초과 → 벤치마크
            var boom = new FakeLlm { Throw = new HttpRequestException("503") };
            using var r1 = new PlutoSocialRadarEngine(Path.Combine(dir, "c.json"), boom);
            Assert.Equal(PlutoSocialRadarEngine.BenchmarksFor(SocialPlatform.InstagramReels).Select(s => s.Id), (await r1.GetTrendingSocialInsightsAsync(SocialPlatform.InstagramReels)).Select(s => s.Id));
            Assert.False(r1.LastUsedLlm);
            using var r2 = new PlutoSocialRadarEngine(Path.Combine(dir, "d.json"), new FakeLlm { Response = "{\"nope\":true}" });
            Assert.Equal(6, (await r2.GetTrendingSocialInsightsAsync(SocialPlatform.YouTubeShorts)).Count);
            Assert.False(r2.LastUsedLlm);
            var off = new FakeLlm { IsAvailable = false, Response = json };
            using var r3 = new PlutoSocialRadarEngine(Path.Combine(dir, "e.json"), off);
            Assert.Equal(6, (await r3.GetTrendingSocialInsightsAsync(SocialPlatform.YouTubeShorts)).Count);
            Assert.Equal(0, off.Calls);
            var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.Null(await PlutoLlmBridge.TryCompleteAsync(new FakeLlm { Gate = gate }, PlutoSocialRadarEngine.LlmRadarInstruction, "u", 0.7, CancellationToken.None, TimeSpan.FromMilliseconds(120)).WaitAsync(TimeSpan.FromSeconds(30)));
            gate.SetResult(json);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoSocialRadar_LearnsChannelStyle_AndUpdatesKnowledgeBase()
    {
        var dir = TempDir();
        try
        {
            var radarPath = Path.Combine(dir, PlutoSocialRadarEngine.FileName);
            using var learning = new PlutoLearningService(Path.Combine(dir, PlutoLearningService.FileName), new Random(1));
            await learning.EnsureLoadedAsync();
            Assert.Equal(0, learning.CurrentXP);

            using (var radar = new PlutoSocialRadarEngine(radarPath, null, learning))
            {
                var changed = 0;
                radar.Changed += () => Interlocked.Increment(ref changed);
                Assert.Equal(1.0, await radar.LearnChannelStyleAsync("srl-yt-01"));
                Assert.Equal(2.0, await radar.LearnChannelStyleAsync("srl-yt-01"));
                Assert.Equal(1.0, await radar.LearnChannelStyleAsync("srl-ig-01"));
                Assert.Equal(3, changed);
                Assert.Equal(3, radar.TotalLearned);
                Assert.Equal(2, radar.GetLearnedCount("srl-yt-01"));

                // 🧠 XP +2 × 3 = 6 (가중치/랭킹 불변)
                Assert.Equal(3 * PlutoSocialRadarEngine.LearnXp, learning.CurrentXP);
                Assert.Equal(1, learning.CurrentLevel);
                Assert.Equal(0, learning.GetWeight("social:srl-yt-01"));

                await Assert.ThrowsAsync<ArgumentException>(() => radar.LearnChannelStyleAsync(" "));
                await Assert.ThrowsAsync<KeyNotFoundException>(() => radar.LearnChannelStyleAsync("srl-unknown"));

                // 📈 상위 패턴 — 가중치 내림차순 · 동일 테마 우선
                var top = radar.TopViralPatterns(StudioThemeCategory.BabyParenting, 2);
                Assert.Equal(new[] { "srl-yt-01", "srl-ig-01" }, top.Select(s => s.Id));
                Assert.Equal("srl-ig-01", radar.TopViralPatterns(StudioThemeCategory.DailyVlog, 1)[0].Id);
            }

            // 💾 pluto_social_radar.json 영속 · 스냅샷 포함
            Assert.True(File.Exists(radarPath));
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(radarPath)))
            {
                var e = doc.RootElement.GetProperty("srl-yt-01");
                Assert.Equal(2.0, e.GetProperty("Weight").GetDouble());
                Assert.Equal(2, e.GetProperty("Learned").GetInt32());
                Assert.Equal("1-Second Hook Toddler Zoom", e.GetProperty("Title").GetString());
            }

            // 🔁 재시작 후 가중치 · XP 복원 + LLM 인사이트도 스냅샷으로 복원
            using (var reloaded = new PlutoSocialRadarEngine(radarPath))
            {
                await reloaded.EnsureLoadedAsync();
                Assert.Equal(2.0, reloaded.GetWeight("srl-yt-01"));
                Assert.Equal(3.0, await reloaded.LearnChannelStyleAsync("srl-yt-01"));
            }
            using (var l2 = new PlutoLearningService(Path.Combine(dir, PlutoLearningService.FileName)))
            {
                await l2.EnsureLoadedAsync();
                Assert.Equal(6, l2.CurrentXP);
            }

            const string json = "{\"insights\":[{\"title\":\"Sleepy Puppy Loop\",\"category\":\"PetHealing\",\"mechanism\":\"yawn hook at 0.5s + loop\",\"pacing\":\"2s holds\",\"bgm\":\"lullaby lofi\"}]}";
            string llmId;
            using (var r = new PlutoSocialRadarEngine(radarPath, new FakeLlm { Response = json }))
            {
                llmId = (await r.GetTrendingSocialInsightsAsync(SocialPlatform.InstagramReels))[0].Id;
                await r.LearnChannelStyleAsync(llmId);
            }
            using (var r = new PlutoSocialRadarEngine(radarPath))
            {
                await r.EnsureLoadedAsync();
                var restored = r.Find(llmId);
                Assert.NotNull(restored);
                Assert.True(restored!.FromLlm);
                Assert.Equal("Sleepy Puppy Loop", restored.Title);
                Assert.Equal(SocialBgmStyle.LullabyLoFi, restored.TrendingBgmStyle);
            }

            // 🔗 생성 엔진 · 코파일럿 — 상위 바이럴 패턴(페이싱/훅) 주입
            using var radar2 = new PlutoSocialRadarEngine(radarPath);
            var gen = new PlutoGenerativeEngine(null, null, radar2, new Random(5));
            var trends = await gen.SynthesizeAsync(StudioThemeCategory.BabyParenting, 3, new DateTime(2026, 11, 10, 9, 0, 0));
            Assert.All(trends, t => Assert.Contains("SNS pacing: ", t.VideoPrompt, StringComparison.Ordinal));
            Assert.Contains(trends, t => t.VideoPrompt.Contains("0.8s - 1.2s rapid pacing", StringComparison.Ordinal));

            var genLlm = new FakeLlm();
            await new PlutoGenerativeEngine(null, genLlm, radar2, new Random(5)).GenerateDynamicTrendsAsync(StudioThemeCategory.BabyParenting, 2);
            Assert.Contains("1-Second Hook Toddler Zoom", genLlm.LastUser, StringComparison.Ordinal);

            var copLlm = new FakeLlm();
            var script = await new PlutoCopilot(null, copLlm, radar2).DraftScriptFromMemoAsync("아기가 오늘 처음 걸었어");
            Assert.Contains("SNS pacing: ", script.VideoPrompt, StringComparison.Ordinal);
            Assert.Contains("Proven SNS viral formula", copLlm.LastUser, StringComparison.Ordinal);
            Assert.DoesNotContain("SNS pacing", new PlutoCopilot().Draft("아기가 오늘 처음 걸었어").VideoPrompt, StringComparison.Ordinal);   // 미주입 → 기존 동작 유지

            // 🚀 스튜디오 BGM 핸드오프(1회 소비)
            var draft = new PromptDraftService();
            draft.SendBgm(PlutoSocialRadarEngine.Benchmarks[0].BgmTrack);
            Assert.Equal(BabyBgmTrack.MusicBox, draft.ConsumeBgm());
            Assert.Null(draft.ConsumeBgm());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoTrendLab_RendersSocialRadarCards_Correctly()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "PlutoTrendLab.razor"));

        // 서브 내비게이션 토글 + 플랫폼 탭
        Assert.Contains("@inject PlutoSocialRadarEngine Radar", page, StringComparison.Ordinal);
        Assert.Contains("🪐 플루토 큐레이션", page, StringComparison.Ordinal);
        Assert.Contains("📈 실시간 SNS 트렌드 레이더", page, StringComparison.Ordinal);
        Assert.Contains("🔴 YouTube Shorts 급상승 공식", page, StringComparison.Ordinal);
        Assert.Contains("🟣 Instagram Reels 감성 무드", page, StringComparison.Ordinal);
        Assert.Contains("Trending YouTube Channel Formulas", page, StringComparison.Ordinal);
        Assert.Contains("Trending Instagram Reels Aesthetics", page, StringComparison.Ordinal);

        // 카드 — 바이럴 메커니즘 배지 · 카메라 · BGM · 태그
        Assert.Contains("data-radar-insight=\"@s.Id\"", page, StringComparison.Ordinal);
        Assert.Contains("data-radar-mechanism", page, StringComparison.Ordinal);
        Assert.Contains("@s.ViralMechanism", page, StringComparison.Ordinal);
        Assert.Contains("@s.RecommendedPromptTags.Camera", page, StringComparison.Ordinal);
        Assert.Contains("PlutoSocialRadarEngine.BgmLabel(s.TrendingBgmStyle)", page, StringComparison.Ordinal);
        Assert.Contains("s.ViralHashtagSet", page, StringComparison.Ordinal);

        // 액션 트리거 결선
        Assert.Contains("🧠 플루토 학습 (+2 XP)", page, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"() => LearnStyleAsync(s)\"", page, StringComparison.Ordinal);
        Assert.Contains("Radar.LearnChannelStyleAsync(s.Id", page, StringComparison.Ordinal);
        Assert.Contains("🚀 이 공식으로 스튜디오 제작", page, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"() => ApplyInsight(s)\"", page, StringComparison.Ordinal);
        Assert.Contains("Draft.SendBgm(s.BgmTrack)", page, StringComparison.Ordinal);
        Assert.Contains("Nav.NavigateTo(\"workspace\")", page, StringComparison.Ordinal);
        Assert.Contains("Radar.GetTrendingSocialInsightsAsync(", page, StringComparison.Ordinal);

        // 구독/해제 쌍
        Assert.Contains("Radar.Changed += OnLearningChanged", page, StringComparison.Ordinal);
        Assert.Contains("Radar.Changed -= OnLearningChanged", page, StringComparison.Ordinal);

        // 작업실 BGM 흡수 · DI 양쪽 등록
        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("Draft.ConsumeBgm()", ws, StringComparison.Ordinal);
        var main = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "MainForm.cs"));
        var maui = File.ReadAllText(Path.Combine(root, "PromptFlowStudio.Mobile", "MauiProgram.cs"));
        Assert.Contains("new PromptFlowStudio.Services.Pluto.PlutoSocialRadarEngine(", main, StringComparison.Ordinal);
        Assert.Contains("new PromptFlowStudio.Services.Pluto.PlutoSocialRadarEngine(", maui, StringComparison.Ordinal);
    }
}
