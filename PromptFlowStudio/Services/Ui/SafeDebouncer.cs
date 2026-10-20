using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlow Studio v13.0.1] 크래시 안전 디바운서/지연 실행기 — Blazor 컴포넌트의 "N ms 뒤 한 번 실행" 패턴 전용(순수 BCL).
///   이전 구현(<c>new Timer(async _ =&gt; await Work())</c>)은 TimerCallback(void) 에 붙은 async void 람다라, 작업 예외나
///   컴포넌트 파기 후 렌더러 종료 경합의 예외가 스레드풀로 재던져져 앱 프로세스가 종료됐다. 또 Dispose 를 구현하지 않은
///   컴포넌트는 타이머 핸들과 콜백 클로저(=파기된 컴포넌트)를 붙잡았다. 이 클래스가 두 결함을 한 곳에서 봉인한다.
///   ▸ 예외 격리: 작업은 Task 로 실행되고 모든 예외를 포착해 <c>onError</c> 로만 전달(onError 자체 예외도 흡수) - async void 0.
///   ▸ 원자적 대체: 새 <see cref="Schedule"/> 는 이전 예약의 CancellationTokenSource 를 Interlocked 교체 후 취소 → 마지막 요청만 실행.
///   ▸ 파기 안전: <see cref="Dispose"/> 후 예약은 무시, 대기 중 작업은 취소. 작업에는 취소 토큰이 전달돼 긴 작업도 협조 중단 가능.
///   ▸ 스레드 안전: 어느 스레드에서 Schedule/Dispose 해도 된다(UI 디스패처 마샬링은 작업 쪽 InvokeAsync 책임).
/// </summary>
public sealed class SafeDebouncer : IDisposable
{
    private readonly Action<Exception>? _onError;
    private CancellationTokenSource? _current;
    private int _disposed;
    private long _scheduled, _executed, _superseded, _faulted;

    public SafeDebouncer(Action<Exception>? onError = null) => _onError = onError;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public long Scheduled => Interlocked.Read(ref _scheduled);
    public long Executed => Interlocked.Read(ref _executed);
    /// <summary>후속 예약·Dispose 로 실행 전에 취소된 횟수.</summary>
    public long Superseded => Interlocked.Read(ref _superseded);
    public long Faulted => Interlocked.Read(ref _faulted);

    /// <summary><paramref name="delay"/> 뒤 <paramref name="work"/> 를 1회 실행(그 전에 다시 호출되면 이전 예약은 취소). 파기 후엔 무시.
    ///   반환 Task 는 관측용(테스트)이며 예외로 끝나지 않는다 - 호출측이 무시해도 미관측 예외가 생기지 않는다.</summary>
    public Task Schedule(TimeSpan delay, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsDisposed) return Task.CompletedTask;

        var cts = new CancellationTokenSource();
        var ct = cts.Token;   // 교체·파기 경합 전에 토큰 확보
        var previous = Interlocked.Exchange(ref _current, cts);
        if (previous is not null) CancelQuietly(previous);   // 해제는 소유자(RunAsync finally)만 — 등록 중 토큰의 CTS 를 타 스레드가 Dispose 하면 취소 콜백 유실

        if (IsDisposed)   // Schedule 와 Dispose 가 교차한 경우 - 방금 넣은 예약도 즉시 취소
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, null, cts), cts)) CancelQuietly(cts);
            cts.Dispose();   // RunAsync 미시작 → 이 토큰을 등록 중인 대기자 없음
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _scheduled);
        return RunAsync(delay, work, cts, ct);
    }

    private async Task RunAsync(TimeSpan delay, Func<CancellationToken, Task> work, CancellationTokenSource cts, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            await work(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _executed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _superseded);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _faulted);
            try { _onError?.Invoke(ex); } catch { /* 오류 처리기 자체 예외도 프로세스로 새지 않게 */ }
        }
        finally
        {
            // 아직 현재 예약이면 슬롯을 비우고, CTS 해제는 항상 여기서(대기 종료 후라 등록 경합 없음 · 대체한 쪽은 취소만)
            Interlocked.CompareExchange(ref _current, null, cts);
            cts.Dispose();
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* 이미 완료·해제 */ }
    }

    /// <summary>대기 중인 예약만 취소(이후 Schedule 은 계속 동작 · 중복 호출 무해). 영구 중단은 <see cref="Dispose"/>.</summary>
    public void Cancel()
    {
        var c = Interlocked.Exchange(ref _current, null);
        if (c is not null) CancelQuietly(c);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
    }
}
