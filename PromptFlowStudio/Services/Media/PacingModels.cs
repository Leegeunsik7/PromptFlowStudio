using System;
using System.Linq;

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// 감정선 기반 동적 BGM 오케스트레이션 플랜 — <see cref="VideoMergeService"/>가 BGM 볼륨 엔벨로프/덕킹 깊이/오디오 페이드에 반영.
///   ▸ <see cref="Tensions"/>: 컷별 긴장도(0~100). 높을수록 BGM 이 고조(볼륨↑)되고 덕킹은 약해진다(BGM 존재감↑).
///   ▸ <see cref="DuckingDepth"/>: 0~1. 나레이션 구간 BGM 감쇄 강도(1=강하게 눌림, 0=거의 안 눌림).
///   ▸ <see cref="FadeInMs"/>/<see cref="FadeOutMs"/>: 최종 오디오 페이드 인/아웃 길이(감정 아크 기반).
/// </summary>
public sealed record EmotionPacing(System.Collections.Generic.IReadOnlyList<int> Tensions, double DuckingDepth, int FadeInMs, int FadeOutMs);

/// <summary>컷 경계 스마트 트랜지션 1개 — 검증된 ffmpeg <c>xfade</c> 전환명 + 지속시간(ms).</summary>
public sealed record VideoTransition(string XfadeName, int DurationMs);

/// <summary>
/// [PromptFlowStudio] ffmpeg <c>xfade</c> 전환명 안전 검증 — 순수 BCL, 화이트리스트만 통과(주입 차단).
///   ⚠ 트랜지션명도 Gemini(비신뢰) 산출이라 filter_complex 에 넣기 전 고정 화이트리스트로 강제 매핑한다(밖이면 fade 폴백).
/// </summary>
public static class TransitionSafety
{
    /// <summary>허용 xfade 전환명(ffmpeg 표준). 밖의 값은 fade 로 폴백.</summary>
    private static readonly string[] Allowed =
    {
        "fade", "fadeblack", "fadewhite", "dissolve", "distance",
        "wipeleft", "wiperight", "wipeup", "wipedown",
        "slideleft", "slideright", "slideup", "slidedown",
        "smoothleft", "smoothright", "smoothup", "smoothdown",
        "circleopen", "circleclose", "circlecrop", "rectcrop",
        "zoomin", "hblur", "radial", "pixelize",
        "diagtl", "diagtr", "diagbl", "diagbr",
    };

    /// <summary>요구사항 키워드(fade/whip-pan/zoom-in/blur 등)를 표준 xfade 전환명으로 매핑. 미상은 fade.</summary>
    public static string MapKind(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return k switch
        {
            "fade" or "crossfade" or "dissolve" => "fade",
            "fadeblack" or "dipToBlack" or "diptoblack" => "fadeblack",
            "fadewhite" => "fadewhite",
            "whip" or "whippan" or "pan" or "whippanleft" => "slideleft",
            "whippanright" => "slideright",
            "slide" or "slideleft" => "slideleft",
            "slideright" => "slideright",
            "zoom" or "zoomin" => "zoomin",
            "blur" or "hblur" => "hblur",
            "wipe" or "wipeleft" => "wipeleft",
            "wiperight" => "wiperight",
            "circle" or "circleopen" => "circleopen",
            "radial" => "radial",
            "pixelize" or "pixel" => "pixelize",
            _ => SafeXfade(k),
        };
    }

    /// <summary>이름을 화이트리스트로 강제 — 목록 밖이면 fade.</summary>
    public static string SafeXfade(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return Allowed.Contains(n) ? n : "fade";
    }
}
