using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.Synergy;

/// <summary>커널 벤치 결과(스칼라 원본 vs 가속 · 최소 시간 · 비트 동일성 · 가속 경로 스레드 할당 바이트).</summary>
public readonly record struct KernelBench(string Name, double ScalarMs, double AcceleratedMs, double Speedup, bool BitExact, long AllocatedBytes);

/// <summary>벤치 묶음 + 측정 구간 GC 세대별 수집 증가량.</summary>
public sealed record HyperBenchReport(string Isa, int Width, int Height, IReadOnlyList<KernelBench> Kernels, int Gen0, int Gen1, int Gen2, DateTime At)
{
    public bool AllBitExact { get { for (var i = 0; i < Kernels.Count; i++) if (!Kernels[i].BitExact) return false; return true; } }
    public long TotalAllocated { get { long s = 0; for (var i = 0; i < Kernels.Count; i++) s += Kernels[i].AllocatedBytes; return s; } }
}

/// <summary>
/// [PromptFlow Studio v16.62.0] ⚡ 엔진 하이퍼 가속 &amp; 메모리 가드.
///   ▸ ICVFX 콘텐츠 프리필터(3탭 분리형) — 열 방향 스트라이드 순회를 행 우선 롤링 버퍼로 재구성 + Vector512/256/128 폭 분기(스칼라 꼬리).
///   ▸ BCI 그레이딩 셰이크 — 픽셀당 클램프 복사를 행 단위 블록 복사(memmove) + 가장자리 채움으로 재구성.
///   ▸ 두 커널 모두 스칼라 원본과 비트 동일(FMA 미사용 · 동일 연산 순서) → <see cref="Enabled"/> 토글은 결과 불변 · 런타임 전환 안전.
///   ▸ 스크래치는 ArrayPool 대여/반환(워밍업 후 0B) · 벤치는 스레드 할당 바이트 + GC 세대별 수집 증가량으로 0B 검증.
/// </summary>
public sealed class EngineHyperAccelerationOptimizer
{
    private static volatile bool s_enabled = true;
    private static long s_calls;

    private readonly object _gate = new();
    private float[] _fIn = Array.Empty<float>(), _fA = Array.Empty<float>(), _fB = Array.Empty<float>();
    private byte[] _bIn = Array.Empty<byte>(), _bA = Array.Empty<byte>(), _bB = Array.Empty<byte>();

    public event Action? OnChange;

    /// <summary>전역 하이퍼 가속 토글(엔진 핫패스가 정적으로 조회).</summary>
    public static bool Enabled { get => s_enabled; set => s_enabled = value; }
    public static long KernelCalls => Interlocked.Read(ref s_calls);

    public static string Isa => Vector512.IsHardwareAccelerated ? "AVX-512" : Vector256.IsHardwareAccelerated ? "AVX2" : Vector128.IsHardwareAccelerated ? "SSE/NEON" : "Scalar";
    public static int LaneFloats => Vector512.IsHardwareAccelerated ? 16 : Vector256.IsHardwareAccelerated ? 8 : Vector128.IsHardwareAccelerated ? 4 : 1;

    public HyperBenchReport? Last { get; private set; }
    public string? LastAction { get; private set; }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        LastAction = on ? "⚡ 하이퍼 가속 ON · " + Isa : "⏸ 하이퍼 가속 OFF(스칼라 원본)";
        Raise();
    }

    // ══════════════════════ 커널 ══════════════════════

    /// <summary>o[i] = c[i]·k0 + (p[i] + q[i])·a — 원본 스칼라 식과 동일 연산 순서.</summary>
    private static void Blend3(ReadOnlySpan<float> c, ReadOnlySpan<float> p, ReadOnlySpan<float> q, Span<float> o, float k0, float a)
    {
        var n = o.Length;
        var i = 0;
        ref var rc = ref MemoryMarshal.GetReference(c);
        ref var rp = ref MemoryMarshal.GetReference(p);
        ref var rq = ref MemoryMarshal.GetReference(q);
        ref var ro = ref MemoryMarshal.GetReference(o);
        if (Vector512.IsHardwareAccelerated && n >= 16)
        {
            Vector512<float> vk = Vector512.Create(k0), va = Vector512.Create(a);
            for (; i <= n - 16; i += 16)
                (Vector512.LoadUnsafe(ref rc, (nuint)i) * vk + (Vector512.LoadUnsafe(ref rp, (nuint)i) + Vector512.LoadUnsafe(ref rq, (nuint)i)) * va).StoreUnsafe(ref ro, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated && n - i >= 8)
        {
            Vector256<float> vk = Vector256.Create(k0), va = Vector256.Create(a);
            for (; i <= n - 8; i += 8)
                (Vector256.LoadUnsafe(ref rc, (nuint)i) * vk + (Vector256.LoadUnsafe(ref rp, (nuint)i) + Vector256.LoadUnsafe(ref rq, (nuint)i)) * va).StoreUnsafe(ref ro, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated && n - i >= 4)
        {
            Vector128<float> vk = Vector128.Create(k0), va = Vector128.Create(a);
            for (; i <= n - 4; i += 4)
                (Vector128.LoadUnsafe(ref rc, (nuint)i) * vk + (Vector128.LoadUnsafe(ref rp, (nuint)i) + Vector128.LoadUnsafe(ref rq, (nuint)i)) * va).StoreUnsafe(ref ro, (nuint)i);
        }
        for (; i < n; i++) o[i] = c[i] * k0 + (p[i] + q[i]) * a;
    }

    /// <summary>가속 3탭 프리필터(interleaved RGB float · 가장자리 복제) — <see cref="PrefilterScalar"/>와 비트 동일.</summary>
    public static void Prefilter3Tap(Span<float> rgb, int w, int h, float a)
    {
        if (w <= 0 || h <= 0 || a <= 0f || rgb.Length < w * h * 3) return;
        Interlocked.Increment(ref s_calls);
        var n = w * 3;
        var k0 = 1 - 2 * a;
        var rent = ArrayPool<float>.Shared.Rent(n * 2);
        try
        {
            var orig = rent.AsSpan(0, n);
            var prev = rent.AsSpan(n, n);
            // 수평: 행 원본 사본 기준 ±3 이웃(같은 채널)
            for (var y = 0; y < h; y++)
            {
                var row = rgb.Slice(y * n, n);
                row.CopyTo(orig);
                if (w == 1) { for (var k = 0; k < 3; k++) row[k] = orig[k] * k0 + (orig[k] + orig[k]) * a; continue; }
                for (var k = 0; k < 3; k++) row[k] = orig[k] * k0 + (orig[k] + orig[k + 3]) * a;
                if (n > 6) Blend3(orig.Slice(3, n - 6), orig.Slice(0, n - 6), orig.Slice(6, n - 6), row.Slice(3, n - 6), k0, a);
                for (var k = n - 3; k < n; k++) row[k] = orig[k] * k0 + (orig[k - 3] + orig[k]) * a;
            }
            // 수직: 행 우선 롤링(이전 원본 행 · 다음 행은 아직 미수정)
            for (var y = 0; y < h; y++)
            {
                var row = rgb.Slice(y * n, n);
                row.CopyTo(orig);
                ReadOnlySpan<float> up = y == 0 ? orig : prev;
                ReadOnlySpan<float> down = y + 1 < h ? rgb.Slice((y + 1) * n, n) : orig;
                Blend3(orig, up, down, row, k0, a);
                var t = prev; prev = orig; orig = t;
            }
        }
        finally { ArrayPool<float>.Shared.Return(rent); }
    }

    /// <summary>원본 스칼라(v16.59 <c>PrefilterContent</c> 동일 식 · 열 방향 스트라이드).</summary>
    public static void PrefilterScalar(Span<float> rgb, int w, int h, float a)
    {
        if (a <= 0f) return;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < h; y++)
            {
                var prev = rgb[(y * w) * 3 + c];
                for (var x = 0; x < w; x++)
                {
                    var k = (y * w + x) * 3 + c;
                    var cur = rgb[k];
                    var next = x + 1 < w ? rgb[k + 3] : cur;
                    rgb[k] = cur * (1 - 2 * a) + (prev + next) * a;
                    prev = cur;
                }
            }
            for (var x = 0; x < w; x++)
            {
                var prev = rgb[x * 3 + c];
                for (var y = 0; y < h; y++)
                {
                    var k = (y * w + x) * 3 + c;
                    var cur = rgb[k];
                    var next = y + 1 < h ? rgb[k + w * 3] : cur;
                    rgb[k] = cur * (1 - 2 * a) + (prev + next) * a;
                    prev = cur;
                }
            }
        }
    }

    /// <summary>가속 셰이크 이동(가장자리 클램프) — 행 블록 복사 · <see cref="ShiftClampScalar"/>와 비트 동일.</summary>
    public static void ShiftClampRgb(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int sx, int sy)
    {
        if (w <= 0 || h <= 0 || src.Length < w * h * 3 || dst.Length < w * h * 3) return;
        Interlocked.Increment(ref s_calls);
        var n = w * 3;
        int x0 = Math.Clamp(sx, 0, w), x1 = Math.Clamp(w + sx, 0, w);
        for (var y = 0; y < h; y++)
        {
            var s = src.Slice(Math.Clamp(y - sy, 0, h - 1) * n, n);
            var d = dst.Slice(y * n, n);
            if (x1 > x0) s.Slice((x0 - sx) * 3, (x1 - x0) * 3).CopyTo(d.Slice(x0 * 3));
            for (var x = 0; x < x0; x++) { d[x * 3] = s[0]; d[x * 3 + 1] = s[1]; d[x * 3 + 2] = s[2]; }
            for (var x = Math.Max(x1, x0); x < w; x++) { d[x * 3] = s[n - 3]; d[x * 3 + 1] = s[n - 2]; d[x * 3 + 2] = s[n - 1]; }
        }
    }

    /// <summary>원본 스칼라(v16.56 <c>GradeFrame</c> 셰이크 루프 동일).</summary>
    public static void ShiftClampScalar(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h, int sx, int sy)
    {
        for (var y = 0; y < h; y++)
        {
            var syy = Math.Clamp(y - sy, 0, h - 1);
            for (var x = 0; x < w; x++)
            {
                var sxx = Math.Clamp(x - sx, 0, w - 1);
                int d = (y * w + x) * 3, s = (syy * w + sxx) * 3;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2];
            }
        }
    }

    /// <summary>합 RGB(interleaved float) — 8픽셀(24 float) 단위 3누산기 · 위상별 채널 환산.</summary>
    public static void SumRgb(ReadOnlySpan<float> rgb, out float r, out float g, out float b)
    {
        double sr = 0, sg = 0, sb = 0;
        var n = rgb.Length / 3 * 3;
        var i = 0;
        if (Vector256.IsHardwareAccelerated && n >= 24)
        {
            Vector256<float> a0 = Vector256<float>.Zero, a1 = a0, a2 = a0;
            ref var rr = ref MemoryMarshal.GetReference(rgb);
            for (; i <= n - 24; i += 24)
            {
                a0 += Vector256.LoadUnsafe(ref rr, (nuint)i);
                a1 += Vector256.LoadUnsafe(ref rr, (nuint)(i + 8));
                a2 += Vector256.LoadUnsafe(ref rr, (nuint)(i + 16));
            }
            for (var l = 0; l < 8; l++)
            {
                Acc(l, a0.GetElement(l), ref sr, ref sg, ref sb);
                Acc(l + 8, a1.GetElement(l), ref sr, ref sg, ref sb);
                Acc(l + 16, a2.GetElement(l), ref sr, ref sg, ref sb);
            }
        }
        for (; i < n; i += 3) { sr += rgb[i]; sg += rgb[i + 1]; sb += rgb[i + 2]; }
        r = (float)sr; g = (float)sg; b = (float)sb;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Acc(int lane, float v, ref double r0, ref double g0, ref double b0)
        {
            switch (lane % 3) { case 0: r0 += v; break; case 1: g0 += v; break; default: b0 += v; break; }
        }
    }

    // ══════════════════════ 벤치 · 메모리 가드 ══════════════════════

    private static void Ensure<T>(ref T[] a, int n) { if (a.Length < n) a = new T[n]; }

    /// <summary>스칼라 원본 vs 가속 — 라운드별 교차 측정 최소 시간 · 비트 동일성 · 가속 구간 할당/GC 증가량.</summary>
    public HyperBenchReport Benchmark(int w = 480, int h = 270, int rounds = 7)
    {
        w = Math.Clamp(w, 8, 1920); h = Math.Clamp(h, 8, 1080); rounds = Math.Clamp(rounds, 1, 50);
        HyperBenchReport rep;
        lock (_gate)
        {
            var nf = w * h * 3;
            Ensure(ref _fIn, nf); Ensure(ref _fA, nf); Ensure(ref _fB, nf);
            Ensure(ref _bIn, nf); Ensure(ref _bA, nf); Ensure(ref _bB, nf);
            var rng = 0x2545F491u;
            for (var i = 0; i < nf; i++)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                _fIn[i] = (rng & 0xFFFF) / 65535f * 4f;
                _bIn[i] = (byte)(rng >> 24);
            }
            var fIn = _fIn.AsSpan(0, nf); var fA = _fA.AsSpan(0, nf); var fB = _fB.AsSpan(0, nf);
            var bIn = _bIn.AsSpan(0, nf); var bA = _bA.AsSpan(0, nf); var bB = _bB.AsSpan(0, nf);
            const float A = 0.2f;
            // 워밍업(JIT · ArrayPool 스레드 캐시)
            fIn.CopyTo(fB); Prefilter3Tap(fB, w, h, A); ShiftClampRgb(bIn, bB, w, h, 3, -2);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            double ps = double.MaxValue, pa = double.MaxValue, ss = double.MaxValue, sa = double.MaxValue;
            long pAlloc = 0, sAlloc = 0;
            for (var r = 0; r < rounds; r++)
            {
                fIn.CopyTo(fA);
                var t = Stopwatch.GetTimestamp();
                PrefilterScalar(fA, w, h, A);
                ps = Math.Min(ps, Ms(t));
                fIn.CopyTo(fB);
                var m = GC.GetAllocatedBytesForCurrentThread();
                t = Stopwatch.GetTimestamp();
                Prefilter3Tap(fB, w, h, A);
                pa = Math.Min(pa, Ms(t));
                pAlloc += GC.GetAllocatedBytesForCurrentThread() - m;

                var sx = (r & 1) == 0 ? 3 : -4; var sy = (r & 2) == 0 ? -2 : 1;
                t = Stopwatch.GetTimestamp();
                ShiftClampScalar(bIn, bA, w, h, sx, sy);
                ss = Math.Min(ss, Ms(t));
                m = GC.GetAllocatedBytesForCurrentThread();
                t = Stopwatch.GetTimestamp();
                ShiftClampRgb(bIn, bB, w, h, sx, sy);
                sa = Math.Min(sa, Ms(t));
                sAlloc += GC.GetAllocatedBytesForCurrentThread() - m;
            }
            var pExact = MemoryMarshal.AsBytes(fA).SequenceEqual(MemoryMarshal.AsBytes(fB));
            var sExact = bA.SequenceEqual(bB);
            int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
            rep = new HyperBenchReport(Isa, w, h, new[]
            {
                new KernelBench("ICVFX 프리필터(3탭)", ps, pa, pa > 0 ? ps / pa : 0, pExact, pAlloc),
                new KernelBench("BCI 그레이딩 셰이크", ss, sa, sa > 0 ? ss / sa : 0, sExact, sAlloc),
            }, d0, d1, d2, DateTime.Now);
            Last = rep;
        }
        LastAction = "🏁 벤치 " + rep.Isa + " · 프리필터 ×" + rep.Kernels[0].Speedup.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + " · 셰이크 ×" + rep.Kernels[1].Speedup.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + (rep.AllBitExact ? " · 비트 동일" : " · ⚠ 불일치");
        Raise();
        return rep;
    }

    private static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Synergy.Hyper.OnChange", ex); }
    }
}
