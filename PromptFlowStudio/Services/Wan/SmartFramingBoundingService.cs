using System;
using PromptFlowStudio.Services.Media;   // SafePlatform

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] 피사체 세이프존 자동 바운딩(프롬프트 프레이밍 보정) — 순수 BCL(외부 NuGet 0 · 인메모리).
///   ▸ Wan 2.1 프롬프트에 <b>아기 얼굴을 숏츠 안전대(상단 여백~하단 자막 사이 상단-중앙)</b>에 배치하는 앵글/구도 키워드를 동적 주입.
///   ▸ 플랫폼별 얼굴 안전대(<see cref="FaceBand"/>): 숏츠 20~65% · 릴스 25~62% · 틱톡 20~60%(하단 UI 클수록 얼굴을 위로).
///   ▸ <b>멱등</b>(<see cref="Marker"/> 있으면 재주입 안 함) · enabled=false 면 원본 유지 · 정적 순수 함수(호출부 DI 불필요).
/// </summary>
public sealed class SmartFramingBoundingService
{
    private const string Marker = "upper-middle safe band";   // 멱등 판별 마커

    /// <summary>플랫폼별 얼굴 안전대(화면 높이 상단%~중단%). 하단 UI 가 클수록 얼굴을 더 위로.</summary>
    public static (int TopPct, int MidPct) FaceBand(SafePlatform platform) => platform switch
    {
        SafePlatform.YouTubeShorts  => (20, 65),
        SafePlatform.InstagramReels => (25, 62),
        SafePlatform.TikTok         => (20, 60),
        _                           => (20, 65),
    };

    /// <summary>안전대 프레이밍 영문 키워드 절.</summary>
    public static string FramingClause(SafePlatform platform)
    {
        var (top, mid) = FaceBand(platform);
        return $"macro center framing, subject's face and eyes positioned in the upper-middle safe band (around {top}%~{mid}% of frame height), head kept clear of the top margin and the lower caption/UI zone, balanced vertical 9:16 composition";
    }

    /// <summary>프롬프트에 프레이밍 절을 주입한다(멱등). enabled=false 면 원본. 이미 주입돼 있으면 그대로.</summary>
    public static string ApplyFraming(string? prompt, SafePlatform platform, bool enabled)
    {
        var p = (prompt ?? "").Trim();
        if (!enabled) return p;
        if (p.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return p;   // 이미 바운딩됨
        var clause = FramingClause(platform);
        return p.Length == 0 ? clause : p.TrimEnd(',', ' ') + ", " + clause;
    }

    /// <summary>UI 안내용 한국어 설명.</summary>
    public string Describe(SafePlatform platform)
    {
        var (top, mid) = FaceBand(platform);
        return $"아기 얼굴을 화면 {top}%~{mid}% 안전대에 배치 (상단 여백·하단 자막 가림 회피)";
    }
}
