using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Diagnostics;

/// <summary>복구 이벤트 — 시각·라벨·사유·조치·복구 성공 여부.</summary>
public sealed record RecoveryEvent(DateTimeOffset Time, string Label, string Reason, string Action, bool Recovered);

/// <summary>
/// [PromptFlowStudio] 렌더링 워커 백그라운드 복구 센티널(v3.4.0 · 관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 백그라운드 파이프라인을 <b>하트비트 와치독</b>으로 감시(<see cref="Beat"/>/<see cref="IsStalled"/>) → 무응답 스레드를 조기 탐지.
///   ▸ 비정상 예외 또는 <b>VRAM/RAM 부족(OutOfMemory)</b> 발생 시 <see cref="RunWithFailoverAsync{T}"/> 로 <b>백업 파이프라인 경로로 즉시 우회(Failover)</b> — 세션 손실 없이 재처리.
///   ▸ OOM 은 GC 회수 후 백업 재시도. 모든 복구 시도를 이벤트 로그로 남겨 사후 추적. 취소는 그대로 전파(복구 대상 아님).
/// </summary>
public sealed class AutonomousRecoverySentinel
{
    private readonly object _lock = new();
    private readonly List<RecoveryEvent> _events = new();
    private readonly Dictionary<string, DateTimeOffset> _beats = new(StringComparer.Ordinal);

    /// <summary>복구/하트비트 변경 방송.</summary>
    public event Action? OnChange;

    /// <summary>복구 이벤트(최신 우선).</summary>
    public IReadOnlyList<RecoveryEvent> Events { get { lock (_lock) return _events.AsEnumerable().Reverse().ToList(); } }
    /// <summary>총 복구 시도 수.</summary>
    public int RecoveryCount { get { lock (_lock) return _events.Count; } }
    /// <summary>성공 복구 수.</summary>
    public int RecoveredCount { get { lock (_lock) return _events.Count(e => e.Recovered); } }

    // ══════════════════════ 페일오버 실행 ══════════════════════

    /// <summary>
    /// primary 를 실행하고, 예외/OOM 시 backup 경로로 우회 재처리한다. 취소는 전파. 두 경로 모두 실패하면 예외 재던짐(이벤트 기록).
    /// </summary>
    public async Task<T> RunWithFailoverAsync<T>(Func<CancellationToken, Task<T>> primary, Func<CancellationToken, Task<T>>? backup, string label, CancellationToken ct = default)
    {
        try
        {
            return await primary(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }   // 취소는 복구 대상 아님
        catch (Exception ex)
        {
            var oom = ex is OutOfMemoryException;
            var reason = oom ? "OutOfMemory(VRAM/RAM)" : ex.GetType().Name;
            if (oom) { try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true); GC.WaitForPendingFinalizers(); } catch { } }

            if (backup is null) { LogEvent(label, reason, "백업 경로 없음 — 재던짐", false); throw; }
            try
            {
                var r = await backup(ct).ConfigureAwait(false);
                LogEvent(label, reason, oom ? "GC 회수 후 백업 경로 우회" : "백업 경로 우회", true);
                return r;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex2)
            {
                LogEvent(label, reason, "백업 경로도 실패: " + ex2.GetType().Name, false);
                throw;
            }
        }
    }

    /// <summary>복구 이벤트를 수동 기록(외부 감지).</summary>
    public void LogEvent(string label, string reason, string action, bool recovered)
    {
        lock (_lock) _events.Add(new RecoveryEvent(DateTimeOffset.Now, label ?? "", reason ?? "", action ?? "", recovered));
        OnChange?.Invoke();
    }

    // ══════════════════════ 하트비트 와치독 ══════════════════════

    /// <summary>워커 하트비트 갱신.</summary>
    public void Beat(string worker) { lock (_lock) _beats[worker] = DateTimeOffset.Now; OnChange?.Invoke(); }

    /// <summary>워커 등록 해제(정상 종료).</summary>
    public void Unregister(string worker) { lock (_lock) _beats.Remove(worker); OnChange?.Invoke(); }

    /// <summary>워커가 timeout(ms) 이상 무응답인지(정체).</summary>
    public bool IsStalled(string worker, int timeoutMs)
    {
        lock (_lock)
            return _beats.TryGetValue(worker, out var last) && (DateTimeOffset.Now - last).TotalMilliseconds > timeoutMs;
    }

    /// <summary>정체된 워커 목록.</summary>
    public IReadOnlyList<string> StalledWorkers(int timeoutMs)
    {
        var now = DateTimeOffset.Now;
        lock (_lock) return _beats.Where(kv => (now - kv.Value).TotalMilliseconds > timeoutMs).Select(kv => kv.Key).ToList();
    }

    /// <summary>이력 초기화.</summary>
    public void Clear() { lock (_lock) { _events.Clear(); } OnChange?.Invoke(); }
}
