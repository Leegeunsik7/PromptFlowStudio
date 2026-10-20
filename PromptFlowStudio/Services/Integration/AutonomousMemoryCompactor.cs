using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

/// <summary>압축 라운드 결과(불변 · HUD/로그 표출).</summary>
public sealed record MemoryCompactionReport(
    long BeforeMb, long AfterMb, long ReclaimedMb,
    double FragBefore, double FragAfter, bool LohCompacted, string Note);

/// <summary>메모리 표본 스냅샷(불변 · 스파크라인 표출 — 스냅샷 시점에만 할당).</summary>
public sealed record MemorySampleView(long ManagedMb, long HeapMb, double FragmentationRatio, DateTimeOffset At);

/// <summary>
/// [PromptFlow Studio v8.8.2] 자율 메모리 압축기(관찰 가능 싱글턴 · 순수 BCL <see cref="GC"/>/<see cref="GCSettings"/>/<see cref="ArrayPool{T}"/> · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="System.Threading.Timer"/> 30초 틱마다 LOH/POH 포함 힙 파편화를 <b>무할당</b> 링버퍼(고정 struct 배열 · <see cref="Interlocked"/> 카운터)로 표집.
///   ▸ 파편화 임계 초과 시 <see cref="GCLargeObjectHeapCompactionMode.CompactOnce"/> + 압축 수집을 1회 예약 → LOH 홀 병합 · ArrayPool.Shared 버킷 트림 유발(자동 틱은 논블로킹 백그라운드 GC).
///   ▸ 핫패스 대여풀(<see cref="RentHot"/>/<see cref="ReturnHot"/>)은 관측 최대 크기 EWMA 로 버킷 상한을 자율 재튜닝하고, MMF 뉴럴 캐시는 히트율 저조 + 포화 시 자율 비움.
///   ⚠ <see cref="OnChange"/> 는 Timer(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장). 수집 실패는 삼켜 Graceful Degradation(앱 동작 무영향).
/// </summary>
public sealed class AutonomousMemoryCompactor : IDisposable
{
    private const int RingCap = 60;                     // 30초 × 60 = 최근 30분 표본
    private const double FragThreshold = 0.25;          // 힙 파편화 25% 초과 → 압축 트리거
    private const long HeapPressureMb = 512;            // 관리 힙 512MB 초과 → 압축 트리거
    private const double CacheHitFloor = 0.35;          // 캐시 히트율 하한(미만 + 포화 시 비움)
    private const double CacheFillCeiling = 0.85;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly Slot[] _ring = new Slot[RingCap];   // 사전 할당 고정 링(핫패스 할당 0)
    private readonly Inference.MultiModalNeuralCache? _cache;

    private ArrayPool<byte> _pool = ArrayPool<byte>.Create(1 << 20, 32);
    private int _poolMaxLen = 1 << 20;
    private int _ringHead;
    private int _ringCount;
    private long _rents;
    private long _returns;
    private long _observedPeak;
    private double _sizeEwma;
    private bool _disposed;

    public AutonomousMemoryCompactor(Inference.MultiModalNeuralCache? cache = null)
    {
        _cache = cache;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(8), TickInterval);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public bool AutoEnabled { get; private set; } = true;
    public long ManagedMb { get; private set; }
    public long HeapMb { get; private set; }
    public long LohMb { get; private set; }
    public long PohMb { get; private set; }
    public double FragmentationRatio { get; private set; }
    public int Gen0Count => GC.CollectionCount(0);
    public int Gen1Count => GC.CollectionCount(1);
    public int Gen2Count => GC.CollectionCount(2);
    public long CompactionCount { get; private set; }
    public long ReclaimedTotalMb { get; private set; }
    public long LastReclaimedMb { get; private set; }
    public long PoolRetunes { get; private set; }
    public long CacheTunes { get; private set; }
    public long SampleCount { get; private set; }
    public long HotRents => Interlocked.Read(ref _rents);
    public long HotReturns => Interlocked.Read(ref _returns);
    public int PoolMaxArrayLength => Volatile.Read(ref _poolMaxLen);
    public string? LastAction { get; private set; }
    public DateTimeOffset? LastCompactionAt { get; private set; }

    /// <summary>임계 초과 여부(HUD 경고등).</summary>
    public bool UnderPressure => FragmentationRatio >= FragThreshold || ManagedMb >= HeapPressureMb;

    /// <summary>최근 표본 스냅샷(신→구 · 스냅샷 시점에만 할당).</summary>
    public IReadOnlyList<MemorySampleView> Samples
    {
        get
        {
            lock (_gate)
            {
                var list = new List<MemorySampleView>(_ringCount);
                for (var i = 0; i < _ringCount; i++)
                {
                    var idx = (_ringHead - 1 - i + RingCap * 2) % RingCap;
                    var s = _ring[idx];
                    list.Add(new MemorySampleView(s.ManagedMb, s.HeapMb, s.Frag, new DateTimeOffset(s.AtTicks, TimeSpan.Zero).ToLocalTime()));
                }
                return list;
            }
        }
    }

    // ── 제어 ──

    /// <summary>자동 압축 토글(끄면 표집만 계속하고 GC 개입은 중단).</summary>
    public void SetAutoCompaction(bool enabled)
    {
        AutoEnabled = enabled;
        LastAction = enabled ? "자동 압축 활성" : "자동 압축 중지(표집만 유지)";
        Notify();
    }

    /// <summary>
    /// 압축 1회 실행 — LOH 압축 모드 예약 후 Gen2 수집. <paramref name="blocking"/> 이면 즉시 압축(수동), 아니면 백그라운드 수집(자동 틱).
    /// </summary>
    public MemoryCompactionReport Compact(bool blocking = true)
    {
        var beforeBytes = GC.GetTotalMemory(false);
        var fragBefore = FragmentationRatio;
        var lohCompacted = false;
        var note = "압축 수행";
        try
        {
            if (fragBefore >= FragThreshold || blocking)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                lohCompacted = true;
            }
            GC.Collect(2, GCCollectionMode.Forced, blocking, compacting: true);
            if (blocking) GC.WaitForPendingFinalizers();
        }
        catch (Exception ex)
        {
            note = "압축 실패 — 힙 상태 유지";
            Program.Log("MemoryCompactor.Compact", ex);
        }

        var afterBytes = GC.GetTotalMemory(false);
        var reclaimed = Math.Max(0, (beforeBytes - afterBytes) / (1024 * 1024));

        SampleNow();
        CompactionCount++;
        LastReclaimedMb = reclaimed;
        ReclaimedTotalMb += reclaimed;
        LastCompactionAt = DateTimeOffset.Now;
        PoolRetunes += RetunePool() ? 1 : 0;
        LastAction = $"{note} · 회수 {reclaimed}MB · 파편화 {(fragBefore * 100):0}%→{(FragmentationRatio * 100):0}%";
        Notify();

        return new MemoryCompactionReport(beforeBytes / (1024 * 1024), afterBytes / (1024 * 1024), reclaimed,
                                          fragBefore, FragmentationRatio, lohCompacted, note);
    }

    // ── 핫패스 대여풀(Zero-Alloc) ──

    /// <summary>핫패스 버퍼 대여 — 크기 통계만 <see cref="Interlocked"/> 로 갱신(할당 0).</summary>
    public byte[] RentHot(int minimumSize)
    {
        var size = Math.Max(16, minimumSize);
        Interlocked.Increment(ref _rents);
        var peak = Interlocked.Read(ref _observedPeak);
        if (size > peak) Interlocked.Exchange(ref _observedPeak, size);
        var prev = Volatile.Read(ref _sizeEwma);
        Interlocked.Exchange(ref _sizeEwma, prev <= 0 ? size : prev * 0.8 + size * 0.2);
        return size > PoolMaxArrayLength ? GC.AllocateUninitializedArray<byte>(size) : _pool.Rent(size);
    }

    /// <summary>핫패스 버퍼 반납(풀 상한 초과분은 GC 로 자연 회수 — 예외 없이 무시).</summary>
    public void ReturnHot(byte[]? buffer, bool clearArray = false)
    {
        if (buffer is null) return;
        Interlocked.Increment(ref _returns);
        if (buffer.Length > PoolMaxArrayLength) return;
        try { _pool.Return(buffer, clearArray); } catch { /* 이중 반납 방어 */ }
    }

    // ── 내부: 표집 · 자율 판정 ──

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            SampleNow();
            SampleCount++;
            var acted = false;

            if (AutoEnabled && UnderPressure)
            {
                Compact(blocking: false);   // 자동 틱은 논블로킹(UI 프레임 정지 회피)
                acted = true;
            }
            if (TuneCache()) acted = true;
            if (!acted)
            {
                LastAction = $"정상 · 힙 {ManagedMb}MB · 파편화 {(FragmentationRatio * 100):0}%";
                Notify();
            }
        }
        catch (Exception ex) { Program.Log("MemoryCompactor.Tick", ex); }
    }

    /// <summary>GC 메모리 정보 표집 — 고정 링버퍼 슬롯에 직접 기입(할당 0).</summary>
    private void SampleNow()
    {
        var info = GC.GetGCMemoryInfo();
        var managed = GC.GetTotalMemory(false);
        var heap = info.HeapSizeBytes > 0 ? info.HeapSizeBytes : managed;
        var frag = heap > 0 ? Math.Clamp(info.FragmentedBytes / (double)heap, 0, 1) : 0d;

        long loh = 0, poh = 0;
        var gens = info.GenerationInfo;
        if (gens.Length > 3) loh = gens[3].SizeAfterBytes;
        if (gens.Length > 4) poh = gens[4].SizeAfterBytes;

        ManagedMb = managed / (1024 * 1024);
        HeapMb = heap / (1024 * 1024);
        LohMb = loh / (1024 * 1024);
        PohMb = poh / (1024 * 1024);
        FragmentationRatio = frag;

        lock (_gate)
        {
            _ring[_ringHead] = new Slot(ManagedMb, HeapMb, frag, DateTimeOffset.UtcNow.Ticks);
            _ringHead = (_ringHead + 1) % RingCap;
            if (_ringCount < RingCap) _ringCount++;
        }
    }

    /// <summary>대여풀 버킷 상한 자율 재튜닝 — 관측 EWMA 대비 과대/과소면 풀을 재생성(드문 경로).</summary>
    private bool RetunePool()
    {
        var peak = (int)Math.Min(int.MaxValue, Math.Max(Interlocked.Read(ref _observedPeak), (long)Volatile.Read(ref _sizeEwma)));
        if (peak <= 0) return false;

        var current = PoolMaxArrayLength;
        var target = 1 << 16;
        while (target < peak && target < (1 << 24)) target <<= 1;   // 관측 피크를 덮는 최소 2의 거듭제곱

        if (target == current) return false;
        if (target < current && current <= target * 4) return false;  // 과대 판정은 4배 이상일 때만(플래핑 방지)

        _pool = ArrayPool<byte>.Create(target, 32);
        Volatile.Write(ref _poolMaxLen, target);
        Interlocked.Exchange(ref _observedPeak, 0);
        return true;
    }

    /// <summary>MMF 뉴럴 캐시 자율 튜닝 — 히트율 저조 + 포화 시 비워 파편/미스 누적 차단(멱등 · 캐시 없으면 무동작).</summary>
    private bool TuneCache()
    {
        if (_cache is null || _cache.Entries == 0) return false;
        if (_cache.HitRatio >= CacheHitFloor || _cache.FillRatio < CacheFillCeiling) return false;

        _cache.Clear();
        CacheTunes++;
        LastAction = $"뉴럴 캐시 자율 비움 — 히트율 {(_cache.HitRatio * 100):0}% · 포화 해소";
        Notify();
        return true;
    }

    /// <summary>링버퍼 슬롯(값 타입 — 박싱/힙 할당 없음).</summary>
    private readonly record struct Slot(long ManagedMb, long HeapMb, double Frag, long AtTicks);

    public void Dispose()
    {
        _disposed = true;
        try { _timer.Dispose(); } catch { /* 무시 */ }
    }
}
