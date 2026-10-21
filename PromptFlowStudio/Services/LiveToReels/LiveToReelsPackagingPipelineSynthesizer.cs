using System.Buffers.Binary;
using System.Text;
using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.LiveToReels;

/// <summary>v17.69.0 다국어 더빙 트랙 입력(v17.64 레인 캡처 · 48 kHz 모노 16 bit · 재사용 버퍼).</summary>
public sealed class ReelsDubTrack
{
    public ReelsDubTrack(int capacity) => Pcm = new short[capacity];
    public DubLanguage Language { get; set; }
    public short[] Pcm { get; }
    public int Length { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>v17.69.0 릴스 패키지 사양(러너 소유 · 재사용).</summary>
public sealed class ReelsPackageSpec
{
    public const int MaxDubs = 3;
    public string Caption { get; set; } = "";
    public TypoLang CaptionLang { get; set; } = TypoLang.Ko;
    public bool IncludeBgmHook { get; set; } = true;
    public double Bpm { get; set; } = 128;
    public int HookBeats { get; set; } = 8;
    public float HookLevel { get; set; } = 0.8f;
    public int DubCount { get; set; }
    public ReelsDubTrack?[] Dubs { get; } = new ReelsDubTrack?[MaxDubs];
}

/// <summary>
/// [PromptFlow Studio v17.69.0] 무할당 라이브-투-릴스 자동 패키징 합성기(백그라운드 워커 · 재인코딩 0).
///   ▸ 입력: v17.62 무손실 클리퍼 fMP4(ftyp · moov · [moof · mdat]×n) → 샘플 테이블(오프셋 · 크기 · 키 · 지속) 판독 — stsd(avc1/avcC · mp4a/esds) 원본 그대로 승계.
///   ▸ 자막: v17.52 SDF 아틀라스 단어 타이밍 → v17.53 비트 그리드 스냅 → 3 플랫폼 데드존 교집합 세이프 존 박스의 tx3g 타임드 텍스트 트랙 +
///     SDF 플레이트(RGBA) 렌더 · 세이프 존 사후 감사(번인은 재인코딩 결선점 — 비트스트림 무손실 유지).
///   ▸ 오디오: 원본 AAC(대체 그룹 1 · 기본) + v17.53 절차적 BGM 비트 드롭 훅(t = 0 다운비트 · HookBeats 박 · 마지막 박 페이드) 16 bit PCM 트랙 +
///     v17.64 다국어 더빙 트랙(ISO 639-2 언어 태그 · 대체 그룹 1 · 플레이어 선택).
///   ▸ 출력: 단일 MP4(faststart · ftyp → moov → mdat · 트랙당 1 청크) — 64 KB 스크래치 스트리밍 복사(정상 상태 무할당).
/// </summary>
public sealed class LiveToReelsPackagingPipelineSynthesizer
{
    public const int MaxVideo = 4096, MaxAudio = 4096, MaxCues = 8, ScratchBytes = 64 * 1024, BoxCap = 512 * 1024, Rate = 48000, PcmBlock = 1024;
    public const int SafeW = 1080, SafeH = 1920;

    private readonly long[] _vOff = new long[MaxVideo], _aOff = new long[MaxAudio];
    private readonly int[] _vSize = new int[MaxVideo], _vDur = new int[MaxVideo], _vCto = new int[MaxVideo], _aSize = new int[MaxAudio], _aDur = new int[MaxAudio];
    private readonly bool[] _vKey = new bool[MaxVideo];
    private readonly byte[] _vStsd = new byte[4096], _aStsd = new byte[1024], _box = new byte[BoxCap], _scratch = new byte[ScratchBytes], _cueText = new byte[4096];
    private readonly int[] _cueStart = new int[MaxCues], _cueLen = new int[MaxCues];
    private readonly float[] _cueT0 = new float[MaxCues], _cueT1 = new float[MaxCues];
    private readonly float[] _l = new float[PcmBlock], _r = new float[PcmBlock];
    private readonly double[] _beatMs = new double[512];
    private readonly int[] _patch = new int[8];
    private readonly char[] _cueChars = new char[1024];
    private int _vN, _aN, _vStsdLen, _aStsdLen, _vTs, _aTs, _cueN, _patchN;
    private bool _hasCto;

    public LiveToReelsPackagingPipelineSynthesizer()
    {
        Atlas = new SdfGlyphAtlas();
        Typo = new RealtimeWordByWordMotionTypographyEngine(Atlas) { Preset = TypoPreset.Pop, FontPx = 84f };
        Guard = new CrossPlatformUiSafeZoneGuard(SafeW, SafeH, CrossPlatformUiSafeZoneGuard.All);
    }

    public SdfGlyphAtlas Atlas { get; }
    public RealtimeWordByWordMotionTypographyEngine Typo { get; }
    public CrossPlatformUiSafeZoneGuard Guard { get; }
    public ProceduralBgmGenerator Bgm { get; } = new();
    /// <summary>SDF 자막 플레이트(첫 큐 중앙 시점 · 지연 생성 1080×1920 RGBA).</summary>
    public RgbaOverlayLayer? Plate { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long Packages { get; private set; }
    public long Failures { get; private set; }
    public string? LastFailure { get; private set; }
    public int LastTracks { get; private set; }
    public long LastBytes { get; private set; }
    public double LastMs { get; private set; }
    public double MaxMs { get; private set; }
    public int LastVideoFrames => _vN;
    public int LastAudioFrames => _aN;
    public int LastKeyFrames { get; private set; }
    public int LastBgmFrames { get; private set; }
    public int LastDubTracks { get; private set; }
    public int LastCueCount => _cueN;
    public int LastBeatSnaps { get; private set; }
    public double LastDurationSec { get; private set; }
    public PxRect LastCaptionBox { get; private set; }
    public bool LastCaptionSafe { get; private set; }
    public long BytesCopied { get; private set; }
    public float CueStartMs(int i) => _cueT0[i];
    public float CueEndMs(int i) => _cueT1[i];
    public ReadOnlySpan<byte> CueUtf8(int i) => _cueText.AsSpan(_cueStart[i], _cueLen[i]);

    /// <summary>ISO 639-2/T 3 문자(mdhd 언어 팩 · 파일 판독 검증용).</summary>
    public static ReadOnlySpan<byte> Iso639(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "kor"u8, DubLanguage.En => "eng"u8, DubLanguage.Ja => "jpn"u8, DubLanguage.Zh => "zho"u8, DubLanguage.Es => "spa"u8, DubLanguage.Fr => "fra"u8,
        DubLanguage.De => "deu"u8, DubLanguage.It => "ita"u8, DubLanguage.Pt => "por"u8, DubLanguage.Ru => "rus"u8, DubLanguage.Ar => "ara"u8, DubLanguage.Hi => "hin"u8, DubLanguage.Vi => "vie"u8, _ => "und"u8,
    };

    private static ushort PackLang(ReadOnlySpan<byte> c) => (ushort)(((c[0] - 0x60) << 10) | ((c[1] - 0x60) << 5) | (c[2] - 0x60));

    // ══════════════════════ 패키징(백그라운드 · 정상 상태 무할당) ══════════════════════
    public bool Package(SegmentedChunkStream src, ReelsPackageSpec spec, Stream dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Parse(src)) { Failures++; return false; }
        var vDurMedia = 0L; for (var i = 0; i < _vN; i++) vDurMedia += _vDur[i];
        var aDurMedia = 0L; for (var i = 0; i < _aN; i++) aDurMedia += _aDur[i];
        var durMs = _vTs > 0 ? vDurMedia * 1000 / _vTs : 0;
        LastDurationSec = durMs / 1000.0;
        var audioFrames = _aTs > 0 ? aDurMedia * Rate / _aTs : durMs * Rate / 1000;

        // ── BGM 비트 드롭 훅(v17.53 템포 · t = 0 다운비트) ──
        var bpm = Math.Clamp(spec.Bpm, 40, 220);
        var bgmFrames = 0;
        if (spec.IncludeBgmHook)
        {
            var hook = (long)Math.Ceiling(spec.HookBeats * 60.0 / bpm * Rate / PcmBlock) * PcmBlock;
            bgmFrames = (int)Math.Min(hook, Math.Max(PcmBlock, audioFrames / PcmBlock * PcmBlock));
            Bgm.SetTempo(bpm, 0); Bgm.Reset(); Bgm.Level = Math.Clamp(spec.HookLevel, 0f, 1f);
        }
        LastBgmFrames = bgmFrames;

        // ── 자막 큐(v17.52 단어 타이밍 → v17.53 비트 스냅 → 페이지 = 큐) ──
        BuildCues(spec, bpm, durMs);

        // ── 더빙 트랙 길이(클립 오디오 길이 상한) ──
        var dubN = 0;
        Span<int> dubFrames = stackalloc int[ReelsPackageSpec.MaxDubs];
        for (var i = 0; i < spec.DubCount && i < ReelsPackageSpec.MaxDubs; i++)
        {
            var d = spec.Dubs[i];
            if (d is null || !d.Enabled || d.Length <= 0) continue;
            dubFrames[i] = (int)Math.Min(d.Length, Math.Min(d.Pcm.Length, audioFrames));
            if (dubFrames[i] > 0) dubN++;
        }
        LastDubTracks = dubN;

        // ── mdat 배치(트랙당 연속 1 청크) ──
        long sizeV = 0, sizeA = 0; for (var i = 0; i < _vN; i++) sizeV += _vSize[i]; for (var i = 0; i < _aN; i++) sizeA += _aSize[i];
        long sizeB = bgmFrames * 4L, sizeT = 0;
        var textSamples = TextSampleCount(durMs);
        for (var i = 0; i < _cueN; i++) sizeT += 2 + _cueLen[i];
        sizeT += 2L * (textSamples - _cueN);
        long sizeD = 0; for (var i = 0; i < ReelsPackageSpec.MaxDubs; i++) sizeD += dubFrames[i] * 2L;
        var payload = sizeV + sizeA + sizeB + sizeD + sizeT;
        if (payload + BoxCap > uint.MaxValue) { Fail("4 GB 초과"); return false; }

        // ── ftyp + moov(청크 오프셋은 mdat 상대 → 길이 확정 후 패치) ──
        var b = _box.AsSpan(); var n = 0; _patchN = 0;
        var ftyp = Open(b, ref n, "ftyp"u8); Put(b, ref n, "isom"u8); U32(b, ref n, 512); Put(b, ref n, "isomiso2avc1mp41"u8); Close(b, ftyp, n);
        var moov = Open(b, ref n, "moov"u8);
        var tracks = 2 + (bgmFrames > 0 ? 1 : 0) + dubN + (_cueN > 0 ? 1 : 0);
        Mvhd(b, ref n, (uint)durMs, (uint)(tracks + 1));
        uint id = 1; long rel = 0;
        // 1) 비디오(원본 avc1 승계)
        VideoTrak(b, ref n, id++, (uint)durMs, (uint)vDurMedia, rel); rel += sizeV;
        // 2) 원본 AAC(대체 그룹 1 · 기본 활성)
        AacTrak(b, ref n, id++, (uint)(aDurMedia * 1000 / Math.Max(_aTs, 1)), (uint)aDurMedia, rel); rel += sizeA;
        // 3) BGM 훅 PCM(그룹 0 · 항상 재생)
        if (bgmFrames > 0) { PcmTrak(b, ref n, id++, 2, (uint)bgmFrames, "und"u8, 0, 3, rel); rel += sizeB; }
        // 4) 다국어 더빙(대체 그룹 1 · 비활성 기본 — 플레이어 언어 선택)
        for (var i = 0; i < ReelsPackageSpec.MaxDubs; i++)
        {
            if (dubFrames[i] <= 0) continue;
            PcmTrak(b, ref n, id++, 1, (uint)dubFrames[i], Iso639(spec.Dubs[i]!.Language), 1, 2, rel); rel += dubFrames[i] * 2L;
        }
        // 5) tx3g 자막(세이프 존 박스)
        if (_cueN > 0) TextTrak(b, ref n, id++, (uint)durMs, textSamples, rel);
        Close(b, moov, n);
        var dataStart = (uint)(n + 8);
        for (var i = 0; i < _patchN; i++) BinaryPrimitives.WriteUInt32BigEndian(b[_patch[i]..], BinaryPrimitives.ReadUInt32BigEndian(b[_patch[i]..]) + dataStart);
        BinaryPrimitives.WriteUInt32BigEndian(b[n..], (uint)(8 + payload)); "mdat"u8.CopyTo(b[(n + 4)..]); n += 8;
        dst.Write(b[..n]);

        // ── mdat 페이로드 스트리밍 ──
        for (var i = 0; i < _vN; i++) Copy(src, _vOff[i], _vSize[i], dst);
        for (var i = 0; i < _aN; i++) Copy(src, _aOff[i], _aSize[i], dst);
        if (bgmFrames > 0) WriteBgm(dst, bgmFrames, bpm);
        for (var i = 0; i < ReelsPackageSpec.MaxDubs; i++) if (dubFrames[i] > 0) WritePcm(dst, spec.Dubs[i]!.Pcm.AsSpan(0, dubFrames[i]));
        if (_cueN > 0) WriteText(dst, durMs);

        LastTracks = tracks;
        LastBytes = n + payload;
        Packages++;
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastMs = ms; if (ms > MaxMs) MaxMs = ms;
        LastFailure = null;
        return true;
    }

    private void Fail(string why) { LastFailure = why; Failures++; }

    // ══════════════════════ 원본 fMP4 판독 ══════════════════════
    private bool Parse(SegmentedChunkStream src)
    {
        _vN = _aN = _vStsdLen = _aStsdLen = 0; _vTs = _aTs = 0; _hasCto = false; LastKeyFrames = 0;
        uint vId = 0, aId = 0;
        long pos = 0, len = src.Length;
        Span<byte> h = stackalloc byte[16];
        while (pos + 8 <= len)
        {
            if (src.ReadAt(pos, h[..8]) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h); var hdr = 8;
            if (size == 1) { if (src.ReadAt(pos + 8, h.Slice(8, 8)) < 8) break; size = (long)BinaryPrimitives.ReadUInt64BigEndian(h[8..]); hdr = 16; }
            else if (size == 0) size = len - pos;
            if (size < hdr || pos + size > len) { LastFailure = "손상된 박스"; return false; }
            var type = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
            if (type == 0x6D6F6F76 /*moov*/ || type == 0x6D6F6F66 /*moof*/)
            {
                if (size > ScratchBytes) { LastFailure = "헤더 박스 과대"; return false; }
                var box = _scratch.AsSpan(0, (int)size);
                if (src.ReadAt(pos, box) < size) { LastFailure = "판독 실패"; return false; }
                var body = box[hdr..];
                if (type == 0x6D6F6F76) { if (!ParseMoov(body, ref vId, ref aId)) return false; }
                else if (!ParseMoof(body, pos, vId, aId)) return false;
            }
            pos += size;
        }
        if (_vN == 0 || _vStsdLen == 0) { LastFailure = "비디오 샘플 없음"; return false; }
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

    private bool ParseMoov(ReadOnlySpan<byte> moov, ref uint vId, ref uint aId)
    {
        var p = 0;
        while (p + 8 <= moov.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(moov[p..]);
            if (s < 8 || p + s > moov.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(moov[(p + 4)..]) == 0x7472616B /*trak*/)
            {
                var trak = moov.Slice(p + 8, s - 8);
                if (Child(trak, 0x746B6864 /*tkhd*/, out var tkhd) && Child(trak, 0x6D646961 /*mdia*/, out var mdia)
                    && Child(mdia, 0x6D646864 /*mdhd*/, out var mdhd) && Child(mdia, 0x68646C72 /*hdlr*/, out var hdlr)
                    && Child(mdia, 0x6D696E66 /*minf*/, out var minf) && Child(minf, 0x7374626C /*stbl*/, out var stbl))
                {
                    var v1 = tkhd[0] == 1;
                    var tid = BinaryPrimitives.ReadUInt32BigEndian(tkhd[(v1 ? 20 : 12)..]);
                    var ts = (int)BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
                    var handler = BinaryPrimitives.ReadUInt32BigEndian(hdlr[8..]);
                    // stsd 박스 전체(헤더 포함) 승계
                    var q = 0; var stsdAt = -1; var stsdLen = 0;
                    while (q + 8 <= stbl.Length) { var ss = (int)BinaryPrimitives.ReadUInt32BigEndian(stbl[q..]); if (ss < 8 || q + ss > stbl.Length) break; if (BinaryPrimitives.ReadUInt32BigEndian(stbl[(q + 4)..]) == 0x73747364) { stsdAt = q; stsdLen = ss; break; } q += ss; }
                    if (stsdAt < 0) { LastFailure = "stsd 없음"; return false; }
                    if (handler == 0x76696465 /*vide*/ && stsdLen <= _vStsd.Length)
                    {
                        vId = tid; _vTs = ts; stbl.Slice(stsdAt, stsdLen).CopyTo(_vStsd); _vStsdLen = stsdLen;
                        Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[^8..]) >> 16); Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[^4..]) >> 16);
                    }
                    else if (handler == 0x736F756E /*soun*/ && stsdLen <= _aStsd.Length) { aId = tid; _aTs = ts; stbl.Slice(stsdAt, stsdLen).CopyTo(_aStsd); _aStsdLen = stsdLen; }
                }
            }
            p += s;
        }
        return true;
    }

    private bool ParseMoof(ReadOnlySpan<byte> moof, long moofPos, uint vId, uint aId)
    {
        var p = 0;
        while (p + 8 <= moof.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(moof[p..]);
            if (s < 8 || p + s > moof.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(moof[(p + 4)..]) == 0x74726166 /*traf*/ && !ParseTraf(moof.Slice(p + 8, s - 8), moofPos, vId, aId)) return false;
            p += s;
        }
        return true;
    }

    private bool ParseTraf(ReadOnlySpan<byte> traf, long moofPos, uint vId, uint aId)
    {
        if (!Child(traf, 0x74666864 /*tfhd*/, out var tfhd)) return true;
        var tf = BinaryPrimitives.ReadUInt32BigEndian(tfhd) & 0xFFFFFF;
        var tid = BinaryPrimitives.ReadUInt32BigEndian(tfhd[4..]);
        var o = 8; long baseOff = moofPos; uint defDur = 0, defSize = 0, defFlags = 0;
        if ((tf & 0x01) != 0) { baseOff = (long)BinaryPrimitives.ReadUInt64BigEndian(tfhd[o..]); o += 8; }
        if ((tf & 0x02) != 0) o += 4;
        if ((tf & 0x08) != 0) { defDur = BinaryPrimitives.ReadUInt32BigEndian(tfhd[o..]); o += 4; }
        if ((tf & 0x10) != 0) { defSize = BinaryPrimitives.ReadUInt32BigEndian(tfhd[o..]); o += 4; }
        if ((tf & 0x20) != 0) { defFlags = BinaryPrimitives.ReadUInt32BigEndian(tfhd[o..]); }
        var video = tid == vId;
        if (!video && tid != aId) return true;
        var p = 0;
        while (p + 8 <= traf.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(traf[p..]);
            if (s < 8 || p + s > traf.Length) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(traf[(p + 4)..]) == 0x7472756E /*trun*/)
            {
                var t = traf.Slice(p + 8, s - 8);
                var fl = BinaryPrimitives.ReadUInt32BigEndian(t) & 0xFFFFFF;
                var cnt = (int)BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
                var q = 8; var off = baseOff; var firstFlags = 0u; var hasFirst = false;
                if ((fl & 0x01) != 0) { off = baseOff + BinaryPrimitives.ReadInt32BigEndian(t[q..]); q += 4; }
                if ((fl & 0x04) != 0) { firstFlags = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; hasFirst = true; }
                for (var i = 0; i < cnt; i++)
                {
                    uint dur = defDur, size = defSize, flags = i == 0 && hasFirst ? firstFlags : defFlags; var cto = 0;
                    if ((fl & 0x100) != 0) { dur = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; }
                    if ((fl & 0x200) != 0) { size = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; }
                    if ((fl & 0x400) != 0) { flags = BinaryPrimitives.ReadUInt32BigEndian(t[q..]); q += 4; }
                    if ((fl & 0x800) != 0) { cto = BinaryPrimitives.ReadInt32BigEndian(t[q..]); q += 4; }
                    if (video)
                    {
                        if (_vN >= MaxVideo) { LastFailure = "비디오 샘플 상한"; return false; }
                        _vOff[_vN] = off; _vSize[_vN] = (int)size; _vDur[_vN] = (int)dur; _vCto[_vN] = cto; if (cto != 0) _hasCto = true;
                        var key = (flags & 0x00010000) == 0; _vKey[_vN] = key; if (key) LastKeyFrames++;
                        _vN++;
                    }
                    else
                    {
                        if (_aN >= MaxAudio) { LastFailure = "오디오 샘플 상한"; return false; }
                        _aOff[_aN] = off; _aSize[_aN] = (int)size; _aDur[_aN] = (int)dur; _aN++;
                    }
                    off += size;
                }
            }
            p += s;
        }
        return true;
    }

    // ══════════════════════ 자막 큐 ══════════════════════
    private void BuildCues(ReelsPackageSpec spec, double bpm, long durMs)
    {
        _cueN = 0; LastBeatSnaps = 0; LastCaptionBox = default; LastCaptionSafe = true;
        if (string.IsNullOrWhiteSpace(spec.Caption) || durMs <= 0) return;
        Typo.SetCaption(spec.Caption, spec.CaptionLang, 400f);
        Typo.Layout((Guard.Safe.W - 2 * RealtimeWordByWordMotionTypographyEngine.OutlineEm * Typo.FontPx) / Typo.FontPx);
        var period = 60_000.0 / bpm; var nb = 0;
        for (var t = 0.0; t < durMs && nb < _beatMs.Length; t += period * 0.5) _beatMs[nb++] = t;
        LastBeatSnaps = Typo.SnapToBeats(_beatMs.AsSpan(0, nb), (float)(period * 0.25));
        var textAt = 0;
        for (var pg = 0; pg < Typo.PageCount && _cueN < MaxCues; pg++)
        {
            float a = float.MaxValue, z = 0; var cc = 0;
            for (var i = 0; i < Typo.WordCount; i++)
            {
                ref readonly var w = ref Typo.Word(i);
                if (w.Page != pg) continue;
                a = MathF.Min(a, w.StartMs); z = MathF.Max(z, w.EndMs);
                var wt = Typo.WordText(i);
                if (cc > 0 && cc < _cueChars.Length) _cueChars[cc++] = w.Line == 1 && i > 0 && Typo.Word(i - 1).Line == 0 ? '\n' : ' ';
                var k = Math.Min(wt.Length, _cueChars.Length - cc); wt[..k].CopyTo(_cueChars.AsSpan(cc)); cc += k;
            }
            if (cc == 0 || a >= durMs) continue;
            if (textAt + 3 * cc + 4 > _cueText.Length) break;
            var bytes = Encoding.UTF8.GetBytes(_cueChars.AsSpan(0, cc), _cueText.AsSpan(textAt, Math.Min(_cueText.Length - textAt, 3 * cc + 4)));
            _cueStart[_cueN] = textAt; _cueLen[_cueN] = bytes; textAt += bytes;
            _cueT0[_cueN] = MathF.Max(a, _cueN > 0 ? _cueT1[_cueN - 1] : 0f); _cueT1[_cueN] = MathF.Min(MathF.Max(z, _cueT0[_cueN] + 300f), durMs);
            _cueN++;
        }
        if (_cueN == 0) return;
        // SDF 플레이트(첫 큐 중앙) — 세이프 존 박스 · 사후 감사
        Plate ??= new RgbaOverlayLayer(SafeW, SafeH);
        Plate.Begin();
        Typo.Render((_cueT0[0] + _cueT1[0]) * 0.5f, Plate, Guard);
        var box = Typo.LastDrawnBounds;
        LastCaptionSafe = Guard.Audit(in box);
        if (box.IsEmpty) box = new PxRect(Guard.Safe.X, Guard.Safe.Y + Guard.Safe.H * 2 / 3, Guard.Safe.W, Guard.Safe.H / 6);
        Guard.Fit(ref box);
        LastCaptionBox = box;
    }

    private int TextSampleCount(long durMs)
    {
        if (_cueN == 0) return 0;
        var k = 0; var t = 0f;
        for (var i = 0; i < _cueN; i++) { if (_cueT0[i] > t) k++; k++; t = _cueT1[i]; }
        if (t < durMs) k++;
        return k;
    }

    // ══════════════════════ moov 박스 ══════════════════════
    private static void Mvhd(Span<byte> b, ref int n, uint durMs, uint nextId)
    {
        var m = Open(b, ref n, "mvhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 1000); U32(b, ref n, durMs);
        U32(b, ref n, 0x00010000); U16(b, ref n, 0x0100); Zero(b, ref n, 10); Matrix(b, ref n); Zero(b, ref n, 24); U32(b, ref n, nextId); Close(b, m, n);
    }

    private static void Tkhd(Span<byte> b, ref int n, uint flags, uint id, uint durMs, short layer, ushort group, ushort volume, int w, int h)
    {
        var t = Open(b, ref n, "tkhd"u8); U32(b, ref n, flags); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, id); U32(b, ref n, 0); U32(b, ref n, durMs);
        Zero(b, ref n, 8); U16(b, ref n, (ushort)layer); U16(b, ref n, group); U16(b, ref n, volume); U16(b, ref n, 0); Matrix(b, ref n);
        U32(b, ref n, (uint)w << 16); U32(b, ref n, (uint)h << 16); Close(b, t, n);
    }

    private static void Mdhd(Span<byte> b, ref int n, uint ts, uint dur, ushort lang) { var m = Open(b, ref n, "mdhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, ts); U32(b, ref n, dur); U16(b, ref n, lang); U16(b, ref n, 0); Close(b, m, n); }
    private static void Hdlr(Span<byte> b, ref int n, ReadOnlySpan<byte> type, ReadOnlySpan<byte> name) { var h = Open(b, ref n, "hdlr"u8); U32(b, ref n, 0); U32(b, ref n, 0); Put(b, ref n, type); Zero(b, ref n, 12); Put(b, ref n, name); b[n++] = 0; Close(b, h, n); }
    private static void Dinf(Span<byte> b, ref int n) { var d = Open(b, ref n, "dinf"u8); var r = Open(b, ref n, "dref"u8); U32(b, ref n, 0); U32(b, ref n, 1); var u = Open(b, ref n, "url "u8); U32(b, ref n, 1); Close(b, u, n); Close(b, r, n); Close(b, d, n); }

    /// <summary>단일 청크 표(stsc 1 항목 · stco 1 항목 = mdat 상대 → 패치 등록).</summary>
    private void OneChunk(Span<byte> b, ref int n, uint samples, long rel)
    {
        var a = Open(b, ref n, "stsc"u8); U32(b, ref n, 0); U32(b, ref n, 1); U32(b, ref n, 1); U32(b, ref n, samples); U32(b, ref n, 1); Close(b, a, n);
        a = Open(b, ref n, "stco"u8); U32(b, ref n, 0); U32(b, ref n, 1); _patch[_patchN++] = n; U32(b, ref n, (uint)rel); Close(b, a, n);
    }

    private static void Stts(Span<byte> b, ref int n, ReadOnlySpan<int> dur)
    {
        var a = Open(b, ref n, "stts"u8); U32(b, ref n, 0); var cntAt = n; U32(b, ref n, 0);
        uint entries = 0;
        for (var i = 0; i < dur.Length;)
        {
            var j = i; while (j < dur.Length && dur[j] == dur[i]) j++;
            U32(b, ref n, (uint)(j - i)); U32(b, ref n, (uint)dur[i]); entries++; i = j;
        }
        BinaryPrimitives.WriteUInt32BigEndian(b[cntAt..], entries); Close(b, a, n);
    }

    private static void Stsz(Span<byte> b, ref int n, ReadOnlySpan<int> size)
    {
        var a = Open(b, ref n, "stsz"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, (uint)size.Length);
        for (var i = 0; i < size.Length; i++) U32(b, ref n, (uint)size[i]);
        Close(b, a, n);
    }

    private void VideoTrak(Span<byte> b, ref int n, uint id, uint durMs, uint mediaDur, long rel)
    {
        var trak = Open(b, ref n, "trak"u8);
        Tkhd(b, ref n, 3, id, durMs, 0, 0, 0, Width, Height);
        var mdia = Open(b, ref n, "mdia"u8);
        Mdhd(b, ref n, (uint)_vTs, mediaDur, 0x55C4);
        Hdlr(b, ref n, "vide"u8, "PromptFlow Reels Video"u8);
        var minf = Open(b, ref n, "minf"u8);
        var vmhd = Open(b, ref n, "vmhd"u8); U32(b, ref n, 1); Zero(b, ref n, 8); Close(b, vmhd, n);
        Dinf(b, ref n);
        var stbl = Open(b, ref n, "stbl"u8);
        Put(b, ref n, _vStsd.AsSpan(0, _vStsdLen));
        Stts(b, ref n, _vDur.AsSpan(0, _vN));
        if (_hasCto)
        {
            var c = Open(b, ref n, "ctts"u8); U32(b, ref n, 0); U32(b, ref n, (uint)_vN);
            for (var i = 0; i < _vN; i++) { U32(b, ref n, 1); U32(b, ref n, (uint)_vCto[i]); }
            Close(b, c, n);
        }
        if (LastKeyFrames < _vN)
        {
            var s = Open(b, ref n, "stss"u8); U32(b, ref n, 0); U32(b, ref n, (uint)LastKeyFrames);
            for (var i = 0; i < _vN; i++) if (_vKey[i]) U32(b, ref n, (uint)(i + 1));
            Close(b, s, n);
        }
        Stsz(b, ref n, _vSize.AsSpan(0, _vN));
        OneChunk(b, ref n, (uint)_vN, rel);
        Close(b, stbl, n); Close(b, minf, n); Close(b, mdia, n); Close(b, trak, n);
    }

    private void AacTrak(Span<byte> b, ref int n, uint id, uint durMs, uint mediaDur, long rel)
    {
        var trak = Open(b, ref n, "trak"u8);
        Tkhd(b, ref n, 3, id, durMs, 0, 1, 0x0100, 0, 0);
        var mdia = Open(b, ref n, "mdia"u8);
        Mdhd(b, ref n, (uint)Math.Max(_aTs, 1), mediaDur, PackLang("kor"u8));
        Hdlr(b, ref n, "soun"u8, "PromptFlow Live Mix"u8);
        var minf = Open(b, ref n, "minf"u8);
        var smhd = Open(b, ref n, "smhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, smhd, n);
        Dinf(b, ref n);
        var stbl = Open(b, ref n, "stbl"u8);
        if (_aStsdLen > 0) Put(b, ref n, _aStsd.AsSpan(0, _aStsdLen));
        else { var e = Open(b, ref n, "stsd"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, e, n); }
        Stts(b, ref n, _aDur.AsSpan(0, _aN));
        Stsz(b, ref n, _aSize.AsSpan(0, _aN));
        OneChunk(b, ref n, (uint)_aN, rel);
        Close(b, stbl, n); Close(b, minf, n); Close(b, mdia, n); Close(b, trak, n);
    }

    /// <summary>16 bit LE PCM('sowt') 트랙 — 샘플 = 프레임(stsz 상수 = 채널 × 2) · stts 1 항목.</summary>
    private void PcmTrak(Span<byte> b, ref int n, uint id, int channels, uint frames, ReadOnlySpan<byte> lang, ushort group, uint flags, long rel)
    {
        var durMs = (uint)(frames * 1000L / Rate);
        var trak = Open(b, ref n, "trak"u8);
        Tkhd(b, ref n, flags, id, durMs, 0, group, 0x0100, 0, 0);
        var mdia = Open(b, ref n, "mdia"u8);
        Mdhd(b, ref n, Rate, frames, PackLang(lang));
        Hdlr(b, ref n, "soun"u8, group == 0 ? "PromptFlow Beat Drop Hook"u8 : "PromptFlow Dub"u8);
        var minf = Open(b, ref n, "minf"u8);
        var smhd = Open(b, ref n, "smhd"u8); U32(b, ref n, 0); U32(b, ref n, 0); Close(b, smhd, n);
        Dinf(b, ref n);
        var stbl = Open(b, ref n, "stbl"u8);
        var stsd = Open(b, ref n, "stsd"u8); U32(b, ref n, 0); U32(b, ref n, 1);
        var e = Open(b, ref n, "sowt"u8); Zero(b, ref n, 6); U16(b, ref n, 1); Zero(b, ref n, 8); U16(b, ref n, (ushort)channels); U16(b, ref n, 16); U32(b, ref n, 0); U32(b, ref n, (uint)Rate << 16);
        Close(b, e, n); Close(b, stsd, n);
        var a = Open(b, ref n, "stts"u8); U32(b, ref n, 0); U32(b, ref n, 1); U32(b, ref n, frames); U32(b, ref n, 1); Close(b, a, n);
        a = Open(b, ref n, "stsz"u8); U32(b, ref n, 0); U32(b, ref n, (uint)(channels * 2)); U32(b, ref n, frames); Close(b, a, n);
        OneChunk(b, ref n, frames, rel);
        Close(b, stbl, n); Close(b, minf, n); Close(b, mdia, n); Close(b, trak, n);
    }

    private void TextTrak(Span<byte> b, ref int n, uint id, uint durMs, int samples, long rel)
    {
        var trak = Open(b, ref n, "trak"u8);
        Tkhd(b, ref n, 3, id, durMs, -1, 0, 0, Width, Height);
        var mdia = Open(b, ref n, "mdia"u8);
        Mdhd(b, ref n, 1000, durMs, PackLang("kor"u8));
        Hdlr(b, ref n, "sbtl"u8, "PromptFlow SDF Caption"u8);
        var minf = Open(b, ref n, "minf"u8);
        var nmhd = Open(b, ref n, "nmhd"u8); U32(b, ref n, 0); Close(b, nmhd, n);
        Dinf(b, ref n);
        var stbl = Open(b, ref n, "stbl"u8);
        var stsd = Open(b, ref n, "stsd"u8); U32(b, ref n, 0); U32(b, ref n, 1);
        var tx = Open(b, ref n, "tx3g"u8); Zero(b, ref n, 6); U16(b, ref n, 1);
        U32(b, ref n, 0); b[n++] = 1; b[n++] = 0xFF;            // displayFlags · 가로 중앙 · 세로 하단
        U32(b, ref n, 0x000000A0);                                // 배경 RGBA(반투명 검정)
        var box = LastCaptionBox; var sx = Width > 0 ? Width / (float)SafeW : 1f; var sy = Height > 0 ? Height / (float)SafeH : 1f;
        U16(b, ref n, (ushort)(short)(box.Y * sy)); U16(b, ref n, (ushort)(short)(box.X * sx)); U16(b, ref n, (ushort)(short)(box.Bottom * sy)); U16(b, ref n, (ushort)(short)(box.Right * sx));
        U16(b, ref n, 0); U16(b, ref n, 0); U16(b, ref n, 1); b[n++] = 1; b[n++] = (byte)Math.Clamp((int)(Typo.FontPx * sy * 0.6f), 12, 255); U32(b, ref n, 0xFFE600FF); // 굵게 · 하이라이트 노랑
        var ft = Open(b, ref n, "ftab"u8); U16(b, ref n, 1); U16(b, ref n, 1); var fn = "Sans-Serif"u8; b[n++] = (byte)fn.Length; Put(b, ref n, fn); Close(b, ft, n);
        Close(b, tx, n); Close(b, stsd, n);
        // stts · stsz(공백 샘플 = 길이 0)
        var a = Open(b, ref n, "stts"u8); U32(b, ref n, 0); U32(b, ref n, (uint)samples);
        var t = 0f;
        for (var i = 0; i < _cueN; i++)
        {
            if (_cueT0[i] > t) { U32(b, ref n, 1); U32(b, ref n, (uint)Math.Max(1, (int)(_cueT0[i] - t))); }
            U32(b, ref n, 1); U32(b, ref n, (uint)Math.Max(1, (int)(_cueT1[i] - _cueT0[i]))); t = _cueT1[i];
        }
        if (t < durMs) { U32(b, ref n, 1); U32(b, ref n, (uint)Math.Max(1, (int)(durMs - t))); }
        Close(b, a, n);
        a = Open(b, ref n, "stsz"u8); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, (uint)samples);
        t = 0f;
        for (var i = 0; i < _cueN; i++) { if (_cueT0[i] > t) U32(b, ref n, 2); U32(b, ref n, (uint)(2 + _cueLen[i])); t = _cueT1[i]; }
        if (t < durMs) U32(b, ref n, 2);
        Close(b, a, n);
        OneChunk(b, ref n, (uint)samples, rel);
        Close(b, stbl, n); Close(b, minf, n); Close(b, mdia, n); Close(b, trak, n);
    }

    // ══════════════════════ mdat 페이로드 ══════════════════════
    private void Copy(SegmentedChunkStream src, long off, int size, Stream dst)
    {
        var left = size;
        while (left > 0)
        {
            var k = src.ReadAt(off, _scratch.AsSpan(0, Math.Min(left, ScratchBytes)));
            if (k <= 0) { _scratch.AsSpan(0, Math.Min(left, ScratchBytes)).Clear(); k = Math.Min(left, ScratchBytes); }
            dst.Write(_scratch.AsSpan(0, k)); off += k; left -= k; BytesCopied += k;
        }
    }

    private void WriteBgm(Stream dst, int frames, double bpm)
    {
        var fadeFrom = frames - (int)(60.0 / bpm * Rate);
        for (var f = 0; f < frames; f += PcmBlock)
        {
            var k = Math.Min(PcmBlock, frames - f);
            Bgm.Render(_l.AsSpan(0, k), _r.AsSpan(0, k));
            var o = _scratch.AsSpan(0, k * 4);
            for (var i = 0; i < k; i++)
            {
                var g = f + i > fadeFrom && fadeFrom > 0 ? 1f - (f + i - fadeFrom) / (float)(frames - fadeFrom) : 1f;
                BinaryPrimitives.WriteInt16LittleEndian(o[(i * 4)..], (short)(Math.Clamp(_l[i] * g, -1f, 1f) * 32767f));
                BinaryPrimitives.WriteInt16LittleEndian(o[(i * 4 + 2)..], (short)(Math.Clamp(_r[i] * g, -1f, 1f) * 32767f));
            }
            dst.Write(o);
        }
    }

    private void WritePcm(Stream dst, ReadOnlySpan<short> pcm)
    {
        for (var f = 0; f < pcm.Length; f += ScratchBytes / 2)
        {
            var k = Math.Min(ScratchBytes / 2, pcm.Length - f);
            var o = _scratch.AsSpan(0, k * 2);
            for (var i = 0; i < k; i++) BinaryPrimitives.WriteInt16LittleEndian(o[(i * 2)..], pcm[f + i]);
            dst.Write(o);
        }
    }

    private void WriteText(Stream dst, long durMs)
    {
        Span<byte> z = stackalloc byte[2]; z.Clear();
        var t = 0f;
        for (var i = 0; i < _cueN; i++)
        {
            if (_cueT0[i] > t) dst.Write(z);
            BinaryPrimitives.WriteUInt16BigEndian(_scratch, (ushort)_cueLen[i]);
            _cueText.AsSpan(_cueStart[i], _cueLen[i]).CopyTo(_scratch.AsSpan(2));
            dst.Write(_scratch.AsSpan(0, 2 + _cueLen[i]));
            t = _cueT1[i];
        }
        if (t < durMs) dst.Write(z);
    }

    private static int Open(Span<byte> b, ref int n, ReadOnlySpan<byte> type) => FragmentedMp4Muxer.Open(b, ref n, type);
    private static void Close(Span<byte> b, int at, int n) => FragmentedMp4Muxer.Close(b, at, n);
    private static void Put(Span<byte> b, ref int n, ReadOnlySpan<byte> s) => FragmentedMp4Muxer.Put(b, ref n, s);
    private static void U32(Span<byte> b, ref int n, uint v) => FragmentedMp4Muxer.U32(b, ref n, v);
    private static void U16(Span<byte> b, ref int n, ushort v) { BinaryPrimitives.WriteUInt16BigEndian(b[n..], v); n += 2; }
    private static void Zero(Span<byte> b, ref int n, int k) { b.Slice(n, k).Clear(); n += k; }
    private static void Matrix(Span<byte> b, ref int n) { U32(b, ref n, 0x00010000); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0x00010000); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0); U32(b, ref n, 0x40000000); }
}
