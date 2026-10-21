using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PromptFlowStudio.Services.GraphBridge;

namespace PromptFlowStudio.Services.LivePublish;

/// <summary>
/// [PromptFlow Studio v17.79.0] 🧪 라이브 배포 계약 시뮬레이터(동기 완료 · 무할당 · 실 네트워크 0) — 자격증명 미설정 CI 의 기본 전송.
/// Google OAuth2(<c>oauth2.googleapis.com/token</c> · authorization_code + PKCE S256 · refresh_token) · YouTube Data API v3
/// (<c>channels?mine=true</c> · resumable 세션 생성 → <c>Content-Range</c> 청크 PUT · 308 Resume Incomplete + <c>Range</c> · <c>bytes */N</c> 오프셋 조회 ·
/// <c>videos?part=status</c> 공개 범위 갱신) · Meta Graph(<c>oauth/access_token</c> 코드 교환 · fb_exchange_token 장기 토큰 · <c>me/accounts</c>)를 구현하고,
/// 릴스 컨테이너/rupload/폴링/media_publish 는 기존 <see cref="SimulatedMetaGraphServer"/>(v17.48)에 위임한다.
/// 토큰·코드는 SHA-256 해시로만 보관 · 비교는 <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// 장애 주입: 청크 전송 중 연결 단절(절반 수신 후 상태 0) · 5xx · 저작권/메타데이터 거부 · 토큰 엔드포인트 장애 · 리프레시 폐기.
/// </summary>
public sealed class LivePublishContractSimulator : IGraphTransport
{
    public const string YtChannelId = "UCpfContractSim000000001", YtChannelTitle = "PromptFlow Contract Channel", IgUsername = "promptflow.contract";
    public const ulong IgUserId = 17841400000000001UL;
    public const long YtAccessTtl = 3599, MetaShortTtl = 3600, MetaLongTtl = 5184000;
    private readonly byte[] _ytId, _ytSecret, _metaId, _metaSecret;
    private readonly byte[] _ytChallenge = new byte[43], _metaChallenge = new byte[43], _ytCode = new byte[32], _metaCode = new byte[32];
    private readonly byte[] _ytAccess = new byte[32], _ytRefresh = new byte[32], _metaToken = new byte[32];
    private readonly byte[] _session = new byte[32], _videoId = new byte[16];
    private int _ytCodeLen, _metaCodeLen, _sessionLen, _videoIdLen, _chunk;
    private bool _ytAccessOk, _ytRefreshOk, _metaOk, _dropped;
    private long _total, _received, _issued;
    private ulong _hash = 14695981039346656037UL;

    public LivePublishContractSimulator(string ytClientId, string ytClientSecret, string metaAppId, string metaAppSecret)
    {
        _ytId = System.Text.Encoding.UTF8.GetBytes(ytClientId); _ytSecret = System.Text.Encoding.UTF8.GetBytes(ytClientSecret);
        _metaId = System.Text.Encoding.UTF8.GetBytes(metaAppId); _metaSecret = System.Text.Encoding.UTF8.GetBytes(metaAppSecret);
        Meta = new SimulatedMetaGraphServer(RandomNumberGenerator.GetBytes(24), _metaSecret) { ProcessingPolls = 1 };
    }

    public SimulatedMetaGraphServer Meta { get; }
    /// <summary>이 인덱스의 청크 전송 중 연결 단절 1 회(절반만 수신 · 상태 0) — 바이트 오프셋 재개 검증.</summary>
    public int DropChunkIndex { get; set; } = -1;
    public int Fail503Next { get; set; }
    public int TokenOutageNext { get; set; }
    public bool RejectCopyright { get; set; }
    public bool RejectMetadata { get; set; }
    public long TokenExchanges { get; private set; }
    public long Refreshes { get; private set; }
    public long ProfileCalls { get; private set; }
    public long PkceFailures { get; private set; }
    public long ClientAuthFailures { get; private set; }
    public long AuthRejected { get; private set; }
    public long InitCalls { get; private set; }
    public long ChunkCalls { get; private set; }
    public long OffsetQueries { get; private set; }
    public long NetworkDrops { get; private set; }
    public long PrivacyUpdates { get; private set; }
    public long YtTotal => _total;
    public long YtReceived => _received;
    public ulong YtHash => _hash;
    /// <summary>0 = 없음 · 1 = private · 2 = unlisted · 3 = public.</summary>
    public int YtPrivacy { get; private set; }
    public static string PrivacyName(int p) => p switch { 1 => "private", 2 => "unlisted", 3 => "public", _ => "—" };
    public void RevokeYouTubeRefresh() => _ytRefreshOk = false;
    public void RevokeMeta() { _metaOk = false; Meta.RevokeToken(); }

    // ══════════════════════ 동의 화면(브라우저 대체 · 비핫패스) ══════════════════════
    /// <summary>인가 URL 검증(client_id · S256 챌린지 · 스코프 · state) 후 동의 → 1 회용 코드 발급(리디렉트 대체).</summary>
    public bool Authorize(string authUrl, out string code, out string state)
    {
        code = state = "";
        var q = authUrl.IndexOf('?');
        if (q < 0) return false;
        string? clientId = null, challenge = null, method = null, scope = null, redirect = null, responseType = null;
        foreach (var kv in authUrl[(q + 1)..].Split('&'))
        {
            var e = kv.IndexOf('=');
            if (e <= 0) continue;
            var v = Uri.UnescapeDataString(kv[(e + 1)..]);
            switch (kv[..e])
            {
                case "client_id": clientId = v; break;
                case "code_challenge": challenge = v; break;
                case "code_challenge_method": method = v; break;
                case "scope": scope = v; break;
                case "redirect_uri": redirect = v; break;
                case "response_type": responseType = v; break;
                case "state": state = v; break;
            }
        }
        var google = authUrl.StartsWith(ProductionOAuth2TokenVaultGateway.GoogleAuthorizeUrl, StringComparison.Ordinal);
        if (clientId is null || challenge is null || challenge.Length != 43 || method != "S256" || responseType != "code" || string.IsNullOrEmpty(redirect) || state.Length < 16 || scope is null) return false;
        if (!System.Text.Encoding.UTF8.GetBytes(clientId).AsSpan().SequenceEqual(google ? _ytId : _metaId)) { ClientAuthFailures++; return false; }
        if (google ? !scope.Contains("youtube.upload", StringComparison.Ordinal) : !scope.Contains("instagram_content_publish", StringComparison.Ordinal)) return false;
        System.Text.Encoding.ASCII.GetBytes(challenge, google ? _ytChallenge : _metaChallenge);
        code = (google ? "4/0SimYt" : "AQSimMeta") + (++_issued).ToString(System.Globalization.CultureInfo.InvariantCulture) + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var cb = google ? _ytCode : _metaCode;
        var n = System.Text.Encoding.ASCII.GetBytes(code, cb);
        if (google) _ytCodeLen = n; else _metaCodeLen = n;
        return true;
    }

    // ══════════════════════ 전송 ══════════════════════
    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
    {
        var u = url.Span;
        if ((GraphBytes.Contains(u, "graph.facebook.com"u8) && !GraphBytes.Contains(u, "/oauth/access_token"u8) && !GraphBytes.Contains(u, "/me/accounts"u8)) || GraphBytes.Contains(u, "rupload.facebook.com"u8))
            return Meta.SendAsync(method, url, headers, body, resp, ct);
        return new(Handle(method, u, headers.Span, body.Span, resp));
    }

    private int Handle(GraphHttpMethod method, ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer resp)
    {
        if (url.StartsWith(ProductionOAuth2TokenVaultGateway.GoogleTokenUrl)) return GoogleToken(body, resp);
        if (GraphBytes.Contains(url, "/oauth/access_token"u8)) return MetaToken(url, resp);
        if (GraphBytes.Contains(url, "/me/accounts"u8)) return MetaAccounts(headers, resp);
        if (url.StartsWith("https://www.googleapis.com/youtube/v3/channels"u8)) return Channels(headers, resp);
        if (url.StartsWith(ProductionResumableVideoUploaderEngine.SessionPrefix))
            return GraphBytes.Contains(url, "upload_id="u8) ? Session(url, headers, body, resp) : Init(headers, body, resp);
        if (url.StartsWith(ProductionResumableVideoUploaderEngine.VideosUrl) && method == GraphHttpMethod.Put) return PrivacyUpdate(headers, body, resp);
        return GoogleError(resp, 404, "notFound"u8, "unknown endpoint"u8);
    }

    // ── Google OAuth2 ──
    private int GoogleToken(ReadOnlySpan<byte> form, GraphResponseBuffer resp)
    {
        if (TokenOutageNext > 0) { TokenOutageNext--; return 503; }
        Span<byte> v = stackalloc byte[512];
        if (!Eq(Decode(Field(form, "client_id"u8), v), _ytId) || !Eq(Decode(Field(form, "client_secret"u8), v), _ytSecret)) { ClientAuthFailures++; return OAuthError(resp, 401, "invalid_client"u8); }
        var grant = Field(form, "grant_type"u8);
        if (grant.SequenceEqual("authorization_code"u8))
        {
            if (_ytCodeLen == 0 || !Eq(Decode(Field(form, "code"u8), v), _ytCode.AsSpan(0, _ytCodeLen))) return OAuthError(resp, 400, "invalid_grant"u8);
            _ytCodeLen = 0;
            if (!PkceOk(Decode(Field(form, "code_verifier"u8), v), _ytChallenge)) { PkceFailures++; return OAuthError(resp, 400, "invalid_grant"u8); }
            TokenExchanges++;
            var b = resp.Body.AsSpan(); var n = 0;
            GraphBytes.Put(b, ref n, "{\"access_token\":\""u8); n += IssueToken(b[n..], "ya29.a0SimYt"u8, _ytAccess); _ytAccessOk = true;
            GraphBytes.Put(b, ref n, "\",\"expires_in\":"u8); GraphBytes.Put(b, ref n, YtAccessTtl);
            GraphBytes.Put(b, ref n, ",\"refresh_token\":\""u8); n += IssueToken(b[n..], "1//0eSimRefresh"u8, _ytRefresh); _ytRefreshOk = true;
            GraphBytes.Put(b, ref n, "\",\"scope\":\"https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly\",\"token_type\":\"Bearer\"}"u8);
            resp.Length = n;
            return 200;
        }
        if (grant.SequenceEqual("refresh_token"u8))
        {
            if (!_ytRefreshOk || !HashEq(Decode(Field(form, "refresh_token"u8), v), _ytRefresh)) return OAuthError(resp, 400, "invalid_grant"u8);
            Refreshes++;
            var b = resp.Body.AsSpan(); var n = 0;
            GraphBytes.Put(b, ref n, "{\"access_token\":\""u8); n += IssueToken(b[n..], "ya29.a0SimYt"u8, _ytAccess); _ytAccessOk = true;
            GraphBytes.Put(b, ref n, "\",\"expires_in\":"u8); GraphBytes.Put(b, ref n, YtAccessTtl);
            GraphBytes.Put(b, ref n, ",\"scope\":\"https://www.googleapis.com/auth/youtube.upload\",\"token_type\":\"Bearer\"}"u8);
            resp.Length = n;
            return 200;
        }
        return OAuthError(resp, 400, "unsupported_grant_type"u8);
    }

    // ── Meta OAuth ──
    private int MetaToken(ReadOnlySpan<byte> url, GraphResponseBuffer resp)
    {
        if (TokenOutageNext > 0) { TokenOutageNext--; return 503; }
        var q = url.IndexOf((byte)'?');
        var query = q < 0 ? default : url[(q + 1)..];
        Span<byte> v = stackalloc byte[512];
        if (!Eq(Decode(Field(query, "client_id"u8), v), _metaId) || !Eq(Decode(Field(query, "client_secret"u8), v), _metaSecret)) { ClientAuthFailures++; return MetaError(resp, 400, 101); }
        long ttl;
        if (Field(query, "grant_type"u8).SequenceEqual("fb_exchange_token"u8))
        {
            if (!_metaOk || !HashEq(Decode(Field(query, "fb_exchange_token"u8), v), _metaToken)) return MetaError(resp, 400, 190);
            Refreshes++; ttl = MetaLongTtl;
        }
        else
        {
            if (_metaCodeLen == 0 || !Eq(Decode(Field(query, "code"u8), v), _metaCode.AsSpan(0, _metaCodeLen))) return MetaError(resp, 400, 100);
            _metaCodeLen = 0;
            if (!PkceOk(Decode(Field(query, "code_verifier"u8), v), _metaChallenge)) { PkceFailures++; return MetaError(resp, 400, 100); }
            TokenExchanges++; ttl = MetaShortTtl;
        }
        var b = resp.Body.AsSpan(); var n = 0;
        GraphBytes.Put(b, ref n, "{\"access_token\":\""u8);
        var t0 = n;
        n += IssueToken(b[n..], ttl == MetaLongTtl ? "EAAGSimLong"u8 : "EAAGSimShort"u8, _metaToken); _metaOk = true;
        Meta.AcceptToken(b[t0..n]);
        GraphBytes.Put(b, ref n, "\",\"token_type\":\"bearer\",\"expires_in\":"u8); GraphBytes.Put(b, ref n, ttl); b[n++] = (byte)'}';
        resp.Length = n;
        return 200;
    }

    private int MetaAccounts(ReadOnlySpan<byte> headers, GraphResponseBuffer resp)
    {
        if (!_metaOk || !HashEq(Bearer(headers), _metaToken)) { AuthRejected++; return MetaError(resp, 401, 190); }
        ProfileCalls++;
        var b = resp.Body.AsSpan(); var n = 0;
        GraphBytes.Put(b, ref n, "{\"data\":[{\"name\":\"PromptFlow Studio Page\",\"id\":\"100000000000777\",\"instagram_business_account\":{\"id\":\""u8);
        GraphBytes.Put(b, ref n, IgUserId); GraphBytes.Put(b, ref n, "\",\"username\":\"promptflow.contract\"}}],\"paging\":{\"cursors\":{\"before\":\"QVFIUmSim\",\"after\":\"QVFIUmSim\"}}}"u8);
        resp.Length = n;
        return 200;
    }

    // ── YouTube Data API v3 ──
    private int Channels(ReadOnlySpan<byte> headers, GraphResponseBuffer resp)
    {
        if (!YtAuth(headers)) return GoogleError(resp, 401, "authError"u8, "Invalid Credentials"u8);
        ProfileCalls++;
        var b = resp.Body.AsSpan(); var n = 0;
        GraphBytes.Put(b, ref n, "{\"kind\":\"youtube#channelListResponse\",\"pageInfo\":{\"totalResults\":1},\"items\":[{\"kind\":\"youtube#channel\",\"id\":\"UCpfContractSim000000001\",\"snippet\":{\"title\":\"PromptFlow Contract Channel\",\"customUrl\":\"@promptflow\"}}]}"u8);
        resp.Length = n;
        return 200;
    }

    private int Init(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer resp)
    {
        if (!YtAuth(headers)) return GoogleError(resp, 401, "authError"u8, "Invalid Credentials"u8);
        InitCalls++;
        if (!Utf8Parser.TryParse(GraphBytes.Header(headers, "X-Upload-Content-Length"u8), out long total, out _) || total <= 0) return GoogleError(resp, 400, "badContent"u8, "X-Upload-Content-Length"u8);
        if (RejectMetadata) return GoogleError(resp, 400, "invalidTitle"u8, "The request metadata specifies an invalid video title."u8);
        var privacy = ReadString(body, "privacyStatus"u8);
        if (!GraphBytes.Contains(body, "\"title\""u8) || privacy == 0) return GoogleError(resp, 400, "invalidVideoMetadata"u8, "snippet.title / status.privacyStatus"u8);
        YtPrivacy = privacy;
        _total = total; _received = 0; _chunk = 0; _dropped = false; _hash = 14695981039346656037UL; _videoIdLen = 0;
        _sessionLen = 0;
        GraphBytes.Put(_session, ref _sessionLen, "SimUp"u8); GraphBytes.Put(_session, ref _sessionLen, InitCalls);
        var l = 0;
        GraphBytes.Put(resp.Location, ref l, ProductionResumableVideoUploaderEngine.InitUrl); GraphBytes.Put(resp.Location, ref l, "&upload_id="u8); GraphBytes.Put(resp.Location, ref l, _session.AsSpan(0, _sessionLen));
        resp.LocationLength = l;
        return 200;
    }

    private int Session(ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer resp)
    {
        var id = url[(url.IndexOf("upload_id="u8) + 10)..];
        if (_sessionLen == 0 || !id.SequenceEqual(_session.AsSpan(0, _sessionLen))) return GoogleError(resp, 404, "notFound"u8, "upload session expired"u8);
        var cr = GraphBytes.Header(headers, "Content-Range"u8);
        if (!cr.StartsWith("bytes "u8)) return GoogleError(resp, 400, "badContent"u8, "Content-Range"u8);
        cr = cr[6..];
        if (cr.StartsWith("*/"u8))
        {
            OffsetQueries++;
            return _videoIdLen > 0 ? WriteVideo(resp) : Incomplete(resp);
        }
        if (Fail503Next > 0) { Fail503Next--; return 503; }
        var dash = cr.IndexOf((byte)'-'); var slash = cr.IndexOf((byte)'/');
        if (dash <= 0 || slash < dash || !Utf8Parser.TryParse(cr[..dash], out long first, out _) || !Utf8Parser.TryParse(cr[(dash + 1)..slash], out long last, out _)
            || !Utf8Parser.TryParse(cr[(slash + 1)..], out long total, out _) || total != _total || last - first + 1 != body.Length) return GoogleError(resp, 400, "badContent"u8, "Content-Range mismatch"u8);
        ChunkCalls++;
        if (first != _received) return Incomplete(resp);
        if (_chunk == DropChunkIndex && !_dropped)
        {
            _dropped = true; NetworkDrops++;
            var half = body.Length / 2;
            _hash = SimulatedMetaGraphServer.Fnv(_hash, body[..half]); _received += half;
            return 0;
        }
        _hash = SimulatedMetaGraphServer.Fnv(_hash, body);
        _received += body.Length; _chunk++;
        if (_received < _total) return Incomplete(resp);
        _videoIdLen = 0;
        GraphBytes.Put(_videoId, ref _videoIdLen, "PfSimV"u8); GraphBytes.Put(_videoId, ref _videoIdLen, InitCalls);
        return WriteVideo(resp);
    }

    private int Incomplete(GraphResponseBuffer resp)
    {
        if (_received > 0)
        {
            var r = 0;
            GraphBytes.Put(resp.Range, ref r, "bytes=0-"u8); GraphBytes.Put(resp.Range, ref r, _received - 1);
            resp.RangeLength = r;
        }
        return 308;
    }

    private int WriteVideo(GraphResponseBuffer resp)
    {
        var b = resp.Body.AsSpan(); var n = 0;
        GraphBytes.Put(b, ref n, "{\"kind\":\"youtube#video\",\"id\":\""u8); GraphBytes.Put(b, ref n, _videoId.AsSpan(0, _videoIdLen));
        GraphBytes.Put(b, ref n, RejectCopyright ? "\",\"status\":{\"uploadStatus\":\"rejected\",\"rejectionReason\":\"copyright\",\"privacyStatus\":\""u8 : "\",\"status\":{\"uploadStatus\":\"uploaded\",\"privacyStatus\":\""u8);
        GraphBytes.Put(b, ref n, YtPrivacy == 3 ? "public"u8 : YtPrivacy == 2 ? "unlisted"u8 : "private"u8);
        GraphBytes.Put(b, ref n, "\",\"selfDeclaredMadeForKids\":false}}"u8);
        resp.Length = n;
        return 200;
    }

    private int PrivacyUpdate(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer resp)
    {
        if (!YtAuth(headers)) return GoogleError(resp, 401, "authError"u8, "Invalid Credentials"u8);
        if (_videoIdLen == 0 || !GraphBytes.Contains(body, _videoId.AsSpan(0, _videoIdLen))) return GoogleError(resp, 404, "videoNotFound"u8, "video not found"u8);
        var p = ReadString(body, "privacyStatus"u8);
        if (p == 0) return GoogleError(resp, 400, "invalidPrivacyStatus"u8, "status.privacyStatus"u8);
        YtPrivacy = p; PrivacyUpdates++;
        return WriteVideo(resp);
    }

    // ══════════════════════ 보조(무할당) ══════════════════════
    private bool YtAuth(ReadOnlySpan<byte> headers)
    {
        var ok = _ytAccessOk && HashEq(Bearer(headers), _ytAccess);
        if (!ok) AuthRejected++;
        return ok;
    }

    private static ReadOnlySpan<byte> Bearer(ReadOnlySpan<byte> headers)
    {
        var a = GraphBytes.Header(headers, "Authorization"u8);
        return a.StartsWith("Bearer "u8) ? a[7..] : default;
    }

    /// <summary>토큰 생성(접두사 + 난수 hex 32) → dst 기록 · 해시 보관 — 기록 길이 반환.</summary>
    private static int IssueToken(Span<byte> dst, ReadOnlySpan<byte> prefix, Span<byte> hash)
    {
        Span<byte> rnd = stackalloc byte[16];
        RandomNumberGenerator.Fill(rnd);
        prefix.CopyTo(dst);
        var n = prefix.Length;
        for (var i = 0; i < 16; i++) { dst[n++] = Hex(rnd[i] >> 4); dst[n++] = Hex(rnd[i] & 15); }
        SHA256.HashData(dst[..n], hash);
        return n;
    }

    private static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'a' + v - 10);

    private static bool HashEq(ReadOnlySpan<byte> presented, ReadOnlySpan<byte> hash)
    {
        if (presented.IsEmpty) return false;
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(presented, h);
        return CryptographicOperations.FixedTimeEquals(h, hash);
    }

    private static bool Eq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    private static bool PkceOk(ReadOnlySpan<byte> verifier, ReadOnlySpan<byte> challenge)
    {
        if (verifier.Length is < 43 or > 128) return false;
        Span<byte> h = stackalloc byte[32]; Span<byte> c = stackalloc byte[44];
        SHA256.HashData(verifier, h);
        return Eq(c[..ProductionOAuth2TokenVaultGateway.Base64Url(h, c)], challenge);
    }

    /// <summary>폼/쿼리 필드 원문(퍼센트 인코딩 유지).</summary>
    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> form, ReadOnlySpan<byte> key)
    {
        while (!form.IsEmpty)
        {
            var amp = form.IndexOf((byte)'&');
            var pair = amp < 0 ? form : form[..amp];
            if (pair.Length > key.Length && pair[key.Length] == (byte)'=' && pair.StartsWith(key)) return pair[(key.Length + 1)..];
            form = amp < 0 ? default : form[(amp + 1)..];
        }
        return default;
    }

    private static ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> s, Span<byte> d)
    {
        var n = 0;
        for (var i = 0; i < s.Length && n < d.Length; i++)
        {
            if (s[i] == (byte)'%' && i + 2 < s.Length && HexVal(s[i + 1]) >= 0 && HexVal(s[i + 2]) >= 0) { d[n++] = (byte)(HexVal(s[i + 1]) << 4 | HexVal(s[i + 2])); i += 2; }
            else d[n++] = s[i] == (byte)'+' ? (byte)' ' : s[i];
        }
        return d[..n];
    }

    private static int HexVal(byte c) => c is >= (byte)'0' and <= (byte)'9' ? c - '0' : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10 : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;

    /// <summary>JSON 문자열 속성 → 공개 범위 코드(1 private · 2 unlisted · 3 public · 0 없음).</summary>
    private static int ReadString(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.ValueTextEquals(name) && r.Read() && r.TokenType == JsonTokenType.String)
                    return r.ValueTextEquals("private"u8) ? 1 : r.ValueTextEquals("unlisted"u8) ? 2 : r.ValueTextEquals("public"u8) ? 3 : 0;
        }
        catch (JsonException) { }
        return 0;
    }

    private static int OAuthError(GraphResponseBuffer resp, int status, ReadOnlySpan<byte> code)
    {
        var n = 0;
        GraphBytes.Put(resp.Body, ref n, "{\"error\":\""u8); GraphBytes.Put(resp.Body, ref n, code); GraphBytes.Put(resp.Body, ref n, "\",\"error_description\":\"contract simulator rejection\"}"u8);
        resp.Length = n;
        return status;
    }

    private static int MetaError(GraphResponseBuffer resp, int status, int code)
    {
        var n = 0;
        GraphBytes.Put(resp.Body, ref n, "{\"error\":{\"message\":\"contract simulator rejection\",\"type\":\"OAuthException\",\"code\":"u8); GraphBytes.Put(resp.Body, ref n, (long)code); GraphBytes.Put(resp.Body, ref n, "}}"u8);
        resp.Length = n;
        return status;
    }

    private static int GoogleError(GraphResponseBuffer resp, int status, ReadOnlySpan<byte> reason, ReadOnlySpan<byte> message)
    {
        var n = 0;
        GraphBytes.Put(resp.Body, ref n, "{\"error\":{\"code\":"u8); GraphBytes.Put(resp.Body, ref n, (long)status); GraphBytes.Put(resp.Body, ref n, ",\"message\":\""u8); GraphBytes.Put(resp.Body, ref n, message);
        GraphBytes.Put(resp.Body, ref n, "\",\"errors\":[{\"domain\":\"youtube\",\"reason\":\""u8); GraphBytes.Put(resp.Body, ref n, reason); GraphBytes.Put(resp.Body, ref n, "\"}]}}"u8);
        resp.Length = n;
        return status;
    }
}
