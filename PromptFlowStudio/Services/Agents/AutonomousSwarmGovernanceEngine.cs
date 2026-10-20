using System;
using System.Collections.Generic;
using System.Threading;

namespace PromptFlowStudio.Services.Agents;

public enum SwarmPeerKind { Desktop, Mobile, Edge }

public enum SwarmPeerState { Healthy, Suspect, Evicted }

public sealed record SwarmPeerView(string Id, SwarmPeerKind Kind, SwarmPeerState State, double LoadPct, int MissedBeats, bool IsLeader);

public sealed record SwarmGovernanceReport(int Quorum, int Healthy, int Total, bool QuorumMet, string Leader, double SpreadPct, long Heartbeats, long Rebalances, long Elections, long Evictions);

/// <summary>
/// v9.7.1 자율 크로스플랫폼 스웜 가버넌스 — 데스크톱/모바일/엣지 피어의 분산 하트비트 스캔,
/// 다수결 쿼럼 자율 재구성(리더 이탈 시 재선출), 부하 편차 임계 초과 시 작업 로드 자율 리밸런싱.
/// 순수 BCL(Timer·Interlocked) · 백엔드 미구성 시 데모 하트비트로 Graceful.
/// </summary>
public sealed class AutonomousSwarmGovernanceEngine : IDisposable
{
    public const int TickMs = 250;
    private const int SuspectAt = 2;
    private const int EvictAt = 4;
    private const double RebalanceSpread = 40;   // 부하 편차(%) 임계

    private sealed class Peer
    {
        public Peer(string id, SwarmPeerKind kind, double load) { Id = id; Kind = kind; Load = load; }
        public string Id { get; }
        public SwarmPeerKind Kind { get; }
        public SwarmPeerState State = SwarmPeerState.Healthy;
        public double Load;
        public int Missed;
    }

    private readonly object _gate = new();
    private readonly List<Peer> _peers = new();
    private readonly Random _rng = new(9701);
    private System.Threading.Timer? _timer;
    private string _leader = "";
    private string? _faultPeer;
    private long _heartbeats, _rebalances, _elections, _evictions, _quorumLost;
    private bool _disposed;

    public event Action? OnChange;

    public AutonomousSwarmGovernanceEngine()
    {
        _peers.Add(new Peer("desktop-1", SwarmPeerKind.Desktop, 55));
        _peers.Add(new Peer("mobile-1", SwarmPeerKind.Mobile, 40));
        _peers.Add(new Peer("mobile-2", SwarmPeerKind.Mobile, 35));
        _peers.Add(new Peer("edge-1", SwarmPeerKind.Edge, 60));
        ElectLocked();
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Heartbeats => Interlocked.Read(ref _heartbeats);
    public long Rebalances => Interlocked.Read(ref _rebalances);
    public long Elections => Interlocked.Read(ref _elections);
    public long Evictions => Interlocked.Read(ref _evictions);
    public long QuorumLost => Interlocked.Read(ref _quorumLost);
    public string Leader { get { lock (_gate) return _leader; } }

    public IReadOnlyList<SwarmPeerView> Peers
    {
        get
        {
            lock (_gate)
            {
                var list = new List<SwarmPeerView>(_peers.Count);
                foreach (var p in _peers) list.Add(new SwarmPeerView(p.Id, p.Kind, p.State, Math.Round(p.Load, 1), p.Missed, p.Id == _leader));
                return list;
            }
        }
    }

    public SwarmGovernanceReport Report { get { lock (_gate) return BuildReportLocked(); } }

    private SwarmGovernanceReport BuildReportLocked()
    {
        int total = _peers.Count, healthy = 0;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var p in _peers)
            if (p.State != SwarmPeerState.Evicted) { healthy++; if (p.Load < min) min = p.Load; if (p.Load > max) max = p.Load; }
        var quorum = total / 2 + 1;
        var spread = healthy > 0 ? max - min : 0;
        return new SwarmGovernanceReport(quorum, healthy, total, healthy >= quorum, _leader, Math.Round(spread, 1),
            Interlocked.Read(ref _heartbeats), Interlocked.Read(ref _rebalances), Interlocked.Read(ref _elections), Interlocked.Read(ref _evictions));
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = $"스웜 하트비트 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "하트비트 정지"; }
        OnChange?.Invoke();
    }

    /// <summary>리더(또는 첫 건강 피어)의 하트비트 유실 주입 → suspect/evict/재선출 유발.</summary>
    public void InjectPeerFault()
    {
        lock (_gate)
        {
            var target = _peers.Find(p => p.Id == _leader && p.State != SwarmPeerState.Evicted) ?? _peers.Find(p => p.State == SwarmPeerState.Healthy);
            if (target is null) return;
            _faultPeer = target.Id;
            LastAction = $"{target.Id} 하트비트 유실 주입";
        }
        OnChange?.Invoke();
    }

    /// <summary>최저 부하 피어에 부하 급증 주입 → 리밸런싱 유발.</summary>
    public void InjectLoadSpike()
    {
        lock (_gate)
        {
            Peer? idlest = null;
            foreach (var p in _peers) if (p.State != SwarmPeerState.Evicted && (idlest is null || p.Load < idlest.Load)) idlest = p;
            if (idlest is null) return;
            idlest.Load = Math.Min(100, idlest.Load + 55);
            LastAction = $"{idlest.Id} 부하 급증 주입";
        }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            foreach (var p in _peers) { p.State = SwarmPeerState.Healthy; p.Missed = 0; p.Load = 40 + _rng.NextDouble() * 20; }
            _faultPeer = null;
            ElectLocked();
            LastAction = "스웜 초기화";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            _heartbeats++;
            foreach (var p in _peers)
            {
                if (p.State == SwarmPeerState.Evicted) continue;
                if (p.Id == _faultPeer)
                {
                    if (++p.Missed >= EvictAt) EvictLocked(p);
                    else if (p.Missed >= SuspectAt) p.State = SwarmPeerState.Suspect;
                }
                else
                {
                    if (p.Missed > 0) p.Missed--;
                    if (p.Missed == 0 && p.State == SwarmPeerState.Suspect) p.State = SwarmPeerState.Healthy;
                    p.Load = Math.Clamp(p.Load + (_rng.NextDouble() * 2 - 1) * 4, 5, 100);
                }
            }
            RebalanceLocked();
        }
        OnChange?.Invoke();
    }

    private void EvictLocked(Peer p)
    {
        p.State = SwarmPeerState.Evicted;
        p.Load = 0;
        _evictions++;
        LastAction = $"{p.Id} 격리(하트비트 {EvictAt}회 유실)";
        if (!BuildReportLocked().QuorumMet) _quorumLost++;
        if (p.Id == _leader) ElectLocked();
    }

    // 리더 = 건강 피어 중 부하 최저(용량 여유 최대). 없으면 빈 문자열(쿼럼 상실).
    private void ElectLocked()
    {
        Peer? best = null;
        foreach (var p in _peers)
            if (p.State != SwarmPeerState.Evicted && (best is null || p.Load < best.Load)) best = p;
        var next = best?.Id ?? "";
        if (next != _leader)
        {
            _leader = next;
            _elections++;
            LastAction = string.IsNullOrEmpty(next) ? "리더 부재(쿼럼 상실)" : $"리더 재선출 → {next}";
        }
    }

    private void RebalanceLocked()
    {
        Peer? busy = null, idle = null;
        foreach (var p in _peers)
        {
            if (p.State == SwarmPeerState.Evicted) continue;
            if (busy is null || p.Load > busy.Load) busy = p;
            if (idle is null || p.Load < idle.Load) idle = p;
        }
        if (busy is null || idle is null || ReferenceEquals(busy, idle) || busy.Load - idle.Load <= RebalanceSpread) return;
        var move = (busy.Load - idle.Load) / 2;
        busy.Load -= move; idle.Load += move;
        _rebalances++;
        LastAction = $"리밸런싱 {busy.Id}→{idle.Id} ({move:0.#}%)";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
