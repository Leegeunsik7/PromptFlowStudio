using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.LiveRestream;

namespace PromptFlowStudio.Services.LiveArchive;

public enum ArchiveSegmentState : byte { Recording, Sealed, Uploading, Uploaded, Failed, Pruned, Recovered }

/// <summary>v17.87.0 아카이브 세그먼트 메타(링 슬롯 · 문자열은 세그먼트 개폐 시 1 회 생성).</summary>
public struct ArchiveSegment
{
    public int Index, Fragments, VideoSamples, AudioSamples, Keyframes;
    public long StartUs, DurationUs, Bytes, UploadedBytes, SealedAtUnixSec;
    public ulong Hash;
    public ArchiveSegmentState State;
    public StorageTier Tier;
    public string? Name, ObjectKey;
}

/// <summary>v17.87.0 세그먼트 저장소 — 기록 중 <c>.part</c> → 원자 확정(rename). 디스크/인메모리 구현.</summary>
public interface ISegmentStore
{
    Stream Create(int index, out string name);
    string Seal(string partName);
    Stream OpenRead(string name);
    void Truncate(string name, long length);
    bool Delete(string name);
    string[] Orphans();
    long FreeBytes { get; }
}

/// <summary>디스크 저장소 — 무버퍼 FileStream(조각 단위 단일 Write + Flush) · 확정은 File.Move 원자 rename.</summary>
public sealed class DiskSegmentStore : ISegmentStore
{
    public const string PartExt = ".mp4.part";

    public DiskSegmentStore(string root) { Root = root; Directory.CreateDirectory(root); }

    public string Root { get; }

    public Stream Create(int index, out string name)
    {
        name = Path.Combine(Root, "seg_" + index.ToString("D5", CultureInfo.InvariantCulture) + "_" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + PartExt);
        return new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.None);
    }

    public string Seal(string partName)
    {
        var final = partName.EndsWith(".part", StringComparison.Ordinal) ? partName.Substring(0, partName.Length - 5) : partName;
        if (!ReferenceEquals(final, partName)) File.Move(partName, final, false);
        return final;
    }

    public Stream OpenRead(string name) => new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    public void Truncate(string name, long length)
    {
        using var fs = new FileStream(name, FileMode.Open, FileAccess.Write, FileShare.Read);
        fs.SetLength(length);
    }

    public bool Delete(string name)
    {
        try { if (!File.Exists(name)) return false; File.Delete(name); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public string[] Orphans() => Directory.GetFiles(Root, "*" + PartExt);

    public long FreeBytes
    {
        get
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root)) ?? Root).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
        }
    }
}

/// <summary>인메모리 저장소(리허설 · 테스트) — 용량 사전 확보 MemoryStream(Write 무할당) · 가상 디스크 여유 = 기준 − 사용량.</summary>
public sealed class MemorySegmentStore : ISegmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MemoryStream> _files = new(StringComparer.Ordinal);

    public MemorySegmentStore(long simulatedFreeBytes = 12L << 30, int initialCapacity = 4 << 20)
    {
        SimulatedFreeBytes = simulatedFreeBytes; InitialCapacity = initialCapacity;
    }

    public long SimulatedFreeBytes { get; set; }
    public int InitialCapacity { get; set; }
    public int Count { get { lock (_gate) return _files.Count; } }

    public long UsedBytes
    {
        get { lock (_gate) { long n = 0; foreach (var kv in _files) n += kv.Value.Length; return n; } }
    }

    public long FreeBytes => Math.Max(0, SimulatedFreeBytes - UsedBytes);

    public Stream Create(int index, out string name)
    {
        name = "seg_" + index.ToString("D5", CultureInfo.InvariantCulture) + DiskSegmentStore.PartExt;
        var ms = new KeepOpenStream(InitialCapacity);
        lock (_gate) _files[name] = ms;
        return ms;
    }

    public string Seal(string partName)
    {
        var final = partName.EndsWith(".part", StringComparison.Ordinal) ? partName.Substring(0, partName.Length - 5) : partName;
        lock (_gate) { if (_files.Remove(partName, out var ms)) _files[final] = ms; }
        return final;
    }

    public Stream OpenRead(string name)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(name, out var ms)) throw new FileNotFoundException(name);
            return new MemoryStream(ms.GetBuffer(), 0, (int)ms.Length, false);
        }
    }

    public void Truncate(string name, long length) { lock (_gate) if (_files.TryGetValue(name, out var ms)) ms.SetLength(length); }

    public bool Delete(string name) { lock (_gate) return _files.Remove(name); }

    public string[] Orphans()
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var k in _files.Keys) if (k.EndsWith(".part", StringComparison.Ordinal)) n++;
            var r = new string[n]; n = 0;
            foreach (var k in _files.Keys) if (k.EndsWith(".part", StringComparison.Ordinal)) r[n++] = k;
            return r;
        }
    }

    /// <summary>테스트 · 충돌 재현용 원본 접근.</summary>
    public bool TryGet(string name, out ArraySegment<byte> bytes)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(name, out var ms)) { bytes = new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length); return true; }
            bytes = default; return false;
        }
    }

    public void Clear() { lock (_gate) _files.Clear(); }

    private sealed class KeepOpenStream : MemoryStream
    {
        public KeepOpenStream(int capacity) : base(capacity) { }
        protected override void Dispose(bool disposing) { }   // 레코더 Dispose 후에도 업로더가 열람
    }
}

/// <summary>
/// [PromptFlow Studio v17.87.0] 💾 무손실 세그먼트 라이브 레코더.
///   ▸ 입력: v17.83 송출 인코더 Annex-B AU(IDR 플래그) + AAC(ADTS) — <see cref="OnVideo"/>/<see cref="OnAudio"/> 무할당 사전 버퍼 적재.
///   ▸ 조각화: GOP(IDR→IDR) 단위로 moof+mdat 를 단일 연속 버퍼에 직렬화 → 1 회 Write + Flush(실시간 moov/mdat 인터리빙) —
///     프로세스 이상 종료 시에도 기록된 조각까지 재생 가능하며 <see cref="RecoverOrphans"/> 가 꼬리 미완 박스를 잘라 확정.
///   ▸ 분할: 경과 ≥ <see cref="SegmentUs"/>(5/15 분)인 첫 IDR 경계에서 세그먼트 원자 확정(.part → .mp4) → 다음 세그먼트 init(ftyp+moov) 개시.
///   ▸ 핫패스(AU/AAC 적재 · 조각 플러시) 할당 0 — 세그먼트 개폐(파일 핸들 · 이름) 만 5/15 분당 1 회.
/// </summary>
public sealed class ZeroAllocLiveSegmentRecordingEngine : IDisposable
{
    public const int VideoTs = 90_000, VideoTrack = 1, AudioTrack = 2, MaxSegments = 512, AacFrame = 1024;
    public const long FiveMinUs = 5 * 60 * 1_000_000L, FifteenMinUs = 15 * 60 * 1_000_000L, MaxFragmentUs = 4_000_000L, MinSegmentUs = 1_000_000L;
    public const uint SyncFlags = 0x02000000u, NonSyncFlags = 0x01010000u;
    private const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;

    private readonly object _gate = new();
    private readonly ArchiveSegment[] _segs = new ArchiveSegment[MaxSegments];
    private readonly byte[] _sps = new byte[256], _pps = new byte[128];
    private readonly int[] _stack = new int[16];
    private byte[] _v = new byte[2 << 20], _a = new byte[256 * 1024], _out = new byte[3 << 20];
    private int[] _vSize = new int[512], _aSize = new int[1024];
    private long[] _vPts = new long[512];
    private bool[] _vKey = new bool[512];
    private int _vN, _aN, _vLen, _aLen, _pos, _sp, _spsLen, _ppsLen, _rate = 48_000, _ch = 2, _total;
    private ushort _asc = 0x1190;
    private long _segStartPts = -1, _lastVideoPts, _avgFrameUs = 33_333, _segAudioSamples;
    private uint _seq;
    private Stream? _cur;
    private string? _curName;
    private ulong _hash;
    private bool _cutRequested;

    public ZeroAllocLiveSegmentRecordingEngine(ISegmentStore store) => Store = store;

    public ISegmentStore Store { get; set; }
    /// <summary>세그먼트 길이(µs · 기본 5 분 · 최소 1 s).</summary>
    public long SegmentUs { get => Volatile.Read(ref _segmentUs); set => Volatile.Write(ref _segmentUs, Math.Max(MinSegmentUs, value)); }
    private long _segmentUs = FiveMinUs;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    /// <summary>세그먼트 확정 콜백(레코더 락 내 호출 · 인덱스 · 비차단 필수).</summary>
    public Action<int>? OnSealed { get; set; }

    public bool Recording => _cur is not null;
    public int SegmentCount => Volatile.Read(ref _total);
    public long VideoFrames { get; private set; }
    public long AudioFrames { get; private set; }
    public long Fragments { get; private set; }
    public long BytesWritten { get; private set; }
    public long DroppedPreIdr { get; private set; }
    public long WriteFailures { get; private set; }
    public long Grows { get; private set; }
    public long Recovered { get; private set; }
    public double LastFlushUs { get; private set; }
    public double MaxFlushUs { get; private set; }
    public long CurrentPts => _lastVideoPts;
    public long CurrentSegmentUs => _segStartPts < 0 ? 0 : _lastVideoPts - _segStartPts;
    public string? LastError { get; private set; }

    /// <summary>링 슬롯 열람(최근 <see cref="MaxSegments"/> 개).</summary>
    public ref ArchiveSegment Segment(int index) => ref _segs[index % MaxSegments];

    /// <summary>링에 남아 있는 가장 오래된 인덱스.</summary>
    public int FirstIndex => Math.Max(0, SegmentCount - MaxSegments);

    // ══════════════════════ 입력(송출 스레드 · 무할당) ══════════════════════

    public void OnVideo(ReadOnlySpan<byte> annexB, bool idr, long ptsUs)
    {
        lock (_gate)
        {
            if (idr)
            {
                CaptureParams(annexB);
                if (_vN > 0) FlushFragment(ptsUs);
                if (_cur is not null && (_cutRequested || ptsUs - _segStartPts >= SegmentUs)) SealCore(ptsUs);
                if (_cur is null && !OpenCore(ptsUs)) return;
            }
            else if (_cur is null) { DroppedPreIdr++; return; }
            else if (_vN > 0 && ptsUs - _vPts[0] >= MaxFragmentUs) FlushFragment(ptsUs);   // 장 GOP 상한 — 세그먼트 경계는 여전히 IDR
            AppendVideo(annexB, idr, ptsUs);
        }
    }

    public void OnAudio(ReadOnlySpan<byte> adts, long ptsUs)
    {
        lock (_gate)
        {
            if (_cur is null || adts.Length < 8 || adts[0] != 0xFF || (adts[1] & 0xF0) != 0xF0) return;
            var hdr = (adts[1] & 1) == 1 ? 7 : 9;
            if (adts.Length <= hdr) return;
            if (_aN == 0) ParseAdts(adts);
            var raw = adts.Slice(hdr);
            Ensure(ref _a, _aLen + raw.Length);
            if (_aN == _aSize.Length) { Array.Resize(ref _aSize, _aN * 2); Grows++; }
            raw.CopyTo(_a.AsSpan(_aLen));
            _aLen += raw.Length;
            _aSize[_aN++] = raw.Length;
            AudioFrames++;
        }
    }

    /// <summary>다음 IDR 에서 세그먼트 강제 분할.</summary>
    public void RequestCut() { lock (_gate) _cutRequested = _cur is not null; }

    /// <summary>녹화 종료 — 잔여 조각 플러시 후 확정.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_cur is null) return;
            if (_vN > 0) FlushFragment(_lastVideoPts + _avgFrameUs);
            SealCore(_lastVideoPts + _avgFrameUs);
        }
    }

    // ══════════════════════ 조각 직렬화 ══════════════════════

    private void CaptureParams(ReadOnlySpan<byte> au)
    {
        var pos = 0;
        while (RtmpWire.NextNal(au, ref pos, out var nal))
        {
            if (nal.Length == 0) continue;
            var t = nal[0] & 0x1F;
            if (t == 7 && nal.Length <= _sps.Length) { nal.CopyTo(_sps); _spsLen = nal.Length; }
            else if (t == 8 && nal.Length <= _pps.Length) { nal.CopyTo(_pps); _ppsLen = nal.Length; }
        }
    }

    private void AppendVideo(ReadOnlySpan<byte> au, bool idr, long ptsUs)
    {
        var pos = 0; var start = _vLen;
        while (RtmpWire.NextNal(au, ref pos, out var nal))
        {
            if (nal.Length == 0) continue;
            var t = nal[0] & 0x1F;
            if (t is 7 or 8 or 9) continue;   // 파라미터 셋은 avcC · AUD 생략
            Ensure(ref _v, _vLen + nal.Length + 4);
            BinaryPrimitives.WriteInt32BigEndian(_v.AsSpan(_vLen), nal.Length);
            nal.CopyTo(_v.AsSpan(_vLen + 4));
            _vLen += nal.Length + 4;
        }
        if (_vLen == start) return;
        if (_vN == _vSize.Length) { Array.Resize(ref _vSize, _vN * 2); Array.Resize(ref _vPts, _vN * 2); Array.Resize(ref _vKey, _vN * 2); Grows++; }
        _vSize[_vN] = _vLen - start; _vPts[_vN] = ptsUs; _vKey[_vN] = idr;
        if (_vN > 0) { var d = ptsUs - _vPts[_vN - 1]; if (d > 0) _avgFrameUs = (_avgFrameUs * 7 + d) / 8; }
        _vN++;
        _lastVideoPts = ptsUs;
        VideoFrames++;
        ref var s = ref Segment(_total - 1);
        s.VideoSamples++; if (idr) s.Keyframes++;
        s.DurationUs = ptsUs - _segStartPts;
    }

    /// <summary>moof(mfhd · 비디오/오디오 traf) + mdat 를 단일 버퍼에 직렬화 → 1 회 Write + Flush.</summary>
    private void FlushFragment(long nextPts)
    {
        var t0 = Stopwatch.GetTimestamp();
        var bound = _vLen + _aLen + _vN * 12 + _aN * 8 + 512;
        if (_out.Length < bound) { _out = new byte[bound + bound / 4]; Grows++; }
        _pos = 0; _sp = 0;
        var moof = _pos;
        Begin(C('m', 'o', 'o', 'f'));
        Full(C('m', 'f', 'h', 'd'), 0, 0); U32(++_seq); End();
        // 비디오 traf
        Begin(C('t', 'r', 'a', 'f'));
        Full(C('t', 'f', 'h', 'd'), 0, 0x020000); U32(VideoTrack); End();
        Full(C('t', 'f', 'd', 't'), 1, 0); U64((ulong)((_vPts[0] - _segStartPts) * VideoTs / 1_000_000)); End();
        Full(C('t', 'r', 'u', 'n'), 0, 0x000701u); U32((uint)_vN);
        var vOff = _pos; U32(0);
        for (var i = 0; i < _vN; i++)
        {
            var next = i + 1 < _vN ? _vPts[i + 1] : Math.Max(nextPts, _vPts[i] + 1);
            U32((uint)Math.Max(1, (next - _vPts[i]) * VideoTs / 1_000_000)); U32((uint)_vSize[i]); U32(_vKey[i] ? SyncFlags : NonSyncFlags);
        }
        End(); End();
        var aOff = -1;
        if (_aN > 0)
        {
            Begin(C('t', 'r', 'a', 'f'));
            Full(C('t', 'f', 'h', 'd'), 0, 0x020008); U32(AudioTrack); U32(AacFrame); End();
            Full(C('t', 'f', 'd', 't'), 1, 0); U64((ulong)_segAudioSamples); End();
            Full(C('t', 'r', 'u', 'n'), 0, 0x000201u); U32((uint)_aN);
            aOff = _pos; U32(0);
            for (var i = 0; i < _aN; i++) U32((uint)_aSize[i]);
            End(); End();
        }
        End();
        U32((uint)(8 + _vLen + _aLen)); U32(C('m', 'd', 'a', 't'));
        Patch(vOff, _pos - moof); Blit(_v, _vLen);
        if (aOff >= 0) { Patch(aOff, _pos - moof); Blit(_a, _aLen); }

        ref var s = ref Segment(_total - 1);
        if (WriteOut(ref s))
        {
            s.Fragments++; s.AudioSamples += _aN; Fragments++;
        }
        _segAudioSamples += (long)_aN * AacFrame;
        _vN = _aN = _vLen = _aLen = 0;
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastFlushUs = us; if (us > MaxFlushUs) MaxFlushUs = us;
    }

    private bool WriteOut(ref ArchiveSegment s)
    {
        var span = _out.AsSpan(0, _pos);
        try
        {
            _cur!.Write(span);
            _cur.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException)
        {
            WriteFailures++; LastError = ex.Message;
            return false;
        }
        var h = _hash;
        for (var i = 0; i < span.Length; i++) h = (h ^ span[i]) * FnvPrime;
        _hash = h;
        s.Bytes += span.Length; BytesWritten += span.Length;
        return true;
    }

    // ══════════════════════ 세그먼트 개폐(5/15 분당 1 회) ══════════════════════

    private bool OpenCore(long ptsUs)
    {
        if (_spsLen < 4 || _ppsLen < 1) { DroppedPreIdr++; return false; }
        Stream s; string name;
        try { s = Store.Create(_total, out name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            WriteFailures++; LastError = ex.Message; DroppedPreIdr++;
            return false;
        }
        _cur = s; _curName = name; _hash = FnvOffset; _seq = 0;
        _segStartPts = ptsUs; _segAudioSamples = 0; _cutRequested = false;
        ref var seg = ref _segs[_total % MaxSegments];
        seg = new ArchiveSegment { Index = _total, StartUs = ptsUs, Name = name, State = ArchiveSegmentState.Recording, Tier = StorageTier.Hot };
        Volatile.Write(ref _total, _total + 1);
        WriteInit();
        WriteOut(ref seg);
        return true;
    }

    private void SealCore(long endPts)
    {
        var cur = _cur; var name = _curName;
        if (cur is null || name is null) return;
        _cur = null; _curName = null;
        ref var seg = ref Segment(_total - 1);
        seg.DurationUs = Math.Max(0, endPts - _segStartPts);
        seg.Hash = _hash;
        try { cur.Dispose(); seg.Name = Store.Seal(name); seg.State = ArchiveSegmentState.Sealed; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteFailures++; LastError = ex.Message; seg.State = ArchiveSegmentState.Failed;
        }
        seg.SealedAtUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _segStartPts = -1;
        var idx = seg.Index;
        if (seg.State == ArchiveSegmentState.Sealed)
            try { OnSealed?.Invoke(idx); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.OnSealed", ex); }
    }

    /// <summary>ftyp + moov(mvhd · 비디오 avc1/avcC · 오디오 mp4a/esds · mvex/trex).</summary>
    private void WriteInit()
    {
        _pos = 0; _sp = 0;
        Begin(C('f', 't', 'y', 'p')); U32(C('i', 's', 'o', '6')); U32(0x200); U32(C('i', 's', 'o', '6')); U32(C('i', 's', 'o', 'm')); U32(C('a', 'v', 'c', '1')); U32(C('m', 'p', '4', '1')); End();
        Begin(C('m', 'o', 'o', 'v'));
        Full(C('m', 'v', 'h', 'd'), 0, 0); U32(0); U32(0); U32(1000); U32(0); U32(0x00010000); U16(0x0100); U16(0); U32(0); U32(0); Matrix(); Zero(24); U32(AudioTrack + 1); End();
        Trak(VideoTrack, true);
        Trak(AudioTrack, false);
        Begin(C('m', 'v', 'e', 'x'));
        Trex(VideoTrack); Trex(AudioTrack);
        End();
        End();
    }

    private void Trak(int id, bool video)
    {
        Begin(C('t', 'r', 'a', 'k'));
        Full(C('t', 'k', 'h', 'd'), 0, 3); U32(0); U32(0); U32((uint)id); U32(0); U32(0); U32(0); U32(0);
        U16(0); U16(video ? 0 : 1); U16(video ? 0 : 0x0100); U16(0); Matrix();
        U32(video ? (uint)Width << 16 : 0); U32(video ? (uint)Height << 16 : 0); End();
        Begin(C('m', 'd', 'i', 'a'));
        Full(C('m', 'd', 'h', 'd'), 0, 0); U32(0); U32(0); U32(video ? (uint)VideoTs : (uint)_rate); U32(0); U16(0x55C4); U16(0); End();
        Full(C('h', 'd', 'l', 'r'), 0, 0); U32(0); U32(video ? C('v', 'i', 'd', 'e') : C('s', 'o', 'u', 'n')); U32(0); U32(0); U32(0);
        Ascii(video ? "PFS Live Archive Video" : "PFS Live Archive Audio"); Byte(0); End();
        Begin(C('m', 'i', 'n', 'f'));
        if (video) { Full(C('v', 'm', 'h', 'd'), 0, 1); U16(0); U16(0); U16(0); U16(0); End(); }
        else { Full(C('s', 'm', 'h', 'd'), 0, 0); U16(0); U16(0); End(); }
        Begin(C('d', 'i', 'n', 'f')); Full(C('d', 'r', 'e', 'f'), 0, 0); U32(1); Full(C('u', 'r', 'l', ' '), 0, 1); End(); End(); End();
        Begin(C('s', 't', 'b', 'l'));
        Full(C('s', 't', 's', 'd'), 0, 0); U32(1);
        if (video)
        {
            Begin(C('a', 'v', 'c', '1'));
            Zero(6); U16(1); U16(0); U16(0); Zero(12); U16(Width); U16(Height); U32(0x00480000); U32(0x00480000); U32(0); U16(1);
            const string name = "PFS Live H.264";
            Byte((byte)name.Length); Ascii(name); Zero(31 - name.Length); U16(0x0018); U16(0xFFFF);
            Begin(C('a', 'v', 'c', 'C'));
            Byte(1); Byte(_sps[1]); Byte(_sps[2]); Byte(_sps[3]); Byte(0xFF); Byte(0xE1);
            U16(_spsLen); Bytes(_sps.AsSpan(0, _spsLen)); Byte(1); U16(_ppsLen); Bytes(_pps.AsSpan(0, _ppsLen));
            if (_sps[1] is 100 or 110 or 122 or 144) { Byte(0xFD); Byte(0xF8); Byte(0xF8); Byte(0); }
            End();
            End();
        }
        else
        {
            Begin(C('m', 'p', '4', 'a'));
            Zero(6); U16(1); Zero(8); U16(_ch); U16(16); U16(0); U16(0); U32((uint)_rate << 16);
            Full(C('e', 's', 'd', 's'), 0, 0);
            Byte(0x03); Byte(25); U16(AudioTrack); Byte(0);
            Byte(0x04); Byte(17); Byte(0x40); Byte(0x15); Byte(0); U16(0); U32(160_000); U32(128_000);
            Byte(0x05); Byte(2); U16(_asc);
            Byte(0x06); Byte(1); Byte(2);
            End();
            End();
        }
        End();
        Full(C('s', 't', 't', 's'), 0, 0); U32(0); End();
        Full(C('s', 't', 's', 'c'), 0, 0); U32(0); End();
        Full(C('s', 't', 's', 'z'), 0, 0); U32(0); U32(0); End();
        Full(C('s', 't', 'c', 'o'), 0, 0); U32(0); End();
        End(); End(); End(); End();
    }

    private void Trex(int id) { Full(C('t', 'r', 'e', 'x'), 0, 0); U32((uint)id); U32(1); U32(0); U32(0); U32(0); End(); }

    /// <summary>ADTS 헤더로 오디오 파라미터 갱신(다음 세그먼트 init 반영 · 비핫패스 호출 가능).</summary>
    public void ConfigureAudio(ReadOnlySpan<byte> adts)
    {
        if (adts.Length < 7 || adts[0] != 0xFF) return;
        lock (_gate) ParseAdts(adts);
    }

    private void ParseAdts(ReadOnlySpan<byte> adts)
    {
        var aot = ((adts[2] >> 6) & 3) + 1; var sfi = (adts[2] >> 2) & 0xF; var ch = ((adts[2] & 1) << 2) | (adts[3] >> 6);
        _asc = (ushort)((aot << 11) | (sfi << 7) | (ch << 3));
        _rate = sfi switch { 3 => 48_000, 4 => 44_100, 5 => 32_000, 6 => 24_000, 8 => 16_000, _ => 48_000 };
        _ch = Math.Max(1, ch);
    }

    // ══════════════════════ 충돌 복구 ══════════════════════

    /// <summary>
    /// 기록 중 종료된 <c>.part</c> 파일 → 마지막 완결 조각(moof+mdat)까지 절단 → 확정. 반환 = 복구 파일 수.
    /// </summary>
    public int RecoverOrphans()
    {
        string[] orphans;
        try { orphans = Store.Orphans(); }
        catch (IOException) { return 0; }
        var n = 0;
        foreach (var name in orphans)
        {
            lock (_gate) if (string.Equals(name, _curName, StringComparison.Ordinal)) continue;
            try
            {
                byte[] buf;
                using (var s = Store.OpenRead(name)) { buf = new byte[s.Length]; s.ReadExactly(buf); }
                var valid = ValidLength(buf, out var frags);
                if (frags == 0) { Store.Delete(name); continue; }
                Store.Truncate(name, valid);
                var final = Store.Seal(name);
                lock (_gate)
                {
                    ref var seg = ref _segs[_total % MaxSegments];
                    ulong h = FnvOffset;
                    for (var i = 0; i < valid; i++) h = (h ^ buf[i]) * FnvPrime;
                    seg = new ArchiveSegment { Index = _total, Name = final, Bytes = valid, Fragments = frags, Hash = h, State = ArchiveSegmentState.Recovered, Tier = StorageTier.Hot, SealedAtUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    Volatile.Write(ref _total, _total + 1);
                }
                n++; Recovered++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex.Message; WriteFailures++;
            }
        }
        return n;
    }

    /// <summary>최상위 박스 순회 — init(ftyp+moov) 이후 moof 다음 mdat 가 완결된 지점까지의 길이.</summary>
    public static long ValidLength(ReadOnlySpan<byte> file, out int fragments)
    {
        fragments = 0;
        long valid = 0; var p = 0; var moov = false; var pendingMoof = false;
        while (p + 8 <= file.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p));
            var type = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p + 4));
            if (size < 8 || (long)p + size > file.Length) break;
            p += (int)size;
            if (type == C('m', 'o', 'o', 'v')) { moov = true; valid = p; }
            else if (type == C('m', 'o', 'o', 'f')) pendingMoof = moov;
            else if (type == C('m', 'd', 'a', 't') && pendingMoof) { pendingMoof = false; fragments++; valid = p; }
        }
        return valid;
    }

    /// <summary>전 조각 검증 — 각 moof 직후 mdat · trun 범위 ⊂ mdat · 조각 첫 비디오 샘플 IDR(키프레임 정렬).</summary>
    public static bool TryValidate(ReadOnlySpan<byte> file, out int fragments, out int videoSamples, out int idrFragments)
    {
        fragments = videoSamples = idrFragments = 0;
        var p = 0; var ftyp = false; var moov = false;
        while (p + 8 <= file.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p));
            var type = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p + 4));
            if (size < 8 || p + size > file.Length) return false;
            if (type == C('f', 't', 'y', 'p')) ftyp = p == 0;
            else if (type == C('m', 'o', 'o', 'v')) moov = ftyp;
            else if (type == C('m', 'o', 'o', 'f'))
            {
                var mdat = p + size;
                if (!moov || mdat + 8 > file.Length || BinaryPrimitives.ReadUInt32BigEndian(file.Slice(mdat + 4)) != C('m', 'd', 'a', 't')) return false;
                var mdatEnd = mdat + (int)BinaryPrimitives.ReadUInt32BigEndian(file.Slice(mdat));
                if (mdatEnd > file.Length) return false;
                if (!WalkMoof(file, p, size, mdat + 8, mdatEnd, ref videoSamples, out var idr)) return false;
                fragments++; if (idr) idrFragments++;
            }
            p += size;
        }
        return p == file.Length && moov && fragments > 0;
    }

    private static bool WalkMoof(ReadOnlySpan<byte> file, int moofStart, int moofSize, int dataStart, int dataEnd, ref int videoSamples, out bool firstIdr)
    {
        firstIdr = false;
        var moof = file.Slice(moofStart + 8, moofSize - 8);
        var q = 0;
        while (q + 8 <= moof.Length)
        {
            var sz = (int)BinaryPrimitives.ReadUInt32BigEndian(moof.Slice(q));
            if (sz < 8 || q + sz > moof.Length) return false;
            if (BinaryPrimitives.ReadUInt32BigEndian(moof.Slice(q + 4)) == C('t', 'r', 'a', 'f'))
            {
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
                        var o = 8; long data = moofStart;
                        if ((flags & 1) != 0) { data += BinaryPrimitives.ReadInt32BigEndian(b.Slice(o)); o += 4; }
                        if ((flags & 4) != 0) o += 4;
                        long sum = 0;
                        for (var i = 0; i < n; i++)
                        {
                            if ((flags & 0x100) != 0) o += 4;
                            if ((flags & 0x200) != 0) { sum += BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o)); o += 4; }
                            if ((flags & 0x400) != 0) o += 4;
                            if ((flags & 0x800) != 0) o += 4;
                        }
                        if (data < dataStart || data + sum > dataEnd) return false;
                        if (track == VideoTrack)
                        {
                            videoSamples += n;
                            if (n > 0 && data + 5 <= dataEnd) firstIdr = (file[(int)data + 4] & 0x1F) == 5;
                        }
                    }
                    t += bs;
                }
            }
            q += sz;
        }
        return true;
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_cur is not null) { try { _cur.Dispose(); } catch (IOException) { } _cur = null; _curName = null; }
            Array.Clear(_segs);
            _total = 0; _vN = _aN = _vLen = _aLen = 0; _segStartPts = -1; _lastVideoPts = 0; _cutRequested = false;
            VideoFrames = AudioFrames = Fragments = BytesWritten = DroppedPreIdr = WriteFailures = Recovered = 0;
            LastFlushUs = MaxFlushUs = 0; LastError = null;
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.Recorder.Dispose", ex); }
    }

    // ══════════════════════ 박스 라이터 ══════════════════════

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

    public static string StateName(ArchiveSegmentState s) => s switch
    {
        ArchiveSegmentState.Recording => "녹화 중", ArchiveSegmentState.Sealed => "확정", ArchiveSegmentState.Uploading => "업로드 중", ArchiveSegmentState.Uploaded => "보관 완료",
        ArchiveSegmentState.Pruned => "로컬 회수", ArchiveSegmentState.Recovered => "복구", _ => "실패",
    };
}
