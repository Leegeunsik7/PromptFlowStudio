using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

/// <summary>
/// [PromptFlow Studio v6.2.0] B2B 테넌트 구성 — 식별자·API Key HMAC 해시·허용 RPS·버스트 용량(불변 값).
///   ▸ <see cref="ApiKeyHash"/> = HMAC-SHA256(gatewaySecret, apiKey) — 원문 키는 저장하지 않는다(유출 방어).
/// </summary>
public sealed record TenantConfig(
    string TenantId, string ApiKeyHash, double RpsLimit, int BurstCapacity, bool Enabled = true);

/// <summary>
/// [PromptFlow Studio v6.2.0] Lock-free Token Bucket(순수 BCL · <see cref="Interlocked"/> CAS 루프).
///   ▸ 토큰은 마이크로 단위 <see cref="long"/> 로 스케일해 원자적으로 증감 → 초당 수백 건 동시 요청에도 락 없이 안전.
///   ▸ 리필은 <see cref="Stopwatch"/> 고해상도 경과로 계산하며, 단일 스레드만 타임스탬프를 전진시킨다(중복 리필 방지).
/// </summary>
public sealed class TokenBucket
{
    private const double Scale = 1_000_000.0;
    private readonly double _capacity;
    private readonly double _refillPerSec;
    private readonly long _capacityMicro;
    private long _tokensMicro;   // 현재 토큰 × Scale(Interlocked)
    private long _lastTicks;     // 마지막 리필 시각(Stopwatch 틱 · Interlocked)

    public TokenBucket(double capacity, double refillPerSec)
    {
        _capacity = Math.Max(1, capacity);
        _refillPerSec = Math.Max(0.1, refillPerSec);
        _capacityMicro = (long)(_capacity * Scale);
        _tokensMicro = _capacityMicro;   // 시작은 가득 채움(버스트 허용)
        _lastTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>토큰 <paramref name="cost"/>개 소비 시도 — 부족하면 false(요청 거부). 원자적.</summary>
    public bool TryConsume(double cost = 1)
    {
        Refill();
        var costMicro = (long)(Math.Max(0, cost) * Scale);
        while (true)
        {
            var cur = Interlocked.Read(ref _tokensMicro);
            if (cur < costMicro) return false;
            if (Interlocked.CompareExchange(ref _tokensMicro, cur - costMicro, cur) == cur) return true;
            // 경합 → 재시도(CAS 루프)
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastTicks);
        var elapsed = (now - last) / (double)Stopwatch.Frequency;
        if (elapsed <= 0) return;
        // 타임스탬프를 전진시킨 단일 스레드만 실제 리필 수행(중복 가산 차단).
        if (Interlocked.CompareExchange(ref _lastTicks, now, last) != last) return;

        var add = (long)(elapsed * _refillPerSec * Scale);
        if (add <= 0) return;
        while (true)
        {
            var cur = Interlocked.Read(ref _tokensMicro);
            var next = Math.Min(_capacityMicro, cur + add);
            if (Interlocked.CompareExchange(ref _tokensMicro, next, cur) == cur) return;
        }
    }

    /// <summary>현재 가용 토큰(표시용 · 근사).</summary>
    public double AvailableTokens => Interlocked.Read(ref _tokensMicro) / Scale;
    public double Capacity => _capacity;
    /// <summary>가용 비율 0~1(HUD 게이지).</summary>
    public double FillRatio => _capacity <= 0 ? 0 : Math.Clamp(AvailableTokens / _capacity, 0, 1);
}

/// <summary>
/// [PromptFlow Studio v6.2.0] 테넌트별 Token Bucket 레지스트리 — 초당 수백 건 동시 요청을 UI/렌더 블로킹 없이 제어.
///   ▸ <see cref="ConcurrentDictionary{TKey,TValue}"/> 로 테넌트당 버킷을 관리(스레드 안전) · 각 버킷은 Interlocked lock-free.
/// </summary>
public sealed class TenantRateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();

    /// <summary>테넌트 한도로 토큰 1개 소비 시도(버킷 없으면 생성). false=한도 초과.</summary>
    public bool TryAcquire(TenantConfig cfg, double cost = 1)
    {
        if (cfg is null) return false;
        var b = _buckets.GetOrAdd(cfg.TenantId,
            _ => new TokenBucket(Math.Max(1, cfg.BurstCapacity), cfg.RpsLimit));
        return b.TryConsume(cost);
    }

    /// <summary>테넌트 버킷 가용 비율(0~1) — 없으면 1(가득).</summary>
    public double FillRatio(string tenantId)
        => _buckets.TryGetValue(tenantId, out var b) ? b.FillRatio : 1;

    /// <summary>테넌트 버킷 초기화(구성 변경·한도 재적용 시 다음 요청에 새 버킷 생성).</summary>
    public void Reset(string tenantId) => _buckets.TryRemove(tenantId, out _);
}
