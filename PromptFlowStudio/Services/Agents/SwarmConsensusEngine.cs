using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PromptFlowStudio.Services.Security;   // SecureSecretVault(v10.5.0-F1)

namespace PromptFlowStudio.Services.Agents;

public enum ConsensusAgentRole { Proposer, Voter, Byzantine, Down }

public enum ConsensusPhase { Idle, PrePrepare, Prepare, Commit, Committed, Aborted }

public sealed record ConsensusAgentView(string Id, ConsensusAgentRole Role, bool Responsive, int Strikes, long VotesCast);

public sealed record ConsensusRoundResult(long Round, ConsensusPhase Phase, bool Committed, int PrepareVotes, int CommitVotes, int Quorum, int Total, string ProposalDigest8, double ElapsedMs, string Note);

/// <summary>
/// v9.9.1 스웜 합의 엔진 — N=3f+1 에이전트 PBFT 3단계(Pre-Prepare→Prepare→Commit) 제안 합의.
/// <see cref="Integration.DistributedEdgeConsensusMesh"/>(Raft+BFT 로그 복제)와 달리 상태 로그가 아니라
/// 단발성 "행동 제안"(예: 렌더 정책 변경)에 대해 2f+1 쿼럼 투표로 합의하고, 비잔틴/무응답 에이전트를
/// 스트라이크 누적으로 격리 후 뷰체인지(제안자 교체)한다. 순수 BCL(SHA-256/HMAC 서명 스텁).
/// </summary>
public sealed class SwarmConsensusEngine : IDisposable
{
    public const int TickMs = 300;
    private const int MaxStrikes = 3;
    // v10.5.0-F1: 하드코딩 리터럴 → SecureSecretVault 이관(환경변수→DPAPI→Graceful 폴백). CLAUDE.md Rule 7 소급 적용.
    private readonly byte[] SignKey;

    private sealed class Agent
    {
        public Agent(string id) { Id = id; }
        public string Id { get; }
        public ConsensusAgentRole Role = ConsensusAgentRole.Voter;
        public bool Byzantine;
        public bool Down;
        public int Strikes;
        public long Votes;
    }

    private readonly object _gate = new();
    private readonly List<Agent> _agents = new();
    private readonly Random _rng = new(9901);
    private System.Threading.Timer? _timer;
    private long _round;
    private string _proposerId = "";
    private long _rounds, _committed, _aborted, _viewChanges, _isolations;
    private bool _disposed;

    public event Action? OnChange;

    public SwarmConsensusEngine(SecureSecretVault vault)
    {
        SignKey = vault.ResolveOrCreate("swarm-consensus-engine");
        for (var i = 1; i <= 7; i++) _agents.Add(new Agent($"agent-{i}"));   // N=7 → f=2 허용(2f+1=5 쿼럼)
        _agents[0].Role = ConsensusAgentRole.Proposer;
        _proposerId = _agents[0].Id;
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Rounds => Interlocked.Read(ref _rounds);
    public long CommittedCount => Interlocked.Read(ref _committed);
    public long AbortedCount => Interlocked.Read(ref _aborted);
    public long ViewChanges => Interlocked.Read(ref _viewChanges);
    public long Isolations => Interlocked.Read(ref _isolations);
    public string ProposerId { get { lock (_gate) return _proposerId; } }

    public IReadOnlyList<ConsensusAgentView> Agents
    {
        get
        {
            lock (_gate)
            {
                var list = new List<ConsensusAgentView>(_agents.Count);
                foreach (var a in _agents) list.Add(new ConsensusAgentView(a.Id, a.Role, !a.Down, a.Strikes, a.Votes));
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
            LastAction = $"합의 라운드 루프 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "합의 루프 정지"; }
        OnChange?.Invoke();
    }

    /// <summary>무작위 비-제안자 에이전트를 비잔틴(상반된 표 이중 제출)으로 전환.</summary>
    public void InjectByzantine()
    {
        lock (_gate)
        {
            var cand = _agents.Find(a => a.Id != _proposerId && !a.Byzantine && !a.Down);
            if (cand is null) return;
            cand.Byzantine = true; cand.Role = ConsensusAgentRole.Byzantine;
            LastAction = $"{cand.Id} 비잔틴 전환(이중 투표 주입)";
        }
        OnChange?.Invoke();
    }

    /// <summary>현재 제안자를 무응답(다운)으로 전환 → 뷰체인지 유발.</summary>
    public void InjectProposerDown()
    {
        lock (_gate)
        {
            var p = _agents.Find(a => a.Id == _proposerId);
            if (p is null) return;
            p.Down = true; p.Role = ConsensusAgentRole.Down;
            LastAction = $"제안자 {p.Id} 무응답 주입";
        }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            foreach (var a in _agents) { a.Role = ConsensusAgentRole.Voter; a.Byzantine = false; a.Down = false; a.Strikes = 0; a.Votes = 0; }
            _agents[0].Role = ConsensusAgentRole.Proposer;
            _proposerId = _agents[0].Id;
            _round = 0;
            LastAction = "스웜 합의 초기화";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        ConsensusRoundResult _;
        lock (_gate) { if (_timer is null) return; _ = RunRoundLocked(); }
        OnChange?.Invoke();
    }

    public ConsensusRoundResult RunRound()
    {
        ConsensusRoundResult r;
        lock (_gate) r = RunRoundLocked();
        OnChange?.Invoke();
        return r;
    }

    private ConsensusRoundResult RunRoundLocked()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _round++; _rounds++;
        var proposer = _agents.Find(a => a.Id == _proposerId);
        if (proposer is null || proposer.Down)
        {
            var next = ViewChangeLocked();
            sw.Stop();
            return new ConsensusRoundResult(_round, ConsensusPhase.Aborted, false, 0, 0, Quorum, Total, "--------", sw.Elapsed.TotalMilliseconds, $"뷰체인지 → {next}");
        }

        var payload = Encoding.UTF8.GetBytes($"proposal:{_round}:{proposer.Id}");
        var digest = Hex8(SHA256.HashData(payload));
        var sig = HMACSHA256.HashData(SignKey, payload);   // 제안 서명(PQC 스텁)

        // Prepare 단계: 각 보팅 에이전트가 서명 검증 후 투표(비잔틴은 이중/상반 투표로 1건만 유효 인정)
        var prepareVotes = 0;
        foreach (var a in _agents)
        {
            if (a.Down) continue;
            var valid = CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(SignKey, payload), sig);
            if (!valid) continue;
            if (a.Byzantine)
            {
                // 비잔틴: 50% 확률로 기권/상반 투표(정직 표 1개로만 카운트되도록 상한 처리)
                if (_rng.NextDouble() < 0.5) { a.Strikes++; continue; }
            }
            prepareVotes++; a.Votes++;
        }

        var quorum = Quorum;
        if (prepareVotes < quorum)
        {
            PenalizeByzantineLocked();
            sw.Stop();
            _aborted++;
            return new ConsensusRoundResult(_round, ConsensusPhase.Prepare, false, prepareVotes, 0, quorum, Total, digest, sw.Elapsed.TotalMilliseconds, "Prepare 쿼럼 미달 — 중단");
        }

        // Commit 단계: Prepare 쿼럼 확보 에이전트가 커밋 투표
        var commitVotes = 0;
        foreach (var a in _agents)
            if (!a.Down && (!a.Byzantine || _rng.NextDouble() >= 0.5)) commitVotes++;

        var committed = commitVotes >= quorum;
        if (committed) _committed++; else _aborted++;
        PenalizeByzantineLocked();
        sw.Stop();
        LastAction = committed ? $"라운드 {_round} 커밋 완료 · {digest}" : $"라운드 {_round} Commit 쿼럼 미달";
        return new ConsensusRoundResult(_round, committed ? ConsensusPhase.Committed : ConsensusPhase.Aborted, committed, prepareVotes, commitVotes, quorum, Total, digest, sw.Elapsed.TotalMilliseconds, LastAction);
    }

    // 스트라이크 3회 누적 비잔틴 에이전트는 격리(Down 전환) — 쿼럼 산정에서 제외.
    private void PenalizeByzantineLocked()
    {
        foreach (var a in _agents)
        {
            if (a.Byzantine && a.Strikes >= MaxStrikes && !a.Down)
            {
                a.Down = true;
                _isolations++;
                LastAction = $"{a.Id} 스트라이크 {a.Strikes}회 — 격리";
            }
        }
    }

    private string ViewChangeLocked()
    {
        var next = _agents.Find(a => a.Id != _proposerId && !a.Down && !a.Byzantine);
        if (next is null) { LastAction = "뷰체인지 불가 — 건강 후보 없음"; return _proposerId; }
        var prev = _agents.Find(a => a.Id == _proposerId);
        if (prev is not null && prev.Role == ConsensusAgentRole.Proposer) prev.Role = prev.Down ? ConsensusAgentRole.Down : ConsensusAgentRole.Voter;
        next.Role = ConsensusAgentRole.Proposer;
        _proposerId = next.Id;
        _viewChanges++;
        LastAction = $"뷰체인지 → 신규 제안자 {next.Id}";
        return next.Id;
    }

    private int Total => _agents.Count;
    private int Quorum
    {
        get
        {
            var healthy = 0;
            foreach (var a in _agents) if (!a.Down) healthy++;
            var f = (Total - 1) / 3;              // 비잔틴 내성 한계
            return Math.Max(2 * f + 1, healthy / 2 + 1);
        }
    }

    private static string Hex8(byte[] h)
    {
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4 && i < h.Length; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
