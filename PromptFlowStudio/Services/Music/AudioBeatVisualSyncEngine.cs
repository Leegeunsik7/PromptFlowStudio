using System.Buffers;
using System.Globalization;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Sound;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Music;

public enum CutVfx : byte { None, Flash, Shake, Explosion }

/// <summary>컷 매핑 플래그(MapCuts 출력).</summary>
[Flags]
public enum CutFlags : byte { None = 0, Drop = 1, Downbeat = 2, PreDrop = 4, Accent = 8, SectionStart = 16 }

/// <summary>오프라인 분석 결과 — BPM · 비트/다운비트 · 온셋(강도) · 드랍 · 마디 에너지 · 온셋 엔벨로프(UI).</summary>
public sealed class BeatAnalysis
{
    public double Bpm { get; init; }
    public double BeatSec => Bpm <= 0 ? 0.5 : 60.0 / Bpm;
    public double Confidence { get; init; }
    public double Duration { get; init; }
    public double HopSec { get; init; }
    public float[] Beats { get; init; } = Array.Empty<float>();
    public float[] Downbeats { get; init; } = Array.Empty<float>();
    public float[] Onsets { get; init; } = Array.Empty<float>();
    public float[] OnsetStrength { get; init; } = Array.Empty<float>();
    public float[] Drops { get; init; } = Array.Empty<float>();
    public float[] BarEnergy { get; init; } = Array.Empty<float>();
    public float[] Envelope { get; init; } = Array.Empty<float>();
}

/// <summary>동기화 컷 — 시작/길이 · 진입 트랜지션(null = 하드컷) · VFX · 속도(스피드 램프) · 드랍/다운비트 · 긴장도.</summary>
public sealed record SyncCut(int Index, double StartSec, double DurationSec, string? Transition, int TransitionMs, CutVfx Vfx, double Speed, CutFlags Flags, int Tension)
{
    public bool OnDrop => (Flags & CutFlags.Drop) != 0;
}

/// <summary>동기화 옵션 — 컷당 박 수(1/2/4/8) · 최대 컷 · 드랍 VFX · 스피드 램프 · 강박 플래시.</summary>
public sealed record BeatSyncOptions(int BeatsPerCut = 4, int MaxCuts = 40, bool DropVfx = true, bool SpeedRamp = true, bool AccentFlash = true);

public sealed record BeatSyncPlan(double Bpm, double Duration, IReadOnlyList<SyncCut> Cuts, int Drops, int Accents, string Summary);

/// <summary>
/// 실시간 비트 추적기(무할당) — 모노 블록 투입 → 링 버퍼 · hop 마다 Hann+FFT 스펙트럴 플럭스 · 적응 임계(EWMA 평균 × 배수 + 피크 감쇠 바닥) ·
/// 최근 8 온셋 IOI → 70~180 BPM 옥타브 접기 히스토그램(감쇠) → BPM.
/// </summary>
public sealed class RealtimeBeatTracker
{
    public const int MinBpm = 70, MaxBpm = 180;
    private readonly FftPlan _fft;
    private readonly float[] _ring, _win, _re, _im, _prev, _hist = new float[MaxBpm - MinBpm + 1];
    private readonly double[] _recent = new double[8];
    private readonly int _hop, _n;
    private int _pos, _sinceHop, _recentCount, _recentPos;
    private long _samples;
    private float _mean, _peak;

    public RealtimeBeatTracker(int rate)
    {
        Rate = Math.Clamp(rate, 8000, 192000);
        _hop = AudioBeatVisualSyncEngine.HopFor(Rate);
        _n = _hop * 2;
        _fft = new FftPlan(_n);
        _ring = new float[_n]; _re = new float[_n]; _im = new float[_n]; _prev = new float[_n / 2];
        _win = AudioBeatVisualSyncEngine.Hann(_n);
        LastOnsetSec = -1;
    }

    public int Rate { get; }
    public long Onsets { get; private set; }
    public double LastOnsetSec { get; private set; }
    public double Bpm { get; private set; }
    public float Flux { get; private set; }

    public void Reset()
    {
        Array.Clear(_ring); Array.Clear(_prev); Array.Clear(_hist); Array.Clear(_recent);
        _pos = _sinceHop = _recentCount = _recentPos = 0; _samples = 0; _mean = _peak = 0;
        Onsets = 0; LastOnsetSec = -1; Bpm = 0; Flux = 0;
    }

    /// <summary>모노 블록 투입(핫패스 · 무할당).</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        for (var i = 0; i < mono.Length; i++)
        {
            _ring[_pos] = mono[i];
            _pos = (_pos + 1) % _n;
            _samples++;
            if (++_sinceHop < _hop) continue;
            _sinceHop = 0;
            Frame();
        }
    }

    private void Frame()
    {
        for (var k = 0; k < _n; k++) { _re[k] = _ring[(_pos + k) % _n] * _win[k]; _im[k] = 0; }
        _fft.Transform(_re, _im, false);
        float flux = 0;
        for (var k = 1; k < _n / 2; k++)
        {
            var m = MathF.Log(1 + 10 * MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]));
            var d = m - _prev[k];
            if (d > 0) flux += d;
            _prev[k] = m;
        }
        Flux = flux;
        var now = (_samples - _n / 2) / (double)Rate;
        var thr = _mean * 1.5f + _peak * 0.1f;
        _mean = _mean * 0.92f + flux * 0.08f;
        _peak = MathF.Max(_peak * 0.995f, flux);
        if (flux <= thr || flux < 1e-3f || (LastOnsetSec >= 0 && now - LastOnsetSec < 0.1)) return;
        Onsets++;
        // IOI → 히스토그램(최근 8 온셋과의 간격 · 옥타브 접기)
        for (var h = 0; h < _hist.Length; h++) _hist[h] *= 0.97f;
        for (var j = 0; j < _recentCount; j++)
        {
            var ioi = now - _recent[(_recentPos - 1 - j + _recent.Length * 2) % _recent.Length];
            if (ioi < 0.2 || ioi > 2.0) continue;
            var bpm = 60.0 / ioi;
            while (bpm < MinBpm) bpm *= 2;
            while (bpm > MaxBpm) bpm /= 2;
            var bin = (int)Math.Round(bpm) - MinBpm;
            if ((uint)bin < (uint)_hist.Length) _hist[bin] += 1f / (1 + j * 0.5f);
        }
        _recent[_recentPos] = now;
        _recentPos = (_recentPos + 1) % _recent.Length;
        if (_recentCount < _recent.Length) _recentCount++;
        LastOnsetSec = now;
        var best = 0; var bv = 0f;
        for (var h = 1; h < _hist.Length - 1; h++)
        {
            var v = _hist[h - 1] * 0.5f + _hist[h] + _hist[h + 1] * 0.5f;
            if (v > bv) { bv = v; best = h; }
        }
        if (bv > 0) Bpm = best + MinBpm;
    }
}

/// <summary>
/// [PromptFlow Studio v16.37.0] 🥁 오디오 비트 탐지 &amp; 비주얼 동기화 엔진.
///   ▸ 오프라인: Hann+FFT 스펙트럴 플럭스(전대역 · 저역 &lt;150Hz) → 적응 임계 온셋 피킹 → 자기상관(+2배 지연 · 120BPM 로그 사전분포 · 포물선 보간) BPM →
///     콤 위상 탐색 비트 그리드 → 다운비트 위상(저역+전대역) → 드랍(박 직전 저역 갭 + 이후 4박/이전 4박 저역 에너지 비 · NMS 8박) · 마디 에너지.
///   ▸ 매칭(무할당 MapCuts): 비트 격자 컷 + 드랍 강제 컷 · 드랍 직전 컷 PreDrop · 다운비트 이진 탐색 플래그.
///   ▸ 플랜: 드랍 = 화이트 플래시 전환 + 폭발 VFX + 임팩트 폴리 · 드랍 직전 = 슬로모션 램프 · 강박 온셋 = 플래시 · 고에너지 다운비트 = 셰이크 · 구간 시작 = 줌인.
///   ▸ 실시간: <see cref="RealtimeBeatTracker"/>(무할당). 분석 스크래치 버퍼는 인스턴스 재사용(락).
/// </summary>
public sealed class AudioBeatVisualSyncEngine
{
    private readonly object _gate = new();
    private float[] _env = Array.Empty<float>(), _low = Array.Empty<float>(), _lowE = Array.Empty<float>();
    private float[] _re = Array.Empty<float>(), _im = Array.Empty<float>(), _prev = Array.Empty<float>(), _win = Array.Empty<float>();
    private FftPlan? _fft;

    public AudioBeatVisualSyncEngine() { }

    public AudioBeatVisualSyncEngine(IServiceProvider sp) : this() { }

    public event Action? OnChange;

    public BeatAnalysis? LastAnalysis { get; private set; }
    public BeatSyncPlan? LastPlan { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>~11.6ms 에 가장 가까운 2의 거듭제곱 hop.</summary>
    public static int HopFor(int rate)
    {
        var target = rate * 0.0116;
        var h = 64;
        while (h * 2 <= target * 1.414) h *= 2;
        return h;
    }

    public static float[] Hann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / n);
        return w;
    }

    private static void Ensure(ref float[] a, int n) { if (a.Length < n) a = new float[n]; }

    /// <summary>스테레오 → 분석(결정적).</summary>
    public BeatAnalysis Analyze(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int rate)
    {
        var len = Math.Min(left.Length, right.Length);
        var hop = HopFor(rate);
        var n = hop * 2;
        var hopSec = hop / (double)rate;
        var frames = len < n ? 0 : (len - n) / hop + 1;
        if (frames < 16) return new BeatAnalysis { Duration = len / (double)rate, HopSec = hopSec };
        lock (_gate)
        {
            if (_fft is null || _fft.N != n) { _fft = new FftPlan(n); _win = Hann(n); _re = new float[n]; _im = new float[n]; _prev = new float[n / 2]; }
            Ensure(ref _env, frames); Ensure(ref _low, frames); Ensure(ref _lowE, frames);
            Array.Clear(_prev);
            var lowBin = Math.Max(2, (int)(150.0 * n / rate));
            float envMax = 1e-9f, lowMax = 1e-9f;
            for (var f = 0; f < frames; f++)
            {
                var o = f * hop;
                for (var i = 0; i < n; i++) { _re[i] = (left[o + i] + right[o + i]) * 0.5f * _win[i]; _im[i] = 0; }
                _fft.Transform(_re, _im, false);
                float flux = 0, lflux = 0, le = 0;
                for (var k = 1; k < n / 2; k++)
                {
                    var mag = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                    var m = MathF.Log(1 + 10 * mag);
                    var d = m - _prev[k];
                    _prev[k] = m;
                    if (d > 0) { flux += d; if (k <= lowBin) lflux += d; }
                    if (k <= lowBin) le += mag * mag;
                }
                _env[f] = flux; _low[f] = lflux; _lowE[f] = le;
                envMax = MathF.Max(envMax, flux); lowMax = MathF.Max(lowMax, lflux);
            }
            for (var f = 0; f < frames; f++) { _env[f] /= envMax; _low[f] /= lowMax; }
            var center = n / 2.0 / rate;
            var env = _env.AsSpan(0, frames);

            // 온셋 피킹
            var onsets = new List<float>(); var strength = new List<float>();
            for (var f = 3; f < frames - 3; f++)
            {
                var v = env[f];
                if (v < 0.08f) continue;
                var isMax = true;
                for (var k = -3; k <= 3 && isMax; k++) if (k != 0 && env[f + k] > v) isMax = false;
                if (!isMax) continue;
                float mean = 0; var cnt = 0;
                for (var k = Math.Max(0, f - 10); k <= Math.Min(frames - 1, f + 10); k++) { mean += env[k]; cnt++; }
                if (v < mean / cnt * 1.4f + 0.03f) continue;
                var t = (float)(f * hopSec + center);
                if (onsets.Count > 0 && t - onsets[^1] < 0.05f) continue;
                onsets.Add(t); strength.Add(v);
            }

            // 템포: 자기상관
            float avg = 0;
            for (var f = 0; f < frames; f++) avg += env[f];
            avg /= frames;
            int minLag = Math.Max(2, (int)(60.0 / (200 * hopSec))), maxLag = Math.Min(frames / 3, (int)(60.0 / (60 * hopSec)) + 1);
            double Ac(int lag)
            {
                double s = 0;
                for (var f = 0; f + lag < frames; f++) s += (_env[f] - avg) * (_env[f + lag] - avg);
                return s / (frames - lag);
            }
            double ac0 = Math.Max(1e-12, Ac(0));
            var bestLag = 0; double bestScore = double.MinValue;
            var scores = ArrayPool<double>.Shared.Rent(maxLag + 2);
            try
            {
                for (var lag = minLag; lag <= maxLag; lag++)
                {
                    var bpmL = 60.0 / (lag * hopSec);
                    var lp = Math.Log2(bpmL / 120) / 0.9;
                    var s = (Ac(lag) + (2 * lag < frames / 2 ? 0.5 * Ac(2 * lag) : 0)) * Math.Exp(-0.5 * lp * lp);
                    scores[lag] = s;
                    if (s > bestScore) { bestScore = s; bestLag = lag; }
                }
                double lagF = bestLag;
                if (bestLag > minLag && bestLag < maxLag)
                {
                    double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
                    var den = a - 2 * b + c;
                    if (Math.Abs(den) > 1e-12) lagF = bestLag + Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
                }
                var bpm = Math.Round(60.0 / (lagF * hopSec), 1);
                var conf = Math.Clamp(Ac(bestLag) / ac0, 0, 1);

                // 위상(콤)
                var bestO = 0; double bestPh = double.MinValue;
                for (var o = 0; o < (int)Math.Ceiling(lagF); o++)
                {
                    double s = 0;
                    for (double p = o; p < frames; p += lagF) s += env[(int)p];
                    if (s > bestPh) { bestPh = s; bestO = o; }
                }
                var beatSec = 60.0 / bpm;
                var first = bestO * hopSec + center;
                while (first - beatSec >= -0.02) first -= beatSec;
                if (first < 0) first += beatSec;
                var duration = len / (double)rate;
                var beats = new List<float>();
                for (var t = first; t < duration - 0.05; t += beatSec) beats.Add((float)t);

                // 다운비트 위상
                Span<double> ph4 = stackalloc double[4];
                for (var bi = 0; bi < beats.Count; bi++)
                {
                    var f = Math.Clamp((int)Math.Round((beats[bi] - center) / hopSec), 0, frames - 1);
                    ph4[bi % 4] += _low[f] + 0.5 * env[f];
                }
                var down = 0;
                for (var p = 1; p < 4; p++) if (ph4[p] > ph4[down]) down = p;
                var downbeats = new List<float>();
                for (var bi = down; bi < beats.Count; bi += 4) downbeats.Add(beats[bi]);

                // 박별 저역 에너지 → 드랍
                var nb = beats.Count;
                var beatLow = new double[nb];
                for (var bi = 0; bi < nb; bi++)
                {
                    int f0 = Math.Clamp((int)((beats[bi] - center) / hopSec), 0, frames - 1), f1 = Math.Clamp((int)((beats[bi] + beatSec - center) / hopSec), f0 + 1, frames);
                    double s = 0;
                    for (var f = f0; f < f1; f++) s += _lowE[f];
                    beatLow[bi] = s / (f1 - f0);
                }
                double maxPost = 1e-12;
                for (var bi = 0; bi + 4 <= nb; bi++) { double s = 0; for (var k = 0; k < 4; k++) s += beatLow[bi + k]; maxPost = Math.Max(maxPost, s / 4); }
                var drops = new List<float>();
                var lastDrop = -100;
                for (var bi = 4; bi + 4 <= nb; bi++)
                {
                    double pre = 0, post = 0;
                    for (var k = 0; k < 4; k++) { pre += beatLow[bi - 4 + k]; post += beatLow[bi + k]; }
                    pre /= 4; post /= 4;
                    var gap = beatLow[bi - 1] < 0.35 * post;
                    if (gap && post > 1.3 * pre && post >= 0.5 * maxPost && bi - lastDrop >= 8)
                    {
                        drops.Add(beats[bi]);
                        lastDrop = bi;
                    }
                }

                // 마디 에너지(전대역 플럭스 평균 · 정규화)
                var bars = new List<float>();
                for (var di = 0; di < downbeats.Count; di++)
                {
                    int f0 = Math.Clamp((int)((downbeats[di] - center) / hopSec), 0, frames - 1), f1 = Math.Clamp((int)((downbeats[di] + 4 * beatSec - center) / hopSec), f0 + 1, frames);
                    float s = 0;
                    for (var f = f0; f < f1; f++) s += _lowE[f] + env[f];
                    bars.Add(s / (f1 - f0));
                }
                var bmax = 1e-9f;
                foreach (var b in bars) bmax = MathF.Max(bmax, b);
                for (var i = 0; i < bars.Count; i++) bars[i] /= bmax;

                // UI 엔벨로프(≤1000 버킷 · 최대)
                var buckets = Math.Min(1000, frames);
                var ui = new float[buckets];
                for (var bk = 0; bk < buckets; bk++)
                {
                    int s0 = (int)((long)frames * bk / buckets), s1 = Math.Max(s0 + 1, (int)((long)frames * (bk + 1) / buckets));
                    for (var f = s0; f < s1; f++) ui[bk] = MathF.Max(ui[bk], env[f]);
                }
                var res = new BeatAnalysis
                {
                    Bpm = bpm, Confidence = Math.Round(conf, 3), Duration = duration, HopSec = hopSec, Beats = beats.ToArray(), Downbeats = downbeats.ToArray(),
                    Onsets = onsets.ToArray(), OnsetStrength = strength.ToArray(), Drops = drops.ToArray(), BarEnergy = bars.ToArray(), Envelope = ui,
                };
                LastAnalysis = res;
                LastAction = "🥁 " + bpm.ToString("0.#", CultureInfo.InvariantCulture) + " BPM · 비트 " + beats.Count.ToString(CultureInfo.InvariantCulture) + " · 온셋 " + onsets.Count.ToString(CultureInfo.InvariantCulture) + " · 드랍 " + drops.Count.ToString(CultureInfo.InvariantCulture);
                Raise();
                return res;
            }
            finally { ArrayPool<double>.Shared.Return(scores); }
        }
    }

    // ── 매칭(무할당) ───────────────────────────────────────────────────────────

    /// <summary>정렬 배열에서 t 에 가장 가까운 값의 인덱스(빈 배열 −1 · 무할당).</summary>
    public static int Nearest(ReadOnlySpan<float> sorted, float t)
    {
        if (sorted.IsEmpty) return -1;
        int lo = 0, hi = sorted.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid] < t) lo = mid + 1; else hi = mid;
        }
        return lo > 0 && t - sorted[lo - 1] < sorted[lo] - t ? lo - 1 : lo;
    }

    /// <summary>비트 → 컷 시작 매핑(무할당). 반환: 컷 수. 첫 컷 = 0초 · 이후 beatsPerCut 박 간격 · 드랍 강제 삽입(직전 컷 PreDrop) · 다운비트 ±40ms 플래그.</summary>
    public static int MapCuts(ReadOnlySpan<float> beats, ReadOnlySpan<float> downbeats, ReadOnlySpan<float> drops, int beatsPerCut, Span<float> starts, Span<CutFlags> flags)
    {
        var cap = Math.Min(starts.Length, flags.Length);
        if (cap == 0) return 0;
        beatsPerCut = Math.Max(1, beatsPerCut);
        var minGap = beats.Length > 1 ? (beats[1] - beats[0]) * 0.5f : 0.25f;
        var count = 0;
        starts[0] = 0; flags[0] = DownFlag(downbeats, 0); count = 1;
        var di = 0;
        for (var b = beatsPerCut; b < beats.Length && count < cap; b += beatsPerCut)
        {
            var t = beats[b];
            while (di < drops.Length && drops[di] <= t + minGap && count < cap)
            {
                if (drops[di] - starts[count - 1] > minGap)
                {
                    flags[count - 1] |= CutFlags.PreDrop;
                    starts[count] = drops[di]; flags[count] = CutFlags.Drop | CutFlags.Downbeat; count++;
                }
                else if (count > 1) { flags[count - 1] |= CutFlags.Drop; flags[count - 2] |= CutFlags.PreDrop; }
                di++;
            }
            if (count < cap && t - starts[count - 1] > minGap) { starts[count] = t; flags[count] = DownFlag(downbeats, t); count++; }
        }
        while (di < drops.Length && count < cap)
        {
            if (drops[di] - starts[count - 1] > minGap) { flags[count - 1] |= CutFlags.PreDrop; starts[count] = drops[di]; flags[count] = CutFlags.Drop | CutFlags.Downbeat; count++; }
            di++;
        }
        return count;
    }

    private static CutFlags DownFlag(ReadOnlySpan<float> downbeats, float t)
    {
        var i = Nearest(downbeats, t);
        return i >= 0 && MathF.Abs(downbeats[i] - t) < 0.04f ? CutFlags.Downbeat : CutFlags.None;
    }

    // ── 플랜 ─────────────────────────────────────────────────────────────────

    private static readonly string[] Rotation = { "slideleft", "smoothright", "slideup", "circleopen" };

    public BeatSyncPlan BuildPlan(BeatAnalysis a, BeatSyncOptions? options = null)
    {
        var o = options ?? new BeatSyncOptions();
        var cap = Math.Clamp(o.MaxCuts, 1, 200);
        var starts = ArrayPool<float>.Shared.Rent(cap);
        var flags = ArrayPool<CutFlags>.Shared.Rent(cap);
        try
        {
            var count = MapCuts(a.Beats, a.Downbeats, o.DropVfx ? a.Drops : ReadOnlySpan<float>.Empty, o.BeatsPerCut, starts.AsSpan(0, cap), flags.AsSpan(0, cap));
            // 강박 온셋(상위 15%) → Accent
            var thr = 1f;
            if (a.OnsetStrength.Length > 0)
            {
                var sorted = (float[])a.OnsetStrength.Clone();
                Array.Sort(sorted);
                thr = sorted[(int)(sorted.Length * 0.85)];
            }
            var cuts = new List<SyncCut>(count);
            var transMs = Math.Clamp((int)(a.BeatSec * 1000 / 4), 80, 400);
            int accents = 0, drops = 0;
            double prevEnergy = -1;
            for (var i = 0; i < count; i++)
            {
                var s = starts[i];
                var e = i + 1 < count ? starts[i + 1] : (float)a.Duration;
                var f = flags[i];
                for (var k = 0; k < a.Onsets.Length; k++)
                    if (a.Onsets[k] >= s && a.Onsets[k] < e && a.OnsetStrength[k] >= thr && (f & CutFlags.Drop) == 0) { f |= CutFlags.Accent; break; }
                var bar = Math.Max(0, Nearest(a.Downbeats, s));
                var energy = a.BarEnergy.Length > 0 ? a.BarEnergy[Math.Min(bar, a.BarEnergy.Length - 1)] : 0.5;
                if (prevEnergy >= 0 && Math.Abs(energy - prevEnergy) > 0.3 && (f & CutFlags.Downbeat) != 0) f |= CutFlags.SectionStart;
                prevEnergy = energy;
                string? trans; var tms = transMs; var vfx = CutVfx.None; var speed = 1.0;
                if ((f & CutFlags.Drop) != 0) { trans = "fadewhite"; tms = 120; vfx = CutVfx.Explosion; drops++; }
                else if ((f & CutFlags.SectionStart) != 0) trans = "zoomin";
                else trans = o.BeatsPerCut <= 2 ? null : Rotation[i % Rotation.Length];
                if (i == 0) trans = null;
                if (vfx == CutVfx.None && (f & CutFlags.Accent) != 0 && o.AccentFlash) { vfx = CutVfx.Flash; accents++; }
                if (vfx == CutVfx.None && energy > 0.85 && (f & CutFlags.Downbeat) != 0) vfx = CutVfx.Shake;
                if (o.SpeedRamp && (f & CutFlags.PreDrop) != 0) speed = 0.6;
                else if (o.SpeedRamp && (f & CutFlags.Drop) != 0) speed = 1.15;
                var tension = (int)Math.Clamp(Math.Round(30 + 70 * energy), 0, 100);
                if ((f & CutFlags.Drop) != 0) tension = 100;
                cuts.Add(new SyncCut(i, Math.Round(s, 3), Math.Round(Math.Max(0.05, e - s), 3), trans, tms, vfx, speed, f, tension));
            }
            var plan = new BeatSyncPlan(a.Bpm, a.Duration, cuts, drops, accents,
                cuts.Count.ToString(CultureInfo.InvariantCulture) + "컷 · 드랍 " + drops.ToString(CultureInfo.InvariantCulture) + " · 플래시 " + accents.ToString(CultureInfo.InvariantCulture) + " · " + a.Bpm.ToString("0.#", CultureInfo.InvariantCulture) + " BPM");
            LastPlan = plan;
            LastAction = "🎬 비트 플랜 " + plan.Summary;
            Raise();
            return plan;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(starts);
            ArrayPool<CutFlags>.Shared.Return(flags);
        }
    }

    public static string VfxToken(CutVfx v) => v switch
    {
        CutVfx.Explosion => "explosive particle burst synchronized to the bass drop",
        CutVfx.Flash => "strobe light flash on the beat",
        CutVfx.Shake => "punchy camera shake impact on the downbeat",
        _ => "",
    };

    public static string VfxLabel(CutVfx v) => v switch { CutVfx.Explosion => "💥 폭발", CutVfx.Flash => "⚡ 플래시", CutVfx.Shake => "📳 셰이크", _ => "—" };

    /// <summary>
    /// 플랜 → 스토리보드(컷 = 씬 순서 1:1). createMissing: 부족한 씬을 기존 프롬프트 순환/기본 프롬프트로 생성.
    /// 진입 전환(화이트리스트) · VFX/스피드 토큰(멱등) · 폴리(whoosh/impact) · 긴장도 · BGM. 반환: 갱신 컷 수.
    /// </summary>
    public int ApplyToProject(StoryboardProject project, BeatSyncPlan plan, string? bgmPath, bool createMissing = true, string? basePrompt = null)
    {
        var cuts = plan.Cuts;
        if (createMissing)
        {
            var existing = project.Scenes.Count;
            for (var i = existing; i < cuts.Count; i++)
                project.Scenes.Add(new StoryboardScene { Prompt = existing > 0 ? project.Scenes[i % existing].Prompt : (basePrompt ?? "cinematic music video shot") });
        }
        var m = Math.Min(cuts.Count, project.Scenes.Count);
        for (var i = 0; i < m; i++)
        {
            var c = cuts[i];
            var sc = project.Scenes[i];
            sc.EnterTransition = c.Transition is null ? null : new VideoTransition(TransitionSafety.SafeXfade(c.Transition), c.TransitionMs);
            sc.PresetTension = c.Tension;
            var tokens = new List<string>(3);
            var vt = VfxToken(c.Vfx);
            if (vt.Length > 0) tokens.Add(vt);
            if (c.Speed < 0.95) tokens.Add("dramatic slow motion speed ramp building tension");
            else if (c.Speed > 1.05) tokens.Add("fast-paced speed ramp burst");
            tokens.Add("edited on the beat");
            sc.Prompt = CharacterIdentityLockService.MergeTokens(sc.Prompt, tokens);
            var foley = new List<FoleySpec>(2);
            if (c.Transition is not null) foley.Add(new FoleySpec("whoosh", 0, 0));
            if (c.Vfx is CutVfx.Explosion or CutVfx.Shake) foley.Add(new FoleySpec("impact", 0, 0));
            sc.PresetFoley = foley.Count == 0 ? null : foley;
        }
        if (!string.IsNullOrWhiteSpace(bgmPath)) project.BgmPath = bgmPath;
        LastAction = "🎞 비트 싱크 적용 · " + m.ToString(CultureInfo.InvariantCulture) + "컷";
        Raise();
        return m;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("BeatSync.OnChange", ex); }
    }
}
