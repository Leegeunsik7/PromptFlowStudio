using System;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>갤러리 자산 종류 — 이미지(추출 프레임/샘플) / 비디오(세그먼트/롱폼 병합).</summary>
public enum GalleryAssetKind { Image, Video }

/// <summary>
/// [PromptFlow Studio v5.6] 스튜디오 갤러리 자산 — 플로우 노드가 생성한 이미지/비디오 1건.
///   ▸ <see cref="Url"/> 은 로컬 파일 경로 · http(s) URL · data URL 중 하나. 프로젝트(project.json)에 임베드되어 역호환(구 JSON 은 빈 목록).
/// </summary>
public sealed class GalleryAsset
{
    /// <summary>자산 위치 — 파일 경로 / http URL / data URL.</summary>
    public string Url { get; set; } = "";
    /// <summary>이미지/비디오 구분.</summary>
    public GalleryAssetKind Kind { get; set; }
    /// <summary>표시 라벨(예: "롱폼 병합", "키프레임 f120").</summary>
    public string Label { get; set; } = "";
    /// <summary>생성 시각(Unix ms).</summary>
    public long CreatedMs { get; set; }
}
