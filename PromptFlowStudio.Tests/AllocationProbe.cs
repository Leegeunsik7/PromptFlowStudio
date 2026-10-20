using System;

namespace PromptFlowStudio.Tests;

/// <summary>Zero-Alloc 판정 보조 - 측정 창 여러 개 중 최솟값을 돌려준다.
///   실제로 할당하는 핫패스는 매 창마다 할당하므로 최솟값도 0 보다 크다(회귀를 여전히 잡음). 반면 CPU 부하 시 런타임이 측정 스레드에서
///   1회성으로 만드는 객체(티어드 컴파일 백그라운드 워커 기동 등, 관측치 128~7904B)는 한 창에만 섞이므로 최솟값에서 걸러진다.
///   첫 창이 0 이면 추가 창 없이 즉시 반환.</summary>
internal static class AllocationProbe
{
    public static long MinAllocated(Action body, int windows = 3)
    {
        var min = long.MaxValue;
        for (var w = 0; w < windows && min != 0; w++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            body();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return min;
    }
}