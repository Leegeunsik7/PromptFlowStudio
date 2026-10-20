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

    /// <summary>🌟 제미나이 픽 — 클릭 시 입력창 통째 교체(정적 리터럴 참조).</summary>
    public static readonly PromptChip[] Picks =
    {
        new("스튜디오 인물 화보", "깔끔한 화이트 스튜디오 배경 앞에서 자연스럽게 포즈를 취하는 인물, 소프트박스 키 라이트와 은은한 림 라이트, 85mm 인물 렌즈 얕은 심도, 하이엔드 매거진 화보 톤", "📸"),
        new("3D 감성 애니",      "따뜻한 햇살이 드는 작은 마을을 걷는 동글동글한 3D 애니메이션 캐릭터, 부드러운 서브서피스 스캐터링 피부, 파스텔 색감, 픽사풍 감성 연출", "🎨"),
        new("16mm 빈티지 필름",  "늦은 오후 바닷가 산책길의 일상 풍경, 16mm 필름 그레인과 살짝 바랜 색감, 라이트 리크, 핸드헬드 카메라의 자연스러운 흔들림, 레트로 홈무비 감성", "🎞️"),
        new("파스텔 몽환 조명",  "분홍빛과 하늘빛이 섞인 파스텔 안개 속에 서 있는 인물, 부드러운 블룸과 빛 번짐, 공중에 떠다니는 반짝이 입자, 꿈결 같은 몽환적 조명", "🌌"),
    };

    /// <summary>
    /// 🪄 AI 문장 자동 강화 — 기존 문장 뒤에 <see cref="QualityTokens"/> 를 ", " 로 1회 결합.
    ///   빈 입력은 토큰 리터럴 참조, 이미 강화된 문장은 현재 참조 그대로(0B · 중복 0), 결합 시 span 슬라이스 + <c>string.Create</c> 1회.
    /// </summary>
    public static string EnhancePromptWithGemini(string? input) => BabyStudioMatrix.AppendModifier(input, QualityTokens);

    /// <summary>추천 프리셋 원클릭 주입 — 범위 밖 인덱스는 현재 입력 유지(0B).</summary>
    public static string InjectPick(int index, string current)
        => (uint)index < (uint)Picks.Length ? Picks[index].Snippet : current;
}
