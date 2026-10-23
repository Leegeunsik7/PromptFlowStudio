using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PromptFlowStudio.Services.GraphBridge;

namespace PromptFlowStudio.Services.LivePublish;

public enum LiveProvider : byte { YouTube, Instagram }
public enum LiveAuthPhase : byte { Disconnected, Exchanging, Connected, Refreshing, Expired, Failed }
internal enum LiveOAuthOp : byte { None, Exchange, LongLived, Profile, Refresh }

/// <summary>플랫폼 계정 슬롯 — 액세스/리프레시 토큰 · PKCE verifier 는 <see cref="GraphSecretSlot"/> 암호문으로만 보관(평문은 송신 버퍼에만 복호화).</summary>
public sealed class LiveOAuthAccount : IDisposable
{
    public const int StateLen = 22, IdCap = 64;
    internal readonly byte[] State = new byte[StateLen], ProfileId = new byte[IdCap];
    internal int ProfileIdLen, Failures;
    internal double RetryAt;
    internal LiveOAuthOp Op;
    internal bool Queued;

    public LiveOAuthAccount(LiveProvider p) => Provider = p;
    public LiveProvider Provider { get; }
    public GraphSecretSlot Access { get; } = new();
    public GraphSecretSlot Refresh { get; } = new();
    public GraphSecretSlot Verifier { get; } = new();
    public LiveAuthPhase Phase { get; internal set; }
    public bool PendingAuthorization { get; internal set; }
    public double IssuedAt { get; internal set; }
    public double ExpiresAt { get; internal set; }
    public string ProfileName { get; internal set; } = "—";
    public string ChannelId { get; internal set; } = "—";
    public long Refreshes { get; internal set; }
    public long RefreshFailures { get; internal set; }
    public string? LastError { get; internal set; }
    public bool IsConnected => Access.IsSet && Phase is LiveAuthPhase.Connected or LiveAuthPhase.Refreshing;
    public double SecondsLeft(double now) => Access.IsSet ? Math.Max(0, ExpiresAt - now) : 0;
    public ReadOnlySpan<byte> ProfileIdSpan => ProfileId.AsSpan(0, ProfileIdLen);
    public ulong IgUserId => Utf8Parser.TryParse(ProfileIdSpan, out ulong v, out _) ? v : 0;

    internal void Reset(LiveAuthPhase phase)
    {
        Access.Clear(); Refresh.Clear(); Verifier.Clear();
        CryptographicOperations.ZeroMemory(State);
        Phase = phase; PendingAuthorization = false; IssuedAt = ExpiresAt = 0; Op = LiveOAuthOp.None; Queued = false; Failures = 0; RetryAt = 0;
        ProfileIdLen = 0; ProfileName = "—"; ChannelId = "—";
    }

    public void Dispose() { Access.Dispose(); Refresh.Dispose(); Verifier.Dispose(); }
}

/// <summary>
/// [PromptFlow Studio v17.79.0] 🔐 프로덕션 OAuth2 PKCE 토큰 볼트 게이트웨이.
/// <b>인가</b>: RFC 7636 PKCE(S256 · verifier 32 B 난수 → base64url 43) + state 16 B(CSRF · <see cref="CryptographicOperations.FixedTimeEquals"/>) —
/// Google Identity(<c>youtube.upload</c> · <c>youtube.readonly</c> · access_type=offline) / Meta Login(<c>instagram_content_publish</c>).
/// <b>교환</b>: Google <c>authorization_code</c> → access + refresh · Meta 코드 → 단기 토큰 → <c>fb_exchange_token</c> 장기(60 일) → 프로필(채널 ID · IG 비즈니스 계정).
/// <b>갱신</b>: 만료 10 분 전 <see cref="Tick"/> 이 논블로킹 요청 발행 · 이후 틱에서 수확(동기 전송은 같은 틱) — 실패 시 2 s × 2ⁿ(상한 300 s) 백오프,
/// invalid_grant/190 은 재인증 필요로 표시. 응답 파싱은 <see cref="Utf8JsonReader"/> → stackalloc → 암호문 슬롯(무할당).
/// <b>자격증명</b>: <c>PROMPTFLOW_YT_CLIENT_ID/SECRET</c> · <c>PROMPTFLOW_META_APP_ID/SECRET</c> 환경변수 — 미설정 플랫폼은 즉시
/// <see cref="LivePublishContractSimulator"/> 계약 Mock 으로 전환(런타임 난수 Mock 자격 · 실 네트워크 0).
/// <b>영속화</b>: AES-GCM 256(<see cref="AesGcmCompat"/> · 12 B 논스 · 16 B 태그 · AAD 버전 바인딩) 단일 파일, 키는 DPAPI 봉인 <c>SecureSecretVault</c> 제공.
/// </summary>
public sealed class ProductionOAuth2TokenVaultGateway : IDisposable
{
    public const string EnvYtClientId = "PROMPTFLOW_YT_CLIENT_ID", EnvYtClientSecret = "PROMPTFLOW_YT_CLIENT_SECRET", EnvMetaAppId = "PROMPTFLOW_META_APP_ID", EnvMetaAppSecret = "PROMPTFLOW_META_APP_SECRET";
    public const string GoogleAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth", MetaAuthorizeUrl = "https://www.facebook.com/v21.0/dialog/oauth";
    public const string YouTubeScopes = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly https://www.googleapis.com/auth/youtube.force-ssl https://www.googleapis.com/auth/yt-analytics.readonly";   // v17.94 videos.update/delete · 완청률 리포트
    public const string InstagramScopes = "instagram_basic,instagram_content_publish,pages_show_list,business_management,instagram_manage_insights";   // v17.94 릴스 인사이트
    public const string LoopbackPrefix = "http://127.0.0.1:53682/", DefaultRedirect = "http://127.0.0.1:53682/oauth/callback";
    public const double RefreshLeadSec = 600, BackoffBaseSec = 2, BackoffMaxSec = 300, ConsentTimeoutSec = 180;
    public const int FormCap = 4096, TokenCap = GraphSecretSlot.Cap, VaultMagic = 0x31565046, VaultVersion = 1;
    public static ReadOnlySpan<byte> GoogleTokenUrl => "https://oauth2.googleapis.com/token"u8;
    public static ReadOnlySpan<byte> MetaTokenUrl => "https://graph.facebook.com/v21.0/oauth/access_token"u8;
    public static ReadOnlySpan<byte> YouTubeChannelsUrl => "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true"u8;
    public static ReadOnlySpan<byte> MetaAccountsUrl => "https://graph.facebook.com/v21.0/me/accounts?fields=name,instagram_business_account%7Bid,username%7D"u8;
    private static ReadOnlySpan<byte> VaultAad => "PromptFlow.LiveOAuthVault.v1"u8;

    private readonly LiveOAuthAccount[] _acc = { new(LiveProvider.YouTube), new(LiveProvider.Instagram) };
    private readonly GraphSecretSlot[] _secret = { new(), new() };
    private readonly byte[][] _clientId = new byte[2][], _redirect = { Array.Empty<byte>(), Array.Empty<byte>() };
    private readonly string[] _clientIdText = new string[2];
    private readonly bool[] _live = new bool[2];
    private readonly GraphCall[] _call = { new(), new() };
    private readonly byte[][] _form = { new byte[FormCap], new byte[FormCap] };
    private readonly int[] _formLen = new int[2];
    private readonly IGraphTransport? _injected;
    private readonly Func<byte[]>? _vaultKey;
    private readonly CancellationTokenSource _cts = new();
    private HttpGraphTransport? _http;
    private uint _rng = 0x2545F491u;

    public ProductionOAuth2TokenVaultGateway(IGraphTransport? transport = null, Func<string, string?>? env = null, Func<byte[]>? vaultKey = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        _injected = transport; _vaultKey = vaultKey;
        string? mockYtSecret = null, mockMetaSecret = null;
        Configure(0, env(EnvYtClientId), env(EnvYtClientSecret), "contract-mock-youtube.apps.googleusercontent.com", ref mockYtSecret);
        Configure(1, env(EnvMetaAppId), env(EnvMetaAppSecret), "100000000000001", ref mockMetaSecret);
        Contract = new LivePublishContractSimulator(_clientIdText[0], mockYtSecret ?? RandomSecret(), _clientIdText[1], mockMetaSecret ?? RandomSecret());
    }

    private void Configure(int i, string? id, string? secret, string mockId, ref string? mockSecret)
    {
        _live[i] = !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret);
        if (!_live[i]) { id = mockId; secret = mockSecret = RandomSecret(); }
        _clientIdText[i] = id!.Trim();
        _clientId[i] = System.Text.Encoding.UTF8.GetBytes(_clientIdText[i]);
        _secret[i].Set(secret!.Trim().AsSpan());
    }

    private static string RandomSecret() => "mock-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>자격증명 미설정 플랫폼이 사용하는 격리 계약 시뮬레이터.</summary>
    public LivePublishContractSimulator Contract { get; }
    public Func<double> Clock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public string VaultPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "LiveOAuth", "vault.pfv");
    public bool IsLive(LiveProvider p) => _live[(int)p];
    public bool AnyLive => _live[0] || _live[1];
    public string ModeName(LiveProvider p) => _live[(int)p] ? "LIVE" : "계약 Mock";
    public string ClientIdMasked(LiveProvider p) { var s = _clientIdText[(int)p]; return s.Length > 10 ? string.Concat(s.AsSpan(0, 6), "…", s.AsSpan(s.Length - 4)) : "****"; }
    public LiveOAuthAccount Account(LiveProvider p) => _acc[(int)p];
    public bool InFlight(LiveProvider p) => _call[(int)p].InFlight;
    /// <summary>토큰 변경(교환/갱신/해제) 후 영속화 필요.</summary>
    public bool Dirty { get; private set; }
    public long Exchanges { get; private set; }
    public long Refreshes { get; private set; }
    public long BackgroundRefreshAttempts { get; private set; }
    public long StateRejections { get; private set; }
    public long SchemaRejections { get; private set; }
    public long VaultSaves { get; private set; }
    public long VaultLoads { get; private set; }
    public string? LastVaultError { get; private set; }
    public double LastTickUs { get; private set; }

    /// <summary>플랫폼별 전송 — 주입 전송 우선, 자격증명 설정 = 실 HttpClient, 미설정 = 계약 Mock.</summary>
    public IGraphTransport TransportFor(LiveProvider p) => _injected ?? (_live[(int)p] ? (IGraphTransport)(_http ??= new HttpGraphTransport()) : Contract);

    // ══════════════════════ PKCE 인가 ══════════════════════
    /// <summary>base64url(패딩 없음) — dst ≥ 4⌈n/3⌉ · 기록 길이 반환(무할당).</summary>
    public static int Base64Url(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Base64.EncodeToUtf8(src, dst, out _, out var n);
        while (n > 0 && dst[n - 1] == (byte)'=') n--;
        for (var i = 0; i < n; i++) dst[i] = dst[i] == (byte)'+' ? (byte)'-' : dst[i] == (byte)'/' ? (byte)'_' : dst[i];
        return n;
    }

    /// <summary>인가 URL 생성(비핫패스 · 할당 허용) — verifier/state 는 암호문 슬롯에 보관.</summary>
    public string BeginAuthorization(LiveProvider p, string redirectUri = DefaultRedirect)
    {
        var a = _acc[(int)p];
        Span<byte> raw = stackalloc byte[32]; Span<byte> ver = stackalloc byte[44]; Span<byte> hash = stackalloc byte[32]; Span<byte> chal = stackalloc byte[44]; Span<byte> st = stackalloc byte[24];
        try
        {
            RandomNumberGenerator.Fill(raw);
            var vn = Base64Url(raw, ver);
            a.Verifier.Set(ver[..vn]);
            SHA256.HashData(ver[..vn], hash);
            var cn = Base64Url(hash, chal);
            RandomNumberGenerator.Fill(raw[..16]);
            Base64Url(raw[..16], st);
            st[..LiveOAuthAccount.StateLen].CopyTo(a.State);
            a.PendingAuthorization = true; a.LastError = null;
            _redirect[(int)p] = System.Text.Encoding.UTF8.GetBytes(redirectUri);
            var challenge = System.Text.Encoding.ASCII.GetString(chal[..cn]);
            var state = System.Text.Encoding.ASCII.GetString(a.State);
            return p == LiveProvider.YouTube
                ? GoogleAuthorizeUrl + "?client_id=" + Uri.EscapeDataString(_clientIdText[0]) + "&redirect_uri=" + Uri.EscapeDataString(redirectUri) + "&response_type=code&scope=" + Uri.EscapeDataString(YouTubeScopes)
                  + "&code_challenge=" + challenge + "&code_challenge_method=S256&access_type=offline&prompt=consent&include_granted_scopes=true&state=" + state
                : MetaAuthorizeUrl + "?client_id=" + Uri.EscapeDataString(_clientIdText[1]) + "&redirect_uri=" + Uri.EscapeDataString(redirectUri) + "&response_type=code&scope=" + Uri.EscapeDataString(InstagramScopes)
                  + "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state;
        }
        finally { CryptographicOperations.ZeroMemory(raw); CryptographicOperations.ZeroMemory(ver); }
    }

    /// <summary>리디렉트 수신 코드 교환 발행 — state 고정 시간 검증 실패 시 false(CSRF 차단).</summary>
    public bool CompleteAuthorization(LiveProvider p, ReadOnlySpan<char> code, ReadOnlySpan<char> state, double now)
    {
        var i = (int)p; var a = _acc[i];
        if (!a.PendingAuthorization) { a.LastError = "진행 중인 인가 요청 없음"; return false; }
        Span<byte> st = stackalloc byte[LiveOAuthAccount.StateLen];
        if (state.Length != LiveOAuthAccount.StateLen || System.Text.Encoding.ASCII.GetBytes(state, st) != st.Length || !CryptographicOperations.FixedTimeEquals(st, a.State))
        { StateRejections++; a.PendingAuthorization = false; a.Verifier.Clear(); a.Phase = LiveAuthPhase.Failed; a.LastError = "state 불일치(CSRF 차단)"; return false; }
        if (code.IsEmpty || code.Length > 1024 || _call[i].InFlight) { a.LastError = "인가 코드 없음/요청 중"; return false; }
        Span<byte> c = stackalloc byte[1024];
        var cl = System.Text.Encoding.UTF8.GetBytes(code, c);
        a.PendingAuthorization = false;
        a.Op = LiveOAuthOp.Exchange; a.Phase = LiveAuthPhase.Exchanging;
        Issue(i, now, c[..cl]);
        CryptographicOperations.ZeroMemory(c);
        Pump(i, now);
        return true;
    }

    /// <summary>계약 Mock 연결(동의 화면 대체) — 인가 URL 검증 · 코드 발급 · PKCE 교환 · 프로필까지 동기 완료.</summary>
    public bool ConnectViaContract(LiveProvider p, double now)
    {
        var sim = _injected as LivePublishContractSimulator ?? Contract;
        var url = BeginAuthorization(p);
        if (!sim.Authorize(url, out var code, out var state)) { _acc[(int)p].LastError = "계약 동의 거부(인가 URL 스키마)"; _acc[(int)p].PendingAuthorization = false; return false; }
        return CompleteAuthorization(p, code, state, now) && _acc[(int)p].IsConnected;
    }

    /// <summary>실 계정 연결 — 시스템 브라우저 + 루프백 리디렉트(<see cref="LoopbackPrefix"/>) · 동의 180 s 타임아웃.</summary>
    public async Task<bool> ConnectLiveAsync(LiveProvider p, CancellationToken ct = default)
    {
        var a = _acc[(int)p];
#if WINDOWS
        using var listener = new System.Net.HttpListener();
        try
        {
            listener.Prefixes.Add(LoopbackPrefix);
            listener.Start();
            var url = BeginAuthorization(p, DefaultRedirect);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(ConsentTimeoutSec), ct).ConfigureAwait(false);
            var code = ctx.Request.QueryString["code"]; var state = ctx.Request.QueryString["state"]; var err = ctx.Request.QueryString["error"];
            var html = System.Text.Encoding.UTF8.GetBytes(err is null ? "<html><body style='font-family:sans-serif'>✅ PromptFlow Studio 계정 연결 완료 — 이 창을 닫아도 됩니다.</body></html>" : "<html><body>⚠ 동의 거부/오류 — 앱으로 돌아가세요.</body></html>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(html, ct).ConfigureAwait(false);
            ctx.Response.Close();
            if (err is not null || code is null || state is null) { a.PendingAuthorization = false; a.Phase = LiveAuthPhase.Failed; a.LastError = "동의 거부: " + (err ?? "코드 없음"); return false; }
            if (!CompleteAuthorization(p, code, state, Clock())) return false;
            var deadline = Environment.TickCount64 + 30_000;
            while (a.Phase == LiveAuthPhase.Exchanging && Environment.TickCount64 < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                Tick(Clock());
            }
            return a.IsConnected;
        }
        catch (Exception ex) when (ex is System.Net.HttpListenerException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException or IOException)
        {
            PromptFlowStudio.Program.Log("LiveOAuth.Connect", ex);
            a.PendingAuthorization = false; a.Phase = LiveAuthPhase.Failed; a.LastError = ex is TimeoutException ? "동의 대기 시간 초과(180 s)" : "루프백 인가 실패: " + ex.GetType().Name;
            return false;
        }
#else
        await Task.CompletedTask.ConfigureAwait(false);
        a.LastError = "모바일 시스템 브라우저 OAuth 미결선 — 데스크톱에서 연결 후 볼트 동기화";
        return false;
#endif
    }

    public void Disconnect(LiveProvider p) { _acc[(int)p].Reset(LiveAuthPhase.Disconnected); Dirty = true; }

    // ══════════════════════ 백그라운드 갱신(무할당 틱) ══════════════════════
    /// <summary>틱(무할당 · 논블로킹) — 완료 수확 → 후속 단계(장기 토큰/프로필) → 만료 10 분 전 갱신 발행.</summary>
    public void Tick(double now)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Pump(0, now); Pump(1, now);
        LastTickUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public bool NeedsRefresh(LiveProvider p, double now)
    {
        var a = _acc[(int)p];
        if (!a.Access.IsSet || a.Op != LiveOAuthOp.None || now < a.RetryAt || a.ExpiresAt - now > RefreshLeadSec) return false;
        return p == LiveProvider.YouTube ? a.Refresh.IsSet : a.ExpiresAt > now;
    }

    private void Pump(int i, double now)
    {
        var a = _acc[i]; var call = _call[i];
        for (var hop = 0; hop < 8; hop++)
        {
            if (call.InFlight)
            {
                if (!call.TryComplete(out var status)) break;
                CryptographicOperations.ZeroMemory(_form[i].AsSpan(0, _formLen[i]));
                Handle(i, status, now);
                continue;
            }
            if (a.Queued) { a.Queued = false; Issue(i, now, default); continue; }
            if (a.Phase == LiveAuthPhase.Connected && a.Access.IsSet && now >= a.ExpiresAt)
            {
                a.Phase = LiveAuthPhase.Expired;
                if (i == 1) { a.LastError = "Meta 장기 토큰 만료 — 재인증 필요"; Dirty = true; }
            }
            if (NeedsRefresh((LiveProvider)i, now))
            {
                BackgroundRefreshAttempts++;
                a.Op = LiveOAuthOp.Refresh;
                if (a.Phase == LiveAuthPhase.Connected) a.Phase = LiveAuthPhase.Refreshing;
                Issue(i, now, default);
                continue;
            }
            break;
        }
    }

    /// <summary>요청 조립(무할당) — 비밀은 송신 버퍼(폼/URL)에 직접 복호화 · 완료 즉시 제로화.</summary>
    private void Issue(int i, double now, ReadOnlySpan<byte> code)
    {
        var a = _acc[i]; var call = _call[i];
        var url = call.Url.AsSpan(); var hdr = call.Hdr.AsSpan(); var f = _form[i].AsSpan();
        int n = 0, h = 0, b = 0;
        call.UrlHasSecret = false;
        if (i == 0)
        {
            if (a.Op == LiveOAuthOp.Profile)
            {
                GraphBytes.Put(url, ref n, YouTubeChannelsUrl);
                h = WriteBearer(LiveProvider.YouTube, hdr);
                call.Method = GraphHttpMethod.Get; call.Body = default;
            }
            else
            {
                GraphBytes.Put(url, ref n, GoogleTokenUrl);
                GraphBytes.Put(f, ref b, a.Op == LiveOAuthOp.Exchange ? "grant_type=authorization_code&code="u8 : "grant_type=refresh_token&refresh_token="u8);
                if (a.Op == LiveOAuthOp.Exchange) GraphBytes.PutEncoded(f, ref b, code); else PutSecretEncoded(f, ref b, a.Refresh);
                GraphBytes.Put(f, ref b, "&client_id="u8); GraphBytes.PutEncoded(f, ref b, _clientId[0]);
                GraphBytes.Put(f, ref b, "&client_secret="u8); PutSecretEncoded(f, ref b, _secret[0]);
                if (a.Op == LiveOAuthOp.Exchange)
                {
                    GraphBytes.Put(f, ref b, "&redirect_uri="u8); GraphBytes.PutEncoded(f, ref b, _redirect[0]);
                    GraphBytes.Put(f, ref b, "&code_verifier="u8); PutSecretEncoded(f, ref b, a.Verifier);
                }
                GraphBytes.Put(hdr, ref h, "Content-Type: application/x-www-form-urlencoded\r\nAccept: application/json\r\n"u8);
                call.Method = GraphHttpMethod.Post; call.Body = _form[i].AsMemory(0, b);
            }
        }
        else if (a.Op == LiveOAuthOp.Profile)
        {
            GraphBytes.Put(url, ref n, MetaAccountsUrl);
            h = WriteBearer(LiveProvider.Instagram, hdr);
            call.Method = GraphHttpMethod.Get; call.Body = default;
        }
        else
        {
            GraphBytes.Put(url, ref n, MetaTokenUrl);
            GraphBytes.Put(url, ref n, "?client_id="u8); GraphBytes.PutEncoded(url, ref n, _clientId[1]);
            GraphBytes.Put(url, ref n, "&client_secret="u8); PutSecretEncoded(url, ref n, _secret[1]);
            if (a.Op == LiveOAuthOp.Exchange)
            {
                GraphBytes.Put(url, ref n, "&redirect_uri="u8); GraphBytes.PutEncoded(url, ref n, _redirect[1]);
                GraphBytes.Put(url, ref n, "&code="u8); GraphBytes.PutEncoded(url, ref n, code);
                GraphBytes.Put(url, ref n, "&code_verifier="u8); PutSecretEncoded(url, ref n, a.Verifier);
            }
            else { GraphBytes.Put(url, ref n, "&grant_type=fb_exchange_token&fb_exchange_token="u8); PutSecretEncoded(url, ref n, a.Access); }
            call.UrlHasSecret = true;
            call.Method = GraphHttpMethod.Get; call.Body = default;
        }
        call.UrlLen = n; call.HdrLen = h; _formLen[i] = b;
        call.Send(TransportFor((LiveProvider)i), _cts.Token);
    }

    private static void PutSecretEncoded(Span<byte> d, ref int n, GraphSecretSlot s)
    {
        Span<byte> tmp = stackalloc byte[TokenCap];
        try { var l = s.CopyTo(tmp); GraphBytes.PutEncoded(d, ref n, tmp[..l]); }
        finally { CryptographicOperations.ZeroMemory(tmp); }
    }

    private void Handle(int i, int status, double now)
    {
        var a = _acc[i]; var op = a.Op; var body = _call[i].Resp.BodySpan;
        a.Op = LiveOAuthOp.None;
        if (status is >= 200 and < 300)
        {
            if (op == LiveOAuthOp.Profile)
            {
                if (!ApplyProfile(i, body)) { SchemaRejections++; Fail(a, "프로필 응답 스키마 불일치(채널/IG 비즈니스 계정 없음)"); return; }
                a.Phase = LiveAuthPhase.Connected; a.Verifier.Clear(); a.Failures = 0; Exchanges++; Dirty = true;
                return;
            }
            if (!ApplyToken(a, body, now)) { SchemaRejections++; Fail(a, "토큰 응답 스키마 불일치"); return; }
            a.Failures = 0; a.RetryAt = 0;
            if (op == LiveOAuthOp.Refresh) { a.Refreshes++; Refreshes++; a.Phase = LiveAuthPhase.Connected; a.LastError = null; Dirty = true; return; }
            a.Op = i == 1 && op == LiveOAuthOp.Exchange ? LiveOAuthOp.LongLived : LiveOAuthOp.Profile;
            a.Queued = true;
            return;
        }
        var reauth = status is 400 or 401 && (ReadOAuthError(body) || (TokenBucketRateLimitOAuthSecurityGuard.TryReadError(body, out var code, out _) && code is 190 or 100 or 101));
        if (op == LiveOAuthOp.Refresh && !reauth)
        {
            a.RefreshFailures++;
            a.RetryAt = now + Backoff(++a.Failures);
            a.Phase = now < a.ExpiresAt ? LiveAuthPhase.Connected : LiveAuthPhase.Expired;
            a.LastError = status == 0 ? "갱신 네트워크 실패 — 백오프 재시도" : "갱신 서버 오류 — 백오프 재시도";
            return;
        }
        if (op == LiveOAuthOp.Refresh) a.RefreshFailures++;
        Fail(a, reauth ? "토큰 거부(invalid_grant/190) — 재인증 필요" : status == 0 ? "토큰 엔드포인트 네트워크 실패" : "토큰 엔드포인트 오류");
    }

    private void Fail(LiveOAuthAccount a, string why)
    {
        a.Reset(LiveAuthPhase.Failed);
        a.LastError = why;
        Dirty = true;
    }

    private double Backoff(int n)
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return Math.Min(BackoffBaseSec * Math.Pow(2, Math.Max(n - 1, 0)), BackoffMaxSec) * (0.75 + 0.25 * (_rng / (double)uint.MaxValue));
    }

    /// <summary>토큰 응답 판독(무할당) — access_token · refresh_token · expires_in. 이스케이프 값은 스택 버퍼로 복원.</summary>
    public static bool TryParseTokenResponse(ReadOnlySpan<byte> json, Span<byte> access, out int accessLen, Span<byte> refresh, out int refreshLen, out long expiresIn)
    {
        accessLen = refreshLen = 0; expiresIn = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 1) continue;
                if (r.ValueTextEquals("access_token"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= access.Length) accessLen = r.CopyString(access); }
                else if (r.ValueTextEquals("refresh_token"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= refresh.Length) refreshLen = r.CopyString(refresh); }
                else if (r.ValueTextEquals("expires_in"u8)) { if (r.Read() && r.TokenType == JsonTokenType.Number) r.TryGetInt64(out expiresIn); }
            }
        }
        catch (JsonException) { return false; }
        return accessLen > 0 && expiresIn > 0;
    }

    private static bool ApplyToken(LiveOAuthAccount a, ReadOnlySpan<byte> json, double now)
    {
        Span<byte> acc = stackalloc byte[TokenCap]; Span<byte> rt = stackalloc byte[TokenCap];
        try
        {
            if (!TryParseTokenResponse(json, acc, out var al, rt, out var rl, out var exp)) return false;
            a.Access.Set(acc[..al]);
            if (rl > 0) a.Refresh.Set(rt[..rl]);
            a.IssuedAt = now; a.ExpiresAt = now + exp;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(acc); CryptographicOperations.ZeroMemory(rt); }
    }

    /// <summary>Google OAuth 오류 {"error":"invalid_grant"|"invalid_client"} — 재인증 필요 여부.</summary>
    private static bool ReadOAuthError(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 1 && r.ValueTextEquals("error"u8) && r.Read() && r.TokenType == JsonTokenType.String)
                    return r.ValueTextEquals("invalid_grant"u8) || r.ValueTextEquals("invalid_client"u8) || r.ValueTextEquals("unauthorized_client"u8);
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>프로필(연결 시 1 회 · 표시 문자열 할당 허용) — YouTube items[0].id/snippet.title · Meta data[*].instagram_business_account.id/username.</summary>
    private bool ApplyProfile(int i, ReadOnlySpan<byte> json)
    {
        var a = _acc[i];
        string? name = null; var idLen = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            var inTarget = false; var targetDepth = -1;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth < targetDepth) { if (idLen > 0) break; inTarget = false; targetDepth = -1; }
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (i == 0)
                {
                    if (r.CurrentDepth == 3 && r.ValueTextEquals("id"u8) && idLen == 0 && r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= LiveOAuthAccount.IdCap) idLen = r.CopyString(a.ProfileId);
                    else if (r.CurrentDepth == 4 && r.ValueTextEquals("title"u8) && name is null && r.Read() && r.TokenType == JsonTokenType.String) name = r.GetString();
                }
                else if (r.ValueTextEquals("instagram_business_account"u8)) { inTarget = true; targetDepth = r.CurrentDepth + 1; }
                else if (inTarget && r.CurrentDepth == targetDepth)
                {
                    if (r.ValueTextEquals("id"u8) && r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= LiveOAuthAccount.IdCap) idLen = r.CopyString(a.ProfileId);
                    else if (r.ValueTextEquals("username"u8) && r.Read() && r.TokenType == JsonTokenType.String) name = "@" + r.GetString();
                }
            }
        }
        catch (JsonException) { return false; }
        if (idLen == 0 || (i == 1 && !Utf8Parser.TryParse(a.ProfileId.AsSpan(0, idLen), out ulong _, out _))) return false;
        a.ProfileIdLen = idLen;
        a.ChannelId = System.Text.Encoding.UTF8.GetString(a.ProfileId, 0, idLen);
        a.ProfileName = name ?? a.ChannelId;
        return true;
    }

    // ══════════════════════ 소비자 API(무할당) ══════════════════════
    /// <summary><c>Authorization: Bearer {token}\r\n</c> 를 dst 에 기록 — 미연결 시 0.</summary>
    public int WriteBearer(LiveProvider p, Span<byte> dst)
    {
        var a = _acc[(int)p];
        if (!a.Access.IsSet) return 0;
        var n = 0;
        GraphBytes.Put(dst, ref n, "Authorization: Bearer "u8);
        n += a.Access.CopyTo(dst[n..]);
        GraphBytes.Put(dst, ref n, "\r\n"u8);
        return n;
    }

    /// <summary>액세스 토큰을 소비자 슬롯(v17.48 레이트 가드 등)으로 재암호화 복사.</summary>
    public bool CopyAccessTo(LiveProvider p, GraphSecretSlot dst)
    {
        var a = _acc[(int)p];
        if (!a.IsConnected) return false;
        Span<byte> tmp = stackalloc byte[TokenCap];
        try { var l = a.Access.CopyTo(tmp); dst.Set(tmp[..l]); return true; }
        finally { CryptographicOperations.ZeroMemory(tmp); }
    }

    /// <summary>UI 마스킹(UI 전용 할당) — 앞 6 · 뒤 4.</summary>
    public string TokenMasked(LiveProvider p)
    {
        var a = _acc[(int)p];
        if (!a.Access.IsSet) return "—";
        Span<byte> tmp = stackalloc byte[TokenCap];
        try
        {
            var l = a.Access.CopyTo(tmp);
            return l > 12 ? System.Text.Encoding.ASCII.GetString(tmp[..6]) + "…" + System.Text.Encoding.ASCII.GetString(tmp.Slice(l - 4, 4)) : "****";
        }
        finally { CryptographicOperations.ZeroMemory(tmp); }
    }

    // ══════════════════════ AES-GCM 256 영속화(비핫패스) ══════════════════════
    /// <summary>연결 계정 → <c>magic | ver | nonce12 | tag16 | ciphertext</c> 원자적 기록(임시 파일 → 교체). 평문 버퍼 즉시 제로화.</summary>
    public bool SaveVault()
    {
        var key = KeyOrNull();
        if (key is null) return false;
        var plain = new byte[8192];
        try
        {
            var n = 0;
            plain[n++] = (byte)VaultVersion;
            for (var i = 0; i < 2; i++)
            {
                var a = _acc[i];
                if (!a.Access.IsSet || a.Phase is LiveAuthPhase.Failed or LiveAuthPhase.Disconnected) continue;
                plain[n++] = (byte)i;
                BitConverter.TryWriteBytes(plain.AsSpan(n), a.IssuedAt); n += 8;
                BitConverter.TryWriteBytes(plain.AsSpan(n), a.ExpiresAt); n += 8;
                BitConverter.TryWriteBytes(plain.AsSpan(n), a.Refreshes); n += 8;
                n = PutSlot(plain, n, a.Access); n = PutSlot(plain, n, a.Refresh);
                n = PutBytes(plain, n, a.ProfileIdSpan);
                n = PutBytes(plain, n, System.Text.Encoding.UTF8.GetBytes(a.ProfileName));
            }
            var file = new byte[4 + 12 + 16 + n];
            BitConverter.TryWriteBytes(file.AsSpan(0, 4), VaultMagic);
            var nonce = file.AsSpan(4, 12); var tag = file.AsSpan(16, 16);
            RandomNumberGenerator.Fill(nonce);
            using (var gcm = new AesGcmCompat(key, 16)) gcm.Encrypt(nonce, plain.AsSpan(0, n), file.AsSpan(32, n), tag, VaultAad);
            Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
            var tmp = VaultPath + ".tmp";
            File.WriteAllBytes(tmp, file);
            File.Move(tmp, VaultPath, overwrite: true);
            VaultSaves++; Dirty = false; LastVaultError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LastVaultError = "볼트 저장 실패: " + ex.GetType().Name;
            PromptFlowStudio.Program.Log("LiveOAuth.Save", ex);
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>볼트 복원 — 태그 불일치(변조/키 불일치) 시 false · 기존 계정 상태 유지. 만료 계정은 Expired(YouTube 는 다음 틱 갱신).</summary>
    public bool LoadVault(double now)
    {
        if (!File.Exists(VaultPath)) { LastVaultError = "볼트 없음"; return false; }
        var key = KeyOrNull();
        if (key is null) return false;
        byte[]? plain = null;
        try
        {
            var file = File.ReadAllBytes(VaultPath);
            if (file.Length < 33 || BitConverter.ToInt32(file, 0) != VaultMagic) { LastVaultError = "볼트 형식 불일치"; return false; }
            var n = file.Length - 32;
            plain = new byte[n];
            using (var gcm = new AesGcmCompat(key, 16)) gcm.Decrypt(file.AsSpan(4, 12), file.AsSpan(32, n), file.AsSpan(16, 16), plain, VaultAad);
            if (plain[0] != VaultVersion) { LastVaultError = "볼트 버전 불일치"; return false; }
            var p = 1;
            while (p < n)
            {
                var a = _acc[plain[p++] & 1];
                a.Reset(LiveAuthPhase.Disconnected);
                a.IssuedAt = BitConverter.ToDouble(plain, p); p += 8;
                a.ExpiresAt = BitConverter.ToDouble(plain, p); p += 8;
                a.Refreshes = BitConverter.ToInt64(plain, p); p += 8;
                p = GetSlot(plain, p, a.Access); p = GetSlot(plain, p, a.Refresh);
                var idLen = BitConverter.ToUInt16(plain, p); p += 2;
                plain.AsSpan(p, idLen).CopyTo(a.ProfileId); a.ProfileIdLen = idLen; p += idLen;
                var nameLen = BitConverter.ToUInt16(plain, p); p += 2;
                a.ProfileName = System.Text.Encoding.UTF8.GetString(plain, p, nameLen); p += nameLen;
                a.ChannelId = System.Text.Encoding.UTF8.GetString(a.ProfileId, 0, idLen);
                a.Phase = now < a.ExpiresAt ? LiveAuthPhase.Connected : LiveAuthPhase.Expired;
            }
            VaultLoads++; LastVaultError = null; Dirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or IndexOutOfRangeException)
        {
            LastVaultError = ex is CryptographicException ? "볼트 인증 태그 불일치(변조/키 불일치)" : "볼트 판독 실패: " + ex.GetType().Name;
            return false;
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
    }

    private byte[]? KeyOrNull()
    {
        byte[]? key = null;
        try { key = _vaultKey?.Invoke(); }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException) { PromptFlowStudio.Program.Log("LiveOAuth.Key", ex); }
        if (key is { Length: 32 }) return key;
        LastVaultError = "볼트 키(32 B) 없음 — SecureSecretVault 미결선";
        return null;
    }

    private static int PutSlot(byte[] d, int n, GraphSecretSlot s)
    {
        var l = s.CopyTo(d.AsSpan(n + 2));
        BitConverter.TryWriteBytes(d.AsSpan(n), (ushort)l);
        return n + 2 + l;
    }

    private static int GetSlot(byte[] d, int p, GraphSecretSlot s)
    {
        var l = BitConverter.ToUInt16(d, p); p += 2;
        if (l > 0) s.Set(d.AsSpan(p, l));
        return p + l;
    }

    private static int PutBytes(byte[] d, int n, ReadOnlySpan<byte> s)
    {
        BitConverter.TryWriteBytes(d.AsSpan(n), (ushort)s.Length);
        s.CopyTo(d.AsSpan(n + 2));
        return n + 2 + s.Length;
    }

    public void Dispose()
    {
        _cts.Cancel(); _cts.Dispose();
        foreach (var a in _acc) a.Dispose();
        foreach (var s in _secret) s.Dispose();
        _http?.Dispose();
    }
}
