using System.Diagnostics;

namespace PromptFlowStudio.Services.CopyrightCompliance;

/// <summary>v17.91.0 Content ID 침해 구간 1 건(값 형식 · 타임코드 10 ms 정밀도).</summary>
public struct InfringementSegment
{
    public int StartMs;
    public int EndMs;
    public int Track;
    public float Risk;
    public int Votes;
    /// <summary>정렬 오프셋(샘플) — 기준 곡 샘플 = 질의 샘플 + 오프셋(프레임 해상도 · 믹서가 상호상관으로 샘플 정밀화).</summary>
    public int RefOffsetSamples;
}

/// <summary>
/// [PromptFlow Studio v17.91.0] 🛡️ 무할당 음향 지문 &amp; Content ID 위험도 분석기(외부 API 0 · <see cref="Feed"/> · <see cref="Finish"/> 무할당).
///   ▸ 추출: 48 kHz 모노 PCM → 1024 점 Hann 창 · 800 샘플 홉(16.7 ms) 반복 radix-2 FFT → 12 로그 대역(234 Hz ~ 12 kHz) 대역별 국소 최대 상위 2 빈 =
///     컨스텔레이션 랜드마크(대역 ±6 빈 주변 평균 대비 돌출 ≥ 3× · −78 dBFS 절대/프레임 최대 −40 dB 상대 바닥 · 포물선 보간 1/16 빈).
///     DB 내 동일 키 &gt; 24 개 해시는 스톱워드로 제외.
///   ▸ 해시: 앵커 랜드마크(프레임 t · 빈 f1) × 타깃 존(t+1..t+3 · ±3 대역 · 빈 f2 · 같은 빈 지속음 쌍 제외) → (f1 · f2 · Δt) 18 bit 키.
///   ▸ DB: 모의 상용 음원 3 곡(8 s · 결정적 합성 8 분 멜로디 · 16 분 아르페지오 · 베이스 · 하이햇) 지문을 2^19 개방 주소 테이블(키 · 곡 · 프레임)에 생성자에서 적재.
///   ▸ 매칭: 0.5 s(30 프레임) 창마다 곡별 오프셋(기준 프레임 − 질의 프레임) 히스토그램 투표 → 인접 2 빈 균등 기대치 초과 득표 · 커버리지(÷ 정렬 구간 DB 해시) × 정밀도(÷ 질의 해시) → 위험도 0~100.
///     ≥ 70 창을 병합(같은 곡 · 최대 2 창(1 s) 공백 허용) · 인접 창의 동일 오프셋 득표로 경계 확장 → 첫/끝 득표 프레임 기준 Start/End 10 ms 반올림.
/// </summary>
public sealed class AcousticFingerprintContentIdAnalyzerEngine
{
    public const int Fs = 48000, FftSize = 1024, Hop = 800, Bands = 12, PeaksPerBand = 2, Slots = Bands * PeaksPerBand, FanFrames = 3, WindowFrames = 30;
    public const int RefTracks = 3, RefSeconds = 8, RefFrames = RefSeconds * Fs / Hop, MaxSeconds = 120;
    public const int MaxFrames = MaxSeconds * Fs / Hop, MaxWindows = MaxFrames / WindowFrames + 1, MaxSegments = 16, MaxBin = 255;
    public const float RiskThreshold = 70f, FullScaleRatio = 0.25f, FullScalePrecision = 0.05f;
    private const int TableBits = 19, TableSize = 1 << TableBits, MinVotes = 6, EdgeVotes = 3, StopFanout = 24, ZoneBands = 3, MaxGapWindows = 2;
    private const float AbsFloor = 1e-3f, RelFloor = 1e-4f;
    private const int OffsetBins = RefFrames + WindowFrames;

    /// <summary>대역 경계(FFT 빈) — 46.875 Hz/빈.</summary>
    public static readonly int[] BandEdges = { 5, 7, 10, 14, 19, 26, 35, 48, 65, 90, 125, 175, 255 };
    public static readonly string[] TrackNames = { "Neon Skyline (모의 레이블 A)", "Midnight Groove (모의 레이블 B)", "Summer Anthem (모의 레이블 C)" };

    private readonly float[] _re = new float[FftSize], _im = new float[FftSize], _win = new float[FftSize], _mag = new float[FftSize / 2];
    private readonly float[] _cos = new float[FftSize / 2], _sin = new float[FftSize / 2];
    private readonly int[] _rev = new int[FftSize];
    private readonly float[] _buf = new float[FftSize];
    private readonly short[] _ring = new short[(FanFrames + 1) * Slots];
    private readonly short[] _peakQ = new short[MaxFrames * Slots];   // 프레임별 랜드마크(빈 × 16 · −1 = 없음)
    private readonly int[] _keys = new int[TableSize];
    private readonly short[] _tTrack = new short[TableSize], _tFrame = new short[TableSize];
    private readonly int[] _votes = new int[RefTracks * OffsetBins], _first = new int[RefTracks * OffsetBins], _last = new int[RefTracks * OffsetBins];
    private readonly float[] _wRisk = new float[MaxWindows];
    private readonly int[] _wTrack = new int[MaxWindows], _wOffset = new int[MaxWindows], _wFirst = new int[MaxWindows], _wLast = new int[MaxWindows], _wVotes = new int[MaxWindows], _wHashes = new int[MaxWindows];
    private readonly InfringementSegment[] _segs = new InfringementSegment[MaxSegments];
    private readonly int[] _trackVotes = new int[RefTracks];
    private readonly int[] _refPrefix = new int[RefTracks * (RefFrames + 1)];   // 곡별 기준 프레임 해시 누적 수(커버리지 분모)
    private readonly int[] _hist = new int[MaxBin + 1];
    private readonly int[] _histQ = new int[MaxBin + 1];
    private int _fill, _frames, _anchor, _window, _windowHashes, _segCount, _indexTrack = -1;
    private long _frameTicks;

    public AcousticFingerprintContentIdAnalyzerEngine()
    {
        var bits = 0;
        while ((1 << bits) < FftSize) bits++;
        for (var i = 0; i < FftSize; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
            _win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / FftSize);
        }
        for (var i = 0; i < FftSize / 2; i++) { _cos[i] = MathF.Cos(2f * MathF.PI * i / FftSize); _sin[i] = -MathF.Sin(2f * MathF.PI * i / FftSize); }

        var pcm = new float[RefSeconds * Fs];
        for (var t = 0; t < RefTracks; t++)
        {
            SynthesizeReference(t, pcm, 0);
            _indexTrack = t;
            BeginRun();
            Feed(pcm);
            FlushAnchors();
        }
        _indexTrack = -1;
        for (var t = 0; t < RefTracks; t++)
            for (var f = 0; f < RefFrames; f++) _refPrefix[t * (RefFrames + 1) + f + 1] += _refPrefix[t * (RefFrames + 1) + f];
        BeginRun();
    }

    public int IndexedHashes { get; private set; }
    public int TableDropped { get; private set; }
    public int Frames => _frames;
    public int Windows => _window + (_frames > 0 ? 1 : 0);
    public int SegmentCount => _segCount;
    public ref readonly InfringementSegment Segment(int i) => ref _segs[i];
    public ReadOnlySpan<InfringementSegment> Segments => _segs.AsSpan(0, _segCount);
    public float WindowRisk(int w) => _wRisk[w];
    public int WindowTrack(int w) => _wTrack[w];
    public int WindowVotes(int w) => _wVotes[w];
    public int WindowHashes(int w) => _wHashes[w];
    public float RiskIndex { get; private set; }
    public int MatchedTrack { get; private set; } = -1;
    public long QueryHashes { get; private set; }
    /// <summary>DB 내 동일 키 &gt; 24 개(정상음 · 반복 화성 등 저정보 해시) — 스톱워드로 제외.</summary>
    public long StopHashes { get; private set; }
    public long Analyses { get; private set; }
    public double LastAnalyzeMs { get; private set; }
    /// <summary>프레임 1 개(FFT · 랜드마크 · 해시 매칭) 평균 처리 시간(µs).</summary>
    public double FrameUs => _frames == 0 ? 0 : _frameTicks * 1_000_000.0 / Stopwatch.Frequency / _frames;
    public static int WindowMs => WindowFrames * Hop * 1000 / Fs;

    // ══════════════════════ 스트림 API(무할당) ══════════════════════

    /// <summary>클립 전체 분석 = 리셋 → 공급 → 종결.</summary>
    public float Analyze(ReadOnlySpan<float> pcm)
    {
        var t0 = Stopwatch.GetTimestamp();
        BeginRun();
        Feed(pcm);
        Finish();
        LastAnalyzeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return RiskIndex;
    }

    public void BeginRun()
    {
        _fill = _frames = _anchor = _window = _windowHashes = _segCount = 0;
        _frameTicks = 0;
        QueryHashes = 0;
        RiskIndex = 0;
        MatchedTrack = -1;
        StopHashes = 0;
        Array.Clear(_votes);
        Array.Clear(_trackVotes);
        Array.Fill(_ring, (short)-1);
    }

    /// <summary>임의 길이 PCM 공급 — 800 샘플마다 1 프레임(16.7 ms) 처리.</summary>
    public void Feed(ReadOnlySpan<float> pcm)
    {
        var i = 0;
        while (i < pcm.Length)
        {
            var take = Math.Min(FftSize - _fill, pcm.Length - i);
            pcm.Slice(i, take).CopyTo(_buf.AsSpan(_fill));
            _fill += take;
            i += take;
            if (_fill < FftSize) break;
            if (_frames >= MaxFrames) return;
            var t0 = Stopwatch.GetTimestamp();
            ProcessFrame();
            _frameTicks += Stopwatch.GetTimestamp() - t0;
            _buf.AsSpan(Hop, FftSize - Hop).CopyTo(_buf);
            _fill = FftSize - Hop;
        }
    }

    /// <summary>잔여 앵커 해시 · 마지막 창 확정 · 침해 구간 병합.</summary>
    public void Finish()
    {
        FlushAnchors();
        if (_frames > 0) CloseWindow();
        BuildSegments();
    }

    private void FlushAnchors()
    {
        while (_anchor < _frames) EmitAnchor(_anchor++);
    }

    // ══════════════════════ 프레임 처리 ══════════════════════

    private void ProcessFrame()
    {
        for (var i = 0; i < FftSize; i++) { var j = _rev[i]; _re[j] = _buf[i] * _win[i]; _im[j] = 0; }
        for (var size = 2; size <= FftSize; size <<= 1)
        {
            var half = size >> 1;
            var step = FftSize / size;
            for (var s = 0; s < FftSize; s += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var c = _cos[k * step]; var sn = _sin[k * step];
                    var a = s + k; var b = a + half;
                    var tr = _re[b] * c - _im[b] * sn;
                    var ti = _re[b] * sn + _im[b] * c;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
            }
        }
        float frameMax = 0;
        for (var k = 0; k < FftSize / 2; k++) { var m = _re[k] * _re[k] + _im[k] * _im[k]; _mag[k] = m; if (m > frameMax) frameMax = m; }
        var absFloor = MathF.Max(AbsFloor, frameMax * RelFloor);   // −78 dBFS 절대 · 프레임 최대 −40 dB 상대(창 사이드로브 누설 배제)

        var slot = (_frames % (FanFrames + 1)) * Slots;
        var log = _frames * Slots;
        for (var b = 0; b < Bands; b++)
        {
            int lo = BandEdges[b], hi = BandEdges[b + 1], best = -1, second = -1;
            float max = 0, max2 = 0;
            for (var k = lo; k < hi; k++)
            {
                var m = _mag[k];
                if (m < _mag[k - 1] || m < _mag[k + 1]) continue;   // 국소 최대만
                if (m > max) { max2 = max; second = best; max = m; best = k; }
                else if (m > max2) { max2 = m; second = k; }
            }
            int nlo = Math.Max(1, lo - 6), nhi = Math.Min(MaxBin, hi + 6);
            float sum = 0;
            for (var k = nlo; k < nhi; k++) sum += _mag[k];
            var floor = MathF.Max(absFloor, 3f * sum / (nhi - nlo));   // 주변(대역 ±6 빈) 평균 대비 돌출 3×
            var q0 = Landmark(best, max, floor);
            var q1 = Landmark(second, max2, floor);
            _ring[slot + b * PeaksPerBand] = q0; _ring[slot + b * PeaksPerBand + 1] = q1;
            _peakQ[log + b * PeaksPerBand] = q0; _peakQ[log + b * PeaksPerBand + 1] = q1;
        }
        _frames++;
        while (_anchor <= _frames - 1 - FanFrames) EmitAnchor(_anchor++);
    }

    private void EmitAnchor(int a)
    {
        if (_indexTrack < 0)
        {
            var w = a / WindowFrames;
            while (w > _window) { CloseWindow(); _window++; }
        }
        var aSlot = (a % (FanFrames + 1)) * Slots;
        for (var l = 0; l < Slots; l++)
        {
            int f1 = _ring[aSlot + l];
            if (f1 < 0) continue;
            f1 >>= 4;
            var b = l / PeaksPerBand;
            for (var d = 1; d <= FanFrames && a + d < _frames; d++)
            {
                var tSlot = ((a + d) % (FanFrames + 1)) * Slots;
                var l2End = Math.Min(Bands, b + 1 + ZoneBands) * PeaksPerBand;
                for (var l2 = Math.Max(0, b - ZoneBands) * PeaksPerBand; l2 < l2End; l2++)
                {
                    int f2 = _ring[tSlot + l2];
                    if (f2 < 0 || Math.Abs((f2 >> 4) - f1) <= 1) continue;   // 지속음(같은 빈) 쌍 = 저정보
                    var key = (f1 | ((f2 >> 4) << 8) | (d << 16)) + 1;
                    if (_indexTrack >= 0) Insert(key, _indexTrack, a);
                    else Query(key, a);
                }
            }
        }
    }

    private short Landmark(int bin, float mag, float floor)
    {
        if (bin < 1 || bin >= MaxBin || mag <= floor) return -1;
        var l = MathF.Sqrt(_mag[bin - 1]); var c = MathF.Sqrt(mag); var r = MathF.Sqrt(_mag[bin + 1]);
        var den = l - 2 * c + r;
        var off = den < 0 ? Math.Clamp(0.5f * (l - r) / den, -0.5f, 0.5f) : 0f;
        return (short)MathF.Round((bin + off) * 16f);
    }

    private static int Slot(int key) => (int)(((uint)key * 2654435761u) >> (32 - TableBits));

    private void Insert(int key, int track, int frame)
    {
        if (IndexedHashes >= TableSize * 3 / 4) { TableDropped++; return; }
        var s = Slot(key);
        while (_keys[s] != 0) s = (s + 1) & (TableSize - 1);
        _keys[s] = key;
        _tTrack[s] = (short)track;
        _tFrame[s] = (short)frame;
        if (frame < RefFrames) _refPrefix[track * (RefFrames + 1) + frame + 1]++;
        IndexedHashes++;
    }

    private void Query(int key, int frame)
    {
        var s = Slot(key);
        var fan = 0;
        for (var p = s; _keys[p] != 0; p = (p + 1) & (TableSize - 1)) if (_keys[p] == key && ++fan > StopFanout) { StopHashes++; return; }
        QueryHashes++;
        _windowHashes++;
        if (fan == 0) return;
        var local = frame - _window * WindowFrames;
        for (; _keys[s] != 0; s = (s + 1) & (TableSize - 1))
        {
            if (_keys[s] != key) continue;
            var o = _tFrame[s] - local + WindowFrames - 1;
            if ((uint)o >= OffsetBins) continue;
            var idx = _tTrack[s] * OffsetBins + o;
            if (_votes[idx]++ == 0) _first[idx] = frame;
            _last[idx] = frame;
            _trackVotes[_tTrack[s]]++;
        }
    }

    /// <summary>창 확정(무할당) — 곡별 인접 2 오프셋 빈 최대 득표 → 위험도.</summary>
    private void CloseWindow()
    {
        var w = _window;
        if (w >= MaxWindows) return;
        int bestTrack = -1, bestBin = 0, bestVotes = 0;
        float bestExcess = 0;
        for (var t = 0; t < RefTracks; t++)
        {
            var row = t * OffsetBins;
            var bg = 2f * _trackVotes[t] / OffsetBins;   // 오프셋 균등 분포 가정 시 인접 2 빈 기대 득표
            for (var o = 0; o < OffsetBins - 1; o++)
            {
                var v = _votes[row + o] + _votes[row + o + 1];
                if (v - bg > bestExcess) { bestExcess = v - bg; bestVotes = v; bestTrack = t; bestBin = o; }
            }
        }
        _wHashes[w] = _windowHashes;
        _wVotes[w] = bestVotes;
        _wTrack[w] = bestTrack;
        if (bestTrack >= 0)
        {
            var i0 = bestTrack * OffsetBins + bestBin;
            var hasA = _votes[i0] > 0; var hasB = _votes[i0 + 1] > 0;
            _wFirst[w] = hasA && hasB ? Math.Min(_first[i0], _first[i0 + 1]) : hasA ? _first[i0] : _first[i0 + 1];
            _wLast[w] = hasA && hasB ? Math.Max(_last[i0], _last[i0 + 1]) : hasA ? _last[i0] : _last[i0 + 1];
            _wOffset[w] = bestBin - (WindowFrames - 1) - w * WindowFrames;   // 전역 오프셋 = 기준 프레임 − 질의 프레임
        }
        // 커버리지 = 정렬 오프셋 초과 득표 / 해당 기준 구간(30 프레임)에 실제 존재하는 DB 해시 수
        var span = 0;
        if (bestTrack >= 0)
        {
            var r0 = Math.Clamp(w * WindowFrames + _wOffset[w], 0, RefFrames);
            var r1 = Math.Clamp(w * WindowFrames + WindowFrames + _wOffset[w], 0, RefFrames);
            span = _refPrefix[bestTrack * (RefFrames + 1) + r1] - _refPrefix[bestTrack * (RefFrames + 1) + r0];
        }
        var ratio = bestExcess / Math.Max(span, 60);
        // 정밀도 = 초과 득표 / 질의 해시 — 잡음/보컬 랜드마크가 많은 창의 우연 정렬 억제
        var precision = bestExcess / Math.Max(_windowHashes, 1);
        var risk = bestVotes < MinVotes ? 0f : Math.Clamp(ratio / FullScaleRatio * 100f, 0f, 100f) * Math.Clamp(precision / FullScalePrecision, 0f, 1f);
        _wRisk[w] = risk;
        if (risk > RiskIndex) { RiskIndex = risk; MatchedTrack = bestTrack; }
        Array.Clear(_votes);
        Array.Clear(_trackVotes);
        _windowHashes = 0;
    }

    private void BuildSegments()
    {
        _segCount = 0;
        var windows = Math.Min(Windows, MaxWindows);
        var w = 0;
        while (w < windows && _segCount < MaxSegments)
        {
            if (_wRisk[w] < RiskThreshold) { w++; continue; }
            var track = _wTrack[w];
            int start = w, end = w;
            float peak = 0; var votes = 0;
            var k = w;
            while (k < windows)
            {
                if (_wRisk[k] >= RiskThreshold && _wTrack[k] == track) { end = k; peak = Math.Max(peak, _wRisk[k]); votes += _wVotes[k]; k++; continue; }
                var resume = -1;
                for (var g = 1; g <= MaxGapWindows && k + g < windows; g++)
                    if (_wRisk[k + g] >= RiskThreshold && _wTrack[k + g] == track) { resume = k + g; break; }
                if (resume < 0) break;
                k = resume;
            }
            var firstFrame = _wFirst[start];
            var lastFrame = _wLast[end];
            if (start > 0 && Edge(start - 1, track, _wOffset[start])) firstFrame = _wFirst[start - 1];
            if (end + 1 < windows && Edge(end + 1, track, _wOffset[end])) lastFrame = _wLast[end + 1];
            ref var s = ref _segs[_segCount++];
            s.Track = track;
            s.Risk = peak;
            s.Votes = votes;
            s.RefOffsetSamples = _wOffset[start] * Hop;
            s.StartMs = Round10((long)firstFrame * Hop * 1000 / Fs);
            s.EndMs = Round10(((long)lastFrame + FanFrames) * Hop * 1000 / Fs + FftSize * 1000 / Fs);
            w = end + 1;
        }
    }

    private bool Edge(int w, int track, int offset) => _wTrack[w] == track && _wVotes[w] >= EdgeVotes && Math.Abs(_wOffset[w] - offset) <= 1;

    public static int Round10(long ms) => (int)((ms + 5) / 10 * 10);

    /// <summary>침해 구간 내 랜드마크 빈 빈도 상위 N → Hz(포물선 보간 평균) — 믹서 노치 필터용. 반환 = 기록 수.</summary>
    public int CollectSegmentPeakHz(int segment, Span<float> hz)
    {
        if ((uint)segment >= (uint)_segCount || hz.IsEmpty) return 0;
        ref readonly var s = ref _segs[segment];
        var f0 = Math.Max(0, (int)((long)s.StartMs * Fs / 1000 / Hop));
        var f1 = Math.Min(_frames, (int)((long)s.EndMs * Fs / 1000 / Hop) + 1);
        Array.Clear(_hist);
        Array.Clear(_histQ);
        for (var f = f0; f < f1; f++)
            for (var l = 0; l < Slots; l++)
            {
                int q = _peakQ[f * Slots + l];
                if (q < 0) continue;
                var bin = Math.Min(MaxBin, (q + 8) >> 4);
                _hist[bin]++;
                _histQ[bin] += q;
            }
        var n = 0;
        while (n < hz.Length)
        {
            int best = -1, cnt = 2;
            for (var k = 0; k <= MaxBin; k++) if (_hist[k] > cnt) { cnt = _hist[k]; best = k; }
            if (best < 0) break;
            hz[n++] = _histQ[best] / (16f * _hist[best]) * Fs / FftSize;
            _hist[best] = 0;
        }
        return n;
    }

    // ══════════════════════ 모의 상용 음원(결정적 합성) ══════════════════════

    private static readonly int[] Pentatonic = { 0, 2, 4, 7, 9, 12, 14, 16, 19, 21, 24, 26 };
    private static readonly float[] RootHz = { 261.63f, 220.00f, 293.66f };
    private static readonly int[] EighthSamples = { Fs * 60 / 118 / 2, Fs * 60 / 96 / 2, Fs * 60 / 128 / 2 };

    /// <summary>모의 상용 곡(트랙 0~2) PCM 렌더 — dst[i] = 곡 시각 (refStartSample + i). 피크 ≈ 0.9.</summary>
    public static void SynthesizeReference(int track, Span<float> dst, int refStartSample)
    {
        var t = Math.Clamp(track, 0, RefTracks - 1);
        var eighth = EighthSamples[t];
        var root = RootHz[t];
        for (var i = 0; i < dst.Length; i++)
        {
            var n = refStartSample + i;
            var note = n / eighth;
            var inNote = n - note * eighth;
            var h = Hash((uint)(t * 7919 + note * 104729 + 17));
            var deg = Pentatonic[h % (uint)Pentatonic.Length];
            var f = root * MathF.Pow(2f, deg / 12f);
            var tn = inNote / (float)Fs;
            var env = MathF.Exp(-tn * 5f) * MathF.Min(1f, inNote / 96f);
            var mel = MathF.Sin(2f * MathF.PI * f * tn) + 0.45f * MathF.Sin(4f * MathF.PI * f * tn) + 0.2f * MathF.Sin(6f * MathF.PI * f * tn);
            var bar = note / 4;
            var bh = Hash((uint)(t * 31 + bar * 977 + 5));
            var fb = root * 0.25f * MathF.Pow(2f, Pentatonic[bh % 4u] / 12f);
            var tb = (n - bar * 4 * eighth) / (float)Fs;
            var bass = MathF.Sin(2f * MathF.PI * fb * tb) * MathF.Exp(-tb * 1.2f);
            var six = eighth / 2;
            var sn = n / six;
            var inSix = n - sn * six;
            var ah = Hash((uint)(t * 131 + sn * 7349 + 3));
            var fa = root * 2f * MathF.Pow(2f, Pentatonic[ah % (uint)Pentatonic.Length] / 12f);
            var ta = inSix / (float)Fs;
            var arp = MathF.Sin(2f * MathF.PI * fa * ta) * MathF.Exp(-ta * 14f) * MathF.Min(1f, inSix / 48f);
            var hat = inSix < 900 ? ((Hash((uint)n) & 0xFFFF) / 32768f - 1f) * 0.10f * (1f - inSix / 900f) : 0f;
            dst[i] = 0.36f * mel * env + 0.30f * bass + 0.26f * arp + hat;
        }
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
        return x;
    }
}
