using System.Buffers.Text;
using System.Text;
using System.Threading.Channels;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.Publisher;

namespace PromptFlowStudio.Services.Autopilot;

public enum CreatorAlertKind : byte { Failover, Recovered, EmergencyAdlib, ReelPublished, QuotaExhausted, UploadFailed, AutopilotStage, Info }
public enum AlertSeverity : byte { Info, Success, Warning, Critical }
public enum WebhookKind : byte { Discord, Telegram }

/// <summary>v17.73.0 알림 이벤트 슬롯(풀 · 본문 = 슬롯 char).</summary>
public struct CreatorAlert
{
    public long AtMs;
    public CreatorAlertKind Kind;
    public AlertSeverity Severity;
    public int TextLen, ReelId;
    public double Value;
}

/// <summary>
/// [PromptFlow Studio v17.73.0] 📣 크리에이터 원격 텔레메트리 &amp; 웹훅 알림 디스패처.
///   ▸ 생산(<see cref="Publish"/> · 방송 스레드 · 무할당): 이벤트 → 고정 슬롯 풀(128 · char 본문 200) → <see cref="Channel"/>(유계 · SingleReader · 슬롯 인덱스 int) 비차단 TryWrite ·
///     포화 시 드롭 계수(방송 루프 차단 0) · 동일 종류 2 s 내 중복 병합.
///   ▸ 소비(백그라운드 1 태스크): Discord(<c>{"username","embeds":[{"title","description","color","timestamp"}]}</c>) · Telegram(<c>sendMessage {"chat_id","text","parse_mode":"HTML"}</c>)
///     JSON 을 재사용 바이트 버퍼에 직접 조립(이스케이프) → <see cref="IGraphTransport"/>(실 HTTPS · 시뮬레이터) 전송 · 429 Retry-After/5xx 지수 백오프(3 회) ·
///     목적지별 최소 간격(Discord 2.1 s · Telegram 1.05 s) · 10 s 타임아웃.
///   ▸ 시크릿: 웹훅 URL · 봇 토큰 = <see cref="GraphSecretSlot"/>(AES-GCM 메모리 암호문) · 기본 환경변수 <c>PROMPTFLOW_SECRET_DISCORD_WEBHOOK</c> · <c>…_TELEGRAM_BOT_TOKEN</c> · <c>…_TELEGRAM_CHAT_ID</c>.
/// </summary>
public sealed class RemoteCreatorTelemetryDispatcher : IDisposable
{
    public const int Slots = 128, TextCap = 200, BodyCap = 4096, MaxRetries = 3;
    public const long DedupMs = 2_000, DiscordGapMs = 2_100, TelegramGapMs = 1_050;

    private readonly CreatorAlert[] _ev = new CreatorAlert[Slots];
    private readonly char[] _text = new char[Slots * TextCap];
    private readonly int[] _free = new int[Slots];
    private readonly long[] _lastKind = new long[8];
    private readonly Channel<int> _ch;
    private readonly GraphSecretSlot _discord = new(), _telegramToken = new(), _telegramChat = new();
    private readonly GraphCall _call = new();
    private readonly byte[] _body = new byte[BodyCap], _tmp = new byte[GraphSecretSlot.Cap];
    private readonly long[] _lastSend = new long[2];
    private readonly object _freeGate = new();
    private int _freeN;
    private Task? _pump;
    private CancellationTokenSource? _cts;

    public RemoteCreatorTelemetryDispatcher(IGraphTransport? transport = null)
    {
        for (var i = 0; i < Slots; i++) _free[i] = Slots - 1 - i;
        _freeN = Slots;
        _ch = Channel.CreateBounded<int>(new BoundedChannelOptions(Slots) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        Transport = transport ?? new HttpGraphTransport();
        var env = static (string k) => Environment.GetEnvironmentVariable("PROMPTFLOW_SECRET_" + k);
        if (env("DISCORD_WEBHOOK") is { Length: > 0 } d) SetDiscord(d);
        if (env("TELEGRAM_BOT_TOKEN") is { Length: > 0 } t && env("TELEGRAM_CHAT_ID") is { Length: > 0 } c) SetTelegram(t, c);
    }

    public IGraphTransport Transport { get; set; }
    public Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    public bool DiscordEnabled => _discord.IsSet;
    public bool TelegramEnabled => _telegramToken.IsSet && _telegramChat.IsSet;
    public string DiscordMasked { get; private set; } = "—";
    public string TelegramMasked { get; private set; } = "—";
    public string StationName { get; set; } = "PromptFlow 24/7 Station";
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Info;
    public long Published => Interlocked.Read(ref _published);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Merged => Interlocked.Read(ref _merged);
    public long Sent { get; private set; }
    public long Failed { get; private set; }
    public long Retries { get; private set; }
    public long RateLimited { get; private set; }
    public double LastPublishUs { get; private set; }
    public double MaxPublishUs { get; private set; }
    public string? LastError { get; private set; }
    public int Pending => _ch.Reader.Count;
    private long _published, _dropped, _merged;

    public static string KindName(CreatorAlertKind k) => k switch
    {
        CreatorAlertKind.Failover => "🛟 MCR 페일오버", CreatorAlertKind.Recovered => "✅ 방송 복구", CreatorAlertKind.EmergencyAdlib => "🎬 쇼러너 비상 애드리브", CreatorAlertKind.ReelPublished => "🚀 릴스 업로드 성공",
        CreatorAlertKind.QuotaExhausted => "⛔ API 쿼터 소진", CreatorAlertKind.UploadFailed => "⚠ 업로드 실패", CreatorAlertKind.AutopilotStage => "🤖 오토파일럿", _ => "ℹ 알림",
    };

    private static int Color(AlertSeverity s) => s switch { AlertSeverity.Success => 0x22C55E, AlertSeverity.Warning => 0xF59E0B, AlertSeverity.Critical => 0xEF4444, _ => 0x38BDF8 };

    // ══════════════════════ 설정(비핫패스) ══════════════════════
    public bool SetDiscord(string webhookUrl)
    {
        var u = webhookUrl.Trim();
        if (!(u.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal) || u.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.Ordinal) || u.StartsWith("https://sim.local/", StringComparison.Ordinal)) || u.Length > GraphSecretSlot.Cap)
        { LastError = "Discord 웹훅 URL 형식 오류"; return false; }
        _discord.Set(u.AsSpan()); DiscordMasked = u[..Math.Min(36, u.Length)] + "…" + u[^4..];
        return true;
    }

    public bool SetTelegram(string botToken, string chatId)
    {
        var t = botToken.Trim(); var c = chatId.Trim();
        if (t.Length < 20 || t.IndexOf(':') < 0 || c.Length == 0 || t.Length > 128 || c.Length > 64) { LastError = "Telegram 봇 토큰/채팅 ID 형식 오류"; return false; }
        _telegramToken.Set(t.AsSpan()); _telegramChat.Set(c.AsSpan()); TelegramMasked = t[..Math.Min(6, t.Length)] + "…:" + c;
        return true;
    }

    public void ClearDiscord() { _discord.Clear(); DiscordMasked = "—"; }
    public void ClearTelegram() { _telegramToken.Clear(); _telegramChat.Clear(); TelegramMasked = "—"; }

    // ══════════════════════ 생산(방송 스레드 · 무할당 · 비차단) ══════════════════════
    public bool Publish(CreatorAlertKind kind, AlertSeverity severity, ReadOnlySpan<char> text, double value = 0, int reelId = 0)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (severity < MinSeverity) return false;
            var now = Clock();
            ref var last = ref _lastKind[(int)kind & 7];
            if (kind != CreatorAlertKind.ReelPublished && now - Volatile.Read(ref last) < DedupMs) { Interlocked.Increment(ref _merged); return false; }
            Volatile.Write(ref last, now);
            int slot;
            lock (_freeGate) { if (_freeN == 0) { Interlocked.Increment(ref _dropped); return false; } slot = _free[--_freeN]; }
            ref var e = ref _ev[slot];
            e.AtMs = now; e.Kind = kind; e.Severity = severity; e.Value = value; e.ReelId = reelId;
            e.TextLen = Math.Min(text.Length, TextCap); text[..e.TextLen].CopyTo(_text.AsSpan(slot * TextCap));
            if (!_ch.Writer.TryWrite(slot)) { Release(slot); Interlocked.Increment(ref _dropped); return false; }
            Interlocked.Increment(ref _published);
            return true;
        }
        finally
        {
            var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            LastPublishUs = us; if (us > MaxPublishUs && Published > 2) MaxPublishUs = us;
        }
    }

    private void Release(int slot) { lock (_freeGate) _free[_freeN++] = slot; }

    // ══════════════════════ 소비(백그라운드) ══════════════════════
    public void Start()
    {
        if (_pump is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _pump = Task.Run(() => PumpAsync(ct));
    }

    public async Task StopAsync()
    {
        var c = _cts; _cts = null;
        if (c is null) return;
        c.Cancel();
        try { if (_pump is not null) await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        c.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (await _ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_ch.Reader.TryRead(out var slot))
            {
                try { await DeliverAsync(slot, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Release(slot); throw; }
                catch (Exception ex) { LastError = ex.Message; Failed++; PromptFlowStudio.Program.Log("CreatorTelemetry", ex); }
                Release(slot);
            }
    }

    /// <summary>슬롯 1 개를 활성 목적지 전부로 전송(재시도 포함) — 테스트 동기 경로 겸용.</summary>
    public async Task DeliverAsync(int slot, CancellationToken ct = default)
    {
        if (DiscordEnabled) await SendAsync(WebhookKind.Discord, slot, ct).ConfigureAwait(false);
        if (TelegramEnabled) await SendAsync(WebhookKind.Telegram, slot, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(WebhookKind kind, int slot, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var gap = (kind == WebhookKind.Discord ? DiscordGapMs : TelegramGapMs) - (Clock() - _lastSend[(int)kind]);
            if (gap > 0 && Transport is HttpGraphTransport) await Task.Delay((int)gap, ct).ConfigureAwait(false);
            Build(kind, slot);
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(10_000);
            _call.Send(Transport, to.Token);
            int status;
            while (!_call.TryComplete(out status)) await Task.Delay(2, ct).ConfigureAwait(false);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_body);
            _lastSend[(int)kind] = Clock();
            if (status is >= 200 and < 300) { Sent++; return; }
            if (status == 429) RateLimited++;
            if (status is 429 or 0 or >= 500 && attempt < MaxRetries)
            {
                Retries++;
                var wait = Math.Max(_call.Resp.RetryAfterSec * 1000, 250 << attempt);
                await Task.Delay(Transport is HttpGraphTransport ? wait : 1, ct).ConfigureAwait(false);
                continue;
            }
            Failed++; LastError = kind + " HTTP " + status;
            return;
        }
    }

    /// <summary>목적지별 URL · JSON 본문 조립(재사용 버퍼 · 무할당).</summary>
    private void Build(WebhookKind kind, int slot)
    {
        ref readonly var e = ref _ev[slot];
        var text = _text.AsSpan(slot * TextCap, e.TextLen);
        _call.Method = GraphHttpMethod.Post; _call.UrlLen = 0; _call.HdrLen = 0; _call.UrlHasSecret = true;
        GraphBytes.Put(_call.Hdr, ref _call.HdrLen, "Content-Type: application/json\r\n"u8);
        var b = _body.AsSpan(); var n = 0;
        if (kind == WebhookKind.Discord)
        {
            _call.UrlLen = _discord.CopyTo(_call.Url);
            GraphBytes.Put(b, ref n, "{\"username\":"u8); MultipartBodyWriter.JsonString(b, ref n, StationName);
            GraphBytes.Put(b, ref n, ",\"embeds\":[{\"title\":"u8); MultipartBodyWriter.JsonString(b, ref n, KindName(e.Kind));
            GraphBytes.Put(b, ref n, ",\"description\":"u8); MultipartBodyWriter.JsonString(b, ref n, text);
            GraphBytes.Put(b, ref n, ",\"color\":"u8); GraphBytes.Put(b, ref n, (long)Color(e.Severity));
            if (e.ReelId > 0) { GraphBytes.Put(b, ref n, ",\"footer\":{\"text\":\"reel #"u8); GraphBytes.Put(b, ref n, (long)e.ReelId); GraphBytes.Put(b, ref n, "\"}"u8); }
            GraphBytes.Put(b, ref n, "}]}"u8);
        }
        else
        {
            GraphBytes.Put(_call.Url, ref _call.UrlLen, "https://api.telegram.org/bot"u8);
            _call.UrlLen += _telegramToken.CopyTo(_call.Url.AsSpan(_call.UrlLen));
            GraphBytes.Put(_call.Url, ref _call.UrlLen, "/sendMessage"u8);
            var k = _telegramChat.CopyTo(_tmp);
            GraphBytes.Put(b, ref n, "{\"chat_id\":\""u8); GraphBytes.Put(b, ref n, _tmp.AsSpan(0, k)); System.Security.Cryptography.CryptographicOperations.ZeroMemory(_tmp.AsSpan(0, k));
            GraphBytes.Put(b, ref n, "\",\"parse_mode\":\"HTML\",\"disable_web_page_preview\":true,\"text\":"u8);
            // <b>제목</b>\n본문 — HTML 특수문자 이스케이프 후 JSON 이스케이프
            Span<char> tmp = stackalloc char[TextCap * 2 + 64];
            var tn = 0;
            "<b>".AsSpan().CopyTo(tmp); tn += 3;
            tn += HtmlEscape(KindName(e.Kind), tmp[tn..]);
            "</b>\n".AsSpan().CopyTo(tmp[tn..]); tn += 5;
            tn += HtmlEscape(text, tmp[tn..]);
            MultipartBodyWriter.JsonString(b, ref n, tmp[..tn]);
            b[n++] = (byte)'}';
        }
        _call.Body = _body.AsMemory(0, n);
    }

    private static int HtmlEscape(ReadOnlySpan<char> s, Span<char> d)
    {
        var n = 0;
        foreach (var c in s)
        {
            ReadOnlySpan<char> r = c switch { '<' => "&lt;", '>' => "&gt;", '&' => "&amp;", _ => default };
            if (r.IsEmpty) { if (n < d.Length) d[n++] = c; }
            else if (n + r.Length <= d.Length) { r.CopyTo(d[n..]); n += r.Length; }
        }
        return n;
    }

    /// <summary>대기 슬롯을 즉시 전송(테스트 · 종료 시 플러시) — 반환 = 처리 수.</summary>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        var n = 0;
        while (_ch.Reader.TryRead(out var slot)) { try { await DeliverAsync(slot, ct).ConfigureAwait(false); } finally { Release(slot); } n++; }
        return n;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { PromptFlowStudio.Program.Log("CreatorTelemetry.Dispose", ex); }
        _discord.Dispose(); _telegramToken.Dispose(); _telegramChat.Dispose();
        (Transport as IDisposable)?.Dispose();
    }
}

/// <summary>v17.73.0 Discord/Telegram 웹훅 시뮬레이터(동기 완료 · JSON 수신 기록 · 429 주입).</summary>
public sealed class SimulatedWebhookServer : IGraphTransport
{
    public readonly List<string> Discord = new(), Telegram = new();
    public int RateLimitNext { get; set; }
    public long Requests { get; private set; }
    public string? LastUrl { get; private set; }

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
    {
        Requests++;
        resp.Length = 0;
        var u = url.Span;
        LastUrl = Encoding.UTF8.GetString(u);
        if (RateLimitNext > 0) { RateLimitNext--; resp.RetryAfterSec = 1; return new(429); }
        if (GraphBytes.Header(headers.Span, "Content-Type"u8).IndexOf("application/json"u8) < 0) return new(415);
        var json = Encoding.UTF8.GetString(body.Span);
        if (u.StartsWith("https://api.telegram.org/bot"u8) && u.EndsWith("/sendMessage"u8)) { lock (Telegram) Telegram.Add(json); return new(200); }
        if (u.StartsWith("https://discord.com/api/webhooks/"u8) || u.StartsWith("https://sim.local/"u8)) { lock (Discord) Discord.Add(json); return new(204); }
        return new(404);
    }
}
