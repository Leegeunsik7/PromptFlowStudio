using PromptFlowStudio.Services.Help;
using PromptFlowStudio.Services.Ui;

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
}
