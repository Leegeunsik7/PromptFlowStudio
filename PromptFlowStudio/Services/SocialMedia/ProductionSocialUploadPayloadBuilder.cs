using System.Buffers;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.SocialMedia;

/// <summary>
/// [PromptFlow Studio v18.0.4] 📤 프로덕션 소셜 업로드 페이로드 빌더 — 인계된 fMP4 · 메타데이터를 플랫폼 전송 규격 바이트로 조립(호출자 Span · GC 0 B).
///   ▸ YouTube Data API v3 Resumable: POST 초기화(X-Upload-Content-Type: video/mp4 · X-Upload-Content-Length · application/json snippet/status 바디) ·
///     PUT 청크 헤드(Content-Range) · multipart/related 프리픽스/서픽스(영상 바이트는 사이에 0-카피 스트리밍).
///   ▸ Instagram Graph API Reels: 컨테이너 생성 폼(media_type=REELS · upload_type=resumable | video_url · caption · thumb_offset · share_to_feed) ·
///     rupload 헤드(Authorization: OAuth · offset · file_size).
///   ▸ 모든 Try* 는 길이 측정 패스 → 기록 패스(2 패스) · 버퍼 부족 시 false(부분 기록 무효) · 문자열 결합/보간/LINQ 없음.
///   ▸ 토큰은 호출자 인자(환경변수/DPAPI 볼트) — 하드코딩 없음.
/// </summary>
public static class ProductionSocialUploadPayloadBuilder
{
    public const int YouTubeTitleMax = 100, YouTubeDescriptionMax = 5000, InstagramCaptionMax = 2200, MaxTags = 30;
    public const string YouTubeHost = "www.googleapis.com", YouTubeResumablePath = "/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
    public const string YouTubeMultipartPath = "/upload/youtube/v3/videos?uploadType=multipart&part=snippet,status";
    public const string YouTubeCategoryPeopleBlogs = "22";
    public const string InstagramRuploadHost = "rupload.facebook.com", InstagramRuploadPath = "/ig-api-upload/v21.0/";

    // ══════════════════════ YouTube Resumable ══════════════════════

    /// <summary>snippet/status JSON 바디만(HttpClient StringContent 대체용 · UTF-8).</summary>
    public static bool TryWriteYouTubeMetadataJson(in SocialPublishPayload p, ReadOnlySpan<char> privacy, Span<byte> dst, out int written)
    {
        var w = new Utf8Writer(dst);
        YouTubeJson(ref w, p, privacy);
        return w.Done(out written);
    }

    /// <summary>
    /// 재개 가능 업로드 세션 초기화 요청 전체(요청줄 · 헤더 · JSON 바디). <paramref name="bodyOffset"/> = JSON 시작 위치.
    /// </summary>
    public static bool TryWriteYouTubeResumableInit(in SocialPublishPayload p, ReadOnlySpan<char> privacy, ReadOnlySpan<char> bearerToken, Span<byte> dst, out int written, out int bodyOffset)
    {
        var m = Utf8Writer.Counter();
        YouTubeJson(ref m, p, privacy);
        var bodyLen = m.Pos;

        var w = new Utf8Writer(dst);
        w.Raw("POST "u8); w.Ascii(YouTubeResumablePath); w.Raw(" HTTP/1.1\r\nHost: "u8); w.Ascii(YouTubeHost); w.Crlf();
        if (!bearerToken.IsEmpty) { w.Raw("Authorization: Bearer "u8); w.Ascii(bearerToken); w.Crlf(); }
        w.Raw("Content-Type: application/json; charset=UTF-8\r\n"u8);
        w.Raw("Content-Length: "u8); w.Int(bodyLen); w.Crlf();
        w.Raw("X-Upload-Content-Type: video/mp4\r\n"u8);
        w.Raw("X-Upload-Content-Length: "u8); w.Int(p.Bytes); w.Crlf();
        w.Crlf();
        bodyOffset = w.Pos;
        YouTubeJson(ref w, p, privacy);
        return w.Done(out written);
    }

    /// <summary>세션 URI 로 보내는 청크 PUT 헤드(바디 = 영상 [offset, offset+length) 슬라이스 · 0-카피).</summary>
    public static bool TryWriteYouTubeChunkHead(ReadOnlySpan<char> sessionPathAndQuery, long offset, int length, long total, Span<byte> dst, out int written)
    {
        var w = new Utf8Writer(dst);
        if (offset < 0 || length <= 0 || offset + length > total) { written = 0; return false; }
        w.Raw("PUT "u8); w.Ascii(sessionPathAndQuery); w.Raw(" HTTP/1.1\r\nHost: "u8); w.Ascii(YouTubeHost); w.Crlf();
        w.Raw("Content-Type: video/mp4\r\nContent-Length: "u8); w.Int(length); w.Crlf();
        w.Raw("Content-Range: bytes "u8); w.Int(offset); w.Byte((byte)'-'); w.Int(offset + length - 1); w.Byte((byte)'/'); w.Int(total); w.Crlf();
        w.Crlf();
        return w.Done(out written);
    }

    /// <summary>multipart/related 프리픽스(경계 · JSON 파트 · video/mp4 파트 헤더) — 뒤에 영상 바이트 · <see cref="TryWriteMultipartSuffix"/>.</summary>
    public static bool TryWriteYouTubeMultipartPrefix(in SocialPublishPayload p, ReadOnlySpan<char> privacy, ReadOnlySpan<char> boundary, Span<byte> dst, out int written)
    {
        var w = new Utf8Writer(dst);
        if (!ValidBoundary(boundary)) { written = 0; return false; }
        w.Raw("--"u8); w.Ascii(boundary); w.Crlf();
        w.Raw("Content-Type: application/json; charset=UTF-8\r\n\r\n"u8);
        YouTubeJson(ref w, p, privacy); w.Crlf();
        w.Raw("--"u8); w.Ascii(boundary); w.Crlf();
        w.Raw("Content-Type: video/mp4\r\n\r\n"u8);
        return w.Done(out written);
    }

    public static bool TryWriteMultipartSuffix(ReadOnlySpan<char> boundary, Span<byte> dst, out int written)
    {
        var w = new Utf8Writer(dst);
        if (!ValidBoundary(boundary)) { written = 0; return false; }
        w.Crlf(); w.Raw("--"u8); w.Ascii(boundary); w.Raw("--\r\n"u8);
        return w.Done(out written);
    }

    /// <summary>RFC 2046 경계(1~70 자 · 영숫자 '-_.' 만 허용).</summary>
    public static bool ValidBoundary(ReadOnlySpan<char> b)
    {
        if (b.IsEmpty || b.Length > 70) return false;
        foreach (var c in b) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        return true;
    }

    private static void YouTubeJson(ref Utf8Writer w, in SocialPublishPayload p, ReadOnlySpan<char> privacy)
    {
        w.Raw("{\"snippet\":{\"title\":"u8);
        w.JsonString(Clamp(p.Title, YouTubeTitleMax));
        w.Raw(",\"description\":"u8);
        w.JsonDescription(Clamp(p.Title, YouTubeTitleMax), Clamp(p.Hashtags, YouTubeDescriptionMax - YouTubeTitleMax - 1));
        w.Raw(",\"tags\":["u8);
        var tags = p.Hashtags.AsSpan();
        var n = 0;
        while (n < MaxTags && NextTag(ref tags, out var tag))
        {
            if (n++ > 0) w.Byte((byte)',');
            w.JsonString(tag);
        }
        w.Raw("],\"categoryId\":\""u8); w.Ascii(YouTubeCategoryPeopleBlogs);
        w.Raw("\"},\"status\":{\"privacyStatus\":"u8);
        w.JsonString(privacy is "public" or "unlisted" ? privacy : "private");
        w.Raw(",\"selfDeclaredMadeForKids\":false}}"u8);
    }

    // ══════════════════════ Instagram Graph Reels ══════════════════════

    /// <summary>
    /// POST /{ig-user-id}/media 폼 바디(x-www-form-urlencoded). <paramref name="videoUrl"/> 비면 upload_type=resumable(rupload 직접 전송).
    /// thumb_offset = 클립 시작 기준 썸네일 ms.
    /// </summary>
    public static bool TryWriteInstagramReelsContainerForm(in SocialPublishPayload p, ReadOnlySpan<char> videoUrl, bool shareToFeed, Span<byte> dst, out int written)
    {
        var w = new Utf8Writer(dst);
        w.Raw("media_type=REELS&"u8);
        if (videoUrl.IsEmpty) w.Raw("upload_type=resumable"u8);
        else { w.Raw("video_url="u8); w.Percent(videoUrl); }
        w.Raw("&caption="u8);
        var title = Clamp(p.Title, InstagramCaptionMax);
        w.Percent(title);
        var tags = Clamp(p.Hashtags, InstagramCaptionMax - title.Length - 1);
        if (!tags.IsEmpty) { w.Raw("%0A"u8); w.Percent(tags); }
        w.Raw("&thumb_offset="u8); w.Int(p.Render.ThumbnailOffsetMs);
        w.Raw(shareToFeed ? "&share_to_feed=true"u8 : "&share_to_feed=false"u8);
        return w.Done(out written);
    }

    /// <summary>rupload 영상 전송 헤드(바디 = 영상 전체 · 0-카피).</summary>
    public static bool TryWriteInstagramRuploadHead(ReadOnlySpan<char> containerId, ReadOnlySpan<char> accessToken, long fileSize, Span<byte> dst, out int written)
    {
        var w = new Utf8Writer(dst);
        if (containerId.IsEmpty || fileSize <= 0) { written = 0; return false; }
        foreach (var c in containerId) if (!char.IsAsciiDigit(c)) { written = 0; return false; }
        w.Raw("POST "u8); w.Ascii(InstagramRuploadPath); w.Ascii(containerId); w.Raw(" HTTP/1.1\r\nHost: "u8); w.Ascii(InstagramRuploadHost); w.Crlf();
        if (!accessToken.IsEmpty) { w.Raw("Authorization: OAuth "u8); w.Ascii(accessToken); w.Crlf(); }
        w.Raw("offset: 0\r\nfile_size: "u8); w.Int(fileSize); w.Crlf();
        w.Raw("Content-Type: application/octet-stream\r\nContent-Length: "u8); w.Int(fileSize); w.Crlf();
        w.Crlf();
        return w.Done(out written);
    }

    /// <summary>HttpClient 전송용 rupload 바디(영상 버퍼 공유 · 복사 0) — 비핫패스.</summary>
    public static HttpContent CreateRuploadContent(ReadOnlyMemory<byte> video)
    {
        var c = new ReadOnlyMemoryContent(video);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        c.Headers.TryAddWithoutValidation("offset", "0");
        c.Headers.TryAddWithoutValidation("file_size", video.Length.ToString(CultureInfo.InvariantCulture));
        return c;
    }

    // ══════════════════════ 검증(무할당 파서) ══════════════════════

    /// <summary>헤더 끝(\r\n\r\n 다음) 위치 · 없으면 -1.</summary>
    public static int HeadLength(ReadOnlySpan<byte> request)
    {
        var i = request.IndexOf("\r\n\r\n"u8);
        return i < 0 ? -1 : i + 4;
    }

    /// <summary>헤더 값 조회(이름 대소문자 무시 · 요청줄 제외).</summary>
    public static bool TryGetHeader(ReadOnlySpan<byte> request, ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        var end = HeadLength(request);
        var head = end < 0 ? request : request[..(end - 2)];
        var nl = head.IndexOf("\r\n"u8);
        if (nl < 0) { value = default; return false; }
        head = head[(nl + 2)..];
        while (!head.IsEmpty)
        {
            nl = head.IndexOf("\r\n"u8);
            var line = nl < 0 ? head : head[..nl];
            head = nl < 0 ? default : head[(nl + 2)..];
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !Ascii.EqualsIgnoreCase(line[..colon], name)) continue;
            value = line[(colon + 1)..].Trim((byte)' ');
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryGetHeaderInt64(ReadOnlySpan<byte> request, ReadOnlySpan<byte> name, out long v)
    {
        v = 0;
        return TryGetHeader(request, name, out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
    }

    // ══════════════════════ 내부 ══════════════════════

    private static ReadOnlySpan<char> Clamp(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || max <= 0) return default;
        var span = s.AsSpan();
        if (span.Length <= max) return span;
        span = span[..max];
        return char.IsHighSurrogate(span[^1]) ? span[..^1] : span;
    }

    /// <summary>공백 구분 해시태그 열거('#' 제거 · 빈 항목 건너뜀).</summary>
    private static bool NextTag(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> tag)
    {
        while (!rest.IsEmpty)
        {
            var sp = rest.IndexOf(' ');
            var t = sp < 0 ? rest : rest[..sp];
            rest = sp < 0 ? default : rest[(sp + 1)..];
            t = t.TrimStart('#').Trim();
            if (!t.IsEmpty) { tag = t; return true; }
        }
        tag = default;
        return false;
    }

    /// <summary>Span 순차 기록기 — 빈 버퍼 + 측정 모드면 길이만 센다 · 초과 시 Overflow(이후 기록 생략 · 길이는 계속 집계).</summary>
    private ref struct Utf8Writer
    {
        private readonly Span<byte> _d;
        private readonly bool _measure;
        private bool _overflow;
        public int Pos;

        public Utf8Writer(Span<byte> d) { _d = d; _measure = false; _overflow = false; Pos = 0; }
        private Utf8Writer(bool measure) { _d = default; _measure = measure; _overflow = false; Pos = 0; }
        public static Utf8Writer Counter() => new(true);

        public readonly bool Done(out int written)
        {
            written = _overflow ? 0 : Pos;
            return !_overflow;
        }

        public void Raw(scoped ReadOnlySpan<byte> s)
        {
            if (!_measure && !_overflow)
            {
                if ((uint)(Pos + s.Length) > (uint)_d.Length) _overflow = true;
                else s.CopyTo(_d[Pos..]);
            }
            Pos += s.Length;
        }

        public void Byte(byte b)
        {
            if (!_measure && !_overflow)
            {
                if (Pos >= _d.Length) _overflow = true;
                else _d[Pos] = b;
            }
            Pos++;
        }

        public void Crlf() => Raw("\r\n"u8);

        /// <summary>ASCII 전용(경로 · 토큰 · 호스트) — 비 ASCII 는 '?'.</summary>
        public void Ascii(scoped ReadOnlySpan<char> s)
        {
            foreach (var c in s) Byte(c < 0x80 ? (byte)c : (byte)'?');
        }

        public void Int(long v)
        {
            Span<byte> tmp = stackalloc byte[20];
            v.TryFormat(tmp, out var n, default, CultureInfo.InvariantCulture);
            Raw(tmp[..n]);
        }

        public void JsonString(scoped ReadOnlySpan<char> s)
        {
            Byte((byte)'"');
            JsonBody(s);
            Byte((byte)'"');
        }

        /// <summary>"제목\n해시태그" 를 하나의 JSON 문자열로(중간 결합 문자열 없음).</summary>
        public void JsonDescription(scoped ReadOnlySpan<char> title, scoped ReadOnlySpan<char> tags)
        {
            Byte((byte)'"');
            JsonBody(title);
            if (!tags.IsEmpty) { Raw("\\n"u8); JsonBody(tags); }
            Byte((byte)'"');
        }

        private void JsonBody(scoped ReadOnlySpan<char> s)
        {
            Span<byte> u = stackalloc byte[4];
            ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
            var i = 0;
            while (i < s.Length)
            {
                if (Rune.DecodeFromUtf16(s[i..], out var r, out var used) != OperationStatus.Done) r = Rune.ReplacementChar;
                i += used > 0 ? used : 1;
                switch (r.Value)
                {
                    case '"': Raw("\\\""u8); continue;
                    case '\\': Raw("\\\\"u8); continue;
                    case '\n': Raw("\\n"u8); continue;
                    case '\r': Raw("\\r"u8); continue;
                    case '\t': Raw("\\t"u8); continue;
                }
                if (r.Value < 0x20)
                {
                    Raw("\\u00"u8); Byte(hex[r.Value >> 4]); Byte(hex[r.Value & 0xF]);
                    continue;
                }
                Raw(u[..r.EncodeToUtf8(u)]);
            }
        }

        /// <summary>application/x-www-form-urlencoded(UTF-8 · 비예약 문자 외 %XX 대문자).</summary>
        public void Percent(scoped ReadOnlySpan<char> s)
        {
            Span<byte> u = stackalloc byte[4];
            ReadOnlySpan<byte> hex = "0123456789ABCDEF"u8;
            var i = 0;
            while (i < s.Length)
            {
                if (Rune.DecodeFromUtf16(s[i..], out var r, out var used) != OperationStatus.Done) r = Rune.ReplacementChar;
                i += used > 0 ? used : 1;
                var n = r.EncodeToUtf8(u);
                for (var k = 0; k < n; k++)
                {
                    var b = u[k];
                    if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~') Byte(b);
                    else { Byte((byte)'%'); Byte(hex[b >> 4]); Byte(hex[b & 0xF]); }
                }
            }
        }
    }
}
