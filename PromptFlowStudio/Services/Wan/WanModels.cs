using System;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// DashScope 비동기 작업 상태 — 통의만상(Wan) 영상생성 태스크의 생명주기.
///   서버 원문(PENDING/RUNNING/SUCCEEDED/FAILED/CANCELED/UNKNOWN)을 <see cref="WanStatusEx.Parse"/> 로 매핑한다.
/// </summary>
public enum WanTaskStatus
{
    /// <summary>알 수 없음(태스크 미존재 또는 상태 불명) — 서버 UNKNOWN·파싱 실패의 기본값.</summary>
    Unknown = 0,
    /// <summary>대기열 등록됨(아직 시작 전).</summary>
    Pending,
    /// <summary>렌더링 진행 중.</summary>
    Running,
    /// <summary>성공 — video_url 사용 가능(발급 후 24시간 유효).</summary>
    Succeeded,
    /// <summary>실패 — code/message 에 원인.</summary>
    Failed,
    /// <summary>취소됨.</summary>
    Canceled,
}

/// <summary>문자열 ↔ <see cref="WanTaskStatus"/> 변환 도우미.</summary>
public static class WanStatusEx
{
    /// <summary>서버 task_status 문자열을 열거형으로(대소문자 무시). 미지 값은 Unknown.</summary>
    public static WanTaskStatus Parse(string? s) => (s ?? "").Trim().ToUpperInvariant() switch
    {
        "PENDING"   => WanTaskStatus.Pending,
        "RUNNING"   => WanTaskStatus.Running,
        "SUCCEEDED" => WanTaskStatus.Succeeded,
        "FAILED"    => WanTaskStatus.Failed,
        "CANCELED"  => WanTaskStatus.Canceled,
        _           => WanTaskStatus.Unknown,
    };

    /// <summary>더 이상 폴링할 필요 없는 종료 상태인지(성공/실패/취소).</summary>
    public static bool IsTerminal(this WanTaskStatus s) =>
        s is WanTaskStatus.Succeeded or WanTaskStatus.Failed or WanTaskStatus.Canceled;
}

/// <summary>
/// 영상생성 요청 파라미터 — 프롬프트만 필수, 나머지는 기본값(미지정 시 appsettings <c>Wan:*</c>).
///   ※ wan2.1 모델은 길이가 5초로 고정이라 duration 파라미터가 없다.
/// </summary>
public sealed record WanVideoRequest
{
    /// <summary>영상 생성 프롬프트(서버 제한 800자). 필수.</summary>
    public required string Prompt { get; init; }

    /// <summary>해상도 "가로*세로"(예: 1280*720). 미지정 시 서비스 기본값(Wan:Size).</summary>
    public string? Size { get; init; }

    /// <summary>모델 오버라이드(예: wan2.1-t2v-plus). 미지정 시 Wan:Model.</summary>
    public string? Model { get; init; }

    /// <summary>프롬프트 자동 확장(smart rewrite). 기본 true.</summary>
    public bool PromptExtend { get; init; } = true;

    /// <summary>워터마크 표시. 기본 false.</summary>
    public bool Watermark { get; init; } = false;

    /// <summary>시드(재현성 · 64비트). 로컬 엔진은 원값, 클라우드는 <see cref="ToCloudSeed"/> 로 [0, 2147483647] 결정적 폴딩. 미지정 시 서버 랜덤.</summary>
    public long? Seed { get; init; }

    /// <summary>클라우드(DashScope·SiliconFlow) 시드 범위 [0, int.MaxValue] 로 결정적 폴딩 — 같은 64비트 시드는 항상 같은 31비트 시드.</summary>
    public static int? ToCloudSeed(long? seed) => seed is long v ? (int)(v & int.MaxValue) : null;

    /// <summary>참조 이미지 로컬 경로. 지정 시 i2v(이미지→영상)로 분기 — 업로드 후 img_url 로 바인딩된다.</summary>
    public string? ImagePath { get; init; }

    /// <summary>사전 업로드된 이미지 URL(oss:// 또는 http). 지정 시 <see cref="ImagePath"/> 재업로드를 건너뛰고 그대로 사용
    ///   (앵커 이미지의 프로젝트 단위 OSS 캐싱 최적화 — 컷마다 재업로드 방지). 이것만 있어도 i2v 로 분기.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>v16.5.0: 끝 프레임(Last Frame) 로컬 경로. 첫 프레임(<see cref="ImagePath"/>/<see cref="ImageUrl"/>)과 함께 지정 시
    ///   이중 키프레임 보간(kf2v · first_frame_url/last_frame_url)으로 분기 — 정확한 모션 동선 보장.</summary>
    public string? LastFramePath { get; init; }

    /// <summary>v16.5.0: 사전 업로드된 끝 프레임 URL(oss:// 또는 http). DashScope 첫 키에서만 재사용.</summary>
    public string? LastFrameUrl { get; init; }

    /// <summary>v15.2.1: 네거티브 프롬프트(input.negative_prompt, 서버 제한 500자). 비어 있으면 필드 생략 — 기존 페이로드와 동일.</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>v15.4.0: Wan 2.1 튜너 값(스텝·CFG·모션 버킷·얼굴 보정·60FPS 보간). null 이면 엔진 기본값.
    ///   DashScope 는 해당 파라미터를 받지 않아 Seed 만 반영되고, 로컬 CLI 엔진은 인자 템플릿 자리표시자로 전달된다.</summary>
    public WanTuning? Tuning { get; init; }
}

/// <summary>v15.4.0: Wan 2.1 생성 튜닝 스냅숏(값 형식 · 복사 0B).</summary>
public readonly record struct WanTuning(int Steps, float CfgScale, int MotionBucket, bool FaceSharpen, bool Interpolate60);

/// <summary>[1단계] 태스크 제출 결과. Ok=false 면 Message 에 원인, TaskId=null.</summary>
public sealed record WanSubmitResult(
    bool Ok, string? TaskId, WanTaskStatus Status, string Message, string? RequestId = null);

/// <summary>[2단계] 태스크 상태 조회 1회 결과. 성공 종료 시 VideoUrl, 실패 시 Code/Message.</summary>
public sealed record WanTaskResult(
    bool Ok, WanTaskStatus Status, string? VideoUrl, string? Code, string Message);

/// <summary>[고수준] 제출→폴링→완료까지의 최종 결과(<see cref="IWanApiService.GenerateAsync"/> 반환).</summary>
public sealed record WanGenerationResult(
    bool Ok, WanTaskStatus Status, string? VideoUrl, string Message, string? TaskId, TimeSpan Elapsed);

/// <summary>[I2V] 로컬 이미지 업로드 결과. 성공 시 oss:// URL, 실패 시 Message 에 원인.
///   v16.3.1: <paramref name="Failure"/> = 실패 원인 분류(UI 알림·재시도 판단) — 성공 시 None.</summary>
public sealed record WanUploadResult(bool Ok, string? OssUrl, string Message, WanUploadFailure Failure = WanUploadFailure.None);

/// <summary>
/// 진행 상황 스냅샷 — 폴링 루프가 <see cref="IProgress{T}"/> 로 보고한다.
///   ⚠ 서버가 백분율을 주지 않으므로 <see cref="Percent"/> 는 상태·폴링수 기반 추정치(0–100)다.
/// </summary>
public sealed record WanProgress(
    WanTaskStatus Status, string Phase, int Percent, int PollCount, TimeSpan Elapsed, string? TaskId);
