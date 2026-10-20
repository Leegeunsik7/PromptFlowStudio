using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
namespace PromptFlowStudio.Services.GraphBridge;

public enum GraphOutcome : byte { Ok, RateLimited, Retry, AuthError, Fatal }

/// <summary>
/// 메모리 내 비밀 슬롯 — 프로세스 임시 키 AES-GCM 암호문으로만 보관(평문은 사용 순간 호출자 버퍼에만 복호화).
/// .NET 8 에는 ProtectedMemory 가 없으므로 BCL AesGcmCompat 로 대체, 미지원 플랫폼은 무작위 패드 XOR.
/// </summary>
public sealed class GraphSecretSlot : IDisposable
{
    public const int Cap = 1024;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32), _nonce = new byte[12], _tag = new byte[16], _cipher = new byte[Cap], _pad = new byte[Cap];
    private readonly AesGcmCompat? _aes;
    public GraphSecretSlot()
    {
        if (AesGcmCompat.IsSupported) _aes = new AesGcmCompat(_key, 16);
    }
    public int Length { get; private set; }
    public bool IsSet => Length > 0;

    public void Set(ReadOnlySpan<byte> plain)
    {
        if (plain.Length > Cap) throw new ArgumentException("secret too long");
        Length = plain.Length;
        RandomNumberGenerator.Fill(_nonce);
        if (_aes is not null) _aes.Encrypt(_nonce, plain, _cipher.AsSpan(0, Length), _tag);
        else { RandomNumberGenerator.Fill(_pad.AsSpan(0, Length)); for (var i = 0; i < Length; i++) _cipher[i] = (byte)(plain[i] ^ _pad[i]); }
    }

    public void Set(ReadOnlySpan<char> plain)
    {
        Span<byte> tmp = stackalloc byte[Cap];
        try { var n = System.Text.Encoding.UTF8.GetBytes(plain, tmp); Set(tmp[..n]); }
        finally { CryptographicOperations.ZeroMemory(tmp); }
    }

    /// <summary>평문을 dst 에 복호화(무할당) — 길이 반환. 호출자가 사용 후 제로화 책임.</summary>
    public int CopyTo(Span<byte> dst)
    {
        if (Length == 0) return 0;
        if (_aes is not null) _aes.Decrypt(_nonce, _cipher.AsSpan(0, Length), _tag, dst[..Length]);
        else for (var i = 0; i < Length; i++) dst[i] = (byte)(_cipher[i] ^ _pad[i]);
        return Length;
    }

    /// <summary>보관 암호문에 평문이 그대로 남지 않았는지(진단 · 테스트).</summary>
    public bool CipherContains(ReadOnlySpan<byte> plain) => plain.Length > 0 && _cipher.AsSpan(0, Length).IndexOf(plain) >= 0;

    public void Clear() { CryptographicOperations.ZeroMemory(_cipher); CryptographicOperations.ZeroMemory(_pad); Length = 0; }
    public void Dispose() { Clear(); _aes?.Dispose(); CryptographicOperations.ZeroMemory(_key); }
}

/// <summary>
/// [PromptFlow Studio v17.48.0] 🔗 토큰 버킷 API 레이트 리밋 &amp; OAuth2 보안 볼트 가드(<see cref="TryAcquire"/> · <see cref="Observe"/> · 헤더 조립 무할당).
/// <b>버킷</b>: 용량 200 · 3600 초에 200 개 리필 — 부족 시 요청은 지연(Deferred) 큐잉. X-App-Usage(call_count · total_cputime · total_time 최대 %)가
/// 95% 이상이면 60 초 선제 정지. <b>429 / 코드 4·17·32·613</b>: 버킷을 1 개로 감축하고 지수 백오프(1 s × 2ⁿ⁻¹ · 상한 300 s · 결정적 지터 0.75~1)와
/// Retry-After 중 큰 값까지 차단. 5xx/네트워크 실패도 같은 백오프로 재시도. <b>토큰</b>: 장기 토큰은 <see cref="GraphSecretSlot"/> 암호문으로만 보관,
/// 헤더 조립 시 송신 버퍼에 직접 복호화하고 전송 완료 즉시 제로화. 만료까지 60 일 미만이면서 발급 24 시간 경과 시 갱신(사실상 일 1 회).
/// 코드 190/HTTP 401 은 토큰 무효로 표시. 웹훅 X-Hub-Signature-256 은 앱 시크릿 HMAC-SHA256 · FixedTimeEquals 로 검증.
/// 콘텐츠 발행 한도 24 시간 50 건(슬라이딩).
/// </summary>
public sealed class TokenBucketRateLimitOAuthSecurityGuard : IDisposable
{
    public const double DefaultCapacity = 200, HourSec = 3600, DaySec = 86400, BackoffBaseSec = 1, BackoffMaxSec = 300, ThrottleUsagePct = 95, ThrottleHoldSec = 60;
    public const int PublishLimit = 50;
    private readonly double[] _publishAt = new double[PublishLimit];
    private int _pubHead, _pubCount;
    private double _tokens = DefaultCapacity, _last;
    private uint _rng = 0x9E3779B9u;

    public GraphSecretSlot Token { get; } = new();
    public GraphSecretSlot AppSecret { get; } = new();
    public double Capacity { get; set; } = DefaultCapacity;
    public double RefillPerSec => Capacity / HourSec;
    public double Tokens => _tokens;
    public double QuotaPct => 100.0 * _tokens / Capacity;
    public double BlockedUntil { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public double LastBackoffSec { get; private set; }
    public int UsagePct { get; private set; }
    public long Acquired { get; private set; }
    public long Deferred { get; private set; }
    public long RateLimitHits { get; private set; }
    public long Retries { get; private set; }
    public long AuthErrors { get; private set; }
    public bool TokenInvalid { get; private set; }
    public double TokenIssuedAt { get; private set; }
    public double TokenExpiresAt { get; private set; }
    public double RefreshThresholdSec { get; set; } = 60 * DaySec;
    public long Refreshes { get; private set; }
    public long SignatureOk { get; private set; }
    public long SignatureBad { get; private set; }
    /// <summary>UI 표시용 마스킹(설정 시 1회 계산).</summary>
    public string TokenMasked { get; private set; } = "—";
    public int PublishedLast24h(double now) { var n = 0; for (var i = 0; i < _pubCount; i++) if (now - _publishAt[i] < DaySec) n++; return n; }
    public double TokenDaysLeft(double now) => Token.IsSet ? (TokenExpiresAt - now) / DaySec : 0;

    // ══════════════════════ 버킷 ══════════════════════
    private void Refill(double now)
    {
        if (now > _last) { _tokens = Math.Min(Capacity, _tokens + (now - _last) * RefillPerSec); _last = now; }
    }

    public bool IsBlocked(double now) => now < BlockedUntil;

    /// <summary>호출 1 회 토큰 획득(무할당) — 차단 중이거나 토큰 부족이면 false(지연).</summary>
    public bool TryAcquire(double now)
    {
        Refill(now);
        if (now < BlockedUntil || _tokens < 1) { Deferred++; return false; }
        _tokens -= 1;
        Acquired++;
        return true;
    }

    /// <summary>다음 호출 가능까지 남은 초.</summary>
    public double WaitSeconds(double now)
    {
        Refill(now);
        var w = Math.Max(BlockedUntil - now, 0);
        return _tokens >= 1 ? w : Math.Max(w, (1 - _tokens) / RefillPerSec);
    }

    public double Backoff(int n)
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        var jitter = 0.75 + 0.25 * (_rng / (double)uint.MaxValue);
        return Math.Min(BackoffBaseSec * Math.Pow(2, Math.Max(n - 1, 0)), BackoffMaxSec) * jitter;
    }

    /// <summary>응답 판정(무할당) — 사용량 헤더 · 429/레이트 코드 · 인증 · 재시도 분류 및 차단 시각 갱신.</summary>
    public GraphOutcome Observe(int status, double now, GraphResponseBuffer resp)
    {
        if (resp.AppUsageLength > 0)
        {
            UsagePct = ParseUsagePct(resp.AppUsageSpan);
            if (UsagePct >= ThrottleUsagePct) BlockedUntil = Math.Max(BlockedUntil, now + ThrottleHoldSec);
        }
        if (status is >= 200 and < 300) { ConsecutiveFailures = 0; return GraphOutcome.Ok; }
        TryReadError(resp.BodySpan, out var code, out _);
        if (status == 429 || code is 4 or 17 or 32 or 613)
        {
            RateLimitHits++;
            var d = Math.Max(resp.RetryAfterSec, Backoff(++ConsecutiveFailures));
            LastBackoffSec = d;
            BlockedUntil = Math.Max(BlockedUntil, now + d);
            Refill(now);
            _tokens = Math.Min(_tokens, 1);
            return GraphOutcome.RateLimited;
        }
        if (status == 401 || code == 190) { AuthErrors++; TokenInvalid = true; return GraphOutcome.AuthError; }
        if (status == 0 || status >= 500)
        {
            Retries++;
            var d = Backoff(++ConsecutiveFailures);
            LastBackoffSec = d;
            BlockedUntil = Math.Max(BlockedUntil, now + d);
            return GraphOutcome.Retry;
        }
        return GraphOutcome.Fatal;
    }

    /// <summary>X-App-Usage / X-Business-Use-Case-Usage 값 중 최대 % (무할당).</summary>
    public static int ParseUsagePct(ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json);
        var max = 0;
        try
        {
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && (r.ValueTextEquals("call_count"u8) || r.ValueTextEquals("total_cputime"u8) || r.ValueTextEquals("total_time"u8)) && r.Read() && r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var v))
                    max = Math.Max(max, v);
        }
        catch (JsonException) { }
        return max;
    }

    /// <summary>Graph 오류 객체 {"error":{"code":n,"error_subcode":m}} 판독(무할당).</summary>
    public static bool TryReadError(ReadOnlySpan<byte> json, out int code, out int subcode)
    {
        code = 0; subcode = 0;
        if (json.IsEmpty) return false;
        var r = new Utf8JsonReader(json);
        var inErr = false;
        try
        {
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.CurrentDepth == 1) { inErr = r.ValueTextEquals("error"u8); continue; }
                if (!inErr || r.CurrentDepth != 2) continue;
                if (r.ValueTextEquals("code"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt32(out code); }
                else if (r.ValueTextEquals("error_subcode"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt32(out subcode); }
            }
        }
        catch (JsonException) { }
        return code != 0;
    }

    // ══════════════════════ 토큰 · 헤더 ══════════════════════
    /// <summary>장기 토큰 설정(UI · 할당 허용) — 발급 경과/잔여 초 지정.</summary>
    public void SetToken(ReadOnlySpan<char> token, double now, double expiresInSec = 60 * DaySec, double issuedAgoSec = 0)
    {
        Token.Set(token);
        TokenIssuedAt = now - issuedAgoSec; TokenExpiresAt = now + expiresInSec; TokenInvalid = false;
        TokenMasked = token.Length > 10 ? string.Concat(token[..4], "…", token[^4..]) : "****";
    }

    public void SetAppSecret(ReadOnlySpan<char> secret) => AppSecret.Set(secret);

    public void ClearCredentials() { Token.Clear(); AppSecret.Clear(); TokenMasked = "—"; TokenExpiresAt = 0; }

    public bool NeedsRefresh(double now) => Token.IsSet && !TokenInvalid && TokenExpiresAt > now && TokenExpiresAt - now < RefreshThresholdSec && now - TokenIssuedAt >= DaySec;

    /// <summary><c>Authorization: {scheme} {token}\r\n</c> 를 dst 에 기록(무할당) — 길이 반환.</summary>
    public int WriteAuthHeader(Span<byte> dst, bool oauthScheme)
    {
        var n = 0;
        GraphBytes.Put(dst, ref n, oauthScheme ? "Authorization: OAuth "u8 : "Authorization: Bearer "u8);
        n += Token.CopyTo(dst[n..]);
        GraphBytes.Put(dst, ref n, "\r\n"u8);
        return n;
    }

    /// <summary>갱신 응답 {"access_token","expires_in"} 적용(무할당) — 성공 시 true.</summary>
    public bool ApplyRefresh(ReadOnlySpan<byte> json, double now)
    {
        Span<byte> tok = stackalloc byte[GraphSecretSlot.Cap];
        var len = 0; long exp = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 1) continue;
                if (r.ValueTextEquals("access_token"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && !r.ValueIsEscaped && r.ValueSpan.Length <= tok.Length) { r.ValueSpan.CopyTo(tok); len = r.ValueSpan.Length; } }
                else if (r.ValueTextEquals("expires_in"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt64(out exp); }
            }
            if (len == 0 || exp <= 0) return false;
            Token.Set(tok[..len]);
            TokenIssuedAt = now; TokenExpiresAt = now + exp; TokenInvalid = false;
            Refreshes++;
            return true;
        }
        catch (JsonException) { return false; }
        finally { CryptographicOperations.ZeroMemory(tok); }
    }

    /// <summary>X-Hub-Signature-256(<c>sha256=hex</c>) 검증(무할당 · 고정 시간 비교).</summary>
    public bool VerifySignature(ReadOnlySpan<byte> body, ReadOnlySpan<byte> header)
    {
        Span<byte> key = stackalloc byte[GraphSecretSlot.Cap];
        Span<byte> mac = stackalloc byte[32];
        Span<byte> got = stackalloc byte[32];
        try
        {
            if (!AppSecret.IsSet || !header.StartsWith("sha256="u8) || header.Length != 7 + 64) { SignatureBad++; return false; }
            for (var i = 0; i < 32; i++)
            {
                int hi = HexVal(header[7 + 2 * i]), lo = HexVal(header[8 + 2 * i]);
                if (hi < 0 || lo < 0) { SignatureBad++; return false; }
                got[i] = (byte)(hi << 4 | lo);
            }
            var kl = AppSecret.CopyTo(key);
            HMACSHA256.HashData(key[..kl], body, mac);
            var ok = CryptographicOperations.FixedTimeEquals(mac, got);
            if (ok) SignatureOk++; else SignatureBad++;
            return ok;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static int HexVal(byte c) => c is >= (byte)'0' and <= (byte)'9' ? c - '0' : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10 : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;

    /// <summary>서명 헤더 생성(시뮬레이터 · 테스트용) — <c>sha256=hex</c> 71 바이트.</summary>
    public static int Sign(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> body, Span<byte> dst)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(secret, body, mac);
        "sha256="u8.CopyTo(dst);
        for (var i = 0; i < 32; i++) { dst[7 + 2 * i] = Hex(mac[i] >> 4); dst[8 + 2 * i] = Hex(mac[i] & 15); }
        return 71;
    }

    private static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'a' + v - 10);

    // ══════════════════════ 발행 한도 ══════════════════════
    public bool CanPublish(double now) => PublishedLast24h(now) < PublishLimit;
    public void RecordPublish(double now)
    {
        _publishAt[_pubHead] = now;
        _pubHead = (_pubHead + 1) % PublishLimit;
        if (_pubCount < PublishLimit) _pubCount++;
    }

    public void Dispose() { Token.Dispose(); AppSecret.Dispose(); }
}
