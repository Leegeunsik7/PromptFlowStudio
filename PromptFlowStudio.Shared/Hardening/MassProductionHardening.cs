using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Shared.Hardening;

/// <summary>
/// [PromptFlow Studio v4.4.1] 광클(연속 클릭) 재진입 차단 게이트(thread-safe · SemaphoreSlim(1,1)).
///   이미 실행 중이면 대기 없이 즉시 무시(false) → 대량 양산 태스크 충돌/교착 방지.
/// </summary>
public sealed class SingleFlightGate : IDisposable
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    /// <summary>현재 실행 중인지.</summary>
    public bool IsBusy => _sem.CurrentCount == 0;

    /// <summary>이미 실행 중이면 <c>false</c> 반환(광클 무시), 아니면 작업 실행 후 <c>true</c>.</summary>
    public async Task<bool> RunAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        if (work is null) return false;
        if (!_sem.Wait(0)) return false;   // 대기 없이 즉시 실패 = 재진입(광클) 무시
        try { await work(ct).ConfigureAwait(false); return true; }
        finally { _sem.Release(); }
    }

    /// <summary>수동 진입 시도(성공 시 <see cref="Exit"/> 필수).</summary>
    public bool TryEnter() => _sem.Wait(0);
    /// <summary>수동 해제.</summary>
    public void Exit() { try { _sem.Release(); } catch (SemaphoreFullException) { } }

    public void Dispose() => _sem.Dispose();
}

/// <summary>
/// [PromptFlow Studio v4.4.1] 대량 양산 비동기 취소 하드닝(Task 3 · 순수 BCL).
///   ▸ 상위 토큰 + 타임아웃 링크로 CancellationToken 을 끝까지 전달 → 잔여 스레드/좀비 프로세스 소멸.
///   ▸ 취소는 예외가 아니라 정상 흐름(false)으로 처리.
/// </summary>
public static class MassProductionHardening
{
    /// <summary>상위 토큰 + 타임아웃을 링크한 CTS(호출부가 using 으로 해제 · FFmpeg/IO 에 .Token 전달).</summary>
    public static CancellationTokenSource LinkWithTimeout(CancellationToken outer, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        if (timeout > TimeSpan.Zero) cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>취소 안전 실행 — 취소 시 예외 전파 없이 <c>false</c>.</summary>
    public static async Task<bool> RunCancelable(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        try { await work(ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
