using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] 비디오 생성 엔진 추상화 — 텍스트/이미지→영상 생성기를 플러그인처럼 교체하기 위한 공통 계약.
///   ▸ 기본 구현은 <see cref="WanApiService"/>(Alibaba Wan 2.1 · 원격 REST). 로컬 CLI 엔진(ComfyUI/CogVideoX 래퍼 등)은
///     <see cref="ComfyUiVideoEngine"/> 처럼 이 인터페이스를 구현해 등록만 하면 <see cref="VideoEngineSelector"/> 로 선택된다.
///   ▸ 결과의 <see cref="WanGenerationResult.VideoUrl"/> 은 원격 URL 또는 <b>로컬 파일 경로</b>일 수 있다(로컬 엔진).
///     LocalMediaService.DownloadAsync 가 로컬 경로면 복사, 원격이면 스트리밍하도록 처리한다.
/// </summary>
public interface IVideoGenerationEngine
{
    /// <summary>엔진 식별 키(설정 저장/선택용). 예: "Wan", "ComfyUI".</summary>
    string Key { get; }

    /// <summary>표시 이름. 예: "Alibaba Wan 2.1".</summary>
    string DisplayName { get; }

    /// <summary>이 엔진이 사용 가능한지(키/실행파일 구성됨). false 면 선택돼도 생성 불가.</summary>
    bool Enabled { get; }

    /// <summary>영상 생성(제출→완료). 결과 VideoUrl 은 원격 URL 또는 로컬 파일 경로. 실패는 Graceful(Ok=false).</summary>
    Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default);

    /// <summary>i2v 참조 이미지 업로드(엔진별 방식). 로컬 엔진은 미지원일 수 있음(Ok=false).</summary>
    Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default);
}
