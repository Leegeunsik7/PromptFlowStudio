using System.Collections.Concurrent;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>5s/10s/15s 멀티 듀레이션 — 하위 컷 분할·그룹 키 · 프롬프트 상속 · 마지막 프레임 I2V 앵커 체이닝 · 그룹 합본 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class MultiDurationPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_dur_" + Guid.NewGuid().ToString("N"));

    public MultiDurationPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(VideoDurationMode.Sec10_Dual, 2)]
    [InlineData(VideoDurationMode.Sec15_Triple, 3)]
    public void RenderQueue_EnqueueMultiDuration_GeneratesChainedJobsWithGroupKey(VideoDurationMode mode, int expected)
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var template = new BatchRenderJob { Prompt = "neon city", Seed = 42, Resolution = "1280x720", DurationMode = mode, ReferenceImagePath = @"C:\anchor.png", OutputPath = @"C:\out\proj.mp4" };

        var jobs = q.EnqueueJob(template, new string?[] { null, "custom part two", null }, "proj");

        Assert.Equal(expected, jobs.Count);
        var snap = q.Snapshot();
        Assert.Equal(expected, snap.Length);
        var group = snap[0].StitchGroupId;
        Assert.False(string.IsNullOrEmpty(group));
        for (var i = 0; i < snap.Length; i++)
        {
            var j = snap[i];
            Assert.Equal(group, j.StitchGroupId);
            Assert.Equal(group, j.Metadata![FFmpegVideoStitchService.MetaGroup]);
            Assert.Equal("proj", j.Metadata[MultiDurationPlanner.MetaProject]);
            Assert.Equal(i + 1, j.CutIndex);
            Assert.Equal(i * 5f, j.StartSec);
            Assert.Equal(5f, j.DurationSec);
            Assert.Equal(42, j.Seed);
            Assert.Equal(mode, j.DurationMode);
            Assert.Equal(BatchJobStatus.Queued, j.Status);
            Assert.Equal(i == 0 ? @"C:\anchor.png" : null, j.ReferenceImagePath);
            Assert.EndsWith("_p" + (i + 1) + ".mp4", j.OutputPath);
        }
        Assert.Equal("neon city, " + MultiDurationPlanner.PartCameraMotions[0], snap[0].Prompt);
        Assert.Equal("custom part two", snap[1].Prompt);
        Assert.Equal(mode == VideoDurationMode.Sec15_Triple ? 15 : 10, snap[0].TargetDurationSeconds);
    }

    [Theory]
    [InlineData(VideoDurationMode.Sec5_Single)]
    [InlineData(VideoDurationMode.Sec15_AmbientSlow)]
    public void RenderQueue_EnqueueSingleShotModes_KeepsOneJobWithoutGroup(VideoDurationMode mode)
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "ocean", DurationMode = mode }, null, "p");
        var j = Assert.Single(jobs);
        Assert.Null(j.StitchGroupId);
        Assert.Equal("ocean", j.Prompt);
        Assert.Equal(mode == VideoDurationMode.Sec15_AmbientSlow, j.IsAmbientLoop);
        Assert.Equal(mode.ToString(), j.Metadata![MultiDurationPlanner.MetaMode]);
    }

    [Fact]
    public void Planner_BuildsLastFrameArgsAndOutputNames()
    {
        Assert.Equal(new[] { "-y", "-hide_banner", "-loglevel", "error", "-sseof", "-0.1", "-i", "a.mp4", "-vframes", "1", "a.png" },
            MultiDurationPlanner.BuildLastFrameArgs("a.mp4", "a.png"));
        Assert.Equal(Path.Combine("d", "my_proj_10s.mp4"), MultiDurationPlanner.BuildOutputPath("d", "my:proj", VideoDurationMode.Sec10_Dual));
        Assert.Equal(Path.Combine("d", "p_15s.mp4"), MultiDurationPlanner.BuildOutputPath("d", "p", VideoDurationMode.Sec15_Triple));
        Assert.Equal(Path.Combine("d", "p_ambient15s.mp4"), MultiDurationPlanner.BuildOutputPath("d", "p", VideoDurationMode.Sec15_AmbientSlow));
        Assert.Equal(MultiDurationPlanner.PartCameraMotions[2], MultiDurationPlanner.ResolvePartPrompt("  ", null, 2, 3));
        Assert.Equal("x", MultiDurationPlanner.ResolvePartPrompt("x", null, 0, 1));
    }

    [Fact]
    public async Task RenderQueue_MultiShot_InjectsLastFrameAnchorAndStitchesGroup()
    {
        var engine = new FileWritingEngine();
        var ffmpegCalls = new ConcurrentQueue<IReadOnlyList<string>>();
        var stitcher = new FFmpegVideoStitchService(() => "ffmpeg", (_, args, _) =>
        {
            ffmpegCalls.Enqueue(args);
            File.WriteAllText(args[^1], "x");
            return Task.FromResult(true);
        });
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine) { Stitcher = stitcher };
        string? stitchedGroup = null;
        q.OnDurationStitched += (g, r) => { if (r.Ok) stitchedGroup = g; };

        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_Triple, OutputPath = Path.Combine(_dir, "proj.mp4") }, null, "proj");
        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.All(jobs, j => Assert.Equal(BatchJobStatus.Completed, j.Status));
        Assert.Null(engine.SeenReferences[0]);
        Assert.Equal(MultiDurationPlanner.LastFramePathFor(jobs[0].OutputPath!), engine.SeenReferences[1]);
        Assert.Equal(MultiDurationPlanner.LastFramePathFor(jobs[1].OutputPath!), engine.SeenReferences[2]);
        Assert.Equal(jobs[0].StitchGroupId, stitchedGroup);
        var stitched = Path.Combine(_dir, "proj_15s.mp4");
        Assert.True(File.Exists(stitched));
        Assert.Equal(stitched, jobs[2].Metadata![MultiDurationPlanner.MetaStitched]);
        Assert.Contains(ffmpegCalls, a => a.Contains("-sseof"));
    }

    [Fact]
    public async Task RenderQueue_MultiShot_NoFfmpeg_FallsBackToT2vWithoutStitch()
    {
        var engine = new FileWritingEngine();
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine) { Stitcher = new FFmpegVideoStitchService(() => null, null) };
        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec10_Dual, OutputPath = Path.Combine(_dir, "v.mp4") }, null, "v");
        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.All(jobs, j => Assert.Equal(BatchJobStatus.Completed, j.Status));
        Assert.Null(engine.SeenReferences[1]);   // 앵커 추출 불가 → T2V
        Assert.False(jobs[1].Metadata!.ContainsKey(MultiDurationPlanner.MetaStitched));   // 가상 병합은 실물 경로로 기록하지 않음
    }

    /// <summary>작업 OutputPath 에 더미 산출물을 쓰고 디스패치 시점 참조 이미지를 기록.</summary>
    private sealed class FileWritingEngine : IRenderExecutionEngine
    {
        public List<string?> SeenReferences { get; } = new();
        public bool IsAvailable => true;
        public string EngineKind => RenderEngineKind.Mock;

        public Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
        {
            SeenReferences.Add(job.ReferenceImagePath);
            File.WriteAllText(job.OutputPath!, "video");
            return Task.FromResult(new RenderExecutionResult(true, EngineKind, job.OutputPath, "ok"));
        }
    }
}
