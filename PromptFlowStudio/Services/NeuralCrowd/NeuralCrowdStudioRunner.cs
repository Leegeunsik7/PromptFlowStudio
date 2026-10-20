using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.NeuralCrowd;

/// <summary>
/// [PromptFlow Studio v17.29.0] 👥 AI 뉴럴 군중 &amp; 에이전트 생태계 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 v17.28 기상 결선(풍속 · 강수 · 운량) → ORCA 흐름장 엔진(가드 밀도/LoD · 침투 투영) → 사회적 감정 합성(파면 · 전염 · 주목 · 웅성거림) 순으로 처리한다.
/// </summary>
public sealed class NeuralCrowdStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float Dt = 1f / Hz;

    private readonly object _gate = new();
    private readonly Microclimate.AtmosphericWeatherStudioRunner? _weather;
    private readonly float[] _msS = new float[SeriesLen], _speedS = new float[SeriesLen], _densS = new float[SeriesLen], _panicS = new float[SeriesLen], _penS = new float[SeriesLen], _loudS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public NeuralCrowdStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(Microclimate.AtmosphericWeatherStudioRunner)) as Microclimate.AtmosphericWeatherStudioRunner) { }

    public NeuralCrowdStudioRunner() : this((Microclimate.AtmosphericWeatherStudioRunner?)null) { }

    private NeuralCrowdStudioRunner(Microclimate.AtmosphericWeatherStudioRunner? weather)
    {
        _weather = weather;
        Guard = new AgentDensityDivergenceAntiPenetrationGuard();
        Engine = new RealtimeOrcaCrowdNavigationEngine(Guard, new WorldExpansion.GenerativeNeuralWorldExpansionEngine());
        Social = new SocialEmotionEnvironmentalReactionSynthesizer();
        Social.ActorPos = Engine.AttractPoint;
        LastAction = "📋 대기 · 에이전트 " + Engine.Count + " · " + RealtimeOrcaCrowdNavigationEngine.G + "×" + RealtimeOrcaCrowdNavigationEngine.G + " m 광장 · 출입문 병목 " + (RealtimeOrcaCrowdNavigationEngine.DoorX1 - RealtimeOrcaCrowdNavigationEngine.DoorX0 + 1) + " m · 비상구 4";
    }

    public event Action? OnChange;

    public RealtimeOrcaCrowdNavigationEngine Engine { get; }
    public AgentDensityDivergenceAntiPenetrationGuard Guard { get; }
    public SocialEmotionEnvironmentalReactionSynthesizer Social { get; }
    public bool WeatherCoupled { get; private set; } = true;
    public bool WeatherAvailable => _weather is not null;
    /// <summary>수동 기상 입력(결선 OFF 또는 기상 러너 부재 시).</summary>
    public float ManualWindMs { get; private set; }
    public float ManualStorm { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float SpeedSeries(int i) => _speedS[Idx(i)];
    public float DensitySeries(int i) => _densS[Idx(i)];
    public float PanicSeries(int i) => _panicS[Idx(i)];
    public float PenetrationSeries(int i) => _penS[Idx(i)];
    public float LoudnessSeries(int i) => _loudS[Idx(i)];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetAgentCount(int n) { lock (_gate) { Engine.SetAgentCount(n); Social.Reset(); } LastAction = "👥 에이전트 " + Engine.Count + " 재배치(포아송 디스크 0.75 m)"; Raise(); }
    public void SetGoal(int which, Vector2 p) { lock (_gate) Engine.SetGoal(which, p); var g = which == 0 ? Engine.GoalA : Engine.GoalB; LastAction = "🎯 목표 " + (which == 0 ? "A" : "B") + " → (" + F0(g.X) + ", " + F0(g.Y) + ") · 흐름장 재구축"; Raise(); }
    public void SetMood(CrowdMood m, float intensity) { lock (_gate) Social.SetMood(m, intensity); LastAction = "🎭 군중 심리 → " + (m == CrowdMood.Calm ? "평온" : m == CrowdMood.Panic ? "패닉" : "흥분") + " " + F0(Social.MoodIntensity * 100f) + " %"; Raise(); }
    public void TriggerLightning() { lock (_gate) Social.Push(CrowdStimulusKind.Lightning, new Vector2(20f, 18f), 1.2f); LastAction = "⚡ 낙뢰(20, 18) · 천둥 343 m/s 파면 · 공포 전염"; Raise(); }
    public void TriggerGust() { lock (_gate) Social.Push(CrowdStimulusKind.Gust, new Vector2(0f, 32f), 1f); LastAction = "💨 돌풍 전선(서쪽) · 14 m/s 파면"; Raise(); }
    public void TriggerFracture()
    {
        lock (_gate) { var at = new Vector2(24f, 40f); Engine.AddRubble(at, 2.5f); Social.Push(CrowdStimulusKind.Fracture, at, 1.5f); }
        LastAction = "💥 물리 파괴(v17.26) · 잔해 " + Engine.RubbleCells + " 셀 · 충격파 120 m/s · 흐름장 재구축"; Raise();
    }
    public void ClearRubble() { lock (_gate) Engine.ClearRubble(); LastAction = "🧹 잔해 제거 · 흐름장 재구축"; Raise(); }
    public void SetSpeech(float level) { lock (_gate) Social.SpeechLevel = Math.Clamp(level, 0f, 1f); LastAction = "🗣 액터 발화(v17.18) → " + F0(Social.SpeechLevel * 100f) + " %"; Raise(); }
    public void SetGaze(float deg) { lock (_gate) Social.GazeDeg = (deg % 360f + 360f) % 360f; LastAction = "👁 액터 시선(v17.12) → " + F0(Social.GazeDeg) + "°"; Raise(); }
    public void SetCameraYaw(float deg) { lock (_gate) Guard.CamYawDeg = (deg % 360f + 360f) % 360f; LastAction = "🎥 카메라 요 → " + F0(Guard.CamYawDeg) + "° · 절두체 LoD 재분류"; Raise(); }
    public void SetWeatherCoupled(bool on) { lock (_gate) WeatherCoupled = on; LastAction = on ? (WeatherAvailable ? "⛈ v17.28 기상 결선 ON" : "⛈ 기상 러너 없음 — 수동 입력 사용") : "⛈ 기상 결선 OFF(수동)"; Raise(); }
    public void SetManualWeather(float windMs, float storm) { lock (_gate) { ManualWindMs = Math.Clamp(windMs, 0f, 40f); ManualStorm = Math.Clamp(storm, 0f, 1f); } LastAction = "🌬 수동 기상 · 풍속 " + F1(ManualWindMs) + " m/s · 뇌우 " + F0(ManualStorm * 100f) + " %"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 가드 ON(밀도 상한 · 비탄성 투영 · 포아송 간격 · NaN 격리 · LoD)" : "⛔ 가드 OFF(순수 ORCA · 침투/튕김 비교)"; Raise(); }
    public void SetLod(bool on) { lock (_gate) Guard.LodEnabled = on; LastAction = on ? "🎥 절두체 LoD ON(시야 밖 1/4 · 원거리 1/8)" : "🎥 LoD OFF(전원 매 프레임)"; Raise(); }
    public void SetSocial(bool on) { lock (_gate) Social.Enabled = on; LastAction = on ? "😱 감정 → 행동 결선 ON" : "😐 감정 결선 OFF"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) Engine.UseSimd = on; LastAction = on ? "⚡ SIMD 적분" : "🐢 스칼라 쌍둥이(비트 동일)"; Raise(); }
    public void InjectFault() { lock (_gate) Engine.InjectFault(Engine.Count / 2); LastAction = "💥 수치 사고 주입(에이전트 " + Engine.Count / 2 + " NaN)"; Raise(); }
    public void ResetMetrics() { lock (_gate) { Guard.ResetMetrics(); MaxTickMs = 0; } LastAction = "↺ 지표 초기화"; Raise(); }

    /// <summary>1 프레임(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("NeuralCrowd.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (WeatherCoupled && _weather is not null)
            {
                Social.AmbientWindMs = _weather.Micro.WindAtHead.Length();
                Social.PrecipMmH = _weather.Engine.MeanPrecipMmH + _weather.Micro.RainOverrideMmH;
                Social.StormIntensity = Math.Clamp((_weather.Engine.CloudCover - 0.3f) / 0.5f, 0f, 1f) * Math.Clamp(Social.PrecipMmH / 15f, 0f, 1f);
            }
            else { Social.AmbientWindMs = ManualWindMs; Social.PrecipMmH = 0f; Social.StormIntensity = ManualStorm; }
            Engine.AttractPoint = Social.ActorPos;
            Engine.Step(Dt);
            Social.Step(Engine, Dt);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _speedS[h] = Engine.MeanSpeed; _densS[h] = Guard.PeakDensity; _penS[h] = Guard.MaxPenetration * 100f;
            _panicS[h] = Engine.ActiveCount == 0 ? 0f : Social.PanicCount * 100f / Engine.ActiveCount; _loudS[h] = Social.LastMurmur.Loudness;
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
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.NeuralCrowd" };
            _loop.Start();
            LastAction = "▶ 군중 가동 · 60 Hz · 흐름장 → ORCA LP → 비탄성 투영 → 감정 전염 → 웅성거림 · 에이전트 " + Engine.ActiveCount + " · 침투 " + F2(Guard.MaxPenetration * 100f) + " cm";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("NeuralCrowd.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("NeuralCrowd.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
