using PromptFlowStudio.Services.InfluencerLive;
using PromptFlowStudio.Services.StreamGateway;

namespace PromptFlowStudio.Services.SemanticChat;

public enum CrowdEmotion : byte { Hype, Joy, Sadness, Anger }

/// <summary>v17.72.0 4 차원 군중 감성 벡터(0~1 · 메시지 비율).</summary>
public struct SentimentVector
{
    public float Hype, Joy, Sadness, Anger;
    public float this[int i] { readonly get => i switch { 0 => Hype, 1 => Joy, 2 => Sadness, _ => Anger }; set { switch (i) { case 0: Hype = value; break; case 1: Joy = value; break; case 2: Sadness = value; break; default: Anger = value; break; } } }
    public readonly CrowdEmotion Dominant { get { var b = 0; for (var i = 1; i < 4; i++) if (this[i] > this[b]) b = i; return (CrowdEmotion)b; } }
    public readonly float Max => MathF.Max(MathF.Max(Hype, Joy), MathF.Max(Sadness, Anger));
}

/// <summary>v17.72.0 표시용 채팅 피드 항목(블라인드 처리 반영).</summary>
public struct ChatFeedEntry
{
    public long AtMs;
    public ChatPlatform Platform;
    public ChatSafetyVerdict Verdict;
    public sbyte Emotion;   // −1 = 중립
    public int Amount, UserLen, TextLen;
}

/// <summary>
/// [PromptFlow Studio v17.72.0] 🧠 로컬 시맨틱 감성 분석기 &amp; 톡시시티 필터(<see cref="IUnifiedChatConsumer"/> · 무할당).
///   ▸ 톡시시티: v17.61 <see cref="ChatSpamToxicitySafetyGuard"/>(다국어 금칙어 정규화 · 봇 누출 버킷 · 도배 · 레이드 · 섀도우밴) 판정 → 차단분은 본문 블라인드(●) ·
///     독성/도배/봇/섀도우밴 = 감성 제외 · 레이드(동일 문구 합창) = 표시만 블라인드 · 감성 반영 · 톡시시티 비율 = 독성 + 섀도우밴.
///   ▸ 감성: 정규화(소문자 · 반복 문자 3+ → 2) 본문에 감성 사전(한 · 영 · 이모지 · 자모 반복 ㅋㅋ/ㅠㅠ · 느낌표 강세) 부분 일치 점수 → 부정어(안/못/not/no) 직전 2 토큰이면 Joy ↔ Sadness 반전 ·
///     메시지 = 최대 감정 1 표(동점 = 강세 우선 Hype) · 후원 메시지 가중 2.
///   ▸ 슬라이딩 창: 100 ms × 50 버킷 = 5 s · 감정별 가중 합 / 감성 표 총합 → 정규화 4 차원 벡터(0~1) + 강도(감성 msg/s) · 무감성 메시지 비율.
/// </summary>
public sealed class SemanticSentimentToxicityAnalyzer : IUnifiedChatConsumer
{
    public const int Emotions = 4, Buckets = 50, BucketMs = 100, WindowMs = Buckets * BucketMs, NormCap = 256, FeedCap = 64, FeedUser = 24, FeedText = 120;

    /// <summary>감성 사전(정규형 부분 일치 · 가중치) — 감정별.</summary>
    public static readonly (string Term, float W)[][] Lexicon =
    {
        new[] { ("가즈아", 2f), ("가자", 1f), ("미쳤", 1.5f), ("레전드", 2f), ("대박", 1.5f), ("개쩐", 1.5f), ("ㄷㄷ", 1f), ("와아", 1f), ("우와", 1f), ("소름", 1.2f), ("드가자", 1.5f), ("pog", 2f), ("hype", 2f), ("letsgo", 2f), ("lets go", 2f), ("insane", 1.5f), ("omg", 1f), ("🔥", 1.5f), ("🚀", 1.5f), ("🎉", 1.2f), ("!!!", 1f) },
        new[] { ("ㅋㅋ", 1f), ("ㅎㅎ", 1f), ("웃기", 1.2f), ("귀여", 1.5f), ("좋아", 1.2f), ("행복", 1.5f), ("사랑", 1.5f), ("최고", 1.2f), ("감사", 1f), ("고마", 1f), ("힐링", 1.2f), ("lol", 1f), ("lmao", 1.2f), ("haha", 1f), ("love", 1.2f), ("cute", 1.2f), ("😂", 1.2f), ("❤", 1.5f), ("😍", 1.5f), ("😊", 1.2f) },
        new[] { ("ㅠㅠ", 1.5f), ("ㅜㅜ", 1.5f), ("슬프", 1.5f), ("슬퍼", 1.5f), ("아쉽", 1.2f), ("아쉬", 1.2f), ("눈물", 1.5f), ("우울", 1.5f), ("힘들", 1.2f), ("보고싶", 1f), ("가지마", 1.5f), ("sad", 1.5f), ("cry", 1.2f), ("miss", 1f), ("sorry", 0.8f), ("😢", 1.5f), ("😭", 1.5f), ("💔", 1.5f) },
        new[] { ("화나", 1.5f), ("짜증", 1.5f), ("열받", 1.5f), ("빡치", 1.5f), ("빡쳐", 1.5f), ("최악", 1.5f), ("어이없", 1.2f), ("뭐하냐", 1f), ("장난하", 1.2f), ("환불", 1.2f), ("렉", 0.8f), ("angry", 1.5f), ("wtf", 1.5f), ("worst", 1.5f), ("trash", 1.2f), ("scam", 1.2f), ("😡", 1.5f), ("🤬", 1.8f), ("👎", 1f) },
    };
    /// <summary>앞 부정어(전치) — 안/못/not … · 뒤 부정(후치 · 6 자 내 않/없/못)은 별도 판정.</summary>
    private static readonly string[] Negators = { "안 ", "못 ", "not ", "no ", "dont", "don't", "never" };

    private readonly ChatSpamToxicitySafetyGuard _guard;
    private readonly char[] _norm = new char[NormCap];
    private readonly float[] _bin = new float[Buckets * (Emotions + 1)];
    private readonly long[] _binStamp = new long[Buckets];
    private readonly int[] _toxBin = new int[Buckets], _msgBin = new int[Buckets];
    private readonly ChatFeedEntry[] _feed = new ChatFeedEntry[FeedCap];
    private readonly char[] _feedUser = new char[FeedCap * FeedUser], _feedText = new char[FeedCap * FeedText];
    private readonly long[] _emotionTotal = new long[Emotions];
    private int _feedHead, _feedN;
    private SentimentVector _vec;

    public SemanticSentimentToxicityAnalyzer(ChatSpamToxicitySafetyGuard? guard = null) => _guard = guard ?? new ChatSpamToxicitySafetyGuard();

    public ChatSpamToxicitySafetyGuard Guard => _guard;
    public long Consumed { get; private set; }
    public long Blinded { get; private set; }
    public long Toxic { get; private set; }
    public long Neutral { get; private set; }
    public long Scored { get; private set; }
    public long EmotionTotal(CrowdEmotion e) => _emotionTotal[(int)e];
    public SentimentVector Vector => _vec;
    /// <summary>창 내 감성 표 msg/s.</summary>
    public float Intensity { get; private set; }
    public float ToxicityRatio { get; private set; }
    public float MessagesPerSec { get; private set; }
    public double LastConsumeUs { get; private set; }
    public double MaxConsumeUs { get; private set; }
    public int FeedCount => _feedN;
    public ref readonly ChatFeedEntry Feed(int i) => ref _feed[(_feedHead - 1 - i + 2 * FeedCap) % FeedCap];
    public ReadOnlySpan<char> FeedUserText(int i) { var k = (_feedHead - 1 - i + 2 * FeedCap) % FeedCap; return _feedUser.AsSpan(k * FeedUser, _feed[k].UserLen); }
    public ReadOnlySpan<char> FeedBody(int i) { var k = (_feedHead - 1 - i + 2 * FeedCap) % FeedCap; return _feedText.AsSpan(k * FeedText, _feed[k].TextLen); }

    public static string EmotionName(CrowdEmotion e) => e switch { CrowdEmotion.Hype => "열광", CrowdEmotion.Joy => "즐거움", CrowdEmotion.Sadness => "슬픔", _ => "분노" };
    public static string EmotionIcon(CrowdEmotion e) => e switch { CrowdEmotion.Hype => "🔥", CrowdEmotion.Joy => "😊", CrowdEmotion.Sadness => "😢", _ => "😡" };

    // ══════════════════════ 메시지 1 건(무할당) ══════════════════════
    public void Consume(in UnifiedChatRecord rec, ReadOnlySpan<char> user, ReadOnlySpan<char> text)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Consumed++;
        var v = _guard.Inspect(user, text, rec.AtMs);
        var bi = Bin(rec.AtMs);
        _msgBin[bi]++;
        sbyte emo = -1;
        if (v != ChatSafetyVerdict.Clean) { Blinded++; if (v is ChatSafetyVerdict.Toxic or ChatSafetyVerdict.Shadowbanned) { _toxBin[bi]++; Toxic++; } }
        // 감성 집계: 정상 + 레이드(동일 문구 군중 합창 = 동조 신호 · 표시만 블라인드) — 독성 · 도배 · 봇 · 섀도우밴 제외
        if (v is ChatSafetyVerdict.Clean or ChatSafetyVerdict.Raid)
        {
            emo = (sbyte)Classify(text, out var strength);
            if (emo < 0) Neutral++;
            else
            {
                var w = rec.Amount > 0 ? 2f : 1f;
                _bin[bi * (Emotions + 1) + emo] += w;
                _bin[bi * (Emotions + 1) + Emotions] += w;
                _emotionTotal[emo]++; Scored++;
            }
        }
        // 피드(블라인드 = 본문 마스킹)
        ref var f = ref _feed[_feedHead];
        f.AtMs = rec.AtMs; f.Platform = rec.Platform; f.Verdict = v; f.Emotion = emo; f.Amount = rec.Amount;
        f.UserLen = Math.Min(user.Length, FeedUser); user[..f.UserLen].CopyTo(_feedUser.AsSpan(_feedHead * FeedUser));
        var ft = _feedText.AsSpan(_feedHead * FeedText, FeedText);
        if (v == ChatSafetyVerdict.Clean) { f.TextLen = Math.Min(text.Length, FeedText); text[..f.TextLen].CopyTo(ft); }
        else { f.TextLen = Math.Min(Math.Max(3, Math.Min(text.Length, 12)), FeedText); ft[..f.TextLen].Fill('●'); }
        _feedHead = (_feedHead + 1) % FeedCap; if (_feedN < FeedCap) _feedN++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastConsumeUs = us; if (us > MaxConsumeUs && Consumed > 8) MaxConsumeUs = us;
    }

    private int Bin(long nowMs)
    {
        var b = nowMs / BucketMs; var bi = (int)(b % Buckets);
        if (_binStamp[bi] != b)
        {
            _binStamp[bi] = b; _toxBin[bi] = 0; _msgBin[bi] = 0;
            _bin.AsSpan(bi * (Emotions + 1), Emotions + 1).Clear();
        }
        return bi;
    }

    /// <summary>감성 분류(무할당) — 최대 점수 감정 · 없음 = −1.</summary>
    public int Classify(ReadOnlySpan<char> text, out float strength)
    {
        var n = NormalizeLower(text, _norm);
        ReadOnlySpan<char> s = _norm.AsSpan(0, n);
        Span<float> sc = stackalloc float[Emotions];
        sc.Clear();
        for (var e = 0; e < Emotions; e++)
        {
            var lex = Lexicon[e];
            for (var k = 0; k < lex.Length; k++)
            {
                var at = s.IndexOf(lex[k].Term.AsSpan(), StringComparison.Ordinal);
                if (at < 0) continue;
                var w = lex[k].W;
                // 부정어 직전(최대 6 자 앞) → Joy ↔ Sadness 반전 · Hype 약화
                var neg = false;
                var from = Math.Max(0, at - 6);
                var pre = s[from..at];
                for (var q = 0; q < Negators.Length && !neg; q++) if (pre.IndexOf(Negators[q].AsSpan(), StringComparison.Ordinal) >= 0) neg = true;
                if (!neg && at + lex[k].Term.Length < s.Length)
                {
                    // 후치 부정(…지 않아 · …하지 않 · …지 마 · 없어): 다음 공백 단위 2 어절(≤ 6 자) 안
                    var post = s[(at + lex[k].Term.Length)..]; post = post[..Math.Min(post.Length, 6)];
                    if (post.IndexOf('않') >= 0 || post.IndexOf('없') >= 0 || post.IndexOf("지 마".AsSpan(), StringComparison.Ordinal) >= 0 || post.IndexOf("지마".AsSpan(), StringComparison.Ordinal) >= 0) neg = true;
                }
                if (neg && e == (int)CrowdEmotion.Joy) sc[(int)CrowdEmotion.Sadness] += w * 0.8f;
                else if (neg && e == (int)CrowdEmotion.Sadness) sc[(int)CrowdEmotion.Joy] += w * 0.5f;
                else if (neg) sc[e] += w * 0.3f;
                else sc[e] += w;
            }
        }
        // 느낌표 강세 → 열광 · 분노 증폭
        var bangs = 0; foreach (var c in s) if (c == '!') bangs++;
        if (bangs >= 2) { sc[(int)CrowdEmotion.Hype] *= 1.3f; sc[(int)CrowdEmotion.Anger] *= 1.3f; }
        var best = -1; var bv = 0.5f;
        for (var e = 0; e < Emotions; e++) if (sc[e] > bv) { bv = sc[e]; best = e; }
        strength = best < 0 ? 0 : bv;
        return best;
    }

    /// <summary>소문자 · 3 회 이상 반복 문자 → 2 회 축약(ㅋㅋㅋㅋ → ㅋㅋ · !!!! → !!) · 제로폭 제거.</summary>
    public static int NormalizeLower(ReadOnlySpan<char> s, Span<char> d)
    {
        var n = 0; var run = 0; var prev = '\0';
        for (var i = 0; i < s.Length && n < d.Length; i++)
        {
            var c = char.ToLowerInvariant(s[i]);
            if (c is >= '​' and <= '‏' or '﻿') continue;
            if (c == prev) { if (++run >= 2 && c != '!') continue; if (c == '!' && run >= 3) continue; }
            else run = 0;
            d[n++] = c; prev = c;
        }
        return n;
    }

    // ══════════════════════ 창 집계(틱 · 무할당) ══════════════════════
    public SentimentVector Step(long nowMs)
    {
        Span<float> sum = stackalloc float[Emotions + 1];
        sum.Clear();
        int tox = 0, msgs = 0;
        var cur = nowMs / BucketMs;
        for (var i = 0; i < Buckets; i++)
        {
            if (cur - _binStamp[i] >= Buckets || _binStamp[i] > cur) continue;
            for (var e = 0; e <= Emotions; e++) sum[e] += _bin[i * (Emotions + 1) + e];
            tox += _toxBin[i]; msgs += _msgBin[i];
        }
        var total = sum[Emotions];
        for (var e = 0; e < Emotions; e++) _vec[e] = total > 0 ? sum[e] / total : 0f;
        Intensity = total / (WindowMs / 1000f);
        MessagesPerSec = msgs / (WindowMs / 1000f);
        ToxicityRatio = msgs > 0 ? tox / (float)msgs : 0f;
        return _vec;
    }

    public void Reset()
    {
        Array.Clear(_bin); Array.Clear(_binStamp); Array.Clear(_toxBin); Array.Clear(_msgBin); Array.Clear(_emotionTotal);
        _vec = default; _feedN = 0; Consumed = Blinded = Toxic = Neutral = Scored = 0; MaxConsumeUs = 0; Intensity = ToxicityRatio = MessagesPerSec = 0;
    }
}
