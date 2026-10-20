using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>탐지된 피어 1건 — UDP 핑/퐁 왕복으로 확보된 주소/TCP 포트.</summary>
public sealed record PeerInfo(string NodeId, IPAddress Address, int TcpPort, DateTimeOffset LastSeen)
{
    /// <summary>v12.2.0-Phase4: 피어의 UDP 디스커버리 송신 포트(유니캐스트 RTT 프로브 대상). 수신 경로에서 채워짐.</summary>
    public int UdpPort { get; init; }
}

/// <summary>v12.2.0-Phase4: Ping→Pong 왕복 시간 1건(값 형식). RTT 는 이 노드 자신의 Stopwatch 타임스탬프를 Pong 이 에코한 값으로 계산 -
///   원격 시계 동기화 불요.</summary>
public readonly record struct PeerRttSample(string NodeId, double RttMs);

/// <summary>오프로드 시도 1건 결과 — HUD/감사용.</summary>
public sealed record RelayResult(bool Ok, bool UsedPeer, string? PeerNodeId, int PayloadBytes, double ElapsedMs, string Message);

/// <summary>
/// [PromptFlow Studio v11.2.0] 순수 BCL P2P 태스크 릴레이 — LAN 상의 다른 PromptFlow Studio 노드를
///   UDP 핑/퐁으로 무설정(Zero-Config) 자동 탐지하고, 로컬이 "오버로드"(자체 동시 처리 임계치 초과 — ⚠
///   HeterogeneousTaskScheduler 는 누적 총 실행 수만 노출해 실시간 재고 신호가 없어, 이 클래스 자신의 동시
///   처리 카운터를 정직한 자체 신호로 사용) 상태일 때 발견된 피어에게 TCP 로 작업을 위임한다.
///   ▸ 피어 부재/연결 실패/타임아웃(400ms) 은 예외 없이 로컬 콜백으로 Fail-Closed 폴백 — 네트워크가 전혀
///     없어도(피어 0개) 정상 동작(항상 로컬 실행).
///   ▸ 피어 측 "원격 처리"는 이 프로젝트 전역의 결정적 시뮬레이션 관례(<see cref="WanPostProcessingEngine"/>
///     의 ApplyMotionInPlace 와 동일한 추상화 수준의 XOR 변형) — 실 신경망/FFT 재계산이 아니라 왕복 자체가
///     올바름을 검증 가능한 자리표시자다.
///   ▸ Start()/Stop() 은 명시적 호출 필요(생성자에서 자동으로 소켓을 열지 않음 — DI 해석만으로 방화벽 팝업/포트
///     점유가 발생하지 않도록).
/// </summary>
public sealed class EdgeP2PTaskRelay : IDisposable
{
    public const int DefaultDiscoveryPort = 47990;
    public const int DefaultTcpPort = 47991;
    private const string PingTag = "PFSP2P-PING";
    private const string PongTag = "PFSP2P-PONG";
    private const int PeerTimeoutMs = 400;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;

    public readonly string NodeId = Guid.NewGuid().ToString("N")[..8];
    private readonly int _discoveryPort;
    private readonly int _tcpPort;
    // v12.2.0-Phase4: 실제 바인드된 포트(구성값 0 = OS 임시 포트 할당 지원). 메시지/진단은 이 값을 쓴다 - 구성값 0 이면 광고 포트가 0 이 되던 문제 방지.
    private int _boundUdpPort, _boundTcpPort;
    public int BoundDiscoveryPort => Volatile.Read(ref _boundUdpPort);
    public int BoundTcpPort => Volatile.Read(ref _boundTcpPort);
    private readonly int _overloadThreshold;
    private readonly object _gate = new();
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetimeCts;
    private UdpClient? _udp;
    private TcpListener? _tcp;
    private int _inFlight;
    private long _localRuns, _peerOffloads, _fallbacks, _pingsSent, _pongsReceived;
    private bool _running, _disposed;

    public event Action? OnChange;
    /// <summary>v12.2.0-Phase4: 타임스탬프 에코 Pong 수신 시 RTT 게시 - <see cref="EdgeRelayProbeBinder"/> 가 P2pLoadBalancer 로 투시.</summary>
    public event Action<PeerRttSample>? OnPeerRtt;
    public string? LastAction { get; private set; }
    public bool Running { get { lock (_gate) return _running; } }
    public IReadOnlyList<PeerInfo> Peers { get { lock (_gate) return _peers.Values.ToList(); } }
    public long LocalRuns => Interlocked.Read(ref _localRuns);
    public long PeerOffloads => Interlocked.Read(ref _peerOffloads);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public long PingsSent => Interlocked.Read(ref _pingsSent);
    public long PongsReceived => Interlocked.Read(ref _pongsReceived);

    public EdgeP2PTaskRelay(int discoveryPort = DefaultDiscoveryPort, int tcpPort = DefaultTcpPort, int overloadThreshold = 2)
    {
        _discoveryPort = discoveryPort;
        _tcpPort = tcpPort;
        _boundUdpPort = discoveryPort;
        _boundTcpPort = tcpPort;
        _overloadThreshold = overloadThreshold;
    }

    /// <summary>UDP 디스커버리 리스너 + TCP 워커 리스너 시작. 포트 점유 등 실패는 그대로 예외(호출측이 재시도/포트 변경 판단).</summary>
    public void Start()
    {
        lock (_gate) { if (_running) return; _running = true; }
        _lifetimeCts?.Dispose();   // v12.1.0: Stop→Start 재시작(HUD 토글) 시 이전 세대 CTS 누수 방지
        _lifetimeCts = new CancellationTokenSource();
        var ct = _lifetimeCts.Token;

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            DisableUdpConnReset(_udp.Client);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _udp.EnableBroadcast = true;

            _tcp = new TcpListener(IPAddress.Any, _tcpPort);
            _tcp.Start();
            Volatile.Write(ref _boundUdpPort, ((IPEndPoint)_udp.Client.LocalEndPoint!).Port);
            Volatile.Write(ref _boundTcpPort, ((IPEndPoint)_tcp.LocalEndpoint).Port);
        }
        catch
        {
            // v12.1.0: 포트 점유 등 바인드 실패 시 _running 이 true 로 남아 이후 Start 가 영구 no-op 되던 문제 롤백.
            try { _udp?.Close(); } catch { /* 무시 */ }
            try { _tcp?.Stop(); } catch { /* 무시 */ }
            lock (_gate) _running = false;
            throw;
        }

        _ = Task.Run(() => UdpListenLoopAsync(ct), ct);
        _ = Task.Run(() => TcpAcceptLoopAsync(ct), ct);

        lock (_gate) LastAction = $"시작됨 - NodeId={NodeId} · UDP:{BoundDiscoveryPort} · TCP:{BoundTcpPort}";
        OnChange?.Invoke();
    }

    public void Stop()
    {
        lock (_gate) { if (!_running) return; _running = false; }
        try { _lifetimeCts?.Cancel(); } catch { /* 무시 */ }
        try { _udp?.Close(); } catch { /* 무시 */ }
        try { _tcp?.Stop(); } catch { /* 무시 */ }
        lock (_gate) LastAction = "중지됨";
        OnChange?.Invoke();
    }

    /// <summary>탐지 핑 발신 — target 이 null 이면 LAN 브로드캐스트, 지정하면(테스트/수동 결선용) 유니캐스트.</summary>
    public async Task PingAsync(IPEndPoint? target = null)
    {
        if (_udp is null) return;
        // v12.2.0-Phase4: 4번째 필드에 로컬 Stopwatch 타임스탬프 - Pong 이 그대로 에코하면 RTT 산출(3필드 구형 Ping 도 계속 수용).
        var msg = Encoding.UTF8.GetBytes($"{PingTag}|{NodeId}|{BoundTcpPort}|{Stopwatch.GetTimestamp()}");
        var dest = target ?? new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
        try { await _udp.SendAsync(msg, msg.Length, dest).ConfigureAwait(false); Interlocked.Increment(ref _pingsSent); }
        catch (Exception ex) { Program.Log("EdgeP2P.Ping", ex); }
    }

    /// <summary>v12.2.0: 수신/수락 루프가 일시적 소켓 오류를 흡수하고 백오프 후 복구한 횟수.</summary>
    public long SocketFaultsRecovered => Interlocked.Read(ref _socketFaultsRecovered);
    private long _socketFaultsRecovered;

    internal const int BackoffInitialMs = 10;
    internal const int BackoffMaxMs = 2000;

    /// <summary>지수 백오프 다음 간격(10→20→40…→2000ms 상한).</summary>
    internal static int NextBackoffMs(int currentMs) => currentMs <= 0 ? BackoffInitialMs : Math.Min(currentMs * 2, BackoffMaxMs);

    /// <summary>재시도로 회복 가능한(소켓 자체는 살아 있는) 오류인지. 그 외(OperationAborted/Interrupted/NotSocket 등)는 종료 신호로 간주.</summary>
    internal static bool IsTransientSocketError(SocketError e) => e is
        SocketError.ConnectionReset or SocketError.NetworkUnreachable or SocketError.HostUnreachable or
        SocketError.NetworkDown or SocketError.NetworkReset or SocketError.HostDown or SocketError.MessageSize or
        SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.NoBufferSpaceAvailable or
        SocketError.TryAgain or SocketError.WouldBlock;

    // Windows: 유니캐스트 Pong/Ping 대상 포트가 닫혀 있으면 ICMP Port Unreachable 이 돌아오고, 그 다음 ReceiveFrom 이
    //   WSAECONNRESET(10054)으로 실패한다(UDP 인데도). SIO_UDP_CONNRESET 을 꺼서 이 오보고 자체를 차단.
    private static void DisableUdpConnReset(Socket s)
    {
#if WINDOWS
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { /* 미지원 스택 - 루프 백오프가 2차 방어 */ }
#endif
    }

    private async Task UdpListenLoopAsync(CancellationToken ct)
    {
        var backoffMs = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult recv;
                try { recv = await _udp!.ReceiveAsync(ct).ConfigureAwait(false); backoffMs = 0; }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException sx) when (IsTransientSocketError(sx.SocketErrorCode) && !ct.IsCancellationRequested)
                {
                    // v12.2.0: 이전엔 모든 SocketException 에서 break - ConnectionReset 1회로 탐지 루프가 영구 사망(Running=true 인 채 무응답).
                    //   즉시 재시도는 네트워크 단절 시 CPU 100% 스핀이 되므로 지수 백오프 후 같은 소켓으로 계속.
                    Interlocked.Increment(ref _socketFaultsRecovered);
                    backoffMs = NextBackoffMs(backoffMs);
                    try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;
                }
                catch (SocketException) { break; }

                var text = Encoding.UTF8.GetString(recv.Buffer);
                var parts = text.Split('|');
                if ((parts.Length != 3 && parts.Length != 4) || !int.TryParse(parts[2], out var remoteTcpPort)) continue;
                if (parts[1] == NodeId) continue;   // 자기 자신의 브로드캐스트 루프백 무시

                if (parts[0] == PingTag)
                {
                    UpsertPeer(parts[1], recv.RemoteEndPoint.Address, remoteTcpPort, recv.RemoteEndPoint.Port);
                    var pong = Encoding.UTF8.GetBytes(parts.Length == 4 ? $"{PongTag}|{NodeId}|{BoundTcpPort}|{parts[3]}" : $"{PongTag}|{NodeId}|{BoundTcpPort}");
                    try { await _udp.SendAsync(pong, pong.Length, recv.RemoteEndPoint).ConfigureAwait(false); } catch { /* 무시 */ }
                }
                else if (parts[0] == PongTag)
                {
                    Interlocked.Increment(ref _pongsReceived);   // v12.2.0: 카운터를 OnChange(UpsertPeer 내부) 발행 전에 갱신 - 이전 순서는 구독자(HUD)가 갱신 전 값을 읽음
                    UpsertPeer(parts[1], recv.RemoteEndPoint.Address, remoteTcpPort, recv.RemoteEndPoint.Port);
                    if (parts.Length == 4 && long.TryParse(parts[3], out var sentTs))
                    {
                        var rttMs = (Stopwatch.GetTimestamp() - sentTs) * 1000.0 / Stopwatch.Frequency;
                        if (rttMs >= 0 && rttMs < 60_000) OnPeerRtt?.Invoke(new PeerRttSample(parts[1], rttMs));   // 위조/재전송 값 범위 방어
                    }
                }
            }
        }
        catch (Exception ex) { Program.Log("EdgeP2P.UdpLoop", ex); }
    }

    private void UpsertPeer(string nodeId, IPAddress address, int tcpPort, int udpPort)
    {
        lock (_gate) _peers[nodeId] = new PeerInfo(nodeId, address, tcpPort, DateTimeOffset.UtcNow) { UdpPort = udpPort };
        OnChange?.Invoke();
    }

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        var backoffMs = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _tcp!.AcceptTcpClientAsync(ct).ConfigureAwait(false); backoffMs = 0; }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException sx) when (IsTransientSocketError(sx.SocketErrorCode) && !ct.IsCancellationRequested)
                {
                    // 수락 직전 클라이언트 RST 등 - 리스너는 살아 있으므로 백오프 후 계속(이전엔 break 로 워커 수락 영구 중단).
                    Interlocked.Increment(ref _socketFaultsRecovered);
                    backoffMs = NextBackoffMs(backoffMs);
                    try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;
                }
                catch (SocketException) { break; }
                _ = HandlePeerRequestAsync(client, ct);
            }
        }
        catch (Exception ex) { Program.Log("EdgeP2P.TcpAcceptLoop", ex); }
    }

    private static async Task HandlePeerRequestAsync(TcpClient client, CancellationToken lifetimeCt)
    {
        try
        {
            // v12.2.0: 요청 처리에 타임아웃 부여 - 이전엔 길이만 보내고 멈춘 피어(slow-loris)가 Stop 전까지 연결/버퍼를 무기한 점유.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCt);
            timeoutCts.CancelAfter(PeerTimeoutMs);
            var ct = timeoutCts.Token;
            using (client)
            using (var stream = client.GetStream())
            {
                var lenBuf = new byte[4];
                await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
                var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (len <= 0 || len > MaxPayloadBytes) return;
                var payload = new byte[len];
                await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);

                ApplyDeterministicStubTransform(payload);

                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);
                await stream.WriteAsync(lenBuf, ct).ConfigureAwait(false);
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Program.Log("EdgeP2P.HandleRequest", ex); }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("피어 연결 조기 종료");
            offset += n;
        }
    }

    // Wan 후처리/FFT "원격 처리" 스텁 — WanPostProcessingEngine.ApplyMotionInPlace 와 동일 추상화 수준(결정적 XOR 변형).
    private static void ApplyDeterministicStubTransform(byte[] payload)
    {
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ 0x5A ^ (byte)i);
    }

    /// <summary>로컬 "오버로드"(자체 동시 처리 임계치 초과) 시 발견된 피어로 오프로드 시도 — 피어 없음/실패/
    ///   타임아웃은 예외 없이 로컬 폴백(<paramref name="localWork"/>)으로 자율 대체(Fail-Closed).</summary>
    public async Task<(RelayResult Relay, byte[] Result)> RunOrOffloadAsync(byte[] payload, Func<byte[], CancellationToken, Task<byte[]>> localWork, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var inFlight = Interlocked.Increment(ref _inFlight);
        try
        {
            var overloaded = inFlight > _overloadThreshold;
            var peer = overloaded ? Peers.FirstOrDefault() : null;

            if (peer is not null)
            {
                var offloaded = await TryOffloadToPeerAsync(peer, payload, ct).ConfigureAwait(false);
                if (offloaded is not null)
                {
                    Interlocked.Increment(ref _peerOffloads);
                    sw.Stop();
                    var okResult = new RelayResult(true, true, peer.NodeId, offloaded.Length, sw.Elapsed.TotalMilliseconds, $"피어({peer.NodeId}) 오프로드 성공");
                    lock (_gate) LastAction = okResult.Message;
                    OnChange?.Invoke();
                    return (okResult, offloaded);
                }
            }

            var local = await localWork(payload, ct).ConfigureAwait(false);
            sw.Stop();
            Interlocked.Increment(ref _localRuns);
            if (overloaded) Interlocked.Increment(ref _fallbacks);
            var result = new RelayResult(true, false, null, local.Length, sw.Elapsed.TotalMilliseconds,
                overloaded ? "오버로드였으나 피어 오프로드 실패/부재 - 로컬로 Fail-Closed 폴백" : "로컬 실행(오버로드 아님)");
            lock (_gate) LastAction = result.Message;
            OnChange?.Invoke();
            return (result, local);
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private async Task<byte[]?> TryOffloadToPeerAsync(PeerInfo peer, byte[] payload, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PeerTimeoutMs);
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(peer.Address, peer.TcpPort);
            var completed = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
            if (completed != connectTask) return null;
            await connectTask.ConfigureAwait(false);   // 연결 자체 실패 시 예외 전파(아래 catch 에서 흡수)

            using var stream = client.GetStream();
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);
            await stream.WriteAsync(lenBuf, timeoutCts.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, timeoutCts.Token).ConfigureAwait(false);

            await ReadExactAsync(stream, lenBuf, timeoutCts.Token).ConfigureAwait(false);
            var respLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (respLen <= 0 || respLen > MaxPayloadBytes) return null;
            var resp = new byte[respLen];
            await ReadExactAsync(stream, resp, timeoutCts.Token).ConfigureAwait(false);
            return resp;
        }
        catch (Exception ex) { Program.Log("EdgeP2P.Offload", ex); return null; }
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _localRuns, 0);
        Interlocked.Exchange(ref _peerOffloads, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _pingsSent, 0);
        Interlocked.Exchange(ref _pongsReceived, 0);
        lock (_gate) { _peers.Clear(); LastAction = "카운터/피어 초기화"; }
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _udp?.Dispose();
        _lifetimeCts?.Dispose();
    }
}