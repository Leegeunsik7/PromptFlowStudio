using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>서킷 브레이커 상태 — Closed(정상 통과)/Open(즉시 차단)/HalfOpen(쿨다운 후 단발 프로브).</summary>
public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>메쉬 RPC 응답 상태(HTTP 코드 근사).</summary>
public enum MeshCallStatus { Ok = 200, Unauthorized = 401, CircuitOpen = 503 }

/// <summary>메쉬 RPC 호출 결과(불변).</summary>
public sealed record MeshCallResult(MeshCallStatus Status, string Message, double LatencyMs)
{
    public bool Ok => Status == MeshCallStatus.Ok;
}

/// <summary>메쉬 피어 스냅샷(불변 · HUD 표출).</summary>
public sealed record MeshPeerView(string SpiffeId, string Endpoint, CircuitState State, long Failures, long Successes, double LatencyMs);

/// <summary>
/// [PromptFlow Studio v8.7.3] 제로 트러스트 메쉬 프록시(관찰 가능 싱글턴 · 순수 BCL HttpClient/HMAC · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="IssueIdentityToken"/>/검증: 실 X.509 mTLS 핸드셰이크는 OS 종속이라(제2헌장) HMAC-SHA256 서명 SPIFFE ID 토큰으로 신원·무결성을 대체 검증.
///   ▸ <see cref="InvokeAsync"/>: 토큰 검증 통과분만 서킷 브레이커(Closed/Open/HalfOpen)를 관통해 노드 간 RPC(HTTP GET mesh/health)를 수행.
///     연속 실패 <see cref="FailureThreshold"/> 회 이상 시 Open 격리(즉시 503, 콜 자체 스킵) → <see cref="OpenCooldown"/> 경과 후 HalfOpen 단발 프로브로 자가 회복.
///   ⚠ <see cref="OnChange"/> 는 호출 스레드(임의)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class ZeroTrustMeshProxy : IDisposable
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan OpenCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, PeerCircuit> _peers = new();
    private string _secret = "promptflow-mesh-spiffe-hmac-secret";

    public ZeroTrustMeshProxy()
    {
        // 부트스트랩 데모 피어 시드(운영 등록 전 HUD 데모 편의 — RegisterPeer 재호출로 실 노드 교체).
        RegisterPeer("spiffe://promptflow/node-gpu-01", "http://127.0.0.1:47010");
        RegisterPeer("spiffe://promptflow/node-gpu-02", "http://127.0.0.1:47011");
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    /// <summary>서킷 Open 전이 이벤트(피어 SPIFFE ID) — HUD/감사 로그 즉시 소비.</summary>
    public event Action<string>? OnCircuitTrip;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long TotalCalls { get; private set; }
    public long Allowed { get; private set; }
    public long Denied401 { get; private set; }
    public long DeniedOpen { get; private set; }
    public long TripCount { get; private set; }
    public int PeerCount => _peers.Count;
    public IReadOnlyList<MeshPeerView> Peers => _peers.Values.OrderBy(p => p.SpiffeId).Select(Snapshot).ToList();

    /// <summary>메쉬 공유 시크릿 설정(SPIFFE 토큰 서명 키).</summary>
    public void ConfigureSecret(string? secret) { if (!string.IsNullOrWhiteSpace(secret)) _secret = secret!.Trim(); }

    /// <summary>SPIFFE 피어 등록/갱신 — RPC 엔드포인트 바인딩 + 회로 Closed 초기화.</summary>
    public void RegisterPeer(string spiffeId, string endpoint)
    {
        var id = (spiffeId ?? "").Trim();
        if (id.Length == 0) return;
        _peers[id] = new PeerCircuit { SpiffeId = id, Endpoint = (endpoint ?? "").Trim(), State = CircuitState.Closed };
        Notify();
    }

    /// <summary>mTLS 대체 SPIFFE 신원 토큰 발급(HMAC-SHA256 서명 · ID+만료 임베드).</summary>
    public string IssueIdentityToken(string spiffeId, TimeSpan? ttl = null)
    {
        var exp = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(30)).ToUnixTimeSeconds();
        var payload = $"{spiffeId}.{exp}";
        return $"{payload}.{WebhookSigner.Sign(_secret, payload)}";
    }

    private bool VerifyIdentityToken(string spiffeId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.', 3);
        if (parts.Length != 3 || parts[0] != spiffeId) return false;
        if (!long.TryParse(parts[1], out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
        var expected = WebhookSigner.Sign(_secret, $"{parts[0]}.{parts[1]}");
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2]));
    }

    /// <summary>
    /// mTLS/SPIFFE 검증 + 서킷 브레이커를 관통하는 노드 간 RPC 호출.
    /// 미등록/토큰 무결성 실패=401, Open 상태(쿨다운 미경과)=즉시 503(콜 스킵), 그 외는 실제 RPC 수행 후 결과로 회로 갱신.
    /// </summary>
    public async Task<MeshCallResult> InvokeAsync(string spiffeId, string? identityToken, CancellationToken ct = default)
    {
        TotalCalls++;
        if (!_peers.TryGetValue(spiffeId, out var peer))
        { Denied401++; Notify(); return new MeshCallResult(MeshCallStatus.Unauthorized, "미등록 SPIFFE 피어", 0); }

        if (!VerifyIdentityToken(spiffeId, identityToken))
        { Denied401++; Notify(); return new MeshCallResult(MeshCallStatus.Unauthorized, "mTLS/SPIFFE 토큰 무결성 검증 실패", 0); }

        lock (peer.Gate)
        {
            if (peer.State == CircuitState.Open)
            {
                if (Environment.TickCount64 - peer.OpenedAtTick < OpenCooldown.TotalMilliseconds)
                { DeniedOpen++; Notify(); return new MeshCallResult(MeshCallStatus.CircuitOpen, $"서킷 Open — {peer.SpiffeId} 격리 중", 0); }
                peer.State = CircuitState.HalfOpen;   // 쿨다운 경과 → 단발 프로브 허용
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var ok = Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var uri);
            if (ok)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(CallTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(uri!, "mesh/health"));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                ok = (int)resp.StatusCode < 500;
            }
            RecordResult(peer, ok, sw.Elapsed.TotalMilliseconds);
            Allowed++; Notify();
            return new MeshCallResult(ok ? MeshCallStatus.Ok : MeshCallStatus.CircuitOpen, ok ? "허용" : "피어 5xx", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            RecordResult(peer, false, sw.Elapsed.TotalMilliseconds);
            Notify();
            return new MeshCallResult(MeshCallStatus.CircuitOpen, $"RPC 실패 — {ex.Message}", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>수동 강제 차단(테스트/HUD 데모) — 즉시 Open 전이.</summary>
    public void ForceTrip(string spiffeId)
    {
        if (!_peers.TryGetValue(spiffeId, out var peer)) return;
        lock (peer.Gate)
        {
            peer.Failures = FailureThreshold;
            if (peer.State != CircuitState.Open) { TripCount++; try { OnCircuitTrip?.Invoke(peer.SpiffeId); } catch { /* 구독자 예외 격리 */ } }
            peer.State = CircuitState.Open;
            peer.OpenedAtTick = Environment.TickCount64;
        }
        Notify();
    }

    private void RecordResult(PeerCircuit peer, bool ok, double latencyMs)
    {
        lock (peer.Gate)
        {
            peer.LatencyMs = latencyMs;
            if (ok)
            {
                peer.Successes++;
                peer.Failures = 0;
                peer.State = CircuitState.Closed;   // HalfOpen 프로브 성공 → 회복
            }
            else
            {
                peer.Failures++;
                var shouldTrip = peer.State == CircuitState.HalfOpen || peer.Failures >= FailureThreshold;
                if (shouldTrip)
                {
                    if (peer.State != CircuitState.Open) { TripCount++; try { OnCircuitTrip?.Invoke(peer.SpiffeId); } catch { /* 구독자 예외 격리 */ } }
                    peer.State = CircuitState.Open;
                    peer.OpenedAtTick = Environment.TickCount64;
                }
            }
        }
    }

    private static MeshPeerView Snapshot(PeerCircuit p)
    {
        lock (p.Gate) return new MeshPeerView(p.SpiffeId, p.Endpoint, p.State, p.Failures, p.Successes, p.LatencyMs);
    }

    private sealed class PeerCircuit
    {
        public string SpiffeId = "";
        public string Endpoint = "";
        public CircuitState State = CircuitState.Closed;
        public long Failures;
        public long Successes;
        public long OpenedAtTick;
        public double LatencyMs;
        public readonly object Gate = new();
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { /* 무시 */ }
    }
}
