using System.Diagnostics;

namespace PromptFlowStudio.Services.LiveClipper;

public enum MediaKind : byte { Video, Audio }

/// <summary>v17.84.0 링 프레임 인덱스(절대 바이트 오프셋 · µs PTS).</summary>
public struct RingFrame
{
    public long Offset, PtsUs;
    public int Length;
    public MediaKind Kind;
    public bool Key, HasParams;
}

/// <summary>v17.84.0 키프레임(IDR) 헤더 인덱스 — 링 프레임 Seq 참조.</summary>
public struct KeyframeIndex
{
    public long Seq, PtsUs, Offset;
    public bool HasParams;
}

/// <summary>v17.84.0 슬라이스 결과 — [FirstSeq, LastSeq] 연속 구간(직전 IDR 시작).</summary>
public struct ClipSlice
{
    public long FirstSeq, LastSeq, StartPtsUs, EndPtsUs, Bytes, RequestedStartUs;
    public int VideoFrames, AudioFrames, Keyframes;
    public bool PrependParams;
    public readonly long DurationUs => EndPtsUs - StartPtsUs;
    public readonly bool IsEmpty => LastSeq < FirstSeq;
}

/// <summary>
/// [PromptFlow Studio v17.84.0] ⏪ 무할당 인메모리 롤링 링버퍼 — 최근 <see cref="WindowSec"/> 초 H.264 Annex-B AU · AAC 청크를 디스크 I/O 없이 순환 보관.
///   ▸ 바이트 링(1 회 할당) + 프레임 인덱스 링(2^n) + IDR 키프레임 인덱스 링 — 기록 시 시간(120 s) · 용량 초과분만 꼬리부터 축출.
///   ▸ 최신 SPS/PPS 캐시 → 인라인 파라미터 없는 IDR 에서 시작하는 슬라이스에도 선행 삽입 → 디코더 깨짐 0.
///   ▸ <see cref="TrySlice"/>: 요청 시작 타임코드 직전 IDR 부터 종료까지 → <see cref="CopyTo"/> 로 연속 memmove 추출.
/// </summary>
public sealed class RollingCircularVideoBufferEngine
{
    public const int WindowSec = 120, FrameCap = 16_384, KeyCap = 1_024, ParamCap = 256;
    public const long WindowUs = WindowSec * 1_000_000L;
    private const int FrameMask = FrameCap - 1, KeyMask = KeyCap - 1;

    private readonly object _gate = new();
    private byte[]? _ring;
    private int _cap;
    private readonly RingFrame[] _frames = new RingFrame[FrameCap];
    private readonly KeyframeIndex[] _keys = new KeyframeIndex[KeyCap];
    private readonly byte[] _params = new byte[ParamCap];
    private long _write, _head, _tail, _keyHead, _keyTail;
    private int _paramLen;

    /// <param name="maxKbps">보관 대상 최대 합산 비트레이트(영상+음성) — 바이트 링 = 120 s × maxKbps (+ 12.5 % 여유) · 첫 기록 시 1 회 지연 할당.</param>
    public RollingCircularVideoBufferEngine(int maxKbps = 1_200) => Configure(maxKbps);

    public int MaxKbps { get; private set; }
    public int Capacity => _cap;
    public bool Allocated => _ring is not null;

    /// <summary>보관 비트레이트 재설정(비핫패스) — 링 비움 · 버퍼는 다음 기록 시 재할당.</summary>
    public void Configure(int maxKbps)
    {
        lock (_gate)
        {
            MaxKbps = Math.Clamp(maxKbps, 64, 16_000);
            var bytes = (long)WindowSec * MaxKbps * 1000 / 8;
            var cap = (int)Math.Min(int.MaxValue - 64, bytes + bytes / 8);
            if (cap != _cap) { _ring = null; _cap = cap; }
            _write = _head = _tail = _keyHead = _keyTail = 0; _paramLen = 0;
        }
    }
    public long OldestSeq { get { lock (_gate) return _tail; } }
    public long NewestSeq { get { lock (_gate) return _head - 1; } }
    public int Frames { get { lock (_gate) return (int)(_head - _tail); } }
    public int Keyframes { get { lock (_gate) return (int)(_keyHead - _keyTail); } }
    public long UsedBytes { get { lock (_gate) return _head == _tail ? 0 : _write - _frames[_tail & FrameMask].Offset; } }
    public float Fill => UsedBytes / (float)_cap;
    public long TotalFrames { get; private set; }
    public long TotalBytes { get; private set; }
    public long Evicted { get; private set; }
    public long Rejected { get; private set; }
    public long Slices { get; private set; }
    public double LastWriteUs { get; private set; }
    public double MaxWriteUs { get; private set; }
    public bool HasParams => _paramLen > 0;

    public long OldestPtsUs { get { lock (_gate) return _head == _tail ? 0 : _frames[_tail & FrameMask].PtsUs; } }
    public long NewestPtsUs { get { lock (_gate) return _head == _tail ? 0 : _frames[(_head - 1) & FrameMask].PtsUs; } }
    public long SpanUs { get { lock (_gate) return _head == _tail ? 0 : _frames[(_head - 1) & FrameMask].PtsUs - _frames[_tail & FrameMask].PtsUs; } }

    public RingFrame Frame(long seq) { lock (_gate) return _frames[seq & FrameMask]; }

    public void Reset()
    {
        lock (_gate)
        {
            _write = _head = _tail = _keyHead = _keyTail = 0; _paramLen = 0;
            TotalFrames = TotalBytes = Evicted = Rejected = Slices = 0; LastWriteUs = MaxWriteUs = 0;
        }
    }

    // ══════════════════════ 기록(무할당 핫패스) ══════════════════════

    /// <summary>H.264 Annex-B AU 기록 — IDR/SPS/PPS 자동 판별. 반환 = 프레임 Seq(거부 = −1).</summary>
    public long WriteVideo(ReadOnlySpan<byte> annexB, long ptsUs)
    {
        Scan(annexB, out var idr, out var spsAt, out var spsLen, out var ppsAt, out var ppsLen);
        var hasParams = spsLen > 0 && ppsLen > 0;
        lock (_gate)
        {
            if (hasParams && spsLen + ppsLen + 8 <= ParamCap)
            {
                var p = _params.AsSpan();
                p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 1;
                annexB.Slice(spsAt, spsLen).CopyTo(p.Slice(4));
                var q = 4 + spsLen;
                p[q] = 0; p[q + 1] = 0; p[q + 2] = 0; p[q + 3] = 1;
                annexB.Slice(ppsAt, ppsLen).CopyTo(p.Slice(q + 4));
                _paramLen = q + 4 + ppsLen;
            }
            return Append(annexB, ptsUs, MediaKind.Video, idr, hasParams);
        }
    }

    /// <summary>AAC(ADTS/원시) 청크 기록. 반환 = 프레임 Seq(거부 = −1).</summary>
    public long WriteAudio(ReadOnlySpan<byte> aac, long ptsUs)
    {
        lock (_gate) return Append(aac, ptsUs, MediaKind.Audio, false, false);
    }

    private long Append(ReadOnlySpan<byte> data, long ptsUs, MediaKind kind, bool key, bool hasParams)
    {
        var t0 = Stopwatch.GetTimestamp();
        if (data.Length == 0 || data.Length > _cap / 4) { Rejected++; return -1; }
        var ring = _ring ??= new byte[_cap];
        // 축출: 프레임 링 포화 · 바이트 덮어쓰기 · 120 s 창 초과
        while (_head - _tail >= FrameCap) EvictOne();
        while (_head > _tail && _write + data.Length - _frames[_tail & FrameMask].Offset > ring.Length) EvictOne();
        while (_head > _tail && ptsUs - _frames[_tail & FrameMask].PtsUs > WindowUs) EvictOne();

        var at = (int)(_write % ring.Length);
        var first = Math.Min(data.Length, ring.Length - at);
        data.Slice(0, first).CopyTo(ring.AsSpan(at));
        if (first < data.Length) data.Slice(first).CopyTo(ring);

        var seq = _head++;
        ref var f = ref _frames[seq & FrameMask];
        f.Offset = _write; f.Length = data.Length; f.PtsUs = ptsUs; f.Kind = kind; f.Key = key; f.HasParams = hasParams;
        _write += data.Length;
        if (key)
        {
            if (_keyHead - _keyTail >= KeyCap) _keyTail++;
            ref var k = ref _keys[_keyHead++ & KeyMask];
            k.Seq = seq; k.PtsUs = ptsUs; k.Offset = f.Offset; k.HasParams = hasParams;
        }
        TotalFrames++; TotalBytes += data.Length;
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastWriteUs = us; if (us > MaxWriteUs) MaxWriteUs = us;
        return seq;
    }

    private void EvictOne()
    {
        _tail++; Evicted++;
        while (_keyHead > _keyTail && _keys[_keyTail & KeyMask].Seq < _tail) _keyTail++;
    }

    /// <summary>VCL 슬라이스 직전까지 NAL 헤더 스캔(페이로드 본문 미탐색).</summary>
    private static void Scan(ReadOnlySpan<byte> a, out bool idr, out int spsAt, out int spsLen, out int ppsAt, out int ppsLen)
    {
        idr = false; spsAt = spsLen = ppsAt = ppsLen = 0;
        var i = 0; var openAt = -1; var openType = 0;
        while (i + 3 < a.Length)
        {
            if (a[i] != 0 || a[i + 1] != 0) { i++; continue; }
            int sc;
            if (a[i + 2] == 1) sc = 3;
            else if (a[i + 2] == 0 && a[i + 3] == 1) sc = 4;
            else { i++; continue; }
            if (openAt >= 0) Close(openType, openAt, i, ref spsAt, ref spsLen, ref ppsAt, ref ppsLen);
            var h = i + sc;
            if (h >= a.Length) break;
            var type = a[h] & 0x1F;
            if (type == 5) { idr = true; return; }
            if (type == 1) return;
            openAt = h; openType = type;
            i = h + 1;
        }
        if (openAt >= 0) Close(openType, openAt, a.Length, ref spsAt, ref spsLen, ref ppsAt, ref ppsLen);
    }

    private static void Close(int type, int at, int end, ref int spsAt, ref int spsLen, ref int ppsAt, ref int ppsLen)
    {
        if (type == 7) { spsAt = at; spsLen = end - at; }
        else if (type == 8) { ppsAt = at; ppsLen = end - at; }
    }

    // ══════════════════════ 슬라이스 추출 ══════════════════════

    /// <summary>[startUs, endUs] 요청 → 직전 IDR 부터 endUs 이하 마지막 프레임까지. 링에 IDR 이 없으면 false.</summary>
    public bool TrySlice(long startUs, long endUs, out ClipSlice s)
    {
        s = default;
        lock (_gate)
        {
            if (_keyHead == _keyTail || endUs <= startUs) return false;
            // 직전 IDR: PTS ≤ start 인 마지막 키 — 없으면 가장 오래된 키(창 앞부분 잘림)
            var k = _keyTail;
            for (var j = _keyHead - 1; j >= _keyTail; j--)
                if (_keys[j & KeyMask].PtsUs <= startUs) { k = j; break; }
            ref readonly var key = ref _keys[k & KeyMask];
            if (key.PtsUs > endUs) return false;
            var last = key.Seq;
            for (var q = _head - 1; q >= key.Seq; q--)
                if (_frames[q & FrameMask].PtsUs <= endUs) { last = q; break; }
            s.FirstSeq = key.Seq; s.LastSeq = last; s.StartPtsUs = key.PtsUs; s.RequestedStartUs = startUs;
            s.EndPtsUs = _frames[last & FrameMask].PtsUs;
            s.PrependParams = !key.HasParams && _paramLen > 0;
            for (var q = key.Seq; q <= last; q++)
            {
                ref readonly var f = ref _frames[q & FrameMask];
                if (f.Kind == MediaKind.Video) { s.VideoFrames++; if (f.Key) s.Keyframes++; } else s.AudioFrames++;
            }
            ref readonly var lf = ref _frames[last & FrameMask];
            s.Bytes = lf.Offset + lf.Length - key.Offset + (s.PrependParams ? _paramLen : 0);
            Slices++;
            return true;
        }
    }

    /// <summary>슬라이스가 아직 링 안에 온전히 남아 있는지(축출 경합 검사).</summary>
    public bool IsResident(in ClipSlice s) { lock (_gate) return !s.IsEmpty && s.FirstSeq >= _tail && s.LastSeq < _head; }

    /// <summary>슬라이스 연속 바이트 추출(파라미터 선행 + 랩어라운드 2 분할 memmove). 반환 = 기록 바이트(축출됨/공간 부족 = −1).</summary>
    public int CopyTo(in ClipSlice s, Span<byte> dst)
    {
        lock (_gate)
        {
            var ring = _ring;
            if (ring is null || s.IsEmpty || s.FirstSeq < _tail || s.LastSeq >= _head || dst.Length < s.Bytes) return -1;
            var n = 0;
            if (s.PrependParams) { _params.AsSpan(0, _paramLen).CopyTo(dst); n = _paramLen; }
            ref readonly var ff = ref _frames[s.FirstSeq & FrameMask];
            ref readonly var lf = ref _frames[s.LastSeq & FrameMask];
            var len = (int)(lf.Offset + lf.Length - ff.Offset);
            var at = (int)(ff.Offset % ring.Length);
            var first = Math.Min(len, ring.Length - at);
            ring.AsSpan(at, first).CopyTo(dst.Slice(n));
            if (first < len) ring.AsSpan(0, len - first).CopyTo(dst.Slice(n + first));
            return n + len;
        }
    }

    /// <summary>최신 SPS/PPS(Annex-B) 복사 — 프레임 단위 추출 시 선행 삽입용. 반환 = 바이트(없음 = 0).</summary>
    public int CopyParams(Span<byte> dst)
    {
        lock (_gate)
        {
            if (_paramLen == 0 || dst.Length < _paramLen) return 0;
            _params.AsSpan(0, _paramLen).CopyTo(dst);
            return _paramLen;
        }
    }

    /// <summary>슬라이스 내 프레임 단위 추출(오프셋 기준 · 무할당).</summary>
    public int CopyFrame(long seq, Span<byte> dst)
    {
        lock (_gate)
        {
            var ring = _ring;
            if (ring is null || seq < _tail || seq >= _head) return -1;
            ref readonly var f = ref _frames[seq & FrameMask];
            if (dst.Length < f.Length) return -1;
            var at = (int)(f.Offset % ring.Length);
            var first = Math.Min(f.Length, ring.Length - at);
            ring.AsSpan(at, first).CopyTo(dst);
            if (first < f.Length) ring.AsSpan(0, f.Length - first).CopyTo(dst.Slice(first));
            return f.Length;
        }
    }
}
