using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Shorts;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>AI 15초 숏폼 스튜디오 — 15초 고정 길이 · 9:16 세로 구도 토큰 · 렌더 큐 3컷 전송 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class AiShortsTests
{
    [Fact]
    public void GenerateSequence_AlwaysYields15SecondsTotalDuration()
    {
        var svc = new AiShortsService();
        foreach (var bgm in Enum.GetValues<BgmBeatPreset>())
        {
            var cuts = svc.GenerateShortsSequence(new ShortsRequest("아침 루틴", ShortsHookType.ShockingFact, bgm, CaptionStyle.NeonCyber));

            Assert.Equal(3, cuts.Count);
            Assert.Equal(new[] { 1, 2, 3 }, cuts.Select(c => c.CutIndex));
            Assert.Equal(new[] { 3.0f, 9.0f, 3.0f }, cuts.Select(c => c.DurationSec));
            Assert.Equal(15.0f, cuts.Sum(c => c.DurationSec));
            Assert.Equal(new[] { 0f, 3f, 12f }, cuts.Select(c => c.StartSec));
            Assert.Equal("Hook [0-3s]", cuts[0].Phase);
            Assert.Equal("Climax/CTA [12-15s]", cuts[2].Phase);

            // BGM 박 정렬 — 마지막 전환 박 = 15초 × BPM / 60
            var bpm = AiShortsService.BpmFor(bgm);
            Assert.All(cuts, c => Assert.Equal(bpm, c.BgmBpm));
            Assert.Equal(15 * bpm / 60, cuts[2].TransitionBeatIndex);
            Assert.All(cuts, c => Assert.False(string.IsNullOrEmpty(c.TransitionFx)));
        }
    }

    [Fact]
    public void GenerateSequence_InjectsVerticalCompositionTokens()
    {
        var cuts = new AiShortsService().GenerateShortsSequence(
            new ShortsRequest("편의점 신상 디저트", ShortsHookType.QuestionHook, BgmBeatPreset.HyperBeat, CaptionStyle.BoldYellow, "1280x720"));

        Assert.All(cuts, c =>
        {
            Assert.Contains(AiShortsService.VerticalToken, c.VisualPrompt);
            Assert.Contains("편의점 신상 디저트", c.VisualPrompt);
            Assert.Contains("safe zone", c.VisualPrompt);
            Assert.Contains(c.CameraMotion, c.VisualPrompt);
            Assert.Equal("720x1280", c.Resolution);   // 가로형 입력 → 세로형 정규화
        });
        Assert.Equal("Fast Push-In", cuts[0].CameraMotion);
        Assert.Equal("Dynamic Orbit", cuts[1].CameraMotion);
        Assert.Equal("Snap Zoom Out", cuts[2].CameraMotion);
        Assert.Contains("bold yellow", cuts[0].VisualPrompt);
    }

    [Fact]
    public void DispatchToRenderQueue_Enqueues3JobsWithVerticalResolution()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var vm = new AiShortsViewModel(new AiShortsService(), q)
        {
            TopicInput = "고양이 하루",
            SelectedHook = ShortsHookType.RelatableDaily,
            SelectedBgm = BgmBeatPreset.LofiChill,
            SelectedCaption = CaptionStyle.MinimalWhite,
        };
        var changed = 0;
        vm.Changed += () => changed++;

        Assert.Equal(15.0f, vm.TotalDurationSec);
        vm.GenerateSequence();
        Assert.Equal(3, vm.Cuts.Count);

        Assert.Equal(3, vm.DispatchToRenderQueue());
        Assert.Equal(3, q.Queue.Count);
        Assert.All(q.Queue, j =>
        {
            Assert.Equal(BatchJobStatus.Queued, j.Status);
            Assert.Equal("720x1280", j.Resolution);
            Assert.Contains(AiShortsService.VerticalToken, j.Prompt);
        });
        Assert.Equal(15.0f, q.Queue.Sum(j => j.DurationSec));
        Assert.Equal(3, vm.DispatchedCount);

        // 재전송은 새 JobId — 큐 중복 차단에 걸리지 않음
        Assert.Equal(3, vm.DispatchToRenderQueue());
        Assert.Equal(6, q.Queue.Select(j => j.JobId).Distinct().Count());
        Assert.Equal(6, vm.DispatchedCount);
        Assert.True(changed >= 3);
    }

    [Fact]
    public void Shorts_SendToRenderQueue_Enforces9x16ResolutionAndBpmMetadata()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        foreach (var bgm in Enum.GetValues<BgmBeatPreset>())
        {
            var vm = new AiShortsViewModel(new AiShortsService(), q)
            {
                TopicInput = "BPM " + bgm,
                SelectedBgm = bgm,
                TargetResolution = "1280x720",   // 16:9 입력도 9:16 으로 잠금
            };
            vm.GenerateSequence();
            var before = q.Queue.Count;
            Assert.Equal(3, vm.DispatchToRenderQueue());

            var jobs = q.Queue.Skip(before).ToList();
            var bpm = AiShortsService.BpmFor(bgm);
            Assert.Equal(3, jobs.Count);
            for (var i = 0; i < 3; i++)
            {
                var j = jobs[i];
                var cut = vm.Cuts[i];
                Assert.Equal("720x1280", j.Resolution);
                Assert.Equal(i + 1, j.CutIndex);
                Assert.Equal(cut.StartSec, j.StartSec);
                Assert.Equal(bpm, j.BgmBpm);
                Assert.Equal(cut.TransitionBeatIndex, j.TransitionBeatIndex);
                Assert.False(string.IsNullOrEmpty(j.TransitionFx));
                Assert.Equal(cut.Seed, j.Seed);
            }
            Assert.Equal(15 * bpm / 60, jobs[2].TransitionBeatIndex);
            Assert.Equal("Hold on Final Beat", jobs[2].TransitionFx);
        }
        Assert.Equal(new[] { 140, 80, 120, 100 }, new[] { BgmBeatPreset.HyperBeat, BgmBeatPreset.LofiChill, BgmBeatPreset.TrendingPop, BgmBeatPreset.DramaticDrop }.Select(AiShortsService.BpmFor));
    }
}
