using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>어떤 백엔드가 작업을 처리하는지 — HUD/로그 표시용.</summary>
public enum WanBackendKind { ComfyUi, DirectMlInProcess }

/// <summary>백엔드에 제출하는 작업 요청 — 기존 v10.1.0 <see cref="WanJobSpec"/> 재사용(신규 카탈로그 금지).</summary>
public sealed record WanBackendJobRequest(string JobId, WanJobSpec Spec, string Prompt, string? ImageRefUrl = null);

/// <summary>진행 단계 — ComfyUI 의 queued/executing/executed 상태와 자연 대응.</summary>
public enum WanBackendStage { Queued, Loading, Sampling, Decoding, Completed, Failed, Cancelled }

/// <summary>진행률 1건(스트리밍 단위) — 타일/프레임/노드 단위 어느 쪽이든 이 레코드로 통일.</summary>
public sealed record WanBackendProgress(string JobId, WanBackendStage Stage, int PercentComplete, string Message, string? PreviewFrameUrl = null);

/// <summary>제출 즉시 응답(수락 여부) — 실제 결과는 <see cref="IWanBackendConnector.StreamProgressAsync"/> 로 스트리밍.</summary>
public sealed record WanBackendSubmitResult(bool Accepted, string JobId, WanBackendKind Backend, string Message);

/// <summary>
/// v10.5.0-F2 Wan 백엔드 커넥터 추상화 — <see cref="ComfyUiWanConnector"/>(원격 REST/WebSocket)와
/// <see cref="DirectMlWanConnector"/>(로컬 인프로세스 폴백)를 동일 계약으로 다뤄 <see cref="WanBackendFactory"/>가
/// 가용성에 따라 투명하게 교체할 수 있게 한다.
/// </summary>
public interface IWanBackendConnector
{
    WanBackendKind Kind { get; }

    /// <summary>이 백엔드가 지금 응답 가능한지(짧은 타임아웃의 헬스체크) — <see cref="WanBackendFactory"/>가 폴백 판단에 사용.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>작업 제출 — 수락 여부만 즉시 반환(결과는 <see cref="StreamProgressAsync"/>로 별도 스트리밍).</summary>
    Task<WanBackendSubmitResult> SubmitJobAsync(WanBackendJobRequest request, CancellationToken ct = default);

    /// <summary>제출된 작업의 진행률을 완료/실패/취소까지 순차 스트리밍.</summary>
    IAsyncEnumerable<WanBackendProgress> StreamProgressAsync(string jobId, CancellationToken ct = default);

    /// <summary>진행 중인 작업 취소 요청(Graceful — 백엔드 부재/이미 종료된 작업도 예외 없이 반환).</summary>
    Task CancelJobAsync(string jobId, CancellationToken ct = default);
}
