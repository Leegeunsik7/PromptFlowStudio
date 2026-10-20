using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using PromptFlowStudio.Services.Sovereign.Pqc;

namespace PromptFlowStudio.Services.AgentFederation;

public enum FedAgentRole : byte { Director, Audio, Vfx, Sre, Asset }

public enum ProposalKind : byte { ControlTransfer, DataExchange, KnowledgeCommit }

public enum ConsensusOutcome : byte { Committed, Aborted, InvalidProposal, Replay }

/// <summary>합의 제안 — canonical 바이트(집계: 등록 에이전트 수 · 정족수 포함)에 ML-DSA-65 서명.</summary>
public sealed record FederationProposal(long Id, ProposalKind Kind, int Proposer, int From, int To, int Resource, float Value, float Baseline,
    byte[] Digest, Guid Nonce, long Epoch);

/// <summary>서명 투표(제안 SHA-256 ‖ 투표자 ‖ 값).</summary>
public sealed record SignedVote(int Voter, long ProposalId, bool Accept, byte[] Signature);

public sealed record ConsensusResult(long ProposalId, ProposalKind Kind, ConsensusOutcome Outcome, int Accepts, int Rejects, int InvalidVotes,
    int Equivocations, int Quorum, byte[] Certificate, double Millis);

/// <summary>에이전트 신원 — 개인키는 신원 객체만 보유(해결기는 공개키만 등록).</summary>
public sealed class FederatedAgentIdentity
{
    private readonly byte[] _sk;

    internal FederatedAgentIdentity(int id, FedAgentRole role, byte[] pk, byte[] sk) { Id = id; Role = role; PublicKey = pk; _sk = sk; }

    public int Id { get; }
    public FedAgentRole Role { get; }
    public byte[] PublicKey { get; }

    public byte[] SignProposal(FederationProposal p, int agents) => MlDsa65.Sign(_sk, ZeroTrustInterAgentConsensusResolver.Canonical(p, agents), ZeroTrustInterAgentConsensusResolver.ProposalContext);

    public SignedVote Vote(FederationProposal p, int agents, bool accept)
        => new(Id, p.Id, accept, MlDsa65.Sign(_sk, ZeroTrustInterAgentConsensusResolver.VoteBytes(p, agents, Id, accept), ZeroTrustInterAgentConsensusResolver.VoteContext));
}

/// <summary>
/// [PromptFlow Studio v16.68.0] 🔐 제로트러스트 에이전트 간 보안 합의 프로토콜 — ML-DSA-65(FIPS 204 · 내장 PQC) 서명 + BFT 정족수.
/// 정족수 q = ⌊2n/3⌋ + 1(n = 등록 에이전트 · f = ⌊(n−1)/3⌋ 비잔틴 허용). 검증: 논스 재생 차단(상한 캐시) · 에포크 · 제안자 서명 ·
/// 투표 서명(위조 → 무효 · 평판 −0.4) · 이중 투표(동일 제안 상반 값 → 양표 폐기 + 즉시 격리) · 격리 에이전트 표 무시.
/// 커밋 인증서 = SHA-256(제안 canonical ‖ 투표자 순 유효 찬성 서명 ‖ 집계(찬성 · 반대 · n · q)). 비교는 FixedTimeEquals.
/// 서명/검증은 제어 평면(제어권 이관 · 데이터 교환 · 스웜 동기화) 전용 — 메시지 버스 핫패스와 분리.
/// </summary>
public sealed class ZeroTrustInterAgentConsensusResolver
{
    public const int MaxAgents = 16, NonceCapacity = 4096, MaxLog = 32;
    internal static readonly byte[] ProposalContext = "PFS-FED-PROPOSAL-1"u8.ToArray();
    internal static readonly byte[] VoteContext = "PFS-FED-VOTE-1"u8.ToArray();

    private readonly object _gate = new();
    private readonly byte[]?[] _pk = new byte[MaxAgents][];
    private readonly FedAgentRole[] _role = new FedAgentRole[MaxAgents];
    private readonly bool[] _quarantined = new bool[MaxAgents];
    private readonly float[] _reputation = new float[MaxAgents];
    private readonly HashSet<Guid> _nonces = new(NonceCapacity + 1);
    private readonly ConsensusResult[] _log = new ConsensusResult[MaxLog];
    private int _count, _logHead, _logCount;
    private long _epoch, _nextId, _committed, _aborted, _invalidProposals, _replays, _invalidVotes, _equivocations, _verifications;

    public event Action? OnChange;

    public int AgentCount { get { lock (_gate) return _count; } }
    public long Epoch { get { lock (_gate) return _epoch; } }
    public long Committed { get { lock (_gate) return _committed; } }
    public long Aborted { get { lock (_gate) return _aborted; } }
    public long InvalidProposals { get { lock (_gate) return _invalidProposals; } }
    public long Replays { get { lock (_gate) return _replays; } }
    public long InvalidVotes { get { lock (_gate) return _invalidVotes; } }
    public long Equivocations { get { lock (_gate) return _equivocations; } }
    public long Verifications { get { lock (_gate) return _verifications; } }
    public string? LastAction { get; private set; }

    public static int QuorumOf(int n) => 2 * n / 3 + 1;
    public int Quorum { get { lock (_gate) return QuorumOf(_count); } }
    public bool IsQuarantined(int a) { lock (_gate) return (uint)a < MaxAgents && _quarantined[a]; }
    public float ReputationOf(int a) { lock (_gate) return (uint)a < MaxAgents ? _reputation[a] : 0; }

    /// <summary>에이전트 등록 — 런타임 ML-DSA-65 키 쌍 생성(하드코딩 0).</summary>
    public FederatedAgentIdentity Register(FedAgentRole role)
    {
        var (pk, sk) = MlDsa65.GenerateKeyPair();
        lock (_gate)
        {
            if (_count >= MaxAgents) throw new InvalidOperationException("에이전트 상한");
            var id = _count++;
            _pk[id] = pk; _role[id] = role; _reputation[id] = 1f;
            return new FederatedAgentIdentity(id, role, pk, sk);
        }
    }

    public void Release(int agent)
    {
        lock (_gate) if ((uint)agent < MaxAgents) { _quarantined[agent] = false; _reputation[agent] = Math.Max(_reputation[agent], 0.5f); }
        LastAction = "🔓 에이전트 #" + agent + " 격리 해제";
        Raise();
    }

    public FederationProposal NewProposal(ProposalKind kind, int proposer, int from, int to, int resource, float value, float baseline, ReadOnlySpan<byte> digest)
    {
        long id, epoch;
        lock (_gate) { id = ++_nextId; epoch = _epoch; }
        return new FederationProposal(id, kind, proposer, from, to, resource, value, baseline, digest.ToArray(), Guid.NewGuid(), epoch);
    }

    internal static byte[] Canonical(FederationProposal p, int agents)
    {
        var b = new byte[16 + 8 + 1 + 4 * 4 + 8 + 16 + 8 + 4 + 4 + 2 + p.Digest.Length];
        var s = b.AsSpan();
        "PFS-FED-PROP-001"u8.CopyTo(s); var o = 16;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], p.Id); o += 8;
        s[o++] = (byte)p.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], p.Proposer); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], p.From); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], p.To); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], p.Resource); o += 4;
        BinaryPrimitives.WriteSingleLittleEndian(s[o..], p.Value); o += 4;
        BinaryPrimitives.WriteSingleLittleEndian(s[o..], p.Baseline); o += 4;
        p.Nonce.TryWriteBytes(s[o..]); o += 16;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], p.Epoch); o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], agents); o += 4;           // 집계: 등록 수
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], QuorumOf(agents)); o += 4; // 집계: 정족수
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)p.Digest.Length); o += 2;
        p.Digest.CopyTo(s[o..]);
        return b;
    }

    internal static byte[] VoteBytes(FederationProposal p, int agents, int voter, bool accept)
    {
        var b = new byte[32 + 4 + 1];
        SHA256.HashData(Canonical(p, agents), b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), voter);
        b[36] = accept ? (byte)1 : (byte)0;
        return b;
    }

    /// <summary>제안 + 투표 판정.</summary>
    public ConsensusResult Resolve(FederationProposal p, ReadOnlySpan<byte> proposerSignature, IReadOnlyList<SignedVote> votes)
    {
        var t0 = Stopwatch.GetTimestamp();
        int n;
        byte[]?[] keys = new byte[MaxAgents][];
        bool[] quarantined = new bool[MaxAgents];
        lock (_gate)
        {
            n = _count;
            Array.Copy(_pk, keys, MaxAgents);
            Array.Copy(_quarantined, quarantined, MaxAgents);
            if (!_nonces.Add(p.Nonce))
            {
                _replays++;
                return Finish(new ConsensusResult(p.Id, p.Kind, ConsensusOutcome.Replay, 0, 0, 0, 0, QuorumOf(n), Array.Empty<byte>(), 0), "♻ 재생 제안 차단 #" + p.Id);
            }
            if (_nonces.Count >= NonceCapacity) _nonces.Clear();
        }
        var q = QuorumOf(n);
        var canonical = Canonical(p, n);
        var proposerOk = (uint)p.Proposer < (uint)n && !quarantined[p.Proposer] && p.Epoch == Epoch
            && MlDsa65.Verify(keys[p.Proposer]!, canonical, proposerSignature, ProposalContext);
        lock (_gate) _verifications++;
        if (!proposerOk)
        {
            lock (_gate) { _invalidProposals++; if ((uint)p.Proposer < (uint)n) Penalize(p.Proposer, 0.4f); }
            return Finish(new ConsensusResult(p.Id, p.Kind, ConsensusOutcome.InvalidProposal, 0, 0, 0, 0, q, Array.Empty<byte>(), Ms(t0)), "⛔ 제안 서명/에포크 무효 #" + p.Id);
        }

        var seen = new sbyte[MaxAgents]; // 0 없음 · 1 찬성 · 2 반대 · 3 이중 투표
        var sigs = new byte[MaxAgents][];
        int invalid = 0, equiv = 0;
        foreach (var v in votes)
        {
            if ((uint)v.Voter >= (uint)n || quarantined[v.Voter] || v.ProposalId != p.Id) { invalid++; continue; }
            var ok = MlDsa65.Verify(keys[v.Voter]!, VoteBytes(p, n, v.Voter, v.Accept), v.Signature, VoteContext);
            lock (_gate) _verifications++;
            if (!ok) { invalid++; lock (_gate) Penalize(v.Voter, 0.4f); continue; }
            var code = (sbyte)(v.Accept ? 1 : 2);
            if (seen[v.Voter] == 0) { seen[v.Voter] = code; sigs[v.Voter] = v.Signature; }
            else if (seen[v.Voter] != code && seen[v.Voter] != 3)
            {
                seen[v.Voter] = 3; equiv++;
                lock (_gate) { _quarantined[v.Voter] = true; _reputation[v.Voter] = 0; }
            }
        }
        int acc = 0, rej = 0;
        for (var a = 0; a < n; a++) { if (seen[a] == 1) acc++; else if (seen[a] == 2) rej++; }
        var outcome = acc >= q ? ConsensusOutcome.Committed : ConsensusOutcome.Aborted;
        byte[] cert = Array.Empty<byte>();
        if (outcome == ConsensusOutcome.Committed)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            h.AppendData(canonical);
            for (var a = 0; a < n; a++) if (seen[a] == 1) h.AppendData(sigs[a]);
            Span<byte> agg = stackalloc byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(agg, acc); BinaryPrimitives.WriteInt32LittleEndian(agg[4..], rej);
            BinaryPrimitives.WriteInt32LittleEndian(agg[8..], n); BinaryPrimitives.WriteInt32LittleEndian(agg[12..], q);
            h.AppendData(agg);
            cert = h.GetHashAndReset();
        }
        lock (_gate)
        {
            _invalidVotes += invalid; _equivocations += equiv;
            if (outcome == ConsensusOutcome.Committed) { _committed++; if (p.Kind == ProposalKind.KnowledgeCommit) _epoch++; }
            else _aborted++;
        }
        var r = new ConsensusResult(p.Id, p.Kind, outcome, acc, rej, invalid, equiv, q, cert, Ms(t0));
        return Finish(r, (outcome == ConsensusOutcome.Committed ? "✅ 합의 커밋 #" : "✖ 합의 중단 #") + p.Id + " · " + KindLabel(p.Kind) + " " + acc + "/" + q);
    }

    /// <summary>인증서 재계산 검증(FixedTimeEquals).</summary>
    public bool VerifyCertificate(FederationProposal p, ConsensusResult r, IReadOnlyList<SignedVote> acceptedVotes, int agents)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Canonical(p, agents));
        var ordered = new SignedVote?[MaxAgents];
        foreach (var v in acceptedVotes) if (v.Accept && (uint)v.Voter < MaxAgents) ordered[v.Voter] = v;
        foreach (var v in ordered) if (v is not null) h.AppendData(v.Signature);
        Span<byte> agg = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(agg, r.Accepts); BinaryPrimitives.WriteInt32LittleEndian(agg[4..], r.Rejects);
        BinaryPrimitives.WriteInt32LittleEndian(agg[8..], agents); BinaryPrimitives.WriteInt32LittleEndian(agg[12..], r.Quorum);
        h.AppendData(agg);
        var c = h.GetHashAndReset();
        return r.Certificate.Length == c.Length && CryptographicOperations.FixedTimeEquals(c, r.Certificate);
    }

    private void Penalize(int a, float by)
    {
        _reputation[a] = MathF.Max(0, _reputation[a] - by);
        if (_reputation[a] < 0.3f) _quarantined[a] = true;
    }

    private static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private ConsensusResult Finish(ConsensusResult r, string action)
    {
        lock (_gate)
        {
            _log[_logHead] = r; _logHead = (_logHead + 1) % MaxLog;
            if (_logCount < MaxLog) _logCount++;
        }
        LastAction = action;
        Raise();
        return r;
    }

    public int CopyLog(Span<ConsensusResult?> dst)
    {
        lock (_gate)
        {
            var c = Math.Min(dst.Length, _logCount);
            for (var i = 0; i < c; i++) dst[i] = _log[(_logHead - 1 - i + MaxLog * 2) % MaxLog];
            return c;
        }
    }

    public static string KindLabel(ProposalKind k) => k switch { ProposalKind.ControlTransfer => "제어권 이관", ProposalKind.DataExchange => "데이터 교환", _ => "지식 커밋" };

    public static string RoleLabel(FedAgentRole r) => r switch
    {
        FedAgentRole.Director => "연출", FedAgentRole.Audio => "음향", FedAgentRole.Vfx => "VFX", FedAgentRole.Sre => "SRE", _ => "자산 관리",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Federation.Consensus.OnChange", ex); }
    }
}
