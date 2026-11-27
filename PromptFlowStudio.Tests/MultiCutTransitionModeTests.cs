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

    // ── 🎞️ 4컷 성장 일기 · 15초 릴스 — 컷 간 앵커 동기화 · 결함 컷 단건 재생성 · 컷별 QC 배지 ──

    [Fact]
    public void AnchorSync_ExtractsWardrobeAndSetting_PropagatesToLaterCutsOnly_Idempotent()
    {
        var a = MultiCutContinuity.Extract("a baby girl, wearing a pink hanbok, sunny living room with wooden floor, smiling softly", "a baby girl");
        Assert.Equal("a baby girl", a.Subject);
        Assert.Equal("wearing a pink hanbok", a.Wardrobe);
        Assert.Equal("sunny living room with wooden floor", a.Setting);

        Assert.Equal("first cut", MultiCutContinuity.Apply("first cut, ", a, 1));   // 컷 1 은 그대로(학습 가이던스 없음)
        var c2 = MultiCutContinuity.Apply("crawling toward the camera", a, 2);
        Assert.Contains(MultiCutContinuity.WardrobePrefix + "wearing a pink hanbok", c2);
        Assert.Contains(MultiCutContinuity.SettingPrefix + "sunny living room with wooden floor", c2);
        Assert.EndsWith(MultiCutContinuity.SubjectPrefix + "a baby girl", c2);   // 기존 계약 — 피사체 구문은 항상 마지막
        Assert.Equal(c2, MultiCutContinuity.Apply(c2, a, 2));                   // 멱등

        // 피사체 문구에 포함된 절은 의상/장소로 중복 추출하지 않음
        var whole = MultiCutContinuity.Extract("a red-haired girl in a yellow raincoat", "a red-haired girl in a yellow raincoat");
        Assert.Null(whole.Wardrobe);
        Assert.Null(whole.Setting);
        Assert.True(MultiCutContinuity.Extract(null, null).IsEmpty);
    }

    [Fact]
    public void StoryboardDispatch_SyncsSharedAnchorAndLearnedRulesAcrossAllCuts_WithHardCutMeta()
    {
        var mem = new PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService(Path.Combine(_dir, "mem.json"));
        const string line1 = "A baby girl, wearing a pink hanbok, sunny living room with wooden floor, waking up";
        mem.RecordFailureAndGetRefinement(line1, PromptFlowStudio.Services.AI.PlutoFeedbackReason.FramingJump);
        var guidance = mem.GetLearnedGuidanceForPrompt(line1);
        Assert.NotEmpty(guidance.PromptAnchors);

        using var vm = new PromptFlowStudio.ViewModels.StoryboardViewModel(new PromptFlowStudio.Services.Storyboard.StoryboardSequencerService(), mem) { SubjectIdentity = "A baby girl" };
        vm.ReceiveGeneratedScript(line1 + "\nShe crawls toward a red ball.\nThe puppy joins her on the rug.\nThey fall asleep together at dusk.");
        vm.AttachProject(new PromptFlowStudio.Services.Storyboard.StoryboardProject());
        Assert.Equal(4, vm.Cuts.Count);
        var shared = MultiCutContinuity.Extract(vm.Cuts[0].RawPrompt, "A baby girl");
        Assert.NotNull(shared.Wardrobe);

        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        Assert.Equal(4, vm.DispatchToRenderQueue(q));
        var jobs = q.Snapshot();
        var learned = guidance.PromptAnchors[0];
        for (var i = 0; i < 4; i++)
        {
            Assert.Contains(learned, jobs[i].Prompt, StringComparison.OrdinalIgnoreCase);   // 🧠 학습 앵커 — 컷 1~4 동일
            Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.TransitionOf(jobs[i]));
            Assert.Equal(vm.LastDispatchGroupId, MultiDurationPlanner.MetaOf(jobs[i], FFmpegVideoStitchService.MetaGroup));   // 하드 컷 메타가 그룹 키를 덮지 않음
            if (i == 0) continue;
            Assert.Contains(shared.Wardrobe!, jobs[i].Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(MultiCutContinuity.SubjectPrefix + "A baby girl", jobs[i].Prompt);
        }
        Assert.False(MultiDurationPlanner.ShouldChainLastFrame(jobs[1]));
    }

    [Fact]
    public void SingleCutRegeneration_RequeuesOnlyDefectiveCut_ResetsQc_KeepsGroupOrder()
    {
        using var vm = new PromptFlowStudio.ViewModels.StoryboardViewModel(new PromptFlowStudio.Services.Storyboard.StoryboardSequencerService()) { MasterSeed = 500L };
        vm.ReceiveGeneratedScript("A cat naps.\nThe cat stretches.\nThe cat eats.\nThe cat sleeps.");
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        Assert.Equal(4, vm.DispatchToRenderQueue(q));
        var group = vm.LastDispatchGroupId;
        var jobs = q.Snapshot();

        Assert.True(MultiCutContinuity.IsGroupInFlight(jobs, group));
        Assert.False(vm.RegenerateCut(q, 3));   // 대기 중 컷은 재생성 불가(중복 렌더 차단)

        foreach (var j in jobs) { j.Status = BatchJobStatus.Completed; j.ResultPath = Path.Combine(_dir, "c" + j.CutIndex + ".mp4"); }
        jobs[1].Metadata = MultiDurationPlanner.WithMeta(MultiDurationPlanner.WithMeta(jobs[1].Metadata, RenderQueueService.MetaQcSelfHealed, "true"), RenderQueueService.MetaQcDiagnosis, "검정 프레임");
        jobs[2].Metadata = MultiDurationPlanner.WithMeta(MultiDurationPlanner.WithMeta(jobs[2].Metadata, RenderQueueService.MetaQcSelfHealed, "true"), RenderQueueService.MetaQcDegraded, "정지 화면");
        jobs[2].Metadata = MultiDurationPlanner.WithMeta(jobs[2].Metadata, RenderQueueService.MetaQcRawPrompt, "The cat eats raw");
        Assert.False(MultiCutContinuity.IsGroupInFlight(jobs, group));

        var before = MultiCutContinuity.GroupStatuses(q.Snapshot(), group);
        Assert.Equal(new[] { 1, 2, 3, 4 }, before.Select(s => s.CutIndex));
        Assert.Equal(new[] { CutQcState.Passed, CutQcState.SelfHealed, CutQcState.Degraded, CutQcState.Passed }, before.Select(s => s.State));
        Assert.Equal("검정 프레임", before[1].Diagnosis);
        var sig0 = MultiCutContinuity.StatusSignature(q.Snapshot(), group);

        var oldSeed = jobs[2].Seed;
        Assert.True(vm.RegenerateCut(q, 3));
        Assert.Equal(BatchJobStatus.Queued, jobs[2].Status);
        Assert.NotEqual(oldSeed, jobs[2].Seed);
        Assert.Equal(MultiCutContinuity.RegenSeed(oldSeed, 1), jobs[2].Seed);
        Assert.Equal("The cat eats raw", jobs[2].Prompt);   // 안정화 앵커 누적 대신 원문 복귀
        Assert.Null(MultiDurationPlanner.MetaOf(jobs[2], RenderQueueService.MetaQcDegraded));
        Assert.Null(MultiDurationPlanner.MetaOf(jobs[2], RenderQueueService.MetaQcSelfHealed));   // 자가 치유 1회 다시 허용
        Assert.Equal(group, MultiDurationPlanner.MetaOf(jobs[2], FFmpegVideoStitchService.MetaGroup));
        Assert.Null(jobs[2].ResultPath);
        Assert.All(new[] { jobs[0], jobs[1], jobs[3] }, j => Assert.Equal(BatchJobStatus.Completed, j.Status));   // 나머지 컷 유지

        Assert.Equal((4, 3), FFmpegVideoStitchService.GroupProgress(q.Snapshot(), group));
        var after = MultiCutContinuity.GroupStatuses(q.Snapshot(), group);
        Assert.Equal(CutQcState.Waiting, after[2].State);
        Assert.Equal(1, after[2].Regenerations);
        Assert.NotEqual(sig0, MultiCutContinuity.StatusSignature(q.Snapshot(), group));
        Assert.False(vm.RegenerateCut(q, 3));   // 재적재된 컷 재클릭 → 거부

        jobs[2].Status = BatchJobStatus.Failed;
        Assert.True(vm.RegenerateCut(q, 3));
        Assert.Equal(MultiCutContinuity.RegenSeed(MultiCutContinuity.RegenSeed(oldSeed, 1), 2), jobs[2].Seed);
        Assert.Equal("2", MultiDurationPlanner.MetaOf(jobs[2], MultiCutContinuity.MetaRegenCount));
        Assert.False(vm.RegenerateCut(q, 9));
        Assert.False(MultiCutContinuity.RegenerateCut(q, null, 1));
    }

    [Fact]
    public void CutQcState_MapsQueueStatusAndQcMeta_AndBadgesCoverEveryState()
    {
        var j = new BatchRenderJob { Status = BatchJobStatus.Queued };
        Assert.Equal(CutQcState.Waiting, MultiCutContinuity.StateOf(j));
        j.Metadata = MultiDurationPlanner.WithMeta(null, RenderQueueService.MetaQcSelfHealed, "true");
        Assert.Equal(CutQcState.Healing, MultiCutContinuity.StateOf(j));
        j.Status = BatchJobStatus.Processing;
        Assert.Equal(CutQcState.Rendering, MultiCutContinuity.StateOf(j));
        j.Status = BatchJobStatus.Cancelled;
        Assert.Equal(CutQcState.Failed, MultiCutContinuity.StateOf(j));
        foreach (var s in Enum.GetValues<CutQcState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(MultiCutContinuity.Label(s)));
            Assert.Contains(MultiCutContinuity.Css(s), new[] { "ok", "warn", "bad", "run" });
        }
        Assert.False(MultiCutContinuity.CanRegenerate(CutQcState.Rendering));
        Assert.False(MultiCutContinuity.CanRegenerate(CutQcState.Healing));
        Assert.True(MultiCutContinuity.CanRegenerate(CutQcState.Degraded));
        Assert.InRange(MultiCutContinuity.RegenSeed(long.MaxValue, 3), 1, int.MaxValue);
    }

    [Fact]
    public void ReelsDispatch_HardCutMeta_InFlightGuard_AndSingleCutRegeneration()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var vm = new PromptFlowStudio.ViewModels.AiShortsViewModel(new PromptFlowStudio.Services.Shorts.AiShortsService(), q) { TopicInput = "강아지 산책, wearing a red raincoat" };
        Assert.False(vm.IsLastDispatchInFlight);
        Assert.Equal(3, vm.DispatchToRenderQueue());
        Assert.True(vm.IsLastDispatchInFlight);   // UI 전송 버튼 비활성 근거
        var jobs = q.Snapshot();
        Assert.All(jobs, j => Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.TransitionOf(j)));
        Assert.All(jobs, j => Assert.False(string.IsNullOrEmpty(MultiDurationPlanner.MetaOf(j, FFmpegVideoStitchService.MetaScript))));
        Assert.False(vm.RegenerateCut(2));

        foreach (var j in jobs) j.Status = BatchJobStatus.Completed;
        Assert.False(vm.IsLastDispatchInFlight);
        Assert.Equal(3, vm.CutStatuses().Count);
        Assert.True(vm.RegenerateCut(2));
        Assert.Equal(BatchJobStatus.Queued, jobs[1].Status);
        Assert.Equal(BatchJobStatus.Completed, jobs[0].Status);
        Assert.True(vm.IsLastDispatchInFlight);
        Assert.Contains("재생성", vm.StatusMessage);
    }

    [Fact]
    public void MultiCutPages_GuardDuplicateStitch_ResetStaleOutput_CancelOnNavigation()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Launcher.csproj"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        var sb = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Storyboard.razor"));
        var reels = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "AiShortsCutdown.razor"));

        // 4컷: 자동 합성 중 수동 합본 차단(양방향) · 컷별 QC 배지 + 단건 재생성 · 이탈 시 합본 취소
        Assert.Contains("disabled=\"@(_stitching || _autoStitching || _stitchDone < _stitchTotal)\"", sb, StringComparison.Ordinal);
        Assert.Contains("if (_stitching || _autoStitching) return;   // 자동 합성과 동시 실행 차단", sb, StringComparison.Ordinal);
        Assert.Contains("data-cut-qc-strip", sb, StringComparison.Ordinal);
        Assert.Contains("Vm.RegenerateCut(RenderQueue, cutIndex)", sb, StringComparison.Ordinal);
        Assert.Contains("_autoStitchedGroup = null;   // 재합성 허용", sb, StringComparison.Ordinal);
        Assert.Contains("try { _stitchCts?.Cancel(); }", sb, StringComparison.Ordinal);

        // 15초 릴스: 렌더 중 재전송 차단 · 새 전송/재생성 시 낡은 합본 해제 · 컷별 재생성 · 이탈 시 합본 취소
        Assert.Contains("disabled=\"@(inFlight || _stitching)\"", reels, StringComparison.Ordinal);
        Assert.Contains("if (_stitching || Vm.IsLastDispatchInFlight) return;", reels, StringComparison.Ordinal);
        Assert.Contains("_fullVideoPath = null;   // 이전 합본은 새 전송과 무관", reels, StringComparison.Ordinal);
        Assert.Contains("data-cut-regen", reels, StringComparison.Ordinal);
        Assert.Contains("try { _stitchCts?.Cancel(); }", reels, StringComparison.Ordinal);
        Assert.Contains("ScriptTts, Subtitles, cts.Token)", reels, StringComparison.Ordinal);
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
