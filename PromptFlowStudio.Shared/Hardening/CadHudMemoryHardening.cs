using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Shared.Hardening;

/// <summary>
/// [PromptFlow Studio v4.4.2] 좌표 안전 기하 유틸(순수 BCL · NaN/Infinity 완전 방어).
///   3D CAD/HUD/터치 연산의 모든 입력 좌표를 유한값으로 정규화 → NaN 전파/렌더 폭주 원천 차단.
/// </summary>
public static class SafeGeom
{
    /// <summary>유한 실수인지(NaN/±Infinity 아님).</summary>
    public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>NaN/Infinity 는 하한으로 대체 후 clamp.</summary>
    public static double SafeClamp(double v, double lo, double hi) => IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;

    /// <summary>두 점 거리 — 입력이 하나라도 비유한이면 0(핀치 0-거리 방어).</summary>
    public static double Distance(double x1, double y1, double x2, double y2)
    {
        if (!IsFinite(x1) || !IsFinite(y1) || !IsFinite(x2) || !IsFinite(y2)) return 0;
        var dx = x2 - x1; var dy = y2 - y1;
        var d = Math.Sqrt(dx * dx + dy * dy);
        return IsFinite(d) ? d : 0;
    }

    /// <summary>두 점 중점 — 비유한 입력이면 (0,0).</summary>
    public static (double X, double Y) Midpoint(double x1, double y1, double x2, double y2)
    {
        if (!IsFinite(x1) || !IsFinite(y1) || !IsFinite(x2) || !IsFinite(y2)) return (0, 0);
        return ((x1 + x2) / 2.0, (y1 + y2) / 2.0);
    }
}

/// <summary>드래그 해제 사유(진단/로깅용).</summary>
public enum DragReleaseReason { None, PointerUp, PointerLeave, PointerCancel, Blur, LostCapture }

/// <summary>
/// [PromptFlow Studio v4.4.2] 3D CAD/HUD 포인터 드래그 락 방어(Task 1 · 순수 BCL · 방어적).
///   ▸ <b>Sticky Drag Lock 차단</b>: pointerup 이 영역 밖에서 누락돼 드래그가 고정되는 현상을,
///     pointerleave / pointercancel / blur / lostpointercapture 시 <see cref="ForceRelease"/>로 무조건 해제.
///   ▸ 모든 좌표에 NaN/Infinity 검증 → 잘못된 델타로 인한 카메라 튐 방지.
/// </summary>
public sealed class PointerDragGuard
{
    private long? _activePointerId;
    private double _lastX, _lastY;

    /// <summary>현재 드래그 중인지.</summary>
    public bool IsDragging => _activePointerId.HasValue;
    /// <summary>활성 포인터 id(없으면 null).</summary>
    public long? ActivePointerId => _activePointerId;
    /// <summary>마지막 해제 사유.</summary>
    public DragReleaseReason LastRelease { get; private set; } = DragReleaseReason.None;
    /// <summary>강제 해제(락 차단) 횟수 — 진단.</summary>
    public int ForceReleaseCount { get; private set; }

    /// <summary>드래그 시작 — 좌표가 비유한이면 무시(false).</summary>
    public bool Begin(long pointerId, double x, double y)
    {
        if (!SafeGeom.IsFinite(x) || !SafeGeom.IsFinite(y)) return false;
        _activePointerId = pointerId;
        _lastX = x; _lastY = y;
        LastRelease = DragReleaseReason.None;
        return true;
    }

    /// <summary>이동 델타 — 활성 포인터가 아니거나 좌표가 비유한이면 (0,0)(튐 방지).</summary>
    public (double Dx, double Dy) Move(long pointerId, double x, double y)
    {
        if (_activePointerId != pointerId) return (0, 0);
        if (!SafeGeom.IsFinite(x) || !SafeGeom.IsFinite(y)) return (0, 0);
        var dx = x - _lastX; var dy = y - _lastY;
        _lastX = x; _lastY = y;
        return (dx, dy);
    }

    /// <summary>정상 종료(pointerup) — 해당 포인터가 활성일 때만.</summary>
    public bool End(long pointerId)
    {
        if (_activePointerId != pointerId) return false;
        _activePointerId = null;
        LastRelease = DragReleaseReason.PointerUp;
        return true;
    }

    /// <summary>★ Sticky Drag Lock 차단 — 영역 이탈/취소/blur/capture 상실 시 무조건 해제(멱등).</summary>
    public void ForceRelease(DragReleaseReason reason = DragReleaseReason.PointerLeave)
    {
        if (_activePointerId is null) return;
        _activePointerId = null;
        LastRelease = reason;
        ForceReleaseCount++;
    }
}

/// <summary>
/// [PromptFlow Studio v4.4.2] Canvas/RGBA 그래픽 버퍼 메모리 하드닝(Task 1 · 순수 BCL · IDisposable).
///   ▸ 버퍼 사용 완료 시 <see cref="Release"/>로 <b>명시적 해제</b> → GPU/메모리 과점유 방지.
///   ▸ WebGL/2D <b>Context Loss</b> 발생 시 <see cref="OnContextLost"/>로 Graceful 폐기,
///     복구 시 <see cref="Reinitialize"/>로 안전 재초기화.
///   ▸ 차원/총 바이트 상한 검증으로 오버플로·과할당 차단.
/// </summary>
public sealed class GraphicsBufferGuard : IDisposable
{
    /// <summary>한 변 최대 픽셀(오버플로·과할당 방어).</summary>
    public const int MaxDim = 32768;
    /// <summary>총 버퍼 상한(256MB · GPU/메모리 과점유 방어).</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    private byte[]? _rgba;
    private bool _disposed;

    /// <summary>현재 폭.</summary>
    public int Width { get; private set; }
    /// <summary>현재 높이.</summary>
    public int Height { get; private set; }
    /// <summary>컨텍스트 손실 상태.</summary>
    public bool ContextLost { get; private set; }
    /// <summary>버퍼 보유 여부.</summary>
    public bool HasBuffer => _rgba is not null;
    /// <summary>버퍼 바이트 길이.</summary>
    public long ByteLength => _rgba?.LongLength ?? 0;

    /// <summary>RGBA 버퍼 할당 — 잘못된 크기/과할당은 거부(false). 기존 버퍼는 먼저 해제.</summary>
    public bool Allocate(int width, int height)
    {
        if (_disposed) return false;
        if (width <= 0 || height <= 0) return false;
        if (width > MaxDim || height > MaxDim) return false;      // 오버플로 방어(선검증)
        long bytes = (long)width * height * 4;
        if (bytes > MaxBytes) return false;                        // 과점유 상한
        Release();                                                 // 기존 버퍼 명시적 해제
        _rgba = new byte[bytes];
        Width = width; Height = height;
        return true;
    }

    /// <summary>버퍼 뷰(없으면 Empty · null 참조 없음).</summary>
    public Span<byte> Buffer => _rgba is null ? Span<byte>.Empty : _rgba.AsSpan();

    /// <summary>사용 완료 → 명시적 해제(GC 회수 촉진 · 재사용 안전).</summary>
    public void Release()
    {
        _rgba = null;
        Width = 0; Height = 0;
    }

    /// <summary>★ WebGL/2D Context Loss — 버퍼를 Graceful 폐기하고 손실 상태로 표시.</summary>
    public void OnContextLost()
    {
        ContextLost = true;
        Release();
    }

    /// <summary>★ Context 복구 — 손실 해제 후 재할당(실패 시 false).</summary>
    public bool Reinitialize(int width, int height)
    {
        if (_disposed) return false;
        ContextLost = false;
        return Allocate(width, height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}

/// <summary>
/// [PromptFlow Studio v4.4.2] 멀티터치 핀치 줌 예외 방어(Task 3 지원 · 순수 기하 · 방어적).
///   ▸ 2점 미만 입력은 <b>즉시 무시</b> → 배열 인덱스 초과(IndexOutOfRangeException) 원천 차단.
///   ▸ 0-거리/NaN 좌표에서 배율을 1.0 으로 안전 폴백하고 [min,max] 로 clamp → NaN 확대/무한 줌 방지.
/// </summary>
public sealed class PinchGuard
{
    private double _baseDistance;
    private bool _active;

    /// <summary>핀치 진행 중인지.</summary>
    public bool IsActive => _active;

    /// <summary>핀치 시작 — 2점 이상 & 유효 거리일 때만(그 외 false).</summary>
    public bool Begin(IReadOnlyList<(double X, double Y)>? points)
    {
        if (points is null || points.Count < 2) return false;   // ★ 인덱스 초과 방어
        var d = SafeGeom.Distance(points[0].X, points[0].Y, points[1].X, points[1].Y);
        if (d <= 0) return false;                               // 0-거리/NaN 방어
        _baseDistance = d;
        _active = true;
        return true;
    }

    /// <summary>현재 배율 — 비활성/무효 입력이면 1.0, 항상 [min,max] clamp(NaN 없음).</summary>
    public double Scale(IReadOnlyList<(double X, double Y)>? points, double min = 0.2, double max = 8.0)
    {
        if (!_active || points is null || points.Count < 2) return 1.0;   // ★ 인덱스 초과 방어
        if (_baseDistance <= 0) return 1.0;
        var d = SafeGeom.Distance(points[0].X, points[0].Y, points[1].X, points[1].Y);
        if (d <= 0) return 1.0;
        var s = d / _baseDistance;
        if (!SafeGeom.IsFinite(s)) return 1.0;                            // NaN/Infinity 방어
        return Math.Clamp(s, min, max);
    }

    /// <summary>핀치 중심(줌 앵커) — 무효 입력이면 (0,0).</summary>
    public (double X, double Y) Center(IReadOnlyList<(double X, double Y)>? points)
    {
        if (points is null || points.Count < 2) return (0, 0);
        return SafeGeom.Midpoint(points[0].X, points[0].Y, points[1].X, points[1].Y);
    }

    /// <summary>핀치 종료(상태 초기화).</summary>
    public void End()
    {
        _active = false;
        _baseDistance = 0;
    }
}
