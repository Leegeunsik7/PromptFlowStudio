using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PromptFlowStudio.Services.Inference;

namespace PromptFlowStudio.Services.Integration;

/// <summary>[v9.3.2] 메시 노드 상태.</summary>
public enum MeshNodeState
{
    /// <summary>정상 참여.</summary>
    Alive = 0,
    /// <summary>의심(지연 급증 · 라우팅 후순위).</summary>
    Suspect = 1,
    /// <summary>이탈(라우팅 제외).</summary>
    Dead = 2,
}

/// <summary>[v15.0.0] 라우팅 종결 사유(값 형식 · 문자열 할당 없이 비교).</summary>
public enum MeshRouteStatus : byte
{
    /// <summary>전달 성공.</summary>
    Delivered = 0,
    /// <summary>경로 없음.</summary>
    NoRoute = 1,
    /// <summary>TTL 소진.</summary>
    TtlExhausted = 2,
    /// <summary>재라우팅 상한 초과.</summary>
    RerouteLimit = 3,
    /// <summary>수신측 멱등 차단.</summary>
    Duplicate = 4,
    /// <summary>수신측 재검증 서명 거부.</summary>
    SignatureRejected = 5,
    /// <summary>목적지 = 로컬(전달 미집계 · 실패로 계수).</summary>
    SelfDestination = 6,
}

/// <summary>[v15.0.0] Zero-Alloc 라우팅 결과(값 형식 · 봉투 미물질화).</summary>
public readonly record struct MeshRouteOutcome(bool Delivered, MeshRouteStatus Status, int HopCount, double CostMs, int Reroutes, Guid MessageId)
{
    /// <summary>사유 라벨(리터럴 · 할당 없음).</summary>
    public string Reason => QuantumP2pMeshRouter.ReasonText(Status);
}

/// <summary>[v9.3.2] HUD 표출용 노드 스냅샷(공개키만 노출 · 개인키 시드는 라우터 내부 전용).</summary>
public sealed record MeshNodeView(
    string NodeId,
    string PublicKeyHex,
    MeshNodeState State,
    int LinkCount,
    int Hops,
    double CostMs,
    long Relayed);

/// <summary>[v9.3.2] 릴레이 홉 증명 — 직전 홉 서명에 체인 결합(홉 삽입/삭제/재정렬 검출).</summary>
public sealed record MeshHopProof(int Index, string NodeId, string SignatureHex);

/// <summary>
/// [v9.3.2] 서명 봉투(불변). 원점 서명은 불변 필드만, 홉 증명은 체인으로 결합한다.
/// ⚠ 검증 정규화에 <b>홉 개수</b>까지 바인딩 — 홉 목록 절단 위조가 통과되지 않도록 한다(v9 봉인 헌장).
/// </summary>
public sealed record MeshEnvelope(
    string MessageId,
    string Origin,
    string Destination,
    long TimestampMs,
    int Ttl,
    string PayloadHashHex,
    string OriginSignatureHex,
    IReadOnlyList<MeshHopProof> Hops,
    int HopCount,
    string HopChainHex);

/// <summary>[v9.3.2] 라우팅 1회 결과.</summary>
public sealed record MeshRouteResult(
    bool Delivered,
    IReadOnlyList<string> Path,
    double CostMs,
    int Reroutes,
    string Reason,
    MeshEnvelope? Envelope);

/// <summary>
/// [PromptFlow Studio v9.3.2] 양자 내성(PQC) P2P 메시 라우터(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 노드마다 ML-DSA-65 키 쌍을 부여하고, 봉투는 원점 서명 + 홉별 체인 서명으로 릴레이된다(각 홉 상수시간 재검증).
///   ▸ 동적 토폴로지: 노드 가입/이탈/의심 전환 시 Dijkstra(지연 가중 · Suspect 3배 페널티)로 경로를 재계산하고,
///     릴레이 도중 다음 홉 이탈은 현재 홉에서 자가치유 재라우팅(상한 3회).
///   ▸ 멱등: MessageId 중복 수신은 차단(상한 4096 · 초과 시 전량 비움).
///   ▸ <see cref="AnchorTopology"/>: 토폴로지 + 집계 카운트 정규화를 WORM 원장(<see cref="QuantumResistantLedgerSigner"/>)에 봉인(미주입 시 -1).
///   ▸ [v15.0.0] Zero-Alloc 코어: Dijkstra 는 노드 인덱스 기반 인스턴스 스크래치 배열(락 내부), 경로는 ArrayPool 대여,
///     정규화는 UTF-8 바이트 직접 기록 + 원샷 HMAC/SHA256(Span), 서명 체인은 stackalloc, 멱등 집합은 Guid 값 키.
///     <see cref="Route"/> 는 힙 할당 0B · <see cref="Send"/> 는 같은 코어 위에 반환 봉투(문자열·레코드)만 물질화.
///     HUD 1줄(<see cref="LastAction"/>)은 조회 시점 지연 포맷.
///   ⚠ <see cref="SignPqc"/>/<see cref="VerifyPqc"/> 는 원장 서명기와 동일한 <b>미결선 스텁</b>(HMAC-SHA512 확산) —
///     .NET 9+ <c>System.Security.Cryptography.MLDsa</c> 교체 시 byte[] in/out 만 유지하면 상위 무변경 승격.
/// </summary>
public sealed class QuantumP2pMeshRouter
{
    private const int MaxReroutes = 3;
    private const int DefaultTtl = 8;
    private const int SeenCap = 4096;
    private const double SuspectPenalty = 3d;
    private const string Algorithm = "ML-DSA-65(stub)";
    private const int SigBytes = 64;       // HMAC-SHA512
    private const int LocalIndex = 0;      // 생성자에서 첫 가입 · 노드 제거 API 없음 → 인덱스 불변

    private enum PendingAction : byte { None, Route, Revive }

    private readonly object _gate = new();
    private readonly Dictionary<string, MeshNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<MeshNode> _order = new();
    private readonly object _seenGate = new();
    private readonly HashSet<Guid> _seen = new(SeenCap + 1);   // 상한 선할당 → 성장 리사이즈 0(Clear 는 버킷 유지)
    private readonly QuantumResistantLedgerSigner? _ledger;

    // Dijkstra 스크래치(_gate 내부 전용 · 노드 가입 시에만 확장).
    private double[] _dist = Array.Empty<double>();
    private int[] _prev = Array.Empty<int>();
    private bool[] _done = Array.Empty<bool>();

    // LastAction 지연 포맷(_gate 보호).
    private string? _lastAction;
    private PendingAction _pending;
    private MeshRouteOutcome _lastOutcome;
    private string _lastDestination = string.Empty;
    private int _pendingCount;

    private long _delivered;
    private long _dropped;
    private long _reroutes;
    private long _rejected;
    private long _duplicates;
    private long _relays;

    public QuantumP2pMeshRouter(QuantumResistantLedgerSigner? ledger = null, string localNodeId = "studio")
    {
        _ledger = ledger;
        LocalNodeId = string.IsNullOrWhiteSpace(localNodeId) ? "studio" : localNodeId;
        lock (_gate) AddNodeLocked(LocalNodeId);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>서명 불일치·위변조 경고.</summary>
    public event Action<string>? OnSecurityAlert;

    /// <summary>로컬 노드 ID.</summary>
    public string LocalNodeId { get; }

    /// <summary>서명 알고리즘 라벨.</summary>
    public string AlgorithmLabel => Algorithm;

    /// <summary>누적 전달 성공.</summary>
    public long DeliveredCount => Interlocked.Read(ref _delivered);

    /// <summary>누적 전달 실패(경로 없음·TTL 소진).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>누적 자가치유 재라우팅.</summary>
    public long RerouteCount => Interlocked.Read(ref _reroutes);

    /// <summary>누적 서명 거부(위변조).</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>누적 중복 차단.</summary>
    public long DuplicateCount => Interlocked.Read(ref _duplicates);

    /// <summary>누적 릴레이 홉.</summary>
    public long RelayCount => Interlocked.Read(ref _relays);

    /// <summary>전체 노드 수(로컬 포함).</summary>
    public int NodeCount { get { lock (_gate) return _nodes.Count; } }

    /// <summary>정상 노드 수.</summary>
    public int AliveCount { get { lock (_gate) return CountAliveLocked(); } }

    /// <summary>양방향 링크 수.</summary>
    public int LinkCount { get { lock (_gate) return CountLinksLocked(); } }

    /// <summary>로컬에서 도달 가능한 노드 비율(0~1 · 로컬 제외).</summary>
    public double ReachabilityRatio
    {
        get
        {
            lock (_gate)
            {
                var others = _order.Count - 1;
                if (others <= 0) return 0d;
                ShortestLocked(LocalIndex);
                var reach = 0;
                for (var i = 0; i < _order.Count; i++)
                    if (i != LocalIndex && !double.IsPositiveInfinity(_dist[i])) reach++;
                return reach / (double)others;
            }
        }
    }

    /// <summary>마지막 라우팅 결과(없으면 null · <see cref="Send"/> 만 갱신).</summary>
    public MeshRouteResult? LastRoute { get; private set; }

    /// <summary>마지막 라우팅 값 결과(<see cref="Route"/>/<see cref="Send"/> 공통).</summary>
    public MeshRouteOutcome LastOutcome { get { lock (_gate) return _lastOutcome; } }

    /// <summary>마지막 원장 봉인 seq(미봉인/미주입 시 -1).</summary>
    public long LastAnchorSequence { get; private set; } = -1;

    /// <summary>마지막 경고(정상 시 null).</summary>
    public string? LastAlert { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄 · 라우팅/복귀는 조회 시점 지연 포맷).</summary>
    public string? LastAction
    {
        get
        {
            lock (_gate)
            {
                switch (_pending)
                {
                    case PendingAction.Route:
                        var o = _lastOutcome;
                        _lastAction = string.Create(CultureInfo.InvariantCulture,
                            $"{(o.Delivered ? "전달" : o.Reason)} {LocalNodeId}→{_lastDestination} · {o.HopCount}홉 · {o.CostMs:0.#}ms · 재라우팅 {o.Reroutes}");
                        break;
                    case PendingAction.Revive:
                        _lastAction = string.Create(CultureInfo.InvariantCulture, $"복귀 {_pendingCount}노드");
                        break;
                }
                _pending = PendingAction.None;
                return _lastAction;
            }
        }
    }

    /// <summary>노드 스냅샷(로컬 기준 홉/비용 포함).</summary>
    public IReadOnlyList<MeshNodeView> Nodes
    {
        get
        {
            lock (_gate)
            {
                ShortestLocked(LocalIndex);
                var views = new MeshNodeView[_order.Count];
                for (var i = 0; i < views.Length; i++)
                {
                    var n = _order[i];
                    var cost = _dist[i];
                    var hops = double.IsPositiveInfinity(cost) ? -1 : HopsLocked(i);
                    views[i] = new MeshNodeView(n.Id, n.PublicKeyHex, n.State, n.Links.Count, hops, cost, n.Relayed);
                }
                Array.Sort(views, static (a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
                return views;
            }
        }
    }

    // ── 동적 토폴로지 ──

    /// <summary>노드를 가입시킨다(이미 있으면 Alive 로 복귀 · 키 유지).</summary>
    public void JoinNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        lock (_gate)
        {
            if (_nodes.TryGetValue(nodeId, out var n)) n.State = MeshNodeState.Alive;
            else AddNodeLocked(nodeId);
        }
        Touch($"가입 {nodeId}");
    }

    /// <summary>두 노드를 지연(ms) 가중 양방향 링크로 연결한다(미가입 노드는 자동 가입).</summary>
    public void Connect(string a, string b, double latencyMs)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || a == b) return;
        var w = double.IsNaN(latencyMs) || latencyMs <= 0d ? 1d : latencyMs;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(a, out var na)) na = AddNodeLocked(a);
            if (!_nodes.TryGetValue(b, out var nb)) nb = AddNodeLocked(b);
            na.Links[b] = w;
            nb.Links[a] = w;
        }
        Touch($"링크 {a}↔{b} {w.ToString("0.#", CultureInfo.InvariantCulture)}ms");
    }

    /// <summary>노드 상태 전환(로컬 노드는 이탈 불가).</summary>
    public void SetState(string nodeId, MeshNodeState state)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(nodeId, out var n)) return;
            if (nodeId == LocalNodeId && state == MeshNodeState.Dead) return;
            n.State = state;
        }
        Touch($"{nodeId} → {state}");
    }

    /// <summary>로컬 제외 임의 정상 노드 1개를 이탈시킨다(카오스 프로브). 대상 없으면 null.</summary>
    public string? FailRandomNode()
    {
        string? victim = null;
        lock (_gate)
        {
            var alive = 0;
            foreach (var n in _order)
                if (n.Index != LocalIndex && n.State != MeshNodeState.Dead) alive++;
            if (alive == 0) return null;
            var pick = Random.Shared.Next(alive);
            foreach (var n in _order)
            {
                if (n.Index == LocalIndex || n.State == MeshNodeState.Dead || pick-- != 0) continue;
                n.State = MeshNodeState.Dead;
                victim = n.Id;
                break;
            }
        }
        Touch($"카오스 이탈 {victim}");
        return victim;
    }

    /// <summary>이탈 노드 전량 복귀(핫패스 안전 · 무할당).</summary>
    public int ReviveAll()
    {
        int revived;
        lock (_gate)
        {
            revived = 0;
            foreach (var n in _order)
                if (n.State != MeshNodeState.Alive) { n.State = MeshNodeState.Alive; revived++; }
            _pending = PendingAction.Revive;
            _pendingCount = revived;
        }
        OnChange?.Invoke();
        return revived;
    }

    /// <summary>링+코드(chord) 데모 메시를 구성한다(로컬 포함 <paramref name="count"/> 노드 · 멱등).</summary>
    public void SeedDemoMesh(int count = 7)
    {
        var n = Math.Clamp(count, 3, 16);
        var ids = new string[n];
        ids[0] = LocalNodeId;
        for (var i = 1; i < n; i++) ids[i] = "edge-" + i.ToString("00", CultureInfo.InvariantCulture);

        for (var i = 0; i < n; i++)
        {
            Connect(ids[i], ids[(i + 1) % n], 8d + (i * 7 % 23));        // 링
            if (n > 4) Connect(ids[i], ids[(i + n / 2) % n], 30d + i * 3); // 코드(우회 경로)
        }
        Touch($"데모 메시 {n}노드");
    }

    // ── 라우팅 & 릴레이 ──

    /// <summary>
    /// 로컬 노드에서 목적지로 서명 봉투를 릴레이한다(홉별 PQC 서명 · 계획 경로 이탈 시 현재 홉에서 자가치유 재라우팅).
    /// <paramref name="chaosMidRoute"/>=true 면 계획 직후 첫 중계 노드를 이탈시켜 재라우팅 경로를 실측한다.
    /// 반환 봉투(문자열·레코드)를 물질화하므로 홉 수에 비례한 할당이 있다 — 고빈도 경로는 <see cref="Route"/>.
    /// </summary>
    public MeshRouteResult Send(string destination, string payload, bool chaosMidRoute = false)
    {
        var dest = destination ?? string.Empty;
        var capture = new RouteCapture();
        var o = RouteCore(dest, (payload ?? string.Empty).AsSpan(), chaosMidRoute, capture);

        var hops = capture.Hops.ToArray();
        var envelope = new MeshEnvelope(capture.MessageId, LocalNodeId, dest, capture.TimestampMs, DefaultTtl, capture.PayloadHashHex,
            capture.OriginSignatureHex, hops, hops.Length, HopChain(hops));
        var result = new MeshRouteResult(o.Delivered, capture.Path.ToArray(), o.CostMs, o.Reroutes, o.Reason, envelope);
        LastRoute = result;
        NotifyRoute(o, dest);
        return result;
    }

    /// <summary>
    /// [v15.0.0] Zero-Alloc 릴레이 — <see cref="Send"/> 와 동일한 서명 체인·자가치유·멱등·수신 재검증을 수행하되
    /// 봉투를 물질화하지 않고 값 결과만 반환한다(정상 경로 힙 할당 0B · 서명 거부 경고 경로만 예외).
    /// </summary>
    public MeshRouteOutcome Route(string destination, ReadOnlySpan<char> payload, bool chaosMidRoute = false)
    {
        var dest = destination ?? string.Empty;
        var o = RouteCore(dest, payload, chaosMidRoute, null);
        NotifyRoute(o, dest);
        return o;
    }

    /// <summary>종결 사유 라벨(리터럴).</summary>
    public static string ReasonText(MeshRouteStatus status) => status switch
    {
        MeshRouteStatus.Delivered or MeshRouteStatus.SelfDestination => "전달",
        MeshRouteStatus.NoRoute => "경로 없음",
        MeshRouteStatus.TtlExhausted => "TTL 소진",
        MeshRouteStatus.RerouteLimit => "재라우팅 상한",
        MeshRouteStatus.Duplicate => "중복",
        _ => "서명 거부",
    };

    private MeshRouteOutcome RouteCore(string destination, ReadOnlySpan<char> payload, bool chaosMidRoute, RouteCapture? capture)
    {
        var id = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Span<byte> idHex = stackalloc byte[32];
        id.TryFormat(idHex, out _, "N");
        Span<byte> hash = stackalloc byte[32];
        Span<byte> hashHex = stackalloc byte[64];
        Span<byte> sigs = stackalloc byte[SigBytes * (DefaultTtl + 1)];   // [0]=원점, [i+1]=홉 i (TTL 상한 = 홉 상한)
        Span<int> hopNodes = stackalloc int[DefaultTtl];

        MeshNode local;
        lock (_gate) local = _order[LocalIndex];

        var payloadBuf = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(payload.Length));
        var canon = ArrayPool<byte>.Shared.Rent(256);
        var plan = ArrayPool<int>.Shared.Rent(16);
        try
        {
            SHA256.HashData(payloadBuf.AsSpan(0, Encoding.UTF8.GetBytes(payload, payloadBuf)), hash);
            WriteHexLower(hash, hashHex);

            var len = WriteOriginCanonical(ref canon, idHex, LocalNodeId, destination, ts, DefaultTtl, hashHex);
            HMACSHA512.HashData(local.Seed, canon.AsSpan(0, len), sigs[..SigBytes]);

            if (capture is not null)
            {
                capture.MessageId = id.ToString("N");
                capture.TimestampMs = ts;
                capture.PayloadHashHex = Hex(hash);
                capture.OriginSignatureHex = Hex(sigs[..SigBytes]);
                capture.Path.Add(LocalNodeId);
            }

            var cur = local;
            var hopCount = 0;
            var reroutes = 0;
            var cost = 0d;
            var ttl = DefaultTtl;
            MeshRouteStatus status;

            var planLen = PlanInto(LocalIndex, destination, ref plan);
            if (chaosMidRoute && planLen > 2)
            {
                lock (_gate) _order[plan[1]].State = MeshNodeState.Dead;   // 계획 경로 첫 중계 이탈
            }
            var cursor = 0;

            while (true)
            {
                if (cur.Id == destination) { status = MeshRouteStatus.Delivered; break; }
                if (planLen < 2 || cursor + 1 >= planLen) { status = MeshRouteStatus.NoRoute; break; }
                if (ttl-- <= 0) { status = MeshRouteStatus.TtlExhausted; break; }

                MeshNode next;
                bool nextDead;
                var linkCost = 0d;
                lock (_gate)
                {
                    next = _order[plan[cursor + 1]];
                    nextDead = next.State == MeshNodeState.Dead || !cur.Links.TryGetValue(next.Id, out linkCost);
                }

                if (nextDead)
                {
                    // 자가치유: 현재 홉 기준 재계획(지나온 경로·서명 체인은 보존).
                    if (++reroutes > MaxReroutes) { status = MeshRouteStatus.RerouteLimit; break; }
                    Interlocked.Increment(ref _reroutes);
                    planLen = PlanInto(cur.Index, destination, ref plan);
                    cursor = 0;
                    continue;
                }

                var sig = sigs.Slice((hopCount + 1) * SigBytes, SigBytes);
                len = WriteHopCanonical(ref canon, idHex, hopCount, next.Id, sigs.Slice(hopCount * SigBytes, SigBytes));
                HMACSHA512.HashData(next.Seed, canon.AsSpan(0, len), sig);
                hopNodes[hopCount] = next.Index;
                if (capture is not null)
                {
                    capture.Hops.Add(new MeshHopProof(hopCount, next.Id, Hex(sig)));
                    capture.Path.Add(next.Id);
                }
                hopCount++;
                cost += linkCost;
                cur = next;
                cursor++;
                Interlocked.Increment(ref _relays);
                lock (_gate) next.Relayed++;
            }

            var delivered = status == MeshRouteStatus.Delivered && destination != LocalNodeId;
            if (status == MeshRouteStatus.Delivered && !delivered) status = MeshRouteStatus.SelfDestination;
            if (delivered)
            {
                // 수신측 멱등 + 전체 체인 재검증(원점 서명 · 홉 체인 · 최종 홉 = 목적지).
                bool fresh;
                lock (_seenGate)
                {
                    fresh = _seen.Add(id);
                    if (_seen.Count > SeenCap) _seen.Clear();
                }
                if (!fresh) { Interlocked.Increment(ref _duplicates); delivered = false; status = MeshRouteStatus.Duplicate; }
                else if (!VerifyChain(ref canon, idHex, ts, destination, hashHex, sigs, hopNodes[..hopCount]))
                {
                    delivered = false;
                    status = MeshRouteStatus.SignatureRejected;
                }
            }

            if (delivered) Interlocked.Increment(ref _delivered);
            else if (status != MeshRouteStatus.SignatureRejected && status != MeshRouteStatus.Duplicate) Interlocked.Increment(ref _dropped);

            return new MeshRouteOutcome(delivered, status, hopCount, cost, reroutes, id);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payloadBuf);
            ArrayPool<byte>.Shared.Return(canon);
            ArrayPool<int>.Shared.Return(plan);
        }
    }

    // 수신측 바이트 재검증 — VerifyEnvelope 와 동일 정규화(문자열 경로와 비트 동일 · 테스트로 고정).
    private bool VerifyChain(ref byte[] canon, ReadOnlySpan<byte> idHex, long ts, string destination, ReadOnlySpan<byte> hashHex,
        ReadOnlySpan<byte> sigs, ReadOnlySpan<int> hopNodes)
    {
        Span<byte> expect = stackalloc byte[SigBytes];
        MeshNode local;
        lock (_gate) local = _order[LocalIndex];

        var len = WriteOriginCanonical(ref canon, idHex, LocalNodeId, destination, ts, DefaultTtl, hashHex);
        HMACSHA512.HashData(local.Seed, canon.AsSpan(0, len), expect);
        if (!CryptographicOperations.FixedTimeEquals(expect, sigs[..SigBytes])) return Reject("원점 PQC 서명 불일치");

        MeshNode? last = null;
        for (var i = 0; i < hopNodes.Length; i++)
        {
            lock (_gate) last = _order[hopNodes[i]];
            len = WriteHopCanonical(ref canon, idHex, i, last.Id, sigs.Slice(i * SigBytes, SigBytes));
            HMACSHA512.HashData(last.Seed, canon.AsSpan(0, len), expect);
            if (!CryptographicOperations.FixedTimeEquals(expect, sigs.Slice((i + 1) * SigBytes, SigBytes)))
                return Reject(string.Create(CultureInfo.InvariantCulture, $"홉 #{i} PQC 서명 불일치"));
        }

        if (last is not null && last.Id != destination) return Reject("최종 홉 ≠ 목적지");
        return true;
    }

    /// <summary>
    /// 봉투 전체를 재검증한다 — 원점 서명, 홉 체인 서명, 홉 개수/체인 다이제스트 바인딩을 모두 상수시간 대조.
    /// 한 항목이라도 불일치하면 false 및 보안 경고.
    /// </summary>
    public bool VerifyEnvelope(MeshEnvelope? envelope)
    {
        if (envelope is null) return Reject("봉투 없음");

        byte[]? originSeed;
        lock (_gate) originSeed = _nodes.TryGetValue(envelope.Origin, out var o) ? o.Seed : null;
        if (originSeed is null) return Reject($"미등록 원점 {envelope.Origin}");

        var originMsg = Encoding.UTF8.GetBytes(OriginCanonical(envelope.MessageId, envelope.Origin, envelope.Destination,
            envelope.TimestampMs, envelope.Ttl, envelope.PayloadHashHex));
        if (!VerifyPqc(originSeed, originMsg, envelope.OriginSignatureHex)) return Reject("원점 PQC 서명 불일치");

        if (envelope.Hops is null || envelope.HopCount != envelope.Hops.Count) return Reject("홉 개수 불일치");
        if (!FixedEquals(HopChain(envelope.Hops), envelope.HopChainHex)) return Reject("홉 체인 다이제스트 불일치");

        var chain = envelope.OriginSignatureHex;
        for (var i = 0; i < envelope.Hops.Count; i++)
        {
            var hop = envelope.Hops[i];
            if (hop.Index != i) return Reject($"홉 순서 위조 #{i}");

            byte[]? seed;
            lock (_gate) seed = _nodes.TryGetValue(hop.NodeId, out var n) ? n.Seed : null;
            if (seed is null) return Reject($"미등록 릴레이 {hop.NodeId}");

            if (!VerifyPqc(seed, Encoding.UTF8.GetBytes(HopCanonical(envelope.MessageId, i, hop.NodeId, chain)), hop.SignatureHex))
                return Reject($"홉 #{i} PQC 서명 불일치");
            chain = hop.SignatureHex;
        }

        if (envelope.Hops.Count > 0 && envelope.Hops[^1].NodeId != envelope.Destination)
            return Reject("최종 홉 ≠ 목적지");

        return true;
    }

    /// <summary>
    /// 토폴로지 + 집계 카운트 정규화를 WORM 원장에 봉인한다. 원장 미주입 시 -1(제5헌장 Graceful).
    /// ⚠ 정규화에 노드/링크/전달/실패/거부/중복/재라우팅 카운트 전량 바인딩(집계 위조 검출).
    /// </summary>
    public long AnchorTopology()
    {
        var canonical = TopologyCanonical();
        if (_ledger is null)
        {
            LastAnchorSequence = -1;
            Touch("원장 미주입 — 봉인 생략");
            return -1;
        }

        try
        {
            var entry = _ledger.SignAndAppend(Encoding.UTF8.GetBytes(canonical), "p2p-mesh");
            LastAnchorSequence = entry.Seq;
            Touch($"토폴로지 원장 봉인 seq={entry.Seq.ToString(CultureInfo.InvariantCulture)}");
            return entry.Seq;
        }
        catch (Exception ex)
        {
            LastAnchorSequence = -1;
            Touch("원장 봉인 실패 — " + ex.Message);
            return -1;
        }
    }

    /// <summary>토폴로지 정규화 문자열(노드·링크·상태 + 집계 카운트).</summary>
    public string TopologyCanonical()
    {
        var sb = new StringBuilder(512);
        lock (_gate)
        {
            sb.Append("mesh|v9.3.2|local=").Append(LocalNodeId)
              .Append("|nodes=").Append(_nodes.Count.ToString(CultureInfo.InvariantCulture))
              .Append("|alive=").Append(CountAliveLocked().ToString(CultureInfo.InvariantCulture))
              .Append("|links=").Append(CountLinksLocked().ToString(CultureInfo.InvariantCulture));
            var sorted = _order.ToArray();
            Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            var keys = new List<string>();
            foreach (var n in sorted)
            {
                sb.Append('|').Append(n.Id).Append(':').Append((int)n.State).Append(':').Append(n.PublicKeyHex);
                keys.Clear();
                foreach (var k in n.Links.Keys) keys.Add(k);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys)
                    if (string.CompareOrdinal(n.Id, k) < 0)
                        sb.Append('>').Append(k).Append('=').Append(n.Links[k].ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        sb.Append("|delivered=").Append(DeliveredCount.ToString(CultureInfo.InvariantCulture))
          .Append("|dropped=").Append(DroppedCount.ToString(CultureInfo.InvariantCulture))
          .Append("|rejected=").Append(RejectedCount.ToString(CultureInfo.InvariantCulture))
          .Append("|duplicates=").Append(DuplicateCount.ToString(CultureInfo.InvariantCulture))
          .Append("|reroutes=").Append(RerouteCount.ToString(CultureInfo.InvariantCulture))
          .Append("|relays=").Append(RelayCount.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ── 내부 ──

    private MeshNode AddNodeLocked(string id)
    {
        var seed = RandomNumberGenerator.GetBytes(64);
        var node = new MeshNode(id, _order.Count, seed, Hex(SHA256.HashData(seed)));
        _nodes[id] = node;
        _order.Add(node);
        if (_dist.Length < _order.Count)
        {
            var cap = Math.Max(16, _order.Count * 2);
            Array.Resize(ref _dist, cap);
            Array.Resize(ref _prev, cap);
            Array.Resize(ref _done, cap);
        }
        return node;
    }

    private int CountAliveLocked()
    {
        var c = 0;
        foreach (var n in _order) if (n.State != MeshNodeState.Dead) c++;
        return c;
    }

    private int CountLinksLocked()
    {
        var c = 0;
        foreach (var n in _order) c += n.Links.Count;
        return c / 2;
    }

    // Dijkstra(소규모 메시 · O(V²) · 인덱스 스크래치 재사용) — Dead 제외, Suspect 는 진입 비용 3배. 결과는 _dist/_prev.
    private void ShortestLocked(int source)
    {
        var count = _order.Count;
        var dist = _dist;
        var prev = _prev;
        var done = _done;
        for (var i = 0; i < count; i++) { dist[i] = double.PositiveInfinity; prev[i] = -1; done[i] = false; }
        if ((uint)source >= (uint)count) return;
        dist[source] = 0d;

        while (true)
        {
            var u = -1;
            var best = double.PositiveInfinity;
            for (var i = 0; i < count; i++)
                if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
            if (u < 0) break;
            done[u] = true;

            foreach (var kv in _order[u].Links)
            {
                var node = _nodes[kv.Key];
                var v = node.Index;
                if (node.State == MeshNodeState.Dead || done[v]) continue;
                var alt = best + (node.State == MeshNodeState.Suspect ? kv.Value * SuspectPenalty : kv.Value);
                if (alt < dist[v]) { dist[v] = alt; prev[v] = u; }
            }
        }
    }

    private int HopsLocked(int target)
    {
        var hops = 0;
        for (var c = _prev[target]; c >= 0; c = _prev[c]) hops++;
        return hops;
    }

    // 출발→목적지 경로를 노드 인덱스로 버퍼에 기록(부족 시 풀 재대여). 경로 없음 0.
    private int PlanInto(int source, string destination, ref int[] buffer)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(destination, out var dst)) return 0;
            ShortestLocked(source);
            if (double.IsPositiveInfinity(_dist[dst.Index])) return 0;
            var len = HopsLocked(dst.Index) + 1;
            if (buffer.Length < len)
            {
                ArrayPool<int>.Shared.Return(buffer);
                buffer = ArrayPool<int>.Shared.Rent(len);
            }
            var k = len;
            for (var c = dst.Index; c >= 0; c = _prev[c]) buffer[--k] = c;
            return len;
        }
    }

    private bool Reject(string reason)
    {
        Interlocked.Increment(ref _rejected);
        LastAlert = reason;
        try { OnSecurityAlert?.Invoke(reason); } catch { /* 구독자 예외 격리 */ }
        Touch("거부 — " + reason);
        return false;
    }

    private void Touch(string action)
    {
        lock (_gate)
        {
            _lastAction = action;
            _pending = PendingAction.None;
        }
        OnChange?.Invoke();
    }

    private void NotifyRoute(in MeshRouteOutcome outcome, string destination)
    {
        lock (_gate)
        {
            _lastOutcome = outcome;
            _lastDestination = destination;
            _pending = PendingAction.Route;
        }
        OnChange?.Invoke();
    }

    private static string OriginCanonical(string id, string origin, string dest, long ts, int ttl, string payloadHash)
        => string.Create(CultureInfo.InvariantCulture, $"origin|{id}|{origin}|{dest}|{ts}|{ttl}|{payloadHash}");

    private static string HopCanonical(string id, int index, string nodeId, string prevSig)
        => string.Create(CultureInfo.InvariantCulture, $"hop|{id}|{index}|{nodeId}|{prevSig}");

    // ── UTF-8 정규화 직접 기록(OriginCanonical/HopCanonical 과 바이트 동일) ──

    private static int WriteOriginCanonical(ref byte[] buf, ReadOnlySpan<byte> idHex, string origin, string dest, long ts, int ttl,
        ReadOnlySpan<byte> payloadHashHex)
    {
        EnsureCapacity(ref buf, 192 + Encoding.UTF8.GetByteCount(origin) + Encoding.UTF8.GetByteCount(dest));
        var s = buf.AsSpan();
        var p = 0;
        Put(s, ref p, "origin|"u8);
        Put(s, ref p, idHex);
        s[p++] = (byte)'|';
        p += Encoding.UTF8.GetBytes(origin, s[p..]);
        s[p++] = (byte)'|';
        p += Encoding.UTF8.GetBytes(dest, s[p..]);
        s[p++] = (byte)'|';
        ts.TryFormat(s[p..], out var w, default, CultureInfo.InvariantCulture);
        p += w;
        s[p++] = (byte)'|';
        ttl.TryFormat(s[p..], out w, default, CultureInfo.InvariantCulture);
        p += w;
        s[p++] = (byte)'|';
        Put(s, ref p, payloadHashHex);
        return p;
    }

    private static int WriteHopCanonical(ref byte[] buf, ReadOnlySpan<byte> idHex, int index, string nodeId, ReadOnlySpan<byte> prevSig)
    {
        EnsureCapacity(ref buf, 192 + Encoding.UTF8.GetByteCount(nodeId));
        var s = buf.AsSpan();
        var p = 0;
        Put(s, ref p, "hop|"u8);
        Put(s, ref p, idHex);
        s[p++] = (byte)'|';
        index.TryFormat(s[p..], out var w, default, CultureInfo.InvariantCulture);
        p += w;
        s[p++] = (byte)'|';
        p += Encoding.UTF8.GetBytes(nodeId, s[p..]);
        s[p++] = (byte)'|';
        WriteHexLower(prevSig, s.Slice(p, prevSig.Length * 2));
        return p + prevSig.Length * 2;
    }

    private static void Put(Span<byte> s, ref int p, ReadOnlySpan<byte> src)
    {
        src.CopyTo(s[p..]);
        p += src.Length;
    }

    private static void EnsureCapacity(ref byte[] buf, int need)
    {
        if (buf.Length >= need) return;
        ArrayPool<byte>.Shared.Return(buf);
        buf = ArrayPool<byte>.Shared.Rent(need);
    }

    private static void WriteHexLower(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var digits = "0123456789abcdef"u8;
        for (var i = 0; i < src.Length; i++)
        {
            dst[i * 2] = digits[src[i] >> 4];
            dst[i * 2 + 1] = digits[src[i] & 0xF];
        }
    }

    private static string HopChain(IReadOnlyList<MeshHopProof> hops)
    {
        var sb = new StringBuilder("hops=").Append(hops.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            sb.Append('|').Append(h.Index.ToString(CultureInfo.InvariantCulture)).Append(h.NodeId).Append(h.SignatureHex);
        }
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ── PQC 서명 스텁(HMAC-SHA512 확산 · ML-DSA 서명 자리) ──
    private static byte[] SignPqc(byte[] seed, byte[] message) => HMACSHA512.HashData(seed, message);

    private static bool VerifyPqc(byte[] seed, byte[] message, string signatureHex)
    {
        try { return CryptographicOperations.FixedTimeEquals(SignPqc(seed, message), Convert.FromHexString(signatureHex ?? string.Empty)); }
        catch (FormatException) { return false; }
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? string.Empty), Encoding.UTF8.GetBytes(b ?? string.Empty));

    // Send 전용 봉투 물질화 수집기(Route 경로는 null).
    private sealed class RouteCapture
    {
        public string MessageId = string.Empty;
        public long TimestampMs;
        public string PayloadHashHex = string.Empty;
        public string OriginSignatureHex = string.Empty;
        public readonly List<MeshHopProof> Hops = new();
        public readonly List<string> Path = new();
    }

    private sealed class MeshNode
    {
        public MeshNode(string id, int index, byte[] seed, string publicKeyHex)
        {
            Id = id;
            Index = index;
            Seed = seed;
            PublicKeyHex = publicKeyHex;
        }

        public string Id { get; }
        public int Index { get; }
        public byte[] Seed { get; }
        public string PublicKeyHex { get; }
        public Dictionary<string, double> Links { get; } = new(StringComparer.Ordinal);
        public MeshNodeState State { get; set; } = MeshNodeState.Alive;
        public long Relayed { get; set; }
    }
}
