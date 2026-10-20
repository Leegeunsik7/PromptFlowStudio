using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>리전 노드 역할 — Active(트래픽 목적지)/Standby(대기).</summary>
public enum RegionRole { Primary, Standby }

/// <summary>리전 헬스 프로브 결과.</summary>
public enum RegionHealth { Unknown, Healthy, Degraded, Down }

/// <summary>리전 노드 스냅샷(불변 · HUD 표출).</summary>
public sealed record RegionNodeView(string Id, string Endpoint, RegionRole Role, RegionHealth Health, double LatencyMs, long ConsecutiveFailures);

/// <summary>
/// [PromptFlow Studio v8.7.1] 멀티 리전 Active-Active 페일오버 엔진(관찰 가능 싱글턴 · 순수 BCL HttpClient/Socket · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="System.Threading.Timer"/> 틱마다 등록 리전을 헬스 프로브(HTTP 엔드포인트는 GET /health, host:port 형태는 TCP 소켓 연결 프로브로 폴백).
///   ▸ 정족수(과반 Healthy/Degraded) 미달 또는 현재 Primary 다운 시 최저지연 Standby 를 자동 승격(<see cref="EvaluateFailover"/>) → <see cref="ResolveActiveEndpoint"/> 로 트래픽 재라우팅.
///   ⚠ <see cref="OnChange"/> 는 Timer(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링. 프로브 실패는 삼켜 Graceful Degradation(1초 내 상태 반영).
/// </summary>
public sealed class MultiRegionFailoverEngine : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private const long FaultThreshold = 3;   // 연속 실패 임계 → Down 격리

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, Region> _regions = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private string? _activeId;

    public MultiRegionFailoverEngine()
    {
        // 부트스트랩 3리전 시드(운영 등록 전 HUD 데모/개발 편의 — RegisterRegion 재호출로 실 인프라 교체).
        RegisterRegion("region-ap", "127.0.0.1:47001", primary: true);
        RegisterRegion("region-us", "127.0.0.1:47002");
        RegisterRegion("region-eu", "127.0.0.1:47003");
        _timer = new System.Threading.Timer(_ => _ = ProbeTickAsync(), null, TimeSpan.FromSeconds(2), ProbeInterval);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public int RegionCount => _regions.Count;
    public int HealthyCount => _regions.Values.Count(r => r.Health is RegionHealth.Healthy or RegionHealth.Degraded);
    public string? ActiveRegionId { get { lock (_gate) return _activeId; } }
    public long FailoverCount { get; private set; }
    public long ProbeCount { get; private set; }
    public bool QuorumOk => RegionCount > 0 && HealthyCount * 2 >= RegionCount;
    public string? LastAction { get; private set; }
    public IReadOnlyList<RegionNodeView> Regions
        => _regions.Values.OrderByDescending(r => r.Role == RegionRole.Primary).ThenBy(r => r.Id).Select(Snapshot).ToList();

    // ── 구성 ──

    /// <summary>리전 등록/갱신. <paramref name="primary"/> 최초 등록분이 기본 Active.</summary>
    public void RegisterRegion(string id, string endpoint, bool primary = false)
    {
        var rid = (id ?? "").Trim();
        if (rid.Length == 0) return;
        _regions[rid] = new Region { Id = rid, Endpoint = (endpoint ?? "").Trim(), Role = primary ? RegionRole.Primary : RegionRole.Standby, Health = RegionHealth.Unknown };
        if (primary) lock (_gate) _activeId ??= rid;
        Notify();
    }

    /// <summary>수동 장애 주입(테스트/HUD 데모) — 즉시 Down 처리 후 페일오버 평가.</summary>
    public void MarkFault(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_regions.TryGetValue(id, out var r)) return;
        r.Health = RegionHealth.Down;
        r.ConsecutiveFailures = FaultThreshold;
        EvaluateFailover();
        Notify();
    }

    /// <summary>현재 활성(트래픽 목적지) 리전 엔드포인트 — Active-Active 라우팅 소비 지점.</summary>
    public string? ResolveActiveEndpoint()
    {
        lock (_gate)
        {
            if (_activeId is not null && _regions.TryGetValue(_activeId, out var a) && a.Health != RegionHealth.Down)
                return a.Endpoint;
        }
        return _regions.Values.Where(r => r.Health != RegionHealth.Down).OrderBy(r => r.LatencyMs).FirstOrDefault()?.Endpoint;
    }

    // ── 헬스 프로빙(BCL HttpClient/Socket) ──

    private async Task ProbeTickAsync()
    {
        try
        {
            foreach (var r in _regions.Values.ToList())
                await ProbeOneAsync(r).ConfigureAwait(false);
            ProbeCount++;
            EvaluateFailover();
            Notify();
        }
        catch (Exception ex) { Program.Log("MultiRegionFailover.Tick", ex); }
    }

    private async Task ProbeOneAsync(Region r)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var cts = new CancellationTokenSource(ProbeTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "health"));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                r.Health = (int)resp.StatusCode < 500 ? RegionHealth.Healthy : RegionHealth.Degraded;   // 4xx 도 도달(HTTP 미구현)로 간주
            }
            else
            {
                var (host, port) = SplitHostPort(r.Endpoint);
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(ProbeTimeout);
                await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                r.Health = RegionHealth.Healthy;
            }
            r.ConsecutiveFailures = 0;
        }
        catch
        {
            r.ConsecutiveFailures++;
            r.Health = r.ConsecutiveFailures >= FaultThreshold ? RegionHealth.Down : RegionHealth.Degraded;
        }
        finally { r.LatencyMs = sw.Elapsed.TotalMilliseconds; }
    }

    private static (string host, int port) SplitHostPort(string endpoint)
    {
        var parts = (endpoint ?? "").Split(':', 2);
        var host = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "127.0.0.1";
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 80;
        return (host, port);
    }

    /// <summary>정족수 미달/Primary 다운 시 최저지연 Standby 를 자동 승격 — 트래픽 재라우팅.</summary>
    private void EvaluateFailover()
    {
        lock (_gate)
        {
            var activeDown = _activeId is null || !_regions.TryGetValue(_activeId, out var a) || a.Health == RegionHealth.Down;
            if (!activeDown) return;
            var best = _regions.Values.Where(r => r.Health != RegionHealth.Down).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (best is null || best.Id == _activeId) return;

            if (_activeId is not null && _regions.TryGetValue(_activeId, out var prevActive)) prevActive.Role = RegionRole.Standby;
            _activeId = best.Id;
            best.Role = RegionRole.Primary;
            FailoverCount++;
            LastAction = $"{best.Id} 승격(쿼럼 {HealthyCount}/{RegionCount})";
        }
    }

    private static RegionNodeView Snapshot(Region r) => new(r.Id, r.Endpoint, r.Role, r.Health, r.LatencyMs, r.ConsecutiveFailures);

    private sealed class Region
    {
        public string Id = "";
        public string Endpoint = "";
        public RegionRole Role;
        public RegionHealth Health;
        public double LatencyMs;
        public long ConsecutiveFailures;
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { /* 무시 */ }
        try { _http.Dispose(); } catch { /* 무시 */ }
    }
}
