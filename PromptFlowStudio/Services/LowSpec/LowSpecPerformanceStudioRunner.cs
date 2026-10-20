using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.LowSpec;

/// <summary>
/// [PromptFlow Studio v16.65.0] ⚡ 저사양 초고성능 최적화 스튜디오 러너 — 하드웨어 프리셋(실측/듀얼코어 4GB iGPU/쿼드코어 8GB iGPU/게이밍 dGPU).
/// 틱: 프레임 비용 모델(등급 기본 비용 × LOD 비용 × 해상도² × 정밀도/경로 계수 × 부스터) → 프로파일러 관측(LOD/해상도) → 30틱마다 양자화 재라우팅 ·
/// 라우팅 정밀도로 실 양자화 커널 실행 · 압착 가드 프레임 파이프라인. 1-Click 부스터: INT4 하한 허용 + GC 억제 세션 + 부스트 계수.
/// 60Hz PeriodicTimer · <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class LowSpecPerformanceStudioRunner : IDisposable
{
    public const int TickHz = 60, HistoryLength = 120;

    private readonly object _gate = new();
    private readonly HardwareAdaptiveLowSpecProfiler _profiler;
    private readonly DynamicLowBitQuantizationFallbackRouter _router;
    private readonly ZeroAllocMemorySqueezerGuard _squeezer;
    private readonly PromptFlowStudio.Services.Broadcast.MultiGpuClusterEngine? _gpus;
    private readonly float[] _frameHist = new float[HistoryLength];
    private readonly float[] _kernelOut = new float[DynamicLowBitQuantizationFallbackRouter.Rows];
    private CancellationTokenSource? _cts;
    private uint _rng = 0x2545F491;
    private int _histHead, _preset;
    private long _frameSeq;
    private float _lastFrameMs, _vramBudgetMb = 1024;
    private bool _booster;

    public LowSpecPerformanceStudioRunner(IServiceProvider sp)
        : this((HardwareAdaptiveLowSpecProfiler?)sp.GetService(typeof(HardwareAdaptiveLowSpecProfiler)) ?? new HardwareAdaptiveLowSpecProfiler(),
               (DynamicLowBitQuantizationFallbackRouter?)sp.GetService(typeof(DynamicLowBitQuantizationFallbackRouter)) ?? new DynamicLowBitQuantizationFallbackRouter(),
               (ZeroAllocMemorySqueezerGuard?)sp.GetService(typeof(ZeroAllocMemorySqueezerGuard)) ?? new ZeroAllocMemorySqueezerGuard(),
               sp.GetService(typeof(PromptFlowStudio.Services.Broadcast.MultiGpuClusterEngine)) as PromptFlowStudio.Services.Broadcast.MultiGpuClusterEngine) { }

    public LowSpecPerformanceStudioRunner(HardwareAdaptiveLowSpecProfiler profiler, DynamicLowBitQuantizationFallbackRouter router, ZeroAllocMemorySqueezerGuard squeezer,
        PromptFlowStudio.Services.Broadcast.MultiGpuClusterEngine? gpus = null)
    {
        _profiler = profiler; _router = router; _squeezer = squeezer; _gpus = gpus;
        var hw = _profiler.Hardware;
        _vramBudgetMb = hw.HasDiscreteGpu ? hw.VramMb : Math.Max(256, hw.VramMb);
        Reroute();
    }

    public event Action? OnChange;

    public HardwareAdaptiveLowSpecProfiler Profiler => _profiler;
    public DynamicLowBitQuantizationFallbackRouter Router => _router;
    public ZeroAllocMemorySqueezerGuard Squeezer => _squeezer;
    public bool IsRunning => _cts is not null;
    public bool Booster { get { lock (_gate) return _booster; } }
    public int Preset { get { lock (_gate) return _preset; } }
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public float LastFrameMs { get { lock (_gate) return _lastFrameMs; } }
    public float Fps { get { var f = LastFrameMs; return f > 0 ? MathF.Min(1000f / f, 999f) : 0f; } }
    public string? LastAction { get; private set; }

    public float VramBudgetMb
    {
        get { lock (_gate) return _vramBudgetMb; }
        set { lock (_gate) _vramBudgetMb = Math.Clamp(value, 128f, 16384f); Reroute(); }
    }

    public static readonly string[] PresetNames = { "현재 PC(실측)", "듀얼코어 4GB iGPU", "쿼드코어 8GB iGPU", "게이밍 dGPU 8GB" };

    /// <summary>하드웨어 프리셋 적용(0=실측).</summary>
    public void ApplyPreset(int preset)
    {
        var hw = preset switch
        {
            1 => HardwareSnapshot.DualCoreIgpu4Gb,
            2 => HardwareSnapshot.QuadCoreIgpu8Gb,
            3 => HardwareSnapshot.GamingDgpu,
            _ => HardwareAdaptiveLowSpecProfiler.Probe(),
        };
        lock (_gate)
        {
            _preset = Math.Clamp(preset, 0, PresetNames.Length - 1);
            _vramBudgetMb = hw.HasDiscreteGpu ? hw.VramMb : Math.Max(256, hw.VramMb);
            Array.Clear(_frameHist);
        }
        _profiler.ApplyHardware(hw);
        Reroute();
        LastAction = "🖥 " + PresetNames[_preset] + " · " + HardwareAdaptiveLowSpecProfiler.TierLabel(hw.Tier);
        Raise();
    }

    /// <summary>실측 GPU 탐지(nvidia-smi 결선 · 실패/미탑재 시 iGPU 유지).</summary>
    public async Task DetectGpuAsync(CancellationToken ct = default)
    {
        long vram = 0;
        if (_gpus is not null)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromSeconds(5));
                foreach (var g in await _gpus.DetectGpusAsync(d.Token).ConfigureAwait(false)) vram = Math.Max(vram, g.VramMB);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            { PromptFlowStudio.Program.Log("LowSpec.DetectGpu", ex); }
        }
        _profiler.UpdateGpu(vram);
        lock (_gate) if (vram > 0) _vramBudgetMb = vram;
        Reroute();
        LastAction = vram > 0 ? "🎮 외장 GPU VRAM " + vram.ToString(System.Globalization.CultureInfo.InvariantCulture) + "MB" : "🧩 외장 GPU 미탐지 — iGPU/CPU 폴백";
        Raise();
    }

    /// <summary>1-Click 부스터: INT4 하한 · GC 억제 세션 · 압착.</summary>
    public void SetBooster(bool on)
    {
        lock (_gate) _booster = on;
        _router.Floor = on ? QuantPrecision.Int4 : QuantPrecision.Int8;
        if (on) { _squeezer.BeginSession(); _squeezer.Squeeze(); }
        else _squeezer.EndSession();
        Reroute();
        LastAction = on ? "🚀 초저사양 부스터 ON — INT4 허용 · GC 억제 · 압착" : "⏹ 부스터 OFF";
        Raise();
    }

    private void Reroute()
    {
        var hw = _profiler.Hardware;
        float budget;
        lock (_gate) budget = _vramBudgetMb;
        var k = HardwareAdaptiveLowSpecProfiler.LodCost(_profiler.Lod);
        _router.Route(LowSpecWorkload.World4D, 900 * k, hw, budget);
        _router.Route(LowSpecWorkload.Gaussian3D, 600 * k, hw, budget);
        _router.Route(LowSpecWorkload.Neural, 1300 * k, hw, budget);
    }

    /// <summary>등급 기본 프레임 비용(ms · LOD0 · 해상도 1).</summary>
    public static float BaseCostMs(in HardwareSnapshot hw) => 6f + 44f * (1f - hw.Score);

    public static float PrecisionFactor(in QuantRoute r)
    {
        var p = r.Precision switch { QuantPrecision.Fp32 => 1.4f, QuantPrecision.Fp16 => 1f, QuantPrecision.Int8 => 0.7f, _ => 0.55f };
        var path = r.Path switch { ComputePath.DiscreteGpu => 0.5f, ComputePath.IntegratedGpu => 1f, ComputePath.CpuAvx2 => 1.25f, ComputePath.CpuNeon => 1.35f, _ => 2.2f };
        return p * path;
    }

    /// <summary>모델 프레임 시간(지터 제외).</summary>
    public float ModelFrameMs()
    {
        var hw = _profiler.Hardware;
        var s = _profiler.RenderScale;
        var route = _router.RouteOf(LowSpecWorkload.World4D);
        return BaseCostMs(hw) * HardwareAdaptiveLowSpecProfiler.LodCost(_profiler.Lod) * s * s * PrecisionFactor(route) * (Booster ? 0.85f : 1f);
    }

    /// <summary>1틱(무할당).</summary>
    public LowSpecDecision TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("LowSpec.Runner.Tick");
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            var jitter = 0.92f + (_rng & 0xFFFF) / 65535f * 0.16f;
            var route = _router.RouteOf(LowSpecWorkload.World4D);
            var ms = ModelFrameMs() * jitter;
            _router.RunKernel(route.Precision, _kernelOut);
            _squeezer.Tick((int)(4096 * _profiler.RenderScale));
            var d = _profiler.Observe(ms);
            _lastFrameMs = ms;
            _frameHist[_histHead] = ms; _histHead = (_histHead + 1) % HistoryLength;
            if (d.Adjust != LowSpecAdjust.Hold || (_frameSeq % 30) == 0) Reroute();
            Interlocked.Increment(ref _frameSeq);
            return d;
        }
    }

    public int CopyFrameHistory(Span<float> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, HistoryLength);
            for (var i = 0; i < n; i++) dst[i] = _frameHist[(_histHead + HistoryLength - n + i) % HistoryLength];
            return n;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 저사양 레귤레이터 라이브";
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
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("LowSpec.Tick", ex); }
                if ((FrameSeq & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LowSpec.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        _squeezer.EndSession();
    }
}
