using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

public enum EdgeConsNodeRole { Follower, Leader, Isolated, Down }

public enum EdgeConsFault { None, Byzantine, Crash }

/// <summary>합의 원장 엔트리 — 이전 해시 체인 + 리더 HMAC 서명.</summary>
public sealed record EdgeConsEntry(long Term, long Index, int FrameIndex, string StateDigest, string PrevHash, string LeaderId, string Signature, string Hash = "");

public sealed record EdgeConsNodeView(string Id, EdgeConsNodeRole Role, EdgeConsFault Fault, long Term, long CommitIndex, int Strikes, long VotesCast);

public sealed record EdgeConsRoundResult(
    bool Committed, long Term, long Index, int FrameIndex, string? LeaderId,
    int Accepts, int Rejects, int Silent, int Quorum, int Members,
    IReadOnlyList<string> NewlyIsolated, bool LeaderRotated, int Synced, double ElapsedMs, string Reason);

/// <summary>
/// v9.5.2 분산 엣지 합의 메시 — Raft 리더 선출(최신 로그 우선) + BFT 쿼럼(n≥3f+1, 2f+1 서명 투표).
/// 노드별 <see cref="Channel{T}"/> 인박스로 Prepare/Vote 를 비동기 교환하고, 서명 위조·다중 다이제스트(equivocation)
/// 노드를 증거 기반 자가 격리한다. 악성 리더는 f+1 정직 거부로 탄핵 후 재선출·재시도. 지연 노드는 커밋 헤드로 상태 동기화. 순수 BCL.
/// </summary>
public sealed class DistributedEdgeConsensusMesh : IDisposable
{
    public const int ReplyTimeoutMs = 150;
    public const int StrikeLimit = 2;
    private const int MaxLog = 64;
    private const string Genesis = "GENESIS";

    private sealed class MeshNode
    {
        public MeshNode(string id, byte[] key)
        {
            Id = id;
            Key = key;
            Inbox = Channel.CreateBounded<Prepare>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        }

        public string Id { get; }
        public byte[] Key { get; }
        public Channel<Prepare> Inbox { get; }
        public EdgeConsNodeRole Role;
        public EdgeConsFault Fault;
        public long Term;
        public long CommitIndex;
        public string LastHash = Genesis;
        public int Strikes;
        public long VotesCast;
    }

    private readonly record struct Prepare(EdgeConsEntry Entry, byte[] LeaderKey, ChannelWriter<Vote> Reply);
    private readonly record struct Vote(string NodeId, long Index, string Digest, bool Accept, string Signature);

    private readonly object _gate = new();
    private readonly List<MeshNode> _nodes = new();
    private readonly List<EdgeConsEntry> _log = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly byte[] _clusterSecret = RandomNumberGenerator.GetBytes(32);
    private CancellationTokenSource _cts = new();
    private MeshNode? _leader;
    private long _term, _commitIndex, _committed, _failed, _isolatedTotal, _elections, _synced;
    private int _frame;
    private string _chainHead = Genesis;
    private EdgeConsRoundResult? _last;
    private bool _disposed;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public EdgeConsRoundResult? LastRound { get { lock (_gate) return _last; } }
    public string? LeaderId { get { lock (_gate) return _leader?.Id; } }
    public long Term { get { lock (_gate) return _term; } }
    public long CommitIndex { get { lock (_gate) return _commitIndex; } }
    public long CommittedCount { get { lock (_gate) return _committed; } }
    public long FailedRounds { get { lock (_gate) return _failed; } }
    public long IsolatedTotal { get { lock (_gate) return _isolatedTotal; } }
    public long Elections { get { lock (_gate) return _elections; } }
    public long SyncedCount { get { lock (_gate) return _synced; } }
    public string ChainHead { get { lock (_gate) return _chainHead; } }
    public int Members { get { lock (_gate) return MembersLocked(); } }
    public int Quorum { get { lock (_gate) return QuorumFor(MembersLocked()); } }

    public IReadOnlyList<EdgeConsNodeView> Nodes
    {
        get
        {
            lock (_gate)
            {
                var list = new List<EdgeConsNodeView>(_nodes.Count);
                foreach (var n in _nodes)
                    list.Add(new EdgeConsNodeView(n.Id, n.Role, n.Fault, n.Term, n.CommitIndex, n.Strikes, Interlocked.Read(ref n.VotesCast)));
                return list;
            }
        }
    }

    public IReadOnlyList<EdgeConsEntry> Log { get { lock (_gate) return _log.ToArray(); } }

    /// <summary>n 노드 클러스터 구성(4~16) — 노드 루프 기동 + 초기 리더 선출.</summary>
    public void SeedCluster(int size = 7)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        size = Math.Clamp(size, 4, 16);
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _cts;
            _cts = new CancellationTokenSource();
            foreach (var n in _nodes) n.Inbox.Writer.TryComplete();
            _nodes.Clear();
            _log.Clear();
            _leader = null;
            _term = _commitIndex = _committed = _failed = _isolatedTotal = _elections = _synced = 0;
            _frame = 0;
            _chainHead = Genesis;
            _last = null;
            for (var i = 0; i < size; i++)
                _nodes.Add(new MeshNode($"edge-{i}", HMACSHA256.HashData(_clusterSecret, Encoding.UTF8.GetBytes("node:" + i))));
            var ct = _cts.Token;
            foreach (var n in _nodes)
            {
                var node = n;
                _ = Task.Run(() => NodeLoopAsync(node, ct));
            }
            ElectLocked();
            LastAction = $"{size}노드 클러스터 · f={FaultTolerance(size)} · 쿼럼 {QuorumFor(size)} · 리더 {_leader?.Id}";
        }
        old.Cancel();
        old.Dispose();
        OnChange?.Invoke();
    }

    public Task<EdgeConsRoundResult> ProposeDemoAsync(CancellationToken ct = default)
    {
        var frame = Interlocked.Increment(ref _frame);
        var state = SHA256.HashData(Encoding.UTF8.GetBytes("frame-state:" + frame));
        return ProposeFrameAsync(frame, state, ct);
    }

    /// <summary>프레임 상태 합의 라운드 — 커밋 시 전 수락 노드 커밋 인덱스 동기.</summary>
    public async Task<EdgeConsRoundResult> ProposeFrameAsync(int frameIndex, ReadOnlyMemory<byte> state, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _roundGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await RunRoundAsync(frameIndex, state, sw, false, ct).ConfigureAwait(false);
            // 악성 리더 탄핵 → 재선출 리더로 1회 재시도
            if (!result.Committed && result.LeaderRotated)
                result = await RunRoundAsync(frameIndex, state, sw, true, ct).ConfigureAwait(false);
            lock (_gate) _last = result;
            LastAction = result.Reason;
            OnChange?.Invoke();
            return result;
        }
        finally { _roundGate.Release(); }
    }

    private async Task<EdgeConsRoundResult> RunRoundAsync(int frameIndex, ReadOnlyMemory<byte> state, Stopwatch sw, bool rotated, CancellationToken ct)
    {
        MeshNode leader;
        EdgeConsEntry entry;
        List<MeshNode> peers;
        int members, synced = 0;
        var replies = Channel.CreateUnbounded<Vote>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            if (_nodes.Count == 0)
                return new EdgeConsRoundResult(false, 0, 0, frameIndex, null, 0, 0, 0, 0, 0, Array.Empty<string>(), rotated, 0, sw.Elapsed.TotalMilliseconds, "클러스터 미구성");
            if (_leader is null || _leader.Role != EdgeConsNodeRole.Leader || _leader.Fault == EdgeConsFault.Crash) ElectLocked();
            members = MembersLocked();
            if (_leader is null)
            {
                _failed++;
                return new EdgeConsRoundResult(false, _term, _commitIndex, frameIndex, null, 0, 0, 0, QuorumFor(members), members,
                    Array.Empty<string>(), rotated, 0, sw.Elapsed.TotalMilliseconds, "정족수 미달 — 리더 선출 불가");
            }
            leader = _leader;

            // 지연 노드 상태 동기화(커밋 헤드 스냅샷 전송)
            foreach (var n in _nodes)
            {
                if (n.Fault == EdgeConsFault.Crash || n.Role == EdgeConsNodeRole.Isolated) continue;
                if (n.Role == EdgeConsNodeRole.Down) n.Role = EdgeConsNodeRole.Follower;
                n.Term = _term;
                if (n.CommitIndex >= _commitIndex) continue;
                n.CommitIndex = _commitIndex;
                n.LastHash = _chainHead;
                synced++;
            }
            _synced += synced;

            var index = _commitIndex + 1;
            var digest = Convert.ToHexString(SHA256.HashData(state.Span));
            var canonical = EntryCanonical(_term, index, frameIndex, digest, _chainHead, leader.Id);
            var signature = leader.Fault == EdgeConsFault.Byzantine
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                : Sign(leader.Key, canonical);
            entry = new EdgeConsEntry(_term, index, frameIndex, digest, _chainHead, leader.Id, signature);
            peers = new List<MeshNode>(_nodes.Count);
            foreach (var n in _nodes)
                if (n != leader && n.Role != EdgeConsNodeRole.Isolated) peers.Add(n);
            foreach (var p in peers) p.Inbox.Writer.TryWrite(new Prepare(entry, leader.Key, replies.Writer));
        }

        var votes = new List<Vote>(peers.Count);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ReplyTimeoutMs);
            try
            {
                while (votes.Count < peers.Count && await replies.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                    while (votes.Count < peers.Count && replies.Reader.TryRead(out var v)) votes.Add(v);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        replies.Writer.TryComplete();

        lock (_gate)
        {
            var quorum = QuorumFor(members);
            var accepts = 1; // 리더 자기 투표
            var rejects = 0;
            var impeach = 0;
            var isolated = new List<string>();
            var accepted = new List<MeshNode>(votes.Count + 1) { leader };
            var replied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in votes)
            {
                replied.Add(v.NodeId);
                var node = FindLocked(v.NodeId);
                if (node is null || node.Role == EdgeConsNodeRole.Isolated) continue;
                var sigOk = Verify(node.Key, VoteCanonical(v.NodeId, v.Index, v.Digest, v.Accept), v.Signature);
                if (!sigOk) { rejects++; StrikeLocked(node, 1, isolated); continue; }
                // 서명된 상충 다이제스트 = equivocation 증거 → 즉시 격리
                if (v.Index != entry.Index || !string.Equals(v.Digest, entry.StateDigest, StringComparison.Ordinal))
                { rejects++; StrikeLocked(node, StrikeLimit, isolated); continue; }
                if (v.Accept) { accepts++; accepted.Add(node); }
                else { rejects++; impeach++; }
            }
            var silent = 0;
            foreach (var p in peers)
            {
                if (replied.Contains(p.Id)) continue;
                silent++;
                if (p.Role != EdgeConsNodeRole.Isolated) p.Role = EdgeConsNodeRole.Down;
            }

            if (accepts >= quorum)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    EntryCanonical(entry.Term, entry.Index, entry.FrameIndex, entry.StateDigest, entry.PrevHash, entry.LeaderId) + "|" + entry.Signature)));
                _log.Add(entry with { Hash = hash });
                if (_log.Count > MaxLog) _log.RemoveAt(0);
                _chainHead = hash;
                _commitIndex = entry.Index;
                _committed++;
                foreach (var n in accepted) { n.CommitIndex = entry.Index; n.LastHash = hash; }
                return new EdgeConsRoundResult(true, entry.Term, entry.Index, frameIndex, leader.Id, accepts, rejects, silent, quorum, members,
                    isolated, rotated, synced, Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    $"커밋 #{entry.Index} · {accepts}/{members} (쿼럼 {quorum})" + (isolated.Count > 0 ? $" · 격리 {string.Join(",", isolated)}" : string.Empty));
            }

            _failed++;
            var rotate = impeach >= FaultTolerance(members) + 1 && leader.Role != EdgeConsNodeRole.Isolated;
            if (rotate)
            {
                leader.Strikes = StrikeLimit;
                leader.Role = EdgeConsNodeRole.Isolated;
                isolated.Add(leader.Id);
                _isolatedTotal++;
                _leader = null;
                ElectLocked();
            }
            return new EdgeConsRoundResult(false, entry.Term, entry.Index, frameIndex, leader.Id, accepts, rejects, silent, quorum, members,
                isolated, rotated || rotate, synced, Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                rotate ? $"리더 {leader.Id} 서명 위조 탄핵 · 신임 {_leader?.Id ?? "없음"}" : $"쿼럼 미달 {accepts}/{quorum}");
        }
    }

    private static async Task NodeLoopAsync(MeshNode node, CancellationToken ct)
    {
        var reader = node.Inbox.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (reader.TryRead(out var msg)) HandlePrepare(node, msg);
        }
        catch (OperationCanceledException) { }
    }

    private static void HandlePrepare(MeshNode node, in Prepare msg)
    {
        if (node.Fault == EdgeConsFault.Crash || node.Role == EdgeConsNodeRole.Isolated) return;
        var e = msg.Entry;
        var cast = Interlocked.Increment(ref node.VotesCast);
        Vote vote;
        if (node.Fault == EdgeConsFault.Byzantine)
        {
            // 악성 행위: 짝수 차례 상충 다이제스트 서명(equivocation), 홀수 차례 서명 위조
            if ((cast & 1) == 0)
            {
                var forged = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.StateDigest + node.Id)));
                vote = new Vote(node.Id, e.Index, forged, true, Sign(node.Key, VoteCanonical(node.Id, e.Index, forged, true)));
            }
            else vote = new Vote(node.Id, e.Index, e.StateDigest, true, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        }
        else
        {
            var ok = Verify(msg.LeaderKey, EntryCanonical(e.Term, e.Index, e.FrameIndex, e.StateDigest, e.PrevHash, e.LeaderId), e.Signature)
                     && e.Term >= node.Term
                     && string.Equals(e.PrevHash, node.LastHash, StringComparison.Ordinal);
            vote = new Vote(node.Id, e.Index, e.StateDigest, ok, Sign(node.Key, VoteCanonical(node.Id, e.Index, e.StateDigest, ok)));
        }
        msg.Reply.TryWrite(vote);
    }

    /// <summary>임의 팔로워를 비잔틴(악성) 노드로 전환.</summary>
    public string? InjectByzantine() => InjectFault(EdgeConsFault.Byzantine, false);

    /// <summary>현 리더를 비잔틴으로 전환 — 다음 라운드 탄핵·재선출 실측.</summary>
    public string? InjectByzantineLeader() => InjectFault(EdgeConsFault.Byzantine, true);

    public string? CrashRandom() => InjectFault(EdgeConsFault.Crash, false);

    private string? InjectFault(EdgeConsFault fault, bool leader)
    {
        string? id = null;
        lock (_gate)
        {
            MeshNode? target = leader ? _leader : null;
            if (!leader)
            {
                var pool = _nodes.FindAll(n => n != _leader && n.Fault == EdgeConsFault.None && n.Role == EdgeConsNodeRole.Follower);
                if (pool.Count > 0) target = pool[Random.Shared.Next(pool.Count)];
            }
            if (target is not null) { target.Fault = fault; id = target.Id; }
            LastAction = id is null ? "주입 대상 없음" : $"{id} ← {(fault == EdgeConsFault.Crash ? "크래시" : "비잔틴")} 주입";
        }
        OnChange?.Invoke();
        return id;
    }

    /// <summary>전 노드 결함 해제·격리 재편입(운영자 승인) + 상태 동기화.</summary>
    public void HealAll()
    {
        lock (_gate)
        {
            foreach (var n in _nodes)
            {
                n.Fault = EdgeConsFault.None;
                n.Strikes = 0;
                if (n.Role != EdgeConsNodeRole.Leader) n.Role = EdgeConsNodeRole.Follower;
                n.Term = _term;
                n.CommitIndex = _commitIndex;
                n.LastHash = _chainHead;
            }
            if (_leader is null) ElectLocked();
            LastAction = $"전 노드 복구 · 리더 {_leader?.Id ?? "없음"} · 헤드 동기화";
        }
        OnChange?.Invoke();
    }

    /// <summary>데모 위조: 커밋 원장 중간 엔트리의 상태 다이제스트 변조.</summary>
    public bool TamperLog()
    {
        bool done;
        lock (_gate)
        {
            done = _log.Count > 0;
            if (done)
            {
                var i = _log.Count / 2;
                _log[i] = _log[i] with { StateDigest = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
            }
            LastAction = done ? "원장 엔트리 변조 주입" : "원장 비어 있음";
        }
        OnChange?.Invoke();
        return done;
    }

    /// <summary>원장 체인 검증 — 해시 연결·리더 서명·헤드 일치.</summary>
    public bool VerifyChain()
    {
        lock (_gate)
        {
            if (_log.Count == 0) return true;
            var prev = _log[0].PrevHash;
            foreach (var e in _log)
            {
                if (!string.Equals(e.PrevHash, prev, StringComparison.Ordinal)) return false;
                var canonical = EntryCanonical(e.Term, e.Index, e.FrameIndex, e.StateDigest, e.PrevHash, e.LeaderId);
                var leader = FindLocked(e.LeaderId);
                if (leader is null || !Verify(leader.Key, canonical, e.Signature)) return false;
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "|" + e.Signature)));
                if (!string.Equals(hash, e.Hash, StringComparison.Ordinal)) return false;
                prev = hash;
            }
            return string.Equals(prev, _chainHead, StringComparison.Ordinal);
        }
    }

    /// <summary>체인 정규화 — 엔트리 수·커밋/실패/격리 집계까지 바인딩.</summary>
    public string ChainCanonical()
    {
        lock (_gate)
            return $"DECM|log={_log.Count}|ci={_commitIndex}|c={_committed}|f={_failed}|iso={_isolatedTotal}|term={_term}|m={MembersLocked()}|head={_chainHead}";
    }

    public string ChainDigest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ChainCanonical())));

    private bool ElectLocked()
    {
        MeshNode? best = null;
        int reachable = 0, members = 0;
        foreach (var n in _nodes)
        {
            if (n.Role == EdgeConsNodeRole.Isolated) continue;
            members++;
            if (n.Fault == EdgeConsFault.Crash) { n.Role = EdgeConsNodeRole.Down; continue; }
            reachable++;
            // Raft 선출 제약: 최신 커밋 로그 보유 노드 우선
            if (best is null || n.CommitIndex > best.CommitIndex) best = n;
        }
        _term++;
        _elections++;
        foreach (var n in _nodes)
            if (n.Role is EdgeConsNodeRole.Leader or EdgeConsNodeRole.Follower) { n.Role = EdgeConsNodeRole.Follower; n.Term = _term; }
        if (best is null || reachable < members / 2 + 1) { _leader = null; return false; }
        best.Role = EdgeConsNodeRole.Leader;
        _leader = best;
        return true;
    }

    private void StrikeLocked(MeshNode node, int weight, List<string> isolated)
    {
        node.Strikes += weight;
        if (node.Strikes < StrikeLimit || node.Role == EdgeConsNodeRole.Isolated) return;
        node.Role = EdgeConsNodeRole.Isolated;
        isolated.Add(node.Id);
        _isolatedTotal++;
    }

    private MeshNode? FindLocked(string id)
    {
        foreach (var n in _nodes)
            if (string.Equals(n.Id, id, StringComparison.Ordinal)) return n;
        return null;
    }

    private int MembersLocked()
    {
        var m = 0;
        foreach (var n in _nodes) if (n.Role != EdgeConsNodeRole.Isolated) m++;
        return m;
    }

    private static int FaultTolerance(int members) => Math.Max(0, (members - 1) / 3);

    private static int QuorumFor(int members) => 2 * FaultTolerance(members) + 1;

    private static string EntryCanonical(long term, long index, int frame, string digest, string prev, string leader) =>
        $"ENT|t={term}|i={index}|fr={frame}|d={digest}|p={prev}|l={leader}";

    private static string VoteCanonical(string node, long index, string digest, bool accept) =>
        $"VOTE|n={node}|i={index}|d={digest}|a={(accept ? 1 : 0)}";

    private static string Sign(byte[] key, string canonical) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));

    private static bool Verify(byte[] key, string canonical, string signature)
    {
        if (signature.Length != 64) return false;
        byte[] given;
        try { given = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical), expected);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
            foreach (var n in _nodes) n.Inbox.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}
