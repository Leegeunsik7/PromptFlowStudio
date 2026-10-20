using System.Globalization;
using PromptFlowStudio.Services.AvatarActing;
using PromptFlowStudio.Services.VirtualLive;
namespace PromptFlowStudio.Services.InfluencerLive;

/// <summary>v17.61.0 시청자 채팅 의도.</summary>
public enum ViewerChatIntent : byte { Chatter, Donation, Question, Praise, Humor }

/// <summary>v17.61.0 수신 채팅 1 건(본문 · ID 는 엔진 문자 링 범위 — 문자열 할당 0).</summary>
public struct ViewerChatMessage
{
    public long Seq, AtMs;
    public int UserHash, UserStart, UserLen, TextStart, TextLen, Amount;
    public ViewerChatIntent Intent;
    public ChatSafetyVerdict Verdict;
    public bool Reacted;
    public float ClassifyUs;
}

/// <summary>v17.61.0 아바타 리액션 큐 항목.</summary>
public struct ViewerReactionCue
{
    public long MessageSeq, ReceivedMs, DispatchedMs;
    public int Amount, Line, Tier;
    public ViewerChatIntent Intent;
    public ReactionGesture Gesture;
    public GestureClass ActingClass;
}

/// <summary>v17.61.0 리액션 수신부(v17.60 연기 디렉터 · v17.32 음성) — 무할당 구현.</summary>
public interface IInfluencerReactionSink
{
    /// <summary>비후원 리액션은 발화 중이면 보류된다.</summary>
    bool Busy { get; }
    void React(in ViewerReactionCue cue, ReadOnlySpan<char> line);
}

/// <summary>
/// [PromptFlow Studio v17.61.0] 💬 실시간 시청자 채팅 감성 분석 &amp; 아바타 리액션 트리거 엔진(<see cref="Ingest"/> · <see cref="Dispatch"/> 무할당).
///   ▸ 파서: WebSocket/HTTP 청크(UTF-8)의 NDJSON(<c>{"user":..,"text":..,"amount":..,"type":"chat|superchat|like"}</c>) 또는 Twitch IRCv3(<c>@bits=100 :nick!.. PRIVMSG #ch :본문</c>) 줄 단위 —
///     바이트 스캔 → <see cref="System.Text.Encoding.UTF8"/> 스팬 디코드 → JSON 이스케이프(\uXXXX 포함) 제자리 해제 → 64 K 문자 링 복사.
///   ▸ 세이프티: <see cref="ChatSpamToxicitySafetyGuard"/> 판정 → 차단분은 분류/반응/하이라이트 집계에서 제외.
///   ▸ 분류(≤ 5 ms 계약 · 실측 µs): 후원(금액) &gt; 질문(?/의문사) &gt; 유머(ㅋㅋ/lol) &gt; 칭찬(최고/love) &gt; 잡담 — 한글 부분 일치 · 영문 단어 경계 일치.
///   ▸ 리액션 큐(64 · 후원 우선 삽입): 매 60 fps 틱 <see cref="Dispatch"/> — 후원은 발화 선점(감사 + 하트 · 포괄 제스처), 질문은 생각 표정 + 손짓(사고),
///     칭찬/유머는 쿨다운(질문 4 s · 기타 6 s)·무발화 시에만. 접수→디스패치 100 ms 초과분은 폐기(후원 제외) — 트리거 지연 계측.
///   ▸ 대사: 후원자 닉네임(유해 시 "익명")·금액(N0)을 고정 문자 버퍼에 조립해 수신부로 전달.
/// </summary>
public sealed class RealtimeViewerChatReactionTriggerEngine
{
    public const int MsgRing = 256, CharRing = 65536, QueueCap = 64, LineCap = 256, ScratchCap = 1024, BudgetMs = 100;
    public const int QuestionCooldownMs = 4000, SocialCooldownMs = 6000;
    public const double ClassifyBudgetUs = 5000;
    public static readonly int[] TierFloor = { 0, 1000, 10000, 50000 };

    public static readonly string[] DonationLines =
    {
        "님 {0}원 후원 정말 감사합니다! 여러분 모두 사랑해요!",
        "님 {0}원 정말 감사해요! 우리 함께 최고의 방송 만들어요!",
        "님 {0}원 와 진짜 감사합니다! 여러분 덕분에 너무 행복해요!",
    };
    public static readonly string[] QuestionLines =
    {
        "음... 좋은 질문이네요, 잠깐 생각해 볼게요?",
        "글쎄요, 음... 아마 이렇게 하면 될 것 같아요.",
        "음 그건 저도 생각해 봤는데요, 아마 다음 방송에서 알려드릴게요?",
    };
    public static readonly string[] PraiseLines =
    {
        "와 정말 고마워요! 당신이 최고예요!",
        "칭찬 감사해요, 여러분이 진짜 멋져요!",
    };
    public static readonly string[] HumorLines =
    {
        "아 진짜 너무 웃겨요! 정말 최고야!",
        "ㅋㅋ 여러분 정말 웃기다, 진짜 못 참겠어요!",
    };

    private readonly ChatSpamToxicitySafetyGuard _guard;
    private readonly ViewerChatMessage[] _msg = new ViewerChatMessage[MsgRing];
    private readonly char[] _chars = new char[CharRing];
    private readonly char[] _scratch = new char[ScratchCap], _user = new char[64], _line = new char[LineCap];
    private readonly ViewerReactionCue[] _q = new ViewerReactionCue[QueueCap];
    private readonly long[] _intentCount = new long[5];
    private readonly float[] _latency = new float[64];
    private long _seq, _charHead, _lastQuestionMs = long.MinValue / 2, _lastSocialMs = long.MinValue / 2;
    private int _qHead, _qCount, _latHead, _latN, _donLine, _qLine, _pLine, _hLine;

    public RealtimeViewerChatReactionTriggerEngine(ChatSpamToxicitySafetyGuard guard) => _guard = guard;

    public ChatSpamToxicitySafetyGuard Guard => _guard;
    public long Received => _seq;
    public long Accepted { get; private set; }
    public long Likes { get; private set; }
    public long ParseErrors { get; private set; }
    public long DonationTotal { get; private set; }
    public long CuesQueued { get; private set; }
    public long CuesDispatched { get; private set; }
    public long CuesStale { get; private set; }
    public long QueueOverflows { get; private set; }
    public long BlockedReactions { get; private set; }
    public double LastClassifyUs { get; private set; }
    public double MaxClassifyUs { get; private set; }
    public double LastIngestUs { get; private set; }
    public double LastTriggerMs { get; private set; }
    public double MaxTriggerMs { get; private set; }
    public int QueueCount => _qCount;
    public long Intents(ViewerChatIntent i) => _intentCount[(int)i];
    public ref readonly ViewerReactionCue Queued(int i) => ref _q[(_qHead + i) % QueueCap];
    public int LatencyCount => _latN;
    public float LatencySeries(int i) => _latency[(_latHead - 1 - i + 2 * _latency.Length) % _latency.Length];
    public ViewerReactionCue LastCue { get; private set; }
    public ReadOnlySpan<char> LastLine => _line.AsSpan(0, _lineLen);
    private int _lineLen;

    /// <summary>i = 0 이 최신(최대 <see cref="MsgRing"/>).</summary>
    public int MessageCount => (int)Math.Min(_seq, MsgRing);
    public ref readonly ViewerChatMessage Message(int i) => ref _msg[(int)((_seq - 1 - i) % MsgRing)];
    public ReadOnlySpan<char> MessageUser(int i) { ref readonly var m = ref Message(i); return Span(m.UserStart, m.UserLen); }
    public ReadOnlySpan<char> MessageText(int i) { ref readonly var m = ref Message(i); return Span(m.TextStart, m.TextLen); }
    private ReadOnlySpan<char> Span(int start, int len) => start + len <= CharRing ? _chars.AsSpan(start, len) : ReadOnlySpan<char>.Empty;

    public static string IntentName(ViewerChatIntent i) => i switch
    {
        ViewerChatIntent.Donation => "💸 후원",
        ViewerChatIntent.Question => "❓ 질문",
        ViewerChatIntent.Praise => "💖 칭찬",
        ViewerChatIntent.Humor => "😂 유머",
        _ => "💬 잡담",
    };

    public static string GestureName(ReactionGesture g) => g switch
    {
        ReactionGesture.HeartHands => "🫶 하트",
        ReactionGesture.ThinkingPose => "🤔 생각",
        ReactionGesture.Clap => "👏 박수",
        ReactionGesture.Wave => "👋 손인사",
        ReactionGesture.Bow => "🙇 인사",
        ReactionGesture.Nod => "🙂 끄덕",
        _ => "·",
    };

    public void Reset()
    {
        _seq = 0; _charHead = 0; _qHead = _qCount = 0; _latHead = _latN = 0; _lineLen = 0;
        _lastQuestionMs = _lastSocialMs = long.MinValue / 2;
        Accepted = Likes = ParseErrors = DonationTotal = CuesQueued = CuesDispatched = CuesStale = QueueOverflows = BlockedReactions = 0;
        MaxClassifyUs = MaxTriggerMs = 0;
        Array.Clear(_intentCount);
    }

    // ══════════════════════ 수신 · 파싱(무할당) ══════════════════════
    /// <summary>WebSocket 프레임/HTTP 청크 1 개(UTF-8 · 개행 구분 다중 줄) 수신. 반환 = 처리 메시지 수.</summary>
    public int Ingest(ReadOnlySpan<byte> utf8, long nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = 0;
        while (!utf8.IsEmpty)
        {
            var nl = utf8.IndexOf((byte)'\n');
            var line = nl < 0 ? utf8 : utf8[..nl];
            utf8 = nl < 0 ? ReadOnlySpan<byte>.Empty : utf8[(nl + 1)..];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (line.IsEmpty) continue;
            if (ParseLine(line, nowMs)) n++; else ParseErrors++;
        }
        LastIngestUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return n;
    }

    private bool ParseLine(ReadOnlySpan<byte> line, long now)
    {
        if (line[0] == (byte)'{') return ParseJson(line, now);
        return ParseIrc(line, now);
    }

    private bool ParseJson(ReadOnlySpan<byte> j, long now)
    {
        var type = JsonString(j, "type"u8, out var typeRaw);
        if (type && typeRaw.SequenceEqual("like"u8))
        {
            var c = JsonInt(j, "count"u8); Likes += c > 0 ? c : 1;
            return true;
        }
        if (!JsonString(j, "user"u8, out var userRaw) || !JsonString(j, "text"u8, out var textRaw)) return false;
        var amount = Math.Max(0, JsonInt(j, "amount"u8));
        var un = Decode(userRaw, _user);
        var tn = Decode(textRaw, _scratch);
        if (un <= 0 || tn < 0) return false;
        Accept(_user.AsSpan(0, un), _scratch.AsSpan(0, tn), amount, now);
        return true;
    }

    /// <summary>Twitch IRCv3: <c>[@tags ]:nick!user@host PRIVMSG #chan :text</c> · bits 태그 = 후원.</summary>
    private bool ParseIrc(ReadOnlySpan<byte> l, long now)
    {
        var amount = 0;
        if (l[0] == (byte)'@')
        {
            var sp = l.IndexOf((byte)' ');
            if (sp < 0) return false;
            var tags = l[1..sp];
            var b = tags.IndexOf("bits="u8);
            if (b >= 0) { var v = tags[(b + 5)..]; var e = v.IndexOf((byte)';'); if (e >= 0) v = v[..e]; amount = ParseInt(v) * 12; }   // 1 bit ≈ 12 원
            l = l[(sp + 1)..];
        }
        if (l.IsEmpty || l[0] != (byte)':') return false;
        var bang = l.IndexOf((byte)'!');
        var cmd = l.IndexOf(" PRIVMSG "u8);
        if (bang < 2 || cmd < bang) return false;
        var rest = l[(cmd + 9)..];
        var colon = rest.IndexOf(" :"u8);
        if (colon < 0) return false;
        var un = Utf8(l[1..bang], _user);
        var tn = Utf8(rest[(colon + 2)..], _scratch);
        if (un <= 0 || tn < 0) return false;
        Accept(_user.AsSpan(0, un), _scratch.AsSpan(0, tn), amount, now);
        return true;
    }

    private static int Utf8(ReadOnlySpan<byte> src, Span<char> dst)
    {
        if (src.Length > dst.Length) src = src[..dst.Length];
        return System.Text.Encoding.UTF8.GetChars(src, dst);
    }

    /// <summary>JSON 문자열 원본(따옴표 내부 · 이스케이프 유지) → UTF-16 디코드 + 이스케이프 제자리 해제.</summary>
    private static int Decode(ReadOnlySpan<byte> raw, Span<char> dst)
    {
        var n = Utf8(raw, dst);
        var w = 0;
        for (var r = 0; r < n; r++)
        {
            var c = dst[r];
            if (c != '\\' || r + 1 >= n) { dst[w++] = c; continue; }
            var e = dst[++r];
            switch (e)
            {
                case 'n': dst[w++] = ' '; break;
                case 't': dst[w++] = ' '; break;
                case 'r': break;
                case 'u':
                    if (r + 4 < n && int.TryParse(dst.Slice(r + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)) { dst[w++] = (char)cp; r += 4; }
                    else return -1;
                    break;
                default: dst[w++] = e; break;
            }
        }
        return w;
    }

    private static bool JsonString(ReadOnlySpan<byte> j, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        value = default;
        var at = FindKey(j, key);
        if (at < 0 || at >= j.Length || j[at] != (byte)'"') return false;
        var s = at + 1;
        for (var i = s; i < j.Length; i++)
        {
            if (j[i] == (byte)'\\') { i++; continue; }
            if (j[i] == (byte)'"') { value = j[s..i]; return true; }
        }
        return false;
    }

    private static int JsonInt(ReadOnlySpan<byte> j, ReadOnlySpan<byte> key)
    {
        var at = FindKey(j, key);
        if (at < 0) return 0;
        if (at < j.Length && j[at] == (byte)'"') at++;
        return ParseInt(j[at..]);
    }

    private static int ParseInt(ReadOnlySpan<byte> s)
    {
        long v = 0; var any = false;
        foreach (var b in s) { if (b is >= (byte)'0' and <= (byte)'9') { v = Math.Min(v * 10 + (b - '0'), int.MaxValue); any = true; } else if (any || b is not ((byte)' ' or (byte)'+')) break; }
        return (int)v;
    }

    /// <summary>"key" 다음 ':' 뒤 첫 값 위치.</summary>
    private static int FindKey(ReadOnlySpan<byte> j, ReadOnlySpan<byte> key)
    {
        var off = 0;
        while (off < j.Length)
        {
            var k = j[off..].IndexOf(key);
            if (k < 0) return -1;
            var p = off + k;
            if (p > 0 && j[p - 1] == (byte)'"' && p + key.Length < j.Length && j[p + key.Length] == (byte)'"')
            {
                var i = p + key.Length + 1;
                while (i < j.Length && (j[i] == (byte)' ' || j[i] == (byte)':')) i++;
                return i;
            }
            off = p + 1;
        }
        return -1;
    }

    // ══════════════════════ 판정 · 분류 · 큐(무할당) ══════════════════════
    /// <summary>디코드된 메시지 1 건 접수(테스트 · 내부 공용).</summary>
    public ChatSafetyVerdict Accept(ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, long now)
    {
        if (user.Length > 48) user = user[..48];
        if (text.Length > ScratchCap) text = text[..ScratchCap];
        var verdict = _guard.Inspect(user, text, now);
        ref var m = ref _msg[(int)(_seq % MsgRing)];
        m = default;
        m.Seq = _seq; m.AtMs = now; m.Amount = amount; m.Verdict = verdict; m.UserHash = ChatSpamToxicitySafetyGuard.HashUser(user);
        m.UserStart = Store(user); m.UserLen = user.Length;
        m.TextStart = Store(text); m.TextLen = text.Length;
        _seq++;
        if (verdict != ChatSafetyVerdict.Clean)
        {
            if (amount > 0) BlockedReactions++;
            return verdict;
        }
        Accepted++;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        m.Intent = Classify(text, amount);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        m.ClassifyUs = (float)us; LastClassifyUs = us; if (us > MaxClassifyUs && Accepted > 8) MaxClassifyUs = us;
        _intentCount[(int)m.Intent]++;
        if (m.Intent == ViewerChatIntent.Donation) DonationTotal += amount;
        if (Enqueue(ref m, now)) m.Reacted = true;
        return verdict;
    }

    private int Store(ReadOnlySpan<char> s)
    {
        var cs = (int)(_charHead % CharRing);
        if (cs + s.Length > CharRing) { _charHead += CharRing - cs; cs = 0; }
        s.CopyTo(_chars.AsSpan(cs));
        _charHead += s.Length;
        return cs;
    }

    /// <summary>의도 분류 — 후원 &gt; 질문 &gt; 유머 &gt; 칭찬 &gt; 잡담(무할당).</summary>
    public static ViewerChatIntent Classify(ReadOnlySpan<char> t, int amount)
    {
        if (amount > 0) return ViewerChatIntent.Donation;
        if (t.IndexOfAny('?', '？') >= 0 || Has(t, "어떻게") || Has(t, "뭐예요") || Has(t, "뭐에요") || Has(t, "뭔가요") || Has(t, "언제") || Has(t, "왜") || Has(t, "어디") || Has(t, "누구")
            || Has(t, "무슨") || Has(t, "알려주") || Has(t, "궁금") || Has(t, "질문") || Word(t, "how") || Word(t, "what") || Word(t, "why") || Word(t, "when") || Word(t, "where") || Word(t, "who")) return ViewerChatIntent.Question;
        if (Has(t, "ㅋㅋ") || Has(t, "ㅎㅎ") || Has(t, "웃겨") || Has(t, "웃기") || Has(t, "빵터") || Has(t, "😂") || Has(t, "🤣") || Word(t, "lol") || Word(t, "lmao") || Word(t, "haha") || Word(t, "rofl")) return ViewerChatIntent.Humor;
        if (Has(t, "최고") || Has(t, "귀여") || Has(t, "예쁘") || Has(t, "이쁘") || Has(t, "멋지") || Has(t, "멋있") || Has(t, "대박") || Has(t, "사랑") || Has(t, "잘한") || Has(t, "천재") || Has(t, "짱")
            || Has(t, "💖") || Has(t, "❤") || Has(t, "👍") || Word(t, "love") || Word(t, "cool") || Word(t, "amazing") || Word(t, "awesome") || Word(t, "great") || Word(t, "best") || Word(t, "cute")) return ViewerChatIntent.Praise;
        return ViewerChatIntent.Chatter;
    }

    private static bool Has(ReadOnlySpan<char> t, string k) => t.IndexOf(k.AsSpan(), StringComparison.Ordinal) >= 0;

    /// <summary>영문 단어 경계 일치(대소문자 무시).</summary>
    private static bool Word(ReadOnlySpan<char> t, string w)
    {
        var off = 0;
        while (off <= t.Length - w.Length)
        {
            var k = t[off..].IndexOf(w.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (k < 0) return false;
            var s = off + k; var e = s + w.Length;
            if ((s == 0 || !char.IsLetter(t[s - 1])) && (e >= t.Length || !char.IsLetter(t[e]))) return true;
            off = s + 1;
        }
        return false;
    }

    private bool Enqueue(ref ViewerChatMessage m, long now)
    {
        var cue = new ViewerReactionCue { MessageSeq = m.Seq, ReceivedMs = now, Amount = m.Amount, Intent = m.Intent };
        switch (m.Intent)
        {
            case ViewerChatIntent.Donation:
                cue.Tier = TierOf(m.Amount);
                cue.Gesture = cue.Tier >= 2 ? ReactionGesture.HeartHands : cue.Tier == 1 ? ReactionGesture.Bow : ReactionGesture.Nod;
                cue.ActingClass = GestureClass.Inclusive;
                cue.Line = _donLine++ % DonationLines.Length;
                break;
            case ViewerChatIntent.Question:
                if (now - _lastQuestionMs < QuestionCooldownMs) return false;
                _lastQuestionMs = now;
                cue.Gesture = ReactionGesture.ThinkingPose; cue.ActingClass = GestureClass.Think; cue.Line = _qLine++ % QuestionLines.Length;
                break;
            case ViewerChatIntent.Praise:
            case ViewerChatIntent.Humor:
                if (now - _lastSocialMs < SocialCooldownMs) return false;
                _lastSocialMs = now;
                var praise = m.Intent == ViewerChatIntent.Praise;
                cue.Gesture = praise ? ReactionGesture.HeartHands : ReactionGesture.Clap;
                cue.ActingClass = praise ? GestureClass.DeicticOther : GestureClass.Emphasis;
                cue.Line = praise ? _pLine++ % PraiseLines.Length : _hLine++ % HumorLines.Length;
                break;
            default:
                return false;
        }
        if (_qCount >= QueueCap)
        {
            if (m.Intent != ViewerChatIntent.Donation) { QueueOverflows++; return false; }
            // 후원이 가득 찬 큐에 도착 — 최후 비후원 항목 교체, 없으면 가장 오래된 후원 폐기
            var victim = -1;
            for (var i = _qCount - 1; i >= 0; i--) if (_q[(_qHead + i) % QueueCap].Intent != ViewerChatIntent.Donation) { victim = i; break; }
            if (victim < 0) { _qHead = (_qHead + 1) % QueueCap; _qCount--; }
            else { for (var i = victim; i < _qCount - 1; i++) _q[(_qHead + i) % QueueCap] = _q[(_qHead + i + 1) % QueueCap]; _qCount--; }
            QueueOverflows++;
        }
        if (m.Intent == ViewerChatIntent.Donation)
        {
            // 후원 우선 삽입(후원끼리는 FIFO)
            var pos = 0;
            while (pos < _qCount && _q[(_qHead + pos) % QueueCap].Intent == ViewerChatIntent.Donation) pos++;
            for (var i = _qCount; i > pos; i--) _q[(_qHead + i) % QueueCap] = _q[(_qHead + i - 1) % QueueCap];
            _q[(_qHead + pos) % QueueCap] = cue;
        }
        else _q[(_qHead + _qCount) % QueueCap] = cue;
        _qCount++;
        CuesQueued++;
        return true;
    }

    public static int TierOf(int amount)
    {
        var t = 0;
        for (var i = 1; i < TierFloor.Length; i++) if (amount >= TierFloor[i]) t = i;
        return t;
    }

    /// <summary>60 fps 틱 — 큐 선두 1 건 디스패치(무할당). 반환 = 디스패치 여부.</summary>
    public bool Dispatch(long now, IInfluencerReactionSink? sink)
    {
        while (_qCount > 0)
        {
            ref var c = ref _q[_qHead];
            var donation = c.Intent == ViewerChatIntent.Donation;
            if (!donation && now - c.ReceivedMs > BudgetMs) { Pop(); CuesStale++; continue; }
            if (!donation && sink is not null && sink.Busy) return false;
            var cue = c; Pop();
            // 원천 메시지가 이후 섀도우밴된 경우(같은 틱 후속 위반) 반응 차단
            ref readonly var m = ref _msg[(int)(cue.MessageSeq % MsgRing)];
            if (m.Seq == cue.MessageSeq && _guard.IsShadowbanned(m.UserHash)) { BlockedReactions++; continue; }
            cue.DispatchedMs = now;
            ComposeLine(in cue, m.Seq == cue.MessageSeq ? Span(m.UserStart, m.UserLen) : ReadOnlySpan<char>.Empty);
            sink?.React(in cue, LastLine);
            var lat = now - cue.ReceivedMs;
            LastTriggerMs = lat; if (lat > MaxTriggerMs) MaxTriggerMs = lat;
            _latency[_latHead] = lat; _latHead = (_latHead + 1) % _latency.Length; if (_latN < _latency.Length) _latN++;
            LastCue = cue;
            CuesDispatched++;
            return true;
        }
        return false;
    }

    private void Pop() { _qHead = (_qHead + 1) % QueueCap; _qCount--; }

    private void ComposeLine(in ViewerReactionCue cue, ReadOnlySpan<char> user)
    {
        var dst = _line.AsSpan();
        var n = 0;
        if (cue.Intent == ViewerChatIntent.Donation)
        {
            if (user.IsEmpty || _guard.IsToxic(user)) user = "익명".AsSpan();
            if (user.Length > 24) user = user[..24];
            user.CopyTo(dst); n = user.Length;
            var tpl = DonationLines[cue.Line].AsSpan();
            var ph = tpl.IndexOf("{0}".AsSpan(), StringComparison.Ordinal);
            tpl[..ph].CopyTo(dst[n..]); n += ph;
            if (cue.Amount.TryFormat(dst[n..], out var w, "N0", CultureInfo.InvariantCulture)) n += w;
            var tail = tpl[(ph + 3)..];
            tail.CopyTo(dst[n..]); n += tail.Length;
        }
        else
        {
            var src = cue.Intent switch { ViewerChatIntent.Question => QuestionLines[cue.Line], ViewerChatIntent.Praise => PraiseLines[cue.Line], _ => HumorLines[cue.Line] };
            src.AsSpan().CopyTo(dst); n = src.Length;
        }
        _lineLen = n;
    }
}
