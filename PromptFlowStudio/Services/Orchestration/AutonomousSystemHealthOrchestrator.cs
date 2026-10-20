using System;
using System.Collections.Generic;
using System.Threading;

namespace PromptFlowStudio.Services.Orchestration;

public enum BreakerState { Closed, Open, HalfOpen }

public sealed record BreakerView(string Name, BreakerState State, int ConsecutiveFailures, long Trips, double CooldownSec);

public sealed record HealthReport(int Total, int Closed, int Open, int HalfOpen, double CompositeScore, long Probes, long Trips, long Recoveries);

/// <summary>
/// v10.0.3 자율 시스템 헬스 오케스트레이터 — 명명된 서비스 의존성마다 고전 서킷 브레이커 상태기계
/// (Closed→[연속 실패 임계]→Open→[쿨다운 경과]→HalfOpen→[프로브 성공]→Closed / [프로브 실패]→Open)를 운영하고
/// 전 브레이커의 가중 평균으로 합성 헬스 점수를 산출한다. <see cref="Agents.SelfHealingPipelineWatchdog"/>
/// (파이프라인 내부 레인 이상 감지)와 달리 외부/내부 서비스 의존성 단위의 장애 격리·자동 재개를 담당한다.
/// 순수 BCL(Timer·Interlocked).
/// </summary>
public sealed class AutonomousSystemHealthOrchestrator : IDisposable
{
    public const int TickMs = 500;
    private const int FailureThreshold = 3;
    private const double CooldownSec = 8;
    private const double HalfOpenProbeSuccessRate = 0.7;   // 데모 프로브 성공 확률(쿨다운 경과 후 회복 추세 가중)

    private sealed class Breaker
    {
        public Breaker(string name) { Name = name; }
        public string Name { get; }
        public BreakerState State = BreakerState.Closed;
        public int ConsecutiveFailures;
        public long Trips;
        public DateTimeOffset OpenedUntil;
        public bool FaultInjected;
    }

    private readonly object _gate = new();
    private readonly List<Breaker> _breakers = new();
    private readonly Random _rng = new(10003);
    private System.Threading.Timer? _timer;
    private long _probes, _trips, _recoveries;
    private bool _disposed;

    public event Action? OnChange;

    public AutonomousSystemHealthOrchestrator()
    {
        foreach (var n in new[] { "wan-render", "gallery-store", "identity-fed", "edge-broadcast" }) _breakers.Add(new Breaker(n));
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Probes => Interlocked.Read(ref _probes);
    public long Trips => Interlocked.Read(ref _trips);
    public long Recoveries => Interlocked.Read(ref _recoveries);

    public IReadOnlyList<BreakerView> Breakers
    {
        get
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var list = new List<BreakerView>(_breakers.Count);
                foreach (var b in _breakers)
                    list.Add(new BreakerView(b.Name, b.State, b.ConsecutiveFailures, b.Trips, b.State == BreakerState.Open ? Math.Max(0, (b.OpenedUntil - now).TotalSeconds) : 0));
                return list;
            }
        }
    }

    public HealthReport Report
    {
        get
        {
            lock (_gate)
            {
                int closed = 0, open = 0, half = 0;
                foreach (var b in _breakers)
                {
                    switch (b.State) { case BreakerState.Closed: closed++; break; case BreakerState.Open: open++; break; default: half++; break; }
                }
                var total = _breakers.Count;
                var score = total == 0 ? 100.0 : (closed * 1.0 + half * 0.5) / total * 100.0;
                return new HealthReport(total, closed, open, half, Math.Round(score, 1), Interlocked.Read(ref _probes), Interlocked.Read(ref _trips), Interlocked.Read(ref _recoveries));
            }
        }
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = $"헬스 프로브 루프 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "헬스 프로브 정지"; }
        OnChange?.Invoke();
    }

    public void InjectFailure(string name)
    {
        lock (_gate)
        {
            var b = _breakers.Find(x => x.Name == name);
            if (b is null) return;
            b.FaultInjected = true;
            LastAction = $"{name} 장애 주입";
        }
        OnChange?.Invoke();
    }

    public void InjectRandomFailure()
    {
        lock (_gate)
        {
            var b = _breakers[_rng.Next(_breakers.Count)];
            b.FaultInjected = true;
            LastAction = $"{b.Name} 무작위 장애 주입";
        }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            foreach (var b in _breakers) { b.State = BreakerState.Closed; b.ConsecutiveFailures = 0; b.Trips = 0; b.FaultInjected = false; }
            LastAction = "헬스 오케스트레이터 초기화";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            _probes++;
            var now = DateTimeOffset.UtcNow;
            foreach (var b in _breakers) ProbeLocked(b, now);
        }
        OnChange?.Invoke();
    }

    private void ProbeLocked(Breaker b, DateTimeOffset now)
    {
        switch (b.State)
        {
            case BreakerState.Closed:
            {
                var success = !b.FaultInjected || _rng.NextDouble() > 0.85;   // 주입된 장애는 대부분 실패, 간헐 회복 시도
                if (success) { b.ConsecutiveFailures = 0; b.FaultInjected = false; }
                else
                {
                    b.ConsecutiveFailures++;
                    if (b.ConsecutiveFailures >= FailureThreshold) TripLocked(b, now);
                }
                break;
            }
            case BreakerState.Open:
                if (now >= b.OpenedUntil) { b.State = BreakerState.HalfOpen; LastAction = $"{b.Name} 쿨다운 경과 — HalfOpen 프로브"; }
                break;
            case BreakerState.HalfOpen:
            {
                var probeOk = _rng.NextDouble() < HalfOpenProbeSuccessRate;
                if (probeOk)
                {
                    b.State = BreakerState.Closed; b.ConsecutiveFailures = 0; b.FaultInjected = false;
                    _recoveries++;
                    LastAction = $"{b.Name} 프로브 성공 — Closed 복귀";
                }
                else { TripLocked(b, now); LastAction = $"{b.Name} 프로브 실패 — 재차단(Open)"; }
                break;
            }
        }
    }

    private void TripLocked(Breaker b, DateTimeOffset now)
    {
        b.State = BreakerState.Open;
        b.OpenedUntil = now.AddSeconds(CooldownSec);
        b.Trips++; _trips++;
        LastAction = $"{b.Name} 연속 실패 {b.ConsecutiveFailures}회 — 회로 개방(Open)";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
