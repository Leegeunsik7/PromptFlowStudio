using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.GenlockFrustum;
using PromptFlowStudio.Services.SetExtension;
namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>
/// [PromptFlow Studio v17.55.0] 🌌 3D 가우시안 스플래팅 볼류메트릭 세트 스튜디오 러너(60 Hz · 320×192).
/// 프레임 = 카메라(내장 오빗 또는 v17.44 트래킹 포즈 추종) → 조기 컬링/VRAM 가드 → 래딕스 정렬 → EWA 투영 + SH 색 → 타일 블렌딩 → LED 게시(무할당).
/// 장면은 첫 사용 시 생성(지연 · 기본 30 만 스플랫)하며, v17.44 인너 프러스텀 · v17.46 세트 확장 텍스처에 <see cref="IDirectionalSceneSampler"/> 로 결선한다.
/// </summary>
public sealed class GaussianSplatEnvironmentStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, DefaultSplats = 300_000, MaxSplats = 2_000_000;
    public const float Dt = 1f / Hz, Aspect = (float)RealtimeGaussianSplatRasterizerEngine.W / RealtimeGaussianSplatRasterizerEngine.H;
    public static readonly int[] SplatPresets = { 100_000, 300_000, 1_000_000, 2_000_000 };
    public const int PreviewBytes = 54 + RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 3;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _visS = new float[Hist], _vramS = new float[Hist], _shS = new float[Hist];
    private readonly GenlockFrustumLedWallStudioRunner? _led;
    private readonly VirtualSetExtensionStudioRunner? _setExt;
    private GaussianSplatScene? _scene;
    private int _target = DefaultSplats, _sHead, _sN;
    private float _orbit;
    private SplatCamera _cam;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public GaussianSplatEnvironmentStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(GenlockFrustumLedWallStudioRunner)) as GenlockFrustumLedWallStudioRunner,
        sp.GetService(typeof(VirtualSetExtensionStudioRunner)) as VirtualSetExtensionStudioRunner) { }

    public GaussianSplatEnvironmentStudioRunner(GenlockFrustumLedWallStudioRunner? led = null, VirtualSetExtensionStudioRunner? setExt = null)
    {
        _led = led; _setExt = setExt;
        _cam = OrbitCamera(0f);
    }

    public event Action? OnChange;
    public RealtimeGaussianSplatRasterizerEngine Engine { get; } = new();
    public SplatFrustumCullingVramBudgetGuard Guard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new();
    public GaussianSplatScene? Scene => _scene;
    public int SplatTarget => _target;
    public int SplatCount => _scene?.Count ?? 0;
    public float OrbitSpeed { get; private set; } = 0.25f;
    public bool FollowLedPose { get; private set; }
    public bool LinkLed { get; private set; }
    public bool LinkSetExt { get; private set; }
    public bool LedAvailable => _led is not null;
    public bool SetExtAvailable => _setExt is not null;
    public bool ShowTiles { get; set; }
    public ref readonly SplatCamera Camera => ref _cam;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float VisibleSeries(int i) => _visS[Idx(i)];
    public float VramSeries(int i) => _vramS[Idx(i)];
    public float ShMsSeries(int i) => _shS[Idx(i)];

    public static SplatCamera OrbitCamera(float a) =>
        SplatCamera.LookAt(new Vector3(4.6f * MathF.Sin(a), 1.7f, 4.6f * MathF.Cos(a)), GaussianSplatScene.SphereCenter, 45f, Aspect, 0.1f, 120f);

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureScene()
    {
        if (_scene is not null && _scene.Count == _target) return;
        if (_scene is null || _scene.Capacity < _target) _scene = new GaussianSplatScene(_target);
        _scene.Generate(_target);
    }

    public void SetSplatCount(int n)
    {
        lock (_gate) { _target = Math.Clamp(n, 1_000, MaxSplats); _scene = null; EnsureScene(); Guard.Reset(); MaxTickMs = 0; }
        LastAction = "🌌 장면 재생성 · " + _target.ToString("N0", CultureInfo.InvariantCulture) + " 스플랫(광택 " + _scene!.Glossy.ToString("N0", CultureInfo.InvariantCulture) + ")";
        Raise();
    }

    public void SetShDegree(int d) { lock (_gate) Sh.Degree = Math.Clamp(d, 0, 3); LastAction = "🌈 SH 차수 " + Sh.Degree.ToString(CultureInfo.InvariantCulture); Raise(); }
    public void SetSpecular(float g) { lock (_gate) Sh.SpecularGain = Math.Clamp(g, 0f, 3f); Raise(); }
    public void SetOrbitSpeed(float rad) { lock (_gate) OrbitSpeed = Math.Clamp(rad, -1.5f, 1.5f); Raise(); }
    public void SetGuard(bool on) { lock (_gate) { Guard.Enabled = on; if (!on) Guard.Reset(); } LastAction = on ? "🛡 VRAM 예산 · LOD 가드 ON" : "⛔ VRAM 가드 OFF(초과 비교)"; Raise(); }
    public void SetBudgetGb(float gb) { lock (_gate) Guard.BudgetBytes = (long)(Math.Clamp(gb, 0.25f, 48f) * (1L << 30)); Raise(); }
    public void SetReplication(float x) { lock (_gate) Guard.Replication = Math.Clamp(x, 1f, 400f); Raise(); }
    public void SetFollowLed(bool on) { lock (_gate) FollowLedPose = on && _led is not null; LastAction = FollowLedPose ? "🎥 v17.44 트래킹 포즈 추종" : "🎥 내장 오빗 카메라"; Raise(); }

    public void SetLinkLed(bool on)
    {
        lock (_gate) { LinkLed = on && _led is not null; if (_led is not null) _led.Synth.InnerScene = LinkLed ? Sh : null; }
        LastAction = LinkLed ? "🖥 v17.44 인너 프러스텀 = 3DGS 뷰" : "🖥 인너 프러스텀 결선 해제";
        Raise();
    }

    public void SetLinkSetExt(bool on)
    {
        lock (_gate) { LinkSetExt = on && _setExt is not null; if (_setExt is not null) _setExt.Engine.ExtensionScene = LinkSetExt ? Sh : null; }
        LastAction = LinkSetExt ? "🏛 v17.46 세트 확장 텍스처 = 3DGS 뷰" : "🏛 세트 확장 결선 해제";
        Raise();
    }

    // ══════════════════════ 프레임 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureScene();
            using var zg = ZeroGcRegionScope.Enter("SplatEnv.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _orbit += OrbitSpeed * Dt;
            if (_orbit > MathF.Tau) _orbit -= MathF.Tau; else if (_orbit < 0f) _orbit += MathF.Tau;
            if (FollowLedPose && _led is not null)
            {
                var p = _led.Pose;
                _cam = new SplatCamera
                {
                    Position = p.Position, Forward = p.Forward, Right = p.Right, Up = p.Up,
                    VFovDeg = Math.Clamp(p.VFovDeg, 5f, 120f), Aspect = MathF.Tan(p.HFovDeg * (MathF.PI / 360f)) / MathF.Max(MathF.Tan(p.VFovDeg * (MathF.PI / 360f)), 1e-3f), Near = 0.1f, Far = 120f,
                };
            }
            else _cam = OrbitCamera(_orbit);
            Engine.Render(_scene!, in _cam, Guard, Sh);
            Sh.Publish(Engine, in _cam);
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
            _msS[_sHead] = (float)ms; _visS[_sHead] = Engine.Visible; _vramS[_sHead] = Guard.VramPct; _shS[_sHead] = (float)(Engine.LastShUs / 1000.0);
            _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
        }
    }

    public void WritePreviewBmp(byte[] dst) { lock (_gate) Engine.WriteBmp(dst, ShowTiles); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SplatEnv" };
            _loop.Start();
        }
        LastAction = "▶ 3DGS 볼류메트릭 세트 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SplatEnv.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SplatEnv.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_led is not null && ReferenceEquals(_led.Synth.InnerScene, Sh)) _led.Synth.InnerScene = null;
            if (_setExt is not null && ReferenceEquals(_setExt.Engine.ExtensionScene, Sh)) _setExt.Engine.ExtensionScene = null;
            Engine.Dispose();
        }
    }
}
