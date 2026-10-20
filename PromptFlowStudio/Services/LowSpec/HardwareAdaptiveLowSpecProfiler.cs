using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PromptFlowStudio.Services.LowSpec;

/// <summary>하드웨어 등급(점수 기반) — 낮을수록 공격적 LOD/정밀도 다운스케일.</summary>
public enum HardwareTier : byte { UltraLow, Low, Mid, High }

/// <summary>하드웨어 스냅샷 — 코어 · RAM(총/여유) · VRAM · GPU 종류 · SIMD.</summary>
public readonly record struct HardwareSnapshot(int LogicalCores, long TotalRamMb, long AvailableRamMb, long VramMb, bool HasDiscreteGpu, bool HasIntegratedGpu, bool Avx2, bool AdvSimd)
{
    /// <summary>0..1 하드웨어 점수(코어 30% · RAM 25% · VRAM 30% · SIMD 15%).</summary>
    public float Score
    {
        get
        {
            var c = Math.Clamp((LogicalCores - 2) / 14f, 0f, 1f);
            var r = Math.Clamp((TotalRamMb - 4096) / (32768f - 4096f), 0f, 1f);
            var v = HasDiscreteGpu ? Math.Clamp(VramMb / 8192f, 0.25f, 1f) : HasIntegratedGpu ? 0.1f : 0f;
            var s = Avx2 || AdvSimd ? 1f : Vector128.IsHardwareAccelerated ? 0.5f : 0f;
            return 0.30f * c + 0.25f * r + 0.30f * v + 0.15f * s;
        }
    }

    public HardwareTier Tier => Score switch { < 0.18f => HardwareTier.UltraLow, < 0.40f => HardwareTier.Low, < 0.68f => HardwareTier.Mid, _ => HardwareTier.High };

    /// <summary>데모 프리셋 — 듀얼코어 4GB iGPU.</summary>
    public static HardwareSnapshot DualCoreIgpu4Gb => new(2, 4096, 1100, 512, false, true, false, false);
    /// <summary>데모 프리셋 — 쿼드코어 8GB iGPU(AVX2).</summary>
    public static HardwareSnapshot QuadCoreIgpu8Gb => new(4, 8192, 3500, 1024, false, true, true, false);
    /// <summary>데모 프리셋 — 게이밍 dGPU 8GB.</summary>
    public static HardwareSnapshot GamingDgpu => new(16, 32768, 20000, 8192, true, true, true, false);
}

/// <summary>틱 결정 — LOD(0=최고) · 연산 해상도 배율 · 목표 프레임 예산 · 조정 사유.</summary>
public readonly record struct LowSpecDecision(int Lod, float RenderScale, float BudgetMs, float SmoothedFrameMs, LowSpecAdjust Adjust);

public enum LowSpecAdjust : byte { Hold, StepDown, StepUp, MemoryPressure, Emergency }

/// <summary>
/// [PromptFlow Studio v16.65.0] ⚡ 적응형 저사양 하드웨어 프로파일러 — BCL 실측(코어 · GC 가용 메모리 · 메모리 부하 · SIMD) + GPU 정보 주입(nvidia-smi/DXGI 결선점).
/// <see cref="Observe"/>: 프레임 시간 EMA vs 목표 예산 → 히스테리시스(하향 3프레임 · 상향 45프레임) LOD/연산 해상도 자율 스케일링.
/// 메모리 압박(여유 RAM &lt; 12%) 시 즉시 하향 · 예산 2배 초과 시 비상 2단 하향. 무할당.
/// </summary>
public sealed class HardwareAdaptiveLowSpecProfiler
{
    public const int MaxLod = 4, DownFrames = 3, UpFrames = 45;
    public const float MinScale = 0.5f, MaxScale = 1f, ScaleStep = 0.05f;

    private readonly object _gate = new();
    private HardwareSnapshot _hw;
    private float _ema = float.NaN, _scale = 1f;
    private int _lod, _over, _under, _targetFps = 60;
    private long _observed, _stepDowns, _stepUps, _pressureEvents;
    private LowSpecDecision _last;

    public HardwareAdaptiveLowSpecProfiler() : this(Probe()) { }

    public HardwareAdaptiveLowSpecProfiler(HardwareSnapshot hw) => ApplyHardware(hw);

    public event Action? OnChange;

    public HardwareSnapshot Hardware { get { lock (_gate) return _hw; } }
    public int Lod => Volatile.Read(ref _lod);
    public float RenderScale { get { lock (_gate) return _scale; } }
    public long Observed => Interlocked.Read(ref _observed);
    public long StepDowns => Interlocked.Read(ref _stepDowns);
    public long StepUps => Interlocked.Read(ref _stepUps);
    public long PressureEvents => Interlocked.Read(ref _pressureEvents);
    public LowSpecDecision Last { get { lock (_gate) return _last; } }
    public string? LastAction { get; private set; }

    public int TargetFps
    {
        get => Volatile.Read(ref _targetFps);
        set => Volatile.Write(ref _targetFps, Math.Clamp(value, 24, 240));
    }

    public float BudgetMs => 1000f / TargetFps;

    /// <summary>BCL 실측 — GPU는 미상(공유 메모리 iGPU 가정 · <see cref="UpdateGpu"/>로 주입).</summary>
    public static HardwareSnapshot Probe()
    {
        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes / (1024 * 1024) : 4096;
        var avail = Math.Max(0, total - info.MemoryLoadBytes / (1024 * 1024));
        return new HardwareSnapshot(Environment.ProcessorCount, total, avail, Math.Min(1024, total / 4), false, true,
            Avx2.IsSupported, AdvSimd.IsSupported);
    }

    /// <summary>하드웨어 교체(프리셋/실측) — 등급별 초기 LOD/해상도로 리셋.</summary>
    public void ApplyHardware(HardwareSnapshot hw)
    {
        lock (_gate)
        {
            _hw = hw;
            (_lod, _scale) = hw.Tier switch
            {
                HardwareTier.UltraLow => (3, 0.6f),
                HardwareTier.Low => (2, 0.75f),
                HardwareTier.Mid => (1, 0.9f),
                _ => (0, 1f),
            };
            _ema = float.NaN; _over = _under = 0;
            _last = new LowSpecDecision(_lod, _scale, BudgetMs, 0, LowSpecAdjust.Hold);
        }
        LastAction = "🔍 하드웨어 " + TierLabel(hw.Tier) + " · LOD " + _lod;
        Raise();
    }

    /// <summary>GPU 탐지 결과 주입(외장 VRAM MB · 0이면 iGPU 공유 메모리 유지).</summary>
    public void UpdateGpu(long dedicatedVramMb)
    {
        HardwareSnapshot hw;
        lock (_gate) hw = _hw;
        ApplyHardware(dedicatedVramMb > 0 ? hw with { VramMb = dedicatedVramMb, HasDiscreteGpu = true } : hw);
    }

    /// <summary>여유 RAM 갱신(주기 샘플링 · 무할당).</summary>
    public void UpdateAvailableRam(long availableMb)
    {
        lock (_gate) _hw = _hw with { AvailableRamMb = Math.Max(0, availableMb) };
    }

    /// <summary>프레임 시간 관측 → LOD/해상도 결정(무할당).</summary>
    public LowSpecDecision Observe(double frameMs)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _observed);
            var f = (float)frameMs;
            _ema = float.IsNaN(_ema) ? f : _ema + 0.2f * (f - _ema);
            var budget = BudgetMs;
            var adj = LowSpecAdjust.Hold;
            var pressure = _hw.TotalRamMb > 0 && _hw.AvailableRamMb * 100 < _hw.TotalRamMb * 12;
            if (pressure && _lod < MaxLod)
            {
                _lod++; _scale = MathF.Max(MinScale, _scale - ScaleStep * 2);
                Interlocked.Increment(ref _pressureEvents); Interlocked.Increment(ref _stepDowns);
                _over = _under = 0; adj = LowSpecAdjust.MemoryPressure;
            }
            else if (f > budget * 2f && (_lod < MaxLod || _scale > MinScale))
            {
                _lod = Math.Min(MaxLod, _lod + 2); _scale = MathF.Max(MinScale, _scale - ScaleStep * 3);
                Interlocked.Increment(ref _stepDowns);
                _over = _under = 0; adj = LowSpecAdjust.Emergency;
            }
            else if (_ema > budget * 1.05f)
            {
                _under = 0;
                if (++_over >= DownFrames && (_lod < MaxLod || _scale > MinScale))
                {
                    // 해상도 먼저(저비용) · 하한 도달 시 LOD
                    if (_scale > MinScale + 0.001f) _scale = MathF.Max(MinScale, _scale - ScaleStep);
                    else _lod = Math.Min(MaxLod, _lod + 1);
                    Interlocked.Increment(ref _stepDowns);
                    _over = 0; adj = LowSpecAdjust.StepDown;
                }
            }
            else if (_ema < budget * 0.75f)
            {
                _over = 0;
                if (++_under >= UpFrames && (_lod > 0 || _scale < MaxScale))
                {
                    // LOD 먼저 복구 · 이후 해상도
                    if (_lod > 0) _lod--;
                    else _scale = MathF.Min(MaxScale, _scale + ScaleStep);
                    Interlocked.Increment(ref _stepUps);
                    _under = 0; adj = LowSpecAdjust.StepUp;
                }
            }
            else _over = _under = 0;
            _last = new LowSpecDecision(_lod, _scale, budget, _ema, adj);
            return _last;
        }
    }

    /// <summary>LOD별 상대 연산 비용(LOD0=1 · 단계당 ×0.62).</summary>
    public static float LodCost(int lod) => lod switch { 0 => 1f, 1 => 0.62f, 2 => 0.38f, 3 => 0.24f, _ => 0.15f };

    public static string TierLabel(HardwareTier t) => t switch
    {
        HardwareTier.UltraLow => "초저사양",
        HardwareTier.Low => "저사양",
        HardwareTier.Mid => "중급",
        _ => "고사양",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LowSpec.Profiler.OnChange", ex); }
    }
}
