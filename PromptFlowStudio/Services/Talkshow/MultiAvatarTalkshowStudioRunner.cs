using System.Numerics;
using PromptFlowStudio.Services.AvatarActing;
using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.GaussianFaceHand;

namespace PromptFlowStudio.Services.Talkshow;

/// <summary>
/// [PromptFlow Studio v17.65.0] 👥 멀티 아바타 토크쇼 스튜디오 러너(60 Hz 전용 스레드 · 틱 무할당).
///   ▸ 좌석: v17.55 세트 좌표계 반원(게스트 1 좌 · 호스트 중앙 · 게스트 2 우 · +z 객석/카메라 측) · 아바타별 v17.60 텍스트-투-제스처 + v17.58 가우시안 안면(52).
///   ▸ 틱: 제스처 Tick → VAD/EOU → 턴테이킹 FSM(인계 150~300 ms · 오버랩/데드락 가드) → 상호 시선/경청 리액션 → 안면 블렌드 → v17.39 멀티캠 컷 · 프레이밍.
///   ▸ 데모 각본: 인사 → 소개 → 게스트 바지인(거부) → 동시 요청(호스트 우선) → 침묵 데드락(호스트 강제 인계) → 마무리 · 자동 반복.
/// </summary>
public sealed class MultiAvatarTalkshowStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, MaxAgents = RealtimeTurnTakingFsmEngine.MaxAgents, FaceSplats = 2500, LineCap = 256;
    public const float Dt = 1f / Hz;

    private enum Cue : byte { Normal, Barge, Simul, Silence }
    private readonly record struct Line(int Agent, int Addressee, Cue Cue, string Text);

    private static readonly Line[] Script =
    {
        new(0, 1, Cue.Normal, "안녕하세요 여러분, 오늘은 두 분의 게스트를 모셨습니다. 먼저 소개 부탁드려요?"),
        new(1, 0, Cue.Normal, "반갑습니다! 가상 프로덕션 감독을 맡고 있어요."),
        new(2, 0, Cue.Normal, "저는 실시간 렌더링 엔지니어입니다, 잘 부탁드립니다."),
        new(0, 2, Cue.Normal, "오늘 주제는 AI 아바타 방송인데요, 어떻게 보세요?"),
        new(2, 0, Cue.Normal, "정말 놀라운 변화예요! 이제 실시간으로 표정까지 만들 수 있거든요, 시선도 자연스럽고요."),
        new(1, 2, Cue.Barge, "잠깐만요, 그 부분 제가 꼭 덧붙이고 싶어요!"),
        new(0, 1, Cue.Simul, "좋습니다, 그럼 두 분 의견을 차례로 들어볼게요."),
        new(2, 0, Cue.Normal, "감정 표현과 시선 처리가 핵심이라고 생각해요."),
        new(0, 1, Cue.Silence, "음, 다들 생각이 깊어지셨네요. 그럼 마무리 한마디씩 부탁드려요?"),
        new(1, 0, Cue.Normal, "오늘 정말 즐거웠습니다, 감사합니다!"),
        new(2, 0, Cue.Normal, "저도 감사합니다, 다음에 또 만나요!"),
        new(0, 2, Cue.Normal, "시청해 주셔서 감사합니다, 다음 주에 다시 만나요!"),
    };

    private static readonly string[] Fillers = { "네, 계속 이야기해 볼까요?", "음, 좋은 말씀이네요.", "맞아요, 저도 같은 생각이에요." };
    public static readonly string[] DefaultNames = { "호스트", "게스트 1", "게스트 2" };

    private readonly object _gate = new();
    private readonly RealtimeLlmTextToGestureTransformer[] _tr = new RealtimeLlmTextToGestureTransformer[MaxAgents];
    private readonly RealtimeGaussianFacialBlendshapeEngine[] _face = new RealtimeGaussianFacialBlendshapeEngine[MaxAgents];
    private readonly Vector3[] _seat = { new(0f, 0f, 0f), new(-1.25f, 0f, 0.35f), new(1.25f, 0f, 0.35f) };
    private readonly int[] _rank = { 1, 0, 2 };
    private readonly double[] _uttEnd = new double[MaxAgents], _turnClock = new double[MaxAgents], _extVoiceUntil = new double[MaxAgents];
    private readonly bool[] _question = new bool[MaxAgents], _spoken = new bool[Script.Length], _bargeUsed = new bool[Script.Length];
    private readonly char[][] _custom = new char[MaxAgents][];
    private readonly int[] _customLen = new int[MaxAgents], _addr = new int[MaxAgents], _line = { -1, -1, -1 };
    private readonly float[] _w = new float[RealtimeGaussianFacialBlendshapeEngine.K];
    private readonly float[] _msS = new float[Hist], _gapS = new float[Hist];
    private int _sHead, _sN, _filler, _active = 3, _requested = -1;
    private long _tick;
    private bool _silenceOnce, _loopPending;
    private double _loopAt;
    private Thread? _loop;
    private volatile bool _running;

    public MultiAvatarTalkshowStudioRunner(IServiceProvider sp) : this() { }

    public MultiAvatarTalkshowStudioRunner(int faceSplats = FaceSplats)
    {
        Guard = new TalkshowMultiCamDeadlockGuard();
        Fsm = new RealtimeTurnTakingFsmEngine(Guard);
        for (var a = 0; a < MaxAgents; a++)
        {
            _tr[a] = new RealtimeLlmTextToGestureTransformer { Persona = a == 0 ? ActingPersona.TedSpeaker : ActingPersona.Calm };
            _face[a] = new RealtimeGaussianFacialBlendshapeEngine(faceSplats, 0xFACE5u + (uint)a);
            _custom[a] = new char[LineCap];
            var head = _seat[a] + new Vector3(0f, 1.62f, 0f);
            var aim = new Vector3(0f, 0f, 1.6f) - _seat[a];
            Gaze.SetSeat(a, head, MathF.Atan2(aim.X, aim.Z));
            Guard.SetSeatRank(a, _rank[a]);
        }
        Fsm.OnTurn += HandleTurn;
        Fsm.Reset(0);
    }

    public event Action? OnChange;
    public RealtimeTurnTakingFsmEngine Fsm { get; }
    public TalkshowMultiCamDeadlockGuard Guard { get; }
    public MutualGazeListeningReactionSynthesizer Gaze { get; } = new();
    public RealtimeLlmTextToGestureTransformer Gesture(int a) => _tr[a];
    public RealtimeGaussianFacialBlendshapeEngine Face(int a) => _face[a];
    public Vector3 Seat(int a) => _seat[a];
    public int SeatRank(int a) => _rank[a];
    public int Active => _active;
    public bool AutoScript { get; private set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double NowMs => Tick * 1000.0 / Hz;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public long FramesOverBudget { get; private set; }
    public long LinesSpoken { get; private set; }
    public int ScriptLength => Script.Length;
    public int Addressee(int a) => _addr[a];
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float GapSeries(int i) => _gapS[Idx(i)];
    public static string Name(int a) => (uint)a < (uint)DefaultNames.Length ? DefaultNames[a] : "—";
    /// <summary>현재 대사(UI · 비핫패스).</summary>
    public string CurrentLine(int a) => _line[a] >= 0 ? Script[_line[a]].Text : _line[a] == -2 ? new string(_custom[a], 0, _customLen[a]) : "";

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void SetActive(int n)
    {
        lock (_gate)
        {
            _active = Math.Clamp(n, 2, MaxAgents);
            Fsm.Agents = _active; Guard.Active = _active; Gaze.Active = _active;
            ResetShow();
        }
        LastAction = "👥 " + _active.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 인 토크쇼 슬롯"; Raise();
    }

    public void SetAutoScript(bool on) { AutoScript = on; LastAction = on ? "📜 데모 각본 자동 진행" : "📜 수동 진행"; Raise(); }
    public void SetTopicOwner(int a) { Guard.TopicOwner = a >= 0 && a < _active ? a : -1; LastAction = Guard.TopicOwner >= 0 ? "🎯 토픽 오너 · " + Name(a) : "🎯 토픽 오너 해제"; Raise(); }
    public void SimulateSilence() { _silenceOnce = true; LastAction = "🤐 다음 EOU 후 전원 침묵 → 데드락 가드"; Raise(); }

    /// <summary>예기치 않은 동시 발화(외부 음성) 주입 — 오버랩 가드 판정.</summary>
    public void InjectOverlap(int a, double ms = 600)
    {
        lock (_gate) { if ((uint)a < (uint)_active) _extVoiceUntil[a] = NowMs + ms; }
        LastAction = "🔊 " + Name(a) + " 동시 발화 주입"; Raise();
    }

    /// <summary>임의 대사 — 발언권 요청 후 인계 시 발화.</summary>
    public bool Say(int a, string? text, bool barge = false)
    {
        if ((uint)a >= (uint)_active || string.IsNullOrWhiteSpace(text)) return false;
        lock (_gate)
        {
            var t = text.AsSpan().Trim(); if (t.Length > LineCap) t = t[..LineCap];
            t.CopyTo(_custom[a]); _customLen[a] = t.Length;
            Fsm.RequestFloor(a, NowMs, barge);
        }
        LastAction = (barge ? "✋ 끼어들기 요청 · " : "🙋 발언권 요청 · ") + Name(a); Raise();
        return true;
    }

    /// <summary>v17.67 쇼러너 애드리브 — 대본 인터럽트 · 강제 발언권 · 즉시 발화(무할당 · 스팬 복사).</summary>
    public bool InjectAdlib(int a, ReadOnlySpan<char> text)
    {
        if ((uint)a >= (uint)_active || text.IsEmpty) return false;
        lock (_gate)
        {
            var t = text.Length > LineCap ? text[..LineCap] : text;
            t.CopyTo(_custom[a]); _customLen[a] = t.Length;
            for (var o = 0; o < _active; o++) if (o != a) { _tr[o].Interrupt(); Fsm.WithdrawRequest(o); }
            _requested = -1;
            Fsm.ForceFloor(a, NowMs);
            Adlibs++;
        }
        return true;
    }

    public long Adlibs { get; private set; }

    public void ResetMetrics()
    {
        lock (_gate) { Fsm.ResetMetrics(); Guard.ResetMetrics(); Gaze.ResetMetrics(); MaxTickMs = 0; FramesOverBudget = 0; }
        LastAction = "♻ 지표 초기화"; Raise();
    }

    private void ResetShow()
    {
        Array.Clear(_spoken); Array.Clear(_bargeUsed);
        for (var a = 0; a < MaxAgents; a++) { _tr[a].Interrupt(); _line[a] = -1; _customLen[a] = 0; _extVoiceUntil[a] = 0; }
        Fsm.Reset(NowMs); _requested = -1; _loopPending = false;
    }

    // ══════════════════════ 턴 이벤트(무할당 · FSM 스레드) ══════════════════════
    private void HandleTurn(in TurnEvent e)
    {
        Guard.OnTurn(in e);
        var a = e.Agent;
        switch (e.Kind)
        {
            case TurnEventKind.Granted:
                Speak(a);
                break;
            case TurnEventKind.Interrupted:
                _tr[a].Interrupt();
                break;
            case TurnEventKind.ForcedListen:
                _tr[a].Interrupt();
                _extVoiceUntil[a] = 0;
                var nx = NextLine();
                if (nx >= 0 && Script[nx].Agent == a && Script[nx].Cue == Cue.Barge) _bargeUsed[nx] = true;
                break;
        }
    }

    private int NextLine() { for (var i = 0; i < Script.Length; i++) if (!_spoken[i] && Script[i].Agent < _active) return i; return -1; }
    private int NextLineOf(int a) { for (var i = 0; i < Script.Length; i++) if (!_spoken[i] && Script[i].Agent == a) return i; return -1; }

    private void Speak(int a)
    {
        var tr = _tr[a];
        tr.Interrupt();
        ReadOnlySpan<char> text;
        if (_customLen[a] > 0) { text = _custom[a].AsSpan(0, _customLen[a]); _line[a] = -2; _addr[a] = a == 0 ? 1 : 0; }
        else
        {
            var k = AutoScript ? NextLineOf(a) : -1;
            if (k >= 0) { _spoken[k] = true; _line[a] = k; _addr[a] = Script[k].Addressee < _active ? Script[k].Addressee : (a == 0 ? 1 : 0); text = Script[k].Text; }
            else { _line[a] = -3; text = Fillers[_filler++ % Fillers.Length]; _addr[a] = Fsm.LastSpeaker >= 0 && Fsm.LastSpeaker != a ? Fsm.LastSpeaker : (a == 0 ? 1 : 0); }
        }
        _question[a] = text.IndexOf('?') >= 0;
        tr.Enqueue(text);
        if (_line[a] == -2) _customLen[a] = 0;
        _uttEnd[a] = tr.ScheduleEndS - RealtimeLlmTextToGestureTransformer.LineGapS;
        _turnClock[a] = tr.Clock;
        Guard.SetAddressee(a, _addr[a]);
        _requested = -1;
        LinesSpoken++;
    }

    // ══════════════════════ 60 Hz 틱(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("Talkshow.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        var now = NowMs;
        var n = _active;
        for (var a = 0; a < n; a++) { _tr[a].Tick(Dt); Fsm.Voice(a, _tr[a].Speaking || now < _extVoiceUntil[a]); }
        var h = Fsm.Holder;
        if (h >= 0 && Fsm.Phase == FloorPhase.Held)
        {
            var tr = _tr[h];
            if (tr.Clock >= _uttEnd[h]) { Fsm.EndOfUtterance(h, now, _question[h], tr.Current.Arousal); DriveScriptAtEou(h, now); }
            else DriveBarge(h, now);
        }
        else if (Fsm.Phase == FloorPhase.Open && AutoScript)
        {
            var nx = NextLine();
            if (nx >= 0) { if (Fsm.LastSpeaker < 0) Fsm.RequestFloor(Script[nx].Agent, now); }
            else if (!_loopPending) { _loopPending = true; _loopAt = now + 3000; }
            else if (now >= _loopAt) { Array.Clear(_spoken); Array.Clear(_bargeUsed); _loopPending = false; Fsm.RequestFloor(0, now); }
        }
        Fsm.Step(now);
        var sp = Fsm.Holder >= 0 && Fsm.Phase == FloorPhase.Held ? Fsm.Holder : -1;
        ref readonly var gf = ref sp >= 0 ? ref _tr[sp].Current : ref _idleRef[0];
        Gaze.Step(Dt, sp, sp >= 0 ? _addr[sp] : -1, Fsm.LastSpeaker, in gf);
        if (Gaze.TakeSurpriseCue(out var who)) Guard.ReactionCue(who, now);
        for (var a = 0; a < n; a++)
        {
            Array.Clear(_w);
            var tr = _tr[a];
            if (a == sp)
            {
                var off = tr.FaceOffsets;
                for (var k = 0; k < _w.Length && k < off.Length; k++) _w[k] = off[k];
                if (tr.Speaking) _w[(int)ArkitBlendshape.JawOpen] += 0.18f + 0.14f * MathF.Abs(MathF.Sin((float)tr.Clock * 31f));
            }
            Gaze.WriteFace(a, _w);
            _face[a].SetWeights(_w);
            _face[a].Blend();
            ref readonly var p = ref Gaze.Pose(a);
            var head = Gaze.Head(a);
            DirectorActor act;
            act.Foot = _seat[a]; act.Head = head; act.Eye = head + new Vector3(0f, -0.02f, 0f) + 0.08f * p.GazeDir; act.Gaze = p.GazeDir; act.Height = 1.72f;
            Guard.SetActor(a, in act);
        }
        Guard.StepCamera(now, Dt);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        if (ms > 1000.0 / Hz) FramesOverBudget++;
        _msS[_sHead] = (float)ms; _gapS[_sHead] = (float)Fsm.SilenceMs; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    private readonly GestureFrame[] _idleRef = new GestureFrame[1];

    /// <summary>EOU — 각본 다음 화자(들) 요청 · 침묵 큐면 요청 없음(데드락 가드 검증).</summary>
    private void DriveScriptAtEou(int h, double now)
    {
        if (!AutoScript || _requested == -9) return;
        _requested = -9;
        var nx = NextLine();
        if (nx < 0) return;
        if (_silenceOnce || Script[nx].Cue == Cue.Silence) { _silenceOnce = false; return; }
        if (Script[nx].Agent != h) Fsm.RequestFloor(Script[nx].Agent, now);
        if (Script[nx].Cue == Cue.Simul)
            for (var i = nx + 1; i < Script.Length; i++)
                if (!_spoken[i] && Script[i].Agent < _active) { if (Script[i].Agent != h && Script[i].Agent != Script[nx].Agent) Fsm.RequestFloor(Script[i].Agent, now); break; }
    }

    /// <summary>바지인 큐 — 화자 발화 45 % 지점에서 끼어들기 요청.</summary>
    private void DriveBarge(int h, double now)
    {
        if (!AutoScript || _requested >= 0) return;
        var nx = NextLine();
        if (nx < 0 || Script[nx].Cue != Cue.Barge || _bargeUsed[nx] || Script[nx].Agent == h) return;
        var tr = _tr[h];
        var frac = (tr.Clock - _turnClock[h]) / Math.Max(1e-3, _uttEnd[h] - _turnClock[h]);
        if (frac < 0.45) return;
        _requested = Script[nx].Agent;
        Fsm.RequestFloor(Script[nx].Agent, now, barge: true);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Talkshow" };
            _loop.Start();
        }
        LastAction = "▶ 60 Hz 토크쇼 · 턴테이킹 · 상호 시선 · 멀티캠";
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Talkshow.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Talkshow.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Fsm.OnTurn -= HandleTurn;
    }
}
