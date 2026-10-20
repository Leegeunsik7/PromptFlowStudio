using System.Buffers.Binary;
using System.Numerics;

namespace PromptFlowStudio.Services.MasterControl;

public enum SignalFault : byte { None, Loss, Black }
public enum PtpState : byte { Acquiring, Locked, Holdover }

/// <summary>
/// [PromptFlow Studio v17.05.0] SMPTE ST 2110 프레이밍 — -20 비압축 비디오(RFC 4175 · YCbCr 4:2:2 10비트 pgroup · BT.709 협대역),
/// -30 PCM 오디오(AES67 L24 · 48 kHz 스테레오 · 1 ms 패킷), -40 보조 데이터(RFC 8331 · ST 12-2 ATC 타임코드 · 10비트 워드 패리티/체크섬).
/// </summary>
public static class St2110
{
    public const byte PtVideo = 96, PtAudio = 97, PtAnc = 100;
    public const int RtpHeader = 12, VideoHeader = 20, AncHeader = 20;
    public const int AudioSamples = 48, AudioChannels = 2, AudioPayload = AudioSamples * AudioChannels * 3;
    public const int VideoTicksPerFrame = 90000 / 60;
    public const int AtcDid = 0x60, AtcSdid = 0x60, AtcWords = 16;

    public static int LineBytes(int width) => width / 2 * 5;
    public static int PgroupBytes(int width, int height) => LineBytes(width) * height;
    public static int MaxPacket(int width) => Math.Max(VideoHeader + LineBytes(width), RtpHeader + AudioPayload);

    public static void WriteRtp(Span<byte> p, byte pt, bool marker, ushort seq, uint ts, uint ssrc)
    {
        p[0] = 0x80; p[1] = (byte)((marker ? 0x80 : 0) | pt);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), ts);
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(8), ssrc);
    }

    public static byte PayloadType(ReadOnlySpan<byte> p) => (byte)(p[1] & 0x7F);
    public static bool Marker(ReadOnlySpan<byte> p) => (p[1] & 0x80) != 0;
    public static ushort Seq(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2));
    public static uint Timestamp(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32BigEndian(p.Slice(4));

    private static void Ycc(int r, int g, int b, out int y, out float cb, out float cr)
    {
        var yl = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        y = 64 + (int)MathF.Round(yl * (876f / 255f));
        cb = (b - yl) / 1.8556f; cr = (r - yl) / 1.5748f;
    }

    /// <summary>RGBA → 4:2:2 10비트 pgroup(Cb Y0 Cr Y1 = 40비트 빅엔디언 · 5 바이트/2 픽셀).</summary>
    public static void RgbaToPgroup(ReadOnlySpan<byte> rgba, Span<byte> pg, int width, int height)
    {
        var d = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x += 2, d += 5)
            {
                var o = (y * width + x) * 4;
                Ycc(rgba[o], rgba[o + 1], rgba[o + 2], out var y0, out var cb0, out var cr0);
                Ycc(rgba[o + 4], rgba[o + 5], rgba[o + 6], out var y1, out var cb1, out var cr1);
                var cb = Math.Clamp(512 + (int)MathF.Round((cb0 + cb1) * 0.5f * (896f / 255f)), 64, 960);
                var cr = Math.Clamp(512 + (int)MathF.Round((cr0 + cr1) * 0.5f * (896f / 255f)), 64, 960);
                y0 = Math.Clamp(y0, 64, 940); y1 = Math.Clamp(y1, 64, 940);
                pg[d] = (byte)(cb >> 2);
                pg[d + 1] = (byte)((cb & 3) << 6 | y0 >> 4);
                pg[d + 2] = (byte)((y0 & 15) << 4 | cr >> 6);
                pg[d + 3] = (byte)((cr & 63) << 2 | y1 >> 8);
                pg[d + 4] = (byte)y1;
            }
    }

    public static void Unpack(ReadOnlySpan<byte> g, out int cb, out int y0, out int cr, out int y1)
    {
        cb = g[0] << 2 | g[1] >> 6;
        y0 = (g[1] & 63) << 4 | g[2] >> 4;
        cr = (g[2] & 15) << 6 | g[3] >> 2;
        y1 = (g[3] & 3) << 8 | g[4];
    }

    /// <summary>pgroup → RGBA(모니터/검증용).</summary>
    public static void PgroupToRgba(ReadOnlySpan<byte> pg, Span<byte> rgba, int width, int height)
    {
        var s = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x += 2, s += 5)
            {
                Unpack(pg.Slice(s, 5), out var cb, out var y0, out var cr, out var y1);
                var o = (y * width + x) * 4;
                Rgb(y0, cb, cr, rgba.Slice(o)); Rgb(y1, cb, cr, rgba.Slice(o + 4));
            }
    }

    private static void Rgb(int y10, int cb10, int cr10, Span<byte> d)
    {
        float yl = (y10 - 64) * (255f / 876f), pb = (cb10 - 512) * (255f / 896f), pr = (cr10 - 512) * (255f / 896f);
        var r = yl + 1.5748f * pr; var b = yl + 1.8556f * pb;
        var g = (yl - 0.2126f * r - 0.0722f * b) / 0.7152f;
        d[0] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255); d[1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255); d[2] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255); d[3] = 255;
    }

    /// <summary>pgroup 평균 휘도(10비트 · 8 그룹 간격 표본).</summary>
    public static int MeanY(ReadOnlySpan<byte> pg)
    {
        long acc = 0; var n = 0;
        for (var s = 0; s + 5 <= pg.Length; s += 40) { Unpack(pg.Slice(s, 5), out _, out var y0, out _, out var y1); acc += y0 + y1; n += 2; }
        return n == 0 ? 0 : (int)(acc / n);
    }

    /// <summary>-20 패킷(라인 1개 = SRD 1개): RTP + 확장 시퀀스 + SRD(길이 · F|행 · C|오프셋) + 라인 페이로드.</summary>
    public static int WriteVideo(Span<byte> p, ushort seq, ushort extSeq, uint ts, uint ssrc, bool marker, int row, ReadOnlySpan<byte> line)
    {
        WriteRtp(p, PtVideo, marker, seq, ts, ssrc);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(12), extSeq);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(14), (ushort)line.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(16), (ushort)(row & 0x7FFF));
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(18), 0);
        line.CopyTo(p.Slice(VideoHeader));
        return VideoHeader + line.Length;
    }

    public static bool ParseVideo(ReadOnlySpan<byte> p, out int row, out int offset, out ReadOnlySpan<byte> line)
    {
        row = offset = 0; line = default;
        if (p.Length < VideoHeader || PayloadType(p) != PtVideo) return false;
        int len = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(14));
        row = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(16)) & 0x7FFF;
        offset = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(18)) & 0x7FFF;
        if (VideoHeader + len > p.Length) return false;
        line = p.Slice(VideoHeader, len);
        return true;
    }

    /// <summary>-30 L24 패킷(48 샘플 × 2 채널 · 빅엔디언 24비트).</summary>
    public static int WriteAudio(Span<byte> p, ushort seq, uint ts, uint ssrc, ReadOnlySpan<float> interleaved)
    {
        WriteRtp(p, PtAudio, false, seq, ts, ssrc);
        var d = RtpHeader;
        for (var i = 0; i < AudioSamples * AudioChannels; i++, d += 3)
        {
            var v = Math.Clamp((int)MathF.Round(interleaved[i] * 8388607f), -8388608, 8388607);
            p[d] = (byte)(v >> 16); p[d + 1] = (byte)(v >> 8); p[d + 2] = (byte)v;
        }
        return d;
    }

    public static int L24(ReadOnlySpan<byte> p, int i) { var o = RtpHeader + i * 3; return (sbyte)p[o] << 16 | p[o + 1] << 8 | p[o + 2]; }

    private static int Word10(int v8)
    {
        var par = BitOperations.PopCount((uint)v8) & 1;
        return v8 & 0xFF | par << 8 | (par ^ 1) << 9;
    }

    private static bool WordOk(int w) { var par = BitOperations.PopCount((uint)(w & 0xFF)) & 1; return (w >> 8 & 1) == par && (w >> 9 & 1) == (par ^ 1); }

    private ref struct Bits
    {
        private readonly Span<byte> _d;
        public int Pos;
        public Bits(Span<byte> d, int pos) { _d = d; Pos = pos; }
        public void Put(int v, int n) { for (var i = n - 1; i >= 0; i--, Pos++) { var bit = v >> i & 1; var bi = Pos >> 3; var m = 0x80 >> (Pos & 7); _d[bi] = (byte)(bit != 0 ? _d[bi] | m : _d[bi] & ~m); } }
        public int Get(ReadOnlySpan<byte> s, int n) { var v = 0; for (var i = 0; i < n; i++, Pos++) v = v << 1 | s[Pos >> 3] >> (7 - (Pos & 7)) & 1; return v; }
    }

    /// <summary>-40 RFC 8331 패킷: ATC(DID 0x60/SDID 0x60 · UDW 16 · BCD 니블 b4..b7) 1개 · 라인 9.</summary>
    public static int WriteAnc(Span<byte> p, ushort seq, ushort extSeq, uint ts, uint ssrc, long frame, int fps)
    {
        WriteRtp(p, PtAnc, true, seq, ts, ssrc);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(12), extSeq);
        p[16] = 1; p[17] = p[18] = p[19] = 0;   // ANC_Count = 1 · F = 00(순차)
        Span<byte> bcd = stackalloc byte[4];
        SmpteTimecode.ToBcd(frame, fps, bcd);
        var bitsLen = 64 + 10 * (3 + AtcWords + 1);
        var bytes = (bitsLen + 31) / 32 * 4;
        p.Slice(AncHeader, bytes).Clear();
        var w = new Bits(p.Slice(AncHeader), 0);
        w.Put(0, 1); w.Put(9, 11); w.Put(0, 12); w.Put(0, 1); w.Put(0, 7);
        int did = Word10(AtcDid), sdid = Word10(AtcSdid), dc = Word10(AtcWords);
        w.Put(did, 10); w.Put(sdid, 10); w.Put(dc, 10);
        var sum = (did & 0x1FF) + (sdid & 0x1FF) + (dc & 0x1FF);
        for (var i = 0; i < AtcWords; i++)
        {
            var nib = i < 8 ? (bcd[i >> 1] >> ((i & 1) * 4)) & 0xF : 0;
            var u = Word10(nib << 4);
            w.Put(u, 10); sum += u & 0x1FF;
        }
        sum &= 0x1FF;
        w.Put(sum | ((sum >> 8 & 1) ^ 1) << 9, 10);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(14), (ushort)bytes);
        return AncHeader + bytes;
    }

    /// <summary>-40 파싱 → 타임코드 프레임(패리티/체크섬/DID 오류 시 −1).</summary>
    public static long ParseAnc(ReadOnlySpan<byte> p, int fps)
    {
        if (p.Length < AncHeader + 4 || PayloadType(p) != PtAnc || p[16] < 1) return -1;
        var s = p.Slice(AncHeader);
        if (s.Length * 8 < 64 + 10 * (3 + AtcWords + 1)) return -1;
        var r = new Bits(Span<byte>.Empty, 0);
        r.Get(s, 32);
        int did = r.Get(s, 10), sdid = r.Get(s, 10), dc = r.Get(s, 10);
        if (!WordOk(did) || !WordOk(sdid) || !WordOk(dc) || (did & 0xFF) != AtcDid || (sdid & 0xFF) != AtcSdid || (dc & 0xFF) != AtcWords) return -1;
        var sum = (did & 0x1FF) + (sdid & 0x1FF) + (dc & 0x1FF);
        Span<byte> bcd = stackalloc byte[4];
        bcd.Clear();
        for (var i = 0; i < AtcWords; i++)
        {
            var u = r.Get(s, 10);
            if (!WordOk(u)) return -1;
            sum += u & 0x1FF;
            if (i < 8) bcd[i >> 1] |= (byte)(((u >> 4) & 0xF) << ((i & 1) * 4));
        }
        var cs = r.Get(s, 10);
        sum &= 0x1FF;
        if ((cs & 0x1FF) != sum || (cs >> 9 & 1) != ((sum >> 8 & 1) ^ 1)) return -1;
        return SmpteTimecode.FromBcd(bcd, fps);
    }
}

/// <summary>[PromptFlow Studio v17.05.0] ST 2022-7 무결절 병합기(RTP 스트림 1개) — 확장 시퀀스 창(4096) · 첫 도착 채택 · 중복 폐기 · 경로 간 지연차(PD) 계측.</summary>
public sealed class HitlessMerger
{
    public const int Window = 4096;
    private readonly long[] _ext = new long[Window];
    private readonly long[] _firstUs = new long[Window];
    private readonly byte[] _firstPath = new byte[Window];
    private long _hi = -1;

    public HitlessMerger() => Array.Fill(_ext, -1L);

    public long ReceivedA { get; private set; }
    public long ReceivedB { get; private set; }
    public long WonA { get; private set; }
    public long WonB { get; private set; }
    public long Duplicates { get; private set; }
    public long Late { get; private set; }
    public long Highest => _hi;
    /// <summary>B − A 도착 차(µs · EWMA) · 최대 절대값.</summary>
    public double SkewUs { get; private set; }
    public double MaxSkewUs { get; private set; }

    public void Reset() { Array.Fill(_ext, -1L); _hi = -1; ReceivedA = ReceivedB = WonA = WonB = Duplicates = Late = 0; SkewUs = MaxSkewUs = 0; }

    /// <summary>패킷 1개 → 첫 사본이면 true(애플리케이션 전달).</summary>
    public bool Accept(int path, ushort seq, long arrivalUs)
    {
        if (path == 0) ReceivedA++; else ReceivedB++;
        var ext = _hi < 0 ? seq : _hi + (short)(seq - (ushort)_hi);
        if (_hi >= 0 && ext <= _hi - Window) { Late++; return false; }
        var idx = (int)(ext & (Window - 1));
        if (_ext[idx] == ext)
        {
            Duplicates++;
            double d = arrivalUs - _firstUs[idx];
            if (_firstPath[idx] == 1) d = -d;
            SkewUs += (d - SkewUs) * 0.05;
            if (Math.Abs(d) > MaxSkewUs) MaxSkewUs = Math.Abs(d);
            return false;
        }
        _ext[idx] = ext; _firstUs[idx] = arrivalUs; _firstPath[idx] = (byte)path;
        if (path == 0) WonA++; else WonB++;
        if (ext > _hi) _hi = ext;
        return true;
    }
}

/// <summary>[PromptFlow Studio v17.05.0] 인프로세스 IP 경로 모사 — 무작위 손실 · 단절(아웃티지) · 기본 지연 + 지터(재정렬) · 고정 패킷 풀.</summary>
public sealed class NetworkPath
{
    private readonly byte[] _pool;
    private readonly long[] _arrive;
    private readonly int[] _len, _active, _due, _free;
    private readonly int _maxPacket;
    private int _activeN, _freeN;
    private ulong _rng;
    private long _downFrom = long.MaxValue, _downUntil = long.MinValue;

    public NetworkPath(int maxPacket, int slots = 2048, ulong seed = 0x9E3779B97F4A7C15UL)
    {
        _maxPacket = maxPacket; _rng = seed | 1;
        _pool = new byte[slots * maxPacket]; _arrive = new long[slots]; _len = new int[slots];
        _active = new int[slots]; _due = new int[slots]; _free = new int[slots];
        for (var i = 0; i < slots; i++) _free[i] = slots - 1 - i;
        _freeN = slots;
    }

    public float LossRate { get; set; }
    public double BaseDelayUs { get; set; } = 400;
    public double JitterUs { get; set; } = 150;
    public long Sent { get; private set; }
    public long Dropped { get; private set; }
    public long Delivered { get; private set; }
    public long Overflow { get; private set; }
    public int InFlight => _activeN;
    public bool IsDown(long nowUs) => nowUs >= _downFrom && nowUs < _downUntil;

    public void Outage(long fromUs, long durationUs) { _downFrom = fromUs; _downUntil = fromUs + durationUs; }

    private double Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 11) * (1.0 / 9007199254740992.0);
    }

    public void Send(ReadOnlySpan<byte> pkt, long nowUs)
    {
        Sent++;
        var r = Next01();
        if (IsDown(nowUs) || r < LossRate) { Dropped++; return; }
        if (_freeN == 0 || pkt.Length > _maxPacket) { Overflow++; return; }
        var s = _free[--_freeN];
        pkt.CopyTo(_pool.AsSpan(s * _maxPacket));
        _len[s] = pkt.Length;
        _arrive[s] = nowUs + (long)(BaseDelayUs + JitterUs * Next01());
        _active[_activeN++] = s;
    }

    /// <summary>도착 시각 ≤ now 패킷을 도착 순으로 가드에 전달(무할당 삽입 정렬).</summary>
    public int Deliver(long nowUs, OnAirBroadcastSignalFailSafeGuard guard, int path)
    {
        var n = 0;
        for (var i = 0; i < _activeN;)
        {
            var s = _active[i];
            if (_arrive[s] > nowUs) { i++; continue; }
            _active[i] = _active[--_activeN];
            var j = n++;
            while (j > 0 && _arrive[_due[j - 1]] > _arrive[s]) { _due[j] = _due[j - 1]; j--; }
            _due[j] = s;
        }
        for (var k = 0; k < n; k++)
        {
            var s = _due[k];
            guard.Ingest(path, _pool.AsSpan(s * _maxPacket, _len[s]), _arrive[s]);
            _free[_freeN++] = s;
        }
        Delivered += n;
        return n;
    }
}

/// <summary>[PromptFlow Studio v17.05.0] ST 2110 송신기 — PGM → -20(라인당 1 패킷 · 마지막 마커) · -30(1 ms L24 · 잔여 샘플 이월) · -40(ATC) → 두 경로 동일 사본(2022-7).</summary>
public sealed class St2110Sender
{
    public const uint SsrcVideo = 0x2110_0020, SsrcAudio = 0x2110_0030, SsrcAnc = 0x2110_0040;
    private readonly byte[] _pkt, _pg;
    private readonly float[] _carry = new float[St2110.AudioSamples * St2110.AudioChannels];
    private readonly int _w, _h;
    private int _carryN;
    private bool _started;
    private uint _vSeq, _aSeq, _ancSeq;
    private long _audioSamples;

    public St2110Sender(int width, int height)
    {
        _w = width; _h = height;
        _pkt = new byte[St2110.MaxPacket(width)];
        _pg = new byte[St2110.PgroupBytes(width, height)];
    }

    /// <summary>직전 송신 프레임 pgroup(무결절 검증 기준).</summary>
    public ReadOnlySpan<byte> LastPgroup => _pg;
    public long VideoPackets { get; private set; }
    public long AudioPackets { get; private set; }
    public long AncPackets { get; private set; }
    public double PacketizeUs { get; private set; }
    /// <summary>비압축 -20 비트레이트(실해상도 기준 아님 · 프록시 해상도 × 60p).</summary>
    public double VideoMbps => St2110.PgroupBytes(_w, _h) * 8.0 * 60 / 1e6;

    public void SendFrame(ReadOnlySpan<byte> pgmRgba, ReadOnlySpan<float> audio, long frame, long t0Us, double frameUs, NetworkPath a, NetworkPath b)
    {
        var c0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_started) { _audioSamples = frame * (audio.Length / St2110.AudioChannels); _started = true; }   // 오디오 RTP 시각 = 프레임 정렬
        St2110.RgbaToPgroup(pgmRgba, _pg, _w, _h);
        var ts = (uint)(frame * St2110.VideoTicksPerFrame);
        var lb = St2110.LineBytes(_w);
        var per = frameUs / (_h + 20);
        for (var y = 0; y < _h; y++)
        {
            var n = St2110.WriteVideo(_pkt, (ushort)_vSeq, (ushort)(_vSeq >> 16), ts, SsrcVideo, y == _h - 1, y, _pg.AsSpan(y * lb, lb));
            _vSeq++;
            var at = t0Us + (long)(y * per);
            a.Send(_pkt.AsSpan(0, n), at); b.Send(_pkt.AsSpan(0, n), at);
            VideoPackets++;
        }
        var spp = St2110.AudioSamples * St2110.AudioChannels;
        for (var i = 0; i < audio.Length; i++)
        {
            _carry[_carryN++] = audio[i];
            if (_carryN < spp) continue;
            var n = St2110.WriteAudio(_pkt, (ushort)_aSeq++, (uint)_audioSamples, SsrcAudio, _carry);
            _audioSamples += St2110.AudioSamples; _carryN = 0;
            var at = t0Us + (long)(i * frameUs / audio.Length);
            a.Send(_pkt.AsSpan(0, n), at); b.Send(_pkt.AsSpan(0, n), at);
            AudioPackets++;
        }
        var na = St2110.WriteAnc(_pkt, (ushort)_ancSeq, (ushort)(_ancSeq >> 16), ts, SsrcAnc, frame, 60);
        _ancSeq++;
        a.Send(_pkt.AsSpan(0, na), t0Us); b.Send(_pkt.AsSpan(0, na), t0Us);
        AncPackets++;
        PacketizeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}

/// <summary>
/// [PromptFlow Studio v17.05.0] PTP(IEEE 1588 · ST 2059-2) 슬레이브 서보 모사 — Sync/Delay_Req 4-타임스탬프 오프셋 · PI(0.7/0.3) 주파수 보정 ·
/// 1 ms 초과 스텝 · Announce 타임아웃(3 × 250 ms) → BMCA(priority1 → clockClass → priority2) GM 전환 · 홀드오버(주파수 동결).
/// </summary>
public sealed class PtpSlaveServo
{
    public const double SyncIntervalUs = 125_000, AnnounceIntervalUs = 250_000, LockNs = 1000, StepNs = 1_000_000;
    public static readonly string[] GmNames = { "GM-A (GPS)", "GM-B (GPS 백업)" };

    private readonly bool[] _alive = { true, true };
    private readonly int[] _prio1 = { 128, 129 }, _clockClass = { 6, 6 }, _prio2 = { 128, 128 };
    private readonly double[] _gmErrNs = { 0, 140 };
    private readonly double[] _lastAnnounceUs = new double[2];
    private double _nowUs, _errNs, _adjPpb, _integral, _nextSyncUs, _holdSinceUs;
    private int _goodSyncs;
    private ulong _rng = 0xD1B54A32D192ED03UL;

    public PtpSlaveServo(double driftPpm = 18, double initialOffsetNs = 250_000) { DriftPpm = driftPpm; _errNs = initialOffsetNs; Selected = 0; }

    public double DriftPpm { get; set; }
    public double AsymmetryJitterNs { get; set; } = 40;
    public int Selected { get; private set; }
    public PtpState State { get; private set; } = PtpState.Acquiring;
    /// <summary>선택 GM 기준 최근 측정 오프셋(ns).</summary>
    public double OffsetNs { get; private set; }
    /// <summary>모사 참값: 슬레이브 − 선택 GM 시각(ns).</summary>
    public double TrueOffsetNs => _errNs - _gmErrNs[Selected];
    public double FrequencyAdjPpb => _adjPpb;
    public long Syncs { get; private set; }
    public long Steps { get; private set; }
    public long GmChanges { get; private set; }
    public double HoldoverUs => State == PtpState.Holdover ? _nowUs - _holdSinceUs : 0;
    public bool GmAlive(int g) => _alive[g];
    /// <summary>스위칭 기준 클록: PTP 락 = 그랜드마스터 · 그 외 = 로컬 플라이휠(프레임 경계 유지).</summary>
    public bool ClockOk => State == PtpState.Locked;

    public void SetGmAlive(int g, bool alive) { _alive[g] = alive; if (alive) _lastAnnounceUs[g] = _nowUs; }

    private double Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return ((_rng >> 11) * (1.0 / 9007199254740992.0) - 0.5) * 2; }

    public void Step(double dtUs)
    {
        _nowUs += dtUs;
        _errNs += dtUs * (DriftPpm * 1000 + _adjPpb) * 1e-6;
        for (var g = 0; g < 2; g++) if (_alive[g] && _nowUs - _lastAnnounceUs[g] >= AnnounceIntervalUs) _lastAnnounceUs[g] = _nowUs;
        if (!_alive[Selected] && _nowUs - _lastAnnounceUs[Selected] > 3 * AnnounceIntervalUs) Bmca();
        if (_nowUs < _nextSyncUs) return;
        _nextSyncUs = _nowUs + SyncIntervalUs;
        if (!_alive[Selected])
        {
            if (State != PtpState.Holdover) { State = PtpState.Holdover; _holdSinceUs = _nowUs; }
            return;
        }
        Syncs++;
        // t2 − t1 = d_ms + off, t4 − t3 = d_sm − off → off = ((t2−t1) − (t4−t3)) / 2 + 경로 비대칭/타임스탬프 지터.
        var meas = TrueOffsetNs + AsymmetryJitterNs * Noise();
        OffsetNs = meas;
        if (Math.Abs(meas) > StepNs) { _errNs -= meas; _integral = 0; _adjPpb = 0; Steps++; _goodSyncs = 0; State = PtpState.Acquiring; return; }
        var T = SyncIntervalUs * 1e-6;
        _integral += 0.3 * meas;
        _adjPpb = -(0.7 * meas + _integral) / T;
        _goodSyncs = Math.Abs(meas) < LockNs ? _goodSyncs + 1 : 0;
        State = _goodSyncs >= 4 ? PtpState.Locked : PtpState.Acquiring;
    }

    /// <summary>최적 마스터 선택(살아 있는 GM 중 priority1 → clockClass → priority2 → 식별자).</summary>
    private void Bmca()
    {
        var best = -1;
        for (var g = 0; g < 2; g++)
        {
            if (!_alive[g]) continue;
            if (best < 0 || _prio1[g] < _prio1[best] || _prio1[g] == _prio1[best] && (_clockClass[g] < _clockClass[best] || _clockClass[g] == _clockClass[best] && _prio2[g] < _prio2[best])) best = g;
        }
        if (best < 0) { if (State != PtpState.Holdover) { State = PtpState.Holdover; _holdSinceUs = _nowUs; } return; }
        if (best != Selected) { Selected = best; GmChanges++; _goodSyncs = 0; State = PtpState.Acquiring; _nextSyncUs = _nowUs; }
    }

    /// <summary>복구된 상위 GM 재선택(Announce 수신 시 BMCA 재평가).</summary>
    public void Reevaluate() => Bmca();
}

/// <summary>
/// [PromptFlow Studio v17.05.0] 🛡 ON-AIR 신호 페일세이프 가드 — ST 2022-7 무결절 보호 전환(-20/-30/-40 스트림별 병합기) +
/// 재조립(8 프레임 슬롯 · 2 프레임 플레이아웃 지연) · 양 경로 동시 손실 라인/프레임 은닉(직전 송출 라인 유지 · 블랙 금지) ·
/// 오디오 패킷 은닉 · ATC 타임코드 검증 · PTP 서보(GM 장애 → BMCA/홀드오버 · 스위칭 플라이휠) ·
/// PGM 입력 감시(신호 결손 ≥ 2 프레임 · 블랙 ≥ 3 프레임 → 건강한 백업으로 프레임 경계 비상 컷).
/// </summary>
public sealed class OnAirBroadcastSignalFailSafeGuard
{
    public const int Slots = 8, AudioSlots = 512;

    private readonly byte[] _reasm, _air, _airRgba, _audio;
    private readonly long[] _slotFrame, _audPkt;
    private readonly bool[] _line;
    private readonly int _w, _h, _lb;
    private long _lastPlayed = -1, _lastAudioPkt = -1;
    private int _blackRun;
    private SignalFault _lastFault;

    public OnAirBroadcastSignalFailSafeGuard(int width = 128, int height = 72)
    {
        _w = width; _h = height; _lb = St2110.LineBytes(width);
        _reasm = new byte[Slots * St2110.PgroupBytes(width, height)];
        _air = new byte[St2110.PgroupBytes(width, height)];
        _airRgba = new byte[width * height * 4];
        _slotFrame = new long[Slots]; Array.Fill(_slotFrame, -1L);
        _line = new bool[Slots * height];
        _audio = new byte[AudioSlots * St2110.AudioPayload];
        _audPkt = new long[AudioSlots]; Array.Fill(_audPkt, -1L);
        // 기동 시 송출 = 블랙 버스트가 아닌 컬러바 대기 신호.
        RealtimeBroadcastVisionSwitcherEngine.RenderBars(_airRgba, width, height);
        St2110.RgbaToPgroup(_airRgba, _air, width, height);
    }

    public HitlessMerger Video { get; } = new();
    public HitlessMerger Audio { get; } = new();
    public HitlessMerger Anc { get; } = new();
    public PtpSlaveServo Ptp { get; } = new();
    public int PlayoutFrames { get; set; } = 2;
    public int BackupInput { get; set; } = 6;
    public int LossFrames { get; set; } = 2;
    public int BlackFrames { get; set; } = 3;
    public int BlackLuma { get; set; } = 16;

    public long AirFrames { get; private set; }
    public long CompleteFrames { get; private set; }
    public long ConcealedLines { get; private set; }
    public long FrozenFrames { get; private set; }
    public long AirBlackFrames { get; private set; }
    public long LatePackets { get; private set; }
    public long AudioConcealed { get; private set; }
    public long AudioPlayed { get; private set; }
    public long AncErrors { get; private set; }
    public long LastTimecode { get; private set; } = -1;
    public long Failovers { get; private set; }
    public long LossFaults { get; private set; }
    public long BlackFaults { get; private set; }
    public SignalFault LastFault => _lastFault;
    public int LastFaultInput { get; private set; } = -1;
    public int LastBackup { get; private set; } = -1;
    public long LastFaultFrame { get; private set; } = -1;
    public int AirMeanY { get; private set; }
    public float AirAudioPeakDbfs { get; private set; } = -120f;
    public double IngestUs { get; private set; }
    public ReadOnlySpan<byte> AirPgroup => _air;
    /// <summary>한 경로로만 도착(다른 경로 손실)해 무결절 복구된 패킷 수(-20 + -30).</summary>
    public long HitlessRecovered => Video.WonA + Video.WonB - Video.Duplicates + Audio.WonA + Audio.WonB - Audio.Duplicates;

    /// <summary>패킷 1개 수신(경로 0 = 메인 · 1 = 서브). 첫 사본만 재조립에 반영.</summary>
    public void Ingest(int path, ReadOnlySpan<byte> p, long arrivalUs)
    {
        if (p.Length < St2110.RtpHeader) return;
        var pt = St2110.PayloadType(p);
        var seq = St2110.Seq(p);
        if (pt == St2110.PtVideo)
        {
            if (!Video.Accept(path, seq, arrivalUs)) return;
            long frame = St2110.Timestamp(p) / St2110.VideoTicksPerFrame;
            if (frame <= _lastPlayed) { LatePackets++; return; }
            var slot = (int)(frame % Slots);
            if (_slotFrame[slot] != frame)
            {
                if (_slotFrame[slot] > frame) { LatePackets++; return; }
                _slotFrame[slot] = frame;
                Array.Clear(_line, slot * _h, _h);
            }
            if (!St2110.ParseVideo(p, out var row, out var off, out var line) || row >= _h || off + line.Length > _lb) return;
            line.CopyTo(_reasm.AsSpan(slot * _air.Length + row * _lb + off));
            _line[slot * _h + row] = true;
        }
        else if (pt == St2110.PtAudio)
        {
            if (!Audio.Accept(path, seq, arrivalUs)) return;
            long pk = St2110.Timestamp(p) / St2110.AudioSamples;
            if (pk <= _lastAudioPkt) { LatePackets++; return; }
            var s = (int)(pk % AudioSlots);
            p.Slice(St2110.RtpHeader, St2110.AudioPayload).CopyTo(_audio.AsSpan(s * St2110.AudioPayload));
            _audPkt[s] = pk;
        }
        else if (pt == St2110.PtAnc)
        {
            if (!Anc.Accept(path, seq, arrivalUs)) return;
            var tc = St2110.ParseAnc(p, 60);
            if (tc < 0) AncErrors++; else if (tc > LastTimecode) LastTimecode = tc;
        }
    }

    /// <summary>플레이아웃 마감 프레임 송출: 수신 라인 반영 · 결손 라인/프레임은 직전 송출 유지(은닉) · 오디오 패킷 은닉.</summary>
    public void Playout(long frame)
    {
        if (frame <= _lastPlayed) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var slot = (int)(frame % Slots);
        if (_slotFrame[slot] != frame) FrozenFrames++;
        else
        {
            var missing = 0;
            for (var y = 0; y < _h; y++)
            {
                if (_line[slot * _h + y]) _reasm.AsSpan(slot * _air.Length + y * _lb, _lb).CopyTo(_air.AsSpan(y * _lb));
                else missing++;
            }
            ConcealedLines += missing;
            if (missing == 0) CompleteFrames++;
            if (missing == _h) FrozenFrames++;
            _slotFrame[slot] = -1;
        }
        _lastPlayed = frame;
        AirFrames++;
        AirMeanY = St2110.MeanY(_air);
        if (AirMeanY < 64 + 16) AirBlackFrames++;
        // 오디오: 이 프레임 구간(800 샘플)에 시작하는 패킷.
        long p0 = frame * RealtimeBroadcastVisionSwitcherEngine.AudioBlock / St2110.AudioSamples, p1 = (frame + 1) * RealtimeBroadcastVisionSwitcherEngine.AudioBlock / St2110.AudioSamples;
        var peak = 0;
        for (var pk = Math.Max(p0, _lastAudioPkt + 1); pk < p1; pk++)
        {
            var s = (int)(pk % AudioSlots);
            if (_audPkt[s] != pk)
            {
                AudioConcealed++;
                var prev = (int)((pk - 1 + AudioSlots) % AudioSlots);
                _audio.AsSpan(prev * St2110.AudioPayload, St2110.AudioPayload).CopyTo(_audio.AsSpan(s * St2110.AudioPayload));
            }
            AudioPlayed++;
            for (var i = 0; i < St2110.AudioSamples * St2110.AudioChannels; i += 8)
            {
                var o = s * St2110.AudioPayload + i * 3;
                var v = Math.Abs((sbyte)_audio[o] << 16 | _audio[o + 1] << 8 | _audio[o + 2]);
                if (v > peak) peak = v;
            }
            _audPkt[s] = -1;
            _lastAudioPkt = pk;
        }
        AirAudioPeakDbfs = peak > 8 ? 20f * MathF.Log10(peak / 8388607f) : -120f;
        IngestUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>송출 pgroup → RGBA(UI 모니터).</summary>
    public ReadOnlySpan<byte> AirRgba()
    {
        St2110.PgroupToRgba(_air, _airRgba, _w, _h);
        return _airRgba;
    }

    /// <summary>
    /// PGM 입력 감시(합성 직전 · 무할당): 신호 결손(프레임 미도착 ≥ LossFrames — 그동안 프레임 싱크가 직전 프레임 유지) 또는
    /// 블랙(평균 휘도 &lt; BlackLuma가 BlackFrames 연속) → 건강한 백업으로 비상 컷. 트랜지션 대상(PVW) 결손 시 트랜지션 중단.
    /// 의도적 블랙(BLK 소스 · FTB)은 제외.
    /// </summary>
    public SignalFault Monitor(RealtimeBroadcastVisionSwitcherEngine sw)
    {
        var pg = sw.Pgm;
        if (sw.InTransition && sw.InputAgeFrames(sw.Pvw) >= LossFrames) sw.EmergencyCut(pg);
        if (pg == RealtimeBroadcastVisionSwitcherEngine.BlackInput || sw.FadeToBlack) { _blackRun = 0; return SignalFault.None; }
        var fault = SignalFault.None;
        if (sw.InputAgeFrames(pg) >= LossFrames) fault = SignalFault.Loss;
        else if (sw.MeanLuma(pg) < BlackLuma) { if (++_blackRun >= BlackFrames) fault = SignalFault.Black; }
        else _blackRun = 0;
        if (fault == SignalFault.None) return fault;
        var backup = PickBackup(sw, pg);
        sw.EmergencyCut(backup);
        _blackRun = 0;
        Failovers++;
        if (fault == SignalFault.Loss) LossFaults++; else BlackFaults++;
        _lastFault = fault; LastFaultInput = pg; LastBackup = backup; LastFaultFrame = sw.Frame;
        return fault;
    }

    private bool Healthy(RealtimeBroadcastVisionSwitcherEngine sw, int i) => sw.InputAgeFrames(i) < LossFrames && sw.MeanLuma(i) >= BlackLuma;

    /// <summary>백업 우선 → 카메라 0..12 중 건강 소스 → 컬러바(블랙 송출 금지).</summary>
    private int PickBackup(RealtimeBroadcastVisionSwitcherEngine sw, int failed)
    {
        if (BackupInput != failed && Healthy(sw, BackupInput)) return BackupInput;
        for (var i = 0; i < RealtimeBroadcastVisionSwitcherEngine.ReplayInput; i++)
            if (i != failed && Healthy(sw, i)) return i;
        return RealtimeBroadcastVisionSwitcherEngine.BarsInput;
    }
}
