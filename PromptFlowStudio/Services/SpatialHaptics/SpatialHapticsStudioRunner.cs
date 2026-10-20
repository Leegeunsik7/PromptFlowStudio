using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.SpatialHaptics;

/// <summary>
/// [PromptFlow Studio v17.24.0] 🧤 AI 6DoF 공간 햅틱 &amp; 역각 피드백 스튜디오 러너(60 Hz 렌더 프레임 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 프레임 장면(호흡 · 요골 맥동)을 갱신하고, 프레임 시간만큼 1 kHz 햅틱 서브사이클(16–17회)을 돌려
/// 갓-오브젝트 접촉력 → 수동성/버징 가드 → 디바이스 적분 → 진동 촉각 4 kHz 합성을 수행한 뒤 스펙트럼을 분석한다.
/// </summary>
public sealed class SpatialHapticsStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, Hips = RealtimeKinestheticForceFeedbackEngine.Hips;
    public const float Dt = 1f / Hz, DtMs = 1000f / Hz;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _fnS = new float[SeriesLen], _ftS = new float[SeriesLen], _eS = new float[SeriesLen], _bzS = new float[SeriesLen], _vbS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private double _simMs;

    public SpatialHapticsStudioRunner(IServiceProvider sp) : this() { }

    public SpatialHapticsStudioRunner()
    {
        Guard = new DualRatePassivityAntiBuzzingGuard();
        Engine = new RealtimeKinestheticForceFeedbackEngine(Guard);
        Vibro = new VibrotactileMicroTextureSynthesizer();
        LastAction = "📋 대기 · HIP " + Hips + "(손가락 5 + XR 컨트롤러) · 접촉 구 " + RealtimeKinestheticForceFeedbackEngine.Spheres + " · 1 kHz 서브사이클 · 진동 " + VibrotactileMicroTextureSynthesizer.Fs + " Hz 합성";
    }

    public event Action? OnChange;

    public RealtimeKinestheticForceFeedbackEngine Engine { get; }
    public VibrotactileMicroTextureSynthesizer Vibro { get; }
    public DualRatePassivityAntiBuzzingGuard Guard { get; }
    public float Bpm { get; private set; } = 72f;
    public bool Breathing { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double SimMs => _simMs;
    public int LastSubSteps { get; private set; }
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public float PulseValue { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float NormalSeries(int i) => _fnS[Idx(i)];
    public float FrictionSeries(int i) => _ftS[Idx(i)];
    public float EnergySeries(int i) => _eS[Idx(i)];
    public float BuzzSeries(int i) => _bzS[Idx(i)];
    public float VibroSeries(int i) => _vbS[Idx(i)];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetGarment(HapticMaterial m) { lock (_gate) Engine.Garment = m; LastAction = "👕 의상 재질 → " + HapticMaterials.Names[(int)m] + " · μ " + F2(HapticMaterials.Friction(m)) + " · k " + F0(Engine.Stiffness(m)) + " N/m"; Raise(); }
    public void SetStiffness(HapticMaterial m, float k) { lock (_gate) Engine.SetStiffness(m, k); LastAction = "🧱 " + HapticMaterials.Names[(int)m] + " 강성 → " + F0(Engine.Stiffness(m)) + " N/m"; Raise(); }
    public void SetDamping(HapticMaterial m, float c) { lock (_gate) Engine.SetDamping(m, c); LastAction = "💧 " + HapticMaterials.Names[(int)m] + " 점성 → " + F2(Engine.Damping(m)) + " N·s/m"; Raise(); }
    public void SetWetness(float w) { lock (_gate) Engine.Wetness = Math.Clamp(w, 0f, 1f); LastAction = "💦 젖음 " + F0(Engine.Wetness * 100f) + " % · 의상 μ " + F2(Engine.EffectiveFriction(Engine.Garment)); Raise(); }
    public void SetBpm(float bpm) { Bpm = Math.Clamp(bpm, 40f, 180f); LastAction = "❤ 심박 " + F0(Bpm) + " bpm(요골 맥동)"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 수동성 가드 ON(POPC · 슬루 · 포화 · 버징 LPF)" : "⛔ 가드 OFF(지연·계단 에너지 생성 비교)"; Raise(); }
    public void SetFoh(bool on) { lock (_gate) Guard.FirstOrderHold = on; LastAction = on ? "📈 60 Hz 장면 1차 홀드 보간 ON" : "📉 0차 홀드(프레임 계단)"; Raise(); }
    public void SetVibro(bool on) { lock (_gate) Vibro.Enabled = on; LastAction = on ? "〰 진동 촉각 텍스처 ON" : "〰 진동 촉각 OFF"; Raise(); }
    public void SetPulse(bool on) { lock (_gate) Vibro.PulseEnabled = on; LastAction = on ? "❤ 맥동 버스트 ON" : "❤ 맥동 버스트 OFF"; Raise(); }
    public void SetHandMotion(bool on) { lock (_gate) Engine.HandMotion = on; LastAction = on ? "✋ 손/컨트롤러 시나리오 ON(쓸기 · 맥진 · 두드림)" : "✋ 손 고정(가슴 누름)"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) Engine.UseSimd = on; LastAction = on ? "⚡ SIMD 커널(구 SDF · 켈빈-보이트)" : "🐢 스칼라 쌍둥이(비트 동일)"; Raise(); }
    public void SetBreathing(bool on) { Breathing = on; LastAction = on ? "🫁 호흡 흉곽 변위 ON(±8 mm · 0.25 Hz)" : "🫁 호흡 정지"; Raise(); }
    public void SetPopc(bool on) { lock (_gate) Guard.PassivityControl = on; LastAction = on ? "⚖ 수동성 제어기(POPC) ON" : "⚖ POPC OFF(관측만 · 슬루/포화/버징 댐핑 유지)"; Raise(); }
    public void ResetMetrics() { lock (_gate) Guard.Reset(); LastAction = "↺ 가드 지표 초기화"; Raise(); }
    public void SetForceMax(float f) { lock (_gate) Guard.ForceMax = Math.Clamp(f, 1f, 20f); LastAction = "🔩 모터 최대 힘 " + F2(Guard.ForceMax) + " N"; Raise(); }

    /// <summary>1 프레임(무할당): 장면 갱신 → 1 kHz 서브사이클 × N(접촉력 · 가드 · 진동 합성) → 스펙트럼 분석.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("SpatialHaptics.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = _simMs;
            var breath = Breathing ? MathF.Sin(2f * MathF.PI * 0.25f * (float)(now / 1000.0)) : 0f;
            var pulse = RealtimeKinestheticForceFeedbackEngine.Pulse(now, Bpm);
            Engine.UpdateScene(now, breath, pulse);
            var end = now + DtMs;
            var n = 0;
            float fnSum = 0, ftSum = 0;
            while (Engine.SimMs < end)
            {
                Engine.SubStep();
                PulseValue = RealtimeKinestheticForceFeedbackEngine.Pulse(Engine.SimMs, Bpm);
                Vibro.RenderMs(Engine.Contacts, Engine.Wetness, PulseValue);
                for (var i = 0; i < Hips; i++) { ref readonly var c = ref Engine.Contact(i); fnSum += c.NormalForce; ftSum += c.FrictionForce; }
                n++;
            }
            Vibro.Analyze();
            _simMs = end;
            LastSubSteps = n;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            var inv = n > 0 ? 1f / n : 0f;
            _msS[h] = ms; _fnS[h] = fnSum * inv; _ftS[h] = ftSum * inv; _eS[h] = (float)Guard.GeneratedJ * 1000f;
            var bz = 0f;
            for (var i = 0; i < Hips; i++) if (Guard.Buzzing(i)) bz += 1f;
            _bzS[h] = bz; _vbS[h] = Vibro.Rms;
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
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SpatialHaptics" };
            _loop.Start();
            LastAction = "▶ 공간 햅틱 가동 · 60 Hz 장면 × 1 kHz 서브사이클 · 갓-오브젝트 켈빈-보이트 → POPC 가드 → 4 kHz 진동 촉각";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SpatialHaptics.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SpatialHaptics.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
