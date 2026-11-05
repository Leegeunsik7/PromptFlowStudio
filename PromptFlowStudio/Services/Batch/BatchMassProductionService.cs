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
    /// <summary>렌더 해상도("1280x720") — CUDA OOM 자동 다운스케일 재시도 시 워커가 하향 갱신.</summary>
    public string Resolution { get; set; } = RenderCostEstimatorService.DefaultResolution;
    public float DurationSec { get; init; } = RenderCostEstimatorService.DefaultDurationSec;
    public BatchJobStatus Status { get; set; } = BatchJobStatus.Pending;
    /// <summary>렌더 진행률 0~100 — <see cref="PromptFlowStudio.Services.Render.RenderQueueService"/> 워커가 갱신.</summary>
    public int ProgressPercent { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>렌더 출력 미디어 경로(선택) — 지정 시 완료 시점에 옆에 <c>.meta.json</c> 기록(갤러리 복원용).</summary>
    public string? OutputPath { get; init; }
    /// <summary>실행 엔진 종류(ComfyUI · Cloud · Mock) — 디스패치 시 기록.</summary>
    public string? EngineKind { get; set; }
    /// <summary>실물 엔진 산출물(로컬 경로 또는 원격 URL) — 시뮬레이션은 null.</summary>
    public string? ResultPath { get; set; }
    /// <summary>실패 원인 — Failed 전이 시 기록, 재적재 시 초기화.</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>자동 재시도 횟수 — CUDA OOM 다운스케일 재시도는 0 일 때 1회만.</summary>
    public int RetryCount { get; set; }
    /// <summary>사용자 [취소] 요청 플래그(직렬화 제외) — 클라우드 저널을 UserCanceled 로 전환해 재시작 시 좀비 다운로드 차단.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UserCancelRequested { get; set; }

    // ── 시퀀스 메타(선택) — 스토리보드/숏폼 전송분만 기록, 단건 작업은 null ──
    /// <summary>시퀀스 내 컷 순서(1부터).</summary>
    public int? CutIndex { get; init; }
    /// <summary>시퀀스 타임라인 시작 시각(초).</summary>
    public float? StartSec { get; init; }
    /// <summary>BGM BPM(숏폼 프리셋 140/80/120/100).</summary>
    public int? BgmBpm { get; init; }
    /// <summary>컷 종료 지점 누적 박 번호(0 기준).</summary>
    public int? TransitionBeatIndex { get; init; }
    /// <summary>다음 컷 진입 전환 효과.</summary>
    public string? TransitionFx { get; init; }
    /// <summary>피사체 일관성 앵커(1번 컷 피사체 키워드) — 후속 컷 공통.</summary>
    public string? SubjectAnchor { get; init; }
    /// <summary>피사체 일관성 참조 이미지 경로(1번 컷/프로젝트 앵커) — 후속 컷 공통. 멀티 샷 체이닝은 이전 컷 마지막 프레임을 워커가 주입.</summary>
    public string? ReferenceImagePath { get; set; }

    // ── 멀티 듀레이션(5s/10s/15s/15s-Ambient) 메타 ──
    /// <summary>듀레이션 모드 — 멀티 샷(10s/15s)은 큐가 5초 하위 컷으로 분할·체이닝·합본, Ambient 는 단일 5초 + 슬로우 루프 후처리.</summary>
    public VideoDurationMode DurationMode { get; init; } = VideoDurationMode.Sec5_Single;
    /// <summary>모드 최종 산출 길이(초) — 5 · 10 · 15 · 15.</summary>
    public int TargetDurationSeconds => MultiDurationPlanner.TargetSeconds(DurationMode);
    /// <summary>15초 앰비언트 슬로우 루프 후처리 대상.</summary>
    public bool IsAmbientLoop => DurationMode == VideoDurationMode.Sec15_AmbientSlow;
    /// <summary>멀티 샷 체이닝 그룹 키(<see cref="FFmpegVideoStitchService.MetaGroup"/> 와 동일 값) — 단일 작업은 null.</summary>
    public string? StitchGroupId { get; init; }
    /// <summary>후처리 메타(커머셜 로고 세이프존·CTA·디스클레이머 · 라이브 루프 등) — 갤러리 <c>.meta.json</c> 에 동반 기록. 후처리 완료 시 엔진이 갱신.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }
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
