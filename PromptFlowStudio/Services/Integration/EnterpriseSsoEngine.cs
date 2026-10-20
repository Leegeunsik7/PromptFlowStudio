using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Integration;

/// <summary>SSO 검증 주체(불변) — 식별자·테넌트·역할·발급자·만료 + 검증 결과.</summary>
public sealed record SsoPrincipal(
    string Subject, string TenantId, TenantRole Role, string Issuer, long ExpiresMs, bool Valid, string Reason);

/// <summary>
/// [PromptFlow Studio v8.5.3] 엔터프라이즈 SSO/SAML 인증 브리지(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ B2B Okta/ActiveDirectory OIDC 토큰(JWS · HS256/RS256)을 순수 BCL 로 서명·만료·발급자·오디언스 검증(외부 IdentityModel 0).
///   ▸ SAML 2.0 어설션은 다이제스트(HMAC-SHA256) 검증으로 근사(XML-DSig 결선점 · 외부 System.Security.Cryptography.Xml 미의존).
///   ▸ 검증 성공 시 <see cref="TenantPolicyEngine"/> 에 테넌트 역할/주권 리전을 <b>바인딩</b>(RBAC 정책 자동 등록) → 게이트웨이 인가에 즉시 반영.
///   ⚠ <see cref="OnChange"/> 는 검증 호출(임의) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링. 비밀은 메모리 상주(로그 미기록).
/// </summary>
public sealed class EnterpriseSsoEngine
{
    private readonly TenantPolicyEngine? _policy;
    private readonly ConcurrentDictionary<string, SsoPrincipal> _sessions = new();

    private string? _hmacKey;
    private RSA? _rsaPublic;
    private string _expectedIssuer = "";
    private string _expectedAudience = "";

    private long _validated, _rejected;

    public EnterpriseSsoEngine(TenantPolicyEngine? policy = null) => _policy = policy;

    public event Action? OnChange;
    /// <summary>인증 성공/거부 알림(HUD · 감사).</summary>
    public event Action<SsoPrincipal>? OnAuthenticated;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long ValidatedOk => _validated;
    public long Rejected => _rejected;
    public int ActiveSessions => _sessions.Count;
    public string? LastSubject { get; private set; }
    public string? LastError { get; private set; }
    public bool RsaConfigured => _rsaPublic is not null;
    public bool HmacConfigured => !string.IsNullOrEmpty(_hmacKey);

    // ── 구성 ───────────────────────────────────────────────────────────────
    public void ConfigureHs256(string? key) { _hmacKey = string.IsNullOrWhiteSpace(key) ? null : key; Notify(); }
    public void ConfigureExpected(string? issuer, string? audience)
    { _expectedIssuer = (issuer ?? "").Trim(); _expectedAudience = (audience ?? "").Trim(); Notify(); }

    /// <summary>RS256 공개키 등록(PEM SubjectPublicKeyInfo). 실패 시 무시(Graceful).</summary>
    public bool ConfigureRs256Pem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return false;
        try { var rsa = RSA.Create(); rsa.ImportFromPem(pem); _rsaPublic?.Dispose(); _rsaPublic = rsa; Notify(); return true; }
        catch (Exception ex) { Program.Log("SSO.ConfigureRsa", ex); LastError = "RSA 공개키 파싱 실패"; return false; }
    }

    // ── OIDC(JWT/JWS) 검증 ────────────────────────────────────────────────

    /// <summary>
    /// OIDC ID 토큰(JWS) 검증 — 서명(HS256/RS256)·exp·nbf·iss·aud 대조 후 클레임을 <see cref="SsoPrincipal"/> 로 매핑하고 정책에 바인딩.
    /// </summary>
    public SsoPrincipal ValidateOidc(string? jwt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jwt)) return Reject("빈 토큰");
            var parts = jwt.Split('.');
            if (parts.Length != 3) return Reject("JWS 형식 아님");

            var header = ParseJson(parts[0]);
            var payload = ParseJson(parts[1]);
            if (header is null || payload is null) return Reject("헤더/페이로드 파싱 실패");

            var alg = GetString(header, "alg");
            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var signature = Base64UrlDecode(parts[2]);
            if (!VerifySignature(alg, signingInput, signature)) return Reject($"서명 검증 실패({alg})");

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var exp = GetLong(payload, "exp");
            var nbf = GetLong(payload, "nbf");
            if (exp > 0 && now >= exp) return Reject("토큰 만료");
            if (nbf > 0 && now < nbf) return Reject("아직 유효하지 않은 토큰");

            var iss = GetString(payload, "iss");
            if (!string.IsNullOrEmpty(_expectedIssuer) && !string.Equals(iss, _expectedIssuer, StringComparison.Ordinal))
                return Reject("발급자(iss) 불일치");
            var aud = GetString(payload, "aud");
            if (!string.IsNullOrEmpty(_expectedAudience) && !string.Equals(aud, _expectedAudience, StringComparison.Ordinal))
                return Reject("오디언스(aud) 불일치");

            var subject = GetString(payload, "sub");
            var tenant = FirstNonEmpty(GetString(payload, "tenant"), GetString(payload, "tid"), subject);
            var role = ParseRole(GetString(payload, "role"));
            var region = GetString(payload, "region");
            var expMs = exp > 0 ? exp * 1000 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000;

            var principal = new SsoPrincipal(subject, tenant, role, iss, expMs, true, "OIDC 검증 통과");
            return Accept(principal, region);
        }
        catch (Exception ex) { Program.Log("SSO.ValidateOidc", ex); return Reject("검증 중 오류"); }
    }

    /// <summary>
    /// SAML 2.0 어설션 검증(근사) — 어설션 본문 + 공유 시크릿 HMAC-SHA256 다이제스트 대조(상수시간). 통과 시 정책 바인딩.
    /// </summary>
    public SsoPrincipal ValidateSaml(string? assertion, string subject, string tenantId, TenantRole role,
        string issuer, long expiresMs, string? region, string? providedDigestHex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assertion) || string.IsNullOrEmpty(_hmacKey)) return Reject("SAML 어설션/시크릿 없음");
            var expected = HmacHex(_hmacKey!, assertion);
            if (!FixedEquals(expected, providedDigestHex ?? "")) return Reject("SAML 다이제스트 불일치");
            if (expiresMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresMs) return Reject("SAML 어설션 만료");

            var principal = new SsoPrincipal(subject, tenantId, role, issuer, expiresMs, true, "SAML 다이제스트 검증 통과");
            return Accept(principal, region);
        }
        catch (Exception ex) { Program.Log("SSO.ValidateSaml", ex); return Reject("SAML 검증 중 오류"); }
    }

    /// <summary>활성 세션 조회(만료분은 미반영 · 표시용).</summary>
    public SsoPrincipal? Session(string subject) => _sessions.TryGetValue(subject, out var p) ? p : null;

    // ── 판정 & 바인딩 ─────────────────────────────────────────────────────
    private SsoPrincipal Accept(SsoPrincipal p, string? region)
    {
        _validated++; LastSubject = p.Subject; LastError = null;
        _sessions[p.Subject] = p;
        try { _policy?.RegisterPolicy(p.TenantId, p.Role, region, maxConcurrentRenders: 4, maxDailyRenderUnits: 10_000); }
        catch (Exception ex) { Program.Log("SSO.BindPolicy", ex); }   // 정책 바인딩 실패는 인증 자체를 무효화하지 않음
        try { OnAuthenticated?.Invoke(p); } catch { /* 구독자 예외 격리 */ }
        Notify();
        return p;
    }

    private SsoPrincipal Reject(string reason)
    {
        _rejected++; LastError = reason;
        var p = new SsoPrincipal("", "", TenantRole.Viewer, "", 0, false, reason);
        try { OnAuthenticated?.Invoke(p); } catch { /* 격리 */ }
        Notify();
        return p;
    }

    // ── 서명 검증(BCL) ──
    private bool VerifySignature(string alg, byte[] signingInput, byte[] signature)
    {
        switch (alg.ToUpperInvariant())
        {
            case "HS256":
                if (string.IsNullOrEmpty(_hmacKey)) return false;
                using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(_hmacKey!)))
                    return CryptographicOperations.FixedTimeEquals(h.ComputeHash(signingInput), signature);
            case "RS256":
                return _rsaPublic is not null && _rsaPublic.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            default:
                return false;
        }
    }

    // ── JSON/Base64Url 헬퍼 ──
    private static Dictionary<string, JsonElement>? ParseJson(string b64url)
    {
        try { return JsonSerializer.Deserialize(Base64UrlDecode(b64url), PromptFlowJsonContext.Default.StringJsonElementDictionary); }
        catch { return null; }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }

    private static string GetString(Dictionary<string, JsonElement> o, string k)
        => o.TryGetValue(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static long GetLong(Dictionary<string, JsonElement> o, string k)
        => o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string FirstNonEmpty(params string[] xs)
    {
        foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x;
        return "";
    }

    private static TenantRole ParseRole(string? role) => (role ?? "").Trim().ToLowerInvariant() switch
    {
        "admin" or "administrator" => TenantRole.Admin,
        "editor" or "contributor" => TenantRole.Editor,
        _ => TenantRole.Viewer,
    };

    private static string HmacHex(string secret, string message)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b ?? ""));
}
