using System.Security.Cryptography;

namespace PromptFlowStudio.Services.AgentFederation;

public enum AgentTaskKind : byte { Shot, Sound, Effect, Ops, Asset }

public enum BusMessageKind : byte { TaskOffer, Bid, Award, Done, Claim, Grant, Deny, Proposal, Vote, Commit, Abort, Sync }

public enum ByzantineMode : byte { Honest, ForgeSignature, Equivocate, Contrarian }

public enum ClaimOutcome : byte { Granted, Renewed, Denied, TransferredByConsensus, TransferRejected }

/// <summary>버스 메시지(값 형식 · 32B 내외) — To −1 = 브로드캐스트.</summary>
public readonly record struct BusMessage(long Seq, double AtMs, BusMessageKind Kind, sbyte From, sbyte To, int Ref, float Value);

/// <summary>에이전트 상태 스냅샷(UI).</summary>
public readonly record struct FedAgentStatus(int Id, FedAgentRole Role, int Node, float Load, int Active, long Completed, long Won, ByzantineMode Mode, bool Quarantined, float Reputation);

/// <summary>
/// [PromptFlow Studio v16.68.0] 🤖 멀티 에이전트 스웜 연합 오케스트레이터 — 연출 · 음향 · VFX · SRE · 자산 관리 5 에이전트.
/// 메시지 버스: 고정 링(4096 · 값 형식 메시지 · 단조 시퀀스) — 발행/구독 커서 · 무할당.
/// 작업 분담(Contract Net): 제안 브로드캐스트 → 에이전트별 입찰(역량 × 1/(1+부하/속도)) → 최고 입찰 낙찰(동률 = 낮은 id) · FIFO 처리 → 완료 시 지식 그래프 반영.
/// 의사결정 경합: 자원 리스(5s) 테이블 — 점수 = 도메인 권한 × 확신도. 도전자 점수가 소유자 + 0.1 초과 시 제로트러스트 합의(<see cref="ZeroTrustInterAgentConsensusResolver"/>)로 제어권 이관 ·
/// 정직 투표자는 권한 과대 주장(점수 &gt; 권한 행렬) 거부. 1-Click 스웜 동기화: 임베딩 전파 · 전이 추론 · 그래프 다이제스트 지식 커밋 합의.
/// </summary>
public sealed class MultiAgentSwarmFederationOrchestrator
{
    public const int Agents = 5, Kinds = 5, BusCapacity = 4096, MaxTasks = 256;
    public const double LeaseMs = 5000;
    public const float PreemptMargin = 0.1f;

    /// <summary>역량/도메인 권한 행렬 [에이전트, 작업 종류].</summary>
    public static readonly float[] Authority =
    {
        1.0f, 0.2f, 0.5f, 0.1f, 0.3f,   // 연출
        0.2f, 1.0f, 0.1f, 0.1f, 0.3f,   // 음향
        0.6f, 0.1f, 1.0f, 0.1f, 0.3f,   // VFX
        0.1f, 0.1f, 0.1f, 1.0f, 0.5f,   // SRE
        0.2f, 0.2f, 0.2f, 0.4f, 1.0f,   // 자산 관리
    };
    private static readonly float[] Speed = { 1.2f, 1.5f, 0.9f, 2f, 1.8f };

    private readonly object _gate = new();
    private readonly RealtimeSpatiotemporalKnowledgeGraph _kg;
    private readonly ZeroTrustInterAgentConsensusResolver _consensus;
    private readonly FederatedAgentIdentity[] _ids = new FederatedAgentIdentity[Agents];
    private readonly int[] _agentNode = new int[Agents];
    private readonly ByzantineMode[] _mode = new ByzantineMode[Agents];
    private readonly ulong[] _localDigest = new ulong[Agents];
    private readonly long[] _completed = new long[Agents], _won = new long[Agents];
    private readonly BusMessage[] _bus = new BusMessage[BusCapacity];
    // 작업
    private readonly AgentTaskKind[] _taskKind = new AgentTaskKind[MaxTasks];
    private readonly float[] _remaining = new float[MaxTasks];
    private readonly int[] _assignee = new int[MaxTasks], _taskRes = new int[MaxTasks];
    private readonly double[] _submittedAt = new double[MaxTasks];
    private readonly byte[] _taskState = new byte[MaxTasks]; // 0 빈 · 1 제안 · 2 배정
    // 자원 리스
    private readonly int[] _owner = new int[RealtimeSpatiotemporalKnowledgeGraph.MaxNodes];
    private readonly float[] _ownerScore = new float[RealtimeSpatiotemporalKnowledgeGraph.MaxNodes];
    private readonly double[] _leaseUntil = new double[RealtimeSpatiotemporalKnowledgeGraph.MaxNodes];
    private long _seq, _cursor, _tasksSubmitted, _tasksDone, _bids, _claims, _conflicts, _preemptions, _denied, _syncs;
    private double _clock, _latencySum;

    public MultiAgentSwarmFederationOrchestrator() : this(new RealtimeSpatiotemporalKnowledgeGraph(), new ZeroTrustInterAgentConsensusResolver()) { }

    public MultiAgentSwarmFederationOrchestrator(IServiceProvider sp)
        : this((RealtimeSpatiotemporalKnowledgeGraph?)sp.GetService(typeof(RealtimeSpatiotemporalKnowledgeGraph)) ?? new RealtimeSpatiotemporalKnowledgeGraph(),
               (ZeroTrustInterAgentConsensusResolver?)sp.GetService(typeof(ZeroTrustInterAgentConsensusResolver)) ?? new ZeroTrustInterAgentConsensusResolver()) { }

    public MultiAgentSwarmFederationOrchestrator(RealtimeSpatiotemporalKnowledgeGraph kg, ZeroTrustInterAgentConsensusResolver consensus)
    {
        _kg = kg; _consensus = consensus;
        Array.Fill(_owner, -1);
        for (var a = 0; a < Agents; a++)
        {
            _ids[a] = _consensus.Register((FedAgentRole)a);
            _agentNode[a] = _kg.AddNode(ZeroTrustInterAgentConsensusResolver.RoleLabel((FedAgentRole)a) + " 에이전트", KgNodeKind.Agent, a * 2f, -6, 0);
        }
        SeedDemo();
        var d = _kg.Digest();
        for (var a = 0; a < Agents; a++) _localDigest[a] = d;
    }

    public event Action? OnChange;

    public RealtimeSpatiotemporalKnowledgeGraph Graph => _kg;
    public ZeroTrustInterAgentConsensusResolver Consensus => _consensus;
    public double ClockMs { get { lock (_gate) return _clock; } }
    public long BusSequence { get { lock (_gate) return _seq; } }
    public long TasksSubmitted { get { lock (_gate) return _tasksSubmitted; } }
    public long TasksDone { get { lock (_gate) return _tasksDone; } }
    public long Bids { get { lock (_gate) return _bids; } }
    public long Claims { get { lock (_gate) return _claims; } }
    public long Conflicts { get { lock (_gate) return _conflicts; } }
    public long Preemptions { get { lock (_gate) return _preemptions; } }
    public long Denied { get { lock (_gate) return _denied; } }
    public long Syncs { get { lock (_gate) return _syncs; } }
    public double MeanTaskLatencyMs { get { lock (_gate) return _tasksDone > 0 ? _latencySum / _tasksDone : 0; } }
    public string? LastAction { get; private set; }

    // 데모 노드 id
    public int World { get; private set; }
    public int[] Shots { get; } = new int[4];
    public int[] Characters { get; } = new int[3];
    public int[] Assets { get; } = new int[4];
    public int[] SreNodes { get; } = new int[2];
    public int AgentNode(int a) => _agentNode[a];

    private void SeedDemo()
    {
        World = _kg.AddNode("네오 서울 4D 월드", KgNodeKind.World4D, 0, 0, 0);
        string[] shots = { "S1 오프닝 크레인", "S2 골목 추격", "S3 옥상 대치", "S4 엔딩 롱테이크" };
        for (var i = 0; i < 4; i++) { Shots[i] = _kg.AddNode(shots[i], KgNodeKind.Shot, -6 + i * 4, 4, 0, i * 10_000, (i + 1) * 10_000); _kg.Upsert(World, KgRelation.Contains, Shots[i]); }
        string[] chars = { "하린", "도윤", "미라" };
        for (var i = 0; i < 3; i++) Characters[i] = _kg.AddNode(chars[i], KgNodeKind.Character, -4 + i * 4, 0, 0);
        string[] assets = { "하린 3DGS 모델", "네온 텍스처팩", "추격 BGM 스템", "옥상 HDRI" };
        for (var i = 0; i < 4; i++) Assets[i] = _kg.AddNode(assets[i], KgNodeKind.Asset, -6 + i * 4, 8, 0);
        SreNodes[0] = _kg.AddNode("SRE 서울-1", KgNodeKind.SreNode, -3, 12, 0);
        SreNodes[1] = _kg.AddNode("SRE 도쿄-1", KgNodeKind.SreNode, 3, 12, 0);
        _kg.Upsert(Characters[0], KgRelation.AppearsIn, Shots[0]); _kg.Upsert(Characters[0], KgRelation.AppearsIn, Shots[2]);
        _kg.Upsert(Characters[1], KgRelation.AppearsIn, Shots[1]); _kg.Upsert(Characters[1], KgRelation.AppearsIn, Shots[2]);
        _kg.Upsert(Characters[2], KgRelation.AppearsIn, Shots[3]);
        _kg.Upsert(Characters[0], KgRelation.Relates, Characters[1], 0.9f); _kg.Upsert(Characters[1], KgRelation.Relates, Characters[0], 0.9f);
        _kg.Upsert(Characters[1], KgRelation.Relates, Characters[2], 0.4f);
        _kg.Upsert(Shots[0], KgRelation.Uses, Assets[0]); _kg.Upsert(Shots[2], KgRelation.Uses, Assets[0]);
        _kg.Upsert(Shots[1], KgRelation.Uses, Assets[1]); _kg.Upsert(Shots[1], KgRelation.Uses, Assets[2]);
        _kg.Upsert(Shots[2], KgRelation.Uses, Assets[3]); _kg.Upsert(Shots[3], KgRelation.Uses, Assets[1]);
        _kg.Upsert(Assets[0], KgRelation.HostedOn, SreNodes[0]); _kg.Upsert(Assets[1], KgRelation.HostedOn, SreNodes[0]);
        _kg.Upsert(Assets[2], KgRelation.HostedOn, SreNodes[1]); _kg.Upsert(Assets[3], KgRelation.HostedOn, SreNodes[1]);
    }

    public void SetMode(int agent, ByzantineMode mode)
    {
        lock (_gate) _mode[agent] = mode;
        LastAction = mode == ByzantineMode.Honest ? "😇 " + RoleOf(agent) + " 정직 모드" : "😈 " + RoleOf(agent) + " 비잔틴 주입 · " + ModeLabel(mode);
        Raise();
    }

    public ByzantineMode ModeOf(int agent) { lock (_gate) return _mode[agent]; }
    private static string RoleOf(int a) => ZeroTrustInterAgentConsensusResolver.RoleLabel((FedAgentRole)a);

    // ── 메시지 버스(무할당) ──
    private void Publish(BusMessageKind kind, int from, int to, int reference, float value)
    {
        var s = _seq++;
        _bus[s % BusCapacity] = new BusMessage(s, _clock, kind, (sbyte)from, (sbyte)to, reference, value);
    }

    /// <summary>최근 메시지(최신 우선 · 협상 로그).</summary>
    public int CopyRecent(Span<BusMessage> dst)
    {
        lock (_gate)
        {
            var n = (int)Math.Min(Math.Min(dst.Length, _seq), BusCapacity);
            for (var i = 0; i < n; i++) dst[i] = _bus[(_seq - 1 - i) % BusCapacity];
            return n;
        }
    }

    /// <summary>작업 제안(무할당) — 슬롯 id 반환(만석 −1).</summary>
    public int Submit(AgentTaskKind kind, float cost, int resourceNode)
    {
        lock (_gate)
        {
            for (var t = 0; t < MaxTasks; t++)
            {
                if (_taskState[t] != 0) continue;
                _taskState[t] = 1; _taskKind[t] = kind; _remaining[t] = Math.Max(0.01f, cost); _assignee[t] = -1;
                _taskRes[t] = resourceNode; _submittedAt[t] = _clock;
                _tasksSubmitted++;
                Publish(BusMessageKind.TaskOffer, -1, -1, t, cost);
                return t;
            }
            return -1;
        }
    }

    public float LoadOf(int a)
    {
        lock (_gate) return LoadLocked(a);
    }

    private float LoadLocked(int a)
    {
        float l = 0;
        for (var t = 0; t < MaxTasks; t++) if (_taskState[t] == 2 && _assignee[t] == a) l += _remaining[t];
        return l;
    }

    /// <summary>1틱(무할당): 버스 처리(제안 → 입찰 → 낙찰) · 작업 진행 · 리스 만료 · 그래프 시계.</summary>
    public void Tick(double dtMs)
    {
        var done = false;
        lock (_gate)
        {
            _clock += dtMs;
            var end = _seq;
            if (end - _cursor > BusCapacity) _cursor = end - BusCapacity;
            for (; _cursor < end; _cursor++)
            {
                ref readonly var m = ref _bus[_cursor % BusCapacity];
                if (m.Kind != BusMessageKind.TaskOffer) continue;
                var t = m.Ref;
                if (_taskState[t] != 1) continue;
                int best = -1; float bestBid = -1;
                for (var a = 0; a < Agents; a++)
                {
                    if (_consensus.IsQuarantined(a)) continue;
                    var bid = Authority[a * Kinds + (int)_taskKind[t]] / (1f + LoadLocked(a) / Speed[a]);
                    _bids++;
                    Publish(BusMessageKind.Bid, a, -1, t, bid);
                    if (bid > bestBid) { bestBid = bid; best = a; }
                }
                if (best < 0) continue;
                _taskState[t] = 2; _assignee[t] = best; _won[best]++;
                Publish(BusMessageKind.Award, -1, best, t, bestBid);
            }
            var dt = (float)(dtMs / 1000.0);
            for (var a = 0; a < Agents; a++)
            {
                var budget = Speed[a] * dt;
                while (budget > 0)
                {
                    var oldest = -1;
                    for (var t = 0; t < MaxTasks; t++)
                        if (_taskState[t] == 2 && _assignee[t] == a && (oldest < 0 || _submittedAt[t] < _submittedAt[oldest])) oldest = t;
                    if (oldest < 0) break;
                    var use = MathF.Min(budget, _remaining[oldest]);
                    _remaining[oldest] -= use; budget -= use;
                    if (_remaining[oldest] > 1e-6f) break;
                    _taskState[oldest] = 0; _completed[a]++; _tasksDone++; done = true;
                    _latencySum += _clock - _submittedAt[oldest];
                    Publish(BusMessageKind.Done, a, -1, oldest, 1);
                    if (_taskRes[oldest] >= 0) _kg.Upsert(_agentNode[a], KgRelation.Relates, _taskRes[oldest], 0.5f, 30_000);
                }
            }
            for (var r = 0; r < _owner.Length; r++)
                if (_owner[r] >= 0 && _clock >= _leaseUntil[r]) { _kg.Remove(_agentNode[_owner[r]], KgRelation.Owns, r); _owner[r] = -1; }
        }
        _kg.Advance(dtMs);
        if (done && (_tasksDone & 7) == 0) Raise();
    }

    public int OwnerOf(int resource) { lock (_gate) return _owner[resource]; }

    /// <summary>자원 제어권 요청 — 빈 자원/갱신은 즉시, 경합 시 점수 비교 → 합의 이관 또는 거부.</summary>
    public ClaimOutcome Claim(int agent, int resource, AgentTaskKind domain, float confidence)
    {
        int owner; float ownerScore;
        var score = Authority[agent * Kinds + (int)domain] * Math.Clamp(confidence, 0f, 1f);
        lock (_gate)
        {
            _claims++;
            Publish(BusMessageKind.Claim, agent, -1, resource, score);
            owner = _owner[resource]; ownerScore = _ownerScore[resource];
            if (owner < 0 || owner == agent)
            {
                var renewed = owner == agent;
                Grant(agent, resource, score);
                return renewed ? ClaimOutcome.Renewed : ClaimOutcome.Granted;
            }
            _conflicts++;
            if (score <= ownerScore + PreemptMargin)
            {
                _denied++;
                Publish(BusMessageKind.Deny, -1, agent, resource, score);
                LastAction = "✋ " + RoleOf(agent) + " 제어권 요청 거부 — " + RoleOf(owner) + " 우선(" + F2(ownerScore) + " ≥ " + F2(score) + ")";
                Raise();
                return ClaimOutcome.Denied;
            }
        }
        var r = TransferControl(agent, owner, resource, score, ownerScore, domain);
        return r.Outcome == ConsensusOutcome.Committed ? ClaimOutcome.TransferredByConsensus : ClaimOutcome.TransferRejected;
    }

    /// <summary>점수 위조(권한 행렬 초과) 시도 — 제로트러스트 거부 시험용.</summary>
    public ConsensusResult ClaimWithInflatedScore(int agent, int resource, AgentTaskKind domain, float claimedScore)
    {
        int owner; float ownerScore;
        lock (_gate) { owner = _owner[resource]; ownerScore = _ownerScore[resource]; _claims++; _conflicts++; }
        return TransferControl(agent, owner, resource, claimedScore, ownerScore, domain);
    }

    private void Grant(int agent, int resource, float score)
    {
        var prev = _owner[resource];
        if (prev >= 0 && prev != agent) _kg.Remove(_agentNode[prev], KgRelation.Owns, resource);
        _owner[resource] = agent; _ownerScore[resource] = score; _leaseUntil[resource] = _clock + LeaseMs;
        _kg.Upsert(_agentNode[agent], KgRelation.Owns, resource, score, LeaseMs);
        Publish(BusMessageKind.Grant, -1, agent, resource, score);
    }

    private ConsensusResult TransferControl(int to, int from, int resource, float score, float ownerScore, AgentTaskKind domain)
    {
        Span<byte> dom = stackalloc byte[1];
        dom[0] = (byte)domain;
        var p = _consensus.NewProposal(ProposalKind.ControlTransfer, to, from, to, resource, score, ownerScore, dom);
        var r = RunConsensus(p, voter => score <= Authority[to * Kinds + (int)domain] + 1e-4f && score > ownerScore + PreemptMargin);
        lock (_gate)
        {
            if (r.Outcome == ConsensusOutcome.Committed) { Grant(to, resource, score); _preemptions++; Publish(BusMessageKind.Commit, -1, to, resource, score); }
            else Publish(BusMessageKind.Abort, -1, to, resource, score);
        }
        LastAction = r.Outcome == ConsensusOutcome.Committed
            ? "🔁 제어권 이관 " + RoleOf(from) + " → " + RoleOf(to) + " · " + _kg.NameOf(resource) + " (" + r.Accepts + "/" + r.Quorum + ")"
            : "⛔ 제어권 이관 거부 " + RoleOf(to) + " · 찬성 " + r.Accepts + "/" + r.Quorum;
        Raise();
        return r;
    }

    /// <summary>에이전트 간 데이터 교환 — 페이로드 SHA-256 다이제스트를 서명 합의(정직 투표자는 다이제스트 재계산 일치 확인).</summary>
    public ConsensusResult ExchangeData(int from, int to, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> receivedPayload)
    {
        Span<byte> d = stackalloc byte[32];
        SHA256.HashData(payload, d);
        Span<byte> got = stackalloc byte[32];
        SHA256.HashData(receivedPayload, got);
        var match = CryptographicOperations.FixedTimeEquals(d, got);
        var p = _consensus.NewProposal(ProposalKind.DataExchange, from, from, to, -1, payload.Length, 0, d);
        var r = RunConsensus(p, _ => match);
        LastAction = (r.Outcome == ConsensusOutcome.Committed ? "📨 데이터 교환 승인 " : "🚫 데이터 교환 거부 ") + RoleOf(from) + " → " + RoleOf(to);
        Raise();
        return r;
    }

    /// <summary>1-Click 스웜 동기화 — 동기화 브로드캐스트 · 임베딩 전파 · 전이 추론 · 그래프 다이제스트 지식 커밋 합의.</summary>
    public ConsensusResult SyncSwarm()
    {
        _kg.PropagateEmbeddings();
        _kg.InferDependencies();
        var digest = _kg.Digest();
        lock (_gate)
        {
            for (var a = 0; a < Agents; a++)
            {
                // 정직 에이전트는 동기화 메시지로 로컬 뷰 정렬 · 비잔틴(대조 모드)은 낡은 뷰 유지
                if (_mode[a] != ByzantineMode.Contrarian) _localDigest[a] = digest;
                Publish(BusMessageKind.Sync, a, -1, (int)(digest & 0x7FFFFFFF), 1);
            }
        }
        Span<byte> d = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(d, digest);
        var p = _consensus.NewProposal(ProposalKind.KnowledgeCommit, 0, 0, -1, -1, _kg.EdgeCount, _kg.NodeCount, d);
        var r = RunConsensus(p, voter => _localDigest[voter] == digest);
        lock (_gate) _syncs++;
        LastAction = (r.Outcome == ConsensusOutcome.Committed ? "🔄 스웜 동기화 커밋 · 에포크 " + _consensus.Epoch : "⚠ 스웜 동기화 합의 실패") + " · 다이제스트 " + digest.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
        return r;
    }

    /// <summary>제안 서명 → 에이전트별 정책 투표(비잔틴 모드 반영) → 해결기 판정.</summary>
    private ConsensusResult RunConsensus(FederationProposal p, Func<int, bool> honestPolicy)
    {
        var n = _consensus.AgentCount;
        var proposerSig = _ids[p.Proposer].SignProposal(p, n);
        lock (_gate) Publish(BusMessageKind.Proposal, p.Proposer, -1, (int)p.Id, p.Value);
        var votes = new List<SignedVote>(Agents + 1);
        for (var a = 0; a < Agents; a++)
        {
            if (_consensus.IsQuarantined(a)) continue;
            var accept = honestPolicy(a);
            ByzantineMode mode;
            lock (_gate) mode = _mode[a];
            switch (mode)
            {
                case ByzantineMode.ForgeSignature:
                    var v = _ids[a].Vote(p, n, !accept);
                    var bad = (byte[])v.Signature.Clone(); bad[10] ^= 0xFF;
                    votes.Add(v with { Signature = bad });
                    break;
                case ByzantineMode.Equivocate:
                    votes.Add(_ids[a].Vote(p, n, true));
                    votes.Add(_ids[a].Vote(p, n, false));
                    break;
                case ByzantineMode.Contrarian:
                    votes.Add(_ids[a].Vote(p, n, false));   // 상시 반대(서명은 유효 — 정족수로만 흡수)
                    break;
                default:
                    votes.Add(_ids[a].Vote(p, n, accept));
                    break;
            }
            lock (_gate) Publish(BusMessageKind.Vote, a, -1, (int)p.Id, accept ? 1 : 0);
        }
        LastVotes = votes;
        LastProposal = p;
        return _consensus.Resolve(p, proposerSig, votes);
    }

    public IReadOnlyList<SignedVote> LastVotes { get; private set; } = Array.Empty<SignedVote>();
    public FederationProposal? LastProposal { get; private set; }

    public FedAgentStatus StatusOf(int a)
    {
        lock (_gate)
        {
            var active = 0;
            for (var t = 0; t < MaxTasks; t++) if (_taskState[t] == 2 && _assignee[t] == a) active++;
            return new FedAgentStatus(a, (FedAgentRole)a, _agentNode[a], LoadLocked(a), active, _completed[a], _won[a], _mode[a], _consensus.IsQuarantined(a), _consensus.ReputationOf(a));
        }
    }

    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string ModeLabel(ByzantineMode m) => m switch
    {
        ByzantineMode.ForgeSignature => "서명 위조", ByzantineMode.Equivocate => "이중 투표", ByzantineMode.Contrarian => "상시 반대·낡은 뷰", _ => "정직",
    };

    public static string KindLabel(AgentTaskKind k) => k switch
    {
        AgentTaskKind.Shot => "샷 연출", AgentTaskKind.Sound => "음향", AgentTaskKind.Effect => "VFX", AgentTaskKind.Ops => "운영", _ => "자산",
    };

    public static string MessageLabel(BusMessageKind k) => k switch
    {
        BusMessageKind.TaskOffer => "작업 제안", BusMessageKind.Bid => "입찰", BusMessageKind.Award => "낙찰", BusMessageKind.Done => "완료",
        BusMessageKind.Claim => "제어권 요청", BusMessageKind.Grant => "승인", BusMessageKind.Deny => "거부", BusMessageKind.Proposal => "합의 제안",
        BusMessageKind.Vote => "투표", BusMessageKind.Commit => "커밋", BusMessageKind.Abort => "중단", _ => "동기화",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Federation.Orchestrator.OnChange", ex); }
    }
}
