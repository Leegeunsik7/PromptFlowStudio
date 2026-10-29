using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>렌더 큐 워커 — 적재 초기 상태 · Queued→Completed 전이 · 취소 · 완료 정리 회귀 잠금(autoStart=false 결정론 구동).</summary>
public class RenderQueueWorkerTests
{
    private static RenderQueueService NewQueue() => new(TimeSpan.Zero, autoStart: false);

    private static List<BatchRenderJob> Jobs(int n)
    {
        var list = new List<BatchRenderJob>();
        for (var i = 0; i < n; i++) list.Add(new BatchRenderJob { Prompt = "p" + i, Seed = i });
        return list;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Enqueue_AddsJobsToQueue_InCorrectInitialStatus()
    {
        using var q = NewQueue();
        var jobs = Jobs(3);

        Assert.Equal(3, q.Enqueue(jobs));
        Assert.Equal(0, q.Enqueue(jobs));   // 중복 JobId 차단
        Assert.Equal(3, q.Queue.Count);
        Assert.All(q.Queue, j => { Assert.Equal(BatchJobStatus.Queued, j.Status); Assert.Equal(0, j.ProgressPercent); Assert.Null(j.CompletedAt); });
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ProcessQueue_TransitionsStatus_FromQueuedToCompleted()
    {
        using var q = NewQueue();
        q.Enqueue(Jobs(2));
        var sawProcessing = false;
        q.OnChange += () => { if (q.ActiveJob is { Status: BatchJobStatus.Processing }) sawProcessing = true; };

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.True(sawProcessing);
        Assert.Null(q.ActiveJob);
        Assert.All(q.Queue, j =>
        {
            Assert.Equal(BatchJobStatus.Completed, j.Status);
            Assert.Equal(100, j.ProgressPercent);
            Assert.NotNull(j.CompletedAt);
        });

        var vm = new RenderQueueViewModel(q);
        Assert.Equal(2, vm.TotalJobsCount);
        Assert.Equal(2, vm.CompletedCount);
        Assert.Equal(0, vm.PendingCount);
        vm.Dispose();
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task CancelJob_UpdatesStatusToFailedOrCancelled()
    {
        using var q = NewQueue();
        var jobs = Jobs(2);
        q.Enqueue(jobs);

        Assert.True(q.CancelJob(jobs[0].JobId));
        Assert.Equal(BatchJobStatus.Cancelled, jobs[0].Status);
        Assert.False(q.CancelJob(Guid.NewGuid()));

        await q.ProcessQueueAsync(CancellationToken.None);
        Assert.Equal(BatchJobStatus.Cancelled, jobs[0].Status);
        Assert.Equal(BatchJobStatus.Completed, jobs[1].Status);

        // 처리 중 취소 — 첫 진행률 틱에서 취소
        using var q2 = NewQueue();
        var live = Jobs(1);
        q2.Enqueue(live);
        q2.OnChange += () => { if (live[0].ProgressPercent == RenderQueueService.ProgressStep) q2.CancelJob(live[0].JobId); };
        await q2.ProcessQueueAsync(CancellationToken.None);
        Assert.Equal(BatchJobStatus.Cancelled, live[0].Status);

        Assert.True(q2.RetryJob(live[0].JobId));
        Assert.Equal(BatchJobStatus.Queued, live[0].Status);
        Assert.Equal(0, live[0].ProgressPercent);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ClearCompleted_RemovesOnlyCompletedJobs()
    {
        using var q = NewQueue();
        var jobs = Jobs(3);
        q.Enqueue(jobs);
        q.CancelJob(jobs[1].JobId);
        await q.ProcessQueueAsync(CancellationToken.None);

        var extra = Jobs(1);
        q.Enqueue(extra);

        Assert.Equal(2, q.ClearCompleted());
        Assert.Equal(2, q.Queue.Count);
        Assert.Contains(jobs[1], q.Queue);
        Assert.Contains(extra[0], q.Queue);
        Assert.DoesNotContain(q.Queue, j => j.Status == BatchJobStatus.Completed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessQueue_InvokesRealEngineAdapter_AndCompletesJob()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs_rq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var engine = new FakeEngine((job, progress) =>
            {
                progress.Report(40);
                progress.Report(100);
                var output = Path.Combine(dir, "clip_" + job.Seed + ".mp4");
                File.WriteAllBytes(output, new byte[] { 0 });
                return new RenderExecutionResult(true, RenderEngineKind.ComfyUi, output, "ok");
            });
            using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
            var jobs = Jobs(2);
            q.Enqueue(jobs);
            var vm = new RenderQueueViewModel(q);
            var sawKind = false;
            q.OnChange += () => { if (vm.ActiveEngineKind == RenderEngineKind.ComfyUi) sawKind = true; };

            await q.ProcessQueueAsync(CancellationToken.None);

            Assert.Equal(2, engine.Calls);
            Assert.True(sawKind);
            Assert.All(jobs, j =>
            {
                Assert.Equal(BatchJobStatus.Completed, j.Status);
                Assert.Equal(100, j.ProgressPercent);
                Assert.Equal(RenderEngineKind.ComfyUi, j.EngineKind);
                Assert.Null(j.ErrorMessage);
                Assert.True(File.Exists(j.ResultPath));
                Assert.True(File.Exists(Path.ChangeExtension(j.ResultPath!, null) + ".meta.json"));
            });
            vm.Dispose();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ProcessQueue_HandlesEngineFailure_MarksJobAsFailed()
    {
        var engine = new FakeEngine((job, _) => job.Seed == 0
            ? throw new InvalidOperationException("GPU OOM")
            : new RenderExecutionResult(false, RenderEngineKind.Cloud, null, "quota exceeded"));
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var jobs = Jobs(2);
        q.Enqueue(jobs);

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Failed, jobs[0].Status);
        Assert.Equal("GPU OOM", jobs[0].ErrorMessage);
        Assert.Equal(BatchJobStatus.Failed, jobs[1].Status);
        Assert.Equal("quota exceeded", jobs[1].ErrorMessage);
        Assert.Null(q.ActiveJob);

        var vm = new RenderQueueViewModel(q);
        Assert.Equal(2, vm.FailedCount);
        Assert.Equal("quota exceeded", vm.LastErrorMessage);
        vm.Dispose();

        // 재시도 시 실패 원인 초기화 · 엔진 미가용이면 시뮬레이션 폴백
        engine.Available = false;
        Assert.True(q.RetryJob(jobs[0].JobId));
        Assert.Null(jobs[0].ErrorMessage);
        await q.ProcessQueueAsync(CancellationToken.None);
        Assert.Equal(BatchJobStatus.Completed, jobs[0].Status);
        Assert.Equal(RenderEngineKind.Mock, jobs[0].EngineKind);
        Assert.Equal(2, engine.Calls);
    }

    private sealed class FakeEngine(Func<BatchRenderJob, IProgress<int>, RenderExecutionResult> run) : IRenderExecutionEngine
    {
        public int Calls;
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public string EngineKind => RenderEngineKind.ComfyUi;

        public Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(run(job, progress));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RenderExecutionEngine_PassesReferenceImagePath_ToInferenceRequest()
    {
        var img = Path.Combine(Path.GetTempPath(), "pfs_i2v_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(img, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            var i2v = VideoEngineRenderExecutionEngine.BuildRequest(new BatchRenderJob { Prompt = "p", Resolution = "1080x1920", Seed = 7, ReferenceImagePath = img });
            Assert.Equal(Path.GetFullPath(img), i2v.ImagePath);   // I2V 분기(엔진은 ImagePath 존재 시 이미지→영상)
            Assert.Equal("1080*1920", i2v.Size);
            Assert.Equal(7, i2v.Seed);

            var missing = VideoEngineRenderExecutionEngine.BuildRequest(new BatchRenderJob { Prompt = "p", ReferenceImagePath = img + ".none" });
            Assert.Null(missing.ImagePath);   // 파일 누락 → T2V 폴백
            Assert.Null(VideoEngineRenderExecutionEngine.BuildRequest(new BatchRenderJob { Prompt = "p" }).ImagePath);
        }
        finally { File.Delete(img); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RenderQueue_AutoPauses_WhenVramExceedsThreshold()
    {
        var vram = 0.97;
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine: null, vramProbe: () => Volatile.Read(ref vram));
        using var vm = new RenderQueueViewModel(q);
        var jobs = Jobs(2);
        q.Enqueue(jobs);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker = q.ProcessQueueAsync(cts.Token);
        for (var i = 0; i < 200 && !q.IsVramPaused; i++) await Task.Delay(10);

        // 디스패치 차단 — 위험 수준에서 어떤 작업도 Processing 으로 꺼내지 않음(크래시 없는 안전 정지)
        Assert.True(q.IsPaused);
        Assert.True(q.IsVramPaused);
        Assert.True(vm.IsVramPaused);
        Assert.False(worker.IsCompleted);
        Assert.All(jobs, j => Assert.Equal(BatchJobStatus.Queued, j.Status));
        Assert.Contains("GPU 메모리가 위험 수준", RenderQueueViewModel.VramPauseNotice);

        // 메모리 확보 후 수동 재개 → 경고 해제 · 전량 완료
        Volatile.Write(ref vram, 0.50);
        q.ResumeQueue();
        Assert.False(q.IsVramPaused);
        await worker.WaitAsync(cts.Token);
        Assert.All(jobs, j => Assert.Equal(BatchJobStatus.Completed, j.Status));
        Assert.False(vm.IsVramPaused);

        // 임계 미만 · GPU 미탐지(null) · 프로브 예외 → 가드 통과
        using var q2 = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine: null, vramProbe: () => throw new InvalidOperationException("nvidia-smi"));
        var more = Jobs(1);
        q2.Enqueue(more);
        await q2.ProcessQueueAsync(cts.Token);
        Assert.Equal(BatchJobStatus.Completed, more[0].Status);
        Assert.False(q2.IsVramPaused);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task RenderExecution_CancelsUnderlyingProcess_OnCancellationToken()
    {
        var fake = new FakeVideoEngine(null, hang: true);
        var interrupted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(fake), null, (kind, _) => { interrupted.TrySetResult(kind); return Task.CompletedTask; });
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var job = new BatchRenderJob { Prompt = "long render" };
        q.Enqueue(new[] { job });

        var worker = q.ProcessQueueAsync(CancellationToken.None);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BatchJobStatus.Processing, job.Status);

        Assert.True(q.CancelJob(job.JobId));
        await worker.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fake.ObservedCancellation);                                        // 엔진 ct 전파
        Assert.Equal(RenderEngineKind.Cloud, await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5)));   // 엔진 중단 신호
        Assert.Equal(BatchJobStatus.Cancelled, job.Status);
        Assert.Null(q.ActiveJob);
    }

    [Fact]
    public async Task ProcessRunner_KillsProcessTree_OnCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await PromptFlowStudio.Services.Engines.ProcessRunner.RunAsync("cmd.exe", new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, ct: cts.Token);
        Assert.Equal(-2, r.ExitCode);                       // 취소됨
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); // 30초 핑 트리 즉시 종료
    }
}
