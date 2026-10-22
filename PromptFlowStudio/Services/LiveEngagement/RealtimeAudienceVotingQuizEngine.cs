namespace PromptFlowStudio.Services.LiveEngagement;

public enum VoteMode : byte { Poll, Quiz }
public enum VoteResult : byte { Ignored, Accepted, Duplicate, Closed, Unknown, Overflow }

/// <summary>
/// [PromptFlow Studio v17.86.0] 실시간 시청자 라이브 투표 &amp; 퀴즈 엔진(무할당).
///   ▸ 수집: v17.83 옴니 채팅 링 텍스트 스팬 → 선행 공백 무시 · '!'/'！' 접두 토큰(<c>!1</c>~<c>!4</c> · 2 지선다 <c>!찬성</c>/<c>!반대</c>) 서수 비교.
///   ▸ 중복 방어: 작성자 FNV-1a 64(ASCII 소문자 정규화) → 16K 개방 주소 해시셋(선형 탐사 · 적재율 75 % 상한 = Overflow) — 1 인 1 표.
///   ▸ 게이지: 목표 득표율을 16.7 ms 고정 스텝 선형 보간(Lerp 0.18) → 표시 백분율 · 제한 시간 자동 마감 · 퀴즈 정답 공개.
///   ▸ <see cref="Render"/>: 합성기 캔버스에 패널 · 트랙 · 득표 바 · 남은 시간 라인을 국소 영역으로 기록.
/// </summary>
public sealed class RealtimeAudienceVotingQuizEngine
{
    public const int MinOptions = 2, MaxOptions = 4, KeysPerOption = 2, KeyCap = 12, VoterCap = 16384, MaxVoters = VoterCap * 3 / 4;
    public const long StepUs = 16_667;
    public const float LerpK = 0.18f;
    public static readonly uint[] OptionColors =
    {
        ZeroAllocAlphaOverlayCompositorEngine.Rgba(56, 189, 248, 255), ZeroAllocAlphaOverlayCompositorEngine.Rgba(244, 114, 182, 255),
        ZeroAllocAlphaOverlayCompositorEngine.Rgba(250, 204, 21, 255), ZeroAllocAlphaOverlayCompositorEngine.Rgba(74, 222, 128, 255),
    };

    private readonly char[] _keys = new char[MaxOptions * KeysPerOption * KeyCap];
    private readonly int[] _keyLen = new int[MaxOptions * KeysPerOption];
    private readonly ulong[] _voters = new ulong[VoterCap];
    private readonly long[] _votes = new long[MaxOptions];
    private readonly float[] _disp = new float[MaxOptions];
    private readonly string[] _labels = { "", "", "", "" };
    private long _acc;

    public string Question { get; private set; } = "";
    public int OptionCount { get; private set; }
    public VoteMode Mode { get; private set; }
    public int CorrectIndex { get; private set; } = -1;
    public bool HasPoll => OptionCount >= MinOptions;
    public bool Open { get; private set; }
    public bool Revealed { get; private set; }
    public long DurationUs { get; private set; }
    public long ElapsedUs { get; private set; }
    public float RemainingSec => DurationUs <= 0 ? 0f : Math.Max(0, DurationUs - ElapsedUs) / 1e6f;
    public long Total { get; private set; }
    public long Accepted { get; private set; }
    public long Duplicates { get; private set; }
    public long Unknown { get; private set; }
    public long Late { get; private set; }
    public long Overflows { get; private set; }
    public long Ignored { get; private set; }
    public long LerpSteps { get; private set; }
    public int VoterCount { get; private set; }
    public long Polls { get; private set; }
    public string Label(int i) => (uint)i < MaxOptions ? _labels[i] : "";
    public long Votes(int i) => (uint)i < MaxOptions ? _votes[i] : 0;
    public float TargetPercent(int i) => Total == 0 || (uint)i >= MaxOptions ? 0f : _votes[i] * 100f / Total;
    public float DisplayPercent(int i) => (uint)i < MaxOptions ? _disp[i] : 0f;
    public ReadOnlySpan<char> Key(int option, int k) { var s = option * KeysPerOption + k; return _keys.AsSpan(s * KeyCap, _keyLen[s]); }
    public long CorrectVoters => CorrectIndex >= 0 && CorrectIndex < OptionCount ? _votes[CorrectIndex] : 0;

    public int Leader
    {
        get
        {
            var best = -1; long bv = 0;
            for (var i = 0; i < OptionCount; i++) if (_votes[i] > bv) { bv = _votes[i]; best = i; }
            return best;
        }
    }

    // ══════════════════════ 구성(비핫패스) ══════════════════════

    /// <summary>새 투표/퀴즈 — 선택지 2~4 · 득표/투표자 초기화 · 즉시 개표 시작. seconds ≤ 0 = 수동 마감.</summary>
    public bool Configure(string question, ReadOnlySpan<string> options, VoteMode mode = VoteMode.Poll, int correct = -1, int seconds = 60)
    {
        if (options.Length < MinOptions || options.Length > MaxOptions) return false;
        Question = string.IsNullOrWhiteSpace(question) ? "시청자 투표" : question.Trim();
        OptionCount = options.Length;
        for (var i = 0; i < MaxOptions; i++)
        {
            _labels[i] = i < OptionCount ? (string.IsNullOrWhiteSpace(options[i]) ? "선택지 " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : options[i].Trim()) : "";
            SetKey(i, 0, i < OptionCount ? "!" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            SetKey(i, 1, OptionCount == 2 && i < 2 ? (i == 0 ? "!찬성" : "!반대") : "");
        }
        Mode = mode;
        CorrectIndex = mode == VoteMode.Quiz && correct >= 0 && correct < OptionCount ? correct : -1;
        DurationUs = seconds > 0 ? seconds * 1_000_000L : 0;
        ClearTally();
        Open = true; Polls++;
        return true;
    }

    private void SetKey(int option, int k, string key)
    {
        var s = option * KeysPerOption + k;
        var n = Math.Min(key.Length, KeyCap);
        key.AsSpan(0, n).CopyTo(_keys.AsSpan(s * KeyCap));
        _keyLen[s] = n;
    }

    private void ClearTally()
    {
        Array.Clear(_voters); Array.Clear(_votes); Array.Clear(_disp);
        VoterCount = 0; Total = 0; ElapsedUs = 0; _acc = 0; Revealed = false;
    }

    public void Close() => Open = false;

    /// <summary>퀴즈 정답 공개(마감 포함). 반환 = 정답자 수.</summary>
    public long Reveal()
    {
        Open = false;
        Revealed = Mode == VoteMode.Quiz && CorrectIndex >= 0;
        return CorrectVoters;
    }

    public void Reset()
    {
        ClearTally();
        Open = false; OptionCount = 0; Question = ""; CorrectIndex = -1; Mode = VoteMode.Poll;
        for (var i = 0; i < _keyLen.Length; i++) _keyLen[i] = 0;
        for (var i = 0; i < MaxOptions; i++) _labels[i] = "";
        Accepted = Duplicates = Unknown = Late = Overflows = Ignored = LerpSteps = 0;
    }

    // ══════════════════════ 수집(무할당 핫패스) ══════════════════════

    /// <summary>채팅 1 건 → 투표 판정. 접두 토큰 이외 본문은 무시(예: "!1 무조건 1번").</summary>
    public VoteResult Ingest(ReadOnlySpan<char> author, ReadOnlySpan<char> text)
    {
        var p = 0;
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        if (p >= text.Length || (text[p] != '!' && text[p] != '！') || !HasPoll) { Ignored++; return VoteResult.Ignored; }
        var e = p + 1;
        while (e < text.Length && !char.IsWhiteSpace(text[e])) e++;
        var tok = text.Slice(p + 1, e - p - 1);   // '!' 이후(전각 '！' 동일 취급)
        var opt = Match(tok);
        if (opt < 0) { Unknown++; return VoteResult.Unknown; }
        if (!Open) { Late++; return VoteResult.Closed; }
        var h = Hash(author);
        var mask = VoterCap - 1;
        var s = (int)(h & (ulong)mask);
        while (_voters[s] != 0)
        {
            if (_voters[s] == h) { Duplicates++; return VoteResult.Duplicate; }
            s = (s + 1) & mask;
        }
        if (VoterCount >= MaxVoters) { Overflows++; return VoteResult.Overflow; }
        _voters[s] = h; VoterCount++;
        _votes[opt]++; Total++; Accepted++;
        return VoteResult.Accepted;
    }

    private int Match(ReadOnlySpan<char> tok)
    {
        for (var i = 0; i < OptionCount; i++)
            for (var k = 0; k < KeysPerOption; k++)
            {
                var key = Key(i, k);
                if (key.Length > 1 && tok.SequenceEqual(key.Slice(1))) return i;
            }
        return -1;
    }

    /// <summary>작성자 FNV-1a 64(ASCII 대소문자 무시) · 0 예약.</summary>
    public static ulong Hash(ReadOnlySpan<char> author)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < author.Length; i++)
        {
            var c = author[i];
            if (c is >= 'A' and <= 'Z') c = (char)(c + 32);
            h = (h ^ (byte)c) * 1099511628211UL;
            h = (h ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        return h == 0 ? 1 : h;
    }

    // ══════════════════════ 게이지 보간 · 렌더(무할당) ══════════════════════

    /// <summary>경과 시간 누적 → 16.7 ms 고정 스텝 Lerp(틱당 최대 60 스텝) · 제한 시간 자동 마감.</summary>
    public void Tick(long dtUs)
    {
        if (!HasPoll || dtUs <= 0) return;
        if (Open)
        {
            ElapsedUs += dtUs;
            if (DurationUs > 0 && ElapsedUs >= DurationUs) Open = false;
        }
        _acc += dtUs;
        var steps = 0;
        while (_acc >= StepUs && steps < 60)
        {
            _acc -= StepUs; steps++;
            for (var i = 0; i < OptionCount; i++)
            {
                var d = TargetPercent(i) - _disp[i];
                _disp[i] = MathF.Abs(d) < 0.01f ? TargetPercent(i) : _disp[i] + d * LerpK;
            }
        }
        if (_acc > StepUs) _acc = StepUs;
        LerpSteps += steps;
    }

    /// <summary>패널 · 선택지 트랙/득표 바 · 남은 시간 라인 기록(영역 1 개 등록).</summary>
    public void Render(ZeroAllocAlphaOverlayCompositorEngine c, in PixelRect panel)
    {
        if (!HasPoll) return;
        var r = c.AddRegion(panel);
        if (r.IsEmpty) return;
        c.FillRect(r, ZeroAllocAlphaOverlayCompositorEngine.Premul(ZeroAllocAlphaOverlayCompositorEngine.Rgba(8, 12, 24, 190)));
        const int pad = 16, gap = 10;
        var rowH = Math.Max(8, (r.H - pad * 2 - 8 - gap * (OptionCount - 1)) / OptionCount);
        var track = ZeroAllocAlphaOverlayCompositorEngine.Premul(ZeroAllocAlphaOverlayCompositorEngine.Rgba(255, 255, 255, 40));
        for (var i = 0; i < OptionCount; i++)
        {
            var row = new PixelRect(r.X + pad, r.Y + pad + i * (rowH + gap), r.W - pad * 2, rowH);
            c.FillRect(row, track);
            var col = OptionColors[i];
            if (Revealed) col = i == CorrectIndex ? ZeroAllocAlphaOverlayCompositorEngine.Rgba(34, 197, 94, 255) : ZeroAllocAlphaOverlayCompositorEngine.Rgba(100, 116, 139, 200);
            var w = (int)(row.W * Math.Clamp(_disp[i], 0f, 100f) / 100f);
            c.FillRect(new PixelRect(row.X, row.Y, w, row.H), ZeroAllocAlphaOverlayCompositorEngine.Premul(col));
        }
        if (DurationUs > 0)
        {
            var left = Math.Clamp(1f - ElapsedUs / (float)DurationUs, 0f, 1f);
            c.FillRect(new PixelRect(r.X + pad, r.Bottom - pad / 2 - 4, (int)((r.W - pad * 2) * left), 4), ZeroAllocAlphaOverlayCompositorEngine.Rgba(255, 255, 255, 255));
        }
    }
}
