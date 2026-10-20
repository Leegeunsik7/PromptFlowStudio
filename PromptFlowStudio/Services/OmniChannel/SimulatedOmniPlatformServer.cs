using System.Buffers.Text;
using PromptFlowStudio.Services.GraphBridge;
namespace PromptFlowStudio.Services.OmniChannel;

/// <summary>
/// 오프라인 YouTube Data API v3 · TikTok Content Posting API v2 · 댓글 엔드포인트 시뮬레이터(동기 완료 · 무할당 · 실 네트워크 미사용).
/// YouTube: 재개 세션(Location) · Content-Range 검증 · 308 Range · 상태 질의(bytes */N) · 메타 검증(#Shorts · 제목 ≤ 100) · commentThreads/comments.insert.
/// TikTok: init(해시태그 ≤ 5 · 커버 ms · 청크 계획 검증 · 분당 6 회 → 429) · PUT 206/201 · status/fetch(처리 2 회 후 완료).
/// Instagram 댓글/답글: graph.facebook.com /{id}/comments · /{id}/replies. 장애 주입: YouTube 청크 503 1 회 · TikTok 429 N 회.
/// </summary>
public sealed class SimulatedOmniPlatformServer : IGraphTransport
{
    private readonly byte[] _ytTok = new byte[32], _ttTok = new byte[32];
    private readonly double[] _ttInit = new double[16];
    private int _ttInitHead, _ytChunkNo, _ttPolls;
    private long _ytReceived, _ytSize, _ttReceived, _ttSize, _ttChunkSize, _ttChunks;
    private ulong _ytHash = 14695981039346656037UL, _ttHash = 14695981039346656037UL;
    private bool _ytFailed;

    public SimulatedOmniPlatformServer(ReadOnlySpan<byte> ytToken, ReadOnlySpan<byte> ttToken)
    {
        System.Security.Cryptography.SHA256.HashData(ytToken, _ytTok);
        System.Security.Cryptography.SHA256.HashData(ttToken, _ttTok);
    }

    public double Now { get; set; }
    public int FailYouTubeChunk { get; set; } = -1;
    public int InjectTikTok429 { get; set; }
    public long YouTubeUploads { get; private set; }
    public long TikTokPublishes { get; private set; }
    public long YouTubeBytes => _ytReceived;
    public long TikTokBytes => _ttReceived;
    public ulong YouTubeHash => _ytHash;
    public ulong TikTokHash => _ttHash;
    public int LastYouTubeTitleChars { get; private set; }
    public bool LastYouTubeHadShorts { get; private set; }
    public int LastTikTokTags { get; private set; }
    public long LastTikTokCoverMs { get; private set; } = -1;
    public long CommentPosts { get; private set; }
    public long ReplyPosts { get; private set; }
    public long StatusQueries { get; private set; }
    public long Rejected { get; private set; }
    /// <summary>YouTube 게시 시각(시뮬레이션 초) — 댓글 목록 성장 기준.</summary>
    public double YouTubePublishedAt { get; set; } = double.MaxValue;

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
        => new(Handle(method, url.Span, headers.Span, body.Span, resp));

    private bool Auth(ReadOnlySpan<byte> headers, byte[] hash)
    {
        var a = GraphBytes.Header(headers, "Authorization"u8);
        var sp = a.IndexOf((byte)' ');
        if (sp < 0) return false;
        Span<byte> h = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(a[(sp + 1)..], h);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(h, hash);
    }

    private int Handle(GraphHttpMethod method, ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer r)
    {
        var b = r.Body.AsSpan(); var n = 0;
        if (GraphBytes.Contains(url, "googleapis.com"u8))
        {
            if (!Auth(headers, _ytTok)) return Err(r, 401, "{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}"u8);
            if (GraphBytes.Contains(url, "uploadType=resumable"u8) && method == GraphHttpMethod.Post)
            {
                if (!Utf8Parser.TryParse(GraphBytes.Header(headers, "X-Upload-Content-Length"u8), out _ytSize, out _)) return Err(r, 400, "{\"error\":{\"code\":400}}"u8);
                LastYouTubeHadShorts = GraphBytes.Contains(body, "#Shorts"u8);
                LastYouTubeTitleChars = TitleChars(body);
                if (LastYouTubeTitleChars > 100 || !LastYouTubeHadShorts || !GraphBytes.Contains(body, "\"categoryId\":\""u8)) return Err(r, 400, "{\"error\":{\"code\":400,\"message\":\"invalidMetadata\"}}"u8);
                _ytReceived = 0; _ytChunkNo = 0; _ytFailed = false; _ytHash = 14695981039346656037UL;
                var l = 0;
                GraphBytes.Put(r.Location, ref l, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=SimYtSession"u8); GraphBytes.Put(r.Location, ref l, (long)YouTubeUploads);
                r.LocationLength = l; r.Length = 0;
                return 200;
            }
            if (GraphBytes.Contains(url, "upload_id=SimYtSession"u8) && method == GraphHttpMethod.Put)
            {
                var cr = GraphBytes.Header(headers, "Content-Range"u8);
                if (cr.StartsWith("bytes */"u8)) { StatusQueries++; return Range308(r); }
                if (_ytChunkNo == FailYouTubeChunk && !_ytFailed) { _ytFailed = true; return 503; }
                if (!ParseRange(cr, out var a0, out var a1, out var tot) || a0 != _ytReceived || tot != _ytSize || a1 - a0 + 1 != body.Length || body.Length % (256 * 1024) != 0 && a1 + 1 != tot) return Err(r, 400, "{\"error\":{\"code\":400,\"message\":\"badRange\"}}"u8);
                _ytHash = SimulatedMetaGraphServer.Fnv(_ytHash, body); _ytReceived += body.Length; _ytChunkNo++;
                if (_ytReceived < _ytSize) return Range308(r);
                YouTubeUploads++;
                GraphBytes.Put(b, ref n, "{\"kind\":\"youtube#video\",\"id\":\"SimShort"u8); GraphBytes.Put(b, ref n, (long)YouTubeUploads); GraphBytes.Put(b, ref n, "\",\"status\":{\"uploadStatus\":\"uploaded\"}}"u8);
                r.Length = n; return 200;
            }
            if (GraphBytes.Contains(url, "/commentThreads?part=snippet&videoId="u8) && method == GraphHttpMethod.Get)
            {
                // 시청자 댓글 목록(게시 후 경과 시간 비례 · 최신순 20 건) — 문구 은행 순환
                var shown = Math.Min((int)(Now - YouTubePublishedAt) / 20, 40);
                GraphBytes.Put(b, ref n, "{\"kind\":\"youtube#commentThreadListResponse\",\"items\":["u8);
                for (var k = Math.Max(shown - 20, 0); k < shown; k++)
                {
                    if (k > Math.Max(shown - 20, 0)) b[n++] = (byte)',';
                    GraphBytes.Put(b, ref n, "{\"id\":\"UgzSim"u8); GraphBytes.Put(b, ref n, (long)k);
                    GraphBytes.Put(b, ref n, "\",\"snippet\":{\"topLevelComment\":{\"id\":\"UgzSim"u8); GraphBytes.Put(b, ref n, (long)k);
                    GraphBytes.Put(b, ref n, "\",\"snippet\":{\"textOriginal\":"u8); GraphBytes.Put(b, ref n, SimPhrases.Json(k));
                    GraphBytes.Put(b, ref n, ",\"authorChannelId\":{\"value\":\"UCsim"u8); GraphBytes.Put(b, ref n, (long)(k % 13)); GraphBytes.Put(b, ref n, "\"}}}}}"u8);
                }
                GraphBytes.Put(b, ref n, "]}"u8);
                r.Length = n; return 200;
            }
            if (GraphBytes.Contains(url, "/commentThreads"u8) || GraphBytes.Contains(url, "/comments?"u8))
            {
                if (GraphBytes.Contains(url, "/comments?"u8)) ReplyPosts++; else CommentPosts++;
                GraphBytes.Put(b, ref n, "{\"kind\":\"youtube#comment\",\"id\":\"SimYtCmt"u8); GraphBytes.Put(b, ref n, CommentPosts + ReplyPosts); GraphBytes.Put(b, ref n, "\"}"u8);
                r.Length = n; return 200;
            }
            return Err(r, 404, "{\"error\":{\"code\":404}}"u8);
        }
        if (GraphBytes.Contains(url, "open.tiktokapis.com"u8))
        {
            if (!Auth(headers, _ttTok)) return Err(r, 401, "{\"error\":{\"code\":\"access_token_invalid\",\"message\":\"\",\"log_id\":\"sim\"}}"u8);
            if (GraphBytes.Contains(url, "/video/init/"u8))
            {
                var k = 0;
                for (var i = 0; i < _ttInit.Length; i++) if (_ttInit[i] > 0 && Now - _ttInit[i] < 60) k++;
                if (InjectTikTok429 > 0 || k >= 6) { if (InjectTikTok429 > 0) InjectTikTok429--; Rejected++; r.RetryAfterSec = 2; return Err(r, 429, "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"\",\"log_id\":\"sim\"}}"u8); }
                _ttInit[_ttInitHead] = Now; _ttInitHead = (_ttInitHead + 1) % _ttInit.Length;
                LastTikTokTags = HashtagsInTitle(body);
                LastTikTokCoverMs = Field(body, "\"video_cover_timestamp_ms\":"u8);
                _ttSize = Field(body, "\"video_size\":"u8); _ttChunkSize = Field(body, "\"chunk_size\":"u8); _ttChunks = Field(body, "\"total_chunk_count\":"u8);
                var okPlan = _ttSize > 0 && _ttChunks >= 1 && (_ttChunks == 1 ? _ttChunkSize == _ttSize : _ttChunkSize >= 5 * 1024 * 1024 && _ttChunkSize <= 64 * 1024 * 1024 && _ttSize / _ttChunkSize == _ttChunks);
                if (LastTikTokTags > 5 || LastTikTokCoverMs < 0 || !okPlan) return Err(r, 400, "{\"error\":{\"code\":\"invalid_params\",\"message\":\"\",\"log_id\":\"sim\"}}"u8);
                _ttReceived = 0; _ttPolls = 0; _ttHash = 14695981039346656037UL;
                GraphBytes.Put(b, ref n, "{\"data\":{\"publish_id\":\"v_pub_file~v2.sim"u8); GraphBytes.Put(b, ref n, TikTokPublishes + 1);
                GraphBytes.Put(b, ref n, "\",\"upload_url\":\"https:\\/\\/open-upload.tiktokapis.com\\/video\\/?upload_id=sim&upload_token=tok\"},\"error\":{\"code\":\"ok\",\"message\":\"\",\"log_id\":\"sim\"}}"u8);
                r.Length = n; return 200;
            }
            if (GraphBytes.Contains(url, "/status/fetch/"u8))
            {
                var done = _ttReceived >= _ttSize && ++_ttPolls > 2;
                if (done && _ttPolls == 3) TikTokPublishes++;
                GraphBytes.Put(b, ref n, done ? "{\"data\":{\"status\":\"PUBLISH_COMPLETE\",\"publicaly_available_post_id\":[7300000000000000001],\"uploaded_bytes\":"u8 : "{\"data\":{\"status\":\"PROCESSING_UPLOAD\",\"uploaded_bytes\":"u8);
                GraphBytes.Put(b, ref n, _ttReceived); GraphBytes.Put(b, ref n, "},\"error\":{\"code\":\"ok\",\"message\":\"\",\"log_id\":\"sim\"}}"u8);
                r.Length = n; return 200;
            }
            return Err(r, 404, "{\"error\":{\"code\":\"not_found\"}}"u8);
        }
        if (GraphBytes.Contains(url, "open-upload.tiktokapis.com"u8) && method == GraphHttpMethod.Put)
        {
            var cr = GraphBytes.Header(headers, "Content-Range"u8);
            if (!ParseRange(cr, out var a0, out var a1, out var tot) || a0 != _ttReceived || tot != _ttSize || a1 - a0 + 1 != body.Length) return Err(r, 400, "{\"error\":{\"code\":\"invalid_range\"}}"u8);
            var last = a1 + 1 == tot;
            if (!last && body.Length != _ttChunkSize) return Err(r, 400, "{\"error\":{\"code\":\"invalid_chunk\"}}"u8);
            _ttHash = SimulatedMetaGraphServer.Fnv(_ttHash, body); _ttReceived += body.Length;
            r.Length = 0;
            return last ? 201 : 206;
        }
        if (GraphBytes.Contains(url, "graph.facebook.com"u8) && (GraphBytes.Contains(url, "/replies"u8) || GraphBytes.Contains(url, "/comments"u8)))
        {
            if (GraphBytes.Contains(url, "/replies"u8)) ReplyPosts++; else CommentPosts++;
            GraphBytes.Put(b, ref n, "{\"id\":\"1790000000000"u8); GraphBytes.Put(b, ref n, CommentPosts + ReplyPosts); GraphBytes.Put(b, ref n, "\"}"u8);
            r.Length = n; return 200;
        }
        return Err(r, 404, "{}"u8);
    }

    private int Range308(GraphResponseBuffer r)
    {
        var l = 0;
        if (_ytReceived > 0) { GraphBytes.Put(r.Range, ref l, "bytes=0-"u8); GraphBytes.Put(r.Range, ref l, _ytReceived - 1); }
        r.RangeLength = l; r.Length = 0;
        return 308;
    }

    private static int Err(GraphResponseBuffer r, int st, ReadOnlySpan<byte> json) { json.CopyTo(r.Body); r.Length = json.Length; return st; }

    private static bool ParseRange(ReadOnlySpan<byte> cr, out long a0, out long a1, out long tot)
    {
        a0 = a1 = tot = -1;
        if (!cr.StartsWith("bytes "u8)) return false;
        var s = cr[6..]; var d = s.IndexOf((byte)'-'); var sl = s.IndexOf((byte)'/');
        return d > 0 && sl > d && Utf8Parser.TryParse(s[..d], out a0, out _) && Utf8Parser.TryParse(s[(d + 1)..sl], out a1, out _) && Utf8Parser.TryParse(s[(sl + 1)..], out tot, out _);
    }

    private static long Field(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key)
    {
        var i = json.IndexOf(key);
        return i >= 0 && Utf8Parser.TryParse(json[(i + key.Length)..], out long v, out _) ? v : -1;
    }

    /// <summary>"title":"…" 의 UTF-16 문자 수(이스케이프 1 문자 처리).</summary>
    private static int TitleChars(ReadOnlySpan<byte> json)
    {
        var i = json.IndexOf("\"title\":\""u8);
        if (i < 0) return 0;
        var s = json[(i + 9)..]; var chars = 0;
        for (var k = 0; k < s.Length; k++)
        {
            var c = s[k];
            if (c == '"') break;
            if (c == '\\') { k++; chars++; continue; }
            if (c < 0x80 || (c & 0xC0) == 0xC0) chars += (c & 0xF8) == 0xF0 ? 2 : 1;
        }
        return chars;
    }

    private static int HashtagsInTitle(ReadOnlySpan<byte> json)
    {
        var i = json.IndexOf("\"title\":\""u8);
        if (i < 0) return 0;
        var s = json[(i + 9)..]; var c = 0;
        for (var k = 0; k + 1 < s.Length && s[k] != '"'; k++) if (s[k] == '#' && s[k + 1] != ' ' && s[k + 1] != '"') c++;
        return c;
    }
}

/// <summary>시뮬레이션 시청자 댓글 문구 은행(4 개 언어 · 5 의도 · JSON 사전 인코딩).</summary>
public static class SimPhrases
{
    public static readonly string[] Texts =
    {
        "와 이거 진짜 대박이다 🔥🔥", "이거 어떻게 만든 거예요?", "이거 가짜 아닌가요? 별로인데", "Who else is watching this at 3am",
        "This is amazing, love the lighting!", "How did you make the transition?", "fake and boring tbh", "follow me for free money http://spam.example",
        "すごい！最高です", "これどうやって撮ったの？", "¡Increíble! me encanta 😍", "¿Cómo hiciste ese efecto?",
        "음악 제목이 뭔가요?", "check my channel sub4sub!!!!!!", "레전드 영상 저장각 📌", "Where was this filmed?",
        "つまらない、やらせでしょ", "nice", "왜 3초에 화면이 바뀌어요?", "What camera do you use?",
    };
    private static readonly byte[][] Enc = Build();
    private static byte[][] Build()
    {
        var r = new byte[Texts.Length][];
        for (var i = 0; i < Texts.Length; i++)
        {
            var buf = new byte[1024]; var n = 0;
            OmniMetadata.Str(buf, ref n, Texts[i]);
            r[i] = buf.AsSpan(0, n).ToArray();
        }
        return r;
    }
    public static ReadOnlySpan<byte> Json(int k) => Enc[k % Enc.Length];
}
