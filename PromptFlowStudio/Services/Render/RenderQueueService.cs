using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Gallery;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] 렌더 큐 + 렌더 파이프라인 워커.
///   ▸ <see cref="IRenderExecutionEngine"/> 가 가용하면 실물 추론(ComfyUI/클라우드) 호출, 미설정·오프라인이면 내장 시뮬레이션 폴백.
///   ▸ Enqueue/Retry/Resume 시 워커가 없으면 1개만 기동(<c>_workerRunning</c> 잠금 판정 — 소진 직전 적재분 유실 방지).
///   ▸ 프레임 틱마다 진행률 +<see cref="ProgressStep"/>% — 완료 시 Completed + CompletedAt.
///   ▸ 처리 중 작업 취소는 작업별 링크 CTS 로 즉시 중단 → Cancelled. 서비스 Dispose 시 처리 중 작업은 Queued 로 복귀.
///   ▸ OnChange 구독자 예외는 격리.
/// </summary>
public sealed class RenderQueueService : IRenderQueueService
{
    public const int ProgressStep = 5;

    private readonly object _gate = new();
    private readonly TimeSpan _frameDelay;
    private readonly bool _autoStart;
    private readonly IRenderExecutionEngine? _engine;
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _resume = NewResumeSignal();
    private CancellationTokenSource? _activeCts;
    private bool _paused;
    private bool _workerRunning;
    private bool _disposed;

    public RenderQueueService() : this(TimeSpan.FromMilliseconds(120), autoStart: true) { }

    public RenderQueueService(IRenderExecutionEngine engine) : this(TimeSpan.FromMilliseconds(120), autoStart: true, engine) { }

    public RenderQueueService(TimeSpan frameDelay, bool autoStart, IRenderExecutionEngine? engine = null)
    {
        _frameDelay = frameDelay < TimeSpan.Zero ? TimeSpan.Zero : frameDelay;
        _autoStart = autoStart;
        _engine = engine;
        _resume.TrySetResult();
    }

    public ObservableCollection<BatchRenderJob> Queue { get; } = new();
    public event Action? OnChange;

    public bool IsPaused { get { lock (_gate) return _paused; } }

    private BatchRenderJob? _activeJob;
    public BatchRenderJob? ActiveJob { get { lock (_gate) return _activeJob; } }

    public int Enqueue(IEnumerable<BatchRenderJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var added = 0;
        lock (_gate)
        {
            foreach (var job in jobs)
            {
                if (job is null || IndexOf(job.JobId) >= 0) continue;   // alreadyRegistered
                job.Status = BatchJobStatus.Queued;
                job.ProgressPercent = 0;
                job.CompletedAt = null;
                ResetRunInfo(job);
                Queue.Add(job);
                added++;
            }
        }
        if (added > 0) { Raise(); EnsureWorker(); }
        return added;
    }

    public bool CancelJob(Guid jobId)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0) return false;
            var job = Queue[i];
            if (job.Status == BatchJobStatus.Processing && ReferenceEquals(job, _activeJob))
            {
                cts = _activeCts;
                if (cts is null || cts.IsCancellationRequested) return false;   // 이미 취소 진행 중 — 중복 통지 차단
            }
            else if (job.Status is BatchJobStatus.Queued or BatchJobStatus.Pending) job.Status = BatchJobStatus.Cancelled;
            else return false;
        }
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        Raise();
        return true;
    }

    public bool RetryJob(Guid jobId)
    {
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0) return false;
            var job = Queue[i];
            if (job.Status is not (BatchJobStatus.Failed or BatchJobStatus.Cancelled)) return false;
            job.Status = BatchJobStatus.Queued;
            job.ProgressPercent = 0;
            job.CompletedAt = null;
            ResetRunInfo(job);
        }
        Raise();
        EnsureWorker();
        return true;
    }

    public int ClearCompleted()
    {
        var removed = 0;
        lock (_gate)
        {
            for (var i = Queue.Count - 1; i >= 0; i--)
            {
                if (Queue[i].Status != BatchJobStatus.Completed) continue;
                Queue.RemoveAt(i);
                removed++;
            }
        }
        if (removed > 0) Raise();
        return removed;
    }

    public void PauseQueue()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _resume = NewResumeSignal();
        }
        Raise();
    }

    public void ResumeQueue()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            _resume.TrySetResult();
        }
        Raise();
        EnsureWorker();
    }

    public BatchRenderJob[] Snapshot()
    {
        lock (_gate)
        {
            var arr = new BatchRenderJob[Queue.Count];
            Queue.CopyTo(arr, 0);
            return arr;
        }
    }

    public async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct).ConfigureAwait(false);

            BatchRenderJob? job = null;
            CancellationTokenSource jobCts;
            lock (_gate)
            {
                for (var i = 0; i < Queue.Count; i++)
                {
                    if (Queue[i].Status == BatchJobStatus.Queued) { job = Queue[i]; break; }
                }
                if (job is null) { _workerRunning = false; return; }
                job.Status = BatchJobStatus.Processing;
                job.ProgressPercent = 0;
                ResetRunInfo(job);
                jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _activeJob = job;
                _activeCts = jobCts;
            }
            Raise();

            var startedAt = DateTimeOffset.Now;
            try
            {
                var engine = _engine;
                if (engine is { IsAvailable: true }) await ExecuteOnEngineAsync(engine, job, jobCts.Token).ConfigureAwait(false);
                else await SimulateAsync(job, jobCts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    job.ProgressPercent = 100;
                    job.Status = BatchJobStatus.Completed;
                    job.CompletedAt = DateTimeOffset.Now;
                }
                WriteRenderMeta(job, startedAt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate) { job.Status = BatchJobStatus.Queued; job.ProgressPercent = 0; }   // 종료 — 재기동 시 재처리
            }
            catch (OperationCanceledException)
            {
                lock (_gate) job.Status = BatchJobStatus.Cancelled;
            }
            catch (Exception ex)
            {
                lock (_gate) { job.Status = BatchJobStatus.Failed; job.ErrorMessage = ex.Message; }
                Program.Log("RenderQueue.Execute", ex);
            }
            finally
            {
                lock (_gate) { _activeJob = null; _activeCts = null; }
                jobCts.Dispose();
                Raise();
            }
        }
    }

    /// <summary>내장 시뮬레이션 — 프레임 틱마다 +<see cref="ProgressStep"/>%.</summary>
    private async Task SimulateAsync(BatchRenderJob job, CancellationToken ct)
    {
        lock (_gate) job.EngineKind = RenderEngineKind.Mock;
        for (var p = ProgressStep; p <= 100; p += ProgressStep)
        {
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            await Task.Delay(_frameDelay, ct).ConfigureAwait(false);
            lock (_gate) job.ProgressPercent = p;
            Raise();
        }
    }

    /// <summary>실물 엔진 실행 — 실패 결과·로컬 산출물 부재는 예외로 승격(Failed 격리).</summary>
    private async Task ExecuteOnEngineAsync(IRenderExecutionEngine engine, BatchRenderJob job, CancellationToken ct)
    {
        await WaitIfPausedAsync(ct).ConfigureAwait(false);
        lock (_gate) job.EngineKind = engine.EngineKind;
        Raise();

        var result = await engine.ExecuteAsync(job, new JobProgress(this, job), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result is null || !result.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result?.Message) ? "렌더 엔진 실행 실패" : result.Message);

        var output = result.OutputPath;
        if (IsLocalPath(output) && !File.Exists(output))
            throw new FileNotFoundException("렌더 산출 파일 없음", output);
        lock (_gate) { job.EngineKind = result.EngineKind; job.ResultPath = output; }
    }

    private static bool IsLocalPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && !path.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(path);

    private static void ResetRunInfo(BatchRenderJob job)
    {
        job.EngineKind = null;
        job.ResultPath = null;
        job.ErrorMessage = null;
    }

    /// <summary>엔진 진행률 → 작업 진행률(동기 반영 · 0~99 클램프, 100 은 완료 전이에서만).</summary>
    private sealed class JobProgress(RenderQueueService owner, BatchRenderJob job) : IProgress<int>
    {
        public void Report(int value)
        {
            var p = value < 0 ? 0 : value > 99 ? 99 : value;
            lock (owner._gate)
            {
                if (job.Status != BatchJobStatus.Processing || p <= job.ProgressPercent) return;
                job.ProgressPercent = p;
            }
            owner.Raise();
        }
    }

    /// <summary>
    /// 완료 작업의 출력 미디어 옆에 <c>{이름}.meta.json</c> 기록 — <see cref="GalleryMediaService"/> 스키마(camelCase).
    /// 기록 실패는 격리(작업 상태는 Completed 유지).
    /// </summary>
    internal static void WriteRenderMeta(BatchRenderJob job, DateTimeOffset startedAt)
    {
        var mediaPath = !string.IsNullOrWhiteSpace(job.OutputPath) ? job.OutputPath
                      : IsLocalPath(job.ResultPath) ? job.ResultPath : null;
        if (mediaPath is null) return;
        try
        {
            var created = job.CompletedAt ?? DateTimeOffset.Now;
            var json = JsonSerializer.Serialize(new
            {
                title = Path.GetFileNameWithoutExtension(mediaPath),
                prompt = job.Prompt,
                seed = job.Seed,
                resolution = job.Resolution,
                createdAt = created,
                durationSec = job.DurationSec,
                renderDurationSec = (float)Math.Max(0, (created - startedAt).TotalSeconds),
            });
            File.WriteAllText(GalleryMediaService.MetaPathFor(mediaPath), json);
        }
        catch (Exception ex) { Program.Log("RenderQueue.Meta", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _shutdown.Dispose();
        OnChange = null;
    }

    private void EnsureWorker()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!_autoStart || _paused || _disposed || _workerRunning) return;
            _workerRunning = true;
            token = _shutdown.Token;
        }
        _ = Task.Run(async () =>
        {
            try { await ProcessQueueAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally { lock (_gate) _workerRunning = false; }
        });
    }

    private Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            if (!_paused) return Task.CompletedTask;
            wait = _resume.Task;
        }
        return wait.WaitAsync(ct);
    }

    private int IndexOf(Guid jobId)
    {
        for (var i = 0; i < Queue.Count; i++)
            if (Queue[i].JobId == jobId) return i;
        return -1;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception) { }
    }

    private static TaskCompletionSource NewResumeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
