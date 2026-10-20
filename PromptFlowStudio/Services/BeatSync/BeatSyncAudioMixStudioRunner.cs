using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.BeatSync;

/// <summary>
/// [PromptFlow Studio v17.53.0] 🎵 비트 동기화 &amp; 오디오 더킹 스튜디오 러너(60 Hz · 블록 800 · v17.49 <see cref="IAudioMixStage"/> 구현).
/// 블록 = BGM 생성(절차적/WAV) → 비트 트래커(모노) → 마스킹 노치(보컬 대비) → 사이드체인 더킹 + 보컬/폴리 합산 → 위상 · −14 LUFS · −1 dBTP 가드.
/// 단독 미리보기 보컬은 내장 음절 발화 생성기(1.8 s 발화 / 1.2 s 휴지), v17.49 결선 시 v17.47 더빙 보이스 · 폴리 스템을 쓴다.
/// 비트 이벤트는 v17.52 단어 팝업 스냅(± 120 ms) · 전환 마커(다운비트) · v17.39 컷 양자화(<see cref="IBeatClock"/>)로 배포한다.
/// </summary>
public sealed class BeatSyncAudioMixStudioRunner : IAudioMixStage, IDisposable
{
    public const int Hz = 60, Block = 800, Hist = 240, WaveBins = 300;
    private readonly object _gate = new();
    private readonly float[] _bl = new float[Block], _br = new float[Block], _ol = new float[Block], _or = new float[Block], _mono = new float[Block];
    private readonly float[] _voice = new float[Block], _fl = new float[Block], _fr = new float[Block];
    private readonly float[] _wMin = new float[WaveBins], _wMax = new float[WaveBins], _grS = new float[Hist], _luS = new float[Hist], _tpS = new float[Hist];
    private readonly double[] _beatMs = new double[128];
    private readonly MotionTypography.MotionOverlayStudioRunner? _motion;
    private readonly CinematicDirector.CinematicDirectorStudioRunner? _director;
    private readonly VerticalTranscoderStudioRunner? _trans;
    private int _wHead, _wN, _sHead, _sN;
    private long _sample, _tick;
    private double _phase;
    private Thread? _loop;
    private volatile bool _running;

    public BeatSyncAudioMixStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(MotionTypography.MotionOverlayStudioRunner)) as MotionTypography.MotionOverlayStudioRunner,
        sp.GetService(typeof(CinematicDirector.CinematicDirectorStudioRunner)) as CinematicDirector.CinematicDirectorStudioRunner,
        sp.GetService(typeof(VerticalTranscoderStudioRunner)) as VerticalTranscoderStudioRunner) { }

    public BeatSyncAudioMixStudioRunner(MotionTypography.MotionOverlayStudioRunner? motion = null, CinematicDirector.CinematicDirectorStudioRunner? director = null, VerticalTranscoderStudioRunner? trans = null)
    { _motion = motion; _director = director; _trans = trans; }

    public event Action? OnChange;
    public ProceduralBgmGenerator Bgm { get; } = new();
    public RealtimeBeatGridTrackerEngine Tracker { get; } = new();
    public VoiceActivatedSidechainDuckingSynthesizer Ducker { get; } = new();
    public LoudnessNormalizationPhaseGuard Guard { get; } = new();
    public bool TestVoice { get; set; } = true;
    public bool SyncWords { get; private set; }
    public bool SyncCuts { get; private set; }
    public bool AttachedToEncoder => _trans is not null && ReferenceEquals(_trans.Synth.MixStage, this);
    public bool MotionLinked => _motion is not null;
    public bool DirectorLinked => _director is not null;
    public bool EncoderLinked => _trans is not null;
    public int WordsSnapped { get; private set; }
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
    public float GrSeries(int i) => _grS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float LufsSeries(int i) => _luS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float TruePeakSeries(int i) => _tpS[(_sHead - 1 - i + 2 * Hist) % Hist];

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void SetDepth(float db) { lock (_gate) Ducker.DepthDb = Math.Clamp(db, -24f, -6f); Raise(); }
    public void SetTempo(double bpm) { lock (_gate) { Bgm.SetTempo(bpm); Bgm.Reset(); Tracker.Reset(); Guard.Reset(); Ducker.Reset(); _sample = 0; } LastAction = "🥁 BGM " + bpm.ToString("0", CultureInfo.InvariantCulture) + " BPM"; Raise(); }
    public void SetDucking(bool on) { lock (_gate) Ducker.Enabled = on; Raise(); }
    public void SetNormalize(bool on) { lock (_gate) Guard.Normalize = on; Raise(); }
    public void SetMasking(bool on) { lock (_gate) Guard.MaskingGuard = on; Raise(); }

    public bool LoadWav(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!Sound.SoundDsp.TryReadWav(bytes, out var l, out var r, out var rate, out _)) { LastAction = "⚠ WAV 판독 실패"; Raise(); return false; }
            lock (_gate) { Bgm.LoadWav(l, r, rate); Tracker.Reset(); Guard.Reset(); }
            LastAction = "🎵 BGM 로드 · " + Path.GetFileName(path);
            Raise();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { LastAction = "⚠ 파일 열기 실패"; Raise(); return false; }
    }

    public void ClearWav() { lock (_gate) { Bgm.LoadWav(null, null, 48000); Tracker.Reset(); } Raise(); }

    public void SetSyncWords(bool on) { lock (_gate) SyncWords = on; LastAction = on ? "🔤 v17.52 단어 팝업 비트 스냅 ON" : "🔤 단어 스냅 OFF"; Raise(); }

    public void SetSyncCuts(bool on)
    {
        lock (_gate) SyncCuts = on;
        if (_director is not null) _director.Cuts.BeatClock = on ? Tracker : null;
        LastAction = on ? "🎬 v17.39 컷 비트 양자화 ON" : "🎬 컷 양자화 OFF";
        Raise();
    }

    public bool AttachEncoder(bool on)
    {
        if (_trans is null) return false;
        _trans.Synth.MixStage = on ? this : null;
        LastAction = on ? "🎞 v17.49 인코딩 오디오 = 비트 동기 믹스" : "🎞 인코딩 오디오 기본 믹스";
        Raise();
        return true;
    }

    /// <summary>최근 비트로 v17.52 단어 스냅 · 다운비트로 전환 마커(비핫패스 · 1 초 주기 호출).</summary>
    private void ApplyMotionSync()
    {
        if (_motion is null || !SyncWords || !Tracker.Locked || Tracker.EventCount < 4) return;
        // 비트 그리드를 0 ms 기준 타임라인으로 투영(마지막 다운비트 = 0)
        double origin = double.NaN;
        for (var i = 0; i < Tracker.EventCount; i++) if (Tracker.Event(i).Downbeat) { origin = Tracker.Event(i).TimeSec; break; }
        if (double.IsNaN(origin)) return;
        // 8 분음표 그리드(비트 = 짝수 지점) · 허용 = 그리드 절반 → 음절 속도 단어도 순서대로 양자화
        var step = 30000.0 / Tracker.Bpm;
        var n = Math.Min(_beatMs.Length, (int)(_motion.Typo.TotalMs / step) + 16);
        for (var k = 0; k < n; k++) _beatMs[k] = k * step;
        WordsSnapped = _motion.Typo.SnapToBeats(_beatMs.AsSpan(0, n), (float)(step * 0.5) + 0.5f);
        Span<float> cuts = stackalloc float[4];
        var c = 0;
        for (var k = 8; k < n && c < 4; k += 8) cuts[c++] = (float)_beatMs[k];
        _motion.Overlay.SetCuts(cuts[..c]);
    }

    // ══════════════════════ 블록(무할당) ══════════════════════
    private void VoiceTest(Span<float> v, Span<float> fl, Span<float> fr)
    {
        for (var i = 0; i < v.Length; i++)
        {
            var t = (_sample + i) / 48000.0;
            var cyc = t % 3.0;
            var env = 0f;
            if (cyc < 1.8) { var syl = (float)(cyc * 5.5 % 1.0); env = syl < 0.75f ? MathF.Sin(MathF.PI * syl / 0.75f) : 0f; }
            _phase += 2 * Math.PI * (150 + 20 * Math.Sin(2 * Math.PI * 0.7 * t)) / 48000;
            var ph = (float)_phase;
            v[i] = 0.22f * env * (MathF.Sin(ph) + 0.6f * MathF.Sin(2 * ph) + 0.4f * MathF.Sin(3 * ph) + 0.25f * MathF.Sin(5 * ph) + 0.15f * MathF.Sin(7 * ph));
            var hit = t % 2.3; var f = hit < 0.12 ? 0.25f * MathF.Exp(-(float)hit / 0.03f) * MathF.Sin(2f * MathF.PI * 420f * (float)hit) : 0f;
            fl[i] = f * 0.8f; fr[i] = f * 0.4f;
        }
        if (_phase > 1e6) _phase %= 2 * Math.PI;
    }

    /// <summary>블록 1 개 처리 — voice/foley 스템 → l/r 최종 믹스(무할당 · 길이 800).</summary>
    private void Process(ReadOnlySpan<float> voice, ReadOnlySpan<float> foleyL, ReadOnlySpan<float> foleyR, Span<float> l, Span<float> r)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Bgm.Render(_bl, _br);
        for (var i = 0; i < Block; i++) _mono[i] = 0.5f * (_bl[i] + _br[i]);
        Tracker.Push(_mono);
        Guard.ProcessBgm(voice, _bl, _br, Ducker.VoiceActive);
        Ducker.Process(voice, foleyL, foleyR, _bl, _br, l, r);
        Guard.ProcessMaster(l, r);
        _sample += Block;
        float mn = 0, mx = 0;
        for (var i = 0; i < Block; i++) { if (l[i] < mn) mn = l[i]; if (l[i] > mx) mx = l[i]; }
        _wMin[_wHead] = mn; _wMax[_wHead] = mx; _wHead = (_wHead + 1) % WaveBins; if (_wN < WaveBins) _wN++;
        _grS[_sHead] = Ducker.GainReductionDb; _luS[_sHead] = Guard.Output.ShortTermLufs; _tpS[_sHead] = Guard.TruePeakDbtp;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastBlockUs = us; if (us > MaxBlockUs && _sample > 48000) MaxBlockUs = us;
    }

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("BeatSync.Step");
            Interlocked.Increment(ref _tick);
            if (TestVoice) VoiceTest(_voice, _fl, _fr); else { Array.Clear(_voice); Array.Clear(_fl); Array.Clear(_fr); }
            Process(_voice, _fl, _fr, _ol, _or);
            if (_tick % Hz == 0) ApplyMotionSync();
        }
    }

    /// <summary><see cref="IAudioMixStage"/> — v17.49 인코딩 오디오(보컬 센터 · 폴리 모노 → 스테레오 · 길이 800).</summary>
    public void Mix(ReadOnlySpan<float> voice, ReadOnlySpan<float> foley, Span<float> l, Span<float> r)
    {
        lock (_gate)
        {
            var n = Math.Min(l.Length, Block);
            for (var i = 0; i < Block; i++) { _voice[i] = i < voice.Length ? 1.4f * voice[i] : 0f; var f = i < foley.Length ? 1.2f * foley[i] : 0f; _fl[i] = f; _fr[i] = f; }
            Process(_voice, _fl, _fr, _ol, _or);
            _ol.AsSpan(0, n).CopyTo(l); _or.AsSpan(0, n).CopyTo(r);
        }
    }

    public ReadOnlySpan<float> OutL => _ol;
    public ReadOnlySpan<float> OutR => _or;

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.BeatSync" };
            _loop.Start();
        }
        LastAction = "▶ 비트 동기 믹스 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("BeatSync.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("BeatSync.Runner.OnChange", ex); }
    }
    public void Dispose()
    {
        Stop();
        if (AttachedToEncoder) _trans!.Synth.MixStage = null;
        if (_director is not null && ReferenceEquals(_director.Cuts.BeatClock, Tracker)) _director.Cuts.BeatClock = null;
    }
}
