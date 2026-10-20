using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>v17.62.0 수신 결과(Count 0 = 원격 종료).</summary>
public readonly record struct WireRead(int Count, bool EndOfMessage);

/// <summary>v17.62.0 스트림/메시지 소켓 추상(IRC/TLS · WebSocket · 시뮬레이션).</summary>
public interface ILiveChatWire : IDisposable
{
    /// <summary>WebSocket 처럼 메시지 경계를 보존하면 true, 바이트 스트림(IRC)이면 false.</summary>
    bool MessageFramed { get; }
    ValueTask ConnectAsync(CancellationToken ct);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask<WireRead> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>v17.62.0 YouTube liveChatMessages 폴러(페이지 JSON → 버퍼 · 반환 = 바이트 수).</summary>
public interface IYouTubeChatPoller : IDisposable
{
    ValueTask<int> PollAsync(ReadOnlyMemory<byte> pageToken, Memory<byte> dst, CancellationToken ct);
}

/// <summary>[v17.62.0] Twitch IRC — TCP 6697 + TLS(SslStream · SNI) 또는 평문(로컬 테스트). 연결 10 s 타임아웃.</summary>
public sealed class TcpTlsChatWire : ILiveChatWire
{
    public const int ConnectTimeoutMs = 10_000;
    private readonly string _host;
    private readonly int _port;
    private readonly bool _tls;
    private TcpClient? _tcp;
    private Stream? _s;

    public TcpTlsChatWire(string host = "irc.chat.twitch.tv", int port = 6697, bool tls = true) { _host = host; _port = port; _tls = tls; }

    public bool MessageFramed => false;

    public async ValueTask ConnectAsync(CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(ConnectTimeoutMs);
        _tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 256 * 1024 };
        await _tcp.ConnectAsync(_host, _port, to.Token).ConfigureAwait(false);
        Stream ns = _tcp.GetStream();
        if (_tls)
        {
            var ssl = new SslStream(ns, false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, to.Token).ConfigureAwait(false);
            ns = ssl;
        }
        _s = ns;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _s is null ? ValueTask.CompletedTask : _s.WriteAsync(data, ct);

    public async ValueTask<WireRead> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_s is null) return default;
        var n = await _s.ReadAsync(buffer, ct).ConfigureAwait(false);
        return new WireRead(n, false);
    }

    public void Dispose() { _s?.Dispose(); _tcp?.Dispose(); _s = null; _tcp = null; }
}

/// <summary>[v17.62.0] 치지직 채팅 WSS(ClientWebSocket · 10 s 연결 타임아웃 · 텍스트 메시지 경계).</summary>
public sealed class WebSocketChatWire : ILiveChatWire
{
    private readonly Uri _uri;
    private ClientWebSocket? _ws;

    public WebSocketChatWire(Uri uri) => _uri = uri;
    public bool MessageFramed => true;

    public async ValueTask ConnectAsync(CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TcpTlsChatWire.ConnectTimeoutMs);
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(_uri, to.Token).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _ws is null ? ValueTask.CompletedTask : _ws.SendAsync(data, WebSocketMessageType.Text, true, ct);

    public async ValueTask<WireRead> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return default;
        var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
        return r.MessageType == WebSocketMessageType.Close ? default : new WireRead(r.Count, r.EndOfMessage);
    }

    public void Dispose() { _ws?.Dispose(); _ws = null; }
}

/// <summary>[v17.62.0] YouTube Data API v3 liveChat/messages 폴러(API 키 = 환경변수 · 10 s 타임아웃 · 비성공 상태 → 예외 → 백오프).</summary>
public sealed class YouTubeHttpChatPoller : IYouTubeChatPoller
{
    public const string ApiKeyEnv = "PF_YOUTUBE_API_KEY";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _liveChatId, _key;

    public YouTubeHttpChatPoller(string liveChatId, string apiKey) { _liveChatId = liveChatId; _key = apiKey; }

    public async ValueTask<int> PollAsync(ReadOnlyMemory<byte> pageToken, Memory<byte> dst, CancellationToken ct)
    {
        var url = "https://www.googleapis.com/youtube/v3/liveChat/messages?part=snippet,authorDetails&maxResults=2000&liveChatId=" + Uri.EscapeDataString(_liveChatId) + "&key=" + Uri.EscapeDataString(_key)
            + (pageToken.IsEmpty ? "" : "&pageToken=" + Uri.EscapeDataString(System.Text.Encoding.ASCII.GetString(pageToken.Span)));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException("YouTube " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture), null, resp.StatusCode);
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var n = 0; int k;
        while (n < dst.Length && (k = await s.ReadAsync(dst[n..], ct).ConfigureAwait(false)) > 0) n += k;
        if (n >= dst.Length) throw new InvalidOperationException("YouTube 페이지 버퍼 초과");
        return n;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>videoId → activeLiveChatId(비핫패스 · 스키마 즉시 검증).</summary>
    public static async Task<string?> ResolveLiveChatIdAsync(string videoId, string apiKey, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync("https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id=" + Uri.EscapeDataString(videoId) + "&key=" + Uri.EscapeDataString(apiKey), ct).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array || items.GetArrayLength() == 0) return null;
        return items[0].TryGetProperty("liveStreamingDetails", out var d) && d.TryGetProperty("activeLiveChatId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
    }
}

/// <summary>[v17.62.0] 치지직 채널 → (chatChannelId · 읽기 accessToken) 해석(비핫패스 · 익명 READ · 스키마 즉시 검증).</summary>
public static class ChzzkEndpointResolver
{
    public static readonly Uri ChatEndpoint = new("wss://kr-ss1.chat.naver.com/chat");

    public static async Task<(string ChatChannelId, string AccessToken)?> ResolveAsync(string channelId, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 PromptFlowStudio");
        var live = await http.GetStringAsync("https://api.chzzk.naver.com/polling/v2/channels/" + Uri.EscapeDataString(channelId) + "/live-status", ct).ConfigureAwait(false);
        string? cid;
        using (var doc = System.Text.Json.JsonDocument.Parse(live))
            cid = doc.RootElement.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object && c.TryGetProperty("chatChannelId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null;
        if (string.IsNullOrEmpty(cid)) return null;
        var tok = await http.GetStringAsync("https://comm-api.game.naver.com/nng_main/v1/chats/access-token?chatType=STREAMING&channelId=" + Uri.EscapeDataString(cid), ct).ConfigureAwait(false);
        using var td = System.Text.Json.JsonDocument.Parse(tok);
        var at = td.RootElement.TryGetProperty("content", out var tc) && tc.ValueKind == System.Text.Json.JsonValueKind.Object && tc.TryGetProperty("accessToken", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString() : null;
        return string.IsNullOrEmpty(at) ? null : (cid, at);
    }
}
