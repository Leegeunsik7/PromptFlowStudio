using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Teleport;

public enum XrDeviceKind : byte { VisionPro, Quest3, WebXr, HologramDisplay }

/// <summary>이종 디바이스 프로파일 — 표시 FPS · 수신 대역 예산 · 좌표계 변환(스케일 · Z 부호 · 원점 Y 오프셋) · 뷰 수.</summary>
public readonly record struct XrDeviceProfile(XrDeviceKind Kind, string Label, string Runtime, int DisplayFps, int MaxKbps, float UnitScale, float ZSign, float OriginYOffset, int Views)
{
    public static XrDeviceProfile For(XrDeviceKind k) => k switch
    {
        XrDeviceKind.VisionPro => new(k, "Apple Vision Pro", "visionOS RealityKit · 우수계 Y-up m", 90, 40000, 1f, 1f, 0f, 2),
        XrDeviceKind.Quest3 => new(k, "Meta Quest 3", "OpenXR stage · 우수계 Y-up m", 72, 25000, 1f, 1f, 0f, 2),
        XrDeviceKind.WebXr => new(k, "WebXR 브라우저", "WebXR local-floor · 우수계 Y-up m", 60, 12000, 1f, 1f, 0f, 2),
        _ => new(k, "홀로그램 디스플레이", "라이트필드 퀼트 · 좌수계 정규화(0.5/m · 눈높이 원점)", 30, 30000, 0.5f, -1f, -1.2f, 45),
    };

    public Vector3 ToDevice(Vector3 p) => new(p.X * UnitScale, (p.Y + OriginYOffset) * UnitScale, p.Z * ZSign * UnitScale);
}

/// <summary>텔레프레젠스 참가 디바이스(링크 모델 · 코덱 상태 · 송신자별 재조립/디코더 · 시계 동기 · 통계).</summary>
public sealed class TelepresencePeer
{
    internal TelepresencePeer(int index, XrDeviceProfile profile) { Index = index; Profile = profile; }

    public int Index { get; }
    public XrDeviceProfile Profile { get; }
    public bool Connected { get; internal set; }
    public float UpMs { get; set; }
    public float DownMs { get; set; }
    public float JitterMs { get; set; }
    public float LossRate { get; set; }
    /// <summary>시뮬레이션 진실값: 디바이스 시계 = 릴레이 시계 + 오프셋.</summary>
    public long ClockOffsetMicros { get; set; }
    public double EstimatedOffsetMicros { get; internal set; }
    public double RttMicros { get; internal set; }
    public float AdaptiveThreshold { get; internal set; }
    public double SendKbps { get; internal set; }
    public long FramesSent, FramesPresented, FramesLate, DecodeFailures, KeyRequests;
    public float LastLatencyMs { get; internal set; }

    internal readonly TeleportMeshCodec Encoder = new();
    internal readonly TeleportMeshFrame Local = new();
    internal byte[] Scratch = Array.Empty<byte>();
    internal byte[] Packets = Array.Empty<byte>();
    internal readonly int[] Lens = new int[TeleportPacket.MaxFragments + 1];
    internal readonly TeleportFrameAssembler?[] Asm = new TeleportFrameAssembler?[HolographicTelepresenceRelayBridge.MaxPeers];
    internal readonly TeleportMeshCodec?[] Dec = new TeleportMeshCodec?[HolographicTelepresenceRelayBridge.MaxPeers];
    internal readonly TeleportMeshFrame?[] Remote = new TeleportMeshFrame?[HolographicTelepresenceRelayBridge.MaxPeers];
    internal readonly long[] ReceivedFrom = new long[HolographicTelepresenceRelayBridge.MaxPeers];
    internal long NextFrameMicros, NextSyncMicros, UpTail, DownTail, WindowBytes, WindowStart;
    internal double BestRtt = double.MaxValue;
    internal uint Seq;
    internal bool KeyRequested, Synced;

    public long ReceivedFromPeer(int sender) => Interlocked.Read(ref ReceivedFrom[sender]);
    public double OffsetErrorMicros => Math.Abs(EstimatedOffsetMicros - ClockOffsetMicros);
}

/// <summary>외부 전송 결선점(실 UDP/WebRTC/QUIC) — 릴레이가 수신 디바이스로 내보내는 다운링크 패킷.</summary>
public delegate void TelepresencePacketSink(int toPeer, ReadOnlySpan<byte> packet);

/// <summary>
/// [PromptFlow Studio v16.55.0] 🛰 홀로그래픽 텔레프레젠스 중계 브릿지(SFU · Pure BCL · 무할당 틱).
///   ▸ 이종 디바이스(Vision Pro · Quest 3 · WebXR · 홀로그램 디스플레이) 프로파일 · 좌표계 변환 · 쌍방향 N:N 팬아웃.
///   ▸ 전송: 디바이스별 업/다운 링크 모델(지연 · 지터 · 손실 · FIFO) → 시간순 최소 힙 패킷 풀(사전 할당 슬롯) → XOR FEC 복원 · 손실 시 키프레임 요청(PLI).
///   ▸ 동기: NTP 4-타임스탬프 오프셋 추정(최소 RTT 필터 · EMA) → 송신 시계 타임스탬프를 릴레이 시계로 환산해 glass-to-glass 지연 측정.
///   ▸ 지연 예산 50ms: 예산 초과 프레임은 디코드(상태 유지)만 하고 표시 생략 · p50/p95 · 수신 대역 예산 초과 송신자 노이즈 임계 적응 상향.
///   ▸ 빔포밍 결합: 참가자 머리 포즈 → <see cref="SpatialAudioVisualBeamformingEngine"/> 시청자 · 트랙 지정.
///   ▸ 라이브 루프는 1ms 시뮬레이션 서브스텝(타이머 해상도 무관 · 테스트와 동일 결정성). 실 네트워크는 <see cref="PacketSink"/>/<see cref="IngestFromDevice"/> 결선.
/// </summary>
public sealed class HolographicTelepresenceRelayBridge : IDisposable
{
    public const int MaxPeers = 6, PoolSlots = 1536, LatencyRing = 512, CaptureFps = 30;
    private const int RelayPeer = -1;

    private readonly object _gate = new();
    private readonly AiVolumetricTeleportationMeshEncoder _encoder;
    private readonly SpatialAudioVisualBeamformingEngine? _beam;
    private readonly TelepresencePeer?[] _peers = new TelepresencePeer?[MaxPeers];
    private readonly byte[] _pool = new byte[PoolSlots * TeleportPacket.Mtu];
    private readonly int[] _len = new int[PoolSlots], _from = new int[PoolSlots], _to = new int[PoolSlots];
    private readonly long[] _due = new long[PoolSlots], _order = new long[PoolSlots];
    private readonly int[] _free = new int[PoolSlots], _heap = new int[PoolSlots];
    private readonly float[] _lat = new float[LatencyRing], _latSort = new float[LatencyRing];
    private int _freeTop, _heapCount, _latPos, _latCount;
    private long _orderSeq, _simNow;
    private uint _rng = 0x9E3779B9u;
    private CancellationTokenSource? _cts;
    private long _delivered, _late, _packetsLost, _fecRecovered, _framesLost, _keyRequests, _poolDrops, _adaptations, _ticks;
    private float _p50, _p95, _max;

    public HolographicTelepresenceRelayBridge(IServiceProvider sp)
        : this((AiVolumetricTeleportationMeshEncoder?)sp.GetService(typeof(AiVolumetricTeleportationMeshEncoder)) ?? new AiVolumetricTeleportationMeshEncoder(),
               sp.GetService(typeof(SpatialAudioVisualBeamformingEngine)) as SpatialAudioVisualBeamformingEngine) { }

    public HolographicTelepresenceRelayBridge(AiVolumetricTeleportationMeshEncoder encoder, SpatialAudioVisualBeamformingEngine? beam = null)
    {
        _encoder = encoder;
        _beam = beam;
        for (var i = 0; i < PoolSlots; i++) _free[i] = PoolSlots - 1 - i;
        _freeTop = PoolSlots;
    }

    public event Action? OnChange;

    public float LatencyBudgetMs { get; set; } = 50f;
    public float SensorNoiseMeters { get; set; } = 0.0015f;
    public bool ParityFec { get; set; } = true;
    public TelepresencePacketSink? PacketSink { get; set; }
    public bool IsRunning => _cts is not null;
    public long NowMicros => Interlocked.Read(ref _simNow);
    public long FramesPresented => Interlocked.Read(ref _delivered);
    public long FramesLate => Interlocked.Read(ref _late);
    public long PacketsLost => Interlocked.Read(ref _packetsLost);
    public long FecRecovered => Interlocked.Read(ref _fecRecovered);
    public long FramesLost => Interlocked.Read(ref _framesLost);
    public long KeyRequests => Interlocked.Read(ref _keyRequests);
    public long PoolDrops => Interlocked.Read(ref _poolDrops);
    public long Adaptations => Interlocked.Read(ref _adaptations);
    public long Ticks => Interlocked.Read(ref _ticks);
    public int InFlight => Volatile.Read(ref _heapCount);
    public float P50Ms => Volatile.Read(ref _p50);
    public float P95Ms => Volatile.Read(ref _p95);
    public float MaxMs => Volatile.Read(ref _max);
    public bool WithinBudget => _latCount > 0 && P95Ms <= LatencyBudgetMs;
    public string? LastAction { get; private set; }

    public TelepresencePeer? Peer(int i) => (uint)i < MaxPeers ? _peers[i] : null;

    public int ConnectedCount { get { var c = 0; for (var i = 0; i < MaxPeers; i++) if (_peers[i] is { Connected: true }) c++; return c; } }

    /// <summary>양방향 동기 쌍 수(서로 1프레임 이상 표시 수신).</summary>
    public int BidirectionalPairs
    {
        get
        {
            var c = 0;
            for (var a = 0; a < MaxPeers; a++)
                for (var b = a + 1; b < MaxPeers; b++)
                    if (_peers[a] is { Connected: true } pa && _peers[b] is { Connected: true } pb && pa.ReceivedFromPeer(b) > 0 && pb.ReceivedFromPeer(a) > 0) c++;
            return c;
        }
    }

    /// <summary>디바이스 접속(빈 슬롯 · 버퍼 1회 할당). 반환 = 인덱스(-1 = 만석).</summary>
    public int Connect(XrDeviceKind kind, float upMs = 12f, float downMs = 12f, float jitterMs = 2f, float lossRate = 0.01f, long clockOffsetMicros = 0)
    {
        var idx = -1;
        lock (_gate)
        {
            for (var i = 0; i < MaxPeers; i++) if (_peers[i] is null || !_peers[i]!.Connected) { idx = i; break; }
            if (idx < 0) return -1;
            var p = _peers[idx] ?? new TelepresencePeer(idx, XrDeviceProfile.For(kind));
            if (p.Profile.Kind != kind) p = new TelepresencePeer(idx, XrDeviceProfile.For(kind));
            if (p.Scratch.Length == 0)
            {
                p.Scratch = new byte[TeleportMeshCodec.MaxFrameBytes];
                p.Packets = new byte[TeleportPacket.MaxPackets(TeleportMeshCodec.MaxFrameBytes) * TeleportPacket.Mtu];
            }
            p.UpMs = upMs; p.DownMs = downMs; p.JitterMs = jitterMs; p.LossRate = Math.Clamp(lossRate, 0f, 0.5f); p.ClockOffsetMicros = clockOffsetMicros;
            p.EstimatedOffsetMicros = 0; p.BestRtt = double.MaxValue; p.Synced = false; p.AdaptiveThreshold = 0;
            p.NextFrameMicros = _simNow + idx * 3_000; p.NextSyncMicros = _simNow; p.UpTail = p.DownTail = _simNow; p.WindowStart = _simNow; p.WindowBytes = 0;
            p.Encoder.Reset(); p.KeyRequested = true;
            p.Connected = true;
            _peers[idx] = p;
            for (var j = 0; j < MaxPeers; j++)
            {
                if (j == idx || _peers[j] is not { Connected: true } q) continue;
                EnsureRx(p, j); EnsureRx(q, idx);
                q.KeyRequested = true;
            }
            _beam?.AssignTrack(idx, idx % SpatialAudioVisualBeamformingEngine.MaxBeams);
            LastAction = "🔗 " + p.Profile.Label + " 접속";
        }
        Raise();
        return idx;
    }

    private static void EnsureRx(TelepresencePeer rx, int sender)
    {
        rx.Asm[sender] ??= new TeleportFrameAssembler();
        rx.Dec[sender] ??= new TeleportMeshCodec();
        rx.Remote[sender] ??= new TeleportMeshFrame();
        rx.Dec[sender]!.Reset();
        Interlocked.Exchange(ref rx.ReceivedFrom[sender], 0);
    }

    public void Disconnect(int index)
    {
        lock (_gate)
        {
            if (Peer(index) is not { Connected: true } p) return;
            p.Connected = false;
            _beam?.RemoveViewer(index);
            LastAction = "⛔ " + p.Profile.Label + " 해제";
        }
        Raise();
    }

    /// <summary>링크 손실률/지연 실시간 조정(관제 UI).</summary>
    public void SetLink(int index, float upMs, float downMs, float jitterMs, float lossRate)
    {
        lock (_gate)
        {
            if (Peer(index) is not { } p) return;
            p.UpMs = upMs; p.DownMs = downMs; p.JitterMs = jitterMs; p.LossRate = Math.Clamp(lossRate, 0f, 0.5f);
        }
    }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f; }

    private long LinkDue(long now, float baseMs, float jitterMs, ref long tail)
    {
        var t = now + (long)((baseMs + (Rand() * 2f - 1f) * jitterMs) * 1000f);
        if (t <= tail) t = tail + 1;
        tail = t;
        return t;
    }

    private bool Enqueue(ReadOnlySpan<byte> packet, int from, int to, long due)
    {
        if (_freeTop == 0) { Interlocked.Increment(ref _poolDrops); return false; }
        var s = _free[--_freeTop];
        packet.CopyTo(_pool.AsSpan(s * TeleportPacket.Mtu, TeleportPacket.Mtu));
        _len[s] = packet.Length; _from[s] = from; _to[s] = to; _due[s] = due; _order[s] = _orderSeq++;
        var i = _heapCount++;
        _heap[i] = s;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (!Less(_heap[i], _heap[parent])) break;
            (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
            i = parent;
        }
        return true;
    }

    private bool Less(int a, int b) => _due[a] < _due[b] || (_due[a] == _due[b] && _order[a] < _order[b]);

    private int PopMin()
    {
        var top = _heap[0];
        _heap[0] = _heap[--_heapCount];
        var i = 0;
        while (true)
        {
            int l = i * 2 + 1, r = l + 1, m = i;
            if (l < _heapCount && Less(_heap[l], _heap[m])) m = l;
            if (r < _heapCount && Less(_heap[r], _heap[m])) m = r;
            if (m == i) break;
            (_heap[i], _heap[m]) = (_heap[m], _heap[i]);
            i = m;
        }
        return top;
    }

    /// <summary>1ms 틱(무할당) — 캡처/인코딩/업링크 → 시간순 전달(릴레이 팬아웃 · 수신 재조립/디코드/지연 측정) → 시계 동기 → 빔포밍 시청자 갱신.</summary>
    public void Tick(long now)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Teleport.Relay.Tick");
            Interlocked.Exchange(ref _simNow, now);
            for (var i = 0; i < MaxPeers; i++)
            {
                if (_peers[i] is not { Connected: true } p) continue;
                if (now >= p.NextSyncMicros) { ClockSync(p, now); p.NextSyncMicros = now + 500_000; }
                if (now >= p.NextFrameMicros)
                {
                    p.NextFrameMicros = Math.Max(p.NextFrameMicros + 1_000_000 / CaptureFps, now - 1_000_000 / CaptureFps);
                    SendFrame(p, now);
                }
            }
            while (_heapCount > 0 && _due[_heap[0]] <= now)
            {
                var s = PopMin();
                var pkt = _pool.AsSpan(s * TeleportPacket.Mtu, _len[s]);
                if (_to[s] == RelayPeer) Forward(pkt, _from[s], now);
                else Receive(pkt, _to[s], now);
                _free[_freeTop++] = s;
            }
            Interlocked.Increment(ref _ticks);
        }
        if ((Interlocked.Read(ref _ticks) & 255) == 0) { ComputeLatencyStats(); Raise(); }
    }

    private void SendFrame(TelepresencePeer p, long now)
    {
        var tSec = now / 1e6;
        _encoder.CaptureLive(tSec, p.Index * 1.3f, p.Local, SensorNoiseMeters);
        p.Local.Seq = p.Seq++;
        if (p.KeyRequested) { p.Encoder.ForceKey = true; p.KeyRequested = false; }
        var ts = (uint)(now + p.ClockOffsetMicros);
        var count = _encoder.EncodeAndPacketize(p.Encoder, p.Local, (ushort)p.Index, ts, p.Scratch, p.Packets, p.Lens, ParityFec, p.AdaptiveThreshold);
        if (count == 0) return;
        Interlocked.Increment(ref p.FramesSent);
        for (var k = 0; k < count; k++)
        {
            var len = p.Lens[k];
            p.WindowBytes += len;
            var due = LinkDue(now, p.UpMs, p.JitterMs, ref p.UpTail);
            if (Rand() < p.LossRate) { Interlocked.Increment(ref _packetsLost); continue; }
            Enqueue(p.Packets.AsSpan(k * TeleportPacket.Mtu, len), p.Index, RelayPeer, due);
        }
        if (now - p.WindowStart >= 1_000_000) Adapt(p, now);
        if (_beam is not null)
        {
            var seat = (p.Index - 1.5f) * 0.9f;
            var head = new Vector3(seat + 0.05f * MathF.Sin((float)tSec * 0.8f + p.Index), 1.62f, 2.2f + 0.04f * MathF.Cos((float)tSec * 0.6f + p.Index));
            _beam.UpdateViewer(p.Index, head, 0.15f * MathF.Sin((float)tSec * 0.5f + p.Index), 0f, now);
            if (p.Index == 0) _beam.Tick(now);
        }
    }

    /// <summary>송신 대역(kbps) 측정 → 최소 수신 예산 초과 시 노이즈 임계 상향(코어스 양자화) · 여유 시 감쇠.</summary>
    private void Adapt(TelepresencePeer p, long now)
    {
        var kbps = p.WindowBytes * 8.0 / 1000.0 / ((now - p.WindowStart) / 1e6);
        p.SendKbps = kbps;
        var budget = int.MaxValue;
        for (var j = 0; j < MaxPeers; j++) if (j != p.Index && _peers[j] is { Connected: true } q) budget = Math.Min(budget, q.Profile.MaxKbps);
        if (budget != int.MaxValue && kbps > budget)
        {
            p.AdaptiveThreshold = MathF.Min(0.01f, p.AdaptiveThreshold + 0.0005f);
            Interlocked.Increment(ref _adaptations);
        }
        else p.AdaptiveThreshold *= 0.9f;
        p.WindowBytes = 0; p.WindowStart = now;
    }

    private void Forward(ReadOnlySpan<byte> pkt, int from, long now)
    {
        for (var j = 0; j < MaxPeers; j++)
        {
            if (j == from || _peers[j] is not { Connected: true } q) continue;
            var due = LinkDue(now + 200, q.DownMs, q.JitterMs, ref q.DownTail);
            if (PacketSink is { } sink)
            {
                try { sink(j, pkt); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { PromptFlowStudio.Program.Log("Teleport.Relay.Sink", ex); }
            }
            if (Rand() < q.LossRate) { Interlocked.Increment(ref _packetsLost); continue; }
            Enqueue(pkt, from, j, due);
        }
    }

    private void Receive(ReadOnlySpan<byte> pkt, int to, long now)
    {
        if (_peers[to] is not { Connected: true } q) return;
        if (!TeleportPacket.TryReadHeader(pkt, out var h) || h.Stream >= MaxPeers || h.Stream == to) return;
        int s = h.Stream;
        if (_peers[s] is not { Connected: true } sender || q.Asm[s] is not { } asm) return;
        var lostBefore = asm.Lost;
        var r = asm.Accept(pkt);
        if (asm.Lost != lostBefore) { Interlocked.Increment(ref _framesLost); RequestKey(sender, q); }
        if (r == AssembleResult.Recovered) Interlocked.Increment(ref _fecRecovered);
        if (r is not (AssembleResult.Complete or AssembleResult.Recovered)) return;
        if (!q.Dec[s]!.Decode(asm.Frame, q.Remote[s]!))
        {
            Interlocked.Increment(ref q.DecodeFailures);
            RequestKey(sender, q);
            return;
        }
        var nowSender = now + (long)sender.EstimatedOffsetMicros;
        var latMs = (int)((uint)nowSender - asm.Timestamp) / 1000f;
        q.LastLatencyMs = latMs;
        _lat[_latPos] = latMs;
        _latPos = (_latPos + 1) % LatencyRing;
        if (_latCount < LatencyRing) _latCount++;
        Interlocked.Increment(ref q.ReceivedFrom[s]);
        if (latMs > LatencyBudgetMs) { Interlocked.Increment(ref q.FramesLate); Interlocked.Increment(ref _late); }
        else { Interlocked.Increment(ref q.FramesPresented); Interlocked.Increment(ref _delivered); }
    }

    private void RequestKey(TelepresencePeer sender, TelepresencePeer rx)
    {
        sender.KeyRequested = true;
        Interlocked.Increment(ref rx.KeyRequests);
        Interlocked.Increment(ref _keyRequests);
    }

    /// <summary>NTP 4-타임스탬프 오프셋 추정(시뮬 교환) — 최소 RTT ×1.25 이내 샘플만 EMA 반영.</summary>
    private void ClockSync(TelepresencePeer p, long now)
    {
        var up = (long)((p.UpMs + (Rand() * 2f - 1f) * p.JitterMs) * 1000f);
        var down = (long)((p.DownMs + (Rand() * 2f - 1f) * p.JitterMs) * 1000f);
        long t0 = now + p.ClockOffsetMicros, t1 = now + up, t2 = t1, t3 = now + up + down + p.ClockOffsetMicros;
        double rtt = (t3 - t0) - (t2 - t1);
        var theta = ((t1 - t0) + (t2 - t3)) / 2.0;
        p.BestRtt = Math.Min(p.BestRtt * 1.02, rtt);
        p.RttMicros = rtt;
        if (rtt > p.BestRtt * 1.25) return;
        var est = -theta;
        p.EstimatedOffsetMicros = p.Synced ? p.EstimatedOffsetMicros + (est - p.EstimatedOffsetMicros) * 0.3 : est;
        p.Synced = true;
    }

    /// <summary>지연 분위수 갱신(무할당 · 스크래치 정렬).</summary>
    public void ComputeLatencyStats()
    {
        lock (_gate)
        {
            var n = _latCount;
            if (n == 0) return;
            var s = _latSort.AsSpan(0, n);
            _lat.AsSpan(0, n).CopyTo(s);
            s.Sort();
            Volatile.Write(ref _p50, s[n / 2]);
            Volatile.Write(ref _p95, s[Math.Min(n - 1, (int)(n * 0.95f))]);
            Volatile.Write(ref _max, s[n - 1]);
        }
    }

    /// <summary>결정적 시뮬레이션(테스트/프리뷰) — stepMicros 간격으로 durationMicros 진행.</summary>
    public void RunSimulated(long durationMicros, long stepMicros = 1000)
    {
        var end = _simNow + durationMicros;
        while (_simNow + stepMicros <= end) Tick(_simNow + stepMicros);
        ComputeLatencyStats();
        Raise();
    }

    /// <summary>수신 디바이스 좌표계로 변환한 최근 원격 메시(정점 xyz) 복사. 반환 = 정점 수.</summary>
    public int PresentToDevice(int receiver, int sender, Span<float> dst)
    {
        lock (_gate)
        {
            if (Peer(receiver) is not { } q || (uint)sender >= MaxPeers || q.Remote[sender] is not { } f || q.ReceivedFromPeer(sender) == 0) return 0;
            var n = Math.Min(f.VertexCount, dst.Length / 3);
            for (var i = 0; i < n; i++)
            {
                var v = q.Profile.ToDevice(new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]));
                dst[i * 3] = v.X; dst[i * 3 + 1] = v.Y; dst[i * 3 + 2] = v.Z;
            }
            return n;
        }
    }

    /// <summary>실 디바이스 업링크 수신 결선점 — 외부 전송으로 받은 패킷을 릴레이 팬아웃에 투입.</summary>
    public void IngestFromDevice(int fromPeer, ReadOnlySpan<byte> packet)
    {
        lock (_gate)
        {
            if (Peer(fromPeer) is not { Connected: true } || packet.Length > TeleportPacket.Mtu) return;
            Forward(packet, fromPeer, _simNow);
        }
    }

    /// <summary>기본 4종 디바이스 구성(Vision Pro · Quest 3 · WebXR · 홀로그램) — 링크 8~16ms · 지터 2ms · 손실 1~2% · 시계 오프셋 상이.</summary>
    public void ConnectDefaultMesh()
    {
        if (ConnectedCount > 0) return;
        Connect(XrDeviceKind.VisionPro, 8f, 8f, 1.5f, 0.01f, 3_250_000);
        Connect(XrDeviceKind.Quest3, 11f, 11f, 2f, 0.015f, -1_840_000);
        Connect(XrDeviceKind.WebXr, 16f, 16f, 3f, 0.02f, 742_000);
        Connect(XrDeviceKind.HologramDisplay, 9f, 9f, 1f, 0.01f, 12_000_000);
    }

    /// <summary>라이브 루프 시작 — PeriodicTimer 깨움마다 실시간까지 1ms 서브스텝(깨움당 최대 200).</summary>
    public void Start()
    {
        if (_cts is not null) return;
        ConnectDefaultMesh();
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 텔레포테이션 중계 시작";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏹ 중계 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        var origin = Stopwatch.GetTimestamp();
        var baseSim = _simNow;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var real = baseSim + (long)((Stopwatch.GetTimestamp() - origin) * 1_000_000.0 / Stopwatch.Frequency);
                var steps = 0;
                while (_simNow + 1000 <= real && steps++ < 200) Tick(_simNow + 1000);
                if (steps >= 200) { baseSim = _simNow; origin = Stopwatch.GetTimestamp(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { PromptFlowStudio.Program.Log("Teleport.Relay.Loop", ex); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Teleport.Relay.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
