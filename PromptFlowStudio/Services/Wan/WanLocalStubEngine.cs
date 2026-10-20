using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlow Studio v5.6 · Task3] 온디바이스 로컬 Wan 3.0 스텁(Mock) 렌더러 — 순수 C# BCL(외부 SDK 0).
///   ▸ 외부 Wan GPU 백엔드가 미구성/단선된 개발·모의 환경에서 <see cref="WanRenderClient"/> 가 폴백으로 사용.
///   ▸ 모의 렌더 지연 + 진행률(%) 보고 + 로컬 시뮬 프레임(<see cref="WanSampleFactory"/> SVG data URL) 반환 →
///     백엔드 없이도 전체 DAG 루프·프로그레스 HUD·갤러리 자산화를 100% 테스트 가능.
///   ⚠ CancellationToken 이식(취소 즉시 중단) · 예외 없음(결정적 반환).
/// </summary>
public sealed class WanLocalStubEngine
{
    /// <summary>모의 렌더 총 지연(ms) — HUD 에 Running 상태가 보이도록.</summary>
    public int RenderDelayMs { get; set; } = 1200;
    /// <summary>모의 병합 지연(ms).</summary>
    public int ConcatDelayMs { get; set; } = 1500;

    /// <summary>모의 렌더 — 단계별 진행률 보고 후 샘플 프레임(video/lastframe)을 완료 상태로 반환.</summary>
    public async Task<WanDispatchResult> SubmitAsync(WanRenderRequest req, IProgress<int>? progress, CancellationToken ct)
    {
        var jobId = string.IsNullOrWhiteSpace(req?.JobId) ? Guid.NewGuid().ToString("N")[..12] : req!.JobId;
        const int steps = 5;
        for (var i = 1; i <= steps; i++)
        {
            try { await Task.Delay(Math.Max(RenderDelayMs / steps, 1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new(false, false, jobId, req?.MotionIntensity ?? 0, "🧪 모의 렌더 취소", null, "canceled"); }
            progress?.Report(i * 100 / steps);
        }

        var i2v = !string.IsNullOrEmpty(req?.ImageUrl);
        var tag = jobId.Length >= 6 ? jobId[..6] : jobId;
        var frame = WanSampleFactory.FrameDataUrl(
            $"{(i2v ? "I2V" : "T2V")} · {req?.Width}×{req?.Height}",
            i2v ? "#ec4899" : "#7c5cff",
            "MOCK RENDER · job " + tag);
        // video_url 도 샘플 이미지로 대체(프리뷰가 이미지로 렌더) · lastframe 은 체인용
        return new(true, false, jobId, req?.MotionIntensity ?? 0, "🧪 로컬 스텁 모의 렌더 완료", frame, "completed", frame);
    }

    /// <summary>모의 병합 — 지연 후 롱폼 샘플 프레임을 완료 상태로 반환.</summary>
    public async Task<WanDispatchResult> ConcatAsync(IReadOnlyList<string> videoUrls, CancellationToken ct)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var n = videoUrls?.Count ?? 0;
        try { await Task.Delay(Math.Max(ConcatDelayMs, 1), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(false, false, jobId, 0, "🧪 모의 병합 취소", null, "canceled"); }

        var merged = WanSampleFactory.FrameDataUrl($"롱폼 병합 · {n}컷", "#f5c451", "MOCK CONCAT · " + jobId[..6]);
        return new(true, false, jobId, 0, $"🧪 로컬 스텁 병합 완료({n}컷)", merged, "completed", merged);
    }
}
