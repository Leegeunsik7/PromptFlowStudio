using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Hardening;

/// <summary>핫패스 본문(상태 ref · 무할당 스크래치). 정적 람다로 캐시해 호출 시 할당 0.</summary>
public delegate void HotPathBody<TState>(ref TState state, Span<float> scratch);

/// <summary>사이트별 누적 통계 스냅샷.</summary>
public readonly record struct HotPathSiteStats(int Site, string Name, long Calls, long Bytes, long MaxBytes, long Violations, long Gen0, long Gen1, long Gen2, bool Bypassed, long Rerouted, bool Instrumented);

/// <summary>
/// [PromptFlow Studio v16.51.0] 🔬 Zero-Alloc SIMD/AVX 핫패스 프로파일링 가드.
///   ▸ 사이트 슬롯(v16.0~v16.51 · 64 상한)별 Begin/End 프로브: 스레드 할당 바이트 · 구간 중 GC Gen0/1/2 발생 · 최대치 · 위반(예산 초과) — Interlocked, 무할당.
///   ▸ 인라인 경고: 위반 즉시 OnViolation(사이트 · 바이트) · 마지막 경고 문자열은 getter 지연 포맷.
///   ▸ 자동 우회: 연속 위반 임계 도달 사이트 → 바이패스 활성(사이트 전용 64B 정렬 고정 스크래치 1회 확보) → Execute 가 무할당 SIMD 경로로 라우팅.
///   ▸ 백그라운드 샘플러(1초): 프로세스 할당률 · GC 세대 카운트 · 사이트별 윈도 델타 링(히트맵 32창).
///   ▸ SimdKernels: Vector512 → Vector256 → Vector128 → Vector&lt;T&gt; → 스칼라(꼬리) — 스칼라 대비 비트 동일.
/// </summary>
public sealed class ZeroAllocHotPathProfilingGuard : IDisposable
{
    public const int MaxSites = 64, Windows = 32, ScratchFloats = 16384;

    private readonly string[] _names = new string[MaxSites];
    private readonly long[] _calls = new long[MaxSites], _bytes = new long[MaxSites], _max = new long[MaxSites], _viol = new long[MaxSites], _streak = new long[MaxSites];
    private readonly long[] _g0 = new long[MaxSites], _g1 = new long[MaxSites], _g2 = new long[MaxSites], _rerouted = new long[MaxSites];
    private readonly long[] _budget = new long[MaxSites];
    private readonly float[]?[] _scratch = new float[MaxSites][];
    private readonly bool[] _bypass = new bool[MaxSites], _instrumented = new bool[MaxSites];
    private readonly long[] _heat = new long[MaxSites * Windows], _lastBytes = new long[MaxSites];
    private readonly ZeroGcCounters? _zgc;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private int _count, _window, _lastWarnSite = -1;
    private long _lastWarnBytes, _samples, _totalViolations, _lastProcAlloc = -1, _lastSampleTicks;
    private double _procRate;

    public ZeroAllocHotPathProfilingGuard() : this(null) { }

    public ZeroAllocHotPathProfilingGuard(ZeroGcCounters? counters)
    {
        _zgc = counters;
        var names = DefaultSiteNames();
        for (var i = 0; i < names.Length; i++) _names[i] = names[i];
        _count = names.Length;
    }

    public event Action<int, long>? OnViolation;
    public event Action? OnChange;

    public int BypassThreshold { get; set; } = 3;
    public int SiteCount => _count;
    public string SiteName(int site) => _names[site];
    public long Samples => Interlocked.Read(ref _samples);
    public long TotalViolations => Interlocked.Read(ref _totalViolations);
    public double ProcessAllocBytesPerSec => _procRate;
    public int CurrentWindow => _window;
    public bool Sampling => _timer is not null;
    public string? LastWarning { get { var s = _lastWarnSite; return s < 0 ? null : "⚠ " + _names[s] + " 핫패스 할당 " + _lastWarnBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B" + (_bypass[s] ? " → SIMD 무할당 우회 활성" : ""); } }

    /// <summary>사이트 등록(이름 중복 시 기존 인덱스). 상한 초과 시 -1.</summary>
    public int Register(string name, long budgetBytes = 0)
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++) if (_names[i] == name) { _budget[i] = budgetBytes; return i; }
            if (_count >= MaxSites) return -1;
            _names[_count] = name; _budget[_count] = budgetBytes;
            return _count++;
        }
    }

    public void SetBudget(int site, long bytes) => _budget[site] = Math.Max(0, bytes);

    // ══════════════════════ 프로브 ══════════════════════

    /// <summary>프로브 토큰(값 형식 — 할당 0).</summary>
    public readonly struct Probe
    {
        internal readonly int Site, G0, G1, G2;
        internal readonly long Start;
        internal Probe(int site) { Site = site; G0 = GC.CollectionCount(0); G1 = GC.CollectionCount(1); G2 = GC.CollectionCount(2); Start = GC.GetAllocatedBytesForCurrentThread(); }
    }

    public Probe Begin(int site) => new(site);

    /// <summary>프로브 종료 → 통계 반영. 반환 = 측정 바이트. 무할당(경고 이벤트 구독자 제외).</summary>
    public long End(in Probe p)
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread() - p.Start;
        var s = p.Site;
        if ((uint)s >= (uint)_count) return bytes;
        _instrumented[s] = true;
        Interlocked.Increment(ref _calls[s]);
        if (bytes > 0) Interlocked.Add(ref _bytes[s], bytes);
        long cur;
        while (bytes > (cur = Volatile.Read(ref _max[s])) && Interlocked.CompareExchange(ref _max[s], bytes, cur) != cur) { }
        var d0 = GC.CollectionCount(0) - p.G0;
        if (d0 > 0) Interlocked.Add(ref _g0[s], d0);
        var d1 = GC.CollectionCount(1) - p.G1;
        if (d1 > 0) Interlocked.Add(ref _g1[s], d1);
        var d2 = GC.CollectionCount(2) - p.G2;
        if (d2 > 0) Interlocked.Add(ref _g2[s], d2);
        if (bytes > _budget[s])
        {
            Interlocked.Increment(ref _viol[s]);
            Interlocked.Increment(ref _totalViolations);
            _lastWarnSite = s; _lastWarnBytes = bytes;
            if (Interlocked.Increment(ref _streak[s]) >= BypassThreshold && !_bypass[s]) EnableBypass(s);
            try { OnViolation?.Invoke(s, bytes); } catch (Exception ex) { PromptFlowStudio.Program.Log("HotPath.OnViolation", ex); }
        }
        else Volatile.Write(ref _streak[s], 0);
        return bytes;
    }

    /// <summary>핫패스 실행: 바이패스 사이트는 무할당 경로(고정 스크래치) · 아니면 기본 경로 — 모두 프로브.</summary>
    public void Execute<TState>(int site, ref TState state, HotPathBody<TState> primary, HotPathBody<TState> zeroAlloc)
    {
        var p = Begin(site);
        if ((uint)site < (uint)_count && _bypass[site] && _scratch[site] is { } buf)
        {
            zeroAlloc(ref state, buf);
            Interlocked.Increment(ref _rerouted[site]);
        }
        else primary(ref state, Span<float>.Empty);
        End(p);
    }

    /// <summary>바이패스 활성(사이트 스크래치 1회 확보 — 고정 · 정렬 해제 불필요한 POH 배열).</summary>
    public void EnableBypass(int site)
    {
        lock (_gate)
        {
            if (_bypass[site]) return;
            _scratch[site] ??= GC.AllocateUninitializedArray<float>(ScratchFloats, pinned: true);
            _bypass[site] = true;
        }
        Raise();
    }

    public void DisableBypass(int site) { _bypass[site] = false; Volatile.Write(ref _streak[site], 0); Raise(); }

    public bool IsBypassed(int site) => _bypass[site];

    public HotPathSiteStats Stats(int site) => new(site, _names[site], Interlocked.Read(ref _calls[site]), Interlocked.Read(ref _bytes[site]), Interlocked.Read(ref _max[site]), Interlocked.Read(ref _viol[site]),
        Interlocked.Read(ref _g0[site]), Interlocked.Read(ref _g1[site]), Interlocked.Read(ref _g2[site]), _bypass[site], Interlocked.Read(ref _rerouted[site]), _instrumented[site]);

    public int InstrumentedCount { get { var n = 0; for (var i = 0; i < _count; i++) if (_instrumented[i]) n++; return n; } }

    public int ViolatingSites { get { var n = 0; for (var i = 0; i < _count; i++) if (Interlocked.Read(ref _viol[i]) > 0) n++; return n; } }

    public void ResetStats()
    {
        lock (_gate)
        {
            Array.Clear(_calls); Array.Clear(_bytes); Array.Clear(_max); Array.Clear(_viol); Array.Clear(_streak); Array.Clear(_g0); Array.Clear(_g1); Array.Clear(_g2);
            Array.Clear(_rerouted); Array.Clear(_heat); Array.Clear(_lastBytes); Array.Clear(_instrumented);
            Interlocked.Exchange(ref _totalViolations, 0); _lastWarnSite = -1;
        }
        Raise();
    }

    // ══════════════════════ 백그라운드 샘플러 · 히트맵 ══════════════════════

    /// <summary>윈도 1회 샘플: 사이트별 누적 바이트 델타 → 히트맵 링 · 프로세스 할당률. 무할당.</summary>
    public void Sample()
    {
        lock (_gate)
        {
            var w = (_window + 1) % Windows;
            for (var i = 0; i < _count; i++)
            {
                var b = Interlocked.Read(ref _bytes[i]);
                _heat[i * Windows + w] = b - _lastBytes[i];
                _lastBytes[i] = b;
            }
            _window = w;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var alloc = GC.GetTotalAllocatedBytes(false);
            if (_lastProcAlloc >= 0 && now > _lastSampleTicks) _procRate = (alloc - _lastProcAlloc) / ((now - _lastSampleTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
            _lastProcAlloc = alloc; _lastSampleTicks = now;
        }
        Interlocked.Increment(ref _samples);
    }

    /// <summary>히트맵 값(age 0 = 최신 윈도) — 바이트.</summary>
    public long Heat(int site, int age) => _heat[site * Windows + ((_window - age) % Windows + Windows) % Windows];

    public void StartSampling(int periodMs = 1000)
    {
        if (_timer is not null) return;
        _timer = new System.Threading.Timer(_ =>
        {
            try { Sample(); Raise(); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("HotPath.Sample", ex); }
        }, null, periodMs, periodMs);
    }

    public void StopSampling() { Interlocked.Exchange(ref _timer, null)?.Dispose(); Raise(); }

    public long ZeroGcViolations => (_zgc ?? ZeroGcCounters.Global).Violations;

    /// <summary>v16.0~v16.51 사이트 이름(마일스톤 시스템).</summary>
    public static string[] DefaultSiteNames()
    {
        var sys = PromptFlowStudio.Services.Empire.ExecutiveEmpireTelemetryAndRevenueMatrix.MilestoneCatalog();
        var res = new string[sys.Length + 2];
        for (var i = 0; i < sys.Length; i++) res[i] = sys[i].Version + " " + sys[i].Name;
        res[sys.Length] = "v16.50 미디어 제국";
        res[sys.Length + 1] = "v16.51 시스템 하드닝";
        return res;
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("HotPath.OnChange", ex); } }

    public void Dispose() => StopSampling();
}

/// <summary>무할당 SIMD 커널 — 모든 경로 public(스칼라 대비 비트 동일 검증용).</summary>
public static class SimdKernels
{
    /// <summary>dst[i] = a[i] * k + b[i] (곱·합 분리 — FMA 미사용으로 스칼라와 비트 동일).</summary>
    public static void ScaleAdd(ReadOnlySpan<float> a, float k, ReadOnlySpan<float> b, Span<float> dst)
    {
        var n = Math.Min(Math.Min(a.Length, b.Length), dst.Length);
        var i = 0;
        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        ref var rd = ref MemoryMarshal.GetReference(dst);
        if (Vector512.IsHardwareAccelerated)
        {
            var vk = Vector512.Create(k);
            for (; i <= n - Vector512<float>.Count; i += Vector512<float>.Count)
                (Vector512.LoadUnsafe(ref ra, (nuint)i) * vk + Vector512.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var vk = Vector256.Create(k);
            for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
                (Vector256.LoadUnsafe(ref ra, (nuint)i) * vk + Vector256.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var vk = Vector128.Create(k);
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
                (Vector128.LoadUnsafe(ref ra, (nuint)i) * vk + Vector128.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        }
        for (; i < n; i++) Unsafe.Add(ref rd, i) = Unsafe.Add(ref ra, i) * k + Unsafe.Add(ref rb, i);
    }

    public static void ScaleAddScalar(ReadOnlySpan<float> a, float k, ReadOnlySpan<float> b, Span<float> dst)
    {
        var n = Math.Min(Math.Min(a.Length, b.Length), dst.Length);
        for (var i = 0; i < n; i++) dst[i] = a[i] * k + b[i];
    }

    /// <summary>Vector&lt;T&gt; 경로(하드웨어 폭 자동).</summary>
    public static void ScaleAddVectorT(ReadOnlySpan<float> a, float k, ReadOnlySpan<float> b, Span<float> dst)
    {
        var n = Math.Min(Math.Min(a.Length, b.Length), dst.Length);
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vk = new Vector<float>(k);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(a.Slice(i)) * vk + new Vector<float>(b.Slice(i))).CopyTo(dst.Slice(i));
        }
        for (; i < n; i++) dst[i] = a[i] * k + b[i];
    }

    /// <summary>최대 절대값(피크) — 순서 무관 정확.</summary>
    public static float MaxAbs(ReadOnlySpan<float> a)
    {
        var i = 0; var m = 0f;
        ref var ra = ref MemoryMarshal.GetReference(a);
        if (Vector256.IsHardwareAccelerated && a.Length >= Vector256<float>.Count)
        {
            var acc = Vector256<float>.Zero;
            for (; i <= a.Length - Vector256<float>.Count; i += Vector256<float>.Count) acc = Vector256.Max(acc, Vector256.Abs(Vector256.LoadUnsafe(ref ra, (nuint)i)));
            for (var k = 0; k < Vector256<float>.Count; k++) m = Math.Max(m, acc.GetElement(k));
        }
        for (; i < a.Length; i++) m = Math.Max(m, Math.Abs(Unsafe.Add(ref ra, i)));
        return m;
    }

    public static float MaxAbsScalar(ReadOnlySpan<float> a) { var m = 0f; foreach (var v in a) m = Math.Max(m, Math.Abs(v)); return m; }

    /// <summary>클램프(정확).</summary>
    public static void Clamp(Span<float> a, float lo, float hi)
    {
        var i = 0;
        ref var ra = ref MemoryMarshal.GetReference(a);
        if (Vector256.IsHardwareAccelerated)
        {
            var vl = Vector256.Create(lo); var vh = Vector256.Create(hi);
            for (; i <= a.Length - Vector256<float>.Count; i += Vector256<float>.Count)
                Vector256.Min(Vector256.Max(Vector256.LoadUnsafe(ref ra, (nuint)i), vl), vh).StoreUnsafe(ref ra, (nuint)i);
        }
        for (; i < a.Length; i++) Unsafe.Add(ref ra, i) = Math.Min(Math.Max(Unsafe.Add(ref ra, i), lo), hi);
    }

    public static void ClampScalar(Span<float> a, float lo, float hi) { for (var i = 0; i < a.Length; i++) a[i] = Math.Min(Math.Max(a[i], lo), hi); }

    public static string ActivePath => Vector512.IsHardwareAccelerated ? "AVX-512" : Vector256.IsHardwareAccelerated ? "AVX2(256)" : Vector128.IsHardwareAccelerated ? "SSE/NEON(128)" : "스칼라";
}
