using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>렌더 모드 실시간 해석 · DashScope 오류 가시화 · 원격 결과 다운로드 · Mock 실물 MP4 생성 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class RenderEngineSettingsSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_rsync_" + Guid.NewGuid().ToString("N"));

    public RenderEngineSettingsSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class CloudEngine(bool ok, string? url, string message) : IVideoGenerationEngine
    {
        public string Key => "Wan";
        public string DisplayName => "Cloud";
        public bool Enabled => true;
        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new WanGenerationResult(ok, ok ? WanTaskStatus.Succeeded : WanTaskStatus.Failed, url, message, "t-1", TimeSpan.Zero));
        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "n/a"));
    }

    private static VideoEngineSelector Sel(IVideoGenerationEngine e) => FakeVideoEngine.Selector(e);

    [Fact]
    public async Task CloudFailure_SurfacesDashScopeMessage_OnFailedJob()
    {
        var engine = new VideoEngineRenderExecutionEngine(Sel(new CloudEngine(false, null, "InvalidApiKey: Invalid API-key provided.")), null, null)
        { RenderMode = () => RenderEngineKind.Cloud };
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = new BatchRenderJob { Prompt = "p", OutputPath = Path.Combine(_dir, "a.mp4") };
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Failed, job.Status);
        Assert.StartsWith(VideoEngineRenderExecutionEngine.CloudErrorPrefix, job.ErrorMessage);
        Assert.Contains("인증 실패", job.ErrorMessage);
        Assert.Contains("InvalidApiKey", job.ErrorMessage);
        Assert.Contains("task_id=t-1", job.ErrorMessage);
    }

    [Theory]
    [InlineData("Arrearage: Access denied, please make sure your account is in good standing.", "할당량 부족")]
    [InlineData("HTTP 429 Throttling.RateQuota", "속도 제한")]
    [InlineData("HTTP 401 Unauthorized", "인증 실패")]
    [InlineData("알 수 없는 오류", null)]
    public void ClassifyCloudError_MapsDashScopeCodes(string msg, string? expected)
        => Assert.Equal(expected, VideoEngineRenderExecutionEngine.ClassifyCloudError(msg));

    [Fact]
    public async Task CloudSuccess_RemoteUrl_DownloadedToOutputPath()
    {
        var output = Path.Combine(_dir, "sub", "c.mp4");
        string? seenUrl = null;
        var engine = new VideoEngineRenderExecutionEngine(Sel(new CloudEngine(true, "https://dashscope.example/v.mp4", "완료")), null, null)
        {
            RenderMode = () => RenderEngineKind.Cloud,
            Downloader = (url, dest, _) => { seenUrl = url; File.WriteAllBytes(dest, new byte[64]); return Task.CompletedTask; },
        };
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = new BatchRenderJob { Prompt = "p", OutputPath = output };
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal("https://dashscope.example/v.mp4", seenUrl);
        Assert.Equal(output, job.ResultPath);
        Assert.Equal(RenderEngineKind.Cloud, job.EngineKind);
        Assert.Equal(output, MiniQueueProgress.ResolveFinal(job));
    }

    [Fact]
    public async Task CloudDownloadFailure_ReportsDownloadError()
    {
        var engine = new VideoEngineRenderExecutionEngine(Sel(new CloudEngine(true, "https://dashscope.example/v.mp4", "완료")), null, null)
        {
            RenderMode = () => RenderEngineKind.Cloud,
            Downloader = (_, _, _) => throw new HttpRequestException("HTTP 403 Forbidden"),
        };
        var r = await engine.ExecuteAsync(new BatchRenderJob { Prompt = "p", OutputPath = Path.Combine(_dir, "d.mp4") }, new Progress<int>(), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("다운로드 실패", r.Message);
        Assert.Contains("403", r.Message);
    }

    [Fact]
    public async Task RenderMode_IsEvaluatedAtDispatchTime()
    {
        var calls = 0;
        var engine = new VideoEngineRenderExecutionEngine(Sel(new CloudEngine(false, null, "x")), null, null)
        { RenderMode = () => { calls++; return RenderEngineKind.Cloud; } };
        await engine.ExecuteAsync(new BatchRenderJob { Prompt = "p" }, new Progress<int>(), CancellationToken.None);
        var first = calls;
        await engine.ExecuteAsync(new BatchRenderJob { Prompt = "p" }, new Progress<int>(), CancellationToken.None);
        Assert.True(first > 0);
        Assert.True(calls > first);
    }

    [Fact]
    public void BuildMockArgs_UsesTestsrcFiveSeconds()
    {
        var a = VideoEngineRenderExecutionEngine.BuildMockArgs("1280x720", "o.mp4");
        Assert.Contains("lavfi", a);
        Assert.Contains("testsrc=duration=5:size=1280x720:rate=30", a);
        Assert.Equal("o.mp4", a[^1]);
        Assert.Contains("testsrc=duration=5:size=1280x720:rate=30", VideoEngineRenderExecutionEngine.BuildMockArgs("bogus", "o.mp4"));
        Assert.Contains("testsrc=duration=5:size=720x1280:rate=30", VideoEngineRenderExecutionEngine.BuildMockArgs("720x1280", "o.mp4"));
    }

    [Fact]
    public async Task MockMode_GeneratesRealMp4_OnDisk()
    {
        IReadOnlyList<string>? seen = null;
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            ForceSimulation = () => true,
            MockFfmpeg = () => "ffmpeg",
            MockRunner = (_, a, _) => { seen = a; File.WriteAllBytes(a[^1], new byte[128]); return Task.FromResult(true); },
        };
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = new BatchRenderJob { Prompt = "p", OutputPath = Path.Combine(_dir, "m.mp4"), Resolution = "1280x720" };
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(RenderEngineKind.Mock, job.EngineKind);
        Assert.NotNull(seen);
        Assert.Equal(job.OutputPath, job.ResultPath);
        Assert.True(File.Exists(job.ResultPath));
    }

    [Fact]
    public async Task MockMode_AmbientJob_ProducesAmbient15s()
    {
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            ForceSimulation = () => true,
            MockFfmpeg = () => "ffmpeg",
            MockRunner = (_, a, _) => { File.WriteAllBytes(a[^1], new byte[128]); return Task.FromResult(true); },
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { File.WriteAllBytes(a[^1], new byte[2048]); return Task.FromResult(true); }),
        };
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = new BatchRenderJob { Prompt = "p", OutputPath = Path.Combine(_dir, "bg.mp4"), DurationMode = VideoDurationMode.Sec15_AmbientSlow };
        job.Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaProject, "bg");
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(Path.Combine(_dir, "bg_ambient15s.mp4"), job.ResultPath);
        Assert.Equal(job.ResultPath, MiniQueueProgress.ResolveFinal(job));
    }

    [Fact]
    public async Task MockMode_NoFfmpeg_CompletesWithoutOutput()
    {
        var engine = new VideoEngineRenderExecutionEngine(null, null, null) { ForceSimulation = () => true, MockFfmpeg = () => null };
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = new BatchRenderJob { Prompt = "p", OutputPath = Path.Combine(_dir, "n.mp4") };
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Null(job.ResultPath);
        Assert.False(File.Exists(job.OutputPath));
    }
}
