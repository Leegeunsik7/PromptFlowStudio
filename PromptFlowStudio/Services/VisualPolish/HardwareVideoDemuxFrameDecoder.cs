using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.VisualPolish;

public enum VideoDecoderBackend : byte { None, MediaFoundation, ManagedBaseline }

/// <summary>v17.74.0 디먹스 샘플(파일 절대 오프셋 · 크기 · 키 · 표시 시각).</summary>
public struct DemuxSample
{
    public long Offset, TimeUnits;
    public int Size, Duration;
    public bool Key;
}

/// <summary>
/// [PromptFlow Studio v17.74.0] 📦 인메모리 MP4/fMP4 H.264 디먹서(무할당 — 개방 시 1 회 표 구축 · 샘플 판독 = 재사용 버퍼).
///   ▸ 조각화(v17.62 클리퍼 · v17.49 트랜스코더: moov[mvex] + moof/traf/tfhd/trun + mdat) · 진행형(v17.69 릴스 패키저: stbl stsz/stco·co64/stsc/stts/stss) 모두 지원.
///   ▸ avcC → SPS/PPS(크기 · 크롭 판독) · AVCC 길이 접두 → Annex-B 변환(키프레임 앞 SPS/PPS 동봉) · 표시 시각 = 미디어 타임스케일.
/// </summary>
public sealed class Mp4H264Demuxer
{
    public const int MaxSamples = 8192, HeaderCap = 2 * 1024 * 1024;

    private readonly DemuxSample[] _s = new DemuxSample[MaxSamples];
    private readonly byte[] _sps = new byte[256], _pps = new byte[256];
    private byte[] _hdr = new byte[64 * 1024], _pkt = new byte[256 * 1024], _au = new byte[256 * 1024 + 1024];
    private SegmentedChunkStream? _src;
    private int _n, _spsLen, _ppsLen, _nalLen = 4;
    private uint _vTrack;

    public int Count => _n;
    public int Timescale { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int CodedWidth { get; private set; }
    public int CodedHeight { get; private set; }
    public bool Fragmented { get; private set; }
    public double DurationSec { get; private set; }
    public double Fps => DurationSec > 0 ? _n / DurationSec : 0;
    public int KeyFrames { get; private set; }
    public string? LastError { get; private set; }
    public ReadOnlySpan<byte> Sps => _sps.AsSpan(0, _spsLen);
    public ReadOnlySpan<byte> Pps => _pps.AsSpan(0, _ppsLen);
    public ref readonly DemuxSample Sample(int i) => ref _s[i];
    public long Opens { get; private set; }

    /// <summary>MP4 열기 — 비디오 샘플 표 구축(false = H.264 비디오 트랙 없음/손상).</summary>
    public bool Open(SegmentedChunkStream src)
    {
        _src = src; _n = 0; _spsLen = _ppsLen = 0; _vTrack = 0; Timescale = 0; KeyFrames = 0; Fragmented = false; LastError = null;
        Opens++;
        long pos = 0, len = src.Length;
        Span<byte> h = stackalloc byte[16];
        long[] stsz = Array.Empty<long>();
        while (pos + 8 <= len)
        {
            if (src.ReadAt(pos, h[..8]) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h); var hdr = 8;
            if (size == 1) { if (src.ReadAt(pos + 8, h.Slice(8, 8)) < 8) break; size = (long)BinaryPrimitives.ReadUInt64BigEndian(h[8..]); hdr = 16; }
            else if (size == 0) size = len - pos;
            if (size < hdr || pos + size > len) { LastError = "손상된 박스"; return false; }
            var type = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
            if (type is 0x6D6F6F76 or 0x6D6F6F66)   // moov · moof
            {
                if (size > HeaderCap) { LastError = "헤더 박스 과대"; return false; }
                if (_hdr.Length < size) _hdr = new byte[(int)size];   // 1 회 성장
                var box = _hdr.AsSpan(0, (int)size);
                if (src.ReadAt(pos, box) < size) { LastError = "판독 실패"; return false; }
                if (type == 0x6D6F6F76) { if (!ParseMoov(box[hdr..])) return false; }
                else { Fragmented = true; if (!ParseMoof(box[hdr..], pos)) return false; }
            }
            pos += size;
        }
        if (_spsLen == 0 || _n == 0) { LastError ??= "H.264 비디오 샘플 없음"; return false; }
        if (!H264SpsInfo.TryParse(Sps, out var info)) { LastError = "SPS 해석 실패"; return false; }
        Width = info.Width; Height = info.Height; CodedWidth = info.MbWidth * 16; CodedHeight = info.MbHeight * 16;
        long t = 0; for (var i = 0; i < _n; i++) { _s[i].TimeUnits = t; t += _s[i].Duration; if (_s[i].Key) KeyFrames++; }
        DurationSec = Timescale > 0 ? (double)t / Timescale : 0;
        var maxSize = 0; for (var i = 0; i < _n; i++) maxSize = Math.Max(maxSize, _s[i].Size);
        if (_pkt.Length < maxSize) { _pkt = new byte[maxSize]; _au = new byte[maxSize * 2 + 1024]; }
        return true;
    }

    private static bool Child(ReadOnlySpan<byte> c, uint type, out ReadOnlySpan<byte> body)
    {
        var p = 0;
        while (p + 8 <= c.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(c[p..]);
            if (s < 8 || p + s > c.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(c[(p + 4)..]) == type) { body = c.Slice(p + 8, s - 8); return true; }
            p += s;
        }
        body = default; return false;
    }

    private bool ParseMoov(ReadOnlySpan<byte> moov)
    {
        var p = 0;
        while (p + 8 <= moov.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(moov[p..]);
            if (s < 8 || p + s > moov.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(moov[(p + 4)..]) == 0x7472616B && _vTrack == 0) ParseTrak(moov.Slice(p + 8, s - 8));
            p += s;
        }
        return true;
    }

    private void ParseTrak(ReadOnlySpan<byte> trak)
    {
        if (!Child(trak, 0x746B6864, out var tkhd) || !Child(trak, 0x6D646961, out var mdia) || !Child(mdia, 0x68646C72, out var hdlr) || BinaryPrimitives.ReadUInt32BigEndian(hdlr[8..]) != 0x76696465) return;
        if (!Child(mdia, 0x6D646864, out var mdhd) || !Child(mdia, 0x6D696E66, out var minf) || !Child(minf, 0x7374626C, out var stbl) || !Child(stbl, 0x73747364, out var stsd)) return;
        // stsd → avc1 → avcC
        var entry = stsd[8..];
        if (entry.Length < 86 || BinaryPrimitives.ReadUInt32BigEndian(entry[4..]) != 0x61766331) return;
        if (!Child(entry[86..BinaryPrimitives.ReadInt32BigEndian(entry)], 0x61766343, out var avcc) || avcc.Length < 7) return;
        _nalLen = (avcc[4] & 3) + 1;
        var q = 6;
        if ((avcc[5] & 31) >= 1) { var l = BinaryPrimitives.ReadUInt16BigEndian(avcc[q..]); q += 2; if (l <= _sps.Length) { avcc.Slice(q, l).CopyTo(_sps); _spsLen = l; } q += l; }
        if (q < avcc.Length && avcc[q] >= 1) { q++; var l = BinaryPrimitives.ReadUInt16BigEndian(avcc[q..]); q += 2; if (l <= _pps.Length) { avcc.Slice(q, l).CopyTo(_pps); _ppsLen = l; } }
        var v1 = tkhd[0] == 1;
        _vTrack = BinaryPrimitives.ReadUInt32BigEndian(tkhd[(v1 ? 20 : 12)..]);
        Timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
        // 진행형 표(조각화 = 빈 표)
        if (!Child(stbl, 0x7374737A, out var stsz)) return;
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stsz[8..]), MaxSamples);
        if (count == 0) return;
        var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(stsz[4..]);
        for (var i = 0; i < count; i++) { _s[i] = default; _s[i].Size = fixedSize != 0 ? fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(stsz[(12 + 4 * i)..]); _s[i].Key = true; }
        if (Child(stbl, 0x73747473, out var stts))
        {
            var e = (int)BinaryPrimitives.ReadUInt32BigEndian(stts[4..]); var k = 0;
            for (var j = 0; j < e && k < count; j++) { var c = (int)BinaryPrimitives.ReadUInt32BigEndian(stts[(8 + 8 * j)..]); var d = (int)BinaryPrimitives.ReadUInt32BigEndian(stts[(12 + 8 * j)..]); for (var r = 0; r < c && k < count; r++) _s[k++].Duration = d; }
        }
        if (Child(stbl, 0x73747373, out var stss))
        {
            for (var i = 0; i < count; i++) _s[i].Key = false;
            var e = (int)BinaryPrimitives.ReadUInt32BigEndian(stss[4..]);
            for (var j = 0; j < e; j++) { var idx = (int)BinaryPrimitives.ReadUInt32BigEndian(stss[(8 + 4 * j)..]) - 1; if ((uint)idx < (uint)count) _s[idx].Key = true; }
        }
        // 청크 오프셋(stco/co64) × stsc 매핑
        var co64 = false; ReadOnlySpan<byte> stco;
        if (!Child(stbl, 0x7374636F, out stco)) { if (!Child(stbl, 0x636F3634, out stco)) return; co64 = true; }
        if (!Child(stbl, 0x73747363, out var stsc)) return;
        var chunks = (int)BinaryPrimitives.ReadUInt32BigEndian(stco[4..]);
        var se = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc[4..]);
        var si = 0;
        for (var ch = 0; ch < chunks && si < count; ch++)
        {
            long off = co64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(stco[(8 + 8 * ch)..]) : BinaryPrimitives.ReadUInt32BigEndian(stco[(8 + 4 * ch)..]);
            var per = 0;
            for (var j = 0; j < se; j++) { var first = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc[(8 + 12 * j)..]) - 1; if (first <= ch) per = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc[(12 + 12 * j)..]); }
            for (var r = 0; r < per && si < count; r++) { _s[si].Offset = off; off += _s[si].Size; si++; }
        }
        _n = si;
    }

    private bool ParseMoof(ReadOnlySpan<byte> moof, long moofPos)
    {
        var p = 0;
        while (p + 8 <= moof.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(moof[p..]);
            if (s < 8 || p + s > moof.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(moof[(p + 4)..]) == 0x74726166 && !ParseTraf(moof.Slice(p + 8, s - 8), moofPos)) return false;
            p += s;
        }
        return true;
    }

    private bool ParseTraf(ReadOnlySpan<byte> traf, long moofPos)
    {
        if (!Child(traf, 0x74666864, out var tfhd)) return true;
        var tf = BinaryPrimitives.ReadUInt32BigEndian(tfhd) & 0xFFFFFF;
        if (BinaryPrimitives.ReadUInt32BigEndian(tfhd[4..]) != _vTrack) return true;
        var o = 8; long baseOff = moofPos; uint defDur = 0, defSize = 0, defFlags = 0;
        if ((tf & 0x01) != 0) { baseOff = (long)BinaryPrimitives.ReadUInt64BigEndian(tfhd[o..]); o += 8; }
        if ((tf & 0x02) != 0) o += 4;
        if ((tf & 0x08) != 0) { defDur = BinaryPrimitives.ReadUInt32BigEndian(tfhd[o..]); o += 4; }
        if ((tf & 0x10) != 0) { defSize = BinaryPrimitives.ReadUInt32BigEndian(tfhd[o..]); o += 4; }
        if ((tf & 0x20) != 0) defFlags = BinaryPrimitives.ReadUInt32BigEndian(tfhd[o..]);
        var p = 0;
        while (p + 8 <= traf.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(traf[p..]);
            if (s < 8 || p + s > traf.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(traf[(p + 4)..]) == 0x7472756E)
            {
                var t = traf.Slice(p + 8, s - 8);
                var fl = BinaryPrimitives.ReadUInt32BigEndian(t) & 0xFFFFFF;
                var cnt = (int)BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
                var q = 8; var off = baseOff; var first = 0u; var hasFirst = false;
                if ((fl & 0x01) != 0) { off = baseOff + BinaryPrimitives.ReadInt32BigEndian(t[q..]); q += 4; }
                if ((fl & 0x04) != 0) { first = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; hasFirst = true; }
                for (var i = 0; i < cnt; i++)
                {
                    uint dur = defDur, size = defSize, flags = i == 0 && hasFirst ? first : defFlags;
                    if ((fl & 0x100) != 0) { dur = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; }
                    if ((fl & 0x200) != 0) { size = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; }
                    if ((fl & 0x400) != 0) { flags = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; }
                    if ((fl & 0x800) != 0) q += 4;
                    if (_n >= MaxSamples) { LastError = "샘플 상한"; return false; }
                    ref var d = ref _s[_n++];
                    d = default; d.Offset = off; d.Size = (int)size; d.Duration = (int)dur; d.Key = (flags & 0x00010000) == 0;
                    off += size;
                }
            }
            p += s;
        }
        return true;
    }

    /// <summary>샘플 i → Annex-B 접근 단위(키프레임 = SPS/PPS 선행) — 재사용 버퍼 스팬(다음 호출 전까지 유효 · 무할당).</summary>
    public ReadOnlySpan<byte> ReadAnnexB(int i)
    {
        if (_src is null || (uint)i >= (uint)_n) return default;
        ref readonly var s = ref _s[i];
        var pkt = _pkt.AsSpan(0, s.Size);
        if (_src.ReadAt(s.Offset, pkt) < s.Size) return default;
        var n = 0; var d = _au.AsSpan();
        if (s.Key) { n = Put(d, n, Sps); n = Put(d, n, Pps); }
        var p = 0;
        while (p + _nalLen <= pkt.Length)
        {
            var l = 0; for (var k = 0; k < _nalLen; k++) l = l << 8 | pkt[p + k];
            p += _nalLen;
            if (l <= 0 || p + l > pkt.Length) break;
            n = Put(d, n, pkt.Slice(p, l));
            p += l;
        }
        return d[..n];
    }

    private static int Put(Span<byte> d, int n, ReadOnlySpan<byte> nal)
    {
        d[n] = 0; d[n + 1] = 0; d[n + 2] = 0; d[n + 3] = 1; nal.CopyTo(d[(n + 4)..]);
        return n + 4 + nal.Length;
    }
}

/// <summary>v17.74.0 SPS 요약(Baseline/Main 공통 필드 · 크롭 반영 크기).</summary>
public readonly record struct H264SpsInfo(int Profile, int MbWidth, int MbHeight, int Width, int Height, int Log2MaxFrameNum, int PocType, int Log2MaxPocLsb)
{
    public static bool TryParse(ReadOnlySpan<byte> spsNal, out H264SpsInfo info)
    {
        info = default;
        if (spsNal.Length < 4 || (spsNal[0] & 31) != 7) return false;
        Span<byte> rbsp = stackalloc byte[Math.Min(spsNal.Length, 512)];
        var r = new H264BitReader(rbsp[..H264BitReader.Unescape(spsNal[1..], rbsp)]);
        var profile = (int)r.U(8); r.U(8); r.U(8); r.Ue();
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128) { if (r.Ue() == 3) r.U(1); r.Ue(); r.Ue(); r.U(1); if (r.U(1) != 0) return false; }
        var l2fn = (int)r.Ue() + 4;
        var poc = (int)r.Ue(); var l2poc = 0;
        if (poc == 0) l2poc = (int)r.Ue() + 4;
        else if (poc == 1) return false;
        r.Ue(); r.U(1);
        var mbw = (int)r.Ue() + 1; var mbh = (int)r.Ue() + 1;
        if (r.U(1) == 0) return false;   // frame_mbs_only 필수
        r.U(1);
        int cl = 0, cr = 0, ct = 0, cb = 0;
        if (r.U(1) != 0) { cl = (int)r.Ue(); cr = (int)r.Ue(); ct = (int)r.Ue(); cb = (int)r.Ue(); }
        if (r.Error) return false;
        info = new H264SpsInfo(profile, mbw, mbh, mbw * 16 - 2 * (cl + cr), mbh * 16 - 2 * (ct + cb), l2fn, poc, l2poc);
        return true;
    }
}

/// <summary>RBSP 비트 판독기(무할당 · Exp-Golomb).</summary>
public ref struct H264BitReader
{
    private readonly ReadOnlySpan<byte> _d;
    private int _pos;
    public bool Error;

    public H264BitReader(ReadOnlySpan<byte> rbsp) { _d = rbsp; _pos = 0; Error = false; }
    public readonly int BitPosition => _pos;
    public readonly int BitsLeft => _d.Length * 8 - _pos;

    public uint U(int n)
    {
        uint v = 0;
        for (var i = 0; i < n; i++)
        {
            if (_pos >= _d.Length * 8) { Error = true; return v; }
            v = v << 1 | (uint)((_d[_pos >> 3] >> (7 - (_pos & 7))) & 1);
            _pos++;
        }
        return v;
    }

    /// <summary>미소비 비교(n ≤ 24 · 부족분 0 채움).</summary>
    public readonly uint Peek(int n)
    {
        uint v = 0; var p = _pos;
        for (var i = 0; i < n; i++, p++) v = v << 1 | (p < _d.Length * 8 ? (uint)((_d[p >> 3] >> (7 - (p & 7))) & 1) : 0u);
        return v;
    }

    public void Skip(int n) { _pos += n; if (_pos > _d.Length * 8) Error = true; }

    public uint Ue()
    {
        var z = 0;
        while (U(1) == 0) { if (++z > 31 || Error) { Error = true; return 0; } }
        return z == 0 ? 0 : (1u << z) - 1 + U(z);
    }

    public int Se() { var k = Ue(); return (k & 1) != 0 ? (int)((k + 1) >> 1) : -(int)(k >> 1); }

    /// <summary>에뮬레이션 방지 바이트 제거(00 00 03 → 00 00) — 길이 반환.</summary>
    public static int Unescape(ReadOnlySpan<byte> s, Span<byte> d)
    {
        var n = 0; var z = 0;
        for (var i = 0; i < s.Length && n < d.Length; i++)
        {
            var b = s[i];
            if (z >= 2 && b == 3) { z = 0; continue; }
            d[n++] = b; z = b == 0 ? z + 1 : 0;
        }
        return n;
    }
}

/// <summary>v17.74.0 비디오 프레임 디코더 추상화 — Annex-B 접근 단위 → NV12(크롭 · 보폭 = Width) 프레임.</summary>
public interface IVideoFrameDecoder : IDisposable
{
    VideoDecoderBackend Backend { get; }
    /// <summary>접근 단위 1 개 투입 — 출력 가능 프레임 수(0 = 지연 · −1 = 미지원/오류).</summary>
    int Decode(ReadOnlySpan<byte> annexB, long time100ns);
    /// <summary>대기 프레임 1 개를 y/uv 에 복사(없으면 false).</summary>
    bool TryTake(Span<byte> y, Span<byte> uv);
    /// <summary>스트림 종료 — 디코더 내부 지연 프레임 배출.</summary>
    void Flush();
}

/// <summary>
/// v17.74.0 관리 코드 H.264 디코더 — v17.49 <see cref="ManagedBaselineH264Encoder"/> 출력 부분집합(Constrained Baseline · I16x16 DC 예측 · 루마/크로마 DC CAVLC · 디블로킹 비활성 · P_Skip 전 MB)
/// 전용 결정적 디코더(인코더 재구성과 비트 동일 · 무할당). 부분집합 밖(AC · 인터 예측 · CABAC 등) = −1 반환 → MF 하드웨어 경로 필요.
/// </summary>
public sealed class ManagedBaselineH264Decoder : IVideoFrameDecoder
{
    private byte[] _y = Array.Empty<byte>(), _u = Array.Empty<byte>(), _v = Array.Empty<byte>(), _rbsp = new byte[64 * 1024];
    private int _mbW, _mbH, _padW, _w, _h, _log2Fn = 4, _poc, _log2Poc, _initQp = 26, _ready;
    private bool _haveSps, _havePps, _haveRef;

    public VideoDecoderBackend Backend => VideoDecoderBackend.ManagedBaseline;
    public int Width => _w;
    public int Height => _h;
    public long Frames { get; private set; }
    public long IdrFrames { get; private set; }
    public long SkipFrames { get; private set; }
    public string? LastError { get; private set; }
    public ReadOnlySpan<byte> PaddedY => _y;

    public int Decode(ReadOnlySpan<byte> annexB, long time100ns)
    {
        var rest = annexB; var produced = 0;
        while (FragmentedMp4Muxer.NextNal(ref rest, out var nal))
        {
            if (nal.IsEmpty) continue;
            var type = nal[0] & 31;
            if (nal.Length + 64 > _rbsp.Length) _rbsp = new byte[nal.Length * 2];
            var n = H264BitReader.Unescape(nal[1..], _rbsp);
            var ok = type switch
            {
                7 => ParseSps(nal),
                8 => ParsePps(_rbsp.AsSpan(0, n)),
                5 or 1 => DecodeSlice(_rbsp.AsSpan(0, n), type == 5, (nal[0] >> 5) & 3),
                _ => true,
            };
            if (!ok) return -1;
            if (type is 5 or 1) produced++;
        }
        _ready += produced;
        return produced;
    }

    private bool ParseSps(ReadOnlySpan<byte> nal)
    {
        if (!H264SpsInfo.TryParse(nal, out var s)) { LastError = "SPS"; return false; }
        if (s.Profile != 66 && s.Profile != 77) { LastError = "프로파일 " + s.Profile; return false; }
        if (s.MbWidth != _mbW || s.MbHeight != _mbH)
        {
            _mbW = s.MbWidth; _mbH = s.MbHeight; _padW = _mbW * 16;
            _y = new byte[_padW * _mbH * 16]; _u = new byte[_padW * _mbH * 4]; _v = new byte[_padW * _mbH * 4];   // 해상도 변경 시 1 회
            _haveRef = false;
        }
        _w = s.Width; _h = s.Height; _log2Fn = s.Log2MaxFrameNum; _poc = s.PocType; _log2Poc = s.Log2MaxPocLsb;
        _haveSps = true;
        return true;
    }

    private bool ParsePps(ReadOnlySpan<byte> rbsp)
    {
        var r = new H264BitReader(rbsp);
        r.Ue(); r.Ue();
        if (r.U(1) != 0) { LastError = "CABAC 미지원"; return false; }
        r.U(1);
        if (r.Ue() != 0) { LastError = "슬라이스 그룹"; return false; }
        r.Ue(); r.Ue();
        if (r.U(1) != 0) { LastError = "가중 예측"; return false; }
        r.U(2);
        _initQp = 26 + r.Se(); r.Se();
        if (r.Se() != 0) { LastError = "chroma_qp_index_offset"; return false; }
        if (r.U(1) == 0) { LastError = "디블로킹 제어 부재"; return false; }
        r.U(1); r.U(1);
        if (r.Error) return false;
        _havePps = true;
        return true;
    }

    private bool DecodeSlice(ReadOnlySpan<byte> rbsp, bool idr, int refIdc)
    {
        if (!_haveSps || !_havePps) { LastError = "파라미터 세트 부재"; return false; }
        var r = new H264BitReader(rbsp);
        if (r.Ue() != 0) { LastError = "다중 슬라이스"; return false; }
        var st = (int)r.Ue() % 5;
        r.Ue(); r.U(_log2Fn);
        if (idr) r.Ue();
        if (_poc == 0) r.U(_log2Poc);
        if (st == 0)
        {
            if (r.U(1) != 0) r.Ue();
            if (r.U(1) != 0) { LastError = "참조 목록 수정"; return false; }
        }
        if (refIdc != 0)
        {
            if (idr) { r.U(1); r.U(1); }
            else if (r.U(1) != 0) { LastError = "적응 참조 표시"; return false; }
        }
        var qp = _initQp + r.Se();
        if (r.Ue() != 1) { LastError = "디블로킹 필터 사용 스트림"; return false; }
        if (r.Error) { LastError = "슬라이스 헤더"; return false; }
        if (st == 0)
        {
            if (!_haveRef) { LastError = "참조 프레임 없음"; return false; }
            if (r.Ue() != (uint)(_mbW * _mbH)) { LastError = "부분 P 슬라이스(인터 예측) 미지원"; return false; }
            SkipFrames++; Frames++;
            return true;   // 전 MB 스킵 = 직전 프레임 유지
        }
        if (st != 2) { LastError = "슬라이스 유형 " + st; return false; }
        Span<int> coef = stackalloc int[16];
        Span<int> dc = stackalloc int[16];
        Span<int> cdc = stackalloc int[8];
        Span<int> cpred = stackalloc int[8];
        for (var my = 0; my < _mbH; my++)
            for (var mx = 0; mx < _mbW; mx++)
            {
                var mbType = (int)r.Ue();
                if (mbType < 1 || mbType > 24) { LastError = "I16x16 이외 MB"; return false; }
                var pm = (mbType - 1) % 4; var cbpC = ((mbType - 1) / 4) % 3; var cbpL = mbType >= 13;
                if (pm != 2 || cbpL || cbpC > 1) { LastError = "DC 예측 · DC 계수 외 MB"; return false; }
                if (r.Ue() != 0) { LastError = "크로마 비 DC 예측"; return false; }
                qp += r.Se();
                if ((uint)qp > 51) { LastError = "QP"; return false; }
                coef.Clear();
                if (!Residual(ref r, coef, 16, false)) return false;
                var qpc = ManagedBaselineH264Encoder.ChromaQp(qp);
                cdc.Clear();
                if (cbpC == 1 && (!Residual(ref r, cdc[..4], 4, true) || !Residual(ref r, cdc[4..], 4, true))) return false;
                ReconLuma(mx, my, qp, coef, dc);
                ReconChroma(mx, my, qpc, cdc, cpred);
            }
        if (r.Error) { LastError = "MB 비트 부족"; return false; }
        _haveRef = true; IdrFrames++; Frames++;
        return true;
    }

    /// <summary>residual_block_cavlc 역(인코더 표 · nC 0..1 / 크로마 DC) — 지그재그 순 계수.</summary>
    private bool Residual(ref H264BitReader r, scoped Span<int> outScan, int maxCoeff, bool chromaDc)
    {
        var tc = -1; var t1 = 0;
        var lenT = chromaDc ? ManagedBaselineH264Encoder.CdcLen : ManagedBaselineH264Encoder.CtLen;
        var bitT = chromaDc ? ManagedBaselineH264Encoder.CdcBits : ManagedBaselineH264Encoder.CtBits;
        for (var k = 0; k < lenT.Length && tc < 0; k++)
        {
            var l = lenT[k];
            if (l == 0 || r.Peek(l) != bitT[k]) continue;
            r.Skip(l); tc = k >> 2; t1 = k & 3;
        }
        if (tc < 0) { LastError = "coeff_token(표 범위 밖)"; return false; }
        if (tc == 0) return true;
        Span<int> lv = stackalloc int[16];
        for (var i = 0; i < t1; i++) lv[i] = r.U(1) != 0 ? -1 : 1;
        var sl = tc > 10 && t1 < 3 ? 1 : 0;
        for (var i = t1; i < tc; i++)
        {
            var prefix = 0;
            while (r.U(1) == 0) { if (++prefix > 15 || r.Error) { LastError = "level_prefix"; return false; } }
            int code;
            if (sl == 0) code = prefix < 14 ? prefix : prefix == 14 ? 14 + (int)r.U(4) : 30 + (int)r.U(12);
            else code = prefix < 15 ? (prefix << sl) + (int)r.U(sl) : (15 << sl) + (int)r.U(12);
            if (i == t1 && t1 < 3) code += 2;
            var l = (code & 1) == 0 ? (code + 2) >> 1 : -((code + 1) >> 1);
            lv[i] = l;
            if (sl == 0) sl = 1;
            if (Math.Abs(l) > 3 << (sl - 1) && sl < 6) sl++;
        }
        var tz = 0;
        if (tc < maxCoeff)
        {
            tz = -1;
            var max = maxCoeff - tc;
            for (var z = 0; z <= max && tz < 0; z++)
            {
                int l, b;
                if (chromaDc) { var idx = (tc - 1) * 4 + z; if (idx >= ManagedBaselineH264Encoder.CTzLen.Length) break; l = ManagedBaselineH264Encoder.CTzLen[idx]; b = ManagedBaselineH264Encoder.CTzBits[idx]; }
                else { var idx = (tc - 1) * 16 + z; l = ManagedBaselineH264Encoder.TzLen[idx]; b = ManagedBaselineH264Encoder.TzBits[idx]; }
                if (l == 0 || r.Peek(l) != (uint)b) continue;
                r.Skip(l); tz = z;
            }
            if (tz < 0) { LastError = "total_zeros"; return false; }
        }
        var left = tz; var idxPos = tc + tz - 1;
        for (var i = 0; i < tc; i++)
        {
            if (idxPos < 0 || idxPos >= outScan.Length) { LastError = "계수 위치"; return false; }
            outScan[idxPos] = lv[i];
            if (i == tc - 1) break;
            var run = 0;
            if (left > 0)
            {
                var row = Math.Min(left, 7) - 1; run = -1;
                for (var q = 0; q <= Math.Min(left, 14) && run < 0; q++)
                {
                    var l = ManagedBaselineH264Encoder.RunLen[row * 15 + q];
                    if (l == 0 || r.Peek(l) != ManagedBaselineH264Encoder.RunBits[row * 15 + q]) continue;
                    r.Skip(l); run = q;
                }
                if (run < 0) { LastError = "run_before"; return false; }
                left -= run;
            }
            idxPos -= run + 1;
        }
        return !r.Error;
    }

    private void ReconLuma(int mx, int my, int qp, ReadOnlySpan<int> scan, Span<int> dc)
    {
        int x0 = mx * 16, y0 = my * 16;
        bool top = my > 0, left = mx > 0;
        int sum = 0;
        if (top) for (var i = 0; i < 16; i++) sum += _y[(y0 - 1) * _padW + x0 + i];
        if (left) for (var i = 0; i < 16; i++) sum += _y[(y0 + i) * _padW + x0 - 1];
        var pred = top && left ? (sum + 16) >> 5 : top || left ? (sum + 8) >> 4 : 128;
        dc.Clear();
        for (var k = 0; k < 16; k++) dc[ManagedBaselineH264Encoder.Zig[k]] = scan[k];
        ManagedBaselineH264Encoder.Hadamard4(dc);
        var ls = 16 * ManagedBaselineH264Encoder.V0[qp % 6];
        for (var i = 0; i < 16; i++)
            dc[i] = qp >= 36 ? (dc[i] * ls) << (qp / 6 - 6) : (dc[i] * ls + (1 << (5 - qp / 6))) >> (6 - qp / 6);
        ref var py = ref _y[0];
        for (var by = 0; by < 4; by++)
            for (var bx = 0; bx < 4; bx++)
            {
                var v4 = 0x01010101u * (uint)Math.Clamp(pred + ((dc[by * 4 + bx] + 32) >> 6), 0, 255);
                var o = (y0 + by * 4) * _padW + x0 + bx * 4;
                for (var j = 0; j < 4; j++, o += _padW) Unsafe.WriteUnaligned(ref Unsafe.Add(ref py, o), v4);   // 4 화소 = 32 비트 1 회
            }
    }

    private void ReconChroma(int mx, int my, int qpc, ReadOnlySpan<int> cdc, Span<int> cpred)
    {
        var cw = _padW / 2; int cx0 = mx * 8, cy0 = my * 8;
        bool top = my > 0, left = mx > 0;
        var cls = 16 * ManagedBaselineH264Encoder.V0[qpc % 6];
        for (var c = 0; c < 2; c++)
        {
            var rec = c == 0 ? _u : _v;
            for (var b = 0; b < 4; b++)
            {
                int bx = b & 1, by = b >> 1, st = 0, sl = 0;
                if (top) for (var i = 0; i < 4; i++) st += rec[(cy0 - 1) * cw + cx0 + bx * 4 + i];
                if (left) for (var i = 0; i < 4; i++) sl += rec[(cy0 + by * 4 + i) * cw + cx0 - 1];
                int p;
                if (bx == by) p = top && left ? (st + sl + 4) >> 3 : left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128;
                else if (bx == 1) p = top ? (st + 2) >> 2 : left ? (sl + 2) >> 2 : 128;
                else p = left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128;
                cpred[c * 4 + b] = p;
            }
            var q = cdc.Slice(c * 4, 4);
            int f0 = q[0] + q[1] + q[2] + q[3], f1 = q[0] - q[1] + q[2] - q[3], f2 = q[0] + q[1] - q[2] - q[3], f3 = q[0] - q[1] - q[2] + q[3];
            for (var b = 0; b < 4; b++)
            {
                var fv = b == 0 ? f0 : b == 1 ? f1 : b == 2 ? f2 : f3;
                var d = ((fv * cls) << (qpc / 6)) >> 5;
                var v4 = 0x01010101u * (uint)Math.Clamp(cpred[c * 4 + b] + ((d + 32) >> 6), 0, 255);
                int bx = b & 1, by = b >> 1;
                ref var pr = ref rec[0];
                var o = (cy0 + by * 4) * cw + cx0 + bx * 4;
                for (var j = 0; j < 4; j++, o += cw) Unsafe.WriteUnaligned(ref Unsafe.Add(ref pr, o), v4);
            }
        }
    }

    public bool TryTake(Span<byte> y, Span<byte> uv)
    {
        if (_ready <= 0 || !_haveRef) return false;
        _ready--;
        for (var j = 0; j < _h; j++) _y.AsSpan(j * _padW, _w).CopyTo(y[(j * _w)..]);
        var cw = _padW / 2;
        ref var pu = ref _u[0]; ref var pv = ref _v[0]; ref var po = ref uv[0];
        for (var j = 0; j < _h / 2; j++)
        {
            int so = j * cw, d = j * _w;
            for (var i = 0; i < _w / 2; i++, d += 2) { Unsafe.Add(ref po, d) = Unsafe.Add(ref pu, so + i); Unsafe.Add(ref po, d + 1) = Unsafe.Add(ref pv, so + i); }
        }
        return true;
    }

    public void Flush() { }
    public void Dispose() { }
}

/// <summary>v17.74.0 Media Foundation H.264 디코더 MFT(하드웨어 가속 · Windows) — NV12 출력 링(32 · MFT 는 DPB 지연분을 종료 드레인에 몰아 배출) · 패딩/보폭 → 크롭 복사.</summary>
public sealed class MediaFoundationVideoDecoder : IVideoFrameDecoder, IMfSampleSink
{
    public const int Ring = 32;
    private readonly MfH264Decoder _dec;
    private byte[][] _ring = new byte[Ring][];
    private readonly int _w, _h;
    private int _head, _count, _stride, _outH;

    private MediaFoundationVideoDecoder(MfH264Decoder dec, int w, int h)
    {
        _dec = dec; _w = w; _h = h;
        for (var i = 0; i < Ring; i++) _ring[i] = Array.Empty<byte>();
    }

    public static MediaFoundationVideoDecoder? TryCreate(int width, int height, int fps)
    {
        var d = MfH264Decoder.TryCreate(width, height, Math.Max(1, fps));
        return d is null ? null : new MediaFoundationVideoDecoder(d, width, height);
    }

    public VideoDecoderBackend Backend => VideoDecoderBackend.MediaFoundation;
    public long Frames { get; private set; }
    public long Overruns { get; private set; }

    public int Decode(ReadOnlySpan<byte> annexB, long time100ns)
    {
        var before = Frames;
        if (!_dec.Decode(annexB, time100ns, this)) return -1;
        return (int)(Frames - before);
    }

    public void OnSample(ReadOnlySpan<byte> data, long time100ns)
    {
        if (_stride == 0 && _dec.OutputSize(out _, out var oh, out var st)) { _stride = st; _outH = oh; }
        if (_count == _ring.Length)
        {
            // 링 가득(지연 배출 폭주) → 2 배 확장 · 선형화(프레임 손실 0 · 드문 1 회 할당)
            var grown = new byte[_ring.Length * 2][];
            for (var i = 0; i < _count; i++) grown[i] = _ring[(_head + i) % _ring.Length];
            for (var i = _count; i < grown.Length; i++) grown[i] = Array.Empty<byte>();
            _ring = grown; _head = 0; Overruns++;
        }
        var slot = (_head + _count) % _ring.Length;
        if (_ring[slot].Length < data.Length) _ring[slot] = new byte[data.Length];   // 링 슬롯 1 회 성장
        data.CopyTo(_ring[slot]);
        _count++; Frames++;
    }

    public bool TryTake(Span<byte> y, Span<byte> uv)
    {
        if (_count == 0) return false;
        var f = _ring[_head]; _head = (_head + 1) % _ring.Length; _count--;
        var st = _stride > 0 ? _stride : _w; var oh = _outH > 0 ? _outH : _h;
        for (var j = 0; j < _h; j++) f.AsSpan(j * st, _w).CopyTo(y[(j * _w)..]);
        for (var j = 0; j < _h / 2; j++) f.AsSpan(st * oh + j * st, _w).CopyTo(uv[(j * _w)..]);
        return true;
    }

    public void Flush() => _dec.Finish(this);
    public void Dispose() => _dec.Dispose();
}

/// <summary>
/// [PromptFlow Studio v17.74.0] 🎞️ 인메모리 fMP4 하드웨어 비디오 디먹서 &amp; 프레임 복원 엔진.
///   ▸ <see cref="Mp4H264Demuxer"/>(무할당 표 · Annex-B) → 디코더 선택: Windows MF H.264 MFT(하드웨어 · DXVA) 우선 → 실패/비 Windows = 관리 Baseline 디코더(v17.49 인코더 부분집합 · 비트 동일).
///   ▸ <see cref="NextFrame"/>: 표시 순서 NV12 프레임(지연 디코더 = 링 · 종료 플러시) · <see cref="ToRgba32"/>: BT.601 제한 범위 → RGBA32 백버퍼(무할당).
///   ▸ 프레임 시간 계측(디먹스 + 디코드 µs) · 키프레임 시크(<see cref="Seek"/>).
/// </summary>
public sealed class HardwareVideoDemuxFrameDecoder : IDisposable
{
    private IVideoFrameDecoder? _dec;
    private int _next, _out;

    public Mp4H264Demuxer Demuxer { get; } = new();
    public VideoDecoderBackend Backend => _dec?.Backend ?? VideoDecoderBackend.None;
    public bool PreferHardware { get; set; } = true;
    public int Width => Demuxer.Width;
    public int Height => Demuxer.Height;
    public int FrameCount => Demuxer.Count;
    public double Fps => Demuxer.Fps;
    public int Position => _out;
    public long Decoded { get; private set; }
    public long Failures { get; private set; }
    public double LastFrameUs { get; private set; }
    public double MaxFrameUs { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>클립 열기 — 디먹스 + 디코더 선택 + 첫 프레임 디코드 검증(실패 = false · 원인 <see cref="LastError"/>).</summary>
    public bool Open(SegmentedChunkStream media)
    {
        _dec?.Dispose(); _dec = null; _next = 0; _out = 0; LastError = null;
        if (!Demuxer.Open(media)) { LastError = Demuxer.LastError; return false; }
        var fps = (int)Math.Round(Math.Max(1, Demuxer.Fps));
        if (PreferHardware) _dec = MediaFoundationVideoDecoder.TryCreate(Width, Height, fps);
        if (_dec is null || !Probe())
        {
            _dec?.Dispose();
            _dec = new ManagedBaselineH264Decoder();
            _next = 0;
            if (!Probe()) { LastError = "디코드 불가: " + ((_dec as ManagedBaselineH264Decoder)?.LastError ?? "?"); _dec.Dispose(); _dec = null; return false; }
        }
        _next = 0; _out = 0;
        return true;
    }

    /// <summary>첫 키프레임 투입 가능 여부(지연 디코더는 출력 0 허용) — 디코더 상태는 재개방으로 초기화.</summary>
    private bool Probe()
    {
        var au = Demuxer.ReadAnnexB(0);
        if (au.IsEmpty || _dec!.Decode(au, 0) < 0) return false;
        // 상태 초기화(재생은 0 부터)
        if (_dec is MediaFoundationVideoDecoder mf)
        {
            mf.Dispose();
            _dec = MediaFoundationVideoDecoder.TryCreate(Width, Height, (int)Math.Round(Math.Max(1, Demuxer.Fps)));
            return _dec is not null;
        }
        _dec.Dispose();
        _dec = new ManagedBaselineH264Decoder();
        return true;
    }

    /// <summary>다음 표시 프레임 → y(Width×Height) · uv(Width×Height/2) — 끝 = false.</summary>
    public bool NextFrame(Span<byte> y, Span<byte> uv)
    {
        if (_dec is null) return false;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ok = false;
        while (!(ok = _dec.TryTake(y, uv)))
        {
            if (_next >= Demuxer.Count)
            {
                if (_next == Demuxer.Count) { _dec.Flush(); _next++; continue; }
                break;
            }
            ref readonly var s = ref Demuxer.Sample(_next);
            var t100 = Demuxer.Timescale > 0 ? s.TimeUnits * 10_000_000 / Demuxer.Timescale : _next * 333_333L;
            var au = Demuxer.ReadAnnexB(_next++);
            if (au.IsEmpty || _dec.Decode(au, t100) < 0) { Failures++; LastError = (_dec as ManagedBaselineH264Decoder)?.LastError ?? "디코드 실패"; return false; }
        }
        if (ok) { _out++; Decoded++; }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastFrameUs = us; if (us > MaxFrameUs && Decoded > 2) MaxFrameUs = us;
        return ok;
    }

    /// <summary>처음으로 되감기(디코더 재생성 · 비핫패스).</summary>
    public bool Rewind()
    {
        if (_dec is null) return false;
        var b = Backend;
        _dec.Dispose();
        _dec = b == VideoDecoderBackend.MediaFoundation ? MediaFoundationVideoDecoder.TryCreate(Width, Height, (int)Math.Round(Math.Max(1, Demuxer.Fps))) : new ManagedBaselineH264Decoder();
        _dec ??= new ManagedBaselineH264Decoder();
        _next = 0; _out = 0;
        return true;
    }

    /// <summary>프레임 i 로 이동(직전 키프레임부터 재디코드 · 비핫패스 · 버퍼 호출자 제공).</summary>
    public bool Seek(int frame, Span<byte> y, Span<byte> uv)
    {
        if (!Rewind()) return false;
        var k = 0; for (var i = Math.Min(frame, Demuxer.Count - 1); i >= 0; i--) if (Demuxer.Sample(i).Key) { k = i; break; }
        _next = k; _out = k;
        while (_out < frame) if (!NextFrame(y, uv)) return false;
        return true;
    }

    /// <summary>NV12(BT.601 제한 범위) → RGBA32(불투명) — 무할당.</summary>
    public static void ToRgba32(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, int w, int h, Span<byte> rgba)
    {
        for (var j = 0; j < h; j++)
        {
            var cr = (j >> 1) * w;
            for (var i = 0; i < w; i++)
            {
                int c = y[j * w + i] - 16, d = uv[cr + (i & ~1)] - 128, e = uv[cr + (i & ~1) + 1] - 128;
                var o = (j * w + i) * 4;
                rgba[o] = Clip((298 * c + 409 * e + 128) >> 8);
                rgba[o + 1] = Clip((298 * c - 100 * d - 208 * e + 128) >> 8);
                rgba[o + 2] = Clip((298 * c + 516 * d + 128) >> 8);
                rgba[o + 3] = 255;
            }
        }
    }

    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public void Dispose() { _dec?.Dispose(); _dec = null; }
}
