using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>3번 워크플로우 — 대량 양산 배치 전개 · 렌더 비용/VRAM 추산 · 뷰모델 큐 전송 회귀 잠금.</summary>
public class MassProductionAndCostTests
{
    private readonly RenderCostEstimatorService _est = new();
    private readonly BatchMassProductionService _batch = new();

    [Theory]
    [InlineData("1280x720", 5.0f, 1, 24, 120, 6.5, 10)]
    [InlineData("1280x720", 10.0f, 3, 24, 720, 6.5, 60)]
    [InlineData("1920x1080", 5.0f, 2, 24, 240, 11.2, 44)]
    [InlineData("854x480", 5.0f, 1, 30, 150, 4.2, 6)]
    [InlineData("3840x2160", 5.0f, 1, 24, 120, 24.0, 80)]
    public void Estimate_ValidRequest_CalculatesCorrectCreditsAndVram(string res, float dur, int jobs, int fps, int frames, double vram, int credits)
    {
        var r = _est.Estimate(new CostEstimateRequest(res, dur, jobs, fps));
        Assert.Equal(frames, r.TotalFrames);
        Assert.Equal(vram, r.EstimatedVramGb, 3);
        Assert.Equal(credits, r.EstimatedCredits);
        Assert.True(r.EstimatedRenderTimeSec > 0);
    }

    [Fact]
    public void Estimate_HigherResolution_TakesLongerPerFrame()
    {
        var hd = _est.Estimate(new CostEstimateRequest("1280x720", 5f, 1));
        var fhd = _est.Estimate(new CostEstimateRequest("1920x1080", 5f, 1));
        Assert.Equal(120 * RenderCostEstimatorService.BaseSecPerFrame, hd.EstimatedRenderTimeSec, 6);
        Assert.True(fhd.EstimatedRenderTimeSec > hd.EstimatedRenderTimeSec);
    }

    [Fact]
    public void GenerateBatchJobs_MultilinePrompts_CreatesCorrectJobCountAndSeeds()
    {
        var jobs = _batch.GenerateBatchJobs("cat on roof\r\n\n  dog in park  \n\n bird at sea\n", 100, true, "1920x1080", 4f);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(new[] { "cat on roof", "dog in park", "bird at sea" }, jobs.ConvertAll(j => j.Prompt));
        Assert.Equal(new long[] { 100, 101, 102 }, jobs.ConvertAll(j => j.Seed));
        Assert.All(jobs, j =>
        {
            Assert.Equal("1920x1080", j.Resolution);
            Assert.Equal(4f, j.DurationSec);
            Assert.Equal(BatchJobStatus.Pending, j.Status);
            Assert.NotEqual(Guid.Empty, j.JobId);
        });
        Assert.Equal(3, jobs.Select(j => j.JobId).Distinct().Count());

        var fixedSeed = _batch.GenerateBatchJobs("a\nb", 7, false, "1280x720", 5f);
        Assert.All(fixedSeed, j => Assert.Equal(7, j.Seed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\r\n  \n")]
    public void GenerateBatchJobs_EmptyPrompt_ReturnsEmptyList(string? input)
    {
        var jobs = _batch.GenerateBatchJobs(input, 1, true, "1280x720", 5f);
        Assert.NotNull(jobs);
        Assert.Empty(jobs);
        Assert.Equal(0, BatchMassProductionService.CountPrompts(input));
    }

    [Theory]
    [InlineData(0f, 0, 0)]
    [InlineData(-3f, -5, -24)]
    [InlineData(float.NaN, 0, 0)]
    public void Estimate_ZeroOrNegativeValues_ClampsToSafeDefaults(float dur, int jobs, int fps)
    {
        var r = _est.Estimate(new CostEstimateRequest("garbage", dur, jobs, fps));
        Assert.Equal(120, r.TotalFrames);       // 5초 × 24fps × 1건
        Assert.Equal(6.5, r.EstimatedVramGb, 3); // 파싱 실패 → 720p
        Assert.Equal(10, r.EstimatedCredits);
    }

    [Fact]
    public void ViewModel_PromptChange_UpdatesEstimate_AndDispatchQueuesJobs()
    {
        var vm = new MassProductionViewModel(_batch, _est);
        Assert.Equal(0, vm.JobCount);
        Assert.Equal(0, vm.TotalEstimatedCredits);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.PromptInputText = "one\ntwo\n\nthree";
        Assert.Equal(3, vm.JobCount);
        Assert.Equal(30, vm.TotalEstimatedCredits);
        Assert.Equal("6.5 GB", vm.EstimatedVramDisplay);
        Assert.Contains(nameof(vm.TotalEstimatedCredits), changed);

        vm.SelectedResolution = "1920x1080";
        Assert.Equal(66, vm.TotalEstimatedCredits);
        Assert.Equal("11.2 GB", vm.EstimatedVramDisplay);

        vm.BaseSeed = 42;
        Assert.Equal(3, vm.DispatchAllToRenderQueue());
        Assert.Equal(3, vm.RenderQueue.Count);
        Assert.All(vm.RenderQueue, j => Assert.Equal(BatchJobStatus.Queued, j.Status));
        Assert.Equal(44, vm.RenderQueue[2].Seed);
    }

    [Fact]
    public void ViewModel_GeneratedJobs_PreviewTracksInput_AndFlipsToQueuedOnDispatch()
    {
        var vm = new MassProductionViewModel(_batch, _est);
        Assert.Empty(vm.GeneratedJobs);

        vm.PromptInputText = "a\nb\nc";
        vm.SelectedResolution = "1920x1080";
        Assert.Equal(405.0, vm.EstimatedRenderTimeSec, 6);
        vm.Fps = 30;
        Assert.Equal(506.25, vm.EstimatedRenderTimeSec, 6);

        vm.BaseSeed = 10;
        Assert.Equal(3, vm.GeneratedJobs.Count);
        Assert.All(vm.GeneratedJobs, j => Assert.Equal(BatchJobStatus.Pending, j.Status));
        Assert.Equal(12, vm.GeneratedJobs[2].Seed);
        vm.IncrementSeed = false;
        Assert.Equal(10, vm.GeneratedJobs[2].Seed);

        vm.DispatchAllToRenderQueue();
        Assert.All(vm.GeneratedJobs, j => Assert.Equal(BatchJobStatus.Queued, j.Status));
        Assert.Same(vm.RenderQueue[0], vm.GeneratedJobs[0]);
    }
}
