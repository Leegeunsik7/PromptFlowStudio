using System.Buffers.Binary;
namespace PromptFlowStudio.Services.GraphBridge;

/// <summary>
/// MP4(ISO BMFF) 헤더 프로브 — moov/mvhd 길이 · 첫 'vide' 트랙 tkhd 해상도 · mdhd/stts 프레임레이트(무할당 · 박스 시크).
/// </summary>
public static class Mp4Probe
{
    private struct TrackAcc { public int W, H; public uint Ts, Delta, Id; public bool Vide; public int PendW, PendH; public uint PendTs, PendId, TrexDur; }

    public static bool TryProbe(Stream s, out ReelsVideoSpec spec)
    {
        spec = default;
        spec.Bytes = s.Length;
        Span<byte> buf = stackalloc byte[128];
        TrackAcc none = default;
        var ok = Walk(s, 0, s.Length, 0, buf, ref none, ref spec);
        if (spec.Width == 0 && none.PendTs > 0 && none.TrexDur > 0) { spec.Width = none.PendW; spec.Height = none.PendH; spec.Fps = (float)none.PendTs / none.TrexDur; } // fMP4: stts 비어 있음 → trex 기본 길이
        return ok && spec.Width > 0 && spec.Height > 0 && spec.Fps > 0 && spec.DurationSec > 0;
    }

    private static bool Walk(Stream s, long start, long end, int depth, Span<byte> buf, ref TrackAcc acc, ref ReelsVideoSpec spec)
    {
        if (depth > 6) return false;
        var pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            if (s.ReadAtLeast(buf[..8], 8, false) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(buf);
            var type = BinaryPrimitives.ReadUInt32BigEndian(buf[4..]);
            var hdr = 8;
            if (size == 1) { if (s.ReadAtLeast(buf[..8], 8, false) < 8) break; size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf); hdr = 16; }
            else if (size == 0) size = end - pos;
            if (size < hdr || pos + size > end) return false;
            var body = pos + hdr; var len = size - hdr;
            switch (type)
            {
                case 0x6D6F6F76: // moov
                case 0x6D646961: // mdia
                case 0x6D696E66: // minf
                case 0x7374626C: // stbl
                case 0x6D766578: // mvex
                    if (!Walk(s, body, body + len, depth + 1, buf, ref acc, ref spec)) return false;
                    break;
                case 0x7472616B: // trak
                    TrackAcc t = default;
                    if (!Walk(s, body, body + len, depth + 1, buf, ref t, ref spec)) return false;
                    if (t.Vide && t.W > 0 && t.Ts > 0 && t.Delta > 0 && spec.Width == 0) { spec.Width = t.W; spec.Height = t.H; spec.Fps = (float)t.Ts / t.Delta; }
                    else if (t.Vide && t.W > 0 && t.Ts > 0 && acc.PendTs == 0) { acc.PendW = t.W; acc.PendH = t.H; acc.PendTs = t.Ts; acc.PendId = t.Id; }
                    break;
                default:
                    var want = (int)Math.Min(len, buf.Length);
                    var n = s.ReadAtLeast(buf[..want], want, false);
                    var p = buf[..n];
                    var v1 = n > 0 && p[0] == 1;
                    if (type == 0x6D766864 && n >= 32) // mvhd
                    {
                        var tsc = BinaryPrimitives.ReadUInt32BigEndian(p[(v1 ? 20 : 12)..]);
                        var d = v1 ? BinaryPrimitives.ReadUInt64BigEndian(p[24..]) : BinaryPrimitives.ReadUInt32BigEndian(p[16..]);
                        if (tsc > 0) spec.DurationSec = (float)((double)d / tsc);
                    }
                    else if (type == 0x746B6864 && n >= (v1 ? 96 : 84)) // tkhd
                    {
                        var o = v1 ? 88 : 76;
                        acc.W = (int)(BinaryPrimitives.ReadUInt32BigEndian(p[o..]) >> 16); acc.H = (int)(BinaryPrimitives.ReadUInt32BigEndian(p[(o + 4)..]) >> 16);
                        acc.Id = BinaryPrimitives.ReadUInt32BigEndian(p[(v1 ? 20 : 12)..]);
                    }
                    else if (type == 0x68646C72 && n >= 12) acc.Vide = p.Slice(8, 4).SequenceEqual("vide"u8); // hdlr
                    else if (type == 0x6D646864 && n >= 24) acc.Ts = BinaryPrimitives.ReadUInt32BigEndian(p[(v1 ? 20 : 12)..]); // mdhd
                    else if (type == 0x74726578 && n >= 16 && BinaryPrimitives.ReadUInt32BigEndian(p[4..]) == acc.PendId) acc.TrexDur = BinaryPrimitives.ReadUInt32BigEndian(p[12..]); // trex
                    else if (type == 0x73747473 && n >= 16) acc.Delta = BinaryPrimitives.ReadUInt32BigEndian(p[12..]); // stts 첫 항목 delta
                    break;
            }
            pos += size;
        }
        return true;
    }

    /// <summary>최소 MP4 헤더(ftyp · moov[mvhd · trak[tkhd · mdia[mdhd · hdlr vide · minf[stbl[stts]]]]] · mdat 헤더) 기록 — 길이 반환.</summary>
    public static int WriteHeader(Span<byte> d, int width, int height, int fps, int durationSec, long totalBytes)
    {
        var n = 0;
        Box(d, ref n, "ftyp"u8, 24); Put(d, ref n, "isom"u8); U32(d, ref n, 512); Put(d, ref n, "isommp42"u8);
        var moovAt = n; Box(d, ref n, "moov"u8, 0);
        Box(d, ref n, "mvhd"u8, 8 + 100); U32(d, ref n, 0); U32(d, ref n, 0); U32(d, ref n, 0); U32(d, ref n, 1000); U32(d, ref n, (uint)(durationSec * 1000)); Zero(d, ref n, 80);
        var trakAt = n; Box(d, ref n, "trak"u8, 0);
        Box(d, ref n, "tkhd"u8, 8 + 84); U32(d, ref n, 3); Zero(d, ref n, 72); U32(d, ref n, (uint)width << 16); U32(d, ref n, (uint)height << 16);
        var mdiaAt = n; Box(d, ref n, "mdia"u8, 0);
        Box(d, ref n, "mdhd"u8, 8 + 24); U32(d, ref n, 0); U32(d, ref n, 0); U32(d, ref n, 0); U32(d, ref n, (uint)(fps * 512)); U32(d, ref n, (uint)(durationSec * fps * 512)); U32(d, ref n, 0);
        Box(d, ref n, "hdlr"u8, 8 + 24); U32(d, ref n, 0); U32(d, ref n, 0); Put(d, ref n, "vide"u8); Zero(d, ref n, 12);
        var minfAt = n; Box(d, ref n, "minf"u8, 0);
        var stblAt = n; Box(d, ref n, "stbl"u8, 0);
        Box(d, ref n, "stts"u8, 8 + 16); U32(d, ref n, 0); U32(d, ref n, 1); U32(d, ref n, (uint)(durationSec * fps)); U32(d, ref n, 512);
        Fix(d, stblAt, n); Fix(d, minfAt, n); Fix(d, mdiaAt, n); Fix(d, trakAt, n); Fix(d, moovAt, n);
        Box(d, ref n, "mdat"u8, (int)Math.Min(totalBytes - n, uint.MaxValue));
        return n;
    }

    private static void Box(Span<byte> d, ref int n, ReadOnlySpan<byte> type, int size) { U32(d, ref n, (uint)size); Put(d, ref n, type); }
    private static void Fix(Span<byte> d, int at, int end) => BinaryPrimitives.WriteUInt32BigEndian(d[at..], (uint)(end - at));
    private static void U32(Span<byte> d, ref int n, uint v) { BinaryPrimitives.WriteUInt32BigEndian(d[n..], v); n += 4; }
    private static void Put(Span<byte> d, ref int n, ReadOnlySpan<byte> s) { s.CopyTo(d[n..]); n += s.Length; }
    private static void Zero(Span<byte> d, ref int n, int k) { d.Slice(n, k).Clear(); n += k; }
}

/// <summary>
/// 시뮬레이션 전용 리믹스 영상 스트림 — 유효 MP4 헤더(1080×1920 · fps · 길이) + mdat 페이로드(v17.47 리믹스 프레임 바이트를 위치 해시로 섞은 결정적 바이트).
/// H.264 인코딩이 아니므로 실 Meta 업로드에는 사용하지 않는다(실 모드는 렌더된 MP4 파일 경로 필요).
/// </summary>
public sealed class SyntheticReelVideoStream : Stream
{
    private readonly byte[] _head = new byte[1024], _seed;
    private readonly int _headLen;
    private long _pos;

    public SyntheticReelVideoStream(long length, ReadOnlySpan<byte> seedFrame, int fps = 30, int durationSec = 15)
    {
        _seed = seedFrame.Length > 0 ? seedFrame.ToArray() : new byte[] { 0x5A };
        Length0 = length;
        _headLen = Mp4Probe.WriteHeader(_head, 1080, 1920, fps, durationSec, length);
    }

    private long Length0 { get; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => Length0;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, Length0); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> dst)
    {
        var k = (int)Math.Min(dst.Length, Length0 - _pos);
        for (var i = 0; i < k; i++)
        {
            var p = _pos + i;
            dst[i] = p < _headLen ? _head[p] : (byte)(_seed[p % _seed.Length] ^ (byte)((ulong)p * 0x9E3779B97F4A7C15UL >> 56));
        }
        _pos += k;
        return k;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length0 + offset };
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
