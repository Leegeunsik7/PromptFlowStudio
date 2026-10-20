using System.Numerics;
using PromptFlowStudio.Services.CognitiveCinematography;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.NeuralRagdoll;

namespace PromptFlowStudio.Services.CinematicDirector;

/// <summary>
/// [PromptFlow Studio v17.39.0] 🎬 AI 시네마틱 감독 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 v17.12 블로킹 세트(두 배우 동선 · 얼굴/시선) → v17.32 음성 엔진 2 레인(배우별 대사 큐 · 48 kHz 렌더 → 프레임 RMS dBFS) →
/// 샷 구도 엔진(4 카메라 · SIMD 후보 · 구도/DoF · 스프링) → 180° 축/점프컷 가드(측 클램프) → 컷 합성기(발화 창 · 충격 인서트 · 가드 심사) 순으로 처리한다.
/// 충격은 전용 v17.37 래그돌 러너의 임펄스 에너지(J)를 그대로 보고(피격 배우 = 1).
/// </summary>
public sealed class CinematicDirectorStudioRunner : IDisposable
{
    public const int Hz = 60, Samples = RealtimeNeuralVoiceCloningDubbingEngine.Fs / Hz, Cams = RealtimeCinematicShotFramingEngine.Cams;
    public const float Dt = 1f / Hz;

    private readonly record struct Line(byte Actor, string Text, float GapAfter);

    /// <summary>대본(배우 0/1 교대 · 빠른 응답 · 긴 독백 포함).</summary>
    private static readonly Line[] Script =
    {
        new(0, "어제 그 일, 정말 네가 한 거야?", 0.5f),
        new(1, "아니, 나는 그냥 지켜보기만 했어.", 0.4f),
        new(0, "그 말을 내가 믿을 거라고 생각해?", 0.3f),
        new(1, "믿든 말든 그건 네 자유야. 하지만 끝까지 들어 봐. 그날 밤 창고 앞에는 나 말고도 세 사람이 더 있었고, 그중 한 명은 너도 잘 아는 사람이었어.", 1.2f),
        new(0, "누구야.", 0.3f),
        new(1, "지금 말하면 너도 위험해져.", 2.8f),
        new(0, "좋아. 그럼 처음부터 다시 얘기해 보자.", 0.6f),
        new(1, "알았어.", 0.8f),
    };

    private readonly object _gate = new();
    private readonly float[] _pcm = new float[Samples];
    private readonly CameraRig[] _snap = new CameraRig[Cams];
    private readonly float[] _lvS = new float[480], _msS = new float[240];
    private readonly bool[] _tally = new bool[Cams];
    private int _line = -1, _lvHead, _msHead, _autoN;
    private float _gapLeft = 0.6f, _autoT;
    private double _now;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public CinematicDirectorStudioRunner(IServiceProvider sp) : this() { }

    public CinematicDirectorStudioRunner()
    {
        Set = new CinematicBlockingSet();
        Voice = new RealtimeNeuralVoiceCloningDubbingEngine();
        Ragdoll = new NeuralRagdollStudioRunner();
        Framing.AddOccluder(new Vector3(-0.2f, 0f, -0.9f), 0.32f);
        Framing.AddOccluder(new Vector3(2.4f, 0f, -2.6f), 0.32f);
        Framing.AddOccluder(new Vector3(-2.6f, 0f, 1.6f), 0.32f);
        Feed();
        Framing.Update(Dt);
        Guard.Track(Framing);
        Snapshot();
        LastAction = "📋 대기 · 카메라 4 · 대본 " + Script.Length + " 줄 · 페이싱 " + DialogueActionCinematicCutSynthesizer.PacingName(Cuts.Pacing);
    }

    public event Action? OnChange;

    public CinematicBlockingSet Set { get; }
    public RealtimeNeuralVoiceCloningDubbingEngine Voice { get; }
    public NeuralRagdollStudioRunner Ragdoll { get; }
    public RealtimeCinematicShotFramingEngine Framing { get; } = new();
    public DialogueActionCinematicCutSynthesizer Cuts { get; } = new();
    public CinematicAxisJumpCutSafetyGuard Guard { get; } = new();
    public bool AutoImpact { get; private set; }
    /// <summary>액션 카메라 자유 궤도 각속도(rad/s · 가드 없으면 선을 넘음).</summary>
    public float OrbitSpeed { get; set; } = 0.35f;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double NowS => _now;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int LineIndex => _line;
    public string CurrentLine => _line >= 0 ? Script[_line].Text : "";
    public int CurrentSpeakerLine => _line >= 0 ? Script[_line].Actor : -1;
    public float TickMsSeries(int i) => _msS[(_msHead - 1 - i + 480) % 240];
    public float LevelSeries(int actor, int i) => _lvS[actor * 240 + (_lvHead - 1 - i + 480) % 240];
    public CameraRig CamSnapshot(int c) { lock (_gate) return _snap[c]; }
    public bool Tally(int c) { lock (_gate) return _tally[c]; }

    // ══════════════════════ 제어 ══════════════════════

    public void SetPacing(CutPacing p)
    {
        lock (_gate) { Cuts.Pacing = p; Guard.MaxCutsPer10s = p switch { CutPacing.Calm => 4, CutPacing.Fast => 14, _ => 8 }; }
        LastAction = "⏱ 페이싱 → " + DialogueActionCinematicCutSynthesizer.PacingName(p) + " · 최소 샷 " + Cuts.MinShot.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
        Raise();
    }

    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 180° 선 · 점프컷 · 간격 가드 ON" : "⛔ 연출 가드 OFF(선 넘김 · 점프컷 비교)"; Raise(); }
    public void SetAutoImpact(bool on) { lock (_gate) { AutoImpact = on; _autoT = 0f; } LastAction = on ? "🤖 자동 충격 ON(7 s 주기)" : "🤖 자동 충격 OFF"; Raise(); }

    /// <summary>v17.37 래그돌 충격(N·s) → 에너지 → 액션 인서트.</summary>
    public float InjectImpact(float magnitudeNs, float azimuthDeg = 90f)
    {
        float e;
        lock (_gate) { e = Ragdoll.InjectImpulse(RagdollJoint.Chest, magnitudeNs, azimuthDeg); Cuts.ReportImpact(e, 1, Framing); }
        LastAction = "💥 충격 " + magnitudeNs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " N·s → " + e.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " J · 액션 인서트 요청";
        Raise();
        return e;
    }

    /// <summary>v17.54 MV 리듬 컷 요청(외부 스레드 · 무할당) — 합성기 최소 샷 · 가드 심사를 그대로 거친다.</summary>
    public void RequestMusicCut(DirectorCam cam) { lock (_gate) Cuts.RequestMusicCut((int)cam); }

    public void ResetMetrics()
    {
        lock (_gate) { Cuts.ResetMetrics(); Guard.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("CinematicDirector.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _now += Dt;
            Set.Advance(Dt);
            Dialogue();
            var l0 = Level(0); var l1 = Level(1);
            Ragdoll.Step();
            if (AutoImpact)
            {
                _autoT += Dt;
                if (_autoT >= 7f) { _autoT = 0f; _autoN++; var e = Ragdoll.InjectImpulse(RagdollJoint.Chest, _autoN % 2 == 0 ? 160f : 40f, 90f); Cuts.ReportImpact(e, 1, Framing); }
            }
            Feed();
            Framing.ActionOrbit += OrbitSpeed * Dt;
            if (Framing.ActionOrbit > MathF.PI) Framing.ActionOrbit -= MathF.Tau;
            Framing.Update(Dt);
            Guard.Track(Framing);
            Cuts.Step(Dt, l0, l1, Framing, Guard);
            Snapshot();
            var h = _lvHead;
            _lvS[h] = l0; _lvS[240 + h] = l1; _lvHead = (h + 1) % 240;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_msHead] = ms; _msHead = (_msHead + 1) % 240;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    /// <summary>대본 진행(레인 비면 간격 후 다음 줄 큐).</summary>
    private void Dialogue()
    {
        if (_line >= 0 && Voice.RemainingSamples(Script[_line].Actor) > 0) return;
        _gapLeft -= Dt;
        if (_gapLeft > 0f) return;
        _line = (_line + 1) % Script.Length;
        ref readonly var ln = ref Script[_line];
        Voice.Enqueue(ln.Actor, DubLanguage.Ko, ln.Text);
        _gapLeft = ln.GapAfter;
    }

    /// <summary>레인 렌더(프레임 800 샘플) → RMS dBFS.</summary>
    private float Level(int lane)
    {
        Voice.Render(lane, _pcm);
        var s = 0f;
        for (var i = 0; i < Samples; i++) s += _pcm[i] * _pcm[i];
        return 10f * MathF.Log10(s / Samples + 1e-10f);
    }

    private void Feed()
    {
        Framing.A = Actor(0); Framing.B = Actor(1);
    }

    private DirectorActor Actor(int i) => new()
    {
        Foot = Set.ActorPosition(i), Head = Set.HeadCenter(i), Eye = Set.Eye(i), Gaze = Set.Gaze(i), Height = CinematicBlockingSet.EyeHeight + 0.12f,
    };

    private void Snapshot()
    {
        for (var c = 0; c < Cams; c++) { _snap[c] = Framing.Rig(c); _tally[c] = c == Cuts.Program; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.CinematicDirector" };
            _loop.Start();
        }
        LastAction = "▶ 시네마틱 감독 60 Hz · " + DialogueActionCinematicCutSynthesizer.PacingName(Cuts.Pacing);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("CinematicDirector.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("CinematicDirector.Runner.OnChange", ex); }
    }

    public void Dispose() { Stop(); Ragdoll.Dispose(); }
}
