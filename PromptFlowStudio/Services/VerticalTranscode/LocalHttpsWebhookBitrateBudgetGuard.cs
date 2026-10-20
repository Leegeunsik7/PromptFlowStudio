using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace PromptFlowStudio.Services.VerticalTranscode;

public enum WebhookHttpStatus : short { NeedMore = 0, Ok = 200, BadRequest = 400, Unauthorized = 401, Forbidden = 403, NotFound = 404, MethodNotAllowed = 405, LengthRequired = 411, PayloadTooLarge = 413, HeadersTooLarge = 431, NotImplemented = 501 }
public enum WebhookRequestKind : byte { None, Challenge, Notification, Other }

/// <summary>HTTP/1.1 요청 헤더 판독 결과(오프셋 · 값 형식).</summary>
public struct WebhookRequest
{
    public WebhookRequestKind Kind;
    public int PathStart, PathLen, QueryStart, QueryLen, SigStart, SigLen, HeaderEnd, ContentLength;
    public bool Chunked;
}

/// <summary>수신 로그 1건(값 형식).</summary>
public struct WebhookLogEntry
{
    public long UnixMs;
    public WebhookRequestKind Kind;
    public short Status;
    public int Bytes;
    public bool Tls;
}

/// <summary>
/// [PromptFlow Studio v17.49.0] 🎞️ 비트레이트 버짓 가드 &amp; 로컬 웹훅 요청 판독기(<see cref="OnFrame"/> · <see cref="ParseHead"/> · <see cref="Respond"/> 무할당).
/// <b>비트레이트</b>: 최근 1 초(fps 슬롯 링) 인코딩 바이트로 kbps 를 측정해 릴스 권장 상한 8 Mbps 의 95% 를 넘으면 QP +2(관리 코드 인코더) /
/// 목표 비트레이트 ×0.85(MF 인코더), 목표 초과 시 QP +1, 목표 70% 미만이면 QP −1 로 완화한다. 인코딩 지연이 프레임 예산을 3 회 연속 넘으면
/// 다음 프레임을 P_Skip 반복으로 대체(프레임 드롭 방어) 하고 QP 를 올린다. QP 는 [<see cref="MinQp"/>, <see cref="MaxQp"/>] 로 클램프.
/// <b>웹훅</b>: Meta 구독 검증(GET hub.mode=subscribe · hub.verify_token · hub.challenge) 과 알림(POST · X-Hub-Signature-256)을
/// 헤더 8 KB · 본문 64 KB 상한(431/413) · Content-Length 필수(411) · chunked 거부(501) 로 판독한다. challenge 는 영숫자 64 자 이하만 반사.
/// </summary>
public sealed class LocalHttpsWebhookBitrateBudgetGuard
{
    public const int CapKbps = 8000, MaxHeader = 8192, MaxBody = 64 * 1024, MinQp = 18, MaxQp = 46, LateStreak = 3;
    private readonly int[] _bytes = new int[120];
    private int _head, _count, _late, _fps = 30;
    private long _sum;

    public int TargetKbps { get; set; } = 6000;
    public int Qp { get; private set; } = 30;
    public int MfKbps { get; private set; } = 6000;
    public double WindowKbps { get; private set; }
    public double PeakKbps { get; private set; }
    public double LastEncodeMs { get; private set; }
    public double Occupancy { get; private set; }
    public long OverCap { get; private set; }
    public long QpRaises { get; private set; }
    public long QpLowers { get; private set; }
    public long LateFrames { get; private set; }
    public long Drops { get; private set; }
    public bool DropNext { get; private set; }

    public void Reset(int fps, int targetKbps, int qp)
    {
        _fps = Math.Clamp(fps, 1, _bytes.Length); TargetKbps = Math.Clamp(targetKbps, 500, CapKbps); MfKbps = TargetKbps; Qp = Math.Clamp(qp, MinQp, MaxQp);
        Array.Clear(_bytes); _head = 0; _count = 0; _sum = 0; _late = 0;
        WindowKbps = 0; PeakKbps = 0; OverCap = 0; QpRaises = 0; QpLowers = 0; LateFrames = 0; Drops = 0; DropNext = false;
    }

    /// <summary>프레임 1개 결과 반영(무할당) — 다음 프레임의 QP · MF 비트레이트 · 드롭 여부를 갱신.</summary>
    public void OnFrame(int bytes, double encodeMs, bool skipped)
    {
        _sum += bytes - _bytes[_head];
        _bytes[_head] = bytes; _head = (_head + 1) % _fps;
        if (_count < _fps) _count++;
        WindowKbps = _sum * 8.0 * _fps / Math.Max(_count, 1) / 1000.0;
        if (_count == _fps && WindowKbps > PeakKbps) PeakKbps = WindowKbps;
        var budget = 1000.0 / _fps;
        LastEncodeMs = encodeMs; Occupancy = 100.0 * encodeMs / budget;
        if (skipped) { Drops++; DropNext = false; }
        else if (encodeMs > budget) { LateFrames++; if (++_late >= LateStreak) { DropNext = true; _late = 0; Raise(1); } }
        else _late = 0;
        if (_count < Math.Max(_fps / 4, 1)) return;
        if (WindowKbps > CapKbps * 0.95) { OverCap++; Raise(2); MfKbps = Math.Max(500, (int)(MfKbps * 0.85)); }
        else if (WindowKbps > TargetKbps) { Raise(1); MfKbps = Math.Max(500, (int)(MfKbps * 0.95)); }
        else if (WindowKbps < TargetKbps * 0.7 && Qp > MinQp) { Qp--; QpLowers++; MfKbps = Math.Min(TargetKbps, (int)(MfKbps * 1.05) + 1); }
    }

    private void Raise(int d) { var q = Math.Min(Qp + d, MaxQp); if (q != Qp) { Qp = q; QpRaises++; } }

    // ══════════════════════ HTTP 판독(무할당) ══════════════════════
    /// <summary>헤더 영역 판독 — NeedMore(헤더 미완) · Ok(유효) · 오류 상태.</summary>
    public static WebhookHttpStatus ParseHead(ReadOnlySpan<byte> buf, out WebhookRequest req)
    {
        req = default; req.ContentLength = -1;
        var end = buf.IndexOf("\r\n\r\n"u8);
        if (end < 0) return buf.Length > MaxHeader ? WebhookHttpStatus.HeadersTooLarge : WebhookHttpStatus.NeedMore;
        if (end + 4 > MaxHeader) return WebhookHttpStatus.HeadersTooLarge;
        req.HeaderEnd = end + 4;
        var lineEnd = buf.IndexOf("\r\n"u8);
        var line = buf[..lineEnd];
        var sp1 = line.IndexOf((byte)' ');
        if (sp1 <= 0) return WebhookHttpStatus.BadRequest;
        var method = line[..sp1];
        var rest = line[(sp1 + 1)..];
        var sp2 = rest.IndexOf((byte)' ');
        if (sp2 <= 0 || !rest[(sp2 + 1)..].StartsWith("HTTP/1."u8)) return WebhookHttpStatus.BadRequest;
        var target = rest[..sp2];
        var q = target.IndexOf((byte)'?');
        req.PathStart = sp1 + 1; req.PathLen = q < 0 ? target.Length : q;
        if (q >= 0) { req.QueryStart = sp1 + 2 + q; req.QueryLen = target.Length - q - 1; }
        var get = method.SequenceEqual("GET"u8); var post = method.SequenceEqual("POST"u8);
        if (!get && !post) return WebhookHttpStatus.MethodNotAllowed;
        var pos = lineEnd + 2;
        while (pos < end)
        {
            var e = buf[pos..].IndexOf("\r\n"u8);
            var h = buf.Slice(pos, e);
            var c = h.IndexOf((byte)':');
            if (c <= 0) return WebhookHttpStatus.BadRequest;
            var name = h[..c]; var value = h[(c + 1)..].Trim((byte)' ');
            if (System.Text.Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
            {
                if (!Utf8Parser.TryParse(value, out int cl, out var used) || used != value.Length || cl < 0) return WebhookHttpStatus.BadRequest;
                req.ContentLength = cl;
            }
            else if (System.Text.Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8)) req.Chunked = true;
            else if (System.Text.Ascii.EqualsIgnoreCase(name, "X-Hub-Signature-256"u8)) { req.SigStart = (int)(pos + c + 1 + (h.Length - c - 1 - h[(c + 1)..].TrimStart((byte)' ').Length)); req.SigLen = value.Length; }
            pos += e + 2;
        }
        if (post)
        {
            if (req.Chunked) return WebhookHttpStatus.NotImplemented;
            if (req.ContentLength < 0) return WebhookHttpStatus.LengthRequired;
            if (req.ContentLength > MaxBody) return WebhookHttpStatus.PayloadTooLarge;
            req.Kind = WebhookRequestKind.Notification;
        }
        else req.Kind = req.QueryLen > 0 ? WebhookRequestKind.Challenge : WebhookRequestKind.Other;
        return WebhookHttpStatus.Ok;
    }

    /// <summary>쿼리 파라미터 값(퍼센트 · '+' 해제)을 dst 에 기록 — 길이(없으면 −1).</summary>
    public static int QueryValue(ReadOnlySpan<byte> query, ReadOnlySpan<byte> key, Span<byte> dst)
    {
        while (!query.IsEmpty)
        {
            var amp = query.IndexOf((byte)'&');
            var kv = amp < 0 ? query : query[..amp];
            query = amp < 0 ? default : query[(amp + 1)..];
            var eq = kv.IndexOf((byte)'=');
            var k = eq < 0 ? kv : kv[..eq];
            if (!k.SequenceEqual(key)) continue;
            var v = eq < 0 ? default : kv[(eq + 1)..];
            var n = 0;
            for (var i = 0; i < v.Length; i++)
            {
                if (n >= dst.Length) return -1;
                if (v[i] == '%' && i + 2 < v.Length && Hex(v[i + 1]) >= 0 && Hex(v[i + 2]) >= 0) { dst[n++] = (byte)(Hex(v[i + 1]) << 4 | Hex(v[i + 2])); i += 2; }
                else dst[n++] = v[i] == '+' ? (byte)' ' : v[i];
            }
            return n;
        }
        return -1;
    }

    private static int Hex(byte c) => c is >= (byte)'0' and <= (byte)'9' ? c - '0' : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10 : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;

    /// <summary>
    /// 완전한 요청(헤더 + 본문) 처리 → 응답 바이트 기록. verify 는 구독 검증 토큰(평문 · 호출자 제로화), notify 는 서명 검증 · 적재 콜백(≥0 성공 · −1 서명 불일치).
    /// </summary>
    public static int Respond(ReadOnlySpan<byte> buf, in WebhookRequest req, ReadOnlySpan<byte> verify, ReadOnlySpan<byte> path, WebhookNotify? notify, Span<byte> dst, out WebhookHttpStatus status)
    {
        Span<byte> mode = stackalloc byte[32];
        Span<byte> tok = stackalloc byte[256];
        Span<byte> ch = stackalloc byte[128];
        status = WebhookHttpStatus.NotFound;
        var reqPath = buf.Slice(req.PathStart, req.PathLen);
        var body = default(ReadOnlySpan<byte>);
        if (req.Kind == WebhookRequestKind.Notification) body = buf.Slice(req.HeaderEnd, req.ContentLength);
        if (!reqPath.SequenceEqual(path)) return Write(dst, status, default);
        if (req.Kind == WebhookRequestKind.Challenge)
        {
            var query = buf.Slice(req.QueryStart, req.QueryLen);
            var ml = QueryValue(query, "hub.mode"u8, mode); var tl = QueryValue(query, "hub.verify_token"u8, tok); var cl = QueryValue(query, "hub.challenge"u8, ch);
            var ok = ml > 0 && mode[..ml].SequenceEqual("subscribe"u8) && tl > 0 && verify.Length > 0 && CryptographicOperations.FixedTimeEquals(tok[..tl], verify) && cl is > 0 and <= 64;
            if (ok) foreach (var c in ch[..cl]) if (!(c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or (byte)'-')) { ok = false; break; }
            CryptographicOperations.ZeroMemory(tok);
            status = ok ? WebhookHttpStatus.Ok : WebhookHttpStatus.Forbidden;
            return Write(dst, status, ok ? ch[..cl] : default);
        }
        if (req.Kind == WebhookRequestKind.Notification)
        {
            if (req.SigLen == 0 || notify is null) { status = WebhookHttpStatus.Unauthorized; return Write(dst, status, default); }
            var k = notify(body, buf.Slice(req.SigStart, req.SigLen));
            status = k >= 0 ? WebhookHttpStatus.Ok : WebhookHttpStatus.Forbidden;
            return Write(dst, status, k >= 0 ? "EVENT_RECEIVED"u8 : default);
        }
        status = WebhookHttpStatus.BadRequest;
        return Write(dst, status, default);
    }

    /// <summary>HTTP/1.1 응답(text/plain · Connection: close).</summary>
    public static int Write(Span<byte> dst, WebhookHttpStatus status, ReadOnlySpan<byte> body)
    {
        var n = 0;
        P(dst, ref n, "HTTP/1.1 "u8);
        Utf8Formatter.TryFormat((int)status, dst[n..], out var w); n += w;
        P(dst, ref n, status switch
        {
            WebhookHttpStatus.Ok => " OK"u8,
            WebhookHttpStatus.BadRequest => " Bad Request"u8,
            WebhookHttpStatus.Unauthorized => " Unauthorized"u8,
            WebhookHttpStatus.Forbidden => " Forbidden"u8,
            WebhookHttpStatus.NotFound => " Not Found"u8,
            WebhookHttpStatus.MethodNotAllowed => " Method Not Allowed"u8,
            WebhookHttpStatus.LengthRequired => " Length Required"u8,
            WebhookHttpStatus.PayloadTooLarge => " Payload Too Large"u8,
            WebhookHttpStatus.HeadersTooLarge => " Request Header Fields Too Large"u8,
            _ => " Not Implemented"u8,
        });
        P(dst, ref n, "\r\nContent-Type: text/plain\r\nConnection: close\r\nContent-Length: "u8);
        Utf8Formatter.TryFormat(body.Length, dst[n..], out w); n += w;
        P(dst, ref n, "\r\n\r\n"u8);
        P(dst, ref n, body);
        return n;
    }

    private static void P(Span<byte> d, ref int n, ReadOnlySpan<byte> s) { s.CopyTo(d[n..]); n += s.Length; }
}

/// <summary>서명 검증 · 적재 콜백 — ≥ 0 성공, −1 서명 불일치.</summary>
public delegate int WebhookNotify(ReadOnlySpan<byte> body, ReadOnlySpan<byte> signature);

/// <summary>
/// 경량 로컬 웹훅 리스너(TcpListener + 선택적 SslStream · 자체 서명 인증서). 연결당 요청 1건 · 동시 16 연결 · 5 초 판독 기한(slowloris 방어) ·
/// 버퍼 ArrayPool 대여(헤더 8 KB + 본문 64 KB 상한). Meta 는 공인 CA HTTPS 만 호출하므로 실 운영은 터널(ngrok/cloudflared 등)이 TLS 를 종단하고
/// 이 리스너(HTTP 또는 자체 서명 HTTPS)로 전달하는 구성을 전제로 한다.
/// </summary>
public sealed class LocalWebhookListener : IDisposable
{
    public const int MaxConnections = 16, ReadTimeoutMs = 5000, LogCap = 64;
    private readonly WebhookLogEntry[] _log = new WebhookLogEntry[LogCap];
    private readonly byte[] _verify = new byte[256];
    private readonly byte[] _path;
    private readonly WebhookNotify? _notify;
    private readonly object _gate = new();
    private int _verifyLen, _active, _logHead, _logCount;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private X509Certificate2? _cert;

    public LocalWebhookListener(WebhookNotify? notify, string path = "/webhook")
    {
        _notify = notify;
        _path = System.Text.Encoding.ASCII.GetBytes(path);
    }

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }
    public bool Tls { get; private set; }
    public string Path => System.Text.Encoding.ASCII.GetString(_path);
    public string? CertThumbprint => _cert?.Thumbprint;
    public DateTime? CertNotAfter => _cert?.NotAfter;
    public long Requests { get; private set; }
    public long Challenges { get; private set; }
    public long Notifications { get; private set; }
    public long Rejected { get; private set; }
    public long Overflows { get; private set; }
    public long Timeouts { get; private set; }
    public long Busy { get; private set; }
    public string? LastError { get; private set; }
    public int LogCount => _logCount;
    public WebhookLogEntry Log(int i) { lock (_gate) return _log[(_logHead - 1 - i + 2 * LogCap) % LogCap]; }

    public void SetVerifyToken(ReadOnlySpan<char> token)
    {
        lock (_gate)
        {
            CryptographicOperations.ZeroMemory(_verify);
            _verifyLen = System.Text.Encoding.UTF8.GetBytes(token[..Math.Min(token.Length, 64)], _verify);
        }
    }

    /// <summary>자체 서명 인증서(ECDSA P-256 · CN=localhost · SAN 127.0.0.1/localhost · 90 일) 생성.</summary>
    public static X509Certificate2 CreateSelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var tmp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90));
        return new X509Certificate2(tmp.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>127.0.0.1:port 바인딩(0 = 임의 포트). tls = 자체 서명 HTTPS.</summary>
    public bool Start(int port, bool tls)
    {
        Stop();
        try
        {
            if (tls) _cert ??= CreateSelfSigned();
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start(MaxConnections);
            Port = ((IPEndPoint)l.LocalEndpoint).Port; Tls = tls;
            var cts = new CancellationTokenSource();
            _cts = cts; _listener = l;
            _ = Task.Run(() => AcceptLoop(l, cts.Token));
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or CryptographicException or PlatformNotSupportedException)
        {
            LastError = ex.GetType().Name;
            PromptFlowStudio.Program.Log("Webhook.Start", ex);
            return false;
        }
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is not null) { c.Cancel(); c.Dispose(); }
        var l = Interlocked.Exchange(ref _listener, null);
        try { l?.Stop(); } catch (SocketException) { }
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { PromptFlowStudio.Program.Log("Webhook.Accept", ex); await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); continue; }
            if (Interlocked.Increment(ref _active) > MaxConnections) { Interlocked.Decrement(ref _active); Busy++; c.Dispose(); continue; }
            _ = Task.Run(() => Serve(c, ct));
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(LocalHttpsWebhookBitrateBudgetGuard.MaxHeader + LocalHttpsWebhookBitrateBudgetGuard.MaxBody);
        var resp = ArrayPool<byte>.Shared.Rent(1024);
        var tls = Tls;
        Stream? s = null;
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(ReadTimeoutMs);
            client.NoDelay = true;
            s = client.GetStream();
            if (tls && _cert is { } cert)
            {
                var ssl = new SslStream(s, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, ClientCertificateRequired = false }, to.Token).ConfigureAwait(false);
                s = ssl;
            }
            var n = 0; WebhookRequest req; WebhookHttpStatus st;
            while (true)
            {
                st = LocalHttpsWebhookBitrateBudgetGuard.ParseHead(buf.AsSpan(0, n), out req);
                if (st != WebhookHttpStatus.NeedMore) break;
                if (n >= LocalHttpsWebhookBitrateBudgetGuard.MaxHeader + 1) { st = WebhookHttpStatus.HeadersTooLarge; break; }
                var k = await s.ReadAsync(buf.AsMemory(n, LocalHttpsWebhookBitrateBudgetGuard.MaxHeader + 1 - n), to.Token).ConfigureAwait(false);
                if (k == 0) return;
                n += k;
            }
            int len;
            if (st == WebhookHttpStatus.Ok && req.Kind == WebhookRequestKind.Notification)
            {
                var need = req.HeaderEnd + req.ContentLength;
                while (n < need)
                {
                    var k = await s.ReadAsync(buf.AsMemory(n, need - n), to.Token).ConfigureAwait(false);
                    if (k == 0) return;
                    n += k;
                }
            }
            if (st == WebhookHttpStatus.Ok) len = Handle(buf, n, in req, resp, out st);
            else { len = LocalHttpsWebhookBitrateBudgetGuard.Write(resp, st, default); if (st is WebhookHttpStatus.HeadersTooLarge or WebhookHttpStatus.PayloadTooLarge) Overflows++; }
            await s.WriteAsync(resp.AsMemory(0, len), to.Token).ConfigureAwait(false);
            await s.FlushAsync(to.Token).ConfigureAwait(false);
            Record(req.Kind, st, n, tls);
        }
        catch (OperationCanceledException) { Timeouts++; Record(WebhookRequestKind.None, WebhookHttpStatus.BadRequest, 0, tls); }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException) { LastError = ex.GetType().Name; }
        finally
        {
            try { s?.Dispose(); } catch (IOException) { }
            client.Dispose();
            CryptographicOperations.ZeroMemory(buf.AsSpan(0, Math.Min(buf.Length, LocalHttpsWebhookBitrateBudgetGuard.MaxHeader + LocalHttpsWebhookBitrateBudgetGuard.MaxBody)));
            ArrayPool<byte>.Shared.Return(buf); ArrayPool<byte>.Shared.Return(resp);
            Interlocked.Decrement(ref _active);
        }
    }

    private int Handle(byte[] buf, int n, in WebhookRequest req, byte[] resp, out WebhookHttpStatus st)
    {
        Span<byte> verify = stackalloc byte[256];
        int vl;
        lock (_gate) { vl = _verifyLen; _verify.AsSpan(0, vl).CopyTo(verify); }
        try { return LocalHttpsWebhookBitrateBudgetGuard.Respond(buf.AsSpan(0, n), in req, verify[..vl], _path, _notify, resp, out st); }
        finally { CryptographicOperations.ZeroMemory(verify); }
    }

    private void Record(WebhookRequestKind kind, WebhookHttpStatus st, int bytes, bool tls)
    {
        lock (_gate)
        {
            Requests++;
            if (st == WebhookHttpStatus.Ok) { if (kind == WebhookRequestKind.Challenge) Challenges++; else if (kind == WebhookRequestKind.Notification) Notifications++; }
            else Rejected++;
            ref var e = ref _log[_logHead];
            e.UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); e.Kind = kind; e.Status = (short)st; e.Bytes = bytes; e.Tls = tls;
            _logHead = (_logHead + 1) % LogCap;
            if (_logCount < LogCap) _logCount++;
        }
    }

    public void Dispose()
    {
        Stop();
        _cert?.Dispose(); _cert = null;
        CryptographicOperations.ZeroMemory(_verify);
    }
}
