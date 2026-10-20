using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using PromptFlowStudio.Services.Mobile;

namespace PromptFlowStudio.Services.Integration;

/// <summary>[v9.1.1] 인식된 제스처 종류.</summary>
public enum MauiGestureKind
{
    None,
    Tap,
    LongPress,
    Drag,
    Pinch,
    Swipe,
}

/// <summary>[v9.1.1] 스와이프 방향(뷰 상태 머신 전환의 입력 신호).</summary>
public enum MauiSwipeDirection
{
    None,
    Left,
    Right,
    Up,
    Down,
}

/// <summary>[v9.1.1] 단일 포인터 접점(디바이스 독립 논리 좌표 · ts 는 단조 증가 밀리초).</summary>
public readonly record struct MauiTouchPoint(int PointerId, double X, double Y, long TimestampMs);

/// <summary>
/// [v9.1.1] 제스처 인식 결과 1건.
///   ▸ <c>Scale</c> 은 핀치 누적 배율(1.0 = 원본), 그 외 제스처에서는 현재 배율을 그대로 싣는다.
/// </summary>
public sealed record MauiGestureEvent(
    MauiGestureKind Kind,
    MauiSwipeDirection Direction,
    double DeltaX,
    double DeltaY,
    double Scale,
    double VelocityPerSec,
    long DurationMs,
    DateTimeOffset At)
{
    /// <summary>HUD/로그용 한 줄 요약(InvariantCulture 고정 — 로캘에 따라 소수점이 흔들리면 안 된다).</summary>
    public string Summary => Kind switch
    {
        MauiGestureKind.Pinch => "핀치 x" + Scale.ToString("0.00", CultureInfo.InvariantCulture),
        MauiGestureKind.Swipe => "스와이프 " + Direction.ToString(),
        MauiGestureKind.Drag => "드래그 " + DeltaX.ToString("0", CultureInfo.InvariantCulture) + "," + DeltaY.ToString("0", CultureInfo.InvariantCulture),
        MauiGestureKind.LongPress => "롱프레스",
        MauiGestureKind.Tap => "탭",
        _ => "-",
    };
}

/// <summary>
/// [PromptFlow Studio v9.1.1] MAUI 모바일 제스처 엔진 — 순수 BCL 터치 인식기.
///   ▸ 터치 드래그 / 핀치 줌 / 스와이프를 포인터 이벤트만으로 인식한다(네이티브 제스처 API 무의존 → 데스크톱 시뮬레이터에서도 동일 동작).
///   ▸ 핀치 배율을 타임라인 트랙 줌(px/sec)에 연동하고, 좌우 스와이프를 <see cref="MobileInternalView"/> 상태 머신 전환에 연동한다.
///   ▸ 헤드(MAUI/시뮬레이터)는 포인터 콜백만 밀어 넣고 결과 이벤트만 구독한다 — 플랫폼 종속 타입을 일절 노출하지 않는다.
/// </summary>
public sealed class MauiMobileGestureEngine : IDisposable
{
    // ── 인식 임계값(논리 픽셀 · 밀리초) ──
    private const double TapMaxMove = 12d;
    private const long TapMaxMs = 260L;
    private const long LongPressMs = 620L;
    private const double SwipeMinDistance = 60d;
    private const long SwipeMaxMs = 700L;
    private const double DragMinMove = 6d;
    private const double MinZoom = 0.25d;
    private const double MaxZoom = 8d;
    private const double MinPixelsPerSecond = 8d;
    private const double MaxPixelsPerSecond = 480d;
    private const double DefaultPixelsPerSecond = 60d;
    private const int MaxPointers = 4;

    private readonly object _sync = new();

    // 포인터 슬롯은 고정 배열(최대 4) — 터치마다 딕셔너리를 할당하지 않는다(Zero-Alloc 핫패스).
    private readonly int[] _ids = new int[MaxPointers];
    private readonly double[] _startX = new double[MaxPointers];
    private readonly double[] _startY = new double[MaxPointers];
    private readonly double[] _curX = new double[MaxPointers];
    private readonly double[] _curY = new double[MaxPointers];
    private readonly long[] _startTs = new long[MaxPointers];
    private readonly bool[] _used = new bool[MaxPointers];

    private double _pinchStartDistance;
    private double _pinchBaseScale = 1d;
    private double _pinchBasePixelsPerSecond = DefaultPixelsPerSecond;
    private bool _disposed;

    private int _tapCount;
    private int _dragCount;
    private int _pinchCount;
    private int _swipeCount;
    private int _longPressCount;

    // 스와이프 링 순서(드로어 순서와 동일 · 상태 머신 전환 축).
    private MobileInternalView[] _ring =
    {
        MobileInternalView.Workspace,
        MobileInternalView.Storyboard,
        MobileInternalView.Prompt,
        MobileInternalView.Flow,
        MobileInternalView.Gallery,
        MobileInternalView.Settings,
    };

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>제스처 인식 1건 통지.</summary>
    public event Action<MauiGestureEvent>? OnGesture;

    /// <summary>스와이프/명시 호출로 내부 뷰가 전환됐을 때 통지.</summary>
    public event Action<MobileInternalView>? OnViewChanged;

    /// <summary>현재 눌린 포인터 수.</summary>
    public int ActivePointers { get; private set; }

    /// <summary>두 손가락 핀치 진행 중.</summary>
    public bool IsPinching { get; private set; }

    /// <summary>한 손가락 드래그 진행 중.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>핀치 누적 배율(0.25~8.0 클램프).</summary>
    public double ZoomScale { get; private set; } = 1d;

    /// <summary>타임라인 트랙 줌(초당 픽셀) — 핀치와 연동될 때만 변한다.</summary>
    public double TimelinePixelsPerSecond { get; private set; } = DefaultPixelsPerSecond;

    /// <summary>드래그 누적 패닝 오프셋(초). 음수로 내려가지 않는다.</summary>
    public double PanOffsetSeconds { get; private set; }

    /// <summary>핀치를 타임라인 줌에 연동할지 여부(기본 켬).</summary>
    public bool PinchDrivesTimeline { get; set; } = true;

    /// <summary>좌우 스와이프를 내부 뷰 전환에 연동할지 여부(기본 켬).</summary>
    public bool SwipeDrivesNavigation { get; set; } = true;

    /// <summary>현재 내부 뷰(모바일 상태 머신의 단일 진실).</summary>
    public MobileInternalView CurrentView { get; private set; } = MobileInternalView.Workspace;

    /// <summary>직전 뷰(뒤로가기 1단계 복원용).</summary>
    public MobileInternalView PreviousView { get; private set; } = MobileInternalView.Workspace;

    /// <summary>마지막 인식 제스처(없으면 null).</summary>
    public MauiGestureEvent? LastGesture { get; private set; }

    /// <summary>마지막 제스처 시각.</summary>
    public DateTimeOffset? LastGestureAt { get; private set; }

    public int TapCount => Volatile.Read(ref _tapCount);

    public int DragCount => Volatile.Read(ref _dragCount);

    public int PinchCount => Volatile.Read(ref _pinchCount);

    public int SwipeCount => Volatile.Read(ref _swipeCount);

    public int LongPressCount => Volatile.Read(ref _longPressCount);

    /// <summary>총 인식 건수(최종 판정만 집계).</summary>
    public int GestureCount => TapCount + DragCount + PinchCount + SwipeCount + LongPressCount;

    /// <summary>스와이프 전환 링(읽기 전용 스냅샷).</summary>
    public IReadOnlyList<MobileInternalView> SwipeRing
    {
        get { lock (_sync) { return (MobileInternalView[])_ring.Clone(); } }
    }

    /// <summary>스와이프 전환 순서를 헤드의 드로어 순서와 맞춘다(빈 목록은 무시 — 기본 링 유지).</summary>
    public void ConfigureSwipeRing(IReadOnlyList<MobileInternalView>? order)
    {
        if (order is null || order.Count == 0) return;

        var next = new MobileInternalView[order.Count];
        for (var i = 0; i < order.Count; i++) next[i] = order[i];

        lock (_sync) { _ring = next; }
        Notify();
    }

    /// <summary>포인터 누름. 두 번째 포인터가 들어오면 핀치 기준선을 잡는다.</summary>
    public void PointerDown(int pointerId, double x, double y, long timestampMs)
    {
        if (_disposed) return;

        lock (_sync)
        {
            var slot = FindSlot(pointerId);
            if (slot < 0) slot = FindFreeSlot();
            if (slot < 0) return;   // 5번째 손가락은 무시(Graceful — 예외 없음)

            _ids[slot] = pointerId;
            _startX[slot] = _curX[slot] = x;
            _startY[slot] = _curY[slot] = y;
            _startTs[slot] = timestampMs;
            _used[slot] = true;
            ActivePointers = CountActive();

            if (ActivePointers >= 2)
            {
                _pinchStartDistance = CurrentPinchDistance();
                _pinchBaseScale = ZoomScale;
                _pinchBasePixelsPerSecond = TimelinePixelsPerSecond;
                IsPinching = _pinchStartDistance > 0d;
                IsDragging = false;
            }
        }

        Notify();
    }

    /// <summary>포인터 이동. 핀치 중이면 배율을, 단일 포인터면 드래그 델타를 반환한다.</summary>
    public MauiGestureEvent? PointerMove(int pointerId, double x, double y, long timestampMs)
    {
        if (_disposed) return null;

        MauiGestureEvent? evt = null;

        lock (_sync)
        {
            var slot = FindSlot(pointerId);
            if (slot < 0) return null;   // 등록되지 않은 포인터는 조용히 무시

            _curX[slot] = x;
            _curY[slot] = y;

            if (IsPinching && ActivePointers >= 2)
            {
                var dist = CurrentPinchDistance();
                if (_pinchStartDistance <= 0.001d || dist <= 0.001d) return null;

                var ratio = dist / _pinchStartDistance;
                ZoomScale = Clamp(_pinchBaseScale * ratio, MinZoom, MaxZoom);

                if (PinchDrivesTimeline)
                    TimelinePixelsPerSecond = Clamp(_pinchBasePixelsPerSecond * ratio, MinPixelsPerSecond, MaxPixelsPerSecond);

                evt = new MauiGestureEvent(MauiGestureKind.Pinch, MauiSwipeDirection.None,
                    0d, 0d, ZoomScale, 0d, Math.Max(0L, timestampMs - _startTs[slot]), DateTimeOffset.UtcNow);
            }
            else if (ActivePointers == 1)
            {
                var dx = x - _startX[slot];
                var dy = y - _startY[slot];
                if (Math.Abs(dx) < DragMinMove && Math.Abs(dy) < DragMinMove) return null;

                IsDragging = true;
                var elapsed = Math.Max(1L, timestampMs - _startTs[slot]);
                var velocity = Math.Sqrt((dx * dx) + (dy * dy)) / (elapsed / 1000d);

                // 가로 드래그는 타임라인 패닝으로 환산(줌이 클수록 같은 픽셀이 더 짧은 시간이 된다).
                if (PinchDrivesTimeline && TimelinePixelsPerSecond > 0.001d)
                    PanOffsetSeconds = Math.Max(0d, PanOffsetSeconds - (dx / TimelinePixelsPerSecond));

                evt = new MauiGestureEvent(MauiGestureKind.Drag, MauiSwipeDirection.None,
                    dx, dy, ZoomScale, velocity, elapsed, DateTimeOffset.UtcNow);
            }
        }

        if (evt is not null) Publish(evt);
        return evt;
    }

    /// <summary>포인터 뗌. 탭/롱프레스/스와이프/드래그를 최종 판정하고 필요 시 뷰를 전환한다.</summary>
    public MauiGestureEvent? PointerUp(int pointerId, double x, double y, long timestampMs)
    {
        if (_disposed) return null;

        MauiGestureEvent? evt = null;
        var navigated = false;
        MobileInternalView target;

        lock (_sync)
        {
            var slot = FindSlot(pointerId);
            if (slot < 0) return null;

            var dx = x - _startX[slot];
            var dy = y - _startY[slot];
            var elapsed = Math.Max(1L, timestampMs - _startTs[slot]);
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var wasPinching = IsPinching;

            _used[slot] = false;
            _ids[slot] = 0;
            ActivePointers = CountActive();
            if (ActivePointers < 2) IsPinching = false;
            if (ActivePointers == 0) IsDragging = false;

            if (wasPinching)
            {
                Interlocked.Increment(ref _pinchCount);
                evt = new MauiGestureEvent(MauiGestureKind.Pinch, MauiSwipeDirection.None,
                    0d, 0d, ZoomScale, 0d, elapsed, DateTimeOffset.UtcNow);
            }
            else if (distance >= SwipeMinDistance && elapsed <= SwipeMaxMs)
            {
                var dir = Math.Abs(dx) >= Math.Abs(dy)
                    ? (dx < 0 ? MauiSwipeDirection.Left : MauiSwipeDirection.Right)
                    : (dy < 0 ? MauiSwipeDirection.Up : MauiSwipeDirection.Down);

                Interlocked.Increment(ref _swipeCount);
                evt = new MauiGestureEvent(MauiGestureKind.Swipe, dir,
                    dx, dy, ZoomScale, distance / (elapsed / 1000d), elapsed, DateTimeOffset.UtcNow);

                if (SwipeDrivesNavigation && (dir == MauiSwipeDirection.Left || dir == MauiSwipeDirection.Right))
                    navigated = TryStep(dir == MauiSwipeDirection.Left ? 1 : -1);
            }
            else if (distance < TapMaxMove && elapsed >= LongPressMs)
            {
                Interlocked.Increment(ref _longPressCount);
                evt = new MauiGestureEvent(MauiGestureKind.LongPress, MauiSwipeDirection.None,
                    dx, dy, ZoomScale, 0d, elapsed, DateTimeOffset.UtcNow);
            }
            else if (distance < TapMaxMove && elapsed <= TapMaxMs)
            {
                Interlocked.Increment(ref _tapCount);
                evt = new MauiGestureEvent(MauiGestureKind.Tap, MauiSwipeDirection.None,
                    dx, dy, ZoomScale, 0d, elapsed, DateTimeOffset.UtcNow);
            }
            else if (distance >= DragMinMove)
            {
                Interlocked.Increment(ref _dragCount);
                evt = new MauiGestureEvent(MauiGestureKind.Drag, MauiSwipeDirection.None,
                    dx, dy, ZoomScale, distance / (elapsed / 1000d), elapsed, DateTimeOffset.UtcNow);
            }

            target = CurrentView;
        }

        if (evt is not null) Publish(evt);
        if (navigated) OnViewChanged?.Invoke(target);
        return evt;
    }

    /// <summary>포인터 취소(앱 전환·시스템 제스처 가로채기) — 누적 상태는 보존한 채 접점만 비운다.</summary>
    public void PointerCancel(int pointerId)
    {
        if (_disposed) return;

        lock (_sync)
        {
            var slot = FindSlot(pointerId);
            if (slot < 0) return;

            _used[slot] = false;
            _ids[slot] = 0;
            ActivePointers = CountActive();
            if (ActivePointers < 2) IsPinching = false;
            if (ActivePointers == 0) IsDragging = false;
        }

        Notify();
    }

    /// <summary>명시적 뷰 전환(드로어 탭 등). 같은 뷰면 아무 일도 하지 않는다(멱등).</summary>
    public bool NavigateTo(MobileInternalView view)
    {
        if (_disposed) return false;

        lock (_sync)
        {
            if (CurrentView == view) return false;
            PreviousView = CurrentView;
            CurrentView = view;
        }

        OnViewChanged?.Invoke(view);
        Notify();
        return true;
    }

    /// <summary>직전 뷰로 1단계 복원.</summary>
    public bool NavigateBack() => NavigateTo(PreviousView);

    /// <summary>타임라인 줌을 직접 설정(트랙 헤더 슬라이더 연동용).</summary>
    public void SetTimelineZoom(double pixelsPerSecond)
    {
        if (_disposed) return;

        lock (_sync) { TimelinePixelsPerSecond = Clamp(pixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond); }
        Notify();
    }

    /// <summary>줌/패닝만 원복(제스처 카운터와 현재 뷰는 보존 — 제5헌장 세션 보존).</summary>
    public void ResetTransform()
    {
        if (_disposed) return;

        lock (_sync)
        {
            ZoomScale = 1d;
            TimelinePixelsPerSecond = DefaultPixelsPerSecond;
            PanOffsetSeconds = 0d;
            _pinchBaseScale = 1d;
            _pinchBasePixelsPerSecond = DefaultPixelsPerSecond;
            _pinchStartDistance = 0d;
        }

        Notify();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync)
        {
            for (var i = 0; i < MaxPointers; i++) { _used[i] = false; _ids[i] = 0; }
            ActivePointers = 0;
            IsPinching = false;
            IsDragging = false;
        }

        OnChange = null;
        OnGesture = null;
        OnViewChanged = null;
    }

    // ── 내부 유틸 ──

    private void Publish(MauiGestureEvent evt)
    {
        LastGesture = evt;
        LastGestureAt = evt.At;
        OnGesture?.Invoke(evt);
        Notify();
    }

    private void Notify() => OnChange?.Invoke();

    private int FindSlot(int pointerId)
    {
        for (var i = 0; i < MaxPointers; i++)
            if (_used[i] && _ids[i] == pointerId) return i;
        return -1;
    }

    private int FindFreeSlot()
    {
        for (var i = 0; i < MaxPointers; i++)
            if (!_used[i]) return i;
        return -1;
    }

    private int CountActive()
    {
        var n = 0;
        for (var i = 0; i < MaxPointers; i++)
            if (_used[i]) n++;
        return n;
    }

    private double CurrentPinchDistance()
    {
        var first = -1;
        for (var i = 0; i < MaxPointers; i++)
        {
            if (!_used[i]) continue;
            if (first < 0) { first = i; continue; }

            var dx = _curX[i] - _curX[first];
            var dy = _curY[i] - _curY[first];
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        return 0d;
    }

    /// <summary>링을 따라 뷰를 한 칸 이동(호출자가 이미 lock 보유).</summary>
    private bool TryStep(int step)
    {
        var ring = _ring;
        if (ring.Length == 0) return false;

        var idx = Array.IndexOf(ring, CurrentView);
        if (idx < 0) idx = 0;

        var next = ring[(((idx + step) % ring.Length) + ring.Length) % ring.Length];
        if (next == CurrentView) return false;

        PreviousView = CurrentView;
        CurrentView = next;
        return true;
    }

    private static double Clamp(double v, double lo, double hi)
        => double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v));
}
