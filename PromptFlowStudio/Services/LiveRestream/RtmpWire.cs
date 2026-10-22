using System.Buffers.Binary;
using System.Text;

namespace PromptFlowStudio.Services.LiveRestream;

/// <summary>v17.83.0 RTMP 메시지 수신 콜백(청크 재조립 완료 시 · 무할당).</summary>
public interface IRtmpMessageHandler
{
    void OnMessage(byte type, uint streamId, uint timestamp, ReadOnlySpan<byte> payload);
}

/// <summary>
/// [PromptFlow Studio v17.83.0] RTMP 와이어 포맷 — 핸드셰이크(C0/C1/C2 · S0/S1/S2) · 청크 직렬화(fmt0 + fmt3 연속 · 확장 타임스탬프) ·
/// 프로토콜 제어 메시지 · FLV 오디오/비디오 태그 바디(AVC 디코더 설정 레코드 · AVCC 길이 프리픽스 · AAC AudioSpecificConfig). 전부 호출자 버퍼 기록(무할당).
/// </summary>
public static class RtmpWire
{
    public const byte Version = 3;
    public const int HandshakeSize = 1536, DefaultChunk = 128, OutChunk = 4096, ExtTs = 0xFFFFFF;
    public const byte TypeSetChunk = 1, TypeAbort = 2, TypeAck = 3, TypeUserCtl = 4, TypeWinAck = 5, TypePeerBw = 6, TypeAudio = 8, TypeVideo = 9, TypeData = 18, TypeCommand = 20;
    public const int CsidControl = 2, CsidCommand = 3, CsidAudio = 4, CsidStream = 5, CsidVideo = 6;

    /// <summary>C0+C1(1537 B) — 시각(BE) · 0 · 난수 1528 B.</summary>
    public static void WriteC0C1(Span<byte> dst, uint timeMs, ref uint seed)
    {
        dst[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(1), timeMs);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(5), 0);
        for (var i = 9; i < 1 + HandshakeSize; i++) dst[i] = Next(ref seed);
    }

    public static byte Next(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (byte)(s >> 24);
    }

    /// <summary>청크 메시지 최대 직렬화 크기(fmt0 12 B + 확장 4 B + 연속 청크당 5 B).</summary>
    public static int MaxChunked(int payload, int chunk) => 16 + payload + (payload / chunk + 1) * 5;

    /// <summary>메시지 1 개 → fmt0 헤더 + fmt3 연속 청크. 반환 = 기록 바이트.</summary>
    public static int WriteMessage(Span<byte> dst, int csid, uint ts, byte type, uint streamId, ReadOnlySpan<byte> payload, int chunk)
    {
        var ext = ts >= ExtTs;
        dst[0] = (byte)(csid & 0x3F);
        Write24(dst.Slice(1), ext ? ExtTs : (int)ts);
        Write24(dst.Slice(4), payload.Length);
        dst[7] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8), streamId);
        var p = 12;
        if (ext) { BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(p), ts); p += 4; }
        var off = 0;
        while (true)
        {
            var n = Math.Min(chunk, payload.Length - off);
            payload.Slice(off, n).CopyTo(dst.Slice(p));
            p += n; off += n;
            if (off >= payload.Length) break;
            dst[p++] = (byte)(0xC0 | (csid & 0x3F));
            if (ext) { BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(p), ts); p += 4; }
        }
        return p;
    }

    /// <summary>프로토콜 제어(SetChunkSize · WinAck · Ack) 4 바이트 BE 페이로드 메시지.</summary>
    public static int WriteControl(Span<byte> dst, byte type, uint value, int chunk)
    {
        Span<byte> v = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(v, value);
        return WriteMessage(dst, CsidControl, 0, type, 0, v, chunk);
    }

    public static int WritePeerBandwidth(Span<byte> dst, uint value, int chunk)
    {
        Span<byte> v = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(v, value);
        v[4] = 2; // dynamic
        return WriteMessage(dst, CsidControl, 0, TypePeerBw, 0, v, chunk);
    }

    public static int WriteUserControl(Span<byte> dst, ushort evt, uint value, int chunk)
    {
        Span<byte> v = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(v, evt);
        BinaryPrimitives.WriteUInt32BigEndian(v.Slice(2), value);
        return WriteMessage(dst, CsidControl, 0, TypeUserCtl, 0, v, chunk);
    }

    public static void Write24(Span<byte> d, int v) { d[0] = (byte)(v >> 16); d[1] = (byte)(v >> 8); d[2] = (byte)v; }
    public static int Read24(ReadOnlySpan<byte> s) => (s[0] << 16) | (s[1] << 8) | s[2];

    // ══════════════════════ FLV 태그 바디 ══════════════════════

    /// <summary>AVC 시퀀스 헤더(키프레임 · AVCPacketType 0 · AVCDecoderConfigurationRecord).</summary>
    public static int WriteAvcSequenceHeader(Span<byte> d, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        d[0] = 0x17; d[1] = 0; d[2] = d[3] = d[4] = 0;
        d[5] = 1; d[6] = sps[1]; d[7] = sps[2]; d[8] = sps[3]; d[9] = 0xFF; d[10] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(11), (ushort)sps.Length);
        sps.CopyTo(d.Slice(13));
        var p = 13 + sps.Length;
        d[p++] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(p), (ushort)pps.Length); p += 2;
        pps.CopyTo(d.Slice(p));
        return p + pps.Length;
    }

    /// <summary>AVC NALU 태그 헤더(5 B) — 이후 AVCC(4 B 길이 + NALU) 반복.</summary>
    public static void WriteAvcNaluHeader(Span<byte> d, bool key, int ctsMs)
    {
        d[0] = key ? (byte)0x17 : (byte)0x27; d[1] = 1;
        Write24(d.Slice(2), ctsMs);
    }

    /// <summary>AAC 시퀀스 헤더 — AAC-LC · 48 kHz · 2ch AudioSpecificConfig.</summary>
    public static int WriteAacSequenceHeader(Span<byte> d, int sampleRateIndex, int channels)
    {
        d[0] = 0xAF; d[1] = 0;
        var asc = (2 << 11) | (sampleRateIndex << 7) | (channels << 3);
        d[2] = (byte)(asc >> 8); d[3] = (byte)asc;
        return 4;
    }

    /// <summary>ADTS 헤더(7/9 B) 제거 → 원시 AAC 프레임.</summary>
    public static ReadOnlySpan<byte> StripAdts(ReadOnlySpan<byte> f)
    {
        if (f.Length >= 7 && f[0] == 0xFF && (f[1] & 0xF0) == 0xF0)
        {
            var h = (f[1] & 1) == 1 ? 7 : 9;
            return f.Length > h ? f.Slice(h) : ReadOnlySpan<byte>.Empty;
        }
        return f;
    }

    /// <summary>Annex-B 다음 NALU(시작 코드 00 00 01 / 00 00 00 01) — pos 진행.</summary>
    public static bool NextNal(ReadOnlySpan<byte> au, ref int pos, out ReadOnlySpan<byte> nal)
    {
        nal = default;
        var s = FindStart(au, pos, out var sc);
        if (s < 0) return false;
        var b = s + sc;
        var e = FindStart(au, b, out _);
        var end = e < 0 ? au.Length : e;
        while (end > b && au[end - 1] == 0) end--; // trailing_zero_8bits
        nal = au.Slice(b, end - b);
        pos = e < 0 ? au.Length : e;
        return nal.Length > 0 || pos < au.Length;
    }

    private static int FindStart(ReadOnlySpan<byte> a, int from, out int len)
    {
        for (var i = from; i + 2 < a.Length; i++)
        {
            if (a[i] != 0 || a[i + 1] != 0) continue;
            if (a[i + 2] == 1) { len = 3; return i; }
            if (i + 3 < a.Length && a[i + 2] == 0 && a[i + 3] == 1) { len = 4; return i; }
        }
        len = 0;
        return -1;
    }

    /// <summary>FNV-1a 64 → 순서 민감 누적(송신/수신 무결성 대조).</summary>
    public static ulong Chain(ulong acc, ReadOnlySpan<byte> payload) => (acc ^ Fnv(payload)) * 1099511628211UL;

    public static ulong Fnv(ReadOnlySpan<byte> payload)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < payload.Length; i++) { h ^= payload[i]; h *= 1099511628211UL; }
        return h;
    }
}

/// <summary>v17.83.0 AMF0 직렬화(무할당 · ref struct).</summary>
public ref struct Amf0Writer
{
    private readonly Span<byte> _d;
    public int Pos;

    public Amf0Writer(Span<byte> dst) { _d = dst; Pos = 0; }
    public ReadOnlySpan<byte> Written => _d.Slice(0, Pos);

    public void Num(double v) { _d[Pos++] = 0; BinaryPrimitives.WriteDoubleBigEndian(_d.Slice(Pos), v); Pos += 8; }
    public void Bool(bool v) { _d[Pos++] = 1; _d[Pos++] = v ? (byte)1 : (byte)0; }
    public void Null() => _d[Pos++] = 5;
    public void Str(ReadOnlySpan<byte> utf8) { _d[Pos++] = 2; Raw(utf8); }
    public void Str(ReadOnlySpan<char> s) { _d[Pos++] = 2; RawChars(s); }
    public void Key(ReadOnlySpan<byte> utf8) => Raw(utf8);
    public void ObjStart() => _d[Pos++] = 3;
    public void EcmaStart(uint count) { _d[Pos++] = 8; BinaryPrimitives.WriteUInt32BigEndian(_d.Slice(Pos), count); Pos += 4; }
    public void ObjEnd() { _d[Pos++] = 0; _d[Pos++] = 0; _d[Pos++] = 9; }
    public void Prop(ReadOnlySpan<byte> k, ReadOnlySpan<byte> v) { Key(k); Str(v); }
    public void Prop(ReadOnlySpan<byte> k, ReadOnlySpan<char> v) { Key(k); Str(v); }
    public void Prop(ReadOnlySpan<byte> k, double v) { Key(k); Num(v); }

    private void Raw(ReadOnlySpan<byte> u)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_d.Slice(Pos), (ushort)u.Length); Pos += 2;
        u.CopyTo(_d.Slice(Pos)); Pos += u.Length;
    }

    private void RawChars(ReadOnlySpan<char> s)
    {
        var n = Encoding.UTF8.GetBytes(s, _d.Slice(Pos + 2));
        BinaryPrimitives.WriteUInt16BigEndian(_d.Slice(Pos), (ushort)n);
        Pos += 2 + n;
    }
}

/// <summary>v17.83.0 AMF0 역직렬화(무할당 · 명령명/트랜잭션/스트림 id/onStatus code 추출용 최소 집합).</summary>
public ref struct Amf0Reader
{
    private readonly ReadOnlySpan<byte> _s;
    public int Pos;

    public Amf0Reader(ReadOnlySpan<byte> src) { _s = src; Pos = 0; }
    public bool End => Pos >= _s.Length;

    public bool String(out ReadOnlySpan<byte> v)
    {
        v = default;
        if (Pos + 3 > _s.Length || _s[Pos] != 2) return false;
        var n = BinaryPrimitives.ReadUInt16BigEndian(_s.Slice(Pos + 1));
        if (Pos + 3 + n > _s.Length) return false;
        v = _s.Slice(Pos + 3, n); Pos += 3 + n;
        return true;
    }

    public bool Number(out double v)
    {
        v = 0;
        if (Pos + 9 > _s.Length || _s[Pos] != 0) return false;
        v = BinaryPrimitives.ReadDoubleBigEndian(_s.Slice(Pos + 1)); Pos += 9;
        return true;
    }

    /// <summary>값 1 개 건너뜀(숫자 · 불리언 · 문자열 · 객체 · null · undefined · ECMA 배열).</summary>
    public bool Skip()
    {
        if (Pos >= _s.Length) return false;
        switch (_s[Pos])
        {
            case 0: Pos += 9; return Pos <= _s.Length;
            case 1: Pos += 2; return Pos <= _s.Length;
            case 2: return String(out _);
            case 5: case 6: Pos++; return true;
            case 3: Pos++; return SkipProps();
            case 8: Pos += 5; return Pos <= _s.Length && SkipProps();
            default: return false;
        }
    }

    private bool SkipProps()
    {
        while (Pos + 3 <= _s.Length)
        {
            var n = BinaryPrimitives.ReadUInt16BigEndian(_s.Slice(Pos));
            if (n == 0 && _s[Pos + 2] == 9) { Pos += 3; return true; }
            Pos += 2 + n;
            if (!Skip()) return false;
        }
        return false;
    }

    /// <summary>객체(0x03)에서 키의 문자열 값 탐색 — 객체 전체 소비.</summary>
    public bool FindString(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (Pos >= _s.Length || _s[Pos] != 3) return false;
        Pos++;
        var found = false;
        while (Pos + 3 <= _s.Length)
        {
            var n = BinaryPrimitives.ReadUInt16BigEndian(_s.Slice(Pos));
            if (n == 0 && _s[Pos + 2] == 9) { Pos += 3; return found; }
            if (Pos + 2 + n > _s.Length) return false;
            var k = _s.Slice(Pos + 2, n);
            Pos += 2 + n;
            if (!found && k.SequenceEqual(key) && String(out value)) { found = true; continue; }
            if (!Skip()) return false;
        }
        return false;
    }
}

/// <summary>
/// v17.83.0 RTMP 청크 스트림 재조립기 — fmt0~3 · 확장 타임스탬프 · SetChunkSize 즉시 반영 · 부분 수신 이월(무할당 · 초기 버퍼 확장만).
/// </summary>
public sealed class RtmpChunkReader
{
    private const int MaxCsid = 64;
    private readonly byte[] _in;
    private readonly byte[]?[] _msg = new byte[]?[MaxCsid];
    private readonly int[] _len = new int[MaxCsid], _got = new int[MaxCsid];
    private readonly uint[] _ts = new uint[MaxCsid], _delta = new uint[MaxCsid], _sid = new uint[MaxCsid];
    private readonly byte[] _type = new byte[MaxCsid];
    private readonly bool[] _ext = new bool[MaxCsid];
    private int _fill;

    public RtmpChunkReader(int capacity = 1 << 20) => _in = new byte[capacity];

    public int ChunkSize { get; private set; } = RtmpWire.DefaultChunk;
    public long Messages { get; private set; }
    public long Errors { get; private set; }

    public void Reset()
    {
        _fill = 0; ChunkSize = RtmpWire.DefaultChunk; Messages = Errors = 0;
        Array.Clear(_len); Array.Clear(_got); Array.Clear(_ts); Array.Clear(_delta); Array.Clear(_sid); Array.Clear(_type); Array.Clear(_ext);
    }

    /// <summary>수신 바이트 투입 → 완결 메시지마다 handler 호출. 반환 false = 프로토콜 오류.</summary>
    public bool Feed(ReadOnlySpan<byte> data, IRtmpMessageHandler handler)
    {
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, _in.Length - _fill);
            if (n == 0) { Errors++; _fill = 0; return false; }
            data.Slice(0, n).CopyTo(_in.AsSpan(_fill));
            _fill += n; data = data.Slice(n);
            var used = Parse(handler);
            if (used < 0) { Errors++; _fill = 0; return false; }
            if (used > 0) { Buffer.BlockCopy(_in, used, _in, 0, _fill - used); _fill -= used; }
        }
        return true;
    }

    private int Parse(IRtmpMessageHandler h)
    {
        var p = 0;
        while (true)
        {
            var s = _in.AsSpan(p, _fill - p);
            if (s.Length < 1) return p;
            var fmt = s[0] >> 6;
            var csid = s[0] & 0x3F;
            var bh = 1;
            if (csid == 0) { if (s.Length < 2) return p; csid = 64 + s[1]; bh = 2; }
            else if (csid == 1) { if (s.Length < 3) return p; csid = 64 + s[1] + s[2] * 256; bh = 3; }
            if (csid >= MaxCsid) return -1;
            var mh = fmt == 0 ? 11 : fmt == 1 ? 7 : fmt == 2 ? 3 : 0;
            if (s.Length < bh + mh) return p;
            var hs = s.Slice(bh);
            var newMsg = _got[csid] == 0;
            uint tsField = 0; var ext = _ext[csid];
            if (fmt <= 2)
            {
                tsField = (uint)RtmpWire.Read24(hs);
                ext = tsField == RtmpWire.ExtTs;
            }
            var hdr = bh + mh + (ext ? 4 : 0);
            if (s.Length < hdr) return p;
            var tsv = ext ? BinaryPrimitives.ReadUInt32BigEndian(s.Slice(bh + mh)) : tsField;
            if (fmt == 0) { _len[csid] = RtmpWire.Read24(hs.Slice(3)); _type[csid] = hs[6]; _sid[csid] = BinaryPrimitives.ReadUInt32LittleEndian(hs.Slice(7)); _ts[csid] = tsv; _delta[csid] = 0; }
            else if (fmt == 1) { _len[csid] = RtmpWire.Read24(hs.Slice(3)); _type[csid] = hs[6]; _delta[csid] = tsv; }
            else if (fmt == 2) _delta[csid] = tsv;
            if (newMsg && fmt == 3 && _len[csid] == 0) return -1;
            var len = _len[csid];
            var take = Math.Min(ChunkSize, len - _got[csid]);
            if (s.Length < hdr + take) return p;
            _ext[csid] = ext;
            if (newMsg && fmt != 0) _ts[csid] += _delta[csid];
            var buf = _msg[csid];
            if (buf is null || buf.Length < len) { buf = new byte[Math.Max(256, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, len)))]; if (_got[csid] > 0 && _msg[csid] is { } old) Buffer.BlockCopy(old, 0, buf, 0, _got[csid]); _msg[csid] = buf; }
            s.Slice(hdr, take).CopyTo(buf.AsSpan(_got[csid]));
            _got[csid] += take;
            p += hdr + take;
            if (_got[csid] >= len)
            {
                _got[csid] = 0;
                var payload = buf.AsSpan(0, len);
                if (_type[csid] == RtmpWire.TypeSetChunk && len >= 4) ChunkSize = (int)Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF, 1u, 1u << 24);
                Messages++;
                h.OnMessage(_type[csid], _sid[csid], _ts[csid], payload);
            }
        }
    }
}
