using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 렌더링 큐 화면 뷰모델 — <see cref="IRenderQueueService"/> 스냅샷 기반 요약 통계 · 활성 작업 · 큐 제어.
///   ▸ 서비스 OnChange 마다 <see cref="Refresh"/> 로 카운트 재계산 후 <see cref="Changed"/> 전파(워커 스레드 — Blazor 는 InvokeAsync 마샬링).
/// </summary>
public sealed class RenderQueueViewModel : IDisposable
{
    private readonly IRenderQueueService _queue;

    public RenderQueueViewModel(IRenderQueueService queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _queue.OnChange += OnQueueChanged;
        Refresh();
    }

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
        var snap = _queue.Snapshot();
        int pending = 0, processing = 0, completed = 0, failed = 0;
        string? lastError = null;
        for (var i = 0; i < snap.Length; i++)
        {
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
    }

    public void Dispose() => _queue.OnChange -= OnQueueChanged;

    private void OnQueueChanged()
    {
        Refresh();
        try { Changed?.Invoke(); } catch (Exception) { }
    }
}
