using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>💡 스토리 레시피 &amp; 인스피레이션 허브 — 프리셋 유효성 · 칩 합성 포맷 · 뷰모델 UserInput 주입 회귀 잠금.</summary>
public class PromptInspirationTests
{
    private readonly PromptInspirationService _svc = new();
    private readonly PromptSynthesizerService _synth = new();

    [Fact]
    public void CuratedPresets_AtLeast15_AllFieldsValid_TitlesUnique()
    {
        var presets = _svc.GetCuratedPresets();
        Assert.True(presets.Count >= 15);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.FullPrompt));
            Assert.False(string.IsNullOrWhiteSpace(r.RecommendedCamera));
            Assert.False(string.IsNullOrWhiteSpace(r.RecommendedLighting));
            Assert.True(r.FullPrompt.Length <= PromptSynthesizerService.MaxCharacterLimit);
            Assert.True(Enum.IsDefined(r.Category));
            Assert.True(titles.Add(r.Title), "중복 제목: " + r.Title);
        }
    }

    [Fact]
    public void EveryCategory_HasPresets_AndFilterMatches()
    {
        foreach (var cat in Enum.GetValues<StoryPresetCategory>())
        {
            var list = _svc.GetPresetsByCategory(cat);
            Assert.NotEmpty(list);
            Assert.All(list, r => Assert.Equal(cat, r.Category));
            Assert.False(string.IsNullOrWhiteSpace(PromptInspirationService.CategoryLabel(cat)));
        }
    }

    [Fact]
    public void BuildFromChips_AllChips_ProducesCinematicFormat()
    {
        var s = _svc.BuildFromChips("a lone astronaut", "a misty fjord", "slow push-in", "golden hour rays");
        Assert.StartsWith("Cinematic shot of a lone astronaut in a misty fjord, camera: slow push-in, lighting: golden hour rays, ", s);
        Assert.Contains("4K", s);
    }

    [Fact]
    public void BuildFromChips_SkipsBlankChips_AndTrims()
    {
        var s = _svc.BuildFromChips("  a golden retriever ", null, "", "  ");
        Assert.StartsWith("Cinematic shot of a golden retriever, ", s);
        Assert.DoesNotContain(" in ", s);
        Assert.DoesNotContain("camera:", s);
        Assert.DoesNotContain("lighting:", s);

        var noSubject = _svc.BuildFromChips(null, "a neon alley", null, null);
        Assert.StartsWith("Cinematic shot of a striking scene in a neon alley, ", noSubject);
    }

    [Fact]
    public void BuildFromChips_AllBlank_ReturnsEmpty_AndOverlongIsTruncated()
    {
        Assert.Equal("", _svc.BuildFromChips(null, " ", "", null));
        var big = _svc.BuildFromChips(new string('x', 2000), null, null, null);
        Assert.Equal(PromptSynthesizerService.MaxCharacterLimit, big.Length);
    }

    [Fact]
    public void ChipCatalogs_NonEmpty_WithSnippets()
    {
        foreach (var group in new[] { PromptInspirationService.Subjects, PromptInspirationService.Environments, PromptInspirationService.Motions, PromptInspirationService.Lightings })
        {
            Assert.NotEmpty(group);
            Assert.All(group, c => { Assert.False(string.IsNullOrWhiteSpace(c.Label)); Assert.False(string.IsNullOrWhiteSpace(c.Snippet)); });
        }
    }

    [Fact]
    public void ViewModel_ApplyRecipe_InjectsUserInput_ResynthesizesAndClosesModal()
    {
        var vm = new PromptInputViewModel(_synth, _svc);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.IsInspirationModalOpen = true;

        var recipe = _svc.GetCuratedPresets()[0];
        vm.ApplyRecipe(recipe);

        Assert.Equal(recipe.FullPrompt, vm.UserInput);
        Assert.False(vm.IsInspirationModalOpen);
        Assert.StartsWith(recipe.FullPrompt, vm.Result.FinalPositivePrompt);
        Assert.Equal(vm.Result.FinalPositivePrompt.Length, vm.CharacterCount);
        Assert.Contains(nameof(PromptInputViewModel.UserInput), raised);
        Assert.Contains(nameof(PromptInputViewModel.IsInspirationModalOpen), raised);
    }

    [Fact]
    public void ViewModel_BuildAndApplyChips_InjectsComposedText_BlankKeepsState()
    {
        var vm = new PromptInputViewModel(_synth, _svc) { UserInput = "기존 입력", IsInspirationModalOpen = true };

        Assert.False(vm.BuildAndApplyChips(null, null, null, null));
        Assert.Equal("기존 입력", vm.UserInput);
        Assert.True(vm.IsInspirationModalOpen);

        Assert.True(vm.BuildAndApplyChips("a cyber ninja", "a neon alley", "fast handheld tracking shot", "neon contrast"));
        Assert.Equal(_svc.BuildFromChips("a cyber ninja", "a neon alley", "fast handheld tracking shot", "neon contrast"), vm.UserInput);
        Assert.False(vm.IsInspirationModalOpen);
    }

    [Fact]
    public void ViewModel_WithoutInspiration_FallsBackToDefaultService()
    {
        var vm = new PromptInputViewModel(_synth);
        Assert.NotNull(vm.Inspiration);
        Assert.True(vm.BuildAndApplyChips("a fluffy golden retriever", null, null, null));
        Assert.StartsWith("Cinematic shot of a fluffy golden retriever", vm.UserInput);
    }
}
