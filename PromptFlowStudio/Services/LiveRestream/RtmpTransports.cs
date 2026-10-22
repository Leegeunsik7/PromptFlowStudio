using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;

namespace PromptFlowStudio.Services.LiveRestream;

/// <summary>v17.83.0 RTMP(S) 바이트 전송 계층 — Send/Receive 모두 논블로킹(송신 큐 적체 · RTT 노출 → ABR 가드 샘플링).</summary>
public interface IRtmpTransport
{
    bool Connected { get; }
    /// <summary>반환: 수락 바이트(전량) · 0 = 송신 큐 포화로 거절 · −1 = 끊김.</summary>
    int Send(ReadOnlySpan<byte> data);
    /// <summary>논블로킹 — 수신 대기 바이트 복사(없으면 0).</summary>
    int Receive(Span<byte> dst);
    long PendingBytes { get; }
    double RttMs { get; }
    void Close();
}

/// <summary>
/// [PromptFlow Studio v17.83.0] 루프백 RTMP 인제스트 서버 + 업링크 시뮬레이터(테스트/오프라인 리허설용 · 무할당).
///   ▸ 서버: S0/S1/S2 핸드셰이크(C1 에코 · C2 검증) → connect/createStream/publish 응답(_result · onStatus NetStream.Publish.Start) → FLV 태그 검수
///     (AVC 설정 레코드 · AVCC 길이 합 정합 · 첫 프레임 키프레임 · AAC 0xAF) · 수신 페이로드 순서 해시.
///   ▸ 업링크: 수락 바이트 → 큐 적체 · <see cref="Advance"/>로 Kbps 배출 · RTT = 기본 + 큐잉 지연(버퍼블로트) · 상한 초과 메시지 거절.
/// </summary>
public sealed class LoopbackRtmpServerTransport : IRtmpTransport, IRtmpMessageHandler
{
    private enum Phase : byte { Hs0, Hs1, Chunks, Closed }

    private readonly byte[] _hs = new byte[1 + RtmpWire.HandshakeSize * 2];
    private readonly byte[] _s1 = new byte[RtmpWire.HandshakeSize];
    private readonly byte[] _out = new byte[64 * 1024];
    private readonly byte[] _cmd = new byte[1024];
    private readonly RtmpChunkReader _reader = new();
    private Phase _phase;
    private int _hsFill, _outR, _outW, _outChunk = RtmpWire.DefaultChunk;
    private double _pending;
    private uint _seed;
    private bool _sawSeq, _sawKey;

    public LoopbackRtmpServerTransport(string name, int uplinkKbps = 10_000, double baseRttMs = 30, long queueCapBytes = 4 << 20, uint seed = 0x5EED1234u)
    {
        Name = name; UplinkKbps = uplinkKbps; BaseRttMs = baseRttMs; QueueCapBytes = queueCapBytes; _seed = seed;
    }

    public string Name { get; }
    public int UplinkKbps { get; set; }
    public double BaseRttMs { get; set; }
    public long QueueCapBytes { get; set; }
    public bool Connected => _phase != Phase.Closed;
    public long PendingBytes => (long)_pending;
    public double RttMs => BaseRttMs + (UplinkKbps > 0 ? _pending * 8.0 / UplinkKbps : 0);
    public bool HandshakeOk { get; private set; }
    public bool Publishing { get; private set; }
    public string? StreamKey { get; private set; }
    public string? App { get; private set; }
    public long BytesIn { get; private set; }
    public long Overflows { get; private set; }
    public long VideoTags { get; private set; }
    public long AudioTags { get; private set; }
    public long Keyframes { get; private set; }
    public long VideoSeqHeaders { get; private set; }
    public long AudioSeqHeaders { get; private set; }
    public long MetaData { get; private set; }
    public long GlitchFrames { get; private set; }
    public long CorruptTags { get; private set; }
    public long Unpublished { get; private set; }
    public ulong PayloadHash { get; private set; }
    public uint LastVideoTs { get; private set; }
    public long ProtocolErrors => _reader.Errors;

    /// <summary>재연결 전 서버 측 세션 초기화(누적 무결성 통계는 유지 · 키프레임 게이트 재무장).</summary>
    public void Reopen()
    {
        _phase = Phase.Hs0; _hsFill = 0; _outR = _outW = 0; _outChunk = RtmpWire.DefaultChunk; _pending = 0;
        _reader.Reset(); HandshakeOk = Publishing = false; _sawSeq = _sawKey = false;
    }

    public void Close() { _phase = Phase.Closed; Publishing = false; _pending = 0; }

    /// <summary>업링크 배출(dt µs).</summary>
    public void Advance(long dtUs)
    {
        _pending = Math.Max(0, _pending - UplinkKbps * 1000.0 / 8.0 * dtUs / 1e6);
    }

    public int Receive(Span<byte> dst)
    {
        var n = Math.Min(dst.Length, _outW - _outR);
        if (n <= 0) return 0;
        _out.AsSpan(_outR, n).CopyTo(dst);
        _outR += n;
        if (_outR == _outW) _outR = _outW = 0;
        return n;
    }

    public int Send(ReadOnlySpan<byte> data)
    {
        if (_phase == Phase.Closed) return -1;
        if (_phase == Phase.Chunks && _pending + data.Length > QueueCapBytes) { Overflows++; return 0; }
        _pending += data.Length;
        BytesIn += data.Length;
        var d = data;
        while (d.Length > 0 && _phase != Phase.Chunks)
        {
            var need = (_phase == Phase.Hs0 ? 1 + RtmpWire.HandshakeSize : RtmpWire.HandshakeSize) - _hsFill;
            var n = Math.Min(need, d.Length);
            d.Slice(0, n).CopyTo(_hs.AsSpan(_hsFill));
            _hsFill += n; d = d.Slice(n);
            if (_hsFill < (_phase == Phase.Hs0 ? 1 + RtmpWire.HandshakeSize : RtmpWire.HandshakeSize)) break;
            if (_phase == Phase.Hs0)
            {
                if (_hs[0] != RtmpWire.Version) { Close(); return -1; }
                var o = Reserve(1 + RtmpWire.HandshakeSize * 2);
                o[0] = RtmpWire.Version;
                BinaryPrimitives.WriteUInt32BigEndian(_s1, 0);
                BinaryPrimitives.WriteUInt32BigEndian(_s1.AsSpan(4), 0);
                for (var i = 8; i < _s1.Length; i++) _s1[i] = RtmpWire.Next(ref _seed);
                _s1.CopyTo(o.Slice(1));
                _hs.AsSpan(1, RtmpWire.HandshakeSize).CopyTo(o.Slice(1 + RtmpWire.HandshakeSize)); // S2 = C1 에코
                _phase = Phase.Hs1; _hsFill = 0;
            }
            else
            {
                if (!_hs.AsSpan(8, RtmpWire.HandshakeSize - 8).SequenceEqual(_s1.AsSpan(8))) { Close(); return -1; }
                HandshakeOk = true; _phase = Phase.Chunks; _hsFill = 0;
            }
        }
        if (d.Length > 0 && _phase == Phase.Chunks) _reader.Feed(d, this);
        return data.Length;
    }

    private Span<byte> Reserve(int n)
    {
        if (_outW + n > _out.Length) { Buffer.BlockCopy(_out, _outR, _out, 0, _outW - _outR); _outW -= _outR; _outR = 0; }
        var s = _out.AsSpan(_outW, n);
        _outW += n;
        return s;
    }

    private void Reply(int csid, byte type, uint sid, ReadOnlySpan<byte> payload)
    {
        var o = Reserve(RtmpWire.MaxChunked(payload.Length, _outChunk));
        var n = RtmpWire.WriteMessage(o, csid, 0, type, sid, payload, _outChunk);
        _outW -= o.Length - n;
    }

    private void ReplyControl(byte type, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        Reply(RtmpWire.CsidControl, type, 0, b);
    }

    public void OnMessage(byte type, uint streamId, uint timestamp, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case RtmpWire.TypeCommand: OnCommand(payload); break;
            case RtmpWire.TypeData:
                {
                    var r = new Amf0Reader(payload);
                    if (r.String(out var n) && n.SequenceEqual("@setDataFrame"u8)) MetaData++;
                    break;
                }
            case RtmpWire.TypeVideo: OnVideo(timestamp, payload); break;
            case RtmpWire.TypeAudio: OnAudio(payload); break;
        }
    }

    private void OnVideo(uint ts, ReadOnlySpan<byte> p)
    {
        if (!Publishing || p.Length < 5 || (p[0] & 0x0F) != 7) { CorruptTags++; return; }
        PayloadHash = RtmpWire.Chain(PayloadHash, p);
        if (p[1] == 0)
        {
            if (p.Length < 16 || p[5] != 1) { CorruptTags++; return; }
            VideoSeqHeaders++; _sawSeq = true;
            return;
        }
        VideoTags++; LastVideoTs = ts;
        var key = p[0] >> 4 == 1;
        if (key) { Keyframes++; _sawKey = true; }
        if (!_sawSeq || !_sawKey) GlitchFrames++;
        var q = 5;
        while (q + 4 <= p.Length)
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(p.Slice(q));
            if (n <= 0 || q + 4 + n > p.Length) { CorruptTags++; return; }
            q += 4 + n;
        }
        if (q != p.Length) CorruptTags++;
    }

    private void OnAudio(ReadOnlySpan<byte> p)
    {
        if (!Publishing || p.Length < 2 || p[0] != 0xAF) { CorruptTags++; return; }
        PayloadHash = RtmpWire.Chain(PayloadHash, p);
        if (p[1] == 0) AudioSeqHeaders++; else AudioTags++;
    }

    private void OnCommand(ReadOnlySpan<byte> payload)
    {
        var r = new Amf0Reader(payload);
        if (!r.String(out var name) || !r.Number(out var txn)) return;
        var w = new Amf0Writer(_cmd);
        if (name.SequenceEqual("connect"u8))
        {
            if (r.FindString("app"u8, out var app)) App = System.Text.Encoding.UTF8.GetString(app);
            ReplyControl(RtmpWire.TypeWinAck, 2_500_000);
            Span<byte> bw = stackalloc byte[5];
            BinaryPrimitives.WriteUInt32BigEndian(bw, 2_500_000); bw[4] = 2;
            Reply(RtmpWire.CsidControl, RtmpWire.TypePeerBw, 0, bw);
            ReplyControl(RtmpWire.TypeSetChunk, RtmpWire.OutChunk);
            _outChunk = RtmpWire.OutChunk;
            w.Str("_result"u8); w.Num(txn);
            w.ObjStart(); w.Prop("fmsVer"u8, "FMS/3,5,7,7009"u8); w.Prop("capabilities"u8, 31); w.ObjEnd();
            w.ObjStart(); w.Prop("level"u8, "status"u8); w.Prop("code"u8, "NetConnection.Connect.Success"u8); w.Prop("description"u8, "Connection succeeded."u8); w.ObjEnd();
            Reply(RtmpWire.CsidCommand, RtmpWire.TypeCommand, 0, w.Written);
        }
        else if (name.SequenceEqual("createStream"u8))
        {
            w.Str("_result"u8); w.Num(txn); w.Null(); w.Num(1);
            Reply(RtmpWire.CsidCommand, RtmpWire.TypeCommand, 0, w.Written);
        }
        else if (name.SequenceEqual("publish"u8))
        {
            r.Skip();
            if (r.String(out var key)) StreamKey = System.Text.Encoding.UTF8.GetString(key);
            Publishing = true; _sawSeq = _sawKey = false;
            w.Str("onStatus"u8); w.Num(0); w.Null();
            w.ObjStart(); w.Prop("level"u8, "status"u8); w.Prop("code"u8, "NetStream.Publish.Start"u8); w.ObjEnd();
            Reply(RtmpWire.CsidStream, RtmpWire.TypeCommand, 1, w.Written);
        }
        else if (name.SequenceEqual("deleteStream"u8) || name.SequenceEqual("FCUnpublish"u8))
        {
            if (Publishing) Unpublished++;
            Publishing = false;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.83.0] 실 RTMPS 전송(TCP 443 + <see cref="SslStream"/> TLS · 시스템 인증서 검증) — 송신 링(4 MB) → 전용 라이터 스레드,
/// 수신 링 ← 리더 스레드. 연결/TLS 협상 타임아웃 · 쓰기 지연 EWMA를 RTT 근사로 노출. 시크릿(스트림 키)은 호출자가 환경 변수에서 주입.
/// </summary>
public sealed class TlsRtmpTransport : IRtmpTransport, IDisposable
{
    private readonly byte[] _tx = new byte[4 << 20], _rx = new byte[256 * 1024];
    private readonly object _txGate = new(), _rxGate = new();
    private int _txR, _txN, _rxR, _rxN;
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private Thread? _writer, _reader;
    private volatile bool _open;
    private double _rtt;

    public string Host { get; }
    public int Port { get; }
    public string? LastError { get; private set; }

    public TlsRtmpTransport(string host, int port = 443) { Host = host; Port = port; }

    public bool Connected => _open;
    public long PendingBytes { get { lock (_txGate) return _txN; } }
    public double RttMs => _rtt;

    /// <summary>TCP 연결 + TLS 핸드셰이크(각 타임아웃) → 송수신 스레드 기동.</summary>
    public bool Open(int timeoutMs = 5000)
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true, SendTimeout = timeoutMs, ReceiveTimeout = 0 };
            using (var cts = new CancellationTokenSource(timeoutMs)) _tcp.ConnectAsync(Host, Port, cts.Token).AsTask().GetAwaiter().GetResult();
            _ssl = new SslStream(_tcp.GetStream(), false);
            using (var cts = new CancellationTokenSource(timeoutMs))
                _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, cts.Token).GetAwaiter().GetResult();
            _open = true;
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "rtmps-tx " + Host };
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "rtmps-rx " + Host };
            _writer.Start(); _reader.Start();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Close();
            return false;
        }
    }

    public int Send(ReadOnlySpan<byte> data)
    {
        if (!_open) return -1;
        lock (_txGate)
        {
            if (_txN + data.Length > _tx.Length) return 0;
            var w = (_txR + _txN) % _tx.Length;
            var first = Math.Min(data.Length, _tx.Length - w);
            data.Slice(0, first).CopyTo(_tx.AsSpan(w));
            data.Slice(first).CopyTo(_tx.AsSpan(0));
            _txN += data.Length;
            Monitor.Pulse(_txGate);
        }
        return data.Length;
    }

    public int Receive(Span<byte> dst)
    {
        lock (_rxGate)
        {
            var n = Math.Min(dst.Length, _rxN);
            for (var i = 0; i < n; i++) dst[i] = _rx[(_rxR + i) % _rx.Length];
            _rxR = (_rxR + n) % _rx.Length; _rxN -= n;
            return n;
        }
    }

    private void WriteLoop()
    {
        var sw = new Stopwatch();
        try
        {
            while (_open)
            {
                int r, n;
                lock (_txGate)
                {
                    while (_txN == 0 && _open) Monitor.Wait(_txGate, 200);
                    r = _txR; n = Math.Min(_txN, _tx.Length - _txR);
                }
                if (n == 0) continue;
                sw.Restart();
                _ssl!.Write(_tx, r, n);
                _rtt = _rtt <= 0 ? sw.Elapsed.TotalMilliseconds : _rtt * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
                lock (_txGate) { _txR = (_txR + n) % _tx.Length; _txN -= n; }
            }
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; _open = false; }
    }

    private void ReadLoop()
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (_open)
            {
                var n = _ssl!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                lock (_rxGate)
                {
                    n = Math.Min(n, _rx.Length - _rxN);
                    for (var i = 0; i < n; i++) _rx[(_rxR + _rxN + i) % _rx.Length] = buf[i];
                    _rxN += n;
                }
            }
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
        _open = false;
    }

    public void Close()
    {
        _open = false;
        lock (_txGate) Monitor.PulseAll(_txGate);
        try { _ssl?.Dispose(); } catch (IOException) { }
        try { _tcp?.Dispose(); } catch (SocketException) { }
        _ssl = null; _tcp = null;
    }

    public void Dispose() => Close();
}
