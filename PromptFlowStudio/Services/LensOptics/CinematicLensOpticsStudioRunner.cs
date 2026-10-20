using System.Numerics;
using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Pyrotechnics;
using PromptFlowStudio.Services.StageLighting;

namespace PromptFlowStudio.Services.LensOptics;

/// <summary>
/// [PromptFlow Studio v17.43.0] 🎥 AI 시네마틱 렌즈 &amp; 포스트 FX 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 카메라 해석(v17.39 감독 프로그램 리그 → v17.41 무대 카메라 → 기본) → 렌즈 FoV · 브리딩 · LUT →
/// 광원 수집(v17.41 픽스처 · v17.42 파이로 클러스터 · 실용광 · 플래시) → 플레어/보케/할레이션 합성 →
/// [포스트] 아나모픽 왜곡 · 색수차 → 블룸 포화 가드 → ACES 톤매핑 → 4 ms 예산 적응 순으로 처리한다.
/// </summary>
public sealed class CinematicLensOpticsStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300, W = RealtimeAnamorphicLensOpticsEngine.W, H = RealtimeAnamorphicLensOpticsEngine.H, Px = RealtimeAnamorphicLensOpticsEngine.Pixels;
    public const int BmpBytes = 54 + Px * 3;
    public const float Dt = 1f / Hz, FlashTau = 0.25f;
    public static readonly Vector3 FlashPoint = new(0f, 1.4f, -1f);
    private static readonly string[] DirectorSources = { "v17.39 " + RealtimeCinematicShotFramingEngine.Name(DirectorCam.Master), "v17.39 " + RealtimeCinematicShotFramingEngine.Name(DirectorCam.CamA), "v17.39 " + RealtimeCinematicShotFramingEngine.Name(DirectorCam.CamB), "v17.39 " + RealtimeCinematicShotFramingEngine.Name(DirectorCam.Action) };

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _postS = new float[Hist], _streakS = new float[Hist], _lvS = new float[Hist];
    private readonly float[] _bgR = new float[Px], _bgG = new float[Px], _bgB = new float[Px];
    private readonly float[] _sr = new float[Px], _sg = new float[Px], _sb = new float[Px], _or = new float[Px], _og = new float[Px], _ob = new float[Px];
    private readonly byte[] _rgb = new byte[Px * 3];
    private readonly IntelligentStageLightingStudioRunner? _stage;
    private readonly NeuralVfxPyrotechnicsStudioRunner? _pyro;
    private readonly CinematicDirectorStudioRunner? _director;
    private LensCamera _cam;
    private int _head, _series;
    private float _flash;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public CinematicLensOpticsStudioRunner(IServiceProvider sp) : this()
    {
        _stage = sp.GetService(typeof(IntelligentStageLightingStudioRunner)) as IntelligentStageLightingStudioRunner;
        _pyro = sp.GetService(typeof(NeuralVfxPyrotechnicsStudioRunner)) as NeuralVfxPyrotechnicsStudioRunner;
        _director = sp.GetService(typeof(CinematicDirectorStudioRunner)) as CinematicDirectorStudioRunner;
    }

    public CinematicLensOpticsStudioRunner()
    {
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = y * W + x;
            var t = y / (float)(H - 1);
            var grid = x % 20 == 0 || y % 20 == 0 ? 0.05f : 0f;
            _bgR[i] = 0.012f + 0.03f * t + grid; _bgG[i] = 0.014f + 0.025f * t + grid; _bgB[i] = 0.03f + 0.01f * t + grid;
        }
        Lens.Configure(RealtimeAnamorphicLensOpticsEngine.Presets[0], RealtimeAnamorphicLensOpticsEngine.Presets[0].Squeeze);
    }

    public event Action? OnChange;

    public RealtimeAnamorphicLensOpticsEngine Lens { get; } = new();
    public PhysicalFlareHalationBokehSynthesizer Flare { get; } = new();
    public HdrBloomSaturationLatencyGuard Guard { get; } = new();
    public LensPreset Preset { get; private set; } = LensPreset.CookeAnamorphic;
    public float Squeeze { get; private set; } = 2f;
    /// <summary>0 = 자동(v17.39 리그 · 기본 40 mm / T2.8).</summary>
    public float FocalOverride { get; private set; }
    public float TStopOverride { get; private set; }
    public bool Practicals { get; private set; } = true;
    public string CameraSource { get; private set; } = "기본";
    public ref readonly LensCamera Camera => ref _cam;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public bool StageLinked => _stage is not null;
    public bool PyroLinked => _pyro is not null;
    public bool DirectorLinked => _director is not null;
    public int Flashes { get; private set; }
    public float FlashCd => _flash;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float TickMsSeries(int i) => _msS[Ix(i)];
    public float PostMsSeries(int i) => _postS[Ix(i)];
    public float StreakSeries(int i) => _streakS[Ix(i)];
    public float LevelSeries(int i) => _lvS[Ix(i)];
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    public ReadOnlySpan<byte> Display => _rgb;
    public ReadOnlySpan<float> OutR => _or;
    public ReadOnlySpan<float> OutG => _og;
    public ReadOnlySpan<float> OutB => _ob;
    public Span<float> SceneR => _sr;

    public static string PresetName(LensPreset p) => RealtimeAnamorphicLensOpticsEngine.Presets[(int)p].Name;

    // ══════════════════════ 제어 ══════════════════════

    public void SetPreset(LensPreset p)
    {
        lock (_gate) { Preset = p; Squeeze = RealtimeAnamorphicLensOpticsEngine.Presets[(int)p].Squeeze; }
        LastAction = "🔭 렌즈 → " + PresetName(p) + " · " + Squeeze.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "x";
        Raise();
    }

    public void SetSqueeze(float s)
    {
        lock (_gate) Squeeze = Math.Clamp(s, 1f, 2f);
        LastAction = "↔ 아나모픽 스퀴즈 " + Squeeze.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "x";
        Raise();
    }

    public void SetFocal(float mm)
    {
        lock (_gate) FocalOverride = mm <= 0f ? 0f : Math.Clamp(mm, 18f, 200f);
        LastAction = FocalOverride > 0f ? "🔍 초점 거리 " + FocalOverride.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " mm" : "🔍 초점 거리 = 카메라 리그 자동";
        Raise();
    }

    public void SetTStop(float t)
    {
        lock (_gate) TStopOverride = t <= 0f ? 0f : Math.Clamp(t, 1.3f, 22f);
        LastAction = TStopOverride > 0f ? "◎ T" + TStopOverride.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "◎ T-스톱 = 카메라 리그 자동";
        Raise();
    }

    public void SetPracticals(bool on) { lock (_gate) Practicals = on; LastAction = on ? "🕯 실용광 ON" : "🕯 실용광 OFF"; Raise(); }

    public void SetGuard(bool on)
    {
        lock (_gate) { Guard.Enabled = on; Guard.ResetLevels(); }
        LastAction = on ? "🛡 블룸 가드 ON(ACEScc 니 · NaN 수리 · 4 ms 피라미드 적응)" : "⛔ 블룸 가드 OFF(포화 · 레이턴시 비교)";
        Raise();
    }

    public void SetBudget(float ms) { lock (_gate) Guard.BudgetMs = Math.Clamp(ms, 0.001f, 1000f); }

    /// <summary>폭발 섬광(감쇠 τ 0.25 s) — 블룸 포화 스트레스.</summary>
    public void Flash(float candela = 5e6f)
    {
        lock (_gate) { _flash = MathF.Max(_flash, candela); Flashes++; }
        LastAction = "💥 섬광 " + candela.ToString("0.#E+0", System.Globalization.CultureInfo.InvariantCulture) + " cd";
        Raise();
    }

    public void ResetMetrics()
    {
        lock (_gate) { Guard.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    /// <summary>UI 뷰어 — 24-bit BMP(하단부터 BGR) 를 호출자 버퍼에 기록.</summary>
    public void WriteBmp(Span<byte> dst)
    {
        dst[..54].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[2..], BmpBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[10..], 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[18..], W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[22..], H);
        dst[26] = 1; dst[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[34..], Px * 3);
        lock (_gate)
        {
            for (var y = 0; y < H; y++)
            {
                var row = 54 + (H - 1 - y) * W * 3;
                for (var x = 0; x < W; x++)
                {
                    var s = 3 * (y * W + x); var o = row + 3 * x;
                    dst[o] = _rgb[s + 2]; dst[o + 1] = _rgb[s + 1]; dst[o + 2] = _rgb[s];
                }
            }
        }
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("LensOptics.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            ResolveCamera();
            Lens.Configure(RealtimeAnamorphicLensOpticsEngine.Presets[(int)Preset], Squeeze);
            Lens.Update(in _cam, Dt);
            _bgR.CopyTo(_sr, 0); _bgG.CopyTo(_sg, 0); _bgB.CopyTo(_sb, 0);
            Gather();
            Flare.Compose(Lens, in _cam, _sr, _sg, _sb);
            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Lens.Apply(_sr, _sg, _sb, _or, _og, _ob);
            Guard.Process(_or, _og, _ob);
            Guard.ToneMap(_or, _og, _ob, _rgb);
            var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            var post = (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Guard.Adapt(post);
            _flash *= MathF.Exp(-Dt / FlashTau);
            if (_flash < 1f) _flash = 0f;
            var ms = (float)((t2 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_head] = ms; _postS[_head] = (float)post; _streakS[_head] = Flare.StreakMeter; _lvS[_head] = Guard.Levels;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private void ResolveCamera()
    {
        float focal = 40f, tstop = 2.8f;
        if (_director is { IsRunning: true })
        {
            ref readonly var rig = ref _director.Framing.Rig(_director.Cuts.Program);
            _cam.Position = rig.Position; _cam.Forward = rig.Forward; _cam.Right = rig.Right; _cam.Up = rig.Up;
            _cam.FocusM = rig.FocusM; focal = rig.FocalMm; tstop = rig.FStop;
            CameraSource = DirectorSources[Math.Clamp(_director.Cuts.Program, 0, DirectorSources.Length - 1)];
        }
        else
        {
            Vector3 pos, target;
            if (_stage is not null) { pos = _stage.Engine.Camera; target = 0.5f * (_stage.Engine.Actor0 + _stage.Engine.Actor1); CameraSource = "v17.41 무대 카메라"; }
            else { pos = new Vector3(0f, 1.7f, 6f); target = new Vector3(0f, 1.6f, 0f); CameraSource = "기본"; }
            var f = target - pos;
            if (f.LengthSquared() < 1e-6f) f = -Vector3.UnitZ;
            f = Vector3.Normalize(f);
            var r = Vector3.Cross(f, Vector3.UnitY);
            r = r.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(r);
            _cam.Position = pos; _cam.Forward = f; _cam.Right = r; _cam.Up = Vector3.Cross(r, f);
            _cam.FocusM = Vector3.Distance(pos, target);
        }
        _cam.FocalMm = FocalOverride > 0f ? FocalOverride : focal > 1f ? focal : 40f;
        _cam.TStop = TStopOverride > 0f ? TStopOverride : tstop > 0.5f ? tstop : 2.8f;
    }

    private void Gather()
    {
        Flare.Begin();
        if (_stage is not null)
        {
            var e = _stage.Engine;
            for (var i = 0; i < RealtimeIntelligentStageLightingEngine.Fixtures; i++)
            {
                ref readonly var fx = ref e.Fixture(i);
                var o = e.OutLevel(i);
                if (fx.Type == FixtureType.Hazer || !(o > 0.01f)) continue;
                var to = _cam.Position - fx.Position;
                var len = to.Length();
                if (len < 1e-3f) continue;
                var cos = Vector3.Dot(fx.Beam, to / len);
                var cone = MathF.Cos(0.5f * (fx.Type == FixtureType.MovingHead ? MathF.Max(fx.Zoom, 4f) : 70f) * MathF.PI / 180f);
                var k = Math.Clamp((cos - (cone - 0.08f)) / 0.08f, 0f, 1f);
                Flare.Add(fx.Position, fx.Color, RealtimeIntelligentStageLightingEngine.MaxBeamCd * o * (0.02f + 0.98f * k * k), LightOrigin.Stage);
            }
        }
        if (_pyro is not null)
        {
            var s = _pyro.Synth;
            var n = Math.Min(s.LightCount, RealtimeIntelligentStageLightingEngine.PointLightSlots);
            for (var i = 0; i < n; i++)
            {
                var l = s.Light(i);
                Flare.Add(l.Position, l.Color, l.Candela * PhysicalFlareHalationBokehSynthesizer.PyroGain, LightOrigin.Pyro);
            }
        }
        if (Practicals)
        {
            Flare.Add(new Vector3(-1.6f, 1.1f, -1.5f), new Vector3(1f, 0.72f, 0.42f), 300f, LightOrigin.Practical);
            Flare.Add(new Vector3(1.9f, 0.9f, 1.2f), new Vector3(1f, 0.8f, 0.55f), 250f, LightOrigin.Practical);
            Flare.Add(new Vector3(0.6f, 4.2f, -4.5f), new Vector3(0.75f, 0.85f, 1f), 6000f, LightOrigin.Practical);
            Flare.Add(new Vector3(-2.8f, 2.2f, -6f), new Vector3(0.9f, 0.95f, 1f), 1500f, LightOrigin.Practical);
        }
        if (_flash > 0f) Flare.Add(FlashPoint, new Vector3(1f, 0.85f, 0.6f), _flash, LightOrigin.Flash);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.LensOptics" };
            _loop.Start();
        }
        LastAction = "▶ 렌즈 광학 60 Hz · " + PresetName(Preset);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("LensOptics.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LensOptics.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
