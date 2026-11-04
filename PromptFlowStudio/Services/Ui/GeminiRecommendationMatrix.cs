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
        new("퀀트 OLED 차트",    "칠흑 같은 OLED 다크 모니터 위로 네온 그린·레드 캔들스틱 차트가 실시간으로 흐르는 퀀트 트레이딩 화면, 미세하게 깜빡이는 호가창, 얕은 심도의 35mm 클로즈업, 사이버 핀테크 무드", "📈"),
        new("핀테크 앰비언트",   "새벽 고층 오피스 창밖으로 펼쳐진 도시 야경과 은은히 빛나는 멀티 모니터 금융 대시보드, 천천히 흐르는 데이터 라인, 부드러운 카메라 드리프트, 루프 친화 앰비언트 배경", "💹"),
        new("트레이딩 데스크",   "심야 월스트리트 트레이딩 데스크, 6분할 모니터에 흐르는 차트와 체결 터미널, 키보드 위 손의 클로즈업, 블루 톤 모니터 반사광과 볼륨메트릭 라이팅, 시네마틱 금융 스릴러 톤", "💻"),
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
