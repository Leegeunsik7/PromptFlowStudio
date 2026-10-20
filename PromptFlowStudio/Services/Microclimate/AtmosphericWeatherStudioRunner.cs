using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Microclimate;

/// <summary>
/// [PromptFlow Studio v17.28.0] ⛈ AI 대기 기상 &amp; 미기후 스튜디오 러너(60 Hz 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 프레임 대기 엔진을 시간 배율(기본 ×20 → 1/3 s)만큼 전진(가드 CFL 하위 단계 · 무발산 투영 · 에너지 클램프) → 강수 입자 · 젖음/적설/사태(가속 시간)
/// → 헤어/망토 공기역학(실시간 1/60 s) 순으로 처리한다.
/// </summary>
public sealed class AtmosphericWeatherStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float Dt = 1f / Hz;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _windS = new float[SeriesLen], _maxS = new float[SeriesLen], _divS = new float[SeriesLen], _rainS = new float[SeriesLen], _cloudS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public AtmosphericWeatherStudioRunner(IServiceProvider sp) : this() { }

    public AtmosphericWeatherStudioRunner()
    {
        World = new WorldExpansion.GenerativeNeuralWorldExpansionEngine();
        Guard = new ThermodynamicEnergyDivergenceWeatherGuard();
        Engine = new RealtimeAtmosphericWeatherSimulationEngine(Guard, World);
        Micro = new MicroclimateAeroPrecipitationSynthesizer();
        PlaceActor();
        LastAction = "📋 대기 · " + Engine.Spec.Name + " · 격자 " + RealtimeAtmosphericWeatherSimulationEngine.NX + "×" + RealtimeAtmosphericWeatherSimulationEngine.NY + "×" + RealtimeAtmosphericWeatherSimulationEngine.NZ + " (" + RealtimeAtmosphericWeatherSimulationEngine.H + " m) · v17.27 지형 위 · 시간 ×" + TimeScale;
    }

    public event Action? OnChange;

    public WorldExpansion.GenerativeNeuralWorldExpansionEngine World { get; }
    public RealtimeAtmosphericWeatherSimulationEngine Engine { get; }
    public ThermodynamicEnergyDivergenceWeatherGuard Guard { get; }
    public MicroclimateAeroPrecipitationSynthesizer Micro { get; }
    public float TimeScale { get; private set; } = 20f;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float WindSeries(int i) => _windS[Idx(i)];
    public float MaxWindSeries(int i) => _maxS[Idx(i)];
    public float DivergenceSeries(int i) => _divS[Idx(i)];
    public float RainSeries(int i) => _rainS[Idx(i)];
    public float CloudSeries(int i) => _cloudS[Idx(i)];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private void PlaceActor()
    {
        const int c = (RealtimeAtmosphericWeatherSimulationEngine.NZ / 2) * RealtimeAtmosphericWeatherSimulationEngine.NX + RealtimeAtmosphericWeatherSimulationEngine.NX / 2;
        Micro.Place(new Vector3(0f, (Engine.GroundLevel(c) + 0.5f) * RealtimeAtmosphericWeatherSimulationEngine.H, 0f));
    }

    public void SetPreset(WeatherPreset p) { lock (_gate) { Engine.SetPreset(p); Micro.ResetGround(); Micro.RainOverrideMmH = 0f; PlaceActor(); } LastAction = "🌦 프리셋 → " + Engine.Spec.Name + " · " + F1(Engine.SurfaceC) + " °C · 습도 " + F0(Engine.Humidity * 100f) + " % · 지균풍 " + F1(Engine.WindMs) + " m/s"; Raise(); }
    public void SetTemperature(float c) { lock (_gate) Engine.SurfaceC = Math.Clamp(c, -30f, 45f); LastAction = "🌡 지상 기온 → " + F1(Engine.SurfaceC) + " °C(감률 6.5 °C/km)"; Raise(); }
    public void SetRain(float mmh) { lock (_gate) Micro.RainOverrideMmH = Math.Clamp(mmh, 0f, 150f); LastAction = "🌧 강우 강도 가산 → " + F1(Micro.RainOverrideMmH) + " mm/h"; Raise(); }
    public void SetWind(float ms) { lock (_gate) Engine.WindMs = Math.Clamp(ms, 0f, 60f); LastAction = "🧭 지균풍 → " + F1(Engine.WindMs) + " m/s"; Raise(); }
    public void SetWindDir(float deg) { lock (_gate) Engine.WindDirDeg = (deg % 360f + 360f) % 360f; LastAction = "🧭 풍향 → " + F0(Engine.WindDirDeg) + "°"; Raise(); }
    public void SetHumidity(float rh) { lock (_gate) Engine.Humidity = Math.Clamp(rh, 0.05f, 1f); LastAction = "💧 상대습도 → " + F0(Engine.Humidity * 100f) + " %"; Raise(); }
    public void SetStress(float k) { lock (_gate) Engine.PressureStress = Math.Clamp(k, 0.5f, 8f); LastAction = "🌀 기압 경도 스트레스 ×" + F1(Engine.PressureStress); Raise(); }
    public void SetTimeScale(float s) { lock (_gate) TimeScale = Math.Clamp(s, 1f, 60f); LastAction = "⏱ 시간 배율 ×" + F0(TimeScale); Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 가드 ON(헬름홀츠-호지 투영 · 마하 0.3 · NaN 격리 · CFL · 에너지)" : "⛔ 가드 OFF(발산 누적 · 폭주 비교)"; Raise(); }
    public void SetAero(bool on) { lock (_gate) Micro.AeroEnabled = on; LastAction = on ? "🌬 헤어/의상 항력 ON" : "🌬 항력 OFF"; Raise(); }
    public void SetPrecip(bool on) { lock (_gate) Micro.PrecipEnabled = on; LastAction = on ? "🌧 강수 입자 ON" : "🌧 강수 OFF"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) Engine.UseSimd = on; LastAction = on ? "⚡ SIMD(발산 · 야코비 · 기울기)" : "🐢 스칼라 쌍둥이(비트 동일)"; Raise(); }
    public void InjectFault() { lock (_gate) Engine.InjectFault(RealtimeAtmosphericWeatherSimulationEngine.Idx(16, 8, 16), float.NaN); LastAction = "💥 수치 사고 주입(중심 셀 NaN)"; Raise(); }
    public void ResetMetrics() { lock (_gate) { Guard.ResetMetrics(); MaxTickMs = 0; } LastAction = "↺ 지표 초기화"; Raise(); }

    /// <summary>1 프레임(무할당): 대기(가속 시간) → 강수 · 지면 → 헤어/의상(실시간).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("Microclimate.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var sim = Dt * TimeScale;
            Engine.Step(sim);
            Micro.StepPrecipitation(Engine, sim);
            Micro.StepAero(Engine, Dt);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _windS[h] = Micro.WindAtHead.Length(); _maxS[h] = Engine.MaxSpeed; _divS[h] = Guard.Enabled ? Guard.DivergenceAfter : Guard.DivergenceBefore;
            _rainS[h] = Engine.MeanPrecipMmH + Micro.RainOverrideMmH; _cloudS[h] = Engine.CloudCover * 100f;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Microclimate" };
            _loop.Start();
            LastAction = "▶ 기상 가동 · 60 Hz × 시간 ×" + F0(TimeScale) + " · 나비에-스톡스 → 무발산 투영 → 강수/젖음/적설 → 헤어/의상 항력";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Microclimate.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Microclimate.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
