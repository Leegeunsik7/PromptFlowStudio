using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Flow;

namespace PromptFlowStudio.Services.Integration;

/// <summary>v16.21.0 라이브 동기화 메시지 종류.</summary>
public static class LiveSyncTypes
{
    public const string Cursor = "cursor", Stroke = "stroke", Mask = "mask", Graph = "graph", NodeState = "node", Keyframes = "keys", Hello = "hello";

    /// <summary>입장 스냅숏으로 캐시하는 상태형 메시지(최신 1건 유지).</summary>
    public static bool IsStateful(string t) => t is Mask or Graph or Keyframes;

    public static bool IsKnown(string? t) => t is Cursor or Stroke or Mask or Graph or NodeState or Keyframes or Hello;
}

/// <summary>v16.21.0 동기화 봉투 — 종류 · 시퀀스 · 발신자 · 시각(ms) · 페이로드 JSON 문자열.</summary>
public sealed record LiveSyncEnvelope(string T, long Seq, string Src, long At, string Data);

/// <summary>v16.21.0 브릿지 상태.</summary>
public enum LiveBridgeState { Idle, Listening, Failed }

/// <summary>
/// [PromptFlowStudio] v16.21.0 📡 하이브리드 라이브 세션 동기화 브릿지 — 데스크톱 코어 ➔ 모바일/웹 클라이언트 실시간 캔버스 좌표·마스크·노드 상태 브로드캐스트(순수 BCL WebSocket).
///   ▸ <see cref="HttpListener"/> <c>ws://localhost:{port}/pfs-live/ws?token=…</c> — 세션 토큰(32B 난수 · base64url) <see cref="CryptographicOperations.FixedTimeEquals"/> 검증 · 클라이언트 상한 16.
///   ▸ 브로드캐스트: 봉투를 1회 직렬화한 UTF-8 바이트를 전 클라이언트가 공유(클라이언트당 재직렬화 0) · 클라이언트별 bounded Channel(256 · DropOldest) 백프레셔.
///   ▸ 커서는 33ms(≈30Hz) 이내 재발행 드롭(카운트) · 상태형(마스크/그래프/키프레임)은 최신 1건 캐시 → 입장 즉시 스냅숏 전송(수렴).
///   ▸ 수신: 64KB 상한 · 종류 화이트리스트 · <see cref="OnRemoteMessage"/>(예외 격리) + 발신자 제외 재중계.
///   ▸ 실패(포트 점유·URL ACL)는 Graceful(State=Failed) · Dispose 시 CTS/리스너/세션 정리. SignalR(외부 NuGet) 대신 RFC6455 BCL 구현.
/// </summary>
public sealed class LiveSessionSyncBridge : IDisposable
{
    public const int DefaultPort = 8791;
    public const int MaxClients = 16, OutboundCapacity = 256, MaxInboundBytes = 64 * 1024;
    public const long CursorIntervalMs = 33;

    private sealed class Client
    {
        public required string Id { get; init; }
        public required WebSocket Socket { get; init; }
        public required Channel<byte[]> Outbound { get; init; }
        public required string Label { get; init; }
    }

    private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _snapshot = new(StringComparer.Ordinal);
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _seq, _sent, _received, _dropped, _rejected, _lastCursorTicks;

    public LiveSessionSyncBridge() => Token = Base64Url(_token);

    public event Action? OnChange;
    /// <summary>원격 클라이언트 메시지(수신 스레드) — 구독자는 UI 마샬링 필요.</summary>
    public event Action<LiveSyncEnvelope>? OnRemoteMessage;

    public string SiteId { get; } = "core-" + Guid.NewGuid().ToString("N")[..6];
    public string Token { get; }
    public LiveBridgeState State { get; private set; }
    public string? Prefix { get; private set; }
    public string? LastError { get; private set; }
    public int Port { get; private set; }
    public bool IsListening => State == LiveBridgeState.Listening;
    public int ClientCount => _clients.Count;
    public long Sent => Interlocked.Read(ref _sent);
    public long Received => Interlocked.Read(ref _received);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long Sequence => Interlocked.Read(ref _seq);

    /// <summary>클라이언트 접속 URL(토큰 포함).</summary>
    public string? JoinUrl => IsListening ? "ws://localhost:" + Port.ToString(CultureInfo.InvariantCulture) + "/pfs-live/ws?token=" + Token : null;

    public bool Start(int port = DefaultPort)
    {
        if (IsListening) return true;
        var p = Math.Clamp(port, 1024, 65535);
        var prefix = "http://localhost:" + p.ToString(CultureInfo.InvariantCulture) + "/pfs-live/";
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            _listener = listener;
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts)?.Dispose();
            Prefix = prefix; Port = p; LastError = null; State = LiveBridgeState.Listening;
            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("LiveSync.Start", ex);
            State = LiveBridgeState.Failed; LastError = ex.Message;
            Raise();
            return false;
        }
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); _listener?.Close(); } catch { /* 무시 */ }
        _listener = null;
        foreach (var c in _clients.Values) Close(c, "bridge stop", graceful: false);
        cts?.Dispose();
        if (State != LiveBridgeState.Failed) State = LiveBridgeState.Idle;
        Prefix = null;
        Raise();
    }

    /// <summary>토큰 검증(고정 시간 비교 · 순수).</summary>
    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64) return false;
        var supplied = Encoding.ASCII.GetBytes(token);
        var expected = Encoding.ASCII.GetBytes(Token);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    // ── 발행 ──────────────────────────────────────────────────────────────────

    /// <summary>봉투 발행(1회 직렬화 → 전 클라이언트 공유). 반환: 큐잉된 클라이언트 수(커서 스로틀 드롭 시 −1).</summary>
    public int Publish(string type, string dataJson, string? excludeClient = null)
    {
        if (!LiveSyncTypes.IsKnown(type)) return 0;
        if (type == LiveSyncTypes.Cursor)
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastCursorTicks);
            if (last != 0 && (now - last) * 1000 / Stopwatch.Frequency < CursorIntervalMs) { Interlocked.Increment(ref _dropped); return -1; }
            Interlocked.Exchange(ref _lastCursorTicks, now);
        }
        var env = new LiveSyncEnvelope(type, Interlocked.Increment(ref _seq), SiteId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), dataJson ?? "{}");
        return Broadcast(env, excludeClient);
    }

    public int PublishGraph(VisualNodeGraph graph) => Publish(LiveSyncTypes.Graph, graph.ToJson());

    public int PublishNodeStates(VisualNodeGraph graph)
    {
        var sb = new StringBuilder(64 + graph.NodeCount * 48).Append('[');
        var first = true;
        foreach (var n in graph.Nodes)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(n.Id)).Append(",\"s\":").Append((int)n.State)
              .Append(",\"m\":").Append(JsonSerializer.Serialize(n.Status ?? "")).Append('}');
        }
        return Publish(LiveSyncTypes.NodeState, sb.Append(']').ToString());
    }

    public int PublishCursor(float x, float y, bool drawing)
        => Publish(LiveSyncTypes.Cursor, "{\"x\":" + x.ToString("0.####", CultureInfo.InvariantCulture) + ",\"y\":" + y.ToString("0.####", CultureInfo.InvariantCulture)
            + ",\"d\":" + (drawing ? "true" : "false") + "}");

    private int Broadcast(LiveSyncEnvelope env, string? exclude)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(env);
        if (LiveSyncTypes.IsStateful(env.T)) _snapshot[env.T] = bytes;
        var n = 0;
        foreach (var c in _clients.Values)
        {
            if (c.Id == exclude) continue;
            if (c.Outbound.Writer.TryWrite(bytes)) n++;
        }
        return n;
    }

    /// <summary>수신 봉투 검증(순수) — JSON · 종류 화이트리스트 · 필드 · 크기.</summary>
    public static LiveSyncEnvelope? ParseEnvelope(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxInboundBytes) return null;
        try
        {
            var env = JsonSerializer.Deserialize<LiveSyncEnvelope>(utf8);
            if (env is null || !LiveSyncTypes.IsKnown(env.T) || env.Data is null || env.Src is null || env.Src.Length > 64) return null;
            return env;
        }
        catch (JsonException) { return null; }
    }

    // ── 수신 서버 ─────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Program.Log("LiveSync.Accept", ex); break; }
            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = (ctx.Request.Url?.AbsolutePath ?? "").TrimEnd('/');
            if (!ctx.Request.IsWebSocketRequest || !path.EndsWith("/ws", StringComparison.Ordinal))
            {
                var body = Encoding.UTF8.GetBytes("{\"service\":\"PromptFlow Live Sync\",\"site\":\"" + SiteId + "\",\"clients\":" + ClientCount.ToString(CultureInfo.InvariantCulture) + "}");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }
            if (!ValidateToken(ctx.Request.QueryString["token"]) || _clients.Count >= MaxClients)
            {
                Interlocked.Increment(ref _rejected);
                ctx.Response.StatusCode = _clients.Count >= MaxClients ? 503 : 401;
                ctx.Response.Close();
                Raise();
                return;
            }
            var ws = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            Register(ws.WebSocket, ctx.Request.QueryString["label"] ?? "client", ct);
        }
        catch (Exception ex) { Program.Log("LiveSync.Handle", ex); }
    }

    private void Register(WebSocket socket, string label, CancellationToken ct)
    {
        var c = new Client
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Socket = socket,
            Label = label.Length > 32 ? label[..32] : label,
            Outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
            {
                SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
            }, _ => Interlocked.Increment(ref _dropped)),
        };
        _clients[c.Id] = c;
        foreach (var snap in _snapshot.Values) c.Outbound.Writer.TryWrite(snap);
        _ = Task.Run(() => SendPumpAsync(c, ct));
        _ = Task.Run(() => ReceiveLoopAsync(c, ct));
        Raise();
    }

    private async Task SendPumpAsync(Client c, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in c.Outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (c.Socket.State != WebSocketState.Open) break;
                await c.Socket.SendAsync(msg, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("LiveSync.Send", ex); }
    }

    private async Task ReceiveLoopAsync(Client c, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var acc = new MemoryStream();
        try
        {
            while (c.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                try { res = await c.Socket.ReceiveAsync(buffer, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (acc.Length + res.Count > MaxInboundBytes) { Interlocked.Increment(ref _rejected); acc.SetLength(0); continue; }
                acc.Write(buffer, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var env = ParseEnvelope(acc.GetBuffer().AsSpan(0, (int)acc.Length));
                acc.SetLength(0);
                if (env is null) { Interlocked.Increment(ref _rejected); continue; }
                Interlocked.Increment(ref _received);
                try { OnRemoteMessage?.Invoke(env); } catch (Exception ex) { Program.Log("LiveSync.OnRemote", ex); }
                // 재중계(발신자 제외) — 시퀀스는 코어가 재부여
                Publish(env.T, env.Data, c.Id);
            }
        }
        catch (Exception ex) { Program.Log("LiveSync.Receive", ex); }
        finally { Close(c, "receive end"); Raise(); }
    }

    /// <param name="graceful">false = 브리지 정지(수신 취소와 동시) — HttpListener WebSocket 은 진행 중 ReceiveAsync 취소(Abort)와
    /// CloseAsync(CloseOutputAsyncCore → Abort)가 경합하면 WebSocketBase 내부 락 순서 교착(2026-10-16 행 덤프 확인) → Abort 만 사용.</param>
    private void Close(Client c, string reason, bool graceful = true)
    {
        _clients.TryRemove(c.Id, out _);
        try { c.Outbound.Writer.TryComplete(); } catch { /* 무시 */ }
        try
        {
            if (c.Socket.State == WebSocketState.Open)
            {
                if (graceful) _ = c.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                else c.Socket.Abort();
            }
        }
        catch { /* 무시 */ }
        try { c.Socket.Dispose(); } catch { /* 무시 */ }
    }

    public static string Base64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("LiveSync.OnChange", ex); }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// [PromptFlowStudio] v16.21.0 📱 라이브 세션 클라이언트(모바일/웹 · <see cref="ClientWebSocket"/>) — 접속(8초 타임아웃) · 수신 루프 · 송신 · 끊김 시 지수 백오프 재접속(1→30초 · 최대 6회).
/// </summary>
public sealed class LiveSessionSyncClient : IDisposable
{
    public const int MaxReconnects = 6;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public event Action<LiveSyncEnvelope>? OnMessage;
    public event Action? OnChange;

    public string SiteId { get; } = "client-" + Guid.NewGuid().ToString("N")[..6];
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string? Url { get; private set; }
    public long Received { get; private set; }
    public int Reconnects { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>재접속 지연(순수) — 1·2·4·8·16·30초.</summary>
    public static TimeSpan BackoffOf(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Clamp(attempt, 0, 5)));

    public static bool IsValidUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "wss" || (u.Scheme == "ws" && (u.IsLoopback || IsPrivate(u.Host))));

    private static bool IsPrivate(string host)
        => IPAddress.TryParse(host, out var ip) && ip.GetAddressBytes() is { Length: 4 } b && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31));

    public async Task<bool> ConnectAsync(string url, CancellationToken ct = default)
    {
        if (!IsValidUrl(url)) { LastError = "유효하지 않은 주소(wss 또는 사설망 ws 만 허용)"; Raise(); return false; }
        Disconnect();
        Url = url;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts)?.Dispose();
        var ok = await OpenAsync(cts.Token).ConfigureAwait(false);
        if (ok) _ = Task.Run(() => RunAsync(cts.Token));
        return ok;
    }

    private async Task<bool> OpenAsync(CancellationToken ct)
    {
        try
        {
            var ws = new ClientWebSocket();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(new Uri(Url!), linked.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _ws, ws)?.Dispose();
            LastError = null;
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Raise();
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            await ReceiveAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || attempt >= MaxReconnects) break;
            try { await Task.Delay(BackoffOf(attempt++), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            Reconnects++;
            if (await OpenAsync(ct).ConfigureAwait(false)) attempt = 0;
        }
        Raise();
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null) return;
        var buffer = new byte[8192];
        using var acc = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (acc.Length + res.Count > LiveSessionSyncBridge.MaxInboundBytes * 16) { acc.SetLength(0); continue; }
                acc.Write(buffer, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var env = LiveSessionSyncBridge.ParseEnvelope(acc.GetBuffer().AsSpan(0, (int)Math.Min(acc.Length, LiveSessionSyncBridge.MaxInboundBytes)));
                acc.SetLength(0);
                if (env is null) continue;
                Received++;
                try { OnMessage?.Invoke(env); } catch (Exception ex) { Program.Log("LiveSyncClient.OnMessage", ex); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = ex.Message; }
        Raise();
    }

    public async Task<bool> SendAsync(string type, string dataJson, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open || !LiveSyncTypes.IsKnown(type)) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LiveSyncEnvelope(type, 0, SiteId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), dataJson));
        if (bytes.Length > LiveSessionSyncBridge.MaxInboundBytes) return false;
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
        finally { _sendGate.Release(); }
    }

    public void Disconnect()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        var ws = Interlocked.Exchange(ref _ws, null);
        try { if (ws?.State == WebSocketState.Open) _ = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { /* 무시 */ }
        ws?.Dispose();
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("LiveSyncClient.OnChange", ex); }
    }

    public void Dispose()
    {
        Disconnect();
        _sendGate.Dispose();
    }
}

/// <summary>v16.21.0 원격 GPU 워커 설정(환경변수 · 시크릿 하드코딩 금지).</summary>
public sealed record GpuWorkerConfig(string? Endpoint, string? ApiKey)
{
    public const string EndpointVar = "PFS_GPU_WORKER_ENDPOINT", KeyVar = "PFS_GPU_WORKER_KEY";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey)
        && Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public static GpuWorkerConfig FromEnvironment()
        => new(Environment.GetEnvironmentVariable(EndpointVar), Environment.GetEnvironmentVariable(KeyVar));
}

/// <summary>
/// [PromptFlowStudio] v16.21.0 ☁️ 원격 GPU 오프로딩 릴레이 — 노드 그래프 JSON 을 클라우드 GPU 워커로 제출·폴링·결과 다운로드.
///   ▸ 헬스 프로브 1초 타임아웃(실패 시 즉시 로컬 폴백) · 제출 3회 지수 백오프(429/408/5xx) · 폴링 상한 · 응답 스키마/URL(https·loopback)/노드 Id 즉시 검증.
///   ▸ 본문 서명: HMACSHA256(키, canonical 본문) 헤더 X-PFS-Signature — canonical 에 노드/엣지 집계 필드 포함. 키는 환경변수.
///   ▸ 노드별 원격 상태를 그래프에 반영 + <see cref="LiveSessionSyncBridge"/> 로 방송(선택).
/// </summary>
public sealed class RemoteGpuOffloadRelay : IRemoteGraphRunner, IDisposable
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    public const int MaxSubmitAttempts = 3, MaxPolls = 600;

    private readonly Func<GpuWorkerConfig> _config;
    private readonly HttpClient _http;
    private readonly TimeSpan _poll;
    private readonly LiveSessionSyncBridge? _bridge;

    public RemoteGpuOffloadRelay(LiveSessionSyncBridge? bridge = null)
        : this(GpuWorkerConfig.FromEnvironment, null, TimeSpan.FromSeconds(2), bridge) { }

    public RemoteGpuOffloadRelay(Func<GpuWorkerConfig> config, HttpMessageHandler? handler, TimeSpan poll, LiveSessionSyncBridge? bridge = null)
    {
        _config = config;
        _poll = poll;
        _bridge = bridge;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public bool IsConfigured => _config().IsConfigured;
    public int Offloaded { get; private set; }
    public int Fallbacks { get; private set; }

    /// <summary>canonical 본문(순수) — 스키마 · 집계 · 그래프 JSON.</summary>
    public static string CanonicalBody(VisualNodeGraph.GraphDto dto)
        => "{\"schema\":" + dto.Schema.ToString(CultureInfo.InvariantCulture) + ",\"nodeCount\":" + dto.NodeCount.ToString(CultureInfo.InvariantCulture)
           + ",\"edgeCount\":" + dto.EdgeCount.ToString(CultureInfo.InvariantCulture) + ",\"graph\":" + JsonSerializer.Serialize(dto) + "}";

    public static string Sign(string key, string body)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body), mac);
        return Convert.ToHexString(mac);
    }

    public static bool VerifySignature(string key, string body, string? signatureHex)
    {
        if (string.IsNullOrEmpty(signatureHex) || signatureHex.Length != 64) return false;
        try
        {
            var expected = Convert.FromHexString(Sign(key, body));
            var supplied = Convert.FromHexString(signatureHex);
            return CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>상태 응답 검증(순수) — status · outputs(https/loopback) · nodes[{id,state,message}].</summary>
    public static (string Status, List<string> Outputs, List<(string Id, VisualNodeState State, string Message)> Nodes, string? Error) ParseStatus(string json)
    {
        var outputs = new List<string>(2);
        var nodes = new List<(string, VisualNodeState, string)>(8);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.String) return ("invalid", outputs, nodes, "스키마 불일치");
            var status = (st.GetString() ?? "").ToLowerInvariant() switch
            {
                "succeeded" or "completed" or "done" => "succeeded",
                "failed" or "error" or "canceled" => "failed",
                "queued" or "running" or "processing" => "running",
                _ => "invalid",
            };
            if (r.TryGetProperty("outputs", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var u in o.EnumerateArray())
                    if (u.ValueKind == JsonValueKind.String && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback) && outputs.Count < 16)
                        outputs.Add(uri.AbsoluteUri);
            if (r.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (var n in ns.EnumerateArray())
                {
                    if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !VisualNodeGraph.IsSafeId(id.GetString())) continue;
                    var state = n.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String && Enum.TryParse<VisualNodeState>(s.GetString(), true, out var vs) ? vs : VisualNodeState.Running;
                    var msg = n.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                    nodes.Add((id.GetString()!, state, msg.Length > 120 ? msg[..120] : msg));
                }
            var err = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (status == "succeeded" && outputs.Count == 0) return ("invalid", outputs, nodes, "outputs 누락");
            return (status, outputs, nodes, err);
        }
        catch (JsonException) { return ("invalid", outputs, nodes, "JSON 파싱 실패"); }
    }

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        var cfg = _config();
        if (!cfg.IsConfigured) return false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(ProbeTimeout);
            using var resp = await _http.GetAsync(cfg.Endpoint!.TrimEnd('/') + "/health", linked.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    public async Task<GraphRunResult> RunAsync(VisualNodeGraph graph, string outputRoot, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cfg = _config();
        if (!cfg.IsConfigured) return Fail("원격 GPU 워커 미구성", sw);
        if (!await ProbeAsync(ct).ConfigureAwait(false)) { Fallbacks++; return Fail("헬스 프로브 1초 내 무응답", sw); }
        var baseUri = cfg.Endpoint!.TrimEnd('/');
        try
        {
            var body = CanonicalBody(graph.ToDto());
            var sig = Sign(cfg.ApiKey!, body);
            string? id = null;
            var delay = TimeSpan.FromMilliseconds(500);
            for (var attempt = 1; attempt <= MaxSubmitAttempts && id is null; attempt++)
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, baseUri + "/graph/run") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                msg.Headers.TryAddWithoutValidation("X-PFS-Signature", sig);
                msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);
                using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    id = IdOf(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (id is null) { Fallbacks++; return Fail("제출 응답 스키마 불일치", sw); }
                    break;
                }
                var code = (int)resp.StatusCode;
                if (!(code == 429 || code == 408 || code >= 500) || attempt == MaxSubmitAttempts) { Fallbacks++; return Fail("HTTP " + code.ToString(CultureInfo.InvariantCulture), sw); }
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay += delay;
            }
            for (var i = 0; i < MaxPolls; i++)
            {
                await Task.Delay(_poll, ct).ConfigureAwait(false);
                using var msg = new HttpRequestMessage(HttpMethod.Get, baseUri + "/graph/run/" + id);
                msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);
                using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { if ((int)resp.StatusCode >= 500) continue; Fallbacks++; return Fail("상태 HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture), sw); }
                var (st, outs, nodeStates, err) = ParseStatus(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                Apply(graph, nodeStates);
                if (st == "running") continue;
                if (st != "succeeded") { Fallbacks++; return Fail(err ?? st, sw); }
                Directory.CreateDirectory(outputRoot);
                var local = new List<string>(outs.Count);
                for (var k = 0; k < outs.Count; k++)
                {
                    var path = Path.Combine(outputRoot, "remote_" + (k + 1).ToString("00", CultureInfo.InvariantCulture) + ".mp4");
                    using var dl = await _http.GetAsync(outs[k], HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!dl.IsSuccessStatusCode) continue;
                    await using (var src = await dl.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                    local.Add(path);
                }
                if (local.Count == 0) { Fallbacks++; return Fail("결과 다운로드 실패", sw); }
                Offloaded++;
                var done = 0;
                foreach (var n in graph.Nodes) if (n.State == VisualNodeState.Completed) done++;
                return new GraphRunResult(true, GraphRunBackend.RemoteGpu, done, 0, 0, local, sw.Elapsed.TotalMilliseconds,
                    "☁️ 원격 GPU 실행 완료 · 출력 " + local.Count.ToString(CultureInfo.InvariantCulture) + "개");
            }
            Fallbacks++;
            return Fail("원격 실행 시간 초과", sw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail("취소됨", sw); }
        catch (Exception ex) { Program.Log("GpuRelay.Run", ex); Fallbacks++; return Fail(ex.GetType().Name, sw); }
    }

    private void Apply(VisualNodeGraph graph, List<(string Id, VisualNodeState State, string Message)> states)
    {
        if (states.Count == 0) return;
        foreach (var (id, s, m) in states)
        {
            var n = graph.Find(id);
            if (n is null) continue;
            n.State = s;
            n.Status = m.Length > 0 ? "☁️ " + m : "☁️ 원격";
        }
        graph.Raise();
        _bridge?.PublishNodeStates(graph);
    }

    private static string? IdOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String
                   && VisualNodeGraph.IsSafeId(v.GetString()) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static GraphRunResult Fail(string msg, Stopwatch sw) => new(false, GraphRunBackend.RemoteGpu, 0, 0, 0, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, msg);

    public void Dispose() => _http.Dispose();
}
