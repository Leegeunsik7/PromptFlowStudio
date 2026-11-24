using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Tests;

/// <summary>Step 35 — CPU 집약 엔진 스위트(시뮬 · 렌더 · SIMD 커널)를 한 컬렉션으로 묶어 서로는 직렬(동시 최대 1 개)로 돌린다.
///   병렬화 자체는 끄지 않으므로 경량 단위 테스트가 나머지 병렬 슬롯을 채운다. 두 개 이상이 동시에 전 코어를 점유하면
///   다른 테스트의 async 연속(continuation) · 소켓 타임아웃이 기아로 수십 초 지연되는 것을 실측으로 확인(Step 35 감사).</summary>
[CollectionDefinition("TitanHeavyCompute")]
public sealed class TitanHeavyComputeCollection { }

/// <summary>Step 42 — 무거운 엔진 스위트 2 번째 직렬 사슬. 단일 사슬(15 클래스 ≈ 140 s)이 병렬 구간의 꼬리였다:
///   경량 병렬 테스트는 ≈ 35 s 에 끝나고 나머지 ≈ 105 s 동안 16 코어 중 1 슬롯만 일함(TRX 타임라인 동시성 1.00).
///   측정 시간 기준 균형 분할 — A(<c>TitanHeavyCompute</c>): Continuum · Microclimate · VerticalVideo · SystemWide · UiExhaustive ≈ 71 s /
///   B(이 컬렉션): ViralLoop · BeatSync · WorldExpansion · RhythmMv · SpatialAcoustics · VisualQa · 경량 4 ≈ 69 s.
///   두 사슬 모두 벽시계 게이트 없음(순수 계산 단언) → 동시 최대 2 개. 각 사슬 내부는 여전히 직렬.</summary>
[CollectionDefinition("TitanHeavyComputeB")]
public sealed class TitanHeavyComputeBCollection { }

// Step 43 — 비병렬 컬렉션(ProcessGcMode · TimingSensitivePerformance) 감사 이관분은 A/B 두 사슬에만 배치한다(측정 시간 균형 A ≈ 131 s / B ≈ 140 s).
//   3 번째 사슬(TitanHeavyComputeC)은 실측 후 폐기: xUnit 동기화 컨텍스트 슬롯(maxParallelThreads 4)을 동기 CPU 중량 테스트 4 개
//   (A · B · C · 단독 BroadcastMcr)가 모두 점유하자 경량 async 테스트의 연속이 ≈ 45 s 정지 → 소켓/마감 타임아웃 5 건(VisionIntelligence ·
//   LocalVlmHttpClient · ServiceLifecycle · WanEngine · MediaFoundation 수명). 규칙: 동시 중량 사슬 ≤ 2(경량 연속용 슬롯 ≥ 2 확보).
// Step 44 — TitanReleaseTests(ProcessGcMode · 비병렬) 순수 계산 메서드 19 개를 TitanReleaseChainATests(A 몫) / TitanReleaseChainBTests(B 몫)로 분리.
//   실측(16 코어 · 유휴): 이관 전 A 177 s / B 216 s(경합 호스트) → 이관 후 A 136 s / B 144 s(1 차 158 / 147 s → 2 메서드 A→B 재균형). 비병렬 구간 -68 s.
//   사슬 수는 그대로 2 — 새 컬렉션 정의 없이 기존 A/B 에만 배치.

/// <summary>Step 35 — 컬렉션 병렬 실행 시 스레드 풀 기아 방지. 기본 최소 스레드(= 코어 수)로는 동기 대기를 섞는 테스트 4 개가
///   동시에 돌 때 풀 확장(초당 ~1–2 스레드) 지연이 async 연속을 수백 ms~수십 s 밀어 타임아웃 · 프로브 창 레이스를 만든다.
///   최소치를 코어 × 2 로 올려 즉시 투입(상한은 런타임 기본값 그대로 · 테스트 프로세스 한정).
///   코어 × 4 는 금지 — <c>AutonomousPerformanceSelfTuningSynthesizer</c> 의 부스트 상한(코어 × 4)과 같아져 자가 튜닝 테스트의 +4 부스트 여유가 사라진다.</summary>
internal static class ParallelExecutionGuards
{
    [ModuleInitializer]
    internal static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var floor = Math.Max(8, Environment.ProcessorCount * 2);
        ThreadPool.SetMinThreads(Math.Max(worker, floor), Math.Max(io, floor));
    }
}

/// <summary>
/// Step 39 — 스레드 국소 무할당 단언의 정상 상태(steady-state) 측정기.
///   <c>TitanReleaseTests</c> 는 <c>[Collection("ProcessGcMode")]</c>(DisableParallelization)라 다른 테스트와 겹쳐 돌지 않으며
///   (Step 44: 분리된 <c>TitanReleaseChainA/BTests</c> 는 A/B 사슬에서 병렬 구간과 겹친다 — 무할당 단언이 전부 이 재시도 창이라 가능),
///   측정값 <see cref="GC.GetAllocatedBytesForCurrentThread"/> 는 스레드 국소라 다른 스레드의 할당이 섞일 수 없다.
///   전체 실행에서만 보인 1회성 4,688 B 는 런타임 자체 전이(계층 JIT 승격 · OSR 패치포인트 · 호출 카운팅 완료 처리)가
///   측정 창 안의 <em>호출 스레드</em>에서 1회 할당한 것 — 앞선 수백 개 테스트의 JIT 부하가 계층 승격 지연(100 ms 타이머)을 밀어
///   단독 실행 때는 워밍업 중에 끝나던 전이가 측정 창으로 들어온다. 직렬화로는 없앨 수 없으므로
///   "같은 측정 창을 최대 N 회 반복해 한 번이라도 0 B 면 무할당" 으로 판정한다(진짜 누수는 매 창마다 재현되어 그대로 실패).
/// </summary>
internal static class SteadyStateAllocation
{
    /// <summary>측정 창 반복 상한(1회성 런타임 전이 흡수용 · 진짜 할당은 매 창 재현).</summary>
    internal const int MaxWindows = 4;

    /// <summary>Step 41 — 강제 전 창 감사 모드 환경변수(<c>1</c>). 켜면 0 B 조기 반환 없이 항상 <see cref="MaxWindows"/> 창을 돌려
    ///   "재시도 창이 후속 단언(카운터 · 링 · 가상 시계)을 깨지 않는가"(창 멱등성)를 전체 스위트에서 실측 검증한다.
    ///   평상시 재시도는 1회성 런타임 전이가 측정 창에 들어올 때만 일어나므로 비멱등 창은 단독 실행에선 절대 드러나지 않는다.</summary>
    internal const string ForceAllWindowsEnv = "PFS_ALLOC_FORCE_ALL_WINDOWS";

    /// <summary>프로세스 시작 시 1회 판독(측정 창 밖 · 문자열 할당이 창에 섞이지 않음).</summary>
    internal static readonly bool ForceAllWindows = Environment.GetEnvironmentVariable(ForceAllWindowsEnv) == "1";

    /// <summary><paramref name="window"/> 를 최대 <see cref="MaxWindows"/> 번 측정해 가장 작은 스레드 국소 할당 바이트를 반환(0 이면 즉시 반환).</summary>
    internal static long MinBytes(Action window) => MinBytes(window, out _);

    /// <summary><see cref="MinBytes(Action)"/> + 실제 실행된 창 수. 창이 상태를 누적(프레임 카운터 · 링 버퍼 · 가상 시계)하면
    ///   후속 단언을 <paramref name="windows"/> 배수로 표현해 재시도 여부와 무관하게 엄격히 유지한다.</summary>
    internal static long MinBytes(Action window, out int windows) => MinBytes(window, null, out windows);

    /// <summary>소비형(one-shot) 창용 — 재시도 <em>직전</em>마다 측정 창 밖에서 <paramref name="beforeRetry"/> 로 동일 작업량을 복원한다
    ///   (파이프라인 재기동 · 제자리 변환 입력 복원 · 출력 파일 정리). 복원 없이 재시도하면 두 번째 창이
    ///   "할 일이 없어서" 0 B 를 보고하는 공허한 통과(vacuous pass)가 되어 첫 창의 진짜 할당을 가린다.</summary>
    internal static long MinBytes(Action window, Action? beforeRetry, out int windows)
    {
        Span<long> samples = stackalloc long[MaxWindows];
        windows = Sample(window, beforeRetry, ForceAllWindows, samples);
        return Min(samples[..windows]);
    }

    /// <summary>표본 최소값(빈 표본은 <see cref="long.MaxValue"/>).</summary>
    internal static long Min(ReadOnlySpan<long> samples)
    {
        var min = long.MaxValue;
        foreach (var s in samples) if (s < min) min = s;
        return min;
    }

    /// <summary>원시 측정기 — 창별 할당 바이트를 <paramref name="samples"/> 에 기록하고 실행 창 수를 반환.
    ///   <paramref name="forceAll"/> 이 false 면 첫 0 B 창에서 멈춘다. 창 · 복원 예외는 재시도 없이 즉시 전파(실패 은폐 금지).</summary>
    internal static int Sample(Action window, Action? beforeRetry, bool forceAll, Span<long> samples)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (samples.IsEmpty) throw new ArgumentException("최소 1 창 필요", nameof(samples));
        var max = Math.Min(MaxWindows, samples.Length);
        var n = 0;
        while (n < max)
        {
            if (n > 0) beforeRetry?.Invoke();   // 측정 창 밖 — 복원 할당은 계상되지 않음
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            window();
            var d = GC.GetAllocatedBytesForCurrentThread() - a0;
            samples[n++] = d;
            if (d == 0 && !forceAll) break;
        }
        return n;
    }

    /// <summary>람다 없는 측정기 — 참조 로컬(<c>stackalloc</c> Span) · <c>ref</c>/<c>out</c> 매개변수는 람다가 캡처할 수 없으므로
    ///   <c>var p = SteadyStateAllocation.Start(); do { 창 } while (p.Retry()); Assert.Equal(0, p.MinBytes);</c> 로 같은 판정을 한다.</summary>
    internal static Probe Start() => Probe.Begin(ForceAllWindows);

    /// <summary>무할당 do/while 측정 상태(값 형식 · 측정 창 안에서 할당 0).</summary>
    internal struct Probe
    {
        private long _a0;
        private long _min;
        private int _windows;
        private bool _forceAll;

        internal static Probe Begin(bool forceAll)
        {
            var p = new Probe { _min = long.MaxValue, _forceAll = forceAll };
            p._a0 = GC.GetAllocatedBytesForCurrentThread();
            return p;
        }

        /// <summary>방금 끝난 창을 기록하고 다음 창을 돌릴지 반환(true 면 새 창 측정 시작).</summary>
        internal bool Retry()
        {
            var d = GC.GetAllocatedBytesForCurrentThread() - _a0;
            _windows++;
            if (d < _min) _min = d;
            if (_windows >= MaxWindows || (d == 0 && !_forceAll)) return false;
            _a0 = GC.GetAllocatedBytesForCurrentThread();
            return true;
        }

        /// <summary>창별 최소 할당 바이트.</summary>
        internal readonly long MinBytes => _min;

        /// <summary>실행된 창 수.</summary>
        internal readonly int Windows => _windows;
    }
}
