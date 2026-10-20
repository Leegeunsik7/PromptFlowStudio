using System.Globalization;
using System.Text;
namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>
/// [v17.62.0] 시뮬레이션 시청자 트래픽 설정(플랫폼당 msg/s · 후원/모더레이터 비율 · 레이드 폭주 · 순단/접속 거부).
/// 생성 바이트는 각 플랫폼 실제 와이어 형식과 동일하다(IRCv3 태그 줄 · 치지직 cmd 93101/93102 JSON · YouTube liveChatMessages 페이지).
/// </summary>
public sealed class SimulatedChatTraffic
{
    internal static readonly string[] Users = BuildUsers();
    internal static readonly string[] Texts =
    {
        "안녕하세요~", "오늘 방송 최고예요", "이 노래 제목이 뭐예요?", "ㅋㅋㅋㅋ 개웃겨", "너무 귀여워요!!", "다음 방송은 언제예요?", "hello from LA", "lol",
        "와 미쳤다", "love this stream", "클립 따라!!", "사랑해요 💖", "what game is this?", "레전드 각", "대박 대박", "처음 왔어요",
    };
    private readonly int[] _drop = new int[3];

    public float RatePerSec { get; set; } = 4f;
    public float RaidRatePerSec { get; set; } = 400f;
    public float DonationRatio { get; set; } = 0.03f;
    public float ModeratorRatio { get; set; } = 0.03f;
    public bool Flaky { get; set; }
    public double MeanUptimeSec { get; set; } = 25;
    public float ConnectFailRatio { get; set; } = 0.3f;
    public long RaidUntilMs { get; private set; }
    public bool RaidActive => GatewayClock.NowMs < RaidUntilMs;
    public float RateNow => RaidActive ? RaidRatePerSec : RatePerSec;

    public void StartRaid(double seconds) => RaidUntilMs = GatewayClock.NowMs + (long)(seconds * 1000);
    public void StopRaid() => RaidUntilMs = 0;
    /// <summary>지정 플랫폼 소켓 즉시 순단.</summary>
    public void ForceDrop(ChatPlatform p) => Interlocked.Exchange(ref _drop[(int)p], 1);
    internal bool TakeDrop(ChatPlatform p) => Interlocked.Exchange(ref _drop[(int)p], 0) == 1;

    private static string[] BuildUsers()
    {
        var u = new string[256];
        for (var i = 0; i < u.Length; i++) u[i] = (i % 3 == 0 ? "시청자" : i % 3 == 1 ? "viewer_" : "fan") + i.ToString("000", CultureInfo.InvariantCulture);
        return u;
    }
}

internal struct SimRng
{
    private uint _s;
    public SimRng(uint seed) => _s = seed | 1;
    public uint Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
    public float F() => (Next() >> 8) * (1f / 16777216f);
}

/// <summary>[v17.62.0] 루프백 시뮬레이션 소켓(Twitch IRC 줄 스트림 · 치지직 WS 프레임) — 핑 응답 · 서버 핑 · 순단 · 접속 거부.</summary>
public sealed class SimulatedChatWire : ILiveChatWire
{
    private readonly ChatPlatform _p;
    private readonly SimulatedChatTraffic _t;
    private readonly byte[] _reply = new byte[256];
    private SimRng _r;
    private int _replyLen;
    private long _replyDue, _downAt, _last, _serverPingAt;
    private double _acc;
    private bool _connected;

    public SimulatedChatWire(ChatPlatform p, SimulatedChatTraffic t) { _p = p; _t = t; _r = new SimRng((uint)Environment.TickCount ^ (uint)p * 7919u); }

    public bool MessageFramed => _p == ChatPlatform.Chzzk;

    public async ValueTask ConnectAsync(CancellationToken ct)
    {
        await Task.Delay(20 + (int)(_r.F() * 40), ct).ConfigureAwait(false);
        if (_t.Flaky && _r.F() < _t.ConnectFailRatio) throw new IOException("시뮬 접속 거부(ECONNREFUSED)");
        _t.TakeDrop(_p);
        var now = GatewayClock.NowMs;
        _downAt = _t.Flaky ? now + (long)(-Math.Log(Math.Max(1e-4, _r.F())) * _t.MeanUptimeSec * 1000) : long.MaxValue;
        _last = now; _serverPingAt = now + 30_000; _connected = true;
        if (_p == ChatPlatform.Twitch) Reply(":tmi.twitch.tv 001 justinfan :Welcome, GLHF!\r\n"u8, now + 5);
        else Reply("{\"ver\":\"2\",\"cmd\":10100,\"retCode\":0,\"bdy\":{\"sid\":\"sim\"}}"u8, now + 5);
    }

    private void Reply(ReadOnlySpan<byte> b, long due) { var n = Math.Min(b.Length, _reply.Length); b[..n].CopyTo(_reply); _replyLen = n; _replyDue = due; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var s = data.Span;
        var rtt = 15 + (long)(_r.F() * 30);
        if (_p == ChatPlatform.Twitch)
        {
            var k = s.IndexOf("PING :"u8);
            if (k >= 0)
            {
                var arg = s[(k + 6)..]; var e = arg.IndexOf((byte)'\r'); if (e >= 0) arg = arg[..e];
                Span<byte> tmp = stackalloc byte[128]; var n = 0;
                var pre = ":tmi.twitch.tv PONG tmi.twitch.tv :"u8; pre.CopyTo(tmp); n = pre.Length; arg[..Math.Min(arg.Length, 80)].CopyTo(tmp[n..]); n += Math.Min(arg.Length, 80); tmp[n++] = (byte)'\r'; tmp[n++] = (byte)'\n';
                Reply(tmp[..n], GatewayClock.NowMs + rtt);
            }
        }
        else if (s.IndexOf("\"cmd\":0}"u8) >= 0) Reply("{\"ver\":\"2\",\"cmd\":10000}"u8, GatewayClock.NowMs + rtt);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<WireRead> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (!_connected) return default;
            var now = GatewayClock.NowMs;
            if (_t.TakeDrop(_p) || now >= _downAt) { _connected = false; return default; }
            if (_replyLen > 0 && now >= _replyDue)
            {
                var n = _replyLen; _reply.AsSpan(0, n).CopyTo(buffer.Span); _replyLen = 0;
                return new WireRead(n, true);
            }
            if (now >= _serverPingAt) { _serverPingAt = now + 30_000; return new WireRead(ServerPing(buffer.Span), true); }
            _acc += _t.RateNow * (now - _last) / 1000.0; _last = now;
            if (_acc >= 1)
            {
                var count = (int)Math.Min(_acc, _p == ChatPlatform.Twitch ? 400 : 100);
                _acc -= count;
                var len = _p == ChatPlatform.Twitch ? WriteIrc(buffer.Span, count) : WriteChzzk(buffer.Span, count);
                if (len > 0) return new WireRead(len, true);
            }
            await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    private int ServerPing(Span<byte> d)
    {
        var ping = _p == ChatPlatform.Twitch ? "PING :tmi.twitch.tv\r\n"u8 : "{\"ver\":\"2\",\"cmd\":0}"u8;
        ping.CopyTo(d);
        return ping.Length;
    }

    private int WriteIrc(Span<byte> d, int count)
    {
        var n = 0;
        for (var i = 0; i < count && n + 400 < d.Length; i++)
        {
            var u = SimulatedChatTraffic.Users[_r.Next() % (uint)SimulatedChatTraffic.Users.Length];
            var txt = SimulatedChatTraffic.Texts[_r.Next() % (uint)SimulatedChatTraffic.Texts.Length];
            var f = _r.F();
            n += A(d[n..], "@badge-info=;badges=");
            if (f < _t.ModeratorRatio) n += A(d[n..], "moderator/1");
            n += A(d[n..], ";color=#1E90FF;display-name="); n += Encoding.UTF8.GetBytes(u, d[n..]);
            if (f > 1f - _t.DonationRatio) { n += A(d[n..], ";bits="); ((int)(100 + _r.Next() % 900)).TryFormat(d[n..], out var w, default, CultureInfo.InvariantCulture); n += w; }
            n += A(d[n..], ";mod="); n += A(d[n..], f < _t.ModeratorRatio ? "1" : "0");
            n += A(d[n..], " :sim!sim@sim.tmi.twitch.tv PRIVMSG #pfstudio :"); n += Encoding.UTF8.GetBytes(txt, d[n..]);
            d[n++] = (byte)'\r'; d[n++] = (byte)'\n';
        }
        return n;
    }

    private int WriteChzzk(Span<byte> d, int count)
    {
        var donation = _r.F() < _t.DonationRatio;
        var n = A(d, "{\"svcid\":\"game\",\"ver\":\"1\",\"bdy\":[");
        for (var i = 0; i < count && n + 600 < d.Length; i++)
        {
            if (i > 0) d[n++] = (byte)',';
            var u = SimulatedChatTraffic.Users[_r.Next() % (uint)SimulatedChatTraffic.Users.Length];
            var txt = SimulatedChatTraffic.Texts[_r.Next() % (uint)SimulatedChatTraffic.Texts.Length];
            var mod = _r.F() < _t.ModeratorRatio;
            n += A(d[n..], "{\"uid\":\"u"); ((int)(_r.Next() % 99999)).TryFormat(d[n..], out var w, default, CultureInfo.InvariantCulture); n += w;
            n += A(d[n..], "\",\"msg\":\""); n += Encoding.UTF8.GetBytes(txt, d[n..]);
            n += A(d[n..], "\",\"msgTypeCode\":"); n += A(d[n..], donation ? "10" : "1");
            n += A(d[n..], ",\"profile\":\"{\\\"nickname\\\":\\\""); n += Encoding.UTF8.GetBytes(u, d[n..]);
            n += A(d[n..], "\\\",\\\"userRoleCode\\\":\\\""); n += A(d[n..], mod ? "streaming_chat_manager" : "common_user"); n += A(d[n..], "\\\"}\"");
            if (donation) { n += A(d[n..], ",\"extras\":\"{\\\"payAmount\\\":"); ((int)(1000 * (1 + _r.Next() % 10))).TryFormat(d[n..], out w, default, CultureInfo.InvariantCulture); n += w; n += A(d[n..], "}\""); }
            n += A(d[n..], ",\"msgTime\":1700000000000}");
        }
        n += A(d[n..], "],\"cmd\":"); n += A(d[n..], donation ? "93102" : "93101"); n += A(d[n..], ",\"tid\":null,\"cid\":\"sim-cid\"}");
        return n;
    }

    internal static int A(Span<byte> d, string s) { for (var i = 0; i < s.Length; i++) d[i] = (byte)s[i]; return s.Length; }

    public void Dispose() => _connected = false;
}

/// <summary>[v17.62.0] 루프백 YouTube liveChatMessages 폴러(페이지 JSON · nextPageToken · pollingIntervalMillis 1 000 · 순단/쿼터 오류 재현).</summary>
public sealed class SimulatedYouTubePoller : IYouTubeChatPoller
{
    private readonly SimulatedChatTraffic _t;
    private SimRng _r = new((uint)Environment.TickCount ^ 0x9E3779B9u);
    private long _last = GatewayClock.NowMs, _page;

    public SimulatedYouTubePoller(SimulatedChatTraffic t) => _t = t;

    public async ValueTask<int> PollAsync(ReadOnlyMemory<byte> pageToken, Memory<byte> dst, CancellationToken ct)
    {
        await Task.Delay(30 + (int)(_r.F() * 50), ct).ConfigureAwait(false);
        if (_t.TakeDrop(ChatPlatform.YouTube) || _t.Flaky && _r.F() < _t.ConnectFailRatio * 0.2f) throw new HttpRequestException("시뮬 YouTube 503");
        return Page(dst.Span);
    }

    private int Page(Span<byte> d)
    {
        var now = GatewayClock.NowMs;
        var count = (int)Math.Min(1500, _t.RateNow * (now - _last) / 1000.0); _last = now;
        var n = 0;
        n += SimulatedChatWire.A(d, "{\"kind\":\"youtube#liveChatMessageListResponse\",\"pollingIntervalMillis\":1000,\"nextPageToken\":\"simtok");
        (++_page).TryFormat(d[n..], out var w, default, CultureInfo.InvariantCulture); n += w;
        n += SimulatedChatWire.A(d[n..], "\",\"items\":[");
        for (var i = 0; i < count && n + 700 < d.Length; i++)
        {
            if (i > 0) d[n++] = (byte)',';
            var u = SimulatedChatTraffic.Users[_r.Next() % (uint)SimulatedChatTraffic.Users.Length];
            var txt = SimulatedChatTraffic.Texts[_r.Next() % (uint)SimulatedChatTraffic.Texts.Length];
            var f = _r.F(); var sc = f > 1f - _t.DonationRatio;
            n += SimulatedChatWire.A(d[n..], sc ? "{\"snippet\":{\"type\":\"superChatEvent\",\"displayMessage\":\"" : "{\"snippet\":{\"type\":\"textMessageEvent\",\"displayMessage\":\"");
            n += Encoding.UTF8.GetBytes(txt, d[n..]); n += SimulatedChatWire.A(d[n..], "\"");
            if (sc) { n += SimulatedChatWire.A(d[n..], ",\"superChatDetails\":{\"amountMicros\":\""); ((long)(1000 + _r.Next() % 20 * 1000) * 1_000_000).TryFormat(d[n..], out w, default, CultureInfo.InvariantCulture); n += w; n += SimulatedChatWire.A(d[n..], "\",\"currency\":\"KRW\"}"); }
            n += SimulatedChatWire.A(d[n..], "},\"authorDetails\":{\"displayName\":\""); n += Encoding.UTF8.GetBytes(u, d[n..]);
            n += SimulatedChatWire.A(d[n..], f < _t.ModeratorRatio ? "\",\"isChatModerator\":true}}" : "\",\"isChatModerator\":false}}");
        }
        n += SimulatedChatWire.A(d[n..], "]}");
        return n;
    }

    public void Dispose() { }
}
