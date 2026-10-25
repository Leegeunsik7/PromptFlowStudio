using System.Buffers.Binary;
using PromptFlowStudio.Services.LiveClipper;
using PromptFlowStudio.Services.RetentionHeatmap;
using PromptFlowStudio.Services.VisualPolish;

namespace PromptFlowStudio.Services.Ui;

public enum LocalVideoImportStatus : byte { Idle, Injected, Busy, NotMp4, TooLarge, NoVideoTrack, UnsupportedCodec, Truncated, Failed }

/// <summary>v18.0.3 MP4 샘플 표 항목(파일 절대 오프셋 · 디코딩 시각 · 합성 오프셋 — 미디어 타임스케일).</summary>
public struct Mp4TableSample
{
    public long Offset, Dts;
    public int Size, Cts;
    public bool Key;
}

/// <summary>
/// [PromptFlow Studio v18.0.3] 📁 로컬 MP4 → 숏폼 팩토리 소스 링 주입기 — 120 s 리허설 캡처 대신 실제 파일의 H.264 NALU · AAC 프레임을
/// <see cref="RollingCircularVideoBufferEngine"/> 에 디코딩 순서로 적재한다(외부 FFmpeg 없음 · 순수 C# ISO-BMFF 파서).
///   ▸ 파싱(비핫패스 · 1 회): moov → trak → mdia(mdhd 타임스케일 · hdlr vide/soun) → minf → stbl(stsd avc1/avc3·avcC · mp4a·esds ASC ·
///     stts · ctts · stss · stsz · stsc · stco/co64) → 트랙별 샘플 표(고정 용량 배열 재사용).
///   ▸ 주입(무할당 핫패스): 비디오/오디오 DTS 병합 순회 · 파일 → 재사용 패킷 버퍼 판독 · AVCC 길이 접두 → Annex-B(AUD + 키프레임 SPS/PPS 동봉) ·
///     원시 AAC → ADTS 7 바이트 헤더 · 링 PTS = (DTS + CTS − 최소 합성 시각) µs(첫 표시 프레임 = 0) · 링 창(120 s) 초과 샘플 미주입.
///   ▸ 링 용량 = 창 구간 실측 비트레이트 × 1.25 로 재구성 · PCM = AAC 프레임 크기 에너지 엔벨로프 프록시(무손실 AAC 디코딩 아님 — ③ 데드에어 계획 전용).
/// </summary>
public sealed class LocalVideoFileRingBufferSourceInjector
{
    public const int MaxSamples = 16_384, MaxMoovBytes = 32 << 20, Fs = ShortsFactoryRetentionRealSliceBinder.Fs, AacFrameSamples = 1024;
    public const long MaxFileBytes = 2L << 30, WindowUs = RollingCircularVideoBufferEngine.WindowUs;

    private sealed class Track
    {
        public readonly Mp4TableSample[] S = new Mp4TableSample[MaxSamples];
        public int N, Timescale;
        public long DurationUnits;
        public bool Truncated;
        public void Clear() { N = 0; Timescale = 0; DurationUnits = 0; Truncated = false; }
        public long Us(long units) => Timescale > 0 ? units * 1_000_000 / Timescale : 0;
    }

    private readonly Track _v = new(), _a = new();
    private readonly byte[] _sps = new byte[256], _pps = new byte[256], _adts = new byte[7];
    private readonly int[] _sizeScratch = new int[MaxSamples];
    private byte[] _moov = new byte[64 * 1024], _pkt = new byte[256 * 1024], _au = new byte[256 * 1024 + 1024];
    private short[] _pcm = Array.Empty<short>();
    private int _spsLen, _ppsLen, _nalLen = 4, _aot, _sfi, _ch;
    private long _ptsShiftUs;
    private int _maxAu;
    private bool _haveVideo, _haveAudio, _avc;

    public LocalVideoImportStatus Status { get; private set; }
    public string? LastError { get; private set; }
    public string? FileName { get; private set; }
    public long FileBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DurationMs { get; private set; }
    public double Fps { get; private set; }
    public int VideoSamples => _v.N;
    public int AudioSamples => _a.N;
    public int AudioRate { get; private set; }
    public int AudioChannels => _ch;
    public bool HasAudio => _haveAudio;
    public ReadOnlySpan<byte> Sps => _sps.AsSpan(0, _spsLen);
    public ReadOnlySpan<byte> Pps => _pps.AsSpan(0, _ppsLen);
    public ref readonly Mp4TableSample VideoSample(int i) => ref _v.S[i];
    public ref readonly Mp4TableSample AudioSample(int i) => ref _a.S[i];
    /// <summary>마지막 주입 결과(창 내 샘플 · 바이트 · 링 kbps · 키프레임).</summary>
    public int InjectedVideo { get; private set; }
    public int InjectedAudio { get; private set; }
    public int InjectedKeyframes { get; private set; }
    public long InjectedBytes { get; private set; }
    public int RingKbps { get; private set; }
    /// <summary>링 거부 · DTS 비단조 · NAL 길이 오류 수(0 = 유효 타임스탬프 시퀀스).</summary>
    public int Faults { get; private set; }
    public long FirstPtsUs { get; private set; }
    public long LastPtsUs { get; private set; }
    public double LastParseMs { get; private set; }
    public double LastInjectMs { get; private set; }
    public long Imports { get; private set; }
    /// <summary>창 구간 PCM 프록시(48 kHz 모노 · 0 = 링 원점) — <see cref="Import"/> 성공 시 갱신.</summary>
    public short[] EnvelopePcm => _pcm;

    /// <summary>바이트 배열 원본 — 내부 MemoryStream 래핑 후 <see cref="Import(Stream, RollingCircularVideoBufferEngine, string?)"/>.</summary>
    public LocalVideoImportStatus Import(byte[] file, RollingCircularVideoBufferEngine ring, string? name = null)
    {
        using var ms = new MemoryStream(file, false);
        return Import(ms, ring, name);
    }

    /// <summary>탐색 가능 스트림 → 파싱 → 링 재구성 · 주입 → PCM 프록시. 실패 시 링은 비워진 상태(리허설 재적재 대상).</summary>
    public LocalVideoImportStatus Import(Stream file, RollingCircularVideoBufferEngine ring, string? name = null)
    {
        FileName = name;
        try
        {
            var st = Parse(file);
            if (st != LocalVideoImportStatus.Injected) return Fail(st, LastError ?? st.ToString());
            st = Inject(file, ring);
            if (st != LocalVideoImportStatus.Injected) return Fail(st, LastError ?? st.ToString());
            BuildEnvelopePcm();
            Imports++;
            return Status = LocalVideoImportStatus.Injected;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            ring.Reset();
            return Fail(LocalVideoImportStatus.Failed, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private LocalVideoImportStatus Fail(LocalVideoImportStatus st, string why)
    {
        LastError = why;
        return Status = st;
    }

    // ══════════════════════ 파싱(비핫패스) ══════════════════════

    /// <summary>최상위 박스 순회 → moov 적재 · 트랙 표 구축 · 메타(해상도 · 길이 · fps).</summary>
    public LocalVideoImportStatus Parse(Stream f)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastError = null;
        _v.Clear(); _a.Clear();
        _spsLen = _ppsLen = 0; _nalLen = 4; _haveVideo = _haveAudio = _avc = false;
        Width = Height = DurationMs = AudioRate = _ch = 0; Fps = 0;
        if (!f.CanSeek || !f.CanRead) return Fail(LocalVideoImportStatus.Failed, "탐색 불가 스트림");
        FileBytes = f.Length;
        if (FileBytes > MaxFileBytes) return Fail(LocalVideoImportStatus.TooLarge, "파일 상한 초과");
        Span<byte> h = stackalloc byte[16];
        long pos = 0, moovAt = -1, moovLen = 0;
        var ftyp = false;
        while (pos + 8 <= FileBytes)
        {
            f.Position = pos;
            f.ReadExactly(h[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
            var hdr = 8;
            if (size == 1) { f.ReadExactly(h[8..16]); size = (long)BinaryPrimitives.ReadUInt64BigEndian(h[8..]); hdr = 16; }
            else if (size == 0) size = FileBytes - pos;
            if (size < hdr || pos + size > FileBytes) break;
            if (type == 0x66747970) ftyp = true;                       // ftyp
            else if (type == 0x6D6F6F76) { moovAt = pos + hdr; moovLen = size - hdr; }   // moov
            pos += size;
        }
        if (!ftyp && moovAt < 0) return Fail(LocalVideoImportStatus.NotMp4, "ISO-BMFF 아님(ftyp/moov 없음)");
        if (moovAt < 0) return Fail(LocalVideoImportStatus.NotMp4, "moov 없음(조각화 전용 · 손상)");
        if (moovLen > MaxMoovBytes) return Fail(LocalVideoImportStatus.TooLarge, "moov 상한 초과");
        if (_moov.Length < moovLen) _moov = new byte[moovLen];
        f.Position = moovAt;
        f.ReadExactly(_moov.AsSpan(0, (int)moovLen));
        var moov = _moov.AsSpan(0, (int)moovLen);

        var at = 0;
        while (Next(moov, ref at, out var type, out var trak))
            if (type == 0x7472616B) ParseTrak(trak);                    // trak
        if (!_haveVideo || _v.N == 0) return Fail(LocalVideoImportStatus.NoVideoTrack, "비디오 트랙 없음");
        if (!_avc || _spsLen == 0 || _ppsLen == 0) return Fail(LocalVideoImportStatus.UnsupportedCodec, "H.264(avc1/avc3 · avcC) 아님");
        if (_v.Truncated || _a.Truncated) return Fail(LocalVideoImportStatus.Truncated, "샘플 표 손상/상한 초과");

        if (TryReadSpsSize(Sps, out var sw, out var sh, out _, out _)) { Width = sw; Height = sh; }
        var durUs = _v.Us(_v.DurationUnits > 0 ? _v.DurationUnits : _v.S[_v.N - 1].Dts);
        DurationMs = (int)Math.Min(int.MaxValue, durUs / 1000);
        Fps = durUs > 0 ? _v.N * 1e6 / durUs : 0;
        // 첫 표시 프레임 = 0(합성 오프셋 최소값 보정 · 편집 목록 미적용)
        var minPts = long.MaxValue;
        for (var i = 0; i < _v.N; i++) minPts = Math.Min(minPts, _v.S[i].Dts + _v.S[i].Cts);
        _ptsShiftUs = _v.Us(minPts);
        var maxSample = 0;
        for (var i = 0; i < _v.N; i++) maxSample = Math.Max(maxSample, _v.S[i].Size);
        for (var i = 0; i < _a.N; i++) maxSample = Math.Max(maxSample, _a.S[i].Size);
        if (_pkt.Length < maxSample) _pkt = new byte[maxSample];
        var auCap = maxSample * 2 + _spsLen + _ppsLen + 64;
        _maxAu = auCap;
        if (_au.Length < auCap) _au = new byte[auCap];
        LastParseMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return LocalVideoImportStatus.Injected;
    }

    /// <summary>SPS NAL(헤더 포함 · 시작 코드 없음) → 표시 크기 · MB 격자. 기본 판독기 실패(스케일링 행렬 · 필드 부호화) 시 크롭 재작성기 전체 파서.</summary>
    public static bool TryReadSpsSize(ReadOnlySpan<byte> spsNal, out int width, out int height, out int mbW, out int mbH)
    {
        if (H264SpsInfo.TryParse(spsNal, out var info) && info.Width > 0 && info.Height > 0)
        {
            width = info.Width; height = info.Height; mbW = info.MbWidth; mbH = info.MbHeight;
            return true;
        }
        width = height = mbW = mbH = 0;
        if (spsNal.Length is < 4 or > 512 || (spsNal[0] & 0x1F) != 7) return false;
        Span<byte> src = stackalloc byte[spsNal.Length + 4];
        src[0] = 0; src[1] = 0; src[2] = 0; src[3] = 1;
        spsNal.CopyTo(src[4..]);
        Span<byte> dst = stackalloc byte[spsNal.Length + 96];
        var rw = new H264SpsCropRewriter();
        if (rw.Rewrite(src, 1f, dst) < 0) return false;
        width = rw.SrcVisibleW; height = rw.SrcVisibleH; mbW = rw.WidthPx / 16; mbH = rw.HeightPx / 16;
        return width > 0 && height > 0;
    }

    private void ParseTrak(ReadOnlySpan<byte> trak)
    {
        if (!Find(trak, 0x6D646961, out var mdia)) return;              // mdia
        if (!Find(mdia, 0x68646C72, out var hdlr) || hdlr.Length < 12) return;   // hdlr
        var handler = BinaryPrimitives.ReadUInt32BigEndian(hdlr[8..]);
        Track t;
        if (handler == 0x76696465 && !_haveVideo) { t = _v; _haveVideo = true; }      // vide
        else if (handler == 0x736F756E && !_haveAudio) { t = _a; }                     // soun
        else return;
        if (Find(mdia, 0x6D646864, out var mdhd) && mdhd.Length >= (mdhd.Length > 0 && mdhd[0] == 1 ? 32 : 20))                 // mdhd
        {
            if (mdhd[0] == 1) { t.Timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(mdhd[20..]); t.DurationUnits = (long)BinaryPrimitives.ReadUInt64BigEndian(mdhd[24..]); }
            else { t.Timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(mdhd[12..]); t.DurationUnits = BinaryPrimitives.ReadUInt32BigEndian(mdhd[16..]); }
        }
        if (t.Timescale <= 0) return;
        if (!Find(mdia, 0x6D696E66, out var minf) || !Find(minf, 0x7374626C, out var stbl)) return;   // minf · stbl
        if (!Find(stbl, 0x73747364, out var stsd) || stsd.Length < 16) return;       // stsd
        var entry = stsd[8..];
        var fmt = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        if (t == _v)
        {
            if (fmt is not (0x61766331 or 0x61766333)) return;                        // avc1 · avc3
            if (!ParseAvcC(entry)) return;
            _avc = true;
        }
        else
        {
            if (fmt != 0x6D703461 || !ParseEsds(entry)) return;                        // mp4a
            _haveAudio = true;
        }
        BuildTable(t, stbl);
    }

    /// <summary>avcC — NAL 길이 접두 크기 · 첫 SPS/PPS.</summary>
    private bool ParseAvcC(ReadOnlySpan<byte> entry)
    {
        var i = entry.IndexOf("avcC"u8);
        if (i < 4) return false;
        var c = entry.Slice(i + 4, Math.Min(entry.Length - i - 4, (int)BinaryPrimitives.ReadUInt32BigEndian(entry[(i - 4)..]) - 8));
        if (c.Length < 7) return false;
        _nalLen = (c[4] & 3) + 1;
        var p = 6;
        var nSps = c[5] & 31;
        for (var k = 0; k < nSps; k++)
        {
            if (p + 2 > c.Length) return false;
            var l = BinaryPrimitives.ReadUInt16BigEndian(c[p..]); p += 2;
            if (p + l > c.Length) return false;
            if (k == 0 && l <= _sps.Length) { c.Slice(p, l).CopyTo(_sps); _spsLen = l; }
            p += l;
        }
        if (p >= c.Length) return false;
        var nPps = c[p++];
        for (var k = 0; k < nPps; k++)
        {
            if (p + 2 > c.Length) return false;
            var l = BinaryPrimitives.ReadUInt16BigEndian(c[p..]); p += 2;
            if (p + l > c.Length) return false;
            if (k == 0 && l <= _pps.Length) { c.Slice(p, l).CopyTo(_pps); _ppsLen = l; }
            p += l;
        }
        return _spsLen > 0 && _ppsLen > 0;
    }

    /// <summary>esds → DecoderSpecificInfo(0x05) → AudioSpecificConfig(객체 형식 · 샘플링 지수 · 채널).</summary>
    private bool ParseEsds(ReadOnlySpan<byte> entry)
    {
        var i = entry.IndexOf("esds"u8);
        if (i < 0) return false;
        if (i < 4) return false;
        var d = entry.Slice(i + 8, Math.Min(entry.Length - i - 8, (int)BinaryPrimitives.ReadUInt32BigEndian(entry[(i - 4)..]) - 12));   // fourcc + 버전/플래그
        // ES_Descriptor(0x03) → DecoderConfigDescriptor(0x04) → DecoderSpecificInfo(0x05)
        var p = 0;
        if (!Descriptor(d, ref p, 0x03, out _)) return false;
        var flags = d[p + 2]; p += 3;
        if ((flags & 0x80) != 0) p += 2;
        if ((flags & 0x40) != 0 && p < d.Length) p += 1 + d[p];
        if ((flags & 0x20) != 0) p += 2;
        if (!Descriptor(d, ref p, 0x04, out _)) return false;
        p += 13;
        if (!Descriptor(d, ref p, 0x05, out var len) || len < 2) return false;
        _aot = d[p] >> 3; _sfi = ((d[p] & 7) << 1) | (d[p + 1] >> 7); _ch = (d[p + 1] >> 3) & 15;
        if (_aot is < 1 or > 4 || _sfi > 12) return false;
        AudioRate = SfiRate(_sfi);
        return true;
    }

    /// <summary>MPEG-4 기술자 헤더(태그 1 · 길이 7 비트 가변 최대 4) — 성공 시 <paramref name="p"/> = 본문 시작.</summary>
    private static bool Descriptor(ReadOnlySpan<byte> d, ref int p, byte tag, out int len)
    {
        len = 0;
        if (p >= d.Length || d[p] != tag) return false;
        p++;
        for (var k = 0; k < 4; k++)
        {
            if (p >= d.Length) return false;
            var b = d[p++];
            len = (len << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return p + len <= d.Length;
    }

    private static int SfiRate(int sfi) => sfi switch
    {
        0 => 96_000, 1 => 88_200, 2 => 64_000, 3 => 48_000, 4 => 44_100, 5 => 32_000, 6 => 24_000,
        7 => 22_050, 8 => 16_000, 9 => 12_000, 10 => 11_025, 11 => 8_000, _ => 7_350,
    };

    /// <summary>stsz · stts · ctts · stss · stsc × stco/co64 → 샘플 표.</summary>
    private static void BuildTable(Track t, ReadOnlySpan<byte> stbl)
    {
        if (!Find(stbl, 0x7374737A, out var stsz) || stsz.Length < 12) return;                // stsz
        var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(stsz[4..]);
        var count = BinaryPrimitives.ReadUInt32BigEndian(stsz[8..]);
        if (count > MaxSamples || (fixedSize == 0 && stsz.Length < 12 + 4 * (long)count)) { t.Truncated = true; return; }
        var n = (int)count;
        for (var i = 0; i < n; i++)
        {
            ref var s = ref t.S[i];
            s = default;
            s.Size = fixedSize != 0 ? fixedSize : (int)BinaryPrimitives.ReadUInt32BigEndian(stsz[(12 + 4 * i)..]);
            s.Key = true;
        }

        if (Find(stbl, 0x73747473, out var stts) && stts.Length >= 8)                            // stts
        {
            var e = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stts[4..]), (uint)((stts.Length - 8) / 8));
            long dts = 0; var k = 0;
            for (var j = 0; j < e && k < n; j++)
            {
                var c = BinaryPrimitives.ReadUInt32BigEndian(stts[(8 + 8 * j)..]); var delta = BinaryPrimitives.ReadUInt32BigEndian(stts[(12 + 8 * j)..]);
                for (var q = 0u; q < c && k < n; q++) { t.S[k++].Dts = dts; dts += delta; }
            }
            for (; k < n; k++) t.S[k].Dts = dts;
        }
        if (Find(stbl, 0x63747473, out var ctts) && ctts.Length >= 8)                            // ctts(v1 = 부호)
        {
            var e = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(ctts[4..]), (uint)((ctts.Length - 8) / 8));
            var k = 0;
            for (var j = 0; j < e && k < n; j++)
            {
                var c = BinaryPrimitives.ReadUInt32BigEndian(ctts[(8 + 8 * j)..]); var off = BinaryPrimitives.ReadInt32BigEndian(ctts[(12 + 8 * j)..]);
                for (var q = 0u; q < c && k < n; q++) t.S[k++].Cts = off;
            }
        }
        if (Find(stbl, 0x73747373, out var stss) && stss.Length >= 8)                            // stss
        {
            for (var i = 0; i < n; i++) t.S[i].Key = false;
            var e = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stss[4..]), (uint)((stss.Length - 8) / 4));
            for (var j = 0; j < e; j++)
            {
                var num = BinaryPrimitives.ReadUInt32BigEndian(stss[(8 + 4 * j)..]);
                if (num >= 1 && num <= n) t.S[num - 1].Key = true;
            }
        }

        var co64 = false; ReadOnlySpan<byte> stco;
        if (!Find(stbl, 0x7374636F, out stco)) { if (!Find(stbl, 0x636F3634, out stco)) { t.Truncated = true; return; } co64 = true; }   // stco · co64
        if (!Find(stbl, 0x73747363, out var stsc) || stsc.Length < 8 || stco.Length < 8) { t.Truncated = true; return; }                 // stsc
        var chunks = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stco[4..]), (uint)((stco.Length - 8) / (co64 ? 8 : 4)));
        var runs = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stsc[4..]), (uint)((stsc.Length - 8) / 12));
        var si = 0;
        for (var r = 0; r < runs && si < n; r++)
        {
            var firstChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc[(8 + 12 * r)..]) - 1;
            var perChunk = (int)BinaryPrimitives.ReadUInt32BigEndian(stsc[(12 + 12 * r)..]);
            var endChunk = r + 1 < runs ? (int)BinaryPrimitives.ReadUInt32BigEndian(stsc[(20 + 12 * r)..]) - 1 : chunks;
            for (var ch = Math.Max(0, firstChunk); ch < Math.Min(endChunk, chunks) && si < n; ch++)
            {
                long off = co64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(stco[(8 + 8 * ch)..]) : BinaryPrimitives.ReadUInt32BigEndian(stco[(8 + 4 * ch)..]);
                for (var q = 0; q < perChunk && si < n; q++) { t.S[si].Offset = off; off += t.S[si].Size; si++; }
            }
        }
        if (si < n) { t.Truncated = true; return; }
        t.N = n;
    }

    private static bool Next(ReadOnlySpan<byte> s, scoped ref int at, out uint type, out ReadOnlySpan<byte> payload)
    {
        type = 0; payload = default;
        if (at + 8 > s.Length) return false;
        long size = BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
        type = BinaryPrimitives.ReadUInt32BigEndian(s[(at + 4)..]);
        var hdr = 8;
        if (size == 1) { if (at + 16 > s.Length) return false; size = (long)BinaryPrimitives.ReadUInt64BigEndian(s[(at + 8)..]); hdr = 16; }
        else if (size == 0) size = s.Length - at;
        if (size < hdr || at + size > s.Length) return false;
        payload = s.Slice(at + hdr, (int)size - hdr);
        at += (int)size;
        return true;
    }

    private static bool Find(ReadOnlySpan<byte> s, uint want, out ReadOnlySpan<byte> payload)
    {
        var at = 0;
        while (Next(s, ref at, out var type, out payload))
        {
            if (type == want) return true;   // 풀 박스 = 버전/플래그 포함 페이로드(호출부가 오프셋 4 부터 판독)
        }
        payload = default;
        return false;
    }

    // ══════════════════════ 주입(무할당 핫패스) ══════════════════════

    /// <summary>
    /// 파싱된 표 → 링 재구성(실측 kbps × 1.25 · 용량 동일 시 버퍼 재사용) → DTS 병합 순회 주입. 창(120 s) 밖 · 링 거부 샘플은 <see cref="Faults"/> 로 집계.
    /// </summary>
    public LocalVideoImportStatus Inject(Stream f, RollingCircularVideoBufferEngine ring)
    {
        if (_v.N == 0) return Fail(LocalVideoImportStatus.NoVideoTrack, "파싱 선행 필요");
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long bytes = 0;
        for (var i = 0; i < _v.N; i++) { var us = PtsUs(_v, i); if (us >= WindowUs) continue; bytes += _v.S[i].Size + 16; }
        for (var i = 0; i < _a.N; i++) { var us = PtsUs(_a, i); if (us >= WindowUs) continue; bytes += _a.S[i].Size + 7; }
        // 링 용량(= 창 × kbps × 1.125) ≥ 창 구간 바이트 × 1.25 · 최대 AU × 4(링 단일 프레임 상한 = 용량 / 4)
        var needCap = Math.Max(bytes * 1.25, _maxAu * 4.0 + 4096);
        RingKbps = (int)Math.Clamp(needCap * 8 / (1000.0 * RollingCircularVideoBufferEngine.WindowSec * 1.125) + 64, 64, 16_000);
        if (ring.MaxKbps != RingKbps) ring.Configure(RingKbps); else ring.Reset();

        int vi = 0, ai = 0, faults = 0, keys = 0, nv = 0, na = 0;
        long injected = 0, prevDts = long.MinValue, firstPts = long.MaxValue, lastPts = 0;
        while (vi < _v.N || ai < _a.N)
        {
            var takeVideo = ai >= _a.N || (vi < _v.N && _v.Us(_v.S[vi].Dts) <= _a.Us(_a.S[ai].Dts));
            if (takeVideo)
            {
                ref readonly var s = ref _v.S[vi++];
                var pts = _v.Us(s.Dts + s.Cts) - _ptsShiftUs;
                if (pts >= WindowUs) continue;
                if (s.Dts <= prevDts) faults++;
                prevDts = s.Dts;
                Read(f, s.Offset, s.Size);
                var n = ToAnnexB(_pkt.AsSpan(0, s.Size), s.Key, _au);
                if (n <= 0 || ring.WriteVideo(_au.AsSpan(0, n), pts) < 0) { faults++; continue; }
                injected += n; nv++;
                if (s.Key) keys++;
                if (pts < firstPts) firstPts = pts;
                if (pts > lastPts) lastPts = pts;
            }
            else
            {
                ref readonly var s = ref _a.S[ai++];
                var pts = _a.Us(s.Dts) - _ptsShiftUs;
                if (pts < 0 || pts >= WindowUs || s.Size + 7 > _au.Length) continue;
                Read(f, s.Offset, s.Size);
                WriteAdtsHeader(_au, s.Size + 7);
                _pkt.AsSpan(0, s.Size).CopyTo(_au.AsSpan(7));
                if (ring.WriteAudio(_au.AsSpan(0, s.Size + 7), pts) < 0) { faults++; continue; }
                injected += s.Size + 7; na++;
            }
        }
        InjectedVideo = nv; InjectedAudio = na; InjectedKeyframes = keys; InjectedBytes = injected; Faults = faults;
        FirstPtsUs = firstPts == long.MaxValue ? 0 : firstPts; LastPtsUs = lastPts;
        LastInjectMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (nv == 0 || keys == 0) { ring.Reset(); return Fail(LocalVideoImportStatus.NoVideoTrack, "창 구간 키프레임 없음"); }
        return LocalVideoImportStatus.Injected;
    }

    private long PtsUs(Track t, int i) => t.Us(t.S[i].Dts + t.S[i].Cts) - _ptsShiftUs;

    private void Read(Stream f, long offset, int size)
    {
        if (offset < 0 || offset + size > FileBytes) throw new EndOfStreamException("샘플 범위가 파일 밖");
        f.Position = offset;
        f.ReadExactly(_pkt.AsSpan(0, size));
    }

    /// <summary>AVCC 샘플 → Annex-B AU: AUD · (키프레임에 SPS/PPS 없으면 avcC 셋 동봉) · NAL(기존 AUD 제외). 반환 = 길이(−1 = 길이 접두 손상).</summary>
    private int ToAnnexB(ReadOnlySpan<byte> sample, bool key, byte[] dst)
    {
        var p = StartCode(dst, 0); dst[p++] = 0x09; dst[p++] = 0xF0;
        if (key && !HasParamSets(sample))
        {
            p = StartCode(dst, p); _sps.AsSpan(0, _spsLen).CopyTo(dst.AsSpan(p)); p += _spsLen;
            p = StartCode(dst, p); _pps.AsSpan(0, _ppsLen).CopyTo(dst.AsSpan(p)); p += _ppsLen;
        }
        var i = 0;
        while (i + _nalLen <= sample.Length)
        {
            var len = 0;
            for (var k = 0; k < _nalLen; k++) len = (len << 8) | sample[i + k];
            i += _nalLen;
            if (len <= 0 || i + len > sample.Length || p + 4 + len > dst.Length) return -1;
            if ((sample[i] & 0x1F) != 9)
            {
                p = StartCode(dst, p);
                sample.Slice(i, len).CopyTo(dst.AsSpan(p));
                p += len;
            }
            i += len;
        }
        return i == sample.Length ? p : -1;
    }

    private bool HasParamSets(ReadOnlySpan<byte> sample)
    {
        var i = 0;
        while (i + _nalLen < sample.Length)
        {
            var len = 0;
            for (var k = 0; k < _nalLen; k++) len = (len << 8) | sample[i + k];
            i += _nalLen;
            if (len <= 0 || i + len > sample.Length) return false;
            if ((sample[i] & 0x1F) == 7) return true;
            i += len;
        }
        return false;
    }

    private static int StartCode(byte[] d, int p)
    {
        d[p] = 0; d[p + 1] = 0; d[p + 2] = 0; d[p + 3] = 1;
        return p + 4;
    }

    /// <summary>ADTS 7 바이트(CRC 없음) — ASC 객체 형식 · 샘플링 지수 · 채널.</summary>
    private void WriteAdtsHeader(byte[] d, int len)
    {
        d[0] = 0xFF; d[1] = 0xF1;
        d[2] = (byte)(((_aot - 1) << 6) | (_sfi << 2) | ((_ch >> 2) & 1));
        d[3] = (byte)(((_ch & 3) << 6) | ((len >> 11) & 3));
        d[4] = (byte)(len >> 3);
        d[5] = (byte)(((len & 7) << 5) | 0x1F);
        d[6] = 0xFC;
    }

    // ══════════════════════ PCM 엔벨로프 프록시(비핫패스) ══════════════════════

    /// <summary>
    /// AAC 프레임 크기 → 음성 에너지 엔벨로프(무음 프레임 ≈ 최소 크기 · 중앙값 이상 = 유성) × 200 Hz 반송파 → 48 kHz 모노 PCM.
    /// 오디오 트랙 없음 = 일정 유성 레벨(데드에어 컷 없음).
    /// </summary>
    private void BuildEnvelopePcm()
    {
        var sec = (int)Math.Clamp((Math.Min(LastPtsUs, WindowUs) + 999_999) / 1_000_000 + 1, 1, RollingCircularVideoBufferEngine.WindowSec);
        if (_pcm.Length != sec * Fs) _pcm = new short[sec * Fs];
        var pcm = _pcm;
        if (!_haveAudio || _a.N == 0)
        {
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(6_000f * MathF.Sin(2f * MathF.PI * 200f * i / Fs));
            return;
        }
        Array.Clear(pcm);
        var n = Math.Min(_a.N, _sizeScratch.Length);
        for (var i = 0; i < n; i++) _sizeScratch[i] = _a.S[i].Size;
        var sizes = _sizeScratch.AsSpan(0, n);
        sizes.Sort();
        float floor = sizes[n / 20] * 1.15f, median = Math.Max(floor + 1f, sizes[n / 2]);
        var frameUs = (long)AacFrameSamples * 1_000_000 / Math.Max(1, AudioRate);
        for (var k = 0; k < _a.N; k++)
        {
            var us = _a.Us(_a.S[k].Dts) - _ptsShiftUs;
            if (us < 0 || us >= WindowUs) continue;
            var level = Math.Clamp((_a.S[k].Size - floor) / (median - floor), 0f, 1.4f);
            if (level <= 0.05f) continue;
            var s0 = (int)(us * Fs / 1_000_000); var s1 = (int)Math.Min(pcm.Length, (us + frameUs) * Fs / 1_000_000);
            for (var i = Math.Max(0, s0); i < s1; i++)
                pcm[i] = (short)Math.Clamp(9_000f * level * MathF.Sin(2f * MathF.PI * 200f * i / Fs), short.MinValue, short.MaxValue);
        }
    }
}
