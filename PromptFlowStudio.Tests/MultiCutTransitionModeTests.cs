using System.Collections.Concurrent;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>🎬 멀티 컷 전환 방식(독립 디졸브 기본 · 프레임 체인 옵션) · 파트 1 주 프롬프트 폴백 · 첫 화면 미리보기 → 컷 1 바인딩 · Pluto 9/6/2.</summary>
[Trait("Category", "FastUnit")]
public class MultiCutTransitionModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_cuttr_" + Guid.NewGuid().ToString("N"));

    public MultiCutTransitionModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Transition_DefaultsToIndependent_AndMetaRoundTrips()
    {
        Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.DefaultTransition);
        var plain = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec10_Dual };
        Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.TransitionOf(plain));
        Assert.Contains("Hard Cut", MultiDurationPlanner.TransitionLabel(CutTransitionMode.IndependentHardCut));
        Assert.False(MultiDurationPlanner.ShouldChainLastFrame(plain));

        var chained = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_Triple, Metadata = MultiDurationPlanner.WithTransition(null, CutTransitionMode.LastFrameChain) };
        Assert.True(MultiDurationPlanner.ShouldChainLastFrame(chained));
        foreach (var j in MultiDurationPlanner.Expand(chained, null, "x"))
            Assert.Equal(CutTransitionMode.LastFrameChain, MultiDurationPlanner.TransitionOf(j));   // 메타가 전 컷에 전파

        var single = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec5_Single, Metadata = MultiDurationPlanner.WithTransition(null, CutTransitionMode.LastFrameChain) };
        Assert.False(MultiDurationPlanner.ShouldChainLastFrame(single));
        Assert.Contains("Crossfade", MultiDurationPlanner.TransitionLabel(CutTransitionMode.IndependentCrossfade));
        Assert.Contains("Last-Frame", MultiDurationPlanner.TransitionLabel(CutTransitionMode.LastFrameChain));
    }

    [Fact]
    public void ContinuityHint_IsIdempotent_AndSkipsEmpty()
    {
        var once = MultiDurationPlanner.WithContinuityHint("wide shot of a player dribbling, ");
        Assert.EndsWith(MultiDurationPlanner.ContinuityHint, once);
        Assert.StartsWith("wide shot of a player dribbling, same subject", once);
        Assert.Equal(once, MultiDurationPlanner.WithContinuityHint(once));
        Assert.Equal("", MultiDurationPlanner.WithContinuityHint("  "));
    }

    [Fact]
    public async Task IndependentMode_NoLastFrameAnchor_AndCrossfadeStitch()
    {
        var engine = new FileWritingEngine();
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var stitcher = new FFmpegVideoStitchService(() => "ffmpeg", (_, args, _) =>
        {
            calls.Enqueue(args);
            File.WriteAllText(args[^1], "x");
            return Task.FromResult(true);
        });
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine) { Stitcher = stitcher };
        var cut1 = Path.Combine(_dir, "cut1.png");
        File.WriteAllText(cut1, "png");
        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "hoop", DurationMode = VideoDurationMode.Sec15_Triple, ReferenceImagePath = cut1, OutputPath = Path.Combine(_dir, "ind.mp4"),
                Metadata = MultiDurationPlanner.WithTransition(null, CutTransitionMode.IndependentCrossfade) },   // 0.25s 디졸브 = 명시 선택
            new string?[] { "wide dribble", "medium jump shot", "hoop swish close-up" }, "ind");
        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.All(jobs, j => Assert.Equal(BatchJobStatus.Completed, j.Status));
        Assert.Equal(cut1, engine.SeenReferences[0]);   // 컷 1 = 첫 화면/참조 이미지
        Assert.Null(engine.SeenReferences[1]);           // 컷 2·3 = 자기 프롬프트 T2V(앵커 미주입)
        Assert.Null(engine.SeenReferences[2]);
        Assert.Equal(new[] { "wide dribble", "medium jump shot", "hoop swish close-up" }, engine.SeenPrompts);
        Assert.DoesNotContain(calls, a => a.Contains("-sseof"));
        var stitch = Assert.Single(calls, a => a.Contains("-filter_complex"));
        var graph = stitch[Array.IndexOf(stitch.ToArray(), "-filter_complex") + 1];
        Assert.Contains("xfade=transition=fade:duration=0.25:offset=4.75", graph);
        Assert.Contains("xfade=transition=fade:duration=0.25:offset=9.5", graph);
        var stitched = Path.Combine(_dir, "ind_15s.mp4");
        Assert.Equal(stitched, jobs[2].Metadata![MultiDurationPlanner.MetaStitched]);
    }

    [Fact]
    public async Task ChainMode_InjectsLastFrame_AndHardCutConcat()
    {
        var engine = new FileWritingEngine();
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var stitcher = new FFmpegVideoStitchService(() => "ffmpeg", (_, args, _) =>
        {
            calls.Enqueue(args);
            File.WriteAllText(args[^1], "x");
            return Task.FromResult(true);
        });
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine) { Stitcher = stitcher };
        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec10_Dual, OutputPath = Path.Combine(_dir, "ch.mp4"),
            Metadata = MultiDurationPlanner.WithTransition(null, CutTransitionMode.LastFrameChain) }, null, "ch");
        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(MultiDurationPlanner.LastFramePathFor(jobs[0].OutputPath!), engine.SeenReferences[1]);
        Assert.Contains(calls, a => a.Contains("-sseof"));
        Assert.DoesNotContain(calls, a => a.Contains("-filter_complex"));   // concat demuxer(-c copy)
        Assert.Contains(calls, a => a.Contains("concat"));
    }

    [Fact]
    public async Task Crossfade_FailureFallsBackToHardCutConcat()
    {
        var a = Path.Combine(_dir, "a.mp4"); var b = Path.Combine(_dir, "b.mp4");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        var stitcher = new FFmpegVideoStitchService(() => "ffmpeg", (_, args, _) =>
        {
            if (args.Contains("-filter_complex")) return Task.FromResult(false);   // xfade 실패
            File.WriteAllText(args[^1], "x");
            return Task.FromResult(true);
        });
        var r = await stitcher.StitchCrossfadeAsync(new[] { new StitchCut(a, 5, null), new StitchCut(b, 5, null) }, Path.Combine(_dir, "o.mp4"), 0.25);
        Assert.True(r.Ok);
        Assert.DoesNotContain("크로스페이드", r.Message);

        var noFfmpeg = new FFmpegVideoStitchService(() => null, null);
        var v = await noFfmpeg.StitchCrossfadeAsync(new[] { new StitchCut(a, 5, null), new StitchCut(b, 5, null) }, Path.Combine(_dir, "v.mp4"), 0.25);
        Assert.True(v.Ok && v.IsVirtual);
    }

    [Fact]
    public void CrossfadeArgs_AudioChain_UsesAcrossfade()
    {
        var args = FFmpegVideoStitchService.BuildCrossfadeArgs(new[] { "a.mp4", "b.mp4" }, new[] { 5.0, 5.0 }, 1281, 721, true, 0.25, "o.mp4");
        var graph = args[Array.IndexOf(args, "-filter_complex") + 1];
        Assert.Contains("scale=1280:720", graph);
        Assert.Contains("[v0][v1]xfade=transition=fade:duration=0.25:offset=4.75[vout]", graph);
        Assert.Contains("[a0][a1]acrossfade=d=0.25[aout]", graph);
        Assert.DoesNotContain("anullsrc", string.Join(' ', args));
    }

    [Theory]
    [InlineData(VideoDurationMode.Sec10_Dual)]
    [InlineData(VideoDurationMode.Sec15_Triple)]
    public void Part1Only_EnablesStart_AndBecomesPrimaryPrompt(VideoDurationMode mode)
    {
        var subs = new string?[] { "  wide dribble  ", null, null };
        Assert.False(WorkspaceGenerationReadiness.CanPress(false, "", null));
        Assert.True(WorkspaceGenerationReadiness.CanPress(false, "", null, mode, subs));
        Assert.Equal(WorkspaceStartBlock.None, WorkspaceGenerationReadiness.Evaluate(false, " ", null, mode, false, subs));
        Assert.Equal("wide dribble", MultiDurationPlanner.ResolvePrimaryPrompt(null, subs, mode));
        Assert.Equal("main", MultiDurationPlanner.ResolvePrimaryPrompt(" main ", subs, mode));   // 메인 우선
        Assert.False(WorkspaceGenerationReadiness.CanPress(true, "", null, mode, subs));          // Busy 유지
    }

    [Fact]
    public void Part1Fallback_IgnoredForSingleShot_AndEmptyParts()
    {
        var subs = new string?[] { "part one", null, null };
        Assert.False(WorkspaceGenerationReadiness.CanPress(false, "", null, VideoDurationMode.Sec5_Single, subs));
        Assert.False(WorkspaceGenerationReadiness.CanPress(false, "", null, VideoDurationMode.Sec15_AmbientSlow, subs));
        Assert.False(WorkspaceGenerationReadiness.CanPress(false, "", null, VideoDurationMode.Sec10_Dual, new string?[] { " ", "p2", null }));
        Assert.Equal(WorkspaceStartBlock.MissingInput, WorkspaceGenerationReadiness.Evaluate(false, "", null, VideoDurationMode.Sec10_Dual, true, null));
    }

    [Fact]
    public void FirstFramePreview_BindsToCut1_WithToggleAndPrecedence()
    {
        const string User = @"C:\u\ref.png", Preview = @"C:\u\firstframe_x.png";
        Assert.Equal(Preview, WorkspaceGenerationReadiness.ResolveCut1Reference(null, Preview, false, true));
        Assert.Equal(User, WorkspaceGenerationReadiness.ResolveCut1Reference(User, Preview, false, true));   // 사용자 이미지 우선
        Assert.Null(WorkspaceGenerationReadiness.ResolveCut1Reference(null, Preview, false, false));         // 토글 OFF
        Assert.Null(WorkspaceGenerationReadiness.ResolveCut1Reference(null, Preview, true, true));           // 로컬 구도 폴백은 바인딩 불가
        Assert.Null(WorkspaceGenerationReadiness.ResolveCut1Reference(null, null, false, true));

        var jobs = MultiDurationPlanner.Expand(new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_Triple, ReferenceImagePath = Preview }, null, "p");
        Assert.Equal(Preview, jobs[0].ReferenceImagePath);
        Assert.Null(jobs[1].ReferenceImagePath);
        Assert.Null(jobs[2].ReferenceImagePath);
    }

    [Fact]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    /// <summary>작업 OutputPath 에 더미 산출물을 쓰고 디스패치 시점 참조 이미지·프롬프트 기록.</summary>
    private sealed class FileWritingEngine : IRenderExecutionEngine
    {
        public List<string?> SeenReferences { get; } = new();
        public List<string> SeenPrompts { get; } = new();
        public bool IsAvailable => true;
        public string EngineKind => RenderEngineKind.Mock;

        public Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
        {
            SeenReferences.Add(job.ReferenceImagePath);
            SeenPrompts.Add(job.Prompt);
            File.WriteAllText(job.OutputPath!, "video");
            return Task.FromResult(new RenderExecutionResult(true, EngineKind, job.OutputPath, "ok"));
        }
    }
}
