using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.LiveStream;

public enum ChatEmotion : byte { Neutral, Joy, Hype, Surprise, Sad, Angry }
public enum ChatVerdict : byte { Accepted, RateLimited, Blocked, Empty }
public enum AnchorMotion : byte { Idle, Nod, Laugh, Cheer, Dance, Surprise, Sad, Calm }

/// <summary>디렉터 출력 스냅샷.</summary>
public readonly record struct DirectorState(float Hype, float Valence, float MsgPerSec, float DonationPerMin, float BgmBpm, float LightHue, float LightSat, float LightLevel,
    AnchorMotion Motion, ChatEmotion Dominant, int WorldBranch, bool PollOpen, long Accepted, long Dropped);

/// <summary>채팅 피드(UI) 항목.</summary>
public readonly record struct ChatItem(long AtMs, string User, string Text, ChatEmotion Emotion, ChatVerdict Verdict, int Donation);

/// <summary>
/// [PromptFlow Studio v16.71.0] 시청자 인터랙티브 피드백 &amp; 동적 장면 디렉터.
/// 채팅(어휘 · 이모지 · 반복 문자 감정 분류 · 유해어 차단 · 사용자별 토큰 버킷) · 후원 이모티콘(금액 로그 가중) · 실시간 투표(사용자 해시 중복 차단 · 정족수)를
/// 지수 감쇠 누적기로 집계해 열기(hype) · 정서(valence)를 산출하고, 감도 계수로 스케일한 뒤
/// 4D 월드 분기(투표 승자) · BGM 템포(슬루 제한) · 조명 색조/채도/밝기(최단 각 경로 슬루) · 앵커 카툰 모션(지배 감정 · 쿨다운) · 카메라 바이어스를 출력.
/// 수신/틱 경로는 고정 크기 해시 테이블 · 링 버퍼만 사용(무할당).
/// </summary>
public sealed class InteractiveStreamViewerFeedbackDirector
{
    public const int UserSlots = 2048, FeedSize = 64, MaxOptions = 6;
    public static readonly string[] WorldBranches = { "네온 사이버 시티", "심해 유적", "천공의 성", "사막 폭풍", "벚꽃 정원", "우주 정거장" };
    public static readonly string[] Emotes = { "🔥", "😂", "😮", "😢", "❤", "👏", "🎉", "😡" };

    private static readonly (string Word, ChatEmotion E)[] Lexicon =
    {
        ("ㅋㅋ", ChatEmotion.Joy), ("ㅎㅎ", ChatEmotion.Joy), ("웃기", ChatEmotion.Joy), ("귀엽", ChatEmotion.Joy), ("lol", ChatEmotion.Joy), ("haha", ChatEmotion.Joy), ("좋아", ChatEmotion.Joy),
        ("대박", ChatEmotion.Hype), ("미쳤", ChatEmotion.Hype), ("가즈아", ChatEmotion.Hype), ("레전드", ChatEmotion.Hype), ("최고", ChatEmotion.Hype), ("pog", ChatEmotion.Hype), ("hype", ChatEmotion.Hype), ("gg", ChatEmotion.Hype),
        ("헐", ChatEmotion.Surprise), ("와", ChatEmotion.Surprise), ("실화", ChatEmotion.Surprise), ("wow", ChatEmotion.Surprise), ("omg", ChatEmotion.Surprise),
        ("ㅠㅠ", ChatEmotion.Sad), ("슬프", ChatEmotion.Sad), ("눈물", ChatEmotion.Sad), ("sad", ChatEmotion.Sad), ("아쉽", ChatEmotion.Sad),
        ("노잼", ChatEmotion.Angry), ("별로", ChatEmotion.Angry), ("지루", ChatEmotion.Angry), ("boring", ChatEmotion.Angry), ("짜증", ChatEmotion.Angry),
    };
    private static readonly string[] Blocklist = { "씨발", "시발", "병신", "fuck", "shit", "nigg" };

    private readonly object _gate = new();
    private readonly ulong[] _userKey = new ulong[UserSlots];
    private readonly float[] _tokens = new float[UserSlots];
    private readonly long[] _tokenAt = new long[UserSlots];
    private readonly int[] _voted = new int[UserSlots];
    private readonly float[] _emo = new float[6];
    private readonly int[] _votes = new int[MaxOptions];
    private readonly ChatItem[] _feed = new ChatItem[FeedSize];
    private int _feedHead, _pollId, _pollOptions, _quorum, _branch, _lastWinner = -1;
    private long _pollEnds, _lastTick, _motionAt, _accepted, _dropped, _feedCount;
    private float _msgRate, _donRate, _hype, _valence, _bpm = 96, _hue = 210, _sat = 0.4f, _level = 0.6f;
    private AnchorMotion _motion;
    private ChatEmotion _dominant;
    private bool _pollOpen;

    public event Action? OnChange;

    /// <summary>시청자 반응 감도(0 = 무시 · 1 = 기본 · 2 = 과민).</summary>
    public float Sensitivity { get; set; } = 1f;
    public float TokensPerUser { get; set; } = 3f;
    public float RefillPerSec { get; set; } = 0.5f;
    public float BpmSlewPerSec { get; set; } = 6f;
    public float HueSlewPerSec { get; set; } = 60f;
    public int MotionCooldownMs { get; set; } = 1500;
    public string? LastAction { get; private set; }
    public int PollId { get { lock (_gate) return _pollId; } }
    public int PollOptions { get { lock (_gate) return _pollOptions; } }
    public long PollEndsMs { get { lock (_gate) return _pollEnds; } }
    public int LastWinner { get { lock (_gate) return _lastWinner; } }

    public int CopyVotes(Span<int> dst) { lock (_gate) { var n = Math.Min(dst.Length, _pollOptions); _votes.AsSpan(0, n).CopyTo(dst); return n; } }

    public int CopyFeed(Span<ChatItem> dst)
    {
        lock (_gate)
        {
            var n = (int)Math.Min(Math.Min(dst.Length, FeedSize), _feedCount);
            for (var i = 0; i < n; i++) dst[i] = _feed[(_feedHead - 1 - i + FeedSize * 2) % FeedSize];
            return n;
        }
    }

    /// <summary>문장 감정 분류(무할당): 어휘 · 이모지 · 느낌표/반복 자모. 차단어 포함 시 <paramref name="blocked"/>.</summary>
    public static ChatEmotion Classify(ReadOnlySpan<char> text, out bool blocked)
    {
        blocked = false;
        foreach (var b in Blocklist) if (text.Contains(b, StringComparison.OrdinalIgnoreCase)) { blocked = true; return ChatEmotion.Angry; }
        Span<int> score = stackalloc int[6];
        foreach (var (w, e) in Lexicon)
        {
            var span = text; int idx;
            while ((idx = span.IndexOf(w, StringComparison.OrdinalIgnoreCase)) >= 0) { score[(int)e] += 2; span = span[(idx + w.Length)..]; }
        }
        for (var i = 0; i < Emotes.Length; i++) if (text.Contains(Emotes[i], StringComparison.Ordinal)) score[(int)EmoteEmotion(i)] += 3;
        var bangs = 0; foreach (var c in text) if (c == '!') bangs++;
        if (bangs >= 2) score[(int)ChatEmotion.Hype] += bangs;
        var best = ChatEmotion.Neutral; var bs = 0;
        for (var e = 1; e < 6; e++) if (score[e] > bs) { bs = score[e]; best = (ChatEmotion)e; }
        return best;
    }

    public static ChatEmotion EmoteEmotion(int emote) => emote switch
    {
        0 => ChatEmotion.Hype, 1 => ChatEmotion.Joy, 2 => ChatEmotion.Surprise, 3 => ChatEmotion.Sad, 4 => ChatEmotion.Joy, 5 => ChatEmotion.Hype, 6 => ChatEmotion.Hype, _ => ChatEmotion.Angry,
    };

    private static ulong Hash(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h | 1;
    }

    /// <summary>오픈 어드레싱 사용자 슬롯(가득 차면 가장 오래된 슬롯 재사용).</summary>
    private int Slot(ulong key, long nowMs)
    {
        var i = (int)(key % UserSlots);
        var oldest = i;
        for (var p = 0; p < 16; p++)
        {
            var j = (i + p) % UserSlots;
            if (_userKey[j] == key) return j;
            if (_userKey[j] == 0)
            {
                _userKey[j] = key; _tokens[j] = TokensPerUser; _tokenAt[j] = nowMs; _voted[j] = 0;
                return j;
            }
            if (_tokenAt[j] < _tokenAt[oldest]) oldest = j;
        }
        _userKey[oldest] = key; _tokens[oldest] = TokensPerUser; _tokenAt[oldest] = nowMs; _voted[oldest] = 0;
        return oldest;
    }

    private bool TakeToken(int slot, long nowMs)
    {
        _tokens[slot] = MathF.Min(TokensPerUser, _tokens[slot] + (nowMs - _tokenAt[slot]) / 1000f * RefillPerSec);
        _tokenAt[slot] = nowMs;
        if (_tokens[slot] < 1) return false;
        _tokens[slot] -= 1;
        return true;
    }

    /// <summary>채팅 수신(무할당 · 문자열은 참조만 피드에 보관).</summary>
    public ChatVerdict Chat(string user, string text, long nowMs)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(text)) return ChatVerdict.Empty;
            var e = Classify(text, out var blocked);
            ChatVerdict v;
            if (blocked) v = ChatVerdict.Blocked;
            else if (!TakeToken(Slot(Hash(user), nowMs), nowMs)) v = ChatVerdict.RateLimited;
            else
            {
                v = ChatVerdict.Accepted;
                _msgRate += 1;
                _emo[(int)e] += 1;
                _accepted++;
            }
            if (v != ChatVerdict.Accepted) _dropped++;
            Push(new ChatItem(nowMs, user, blocked ? "[차단된 메시지]" : text, e, v, 0));
            return v;
        }
    }

    /// <summary>후원 이모티콘(금액 KRW · 로그 가중 · 레이트 리밋 없음).</summary>
    public void Donate(string user, int emote, int amount, long nowMs)
    {
        lock (_gate)
        {
            var w = MathF.Log2(1 + MathF.Max(0, amount) / 1000f) * 2 + 1;
            var e = EmoteEmotion(Math.Clamp(emote, 0, Emotes.Length - 1));
            _emo[(int)e] += w;
            _donRate += w;
            _accepted++;
            Push(new ChatItem(nowMs, user, Emotes[Math.Clamp(emote, 0, Emotes.Length - 1)], e, ChatVerdict.Accepted, amount));
        }
    }

    private void Push(in ChatItem item)
    {
        _feed[_feedHead] = item;
        _feedHead = (_feedHead + 1) % FeedSize;
        _feedCount++;
    }

    /// <summary>투표 시작(옵션 = 월드 분기 앞 n개). 사용자당 1표.</summary>
    public void StartPoll(int options, long nowMs, int durationMs = 20_000, int quorum = 5)
    {
        lock (_gate)
        {
            _pollOptions = Math.Clamp(options, 2, MaxOptions);
            Array.Clear(_votes);
            _pollId++;
            _pollEnds = nowMs + durationMs;
            _quorum = Math.Max(1, quorum);
            _pollOpen = true;
        }
        Announce("🗳 투표 시작 · 월드 분기 " + options.ToString(System.Globalization.CultureInfo.InvariantCulture) + "지");
    }

    public bool Vote(string user, int option, long nowMs)
    {
        lock (_gate)
        {
            if (!_pollOpen || (uint)option >= (uint)_pollOptions || nowMs > _pollEnds) return false;
            var slot = Slot(Hash(user), nowMs);
            if (_voted[slot] == _pollId) return false;
            _voted[slot] = _pollId;
            _votes[option]++;
            return true;
        }
    }

    /// <summary>디렉터 틱(무할당): 감쇠 · 투표 마감 · 목표 산출 · 슬루 · 모션 결정.</summary>
    public DirectorState Tick(long nowMs)
    {
        string? announce = null;
        DirectorState st;
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Live.Director.Tick");
            var dt = _lastTick == 0 ? 1f / 60 : Math.Clamp((nowMs - _lastTick) / 1000f, 0, 1);
            _lastTick = nowMs;
            var d3 = MathF.Exp(-dt / 3f);
            // 누적기는 "최근 3초" 지수 창 — 율 = 누적/τ.
            _msgRate *= d3; _donRate *= MathF.Exp(-dt / 10f);
            for (var e = 0; e < 6; e++) _emo[e] *= d3;

            if (_pollOpen && nowMs >= _pollEnds)
            {
                _pollOpen = false;
                var total = 0; var win = 0; var tie = false;
                for (var o = 0; o < _pollOptions; o++)
                {
                    total += _votes[o];
                    if (_votes[o] > _votes[win]) { win = o; tie = false; }
                    else if (o != win && _votes[o] == _votes[win]) tie = true;
                }
                if (total >= _quorum && !tie) { _branch = win; _lastWinner = win; announce = "🌍 투표 결과 → 4D 월드 분기: " + WorldBranches[win]; }
                else announce = "🗳 투표 무효(정족수 미달/동률) · 현재 월드 유지";
            }

            var k = Math.Clamp(Sensitivity, 0, 2);
            var total2 = 1e-3f; for (var e = 0; e < 6; e++) total2 += _emo[e];
            var rate = _msgRate / 3f;
            var energy = (rate / 4f) + (_emo[(int)ChatEmotion.Hype] + 0.5f * _emo[(int)ChatEmotion.Surprise]) / (total2 + 3f) + _donRate / 30f;
            var hypeT = Math.Clamp(k * energy, 0, 1);
            var valT = Math.Clamp(k * (_emo[(int)ChatEmotion.Joy] + _emo[(int)ChatEmotion.Hype] - _emo[(int)ChatEmotion.Sad] - 1.2f * _emo[(int)ChatEmotion.Angry]) / total2, -1, 1);
            _hype += (1 - MathF.Exp(-dt / 0.6f)) * (hypeT - _hype);
            _valence += (1 - MathF.Exp(-dt / 1.0f)) * (valT - _valence);

            var bpmT = 84 + 72 * _hype;
            _bpm += Math.Clamp(bpmT - _bpm, -BpmSlewPerSec * dt, BpmSlewPerSec * dt);
            var hueT = _valence >= 0 ? 40 - 30 * _valence : 210 + 60 * -_valence;   // 따뜻한 금색 ↔ 차가운 청보라
            var dh = ((hueT - _hue) % 360 + 540) % 360 - 180;
            _hue = (_hue + Math.Clamp(dh, -HueSlewPerSec * dt, HueSlewPerSec * dt) + 360) % 360;
            _sat += (1 - MathF.Exp(-dt / 0.8f)) * (0.3f + 0.65f * _hype - _sat);
            _level += (1 - MathF.Exp(-dt / 0.8f)) * (0.45f + 0.5f * _hype - _level);

            var dom = ChatEmotion.Neutral; var ds = 0f;
            for (var e = 1; e < 6; e++) if (_emo[e] > ds) { ds = _emo[e]; dom = (ChatEmotion)e; }
            if (ds / total2 < 0.35f || ds < 1f) dom = ChatEmotion.Neutral;
            _dominant = dom;
            if (nowMs - _motionAt >= MotionCooldownMs)
            {
                var m = dom switch
                {
                    ChatEmotion.Joy => AnchorMotion.Laugh,
                    ChatEmotion.Hype => _hype > 0.75f ? AnchorMotion.Dance : AnchorMotion.Cheer,
                    ChatEmotion.Surprise => AnchorMotion.Surprise,
                    ChatEmotion.Sad => AnchorMotion.Sad,
                    ChatEmotion.Angry => AnchorMotion.Calm,
                    _ => rate > 0.5f ? AnchorMotion.Nod : AnchorMotion.Idle,
                };
                if (m != _motion) { _motion = m; _motionAt = nowMs; }
            }
            st = new DirectorState(_hype, _valence, rate, _donRate / 10f * 60f, _bpm, _hue, _sat, _level, _motion, _dominant, _branch, _pollOpen, _accepted, _dropped);
        }
        if (announce is not null) Announce(announce);
        return st;
    }

    public DirectorState Snapshot()
    {
        lock (_gate) return new DirectorState(_hype, _valence, _msgRate / 3f, _donRate / 10f * 60f, _bpm, _hue, _sat, _level, _motion, _dominant, _branch, _pollOpen, _accepted, _dropped);
    }

    /// <summary>카메라 바이어스: 열기 ↑ → 와이드 · 가상(3DGS/ICVFX) 선호, 열기 ↓ · 슬픔 → 클로즈업 선호(무할당).</summary>
    public void FillCameraBias(AiLiveBroadcastAutonomousSwitchingEngine eng, Span<float> bias)
    {
        float hype, val;
        lock (_gate) { hype = _hype; val = _valence; }
        var n = Math.Min(bias.Length, eng.SourceCount);
        for (var s = 0; s < n; s++)
        {
            var src = eng.SourceAt(s);
            var wide = src.Kind != LiveSourceKind.Camera || src.Name.Contains("와이드", StringComparison.Ordinal) || src.Name.Contains("크레인", StringComparison.Ordinal);
            bias[s] = wide ? 0.9f * MathF.Max(0, hype - 0.4f) : 0.4f * MathF.Max(0, 0.4f - hype) + 0.2f * MathF.Max(0, -val);
        }
    }

    public void Announce(string action)
    {
        LastAction = action;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Live.Director.OnChange", ex); }
    }
}
