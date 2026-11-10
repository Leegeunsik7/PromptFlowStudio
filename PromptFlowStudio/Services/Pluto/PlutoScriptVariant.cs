using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎞 트리플렛 코파일럿 1개 스타일 해석 — 15초 3막(0-4s Hook · 4-11s Climax · 11-15s Emotional Smile) + 카메라 + BGM.</summary>
public sealed record PlutoScriptVariant(
    string VariantId,
    string StyleName,
    string HookTitle,
    string Prompt,
    string CameraMovement,
    string RecommendedBgm,
    string Act1Hook,
    string Act2Climax,
    string Act3Ending)
{
    /// <summary>🎵 추천 BGM 트랙(스튜디오 BGM 토글 결선용).</summary>
    public BabyBgmTrack BgmTrack { get; init; } = BabyBgmTrack.MusicBox;

    /// <summary>🏷 감지 마일스톤(한국어).</summary>
    public string MilestoneKo { get; init; } = "";

    /// <summary>🧠 Gemini 생성 여부(false = 로컬 템플릿 + 스타일 모디파이어 사전).</summary>
    public bool UsedLlm { get; init; }

    /// <summary>🚀 스튜디오 주입용 프롬프트(3막 + 카메라).</summary>
    public string BuildStudioPrompt() => Prompt + ", camera: " + CameraMovement;
}
