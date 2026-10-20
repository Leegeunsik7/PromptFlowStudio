using System;
using System.Threading;

namespace PromptFlowStudio.Shared.Hardening;

/// <summary>
/// [PromptFlow Studio v4.4.2] 프레임 틱 스로틀러(Task 2 · 순수 BCL · CPU 스파이크 방어).
///   고주파(60fps+) 이벤트가 몰아쳐도 최소 간격 미만이면 렌더를 <b>skip</b> → CPU 폭주 차단.
///   시간 소스(nowTicks)를 주입받아 결정적으로 동작(테스트 가능).
/// </summary>
public sealed class FrameThrottle
{
    private readonly long _minTicks;
    private long _lastTicks = long.MinValue;

    /// <summary>최대 렌더 FPS(1~240 clamp · NaN/0 은 60 으로 폴백).</summary>
    public FrameThrottle(double maxFps = 60.0)
    {
        var fps = SafeGeom.IsFinite(maxFps) && maxFps > 0 ? Math.Clamp(maxFps, 1.0, 240.0) : 60.0;
        _minTicks = (long)(TimeSpan.TicksPerSecond / fps);
    }

    /// <summary>이번 틱(nowTicks)을 렌더할지 — 최소 간격 미만이면 false(스로틀).</summary>
    public bool ShouldRender(long nowTicks)
    {
        if (_lastTicks != long.MinValue && nowTicks - _lastTicks < _minTicks) return false;
        _lastTicks = nowTicks;
        return true;
    }

    /// <summary>스로틀 상태 초기화.</summary>
    public void Reset() => _lastTicks = long.MinValue;

    /// <summary>최소 프레임 간격(ms).</summary>
    public double MinIntervalMs => _minTicks / (double)TimeSpan.TicksPerMillisecond;
}

/// <summary>
/// [PromptFlow Studio v4.4.2] 트레일링 디바운서(Task 2 · System.Threading.Timer · 안전 해제).
///   연속 입력 중에는 실행을 미루고 <b>조용해진 뒤 1회만</b> 실행 → 리사이즈/드래그 폭주 방어.
///   내부 Timer 를 매 트리거마다 안전 교체하고 Dispose 에서 확실히 해제(멱등).
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly object _lock = new();
    private readonly int _delayMs;
    private Timer? _timer;
    private bool _disposed;

    public Debouncer(int delayMs) => _delayMs = Math.Max(1, delayMs);

    /// <summary>실행 예약(직전 예약은 취소). Dispose 후에는 무시.</summary>
    public void Trigger(Action action)
    {
        if (action is null) return;
        lock (_lock)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = new Timer(_ => { try { action(); } catch { /* 콜백 예외 격리 */ } }, null, _delayMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v4.4.2] 모션 카메라 렌더 루프(Task 2 · System.Threading.Timer + CancellationToken).
///   ▸ 궤적 계산/보간 애니메이션을 주기 타이머로 구동하고, <see cref="Stop"/>/<see cref="Dispose"/>에서
///     <b>Timer 와 CTS 를 확실히 해제</b> → 백그라운드 스레드/타이머 누수 원천 봉쇄.
///   ▸ 이전 틱이 아직 실행 중이면 이번 틱을 <b>skip</b>(재진입 가드) → 누적 지연/CPU 스파이크 방어.
///   ▸ 틱 콜백 예외가 루프를 죽이지 않도록 격리.
/// </summary>
public sealed class MotionCameraLoop : IDisposable
{
    private readonly object _lock = new();
    private readonly long _periodMs;
    private readonly Action<long> _onTick;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private long _frame;
    private int _inTick;      // 0=idle, 1=running (재진입 가드)
    private bool _disposed;

    /// <summary>실행 중 여부.</summary>
    public bool IsRunning { get; private set; }
    /// <summary>누적 프레임 수.</summary>
    public long FrameCount => Interlocked.Read(ref _frame);

    /// <summary>틱 콜백(프레임 인덱스)과 목표 FPS(1~240).</summary>
    public MotionCameraLoop(Action<long> onTick, double fps = 60.0)
    {
        _onTick = onTick ?? (_ => { });
        var f = SafeGeom.IsFinite(fps) && fps > 0 ? Math.Clamp(fps, 1.0, 240.0) : 60.0;
        _periodMs = Math.Max(1, (long)(1000.0 / f));
    }

    /// <summary>루프 시작(멱등) — 해제됐거나 이미 실행 중이면 기존/취소 토큰 반환.</summary>
    public CancellationToken Start()
    {
        lock (_lock)
        {
            if (_disposed) return new CancellationToken(true);
            if (IsRunning) return _cts!.Token;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _timer = new Timer(Tick, null, _periodMs, _periodMs);
            return _cts.Token;
        }
    }

    private void Tick(object? _)
    {
        // 재진입 가드 — 직전 틱이 아직 돌고 있으면 이번 틱은 버린다(누적 방지).
        if (Interlocked.Exchange(ref _inTick, 1) == 1) return;
        try
        {
            var cts = _cts;
            if (cts is null || cts.IsCancellationRequested) return;
            var n = Interlocked.Increment(ref _frame);
            try { _onTick(n); } catch { /* 틱 예외 격리 — 루프 유지 */ }
        }
        finally { Interlocked.Exchange(ref _inTick, 0); }
    }

    /// <summary>루프 정지 — Timer/CTS 확실히 해제(멱등).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            _timer?.Dispose(); _timer = null;
            _cts?.Dispose(); _cts = null;
        }
    }

    public void Dispose()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        Stop();
    }
}

/// <summary>
/// [PromptFlow Studio v4.4.2] 카메라 궤적 보간 수학(Task 2 · 순수 BCL · NaN 방어).
///   모든 보간 입력/파라미터에 유한성 검증과 t clamp[0,1] 적용 → NaN 좌표/발산 궤적 방지.
/// </summary>
public static class TrajectoryMath
{
    /// <summary>선형 보간 — 비유한 입력은 0, t 는 [0,1] clamp.</summary>
    public static double Lerp(double a, double b, double t)
    {
        if (!SafeGeom.IsFinite(a)) a = 0;
        if (!SafeGeom.IsFinite(b)) b = 0;
        t = SafeGeom.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 0.0;
        return a + (b - a) * t;
    }

    /// <summary>ease-in-out(smoothstep) — t 는 [0,1] clamp, NaN 방어.</summary>
    public static double SmoothStep(double t)
    {
        t = SafeGeom.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 0.0;
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>3D 카메라 위치 보간(from→to) — ease 적용 · 결과는 항상 유한.</summary>
    public static (double X, double Y, double Z) Interpolate(
        (double X, double Y, double Z) from, (double X, double Y, double Z) to, double t)
    {
        var e = SmoothStep(t);
        return (Lerp(from.X, to.X, e), Lerp(from.Y, to.Y, e), Lerp(from.Z, to.Z, e));
    }
}
