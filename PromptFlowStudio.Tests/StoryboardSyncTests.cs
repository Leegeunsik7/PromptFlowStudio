using System.Text.Json;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>2번 워크플로우 컷 덱(StoryboardViewModel) ↔ 프로젝트 코어(StoryboardProject.Scenes) 양방향 동기화 회귀 잠금.</summary>
public class StoryboardSyncTests
{
    private const string FourLineScript =
        "A baby wakes up in a sunny nursery.\nShe crawls toward a red ball.\nThe puppy joins her on the rug.\nThey fall asleep together at dusk.";

    private static StoryboardViewModel NewVm() => new(new StoryboardSequencerService());

    [Fact]
    public void CutDeck_Change_SynchronizesToCoreProjectScenes()
    {
        using var vm = NewVm();
        var project = new StoryboardProject();
        project.Scenes.Add(new StoryboardScene());   // 신규 프로젝트 기본 빈 컷
        vm.ReceiveGeneratedScript(FourLineScript);
        vm.AttachProject(project);

        Assert.Equal(4, project.Scenes.Count);   // 빈 프로젝트 + 수신 덱 → 덱 우선 채움
        for (var i = 0; i < 4; i++) Assert.Equal(vm.Cuts[i].RawPrompt, project.Scenes[i].Prompt);

        var cut = vm.Cuts[2];
        cut.RawPrompt = "edited prompt";
        cut.CameraMotion = "crane up";
        cut.Seed = 424242L;
        cut.DurationSec = 3.5f;
        cut.Script = "새 나레이션";
        cut.TransitionType = TransitionType.WhipPan;

        var scene = project.Scenes[2];
        Assert.Equal("edited prompt", scene.Prompt);
        Assert.Equal("crane up", scene.CameraMotion);
        Assert.Equal(424242L, scene.Seed);
        Assert.Equal(3.5f, scene.DurationSec);
        Assert.Equal("새 나레이션", scene.VoiceText);
        Assert.Equal(TransitionType.WhipPan, scene.CutTransition);

        var keepId = project.Scenes[0].Id;
        vm.AddCut("fifth");
        Assert.Equal(5, project.Scenes.Count);
        Assert.Equal("fifth", project.Scenes[4].Prompt);

        vm.RemoveCut(1);
        Assert.Equal(4, project.Scenes.Count);
        Assert.Same(scene, project.Scenes[1]);   // 씬 인스턴스(런타임 상태) 보존
        Assert.Equal(keepId, project.Scenes[0].Id);

        vm.MoveDown(0);
        Assert.Equal(keepId, project.Scenes[1].Id);
        for (var i = 0; i < vm.Cuts.Count; i++) Assert.Equal(vm.Cuts[i].RawPrompt, project.Scenes[i].Prompt);
    }

    [Fact]
    public void LoadProject_PopulatesCutDeckCorrectly()
    {
        using var vm = NewVm();
        vm.ReceiveGeneratedScript(FourLineScript);   // 기존 덱 존재 — 작성된 프로젝트 로드 시 프로젝트 우선
        var project = new StoryboardProject();
        for (var i = 0; i < 4; i++)
            project.Scenes.Add(new StoryboardScene
            {
                Prompt = "p" + i, VoiceText = "v" + i, CameraMotion = "cam" + i,
                Seed = 1000L + i, DurationSec = 2f + i, CutTransition = TransitionType.ZoomIn,
            });
        project.Scenes.Add(new StoryboardScene { Prompt = "legacy" });   // 덱 필드 없는 구버전 씬

        vm.AttachProject(project);

        Assert.Same(project, vm.Project);
        Assert.Equal(5, vm.Cuts.Count);
        for (var i = 0; i < 4; i++)
        {
            var c = vm.Cuts[i];
            Assert.Equal(i + 1, c.CutIndex);
            Assert.Equal("p" + i, c.RawPrompt);
            Assert.Equal("v" + i, c.Script);
            Assert.Equal("cam" + i, c.CameraMotion);
            Assert.Equal(1000L + i, c.Seed);
            Assert.Equal(2f + i, c.DurationSec);
            Assert.Equal(TransitionType.ZoomIn, c.TransitionType);
        }
        Assert.Equal(StoryboardCut.DefaultDurationSec, vm.Cuts[4].DurationSec);
        Assert.Equal(StoryboardSequencerService.ClosingMotion, vm.Cuts[4].CameraMotion);   // 위치 기반 기본 카메라
        Assert.Equal(2f + 3f + 4f + 5f + StoryboardCut.DefaultDurationSec, vm.TotalDurationSec, 3);

        // 외부(기존 컷 목록) 직접 편집 → 역복원(카드 인스턴스 유지)
        var first = vm.Cuts[0];
        project.Scenes[0].Prompt = "external";
        vm.PullFromProject();
        Assert.Same(first, vm.Cuts[0]);
        Assert.Equal("external", first.RawPrompt);

        project.Scenes.RemoveAt(4);
        vm.PullFromProject();
        Assert.Equal(4, vm.Cuts.Count);
    }

    [Fact]
    public void SaveProject_PersistsAllDeckModifications()
    {
        using var vm = NewVm();
        var project = new StoryboardProject { Title = "sync" };
        project.Scenes.Add(new StoryboardScene { Prompt = "a" });
        project.Scenes.Add(new StoryboardScene { Prompt = "b" });
        vm.AttachProject(project);

        vm.Cuts[0].RawPrompt = "a2";
        vm.Cuts[1].Seed = 77L;
        vm.Cuts[1].Script = "대사";
        vm.AddCut("c").DurationSec = 4f;
        vm.FlushToProject();

        var json = JsonSerializer.Serialize(project);
        var loaded = JsonSerializer.Deserialize<StoryboardProject>(json);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Scenes.Count);
        Assert.Equal("a2", loaded.Scenes[0].Prompt);
        Assert.Equal(77L, loaded.Scenes[1].Seed);
        Assert.Equal("대사", loaded.Scenes[1].VoiceText);
        Assert.Equal("c", loaded.Scenes[2].Prompt);
        Assert.Equal(4f, loaded.Scenes[2].DurationSec);

        using var vm2 = NewVm();
        vm2.AttachProject(loaded);
        Assert.Equal(3, vm2.Cuts.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(vm.Cuts[i].RawPrompt, vm2.Cuts[i].RawPrompt);
            Assert.Equal(vm.Cuts[i].Seed, vm2.Cuts[i].Seed);
            Assert.Equal(vm.Cuts[i].Script, vm2.Cuts[i].Script);
            Assert.Equal(vm.Cuts[i].CameraMotion, vm2.Cuts[i].CameraMotion);
            Assert.Equal(vm.Cuts[i].DurationSec, vm2.Cuts[i].DurationSec);
        }

        vm.AttachProject(null);   // 해제 후 편집은 프로젝트에 미반영
        vm.Cuts[0].RawPrompt = "detached";
        Assert.Equal("a2", project.Scenes[0].Prompt);
    }
}
