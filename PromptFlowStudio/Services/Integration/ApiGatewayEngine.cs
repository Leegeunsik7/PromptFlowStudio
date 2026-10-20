using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Flow;  // FlowExecutionPipeline.ConcatInputs(세그먼트 용량 상한)
using PromptFlowStudio.Services.Wan;   // DistributedRenderGrid · WanRenderClient · WanPromptBuild · WanRenderRequest

namespace PromptFlowStudio.Services.Integration;

/// <summary>게이트웨이 응답 상태(HTTP 코드 매핑).</summary>
public enum GatewayStatus { Ok = 200, Unauthorized = 401, Forbidden = 403, TooManyRequests = 429, ServiceUnavailable = 503 }

/// <summary>API 게이트웨이 처리 결과(불변).</summary>
public sealed record ApiGatewayResult(GatewayStatus Status, string Message, string? TenantId = null, int? RetryAfterSec = null)
{
    public bool Ok => Status == GatewayStatus.Ok;
    public int Code => (int)Status;
}

/// <summary>게이트웨이 카운터 스냅샷(텔레메트리 집계용).</summary>
public sealed record GatewayCounters(long Total, long Allowed, long Denied429, long Denied401, long Dispatched, int TenantCount);

/// <summary>
/// [PromptFlow Studio v6.2.0] B2B API 게이트웨이 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 외부 고객사 요청의 <b>X-API-Key</b> 를 HMAC-SHA256 해시 대조로 검증(원문 키 미저장 · 상수시간 비교) → 테넌트 식별.
///   ▸ <see cref="TenantRateLimiter"/> Token Bucket 으로 테넌트별 RPS 를 제어 — 초과 시 <b>429 Too Many Requests</b> 거부(Retry-After 안내).
///   ▸ 인증·레이트리밋 통과분만 <see cref="DistributedRenderGrid"/> 로 렌더 요청을 이관(가용 워커 0이면 503).
///   ⚠ <see cref="OnChange"/> 는 임의 스레드(동시 요청)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class ApiGatewayEngine
{
    private readonly ConcurrentDictionary<string, TenantConfig> _tenants = new();
    private readonly TenantRateLimiter _limiter = new();
    private readonly DistributedRenderGrid _grid;
    private string _secret = "promptflow-gateway-hmac-secret";

    private long _total, _allowed, _denied429, _denied401, _dispatched;

    public ApiGatewayEngine(DistributedRenderGrid grid) { _grid = grid; }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 카운터(텔레메트리) ──
    public long TotalRequests => Interlocked.Read(ref _total);
    public long AllowedCount => Interlocked.Read(ref _allowed);
    public long Denied429Count => Interlocked.Read(ref _denied429);
    public long Denied401Count => Interlocked.Read(ref _denied401);
    public long DispatchedCount => Interlocked.Read(ref _dispatched);
    public int TenantCount => _tenants.Count;
    public IReadOnlyList<TenantConfig> Tenants => _tenants.Values.OrderBy(t => t.TenantId).ToList();

    public GatewayCounters Snapshot() => new(TotalRequests, AllowedCount, Denied429Count, Denied401Count, DispatchedCount, TenantCount);

    /// <summary>테넌트별 레이트리밋 가용 비율(0~1 · HUD 게이지).</summary>
    public double RateFillRatio(string tenantId) => _limiter.FillRatio(tenantId);

    // ── 구성 ───────────────────────────────────────────────────────────────

    /// <summary>게이트웨이 공유 시크릿 설정(API Key 해시 산출용).</summary>
    public void ConfigureSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret)) _secret = secret!.Trim();
    }

    /// <summary>
    /// 테넌트 등록/갱신 — 원문 <paramref name="apiKey"/> 를 HMAC 해시로 봉인해 저장하고 레이트 버킷을 재설정한다.
    /// </summary>
    public TenantConfig RegisterTenant(string tenantId, string apiKey, double rpsLimit, int burstCapacity)
    {
        var id = (tenantId ?? "").Trim();
        var cfg = new TenantConfig(
            id, WebhookSigner.Sign(_secret, apiKey ?? ""),
            Math.Max(0.1, rpsLimit), Math.Max(1, burstCapacity), true);
        _tenants[id] = cfg;
        _limiter.Reset(id);   // 한도 변경분 반영(다음 요청에 새 버킷)
        Notify();
        return cfg;
    }

    /// <summary>테넌트 활성/비활성 전환.</summary>
    public void SetEnabled(string tenantId, bool enabled)
    {
        if (_tenants.TryGetValue(tenantId, out var c)) { _tenants[tenantId] = c with { Enabled = enabled }; Notify(); }
    }

    /// <summary>테넌트 제거.</summary>
    public void RemoveTenant(string tenantId)
    {
        if (_tenants.TryRemove(tenantId, out _)) { _limiter.Reset(tenantId); Notify(); }
    }

    // ── 인증 & 처리 ──────────────────────────────────────────────────────────

    /// <summary>X-API-Key → 테넌트 식별(HMAC 해시 상수시간 대조). 실패 시 null.</summary>
    private TenantConfig? Authenticate(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var hash = Encoding.UTF8.GetBytes(WebhookSigner.Sign(_secret, apiKey));
        foreach (var t in _tenants.Values)
            if (CryptographicOperations.FixedTimeEquals(hash, Encoding.UTF8.GetBytes(t.ApiKeyHash)))
                return t;
        return null;
    }

    /// <summary>
    /// 요청 인가 — X-API-Key 검증 + 레이트리밋. 401(키 무효)·403(비활성)·429(한도초과)·200(허용).
    /// </summary>
    public ApiGatewayResult Authorize(string? apiKey)
    {
        Interlocked.Increment(ref _total);
        var t = Authenticate(apiKey);
        if (t is null)
        {
            Interlocked.Increment(ref _denied401); Notify();
            return new(GatewayStatus.Unauthorized, "유효하지 않은 X-API-Key");
        }
        if (!t.Enabled)
        {
            Notify();
            return new(GatewayStatus.Forbidden, "비활성 테넌트입니다.", t.TenantId);
        }
        if (!_limiter.TryAcquire(t))
        {
            Interlocked.Increment(ref _denied429); Notify();
            var retry = (int)Math.Ceiling(1.0 / Math.Max(0.1, t.RpsLimit));
            return new(GatewayStatus.TooManyRequests, $"RPS 한도 초과({t.RpsLimit:0.#}/s) — 잠시 후 재시도", t.TenantId, retry);
        }
        Interlocked.Increment(ref _allowed); Notify();
        return new(GatewayStatus.Ok, "허용됨", t.TenantId);
    }

    /// <summary>
    /// 인증·레이트리밋 통과 시 렌더 요청을 GPU 분산 그리드로 이관한다(가용 워커 0이면 503). 게이트웨이는 '수락'만 하고 fire-and-forget.
    /// </summary>
    public Task<ApiGatewayResult> SubmitRenderAsync(string? apiKey, string prompt, int segments, CancellationToken ct = default)
    {
        var auth = Authorize(apiKey);
        if (!auth.Ok) return Task.FromResult(auth);
        if (string.IsNullOrWhiteSpace(prompt))
            return Task.FromResult(new ApiGatewayResult(GatewayStatus.Ok, "빈 프롬프트 — 렌더 없음", auth.TenantId));
        if (_grid.UsableNodeCount == 0)
            return Task.FromResult(new ApiGatewayResult(GatewayStatus.ServiceUnavailable, "가용 GPU 워커 없음 — 잠시 후 재시도", auth.TenantId, 5));

        var reqs = BuildRequests(prompt, segments);
        Interlocked.Increment(ref _dispatched); Notify();
        _ = _grid.DispatchAsync(reqs, null, ct);   // 수락 후 비동기 이관(그리드가 내부 페일오버·자산화 처리)
        return Task.FromResult(new ApiGatewayResult(GatewayStatus.Ok, $"수락 — {reqs.Count}세그먼트 분산 배정", auth.TenantId));
    }

    private static List<WanRenderRequest> BuildRequests(string prompt, int segments)
    {
        var n = Math.Clamp(segments, 1, FlowExecutionPipeline.ConcatInputs);
        var list = new List<WanRenderRequest>(n);
        for (var i = 0; i < n; i++)
            list.Add(WanRenderClient.BuildRequest(new WanPromptBuild(prompt, 30, Array.Empty<string>(), null), null));
        return list;
    }
}
