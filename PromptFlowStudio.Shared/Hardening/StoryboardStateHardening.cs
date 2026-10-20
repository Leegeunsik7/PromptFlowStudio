using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Shared.Hardening;

/// <summary>
/// [PromptFlow Studio v4.4.1] 이벤트 구독/해제 누수 차단 봉투(thread-safe · 멱등).
///   컴포넌트가 여러 <c>event Action</c> 을 구독할 때 해제 액션을 모아 <see cref="Dispose"/>에서 확실히 -= 한다.
/// </summary>
public sealed class DisposableBag : IDisposable
{
    private readonly object _lock = new();
    private readonly List<Action> _teardowns = new();
    private bool _disposed;

    /// <summary>해제 액션 등록(이미 Dispose 됐으면 즉시 실행).</summary>
    public void Add(Action teardown)
    {
        if (teardown is null) return;
        lock (_lock)
        {
            if (_disposed) { SafeRun(teardown); return; }
            _teardowns.Add(teardown);
        }
    }

    /// <summary>IDisposable 등록.</summary>
    public void Add(IDisposable disposable) { if (disposable is not null) Add(disposable.Dispose); }

    /// <summary>등록 개수.</summary>
    public int Count { get { lock (_lock) return _teardowns.Count; } }

    /// <summary>전 해제 액션을 1회만 실행(개별 예외는 무시).</summary>
    public void Dispose()
    {
        Action[] arr;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            arr = _teardowns.ToArray();
            _teardowns.Clear();
        }
        foreach (var a in arr) SafeRun(a);
    }

    private static void SafeRun(Action a) { try { a(); } catch { /* 해제 중 예외 무시 */ } }
}

/// <summary>
/// [PromptFlow Studio v4.4.1] 스토리보드 상태/누수 하드닝(Task 2 · 순수 BCL).
///   ▸ 전역 서비스 <c>event Action</c> 구독을 IDisposable 로 감싸 컴포넌트 Dispose 에서 확실히 해제.
///   ▸ 100컷 이상 대용량은 Virtualize/페이징으로 DOM 병목 차단.
/// </summary>
public static class StoryboardStateHardening
{
    /// <summary>이 개수 이상이면 Virtualize/페이징 권장.</summary>
    public const int VirtualizeThreshold = 100;

    /// <summary>대용량 렌더 여부.</summary>
    public static bool ShouldVirtualize(int itemCount) => itemCount >= VirtualizeThreshold;

    /// <summary>구독을 IDisposable 로 감싼다 — 즉시 subscribe 실행, Dispose 시 unsubscribe(멱등).</summary>
    public static IDisposable Subscribe(Action subscribe, Action unsubscribe)
    {
        subscribe?.Invoke();
        return new Unsub(unsubscribe);
    }

    /// <summary>총 개수/페이지 크기 → 페이지 계획.</summary>
    public static PagePlan Paginate(int total, int pageSize)
    {
        var ps = Math.Max(1, pageSize);
        var t = Math.Max(0, total);
        var count = t == 0 ? 0 : (int)Math.Ceiling(t / (double)ps);
        return new PagePlan(t, ps, count);
    }

    private sealed class Unsub : IDisposable
    {
        private Action? _unsubscribe;
        public Unsub(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() { var u = _unsubscribe; _unsubscribe = null; try { u?.Invoke(); } catch { } }
    }
}
