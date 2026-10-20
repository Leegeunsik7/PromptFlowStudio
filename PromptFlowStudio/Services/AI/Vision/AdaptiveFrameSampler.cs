using System;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Vision;

/// <summary>v14.3.0 VLM 지연 분포 스냅샷(값형 · 할당 0). 히스토그램 버킷은 <see cref="AdaptiveFrameSampler.CopyHistogram"/> 로 별도 복사.</summary>
public readonly record struct VlmLatencyStats(int Count, float P50Ms, float P90Ms, float P95Ms, float P99Ms, float EwmaMs, float TimeoutRate, int IntervalMs);

/// <summary>
/// v14.3.0 가변 VLM 프레임 샘플러 — E2E 지연 p95·EWMA 로 샘플 간격을 100ms(10 FPS)~2000ms(0.5 FPS) 자율 스케일.
/// 타임아웃 빈도(EWMA 비율)가 임계 이상이면 즉시 2초로 완화해 GPU 보호. 기록·백분위·히스토그램 전부 고정 버퍼(할당 0).
/// </summary>
public sealed class AdaptiveFrameSampler
{
    public const int MinIntervalMs = 100;
    public const int MaxIntervalMs = 2000;
    public const int DefaultIntervalMs = 1000;
    public const int WindowSize = 64;          // 최근 지연 링버퍼(백분위 창) - stackalloc 정렬 상한
    public const int MinSamplesForP95 = 4;     // 미만이면 EWMA 만으로 판단
    public const float Alpha = 0.3f;           // 지연 EWMA 가중
    public const float TimeoutAlpha = 0.3f;    // 타임아웃 비율 EWMA 가중(1회 타임아웃 = 0.3 → 즉시 완화)
    public const float TimeoutRateThreshold = 0.25f;
    public const float Headroom = 1.2f;        // 다음 샘플 전 직전 추론 완료 여유

    /// <summary>히스토그램 버킷 상한(ms) - 마지막 버킷은 초과분(+∞).</summary>
    public static ReadOnlySpan<float> BucketUpperMs => s_bucketUpperMs;
    private static readonly float[] s_bucketUpperMs = { 100, 200, 300, 500, 750, 1000, 1500, 2000, 3000, float.PositiveInfinity };
    public const int BucketCount = 10;

    private readonly object _gate = new();
    private readonly float[] _window = new float[WindowSize];
    private int _head, _count;
    private int _ewmaBits;          // float 비트(Interlocked 스왑)
    private int _timeoutRateBits;
    private int _intervalMs = DefaultIntervalMs;
    private long _recorded, _timeouts, _scaleChanges;

    public int IntervalMs => Volatile.Read(ref _intervalMs);
    public float EwmaMs => BitConverter.Int32BitsToSingle(Volatile.Read(ref _ewmaBits));
    public float TimeoutRate => BitConverter.Int32BitsToSingle(Volatile.Read(ref _timeoutRateBits));
    public long Recorded => Interlocked.Read(ref _recorded);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    /// <summary>간격이 실제로 바뀐 횟수(타이머 재설정 빈도 관측).</summary>
    public long ScaleChanges => Interlocked.Read(ref _scaleChanges);
    /// <summary>현재 간격 기준 목표 FPS.</summary>
    public double TargetFps => 1000.0 / IntervalMs;

    /// <summary>순수 EWMA(테스트 가능 · 할당 0): seed 에서 시작해 samples 를 순서대로 반영.</summary>
    public static float Ewma(ReadOnlySpan<float> samples, float alpha, float seed)
    {
        var e = seed;
        for (var i = 0; i < samples.Length; i++) e = e <= 0 ? samples[i] : e + alpha * (samples[i] - e);
        return e;
    }

    /// <summary>지연 → 간격 매핑(순수 함수). 타임아웃 비율 임계 이상이면 무조건 최대.</summary>
    public static int ComputeIntervalMs(float p95Ms, float ewmaMs, float timeoutRate)
    {
        if (timeoutRate >= TimeoutRateThreshold) return MaxIntervalMs;
        var basis = Math.Max(p95Ms, ewmaMs) * Headroom;
        if (!(basis > MinIntervalMs)) return MinIntervalMs;   // NaN/0 포함
        return basis >= MaxIntervalMs ? MaxIntervalMs : (int)basis;
    }

    /// <summary>VLM 응답 1건의 E2E 지연 기록 → 새 간격 반환.</summary>
    public int RecordLatency(double latencyMs)
    {
        var ms = (float)Math.Max(0, latencyMs);
        Interlocked.Increment(ref _recorded);
        SwapEwma(ref _ewmaBits, ms, Alpha);
        SwapEwma(ref _timeoutRateBits, 0f, TimeoutAlpha, seedIfZero: false);
        float p95;
        lock (_gate)
        {
            _window[_head] = ms;
            _head = (_head + 1) % WindowSize;
            if (_count < WindowSize) _count++;
            p95 = _count >= MinSamplesForP95 ? PercentileLocked(0.95f) : 0f;
        }
        return Apply(ComputeIntervalMs(p95, EwmaMs, TimeoutRate));
    }

    /// <summary>타임아웃/무응답 1건 기록 - 빈도 상승 시 즉시 최대 간격(2초).</summary>
    public int RecordTimeout()
    {
        Interlocked.Increment(ref _timeouts);
        SwapEwma(ref _timeoutRateBits, 1f, TimeoutAlpha, seedIfZero: false);
        float p95;
        lock (_gate) p95 = _count >= MinSamplesForP95 ? PercentileLocked(0.95f) : 0f;
        return Apply(ComputeIntervalMs(p95, EwmaMs, TimeoutRate));
    }

    public VlmLatencyStats Snapshot()
    {
        float p50 = 0, p90 = 0, p95 = 0, p99 = 0; int n;
        lock (_gate)
        {
            n = _count;
            if (n > 0)
            {
                Span<float> s = stackalloc float[WindowSize];
                var sorted = SortedLocked(s);
                p50 = Pick(sorted, 0.50f); p90 = Pick(sorted, 0.90f); p95 = Pick(sorted, 0.95f); p99 = Pick(sorted, 0.99f);
            }
        }
        return new VlmLatencyStats(n, p50, p90, p95, p99, EwmaMs, TimeoutRate, IntervalMs);
    }

    /// <summary>최근 창의 버킷별 빈도를 destination(≥ <see cref="BucketCount"/>)에 복사 - 반환 = 최대 버킷 값(막대 정규화용).</summary>
    public int CopyHistogram(Span<int> destination)
    {
        var n = Math.Min(destination.Length, BucketCount);
        destination.Slice(0, n).Clear();
        var edges = BucketUpperMs;
        var max = 0;
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var v = _window[i];
                var b = 0;
                while (b < BucketCount - 1 && v > edges[b]) b++;
                if (b < n && ++destination[b] > max) max = destination[b];
            }
        }
        return max;
    }

    public void Reset()
    {
        lock (_gate) { _head = 0; _count = 0; Array.Clear(_window); }
        Volatile.Write(ref _ewmaBits, 0);
        Volatile.Write(ref _timeoutRateBits, 0);
        Volatile.Write(ref _intervalMs, DefaultIntervalMs);
    }

    private int Apply(int next)
    {
        if (Interlocked.Exchange(ref _intervalMs, next) != next) Interlocked.Increment(ref _scaleChanges);
        return next;
    }

    private static void SwapEwma(ref int bits, float sample, float alpha, bool seedIfZero = true)
    {
        while (true)
        {
            var seen = Volatile.Read(ref bits);
            var cur = BitConverter.Int32BitsToSingle(seen);
            var next = seedIfZero && cur <= 0 ? sample : cur + alpha * (sample - cur);
            if (Interlocked.CompareExchange(ref bits, BitConverter.SingleToInt32Bits(next), seen) == seen) return;
        }
    }

    private float PercentileLocked(float q)
    {
        Span<float> s = stackalloc float[WindowSize];
        return Pick(SortedLocked(s), q);
    }

    private ReadOnlySpan<float> SortedLocked(Span<float> scratch)
    {
        var s = scratch.Slice(0, _count);
        _window.AsSpan(0, _count).CopyTo(s);
        s.Sort();
        return s;
    }

    // nearest-rank
    private static float Pick(ReadOnlySpan<float> sorted, float q)
    {
        var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
