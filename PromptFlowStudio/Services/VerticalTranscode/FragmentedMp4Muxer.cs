using System.Buffers;
using System.Buffers.Binary;
namespace PromptFlowStudio.Services.VerticalTranscode;

/// <summary>
/// 4 MB 세그먼트 메모리 스트림 — ArrayPool 대여 세그먼트에 기록하고 v17.48 분할 업로더가 같은 4 MB 경계로 읽는다(복사 1회 · 탐색 가능).
/// 상한 초과 시 IOException. Dispose 시 세그먼트 제로화 없이 반환(미디어 데이터 — 비밀 아님).
/// </summary>
public sealed class SegmentedChunkStream : Stream
{
    public const int Segment = 4 * 1024 * 1024;
    private readonly byte[]?[] _seg;
    private long _len, _pos;
    private bool _disposed;
    private static long _live;

    /// <summary>프로세스 전체 대여 중 세그먼트 수(누수 진단).</summary>
    public static long LiveSegments => Interlocked.Read(ref _live);

    public SegmentedChunkStream(long maxBytes = 300L * 1024 * 1024) => _seg = new byte[]?[(int)((maxBytes + Segment - 1) / Segment)];

    public long MaxBytes => (long)_seg.Length * Segment;
    public int Segments { get; private set; }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    public override long Length => _len;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _len); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    /// <summary>v17.51 — 위치 독립 판독(옴니채널 분기 뷰 공유 · 무할당). 판독 바이트 수 반환.</summary>
    public int ReadAt(long pos, Span<byte> dst)
    {
        var total = 0;
        while (dst.Length > 0 && pos < _len)
        {
            var s = (int)(pos / Segment); var o = (int)(pos % Segment);
            var k = (int)Math.Min(Math.Min(dst.Length, Segment - o), _len - pos);
            _seg[s].AsSpan(o, k).CopyTo(dst);
            dst = dst[k..]; pos += k; total += k;
        }
        return total;
    }

    public override int Read(Span<byte> dst)
    {
        var total = 0;
        while (dst.Length > 0 && _pos < _len)
        {
            var s = (int)(_pos / Segment); var o = (int)(_pos % Segment);
            var k = (int)Math.Min(Math.Min(dst.Length, Segment - o), _len - _pos);
            _seg[s].AsSpan(o, k).CopyTo(dst);
            dst = dst[k..]; _pos += k; total += k;
        }
        return total;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> src)
    {
        if (_pos + src.Length > MaxBytes) throw new IOException("segmented stream capacity exceeded");
        while (src.Length > 0)
        {
            var s = (int)(_pos / Segment); var o = (int)(_pos % Segment);
            if (_seg[s] is null) { _seg[s] = ArrayPool<byte>.Shared.Rent(Segment); Segments++; Interlocked.Increment(ref _live); }
            var k = Math.Min(src.Length, Segment - o);
            src[..k].CopyTo(_seg[s].AsSpan(o));
            src = src[k..]; _pos += k;
            if (_pos > _len) _len = _pos;
        }
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
    public override void SetLength(long value) => _len = Math.Clamp(value, 0, MaxBytes);
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            for (var i = 0; i < _seg.Length; i++) if (_seg[i] is { } a) { ArrayPool<byte>.Shared.Return(a); _seg[i] = null; Interlocked.Decrement(ref _live); }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// [PromptFlow Studio v17.49.0] 🎞️ 조각화 MP4(fMP4) 먹서(<see cref="AddVideo"/> · <see cref="AddAudio"/> · <see cref="Flush"/> 무할당).
/// ftyp · moov(mvhd · trak[avc1/avcC] · trak[mp4a/esds] · mvex[mehd · trex×2]) 를 첫 키프레임 SPS/PPS 확보 시 기록하고, 키프레임마다
/// moof(mfhd · traf[tfhd · tfdt · trun]×2) + mdat 조각을 내보낸다. Annex-B 입력은 SPS/PPS/AUD/SEI 를 걸러 4 바이트 길이 접두(AVCC)로 변환.
/// 종료 시 mvhd · tkhd · mehd 길이를 역기록(탐색 가능 스트림 필요). 표 · 페이로드 버퍼는 생성 시 1회 할당.
/// </summary>
public sealed class FragmentedMp4Muxer
{
    public const int MaxFragFrames = 256, MaxFragAudio = 512, AudioRate = 48000, AacFrame = 1024;
    private readonly byte[] _vData, _aData, _box = new byte[64 * 1024], _sps = new byte[256], _pps = new byte[256];
    private readonly int[] _vSize = new int[MaxFragFrames], _aSize = new int[MaxFragAudio];
    private readonly bool[] _vKey = new bool[MaxFragFrames];
    private readonly Stream _out;
    private int _vN, _aN, _vLen, _aLen, _spsLen, _ppsLen, _seq;
    private long _vBase, _aBase, _mvhdDurAt = -1, _tkhdVDurAt = -1, _tkhdADurAt = -1, _mehdAt = -1;
    private bool _headerDone;

    public FragmentedMp4Muxer(Stream output, int width, int height, int fps, int fragPayloadBytes = 16 * 1024 * 1024)
    {
        _out = output; Width = width; Height = height; Fps = fps;
        _vData = new byte[fragPayloadBytes]; _aData = new byte[256 * 1024];
    }

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int VideoTimescale => Fps * 1000;
    public long VideoSamples { get; private set; }
    public long AudioSamples { get; private set; }
    public long Fragments { get; private set; }
    public long BytesWritten => _out.Position;
    public bool HeaderWritten => _headerDone;
    public double DurationSec => (double)VideoSamples / Fps;
    public ReadOnlySpan<byte> Sps => _sps.AsSpan(0, _spsLen);
    public ReadOnlySpan<byte> Pps => _pps.AsSpan(0, _ppsLen);
    /// <summary>AudioSpecificConfig — AAC-LC · 48 kHz · 스테레오.</summary>
    public static ReadOnlySpan<byte> Asc => new byte[] { 0x11, 0x90 };

    /// <summary>Annex-B 접근 단위 1개 추가(키프레임이면 직전 조각 송출). false = 페이로드 버퍼 부족/헤더 전 비키프레임.</summary>
    public bool AddVideo(ReadOnlySpan<byte> annexB, bool key)
    {
        if (key && _vN >= Fps) Flush(); // 키프레임 + 1 초 이상 누적 시 조각 경계
        var start = _vLen;
        var rest = annexB;
        while (NextNal(ref rest, out var nal))
        {
            if (nal.IsEmpty) continue;
            var type = nal[0] & 31;
            if (type == 7) { if (nal.Length <= _sps.Length) { nal.CopyTo(_sps); _spsLen = nal.Length; } continue; }
            if (type == 8) { if (nal.Length <= _pps.Length) { nal.CopyTo(_pps); _ppsLen = nal.Length; } continue; }
            if (type is 9 or 6) continue;
            if (_vLen + 4 + nal.Length > _vData.Length) { _vLen = start; return false; }
            BinaryPrimitives.WriteInt32BigEndian(_vData.AsSpan(_vLen), nal.Length);
            nal.CopyTo(_vData.AsSpan(_vLen + 4));
            _vLen += 4 + nal.Length;
        }
        if (_vLen == start) return false;
        if (!_headerDone)
        {
            if (!key || _spsLen == 0 || _ppsLen == 0) { _vLen = start; return false; }
            WriteHeader();
        }
        if (_vN == MaxFragFrames) Flush();
        _vSize[_vN] = _vLen - start; _vKey[_vN] = key; _vN++;
        VideoSamples++;
        return true;
    }

    /// <summary>원시 AAC 프레임(1024 샘플) 추가.</summary>
    public bool AddAudio(ReadOnlySpan<byte> raw)
    {
        if (_aN == MaxFragAudio || _aLen + raw.Length > _aData.Length) { if (_vN > 0 && _headerDone) Flush(); else return false; }
        raw.CopyTo(_aData.AsSpan(_aLen));
        _aLen += raw.Length; _aSize[_aN++] = raw.Length;
        AudioSamples++;
        return true;
    }

    /// <summary>Annex-B 다음 NAL(시작 코드 3/4 바이트).</summary>
    public static bool NextNal(ref ReadOnlySpan<byte> s, out ReadOnlySpan<byte> nal)
    {
        nal = default;
        var i = s.IndexOf("\0\0\u0001"u8);
        if (i < 0) { if (s.IsEmpty) return false; nal = s; s = default; return true; }
        s = s[(i + 3)..];
        var j = s.IndexOf("\0\0\u0001"u8);
        if (j < 0) { nal = s; s = default; }
        else { var e = j > 0 && s[j - 1] == 0 ? j - 1 : j; nal = s[..e]; s = s[j..]; }
        while (!nal.IsEmpty && nal[^1] == 0) nal = nal[..^1];
        return true;
    }

    // ══════════════════════ 헤더 ══════════════════════
    private void WriteHeader()
    {
        var n = WriteInit(_box, Sps, Pps, Width, Height, Fps, out _mvhdDurAt, out _tkhdVDurAt, out _tkhdADurAt, out _mehdAt);
        var basePos = _out.Position;
        _mvhdDurAt += basePos; _tkhdVDurAt += basePos; _tkhdADurAt += basePos; _mehdAt += basePos;
        _out.Write(_box.AsSpan(0, n));
        _headerDone = true;
    }

    /// <summary>ftyp + moov(avc1/avcC · mp4a/esds · mvex) 초기화 세그먼트 기록(무할당) — 길이 필드 위치(상대) 반환. v17.62 무손실 리먹스 공용.</summary>
    public static int WriteInit(Span<byte> b, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps, int width, int height, int fps, out long mvhdDurAt, out long tkhdVDurAt, out long tkhdADurAt, out long mehdAt)
    {
        var n = 0;
        var ftyp = Open(b, ref n, "ftyp"u8); Put(b, ref n, "isom"u8); U32(b, ref n, 512); Put(b, ref n, "isomiso6avc1mp41"u8); Close(b, ftyp, n);
        var moov = Open(b, ref n, "moov"u8);
        var mvhd = Open(b, ref n, "mvhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 1000); mvhdDurAt = n; U32(b, ref n, 0);
        U32(b, ref n, 0x00010000); U16(b, ref n, 0x0100); Zero(b, ref n, 10); Matrix(b, ref n); Zero(b, ref n, 24); U32(b, ref n, 3); Close(b, mvhd, n);
        // 비디오 트랙
        var trak = Open(b, ref n, "trak"u8);
        tkhdVDurAt = Tkhd(b, ref n, 1, 0, width, height);
        var mdia = Open(b, ref n, "mdia"u8);
        Mdhd(b, ref n, (uint)(fps * 1000));
        Hdlr(b, ref n, "vide"u8, "VideoHandler"u8);
        var minf = Open(b, ref n, "minf"u8);
        var vmhd = Open(b, ref n, "vmhd"u8); U32(b, ref n, 1); Zero(b, ref n, 8); Close(b, vmhd, n);
        Dinf(b, ref n);
        var stbl = Open(b, ref n, "stbl"u8);
        var stsd = Open(b, ref n, "stsd"u8); U32(b, ref n, 0); U32(b, ref n, 1);
        var avc1 = Open(b, ref n, "avc1"u8); Zero(b, ref n, 6); U16(b, ref n, 1); Zero(b, ref n, 16); U16(b, ref n, (ushort)width); U16(b, ref n, (ushort)height);
        U32(b, ref n, 0x00480000); U32(b, ref n, 0x00480000); U32(b, ref n, 0); U16(b, ref n, 1); Zero(b, ref n, 32); U16(b, ref n, 0x18); U16(b, ref n, 0xFFFF);
        var avcc = Open(b, ref n, "avcC"u8);
        b[n++] = 1; b[n++] = sps[1]; b[n++] = sps[2]; b[n++] = sps[3]; b[n++] = 0xFF; b[n++] = 0xE1;
        U16(b, ref n, (ushort)sps.Length); Put(b, ref n, sps); b[n++] = 1; U16(b, ref n, (ushort)pps.Length); Put(b, ref n, pps);
        Close(b, avcc, n); Close(b, avc1, n); Close(b, stsd, n);
        EmptyTables(b, ref n);
        Close(b, stbl, n); Close(b, minf, n); Close(b, mdia, n); Close(b, trak, n);
        // 오디오 트랙
        trak = Open(b, ref n, "trak"u8);
        tkhdADurAt = Tkhd(b, ref n, 2, 0x0100, 0, 0);
        mdia = Open(b, ref n, "mdia"u8);
        Mdhd(b, ref n, AudioRate);
        Hdlr(b, ref n, "soun"u8, "SoundHandler"u8);
        minf = Open(b, ref n, "minf"u8);
        var smhd = Open(b, ref n, "smhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, smhd, n);
        Dinf(b, ref n);
        stbl = Open(b, ref n, "stbl"u8);
        stsd = Open(b, ref n, "stsd"u8); U32(b, ref n, 0); U32(b, ref n, 1);
        var mp4a = Open(b, ref n, "mp4a"u8); Zero(b, ref n, 6); U16(b, ref n, 1); Zero(b, ref n, 8); U16(b, ref n, 2); U16(b, ref n, 16); U32(b, ref n, 0); U32(b, ref n, (uint)AudioRate << 16);
        var esds = Open(b, ref n, "esds"u8); U32(b, ref n, 0);
        b[n++] = 3; b[n++] = 25; U16(b, ref n, 2); b[n++] = 0;
        b[n++] = 4; b[n++] = 17; b[n++] = 0x40; b[n++] = 0x15; b[n++] = 0; b[n++] = 0x18; b[n++] = 0; U32(b, ref n, 160000); U32(b, ref n, 128000);
        b[n++] = 5; b[n++] = 2; Put(b, ref n, Asc);
        b[n++] = 6; b[n++] = 1; b[n++] = 2;
        Close(b, esds, n); Close(b, mp4a, n); Close(b, stsd, n);
        EmptyTables(b, ref n);
        Close(b, stbl, n); Close(b, minf, n); Close(b, mdia, n); Close(b, trak, n);
        var mvex = Open(b, ref n, "mvex"u8);
        var mehd = Open(b, ref n, "mehd"u8); U32(b, ref n, 0); mehdAt = n; U32(b, ref n, 0); Close(b, mehd, n);
        Trex(b, ref n, 1, 1000); Trex(b, ref n, 2, AacFrame);
        Close(b, mvex, n); Close(b, moov, n);
        return n;
    }

    private static int Tkhd(Span<byte> b, ref int n, uint id, ushort volume, int w, int h)
    {
        var t = Open(b, ref n, "tkhd"u8); U32(b, ref n, 3); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, id); U32(b, ref n, 0);
        var durAt = n; U32(b, ref n, 0); Zero(b, ref n, 8); U16(b, ref n, 0); U16(b, ref n, 0); U16(b, ref n, volume); U16(b, ref n, 0);
        Matrix(b, ref n); U32(b, ref n, (uint)w << 16); U32(b, ref n, (uint)h << 16); Close(b, t, n);
        return durAt;
    }
    private static void Mdhd(Span<byte> b, ref int n, uint ts) { var m = Open(b, ref n, "mdhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, ts); U32(b, ref n, 0); U16(b, ref n, 0x55C4); U16(b, ref n, 0); Close(b, m, n); }
    private static void Hdlr(Span<byte> b, ref int n, ReadOnlySpan<byte> type, ReadOnlySpan<byte> name) { var h = Open(b, ref n, "hdlr"u8); U32(b, ref n, 0); U32(b, ref n, 0); Put(b, ref n, type); Zero(b, ref n, 12); Put(b, ref n, name); b[n++] = 0; Close(b, h, n); }
    private static void Dinf(Span<byte> b, ref int n) { var d = Open(b, ref n, "dinf"u8); var r = Open(b, ref n, "dref"u8); U32(b, ref n, 0); U32(b, ref n, 1); var u = Open(b, ref n, "url "u8); U32(b, ref n, 1); Close(b, u, n); Close(b, r, n); Close(b, d, n); }
    private static void EmptyTables(Span<byte> b, ref int n)
    {
        var a = Open(b, ref n, "stts"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, a, n);
        a = Open(b, ref n, "stsc"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, a, n);
        a = Open(b, ref n, "stsz"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, a, n);
        a = Open(b, ref n, "stco"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, a, n);
    }
    private static void Trex(Span<byte> b, ref int n, uint id, uint dur) { var t = Open(b, ref n, "trex"u8); U32(b, ref n, 0); U32(b, ref n, id); U32(b, ref n, 1); U32(b, ref n, dur); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, t, n); }
    private static void Matrix(Span<byte> b, ref int n) { U32(b, ref n, 0x00010000); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0x00010000); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0x40000000); }

    // ══════════════════════ 조각 ══════════════════════
    /// <summary>대기 샘플을 moof + mdat 조각으로 송출(무할당).</summary>
    public void Flush()
    {
        if (!_headerDone || _vN == 0 && _aN == 0) return;
        var b = _box.AsSpan(); var n = 0;
        var moof = Open(b, ref n, "moof"u8);
        var mfhd = Open(b, ref n, "mfhd"u8); U32(b, ref n, 0); U32(b, ref n, (uint)++_seq); Close(b, mfhd, n);
        var vOffAt = -1; var aOffAt = -1;
        if (_vN > 0)
        {
            var traf = Open(b, ref n, "traf"u8);
            var tfhd = Open(b, ref n, "tfhd"u8); U32(b, ref n, 0x020000); U32(b, ref n, 1); Close(b, tfhd, n);
            var tfdt = Open(b, ref n, "tfdt"u8); U32(b, ref n, 0x01000000); U64(b, ref n, (ulong)_vBase); Close(b, tfdt, n);
            var trun = Open(b, ref n, "trun"u8); U32(b, ref n, 0x000701); U32(b, ref n, (uint)_vN); vOffAt = n; U32(b, ref n, 0);
            for (var i = 0; i < _vN; i++) { U32(b, ref n, 1000); U32(b, ref n, (uint)_vSize[i]); U32(b, ref n, _vKey[i] ? 0x02000000u : 0x01010000u); }
            Close(b, trun, n); Close(b, traf, n);
        }
        if (_aN > 0)
        {
            var traf = Open(b, ref n, "traf"u8);
            var tfhd = Open(b, ref n, "tfhd"u8); U32(b, ref n, 0x020000); U32(b, ref n, 2); Close(b, tfhd, n);
            var tfdt = Open(b, ref n, "tfdt"u8); U32(b, ref n, 0x01000000); U64(b, ref n, (ulong)_aBase); Close(b, tfdt, n);
            var trun = Open(b, ref n, "trun"u8); U32(b, ref n, 0x000301); U32(b, ref n, (uint)_aN); aOffAt = n; U32(b, ref n, 0);
            for (var i = 0; i < _aN; i++) { U32(b, ref n, AacFrame); U32(b, ref n, (uint)_aSize[i]); }
            Close(b, trun, n); Close(b, traf, n);
        }
        Close(b, moof, n);
        if (vOffAt >= 0) BinaryPrimitives.WriteInt32BigEndian(b[vOffAt..], n + 8);
        if (aOffAt >= 0) BinaryPrimitives.WriteInt32BigEndian(b[aOffAt..], n + 8 + _vLen);
        BinaryPrimitives.WriteUInt32BigEndian(b[n..], (uint)(8 + _vLen + _aLen)); "mdat"u8.CopyTo(b[(n + 4)..]);
        _out.Write(b[..(n + 8)]);
        _out.Write(_vData.AsSpan(0, _vLen));
        _out.Write(_aData.AsSpan(0, _aLen));
        _vBase += 1000L * _vN; _aBase += (long)AacFrame * _aN;
        _vN = 0; _aN = 0; _vLen = 0; _aLen = 0;
        Fragments++;
    }

    /// <summary>잔여 조각 송출 · 길이 필드 역기록.</summary>
    public void Finish()
    {
        Flush();
        if (!_headerDone) return;
        var end = _out.Position;
        Span<byte> v = stackalloc byte[4];
        var ms = (uint)Math.Round(DurationSec * 1000);
        BinaryPrimitives.WriteUInt32BigEndian(v, ms);
        _out.Position = _mvhdDurAt; _out.Write(v);
        _out.Position = _tkhdVDurAt; _out.Write(v);
        _out.Position = _mehdAt; _out.Write(v);
        BinaryPrimitives.WriteUInt32BigEndian(v, (uint)Math.Round(AudioSamples * (double)AacFrame / AudioRate * 1000));
        _out.Position = _tkhdADurAt; _out.Write(v);
        _out.Position = end;
    }

    internal static int Open(Span<byte> b, ref int n, ReadOnlySpan<byte> type) { var at = n; n += 4; type.CopyTo(b[n..]); n += 4; return at; }
    internal static void Close(Span<byte> b, int at, int n) => BinaryPrimitives.WriteUInt32BigEndian(b[at..], (uint)(n - at));
    internal static void Put(Span<byte> b, ref int n, ReadOnlySpan<byte> s) { s.CopyTo(b[n..]); n += s.Length; }
    private static void U16(Span<byte> b, ref int n, ushort v) { BinaryPrimitives.WriteUInt16BigEndian(b[n..], v); n += 2; }
    internal static void U32(Span<byte> b, ref int n, uint v) { BinaryPrimitives.WriteUInt32BigEndian(b[n..], v); n += 4; }
    internal static void U64(Span<byte> b, ref int n, ulong v) { BinaryPrimitives.WriteUInt64BigEndian(b[n..], v); n += 8; }
    private static void Zero(Span<byte> b, ref int n, int k) { b.Slice(n, k).Clear(); n += k; }

    /// <summary>무음 AAC-LC 원시 프레임(CPE · max_sfb 0) — 관리 코드 폴백(오디오 인코더 부재) 전용. 길이 반환.</summary>
    public static int WriteSilentAacFrame(Span<byte> dst)
    {
        // CPE(id 1 · tag 0) · common_window 0 · ICS×2(global_gain 100 · ics_info: ONLY_LONG · max_sfb 0 · 예측 0 · pulse/tns/gain 0) · END(7) · 바이트 정렬
        ulong acc = 0; var bits = 0; var n = 0;
        void W(uint v, int k, Span<byte> d, ref ulong a, ref int nb, ref int nn) { a = a << k | v; nb += k; while (nb >= 8) { nb -= 8; d[nn++] = (byte)(a >> nb); } }
        W(1, 3, dst, ref acc, ref bits, ref n); W(0, 4, dst, ref acc, ref bits, ref n); W(0, 1, dst, ref acc, ref bits, ref n);
        for (var c = 0; c < 2; c++)
        {
            W(100, 8, dst, ref acc, ref bits, ref n);
            W(0, 1, dst, ref acc, ref bits, ref n); W(0, 2, dst, ref acc, ref bits, ref n); W(0, 1, dst, ref acc, ref bits, ref n); W(0, 6, dst, ref acc, ref bits, ref n); W(0, 1, dst, ref acc, ref bits, ref n);
            W(0, 1, dst, ref acc, ref bits, ref n); W(0, 1, dst, ref acc, ref bits, ref n); W(0, 1, dst, ref acc, ref bits, ref n);
        }
        W(7, 3, dst, ref acc, ref bits, ref n);
        if (bits > 0) W(0, 8 - bits, dst, ref acc, ref bits, ref n);
        return n;
    }
}
