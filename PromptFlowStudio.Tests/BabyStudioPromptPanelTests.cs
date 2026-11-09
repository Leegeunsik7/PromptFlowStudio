using System;
using PromptFlowStudio.Services.AI;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>👶 프롬프트 패널 베이비 스튜디오 정렬 — 아기 감성 무드 · 성장 단계 인젝터 회귀 잠금.</summary>
public class BabyStudioPromptPanelTests
{
    private static readonly string[] StockKeywords = { "bull", "bear", "candlestick", "order book", "order-book", "fibonacci", "nasdaq", "wall street", "volatility", "trade", "ticker" };

    private static void AssertNoStock(string s)
    {
        foreach (var k in StockKeywords) Assert.DoesNotContain(k, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeminiPromptService_AppliesBabyMoods_Correctly()
    {
        const string idea = "a cute baby in a sunny nursery";
        Assert.Equal(4, GeminiPromptService.BabyMoods.Length);

        var sleep = GeminiPromptService.ApplyBabyMood(idea, BabyMood.SereneSleep);
        Assert.StartsWith(idea + ", ", sleep);
        Assert.Contains("sweet nap", sleep);
        Assert.Contains("soft breathing", sleep);
        Assert.Equal(BabyMood.SereneSleep, GeminiPromptService.ActiveBabyMood(sleep));

        var play = GeminiPromptService.ApplyBabyMood(sleep, BabyMood.GigglePlay);
        Assert.Contains("toy playtime", play);
        Assert.DoesNotContain("sweet nap", play);
        Assert.Equal(BabyMood.GigglePlay, GeminiPromptService.ActiveBabyMood(play));
        Assert.Equal(play, GeminiPromptService.ApplyBabyMood(play, BabyMood.GigglePlay));
        Assert.Equal(idea, GeminiPromptService.StripBabyMood(play));

        Assert.Contains("milestone celebration", GeminiPromptService.ApplyBabyMood(idea, BabyMood.FirstMilestone));
        Assert.Contains("golden sunbeam", GeminiPromptService.ApplyBabyMood(idea, BabyMood.FairytaleFantasy));
        Assert.Equal(GeminiPromptService.BabyMoodOf(BabyMood.SereneSleep).Tokens, GeminiPromptService.ApplyBabyMood("  ", BabyMood.SereneSleep));
        Assert.Null(GeminiPromptService.ActiveBabyMood(idea));

        foreach (var m in GeminiPromptService.BabyMoods) { AssertNoStock(m.Tokens); AssertNoStock(m.Label + m.English); }
    }

    [Fact]
    public void Workspace_BabyGrowthStage_SetsContextualKeywords()
    {
        const string idea = "baby smiling at mom";
        Assert.Equal(4, GeminiPromptService.BabyGrowthStages.Length);

        var nb = GeminiPromptService.ApplyGrowthStage(idea, BabyGrowthStage.Newborn);
        Assert.StartsWith("newborn baby 0-3 months old", nb);
        Assert.Contains("soft swaddle", nb);
        Assert.EndsWith(idea, nb);
        Assert.Equal(BabyGrowthStage.Newborn, GeminiPromptService.ActiveGrowthStage(nb));

        // 단계 전환 — 이전 연령 키워드 교체(누적 없음) · 무드 블록 보존
        var withMood = GeminiPromptService.ApplyBabyMood(nb, BabyMood.FairytaleFantasy);
        var bday = GeminiPromptService.ApplyGrowthStage(withMood, BabyGrowthStage.FirstBirthday);
        Assert.Contains("hanbok", bday);
        Assert.Contains("birthday cake", bday);
        Assert.DoesNotContain("swaddle", bday);
        Assert.Equal(BabyGrowthStage.FirstBirthday, GeminiPromptService.ActiveGrowthStage(bday));
        Assert.Equal(BabyMood.FairytaleFantasy, GeminiPromptService.ActiveBabyMood(bday));
        Assert.Equal(bday, GeminiPromptService.ApplyGrowthStage(bday, BabyGrowthStage.FirstBirthday));
        Assert.Equal(GeminiPromptService.StripBabyMood(withMood).Replace(GeminiPromptService.GrowthStageOf(BabyGrowthStage.Newborn).Tokens + ", ", ""),
            GeminiPromptService.StripBabyMood(GeminiPromptService.StripGrowthStage(bday)));

        Assert.Contains("tummy time", GeminiPromptService.ApplyGrowthStage(idea, BabyGrowthStage.HundredDays));
        Assert.Contains("blowing bubbles", GeminiPromptService.ApplyGrowthStage(idea, BabyGrowthStage.Toddler));
        Assert.Equal(GeminiPromptService.GrowthStageOf(BabyGrowthStage.Toddler).Tokens, GeminiPromptService.ApplyGrowthStage(null, BabyGrowthStage.Toddler));
        Assert.Null(GeminiPromptService.ActiveGrowthStage(idea));

        foreach (var g in GeminiPromptService.BabyGrowthStages) AssertNoStock(g.Tokens);
    }
}
