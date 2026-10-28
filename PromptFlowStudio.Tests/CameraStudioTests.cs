using PromptFlowStudio.Services.Camera;
using PromptFlowStudio.Services.Help;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🎥 스마트 카메라 & 모션 통합 — 사이드바 가이드 단일화 · 도움말 라우트(탭 쿼리 포함) 매핑 회귀 잠금.</summary>
public class CameraStudioTests
{
    [Fact]
    public void SidebarGuide_MergesWirelessAndMotionIntoCameraStudio()
    {
        var svc = new SidebarGuideService();
        var g = svc.Get("camera-studio");
        Assert.NotNull(g);
        Assert.Equal("camera-studio", g!.Route);
        Assert.Null(svc.Get("wireless-camera"));
        Assert.Null(svc.Get("motion"));
    }

    [Theory]
    [InlineData("camera-studio")]
    [InlineData("/camera-studio?tab=motion")]
    [InlineData("camera-studio?tab=wireless")]
    public void HelpContent_ResolvesCameraStudioRegardlessOfTab(string path)
    {
        var t = HelpContent.ForPath(path);
        Assert.NotNull(t);
        Assert.Equal("camera-studio", t!.Route);
    }

    [Fact]
    public async Task CaptureFrame_AppendsToRecentCaptures_UpToMaxLimit()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        var vm = new WirelessCameraViewModel(svc);
        await vm.ToggleConnection();
        Assert.True(vm.IsStreaming);

        var ids = new List<Guid>();
        for (var i = 0; i < WirelessCameraViewModel.MaxRecentCaptures + 2; i++)
        {
            await vm.CaptureCurrentFrame();
            ids.Add(vm.SelectedFrame!.FrameId);
        }

        Assert.Equal(WirelessCameraViewModel.MaxRecentCaptures, vm.RecentCaptures.Count);
        Assert.Equal(ids[^1], vm.RecentCaptures[0].FrameId);   // 최신순
        Assert.Equal(ids[2], vm.RecentCaptures[^1].FrameId);   // 가장 오래된 2장 롤오버
        Assert.DoesNotContain(vm.RecentCaptures, f => f.FrameId == ids[0] || f.FrameId == ids[1]);
        Assert.Equal(ids[^1], vm.SelectedFrame!.FrameId);

        // 활성 프레임 변경 · 롤 밖 ID 거부 · 해제 후에도 롤 유지
        Assert.True(vm.SelectFrame(ids[3]));
        Assert.Equal(ids[3], vm.SelectedFrame!.FrameId);
        Assert.False(vm.SelectFrame(ids[0]));
        await vm.ToggleConnection();
        Assert.Equal(WirelessCameraViewModel.MaxRecentCaptures, vm.RecentCaptures.Count);

        // 종횡비 가이드 토글(미지원 값 무시)
        vm.SetAspectGuide("9:16");
        Assert.Equal("9:16", vm.AspectGuide);
        vm.SetAspectGuide("4:3");
        Assert.Equal("9:16", vm.AspectGuide);
    }

    [Fact]
    public void MergeCameraPrompt_ReplacesExistingCameraTokens_WithoutDuplication()
    {
        const string existing = "a girl walking in the rain, cinematic pan left, zoom in slowly, neon lights, Neon Lights";
        var merged = SmartCameraMerge.MergeCameraPrompt(existing, "camera orbiting around the subject", MotionIntensity.Dynamic);

        Assert.Equal("a girl walking in the rain, camera orbiting around the subject, dynamic energetic camera movement, neon lights", merged);
        Assert.DoesNotContain("pan left", merged);
        Assert.DoesNotContain("zoom", merged);

        // 재병합 멱등 — 이전 모션·강도 수식어도 교체(중복 0)
        var again = SmartCameraMerge.MergeCameraPrompt(merged, "cinematic pan right", MotionIntensity.Subtle, 1.5);
        Assert.Equal("a girl walking in the rain, cinematic pan right, subtle gentle camera movement, fast-paced camera motion, neon lights", again);
        Assert.Equal(again, SmartCameraMerge.MergeCameraPrompt(again, "cinematic pan right", MotionIntensity.Subtle, 1.5));

        // 카메라 지시어 없는 기존 프롬프트 → 끝에 추가 · Wide shot/한국어 지시어 감지 · 빈 모션은 기존 유지
        Assert.Equal("a cat, dolly zoom, steady camera movement", SmartCameraMerge.MergeCameraPrompt("a cat", "dolly zoom", MotionIntensity.Normal));
        Assert.Equal("dolly zoom, steady camera movement, a cat", SmartCameraMerge.MergeCameraPrompt("wide shot, a cat", "dolly zoom", MotionIntensity.Normal));
        Assert.Equal("아기, cinematic pan right, steady camera movement", SmartCameraMerge.MergeCameraPrompt("아기, 부드러운 슬로우 카메라 팬", "cinematic pan right", MotionIntensity.Normal));
        Assert.Equal("a cat, slow motion", SmartCameraMerge.MergeCameraPrompt(" a cat, slow motion ", "", MotionIntensity.Dynamic));
    }

    [Fact]
    public async Task TransferToWorkspace_ActivatesI2VMode_AndPreservesPayload()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        var cam = new WirelessCameraViewModel(svc) { Intensity = MotionIntensity.Dynamic, Speed = 0.5 };
        Assert.False(cam.TransferToWorkspace(""));   // 프레임·모션 모두 없음 → 거부
        Assert.False(svc.HasPendingWorkspaceTransfer);

        await cam.ToggleConnection();
        await cam.CaptureCurrentFrame();
        var best = cam.SelectedFrame!;
        await cam.CaptureCurrentFrame();
        Assert.True(cam.SelectFrame(best.FrameId));   // 롤의 이전 컷(DataUrl 복원 경로) 선택

        Assert.True(cam.TransferToWorkspace("camera orbiting around the subject, slow motion"));
        var transfer = svc.TakeWorkspaceTransfer();
        var frame = svc.TakeI2vReference();
        Assert.NotNull(transfer);
        Assert.NotNull(frame);
        Assert.Null(svc.TakeWorkspaceTransfer());   // 1회 소비
        Assert.Equal(MotionIntensity.Dynamic, transfer!.Intensity);
        Assert.Equal(0.5, transfer.Speed);
        Assert.Equal(Convert.FromBase64String(best.FrameDataUrl[(best.FrameDataUrl.IndexOf(',') + 1)..]), frame);

        var ws = new PromptInputViewModel(new PromptSynthesizerService(), new PromptInspirationService()) { UserInput = "a fox in snow, tracking shot following the subject" };
        Assert.False(ws.IsI2V);
        Assert.True(await ws.ApplyCameraTransferAsync(transfer, frame));
        Assert.True(ws.IsI2V);
        Assert.True(ws.I2vModeRequested);
        Assert.Equal(PromptInputViewModel.I2VBadge, ws.StatusBadge);
        Assert.Equal("a fox in snow, camera orbiting around the subject, slow motion, dynamic energetic camera movement, slow-paced camera motion", ws.UserInput);

        ws.RemoveReferenceImage();   // I2V 해제 → 강조 플래그 해제
        Assert.False(ws.I2vModeRequested);
    }
}
