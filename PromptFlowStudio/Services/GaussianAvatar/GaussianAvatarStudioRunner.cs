using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.FluidWetness;
using PromptFlowStudio.Services.SemanticMatting;

namespace PromptFlowStudio.Services.GaussianAvatar;

/// <summary>
/// [PromptFlow Studio v17.20.0] 🔮 AI 4D 가우시안 아바타 스튜디오 러너(60 Hz 전용 스레드 · 결정적).
/// v17.19 습윤 스택(v17.14 근육 · v17.16 의상 · v17.15 헤어 · v17.17 SSS · 땀/비) 을 짝수 틱(30 Hz)으로 진행하고,
/// 매 틱 앵커 DQ → 가우시안 DQS(<see cref="DeformableGaussianSkinningBindingEngine"/>) → 젖음 샘플 · v17.08 HDRI 링버퍼 → SH 차수 3 재조명
/// → 16 비트 래딕스 정렬 · 컬링 → 남은 프레임 예산 안에서 타일 스플래팅(0.999 조기 종료) 순으로 처리한다.
/// </summary>
public sealed class GaussianAvatarStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float Dt = 1f / Hz, MinRasterBudgetMs = 6f;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _sortS = new float[SeriesLen], _skinS = new float[SeriesLen], _shS = new float[SeriesLen], _rasterS = new float[SeriesLen], _visS = new float[SeriesLen];
    private readonly byte[] _bmp = new byte[54 + RadixSortDepthCullingTileRasterizerGuard.W * RadixSortDepthCullingTileRasterizerGuard.H * 3];
    private readonly Vector3 _target;
    private readonly float _baseDistance;
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public GaussianAvatarStudioRunner(IServiceProvider sp) : this() { }

    public GaussianAvatarStudioRunner() : this(DeformableGaussianSkinningBindingEngine.DefaultCount) { }

    public GaussianAvatarStudioRunner(int gaussians)
    {
        Base = new FluidWetnessStudioRunner();
        Base.SetScenario(true);
        Env = new HdriEnvironment();
        var g = Base.Garment;
        Engine = new DeformableGaussianSkinningBindingEngine(g.Skin, g.Engine, g.Hair, gaussians);
        Synth = new SphericalHarmonicsNeuralRelightingSynthesizer(Engine, Base.Skin, g.Engine.LayerOf);
        Guard = new RadixSortDepthCullingTileRasterizerGuard(Engine.N);
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (var i = 0; i < Engine.N; i += 7) { var p = Engine.RestCenter(i); lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        _target = (lo + hi) * 0.5f;
        var half = MathF.Max(0.3f, 0.5f * MathF.Max(hi.Y - lo.Y, hi.X - lo.X));
        _baseDistance = half / MathF.Tan(Guard.FovYDeg * MathF.PI / 360f) * 1.12f;
        Synth.PushEnvironment(Env);
        LastAction = "📋 대기 · 가우시안 " + Engine.N + "(근육 " + Engine.Count(GaussianSurface.Muscle) + " · 의상 " + Engine.Count(GaussianSurface.Garment) + " · 헤어 " + Engine.Count(GaussianSurface.Hair) + ") · 앵커 " + Engine.A + " · " + Engine.Path;
    }

    public event Action? OnChange;

    public FluidWetnessStudioRunner Base { get; }
    public HdriEnvironment Env { get; }
    public DeformableGaussianSkinningBindingEngine Engine { get; }
    public SphericalHarmonicsNeuralRelightingSynthesizer Synth { get; }
    public RadixSortDepthCullingTileRasterizerGuard Guard { get; }
    public float CameraYawDeg { get; set; } = 20f;
    public float CameraPitchDeg { get; set; } = 8f;
    public float Zoom { get; set; } = 1f;
    public bool AutoOrbit { get; set; } = true;
    public bool BaseSim { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double LastBaseMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public float BudgetMs { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float SortUsSeries(int i) => _sortS[Idx(i)];
    public float SkinUsSeries(int i) => _skinS[Idx(i)];
    public float ShUsSeries(int i) => _shS[Idx(i)];
    public float RasterUsSeries(int i) => _rasterS[Idx(i)];
    public float VisibleSeries(int i) => _visS[Idx(i)];

    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetView(SplatView v) { lock (_gate) Guard.View = v; LastAction = v == SplatView.Point ? "• 포인트 뷰(중심 1 px · 밀도 확인)" : v == SplatView.Density ? "🔥 스플랫 중첩 밀도 히트맵" : "⬭ 타원체 EWA 스플랫 뷰"; Raise(); }
    public void SetSpecular(float g) { lock (_gate) Synth.SpecularGain = Math.Clamp(g, 0f, 4f); LastAction = "✨ SH 반사광 강도 × " + F2(Synth.SpecularGain); Raise(); }
    public void SetDiffuse(float g) { lock (_gate) Synth.DiffuseGain = Math.Clamp(g, 0f, 3f); LastAction = "💡 확산 조도 × " + F2(Synth.DiffuseGain); Raise(); }
    public void SetSss(bool on) { lock (_gate) Synth.SssEnabled = on; LastAction = on ? "🩸 v17.17 SSS 역광 투과 ON" : "🩸 SSS 투과 OFF"; Raise(); }
    public void SetWet(bool on) { lock (_gate) Synth.WetEnabled = on; LastAction = on ? "💧 v17.19 젖음 암전 · 광택 ON" : "💧 젖음 합성 OFF"; Raise(); }
    public void SetRelight(bool on) { lock (_gate) Synth.Relight = on; LastAction = on ? "🌐 HDRI SH9 재조명 ON" : "🌐 재조명 OFF(고정 키 라이트)"; Raise(); }
    public void SetEarlyTermination(bool on) { lock (_gate) Guard.EarlyTermination = on; LastAction = on ? "⛔ 알파 0.999 조기 종료 ON" : "⛔ 조기 종료 OFF(전량 스플랫)"; Raise(); }
    public void SetHdri(HdriPreset p) { lock (_gate) Env.SetPreset(p); LastAction = "🌅 HDRI " + p + " · 링버퍼 " + SphericalHarmonicsNeuralRelightingSynthesizer.Ring + " 프레임 평활"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) { var p = on ? GaussianSimdPath.V256 : GaussianSimdPath.Scalar; Engine.Path = p; Synth.Path = p; } LastAction = on ? "⚡ Vector256 SoA 경로" : "🐢 스칼라 경로(비트 동일 비교)"; Raise(); }
    public void SetYaw(float deg) { lock (_gate) { AutoOrbit = false; CameraYawDeg = deg % 360f; } LastAction = "🎥 궤도 " + F2(CameraYawDeg) + "°(수동)"; Raise(); }
    public void SetZoom(float z) { lock (_gate) Zoom = Math.Clamp(z, 0.4f, 3f); LastAction = "🔍 줌 × " + F2(Zoom); Raise(); }
    public void SetOrbit(bool on) { lock (_gate) AutoOrbit = on; LastAction = on ? "🎥 자동 궤도 ON" : "🎥 궤도 고정"; Raise(); }
    public void SetBaseSim(bool on) { lock (_gate) BaseSim = on; LastAction = on ? "🧬 근육 · 의상 · 헤어 · 습윤 동역학 ON" : "🧬 동역학 정지(휴지 포즈)"; Raise(); }

    public Vector3 CameraPosition()
    {
        float yaw = CameraYawDeg * MathF.PI / 180f, pitch = CameraPitchDeg * MathF.PI / 180f;
        var d = _baseDistance / Zoom;
        return _target + new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)) * d;
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("GaussianAvatar.Step");
            var freq = System.Diagnostics.Stopwatch.Frequency;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _time += Dt;
            // 짝수 틱 = 동역학(30 Hz) → 앵커가 바뀐 틱에만 DQS · 젖음 샘플 / 홀수 틱 = SH 재조명 — 무거운 단계를 교대 분산
            var even = (_tick & 1) == 0;
            if (even)
            {
                if (BaseSim) Base.Step();
                LastBaseMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq;
                if (BaseSim || _tick == 0) Engine.Update();
                Synth.SampleSurface(Base.Wet, Base.Skin, Base.Guard.HairWetMean);
            }
            if (Synth.EnvironmentVersion != Env.Version || Synth.RingCount < SphericalHarmonicsNeuralRelightingSynthesizer.Ring) Synth.PushEnvironment(Env);
            if (AutoOrbit) CameraYawDeg = (CameraYawDeg + 12f * Dt) % 360f;
            var cam = CameraPosition();
            Guard.SetCamera(cam, _target);
            if (!even || _tick == 0) Synth.Update(cam);
            Guard.CullAndSort(Engine, Synth);
            var used = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq);
            BudgetMs = Math.Clamp(RadixSortDepthCullingTileRasterizerGuard.FrameBudgetMs - used, MinRasterBudgetMs, RadixSortDepthCullingTileRasterizerGuard.FrameBudgetMs);
            Guard.Rasterize(Engine, Synth, BudgetMs);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq);
            var h = _seriesHead;
            _msS[h] = ms; _sortS[h] = (float)Guard.LastSortUs; _skinS[h] = (float)Engine.LastUpdateUs; _shS[h] = (float)(Synth.LastShUs + Synth.LastShadeUs); _rasterS[h] = (float)Guard.LastRasterUs; _visS[h] = Guard.Visible;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>스플랫 뷰포트 BMP(<see cref="RadixSortDepthCullingTileRasterizerGuard.View"/>).</summary>
    public string RenderBase64()
    {
        lock (_gate)
        {
            Guard.WriteBmp(_bmp);
            return Convert.ToBase64String(_bmp);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.GaussianAvatar" };
            _loop.Start();
            LastAction = "▶ 4D 가우시안 아바타 가동 · 60 Hz · DQS 스키닝 → SH3 재조명 → 16 비트 래딕스 정렬 → 타일 스플래팅";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("GaussianAvatar.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("GaussianAvatar.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Base.Dispose();
    }
}
