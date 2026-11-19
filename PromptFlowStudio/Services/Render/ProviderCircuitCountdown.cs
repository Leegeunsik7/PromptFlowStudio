using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// ⏱️ 회로 차단 실시간 카운트다운 구동기(순수 BCL · 스레드 안전 · 자가 종료).
///   ▸ <see cref="ProviderCircuitBreaker.StateChanged"/> 를 구독해, 닫히지 않은(Open · HalfOpen) 회로가 하나라도 있을 때만 <see cref="PeriodicTimer"/>(기본 1초)를 돌린다.
///   ▸ 틱마다 <see cref="ProviderCircuitBreaker.PromoteExpired"/> — 냉각이 끝난 Open 회로를 호출 없이도 HalfOpen 으로 전이.
///   ▸ 남은 냉각이 줄어드는 동안(Open 존재) 또는 전이가 일어난 틱에만 <c>onTick</c> 통지 — HalfOpen 대기 중에는 UI 를 깨우지 않는다.
///   ▸ 모든 회로가 Closed 로 돌아오면 타이머를 즉시 정지·폐기(정상 상태 백그라운드 비용 0) · 다시 차단되면 새 타이머로 재시작.
///   ▸ 정지는 <see cref="PeriodicTimer.Dispose"/>(대기 중인 틱이 false 로 완료) — 별도 CTS 없이 결정론적으로 끝난다.
/// </summary>
public sealed class ProviderCircuitCountdown : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ProviderCircuitBreaker _breaker;
    private readonly Action<IReadOnlyList<ProviderCircuitSnapshot>>? _onTick;
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;
    private int _timerStarts, _tickCount, _notifyCount;

    /// <param name="breaker">공유 차단기(엔진과 같은 인스턴스).</param>
    /// <param name="onTick">틱 통지(스레드 풀 스레드 · 예외 격리) — 인자 = 닫히지 않은 회로 스냅샷(남은 냉각 최신값).</param>
    /// <param name="interval">틱 간격(null → 1초).</param>
    public ProviderCircuitCountdown(ProviderCircuitBreaker breaker, Action<IReadOnlyList<ProviderCircuitSnapshot>>? onTick = null, TimeSpan? interval = null)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _onTick = onTick;
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
        _breaker.StateChanged += OnStateChanged;
        Evaluate();   // 이미 차단 중인 회로가 있으면 즉시 구동
    }

    /// <summary>타이머가 살아 있는가(모든 회로 Closed → false).</summary>
    public bool IsRunning { get { lock (_sync) return _timer is not null; } }
    /// <summary>타이머 생성 횟수(차단 구간 1회당 1).</summary>
    public int TimerStarts => Volatile.Read(ref _timerStarts);
    /// <summary>누적 틱 수.</summary>
    public int TickCount => Volatile.Read(ref _tickCount);
    /// <summary>누적 <c>onTick</c> 통지 수(HalfOpen 대기 틱은 제외).</summary>
    public int NotifyCount => Volatile.Read(ref _notifyCount);

    private void OnStateChanged() => Evaluate();

    private void Evaluate()
    {
        PeriodicTimer? stop = null;
        lock (_sync)
        {
            if (_disposed) return;
            var tripped = _breaker.HasTrippedCircuits;
            if (tripped && _timer is null)
            {
                var timer = new PeriodicTimer(_interval);
                _timer = timer;
                Interlocked.Increment(ref _timerStarts);
                _loop = Task.Run(() => RunAsync(timer));
            }
            else if (!tripped && _timer is not null)
            {
                stop = _timer;
                _timer = null;
            }
        }
        stop?.Dispose();   // 잠금 밖 — 대기 중인 WaitForNextTickAsync 가 false 로 완료
    }

    private async Task RunAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                Interlocked.Increment(ref _tickCount);
                var promoted = 0;
                var notify = false;
                IReadOnlyList<ProviderCircuitSnapshot> rows = Array.Empty<ProviderCircuitSnapshot>();
                try
                {
                    promoted = _breaker.PromoteExpired();   // 전이 시 StateChanged → Evaluate(재진입 안전)
                    rows = _breaker.GetTrippedSnapshots();
                    notify = promoted > 0;
                    for (var i = 0; i < rows.Count && !notify; i++) notify = rows[i].State == ProviderCircuitState.Open;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Render.CircuitCountdown] tick: " + ex.Message); }

                lock (_sync)
                {
                    if (_disposed || !ReferenceEquals(_timer, timer)) return;   // 폐기 · 세대 교체
                    if (!_breaker.HasTrippedCircuits) { _timer = null; return; }   // 전부 Closed → 자가 종료(잠금 안 판정 → 재차단 경합 없음)
                }
                if (notify && _onTick is { } cb)
                {
                    Interlocked.Increment(ref _notifyCount);
                    try { cb(rows); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Render.CircuitCountdown] onTick: " + ex.Message); }
                }
            }
        }
        catch (ObjectDisposedException) { }   // 대기 중 폐기 경합
        finally { timer.Dispose(); }
    }

    private Task Stop()
    {
        PeriodicTimer? stop;
        Task loop;
        lock (_sync)
        {
            if (_disposed) return _loop;
            _disposed = true;
            stop = _timer;
            _timer = null;
            loop = _loop;
        }
        _breaker.StateChanged -= OnStateChanged;
        stop?.Dispose();
        return loop;
    }

    /// <summary>구독 해제 + 타이머 폐기(멱등 · 진행 중인 틱은 기다리지 않음 — 폐기 후 통지 없음).</summary>
    public void Dispose() => _ = Stop();

    /// <summary>구독 해제 + 타이머 폐기 후 루프 종료까지 대기(멱등).</summary>
    public async ValueTask DisposeAsync()
    {
        try { await Stop().ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Render.CircuitCountdown] dispose: " + ex.Message); }
    }
}
