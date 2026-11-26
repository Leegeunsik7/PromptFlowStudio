namespace PromptFlowStudio.Services.Render;

/// <summary>작업실 주 생성 경로 — 입력 없음 / 프롬프트 전용(T2V) / 참조 이미지 보조(I2V).</summary>
public enum WorkspaceGenerationPath : byte { None, TextToVideo, ImageToVideo }

/// <summary>[✨ 생성 시작] 차단 사유 — None 이면 시작 가능.</summary>
public enum WorkspaceStartBlock : byte { None, Busy, MissingInput, DirectEngineUnavailable }

/// <summary>
/// [PromptFlowStudio] 작업실 주 생성 버튼 준비도(순수 함수 · 0B 할당).
///   ▸ 버튼 활성은 입력(프롬프트 또는 참조 이미지)과 Busy 만으로 판단 — 엔진 키 미구성이 유효 T2V 프롬프트를 잠그지 않는다.
///   ▸ 5초 단일 컷만 Wan 직결 경로(키 필요). 10s/15s/앰비언트는 렌더 큐(디스패치 시점 엔진 확정)라 Wan 키 불요.
///   ▸ 엔진 전제조건 미충족은 클릭 시 레드액션된 안내 문구로 표시(무음 실패 금지).
/// </summary>
public static class WorkspaceGenerationReadiness
{
    public const string MissingInputMessage = "⚠ 프롬프트(T2V) 또는 참조 사진 1장(I2V)이 필요합니다.";
    public const string DirectEngineUnavailableMessage = "⚠ Wan API 키가 구성되지 않았습니다 — 관리자 설정 › 영상(Wan) 에서 키를 등록하거나 10s/15s 모드(렌더 큐)를 사용하세요.";

    /// <summary>참조 이미지가 있으면 I2V(프롬프트 선택), 없으면 프롬프트가 있을 때 T2V.</summary>
    public static WorkspaceGenerationPath ResolvePath(string? prompt, string? imagePath)
    {
        if (!string.IsNullOrWhiteSpace(imagePath)) return WorkspaceGenerationPath.ImageToVideo;
        if (!string.IsNullOrWhiteSpace(prompt)) return WorkspaceGenerationPath.TextToVideo;
        return WorkspaceGenerationPath.None;
    }

    /// <summary>버튼 활성 — 엔진 상태와 무관(클릭 시 <see cref="Evaluate"/> 가 안내).</summary>
    public static bool CanPress(bool busy, string? prompt, string? imagePath)
        => !busy && ResolvePath(prompt, imagePath) != WorkspaceGenerationPath.None;

    /// <summary>5초 단일 컷 외 모드는 렌더 큐 경로.</summary>
    public static bool UsesRenderQueue(VideoDurationMode mode) => mode != VideoDurationMode.Sec5_Single;

    /// <summary>클릭 시 최종 판정 — 직결 경로(5초 단일)만 Wan 엔진 활성 요구.</summary>
    public static WorkspaceStartBlock Evaluate(bool busy, string? prompt, string? imagePath, VideoDurationMode mode, bool directEngineEnabled)
    {
        if (busy) return WorkspaceStartBlock.Busy;
        if (ResolvePath(prompt, imagePath) == WorkspaceGenerationPath.None) return WorkspaceStartBlock.MissingInput;
        if (!UsesRenderQueue(mode) && !directEngineEnabled) return WorkspaceStartBlock.DirectEngineUnavailable;
        return WorkspaceStartBlock.None;
    }

    /// <summary>차단 사유 → 레드액션된 사용자 안내(None/Busy 는 null).</summary>
    public static string? BlockMessage(WorkspaceStartBlock block) => block switch
    {
        WorkspaceStartBlock.MissingInput => MissingInputMessage,
        WorkspaceStartBlock.DirectEngineUnavailable => PromptFlowStudio.Services.Pluto.PlutoAutonomousActivityFeedService.Redact(DirectEngineUnavailableMessage),
        _ => null,
    };
}
