using System;
using System.Runtime;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Tests;

/// <summary>GC 지연 모드/NoGC 영역은 프로세스 전역 - 이 컬렉션은 다른 테스트와 병렬 실행하지 않는다.</summary>
[CollectionDefinition("ProcessGcMode", DisableParallelization = true)]
public sealed class ProcessGcModeCollection { }

/// <summary>v12.3.0: 할당 가드(부작용 없음)·NoGC 임계 영역·저지연 세션 참조 카운트 회귀 잠금.</summary>
[Trait("Category", "Integration")]
public class ZeroGcRegionGuardTests
{
    [Fact]
    public void Guard_NonAllocatingBody_RecordsScopeWithoutViolation()
    {
        var c = new ZeroGcCounters();
        long sum = 0;
        using (var g = ZeroGcRegionScope.Enter("test.noalloc", c))
            for (var i = 0; i < 10_000; i++) sum += i;
        Assert.True(sum > 0);
        Assert.Equal(1, c.Scopes);
        Assert.Equal(0, c.Violations);
    }

    [Fact]
    public void Guard_AllocatingBody_IsReportedWithSiteAndBytes()
    {
        var c = new ZeroGcCounters();
        byte[] keep;
        using (var g = ZeroGcRegionScope.Enter("test.alloc", c))
            keep = new byte[4096];
        GC.KeepAlive(keep);
        // 구간 도중 타 스레드 GC 가 겹치면 판정 보류(위반 아님)로 분류된다 - 둘 중 정확히 하나
        Assert.Equal(1, c.Violations + c.InconclusiveGcOverlap);
        if (c.Violations == 1)
        {
            Assert.Equal("test.alloc", c.LastViolationSite);
            Assert.True(c.LastViolationBytes >= 4096);
        }
    }

    [Fact]
    public void Guard_EnterDispose_ItselfDoesNotAllocate()
    {
        var c = new ZeroGcCounters();
        void Body() { for (var i = 0; i < 20_000; i++) { using var g = ZeroGcRegionScope.Enter("test.overhead", c); } }
        Body();   // 워밍업
        Assert.Equal(0, AllocationProbe.MinAllocated(Body));
        Assert.Equal(0, c.Violations);
    }

    [Fact]
    public void HotPaths_AreGuarded_ScopesIncreaseWhenExercised()
    {
        var before = ZeroGcCounters.Global.Scopes;
        using var fx = new StreamerReactionEngine(new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler()), new ReactionOverlayPipeline(), () => 0);
        fx.Advance(30);   // 스텝 가드 1회(3스텝 묶음)
        using var lb = new P2pLoadBalancer(() => 0);
        lb.AddNode("a"); lb.RecordSample(0, 5, 100);
        lb.Rebalance();   // 재구성 가드 1회
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var pipe = new LiveStreamVirtualCamPipe("Local\\pfs_test_zg_map_" + tag, "Local\\pfs_test_zg_mtx_" + tag);
        pipe.SubmitFrame(new byte[16], 2, 2, VirtualCamPixelFormat.Rgba32);   // MMF 블릿 가드 1회
        Assert.True(ZeroGcCounters.Global.Scopes - before >= 3);
    }
}

[Collection("ProcessGcMode")]
[Trait("Category", "Integration")]
public class ZeroGcProcessModeTests
{
    [Fact]
    public void LatencySession_RefCounted_RestoresOriginalModeOnLastRelease()
    {
        var original = GCSettings.LatencyMode;
        GcLatencySession.Acquire();
        GcLatencySession.Acquire();
        Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        GcLatencySession.Release();
        Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);   // 아직 1개 보유
        GcLatencySession.Release();
        Assert.Equal(original, GCSettings.LatencyMode);
        GcLatencySession.Release();   // 과다 해제는 무해
        Assert.Equal(0, GcLatencySession.ActiveCount);
    }

    [Fact]
    public void CriticalRegion_NestedAndBudgetExceeded_NeverThrows_AndLeavesProcessCleanly()
    {
        var c = new ZeroGcCounters();
        var original = GCSettings.LatencyMode;
        using (var outer = ZeroGcRegionScope.EnterCritical("test.critical.outer", 1024 * 1024, c))
        {
            using (var inner = ZeroGcRegionScope.EnterCritical("test.critical.inner", 1024 * 1024, c)) { }   // 중첩 - 예외 없이 공유
            Assert.Equal(1, NoGcRegionGate.Depth);
            var big = new byte[8 * 1024 * 1024];   // 임계 구간 내 할당 → 가드 위반으로 집계
            GC.KeepAlive(big);
            // 예산 초과 시 런타임이 영역을 조기 종료하는 시점은 구현 의존(요청보다 넓게 예약될 수 있음) → 직접 종료해 그 상태를 결정적으로 재현
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }
        Assert.Equal(0, NoGcRegionGate.Depth);
        Assert.NotEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
        Assert.Equal(original, GCSettings.LatencyMode);
        Assert.Equal(1, c.NoGcRegionsEntered + c.NoGcRegionsRefused);   // 첫 진입만 실제 시도
        if (c.NoGcRegionsEntered == 1) Assert.Equal(1, c.NoGcBudgetExceeded);
        Assert.True(c.Violations >= 1);   // 임계 구간 내 할당은 GC 겹침과 무관하게 위반으로 집계
    }

    [Fact]
    public void LatencySessionReleasedDuringCriticalRegion_IsRestoredAfterRegionEnds()
    {
        var original = GCSettings.LatencyMode;
        GcLatencySession.Acquire();
        using (var crit = ZeroGcRegionScope.EnterCritical("test.pending", 1024 * 1024, new ZeroGcCounters()))
        {
            GcLatencySession.Release();   // NoGC 영역 중 - 모드 변경 대신 복원 예약
        }
        Assert.Equal(0, GcLatencySession.ActiveCount);
        Assert.Equal(original, GCSettings.LatencyMode);
    }
}
