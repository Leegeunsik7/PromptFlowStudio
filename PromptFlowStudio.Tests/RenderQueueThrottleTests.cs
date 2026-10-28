using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>렌더 큐 VM 스로틀 — 진행률 폭주 통지 합류 · 상태 전이 즉시 전파 · 트레일링 최종값 · Version 변경 감지 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class RenderQueueThrottleTests
{
    /// <summary>OnChange 를 수동 발화하는 최소 큐 대역.</summary>
    private sealed class FakeQueue : IRenderQueueService
    {
        public readonly System.Collections.ObjectModel.ObservableCollection<BatchRenderJob> Items = new();
        public bool Paused;
        public event Action? OnChange;
        public void Fire() => OnChange?.Invoke();
        public BatchRenderJob[] Snapshot() => Items.ToArray();
        public BatchRenderJob? ActiveJob { get { foreach (var j in Items) if (j.Status == BatchJobStatus.Processing) return j; return null; } }
        public bool IsPaused => Paused;
        public bool IsVramPaused => false;
        public System.Collections.ObjectModel.ObservableCollection<BatchRenderJob> Queue => Items;
        public int Enqueue(IEnumerable<BatchRenderJob> jobs) { var n = 0; foreach (var j in jobs) { Items.Add(j); n++; } return n; }
        public void PauseQueue() { Paused = true; Fire(); }
        public void ResumeQueue() { Paused = false; Fire(); }
        public bool CancelJob(Guid jobId) => false;
        public bool RetryJob(Guid jobId) => false;
        public int ClearCompleted() => 0;
        public Task ProcessQueueAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static (FakeQueue Q, BatchRenderJob Job) Running()
    {
        var q = new FakeQueue();
        var job = new BatchRenderJob { Prompt = "p", Status = BatchJobStatus.Processing };
        q.Items.Add(job);
        return (q, job);
    }

    [Fact]
    public void ProgressBurst_IsCoalesced_WithinThrottleWindow()
    {
        var (q, job) = Running();
        using var vm = new RenderQueueViewModel(q, TimeSpan.FromSeconds(5));
        var emits = 0;
        vm.Changed += () => Interlocked.Increment(ref emits);

        q.Fire();                                   // 첫 통지 — 간격 만족 → 즉시
        for (var p = 1; p <= 99; p++) { job.ProgressPercent = p; q.Fire(); }

        Assert.Equal(1, emits);
    }

    [Fact]
    public void StateTransition_IsPropagatedImmediately()
    {
        var (q, job) = Running();
        using var vm = new RenderQueueViewModel(q, TimeSpan.FromSeconds(5));
        var emits = 0;
        vm.Changed += () => Interlocked.Increment(ref emits);

        q.Fire();
        job.ProgressPercent = 50; q.Fire();         // 스로틀
        job.ProgressPercent = 100; job.Status = BatchJobStatus.Completed; q.Fire();   // 완료 — 즉시
        q.PauseQueue();                             // 일시정지 — 즉시

        Assert.Equal(3, emits);
        Assert.Equal(1, vm.CompletedCount);
        Assert.True(vm.IsPaused);
    }

    [Fact]
    public async Task Trailing_EmitsFinalProgress()
    {
        var (q, job) = Running();
        using var vm = new RenderQueueViewModel(q, TimeSpan.FromMilliseconds(50));
        var emits = 0;
        vm.Changed += () => Interlocked.Increment(ref emits);

        q.Fire();
        job.ProgressPercent = 42; q.Fire();
        for (var i = 0; i < 100 && Volatile.Read(ref emits) < 2; i++) await Task.Delay(20);

        Assert.Equal(2, emits);
        Assert.Equal(42, vm.ActiveJob!.ProgressPercent);
    }

    [Fact]
    public void Version_ChangesOnlyWhenDataChanges()
    {
        var (q, job) = Running();
        using var vm = new RenderQueueViewModel(q, TimeSpan.Zero);
        var v0 = vm.Version;

        q.Fire();
        Assert.Equal(v0, vm.Version);               // 동일 데이터 — 렌더 불필요
        job.ProgressPercent = 10; q.Fire();
        Assert.NotEqual(v0, vm.Version);
    }

    [Fact]
    public void Dispose_StopsPropagation()
    {
        var (q, job) = Running();
        var vm = new RenderQueueViewModel(q, TimeSpan.Zero);
        var emits = 0;
        vm.Changed += () => emits++;
        vm.Dispose();
        job.ProgressPercent = 5; q.Fire();
        Assert.Equal(0, emits);
    }
}
