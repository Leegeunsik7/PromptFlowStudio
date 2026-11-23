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
