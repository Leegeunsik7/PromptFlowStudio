using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Ui;

/// <summary>스위치 역할 가이드 1개 — 켰을 때/껐을 때 연출 차이 + 권장 사용 시나리오.</summary>
public sealed record SwitchGuide(string Key, string Icon, string Title, string WhenOn, string WhenOff, string Scenario);

/// <summary>
/// [PromptFlowStudio] 스위치 역할 실시간 가이드 — 순수 BCL 오프라인 Authored Dictionary(외부 NuGet 0).
///   ▸ 주요 후처리 스위치(스마트 트랜지션/키네틱 자막/감정선 BGM/Gen-Foley/시각 뱃지/템포 배속/세이프존/피사체 프레이밍)의
///     <b>ON/OFF 연출 차이 + 권장 시나리오</b>를 담아 클릭 즉시 <see cref="ToggleTooltip"/> 카드 토스트로 안내.
/// </summary>
public sealed class SwitchRoleGuideService
{
    /// <summary>스위치 키 상수(호출부 오타 방지).</summary>
    public static class Keys
    {
        public const string SmartTransitions = "smartTransitions";
        public const string KineticCaptions = "kineticCaptions";
        public const string EmotionPacing = "emotionPacing";
        public const string GenFoley = "genFoley";
        public const string VisualBadges = "visualBadges";
        public const string TempoSpeedup = "tempoSpeedup";
        public const string SafeZone = "safeZone";
        public const string SmartFraming = "smartFraming";
    }

    private static readonly Dictionary<string, SwitchGuide> Guides = new(StringComparer.OrdinalIgnoreCase)
    {
        [Keys.SmartTransitions] = new(Keys.SmartTransitions, "🎞️", "스마트 트랜지션",
            "컷 경계에 xfade(페이드·슬라이드·줌·블러)를 자동 적용해 장면이 매끄럽게 이어집니다.",
            "컷을 그대로 이어붙입니다(하드컷) — 빠른 리듬감엔 오히려 유리.",
            "감성·브이로그·시네마틱 스토리. 숏츠의 급전환 개그는 하드컷(OFF)도 좋습니다."),
        [Keys.KineticCaptions] = new(Keys.KineticCaptions, "💬", "키네틱 자막",
            "나레이션을 단어 단위로 색상 전환되는 팝 자막(골드 강조)으로 번인합니다.",
            "정적 자막 또는 자막 없이 진행합니다.",
            "음소거 시청·정보 전달·바이럴 훅. 대사가 있는 컷에 특히 효과적."),
        [Keys.EmotionPacing] = new(Keys.EmotionPacing, "🎭", "감정선 BGM",
            "컷별 긴장도로 BGM 볼륨·덕킹·페이드를 동적으로 조절해 클라이맥스를 고조시킵니다.",
            "BGM을 고정 볼륨으로 재생합니다.",
            "BGM이 있는 스토리형·반전 연출. 하단 텐션 파형으로 밀당을 확인하세요."),
        [Keys.GenFoley] = new(Keys.GenFoley, "🔊", "생성형 폴리",
            "컷 상황에 맞는 효과음(콰광!·뿅!)을 3D 공간 팬으로 좌/우 배치해 믹싱합니다.",
            "효과음 없이 나레이션/BGM만 사용합니다.",
            "코믹·리액션·임팩트 강조. 시각 뱃지와 함께 쓰면 음소거에도 강력."),
        [Keys.VisualBadges] = new(Keys.VisualBadges, "✨", "시각 효과음 뱃지",
            "효과음이 터지는 순간 이모지 뱃지(에취! 🤧)를 화면 상단에 팝업합니다 — 음소거 대응.",
            "화면 뱃지 없이 소리로만 효과음을 전달합니다.",
            "육아 음소거 시청 환경·코믹 강조. Gen-Foley와 세트로 권장."),
        [Keys.TempoSpeedup] = new(Keys.TempoSpeedup, "⚡", "템포 배속",
            "영상과 오디오를 함께 빠르게 재생해 귀여움과 속도감을 살립니다(예: 펭귄 걸음마 2배속).",
            "원본 속도로 재생합니다.",
            "걸음마·빠른 리액션·숏츠 리듬. 롱폼(브이로그)에는 OFF가 자연스럽습니다."),
        [Keys.SafeZone] = new(Keys.SafeZone, "🛡️", "세이프존",
            "자막·뱃지를 선택한 플랫폼(숏츠/릴스/틱톡)의 UI 가림 영역을 피해 자동 배치합니다.",
            "기존 위치(하단 13%·상단 10%)에 그대로 배치합니다.",
            "숏츠/릴스/틱톡 업로드 전 필수. 미리보기 오버레이로 가림을 확인하세요."),
        [Keys.SmartFraming] = new(Keys.SmartFraming, "🎯", "피사체 세이프존 프레이밍",
            "아기 얼굴을 상단-중앙 안전대(20~65%)에 두도록 Wan 프롬프트에 구도 키워드를 자동 주입합니다.",
            "프롬프트 원본 구도로 생성합니다(얼굴이 하단/상단에 잘릴 수 있음).",
            "세로 숏츠·얼굴 클로즈업. 세이프존과 함께 쓰면 UI 가림 없는 완성형 구도."),
    };

    /// <summary>키로 가이드 조회(없으면 null).</summary>
    public SwitchGuide? Get(string? key) => !string.IsNullOrWhiteSpace(key) && Guides.TryGetValue(key!, out var g) ? g : null;

    /// <summary>전체 가이드.</summary>
    public IReadOnlyList<SwitchGuide> All => Guides.Values.ToList();
}
