using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Live;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>24/7 AI 라이브 스튜디오 — 5단계 피치 생성 · 과장광고 보정 · 릴레이 무중단 순환 · 렌더 큐 디스패치 회귀 잠금.</summary>
public class LiveStudioHostTests
{
    private static LiveProductSpec Spec(params string[] points) =>
        new("저자극 수분 크림", 40000m, 25, new List<string>(points), PromotionType.TimeAttack);

    [Fact]
    public void GeneratePitchSequence_CreatesComplete5PhaseFlow()
    {
        var host = new LiveStudioHostService();
        var segs = host.GeneratePitchSequence(Spec("72시간 보습", "무향 포뮬러", "대용량 리필"));

        Assert.Equal(5, segs.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal((LivePitchPhase)i, segs[i].Phase);
            Assert.Equal(LiveSegmentKind.Pitch, segs[i].Kind);
            Assert.NotEqual(Guid.Empty, segs[i].SegmentId);
            Assert.False(string.IsNullOrWhiteSpace(segs[i].HostScript));
            Assert.InRange(segs[i].HostScript.Length, 60, 400);   // 20~30초 낭독 분량
            Assert.StartsWith("Wan2.1", segs[i].VisualPrompt);
            Assert.False(string.IsNullOrWhiteSpace(segs[i].RecommendedCameraMotion));
        }
        Assert.Equal(5, segs.Select(s => s.SegmentId).Distinct().Count());
        Assert.Contains("72시간 보습", segs[2].HostScript);
        Assert.Contains("30,000원", segs[4].HostScript);   // 40,000 × (1 − 25%)
        Assert.Equal(5, host.BufferDepth);
    }

    [Fact]
    public void ComplianceFilter_SanitizesExaggeratedClaims()
    {
        Assert.True(LiveStudioHostService.ContainsViolation("아토피 100% 완치!"));
        Assert.Equal("많은 분들이 만족한 품질", LiveStudioHostService.SanitizeClaims("무조건 최고 품질"));
        Assert.Equal("", LiveStudioHostService.SanitizeClaims(null));

        var host = new LiveStudioHostService();
        var segs = host.GeneratePitchSequence(new LiveProductSpec("기적의 크림", 10000m, 10,
            new List<string> { "아토피 100% 완치", "무조건 최고 보습", "부작용 없는 성분" }, PromotionType.FlashSale));

        Assert.All(segs, s =>
        {
            Assert.False(LiveStudioHostService.ContainsViolation(s.HostScript), s.HostScript);
            Assert.False(LiveStudioHostService.ContainsViolation(s.VisualPrompt), s.VisualPrompt);
        });
        Assert.Contains("꾸준한 관리에 도움", segs[2].HostScript);
        Assert.Contains("주목받는 크림", segs[0].HostScript);
    }

    [Fact]
    public void LiveRelay_LoopsSeamlessly_WhenSegmentsExhausted()
    {
        var host = new LiveStudioHostService();
        Assert.Equal(LiveSegmentKind.BgmLoop, host.GetNextSegment().Kind);   // 피치 미생성에도 송출 공백 없음

        var segs = host.GeneratePitchSequence(Spec("72시간 보습"));
        for (var i = 0; i < 5; i++) Assert.Same(segs[i], host.GetNextSegment());

        // 1사이클: FAQ 컷 → 피치 재송출
        var interlude1 = host.GetNextSegment();
        Assert.Equal(LiveSegmentKind.Faq, interlude1.Kind);
        Assert.Equal(1, host.CycleCount);
        for (var i = 0; i < 5; i++) Assert.Same(segs[i], host.GetNextSegment());

        // 2사이클: BGM 루프 컷 → 피치 재송출
        var interlude2 = host.GetNextSegment();
        Assert.Equal(LiveSegmentKind.BgmLoop, interlude2.Kind);
        Assert.Equal(2, host.CycleCount);

        for (var i = 0; i < 120; i++) Assert.NotNull(host.GetNextSegment());
        Assert.True(host.CycleCount > 2);
    }

    [Fact]
    public void SendToRenderQueue_EnqueuesBatchJobCorrectly()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        using var vm = new LiveStudioViewModel(new LiveStudioHostService(), q)
        {
            ProductName = "저자극 수분 크림",
            Price = 40000m,
            DiscountRate = 25,
            SellingPointsInput = "72시간 보습\n무향 포뮬러, 대용량 리필",
        };

        Assert.True(vm.BuildPitchSequence());
        Assert.Equal(5, vm.Segments.Count);
        Assert.Contains("무향 포뮬러", vm.Segments[2].HostScript);

        var target = vm.Segments[4];
        Assert.True(vm.SendToRenderQueue(target));
        Assert.False(vm.SendToRenderQueue(null));

        var job = Assert.Single(q.Queue);
        Assert.Equal(target.VisualPrompt, job.Prompt);
        Assert.Equal(BatchJobStatus.Queued, job.Status);
        Assert.Equal(1, vm.DispatchedCount);

        vm.Advance();
        Assert.Equal(LivePitchPhase.Hook, vm.CurrentPhase);
        Assert.Same(vm.Segments[0], vm.ActiveSegment);
    }
}
