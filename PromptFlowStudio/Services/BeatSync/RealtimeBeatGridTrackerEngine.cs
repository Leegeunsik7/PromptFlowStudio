using System.Numerics;
using PromptFlowStudio.Services.Sound;
namespace PromptFlowStudio.Services.BeatSync;

/// <summary>비트 이벤트(값 형식) — 오디오 시계 초 · 다운비트 · 강도 · 온셋 정렬 여부.</summary>
public struct BeatEvent
{
    public double TimeSec;
    public long Index;
    public bool Downbeat, Snapped;
    public float Strength;
}

/// <summary>비트 시계(v17.39 컷 양자화 · v17.52 단어 정렬 공용).</summary>
public interface IBeatClock
{
    bool Locked { get; }
    double Bpm { get; }
    /// <summary>현재 오디오 시각이 비트(downbeatOnly 면 다운비트) ±windowSec 안이면 true.</summary>
    bool NearBeat(double windowSec, bool downbeatOnly);
}

/// <summary>
/// [PromptFlow Studio v17.53.0] 🥁 실시간 BGM 스펙트럼 플럭스 비트 그리드 &amp; 템포 트래킹 엔진(<see cref="Push"/> 무할당).
/// 48 kHz 모노 · 1024 점 Hann FFT · 홉 512(93.75 프레임/s). 로그 압축 진폭 log(1 + 100|X|) 의 양의 차분 합(스펙트럼 플럭스 · Vector&lt;float&gt;) 과 킥 대역(47~280 Hz) 에너지를 링에 적재한다.
/// <b>온셋</b>: 적응 임계(최근 16 프레임 평균 + 1.5σ) 국소 최대 · 최소 간격 60 ms → 시간 영역 정밀화(64 샘플 후/256 샘플 전 에너지 비 최대 지점 · 1 샘플 해상도).
/// <b>템포</b>: 32 프레임마다 최근 5.5 초 플럭스 자기상관(지연 60~180 BPM · 120 BPM 로그 가우시안 사전 · 포물선 보간) + 2 배 지연 비교로 옥타브 오류 억제.
/// <b>위상</b>: 최근 8 비트 빗살 합 최대 위상 → 다음 비트 예측 · 도달 시 ±40 ms 내 정밀 온셋으로 스냅해 <see cref="BeatEvent"/> 링(64)에 발행.
/// <b>다운비트</b>: 비트 인덱스 mod 4 버킷별 킥 에너지 EWMA 최대 버킷.
/// </summary>
public sealed class RealtimeBeatGridTrackerEngine : IBeatClock
{
    public const int Fs = 48000, N = 1024, Hop = 512, Bins = N / 2, Ring = 1024, AcfFrames = 512, EventCap = 64, OnsetCap = 64, RawCap = 4096;
    public const double FrameRate = (double)Fs / Hop, MinBpm = 60, MaxBpm = 180;
    private readonly FftPlan _fft = new(N);
    private readonly float[] _win = new float[N], _re = new float[N], _im = new float[N], _mag = new float[Bins], _prev = new float[Bins];
    private readonly float[] _flux = new float[Ring], _kick = new float[Ring];
    private readonly float[] _raw = new float[RawCap];
    private readonly float[] _acf = new float[160];
    private readonly double[] _onsetT = new double[OnsetCap];
    private readonly float[] _onsetS = new float[OnsetCap];
    private readonly BeatEvent[] _events = new BeatEvent[EventCap];
    private readonly float[] _barE = new float[4];
    private long _samples, _frames, _rawHead, _beatIndex = -1;
    private int _fill, _onHead, _onCount, _evHead, _evCount;
    private double _period, _nextBeat = double.NaN, _lastOnsetT = -1;
    private float _acfConf;

    public RealtimeBeatGridTrackerEngine()
    {
        for (var i = 0; i < N; i++) _win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / N);
    }

    public bool UseSimd { get; set; } = true;
    public double Bpm { get; private set; }
    public bool Locked => Bpm > 0 && _acfConf > 0.15f;
    public float Confidence => _acfConf;
    public double TimeSec => _samples / (double)Fs;
    public long Frames => _frames;
    public long Onsets { get; private set; }
    public long Beats { get; private set; }
    public long Snaps { get; private set; }
    public int DownbeatSlot { get; private set; }
    public double NextBeatSec => _nextBeat;
    public double LastPushUs { get; private set; }
    public float LastFlux { get; private set; }
    public int EventCount => _evCount;
    /// <summary>i = 0 이 최신.</summary>
    public ref readonly BeatEvent Event(int i) => ref _events[(_evHead - 1 - i + 2 * EventCap) % EventCap];
    public int OnsetCount => _onCount;
    public double OnsetTime(int i) => _onsetT[(_onHead - 1 - i + 2 * OnsetCap) % OnsetCap];
    public float FluxSeries(int i) => _flux[(int)((_frames - 1 - i) % Ring + Ring) % Ring];

    public void Reset()
    {
        _samples = _frames = _rawHead = 0; _beatIndex = -1; _fill = 0; _onHead = _onCount = _evHead = _evCount = 0;
        _period = 0; _nextBeat = double.NaN; _lastOnsetT = -1; Bpm = 0; _acfConf = 0; Onsets = Beats = Snaps = 0;
        Array.Clear(_prev); Array.Clear(_flux); Array.Clear(_kick); Array.Clear(_barE); Array.Clear(_raw);
    }

    /// <summary>모노 PCM 블록 공급(무할당 · 임의 길이).</summary>
    public void Push(ReadOnlySpan<float> x)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < x.Length; i++)
        {
            _raw[_rawHead % RawCap] = x[i];
            _rawHead++; _samples++; _fill++;
            if (_fill >= N && (_samples - N) % Hop == 0) Frame();
            if (!double.IsNaN(_nextBeat) && TimeSec >= _nextBeat + 0.045) EmitBeat();
        }
        LastPushUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Frame()
    {
        var start = _rawHead - N;
        for (var i = 0; i < N; i++) { _re[i] = _raw[(start + i) % RawCap] * _win[i]; _im[i] = 0f; }
        _fft.Transform(_re, _im, false);
        var kick = 0f;
        for (var k = 0; k < Bins; k++)
        {
            var m = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            _mag[k] = MathF.Log(1f + 100f * m);
            if (k is >= 1 and <= 6) kick += m;
        }
        var flux = Flux(_mag, _prev, UseSimd);
        _mag.AsSpan().CopyTo(_prev);
        var fi = (int)(_frames % Ring);
        _flux[fi] = flux; _kick[fi] = kick;
        LastFlux = flux;
        _frames++;
        PickOnset();
        if (_frames % 32 == 0 && _frames >= 192) Tempo();
    }

    /// <summary>스펙트럼 플럭스 Σ max(0, cur − prev) — SIMD / 스칼라(동일 결과 범위).</summary>
    public static float Flux(ReadOnlySpan<float> cur, ReadOnlySpan<float> prev, bool simd = true)
    {
        var i = 0; var s = 0f;
        if (simd && Vector.IsHardwareAccelerated)
        {
            var n = Vector<float>.Count; var acc = Vector<float>.Zero;
            for (; i <= cur.Length - n; i += n) acc += Vector.Max(new Vector<float>(cur.Slice(i, n)) - new Vector<float>(prev.Slice(i, n)), Vector<float>.Zero);
            s = Vector.Sum(acc);
        }
        for (; i < cur.Length; i++) s += MathF.Max(cur[i] - prev[i], 0f);
        return s;
    }

    private float Kn(long frame) => frame < 0 || frame < _frames - Ring || frame >= _frames ? 0f : _kick[(int)(frame % Ring)];

    private float F(long frame) => frame < 0 || frame < _frames - Ring || frame >= _frames ? 0f : _flux[(int)(frame % Ring)];

    private void PickOnset()
    {
        if (_frames < 20) return;
        var c = _frames - 2;
        float a = F(c - 1), b = F(c), d = F(c + 1);
        if (!(b > a && b >= d)) return;
        float mean = 0, sq = 0;
        for (var k = 3; k < 19; k++) { var v = F(_frames - k); mean += v; sq += v * v; }
        mean /= 16; var sd = MathF.Sqrt(MathF.Max(sq / 16 - mean * mean, 0));
        if (b < mean + 1.5f * sd + 0.5f) return;
        var t = Refine(c);
        if (_lastOnsetT >= 0 && t - _lastOnsetT < 0.06) return;
        _lastOnsetT = t;
        _onsetT[_onHead] = t; _onsetS[_onHead] = b;
        _onHead = (_onHead + 1) % OnsetCap;
        if (_onCount < OnsetCap) _onCount++;
        Onsets++;
    }

    /// <summary>프레임 c 의 창 구간에서 에너지 비(뒤 64 / 앞 256) 최대 샘플 → 초.</summary>
    private double Refine(long c)
    {
        var s0 = c * Hop - Hop / 2; var s1 = c * Hop + N;
        s0 = Math.Max(s0, Math.Max(_rawHead - RawCap + 256, 256)); s1 = Math.Min(s1, _rawHead - 64);
        double pre = 0, post = 0;
        for (var k = s0 - 256; k < s0; k++) { var v = _raw[k % RawCap]; pre += v * v; }
        for (var k = s0; k < s0 + 64; k++) { var v = _raw[k % RawCap]; post += v * v; }
        double best = -1; var at = s0;
        for (var p = s0; p < s1; p++)
        {
            var r = post / (pre * 0.25 + 1e-7);
            if (r > best) { best = r; at = p; }
            var a = _raw[p % RawCap]; var b = _raw[(p + 64) % RawCap]; var o = _raw[(p - 256) % RawCap];
            post += b * b - a * a; pre += a * a - o * o;
            if (pre < 0) pre = 0; if (post < 0) post = 0;
        }
        return at / (double)Fs;
    }

    private void Tempo()
    {
        var len = (int)Math.Min(AcfFrames, _frames);
        var end = _frames;
        double mean = 0;
        for (var i = 0; i < len; i++) mean += F(end - 1 - i);
        mean /= len;
        int lmin = (int)Math.Floor(FrameRate * 60 / MaxBpm), lmax = (int)Math.Ceiling(FrameRate * 60 / MinBpm);
        double e0 = 0;
        for (var i = 0; i < len; i++) { var v = F(end - 1 - i) - mean; e0 += v * v; }
        if (e0 <= 1e-9) return;
        var best = -1.0; var bl = 0;
        for (var l = lmin; l <= Math.Min(lmax * 2, _acf.Length - 1); l++)
        {
            double s = 0;
            for (var i = 0; i + l < len; i++) s += (F(end - 1 - i) - mean) * (F(end - 1 - i - l) - mean);
            _acf[l] = (float)(s / e0);
        }
        for (var l = lmin; l <= lmax; l++)
        {
            var bpm = 60 * FrameRate / l;
            var prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.9, 2));
            var score = (_acf[l] + 0.5 * (2 * l < _acf.Length ? _acf[2 * l] : 0)) * prior;
            if (score > best) { best = score; bl = l; }
        }
        // 옥타브 하향 오류 억제 — 절반 지연(비정수 · 홉 경계에 갈라지는 피크)이 충분히 강하면 2 배 템포 채택
        if (bl / 2 > lmin && 120 * FrameRate / bl <= MaxBpm)
        {
            var h = bl / 2; var hb = bl % 2 == 0 ? h : (_acf[h] >= _acf[h + 1] ? h : h + 1);
            if (Math.Max(_acf[h], _acf[h + 1]) >= 0.5f * _acf[bl] && _acf[hb] >= _acf[hb - 1] && _acf[hb] >= _acf[hb + 1]) bl = hb;
        }
        if (bl <= lmin || bl >= lmax) { _acfConf = 0; return; }
        double y0 = _acf[bl - 1], y1 = _acf[bl], y2 = _acf[bl + 1];
        var den = y0 - 2 * y1 + y2;
        var lag = bl + (Math.Abs(den) > 1e-9 ? 0.5 * (y0 - y2) / den : 0);
        _acfConf = _acf[bl];
        _period = lag / FrameRate;
        Bpm = 60.0 / _period;
        Phase();
    }

    /// <summary>최근 8 비트 빗살 합 최대 위상 → 다음 비트 예측(이미 발행된 비트 이후).</summary>
    private void Phase()
    {
        var pf = _period * FrameRate;
        var bestS = -1.0; var bestPh = 0.0;
        // 위상 증거 = 플럭스(정규화) + 킥 대역(정규화) — 오프비트 하이햇/스네어보다 킥에 정렬
        float fMax = 1e-6f, kMax = 1e-6f;
        for (var i = 0; i < Math.Min(_frames, (long)(9 * pf) + 4); i++) { fMax = MathF.Max(fMax, F(_frames - 1 - i)); kMax = MathF.Max(kMax, Kn(_frames - 1 - i)); }
        for (var ph = 0; ph < (int)pf; ph++)
        {
            double s = 0;
            for (var k = 0; k < 8; k++)
            {
                var f = _frames - 3 - ph - k * pf;
                var fl = (long)Math.Floor(f); var fr = f - fl;
                s += (F(fl) / fMax + 2f * Kn(fl) / kMax) * (1 - fr) + (F(fl + 1) / fMax + 2f * Kn(fl + 1) / kMax) * fr;
            }
            if (s > bestS) { bestS = s; bestPh = ph; }
        }
        var lastBeatFrame = _frames - 3 - bestPh;
        var lastBeat = (lastBeatFrame * Hop + N / 2.0) / Fs;
        // 정밀 온셋으로 보정
        var snapped = NearestOnset(lastBeat, 0.04);
        if (!double.IsNaN(snapped)) lastBeat = snapped;
        var next = lastBeat + _period;
        while (next < TimeSec - 0.04) next += _period;
        var lastEmitted = _evCount > 0 ? Event(0).TimeSec : double.NegativeInfinity;
        while (next - lastEmitted < 0.6 * _period) next += _period;
        _nextBeat = next;
    }

    private double NearestOnset(double t, double win)
    {
        var best = double.NaN; var bd = win;
        for (var i = 0; i < _onCount; i++) { var d = Math.Abs(OnsetTime(i) - t); if (d <= bd) { bd = d; best = OnsetTime(i); } }
        return best;
    }

    private float KickNear(double t)
    {
        var f = (long)Math.Round((t * Fs - N / 2.0) / Hop);
        var m = 0f;
        for (var k = -1; k <= 2; k++) m = MathF.Max(m, f + k >= 0 && f + k < _frames && f + k >= _frames - Ring ? _kick[(int)((f + k) % Ring)] : 0f);
        return m;
    }

    private void EmitBeat()
    {
        var t = _nextBeat;
        var snap = NearestOnset(t, 0.04);
        var ok = !double.IsNaN(snap);
        if (ok) { t = snap; Snaps++; }
        // 위상 재추정으로 비트가 건너뛰어져도 마디 슬롯이 밀리지 않도록 경과 시간으로 인덱스 전진
        _beatIndex += _evCount > 0 && _period > 0 ? Math.Max(1L, (long)Math.Round((t - Event(0).TimeSec) / _period)) : 1L;
        var slot = (int)(_beatIndex % 4);
        _barE[slot] = 0.85f * _barE[slot] + 0.15f * KickNear(t);
        var db = 0; for (var k = 1; k < 4; k++) if (_barE[k] > _barE[db]) db = k;
        DownbeatSlot = db;
        ref var e = ref _events[_evHead];
        e.TimeSec = t; e.Index = _beatIndex; e.Downbeat = slot == db; e.Snapped = ok; e.Strength = KickNear(t);
        _evHead = (_evHead + 1) % EventCap;
        if (_evCount < EventCap) _evCount++;
        Beats++;
        _nextBeat = t + _period;
    }

    public bool NearBeat(double windowSec, bool downbeatOnly)
    {
        if (!Locked || _evCount == 0) return false;
        var now = TimeSec;
        ref readonly var last = ref Event(0);
        if (Math.Abs(now - last.TimeSec) <= windowSec && (!downbeatOnly || last.Downbeat)) return true;
        if (double.IsNaN(_nextBeat) || Math.Abs(_nextBeat - now) > windowSec) return false;
        return !downbeatOnly || (last.Index + 1) % 4 == DownbeatSlot;
    }
}
