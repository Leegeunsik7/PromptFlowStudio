using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>[v9.2.2] 이 단말이 동기화 토폴로지에서 맡은 역할.</summary>
public enum CrossDeviceRole
{
    /// <summary>미시동.</summary>
    Idle = 0,
    /// <summary>데스크톱 Studio — LAN 수신 대기 허브.</summary>
    Host = 1,
    /// <summary>모바일 Deck — 허브 접속 클라이언트.</summary>
    Client = 2,
}

/// <summary>[v9.2.2] 링크 상태.</summary>
public enum CrossDeviceLinkState
{
    /// <summary>미연결.</summary>
    Offline = 0,
    /// <summary>수신 대기(호스트).</summary>
    Listening = 1,
    /// <summary>접속 시도 중.</summary>
    Connecting = 2,
    /// <summary>세션 성립.</summary>
    Connected = 3,
    /// <summary>LAN 실패 → 대체 전송(Bluetooth 훅/로컬 큐)으로 강등.</summary>
    Degraded = 4,
}

/// <summary>[v9.2.2] CRDT 델타 1건(LWW-Register · Lamport + ReplicaId 로 전순서 결정).</summary>
public sealed record CrossDeviceDelta(
    string Key,
    string Value,
    long Lamport,
    string ReplicaId,
    long AtUnixMs)
{
    /// <summary>멱등 dedupe 지문(같은 복제본의 같은 시계는 1회만 반영).</summary>
    public string Fingerprint => ReplicaId + ":" + Lamport.ToString(CultureInfo.InvariantCulture);
}

/// <summary>[v9.2.2] 연결된 상대 단말 스냅샷.</summary>
public sealed record CrossDevicePeer(
    string PeerId,
    string Endpoint,
    bool Connected,
    DateTimeOffset LastSeenAt,
    int Sent,
    int Received);

/// <summary>
/// [PromptFlow Studio v9.2.2] 크로스 디바이스 상태 동기화 파이프라인.
///   ▸ 데스크톱 Studio(호스트)와 모바일 Deck(클라이언트)를 LAN TCP 로 직결하고, 타임라인/프롬프트 변경을 <b>CRDT 델타</b>로 실시간 교환한다.
///   ▸ 충돌 해소는 LWW-Register(Lamport 시계 → 동률 시 ReplicaId 사전순) — 교환법칙·멱등성이 성립해 재전송·순서역전에도 같은 상태로 수렴한다.
///   ▸ 프레임은 <c>{JSON}|{HMAC-SHA256}</c> 라인 포맷이며 수신 측은 <see cref="CryptographicOperations.FixedTimeEquals"/> 상수시간 대조로 위변조를 차단한다.
///   ▸ 제5헌장: LAN 접속 실패 시 예외 전파 없이 <see cref="CrossDeviceLinkState.Degraded"/> 로 내려가 대체 전송(<see cref="AltTransportSender"/> · Bluetooth 등)과 로컬 보류 큐로 이어붙인다.
///   ▸ 외부 NuGet 0 — <c>TcpListener</c>/<c>TcpClient</c>/<c>Channel&lt;T&gt;</c>/<c>System.Text.Json</c> 순수 BCL.
/// </summary>
public sealed class CrossDeviceStateSyncPipeline : IDisposable
{
    private const int DefaultPort = 47821;
    private const int ConnectTimeoutMs = 1_500;
    private const int MaxPendingFrames = 2_048;
    private const int MaxSeenFingerprints = 8_192;
    private const int MaxLogLines = 40;

    private readonly ConcurrentDictionary<string, CrossDeviceDelta> _state = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerChannel> _peers = new(StringComparer.Ordinal);
    private readonly Channel<CrossDeviceDelta> _outbound =
        Channel.CreateBounded<CrossDeviceDelta>(new BoundedChannelOptions(MaxPendingFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly List<string> _log = new(MaxLogLines);
    private readonly object _logSync = new();
    private readonly byte[] _sessionKey;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _pumpLoop;
    private long _lamport;
    private int _sent;
    private int _received;
    private int _merged;
    private int _conflicts;
    private int _duplicates;
    private int _rejected;
    private int _altSent;
    private bool _disposed;

    public CrossDeviceStateSyncPipeline(string? replicaId = null, string? sharedSecret = null)
    {
        ReplicaId = string.IsNullOrWhiteSpace(replicaId)
            ? (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? "deck-" : "studio-") + Guid.NewGuid().ToString("N")[..6]
            : replicaId!.Trim();

        _sessionKey = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(sharedSecret) ? "promptflow-crossdevice-v92" : sharedSecret!));
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>원격 델타가 로컬 상태에 반영될 때 발생(타임라인 재생 훅).</summary>
    public event Action<CrossDeviceDelta>? OnDeltaApplied;

    /// <summary>이 단말의 복제본 식별자.</summary>
    public string ReplicaId { get; }

    /// <summary>현재 역할.</summary>
    public CrossDeviceRole Role { get; private set; } = CrossDeviceRole.Idle;

    /// <summary>현재 링크 상태.</summary>
    public CrossDeviceLinkState LinkState { get; private set; } = CrossDeviceLinkState.Offline;

    /// <summary>수신 대기 포트(호스트 시동 후 유효).</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>
    /// 대체 전송 훅 — LAN 이 끊겼을 때 프레임을 Bluetooth/NFC 등 플랫폼 전송으로 넘긴다.
    /// MAUI 헤드에서 주입하며, 미주입이면 보류 큐에만 적재된다(순수 BCL 에는 블루투스 스택이 없다).
    /// </summary>
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>>? AltTransportSender { get; set; }

    /// <summary>Lamport 논리 시계 현재값.</summary>
    public long Clock => Interlocked.Read(ref _lamport);

    /// <summary>동기화 키 개수.</summary>
    public int KeyCount => _state.Count;

    /// <summary>연결(또는 등록)된 상대 단말 수.</summary>
    public int PeerCount => _peers.Count;

    /// <summary>실제 연결 유지 중인 상대 수.</summary>
    public int ConnectedPeerCount
    {
        get
        {
            var n = 0;
            foreach (var kv in _peers) if (kv.Value.IsConnected) n++;
            return n;
        }
    }

    /// <summary>송신 프레임 수.</summary>
    public int SentCount => Volatile.Read(ref _sent);

    /// <summary>수신 프레임 수.</summary>
    public int ReceivedCount => Volatile.Read(ref _received);

    /// <summary>로컬 상태에 병합 반영된 델타 수.</summary>
    public int MergedCount => Volatile.Read(ref _merged);

    /// <summary>LWW 로 해소된 충돌(같은 키 동시 갱신) 수.</summary>
    public int ConflictCount => Volatile.Read(ref _conflicts);

    /// <summary>멱등 차단된 중복 델타 수.</summary>
    public int DuplicateCount => Volatile.Read(ref _duplicates);

    /// <summary>HMAC 불일치로 폐기된 프레임 수.</summary>
    public int RejectedCount => Volatile.Read(ref _rejected);

    /// <summary>대체 전송으로 넘어간 프레임 수.</summary>
    public int AltSentCount => Volatile.Read(ref _altSent);

    /// <summary>미전송 보류 프레임 수.</summary>
    public int PendingCount => _outbound.Reader.Count;

    /// <summary>수렴률(수신 대비 반영+중복 = 처리 완료 비율 · 0~1).</summary>
    public double ConvergenceRatio
    {
        get
        {
            var rx = ReceivedCount;
            return rx == 0 ? 1d : Math.Clamp((MergedCount + DuplicateCount) / (double)rx, 0d, 1d);
        }
    }

    /// <summary>마지막 오류(정상 시 null).</summary>
    public string? LastError { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>마지막 동기화 시각.</summary>
    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>현재 상태 스냅샷(키 사전순).</summary>
    public IReadOnlyList<CrossDeviceDelta> Snapshot
    {
        get
        {
            var list = new List<CrossDeviceDelta>(_state.Count);
            foreach (var kv in _state) list.Add(kv.Value);
            list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }
    }

    /// <summary>상대 단말 스냅샷(최근 수신순).</summary>
    public IReadOnlyList<CrossDevicePeer> Peers
    {
        get
        {
            var list = new List<CrossDevicePeer>(_peers.Count);
            foreach (var kv in _peers) list.Add(kv.Value.ToSnapshot());
            list.Sort(static (a, b) => b.LastSeenAt.CompareTo(a.LastSeenAt));
            return list;
        }
    }

    /// <summary>최근 활동 로그(최신순).</summary>
    public IReadOnlyList<string> ActivityLog
    {
        get { lock (_logSync) { return _log.ToArray(); } }
    }

    // ── 시동/접속 ──

    /// <summary>호스트(데스크톱 Studio) 수신 시작. 포트 점유 시 예외 없이 Degraded 로 내려간다.</summary>
    public Task<bool> StartHostAsync(int port = DefaultPort)
    {
        if (_disposed) return Task.FromResult(false);
        if (_listener is not null) return Task.FromResult(true);

        try
        {
            Port = port <= 0 || port > 65535 ? DefaultPort : port;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();

            Role = CrossDeviceRole.Host;
            LinkState = CrossDeviceLinkState.Listening;
            LastError = null;
            LastAction = "호스트 수신 시작 · 포트 " + Port.ToString(CultureInfo.InvariantCulture);
            Append(LastAction);

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            EnsurePump();
            Notify();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _listener = null;
            Role = CrossDeviceRole.Host;
            LinkState = CrossDeviceLinkState.Degraded;
            LastError = "수신 시작 실패: " + ex.Message;
            Append(LastError);
            EnsurePump();
            Notify();
            return Task.FromResult(false);
        }
    }

    /// <summary>클라이언트(모바일 Deck)로 허브에 접속. 실패는 Degraded + 보류 큐 유지.</summary>
    public async Task<bool> ConnectAsync(string host, int port = DefaultPort, CancellationToken ct = default)
    {
        if (_disposed) return false;

        Role = CrossDeviceRole.Client;
        LinkState = CrossDeviceLinkState.Connecting;
        LastError = null;
        Notify();

        var target = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        Port = port <= 0 || port > 65535 ? DefaultPort : port;

        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(ConnectTimeoutMs);

            await client.ConnectAsync(target, Port, linked.Token).ConfigureAwait(false);

            var peer = Register(client, target + ":" + Port.ToString(CultureInfo.InvariantCulture));
            LinkState = CrossDeviceLinkState.Connected;
            LastAction = "허브 접속 · " + peer.Endpoint;
            Append(LastAction);

            EnsurePump();
            _ = Task.Run(() => ReadLoopAsync(peer, _cts.Token));

            // 접속 즉시 로컬 상태 전량을 밀어 넣어 초기 수렴을 만든다(멱등이므로 중복 안전).
            foreach (var d in Snapshot) Enqueue(d);

            Notify();
            return true;
        }
        catch (Exception ex)
        {
            client?.Dispose();
            LinkState = CrossDeviceLinkState.Degraded;
            LastError = "접속 실패: " + ex.Message + " — 대체 전송/보류 큐로 강등";
            Append(LastError);
            EnsurePump();
            Notify();
            return false;
        }
    }

    /// <summary>링크 종료(상태는 보존 · 보류 큐 유지).</summary>
    public void Disconnect()
    {
        foreach (var kv in _peers)
        {
            kv.Value.Close();
            _peers.TryRemove(kv.Key, out _);
        }

        try { _listener?.Stop(); } catch { /* 종료 경합 무시 */ }
        _listener = null;

        LinkState = CrossDeviceLinkState.Offline;
        Role = CrossDeviceRole.Idle;
        LastAction = "링크 종료 · 상태 " + KeyCount.ToString(CultureInfo.InvariantCulture) + "키 보존";
        Append(LastAction);
        Notify();
    }

    // ── 델타 발행/병합 ──

    /// <summary>로컬 변경 발행(타임라인 클립 이동·프롬프트 편집 등). 즉시 로컬 반영 후 브로드캐스트 큐에 적재한다.</summary>
    public CrossDeviceDelta Publish(string key, string value)
    {
        var delta = new CrossDeviceDelta(
            string.IsNullOrWhiteSpace(key) ? "state" : key.Trim(),
            value ?? string.Empty,
            Interlocked.Increment(ref _lamport),
            ReplicaId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (_disposed) return delta;

        Merge(delta, local: true);
        Enqueue(delta);
        return delta;
    }

    /// <summary>키 조회(없으면 null).</summary>
    public string? Get(string key)
        => string.IsNullOrWhiteSpace(key) ? null : (_state.TryGetValue(key.Trim(), out var d) ? d.Value : null);

    /// <summary>
    /// 외부 전송(Bluetooth/NFC 등)이 수신한 원시 프레임을 파이프라인에 주입한다.
    /// LAN 수신과 완전히 같은 검증·병합 경로를 탄다.
    /// </summary>
    public bool ReceiveExternalFrame(string frame)
    {
        if (_disposed || string.IsNullOrWhiteSpace(frame)) return false;

        Interlocked.Increment(ref _received);
        var ok = TryParse(frame, out var delta) && delta is not null && Merge(delta, local: false);
        Notify();
        return ok;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* 이미 취소됨 */ }

        foreach (var kv in _peers) kv.Value.Close();
        _peers.Clear();

        try { _listener?.Stop(); } catch { /* 종료 경합 무시 */ }
        _listener = null;

        _outbound.Writer.TryComplete();
        _cts.Dispose();

        OnChange = null;
        OnDeltaApplied = null;
    }

    // ── 내부: 수신/송신 루프 ──

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "peer";
                var peer = Register(client, endpoint);

                LinkState = CrossDeviceLinkState.Connected;
                LastAction = "Deck 연결 · " + endpoint;
                Append(LastAction);
                Notify();

                _ = Task.Run(() => ReadLoopAsync(peer, ct), ct);

                foreach (var d in Snapshot) Enqueue(d);   // 신규 Deck 초기 수렴
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = "수락 실패: " + ex.Message;
                Notify();
                return;
            }
        }
    }

    private async Task ReadLoopAsync(PeerChannel peer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && peer.IsConnected)
            {
                var line = await peer.Reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                Interlocked.Increment(ref _received);
                peer.MarkReceived();

                if (TryParse(line, out var delta) && delta is not null)
                {
                    if (Merge(delta, local: false)) Enqueue(delta, exceptPeerId: peer.PeerId);   // 허브 중계
                }

                Notify();
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            LastError = "수신 중단: " + ex.Message;
        }
        finally
        {
            peer.Close();
            _peers.TryRemove(peer.PeerId, out _);
            if (ConnectedPeerCount == 0 && LinkState == CrossDeviceLinkState.Connected)
                LinkState = _listener is not null ? CrossDeviceLinkState.Listening : CrossDeviceLinkState.Degraded;
            Notify();
        }
    }

    private void EnsurePump() => _pumpLoop ??= Task.Run(() => PumpLoopAsync(_cts.Token));

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_outbound.Reader.TryRead(out var delta))
                {
                    var frame = BuildFrame(delta);
                    var delivered = false;

                    foreach (var kv in _peers)
                    {
                        if (!kv.Value.IsConnected) continue;
                        if (await kv.Value.SendAsync(frame, ct).ConfigureAwait(false))
                        {
                            delivered = true;
                            Interlocked.Increment(ref _sent);
                        }
                    }

                    if (!delivered)
                    {
                        var alt = AltTransportSender;
                        if (alt is not null)
                        {
                            try
                            {
                                if (await alt(Encoding.UTF8.GetBytes(frame), ct).ConfigureAwait(false))
                                {
                                    Interlocked.Increment(ref _altSent);
                                    delivered = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                LastError = "대체 전송 실패: " + ex.Message;
                            }
                        }
                    }

                    if (delivered)
                    {
                        LastSyncAt = DateTimeOffset.UtcNow;
                        Notify();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
        catch (Exception ex)
        {
            LastError = "송신 펌프 중단: " + ex.Message;
            Notify();
        }
    }

    private void Enqueue(CrossDeviceDelta delta, string? exceptPeerId = null)
    {
        _ = exceptPeerId;   // 허브 중계 시 역전송 억제는 dedupe 지문이 담당(수신 측에서 멱등 차단)
        if (!_outbound.Writer.TryWrite(delta)) Interlocked.Increment(ref _duplicates);
    }

    // ── 내부: CRDT 병합(LWW-Register) ──

    private bool Merge(CrossDeviceDelta delta, bool local)
    {
        if (!_seen.TryAdd(delta.Fingerprint, 1))
        {
            Interlocked.Increment(ref _duplicates);
            return false;
        }

        if (_seen.Count > MaxSeenFingerprints) TrimSeen();

        // 수신 델타의 논리 시계를 흡수(Lamport 규칙: max(local, remote) + 1).
        if (!local)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref _lamport);
                if (current >= delta.Lamport) break;
            }
            while (Interlocked.CompareExchange(ref _lamport, delta.Lamport, current) != current);
        }

        var applied = false;
        _state.AddOrUpdate(
            delta.Key,
            _ => { applied = true; return delta; },
            (_, existing) =>
            {
                if (Wins(delta, existing))
                {
                    if (!string.Equals(existing.ReplicaId, delta.ReplicaId, StringComparison.Ordinal))
                        Interlocked.Increment(ref _conflicts);
                    applied = true;
                    return delta;
                }

                Interlocked.Increment(ref _conflicts);
                return existing;
            });

        if (!applied) return false;

        Interlocked.Increment(ref _merged);
        LastSyncAt = DateTimeOffset.UtcNow;
        if (!local)
        {
            LastAction = "원격 병합 · " + delta.Key + "@" + delta.Lamport.ToString(CultureInfo.InvariantCulture);
            Append(LastAction);
            OnDeltaApplied?.Invoke(delta);
        }

        return true;
    }

    /// <summary>LWW 전순서 — Lamport 우선, 동률이면 ReplicaId 사전순(모든 복제본에서 동일 결정).</summary>
    private static bool Wins(CrossDeviceDelta candidate, CrossDeviceDelta existing)
        => candidate.Lamport != existing.Lamport
            ? candidate.Lamport > existing.Lamport
            : string.CompareOrdinal(candidate.ReplicaId, existing.ReplicaId) > 0;

    private void TrimSeen()
    {
        var over = _seen.Count - MaxSeenFingerprints / 2;
        foreach (var kv in _seen)
        {
            if (over-- <= 0) break;
            _seen.TryRemove(kv.Key, out _);
        }
    }

    // ── 내부: 프레임 직렬화 + HMAC ──

    private string BuildFrame(CrossDeviceDelta delta)
    {
        var json = JsonSerializer.Serialize(delta, PromptFlowJsonContext.Default.CrossDeviceDelta);
        return json + "|" + Sign(json);
    }

    private bool TryParse(string frame, out CrossDeviceDelta? delta)
    {
        delta = null;

        var cut = frame.LastIndexOf('|');
        if (cut <= 0 || cut >= frame.Length - 1)
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }

        var json = frame[..cut];
        var mac = frame[(cut + 1)..];

        if (!VerifySignature(json, mac))
        {
            Interlocked.Increment(ref _rejected);
            LastError = "HMAC 불일치 프레임 폐기";
            return false;
        }

        try
        {
            delta = JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.CrossDeviceDelta);
            if (delta is null || string.IsNullOrWhiteSpace(delta.Key) || string.IsNullOrWhiteSpace(delta.ReplicaId))
            {
                Interlocked.Increment(ref _rejected);
                delta = null;
                return false;
            }

            return true;
        }
        catch
        {
            Interlocked.Increment(ref _rejected);
            delta = null;
            return false;
        }
    }

    private string Sign(string json)
        => Convert.ToHexString(HMACSHA256.HashData(_sessionKey, Encoding.UTF8.GetBytes(json)));

    /// <summary>상수시간 대조(타이밍 공격 차단).</summary>
    private bool VerifySignature(string json, string macHex)
    {
        try
        {
            var expected = HMACSHA256.HashData(_sessionKey, Encoding.UTF8.GetBytes(json));
            var actual = Convert.FromHexString(macHex);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    // ── 내부: 피어 등록/로그 ──

    private PeerChannel Register(TcpClient client, string endpoint)
    {
        var peer = new PeerChannel(Guid.NewGuid().ToString("N")[..8], endpoint, client);
        _peers[peer.PeerId] = peer;
        return peer;
    }

    private void Append(string line)
    {
        lock (_logSync)
        {
            _log.Insert(0, DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line);
            if (_log.Count > MaxLogLines) _log.RemoveRange(MaxLogLines, _log.Count - MaxLogLines);
        }
    }

    private void Notify() => OnChange?.Invoke();

    /// <summary>TCP 상대 1개의 송수신 채널(라인 프레이밍).</summary>
    private sealed class PeerChannel
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private int _sent;
        private int _received;
        private volatile bool _closed;

        public PeerChannel(string peerId, string endpoint, TcpClient client)
        {
            PeerId = peerId;
            Endpoint = endpoint;
            _client = client;

            var stream = client.GetStream();
            Reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = false };
            LastSeenAt = DateTimeOffset.UtcNow;
        }

        public string PeerId { get; }
        public string Endpoint { get; }
        public StreamReader Reader { get; }
        public DateTimeOffset LastSeenAt { get; private set; }
        public bool IsConnected => !_closed && _client.Connected;

        public async Task<bool> SendAsync(string frame, CancellationToken ct)
        {
            if (!IsConnected) return false;

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(frame.AsMemory(), ct).ConfigureAwait(false);
                await _writer.FlushAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
                LastSeenAt = DateTimeOffset.UtcNow;
                return true;
            }
            catch
            {
                Close();
                return false;
            }
            finally
            {
                if (!_closed) _sendGate.Release();
            }
        }

        public void MarkReceived()
        {
            Interlocked.Increment(ref _received);
            LastSeenAt = DateTimeOffset.UtcNow;
        }

        public CrossDevicePeer ToSnapshot()
            => new(PeerId, Endpoint, IsConnected, LastSeenAt, Volatile.Read(ref _sent), Volatile.Read(ref _received));

        public void Close()
        {
            if (_closed) return;
            _closed = true;

            try { _writer.Dispose(); } catch { /* 종료 경합 무시 */ }
            try { Reader.Dispose(); } catch { /* 종료 경합 무시 */ }
            try { _client.Close(); } catch { /* 종료 경합 무시 */ }
            try { _sendGate.Release(); } catch { /* 미점유 상태 */ }
            _sendGate.Dispose();
        }
    }
}
