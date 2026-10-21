using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.Publisher;

public enum SocialPlatform : byte { YouTube, Instagram }
public enum SocialLinkState : byte { Disconnected, Connected, Expiring, Refreshing, Invalid }
public enum SocialUploadPhase : byte { Idle, Refreshing, Init, Uploading, Processing, Publishing, Done, Failed }

/// <summary>v17.71.0 업로드 메타데이터(제목 · 설명 · 해시태그) — AI 추천기 산출 · 편집창 수정.</summary>
public sealed class SocialPostMetadata
{
    public const int MaxTags = 5;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Tags { get; } = new string[MaxTags];
    public int TagCount { get; set; }
    public string Privacy { get; set; } = "private";
}

/// <summary>v17.71.0 업로드 결과(플랫폼 미디어 ID · 소요 · 실패 사유).</summary>
public readonly record struct SocialUploadResult(SocialPlatform Platform, bool Ok, string? MediaId, int Chunks, long Bytes, double Ms, int Refreshes, string? Error);

/// <summary>
/// v17.71.0 OAuth2 토큰 보안 볼트 — Access/Refresh 토큰은 메모리에서 v17.48 <see cref="GraphSecretSlot"/>(프로세스 임시 키 AES-GCM 암호문)로만 보관,
/// 디스크에는 영속 키(<see cref="Security.SecureSecretVault"/> DPAPI 봉인 32 B · 환경변수 우선)로 AES-GCM 봉인한 단일 파일(magic · nonce · tag · 암호문)로 저장.
/// 평문은 헤더 조립 순간 송신 버퍼에만 복호화(무할당) · 봉인/해제 임시 버퍼는 즉시 제로화. 변조/키 불일치 = 태그 실패 → 빈 볼트(재연동 유도).
/// </summary>
public sealed class OAuthTokenVault : IDisposable
{
    public const int Platforms = 2, FileCap = 16 * 1024;
    private static ReadOnlySpan<byte> Magic => "PFOV1\0"u8;

    private readonly GraphSecretSlot[] _access = { new(), new() }, _refresh = { new(), new() };
    private readonly long[] _expiresAt = new long[Platforms], _issuedAt = new long[Platforms];
    private readonly string[] _accountId = { "", "" };
    private readonly Func<byte[]> _key;

    public OAuthTokenVault(string path, Func<byte[]> persistentKey) { Path = path; _key = persistentKey; }

    public string Path { get; }
    public long Saves { get; private set; }
    public long Loads { get; private set; }
    public long TamperRejects { get; private set; }
    public bool HasAccess(SocialPlatform p) => _access[(int)p].IsSet;
    public bool HasRefresh(SocialPlatform p) => _refresh[(int)p].IsSet;
    public long ExpiresAtUnix(SocialPlatform p) => _expiresAt[(int)p];
    public long IssuedAtUnix(SocialPlatform p) => _issuedAt[(int)p];
    public string AccountId(SocialPlatform p) => _accountId[(int)p];
    public double SecondsLeft(SocialPlatform p, long nowUnix) => HasAccess(p) ? _expiresAt[(int)p] - nowUnix : 0;
    internal GraphSecretSlot Access(SocialPlatform p) => _access[(int)p];
    internal GraphSecretSlot Refresh(SocialPlatform p) => _refresh[(int)p];
    /// <summary>메모리 암호문에 평문 잔존 여부(진단).</summary>
    public bool MemoryLeaks(SocialPlatform p, ReadOnlySpan<byte> plain) => _access[(int)p].CipherContains(plain) || _refresh[(int)p].CipherContains(plain);

    public void Store(SocialPlatform p, ReadOnlySpan<char> access, ReadOnlySpan<char> refresh, long expiresAtUnix, long nowUnix, string? accountId = null)
    {
        _access[(int)p].Set(access);
        if (!refresh.IsEmpty) _refresh[(int)p].Set(refresh);
        _expiresAt[(int)p] = expiresAtUnix; _issuedAt[(int)p] = nowUnix;
        if (accountId is not null) _accountId[(int)p] = accountId;
    }

    internal void StoreUtf8(SocialPlatform p, ReadOnlySpan<byte> access, ReadOnlySpan<byte> refresh, long expiresAtUnix, long nowUnix)
    {
        _access[(int)p].Set(access);
        if (!refresh.IsEmpty) _refresh[(int)p].Set(refresh);
        _expiresAt[(int)p] = expiresAtUnix; _issuedAt[(int)p] = nowUnix;
    }

    public void Revoke(SocialPlatform p) { _access[(int)p].Clear(); _refresh[(int)p].Clear(); _expiresAt[(int)p] = 0; _issuedAt[(int)p] = 0; _accountId[(int)p] = ""; }

    /// <summary>디스크 봉인 저장(AES-GCM · 원자적 교체).</summary>
    public bool Save()
    {
        var plain = ArrayPool<byte>.Shared.Rent(FileCap);
        var file = ArrayPool<byte>.Shared.Rent(FileCap + 64);
        var key = _key();
        try
        {
            var n = 0; var b = plain.AsSpan(0, FileCap);
            for (var p = 0; p < Platforms; p++)
            {
                n = PutSlot(b, n, _access[p]); n = PutSlot(b, n, _refresh[p]);
                BinaryPrimitives.WriteInt64LittleEndian(b[n..], _expiresAt[p]); n += 8;
                BinaryPrimitives.WriteInt64LittleEndian(b[n..], _issuedAt[p]); n += 8;
                var acc = Encoding.UTF8.GetBytes(_accountId[p].AsSpan(0, Math.Min(_accountId[p].Length, 128)), b[(n + 2)..]);
                BinaryPrimitives.WriteUInt16LittleEndian(b[n..], (ushort)acc); n += 2 + acc;
            }
            var f = file.AsSpan(); Magic.CopyTo(f);
            var nonce = f.Slice(6, 12); RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcmCompat(key, 16)) aes.Encrypt(nonce, b[..n], f.Slice(34, n), f.Slice(18, 16), Magic);
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) fs.Write(f[..(34 + n)]);
            File.Move(tmp, Path, true);
            Saves++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            PromptFlowStudio.Program.Log("OAuthVault.Save", ex);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key);
            ArrayPool<byte>.Shared.Return(plain); ArrayPool<byte>.Shared.Return(file);
        }
    }

    private static int PutSlot(Span<byte> b, int n, GraphSecretSlot s)
    {
        var len = s.CopyTo(b[(n + 2)..]);
        BinaryPrimitives.WriteUInt16LittleEndian(b[n..], (ushort)len);
        return n + 2 + len;
    }

    /// <summary>디스크 해제(태그 검증 실패 = 변조 · 키 불일치 → false · 메모리 볼트 불변).</summary>
    public bool Load()
    {
        if (!File.Exists(Path)) return false;
        var file = ArrayPool<byte>.Shared.Rent(FileCap + 64);
        var plain = ArrayPool<byte>.Shared.Rent(FileCap);
        var key = _key();
        try
        {
            int len;
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 34 || fs.Length > FileCap + 34) return false;
                len = (int)fs.Length; fs.ReadExactly(file.AsSpan(0, len));
            }
            var f = file.AsSpan(0, len);
            if (!f[..6].SequenceEqual(Magic)) { TamperRejects++; return false; }
            var n = len - 34;
            try { using var aes = new AesGcmCompat(key, 16); aes.Decrypt(f.Slice(6, 12), f.Slice(34, n), f.Slice(18, 16), plain.AsSpan(0, n), Magic); }
            catch (CryptographicException) { TamperRejects++; return false; }
            var b = plain.AsSpan(0, n); var o = 0;
            for (var p = 0; p < Platforms; p++)
            {
                o = GetSlot(b, o, _access[p]); o = GetSlot(b, o, _refresh[p]);
                _expiresAt[p] = BinaryPrimitives.ReadInt64LittleEndian(b[o..]); o += 8;
                _issuedAt[p] = BinaryPrimitives.ReadInt64LittleEndian(b[o..]); o += 8;
                var al = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2;
                _accountId[p] = Encoding.UTF8.GetString(b.Slice(o, al)); o += al;
            }
            Loads++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            PromptFlowStudio.Program.Log("OAuthVault.Load", ex);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key);
            ArrayPool<byte>.Shared.Return(plain); ArrayPool<byte>.Shared.Return(file);
        }
    }

    private static int GetSlot(ReadOnlySpan<byte> b, int o, GraphSecretSlot s)
    {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2;
        if (len > 0) s.Set(b.Slice(o, len)); else s.Clear();
        return o + len;
    }

    public void Dispose() { foreach (var s in _access) s.Dispose(); foreach (var s in _refresh) s.Dispose(); }
}

/// <summary>v17.71.0 multipart 본문 조립기(RFC 2046 · multipart/related(YouTube) · multipart/form-data(Graph)) — 재사용 버퍼 · 무할당.</summary>
public static class MultipartBodyWriter
{
    public static ReadOnlySpan<byte> Boundary => "PromptFlowBoundary7MA4YWxkTrZu0gW"u8;

    public static void Begin(Span<byte> d, ref int n) { GraphBytes.Put(d, ref n, "--"u8); GraphBytes.Put(d, ref n, Boundary); GraphBytes.Put(d, ref n, "\r\n"u8); }

    /// <summary>파트 헤더(Content-Type · 선택 Content-Disposition name) + 빈 줄.</summary>
    public static void PartHeader(Span<byte> d, ref int n, ReadOnlySpan<byte> contentType, ReadOnlySpan<byte> formName = default)
    {
        if (!formName.IsEmpty) { GraphBytes.Put(d, ref n, "Content-Disposition: form-data; name=\""u8); GraphBytes.Put(d, ref n, formName); GraphBytes.Put(d, ref n, "\"\r\n"u8); }
        GraphBytes.Put(d, ref n, "Content-Type: "u8); GraphBytes.Put(d, ref n, contentType); GraphBytes.Put(d, ref n, "\r\n\r\n"u8);
    }

    public static void Next(Span<byte> d, ref int n) { GraphBytes.Put(d, ref n, "\r\n--"u8); GraphBytes.Put(d, ref n, Boundary); GraphBytes.Put(d, ref n, "\r\n"u8); }
    public static void End(Span<byte> d, ref int n) { GraphBytes.Put(d, ref n, "\r\n--"u8); GraphBytes.Put(d, ref n, Boundary); GraphBytes.Put(d, ref n, "--\r\n"u8); }

    /// <summary>JSON 문자열 이스케이프(UTF-16 → UTF-8 · 제어문자 \uXXXX) — 따옴표 포함 기록.</summary>
    public static void JsonString(Span<byte> d, ref int n, ReadOnlySpan<char> s)
    {
        d[n++] = (byte)'"';
        Span<byte> u = stackalloc byte[4];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' || c == '\\') { d[n++] = (byte)'\\'; d[n++] = (byte)c; }
            else if (c == '\n') { d[n++] = (byte)'\\'; d[n++] = (byte)'n'; }
            else if (c < 0x20) { GraphBytes.Put(d, ref n, "\\u00"u8); d[n++] = Hex(c >> 4); d[n++] = Hex(c & 15); }
            else if (c < 0x80) d[n++] = (byte)c;
            else
            {
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(c, s[i + 1]); i++; }
                if (!new Rune(cp).TryEncodeToUtf8(u, out var w)) continue;
                u[..w].CopyTo(d[n..]); n += w;
            }
        }
        d[n++] = (byte)'"';
    }

    private static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'a' + v - 10);
}

/// <summary>
/// [PromptFlow Studio v17.71.0] 🔐 소셜 미디어 API 게이트웨이 엔진 — YouTube Data API v3 · Meta(Instagram) Graph API 실 업로드 + OAuth2 토큰 볼트 자동 갱신.
///   ▸ YouTube: ≤ <see cref="MultipartMax"/> = <c>uploadType=multipart</c>(multipart/related: JSON snippet/status 파트 + video/mp4 파트) ·
///     초과 = <c>uploadType=resumable</c> 세션(Location) → 256 KiB 배수 청크 PUT(Content-Range · 308 Range 재개) → 200/201 id.
///   ▸ Instagram: <c>POST /{ig-user-id}/media?media_type=REELS&amp;upload_type=resumable</c>(multipart/form-data caption) → rupload.facebook.com 청크(offset · file_size) →
///     상태 폴링(status_code FINISHED) → <c>media_publish</c>.
///   ▸ OAuth2: 만료 &lt; <see cref="RefreshSkewSec"/> 또는 401 → refresh_token(Google oauth2/token) · fb_exchange_token(Meta) 갱신 → 볼트 봉인 저장 → 1 회 재시도.
///     클라이언트 ID/시크릿 = 환경변수(<c>PROMPTFLOW_SECRET_YT_CLIENT_ID</c> 등 · 하드코딩 0) · PKCE 인가 URL/코드 교환 지원.
///   ▸ 헤더/URL 조립은 재사용 <see cref="GraphCall"/> 버퍼에 토큰 직접 복호화 → 완료 즉시 제로화(무할당 · 실 HttpClient 경로의 요청 객체 할당은 BCL 한계).
/// </summary>
public sealed class SocialMediaApiGatewayEngine : IDisposable
{
    public const int ChunkBytes = 32 * 256 * 1024, MultipartMax = 4 * 1024 * 1024, BodyCap = MultipartMax + 64 * 1024, MaxPolls = 30, MaxRetries = 3;
    public const long RefreshSkewSec = 300, YouTubeTokenSec = 3600, MetaTokenSec = 60L * 86400;
    public static ReadOnlySpan<byte> YtMultipartUrl => "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=multipart&part=snippet,status"u8;
    public static ReadOnlySpan<byte> YtResumableUrl => "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status"u8;
    public static ReadOnlySpan<byte> GoogleTokenUrl => "https://oauth2.googleapis.com/token"u8;
    public static ReadOnlySpan<byte> GraphBase => "https://graph.facebook.com/v19.0/"u8;
    public static ReadOnlySpan<byte> RuploadBase => "https://rupload.facebook.com/ig-api-upload/v19.0/"u8;
    public const string GoogleAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth", MetaAuthorizeUrl = "https://www.facebook.com/v19.0/dialog/oauth";
    public const string YtScope = "https://www.googleapis.com/auth/youtube.upload", MetaScope = "instagram_basic,instagram_content_publish,pages_show_list";

    private readonly GraphCall _call = new();
    private readonly byte[] _body = new byte[BodyCap];
    private readonly byte[] _id = new byte[256], _uri = new byte[1024], _tok = new byte[GraphSecretSlot.Cap], _tok2 = new byte[GraphSecretSlot.Cap];
    private readonly byte[] _client = new byte[512];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long[] _uploads = new long[2], _failures = new long[2], _refreshes = new long[2];
    private readonly SocialUploadPhase[] _phase = new SocialUploadPhase[2];
    private readonly float[] _progress = new float[2];
    private readonly string?[] _lastId = new string?[2], _lastError = new string?[2];
    private readonly int[] _lastStatus = new int[2], _lastRetryAfter = new int[2];
    private readonly byte[][] _lastBody = { new byte[512], new byte[512] };
    private readonly int[] _lastBodyLen = new int[2];
    private int _idLen, _uriLen;
    private string? _pkceVerifier;

    public SocialMediaApiGatewayEngine(IGraphTransport transport, OAuthTokenVault vault)
    {
        Transport = transport; Vault = vault;
    }

    public IGraphTransport Transport { get; set; }
    public OAuthTokenVault Vault { get; }
    public Func<long> UnixNow { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    /// <summary>클라이언트 크리덴셜 조회(키 → 값 · null = 미설정) — 기본 환경변수 <c>PROMPTFLOW_SECRET_{KEY}</c>.</summary>
    public Func<string, string?> Credential { get; set; } = static k => Environment.GetEnvironmentVariable("PROMPTFLOW_SECRET_" + k);
    /// <summary>재개/rupload 청크 크기(256 KiB 배수 강제 · 기본 8 MiB).</summary>
    public int ChunkSize { get => _chunk; set => _chunk = Math.Clamp(value / (256 * 1024), 1, ChunkBytes / (256 * 1024)) * 256 * 1024; }
    private int _chunk = ChunkBytes;
    public string InstagramUserId { get; set; } = "";
    public string RedirectUri { get; set; } = "http://127.0.0.1:53682/callback";
    public long Uploads(SocialPlatform p) => _uploads[(int)p];
    public long Failures(SocialPlatform p) => _failures[(int)p];
    public long Refreshes(SocialPlatform p) => _refreshes[(int)p];
    public SocialUploadPhase Phase(SocialPlatform p) => _phase[(int)p];
    public float Progress(SocialPlatform p) => _progress[(int)p];
    public string? LastMediaId(SocialPlatform p) => _lastId[(int)p];
    public string? LastError(SocialPlatform p) => _lastError[(int)p];
    /// <summary>v17.73 마지막 업로드 HTTP 상태 · Retry-After · 오류 본문(쿼터 가드 분류용).</summary>
    public int LastStatus(SocialPlatform p) => _lastStatus[(int)p];
    public int LastRetryAfterSec(SocialPlatform p) => _lastRetryAfter[(int)p];
    public ReadOnlySpan<byte> LastErrorBody(SocialPlatform p) => _lastBody[(int)p].AsSpan(0, _lastBodyLen[(int)p]);

    private void RecordStatus(SocialPlatform p, int st)
    {
        _lastStatus[(int)p] = st; _lastRetryAfter[(int)p] = _call.Resp.RetryAfterSec;
        var b = _call.Resp.BodySpan; var k = Math.Min(b.Length, 512); b[..k].CopyTo(_lastBody[(int)p]); _lastBodyLen[(int)p] = k;
    }
    public long ChunksSent { get; private set; }
    public long BytesSent { get; private set; }
    public double LastHeaderUs { get; private set; }

    public static string PlatformName(SocialPlatform p) => p == SocialPlatform.YouTube ? "YouTube Shorts" : "Instagram Reels";
    public static string StateName(SocialLinkState s) => s switch
    {
        SocialLinkState.Connected => "연동됨", SocialLinkState.Expiring => "만료 임박", SocialLinkState.Refreshing => "갱신 중", SocialLinkState.Invalid => "재인증 필요", _ => "미연동",
    };

    public SocialLinkState State(SocialPlatform p)
    {
        if (_phase[(int)p] == SocialUploadPhase.Refreshing) return SocialLinkState.Refreshing;
        if (!Vault.HasAccess(p)) return SocialLinkState.Disconnected;
        var left = Vault.SecondsLeft(p, UnixNow());
        if (left <= 0) return Vault.HasRefresh(p) || p == SocialPlatform.Instagram ? SocialLinkState.Expiring : SocialLinkState.Invalid;
        return left < RefreshSkewSec ? SocialLinkState.Expiring : SocialLinkState.Connected;
    }

    // ══════════════════════ OAuth2 인가(비핫패스) ══════════════════════
    /// <summary>PKCE(S256) 인가 URL — 사용자가 브라우저에서 승인 → 리디렉션 code 를 <see cref="ExchangeCodeAsync"/> 로 교환.</summary>
    public string BuildAuthorizeUrl(SocialPlatform p)
    {
        Span<byte> raw = stackalloc byte[32]; RandomNumberGenerator.Fill(raw);
        _pkceVerifier = B64Url(raw);
        Span<byte> hash = stackalloc byte[32]; SHA256.HashData(Encoding.ASCII.GetBytes(_pkceVerifier), hash);
        var challenge = B64Url(hash);
        var id = Credential(p == SocialPlatform.YouTube ? "YT_CLIENT_ID" : "META_APP_ID") ?? "";
        return p == SocialPlatform.YouTube
            ? GoogleAuthorizeUrl + "?response_type=code&access_type=offline&prompt=consent&client_id=" + Uri.EscapeDataString(id) + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
              + "&scope=" + Uri.EscapeDataString(YtScope) + "&code_challenge_method=S256&code_challenge=" + challenge
            : MetaAuthorizeUrl + "?response_type=code&client_id=" + Uri.EscapeDataString(id) + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&scope=" + Uri.EscapeDataString(MetaScope)
              + "&code_challenge_method=S256&code_challenge=" + challenge;
    }

    private static string B64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>인가 코드 → 토큰 교환(Google: access+refresh · Meta: 단기 → 장기 교환) → 볼트 봉인.</summary>
    public async Task<bool> ExchangeCodeAsync(SocialPlatform p, string code, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var n = BuildExchange(p, code);
            var status = await PostFormAsync(n, ct).ConfigureAwait(false);
            if (status != 200) { _lastError[(int)p] = "코드 교환 실패 HTTP " + status; return false; }
            return AcceptToken(p, p == SocialPlatform.YouTube ? YouTubeTokenSec : MetaTokenSec);
        }
        finally { _gate.Release(); }
    }

    private int BuildExchange(SocialPlatform p, string code)
    {
        var n = 0; var b = _body.AsSpan();
        GraphBytes.Put(b, ref n, "grant_type=authorization_code&code="u8); GraphBytes.PutEncoded(b, ref n, Encoding.UTF8.GetBytes(code));
        GraphBytes.Put(b, ref n, "&redirect_uri="u8); GraphBytes.PutEncoded(b, ref n, Encoding.UTF8.GetBytes(RedirectUri));
        if (_pkceVerifier is { } v) { GraphBytes.Put(b, ref n, "&code_verifier="u8); GraphBytes.Put(b, ref n, Encoding.ASCII.GetBytes(v)); }
        PutClient(b, ref n, p);
        PrepareForm(p == SocialPlatform.YouTube ? GoogleTokenUrl : "https://graph.facebook.com/v19.0/oauth/access_token"u8, n);
        return n;
    }

    private void PutClient(Span<byte> b, ref int n, SocialPlatform p)
    {
        var id = Credential(p == SocialPlatform.YouTube ? "YT_CLIENT_ID" : "META_APP_ID");
        var secret = Credential(p == SocialPlatform.YouTube ? "YT_CLIENT_SECRET" : "META_APP_SECRET");
        if (!string.IsNullOrEmpty(id)) { GraphBytes.Put(b, ref n, "&client_id="u8); var k = Encoding.UTF8.GetBytes(id, _client); GraphBytes.PutEncoded(b, ref n, _client.AsSpan(0, k)); }
        if (!string.IsNullOrEmpty(secret)) { GraphBytes.Put(b, ref n, "&client_secret="u8); var k = Encoding.UTF8.GetBytes(secret, _client); GraphBytes.PutEncoded(b, ref n, _client.AsSpan(0, k)); CryptographicOperations.ZeroMemory(_client); }
    }

    private void PrepareForm(ReadOnlySpan<byte> url, int bodyLen)
    {
        _call.Method = GraphHttpMethod.Post; _call.UrlHasSecret = false;
        _call.UrlLen = 0; GraphBytes.Put(_call.Url, ref _call.UrlLen, url);
        _call.HdrLen = 0; GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "Content-Type: application/x-www-form-urlencoded\r\n"u8);
        _call.Body = _body.AsMemory(0, bodyLen);
    }

    private async ValueTask<int> PostFormAsync(int bodyLen, CancellationToken ct)
    {
        var st = await SendAsync(ct).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_body.AsSpan(0, bodyLen));   // 리프레시 토큰 · 클라이언트 시크릿 포함
        return st;
    }

    /// <summary>토큰 응답(JSON access_token · refresh_token · expires_in) → 볼트(무할당 판독 · 봉인 저장).</summary>
    private bool AcceptToken(SocialPlatform p, long defaultTtl)
    {
        var body = _call.Resp.BodySpan;
        var a = JsonField(body, "\"access_token\""u8);
        if (a.IsEmpty) return false;
        var r = JsonField(body, "\"refresh_token\""u8);
        var ttl = JsonNumber(body, "\"expires_in\""u8);
        var now = UnixNow();
        Vault.StoreUtf8(p, a, r, now + (ttl > 0 ? ttl : defaultTtl), now);
        _refreshes[(int)p]++;
        Vault.Save();
        return true;
    }

    /// <summary>만료 임박/401 시 갱신 — YouTube refresh_token · Instagram 장기 토큰 재교환(ig_refresh_token).</summary>
    public async Task<bool> RefreshAsync(SocialPlatform p, CancellationToken ct = default)
    {
        var prev = _phase[(int)p];
        _phase[(int)p] = SocialUploadPhase.Refreshing;
        try
        {
            if (p == SocialPlatform.YouTube)
            {
                if (!Vault.HasRefresh(p)) return false;
                var n = BuildYtRefresh();
                var st = await PostFormAsync(n, ct).ConfigureAwait(false);
                return st == 200 && AcceptToken(p, YouTubeTokenSec);
            }
            if (!Vault.HasAccess(p)) return false;
            BuildIgRefresh();
            var s2 = await SendAsync(ct).ConfigureAwait(false);
            return s2 == 200 && AcceptToken(p, MetaTokenSec);
        }
        finally { _phase[(int)p] = prev == SocialUploadPhase.Refreshing ? SocialUploadPhase.Idle : prev; }
    }

    private int BuildYtRefresh()
    {
        var n = 0; var b = _body.AsSpan();
        GraphBytes.Put(b, ref n, "grant_type=refresh_token&refresh_token="u8);
        var k = Vault.Refresh(SocialPlatform.YouTube).CopyTo(_tok); GraphBytes.PutEncoded(b, ref n, _tok.AsSpan(0, k)); CryptographicOperations.ZeroMemory(_tok.AsSpan(0, k));
        PutClient(b, ref n, SocialPlatform.YouTube);
        PrepareForm(GoogleTokenUrl, n);
        return n;
    }

    private void BuildIgRefresh()
    {
        _call.Method = GraphHttpMethod.Get;
        _call.UrlLen = 0;
        GraphBytes.Put(_call.Url, ref _call.UrlLen, "https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token="u8);
        var t = Vault.Access(SocialPlatform.Instagram).CopyTo(_tok); GraphBytes.Put(_call.Url, ref _call.UrlLen, _tok.AsSpan(0, t)); CryptographicOperations.ZeroMemory(_tok.AsSpan(0, t));
        _call.UrlHasSecret = true; _call.HdrLen = 0; _call.Body = default;
    }

    // ══════════════════════ 헤더 조립(무할당) ══════════════════════
    /// <summary>Authorization 헤더를 송신 버퍼에 직접 복호화 기록(무할당) — 길이 반환. 완료 시 <see cref="GraphCall.TryComplete"/> 가 제로화.</summary>
    public int WriteAuthHeader(SocialPlatform p, Span<byte> hdr, ref int n)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var start = n;
        GraphBytes.Put(hdr, ref n, p == SocialPlatform.YouTube ? "Authorization: Bearer "u8 : "Authorization: OAuth "u8);
        n += Vault.Access(p).CopyTo(hdr[n..]);
        GraphBytes.Put(hdr, ref n, "\r\n"u8);
        LastHeaderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return n - start;
    }

    private void Begin(GraphHttpMethod m, SocialPlatform p)
    {
        _call.Method = m; _call.UrlLen = 0; _call.HdrLen = 0; _call.Body = default; _call.UrlHasSecret = false;
        WriteAuthHeader(p, _call.Hdr, ref _call.HdrLen);
    }

    private async ValueTask<int> SendAsync(CancellationToken ct)
    {
        _call.Send(Transport, ct);
        int status;
        while (!_call.TryComplete(out status)) await Task.Delay(5, ct).ConfigureAwait(false);
        return status;
    }

    // ══════════════════════ 업로드 ══════════════════════
    /// <summary>플랫폼 업로드(만료 선갱신 · 401 1 회 재시도 · 지수 백오프 5xx) — 결과 기록.</summary>
    public async Task<SocialUploadResult> UploadAsync(SocialPlatform p, SegmentedChunkStream media, SocialPostMetadata meta, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var chunks0 = ChunksSent; var ref0 = _refreshes[(int)p];
        try
        {
            if (!Vault.HasAccess(p)) return Fail(p, t0, chunks0, ref0, "계정 미연동");
            if (p == SocialPlatform.Instagram && InstagramUserId.Length == 0) return Fail(p, t0, chunks0, ref0, "IG 사용자 ID 미설정(PROMPTFLOW_SECRET_IG_USER_ID)");
            if (Vault.SecondsLeft(p, UnixNow()) < RefreshSkewSec && !await RefreshAsync(p, ct).ConfigureAwait(false) && Vault.SecondsLeft(p, UnixNow()) <= 0)
                return Fail(p, t0, chunks0, ref0, "토큰 갱신 실패 — 재인증 필요");
            _progress[(int)p] = 0;
            string? id = null; string? err = null;
            for (var attempt = 0; attempt <= MaxRetries && id is null; attempt++)
            {
                var (st, mid) = p == SocialPlatform.YouTube ? await YouTubeAsync(media, meta, ct).ConfigureAwait(false) : await InstagramAsync(media, meta, ct).ConfigureAwait(false);
                RecordStatus(p, st);
                if (mid is not null) { id = mid; break; }
                if (st == 401 && attempt == 0) { if (await RefreshAsync(p, ct).ConfigureAwait(false)) continue; err = "401 · 갱신 실패"; break; }
                if (st is 0 or 429 or >= 500) { err = "HTTP " + st; await Task.Delay(Math.Min(8_000, 250 << attempt), ct).ConfigureAwait(false); continue; }
                err = "HTTP " + st + " " + Snippet(_call.Resp.BodySpan); break;
            }
            if (id is null) return Fail(p, t0, chunks0, ref0, err ?? "업로드 실패");
            _phase[(int)p] = SocialUploadPhase.Done; _progress[(int)p] = 1f;
            _uploads[(int)p]++; _lastId[(int)p] = id; _lastError[(int)p] = null;
            return new SocialUploadResult(p, true, id, (int)(ChunksSent - chunks0), media.Length, Ms(t0), (int)(_refreshes[(int)p] - ref0), null);
        }
        catch (OperationCanceledException) { return Fail(p, t0, chunks0, ref0, "취소"); }
        finally { _gate.Release(); }
    }

    private SocialUploadResult Fail(SocialPlatform p, long t0, long chunks0, long ref0, string why)
    {
        _phase[(int)p] = SocialUploadPhase.Failed; _failures[(int)p]++; _lastError[(int)p] = why;
        return new SocialUploadResult(p, false, null, (int)(ChunksSent - chunks0), 0, Ms(t0), (int)(_refreshes[(int)p] - ref0), why);
    }

    private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static string Snippet(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b[..Math.Min(b.Length, 120)]);

    private int WriteYtJson(Span<byte> b, int n, SocialPostMetadata m)
    {
        GraphBytes.Put(b, ref n, "{\"snippet\":{\"title\":"u8); MultipartBodyWriter.JsonString(b, ref n, m.Title);
        GraphBytes.Put(b, ref n, ",\"description\":"u8); MultipartBodyWriter.JsonString(b, ref n, m.Description);
        GraphBytes.Put(b, ref n, ",\"tags\":["u8);
        for (var i = 0; i < m.TagCount; i++) { if (i > 0) b[n++] = (byte)','; MultipartBodyWriter.JsonString(b, ref n, m.Tags[i].AsSpan().TrimStart('#')); }
        GraphBytes.Put(b, ref n, "],\"categoryId\":\"24\"},\"status\":{\"privacyStatus\":"u8); MultipartBodyWriter.JsonString(b, ref n, m.Privacy);
        GraphBytes.Put(b, ref n, ",\"selfDeclaredMadeForKids\":false}}"u8);
        return n;
    }

    private async ValueTask<(int Status, string? Id)> YouTubeAsync(SegmentedChunkStream media, SocialPostMetadata meta, CancellationToken ct)
    {
        const SocialPlatform P = SocialPlatform.YouTube;
        var size = media.Length;
        if (size <= MultipartMax)
        {
            // ── multipart/related 단일 요청 ──
            _phase[(int)P] = SocialUploadPhase.Uploading;
            BuildYtMultipart(media, meta);
            var st = await SendAsync(ct).ConfigureAwait(false);
            ChunksSent++; BytesSent += size;
            return st is 200 or 201 && TakeId(_call.Resp.BodySpan) ? (st, IdString()) : (st, null);
        }
        // ── 재개 세션 ──
        _phase[(int)P] = SocialUploadPhase.Init;
        BuildYtInit(size, meta);
        var s0 = await SendAsync(ct).ConfigureAwait(false);
        if (s0 != 200 || _call.Resp.LocationLength == 0) return (s0, null);
        _uriLen = _call.Resp.LocationLength; _call.Resp.LocationSpan.CopyTo(_uri);
        _phase[(int)P] = SocialUploadPhase.Uploading;
        long off = 0;
        while (off < size)
        {
            var k = BuildYtChunk(media, off, size);
            var st = await SendAsync(ct).ConfigureAwait(false);
            ChunksSent++;
            if (st == 308) { off = ResumeOffset(off + k); BytesSent += k; _progress[(int)P] = (float)off / size; continue; }
            if (st is 200 or 201) { BytesSent += k; _progress[(int)P] = 1f; return TakeId(_call.Resp.BodySpan) ? (st, IdString()) : (st, null); }
            return (st, null);
        }
        return (0, null);
    }

    private void BuildYtMultipart(SegmentedChunkStream media, SocialPostMetadata meta)
    {
        Begin(GraphHttpMethod.Post, SocialPlatform.YouTube);
        GraphBytes.Put(_call.Url, ref _call.UrlLen, YtMultipartUrl);
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "Content-Type: multipart/related; boundary="u8); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, MultipartBodyWriter.Boundary); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "\r\n"u8);
        var n = 0; var b = _body.AsSpan();
        MultipartBodyWriter.Begin(b, ref n);
        MultipartBodyWriter.PartHeader(b, ref n, "application/json; charset=UTF-8"u8);
        n = WriteYtJson(b, n, meta);
        MultipartBodyWriter.Next(b, ref n);
        MultipartBodyWriter.PartHeader(b, ref n, "video/mp4"u8);
        n += media.ReadAt(0, b.Slice(n, (int)media.Length));
        MultipartBodyWriter.End(b, ref n);
        _call.Body = _body.AsMemory(0, n);
    }

    private void BuildYtInit(long size, SocialPostMetadata meta)
    {
        Begin(GraphHttpMethod.Post, SocialPlatform.YouTube);
        GraphBytes.Put(_call.Url, ref _call.UrlLen, YtResumableUrl);
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "Content-Type: application/json; charset=UTF-8\r\nX-Upload-Content-Type: video/mp4\r\nX-Upload-Content-Length: "u8);
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, size); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "\r\n"u8);
        var jn = WriteYtJson(_body, 0, meta);
        _call.Body = _body.AsMemory(0, jn);
    }

    private int BuildYtChunk(SegmentedChunkStream media, long off, long size)
    {
        var len = (int)Math.Min(_chunk, size - off);
        Begin(GraphHttpMethod.Put, SocialPlatform.YouTube);
        GraphBytes.Put(_call.Url, ref _call.UrlLen, _uri.AsSpan(0, _uriLen));
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "Content-Type: video/mp4\r\nContent-Range: bytes "u8);
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, off); _call.Hdr[_call.HdrLen++] = (byte)'-'; GraphBytes.Put(_call.Hdr, ref _call.HdrLen, off + len - 1); _call.Hdr[_call.HdrLen++] = (byte)'/';
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, size); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "\r\n"u8);
        var k = media.ReadAt(off, _body.AsSpan(0, len));
        _call.Body = _body.AsMemory(0, k);
        return k;
    }

    /// <summary>308 Range: bytes=0-N → 서버 수신 오프셋 재동기(헤더 부재 시 낙관 진행).</summary>
    private long ResumeOffset(long optimistic)
    {
        var r = _call.Resp.RangeSpan; var dash = r.LastIndexOf((byte)'-');
        return dash > 0 && Utf8Parser.TryParse(r[(dash + 1)..], out long last, out _) ? last + 1 : optimistic;
    }

    private async ValueTask<(int Status, string? Id)> InstagramAsync(SegmentedChunkStream media, SocialPostMetadata meta, CancellationToken ct)
    {
        const SocialPlatform P = SocialPlatform.Instagram;
        var size = media.Length;
        // ① 컨테이너(multipart/form-data)
        _phase[(int)P] = SocialUploadPhase.Init;
        BuildIgContainer(meta);
        var s0 = await SendAsync(ct).ConfigureAwait(false);
        if (s0 != 200 || !TakeId(_call.Resp.BodySpan)) return (s0, null);
        var cid = IdString();
        // ② rupload 청크(offset · file_size)
        _phase[(int)P] = SocialUploadPhase.Uploading;
        long off = 0;
        while (off < size)
        {
            var k = BuildIgChunk(media, off, size);
            var st = await SendAsync(ct).ConfigureAwait(false);
            ChunksSent++;
            if (st != 200) return (st, null);
            off += k; BytesSent += k; _progress[(int)P] = (float)off / size;
        }
        // ③ 처리 상태 폴링
        _phase[(int)P] = SocialUploadPhase.Processing;
        var ok = false;
        for (var i = 0; i < MaxPolls && !ok; i++)
        {
            BuildIgGraph(GraphHttpMethod.Get, cid, "?fields=status_code");
            var st = await SendAsync(ct).ConfigureAwait(false);
            if (st != 200) return (st, null);
            var sc = StatusCode();
            if (sc == 1) ok = true;
            else if (sc < 0) return (422, null);
            else await Task.Delay(Transport is HttpGraphTransport ? 3_000 : 1, ct).ConfigureAwait(false);
        }
        if (!ok) return (504, null);
        // ④ 게시
        _phase[(int)P] = SocialUploadPhase.Publishing;
        BuildIgGraph(GraphHttpMethod.Post, InstagramUserId, "/media_publish?creation_id=" + cid);
        var sp = await SendAsync(ct).ConfigureAwait(false);
        return sp == 200 && TakeId(_call.Resp.BodySpan) ? (sp, IdString()) : (sp, null);
    }

    private int StatusCode()
    {
        var sc = JsonField(_call.Resp.BodySpan, "\"status_code\""u8);
        return sc.SequenceEqual("FINISHED"u8) ? 1 : sc.SequenceEqual("ERROR"u8) || sc.SequenceEqual("EXPIRED"u8) ? -1 : 0;
    }

    private void BuildIgGraph(GraphHttpMethod m, string node, string suffix)
    {
        Begin(m, SocialPlatform.Instagram);
        GraphBytes.Put(_call.Url, ref _call.UrlLen, GraphBase);
        _call.UrlLen += Encoding.ASCII.GetBytes(node, _call.Url.AsSpan(_call.UrlLen));
        _call.UrlLen += Encoding.ASCII.GetBytes(suffix, _call.Url.AsSpan(_call.UrlLen));
    }

    private void BuildIgContainer(SocialPostMetadata meta)
    {
        BuildIgGraph(GraphHttpMethod.Post, InstagramUserId, "/media");
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "Content-Type: multipart/form-data; boundary="u8); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, MultipartBodyWriter.Boundary); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "\r\n"u8);
        var n = 0; var b = _body.AsSpan();
        MultipartBodyWriter.Begin(b, ref n);
        MultipartBodyWriter.PartHeader(b, ref n, "text/plain"u8, "media_type"u8); GraphBytes.Put(b, ref n, "REELS"u8);
        MultipartBodyWriter.Next(b, ref n);
        MultipartBodyWriter.PartHeader(b, ref n, "text/plain"u8, "upload_type"u8); GraphBytes.Put(b, ref n, "resumable"u8);
        MultipartBodyWriter.Next(b, ref n);
        MultipartBodyWriter.PartHeader(b, ref n, "text/plain; charset=UTF-8"u8, "caption"u8);
        n += Encoding.UTF8.GetBytes(meta.Title, b[n..]); b[n++] = (byte)'\n'; b[n++] = (byte)'\n';
        n += Encoding.UTF8.GetBytes(meta.Description, b[n..]);
        for (var i = 0; i < meta.TagCount; i++) { b[n++] = (byte)' '; n += Encoding.UTF8.GetBytes(meta.Tags[i], b[n..]); }
        MultipartBodyWriter.Next(b, ref n);
        MultipartBodyWriter.PartHeader(b, ref n, "text/plain"u8, "share_to_feed"u8); GraphBytes.Put(b, ref n, "true"u8);
        MultipartBodyWriter.End(b, ref n);
        _call.Body = _body.AsMemory(0, n);
    }

    private int BuildIgChunk(SegmentedChunkStream media, long off, long size)
    {
        var len = (int)Math.Min(_chunk, size - off);
        Begin(GraphHttpMethod.Post, SocialPlatform.Instagram);
        GraphBytes.Put(_call.Url, ref _call.UrlLen, RuploadBase); GraphBytes.Put(_call.Url, ref _call.UrlLen, _id.AsSpan(0, _idLen));
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "offset: "u8); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, off);
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "\r\nfile_size: "u8); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, size); GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "\r\nContent-Type: application/octet-stream\r\n"u8);
        var k = media.ReadAt(off, _body.AsSpan(0, len));
        _call.Body = _body.AsMemory(0, k);
        return k;
    }

    private bool TakeId(ReadOnlySpan<byte> body)
    {
        var v = JsonField(body, "\"id\""u8);
        if (v.IsEmpty || v.Length > _id.Length) return false;
        v.CopyTo(_id); _idLen = v.Length;
        return true;
    }

    private string IdString() => Encoding.ASCII.GetString(_id, 0, _idLen);

    /// <summary>평탄 JSON 문자열 필드 값(무할당 · 이스케이프 미해석 — 토큰/ID 용).</summary>
    public static ReadOnlySpan<byte> JsonField(ReadOnlySpan<byte> json, ReadOnlySpan<byte> quotedKey)
    {
        var i = json.IndexOf(quotedKey);
        if (i < 0) return default;
        var s = json[(i + quotedKey.Length)..];
        var c = s.IndexOf((byte)':'); if (c < 0) return default;
        s = s[(c + 1)..].TrimStart(" \t\r\n"u8);
        if (s.IsEmpty || s[0] != (byte)'"') return default;
        s = s[1..];
        var e = s.IndexOf((byte)'"');
        return e < 0 ? default : s[..e];
    }

    public static long JsonNumber(ReadOnlySpan<byte> json, ReadOnlySpan<byte> quotedKey)
    {
        var i = json.IndexOf(quotedKey);
        if (i < 0) return 0;
        var s = json[(i + quotedKey.Length)..];
        var c = s.IndexOf((byte)':'); if (c < 0) return 0;
        s = s[(c + 1)..].TrimStart(" \t\r\n\""u8);
        return Utf8Parser.TryParse(s, out long v, out _) ? v : 0;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_tok); CryptographicOperations.ZeroMemory(_tok2); CryptographicOperations.ZeroMemory(_body); CryptographicOperations.ZeroMemory(_client);
        _gate.Dispose();
    }
}

/// <summary>
/// v17.71.0 YouTube Data API v3 · Graph API · OAuth2 토큰 엔드포인트 시뮬레이터(동기 완료 = 무할당 경로 · 규격 검증 · 회귀/데모).
///   ▸ Bearer/OAuth 토큰 일치 · 만료(시계 기준) → 401 · refresh_token/ig_refresh_token → 신규 토큰 발급(회전).
///   ▸ multipart/related: 경계 · JSON 파트(title ≤ 100 · #Shorts 권장) · video/mp4 파트 FNV 일치. 재개: Location · Content-Range 연속성 · 308 Range.
///   ▸ IG: multipart/form-data media_type=REELS · caption ≤ 2200 · 해시태그 ≤ 30 → 컨테이너 · rupload offset 연속 · 상태 폴링 IN_PROGRESS → FINISHED · media_publish.
/// </summary>
public sealed class SimulatedSocialApiServer : IGraphTransport
{
    private readonly byte[] _ytToken = new byte[64], _igToken = new byte[64], _ytRefresh = new byte[64];
    private int _ytLen, _igLen, _rfLen, _rot, _polls;
    private long _ytExp, _igExp, _resRecv, _resSize, _igRecv, _igSize;
    private uint _resHash, _igHash, _expectHash;
    private bool _igContainer;

    public SimulatedSocialApiServer(Func<long> clock) => Clock = clock;

    public Func<long> Clock { get; set; }
    public long Requests { get; private set; }
    public long Unauthorized { get; private set; }
    public long Refreshes { get; private set; }
    public long MultipartUploads { get; private set; }
    public long ResumableChunks { get; private set; }
    public long IgChunks { get; private set; }
    public long Published { get; private set; }
    public long Rejects { get; private set; }
    public int FailNextChunkWith { get; set; }
    /// <summary>v17.73 업로드 개시 요청 강제 응답(쿼터/레이트 주입) — 플랫폼 · 상태 · 본문 · 남은 횟수.</summary>
    public SocialPlatform ForcePlatform { get; set; }
    public int ForceStatus { get; set; }
    public string ForceBody { get; set; } = "";
    public int ForceCount { get; set; }
    private int Forced(SocialPlatform p, GraphResponseBuffer r)
    {
        if (ForceCount <= 0 || ForcePlatform != p) return 0;
        ForceCount--; var n = Encoding.UTF8.GetBytes(ForceBody, r.Body); r.Length = n; r.RetryAfterSec = ForceStatus == 429 ? 1 : 0;
        return ForceStatus;
    }
    public string? LastTitle { get; private set; }
    public string? LastCaption { get; private set; }
    public int LastTagCount { get; private set; }
    public string? LastReject { get; private set; }
    public int ProcessingPolls { get; set; } = 2;
    /// <summary>업로드 원본 FNV(검증 기대값).</summary>
    public void ExpectMedia(ReadOnlySpan<byte> media) => _expectHash = Fnv(2166136261, media);

    public static uint Fnv(uint h, ReadOnlySpan<byte> s) { for (var i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; } return h; }

    /// <summary>발급 토큰 설정(테스트가 볼트에 같은 값 저장).</summary>
    public void Issue(SocialPlatform p, ReadOnlySpan<byte> access, long expiresAt, ReadOnlySpan<byte> refresh = default)
    {
        if (p == SocialPlatform.YouTube) { access.CopyTo(_ytToken); _ytLen = access.Length; _ytExp = expiresAt; refresh.CopyTo(_ytRefresh); _rfLen = refresh.Length; }
        else { access.CopyTo(_igToken); _igLen = access.Length; _igExp = expiresAt; }
    }

    public ReadOnlySpan<byte> CurrentToken(SocialPlatform p) => p == SocialPlatform.YouTube ? _ytToken.AsSpan(0, _ytLen) : _igToken.AsSpan(0, _igLen);

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
        => new(Handle(method, url.Span, headers.Span, body.Span, resp));

    private int Reject(GraphResponseBuffer r, int st, string why) { Rejects++; LastReject = why; return Body(r, st, "{\"error\":{\"message\":\"rejected\"}}"u8); }

    private static int Body(GraphResponseBuffer r, int st, ReadOnlySpan<byte> json) { json.CopyTo(r.Body); r.Length = json.Length; return st; }

    private bool Authorized(SocialPlatform p, ReadOnlySpan<byte> headers)
    {
        var auth = GraphBytes.Header(headers, "Authorization"u8);
        var tok = p == SocialPlatform.YouTube ? _ytToken.AsSpan(0, _ytLen) : _igToken.AsSpan(0, _igLen);
        var exp = p == SocialPlatform.YouTube ? _ytExp : _igExp;
        ReadOnlySpan<byte> given = default;
        if (auth.StartsWith("Bearer "u8)) given = auth[7..];
        else if (auth.StartsWith("OAuth "u8)) given = auth[6..];
        var ok = !tok.IsEmpty && given.Length == tok.Length && CryptographicOperations.FixedTimeEquals(given, tok) && Clock() < exp;
        if (!ok) Unauthorized++;
        return ok;
    }

    private int Rotate(SocialPlatform p, GraphResponseBuffer r, long ttl)
    {
        _rot++; Refreshes++;
        var n = 0; var b = r.Body.AsSpan();
        var buf = p == SocialPlatform.YouTube ? _ytToken : _igToken;
        var len = 0; GraphBytes.Put(buf, ref len, p == SocialPlatform.YouTube ? "ya29.sim-"u8 : "IGQV.sim-"u8); GraphBytes.Put(buf, ref len, (long)_rot);
        if (p == SocialPlatform.YouTube) { _ytLen = len; _ytExp = Clock() + ttl; } else { _igLen = len; _igExp = Clock() + ttl; }
        GraphBytes.Put(b, ref n, "{\"access_token\":\""u8); GraphBytes.Put(b, ref n, buf.AsSpan(0, len)); GraphBytes.Put(b, ref n, "\",\"expires_in\":"u8); GraphBytes.Put(b, ref n, ttl);
        GraphBytes.Put(b, ref n, ",\"token_type\":\"Bearer\"}"u8);
        r.Length = n;
        return 200;
    }

    private int Handle(GraphHttpMethod m, ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer r)
    {
        Requests++;
        // ── OAuth 토큰 엔드포인트 ──
        if (url.SequenceEqual(SocialMediaApiGatewayEngine.GoogleTokenUrl))
        {
            var i = body.IndexOf("refresh_token="u8);
            if (body.IndexOf("grant_type=refresh_token"u8) < 0 || i < 0) return Reject(r, 400, "grant");
            var v = body[(i + 14)..]; var amp = v.IndexOf((byte)'&'); if (amp >= 0) v = v[..amp];
            Span<byte> dec = stackalloc byte[v.Length]; var dn = PercentDecode(v, dec);
            if (_rfLen == 0 || !dec[..dn].SequenceEqual(_ytRefresh.AsSpan(0, _rfLen))) return Reject(r, 400, "invalid_grant");
            return Rotate(SocialPlatform.YouTube, r, SocialMediaApiGatewayEngine.YouTubeTokenSec);
        }
        if (url.StartsWith("https://graph.instagram.com/refresh_access_token"u8))
        {
            var i = url.IndexOf("access_token="u8);
            var given = i < 0 ? default : url[(i + 13)..];
            if (_igLen == 0 || !given.SequenceEqual(_igToken.AsSpan(0, _igLen))) return Reject(r, 400, "ig_refresh");
            return Rotate(SocialPlatform.Instagram, r, SocialMediaApiGatewayEngine.MetaTokenSec);
        }
        // ── YouTube ──
        if (url.SequenceEqual(SocialMediaApiGatewayEngine.YtMultipartUrl) || url.SequenceEqual(SocialMediaApiGatewayEngine.YtResumableUrl)) { var f = Forced(SocialPlatform.YouTube, r); if (f != 0) return f; }
        if (url.SequenceEqual(SocialMediaApiGatewayEngine.YtMultipartUrl))
        {
            if (!Authorized(SocialPlatform.YouTube, headers)) return Body(r, 401, "{\"error\":{\"code\":401}}"u8);
            var ctype = GraphBytes.Header(headers, "Content-Type"u8);
            if (!ctype.StartsWith("multipart/related; boundary="u8)) return Reject(r, 400, "ctype");
            var bnd = ctype[28..];
            Span<byte> delim = stackalloc byte[bnd.Length + 4]; delim[0] = (byte)'-'; delim[1] = (byte)'-'; bnd.CopyTo(delim[2..]);
            if (!body.StartsWith(delim[..(bnd.Length + 2)])) return Reject(r, 400, "boundary");
            var p1 = body.IndexOf("\r\n\r\n"u8); if (p1 < 0) return Reject(r, 400, "part1");
            var rest = body[(p1 + 4)..];
            var e1 = rest.IndexOf(delim[..(bnd.Length + 2)]); if (e1 < 2) return Reject(r, 400, "part1end");
            var json = rest[..(e1 - 2)];
            if (!CheckYtJson(json)) return Reject(r, 400, LastReject ?? "json");
            var part2 = rest[e1..]; var h2 = part2.IndexOf("\r\n\r\n"u8); if (h2 < 0 || part2[..h2].IndexOf("video/mp4"u8) < 0) return Reject(r, 400, "part2");
            var media = part2[(h2 + 4)..];
            var end = media.LastIndexOf(delim[..(bnd.Length + 2)]); if (end < 2 || !media[(end + bnd.Length + 2)..].StartsWith("--"u8)) return Reject(r, 400, "close");
            media = media[..(end - 2)];
            if (_expectHash != 0 && Fnv(2166136261, media) != _expectHash) return Reject(r, 400, "media hash");
            MultipartUploads++;
            return Body(r, 200, "{\"kind\":\"youtube#video\",\"id\":\"ytSimMulti01\",\"status\":{\"uploadStatus\":\"uploaded\"}}"u8);
        }
        if (url.SequenceEqual(SocialMediaApiGatewayEngine.YtResumableUrl))
        {
            if (!Authorized(SocialPlatform.YouTube, headers)) return Body(r, 401, "{\"error\":{\"code\":401}}"u8);
            if (!CheckYtJson(body)) return Reject(r, 400, LastReject ?? "json");
            if (!Utf8Parser.TryParse(GraphBytes.Header(headers, "X-Upload-Content-Length"u8), out long size, out _) || size <= 0) return Reject(r, 400, "len");
            _resSize = size; _resRecv = 0; _resHash = 2166136261;
            var loc = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=SIMSESSION1"u8;
            loc.CopyTo(r.Location); r.LocationLength = loc.Length; r.Length = 0;
            return 200;
        }
        if (url.StartsWith("https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=SIMSESSION1"u8) && m == GraphHttpMethod.Put)
        {
            if (!Authorized(SocialPlatform.YouTube, headers)) return Body(r, 401, "{\"error\":{\"code\":401}}"u8);
            if (FailNextChunkWith != 0) { var f = FailNextChunkWith; FailNextChunkWith = 0; return Body(r, f, "{}"u8); }
            var cr = GraphBytes.Header(headers, "Content-Range"u8);
            if (!cr.StartsWith("bytes "u8)) return Reject(r, 400, "range");
            var s = cr[6..]; var dash = s.IndexOf((byte)'-'); var slash = s.IndexOf((byte)'/');
            if (dash < 0 || slash < dash || !Utf8Parser.TryParse(s[..dash], out long a, out _) || !Utf8Parser.TryParse(s[(dash + 1)..slash], out long z, out _)) return Reject(r, 400, "range parse");
            if (a != _resRecv || z - a + 1 != body.Length) return Reject(r, 400, "range gap");
            if (z + 1 < _resSize && body.Length % (256 * 1024) != 0) return Reject(r, 400, "chunk 256KiB");
            _resRecv = z + 1; _resHash = Fnv(_resHash, body); ResumableChunks++;
            if (_resRecv < _resSize)
            {
                var n = 0; GraphBytes.Put(r.Range, ref n, "bytes=0-"u8); GraphBytes.Put(r.Range, ref n, _resRecv - 1); r.RangeLength = n; r.Length = 0;
                return 308;
            }
            if (_expectHash != 0 && _resHash != _expectHash) return Reject(r, 400, "media hash");
            return Body(r, 201, "{\"kind\":\"youtube#video\",\"id\":\"ytSimResume01\"}"u8);
        }
        // ── Instagram ──
        if (url.StartsWith(SocialMediaApiGatewayEngine.GraphBase))
        {
            if (!Authorized(SocialPlatform.Instagram, headers)) return Body(r, 401, "{\"error\":{\"code\":190}}"u8);
            var path = url[SocialMediaApiGatewayEngine.GraphBase.Length..];
            if (path.EndsWith("/media"u8) && m == GraphHttpMethod.Post)
            {
                var fi = Forced(SocialPlatform.Instagram, r); if (fi != 0) return fi;
                var ctype = GraphBytes.Header(headers, "Content-Type"u8);
                if (!ctype.StartsWith("multipart/form-data; boundary="u8)) return Reject(r, 400, "ig ctype");
                if (Field(body, "media_type"u8).SequenceEqual("REELS"u8) == false || !Field(body, "upload_type"u8).SequenceEqual("resumable"u8)) return Reject(r, 400, "ig fields");
                var cap = Field(body, "caption"u8);
                var capText = Encoding.UTF8.GetString(cap);
                if (capText.Length > 2200) return Reject(r, 400, "caption > 2200");
                var tags = 0; foreach (var ch in capText) if (ch == '#') tags++;
                if (tags > 30) return Reject(r, 400, "hashtags > 30");
                LastCaption = capText; LastTagCount = tags;
                _igContainer = true; _igRecv = 0; _igHash = 2166136261; _polls = 0;
                return Body(r, 200, "{\"id\":\"17900000000000001\",\"uri\":\"https://rupload.facebook.com/ig-api-upload/v19.0/17900000000000001\"}"u8);
            }
            if (path.StartsWith("17900000000000001?fields=status_code"u8))
            {
                if (!_igContainer || _igRecv < _igSize || _igSize == 0) return Body(r, 200, "{\"status_code\":\"IN_PROGRESS\",\"id\":\"17900000000000001\"}"u8);
                return ++_polls > ProcessingPolls ? Body(r, 200, "{\"status_code\":\"FINISHED\",\"id\":\"17900000000000001\"}"u8) : Body(r, 200, "{\"status_code\":\"IN_PROGRESS\",\"id\":\"17900000000000001\"}"u8);
            }
            if (path.IndexOf("/media_publish?creation_id=17900000000000001"u8) >= 0)
            {
                if (!_igContainer || _polls <= ProcessingPolls) return Reject(r, 400, "not finished");
                if (_expectHash != 0 && _igHash != _expectHash) return Reject(r, 400, "ig media hash");
                _igContainer = false; Published++;
                return Body(r, 200, "{\"id\":\"18000000000000042\"}"u8);
            }
            return Reject(r, 404, "graph path");
        }
        if (url.StartsWith(SocialMediaApiGatewayEngine.RuploadBase))
        {
            if (!Authorized(SocialPlatform.Instagram, headers)) return Body(r, 401, "{\"error\":{\"code\":190}}"u8);
            if (!Utf8Parser.TryParse(GraphBytes.Header(headers, "offset"u8), out long off, out _) || !Utf8Parser.TryParse(GraphBytes.Header(headers, "file_size"u8), out long fs, out _)) return Reject(r, 400, "rupload hdr");
            if (!_igContainer || off != _igRecv) return Reject(r, 400, "rupload offset");
            _igSize = fs; _igRecv += body.Length; _igHash = Fnv(_igHash, body); IgChunks++;
            return Body(r, 200, "{\"success\":true}"u8);
        }
        return Reject(r, 404, "unknown");
    }

    /// <summary>application/x-www-form-urlencoded 값 디코드(%XX · + = 공백).</summary>
    private static int PercentDecode(ReadOnlySpan<byte> s, Span<byte> d)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == (byte)'%' && i + 2 < s.Length && Utf8Parser.TryParse(s.Slice(i + 1, 2), out byte b, out var w, 'X') && w == 2) { d[n++] = b; i += 2; }
            else d[n++] = s[i] == (byte)'+' ? (byte)' ' : s[i];
        }
        return n;
    }

    /// <summary>JSON 문자열 필드 완전 해석(이스케이프 포함 · 서버 판독 전용).</summary>
    private static string JsonStringValue(ReadOnlySpan<byte> json, ReadOnlySpan<byte> quotedKey)
    {
        var i = json.IndexOf(quotedKey); if (i < 0) return "";
        var s = json[(i + quotedKey.Length)..]; var c = s.IndexOf((byte)':'); if (c < 0) return "";
        s = s[(c + 1)..].TrimStart(" \t\r\n"u8); if (s.IsEmpty || s[0] != (byte)'"') return "";
        var e = 1;
        while (e < s.Length && s[e] != (byte)'"') e += s[e] == (byte)'\\' ? 2 : 1;
        try { return System.Text.Json.JsonSerializer.Deserialize(s[..Math.Min(e + 1, s.Length)], PublisherWireJsonContext.Default.String) ?? ""; }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    private bool CheckYtJson(ReadOnlySpan<byte> json)
    {
        var t = JsonStringValue(json, "\"title\""u8);
        if (t.Length == 0 || t.Length > 100) { LastReject = "title length " + t.Length; return false; }
        if (json.IndexOf("\"privacyStatus\""u8) < 0) { LastReject = "privacy"; return false; }
        LastTitle = t;
        return true;
    }

    /// <summary>multipart/form-data 필드 값(테스트 서버 판독).</summary>
    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> body, ReadOnlySpan<byte> name)
    {
        Span<byte> key = stackalloc byte[name.Length + 7];
        "name=\""u8.CopyTo(key); name.CopyTo(key[6..]); key[^1] = (byte)'"';
        var i = body.IndexOf(key); if (i < 0) return default;
        var s = body[i..]; var h = s.IndexOf("\r\n\r\n"u8); if (h < 0) return default;
        s = s[(h + 4)..];
        var e = s.IndexOf("\r\n--"u8);
        return e < 0 ? s : s[..e];
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class PublisherWireJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
