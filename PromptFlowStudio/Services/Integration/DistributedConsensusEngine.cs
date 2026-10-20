using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

/// <summary>Raft 노드 역할 — 팔로워/후보자/리더.</summary>
public enum RaftRole { Follower, Candidate, Leader }

/// <summary>Raft 노드 스냅샷(불변 · HUD 표출).</summary>
public sealed record RaftNodeView(string Id, RaftRole Role, long Term, int MatchIndex, bool Alive);

/// <summary>복제 로그 엔트리(불변) — 인덱스·임기·명령·커밋 여부.</summary>
public sealed record RaftLogEntry(int Index, long Term, string Command, bool Committed);

/// <summary>
/// [PromptFlow Studio v8.6.1] 분산 합의 &amp; 리더 선출 엔진(관찰 가능 싱글턴 · 순수 BCL Raft · 외부 NuGet 0 · IDisposable).
///   ▸ GPU 그리드 노드 집합에 대해 Raft(Leader Election · Log Replication · Heartbeat)를 인프로세스로 구동해 분산 상태를 단일 리더로 직렬화.
///   ▸ <see cref="System.Threading.Timer"/> 틱마다 리더 하트비트 → 팔로워 갱신·과반 복제 커밋. 리더 부재/실효 시 임기 증가 후 과반 득표로 재선출.
///   ▸ <see cref="AppendCommand"/> 는 리더 로그에 적재 후 과반 매치 시 커밋(트랜잭션 무결성). 노드 장애(<see cref="MarkFault"/>)는 즉시 재선출을 유발.
///   ⚠ <see cref="OnChange"/> 는 타이머(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링. 예외는 삼켜 합의가 앱을 멈추지 않는다(Graceful).
/// </summary>
public sealed class DistributedConsensusEngine : IDisposable
{
    private const int HeartbeatMs = 600;
    private const int ElectionTimeoutMs = 1500;   // 리더 하트비트 미수신 임계

    private readonly ConcurrentDictionary<string, Node> _nodes = new();
    private readonly List<RaftLogEntry> _log = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;

    private long _term;
    private string? _leaderId;
    private int _commitIndex = -1;
    private long _lastLeaderBeatTick;

    public DistributedConsensusEngine()
    {
        // 기본 3노드 클러스터 시드(그리드 워커 매핑 전 부트스트랩).
        for (var i = 1; i <= 3; i++) RegisterNode($"raft{i}");
        _lastLeaderBeatTick = Environment.TickCount64;
        _timer = new System.Threading.Timer(_ => Tick(), null, HeartbeatMs, HeartbeatMs);
    }

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long CurrentTerm => Interlocked.Read(ref _term);
    public string? LeaderId { get { lock (_gate) return _leaderId; } }
    public int CommitIndex { get { lock (_gate) return _commitIndex; } }
    public long Elections { get; private set; }
    public long HeartbeatCount { get; private set; }
    public int NodeCount => _nodes.Count;
    public int AliveCount => _nodes.Values.Count(n => n.Alive);
    public int LogLength { get { lock (_gate) return _log.Count; } }
    public bool HasLeader { get { lock (_gate) return _leaderId is not null && _nodes.TryGetValue(_leaderId, out var l) && l.Alive; } }
    public IReadOnlyList<RaftNodeView> Nodes => _nodes.Values.OrderBy(n => n.Id).Select(Snapshot).ToList();
    public IReadOnlyList<RaftLogEntry> Log { get { lock (_gate) return _log.ToList(); } }

    // ── 클러스터 구성 ──
    public void RegisterNode(string id)
    {
        var nid = (id ?? "").Trim();
        if (nid.Length == 0) return;
        _nodes.TryAdd(nid, new Node { Id = nid, Role = RaftRole.Follower, Alive = true, MatchIndex = -1 });
        Notify();
    }

    /// <summary>노드 장애 표시 — 리더 장애 시 즉시 재선출 유발.</summary>
    public void MarkFault(string id)
    {
        if (!_nodes.TryGetValue(id, out var n)) return;
        n.Alive = false;
        lock (_gate) { if (_leaderId == id) _leaderId = null; }
        Notify();
    }

    public void Revive(string id)
    {
        if (_nodes.TryGetValue(id, out var n)) { n.Alive = true; n.Role = RaftRole.Follower; Notify(); }
    }

    /// <summary>
    /// 리더 로그에 명령 적재 → 다음 하트비트에 복제/커밋. 리더 부재 시 거부(false).
    /// </summary>
    public bool AppendCommand(string command)
    {
        lock (_gate)
        {
            if (_leaderId is null || !_nodes.TryGetValue(_leaderId, out var leader) || !leader.Alive) return false;
            var entry = new RaftLogEntry(_log.Count, _term, command ?? "", false);
            _log.Add(entry);
            leader.MatchIndex = _log.Count - 1;   // 리더는 자기 로그 즉시 매치
        }
        Notify();
        return true;
    }

    // ── Raft 코어 틱 ──
    private void Tick()
    {
        try
        {
            var now = Environment.TickCount64;
            bool leaderLive;
            lock (_gate) leaderLive = _leaderId is not null && _nodes.TryGetValue(_leaderId, out var l) && l.Alive;

            if (!leaderLive || now - _lastLeaderBeatTick > ElectionTimeoutMs)
                ElectLeader();
            else
                LeaderHeartbeat();

            Notify();
        }
        catch (Exception ex) { Program.Log("Consensus.Tick", ex); }
    }

    /// <summary>리더 선출 — 임기 증가 후 살아있는 후보가 과반 득표 시 리더 승격.</summary>
    private void ElectLeader()
    {
        var alive = _nodes.Values.Where(n => n.Alive).OrderBy(n => n.Id).ToList();
        var total = _nodes.Count;
        var majority = total / 2 + 1;
        if (alive.Count < majority) { lock (_gate) _leaderId = null; return; }   // 정족수 미달 → 리더 없음

        var term = Interlocked.Increment(ref _term);
        var candidate = alive[0];                     // 결정적 후보(최소 Id) — 스플릿 보트 방지
        foreach (var n in _nodes.Values) { n.Role = RaftRole.Follower; n.Term = term; }
        candidate.Role = RaftRole.Leader;

        lock (_gate) { _leaderId = candidate.Id; }
        _lastLeaderBeatTick = Environment.TickCount64;
        Elections++;
    }

    /// <summary>리더 하트비트 — 팔로워 임기 동기화 + 미커밋 로그 과반 복제 시 커밋 전진.</summary>
    private void LeaderHeartbeat()
    {
        _lastLeaderBeatTick = Environment.TickCount64;
        HeartbeatCount++;
        var alive = _nodes.Values.Where(n => n.Alive).ToList();
        var majority = _nodes.Count / 2 + 1;

        lock (_gate)
        {
            // 팔로워 매치 인덱스를 한 스텝씩 리더로 수렴(점진 복제).
            foreach (var f in alive.Where(n => n.Role != RaftRole.Leader))
            {
                f.Term = _term;
                if (f.MatchIndex < _log.Count - 1) f.MatchIndex++;
            }
            // 과반 노드가 보유한 최대 인덱스까지 커밋.
            for (var i = _log.Count - 1; i > _commitIndex; i--)
            {
                var replicated = alive.Count(n => n.MatchIndex >= i);
                if (replicated >= majority)
                {
                    for (var j = _commitIndex + 1; j <= i; j++) _log[j] = _log[j] with { Committed = true };
                    _commitIndex = i;
                    break;
                }
            }
        }
    }

    private static RaftNodeView Snapshot(Node n) => new(n.Id, n.Role, n.Term, n.MatchIndex, n.Alive);

    private sealed class Node
    {
        public string Id = "";
        public RaftRole Role;
        public long Term;
        public int MatchIndex = -1;
        public bool Alive = true;
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { /* 무시 */ }
    }
}
