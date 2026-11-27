using System.Text.Json;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🖼️ 미디어 앨범 메타 배지 · 👍 학습 등록 · 🗑️ 중복/실패 정리 · 🎞️ 렌더 큐 🎲 새 시드 재시도 · 🧹 완료/실패 정리 ·
/// 자율 발견 결함(재정렬 미렌더 · durationSec 뱃지 누락 · 고아 포스터 · 재생 중 삭제 잠금 · 정리 중복 실행) 회귀 잠금.
/// </summary>
[Trait("Category", "FastUnit")]
public class GalleryQueueRefinementTests : IDisposable
{
    private const string Prompt = "golden retriever running on a sunny beach, cinematic slow motion";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_gq_" + Guid.NewGuid().ToString("N"));

    public GalleryQueueRefinementTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── 🎞️ 렌더 큐 ──

    [Fact]
    public void RetryFresh_FailedJob_RequeuesWithNewSeed_StabilizedPrompt_AndRawRestoredOnNextRegen()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var job = new BatchRenderJob { Prompt = Prompt, Seed = 42 };
        q.Enqueue(new[] { job });
        job.Status = BatchJobStatus.Failed;
        job.ErrorMessage = "boom";

        Assert.True(q.RetryFresh(job.JobId, 777, stabilize: true));
        Assert.Equal(BatchJobStatus.Queued, job.Status);
        Assert.Equal(777, job.Seed);
        Assert.Null(job.ErrorMessage);
        Assert.Contains(RenderQueueService.QcStabilizeAnchor, job.Prompt, StringComparison.Ordinal);
        Assert.Equal(Prompt, MultiDurationPlanner.MetaOf(job, RenderQueueService.MetaQcRawPrompt));

        job.Status = BatchJobStatus.Failed;
        Assert.True(q.RegenerateJob(job.JobId, 778));   // 다음 재생성 — 원문 복귀(앵커 누적 없음)
        Assert.Equal(Prompt, job.Prompt);
        Assert.Equal(778, job.Seed);
    }

    [Fact]
    public void RetryFresh_RejectsActiveJobs()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var job = new BatchRenderJob { Prompt = Prompt, Seed = 1 };
        q.Enqueue(new[] { job });
        Assert.False(q.RetryFresh(job.JobId, 2, stabilize: true));
        Assert.Equal(1, job.Seed);
    }

    [Fact]
    public void ClearFinished_RemovesTerminalEntries_KeepsLiveGroups_AndNeverDeletesMedia()
    {
        var media = Path.Combine(_dir, "done.mp4");
        File.WriteAllBytes(media, new byte[4096]);
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var done = new BatchRenderJob { Prompt = "a", OutputPath = media };
        var failed = new BatchRenderJob { Prompt = "b" };
        var cancelled = new BatchRenderJob { Prompt = "c" };
        var waiting = new BatchRenderJob { Prompt = "d" };
        var groupDone = new BatchRenderJob { Prompt = "e", StitchGroupId = "g1" };
        var groupLive = new BatchRenderJob { Prompt = "f", StitchGroupId = "g1" };
        q.Enqueue(new[] { done, failed, cancelled, waiting, groupDone, groupLive });
        done.Status = BatchJobStatus.Completed;
        failed.Status = BatchJobStatus.Failed;
        cancelled.Status = BatchJobStatus.Cancelled;
        groupDone.Status = BatchJobStatus.Completed;

        Assert.Equal(3, q.ClearFinished());
        Assert.Contains(waiting, q.Queue);
        Assert.Contains(groupDone, q.Queue);   // 같은 합본 그룹 컷이 대기 중 → 보존
        Assert.Contains(groupLive, q.Queue);
        Assert.True(File.Exists(media));       // 목록 정리만 — 내보낸 미디어 보존
    }

    [Fact]
    public void QueueVm_RetryFresh_TargetsFailedAndQcWarnedJobsOnly()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var warned = new BatchRenderJob { Prompt = Prompt, Seed = 10 };
        var clean = new BatchRenderJob { Prompt = Prompt, Seed = 20 };
        q.Enqueue(new[] { warned, clean });
        warned.Status = BatchJobStatus.Completed;
        warned.Metadata = MultiDurationPlanner.WithMeta(null, RenderQueueService.MetaQcDegraded, "flicker");
        clean.Status = BatchJobStatus.Completed;
        using var vm = new RenderQueueViewModel(q, TimeSpan.Zero);

        Assert.True(RenderQueueViewModel.IsQcWarned(warned));
        Assert.False(RenderQueueViewModel.CanRetryFresh(clean));
        Assert.False(vm.RetryFresh(clean.JobId));
        Assert.True(vm.RetryFresh(warned.JobId));
        Assert.Equal(BatchJobStatus.Queued, warned.Status);
        Assert.Equal(RenderQueueViewModel.FreshSeed(10), warned.Seed);
        Assert.NotEqual(10, warned.Seed);
        Assert.Null(MultiDurationPlanner.MetaOf(warned, RenderQueueService.MetaQcDegraded));
        Assert.Equal(1, vm.FinishedCount);
    }

    /// <summary>자율 발견 — 0% 대기 작업만 재정렬하면 진행률 해시가 같아 Version 불변 → ShouldRender 가 재정렬을 그리지 않던 결함.</summary>
    [Fact]
    public void QueueVm_Version_Bumps_OnReorderOfZeroPercentQueuedJobs()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var a = new BatchRenderJob { Prompt = "a" };
        var b = new BatchRenderJob { Prompt = "b" };
        q.Enqueue(new[] { a, b });
        using var vm = new RenderQueueViewModel(q, TimeSpan.Zero);
        var before = vm.Version;

        Assert.True(q.MoveJobToTop(b.JobId));

        Assert.Equal(b.JobId, vm.Jobs[0].JobId);
        Assert.True(vm.Version > before);
    }

    // ── 🖼️ 미디어 앨범 ──

    private string WriteClip(string name, byte[] bytes, object? meta = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        if (meta is not null) File.WriteAllText(GalleryMediaService.MetaPathFor(path), JsonSerializer.Serialize(meta));
        return path;
    }

    private static byte[] Bytes(int n, byte fill)
    {
        var b = new byte[n];
        Array.Fill(b, fill);
        return b;
    }

    [Fact]
    public void MetaKeys_MatchRenderPipelineConstants()
    {
        Assert.Equal(MultiDurationPlanner.MetaWanFamily, GalleryMediaService.MetaKeyWanFamily);
        Assert.Equal(RenderQueueService.MetaQcRawPrompt, GalleryMediaService.MetaKeyQcRawPrompt);
        Assert.Equal(RenderQueueService.MetaQcSelfHealed, GalleryMediaService.MetaKeyQcSelfHealed);
        Assert.Equal(RenderQueueService.MetaQcDegraded, GalleryMediaService.MetaKeyQcDegraded);
        Assert.Equal(RenderQueueService.MetaQcDiagnosis, GalleryMediaService.MetaKeyQcDiagnosis);
    }

    [Fact]
    public void Scan_SurfacesRefinedPrompt_Model_Qc_Learned_AndQueueDurationSec()
    {
        WriteClip("refined.mp4", Bytes(4096, 1), new
        {
            prompt = Prompt + ", steady camera",
            engine = "ComfyUI",
            durationSec = 5.0,
            plutoLearned = true,
            metadata = new Dictionary<string, string>
            {
                [GalleryMediaService.MetaKeyRawPrompt] = Prompt,
                [GalleryMediaService.MetaKeyWanFamily] = "Wan26",
                [GalleryMediaService.MetaKeyQcDegraded] = "flicker",
            },
        });
        WriteClip("healed.mp4", Bytes(4096, 2), new
        {
            prompt = Prompt,
            engine = "Cloud",
            metadata = new Dictionary<string, string> { [GalleryMediaService.MetaKeyQcSelfHealed] = "true", [GalleryMediaService.MetaKeyQcDiagnosis] = "dark" },
        });
        var g = new GalleryMediaService();
        var items = g.ScanGalleryItems(_dir);
        var r = items.Single(i => i.Title == "refined");
        var h = items.Single(i => i.Title == "healed");

        Assert.True(r.IsRefinedPrompt);
        Assert.Equal("Wan26", GalleryMediaService.ModelBadge(r));
        Assert.Equal(GalleryQcStatus.Degraded, r.QcStatus);
        Assert.Equal("⚠ QC 경고", GalleryMediaService.QcBadge(r.QcStatus));
        Assert.True(r.IsLearned);
        Assert.Equal(5.0, r.DurationSeconds);   // 자율 발견 — 렌더 큐 사이드카 durationSec 폴백

        Assert.False(h.IsRefinedPrompt);
        Assert.Equal("Cloud", GalleryMediaService.ModelBadge(h));
        Assert.Equal(GalleryQcStatus.SelfHealed, h.QcStatus);
        Assert.Equal("dark", h.QcDiagnosis);
    }

    [Fact]
    public void LearnFromItem_RecordsRecipe_MarksMeta_AndBlocksDuplicateLearning()
    {
        WriteClip("good.mp4", Bytes(4096, 3), new { prompt = Prompt, engine = "ComfyUI" });
        WriteClip("mock.mp4", Bytes(4096, 4), new { prompt = Prompt, engine = "Mock" });
        var mem = new PlutoVideoLearningMemoryService(Path.Combine(_dir, "_mem", PlutoVideoLearningMemoryService.FileName));
        var g = new GalleryMediaService();
        var vm = new GalleryViewModel(g);
        vm.Refresh(_dir);
        var good = g.Items.Single(i => i.Title == "good");
        var mock = g.Items.Single(i => i.Title == "mock");
        var before = mem.LearnedRuleCount;

        Assert.False(vm.LearnFromItem(mock, mem));
        Assert.True(vm.LearnFromItem(good, mem));
        Assert.True(good.IsLearned);
        Assert.True(mem.LearnedRuleCount > before);
        Assert.False(vm.LearnFromItem(good, mem));   // 재클릭 중복 학습 차단

        using var doc = JsonDocument.Parse(File.ReadAllText(GalleryMediaService.MetaPathFor(good.FilePath)));
        Assert.True(doc.RootElement.GetProperty("plutoLearned").GetBoolean());
        Assert.Equal(Prompt, doc.RootElement.GetProperty("prompt").GetString());   // 기존 필드 보존
        Assert.True(new GalleryMediaService().ScanGalleryItems(_dir).Single(i => i.Title == "good").IsLearned);
    }

    [Fact]
    public async Task Cleanup_DeletesDuplicatesBrokenAndOrphans_KeepsProtectedAndUnique()
    {
        var content = Bytes(8192, 7);
        var older = WriteClip("dup_old.mp4", content);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        var newer = WriteClip("dup_new.mp4", content, new { prompt = "x" });
        var fav = WriteClip("dup_fav.mp4", content, new { prompt = "x", isFavorite = true });
        var unique = WriteClip("unique.mp4", Bytes(8192, 9));
        var broken = WriteClip("broken.mp4", Array.Empty<byte>());
        var orphanMeta = Path.Combine(_dir, "gone.meta.json");
        File.WriteAllText(orphanMeta, "{}");
        var orphanPoster = Path.Combine(_dir, "gone2" + GalleryMediaService.PosterSuffix);
        File.WriteAllBytes(orphanPoster, new byte[10]);

        var g = new GalleryMediaService();
        var vm = new GalleryViewModel(g);
        vm.Refresh(_dir);
        var plan = await vm.PlanCleanupAsync(_dir);

        Assert.NotNull(plan);
        var (dups, brk, orph, _) = GalleryViewModel.Summarize(plan);
        Assert.Equal(2, dups);
        Assert.Equal(1, brk);
        Assert.Equal(2, orph);
        Assert.All(plan!.Where(c => c.Reason == GalleryCleanupReason.Duplicate), c => Assert.Equal(fav, c.KeptPath));
        Assert.True(File.Exists(older));   // 계획만으로는 아무것도 지우지 않음

        var r = await vm.ExecutePendingCleanupAsync();

        Assert.NotNull(r);
        Assert.Equal(5, r!.Deleted);
        Assert.Equal(0, r.Failed);
        Assert.True(File.Exists(fav));
        Assert.True(File.Exists(unique));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(newer));
        Assert.False(File.Exists(GalleryMediaService.MetaPathFor(newer)));
        Assert.False(File.Exists(broken));
        Assert.False(File.Exists(orphanMeta));
        Assert.False(File.Exists(orphanPoster));
        Assert.DoesNotContain(g.Items, i => i.FilePath == older || i.FilePath == newer);
        Assert.Null(vm.PendingCleanup);
    }

    /// <summary>자율 발견 — 정리 중복 클릭/동시 실행 차단(가드는 첫 await 이전에 동기 획득).</summary>
    [Fact]
    public async Task Cleanup_ConcurrentPlanRequest_IsRejected()
    {
        WriteClip("a.mp4", Bytes(4096, 1));
        var vm = new GalleryViewModel(new GalleryMediaService());
        vm.Refresh(_dir);

        var first = vm.PlanCleanupAsync(_dir);
        var second = await vm.PlanCleanupAsync(_dir);

        Assert.Null(second);
        Assert.NotNull(await first);
        Assert.False(vm.IsCleanupBusy);
        Assert.Null(await vm.ExecutePendingCleanupAsync());   // 후보 0개 → 실행 없음
    }

    /// <summary>자율 발견 — 삭제 시 숨김 포스터(.poster.jpg)가 고아로 남던 결함.</summary>
    [Fact]
    public void Delete_RemovesPosterSidecar()
    {
        var clip = WriteClip("p.mp4", Bytes(4096, 5), new { prompt = "x" });
        var poster = GalleryMediaService.PosterPathFor(clip);
        File.WriteAllBytes(poster, new byte[16]);
        var g = new GalleryMediaService();
        var vm = new GalleryViewModel(g);
        vm.Refresh(_dir);

        Assert.True(vm.Delete(g.Items.Single(i => i.Title == "p")));
        Assert.False(File.Exists(clip));
        Assert.False(File.Exists(GalleryMediaService.MetaPathFor(clip)));
        Assert.False(File.Exists(poster));
    }

    // ── UI 결선 · 불변식 ──

    [Fact]
    public void Pages_WireRetryCleanupLearn_AndReleaseVideoBeforeDelete()
    {
        var root = RepoRoot();
        var queue = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "RenderQueue.razor"));
        Assert.Contains("data-queue-retry-fresh=\"1\"", queue, StringComparison.Ordinal);
        Assert.Contains("data-queue-clear-finished=\"1\"", queue, StringComparison.Ordinal);
        Assert.Contains("Vm.RetryFresh(job.JobId)", queue, StringComparison.Ordinal);
        Assert.Contains("완료/실패 항목 정리", queue, StringComparison.Ordinal);

        var gal = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "MediaGallery.razor"));
        Assert.Contains("data-gallery-learn=\"1\"", gal, StringComparison.Ordinal);
        Assert.Contains("data-gallery-cleanup=\"1\"", gal, StringComparison.Ordinal);
        Assert.Contains("data-gallery-prompt=\"refined\"", gal, StringComparison.Ordinal);
        Assert.Contains("_releaseId != si.ItemId", gal, StringComparison.Ordinal);
        var confirm = gal.IndexOf("private async Task Confirm(MediaItem m)", StringComparison.Ordinal);
        Assert.True(confirm > 0);
        var unmount = gal.IndexOf("_modal = null;", confirm, StringComparison.Ordinal);
        var delete = gal.IndexOf("Library.Delete(m.Path);", confirm, StringComparison.Ordinal);
        Assert.True(unmount > 0 && unmount < delete);   // 재생 핸들 해제 → 삭제 순서

        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("GalleryMediaService.MetaKeyRawPrompt, rawForGallery", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Launcher.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
