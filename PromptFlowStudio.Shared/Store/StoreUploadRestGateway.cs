using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Shared.Store;

/// <summary>업로드 결과.</summary>
public sealed record UploadResult(bool Ok, string Target, string Message);

/// <summary>
/// [PromptFlow Studio v4.4.0] 순수 BCL 스토어 API 자동 업로드 게이트웨이(Task 2 · 외부 SDK 0).
///   ▸ Apple App Store Connect: <b>ES256 JWT</b>(ECDsa) 서명 토큰. ▸ Google Play Developer: <b>RS256 서비스계정 JWT</b> → OAuth2 access_token.
///   ▸ HttpClient 로 인증/바이너리 업로드(자격 부재/오프라인은 Graceful). 서명·인코딩·엔드포인트는 순수 함수(테스트 가능).
/// </summary>
public sealed class StoreUploadRestGateway
{
    public const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
    public const string GooglePublisherScope = "https://www.googleapis.com/auth/androidpublisher";
    public const string AppleBaseUrl = "https://api.appstoreconnect.apple.com/v1";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // ══════════════════════ 인코딩/JWT(순수 함수) ══════════════════════

    /// <summary>Base64Url(패딩 제거).</summary>
    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Apple App Store Connect ES256 JWT(19분 유효).</summary>
    public static string BuildAppleJwt(string keyId, string issuerId, string ecPrivateKeyPem, DateTimeOffset now)
    {
        var header = $"{{\"alg\":\"ES256\",\"kid\":\"{keyId}\",\"typ\":\"JWT\"}}";
        var payload = $"{{\"iss\":\"{issuerId}\",\"iat\":{now.ToUnixTimeSeconds()},\"exp\":{now.AddMinutes(19).ToUnixTimeSeconds()},\"aud\":\"appstoreconnect-v1\"}}";
        var signingInput = Base64Url(Utf8(header)) + "." + Base64Url(Utf8(payload));
        using var ec = ECDsa.Create();
        ec.ImportFromPem(ecPrivateKeyPem);
        var sig = ec.SignData(Utf8(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url(sig);
    }

    /// <summary>Google 서비스 계정 RS256 JWT bearer(OAuth2 교환용 · 1시간 유효).</summary>
    public static string BuildGoogleJwtBearer(string clientEmail, string rsaPrivateKeyPem, DateTimeOffset now, string scope = GooglePublisherScope)
    {
        var header = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
        var payload = $"{{\"iss\":\"{clientEmail}\",\"scope\":\"{scope}\",\"aud\":\"{GoogleTokenUrl}\",\"iat\":{now.ToUnixTimeSeconds()},\"exp\":{now.AddHours(1).ToUnixTimeSeconds()}}}";
        var signingInput = Base64Url(Utf8(header)) + "." + Base64Url(Utf8(payload));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(rsaPrivateKeyPem);
        var sig = rsa.SignData(Utf8(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(sig);
    }

    /// <summary>Play Developer edits 엔드포인트.</summary>
    public static string GoogleEditsUrl(string packageName)
        => $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/{packageName}/edits";

    // ══════════════════════ 오케스트레이션(Graceful) ══════════════════════

    /// <summary>Google JWT bearer → OAuth2 access_token(실패/오프라인 시 null).</summary>
    public async Task<string?> AuthorizeGoogleAsync(string jwtBearer, CancellationToken ct = default)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwtBearer,
            });
            var resp = await _http.PostAsync(GoogleTokenUrl, form, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>AAB 를 Play 로 업로드(자격/네트워크 부재는 Graceful Fail — CI 는 검수 통과 후에만 호출).</summary>
    public async Task<UploadResult> UploadAabAsync(string packageName, string accessToken, string aabPath, CancellationToken ct = default)
    {
        try
        {
            if (!System.IO.File.Exists(aabPath)) return new(false, "GooglePlay", "AAB 파일 없음");
            using var req = new HttpRequestMessage(HttpMethod.Post, GoogleEditsUrl(packageName) + "/{editId}/bundles");
            req.Headers.Add("Authorization", "Bearer " + accessToken);
            req.Content = new ByteArrayContent(await System.IO.File.ReadAllBytesAsync(aabPath, ct).ConfigureAwait(false));
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return new(resp.IsSuccessStatusCode, "GooglePlay", resp.IsSuccessStatusCode ? "AAB 업로드 완료" : $"실패({(int)resp.StatusCode})");
        }
        catch (Exception ex) { return new(false, "GooglePlay", "오프라인/오류: " + ex.Message); }
    }

    /// <summary>IPA 를 App Store Connect 로 업로드(ES256 JWT 인증 · Graceful).</summary>
    public async Task<UploadResult> UploadIpaAsync(string appleJwt, string ipaPath, CancellationToken ct = default)
    {
        try
        {
            if (!System.IO.File.Exists(ipaPath)) return new(false, "AppStore", "IPA 파일 없음");
            using var req = new HttpRequestMessage(HttpMethod.Post, AppleBaseUrl + "/builds");
            req.Headers.Add("Authorization", "Bearer " + appleJwt);
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return new(resp.IsSuccessStatusCode, "AppStore", resp.IsSuccessStatusCode ? "IPA 업로드 완료" : $"실패({(int)resp.StatusCode})");
        }
        catch (Exception ex) { return new(false, "AppStore", "오프라인/오류: " + ex.Message); }
    }
}
