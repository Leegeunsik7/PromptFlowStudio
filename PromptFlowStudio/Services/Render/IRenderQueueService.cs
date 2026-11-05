using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] 전역 렌더 큐 — 대량 양산 작업을 적재하고 백그라운드 워커가 순차 렌더(Queued → Processing → Completed).
///   ⚠ <see cref="Queue"/> 변경·작업 필드 갱신은 내부 잠금 하에서만 발생 — UI 는 <see cref="Snapshot"/> 으로 읽고 <see cref="OnChange"/> 구독.
/// </summary>
public interface IRenderQueueService : IDisposable
{
    ObservableCollection<BatchRenderJob> Queue { get; }
    bool IsPaused { get; }
    /// <summary>VRAM 위험 수준(≥95%) 감지로 자동 일시정지됨 — <see cref="ResumeQueue"/> 시 해제.</summary>
    bool IsVramPaused { get; }
    /// <summary>출력 드라이브 잔여 5GB 미만 감지로 자동 일시정지됨 — <see cref="ResumeQueue"/> 시 해제.</summary>
    bool IsDiskPaused => false;
    BatchRenderJob? ActiveJob { get; }

    /// <summary>큐/작업 상태 변경 알림 — 워커 스레드에서 발생할 수 있음.</summary>
    event Action? OnChange;

    /// <summary>작업 일괄 등록(Queued 로 전환) — 이미 등록된 JobId 는 건너뜀. 실제 적재 건수 반환.</summary>
    int Enqueue(IEnumerable<BatchRenderJob> jobs);

    /// <summary>
    /// 듀레이션 모드 단위 적재 — 멀티 샷(10s/15s)은 동일 그룹 키 하위 컷 2~3개로 전개해 단일 잠금 하 원자적 삽입(<see cref="MultiDurationPlanner.Expand"/>).
    /// 적재된 작업 반환(중복 등으로 일부라도 누락 시 빈 배열).
    /// </summary>
    IReadOnlyList<BatchRenderJob> EnqueueJob(BatchRenderJob job, IReadOnlyList<string?>? subPrompts = null, string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobs = MultiDurationPlanner.Expand(job, subPrompts, projectName);
        return Enqueue(jobs) == jobs.Length ? jobs : Array.Empty<BatchRenderJob>();
    }
    bool CancelJob(Guid jobId);

    /// <summary>
    /// 합본 그룹(<see cref="BatchRenderJob.StitchGroupId"/>) 일괄 취소 — 대기 컷 먼저 Cancelled 확정 후 진행 중 컷 취소(엔진 프로세스 킬 · ComfyUI <c>/interrupt</c>).
    /// 기본 구현은 <see cref="Snapshot"/> + <see cref="CancelJob"/> 조합. 취소된 작업 수 반환.
    /// </summary>
    int CancelJobGroup(string stitchGroupId)
    {
        if (string.IsNullOrEmpty(stitchGroupId)) return 0;
        var snap = Snapshot();
        var n = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < snap.Length; i++)
            {
                var j = snap[i];
                if (!string.Equals(j.StitchGroupId, stitchGroupId, StringComparison.Ordinal)) continue;
                if ((pass == 0) == (j.Status == BatchJobStatus.Processing)) continue;   // 1패스: 대기 컷 · 2패스: 진행 컷
                if (CancelJob(j.JobId)) n++;
            }
        return n;
    }
    bool RetryJob(Guid jobId);
    int ClearCompleted();

    /// <summary>⚡ 대기 작업 우선순위 — 바로 위 대기 작업과 자리 교환(대기/보류 상태만). 이동 시 true.</summary>
    bool MoveJobUp(Guid jobId) => false;
    /// <summary>⚡ 대기 작업 우선순위 — 바로 아래 대기 작업과 자리 교환.</summary>
    bool MoveJobDown(Guid jobId) => false;
    /// <summary>⚡ 최우선 실행 — 대기열 1순위(다음 디스패치 대상)로 즉시 끌어올림.</summary>
    bool MoveJobToTop(Guid jobId) => false;
    void PauseQueue();
    void ResumeQueue();

    /// <summary>잠금 하 복사본 — 렌더 스레드에서 안전하게 열거.</summary>
    BatchRenderJob[] Snapshot();

    /// <summary>Queued 작업이 소진될 때까지 순차 처리(일시정지 중엔 대기).</summary>
    Task ProcessQueueAsync(CancellationToken ct);
}
