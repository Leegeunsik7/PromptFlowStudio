using System;
using System.Collections.Generic;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// Step 41 — <see cref="SteadyStateAllocation"/> 계약 잠금(TitanReleaseTests 무할당 단언 전량 이관의 토대).
///   ① 1회성 런타임 전이는 재시도 창에서 흡수 ② 진짜 정상 상태 할당은 매 창 재현 → 4 창 모두 실패 ③ 창 예외는 재시도 없이 전파
///   ④ 소비형 창의 공허한 통과(vacuous pass) 차단 — 재시도 직전 복원 콜백 ⑤ 람다 캡처 불가(stackalloc Span) 창용 무람다 Probe
///   ⑥ 강제 전 창 감사 모드(<see cref="SteadyStateAllocation.ForceAllWindowsEnv"/>)의 멱등성 검출력.
/// 스레드 국소 할당 측정이므로 ProcessGcMode(DisableParallelization)에서 단독 실행.
/// </summary>
[Collection("ProcessGcMode")]
public sealed class SteadyStateAllocationTests
{
    private static object? _sink;

    [Fact]
    public void GenuineSteadyStateAllocation_FailsOnEveryWindow_AndReportsTheSmallestSample()
    {
        var calls = 0;
        Action window = () => { calls++; _sink = new byte[256]; };

        var min = SteadyStateAllocation.MinBytes(window, out var windows);

        Assert.Equal(SteadyStateAllocation.MaxWindows, windows);   // 0 B 창이 없으므로 상한까지 전부 측정
        Assert.Equal(SteadyStateAllocation.MaxWindows, calls);
        Assert.True(min >= 256, "진짜 할당은 최소값에서도 보여야 함: " + min);
        Assert.NotEqual(0, SteadyStateAllocation.MinBytes(window));   // Assert.Equal(0, …) 단언은 그대로 실패
    }

    [Fact]
    public void OneShotTransientAllocation_IsAbsorbedByARetryWindow()
    {
        var calls = 0;
        Action window = () => { if (calls++ == 0) _sink = new byte[4688]; };   // Step 39 관측 1회성 4,688 B 모사

        var min = SteadyStateAllocation.MinBytes(window, out var windows);

        Assert.Equal(0, min);
        Assert.InRange(windows, 2, SteadyStateAllocation.MaxWindows);
        Assert.Equal(windows, calls);
    }

    [Fact]
    public void Sample_RecordsEveryWindow_StopsAtFirstZero_UnlessForced()
    {
        var calls = 0;
        Action window = () => { if (calls++ == 0) _sink = new byte[128]; };
        Span<long> samples = stackalloc long[SteadyStateAllocation.MaxWindows];

        var n = SteadyStateAllocation.Sample(window, null, forceAll: false, samples);
        Assert.InRange(n, 2, SteadyStateAllocation.MaxWindows);
        Assert.True(samples[0] >= 128);
        Assert.Equal(0, samples[n - 1]);

        calls = 1;   // 이후 창은 모두 무할당
        var forced = SteadyStateAllocation.Sample(window, null, forceAll: true, samples);
        Assert.Equal(SteadyStateAllocation.MaxWindows, forced);   // 감사 모드: 0 B 여도 전 창 실행
        Assert.Equal(0, SteadyStateAllocation.Min(samples[..forced]));

        Assert.Throws<ArgumentException>(() => SteadyStateAllocation.Sample(window, null, false, Span<long>.Empty));
        Assert.Throws<ArgumentNullException>(() => SteadyStateAllocation.MinBytes(null!));
    }

    [Fact]
    public void WindowException_PropagatesImmediately_WithoutRetry()
    {
        var calls = 0;
        var ex = Assert.Throws<InvalidOperationException>(() => SteadyStateAllocation.MinBytes(() => { calls++; throw new InvalidOperationException("boom"); }));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(1, calls);   // 실패를 재시도로 가리지 않음
    }

    /// <summary>
    /// 자체 추가 ① — 소비형 창(파이프라인 완주 · 채널 드레인 · 제자리 변환)은 재시도 창이 "할 일이 없어서" 0 B 를 보고한다.
    /// 첫 창의 진짜 할당이 가려지는 공허한 통과 → 복원 콜백(측정 창 밖)으로 매 재시도에 동일 작업량을 되돌려야 회귀가 잡힌다.
    /// </summary>
    [Fact]
    public void CoArchitect_ConsumingWindow_WithoutReset_IsAVacuousPass_ResetRestoresDetection()
    {
        // 큐를 비우며 원소마다 할당하는 "누수" 창 — 재시도 때 큐가 비어 있으면 0 B 로 통과해 버린다
        var queue = new Queue<int>();
        void Refill() { queue.Clear(); for (var i = 0; i < 8; i++) queue.Enqueue(i); }
        Action leakyDrain = () => { while (queue.TryDequeue(out var v)) _sink = new int[v + 16]; };

        Refill();
        Assert.Equal(0, SteadyStateAllocation.MinBytes(leakyDrain));   // ❌ 공허한 통과: 진짜 누수를 놓침

        Refill();
        var resets = 0;
        var min = SteadyStateAllocation.MinBytes(leakyDrain, () => { resets++; Refill(); }, out var windows);
        Assert.True(min > 0, "복원 후에는 매 창 누수가 재현되어야 함");                 // ✅ 회귀 검출
        Assert.Equal(SteadyStateAllocation.MaxWindows, windows);
        Assert.Equal(windows - 1, resets);                                              // 복원은 재시도 직전에만(첫 창 전 없음)
        Assert.Empty(queue);
    }

    [Fact]
    public void CoArchitect_ResetCallbackAllocations_AreNeverCountedInsideTheMeasuredWindow()
    {
        var work = new float[64];
        var calls = 0;
        Action window = () => { if (calls++ == 0) _sink = new byte[512]; for (var i = 0; i < work.Length; i++) work[i] *= 0.5f; };

        var min = SteadyStateAllocation.MinBytes(window, () => { _sink = new byte[64 * 1024]; Array.Fill(work, 1f); }, out var windows);

        Assert.Equal(0, min);   // 복원 콜백의 64 KiB 는 창 밖
        Assert.InRange(windows, 2, SteadyStateAllocation.MaxWindows);
        Assert.Equal(0.5f, work[0]);   // 마지막 창 = 복원된 입력에 대한 단일 처리(누적 반감 없음)
    }

    /// <summary>
    /// 자체 추가 ② — 창이 상태를 전진(시퀀스 · 구독 통지 · 소비 카운터)시키면 재시도 횟수만큼 후속 단언이 바뀐다.
    /// <c>out windows</c> 로 "1 + 창 수" 처럼 엄격 단언을 유지한다(단독 실행 1 창 · 전체 실행 재시도 N 창 모두 통과).
    /// </summary>
    [Fact]
    public void CoArchitect_StatefulWindow_PostAssertionsScaleWithExecutedWindowCount()
    {
        var sequence = 1;
        var calls = 0;
        Action handoff = () => { sequence++; if (calls++ < 2) _sink = new byte[64]; };

        var min = SteadyStateAllocation.MinBytes(handoff, out var windows);

        Assert.Equal(0, min);
        Assert.InRange(windows, 3, SteadyStateAllocation.MaxWindows);
        Assert.Equal(1 + windows, sequence);   // 고정 "2" 단언이었다면 재시도 시 거짓 실패
    }

    /// <summary>자체 추가 ③ — stackalloc Span · ref 로컬은 람다가 캡처할 수 없다 → 무람다 Probe 가 같은 판정을 한다.</summary>
    [Fact]
    public void CoArchitect_LambdaFreeProbe_MatchesMinBytesSemantics_ForSpanWindows()
    {
        Span<float> scratch = stackalloc float[32];
        var calls = 0;
        var probe = SteadyStateAllocation.Probe.Begin(forceAll: false);
        do
        {
            if (calls++ == 0) _sink = new byte[1024];   // 1회성 전이
            for (var i = 0; i < scratch.Length; i++) scratch[i] = i;
        } while (probe.Retry());
        Assert.Equal(0, probe.MinBytes);
        Assert.InRange(probe.Windows, 2, SteadyStateAllocation.MaxWindows);

        calls = 0;
        var leak = SteadyStateAllocation.Probe.Begin(forceAll: false);
        do { calls++; _sink = new byte[96]; scratch[0] = calls; } while (leak.Retry());
        Assert.True(leak.MinBytes >= 96);
        Assert.Equal(SteadyStateAllocation.MaxWindows, leak.Windows);
        Assert.Equal(SteadyStateAllocation.MaxWindows, calls);

        var forced = SteadyStateAllocation.Probe.Begin(forceAll: true);
        var n = 0;
        do { n++; } while (forced.Retry());
        Assert.Equal(SteadyStateAllocation.MaxWindows, n);
        Assert.Equal(0, forced.MinBytes);
    }

    [Fact]
    public void ForceAllWindowsSwitch_IsReadOnceFromTheDocumentedEnvironmentVariable()
    {
        Assert.Equal("PFS_ALLOC_FORCE_ALL_WINDOWS", SteadyStateAllocation.ForceAllWindowsEnv);
        Assert.Equal(Environment.GetEnvironmentVariable(SteadyStateAllocation.ForceAllWindowsEnv) == "1", SteadyStateAllocation.ForceAllWindows);
    }
}
