using PromptFlowStudio.Services.Platform;

namespace PromptFlowStudio.Tests;

/// <summary>리소스 모니터 GPU 캐시 — TTL 내 재조회 스폰 0 · 동시 호출 합류 · TTL 경과 후 재조회 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class ResourceMonitorCacheTests
{
    private static readonly (string Name, double Util, double Used, double Total)? Gpu = ("RTX", 40, 4000, 8000);

    [Fact]
    public async Task Sample_WithinTtl_ReusesCachedGpu()
    {
        var calls = 0;
        var mon = new ResourceMonitorService(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Gpu); });

        for (var i = 0; i < 10; i++)
        {
            var s = await mon.SampleAsync();
            Assert.True(s.GpuAvailable);
            Assert.Equal(4000, s.VramUsedMb);
        }

        Assert.Equal(1, calls);
        Assert.Equal(1, mon.GpuQueryCount);
    }

    [Fact]
    public async Task Sample_ConcurrentCallers_JoinSingleQuery()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<(string Name, double Util, double Used, double Total)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mon = new ResourceMonitorService(_ => { Interlocked.Increment(ref calls); return gate.Task; });

        var tasks = new Task<ResourceSample>[8];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = mon.SampleAsync();
        gate.SetResult(Gpu);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(8000, r.VramTotalMb));
    }

    [Fact]
    public async Task Sample_AfterTtl_RequeriesGpu()
    {
        var calls = 0;
        var mon = new ResourceMonitorService(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Gpu); });

        await mon.SampleAsync();
        await Task.Delay(ResourceMonitorService.GpuCacheTtl + TimeSpan.FromMilliseconds(100));
        await mon.SampleAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Sample_QueryThrows_IsIsolated_AndGpuUnavailable()
    {
        var mon = new ResourceMonitorService(_ => throw new InvalidOperationException("boom"));
        var s = await mon.SampleAsync();
        Assert.False(s.GpuAvailable);
        Assert.Null(s.VramUsedMb);
    }
}
