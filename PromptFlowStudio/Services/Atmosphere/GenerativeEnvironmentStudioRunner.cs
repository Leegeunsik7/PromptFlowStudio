using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Atmosphere;

/// <summary>
/// [PromptFlow Studio v16.84.0] 🌦 AI 제너레이티브 환경 &amp; 미세 기상 스튜디오 러너(60 Hz · <see cref="TickOnce"/> 무할당).
/// 기상 합성 → 태양(수동 고도/방위 | NOAA 시계) · 달 → 대기 산란/안개 → 습윤/적설 물 수지(지표 습윤 → 습도 피드백) →
/// 지형 셰이딩(Lambert + GGX 수막 반사 · 섬광) → 공중 원근 SIMD 합성 → 3DGS(지형 1600 + 강수 표시 입자).
/// </summary>
public sealed class GenerativeEnvironmentStudioRunner : IDisposable
{
    public const int Hz = 60, DisplayParticles = 1200;
    private const int Cells = EnvironmentalBiomeWetnessGuard.Cells;

    private readonly object _gate = new();
    private readonly AtmosphericScatteringLightingEngine _atmo;
    private readonly DynamicMicroWeatherSimulatorEngine _weather;
    private readonly EnvironmentalBiomeWetnessGuard _wet;
    private readonly GenerativeWorldProceduralEngine _world;
    private readonly GaussianScene _splats = new(Cells + DisplayParticles);
    private readonly float[] _r = new float[Cells], _g = new float[Cells], _b = new float[Cells], _dist = new float[Cells], _t = new float[Cells];
    public readonly float[] TerrainR = new float[Cells], TerrainG = new float[Cells], TerrainB = new float[Cells];
    private CancellationTokenSource? _cts;
    private long _frame;
    private float _manualElev = 35f, _manualAz = 140f;

    public GenerativeEnvironmentStudioRunner(IServiceProvider sp)
        : this((AtmosphericScatteringLightingEngine?)sp.GetService(typeof(AtmosphericScatteringLightingEngine)) ?? new AtmosphericScatteringLightingEngine(),
               (DynamicMicroWeatherSimulatorEngine?)sp.GetService(typeof(DynamicMicroWeatherSimulatorEngine)) ?? new DynamicMicroWeatherSimulatorEngine(),
               (EnvironmentalBiomeWetnessGuard?)sp.GetService(typeof(EnvironmentalBiomeWetnessGuard)) ?? new EnvironmentalBiomeWetnessGuard(),
               (GenerativeWorldProceduralEngine?)sp.GetService(typeof(GenerativeWorldProceduralEngine)) ?? new GenerativeWorldProceduralEngine()) { }

    public GenerativeEnvironmentStudioRunner(AtmosphericScatteringLightingEngine atmo, DynamicMicroWeatherSimulatorEngine weather, EnvironmentalBiomeWetnessGuard wet, GenerativeWorldProceduralEngine world)
    {
        _atmo = atmo; _weather = weather; _wet = wet; _world = world;
        SyncWorld();
        _weather.SnapToTarget();
        lock (_gate) { Advance(1f / Hz); }
    }

    public event Action? OnChange;

    public AtmosphericScatteringLightingEngine Atmosphere => _atmo;
    public DynamicMicroWeatherSimulatorEngine Weather => _weather;
    public EnvironmentalBiomeWetnessGuard Wet => _wet;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public bool ClockMode { get; private set; }
    public bool AutoTime { get; set; }
    public float Hour { get; private set; } = 15f;
    public float TimeSpeed { get; set; } = 0.1f;
    public int DayOfYear { get; private set; } = 172;
    public float Latitude { get; private set; } = 37.5f;
    public float MoonPhase { get; private set; } = 0.5f;
    public Vector3 Eye { get; private set; }
    public Vector3 LookTarget { get; private set; }
    public Vector3 BoxCenter { get; private set; }
    public float SunElevation => _atmo.SunElevationDeg;
    public float SunAzimuth { get; private set; }
    /// <summary>슬라이더 설정점 — 수동 모드는 다음 틱 전에도 입력값 즉시 반영(틱 산출값으로 되튕김 방지).</summary>
    public float SunElevationSetpoint => ClockMode ? SunElevation : _manualElev;
    public float SunAzimuthSetpoint => ClockMode ? SunAzimuth : _manualAz;
    public double LastTickMs { get; private set; }
    public double LastShadeMs { get; private set; }
    public long FireflyClamps { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>OpenUSD 절차 월드(v16.72) → 지형 재샘플 · 카메라 · 시나리오 메타데이터(기온/습도/안개/태양) 동기화.</summary>
    public void SyncWorld()
    {
        lock (_gate)
        {
            _wet.Rebuild(_world);
            var mx = 0f;
            for (var c = 0; c < Cells; c++) mx = MathF.Max(mx, _wet.Height(c));
            Eye = new Vector3(0f, MathF.Max(mx, 0f) + 9f, -58f);
            LookTarget = new Vector3(0f, MathF.Max(mx, 0f) * 0.3f, 12f);
            var f = Vector3.Normalize(LookTarget - Eye);
            var bc = Eye + new Vector3(f.X, 0f, f.Z) * 22f;
            BoxCenter = new Vector3(bc.X, MathF.Max(_world.HeightAt(bc.X, bc.Z), 0f), bc.Z);
            var tr = _world.Traits;
            var s = _weather.Target;
            s.Temperature = -15f + 50f * tr.Temperature; s.Humidity = 0.2f + 0.75f * tr.Moisture; s.Fog = Math.Clamp(tr.Fog * 1.5f, 0f, 1f);
            _weather.SetScenario(s);
            _manualElev = tr.SunElevation * 85f - 8f; _manualAz = tr.SunAzimuth * 360f; ClockMode = false;
        }
        LastAction = "🗺 월드 동기화 — 육지 " + _wet.LandCells.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 셀";
        Raise();
    }

    public void NewShot()
    {
        lock (_gate) { _wet.ResetState(); _weather.SnapToTarget(); _frame = 0; Advance(1f / Hz); }
        LastAction = "🎬 새 샷 — 표면 건조 · 기상 즉시 수렴";
        Raise();
    }

    /// <summary>태양 고도각/방위 수동 제어(°).</summary>
    public void SetSun(float elevDeg, float azDeg)
    {
        _manualElev = Math.Clamp(elevDeg, -30f, 90f); _manualAz = ((azDeg % 360f) + 360f) % 360f; ClockMode = false;
        Raise();
    }

    /// <summary>시계 모드: 시(0..24) · 연중 일 · 위도 → NOAA 태양 위치.</summary>
    public void SetClock(float hour, int dayOfYear, float latitude)
    {
        Hour = ((hour % 24f) + 24f) % 24f; DayOfYear = Math.Clamp(dayOfYear, 1, 365); Latitude = Math.Clamp(latitude, -89f, 89f); ClockMode = true;
        Raise();
    }

    public void SetMoonPhase(float phase) { MoonPhase = phase - MathF.Floor(phase); Raise(); }

    public void SetScenario(in WeatherScenario s) { _weather.SetScenario(s); LastAction = "🌦 시나리오 적용"; Raise(); }

    public void SetPrecipitation(float mmH) { var s = _weather.Target; s.Precipitation = mmH; _weather.SetScenario(s); Raise(); }
    public void SetCloudCoverage(float c) { var s = _weather.Target; s.CloudCoverage = c; _weather.SetScenario(s); Raise(); }
    public void SetTemperature(float t) { var s = _weather.Target; s.Temperature = t; _weather.SetScenario(s); Raise(); }
    public void SetWind(float speed, float yawDeg) { var s = _weather.Target; s.WindSpeed = speed; s.WindYawDeg = yawDeg; _weather.SetScenario(s); Raise(); }
    public void SetFog(float f) { var s = _weather.Target; s.Fog = f; _weather.SetScenario(s); Raise(); }
    public void SetStorm(float st) { var s = _weather.Target; s.Storm = st; _weather.SetScenario(s); Raise(); }

    /// <summary>프롬프트(한/영) → 기상 + 시간대.</summary>
    public int TriggerPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { LastAction = "⚠ 프롬프트 없음"; Raise(); return 0; }
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        var s = _weather.Target;
        var n = DynamicMicroWeatherSimulatorEngine.ParsePrompt(prompt, ref s);
        if (n > 0) _weather.SetScenario(s);
        if (Has(prompt, "일출", "sunrise") || Has(prompt, "새벽", "dawn")) { SetSun(3f, 75f); n++; }
        else if (Has(prompt, "석양", "sunset") || Has(prompt, "노을", "dusk")) { SetSun(2f, 290f); n++; }
        else if (Has(prompt, "황혼", "twilight") || Has(prompt, "블루아워", "blue hour")) { SetSun(-4f, 295f); n++; }
        else if (Has(prompt, "한밤", "midnight") || Has(prompt, "밤", "night") || Has(prompt, "달빛", "moonlight")) { SetClock(23.5f, DayOfYear, Latitude); SetMoonPhase(0.5f); n++; }
        else if (Has(prompt, "정오", "noon") || Has(prompt, "한낮", "midday")) { SetClock(12.5f, DayOfYear, Latitude); n++; }
        LastAction = n > 0 ? "✨ 프롬프트 → 반영 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건" : "⚠ 인식된 키워드 없음";
        Raise();
        return n;
    }

    /// <summary>1프레임(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Environment.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Advance(1f / Hz);
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void Advance(float dt)
    {
        if (AutoTime && ClockMode) Hour = (Hour + TimeSpeed * dt) % 24f;
        _weather.SurfaceMoisture = _wet.MeanWetness;
        _weather.Step(dt);
        Vector3 sun;
        if (ClockMode)
        {
            var (e, a) = AtmosphericScatteringLightingEngine.SolarPosition(DayOfYear, Hour, Latitude);
            sun = AtmosphericScatteringLightingEngine.FromAngles(e, a); SunAzimuth = a;
        }
        else { sun = AtmosphericScatteringLightingEngine.FromAngles(_manualElev, _manualAz); SunAzimuth = _manualAz; }
        var (me, ma) = AtmosphericScatteringLightingEngine.LunarPosition(DayOfYear, ClockMode ? Hour : 12f + (_manualAz - 180f) / 15f, Latitude, MoonPhase);
        var moon = AtmosphericScatteringLightingEngine.FromAngles(me, ma);
        var st = _weather.State;
        _atmo.Update(sun, moon, MoonPhase, st.CloudCoverage, _weather.CloudOpticalDepth, _weather.FogExtinction, _weather.Haze, dt);

        var p = st.Precipitation; var sf = _weather.SnowFraction;
        var ins = Math.Clamp(AtmosphericScatteringLightingEngine.Luminance(_atmo.SunIrradiance) * MathF.Max(sun.Y, 0f) / 0.8f, 0f, 1f);
        var fall = 6f + (1f - 6f) * sf;
        _wet.Step(dt, new PrecipitationInput(p * (1f - sf), p * sf, st.Temperature, _weather.RelativeHumidity, st.WindSpeed, ins, fall));
        Shade(sun, moon);
        WriteSplats();
    }

    /// <summary>지형 셰이딩: 알베도/π · (태양 + 달 + 반구 환경 + 섬광) + GGX 수막 반사(파이어플라이 클램프) → 공중 원근(SIMD).</summary>
    private void Shade(Vector3 l, Vector3 lm)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var es = _atmo.SunIrradiance; var em = _atmo.MoonLight; var amb = _atmo.AmbientIrradiance;
        var flash = new Vector3(0.8f, 0.85f, 1f) * (_weather.FlashIntensity * 1.5f / MathF.Max(_atmo.Exposure, 1e-3f));
        var eye = Eye;
        for (var c = 0; c < Cells; c++)
        {
            var h = _wet.Height(c);
            var biome = _wet.BiomeOf(c);
            var pos = EnvironmentalBiomeWetnessGuard.CellCenter(c, biome == Biome.Ocean ? MathF.Max(h, 0f) : h);
            var n = biome == Biome.Ocean && h < 0f ? Vector3.UnitY : _wet.Normal(c);
            var toEye = eye - pos; var dist = toEye.Length(); var v = toEye / MathF.Max(dist, 1e-3f);
            var alb = new Vector3(_wet.AlbedoR[c], _wet.AlbedoG[c], _wet.AlbedoB[c]);
            var nl = MathF.Max(Vector3.Dot(n, l), 0f); var nm = MathF.Max(Vector3.Dot(n, lm), 0f);
            var col = alb / MathF.PI * (es * nl + em * nm + amb * (0.5f + 0.5f * n.Y) + flash);
            if (nl > 0f)
            {
                var hv = Vector3.Normalize(l + v);
                var nh = MathF.Max(Vector3.Dot(n, hv), 0f); var lh = MathF.Max(Vector3.Dot(l, hv), 0.1f);
                var a = _wet.Roughness[c] * _wet.Roughness[c]; var a2 = a * a;
                var den = nh * nh * (a2 - 1f) + 1f;
                var d = a2 / (MathF.PI * den * den);
                var f0 = biome == Biome.Ocean ? 0.02f : _wet.F0[c];
                var fr = f0 + (1f - f0) * MathF.Pow(1f - lh, 5f);
                var spec = d * fr / (4f * lh * lh) * nl;
                if (spec > 30f) { spec = 30f; FireflyClamps++; }
                col += es * spec;
            }
            _r[c] = col.X; _g[c] = col.Y; _b[c] = col.Z; _dist[c] = dist;
        }
        _atmo.ApplyAerial(_r, _g, _b, _dist, _t);
        for (var c = 0; c < Cells; c++)
        {
            var dsp = _atmo.ToDisplay(new Vector3(_r[c], _g[c], _b[c]));
            TerrainR[c] = dsp.X; TerrainG[c] = dsp.Y; TerrainB[c] = dsp.Z;
        }
        LastShadeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void WriteSplats()
    {
        _splats.Clear();
        for (var c = 0; c < Cells; c++)
        {
            var biome = _wet.BiomeOf(c); var h = _wet.Height(c);
            var n = biome == Biome.Ocean && h < 0f ? Vector3.UnitY : _wet.Normal(c);
            _splats.Add(EnvironmentalBiomeWetnessGuard.CellCenter(c, MathF.Max(h, biome == Biome.Ocean ? 0f : h)), PhotorealisticMicroSkinSssSynthesizer.FromTo(Vector3.UnitZ, n),
                new Vector3(1.3f, 1.3f, 0.05f), 0.98f, new Vector3(TerrainR[c], TerrainG[c], TerrainB[c]));
        }
        var active = _weather.ActiveParticles;
        var stride = Math.Max(1, active / DisplayParticles);
        var drop = _atmo.ToDisplay(_atmo.AmbientIrradiance * (0.6f / MathF.PI) + _atmo.SunIrradiance * 0.05f);
        for (var i = 0; i < active && _splats.Count < _splats.Capacity; i += stride)
        {
            var pos = BoxCenter + _weather.Position(i);
            if (_weather.ParticleKind(i) == PrecipKind.Snow)
                _splats.Add(pos, Quaternion.Identity, new Vector3(0.012f), 0.9f, new Vector3(0.95f, 0.95f, 0.97f));
            else
            {
                var vel = _weather.Velocity(i); var s = vel.Length();
                _splats.Add(pos, PhotorealisticMicroSkinSssSynthesizer.FromTo(Vector3.UnitZ, vel / MathF.Max(s, 1e-3f)), new Vector3(0.002f, 0.002f, 0.012f * s), 0.45f, drop);
            }
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 환경 시뮬레이션 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Environment.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Environment.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
