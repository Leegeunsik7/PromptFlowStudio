using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Batch;

public enum BatchJobStatus { Pending, Queued, Processing, Completed, Failed, Cancelled }

/// <summary>대량 양산 렌더 작업 1건(프롬프트 1줄 = 작업 1건).</summary>
public sealed class BatchRenderJob
{
    public Guid JobId { get; init; } = Guid.NewGuid();
    public string Prompt { get; init; } = "";
    public long Seed { get; init; }
    public string Resolution { get; init; } = RenderCostEstimatorService.DefaultResolution;
    public float DurationSec { get; init; } = RenderCostEstimatorService.DefaultDurationSec;
    public BatchJobStatus Status { get; set; } = BatchJobStatus.Pending;
    /// <summary>렌더 진행률 0~100 — <see cref="PromptFlowStudio.Services.Render.RenderQueueService"/> 워커가 갱신.</summary>
    public int ProgressPercent { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// [PromptFlowStudio] 3번 워크플로우 【대량 양산 &amp; 비용】 배치 디스패처 — 다중 줄 프롬프트를 렌더 작업 목록으로 전개.
///   ▸ 줄바꿈(\n · \r\n) 분할 · 앞뒤 공백 제거 · 빈 줄 제외.
///   ▸ incrementSeed=true 면 작업마다 baseSeed+i, false 면 전 작업 동일 시드.
/// </summary>
public sealed class BatchMassProductionService
{
    public List<BatchRenderJob> GenerateBatchJobs(string? multilinePrompts, long baseSeed, bool incrementSeed, string? resolution, float duration)
    {
        var jobs = new List<BatchRenderJob>();
        if (string.IsNullOrWhiteSpace(multilinePrompts)) return jobs;

        var res = string.IsNullOrWhiteSpace(resolution) ? RenderCostEstimatorService.DefaultResolution : resolution.Trim();
        var dur = float.IsFinite(duration) && duration > 0f ? duration : RenderCostEstimatorService.DefaultDurationSec;

        foreach (var line in multilinePrompts.Split('\n'))
        {
            var prompt = line.Trim();
            if (prompt.Length == 0) continue;
            jobs.Add(new BatchRenderJob
            {
                Prompt = prompt,
                Seed = incrementSeed ? unchecked(baseSeed + jobs.Count) : baseSeed,
                Resolution = res,
                DurationSec = dur,
            });
        }
        return jobs;
    }

    /// <summary>비어 있지 않은 프롬프트 줄 수 — 입력 변경마다 호출되므로 할당 없이 스캔.</summary>
    public static int CountPrompts(string? multilinePrompts)
    {
        if (string.IsNullOrEmpty(multilinePrompts)) return 0;
        var span = multilinePrompts.AsSpan();
        var count = 0;
        while (true)
        {
            var nl = span.IndexOf('\n');
            var line = nl < 0 ? span : span[..nl];
            if (!line.IsWhiteSpace()) count++;
            if (nl < 0) return count;
            span = span[(nl + 1)..];
        }
    }
}
