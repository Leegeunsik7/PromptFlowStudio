using System.Globalization;
using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LensOptics;
using PromptFlowStudio.Services.Pyrotechnics;
using PromptFlowStudio.Services.StageLighting;
namespace PromptFlowStudio.Services.RhythmAction;

/// <summary>
/// [PromptFlow Studio v17.54.0] 🎬 MV 리듬 액션 &amp; 오디오 반응형 VFX 스튜디오 러너(60 Hz · 블록 800).
/// 블록 = 24 마디 편곡 BGM(인트로 4 · 버스 8 · 빌드업 4 · 드롭 8 — v17.53 절차적 생성기 레벨 오토메이션) → v17.53 비트 트래커 →
/// 리듬 드롭 컷 시퀀서(+ 시각 피로도/180° 가드) → 저음역 반응 합성기. 연산 구간은 무할당이며, 결선 디스패치(v17.39 감독 컷 요청 ·
/// v17.41 DMX 파킹 · v17.42 점화 · v17.43 섬광)는 이벤트가 있을 때만 각 러너의 잠금 API 로 전달한다.
/// </summary>
public sealed class RhythmActionMvStudioRunner : IDisposable
{
    public const int Hz = 60, Block = 800, Hist = 240, WaveBins = 300, SongBars = 24, DropBar = 16;
    public const float Dt = 1f / Hz;
    /// <summary>마디별 편곡 게인(정답 구간 — 시퀀서는 오디오 에너지만으로 판정).</summary>
    private static readonly float[] Arrangement =
    {
        0.35f, 0.35f, 0.35f, 0.35f,
        0.42f, 0.42f, 0.42f, 0.42f, 0.42f, 0.42f, 0.42f, 0.42f,
        0.46f, 0.50f, 0.54f, 0.58f,
        1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
    };

    private readonly object _gate = new();
    private readonly float[] _l = new float[Block], _r = new float[Block], _mono = new float[Block];
    private readonly float[] _wMin = new float[WaveBins], _wMax = new float[WaveBins], _bassS = new float[Hist], _dmxS = new float[Hist], _cpsS = new float[Hist];
    private readonly short[] _parked = new short[RealtimeIntelligentStageLightingEngine.Fixtures];
    private readonly CinematicDirectorStudioRunner? _director;
    private readonly IntelligentStageLightingStudioRunner? _lights;
    private readonly NeuralVfxPyrotechnicsStudioRunner? _pyro;
    private readonly CinematicLensOpticsStudioRunner? _lens;
    private int _wHead, _wN, _sHead, _sN;
    private long _sample, _tick, _sentCuts, _sentPyro, _sentFlares;
    private Thread? _loop;
    private volatile bool _running;

    public RhythmActionMvStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(CinematicDirectorStudioRunner)) as CinematicDirectorStudioRunner,
        sp.GetService(typeof(IntelligentStageLightingStudioRunner)) as IntelligentStageLightingStudioRunner,
        sp.GetService(typeof(NeuralVfxPyrotechnicsStudioRunner)) as NeuralVfxPyrotechnicsStudioRunner,
        sp.GetService(typeof(CinematicLensOpticsStudioRunner)) as CinematicLensOpticsStudioRunner) { }

    public RhythmActionMvStudioRunner(CinematicDirectorStudioRunner? director = null, IntelligentStageLightingStudioRunner? lights = null, NeuralVfxPyrotechnicsStudioRunner? pyro = null, CinematicLensOpticsStudioRunner? lens = null)
    {
        _director = director; _lights = lights; _pyro = pyro; _lens = lens;
        Array.Fill(_parked, (short)-1);
        Bgm.SetTempo(128, 0.1);
    }

    public event Action? OnChange;
    public ProceduralBgmGenerator Bgm { get; } = new();
    public RealtimeBeatGridTrackerEngine Tracker { get; } = new();
    public RealtimeRhythmDropCameraSequencerEngine Engine { get; } = new();
    public RapidCutVisualFatigueGuard Guard { get; } = new();
    public AudioReactiveLightingPyroSynthesizer Reactive { get; } = new();
    public bool LinkDirector { get; private set; }
    public bool LinkLights { get; private set; }
    public bool LinkPyro { get; private set; }
    public bool LinkLens { get; private set; }
    public bool DirectorAvailable => _director is not null;
    public bool LightsAvailable => _lights is not null;
    public bool PyroAvailable => _pyro is not null;
    public bool LensAvailable => _lens is not null;
    public long DirectorCutsSent { get; private set; }
    public long PyroSent { get; private set; }
    public long FlaresSent { get; private set; }
    public long LightFrames { get; private set; }
    public double TimeSec => _sample / 48000.0;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastBlockUs { get; private set; }
    public double MaxBlockUs { get; private set; }
    public string? LastAction { get; private set; }
    public int WaveCount => _wN;
    public float WaveMin(int i) => _wMin[(_wHead - 1 - i + 2 * WaveBins) % WaveBins];
    public float WaveMax(int i) => _wMax[(_wHead - 1 - i + 2 * WaveBins) % WaveBins];
    public int SeriesCount => _sN;
    public float BassSeries(int i) => _bassS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float DmxSeries(int i) => _dmxS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float CutRateSeries(int i) => _cpsS[(_sHead - 1 - i + 2 * Hist) % Hist];

    /// <summary>정답 드롭 다운비트 시각(k 번째 루프).</summary>
    public double TruthDropSec(int k) => Bgm.BeatTimeSec(((long)k * SongBars + DropBar) * 4);
    public int SongBar => (int)(Math.Max(0, Math.Floor((TimeSec - Bgm.OffsetSec) / Bgm.BeatPeriod)) / 4 % SongBars);

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void SetTempo(double bpm)
    {
        lock (_gate) { Bgm.SetTempo(bpm, 0.1); Bgm.Reset(); Tracker.Reset(); Engine.Reset(); Guard.Reset(); Reactive.Reset(); _sample = 0; _sentCuts = _sentPyro = _sentFlares = 0; _wN = _sN = 0; MaxBlockUs = 0; }
        LastAction = "🥁 MV 트랙 " + bpm.ToString("0", CultureInfo.InvariantCulture) + " BPM · 24 마디(드롭 17 마디)";
        Raise();
    }

    public void SetLightSensitivity(float v) { lock (_gate) Reactive.LightSensitivity = Math.Clamp(v, 0f, 2f); Raise(); }
    public void SetVfxSensitivity(float v) { lock (_gate) Reactive.VfxSensitivity = Math.Clamp(v, 0f, 2f); Raise(); }
    public void SetPhraseBars(int bars) { lock (_gate) Engine.PhraseBars = bars; LastAction = "🎼 프레이즈 " + Engine.PhraseBars.ToString(CultureInfo.InvariantCulture) + " 마디"; Raise(); }
    public void SetDoubleTime(bool on) { lock (_gate) Engine.DoubleTimeDrop = on; LastAction = on ? "⚡ 드롭 8 분 더블 타임 컷 요청 ON" : "⚡ 더블 타임 OFF"; Raise(); }
    public void SetMinHold(float s) { lock (_gate) Guard.MinHoldS = s; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 최소 유지 0.35 s · 피로도 · 180° 가드 ON" : "⛔ 가드 OFF(유지 미달 · 선 넘김 비교)"; Raise(); }

    public void SetLinkDirector(bool on) { lock (_gate) { LinkDirector = on && _director is not null; _sentCuts = Engine.Cuts; } LastAction = LinkDirector ? "🎥 v17.39 멀티캠 컷 결선" : "🎥 멀티캠 결선 해제"; Raise(); }
    public void SetLinkPyro(bool on) { lock (_gate) { LinkPyro = on && _pyro is not null; _sentPyro = Reactive.PyroIgnitions; } LastAction = LinkPyro ? "🔥 v17.42 파이로 점화 결선" : "🔥 파이로 결선 해제"; Raise(); }
    public void SetLinkLens(bool on) { lock (_gate) { LinkLens = on && _lens is not null; _sentFlares = Reactive.Flares; } LastAction = LinkLens ? "✨ v17.43 스트릭 플레어 결선" : "✨ 플레어 결선 해제"; Raise(); }
    public void SetLinkLights(bool on)
    {
        lock (_gate) { LinkLights = on && _lights is not null; if (!LinkLights) ReleaseParks(); }
        LastAction = LinkLights ? "💡 v17.41 DMX 조도/림 펄스 결선" : "💡 DMX 결선 해제(파킹 반환)";
        Raise();
    }

    // ══════════════════════ 블록 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            {
                using var zg = ZeroGcRegionScope.Enter("RhythmMv.Step");
                Compute();
            }
            Dispatch();
        }
    }

    /// <summary>연산 구간(무할당).</summary>
    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        var start = _sample / 48000.0;
        // 블록 끝 기준 마디 게인(다운비트 킥 어택이 이전 마디 레벨로 잘리지 않도록)
        var beat = Math.Floor((start + Block / 48000.0 - Bgm.OffsetSec) / Bgm.BeatPeriod);
        var bar = beat < 0 ? 0 : (int)((long)beat / 4 % SongBars);
        Bgm.Level = 0.5f * Arrangement[bar];
        Bgm.Render(_l, _r);
        float mn = 0, mx = 0, ms = 0;
        for (var i = 0; i < Block; i++)
        {
            var m = 0.5f * (_l[i] + _r[i]); _mono[i] = m; ms += m * m;
            if (m < mn) mn = m; if (m > mx) mx = m;
        }
        _sample += Block;
        Tracker.Push(_mono);
        Engine.Step(Tracker.TimeSec, Dt, Tracker, Guard, ms / Block);
        Reactive.Process(_mono, start, Tracker, Engine.Section, Engine.DropThisStep);
        _wMin[_wHead] = mn; _wMax[_wHead] = mx; _wHead = (_wHead + 1) % WaveBins; if (_wN < WaveBins) _wN++;
        ref readonly var f = ref Reactive.Current;
        _bassS[_sHead] = f.BassDb; _dmxS[_sHead] = f.DmxBoost; _cpsS[_sHead] = Guard.CutsPerSecond;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastBlockUs = us; if (us > MaxBlockUs && _sample > 48000) MaxBlockUs = us;
    }

    private static DirectorCam Map(MvCam c) => c switch { MvCam.Wide => DirectorCam.Master, MvCam.Medium => DirectorCam.CamA, MvCam.CloseUp => DirectorCam.CamB, _ => DirectorCam.Action };

    /// <summary>결선 디스패치 — 새 이벤트만(컷 · 점화 · 플레어) · DMX 는 펄스 중에만 파킹.</summary>
    private void Dispatch()
    {
        try
        {
            if (LinkDirector && _director is not null && Engine.Cuts > _sentCuts)
            {
                var n = (int)Math.Min(Engine.Cuts - _sentCuts, Engine.CutCount);
                for (var k = n - 1; k >= 0; k--) { ref readonly var c = ref Engine.Cut(k); if (c.Reason != RhythmCutReason.Start) { _director.RequestMusicCut(Map(c.To)); DirectorCutsSent++; } }
                _sentCuts = Engine.Cuts;
            }
            if (LinkPyro && _pyro is not null && Reactive.PyroIgnitions > _sentPyro) { _sentPyro = Reactive.PyroIgnitions; _pyro.Detonate(); PyroSent++; }
            if (LinkLens && _lens is not null && Reactive.Flares > _sentFlares) { _sentFlares = Reactive.Flares; _lens.Flash(5e5f + 3e6f * Reactive.Current.FlareStreak); FlaresSent++; }
            if (LinkLights && _lights is not null) DriveLights(_lights);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { PromptFlowStudio.Program.Log("RhythmMv.Dispatch", ex); }
    }

    /// <summary>림 = 림 펄스 · 워시 = 조도 부스트(0..255 파킹) · 펄스가 꺼지면 파킹 반환.</summary>
    private void DriveLights(IntelligentStageLightingStudioRunner li)
    {
        ref readonly var f = ref Reactive.Current;
        var any = false;
        for (var i = 0; i < RealtimeIntelligentStageLightingEngine.Fixtures; i++)
        {
            ref readonly var fx = ref li.Engine.Fixture(i);
            var lv = fx.Role == LightRole.Rim ? f.RimPulse : fx.Role == LightRole.Wash ? f.DmxBoost : 0f;
            var v = lv > 0.02f ? (short)Math.Clamp((int)(lv * 255f), 0, 255) : (short)-1;
            if (v == _parked[i] || (v >= 0 && _parked[i] >= 0 && Math.Abs(v - _parked[i]) < 3)) { any |= _parked[i] >= 0; continue; }
            li.Park(li.Engine.DimmerChannel(i), v);
            _parked[i] = v; any |= v >= 0;
        }
        if (any) LightFrames++;
    }

    private void ReleaseParks()
    {
        if (_lights is null) return;
        for (var i = 0; i < _parked.Length; i++)
            if (_parked[i] >= 0) { _lights.Park(_lights.Engine.DimmerChannel(i), -1); _parked[i] = -1; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.RhythmMv" };
            _loop.Start();
        }
        LastAction = "▶ MV 리듬 액션 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("RhythmMv.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("RhythmMv.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) ReleaseParks();
    }
}
