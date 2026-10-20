using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.Xr;

public enum StageFx : byte { Wash, Beam, Strobe, Laser, Smoke, Pyro, Fireworks, Confetti, Co2 }

/// <summary>무대 큐(시각 · 효과 · 픽스처 · 강도 · 색상(0~1) · 룸 좌표 · 지속).</summary>
public readonly record struct StageCue(double T, StageFx Fx, byte Fixture, float Intensity, float Hue, float X, float Y, float Z, int DurMs);

public struct FixtureState { public float Level, Hue; public double Start, End; public float Peak; }

public sealed record ConcertOptions(bool PreDropSmoke = true, bool DropPyro = true, bool LaserOnHighEnergy = true, int DropLaserBars = 8, float BeamIntensity = 0.75f);

/// <summary>
/// [PromptFlow Studio v16.40.0] 🎤 가상 라이브 콘서트 무대 &amp; 공간 오디오 연동기.
///   ▸ v16.37.0 비트 분석(BPM · 다운비트 · 드랍 · 마디 에너지) + 안무 계획(PowerHit/FreezePose) → 조명(워시/빔)·스트로브·레이저·스모크·파이로·폭죽·CO2 큐시트(비트 타임코드 정렬).
///   ▸ v16.33.0 Dolby Atmos: 픽스처 룸 좌표 → 7.1.4 패닝 게인 · 파이로/폭죽 SFX 오브젝트 합성 후 Atmos 매퍼에 등록.
///   ▸ 실시간 재생: 전용 60Hz 스레드 · 커서 기반 큐 발화 · 픽스처 엔벌로프 평가(무할당) · 20Hz 무대 상태 JSON → XR 홀로그램 SSE 다중화 · 수동/관객 트리거 즉시 발화.
/// </summary>
public sealed class VirtualConcertStageOrchestrator : IDisposable
{
    public const int Fixtures = 20, Heads = 8, LaserBase = 8, SmokeBase = 12, PyroBase = 14, ConfettiFx = 18, Co2Fx = 19, AllWash = 255;
    public const int TimecodeFps = 30;

    /// <summary>픽스처 룸 좌표(X 좌우 · Y 높이 0~1 · Z 무대 깊이) — Atmos 패닝 공용.</summary>
    public static readonly float[] FixturePos = BuildPositions();
    private static readonly float[] Palette = { 0.55f, 0.83f, 0.95f, 0.12f, 0.33f, 0.68f };

    private readonly VolumetricVideoHologramStreamer? _holo;
    private readonly AudioBeatVisualSyncEngine? _beats;
    private readonly AlgorithmicChoreographyController? _choreo;
    private readonly DolbyAtmosSpatialAudioMapper? _atmos;
    private readonly object _gate = new();
    private readonly FixtureState[] _fix = new FixtureState[Fixtures];
    private readonly ArrayBufferWriter<byte> _json = new(4096);
    private readonly Utf8JsonWriter _writer;
    private StageCue[] _cues = Array.Empty<StageCue>();
    private float[] _beatGrid = Array.Empty<float>();
    private Thread? _thread;
    private volatile bool _run;
    private int _cursor;
    private double _playhead, _duration, _bpm, _origin;
    private readonly Stopwatch _clock = new();
    private long _fired, _manual, _published;
    private StageFx _lastFx;
    private long _lastRaise;

    public VirtualConcertStageOrchestrator(IServiceProvider sp)
        : this(sp.GetService(typeof(VolumetricVideoHologramStreamer)) as VolumetricVideoHologramStreamer,
               sp.GetService(typeof(AudioBeatVisualSyncEngine)) as AudioBeatVisualSyncEngine,
               sp.GetService(typeof(AlgorithmicChoreographyController)) as AlgorithmicChoreographyController,
               sp.GetService(typeof(DolbyAtmosSpatialAudioMapper)) as DolbyAtmosSpatialAudioMapper) { }

    public VirtualConcertStageOrchestrator(VolumetricVideoHologramStreamer? holo, AudioBeatVisualSyncEngine? beats = null,
        AlgorithmicChoreographyController? choreo = null, DolbyAtmosSpatialAudioMapper? atmos = null)
    {
        _holo = holo; _beats = beats; _choreo = choreo; _atmos = atmos;
        _writer = new Utf8JsonWriter(_json);
    }

    public event Action? OnChange;

    public bool IsPlaying => _thread is not null;
    public IReadOnlyList<StageCue> Cues => _cues;
    public int CueCount => _cues.Length;
    public double Bpm => _bpm;
    public double Duration => _duration;
    public double Playhead => Volatile.Read(ref _playhead);
    public long Fired => Interlocked.Read(ref _fired);
    public long ManualFired => Interlocked.Read(ref _manual);
    public long Published => Interlocked.Read(ref _published);
    public StageFx LastFx => _lastFx;
    public string? LastAction { get; private set; }
    public string Timecode { get { Span<char> s = stackalloc char[11]; return FormatTimecode(Playhead, TimecodeFps, s) ? new string(s) : "00:00:00:00"; } }
    public int BeatIndex { get { var g = _beatGrid; var i = AudioBeatVisualSyncEngine.Nearest(g, (float)Playhead); return i < 0 ? 0 : i; } }

    private static float[] BuildPositions()
    {
        var p = new float[Fixtures * 3];
        void Set(int i, float x, float y, float z) { p[i * 3] = x; p[i * 3 + 1] = y; p[i * 3 + 2] = z; }
        for (var i = 0; i < Heads; i++) Set(i, -0.9f + i * (1.8f / (Heads - 1)), 1f, 0.6f);
        for (var i = 0; i < 4; i++) Set(LaserBase + i, -0.75f + i * 0.5f, 0.85f, 0.9f);
        Set(SmokeBase, -0.8f, 0.05f, 0.8f); Set(SmokeBase + 1, 0.8f, 0.05f, 0.8f);
        for (var i = 0; i < 4; i++) Set(PyroBase + i, -0.9f + i * 0.6f, 0.1f, 0.5f);
        Set(ConfettiFx, 0f, 1f, 0.2f); Set(Co2Fx, 0f, 0.1f, 0.7f);
        return p;
    }

    public static string FxLabel(StageFx f) => f switch
    {
        StageFx.Wash => "🎨 워시", StageFx.Beam => "🔦 빔", StageFx.Strobe => "⚡ 스트로브", StageFx.Laser => "🟩 레이저", StageFx.Smoke => "🌫 스모크",
        StageFx.Pyro => "🔥 파이로", StageFx.Fireworks => "🎆 폭죽", StageFx.Confetti => "🎊 컨페티", _ => "❄ CO2",
    };

    // ══════════════════════ 큐시트 생성 ══════════════════════

    /// <summary>합성 비트 그리드(분석 없을 때 데모) — BPM · 마디 수 · 드랍 마디.</summary>
    public static BeatAnalysis DemoAnalysis(double bpm = 128, int bars = 32, params int[] dropBars)
    {
        var beatSec = 60.0 / bpm;
        var beats = new float[bars * 4];
        for (var i = 0; i < beats.Length; i++) beats[i] = (float)(i * beatSec);
        var down = new float[bars];
        for (var i = 0; i < bars; i++) down[i] = beats[i * 4];
        var drops = dropBars.Length == 0 ? new[] { 8, 24 } : dropBars;
        var dropT = new float[drops.Length];
        for (var i = 0; i < drops.Length; i++) dropT[i] = (float)(Math.Clamp(drops[i], 0, bars - 1) * 4 * beatSec);
        var energy = new float[bars];
        for (var b = 0; b < bars; b++)
        {
            var e = 0.35f;
            foreach (var d in drops) if (b >= d && b < d + 8) e = 0.95f; else if (b >= d - 2 && b < d) e = Math.Max(e, 0.7f);
            energy[b] = e;
        }
        return new BeatAnalysis { Bpm = bpm, Confidence = 1, Duration = bars * 4 * beatSec, HopSec = 0.01, Beats = beats, Downbeats = down, Drops = dropT, BarEnergy = energy };
    }

    /// <summary>비트 분석(+안무) → 정렬된 큐시트. 모든 비트 큐는 비트 시각에 정확히 정렬.</summary>
    public static StageCue[] BuildCues(BeatAnalysis a, ChoreographyPlan? choreo = null, ConcertOptions? options = null)
    {
        var o = options ?? new ConcertOptions();
        var cues = new List<StageCue>(a.Beats.Length * 2 + 64);
        var beatSec = a.BeatSec;
        var beatMs = (int)(beatSec * 1000);
        var bar = 0;
        for (var i = 0; i < a.Beats.Length; i++)
        {
            var t = a.Beats[i];
            while (bar + 1 < a.Downbeats.Length && a.Downbeats[bar + 1] <= t + 1e-4f) bar++;
            var energy = a.BarEnergy.Length > bar ? a.BarEnergy[bar] : 0.5f;
            var hue = Palette[bar % Palette.Length];
            var head = (byte)(i % Heads);
            cues.Add(Cue(t, StageFx.Beam, head, Math.Clamp(o.BeamIntensity * (0.6f + energy * 0.4f), 0f, 1f), hue, (int)(beatMs * 0.8)));
            if (a.Downbeats.Length > bar && MathF.Abs(a.Downbeats[bar] - t) < 1e-4f)
            {
                cues.Add(Cue(t, StageFx.Wash, AllWash, 0.5f + energy * 0.5f, hue, beatMs * 4));
                if (o.LaserOnHighEnergy && energy >= 0.75f)
                    for (var l = 0; l < 4; l++) cues.Add(Cue(t, StageFx.Laser, (byte)(LaserBase + l), energy, (hue + 0.33f) % 1f, beatMs * 2));
            }
        }
        foreach (var d in a.Drops)
        {
            var di = AudioBeatVisualSyncEngine.Nearest(a.Beats, d);
            if (di < 0) continue;
            var dt = a.Beats[di];
            if (o.PreDropSmoke && di >= 4)
            {
                cues.Add(Cue(a.Beats[di - 4], StageFx.Smoke, SmokeBase, 0.9f, 0f, beatMs * 6));
                cues.Add(Cue(a.Beats[di - 4], StageFx.Smoke, SmokeBase + 1, 0.9f, 0f, beatMs * 6));
                for (var k = Math.Max(0, di - 4); k < di; k++)
                {
                    cues.Add(Cue(a.Beats[k], StageFx.Strobe, AllWash, 0.6f + (k - di + 4) * 0.1f, 0f, beatMs / 4));
                    cues.Add(Cue(a.Beats[k] + (float)(beatSec / 2), StageFx.Strobe, AllWash, 0.6f + (k - di + 4) * 0.1f, 0f, beatMs / 4));
                }
            }
            if (o.DropPyro)
            {
                for (var p = 0; p < 4; p++) cues.Add(Cue(dt, StageFx.Pyro, (byte)(PyroBase + p), 1f, 0.08f, 1200));
                cues.Add(Cue(dt, StageFx.Co2, Co2Fx, 1f, 0f, 1500));
                cues.Add(Cue(dt, StageFx.Fireworks, ConfettiFx, 1f, 0.12f, 2500));
            }
            var end = di + o.DropLaserBars * 4;
            for (var k = di; k < end && k < a.Beats.Length; k++)
                cues.Add(Cue(a.Beats[k], StageFx.Laser, (byte)(LaserBase + (k - di) % 4), 1f, 0.33f, (int)(beatMs * 0.9)));
        }
        if (choreo is not null && choreo.BeatSec > 0)
        {
            foreach (var s in choreo.Segments)
            {
                if (s.Move is not (DanceMove.PowerHit or DanceMove.FreezePose)) continue;
                var t = (float)(choreo.FirstBeatSec + s.StartBeat * choreo.BeatSec);
                var bi = AudioBeatVisualSyncEngine.Nearest(a.Beats, t);
                if (bi >= 0) t = a.Beats[bi];
                if (s.Move == DanceMove.PowerHit) cues.Add(Cue(t, StageFx.Pyro, (byte)(PyroBase + (bi & 3)), Math.Clamp(s.Energy, 0.5f, 1f), 0.08f, 800));
                else cues.Add(Cue(t, StageFx.Strobe, AllWash, 1f, 0f, beatMs));
            }
        }
        cues.Sort((x, y) => x.T.CompareTo(y.T));
        return cues.ToArray();
    }

    private static StageCue Cue(double t, StageFx fx, byte fixture, float intensity, float hue, int durMs)
    {
        var f = fixture == AllWash ? -1 : fixture;
        float x = f < 0 ? 0 : FixturePos[f * 3], y = f < 0 ? 1 : FixturePos[f * 3 + 1], z = f < 0 ? 0.5f : FixturePos[f * 3 + 2];
        return new StageCue(t, fx, fixture, intensity, hue, x, y, z, Math.Max(30, durMs));
    }

    /// <summary>큐 → Atmos 7.1.4 스피커 게인(무할당 · Σg²=1).</summary>
    public static void SpatialGains(in StageCue c, Span<float> gains)
        => DolbyAtmosSpatialAudioMapper.PanGains(c.X, c.Y, c.Z, DolbyAtmosSpatialAudioMapper.LayoutOf(SpatialAudioLayout.Surround714), gains);

    /// <summary>파이로/폭죽/CO2 큐 → 합성 SFX(감쇠 노이즈 버스트) Atmos 오브젝트 등록. 반환 = 등록 수.</summary>
    public int ExportFxToAtmos(int maxObjects = 4)
    {
        if (_atmos is null || _cues.Length == 0) return 0;
        const int rate = DolbyAtmosSpatialAudioMapper.Rate;
        var total = (int)Math.Min(_duration * rate + rate, rate * 600.0);
        var added = 0;
        for (var group = 0; group < 3 && added < maxObjects; group++)
        {
            var fx = group == 0 ? StageFx.Pyro : group == 1 ? StageFx.Fireworks : StageFx.Co2;
            var mono = new float[total];
            var track = new List<(double T, float U, float V, float DepthM)>();
            uint seed = 0x9E3779B9u + (uint)group;
            foreach (var c in _cues)
            {
                if (c.Fx != fx) continue;
                var s0 = (int)(c.T * rate);
                var len = Math.Min(total - s0, (int)(c.DurMs / 1000.0 * rate));
                for (var i = 0; i < len; i++)
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    var env = MathF.Exp(-i / (rate * (fx == StageFx.Co2 ? 0.5f : 0.18f)));
                    mono[s0 + i] += ((seed & 0xFFFF) / 32768f - 1f) * env * 0.5f * c.Intensity;
                }
                track.Add((c.T, (c.X + 1f) * 0.5f, 1f - c.Y, 1f + c.Z * 4f));
            }
            if (track.Count == 0) continue;
            if (_atmos.AddTrackedObject("Stage " + fx, mono, track)) added++;
        }
        LastAction = "🔊 무대 SFX → Atmos 오브젝트 " + added.ToString(CultureInfo.InvariantCulture) + "개";
        Raise(true);
        return added;
    }

    /// <summary>큐시트 로드(분석 미지정 시 v16.37.0 마지막 분석 → 없으면 데모 128BPM).</summary>
    public int Load(BeatAnalysis? analysis = null, ChoreographyPlan? choreo = null, ConcertOptions? options = null)
    {
        var a = analysis ?? _beats?.LastAnalysis ?? DemoAnalysis();
        var cues = BuildCues(a, choreo ?? (analysis is null ? _choreo?.Current : null), options);
        lock (_gate)
        {
            _cues = cues;
            _beatGrid = a.Beats;
            _duration = a.Duration;
            _bpm = a.Bpm;
            _cursor = 0;
            Array.Clear(_fix);
        }
        LastAction = "🎼 큐시트 " + cues.Length.ToString(CultureInfo.InvariantCulture) + " · " + a.Bpm.ToString("0", CultureInfo.InvariantCulture) + " BPM";
        Raise(true);
        return cues.Length;
    }

    // ══════════════════════ 실시간 평가(무할당) ══════════════════════

    /// <summary>재생 위치 t 로 진행: 커서 이후 도달 큐 발화(되감기 시 재탐색). 반환 = 발화 수.</summary>
    public int Advance(double t)
    {
        var n = 0;
        lock (_gate)
        {
            if (t < _playhead - 1e-6) { _cursor = LowerBound(_cues, t - 0.05); Array.Clear(_fix); } // 되감기: 직전 50ms 큐 재발화
            while (_cursor < _cues.Length && _cues[_cursor].T <= t)
            {
                Apply(in _cues[_cursor], _cues[_cursor].T);
                _cursor++; n++;
            }
            Volatile.Write(ref _playhead, t);
        }
        if (n > 0) Interlocked.Add(ref _fired, n);
        return n;
    }

    private static int LowerBound(StageCue[] cues, double t)
    {
        int lo = 0, hi = cues.Length;
        while (lo < hi) { var m = (lo + hi) >> 1; if (cues[m].T < t) lo = m + 1; else hi = m; }
        return lo;
    }

    private void Apply(in StageCue c, double at)
    {
        var end = at + c.DurMs / 1000.0;
        if (c.Fixture == AllWash)
        {
            for (var i = 0; i < Heads; i++) Arm(i, c.Intensity, c.Fx == StageFx.Strobe ? 0f : c.Hue, at, end);
        }
        else if (c.Fixture < Fixtures) Arm(c.Fixture, c.Intensity, c.Hue, at, end);
        _lastFx = c.Fx;
    }

    private void Arm(int i, float peak, float hue, double start, double end)
    {
        ref var f = ref _fix[i];
        f.Peak = Math.Max(peak, f.End > start ? f.Level : 0f);
        f.Hue = hue; f.Start = start; f.End = end;
    }

    /// <summary>시각 t 의 픽스처 레벨 평가(선형 감쇠 엔벌로프) → dst(Fixtures).</summary>
    public void Evaluate(double t, Span<float> levels, Span<float> hues)
    {
        lock (_gate)
        {
            for (var i = 0; i < Fixtures && i < levels.Length; i++)
            {
                ref var f = ref _fix[i];
                var lv = t >= f.End || f.End <= f.Start ? 0f : (float)(f.Peak * (1.0 - (t - f.Start) / (f.End - f.Start)));
                f.Level = Math.Clamp(lv, 0f, 1f);
                levels[i] = f.Level;
                if (i < hues.Length) hues[i] = f.Hue;
            }
        }
    }

    /// <summary>수동/관객 트리거 즉시 발화(현재 재생 위치 기준).</summary>
    public void Fire(StageFx fx, float intensity = 1f)
    {
        var t = Playhead;
        lock (_gate)
        {
            switch (fx)
            {
                case StageFx.Laser: for (var l = 0; l < 4; l++) Apply(Cue(t, fx, (byte)(LaserBase + l), intensity, 0.33f, 1600), t); break;
                case StageFx.Pyro: for (var p = 0; p < 4; p++) Apply(Cue(t, fx, (byte)(PyroBase + p), intensity, 0.08f, 1200), t); break;
                case StageFx.Smoke: Apply(Cue(t, fx, SmokeBase, intensity, 0, 3000), t); Apply(Cue(t, fx, SmokeBase + 1, intensity, 0, 3000), t); break;
                case StageFx.Fireworks: case StageFx.Confetti: Apply(Cue(t, fx, ConfettiFx, intensity, 0.12f, 2500), t); break;
                case StageFx.Co2: Apply(Cue(t, fx, Co2Fx, intensity, 0, 1500), t); break;
                default: Apply(Cue(t, fx, AllWash, intensity, fx == StageFx.Strobe ? 0f : Palette[(int)(t * 2) % Palette.Length], 600), t); break;
            }
        }
        Interlocked.Increment(ref _manual);
        LastAction = "🎇 수동 발화 · " + FxLabel(fx);
        Raise(true);
    }

    /// <summary>SMPTE 타임코드 HH:MM:SS:FF(무할당 · dst ≥ 11).</summary>
    public static bool FormatTimecode(double sec, int fps, Span<char> dst)
    {
        if (dst.Length < 11 || fps <= 0) return false;
        var totalFrames = (long)Math.Floor(Math.Max(0, sec) * fps + 1e-6);
        var ff = (int)(totalFrames % fps); var s = totalFrames / fps;
        int hh = (int)(s / 3600 % 100), mm = (int)(s / 60 % 60), ss = (int)(s % 60);
        Two(dst, 0, hh); dst[2] = ':'; Two(dst, 3, mm); dst[5] = ':'; Two(dst, 6, ss); dst[8] = ':'; Two(dst, 9, ff);
        return true;
        static void Two(Span<char> d, int at, int v) { d[at] = (char)('0' + v / 10 % 10); d[at + 1] = (char)('0' + v % 10); }
    }

    /// <summary>무대 상태 JSON 직렬화(재사용 버퍼 · 무할당) → 반환 바이트 스팬은 다음 호출 전까지 유효.</summary>
    public ReadOnlySpan<byte> SerializeState(double t)
    {
        Span<float> lv = stackalloc float[Fixtures];
        Span<float> hu = stackalloc float[Fixtures];
        Evaluate(t, lv, hu);
        float wash = 0, washHue = 0, strobe = 0;
        for (var i = 0; i < Heads; i++) if (lv[i] > wash) { wash = lv[i]; washHue = hu[i]; }
        if (_lastFx == StageFx.Strobe && wash > 0) strobe = wash;
        _json.ResetWrittenCount();
        _writer.Reset(_json);
        _writer.WriteStartObject();
        _writer.WriteString("type"u8, "stage"u8);
        _writer.WriteNumber("t"u8, Math.Round(t, 3));
        _writer.WriteNumber("wash"u8, wash);
        _writer.WriteNumber("hue"u8, washHue);
        _writer.WriteNumber("strobe"u8, strobe);
        _writer.WriteStartArray("lv"u8);
        for (var i = 0; i < Fixtures; i++) _writer.WriteNumberValue(MathF.Round(lv[i] * 100f) / 100f);
        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.Flush();
        return _json.WrittenSpan;
    }

    // ══════════════════════ 재생 ══════════════════════

    public void Play(double fromSec = 0)
    {
        if (_thread is not null) return;
        if (_cues.Length == 0) Load();
        lock (_gate) { _cursor = LowerBound(_cues, fromSec); Array.Clear(_fix); Volatile.Write(ref _playhead, fromSec); }
        _origin = fromSec;
        _clock.Restart();
        _run = true;
        var th = new Thread(PlayLoop) { IsBackground = true, Name = "PFS.ConcertStage" };
        _thread = th;
        th.Start();
        LastAction = "▶ 콘서트 무대 재생 · " + _bpm.ToString("0", CultureInfo.InvariantCulture) + " BPM";
        Raise(true);
    }

    public void Stop()
    {
        var th = Interlocked.Exchange(ref _thread, null);
        if (th is null) return;
        _run = false;
        th.Join(1000);
        _clock.Stop();
        LastAction = "⏹ 무대 정지 · " + Timecode;
        Raise(true);
    }

    private void PlayLoop()
    {
        var nextPub = 0.0;
        while (_run)
        {
            var t = _origin + _clock.Elapsed.TotalSeconds;
            if (_duration > 0 && t > _duration) { _origin = 0; _clock.Restart(); t = 0; }
            try
            {
                Advance(t);
                if (t >= nextPub || t < nextPub - 1)
                {
                    nextPub = t + 0.05;
                    if (_holo is { IsRunning: true } h) { h.PublishState(SerializeState(t)); Interlocked.Increment(ref _published); }
                }
            }
            catch (Exception ex) { Program.Log("Concert.Tick", ex); }
            Raise(false);
            Thread.Sleep(16);
        }
    }

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Concert.OnChange", ex); }
    }

    public void Dispose() { Stop(); _writer.Dispose(); }
}
