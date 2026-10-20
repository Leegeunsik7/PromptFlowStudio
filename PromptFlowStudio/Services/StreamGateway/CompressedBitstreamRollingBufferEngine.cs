using System.Buffers.Binary;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>v17.62.0 링 내 압축 샘플 1 개(절대 바이트 위치 · 길이 · 세션 내 인덱스).</summary>
public struct BitstreamSample
{
    public long Pos, Index;
    public int Len, Epoch;
    public bool Key;
}

/// <summary>
/// [PromptFlow Studio v17.62.0] 🎞 무할당 1080p 압축 NAL 비트스트림 순환 링버퍼 엔진(<see cref="OnVideo"/> · <see cref="OnAudio"/> · <see cref="Extract"/> 무할당).
///   ▸ 기록: v17.49 인코더 탭(<see cref="IEncodedStreamTap"/>) Annex-B 접근 단위 → NAL 분해 → SPS/PPS 는 구성 슬롯(변경 시 에포크 +1) · AUD/SEI 제외 · 슬라이스는 4 바이트 길이 접두(AVCC)로
///     32 MB 고정 바이트 풀에 연속 기록(경계 넘침 시 패딩 후 0 으로 감음). 원시 AAC 프레임도 같은 풀. 디스크립터 링(영상 8 192 · 음성 4 096 · IDR 색인 1 024)은 30 s 보존 + 풀 덮어쓰기 시 축출.
///   ▸ 추출: 요청 길이 시작점에 최인접(동일 에포크) IDR 을 찾아 재인코딩 없이 ftyp/moov(v17.49 <see cref="FragmentedMp4Muxer.WriteInit"/> · 원본 SPS/PPS avcC) + GOP 단위 moof/mdat 를
///     사전 할당 아레나에 조립 — mdat 은 링 스팬을 연속 구간 단위로 그대로 복사(비트 동일 무손실). 영상/음성은 세션 시작 기준 인덱스로 정렬(음성 = round(v·48 000 / (fps·1 024))).
/// </summary>
public sealed class CompressedBitstreamRollingBufferEngine : IEncodedStreamTap
{
    public const int PoolBytes = 32 * 1024 * 1024, VideoSlots = 8192, AudioSlots = 4096, KeySlots = 1024, RetainSec = 30, MaxClipSec = 30, BoxCap = 1024 * 1024;
    public const int AudioRate = FragmentedMp4Muxer.AudioRate, AacFrame = FragmentedMp4Muxer.AacFrame, MaxSampleBytes = PoolBytes / 4, MaxFragFrames = 256;

    private readonly object _lock = new();
    private readonly byte[] _pool = new byte[PoolBytes];
    private readonly byte[] _arena = new byte[PoolBytes + BoxCap];
    private readonly BitstreamSample[] _v = new BitstreamSample[VideoSlots], _a = new BitstreamSample[AudioSlots];
    private readonly long[] _keys = new long[KeySlots];
    private readonly byte[] _sps = new byte[256], _pps = new byte[256];
    private int _spsLen, _ppsLen, _epoch, _clipLen;
    private long _wpos, _vHead, _vTail, _aHead, _aTail, _kHead, _kTail;

    public int Width { get; private set; } = 1080;
    public int Height { get; private set; } = 1920;
    public int Fps { get; private set; } = 60;
    public int Epoch => _epoch;
    public long BytesWritten { get; private set; }
    public long VideoFrames { get; private set; }
    public long AudioFrames { get; private set; }
    public long Evicted { get; private set; }
    public long Oversize { get; private set; }
    public long Clips { get; private set; }
    public long ClipFailures { get; private set; }
    public double LastRemuxMs { get; private set; }
    public double MaxRemuxMs { get; private set; }
    public double LastRecordUs { get; private set; }
    public long LastClipStart { get; private set; } = -1;
    public long LastClipEnd { get; private set; } = -1;
    public int LastClipFrames { get; private set; }
    public int LastClipAudio { get; private set; }
    public int LastClipFragments { get; private set; }
    public double LastClipSec => Fps > 0 ? (double)LastClipFrames / Fps : 0;
    public string? LastFailure { get; private set; }
    public bool HasConfig => _spsLen > 0 && _ppsLen > 0;
    /// <summary>직전 추출 클립(다음 추출 전까지 유효).</summary>
    public ReadOnlySpan<byte> LastClip => _arena.AsSpan(0, _clipLen);
    public int LastClipBytes => _clipLen;

    public int VideoCount { get { lock (_lock) return (int)(_vTail - _vHead); } }
    public int AudioCount { get { lock (_lock) return (int)(_aTail - _aHead); } }
    public long OldestVideo { get { lock (_lock) return _vHead; } }
    public long NewestVideo { get { lock (_lock) return _vTail - 1; } }
    public double BufferedSec { get { lock (_lock) return Fps > 0 ? (double)(_vTail - _vHead) / Fps : 0; } }
    /// <summary>링 점유 바이트(가장 오래된 유효 샘플 ~ 기록 위치).</summary>
    public long OccupiedBytes
    {
        get
        {
            lock (_lock)
            {
                var oldest = _wpos;
                if (_vTail > _vHead) oldest = Math.Min(oldest, _v[_vHead % VideoSlots].Pos);
                if (_aTail > _aHead) oldest = Math.Min(oldest, _a[_aHead % AudioSlots].Pos);
                return Math.Min(PoolBytes, _wpos - oldest);
            }
        }
    }
    public double Occupancy => OccupiedBytes / (double)PoolBytes;
    public int KeyCount { get { lock (_lock) return (int)(_kTail - _kHead); } }
    /// <summary>i = 0 이 최신 IDR 의 영상 인덱스.</summary>
    public long KeyAt(int i) { lock (_lock) return (uint)i < (uint)(_kTail - _kHead) ? _keys[(_kTail - 1 - i) % KeySlots] : -1; }

    // ══════════════════════ 기록(IEncodedStreamTap · 무할당) ══════════════════════
    public void OnSessionStart(int width, int height, int fps)
    {
        lock (_lock)
        {
            Width = width; Height = height; Fps = Math.Max(1, fps);
            _vHead = _vTail = _aHead = _aTail = _kHead = _kTail = 0;
            _spsLen = _ppsLen = 0; _epoch++;
        }
    }

    public void OnVideo(ReadOnlySpan<byte> annexB, bool key)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var len = 0; var rest = annexB;
            while (FragmentedMp4Muxer.NextNal(ref rest, out var nal))
            {
                if (nal.IsEmpty) continue;
                var type = nal[0] & 31;
                if (type == 7) { Config(nal, _sps, ref _spsLen); continue; }
                if (type == 8) { Config(nal, _pps, ref _ppsLen); continue; }
                if (type is 9 or 6) continue;
                if (type == 5) key = true;
                len += 4 + nal.Length;
            }
            if (len == 0) return;
            if (len > MaxSampleBytes) { Oversize++; return; }
            var pos = Reserve(len);
            var dst = _pool.AsSpan((int)(pos % PoolBytes), len);
            rest = annexB; var o = 0;
            while (FragmentedMp4Muxer.NextNal(ref rest, out var nal))
            {
                if (nal.IsEmpty) continue;
                var type = nal[0] & 31;
                if (type is 7 or 8 or 9 or 6) continue;
                BinaryPrimitives.WriteInt32BigEndian(dst[o..], nal.Length);
                nal.CopyTo(dst[(o + 4)..]);
                o += 4 + nal.Length;
            }
            if (_vTail - _vHead >= VideoSlots) _vHead++;
            ref var s = ref _v[_vTail % VideoSlots];
            s.Pos = pos; s.Len = len; s.Index = _vTail; s.Key = key; s.Epoch = _epoch;
            if (key) { if (_kTail - _kHead >= KeySlots) _kHead++; _keys[_kTail % KeySlots] = _vTail; _kTail++; }
            _vTail++; VideoFrames++;
            var keep = (long)RetainSec * Fps;
            while (_vTail - _vHead > keep) _vHead++;
            while (_kTail > _kHead && _keys[_kHead % KeySlots] < _vHead) _kHead++;
        }
        LastRecordUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void OnAudio(ReadOnlySpan<byte> aacRaw)
    {
        if (aacRaw.IsEmpty) return;
        lock (_lock)
        {
            if (aacRaw.Length > MaxSampleBytes) { Oversize++; return; }
            var pos = Reserve(aacRaw.Length);
            aacRaw.CopyTo(_pool.AsSpan((int)(pos % PoolBytes)));
            if (_aTail - _aHead >= AudioSlots) _aHead++;
            ref var s = ref _a[_aTail % AudioSlots];
            s.Pos = pos; s.Len = aacRaw.Length; s.Index = _aTail; s.Key = true; s.Epoch = _epoch;
            _aTail++; AudioFrames++;
            var keep = (long)RetainSec * AudioRate / AacFrame + 1;
            while (_aTail - _aHead > keep) _aHead++;
        }
    }

    private void Config(ReadOnlySpan<byte> nal, byte[] slot, ref int len)
    {
        if (nal.Length > slot.Length) return;
        if (len == nal.Length && nal.SequenceEqual(slot.AsSpan(0, len))) return;
        if (len > 0) _epoch++;
        nal.CopyTo(slot); len = nal.Length;
    }

    /// <summary>연속 영역 예약(풀 끝 넘침 시 패딩) · 덮어쓴 샘플 축출.</summary>
    private long Reserve(int len)
    {
        var off = _wpos % PoolBytes;
        if (off + len > PoolBytes) _wpos += PoolBytes - off;
        var pos = _wpos;
        _wpos += len; BytesWritten += len;
        var floor = _wpos - PoolBytes;
        while (_vTail > _vHead && _v[_vHead % VideoSlots].Pos < floor) { _vHead++; Evicted++; }
        while (_aTail > _aHead && _a[_aHead % AudioSlots].Pos < floor) { _aHead++; Evicted++; }
        while (_kTail > _kHead && _keys[_kHead % KeySlots] < _vHead) _kHead++;
        return pos;
    }

    /// <summary>진단 · 검증 — 영상 샘플(AVCC) 복사. 반환 = 길이(-1 = 축출/범위 밖).</summary>
    public int CopyVideoSample(long index, Span<byte> dst)
    {
        lock (_lock)
        {
            if (index < _vHead || index >= _vTail) return -1;
            ref readonly var s = ref _v[index % VideoSlots];
            if (dst.Length < s.Len) return -1;
            _pool.AsSpan((int)(s.Pos % PoolBytes), s.Len).CopyTo(dst);
            return s.Len;
        }
    }

    public static long AudioIndexFor(long videoIndex, int fps) => (videoIndex * AudioRate + fps * AacFrame / 2) / ((long)fps * AacFrame);

    // ══════════════════════ 무손실 리먹스(무할당) ══════════════════════
    /// <summary>최근 seconds 초 구간을 최인접 IDR 부터 fMP4 로 조립. 반환 = 바이트 수(0 = 실패 · <see cref="LastFailure"/>).</summary>
    public int Extract(double seconds)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _clipLen = 0;
            if (!HasConfig || _vTail - _vHead < 2) return Fail("버퍼 비어 있음(SPS/PPS · 프레임 부족)");
            var end = _vTail;
            var want = end - (long)Math.Round(Math.Clamp(seconds, 0.5, MaxClipSec) * Fps);
            var lo = Math.Max(_vHead, end - (long)MaxClipSec * Fps);
            long start = -1, best = long.MaxValue;
            for (var k = _kHead; k < _kTail; k++)
            {
                var idx = _keys[k % KeySlots];
                if (idx < lo || idx >= end - 1 || _v[idx % VideoSlots].Epoch != _epoch || _v[idx % VideoSlots].Index != idx) continue;
                var d = Math.Abs(idx - want);
                if (d < best) { best = d; start = idx; }
            }
            if (start < 0) return Fail("유효 IDR 없음");
            for (var i = start; i < end; i++) if (_v[i % VideoSlots].Epoch != _epoch) return Fail("구성(SPS) 변경 구간");
            var a0 = AudioIndexFor(start, Fps);
            var b = _arena.AsSpan();
            var n = FragmentedMp4Muxer.WriteInit(b, _sps.AsSpan(0, _spsLen), _pps.AsSpan(0, _ppsLen), Width, Height, Fps, out var mvhdAt, out var tkVAt, out var tkAAt, out var mehdAt);
            uint seq = 0; var audioN = 0; var frags = 0;
            var f = start;
            while (f < end)
            {
                // GOP 경계(키프레임 · 1 s 이상) 또는 256 프레임
                var g = f + 1;
                while (g < end && g - f < MaxFragFrames && !(_v[g % VideoSlots].Key && g - f >= Fps)) g++;
                var aFrom = Math.Max(AudioIndexFor(f, Fps), Math.Max(_aHead, a0));
                var aTo = Math.Min(AudioIndexFor(g, Fps), _aTail);
                var moof = FragmentedMp4Muxer.Open(b, ref n, "moof"u8);
                var mfhd = FragmentedMp4Muxer.Open(b, ref n, "mfhd"u8); FragmentedMp4Muxer.U32(b, ref n, 0); FragmentedMp4Muxer.U32(b, ref n, ++seq); FragmentedMp4Muxer.Close(b, mfhd, n);
                var traf = FragmentedMp4Muxer.Open(b, ref n, "traf"u8);
                var tfhd = FragmentedMp4Muxer.Open(b, ref n, "tfhd"u8); FragmentedMp4Muxer.U32(b, ref n, 0x020000); FragmentedMp4Muxer.U32(b, ref n, 1); FragmentedMp4Muxer.Close(b, tfhd, n);
                var tfdt = FragmentedMp4Muxer.Open(b, ref n, "tfdt"u8); FragmentedMp4Muxer.U32(b, ref n, 0x01000000); FragmentedMp4Muxer.U64(b, ref n, (ulong)((f - start) * 1000)); FragmentedMp4Muxer.Close(b, tfdt, n);
                var trun = FragmentedMp4Muxer.Open(b, ref n, "trun"u8); FragmentedMp4Muxer.U32(b, ref n, 0x000701); FragmentedMp4Muxer.U32(b, ref n, (uint)(g - f)); var vOffAt = n; FragmentedMp4Muxer.U32(b, ref n, 0);
                long vBytes = 0;
                for (var i = f; i < g; i++)
                {
                    ref readonly var s = ref _v[i % VideoSlots];
                    FragmentedMp4Muxer.U32(b, ref n, 1000); FragmentedMp4Muxer.U32(b, ref n, (uint)s.Len); FragmentedMp4Muxer.U32(b, ref n, s.Key ? 0x02000000u : 0x01010000u);
                    vBytes += s.Len;
                }
                FragmentedMp4Muxer.Close(b, trun, n); FragmentedMp4Muxer.Close(b, traf, n);
                var aOffAt = -1; long aBytes = 0;
                if (aTo > aFrom)
                {
                    traf = FragmentedMp4Muxer.Open(b, ref n, "traf"u8);
                    tfhd = FragmentedMp4Muxer.Open(b, ref n, "tfhd"u8); FragmentedMp4Muxer.U32(b, ref n, 0x020000); FragmentedMp4Muxer.U32(b, ref n, 2); FragmentedMp4Muxer.Close(b, tfhd, n);
                    tfdt = FragmentedMp4Muxer.Open(b, ref n, "tfdt"u8); FragmentedMp4Muxer.U32(b, ref n, 0x01000000); FragmentedMp4Muxer.U64(b, ref n, (ulong)((aFrom - a0) * AacFrame)); FragmentedMp4Muxer.Close(b, tfdt, n);
                    trun = FragmentedMp4Muxer.Open(b, ref n, "trun"u8); FragmentedMp4Muxer.U32(b, ref n, 0x000301); FragmentedMp4Muxer.U32(b, ref n, (uint)(aTo - aFrom)); aOffAt = n; FragmentedMp4Muxer.U32(b, ref n, 0);
                    for (var i = aFrom; i < aTo; i++) { var len = _a[i % AudioSlots].Len; FragmentedMp4Muxer.U32(b, ref n, AacFrame); FragmentedMp4Muxer.U32(b, ref n, (uint)len); aBytes += len; }
                    FragmentedMp4Muxer.Close(b, trun, n); FragmentedMp4Muxer.Close(b, traf, n);
                }
                FragmentedMp4Muxer.Close(b, moof, n);
                if (n + 8 + vBytes + aBytes > _arena.Length) return Fail("아레나 용량 초과");
                var moofLen = n - moof;
                BinaryPrimitives.WriteInt32BigEndian(b[vOffAt..], moofLen + 8);
                if (aOffAt >= 0) BinaryPrimitives.WriteInt32BigEndian(b[aOffAt..], (int)(moofLen + 8 + vBytes));
                BinaryPrimitives.WriteUInt32BigEndian(b[n..], (uint)(8 + vBytes + aBytes)); "mdat"u8.CopyTo(b[(n + 4)..]); n += 8;
                // 링 스팬 → mdat(연속 구간 병합 복사)
                n = CopyRuns(_v, VideoSlots, f, g, b, n);
                if (aTo > aFrom) n = CopyRuns(_a, AudioSlots, aFrom, aTo, b, n);
                audioN += (int)Math.Max(0, aTo - aFrom); frags++;
                f = g;
            }
            var frames = (int)(end - start);
            var ms = (uint)Math.Round(frames * 1000.0 / Fps);
            BinaryPrimitives.WriteUInt32BigEndian(b[(int)mvhdAt..], ms);
            BinaryPrimitives.WriteUInt32BigEndian(b[(int)tkVAt..], ms);
            BinaryPrimitives.WriteUInt32BigEndian(b[(int)mehdAt..], ms);
            BinaryPrimitives.WriteUInt32BigEndian(b[(int)tkAAt..], (uint)Math.Round(audioN * (double)AacFrame / AudioRate * 1000));
            _clipLen = n;
            LastClipStart = start; LastClipEnd = end - 1; LastClipFrames = frames; LastClipAudio = audioN; LastClipFragments = frags;
            Clips++; LastFailure = null;
        }
        var el = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastRemuxMs = el; if (el > MaxRemuxMs) MaxRemuxMs = el;
        return _clipLen;
    }

    private int CopyRuns(BitstreamSample[] ring, int slots, long from, long to, Span<byte> b, int n)
    {
        var i = from;
        while (i < to)
        {
            var p0 = ring[i % slots].Pos; var len = (long)ring[i % slots].Len;
            var j = i + 1;
            while (j < to && ring[j % slots].Pos == p0 + len && (p0 % PoolBytes) + len + ring[j % slots].Len <= PoolBytes) { len += ring[j % slots].Len; j++; }
            _pool.AsSpan((int)(p0 % PoolBytes), (int)len).CopyTo(b[n..]);
            n += (int)len;
            i = j;
        }
        return n;
    }

    private int Fail(string why) { ClipFailures++; LastFailure = why; return 0; }
}
