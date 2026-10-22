using System.Buffers.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Unicode;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.ViralVoiceFx;

namespace PromptFlowStudio.Services.LiveRestream;

public enum ChatPlatform : byte { YouTube, Twitch }
public enum ChatKind : byte { Chat, SuperChat, Bits, Membership }

/// <summary>v17.83.0 통합 채팅 이벤트(링 슬롯 · 문자열은 병렬 char 버퍼).</summary>
public struct ChatEvent
{
    public long Seq, IngestTicks, AmountMicros, AmountKrw;
    public ChatPlatform Platform;
    public ChatKind Kind;
    public byte Importance;
    public char Cur0, Cur1, Cur2;
    public int AuthorLen, TextLen;
}

/// <summary>v17.83.0 후원/멤버십 리액션 출력 — v17.80 감정 태그 + v17.68 리액션 제스처.</summary>
public interface ILiveChatReactionSink
{
    void OnReaction(in ChatEvent e, VoiceEmotion emotion, HoldPose pose, float seconds);
}

/// <summary>
/// [PromptFlow Studio v17.83.0] 옴니채널 라이브 채팅 &amp; 슈퍼챗 집계 릴레이 엔진(무할당).
///   ▸ 수집: YouTube liveChatMessages.list 폴링 JSON(<see cref="Utf8JsonReader"/> · textMessage/superChat/superSticker/newSponsor/memberMilestone · id 중복 제거 ·
///     nextPageToken/pollingIntervalMillis 추종) + Twitch IRC(WebSocket 텍스트 프레임 · IRCv3 태그 · PRIVMSG bits · USERNOTICE sub/resub · PING 감지).
///   ▸ 단일 순차 링(256 · 전역 Seq) — 다중 생산자 락 · 단일 소비자 커서(<see cref="Drain"/>) · 미소비 덮어쓰기 = Overruns.
///   ▸ 중요도: 금액 KRW 환산(USD/JPY/EUR/GBP/KRW · Bits 1 = $0.01) → 1(₩1 ~) · 2(₩10,000 ~ · 멤버십) · 3(₩50,000 ~) →
///     v17.80 감정(Funny/Excited) + v17.68 제스처(ThumbsUp/Clap · 1.2~3.5 s) 즉시 트리거 · 수집→트리거 지연 µs 계측.
/// </summary>
public sealed class OmniLiveChatSuperChatAggregatorEngine
{
    public const int Capacity = 256, AuthorCap = 32, TextCap = 160, TokenCap = 64, IdRing = 128;
    public const long Tier2Krw = 10_000, Tier3Krw = 50_000;

    private readonly object _gate = new();
    private readonly ChatEvent[] _ev = new ChatEvent[Capacity];
    private readonly char[] _author = new char[Capacity * AuthorCap];
    private readonly char[] _text = new char[Capacity * TextCap];
    private readonly char[] _scratch = new char[2048];
    private readonly char[] _token = new char[TokenCap];
    private readonly ulong[] _ids = new ulong[IdRing];
    private readonly long[] _perPlatform = new long[2];
    private int _idHead, _tokenLen;
    private long _seq, _cursor;

    // 진행 중 YouTube 아이템
    private ChatKind _yKind;
    private long _yMicros;
    private ulong _yId;
    private int _yAuthorLen, _yTextLen, _yTextPri;
    private char _yC0, _yC1, _yC2;

    public long Total => _seq;
    public long Overruns { get; private set; }
    public long Duplicates { get; private set; }
    public long Malformed { get; private set; }
    public long Pings { get; private set; }
    public long SuperChats { get; private set; }
    public long TotalKrw { get; private set; }
    public long Reactions { get; private set; }
    public double LastReactionUs { get; private set; }
    public double MaxReactionUs { get; private set; }
    public int PollIntervalMs { get; private set; } = 2000;
    public long Count(ChatPlatform p) => _perPlatform[(int)p];
    public ReadOnlySpan<char> PageToken => _token.AsSpan(0, _tokenLen);

    /// <summary>최근 이벤트 수(최대 Capacity).</summary>
    public int Recent => (int)Math.Min(_seq, Capacity);
    /// <summary>i = 0 최신.</summary>
    public ref readonly ChatEvent Event(int i) => ref _ev[Slot(_seq - 1 - i)];
    public ReadOnlySpan<char> Author(int i) { var s = Slot(_seq - 1 - i); return _author.AsSpan(s * AuthorCap, _ev[s].AuthorLen); }
    public ReadOnlySpan<char> Text(int i) { var s = Slot(_seq - 1 - i); return _text.AsSpan(s * TextCap, _ev[s].TextLen); }
    private static int Slot(long seq) => (int)(seq % Capacity);

    public void Reset()
    {
        lock (_gate)
        {
            _seq = _cursor = 0; _idHead = _tokenLen = 0; Array.Clear(_ids); Array.Clear(_perPlatform);
            Overruns = Duplicates = Malformed = Pings = SuperChats = TotalKrw = Reactions = 0; LastReactionUs = MaxReactionUs = 0;
        }
    }

    // ══════════════════════ 중요도 · 사상 ══════════════════════

    public static long ToKrw(long micros, char c0, char c1, char c2)
    {
        long rate = (c0, c1, c2) switch
        {
            ('K', 'R', 'W') => 1, ('J', 'P', 'Y') => 9, ('E', 'U', 'R') => 1_450, ('G', 'B', 'P') => 1_700, _ => 1_350,
        };
        return micros / 1_000_000 * rate + micros % 1_000_000 * rate / 1_000_000;
    }

    public static byte Classify(ChatKind k, long krw) => k switch
    {
        ChatKind.Membership => 2,
        ChatKind.SuperChat or ChatKind.Bits => krw >= Tier3Krw ? (byte)3 : krw >= Tier2Krw ? (byte)2 : krw > 0 ? (byte)1 : (byte)0,
        _ => 0,
    };

    public static VoiceEmotion EmotionFor(byte importance) => importance >= 2 ? VoiceEmotion.Excited : importance == 1 ? VoiceEmotion.Funny : VoiceEmotion.Neutral;
    public static HoldPose PoseFor(in ChatEvent e) => e.Importance >= 3 || (e.Importance == 2 && e.Kind != ChatKind.Membership) ? HoldPose.Clap : e.Importance > 0 ? HoldPose.ThumbsUp : HoldPose.Rest;
    public static float SecondsFor(byte importance) => importance switch { 3 => 3.5f, 2 => 2f, 1 => 1.2f, _ => 0f };

    public static string PlatformIcon(ChatPlatform p) => p == ChatPlatform.YouTube ? "▶️" : "🟣";
    public static string KindName(ChatKind k) => k switch { ChatKind.SuperChat => "슈퍼챗", ChatKind.Bits => "비트", ChatKind.Membership => "멤버십", _ => "채팅" };

    // ══════════════════════ 링 기록 ══════════════════════

    private void Push(ChatPlatform p, ChatKind k, long micros, char c0, char c1, char c2, ReadOnlySpan<char> author, ReadOnlySpan<char> text)
    {
        if (_seq - _cursor >= Capacity) { Overruns++; _cursor = _seq - Capacity + 1; }
        var s = Slot(_seq);
        ref var e = ref _ev[s];
        e.Seq = _seq; e.Platform = p; e.Kind = k; e.AmountMicros = micros;
        e.Cur0 = c0; e.Cur1 = c1; e.Cur2 = c2;
        e.AmountKrw = k == ChatKind.Chat || k == ChatKind.Membership ? 0 : ToKrw(micros, c0, c1, c2);
        e.Importance = Classify(k, e.AmountKrw);
        e.IngestTicks = Stopwatch.GetTimestamp();
        e.AuthorLen = Math.Min(author.Length, AuthorCap);
        author.Slice(0, e.AuthorLen).CopyTo(_author.AsSpan(s * AuthorCap));
        e.TextLen = Math.Min(text.Length, TextCap);
        text.Slice(0, e.TextLen).CopyTo(_text.AsSpan(s * TextCap));
        if (k is ChatKind.SuperChat or ChatKind.Bits) { SuperChats++; TotalKrw += e.AmountKrw; }
        _perPlatform[(int)p]++;
        _seq++;
    }

    /// <summary>미소비 이벤트 → 중요도 &gt; 0 이면 리액션 즉시 발화. 반환 = 소비 수.</summary>
    public int Drain(ILiveChatReactionSink? sink)
    {
        lock (_gate)
        {
            var n = 0;
            while (_cursor < _seq)
            {
                ref readonly var e = ref _ev[Slot(_cursor)];
                _cursor++; n++;
                if (e.Importance == 0 || sink is null) continue;
                sink.OnReaction(in e, EmotionFor(e.Importance), PoseFor(in e), SecondsFor(e.Importance));
                Reactions++;
                var us = (Stopwatch.GetTimestamp() - e.IngestTicks) * 1e6 / Stopwatch.Frequency;
                LastReactionUs = us; if (us > MaxReactionUs) MaxReactionUs = us;
            }
            return n;
        }
    }

    // ══════════════════════ Twitch IRC(WebSocket 텍스트 프레임) ══════════════════════

    /// <summary>IRC 줄 묶음(CRLF) 수집. 반환 = 추가 이벤트 수.</summary>
    public int IngestTwitchIrc(ReadOnlySpan<byte> payload)
    {
        var added = 0;
        lock (_gate)
        {
            while (payload.Length > 0)
            {
                var nl = payload.IndexOf((byte)'\n');
                var line = nl < 0 ? payload : payload.Slice(0, nl);
                payload = nl < 0 ? ReadOnlySpan<byte>.Empty : payload.Slice(nl + 1);
                if (line.Length > 0 && line[^1] == (byte)'\r') line = line.Slice(0, line.Length - 1);
                if (line.Length == 0) continue;
                if (IrcLine(line)) added++;
            }
        }
        return added;
    }

    private bool IrcLine(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> tags = default;
        if (line[0] == (byte)'@')
        {
            var sp = line.IndexOf((byte)' ');
            if (sp < 0) { Malformed++; return false; }
            tags = line.Slice(1, sp - 1); line = line.Slice(sp + 1);
        }
        ReadOnlySpan<byte> prefix = default;
        if (line.Length > 0 && line[0] == (byte)':')
        {
            var sp = line.IndexOf((byte)' ');
            if (sp < 0) { Malformed++; return false; }
            prefix = line.Slice(1, sp - 1); line = line.Slice(sp + 1);
        }
        var cs = line.IndexOf((byte)' ');
        var cmd = cs < 0 ? line : line.Slice(0, cs);
        if (cmd.SequenceEqual("PING"u8)) { Pings++; return false; }
        var isMsg = cmd.SequenceEqual("PRIVMSG"u8);
        var isNotice = cmd.SequenceEqual("USERNOTICE"u8);
        if (!isMsg && !isNotice) return false;
        var rest = cs < 0 ? ReadOnlySpan<byte>.Empty : line.Slice(cs + 1);
        var tc = rest.IndexOf(" :"u8);
        var body = tc < 0 ? ReadOnlySpan<byte>.Empty : rest.Slice(tc + 2);

        var name = Tag(tags, "display-name"u8);
        if (name.Length == 0) { var bang = prefix.IndexOf((byte)'!'); name = bang > 0 ? prefix.Slice(0, bang) : prefix; }
        var kind = ChatKind.Chat; long micros = 0;
        if (isNotice)
        {
            var id = Tag(tags, "msg-id"u8);
            if (!(id.SequenceEqual("sub"u8) || id.SequenceEqual("resub"u8) || id.SequenceEqual("subgift"u8))) return false;
            kind = ChatKind.Membership;
            if (body.Length == 0) body = "구독 감사합니다!"u8;
        }
        else if (Utf8Parser.TryParse(Tag(tags, "bits"u8), out long bits, out _) && bits > 0)
        {
            kind = ChatKind.Bits; micros = bits * 10_000; // 1 bit = $0.01
        }
        var a = _scratch.AsSpan(0, AuthorCap);
        var an = Decode(name, a);
        var t = _scratch.AsSpan(AuthorCap, TextCap);
        var tn = Decode(body, t);
        Push(ChatPlatform.Twitch, kind, micros, 'U', 'S', 'D', a.Slice(0, an), t.Slice(0, tn));
        return true;
    }

    private static ReadOnlySpan<byte> Tag(ReadOnlySpan<byte> tags, ReadOnlySpan<byte> key)
    {
        while (tags.Length > 0)
        {
            var semi = tags.IndexOf((byte)';');
            var kv = semi < 0 ? tags : tags.Slice(0, semi);
            tags = semi < 0 ? ReadOnlySpan<byte>.Empty : tags.Slice(semi + 1);
            var eq = kv.IndexOf((byte)'=');
            if (eq == key.Length && kv.StartsWith(key)) return kv.Slice(eq + 1);
        }
        return default;
    }

    private static int Decode(ReadOnlySpan<byte> utf8, Span<char> dst)
    {
        Utf8.ToUtf16(utf8, dst, out _, out var written, replaceInvalidSequences: true, isFinalBlock: true);
        return written;
    }

    // ══════════════════════ YouTube liveChatMessages.list 폴링 JSON ══════════════════════

    private enum YProp : byte { None, Id, Type, DisplayMessage, MessageText, UserComment, AmountMicros, Currency, DisplayName, NextPageToken, PollingInterval }

    /// <summary>폴링 응답 1 건 수집. 반환 = 추가 이벤트 수(중복 id 제외).</summary>
    public int IngestYouTubePoll(ReadOnlySpan<byte> json)
    {
        var added = 0;
        lock (_gate)
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            var prop = YProp.None; var inItems = false; var inItem = false;
            try
            {
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            prop = PropOf(ref r);
                            if (r.CurrentDepth == 1 && r.ValueTextEquals("items"u8)) inItems = true;
                            break;
                        case JsonTokenType.StartObject:
                            if (inItems && r.CurrentDepth == 2) { inItem = true; BeginItem(); }
                            prop = YProp.None;
                            break;
                        case JsonTokenType.EndObject:
                            if (inItem && r.CurrentDepth == 2) { inItem = false; if (CommitItem()) added++; }
                            break;
                        case JsonTokenType.EndArray:
                            if (inItems && r.CurrentDepth == 1) inItems = false;
                            break;
                        case JsonTokenType.String:
                        case JsonTokenType.Number:
                            Value(ref r, prop, inItem);
                            prop = YProp.None;
                            break;
                        default:
                            prop = YProp.None;
                            break;
                    }
                }
            }
            catch (JsonException) { Malformed++; }
        }
        return added;
    }

    private static YProp PropOf(ref Utf8JsonReader r) =>
        r.ValueTextEquals("id"u8) ? YProp.Id :
        r.ValueTextEquals("type"u8) ? YProp.Type :
        r.ValueTextEquals("displayMessage"u8) ? YProp.DisplayMessage :
        r.ValueTextEquals("messageText"u8) ? YProp.MessageText :
        r.ValueTextEquals("userComment"u8) ? YProp.UserComment :
        r.ValueTextEquals("amountMicros"u8) ? YProp.AmountMicros :
        r.ValueTextEquals("currency"u8) ? YProp.Currency :
        r.ValueTextEquals("displayName"u8) ? YProp.DisplayName :
        r.ValueTextEquals("nextPageToken"u8) ? YProp.NextPageToken :
        r.ValueTextEquals("pollingIntervalMillis"u8) ? YProp.PollingInterval : YProp.None;

    private void BeginItem()
    {
        _yKind = ChatKind.Chat; _yMicros = 0; _yId = 0; _yAuthorLen = _yTextLen = _yTextPri = 0;
        _yC0 = 'U'; _yC1 = 'S'; _yC2 = 'D';
    }

    private void Value(ref Utf8JsonReader r, YProp prop, bool inItem)
    {
        if (prop == YProp.None) return;
        if (!inItem)
        {
            if (prop == YProp.PollingInterval && r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var ms)) PollIntervalMs = Math.Clamp(ms, 500, 60_000);
            else if (prop == YProp.NextPageToken && r.TokenType == JsonTokenType.String) _tokenLen = Copy(ref r, _token);
            return;
        }
        switch (prop)
        {
            case YProp.Id: _yId = RtmpWire.Fnv(r.ValueSpan); break;
            case YProp.Type:
                if (r.ValueTextEquals("superChatEvent"u8) || r.ValueTextEquals("superStickerEvent"u8)) _yKind = ChatKind.SuperChat;
                else if (r.ValueTextEquals("newSponsorEvent"u8) || r.ValueTextEquals("memberMilestoneChatEvent"u8)) _yKind = ChatKind.Membership;
                break;
            case YProp.AmountMicros:
                if (r.TokenType == JsonTokenType.Number) { if (r.TryGetInt64(out var v)) _yMicros = v; }
                else if (!r.ValueIsEscaped && Utf8Parser.TryParse(r.ValueSpan, out long s, out _)) _yMicros = s;
                break;
            case YProp.Currency:
                if (r.ValueSpan.Length == 3) { _yC0 = (char)r.ValueSpan[0]; _yC1 = (char)r.ValueSpan[1]; _yC2 = (char)r.ValueSpan[2]; }
                break;
            case YProp.DisplayName: _yAuthorLen = Copy(ref r, _scratch.AsSpan(TextCap, AuthorCap)); break;
            case YProp.UserComment: TakeText(ref r, 3); break;
            case YProp.MessageText: TakeText(ref r, 2); break;
            case YProp.DisplayMessage: TakeText(ref r, 1); break;
        }
    }

    private void TakeText(ref Utf8JsonReader r, int pri)
    {
        if (pri < _yTextPri || r.TokenType != JsonTokenType.String) return;
        _yTextPri = pri;
        _yTextLen = Copy(ref r, _scratch.AsSpan(0, TextCap));
    }

    /// <summary>JSON 문자열 → 고정 버퍼(이스케이프 해제 · 초과 시 절단).</summary>
    private int Copy(ref Utf8JsonReader r, Span<char> dst)
    {
        if (r.TokenType != JsonTokenType.String) return 0;
        var raw = r.ValueSpan;
        var tmp = _scratch.AsSpan(TextCap + AuthorCap);
        if (raw.Length <= tmp.Length)
        {
            var n = r.CopyString(tmp);
            n = Math.Min(n, dst.Length);
            tmp.Slice(0, n).CopyTo(dst);
            return n;
        }
        return Decode(raw.Slice(0, Math.Min(raw.Length, dst.Length)), dst);
    }

    private bool CommitItem()
    {
        if (_yId != 0)
        {
            for (var i = 0; i < IdRing; i++) if (_ids[i] == _yId) { Duplicates++; return false; }
            _ids[_idHead] = _yId; _idHead = (_idHead + 1) % IdRing;
        }
        if (_yKind == ChatKind.Chat && _yTextLen == 0) return false;
        ReadOnlySpan<char> text = _scratch.AsSpan(0, _yTextLen);
        if (_yKind == ChatKind.Membership && _yTextLen == 0) text = "새 멤버십 가입!".AsSpan();
        Push(ChatPlatform.YouTube, _yKind, _yKind == ChatKind.SuperChat ? _yMicros : 0, _yC0, _yC1, _yC2, _scratch.AsSpan(TextCap, _yAuthorLen), text);
        return true;
    }
}
