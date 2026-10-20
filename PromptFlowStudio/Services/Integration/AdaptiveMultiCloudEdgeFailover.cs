using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

public enum CloudRegion { EdgeLocal, UsEast, EuWest, ApSouth }

public enum RouteState { Primary, Failover, Draining, Down }

public sealed record RouteNodeView(CloudRegion Region, RouteState State, double RttMs, double LossPct, int Sessions, bool IsActive);

public sealed record FailoverReport(CloudRegion Active, int Healthy, int Total, long Failovers, long SessionsMigrated, long Losses, double LastSwitchMs, double SlaBudgetMs);

/// <summary>
/// v9.8.3 적응형 멀티클라우드 엣지 페일오버 — 노드 RTT/손실 하트비트 스캔으로 네트워크 단절·노드 다운을 감지,
/// 최저 RTT 건강 노드로 SLA 10ms 이내 세션 무유실 자율 우회(활성 포인터 원자 스왑 + 세션 마이그레이션).
/// 순수 BCL(Timer·Interlocked·Stopwatch).
/// </summary>
public sealed class AdaptiveMultiCloudEdgeFailover : IDisposable
{
    public const int TickMs = 250;
    public const double SlaBudgetMs = 10;
    private const double RttFailover = 180;

    private sealed class Node
    {
        public Node(CloudRegion region, double rtt, int sessions) { Region = region; Rtt = rtt; Sessions = sessions; }
        public CloudRegion Region { get; }
        public RouteState State = RouteState.Draining;
        public double Rtt;
        public double Loss;
        public int Sessions;
        public bool Down;
    }

    private readonly object _gate = new();
    private readonly List<Node> _nodes = new();
    private readonly Random _rng = new(9803);
    private System.Threading.Timer? _timer;
    private Node _active;
    private double _lastSwitchMs;
    private long _failovers, _sessionsMigrated, _losses, _heartbeats;
    private bool _disposed;

    public event Action? OnChange;

    public AdaptiveMultiCloudEdgeFailover()
    {
        _nodes.Add(new Node(CloudRegion.EdgeLocal, 8, 40));
        _nodes.Add(new Node(CloudRegion.UsEast, 45, 0));
        _nodes.Add(new Node(CloudRegion.EuWest, 70, 0));
        _nodes.Add(new Node(CloudRegion.ApSouth, 95, 0));
        _active = _nodes[0];
        _active.State = RouteState.Primary;
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public double LastSwitchMs { get { lock (_gate) return _lastSwitchMs; } }
    public long Failovers => Interlocked.Read(ref _failovers);
    public long SessionsMigrated => Interlocked.Read(ref _sessionsMigrated);
    public long Losses => Interlocked.Read(ref _losses);
    public long Heartbeats => Interlocked.Read(ref _heartbeats);
    public CloudRegion ActiveRegion { get { lock (_gate) return _active.Region; } }

    public FailoverReport Report
    {
        get
        {
            lock (_gate)
            {
                var healthy = 0;
                foreach (var n in _nodes) if (!n.Down) healthy++;
                return new FailoverReport(_active.Region, healthy, _nodes.Count,
                    Interlocked.Read(ref _failovers), Interlocked.Read(ref _sessionsMigrated), Interlocked.Read(ref _losses), Math.Round(_lastSwitchMs, 3), SlaBudgetMs);
            }
        }
    }

    public IReadOnlyList<RouteNodeView> Nodes
    {
        get
        {
            lock (_gate)
            {
                var list = new List<RouteNodeView>(_nodes.Count);
                foreach (var n in _nodes) list.Add(new RouteNodeView(n.Region, n.State, Math.Round(n.Rtt, 0), Math.Round(n.Loss, 2), n.Sessions, ReferenceEquals(n, _active)));
                return list;
            }
        }
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = "멀티클라우드 하트비트 기동";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "하트비트 정지"; }
        OnChange?.Invoke();
    }

    public void InjectNodeDown()
    {
        lock (_gate)
        {
            _active.Down = true;
            _active.State = RouteState.Down;
            _active.Loss = 100;
            LastAction = $"{_active.Region} 노드 다운 주입";
            RerouteLocked("노드 다운");
        }
        OnChange?.Invoke();
    }

    public void InjectLatencySpike()
    {
        lock (_gate) { _active.Rtt = Math.Min(400, _active.Rtt + 220); LastAction = $"{_active.Region} RTT 스파이크 주입"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            double[] baseRtt = { 8, 45, 70, 95 };
            for (var i = 0; i < _nodes.Count; i++)
            {
                var n = _nodes[i];
                n.Down = false; n.Loss = 0; n.Rtt = baseRtt[i]; n.Sessions = 0; n.State = RouteState.Draining;
            }
            _active = _nodes[0]; _active.State = RouteState.Primary; _active.Sessions = 40;
            _lastSwitchMs = 0;
            LastAction = "페일오버 초기화";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            _heartbeats++;
            foreach (var n in _nodes)
            {
                if (n.Down) continue;
                n.Rtt = Math.Clamp(n.Rtt + (_rng.NextDouble() * 2 - 1) * 6, 4, 400);
                n.Loss = Math.Clamp(n.Loss + (_rng.NextDouble() * 2 - 1) * 0.5, 0, 100);
            }
            if (_active.Down || _active.Rtt > RttFailover || _active.Loss > 10)
                RerouteLocked(_active.Rtt > RttFailover ? "RTT 초과" : "손실 초과");
        }
        OnChange?.Invoke();
    }

    // 활성 포인터 원자 스왑 + 세션 마이그레이션(무유실). Stopwatch 로 스위치 시간 실측(SLA 10ms).
    private void RerouteLocked(string cause)
    {
        Node? best = null;
        foreach (var n in _nodes)
            if (!n.Down && !ReferenceEquals(n, _active) && (best is null || n.Rtt < best.Rtt)) best = n;
        if (best is null) { _losses++; LastAction = "건강 노드 없음 — 세션 대기 큐(우회 불가)"; return; }

        var sw = Stopwatch.StartNew();
        var migrate = _active.Sessions;
        if (_active.State != RouteState.Down) _active.State = RouteState.Draining;
        best.Sessions += migrate;
        _active.Sessions = 0;
        best.State = RouteState.Primary;
        var from = _active.Region;
        _active = best;
        sw.Stop();
        _lastSwitchMs = sw.Elapsed.TotalMilliseconds;
        _failovers++;
        _sessionsMigrated += migrate;
        foreach (var n in _nodes)
            if (!n.Down && !ReferenceEquals(n, _active) && n.State == RouteState.Primary) n.State = RouteState.Failover;
        LastAction = $"페일오버 {from}→{best.Region} ({cause}) · {migrate}세션 · {_lastSwitchMs:0.###}ms";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
