using System;
using PromptFlowStudio.Services.Wan;   // WanModelTier(AlibabaWanHybridEngine 모델 카탈로그) 재사용 — 카탈로그 중복 금지

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>가중치 정밀도 — 값이 클수록 메모리 절감(Fp16 원본 → Fp8 → Int8).</summary>
public enum WanQuantMode { Fp16, Fp8, Int8 }

/// <summary>생성 작업 스펙(해상도/프레임/fps/양자화) — 다운스케일 사다리의 각 단은 이 레코드를 갱신한다.</summary>
public sealed record WanJobSpec(string ModelTierId, int Width, int Height, int Frames, int Fps, WanQuantMode Quant);

/// <summary>VAE 시간축 타일링 계획 — TileFrames 단위 분할 디코드(피크 활성값 메모리를 프레임 전체가 아닌 1타일로 절감).</summary>
public sealed record WanTilingPlan(int TileFrames, int TileCount, int OverlapFrames);

/// <summary>순차 CPU↔GPU 블록 오프로드 계획 — ResidentBlocks 만 GPU 상주, 나머지는 CPU 상주 후 필요 시 스트리밍.</summary>
public sealed record WanOffloadPlan(int ResidentBlocks, int TotalBlocks, int OffloadedBlocks);

/// <summary>실행 계획 최종 판정 — Ok=false 면 최소 레지던트(1블록)조차 예산 초과(추가 다운스케일 필요).</summary>
public sealed record WanExecutionPlan(bool Ok, WanQuantMode Quant, WanTilingPlan Tiling, WanOffloadPlan Offload, double PeakVramMb, double BudgetVramMb, string Note);

/// <summary>
/// v10.1.1 알리바바 Wan 2.1 저사양 가속 엔진 — 4~8GB VRAM 에서도 OOM 없이 추론 가능한 실행 계획을 산출한다.
///   ▸ <b>순차 CPU↔GPU 블록 오프로드</b>: 전체 DiT 블록 중 예산이 허용하는 최대 개수만 GPU 상주, 나머지는 CPU에
///     두고 필요한 순간에만 스트리밍(고전 sequential offload 기법 — 상주 블록이 많을수록 빠르나 VRAM 더 소모).
///   ▸ <b>VAE 시간축 타일링</b>: 전체 프레임을 한 번에 디코드하지 않고 TileFrames 단위로 나눠 순차 디코드해
///     피크 활성값 메모리를 프레임 총량이 아닌 1타일 분량으로 억제.
///   ▸ <b>FP8/INT8 동적 양자화</b>: 가중치 바이트폭을 축소(Fp16→Fp8→Int8)해 상주 블록당 메모리 자체를 절감.
///   ▸ 세 기법을 결합해 예산 내 <b>최대 레지던트 블록 수</b>를 이진탐색으로 확정(<see cref="PlanExecution"/>).
///   ⚠ 실 텐서 커널/모델 로딩은 결선점(스텁 · 교체 시 상위 무변경) — 여기서는 메모리 예산 산정과 오프로드/타일링/
///     양자화 계획 산출만 담당(순수 BCL·수치 모델). <see cref="Services.Wan.AlibabaWanHybridEngine"/> 의 모델 티어
///     카탈로그(<see cref="WanModelTier"/>)를 그대로 재사용해 병행 카탈로그를 만들지 않는다.
/// </summary>
public sealed class AlibabaWanLowSpecEngine
{
    /// <summary>Wan 2.1 DiT 블록 수 근사(placeholder — 실 아키텍처 확정 시 상수만 교체하면 상위 계산 로직 무변경).</summary>
    public const int TotalBlocks = 30;
    private const double ActivationBaseMbAt720p = 350;   // 720p 1프레임 활성값 근사(placeholder)
    private const double ReservedOverheadMb = 512;        // CUDA 컨텍스트 등 고정 오버헤드 근사
    private const int DefaultTileFrames = 8;
    private const int DefaultOverlapFrames = 2;

    private long _plansComputed;
    private long _oomGuardTrips;

    /// <summary>산출된 실행계획 누적 수.</summary>
    public long PlansComputed => System.Threading.Interlocked.Read(ref _plansComputed);
    /// <summary>최소 레지던트(1블록)조차 예산 초과로 판정된 횟수(추가 다운스케일 필요 신호).</summary>
    public long OomGuardTrips => System.Threading.Interlocked.Read(ref _oomGuardTrips);

    /// <summary>양자화 모드별 가중치 바이트/파라미터 절감 계수(Fp16=1.0 기준). Int8 은 활성값 보조 절감도 반영해 더 공격적.</summary>
    public static double QuantFactor(WanQuantMode m) => m switch { WanQuantMode.Fp16 => 1.0, WanQuantMode.Fp8 => 0.5, _ => 0.28 };

    /// <summary>모델 파라미터(B) → FP16 기준 총 가중치 MB(1B=2GB, 2바이트/파라미터) → 양자화 계수 적용.</summary>
    public static double WeightsMb(double paramsB, WanQuantMode quant) => paramsB * 2000.0 * QuantFactor(quant);

    /// <summary>해상도(720p=1280×720 기준 선형 스케일) 1프레임 활성값 MB 근사.</summary>
    public static double ActivationMbPerFrame(int width, int height)
        => width * (double)height / (1280.0 * 720.0) * ActivationBaseMbAt720p;

    /// <summary>VAE 시간축 타일링 계획 — TileFrames 단위 분할(마지막 타일은 나머지 프레임 · 최소 1타일).</summary>
    public static WanTilingPlan PlanTiling(int frames, int tileFrames = DefaultTileFrames, int overlap = DefaultOverlapFrames)
    {
        var tf = Math.Max(1, tileFrames);
        var tiles = Math.Max(1, (int)Math.Ceiling(Math.Max(frames, 1) / (double)tf));
        var ov = Math.Clamp(overlap, 0, Math.Max(tf - 1, 0));
        return new WanTilingPlan(tf, tiles, ov);
    }

    /// <summary>레지던트 블록 r 개에서의 피크 VRAM(MB) = r/총블록 × 가중치 + 타일당 활성값 + 고정 오버헤드.</summary>
    public static double PeakVramMb(int residentBlocks, double weightsMb, double activationMbPerFrame, int tileFrames)
        => residentBlocks / (double)TotalBlocks * weightsMb + activationMbPerFrame * tileFrames + ReservedOverheadMb;

    /// <summary>
    /// VRAM 예산(MB) 내 최대 레지던트 블록 수를 이진탐색으로 확정(상주 많을수록 CPU↔GPU 스트리밍 오버헤드 감소=고속).
    /// 1블록조차 예산 초과면 Ok=false(추가 양자화/해상도/프레임 다운스케일은 <c>AdaptiveHardwareProfiler</c> 몫).
    /// </summary>
    public WanExecutionPlan PlanExecution(WanModelTier tier, int width, int height, int frames, WanQuantMode quant, double budgetVramMb)
    {
        System.Threading.Interlocked.Increment(ref _plansComputed);
        var weights = WeightsMb(tier.ParamsB, quant);
        var actPerFrame = ActivationMbPerFrame(width, height);
        var tiling = PlanTiling(frames);

        int lo = 1, hi = TotalBlocks, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (PeakVramMb(mid, weights, actPerFrame, tiling.TileFrames) <= budgetVramMb) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (best == 0)
        {
            System.Threading.Interlocked.Increment(ref _oomGuardTrips);
            var minPeak = PeakVramMb(1, weights, actPerFrame, tiling.TileFrames);
            return new WanExecutionPlan(false, quant, tiling, new WanOffloadPlan(0, TotalBlocks, TotalBlocks), minPeak, budgetVramMb,
                $"최소 레지던트(1블록)조차 예산 초과({minPeak:0}MB > {budgetVramMb:0}MB) — 추가 다운스케일 필요");
        }

        var finalPeak = PeakVramMb(best, weights, actPerFrame, tiling.TileFrames);
        return new WanExecutionPlan(true, quant, tiling, new WanOffloadPlan(best, TotalBlocks, TotalBlocks - best), finalPeak, budgetVramMb,
            $"레지던트 {best}/{TotalBlocks}블록 · 타일 {tiling.TileCount}개(각 {tiling.TileFrames}프레임) · 피크 {finalPeak:0}/{budgetVramMb:0}MB");
    }

    /// <summary>
    /// v10.2.1: 동일 스펙(해상도/프레임 고정)에서 Fp16→Fp8→Int8 순으로 예산 충족 최소 양자화를 탐색(벤치마크 전용
    /// 가벼운 헬퍼 — 해상도/프레임까지 건드리는 <c>AdaptiveHardwareProfiler.AutoDownscaleAsync</c> 와 달리 양자화
    /// 축 하나만 순회해 "동일 작업에 VRAM 예산만 바뀌면?"을 순수하게 격리 관측할 수 있다). 전부 실패해도 Int8
    /// 계획(Ok=false 가능)을 반환해 호출측이 실패 사유를 그대로 보고할 수 있게 한다.
    /// </summary>
    public (WanExecutionPlan Plan, WanQuantMode QuantUsed) PlanBestQuant(WanModelTier tier, int width, int height, int frames, double budgetVramMb)
    {
        WanExecutionPlan? last = null;
        foreach (var q in new[] { WanQuantMode.Fp16, WanQuantMode.Fp8, WanQuantMode.Int8 })
        {
            last = PlanExecution(tier, width, height, frames, q, budgetVramMb);
            if (last.Ok) return (last, q);
        }
        return (last!, WanQuantMode.Int8);
    }
}
