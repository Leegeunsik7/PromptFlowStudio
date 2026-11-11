using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🔥 플루토 바이럴 트렌드 학습 — 분석기 토큰 · 메모리 적재(+2 XP) · 코파일럿 프롬프트 자동 결합.</summary>
public class PlutoTrendLearningTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 9, 0, 0);
    private const string Memo = "오늘 아기가 거실에서 처음으로 뒤집기 성공하고 방긋 웃었어";

    private static string TempFile()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-viral-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return Path.Combine(d, PlutoLearningService.FileName);
    }

    private static PlutoTrend Card(string prompt, string camera) => new(
        "t1", StudioThemeCategory.PetsCompanions, "title", "hook", prompt, camera, "lofi",
        SocialPlatform.Both, new[] { "#shorts" }, new[] { "#reels" });

    [Theory]
    [InlineData(StudioThemeCategory.BabyParenting)]
    [InlineData(StudioThemeCategory.PetsCompanions)]
    [InlineData(StudioThemeCategory.HealingNature)]
    [InlineData(StudioThemeCategory.DailyVlog)]
    public void PlutoTrendAnalyzer_ExtractsViralTokens_FromInputCategory(StudioThemeCategory category)
    {
        var a = PlutoTrendAnalyzer.Analyze(category);

        Assert.Equal(category, a.Category);
        Assert.Equal(PlutoTrendAnalyzer.KnownTags.Count, a.Tokens.Count);
        Assert.Contains(a.Tokens, t => t.Facet == ViralTrendFacet.Hook && (t.Tag == PlutoTrendAnalyzer.Pov || t.Tag == PlutoTrendAnalyzer.MicroZoomHook));
        Assert.Contains(a.Tokens, t => t.Facet == ViralTrendFacet.Pacing && t.Tag == PlutoTrendAnalyzer.AsmrWhisper);
        foreach (var t in a.Tokens)
        {
            Assert.Contains(t.Tag, PlutoTrendAnalyzer.KnownTags);
            Assert.True(t.Score > 0);
            Assert.False(string.IsNullOrWhiteSpace(t.Modifier));
        }
        for (var i = 1; i < a.Tokens.Count; i++) Assert.True(a.Tokens[i - 1].Score >= a.Tokens[i].Score);
        Assert.Contains("1.5s", a.HookStructure, StringComparison.Ordinal);
        Assert.Contains("cuts", a.Pacing, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(a.LensAngle));
        Assert.False(string.IsNullOrWhiteSpace(a.Lighting));
    }

    [Fact]
    public void PlutoTrendAnalyzer_SampleKeywordHits_BoostMatchingTag()
    {
        var baseline = PlutoTrendAnalyzer.Analyze(StudioThemeCategory.PetsCompanions);
        var samples = new[]
        {
            Card("16mm vintage film grain puppy nap", "handheld"),
            Card("retro home video kitten", "manual zoom"),
        };
        var boosted = PlutoTrendAnalyzer.Analyze(StudioThemeCategory.PetsCompanions, samples);

        static double Score(ViralTrendAnalysis a, string tag) { foreach (var t in a.Tokens) if (t.Tag == tag) return t.Score; return 0; }
        Assert.True(Score(boosted, PlutoTrendAnalyzer.VintageFilm16mm) > Score(baseline, PlutoTrendAnalyzer.VintageFilm16mm));
        Assert.Equal(Score(baseline, PlutoTrendAnalyzer.GoldenHourGaze), Score(boosted, PlutoTrendAnalyzer.GoldenHourGaze));
    }

    [Fact]
    public async Task PlutoLearningService_IngestViralTrends_PersistsPoolAndAwardsXp()
    {
        var path = TempFile();
        using (var learning = new PlutoLearningService(path, new Random(1)))
        {
            var xp0 = learning.CurrentXP;
            var xp1 = await learning.IngestViralTrendsAsync(PlutoTrendAnalyzer.Analyze(StudioThemeCategory.BabyParenting).Tokens);
            Assert.Equal(xp0 + PlutoLearningService.TrendIngestXp, xp1);
            var pool = learning.GetActiveViralTrends(3);
            Assert.Equal(3, pool.Count);
            Assert.Equal(PlutoTrendAnalyzer.MicroZoomHook, pool[0].Tag);   // 🍼 아기 카테고리 사전 최상위
            Assert.Equal(xp1, await learning.IngestViralTrendsAsync(Array.Empty<ViralTrendToken>()));   // 빈 적재 → XP 미적립
        }
        Assert.True(File.Exists(path));
        using var reload = new PlutoLearningService(path, new Random(1));
        await reload.EnsureLoadedAsync();
        Assert.Equal(PlutoLearningService.TrendIngestXp, reload.CurrentXP);
        Assert.Equal(PlutoTrendAnalyzer.MicroZoomHook, reload.GetActiveViralTrends(1)[0].Tag);
    }

    [Fact]
    public async Task PlutoCopilot_InjectsActiveViralTokens_IntoPrompts()
    {
        using var learning = new PlutoLearningService(TempFile(), new Random(1));
        var plain = new PlutoCopilot().DraftTripletLocal(Memo, Now);
        Assert.All(plain, v => Assert.DoesNotContain(PlutoPromptCompressor.ViralMarker.Trim(), v.Prompt, StringComparison.Ordinal));

        await learning.IngestViralTrendsAsync(PlutoTrendAnalyzer.Analyze(StudioThemeCategory.BabyParenting).Tokens);
        var copilot = new PlutoCopilot { Learning = learning };
        var top = learning.GetActiveViralTrends(PlutoCopilot.MaxViralModifiers);

        var triplet = copilot.DraftTripletLocal(Memo, Now);
        Assert.Equal(3, triplet.Count);
        foreach (var v in triplet)
        {
            Assert.Contains(PlutoPromptCompressor.ViralMarker.Trim(), v.Prompt, StringComparison.Ordinal);
            foreach (var t in top) Assert.Contains(PlutoTrendAnalyzer.ModifierFor(t.Tag)!, v.Prompt, StringComparison.Ordinal);
        }

        var script = await copilot.DraftScriptFromMemoAsync(Memo, Now);
        Assert.Contains(PlutoTrendAnalyzer.ModifierFor(top[0].Tag)!, script.VideoPrompt, StringComparison.Ordinal);

        var streamed = new List<PlutoScriptVariant>();
        await foreach (var v in copilot.StreamTripletVariantsAsync(Memo, Now)) streamed.Add(v);
        Assert.Equal(3, streamed.Count);
        Assert.All(streamed, v => Assert.Contains(PlutoTrendAnalyzer.ModifierFor(top[0].Tag)!, v.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void PlutoPromptCompressor_BlendViralModifiers_IsIdempotent()
    {
        var mods = new[] { "first-person POV opening shot", "first-person POV opening shot", " " };
        var once = PlutoPromptCompressor.BlendViralModifiers("Act 1: baby smiles", mods);
        Assert.Equal("Act 1: baby smiles. Viral trend: first-person POV opening shot.", once);
        Assert.Equal(once, PlutoPromptCompressor.BlendViralModifiers(once, mods));
        Assert.Equal("x", PlutoPromptCompressor.BlendViralModifiers("x", null));

        var compact = PlutoPromptCompressor.Compose(PlutoCopilot.LlmTripletInstruction, PlutoPromptCompressor.ViralRequestLabel + "#POV = pov");
        Assert.Contains("viral trends: #POV = pov", compact.User, StringComparison.Ordinal);
    }
}
