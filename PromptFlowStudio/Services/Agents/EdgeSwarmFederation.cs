using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Agents;

/// <summary>연합 피어 링크 상태 — 미탐색/연결/성능저하/분단.</summary>
public enum FederationPeerState { Unknown, Linked, Degraded, Partitioned }

/// <summary>P2P 로 중계되는 스웜 델타 봉투(불변 · HMAC-SHA256 서명 포함).</summary>
public sealed record SwarmDeltaEnvelope(
    string OriginNodeId, long Sequence, int Round, string Category, string Payload,
    string Checksum = "", string Signature = "")
{
    /// <summary>서명 대상 정규화 문자열(필드 순서 고정 — 노드 간 동일 직렬화 보장).</summary>
    public string Canonical() => $"{OriginNodeId}|{Sequence}|{Round}|{Category}|{Payload}|{Checksum}";
}

/// <summary>연합 피어 스냅샷(불변 · HUD 표출).</summary>
public sealed record FederationPeerView(
    string NodeId, string Endpoint, FederationPeerState State,
    long Sent, long Failed, double RttMs, int LastRound, bool Agreed);

/// <summary>
/// [PromptFlow Studio v8.8.1] 엣지 스웜 연합 연동 엔진(관찰 가능 싱글턴 · 순수 BCL HttpClient/Channel · 외부 NuGet 0 · IDisposable).
///   ▸ 로컬 <see cref="AgentSwarmOrchestrator"/> 의 6라운드 자율 수렴 상태를 <b>델타 봉투</b>로 압축해 엣지 피어에 P2P 중계(<see cref="Channel{T}"/> 아웃바운드 펌프 · 논블로킹).
///   ▸ 수신 델타는 HMAC-SHA256 상수시간 검증 → 지문 해시 dedupe(재전송/중계 루프 중복 0) 후 적용 → <b>멱등</b>(같은 델타는 몇 번 와도 같은 결과).
///   ▸ 라운드 체크섬 합의율(<see cref="AgreementRatio"/>)이 100% 이거나 <see cref="MaxRounds"/> 도달 시 수렴 종료 — 분단 피어는 하트비트(15초)로 자동 재편입.
///   ⚠ <see cref="OnChange"/> 는 펌프/타이머(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장). 전송 실패는 삼켜 Graceful Degradation(로컬 델타·수렴 루프 보존).
/// </summary>
public sealed class EdgeSwarmFederation : IDisposable
{
    /// <summary>자율 수렴 최대 라운드(로컬 스웜과 동일 상한).</summary>
    public const int MaxRounds = 6;

    private const int DedupeCap = 512;
    private const int LogCap = 40;
    private const long FailThreshold = 3;               // 연속 실패 임계 → Partitioned 격리
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http = new() { Timeout = SendTimeout };
    private readonly Channel<SwarmDeltaEnvelope> _out =
        Channel.CreateUnbounded<SwarmDeltaEnvelope>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly System.Threading.Timer _heartbeat;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly List<string> _log = new();
    private readonly AgentSwarmOrchestrator? _swarm;

    private byte[] _key = RandomNumberGenerator.GetBytes(32);   // 연합 공유 서명키(운영 시 SetFederationKey 로 교체)
    private long _seq;

    public EdgeSwarmFederation(AgentSwarmOrchestrator? swarm = null)
    {
        _swarm = swarm;
        NodeId = $"edge-{Environment.MachineName.ToLowerInvariant()}";
        _pump = Task.Run(() => PumpAsync(_cts.Token));
        _heartbeat = new System.Threading.Timer(_ => _ = HeartbeatTickAsync(), null, TimeSpan.FromSeconds(5), HeartbeatInterval);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public string NodeId { get; }
    public int PeerCount => _peers.Count;
    public int LinkedCount => _peers.Values.Count(p => p.State is FederationPeerState.Linked or FederationPeerState.Degraded);
    public long DeltasOut { get; private set; }
    public long DeltasIn { get; private set; }
    public long DuplicatesDropped { get; private set; }
    public long SignatureRejects { get; private set; }
    public int CurrentRound { get; private set; }
    public int ConvergedRound { get; private set; }
    public bool IsFederating { get; private set; }
    public string? LocalChecksum { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>합의율 — 로컬 라운드 체크섬과 동일 값을 보고한 Linked/Degraded 피어 비율(피어 0 = 단독 노드 = 1).</summary>
    public double AgreementRatio
    {
        get
        {
            var linked = _peers.Values.Where(p => p.State is FederationPeerState.Linked or FederationPeerState.Degraded).ToList();
            if (linked.Count == 0) return 1d;
            var head = LocalChecksum;
            if (string.IsNullOrEmpty(head)) return 0d;
            return linked.Count(p => string.Equals(p.LastChecksum, head, StringComparison.Ordinal)) / (double)linked.Count;
        }
    }

    public bool IsConverged => ConvergedRound > 0 && AgreementRatio >= 1d;
    public IReadOnlyList<FederationPeerView> Peers
        => _peers.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(Snapshot).ToList();
    public IReadOnlyList<string> RecentLog { get { lock (_gate) return _log.Take(12).ToList(); } }

    // ── 구성 ──

    /// <summary>피어 등록/갱신(멱등 — 같은 id 재등록 시 엔드포인트만 교체).</summary>
    public void RegisterPeer(string nodeId, string endpoint)
    {
        var id = (nodeId ?? "").Trim();
        var ep = (endpoint ?? "").Trim();
        if (id.Length == 0 || ep.Length == 0) return;
        var peer = _peers.GetOrAdd(id, key => new Peer(key, ep));
        peer.Endpoint = ep;
        Log($"피어 등록 {id} → {ep}");
        Notify();
    }

    /// <summary>피어 해제.</summary>
    public void RemovePeer(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId) && _peers.TryRemove(nodeId.Trim(), out _))
        {
            Log($"피어 해제 {nodeId}");
            Notify();
        }
    }

    /// <summary>연합 공유 서명키 교체(모든 엣지 노드가 동일 키를 가져야 상호 검증 성립).</summary>
    public void SetFederationKey(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        Log("연합 서명키 교체 — 기존 서명 무효화");
        Notify();
    }

    // ── 발행 / 중계 ──

    /// <summary>로컬 델타 발행 → dedupe 등록 후 아웃바운드 채널 적재(호출 스레드 논블로킹).</summary>
    public SwarmDeltaEnvelope PublishDelta(int round, string category, string payload)
    {
        var seq = Interlocked.Increment(ref _seq);
        var checksum = LocalChecksum ?? ComputeChecksum(round);
        var env = new SwarmDeltaEnvelope(NodeId, seq, round, string.IsNullOrWhiteSpace(category) ? "delta" : category, payload ?? "", checksum);
        env = env with { Signature = Sign(env.Canonical()) };
        MarkSeen(FingerprintOf(env));
        _out.Writer.TryWrite(env);
        DeltasOut++;
        return env;
    }

    /// <summary>
    /// 원격 피어 델타 수신 — 서명 검증 → 중복 차단 → 적용. 같은 봉투 재수신은 항상 false(멱등).
    /// </summary>
    public bool ApplyInbound(SwarmDeltaEnvelope? env)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.OriginNodeId)) return false;
        if (!VerifySignature(env))
        {
            SignatureRejects++;
            Log($"서명 거부 {env.OriginNodeId}#{env.Sequence}");
            Notify();
            return false;
        }

        var fp = FingerprintOf(env);
        lock (_gate)
        {
            if (_seen.Contains(fp)) { DuplicatesDropped++; return false; }
        }
        MarkSeen(fp);

        var peer = _peers.GetOrAdd(env.OriginNodeId, id => new Peer(id, ""));
        peer.LastRound = env.Round;
        peer.LastChecksum = env.Checksum;
        peer.LastSeen = DateTimeOffset.Now;
        peer.Failures = 0;
        peer.State = FederationPeerState.Linked;

        DeltasIn++;
        if (env.Round > CurrentRound) CurrentRound = env.Round;
        Log($"수신 {env.OriginNodeId}#{env.Sequence} r{env.Round} · {env.Category}");
        Notify();
        return true;
    }

    /// <summary>
    /// 연합 자율 수렴 — 라운드마다 로컬 스웜 지표 체크섬을 델타로 브로드캐스트하고 피어 합의율을 평가한다.
    /// 합의 100% 도달 시 조기 종료(멱등 · 같은 상태면 같은 체크섬으로 즉시 수렴 · 무한루프 없음).
    /// </summary>
    public async Task<string> RunFederatedConvergenceAsync(int maxRounds = MaxRounds, CancellationToken ct = default)
    {
        if (IsFederating) return "이미 연합 수렴이 실행 중입니다.";
        IsFederating = true;
        ConvergedRound = 0;
        Notify();

        var rounds = Math.Clamp(maxRounds, 1, MaxRounds);
        try
        {
            for (var r = 1; r <= rounds; r++)
            {
                ct.ThrowIfCancellationRequested();
                CurrentRound = r;
                LocalChecksum = ComputeChecksum(r);
                PublishDelta(r, "round-sync", $"defects={_swarm?.DefectsFound ?? 0};resolved={_swarm?.DefectsResolved ?? 0}");
                Notify();

                await Task.Delay(120, ct).ConfigureAwait(false);   // 델타 전파 여유(펌프 소비 · 피어 ACK)

                ConvergedRound = r;
                if (AgreementRatio >= 1d)
                {
                    LastAction = $"라운드 {r} 합의 100% — 연합 수렴 완료";
                    Log(LastAction);
                    Notify();
                    return LastAction;
                }
            }
            LastAction = $"라운드 {rounds} 종료 — 합의율 {(AgreementRatio * 100):0}% (잔여 피어 하트비트 재동기 대기)";
        }
        catch (OperationCanceledException) { LastAction = "연합 수렴 취소됨(발행 델타는 보존)"; }
        catch (Exception ex)
        {
            LastAction = "연합 수렴 실패 — 로컬 상태 유지";
            Program.Log("EdgeSwarmFederation.Converge", ex);
        }
        finally
        {
            IsFederating = false;
            Log(LastAction ?? "");
            Notify();
        }
        return LastAction ?? "";
    }

    // ── 내부: 아웃바운드 펌프 ──

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var env in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var peer in _peers.Values)
                {
                    if (peer.Endpoint.Length == 0) continue;
                    await RelayAsync(peer, env, ct).ConfigureAwait(false);
                }
                Notify();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("EdgeSwarmFederation.Pump", ex); }
    }

    private async Task RelayAsync(Peer peer, SwarmDeltaEnvelope env, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var json = JsonSerializer.Serialize(env, PromptFlowJsonContext.Default.SwarmDeltaEnvelope);
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(peer.Endpoint)) { Content = body };
            req.Headers.TryAddWithoutValidation("X-Swarm-Node", NodeId);
            req.Headers.TryAddWithoutValidation("X-Swarm-Sign", env.Signature);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(SendTimeout);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            peer.Sent++;
            peer.LastSeen = DateTimeOffset.Now;
            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            peer.Rtt = peer.Rtt <= 0 ? elapsed : peer.Rtt * 0.7 + elapsed * 0.3;   // EWMA 왕복지연
            if (res.IsSuccessStatusCode)
            {
                peer.Failures = 0;
                peer.State = FederationPeerState.Linked;
                peer.LastRound = env.Round;
                peer.LastChecksum = env.Checksum;   // 성공 ACK = 동일 델타 수용(체크섬 합의)
            }
            else
            {
                peer.State = FederationPeerState.Degraded;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 네트워크/타임아웃 실패는 삼키고 상태만 강등 — 로컬 델타와 수렴 루프는 계속 진행(제5헌장).
            peer.Failed++;
            peer.Failures++;
            peer.State = peer.Failures >= FailThreshold ? FederationPeerState.Partitioned : FederationPeerState.Degraded;
        }
    }

    private async Task HeartbeatTickAsync()
    {
        if (_cts.IsCancellationRequested) return;
        try
        {
            var revived = 0;
            foreach (var peer in _peers.Values)
            {
                if (peer.State != FederationPeerState.Partitioned || peer.Endpoint.Length == 0) continue;
                var probe = new SwarmDeltaEnvelope(NodeId, Interlocked.Increment(ref _seq), CurrentRound, "heartbeat", "ping", LocalChecksum ?? "");
                probe = probe with { Signature = Sign(probe.Canonical()) };
                await RelayAsync(peer, probe, _cts.Token).ConfigureAwait(false);
                if (peer.State == FederationPeerState.Linked) revived++;
            }
            if (revived > 0)
            {
                LastAction = $"하트비트 재편입 {revived}피어";
                Log(LastAction);
            }
            Notify();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("EdgeSwarmFederation.Heartbeat", ex); }
    }

    // ── 내부: 서명 · 체크섬 · dedupe ──

    private string Sign(string canonical)
    {
        using var mac = new HMACSHA256(_key);
        return Convert.ToHexString(mac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private bool VerifySignature(SwarmDeltaEnvelope env)
    {
        if (string.IsNullOrEmpty(env.Signature)) return false;
        var expected = Encoding.UTF8.GetBytes(Sign(env.Canonical()));
        var actual = Encoding.UTF8.GetBytes(env.Signature.ToLowerInvariant());
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>라운드 체크섬 — 로컬 스웜 수렴 지표의 결정적 해시(같은 상태 = 같은 값 = 즉시 합의).</summary>
    private string ComputeChecksum(int round)
    {
        var seed = $"{round}|{_swarm?.TotalRounds ?? 0}|{_swarm?.DefectsFound ?? 0}|{_swarm?.DefectsResolved ?? 0}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..12].ToLowerInvariant();
    }

    private static string FingerprintOf(SwarmDeltaEnvelope env)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(env.Canonical())))[..16];

    private void MarkSeen(string fingerprint)
    {
        lock (_gate)
        {
            if (!_seen.Add(fingerprint)) return;
            _seenOrder.Enqueue(fingerprint);
            while (_seenOrder.Count > DedupeCap) _seen.Remove(_seenOrder.Dequeue());
        }
    }

    private static Uri BuildUri(string endpoint)
    {
        var raw = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : $"http://{endpoint}";
        if (!raw.EndsWith('/')) raw += "/";
        return new Uri(new Uri(raw), "api/v1/swarm/delta");
    }

    private void Log(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            _log.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss} {line}");
            if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap);
        }
    }

    private FederationPeerView Snapshot(Peer p)
        => new(p.Id, p.Endpoint, p.State, p.Sent, p.Failed, Math.Round(p.Rtt, 1), p.LastRound,
               !string.IsNullOrEmpty(LocalChecksum) && string.Equals(p.LastChecksum, LocalChecksum, StringComparison.Ordinal));

    /// <summary>피어 가변 상태(펌프/타이머 스레드에서만 변조 · 스냅샷으로만 노출).</summary>
    private sealed class Peer(string id, string endpoint)
    {
        public string Id { get; } = id;
        public string Endpoint { get; set; } = endpoint;
        public FederationPeerState State { get; set; } = FederationPeerState.Unknown;
        public long Sent;
        public long Failed;
        public long Failures;
        public double Rtt;
        public int LastRound;
        public string? LastChecksum { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 무시 */ }
        try { _out.Writer.TryComplete(); } catch { /* 무시 */ }
        try { _heartbeat.Dispose(); } catch { /* 무시 */ }
        try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch { /* 무시 */ }
        try { _http.Dispose(); } catch { /* 무시 */ }
        try { _cts.Dispose(); } catch { /* 무시 */ }
    }
}
