using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎬 Pluto 디렉터 랭크 티어 — 누적 XP 구간별 시네마토그래피 앵커(광학 키워드 · 프롬프트 모디파이어).
/// 불변 정적 인스턴스(<see cref="PlutoDirectorRankService.Tiers"/>)만 사용 — 조회 경로 무할당.
/// </summary>
public sealed class PlutoDirectorTier
{
    public PlutoDirectorTier(int level, string title, string englishTitle, int requiredXp, int nextTierXp, string[] keywords, string promptModifier, string[]? koreanTraits = null)
    {
        Level = level;
        Title = title;
        EnglishTitle = englishTitle;
        RequiredXp = requiredXp;
        NextTierXp = nextTierXp;
        CinematographyKeywords = keywords;
        PromptModifier = promptModifier;
        KoreanTraits = koreanTraits ?? System.Array.Empty<string>();
    }

    /// <summary>1 ~ 4.</summary>
    public int Level { get; }
    public string Title { get; }
    public string EnglishTitle { get; }
    /// <summary>티어 진입 누적 XP(하한 포함).</summary>
    public int RequiredXp { get; }
    /// <summary>다음 티어 진입 XP(최고 티어는 표시용 목표치).</summary>
    public int NextTierXp { get; }
    public IReadOnlyList<string> CinematographyKeywords { get; }
    /// <summary>프롬프트 말미 결합용 모디파이어(", " 로 시작).</summary>
    public string PromptModifier { get; }
    /// <summary>🪐 HUD 툴팁용 한글 광학 특성(예: "85mm f/1.4", "골든아워 역광").</summary>
    public IReadOnlyList<string> KoreanTraits { get; }

    /// <summary>최고 티어 여부.</summary>
    public bool IsMax => Level >= PlutoDirectorRankService.MaxLevel;
}
