using System.Buffers.Binary;
using System.Diagnostics;
using PromptFlowStudio.Services.RetentionHeatmap;

namespace PromptFlowStudio.Services.ViralExport;

/// <summary>fMP4 적합성 결함 분류(첫 결함만 보고 · <see cref="Fmp4ConformanceReport.FaultOffset"/> = 결함 박스 오프셋).</summary>
public enum Fmp4Fault : byte
{
    None = 0,
    BoxSize,            // 길이 필드 < 8 · 부모 초과 · 자식 합 ≠ 부모 본문
    FtypNotFirst,
    Brand,              // major/compatible 에 isom · mp42 부재 · 호환 목록 4 B 비정렬
    MoovOrder,          // moov 없음 · moof 뒤 moov
    MissingBox,         // 필수 자식(mvhd/trak/tkhd/mdia/mdhd/hdlr/minf/stbl/stsd/mvex/trex/mfhd/traf/tfhd/trun) 부재
    TrackHeader,        // tkhd 길이/버전 · 비활성 · track_ID 0 · trex 미대응
    Matrix,             // 단위/90°/180°/270° 외 행렬 · w ≠ 1.0
    Aspect,             // 회전 반영 표시 창 ≠ 9:16
    SampleEntry,        // avc1/avc3 · avcC 구조 · 길이 접두 크기
    SpsConformance,     // avcC/인밴드 SPS RBSP 적합성 실패
    MdatOrder,          // moof 직후 mdat 아님
    Mfhd,               // sequence_number 0 · 비증가
    Trun,               // trun 길이 ≠ 플래그 산출 길이 · 샘플 크기 0
    DataOffset,         // 샘플 범위 ⊄ mdat 페이로드
    FirstSampleSync,    // 비디오 프래그먼트 첫 샘플 비동기/sample_depends_on ≠ 2
    SyncFlagMismatch,   // 동기 플래그 ↔ IDR NAL 불일치 · depends_on/is_non_sync 모순
    NalLength,          // AVCC 길이 접두 합 ≠ 샘플 크기 · forbidden_zero_bit · 빈 NAL
    InBandParamsInAvc1, // avc1 샘플 내 SPS/PPS(avc3 필요)
    FirstNalNotIdr,     // 동기 샘플 첫 VCL ≠ IDR
    TooManyTracks,
}

/// <summary>fMP4 컨포먼스 리포트(무할당 값 구조체).</summary>
public struct Fmp4ConformanceReport
{
    public Fmp4Fault Fault;
    public int FaultOffset, TopBoxes, Boxes, Tracks, Fragments, Trafs, Samples, VideoSamples, SyncSamples, NalUnits, SpsChecked, InBandSps, LengthSize;
    public uint MajorBrand, VideoSampleEntry;
    public int TkhdWidth, TkhdHeight, RotationDeg;
    public bool Avc3;
    public readonly bool Conformant => Fault == Fmp4Fault.None;
}

/// <summary>
/// [PromptFlow Studio v17.98.2] 📦 ISO-BMFF fMP4 컨포먼스 밸리데이터 — 모바일/데스크톱 디코더 파싱 크래시 원천 방어.
///   ▸ 박스 트리: 모든 컨테이너(moov/trak/mdia/minf/dinf/stbl/stsd/mvex/moof/traf · 샘플 엔트리 고정부 이후) 자식 길이 합 = 부모 본문(32/64 bit 길이 · size 0 = 파일 끝).
///   ▸ 헤더: ftyp 최선두 · isom/mp42 브랜드 · moov 가 첫 moof 앞 · 트랙별 trex · tkhd 활성/행렬(단위·직각 회전 · w = 1.0) · 비디오 표시 창 9:16(회전 반영).
///   ▸ 프래그먼트: moof → 직후 mdat · mfhd 순번 증가 · tfhd(base-data-offset/default-base-is-moof/기본값) · trun 길이 = 플래그 산출 길이 · data_offset + Σ크기 ⊂ mdat.
///   ▸ 동기: 비디오 traf 첫 샘플 flags(first_sample_flags → 샘플별 → tfhd → trex 순 해석) is_non_sync = 0 ∧ sample_depends_on = 2 ·
///     전 샘플 AVCC 길이 접두 순회 → 동기 ⇔ IDR · 동기 샘플 첫 VCL = IDR · avc1 인밴드 SPS/PPS 금지 · avcC/인밴드 SPS 는 <see cref="BitstreamRbspConformanceSafetyEngine"/> 검증.
///   ▸ 사전 할당 트랙 표만 사용 — <see cref="Validate"/> 무할당.
/// </summary>
public sealed class IsoBmffFragmentedMp4ConformanceValidator
{
    public const int MaxTracks = 8, MaxDepth = 12;

    private readonly uint[] _trackId = new uint[MaxTracks], _trexFlags = new uint[MaxTracks], _trexSize = new uint[MaxTracks];
    private readonly bool[] _trex = new bool[MaxTracks], _video = new bool[MaxTracks];
    private Fmp4ConformanceReport _r;
    private int _tN, _depthMax, _moofSeq;
    private bool _trakHasTkhd, _trakHasMdia, _trakVideo;
    private int _tkhdAt;

    public BitstreamRbspConformanceSafetyEngine Sps { get; } = new();
    /// <summary>비디오 트랙 표시 창 9:16 강제(숏폼 엑스포터 계약).</summary>
    public bool RequirePortrait916 { get; set; } = true;
    public ref readonly Fmp4ConformanceReport Last => ref _r;
    public long Validations { get; private set; }
    public long Rejects { get; private set; }
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }

    public static uint C(char a, char b, char c, char d) => FastShortsMuxingHardwareExporterEngine.C(a, b, c, d);

    private static readonly uint Ftyp = C('f', 't', 'y', 'p'), Moov = C('m', 'o', 'o', 'v'), Moof = C('m', 'o', 'o', 'f'), Mdat = C('m', 'd', 'a', 't'),
        Mvhd = C('m', 'v', 'h', 'd'), Trak = C('t', 'r', 'a', 'k'), Tkhd = C('t', 'k', 'h', 'd'), Mdia = C('m', 'd', 'i', 'a'), Mdhd = C('m', 'd', 'h', 'd'),
        Hdlr = C('h', 'd', 'l', 'r'), Minf = C('m', 'i', 'n', 'f'), Dinf = C('d', 'i', 'n', 'f'), Stbl = C('s', 't', 'b', 'l'), Stsd = C('s', 't', 's', 'd'),
        Mvex = C('m', 'v', 'e', 'x'), Trex = C('t', 'r', 'e', 'x'), Mfhd = C('m', 'f', 'h', 'd'), Traf = C('t', 'r', 'a', 'f'), Tfhd = C('t', 'f', 'h', 'd'),
        Trun = C('t', 'r', 'u', 'n'), Avc1 = C('a', 'v', 'c', '1'), Avc3 = C('a', 'v', 'c', '3'), AvcCc = C('a', 'v', 'c', 'C'), Mp4a = C('m', 'p', '4', 'a'),
        Tx3g = C('t', 'x', '3', 'g'), Ipcm = C('i', 'p', 'c', 'm'), Vide = C('v', 'i', 'd', 'e'),
        Isom = C('i', 's', 'o', 'm'), Mp42 = C('m', 'p', '4', '2');

    /// <summary>fMP4 바이트 전체 검증. 반환 = 첫 결함(None = 적합).</summary>
    public Fmp4Fault Validate(ReadOnlySpan<byte> mp4)
    {
        var t0 = Stopwatch.GetTimestamp();
        _r = default; _tN = 0; _depthMax = 0; _moofSeq = 0;
        Array.Clear(_trex); Array.Clear(_video);
        var f = ValidateCore(mp4);
        _r.Fault = f;
        Validations++;
        if (f != Fmp4Fault.None) Rejects++;
        LastUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        if (LastUs > MaxUs) MaxUs = LastUs;
        return f;
    }

    private Fmp4Fault Fail(Fmp4Fault f, int at) { _r.FaultOffset = at; return f; }

    /// <summary>박스 헤더 판독 — 반환 = 헤더 길이(8/16 · 0 = 오류). size 0 = 버퍼 끝까지.</summary>
    private static int Header(ReadOnlySpan<byte> s, int p, out long size, out uint type)
    {
        size = 0; type = 0;
        if (p + 8 > s.Length) return 0;
        size = BinaryPrimitives.ReadUInt32BigEndian(s[p..]);
        type = BinaryPrimitives.ReadUInt32BigEndian(s[(p + 4)..]);
        var h = 8;
        if (size == 1)
        {
            if (p + 16 > s.Length) return 0;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(s[(p + 8)..]);
            h = 16;
        }
        else if (size == 0) size = s.Length - p;
        return size < h || p + size > s.Length ? 0 : h;
    }

    private Fmp4Fault ValidateCore(ReadOnlySpan<byte> mp4)
    {
        var p = 0;
        var moov = false;
        while (p < mp4.Length)
        {
            var h = Header(mp4, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, p);
            _r.TopBoxes++; _r.Boxes++;
            var body = mp4.Slice(p + h, (int)size - h);
            if (_r.TopBoxes == 1 && type != Ftyp) return Fail(Fmp4Fault.FtypNotFirst, p);
            if (type == Ftyp)
            {
                if (_r.TopBoxes != 1) return Fail(Fmp4Fault.FtypNotFirst, p);
                var bf = CheckBrand(body);
                if (bf != Fmp4Fault.None) return Fail(bf, p);
            }
            else if (type == Moov)
            {
                if (moov || _r.Fragments > 0) return Fail(Fmp4Fault.MoovOrder, p);
                moov = true;
                var mf = CheckMoov(body, p + h);
                if (mf != Fmp4Fault.None) return mf;
            }
            else if (type == Moof)
            {
                if (!moov) return Fail(Fmp4Fault.MoovOrder, p);
                // 직후 박스 = mdat(샘플 데이터 범위 확정)
                var q = p + (int)size;
                var mh = Header(mp4, q, out var msize, out var mtype);
                if (mh == 0 || mtype != Mdat) return Fail(Fmp4Fault.MdatOrder, q);
                _r.Fragments++;
                var ff = CheckMoof(mp4, p, (int)size, h, q + mh, q + (int)msize);
                if (ff != Fmp4Fault.None) return ff;
            }
            p += (int)size;
        }
        if (!moov) return Fail(Fmp4Fault.MoovOrder, mp4.Length);
        if (_r.Fragments == 0) return Fail(Fmp4Fault.MissingBox, mp4.Length);
        return Fmp4Fault.None;
    }

    private Fmp4Fault CheckBrand(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8 || ((b.Length - 8) & 3) != 0) return Fmp4Fault.Brand;
        _r.MajorBrand = BinaryPrimitives.ReadUInt32BigEndian(b);
        var ok = _r.MajorBrand == Isom || _r.MajorBrand == Mp42;
        for (var i = 8; i + 4 <= b.Length && !ok; i += 4)
        {
            var c = BinaryPrimitives.ReadUInt32BigEndian(b[i..]);
            ok = c == Isom || c == Mp42;
        }
        return ok ? Fmp4Fault.None : Fmp4Fault.Brand;
    }

    // ══════════════════════ moov ══════════════════════

    private Fmp4Fault CheckMoov(ReadOnlySpan<byte> body, int abs)
    {
        bool mvhd = false, mvex = false;
        var p = 0;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            var inner = body.Slice(p + h, (int)size - h);
            if (type == Mvhd) mvhd = true;
            else if (type == Trak)
            {
                if (_tN == MaxTracks) return Fail(Fmp4Fault.TooManyTracks, abs + p);
                var tf = CheckTrak(inner, abs + p + h);
                if (tf != Fmp4Fault.None) return tf;
            }
            else if (type == Mvex)
            {
                mvex = true;
                var xf = CheckMvex(inner, abs + p + h);
                if (xf != Fmp4Fault.None) return xf;
            }
            p += (int)size;
        }
        if (!mvhd || _tN == 0 || !mvex) return Fail(Fmp4Fault.MissingBox, abs);
        for (var i = 0; i < _tN; i++) if (!_trex[i]) return Fail(Fmp4Fault.TrackHeader, abs);
        _r.Tracks = _tN;
        return Fmp4Fault.None;
    }

    private Fmp4Fault CheckTrak(ReadOnlySpan<byte> body, int abs)
    {
        _trakHasTkhd = _trakHasMdia = _trakVideo = false;
        _tkhdAt = -1;
        var p = 0;
        ReadOnlySpan<byte> tkhd = default;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            var inner = body.Slice(p + h, (int)size - h);
            if (type == Tkhd) { _trakHasTkhd = true; tkhd = inner; _tkhdAt = abs + p; }
            else if (type == Mdia)
            {
                _trakHasMdia = true;
                var mf = CheckMdia(inner, abs + p + h, 1);
                if (mf != Fmp4Fault.None) return mf;
            }
            p += (int)size;
        }
        if (!_trakHasTkhd || !_trakHasMdia) return Fail(Fmp4Fault.MissingBox, abs);
        return CheckTkhd(tkhd, _tkhdAt);
    }

    private Fmp4Fault CheckTkhd(ReadOnlySpan<byte> b, int at)
    {
        if (b.Length < 4) return Fail(Fmp4Fault.TrackHeader, at);
        var ver = b[0];
        var flags = BinaryPrimitives.ReadUInt32BigEndian(b) & 0xFFFFFF;
        var expect = ver == 1 ? 96 : ver == 0 ? 84 : -1;
        if (b.Length != expect || (flags & 1) == 0) return Fail(Fmp4Fault.TrackHeader, at);
        var o = 4 + (ver == 1 ? 16 : 8);
        var id = BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
        if (id == 0) return Fail(Fmp4Fault.TrackHeader, at);
        for (var i = 0; i < _tN; i++) if (_trackId[i] == id) return Fail(Fmp4Fault.TrackHeader, at);
        o += 4 + 4 + (ver == 1 ? 8 : 4) + 8 + 8;   // reserved · duration · reserved[2] · layer/alt/volume/reserved
        int a = BinaryPrimitives.ReadInt32BigEndian(b[o..]), bb = BinaryPrimitives.ReadInt32BigEndian(b[(o + 4)..]), u = BinaryPrimitives.ReadInt32BigEndian(b[(o + 8)..]);
        int c = BinaryPrimitives.ReadInt32BigEndian(b[(o + 12)..]), d = BinaryPrimitives.ReadInt32BigEndian(b[(o + 16)..]), v = BinaryPrimitives.ReadInt32BigEndian(b[(o + 20)..]);
        var w = BinaryPrimitives.ReadInt32BigEndian(b[(o + 32)..]);
        const int One = 0x00010000;
        int rot;
        if (a == One && bb == 0 && c == 0 && d == One) rot = 0;
        else if (a == 0 && bb == One && c == -One && d == 0) rot = 90;
        else if (a == -One && bb == 0 && c == 0 && d == -One) rot = 180;
        else if (a == 0 && bb == -One && c == One && d == 0) rot = 270;
        else return Fail(Fmp4Fault.Matrix, at);
        if (u != 0 || v != 0 || w != 0x40000000) return Fail(Fmp4Fault.Matrix, at);
        var tw = (int)(BinaryPrimitives.ReadUInt32BigEndian(b[(o + 36)..]) >> 16);
        var th = (int)(BinaryPrimitives.ReadUInt32BigEndian(b[(o + 40)..]) >> 16);
        if (_trakVideo)
        {
            _r.TkhdWidth = tw; _r.TkhdHeight = th; _r.RotationDeg = rot;
            int dw = rot is 90 or 270 ? th : tw, dh = rot is 90 or 270 ? tw : th;
            if (dw <= 0 || dh <= 0) return Fail(Fmp4Fault.Aspect, at);
            // 9:16 — 짝수 정렬 반올림 허용 오차 1 %
            if (RequirePortrait916 && Math.Abs(dw * 16L - dh * 9L) * 100 > dh * 9L) return Fail(Fmp4Fault.Aspect, at);
        }
        _trackId[_tN] = id; _video[_tN] = _trakVideo; _tN++;
        return Fmp4Fault.None;
    }

    private Fmp4Fault CheckMdia(ReadOnlySpan<byte> body, int abs, int depth)
    {
        bool mdhd = false, hdlr = false, minf = false;
        var p = 0;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            var inner = body.Slice(p + h, (int)size - h);
            if (type == Mdhd) mdhd = true;
            else if (type == Hdlr)
            {
                hdlr = true;
                if (inner.Length < 12) return Fail(Fmp4Fault.BoxSize, abs + p);
                _trakVideo = BinaryPrimitives.ReadUInt32BigEndian(inner[8..]) == Vide;
            }
            else if (type == Minf)
            {
                minf = true;
                var f = CheckContainer(inner, abs + p + h, depth + 1, out var stbl);
                if (f != Fmp4Fault.None) return f;
                if (!stbl) return Fail(Fmp4Fault.MissingBox, abs + p);
            }
            p += (int)size;
        }
        return mdhd && hdlr && minf ? Fmp4Fault.None : Fail(Fmp4Fault.MissingBox, abs);
    }

    /// <summary>minf/dinf/stbl/stsd 하위 트리 — 자식 길이 정합 · 샘플 엔트리 고정부 이후 자식 · avcC 검사.</summary>
    private Fmp4Fault CheckContainer(ReadOnlySpan<byte> body, int abs, int depth, out bool stbl)
    {
        stbl = false;
        if (depth > MaxDepth) return Fail(Fmp4Fault.BoxSize, abs);
        var p = 0;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            var inner = body.Slice(p + h, (int)size - h);
            var at = abs + p + h;
            Fmp4Fault f = Fmp4Fault.None;
            if (type == Dinf) f = CheckContainer(inner, at, depth + 1, out _);
            else if (type == Stbl)
            {
                stbl = true;
                f = CheckStbl(inner, at, depth + 1);
            }
            if (f != Fmp4Fault.None) return f;
            p += (int)size;
        }
        return Fmp4Fault.None;
    }

    private Fmp4Fault CheckStbl(ReadOnlySpan<byte> body, int abs, int depth)
    {
        var stsd = false;
        var p = 0;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            if (type == Stsd)
            {
                stsd = true;
                var inner = body.Slice(p + h, (int)size - h);
                if (inner.Length < 8) return Fail(Fmp4Fault.SampleEntry, abs + p);
                var n = BinaryPrimitives.ReadUInt32BigEndian(inner[4..]);
                var e = inner[8..];
                var q = 0;
                for (var i = 0u; i < n; i++)
                {
                    var eh = Header(e, q, out var es, out var et);
                    if (eh == 0) return Fail(Fmp4Fault.BoxSize, abs + p + h + 8 + q);
                    _r.Boxes++;
                    var f = CheckSampleEntry(e.Slice(q + eh, (int)es - eh), et, abs + p + h + 8 + q, depth + 1);
                    if (f != Fmp4Fault.None) return f;
                    q += (int)es;
                }
                if (q != e.Length) return Fail(Fmp4Fault.BoxSize, abs + p);
            }
            p += (int)size;
        }
        return stsd ? Fmp4Fault.None : Fail(Fmp4Fault.MissingBox, abs);
    }

    private Fmp4Fault CheckSampleEntry(ReadOnlySpan<byte> b, uint type, int at, int depth)
    {
        // 고정부 길이: VisualSampleEntry 78 · AudioSampleEntry 28 · tx3g 38 · 기타 = 자식 미검사
        var fixedLen = type == Avc1 || type == Avc3 ? 78 : type == Mp4a || type == Ipcm ? 28 : type == Tx3g ? 38 : -1;
        if (fixedLen < 0) return Fmp4Fault.None;
        if (b.Length < fixedLen) return Fail(Fmp4Fault.SampleEntry, at);
        var video = type == Avc1 || type == Avc3;
        if (video) { _r.VideoSampleEntry = type; _r.Avc3 = type == Avc3; }
        var kids = b[fixedLen..];
        var p = 0; var avcC = false;
        while (p < kids.Length)
        {
            var h = Header(kids, p, out var size, out var t);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, at + 8 + fixedLen + p);
            _r.Boxes++;
            if (video && t == AvcCc)
            {
                avcC = true;
                var f = CheckAvcC(kids.Slice(p + h, (int)size - h), at + 8 + fixedLen + p);
                if (f != Fmp4Fault.None) return f;
            }
            p += (int)size;
        }
        if (depth > _depthMax) _depthMax = depth;
        return video && !avcC ? Fail(Fmp4Fault.SampleEntry, at) : Fmp4Fault.None;
    }

    private Fmp4Fault CheckAvcC(ReadOnlySpan<byte> b, int at)
    {
        if (b.Length < 7 || b[0] != 1 || (b[4] & 0xFC) != 0xFC || (b[5] & 0xE0) != 0xE0) return Fail(Fmp4Fault.SampleEntry, at);
        _r.LengthSize = (b[4] & 3) + 1;
        if (_r.LengthSize == 3) return Fail(Fmp4Fault.SampleEntry, at);
        var nSps = b[5] & 0x1F;
        var o = 6;
        for (var i = 0; i < nSps; i++)
        {
            if (o + 2 > b.Length) return Fail(Fmp4Fault.SampleEntry, at);
            var len = BinaryPrimitives.ReadUInt16BigEndian(b[o..]); o += 2;
            if (len == 0 || o + len > b.Length) return Fail(Fmp4Fault.SampleEntry, at);
            var sps = b.Slice(o, len);
            // avcC SPS 헤더 바이트 1..3 = profile/compat/level 일치 필수
            if (i == 0 && (sps.Length < 4 || sps[1] != b[1] || sps[3] != b[3])) return Fail(Fmp4Fault.SampleEntry, at);
            if (Sps.Validate(sps) != RbspConformanceFault.None) return Fail(Fmp4Fault.SpsConformance, at);
            _r.SpsChecked++;
            o += len;
        }
        if (nSps == 0 || o >= b.Length) return Fail(Fmp4Fault.SampleEntry, at);
        var nPps = b[o++];
        for (var i = 0; i < nPps; i++)
        {
            if (o + 2 > b.Length) return Fail(Fmp4Fault.SampleEntry, at);
            var len = BinaryPrimitives.ReadUInt16BigEndian(b[o..]); o += 2;
            if (len == 0 || o + len > b.Length || (b[o] & 0x1F) != 8) return Fail(Fmp4Fault.SampleEntry, at);
            o += len;
        }
        return nPps == 0 ? Fail(Fmp4Fault.SampleEntry, at) : Fmp4Fault.None;
    }

    private Fmp4Fault CheckMvex(ReadOnlySpan<byte> body, int abs)
    {
        var p = 0;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            if (type == Trex)
            {
                var b = body.Slice(p + h, (int)size - h);
                if (b.Length != 24) return Fail(Fmp4Fault.BoxSize, abs + p);
                var k = TrackIndex(BinaryPrimitives.ReadUInt32BigEndian(b[4..]));
                if (k < 0) return Fail(Fmp4Fault.TrackHeader, abs + p);
                _trex[k] = true;
                _trexSize[k] = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
                _trexFlags[k] = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
            }
            p += (int)size;
        }
        return Fmp4Fault.None;
    }

    private int TrackIndex(uint id)
    {
        for (var i = 0; i < _tN; i++) if (_trackId[i] == id) return i;
        return -1;
    }

    // ══════════════════════ moof / traf / trun ══════════════════════

    private Fmp4Fault CheckMoof(ReadOnlySpan<byte> mp4, int moofAt, int moofSize, int h, int mdatStart, int mdatEnd)
    {
        var body = mp4.Slice(moofAt + h, moofSize - h);
        var p = 0;
        bool mfhd = false, traf = false;
        while (p < body.Length)
        {
            var bh = Header(body, p, out var size, out var type);
            if (bh == 0) return Fail(Fmp4Fault.BoxSize, moofAt + h + p);
            _r.Boxes++;
            var inner = body.Slice(p + bh, (int)size - bh);
            if (type == Mfhd)
            {
                if (traf || inner.Length != 8) return Fail(Fmp4Fault.Mfhd, moofAt + h + p);
                var seq = (int)BinaryPrimitives.ReadUInt32BigEndian(inner[4..]);
                if (seq <= _moofSeq) return Fail(Fmp4Fault.Mfhd, moofAt + h + p);
                _moofSeq = seq; mfhd = true;
            }
            else if (type == Traf)
            {
                if (!mfhd) return Fail(Fmp4Fault.MissingBox, moofAt + h + p);
                traf = true;
                _r.Trafs++;
                var f = CheckTraf(mp4, inner, moofAt + h + p + bh, moofAt, mdatStart, mdatEnd);
                if (f != Fmp4Fault.None) return f;
            }
            p += (int)size;
        }
        return mfhd && traf ? Fmp4Fault.None : Fail(Fmp4Fault.MissingBox, moofAt);
    }

    private Fmp4Fault CheckTraf(ReadOnlySpan<byte> mp4, ReadOnlySpan<byte> body, int abs, int moofAt, int mdatStart, int mdatEnd)
    {
        var p = 0;
        var k = -1;
        long baseOff = moofAt, next = -1;
        uint defSize = 0, defFlags = 0;
        var firstTrun = true;
        while (p < body.Length)
        {
            var h = Header(body, p, out var size, out var type);
            if (h == 0) return Fail(Fmp4Fault.BoxSize, abs + p);
            _r.Boxes++;
            var b = body.Slice(p + h, (int)size - h);
            if (type == Tfhd)
            {
                if (p != 0 || b.Length < 8) return Fail(Fmp4Fault.MissingBox, abs + p);
                var fl = BinaryPrimitives.ReadUInt32BigEndian(b) & 0xFFFFFF;
                k = TrackIndex(BinaryPrimitives.ReadUInt32BigEndian(b[4..]));
                if (k < 0) return Fail(Fmp4Fault.TrackHeader, abs + p);
                var need = 8 + ((fl & 1) != 0 ? 8 : 0) + ((fl & 2) != 0 ? 4 : 0) + ((fl & 8) != 0 ? 4 : 0) + ((fl & 0x10) != 0 ? 4 : 0) + ((fl & 0x20) != 0 ? 4 : 0);
                if (b.Length != need) return Fail(Fmp4Fault.BoxSize, abs + p);
                var o = 8;
                if ((fl & 1) != 0) { baseOff = (long)BinaryPrimitives.ReadUInt64BigEndian(b[o..]); o += 8; }
                if ((fl & 2) != 0) o += 4;
                if ((fl & 8) != 0) o += 4;
                defSize = (fl & 0x10) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(b[o..]) : _trexSize[k];
                if ((fl & 0x10) != 0) o += 4;
                defFlags = (fl & 0x20) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(b[o..]) : _trexFlags[k];
            }
            else if (type == Trun)
            {
                if (k < 0) return Fail(Fmp4Fault.MissingBox, abs + p);
                var f = CheckTrun(mp4, b, abs + p, k, baseOff, ref next, defSize, defFlags, firstTrun, mdatStart, mdatEnd);
                if (f != Fmp4Fault.None) return f;
                firstTrun = false;
            }
            p += (int)size;
        }
        return k >= 0 && !firstTrun ? Fmp4Fault.None : Fail(Fmp4Fault.MissingBox, abs);
    }

    private Fmp4Fault CheckTrun(ReadOnlySpan<byte> mp4, ReadOnlySpan<byte> b, int at, int k, long baseOff, ref long next, uint defSize, uint defFlags, bool firstTrun, int mdatStart, int mdatEnd)
    {
        if (b.Length < 8) return Fail(Fmp4Fault.Trun, at);
        var fl = BinaryPrimitives.ReadUInt32BigEndian(b) & 0xFFFFFF;
        var n = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
        var per = ((fl & 0x100) != 0 ? 4 : 0) + ((fl & 0x200) != 0 ? 4 : 0) + ((fl & 0x400) != 0 ? 4 : 0) + ((fl & 0x800) != 0 ? 4 : 0);
        var need = 8L + ((fl & 1) != 0 ? 4 : 0) + ((fl & 4) != 0 ? 4 : 0) + per * (long)n;
        if (b.Length != need || ((fl & 4) != 0 && (fl & 0x400) != 0)) return Fail(Fmp4Fault.Trun, at);
        var o = 8;
        long data;
        if ((fl & 1) != 0) { data = baseOff + BinaryPrimitives.ReadInt32BigEndian(b[o..]); o += 4; }
        else data = next >= 0 ? next : baseOff;
        var firstFlags = 0u; var hasFirst = (fl & 4) != 0;
        if (hasFirst) { firstFlags = BinaryPrimitives.ReadUInt32BigEndian(b[o..]); o += 4; }
        var video = _video[k];
        for (var i = 0u; i < n; i++)
        {
            if ((fl & 0x100) != 0) o += 4;
            var sz = defSize;
            if ((fl & 0x200) != 0) { sz = BinaryPrimitives.ReadUInt32BigEndian(b[o..]); o += 4; }
            var sf = i == 0 && hasFirst ? firstFlags : defFlags;
            if ((fl & 0x400) != 0) { sf = BinaryPrimitives.ReadUInt32BigEndian(b[o..]); o += 4; }
            if ((fl & 0x800) != 0) o += 4;
            if (sz == 0) return Fail(Fmp4Fault.Trun, at);
            if (data < mdatStart || data + sz > mdatEnd) return Fail(Fmp4Fault.DataOffset, at);
            _r.Samples++;
            if (video)
            {
                // sample_flags: is_leading(2) depends_on(2) is_depended_on(2) has_redundancy(2) padding(3) is_non_sync(1) degradation(16)
                var dependsOn = (sf >> 24) & 3;
                var nonSync = (sf & 0x10000) != 0;
                if (i == 0 && firstTrun && (nonSync || dependsOn != 2)) return Fail(Fmp4Fault.FirstSampleSync, at);
                if (dependsOn == 2 && nonSync) return Fail(Fmp4Fault.SyncFlagMismatch, at);
                var nf = CheckAvccSample(mp4.Slice((int)data, (int)sz), !nonSync, (int)data);
                if (nf != Fmp4Fault.None) return nf;
                _r.VideoSamples++;
                if (!nonSync) _r.SyncSamples++;
            }
            data += sz;
        }
        next = data;
        return Fmp4Fault.None;
    }

    /// <summary>AVCC 길이 접두 NAL 순회 — 길이 합 = 샘플 크기 · 동기 ⇔ IDR · avc1 인밴드 파라미터 셋 금지 · 인밴드 SPS 적합성.</summary>
    private Fmp4Fault CheckAvccSample(ReadOnlySpan<byte> s, bool sync, int at)
    {
        var ls = _r.LengthSize;
        if (ls is not (1 or 2 or 4)) return Fail(Fmp4Fault.SampleEntry, at);
        var p = 0;
        var idr = false; var firstVcl = -1;
        while (p < s.Length)
        {
            if (p + ls > s.Length) return Fail(Fmp4Fault.NalLength, at + p);
            var len = ls == 4 ? (long)BinaryPrimitives.ReadUInt32BigEndian(s[p..]) : ls == 2 ? BinaryPrimitives.ReadUInt16BigEndian(s[p..]) : s[p];
            p += ls;
            if (len == 0 || p + len > s.Length) return Fail(Fmp4Fault.NalLength, at + p);
            var nal = s.Slice(p, (int)len);
            if ((nal[0] & 0x80) != 0) return Fail(Fmp4Fault.NalLength, at + p);
            var t = nal[0] & 0x1F;
            if (t is 7 or 8)
            {
                if (!_r.Avc3) return Fail(Fmp4Fault.InBandParamsInAvc1, at + p);
                if (t == 7)
                {
                    if (Sps.Validate(nal) != RbspConformanceFault.None) return Fail(Fmp4Fault.SpsConformance, at + p);
                    _r.InBandSps++; _r.SpsChecked++;
                }
            }
            if (t is >= 1 and <= 5 && firstVcl < 0) firstVcl = t;
            if (t == 5) idr = true;
            _r.NalUnits++;
            p += (int)len;
        }
        if (firstVcl < 0) return Fail(Fmp4Fault.NalLength, at);
        if (sync && firstVcl != 5) return Fail(Fmp4Fault.FirstNalNotIdr, at);
        if (sync != idr) return Fail(Fmp4Fault.SyncFlagMismatch, at);
        return Fmp4Fault.None;
    }
}
