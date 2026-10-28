using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 렌더링 큐 화면 뷰모델 — <see cref="IRenderQueueService"/> 스냅샷 기반 요약 통계 · 활성 작업 · 큐 제어.
///   ▸ 서비스 OnChange 마다 <see cref="Refresh"/> 로 카운트 재계산 후 <see cref="Changed"/> 전파(워커 스레드 — Blazor 는 InvokeAsync 마샬링).
///   ▸ 진행률만 바뀐 통지는 <see cref="ProgressThrottle"/> 간격으로 스로틀(마지막 값은 트레일링 1회 보장) · 상태 전이(완료/실패/일시정지·활성 작업 교체)는 즉시.
///   ▸ <see cref="Version"/> 은 표시 데이터가 실제로 바뀐 경우에만 증가 — 페이지 ShouldRender 판정용.
/// </summary>
public sealed class RenderQueueViewModel : IDisposable
{
    /// <summary>진행률 전용 통지의 최소 전파 간격.</summary>
    public static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(100);

    private readonly IRenderQueueService _queue;
    private readonly TimeSpan _throttle;
    private readonly object _sync = new();
    private readonly System.Threading.Timer _trailing;
    private int _structSig;
    private int _dataSig;
    private long _lastEmit;          // Stopwatch 타임스탬프
    private bool _trailingArmed;
    private bool _disposed;
    private long _version;

    public RenderQueueViewModel(IRenderQueueService queue) : this(queue, ProgressThrottle) { }

    /// <param name="throttle">진행률 통지 스로틀 간격(≤0 이면 스로틀 없음).</param>
    internal RenderQueueViewModel(IRenderQueueService queue, TimeSpan throttle)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _throttle = throttle;
        _trailing = new System.Threading.Timer(OnTrailing);
        _queue.OnChange += OnQueueChanged;
        Refresh();
    }

    /// <summary>표시 데이터 변경 버전(카운트·상태·진행률 중 하나라도 바뀌면 증가).</summary>
    public long Version => Interlocked.Read(ref _version);

    public event Action? Changed;

    public IReadOnlyList<BatchRenderJob> Jobs { get; private set; } = Array.Empty<BatchRenderJob>();
    public BatchRenderJob? ActiveJob { get; private set; }
    public bool IsPaused { get; private set; }
    /// <summary>VRAM 위험 수준 자동 일시정지 상태(배너 표출).</summary>
    public bool IsVramPaused { get; private set; }
    public int TotalJobsCount { get; private set; }
    public int PendingCount { get; private set; }
    public int ProcessingCount { get; private set; }
    public int CompletedCount { get; private set; }
    public int FailedCount { get; private set; }
    /// <summary>활성 작업 실행 엔진(ComfyUI · Cloud · Mock) — 대기 중이면 null.</summary>
    public string? ActiveEngineKind { get; private set; }
    /// <summary>가장 최근(큐 후순위) 실패 작업의 원인 — 없으면 null.</summary>
    public string? LastErrorMessage { get; private set; }

    public void TogglePause() { if (_queue.IsPaused) _queue.ResumeQueue(); else _queue.PauseQueue(); }
    public void ClearCompleted() => _queue.ClearCompleted();
    public void Cancel(Guid jobId) => _queue.CancelJob(jobId);
    public void Retry(Guid jobId) => _queue.RetryJob(jobId);

    public const string VramPauseNotice = "GPU 메모리가 위험 수준에 도달하여 렌더 큐를 일시 정지했습니다. 메모리가 확보되면 재개하십시오.";

    /// <summary>완료 작업 → 갤러리 딥링크(<c>gallery?focus=</c> 출력 경로) — 완료 전이거나 로컬 산출물이 없으면 null.</summary>
    public static string? GalleryLinkFor(BatchRenderJob job)
    {
        if (job is null || job.Status != BatchJobStatus.Completed) return null;
        var path = !string.IsNullOrWhiteSpace(job.OutputPath) ? job.OutputPath
                 : !string.IsNullOrWhiteSpace(job.ResultPath) && !job.ResultPath.Contains("://", StringComparison.Ordinal) ? job.ResultPath : null;
        return path is null ? null : "gallery?focus=" + Uri.EscapeDataString(path);
    }

    public void Refresh()
    {
        lock (_sync) RefreshCore(out _);
    }

    /// <param name="structural">진행률 외 상태(카운트·일시정지·활성 작업·오류)가 바뀌었는지.</param>
    private void RefreshCore(out bool structural)
    {
        var snap = _queue.Snapshot();
        int pending = 0, processing = 0, completed = 0, failed = 0;
        string? lastError = null;
        var progress = new HashCode();
        for (var i = 0; i < snap.Length; i++)
        {
            progress.Add(snap[i].ProgressPercent);
            switch (snap[i].Status)
            {
                case BatchJobStatus.Pending or BatchJobStatus.Queued: pending++; break;
                case BatchJobStatus.Processing: processing++; break;
                case BatchJobStatus.Completed: completed++; break;
                default: failed++; break;   // Failed · Cancelled
            }
            if (snap[i].Status == BatchJobStatus.Failed && !string.IsNullOrEmpty(snap[i].ErrorMessage)) lastError = snap[i].ErrorMessage;
        }
        Jobs = snap;
        ActiveJob = _queue.ActiveJob;
        ActiveEngineKind = ActiveJob?.EngineKind;
        LastErrorMessage = lastError;
        IsPaused = _queue.IsPaused;
        IsVramPaused = _queue.IsVramPaused;
        TotalJobsCount = snap.Length;
        PendingCount = pending;
        ProcessingCount = processing;
        CompletedCount = completed;
        FailedCount = failed;

        var sig = HashCode.Combine(snap.Length, pending, processing, completed, failed, IsPaused, IsVramPaused, HashCode.Combine(ActiveJob?.JobId, ActiveEngineKind, lastError));
        var data = HashCode.Combine(sig, progress.ToHashCode());
        structural = sig != _structSig;
        _structSig = sig;
        if (data != _dataSig) { _dataSig = data; Interlocked.Increment(ref _version); }
    }

    public void Dispose()
    {
        _queue.OnChange -= OnQueueChanged;
        lock (_sync) { _disposed = true; _trailingArmed = false; }
        _trailing.Dispose();
    }

    private void OnQueueChanged()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RefreshCore(out var structural);
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastEmit, now);
            if (!structural && _throttle > TimeSpan.Zero && elapsed < _throttle)
            {
                if (!_trailingArmed)
                {
                    _trailingArmed = true;
                    try { _trailing.Change(_throttle - elapsed, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                }
                return;
            }
            _lastEmit = now;
            _trailingArmed = false;
        }
        Emit();
    }

    /// <summary>스로틀 구간 마지막 진행률을 반영하는 트레일링 통지(타이머 콜백 — 예외 격리).</summary>
    private void OnTrailing(object? state)
    {
        lock (_sync)
        {
            if (_disposed || !_trailingArmed) return;
            _trailingArmed = false;
            RefreshCore(out _);
            _lastEmit = Stopwatch.GetTimestamp();
        }
        Emit();
    }

    private void Emit()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }
}
