using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.ProductionCrew;

namespace PromptFlowStudio.Services.LiveTelemetry;

public enum TuneKnob : byte { BufferChunk, SimdWidth, ThreadPoolMin, CostFeedback, Bottleneck }

/// <summary>튜닝 결정(값 형식).</summary>
public readonly record struct TuneDecision(long Tick, TuneKnob Knob, double From, double To, string Reason);

/// <summary>
/// [PromptFlow Studio v16.94.0] 🎛 자율 성능 셀프 튜닝 &amp; 동적 메모리 버퍼 적응 합성기(규칙 기반 피드백 제어).
///   ▸ 버퍼: <see cref="RentChunk"/>/<see cref="Return"/>(ArrayPool&lt;byte&gt;.Shared) 요청 크기 log₂ 히스토그램 → p95 2의 거듭제곱으로 청크 재조정(히스테리시스 2회 연속) · 미스/낭비율.
///   ▸ SIMD 폭: 스칼라 · Vector128 · Vector&lt;float&gt;(256 가능 시) 커널(제곱합 4096) 실측 → 최속 폭 선택(10% 이상 개선 시 전환).
///   ▸ 스레드 풀: 대기 작업 ≥ 임계 &amp; CPU &lt; 0.85 → 최소 워커 +2(상한 코어×4) · 대기 0 지속 → 원값 방향 감쇠. 적용은 <see cref="ApplyThreadPool"/> 옵트인(전역 영향) · 해제 시 원값 복원.
///   ▸ 병목 차단: 프로브 p95(µs) &gt; 예산 → 실행 가드 모듈 선언 비용을 실측 EMA로 대체(<see cref="ApplyCostFeedback"/>) → 가드 선점/거부가 실측 기반으로 동작.
/// </summary>
public sealed class AutonomousPerformanceSelfTuningSynthesizer : IDisposable
{
    public const int SizeBuckets = 24, LogSize = 64, KernelLen = 4096;
    public static readonly string[] KnobNames = { "버퍼 청크", "SIMD 폭", "스레드 풀 최소", "비용 피드백", "병목" };

    private readonly RealtimeTelemetryBottleneckProfilerEngine _p;
    private readonly RealtimeProductionSwarmExecutionGuard? _guard;
    private readonly long[] _sizeHist = new long[SizeBuckets];
    private readonly TuneDecision[] _log = new TuneDecision[LogSize];
    private readonly float[] _kernel = new float[KernelLen];
    private readonly double[] _simdNs = new double[3];
    private readonly int _origMinWorker, _origMinIo;
    private int _logHead, _logCount, _pendingChunk, _chunkVotes, _idleTicks;
    private long _tick;
    private bool _applied;

    public AutonomousPerformanceSelfTuningSynthesizer(RealtimeTelemetryBottleneckProfilerEngine profiler, RealtimeProductionSwarmExecutionGuard? guard = null)
    {
        _p = profiler; _guard = guard;
        ThreadPool.GetMinThreads(out _origMinWorker, out _origMinIo);
        MinWorkerThreads = _origMinWorker;
        for (var i = 0; i < KernelLen; i++) _kernel[i] = (i % 97) * 0.01f;
    }

    public int ChunkBytes { get; private set; } = 64 * 1024;
    public int SimdLanes { get; private set; } = 1;
    public int MinWorkerThreads { get; private set; }
    public int OriginalMinWorkerThreads => _origMinWorker;
    public bool ApplyThreadPool { get; set; }
    public bool ApplyCostFeedback { get; set; } = true;
    public bool AutoTune { get; set; } = true;
    public float BottleneckBudgetUs { get; set; } = 8000f;
    public int PendingThreshold { get; set; } = 32;
    public long Rents { get; private set; }
    public long Misses { get; private set; }
    public long WastedBytes { get; private set; }
    public long RentedBytes { get; private set; }
    public double WasteRatio => RentedBytes == 0 ? 0d : (double)WastedBytes / RentedBytes;
    public double SimdNs(int k) => _simdNs[k];
    public int BottleneckSlot { get; private set; } = -1;
    public float BottleneckP95Us { get; private set; }
    public long CostUpdates { get; private set; }
    public long Decisions { get; private set; }
    public int LogCount => _logCount;
    public TuneDecision Log(int back) => _log[(_logHead - 1 - back + 2 * LogSize) % LogSize];

    /// <summary>적응 청크 대여(요청 크기 기록 · 무할당 히스토그램).</summary>
    public byte[] RentChunk(int minBytes)
    {
        var need = Math.Max(1, minBytes);
        Interlocked.Increment(ref _sizeHist[Math.Min(SizeBuckets - 1, BitOperations.Log2((uint)need - 1 | 1) + 1)]);
        Rents++;
        if (need > ChunkBytes) Misses++;
        var arr = ArrayPool<byte>.Shared.Rent(Math.Max(need, ChunkBytes));
        RentedBytes += arr.Length; WastedBytes += arr.Length - need;
        return arr;
    }

    public void Return(byte[] arr) => ArrayPool<byte>.Shared.Return(arr);

    /// <summary>요청 크기 p95 → 2의 거듭제곱(최소 4 KB · 최대 4 MB).</summary>
    public int RecommendedChunk()
    {
        long tot = 0;
        for (var b = 0; b < SizeBuckets; b++) tot += _sizeHist[b];
        if (tot == 0) return ChunkBytes;
        var target = (long)Math.Ceiling(tot * 0.95); long acc = 0;
        for (var b = 0; b < SizeBuckets; b++)
        {
            acc += _sizeHist[b];
            if (acc >= target) return Math.Clamp(1 << b, 4096, 4 * 1024 * 1024);
        }
        return ChunkBytes;
    }

    /// <summary>SIMD 폭 실측(0 = 스칼라 · 1 = Vector128 · 2 = Vector&lt;float&gt;) → 선택 레인 수.</summary>
    public int CalibrateSimd(int reps = 24)
    {
        var data = _kernel.AsSpan();
        _simdNs[0] = Time(0, data, reps);
        _simdNs[1] = Vector128.IsHardwareAccelerated ? Time(1, data, reps) : double.MaxValue;
        _simdNs[2] = Vector.IsHardwareAccelerated ? Time(2, data, reps) : double.MaxValue;
        var best = 0;
        for (var k = 1; k < 3; k++) if (_simdNs[k] < _simdNs[best] * 0.9) best = k;
        var lanes = best switch { 1 => Vector128<float>.Count, 2 => Vector<float>.Count, _ => 1 };
        if (lanes != SimdLanes) { Decide(TuneKnob.SimdWidth, SimdLanes, lanes, "커널 실측 최속 폭"); SimdLanes = lanes; }
        return lanes;
    }

    private static double Time(int kind, ReadOnlySpan<float> d, int reps)
    {
        SumSquares(kind, d);
        var t0 = Stopwatch.GetTimestamp();
        var acc = 0f;
        for (var r = 0; r < reps; r++) acc += SumSquares(kind, d);
        var ns = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6 / reps;
        return acc == float.NegativeInfinity ? 0 : ns;
    }

    /// <summary>제곱합 커널(폭별) — 결과 동등성은 테스트로 검증.</summary>
    public static float SumSquares(int kind, ReadOnlySpan<float> d)
    {
        var i = 0; var s = 0f;
        if (kind == 2 && Vector.IsHardwareAccelerated)
        {
            var acc = Vector<float>.Zero; var w = Vector<float>.Count;
            for (; i <= d.Length - w; i += w) { var x = new Vector<float>(d.Slice(i, w)); acc += x * x; }
            s = Vector.Sum(acc);
        }
        else if (kind == 1 && Vector128.IsHardwareAccelerated)
        {
            var acc = Vector128<float>.Zero;
            for (; i <= d.Length - 4; i += 4) { var x = Vector128.Create(d[i], d[i + 1], d[i + 2], d[i + 3]); acc += x * x; }
            s = Vector128.Sum(acc);
        }
        for (; i < d.Length; i++) s += d[i] * d[i];
        return s;
    }

    /// <summary>튜닝 1회: 버퍼 · 스레드 풀 · 비용 피드백 · 병목(무할당 — 결정 시 사유 문자열은 상수).</summary>
    public void Tune(in RuntimeSample s)
    {
        _tick++;
        if (!AutoTune) return;
        // 버퍼 청크(2회 연속 동일 권고 시 전환)
        var rec = RecommendedChunk();
        if (rec != ChunkBytes)
        {
            if (rec == _pendingChunk) _chunkVotes++; else { _pendingChunk = rec; _chunkVotes = 1; }
            if (_chunkVotes >= 2) { Decide(TuneKnob.BufferChunk, ChunkBytes, rec, rec > ChunkBytes ? "p95 요청 > 청크(미스)" : "p95 요청 ≪ 청크(낭비)"); ChunkBytes = rec; _chunkVotes = 0; }
        }
        // 스레드 풀
        var cap = Environment.ProcessorCount * 4;
        if (s.PendingWork >= PendingThreshold && s.CpuLoad < 0.85f && MinWorkerThreads < cap)
        {
            var to = Math.Min(cap, MinWorkerThreads + 2);
            Decide(TuneKnob.ThreadPoolMin, MinWorkerThreads, to, "대기 작업 적체 · CPU 여유");
            MinWorkerThreads = to; _idleTicks = 0; ApplyPool();
        }
        else if (s.PendingWork == 0 && MinWorkerThreads > _origMinWorker && ++_idleTicks >= 20)
        {
            var to = Math.Max(_origMinWorker, MinWorkerThreads - 1);
            Decide(TuneKnob.ThreadPoolMin, MinWorkerThreads, to, "적체 해소 · 원값 감쇠");
            MinWorkerThreads = to; _idleTicks = 0; ApplyPool();
        }
        // 병목 + 실측 비용 피드백
        var prev = BottleneckSlot;
        BottleneckSlot = _p.WorstBottleneck(BottleneckBudgetUs, out var p95);
        if (BottleneckSlot >= 0 && BottleneckSlot != prev) Decide(TuneKnob.Bottleneck, BottleneckP95Us, p95, "p95 예산 초과 병목 감지");
        BottleneckP95Us = p95;
        if (_guard is not null && ApplyCostFeedback) FeedCosts();
    }

    private void FeedCosts()
    {
        var g = _guard!;
        for (var s = 0; s < _p.Count; s++)
        {
            var st = _p.Stats(s);
            if (st.Count < 8) continue;
            var name = _p.Name(s);
            for (var m = 0; m < g.Count; m++)
            {
                var mod = g.Module(m);
                if (!string.Equals(mod.Name, name, StringComparison.Ordinal)) continue;
                var measured = st.P95Us / 1000f;
                var next = mod.CostMs * 0.7f + measured * 0.3f;
                if (MathF.Abs(next - mod.CostMs) > 0.05f)
                {
                    if (st.P95Us > BottleneckBudgetUs) Decide(TuneKnob.CostFeedback, mod.CostMs, next, "p95 예산 초과 → 가드 비용 상향");
                    mod.CostMs = next; CostUpdates++;
                }
                break;
            }
        }
    }

    private void ApplyPool()
    {
        if (!ApplyThreadPool) return;
        if (ThreadPool.SetMinThreads(MinWorkerThreads, _origMinIo)) _applied = true;
    }

    /// <summary>스레드 풀 원값 복원.</summary>
    public void RestoreThreadPool()
    {
        if (_applied) { ThreadPool.SetMinThreads(_origMinWorker, _origMinIo); _applied = false; }
        if (MinWorkerThreads != _origMinWorker) { Decide(TuneKnob.ThreadPoolMin, MinWorkerThreads, _origMinWorker, "수동 복원"); MinWorkerThreads = _origMinWorker; }
    }

    public void ResetBuffers()
    {
        Array.Clear(_sizeHist); Rents = Misses = WastedBytes = RentedBytes = 0; _chunkVotes = 0;
    }

    private void Decide(TuneKnob k, double from, double to, string reason)
    {
        _log[_logHead] = new TuneDecision(_tick, k, from, to, reason);
        _logHead = (_logHead + 1) % LogSize; if (_logCount < LogSize) _logCount++;
        Decisions++;
    }

    public void Dispose() => RestoreThreadPool();
}
