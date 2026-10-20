using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>[v9.4.2] 브로드캐스트 패킷 종류.</summary>
public enum EdgePacketKind : byte
{
    VideoFrame = 1,
    AudioChunk = 2,
    Heartbeat = 3,
}

/// <summary>[v9.4.2] 브로드캐스터 링크 상태.</summary>
public enum EdgeBroadcastState
{
    /// <summary>정지.</summary>
    Stopped = 0,
    /// <summary>소켓 수신 대기(엣지 구독 가능).</summary>
    Live = 1,
    /// <summary>소켓 실패 — 인프로세스 전달로 강등.</summary>
    InProcess = 2,
}

/// <summary>[v9.4.2] 엣지 구독자 스냅샷.</summary>
public sealed record EdgeSubscriberView(int Id, string Endpoint, long Packets, long Bytes, bool Probe);

/// <summary>
/// [PromptFlow Studio v9.4.2] 초저지연 엣지 프리뷰 브로드캐스터(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 발행 → <see cref="Channel{T}"/>(256 · DropOldest · 단일 리더) → 펌프가 25바이트 헤더 + 페이로드를 <c>Socket.SendAsync</c> 로 팬아웃.
///     오래된 프레임은 대기열에서 폐기되어 누적 지연이 생기지 않는다(프리뷰는 최신성 우선).
///   ▸ 헤더: magic(4) · seq(8) · kind(1) · 발행 타임스탬프(Stopwatch ticks 8) · 길이(4) — Big-Endian 고정.
///   ▸ <see cref="Socket.NoDelay"/>(Nagle 끔) + 페이로드 <see cref="ArrayPool{T}"/> 임대로 핫패스 할당 최소화.
///   ▸ SLA: 발행→소켓 송신 완료 지연 &lt; 50ms 비율, EWMA, P95(128 표본 링) 집계. 루프백 프로브 구독자가 수신측 종단 지연을 실측한다.
///   ▸ 제5헌장: 리스너 바인드 실패 시 <see cref="EdgeBroadcastState.InProcess"/> 로 강등해 <see cref="OnPacket"/> 인프로세스 전달만 유지.
///   ▸ 제3헌장: CTS·리스너·소켓·데모 타이머·채널은 <see cref="Dispose"/> 에서 전량 해제.
/// </summary>
public sealed class ZeroLatencyEdgeBroadcaster : IDisposable
{
    /// <summary>SLA 지연 상한(ms).</summary>
    public const double SlaMs = 50d;

    private const uint Magic = 0x5046_4542;   // "PFEB"
    private const int HeaderBytes = 25;
    private const int QueueCapacity = 256;
    private const int SampleCap = 128;
    private const int MaxPayload = 4 * 1024 * 1024;

    private readonly ConcurrentDictionary<int, Subscriber> _subs = new();
    private readonly double[] _samples = new double[SampleCap];
    private readonly object _sampleGate = new();
    private Channel<Packet> _channel;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private System.Threading.Timer? _demo;
    private int _sampleCursor;
    private int _sampleCount;
    private int _nextSubId;
    private long _seq;
    private long _published;
    private long _sent;
    private long _bytes;
    private long _dropped;
    private long _withinSla;
    private long _late;
    private long _probeReceived;
    private double _ewmaMs;
    private double _probeEwmaMs;
    private volatile bool _probePending;
    private bool _disposed;

    public ZeroLatencyEdgeBroadcaster() => _channel = CreateChannel();

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>인프로세스 패킷 전달(소켓 여부와 무관 · 구독자 예외 격리). 인자: 종류 · seq · 페이로드 길이.</summary>
    public event Action<EdgePacketKind, long, int>? OnPacket;

    /// <summary>현재 상태.</summary>
    public EdgeBroadcastState State { get; private set; } = EdgeBroadcastState.Stopped;

    /// <summary>수신 포트(미기동 시 0).</summary>
    public int Port { get; private set; }

    /// <summary>데모 스트림 동작 여부.</summary>
    public bool DemoRunning => _demo is not null;

    /// <summary>누적 발행.</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>누적 송신 패킷(구독자 합).</summary>
    public long SentCount => Interlocked.Read(ref _sent);

    /// <summary>누적 송신 바이트.</summary>
    public long SentBytes => Interlocked.Read(ref _bytes);

    /// <summary>대기열 폐기(최신성 우선 DropOldest).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>SLA 이내 전달.</summary>
    public long WithinSlaCount => Interlocked.Read(ref _withinSla);

    /// <summary>SLA 초과 전달.</summary>
    public long LateCount => Interlocked.Read(ref _late);

    /// <summary>SLA 준수율(0~1 · 표본 없으면 1).</summary>
    public double SlaRatio
    {
        get
        {
            var ok = WithinSlaCount;
            var total = ok + LateCount;
            return total == 0 ? 1d : ok / (double)total;
        }
    }

    /// <summary>송신 지연 EWMA(ms).</summary>
    public double LatencyMs => _ewmaMs;

    /// <summary>루프백 프로브 수신 종단 지연 EWMA(ms · 미수신 0).</summary>
    public double ProbeLatencyMs => _probeEwmaMs;

    /// <summary>프로브 수신 패킷 수.</summary>
    public long ProbeReceived => Interlocked.Read(ref _probeReceived);

    /// <summary>구독자 수.</summary>
    public int SubscriberCount => _subs.Count;

    /// <summary>마지막 오류(정상 시 null).</summary>
    public string? LastError { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>송신 지연 P95(ms).</summary>
    public double P95Ms
    {
        get
        {
            double[] copy;
            lock (_sampleGate)
            {
                if (_sampleCount == 0) return 0d;
                copy = new double[_sampleCount];
                Array.Copy(_samples, copy, _sampleCount);
            }
            Array.Sort(copy);
            return copy[Math.Clamp((int)Math.Ceiling(copy.Length * 0.95d) - 1, 0, copy.Length - 1)];
        }
    }

    /// <summary>구독자 스냅샷.</summary>
    public EdgeSubscriberView[] Subscribers
    {
        get
        {
            var list = new System.Collections.Generic.List<EdgeSubscriberView>(_subs.Count);
            foreach (var s in _subs.Values)
                list.Add(new EdgeSubscriberView(s.Id, s.Endpoint, Interlocked.Read(ref s.Packets), Interlocked.Read(ref s.Bytes), s.Probe));
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list.ToArray();
        }
    }

    /// <summary>
    /// 브로드캐스트를 기동한다(포트 0=임의 · 기본 루프백 바인드). 바인드 실패는 인프로세스 강등 후 false.
    /// 이미 기동 중이면 true.
    /// </summary>
    public bool Start(int port = 0, bool bindAny = false)
    {
        if (_disposed) return false;
        if (State != EdgeBroadcastState.Stopped) return true;

        _cts = new CancellationTokenSource();
        _channel = CreateChannel();
        var ct = _cts.Token;
        var channel = _channel;
        _ = Task.Run(() => PumpAsync(channel, ct));

        try
        {
            _listener = new TcpListener(bindAny ? IPAddress.Any : IPAddress.Loopback, Math.Clamp(port, 0, 65535));
            var listener = _listener;
            listener.Start(16);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            State = EdgeBroadcastState.Live;
            LastError = null;
            _ = Task.Run(() => AcceptLoopAsync(listener, ct));
            Touch("기동 · 포트 " + Port.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _listener = null;
            Port = 0;
            State = EdgeBroadcastState.InProcess;
            LastError = "소켓 바인드 실패 — 인프로세스 강등: " + ex.Message;
            Touch("인프로세스 강등");
            return false;
        }
    }

    /// <summary>브로드캐스트 정지(구독자 소켓 종료 · 통계 보존).</summary>
    public void Stop()
    {
        StopDemo();
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        _channel.Writer.TryComplete();

        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;

        foreach (var s in _subs.Values) CloseSubscriber(s);
        _subs.Clear();

        Port = 0;
        State = EdgeBroadcastState.Stopped;
        Touch("정지");
    }

    /// <summary>
    /// 프레임/오디오를 발행한다(페이로드는 풀 버퍼로 복사 · 대기열 포화 시 가장 오래된 패킷 폐기).
    /// 미기동 상태면 false.
    /// </summary>
    public bool Publish(EdgePacketKind kind, ReadOnlySpan<byte> payload)
    {
        if (State == EdgeBroadcastState.Stopped || payload.Length > MaxPayload) return false;

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
        payload.CopyTo(buffer);
        var packet = new Packet(Interlocked.Increment(ref _seq), kind, Stopwatch.GetTimestamp(), buffer, payload.Length);

        if (!_channel.Writer.TryWrite(packet))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return false;
        }
        Interlocked.Increment(ref _published);
        return true;
    }

    /// <summary>합성 프리뷰 스트림(영상 30fps 16KB + 오디오 20ms 3840B) 시작 — 미기동이면 자동 기동.</summary>
    public void StartDemo(int fps = 30)
    {
        if (_disposed || _demo is not null) return;
        if (State == EdgeBroadcastState.Stopped) Start();

        var interval = 1000 / Math.Clamp(fps, 1, 120);
        var frame = new byte[16 * 1024];
        var audio = new byte[3840];
        var tick = 0;
        _demo = new System.Threading.Timer(_ =>
        {
            var t = Interlocked.Increment(ref tick);
            frame[0] = (byte)t;
            audio[0] = (byte)t;
            Publish(EdgePacketKind.VideoFrame, frame);
            Publish(EdgePacketKind.AudioChunk, audio);
            if (t % 30 == 0) Touch("데모 스트림 " + t.ToString(CultureInfo.InvariantCulture) + "프레임");
        }, null, 0, interval);
        Touch("데모 스트림 시작 · " + fps.ToString(CultureInfo.InvariantCulture) + "fps");
    }

    /// <summary>합성 스트림 정지.</summary>
    public void StopDemo()
    {
        var t = Interlocked.Exchange(ref _demo, null);
        if (t is null) return;
        t.Dispose();
        Touch("데모 스트림 정지");
    }

    /// <summary>
    /// 루프백 프로브 구독자를 접속시켜 수신측 종단 지연(발행→수신 헤더 파싱)을 실측한다.
    /// 소켓 미기동/실패 시 false(1초 타임아웃).
    /// </summary>
    public async Task<bool> ConnectProbeAsync()
    {
        if (State != EdgeBroadcastState.Live || Port == 0 || _cts is null) return false;
        var ct = _cts.Token;
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _probePending = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1000);
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            client.Dispose();
            LastError = "프로브 접속 실패: " + ex.Message;
            Touch("프로브 실패");
            return false;
        }

        _ = Task.Run(() => ProbeReadLoopAsync(client, ct));
        Touch("루프백 프로브 접속");
        return true;
    }

    /// <summary>통계 초기화(구독/기동 상태 유지).</summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _published, 0);
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _bytes, 0);
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _withinSla, 0);
        Interlocked.Exchange(ref _late, 0);
        Interlocked.Exchange(ref _probeReceived, 0);
        _ewmaMs = 0d;
        _probeEwmaMs = 0d;
        lock (_sampleGate) { _sampleCount = 0; _sampleCursor = 0; }
        Touch("통계 초기화");
    }

    // ── 내부 루프 ──

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            socket.NoDelay = true;
            socket.SendTimeout = 250;
            var id = Interlocked.Increment(ref _nextSubId);
            var sub = new Subscriber(id, socket, socket.RemoteEndPoint?.ToString() ?? "edge", socket.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address) && _probePending);
            _probePending = false;
            _subs[id] = sub;
            Touch("엣지 구독 #" + id.ToString(CultureInfo.InvariantCulture) + " " + sub.Endpoint);
        }
    }

    private async Task PumpAsync(Channel<Packet> channel, CancellationToken ct)
    {
        var reader = channel.Reader;
        var header = new byte[HeaderBytes];
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var p))
                {
                    try
                    {
                        WriteHeader(header, p);
                        var delivered = 0;
                        foreach (var s in _subs.Values)
                        {
                            try
                            {
                                await s.Socket.SendAsync(header.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false);
                                if (p.Length > 0) await s.Socket.SendAsync(p.Buffer.AsMemory(0, p.Length), SocketFlags.None, ct).ConfigureAwait(false);
                                Interlocked.Increment(ref s.Packets);
                                Interlocked.Add(ref s.Bytes, HeaderBytes + p.Length);
                                delivered++;
                            }
                            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                            {
                                if (_subs.TryRemove(s.Id, out var gone)) CloseSubscriber(gone);
                                Touch("엣지 이탈 #" + s.Id.ToString(CultureInfo.InvariantCulture));
                            }
                        }

                        try { OnPacket?.Invoke(p.Kind, p.Seq, p.Length); } catch { /* 구독자 예외 격리 */ }

                        Interlocked.Add(ref _sent, delivered);
                        Interlocked.Add(ref _bytes, (long)delivered * (HeaderBytes + p.Length));
                        Record(Stopwatch.GetElapsedTime(p.Timestamp).TotalMilliseconds);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(p.Buffer);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            while (reader.TryRead(out var left)) ArrayPool<byte>.Shared.Return(left.Buffer);
        }
    }

    private async Task ProbeReadLoopAsync(Socket client, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        var sink = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(client, header, HeaderBytes, ct).ConfigureAwait(false)) break;
                if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic) break;
                var ts = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(13));
                var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(21));
                if (len < 0 || len > MaxPayload) break;

                var remaining = len;
                while (remaining > 0)
                {
                    var n = await client.ReceiveAsync(sink.AsMemory(0, Math.Min(sink.Length, remaining)), SocketFlags.None, ct).ConfigureAwait(false);
                    if (n <= 0) { remaining = -1; break; }
                    remaining -= n;
                }
                if (remaining < 0) break;

                var ms = Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
                _probeEwmaMs = _probeEwmaMs <= 0d ? ms : _probeEwmaMs * 0.8d + ms * 0.2d;
                Interlocked.Increment(ref _probeReceived);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(sink);
            try { client.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            client.Dispose();
        }
    }

    private static async Task<bool> ReadExactAsync(Socket s, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await s.ReceiveAsync(buffer.AsMemory(read, count - read), SocketFlags.None, ct).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void WriteHeader(Span<byte> h, in Packet p)
    {
        BinaryPrimitives.WriteUInt32BigEndian(h, Magic);
        BinaryPrimitives.WriteInt64BigEndian(h[4..], p.Seq);
        h[12] = (byte)p.Kind;
        BinaryPrimitives.WriteInt64BigEndian(h[13..], p.Timestamp);
        BinaryPrimitives.WriteInt32BigEndian(h[21..], p.Length);
    }

    private void Record(double ms)
    {
        if (ms < SlaMs) Interlocked.Increment(ref _withinSla);
        else Interlocked.Increment(ref _late);
        _ewmaMs = _ewmaMs <= 0d ? ms : _ewmaMs * 0.8d + ms * 0.2d;
        lock (_sampleGate)
        {
            _samples[_sampleCursor] = ms;
            _sampleCursor = (_sampleCursor + 1) % SampleCap;
            if (_sampleCount < SampleCap) _sampleCount++;
        }
    }

    private Channel<Packet> CreateChannel()
        => Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            dropped =>
            {
                ArrayPool<byte>.Shared.Return(dropped.Buffer);
                Interlocked.Increment(ref _dropped);
            });

    private static void CloseSubscriber(Subscriber s)
    {
        try { s.Socket.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        s.Socket.Dispose();
    }

    private void Touch(string action)
    {
        LastAction = action;
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private readonly record struct Packet(long Seq, EdgePacketKind Kind, long Timestamp, byte[] Buffer, int Length);

    private sealed class Subscriber
    {
        public Subscriber(int id, Socket socket, string endpoint, bool probe)
        {
            Id = id;
            Socket = socket;
            Endpoint = endpoint;
            Probe = probe;
        }

        public int Id { get; }
        public Socket Socket { get; }
        public string Endpoint { get; }
        public bool Probe { get; }
        public long Packets;
        public long Bytes;
    }
}
