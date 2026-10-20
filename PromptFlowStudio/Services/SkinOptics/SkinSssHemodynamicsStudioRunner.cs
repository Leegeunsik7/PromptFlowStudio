using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.FacialPerformance;
using PromptFlowStudio.Services.HoloTelepresence;

namespace PromptFlowStudio.Services.SkinOptics;

public enum EmotionSource : byte { Director = 0, Manual }

/// <summary>
/// [PromptFlow Studio v17.17.0] 🧬 AI 피부 SSS &amp; 미세 혈류 스튜디오 러너(60 Hz 전용 스레드 · 결정적).
/// v17.00 AGI 디렉터(정서 비트) + v17.12 페이셜 퍼포먼스(ARKit 52 · DI 결선 시) 또는 수동 감정 → 혈류/홍조 → 알베도 → 분리형 SSS + 투광 렌더 →
/// v17.12 생체 시선 가드(시선 · 무의식 깜빡임) + 눈물막 가드 깜빡임 요청 → 각막 이중 굴절 안구 렌더 · 계측.
/// </summary>
public sealed class SkinSssHemodynamicsStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float Dt = 1f / Hz;

    private readonly object _gate = new();
    private readonly float[] _arkit = new float[NeuralFacialMuscleSimulator.ShapeCount];
    private readonly float[] _msS = new float[SeriesLen], _bpmS = new float[SeriesLen], _cheekS = new float[SeriesLen], _foreS = new float[SeriesLen], _tearS = new float[SeriesLen];
    private readonly byte[] _bmpSkin = new byte[54 + SeparableNeuralSkinSssEngine.W * SeparableNeuralSkinSssEngine.H * 3];
    private readonly byte[] _bmpEye = new byte[54 + CornealRefractionTearFilmOpticsGuard.Img * CornealRefractionTearFilmOpticsGuard.Img * 3];
    private readonly FacialPerformanceStudioRunner? _face;
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time, _blinkStart = -10f;

    public SkinSssHemodynamicsStudioRunner(IServiceProvider sp)
        : this((FacialPerformanceStudioRunner?)sp.GetService(typeof(FacialPerformanceStudioRunner)))
    {
    }

    public SkinSssHemodynamicsStudioRunner(FacialPerformanceStudioRunner? face = null)
    {
        _face = face;
        Skin = new SeparableNeuralSkinSssEngine();
        Vascular = new MicroVascularHemodynamicsFlushingSynthesizer(Skin);
        Eye = new CornealRefractionTearFilmOpticsGuard();
        Director = new UniversalAgiCinematicDirectorSynthesizer();
        Gaze = new BiologicalEyeGazeMicroTremorGuard();
        Skin.Render();
        Eye.Measure();
        LastAction = "📋 대기 · 피부 UV " + SeparableNeuralSkinSssEngine.W + "² · 6-가우시안 분리형 SSS · 혈류 영역 " + MicroVascularHemodynamicsFlushingSynthesizer.Regions + " · 각막 n " + CornealRefractionTearFilmOpticsGuard.NCornea.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + Skin.Path;
    }

    public event Action? OnChange;

    public SeparableNeuralSkinSssEngine Skin { get; }
    public MicroVascularHemodynamicsFlushingSynthesizer Vascular { get; }
    public CornealRefractionTearFilmOpticsGuard Eye { get; }
    public UniversalAgiCinematicDirectorSynthesizer Director { get; }
    public BiologicalEyeGazeMicroTremorGuard Gaze { get; }
    public bool FaceBound => _face is not null;
    public bool Motion { get; set; } = true;
    public EmotionSource Source { get; set; } = EmotionSource.Director;
    public float ManualValence { get; set; }
    public float ManualArousal { get; set; } = 0.3f;
    public float ManualTension { get; set; }
    public float Lux { get; set; } = 400f;
    public float LightYawDeg { get; set; } = 30f;
    public Vector3 IrisTint { get; set; } = new(0.32f, 0.22f, 0.12f);
    public bool EyeOverlay { get; set; } = true;
    /// <summary>0 = 최종 피부 · 1 = 혈류 관류 히트맵 · 2 = SSS 확산만 · 3 = 투광만.</summary>
    public int SkinView { get; set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public float Blink { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float BpmSeries(int i) => _bpmS[Idx(i)];
    public float CheekSeries(int i) => _cheekS[Idx(i)];
    public float ForeheadSeries(int i) => _foreS[Idx(i)];
    public float TearSeries(int i) => _tearS[Idx(i)];

    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetRadius(float r) { lock (_gate) Skin.TranslucencyRadius = Math.Clamp(r, 0.1f, 3f); LastAction = "🔆 투광 반경 × " + F2(Skin.TranslucencyRadius) + " · 적색 확산 분산 " + F2(Skin.KernelVarianceMm2(0)) + " mm²"; Raise(); }
    public void SetManual(float valence, float arousal, float tension)
    {
        lock (_gate) { Source = EmotionSource.Manual; ManualValence = Math.Clamp(valence, -1f, 1f); ManualArousal = Math.Clamp(arousal, 0f, 1f); ManualTension = Math.Clamp(tension, 0f, 1f); }
        LastAction = "🎚 수동 감정 · 정서가 " + F2(ManualValence) + " · 각성 " + F2(ManualArousal) + " · 긴장 " + F2(ManualTension);
        Raise();
    }
    public void SetSource(EmotionSource s) { lock (_gate) Source = s; LastAction = s == EmotionSource.Director ? "🎬 감정 = v17.00 AGI 디렉터" + (FaceBound ? " + v17.12 페이셜" : "") : "🎚 감정 = 수동"; Raise(); }
    public void SetRefraction(bool on) { lock (_gate) Eye.RefractionEnabled = on; LastAction = on ? "👁 각막 이중 굴절 ON" : "👁 굴절 OFF(직진 비교)"; Raise(); }
    public void SetTearGuard(bool on) { lock (_gate) Eye.TearFilmGuard = on; LastAction = on ? "💧 눈물막 가드 ON" : "💧 눈물막 가드 OFF — 건조 허용"; Raise(); }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("SkinOptics.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            float v, a, t;
            if (Source == EmotionSource.Director)
            {
                Director.Tick(Dt);
                v = Director.Valence; a = Director.Arousal; t = Director.Tension;
                if (_face is not null) for (var k = 0; k < _arkit.Length; k++) _arkit[k] = _face.Final(k);
            }
            else { v = ManualValence; a = ManualArousal; t = ManualTension; Array.Clear(_arkit); }
            Vascular.SetEmotion(v, a, t, _face is not null && Source == EmotionSource.Director ? _arkit : ReadOnlySpan<float>.Empty);
            Vascular.Update(Dt);
            var ly = LightYawDeg * MathF.PI / 180f;
            Skin.LightDir = Vector3.Normalize(new Vector3(MathF.Sin(ly), 0.3f, MathF.Cos(ly)));
            Skin.Render();
            // 시선 · 깜빡임(생체 가드) + 눈물막 깜빡임 요청
            Gaze.Process(0f, 0f, 0f, 0f, 0f, 0f, Dt);
            if (Eye.BlinkRequested && _time - _blinkStart > 0.5f) _blinkStart = _time;
            Blink = MathF.Max(Gaze.BlinkL, BiologicalEyeGazeMicroTremorGuard.BlinkProfile(_time - _blinkStart));
            Eye.UpdateTearFilm(Blink, Dt);
            Eye.SetPupil(Lux, Vascular.Arousal);
            Eye.LightDir = Vector3.Normalize(new Vector3(MathF.Sin(ly), 0.45f, MathF.Cos(ly)));
            Eye.Measure();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _bpmS[h] = Vascular.Bpm; _cheekS[h] = Vascular.RegionErythema(0); _foreS[h] = Vascular.RegionErythema(3); _tearS[h] = Eye.TearFilmUm;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    // ── 모니터(BMP) ─────────────────────────────────────────

    private static byte B8(float v) => (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(v, 0f, 1f), 1f / 2.2f) * 255f + 0.5f), 0, 255);

    private static void Header(byte[] bmp, int w, int h)
    {
        var d = bmp.AsSpan();
        d.Slice(0, 54).Clear();
        d[0] = (byte)'B'; d[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -h);
        d[26] = 1; d[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), bmp.Length - 54);
    }

    private static void Heat(float t, out float r, out float g, out float b)
    {
        t = Math.Clamp(t, 0f, 1f);
        r = Math.Clamp(1.5f - MathF.Abs(4f * t - 3f), 0f, 1f); g = Math.Clamp(1.5f - MathF.Abs(4f * t - 2f), 0f, 1f); b = Math.Clamp(1.5f - MathF.Abs(4f * t - 1f), 0f, 1f);
        if (t < 0.12f) { var k = t / 0.12f; r *= k; g *= k; b *= k; }
    }

    /// <summary>피부 뷰 BMP(<see cref="SkinView"/>).</summary>
    public string SkinBase64()
    {
        lock (_gate)
        {
            Header(_bmpSkin, SeparableNeuralSkinSssEngine.W, SeparableNeuralSkinSssEngine.H);
            var s = Skin;
            for (var i = 0; i < SeparableNeuralSkinSssEngine.N; i++)
            {
                float r, g, b;
                switch (SkinView)
                {
                    case 1:
                        if (s.Mask[i] <= 0f) { r = g = b = 0.03f; break; }
                        Heat(Vascular.Volume(i) / 0.09f, out r, out g, out b); break;
                    case 2: r = s.SssR[i] * 0.6f; g = s.SssG[i] * 0.6f; b = s.SssB[i] * 0.6f; break;
                    case 3: r = s.TrR[i] * 4f; g = s.TrG[i] * 4f; b = s.TrB[i] * 4f; break;
                    default: r = s.OutR[i] / (1f + s.OutR[i]) * 1.6f; g = s.OutG[i] / (1f + s.OutG[i]) * 1.6f; b = s.OutB[i] / (1f + s.OutB[i]) * 1.6f; break;
                }
                var o = 54 + i * 3;
                _bmpSkin[o] = B8(b); _bmpSkin[o + 1] = B8(g); _bmpSkin[o + 2] = B8(r);
            }
            return Convert.ToBase64String(_bmpSkin);
        }
    }

    /// <summary>안구 뷰 BMP(각막 굴절 · 눈물막 메니스커스 · 오버레이).</summary>
    public string EyeBase64()
    {
        lock (_gate)
        {
            Eye.Render(Gaze.Yaw(0), Gaze.Pitch(0), Blink, IrisTint, Vascular.Tension);
            Header(_bmpEye, CornealRefractionTearFilmOpticsGuard.Img, CornealRefractionTearFilmOpticsGuard.Img);
            for (var i = 0; i < CornealRefractionTearFilmOpticsGuard.Img * CornealRefractionTearFilmOpticsGuard.Img; i++)
            {
                float r = Eye.ImgR[i], g = Eye.ImgG[i], b = Eye.ImgB[i];
                if (EyeOverlay)
                    switch (Eye.Overlay[i])
                    {
                        case 1: r = 0.1f; g = 1f; b = 0.4f; break;
                        case 2: r = 1f; g = 0.3f; b = 0.9f; break;
                        case 3: r = MathF.Max(r, 0.3f); g = MathF.Max(g, 0.85f); b = 1f; break;
                    }
                var o = 54 + i * 3;
                _bmpEye[o] = B8(b); _bmpEye[o + 1] = B8(g); _bmpEye[o + 2] = B8(r);
            }
            return Convert.ToBase64String(_bmpEye);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SkinOptics" };
            _loop.Start();
            LastAction = "▶ 피부 광학 가동 · 60 Hz · 감정 → 혈류/홍조 → 분리형 SSS · 투광 → 각막 굴절 · 눈물막";
        }
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SkinOptics.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SkinOptics.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
