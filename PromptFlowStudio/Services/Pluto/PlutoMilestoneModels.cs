using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎞 4컷 성장 마일스톤 테마.</summary>
public enum PlutoMilestoneTheme : byte
{
    /// <summary>💯 100일의 기적.</summary>
    Day100 = 0,
    /// <summary>🎂 첫돌의 감동.</summary>
    FirstBirthday = 1,
    /// <summary>☀️ 우리아이 하루.</summary>
    DailyRoutine = 2,
}

/// <summary>🎞 4컷 중 1컷 — 슬롯(1~4) · 시간 구간 · 프롬프트(앵커 선두) · 카메라 · BGM.</summary>
public sealed class PlutoMilestoneScene
{
    public int SlotIndex { get; init; }
    public string TimeRange { get; init; } = "";
    public string SceneTitle { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string CameraMovement { get; init; } = "";
    public string RecommendedBgm { get; init; } = "";

    /// <summary>구간 길이(초) — "0-4s" → 4.</summary>
    public float DurationSec => PlutoMilestoneOrchestrator.DurationOf(SlotIndex);
}

/// <summary>🎞 4컷 성장 마일스톤 플랜(정확히 4씬 · 15초).</summary>
public sealed class PlutoMilestonePlan
{
    public string MilestoneName { get; init; } = "";
    public PlutoMilestoneTheme Theme { get; init; }
    public IReadOnlyList<PlutoMilestoneScene> Scenes { get; init; } = Array.Empty<PlutoMilestoneScene>();
    public string PersonaConsistencyAnchor { get; init; } = "";

    /// <summary>LLM(Gemini/로컬 게이트웨이) 결과가 1컷 이상 채택됐는지.</summary>
    public bool UsedLlm { get; init; }

    /// <summary>🌊 스트리밍으로 LLM 씬이 도착·채택된 슬롯 비트(슬롯 1 = bit0) — 점진 페인트 표시용.</summary>
    public int StreamedSlotMask { get; init; }

    /// <summary>🎬 스토리보드 4슬롯 일괄 적용 입력(<c>StoryboardViewModel.ApplyDirectorShots</c>).</summary>
    public PromptFlowStudio.Services.AI.DirectorShot[] ToDirectorShots()
    {
        var shots = new PromptFlowStudio.Services.AI.DirectorShot[Scenes.Count];
        for (var i = 0; i < shots.Length; i++)
        {
            var s = Scenes[i];
            shots[i] = new PromptFlowStudio.Services.AI.DirectorShot(s.SlotIndex, s.SceneTitle, s.Prompt, s.CameraMovement, s.DurationSec);
        }
        return shots;
    }
}
