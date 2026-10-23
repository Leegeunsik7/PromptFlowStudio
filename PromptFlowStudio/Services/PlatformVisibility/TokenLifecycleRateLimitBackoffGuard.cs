using System.Buffers.Text;
using System.Security.Cryptography;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.LivePublish;

namespace PromptFlowStudio.Services.PlatformVisibility;

public enum QuotaDecision : byte { Allowed, NoToken, BackingOff, SafetyThrottled }

/// <summary>액세스 토큰 갱신 공급자 — 성공 시 <paramref name="dst"/> 에 새 토큰 · 만료 시각(Unix 초) 반환.</summary>
public interface IPlatformTokenSource
{
    bool TryRefresh(LiveProvider p, double now, GraphSecretSlot dst, out double expiresAt);
}

/// <summary>오프라인 결정적 토큰 공급자 — <c>sim-{yt|ig}-{n}</c> 발급 · 수명 1 시간 · 실패 주입(1 회 소모).</summary>
public sealed class SimulatedPlatformTokenSource : IPlatformTokenSource
{
    private int _failNext;
    public double LifetimeSec { get; set; } = 3600;
    public long Issued { get; private set; }

    public void InjectFailures(int count) => _failNext = Math.Max(0, count);

    public bool TryRefresh(LiveProvider p, double now, GraphSecretSlot dst, out double expiresAt)
    {
        expiresAt = 0;
        if (_failNext > 0) { _failNext--; return false; }
        Span<byte> tok = stackalloc byte[32];
        var n = 0;
        GraphBytes.Put(tok, ref n, p == LiveProvider.YouTube ? "sim-yt-"u8 : "sim-ig-"u8);
        GraphBytes.Put(tok, ref n, ++Issued);
        dst.Set(tok[..n]);
        CryptographicOperations.ZeroMemory(tok);
        expiresAt = now + LifetimeSec;
        return true;
    }
}

/// <summary>
/// v17.79 OAuth2 볼트 결선 공급자 — 실 자격증명(LIVE) 플랫폼은 볼트 틱(만료 10 분 전 Refresh Token Exchange)을 돌린 뒤 액세스 토큰을 재암호화 복사,
/// 계약 Mock 플랫폼은 <see cref="SimulatedPlatformTokenSource"/> 로 폴백.
/// </summary>
public sealed class VaultPlatformTokenSource : IPlatformTokenSource
{
    private readonly ProductionOAuth2TokenVaultGateway _vault;
    private readonly IPlatformTokenSource _fallback;

    public VaultPlatformTokenSource(ProductionOAuth2TokenVaultGateway vault, IPlatformTokenSource fallback)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public bool TryRefresh(LiveProvider p, double now, GraphSecretSlot dst, out double expiresAt)
    {
        if (!_vault.IsLive(p)) return _fallback.TryRefresh(p, now, dst, out expiresAt);
        expiresAt = 0;
        _vault.Tick(now);
        var a = _vault.Account(p);
        if (!a.IsConnected || !_vault.CopyAccessTo(p, dst)) return false;
        expiresAt = a.ExpiresAt;
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v17.94.0] 🛡️ 토큰 생명주기 자동 갱신 &amp; 429 지수 백오프 세이프티 가드.
///   ▸ 플랫폼별 액세스 토큰을 <see cref="GraphSecretSlot"/> 암호문으로 보관 — 만료 <see cref="RefreshLeadSec"/>(5 분) 전 <see cref="Tick"/> 에서 무할당 갱신.
///   ▸ 429 · rateLimitExceeded · Graph 코드 4/17/32/613 → 지수 백오프(1.5 s × 2ⁿ⁻¹ · 상한 30 s · xorshift 지터 0~25 %) 와 Retry-After 중 큰 값까지 차단.
///   ▸ 일일 쿼터(YouTube 10,000 units · Instagram 4,800 calls)를 UTC 일 단위 인메모리 카운터로 추적 — 90 % 초과 요청은 안전 정지, quotaExceeded 응답은 당일 소진 처리.
/// </summary>
public sealed class TokenLifecycleRateLimitBackoffGuard : IDisposable
{
    public const double RefreshLeadSec = 300, BackoffBaseSec = 1.5, BackoffMaxSec = 30, SafetyRatio = 0.9, RetryAfterCapSec = 3600;
    public const int YouTubeDailyQuota = 10_000, InstagramDailyQuota = 4_800;
    public const int CostYtUpdate = 50, CostYtDelete = 50, CostYtList = 1, CostAnalytics = 1, CostIg = 1;

    private readonly GraphSecretSlot[] _token = { new(), new() };
    private readonly double[] _expiresAt = new double[2], _blockedUntil = new double[2];
    private readonly int[] _hits = new int[2], _used = new int[2], _limit = { YouTubeDailyQuota, InstagramDailyQuota };
    private readonly long[] _day = { -1, -1 };
    private uint _rng = 0x9E3779B9u;

    public TokenLifecycleRateLimitBackoffGuard(IPlatformTokenSource source) => Source = source ?? throw new ArgumentNullException(nameof(source));

    public IPlatformTokenSource Source { get; }
    /// <summary>백오프 대기 — 기본 Task.Delay, 테스트는 가상 시계 전진(동기 완료 · 무할당).</summary>
    public Func<double, CancellationToken, ValueTask> Delay { get; set; } = static (sec, ct) => new ValueTask(Task.Delay(TimeSpan.FromSeconds(sec), ct));
    public long Refreshes { get; private set; }
    public long BackgroundRefreshes { get; private set; }
    public long RefreshFailures { get; private set; }
    public long RateLimitHits { get; private set; }
    public long QuotaExhaustions { get; private set; }
    public long SafetyThrottles { get; private set; }
    public double LastBackoffSec { get; private set; }

    public bool HasToken(LiveProvider p) => _token[(int)p].IsSet;
    public double ExpiresAt(LiveProvider p) => _expiresAt[(int)p];
    public double SecondsLeft(LiveProvider p, double now) => _token[(int)p].IsSet ? Math.Max(0, _expiresAt[(int)p] - now) : 0;
    public double BlockedUntil(LiveProvider p) => _blockedUntil[(int)p];
    public int ConsecutiveHits(LiveProvider p) => _hits[(int)p];
    public int QuotaUsed(LiveProvider p) => _used[(int)p];
    public int QuotaLimit(LiveProvider p) => _limit[(int)p];
    public float QuotaRatio(LiveProvider p) => (float)_used[(int)p] / _limit[(int)p];
    public bool Throttled(LiveProvider p) => _used[(int)p] >= _limit[(int)p] * SafetyRatio;

    public void SetQuotaLimit(LiveProvider p, int units) => _limit[(int)p] = Math.Max(1, units);

    // ══════════════════════ 토큰 생명주기(무할당) ══════════════════════

    /// <summary>요청 직전 토큰 보장 — 미보유 · 만료 5 분 이내면 즉시 갱신. 반환 = 유효 토큰 보유.</summary>
    public bool EnsureToken(LiveProvider p, double now)
    {
        var i = (int)p;
        if (_token[i].IsSet && _expiresAt[i] - now > RefreshLeadSec) return true;
        Refresh(i, now);
        return _token[i].IsSet && _expiresAt[i] > now;
    }

    /// <summary>백그라운드 틱 — 보유 토큰 중 만료 5 분 이내인 것만 갱신(미보유 플랫폼은 첫 요청 시 지연 발급).</summary>
    public void Tick(double now)
    {
        for (var i = 0; i < 2; i++)
        {
            if (!_token[i].IsSet || _expiresAt[i] - now > RefreshLeadSec) continue;
            BackgroundRefreshes++;
            Refresh(i, now);
        }
    }

    private void Refresh(int i, double now)
    {
        bool ok;
        double exp;
        try { ok = Source.TryRefresh((LiveProvider)i, now, _token[i], out exp); }
        catch (Exception ex) { ok = false; exp = 0; PromptFlowStudio.Program.Log("PlatformVisibility.TokenRefresh", ex); }
        if (ok && exp > now) { _expiresAt[i] = exp; Refreshes++; }
        else RefreshFailures++;
    }

    /// <summary>401 수신 — 토큰 폐기(다음 요청에서 강제 재발급).</summary>
    public void Invalidate(LiveProvider p) { _token[(int)p].Clear(); _expiresAt[(int)p] = 0; }

    /// <summary>만료 시각 강제 조정(데모 · 테스트) — 토큰 보유 시만.</summary>
    public void ForceExpiry(LiveProvider p, double expiresAt) { if (_token[(int)p].IsSet) _expiresAt[(int)p] = expiresAt; }

    /// <summary><c>Authorization: Bearer …\r\n</c> 헤더 줄을 dst 에 직접 복호화(무할당) — 길이 반환, 호출자 제로화 책임.</summary>
    public int WriteBearer(LiveProvider p, Span<byte> dst)
    {
        var t = _token[(int)p];
        if (!t.IsSet) return 0;
        var n = 0;
        GraphBytes.Put(dst, ref n, "Authorization: Bearer "u8);
        n += t.CopyTo(dst[n..]);
        GraphBytes.Put(dst, ref n, "\r\n"u8);
        return n;
    }

    // ══════════════════════ 쿼터 · 백오프(무할당) ══════════════════════

    /// <summary>요청 허가 — 토큰 · 백오프 창 · 일일 쿼터 90 % 안전선 순서로 판정, 허가 시 비용 선차감.</summary>
    public QuotaDecision TryAcquire(LiveProvider p, int cost, double now)
    {
        var i = (int)p;
        Roll(i, now);
        if (!_token[i].IsSet || _expiresAt[i] <= now) return QuotaDecision.NoToken;
        if (now < _blockedUntil[i]) return QuotaDecision.BackingOff;
        if (_used[i] + cost > _limit[i] * SafetyRatio) { SafetyThrottles++; return QuotaDecision.SafetyThrottled; }
        _used[i] += cost;
        return QuotaDecision.Allowed;
    }

    private void Roll(int i, double now)
    {
        var day = (long)Math.Floor(now / 86400);
        if (day == _day[i]) return;
        _day[i] = day; _used[i] = 0;
    }

    /// <summary>429 · 레이트 초과 — 지수 백오프 지연(초) 산출 · 차단 창 설정.</summary>
    public double OnRateLimited(LiveProvider p, double now, int retryAfterSec)
    {
        var i = (int)p;
        RateLimitHits++;
        var k = Math.Min(++_hits[i], 16);
        var d = Math.Min(BackoffMaxSec, BackoffBaseSec * (1 << (k - 1)));
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        d = Math.Min(BackoffMaxSec, d * (1 + 0.25 * (_rng % 1000) / 1000.0));
        if (retryAfterSec > d) d = Math.Min(retryAfterSec, RetryAfterCapSec);
        _blockedUntil[i] = now + d;
        LastBackoffSec = d;
        return d;
    }

    /// <summary>quotaExceeded — 당일 쿼터 소진 처리(UTC 일 경계까지 안전 정지).</summary>
    public void OnQuotaExhausted(LiveProvider p, double now)
    {
        var i = (int)p;
        Roll(i, now);
        QuotaExhaustions++;
        _used[i] = _limit[i];
    }

    public void OnSuccess(LiveProvider p) => _hits[(int)p] = 0;

    /// <summary>응답 본문 레이트 리밋 판정 — YouTube rateLimitExceeded/userRateLimitExceeded · Graph 코드 4/17/32/613.</summary>
    public static bool IsRateLimitBody(ReadOnlySpan<byte> body) =>
        body.IndexOf("RateLimitExceeded"u8) >= 0 || body.IndexOf("rateLimitExceeded"u8) >= 0 ||
        GraphCode(body) is 4 or 17 or 32 or 613;

    /// <summary>응답 본문 일일 쿼터 소진 판정 — YouTube quotaExceeded/dailyLimitExceeded.</summary>
    public static bool IsQuotaBody(ReadOnlySpan<byte> body) => body.IndexOf("quotaExceeded"u8) >= 0 || body.IndexOf("dailyLimitExceeded"u8) >= 0;

    /// <summary>Graph 오류 <c>"code":N</c> 추출(없으면 0).</summary>
    public static int GraphCode(ReadOnlySpan<byte> body)
    {
        var k = body.IndexOf("\"code\":"u8);
        if (k < 0) return 0;
        var s = body[(k + 7)..].TrimStart((byte)' ');
        return Utf8Parser.TryParse(s, out int v, out _) ? v : 0;
    }

    public void Dispose() { _token[0].Dispose(); _token[1].Dispose(); }
}
