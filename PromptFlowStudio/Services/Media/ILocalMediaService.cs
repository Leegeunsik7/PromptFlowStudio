using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// 원격 미디어를 로컬로 자산화한 결과.
///   ⚠ <see cref="PlaybackUrl"/> 는 성공 시 로컬 가상호스트 URL, 실패 시 원격 URL(폴백)이라 항상 재생 가능한 값이다.
/// </summary>
/// <param name="Ok">로컬 저장 성공 여부. false 면 원격 폴백 상태.</param>
/// <param name="LocalPath">디스크 절대경로(성공 시). 실패 시 null.</param>
/// <param name="PlaybackUrl">&lt;video src&gt; 에 바로 바인딩할 재생 URL(로컬 가상호스트 또는 원격 폴백).</param>
/// <param name="SourceUrl">원격 원본 URL(24시간 만료).</param>
/// <param name="Bytes">저장된 파일 크기(바이트). 실패 시 0.</param>
/// <param name="Message">사람이 읽는 상태 메시지.</param>
public sealed record LocalMediaResult(
    bool Ok, string? LocalPath, string PlaybackUrl, string SourceUrl, long Bytes, string Message);

/// <summary>
/// 원격 미디어(Wan video_url 등)를 <c>%LocalAppData%/PromptFlowStudio/Media/{JobId}/</c> 로
///   고성능 스트리밍 다운로드해 영구 자산화하고, BlazorWebView 에서 재생 가능한 경로로 변환하는 계약.
///   구현은 <see cref="LocalMediaService"/>(BCL HttpClient + System.IO · 외부 NuGet 0).
/// </summary>
public interface ILocalMediaService
{
    /// <summary>미디어 저장 루트(<c>%LocalAppData%/PromptFlowStudio/Media</c> 또는 <c>Media:Root</c> 재정의). 가상 호스트 매핑 대상.</summary>
    string MediaRoot { get; }

    /// <summary>BlazorWebView 로컬 재생용 가상 호스트명(예: <c>media.promptflow.local</c>). <see cref="MediaRoot"/> 에 매핑된다.</summary>
    string VirtualHost { get; }

    /// <summary>
    /// 원격 URL 을 <c>{MediaRoot}/{jobId}/</c> 로 스트리밍 다운로드한다(전체를 RAM 에 올리지 않음).
    ///   실패해도 예외를 던지지 않고 원격 URL 을 폴백으로 담아 반환한다(호출측 작업 실패 방지).
    /// </summary>
    /// <param name="jobId">작업 식별자(폴더명). 보통 Wan task_id.</param>
    /// <param name="remoteUrl">다운로드할 원격 미디어 URL.</param>
    /// <param name="progress">0–100 다운로드 진행률(선택). Content-Length 없으면 완료 시 100 만 보고.</param>
    /// <param name="ct">취소 토큰. 취소 시 부분파일을 정리하고 폴백으로 반환한다.</param>
    Task<LocalMediaResult> DownloadAsync(
        string jobId, string remoteUrl, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>디스크 절대경로를 BlazorWebView 재생용 가상호스트 URL(<c>https://{VirtualHost}/…</c>)로 변환한다. 루트 밖이면 file:// 폴백.</summary>
    string ToPlaybackUrl(string localPath);

    /// <summary>디스크 절대경로를 <c>file:///</c> URL 로 변환(완결성용 헬퍼). ⚠ BlazorWebView(https 오리진)에선 대개 재생 차단됨 — 가상호스트 URL 을 쓸 것.</summary>
    string ToFileUrl(string localPath);
}
