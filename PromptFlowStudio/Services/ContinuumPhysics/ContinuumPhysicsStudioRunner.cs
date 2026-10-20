using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.ContinuumPhysics;

/// <summary>
/// [PromptFlow Studio v17.26.0] ⚛ AI 실시간 뉴럴 물리 &amp; 연속체 역학 스튜디오 러너(60 Hz 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 프레임 시간 배율만큼 MPM 을 전진(가드 CFL 하위 단계 · 콜라이더 SDF · 입자 투영) → 보로노이 파괴/소성 표면 합성 → 해밀토니안 에너지 클램프 순으로 처리하고,
/// 시나리오 주기(4 s) 가 끝나면 같은 재질 블록을 재생성한다.
/// </summary>
public sealed class ContinuumPhysicsStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _keS = new float[SeriesLen], _eeS = new float[SeriesLen], _peS = new float[SeriesLen], _hS = new float[SeriesLen], _dentS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _pe0 = float.NaN;

    public ContinuumPhysicsStudioRunner(IServiceProvider sp) : this() { }

    public ContinuumPhysicsStudioRunner()
    {
        Guard = new NonPenetrationEnergyConservationPhysicsGuard();
        Engine = new RealtimeMpmContinuumPhysicsEngine(Guard);
        Fracture = new ProceduralFracturePlasticDeformationSynthesizer();
        Fracture.Attach(Engine);
        LastAction = "📋 대기 · " + ContinuumMaterials.Names[(int)Engine.Material] + " 입자 " + Engine.Count + " · 격자 " + RealtimeMpmContinuumPhysicsEngine.N + "³ · APIC/MLS-MPM";
    }

    public event Action? OnChange;

    public RealtimeMpmContinuumPhysicsEngine Engine { get; }
    public NonPenetrationEnergyConservationPhysicsGuard Guard { get; }
    public ProceduralFracturePlasticDeformationSynthesizer Fracture { get; }
    public float TimeScale { get; private set; } = 0.35f;
    public bool AutoLoop { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public int Loops { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float KineticSeries(int i) => _keS[Idx(i)];
    public float ElasticSeries(int i) => _eeS[Idx(i)];
    /// <summary>중력 위치 에너지(초기 대비 변화량).</summary>
    public float PotentialSeries(int i) => _peS[Idx(i)];
    public float TotalSeries(int i) => _hS[Idx(i)];
    public float DentSeries(int i) => _dentS[Idx(i)];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetMaterial(ContinuumMaterial m) { lock (_gate) Respawn(m); LastAction = "🧱 재질 → " + ContinuumMaterials.Names[(int)m] + " · E " + F0(ContinuumMaterials.YoungModulus(m)) + " · ν " + F2(ContinuumMaterials.Poisson(m)) + " · 입자 " + Engine.Count; Raise(); }
    public void Respawn() { lock (_gate) Respawn(Engine.Material); LastAction = "↺ 블록 재생성 · " + ContinuumMaterials.Names[(int)Engine.Material]; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 가드 ON(CFL 적응 Δt · 1셀 속도 클램프 · SDF 입자 투영 · 해밀토니안 클램프)" : "⛔ 가드 OFF(고정 " + NonPenetrationEnergyConservationPhysicsGuard.OffSubsteps + " 하위 단계 · 격자 충돌만)"; Raise(); }
    public void SetProjection(bool on) { lock (_gate) Guard.ParticleProjection = on; LastAction = on ? "📐 입자 SDF 비침투 투영 ON" : "📐 입자 투영 OFF(격자 충돌만)"; Raise(); }
    public void SetEnergyClamp(bool on) { lock (_gate) Guard.EnergyClamp = on; LastAction = on ? "⚖ 해밀토니안 에너지 클램프 ON" : "⚖ 에너지 클램프 OFF"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) Engine.UseSimd = on; LastAction = on ? "⚡ SIMD(det · 신-후크 τ/ψ · 격자 정규화)" : "🐢 스칼라 쌍둥이(비트 동일)"; Raise(); }
    public void SetFracture(bool on) { lock (_gate) Fracture.Enabled = on; LastAction = on ? "💥 보로노이 파괴 ON" : "💥 파괴 OFF"; Raise(); }
    public void SetCriticalEnergy(float psi) { lock (_gate) Fracture.CriticalEnergy = Math.Clamp(psi, 0.01f, 50f); LastAction = "💥 임계 변형 에너지 ψc → " + F2(Fracture.CriticalEnergy); Raise(); }
    public void SetHammerSpeed(float v) { lock (_gate) Guard.HammerSpeed = Math.Clamp(v, 0.5f, 12f); LastAction = "🔨 원격 해머 속도 → " + F2(Guard.HammerSpeed) + " m/s"; Raise(); }
    public void SetTimeScale(float s) { lock (_gate) TimeScale = Math.Clamp(s, 0.1f, 1f); LastAction = "⏱ 시간 배율 → " + F2(TimeScale) + "×"; Raise(); }
    public void ResetMetrics() { lock (_gate) { Guard.ResetMetrics(); MaxTickMs = 0; } LastAction = "↺ 지표 초기화"; Raise(); }

    private void Respawn(ContinuumMaterial m)
    {
        Engine.Reset(m);
        Fracture.Attach(Engine);
        _pe0 = Engine.PotentialEnergy;
    }

    /// <summary>1 프레임(무할당): MPM 전진 → 파괴/소성 합성 → 에너지 가드 → 시계열.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("ContinuumPhysics.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (AutoLoop && Engine.SimTime >= NonPenetrationEnergyConservationPhysicsGuard.Period) { Respawn(Engine.Material); Loops++; }
            if (float.IsNaN(_pe0)) _pe0 = Engine.PotentialEnergy;
            Engine.Step(TimeScale / Hz);
            Fracture.AfterFrame(Engine);
            Guard.EndFrame(Engine);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _keS[h] = Engine.KineticEnergy; _eeS[h] = Engine.ElasticEnergy; _peS[h] = Engine.PotentialEnergy - _pe0;
            _hS[h] = Engine.KineticEnergy + Engine.ElasticEnergy + Engine.PotentialEnergy - _pe0; _dentS[h] = Fracture.DentMm;
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
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.ContinuumPhysics" };
            _loop.Start();
            LastAction = "▶ 연속체 물리 가동 · 60 Hz × CFL 하위 단계 · APIC P2G/G2P → 보로노이 파괴 · 소성 표면 → 해밀토니안 가드";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("ContinuumPhysics.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ContinuumPhysics.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
