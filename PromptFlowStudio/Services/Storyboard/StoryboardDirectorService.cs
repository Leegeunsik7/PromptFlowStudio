using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.AI;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>👶 아기 마일스톤 4컷 프리셋 키.</summary>
public enum BabyMilestonePresetKind { HundredDayMiracle, FirstBirthday, DayInLife }

/// <summary>마일스톤 프리셋 — 기본 성장 단계 · 4컷 장면 묘사(영문 Wan 프롬프트 본문).</summary>
public sealed record BabyMilestonePreset(BabyMilestonePresetKind Kind, string Emoji, string Label, string English, BabyGrowthStage DefaultStage, string[] Cuts);

/// <summary>
/// [PromptFlowStudio] 🍼 아기 성장 다이어리 · 마일스톤 4컷 디렉터 — 순수 BCL.
///   ▸ 3종 프리셋(백일의 기적 · 첫돌 성장기 · 아기의 하루) × 4컷.
///   ▸ 전 컷 선두에 동일한 캐릭터 일관성 태그(성장 단계 연령 토큰 + 사용자 특징)를 시드 → 4컷 동일 아기 유지.
///   ▸ 개별 컷 재생성(<see cref="BuildCut"/>) — 컷 텍스트만 교체, 일관성 태그 보존.
/// </summary>
public static class StoryboardDirectorService
{
    public const int CutCount = 4;
    public const string ConsistencyTag = "same baby character in every cut, consistent face and features";
    public const string StyleTail = "soft cinematic natural light, shallow depth of field, photorealistic, 4K, heartwarming family film look";

    public static readonly BabyMilestonePreset[] Presets =
    {
        new(BabyMilestonePresetKind.HundredDayMiracle, "🍼", "백일의 기적", "100-Day Miracle", BabyGrowthStage.HundredDays, new[]
        {
            "tummy time struggle on a soft mat with bright wide eyes, soft morning sunlight streaming through sheer curtains",
            "celebrating the 100th day in a traditional formal baby gown wearing a tiny silver ring, festive 100-day table setting",
            "heartwarming eye contact with loving parents, bright gummy smile, tender close-up",
            "peaceful deep slumber hugging a plush bunny, soft angelic breathing, warm dim nightlight glow",
        }),
        new(BabyMilestonePresetKind.FirstBirthday, "🎂", "첫돌 성장기", "1st Birthday Celebration", BabyGrowthStage.FirstBirthday, new[]
        {
            "wobbly first steps across the living room carpet with arms outstretched toward loving arms",
            "dressed in a colorful silk hanbok festive dress, sparkling first birthday celebration backdrop",
            "grabbing the doljabi item (microphone, pencil or yarn) amidst clapping family and falling confetti",
            "candlelight reflecting on a joyful face beside the first birthday cake with one candle",
        }),
        new(BabyMilestonePresetKind.DayInLife, "☀️", "아기의 하루", "A Day in Baby's Life", BabyGrowthStage.HundredDays, new[]
        {
            "morning yawn and stretching tiny arms in a cozy crib, gentle sunrise light",
            "messy baby food feeding time, giggling with a little spoon, food on chubby cheeks",
            "splashing bubbles in a warm bath with a floating rubber duck, sparkling water droplets",
            "drifting to sleep under a starlight nursery mobile, peaceful twilight mood",
        }),
    };

    public static BabyMilestonePreset PresetOf(BabyMilestonePresetKind kind)
    {
        foreach (var p in Presets) if (p.Kind == kind) return p;
        return Presets[0];
    }

    /// <summary>캐릭터 일관성 태그 — 일관성 고정 문구 + 성장 단계 연령 토큰 + (선택) 사용자 특징.</summary>
    public static string BuildCharacterTag(BabyGrowthStage stage, string? features)
    {
        var age = GeminiPromptService.GrowthStageOf(stage).Tokens;
        var f = features?.Trim();
        return string.IsNullOrEmpty(f) ? ConsistencyTag + ", " + age : ConsistencyTag + ", " + age + ", " + f;
    }

    /// <summary>개별 컷 프롬프트 — 일관성 태그 + 컷 장면 + 공통 스타일(재생성 단위).</summary>
    public static string BuildCut(BabyMilestonePresetKind kind, int index, BabyGrowthStage stage, string? features)
    {
        var p = PresetOf(kind);
        var i = Math.Clamp(index, 0, p.Cuts.Length - 1);
        return BuildCharacterTag(stage, features) + ", " + p.Cuts[i] + ", " + StyleTail;
    }

    /// <summary>프리셋 4컷 일괄 생성(stage null → 프리셋 기본 단계).</summary>
    public static string[] BuildCuts(BabyMilestonePresetKind kind, BabyGrowthStage? stage = null, string? features = null)
    {
        var p = PresetOf(kind);
        var st = stage ?? p.DefaultStage;
        var cuts = new string[p.Cuts.Length];
        for (var i = 0; i < cuts.Length; i++) cuts[i] = BuildCut(kind, i, st, features);
        return cuts;
    }

    /// <summary>프로젝트에 4컷 적용 — 기존 컷 교체, 9:16 세로·성장 프리셋 메타 설정. 앵커가 있으면 i2v(얼굴 유지).</summary>
    public static bool ApplyToProject(StoryboardProject project, BabyMilestonePresetKind kind, BabyGrowthStage? stage = null, string? features = null)
    {
        if (project is null) return false;
        var p = PresetOf(kind);
        var cuts = BuildCuts(kind, stage, features);
        var mode = string.IsNullOrWhiteSpace(project.AnchorImagePath) ? SceneMode.TextToVideo : SceneMode.ImageToVideo;
        project.Scenes.Clear();
        for (var i = 0; i < cuts.Length; i++)
            project.Scenes.Add(new StoryboardScene { Mode = mode, Prompt = cuts[i], Size = "720*1280" });
        project.Title = p.Emoji + " " + p.Label;
        project.ExportResolution = "1080x1920";
        project.IsBabyPreset = true;
        project.PresetKey = "milestone_" + p.Kind.ToString();
        return true;
    }
}
