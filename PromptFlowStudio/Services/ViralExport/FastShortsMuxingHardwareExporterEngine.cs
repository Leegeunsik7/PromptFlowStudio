using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PromptFlowStudio.Services.KineticBRoll;
using PromptFlowStudio.Services.LiveClipper;
using PromptFlowStudio.Services.LiveRestream;
using PromptFlowStudio.Services.SmartPublishing;
using PromptFlowStudio.Services.VisualHooks;

namespace PromptFlowStudio.Services.ViralExport;

public enum ExportStatus : byte { Idle, Rendering, Ready, Expired, Failed, Cancelled }

/// <summary>v17.85.0 숏폼 합성 지시(v17.77 9:16 크롭 · v17.78 키네틱 자막 · v17.76 효과음 큐 원점).</summary>
public struct ShortsOverlay
{
    public int CropX, Caption;
    public HookBox CaptionBox;
    public HormoziPreset Preset;
    public float BounceScale;
    public double SpikeMs;

    public static ShortsOverlay From(in PackagedShort s) => new()
    {
        CropX = s.CropX, Caption = Math.Clamp(s.Caption, 0, InstantShortsPackagingPipelineGuard.Captions.Length - 1), CaptionBox = s.CaptionBox,
        Preset = s.Preset, BounceScale = s.BounceScale <= 0 ? 1f : s.BounceScale, SpikeMs = Math.Max(0, (s.SpikeUs - s.StartUs) / 1000.0),
    };
}

/// <summary>v17.85.0 fMP4 인출 결과(바이트는 엔진 소유 버퍼 — 다음 인출 전까지 유효).</summary>
public struct ExportResult
{
    public long ShortId, DurationUs;
    public int Bytes, VideoSamples, AudioSamples, TextSamples, SfxSamples, Keyframes, CropX, AudioRate, AudioChannels;
    public ulong Hash;
    public float RenderMs;
    public bool StartsWithIdr;
    public ExportStatus Status;
}

/// <summary>v17.85.0 ISO BMFF 구조 검증 리포트(<see cref="FastShortsMuxingHardwareExporterEngine.TryInspect"/>).</summary>
public struct Mp4Inspection
{
    public int TopBoxes, Tracks, Trafs, Samples;
    public uint MajorBrand;
    public long MdatPayload, SampleBytes;
    public bool Ftyp, Moov, Mvex, Moof, Mdat, Avc1, AvcC, Mp4a, Esds, Tx3g, Clap, Ipcm, OffsetsInMdat, FirstVideoIdr;
    public readonly bool Valid => Ftyp && Moov && Mvex && Moof && Mdat && Avc1 && AvcC && OffsetsInMdat && FirstVideoIdr;
}

/// <summary>
/// [PromptFlow Studio v17.85.0] 🎬 무할당 인메모리 fMP4 숏폼 하드웨어 엑스포터 — 외부 FFmpeg 프로세스 · 디스크 임시 파일 0.
///   ▸ 입력: v17.84 클리퍼 링 슬라이스(H.264 Annex-B AU + AAC ADTS) — 프레임 단위 짧은 락 복사 → Annex-B → AVCC(4 B 길이 접두 · AUD/SPS/PPS 제거) ·
///     ADTS 헤더 제거 + AudioSpecificConfig 추출.
///   ▸ 출력: ftyp(isom/iso6/iso5/avc1/mp41) · moov(mvhd · trak[avc1+avcC+clap+pasp · mp4a+esds · tx3g · ipcm+pcmC] · mvex/mehd/trex) · moof(mfhd · traf[tfhd/tfdt/trun]) · mdat.
///   ▸ 합성: v17.77 9:16 크롭 = avc1 Clean Aperture(clap · 607 × 1080 창 피사체 중심 오프셋) + tkhd 1080 × 1920 표시 ·
///     v17.78 키네틱 자막 = 3GPP 타임드 텍스트(3 단어 창 · 활성 단어 styl 프리셋 색 + 1.15× 바운스 폰트) · v17.76 효과음 = ISO 23003-5 ipcm 16-bit 트랙(선택).
///   ▸ 무할당 계약: 작업/출력 버퍼는 최초 1 회 확장 후 재사용 — 동일 규모 재인출 0 B. <see cref="Output"/> = <see cref="ReadOnlyMemory{T}"/> 즉시 공급.
/// </summary>
public sealed class FastShortsMuxingHardwareExporterEngine
{
    public const int OutW = 1080, OutH = 1920, VideoTs = 90_000, TextTs = 1_000, MovieTs = 1_000, SfxFrames = 1_024, PageWords = 3;
    public const int VideoTrack = 1, AudioTrack = 2, TextTrack = 3, SfxTrack = 4, WordMs = 400, HoldMs = 1_200;
    public const float BaseFontPx = 64f;
    public const uint SyncFlags = 0x02000000u, NonSyncFlags = 0x01010000u;

    private readonly object _gate = new();
    private readonly ProceduralViralSoundFxSynthesizer _sfx = new(0x5FA7u);
    private readonly float[] _mixScratch = new float[SfxFrames];
    private readonly short[] _pcm = new short[SfxFrames];
    private readonly int[] _stack = new int[16];
    private readonly byte[] _sps = new byte[256], _pps = new byte[128];
    private readonly char[] _page = new char[160];
    private byte[] _frame = new byte[128 * 1024];
    private byte[] _v = new byte[1 << 20], _a = new byte[256 * 1024], _t = new byte[8 * 1024], _s = new byte[64 * 1024];
    private byte[] _out = new byte[2 << 20];
    private int[] _vSize = new int[2048], _aSize = new int[4096];
    private uint[] _vDur = new uint[2048];
    private bool[] _vKey = new bool[2048];
    private long[] _vPts = new long[2048];
    private readonly int[] _tSize = new int[64];
    private readonly uint[] _tDur = new uint[64];
    private int _vN, _aN, _tN, _sN, _vLen, _aLen, _tLen, _sLen, _spsLen, _ppsLen, _pos, _sp, _rate = 48_000, _ch = 2, _keys;
    private long _vPts0 = -1, _aPts0 = -1, _vLastPts;
    private ushort _asc;
    private float _progress;

    public bool IncludeSfxTrack { get; set; }
    public bool IncludeCaptionTrack { get; set; } = true;
    public float Progress => Volatile.Read(ref _progress);
    public ExportResult Last { get; private set; }
    public long Exports { get; private set; }
    public long Failures { get; private set; }
    public int Capacity => _out.Length;
    public long Grows { get; private set; }

    /// <summary>최근 인출 fMP4 바이트(다음 인출 전까지 유효 · 복사 없음).</summary>
    public ReadOnlyMemory<byte> Output { get { lock (_gate) return Last.Status == ExportStatus.Ready ? _out.AsMemory(0, Last.Bytes) : ReadOnlyMemory<byte>.Empty; } }

    /// <summary>다운로드/업로드용 읽기 전용 스트림(버퍼 공유 · 비핫패스).</summary>
    public Stream OpenRead() { lock (_gate) return new MemoryStream(_out, 0, Last.Status == ExportStatus.Ready ? Last.Bytes : 0, false); }

    // ══════════════════════ 링 슬라이스 → fMP4 ══════════════════════

    public ExportStatus Export(RollingCircularVideoBufferEngine ring, in ClipSlice slice, in ShortsOverlay ov, long shortId = 0, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            ResetClip();
            var status = ExportStatus.Ready;
            if (slice.IsEmpty) status = ExportStatus.Failed;
            if (status == ExportStatus.Ready && slice.PrependParams)
            {
                var n = ring.CopyParams(_frame);
                if (n > 0) AddVideoCore(_frame.AsSpan(0, n), -1);
            }
            var total = Math.Max(1, slice.LastSeq - slice.FirstSeq + 1);
            for (var q = slice.FirstSeq; status == ExportStatus.Ready && q <= slice.LastSeq; q++)
            {
                var f = ring.Frame(q);
                if (f.Length > _frame.Length) { _frame = new byte[f.Length + f.Length / 2]; Grows++; }
                var w = ring.CopyFrame(q, _frame);
                if (w < 0) { status = ExportStatus.Expired; break; }
                if (f.Kind == MediaKind.Video) AddVideoCore(_frame.AsSpan(0, w), f.PtsUs);
                else AddAudioCore(_frame.AsSpan(0, w), f.PtsUs);
                if (((q - slice.FirstSeq) & 63) == 63)
                {
                    Volatile.Write(ref _progress, 0.9f * (q - slice.FirstSeq) / total);
                    if (ct.IsCancellationRequested) { status = ExportStatus.Cancelled; break; }
                }
            }
            return Complete(status, ov, shortId, t0);
        }
    }

    // ══════════════════════ 저수준 API(외부 소스 · 테스트) ══════════════════════

    public void BeginClip() { lock (_gate) ResetClip(); }

    public void AddVideo(ReadOnlySpan<byte> annexB, long ptsUs) { lock (_gate) AddVideoCore(annexB, ptsUs); }

    public void AddAudio(ReadOnlySpan<byte> adts, long ptsUs) { lock (_gate) AddAudioCore(adts, ptsUs); }

    public ExportStatus FinishClip(in ShortsOverlay ov, long shortId = 0) { lock (_gate) return Complete(ExportStatus.Ready, ov, shortId, Stopwatch.GetTimestamp()); }

    private void ResetClip()
    {
        _vN = _aN = _tN = _sN = _vLen = _aLen = _tLen = _sLen = _keys = 0;
        _vPts0 = _aPts0 = -1; _vLastPts = 0;
        Volatile.Write(ref _progress, 0f);
        var r = Last; r.Status = ExportStatus.Rendering; Last = r;
    }

    private void AddVideoCore(ReadOnlySpan<byte> au, long ptsUs)
    {
        var pos = 0; var start = _vLen; var key = false;
        while (RtmpWire.NextNal(au, ref pos, out var nal))
        {
            if (nal.Length == 0) continue;
            var t = nal[0] & 0x1F;
            if (t == 7) { if (nal.Length <= _sps.Length) { nal.CopyTo(_sps); _spsLen = nal.Length; } continue; }
            if (t == 8) { if (nal.Length <= _pps.Length) { nal.CopyTo(_pps); _ppsLen = nal.Length; } continue; }
            if (t == 9 || ptsUs < 0) continue;
            if (t == 5) key = true;
            Ensure(ref _v, _vLen + nal.Length + 4);
            BinaryPrimitives.WriteInt32BigEndian(_v.AsSpan(_vLen), nal.Length);
            nal.CopyTo(_v.AsSpan(_vLen + 4));
            _vLen += nal.Length + 4;
        }
        if (ptsUs < 0 || _vLen == start) return;
        if (_vN == 0 && !key) { _vLen = start; return; }   // 디코더 진입점 = IDR — 선행 P 프레임 폐기
        if (_vN == _vSize.Length) { Array.Resize(ref _vSize, _vN * 2); Array.Resize(ref _vDur, _vN * 2); Array.Resize(ref _vKey, _vN * 2); Array.Resize(ref _vPts, _vN * 2); Grows++; }
        if (_vPts0 < 0) _vPts0 = ptsUs;
        _vSize[_vN] = _vLen - start; _vKey[_vN] = key; _vPts[_vN] = ptsUs; _vLastPts = ptsUs;
        if (key) _keys++;
        _vN++;
    }

    private void AddAudioCore(ReadOnlySpan<byte> adts, long ptsUs)
    {
        if (adts.Length < 7 || adts[0] != 0xFF || (adts[1] & 0xF0) != 0xF0 || _vPts0 < 0) return;   // 비디오 IDR 이전 오디오 폐기(A/V 정렬)
        var hdr = (adts[1] & 1) == 1 ? 7 : 9;
        if (adts.Length <= hdr) return;
        var aot = ((adts[2] >> 6) & 3) + 1; var sfi = (adts[2] >> 2) & 0xF; var ch = ((adts[2] & 1) << 2) | (adts[3] >> 6);
        _asc = (ushort)((aot << 11) | (sfi << 7) | (ch << 3));
        _rate = SfiRate(sfi); _ch = Math.Max(1, ch);
        var raw = adts.Slice(hdr);
        Ensure(ref _a, _aLen + raw.Length);
        if (_aN == _aSize.Length) { Array.Resize(ref _aSize, _aN * 2); Grows++; }
        raw.CopyTo(_a.AsSpan(_aLen));
        _aLen += raw.Length;
        _aSize[_aN++] = raw.Length;
        if (_aPts0 < 0) _aPts0 = ptsUs;
    }

    private ExportStatus Complete(ExportStatus status, in ShortsOverlay ov, long shortId, long t0)
    {
        if (status == ExportStatus.Ready && (_vN == 0 || _spsLen < 4 || _ppsLen == 0)) status = ExportStatus.Failed;
        var r = new ExportResult { ShortId = shortId, Status = status, CropX = ov.CropX, AudioRate = _rate, AudioChannels = _ch };
        if (status == ExportStatus.Ready)
        {
            // 비디오 샘플 지속(90 kHz) — 마지막 = 직전 간격
            uint lastDur = 3_000;
            for (var i = 0; i < _vN; i++)
            {
                if (i + 1 < _vN) lastDur = (uint)Math.Max(1, (_vPts[i + 1] - _vPts[i]) * VideoTs / 1_000_000);
                _vDur[i] = lastDur;
            }
            var durUs = _vLastPts - _vPts0 + (long)lastDur * 1_000_000 / VideoTs;
            var durMs = (int)(durUs / 1000);
            if (IncludeCaptionTrack) BuildCaptions(ov, durMs);
            if (IncludeSfxTrack) BuildSfx(ov, durUs);
            Volatile.Write(ref _progress, 0.95f);
            WriteFile(ov, durUs, durMs);
            r.Bytes = _pos; r.DurationUs = durUs; r.VideoSamples = _vN; r.AudioSamples = _aN; r.TextSamples = IncludeCaptionTrack ? _tN : 0;
            r.SfxSamples = IncludeSfxTrack ? _sN : 0; r.Keyframes = _keys; r.StartsWithIdr = _vKey[0];
            r.Hash = RtmpWire.Fnv(_out.AsSpan(0, _pos));
            Exports++;
        }
        else Failures++;
        r.RenderMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Last = r;
        Volatile.Write(ref _progress, status == ExportStatus.Ready ? 1f : 0f);
        return status;
    }

    // ══════════════════════ v17.78 키네틱 자막(3GPP 타임드 텍스트) ══════════════════════

    private void BuildCaptions(in ShortsOverlay ov, int durMs)
    {
        _tN = 0; _tLen = 0;
        var cap = InstantShortsPackagingPipelineGuard.Captions[ov.Caption].AsSpan();
        var words = CountWords(cap);
        var startMs = (int)Math.Clamp(ov.SpikeMs - 1000, 0, Math.Max(0, durMs - 1));
        var t = 0;
        if (startMs > 0) { TextSample(ReadOnlySpan<char>.Empty, 0, 0, 0, ov, (uint)startMs); t = startMs; }
        for (var w = 0; w < words && t < durMs && _tN < _tSize.Length - 2; w++)
        {
            var d = Math.Min(WordMs, durMs - t);
            var page = BuildPage(cap, w / PageWords * PageWords, out var hs, out var he, w);
            TextSample(_page.AsSpan(0, page), hs, he, 1, ov, (uint)d);
            t += d;
        }
        if (t < durMs && words > 0)
        {
            var d = Math.Min(HoldMs, durMs - t);
            var page = BuildPage(cap, (words - 1) / PageWords * PageWords, out _, out _, -1);
            TextSample(_page.AsSpan(0, page), 0, 0, 0, ov, (uint)d);
            t += d;
        }
        if (t < durMs) TextSample(ReadOnlySpan<char>.Empty, 0, 0, 0, ov, (uint)(durMs - t));
    }

    /// <summary>firstWord 부터 3 단어 페이지를 _page 에 기록 · active 단어 문자 범위 반환.</summary>
    private int BuildPage(ReadOnlySpan<char> cap, int firstWord, out int hs, out int he, int active)
    {
        hs = he = 0;
        int n = 0, word = -1, i = 0;
        while (i < cap.Length)
        {
            while (i < cap.Length && char.IsWhiteSpace(cap[i])) i++;
            if (i >= cap.Length) break;
            var s = i;
            while (i < cap.Length && !char.IsWhiteSpace(cap[i])) i++;
            word++;
            if (word < firstWord) continue;
            if (word >= firstWord + PageWords) break;
            if (n > 0 && n < _page.Length) _page[n++] = ' ';
            var len = Math.Min(i - s, _page.Length - n);
            if (word == active) { hs = n; he = n + len; }
            cap.Slice(s, len).CopyTo(_page.AsSpan(n));
            n += len;
        }
        return n;
    }

    private void TextSample(ReadOnlySpan<char> text, int hs, int he, int styles, in ShortsOverlay ov, uint durMs)
    {
        var need = 2 + Encoding.UTF8.GetMaxByteCount(text.Length) + 32;
        Ensure(ref _t, _tLen + need);
        var p = _tLen;
        var len = Encoding.UTF8.GetBytes(text, _t.AsSpan(p + 2));
        BinaryPrimitives.WriteUInt16BigEndian(_t.AsSpan(p), (ushort)len);
        p += 2 + len;
        if (styles > 0 && he > hs)
        {
            // styl: 활성 단어 — 프리셋 활성 색 + 스프링 바운스 폰트(1.15×)
            BinaryPrimitives.WriteUInt32BigEndian(_t.AsSpan(p), 22); p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(_t.AsSpan(p), C('s', 't', 'y', 'l')); p += 4;
            BinaryPrimitives.WriteUInt16BigEndian(_t.AsSpan(p), 1); p += 2;
            p = StyleRecord(_t, p, (ushort)hs, (ushort)he, (byte)Math.Clamp(BaseFontPx * ov.BounceScale, 8f, 255f), Rgba(WordByWordKineticSubtitleEngine.PresetColors[(int)ov.Preset][0]));
        }
        _tSize[_tN] = p - _tLen; _tDur[_tN] = Math.Max(1u, durMs);
        _tLen = p; _tN++;
    }

    private static int StyleRecord(byte[] b, int p, ushort start, ushort end, byte size, uint rgba)
    {
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), start);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p + 2), end);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p + 4), 1);
        b[p + 6] = 1;   // bold
        b[p + 7] = size;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 8), rgba);
        return p + 12;
    }

    // ══════════════════════ v17.76 효과음(ipcm 16-bit 모노) ══════════════════════

    private void BuildSfx(in ShortsOverlay ov, long durUs)
    {
        _sfx.ClearCues();
        var spike = ov.SpikeMs;
        _sfx.AddCue(Math.Max(0, spike - ProceduralViralSoundFxSynthesizer.WhooshLeadMs), ViralSfxKind.Whoosh, 0.8f);
        _sfx.AddCue(spike, ViralSfxKind.Impact, 1.2f);
        var w0 = Math.Max(0, spike - 1000);
        var words = CountWords(InstantShortsPackagingPipelineGuard.Captions[ov.Caption]);
        for (var i = 0; i < words; i++) _sfx.AddCue(w0 + i * (double)WordMs, ViralSfxKind.Pop, i == 0 ? 1f : 0.55f);
        var frames = durUs * ProceduralViralSoundFxSynthesizer.Fs / 1_000_000;
        _sN = (int)((frames + SfxFrames - 1) / SfxFrames);
        _sLen = _sN * SfxFrames * 2;
        Ensure(ref _s, _sLen);
        for (var k = 0; k < _sN; k++)
        {
            Array.Clear(_pcm);
            _sfx.Mix(_pcm, 1, (long)k * SfxFrames, _mixScratch);
            var dst = _s.AsSpan(k * SfxFrames * 2, SfxFrames * 2);
            for (var i = 0; i < SfxFrames; i++) BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(i * 2), _pcm[i]);
        }
    }

    public int SfxCueCount => _sfx.CueCount;

    // ══════════════════════ ISO BMFF 직렬화 ══════════════════════

    private void WriteFile(in ShortsOverlay ov, long durUs, int durMs)
    {
        var text = IncludeCaptionTrack && _tN > 0; var sfx = IncludeSfxTrack && _sN > 0; var audio = _aN > 0;
        var bound = _vLen + _aLen + _tLen + _sLen + _vN * 16 + _aN * 8 + _tN * 8 + _sN * 8 + 8192;
        if (_out.Length < bound) { _out = new byte[bound + bound / 4]; Grows++; }
        _pos = 0; _sp = 0;
        var nextTrack = (uint)(sfx ? SfxTrack + 1 : text ? TextTrack + 1 : AudioTrack + 1);

        // ftyp
        Begin(C('f', 't', 'y', 'p')); U32(C('i', 's', 'o', 'm')); U32(0x200); U32(C('i', 's', 'o', 'm')); U32(C('i', 's', 'o', '6')); U32(C('i', 's', 'o', '5')); U32(C('a', 'v', 'c', '1')); U32(C('m', 'p', '4', '1')); End();

        // moov
        Begin(C('m', 'o', 'o', 'v'));
        Full(C('m', 'v', 'h', 'd'), 0, 0); U32(0); U32(0); U32(MovieTs); U32(0); U32(0x00010000); U16(0x0100); U16(0); U32(0); U32(0); Matrix(); Zero(24); U32(nextTrack); End();
        Trak(VideoTrack, C('v', 'i', 'd', 'e'), VideoTs, ov);
        if (audio) Trak(AudioTrack, C('s', 'o', 'u', 'n'), (uint)_rate, ov);
        if (text) Trak(TextTrack, C('t', 'e', 'x', 't'), TextTs, ov);
        if (sfx) Trak(SfxTrack, C('s', 'o', 'u', 'n'), ProceduralViralSoundFxSynthesizer.Fs, ov);
        Begin(C('m', 'v', 'e', 'x'));
        Full(C('m', 'e', 'h', 'd'), 0, 0); U32((uint)durMs); End();
        Trex(VideoTrack); if (audio) Trex(AudioTrack); if (text) Trex(TextTrack); if (sfx) Trex(SfxTrack);
        End();
        End();

        // moof
        var moof = _pos;
        Begin(C('m', 'o', 'o', 'f'));
        Full(C('m', 'f', 'h', 'd'), 0, 0); U32(1); End();
        var vOff = Traf(VideoTrack, 0, _vN, 0x000701u, _vSize, _vDur, 0, true);
        var aOff = audio ? Traf(AudioTrack, (ulong)Math.Max(0, (_aPts0 - _vPts0) * _rate / 1_000_000), _aN, 0x000301u, _aSize, null, 1024, false) : -1;
        var tOff = text ? Traf(TextTrack, 0, _tN, 0x000301u, _tSize, _tDur, 0, false) : -1;
        var sOff = sfx ? Traf(SfxTrack, 0, _sN, 0x000301u, null, null, SfxFrames, false) : -1;
        End();

        // mdat — 비디오 · 오디오 · 자막 · 효과음 연속 배치 → trun data_offset(moof 기준) 패치
        U32((uint)(8 + _vLen + _aLen + (text ? _tLen : 0) + (sfx ? _sLen : 0))); U32(C('m', 'd', 'a', 't'));
        var payload = _pos;
        Patch(vOff, payload - moof); Blit(_v, _vLen);
        if (aOff >= 0) { Patch(aOff, _pos - moof); Blit(_a, _aLen); }
        if (tOff >= 0) { Patch(tOff, _pos - moof); Blit(_t, _tLen); }
        if (sOff >= 0) { Patch(sOff, _pos - moof); Blit(_s, _sLen); }
    }

    private void Trak(int id, uint handler, uint timescale, in ShortsOverlay ov)
    {
        var video = handler == C('v', 'i', 'd', 'e'); var sound = handler == C('s', 'o', 'u', 'n'); var text = handler == C('t', 'e', 'x', 't');
        Begin(C('t', 'r', 'a', 'k'));
        Full(C('t', 'k', 'h', 'd'), 0, 3); U32(0); U32(0); U32((uint)id); U32(0); U32(0); U32(0); U32(0);
        U16(text ? (ushort)0xFFFF : (ushort)0); U16(sound ? (ushort)1 : (ushort)0); U16(sound ? (ushort)0x0100 : (ushort)0); U16(0); Matrix();
        U32(video || text ? (uint)OutW << 16 : 0); U32(video || text ? (uint)OutH << 16 : 0); End();
        Begin(C('m', 'd', 'i', 'a'));
        Full(C('m', 'd', 'h', 'd'), 0, 0); U32(0); U32(0); U32(timescale); U32(0); U16(0x55C4); U16(0); End();
        Full(C('h', 'd', 'l', 'r'), 0, 0); U32(0); U32(handler); U32(0); U32(0); U32(0);
        Ascii(video ? "PFS 9:16 Video" : text ? "PFS Kinetic Caption" : id == SfxTrack ? "PFS Viral SFX" : "PFS Audio"); Byte(0); End();
        Begin(C('m', 'i', 'n', 'f'));
        if (video) { Full(C('v', 'm', 'h', 'd'), 0, 1); U16(0); U16(0); U16(0); U16(0); End(); }
        else if (sound) { Full(C('s', 'm', 'h', 'd'), 0, 0); U16(0); U16(0); End(); }
        else { Full(C('n', 'm', 'h', 'd'), 0, 0); End(); }
        Begin(C('d', 'i', 'n', 'f')); Full(C('d', 'r', 'e', 'f'), 0, 0); U32(1); Full(C('u', 'r', 'l', ' '), 0, 1); End(); End(); End();
        Begin(C('s', 't', 'b', 'l'));
        Full(C('s', 't', 's', 'd'), 0, 0); U32(1);
        if (video) Avc1(ov);
        else if (text) Tx3g(ov);
        else if (id == SfxTrack) Ipcm();
        else Mp4a();
        End();
        Full(C('s', 't', 't', 's'), 0, 0); U32(0); End();
        Full(C('s', 't', 's', 'c'), 0, 0); U32(0); End();
        Full(C('s', 't', 's', 'z'), 0, 0); U32(0); U32(0); End();
        Full(C('s', 't', 'c', 'o'), 0, 0); U32(0); End();
        End(); End(); End(); End();
    }

    private void Avc1(in ShortsOverlay ov)
    {
        const int cropW = InstantShortsPackagingPipelineGuard.CropW, srcW = InstantShortsPackagingPipelineGuard.SrcW, srcH = InstantShortsPackagingPipelineGuard.SrcH;
        Begin(C('a', 'v', 'c', '1'));
        Zero(6); U16(1); U16(0); U16(0); Zero(12); U16(srcW); U16(srcH); U32(0x00480000); U32(0x00480000); U32(0); U16(1);
        var name = "PFS Shorts H.264";
        Byte((byte)name.Length); Ascii(name); Zero(31 - name.Length); U16(0x0018); U16(0xFFFF);
        Begin(C('a', 'v', 'c', 'C'));
        Byte(1); Byte(_sps[1]); Byte(_sps[2]); Byte(_sps[3]); Byte(0xFF); Byte(0xE1);
        U16((ushort)_spsLen); Bytes(_sps.AsSpan(0, _spsLen)); Byte(1); U16((ushort)_ppsLen); Bytes(_pps.AsSpan(0, _ppsLen));
        if (_sps[1] is 100 or 110 or 122 or 144) { Byte(0xFD); Byte(0xF8); Byte(0xF8); Byte(0); }
        End();
        // v17.77 9:16 리프레이밍 — Clean Aperture 창 607 × 1080, 중심 오프셋(1/2 px 분해능)
        var cropX = Math.Clamp(ov.CropX, 0, srcW - cropW);
        Begin(C('c', 'l', 'a', 'p')); U32(cropW); U32(1); U32(srcH); U32(1); U32((uint)(2 * cropX + cropW - srcW)); U32(2); U32(0); U32(1); End();
        Begin(C('p', 'a', 's', 'p')); U32(1); U32(1); End();
        End();
    }

    private void Mp4a()
    {
        Begin(C('m', 'p', '4', 'a'));
        Zero(6); U16(1); Zero(8); U16((ushort)_ch); U16(16); U16(0); U16(0); U32((uint)_rate << 16);
        Full(C('e', 's', 'd', 's'), 0, 0);
        Byte(0x03); Byte(25); U16(AudioTrack); Byte(0);
        Byte(0x04); Byte(17); Byte(0x40); Byte(0x15); Byte(0); U16(0); U32(128_000); U32(96_000);
        Byte(0x05); Byte(2); U16(_asc);
        Byte(0x06); Byte(1); Byte(2);
        End();
        End();
    }

    private void Tx3g(in ShortsOverlay ov)
    {
        var b = ov.CaptionBox;
        Begin(C('t', 'x', '3', 'g'));
        Zero(6); U16(1);
        U32(0x00000800);                       // displayFlags: 연속 스크롤 없음 · 박스 지정
        Byte(1); Byte(0xFF);                   // 가로 중앙 · 세로 하단
        U32(0x00000000);                       // 배경 투명
        U16((ushort)Math.Clamp(b.Y * OutH, 0, OutH)); U16((ushort)Math.Clamp(b.X * OutW, 0, OutW));
        U16((ushort)Math.Clamp(b.Bottom * OutH, 0, OutH)); U16((ushort)Math.Clamp(b.Right * OutW, 0, OutW));
        var basePx = (byte)BaseFontPx;
        Ensure(ref _frame, 16);
        StyleRecord(_frame, 0, 0, 0, basePx, Rgba(WordByWordKineticSubtitleEngine.PresetColors[(int)ov.Preset][2]));
        Bytes(_frame.AsSpan(0, 12));
        Begin(C('f', 't', 'a', 'b')); U16(1); U16(1); var font = "Pretendard"; Byte((byte)font.Length); Ascii(font); End();
        End();
    }

    private void Ipcm()
    {
        Begin(C('i', 'p', 'c', 'm'));
        Zero(6); U16(1); Zero(8); U16(1); U16(16); U16(0); U16(0); U32((uint)ProceduralViralSoundFxSynthesizer.Fs << 16);
        Full(C('p', 'c', 'm', 'C'), 0, 0); Byte(1); Byte(16); End();   // little-endian · 16 bit
        End();
    }

    private void Trex(int id)
    {
        Full(C('t', 'r', 'e', 'x'), 0, 0); U32((uint)id); U32(1); U32(0); U32(0); U32(0); End();
    }

    /// <summary>traf(tfhd default-base-is-moof · tfdt v1 · trun) — 반환 = data_offset 패치 위치.</summary>
    private int Traf(int id, ulong baseTime, int n, uint trunFlags, int[]? sizes, uint[]? durs, uint fixedDur, bool video)
    {
        Begin(C('t', 'r', 'a', 'f'));
        Full(C('t', 'f', 'h', 'd'), 0, 0x020000); U32((uint)id); End();
        Full(C('t', 'f', 'd', 't'), 1, 0); U64(baseTime); End();
        Full(C('t', 'r', 'u', 'n'), 0, trunFlags); U32((uint)n);
        var off = _pos; U32(0);
        for (var i = 0; i < n; i++)
        {
            U32(durs is null ? fixedDur : durs[i]);
            U32(sizes is null ? SfxFrames * 2u : (uint)sizes[i]);
            if (video) U32(_vKey[i] ? SyncFlags : NonSyncFlags);
        }
        End();
        End();
        return off;
    }

    private void Begin(uint cc) { _stack[_sp++] = _pos; U32(0); U32(cc); }

    private void Full(uint cc, byte version, uint flags) { Begin(cc); U32(((uint)version << 24) | (flags & 0xFFFFFF)); }

    private void End() { var s = _stack[--_sp]; BinaryPrimitives.WriteUInt32BigEndian(_out.AsSpan(s), (uint)(_pos - s)); }

    private void Patch(int at, int value) => BinaryPrimitives.WriteInt32BigEndian(_out.AsSpan(at), value);

    private void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_out.AsSpan(_pos), v); _pos += 4; }

    private void U64(ulong v) { BinaryPrimitives.WriteUInt64BigEndian(_out.AsSpan(_pos), v); _pos += 8; }

    private void U16(int v) { BinaryPrimitives.WriteUInt16BigEndian(_out.AsSpan(_pos), (ushort)v); _pos += 2; }

    private void Byte(byte v) => _out[_pos++] = v;

    private void Zero(int n) { _out.AsSpan(_pos, n).Clear(); _pos += n; }

    private void Bytes(ReadOnlySpan<byte> b) { b.CopyTo(_out.AsSpan(_pos)); _pos += b.Length; }

    private void Blit(byte[] src, int n) { src.AsSpan(0, n).CopyTo(_out.AsSpan(_pos)); _pos += n; }

    private void Ascii(string s) { for (var i = 0; i < s.Length; i++) _out[_pos++] = (byte)s[i]; }

    private void Matrix() { U32(0x00010000); U32(0); U32(0); U32(0); U32(0x00010000); U32(0); U32(0); U32(0); U32(0x40000000); }

    private void Ensure(ref byte[] b, int need)
    {
        if (b.Length >= need) return;
        Array.Resize(ref b, Math.Max(need, b.Length * 2));
        Grows++;
    }

    public static uint C(char a, char b, char c, char d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    private static uint Rgba(uint argb) => (argb << 8) | (argb >> 24);

    private static int SfiRate(int sfi) => sfi switch
    {
        0 => 96_000, 1 => 88_200, 2 => 64_000, 3 => 48_000, 4 => 44_100, 5 => 32_000, 6 => 24_000, 7 => 22_050, 8 => 16_000, 9 => 12_000, 10 => 11_025, 11 => 8_000, _ => 48_000,
    };

    public static int CountWords(ReadOnlySpan<char> s)
    {
        var n = 0; var inWord = false;
        for (var i = 0; i < s.Length; i++)
        {
            var w = !char.IsWhiteSpace(s[i]);
            if (w && !inWord) n++;
            inWord = w;
        }
        return n;
    }

    // ══════════════════════ 구조 검증(무할당 파서) ══════════════════════

    /// <summary>최상위 ftyp/moov/moof/mdat 순회 · 트랙 샘플 엔트리 · trun data_offset → mdat 범위 · 첫 비디오 샘플 IDR 검사.</summary>
    public static bool TryInspect(ReadOnlySpan<byte> mp4, out Mp4Inspection r)
    {
        r = default;
        long mdatStart = -1, mdatEnd = -1, moofStart = -1;
        var p = 0;
        while (p + 8 <= mp4.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(mp4.Slice(p));
            var type = BinaryPrimitives.ReadUInt32BigEndian(mp4.Slice(p + 4));
            if (size < 8 || p + size > mp4.Length) return false;
            var body = mp4.Slice(p + 8, (int)size - 8);
            r.TopBoxes++;
            if (type == C('f', 't', 'y', 'p')) { r.Ftyp = r.TopBoxes == 1; r.MajorBrand = body.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(body) : 0; }
            else if (type == C('m', 'o', 'o', 'v')) { r.Moov = true; Walk(body, ref r, 0); }
            else if (type == C('m', 'o', 'o', 'f')) { r.Moof = true; moofStart = p; }
            else if (type == C('m', 'd', 'a', 't')) { r.Mdat = true; mdatStart = p + 8; mdatEnd = p + size; r.MdatPayload = size - 8; }
            p += (int)size;
        }
        if (p != mp4.Length || moofStart < 0 || mdatStart < 0) return false;

        // moof → traf → trun 검사(data_offset + Σ샘플 크기 ⊂ mdat)
        var moofSize = (int)BinaryPrimitives.ReadUInt32BigEndian(mp4.Slice((int)moofStart));
        var moof = mp4.Slice((int)moofStart + 8, moofSize - 8);
        var ok = true; var q = 0;
        while (q + 8 <= moof.Length)
        {
            var sz = (int)BinaryPrimitives.ReadUInt32BigEndian(moof.Slice(q));
            if (sz < 8 || q + sz > moof.Length) return false;
            if (BinaryPrimitives.ReadUInt32BigEndian(moof.Slice(q + 4)) == C('t', 'r', 'a', 'f'))
            {
                r.Trafs++;
                var traf = moof.Slice(q + 8, sz - 8);
                uint track = 0; var t = 0;
                while (t + 8 <= traf.Length)
                {
                    var bs = (int)BinaryPrimitives.ReadUInt32BigEndian(traf.Slice(t));
                    if (bs < 8 || t + bs > traf.Length) return false;
                    var bt = BinaryPrimitives.ReadUInt32BigEndian(traf.Slice(t + 4));
                    var b = traf.Slice(t + 8, bs - 8);
                    if (bt == C('t', 'f', 'h', 'd')) track = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                    else if (bt == C('t', 'r', 'u', 'n'))
                    {
                        var flags = BinaryPrimitives.ReadUInt32BigEndian(b) & 0xFFFFFF;
                        var n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                        var o = 8;
                        long data = 0;
                        if ((flags & 1) != 0) { data = moofStart + BinaryPrimitives.ReadInt32BigEndian(b.Slice(o)); o += 4; }
                        if ((flags & 4) != 0) o += 4;
                        long sum = 0; var first = 0;
                        for (var i = 0; i < n; i++)
                        {
                            if ((flags & 0x100) != 0) o += 4;
                            if ((flags & 0x200) != 0) { var s = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o)); if (i == 0) first = s; sum += s; o += 4; }
                            if ((flags & 0x400) != 0) o += 4;
                            if ((flags & 0x800) != 0) o += 4;
                        }
                        r.Samples += n; r.SampleBytes += sum;
                        ok &= data >= mdatStart && data + sum <= mdatEnd;
                        if (track == VideoTrack && n > 0 && first > 5 && data + 5 <= mdatEnd) r.FirstVideoIdr = (mp4[(int)data + 4] & 0x1F) == 5;
                    }
                    t += bs;
                }
            }
            q += sz;
        }
        r.OffsetsInMdat = ok && r.Trafs > 0;
        return r.Valid;
    }

    private static void Walk(ReadOnlySpan<byte> body, ref Mp4Inspection r, int depth)
    {
        var p = 0;
        while (p + 8 <= body.Length && depth < 12)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p));
            if (size < 8 || p + size > body.Length) return;
            var type = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p + 4));
            var inner = body.Slice(p + 8, size - 8);
            if (type == C('t', 'r', 'a', 'k')) { r.Tracks++; Walk(inner, ref r, depth + 1); }
            else if (type == C('m', 'v', 'e', 'x')) r.Mvex = true;
            else if (type == C('m', 'd', 'i', 'a') || type == C('m', 'i', 'n', 'f') || type == C('s', 't', 'b', 'l')) Walk(inner, ref r, depth + 1);
            else if (type == C('s', 't', 's', 'd') && inner.Length > 8) Walk(inner.Slice(8), ref r, depth + 1);
            else if (type == C('a', 'v', 'c', '1')) { r.Avc1 = true; if (inner.Length > 78) Walk(inner.Slice(78), ref r, depth + 1); }
            else if (type == C('m', 'p', '4', 'a')) { r.Mp4a = true; if (inner.Length > 28) Walk(inner.Slice(28), ref r, depth + 1); }
            else if (type == C('i', 'p', 'c', 'm')) r.Ipcm = true;
            else if (type == C('t', 'x', '3', 'g')) r.Tx3g = true;
            else if (type == C('a', 'v', 'c', 'C')) r.AvcC = inner.Length > 8 && inner[0] == 1;
            else if (type == C('e', 's', 'd', 's')) r.Esds = inner.Length > 20 && inner[4] == 0x03;
            else if (type == C('c', 'l', 'a', 'p')) r.Clap = inner.Length == 32;
            p += size;
        }
    }

    public static string StatusName(ExportStatus s) => s switch
    {
        ExportStatus.Idle => "대기", ExportStatus.Rendering => "렌더 중", ExportStatus.Ready => "인출 준비", ExportStatus.Expired => "링 만료",
        ExportStatus.Cancelled => "취소", _ => "실패",
    };
}
