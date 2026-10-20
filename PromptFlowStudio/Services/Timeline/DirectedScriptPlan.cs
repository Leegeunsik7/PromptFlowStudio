using System.Collections.Generic;

namespace PromptFlowStudio.Services.Timeline;

/// <summary>씬 감정 등급 — 자이로 모션·프롬프트 수식어·립싱크 강도 배정 기준.</summary>
public enum SceneEmotion { Calm, Joy, Tension, Sad, Epic }

/// <summary>
/// [PromptFlow Studio v7.2.0] 오토 디렉팅된 씬 1개 — 타임코드/감정/자이로 모션/립싱크 강도/프롬프트 수식어.
/// </summary>
public sealed record DirectedScene(
    int Index, string Text, double StartSec, double DurationSec,
    SceneEmotion Emotion, string GyroMotion, double LipSyncIntensity, string PromptModifier);

/// <summary>대본 파싱 결과 — 씬 목록 + 총 길이(초). <see cref="AiAutoDirectorEngine"/> 산출.</summary>
public sealed record DirectedScriptPlan(IReadOnlyList<DirectedScene> Scenes, double TotalSec)
{
    public static DirectedScriptPlan Empty { get; } = new(System.Array.Empty<DirectedScene>(), 0);
}
