using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📈 SNS 채널 카테고리 — 아기 마일스톤 · 반려동물 힐링 · 데일리 감성 · 감성 ASMR.</summary>
public enum SocialChannelCategory
{
    BabyMilestones = 0,
    PetHealing = 1,
    DailyAesthetic = 2,
    EmotionalASMR = 3,
}

/// <summary>📈 SNS 급상승 BGM 스타일.</summary>
public enum SocialBgmStyle
{
    LullabyLoFi = 0,
    AcousticWarmth = 1,
    MusicBoxReverb = 2,
}

/// <summary>📈 추천 프롬프트 태그 — 카메라 · 조명 · 주체 행동.</summary>
public sealed record SocialPromptTags(string Camera, string Lighting, string SubjectAction);

/// <summary>📈 SNS 트렌드 인사이트 1건 — 플랫폼 · 채널 공식 · 바이럴 메커니즘 · 페이싱 · BGM · 해시태그.</summary>
public sealed record SocialTrendInsight(
    string Id,
    SocialPlatform Platform,
    SocialChannelCategory ChannelCategory,
    string Title,
    string ViralMechanism,
    SocialPromptTags RecommendedPromptTags,
    string PacingGuideline,
    SocialBgmStyle TrendingBgmStyle,
    IReadOnlyList<string> ViralHashtagSet)
{
    /// <summary>🧠 Gemini 추출 인사이트 여부(false = 내장 벤치마크).</summary>
    public bool FromLlm { get; init; }

    /// <summary>스튜디오 테마 카테고리 매핑.</summary>
    public StudioThemeCategory ThemeCategory => PlutoSocialRadarEngine.ToTheme(ChannelCategory);

    /// <summary>작업실 감성 BGM 트랙 매핑.</summary>
    public BabyBgmTrack BgmTrack => PlutoSocialRadarEngine.ToBgmTrack(TrendingBgmStyle);

    /// <summary>🚀 스튜디오 주입용 프롬프트 — 주체 행동 · 조명 · 바이럴 훅 · 페이싱 + 카메라.</summary>
    public string BuildStudioPrompt()
    {
        var t = RecommendedPromptTags;
        var sb = new System.Text.StringBuilder(256);
        sb.Append(PlutoTrendEngine.EnglishName(ThemeCategory)).Append(" theme, vertical 9:16 short, ")
          .Append(t.SubjectAction).Append(", ").Append(t.Lighting)
          .Append(", viral hook: ").Append(ViralMechanism)
          .Append(", pacing: ").Append(PacingGuideline)
          .Append(", camera: ").Append(t.Camera);
        return sb.ToString();
    }
}
