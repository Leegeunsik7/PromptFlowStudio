using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Edge;

namespace PromptFlowStudio.Services.LowSpec;

public enum QuantPrecision : byte { Fp32, Fp16, Int8, Int4 }

public enum ComputePath : byte { DiscreteGpu, IntegratedGpu, CpuAvx2, CpuNeon, CpuScalar }

public enum LowSpecWorkload : byte { World4D, Gaussian3D, Neural }

/// <summary>라우팅 결과 — 정밀도 · 경로 · 메모리 점유(MB) · 예산(MB) · 적합 여부.</summary>
public readonly record struct QuantRoute(LowSpecWorkload Workload, QuantPrecision Precision, ComputePath Path, float FootprintMb, float BudgetMb, bool Fits);

/// <summary>
/// [PromptFlow Studio v16.65.0] ⚡ 동적 INT4/INT8 가변 양자화 &amp; iGPU 폴백 라우터.
/// <see cref="Route"/>: 외장 GPU VRAM 예산 안에 FP16이 들어가면 dGPU · 아니면 iGPU 공유 메모리(여유 RAM 25%)에서 FP16 → INT8 → INT4 순 다운스케일 ·
/// 그래도 초과 시 CPU SIMD(AVX2 → NEON → 스칼라) 경로로 우회. 하한 정밀도(<see cref="Floor"/>) 존중.
/// 커널: 대칭 INT8(텐서 스케일) · 블록 INT4(32원소 스케일 · 니블 패킹) · int8 내적(Vector256/Vector128/스칼라 비트 동일) — 실행 경로 무할당.
/// </summary>
public sealed class DynamicLowBitQuantizationFallbackRouter
{
    public const int BlockSize = 32, Rows = 128, Cols = 256;

    private readonly object _gate = new();
    private readonly float[] _w = new float[Rows * Cols];
    private readonly sbyte[] _w8 = new sbyte[Rows * Cols];
    private readonly float[] _w8Scale = new float[Rows];
    private readonly byte[] _w4 = new byte[Rows * Cols / 2];
    private readonly float[] _w4Scale = new float[Rows * Cols / BlockSize];
    private readonly float[] _x = new float[Cols];
    private readonly sbyte[] _x8 = new sbyte[Cols];
    private readonly float[] _ref = new float[Rows];
    private readonly QuantRoute[] _routes = new QuantRoute[3];
    private long _kernelRuns, _fallbacks, _routed;
    private double _lastKernelMicros;

    public DynamicLowBitQuantizationFallbackRouter()
    {
        // 시드 고정 가중치(정규 근사) — 양자화 오차 실측용
        uint s = 0x9E3779B9;
        for (var i = 0; i < _w.Length; i++) _w[i] = Gauss(ref s) * 0.05f;
        for (var i = 0; i < _x.Length; i++) _x[i] = Gauss(ref s);
        for (var r = 0; r < Rows; r++)
        {
            var row = _w.AsSpan(r * Cols, Cols);
            _w8Scale[r] = QuantizeInt8(row, _w8.AsSpan(r * Cols, Cols));
            QuantizeInt4(row, _w4.AsSpan(r * Cols / 2, Cols / 2), _w4Scale.AsSpan(r * Cols / BlockSize, Cols / BlockSize));
            _ref[r] = DotF32(row, _x);
        }
        XScale = QuantizeInt8(_x, _x8);
        Span<float> o = stackalloc float[Rows];
        Int8RelError = RelError(RunKernelCore(QuantPrecision.Int8, o), o);
        Int4RelError = RelError(RunKernelCore(QuantPrecision.Int4, o), o);
    }

    public event Action? OnChange;

    public QuantPrecision Floor { get; set; } = QuantPrecision.Int8;
    public float XScale { get; }
    public float Int8RelError { get; }
    public float Int4RelError { get; }
    public long KernelRuns => Interlocked.Read(ref _kernelRuns);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public long Routed => Interlocked.Read(ref _routed);
    public double LastKernelMicros => Volatile.Read(ref _lastKernelMicros);
    public string? LastAction { get; private set; }

    public QuantRoute RouteOf(LowSpecWorkload w) { lock (_gate) return _routes[(int)w]; }

    /// <summary>CPU SIMD 경로 판별(AVX2 → NEON → 스칼라).</summary>
    public static ComputePath CpuPath(in HardwareSnapshot hw) =>
        hw.Avx2 ? ComputePath.CpuAvx2 : hw.AdvSimd ? ComputePath.CpuNeon : ComputePath.CpuScalar;

    public static float BytesPer(QuantPrecision p) => p switch { QuantPrecision.Fp32 => 4f, QuantPrecision.Fp16 => 2f, QuantPrecision.Int8 => 1f + 4f / Cols, _ => 0.5f + 4f / BlockSize };

    /// <summary>워크로드 파라미터(백만) → 정밀도/경로 결정(무할당).</summary>
    public QuantRoute Route(LowSpecWorkload workload, float paramsMillions, in HardwareSnapshot hw, float vramBudgetMb)
    {
        var floor = Floor;
        QuantRoute r;
        var gpuBudget = hw.HasDiscreteGpu ? MathF.Min(vramBudgetMb, hw.VramMb) : MathF.Min(vramBudgetMb, hw.AvailableRamMb * 0.25f);
        var gpuPath = hw.HasDiscreteGpu ? ComputePath.DiscreteGpu : hw.HasIntegratedGpu ? ComputePath.IntegratedGpu : ComputePath.CpuScalar;
        if (hw.HasDiscreteGpu && paramsMillions * BytesPer(QuantPrecision.Fp16) <= gpuBudget)
            r = new QuantRoute(workload, QuantPrecision.Fp16, gpuPath, paramsMillions * BytesPer(QuantPrecision.Fp16), gpuBudget, true);
        else if ((hw.HasDiscreteGpu || hw.HasIntegratedGpu) && TryFit(paramsMillions, gpuBudget, floor, out var gp))
            r = new QuantRoute(workload, gp, gpuPath, paramsMillions * BytesPer(gp), gpuBudget, true);
        else
        {
            // GPU 예산 초과 → CPU SIMD 우회(여유 RAM 50%)
            var cpuBudget = hw.AvailableRamMb * 0.5f;
            var fits = TryFit(paramsMillions, cpuBudget, QuantPrecision.Int4, out var cp);
            if (!fits) cp = QuantPrecision.Int4;
            if (cp < QuantPrecision.Int8 && floor >= QuantPrecision.Int8) cp = QuantPrecision.Int8; // CPU는 int8 SIMD가 최적
            r = new QuantRoute(workload, cp, CpuPath(hw), paramsMillions * BytesPer(cp), cpuBudget, fits);
            Interlocked.Increment(ref _fallbacks);
        }
        lock (_gate) _routes[(int)workload] = r;
        Interlocked.Increment(ref _routed);
        return r;
    }

    /// <summary>FP16 → INT8 → INT4 중 예산에 맞는 최고 정밀도(하한 이하 금지).</summary>
    private static bool TryFit(float paramsMillions, float budgetMb, QuantPrecision floor, out QuantPrecision p)
    {
        for (var q = QuantPrecision.Fp16; q <= floor; q++)
            if (paramsMillions * BytesPer(q) <= budgetMb) { p = q; return true; }
        p = floor;
        return false;
    }

    /// <summary>워크로드 3종 일괄 라우팅 · 1줄 요약(UI 버튼 경로 — 할당 허용).</summary>
    public void RouteAll(in HardwareSnapshot hw, float vramBudgetMb, float scale)
    {
        var a = Route(LowSpecWorkload.World4D, 900 * scale, hw, vramBudgetMb);
        Route(LowSpecWorkload.Gaussian3D, 600 * scale, hw, vramBudgetMb);
        Route(LowSpecWorkload.Neural, 1300 * scale, hw, vramBudgetMb);
        LastAction = "🧮 라우팅 " + PrecisionLabel(a.Precision) + " · " + PathLabel(a.Path);
        Raise();
    }

    /// <summary>양자화 행렬-벡터 곱 실행(128×256 · 무할당) — 경로 지연 실측 · FP32 기준 상대오차 반환.</summary>
    public float RunKernel(QuantPrecision p, Span<float> output)
    {
        var t0 = Stopwatch.GetTimestamp();
        var err = RelError(RunKernelCore(p, output), output);
        Volatile.Write(ref _lastKernelMicros, (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
        Interlocked.Increment(ref _kernelRuns);
        return err;
    }

    private float[] RunKernelCore(QuantPrecision p, Span<float> output)
    {
        var n = Math.Min(Rows, output.Length);
        Span<sbyte> tmp = stackalloc sbyte[BlockSize];
        for (var r = 0; r < n; r++)
        {
            output[r] = p switch
            {
                QuantPrecision.Int8 => DotInt8(_w8.AsSpan(r * Cols, Cols), _x8) * _w8Scale[r] * XScale,
                QuantPrecision.Int4 => DotInt4Int8(_w4.AsSpan(r * Cols / 2, Cols / 2), _w4Scale.AsSpan(r * Cols / BlockSize, Cols / BlockSize), _x8, tmp) * XScale,
                _ => DotF32(_w.AsSpan(r * Cols, Cols), _x),
            };
        }
        return _ref;
    }

    private static float RelError(float[] reference, ReadOnlySpan<float> got)
    {
        double num = 0, den = 0;
        for (var i = 0; i < reference.Length && i < got.Length; i++) { var d = reference[i] - got[i]; num += d * d; den += reference[i] * (double)reference[i]; }
        return den > 0 ? (float)Math.Sqrt(num / den) : 0f;
    }

    // ── 커널 ──

    /// <summary>대칭 INT8 양자화(absmax/127) — 스케일 반환.</summary>
    public static float QuantizeInt8(ReadOnlySpan<float> src, Span<sbyte> dst)
    {
        var max = 0f;
        for (var i = 0; i < src.Length; i++) max = MathF.Max(max, MathF.Abs(src[i]));
        var scale = max > 0 ? max / 127f : 1f;
        var inv = 1f / scale;
        for (var i = 0; i < src.Length && i < dst.Length; i++) dst[i] = (sbyte)Math.Clamp((int)MathF.Round(src[i] * inv), -127, 127);
        return scale;
    }

    public static void DequantizeInt8(ReadOnlySpan<sbyte> src, float scale, Span<float> dst)
    {
        for (var i = 0; i < src.Length && i < dst.Length; i++) dst[i] = src[i] * scale;
    }

    /// <summary>블록 INT4 양자화(32원소 absmax/7 · -7..7 · 짝수=하위 니블).</summary>
    public static void QuantizeInt4(ReadOnlySpan<float> src, Span<byte> packed, Span<float> scales)
    {
        var blocks = (src.Length + BlockSize - 1) / BlockSize;
        for (var b = 0; b < blocks && b < scales.Length; b++)
        {
            var o = b * BlockSize; var len = Math.Min(BlockSize, src.Length - o);
            var max = 0f;
            for (var i = 0; i < len; i++) max = MathF.Max(max, MathF.Abs(src[o + i]));
            var scale = max > 0 ? max / 7f : 1f;
            scales[b] = scale;
            var inv = 1f / scale;
            for (var i = 0; i < len; i++)
            {
                var q = Math.Clamp((int)MathF.Round(src[o + i] * inv), -7, 7) & 0xF;
                var k = (o + i) >> 1;
                if ((uint)k >= (uint)packed.Length) return;
                packed[k] = ((o + i) & 1) == 0 ? (byte)((packed[k] & 0xF0) | q) : (byte)((packed[k] & 0x0F) | (q << 4));
            }
        }
    }

    public static sbyte Nibble(byte packed, bool high) => (sbyte)((sbyte)((high ? packed : packed << 4) & 0xF0) >> 4);

    public static void DequantizeInt4(ReadOnlySpan<byte> packed, ReadOnlySpan<float> scales, Span<float> dst)
    {
        for (var i = 0; i < dst.Length && (i >> 1) < packed.Length && i / BlockSize < scales.Length; i++)
            dst[i] = Nibble(packed[i >> 1], (i & 1) == 1) * scales[i / BlockSize];
    }

    /// <summary>INT4(블록 스케일) × INT8 내적 — 블록 언팩 → int8 SIMD 내적 × 블록 스케일. <paramref name="scratch"/> ≥ 32.</summary>
    public static float DotInt4Int8(ReadOnlySpan<byte> packed, ReadOnlySpan<float> scales, ReadOnlySpan<sbyte> x, Span<sbyte> scratch)
    {
        var sum = 0f;
        var n = Math.Min(packed.Length * 2, x.Length);
        for (var o = 0; o < n && o / BlockSize < scales.Length; o += BlockSize)
        {
            var len = Math.Min(BlockSize, n - o);
            for (var i = 0; i < len; i++) scratch[i] = Nibble(packed[(o + i) >> 1], ((o + i) & 1) == 1);
            sum += DotInt8(scratch[..len], x.Slice(o, len)) * scales[o / BlockSize];
        }
        return sum;
    }

    /// <summary>int8 내적 디스패치 — Vector256(AVX2) → Vector128(NEON/SSE) → 스칼라.</summary>
    public static int DotInt8(ReadOnlySpan<sbyte> x, ReadOnlySpan<sbyte> w)
    {
        if (Vector256.IsHardwareAccelerated) return EdgeAutonomousAiAgentEngine.DotInt8(x, w);
        if (Vector128.IsHardwareAccelerated) return DotInt8Vector128(x, w);
        return DotInt8Scalar(x, w);
    }

    public static int DotInt8Vector128(ReadOnlySpan<sbyte> x, ReadOnlySpan<sbyte> w)
    {
        var n = Math.Min(x.Length, w.Length);
        var i = 0; var sum = 0;
        if (n >= 16)
        {
            var acc = Vector128<int>.Zero;
            ref var rx = ref MemoryMarshal.GetReference(x);
            ref var rw = ref MemoryMarshal.GetReference(w);
            for (; i <= n - 16; i += 16)
            {
                var (xl, xh) = Vector128.Widen(Vector128.LoadUnsafe(ref rx, (nuint)i));
                var (wl, wh) = Vector128.Widen(Vector128.LoadUnsafe(ref rw, (nuint)i));
                var (p0, p1) = Vector128.Widen(xl * wl);
                var (p2, p3) = Vector128.Widen(xh * wh);
                acc += p0 + p1 + p2 + p3;
            }
            sum = Vector128.Sum(acc);
        }
        for (; i < n; i++) sum += x[i] * w[i];
        return sum;
    }

    public static int DotInt8Scalar(ReadOnlySpan<sbyte> x, ReadOnlySpan<sbyte> w)
    {
        var n = Math.Min(x.Length, w.Length);
        var sum = 0;
        for (var i = 0; i < n; i++) sum += x[i] * w[i];
        return sum;
    }

    /// <summary>FP32 내적(Vector&lt;float&gt; · 스칼라 꼬리).</summary>
    public static float DotF32(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0; var sum = 0f;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..n]);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..n]);
            for (var k = 0; k < va.Length; k++) acc += va[k] * vb[k];
            sum = Vector.Sum(acc);
            i = va.Length * Vector<float>.Count;
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    private static float Gauss(ref uint s)
    {
        float u = 0;
        for (var k = 0; k < 4; k++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; u += (s & 0xFFFFFF) / 16777216f; }
        return (u - 2f) * 1.732f;
    }

    public static string PrecisionLabel(QuantPrecision p) => p switch { QuantPrecision.Fp32 => "FP32", QuantPrecision.Fp16 => "FP16", QuantPrecision.Int8 => "INT8", _ => "INT4" };

    public static string PathLabel(ComputePath p) => p switch
    {
        ComputePath.DiscreteGpu => "외장 GPU",
        ComputePath.IntegratedGpu => "내장 iGPU",
        ComputePath.CpuAvx2 => "CPU AVX2",
        ComputePath.CpuNeon => "CPU NEON",
        _ => "CPU 스칼라",
    };

    public static string WorkloadLabel(LowSpecWorkload w) => w switch { LowSpecWorkload.World4D => "4D 월드", LowSpecWorkload.Gaussian3D => "3DGS", _ => "신경망" };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LowSpec.Quant.OnChange", ex); }
    }
}
