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
}
