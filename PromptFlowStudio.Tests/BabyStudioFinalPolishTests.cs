using System;
using System.IO;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>Step 5: 헤더 증시 잔재 제거 · 캔버스 오버레이 정리 · 단일 신호등 상태 배지.</summary>
public class BabyStudioFinalPolishTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    private static string Blazor(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", Path.Combine(parts)));

    private static string Slice(string s, string start, string end)
    {
        var a = s.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, "시작 마커 누락: " + start);
        var b = s.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, "끝 마커 누락: " + end);
        return s.Substring(a, b - a);
    }

    [Fact]
    public void Workspace_CanvasOverlay_RemovesStockHudAndMovingAverages()
    {
        var ws = Blazor("Pages", "Workspace.razor");
        var tools = Slice(ws, "<span class=\"ws-hts-tools\">", "<span class=\"ws-badge @PhaseClass\">");

        Assert.DoesNotContain("HTS HUD", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("이평선", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("이동평균", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("_htsHud = !_htsHud", ws, StringComparison.Ordinal);
        Assert.DoesNotContain("<HtsHudOverlay", ws, StringComparison.Ordinal);

        Assert.Contains("data-canvas-tool=\"growth-badge\"", tools, StringComparison.Ordinal);
        Assert.Contains("🏷️ 성장 뱃지/자막", tools, StringComparison.Ordinal);
        Assert.Contains("data-canvas-tool=\"bgm\"", tools, StringComparison.Ordinal);
        Assert.Contains("🎵 감성 BGM", tools, StringComparison.Ordinal);
        Assert.Contains("data-canvas-tool=\"lut\"", tools, StringComparison.Ordinal);
        Assert.Contains("🎨 톤매핑/LUT", tools, StringComparison.Ordinal);

        // 작업실 상단 행: KRX 세션 배지 · StockSuite 모니터 · IPC 배지 제거
        var head = Slice(ws, "<header class=\"ws-head\">", "</header>");
        Assert.DoesNotContain("ws-trade-guard", head, StringComparison.Ordinal);
        Assert.DoesNotContain("StockSuite", head, StringComparison.Ordinal);
        Assert.DoesNotContain("ws-ipc", head, StringComparison.Ordinal);
    }

    [Fact]
    public void MainLayout_Header_DoesNotExposeKrxTradingClocks()
    {
        var header = Slice(Blazor("MainLayout.razor"), "<header class=\"topbar\"", "</header>");

        Assert.DoesNotContain("<MasterTransportBar", header, StringComparison.Ordinal);
        Assert.DoesNotContain("KRX", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Krx", header, StringComparison.Ordinal);
        Assert.DoesNotContain("TradingSafetyGuard", header, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"ws-trade-guard", header, StringComparison.Ordinal);
        Assert.DoesNotContain("stocksuite", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("09:00", header, StringComparison.Ordinal);

        Assert.Contains("data-header-brand", header, StringComparison.Ordinal);
        Assert.Contains("PromptFlow Studio", header, StringComparison.Ordinal);
        Assert.Contains("아기 성장 &amp; 감성 영상 시네마틱 스튜디오", header, StringComparison.Ordinal);
        Assert.Contains("🟢 준비 완료", header, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"ToggleSidebar\"", header, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ok", false, 0, "ok", "🟢 아기 스튜디오 엔진 정상 가동 중")]
    [InlineData("warn", false, 1, "warn", "🟡 렌더링 리소스 집중 중")]
    [InlineData("crit", false, 1, "warn", "🟡 렌더링 리소스 집중 중")]
    [InlineData("ok", true, 2, "crit", "🔴 시스템 점검 필요")]
    public void StatusFooter_TrafficLight_MapsLoadAndErrors(string level, bool error, int light, string css, string text)
    {
        var l = PromptFlowStudio.Blazor.Components.Common.GpuTelemetryBadge.LightFrom(level, error);
        Assert.Equal(light, l);
        Assert.Equal(css, PromptFlowStudio.Blazor.Components.Common.GpuTelemetryBadge.LightCss(l));
        Assert.Equal(text, PromptFlowStudio.Blazor.Components.Common.GpuTelemetryBadge.LightText(l));
    }

    [Fact]
    public void StatusFooter_RawGpuNumbers_OnlyInsidePopover()
    {
        var badge = Blazor("Components", "Common", "GpuTelemetryBadge.razor");
        var button = Slice(badge, "<button type=\"button\" class=\"sys-light", "</button>");
        Assert.DoesNotContain("_text", button, StringComparison.Ordinal);
        Assert.Contains("_open = !_open", button, StringComparison.Ordinal);
        Assert.Contains("sys-light-pop", badge, StringComparison.Ordinal);
    }
}
