using System;
using System.Collections.Generic;
using System.IO;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>💡 스토리 레시피 &amp; 인스피레이션 허브 — 프리셋 유효성 · 칩 합성 포맷 · 뷰모델 UserInput 주입 회귀 잠금.</summary>
public class PromptInspirationTests
{
    private readonly PromptInspirationService _svc = new();
    private readonly PromptSynthesizerService _synth = new();

    [Fact]
    [Trait("Category", "FastUnit")]
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
    [Trait("Category", "FastUnit")]
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
    [Trait("Category", "FastUnit")]
    public void GetCuratedPresets_ContainsCuteBabyKidsCategory()
    {
        var baby = _svc.GetPresetsByCategory(StoryPresetCategory.CuteBabyKids);
        Assert.Equal(4, baby.Count);
        Assert.Contains(_svc.GetCuratedPresets(), r => r.Category == StoryPresetCategory.CuteBabyKids && r.Title == "햇살 속 첫 걸음마와 까르르 웃음");
        Assert.All(baby, r => Assert.False(string.IsNullOrWhiteSpace(r.RecommendedCamera)));
        Assert.Equal("👶 큐트 베이비", PromptInspirationService.CategoryLabel(StoryPresetCategory.CuteBabyKids));
        Assert.Contains(PromptInspirationService.Subjects, c => c.Label.Contains("앙증맞은 아기"));
        Assert.Contains(PromptInspirationService.Environments, c => c.Label.Contains("파스텔톤 햇살 아기방"));
        Assert.Contains(PromptInspirationService.Lightings, c => c.Label.Contains("파스텔 솜사탕 글로우"));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void BuildFromChips_AllChips_ProducesCinematicFormat()
    {
        var s = _svc.BuildFromChips("a lone astronaut", "a misty fjord", "slow push-in", "golden hour rays");
        Assert.StartsWith("Cinematic shot of a lone astronaut in a misty fjord, camera: slow push-in, lighting: golden hour rays, ", s);
        Assert.Contains("4K", s);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
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
    [Trait("Category", "FastUnit")]
    public void BuildFromChips_AllBlank_ReturnsEmpty_AndOverlongIsTruncated()
    {
        Assert.Equal("", _svc.BuildFromChips(null, " ", "", null));
        var big = _svc.BuildFromChips(new string('x', 2000), null, null, null);
        Assert.Equal(PromptSynthesizerService.MaxCharacterLimit, big.Length);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ChipCatalogs_NonEmpty_WithSnippets()
    {
        foreach (var group in new[] { PromptInspirationService.Subjects, PromptInspirationService.Environments, PromptInspirationService.Motions, PromptInspirationService.Lightings })
        {
            Assert.NotEmpty(group);
            Assert.All(group, c => { Assert.False(string.IsNullOrWhiteSpace(c.Label)); Assert.False(string.IsNullOrWhiteSpace(c.Snippet)); });
        }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
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
    [Trait("Category", "FastUnit")]
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

    private static string TempPresetPath() =>
        Path.Combine(Path.GetTempPath(), "pfs_presets_" + Guid.NewGuid().ToString("N"), PromptInspirationService.CustomPresetFileName);

    private static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SaveCustomPreset_PersistsToFileAndLoadsCorrectly()
    {
        var path = TempPresetPath();
        try
        {
            Assert.Empty(new PromptInspirationService(path).LoadCustomPresets());

            var svc = new PromptInspirationService(path);
            svc.SaveCustomPreset("  내 우주 씬 ", "An astronaut drifting past Saturn rings", "slow push-in");
            svc.SaveCustomPreset("두번째", "A cat on a rooftop at dusk", "");
            Assert.True(File.Exists(path));

            var loaded = new PromptInspirationService(path).LoadCustomPresets();   // 새 인스턴스 = 디스크 재로드
            Assert.Equal(2, loaded.Count);
            Assert.Equal("두번째", loaded[0].Title);   // 최신 우선
            Assert.Equal("내 우주 씬", loaded[1].Title);
            Assert.Equal("An astronaut drifting past Saturn rings", loaded[1].Prompt);
            Assert.Equal("slow push-in", loaded[1].CameraMotion);
            Assert.NotEqual(Guid.Empty, loaded[1].Id);
            Assert.NotEqual(loaded[0].Id, loaded[1].Id);

            var (curated, custom) = svc.GetAllPresets();
            Assert.Same(svc.GetCuratedPresets(), curated);
            Assert.Equal(2, custom.Count);

            Assert.Throws<ArgumentException>(() => svc.SaveCustomPreset(" ", "x", ""));
            Assert.Throws<ArgumentException>(() => svc.SaveCustomPreset("t", "", ""));

            var vm = new PromptInputViewModel(_synth, new PromptInspirationService(path)) { UserInput = "A fox in snow", CameraMotion = "orbit", IsInspirationModalOpen = true };
            Assert.Equal(2, vm.CustomPresets.Count);
            Assert.True(vm.SaveCurrentAsPreset("여우"));
            Assert.Equal(3, vm.CustomPresets.Count);
            vm.UserInput = "다른 입력";
            vm.ApplyCustomPreset(vm.CustomPresets[0]);
            Assert.Equal("A fox in snow", vm.UserInput);
            Assert.Equal("orbit", vm.CameraMotion);
            Assert.False(vm.IsInspirationModalOpen);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DeleteCustomPreset_RemovesTargetPreset()
    {
        var path = TempPresetPath();
        try
        {
            var svc = new PromptInspirationService(path);
            svc.SaveCustomPreset("A", "prompt a", "");
            svc.SaveCustomPreset("B", "prompt b", "");
            var target = svc.LoadCustomPresets()[1];   // "A"

            Assert.True(svc.DeleteCustomPreset(target.Id));
            Assert.False(svc.DeleteCustomPreset(target.Id));
            Assert.False(svc.DeleteCustomPreset(Guid.NewGuid()));

            var reloaded = new PromptInspirationService(path).LoadCustomPresets();
            Assert.Single(reloaded);
            Assert.Equal("B", reloaded[0].Title);

            var vm = new PromptInputViewModel(_synth, new PromptInspirationService(path));
            Assert.True(vm.DeletePreset(reloaded[0].Id));
            Assert.Empty(vm.CustomPresets);
            Assert.Empty(new PromptInspirationService(path).LoadCustomPresets());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LoadCustomPresets_CorruptFile_ReturnsEmptyAndKeepsBackup()
    {
        var path = TempPresetPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            var svc = new PromptInspirationService(path);
            Assert.Empty(svc.LoadCustomPresets());
            Assert.True(File.Exists(path + ".corrupt.bak"));
            svc.SaveCustomPreset("복구", "recovered prompt", "");
            Assert.Single(new PromptInspirationService(path).LoadCustomPresets());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ViewModel_WithoutInspiration_FallsBackToDefaultService()
    {
        var vm = new PromptInputViewModel(_synth);
        Assert.NotNull(vm.Inspiration);
        Assert.True(vm.BuildAndApplyChips("a fluffy golden retriever", null, null, null));
        Assert.StartsWith("Cinematic shot of a fluffy golden retriever", vm.UserInput);
    }
}
