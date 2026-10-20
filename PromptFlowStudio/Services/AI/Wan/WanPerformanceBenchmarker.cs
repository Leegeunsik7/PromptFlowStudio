using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // WanModelTier/AlibabaWanHybridEngine.Tiers 재사용

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>가상 VRAM 예산 1개 티어의 스트레스 결과. P95/P99·DroppedFrames 는 v10.5.0 PerformanceReview 프로토콜(01_performance_review.xml) 요구 지표.</summary>
public sealed record BenchmarkTierResult(int BudgetMb, bool Ok, WanQuantMode QuantUsed, int ResidentBlocks, int TotalBlocks,
    int TileCount, double DecodeMsPerTile, double TotalDecodeMs, double PeakVramMb, double ThroughputFramesPerSec, string Note,
    double P95Ms, double P99Ms, int DroppedFrames);

/// <summary>전체 스윕(4/6/8GB) 결과 — 최고/최저 처리량 예산도 함께 산출.</summary>
public sealed record BenchmarkSweepReport(DateTimeOffset At, string ModelTierId, int Width, int Height, int Frames,
    IReadOnlyList<BenchmarkTierResult> Tiers, int BestThroughputBudgetMb, int WorstThroughputBudgetMb, double SweepElapsedMs);

/// <summary>v10.4.0 동적 램프 스트레스 결과 — 고정 워크로드가 처음 실패(Ok=false)하는 가상 VRAM 브레이킹포인트.</summary>
public sealed record RampStressResult(int BreakingPointMb, int StartMb, int StepMb, int StepsRun, WanQuantMode QuantAtBreak, double ElapsedMs, string Note);

/// <summary>
/// v10.2.1 Wan 성능 벤치마커 — 실 하드웨어 탐지에 의존하지 않는 <b>가상 VRAM 예산</b>(4000/6000/8000MB) 3단
/// 스트레스 스윕. 각 예산에서 <see cref="AlibabaWanLowSpecEngine.PlanBestQuant"/> 로 최소 충족 양자화를 확정한 뒤,
/// 타일당 실제 CPU 부동소수 연산 프록시(해상도×타일프레임×양자화 계수에 비례한 반복량)를 <see cref="Stopwatch"/> 로
/// 실측해 "VAE 디코딩 속도"의 정직한 대리 지표를 산출한다(⚠ 실 GPU 텐서 커널 결선 전까지 CPU 프록시 실측 ·
/// 결선점 스텁 · 교체 시 상위 무변경). 피크 메모리는 <see cref="AlibabaWanLowSpecEngine"/> 의 계산값(추정) 그대로
/// 보고한다(가상 지표를 실측처럼 과장하지 않음). 순수 BCL · 하드웨어 비의존이라 모바일 등록 가능.
/// </summary>
public sealed class WanPerformanceBenchmarker
{
    /// <summary>스윕 대상 가상 VRAM 예산(MB) — 저사양 4~8GB 대역 커버.</summary>
    public static readonly int[] VirtualBudgetsMb = { 4000, 6000, 8000 };

    // v10.5.0-F3: 하드코딩 반복 상수 제거 — AutoTunerHardwareCalibrator 가 호스트별로 산출하는 유효 반복량으로 대체
    // (원본 상수 18.5M 은 이제 AutoTunerHardwareCalibrator.SafeFallbackBaseIterations 에 안전 폴백으로만 보존).

    private readonly AlibabaWanLowSpecEngine _engine;
    private readonly AutoTunerHardwareCalibrator _calibrator;
    private readonly object _gate = new();
    private System.Threading.Timer? _demoTimer;
    private BenchmarkSweepReport? _last;
    private RampStressResult? _lastRamp;
    private long _sweeps, _tiersProfiled, _ramps;
    private double _sink;   // JIT 데드코드 제거 방지용 관측 싱크(실제 사용처 없음 · 부작용 유지 목적)
    private bool _disposed;

    public event Action? OnChange;

    public WanPerformanceBenchmarker(AlibabaWanLowSpecEngine engine, AutoTunerHardwareCalibrator calibrator)
    {
        _engine = engine;
        _calibrator = calibrator;
    }

    public bool DemoRunning => _demoTimer is not null;
    public string? LastAction { get; private set; }
    public BenchmarkSweepReport? LastReport { get { lock (_gate) return _last; } }
    public long Sweeps => Interlocked.Read(ref _sweeps);
    public long TiersProfiled => Interlocked.Read(ref _tiersProfiled);
    public long Ramps => Interlocked.Read(ref _ramps);
    public RampStressResult? LastRamp { get { lock (_gate) return _lastRamp; } }

    /// <summary>고정 워크로드(기본 832×480@33f · wan1.3b_fp8)로 4/6/8GB 가상 예산 스윕 1회 실행.</summary>
    public async Task<BenchmarkSweepReport> RunFullSweepAsync(string? modelTierId = null, int width = 832, int height = 480, int frames = 33, CancellationToken ct = default)
    {
        var tier = AlibabaWanHybridEngine.TierFor(modelTierId) ?? AlibabaWanHybridEngine.Tiers[0];
        var sw = Stopwatch.StartNew();
        var results = new List<BenchmarkTierResult>(VirtualBudgetsMb.Length);

        foreach (var budget in VirtualBudgetsMb)
        {
            ct.ThrowIfCancellationRequested();
            var (plan, quant) = _engine.PlanBestQuant(tier, width, height, frames, budget);
            var tileCount = plan.Tiling.TileCount;
            var decodeMs = 0.0;
            var tileTimes = new List<double>(tileCount);   // P95/P99 산출용 원표본(스윕은 저빈도라 규칙6 핫패스 예외)
            if (plan.Ok)
            {
                for (var i = 0; i < tileCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = await Task.Run(() => BusyWorkMs(IterationsFor(width, height, plan.Tiling.TileFrames, quant)), ct).ConfigureAwait(false);
                    decodeMs += t;
                    tileTimes.Add(t);
                }
            }
            var perTile = tileCount > 0 ? decodeMs / tileCount : 0;
            var fps = decodeMs > 0 ? frames / (decodeMs / 1000.0) : 0;
            var (p95, p99, dropped) = ComputePercentilesAndDrops(tileTimes);
            results.Add(new BenchmarkTierResult(budget, plan.Ok, quant, plan.Offload.ResidentBlocks, plan.Offload.TotalBlocks,
                tileCount, Math.Round(perTile, 3), Math.Round(decodeMs, 3), Math.Round(plan.PeakVramMb, 1), Math.Round(fps, 2), plan.Note,
                Math.Round(p95, 3), Math.Round(p99, 3), dropped));
            Interlocked.Increment(ref _tiersProfiled);
        }
        sw.Stop();

        var best = 0; var worst = 0; var bestFps = -1.0; var worstFps = double.MaxValue;
        foreach (var r in results)
        {
            if (r.Ok && r.ThroughputFramesPerSec > bestFps) { bestFps = r.ThroughputFramesPerSec; best = r.BudgetMb; }
            if (r.Ok && r.ThroughputFramesPerSec < worstFps) { worstFps = r.ThroughputFramesPerSec; worst = r.BudgetMb; }
        }

        var report = new BenchmarkSweepReport(DateTimeOffset.UtcNow, tier.Id, width, height, frames, results, best, worst, sw.Elapsed.TotalMilliseconds);
        Interlocked.Increment(ref _sweeps);
        lock (_gate) { _last = report; LastAction = $"스윕 완료 · {results.Count}티어 · {sw.Elapsed.TotalMilliseconds:0}ms"; }
        OnChange?.Invoke();
        return report;
    }

    /// <summary>
    /// v10.4.0 동적 램프 스트레스 — <see cref="RunFullSweepAsync"/>(고정 3티어 실측 처리량)와 달리 startMb 에서
    /// stepMb 씩 감소시키며 <see cref="AlibabaWanLowSpecEngine.PlanBestQuant"/> 계획만 재평가(실 busy-work 없음 ·
    /// 구조적 탐색이라 빠름)해 워크로드가 처음 실패하는 가상 VRAM 브레이킹포인트를 확정한다. Zero-Alloc(CLAUDE.md
    /// 규칙6): 루프 내부 LINQ/보간 없이 순수 산술 반복만 수행 — 결과 Note 문자열은 루프 종료 후 1회만 구성.
    /// </summary>
    public async Task<RampStressResult> RunDynamicRampAsync(string? modelTierId = null, int width = 832, int height = 480, int frames = 33,
        int startMb = 8000, int stepMb = 250, int floorMb = 1000, CancellationToken ct = default)
    {
        var tier = AlibabaWanHybridEngine.TierFor(modelTierId) ?? AlibabaWanHybridEngine.Tiers[0];
        var sw = Stopwatch.StartNew();
        var budget = startMb;
        var steps = 0;
        WanExecutionPlan plan;
        WanQuantMode quant;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (plan, quant) = _engine.PlanBestQuant(tier, width, height, frames, budget);   // 순수 산술(이진탐색) · Task.Run 불필요
            steps++;
            if (steps % 8 == 0) await Task.Yield();   // 협조적 양보(장시간 램프에도 호출 스레드 비블로킹) · 매 스텝 Task 디스패치 회피
            if (!plan.Ok || budget <= floorMb) break;
            budget -= stepMb;
        }
        sw.Stop();
        Interlocked.Increment(ref _ramps);

        var breakingPoint = plan.Ok ? floorMb : budget;
        var note = plan.Ok
            ? $"바닥({floorMb}MB)까지 안정 — {steps}스텝 내 브레이킹포인트 미도달"
            : $"브레이킹포인트 {breakingPoint}MB 부근에서 실패 검출({steps}스텝 · {quant})";
        var result = new RampStressResult(breakingPoint, startMb, stepMb, steps, quant, sw.Elapsed.TotalMilliseconds, note);
        lock (_gate) { _lastRamp = result; LastAction = note; }
        OnChange?.Invoke();
        return result;
    }

    // 해상도×타일프레임×양자화 계수에 비례한 반복량 — 값이 클수록 실측 ms 도 커짐(정직한 상대 비교 · 절대치는 참고용).
    private int IterationsFor(int width, int height, int tileFrames, WanQuantMode quant)
    {
        var resScale = width * (double)height / (1280.0 * 720.0);
        var quantScale = AlibabaWanLowSpecEngine.QuantFactor(quant);
        var baseIterations = _calibrator.GetEffectiveBaseIterations();   // 호스트별 자율 캘리브레이션(실패/스파이크 시 18.5M 안전 폴백)
        return Math.Max(1000, (int)(baseIterations * resScale * quantScale * Math.Max(tileFrames, 1)));
    }

    // v10.5.0(01_performance_review.xml): 티어 내 원표본 정렬 후 P95/P99 산출 + LivePipelineAutoTuner.TargetFrameMs
    // 초과분을 "프레임 드랍"으로 계수(두 클래스의 실시간 SLA 기준을 일치시켜 상호 참조 가능하게 함).
    private static (double P95, double P99, int Dropped) ComputePercentilesAndDrops(List<double> samples)
    {
        if (samples.Count == 0) return (0, 0, 0);
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        double At(double p)
        {
            var idx = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[idx];
        }
        var dropped = 0;
        foreach (var s in samples) if (s > LivePipelineAutoTuner.TargetFrameMs) dropped++;
        return (At(0.95), At(0.99), dropped);
    }

    // 실제 CPU 부동소수 연산 루프(Stopwatch 실측) — 필드 부작용으로 JIT 데드코드 제거 회피.
    private double BusyWorkMs(int iterations)
    {
        var sw = Stopwatch.StartNew();
        var acc = 1.0001;
        for (var i = 0; i < iterations; i++) acc = acc * 1.0000001 + 0.0000001;
        _sink = acc;
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_demoTimer is not null || _disposed) return;
            _demoTimer = new System.Threading.Timer(_ => _ = SweepTickAsync(), null, 0, 8000);
            LastAction = "벤치마크 오토 스윕 기동(8초 주기)";
        }
        OnChange?.Invoke();
    }

    private async Task SweepTickAsync()
    {
        try { await RunFullSweepAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (_gate) LastAction = "스윕 오류: " + ex.Message; OnChange?.Invoke(); }
    }

    public void StopDemo()
    {
        lock (_gate) { _demoTimer?.Dispose(); _demoTimer = null; LastAction = "벤치마크 오토 스윕 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate) { _last = null; _lastRamp = null; _sweeps = 0; _tiersProfiled = 0; _ramps = 0; LastAction = "벤치마커 초기화"; }
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _demoTimer?.Dispose(); _demoTimer = null; }
    }
}
