using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>Step 45 — <see cref="EngineRoutingAndProjectExportTests"/> 에서 분리한 벽시계 게이트(Stopwatch ≤ 5000 ms · FastProbeTimeout 하한).
///   나머지 메서드(결정론 단언)는 병렬 풀에 그대로 남는다.</summary>
[Collection("TimingSensitivePerformance")]   // Step 45: Stopwatch 벽시계 상한 단언(FastProbe 400 ms 컷 ~ 5000 ms) → 병렬 구간 제외
public sealed class EngineRoutingFastProbeTimingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_route_fp_" + Guid.NewGuid().ToString("N"));

    public EngineRoutingFastProbeTimingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RenderEngine_UnprobedOfflineComfyUIWithDashScopeKey_FastProbesAndRoutesToCloudOnFirstClick()
    {
        var exe = Path.Combine(_dir, "comfy_cli.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        var cfg = new EngineConfigService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Engines:VideoCli:Exe"] = exe }).Build());
        var comfy = new ComfyUiVideoEngine(cfg);
        Assert.True(comfy.Enabled);   // 로컬 CLI 구성됨 — 서버만 오프라인
        var cloud = new EngineRoutingAndProjectExportTests.CloudFake(enabled: true);   // DashScope 키 저장됨
        var probes = new List<string>();
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var engine = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, cloud }, cfg), null, null)
        {
            RenderMode = () => RenderEngineKind.ComfyUi,
            ForceSimulation = () => false,
            BlockSilentMock = true,
            ComfyOnline = () => null,   // 앱 기동 직후 — 헬스 체크 미완료
            ComfyBaseUrl = () => "http://127.0.0.1:59998/",
            ComfyProbe = async (url, ct) => { probes.Add(url); await Task.Delay(Timeout.Infinite, ct); return true; },   // 무응답 → 400ms 컷
            Clock = () => now,
            Downloader = (_, dest, _) => { File.WriteAllBytes(dest, new byte[4096]); return Task.CompletedTask; },
            Ambient = null,
            MockFfmpeg = () => throw new InvalidOperationException("Mock 생성 금지"),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = new BatchRenderJob { Prompt = "첫 클릭", Resolution = "1280x720", OutputPath = Path.Combine(_dir, "first.mp4") };
        var r = await engine.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);
        sw.Stop();

        Assert.True(r.Ok, r.Message);
        Assert.Equal(RenderEngineKind.Cloud, r.EngineKind);
        Assert.Equal(1, cloud.Calls);                                    // 첫 클릭에 즉시 Cloud Wan 2.1
        Assert.Equal(new[] { "http://127.0.0.1:59998" }, probes);        // 설정 엔드포인트로 1회 프로브
        Assert.Equal(1, engine.FastProbeCount);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, VideoEngineRenderExecutionEngine.FastProbeTimeout.TotalMilliseconds - 50, 5000);
        Assert.Equal(VideoEngineRenderExecutionEngine.AutoCloudNotice, MultiDurationPlanner.MetaOf(job, VideoEngineRenderExecutionEngine.MetaEngineNotice));

        // 15초 이내 재클릭 → 캐시 결과 재사용(재프로브 없음)
        now = now.AddSeconds(10);
        Assert.True((await engine.ExecuteAsync(new BatchRenderJob { Prompt = "2", OutputPath = Path.Combine(_dir, "second.mp4") }, new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(1, engine.FastProbeCount);
        Assert.Equal(2, cloud.Calls);

        // 15초 경과 → 재프로브
        now = now.AddSeconds(6);
        await engine.EnsureFastProbeAsync(CancellationToken.None);
        Assert.Equal(2, engine.FastProbeCount);

        // 신선한 헬스 체크 결과가 있으면 프로브 생략 · Cloud 키 미구성이면 프로브 생략
        var fresh = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, cloud }, cfg), null, null)
        { RenderMode = () => RenderEngineKind.ComfyUi, ComfyOnline = () => false, ComfyCheckedAt = () => now.AddSeconds(-3), Clock = () => now, ComfyProbe = (_, _) => throw new InvalidOperationException("프로브 금지") };
        await fresh.EnsureFastProbeAsync(CancellationToken.None);
        Assert.Equal(0, fresh.FastProbeCount);
        var noKey = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, new EngineRoutingAndProjectExportTests.CloudFake(enabled: false) }, cfg), null, null)
        { RenderMode = () => RenderEngineKind.ComfyUi, ComfyProbe = (_, _) => throw new InvalidOperationException("프로브 금지") };
        await noKey.EnsureFastProbeAsync(CancellationToken.None);
        Assert.Equal(0, noKey.FastProbeCount);
    }
}
