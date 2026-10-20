namespace PromptFlowStudio.Services.Talkshow;

public enum TalkRole : byte { Host, Guest }
public enum FloorState : byte { Listening, Speaking, Yielding, Interrupted }
public enum FloorPhase : byte { Open, Held, Gap }
public enum TurnEventKind : byte { Requested, Granted, Yielded, Interrupted, ForcedListen, OverlapResolved, DeadlockBreak }

/// <summary>v17.65.0 턴 이벤트(무할당 값 전달) — 대상 · 이전 화자 · 시각 · 실측 침묵 간격 · 중재 점수.</summary>
public struct TurnEvent
{
    public TurnEventKind Kind;
    public sbyte Agent, From;
    public double AtMs;
    public float GapMs, Score;
    public byte Contenders;
}

public delegate void TurnEventHandler(in TurnEvent e);

/// <summary>
/// [PromptFlow Studio v17.65.0] 실시간 멀티 아바타 턴테이킹 유한 상태 기계(호스트 + 게스트 ≤ 2 · 무할당 핫패스).
///   ▸ 아바타 상태: Listening → (Granted) Speaking → (EOU) Yielding → Listening · 바지인 패배 시 Interrupted(잔여 발성 종료까지 억제).
///   ▸ 발언권 위상: Held(화자 보유) → EOU 큐 → Gap(150~300 ms 자연 침묵 · 각성 ↑ · 질문 → 짧게) → 대기 요청 중재(<see cref="TalkshowMultiCamDeadlockGuard.Resolve"/>) → Granted.
///     요청 없음 → Open(침묵) → <see cref="TalkshowMultiCamDeadlockGuard.DeadlockMs"/> 초과 시 호스트/토픽 오너 강제 인계(데드락 해소).
///   ▸ 비화자 발성 · 바지인 요청 = 오버랩 → 가드 점수 즉시 판정(화자 유지 또는 인터럽트) · 패자는 경청 강제 전이.
///   ▸ 이벤트: 64 링 + <see cref="OnTurn"/>(in 구조체 · 무할당 디스패치).
/// </summary>
public sealed class RealtimeTurnTakingFsmEngine
{
    public const int MaxAgents = 3, EventRing = 64;
    public const float MinGapMs = 150f, MaxGapMs = 300f, TickMarginMs = 17f, EouFallbackMs = 650f;

    private readonly FloorState[] _state = new FloorState[MaxAgents];
    private readonly bool[] _voiced = new bool[MaxAgents], _req = new bool[MaxAgents], _barge = new bool[MaxAgents], _suppressed = new bool[MaxAgents];
    private readonly double[] _reqAt = new double[MaxAgents], _talkMs = new double[MaxAgents], _since = new double[MaxAgents];
    private readonly TurnEvent[] _ev = new TurnEvent[EventRing];
    private int _evHead, _evN, _holder = -1, _agents = 3, _lastSpeaker = -1;
    private FloorPhase _phase;
    private double _now, _lastNow = double.NaN, _eouAt, _gapEnd, _openSince, _turnStart, _lastVoiceAt;
    private bool _eou, _eouQuestion;
    private float _eouArousal;
    private uint _rng = 0x7A1C5u;

    public RealtimeTurnTakingFsmEngine(TalkshowMultiCamDeadlockGuard guard) => Guard = guard;

    public TalkshowMultiCamDeadlockGuard Guard { get; }
    public event TurnEventHandler? OnTurn;
    public int Agents { get => _agents; set => _agents = Math.Clamp(value, 2, MaxAgents); }
    public FloorPhase Phase => _phase;
    public int Holder => _holder;
    public int LastSpeaker => _lastSpeaker;
    public FloorState State(int a) => _state[a];
    public bool Voiced(int a) => _voiced[a];
    public bool Requesting(int a) => _req[a];
    public bool Suppressed(int a) => _suppressed[a];
    public double TalkMs(int a) => _talkMs[a];
    public double TurnAgeMs => _holder >= 0 ? _now - _turnStart : 0;
    public double SilenceMs => _holder < 0 || _phase != FloorPhase.Held ? _now - (_phase == FloorPhase.Open ? _openSince : _eouAt) : 0;
    public double PlannedGapMs { get; private set; }
    public long Handovers { get; private set; }
    public float LastHandoverGapMs { get; private set; }
    public float MinHandoverGapMs { get; private set; } = float.MaxValue;
    public float MaxHandoverGapMs { get; private set; }
    public long Interruptions { get; private set; }
    public double OverlapMs { get; private set; }
    public double UnresolvedOverlapMs { get; private set; }
    public double LastStepUs { get; private set; }
    public double MaxStepUs { get; private set; }
    public int EventCount => _evN;
    /// <summary>최근 이벤트(0 = 최신).</summary>
    public ref readonly TurnEvent Event(int i) => ref _ev[(_evHead - 1 - i + 2 * EventRing) % EventRing];

    public static string StateName(FloorState s) => s switch { FloorState.Speaking => "발언", FloorState.Yielding => "양보", FloorState.Interrupted => "끊김", _ => "경청" };
    public static string KindName(TurnEventKind k) => k switch
    {
        TurnEventKind.Requested => "요청", TurnEventKind.Granted => "인계", TurnEventKind.Yielded => "EOU", TurnEventKind.Interrupted => "인터럽트",
        TurnEventKind.ForcedListen => "경청 강제", TurnEventKind.OverlapResolved => "오버랩 판정", _ => "데드락 해소",
    };

    public void Reset(double nowMs = 0)
    {
        for (var a = 0; a < MaxAgents; a++) { _state[a] = FloorState.Listening; _voiced[a] = _req[a] = _barge[a] = _suppressed[a] = false; _reqAt[a] = _talkMs[a] = 0; _since[a] = nowMs; }
        _evHead = _evN = 0; _holder = _lastSpeaker = -1; _phase = FloorPhase.Open;
        _now = nowMs; _lastNow = double.NaN; _openSince = nowMs; _eou = false; PlannedGapMs = 0;
        Handovers = Interruptions = 0; LastHandoverGapMs = 0; MinHandoverGapMs = float.MaxValue; MaxHandoverGapMs = 0; OverlapMs = UnresolvedOverlapMs = 0; MaxStepUs = 0;
    }

    public void ResetMetrics() { Handovers = Interruptions = 0; MinHandoverGapMs = float.MaxValue; MaxHandoverGapMs = 0; OverlapMs = UnresolvedOverlapMs = 0; MaxStepUs = 0; }

    // ══════════════════════ 입력(무할당) ══════════════════════
    /// <summary>발언권 요청 — <paramref name="barge"/> = 화자 발화 중 끼어들기(오버랩 판정 대상).</summary>
    public void RequestFloor(int a, double nowMs, bool barge = false)
    {
        if ((uint)a >= (uint)_agents || a == _holder && _phase == FloorPhase.Held) return;
        if (!_req[a]) { _req[a] = true; _reqAt[a] = nowMs; Emit(TurnEventKind.Requested, a, _holder, nowMs, 0f, 0f, 0); }
        _barge[a] |= barge;
    }

    public void WithdrawRequest(int a) { _req[a] = false; _barge[a] = false; }

    /// <summary>음성 활동(VAD) — 틱마다 갱신.</summary>
    public void Voice(int a, bool voiced) => _voiced[a] = voiced;

    /// <summary>발화 종료(EOU) 큐 — 화자만 유효 · 질문/각성으로 침묵 간격 조절.</summary>
    public void EndOfUtterance(int a, double nowMs, bool question = false, float arousal = 0.4f)
    {
        if (a != _holder || _phase != FloorPhase.Held || _eou) return;
        _eou = true; _eouAt = nowMs; _eouQuestion = question; _eouArousal = arousal;
    }

    // ══════════════════════ 틱(무할당) ══════════════════════
    public void Step(double nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = double.IsNaN(_lastNow) ? 0 : Math.Max(0, nowMs - _lastNow);
        _lastNow = _now = nowMs;
        Account(dt);
        ResolveOverlap(nowMs);
        switch (_phase)
        {
            case FloorPhase.Held:
                if (_voiced[_holder]) _lastVoiceAt = nowMs;
                if (!_eou && nowMs - Math.Max(_lastVoiceAt, _turnStart) >= EouFallbackMs) { _eou = true; _eouAt = nowMs; _eouQuestion = false; _eouArousal = 0.4f; }
                if (_eou) BeginGap(nowMs);
                break;
            case FloorPhase.Gap:
                if (nowMs >= _gapEnd)
                {
                    var w = Guard.Resolve(_req, _reqAt, _talkMs, _agents, nowMs, -1, out var n, out var s);
                    if (w >= 0) Grant(w, nowMs, n > 1 ? TurnEventKind.OverlapResolved : TurnEventKind.Granted, s, n, true);
                    else { if (_holder >= 0) Set(_holder, FloorState.Listening, nowMs); _holder = -1; _phase = FloorPhase.Open; _openSince = _eouAt; }
                }
                break;
            default:
            {
                var w = Guard.Resolve(_req, _reqAt, _talkMs, _agents, nowMs, -1, out var n, out var s);
                if (w >= 0)
                {
                    if (nowMs - _openSince >= MinGapMs || _lastSpeaker < 0) Grant(w, nowMs, n > 1 ? TurnEventKind.OverlapResolved : TurnEventKind.Granted, s, n, false);
                }
                else if (nowMs - _openSince >= Guard.DeadlockMs)
                {
                    w = Guard.BreakDeadlock(_agents, _lastSpeaker);
                    Grant(w, nowMs, TurnEventKind.DeadlockBreak, 0f, 0, false);
                }
                break;
            }
        }
        for (var a = 0; a < _agents; a++)
        {
            if (_suppressed[a] && !_voiced[a]) _suppressed[a] = false;
            if (_state[a] == FloorState.Interrupted && !_voiced[a]) Set(a, FloorState.Listening, nowMs);
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastStepUs = us; if (us > MaxStepUs) MaxStepUs = us;
    }

    private void Account(double dt)
    {
        if (dt <= 0) return;
        int voiced = 0, live = 0;
        for (var a = 0; a < _agents; a++)
        {
            if (!_voiced[a]) continue;
            voiced++;
            if (!_suppressed[a] && _state[a] != FloorState.Interrupted) live++;
            if (a == _holder) _talkMs[a] += dt;
        }
        if (voiced >= 2) OverlapMs += dt;
        if (live >= 2) UnresolvedOverlapMs += dt;
    }

    /// <summary>비화자 발성 · 바지인 요청 → 화자와 점수 판정(즉시).</summary>
    private void ResolveOverlap(double nowMs)
    {
        for (var a = 0; a < _agents; a++)
        {
            if (a == _holder && _phase == FloorPhase.Held) continue;
            var uninvited = _voiced[a] && !_suppressed[a] && _state[a] == FloorState.Listening;
            if (!uninvited && !_barge[a]) continue;
            if (_holder < 0 || _phase != FloorPhase.Held)
            {
                if (uninvited && !_req[a]) { _req[a] = true; _reqAt[a] = nowMs; }
                _barge[a] = false;
                continue;
            }
            var w = Guard.ResolvePair(_holder, a, _talkMs, _agents, nowMs - _turnStart, nowMs - _reqAt[a], out var score);
            if (w == a)
            {
                var prev = _holder;
                Set(prev, FloorState.Interrupted, nowMs);
                _suppressed[prev] = _voiced[prev];
                Interruptions++;
                Emit(TurnEventKind.Interrupted, prev, a, nowMs, 0f, score, 2);
                Grant(a, nowMs, TurnEventKind.OverlapResolved, score, 2, false);
            }
            else
            {
                _barge[a] = false;
                if (uninvited) { _suppressed[a] = true; _req[a] = false; }
                Emit(TurnEventKind.ForcedListen, a, _holder, nowMs, 0f, score, 2);
            }
        }
    }

    private void BeginGap(double nowMs)
    {
        _phase = FloorPhase.Gap;
        Set(_holder, FloorState.Yielding, nowMs);
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        var jitter = ((_rng & 0xFFFF) / 65535f - 0.5f) * 40f;
        var gap = MinGapMs + 120f * (1f - Math.Clamp(_eouArousal, 0f, 1f)) + (_eouQuestion ? -35f : 0f) + jitter;
        PlannedGapMs = Math.Clamp(gap, MinGapMs, MaxGapMs - TickMarginMs);
        _gapEnd = _eouAt + PlannedGapMs;
        Emit(TurnEventKind.Yielded, _holder, _holder, nowMs, (float)PlannedGapMs, 0f, 0);
    }

    private void Grant(int a, double nowMs, TurnEventKind kind, float score, int contenders, bool measured)
    {
        var from = _holder >= 0 ? _holder : _lastSpeaker;
        if (_holder >= 0 && _holder != a && _state[_holder] != FloorState.Interrupted) Set(_holder, FloorState.Listening, nowMs);
        var gap = _phase == FloorPhase.Held ? 0f : (float)(nowMs - (_phase == FloorPhase.Open ? _openSince : _eouAt));
        _holder = a; _lastSpeaker = a; _phase = FloorPhase.Held; _eou = false; _turnStart = _lastVoiceAt = nowMs;
        _req[a] = false; _barge[a] = false; _suppressed[a] = false;
        Set(a, FloorState.Speaking, nowMs);
        for (var o = 0; o < _agents; o++)
        {
            if (o == a || _state[o] == FloorState.Interrupted) continue;
            if (_state[o] != FloorState.Listening) Set(o, FloorState.Listening, nowMs);
            if (_req[o] && contenders > 1) Emit(TurnEventKind.ForcedListen, o, a, nowMs, 0f, score, (byte)contenders);
        }
        if (measured)
        {
            Handovers++; LastHandoverGapMs = gap;
            if (gap < MinHandoverGapMs) MinHandoverGapMs = gap;
            if (gap > MaxHandoverGapMs) MaxHandoverGapMs = gap;
        }
        Emit(kind, a, from, nowMs, gap, score, (byte)contenders);
        if (kind != TurnEventKind.Granted) Emit(TurnEventKind.Granted, a, from, nowMs, gap, score, (byte)contenders);
    }

    private void Set(int a, FloorState s, double nowMs) { _state[a] = s; _since[a] = nowMs; }

    private void Emit(TurnEventKind k, int a, int from, double at, float gap, float score, byte n)
    {
        ref var e = ref _ev[_evHead];
        e.Kind = k; e.Agent = (sbyte)a; e.From = (sbyte)from; e.AtMs = at; e.GapMs = gap; e.Score = score; e.Contenders = n;
        _evHead = (_evHead + 1) % EventRing; if (_evN < EventRing) _evN++;
        try { OnTurn?.Invoke(in e); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("TurnFsm.OnTurn", ex); }
    }
}
