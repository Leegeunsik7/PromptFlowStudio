using System.Buffers.Text;
using System.Text.Json;
namespace PromptFlowStudio.Services.GraphBridge;

public enum GraphMediaKind : byte { Unknown, Video, Image, Carousel }
public enum GraphWebhookField : byte { Other, Comments, Mentions, StoryInsights, LiveComments }
public enum GraphQueryKind : byte { HashtagSearch, TopMedia, OwnMedia }

/// <summary>미디어 링 슬롯(값 형식) — 문자열은 엔진 소유 고정 바이트 영역에 보관.</summary>
public struct GraphMediaSlot
{
    public ulong Id;
    public GraphMediaKind Kind;
    public bool IsReel, Own;
    public long Likes, Comments, Views, TimestampUnix;
    public int PermalinkLen, MediaUrlLen;
    public long Seq;
}

/// <summary>미디어 인사이트(v17.50 피드백) — 값 형식 · 미수신 −1.</summary>
public struct GraphMediaInsights
{
    public ulong MediaId;
    public long Views, Reach, Saved, Comments, Shares, AvgWatchMs, Interactions;
    public void Clear(ulong id) { MediaId = id; Views = Reach = Saved = Comments = Shares = AvgWatchMs = Interactions = -1; }
}

/// <summary>웹훅 이벤트(값 형식).</summary>
public struct GraphWebhookEvent
{
    public GraphWebhookField Field;
    public ulong EntryId, MediaId, ItemId;
    public long Time;
    public bool SignatureOk;
}

/// <summary>
/// [PromptFlow Studio v17.48.0] 🔗 실시간 Meta Graph API 트렌딩 미디어 인제스천 엔진(URL 조립 · <see cref="ParseMedia"/> · <see cref="IngestWebhook"/> 무할당).
/// Instagram Graph API v21.0 — <c>/ig_hashtag_search</c>(해시태그 ID) → <c>/{hashtag-id}/top_media</c>(공개 인기 미디어: 좋아요 · 댓글 · 퍼머링크 · 미디어 URL),
/// <c>/{ig-user-id}/media</c>(본인 미디어 + <c>insights.metric(views)</c> 조회수). 응답은 <see cref="Utf8JsonReader"/> 로 깊이 추적 스트리밍 파싱해
/// 64 슬롯 링(같은 ID 는 갱신)에 적재하고 <c>paging.cursors.after</c> 를 보존한다. 문자열(<c>\/</c> 이스케이프 포함)은 CopyString 으로 슬롯 영역에 직접 복사.
/// 웹훅(<c>entry[].changes[]</c> field/value)은 X-Hub-Signature-256 검증 통과분만 64 이벤트 링에 기록한다.
/// </summary>
public sealed class RealtimeMetaGraphApiIngestionEngine
{
    public const int Cap = 64, EventCap = 64, PermalinkCap = 256, MediaUrlCap = 1024, CursorCap = 256, TagCap = 64;
    public const string ApiVersion = "v21.0";
    public static ReadOnlySpan<byte> GraphHost => "https://graph.facebook.com/v21.0/"u8;
    public static ReadOnlySpan<byte> MediaFields => "id,media_type,media_product_type,like_count,comments_count,permalink,media_url,timestamp"u8;
    private readonly GraphMediaSlot[] _media = new GraphMediaSlot[Cap];
    private readonly byte[] _permalink = new byte[Cap * PermalinkCap], _mediaUrl = new byte[Cap * MediaUrlCap], _cursor = new byte[CursorCap], _tag = new byte[TagCap];
    private readonly GraphWebhookEvent[] _events = new GraphWebhookEvent[EventCap];
    private int _head, _count, _evHead, _evCount, _cursorLen, _tagLen;
    private long _seq;

    public RealtimeMetaGraphApiIngestionEngine() => SetHashtag("reels"u8);

    public ulong HashtagId { get; set; }
    public int Count => _count;
    public long Parsed { get; private set; }
    public long Updated { get; private set; }
    public long ParseErrors { get; private set; }
    public long LastErrorCode { get; private set; }
    public long WebhookEvents { get; private set; }
    public long WebhookRejected { get; private set; }
    public double LastParseUs { get; private set; }
    public int EventCount => _evCount;
    public ReadOnlySpan<byte> Cursor => _cursor.AsSpan(0, _cursorLen);
    public ReadOnlySpan<byte> Hashtag => _tag.AsSpan(0, _tagLen);
    /// <summary>i = 0 이 최근 적재.</summary>
    public ref readonly GraphMediaSlot Media(int i) => ref _media[SlotIx(i)];
    public ReadOnlySpan<byte> Permalink(int i) { var s = SlotIx(i); return _permalink.AsSpan(s * PermalinkCap, _media[s].PermalinkLen); }
    public ReadOnlySpan<byte> MediaUrl(int i) { var s = SlotIx(i); return _mediaUrl.AsSpan(s * MediaUrlCap, _media[s].MediaUrlLen); }
    private int SlotIx(int i) => (_head - 1 - i + 2 * Cap) % Cap;
    public ref readonly GraphWebhookEvent Event(int i) => ref _events[(_evHead - 1 - i + 2 * EventCap) % EventCap];

    /// <summary>해시태그 설정(영숫자/밑줄만 · 최대 64 바이트) — ID 캐시 초기화.</summary>
    public bool SetHashtag(ReadOnlySpan<byte> tag)
    {
        if (tag.IsEmpty || tag.Length > TagCap) return false;
        foreach (var c in tag) if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' || c >= 0x80)) return false;
        tag.CopyTo(_tag); _tagLen = tag.Length; HashtagId = 0;
        return true;
    }

    // ══════════════════════ URL 조립(무할당) ══════════════════════
    public int BuildUrl(GraphQueryKind q, ulong igUserId, Span<byte> dst)
    {
        var n = 0;
        GraphBytes.Put(dst, ref n, GraphHost);
        switch (q)
        {
            case GraphQueryKind.HashtagSearch:
                GraphBytes.Put(dst, ref n, "ig_hashtag_search?user_id="u8); GraphBytes.Put(dst, ref n, igUserId);
                GraphBytes.Put(dst, ref n, "&q="u8); GraphBytes.PutEncoded(dst, ref n, Hashtag);
                break;
            case GraphQueryKind.TopMedia:
                GraphBytes.Put(dst, ref n, HashtagId); GraphBytes.Put(dst, ref n, "/top_media?user_id="u8); GraphBytes.Put(dst, ref n, igUserId);
                GraphBytes.Put(dst, ref n, "&fields="u8); GraphBytes.Put(dst, ref n, MediaFields); GraphBytes.Put(dst, ref n, "&limit=25"u8);
                break;
            default:
                GraphBytes.Put(dst, ref n, igUserId); GraphBytes.Put(dst, ref n, "/media?fields="u8); GraphBytes.Put(dst, ref n, MediaFields);
                GraphBytes.Put(dst, ref n, ",insights.metric(views)&limit=25"u8);
                if (_cursorLen > 0) { GraphBytes.Put(dst, ref n, "&after="u8); GraphBytes.PutEncoded(dst, ref n, Cursor); }
                break;
        }
        return n;
    }

    /// <summary>해시태그 검색 응답 {"data":[{"id":"..."}]} → <see cref="HashtagId"/>.</summary>
    public bool ParseHashtagSearch(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 3 && r.ValueTextEquals("id"u8) && r.Read() && Utf8Parser.TryParse(r.ValueSpan, out ulong id, out _))
                {
                    HashtagId = id;
                    return true;
                }
        }
        catch (JsonException) { }
        ParseErrors++;
        return false;
    }

    // ══════════════════════ 미디어 파싱(무할당) ══════════════════════
    /// <summary>미디어 목록 응답 스트리밍 파싱 → 링 적재 · 커서 보존. 오류 객체면 코드 기록 후 −1, 아니면 적재 수.</summary>
    public int ParseMedia(ReadOnlySpan<byte> json, bool own)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var added = 0;
        GraphMediaSlot cur = default;
        Span<byte> link = stackalloc byte[PermalinkCap];
        Span<byte> url = stackalloc byte[MediaUrlCap];
        var inItem = false; var inPaging = false; var inData = false; var viewsName = false;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                var d = r.CurrentDepth;
                switch (r.TokenType)
                {
                    case JsonTokenType.StartObject when d == 2 && inData:
                        cur = default; cur.Own = own; inItem = true; viewsName = false;
                        break;
                    case JsonTokenType.EndObject when d == 2 && inItem:
                        inItem = false;
                        if (cur.Id != 0) { Commit(ref cur, link[..cur.PermalinkLen], url[..cur.MediaUrlLen]); added++; }
                        break;
                    case JsonTokenType.PropertyName when d == 1:
                        inPaging = r.ValueTextEquals("paging"u8); inData = r.ValueTextEquals("data"u8);
                        if (r.ValueTextEquals("error"u8))
                        {
                            TokenBucketRateLimitOAuthSecurityGuard.TryReadError(json, out var code, out _);
                            LastErrorCode = code; ParseErrors++;
                            return -1;
                        }
                        break;
                    case JsonTokenType.PropertyName when inPaging && r.ValueTextEquals("after"u8):
                        if (r.Read() && r.TokenType == JsonTokenType.String && !r.ValueIsEscaped && r.ValueSpan.Length <= CursorCap) { r.ValueSpan.CopyTo(_cursor); _cursorLen = r.ValueSpan.Length; }
                        break;
                    case JsonTokenType.PropertyName when inItem && d == 3:
                        ReadItemField(ref r, ref cur, link, url);
                        break;
                    case JsonTokenType.PropertyName when inItem && d == 6 && r.ValueTextEquals("name"u8):
                        viewsName = r.Read() && (r.ValueTextEquals("views"u8) || r.ValueTextEquals("plays"u8));
                        break;
                    case JsonTokenType.PropertyName when inItem && d == 8 && viewsName && r.ValueTextEquals("value"u8):
                        if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt64(out cur.Views);
                        break;
                }
            }
        }
        catch (JsonException) { ParseErrors++; return -1; }
        LastParseUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return added;
    }

    private static void ReadItemField(ref Utf8JsonReader r, ref GraphMediaSlot cur, scoped Span<byte> link, scoped Span<byte> url)
    {
        if (r.ValueTextEquals("id"u8)) { if (r.Read() && Utf8Parser.TryParse(r.ValueSpan, out ulong id, out _)) cur.Id = id; }
        else if (r.ValueTextEquals("media_type"u8))
        {
            r.Read();
            cur.Kind = r.ValueTextEquals("VIDEO"u8) ? GraphMediaKind.Video : r.ValueTextEquals("IMAGE"u8) ? GraphMediaKind.Image : r.ValueTextEquals("CAROUSEL_ALBUM"u8) ? GraphMediaKind.Carousel : GraphMediaKind.Unknown;
        }
        else if (r.ValueTextEquals("media_product_type"u8)) { r.Read(); cur.IsReel = r.ValueTextEquals("REELS"u8); }
        else if (r.ValueTextEquals("like_count"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt64(out cur.Likes); }
        else if (r.ValueTextEquals("comments_count"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt64(out cur.Comments); }
        else if (r.ValueTextEquals("permalink"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String) cur.PermalinkLen = CopyStr(ref r, link); }
        else if (r.ValueTextEquals("media_url"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String) cur.MediaUrlLen = CopyStr(ref r, url); }
        else if (r.ValueTextEquals("timestamp"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String) cur.TimestampUnix = ParseTimestamp(r.ValueSpan); }
    }

    private static int CopyStr(ref Utf8JsonReader r, scoped Span<byte> dst)
    {
        var raw = r.HasValueSequence ? (int)r.ValueSequence.Length : r.ValueSpan.Length;
        return raw <= dst.Length ? r.CopyString(dst) : 0;
    }

    /// <summary><c>2026-10-19T03:00:00+0000</c> → 유닉스 초(무할당 · 실패 0).</summary>
    public static long ParseTimestamp(ReadOnlySpan<byte> s)
    {
        if (s.Length < 19 || s[4] != '-' || s[7] != '-' || s[10] != 'T') return 0;
        if (!D(s, 0, 4, out var y) || !D(s, 5, 2, out var mo) || !D(s, 8, 2, out var da) || !D(s, 11, 2, out var h) || !D(s, 14, 2, out var mi) || !D(s, 17, 2, out var se)) return 0;
        if (y < 1 || mo is < 1 or > 12 || da < 1 || da > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || se > 59) return 0;
        var off = 0;
        if (s.Length >= 24 && (s[19] == '+' || s[19] == '-') && D(s, 20, 2, out var oh) && D(s, s[22] == ':' ? 23 : 22, 2, out var om)) off = (s[19] == '-' ? -1 : 1) * (oh * 3600 + om * 60);
        return new DateTimeOffset(y, mo, da, h, mi, se, TimeSpan.Zero).ToUnixTimeSeconds() - off;
    }

    private static bool D(ReadOnlySpan<byte> s, int at, int len, out int v)
    {
        v = 0;
        if (at + len > s.Length) return false;
        for (var i = at; i < at + len; i++) { var c = s[i] - '0'; if ((uint)c > 9) return false; v = v * 10 + c; }
        return true;
    }

    private void Commit(ref GraphMediaSlot m, ReadOnlySpan<byte> link, ReadOnlySpan<byte> url)
    {
        var slot = -1;
        for (var i = 0; i < _count; i++) if (_media[i].Id == m.Id) { slot = i; break; }
        if (slot >= 0)
        {
            if (m.Views == 0) m.Views = _media[slot].Views;
            m.Own |= _media[slot].Own;
            Updated++;
        }
        else
        {
            slot = _head;
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
        }
        m.Seq = ++_seq;
        link.CopyTo(_permalink.AsSpan(slot * PermalinkCap)); url.CopyTo(_mediaUrl.AsSpan(slot * MediaUrlCap));
        _media[slot] = m;
        Parsed++;
    }

    /// <summary>링에서 참여도(조회 + 좋아요×10 + 댓글×30) 최댓값 슬롯 인덱스(i 기준) — 없으면 −1.</summary>
    public int TopIndex(bool reelsOnly)
    {
        var best = -1; long bs = -1;
        for (var i = 0; i < _count; i++)
        {
            ref readonly var m = ref Media(i);
            if (reelsOnly && !m.IsReel) continue;
            var s = m.Views + 10 * m.Likes + 30 * m.Comments;
            if (s > bs) { bs = s; best = i; }
        }
        return best;
    }

    public static long Engagement(in GraphMediaSlot m) => m.Views + 10 * m.Likes + 30 * m.Comments;

    // ══════════════════════ 웹훅(무할당) ══════════════════════
    /// <summary>웹훅 구독 검증 핸드셰이크 — mode=subscribe · 검증 토큰 일치 시 challenge 반환 대상.</summary>
    public static bool VerifySubscription(ReadOnlySpan<byte> mode, ReadOnlySpan<byte> verifyToken, ReadOnlySpan<byte> expected)
        => mode.SequenceEqual("subscribe"u8) && expected.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(verifyToken, expected);

    /// <summary>서명 검증 후 <c>entry[].changes[]</c> 를 이벤트 링에 적재 — 적재 수(서명 실패 −1).</summary>
    public int IngestWebhook(ReadOnlySpan<byte> body, ReadOnlySpan<byte> signature, TokenBucketRateLimitOAuthSecurityGuard guard)
    {
        if (!guard.VerifySignature(body, signature)) { WebhookRejected++; return -1; }
        var added = 0;
        GraphWebhookEvent ev = default;
        ulong entryId = 0; long time = 0;
        var inMedia = false;
        try
        {
            var r = new Utf8JsonReader(body);
            while (r.Read())
            {
                var d = r.CurrentDepth;
                if (r.TokenType == JsonTokenType.StartObject && d == 4) { ev = default; inMedia = false; continue; }
                if (r.TokenType == JsonTokenType.EndObject && d == 4)
                {
                    ev.EntryId = entryId; ev.Time = time; ev.SignatureOk = true;
                    _events[_evHead] = ev; _evHead = (_evHead + 1) % EventCap;
                    if (_evCount < EventCap) _evCount++;
                    WebhookEvents++; added++;
                    continue;
                }
                if (r.TokenType == JsonTokenType.EndObject && d == 6) { inMedia = false; continue; }
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (d == 3)
                {
                    if (r.ValueTextEquals("id"u8)) { r.Read(); Utf8Parser.TryParse(r.ValueSpan, out entryId, out _); }
                    else if (r.ValueTextEquals("time"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt64(out time); }
                }
                else if (d == 5 && r.ValueTextEquals("field"u8))
                {
                    r.Read();
                    ev.Field = r.ValueTextEquals("comments"u8) ? GraphWebhookField.Comments : r.ValueTextEquals("mentions"u8) ? GraphWebhookField.Mentions
                        : r.ValueTextEquals("story_insights"u8) ? GraphWebhookField.StoryInsights : r.ValueTextEquals("live_comments"u8) ? GraphWebhookField.LiveComments : GraphWebhookField.Other;
                }
                else if (d == 6)
                {
                    if (r.ValueTextEquals("id"u8) || r.ValueTextEquals("comment_id"u8)) { r.Read(); Utf8Parser.TryParse(r.ValueSpan, out ev.ItemId, out _); }
                    else if (r.ValueTextEquals("media_id"u8)) { r.Read(); Utf8Parser.TryParse(r.ValueSpan, out ev.MediaId, out _); }
                    else if (r.ValueTextEquals("media"u8)) inMedia = true;
                }
                else if (d == 7 && inMedia && r.ValueTextEquals("id"u8)) { r.Read(); Utf8Parser.TryParse(r.ValueSpan, out ev.MediaId, out _); }
            }
        }
        catch (JsonException) { ParseErrors++; }
        return added;
    }

    // ══════════════════════ 인사이트(v17.50 · 무할당) ══════════════════════
    public static ReadOnlySpan<byte> InsightMetrics => "views,reach,saved,comments,shares,total_interactions,ig_reels_avg_watch_time"u8;

    public static int BuildInsightsUrl(ulong mediaId, Span<byte> dst)
    {
        var n = 0;
        GraphBytes.Put(dst, ref n, GraphHost); GraphBytes.Put(dst, ref n, mediaId); GraphBytes.Put(dst, ref n, "/insights?metric="u8); GraphBytes.Put(dst, ref n, InsightMetrics);
        return n;
    }

    /// <summary>{"data":[{"name":"views","values":[{"value":n}]}…]} 판독 — 성공 시 true.</summary>
    public static bool ParseInsights(ReadOnlySpan<byte> json, ulong mediaId, out GraphMediaInsights m)
    {
        m = default; m.Clear(mediaId);
        var metric = 0; var any = false;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.CurrentDepth == 3 && r.ValueTextEquals("name"u8) && r.Read())
                    metric = r.ValueTextEquals("views"u8) ? 1 : r.ValueTextEquals("reach"u8) ? 2 : r.ValueTextEquals("saved"u8) ? 3 : r.ValueTextEquals("comments"u8) ? 4
                        : r.ValueTextEquals("shares"u8) ? 5 : r.ValueTextEquals("ig_reels_avg_watch_time"u8) ? 6 : r.ValueTextEquals("total_interactions"u8) ? 7 : 0;
                else if (r.CurrentDepth == 5 && r.ValueTextEquals("value"u8) && r.Read() && r.TokenType == JsonTokenType.Number && r.TryGetInt64(out var v))
                {
                    any = true;
                    switch (metric) { case 1: m.Views = v; break; case 2: m.Reach = v; break; case 3: m.Saved = v; break; case 4: m.Comments = v; break; case 5: m.Shares = v; break; case 6: m.AvgWatchMs = v; break; case 7: m.Interactions = v; break; }
                }
            }
        }
        catch (JsonException) { return false; }
        return any;
    }

    public static string FieldName(GraphWebhookField f) => f switch
    {
        GraphWebhookField.Comments => "comments",
        GraphWebhookField.Mentions => "mentions",
        GraphWebhookField.StoryInsights => "story_insights",
        GraphWebhookField.LiveComments => "live_comments",
        _ => "other",
    };
}
