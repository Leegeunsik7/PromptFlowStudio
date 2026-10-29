using System;
using System.Collections.Generic;
using System.Threading;
using PromptFlowStudio.Services.AI.Vision;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Operations;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.Tests;

/// <summary>v14.0.1: 배포 후 관제 가드레일 - 구독자 예외 격리·파기 후 잔류 호출·로그 홍수 억제.</summary>
[Trait("Category", "FastUnit")]
public class PostReleaseMonitoringTests
{
    private static LiveStreamVirtualCamPipe Pipe()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        return new LiveStreamVirtualCamPipe("Local\\pfs_test_prm_map_" + tag, "Local\\pfs_test_prm_mtx_" + tag);
    }

    private static byte[] Solid(int w, int h, byte v)
    {
        var f = new byte[w * h * 4];
        Array.Fill(f, v);
        return f;
    }

    [Fact]
    public void Canary_ThrowingSubscriber_DoesNotBreakProbe_AndLaterSubscribersStillNotified()
    {
        var vault = new SecureSecretVault();
        var visualizer = new OtlpTelemetryVisualizerProvider(new ProductionTelemetryExporter(new EnterpriseAuditTelemetryAggregator(), vault));
        var tuner = new LivePipelineAutoTuner(new AutoTunerHardwareCalibrator());
        using var canary = new OperationsCanaryHealthChecker(vault, visualizer, tuner);

        var notified = 0;
        canary.OnChange += () => throw new InvalidOperationException("[test] 파기된 HUD 렌더");
        canary.OnChange += () => notified++;

        var report = canary.RunProbe();
        Assert.Equal(3, report.Signals.Count);
        Assert.Equal(1, notified);           // 앞 구독자 예외에도 뒤 구독자 통지
        Assert.Equal(1, canary.Probes);
        canary.RunProbe();
        Assert.Equal(2, notified);           // 다음 프로브도 정상 지속
    }

    [Fact]
    public void Analyzer_ThrowingSubscribers_AreIsolated_OthersNotified_AndFaultsCounted()
    {
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, vlm: null);
        var scenes = 0; var changes = 0;
        analyzer.OnScene += _ => throw new InvalidOperationException("[test] 바인더 결함");
        analyzer.OnScene += _ => scenes++;
        analyzer.OnChange += () => throw new ObjectDisposedException("[test] HUD");
        analyzer.OnChange += () => changes++;

        pipe.SubmitFrame(Solid(64, 36, 80), 64, 36, VirtualCamPixelFormat.Rgba32);
        Assert.True(analyzer.Tick());        // 예외가 틱 밖으로 새지 않음
        Assert.Equal(1, scenes);
        Assert.Equal(1, changes);
        Assert.Equal(2, analyzer.SubscriberFaults);
        Assert.Equal(1, analyzer.Samples);
    }

    [Fact]
    public void Analyzer_Unsubscribe_RemovesOnlyThatHandler()
    {
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, vlm: null);
        var a = 0; var b = 0;
        Action<VisionSceneResult> ha = _ => a++, hb = _ => b++;
        analyzer.OnScene += ha; analyzer.OnScene += hb;
        analyzer.OnScene -= ha;
        analyzer.OnScene -= ha;              // 중복 해지 무해

        pipe.SubmitFrame(Solid(64, 36, 90), 64, 36, VirtualCamPixelFormat.Rgba32);
        analyzer.Tick();
        Assert.Equal(0, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void Analyzer_TickWithSubscribers_StaysZeroAllocation()
    {
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, vlm: null);
        var scenes = 0; var changes = 0;
        analyzer.OnScene += _ => scenes++;
        analyzer.OnChange += () => changes++;
        var x = Solid(64, 36, 70); var y = Solid(64, 36, 150);
        var n = 0;

        var allocated = AllocationProbe.MinAllocated(() =>
        {
            for (var i = 0; i < 60; i++) { pipe.SubmitFrame((n++ & 1) == 0 ? x : y, 64, 36, VirtualCamPixelFormat.Rgba32); analyzer.Tick(); }
        });
        Assert.Equal(0, allocated);          // 격리 배열 순회는 무할당(구독/해지 시에만 재구성)
        Assert.True(scenes > 0 && changes > 0);
    }

    [Fact]
    public void Analyzer_TickAfterDispose_ReturnsFalse_WithoutReRentingBuffer()
    {
        using var pipe = Pipe();
        var analyzer = new VisionContextAnalyzer(pipe, vlm: null);
        pipe.SubmitFrame(Solid(64, 36, 60), 64, 36, VirtualCamPixelFormat.Rgba32);
        Assert.True(analyzer.Tick());
        analyzer.Dispose();
        pipe.SubmitFrame(Solid(64, 36, 61), 64, 36, VirtualCamPixelFormat.Rgba32);
        Assert.False(analyzer.Tick());
        Assert.Equal(1, analyzer.Samples);
    }

    [Fact]
    public void LogFloodGuard_SuppressesRepeatsWithinWindow_ThenReportsSkippedCount()
    {
        var t = new Dictionary<string, (long AtMs, int Suppressed)>(StringComparer.Ordinal);
        Assert.True(PromptFlowStudio.Program.ShouldEmitLog(t, "k", 1_000, out var s0));
        Assert.Equal(0, s0);
        for (var i = 1; i <= 5; i++) Assert.False(PromptFlowStudio.Program.ShouldEmitLog(t, "k", 1_000 + i * 1_000, out _));
        Assert.True(PromptFlowStudio.Program.ShouldEmitLog(t, "other", 2_000, out _));   // 다른 키는 독립
        Assert.True(PromptFlowStudio.Program.ShouldEmitLog(t, "k", 61_000, out var s1));
        Assert.Equal(5, s1);                 // 재기록 시 생략 건수 보고
        Assert.False(PromptFlowStudio.Program.ShouldEmitLog(t, "k", 62_000, out _));
    }
}
