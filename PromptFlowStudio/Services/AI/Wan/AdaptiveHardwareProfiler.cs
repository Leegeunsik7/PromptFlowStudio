using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // WanPipelineHardening(하드닝된 nvidia-smi 프로브) · AlibabaWanHybridEngine.RecommendLocalTier 재사용

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>하드웨어 프로필 스냅샷 — VRAM/RAM 탐지치 + 권장 모델 티어.</summary>
public sealed record HardwareProfile(int VramMb, bool VramDetected, long RamMb, WanModelTier RecommendedTier, DateTimeOffset ProbedAt);

/// <summary>자동 다운스케일 결과 — 실행 가능 여부, 최종 확정 스펙, 적용된 다운스케일 단계 수, 최종 실행계획.</summary>
public sealed record WanDownscaleResult(bool Feasible, WanJobSpec FinalSpec, int StepsApplied, WanExecutionPlan Plan);

/// <summary>
/// v10.1.2 적응형 하드웨어 프로파일러 — <see cref="WanPipelineHardening"/>(타임아웃 강제종료 nvidia-smi 프로브)를
/// 재사용해 VRAM 을 탐지하고(중복 구현 금지 · TTL 캐시로 과다 프로세스 스폰 방지), 순수 BCL
/// <see cref="GC.GetGCMemoryInfo"/>(컨테이너/cgroup 인지)로 RAM 가용량을 산정한다. 원하는 작업 스펙이 예산을
/// 초과하면 양자화(Fp16→Fp8→Int8) → 해상도(100%→75%→50%) → 프레임수(바닥까지) 순으로 결정적 사다리를 따라
/// <see cref="AlibabaWanLowSpecEngine"/> 재평가를 반복해 OOM 없이 실행 가능한 최소 타협 스펙을 자율 확정한다.
///   ⚠ VRAM 탐지가 Process 기반(nvidia-smi)이라 <b>데스크톱 전용</b> — Mobile DI 미등록(<see cref="WanPipelineHardening"/>
///     과 동일 정책, iOS/Android 는 Process.Start 미지원).
/// </summary>
public sealed class AdaptiveHardwareProfiler
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(4);     // nvidia-smi 과다 스폰 방지 캐시 창
    private const double VramSafetyMargin = 0.85;                            // OS/타 프로세스용 15% 예비
    private const int FallbackFloorVramMb = 4000;                            // 미탐지 시 과제 명시 하한(4GB)로 보수 가정
    private const int MaxDownscaleSteps = 12;
    private const int MinFrames = 16;

    private readonly WanPipelineHardening _hardening;
    private readonly AlibabaWanLowSpecEngine _engine;
    private readonly object _gate = new();
    private HardwareProfile? _cached;
    private long _profiles, _downscales, _giveUps;

    public AdaptiveHardwareProfiler(WanPipelineHardening hardening, AlibabaWanLowSpecEngine engine)
    {
        _hardening = hardening;
        _engine = engine;
    }

    public long ProfileCount => Interlocked.Read(ref _profiles);
    public long DownscaleCount => Interlocked.Read(ref _downscales);
    public long GiveUpCount => Interlocked.Read(ref _giveUps);
    public HardwareProfile? LastProfile { get { lock (_gate) return _cached; } }
    /// <summary>가장 최근 <see cref="AutoDownscaleAsync"/> 결과(HUD 표시용 · 계산 자체엔 미사용).</summary>
    public WanDownscaleResult? LastDownscale { get; private set; }

    /// <summary>TTL 캐시 우선 하드웨어 프로파일 — 만료 시에만 실제 nvidia-smi 재탐지(무분별 스폰 방지).</summary>
    public async Task<HardwareProfile> ProfileAsync(CancellationToken ct = default)
    {
        lock (_gate) { if (_cached is { } c && DateTimeOffset.UtcNow - c.ProbedAt < ProbeTtl) return c; }

        var probe = await _hardening.DetectVramMbAsync(ct: ct).ConfigureAwait(false);
        var vramMb = probe.Detected ? probe.TotalMb : 0;
        var ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var tier = AlibabaWanHybridEngine.RecommendLocalTier(vramMb);   // 정적 헬퍼 재사용(카탈로그/임계값 중복 금지)
        var profile = new HardwareProfile(vramMb, probe.Detected, ramMb, tier, DateTimeOffset.UtcNow);

        Interlocked.Increment(ref _profiles);
        lock (_gate) _cached = profile;
        return profile;
    }

    /// <summary>
    /// 원하는 스펙이 현재 하드웨어 예산을 초과하면 결정적 사다리(양자화→해상도→프레임)로 자율 다운스케일해
    /// 실행 가능한 최소 타협 스펙을 확정한다. 바닥 스펙까지도 불가하면 Feasible=false(포기 · GiveUpCount 증가).
    /// </summary>
    public async Task<WanDownscaleResult> AutoDownscaleAsync(WanJobSpec desired, CancellationToken ct = default)
    {
        var profile = await ProfileAsync(ct).ConfigureAwait(false);
        var budget = profile.VramDetected ? profile.VramMb * VramSafetyMargin : FallbackFloorVramMb * VramSafetyMargin;
        var tier = AlibabaWanHybridEngine.TierFor(desired.ModelTierId) ?? profile.RecommendedTier;

        var spec = desired;
        var plan = _engine.PlanExecution(tier, spec.Width, spec.Height, spec.Frames, spec.Quant, budget);
        var steps = 0;

        while (!plan.Ok && steps < MaxDownscaleSteps)
        {
            steps++;
            if (spec.Quant != WanQuantMode.Int8)
            {
                spec = spec with { Quant = spec.Quant == WanQuantMode.Fp16 ? WanQuantMode.Fp8 : WanQuantMode.Int8 };
            }
            else if (spec.Width > 512 || spec.Height > 288)
            {
                spec = spec with { Width = Math.Max(512, (int)(spec.Width * 0.75) / 8 * 8), Height = Math.Max(288, (int)(spec.Height * 0.75) / 8 * 8) };
            }
            else if (spec.Frames > MinFrames)
            {
                spec = spec with { Frames = Math.Max(MinFrames, spec.Frames - 8) };
            }
            else break;   // 양자화 바닥 + 해상도 바닥 + 프레임 바닥 — 더 내릴 곳 없음

            plan = _engine.PlanExecution(tier, spec.Width, spec.Height, spec.Frames, spec.Quant, budget);
        }

        if (steps > 0) Interlocked.Increment(ref _downscales);
        if (!plan.Ok) Interlocked.Increment(ref _giveUps);
        var result = new WanDownscaleResult(plan.Ok, spec, steps, plan);
        LastDownscale = result;
        return result;
    }

    public void ResetCounters()
    {
        lock (_gate) _cached = null;
        Interlocked.Exchange(ref _profiles, 0);
        Interlocked.Exchange(ref _downscales, 0);
        Interlocked.Exchange(ref _giveUps, 0);
    }
}
