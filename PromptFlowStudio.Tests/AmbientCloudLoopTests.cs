using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>15초 앰비언트 Cloud — Wan 2.1 5초 요청 · 3세그먼트 핑퐁/크로스디졸브 확장 · Cloud 오류 명시(Mock 무폴백) · 옵시디언 Mock 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class AmbientCloudLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ambcloud_" + Guid.NewGuid().ToString("N"));

    public AmbientCloudLoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class CaptureCloudEngine(bool enabled, bool ok, string message) : IVideoGenerationEngine
    {
        public WanVideoRequest? Seen { get; private set; }
        public int Calls { get; private set; }
        public string Key => "Wan";
        public string DisplayName => "Cloud";
        public bool Enabled => enabled;
        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            Seen = request; Calls++;
            return Task.FromResult(new WanGenerationResult(ok, ok ? WanTaskStatus.Succeeded : WanTaskStatus.Failed,
                ok ? "https://dashscope.example/v.mp4" : null, message, "t-9", TimeSpan.Zero));
        }
        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "n/a"));
    }

    private BatchRenderJob AmbientJob(bool pingPong)
    {
        var job = new BatchRenderJob
        {
            Prompt = "obsidian trading desk, slow dolly", Resolution = "1280x720",
            OutputPath = Path.Combine(_dir, "ws.mp4"), DurationMode = VideoDurationMode.Sec15_AmbientSlow,
            DurationSec = MultiDurationPlanner.ShotSeconds,
        };
        var meta = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaProject, "ws");
        meta = MultiDurationPlanner.WithMeta(meta, VideoEngineRenderExecutionEngine.MetaNegativePrompt, "blurry, text");
        if (pingPong) meta = MultiDurationPlanner.WithMeta(meta, VideoEngineRenderExecutionEngine.MetaAmbientPingPong, "true");
        job.Metadata = meta;
        return job;
    }

    private (VideoEngineRenderExecutionEngine Engine, List<IReadOnlyList<string>> Ffmpeg) CloudEngine(CaptureCloudEngine cloud)
    {
        var calls = new List<IReadOnlyList<string>>();
        var engine = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(cloud), null, null)
        {
            RenderMode = () => RenderEngineKind.Cloud,
            ForceSimulation = () => false,
            Downloader = (_, dest, _) => { File.WriteAllBytes(dest, new byte[4096]); return Task.CompletedTask; },
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { calls.Add(a); File.WriteAllBytes(a[^1], new byte[4096]); return Task.FromResult(true); }),
            MockFfmpeg = () => throw new InvalidOperationException("Cloud 모드에서 Mock 생성 금지"),
        };
        return (engine, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloudAmbient_RequestsFiveSeconds_AndExpandsTo15sLoop(bool pingPong)
    {
        var cloud = new CaptureCloudEngine(enabled: true, ok: true, "완료");
        var (engine, ffmpeg) = CloudEngine(cloud);
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = AmbientJob(pingPong);
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(1, cloud.Calls);
        Assert.Equal(5, cloud.Seen!.Duration);
        Assert.NotEqual(15, cloud.Seen.Duration);
        Assert.Equal("1280*720", cloud.Seen.Size);
        Assert.Equal("blurry, text", cloud.Seen.NegativePrompt);
        Assert.Equal(RenderEngineKind.Cloud, job.EngineKind);   // 갤러리 AI Render 태깅(Mock 아님)
        Assert.Equal(Path.Combine(_dir, "ws_ambient15s.mp4"), job.ResultPath);

        var args = Assert.Single(ffmpeg);
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("-t", args);
        Assert.Equal("15", args[args.ToList().IndexOf("-t") + 1]);
        if (pingPong)
        {
            Assert.Equal(AmbientLoopService.BuildTriplePingPongFilterGraph(), graph);
            Assert.Contains("split=3", graph);
            Assert.Contains("reverse", graph);
            Assert.Contains("concat=n=3:v=1:a=0", graph);
            Assert.DoesNotContain("setpts=1.5", graph);   // 원속도(슬로우 아님)
        }
        else
        {
            Assert.DoesNotContain("reverse", graph);
            Assert.Contains("xfade=transition=fade:duration=0.75:offset=4.25", graph);
            Assert.Contains("xfade=transition=fade:duration=0.75:offset=8.5", graph);
            Assert.Contains("tpad=stop_mode=clone:stop_duration=1.5", graph);
            Assert.Contains("trim=duration=15", graph);
        }
    }

    [Fact]
    public async Task CloudApiFailure_SurfacesErrorState_NotMock()
    {
        var cloud = new CaptureCloudEngine(enabled: true, ok: false, "HTTP 400 InvalidParameter: duration must be 5");
        var (engine, ffmpeg) = CloudEngine(cloud);
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = AmbientJob(pingPong: true);
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Failed, job.Status);
        Assert.NotEqual(RenderEngineKind.Mock, job.EngineKind);
        Assert.StartsWith(VideoEngineRenderExecutionEngine.CloudErrorPrefix, job.ErrorMessage);
        Assert.Contains("InvalidParameter", job.ErrorMessage);
        Assert.Null(job.ResultPath);
        Assert.Empty(ffmpeg);
    }

    [Fact]
    public async Task CloudMode_EngineNotConfigured_FailsExplicitly_InsteadOfSilentMock()
    {
        var cloud = new CaptureCloudEngine(enabled: false, ok: true, "n/a");
        var (engine, _) = CloudEngine(cloud);
        Assert.True(engine.IsAvailable);   // 큐가 SimulateAsync(Mock)로 빠지지 않도록
        Assert.Equal(RenderEngineKind.Cloud, engine.EngineKind);
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = AmbientJob(pingPong: true);
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Failed, job.Status);
        Assert.Equal(0, cloud.Calls);
        Assert.NotEqual(RenderEngineKind.Mock, job.EngineKind);
        Assert.StartsWith(VideoEngineRenderExecutionEngine.CloudErrorPrefix, job.ErrorMessage);
        Assert.Contains("DashScope", job.ErrorMessage);
    }

    [Fact]
    public void NonAmbientJob_OmitsDuration()
        => Assert.Null(VideoEngineRenderExecutionEngine.BuildRequest(new BatchRenderJob { Prompt = "p" }).Duration);

    [Fact]
    public void MockAmbientArgs_UseObsidianPulse_NotColorBars()
    {
        var a = VideoEngineRenderExecutionEngine.BuildMockArgs("1280x720", "o.mp4", ambient: true);
        var src = a[Array.IndexOf(a, "-i") + 1];
        var vf = a[Array.IndexOf(a, "-vf") + 1];
        Assert.StartsWith("color=c=0x07090E:", src);
        Assert.DoesNotContain("testsrc", string.Join(' ', a));
        Assert.DoesNotContain("smptebars", string.Join(' ', a));
        Assert.Contains("geq=", vf);
        Assert.Contains("scale=1280:720", vf);
        Assert.Contains("vignette", vf);
        Assert.Equal("o.mp4", a[^1]);
    }

    [Fact]
    public async Task MockMode_AmbientJob_UsesObsidianGenerator()
    {
        IReadOnlyList<string>? seen = null;
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            ForceSimulation = () => true,
            MockFfmpeg = () => "ffmpeg",
            MockRunner = (_, a, _) => { seen = a; File.WriteAllBytes(a[^1], new byte[128]); return Task.FromResult(true); },
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { File.WriteAllBytes(a[^1], new byte[2048]); return Task.FromResult(true); }),
        };
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = AmbientJob(pingPong: true);
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(RenderEngineKind.Mock, job.EngineKind);
        Assert.Contains(seen!, s => s.StartsWith("color=c=0x07090E", StringComparison.Ordinal));
    }
}
