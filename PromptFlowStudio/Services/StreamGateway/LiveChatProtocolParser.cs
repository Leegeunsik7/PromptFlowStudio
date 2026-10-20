using System.Buffers.Text;
using System.Text.Json;
namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>v17.62.0 IRC 줄 종류.</summary>
public enum IrcLineKind : byte { Other, Ping, Pong, PrivMsg, Reconnect, Welcome, Notice }

/// <summary>v17.62.0 게이트웨이 단조 시계(ms) — 배압 큐 스탬프 · 지연 폐기 · 핑.</summary>
public static class GatewayClock
{
    public static long NowMs => Environment.TickCount64;
}

/// <summary>
/// [PromptFlow Studio v17.62.0] 🧩 Twitch IRCv3 · YouTube liveChatMessages · 치지직 WebSocket 페이로드 무할당 파서(세션 스레드 전용 인스턴스).
///   ▸ Twitch: <c>@tags :nick!u@h PRIVMSG #ch :본문</c> — display-name(\s 해제) · bits(×12 원) · badges/mod(모더레이터 · 방송인 · 구독자) · PING/PONG/RECONNECT/001.
///   ▸ YouTube: <see cref="Utf8JsonReader"/> 로 items[].snippet(displayMessage · superChatDetails/superStickerDetails amountMicros · currency) · authorDetails(displayName · isChatModerator · isChatOwner) · nextPageToken · pollingIntervalMillis.
///   ▸ 치지직: cmd 0(서버 핑) · 10000(퐁) · 93101(채팅) · 93102(후원) — bdy[].msg · profile(JSON 문자열 → 중첩 리더 nickname · userRoleCode) · extras(payAmount).
///   ▸ 모든 문자열은 <c>CopyString</c>/UTF-8 스팬 디코드로 고정 버퍼에 해제 → <see cref="SocketBackpressureDropoutGuard.Offer"/> 직결.
/// </summary>
public sealed class LiveChatProtocolParser
{
    public const int CharCap = 1024, ByteCap = 16 * 1024, TokenCap = 256;
    private readonly char[] _user = new char[SocketBackpressureDropoutGuard.UserCap], _text = new char[CharCap];
    private readonly byte[] _unesc = new byte[ByteCap], _nested = new byte[ByteCap];
    private readonly byte[] _token = new byte[TokenCap];
    private int _tokenLen;

    public long Parsed { get; private set; }
    public long Errors { get; private set; }
    public long Offered { get; private set; }
    public long Dropped { get; private set; }
    public ReadOnlySpan<byte> PageToken => _token.AsSpan(0, _tokenLen);
    public int PollingIntervalMs { get; private set; } = 3000;
    public int LastChzzkCmd { get; private set; }
    public ReadOnlySpan<char> LastUser => _user.AsSpan(0, _lastUser);
    public ReadOnlySpan<char> LastText => _text.AsSpan(0, _lastText);
    private int _lastUser, _lastText;

    public void ResetPaging() { _tokenLen = 0; PollingIntervalMs = 3000; }

    private void Emit(SocketBackpressureDropoutGuard g, ChatPlatform p, int ul, int tl, int amount, ChatRole role, long now)
    {
        _lastUser = ul; _lastText = tl;
        Offered++;
        var r = g.Offer(p, _user.AsSpan(0, ul), _text.AsSpan(0, tl), amount, role, now);
        if (r is not (IngressResult.Queued or IngressResult.PriorityQueued)) Dropped++;
    }

    // ══════════════════════ Twitch IRC ══════════════════════
    public IrcLineKind ParseTwitch(ReadOnlySpan<byte> line, SocketBackpressureDropoutGuard g, long now)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        if (line.IsEmpty) return IrcLineKind.Other;
        Parsed++;
        if (line.StartsWith("PING"u8)) return IrcLineKind.Ping;
        ReadOnlySpan<byte> tags = default;
        if (line[0] == (byte)'@')
        {
            var sp = line.IndexOf((byte)' ');
            if (sp < 0) { Errors++; return IrcLineKind.Other; }
            tags = line[1..sp]; line = line[(sp + 1)..];
        }
        ReadOnlySpan<byte> prefix = default;
        if (!line.IsEmpty && line[0] == (byte)':')
        {
            var sp = line.IndexOf((byte)' ');
            if (sp < 0) { Errors++; return IrcLineKind.Other; }
            prefix = line[1..sp]; line = line[(sp + 1)..];
        }
        var cs = line.IndexOf((byte)' ');
        var cmd = cs < 0 ? line : line[..cs];
        if (cmd.SequenceEqual("PONG"u8)) return IrcLineKind.Pong;
        if (cmd.SequenceEqual("RECONNECT"u8)) return IrcLineKind.Reconnect;
        if (cmd.SequenceEqual("001"u8)) return IrcLineKind.Welcome;
        if (cmd.SequenceEqual("NOTICE"u8)) return IrcLineKind.Notice;
        if (cmd.SequenceEqual("PING"u8)) return IrcLineKind.Ping;
        if (!cmd.SequenceEqual("PRIVMSG"u8) || cs < 0) return IrcLineKind.Other;
        var rest = line[(cs + 1)..];
        var colon = rest.IndexOf(" :"u8);
        if (colon < 0) { Errors++; return IrcLineKind.Other; }
        var body = rest[(colon + 2)..];
        // ACTION(/me) 해제
        if (body.Length > 9 && body[0] == 1 && body[1..].StartsWith("ACTION "u8)) { body = body[8..]; if (!body.IsEmpty && body[^1] == 1) body = body[..^1]; }
        var role = ChatRole.Viewer; var amount = 0; ReadOnlySpan<byte> disp = default;
        while (!tags.IsEmpty)
        {
            var semi = tags.IndexOf((byte)';');
            var kv = semi < 0 ? tags : tags[..semi];
            tags = semi < 0 ? default : tags[(semi + 1)..];
            var eq = kv.IndexOf((byte)'=');
            if (eq < 0) continue;
            var k = kv[..eq]; var v = kv[(eq + 1)..];
            if (k.SequenceEqual("display-name"u8)) disp = v;
            else if (k.SequenceEqual("bits"u8) && Utf8Parser.TryParse(v, out int bits, out _)) amount = (int)Math.Min(int.MaxValue, bits * 12L);
            else if (k.SequenceEqual("mod"u8) && v.SequenceEqual("1"u8)) role |= ChatRole.Moderator;
            else if (k.SequenceEqual("badges"u8))
            {
                if (v.IndexOf("broadcaster/"u8) >= 0) role |= ChatRole.Broadcaster;
                if (v.IndexOf("moderator/"u8) >= 0) role |= ChatRole.Moderator;
                if (v.IndexOf("subscriber/"u8) >= 0) role |= ChatRole.Subscriber;
            }
        }
        if (disp.IsEmpty) { var bang = prefix.IndexOf((byte)'!'); disp = bang > 0 ? prefix[..bang] : prefix; }
        var ul = Utf8(disp, _user);
        ul = UnescapeTag(_user.AsSpan(0, ul));
        var tl = Utf8(body, _text);
        if (ul == 0) { Errors++; return IrcLineKind.Other; }
        Emit(g, ChatPlatform.Twitch, ul, tl, amount, role, now);
        return IrcLineKind.PrivMsg;
    }

    private static int UnescapeTag(Span<char> s)
    {
        var w = 0;
        for (var r = 0; r < s.Length; r++)
        {
            if (s[r] == '\\' && r + 1 < s.Length) { var e = s[++r]; s[w++] = e switch { 's' => ' ', ':' => ';', _ => e }; }
            else s[w++] = s[r];
        }
        return w;
    }

    // ══════════════════════ YouTube liveChatMessages ══════════════════════
    /// <summary>페이지 1 개 파싱 → 메시지 수(-1 = 스키마 오류). nextPageToken · pollingIntervalMillis 갱신.</summary>
    public int ParseYouTubePage(ReadOnlySpan<byte> json, SocketBackpressureDropoutGuard g, long now)
    {
        Parsed++;
        var n = 0;
        try
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) { Errors++; return -1; }
            var sawItems = false;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == 0) break;
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 1) continue;
                if (r.ValueTextEquals("nextPageToken"u8)) { r.Read(); _tokenLen = r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= TokenCap && !r.ValueIsEscaped ? Copy(r.ValueSpan, _token) : 0; }
                else if (r.ValueTextEquals("pollingIntervalMillis"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var ms)) PollingIntervalMs = Math.Clamp(ms, 500, 30_000); }
                else if (r.ValueTextEquals("error"u8)) { Errors++; return -1; }
                else if (r.ValueTextEquals("items"u8))
                {
                    r.Read();
                    if (r.TokenType != JsonTokenType.StartArray) { Errors++; return -1; }
                    sawItems = true;
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                        if (YouTubeItem(ref r, g, now)) n++;
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            if (!sawItems) { Errors++; return -1; }
        }
        catch (JsonException) { Errors++; return -1; }
        return n;
    }

    private bool YouTubeItem(ref Utf8JsonReader r, SocketBackpressureDropoutGuard g, long now)
    {
        var depth = r.CurrentDepth;
        int ul = 0, tl = 0, comment = 0; long micros = 0; var rate = 1.0; var role = ChatRole.Viewer; var known = false;
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == depth) break;
            if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != depth + 1) continue;
            if (r.ValueTextEquals("snippet"u8))
            {
                r.Read(); var d = r.CurrentDepth;
                while (r.Read() && !(r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == d))
                {
                    if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != d + 1) continue;
                    if (r.ValueTextEquals("type"u8)) { r.Read(); known = r.ValueTextEquals("textMessageEvent"u8) || r.ValueTextEquals("superChatEvent"u8) || r.ValueTextEquals("superStickerEvent"u8); }
                    else if (r.ValueTextEquals("displayMessage"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String && comment == 0) tl = Str(ref r, _text); }
                    else if (r.ValueTextEquals("superChatDetails"u8) || r.ValueTextEquals("superStickerDetails"u8))
                    {
                        r.Read(); var sd = r.CurrentDepth;
                        while (r.Read() && !(r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == sd))
                        {
                            if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != sd + 1) continue;
                            if (r.ValueTextEquals("amountMicros"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) Utf8Parser.TryParse(r.ValueSpan, out micros, out _); else if (r.TokenType == JsonTokenType.Number) r.TryGetInt64(out micros); }
                            else if (r.ValueTextEquals("currency"u8)) { r.Read(); rate = KrwRate(ref r); }
                            else if (r.ValueTextEquals("userComment"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) { comment = Str(ref r, _text); if (comment > 0) tl = comment; } }
                            else { r.Read(); r.Skip(); }
                        }
                    }
                    else { r.Read(); r.Skip(); }
                }
            }
            else if (r.ValueTextEquals("authorDetails"u8))
            {
                r.Read(); var d = r.CurrentDepth;
                while (r.Read() && !(r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == d))
                {
                    if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != d + 1) continue;
                    if (r.ValueTextEquals("displayName"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) ul = Str(ref r, _user); }
                    else if (r.ValueTextEquals("isChatModerator"u8)) { r.Read(); if (r.TokenType == JsonTokenType.True) role |= ChatRole.Moderator; }
                    else if (r.ValueTextEquals("isChatOwner"u8)) { r.Read(); if (r.TokenType == JsonTokenType.True) role |= ChatRole.Broadcaster; }
                    else if (r.ValueTextEquals("isChatSponsor"u8)) { r.Read(); if (r.TokenType == JsonTokenType.True) role |= ChatRole.Subscriber; }
                    else { r.Read(); r.Skip(); }
                }
            }
            else { r.Read(); r.Skip(); }
        }
        if (!known || ul == 0) return false;
        var amount = micros > 0 ? (int)Math.Min(int.MaxValue, Math.Round(micros / 1e6 * rate)) : 0;
        Emit(g, ChatPlatform.YouTube, ul, tl, amount, role, now);
        return true;
    }

    private static double KrwRate(ref Utf8JsonReader r)
    {
        if (r.ValueTextEquals("KRW"u8)) return 1;
        if (r.ValueTextEquals("USD"u8)) return 1350;
        if (r.ValueTextEquals("JPY"u8)) return 9;
        if (r.ValueTextEquals("EUR"u8)) return 1450;
        if (r.ValueTextEquals("TWD"u8)) return 42;
        return 1300;
    }

    // ══════════════════════ 치지직 WebSocket ══════════════════════
    /// <summary>프레임 1 개 파싱 → 메시지 수(-1 = 오류). cmd 는 <see cref="LastChzzkCmd"/>.</summary>
    public int ParseChzzk(ReadOnlySpan<byte> frame, SocketBackpressureDropoutGuard g, long now)
    {
        Parsed++;
        LastChzzkCmd = -1;
        var n = 0;
        try
        {
            // 1 차: cmd(필드 순서 무관 — 실서버는 bdy 가 cmd 보다 앞설 수 있음)
            var c0 = new Utf8JsonReader(frame);
            if (!c0.Read() || c0.TokenType != JsonTokenType.StartObject) { Errors++; return -1; }
            while (c0.Read())
            {
                if (c0.TokenType == JsonTokenType.EndObject && c0.CurrentDepth == 0) break;
                if (c0.TokenType != JsonTokenType.PropertyName || c0.CurrentDepth != 1) continue;
                if (c0.ValueTextEquals("cmd"u8)) { c0.Read(); if (c0.TokenType == JsonTokenType.Number && c0.TryGetInt32(out var c)) LastChzzkCmd = c; break; }
                c0.Read(); c0.Skip();
            }
            if (LastChzzkCmd is not (93101 or 93102)) return 0;
            var r = new Utf8JsonReader(frame);
            r.Read();
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == 0) break;
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 1) continue;
                if (r.ValueTextEquals("bdy"u8))
                {
                    r.Read();
                    if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                        if (ChzzkMessage(ref r, g, now, LastChzzkCmd == 93102)) n++;
                    }
                }
                else { r.Read(); r.Skip(); }
            }
        }
        catch (JsonException) { Errors++; return -1; }
        return n;
    }

    private bool ChzzkMessage(ref Utf8JsonReader r, SocketBackpressureDropoutGuard g, long now, bool donation)
    {
        var depth = r.CurrentDepth;
        int ul = 0, tl = 0, amount = 0; var role = ChatRole.Viewer;
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == depth) break;
            if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != depth + 1) continue;
            if (r.ValueTextEquals("msg"u8) || r.ValueTextEquals("content"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) tl = Str(ref r, _text); }
            else if (r.ValueTextEquals("profile"u8))
            {
                r.Read();
                if (r.TokenType != JsonTokenType.String) { r.Skip(); continue; }
                var k = Nested(ref r);
                if (k <= 0) continue;
                var p = new Utf8JsonReader(_nested.AsSpan(0, k));
                while (p.Read())
                {
                    if (p.TokenType != JsonTokenType.PropertyName || p.CurrentDepth != 1) continue;
                    if (p.ValueTextEquals("nickname"u8)) { p.Read(); if (p.TokenType == JsonTokenType.String) ul = Str(ref p, _user); }
                    else if (p.ValueTextEquals("userRoleCode"u8))
                    {
                        p.Read();
                        if (p.ValueTextEquals("streamer"u8)) role |= ChatRole.Broadcaster;
                        else if (p.ValueTextEquals("streaming_channel_manager"u8) || p.ValueTextEquals("streaming_chat_manager"u8) || p.ValueTextEquals("streaming_channel_owner"u8)) role |= ChatRole.Moderator;
                    }
                    else { p.Read(); p.Skip(); }
                }
            }
            else if (r.ValueTextEquals("extras"u8))
            {
                r.Read();
                if (r.TokenType != JsonTokenType.String) { r.Skip(); continue; }
                var k = Nested(ref r);
                if (k <= 0) continue;
                var p = new Utf8JsonReader(_nested.AsSpan(0, k));
                while (p.Read())
                {
                    if (p.TokenType != JsonTokenType.PropertyName || p.CurrentDepth != 1) continue;
                    if (p.ValueTextEquals("payAmount"u8)) { p.Read(); if (p.TokenType == JsonTokenType.Number && p.TryGetInt32(out var a)) amount = Math.Max(0, a); }
                    else { p.Read(); p.Skip(); }
                }
            }
            else { r.Read(); r.Skip(); }
        }
        if (ul == 0) return false;
        if (!donation) amount = 0;
        Emit(g, ChatPlatform.Chzzk, ul, tl, amount, role, now);
        return true;
    }

    /// <summary>JSON 문자열 값(이스케이프 포함) → 해제된 UTF-8 중첩 문서 바이트.</summary>
    private int Nested(ref Utf8JsonReader r)
    {
        var raw = r.ValueSpan;
        if (raw.Length > _nested.Length) return 0;
        return r.ValueIsEscaped ? r.CopyString(_nested) : Copy(raw, _nested);
    }

    private int Str(ref Utf8JsonReader r, Span<char> dst)
    {
        var raw = r.ValueSpan;
        if (!r.ValueIsEscaped) return Utf8(raw, dst);
        if (raw.Length <= _unesc.Length) { var k = r.CopyString(_unesc); return Utf8(_unesc.AsSpan(0, k), dst); }
        return Utf8(raw, dst);
    }

    private static int Copy(ReadOnlySpan<byte> src, Span<byte> dst) { src.CopyTo(dst); return src.Length; }

    /// <summary>UTF-8 → UTF-16(대상 길이 초과 시 코드 포인트 경계에서 절단).</summary>
    public static int Utf8(ReadOnlySpan<byte> src, Span<char> dst)
    {
        if (src.Length > dst.Length)
        {
            var len = dst.Length;
            while (len > 0 && (src[len] & 0xC0) == 0x80) len--;
            src = src[..len];
        }
        return System.Text.Encoding.UTF8.GetChars(src, dst);
    }
}
