using System.Diagnostics;
using System.Text.Json;

namespace PromptFlowStudio.Services.Neuro;

/// <summary>집단 공감 순간 — 미디어 시각 · 평균 몰입/흥분 · 공감도 · 반응 비율 · 지배 감정(리터럴).</summary>
public readonly record struct PeakMoment(double MediaTimeSec, float Engagement, float Arousal, float Synchrony, float Responding, float Score, string Emotion);

/// <summary>
/// [PromptFlow Studio v16.56.0] 🌡 집단 관객 감정 공감도 열지도 집계기(Pure BCL · 무할당 집계).
///   ▸ 병렬 수집: 관객 슬롯별 시퀀스 락(seqlock) 기록 — 다수 스레드/소켓이 락 없이 동시 보고 · 집계 측 일관 스냅샷 재시도.
///   ▸ 집계: 활성(3s 이내 · 신뢰도 ≥0.2) 평균/표준편차 · 공감도 = 0.5·감정 변화 방향 일치도(단위 벡터 평균 크기 × 변화 비율) + 0.5·응집도(중심 거리 역).
///   ▸ 곡선: 몰입/흥분/공감 링(600) · 피크: 집단 강도 EMA z-점수 ≥ 2 · 공감 ≥ 0.45 · 반응 ≥ 40% 구간의 극대점(5s 불응기) → 상한 링(64) + OnPeak.
///   ▸ 3D 감정 스펙트럼: 쾌적(16) × 흥분(16) 히스토그램 + 시간 축 32 슬라이스 볼륨 링 · 좌석(행×열) 몰입 열지도 · 텔레메트리 JSON 스냅샷.
/// </summary>
public sealed class CollectiveEmotionHeatmapAggregator
{
    public const int MaxViewers = 4096, Grid = 16, CurveLen = 600, VolumeDepth = 32, MaxPeaks = 64, SeatRows = 16, SeatCols = 32;
    public const long StaleMicros = 3_000_000;

    private readonly int[] _ver = new int[MaxViewers];
    private readonly float[] _e = new float[MaxViewers], _a = new float[MaxViewers], _v = new float[MaxViewers], _f = new float[MaxViewers], _c = new float[MaxViewers];
    private readonly float[] _pa = new float[MaxViewers], _pv = new float[MaxViewers];
    private readonly long[] _t = new long[MaxViewers];
    private readonly short[] _row = new short[MaxViewers], _col = new short[MaxViewers];
    private readonly bool[] _seen = new bool[MaxViewers];
    private readonly float[] _spectrum = new float[Grid * Grid], _volume = new float[Grid * Grid * VolumeDepth];
    private readonly float[] _seat = new float[SeatRows * SeatCols], _seatW = new float[SeatRows * SeatCols];
    private readonly float[] _curveE = new float[CurveLen], _curveA = new float[CurveLen], _curveS = new float[CurveLen];
    private readonly double[] _curveT = new double[CurveLen];
    private readonly PeakMoment[] _peaks = new PeakMoment[MaxPeaks];
    private readonly object _gate = new();
    private int _maxViewer = -1, _curveHead, _curveCount, _volHead, _peakHead, _peakCount, _active;
    private float _mE = 0.5f, _mA = 0.5f, _mV = 0.5f, _mF = 0.2f, _sdA, _sdV, _sync, _responding;
    private double _iMean = -1, _iVar = 0.01, _lastPeakT = double.NegativeInfinity, _candT;
    private bool _cand;
    private float _candScore, _candE, _candA, _candS, _candR;
    private long _reports, _aggs;
    private double _aggMicros;

    public event Action? OnChange;
    public event Action<PeakMoment>? OnPeak;

    public int ActiveViewers => Volatile.Read(ref _active);
    public float MeanEngagement => Volatile.Read(ref _mE);
    public float MeanArousal => Volatile.Read(ref _mA);
    public float MeanValence => Volatile.Read(ref _mV);
    public float MeanFear => Volatile.Read(ref _mF);
    public float ArousalSpread => Volatile.Read(ref _sdA);
    public float ValenceSpread => Volatile.Read(ref _sdV);
    public float Synchrony => Volatile.Read(ref _sync);
    public float Responding => Volatile.Read(ref _responding);
    public long Reports => Interlocked.Read(ref _reports);
    public long Aggregations => Interlocked.Read(ref _aggs);
    public double LastAggregateMicros => Volatile.Read(ref _aggMicros);
    public int PeakCount => Volatile.Read(ref _peakCount);
    public int CurveCount => Volatile.Read(ref _curveCount);
    public double MediaTimeSec { get; private set; }
    public string? LastAction { get; private set; }
    public ReadOnlySpan<float> Spectrum => _spectrum;
    public ReadOnlySpan<float> SeatHeat => _seat;

    /// <summary>집단 평균을 단일 감정 상태로(변조기 입력).</summary>
    public EmotionState CollectiveState => new(MeanEngagement, MeanArousal, MeanValence, MeanFear, ActiveViewers > 0 ? 1f : 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0);

    public void SetSeat(int viewer, int row, int col)
    {
        if ((uint)viewer >= MaxViewers) return;
        _row[viewer] = (short)Math.Clamp(row, 0, SeatRows - 1);
        _col[viewer] = (short)Math.Clamp(col, 0, SeatCols - 1);
    }

    /// <summary>관객 감정 보고(무할당 · 스레드 안전 seqlock).</summary>
    public void Report(int viewer, in EmotionState s, long tMicros)
    {
        if ((uint)viewer >= MaxViewers) return;
        Interlocked.Increment(ref _ver[viewer]);
        _e[viewer] = s.Engagement; _a[viewer] = s.Arousal; _v[viewer] = s.Valence; _f[viewer] = s.Fear; _c[viewer] = s.Confidence;
        Volatile.Write(ref _t[viewer], tMicros);
        Interlocked.Increment(ref _ver[viewer]);
        int m;
        while (viewer > (m = Volatile.Read(ref _maxViewer)) && Interlocked.CompareExchange(ref _maxViewer, viewer, m) != m) { }
        Interlocked.Increment(ref _reports);
    }

    private bool Read(int i, out float e, out float a, out float v, out float f, out float c, out long t)
    {
        for (var tries = 0; tries < 4; tries++)
        {
            var v0 = Volatile.Read(ref _ver[i]);
            if ((v0 & 1) != 0) continue;
            e = _e[i]; a = _a[i]; v = _v[i]; f = _f[i]; c = _c[i]; t = _t[i];
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _ver[i]) == v0) return true;
        }
        e = a = v = f = c = 0; t = 0;
        return false;
    }

    private static int Bin(float x) => Math.Clamp((int)(x * Grid), 0, Grid - 1);

    /// <summary>집계 1회(무할당) — 평균/분산 · 공감도 · 스펙트럼/볼륨 · 좌석 열지도 · 곡선 · 피크 탐지.</summary>
    public void Aggregate(long nowMicros, double mediaTimeSec)
    {
        var t0 = Stopwatch.GetTimestamp();
        PeakMoment? emitted = null;
        lock (_gate)
        {
            MediaTimeSec = mediaTimeSec;
            Array.Clear(_spectrum); Array.Clear(_seat); Array.Clear(_seatW);
            var max = Volatile.Read(ref _maxViewer);
            int n = 0, resp = 0;
            double se = 0, sa = 0, sv = 0, sf = 0, sa2 = 0, sv2 = 0, ux = 0, uy = 0;
            for (var i = 0; i <= max; i++)
            {
                if (!Read(i, out var e, out var a, out var v, out var f, out var c, out var t)) continue;
                if (t == 0 || nowMicros - t > StaleMicros || c < 0.2f) continue;
                n++;
                se += e; sa += a; sv += v; sf += f; sa2 += a * a; sv2 += v * v;
                if (a > 0.6f || e > 0.7f) resp++;
                _spectrum[Bin(a) * Grid + Bin(v)] += 1f;
                var si = _row[i] * SeatCols + _col[i];
                _seat[si] += e; _seatW[si] += 1f;
                if (_seen[i])
                {
                    float dx = v - _pv[i], dy = a - _pa[i];
                    var len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len > 0.005f) { ux += dx / len; uy += dy / len; }
                }
                _pa[i] = a; _pv[i] = v; _seen[i] = true;
            }
            for (var k = 0; k < _seat.Length; k++) if (_seatW[k] > 0) _seat[k] /= _seatW[k];
            Volatile.Write(ref _active, n);
            if (n > 0)
            {
                float mE = (float)(se / n), mA = (float)(sa / n), mV = (float)(sv / n), mF = (float)(sf / n);
                var sdA = (float)Math.Sqrt(Math.Max(0, sa2 / n - mA * mA));
                var sdV = (float)Math.Sqrt(Math.Max(0, sv2 / n - mV * mV));
                for (var k = 0; k < _spectrum.Length; k++) _spectrum[k] /= n;
                var direction = (float)(Math.Sqrt(ux * ux + uy * uy) / n);
                var cohesion = Math.Clamp(1f - MathF.Sqrt(sdA * sdA + sdV * sdV) / 0.35f, 0f, 1f);
                var sync = 0.5f * direction + 0.5f * cohesion;
                var respond = resp / (float)n;
                Volatile.Write(ref _mE, mE); Volatile.Write(ref _mA, mA); Volatile.Write(ref _mV, mV); Volatile.Write(ref _mF, mF);
                Volatile.Write(ref _sdA, sdA); Volatile.Write(ref _sdV, sdV); Volatile.Write(ref _sync, sync); Volatile.Write(ref _responding, respond);
                _curveE[_curveHead] = mE; _curveA[_curveHead] = mA; _curveS[_curveHead] = sync; _curveT[_curveHead] = mediaTimeSec;
                _curveHead = (_curveHead + 1) % CurveLen;
                if (_curveCount < CurveLen) Volatile.Write(ref _curveCount, _curveCount + 1);
                _spectrum.AsSpan().CopyTo(_volume.AsSpan(_volHead * Grid * Grid, Grid * Grid));
                _volHead = (_volHead + 1) % VolumeDepth;
                emitted = DetectPeak(mE, mA, sync, respond, mF, mV, mediaTimeSec);
            }
        }
        Volatile.Write(ref _aggMicros, (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
        if (emitted is { } pk)
        {
            try { OnPeak?.Invoke(pk); } catch (Exception ex) { PromptFlowStudio.Program.Log("Neuro.Collective.OnPeak", ex); }
        }
        if ((Interlocked.Increment(ref _aggs) & 7) == 0 || emitted is not null) Raise();
    }

    private PeakMoment? DetectPeak(float mE, float mA, float sync, float respond, float mF, float mV, double mt)
    {
        var intensity = (0.5 * mE + 0.5 * mA) * (0.5 + 0.5 * sync);
        if (_iMean < 0) { _iMean = intensity; return null; }
        var z = (intensity - _iMean) / Math.Sqrt(Math.Max(_iVar, 1e-4));
        PeakMoment? result = null;
        var hot = z >= 2.0 && sync >= 0.45f && respond >= 0.4f && mt - _lastPeakT >= 5.0;
        if (hot)
        {
            if (!_cand || z > _candScore) { _cand = true; _candScore = (float)z; _candT = mt; _candE = mE; _candA = mA; _candS = sync; _candR = respond; }
        }
        else if (_cand)
        {
            var label = mF > 0.6f || _candA > 0.7f && mV < 0.45f ? "공포/전율" : mV > 0.55f ? "환희/흥분" : "집중/몰입";
            var p = new PeakMoment(_candT, _candE, _candA, _candS, _candR, _candScore, label);
            _peaks[_peakHead] = p;
            _peakHead = (_peakHead + 1) % MaxPeaks;
            if (_peakCount < MaxPeaks) Volatile.Write(ref _peakCount, _peakCount + 1);
            _lastPeakT = _candT;
            _cand = false;
            result = p;
            LastAction = "⚡ 집단 공감 피크";
        }
        if (!_cand)
        {
            var d = intensity - _iMean;
            _iMean += 0.05 * d;
            _iVar = 0.95 * (_iVar + 0.05 * d * d);
        }
        return result;
    }

    /// <summary>최근 몰입/흥분/공감 곡선 복사(오래된 → 최신). 반환 = 개수.</summary>
    public int CopyCurve(Span<float> engagement, Span<float> arousal, Span<float> synchrony)
    {
        lock (_gate)
        {
            var n = Math.Min(_curveCount, Math.Min(engagement.Length, Math.Min(arousal.Length, synchrony.Length)));
            for (var i = 0; i < n; i++)
            {
                var k = (_curveHead - n + i + CurveLen) % CurveLen;
                engagement[i] = _curveE[k]; arousal[i] = _curveA[k]; synchrony[i] = _curveS[k];
            }
            return n;
        }
    }

    /// <summary>피크 목록 복사(최신 우선). 반환 = 개수.</summary>
    public int CopyPeaks(Span<PeakMoment> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(_peakCount, dst.Length);
            for (var i = 0; i < n; i++) dst[i] = _peaks[(_peakHead - 1 - i + MaxPeaks) % MaxPeaks];
            return n;
        }
    }

    /// <summary>3D 볼륨 슬라이스(age 0 = 최신) 복사.</summary>
    public void CopyVolumeSlice(int age, Span<float> dst)
    {
        lock (_gate) _volume.AsSpan(((_volHead - 1 - age + VolumeDepth * 2) % VolumeDepth) * Grid * Grid, Grid * Grid).CopyTo(dst);
    }

    /// <summary>텔레메트리 스냅샷 JSON(System.Text.Json · UI/외부 전송용 — 핫패스 외).</summary>
    public string ToTelemetryJson()
    {
        var peaks = new PeakMoment[MaxPeaks];
        var pc = CopyPeaks(peaks);
        var snap = new Dictionary<string, object>
        {
            ["schema"] = "pfs.collective-emotion.v1",
            ["mediaTimeSec"] = MediaTimeSec,
            ["active"] = ActiveViewers,
            ["engagement"] = MeanEngagement, ["arousal"] = MeanArousal, ["valence"] = MeanValence, ["fear"] = MeanFear,
            ["synchrony"] = Synchrony, ["responding"] = Responding,
            ["spectrum"] = _spectrum.ToArray(),
            ["peaks"] = peaks.Take(pc).Select(p => new { t = p.MediaTimeSec, p.Engagement, p.Arousal, p.Synchrony, p.Score, p.Emotion }).ToArray(),
        };
        return JsonSerializer.Serialize(snap);
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_t); Array.Clear(_seen); Volatile.Write(ref _maxViewer, -1);
            _curveHead = _curveCount = _volHead = _peakHead = _peakCount = 0;
            _iMean = -1; _iVar = 0.01; _lastPeakT = double.NegativeInfinity; _cand = false;
            Volatile.Write(ref _active, 0);
        }
        LastAction = "♻ 집계 초기화";
        Raise();
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Neuro.Collective.OnChange", ex); }
    }
}
