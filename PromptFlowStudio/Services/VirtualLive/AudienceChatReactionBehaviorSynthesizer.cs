namespace PromptFlowStudio.Services.VirtualLive;

/// <summary>v17.31.0 채팅 감정 극성.</summary>
public enum ChatPolarity : byte { Neutral, Positive, Question, Celebrate, Surprise }

/// <summary>v17.31.0 액터 리액션 제스처.</summary>
public enum ReactionGesture : byte { Idle, Nod, Wave, Wink, HeartHands, Clap, ThinkingPose, ReactionDance, Bow }

/// <summary>v17.31.0 시선 대상(v17.12 시선 리그 결선점).</summary>
public enum GazeTarget : byte { Camera, ChatPanel, Donor }

/// <summary>v17.31.0 채팅 피드 항목(문자열은 수신 원본 참조 — 복사 0).</summary>
public readonly record struct LiveChatEntry(long AtMs, string User, string Text, ChatPolarity Polarity, LiveChatVerdict Verdict, int Amount);

/// <summary>v17.31.0 슈퍼챗 리액션 큐 항목.</summary>
public readonly record struct SuperchatCue(long AtMs, string User, int Amount, int Tier, ReactionGesture Gesture, int DurationMs, int VoiceLine, bool Masked);

/// <summary>v17.31.0 프레임별 액터 리액션 출력(v17.12 시선 · 표정 블렌드셰이프 · v17.18 음성 대사 인덱스 · 제스처).</summary>
public struct ActorReactionState
{
    public GazeTarget Gaze;
    public float GazeYawDeg, GazePitchDeg;
    public float Smile, WinkL, WinkR, BrowRaise, MouthOpen;
    public float ArmL, ArmR, BodySwayDeg, BowDeg, DanceEnergy;
    public ReactionGesture Gesture;
    public float GesturePhase;
    public int VoiceLine;
}

/// <summary>
/// [PromptFlow Studio v17.31.0] 💬 시청자 채팅 감정 분석 &amp; 슈퍼챗 반응 행동 합성기(핫패스 무할당).
///   ▸ 토크나이저: 공백/구두점 경계 스팬 순회 → 한글 어간 접두 일치 · 영문 대소문자 무시 일치 · '?' '!' · ㅋㅋ/ㅎㅎ/ㄷㄷ 자모 신호 → 극성(긍정 · 질문 · 축하 · 놀람) 점수.
///   ▸ 집계: 극성별 지수 감쇠 카운터(τ = 5 s) · 초당 메시지 EWMA · 감정 온도(0~100) · 지배 무드.
///   ▸ 슈퍼챗: 금액 5단(&lt;1천 끄덕 · 1천 손인사 · 1만 윙크+하트 · 5만 리액션 댄스 · 10만 인사+댄스) → 고정 링 큐(5만↑ 우선 삽입) → 절차적 시퀀스(엔벨로프 · 120 BPM 박자).
///   ▸ 유해 판정 메시지는 집계 제외 · 유해 슈퍼챗은 금액 감사만 수행하고 본문 낭독 차단(Masked).
/// </summary>
public sealed class AudienceChatReactionBehaviorSynthesizer
{
    public const int FeedCap = 64, QueueCap = 64;
    public const float TauSec = 5f, DanceBpm = 120f;
    public static readonly int[] TierFloor = { 0, 1000, 10000, 50000, 100000 };
    public static readonly string[] VoiceLines =
    {
        "", "감사합니다!", "와, 정말 고마워요!", "하트 받으세요 💖", "대박! 춤 한번 갈게요!", "여러분 정말 사랑해요! 🙇",
        "좋은 질문이에요, 잠깐만요", "축하해요! 🎉", "헉, 진짜요?!",
    };

    private static readonly string[] PosKo = { "좋아", "좋다", "최고", "사랑", "귀여", "귀엽", "예쁘", "이쁘", "멋지", "멋있", "감사", "고마", "힐링", "행복", "잘한", "짱" };
    private static readonly string[] PosEn = { "love", "nice", "good", "great", "cute", "best", "lol", "awesome", "beautiful" };
    private static readonly string[] QKo = { "뭐", "왜", "어떻게", "언제", "어디", "누구", "몇", "질문", "궁금", "알려" };
    private static readonly string[] QEn = { "what", "why", "how", "when", "where", "who", "which" };
    private static readonly string[] CelKo = { "축하", "생일", "생축", "기념", "경축", "우승", "1등", "합격", "데뷔" };
    private static readonly string[] CelEn = { "congrats", "congratulations", "hbd", "birthday", "gg", "cheers" };
    private static readonly string[] SurKo = { "와", "헐", "대박", "미쳤", "실화", "놀라", "깜짝", "오오", "세상에" };
    private static readonly string[] SurEn = { "wow", "omg", "whoa", "insane", "unreal" };

    private readonly StreamSafetyAntiTrollingDivergenceGuard _guard;
    private readonly LiveChatEntry[] _feed = new LiveChatEntry[FeedCap];
    private int _feedHead, _feedCount;
    private readonly SuperchatCue[] _queue = new SuperchatCue[QueueCap];
    private int _qHead, _qCount;
    private readonly float[] _decay = new float[5];
    private readonly long[] _totals = new long[5];
    private SuperchatCue _active;
    private bool _hasActive;
    private long _activeStartMs, _lastTickMs = -1, _lastQuestionMs = -100000, _lastCelebrateMs = -100000, _lastSurpriseMs = -100000;
    private int _windowMsgs;
    private ActorReactionState _state;

    public AudienceChatReactionBehaviorSynthesizer(StreamSafetyAntiTrollingDivergenceGuard guard) => _guard = guard;

    public long Messages { get; private set; }
    public long Accepted { get; private set; }
    public long SuperchatCount { get; private set; }
    public long SuperchatAmount { get; private set; }
    public long CuesPlayed { get; private set; }
    public long QueueOverflows { get; private set; }
    public float MsgPerSec { get; private set; }
    public float Temperature { get; private set; }
    public ChatPolarity Mood { get; private set; }
    public double LastIngestUs { get; private set; }
    public double MaxIngestUs { get; private set; }
    public int QueueCount => _qCount;
    public bool HasActive => _hasActive;
    public SuperchatCue Active => _active;
    public float ActiveProgress { get; private set; }
    public ActorReactionState State => _state;

    public float Rate(ChatPolarity p) => _decay[(int)p] / TauSec;
    public long Total(ChatPolarity p) => _totals[(int)p];

    public static int TierOf(int amount)
    {
        var t = 0;
        for (var i = 0; i < TierFloor.Length; i++) if (amount >= TierFloor[i]) t = i;
        return t;
    }

    // ── 토크나이저(무할당) ──

    private static bool IsSep(char c) => char.IsWhiteSpace(c) || (char.IsPunctuation(c) && c != '?' && c != '!') || char.IsSymbol(c);

    private static bool AnyPrefix(ReadOnlySpan<char> tok, string[] kws)
    {
        foreach (var k in kws) if (tok.StartsWith(k.AsSpan(), StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool AnyEqual(ReadOnlySpan<char> tok, string[] kws)
    {
        foreach (var k in kws) if (tok.Equals(k.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>극성 점수 산출 → 지배 극성(동점 우선순위: 축하 &gt; 질문 &gt; 놀람 &gt; 긍정).</summary>
    public static ChatPolarity Classify(ReadOnlySpan<char> text, Span<float> scores)
    {
        scores.Clear();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSep(text[i])) i++;
            var s = i;
            while (i < text.Length && !IsSep(text[i])) i++;
            if (i == s) break;
            var tok = text[s..i];
            // 말미 ? ! 는 신호로 분리
            var end = tok.Length;
            while (end > 0 && (tok[end - 1] == '?' || tok[end - 1] == '!'))
            {
                scores[tok[end - 1] == '?' ? (int)ChatPolarity.Question : (int)ChatPolarity.Surprise] += tok[end - 1] == '?' ? 1f : 0.4f;
                end--;
            }
            tok = tok[..end];
            if (tok.Length == 0) continue;
            if (tok[0] >= 'ㄱ' && tok[0] <= 'ㆎ')
            {
                // 자모 반복 신호
                if (tok.Contains('ㅋ') || tok.Contains('ㅎ')) scores[(int)ChatPolarity.Positive] += 1f;
                if (tok.Contains('ㄷ')) scores[(int)ChatPolarity.Surprise] += 1f;
                if (tok.Contains('ㅊ') && tok.Contains('ㅋ')) scores[(int)ChatPolarity.Celebrate] += 1f;   // ㅊㅋ
                continue;
            }
            if (AnyPrefix(tok, CelKo) || AnyEqual(tok, CelEn)) scores[(int)ChatPolarity.Celebrate] += 1.5f;
            if (AnyPrefix(tok, QKo) || AnyEqual(tok, QEn)) scores[(int)ChatPolarity.Question] += 1f;
            if (AnyPrefix(tok, SurKo) || AnyEqual(tok, SurEn)) scores[(int)ChatPolarity.Surprise] += 1f;
            if (AnyPrefix(tok, PosKo) || AnyEqual(tok, PosEn)) scores[(int)ChatPolarity.Positive] += 1f;
        }
        var best = ChatPolarity.Neutral; var bv = 0.5f;
        ReadOnlySpan<ChatPolarity> order = stackalloc ChatPolarity[] { ChatPolarity.Celebrate, ChatPolarity.Question, ChatPolarity.Surprise, ChatPolarity.Positive };
        foreach (var p in order) if (scores[(int)p] > bv) { bv = scores[(int)p]; best = p; }
        return best;
    }

    // ── 수신 ──

    /// <summary>채팅/슈퍼챗 1건(무할당 — 문자열은 참조만 보관).</summary>
    public LiveChatVerdict Ingest(string user, string text, int amount, long nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Messages++;
        _windowMsgs++;
        var verdict = _guard.Screen(user, text, nowMs);
        Span<float> scores = stackalloc float[5];
        var pol = ChatPolarity.Neutral;
        if (verdict == LiveChatVerdict.Allow)
        {
            Accepted++;
            pol = Classify(text, scores);
            if (pol != ChatPolarity.Neutral) { _decay[(int)pol] += 1f; _totals[(int)pol]++; }
            if (pol == ChatPolarity.Question) _lastQuestionMs = nowMs;
            else if (pol == ChatPolarity.Celebrate) _lastCelebrateMs = nowMs;
            else if (pol == ChatPolarity.Surprise) _lastSurpriseMs = nowMs;
        }
        if (amount > 0) EnqueueSuperchat(user, amount, nowMs, verdict != LiveChatVerdict.Allow && verdict != LiveChatVerdict.Flood);
        _feed[(_feedHead + _feedCount) % FeedCap] = new LiveChatEntry(nowMs, user, text, pol, verdict, amount);
        if (_feedCount < FeedCap) _feedCount++; else _feedHead = (_feedHead + 1) % FeedCap;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastIngestUs = us;
        if (us > MaxIngestUs) MaxIngestUs = us;
        return verdict;
    }

    private void EnqueueSuperchat(string user, int amount, long nowMs, bool masked)
    {
        SuperchatCount++;
        SuperchatAmount += amount;
        var tier = TierOf(amount);
        var (g, dur, voice) = tier switch
        {
            0 => (ReactionGesture.Nod, 1200, 1),
            1 => (ReactionGesture.Wave, 2000, 1),
            2 => (ReactionGesture.HeartHands, 3000, 3),
            3 => (ReactionGesture.ReactionDance, 5000, 4),
            _ => (ReactionGesture.Bow, 8000, 5),
        };
        var cue = new SuperchatCue(nowMs, user, amount, tier, g, dur, voice, masked);
        _decay[(int)ChatPolarity.Celebrate] += 1f + tier;   // 후원은 온도 가산
        if (_qCount == QueueCap) { QueueOverflows++; return; }
        if (tier >= 3)
        {
            // 고액 우선: 큐 앞쪽(같은 등급 이상 뒤)으로 삽입
            var pos = 0;
            while (pos < _qCount && _queue[(_qHead + pos) % QueueCap].Tier >= tier) pos++;
            for (var k = _qCount; k > pos; k--) _queue[(_qHead + k) % QueueCap] = _queue[(_qHead + k - 1) % QueueCap];
            _queue[(_qHead + pos) % QueueCap] = cue;
        }
        else _queue[(_qHead + _qCount) % QueueCap] = cue;
        _qCount++;
    }

    public int CopyFeed(Span<LiveChatEntry> dst)
    {
        var n = Math.Min(dst.Length, _feedCount);
        for (var i = 0; i < n; i++) dst[i] = _feed[(_feedHead + _feedCount - 1 - i) % FeedCap];   // 최신 우선
        return n;
    }

    public int CopyQueue(Span<SuperchatCue> dst)
    {
        var n = Math.Min(dst.Length, _qCount);
        for (var i = 0; i < n; i++) dst[i] = _queue[(_qHead + i) % QueueCap];
        return n;
    }

    // ── 틱: 집계 감쇠 → 큐 진행 → 절차적 행동 합성 ──

    public void Tick(long nowMs)
    {
        var dt = _lastTickMs < 0 ? 0f : (nowMs - _lastTickMs) / 1000f;
        _lastTickMs = nowMs;
        if (dt > 0f)
        {
            var k = MathF.Exp(-dt / TauSec);
            for (var i = 0; i < _decay.Length; i++) _decay[i] *= k;
            MsgPerSec += (_windowMsgs / dt - MsgPerSec) * MathF.Min(1f, dt / 1f);
            _windowMsgs = 0;
        }
        var energy = Rate(ChatPolarity.Positive) + 1.5f * Rate(ChatPolarity.Celebrate) + 1.2f * Rate(ChatPolarity.Surprise) + 0.5f * Rate(ChatPolarity.Question);
        Temperature = 100f * (1f - MathF.Exp(-energy / 20f));
        var mood = ChatPolarity.Neutral; var mv = 0.2f;
        for (var i = 1; i < _decay.Length; i++) if (_decay[i] > mv) { mv = _decay[i]; mood = (ChatPolarity)i; }
        Mood = mood;

        if (_hasActive && nowMs - _activeStartMs >= _active.DurationMs) { _hasActive = false; CuesPlayed++; }
        if (!_hasActive && _qCount > 0)
        {
            _active = _queue[_qHead];
            _qHead = (_qHead + 1) % QueueCap; _qCount--;
            _hasActive = true;
            _activeStartMs = nowMs;
        }
        Synthesize(nowMs);
    }

    private static float Env(float t, float attack = 0.12f, float release = 0.2f) =>
        MathF.Max(0f, MathF.Min(1f, MathF.Min(t / attack, (1f - t) / release)));

    private void Synthesize(long nowMs)
    {
        ref var s = ref _state;
        var sec = nowMs / 1000f;
        // 기본 아이들: 무드 기반 미소 · 호흡 스웨이
        s = default;
        s.Gaze = GazeTarget.Camera;
        s.Smile = 0.25f + 0.5f * Temperature / 100f;
        s.BodySwayDeg = 1.5f * MathF.Sin(sec * 1.3f);
        s.ArmL = s.ArmR = 0.05f;

        if (_hasActive)
        {
            var t = Math.Clamp((nowMs - _activeStartMs) / (float)_active.DurationMs, 0f, 1f);
            ActiveProgress = t;
            var e = Env(t);
            s.Gesture = _active.Gesture;
            s.GesturePhase = t;
            s.VoiceLine = _active.Masked ? 1 : _active.VoiceLine;
            s.Gaze = t < 0.25f ? GazeTarget.Donor : GazeTarget.Camera;
            s.Smile = MathF.Max(s.Smile, 0.6f + 0.4f * e);
            var beat = sec * DanceBpm / 60f * MathF.Tau;
            switch (_active.Gesture)
            {
                case ReactionGesture.Nod:
                    s.GazePitchDeg = -10f * e * MathF.Max(0f, MathF.Sin(t * MathF.Tau * 2f));
                    break;
                case ReactionGesture.Wave:
                    s.ArmR = 0.75f * e + 0.2f * e * MathF.Sin(sec * MathF.Tau * 2.5f);
                    break;
                case ReactionGesture.HeartHands:
                    s.ArmL = s.ArmR = 0.9f * e;
                    s.WinkR = t is > 0.35f and < 0.6f ? MathF.Sin((t - 0.35f) / 0.25f * MathF.PI) : 0f;
                    break;
                case ReactionGesture.ReactionDance:
                case ReactionGesture.Bow:
                    var danceT = _active.Gesture == ReactionGesture.Bow ? Math.Clamp((t - 0.3f) / 0.7f, 0f, 1f) : t;
                    var de = _active.Gesture == ReactionGesture.Bow ? Env(danceT, 0.1f, 0.15f) : e;
                    s.DanceEnergy = de;
                    s.BodySwayDeg = 12f * de * MathF.Sin(beat * 0.5f);
                    s.ArmL = 0.5f * de + 0.4f * de * MathF.Max(0f, MathF.Sin(beat));
                    s.ArmR = 0.5f * de + 0.4f * de * MathF.Max(0f, -MathF.Sin(beat));
                    s.MouthOpen = 0.3f * de;
                    if (_active.Gesture == ReactionGesture.Bow && t < 0.3f) s.BowDeg = 35f * MathF.Sin(t / 0.3f * MathF.PI);
                    break;
            }
            return;
        }
        ActiveProgress = 0f;

        // 큐가 비면 채팅 무드 리액션(최근 1.5 s 신호)
        if (nowMs - _lastQuestionMs < 1500)
        {
            s.Gesture = ReactionGesture.ThinkingPose;
            s.Gaze = GazeTarget.ChatPanel;
            s.GazeYawDeg = 25f; s.GazePitchDeg = -8f;
            s.ArmR = 0.55f; s.BrowRaise = 0.4f;
            s.VoiceLine = 6;
        }
        else if (nowMs - _lastCelebrateMs < 1500)
        {
            s.Gesture = ReactionGesture.Clap;
            s.ArmL = s.ArmR = 0.55f + 0.15f * MathF.Max(0f, MathF.Sin(sec * MathF.Tau * 3f));
            s.Smile = 1f;
            s.VoiceLine = 7;
        }
        else if (nowMs - _lastSurpriseMs < 1000)
        {
            s.BrowRaise = 0.9f; s.MouthOpen = 0.6f;
            s.VoiceLine = 8;
        }
    }

    public void ResetMetrics()
    {
        Messages = 0; Accepted = 0; SuperchatCount = 0; SuperchatAmount = 0; CuesPlayed = 0; QueueOverflows = 0; MaxIngestUs = 0;
        Array.Clear(_totals);
    }
}
