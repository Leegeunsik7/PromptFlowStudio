using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace PromptFlowStudio.Services.Pluto.Dsp;

/// <summary>🥁 BGM 비트 검출 계약 — WAV 바이트 → 타임라인(0~15s) 비트 타임스탬프(초 · 오름차순).</summary>
public interface IPlutoAudioBeatDetector
{
    IReadOnlyList<double> DetectBeats(ReadOnlySpan<byte> wavBytes, double minBpm = 60.0, double maxBpm = 140.0);
}

/// <summary>🥁 비트 그리드 분석 결과 — <see cref="IsFallback"/> = 무음/파싱 실패/온셋 부족으로 결정적 1초 격자 대체.</summary>
public sealed record PlutoBeatGrid(double Bpm, double PhaseSec, IReadOnlyList<double> Beats, int OnsetCount, bool IsFallback);

/// <summary>
/// 🥁 순수 C# 스펙트럴 플럭스 비트 검출기 — 외부 라이브러리 0.
///   ▸ 프레임 N=2048 · 홉 H=512 · Hann <c>w(n)=0.5−0.5cos(2πn/(N−1))</c> → 인플레이스 Cooley-Tukey Radix-2 FFT.
///   ▸ 플럭스 <c>SF(m)=Σₖ max(0, |X(m,k)|−|X(m−1,k)|)</c>(k=0..N/2) → 15프레임 이동 중앙값 × λ(1.35) 적응 임계 + 국소 최대 피크 픽킹.
///   ▸ IOI 클러스터링(Dixon) — 온셋 쌍 간격을 25ms 폭으로 군집 → 정수배 조화 점수 → BPM 범위 내 최고 군집 = 비트 주기.
///   ▸ 위상 탐색(5ms) + 매칭 온셋 최소제곱 회귀로 주기/위상 정밀화 → 0~15초 비트 격자.
/// 작업 버퍼는 <see cref="ArrayPool{T}"/> 대여(반환 보장) · 상태 없음(스레드 안전).
/// </summary>
public sealed class PlutoAudioBeatDetector : IPlutoAudioBeatDetector
{
    public static readonly PlutoAudioBeatDetector Shared = new();

    public const int FrameSize = 2048, HopSize = 512, Bins = FrameSize / 2 + 1;
    public const int MedianWindow = 15;
    public const double ThresholdLambda = 1.35;
    public const double TimelineSec = 15.0;
    /// <summary>분석 상한(초) — 루프 BGM 이 길어도 타임라인 비트 추정엔 충분.</summary>
    public const double MaxAnalysisSec = 30.0;
    /// <summary>IOI 군집 폭(초).</summary>
    public const double ClusterWidthSec = 0.025;
    private const double MinIoiSec = 0.07, MaxIoiSec = 2.5, MinOnsetGapSec = 0.05, PhaseStepSec = 0.005, MatchTolSec = 0.06;
    private const float SilencePeak = 1e-4f;

    private static readonly float[] s_hann = BuildHann();
    private static readonly float[] s_cos = BuildTwiddle(cos: true);
    private static readonly float[] s_sin = BuildTwiddle(cos: false);
    private static readonly ReadOnlyCollection<double> s_fallback = BuildFallback();

    /// <summary>결정적 폴백 격자 <c>0, 1, …, 15</c>(초).</summary>
    public static IReadOnlyList<double> FallbackGrid => s_fallback;

    public IReadOnlyList<double> DetectBeats(ReadOnlySpan<byte> wavBytes, double minBpm = 60.0, double maxBpm = 140.0)
        => Analyze(wavBytes, minBpm, maxBpm).Beats;

    /// <summary>WAV → 비트 그리드(파싱 실패 · 무음 · 예외 → 폴백).</summary>
    public PlutoBeatGrid Analyze(ReadOnlySpan<byte> wavBytes, double minBpm = 60.0, double maxBpm = 140.0)
    {
        if (!PlutoPcmWav.TryReadHeader(wavBytes, out var info)) return Fallback(0);
        var frames = (int)Math.Min(info.Frames, (long)(MaxAnalysisSec * info.SampleRate));
        var pool = ArrayPool<float>.Shared;
        var mono = pool.Rent(info.Frames);
        try
        {
            PlutoPcmWav.DecodeMono(wavBytes, in info, mono);
            return AnalyzeMono(mono.AsSpan(0, frames), info.SampleRate, minBpm, maxBpm);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            Program.Log("Pluto.Dsp.BeatDetector", ex);
            return Fallback(0);
        }
        finally { pool.Return(mono); }
    }

    /// <summary>모노 float 샘플 → 비트 그리드.</summary>
    public static PlutoBeatGrid AnalyzeMono(ReadOnlySpan<float> mono, int sampleRate, double minBpm = 60.0, double maxBpm = 140.0)
    {
        NormalizeRange(ref minBpm, ref maxBpm);
        if (sampleRate < 8000 || mono.Length < FrameSize) return Fallback(0);
        var peak = 0f;
        for (var i = 0; i < mono.Length; i++) { var a = Math.Abs(mono[i]); if (a > peak) peak = a; }
        if (!(peak > SilencePeak)) return Fallback(0);

        var frames = 1 + (mono.Length - FrameSize) / HopSize;
        var fp = ArrayPool<float>.Shared;
        var dp = ArrayPool<double>.Shared;
        float[] re = fp.Rent(FrameSize), im = fp.Rent(FrameSize), mag = fp.Rent(Bins), prev = fp.Rent(Bins), sf = fp.Rent(frames), strength = fp.Rent(frames);
        var onsets = dp.Rent(frames);
        try
        {
            ComputeSpectralFlux(mono, frames, re, im, mag, prev, sf);
            var count = PickOnsets(sf.AsSpan(0, frames), sampleRate, onsets, strength);
            if (count < 4) return Fallback(count);
            if (!EstimatePeriod(onsets.AsSpan(0, count), 60.0 / maxBpm, 60.0 / minBpm, out var period)) return Fallback(count);
            var phase = RefineGrid(onsets.AsSpan(0, count), strength.AsSpan(0, count), 60.0 / maxBpm, 60.0 / minBpm, ref period);
            return new PlutoBeatGrid(Math.Round(60.0 / period, 2), Math.Round(phase, 4), BuildGrid(phase, period), count, false);
        }
        finally
        {
            fp.Return(re); fp.Return(im); fp.Return(mag); fp.Return(prev); fp.Return(sf); fp.Return(strength);
            dp.Return(onsets);
        }
    }

    /// <summary>
    /// 인플레이스 Cooley-Tukey Radix-2 DIT FFT(순방향 <c>e^{−2πikn/N}</c>) — 길이는 2의 거듭제곱 · ≤ <see cref="FrameSize"/>.
    /// </summary>
    public static void Fft(Span<float> re, Span<float> im)
    {
        var n = re.Length;
        if (n != im.Length || n < 2 || n > FrameSize || (n & (n - 1)) != 0) throw new ArgumentException("FFT 길이는 2..2048 의 2의 거듭제곱이어야 합니다.", nameof(re));
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = FrameSize / len;
            for (var i = 0; i < n; i += len)
            {
                for (var k = 0; k < half; k++)
                {
                    float wr = s_cos[k * step], wi = -s_sin[k * step];
                    int a = i + k, b = a + half;
                    var xr = re[b] * wr - im[b] * wi;
                    var xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
    }

    // ══════════════════════ 🔧 내부 ══════════════════════

    private static void ComputeSpectralFlux(ReadOnlySpan<float> mono, int frames, float[] re, float[] im, float[] mag, float[] prev, float[] sf)
    {
        Array.Clear(prev, 0, Bins);
        for (var m = 0; m < frames; m++)
        {
            var src = mono.Slice(m * HopSize, FrameSize);
            for (var i = 0; i < FrameSize; i++) { re[i] = src[i] * s_hann[i]; im[i] = 0f; }
            Fft(re.AsSpan(0, FrameSize), im.AsSpan(0, FrameSize));
            var flux = 0.0;
            for (var k = 0; k < Bins; k++)
            {
                var v = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                var d = v - prev[k];
                if (d > 0) flux += d;
                mag[k] = v;
            }
            sf[m] = (float)flux;
            (mag, prev) = (prev, mag);   // 다음 프레임의 직전 스펙트럼
        }
    }

    /// <summary>적응 임계(15프레임 중앙값 × λ + δ) · ±3프레임 국소 최대 · 50ms 최소 간격 · 포물선 보간 → 온셋 시각(프레임 중심 기준).</summary>
    private static int PickOnsets(ReadOnlySpan<float> sf, int rate, double[] onsets, float[] strength)
    {
        double max = 0, mean = 0;
        for (var i = 0; i < sf.Length; i++) { mean += sf[i]; if (sf[i] > max) max = sf[i]; }
        mean /= sf.Length;
        if (!(max > 1e-9)) return 0;
        var delta = (float)(0.1 * mean + 1e-4 * max);   // 디지털 무음 구간의 미세 리플 차단
        var minGap = Math.Max(1, (int)Math.Round(MinOnsetGapSec * rate / HopSize));
        const int half = MedianWindow / 2, local = 3;
        Span<float> win = stackalloc float[MedianWindow];
        int count = 0, last = int.MinValue / 2;
        for (var m = 0; m < sf.Length; m++)
        {
            var v = sf[m];
            if (v <= delta) continue;
            var isMax = true;
            for (var k = Math.Max(0, m - local); k <= Math.Min(sf.Length - 1, m + local) && isMax; k++)
                if (k < m ? sf[k] >= v : k > m && sf[k] > v) isMax = false;
            if (!isMax || m - last < minGap) continue;

            int lo = Math.Max(0, m - half), hi = Math.Min(sf.Length - 1, m + half), w = hi - lo + 1;
            for (var k = 0; k < w; k++) win[k] = sf[lo + k];
            var med = Median(win.Slice(0, w));
            if (v <= ThresholdLambda * med + delta) continue;

            double off = 0;
            if (m > 0 && m < sf.Length - 1)
            {
                double a = sf[m - 1], c = sf[m + 1], den = a - 2.0 * v + c;
                if (den < 0) off = Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
            }
            onsets[count] = ((m + off) * HopSize + FrameSize / 2.0) / rate;
            strength[count] = v;
            count++;
            last = m;
        }
        return count;
    }

    private static float Median(Span<float> w)
    {
        for (var i = 1; i < w.Length; i++)   // 삽입 정렬(≤15)
        {
            var x = w[i];
            var j = i - 1;
            while (j >= 0 && w[j] > x) { w[j + 1] = w[j]; j--; }
            w[j + 1] = x;
        }
        var n = w.Length;
        return (n & 1) == 1 ? w[n >> 1] : 0.5f * (w[(n >> 1) - 1] + w[n >> 1]);
    }

    /// <summary>Dixon IOI 군집 → 조화 점수(n=1..8 · 가중 6−n, n≥5 → 1) → 범위 내 최고 군집 평균 = 주기.</summary>
    private static bool EstimatePeriod(ReadOnlySpan<double> onsets, double pMin, double pMax, out double period)
    {
        period = 0;
        var n = 0;
        for (var i = 0; i < onsets.Length; i++)
            for (var j = i + 1; j < onsets.Length && onsets[j] - onsets[i] <= MaxIoiSec; j++)
                if (onsets[j] - onsets[i] >= MinIoiSec) n++;
        if (n == 0) return false;

        var dp = ArrayPool<double>.Shared;
        var ip = ArrayPool<int>.Shared;
        double[] iois = dp.Rent(n), means = dp.Rent(n);
        var counts = ip.Rent(n);
        try
        {
            var c = 0;
            for (var i = 0; i < onsets.Length; i++)
                for (var j = i + 1; j < onsets.Length && onsets[j] - onsets[i] <= MaxIoiSec; j++)
                {
                    var d = onsets[j] - onsets[i];
                    if (d >= MinIoiSec) iois[c++] = d;
                }
            iois.AsSpan(0, c).Sort();

            var k = 0;
            for (var i = 0; i < c; i++)
            {
                if (k > 0 && iois[i] - means[k - 1] < ClusterWidthSec)
                {
                    counts[k - 1]++;
                    means[k - 1] += (iois[i] - means[k - 1]) / counts[k - 1];
                }
                else { means[k] = iois[i]; counts[k] = 1; k++; }
            }

            double bestScore = -1, bestAny = -1, bestAnyMean = 0;
            for (var i = 0; i < k; i++)
            {
                double score = 0;
                for (var h = 1; h <= 8; h++)
                {
                    var target = h * means[i];
                    if (target > MaxIoiSec + ClusterWidthSec) break;
                    var weight = h <= 4 ? 6 - h : 1;
                    for (var j = 0; j < k; j++)
                        if (Math.Abs(means[j] - target) < ClusterWidthSec) score += weight * counts[j];
                }
                if (score > bestAny) { bestAny = score; bestAnyMean = means[i]; }
                if (means[i] >= pMin && means[i] <= pMax && score > bestScore) { bestScore = score; period = means[i]; }
            }
            if (bestScore < 0)
            {
                // 범위 밖 최강 군집 → 2배/절반 접기.
                var p = bestAnyMean;
                for (var g = 0; g < 8 && p > 0 && (p < pMin || p > pMax); g++) p = p < pMin ? p * 2 : p * 0.5;
                if (!(p >= pMin && p <= pMax)) return false;
                period = p;
            }
            return true;
        }
        finally
        {
            dp.Return(iois); dp.Return(means); ip.Return(counts);
        }
    }

    /// <summary>위상 탐색(5ms · 강도 가중 삼각 커널) + 매칭 온셋 최소제곱 회귀 2회 → 위상(초 · [0, P)) · 주기 갱신.</summary>
    private static double RefineGrid(ReadOnlySpan<double> onsets, ReadOnlySpan<float> strength, double pMin, double pMax, ref double period)
    {
        var phase = SearchPhase(onsets, strength, period);
        for (var iter = 0; iter < 2; iter++)
        {
            double sk = 0, st = 0, skk = 0, skt = 0;
            var m = 0;
            for (var i = 0; i < onsets.Length; i++)
            {
                var k = Math.Round((onsets[i] - phase) / period);
                var dev = onsets[i] - (phase + k * period);
                if (Math.Abs(dev) > MatchTolSec * 1.5) continue;
                sk += k; st += onsets[i]; skk += k * k; skt += k * onsets[i];
                m++;
            }
            if (m < 3) break;
            var den = m * skk - sk * sk;
            if (Math.Abs(den) < 1e-9) break;
            var b = (m * skt - sk * st) / den;
            var a = (st - b * sk) / m;
            if (!(b >= pMin * 0.97 && b <= pMax * 1.03)) break;
            period = b;
            phase = a;
        }
        phase -= Math.Floor(phase / period) * period;
        return phase;
    }

    private static double SearchPhase(ReadOnlySpan<double> onsets, ReadOnlySpan<float> strength, double period)
    {
        double best = -1, bestPhase = 0;
        for (var phi = 0.0; phi < period; phi += PhaseStepSec)
        {
            double score = 0;
            for (var i = 0; i < onsets.Length; i++)
            {
                var x = (onsets[i] - phi) / period;
                var dev = Math.Abs(x - Math.Round(x)) * period;
                if (dev < MatchTolSec) score += strength[i] * (1.0 - dev / MatchTolSec);
            }
            if (score > best) { best = score; bestPhase = phi; }
        }
        return bestPhase;
    }

    private static IReadOnlyList<double> BuildGrid(double phase, double period)
    {
        var n = (int)Math.Floor((TimelineSec - phase) / period + 1e-9) + 1;
        var beats = new double[Math.Max(0, n)];
        for (var i = 0; i < beats.Length; i++) beats[i] = Math.Round(phase + i * period, 4);
        return Array.AsReadOnly(beats);
    }

    private static PlutoBeatGrid Fallback(int onsets) => new(60.0, 0.0, s_fallback, onsets, true);

    private static void NormalizeRange(ref double minBpm, ref double maxBpm)
    {
        if (!double.IsFinite(minBpm) || minBpm <= 0) minBpm = 60.0;
        if (!double.IsFinite(maxBpm) || maxBpm <= 0) maxBpm = 140.0;
        minBpm = Math.Clamp(minBpm, 30.0, 300.0);
        maxBpm = Math.Clamp(maxBpm, 30.0, 300.0);
        if (maxBpm <= minBpm * 1.05) { minBpm = 60.0; maxBpm = 140.0; }
    }

    private static float[] BuildHann()
    {
        var w = new float[FrameSize];
        for (var n = 0; n < FrameSize; n++) w[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (FrameSize - 1)));
        return w;
    }

    private static float[] BuildTwiddle(bool cos)
    {
        var t = new float[FrameSize / 2];
        for (var k = 0; k < t.Length; k++)
        {
            var a = 2 * Math.PI * k / FrameSize;
            t[k] = (float)(cos ? Math.Cos(a) : Math.Sin(a));
        }
        return t;
    }

    private static ReadOnlyCollection<double> BuildFallback()
    {
        var g = new double[(int)TimelineSec + 1];
        for (var i = 0; i < g.Length; i++) g[i] = i;
        return Array.AsReadOnly(g);
    }
}
