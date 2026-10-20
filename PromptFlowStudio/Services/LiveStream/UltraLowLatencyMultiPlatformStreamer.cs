using System.Buffers.Binary;
using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.LiveStream;

public enum LiveProtocol : byte { Rtmp, Srt, WebRtc }

/// <summary>송출 대상. 스트림 키는 환경변수 이름만 보관(값은 메모리 내 존재 여부만 확인 · 로그 금지).</summary>
public sealed record LiveDestination(string Id, string Platform, LiveProtocol Protocol, string KeyEnv, float RttMs, float LossPct, float BandwidthMbps, float JitterMs = 2);

/// <summary>대상별 송출 통계(인제스트 지연 = 캡처 시각 → 플랫폼 인제스트 서버 프레임 완성).</summary>
public readonly record struct LiveDestinationStats(string Id, string Platform, LiveProtocol Protocol, int BitrateKbps, long Frames, long Delivered, long Dropped,
    long Retransmits, long Packets, float P50Ms, float P95Ms, float LastMs, bool KeyConfigured, bool Healthy, long Bytes);

/// <summary>
/// [PromptFlow Studio v16.71.0] 초저지연 다중 플랫폼 스트리밍 프로토콜 엔진.
/// 인코딩 프레임을 대상별 프로토콜로 실제 와이어 포맷 패킷화 — RTMP(청크 스트림 fmt0/fmt3 · FLV 비디오 태그 헤더 · 청크 4096) ·
/// SRT(16B 데이터 헤더 · PP 위치 플래그 · 메시지 번호 · μs 타임스탬프 · 1316B 페이로드) · WebRTC(RTP v2 · 90kHz · 마커 비트 · 1200B) — 후
/// 대상 링크 모델로 전달을 계산: RTMP=TCP 재전송(+RTT) · 프레임 간 HOL 블로킹, SRT=NAK ARQ + TSBPD 지연 창(창 초과 시 too-late drop),
/// WebRTC=NACK 1회 재전송 · 마감(150ms) 초과 시 드롭 + PLI. 대상별 P50/P95 인제스트 지연으로 ABR 래더(8000→1500kbps) 조정.
/// 링크는 결정적 시뮬레이터이며 실 소켓 송출은 <see cref="Transport"/> 결선점(키 설정된 대상만)으로 위임. 시청자 측 플랫폼 배포 지연(HLS 등)은 포함하지 않는다.
/// 패킷화 · 전달 경로는 사전 할당 버퍼만 사용(무할당).
/// </summary>
public sealed class UltraLowLatencyMultiPlatformStreamer
{
    public const int RtmpChunk = 4096, SrtPayload = 1316, RtpPayload = 1200, MaxFrameBytes = 160 * 1024, MaxPackets = 160, HistBins = 500, Fps = 60;
    public const float WebRtcDeadlineMs = 150, TargetMs = 100;
    public static readonly int[] Ladder = { 8000, 6000, 4500, 3000, 1500 };

    public interface ILiveTransport { void Send(string destinationId, LiveProtocol protocol, ReadOnlySpan<byte> packet); }

    private sealed class Dest
    {
        public LiveDestination D = null!;
        public bool Key;
        public int Rung = 1;
        public ulong Rng;
        public uint Seq, MsgNo, Ssrc;
        public float TcpReadyMs, TxFreeMs, LastMs;
        public long Frames, Delivered, Dropped, Retx, Packets, Bytes, Stable, PliPending;
        public readonly int[] Hist = new int[HistBins];
        public readonly float[] Recent = new float[120];
        public int RecentHead, RecentCount;
    }

    private readonly object _gate = new();
    private readonly List<Dest> _dest = new();
    private readonly byte[] _frame = new byte[MaxFrameBytes];
    private readonly byte[] _wire = new byte[MaxFrameBytes + MaxPackets * 32];
    private readonly int[] _pktOff = new int[MaxPackets + 1];
    private readonly float[] _sorted = new float[120];
    private long _pushes;
    private double _lastPushMicros;

    public UltraLowLatencyMultiPlatformStreamer()
    {
        Add(new LiveDestination("youtube", "YouTube Live", LiveProtocol.Rtmp, "PFS_YOUTUBE_STREAM_KEY", 34, 0.05f, 60));
        Add(new LiveDestination("twitch", "Twitch", LiveProtocol.Rtmp, "PFS_TWITCH_STREAM_KEY", 46, 0.3f, 40));
        Add(new LiveDestination("chzzk", "치지직", LiveProtocol.Rtmp, "PFS_CHZZK_STREAM_KEY", 6, 0.1f, 80));
        Add(new LiveDestination("srt-relay", "SRT 클라우드 릴레이", LiveProtocol.Srt, "PFS_SRT_PASSPHRASE", 24, 1.5f, 50));
        Add(new LiveDestination("metaverse", "메타버스 WebRTC", LiveProtocol.WebRtc, "PFS_METAVERSE_TOKEN", 18, 1f, 30, 4));
    }

    public event Action? OnChange;

    public ILiveTransport? Transport { get; set; }
    public long Pushes => Interlocked.Read(ref _pushes);
    public double LastPushMicros { get { lock (_gate) return _lastPushMicros; } }
    public int Count { get { lock (_gate) return _dest.Count; } }
    public string? LastAction { get; private set; }

    public void Add(LiveDestination d)
    {
        lock (_gate)
        {
            foreach (var x in _dest) if (x.D.Id == d.Id) return;   // alreadyRegistered
            var h = 1469598103934665603UL; foreach (var c in d.Id) { h ^= c; h *= 1099511628211UL; }
            _dest.Add(new Dest { D = d, Key = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(d.KeyEnv)), Rng = h | 1, Ssrc = (uint)(h >> 32) });
        }
    }

    public void Clear() { lock (_gate) _dest.Clear(); }

    public int CopyStats(Span<LiveDestinationStats> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _dest.Count);
            for (var i = 0; i < n; i++)
            {
                var d = _dest[i];
                var p95 = Pct(d, 0.95f);
                dst[i] = new LiveDestinationStats(d.D.Id, d.D.Platform, d.D.Protocol, Ladder[d.Rung], d.Frames, d.Delivered, d.Dropped, d.Retx, d.Packets,
                    Pct(d, 0.5f), p95, d.LastMs, d.Key, p95 <= TargetMs && d.Dropped * 50 <= Math.Max(1, d.Frames), d.Bytes);
            }
            return n;
        }
    }

    private static float Pct(Dest d, float p)
    {
        long total = 0; foreach (var c in d.Hist) total += c;
        if (total == 0) return 0;
        var target = (long)MathF.Ceiling(total * p); long acc = 0;
        for (var i = 0; i < HistBins; i++) { acc += d.Hist[i]; if (acc >= target) return i + 0.5f; }
        return HistBins;
    }

    // ══════════ 와이어 포맷 ══════════

    /// <summary>RTMP 청크 스트림 직렬화(fmt0 + fmt3 연속 청크). 반환: 기록 바이트.</summary>
    public static int WriteRtmp(Span<byte> dst, int csid, uint timestampMs, byte msgType, uint streamId, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body, int chunkSize = RtmpChunk)
    {
        var len = header.Length + body.Length;
        var o = 0;
        dst[o++] = (byte)(csid & 0x3F);
        var ext = timestampMs >= 0xFFFFFF;
        WriteU24(dst[o..], ext ? 0xFFFFFFu : timestampMs); o += 3;
        WriteU24(dst[o..], (uint)len); o += 3;
        dst[o++] = msgType;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[o..], streamId); o += 4;
        if (ext) { BinaryPrimitives.WriteUInt32BigEndian(dst[o..], timestampMs); o += 4; }
        var written = 0;
        while (written < len)
        {
            if (written > 0)
            {
                dst[o++] = (byte)(0xC0 | (csid & 0x3F));
                if (ext) { BinaryPrimitives.WriteUInt32BigEndian(dst[o..], timestampMs); o += 4; }
            }
            var n = Math.Min(chunkSize, len - written);
            for (var i = 0; i < n; i++)
            {
                var k = written + i;
                dst[o + i] = k < header.Length ? header[k] : body[k - header.Length];
            }
            o += n; written += n;
        }
        return o;
    }

    /// <summary>RTMP 청크 역직렬화(단일 메시지 · 검증용). 반환: 페이로드 길이(실패 −1).</summary>
    public static int ReadRtmp(ReadOnlySpan<byte> src, Span<byte> payload, out uint timestampMs, out byte msgType, out uint streamId, int chunkSize = RtmpChunk)
    {
        timestampMs = 0; msgType = 0; streamId = 0;
        if (src.Length < 12 || (src[0] >> 6) != 0) return -1;
        var csid = src[0] & 0x3F;
        var o = 1;
        timestampMs = ReadU24(src[o..]); o += 3;
        var len = (int)ReadU24(src[o..]); o += 3;
        msgType = src[o++];
        streamId = BinaryPrimitives.ReadUInt32LittleEndian(src[o..]); o += 4;
        var ext = timestampMs == 0xFFFFFF;
        if (ext) { timestampMs = BinaryPrimitives.ReadUInt32BigEndian(src[o..]); o += 4; }
        var got = 0;
        while (got < len)
        {
            if (got > 0)
            {
                if (src[o] != (0xC0 | csid)) return -1;
                o++;
                if (ext) o += 4;
            }
            var n = Math.Min(chunkSize, len - got);
            src.Slice(o, n).CopyTo(payload[got..]);
            o += n; got += n;
        }
        return len;
    }

    /// <summary>SRT 데이터 패킷 헤더(16B). position: 0b11 단독 · 0b10 첫 · 0b00 중간 · 0b01 끝.</summary>
    public static void WriteSrtHeader(Span<byte> dst, uint seq, int position, uint msgNo, uint timestampUs, uint socketId)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst, seq & 0x7FFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], ((uint)position << 30) | (1u << 29) | (msgNo & 0x03FFFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], timestampUs);
        BinaryPrimitives.WriteUInt32BigEndian(dst[12..], socketId);
    }

    /// <summary>RTP 고정 헤더(12B · V=2 · PT 96).</summary>
    public static void WriteRtpHeader(Span<byte> dst, bool marker, ushort seq, uint ts90k, uint ssrc)
    {
        dst[0] = 0x80;
        dst[1] = (byte)((marker ? 0x80 : 0) | 96);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], ts90k);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], ssrc);
    }

    private static void WriteU24(Span<byte> d, uint v) { d[0] = (byte)(v >> 16); d[1] = (byte)(v >> 8); d[2] = (byte)v; }
    private static uint ReadU24(ReadOnlySpan<byte> s) => (uint)(s[0] << 16 | s[1] << 8 | s[2]);

    // ══════════ 송출 ══════════

    /// <summary>대상 비트레이트에 맞춘 프레임 바이트(키프레임 ×4).</summary>
    public static int FrameBytesFor(int kbps, bool key) => Math.Min(MaxFrameBytes, kbps * 1000 / 8 / Fps * (key ? 4 : 1));

    /// <summary>
    /// 1프레임 멀티캐스트(무할당): <paramref name="seed"/> 를 대상 렌디션 크기로 채움 → 프로토콜 패킷화 → 링크 모델 → 지연 · 드롭 · ABR.
    /// <paramref name="captureMs"/> 는 캡처 시각, <paramref name="pipelineMs"/> 는 캡처→인코더 출력까지의 내부 지연(스위처 정렬 + 인코드).
    /// </summary>
    public void Push(ReadOnlySpan<byte> seed, bool keyframe, long captureMs, float pipelineMs)
    {
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Live.Streamer.Push");
            foreach (var d in _dest)
            {
                var size = FrameBytesFor(Ladder[d.Rung], keyframe);
                for (var o = 0; o < size; o += seed.Length) seed[..Math.Min(seed.Length, size - o)].CopyTo(_frame.AsSpan(o));
                var body = _frame.AsSpan(0, size);
                var count = Packetize(d, body, keyframe, (uint)captureMs);
                d.Frames++;
                d.Packets += count;
                d.Bytes += _pktOff[count];
                if (d.Key && Transport is { } tr)
                    for (var p = 0; p < count; p++) tr.Send(d.D.Id, d.D.Protocol, _wire.AsSpan(_pktOff[p], _pktOff[p + 1] - _pktOff[p]));
                Deliver(d, count, keyframe, captureMs, pipelineMs);
            }
            _lastPushMicros = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
        Interlocked.Increment(ref _pushes);
    }

    private int Packetize(Dest d, ReadOnlySpan<byte> body, bool key, uint tsMs)
    {
        var wire = _wire.AsSpan();
        var n = 0; var o = 0;
        switch (d.D.Protocol)
        {
            case LiveProtocol.Rtmp:
            {
                Span<byte> flv = stackalloc byte[5];
                flv[0] = (byte)(key ? 0x17 : 0x27); flv[1] = 1; flv[2] = 0; flv[3] = 0; flv[4] = 0;
                var len = WriteRtmp(wire, 6, tsMs, 9, 1, flv, body);
                // TCP 세그먼트(≈MSS 1448) 단위로 손실 모델링.
                for (var p = 0; p < len && n < MaxPackets; p += 1448) { _pktOff[n++] = p; }
                o = len;
                break;
            }
            case LiveProtocol.Srt:
            {
                var frags = (body.Length + SrtPayload - 1) / SrtPayload;
                d.MsgNo++;
                for (var f = 0; f < frags && n < MaxPackets; f++)
                {
                    var pl = Math.Min(SrtPayload, body.Length - f * SrtPayload);
                    var pos = frags == 1 ? 3 : f == 0 ? 2 : f == frags - 1 ? 1 : 0;
                    _pktOff[n++] = o;
                    WriteSrtHeader(wire[o..], d.Seq++, pos, d.MsgNo, tsMs * 1000, 0x5F5F0001);
                    body.Slice(f * SrtPayload, pl).CopyTo(wire[(o + 16)..]);
                    o += 16 + pl;
                }
                break;
            }
            default:
            {
                var frags = (body.Length + RtpPayload - 1) / RtpPayload;
                for (var f = 0; f < frags && n < MaxPackets; f++)
                {
                    var pl = Math.Min(RtpPayload, body.Length - f * RtpPayload);
                    _pktOff[n++] = o;
                    WriteRtpHeader(wire[o..], f == frags - 1, (ushort)d.Seq++, tsMs * 90, d.Ssrc);
                    body.Slice(f * RtpPayload, pl).CopyTo(wire[(o + 12)..]);
                    o += 12 + pl;
                }
                break;
            }
        }
        _pktOff[n] = o;
        return n;
    }

    private void Deliver(Dest d, int count, bool key, long captureMs, float pipelineMs)
    {
        var L = d.D;
        var bytesPerMs = L.BandwidthMbps * 1000f / 8f;
        var loss = L.LossPct / 100f;
        var send0 = captureMs + pipelineMs;
        var srtWindow = Math.Clamp(3 * L.RttMs + 10, 20, 500);
        var complete = 0f; var dropped = false;
        var start = MathF.Max(send0, d.TxFreeMs);   // 송신 큐(직렬화 대기)
        d.TxFreeMs = start + _pktOff[count] / bytesPerMs;
        for (var p = 0; p < count; p++)
        {
            var depart = start + _pktOff[p + 1] / bytesPerMs;
            var arrive = depart + L.RttMs / 2 + L.JitterMs * Next(ref d.Rng);
            var tries = 0;
            while (Next(ref d.Rng) < loss && tries < 4)
            {
                tries++;
                d.Retx++;
                arrive += L.Protocol == LiveProtocol.Rtmp ? L.RttMs + 10 : L.RttMs + L.JitterMs;   // TCP 빠른 재전송 · NAK/NACK 왕복
                if (L.Protocol == LiveProtocol.WebRtc && tries >= 1 && arrive - captureMs > WebRtcDeadlineMs) { dropped = true; break; }
            }
            complete = MathF.Max(complete, arrive);
        }
        float latency;
        switch (L.Protocol)
        {
            case LiveProtocol.Rtmp:
                complete = MathF.Max(complete, d.TcpReadyMs);   // 순서 보장 바이트 스트림 — 선행 프레임 HOL
                d.TcpReadyMs = complete;
                latency = complete - captureMs;
                break;
            case LiveProtocol.Srt:
                var deadline = send0 + srtWindow;
                if (complete > deadline) dropped = true;
                latency = srtWindow + pipelineMs;
                break;
            default:
                if (d.PliPending > 0 && !key) dropped = true;   // 키프레임 대기(PLI)
                latency = complete - captureMs;
                if (latency > WebRtcDeadlineMs) dropped = true;
                break;
        }
        if (dropped)
        {
            d.Dropped++;
            if (L.Protocol == LiveProtocol.WebRtc) d.PliPending = 1;
        }
        else
        {
            if (key) d.PliPending = 0;
            d.Delivered++;
            d.LastMs = latency;
            d.Hist[Math.Clamp((int)latency, 0, HistBins - 1)]++;
            d.Recent[d.RecentHead] = latency;
            d.RecentHead = (d.RecentHead + 1) % d.Recent.Length;
            if (d.RecentCount < d.Recent.Length) d.RecentCount++;
        }
        Adapt(d, dropped);
    }

    /// <summary>ABR: 최근 2초 P95 &gt; 목표 또는 드롭 → 한 단계 하향, 5초 안정 + 대역 여유 → 상향.</summary>
    private void Adapt(Dest d, bool dropped)
    {
        if (d.RecentCount < 30 && !dropped) return;
        var n = d.RecentCount;
        d.Recent.AsSpan(0, n).CopyTo(_sorted);
        _sorted.AsSpan(0, n).Sort();
        var p95 = n > 0 ? _sorted[Math.Min(n - 1, (int)(n * 0.95f))] : 0;
        var over = Ladder[d.Rung] / 1000f > d.D.BandwidthMbps * 0.8f;
        if ((p95 > TargetMs && d.D.Protocol != LiveProtocol.Srt || dropped || over) && d.Rung < Ladder.Length - 1)
        {
            d.Rung++; d.Stable = 0; d.RecentCount = 0;
            return;
        }
        if (++d.Stable >= Fps * 5 && d.Rung > 0 && Ladder[d.Rung - 1] / 1000f < d.D.BandwidthMbps * 0.5f && p95 < TargetMs * 0.7f)
        {
            d.Rung--; d.Stable = 0; d.RecentCount = 0;
        }
    }

    private static float Next(ref ulong s)
    {
        s ^= s << 13; s ^= s >> 7; s ^= s << 17;
        return ((s * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    public void Announce(string action)
    {
        LastAction = action;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Live.Streamer.OnChange", ex); }
    }
}
