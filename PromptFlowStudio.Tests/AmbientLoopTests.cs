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
        Assert.StartsWith("[0:v]setpts=1.5*PTS,fps=30,split=2[v0][v1]", fc);
        Assert.DoesNotContain("minterpolate", fc);
        Assert.StartsWith("[0:v]setpts=1.5*PTS,minterpolate='fps=30:mi_mode=mci',split=2[v0][v1]", AmbientLoopService.BuildPingPongFilterGraph(highQuality: true));
        Assert.Contains("[v1]reverse[v2]", fc);
        Assert.Contains("[v0][v2]concat=n=2:v=1:a=0", fc);
        Assert.Equal("[outv]", args[Array.IndexOf(args, "-map") + 1]);
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
        Assert.Equal("[0:v]setpts=1.5*PTS,fps=30,split=2[v0][v1];[v1]reverse[v2];[v0][v2]concat=n=2:v=1:a=0[outv]", fc);
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
        Assert.Equal(Path.GetFullPath(src), r.OutputPath);   // 원본 대체 보존
    }

    [Fact]
    public async Task AmbientLoop_WhenFfmpegFailsOrMissing_FallsBackToSourceVideo_WithoutLosingFile()
    {
        var src = Path.Combine(_dir, "s.mp4");
        File.WriteAllBytes(src, new byte[4096]);
        var target = Path.Combine(_dir, "s_ambient15s.mp4");

        // ① ffmpeg 미설치
        var missing = await new AmbientLoopService(() => null, null).RenderAsync(src, target);
        // ② 비정상 종료(0 이 아닌 종료 코드) — 헤더만 남긴 잔여물
        var failed = await new AmbientLoopService(() => "ffmpeg", (_, a, _) => { File.WriteAllBytes(a[^1], new byte[10]); return Task.FromResult(false); }).RenderAsync(src, target);
        // ③ 정상 종료지만 출력 누락
        var noOut = await new AmbientLoopService(() => "ffmpeg", (_, _, _) => Task.FromResult(true)).RenderAsync(src, target);
        // ④ 실행기 예외 — 작업을 날리지 않음
        var thrown = await new AmbientLoopService(() => "ffmpeg", (_, _, _) => throw new InvalidOperationException("boom")).RenderAsync(src, target);

        foreach (var r in new[] { missing, failed, noOut, thrown })
        {
            Assert.False(r.Ok);
            Assert.Equal(Path.GetFullPath(src), r.OutputPath);
            Assert.Contains(AmbientLoopService.FallbackNotice, r.Message);
        }
        Assert.True(File.Exists(src));
        Assert.Equal(4096, new FileInfo(src).Length);
        Assert.False(File.Exists(target));   // 손상 잔여물 정리

        // 엔진 결선 — 실패해도 원본 5초 반환 + 메타에 대체 보존 기록
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, _, _) => Task.FromResult(false)),
        };
        var job = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_AmbientSlow };
        var output = await engine.ApplyAmbientLoopAsync(job, src, CancellationToken.None);
        Assert.Equal(src, output);
        Assert.Equal("failed", job.Metadata![VideoEngineRenderExecutionEngine.MetaAmbientApplied]);
        Assert.Contains(AmbientLoopService.FallbackNotice, job.Metadata[VideoEngineRenderExecutionEngine.MetaAmbientNotice]);
    }

    [Fact]
    public void AmbientLoop_BuildsValidFilterComplex_ForPingPongLoop()
    {
        var args = AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4", pingPong: true);
        var i = Array.IndexOf(args, "-filter_complex");
        Assert.True(i > 0);
        var fc = args[i + 1];
        Assert.Equal("[0:v]setpts=1.5*PTS,fps=30,split=2[v0][v1];[v1]reverse[v2];[v0][v2]concat=n=2:v=1:a=0[outv]", fc);
        Assert.EndsWith(args[Array.IndexOf(args, "-map") + 1], fc);   // map 라벨 == 그래프 출력 라벨
        Assert.True(Array.IndexOf(args, "-map") > i);
        Assert.DoesNotContain("-vf", args);
        Assert.Equal(3, fc.Split(';').Length);
        Assert.DoesNotContain("\"", fc);   // ArgumentList 전달 — 수동 따옴표 금지

        var simple = AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4");
        Assert.Equal("setpts=3.0*PTS,fps=30", simple[Array.IndexOf(simple, "-vf") + 1]);
        Assert.DoesNotContain("-filter_complex", simple);
    }

    [Fact]
    public async Task RenderEngine_AmbientJob_ReplacesOutputWithAmbient15s()
    {
        var src = Path.Combine(_dir, "clip.mp4");
        File.WriteAllText(src, "v");
        IReadOnlyList<string>? seen = null;
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { seen = a; File.WriteAllBytes(a[^1], new byte[2048]); return Task.FromResult(true); }),
        };
        var job = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_AmbientSlow };
        job.Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaProject, "bg");

        var output = await engine.ApplyAmbientLoopAsync(job, src, CancellationToken.None);

        Assert.Equal(Path.Combine(_dir, "bg_ambient15s.mp4"), output);
        Assert.NotNull(seen);
        Assert.Contains("-filter_complex", seen!);   // 기본 = 정방향 크로스페이드(역재생 없음)
        Assert.DoesNotContain(seen!, a => a.Contains("reverse", StringComparison.Ordinal));
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
