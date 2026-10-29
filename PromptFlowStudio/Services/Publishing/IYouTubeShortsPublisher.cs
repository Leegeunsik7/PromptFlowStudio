using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>쇼츠 업로드 메타데이터 — 제목(≤100) · 설명(≤5000) · 태그(#Shorts 포함) · 공개 범위 · 예약 시각(UTC, private 강제).</summary>
public sealed record ShortsMetadata(string Title, string Description, IReadOnlyList<string> Tags, string PrivacyStatus = "private", DateTime? PublishAtUtc = null);

/// <summary>쇼츠 발행 결과(예외 0). IsMock=true 면 자격 미구성 오프라인 모의 발행(실제 업로드 없음).</summary>
public sealed record ShortsPublishResult(bool Ok, bool IsMock, string? VideoId, string? Url, bool CaptionUploaded, string Message);

/// <summary>
/// YouTube Shorts 원클릭 발행 계약 — 영상 resumable 업로드 + 옆 <c>.srt</c> 자막 트랙(captions.insert) 자동 등록.
///   ※ 기존 <see cref="Wan.IYouTubePublishService"/>(appsettings 자격 · 스토리보드 비공개 업로드)와 이름 충돌을 피해 별도 계약으로 둔다.
/// </summary>
public interface IYouTubeShortsPublisher
{
    /// <summary>OAuth 자격(ClientId/Secret/RefreshToken) 구성 여부 — false 면 <see cref="PublishAsync"/> 는 모의 발행.</summary>
    bool Enabled { get; }

    /// <summary>영상 업로드 → (자막 파일 존재 시) 캡션 트랙 등록. <paramref name="srtPath"/> null 이면 영상 옆 <c>.srt</c> 자동 탐색.</summary>
    Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>리프레시 토큰으로 액세스 토큰 발급 가능 여부(설정 화면 연결 배지).</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}
