using System.Numerics;
using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.ProceduralFoley;

namespace PromptFlowStudio.Services.NarrativeTimeline;

/// <summary>
/// [PromptFlow Studio v17.40.0] 📜 AI 각본 내러티브 타임라인 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 타임코드 셔틀(정지/0.5×/1×/2×) → 시퀀서 틱(5 트랙 커서 → 발화 링) → 이벤트 디스패치
/// (씬 = 블로킹 마크 · 비트 = 긴장/친밀 · 모션 큐 · v17.39 카메라 프로그램 · v17.38 모달 폴리 타격 · v17.32 음성 레인 큐) →
/// 드라마투르기 합성 → 연속성 가드(분기 관성 보간 · 음성 페이드/DC 보정) → 보행 발소리 폴리 → 레벨/프레임 이력 순으로 처리한다.
/// 분기점 도달 시 관객 투표 창(5 s · v17.31 투표 입력) 후 다수결 · 감독 인터럽트는 대사 도중에도 즉시 전환.
/// </summary>
public sealed class NarrativeTimelineStudioRunner : IDisposable
{
    public const int Hz = 60, Samples = RealtimeNeuralVoiceCloningDubbingEngine.Fs / Hz, Lanes = InteractiveBranchingContinuitySafetyGuard.Lanes, Hist = 240;
    public const float Dt = 1f / Hz, VoteWindowS = 5f, EndHoldS = 3f, StrideM = 0.75f;

    /// <summary>기본 각본(Fountain · 분기 1 · 섹션 2 · 씬 3).</summary>
    public const string DefaultScript =
        "INT. 폐창고 - 밤\n\n" +
        "= 의심 | T=0.30 | A=-0.2\n\n" +
        "민준이 손전등을 켜고 천천히 걸어 들어온다. 젖은 바닥에 발소리가 울린다.\n\n" +
        "@민준\n거기 누구 있어?\n\n" +
        "@서연\n(그림자 속에서)\n늦었네. 혼자 온 거 맞지?\n\n" +
        "@민준\n약속대로 혼자야. 장부는 어디 있어?\n\n" +
        "= 대립 | T=0.72 | A=-0.6\n\n" +
        "서연이 민준에게 다가선다. 쇠파이프가 바닥에 끌린다.\n\n" +
        "@서연\n그 장부 때문에 세 사람이 죽었어. 왜 이제 와서 찾는 거야!\n\n" +
        "@민준\n진실을 밝히려고. 거짓말은 이제 그만해!\n\n" +
        "서연이 주먹으로 철제 선반을 쾅 내리친다.\n\n" +
        "?? 고백 | 배신\n\n" +
        "# 고백\n\n" +
        "INT. 폐창고 - 계속\n\n" +
        "= 화해 | T=0.45 | A=0.8\n\n" +
        "서연이 민준에게 다가가 젖은 장부를 건넨다.\n\n" +
        "@서연\n미안해. 나도 무서웠어.\n\n" +
        "@민준\n이제 괜찮아. 같이 끝내자.\n\n" +
        "> 페이드 아웃\n\n" +
        "# 배신\n\n" +
        "EXT. 창고 앞 골목 - 밤\n\n" +
        "= 추격 | T=0.95 | A=-0.9\n\n" +
        "민준이 뒷걸음질 친다. 유리창이 깨지며 총성이 울린다.\n\n" +
        "@서연\n처음부터 넌 미끼였어!\n\n" +
        "@민준\n왜! 왜 이런 짓을 해!\n\n" +
        "서연이 골목을 가로질러 사라진다.\n";

    private readonly object _gate = new();
    private readonly float[] _pcm = new float[Samples], _fb0 = new float[Samples], _fb1 = new float[Samples];
    private readonly float[] _src = new float[DramaturgyEmotionalArcBlockingSynthesizer.Channels], _out = new float[DramaturgyEmotionalArcBlockingSynthesizer.Channels];
    private readonly float[] _lvS = new float[Lanes * Hist], _msS = new float[Hist], _fzS = new float[Hist];
    private readonly int[] _votes = new int[RealtimeScriptToScreenTimelineSequencerEngine.MaxOptions];
    private readonly int[] _path = new int[64];
    private readonly float[] _stride = new float[2];
    private readonly Vector2[] _lastFoot = new Vector2[2];
    private string[] _branchMsg = Array.Empty<string>();
    private int _lvHead, _msHead, _pathN, _pendingSection = -1;
    private bool _switch, _voteOpen;
    private float _voteLeft, _endLeft = -1f;
    private double _now;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public NarrativeTimelineStudioRunner(IServiceProvider sp) : this() { }

    public NarrativeTimelineStudioRunner()
    {
        LoadScript(DefaultScript);
    }

    public event Action? OnChange;

    public RealtimeScriptToScreenTimelineSequencerEngine Sequencer { get; } = new();
    public DramaturgyEmotionalArcBlockingSynthesizer Arc { get; } = new();
    public InteractiveBranchingContinuitySafetyGuard Guard { get; } = new();
    public RealtimeNeuralVoiceCloningDubbingEngine Voice { get; } = new();
    public RealtimeModalContactFoleyEngine Foley { get; } = new();
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double NowS => _now;
    /// <summary>셔틀 배속(0 = 일시정지).</summary>
    public float Rate { get; private set; } = 1f;
    public int Program { get; private set; }
    public int CameraCuts { get; private set; }
    public int FoleyStrikes { get; private set; }
    public int Footsteps { get; private set; }
    public int LinesSpoken { get; private set; }
    public int SceneIndex { get; private set; } = -1;
    public int CurrentToken { get; private set; } = -1;
    public int CurrentLineToken { get; private set; } = -1;
    public int BranchesTaken { get; private set; }
    public int DirectorInterrupts { get; private set; }
    public bool VoteOpen => _voteOpen;
    public float VoteLeftS => _voteLeft;
    public int Votes(int k) => _votes[k];
    public int PathCount => _pathN;
    public int PathSection(int i) => _path[i];
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float TickMsSeries(int i) => _msS[(_msHead - 1 - i + 2 * Hist) % Hist];
    public float LevelSeries(int lane, int i) => _lvS[lane * Hist + (_lvHead - 1 - i + 2 * Hist) % Hist];
    public float FoleySeries(int i) => _fzS[(_lvHead - 1 - i + 2 * Hist) % Hist];
    /// <summary>가드 출력 채널(포즈/표정/조명 …) — 화면에 실제로 보이는 값.</summary>
    public float Output(int ch) { lock (_gate) return _out[ch]; }
    public float Source(int ch) { lock (_gate) return _src[ch]; }

    // ══════════════════════ 제어 ══════════════════════

    /// <summary>각본 교체(파싱 · 메인 섹션 스케줄 · 0 s 재시작).</summary>
    public bool LoadScript(string text)
    {
        bool ok;
        lock (_gate)
        {
            ok = Sequencer.Load(text);
            var msg = new string[Math.Max(1, Sequencer.SectionCount)];
            for (var k = 0; k < msg.Length; k++) msg[k] = "🔀 분기 → " + new string(Sequencer.SectionName(k));
            _branchMsg = msg;
            Restart();
        }
        LastAction = ok
            ? "📜 각본 " + Sequencer.TokenCount + " 토큰 · 씬 " + Sequencer.SceneCount + " · 배역 " + Sequencer.CastCount + " · 분기 " + Sequencer.BranchCount + " · 파싱 " + Sequencer.LastParseUs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " µs"
            : "⚠ 각본 비어 있음";
        Raise();
        return ok;
    }

    private void Restart()
    {
        _now = 0; _pathN = 0; _voteOpen = false; _endLeft = -1f; _pendingSection = -1; SceneIndex = -1; CurrentToken = CurrentLineToken = -1;
        for (var l = 0; l < RealtimeNeuralVoiceCloningDubbingEngine.MaxLanes; l++) Voice.ResetLane(l);
        Foley.Clear();
        Sequencer.Schedule(0, 0);
        _path[_pathN++] = 0;
        Guard.NoteEditorialCut();
    }

    public void Rewind() { lock (_gate) Restart(); LastAction = "⏮ 처음부터"; Raise(); }

    public void SetRate(float rate)
    {
        lock (_gate) Rate = Math.Clamp(rate, 0f, 2f);
        LastAction = Rate == 0f ? "⏸ 셔틀 정지" : "⏩ 셔틀 " + Rate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";
        Raise();
    }

    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 분기 연속성 가드 ON(관성 보간 · 페이드)" : "⛔ 연속성 가드 OFF(하드 컷 비교)"; Raise(); }

    /// <summary>관객 투표(v17.31) — 투표 창 열린 동안만 집계.</summary>
    public bool CastVote(int option, int count = 1)
    {
        lock (_gate)
        {
            if (!_voteOpen || option < 0 || option >= Sequencer.BranchOptionCount(Sequencer.PendingBranch)) return false;
            _votes[option] += count;
            return true;
        }
    }

    /// <summary>감독 인터럽트 — 섹션 즉시 전환(대사 도중 포함 · 다음 프레임 적용).</summary>
    public void DirectorBranch(int section)
    {
        lock (_gate)
        {
            if (section < 1 || section >= Sequencer.SectionCount) return;
            DirectorInterrupts++;
            SwitchTo(section);
        }
        LastAction = "🎬 감독 인터럽트 · " + _branchMsg[section];
        Raise();
    }

    public void ResetMetrics()
    {
        lock (_gate) { Guard.ResetMetrics(); Sequencer.ResetMetrics(); Arc.ResetMetrics(); MaxTickMs = 0; CameraCuts = FoleyStrikes = Footsteps = LinesSpoken = BranchesTaken = DirectorInterrupts = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    private void SwitchTo(int section)
    {
        _voteOpen = false; _endLeft = -1f;
        _pendingSection = section;
        _switch = true;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("NarrativeTimeline.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            var switching = _switch; _switch = false;
            var voiceDone = false;
            if (switching)
            {
                // 분기 프레임: ON = 구 분기 발화 렌더 → 페이드아웃 → 리셋 · OFF = 즉시 리셋(하드 컷)
                Guard.BeginTransition(); Guard.ArmAudio();
                if (Guard.Enabled) { RenderVoice(true); voiceDone = true; }
                for (var l = 0; l < Lanes; l++) Voice.ResetLane(l);
                Sequencer.Schedule(_pendingSection, _now);
                if (_pathN < _path.Length) _path[_pathN++] = _pendingSection;
                BranchesTaken++;
                LastAction = _branchMsg[_pendingSection];
                _pendingSection = -1;
            }
            if (!_voteOpen && _endLeft < 0f) _now += Dt * Rate;
            Sequencer.Tick(_now);
            while (Sequencer.TryDequeue(out var e)) Dispatch(in e, switching);
            Votes();
            Arc.Step(Dt * (Rate > 0f ? 1f : 0f));
            Arc.WriteChannels(_src);
            Guard.Filter(_src, _out, Dt);
            Steps();
            if (!voiceDone) RenderVoice(false);
            Array.Clear(_fb0); Array.Clear(_fb1);
            Foley.Render(_fb0, _fb1);
            var s = 0f;
            for (var i = 0; i < Samples; i++) { var v = _fb0[i] + _fb1[i]; s += v * v; }
            _fzS[_lvHead] = 10f * MathF.Log10(s / Samples + 1e-10f);
            _lvHead = (_lvHead + 1) % Hist;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_msHead] = ms; _msHead = (_msHead + 1) % Hist;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private void Dispatch(in TimelineEvent e, bool switching)
    {
        switch (e.Track)
        {
            case TimelineTrack.Script:
                CurrentToken = e.Token;
                switch ((ScriptTokenKind)e.Param)
                {
                    case ScriptTokenKind.Scene:
                        SceneIndex = (int)e.Value;
                        if (!switching) Guard.NoteEditorialCut();
                        Arc.BeginScene(SceneIndex);
                        _lastFoot[0] = Arc.P0; _lastFoot[1] = Arc.P1;
                        break;
                    case ScriptTokenKind.Beat:
                        Arc.SetBeat(e.Value, e.Value2);
                        break;
                    case ScriptTokenKind.Branch:
                        if (Sequencer.BranchOptionCount((int)e.Value) > 0)
                        {
                            _voteOpen = true; _voteLeft = VoteWindowS;
                            Array.Clear(_votes);
                        }
                        break;
                }
                break;
            case TimelineTrack.Actor:
                Arc.MotionCue(e.Actor, (ActorMotion)e.Param);
                break;
            case TimelineTrack.Camera:
                if (Program != e.Param) { Program = e.Param; CameraCuts++; }
                break;
            case TimelineTrack.Foley:
            {
                var p = e.Actor == 1 ? Arc.P1 : Arc.P0;
                var c = new FoleyContact((FoleyMaterial)e.Param, FoleyMaterial.Flesh, 0.4f + 2.6f * e.Value, 0.03f, 0.8f + 5.2f * e.Value, new Vector3(p.X, 1f, p.Y), 1);
                Foley.Strike(in c, 0);
                FoleyStrikes++;
                break;
            }
            case TimelineTrack.Voice:
            {
                var lane = Math.Clamp((int)e.Actor, 0, Lanes - 1);
                Voice.Enqueue(lane, (DubLanguage)e.Param, Sequencer.Text(e.Token));
                Arc.PulseLine(e.Value, lane);
                CurrentLineToken = e.Token;
                LinesSpoken++;
                break;
            }
        }
    }

    /// <summary>분기 투표 창 카운트다운 · 다수결(동률 = 앞 옵션) · 끝 도달 후 처음부터 반복.</summary>
    private void Votes()
    {
        if (_voteOpen)
        {
            _voteLeft -= Dt;
            if (_voteLeft > 0f) return;
            var b = Sequencer.PendingBranch;
            var best = 0;
            for (var k = 1; k < Sequencer.BranchOptionCount(b); k++) if (_votes[k] > _votes[best]) best = k;
            SwitchTo(Sequencer.BranchOption(b, best));
            return;
        }
        if (_pendingSection >= 0 || (Sequencer.PendingBranch >= 0 && Sequencer.BranchOptionCount(Sequencer.PendingBranch) > 0)) return;
        if (_endLeft < 0f)
        {
            if (_now >= Sequencer.ScheduleEndS && Sequencer.Cursor(TimelineTrack.Script) >= Sequencer.EventCount(TimelineTrack.Script)) _endLeft = EndHoldS;
            return;
        }
        _endLeft -= Dt;
        if (_endLeft <= 0f) Restart();
    }

    /// <summary>보행 발소리(v17.33 보폭 리듬) — 출력 포즈 이동 거리 누적 0.75 m 마다 콘크리트 접지.</summary>
    private void Steps()
    {
        for (var a = 0; a < 2; a++)
        {
            var p = new Vector2(_out[a * 2], _out[a * 2 + 1]);
            var d = Vector2.Distance(p, _lastFoot[a]);
            _lastFoot[a] = p;
            if (d > 0.5f) continue;
            _stride[a] += d;
            if (_stride[a] < StrideM) continue;
            _stride[a] -= StrideM;
            var c = new FoleyContact(FoleyMaterial.Concrete, FoleyMaterial.Flesh, 1.2f, 0.04f, 0.6f + 0.6f * Arc.Tension, new Vector3(p.X, 0f, p.Y), 0);
            Foley.Strike(in c, 0);
            Footsteps++;
        }
    }

    private void RenderVoice(bool fade)
    {
        var h = _lvHead;
        for (var l = 0; l < Lanes; l++)
        {
            Voice.Render(l, _pcm);
            if (fade) InteractiveBranchingContinuitySafetyGuard.FadeOut(_pcm);
            Guard.ProcessLane(l, _pcm);
            var s = 0f;
            for (var i = 0; i < Samples; i++) s += _pcm[i] * _pcm[i];
            _lvS[l * Hist + h] = 10f * MathF.Log10(s / Samples + 1e-10f);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.NarrativeTimeline" };
            _loop.Start();
        }
        LastAction = "▶ 내러티브 타임라인 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("NarrativeTimeline.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("NarrativeTimeline.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
