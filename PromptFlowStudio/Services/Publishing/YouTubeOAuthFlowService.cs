using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Configuration;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>authorization_code 교환 결과(리프레시 토큰 필수).</summary>
public sealed record OAuthTokenResult(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// [PromptFlowStudio] 🔗 YouTube 원클릭 브라우저 OAuth — 순수 BCL(HttpListener 루프백 + HttpClient · 외부 NuGet 0).
///   ▸ ① 루프백 리스너(<c>http://127.0.0.1:5000/oauth2callback</c> · 점유 시 가용 포트) → ② 기본 브라우저로 Google 동의 화면(PKCE S256 · state)
///     → ③ 콜백 code 수신 · state 고정시간 비교 → ④ 토큰 교환(access_type=offline · prompt=consent 로 RefreshToken 보장)
///     → ⑤ 채널 표시명 조회(실패해도 연동 유지) → ⑥ <see cref="AppSettingsManager.SaveSns"/>(머신바운드 암호화) 영속.
///   ▸ 클라이언트: 환경변수 <c>PFS_YOUTUBE_CLIENT_ID/SECRET</c> → 번들 appsettings <c>YouTube:ClientId/ClientSecret</c> → 저장소(시크릿 하드코딩 0).
///   ▸ 로그인 대기 5분 · 제어 요청 30초 타임아웃 · 429/5xx 지수 백오프 3회 · 동시 로그인 1건.
/// </summary>
public sealed class YouTubeOAuthFlowService : IDisposable
{
    public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string ChannelEndpoint = "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true";
    /// <summary>업로드 + 자막(captions.insert · channels.list mine) 권한.</summary>
    public const string Scope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.force-ssl";
    public const int PreferredPort = 5000;
    public const string CallbackPath = "/oauth2callback";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string SuccessHtml =
        "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>PromptFlow Studio</title></head>" +
        "<body style=\"font-family:sans-serif;text-align:center;padding-top:80px\"><h2>✅ 연동이 완료되었습니다.</h2>" +
        "<p>창을 닫고 PromptFlowStudio로 돌아가세요.</p></body></html>";

    private readonly AppSettingsManager _settings;
    private readonly Func<(string ClientId, string ClientSecret)> _client;
    private readonly Func<string, bool> _openBrowser;
    private readonly HttpClient _http;
    private int _busy;

    /// <summary>DI — 환경변수 → 번들 appsettings → 저장소 순으로 OAuth 클라이언트 해석.</summary>
    public static YouTubeOAuthFlowService Create(AppSettingsManager settings, IConfiguration? cfg)
        => new(settings, () => ResolveClient(settings, cfg));

    /// <param name="handler">테스트 주입(가짜 Google 엔드포인트).</param>
    /// <param name="openBrowser">테스트 주입(브라우저 대신 콜백 시뮬레이션).</param>
    public YouTubeOAuthFlowService(AppSettingsManager settings, Func<(string ClientId, string ClientSecret)> client,
        HttpMessageHandler? handler = null, Func<string, bool>? openBrowser = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _openBrowser = openBrowser ?? OpenSystemBrowser;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 요청별 CTS 가 제한
    }

    /// <summary>백오프 기준(초) — 2^n 배. 테스트에서 0.</summary>
    public double BackoffBaseSeconds { get; set; } = 1.0;

    /// <summary>OAuth 클라이언트(ID/Secret) 구성 여부 — false 면 로그인 버튼 비활성.</summary>
    public bool HasClient
    {
        get { var (id, secret) = SafeClient(); return id.Length > 0 && secret.Length > 0; }
    }

    public bool IsRunning => Volatile.Read(ref _busy) != 0;

    /// <summary>마지막 로그인/해제 실패 사유(성공 시 null).</summary>
    public string? LastError { get; private set; }

    // ── 순수 함수(테스트 잠금) ──

    /// <summary>Google 동의 화면 URL — offline + consent(리프레시 토큰 보장) · PKCE S256 · state.</summary>
    public static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge)
        => AuthEndpoint
         + "?client_id=" + Uri.EscapeDataString(clientId)
         + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
         + "&response_type=code"
         + "&scope=" + Uri.EscapeDataString(Scope)
         + "&access_type=offline&prompt=consent&include_granted_scopes=true"
         + "&state=" + Uri.EscapeDataString(state)
         + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
         + "&code_challenge_method=S256";

    /// <summary>PKCE code_verifier — 32바이트 난수 base64url(43자).</summary>
    public static string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>PKCE code_challenge = base64url(SHA256(verifier)).</summary>
    public static string CreateCodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>콜백 쿼리(<c>?code=..&amp;state=..&amp;error=..</c>) 파싱 — 퍼센트/+ 디코딩.</summary>
    public static (string? Code, string? State, string? Error) ParseCallbackQuery(string? query)
    {
        string? code = null, state = null, error = null;
        var q = (query ?? "").TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var val = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (key == "code") code = val;
            else if (key == "state") state = val;
            else if (key == "error") error = val;
        }
        return (string.IsNullOrEmpty(code) ? null : code, state, string.IsNullOrEmpty(error) ? null : error);
    }

    /// <summary>토큰 응답 스키마 검증 — access_token · refresh_token 필수, expires_in 기본 3600. 불충족 null.</summary>
    public static OAuthTokenResult? ParseTokenResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return null;
            var ttl = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : 3600;
            return new OAuthTokenResult(access, refresh, ttl);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>channels.list 응답 → <c>@handle</c>(customUrl) 우선, 없으면 채널 제목. 실패 null.</summary>
    public static string? ParseChannelName(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;
            if (!items[0].TryGetProperty("snippet", out var sn) || sn.ValueKind != JsonValueKind.Object) return null;
            if (sn.TryGetProperty("customUrl", out var cu) && cu.ValueKind == JsonValueKind.String && cu.GetString() is { Length: > 0 } handle)
                return handle.StartsWith('@') ? handle : "@" + handle;
            return sn.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } title ? title : null;
        }
        catch (JsonException) { return null; }
    }

    // ── 오케스트레이션 ──

    /// <summary>
    /// 원클릭 로그인 — 브라우저 동의 → 루프백 콜백 → 토큰 교환 → 암호화 저장. 실패/취소/시간초과는 false(<see cref="LastError"/>).
    /// </summary>
    public async Task<bool> StartInteractiveLoginAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) { LastError = "이미 로그인이 진행 중입니다."; return false; }
        try
        {
            var (clientId, clientSecret) = SafeClient();
            if (clientId.Length == 0 || clientSecret.Length == 0)
            {
                LastError = "OAuth 클라이언트 미구성 — appsettings.json YouTube:ClientId/ClientSecret 또는 환경변수 PFS_YOUTUBE_CLIENT_ID/SECRET 을 설정하세요.";
                return false;
            }

            var listener = StartListener(out var redirectUri);
            if (listener is null) { LastError = "로컬 콜백 포트를 열지 못했습니다(방화벽/포트 점유 확인)."; return false; }
            using (listener)
            {
                var verifier = CreateCodeVerifier();
                var state = CreateCodeVerifier();
                if (!_openBrowser(BuildAuthorizationUrl(clientId, redirectUri, state, CreateCodeChallenge(verifier))))
                {
                    LastError = "기본 브라우저를 열지 못했습니다.";
                    return false;
                }

                string? code;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(LoginTimeout);
                    try { code = await WaitForCodeAsync(listener, state, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        LastError = "로그인 대기 시간(5분)이 초과되었습니다.";
                        return false;
                    }
                }
                if (code is null) return false;

                var tokens = await ExchangeCodeAsync(clientId, clientSecret, code, redirectUri, verifier, ct).ConfigureAwait(false);
                if (tokens is null) { LastError ??= "토큰 교환에 실패했습니다(리프레시 토큰 미수신)."; return false; }

                var channel = await FetchChannelNameAsync(tokens.AccessToken, ct).ConfigureAwait(false) ?? "";
                var next = _settings.Sns with
                {
                    YouTubeClientId = clientId,
                    YouTubeClientSecret = clientSecret,
                    YouTubeRefreshToken = tokens.RefreshToken,
                    YouTubeChannelName = channel,
                };
                if (!_settings.SaveSns(next)) { LastError = "토큰 저장에 실패했습니다(로그 확인)."; return false; }
                LastError = null;
                return true;
            }
        }
        catch (OperationCanceledException) { LastError = "로그인이 취소되었습니다."; return false; }
        catch (Exception ex) { Program.Log("YouTubeOAuth.Login", ex); LastError = "로그인 실패: " + ex.Message; return false; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>authorization_code → 토큰 교환(PKCE verifier 동봉). 실패/스키마 불충족 null.</summary>
    public async Task<OAuthTokenResult?> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri,
        string codeVerifier, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>(6)
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        };
        var (st, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, YouTubeShortsPublisher.TokenEndpoint) { Content = new FormUrlEncodedContent(form) }, ct).ConfigureAwait(false);
        if (st != 200) { LastError = "토큰 교환 실패(HTTP " + st.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")."; return null; }
        return ParseTokenResponse(body);
    }

    /// <summary>연결된 채널 표시명 — 실패 시 null(연동 자체는 유지).</summary>
    public async Task<string?> FetchChannelNameAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            var (st, body) = await SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, ChannelEndpoint);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return r;
            }, ct).ConfigureAwait(false);
            return st == 200 ? ParseChannelName(body) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("YouTubeOAuth.Channel", ex); return null; }
    }

    /// <summary>연동 해제 — Google 토큰 폐기(best-effort) 후 로컬 RefreshToken · 채널명 삭제(클라이언트 ID/Secret 유지).</summary>
    public async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        var cur = _settings.Sns;
        if (cur.YouTubeRefreshToken.Length > 0)
        {
            try
            {
                var form = new Dictionary<string, string>(1) { ["token"] = cur.YouTubeRefreshToken };
                await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new FormUrlEncodedContent(form) }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("YouTubeOAuth.Revoke", ex); }   // 폐기 실패해도 로컬 삭제 진행
        }
        var ok = _settings.SaveSns(cur with { YouTubeRefreshToken = "", YouTubeChannelName = "" });
        LastError = ok ? null : "연동 해제 저장에 실패했습니다(로그 확인).";
        return ok;
    }

    // ── 내부 ──

    /// <summary>콜백 대기 — 경로 불일치(파비콘 등)는 404 후 계속, state 불일치/거부는 실패 HTML 후 null.</summary>
    private async Task<string?> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var reg = ct.Register(static l =>
        {
            try { ((HttpListener)l!).Stop(); } catch (ObjectDisposedException) { }
        }, listener);
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                LastError = "콜백 수신 실패: " + ex.Message;
                return null;
            }

            if (!string.Equals(ctx.Request.Url?.AbsolutePath, CallbackPath, StringComparison.Ordinal))
            {
                await RespondAsync(ctx, 404, "").ConfigureAwait(false);
                continue;
            }
            var (code, st, err) = ParseCallbackQuery(ctx.Request.Url?.Query);
            var ok = err is null && code is not null && StateEquals(st, state);
            await RespondAsync(ctx, ok ? 200 : 400, ok ? SuccessHtml : FailureHtml(err ?? "state 불일치")).ConfigureAwait(false);
            if (ok) return code;
            LastError = err is null ? "인증 응답 검증 실패(state 불일치)." : "Google 인증이 거부되었습니다(" + err + ").";
            return null;
        }
    }

    private static string FailureHtml(string reason)
        => "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>PromptFlow Studio</title></head>" +
           "<body style=\"font-family:sans-serif;text-align:center;padding-top:80px\"><h2>⚠ 연동에 실패했습니다.</h2><p>" +
           WebUtility.HtmlEncode(reason) + "</p><p>PromptFlowStudio 로 돌아가 다시 시도하세요.</p></body></html>";

    private static async Task RespondAsync(HttpListenerContext ctx, int status, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }   // 브라우저가 먼저 닫힘
        finally { try { ctx.Response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { } }
    }

    private static bool StateEquals(string? got, string expected)
        => got is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(expected));

    /// <summary>루프백 리스너 — 5000 우선, 점유 시 OS 가용 포트. 실패 null.</summary>
    private static HttpListener? StartListener(out string redirectUri)
    {
        for (var i = 0; i < 2; i++)
        {
            var port = i == 0 ? PreferredPort : FreePort();
            if (port <= 0) continue;
            var l = new HttpListener();
            try
            {
                l.Prefixes.Add("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
                l.Start();
                redirectUri = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + CallbackPath;
                return l;
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or PlatformNotSupportedException)
            {
                l.Close();
            }
        }
        redirectUri = "";
        return null;
    }

    private static int FreePort()
    {
        try
        {
            var t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            var p = ((IPEndPoint)t.LocalEndpoint).Port;
            t.Stop();
            return p;
        }
        catch (SocketException) { return 0; }
    }

    private static bool OpenSystemBrowser(string url)
    {
        try { using var p = ProcessCompat.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return ProcessCompat.IsSupported; }
        catch (Exception ex) { Program.Log("YouTubeOAuth.OpenBrowser", ex); return false; }
    }

    /// <summary>제어 요청 — 30초 타임아웃 · 429/5xx 지수 백오프(Retry-After 우선) · 예외는 상태코드로 환원.</summary>
    private async Task<(int Status, string Body)> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            int status;
            string body;
            TimeSpan? retryAfter = null;
            try
            {
                using var req = factory();
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                status = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                retryAfter = resp.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = 504; body = ""; }
            catch (HttpRequestException) { status = 503; body = ""; }

            if (status is not (429 or 500 or 502 or 503 or 504) || attempt >= MaxAttempts) return (status, body);
            var delay = retryAfter ?? TimeSpan.FromSeconds(BackoffBaseSeconds * (1 << (attempt - 1)));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private (string ClientId, string ClientSecret) SafeClient()
    {
        try
        {
            var (id, secret) = _client();
            return ((id ?? "").Trim(), (secret ?? "").Trim());
        }
        catch (Exception ex) { Program.Log("YouTubeOAuth.Client", ex); return ("", ""); }
    }

    private static (string ClientId, string ClientSecret) ResolveClient(AppSettingsManager settings, IConfiguration? cfg)
    {
        var envId = Environment.GetEnvironmentVariable("PFS_YOUTUBE_CLIENT_ID");
        var envSecret = Environment.GetEnvironmentVariable("PFS_YOUTUBE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret)) return (envId, envSecret);
        var id = cfg?["YouTube:ClientId"];
        var secret = cfg?["YouTube:ClientSecret"];
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret)) return (id, secret);
        return (settings.Sns.YouTubeClientId, settings.Sns.YouTubeClientSecret);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _http.Dispose();
}
