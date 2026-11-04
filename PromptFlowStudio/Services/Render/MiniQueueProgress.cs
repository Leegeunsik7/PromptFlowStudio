using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Render;

/// <summary>작업실 미니 큐 진행 단계.</summary>
public enum MiniQueuePhase : byte { Idle, Rendering, Stitching, Completed, Failed }

/// <summary>
/// [PromptFlowStudio] 작업실 인라인 큐 진행률 — 10s/15s/앰비언트 적재 작업(JobId 집합)을 큐 스냅샷에서 집계.
///   ▸ 멀티 샷: 전 컷 Completed + 합본 메타(<see cref="MultiDurationPlanner.MetaStitched"/>/<c>…Virtual</c>) → Completed, 메타 대기 중 → Stitching.
///   ▸ 단일(앰비언트): Completed → 최종 산출물(ResultPath 우선 → OutputPath) 로컬 실존 경로.
///   ▸ Failed/Cancelled 컷이 하나라도 있으면 Failed. 스냅샷에 없는 작업(정리됨)은 무시, 전부 없으면 Idle.
/// </summary>
public readonly record struct MiniQueueProgress(MiniQueuePhase Phase, int TotalCuts, int CompletedCuts, int Percent, string? FinalPath)
{
    public static readonly MiniQueueProgress None = new(MiniQueuePhase.Idle, 0, 0, 0, null);

    /// <summary>스냅샷 집계 — UI 갱신 시점에만 호출(핫패스 아님 · LINQ 없음).</summary>
    public static MiniQueueProgress Compute(IReadOnlyList<BatchRenderJob> snapshot, IReadOnlyList<Guid> jobIds)
    {
        if (snapshot is null || jobIds is null || jobIds.Count == 0) return None;
        int total = 0, done = 0, active = 0;
        var failed = false;
        string? stitched = null;
        BatchRenderJob? last = null;
        for (var i = 0; i < jobIds.Count; i++)
        {
            BatchRenderJob? j = null;
            for (var k = 0; k < snapshot.Count; k++)
                if (snapshot[k].JobId == jobIds[i]) { j = snapshot[k]; break; }
            if (j is null) continue;
            total++;
            last = j;
            switch (j.Status)
            {
                case BatchJobStatus.Completed: done++; break;
                case BatchJobStatus.Failed or BatchJobStatus.Cancelled: failed = true; break;
                case BatchJobStatus.Processing: active += Math.Clamp(j.ProgressPercent, 0, 100); break;
            }
            stitched ??= MultiDurationPlanner.MetaOf(j, MultiDurationPlanner.MetaStitched) ?? MultiDurationPlanner.MetaOf(j, MultiDurationPlanner.MetaStitchedVirtual);
        }
        if (total == 0 || last is null) return None;
        var pct = Math.Clamp((done * 100 + active) / total, 0, 100);
        if (failed) return new(MiniQueuePhase.Failed, total, done, pct, null);
        if (done < total) return new(MiniQueuePhase.Rendering, total, done, pct, null);
        if (jobIds.Count > 1)
            return stitched is { Length: > 0 } ? new(MiniQueuePhase.Completed, total, done, 100, stitched) : new(MiniQueuePhase.Stitching, total, done, 100, null);
        return new(MiniQueuePhase.Completed, total, done, 100, ResolveFinal(last));
    }

    /// <summary>단일 작업 최종 산출물 — 엔진 후처리(앰비언트) 결과 ResultPath 우선, 없으면 OutputPath. 로컬 실존만.</summary>
    public static string? ResolveFinal(BatchRenderJob job)
    {
        var r = job.ResultPath;
        if (!string.IsNullOrWhiteSpace(r) && !r.Contains("://", StringComparison.Ordinal) && File.Exists(r)) return Path.GetFullPath(r);
        return FFmpegVideoStitchService.LocalOutputOf(job);
    }

    /// <summary>표시 라벨 — 예: <c>🎬 멀티 컷 렌더링 중... 2/3 컷 생성 완료 (66%)</c>.</summary>
    public string Label => Phase switch
    {
        MiniQueuePhase.Rendering when TotalCuts > 1 => "🎬 멀티 컷 렌더링 중... " + CompletedCuts.ToString(CultureInfo.InvariantCulture) + "/"
            + TotalCuts.ToString(CultureInfo.InvariantCulture) + " 컷 생성 완료 (" + Percent.ToString(CultureInfo.InvariantCulture) + "%)",
        MiniQueuePhase.Rendering => "🎬 렌더링 중... (" + Percent.ToString(CultureInfo.InvariantCulture) + "%)",
        MiniQueuePhase.Stitching => "🎞️ 최종 비디오 합본 중...",
        MiniQueuePhase.Completed => FinalPath is null ? "✅ 렌더 완료 (로컬 산출물 없음)" : "✅ 완료 — " + Path.GetFileName(FinalPath),
        MiniQueuePhase.Failed => "⚠ 렌더 실패/취소된 컷이 있습니다 (" + CompletedCuts.ToString(CultureInfo.InvariantCulture) + "/" + TotalCuts.ToString(CultureInfo.InvariantCulture) + ")",
        _ => "",
    };
}
