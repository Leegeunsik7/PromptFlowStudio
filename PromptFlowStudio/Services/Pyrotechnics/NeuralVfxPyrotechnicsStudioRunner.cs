using System.Numerics;
using PromptFlowStudio.Services.ContinuumPhysics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.StageLighting;

namespace PromptFlowStudio.Services.Pyrotechnics;

/// <summary>
/// [PromptFlow Studio v17.42.0] 🔥 AI 뉴럴 특수효과 &amp; 파이로 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 사전 수치 가드(주입 스파이크) → 연속 연료/점화 소스 · v17.26 파괴 이벤트 자동 점화 → 연소 유체 스텝 →
/// 사후 수치 가드(에너지 보존) → 스파크 연속 방출(버짓 승인) · 탄도 적분 → 버짓 집행 → 발광 클러스터 포인트 라이트 → v17.41 무대 조명 주입 순으로 처리한다.
/// </summary>
public sealed class NeuralVfxPyrotechnicsStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300;
    public const float Dt = 1f / Hz;
    public static readonly Vector3 DefaultOrigin = new(0f, 1.2f, 0f), EmitterBase = new(0f, 0.35f, 0f);

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _liveS = new float[Hist], _tempS = new float[Hist], _cdS = new float[Hist];
    private readonly IntelligentStageLightingStudioRunner? _stage;
    private readonly ContinuumPhysicsStudioRunner? _physics;
    private int _msHead, _series, _lastFracture = -1;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public NeuralVfxPyrotechnicsStudioRunner(IServiceProvider sp) : this()
    {
        _stage = sp.GetService(typeof(IntelligentStageLightingStudioRunner)) as IntelligentStageLightingStudioRunner;
        _physics = sp.GetService(typeof(ContinuumPhysicsStudioRunner)) as ContinuumPhysicsStudioRunner;
    }

    public NeuralVfxPyrotechnicsStudioRunner() { }

    public event Action? OnChange;

    public RealtimeNeuralCombustionFluidEngine Fluid { get; } = new();
    public ProceduralSparkPyrotechnicsSynthesizer Synth { get; } = new();
    public VfxParticleBudgetEnergyExplosionGuard Guard { get; } = new();
    public PyroPreset Preset { get; private set; } = PyroPreset.Campfire;
    public PyroPresetSpec Spec => RealtimeNeuralCombustionFluidEngine.Presets[(int)Preset];
    public Vector3 Camera { get; } = new(0f, 1.7f, 6f);
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public int Detonations { get; private set; }
    public int PhysicsDetonations { get; private set; }
    public bool LightsToStage { get; private set; } = true;
    public bool StageLinked => _stage is not null;
    public bool PhysicsLinked => _physics is not null;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float TickMsSeries(int i) => _msS[Ix(i)];
    public float LiveSeries(int i) => _liveS[Ix(i)];
    public float TempSeries(int i) => _tempS[Ix(i)];
    public float CandelaSeries(int i) => _cdS[Ix(i)];
    private int Ix(int i) => (_msHead - 1 - i + 2 * Hist) % Hist;

    public static string PresetName(PyroPreset p) => RealtimeNeuralCombustionFluidEngine.Presets[(int)p].Name;

    // ══════════════════════ 제어 ══════════════════════

    public void SetPreset(PyroPreset p)
    {
        lock (_gate) Preset = p;
        LastAction = "🎛 프리셋 → " + PresetName(p);
        Raise();
    }

    public void Detonate() => Detonate(DefaultOrigin, "💥 점화(Detonate)");

    public void Detonate(Vector3 at, string label)
    {
        int n;
        lock (_gate)
        {
            var s = Spec;
            Fluid.Inject(at, s.BurstRadius, s.BurstFuel, s.BurstTemp, 0.4f, s.BurstSpeed, Vector3.Zero);
            n = Synth.Emit(at, s.BurstSparks, s, Vector3.Zero, Guard, Camera);
            Synth.StartShock(at, MathF.Min(1f, s.BurstSpeed / 22f));
            Detonations++;
        }
        LastAction = label + " · " + PresetName(Preset) + " · 스파크 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void SetGuard(bool on)
    {
        lock (_gate) { Guard.Enabled = on; Guard.ResetEnergy(Fluid.ThermalEnergy); }
        LastAction = on ? "🛡 버짓/수치 가드 ON(65,536 · NaN · CFL · 에너지 보존)" : "⛔ 가드 OFF(링 덮어쓰기 · 발산 비교)";
        Raise();
    }

    public void SetBudget(int budget)
    {
        lock (_gate) Guard.Budget = budget;
        LastAction = "🎯 파티클 버짓 " + Guard.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void SetLightsToStage(bool on)
    {
        lock (_gate)
        {
            LightsToStage = on;
            if (!on) _stage?.InjectPointLights(ReadOnlySpan<DynamicPointLight>.Empty);
        }
        LastAction = on ? "💡 v17.41 무대 조명 포인트 라이트 주입 ON" : "💡 무대 조명 주입 OFF";
        Raise();
    }

    public void ResetSim()
    {
        lock (_gate) { Fluid.Reset(); Synth.Clear(); Guard.ResetEnergy(); }
        _stage?.InjectPointLights(ReadOnlySpan<DynamicPointLight>.Empty);
        LastAction = "↺ 시뮬레이션 초기화";
        Raise();
    }

    public void ResetMetrics()
    {
        lock (_gate) { Guard.ResetMetrics(); Synth.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    /// <summary>UI 뷰어 — 궤도 레이마치(게이트 하 · 호출자 버퍼).</summary>
    public void RenderView(float yaw, Span<float> rgb, int res) { lock (_gate) Fluid.RenderView(yaw, Spec.Plasma, rgb, res); }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        var auto = false; var at = Vector3.Zero;
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("Pyro.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            var s = Spec;
            Guard.Sanitize(Fluid, Dt, false);
            if (s.FuelRate > 0f) Fluid.Inject(EmitterBase, s.EmitRadius, s.FuelRate * Dt, s.EmitTemp * Dt * 4f, 0f, 0f, new Vector3(0f, 0.25f, 0f));
            if (_physics is { IsRunning: true })
            {
                var ev = _physics.Fracture.Events;
                if (_lastFracture >= 0 && ev > _lastFracture)
                {
                    var p = _physics.Fracture.LastImpact;
                    at = new Vector3(Math.Clamp(p.X * 4f - 2f, -3f, 3f), 0.6f, Math.Clamp(p.Z * 4f - 2f, -3f, 3f));
                    auto = true;
                }
                _lastFracture = ev;
            }
            Fluid.Step(Dt, s);
            Guard.Sanitize(Fluid, Dt, true);
            Synth.Continuous(Dt, s, EmitterBase, Guard, Camera);
            Synth.Step(Dt);
            Guard.Enforce(Synth, Camera);
            Synth.ComputeLights(Fluid, s.Plasma);
            if (LightsToStage) _stage?.InjectPointLights(Synth.LightSpan);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_msHead] = ms; _liveS[_msHead] = Synth.Live; _tempS[_msHead] = Fluid.MaxTempK; _cdS[_msHead] = Synth.TotalCandela;
            _msHead = (_msHead + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
        if (auto) { PhysicsDetonations++; Detonate(at, "💥 v17.26 파괴 충돌 자동 점화"); }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Pyro" };
            _loop.Start();
        }
        LastAction = "▶ 뉴럴 파이로 60 Hz · " + PresetName(Preset);
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        _stage?.InjectPointLights(ReadOnlySpan<DynamicPointLight>.Empty);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Pyro.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Pyro.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
