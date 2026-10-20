using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Ui;

/// <summary>도움말 섹션 1개(소제목 + 본문). 본문은 줄바꿈(\n)으로 여러 줄.</summary>
public sealed record ContextHelpSection(string Heading, string Body);

/// <summary>메뉴별 컨텍스트 도움말 1개 — 핵심 개념 + 섹션(플랫폼별 권장·실패 시 조치 등).
///   ⚠ 이름 <c>ContextHelpTopic</c>: 기존 F1 HelpDrawer 의 <see cref="Services.Help"/>.HelpTopic 과 충돌 회피.</summary>
public sealed record ContextHelpTopic(string Key, string Icon, string Title, string Concept, IReadOnlyList<ContextHelpSection> Sections);

/// <summary>
/// [PromptFlowStudio] 메뉴별 컨텍스트 도움말 콘텐츠 — 순수 BCL 오프라인 Authored Dictionary(외부 NuGet 0).
///   ▸ 주요 메뉴(아기 테마 백과사전/숏츠·롱폼 스위처/OSMU 내보내기/세이프존 가이드 등)의 <b>핵심 개념 · 플랫폼별 권장 옵션 · 실패 시 조치 팁</b>을
///     정돈해 <see cref="HelpBadge"/> [❓] 클릭 시 <see cref="ContextHelpModal"/> 로 표출.
/// </summary>
public sealed class HelpContentService
{
    /// <summary>도움말 토픽 키 상수.</summary>
    public static class Keys
    {
        public const string BabyThemes = "babyThemes";
        public const string FormatSwitch = "formatSwitch";
        public const string Osmu = "osmu";
        public const string SafeZone = "safeZone";
        public const string TrendingAudio = "trendingAudio";
        public const string TensionTimeline = "tensionTimeline";
    }

    private static readonly Dictionary<string, ContextHelpTopic> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        [Keys.BabyThemes] = new(Keys.BabyThemes, "📚", "아기 테마 백과사전",
            "10대 카테고리 × 50+ 소분류 테마를 고른 뒤, 사진 1장 + 포맷만 선택하면 대본·프롬프트·키네틱 자막·텐션·폴리가 자동 완성됩니다.",
            new List<ContextHelpSection>{
                new("핵심 개념", "각 테마는 고유 렌즈·조명·페르소나 어조·3D 폴리를 갖는 6컷(숏츠)/16컷(롱폼) 완성형입니다.\n좌측 카테고리 탭 → 우측 소분류 카드 선택 → [✨ 자동 구성] 또는 [🚀 즉시 생성]."),
                new("권장 사용", "먼저 우측 [⚓ 앵커]에 아기 사진을 등록하면 전 컷 i2v로 얼굴 일관성이 유지됩니다.\n배속이 필수인 테마(펭귄 2.0x 등)는 템포 배속이 자동 체크됩니다."),
                new("실패 시 조치", "테마 적용이 안 되면 생성/병합 중인지 확인하세요(진행 중엔 적용 불가).\nGemini 키가 없어도 authored 데이터로 오프라인 구동되니 키 없이도 정상입니다."),
            }),
        [Keys.FormatSwitch] = new(Keys.FormatSwitch, "📱", "숏츠 / 롱폼 포맷 스위처",
            "같은 테마를 6컷 숏츠(세로·루프·급스파이크) 또는 16컷 롱폼(가로·기승전결 서사)으로 구성합니다.",
            new List<ContextHelpSection>{
                new("핵심 개념", "숏츠: 1080×1920 · 빠른 배속 · 3초 반전 무한 루프 · 급상승 텐션.\n롱폼: 1920×1080 · 원본 배속 · 인트로/아웃트로 · 완만한 빌드업(22→90→26)."),
                new("권장 사용", "빠른 리액션·개그·밈은 숏츠, 하루 기록·감성 스토리는 롱폼.\n동일 테마를 두 포맷으로 뽑아 A/B로 올려도 좋습니다."),
                new("실패 시 조치", "컷 수가 많아 화면이 길어지면 컷 목록이 자동 스크롤됩니다.\n포맷을 바꾸면 컷이 새로 구성되므로 생성 전에 선택하세요."),
            }),
        [Keys.Osmu] = new(Keys.Osmu, "📦", "OSMU 멀티 플랫폼 내보내기",
            "1회 생성 콘텐츠를 유튜브 숏츠·인스타 릴스·틱톡 최적 포맷(제목·캡션·해시태그·트렌딩 오디오)으로 재구성해 원클릭 복사합니다.",
            new List<ContextHelpSection>{
                new("핵심 개념", "플랫폼 탭마다 정보형/감성형/유머형 문구와 태그가 다르게 재구성됩니다.\n[🎵 트렌딩 오디오] 탭에서 플랫폼별 추천 밈 BGM 태그와 음원 검색 딥링크를 제공."),
                new("플랫폼별 권장", "유튜브: 정보형 제목 + 상세 설명 + 검색 태그.\n인스타: 감성 캡션 + 댓글 유도 + 육아스타그램 태그.\n틱톡: 짧고 강한 훅 + fyp 태그."),
                new("실패 시 조치", "오디오 추천은 실제 음원이 아닌 '검색 태그'입니다 — 각 플랫폼에서 유사 트렌딩 사운드를 찾아 저작권을 확인 후 사용하세요.\n[음원 검색] 버튼이 안 열리면 기본 브라우저 설정을 확인하세요."),
            }),
        [Keys.SafeZone] = new(Keys.SafeZone, "🛡️", "세이프존 가이드",
            "숏츠/릴스/틱톡의 UI(우측 버튼·하단 제목/계정·상단 여백)가 자막·얼굴을 가리지 않도록 안전 영역을 시각화하고, 병합 시 자막/뱃지 위치를 보정합니다.",
            new List<ContextHelpSection>{
                new("핵심 개념", "미리보기·컷 카드의 빨강=위험영역, 초록=안전 밴드, 점선=실제 자막/뱃지 번인 위치(병합과 동일).\n안전 밴드: 숏츠 15~80% · 릴스 20~75% · 틱톡 15~70%."),
                new("플랫폼별 권장", "업로드할 플랫폼을 선택하면 자막이 그 플랫폼 하단 UI 위로, 뱃지가 상단 UI 아래로 이동합니다.\n[🎯 피사체 프레이밍]을 함께 켜면 얼굴도 안전대로 배치됩니다."),
                new("실패 시 조치", "오버레이는 세로(쇼츠) 비율에서만 표시됩니다 — 해상도를 세로로 설정하세요.\n세이프존은 시각 가이드 + 자막/뱃지 보정이며, 원본 영상 픽셀은 변형하지 않습니다."),
            }),
        [Keys.TrendingAudio] = new(Keys.TrendingAudio, "🎵", "트렌딩 오디오",
            "테마 에너지(배속)에 맞춰 유튜브/인스타/틱톡별 유행 밈 BGM 추천 태그를 제시하고, 음원 검색 페이지로 바로 이동합니다.",
            new List<ContextHelpSection>{
                new("핵심 개념", "빠른 테마=코믹 밈 사운드, 느린 테마=감성 오디오로 자동 분기됩니다.\nmetadata_seo.json 에도 함께 저장됩니다."),
                new("실패 시 조치", "추천 태그는 실제 음원이 아니므로, 각 플랫폼 앱의 오디오 라이브러리에서 검색해 붙여넣거나 유사 사운드를 선택하세요.\n저작권/사용 가능 여부는 반드시 앱 내에서 확인하세요."),
            }),
        [Keys.TensionTimeline] = new(Keys.TensionTimeline, "📈", "긴장도 타임라인",
            "1컷부터 마지막 컷까지 긴장도(Tension) 흐름을 하나의 꺾은선(스토리 아크)으로 보여주고, BGM 덕킹·SFX·루프 지점을 표시합니다.",
            new List<ContextHelpSection>{
                new("핵심 개념", "곡선이 오를수록 긴장 고조. 붉은 점선 구간=BGM 덕킹(나레이션), 청록 핀=효과음 발생.\n숏츠는 마지막→첫 컷 루프 연결(🔁)이 표시됩니다."),
                new("권장 사용", "숏츠는 급상승 스파이크가, 롱폼은 완만한 빌드업(22→90→26)이 이상적입니다.\n곡선이 밋밋하면 클라이맥스 컷의 텐션을 높여보세요."),
                new("실패 시 조치", "authored 데이터(프리셋)가 있는 컷만 표시됩니다 — 수동 컷은 텐션이 없을 수 있습니다."),
            }),
    };

    /// <summary>키로 토픽 조회(없으면 null).</summary>
    public ContextHelpTopic? Get(string? key) => !string.IsNullOrWhiteSpace(key) && Topics.TryGetValue(key!, out var t) ? t : null;

    /// <summary>전체 토픽.</summary>
    public IReadOnlyList<ContextHelpTopic> All => Topics.Values.ToList();
}
