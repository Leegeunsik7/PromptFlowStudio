namespace PromptFlowStudio.Services.GraphBridge;

/// <summary>
/// 오프라인 Meta Graph API 시뮬레이터(동기 완료 · 무할당) — 해시태그 검색 · top_media · 본인 media(insights) · REELS 컨테이너 ·
/// rupload offset 검증/FNV-1a 수신 해시 · status_code 폴링 · media_publish · 토큰 갱신 · X-App-Usage · 서명 웹훅 생성.
/// 장애 주입: 429(코드 4) N 회 · 지정 청크 503 1 회 · 토큰 폐기(190). 실 네트워크 미사용. 토큰은 SHA-256 해시로만 보관·비교.
/// </summary>
public sealed class SimulatedMetaGraphServer : IGraphTransport
{
    public const ulong HashtagId = 17843853986012965, ContainerBase = 17889455560051000, MediaBase = 17900000000000000;
    public const int HourWindow = 512;
    private readonly byte[] _tokenHash = new byte[32], _secret = new byte[64];
    private readonly double[] _callAt = new double[HourWindow];
    private int _secretLen, _callHead, _callCount, _uploadChunk, _pollsLeft;
    private long _received, _fileSize;
    private ulong _hash = 14695981039346656037UL, _container;
    private bool _chunkFailed;

    public SimulatedMetaGraphServer(ReadOnlySpan<byte> token, ReadOnlySpan<byte> appSecret)
    {
        System.Security.Cryptography.SHA256.HashData(token, _tokenHash);
        appSecret.CopyTo(_secret); _secretLen = appSecret.Length;
    }

    public double Now { get; set; }
    public int Inject429 { get; set; }
    public int RetryAfterSec { get; set; }
    public int FailChunkIndex { get; set; } = -1;
    public int ProcessingPolls { get; set; } = 2;
    /// <summary>v17.50 인사이트 모델(미디어 ID → 지표) — 미설정 시 0.</summary>
    public Func<ulong, GraphMediaInsights>? InsightsModel { get; set; }
    public double CallCapacityPerHour { get; set; } = 200;
    public long GraphCalls { get; private set; }
    public long UploadCalls { get; private set; }
    public long Rejected429 { get; private set; }
    public long AuthRejected { get; private set; }
    public long Published { get; private set; }
    public long Refreshed { get; private set; }
    /// <summary>top_media 응답 횟수 — 호출마다 인기 목록 7 건 교체(트렌드 회전 시뮬레이션).</summary>
    public long TopMediaCalls { get; private set; }
    public long ReceivedBytes => _received;
    public ulong ReceivedHash => _hash;
    public int LastUsagePct { get; private set; }
    public void RevokeToken() { _tokenHash[0] ^= 0x5A; }

    private bool TokenMatches(ReadOnlySpan<byte> presented)
    {
        Span<byte> h = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(presented, h);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(h, _tokenHash);
    }

    public static ulong Fnv(ulong h, ReadOnlySpan<byte> s)
    {
        foreach (var b in s) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
        => new(Handle(method, url.Span, headers.Span, body.Span, resp));

    private int Handle(GraphHttpMethod method, ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer resp)
    {
        var b = resp.Body.AsSpan(); var n = 0;
        var upload = GraphBytes.Contains(url, "rupload.facebook.com"u8);
        if (GraphBytes.Contains(url, "refresh_access_token"u8))
        {
            var q = url.IndexOf("access_token="u8);
            if (q < 0 || !TokenMatches(url[(q + 13)..])) return AuthFail(resp);
            Refreshed++;
            GraphBytes.Put(b, ref n, "{\"access_token\":\""u8);
            var t0 = n;
            GraphBytes.Put(b, ref n, "IGSIMREFRESHED"u8); GraphBytes.Put(b, ref n, Refreshed);
            System.Security.Cryptography.SHA256.HashData(b[t0..n], _tokenHash);
            GraphBytes.Put(b, ref n, "\",\"token_type\":\"bearer\",\"expires_in\":5184000}"u8);
            resp.Length = n;
            return 200;
        }
        var auth = GraphBytes.Header(headers, "Authorization"u8);
        var sp = auth.IndexOf((byte)' ');
        if (sp < 0 || !TokenMatches(auth[(sp + 1)..])) return AuthFail(resp);

        if (upload)
        {
            UploadCalls++;
            if (!Utf8Long(GraphBytes.Header(headers, "offset"u8), out var off) || !Utf8Long(GraphBytes.Header(headers, "file_size"u8), out var size)) return Bad(resp, "missing offset/file_size"u8);
            if (_uploadChunk == FailChunkIndex && !_chunkFailed) { _chunkFailed = true; return 503; }
            if (off != _received) return Bad(resp, "offset mismatch"u8);
            _fileSize = size;
            _hash = Fnv(_hash, body);
            _received += body.Length;
            _uploadChunk++;
            if (_received >= _fileSize) _pollsLeft = ProcessingPolls;
            GraphBytes.Put(b, ref n, "{\"success\":true,\"message\":\"Upload successful.\"}"u8);
            resp.Length = n;
            return 200;
        }

        GraphCalls++;
        _callAt[_callHead] = Now; _callHead = (_callHead + 1) % HourWindow;
        if (_callCount < HourWindow) _callCount++;
        var inHour = 0;
        for (var i = 0; i < _callCount; i++) if (Now - _callAt[i] < 3600) inHour++;
        LastUsagePct = (int)Math.Min(100, inHour * 100 / CallCapacityPerHour);
        var u = 0;
        GraphBytes.Put(resp.AppUsage, ref u, "{\"call_count\":"u8); GraphBytes.Put(resp.AppUsage, ref u, LastUsagePct); GraphBytes.Put(resp.AppUsage, ref u, ",\"total_cputime\":1,\"total_time\":1}"u8);
        resp.AppUsageLength = u;
        if (Inject429 > 0)
        {
            Inject429--; Rejected429++;
            resp.RetryAfterSec = RetryAfterSec;
            GraphBytes.Put(b, ref n, "{\"error\":{\"message\":\"(#4) Application request limit reached\",\"type\":\"OAuthException\",\"is_transient\":true,\"code\":4,\"fbtrace_id\":\"AsimTrace\"}}"u8);
            resp.Length = n;
            return 429;
        }

        if (GraphBytes.Contains(url, "/insights?metric="u8))
        {
            var p = url.IndexOf("/insights"u8); var s0 = p - 1;
            while (s0 >= 0 && url[s0] != '/') s0--;
            System.Buffers.Text.Utf8Parser.TryParse(url[(s0 + 1)..p], out ulong mid, out _);
            GraphMediaInsights m = default; m.Clear(mid);
            if (InsightsModel is not null) m = InsightsModel(mid);
            GraphBytes.Put(b, ref n, "{\"data\":["u8);
            Metric(b, ref n, "views"u8, m.Views, false); Metric(b, ref n, "reach"u8, m.Reach, true); Metric(b, ref n, "saved"u8, m.Saved, true);
            Metric(b, ref n, "comments"u8, m.Comments, true); Metric(b, ref n, "shares"u8, m.Shares, true); Metric(b, ref n, "total_interactions"u8, m.Interactions, true);
            Metric(b, ref n, "ig_reels_avg_watch_time"u8, m.AvgWatchMs, true);
            GraphBytes.Put(b, ref n, "]}"u8);
        }
        else if (GraphBytes.Contains(url, "ig_hashtag_search"u8))
        {
            GraphBytes.Put(b, ref n, "{\"data\":[{\"id\":\""u8); GraphBytes.Put(b, ref n, HashtagId); GraphBytes.Put(b, ref n, "\"}]}"u8);
        }
        else if (GraphBytes.Contains(url, "/top_media"u8)) { n = WriteMediaList(b, false); TopMediaCalls++; }
        else if (GraphBytes.Contains(url, "/media_publish"u8))
        {
            Published++;
            GraphBytes.Put(b, ref n, "{\"id\":\""u8); GraphBytes.Put(b, ref n, MediaBase + 900000 + (ulong)Published); GraphBytes.Put(b, ref n, "\"}"u8);
        }
        else if (GraphBytes.Contains(url, "fields=status_code"u8))
        {
            var done = _received > 0 && _received >= _fileSize && _pollsLeft-- <= 0;
            GraphBytes.Put(b, ref n, done ? "{\"status_code\":\"FINISHED\",\"status\":\"Finished: Media has been uploaded and it is ready to be published.\",\"id\":\""u8 : "{\"status_code\":\"IN_PROGRESS\",\"status\":\"In Progress\",\"id\":\""u8);
            GraphBytes.Put(b, ref n, _container); GraphBytes.Put(b, ref n, "\"}"u8);
        }
        else if (method == GraphHttpMethod.Get && GraphBytes.Contains(url, "/media?"u8)) n = WriteMediaList(b, true);
        else if (method == GraphHttpMethod.Post && url.EndsWith("/media"u8))
        {
            if (!GraphBytes.Contains(body, "media_type=REELS"u8) || !GraphBytes.Contains(body, "upload_type=resumable"u8)) return Bad(resp, "media_type/upload_type"u8);
            _container = ContainerBase + (ulong)GraphCalls;
            _received = 0; _fileSize = 0; _uploadChunk = 0; _chunkFailed = false; _hash = 14695981039346656037UL;
            GraphBytes.Put(b, ref n, "{\"id\":\""u8); GraphBytes.Put(b, ref n, _container);
            GraphBytes.Put(b, ref n, "\",\"uri\":\"https:\\/\\/rupload.facebook.com\\/ig-api-upload\\/v21.0\\/"u8); GraphBytes.Put(b, ref n, _container); GraphBytes.Put(b, ref n, "\"}"u8);
        }
        else return Bad(resp, "unknown endpoint"u8);
        resp.Length = n;
        return 200;
    }

    private int WriteMediaList(Span<byte> b, bool own)
    {
        var n = 0;
        GraphBytes.Put(b, ref n, "{\"data\":["u8);
        for (var i = 0; i < 25; i++)
        {
            if (i > 0) b[n++] = (byte)',';
            var id = MediaBase + (own ? 50000UL : 0UL) + (ulong)i + (own ? 0UL : 7UL * (ulong)TopMediaCalls);
            var reel = own || i % 3 != 2;
            var likes = (long)(own ? 40 + 7 * i : 500 + (i * 7919 % 9000)) + GraphCalls;
            GraphBytes.Put(b, ref n, "{\"id\":\""u8); GraphBytes.Put(b, ref n, id);
            GraphBytes.Put(b, ref n, reel ? "\",\"media_type\":\"VIDEO\",\"media_product_type\":\"REELS\""u8 : "\",\"media_type\":\"IMAGE\",\"media_product_type\":\"FEED\""u8);
            GraphBytes.Put(b, ref n, ",\"like_count\":"u8); GraphBytes.Put(b, ref n, likes);
            GraphBytes.Put(b, ref n, ",\"comments_count\":"u8); GraphBytes.Put(b, ref n, likes / 20);
            GraphBytes.Put(b, ref n, ",\"permalink\":\"https:\\/\\/www.instagram.com\\/reel\\/Sim"u8); GraphBytes.Put(b, ref n, id); GraphBytes.Put(b, ref n, "\\/\""u8);
            GraphBytes.Put(b, ref n, ",\"media_url\":\"https:\\/\\/scontent.cdninstagram.com\\/v\\/t51.sim\\/"u8); GraphBytes.Put(b, ref n, id); GraphBytes.Put(b, ref n, ".mp4?_nc_ht=sim&oh=00_sim\""u8);
            GraphBytes.Put(b, ref n, ",\"timestamp\":\"2026-10-1"u8); GraphBytes.Put(b, ref n, (long)(i % 9)); GraphBytes.Put(b, ref n, "T03:00:00+0000\""u8);
            if (own) { GraphBytes.Put(b, ref n, ",\"insights\":{\"data\":[{\"name\":\"views\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(b, ref n, likes * 31); GraphBytes.Put(b, ref n, "}],\"id\":\"x\"}]}"u8); }
            b[n++] = (byte)'}';
        }
        GraphBytes.Put(b, ref n, own ? "],\"paging\":{\"cursors\":{\"before\":\"QVFIUmSimB\",\"after\":\"QVFIUmSimAfter"u8 : "],\"paging\":{\"cursors\":{\"after\":\"QVFIUmTagAfter"u8);
        GraphBytes.Put(b, ref n, GraphCalls); GraphBytes.Put(b, ref n, "\"}}}"u8);
        return n;
    }

    /// <summary>서명된 웹훅 페이로드 생성 — 본문 길이 반환 · sig 에 <c>sha256=hex</c>(71 B).</summary>
    public int WriteWebhook(Span<byte> body, Span<byte> sig, long seq, bool tamper = false)
    {
        var n = 0;
        GraphBytes.Put(body, ref n, "{\"object\":\"instagram\",\"entry\":[{\"id\":\"17841400000000001\",\"time\":"u8); GraphBytes.Put(body, ref n, 1792000000 + seq);
        GraphBytes.Put(body, ref n, seq % 3 == 2 ? ",\"changes\":[{\"field\":\"mentions\",\"value\":{\"media_id\":\""u8 : ",\"changes\":[{\"field\":\"comments\",\"value\":{\"from\":{\"id\":\"1\",\"username\":\"fan\"},\"media\":{\"id\":\""u8);
        GraphBytes.Put(body, ref n, MediaBase + 50000UL + (ulong)(seq % 25));
        GraphBytes.Put(body, ref n, seq % 3 == 2 ? "\",\"comment_id\":\""u8 : "\",\"media_product_type\":\"REELS\"},\"id\":\""u8);
        GraphBytes.Put(body, ref n, 18000000000000000UL + (ulong)seq);
        GraphBytes.Put(body, ref n, seq % 3 == 2 ? "\"}}]}]}"u8 : "\",\"text\":\"\\ud83d\\udd25 nice\"}}]}]}"u8);
        TokenBucketRateLimitOAuthSecurityGuard.Sign(_secret.AsSpan(0, _secretLen), body[..n], sig);
        if (tamper) body[n - 6] ^= 1;
        return n;
    }

    private static void Metric(Span<byte> b, ref int n, ReadOnlySpan<byte> name, long v, bool comma)
    {
        if (comma) b[n++] = (byte)',';
        GraphBytes.Put(b, ref n, "{\"name\":\""u8); GraphBytes.Put(b, ref n, name); GraphBytes.Put(b, ref n, "\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8);
        GraphBytes.Put(b, ref n, Math.Max(v, 0)); GraphBytes.Put(b, ref n, "}],\"id\":\"x\"}"u8);
    }

    private static bool Utf8Long(ReadOnlySpan<byte> s, out long v) => System.Buffers.Text.Utf8Parser.TryParse(s, out v, out var used) && used == s.Length && s.Length > 0;

    private int AuthFail(GraphResponseBuffer resp)
    {
        AuthRejected++;
        var n = 0;
        GraphBytes.Put(resp.Body, ref n, "{\"error\":{\"message\":\"Error validating access token: Session has expired.\",\"type\":\"OAuthException\",\"code\":190,\"error_subcode\":463}}"u8);
        resp.Length = n;
        return 401;
    }

    private static int Bad(GraphResponseBuffer resp, ReadOnlySpan<byte> why)
    {
        var n = 0;
        GraphBytes.Put(resp.Body, ref n, "{\"error\":{\"message\":\""u8); GraphBytes.Put(resp.Body, ref n, why); GraphBytes.Put(resp.Body, ref n, "\",\"type\":\"OAuthException\",\"code\":100}}"u8);
        resp.Length = n;
        return 400;
    }
}
