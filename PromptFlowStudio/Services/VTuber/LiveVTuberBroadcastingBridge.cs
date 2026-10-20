using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.VTuber;

/// <summary>VRM 0.x 프리셋 블렌드셰이프(VMC 프로토콜 이름) 인덱스.</summary>
public static class VrmBlend
{
    public const int A = 0, I = 1, U = 2, E = 3, O = 4, Blink = 5, Joy = 6, Angry = 7, Sorrow = 8, Fun = 9, Surprised = 10, Count = 11;
    public static readonly string[] Names = { "A", "I", "U", "E", "O", "Blink", "Joy", "Angry", "Sorrow", "Fun", "Surprised" };
    internal static readonly byte[][] Utf8 = Encode(Names);

    internal static byte[][] Encode(string[] names)
    {
        var r = new byte[names.Length][];
        for (var i = 0; i < names.Length; i++) r[i] = Encoding.UTF8.GetBytes(names[i]);
        return r;
    }
}

/// <summary>VMC 프로토콜(OSC over UDP) 인코더 — 번들 · Blend/Val · Blend/Apply · Bone/Pos · Root/Pos(무할당 · 빅엔디언 · 4바이트 정렬).</summary>
public static class VmcCodec
{
    public static readonly byte[] BlendVal = "/VMC/Ext/Blend/Val"u8.ToArray(), BlendApply = "/VMC/Ext/Blend/Apply"u8.ToArray(),
        BonePos = "/VMC/Ext/Bone/Pos"u8.ToArray(), RootPos = "/VMC/Ext/Root/Pos"u8.ToArray(), Ok = "/VMC/Ext/OK"u8.ToArray(), Root = "root"u8.ToArray();

    private static int Str(Span<byte> d, int pos, ReadOnlySpan<byte> s)
    {
        var len = Virtual.OscCodec.Pad4(s.Length + 1);
        d.Slice(pos, len).Clear();
        s.CopyTo(d[pos..]);
        return pos + len;
    }

    /// <summary>주소 + (선택 문자열) + float 인자 메시지 → 길이(버퍼 부족 −1).</summary>
    public static int Message(Span<byte> d, ReadOnlySpan<byte> address, ReadOnlySpan<byte> str, ReadOnlySpan<float> args)
    {
        var hasStr = !str.IsEmpty;
        var tags = 1 + (hasStr ? 1 : 0) + args.Length;
        var need = Virtual.OscCodec.Pad4(address.Length + 1) + Virtual.OscCodec.Pad4(tags + 1) + (hasStr ? Virtual.OscCodec.Pad4(str.Length + 1) : 0) + args.Length * 4;
        if (d.Length < need) return -1;
        var pos = Str(d, 0, address);
        var tlen = Virtual.OscCodec.Pad4(tags + 1);
        d.Slice(pos, tlen).Clear();
        d[pos] = (byte)',';
        var k = 1;
        if (hasStr) d[pos + k++] = (byte)'s';
        for (var i = 0; i < args.Length; i++) d[pos + k++] = (byte)'f';
        pos += tlen;
        if (hasStr) pos = Str(d, pos, str);
        for (var i = 0; i < args.Length; i++, pos += 4) BinaryPrimitives.WriteSingleBigEndian(d[pos..], args[i]);
        return pos;
    }

    /// <summary>번들 헤더(#bundle · 즉시 타임태그) → 길이 16.</summary>
    public static int BundleHeader(Span<byte> d)
    {
        "#bundle\0"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt64BigEndian(d[8..], 1);
        return 16;
    }

    /// <summary>번들에 메시지 추가(크기 프리픽스) → 새 위치(부족 −1).</summary>
    public static int Append(Span<byte> d, int pos, ReadOnlySpan<byte> address, ReadOnlySpan<byte> str, ReadOnlySpan<float> args)
    {
        if (pos < 0 || pos + 4 > d.Length) return -1;
        var n = Message(d[(pos + 4)..], address, str, args);
        if (n < 0) return -1;
        BinaryPrimitives.WriteInt32BigEndian(d[pos..], n);
        return pos + 4 + n;
    }

    /// <summary>번들 → (주소, 문자열 인자, float 인자) 열거 콜백(테스트/수신 진단).</summary>
    public static int Parse(ReadOnlySpan<byte> p, Action<string, string?, float[]> onMessage)
    {
        if (!Virtual.OscCodec.IsBundle(p)) return ParseOne(p, onMessage) ? 1 : 0;
        var pos = 16; var count = 0;
        while (pos + 4 <= p.Length)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(p[pos..]);
            if (size <= 0 || pos + 4 + size > p.Length) break;
            if (ParseOne(p.Slice(pos + 4, size), onMessage)) count++;
            pos += 4 + size;
        }
        return count;
    }

    private static bool ParseOne(ReadOnlySpan<byte> m, Action<string, string?, float[]> onMessage)
    {
        var an = m.IndexOf((byte)0);
        if (an <= 0) return false;
        var addr = Encoding.UTF8.GetString(m[..an]);
        var pos = Virtual.OscCodec.Pad4(an + 1);
        var tn = m[pos..].IndexOf((byte)0);
        var tags = m.Slice(pos + 1, tn - 1);
        pos += Virtual.OscCodec.Pad4(tn + 1);
        string? s = null; var args = new List<float>();
        foreach (var t in tags)
        {
            if (t == (byte)'s') { var z = m[pos..].IndexOf((byte)0); s = Encoding.UTF8.GetString(m.Slice(pos, z)); pos += Virtual.OscCodec.Pad4(z + 1); }
            else if (t == (byte)'f') { args.Add(BinaryPrimitives.ReadSingleBigEndian(m[pos..])); pos += 4; }
            else return false;
        }
        onMessage(addr, s, args.ToArray());
        return true;
    }
}

/// <summary>발화 퍼포먼스 — 텍스트 · 감정 · 강도 · 입모양 타임라인 · 길이 · 음성 파일(선택).</summary>
public sealed record SpeechPerformance(string Text, FacialEmotion Emotion, double Intensity, VisemeCue[] Cues, double Duration, string? VoiceFile);

/// <summary>오버레이 큐(SSE/OBS 커스텀 이벤트) — 종류 · VFX · 배너 · 징글 · 길이.</summary>
public sealed record OverlayCue(string Type, string Vfx, string Banner, string? Detail, string? Media, int DurationMs, int Tier);

/// <summary>
/// 단순 JSON WebSocket 채널(BCL ClientWebSocket) — 핸드셰이크 수신 · 송신 in-flight 1슬롯(초과 드롭) · 백그라운드 드레인.
/// </summary>
public sealed class WsJsonChannel : IDisposable
{
    private readonly byte[] _rx = new byte[1 << 16];
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _sending;

    public bool IsOpen => _ws?.State == WebSocketState.Open;
    public long Sent { get; private set; }
    public long Dropped { get; private set; }
    public string? LastReceived { get; private set; }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        Dispose();
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(3));
        await ws.ConnectAsync(uri, d.Token).ConfigureAwait(false);
        _ws = ws;
        _cts = new CancellationTokenSource();
    }

    /// <summary>텍스트 메시지 1건 수신(데드라인 3초).</summary>
    public async Task<JsonDocument> ReceiveAsync(CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("연결 없음");
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(3));
        var total = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(_rx, total, _rx.Length - total), d.Token).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("서버 종료");
            total += r.Count;
            if (r.EndOfMessage) break;
            if (total >= _rx.Length) throw new InvalidDataException("메시지 64KB 초과");
        }
        LastReceived = Encoding.UTF8.GetString(_rx, 0, Math.Min(total, 512));
        return JsonDocument.Parse(_rx.AsMemory(0, total));
    }

    /// <summary>송신(대기 · 핸드셰이크용).</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("연결 없음");
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        Sent++;
    }

    /// <summary>송신 시도(in-flight 중이면 드롭 · 백프레셔). json 은 완료 전까지 호출자가 유지해야 함 → 복사본 사용.</summary>
    public bool TrySend(ReadOnlySpan<byte> json)
    {
        if (!IsOpen) return false;
        if (Interlocked.Exchange(ref _sending, 1) != 0) { Dropped++; return false; }
        var buf = ArrayPool<byte>.Shared.Rent(json.Length);
        json.CopyTo(buf);
        _ = SendPooledAsync(buf, json.Length);
        return true;
    }

    private async Task SendPooledAsync(byte[] buf, int len)
    {
        try { await _ws!.SendAsync(buf.AsMemory(0, len), WebSocketMessageType.Text, true, _cts?.Token ?? default).ConfigureAwait(false); Sent++; }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { Dropped++; }
        finally { ArrayPool<byte>.Shared.Return(buf); Volatile.Write(ref _sending, 0); }
    }

    /// <summary>핸드셰이크 이후 수신 버퍼 드레인(응답 무시 · 마지막 메시지 보관).</summary>
    public void StartDrain()
    {
        var ws = _ws; var cts = _cts;
        if (ws is null || cts is null) return;
        _ = Task.Run(async () =>
        {
            var buf = new byte[8192];
            try
            {
                while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    LastReceived = Encoding.UTF8.GetString(buf, 0, Math.Min(r.Count, 512));
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        });
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        var ws = Interlocked.Exchange(ref _ws, null);
        try { ws?.Abort(); } catch { /* 무시 */ }
        ws?.Dispose();
    }
}

/// <summary>
/// 방송 오버레이 서버(BCL TcpListener · 루프백) — GET /overlay(HTML 캔버스 폭죽/컨페티/하트/스파클 · 배너 · 자막 · 징글 재생) ·
/// GET /events(SSE) · GET /media/{name}(화이트리스트 파일명 · 단일 폴더). OBS 브라우저 소스로 추가.
/// </summary>
public sealed class OverlaySseServer : IDisposable
{
    private readonly object _gate = new();
    private readonly List<NetworkStream> _clients = new();
    private readonly string _mediaRoot;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private readonly string _html;

    public OverlaySseServer(string mediaRoot, string? html = null) { _mediaRoot = mediaRoot; _html = html ?? OverlayHtml; }

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public int Clients { get { lock (_gate) return _clients.Count; } }
    public long Broadcasts { get; private set; }
    public string Url => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/overlay";

    public void Start(int port)
    {
        if (_listener is not null) return;
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = AcceptLoopAsync(l, cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            _ = HandleAsync(c, ct);
        }
    }

    public static bool IsSafeName(string n)
    {
        if (n.Length is 0 or > 80) return false;
        foreach (var ch in n) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')) return false;
        return !n.Contains("..", StringComparison.Ordinal) && (n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
    }

    private async Task HandleAsync(TcpClient c, CancellationToken ct)
    {
        var keep = false;
        var s = c.GetStream();
        try
        {
            var buf = new byte[2048];
            var n = 0;
            using (var d = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                d.CancelAfter(TimeSpan.FromSeconds(5));
                while (n < buf.Length)
                {
                    var r = await s.ReadAsync(buf.AsMemory(n), d.Token).ConfigureAwait(false);
                    if (r == 0) break;
                    n += r;
                    if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
                }
            }
            var head = Encoding.ASCII.GetString(buf, 0, n);
            var sp1 = head.IndexOf(' '); var sp2 = sp1 < 0 ? -1 : head.IndexOf(' ', sp1 + 1);
            if (!head.StartsWith("GET ", StringComparison.Ordinal) || sp2 < 0) { await WriteAsync(s, 405, "text/plain", "method"u8.ToArray(), ct).ConfigureAwait(false); return; }
            var path = head[(sp1 + 1)..sp2];
            var q = path.IndexOf('?'); if (q >= 0) path = path[..q];
            if (path is "/" or "/overlay") { await WriteAsync(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(_html), ct).ConfigureAwait(false); return; }
            if (path == "/events")
            {
                await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n: pfs\n\n"u8.ToArray(), ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_clients.Count >= 16) { _clients[0].Dispose(); _clients.RemoveAt(0); }
                    _clients.Add(s);
                }
                keep = true;
                return;
            }
            if (path.StartsWith("/media/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path[7..]);
                var file = Path.Combine(_mediaRoot, name);
                if (IsSafeName(name) && File.Exists(file))
                {
                    var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                    await WriteAsync(s, 200, name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav", bytes, ct).ConfigureAwait(false);
                    return;
                }
            }
            await WriteAsync(s, 404, "text/plain", "not found"u8.ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception ex) { Program.Log("Overlay.Handle", ex); }
        finally { if (!keep) c.Dispose(); }
    }

    private static async Task WriteAsync(NetworkStream s, int code, string type, byte[] body, CancellationToken ct)
    {
        var head = "HTTP/1.1 " + code.ToString(CultureInfo.InvariantCulture) + (code == 200 ? " OK" : " Error") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture)
                   + "\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>SSE 브로드캐스트(끊긴 클라이언트 제거). 반환: 전송 클라이언트 수.</summary>
    public int Broadcast(string json)
    {
        var payload = Encoding.UTF8.GetBytes("data: " + json.Replace("\n", " ") + "\n\n");
        var sent = 0;
        lock (_gate)
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                try { _clients[i].Write(payload); _clients[i].Flush(); sent++; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _clients[i].Dispose(); _clients.RemoveAt(i); }
            }
        }
        Broadcasts++;
        return sent;
    }

    /// <summary>v16.40.0 사전 직렬화된 SSE 프레임("data: …\n\n" UTF-8) 무할당 브로드캐스트.</summary>
    public int BroadcastRaw(ReadOnlySpan<byte> frame)
    {
        var sent = 0;
        lock (_gate)
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                try { _clients[i].Write(frame); sent++; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _clients[i].Dispose(); _clients.RemoveAt(i); }
            }
        }
        Broadcasts++;
        return sent;
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        try { _listener?.Stop(); } catch { /* 무시 */ }
        _listener = null;
        lock (_gate) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
    }

    public const string OverlayHtml = """
<!doctype html><html><head><meta charset="utf-8"><title>PromptFlow VTuber Overlay</title>
<style>html,body{margin:0;background:transparent;overflow:hidden;font-family:'Pretendard','Malgun Gothic',sans-serif}
canvas{position:fixed;inset:0;width:100vw;height:100vh}#banner{position:fixed;left:50%;top:8%;transform:translateX(-50%) scale(.6);opacity:0;transition:all .35s cubic-bezier(.2,1.6,.4,1);
padding:16px 34px;border-radius:22px;background:linear-gradient(135deg,#f472b6,#a78bfa);color:#fff;font-size:42px;font-weight:900;text-shadow:0 3px 10px #0008;box-shadow:0 10px 40px #f472b688;text-align:center}
#banner.on{opacity:1;transform:translateX(-50%) scale(1)}#banner small{display:block;font-size:24px;font-weight:600;opacity:.9}
#cap{position:fixed;left:50%;bottom:6%;transform:translateX(-50%);max-width:80vw;padding:10px 22px;border-radius:14px;background:#000a;color:#fff;font-size:30px;opacity:0;transition:opacity .25s}#cap.on{opacity:1}</style></head>
<body><canvas id="c"></canvas><div id="banner"></div><div id="cap"></div><script>
const c=document.getElementById('c'),x=c.getContext('2d');let P=[];function fit(){c.width=innerWidth;c.height=innerHeight}fit();onresize=fit;
const COL=['#f472b6','#fde047','#38bdf8','#4ade80','#a78bfa','#fb923c','#fff'];function R(a,b){return a+Math.random()*(b-a)}
function burst(n,cx,cy,kind){for(let i=0;i<n;i++){const a=R(0,6.283),s=kind==='confetti'?R(2,7):R(2,9);P.push({x:cx,y:cy,vx:Math.cos(a)*s,vy:Math.sin(a)*s-(kind==='confetti'?6:0),l:R(60,110),k:kind,c:COL[i%COL.length],r:R(2,5),rot:R(0,6)})}}
function fx(v,tier,ms){const W=c.width,H=c.height,t0=performance.now(),waves=Math.max(1,tier*2);for(let w=0;w<waves;w++)setTimeout(()=>{
if(v==='hearts')for(let i=0;i<20+tier*10;i++)P.push({x:R(0,W),y:H+10,vx:R(-1,1),vy:R(-6,-3),l:R(90,160),k:'heart',c:'#f472b6',r:R(8,16),rot:0});
else if(v==='sparkle')burst(40,R(W*.3,W*.7),R(H*.3,H*.6),'spark');else if(v==='confetti')burst(120,W/2,H*.2,'confetti');
else{burst(80+tier*30,R(W*.15,W*.85),R(H*.15,H*.5),'spark');if(v==='mega-fireworks')burst(160,W/2,H/2,'confetti')}},w*Math.min(600,ms/waves))}
function draw(){x.clearRect(0,0,c.width,c.height);P=P.filter(p=>p.l-->0);for(const p of P){p.x+=p.vx;p.y+=p.vy;p.vy+=p.k==='heart'?-0.02:0.12;p.vx*=.99;p.rot+=.1;x.globalAlpha=Math.min(1,p.l/40);x.fillStyle=p.c;
if(p.k==='heart'){x.font=p.r*2+'px serif';x.fillText('💖',p.x,p.y)}else if(p.k==='confetti'){x.save();x.translate(p.x,p.y);x.rotate(p.rot);x.fillRect(-p.r,-p.r/2,p.r*2,p.r);x.restore()}else{x.beginPath();x.arc(p.x,p.y,p.r,0,6.283);x.fill()}}x.globalAlpha=1;requestAnimationFrame(draw)}draw();
const b=document.getElementById('banner'),cap=document.getElementById('cap');let bt=0,ct=0;
new EventSource('/events').onmessage=e=>{const m=JSON.parse(e.data);if(m.type==='reaction'){b.innerHTML='';b.textContent=m.banner;if(m.detail){const s=document.createElement('small');s.textContent=m.detail;b.appendChild(s)}b.classList.add('on');clearTimeout(bt);bt=setTimeout(()=>b.classList.remove('on'),m.durationMs||4000);
fx(m.vfx,m.tier||1,m.durationMs||4000);if(m.media)try{new Audio('/media/'+encodeURIComponent(m.media)).play()}catch(_){}}
else if(m.type==='speech'){cap.textContent=m.banner;cap.classList.add('on');clearTimeout(ct);ct=setTimeout(()=>cap.classList.remove('on'),m.durationMs||4000);if(m.media)try{new Audio('/media/'+encodeURIComponent(m.media)).play()}catch(_){}}};
</script></body></html>
""";
}

/// <summary>
/// [PromptFlow Studio v16.38.0] 📡 실시간 Live2D/VRM 브로드캐스팅 브릿지.
///   ▸ 발화: AI 음성(v16.14.0 TTS) → v16.20.0 입모양 타임라인(오디오 분석 · 실패 시 대사 음절 추정) + 감정 → VRM 블렌드셰이프(A/I/U/E/O · Joy/Fun/Sorrow/Angry/Surprised · 절차적 깜빡임).
///   ▸ 안무: v16.37.0 ChoreographyPlan → v16.36.0 리그 리타겟 → 로컬 회전(T-포즈 기준 · Unity 좌표 X 반전) → VMC Bone/Pos.
///   ▸ 송출: VMC(OSC/UDP · VSeeFace/VNyan/Warudo · 연결형 소켓 무할당 번들) · VTube Studio 공식 API(WebSocket · 토큰 인증 · InjectParameterData 30Hz · in-flight 1슬롯) ·
///     OBS WebSocket v5(Hello/Identify SHA256 인증 · BroadcastCustomEvent) · 루프백 오버레이 SSE 서버(OBS 브라우저 소스).
///   ▸ 60FPS 전용 스레드(Stopwatch 데드라인) · 틱 무할당(VMC 경로) · 예외 격리 · HUD OnChange 250ms 스로틀.
/// </summary>
public sealed class LiveVTuberBroadcastingBridge : IDisposable
{
    public const int Fps = 60;
    public const string PluginName = "PromptFlow Studio", PluginDeveloper = "PromptFlow";
    public const string VtsTokenVar = "PFS_VTS_TOKEN", ObsPasswordVar = "PFS_OBS_PASSWORD";

    private static readonly int[] BoneJoints = { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 13, 14, 15, 17, 18, 19 };
    private static readonly byte[][] BoneNames = VrmBlend.Encode(new[]
    {
        "Hips", "Spine", "Chest", "Neck", "Head", "LeftUpperArm", "LeftLowerArm", "LeftHand", "RightUpperArm", "RightLowerArm", "RightHand",
        "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot",
    });

    private readonly AiAudioDrivenLipSyncService? _lip;
    private readonly object _gate = new();
    private readonly byte[] _packet = new byte[4096];
    private readonly float[] _blend = new float[VrmBlend.Count];
    private readonly float[] _pose = new float[MocapFrame.PoseLength];
    private readonly float[] _p3 = new float[MocapFrame.PoseJoints * 3];
    private readonly Vector3[] _src = new Vector3[Rig.Joints], _dst = new Vector3[Rig.Joints];
    private readonly Quaternion[] _world = new Quaternion[Rig.Joints], _local = new Quaternion[Rig.Joints];
    private readonly ArrayBufferWriter<byte> _json = new(2048);
    private readonly Utf8JsonWriter _jw;
    private readonly WsJsonChannel _vts = new(), _obs = new();
    private Socket? _udp;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private SpeechPerformance? _speech;
    private double _speechStart = -1, _danceStart = -1, _danceDur, _lastRaise;
    private ChoreographyPlan? _dance;
    private FacialEmotion _mood = FacialEmotion.HappySmile;
    private long _frames, _bytes, _obsReq;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public LiveVTuberBroadcastingBridge(IServiceProvider sp)
        : this(sp.GetService(typeof(AiAudioDrivenLipSyncService)) as AiAudioDrivenLipSyncService,
               sp.GetService(typeof(ILocalMediaService)) is ILocalMediaService m ? Path.Combine(m.MediaRoot, "vtuber") : null) { }

    public LiveVTuberBroadcastingBridge(AiAudioDrivenLipSyncService? lip, string? mediaRoot)
    {
        _lip = lip;
        MediaRoot = mediaRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Media", "vtuber");
        Overlay = new OverlaySseServer(MediaRoot);
        _jw = new Utf8JsonWriter(_json);
    }

    public event Action? OnChange;

    public string MediaRoot { get; }
    public OverlaySseServer Overlay { get; }
    public string VmcHost { get; set; } = "127.0.0.1";
    public int VmcPort { get; set; } = 39539;
    public bool IsRunning => _thread is not null;
    public bool VmcOpen => _udp is not null;
    public bool VtsConnected { get; private set; }
    public bool ObsConnected { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    public long BytesSent => Interlocked.Read(ref _bytes);
    public long VtsSent => _vts.Sent;
    public long ObsEvents => Interlocked.Read(ref _obsReq);
    public bool Speaking { get { var s = _speech; return s is not null && _clock.Elapsed.TotalSeconds - _speechStart < s.Duration; } }
    public bool Dancing => _dance is not null && _clock.Elapsed.TotalSeconds - _danceStart < _danceDur;
    public string? CurrentText => _speech?.Text;
    public string? LastAction { get; private set; }
    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>UI 미러(락).</summary>
    public void CopyBlend(Span<float> dst) { lock (_gate) _blend.AsSpan().CopyTo(dst); }

    // ── 순수: 블렌드셰이프 ───────────────────────────────────────────────────

    /// <summary>대사 → 음절 균등 입모양 타임라인(초당 ~6음절 · 공백 = Rest).</summary>
    public static VisemeCue[] TranscriptCues(string text, double secPerSyllable = 0.16)
    {
        var buf = ArrayPool<Viseme>.Shared.Rent(Math.Max(1, text.Length * 2));
        try
        {
            var n = AiAudioDrivenLipSyncService.TranscriptToVisemes(text, buf);
            var cues = new VisemeCue[n];
            for (var i = 0; i < n; i++)
                cues[i] = new VisemeCue(i * secPerSyllable, (i + 1) * secPerSyllable, buf[i], buf[i] is Viseme.Rest or Viseme.Closed ? 0 : 0.85);
            return cues;
        }
        finally { ArrayPool<Viseme>.Shared.Return(buf); }
    }

    private static int CueAt(ReadOnlySpan<VisemeCue> cues, double t)
    {
        int lo = 0, hi = cues.Length - 1, res = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cues[mid].Start <= t) { res = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return res;
    }

    private static int VowelIndex(Viseme v) => v switch { Viseme.A => VrmBlend.A, Viseme.I => VrmBlend.I, Viseme.U => VrmBlend.U, Viseme.E => VrmBlend.E, Viseme.O => VrmBlend.O, _ => -1 };

    /// <summary>
    /// 시각 → VRM 블렌드(무할당). speechT &lt; 0 = 무발화. 입모양 40ms 크로스페이드 · 감정 프리셋 · 결정적 깜빡임(주기 2.8~4.6초 · 150ms).
    /// </summary>
    public static void EvaluateBlend(ReadOnlySpan<VisemeCue> cues, double speechT, FacialEmotion emotion, double intensity, double clockSec, Span<float> w)
    {
        w[..VrmBlend.Count].Clear();
        if (speechT >= 0 && cues.Length > 0)
        {
            var i = CueAt(cues, speechT);
            if (i >= 0 && speechT < cues[i].End)
            {
                var c = cues[i];
                var vi = VowelIndex(c.Shape);
                const double Fade = 0.04;
                var a = Math.Min(1, Math.Min((speechT - c.Start) / Fade, (c.End - speechT) / Fade + 0.5));
                if (vi >= 0) w[vi] = (float)(Math.Clamp(c.Openness, 0, 1) * Math.Clamp(a, 0, 1));
                if (i > 0 && speechT - c.Start < Fade)
                {
                    var p = cues[i - 1]; var pi = VowelIndex(p.Shape);
                    if (pi >= 0 && pi != vi && p.End >= c.Start - 0.001) w[pi] = MathF.Max(w[pi], (float)(Math.Clamp(p.Openness, 0, 1) * (1 - (speechT - c.Start) / Fade)));
                }
            }
        }
        var k = (float)Math.Clamp(intensity, 0, 1);
        switch (emotion)
        {
            case FacialEmotion.HappySmile: w[VrmBlend.Joy] = 0.75f * k; break;
            case FacialEmotion.Giggle: w[VrmBlend.Fun] = 0.85f * k; w[VrmBlend.Joy] = 0.3f * k; break;
            case FacialEmotion.Surprised: w[VrmBlend.Surprised] = 0.85f * k; w[VrmBlend.O] = MathF.Max(w[VrmBlend.O], 0.3f * k); break;
            case FacialEmotion.Curious: w[VrmBlend.Fun] = 0.3f * k; break;
            case FacialEmotion.Pout: w[VrmBlend.Sorrow] = 0.5f * k; w[VrmBlend.U] = MathF.Max(w[VrmBlend.U], 0.25f * k); break;
        }
        // 깜빡임(Joy/Fun 은 눈웃음 → 깜빡임 억제)
        var cycle = (long)(clockSec / 3.7);
        var h = (uint)(cycle * 2654435761L);
        var period = 2.8 + (h % 1000) / 1000.0 * 1.8;
        var phase = clockSec - cycle * 3.7;
        if (phase < 0.15 && period > 0) w[VrmBlend.Blink] = (float)(1 - Math.Abs(phase - 0.075) / 0.075) * (1 - MathF.Max(w[VrmBlend.Joy], w[VrmBlend.Fun]));
    }

    /// <summary>VRM 블렌드 → VTube Studio 기본 입력 파라미터(MouthOpen · MouthSmile · EyeOpenLeft/Right · BrowLeftY/RightY).</summary>
    public static (float MouthOpen, float MouthSmile, float EyeOpen, float Brow) ToVts(ReadOnlySpan<float> w)
    {
        var open = MathF.Max(MathF.Max(w[VrmBlend.A], w[VrmBlend.O]), MathF.Max(w[VrmBlend.U] * 0.7f, MathF.Max(w[VrmBlend.E] * 0.6f, w[VrmBlend.I] * 0.45f)));
        var smile = Math.Clamp(0.25f + w[VrmBlend.Joy] * 0.75f + w[VrmBlend.Fun] * 0.6f - w[VrmBlend.Sorrow] * 0.6f, 0, 1);
        var eye = Math.Clamp(1 - w[VrmBlend.Blink] - w[VrmBlend.Joy] * 0.25f + w[VrmBlend.Surprised] * 0.2f, 0, 1);
        var brow = Math.Clamp(w[VrmBlend.Surprised] - w[VrmBlend.Angry] - w[VrmBlend.Sorrow] * 0.3f, -1, 1);
        return (open, smile, eye, brow);
    }

    // ── 순수: VMC 프레임 ────────────────────────────────────────────────────

    /// <summary>블렌드(+ 선택 본 회전) → VMC 번들(무할당). 반환: 길이.</summary>
    public static int WriteVmcFrame(Span<byte> dst, ReadOnlySpan<float> blend, ReadOnlySpan<Quaternion> local, ReadOnlySpan<Vector3> rootPos, bool bones)
    {
        var pos = VmcCodec.BundleHeader(dst);
        Span<float> a = stackalloc float[7];
        for (var i = 0; i < VrmBlend.Count; i++)
        {
            a[0] = blend[i];
            pos = VmcCodec.Append(dst, pos, VmcCodec.BlendVal, VrmBlend.Utf8[i], a[..1]);
        }
        pos = VmcCodec.Append(dst, pos, VmcCodec.BlendApply, default, default);
        if (bones && local.Length >= Rig.Joints)
        {
            var r = rootPos.Length > 0 ? rootPos[0] : Vector3.Zero;
            a[0] = -r.X; a[1] = r.Y; a[2] = r.Z; a[3] = 0; a[4] = 0; a[5] = 0; a[6] = 1;
            pos = VmcCodec.Append(dst, pos, VmcCodec.RootPos, VmcCodec.Root, a);
            for (var b = 0; b < BoneJoints.Length; b++)
            {
                var q = local[BoneJoints[b]];
                a[0] = 0; a[1] = 0; a[2] = 0; a[3] = q.X; a[4] = -q.Y; a[5] = -q.Z; a[6] = q.W;   // 우수 → Unity 좌수(X 반전)
                pos = VmcCodec.Append(dst, pos, VmcCodec.BonePos, BoneNames[b], a);
            }
        }
        return pos;
    }

    /// <summary>리그 위치 → 로컬 회전(T-포즈 휴지 방향 기준 · 무할당).</summary>
    public static void LocalRotations(ReadOnlySpan<Vector3> joints, Span<Quaternion> world, Span<Quaternion> local)
    {
        for (var j = 0; j < Rig.Joints; j++)
        {
            var c = Rig.Primary[j];
            if (c < 0) { world[j] = j > 0 ? world[Rig.Parent[j]] : Quaternion.Identity; continue; }
            var d = joints[c] - joints[j];
            world[j] = d.LengthSquared() < 1e-12f ? Quaternion.Identity : FacialAndHandGestureMappingController.Arc(Rig.RestDir[c], Vector3.Normalize(d));
        }
        for (var j = 0; j < Rig.Joints; j++)
            local[j] = j == 0 ? world[0] : Quaternion.Normalize(Quaternion.Inverse(world[Rig.Parent[j]]) * world[j]);
    }

    // ── 순수: VTS / OBS JSON ─────────────────────────────────────────────────

    public static byte[] VtsAuthTokenRequest() => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["apiName"] = "VTubeStudioPublicAPI", ["apiVersion"] = "1.0", ["requestID"] = "pfs-token", ["messageType"] = "AuthenticationTokenRequest",
        ["data"] = new Dictionary<string, string> { ["pluginName"] = PluginName, ["pluginDeveloper"] = PluginDeveloper },
    });

    public static byte[] VtsAuthRequest(string token) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["apiName"] = "VTubeStudioPublicAPI", ["apiVersion"] = "1.0", ["requestID"] = "pfs-auth", ["messageType"] = "AuthenticationRequest",
        ["data"] = new Dictionary<string, string> { ["pluginName"] = PluginName, ["pluginDeveloper"] = PluginDeveloper, ["authenticationToken"] = token },
    });

    /// <summary>InjectParameterDataRequest(재사용 Utf8JsonWriter · 워밍업 후 무할당) → 버퍼 슬라이스.</summary>
    public ReadOnlySpan<byte> VtsInject(ReadOnlySpan<float> blend)
    {
        var (open, smile, eye, brow) = ToVts(blend);
        _json.Clear();
        _jw.Reset(_json);
        _jw.WriteStartObject();
        _jw.WriteString("apiName"u8, "VTubeStudioPublicAPI"u8);
        _jw.WriteString("apiVersion"u8, "1.0"u8);
        _jw.WriteString("requestID"u8, "pfs-inject"u8);
        _jw.WriteString("messageType"u8, "InjectParameterDataRequest"u8);
        _jw.WriteStartObject("data"u8);
        _jw.WriteBoolean("faceFound"u8, true);
        _jw.WriteString("mode"u8, "set"u8);
        _jw.WriteStartArray("parameterValues"u8);
        Param("MouthOpen"u8, open); Param("MouthSmile"u8, smile); Param("EyeOpenLeft"u8, eye); Param("EyeOpenRight"u8, eye); Param("BrowLeftY"u8, brow); Param("BrowRightY"u8, brow);
        _jw.WriteEndArray();
        _jw.WriteEndObject();
        _jw.WriteEndObject();
        _jw.Flush();
        return _json.WrittenSpan;
    }

    private void Param(ReadOnlySpan<byte> id, float v)
    {
        _jw.WriteStartObject();
        _jw.WriteString("id"u8, id);
        _jw.WriteNumber("value"u8, MathF.Round(v, 4));
        _jw.WriteEndObject();
    }

    /// <summary>OBS WebSocket v5 인증 문자열 = base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    public static string ObsAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public static string OverlayJson(OverlayCue c) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["type"] = c.Type, ["vfx"] = c.Vfx, ["banner"] = c.Banner, ["detail"] = c.Detail, ["media"] = c.Media, ["durationMs"] = c.DurationMs, ["tier"] = c.Tier,
    });

    // ── 연결 ────────────────────────────────────────────────────────────────

    /// <summary>VTube Studio 연결 · 인증(토큰: 인자 → 환경변수 → 토큰 요청(앱에서 사용자 승인)). 반환: (성공, 토큰, 메시지).</summary>
    public async Task<(bool Ok, string? Token, string Message)> ConnectVtsAsync(Uri uri, string? token = null, CancellationToken ct = default)
    {
        VtsConnected = false;
        try
        {
            await _vts.ConnectAsync(uri, ct).ConfigureAwait(false);
            token ??= Environment.GetEnvironmentVariable(VtsTokenVar);
            if (string.IsNullOrWhiteSpace(token))
            {
                await _vts.SendAsync(VtsAuthTokenRequest(), ct).ConfigureAwait(false);
                using var tr = await ReceiveTypedAsync(_vts, "AuthenticationTokenResponse", ct).ConfigureAwait(false);
                token = tr.RootElement.GetProperty("data").GetProperty("authenticationToken").GetString();
                if (string.IsNullOrWhiteSpace(token)) return Fail("VTS 토큰 거부");
            }
            await _vts.SendAsync(VtsAuthRequest(token), ct).ConfigureAwait(false);
            using var ar = await ReceiveTypedAsync(_vts, "AuthenticationResponse", ct).ConfigureAwait(false);
            if (!ar.RootElement.GetProperty("data").GetProperty("authenticated").GetBoolean()) return Fail("VTS 인증 실패");
            _vts.StartDrain();
            VtsConnected = true;
            LastAction = "🔗 VTube Studio 인증 완료";
            Raise();
            return (true, token, LastAction);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Program.Log("VTuber.Vts", ex);
            return Fail("VTS 연결 실패: " + ex.Message);
        }

        (bool, string?, string) Fail(string m) { _vts.Dispose(); LastAction = "⚠ " + m; Raise(); return (false, null, m); }
    }

    private static async Task<JsonDocument> ReceiveTypedAsync(WsJsonChannel ch, string type, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            var doc = await ch.ReceiveAsync(ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("messageType", out var mt))
            {
                if (mt.GetString() == type) return doc;
                if (mt.GetString() == "APIError") { var msg = doc.RootElement.GetProperty("data").GetProperty("message").GetString(); doc.Dispose(); throw new InvalidOperationException(msg); }
            }
            doc.Dispose();
        }
        throw new InvalidDataException("응답 스키마 위반: " + type);
    }

    /// <summary>OBS WebSocket v5 연결 · Identify(비밀번호: 인자 → 환경변수).</summary>
    public async Task<(bool Ok, string Message)> ConnectObsAsync(Uri uri, string? password = null, CancellationToken ct = default)
    {
        ObsConnected = false;
        try
        {
            await _obs.ConnectAsync(uri, ct).ConfigureAwait(false);
            using var hello = await _obs.ReceiveAsync(ct).ConfigureAwait(false);
            if (!hello.RootElement.TryGetProperty("op", out var op) || op.GetInt32() != 0) throw new InvalidDataException("응답 스키마 위반: Hello");
            var d = hello.RootElement.GetProperty("d");
            var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
            if (d.TryGetProperty("authentication", out var auth))
            {
                password ??= Environment.GetEnvironmentVariable(ObsPasswordVar);
                if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("OBS 비밀번호 필요(" + ObsPasswordVar + ")");
                identify["authentication"] = ObsAuth(password, auth.GetProperty("salt").GetString() ?? "", auth.GetProperty("challenge").GetString() ?? "");
            }
            await _obs.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["op"] = 1, ["d"] = identify }), ct).ConfigureAwait(false);
            using var ok = await _obs.ReceiveAsync(ct).ConfigureAwait(false);
            if (!ok.RootElement.TryGetProperty("op", out var op2) || op2.GetInt32() != 2) throw new InvalidOperationException("OBS Identify 거부");
            _obs.StartDrain();
            ObsConnected = true;
            LastAction = "🔗 OBS WebSocket 연결";
            Raise();
            return (true, LastAction);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Program.Log("VTuber.Obs", ex);
            _obs.Dispose();
            LastAction = "⚠ OBS 연결 실패: " + ex.Message;
            Raise();
            return (false, LastAction);
        }
    }

    // ── 수명 ────────────────────────────────────────────────────────────────

    /// <summary>VMC UDP 소켓 + 오버레이 서버(port 0 = 임의) + 60FPS 루프 시작(멱등).</summary>
    public void Start(int overlayPort = 17860)
    {
        if (_thread is not null) return;
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Parse(VmcHost), VmcPort);
            _udp = s;
        }
        catch (Exception ex) { Program.Log("VTuber.Udp", ex); LastAction = "⚠ VMC 소켓 실패: " + ex.Message; }
        try { Directory.CreateDirectory(MediaRoot); Overlay.Start(overlayPort); }
        catch (Exception ex) { Program.Log("VTuber.Overlay", ex); LastAction = "⚠ 오버레이 포트 실패: " + ex.Message; }
        var cts = new CancellationTokenSource();
        _cts = cts;
        var t = new Thread(() => Loop(cts.Token)) { IsBackground = true, Name = "PFS-VTuber60", Priority = ThreadPriority.AboveNormal };
        _thread = t;
        t.Start();
        LastAction = "▶ 브릿지 시작 · VMC " + VmcHost + ":" + VmcPort.ToString(CultureInfo.InvariantCulture) + (Overlay.IsRunning ? " · 오버레이 " + Overlay.Url : "");
        Raise();
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        var t = Interlocked.Exchange(ref _thread, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            if (t is not null && t != Thread.CurrentThread) t.Join(500);
            cts.Dispose();
        }
        Interlocked.Exchange(ref _udp, null)?.Dispose();
        Overlay.Dispose();
        _vts.Dispose(); _obs.Dispose();
        VtsConnected = ObsConnected = false;
        LastAction = "■ 브릿지 정지";
        Raise();
    }

    private void Loop(CancellationToken ct)
    {
        var next = _clock.Elapsed.TotalMilliseconds;
        while (!ct.IsCancellationRequested)
        {
            try { Step(_clock.Elapsed.TotalSeconds); }
            catch (Exception ex) { Program.Log("VTuber.Tick", ex); }
            next += 1000.0 / Fps;
            var now = _clock.Elapsed.TotalMilliseconds;
            if (next < now - 100) next = now;
            var remain = next - now;
            if (remain > 2) Thread.Sleep((int)(remain - 1.5));
            while (_clock.Elapsed.TotalMilliseconds < next && !ct.IsCancellationRequested) Thread.SpinWait(64);
        }
    }

    /// <summary>1틱(무할당 VMC 경로) — 블렌드 · 안무 본 → VMC 송신 · VTS 30Hz 주입. 반환: 패킷 길이.</summary>
    public int Step(double nowSec)
    {
        var sp = Volatile.Read(ref _speech);
        var dance = Volatile.Read(ref _dance);
        int len;
        lock (_gate)
        {
            var st = sp is not null && nowSec - _speechStart < sp.Duration ? nowSec - _speechStart : -1;
            EvaluateBlend(sp is null ? default : sp.Cues, st, st >= 0 ? sp!.Emotion : _mood, st >= 0 ? sp!.Intensity : 0.35, nowSec, _blend);
            var dancing = dance is not null && nowSec - _danceStart < _danceDur;
            if (dancing)
            {
                AlgorithmicChoreographyController.EvaluatePose(dance!, nowSec - _danceStart, _pose);
                for (var j = 0; j < MocapFrame.PoseJoints; j++) { _p3[j * 3] = _pose[j * 4]; _p3[j * 3 + 1] = _pose[j * 4 + 1]; _p3[j * 3 + 2] = _pose[j * 4 + 2]; }
                FacialAndHandGestureMappingController.SourceRig(_p3, _src);
                FacialAndHandGestureMappingController.Retarget(_src, RigProportions.Adult, _dst);
                LocalRotations(_dst, _world, _local);
            }
            len = WriteVmcFrame(_packet, _blend, _local, _dst, dancing);
            var udp = _udp;
            if (udp is not null && len > 0)
            {
                try { udp.Send(_packet.AsSpan(0, len)); Interlocked.Add(ref _bytes, len); }
                catch (SocketException) { /* 수신기 없음(ICMP) — 무시 */ }
            }
            if (VtsConnected && (Interlocked.Increment(ref _frames) & 1) == 0) _vts.TrySend(VtsInject(_blend));
            else if (!VtsConnected) Interlocked.Increment(ref _frames);
        }
        if (nowSec - _lastRaise >= 0.25) { _lastRaise = nowSec; Raise(); }
        return len;
    }

    // ── 퍼포먼스 ───────────────────────────────────────────────────────────

    /// <summary>발화 퍼포먼스 구성(음성 파일 → 오디오 분석 · 실패/없음 → 대사 추정).</summary>
    public async Task<SpeechPerformance> PrepareSpeechAsync(string text, FacialEmotion emotion, string? voicePath, CancellationToken ct = default)
    {
        VisemeCue[]? cues = null;
        double duration = 0;
        if (voicePath is not null && File.Exists(voicePath) && _lip is not null)
        {
            try
            {
                var (act, list) = await _lip.AnalyzeAudioAsync(voicePath, text, FacialEmotionPuppeteerEngine.MouthScaleOf(emotion), ct).ConfigureAwait(false);
                if (list.Count > 0) { cues = list.ToArray(); duration = Math.Max(act.DurationSeconds, cues[^1].End); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("VTuber.Lip", ex); }
        }
        if (cues is null) { cues = TranscriptCues(text); duration = cues.Length == 0 ? 1 : cues[^1].End + 0.2; }
        string? media = null;
        if (voicePath is not null && File.Exists(voicePath))
        {
            try
            {
                Directory.CreateDirectory(MediaRoot);
                media = "voice_" + Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(voicePath).ToLowerInvariant();
                if (!OverlaySseServer.IsSafeName(media)) media = null;
                else File.Copy(voicePath, Path.Combine(MediaRoot, media), true);
            }
            catch (Exception ex) { Program.Log("VTuber.VoiceCopy", ex); media = null; }
        }
        return new SpeechPerformance(text, emotion, 0.7, cues, duration, media);
    }

    /// <summary>발화 시작(입모양/감정 · 오버레이 자막 + 음성 재생).</summary>
    public void Speak(SpeechPerformance p)
    {
        lock (_gate) { _speech = p; _speechStart = _clock.Elapsed.TotalSeconds; _mood = p.Emotion; }
        PushOverlay(new OverlayCue("speech", "", p.Text, null, p.VoiceFile, (int)(p.Duration * 1000) + 800, 0));
        LastAction = "🗣 " + VTuberPersonaMemoryEngine.Clip(p.Text, 40);
        Raise();
    }

    /// <summary>안무 재생(seconds 동안 · 0초부터).</summary>
    public void PlayDance(ChoreographyPlan plan, double seconds)
    {
        lock (_gate) { _dance = plan; _danceStart = _clock.Elapsed.TotalSeconds; _danceDur = Math.Clamp(seconds, 0.5, 60); }
        LastAction = "💃 안무 " + AlgorithmicChoreographyController.StyleLabel(plan.Style) + " " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + "초";
        Raise();
    }

    /// <summary>오버레이 큐 송출(SSE + OBS BroadcastCustomEvent). 반환: SSE 수신 클라이언트 수.</summary>
    public int PushOverlay(OverlayCue cue)
    {
        var json = OverlayJson(cue);
        var n = Overlay.IsRunning ? Overlay.Broadcast(json) : 0;
        if (ObsConnected)
        {
            var id = Interlocked.Increment(ref _obsReq);
            var req = "{\"op\":6,\"d\":{\"requestType\":\"BroadcastCustomEvent\",\"requestId\":\"pfs-" + id.ToString(CultureInfo.InvariantCulture) + "\",\"requestData\":{\"eventData\":" + json + "}}}";
            _obs.TrySend(Encoding.UTF8.GetBytes(req));
        }
        return n;
    }

    public void SetMood(FacialEmotion e) { lock (_gate) _mood = e; }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("VTuber.Bridge.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        _jw.Dispose();
    }
}
