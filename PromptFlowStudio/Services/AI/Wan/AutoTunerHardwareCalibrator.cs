using System;
using System.Diagnostics;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>캘리브레이션 1회 실행 결과 — 값 자체를 신뢰 여부(Ok)와 함께 보고(비정상 시 안전 폴백이 이미 반영됨).</summary>
public sealed record CalibrationResult(bool Ok, double MeasuredOpsPerMs, double DynamicIterationMultiplier, long EffectiveBaseIterations, DateTimeOffset At, double ElapsedMs, string Note);

/// <summary>
/// v10.5.0-F3 하드웨어 자율 캘리브레이터 — <see cref="WanPerformanceBenchmarker"/>의 CPU 프록시 반복 상수
/// (원래 하드코딩값 <see cref="SafeFallbackBaseIterations"/>=18.5M · v10.4.0 에서 이 개발 머신에 맞춰 확정됨)를
/// <b>호스트별로 재정규화</b>한다: 시동 시(또는 24시간 TTL 경과 후) 동일 연산 프리미티브를 3회 마이크로 벤치마크해
/// 중앙값(1회성 스파이크 완충)으로 이 머신의 ops/ms 를 측정하고, <see cref="ReferenceOpsPerMs"/>(=18.5M 상수를
/// 확정한 그 개발 머신에서 동일 방식으로 측정된 기준값) 대비 순수 비율로 배율을 산출한다 — <see cref="WanPerformanceBenchmarker.IterationsFor"/>
/// 가 해상도/양자화/타일수 스케일을 이미 자체 적용하므로, 캘리브레이터는 "이 머신이 기준 머신보다 몇 배 빠른가"라는
/// 순수 CPU 속도비만 책임진다(해상도·타일수를 캘리브레이터가 다시 가정하면 이중 스케일링 버그 — v10.5.0-F3 최초
/// 구현에서 "720p·8타일 참조" 가정으로 실제 발생 후 런타임 프로브로 발견/수정). 측정값이 안전 범위를 벗어나면
/// (스파이크/실패) 배율 1.0(원본 18.5M 그대로)으로 Graceful 폴백한다.
///   ▸ Zero-Alloc: 벤치마크 루프는 스칼라 산술만(박싱/할당 0) · 외부 IO 0(파일/네트워크 접근 없음 · 메모리 내 TTL 캐시만).
/// </summary>
public sealed class AutoTunerHardwareCalibrator
{
    /// <summary>원본 하드코딩 안전 상수(v10.4.0) — 캘리브레이션 실패/스파이크 시 이 값 그대로(배율 1.0) 사용.</summary>
    public const long SafeFallbackBaseIterations = 18_500_000;

    // SafeFallbackBaseIterations(18.5M)를 확정한 그 개발 머신에서 MicroBenchOpsPerMs 와 동일 루프로 측정한
    // 기준 ops/ms(4회 측정 665,491/668,225.9/645,515.3/670,915.8 의 중앙값대 대표값) — 배율=측정값/이 값이므로
    // "그 머신을 다시 측정하면 배율=1.0"이 성립(자기일관성, 프로브로 실측 검증됨).
    private const double ReferenceOpsPerMs = 665_000.0;
    private const int MicroBenchIterations = 2_000_000; // 트라이얼당 반복량(수 ms 대 측정 · 과도한 시동 지연 방지)
    private const int Trials = 3;                       // 중앙값 채택 표본 수(홀수 · 스파이크 완충)
    private const double MinSaneMultiplier = 0.1, MaxSaneMultiplier = 10.0;   // 이 범위 밖은 스파이크로 간주 → 폴백
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private CalibrationResult? _cached;
    private double _sink;   // JIT 데드코드 제거 방지(WanPerformanceBenchmarker.BusyWorkMs 와 동일 기법)
    private long _calibrations, _fallbacks;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public long Calibrations => Interlocked.Read(ref _calibrations);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public CalibrationResult? LastResult { get { lock (_gate) return _cached; } }

    /// <summary>현재 유효 배율(미측정이면 즉시 1회 캘리브레이션 후 반환 · TTL 이내면 캐시 재사용).</summary>
    public double GetMultiplier() => EnsureCalibrated().DynamicIterationMultiplier;

    /// <summary>이 머신에서 사용할 유효 반복 상수(=SafeFallbackBaseIterations × 배율).</summary>
    public long GetEffectiveBaseIterations() => EnsureCalibrated().EffectiveBaseIterations;

    /// <summary>TTL 캐시 우선 조회 — 만료(24시간) 또는 <paramref name="force"/> 시에만 실제 재측정.</summary>
    public CalibrationResult EnsureCalibrated(bool force = false)
    {
        lock (_gate)
        {
            if (!force && _cached is { } c && DateTimeOffset.UtcNow - c.At < CacheTtl) return c;
        }
        var result = RunCalibration();
        lock (_gate) { _cached = result; LastAction = result.Note; }
        Interlocked.Increment(ref _calibrations);
        if (!result.Ok) Interlocked.Increment(ref _fallbacks);
        OnChange?.Invoke();
        return result;
    }

    /// <summary>HUD "지금 재캘리브레이션" 버튼 등 — TTL 무시 강제 재측정.</summary>
    public CalibrationResult ForceRecalibrate() => EnsureCalibrated(force: true);

    private CalibrationResult RunCalibration()
    {
        var sw = Stopwatch.StartNew();
        Span<double> samples = stackalloc double[Trials];
        for (var t = 0; t < Trials; t++) samples[t] = MicroBenchOpsPerMs();
        samples.Sort();
        var median = samples[Trials / 2];
        sw.Stop();

        if (median <= 0 || double.IsNaN(median) || double.IsInfinity(median))
            return Fallback(sw.Elapsed.TotalMilliseconds, "측정 실패(비정상 값 · 0/NaN/Infinity) — 안전 폴백(배율 1.0)");

        // 순수 속도비 — 해상도/양자화/타일수는 IterationsFor 호출부가 이미 곱해 쓰므로 여기서 재적용하지 않는다.
        var multiplier = median / ReferenceOpsPerMs;
        var effectiveBase = SafeFallbackBaseIterations * multiplier;

        if (double.IsNaN(multiplier) || multiplier < MinSaneMultiplier || multiplier > MaxSaneMultiplier)
            return Fallback(sw.Elapsed.TotalMilliseconds, $"배율 {multiplier:0.###} 안전범위[{MinSaneMultiplier},{MaxSaneMultiplier}] 밖 — 스파이크 의심, 안전 폴백(배율 1.0)");

        return new CalibrationResult(true, Math.Round(median, 1), Math.Round(multiplier, 4), (long)Math.Round(effectiveBase),
            DateTimeOffset.UtcNow, sw.Elapsed.TotalMilliseconds,
            $"캘리브레이션 완료 · {median:0}ops/ms · 배율 {multiplier:0.###}× · 유효반복 {(long)effectiveBase:N0}");
    }

    private CalibrationResult Fallback(double elapsedMs, string note)
        => new(false, 0, 1.0, SafeFallbackBaseIterations, DateTimeOffset.UtcNow, elapsedMs, note);

    // WanPerformanceBenchmarker.BusyWorkMs 와 동일 연산 프리미티브 — 다른 명령 혼합이면 배율이 그 워크로드에 유효하지 않음.
    // Zero-Alloc(스칼라 산술만) · 외부 IO 0.
    private double MicroBenchOpsPerMs()
    {
        var sw = Stopwatch.StartNew();
        var acc = 1.0001;
        for (var i = 0; i < MicroBenchIterations; i++) acc = acc * 1.0000001 + 0.0000001;
        _sink = acc;
        sw.Stop();
        var ms = Math.Max(sw.Elapsed.TotalMilliseconds, 0.001);   // 0 나눗셈 회피(초고속 CPU 대비)
        return MicroBenchIterations / ms;
    }
}
