using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>릴스 발행 경로 — Graph API(공개 URL) · 로컬 업로드 브릿지 · 웹훅(Zapier/Make) · 모의.</summary>
public enum ReelsRoute { None, GraphApi, LocalBridge, Webhook, Mock }

/// <summary>릴스 발행 결과(예외 0). IsMock=true 면 자격/브릿지 미구성 오프라인 모의 발행.</summary>
public sealed record ReelsPublishOutcome(bool Ok, bool IsMock, ReelsRoute Route, string? MediaId, string Message);

/// <summary>
/// Instagram Reels 원클릭 발행 계약 — 기존 <see cref="SocialMedia.InstagramReelsService"/>(컨테이너 → 폴링 → media_publish) 위임 +
/// 공개 URL 이 없는 로컬 환경용 웹훅 브릿지 폴백.
/// </summary>
public interface IInstagramReelsPublisher
{
    /// <summary>Graph 자격 또는 웹훅 브릿지 중 하나라도 구성됐는지 — false 면 모의 발행.</summary>
    bool Enabled { get; }

    /// <param name="publicVideoUrl">공개 HTTPS URL(있으면 Graph API 직결).</param>
    /// <param name="publicConsent">공개 게시 명시 동의(로컬 브릿지 경로 필수).</param>
    Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Graph 자격 유효성 검사(설정 화면) — 저장하지 않음.</summary>
    Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default);
}
