using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>🎬 작업실 주 생성 버튼 — 프롬프트 전용(T2V, 0장) · 이미지 보조(I2V) 준비도 · 엔진 미구성 레드액션 안내 · Pluto 불변식(9/6/2).</summary>
public class WorkspaceGenerationReadinessTests
{
    private const string AmbientPrompt = "soft morning light over a sleeping baby, slow ambient drift";
    private const string Img = @"C:\media\_uploads\ref.png";

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData(VideoDurationMode.Sec15_AmbientSlow, false)]
    [InlineData(VideoDurationMode.Sec15_AmbientSlow, true)]
    [InlineData(VideoDurationMode.Sec10_Dual, false)]
    [InlineData(VideoDurationMode.Sec15_Triple, false)]
    [InlineData(VideoDurationMode.Sec5_Single, true)]
    public void PromptOnly_ZeroImages_EnablesButton_AndStarts(VideoDurationMode mode, bool wanEnabled)
    {
        Assert.Equal(WorkspaceGenerationPath.TextToVideo, WorkspaceGenerationReadiness.ResolvePath(AmbientPrompt, null));
        Assert.True(WorkspaceGenerationReadiness.CanPress(false, AmbientPrompt, null));
        Assert.Equal(WorkspaceStartBlock.None, WorkspaceGenerationReadiness.Evaluate(false, AmbientPrompt, null, mode, wanEnabled));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PromptOnly_ButtonNotLockedByMissingEngineKey()
    {
        // 엔진 키 미구성이어도 버튼은 활성 — 클릭 시 직결(5초) 경로만 안내로 차단.
        Assert.True(WorkspaceGenerationReadiness.CanPress(false, AmbientPrompt, null));
        Assert.Equal(WorkspaceStartBlock.DirectEngineUnavailable,
            WorkspaceGenerationReadiness.Evaluate(false, AmbientPrompt, null, VideoDurationMode.Sec5_Single, false));
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("hero shot")]
    public void ImageAssisted_EnablesWithOrWithoutPrompt(string? prompt)
    {
        Assert.Equal(WorkspaceGenerationPath.ImageToVideo, WorkspaceGenerationReadiness.ResolvePath(prompt, Img));
        Assert.True(WorkspaceGenerationReadiness.CanPress(false, prompt, Img));
        Assert.Equal(WorkspaceStartBlock.None, WorkspaceGenerationReadiness.Evaluate(false, prompt, Img, VideoDurationMode.Sec5_Single, true));
        Assert.Equal(WorkspaceStartBlock.None, WorkspaceGenerationReadiness.Evaluate(false, prompt, Img, VideoDurationMode.Sec15_Triple, false));
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", " ")]
    public void NoInput_DisablesButton_WithMissingInputMessage(string? prompt, string? image)
    {
        Assert.False(WorkspaceGenerationReadiness.CanPress(false, prompt, image));
        var block = WorkspaceGenerationReadiness.Evaluate(false, prompt, image, VideoDurationMode.Sec15_AmbientSlow, true);
        Assert.Equal(WorkspaceStartBlock.MissingInput, block);
        Assert.Equal(WorkspaceGenerationReadiness.MissingInputMessage, WorkspaceGenerationReadiness.BlockMessage(block));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Busy_DisablesButton_WithoutMessage()
    {
        Assert.False(WorkspaceGenerationReadiness.CanPress(true, AmbientPrompt, Img));
        var block = WorkspaceGenerationReadiness.Evaluate(true, AmbientPrompt, Img, VideoDurationMode.Sec5_Single, true);
        Assert.Equal(WorkspaceStartBlock.Busy, block);
        Assert.Null(WorkspaceGenerationReadiness.BlockMessage(block));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void EngineUnavailable_MessageIsRedactedAndNonEmpty()
    {
        var msg = WorkspaceGenerationReadiness.BlockMessage(WorkspaceStartBlock.DirectEngineUnavailable);
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.Equal(PlutoAutonomousActivityFeedService.Redact(WorkspaceGenerationReadiness.DirectEngineUnavailableMessage), msg);
        Assert.Contains("Wan", msg, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void OnlySingleClipUsesDirectEngine()
    {
        Assert.False(WorkspaceGenerationReadiness.UsesRenderQueue(VideoDurationMode.Sec5_Single));
        Assert.True(WorkspaceGenerationReadiness.UsesRenderQueue(VideoDurationMode.Sec10_Dual));
        Assert.True(WorkspaceGenerationReadiness.UsesRenderQueue(VideoDurationMode.Sec15_Triple));
        Assert.True(WorkspaceGenerationReadiness.UsesRenderQueue(VideoDurationMode.Sec15_AmbientSlow));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }
}
