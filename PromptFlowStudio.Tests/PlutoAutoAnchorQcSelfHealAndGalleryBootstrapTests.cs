using System.Net;
using System.Net.Http;
using System.Text;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>🎯 자동 시각 앵커 · 🩺 렌더 후 QC 자가 치유(1회) · 📥 갤러리 학습 흡수(멱등) · Pluto 9/6/2 · DI 패리티.</summary>
[Trait("Category", "FastUnit")]
public class PlutoAutoAnchorQcSelfHealAndGalleryBootstrapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_aaqc_" + Guid.NewGuid().ToString("N"));
    private string MemPath => Path.Combine(_dir, PlutoVideoLearningMemoryService.FileName);

    public PlutoAutoAnchorQcSelfHealAndGalleryBootstrapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Prompt = "a toddler runs across the park chasing soap bubbles";

    // ── 🎯 자동 시각 앵커 ──

    [Fact]
    public async Task AutoAnchor_AttachesRemoteFirstFrame_WhenNoManualPreview()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var h = new StubHandler(_ => Image(png));
        var gemini = Gem(h, () => "k");
        var saveDir = Path.Combine(_dir, "_uploads");

        var r = await RenderQueueService.TryAutoVisualAnchorAsync(gemini, Prompt, "1280*720", null, true, saveDir);

        Assert.True(r.Generated);
        Assert.NotNull(r.Path);
        Assert.True(File.Exists(r.Path));
        Assert.StartsWith(saveDir, r.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(png, File.ReadAllBytes(r.Path!));
        Assert.True(h.Calls >= 1);

        // 컷 1 I2V 시드로 전달되는지 — 렌더 요청 ImagePath 바인딩
        var req = VideoEngineRenderExecutionEngine.BuildRequest(new BatchRenderJob { Prompt = Prompt, Resolution = "1280x720", ReferenceImagePath = r.Path });
        Assert.Equal(Path.GetFullPath(r.Path!), req.ImagePath);
    }

    [Fact]
    public async Task AutoAnchor_KeepsPinnedReference_WithoutCallingGemini()
    {
        var h = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var gemini = Gem(h, () => "k");
        var pinned = Path.Combine(_dir, "pinned.png");

        var r = await RenderQueueService.TryAutoVisualAnchorAsync(gemini, Prompt, "1280*720", pinned, true, _dir);

        Assert.Equal(pinned, r.Path);
        Assert.False(r.Generated);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task AutoAnchor_SkipsGracefully_WhenNoKey_Disabled_OrOffline()
    {
        var noKeyHandler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var noKey = await RenderQueueService.TryAutoVisualAnchorAsync(Gem(noKeyHandler, () => null), Prompt, "1280*720", null, true, _dir);
        Assert.Null(noKey.Path);
        Assert.Equal(0, noKeyHandler.Calls);
        Assert.False(Gem(noKeyHandler, () => " ").HasApiKey);

        var off = await RenderQueueService.TryAutoVisualAnchorAsync(Gem(noKeyHandler, () => "k"), Prompt, "1280*720", null, false, _dir);
        Assert.Null(off.Path);
        Assert.Equal(0, noKeyHandler.Calls);

        // 오프라인 — 원격 실패 시 로컬 구도 PNG 폴백은 무시(시드 미부착 · throw 없음)
        var offline = Gem(new StubHandler(_ => throw new HttpRequestException("offline")), () => "k");
        var direct = await offline.GenerateFirstFramePreviewAsync(Prompt, "1280*720");
        Assert.False(direct.Ok);
        Assert.Null(direct.Image);
        var r = await RenderQueueService.TryAutoVisualAnchorAsync(offline, Prompt, "1280*720", null, true, Path.Combine(_dir, "off"));
        Assert.Null(r.Path);
        Assert.False(r.Generated);
        Assert.False(Directory.Exists(Path.Combine(_dir, "off")) && Directory.GetFiles(Path.Combine(_dir, "off")).Length > 0);
    }

    // ── 🩺 렌더 후 QC ──

    private static byte[] Frame(byte luma, int seed = 0)
    {
        var f = new byte[FFmpegVideoStitchService.QcThumbWidth * FFmpegVideoStitchService.QcThumbHeight];
        for (var i = 0; i < f.Length; i++) f[i] = (byte)Math.Clamp(luma + (seed == 0 ? 0 : (i * seed) % 40), 0, 255);
        return f;
    }

    [Fact]
    public void EvaluateFrames_DetectsFrozenBlackCorruptAndZeroDuration()
    {
        Assert.False(FFmpegVideoStitchService.EvaluateFrames(5, new[] { Frame(120, 1), Frame(90, 7), Frame(140, 13) }).IsSevere);

        var frozen = FFmpegVideoStitchService.EvaluateFrames(5, new[] { Frame(120, 3), Frame(120, 3), Frame(120, 3) });
        Assert.True(frozen.Checked);
        Assert.Equal(RenderQualityDefect.Frozen, frozen.Defects);
        Assert.Contains("정지 화면", frozen.SummaryKo);

        var black = FFmpegVideoStitchService.EvaluateFrames(5, new[] { Frame(2), Frame(3), Frame(1) });
        Assert.Equal(RenderQualityDefect.NearBlack, black.Defects);   // 검정은 정지로 중복 판정하지 않음

        var corrupt = FFmpegVideoStitchService.EvaluateFrames(5, new byte[]?[] { null, new byte[3], null });
        Assert.True((corrupt.Defects & RenderQualityDefect.Corrupt) != 0);

        var zero = FFmpegVideoStitchService.EvaluateFrames(0, Array.Empty<byte[]?>());
        Assert.Equal(RenderQualityDefect.ZeroDuration, zero.Defects);

        // 짧은 정적 샷은 정지 판정 생략(오탐 방지)
        Assert.False(FFmpegVideoStitchService.EvaluateFrames(1.0, new[] { Frame(120, 3), Frame(120, 3), Frame(120, 3) }).IsSevere);
    }

    [Fact]
    public async Task AnalyzeRenderQuality_UsesInjectedSampler_AndSkipsWithoutFfmpeg()
    {
        var clip = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(clip, new byte[] { 1 });
        var svc = new FFmpegVideoStitchService(() => "ffmpeg", (_, _, _) => Task.FromResult(true))
        {
            QualitySampler = (_, _, _) => Task.FromResult<RenderFrameSamples?>(new RenderFrameSamples(5, new[] { Frame(80, 2), Frame(80, 2), Frame(80, 2) })),
        };
        var r = await svc.AnalyzeRenderQualityAsync(clip);
        Assert.True(r.IsSevere);
        Assert.Equal(RenderQualityDefect.Frozen, r.Defects);

        var none = await new FFmpegVideoStitchService(() => null, (_, _, _) => Task.FromResult(true)).AnalyzeRenderQualityAsync(clip);
        Assert.False(none.Checked);
        Assert.False(none.IsSevere);
    }

    [Fact]
    public async Task FrozenOutput_RecordsLearningRule_AndRetriesExactlyOnce_ThenSurfacesClip()
    {
        var mem = new PlutoVideoLearningMemoryService(MemPath);
        var prompts = new List<string>();
        var engine = new FakeEngine(job =>
        {
            prompts.Add(job.Prompt);
            var output = Path.Combine(_dir, "clip_" + prompts.Count + ".mp4");
            File.WriteAllBytes(output, new byte[] { 0 });
            return new RenderExecutionResult(true, RenderEngineKind.ComfyUi, output, "ok");
        });
        var probes = 0;
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine)
        {
            LearningMemory = mem,
            QualityProbe = (_, _) => { probes++; return Task.FromResult(FFmpegVideoStitchService.EvaluateFrames(5, new[] { Frame(100, 5), Frame(100, 5), Frame(100, 5) })); },
        };
        var job = new BatchRenderJob { Prompt = Prompt, Seed = 1 };
        q.Enqueue(new[] { job });

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(2, engine.Calls);   // 원 렌더 1 + 자가 치유 재시도 1(상한)
        Assert.Equal(2, probes);
        Assert.Equal(BatchJobStatus.Completed, job.Status);   // 재시도 후에도 결함 → 최종 클립은 노출
        Assert.Equal("true", MultiDurationPlanner.MetaOf(job, RenderQueueService.MetaQcSelfHealed));
        Assert.Contains("정지 화면", MultiDurationPlanner.MetaOf(job, RenderQueueService.MetaQcDegraded));
        Assert.Equal(Prompt, prompts[0]);
        Assert.Contains(RenderQueueService.QcStabilizeAnchor, prompts[1]);
        Assert.Contains("frozen frame", MultiDurationPlanner.MetaOf(job, VideoEngineRenderExecutionEngine.MetaNegativePrompt));

        var rule = Assert.Single(mem.Snapshot(), r => r.Kind == "avoid");
        Assert.Equal(PlutoVideoLearningMemoryService.QcReasonLabel, rule.Reason);
        Assert.Equal(2, rule.Hits);   // 두 번 모두 같은 규칙 강화(원문 키 기준)
        Assert.Contains("toddler", rule.Keywords);
        var g = mem.GetLearnedGuidanceForPrompt("toddler chasing bubbles in a sunny park");
        Assert.Contains(PlutoVideoLearningMemoryService.QcAvoidAnchor, g.PromptAnchors);
    }

    [Fact]
    public async Task HealthyOutput_CompletesWithoutRetry_AndMockEngineSkipsQc()
    {
        var mem = new PlutoVideoLearningMemoryService(MemPath);
        var engine = new FakeEngine(job =>
        {
            var output = Path.Combine(_dir, "ok_" + job.Seed + ".mp4");
            File.WriteAllBytes(output, new byte[] { 0 });
            return new RenderExecutionResult(true, RenderEngineKind.ComfyUi, output, "ok");
        });
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine)
        {
            LearningMemory = mem,
            QualityProbe = (_, _) => Task.FromResult(FFmpegVideoStitchService.EvaluateFrames(5, new[] { Frame(120, 1), Frame(90, 7), Frame(140, 13) })),
        };
        var job = new BatchRenderJob { Prompt = Prompt, Seed = 3 };
        q.Enqueue(new[] { job });
        await q.ProcessQueueAsync(CancellationToken.None);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Null(MultiDurationPlanner.MetaOf(job, RenderQueueService.MetaQcSelfHealed));
        Assert.Equal(0, mem.LearnedRuleCount);

        var mockEngine = new FakeEngine(j =>
        {
            var output = Path.Combine(_dir, "mock_" + j.Seed + ".mp4");
            File.WriteAllBytes(output, new byte[] { 0 });
            return new RenderExecutionResult(true, RenderEngineKind.Mock, output, "ok");
        });
        var called = 0;
        using var mq = new RenderQueueService(TimeSpan.Zero, autoStart: false, mockEngine) { QualityProbe = (_, _) => { called++; return Task.FromResult(RenderQualityReport.Skipped("x")); } };
        mq.Enqueue(new[] { new BatchRenderJob { Prompt = Prompt, Seed = 4 } });
        await mq.ProcessQueueAsync(CancellationToken.None);
        Assert.Equal(0, called);
    }

    [Fact]
    public void StabilizeAndNegativeMerge_AreIdempotent()
    {
        var once = RenderQueueService.StabilizeForRetry(Prompt + ".");
        Assert.Equal(once, RenderQueueService.StabilizeForRetry(once));
        var neg = RenderQueueService.MergeQcNegative("blurry, Frozen Frame");
        Assert.Equal(neg, RenderQueueService.MergeQcNegative(neg));
        Assert.Equal(1, CountOf(neg.ToLowerInvariant(), "frozen frame"));
    }

    // ── 📥 갤러리 학습 흡수 ──

    [Fact]
    public void GalleryBootstrap_ImportsBaselineRecipes_IdempotentAndDeduplicated()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(Path.Combine(media, "sub"));
        File.WriteAllText(Path.Combine(media, "a.meta.json"), "{\"prompt\":\"a golden retriever walks along a sunset beach, warm backlit golden hour haze, slow dolly\",\"engine\":\"ComfyUI\"}");
        File.WriteAllText(Path.Combine(media, "sub", "b.meta.json"), "{\"prompt\":\"A golden retriever walks along a   sunset beach, warm backlit golden hour haze, slow dolly\"}");   // 정규화 중복
        File.WriteAllText(Path.Combine(media, "c.meta.json"), "{\"prompt\":\"testsrc mock clip\",\"isMock\":true}");
        File.WriteAllText(Path.Combine(media, "d.meta.json"), "{ broken");
        File.WriteAllText(Path.Combine(media, "e.meta.json"), "{\"prompt\":\"matte black earbuds rotate on a marble pedestal, soft studio rim light\"}");

        var mem = new PlutoVideoLearningMemoryService(MemPath);
        var first = mem.ImportGalleryBaselineFromDirectory(media);
        Assert.Equal(5, first.Scanned);
        Assert.Equal(2, first.Imported);
        Assert.Equal(1, first.Duplicates);
        Assert.Equal(2, first.Skipped);   // mock + 손상
        Assert.Equal(2, mem.LearnedRuleCount);
        var recipe = Assert.Single(mem.Snapshot(), r => r.Kind == "recipe" && r.Keywords.Contains("retriever"));
        Assert.Equal(PlutoVideoLearningMemoryService.GalleryBaselineWeight, recipe.Weight);
        Assert.Contains("warm backlit golden hour haze", recipe.PromptAnchor);
        Assert.DoesNotContain("golden retriever walks", recipe.PromptAnchor);   // 피사체 절은 힌트에서 제외

        var again = new PlutoVideoLearningMemoryService(MemPath).ImportGalleryBaselineFromDirectory(media);   // 재시작 후 재실행
        Assert.Equal(0, again.Imported);
        Assert.Equal(3, again.Duplicates);
        var reloaded = new PlutoVideoLearningMemoryService(MemPath);
        Assert.Equal(2, reloaded.LearnedRuleCount);
        Assert.Equal(PlutoVideoLearningMemoryService.GalleryBaselineWeight, Assert.Single(reloaded.Snapshot(), r => r.Id == recipe.Id).Weight);

        var g = reloaded.GetLearnedGuidanceForPrompt("golden retriever on the beach");
        Assert.Equal(1, g.LearnedCount);
        Assert.Equal(PlutoVideoLearningMemoryService.PromptHash("A  Dog "), PlutoVideoLearningMemoryService.PromptHash("a dog"));
        Assert.Equal(0, reloaded.ImportGalleryBaselineFromDirectory(Path.Combine(_dir, "missing")).Scanned);
    }

    // ── UI 결선 · 불변식 · DI 패리티 ──

    [Fact]
    public void Workspace_WiresAutoAnchor_MagicExpand_AndGalleryImport()
    {
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("🎯 자동 첫 프레임 시드 고정", ws, StringComparison.Ordinal);
        Assert.Contains("_autoAnchorOn = GeminiSvc.HasApiKey;", ws, StringComparison.Ordinal);
        Assert.Contains("RenderQueueService.TryAutoVisualAnchorAsync(", ws, StringComparison.Ordinal);
        Assert.Contains("✨ 플루토 감독 확장", ws, StringComparison.Ordinal);
        Assert.Contains("📥 기존 갤러리 학습 흡수", ws, StringComparison.Ordinal);
        Assert.Contains("PlutoLearn.ImportGalleryBaselineFromDirectory(", ws, StringComparison.Ordinal);
        Assert.Contains("rqLearn.LearningMemory ??= PlutoLearn;", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void PlutoInvariants_AndDiParity_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);

        var root = RepoRoot();
        var desktop = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "MainForm.cs"));
        var mobile = File.ReadAllText(Path.Combine(root, "PromptFlowStudio.Mobile", "MauiProgram.cs"));
        foreach (var reg in new[]
        {
            "AddSingleton(_ => new PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService())",
            "AddSingleton<PromptFlowStudio.Services.Render.IRenderQueueService, PromptFlowStudio.Services.Render.RenderQueueService>()",
        })
        {
            Assert.Equal(1, CountOf(desktop, reg));
            Assert.Equal(1, CountOf(mobile, reg));
        }
    }

    private static GeminiPromptService Gem(HttpMessageHandler h, Func<string?> key) => new(new HttpClient(h), key)
    {
        Resilience = new PromptFlowStudio.Services.Network.ResilientHttpClient { Delay = static (_, _) => Task.CompletedTask, Jitter = static () => 0 },
    };

    private sealed class FakeEngine(Func<BatchRenderJob, RenderExecutionResult> run) : IRenderExecutionEngine
    {
        public int Calls;
        public bool IsAvailable => true;
        public string EngineKind => RenderEngineKind.ComfyUi;

        public Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(run(job));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(f(request));
        }
    }

    private static HttpResponseMessage Image(byte[] png) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"" + Convert.ToBase64String(png) + "\"}}]}}]}",
            Encoding.UTF8, "application/json"),
    };

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Launcher.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
