using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Render;

/// <summary>렌더 실행 엔진 종류 표시 키 — 큐 화면 활성 작업 배지용.</summary>
public static class RenderEngineKind
{
    public const string ComfyUi = "ComfyUI";
    public const string Cloud = "Cloud";
    public const string Mock = "Mock";
}

/// <summary>렌더 실행 1회 결과. Ok=false 면 Message 에 원인. OutputPath 는 로컬 파일 경로 또는 원격 URL.</summary>
public sealed record RenderExecutionResult(bool Ok, string EngineKind, string? OutputPath, string Message);

/// <summary>
/// [PromptFlowStudio] 렌더 큐 → 실물 추론 엔진 어댑터.
///   ▸ <see cref="IsAvailable"/>=false(엔진 미설정·오프라인·무GPU)면 <see cref="RenderQueueService"/> 가 내장 시뮬레이션으로 폴백.
///   ▸ 진행률은 0~100 정수로 <c>progress</c> 에 보고. 취소는 <c>ct</c> 로 즉시 중단.
/// </summary>
public interface IRenderExecutionEngine
{
    /// <summary>실물 추론 가능 여부 — 작업 디스패치 직전마다 재평가.</summary>
    bool IsAvailable { get; }

    /// <summary>현재 결선된 엔진 종류(<see cref="RenderEngineKind"/>).</summary>
    string EngineKind { get; }

    Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct);
}

/// <summary>
/// [선택] Mock(시뮬레이션) 렌더 시 실물 샘플 산출물 생성기 — 큐 시뮬레이션 완료 직후 호출.
/// 생성된 로컬 경로(성공) 또는 null(미구성·ffmpeg 부재·실패 → 기존 빈 완료 유지).
/// </summary>
public interface IMockRenderOutputProvider
{
    Task<string?> RenderMockAsync(BatchRenderJob job, CancellationToken ct);
}
