using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// 알리바바 통의만상(Wan 2.1) 영상생성 API(DashScope async REST) 연동 계약.
///   저수준(제출/조회)과 고수준(제출→폴링→완료 · 진행보고) 을 함께 노출한다.
///   구현은 <see cref="WanApiService"/>(BCL HttpClient + System.Text.Json · 외부 NuGet 0).
/// </summary>
public interface IWanApiService
{
    /// <summary>API 키(appsettings <c>Wan:ApiKey</c>) 구성 여부. false 면 호출부가 비활성/데모 처리.</summary>
    bool Enabled { get; }

    /// <summary>[1단계] 영상생성 태스크 제출 → task_id 반환(비동기, 즉시 반환).</summary>
    Task<WanSubmitResult> SubmitAsync(WanVideoRequest request, CancellationToken ct = default);

    /// <summary>[2단계] task_id 상태를 1회 조회(폴링 단위). 성공 종료 시 VideoUrl 채움.</summary>
    Task<WanTaskResult> QueryAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// [연결 테스트] 저장하지 않은 키·엔드포인트로 인증만 가볍게 확인한다(영상 생성 없음 · 과금 없음).
    ///   더미 task_id 조회로 401/403·InvalidApiKey 면 실패, 그 외(태스크 없음 등)면 인증 통과로 판정.
    /// </summary>
    /// <returns>(성공여부, 사람이 읽는 메시지).</returns>
    Task<(bool Ok, string Message)> ValidateKeyAsync(string apiKey, string baseUrl, CancellationToken ct = default);

    /// <summary>
    /// [I2V] 로컬 이미지를 DashScope 임시 스토리지에 업로드하고 oss:// URL 을 반환한다(getPolicy → OSS 멀티파트 업로드).
    ///   이 URL 은 제출 시 <c>X-DashScope-OssResourceResolve: enable</c> 헤더와 함께 img_url 로 전달된다.
    /// </summary>
    /// <param name="localImagePath">업로드할 로컬 이미지 경로.</param>
    /// <param name="model">업로드 정책 스코프 모델(미지정 시 i2v 기본). 키·엔드포인트와 같은 지역이어야 한다.</param>
    Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default);

    /// <summary>
    /// [고수준] 제출 후 종료(성공/실패/취소)까지 주기적으로 폴링하고 진행 상황을 <paramref name="progress"/> 로 보고한다.
    ///   <paramref name="ct"/> 취소 시 폴링만 중단하고 Canceled 로 반환한다(원격 서버 태스크는 계속될 수 있음).
    /// </summary>
    Task<WanGenerationResult> GenerateAsync(
        WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default);
}
