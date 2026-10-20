using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Neural3d;

/// <summary>
/// [PromptFlow Studio v16.58.0] 🧊 3DGS &amp; NeRF 라이브 스튜디오 러너 — 동적 NeRF(시간 t) → 3DGS 변환 → 스트림 인코딩(MTU+FEC) → 수신 디코드 →
/// HDR 리라이트 → 오빗 카메라 랜드마크 관측 → 서브밀리 포즈 추적 → 추적 포즈로 라스터라이즈(프런트 버퍼). 30Hz PeriodicTimer · <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class GaussianSplattingStudioRunner : IDisposable
{
    public const int FrameWidth = 192, FrameHeight = 108, TickHz = 30;
    private static readonly Vector3 Pivot = new(0f, 0.3f, 0f);

    private readonly object _gate = new();
    private readonly AiGaussianSplattingRasterizerEngine _ras;
    private readonly DynamicNeRfToSplattingConverter _conv;
    private readonly NeuralCameraPoseRelightingEngine _pose;
    private readonly GaussianScene _work = new(), _rx = new(), _lit = new();
    private readonly SplatStreamCodec _rxCodec = new();
    private readonly Vector3[] _lm = new Vector3[160];
    private readonly Vector2[] _obs = new Vector2[160];
    private readonly byte[] _back = new byte[FrameWidth * FrameHeight * 3], _front = new byte[FrameWidth * FrameHeight * 3];
    private readonly float[] _depth = new float[FrameWidth * FrameHeight];
    private IRadianceField _field = AnalyticRadianceField.Demo();
    private CancellationTokenSource? _cts;
    private uint _rng = 0x9E3779B9u;
    private double _t, _az;
    private long _frameSeq, _micros;
    private int _res = 36;
    private double _trackErrMm;
    private bool _poseInit;

    public GaussianSplattingStudioRunner(IServiceProvider sp)
        : this((AiGaussianSplattingRasterizerEngine?)sp.GetService(typeof(AiGaussianSplattingRasterizerEngine)) ?? new AiGaussianSplattingRasterizerEngine(),
               (DynamicNeRfToSplattingConverter?)sp.GetService(typeof(DynamicNeRfToSplattingConverter)) ?? new DynamicNeRfToSplattingConverter(),
               (NeuralCameraPoseRelightingEngine?)sp.GetService(typeof(NeuralCameraPoseRelightingEngine)) ?? new NeuralCameraPoseRelightingEngine()) { }

    public GaussianSplattingStudioRunner(AiGaussianSplattingRasterizerEngine ras, DynamicNeRfToSplattingConverter conv, NeuralCameraPoseRelightingEngine pose)
    {
        _ras = ras; _conv = conv; _pose = pose;
        _rxCodec.Reset(_res * _res * _res);
    }

    public event Action? OnChange;

    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public GaussianScene Scene => _lit;
    public double TimeSec => _t;
    public double TrackErrorMm => Volatile.Read(ref _trackErrMm);
    public float Density { get; set; } = 1f;
    public float NearClip { get; set; } = 0.05f;
    public float FarClip { get; set; } = 20f;
    public float ScaleMultiplier { get; set; } = 1f;
    public float OrbitSpeedDeg { get; set; } = 12f;
    public float ElevationDeg { get; set; } = 22f;
    public float Distance { get; set; } = 2.6f;
    public bool Animate { get; set; } = true;
    public bool Relighting { get; set; } = true;
    public float SensorNoisePx { get; set; } = 0.15f;
    public string? LastAction { get; private set; }
    /// <summary>최근 프레임 MTU 패킷 수(0 = 키 프레임이 패킷 상한 초과 — 해상도 하향 필요).</summary>
    public int LastPackets { get; private set; }

    /// <summary>NeRF 변환 해상도(16..64) — 변경 시 수신 코덱 재설정.</summary>
    public int Resolution
    {
        get => _res;
        set { lock (_gate) { _res = Math.Clamp(value, 16, 64); _rxCodec.Reset(_res * _res * _res); _conv.Codec.Reset(_res * _res * _res); } }
    }

    /// <summary>외부 NeRF(학습 필드) 결선점.</summary>
    public void SetField(IRadianceField field) { lock (_gate) _field = field; LastAction = "🔌 외부 방사 필드 결선"; Raise(); }

    /// <summary>정적 장면(PLY/다시점 복원) 로드 — 스트림 대신 직접 표시.</summary>
    public GaussianScene? StaticScene { get; set; }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f - 0.5f; }

    public SplatCamera TrueCamera(int w = 1280, int h = 720) => SplatCamera.Orbit(Pivot, (float)_az, ElevationDeg, Distance, 55f, w, h);

    /// <summary>1틱(무할당 · 해상도/용량 변경 시만 확장).</summary>
    public void TickOnce(double dt)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Gs.Runner.Tick");
            if (Animate) { _t += dt; _az += OrbitSpeedDeg * dt; }
            _micros += (long)(dt * 1e6);
            GaussianScene src;
            if (StaticScene is { } st) src = st;
            else
            {
                LastPackets = _conv.StreamFrame(_field, _work, new ConvertOptions(Resolution: _res, Time: (float)_t), 1, (uint)_micros);
                if (!_rxCodec.Decode(_conv.LastEncoded, _rx)) { _conv.Codec.Reset(_res * _res * _res); _rxCodec.Reset(_res * _res * _res); }
                src = _rx;
            }
            var truth = TrueCamera();
            if (!_poseInit) { _pose.ResetPose(truth); _poseInit = true; }
            var n = NeuralCameraPoseRelightingEngine.SelectLandmarks(src, _lm);
            for (var i = 0; i < n; i++) _obs[i] = truth.Project(truth.ToCamera(_lm[i])) + new Vector2(Rand() * 2f * SensorNoisePx, Rand() * 2f * SensorNoisePx);
            if (n >= 4)
            {
                _pose.Track(_lm.AsSpan(0, n), _obs.AsSpan(0, n), truth, _micros);
                Volatile.Write(ref _trackErrMm, Vector3.Distance(_pose.Position, truth.Position) * 1000.0);
            }
            var cam = n >= 4 ? _pose.CurrentCamera(SplatCamera.Orbit(Pivot, 0, 0, 1, 55f, FrameWidth, FrameHeight)) : TrueCamera(FrameWidth, FrameHeight);
            GaussianScene draw = src;
            if (Relighting) { _pose.Relight(src, _lit, cam.Position); draw = _lit; }
            else src.CopyTo(_lit);
            _ras.Render(draw, cam, new SplatRenderOptions(Near: NearClip, Far: FarClip, Density: Density, ScaleMultiplier: ScaleMultiplier, Background: new Vector3(0.04f, 0.05f, 0.08f)), _back, _depth);
            _back.AsSpan().CopyTo(_front);
            Interlocked.Increment(ref _frameSeq);
        }
    }

    public void CopyFrame(Span<byte> dst) { lock (_gate) _front.AsSpan().CopyTo(dst); }

    /// <summary>현재 표시 장면 스냅샷(UI 내보내기용 · 락 복사).</summary>
    public void SnapshotScene(GaussianScene dst) { lock (_gate) _lit.CopyTo(dst); }

    public void SetLight(in LightRig rig) { lock (_gate) _pose.SetLight(rig); Raise(); }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 3DGS 라이브";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(0.2, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                TickOnce(dt);
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Gs.Runner.Loop", ex); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Gs.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
