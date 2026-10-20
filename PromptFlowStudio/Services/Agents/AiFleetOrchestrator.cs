using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace PromptFlowStudio.Services.Agents;

/// <summary>플릿 워커 상태 — 유휴/활성/격리/드레이닝.</summary>
public enum FleetWorkerState { Idle, Active, Quarantined, Draining }

/// <summary>플릿 워커 스냅샷(불변 · HUD 표출용).</summary>
public sealed record FleetWorkerView(string Id, FleetWorkerState State, int Load, double HealthScore, string Region);

/// <summary>플릿 제어 조치 로그(불변) — 조치 종류·대상 워커·근거.</summary>
public sealed record FleetAction(string Kind, string WorkerId, string Reason, DateTimeOffset At = default)
{
    public FleetAction Stamped() => this with { At = DateTimeOffset.Now };
}

/// <summary>
/// [PromptFlow Studio v8.4.1] 전역 AI 에이전트 플릿 오케스트레이터(관찰 가능 싱글턴 · 순수 BCL · IDisposable).
///   ▸ <see cref="AiSelfDiagnosticEngine.OnHealDirective"/> 를 구독해 자가 치유 지시(ScaleOut/Isolate/Throttle/Recover)를 멀티 워커 제어로 물질화.
///   ▸ ScaleOut→동적 스폰(최대치 클램프) · Isolate→병목 워커 격리 · Throttle→드레이닝 · Recover→격리 해제. 모두 <b>멱등</b>(중복 조치 무시).
///   ▸ <see cref="AssignWork"/> 는 활성 워커 중 최소 부하 노드에 배정(로드밸런싱) · <see cref="IngestEdgeMetric"/> 로 리전 지연을 헬스에 반영(GlobalEdgeFederation 결선점, 하드 의존 없음).
///   ⚠ <see cref="OnChange"/> 는 진단 콜백(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class AiFleetOrchestrator : IDisposable
{
    private const int LogCap = 60;

    private readonly AiSelfDiagnosticEngine? _diagnostic;
    private readonly int _minWorkers;
    private readonly int _maxWorkers;
    private readonly object _gate = new();
    private readonly Dictionary<string, Worker> _workers = new();
    private readonly List<FleetAction> _log = new();
    private long _seq;

    public AiFleetOrchestrator(AiSelfDiagnosticEngine? diagnostic = null, int minWorkers = 2, int maxWorkers = 8)
    {
        _minWorkers = Math.Clamp(minWorkers, 1, 64);
        _maxWorkers = Math.Clamp(maxWorkers, _minWorkers, 128);
        for (var i = 0; i < _minWorkers; i++) SpawnInternal("seed");
        _diagnostic = diagnostic;
        if (_diagnostic is not null) _diagnostic.OnHealDirective += OnHeal;   // 자가 진단 → 플릿 제어 결선
    }

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long SpawnCount { get; private set; }
    public long QuarantineCount { get; private set; }
    public long RecoverCount { get; private set; }
    public long TotalAssigned { get; private set; }
    public string? LastAction { get; private set; }
    public int WorkerCount { get { lock (_gate) return _workers.Count; } }
    public int ActiveWorkers { get { lock (_gate) return _workers.Values.Count(w => w.State == FleetWorkerState.Active); } }
    public int QuarantinedWorkers { get { lock (_gate) return _workers.Values.Count(w => w.State == FleetWorkerState.Quarantined); } }
    public IReadOnlyList<FleetWorkerView> Workers { get { lock (_gate) return _workers.Values.OrderBy(w => w.Id).Select(Snapshot).ToList(); } }
    public IReadOnlyList<FleetAction> RecentActions { get { lock (_gate) return _log.Take(24).ToList(); } }

    // ── 자가 진단 지시 처리(멱등) ──
    private void OnHeal(HealDirective d)
    {
        try
        {
            switch (d.Action)
            {
                case HealAction.ScaleOut: ScaleOut(d.Reason); break;
                case HealAction.Isolate: Isolate(d.Reason); break;
                case HealAction.Throttle: Drain(d.Reason); break;
                case HealAction.Recover: Recover(d.Reason); break;
            }
        }
        catch (Exception ex) { Program.Log("Fleet.OnHeal", ex); }   // 제어 실패는 삼킨다(Graceful)
    }

    /// <summary>동적 스폰 — 최대치 미만일 때만(초과 시 무시 · 멱등).</summary>
    public void ScaleOut(string reason)
    {
        string? id = null;
        lock (_gate) { if (_workers.Count < _maxWorkers) id = SpawnInternal(reason); }
        if (id is not null) { SpawnCount++; Record("scale-out", id, reason); }
    }

    /// <summary>병목 워커 격리 — 활성 중 최고 부하 노드를 격리(없으면 무시).</summary>
    public void Isolate(string reason)
    {
        string? id = null;
        lock (_gate)
        {
            var victim = _workers.Values.Where(w => w.State == FleetWorkerState.Active).OrderByDescending(w => w.Load).FirstOrDefault();
            if (victim is not null && _workers.Count > _minWorkers) { victim.State = FleetWorkerState.Quarantined; id = victim.Id; }
        }
        if (id is not null) { QuarantineCount++; Record("isolate", id, reason); }
    }

    /// <summary>스로틀 — 활성 워커 1기를 드레이닝 전환(신규 배정 제외).</summary>
    public void Drain(string reason)
    {
        string? id = null;
        lock (_gate)
        {
            var w = _workers.Values.Where(x => x.State == FleetWorkerState.Active).OrderByDescending(x => x.Load).FirstOrDefault();
            if (w is not null && _workers.Count > _minWorkers) { w.State = FleetWorkerState.Draining; id = w.Id; }
        }
        if (id is not null) Record("drain", id, reason);
    }

    /// <summary>격리/드레이닝 워커 회복 — 가장 오래 격리된 노드를 활성 복귀.</summary>
    public void Recover(string reason)
    {
        string? id = null;
        lock (_gate)
        {
            var w = _workers.Values.FirstOrDefault(x => x.State is FleetWorkerState.Quarantined or FleetWorkerState.Draining);
            if (w is not null) { w.State = FleetWorkerState.Active; w.HealthScore = 1.0; id = w.Id; }
        }
        if (id is not null) { RecoverCount++; Record("recover", id, reason); }
    }

    /// <summary>로드밸런싱 — 활성 워커 중 최소 부하 노드에 작업 배정. 반환=배정 워커 Id(가용 없으면 null).</summary>
    public string? AssignWork(int cost = 1)
    {
        string? id = null;
        lock (_gate)
        {
            var w = _workers.Values.Where(x => x.State == FleetWorkerState.Active).OrderBy(x => x.Load).FirstOrDefault();
            if (w is not null) { w.Load += Math.Max(1, cost); id = w.Id; }
        }
        if (id is not null) { TotalAssigned++; Notify(); }
        return id;
    }

    /// <summary>작업 완료 — 워커 부하 감산(0 하한).</summary>
    public void CompleteWork(string workerId, int cost = 1)
    {
        lock (_gate) { if (_workers.TryGetValue(workerId, out var w)) w.Load = Math.Max(0, w.Load - Math.Max(1, cost)); }
        Notify();
    }

    /// <summary>엣지 리전 지연 반영(GlobalEdgeFederation 결선점) — 지연 급증 시 해당 리전 워커 헬스 하향.</summary>
    public void IngestEdgeMetric(string region, double latencyMs)
    {
        var r = (region ?? "").Trim();
        lock (_gate)
            foreach (var w in _workers.Values.Where(w => string.Equals(w.Region, r, StringComparison.OrdinalIgnoreCase)))
                w.HealthScore = latencyMs <= 0 ? 1.0 : Math.Clamp(1.0 - Math.Min(1.0, latencyMs / 1000.0), 0.05, 1.0);
        Notify();
    }

    // ── 내부 ──
    private string SpawnInternal(string reason)   // 락 아래 호출
    {
        var id = $"w{Interlocked.Increment(ref _seq):D3}";
        _workers[id] = new Worker { Id = id, State = FleetWorkerState.Active, Load = 0, HealthScore = 1.0, Region = "auto" };
        return id;
    }

    private void Record(string kind, string workerId, string reason)
    {
        LastAction = $"[{kind}] {workerId} · {reason}";
        lock (_gate) { _log.Insert(0, new FleetAction(kind, workerId, reason).Stamped()); if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap); }
        Notify();
    }

    private static FleetWorkerView Snapshot(Worker w) => new(w.Id, w.State, w.Load, w.HealthScore, w.Region);

    /// <summary>내부 가변 워커(락 보호). 외부에는 <see cref="FleetWorkerView"/> 스냅샷만 노출.</summary>
    private sealed class Worker
    {
        public string Id = "";
        public FleetWorkerState State;
        public int Load;
        public double HealthScore;
        public string Region = "auto";
    }

    public void Dispose()
    {
        try { if (_diagnostic is not null) _diagnostic.OnHealDirective -= OnHeal; } catch { /* 무시 */ }
    }
}
