using System;
using System.Runtime;
using System.Threading;

namespace PromptFlowStudio.Services.Diagnostics;

/// <summary>할당 가드/NoGC 영역 계측 카운터(스레드 안전). 기본은 <see cref="Global"/>, 테스트는 인스턴스를 주입해 격리.</summary>
public sealed class ZeroGcCounters
{
    public static readonly ZeroGcCounters Global = new();

    internal long _scopes, _violations, _inconclusive, _lastViolationBytes;
    internal long _noGcEntered, _noGcRefused, _noGcBudgetExceeded;
    private string? _lastViolationSite;

    public long Scopes => Interlocked.Read(ref _scopes);
    /// <summary>가드 구간 안에서 GC 없이 관리 힙 할당이 관측된 횟수(= Zero-Alloc 회귀).</summary>
    public long Violations => Interlocked.Read(ref _violations);
    /// <summary>구간 도중 GC 가 겹쳐 판정을 보류한 횟수(할당 카운터 해석 불확실 - 위반으로 세지 않음).</summary>
    public long InconclusiveGcOverlap => Interlocked.Read(ref _inconclusive);
    public long LastViolationBytes => Interlocked.Read(ref _lastViolationBytes);
    public string? LastViolationSite => Volatile.Read(ref _lastViolationSite);
    public long NoGcRegionsEntered => Interlocked.Read(ref _noGcEntered);
    public long NoGcRegionsRefused => Interlocked.Read(ref _noGcRefused);
    /// <summary>NoGC 영역이 예산 초과(타 스레드 할당 포함)로 런타임에 의해 조기 종료된 횟수.</summary>
    public long NoGcBudgetExceeded => Interlocked.Read(ref _noGcBudgetExceeded);

    internal void RecordViolation(string site, long bytes)
    {
        Interlocked.Increment(ref _violations);
        Interlocked.Exchange(ref _lastViolationBytes, bytes);
        Volatile.Write(ref _lastViolationSite, site);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _scopes, 0); Interlocked.Exchange(ref _violations, 0); Interlocked.Exchange(ref _inconclusive, 0);
        Interlocked.Exchange(ref _lastViolationBytes, 0); Volatile.Write(ref _lastViolationSite, null);
        Interlocked.Exchange(ref _noGcEntered, 0); Interlocked.Exchange(ref _noGcRefused, 0); Interlocked.Exchange(ref _noGcBudgetExceeded, 0);
    }
}

/// <summary>
/// [PromptFlow Studio v12.3.0] 핫패스 GC 억제/검증 스코프 — <c>using var _ = ZeroGcRegionScope.Enter("site");</c>
///   ⚠ 설계 근거(실측): <see cref="GC.TryStartNoGCRegion(long)"/> 은 ① 진입 시 전체 블로킹 GC 를 먼저 수행하고(gen2 +1 관측)
///   ② 프로세스 전역이라 중첩 시 InvalidOperationException ③ 다른 스레드의 할당도 예산을 소모해 영역이 조용히 끝나며
///   그 뒤 EndNoGCRegion 도 예외를 던진다. 따라서 60FPS·10ms 주기 핫패스마다 거는 것은 매 프레임 전체 GC 를 유발하는 역효과다.
///   그래서 이 스코프는 세 용도로 분리했다:
///   ▸ <see cref="Enter"/>(기본, 핫패스): 스레드별 할당 바이트를 구간 전후 비교하는 <b>부작용 없는 가드</b>(ref struct · 할당 0 · 수십 ns).
///     GC 없이 할당이 관측되면 위반으로 집계(사이트명 기록) - "원천 통제"를 런타임 GC 조작이 아닌 회귀 즉시 검출로 구현.
///   ▸ <see cref="EnterCritical"/>(opt-in, 드문 짧은 버스트 전용): 참조 카운트로 첫 진입만 TryStartNoGCRegion, 마지막 퇴장만 End -
///     중첩·예산 초과·거절을 전부 예외 없이 흡수하고 카운터로 노출.
///   ▸ <see cref="GcLatencySession"/>: 실시간 루프(리액션 오버레이·메시 프로브) 가동 동안 SustainedLowLatency(블로킹 gen2 억제)를
///     참조 카운트로 유지 - 스트리밍 세션에 적합한 런타임 권장 방식.
/// </summary>
public ref struct ZeroGcRegionScope
{
    private readonly ZeroGcCounters _counters;
    private readonly string _site;
    private readonly long _startBytes;
    private readonly int _startGen0;
    private readonly bool _critical;
    private bool _disposed;

    private ZeroGcRegionScope(ZeroGcCounters counters, string site, bool critical)
    {
        _counters = counters;
        _site = site;
        _critical = critical;
        _disposed = false;
        Interlocked.Increment(ref counters._scopes);
        _startGen0 = GC.CollectionCount(0);
        _startBytes = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>할당 가드 구간 시작(런타임 GC 동작 무변경). <paramref name="site"/> 는 상수 문자열 권장(할당 없음).</summary>
    public static ZeroGcRegionScope Enter(string site, ZeroGcCounters? counters = null)
        => new(counters ?? ZeroGcCounters.Global, site, critical: false);

    /// <summary>짧은 임계 버스트용 NoGC 영역 + 가드. 이미 다른 임계 구간이 열려 있으면 그 영역을 공유(중첩 예외 없음).
    ///   거절/예산 초과 시에도 가드로는 계속 동작. ⚠ 진입 시 런타임이 전체 GC 를 수행할 수 있다 - 프레임 루프에 쓰지 말 것.</summary>
    public static ZeroGcRegionScope EnterCritical(string site, long budgetBytes, ZeroGcCounters? counters = null)
    {
        var c = counters ?? ZeroGcCounters.Global;
        NoGcRegionGate.Enter(budgetBytes, c);
        return new ZeroGcRegionScope(c, site, critical: true);
    }

    /// <summary>현재까지 이 구간에서 관측된 할당 바이트(GC 겹침 여부 무관 원시값).</summary>
    public readonly long AllocatedSoFar => GC.GetAllocatedBytesForCurrentThread() - _startBytes;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - _startBytes;
        var gcOverlap = GC.CollectionCount(0) != _startGen0;
        if (_critical) NoGcRegionGate.Exit(_counters);
        if (bytes <= 0) return;
        if (gcOverlap && !_critical) Interlocked.Increment(ref _counters._inconclusive);
        else _counters.RecordViolation(_site, bytes);
    }
}

/// <summary>프로세스 전역 NoGC 영역 참조 카운터(첫 진입만 시작, 마지막 퇴장만 종료).</summary>
internal static class NoGcRegionGate
{
    private static readonly object Gate = new();
    private static int _depth;
    private static bool _owned;

    internal static int Depth { get { lock (Gate) return _depth; } }

    internal static void Enter(long budgetBytes, ZeroGcCounters counters)
    {
        lock (Gate)
        {
            if (_depth++ > 0) return;
            bool started;
            try { started = GC.TryStartNoGCRegion(Math.Max(1, budgetBytes)); }
            catch (InvalidOperationException) { started = false; }       // 외부 코드가 이미 NoGC 영역 보유
            catch (ArgumentOutOfRangeException) { started = false; }    // 예산이 에페메랄 한도 초과
            _owned = started;
            if (started) Interlocked.Increment(ref counters._noGcEntered);
            else Interlocked.Increment(ref counters._noGcRefused);
        }
    }

    internal static void Exit(ZeroGcCounters counters)
    {
        lock (Gate)
        {
            if (_depth == 0) return;               // 과다 해제 방어
            if (--_depth > 0 || !_owned) return;
            _owned = false;
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                try { GC.EndNoGCRegion(); }
                catch (InvalidOperationException) { Interlocked.Increment(ref counters._noGcBudgetExceeded); }
            }
            else Interlocked.Increment(ref counters._noGcBudgetExceeded);   // 예산 초과로 런타임이 이미 종료
            GcLatencySession.ApplyPendingRestore();
        }
    }
}

/// <summary>실시간 루프 가동 동안 <see cref="GCLatencyMode.SustainedLowLatency"/> 를 참조 카운트로 유지(마지막 해제 시 원래 모드 복원).</summary>
public static class GcLatencySession
{
    private static readonly object Gate = new();
    private static int _count;
    private static GCLatencyMode _saved;
    private static GCLatencyMode? _pendingRestore;

    public static int ActiveCount { get { lock (Gate) return _count; } }

    public static void Acquire()
    {
        lock (Gate)
        {
            if (_count++ > 0) return;
            var cur = GCSettings.LatencyMode;
            if (cur == GCLatencyMode.NoGCRegion) { _saved = GCLatencyMode.Interactive; return; }   // NoGC 영역 중엔 모드 변경 금지 - 복원 기준만 기록
            _saved = cur;
            _pendingRestore = null;
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { /* 미지원 GC 구성 - 무시 */ }
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_count == 0) return;               // 과다 해제 방어
            if (--_count > 0) return;
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) { _pendingRestore = _saved; return; }   // 영역 종료 후 복원
            try { if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency) GCSettings.LatencyMode = _saved; } catch { /* 무시 */ }
        }
    }

    internal static void ApplyPendingRestore()
    {
        lock (Gate)
        {
            if (_pendingRestore is not { } mode || _count > 0) return;
            _pendingRestore = null;
            try { if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion) GCSettings.LatencyMode = mode; } catch { /* 무시 */ }
        }
    }
}
