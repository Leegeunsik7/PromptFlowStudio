using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>2번 워크플로우 — 스토리보드 시퀀서(대본 N컷 분할 · 시드/피사체 전파) + 뷰모델(순서 변경 · 총 재생 시간) 회귀 잠금.</summary>
public class StoryboardSequencerTests
{
    private readonly StoryboardSequencerService _seq = new();

    private const string FourLineScript =
        "A baby wakes up in a sunny nursery.\nShe crawls toward a red ball.\nThe puppy joins her on the rug.\nThey fall asleep together at dusk.";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Breakdown_ByLines_ProducesTargetCutCount(int target)
    {
        var cuts = _seq.BreakdownScript(FourLineScript, target);
        Assert.Equal(target, cuts.Count);
        for (var i = 0; i < cuts.Count; i++)
        {
            Assert.Equal(i + 1, cuts[i].CutIndex);
            Assert.Equal(StoryboardCut.DefaultDurationSec, cuts[i].DurationSec);
            Assert.False(string.IsNullOrWhiteSpace(cuts[i].RawPrompt));
            Assert.False(string.IsNullOrWhiteSpace(cuts[i].CameraMotion));
        }
    }

    [Fact]
    public void Breakdown_MergesSegmentsInOrder_WithoutLosingText()
    {
        var cuts = _seq.BreakdownScript(FourLineScript, 2);
        Assert.Equal("A baby wakes up in a sunny nursery. She crawls toward a red ball.", cuts[0].RawPrompt);
        Assert.Equal("The puppy joins her on the rug. They fall asleep together at dusk.", cuts[1].RawPrompt);
    }

    [Fact]
    public void Breakdown_SingleLine_SplitsBySentencePunctuation()
    {
        var cuts = _seq.BreakdownScript("아기가 웃는다. 강아지가 달려온다! 둘이 함께 논다? 노을이 진다…", 4);
        Assert.Equal(4, cuts.Count);
        Assert.Equal("아기가 웃는다.", cuts[0].RawPrompt);
        Assert.Equal("노을이 진다…", cuts[3].RawPrompt);
    }

    [Fact]
    public void Breakdown_KeepsDecimalNumbersIntact()
    {
        var cuts = _seq.BreakdownScript("Zoom 1.5x on the cake. Candles flicker.", 2);
        Assert.Equal(2, cuts.Count);
        Assert.Equal("Zoom 1.5x on the cake.", cuts[0].RawPrompt);
    }

    [Fact]
    public void Breakdown_ShortScript_SplitsLongestAtCommaOrSpace()
    {
        var cuts = _seq.BreakdownScript("a baby laughs, a dog barks", 2);
        Assert.Equal(2, cuts.Count);
        Assert.Equal("a baby laughs,", cuts[0].RawPrompt);
        Assert.Equal("a dog barks", cuts[1].RawPrompt);
    }

    [Fact]
    public void Breakdown_NotEnoughContent_ReturnsFewerCuts()
    {
        Assert.Single(_seq.BreakdownScript("sunrise", 4));
        Assert.Empty(_seq.BreakdownScript("   \n ", 4));
        Assert.Empty(_seq.BreakdownScript(null, 4));
    }

    [Fact]
    public void Breakdown_AssignsCameraMotionAndTransitionsByPosition()
    {
        var cuts = _seq.BreakdownScript(FourLineScript, 4);
        Assert.Equal(StoryboardSequencerService.OpeningMotion, cuts[0].CameraMotion);
        Assert.Equal(StoryboardSequencerService.ClosingMotion, cuts[3].CameraMotion);
        Assert.NotEqual(cuts[1].CameraMotion, cuts[2].CameraMotion);
        Assert.Equal(TransitionType.Cut, cuts[0].TransitionType);
        Assert.Equal(TransitionType.Dissolve, cuts[1].TransitionType);
    }

    [Fact]
    public void Propagate_InheritsMasterSeedAndSubject_ToFollowingCuts()
    {
        var cuts = _seq.BreakdownScript(FourLineScript, 4);
        _seq.PropagateConsistency(cuts, 424242L, "  1-year-old Korean baby girl, pink onesie  ");

        Assert.False(cuts[0].IsSubjectLocked);
        for (var i = 0; i < cuts.Count; i++)
        {
            Assert.Equal(424242L, cuts[i].Seed);
            Assert.Equal("1-year-old Korean baby girl, pink onesie", cuts[i].SubjectIdentity);
            if (i > 0) Assert.True(cuts[i].IsSubjectLocked);
        }
    }

    [Fact]
    public void Propagate_BlankSubject_ClearsIdentity_EmptyListIsNoop()
    {
        var cuts = _seq.BreakdownScript(FourLineScript, 2);
        _seq.PropagateConsistency(cuts, 7L, "  ");
        Assert.Null(cuts[1].SubjectIdentity);
        Assert.Equal(7L, cuts[1].Seed);
        _seq.PropagateConsistency(new List<StoryboardCut>(), 7L, "x");
        Assert.Throws<ArgumentNullException>(() => _seq.PropagateConsistency(null!, 7L, "x"));
    }

    [Fact]
    public void ViewModel_ReceiveScript_ExpandsCuts_AndSumsDuration()
    {
        using var vm = new StoryboardViewModel(_seq) { TargetCutCount = 3 };
        Assert.True(vm.ReceiveGeneratedScript(FourLineScript));
        Assert.Equal(3, vm.Cuts.Count);
        Assert.Equal(15f, vm.TotalDurationSec);
        Assert.False(vm.ReceiveGeneratedScript("  "));
        Assert.Equal(3, vm.Cuts.Count);
    }

    [Fact]
    public void ViewModel_MoveUpDown_ReordersAndRenumbers()
    {
        using var vm = new StoryboardViewModel(_seq);
        vm.ReceiveGeneratedScript(FourLineScript);
        var third = vm.Cuts[2];

        Assert.True(vm.MoveUp(2));
        Assert.Same(third, vm.Cuts[1]);
        Assert.Equal(2, third.CutIndex);
        Assert.True(vm.MoveDown(1));
        Assert.Same(third, vm.Cuts[2]);
        Assert.Equal(3, third.CutIndex);

        Assert.False(vm.MoveUp(0));
        Assert.False(vm.MoveDown(3));
        for (var i = 0; i < vm.Cuts.Count; i++) Assert.Equal(i + 1, vm.Cuts[i].CutIndex);
    }

    [Fact]
    public void ViewModel_TotalDuration_TracksEditsAddRemove()
    {
        using var vm = new StoryboardViewModel(_seq);
        vm.ReceiveGeneratedScript(FourLineScript);
        Assert.Equal(20f, vm.TotalDurationSec);

        var changed = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StoryboardViewModel.TotalDurationSec)) changed++; };

        vm.Cuts[0].DurationSec = 2.5f;
        Assert.Equal(17.5f, vm.TotalDurationSec);

        var added = vm.AddCut("epilogue");
        Assert.Equal(5, added.CutIndex);
        Assert.Equal(22.5f, vm.TotalDurationSec);

        Assert.True(vm.RemoveCut(0));
        Assert.Equal(20f, vm.TotalDurationSec);
        Assert.Equal(1, vm.Cuts[0].CutIndex);
        Assert.False(vm.RemoveCut(99));
        Assert.Equal(3, changed);

        var removed = vm.Cuts[0];
        vm.RemoveCut(removed);
        removed.DurationSec = 100f;   // 해제된 컷 편집은 합계에 영향 없음
        Assert.Equal(15f, vm.TotalDurationSec);
    }

    [Fact]
    public void ViewModel_MasterSeed_RepropagatesAfterReorder()
    {
        using var vm = new StoryboardViewModel(_seq) { MasterSeed = 99L, SubjectIdentity = "golden retriever" };
        vm.ReceiveGeneratedScript(FourLineScript);
        Assert.False(vm.Cuts[0].IsSubjectLocked);
        Assert.True(vm.Cuts[1].IsSubjectLocked);

        vm.MoveUp(1);   // 2번 컷이 1번으로 승격 → 잠금 해제, 기존 1번은 상속 컷으로
        Assert.False(vm.Cuts[0].IsSubjectLocked);
        Assert.True(vm.Cuts[1].IsSubjectLocked);
        foreach (var c in vm.Cuts)
        {
            Assert.Equal(99L, c.Seed);
            Assert.Equal("golden retriever", c.SubjectIdentity);
        }
    }
}
