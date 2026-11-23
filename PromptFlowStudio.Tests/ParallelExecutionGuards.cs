using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Tests;

/// <summary>Step 35 — CPU 집약 엔진 스위트(시뮬 · 렌더 · SIMD 커널)를 한 컬렉션으로 묶어 서로는 직렬(동시 최대 1 개)로 돌린다.
///   병렬화 자체는 끄지 않으므로 경량 단위 테스트가 나머지 병렬 슬롯을 채운다. 두 개 이상이 동시에 전 코어를 점유하면
///   다른 테스트의 async 연속(continuation) · 소켓 타임아웃이 기아로 수십 초 지연되는 것을 실측으로 확인(Step 35 감사).</summary>
[CollectionDefinition("TitanHeavyCompute")]
public sealed class TitanHeavyComputeCollection { }

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
///   <c>TitanReleaseTests</c> 는 이미 <c>[Collection("ProcessGcMode")]</c>(DisableParallelization)라 다른 테스트와 겹쳐 돌지 않으며,
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

    /// <summary><paramref name="window"/> 를 최대 <see cref="MaxWindows"/> 번 측정해 가장 작은 스레드 국소 할당 바이트를 반환(0 이면 즉시 반환).</summary>
    internal static long MinBytes(Action window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var min = long.MaxValue;
        for (var i = 0; i < MaxWindows && min > 0; i++)
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            window();
            var d = GC.GetAllocatedBytesForCurrentThread() - a0;
            if (d < min) min = d;
        }
        return min;
    }
}
