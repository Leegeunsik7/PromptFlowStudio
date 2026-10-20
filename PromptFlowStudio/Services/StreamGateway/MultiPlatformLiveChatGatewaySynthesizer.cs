using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>v17.62.0 플랫폼 링크 상태.</summary>
public enum GatewayLinkState : byte { Offline, Connecting, Connected, Backoff }

/// <summary>
/// [v17.62.0] 플랫폼 세션 1 개 — 연결 → 핸드셰이크 → 수신 루프(IRC 줄 프레이밍 · WS 메시지 조립 · YouTube 폴링) → 파서 → 배압 가드.
/// 끊김/예외는 가드의 지수 백오프(지터) 후 무한 재접속. 핑(Twitch PING · 치지직 cmd 0) 왕복으로 RTT 계측. 수신 버퍼는 세션 생성 시 1 회 할당.
/// </summary>
public sealed class LiveChatPlatformSession
{
    public const int StreamBuf = 64 * 1024, MessageBuf = 256 * 1024, PingIntervalMs = 20_000;
    private static ReadOnlySpan<byte> ChzzkPing => "{\"ver\":\"2\",\"cmd\":0}"u8;
    private static ReadOnlySpan<byte> ChzzkPong => "{\"ver\":\"2\",\"cmd\":10000}"u8;
    private readonly SocketBackpressureDropoutGuard _g;
    private readonly byte[] _buf = new byte[StreamBuf], _msg = new byte[MessageBuf], _send = new byte[1024];
    private Func<ILiveChatWire>? _wire;
    private Func<IYouTubeChatPoller>? _poller;
    private byte[] _handshake = Array.Empty<byte>();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _pingSentMs = -1, _lastPingMs;

    internal LiveChatPlatformSession(ChatPlatform p, SocketBackpressureDropoutGuard g) { Platform = p; _g = g; }

    public ChatPlatform Platform { get; }
    public LiveChatProtocolParser Parser { get; } = new();
    public GatewayLinkState State { get; private set; }
    public string Endpoint { get; private set; } = "—";
    public bool Simulated { get; private set; }
    public double RttMs { get; private set; } = -1;
    public long Messages => Parser.Offered;
    public long Bytes { get; private set; }
    public long Connects { get; private set; }
    public long Failures { get; private set; }
    public long ConnectedAtMs { get; private set; }
    public long LastDataMs { get; private set; }
    public long NextRetryAtMs { get; private set; }
    public string? LastError { get; private set; }
    public bool Running => _task is { IsCompleted: false };

    internal void Start(Func<ILiveChatWire>? wire, Func<IYouTubeChatPoller>? poller, byte[] handshake, string endpoint, bool simulated)
    {
        Stop();
        _wire = wire; _poller = poller; _handshake = handshake; Endpoint = endpoint; Simulated = simulated;
        LastError = null; RttMs = -1;
        Parser.ResetPaging();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        var c = _cts; var t = _task;
        _cts = null; _task = null;
        if (c is null) return;
        try { c.Cancel(); t?.Wait(3000); }
        catch (AggregateException ex) { PromptFlowStudio.Program.Log("Gateway.Stop", ex); }
        finally { c.Dispose(); }
        State = GatewayLinkState.Offline;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                State = GatewayLinkState.Connecting;
                if (_poller is not null) await PollLoopAsync(ct).ConfigureAwait(false);
                else await StreamLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or HttpRequestException or InvalidOperationException
                                          or System.Security.Authentication.AuthenticationException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                LastError = ex.Message; Failures++;
            }
            if (ct.IsCancellationRequested) break;
            var d = _g.OnDisconnected(Platform);
            State = GatewayLinkState.Backoff;
            NextRetryAtMs = GatewayClock.NowMs + (long)d.TotalMilliseconds;
            try { await Task.Delay(d, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        State = GatewayLinkState.Offline;
    }

    private void OnConnected()
    {
        State = GatewayLinkState.Connected; Connects++;
        ConnectedAtMs = LastDataMs = _lastPingMs = GatewayClock.NowMs; _pingSentMs = -1;
        _g.OnConnected(Platform);
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        using var w = _wire!();
        await w.ConnectAsync(ct).ConfigureAwait(false);
        OnConnected();
        if (_handshake.Length > 0) await w.SendAsync(_handshake, ct).ConfigureAwait(false);
        var carry = 0; var skipping = false;
        while (!ct.IsCancellationRequested)
        {
            var now = GatewayClock.NowMs;
            if (now - _lastPingMs >= PingIntervalMs && _pingSentMs < 0) { _lastPingMs = now; _pingSentMs = now; await w.SendAsync(Platform == ChatPlatform.Twitch ? "PING :pf-rtt\r\n"u8.ToArray() : ChzzkPing.ToArray(), ct).ConfigureAwait(false); }
            if (w.MessageFramed)
            {
                var r = await w.ReceiveAsync(_msg.AsMemory(carry), ct).ConfigureAwait(false);
                if (r.Count == 0) throw new IOException("원격 종료");
                Bytes += r.Count; LastDataMs = GatewayClock.NowMs;
                carry += r.Count;
                if (!r.EndOfMessage)
                {
                    if (carry >= _msg.Length) { carry = 0; skipping = true; Parser.ResetPaging(); }
                    continue;
                }
                if (!skipping) await OnFrameAsync(w, carry, ct).ConfigureAwait(false);
                carry = 0; skipping = false;
            }
            else
            {
                if (carry >= _buf.Length) carry = 0;   // 개행 없는 과대 줄 폐기
                var r = await w.ReceiveAsync(_buf.AsMemory(carry), ct).ConfigureAwait(false);
                if (r.Count == 0) throw new IOException("원격 종료");
                Bytes += r.Count; LastDataMs = GatewayClock.NowMs;
                var total = carry + r.Count; var start = 0;
                while (true)
                {
                    var nl = _buf.AsSpan(start, total - start).IndexOf((byte)'\n');
                    if (nl < 0) break;
                    var kind = Parser.ParseTwitch(_buf.AsSpan(start, nl), _g, GatewayClock.NowMs);
                    if (kind == IrcLineKind.Ping) await PongAsync(w, start, nl, ct).ConfigureAwait(false);
                    else if (kind == IrcLineKind.Pong && _pingSentMs >= 0) { RttMs = GatewayClock.NowMs - _pingSentMs; _pingSentMs = -1; }
                    else if (kind is IrcLineKind.Welcome or IrcLineKind.PrivMsg) _g.OnHealthy(Platform);
                    else if (kind == IrcLineKind.Reconnect) throw new IOException("서버 RECONNECT 요청");
                    start += nl + 1;
                }
                carry = total - start;
                if (carry > 0 && start > 0) _buf.AsSpan(start, carry).CopyTo(_buf);
            }
        }
    }

    private ValueTask PongAsync(ILiveChatWire w, int start, int len, CancellationToken ct)
    {
        var line = _buf.AsSpan(start, len);
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        var arg = line.Length > 4 ? line[4..] : default;
        var n = Math.Min(arg.Length, _send.Length - 8);
        "PONG"u8.CopyTo(_send); arg[..n].CopyTo(_send.AsSpan(4)); _send[4 + n] = (byte)'\r'; _send[5 + n] = (byte)'\n';
        return w.SendAsync(_send.AsMemory(0, 6 + n), ct);
    }

    private async ValueTask OnFrameAsync(ILiveChatWire w, int len, CancellationToken ct)
    {
        var n = Parser.ParseChzzk(_msg.AsSpan(0, len), _g, GatewayClock.NowMs);
        var cmd = Parser.LastChzzkCmd;
        if (cmd == 0) { ChzzkPong.CopyTo(_send); await w.SendAsync(_send.AsMemory(0, ChzzkPong.Length), ct).ConfigureAwait(false); }
        else if (cmd == 10000 && _pingSentMs >= 0) { RttMs = GatewayClock.NowMs - _pingSentMs; _pingSentMs = -1; }
        if (cmd == 10100 || n > 0) _g.OnHealthy(Platform);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var p = _poller!();
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var t0 = GatewayClock.NowMs;
            var n = await p.PollAsync(Parser.PageToken.ToArray(), _msg, ct).ConfigureAwait(false);
            RttMs = GatewayClock.NowMs - t0;
            if (first) { OnConnected(); first = false; }
            Bytes += n; LastDataMs = GatewayClock.NowMs;
            if (Parser.ParseYouTubePage(_msg.AsSpan(0, n), _g, GatewayClock.NowMs) < 0) throw new InvalidOperationException("YouTube 응답 스키마 불일치");
            _g.OnHealthy(Platform);
            await Task.Delay(TimeSpan.FromMilliseconds(Parser.PollingIntervalMs * Math.Max(_g.BackoffScale, 0.05)), ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.62.0] 🛰 멀티 플랫폼 실시간 라이브 채팅 게이트웨이 합성기.
///   ▸ Twitch IRC(TLS 6697 · CAP twitch.tv/tags · 익명 justinfan 또는 PF_TWITCH_OAUTH/PF_TWITCH_NICK) · YouTube Live(liveChat/messages 폴링 · PF_YOUTUBE_API_KEY) · 치지직(WSS · cmd 100 READ 접속)을
///     <see cref="LiveChatPlatformSession"/> 3 개로 추상화 — 수신 UTF-8 은 <see cref="LiveChatProtocolParser"/> 가 고정 버퍼 스팬으로 해제해 <see cref="SocketBackpressureDropoutGuard"/> 에 적재,
///     렌더 틱이 배압 큐를 v17.61 리액션 트리거 엔진(<see cref="IChatDispatchTarget"/>)으로 직접 디스패치한다.
///   ▸ 시크릿은 환경변수에서만 읽고(하드코딩 금지), 토큰/채널 해석 실패는 1 초 내 시뮬레이션 소켓 폴백을 선택할 수 있다.
///   ▸ 시뮬레이션 모드: 플랫폼별 실제 프로토콜 바이트(IRC 줄 · WS JSON 프레임 · YouTube 페이지)를 생성하는 루프백 소켓 — 순단 · 접속 거부 · 레이드 폭주 재현.
/// </summary>
public sealed class MultiPlatformLiveChatGatewaySynthesizer : IDisposable
{
    public const string TwitchOauthEnv = "PF_TWITCH_OAUTH", TwitchNickEnv = "PF_TWITCH_NICK", ChzzkTokenEnv = "PF_CHZZK_TOKEN";
    private readonly LiveChatPlatformSession[] _s = new LiveChatPlatformSession[3];

    public MultiPlatformLiveChatGatewaySynthesizer(SocketBackpressureDropoutGuard guard)
    {
        Guard = guard;
        for (var i = 0; i < 3; i++) _s[i] = new LiveChatPlatformSession((ChatPlatform)i, guard);
    }

    public SocketBackpressureDropoutGuard Guard { get; }
    public SimulatedChatTraffic Traffic { get; } = new();
    public LiveChatPlatformSession Session(ChatPlatform p) => _s[(int)p];
    public int ConnectedCount { get { var n = 0; foreach (var s in _s) if (s.State == GatewayLinkState.Connected) n++; return n; } }
    public static string PlatformName(ChatPlatform p) => p switch { ChatPlatform.Twitch => "Twitch", ChatPlatform.YouTube => "YouTube Live", _ => "치지직" };
    public static string StateName(GatewayLinkState s) => s switch { GatewayLinkState.Connecting => "연결 중", GatewayLinkState.Connected => "연결됨", GatewayLinkState.Backoff => "재접속 대기", _ => "오프라인" };

    // ══════════════════════ 연결(비핫패스) ══════════════════════
    public void ConnectSimulated(ChatPlatform p)
    {
        var t = Traffic;
        if (p == ChatPlatform.YouTube) _s[(int)p].Start(null, () => new SimulatedYouTubePoller(t), Array.Empty<byte>(), "sim://youtube/liveChat", true);
        else _s[(int)p].Start(() => new SimulatedChatWire(p, t), null, p == ChatPlatform.Twitch ? TwitchHandshake("pfstudio", null, null) : ChzzkHandshake("sim-cid", "sim-token"), p == ChatPlatform.Twitch ? "sim://irc.chat.twitch.tv:6697" : "sim://kr-ss1.chat.naver.com/chat", true);
    }

    public void ConnectTwitch(string channel, string host = "irc.chat.twitch.tv", int port = 6697, bool tls = true)
    {
        var ch = channel.Trim().TrimStart('#').ToLowerInvariant();
        var hs = TwitchHandshake(ch, Environment.GetEnvironmentVariable(TwitchOauthEnv), Environment.GetEnvironmentVariable(TwitchNickEnv));
        _s[0].Start(() => new TcpTlsChatWire(host, port, tls), null, hs, (tls ? "ircs://" : "irc://") + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/#" + ch, false);
    }

    public void ConnectChzzk(string chatChannelId, string accessToken, Uri? endpoint = null)
    {
        var uri = endpoint ?? ChzzkEndpointResolver.ChatEndpoint;
        _s[2].Start(() => new WebSocketChatWire(uri), null, ChzzkHandshake(chatChannelId, accessToken), uri + " · " + chatChannelId, false);
    }

    public async Task<bool> ConnectChzzkChannelAsync(string channelId, CancellationToken ct)
    {
        var r = await ChzzkEndpointResolver.ResolveAsync(channelId.Trim(), ct).ConfigureAwait(false);
        if (r is not { } ok) return false;
        ConnectChzzk(ok.ChatChannelId, ok.AccessToken);
        return true;
    }

    public void ConnectYouTube(string liveChatId, string apiKey) => _s[1].Start(null, () => new YouTubeHttpChatPoller(liveChatId, apiKey), Array.Empty<byte>(), "https://www.googleapis.com/youtube/v3/liveChat · " + liveChatId, false);

    public async Task<bool> ConnectYouTubeVideoAsync(string videoId, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable(YouTubeHttpChatPoller.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key)) return false;
        var id = await YouTubeHttpChatPoller.ResolveLiveChatIdAsync(videoId.Trim(), key, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(id)) return false;
        ConnectYouTube(id, key);
        return true;
    }

    /// <summary>임의 소켓 결선(테스트 · 사설 릴레이).</summary>
    public void ConnectWire(ChatPlatform p, Func<ILiveChatWire> wire, byte[] handshake, string endpoint) => _s[(int)p].Start(wire, null, handshake, endpoint, false);
    public void ConnectPoller(Func<IYouTubeChatPoller> poller, string endpoint) => _s[1].Start(null, poller, Array.Empty<byte>(), endpoint, false);

    public void Disconnect(ChatPlatform p) => _s[(int)p].Stop();
    public void DisconnectAll() { foreach (var s in _s) s.Stop(); }

    public static byte[] TwitchHandshake(string channel, string? oauth, string? nick)
    {
        var sb = new StringBuilder("CAP REQ :twitch.tv/tags twitch.tv/commands\r\n");
        if (!string.IsNullOrWhiteSpace(oauth) && !string.IsNullOrWhiteSpace(nick))
            sb.Append("PASS ").Append(oauth.StartsWith("oauth:", StringComparison.Ordinal) ? oauth : "oauth:" + oauth).Append("\r\nNICK ").Append(nick.ToLowerInvariant()).Append("\r\n");
        else sb.Append("NICK justinfan").Append(Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("JOIN #").Append(channel).Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static byte[] ChzzkHandshake(string chatChannelId, string accessToken)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("ver", "2"); w.WriteNumber("cmd", 100); w.WriteString("svcid", "game"); w.WriteString("cid", chatChannelId);
            w.WriteStartObject("bdy"); w.WriteNull("uid"); w.WriteNumber("devType", 2001); w.WriteString("accTkn", accessToken); w.WriteString("auth", "READ"); w.WriteEndObject();
            w.WriteNumber("tid", 1);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public void Dispose() => DisconnectAll();
}
