using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// 자막/뱃지 세이프존 대상 플랫폼 — None(전체화면·기존 위치) + 8대 글로벌/국내 숏폼 플랫폼.
///   ⚠ 신규 멤버는 <b>항상 끝에 추가</b>한다(0~3 기존 직렬화값 불변 → 100% 하위호환).
/// </summary>
public enum SafePlatform
{
    None, YouTubeShorts, InstagramReels, TikTok,
    // ── v2.0.0 확장: 국내외 8대 플랫폼(기존 3 + 신규 5) ──
    NaverClip, KarrotStory, Xiaohongshu, Pinterest, Chzzk,
}

/// <summary>
/// [PromptFlowStudio] 멀티 플랫폼 세이프존 서비스 — 순수 BCL(외부 NuGet 0 · 오프라인).
///   ▸ 각 플랫폼 반응형 UI 가림 영역(상단 탑바/진행바, 우측 상호작용 버튼, 하단 계정/제목/사운드)을
///     세로 화면의 안전 Y 밴드(TopPct~BottomPct)로 모델링 → 키네틱 자막(하단 앵커)·시각 뱃지(상단 앵커) Y 오프셋을 자동 보정.
///   ▸ 8대 플랫폼: 유튜브 숏츠·인스타 릴스·틱톡 + <b>네이버 클립·당근 스토리·샤오홍슈(RED)·핀터레스트·치지직</b>.
///   ▸ <see cref="CaptionBottomFraction"/>/<see cref="BadgeTopFraction"/> 는 <b>정적 순수 계산</b> — <see cref="VideoMergeService"/>가 DI 없이 사용.
///   ▸ <see cref="SafePlatform.None"/> 은 기존 상수(자막 하단 13% · 뱃지 상단 10%)와 동일 → 미지정 시 <b>바이트 동일(하위호환)</b>.
///   ▸ 인스턴스(All/Get)는 UI 셀렉트/라벨용.
/// </summary>
public sealed class MultiPlatformSafeZoneService
{
    /// <summary>플랫폼 세이프존 1개 — 안전 Y 밴드(상단~하단, 화면 높이 비율)와 표시 메타.</summary>
    public sealed record SafeZone(SafePlatform Platform, string Label, string Icon, double TopPct, double BottomPct, string Note);

    // 세로 화면 기준 안전 Y 밴드(0=상단, 1=하단). 밴드 밖은 플랫폼 UI 가 덮는 위험 영역.
    //   TopPct = 상단 탑바/진행바 아래 안전 시작선, BottomPct = 하단 계정/제목/사운드 위 안전 종료선.
    private static readonly SafeZone[] Zones =
    {
        new(SafePlatform.None,            "전체 화면 (가이드 없음)", "🖥️", 0.00, 1.00, "세이프존 미적용 — 기존 위치 유지"),
        new(SafePlatform.YouTubeShorts,   "유튜브 숏츠",             "▶️", 0.15, 0.80, "우측 좋아요/댓글·하단 제목 영역 회피"),
        new(SafePlatform.InstagramReels,  "인스타그램 릴스",         "📸", 0.20, 0.75, "하단 계정명/캡션 영역 회피"),
        new(SafePlatform.TikTok,          "틱톡",                   "🎵", 0.15, 0.70, "우측 프로필/음악 디스크·하단 캡션 회피"),
        // ── 국내외 5대 신규 플랫폼(반응형 UI 실측 근사) ──
        new(SafePlatform.NaverClip,       "네이버 클립",            "🟢", 0.14, 0.78, "상단 탑바·우측 상호작용·하단 제목/출처 회피"),
        new(SafePlatform.KarrotStory,     "당근 스토리",            "🥕", 0.16, 0.82, "상단 스토리 진행바·하단 동네/계정/공감 버튼 회피"),
        new(SafePlatform.Xiaohongshu,     "샤오홍슈 (RED)",         "📕", 0.15, 0.72, "상단 검색바·우측 좋아요/컬렉션·하단 노트 제목/계정 회피"),
        new(SafePlatform.Pinterest,       "핀터레스트",             "📌", 0.12, 0.74, "하단 핀 제목/설명/저장 버튼·우측 액션 회피"),
        new(SafePlatform.Chzzk,           "치지직",                 "🎮", 0.14, 0.76, "상단 탑바·우측 상호작용·하단 스트리머/클립 제목 회피"),
    };

    // 기존(세이프존 미적용) 위치 상수 — None 폴백 시 바이트 동일 보장.
    private const double DefaultCaptionBottom = 0.13;   // 자막 하단 마진(화면 높이 비율)
    private const double DefaultBadgeTop = 0.10;        // 뱃지 상단 위치(화면 높이 비율)

    /// <summary>UI 셀렉트용 전체 목록(순서 = 표시 순서).</summary>
    public IReadOnlyList<SafeZone> All => Zones;

    /// <summary>플랫폼 세이프존 조회(없으면 None).</summary>
    public SafeZone Get(SafePlatform p) => Zones.FirstOrDefault(z => z.Platform == p) ?? Zones[0];

    /// <summary>자막(하단 앵커) 하단 마진 비율 — 세이프존 하단 경계 위로 끌어올림. None 은 기존 0.13.</summary>
    public static double CaptionBottomFraction(SafePlatform p)
    {
        var z = Zones.FirstOrDefault(x => x.Platform == p) ?? Zones[0];
        return Math.Max(DefaultCaptionBottom, 1.0 - z.BottomPct);
    }

    /// <summary>뱃지(상단 앵커) 상단 위치 비율 — 세이프존 상단 경계 아래로 내림. None 은 기존 0.10.</summary>
    public static double BadgeTopFraction(SafePlatform p)
    {
        var z = Zones.FirstOrDefault(x => x.Platform == p) ?? Zones[0];
        return Math.Max(DefaultBadgeTop, z.TopPct);
    }
}
