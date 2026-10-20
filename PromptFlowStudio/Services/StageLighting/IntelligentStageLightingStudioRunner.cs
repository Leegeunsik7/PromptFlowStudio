using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Microclimate;
using PromptFlowStudio.Services.NarrativeTimeline;

namespace PromptFlowStudio.Services.StageLighting;

/// <summary>
/// [PromptFlow Studio v17.41.0] 💡 AI 지능형 무대 조명 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 v17.40 내러티브 타임라인(배우 블로킹 · 긴장도 · v17.39 프로그램 카메라 · 화자) 틱 →
/// v17.28 안개 소광 밀도(기상 러너 가동 시 가시거리 3.912/V · 아니면 수동) → 볼류메트릭/CCT 합성 →
/// 무빙헤드 역할 배정 · 에르미트 추적 · 광도 계획 → PSE/조도 광학 가드 → DMX 인코딩 · 44 Hz Art-Net 순으로 처리한다.
/// </summary>
public sealed class IntelligentStageLightingStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300;
    public const float Dt = 1f / Hz, DefaultFog = 0.015f;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist];
    private readonly AtmosphericWeatherStudioRunner? _weather;
    private int _msHead;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public IntelligentStageLightingStudioRunner(IServiceProvider sp) : this()
    {
        _weather = sp.GetService(typeof(AtmosphericWeatherStudioRunner)) as AtmosphericWeatherStudioRunner;
    }

    public IntelligentStageLightingStudioRunner() { }

    public event Action? OnChange;

    public NarrativeTimelineStudioRunner Story { get; } = new();
    public EmotionalVolumetricLightBeamSynthesizer Look { get; } = new();
    public RealtimeIntelligentStageLightingEngine Engine { get; } = new();
    public PhotosensitiveEpilepsyLuxSafetyGuard Guard { get; } = new();
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    /// <summary>수동 긴장도(−1 = v17.40 드라마투르기 자동).</summary>
    public float TensionOverride { get; private set; } = -1f;
    public float ManualFog { get; private set; } = DefaultFog;
    public bool FogFromWeather { get; private set; }
    public float Tension { get; private set; }
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float TickMsSeries(int i) => _msS[(_msHead - 1 - i + 2 * Hist) % Hist];

    // ══════════════════════ 제어 ══════════════════════

    public void SetMood(LightingMood m)
    {
        lock (_gate) Look.SetMood(m);
        LastAction = "🎨 무드 → " + EmotionalVolumetricLightBeamSynthesizer.MoodName(m) + "(2 s 크로스페이드)";
        Raise();
    }

    public void SetTensionOverride(float t)
    {
        lock (_gate) TensionOverride = t < 0f ? -1f : Math.Clamp(t, 0f, 1f);
        LastAction = TensionOverride < 0f ? "📜 긴장도 = v17.40 드라마투르기 자동" : "🎚 긴장도 수동 " + TensionOverride.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void SetCct(float k)
    {
        lock (_gate) Look.CctOverrideK = k <= 0f ? 0f : Math.Clamp(k, EmotionalVolumetricLightBeamSynthesizer.MinK, EmotionalVolumetricLightBeamSynthesizer.MaxK);
        LastAction = Look.CctOverrideK > 0f ? "🌡 CCT 고정 " + Look.CctOverrideK.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " K" : "🌡 CCT = 긴장도 자동(2700 → 6500 K)";
        Raise();
    }

    public void SetFog(float density)
    {
        lock (_gate) ManualFog = Math.Clamp(density, 0f, 0.2f);
        LastAction = "🌫 수동 안개 σ " + ManualFog.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " /m";
        Raise();
    }

    public void SetGuard(bool on)
    {
        lock (_gate) { Guard.Enabled = on; Guard.Reset(); }
        LastAction = on ? "🛡 광학 가드 ON(BT.1702 3 플래시/s · 2.8 Hz 스트로브 · 2500 lux)" : "⛔ 광학 가드 OFF(위반 비교)";
        Raise();
    }

    public void Park(int ch, int value) { lock (_gate) Engine.Park(ch, value); }

    /// <summary>[v17.42.0] 파이로 스파크 클러스터 · 화염광 동적 포인트 라이트 주입(외부 스레드 · 무할당).</summary>
    public void InjectPointLights(ReadOnlySpan<DynamicPointLight> lights) { lock (_gate) Engine.SetPointLights(lights); }

    public void ReleaseAll() { lock (_gate) Engine.ReleaseAll(); LastAction = "🎚 파킹 전체 해제"; Raise(); }

    public bool EnableArtNet(string host)
    {
        bool ok;
        lock (_gate) ok = Engine.EnableArtNet(host.Trim());
        LastAction = ok ? "📡 Art-Net UDP → " + Engine.ArtNetTarget + " · 44 Hz" : "⚠ Art-Net 주소 오류(IP 리터럴)";
        Raise();
        return ok;
    }

    public void DisableArtNet() { lock (_gate) Engine.DisableArtNet(); LastAction = "📡 Art-Net UDP 해제(루프백 노드만)"; Raise(); }

    public void ResetMetrics()
    {
        lock (_gate) { Guard.ResetMetrics(); Engine.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("StageLighting.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            Story.Step();
            var tension = Tension = TensionOverride >= 0f ? TensionOverride : Story.Arc.Tension;
            var fog = ManualFog;
            FogFromWeather = _weather is { IsRunning: true };
            if (FogFromWeather) fog = 3.912f / MathF.Max(_weather!.Engine.FogVisibilityM, 1f);
            Look.Step(Dt, tension, Story.Arc.Affinity, fog);
            var a0 = new Vector2(Story.Output(0), Story.Output(1));
            var a1 = new Vector2(Story.Output(2), Story.Output(3));
            Engine.Plan(a0, a1, Story.Program, Story.Arc.Speaker, Look, Guard.ClampStrobeHz(Look.StrobeRequestHz), Dt);
            Guard.Process(Engine.Raw, Engine.Luminance, Engine.RedLuminance, Engine.Lux0, Engine.Lux1, Engine.Out, Dt);
            Engine.Commit(Dt);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_msHead] = ms; _msHead = (_msHead + 1) % Hist;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.StageLighting" };
            _loop.Start();
        }
        LastAction = "▶ 지능형 무대 조명 60 Hz · Art-Net 44 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("StageLighting.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("StageLighting.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Engine.Dispose();
        Story.Dispose();
    }
}
