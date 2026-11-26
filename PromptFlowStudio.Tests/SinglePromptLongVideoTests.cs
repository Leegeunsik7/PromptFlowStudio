using System.Collections.Concurrent;
using System.Net;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>🎞️ 15초 원테이크(Wan 2.6 네이티브) · 30초 자동 — 단일 프롬프트 준비도 · 분할 계획 · DashScope duration 페이로드 · 2.1 폴백 · Pluto 9/6/2.</summary>
[Trait("Category", "FastUnit")]
public class SinglePromptLongVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_long_" + Guid.NewGuid().ToString("N"));

    public SinglePromptLongVideoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(VideoDurationMode.Sec15_OneTake, 15, 1, 15)]
    [InlineData(VideoDurationMode.Sec15_OneTake, 5, 3, 5)]
    [InlineData(VideoDurationMode.Sec30_Auto, 15, 2, 15)]
    [InlineData(VideoDurationMode.Sec30_Auto, 10, 3, 10)]
    [InlineData(VideoDurationMode.Sec30_Auto, 5, 6, 5)]
    public void SegmentPlan_CoversTargetDuration(VideoDurationMode mode, int native, int count, int seconds)
    {
        Assert.Equal((count, seconds), MultiDurationPlanner.SegmentPlan(mode, native));
        Assert.Equal(MultiDurationPlanner.TargetSeconds(mode), count * seconds);
        Assert.True(MultiDurationPlanner.IsSinglePromptLong(mode));
        Assert.False(MultiDurationPlanner.IsMultiShot(mode));   // 파트 입력 UI 없음
    }

    [Fact]
    public void Labels_Costs_AndExistingModesPreserved()
    {
        Assert.Equal("15초 · 원테이크", MultiDurationPlanner.Label(VideoDurationMode.Sec15_OneTake));
        Assert.Equal("30초 · 자동", MultiDurationPlanner.Label(VideoDurationMode.Sec30_Auto));
        Assert.Equal(3, MultiDurationPlanner.CostMultiplier(VideoDurationMode.Sec15_OneTake));
        Assert.Equal(6, MultiDurationPlanner.CostMultiplier(VideoDurationMode.Sec30_Auto));
        Assert.Equal(3, MultiDurationPlanner.CostMultiplier(VideoDurationMode.Sec15_Triple));
        Assert.Equal("10초 · 2컷", MultiDurationPlanner.Label(VideoDurationMode.Sec10_Dual));
        Assert.Equal(Path.Combine("d", "p_30s.mp4"), MultiDurationPlanner.BuildOutputPath("d", "p", VideoDurationMode.Sec30_Auto));
    }

    [Theory]
    [InlineData(VideoDurationMode.Sec15_OneTake)]
    [InlineData(VideoDurationMode.Sec30_Auto)]
    public void Readiness_SinglePromptOnly_NoPartsRequired(VideoDurationMode mode)
    {
        var emptyParts = new string?[3];
        Assert.True(WorkspaceGenerationReadiness.CanPress(false, "basketball player one continuous take", null, mode, emptyParts));
        Assert.Equal(WorkspaceStartBlock.None, WorkspaceGenerationReadiness.Evaluate(false, "basketball", null, mode, false, emptyParts));   // 렌더 큐 경로 — Wan 키 불요
        Assert.False(WorkspaceGenerationReadiness.CanPress(false, "", null, mode, new string?[] { "part one only", null, null }));          // 파트 폴백 없음(메인 전용)
        Assert.True(WorkspaceGenerationReadiness.CanPress(false, "", @"C:\ref.png", mode, emptyParts));                                       // I2V
    }

    [Fact]
    public void Expand_OneTakeNative_SingleFifteenSecondJob()
    {
        var t = new BatchRenderJob { Prompt = " slam dunk ", DurationMode = VideoDurationMode.Sec15_OneTake, OutputPath = @"C:\o\p.mp4", ReferenceImagePath = @"C:\r.png",
            Metadata = MultiDurationPlanner.WithMeta(MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaNativeSeconds, "15"), MultiDurationPlanner.MetaWanFamily, "Wan26") };
        var j = Assert.Single(MultiDurationPlanner.Expand(t, new string?[] { "ignored", null, null }, "p"));
        Assert.Equal(15f, j.DurationSec);
        Assert.Equal("slam dunk", j.Prompt);
        Assert.Null(j.StitchGroupId);
        Assert.Equal(@"C:\r.png", j.ReferenceImagePath);
        Assert.Equal("Wan26", j.Metadata![MultiDurationPlanner.MetaWanFamily]);

        var req = VideoEngineRenderExecutionEngine.BuildRequest(j);
        Assert.Equal(15, req.Duration);
        Assert.Equal(WanModelCatalog.Wan26T2v, req.Model);   // 참조 파일 없음 → T2V
    }

    [Fact]
    public void Expand_Thirty_SixBeats_OnFiveSecondBackend_WithContinuityAndGroup()
    {
        var t = new BatchRenderJob { Prompt = "girl in red uniform plays basketball", DurationMode = VideoDurationMode.Sec30_Auto, OutputPath = @"C:\o\p.mp4", ReferenceImagePath = @"C:\r.png" };
        var jobs = MultiDurationPlanner.Expand(t, null, "p");
        Assert.Equal(6, jobs.Length);
        var g = jobs[0].StitchGroupId;
        Assert.False(string.IsNullOrEmpty(g));
        for (var i = 0; i < jobs.Length; i++)
        {
            Assert.Equal(g, jobs[i].StitchGroupId);
            Assert.Equal(5f, jobs[i].DurationSec);
            Assert.Equal(i * 5f, jobs[i].StartSec);
            Assert.Equal(6, MultiDurationPlanner.ShotCountOf(jobs[i]));
            Assert.StartsWith("girl in red uniform plays basketball, " + MultiDurationPlanner.BeatOf(i, 6), jobs[i].Prompt);
            Assert.EndsWith(MultiDurationPlanner.ContinuityHint, jobs[i].Prompt);
            Assert.Equal(CutTransitionMode.IndependentCrossfade, MultiDurationPlanner.TransitionOf(jobs[i]));
            Assert.Equal(i == 0 ? @"C:\r.png" : null, jobs[i].ReferenceImagePath);
            Assert.Null(VideoEngineRenderExecutionEngine.BuildRequest(jobs[i]).Duration);   // 2.1/로컬 — duration 생략(5초)
        }
    }

    [Fact]
    public async Task Thirty_TwoByFifteen_OnWan26_RendersAndStitchesOneMp4()
    {
        var engine = new RecordingEngine();
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var stitcher = new FFmpegVideoStitchService(() => "ffmpeg", (_, args, _) =>
        {
            calls.Enqueue(args);
            File.WriteAllText(args[^1], "x");
            return Task.FromResult(true);
        });
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine) { Stitcher = stitcher };
        var meta = MultiDurationPlanner.WithMeta(MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaNativeSeconds, "15"), MultiDurationPlanner.MetaWanFamily, "Wan26");
        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "skate park", DurationMode = VideoDurationMode.Sec30_Auto, OutputPath = Path.Combine(_dir, "sk.mp4"), Metadata = meta }, null, "sk");
        Assert.Equal(2, jobs.Count);
        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.All(jobs, j => Assert.Equal(BatchJobStatus.Completed, j.Status));
        Assert.Equal(new int?[] { 15, 15 }, engine.Durations);
        Assert.DoesNotContain(calls, a => a.Contains("-sseof"));   // 독립 세그먼트(앵커 체인 없음)
        var graph = Assert.Single(calls, a => a.Contains("-filter_complex"));
        Assert.Contains("xfade=transition=fade:duration=0.25:offset=14.75", graph[Array.IndexOf(graph.ToArray(), "-filter_complex") + 1]);
        Assert.Equal(Path.Combine(_dir, "sk_30s.mp4"), jobs[1].Metadata![MultiDurationPlanner.MetaStitched]);
    }

    [Fact]
    public async Task DashScope_Wan26_SendsNativeFifteenSecondDuration()
    {
        var handler = new SubmitHandler();
        var svc = NewService(handler);
        var r = await svc.SubmitAsync(new WanVideoRequest { Prompt = "one continuous take of a surfer", Model = WanModelCatalog.Wan26T2v, Duration = 15 });
        Assert.True(r.Ok, r.Message);
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"model\":\"wan2.6-t2v\"", body);
        Assert.Contains("\"duration\":15", body);
    }

    [Fact]
    public async Task DashScope_Wan26Unsupported_FallsBackToWan21FiveSeconds()
    {
        var handler = new SubmitHandler { RejectWan26 = true };
        var svc = NewService(handler);
        var r = await svc.SubmitAsync(new WanVideoRequest { Prompt = "surfer", Model = WanModelCatalog.Wan26T2v, Duration = 15 });
        Assert.True(r.Ok, r.Message);
        Assert.Contains(WanModelCatalog.FallbackNotice, r.Message);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"model\":\"wan2.1-t2v-turbo\"", handler.Bodies[1]);
        Assert.DoesNotContain("\"duration\"", handler.Bodies[1]);
    }

    [Fact]
    public void Catalog_ErrorClassification_And_DurationSnap()
    {
        Assert.True(WanModelCatalog.IsUnsupportedModelError("제출 실패(DashScope · HTTP 400 · InvalidParameter): Model not exist."));
        Assert.True(WanModelCatalog.IsUnsupportedModelError("code Model.AccessDenied"));
        Assert.False(WanModelCatalog.IsUnsupportedModelError("HTTP 429 · Throttling: quota"));
        Assert.False(WanModelCatalog.IsUnsupportedModelError(null));
        Assert.Equal(15, WanModelCatalog.DurationFor(WanModelFamily.Wan26, 15));
        Assert.Equal(10, WanModelCatalog.DurationFor(WanModelFamily.Wan26, 10));
        Assert.Null(WanModelCatalog.DurationFor(WanModelFamily.Wan26, 5));
        Assert.Null(WanModelCatalog.DurationFor(WanModelFamily.Wan21, 15));
        Assert.Equal(WanModelFamily.Wan26, WanModelCatalog.ParseFamily("wan2.6-i2v"));
        Assert.Equal(WanModelFamily.Wan21, WanModelCatalog.ParseFamily(""));
        Assert.Equal(WanModelCatalog.Wan26I2v, WanModelCatalog.ModelFor(WanModelFamily.Wan26, true));
        IWanConfigService fake = new WanEngineTests.FakeWanConfig();
        Assert.Equal(WanModelFamily.Wan21, fake.ModelFamily);   // 기본 인터페이스 구현 — 기존 구현체 호환
        Assert.False(fake.SaveModelFamily(WanModelFamily.Wan26));
    }

    [Fact]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    private static WanApiService NewService(HttpMessageHandler handler)
    {
        var vault = new WanTokenVault(null);
        vault.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-1" });
        return new WanApiService(new WanEngineTests.FakeWanConfig(), new QuotaRateGuardService(), vault, handler);
    }

    /// <summary>DashScope 제출 모의 — 본문 기록 · RejectWan26 이면 wan2.6 모델을 400 InvalidParameter 로 거부.</summary>
    private sealed class SubmitHandler : HttpMessageHandler
    {
        public bool RejectWan26 { get; init; }
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (!url.Contains("/video-synthesis", StringComparison.Ordinal))
                return Json(HttpStatusCode.NotFound, "{}");
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return RejectWan26 && body.Contains("wan2.6", StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest, "{\"code\":\"InvalidParameter\",\"message\":\"Model not exist.\",\"request_id\":\"r\"}")
                : Json(HttpStatusCode.OK, "{\"output\":{\"task_id\":\"t-1\",\"task_status\":\"PENDING\"},\"request_id\":\"r\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode sc, string body)
            => new(sc) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    /// <summary>산출물 기록 + 엔진 요청 duration 기록(<see cref="VideoEngineRenderExecutionEngine.BuildRequest"/>).</summary>
    private sealed class RecordingEngine : IRenderExecutionEngine
    {
        public List<int?> Durations { get; } = new();
        public bool IsAvailable => true;
        public string EngineKind => RenderEngineKind.Mock;

        public Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
        {
            Durations.Add(VideoEngineRenderExecutionEngine.BuildRequest(job).Duration);
            File.WriteAllText(job.OutputPath!, "video");
            return Task.FromResult(new RenderExecutionResult(true, EngineKind, job.OutputPath, "ok"));
        }
    }
}
