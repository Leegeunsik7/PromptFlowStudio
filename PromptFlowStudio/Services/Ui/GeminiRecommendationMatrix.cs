using System;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlowStudio] v15.3.0 ✨ 제미나이 AI 추천 스튜디오 — 추천 프리셋(교체형) 카탈로그 + 시네마틱 품질 토큰 강화(누적형).
///   외부 API 호출 없는 정적 카탈로그(순수 BCL) · Workspace 우측 레일이 사용하며 모바일에도 링크된다.
/// </summary>
public static class GeminiRecommendationMatrix
{
    /// <summary>문장 자동 강화 시 꼬리에 1회 결합되는 시네마틱 품질 향상 토큰.</summary>
    public const string QualityTokens = "masterpiece quality, 8k UHD, soft volumetric lighting, professional color grading";

    /// <summary>🎨 아기 시네마틱 비주얼 스타일 3종 — 그룹 배타(스타일 1개만 유지) 토큰 결합.</summary>
    public static readonly PromptChip[] Picks =
    {
        new("스튜디오 감성 화보",     "cinematic soft studio lighting, 85mm f/1.4 portrait lens, gentle shallow depth of field, warm creamy bokeh, ultra-detailed skin texture", "📷"),
        new("픽사풍 3D 감성 애니",    "heartwarming Pixar-style 3D animation, adorable expressive eyes, vibrant soft pastel lighting, stylized playful atmosphere", "🎨"),
        new("16mm 레트로 홈비디오",   "nostalgic 1990s Kodak 16mm film aesthetic, warm retro grain, gentle vignette, authentic VHS home-video warmth", "📼"),
    };

    /// <summary>
    /// 비주얼 스타일 원클릭 — 이전 스타일 토큰은 제거하고 새 스타일을 꼬리에 1회 결합(중복 0).
    ///   재클릭·범위 밖 인덱스는 현재 참조 유지(0B).
    /// </summary>
    public static string ApplyStyle(string? current, int index) => WanCameraStyleStudio.ApplyExclusive(current, Picks, index);

    /// <summary>
    /// 🪄 AI 문장 자동 강화 — 기존 문장 뒤에 <see cref="QualityTokens"/> 를 ", " 로 1회 결합.
    ///   빈 입력은 토큰 리터럴 참조, 이미 강화된 문장은 현재 참조 그대로(0B · 중복 0), 결합 시 span 슬라이스 + <c>string.Create</c> 1회.
    /// </summary>
    public static string EnhancePromptWithGemini(string? input) => BabyStudioMatrix.AppendModifier(input, QualityTokens);

    /// <summary>추천 프리셋 원클릭 주입 — 범위 밖 인덱스는 현재 입력 유지(0B).</summary>
    public static string InjectPick(int index, string current)
        => (uint)index < (uint)Picks.Length ? Picks[index].Snippet : current;
}
