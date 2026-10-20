using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime;
using System.Threading;

namespace PromptFlowStudio.Services.Agents;

/// <summary>[v9.3.3] 워치독 감지 이상 유형.</summary>
public enum WatchdogFaultKind
{
    /// <summary>이상 없음.</summary>
    None = 0,
    /// <summary>핫패스 교착(작업 중 하트비트 정지).</summary>
    Deadlock = 1,
    /// <summary>관리 힙 단조 증가(누수 추정).</summary>
    MemoryLeak = 2,
    /// <summary>힙 파편화 임계 초과.</summary>
    Fragmentation = 3,
    /// <summary>NPU/추론 과부하(스롯틀 Throttling 이상).</summary>
    NpuOverload = 4,
}

/// <summary>[v9.3.3] 감시 레인 스냅샷(조회 시점에만 할당).</summary>
public sealed record WatchdogLaneView(
    int Slot,
    string Name,
    bool Busy,
    double StallMs,
    long Beats,
    int Resets);

/// <summary>[v9.3.3] 복구 이력 1건.</summary>
public sealed record WatchdogRecovery(
    long Sequence,
    WatchdogFaultKind Kind,
    string Target,
    string Action,
    double Metric,
    DateTimeOffset At);

/// <summary>
/// [PromptFlow Studio v9.3.3] 자율 복구 파이프라인 워치독(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 핫패스 교착: 고정 16슬롯 레인에 <see cref="Beat"/> 틱을 <c>Interlocked</c> 로 기록 — Busy 상태에서 임계(기본 5초) 이상 정지 시
///     레인의 세션 <see cref="CancellationTokenSource"/> 를 취소·교체(무할당 세션 재설정 · 소유자는 <see cref="GetLaneToken"/> 로 재획득).
///   ▸ 메모리 누수: 8표본 링버퍼에서 관리 힙 단조 증가 + 누적 증가율을 판정 → Gen2 압축 수집(LOH Compact 1회).
///   ▸ 파편화: <see cref="GC.GetGCMemoryInfo()"/> FragmentedBytes/HeapSize 비율이 임계 초과 시 압축 수집.
///   ▸ NPU 과부하: <see cref="AutonomousCrashTelemetryExporter"/> 스롯틀 등급이 Throttling 이상이면 저전력 거버너를 Saver 로 강제하고,
///     Nominal 회복 시 강제를 해제한다(미주입 시 해당 축은 관측 제외).
///   ▸ 제3헌장: 2초 감시 타이머와 레인 CTS 전량 <see cref="Dispose"/> 해제. 제5헌장: 복구 실패는 이력만 남기고 감시를 지속한다.
/// </summary>
public sealed class SelfHealingPipelineWatchdog : IDisposable
{
    private const int LaneCapacity = 16;
    private const int HeapSamples = 8;
    private const int HistoryCap = 32;
    private const int TickMs = 2_000;
    private const double LeakGrowthRatio = 0.25d;
    private const double FragmentationRatio = 0.35d;
    private const int CompactCooldownTicks = 5;

    private readonly Lane[] _lanes = new Lane[LaneCapacity];
    private readonly long[] _heap = new long[HeapSamples];
    private readonly Queue<WatchdogRecovery> _history = new(HistoryCap);
    private readonly object _gate = new();
    private readonly AutonomousCrashTelemetryExporter? _crash;
    private readonly MobileLowPowerOptimizer? _power;
    // CS0104 회피: WinForms 헤드의 System.Windows.Forms.Timer 와 충돌하므로 완전 한정명 고정.
    private readonly System.Threading.Timer _timer;

    private int _laneCount;
    private int _heapCursor;
    private int _heapFilled;
    private int _compactCooldown;
    private long _ticks;
    private long _sequence;
    private int _deadlocks;
    private int _leaks;
    private int _fragmentations;
    private int _npuOverloads;
    private int _recoveries;
    private bool _npuThrottled;
    private bool _disposed;

    public SelfHealingPipelineWatchdog(
        AutonomousCrashTelemetryExporter? crash = null,
        MobileLowPowerOptimizer? power = null,
        double stallThresholdMs = 5_000d)
    {
        _crash = crash;
        _power = power;
        StallThresholdMs = double.IsNaN(stallThresholdMs) || stallThresholdMs < 250d ? 5_000d : stallThresholdMs;
        _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>복구 실행 알림.</summary>
    public event Action<WatchdogRecovery>? OnRecovered;

    /// <summary>교착 판정 임계(ms).</summary>
    public double StallThresholdMs { get; }

    /// <summary>감시 활성 여부(일시정지 시 false).</summary>
    public bool IsArmed { get; private set; } = true;

    /// <summary>등록 레인 수.</summary>
    public int LaneCount => Volatile.Read(ref _laneCount);

    /// <summary>누적 감시 틱.</summary>
    public long TickCount => Interlocked.Read(ref _ticks);

    /// <summary>누적 교착 복구.</summary>
    public int DeadlockCount => Volatile.Read(ref _deadlocks);

    /// <summary>누적 누수 복구.</summary>
    public int LeakCount => Volatile.Read(ref _leaks);

    /// <summary>누적 파편화 복구.</summary>
    public int FragmentationCount => Volatile.Read(ref _fragmentations);

    /// <summary>누적 NPU 과부하 감속.</summary>
    public int NpuOverloadCount => Volatile.Read(ref _npuOverloads);

    /// <summary>전체 복구 횟수.</summary>
    public int RecoveryCount => Volatile.Read(ref _recoveries);

    /// <summary>NPU 감속 강제 중 여부.</summary>
    public bool NpuThrottled => _npuThrottled;

    /// <summary>최근 힙 표본(바이트).</summary>
    public long HeapBytes { get; private set; }

    /// <summary>최근 파편화 비율(0~1).</summary>
    public double FragmentationRatioNow { get; private set; }

    /// <summary>표본 창 내 힙 증가율(첫 표본 대비 · 음수 가능).</summary>
    public double HeapGrowthRatio { get; private set; }

    /// <summary>최근 판정(없으면 None).</summary>
    public WatchdogFaultKind LastFault { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>건전성 — 교착 레인이 없고 NPU 감속이 아니면 true.</summary>
    public bool IsHealthy
    {
        get
        {
            if (_npuThrottled) return false;
            var now = Environment.TickCount64;
            var n = LaneCount;
            for (var i = 0; i < n; i++)
            {
                ref var lane = ref _lanes[i];
                if (Volatile.Read(ref lane.Busy) == 1 && now - Interlocked.Read(ref lane.LastBeat) >= StallThresholdMs) return false;
            }
            return true;
        }
    }

    // ── 레인 등록 & 하트비트(무할당 핫패스) ──

    /// <summary>핫패스 레인을 등록하고 슬롯 번호를 반환한다(동일 이름 재등록은 기존 슬롯 · 용량 초과 시 -1).</summary>
    public int RegisterLane(string name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "lane" : name;
        lock (_gate)
        {
            for (var i = 0; i < _laneCount; i++)
                if (string.Equals(_lanes[i].Name, label, StringComparison.Ordinal)) return i;
            if (_laneCount >= LaneCapacity) return -1;

            var slot = _laneCount;
            _lanes[slot] = new Lane
            {
                Name = label,
                Session = new CancellationTokenSource(),
                LastBeat = Environment.TickCount64,
            };
            Volatile.Write(ref _laneCount, slot + 1);
            Touch($"레인 등록 #{slot} {label}");
            return slot;
        }
    }

    /// <summary>레인 작업 시작/종료 표시(Busy 인 동안만 교착 판정).</summary>
    public void SetBusy(int slot, bool busy)
    {
        if ((uint)slot >= (uint)LaneCount) return;
        ref var lane = ref _lanes[slot];
        Interlocked.Exchange(ref lane.LastBeat, Environment.TickCount64);
        Volatile.Write(ref lane.Busy, busy ? 1 : 0);
    }

    /// <summary>하트비트(무할당 · Interlocked 2회).</summary>
    public void Beat(int slot)
    {
        if ((uint)slot >= (uint)LaneCount) return;
        ref var lane = ref _lanes[slot];
        Interlocked.Exchange(ref lane.LastBeat, Environment.TickCount64);
        Interlocked.Increment(ref lane.Beats);
    }

    /// <summary>레인 현재 세션 토큰(교착 복구 후에는 새 토큰 · 미등록 시 None).</summary>
    public CancellationToken GetLaneToken(int slot)
    {
        if ((uint)slot >= (uint)LaneCount) return CancellationToken.None;
        var cts = Volatile.Read(ref _lanes[slot].Session);
        try { return cts?.Token ?? CancellationToken.None; }
        catch (ObjectDisposedException) { return CancellationToken.None; }
    }

    /// <summary>레인 스냅샷(조회 시점에만 할당).</summary>
    public IReadOnlyList<WatchdogLaneView> Lanes
    {
        get
        {
            var n = LaneCount;
            var now = Environment.TickCount64;
            var list = new WatchdogLaneView[n];
            for (var i = 0; i < n; i++)
            {
                ref var lane = ref _lanes[i];
                var busy = Volatile.Read(ref lane.Busy) == 1;
                list[i] = new WatchdogLaneView(i, lane.Name ?? "lane", busy,
                    busy ? now - Interlocked.Read(ref lane.LastBeat) : 0d,
                    Interlocked.Read(ref lane.Beats), Volatile.Read(ref lane.Resets));
            }
            return list;
        }
    }

    /// <summary>최근 복구 이력(최신순).</summary>
    public IReadOnlyList<WatchdogRecovery> History
    {
        get
        {
            lock (_gate)
            {
                var arr = _history.ToArray();
                Array.Reverse(arr);
                return arr;
            }
        }
    }

    /// <summary>감시 일시정지/재개.</summary>
    public void SetArmed(bool armed)
    {
        IsArmed = armed;
        Touch(armed ? "감시 재개" : "감시 일시정지");
    }

    /// <summary>즉시 1회 감시(HUD 수동 프로브) — 판정된 최악 이상 유형을 반환한다.</summary>
    public WatchdogFaultKind ProbeNow() => Tick(force: true);

    /// <summary>교착 시뮬레이션 — 프로브 레인을 Busy 로 두고 하트비트를 임계 이전 시점으로 되돌린다.</summary>
    public int SimulateStall(string name = "hud-probe")
    {
        var slot = RegisterLane(name);
        if (slot < 0) return -1;
        ref var lane = ref _lanes[slot];
        Volatile.Write(ref lane.Busy, 1);
        Interlocked.Exchange(ref lane.LastBeat, Environment.TickCount64 - (long)StallThresholdMs - 1);
        Touch($"교착 주입 #{slot} {name}");
        return slot;
    }

    // ── 감시 루프 ──

    private WatchdogFaultKind Tick(bool force = false)
    {
        if (_disposed || (!IsArmed && !force)) return WatchdogFaultKind.None;
        Interlocked.Increment(ref _ticks);
        var worst = WatchdogFaultKind.None;

        try
        {
            worst = Max(worst, CheckDeadlocks());
            worst = Max(worst, CheckMemory());
            worst = Max(worst, CheckNpu());
        }
        catch (Exception ex)
        {
            // 제5헌장: 감시 자체 실패는 기록만 하고 다음 틱 지속.
            LastAction = "감시 실패 — " + ex.Message;
        }

        LastFault = worst;
        OnChange?.Invoke();
        return worst;
    }

    private WatchdogFaultKind CheckDeadlocks()
    {
        var found = WatchdogFaultKind.None;
        var now = Environment.TickCount64;
        var n = LaneCount;
        for (var i = 0; i < n; i++)
        {
            ref var lane = ref _lanes[i];
            if (Volatile.Read(ref lane.Busy) != 1) continue;
            var stall = now - Interlocked.Read(ref lane.LastBeat);
            if (stall < StallThresholdMs) continue;

            // 무할당 세션 재설정: 기존 CTS 취소 → 새 CTS 교체 → 레인 Idle 복귀(소유자는 토큰 재획득).
            var fresh = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref lane.Session, fresh);
            try { old?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
            old?.Dispose();

            Volatile.Write(ref lane.Busy, 0);
            Interlocked.Exchange(ref lane.LastBeat, now);
            Interlocked.Increment(ref lane.Resets);
            Interlocked.Increment(ref _deadlocks);
            Record(WatchdogFaultKind.Deadlock, lane.Name ?? "lane", "세션 CTS 취소·교체", stall);
            found = WatchdogFaultKind.Deadlock;
        }
        return found;
    }

    private WatchdogFaultKind CheckMemory()
    {
        var heap = GC.GetTotalMemory(false);
        HeapBytes = heap;
        _heap[_heapCursor] = heap;
        _heapCursor = (_heapCursor + 1) % HeapSamples;
        if (_heapFilled < HeapSamples) _heapFilled++;

        var info = GC.GetGCMemoryInfo();
        FragmentationRatioNow = info.HeapSizeBytes <= 0 ? 0d : info.FragmentedBytes / (double)info.HeapSizeBytes;

        if (_compactCooldown > 0) { _compactCooldown--; return WatchdogFaultKind.None; }

        // 누수: 표본 창이 가득 찼고 단조 비감소 + 누적 증가율 임계 초과.
        if (_heapFilled == HeapSamples)
        {
            var oldest = _heap[_heapCursor];
            var monotonic = true;
            for (var i = 1; i < HeapSamples; i++)
            {
                var a = _heap[(_heapCursor + i - 1) % HeapSamples];
                var b = _heap[(_heapCursor + i) % HeapSamples];
                if (b < a) { monotonic = false; break; }
            }
            HeapGrowthRatio = oldest <= 0 ? 0d : (heap - oldest) / (double)oldest;

            if (monotonic && HeapGrowthRatio >= LeakGrowthRatio)
            {
                var freed = Compact();
                Interlocked.Increment(ref _leaks);
                Record(WatchdogFaultKind.MemoryLeak, "managed-heap",
                    "Gen2 압축 수집 · " + (freed / 1024).ToString(CultureInfo.InvariantCulture) + "KB 회수", HeapGrowthRatio);
                _heapFilled = 0;   // 창 재시작(동일 추세 중복 판정 방지)
                return WatchdogFaultKind.MemoryLeak;
            }
        }

        if (FragmentationRatioNow >= FragmentationRatio)
        {
            var ratio = FragmentationRatioNow;
            var freed = Compact();
            Interlocked.Increment(ref _fragmentations);
            Record(WatchdogFaultKind.Fragmentation, "gc-heap",
                "LOH 압축 · " + (freed / 1024).ToString(CultureInfo.InvariantCulture) + "KB 회수", ratio);
            return WatchdogFaultKind.Fragmentation;
        }

        return WatchdogFaultKind.None;
    }

    private WatchdogFaultKind CheckNpu()
    {
        if (_crash is null) return WatchdogFaultKind.None;   // 관측 제외(Graceful)

        var thermal = _crash.Thermal;
        if (thermal >= ThermalState.Throttling && !_npuThrottled)
        {
            _npuThrottled = true;
            _power?.ForceProfile(thermal == ThermalState.Critical ? MobilePowerProfile.Critical : MobilePowerProfile.Saver);
            Interlocked.Increment(ref _npuOverloads);
            Record(WatchdogFaultKind.NpuOverload, "npu",
                _power is null ? "감속 권고(거버너 미주입)" : "거버너 감속 강제", _crash.ThermalRatio);
            return WatchdogFaultKind.NpuOverload;
        }

        if (thermal == ThermalState.Nominal && _npuThrottled)
        {
            _npuThrottled = false;
            _power?.ForceProfile(null);
            LastAction = "NPU 정상 복귀 · 감속 해제";
        }

        return _npuThrottled ? WatchdogFaultKind.NpuOverload : WatchdogFaultKind.None;
    }

    private long Compact()
    {
        var before = GC.GetTotalMemory(false);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        _compactCooldown = CompactCooldownTicks;
        return Math.Max(0L, before - GC.GetTotalMemory(false));
    }

    private void Record(WatchdogFaultKind kind, string target, string action, double metric)
    {
        var rec = new WatchdogRecovery(Interlocked.Increment(ref _sequence), kind, target, action, metric, DateTimeOffset.Now);
        lock (_gate)
        {
            if (_history.Count >= HistoryCap) _history.Dequeue();
            _history.Enqueue(rec);
        }
        Interlocked.Increment(ref _recoveries);
        LastAction = target + " · " + action;
        try { OnRecovered?.Invoke(rec); } catch { /* 구독자 예외 격리 */ }
    }

    private void Touch(string action)
    {
        LastAction = action;
        OnChange?.Invoke();
    }

    private static WatchdogFaultKind Max(WatchdogFaultKind a, WatchdogFaultKind b) => b > a ? b : a;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        if (_npuThrottled) _power?.ForceProfile(null);

        var n = LaneCount;
        for (var i = 0; i < n; i++)
        {
            var cts = Interlocked.Exchange(ref _lanes[i].Session, null);
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            cts?.Dispose();
        }
    }

    // 레인 슬롯(struct 배열 · 핫패스 필드는 Interlocked 로만 갱신).
    private struct Lane
    {
        public string? Name;
        public CancellationTokenSource? Session;
        public long LastBeat;
        public long Beats;
        public int Busy;
        public int Resets;
    }
}
