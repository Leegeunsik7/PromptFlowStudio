using System.Text;
namespace PromptFlowStudio.Services.OmniChannel;

/// <summary>
/// [PromptFlow Studio v17.51.0] 플랫폼별 메타데이터 변환(ReadOnlySpan&lt;char&gt; → 호출자 Span&lt;char&gt; · 무할당).
/// YouTube Shorts: 제목 ≤ 100 · '&lt;' '&gt;' 제거 · 설명 ≤ 5,000 · #Shorts 필수(제목에 여유 없으면 설명 첫 줄) · 태그 총 ≤ 500 자 · 카테고리 ID 매핑.
/// TikTok v2: 캡션 ≤ 2,200 · 해시태그 ≤ 5(초과분 제거) · 커버 프레임 ms. Instagram: 캡션 ≤ 2,200 · 해시태그 ≤ 30.
/// 길이 절단은 서로게이트 쌍을 깨지 않는다. JSON 문자열은 UTF-8 + 이스케이프로 바이트 버퍼에 직접 기록.
/// </summary>
public static class OmniMetadata
{
    public const int YtTitleMax = 100, YtDescMax = 5000, YtTagsMax = 500, CaptionMax = 2200, TikTokTagMax = 5, IgTagMax = 30;

    /// <summary>v17.50 큐레이션 카테고리(0 엔터 · 1 테크 · 2 라이프 · 3 푸드 · 4 피트니스) → YouTube videoCategoryId.</summary>
    public static int YouTubeCategory(int curationCategory) => curationCategory switch { 0 => 24, 1 => 28, 3 => 26, 4 => 17, _ => 22 };

    private static int SafeCut(ReadOnlySpan<char> s, int max) => s.Length <= max ? s.Length : char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;

    /// <summary>공백 정규화 · 금지 문자 제거 복사(길이 반환).</summary>
    private static int Clean(ReadOnlySpan<char> src, Span<char> dst, bool stripAngle, bool keepNewlines)
    {
        var n = 0; var space = true;
        foreach (var c in src)
        {
            if (stripAngle && (c == '<' || c == '>')) continue;
            if (c == '\n' && keepNewlines) { while (n > 0 && dst[n - 1] == ' ') n--; if (n < dst.Length) dst[n++] = '\n'; space = true; continue; }
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { if (!space && n < dst.Length) { dst[n++] = ' '; space = true; } continue; }
            if (n >= dst.Length) break;
            dst[n++] = c; space = false;
        }
        while (n > 0 && (dst[n - 1] == ' ' || dst[n - 1] == '\n')) n--;
        return n;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<char> s, ReadOnlySpan<char> k) => s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>YouTube 제목 — 100 자 클램프 + " #Shorts" 부착(여유 시). shortsInTitle 반환.</summary>
    public static int YouTubeTitle(ReadOnlySpan<char> src, Span<char> dst, out bool shortsInTitle)
    {
        Span<char> tmp = stackalloc char[YtTitleMax * 2];
        var n = Clean(src[..Math.Min(src.Length, YtTitleMax * 2)], tmp, true, false);
        shortsInTitle = ContainsIgnoreCase(tmp[..n], "#shorts");
        const string tag = " #Shorts";
        if (!shortsInTitle && n + tag.Length <= YtTitleMax) { tag.AsSpan().CopyTo(tmp[n..]); n += tag.Length; shortsInTitle = true; }
        n = SafeCut(tmp[..n], YtTitleMax);
        while (n > 0 && tmp[n - 1] == ' ') n--;
        tmp[..n].CopyTo(dst);
        return n;
    }

    /// <summary>YouTube 설명 — 제목에 #Shorts 가 없으면 첫 줄 "#Shorts" · 5,000 자 클램프.</summary>
    public static int YouTubeDescription(ReadOnlySpan<char> src, bool shortsInTitle, Span<char> dst)
    {
        var n = 0;
        if (!shortsInTitle && !ContainsIgnoreCase(src, "#shorts")) { "#Shorts\n".AsSpan().CopyTo(dst); n = 8; }
        n += Clean(src, dst[n..Math.Min(dst.Length, YtDescMax)], true, true);
        return SafeCut(dst[..n], YtDescMax);
    }

    /// <summary>해시태그 추출('#' 제거 · 쉼표 구분 · 총 500 자) — YouTube tags.</summary>
    public static int YouTubeTags(ReadOnlySpan<char> caption, Span<char> dst, out int count)
    {
        var n = 0; count = 0;
        for (var i = 0; i < caption.Length; i++)
        {
            if (caption[i] != '#') continue;
            var j = i + 1;
            while (j < caption.Length && (char.IsLetterOrDigit(caption[j]) || caption[j] == '_')) j++;
            var t = caption[(i + 1)..j];
            if (t.Length == 0) continue;
            if (n + t.Length + (count > 0 ? 1 : 0) > YtTagsMax) break;
            if (count > 0) dst[n++] = ',';
            t.CopyTo(dst[n..]); n += t.Length; count++;
            i = j - 1;
        }
        return n;
    }

    /// <summary>캡션 — 해시태그 maxTags 초과분 제거 · maxLen 클램프. TikTok(5) · Instagram(30).</summary>
    public static int Caption(ReadOnlySpan<char> src, int maxTags, int maxLen, Span<char> dst, out int tags)
    {
        var n = 0; tags = 0;
        for (var i = 0; i < src.Length && n < dst.Length; i++)
        {
            if (src[i] == '#' && i + 1 < src.Length && (char.IsLetterOrDigit(src[i + 1]) || src[i + 1] == '_'))
            {
                var j = i + 1;
                while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] == '_')) j++;
                if (tags < maxTags) { var t = src[i..j]; if (n + t.Length > dst.Length) break; t.CopyTo(dst[n..]); n += t.Length; tags++; }
                else while (n > 0 && dst[n - 1] == ' ') n--;
                i = j - 1;
                continue;
            }
            dst[n++] = src[i];
        }
        while (n > 0 && (dst[n - 1] == ' ' || dst[n - 1] == '\n')) n--;
        return SafeCut(dst[..n], maxLen);
    }

    public static int CountTags(ReadOnlySpan<char> s)
    {
        var c = 0;
        for (var i = 0; i + 1 < s.Length; i++) if (s[i] == '#' && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] == '_')) c++;
        return c;
    }

    // ══════════════════════ JSON(UTF-8 · 무할당) ══════════════════════
    public static void Raw(Span<byte> d, ref int n, ReadOnlySpan<byte> s) { s.CopyTo(d[n..]); n += s.Length; }
    public static void Num(Span<byte> d, ref int n, long v) { System.Buffers.Text.Utf8Formatter.TryFormat(v, d[n..], out var w); n += w; }

    /// <summary>"…" JSON 문자열(따옴표 포함 · RFC 8259 이스케이프).</summary>
    public static void Str(Span<byte> d, ref int n, ReadOnlySpan<char> s)
    {
        d[n++] = (byte)'"';
        Span<byte> u = stackalloc byte[4];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' || c == '\\') { d[n++] = (byte)'\\'; d[n++] = (byte)c; }
            else if (c == '\n') { d[n++] = (byte)'\\'; d[n++] = (byte)'n'; }
            else if (c == '\r') { d[n++] = (byte)'\\'; d[n++] = (byte)'r'; }
            else if (c == '\t') { d[n++] = (byte)'\\'; d[n++] = (byte)'t'; }
            else if (c < 0x20) { Raw(d, ref n, "\\u00"u8); d[n++] = Hex(c >> 4); d[n++] = Hex(c & 15); }
            else if (c < 0x80) d[n++] = (byte)c;
            else
            {
                var len = char.IsHighSurrogate(c) && i + 1 < s.Length ? 2 : 1;
                var w = Encoding.UTF8.GetBytes(s.Slice(i, len), u);
                u[..w].CopyTo(d[n..]); n += w; i += len - 1;
            }
        }
        d[n++] = (byte)'"';
    }

    private static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'a' + v - 10);
}
