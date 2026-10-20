using System;
using System.Linq;

namespace PromptFlowStudio.Services.Media;

/// <summary>키네틱 하이라이트 자막의 단어 1개 — 컷-로컬 밀리초 타임코드. Highlight=true 면 강조색으로 표시.</summary>
public sealed record KineticCaptionWord(string Text, int StartMs, int EndMs, bool Highlight);

/// <summary>정밀 타임코드 SFX 트리거 1개 — 컷-로컬 밀리초 시점에 지정 효과음(whoosh/pop 등) 재생.</summary>
public sealed record KineticSfxCue(string SfxType, int TimestampMs);

/// <summary>생성형 폴리 큐 1개 — 해결된 효과음 파일 + 컷-로컬 밀리초 시점 + 3D 공간 팬(-1 좌 ~ 0 중앙 ~ +1 우).</summary>
public sealed record FoleyCue(string AudioPath, int TimestampMs, double Pan);

/// <summary>authored 폴리 스펙(프리셋) — 효과음 <b>타입</b>(파일 미해결) + 컷-로컬 밀리초 시점 + 팬. 병합 시 <see cref="SfxResolver"/>로 파일 해결 → <see cref="FoleyCue"/>.</summary>
public sealed record FoleySpec(string SfxType, int TimestampMs, double Pan);

/// <summary>
/// 음소거 시청 대응 시각 효과음 뱃지 — 소리가 꺼진 상태에서도 효과음 순간을 보여주는 이모지 결합형 화면 뱃지(예: 에취! 🤧).
///   <see cref="Label"/>(=Text+Emoji 결합)를 컷-로컬 [StartMs,EndMs]에 화면 상단 중앙에 팝업 drawtext 로 번인한다.
/// </summary>
public sealed record VisualSfxBadge(string Text, string Emoji, int StartMs, int EndMs)
{
    /// <summary>번인/표시용 결합 라벨(예: "에취! 🤧"). 이모지 없으면 텍스트만.</summary>
    public string Label => string.IsNullOrEmpty(Emoji) ? Text : (Text + " " + Emoji);
}

/// <summary>
/// [PromptFlowStudio] FFmpeg 컬러 그레이딩 필터 문자열 안전 검증(주입 차단) — 순수 BCL, 로캘 무관 문자 검사.
///   ⚠ 컬러 그레이딩 필터는 <b>Gemini(원격·비신뢰) 출력</b>이라 filter_complex 인자에 그대로 넣으면 안 된다.
///     필터그래프 구분자(<c>;</c>)·라벨(<c>[..]</c>)·따옴표·역슬래시·외부리소스 로더(movie/file 등)를 차단하고,
///     콤마로 나눈 각 필터의 이름이 <see cref="Whitelist"/>(순수 색보정 필터)에 있을 때만 통과시킨다.
///   ▸ VideoMergeService 와 GeminiColorGradingService 양쪽이 이 가드를 호출(방어 심층화). 위험하면 null 반환 → 그레이딩 생략(Graceful).
/// </summary>
public static class FfmpegFilterSafety
{
    /// <summary>허용 색보정/후처리 필터 이름(외부 리소스·오디오·오버레이 계열 제외).</summary>
    private static readonly string[] Whitelist =
    {
        "eq", "colorbalance", "colorchannelmixer", "hue", "curves", "colortemperature",
        "vibrance", "vignette", "unsharp", "gblur", "boxblur", "colorlevels",
        "colorcontrast", "exposure", "monochrome", "hqdn3d", "noise", "fade", "format",
    };

    // 명백히 위험한 토큰(외부 파일/네트워크/다른 필터 카테고리 로드).
    private static readonly string[] Forbidden =
    {
        "movie", "amovie", "filename", "file=", "http", "://", "load", "concat",
        "subtitles", "ass=", "drawtext", "overlay", "sendcmd", "\\",
    };

    // 허용 문자(필터 파라미터 표현식): 영숫자 + 수식/구분 기호.
    private const string AllowedPunct = "=:.,/*-+() @|_";

    /// <summary>
    /// 컬러 그레이딩 필터 체인을 검증한다. 안전하면 정규화된 문자열, 위험하면 null.
    ///   빈 입력·과길이·금지문자·금지토큰·화이트리스트 밖 필터는 모두 null(Graceful).
    /// </summary>
    public static string? SanitizeColorGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length > 400) return null;

        // 필터그래프 탈출/따옴표/제어문자 차단.
        foreach (var ch in s)
        {
            if (ch is '[' or ']' or ';' or '\'' or '"' or '`' or '#' or '$' or '!' or '{' or '}' or '<' or '>') return null;
            if (ch < 0x20) return null;   // 개행/제어문자
            if (!(char.IsLetterOrDigit(ch) || AllowedPunct.IndexOf(ch) >= 0)) return null;
        }

        var lower = s.ToLowerInvariant();
        foreach (var bad in Forbidden)
            if (lower.Contains(bad, StringComparison.Ordinal)) return null;

        // 콤마로 나눈 각 필터의 이름이 화이트리스트에 있어야 한다.
        foreach (var seg in s.Split(','))
        {
            var t = seg.Trim();
            if (t.Length == 0) return null;   // 빈 세그먼트(콤마 오남용)
            var eq = t.IndexOf('=');
            var name = (eq >= 0 ? t[..eq] : t).Trim().ToLowerInvariant();
            if (!Whitelist.Contains(name)) return null;
        }
        return s;
    }
}
