using System.Buffers.Binary;

namespace PromptFlowStudio.Services.VirtualLive;

/// <summary>v17.31.0 렌더 프레임 화면비.</summary>
public enum LiveAspect : byte { Landscape16x9, Portrait9x16 }

/// <summary>v17.31.0 송출 전송 규격 — RTMP(FLV 태그) · WebRTC(RTP/FU-A).</summary>
public enum LiveTransport : byte { Rtmp, WebRtc }

/// <summary>v17.31.0 실제 소켓 결선점(RTMP TCP 청크 스트림 · WebRTC SRTP). 미주입이면 시뮬레이션 링크만 사용.</summary>
public interface ILiveTransportSink
{
    void Send(int outlet, LiveTransport transport, ReadOnlySpan<byte> packet);
}

/// <summary>v17.31.0 송출 목적지 스냅샷(무할당 복사).</summary>
public readonly record struct LiveOutletStats(
    string Name, string Platform, LiveTransport Transport, LiveAspect Aspect, int BitrateKbps, int Rung,
    float BandwidthKbps, float EwmaKbps, bool InSpike, long Delivered, long Dropped, long Stabilized, long Packets, long BytesSent,
    float Fps, float LatencyMs, float FreezeMs, float MaxFreezeMs);

/// <summary>
/// [PromptFlow Studio v17.31.0] 📡 실시간 멀티플랫폼 버추얼 라이브 송출 &amp; 적응형 비트레이트(ABR) 엔진(핫패스 무할당).
///   ▸ 입력: 9:16 · 16:9 렌더 프레임(H.264 ES)과 AAC 원시 프레임을 고정 슬롯 링버퍼(<see cref="RingSlots"/>)에 복사 — v17.21 시네마틱 카메라 · v17.23 공간 음향 결선점.
///   ▸ 패킷화: Annex-B NAL(키프레임 SPS/PPS/IDR · 비키 non-IDR) + 에뮬레이션 방지 바이트 → RTMP 는 FLV 비디오 태그(AVCC 길이 접두 · 최초 AVC 시퀀스 헤더) ·
///     WebRTC 는 RTP(PT 96 · 90 kHz · 단일 NAL / FU-A 분할 · 프레임 끝 마커). 오디오는 ADTS(로컬 아카이브) · FLV 오디오 태그 · RTP PT 97.
///   ▸ ABR: 목적지별 대역폭 EWMA 의 85 % 이하 최고 래더 단(800 → 8000 kbps) · 하향 0.5 s · 상향 2 s 안정 후 · 시뮬캐스트 레이어 근사(단 비율로 페이로드 절단).
///   ▸ 링크: 틱당 용량만큼 큐 배출 → 전달 프레임 · 지연 · 정지(freeze) 측정. 가드 ON 이면 스파이크/백로그 시 직전 키프레임 참조 P-Skip(수 바이트)로 안정 보간,
///     OFF 면 백로그 2 s 초과 프레임 폐기(화면 멈춤) — 비교 지표 노출.
/// </summary>
public sealed class RealtimeVirtualLiveBroadcastEngine
{
    public const int Fps = 60, Gop = 120, RingSlots = 8, AudioSlots = 16;
    public const int MaxFrameBytes = 96 * 1024, MaxAudioBytes = 2048, Mtu = 1200, MaxOutlets = 8, QueueCap = 256;
    public const float MaxBacklogMs = 250f, DropBacklogMs = 2000f, SafetyFactor = 0.85f;
    public static readonly int[] Ladder = { 800, 1500, 2500, 4500, 6000, 8000 };
    public static int TopKbps => Ladder[^1];

    // 고정 SPS(1080p High@4.2 근사) · PPS — 디코더 초기화용 상수
    private static readonly byte[] Sps = { 0x67, 0x64, 0x00, 0x2A, 0xAC, 0xD9, 0x40, 0x78, 0x02, 0x27, 0xE5, 0x84, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00, 0x03, 0x00, 0xF0, 0x3C, 0x60, 0xC6, 0x58 };
    private static readonly byte[] Pps = { 0x68, 0xEB, 0xE3, 0xCB, 0x22, 0xC0 };
    // P-Skip: non-IDR 슬라이스 헤더 + mb_skip_run 전체(직전 참조 프레임 그대로 표시)
    private static readonly byte[] SkipSlice = { 0x41, 0x9A, 0x02, 0x0B, 0xC0 };

    private readonly StreamSafetyAntiTrollingDivergenceGuard _guard;
    private readonly ILiveTransportSink? _sink;

    private readonly byte[][] _ring = new byte[RingSlots][];
    private readonly int[] _ringLen = new int[RingSlots];
    private readonly bool[] _ringKey = new bool[RingSlots];
    private readonly long[] _ringPts = new long[RingSlots];
    private readonly LiveAspect[] _ringAspect = new LiveAspect[RingSlots];
    private int _ringHead, _ringCount;

    private readonly byte[][] _aRing = new byte[AudioSlots][];
    private readonly int[] _aLen = new int[AudioSlots];
    private readonly long[] _aPts = new long[AudioSlots];
    private int _aHead, _aCount;

    private readonly byte[] _nal = new byte[MaxFrameBytes * 3 / 2 + 64];
    private readonly byte[] _pkt = new byte[MaxFrameBytes * 3 / 2 + 8192];
    private readonly int[] _pktLens = new int[512];
    private readonly Outlet[] _out = new Outlet[MaxOutlets];
    private int _outCount;

    public RealtimeVirtualLiveBroadcastEngine(StreamSafetyAntiTrollingDivergenceGuard guard, ILiveTransportSink? sink = null)
    {
        _guard = guard;
        _sink = sink;
        for (var i = 0; i < RingSlots; i++) _ring[i] = new byte[MaxFrameBytes];
        for (var i = 0; i < AudioSlots; i++) _aRing[i] = new byte[MaxAudioBytes];
    }

    private sealed class Outlet
    {
        public string Name = "", Platform = "";
        public LiveTransport Transport;
        public LiveAspect Aspect;
        public float BandwidthKbps, RttMs, EwmaKbps;
        public bool InSpike, SentConfig, Stabilizing;
        public int Rung, StableTicks, DownTicks;
        public readonly int[] QBytes = new int[QueueCap];
        public int QHead, QCount;
        public long BacklogBytes;
        public int PendingAudio;
        public long Delivered, Dropped, Stabilized, Packets, BytesSent;
        public long LastDeliveredMs = -1, WindowStartMs, WindowDelivered;
        public float Fps, FreezeMs, MaxFreezeMs;
        public ushort RtpSeq;
        public uint Ssrc;
    }

    public long FramesIn { get; private set; }
    public long SourceOverruns { get; private set; }
    public long AudioPackets { get; private set; }
    public int OutletCount => _outCount;
    public double LastPacketizeUs { get; private set; }

    public int AddOutlet(string name, string platform, LiveTransport transport, LiveAspect aspect, float bandwidthKbps, float rttMs = 40f)
    {
        if (_outCount == MaxOutlets) return -1;
        var o = _out[_outCount] = new Outlet
        {
            Name = name, Platform = platform, Transport = transport, Aspect = aspect, BandwidthKbps = bandwidthKbps, RttMs = rttMs,
            Rung = 2, Ssrc = 0x50460000u + (uint)_outCount,
        };
        o.EwmaKbps = bandwidthKbps;
        return _outCount++;
    }

    /// <summary>링크 용량 변경(관측 피드백 · 장애 주입).</summary>
    public void SetBandwidth(int outlet, float kbps) => _out[outlet].BandwidthKbps = MathF.Max(50f, kbps);

    public float BandwidthOf(int outlet) => _out[outlet].BandwidthKbps;
    public int BitrateOf(int outlet) => Ladder[_out[outlet].Rung];

    public int CopyStats(Span<LiveOutletStats> dst)
    {
        var n = Math.Min(dst.Length, _outCount);
        for (var i = 0; i < n; i++)
        {
            var o = _out[i];
            var lat = o.BacklogBytes * 8f / MathF.Max(1f, o.BandwidthKbps) + o.RttMs * 0.5f;
            dst[i] = new LiveOutletStats(o.Name, o.Platform, o.Transport, o.Aspect, Ladder[o.Rung], o.Rung, o.BandwidthKbps, o.EwmaKbps, o.InSpike,
                o.Delivered, o.Dropped, o.Stabilized, o.Packets, o.BytesSent, o.Fps, lat, o.FreezeMs, o.MaxFreezeMs);
        }
        return n;
    }

    // ── 입력 링버퍼 ──

    /// <summary>렌더 프레임 ES 복사(무할당). 링 가득 차면 가장 오래된 프레임을 덮어씀(오버런 집계).</summary>
    public void SubmitVideo(ReadOnlySpan<byte> es, bool keyframe, long ptsMs, LiveAspect aspect)
    {
        if (_ringCount == RingSlots) { _ringHead = (_ringHead + 1) % RingSlots; _ringCount--; SourceOverruns++; }
        var slot = (_ringHead + _ringCount) % RingSlots;
        var n = Math.Min(es.Length, MaxFrameBytes);
        es[..n].CopyTo(_ring[slot]);
        _ringLen[slot] = n; _ringKey[slot] = keyframe; _ringPts[slot] = ptsMs; _ringAspect[slot] = aspect;
        _ringCount++;
        FramesIn++;
    }

    public void SubmitAudio(ReadOnlySpan<byte> aac, long ptsMs)
    {
        if (_aCount == AudioSlots) { _aHead = (_aHead + 1) % AudioSlots; _aCount--; }
        var slot = (_aHead + _aCount) % AudioSlots;
        var n = Math.Min(aac.Length, MaxAudioBytes);
        aac[..n].CopyTo(_aRing[slot]);
        _aLen[slot] = n; _aPts[slot] = ptsMs;
        _aCount++;
    }

    // ── 1 틱(1/60 s): ABR → 오디오 → 비디오 패킷화 → 링크 배출 ──

    public void Tick(long nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < _outCount; i++) Adapt(_out[i]);

        while (_aCount > 0)
        {
            var s = _aHead;
            _aHead = (_aHead + 1) % AudioSlots; _aCount--;
            for (var i = 0; i < _outCount; i++) SendAudio(i, _aRing[s].AsSpan(0, _aLen[s]), _aPts[s]);
            AudioPackets++;
        }

        while (_ringCount > 0)
        {
            var s = _ringHead;
            _ringHead = (_ringHead + 1) % RingSlots; _ringCount--;
            for (var i = 0; i < _outCount; i++)
                if (_out[i].Aspect == _ringAspect[s]) SendVideo(i, _ring[s].AsSpan(0, _ringLen[s]), _ringKey[s], _ringPts[s]);
        }
        LastPacketizeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;

        for (var i = 0; i < _outCount; i++) Drain(_out[i], nowMs);
    }

    private void Adapt(Outlet o)
    {
        var spike = _guard.ObserveBandwidth(ref o.EwmaKbps, ref o.InSpike, o.BandwidthKbps);
        if (spike)
        {
            // 가드: 순간 대역폭에 맞는 단으로 즉시 하향
            var fit = FitRung(o.BandwidthKbps * SafetyFactor);
            if (fit < o.Rung) o.Rung = fit;
            o.StableTicks = 0; o.DownTicks = 0;
            return;
        }
        var target = o.EwmaKbps * SafetyFactor;
        if (Ladder[o.Rung] > target && o.Rung > 0)
        {
            if (++o.DownTicks >= Fps / 2) { o.Rung--; o.DownTicks = 0; }
            o.StableTicks = 0;
        }
        else if (o.Rung < Ladder.Length - 1 && Ladder[o.Rung + 1] <= target && BacklogMs(o) < 100f)
        {
            o.DownTicks = 0;
            if (++o.StableTicks >= Fps * 2) { o.Rung++; o.StableTicks = 0; }
        }
        else { o.StableTicks = 0; o.DownTicks = 0; }
    }

    private static int FitRung(float kbps)
    {
        var r = 0;
        for (var i = 0; i < Ladder.Length; i++) if (Ladder[i] <= kbps) r = i;
        return r;
    }

    private float BacklogMs(Outlet o) => o.BacklogBytes * 8f / MathF.Max(1f, o.BandwidthKbps);

    private void SendVideo(int idx, ReadOnlySpan<byte> es, bool key, long ptsMs)
    {
        var o = _out[idx];
        var backlog = BacklogMs(o);
        int nalLen;
        var stabilize = _guard.Enabled && (o.InSpike || backlog > MaxBacklogMs) && !(key && backlog < MaxBacklogMs * 0.5f);
        if (stabilize)
        {
            // 대기 중(미전송) 프레임은 P-Skip 으로 대체 — 이미 송신 시작한 머리 프레임만 유지(지연 즉시 회수)
            if (backlog > MaxBacklogMs && o.QCount > 1) TrimQueue(o);
            // 직전 키프레임 참조 P-Skip — 디코더는 마지막 화면을 유지하며 카덴스(60 fps)는 끊기지 않음
            SkipSlice.AsSpan().CopyTo(_nal);
            nalLen = SkipSlice.Length;
            o.Stabilized++;
            _guard.CountStabilized();
            o.Stabilizing = true;
        }
        else
        {
            if (!_guard.Enabled && backlog > DropBacklogMs) { o.Dropped++; return; }
            if (o.Stabilizing && !key) { o.Stabilized++; _guard.CountStabilized(); nalLen = SkipSlice.Length; SkipSlice.AsSpan().CopyTo(_nal); }   // 회복 후 다음 키프레임까지 참조 유지
            else
            {
                o.Stabilizing = false;
                // 시뮬캐스트 레이어 근사: 단 비율만큼 페이로드 사용
                var layer = Math.Max(16, (int)((long)es.Length * Ladder[o.Rung] / TopKbps));
                if (layer > es.Length) layer = es.Length;
                _nal[0] = key ? (byte)0x65 : (byte)0x41;
                nalLen = 1 + WriteEscaped(es[..layer], _nal.AsSpan(1));
            }
        }

        var nal = _nal.AsSpan(0, nalLen);
        var bytes = 0;
        if (o.Transport == LiveTransport.Rtmp)
        {
            var ts = (uint)ptsMs;
            if (!o.SentConfig && key)
            {
                var c = WriteFlvAvcConfig(_pkt, ts);
                Emit(idx, o, _pkt.AsSpan(0, c)); bytes += c;
                o.SentConfig = true;
            }
            var n = WriteFlvVideoTag(_pkt, nal, key && !o.Stabilizing, ts);
            Emit(idx, o, _pkt.AsSpan(0, n)); bytes += n;
        }
        else
        {
            var ts90 = (uint)(ptsMs * 90);
            if (key && !o.Stabilizing)
            {
                bytes += SendRtp(idx, o, Sps, ts90, marker: false);
                bytes += SendRtp(idx, o, Pps, ts90, marker: false);
            }
            bytes += SendRtp(idx, o, nal, ts90, marker: true);
        }
        Enqueue(o, bytes + o.PendingAudio);
        o.PendingAudio = 0;
    }

    private int SendRtp(int idx, Outlet o, ReadOnlySpan<byte> nal, uint ts90, bool marker)
    {
        var count = PacketizeRtp(nal, _pkt, _pktLens, ref o.RtpSeq, ts90, o.Ssrc, Mtu, marker);
        var off = 0;
        for (var k = 0; k < count; k++)
        {
            Emit(idx, o, _pkt.AsSpan(off, _pktLens[k]));
            off += _pktLens[k];
        }
        return off;
    }

    private void SendAudio(int idx, ReadOnlySpan<byte> aac, long ptsMs)
    {
        var o = _out[idx];
        int n;
        if (o.Transport == LiveTransport.Rtmp)
        {
            n = WriteFlvAudioTag(_pkt, aac, (uint)ptsMs);
            Emit(idx, o, _pkt.AsSpan(0, n));
        }
        else
        {
            WriteRtpHeader(_pkt, 97, true, o.RtpSeq++, (uint)(ptsMs * 48), o.Ssrc + 0x100u);
            aac.CopyTo(_pkt.AsSpan(12));
            n = 12 + aac.Length;
            Emit(idx, o, _pkt.AsSpan(0, n));
        }
        o.PendingAudio += n;
    }

    private void Emit(int idx, Outlet o, ReadOnlySpan<byte> packet)
    {
        o.Packets++;
        o.BytesSent += packet.Length;
        _sink?.Send(idx, o.Transport, packet);
    }

    private static void Enqueue(Outlet o, int bytes)
    {
        if (o.QCount == QueueCap) { o.BacklogBytes -= o.QBytes[o.QHead]; o.QHead = (o.QHead + 1) % QueueCap; o.QCount--; o.Dropped++; }
        o.QBytes[(o.QHead + o.QCount) % QueueCap] = bytes;
        o.QCount++;
        o.BacklogBytes += bytes;
    }

    private void TrimQueue(Outlet o)
    {
        for (var k = 1; k < o.QCount; k++)
        {
            ref var q = ref o.QBytes[(o.QHead + k) % QueueCap];
            if (q <= SkipSlice.Length + 32) continue;
            o.BacklogBytes -= q - (SkipSlice.Length + 32);
            q = SkipSlice.Length + 32;
            o.Stabilized++;
            _guard.CountStabilized();
        }
    }

    private static void Drain(Outlet o, long nowMs)
    {
        var budget = (long)(o.BandwidthKbps * 1000f / 8f / Fps);
        while (o.QCount > 0 && budget > 0)
        {
            ref var head = ref o.QBytes[o.QHead];
            var take = Math.Min(budget, head);
            head -= (int)take; budget -= take; o.BacklogBytes -= take;
            if (head > 0) break;
            o.QHead = (o.QHead + 1) % QueueCap; o.QCount--;
            o.Delivered++; o.WindowDelivered++;
            o.LastDeliveredMs = nowMs;
        }
        if (o.LastDeliveredMs >= 0)
        {
            o.FreezeMs = nowMs - o.LastDeliveredMs;
            if (o.FreezeMs > o.MaxFreezeMs) o.MaxFreezeMs = o.FreezeMs;
        }
        if (nowMs - o.WindowStartMs >= 1000)
        {
            o.Fps = o.WindowDelivered * 1000f / MathF.Max(1, nowMs - o.WindowStartMs);
            o.WindowDelivered = 0; o.WindowStartMs = nowMs;
        }
    }

    public void ResetMetrics()
    {
        for (var i = 0; i < _outCount; i++)
        {
            var o = _out[i];
            o.Delivered = 0; o.Dropped = 0; o.Stabilized = 0; o.Packets = 0; o.BytesSent = 0; o.MaxFreezeMs = 0;
        }
        SourceOverruns = 0;
    }

    // ── 패킷화 프리미티브(정적 · 무할당 · 테스트 잠금) ──

    /// <summary>RBSP → NAL 페이로드: 00 00 뒤 0x00~0x03 앞에 0x03 삽입.</summary>
    public static int WriteEscaped(ReadOnlySpan<byte> rbsp, Span<byte> dst)
    {
        var n = 0; var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3) { dst[n++] = 3; zeros = 0; }
            dst[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return n;
    }

    public static int Unescape(ReadOnlySpan<byte> nal, Span<byte> dst)
    {
        var n = 0; var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            dst[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return n;
    }

    /// <summary>Annex-B 단일 NAL(4바이트 시작 코드).</summary>
    public static int WriteAnnexB(ReadOnlySpan<byte> nal, Span<byte> dst)
    {
        dst[0] = 0; dst[1] = 0; dst[2] = 0; dst[3] = 1;
        nal.CopyTo(dst[4..]);
        return 4 + nal.Length;
    }

    private static void WriteFlvHeader(Span<byte> d, byte type, int dataSize, uint ts)
    {
        d[0] = type;
        d[1] = (byte)(dataSize >> 16); d[2] = (byte)(dataSize >> 8); d[3] = (byte)dataSize;
        d[4] = (byte)(ts >> 16); d[5] = (byte)(ts >> 8); d[6] = (byte)ts; d[7] = (byte)(ts >> 24);
        d[8] = 0; d[9] = 0; d[10] = 0;
    }

    /// <summary>FLV 비디오 태그(AVC NALU · AVCC 4바이트 길이 접두) + PreviousTagSize.</summary>
    public static int WriteFlvVideoTag(Span<byte> dst, ReadOnlySpan<byte> nal, bool key, uint tsMs)
    {
        var dataSize = 5 + 4 + nal.Length;
        WriteFlvHeader(dst, 9, dataSize, tsMs);
        dst[11] = key ? (byte)0x17 : (byte)0x27;
        dst[12] = 1; dst[13] = 0; dst[14] = 0; dst[15] = 0;
        BinaryPrimitives.WriteInt32BigEndian(dst[16..], nal.Length);
        nal.CopyTo(dst[20..]);
        var end = 11 + dataSize;
        BinaryPrimitives.WriteInt32BigEndian(dst[end..], end);
        return end + 4;
    }

    /// <summary>FLV AVC 시퀀스 헤더(AVCDecoderConfigurationRecord · SPS/PPS 1개씩).</summary>
    public static int WriteFlvAvcConfig(Span<byte> dst, uint tsMs)
    {
        var dataSize = 5 + 6 + 2 + Sps.Length + 1 + 2 + Pps.Length;
        WriteFlvHeader(dst, 9, dataSize, tsMs);
        var p = 11;
        dst[p++] = 0x17; dst[p++] = 0; dst[p++] = 0; dst[p++] = 0; dst[p++] = 0;
        dst[p++] = 1; dst[p++] = Sps[1]; dst[p++] = Sps[2]; dst[p++] = Sps[3]; dst[p++] = 0xFF; dst[p++] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(dst[p..], (ushort)Sps.Length); p += 2;
        Sps.CopyTo(dst[p..]); p += Sps.Length;
        dst[p++] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(dst[p..], (ushort)Pps.Length); p += 2;
        Pps.CopyTo(dst[p..]); p += Pps.Length;
        BinaryPrimitives.WriteInt32BigEndian(dst[p..], p);
        return p + 4;
    }

    /// <summary>FLV 오디오 태그(AAC 원시 · 44.1 kHz 스테레오 16bit 표기).</summary>
    public static int WriteFlvAudioTag(Span<byte> dst, ReadOnlySpan<byte> aac, uint tsMs)
    {
        var dataSize = 2 + aac.Length;
        WriteFlvHeader(dst, 8, dataSize, tsMs);
        dst[11] = 0xAF; dst[12] = 1;
        aac.CopyTo(dst[13..]);
        var end = 11 + dataSize;
        BinaryPrimitives.WriteInt32BigEndian(dst[end..], end);
        return end + 4;
    }

    /// <summary>ADTS 헤더 7바이트(AAC-LC · 48 kHz · 2ch).</summary>
    public static void WriteAdts(Span<byte> dst, int aacLen)
    {
        var len = aacLen + 7;
        dst[0] = 0xFF; dst[1] = 0xF1;
        dst[2] = (1 << 6) | (3 << 2);   // profile LC(1) · 48 kHz(idx 3) · ch 상위 비트 0
        dst[3] = (byte)((2 << 6) | (len >> 11));
        dst[4] = (byte)(len >> 3);
        dst[5] = (byte)(((len & 7) << 5) | 0x1F);
        dst[6] = 0xFC;
    }

    public static void WriteRtpHeader(Span<byte> d, byte pt, bool marker, ushort seq, uint ts, uint ssrc)
    {
        d[0] = 0x80;
        d[1] = (byte)(pt | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], ts);
        BinaryPrimitives.WriteUInt32BigEndian(d[8..], ssrc);
    }

    /// <summary>RFC 6184 RTP 패킷화 — MTU 이하 단일 NAL, 초과 시 FU-A(S/E 비트). 패킷은 dst 에 연속 기록 · 길이는 lens. 반환 = 패킷 수.</summary>
    public static int PacketizeRtp(ReadOnlySpan<byte> nal, Span<byte> dst, Span<int> lens, ref ushort seq, uint ts90, uint ssrc, int mtu, bool marker)
    {
        var maxPayload = mtu - 12;
        if (nal.Length <= maxPayload)
        {
            WriteRtpHeader(dst, 96, marker, seq++, ts90, ssrc);
            nal.CopyTo(dst[12..]);
            lens[0] = 12 + nal.Length;
            return 1;
        }
        var hdr = nal[0];
        var body = nal[1..];
        var chunk = maxPayload - 2;
        var off = 0; var count = 0; var w = 0;
        while (off < body.Length)
        {
            var n = Math.Min(chunk, body.Length - off);
            var last = off + n == body.Length;
            WriteRtpHeader(dst[w..], 96, marker && last, seq++, ts90, ssrc);
            dst[w + 12] = (byte)((hdr & 0x60) | 28);
            dst[w + 13] = (byte)((off == 0 ? 0x80 : 0) | (last ? 0x40 : 0) | (hdr & 0x1F));
            body.Slice(off, n).CopyTo(dst[(w + 14)..]);
            lens[count++] = 14 + n;
            w += 14 + n;
            off += n;
        }
        return count;
    }
}
