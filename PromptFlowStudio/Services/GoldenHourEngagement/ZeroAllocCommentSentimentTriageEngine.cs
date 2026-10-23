namespace PromptFlowStudio.Services.GoldenHourEngagement;

public enum CommentSentiment : byte { Neutral, Positive, Question, Negative, Spam }

[Flags]
public enum CommentHideReason : byte { None = 0, LinkFlood = 1, Profanity = 2, Troll = 4, CharFlood = 8, Scam = 16 }

/// <summary>댓글 1 건 스코어 — 값 타입 · 어휘 히트 수 기반 정수 점수.</summary>
public struct CommentTriageResult
{
    public CommentSentiment Sentiment;
    public CommentHideReason Hide;
    public int Positive, Question, Negative, Spam, Links, Profanity;
    public readonly bool ShouldHide => Hide != CommentHideReason.None;
}

/// <summary>플랫폼 댓글 조작 게이트웨이 — 반환 = HTTP 상태 코드. (YouTube <c>comments.setModerationStatus</c> · IG <c>POST /{comment-id}?hide=true</c> 계약)</summary>
public interface ICommentModerationGateway
{
    int HideComment(long commentId, CommentHideReason reason);
    int PostReply(long parentCommentId, ReadOnlySpan<char> text);
    int PinComment(ReadOnlySpan<char> text);
}

/// <summary>인메모리 모의 댓글 플랫폼 — 숨김/답글/고정 카운터 · 거절 상태 주입(1 회 소모).</summary>
public sealed class SimulatedCommentModerationGateway : ICommentModerationGateway
{
    private int _failNext, _failStatus;

    public long Hides { get; private set; }
    public long Replies { get; private set; }
    public long Pins { get; private set; }
    public long Calls { get; private set; }
    public int LastReplyLength { get; private set; }

    public void InjectFailures(int count, int status) { _failNext = Math.Max(0, count); _failStatus = status; }
    public void Reset() { Hides = Replies = Pins = Calls = 0; _failNext = 0; LastReplyLength = 0; }

    public int HideComment(long commentId, CommentHideReason reason)
    {
        Calls++;
        if (_failNext > 0) { _failNext--; return _failStatus; }
        Hides++;
        return 200;
    }

    public int PostReply(long parentCommentId, ReadOnlySpan<char> text)
    {
        Calls++;
        if (_failNext > 0) { _failNext--; return _failStatus; }
        if (text.IsEmpty) return 400;
        Replies++;
        LastReplyLength = text.Length;
        return 200;
    }

    public int PinComment(ReadOnlySpan<char> text)
    {
        Calls++;
        if (_failNext > 0) { _failNext--; return _failStatus; }
        if (text.IsEmpty) return 400;
        Pins++;
        return 200;
    }
}

/// <summary>
/// [PromptFlow Studio v17.95.0] 💬 무할당 실시간 댓글 감정 분석 &amp; 스팸 필터.
///   ▸ 댓글 버퍼를 <see cref="ReadOnlySpan{T}"/> 로 스캔 — 정적 어휘 사전(긍정 · 의문 · 부정 · 욕설 · 트롤 · 스캠) OrdinalIgnoreCase 히트 수 + 링크 · 동일 문자 도배 런 길이.
///   ▸ 외부 링크 도배(2+ 또는 스캠 어휘 동반) · 욕설 · 트롤 · 문자 도배 · 스캠 2+ → <see cref="ICommentModerationGateway.HideComment"/> 즉시 숨김.
///   ▸ 이미 숨긴 댓글 ID 는 64 링으로 중복 차단 · 스캔/분류/카운터 경로 힙 할당 0.
/// </summary>
public sealed class ZeroAllocCommentSentimentTriageEngine
{
    public const int HiddenRing = 64, FloodRun = 12, LaughFloodRun = 30, SpamThreshold = 3;

    private static readonly string[] PositiveTerms = { "좋아", "좋네", "최고", "대박", "감사", "고마", "멋지", "멋져", "짱", "굿", "사랑", "응원", "재밌", "꿀팁", "유익", "ㅋㅋ", "👍", "❤", "🔥", "love", "great", "awesome", "amazing", "nice", "thanks" };
    private static readonly string[] QuestionTerms = { "?", "？", "어떻게", "언제", "어디", "무슨", "뭔가요", "뭐예요", "뭐에요", "인가요", "나요", "까요", "알려주", "궁금", "how", "what", "when", "where", "which", "why" };
    private static readonly string[] NegativeTerms = { "별로", "싫", "노잼", "실망", "최악", "지루", "아쉽", "구리", "bad", "boring", "worst", "hate" };
    private static readonly string[] ProfanityTerms = { "시발", "씨발", "ㅅㅂ", "ㅆㅂ", "병신", "ㅂㅅ", "개새", "존나", "fuck", "shit", "bitch" };
    private static readonly string[] TrollTerms = { "구독취소", "구취", "꺼져", "망해라", "이딴", "신고함", "쓰레기", "어그로" };
    private static readonly string[] ScamTerms = { "부업", "수익 보장", "월 500", "무료 상담", "코인 리딩", "카톡", "텔레그램", "맞구독", "선착순", "클릭", "dm me", "free money" };
    private static readonly string[] LinkTerms = { "http://", "https://", "www.", "bit.ly", "t.me/", "open.kakao", ".com/", ".kr/", ".xyz" };

    private readonly ICommentModerationGateway _gateway;
    private readonly long[] _hidden = new long[HiddenRing];
    private readonly long[] _bySentiment = new long[5];
    private int _hiddenHead, _hiddenCount;

    public ZeroAllocCommentSentimentTriageEngine(ICommentModerationGateway gateway) => _gateway = gateway;

    public long Scanned { get; private set; }
    public long Hidden { get; private set; }
    public long HideFailures { get; private set; }
    public long DuplicateSkips { get; private set; }
    public int LastHideStatus { get; private set; }
    public long Count(CommentSentiment s) => _bySentiment[(int)s];

    public static string SentimentName(CommentSentiment s) => s switch
    {
        CommentSentiment.Positive => "긍정",
        CommentSentiment.Question => "질문",
        CommentSentiment.Negative => "부정",
        CommentSentiment.Spam => "스팸",
        _ => "중립",
    };

    public static string HideReasonText(CommentHideReason r)
    {
        if (r == CommentHideReason.None) return "-";
        if ((r & CommentHideReason.LinkFlood) != 0) return "링크 도배";
        if ((r & CommentHideReason.Scam) != 0) return "스캠";
        if ((r & CommentHideReason.Profanity) != 0) return "욕설";
        if ((r & CommentHideReason.Troll) != 0) return "트롤링";
        return "문자 도배";
    }

    /// <summary>순수 스코어링(부작용 없음 · 무할당).</summary>
    public static CommentTriageResult Score(ReadOnlySpan<char> text)
    {
        var r = new CommentTriageResult
        {
            Positive = Hits(text, PositiveTerms),
            Question = Hits(text, QuestionTerms),
            Negative = Hits(text, NegativeTerms),
            Profanity = Hits(text, ProfanityTerms),
            Links = Hits(text, LinkTerms),
        };
        var troll = Hits(text, TrollTerms);
        var scam = Hits(text, ScamTerms);
        var flood = IsFlood(text);

        r.Spam = r.Links * 2 + scam * 2 + (flood ? SpamThreshold : 0);
        r.Negative += r.Profanity * 3 + troll * 2;
        if (r.Links >= 2 || r.Links > 0 && scam > 0) r.Hide |= CommentHideReason.LinkFlood;
        if (scam >= 2) r.Hide |= CommentHideReason.Scam;
        if (r.Profanity > 0) r.Hide |= CommentHideReason.Profanity;
        if (troll > 0) r.Hide |= CommentHideReason.Troll;
        if (flood) r.Hide |= CommentHideReason.CharFlood;

        r.Sentiment = r.Spam >= SpamThreshold ? CommentSentiment.Spam
            : r.Negative >= 2 || r.Profanity > 0 || r.Negative > r.Positive && r.Negative > 0 ? CommentSentiment.Negative
            : r.Question > 0 ? CommentSentiment.Question
            : r.Positive > 0 ? CommentSentiment.Positive
            : CommentSentiment.Neutral;
        return r;
    }

    /// <summary>분류 + 카운터 + (해당 시) 게이트웨이 숨김. 핫패스 — 무할당.</summary>
    public CommentTriageResult Triage(long commentId, ReadOnlySpan<char> text)
    {
        var r = Score(text);
        Scanned++;
        _bySentiment[(int)r.Sentiment]++;
        if (!r.ShouldHide) return r;
        if (AlreadyHidden(commentId)) { DuplicateSkips++; return r; }
        var st = 0;
        try { st = _gateway.HideComment(commentId, r.Hide); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("GoldenHourEngagement.Hide", ex); st = 599; }
        LastHideStatus = st;
        if (st is >= 200 and < 300)
        {
            Hidden++;
            _hidden[_hiddenHead] = commentId;
            _hiddenHead = (_hiddenHead + 1) % HiddenRing;
            if (_hiddenCount < HiddenRing) _hiddenCount++;
        }
        else HideFailures++;
        return r;
    }

    public bool AlreadyHidden(long commentId)
    {
        for (var i = 0; i < _hiddenCount; i++) if (_hidden[i] == commentId) return true;
        return false;
    }

    public void Reset()
    {
        Array.Clear(_hidden);
        Array.Clear(_bySentiment);
        _hiddenHead = _hiddenCount = 0;
        Scanned = Hidden = HideFailures = DuplicateSkips = 0;
        LastHideStatus = 0;
    }

    private static int Hits(ReadOnlySpan<char> s, string[] terms)
    {
        var n = 0;
        foreach (var t in terms)
        {
            var rest = s;
            int i;
            while ((i = rest.IndexOf(t.AsSpan(), StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                n++;
                rest = rest[(i + t.Length)..];
            }
        }
        return n;
    }

    /// <summary>공백 제외 동일 문자 연속 도배 — 웃음/울음 자모(ㅋ ㅎ ㅠ ㅜ)는 <see cref="LaughFloodRun"/> 까지 허용.</summary>
    private static bool IsFlood(ReadOnlySpan<char> s)
    {
        var run = 0;
        var prev = '\0';
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            run = ch == prev ? run + 1 : 1;
            prev = ch;
            if (run >= (ch is 'ㅋ' or 'ㅎ' or 'ㅠ' or 'ㅜ' ? LaughFloodRun : FloodRun)) return true;
        }
        return false;
    }
}
