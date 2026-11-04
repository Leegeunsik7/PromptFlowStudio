using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>15초 앰비언트 슬로우 루프 — 필터 인자 포맷 · 핑퐁 그래프 · ffmpeg 부재 Graceful · 엔진 후처리 결선 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class AmbientLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_amb_" + Guid.NewGuid().ToString("N"));

    public AmbientLoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AmbientLoopService_BuildsCorrectSlowMotionFilterArgs()
    {
        Assert.Equal("setpts=3.0*PTS,minterpolate='fps=30:mi_mode=mci'", AmbientLoopService.BuildSlowMotionFilter());

        var args = AmbientLoopService.BuildAmbientArgs("in.mp4", "out_ambient15s.mp4", highQuality: true);
        var vf = Array.IndexOf(args, "-vf");
        Assert.True(vf > 0);
        Assert.Equal("setpts=3.0*PTS,minterpolate='fps=30:mi_mode=mci'", args[vf + 1]);
        Assert.Equal("in.mp4", args[Array.IndexOf(args, "-i") + 1]);
        Assert.Contains("-an", args);
        Assert.Equal("15", args[Array.IndexOf(args, "-t") + 1]);
        Assert.Equal("out_ambient15s.mp4", args[^1]);
        Assert.DoesNotContain("-filter_complex", args);
    }

    [Fact]
    public void AmbientLoopService_PingPong_UsesReverseConcatGraph()
    {
        var args = AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4", pingPong: true);
        var fc = args[Array.IndexOf(args, "-filter_complex") + 1];
        Assert.StartsWith("[0:v]setpts=1.5*PTS,fps=30,split[fw][bw]", fc);
        Assert.DoesNotContain("minterpolate", fc);
        Assert.StartsWith("[0:v]setpts=1.5*PTS,minterpolate='fps=30:mi_mode=mci',split[fw][bw]", AmbientLoopService.BuildPingPongFilterGraph(highQuality: true));
        Assert.Contains("[bw]reverse[rv]", fc);
        Assert.Contains("[fw][rv]concat=n=2:v=1:a=0", fc);
        Assert.Equal("[vout]", args[Array.IndexOf(args, "-map") + 1]);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void AmbientLoop_UsesFastBlendFilter_WithoutCpuBottleneck()
    {
        Assert.Equal("setpts=3.0*PTS,fps=30", AmbientLoopService.BuildFastSlowFilter());
        var args = AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4");
        Assert.Equal("setpts=3.0*PTS,fps=30", args[Array.IndexOf(args, "-vf") + 1]);
        foreach (var a in args) Assert.DoesNotContain("minterpolate", a);
        Assert.Equal("veryfast", args[Array.IndexOf(args, "-preset") + 1]);
        Assert.Equal("15", args[Array.IndexOf(args, "-t") + 1]);

        var pp = AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4", pingPong: true);
        var fc = pp[Array.IndexOf(pp, "-filter_complex") + 1];
        Assert.Equal("[0:v]setpts=1.5*PTS,fps=30,split[fw][bw];[bw]reverse[rv];[fw][rv]concat=n=2:v=1:a=0,format=yuv420p[vout]", fc);
    }

    [Fact]
    public async Task AmbientLoopService_NoFfmpeg_ReturnsVirtualAndKeepsOriginal()
    {
        var src = Path.Combine(_dir, "a.mp4");
        File.WriteAllText(src, "v");
        var r = await new AmbientLoopService(() => null, null).RenderAsync(src, Path.Combine(_dir, "a_ambient15s.mp4"));
        Assert.False(r.Ok);
        Assert.True(r.IsVirtual);
        Assert.False(File.Exists(Path.Combine(_dir, "a_ambient15s.mp4")));
    }

    [Fact]
    public async Task RenderEngine_AmbientJob_ReplacesOutputWithAmbient15s()
    {
        var src = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(src, "v");
        IReadOnlyList<string>? seen = null;
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { seen = a; File.WriteAllText(a[^1], "x"); return Task.FromResult(true); }),
        };
        var job = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_AmbientSlow };
        job.Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaProject, "bg");

        var output = await engine.ApplyAmbientLoopAsync(job, src, CancellationToken.None);

        Assert.Equal(Path.Combine(_dir, "bg_ambient15s.mp4"), output);
        Assert.NotNull(seen);
        Assert.Contains("-vf", seen!);
        Assert.Equal("applied", job.Metadata![VideoEngineRenderExecutionEngine.MetaAmbientApplied]);
    }

    [Fact]
    public async Task RenderEngine_NonAmbientJob_SkipsPostProcess()
    {
        var src = Path.Combine(_dir, "c.mp4");
        File.WriteAllText(src, "v");
        var called = false;
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, _, _) => { called = true; return Task.FromResult(true); }),
        };
        var output = await engine.ApplyAmbientLoopAsync(new BatchRenderJob { DurationMode = VideoDurationMode.Sec10_Dual }, src, CancellationToken.None);
        Assert.Equal(src, output);
        Assert.False(called);
    }
}
