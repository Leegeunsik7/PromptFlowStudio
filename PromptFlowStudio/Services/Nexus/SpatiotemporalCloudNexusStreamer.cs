using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Nexus;

/// <summary>단일 스트림 계층(값이 작을수록 우선 송출 · 엔드포인트 구독 상한).</summary>
public enum NexusLayer : byte { Control, Pose, Telemetry, Audio, Video, Splat }

public enum NexusEndpointKind : byte { CloudRegion, EdgePop, XrHeadset, Mobile }

/// <summary>송출 대상(링크 특성은 시뮬레이션 파라미터).</summary>
public sealed record NexusEndpoint(string Id, string Region, NexusEndpointKind Kind, float RttMs, float LossPct, float BandwidthMbps, bool Tampering = false);

/// <summary>엔드포인트별 송수신 통계(UI 스냅샷).</summary>
public readonly record struct NexusEndpointStats(string Id, NexusLayer MaxLayer, long FramesSent, long Delivered, long Lost, long Recovered, long Rejected,
    long PacketsLost, float LatencyMs, float P95Ms, float LossEwmaPct, float LoadMbps);

/// <summary>
/// [PromptFlow Studio v16.70.0] 실시간 시공간 클라우드 넥서스 스트리머.
/// 옴니모달 프레임(제어 · 포즈 · 텔레메트리 · 오디오 · 영상 · 3DGS)을 계층 우선순위로 정렬해 단일 패킷 스트림(헤더 32B · MTU 1200 · CRC32)으로
/// 분할하고, 계층 경계로 끊는 K=8 XOR 패리티 FEC(그룹당 1손실 복구) · 프레임별 HMAC-SHA256 봉인(seq · 계층 · 길이 집계 포함, FixedTimeEquals 검증) 을 적용.
/// 엔드포인트마다 손실/대역 기반 적응 계층 구독(ABR)과 지연 히스토그램(P95)을 유지. 링크는 결정적 손실 시뮬레이터이며 실제 소켓 전송은
/// <see cref="Transport"/> 결선점으로 위임한다. 패킷화 · 수신 재조립 경로는 사전 할당 버퍼만 사용(무할당).
/// </summary>
public sealed class SpatiotemporalCloudNexusStreamer
{
    public const int Header = 32, Mtu = 1200, MaxPayload = Mtu - Header, FecK = 8, TagBytes = 32;
    public const int MaxFrames = 32, MaxFrameBytes = 16 * 1024, MaxPackets = 512, HistBins = 128;
    private const ushort Magic = 0x584E;

    /// <summary>실 전송 결선점(UDP/QUIC 등). 등록 시 패킷이 그대로 전달된다.</summary>
    public interface INexusTransport { void Send(string endpointId, ReadOnlySpan<byte> packet); }

    private sealed class Rx
    {
        public NexusEndpoint Ep = null!;
        public NexusLayer MaxLayer = NexusLayer.Splat;
        public ulong Rng;
        public long FramesSent, Delivered, Lost, Recovered, Rejected, PacketsLost;
        public float LatencyEwma, LossEwma, LoadMbps;
        public readonly int[] Hist = new int[HistBins];
        public long HistCount;
        public readonly byte[] Frames = new byte[MaxFrames * MaxFrameBytes];
        public readonly ushort[] Got = new ushort[MaxFrames];
        public readonly bool[] Received = new bool[MaxPackets];
    }

    private readonly object _gate = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _stage = new byte[MaxFrames * MaxFrameBytes];
    private readonly int[] _len = new int[MaxFrames], _order = new int[MaxFrames];
    private readonly NexusLayer[] _layer = new NexusLayer[MaxFrames];
    private readonly byte[] _packets = new byte[MaxPackets * Mtu];
    private readonly short[] _pktFrame = new short[MaxPackets];
    private readonly int[] _pktEnd = new int[MaxPackets];
    private readonly byte[] _tag = new byte[TagBytes], _mac = new byte[12 + MaxFrameBytes];
    private readonly List<Rx> _rx = new();
    private int _staged, _stageBytes, _packetCount;
    private uint _frameSeq, _groupId;
    private long _flushes, _bytesOut, _parityPackets;
    private double _lastFlushMicros;

    public SpatiotemporalCloudNexusStreamer()
    {
        AddEndpoint(new NexusEndpoint("aws-ap-northeast-2", "서울", NexusEndpointKind.CloudRegion, 8, 0.2f, 800));
        AddEndpoint(new NexusEndpoint("gcp-us-west1", "오리건", NexusEndpointKind.CloudRegion, 132, 0.8f, 600));
        AddEndpoint(new NexusEndpoint("edge-fra-pop", "프랑크푸르트", NexusEndpointKind.EdgePop, 238, 1.5f, 300));
        AddEndpoint(new NexusEndpoint("edge-sin-pop", "싱가포르", NexusEndpointKind.EdgePop, 72, 2.5f, 250));
        AddEndpoint(new NexusEndpoint("xr-visionpro-01", "로컬 Wi-Fi 6E", NexusEndpointKind.XrHeadset, 4, 3f, 120));
        AddEndpoint(new NexusEndpoint("xr-quest3-02", "로컬 Wi-Fi 6", NexusEndpointKind.XrHeadset, 6, 4f, 60));
        AddEndpoint(new NexusEndpoint("mobile-5g-07", "5G SA", NexusEndpointKind.Mobile, 28, 6f, 12));
    }

    public event Action? OnChange;

    public INexusTransport? Transport { get; set; }
    public long Flushes => Interlocked.Read(ref _flushes);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long ParityPackets => Interlocked.Read(ref _parityPackets);
    public int LastPacketCount { get { lock (_gate) return _packetCount; } }
    public double LastFlushMicros { get { lock (_gate) return _lastFlushMicros; } }
    public uint FrameSeq { get { lock (_gate) return _frameSeq; } }
    public int EndpointCount { get { lock (_gate) return _rx.Count; } }
    public string? LastAction { get; private set; }

    public void AddEndpoint(NexusEndpoint ep)
    {
        lock (_gate)
        {
            foreach (var r in _rx) if (r.Ep.Id == ep.Id) return;   // alreadyRegistered
            _rx.Add(new Rx { Ep = ep, Rng = 0x2545F4914F6CDD1DUL ^ (ulong)(uint)StableHash(ep.Id) * 0x9E3779B97F4A7C15UL | 1 });
        }
    }

    public void ClearEndpoints() { lock (_gate) _rx.Clear(); }

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s) h = h * 31 + c;
        return h;
    }

    public int CopyStats(Span<NexusEndpointStats> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _rx.Count);
            for (var i = 0; i < n; i++)
            {
                var r = _rx[i];
                dst[i] = new NexusEndpointStats(r.Ep.Id, r.MaxLayer, r.FramesSent, r.Delivered, r.Lost, r.Recovered, r.Rejected, r.PacketsLost,
                    r.LatencyEwma, Percentile(r, 0.95f), r.LossEwma * 100, r.LoadMbps);
            }
            return n;
        }
    }

    public NexusEndpoint EndpointAt(int i) { lock (_gate) return _rx[i].Ep; }

    private static float Percentile(Rx r, float p)
    {
        if (r.HistCount == 0) return 0;
        var target = (long)MathF.Ceiling(r.HistCount * p);
        long acc = 0;
        for (var i = 0; i < HistBins; i++) { acc += r.Hist[i]; if (acc >= target) return i * 2 + 1; }
        return HistBins * 2;
    }

    /// <summary>프레임 적재(복사 · 무할당). 큐가 차거나 프레임이 크면 false.</summary>
    public bool Enqueue(NexusLayer layer, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_staged >= MaxFrames || payload.Length + TagBytes > MaxFrameBytes) return false;
            payload.CopyTo(_stage.AsSpan(_staged * MaxFrameBytes));
            _len[_staged] = payload.Length;
            _layer[_staged] = layer;
            _staged++;
            _stageBytes += payload.Length;
            return true;
        }
    }

    /// <summary>
    /// 적재 프레임 송출: 계층 정렬 → 봉인 → 패킷화(+FEC 패리티) → 엔드포인트별 링크 시뮬레이션 · 수신 재조립 · 검증 · 지연 계측. 반환: 패킷 수.
    /// </summary>
    public int Flush()
    {
        var t0 = Stopwatch.GetTimestamp();
        int packets;
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Nexus.Streamer.Flush");
            for (var i = 0; i < _staged; i++) _order[i] = i;
            for (var a = 1; a < _staged; a++)
                for (var b = a; b > 0 && _layer[_order[b]] < _layer[_order[b - 1]]; b--) (_order[b], _order[b - 1]) = (_order[b - 1], _order[b]);

            _packetCount = 0;
            var groupStart = 0;
            for (var oi = 0; oi < _staged; oi++)
            {
                var slot = _order[oi];
                var layer = _layer[slot];
                if (oi > 0 && layer != _layer[_order[oi - 1]] && _packetCount > groupStart) { EmitParity(groupStart); groupStart = _packetCount; }
                var seq = _frameSeq++;
                var frame = _stage.AsSpan(slot * MaxFrameBytes, MaxFrameBytes);
                var len = _len[slot];
                Seal(seq, layer, frame[..len], frame.Slice(len, TagBytes));
                var total = len + TagBytes;
                var frags = (total + MaxPayload - 1) / MaxPayload;
                for (var f = 0; f < frags; f++)
                {
                    if (_packetCount - groupStart == FecK) { EmitParity(groupStart); groupStart = _packetCount; }
                    if (_packetCount >= MaxPackets - 2) break;
                    var off = f * MaxPayload;
                    var pl = Math.Min(MaxPayload, total - off);
                    var p = _packets.AsSpan(_packetCount * Mtu, Mtu);
                    p.Clear();
                    WriteHeader(p, 0, layer, (byte)(_packetCount - groupStart), (ushort)pl, seq, (ushort)f, (ushort)frags, _groupId, (uint)total);
                    frame.Slice(off, pl).CopyTo(p[Header..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(p[28..], Crc32(p[..28], p.Slice(Header, pl)));
                    _pktFrame[_packetCount] = (short)oi;
                    _packetCount++;
                }
            }
            if (_packetCount > groupStart) EmitParity(groupStart);
            packets = _packetCount;

            for (var e = 0; e < _rx.Count; e++) Deliver(_rx[e]);
            _bytesOut += (long)packets * Mtu;
            _staged = 0; _stageBytes = 0;
            _lastFlushMicros = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
        Interlocked.Increment(ref _flushes);
        return packets;
    }

    private static void WriteHeader(Span<byte> p, byte flags, NexusLayer layer, byte groupPos, ushort payloadLen, uint seq, ushort frag, ushort frags, uint group, uint frameLen)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(p, Magic);
        p[2] = 1; p[3] = flags; p[4] = (byte)layer; p[5] = groupPos;
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], seq);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], frag);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], frags);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], group);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], frameLen);
        BinaryPrimitives.WriteUInt32LittleEndian(p[24..], (uint)Environment.TickCount);
    }

    /// <summary>XOR 패리티: 그룹 내 각 패킷의 [4, Mtu) (CRC 영역 0) 를 XOR. groupPos = 그룹 크기.</summary>
    private void EmitParity(int groupStart)
    {
        if (_packetCount >= MaxPackets) return;
        var k = _packetCount - groupStart;
        var par = _packets.AsSpan(_packetCount * Mtu, Mtu);
        par.Clear();
        for (var i = groupStart; i < _packetCount; i++)
        {
            var src = _packets.AsSpan(i * Mtu, Mtu);
            XorInto(src[4..28], par[4..28]);
            XorInto(src[Header..], par[Header..]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(par, Magic);
        par[2] = 1; par[3] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(par[16..], _groupId);
        par[5] = (byte)k;   // 패리티 헤더의 groupPos 는 XOR 값 대신 그룹 크기(수신측이 패리티 groupPos 는 복구에 쓰지 않음)
        BinaryPrimitives.WriteUInt32LittleEndian(par[28..], Crc32(par[..28], par[Header..]));
        _pktFrame[_packetCount] = -1;
        _packetCount++;
        _groupId++;
        _parityPackets++;
    }

    private static void XorInto(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && src.Length >= System.Numerics.Vector<byte>.Count)
        {
            var vs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, System.Numerics.Vector<byte>>(src);
            var vd = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, System.Numerics.Vector<byte>>(dst);
            for (var k = 0; k < vs.Length; k++) vd[k] ^= vs[k];
            i = vs.Length * System.Numerics.Vector<byte>.Count;
        }
        for (; i < src.Length; i++) dst[i] ^= src[i];
    }

    /// <summary>봉인 canonical = seq(4) · 계층(1) · 예약(3) · 길이(4) ‖ 페이로드 → HMAC-SHA256(one-shot · 무할당).</summary>
    private void Mac(uint seq, NexusLayer layer, ReadOnlySpan<byte> payload, Span<byte> tag)
    {
        var m = _mac.AsSpan(0, 12 + payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(m, seq);
        m[4] = (byte)layer; m[5] = 0; m[6] = 0; m[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(m[8..], payload.Length);
        payload.CopyTo(m[12..]);
        HMACSHA256.HashData(_key, m, tag);
    }

    private void Seal(uint seq, NexusLayer layer, ReadOnlySpan<byte> payload, Span<byte> tag) => Mac(seq, layer, payload, tag);

    private bool Verify(uint seq, NexusLayer layer, ReadOnlySpan<byte> sealedFrame)
    {
        if (sealedFrame.Length < TagBytes) return false;
        Mac(seq, layer, sealedFrame[..^TagBytes], _tag);
        return CryptographicOperations.FixedTimeEquals(_tag, sealedFrame[^TagBytes..]);
    }

    private void Deliver(Rx r)
    {
        var ep = r.Ep;
        Array.Clear(r.Received, 0, _packetCount);
        Array.Clear(r.Got);
        var lossP = ep.LossPct / 100f;
        var bytesPerMs = ep.BandwidthMbps * 1000f / 8f;
        var sentBytes = 0;
        var lostPk = 0; var sentPk = 0;
        for (var i = 0; i < _packetCount; i++)
        {
            var p = _packets.AsSpan(i * Mtu, Mtu);
            if (!Subscribed(r, i)) continue;
            sentPk++;
            sentBytes += Mtu;
            Transport?.Send(ep.Id, p);
            if (Next(ref r.Rng) < lossP) { lostPk++; continue; }
            r.Received[i] = true;
        }
        r.PacketsLost += lostPk;
        r.LossEwma += 0.1f * ((sentPk > 0 ? (float)lostPk / sentPk : 0) - r.LossEwma);
        r.LoadMbps += 0.2f * (sentBytes * 8f / 1e6f * 30f - r.LoadMbps);   // 30 Hz 송출 기준 환산

        // FEC: 그룹당 1손실 복구(복구본은 수신 버퍼에 재구성).
        var g0 = 0;
        for (var i = 0; i < _packetCount; i++)
        {
            if (_pktFrame[i] >= 0) continue;
            var missing = -1; var miss = 0;
            for (var j = g0; j < i; j++) if (!r.Received[j] && Subscribed(r, j)) { missing = j; miss++; }
            for (var j = g0; j < i; j++) if (r.Received[j]) Place(r, _packets.AsSpan(j * Mtu, Mtu), false);
            if (miss == 1 && r.Received[i]) RecoverInto(r, g0, i, missing);
            g0 = i + 1;
        }

        // 재조립 완료 프레임 검증 · 지연 계측(마지막 조각 시점 누적 직렬화 + RTT/2 + 지터).
        var cumBytes = 0;
        for (var i = 0; i < _packetCount; i++)
        {
            if (!Subscribed(r, i)) continue;
            cumBytes += Mtu;
            if (_pktFrame[i] < 0) continue;
            var p = _packets.AsSpan(i * Mtu, Mtu);
            var frags = BinaryPrimitives.ReadUInt16LittleEndian(p[14..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(p[12..]) != frags - 1) continue;
            r.FramesSent++;
            var oi = _pktFrame[i];
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
            var frameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[20..]);
            if (r.Got[oi] != frags) { r.Lost++; continue; }
            if (!Verify(seq, (NexusLayer)p[4], r.Frames.AsSpan(oi * MaxFrameBytes, frameLen))) { r.Rejected++; continue; }
            r.Delivered++;
            var ms = ep.RttMs * 0.5f + cumBytes / bytesPerMs + ep.RttMs * 0.05f * Next(ref r.Rng);
            r.LatencyEwma = r.Delivered == 1 ? ms : r.LatencyEwma + 0.05f * (ms - r.LatencyEwma);
            r.Hist[Math.Clamp((int)(ms / 2), 0, HistBins - 1)]++;
            r.HistCount++;
        }

        // ABR: 손실·대역 여유 기반 계층 상한 조정(제어/포즈/텔레메트리는 항상 유지).
        var budget = ep.BandwidthMbps * 0.85f;
        if ((r.LoadMbps > budget || r.LossEwma > 0.08f) && r.MaxLayer > NexusLayer.Telemetry) { r.MaxLayer--; r.LoadMbps *= 0.6f; }
        else if (r.LoadMbps < budget * 0.45f && r.LossEwma < 0.05f && r.MaxLayer < NexusLayer.Splat) r.MaxLayer++;
    }

    private bool Subscribed(Rx r, int i) => (_pktFrame[i] >= 0 ? (NexusLayer)_packets[i * Mtu + 4] : GroupLayer(i)) <= r.MaxLayer;

    private NexusLayer GroupLayer(int parityIndex)
    {
        var j = parityIndex - 1;
        return j >= 0 && _pktFrame[j] >= 0 ? (NexusLayer)_packets[j * Mtu + 4] : NexusLayer.Control;
    }

    private void Place(Rx r, ReadOnlySpan<byte> p, bool fromFec)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(p) != Magic) return;
        var pl = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
        if (pl > MaxPayload || BinaryPrimitives.ReadUInt32LittleEndian(p[28..]) != Crc32(p[..28], p.Slice(Header, pl))) return;
        var frag = BinaryPrimitives.ReadUInt16LittleEndian(p[12..]);
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        var oi = (int)(seq - (_frameSeq - (uint)_staged));
        if ((uint)oi >= MaxFrames) return;
        var dst = r.Frames.AsSpan(oi * MaxFrameBytes + frag * MaxPayload, pl);
        p.Slice(Header, pl).CopyTo(dst);
        if (r.Ep.Tampering && frag == 0 && pl > 0) dst[0] ^= 0x5A;   // MITM 변조 시뮬레이션(CRC 재계산 가정)
        r.Got[oi]++;
        if (fromFec) r.Recovered++;
    }

    private void RecoverInto(Rx r, int g0, int parity, int missing)
    {
        Span<byte> rec = stackalloc byte[Mtu];
        _packets.AsSpan(parity * Mtu, Mtu).CopyTo(rec);
        for (var j = g0; j < parity; j++)
        {
            if (j == missing) continue;
            var src = _packets.AsSpan(j * Mtu, Mtu);
            XorInto(src[4..28], rec[4..28]);
            XorInto(src[Header..], rec[Header..]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(rec, Magic);
        rec[2] = 1; rec[3] = 0;
        var pl = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
        if (pl > MaxPayload) return;
        BinaryPrimitives.WriteUInt32LittleEndian(rec[28..], Crc32(rec[..28], rec.Slice(Header, pl)));
        Place(r, rec, true);
    }

    private static float Next(ref ulong s)
    {
        s ^= s << 13; s ^= s >> 7; s ^= s << 17;
        return ((s * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    private static readonly uint[] CrcTable = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC-32(IEEE) — 헤더 [0,28) ‖ 페이로드.</summary>
    public static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public void Announce(string action)
    {
        LastAction = action;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Nexus.Streamer.OnChange", ex); }
    }
}
