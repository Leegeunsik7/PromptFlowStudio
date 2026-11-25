using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Tests;

/// <summary>👶 아기 시나리오 프리셋 · 짧은 프롬프트 자동 확장 · 합성기 결선 · Pluto 불변식(9/6/2) 회귀 잠금.</summary>
public class BabyPromptAutoExpanderTests
{
    private readonly PromptSynthesizerService _synth = new();

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Scenarios_CoverFiveDistinctThemes_WithAllLocales()
    {
        var s = BabyPromptAutoExpander.Scenarios;
        Assert.Equal(5, s.Count);
        string[] keys = { "firststeps", "naptime", "bath", "toydiscovery", "parentbond" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < s.Count; i++)
        {
            Assert.Equal(keys[i], s[i].Key);
            Assert.True(seen.Add(s[i].Key));
            Assert.False(string.IsNullOrWhiteSpace(s[i].LabelKo));
            Assert.False(string.IsNullOrWhiteSpace(s[i].LabelEn));
            Assert.False(string.IsNullOrWhiteSpace(s[i].LabelJa));
            Assert.False(string.IsNullOrWhiteSpace(s[i].LabelEs));
            Assert.Equal(s[i].LabelEn, BabyPromptAutoExpander.Label(s[i], "en-US"));
            Assert.Equal(s[i].LabelJa, BabyPromptAutoExpander.Label(s[i], "ja"));
            Assert.Equal(s[i].LabelEs, BabyPromptAutoExpander.Label(s[i], "es-ES"));
            Assert.Equal(s[i].LabelKo, BabyPromptAutoExpander.Label(s[i], "fr"));
            Assert.Same(s[i], BabyPromptAutoExpander.MatchScenario(s[i].Seed));
        }
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData("baby first steps", "firststeps")]
    [InlineData("아기 낮잠", "naptime")]
    [InlineData("赤ちゃんのお風呂", "bath")]
    [InlineData("bebé con juguete", "toydiscovery")]
    [InlineData("엄마와 아기", "parentbond")]
    public void ShortBabyKeyword_ExpandsWithScenarioFragments_InEveryLocale(string input, string key)
    {
        var s = BabyPromptAutoExpander.Get(key)!;
        var r = BabyPromptAutoExpander.Expand(input);

        Assert.StartsWith(input, r);
        Assert.Contains(s.Subject, r);
        Assert.Contains(s.Lighting, r);
        Assert.Contains(s.Camera, r);
        Assert.Contains(s.Atmosphere, r);
        Assert.Contains(BabyPromptAutoExpander.SafetyPhrase, r);
        Assert.True(r.Length <= PromptSynthesizerService.MaxCharacterLimit);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void BabyWithoutScenario_UsesWarmDefaults()
    {
        var r = BabyPromptAutoExpander.Expand("smiling baby");
        Assert.Contains(BabyPromptAutoExpander.DefaultLighting, r);
        Assert.Contains(BabyPromptAutoExpander.DefaultCamera, r);
        Assert.Contains(BabyPromptAutoExpander.DefaultAtmosphere, r);
        Assert.EndsWith(BabyPromptAutoExpander.SafetyPhrase, r);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Expand_IsIdempotent_AndPreservesNonBabyAndLongPrompts()
    {
        var once = BabyPromptAutoExpander.Expand("baby nap");
        Assert.Equal(once, BabyPromptAutoExpander.Expand(once));

        Assert.Equal("a cat", BabyPromptAutoExpander.Expand("  a cat  "));
        Assert.Equal("", BabyPromptAutoExpander.Expand(null));

        var longBaby = "baby " + new string('x', BabyPromptAutoExpander.ShortPromptThreshold);
        Assert.Equal(longBaby, BabyPromptAutoExpander.Expand(longBaby));

        // 아기 모드 ON 이면 토큰 없는 짧은 입력도 확장.
        Assert.Contains(BabyPromptAutoExpander.SafetyPhrase, BabyPromptAutoExpander.Expand("sunny garden", babyMode: true));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Expand_DoesNotDuplicateFragmentsAlreadyPresent()
    {
        var s = BabyPromptAutoExpander.Get("bath")!;
        var r = BabyPromptAutoExpander.Expand("baby bath, " + s.Camera, babyMode: true);
        Assert.Equal(r.IndexOf(s.Camera, StringComparison.Ordinal), r.LastIndexOf(s.Camera, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Synthesizer_AutoExpandsShortBabyPrompt_ByDefault_AndCanOptOut()
    {
        var on = _synth.Synthesize(new PromptContext("baby first steps", CameraMotion: "Orbit"));
        Assert.Contains(BabyPromptAutoExpander.SafetyPhrase, on.FinalPositivePrompt);
        Assert.Contains("camera motion: Orbit", on.FinalPositivePrompt);
        Assert.False(on.IsOverBudget);

        var off = _synth.Synthesize(new PromptContext("baby first steps", AutoBabyExpansion: false));
        Assert.Equal("baby first steps", off.FinalPositivePrompt);

        Assert.Equal("a cat", _synth.Synthesize(new PromptContext("a cat")).FinalPositivePrompt);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void BabyScenarioPresets_AreExpandedOneClickPrompts_AndLegacyPresetsUntouched()
    {
        var chips = PromptPresets.BabyScenarios;
        Assert.Equal(BabyPromptAutoExpander.Scenarios.Count, chips.Length);
        for (var i = 0; i < chips.Length; i++)
        {
            Assert.Contains(BabyPromptAutoExpander.SafetyPhrase, chips[i].Snippet);
            Assert.Equal(BabyPromptAutoExpander.Scenarios[i].Emoji, chips[i].Icon);
            Assert.True(chips[i].Snippet.Length <= PromptSynthesizerService.MaxCharacterLimit);
        }
        Assert.Equal("", BabyPromptAutoExpander.ExpandScenario("missing"));
        Assert.Equal(5, PromptPresets.Baby.Length);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoInvariants_RemainNineSixTwo()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }
}
