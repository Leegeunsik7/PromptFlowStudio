using System.Buffers.Text;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.LivePublish;

namespace PromptFlowStudio.Services.PlatformVisibility;

/// <summary>
/// [PromptFlow Studio v17.94.0] 🧪 YouTube Data/Analytics API · Instagram Graph API 계약 시뮬레이터(오프라인 결정적 폴백 · 동기 완료 · 무할당).
///   ▸ 실 엔드포인트와 같은 URL · 메서드 · Bearer 헤더 · JSON 응답 스키마를 재현 — 게이트웨이/폴러는 전송 계층만 바꿔 동일 코드 경로로 동작.
///   ▸ 영상 레코드 고정 64 슬롯(상태 · 우세/열세 프로필 · 발행 시각) — 통계는 경과 분 기반 결정적 곡선.
///   ▸ 장애 주입(1 회 소모): 429 rateLimitExceeded(Graph = 400 · code 4) · 403 quotaExceeded · 임의 상태.
/// </summary>
public sealed class PlatformApiContractSimulator : IGraphTransport
{
    public const int Capacity = 64;

    private readonly byte[][] _id = new byte[Capacity][];
    private readonly int[] _idLen = new int[Capacity];
    private readonly LiveProvider[] _prov = new LiveProvider[Capacity];
    private readonly VideoVisibility[] _vis = new VideoVisibility[Capacity];
    private readonly bool[] _strong = new bool[Capacity], _comments = new bool[Capacity];
    private readonly double[] _published = new double[Capacity];
    private readonly Func<double> _clock;
    private int _count, _rateNext, _quotaNext, _statusNext, _status, _retryAfter;

    public PlatformApiContractSimulator(Func<double> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        for (var i = 0; i < Capacity; i++) _id[i] = new byte[PlatformVideoBinding.IdCap];
    }

    public long Requests { get; private set; }
    public long Updates { get; private set; }
    public long Deletes { get; private set; }
    public long Lists { get; private set; }
    public long Insights { get; private set; }
    public long Unauthorized { get; private set; }
    public long InjectedFaults { get; private set; }
    public int Count => _count;

    public void InjectRateLimit(int count, int retryAfterSec = 0) { _rateNext = Math.Max(0, count); _retryAfter = Math.Max(0, retryAfterSec); }
    public void InjectQuotaExceeded(int count) => _quotaNext = Math.Max(0, count);
    public void InjectStatus(int count, int status) { _statusNext = Math.Max(0, count); _status = status; }

    /// <summary>영상 등록(발행) — 같은 ID 는 재설정. 반환 = 레코드 인덱스(-1 = 용량 초과/스키마 위반).</summary>
    public int Seed(LiveProvider p, string id, double publishedAt, bool strong)
    {
        if (!PlatformJson.IsValidId(id)) return -1;
        Span<byte> tmp = stackalloc byte[PlatformVideoBinding.IdCap];
        var n = System.Text.Encoding.ASCII.GetBytes(id, tmp);
        var i = Find(p, tmp[..n]);
        if (i < 0) { if (_count >= Capacity) return -1; i = _count++; }
        tmp[..n].CopyTo(_id[i]); _idLen[i] = n;
        _prov[i] = p; _vis[i] = VideoVisibility.Public; _strong[i] = strong; _comments[i] = true; _published[i] = publishedAt;
        return i;
    }

    public VideoVisibility StateOf(LiveProvider p, string id)
    {
        Span<byte> tmp = stackalloc byte[PlatformVideoBinding.IdCap];
        if (id.Length > tmp.Length) return VideoVisibility.Unknown;
        var i = Find(p, tmp[..System.Text.Encoding.ASCII.GetBytes(id, tmp)]);
        return i < 0 ? VideoVisibility.Unknown : _vis[i];
    }

    public void Reset() { _count = 0; _rateNext = _quotaNext = _statusNext = 0; }

    private int Find(LiveProvider p, ReadOnlySpan<byte> id)
    {
        for (var i = 0; i < _count; i++) if (_prov[i] == p && _id[i].AsSpan(0, _idLen[i]).SequenceEqual(id)) return i;
        return -1;
    }

    /// <summary>결정적 통계 곡선(경과 분 m) — 우세: 14m + 0.12m² · 열세: 0.55m.</summary>
    public void Stats(int i, out long views, out long likes, out long comments, out long shares, out double avgViewPct)
    {
        var m = Math.Max(0, (_clock() - _published[i]) / 60.0);
        var s = _strong[i];
        views = s ? (long)(m * 14 + m * m * 0.12) : (long)(m * 0.55);
        likes = views / (s ? 12 : 60);
        comments = views / (s ? 80 : 400);
        shares = views / (s ? 22 : 160);
        avgViewPct = s ? 58 + Math.Sin(m * 0.21) * 3 : 27 + Math.Sin(m * 0.17) * 2;
    }

    // ══════════════════════ IGraphTransport(동기 완료) ══════════════════════

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
        => new(Handle(method, url.Span, headers.Span, body.Span, resp));

    private int Handle(GraphHttpMethod m, ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer r)
    {
        Requests++;
        var o = r.Body.AsSpan(); var n = 0;
        var graph = url.StartsWith(ProductionPlatformVisibilityGatewayEngine.GraphBaseUrl);
        var auth = GraphBytes.Header(headers, "Authorization"u8);
        if (auth.Length <= 7 || !auth.StartsWith("Bearer "u8))
        {
            Unauthorized++;
            GraphBytes.Put(o, ref n, graph ? "{\"error\":{\"message\":\"Invalid OAuth access token.\",\"type\":\"OAuthException\",\"code\":190}}"u8 : "{\"error\":{\"code\":401,\"errors\":[{\"reason\":\"authError\"}]}}"u8);
            r.Length = n; return 401;
        }
        if (_quotaNext > 0)
        {
            _quotaNext--; InjectedFaults++;
            GraphBytes.Put(o, ref n, "{\"error\":{\"code\":403,\"errors\":[{\"domain\":\"youtube.quota\",\"reason\":\"quotaExceeded\"}]}}"u8);
            r.Length = n; return 403;
        }
        if (_rateNext > 0)
        {
            _rateNext--; InjectedFaults++;
            r.RetryAfterSec = _retryAfter;
            GraphBytes.Put(o, ref n, graph ? "{\"error\":{\"message\":\"Application request limit reached\",\"type\":\"OAuthException\",\"code\":4}}"u8 : "{\"error\":{\"code\":429,\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}"u8);
            r.Length = n; return graph ? 400 : 429;
        }
        if (_statusNext > 0) { _statusNext--; InjectedFaults++; r.Length = 0; return _status; }
        var st = graph ? HandleGraph(m, url[ProductionPlatformVisibilityGatewayEngine.GraphBaseUrl.Length..], o, ref n)
            : url.StartsWith(ProductionPlatformVisibilityGatewayEngine.YouTubeVideosUrl) ? HandleYouTube(m, url, body, o, ref n)
            : url.StartsWith(ProductionPlatformVisibilityGatewayEngine.YouTubeAnalyticsUrl) ? HandleAnalytics(url, o, ref n)
            : 404;
        r.Length = n;
        return st;
    }

    private static ReadOnlySpan<byte> Query(ReadOnlySpan<byte> url, ReadOnlySpan<byte> key)
    {
        var q = url.IndexOf((byte)'?');
        if (q < 0) return default;
        var s = url[(q + 1)..];
        while (!s.IsEmpty)
        {
            var amp = s.IndexOf((byte)'&');
            var kv = amp < 0 ? s : s[..amp];
            s = amp < 0 ? default : s[(amp + 1)..];
            if (kv.Length > key.Length && kv.StartsWith(key) && kv[key.Length] == (byte)'=') return kv[(key.Length + 1)..];
        }
        return default;
    }

    private int HandleYouTube(GraphHttpMethod m, ReadOnlySpan<byte> url, ReadOnlySpan<byte> body, Span<byte> o, ref int n)
    {
        if (m == GraphHttpMethod.Delete)
        {
            var i = Find(LiveProvider.YouTube, Query(url, "id"u8));
            if (i < 0 || _vis[i] == VideoVisibility.Deleted) { GraphBytes.Put(o, ref n, "{\"error\":{\"code\":404,\"errors\":[{\"reason\":\"videoNotFound\"}]}}"u8); return 404; }
            _vis[i] = VideoVisibility.Deleted; Deletes++;
            return 204;
        }
        if (m == GraphHttpMethod.Put)
        {
            if (!Query(url, "part"u8).SequenceEqual("status"u8) || !PlatformJson.TryString(body, "\"id\""u8, out var id) || !PlatformJson.TryString(body, "\"privacyStatus\""u8, out var pv) || PlatformJson.ParsePrivacy(pv) == VideoVisibility.Unknown)
            { GraphBytes.Put(o, ref n, "{\"error\":{\"code\":400,\"errors\":[{\"reason\":\"invalidVideoMetadata\"}]}}"u8); return 400; }
            var i = Find(LiveProvider.YouTube, id);
            if (i < 0 || _vis[i] == VideoVisibility.Deleted) { GraphBytes.Put(o, ref n, "{\"error\":{\"code\":404,\"errors\":[{\"reason\":\"videoNotFound\"}]}}"u8); return 404; }
            _vis[i] = PlatformJson.ParsePrivacy(pv); Updates++;
            GraphBytes.Put(o, ref n, "{\"kind\":\"youtube#video\",\"id\":\""u8); GraphBytes.Put(o, ref n, id);
            GraphBytes.Put(o, ref n, "\",\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\""u8); GraphBytes.Put(o, ref n, pv); GraphBytes.Put(o, ref n, "\"}}"u8);
            return 200;
        }
        // GET videos?part=status|statistics&id=a,b,c — 삭제된 ID 는 items 에서 누락(실 API 동일)
        Lists++;
        var stats = Query(url, "part"u8).SequenceEqual("statistics"u8);
        var ids = Query(url, "id"u8);
        GraphBytes.Put(o, ref n, "{\"kind\":\"youtube#videoListResponse\",\"items\":["u8);
        var first = true;
        while (!ids.IsEmpty)
        {
            var c = ids.IndexOf("%2C"u8); var cl = 3;
            var c2 = ids.IndexOf((byte)',');
            if (c2 >= 0 && (c < 0 || c2 < c)) { c = c2; cl = 1; }
            var id = c < 0 ? ids : ids[..c];
            ids = c < 0 ? default : ids[(c + cl)..];
            var i = Find(LiveProvider.YouTube, id);
            if (i < 0 || _vis[i] == VideoVisibility.Deleted) continue;
            if (!first) GraphBytes.Put(o, ref n, ","u8);
            first = false;
            GraphBytes.Put(o, ref n, "{\"kind\":\"youtube#video\",\"id\":\""u8); GraphBytes.Put(o, ref n, id);
            if (stats)
            {
                Stats(i, out var v, out var l, out var cm, out _, out _);
                GraphBytes.Put(o, ref n, "\",\"statistics\":{\"viewCount\":\""u8); GraphBytes.Put(o, ref n, v);
                GraphBytes.Put(o, ref n, "\",\"likeCount\":\""u8); GraphBytes.Put(o, ref n, l);
                GraphBytes.Put(o, ref n, "\",\"favoriteCount\":\"0\",\"commentCount\":\""u8); GraphBytes.Put(o, ref n, cm);
                GraphBytes.Put(o, ref n, "\"}}"u8);
            }
            else
            {
                GraphBytes.Put(o, ref n, "\",\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\""u8);
                GraphBytes.Put(o, ref n, PlatformJson.PrivacyToken(_vis[i])); GraphBytes.Put(o, ref n, "\"}}"u8);
            }
        }
        GraphBytes.Put(o, ref n, "]}"u8);
        return 200;
    }

    /// <summary>YouTube Analytics v2 reports — <c>filters=video==ID</c> · <c>metrics=averageViewPercentage</c>.</summary>
    private int HandleAnalytics(ReadOnlySpan<byte> url, Span<byte> o, ref int n)
    {
        Lists++;
        var f = Query(url, "filters"u8);
        var eq = f.IndexOf("%3D%3D"u8);
        var id = eq >= 0 ? f[(eq + 6)..] : f.StartsWith("video=="u8) ? f[7..] : default;
        var i = Find(LiveProvider.YouTube, id);
        GraphBytes.Put(o, ref n, "{\"kind\":\"youtubeAnalytics#resultTable\",\"columnHeaders\":[{\"name\":\"averageViewPercentage\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"}],\"rows\":["u8);
        if (i >= 0 && _vis[i] != VideoVisibility.Deleted)
        {
            Stats(i, out _, out _, out _, out _, out var pct);
            GraphBytes.Put(o, ref n, "["u8);
            Utf8Formatter.TryFormat(Math.Round(pct, 2), o[n..], out var w); n += w;
            GraphBytes.Put(o, ref n, "]"u8);
        }
        GraphBytes.Put(o, ref n, "]}"u8);
        return 200;
    }

    private int HandleGraph(GraphHttpMethod m, ReadOnlySpan<byte> path, Span<byte> o, ref int n)
    {
        var end = path.IndexOfAny((byte)'?', (byte)'/');
        var id = end < 0 ? path : path[..end];
        var rest = end < 0 ? default : path[end..];
        var i = Find(LiveProvider.Instagram, id);
        if (i < 0 || _vis[i] == VideoVisibility.Deleted)
        {
            GraphBytes.Put(o, ref n, "{\"error\":{\"message\":\"Unsupported get request. Object does not exist\",\"type\":\"GraphMethodException\",\"code\":100,\"error_subcode\":33}}"u8);
            return 400;
        }
        if (m == GraphHttpMethod.Delete) { _vis[i] = VideoVisibility.Deleted; Deletes++; GraphBytes.Put(o, ref n, "{\"success\":true}"u8); return 200; }
        if (m == GraphHttpMethod.Post)
        {
            var ce = Query(rest, "comment_enabled"u8);
            if (ce.IsEmpty) { GraphBytes.Put(o, ref n, "{\"error\":{\"message\":\"(#100) Invalid parameter\",\"type\":\"OAuthException\",\"code\":100}}"u8); return 400; }
            _comments[i] = ce.SequenceEqual("true"u8);
            _vis[i] = _comments[i] ? VideoVisibility.Public : VideoVisibility.Hidden;
            Updates++;
            GraphBytes.Put(o, ref n, "{\"success\":true}"u8);
            return 200;
        }
        if (rest.StartsWith("/insights"u8))
        {
            Insights++;
            Stats(i, out var v, out var l, out var cm, out var sh, out _);
            GraphBytes.Put(o, ref n, "{\"data\":[{\"name\":\"views\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(o, ref n, v);
            GraphBytes.Put(o, ref n, "}]},{\"name\":\"likes\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(o, ref n, l);
            GraphBytes.Put(o, ref n, "}]},{\"name\":\"comments\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(o, ref n, cm);
            GraphBytes.Put(o, ref n, "}]},{\"name\":\"shares\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(o, ref n, sh);
            GraphBytes.Put(o, ref n, "}]}]}"u8);
            return 200;
        }
        Lists++;
        GraphBytes.Put(o, ref n, "{\"id\":\""u8); GraphBytes.Put(o, ref n, id);
        GraphBytes.Put(o, ref n, "\",\"media_type\":\"VIDEO\",\"permalink\":\"https://www.instagram.com/reel/"u8); GraphBytes.Put(o, ref n, id); GraphBytes.Put(o, ref n, "/\"}"u8);
        return 200;
    }
}
