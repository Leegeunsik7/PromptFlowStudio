using System.Diagnostics;
using System.Numerics;
using System.Reflection;

namespace PromptFlowStudio.Services.LiveTelemetry;

/// <summary>프로브 통계(값 형식 · µs).</summary>
public readonly record struct ProbeStats(int Count, float MeanUs, float MinUs, float MaxUs, float P95Us, float LastUs, long Total);

/// <summary>런타임 표본(값 형식).</summary>
public readonly record struct RuntimeSample(long Tick, float CpuLoad, float HeapMb, float AllocMbPerSec, int Gen0, int Gen1, int Gen2, int ThreadPoolThreads, long PendingWork, float RenderFps, int RenderStacks);

/// <summary>
/// [PromptFlow Studio v16.94.0] 📊 실시간 라이브 텔레메트리 &amp; 마이크로초 병목 프로파일러(무할당 핫패스).
///   ▸ 능동 프로브: <c>using var _ = profiler.Measure(slot)</c>(ref struct · Stopwatch 틱) → 슬롯별 링 512(µs) + log₂ 히스토그램 24 버킷(p95 O(24)).
///   ▸ 수동 게이지: 러너의 LastTickMs/LastFrameMs/LastStepMs 등 속성 → 등록 시 1회 리플렉션으로 Func 델리게이트 결합 → 표본 시 무할당 호출.
///   ▸ 통계: System.Numerics.Vector&lt;float&gt; SIMD 합/최소/최대(스칼라 꼬리) · 스칼라 경로와 최소/최대 동일 · 합 상대 오차 1e-5(테스트 검증).
///   ▸ 런타임: GC.CollectionCount(0/1/2) Δ · GetTotalMemory(false) · GetTotalAllocatedBytes Δ/s · 프로세스 CPU(AppDomain 모니터링 Δ ÷ 경과 × 코어) · ThreadPool 스레드/대기.
///     세대별 점유(GetGCMemoryInfo)는 내부 객체 할당이 있어 저주기(기본 1 s) 콜드 경로로 분리.
///   ▸ UI 렌더 스태킹: <see cref="MarkRender"/>(HUD OnAfterRender) — 프레임 예산(16.6 ms) 내 2회 이상 렌더 = 스택 · FPS EMA.
///   코어별 부하는 BCL 비지원 → 프로세스 전체 정규화(0..1) 보고.
/// </summary>
public sealed class RealtimeTelemetryBottleneckProfilerEngine
{
    public const int MaxProbes = 48, Ring = 512, Buckets = 24, SeriesLen = 120;

    private readonly string[] _names = new string[MaxProbes];
    private readonly float[][] _ring = new float[MaxProbes][];
    private readonly int[] _head = new int[MaxProbes], _count = new int[MaxProbes];
    private readonly long[] _total = new long[MaxProbes];
    private readonly int[,] _hist = new int[MaxProbes, Buckets];
    private readonly float[] _last = new float[MaxProbes];
    private readonly Func<double>?[] _gauge = new Func<double>?[MaxProbes];
    private readonly float[] _gaugeScale = new float[MaxProbes];
    private readonly RuntimeSample[] _series = new RuntimeSample[SeriesLen];
    private readonly float[][] _probeSeries = new float[MaxProbes][];
    private readonly long[] _genBytes = new long[4];
    private int _n, _seriesHead, _seriesCount;
    private long _tick, _lastWall, _lastAlloc, _lastRender, _lastGenInfo;
    private TimeSpan _lastCpu;
    private int _g0, _g1, _g2, _stacksInWindow;
    private float _fps;
    private static readonly double UsPerTick = 1_000_000d / Stopwatch.Frequency;

    public RealtimeTelemetryBottleneckProfilerEngine()
    {
        try { AppDomain.MonitoringIsEnabled = true; } catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException) { PromptFlowStudio.Program.Log("Telemetry.Monitoring", ex); }
        _lastWall = Stopwatch.GetTimestamp(); _lastAlloc = GC.GetTotalAllocatedBytes(false);
        _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
        _lastCpu = CpuNow();
    }

    public int Count => _n;
    public string Name(int i) => _names[i];
    public bool IsGauge(int i) => _gauge[i] is not null;
    public long Samples => _tick;
    public long RenderCount { get; private set; }
    public long RenderStacks { get; private set; }
    public float RenderFps => _fps;
    public double GenInfoIntervalMs { get; set; } = 1000d;
    public float RenderBudgetMs { get; set; } = 16.6f;
    public long Gen0Bytes => _genBytes[0];
    public long Gen1Bytes => _genBytes[1];
    public long Gen2Bytes => _genBytes[2];
    public long LohBytes => _genBytes[3];
    public int SeriesCount => _seriesCount;
    /// <summary>시계열(back = 0 최신).</summary>
    public RuntimeSample Series(int back) => _series[(_seriesHead - 1 - back + 2 * SeriesLen) % SeriesLen];
    public float ProbeSeries(int slot, int back) => _probeSeries[slot][(_seriesHead - 1 - back + 2 * SeriesLen) % SeriesLen];
    public RuntimeSample Latest => _seriesCount == 0 ? default : Series(0);

    /// <summary>능동 프로브 슬롯 등록(중복 이름 = 기존 슬롯).</summary>
    public int Register(string name)
    {
        lock (_names)
        {
            for (var i = 0; i < _n; i++) if (_names[i] == name) return i;
            if (_n >= MaxProbes) return -1;
            _names[_n] = name; _ring[_n] = new float[Ring]; _probeSeries[_n] = new float[SeriesLen];
            return _n++;
        }
    }

    /// <summary>수동 게이지 등록(ms 반환 델리게이트 → µs).</summary>
    public int RegisterGauge(string name, Func<double> ms)
    {
        var s = Register(name);
        if (s >= 0) { _gauge[s] = ms; _gaugeScale[s] = 1000f; }
        return s;
    }

    /// <summary>러너 인스턴스의 지연 속성(ms) 자동 결합 — 미지원 형식은 -1.</summary>
    public int RegisterRunner(string name, object runner)
    {
        foreach (var pn in GaugeProperties)
        {
            var p = runner.GetType().GetProperty(pn, BindingFlags.Public | BindingFlags.Instance);
            if (p?.GetMethod is not { } g) continue;
            if (p.PropertyType == typeof(double)) return RegisterGauge(name, g.CreateDelegate<Func<double>>(runner));
            if (p.PropertyType == typeof(float)) { var f = g.CreateDelegate<Func<float>>(runner); return RegisterGauge(name, () => f()); }
        }
        return -1;
    }

    private static readonly string[] GaugeProperties = { "LastTickMs", "LastFrameMs", "LastStepMs", "LastScanMs", "TickMs", "FrameMs" };

    /// <summary>v16.71~v16.91 스튜디오 러너(실행 가드 모듈명과 동일 이름) 게이지 등록 → 결합 수.</summary>
    public int RegisterStudios(IServiceProvider sp)
    {
        var n = 0;
        for (var i = 0; i < StudioTypes.Length; i++)
            if (sp.GetService(StudioTypes[i].Type) is { } s && RegisterRunner(StudioTypes[i].Name, s) >= 0) n++;
        return n;
    }

    public static readonly (Type Type, string Name)[] StudioTypes =
    {
        (typeof(LiveStream.LiveBroadcastStudioRunner), "라이브 방송"), (typeof(OpenUsd.OpenUsdSpatialStudioRunner), "OpenUSD 공간"), (typeof(Twin.DigitalTwinStudioRunner), "디지털 트윈"),
        (typeof(PathTracing.PathTracingStudioRunner), "경로 추적"), (typeof(Mocap.MotionCaptureStudioRunner), "모션 캡처"), (typeof(SpatialAudio.SpatialAudioStudioRunner), "공간 오디오"),
        (typeof(Holo.HolographicTeleportationStudioRunner), "홀로그래픽"), (typeof(Cinema.InteractiveCinemaStudioRunner), "인터랙티브 시네마"), (typeof(Editorial.AutonomousEditorialStudioRunner), "자율 편집"),
        (typeof(CinematicMastering.VirtualProductionMasteringStudioRunner), "가상 프로덕션 마스터링"), (typeof(GenerativeVfx.GenerativeVfxStudioRunner), "VFX 파티클"), (typeof(DigitalHuman.DigitalHumanPerformanceStudioRunner), "디지털 휴먼"),
        (typeof(HairCloth.NeuralHairClothStudioRunner), "헤어·의상"), (typeof(Atmosphere.GenerativeEnvironmentStudioRunner), "환경·기상"), (typeof(Cinematography.NeuralCinematographyStudioRunner), "시네마토그래피"),
        (typeof(FilmScore.FilmScoreStudioRunner), "영화 음악"), (typeof(VideoDiffusion.LatentVideoDiffusionStudioRunner), "비디오 디퓨전"), (typeof(Continuity.ScriptContinuityStudioRunner), "스크립트 연속성"),
        (typeof(Localization.Dubbing.NeuralLipSyncDubbingStudioRunner), "립싱크 더빙"), (typeof(ContentSecurity.ContentSecurityStudioRunner), "콘텐츠 보안"), (typeof(ProductionCrew.MultiAgentCrewStudioRunner), "프로덕션 크루"),
    };

    /// <summary>측정 스코프(ref struct · 무할당).</summary>
    public Scope Measure(int slot) => new(this, slot, Stopwatch.GetTimestamp());

    public ref struct Scope
    {
        private readonly RealtimeTelemetryBottleneckProfilerEngine? _p;
        private readonly int _slot;
        private readonly long _t0;
        internal Scope(RealtimeTelemetryBottleneckProfilerEngine p, int slot, long t0) { _p = p; _slot = slot; _t0 = t0; }
        public void Dispose() { if (_p is not null && _slot >= 0) _p.Record(_slot, (float)((Stopwatch.GetTimestamp() - _t0) * UsPerTick)); }
    }

    /// <summary>표본 기록(µs · 무할당 · 단일 기록자 가정 — 동시 기록 시 근사).</summary>
    public void Record(int slot, float us)
    {
        if ((uint)slot >= (uint)_n || !(us >= 0f)) return;
        var h = _head[slot];
        _ring[slot][h] = us;
        _head[slot] = (h + 1) & (Ring - 1);
        if (_count[slot] < Ring) _count[slot]++;
        _total[slot]++;
        _last[slot] = us;
        _hist[slot, Bucket(us)]++;
    }

    private static int Bucket(float us)
    {
        if (us < 1f) return 0;
        var b = 1 + BitOperations.Log2((uint)Math.Min(us, (float)int.MaxValue));
        return b >= Buckets ? Buckets - 1 : b;
    }

    /// <summary>통계(SIMD 합/최소/최대 + 히스토그램 p95 상한).</summary>
    public ProbeStats Stats(int slot)
    {
        if ((uint)slot >= (uint)_n) return default;
        var c = _count[slot];
        if (c == 0) return new ProbeStats(0, 0, 0, 0, 0, 0, _total[slot]);
        var (sum, min, max) = SimdSumMinMax(_ring[slot].AsSpan(0, c));
        return new ProbeStats(c, sum / c, min, max, P95(slot, max), _last[slot], _total[slot]);
    }

    private float P95(int slot, float max)
    {
        long tot = 0;
        for (var b = 0; b < Buckets; b++) tot += _hist[slot, b];
        if (tot == 0) return 0f;
        var target = (long)Math.Ceiling(tot * 0.95);
        long acc = 0;
        for (var b = 0; b < Buckets; b++)
        {
            acc += _hist[slot, b];
            if (acc >= target) return MathF.Min(max, b == 0 ? 1f : (float)(1u << b)); // 버킷 상한(µs)
        }
        return max;
    }

    public static (float Sum, float Min, float Max) SimdSumMinMax(ReadOnlySpan<float> v)
    {
        float sum = 0f, min = float.MaxValue, max = float.MinValue;
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && v.Length >= w)
        {
            var vs = Vector<float>.Zero; var vmin = new Vector<float>(float.MaxValue); var vmax = new Vector<float>(float.MinValue);
            for (; i <= v.Length - w; i += w)
            {
                var x = new Vector<float>(v.Slice(i, w));
                vs += x; vmin = Vector.Min(vmin, x); vmax = Vector.Max(vmax, x);
            }
            for (var k = 0; k < w; k++) { sum += vs[k]; min = MathF.Min(min, vmin[k]); max = MathF.Max(max, vmax[k]); }
        }
        for (; i < v.Length; i++) { sum += v[i]; min = MathF.Min(min, v[i]); max = MathF.Max(max, v[i]); }
        return (sum, min, max);
    }

    public static (float Sum, float Min, float Max) ScalarSumMinMax(ReadOnlySpan<float> v)
    {
        float sum = 0f, min = float.MaxValue, max = float.MinValue;
        for (var i = 0; i < v.Length; i++) { sum += v[i]; min = MathF.Min(min, v[i]); max = MathF.Max(max, v[i]); }
        return (sum, min, max);
    }

    /// <summary>UI 렌더 완료 표식(무할당) — 예산 내 연속 렌더 = 스태킹.</summary>
    public void MarkRender()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Exchange(ref _lastRender, now);
        RenderCount++;
        if (last == 0) return;
        var ms = (now - last) * 1000d / Stopwatch.Frequency;
        if (ms < RenderBudgetMs) { RenderStacks++; Interlocked.Increment(ref _stacksInWindow); }
        var inst = ms > 0 ? (float)(1000d / ms) : 0f;
        _fps = _fps <= 0f ? inst : _fps * 0.9f + MathF.Min(inst, 1000f) * 0.1f;
    }

    /// <summary>런타임 표본 1회(무할당 · 세대 점유는 저주기 콜드 경로).</summary>
    public RuntimeSample Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Max(1e-6, (now - _lastWall) / (double)Stopwatch.Frequency);
        var cpu = CpuNow();
        var load = (float)Math.Clamp((cpu - _lastCpu).TotalSeconds / (dt * Environment.ProcessorCount), 0d, 1d);
        var alloc = GC.GetTotalAllocatedBytes(false);
        var allocRate = (float)((alloc - _lastAlloc) / dt / (1024d * 1024d));
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        if ((now - _lastGenInfo) * 1000d / Stopwatch.Frequency >= GenInfoIntervalMs) { _lastGenInfo = now; SampleGenerations(); }
        for (var s = 0; s < _n; s++)
            if (_gauge[s] is { } gauge)
            {
                double ms;
                try { ms = gauge(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { continue; }
                if (ms > 0d) Record(s, (float)(ms * _gaugeScale[s]));
            }
        var stacks = Interlocked.Exchange(ref _stacksInWindow, 0);
        var r = new RuntimeSample(++_tick, load, GC.GetTotalMemory(false) / (1024f * 1024f), allocRate, g0 - _g0, g1 - _g1, g2 - _g2, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, _fps, stacks);
        _series[_seriesHead] = r;
        for (var s = 0; s < _n; s++) _probeSeries[s][_seriesHead] = _last[s];
        _seriesHead = (_seriesHead + 1) % SeriesLen; if (_seriesCount < SeriesLen) _seriesCount++;
        _lastWall = now; _lastCpu = cpu; _lastAlloc = alloc; _g0 = g0; _g1 = g1; _g2 = g2;
        return r;
    }

    private void SampleGenerations()
    {
        try
        {
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            var gens = info.GenerationInfo;
            for (var k = 0; k < 4 && k < gens.Length; k++) _genBytes[k] = gens[k].SizeAfterBytes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { PromptFlowStudio.Program.Log("Telemetry.GenInfo", ex); }
    }

    private static TimeSpan CpuNow()
    {
        try { return AppDomain.MonitoringIsEnabled ? AppDomain.CurrentDomain.MonitoringTotalProcessorTime : TimeSpan.Zero; }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return TimeSpan.Zero; }
    }

    /// <summary>예산(µs) 초과 p95 프로브 중 최악 → 슬롯(-1 = 없음).</summary>
    public int WorstBottleneck(float budgetUs, out float p95)
    {
        p95 = 0f; var worst = -1;
        for (var s = 0; s < _n; s++)
        {
            var st = Stats(s);
            if (st.Count > 0 && st.P95Us > budgetUs && st.P95Us > p95) { p95 = st.P95Us; worst = s; }
        }
        return worst;
    }

    public void ResetProbe(int slot)
    {
        if ((uint)slot >= (uint)_n) return;
        _head[slot] = _count[slot] = 0; _total[slot] = 0; _last[slot] = 0f;
        for (var b = 0; b < Buckets; b++) _hist[slot, b] = 0;
    }
}
