using System;

namespace PromptFlowStudio.Services.Agents;

/// <summary>스웜 에이전트 역할 — 연출/사운드/에디터/품질검수.</summary>
public enum AgentRole { Director, Audio, Editor, Qc }

/// <summary>피드백 심각도 — 정보/경고/결함.</summary>
public enum FeedbackSeverity { Info, Warn, Defect }

/// <summary>
/// [PromptFlow Studio v8.0.0] 에이전트 간 교환 메시지(불변) — 역할·심각도·범주·대상 클립.
/// </summary>
public sealed record AgentFeedback(
    AgentRole Role, FeedbackSeverity Severity, string Category, string Message,
    string? TargetClipId = null, DateTimeOffset At = default)
{
    /// <summary>지금 시각으로 스탬프된 사본(발행 시 사용).</summary>
    public AgentFeedback Stamped() => this with { At = DateTimeOffset.Now };
}
