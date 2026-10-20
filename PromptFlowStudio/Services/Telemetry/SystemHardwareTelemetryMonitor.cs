using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Telemetry;

/// <summary>v16.18.0 하드웨어 샘플 1회 — 프로세스 CPU% · 시스템 RAM(사용/총량) · GPU VRAM/사용률(nvidia-smi 없으면 null) · 프로세스 작업셋.</summary>
public readonly record struct HardwareSample(
    DateTimeOffset At, double CpuPct, long RamUsedMb, long RamTotalMb, double? VramUsedMb, double? VramTotalMb, double? GpuUtilPct, long ProcessMb)
{
    public double RamUsedRatio => RamTotalMb > 0 ? Math.Clamp(RamUsedMb / (double)RamTotalMb, 0, 1) : 0;
    public double? VramUsedRatio => VramTotalMb is > 0 && VramUsedMb is { } u ? Math.Clamp(u / VramTotalMb.Value, 0, 1) : null;
}

/// <summary>v16.18.0 메모리 압박 수준.</summary>
public enum MemoryPressure { Normal, Elevated, Critical }

/// <summary>v16.18.0 OOM 징후 신호 — 수준 · 권장 동시성 · 사유.</summary>
public readonly record struct PressureSignal(MemoryPressure Level, int SuggestedConcurrency, string Reason);

/// <summary>
/// [PromptFlowStudio] v16.18.0 🌡️ 하드웨어 텔레메트리 &amp; 렌더 효율 스캐너 — VRAM/RAM/CPU 실시간 폴링 + EWMA 평활 + 컷당 렌더 시간/ETA(순수 BCL).
///   ▸ 폴링: <see cref="Start"/>/<see cref="Stop"/> 참조 카운트(대시보드·배치가 각자 시작/정지) · PeriodicTimer · 샘플 예외 격리.
///     VRAM 은 <see cref="ResourceMonitorService"/>(nvidia-smi) 재사용, RAM 은 <see cref="GC.GetGCMemoryInfo()"/> 시스템 메모리 부하, CPU 는 프로세스 CPU 시간 델타.
///   ▸ 이력: 고정 120 슬롯 링버퍼(<see cref="CopyHistory"/>) · EWMA α=0.3(CPU · RAM · VRAM 사용률).
///   ▸ OOM 징후: 여유(RAM/VRAM 중 작은 쪽) &lt;8% 2회 연속 → Critical(동시성 1) · &lt;15% 또는 여유 &lt;25% 에서 샘플당 +5%p 급증 → Elevated(현재 한도 −1).
///     &gt;22% 로 회복해야 Normal(히스테리시스). 상승 전이마다 <see cref="OnPressure"/> 브로드캐스트 + <see cref="QueueResourceGovernor.SignalPressure"/>(60초 유지).
///   ▸ 렌더 효율: <see cref="StoryboardJobManager"/> OnChange 로 컷 상태 전이(Generating → Completed)를 관찰해 컷당 소요시간 EWMA · 최소/최대 · ETA.
/// </summary>
public sealed class SystemHardwareTelemetryMonitor : IDisposable
{
    public const int HistorySize = 120;
    public const double Alpha = 0.3;
    public const double CriticalFree = 0.08;
    public const double ElevatedFree = 0.15;
    public const double RecoverFree = 0.22;
    public const double SurgeDelta = 0.05;
    public static readonly TimeSpan PressureHold = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<HardwareSample>> _sampler;
    private readonly QueueResourceGovernor? _governor;
    private readonly StoryboardJobManager? _jobs;
    private readonly object _gate = new();
    private readonly HardwareSample[] _ring = new HardwareSample[HistorySize];
    private readonly Dictionary<string, long> _cutStarts = new(32);
    private int _ringHead;
    private int _ringCount;
    private double _cpu = double.NaN, _ram = double.NaN, _vram = double.NaN;
    private double _prevTight = double.NaN;
    private int _criticalStreak;
    private MemoryPressure _pressure;
    private PressureSignal _lastSignal;
    private long _signals;
    private double _cutAvg;
    private double _cutMin = double.MaxValue, _cutMax;
    private long _cutCount;
    private int _refs;
    private CancellationTokenSource? _cts;
    private int _disposed;

    // 프로세스 CPU 델타 상태(샘플러 단일 실행).
    private TimeSpan _lastCpuTime;
    private long _lastCpuTicks;

    public SystemHardwareTelemetryMonitor(ResourceMonitorService? monitor = null, QueueResourceGovernor? governor = null, StoryboardJobManager? jobs = null)
    {
        _governor = governor;
        _jobs = jobs;
        _sampler = ct => SampleSystemAsync(monitor, ct);
        if (_jobs is not null) _jobs.OnChange += OnJobsChanged;
    }

    /// <param name="sampler">샘플러(테스트 주입).</param>
    public SystemHardwareTelemetryMonitor(Func<CancellationToken, Task<HardwareSample>> sampler, QueueResourceGovernor? governor = null)
    {
        _sampler = sampler;
        _governor = governor;
    }

    /// <summary>샘플/압박/렌더 통계 변경(백그라운드 · HUD 는 OnLiveChanged 로 마샬링).</summary>
    public event Action? OnChange;
    /// <summary>OOM 징후 상승 전이(배치 동시성 감소 신호).</summary>
    public event Action<PressureSignal>? OnPressure;

    public bool IsRunning => Volatile.Read(ref _cts) is not null;
    public MemoryPressure Pressure { get { lock (_gate) return _pressure; } }
    public PressureSignal LastSignal { get { lock (_gate) return _lastSignal; } }
    public long PressureSignals => Interlocked.Read(ref _signals);
    public double CpuEwma { get { lock (_gate) return double.IsNaN(_cpu) ? 0 : _cpu; } }
    public double RamEwma { get { lock (_gate) return double.IsNaN(_ram) ? 0 : _ram; } }
    /// <summary>VRAM 사용률 EWMA(GPU 미탐지 시 null).</summary>
    public double? VramEwma { get { lock (_gate) return double.IsNaN(_vram) ? null : _vram; } }
    public HardwareSample? Latest { get { lock (_gate) return _ringCount == 0 ? null : _ring[(_ringHead - 1 + HistorySize) % HistorySize]; } }
    public double AverageCutSeconds { get { lock (_gate) return _cutAvg; } }
    public double MinCutSeconds { get { lock (_gate) return _cutCount == 0 ? 0 : _cutMin; } }
    public double MaxCutSeconds { get { lock (_gate) return _cutMax; } }
    public long CutsMeasured => Interlocked.Read(ref _cutCount);

    /// <summary>이력 복사(오래된 → 최신). 반환: 기록 수 · 할당 0(호출자 버퍼).</summary>
    public int CopyHistory(Span<HardwareSample> dest)
    {
        lock (_gate)
        {
            var n = Math.Min(dest.Length, _ringCount);
            var start = (_ringHead - n + HistorySize) % HistorySize;
            for (var i = 0; i < n; i++) dest[i] = _ring[(start + i) % HistorySize];
            return n;
        }
    }

    /// <summary>남은 컷 ETA — 평균 컷 시간 × 남은 컷 ÷ 동시 한도(측정 없으면 0).</summary>
    public TimeSpan EstimateRemaining(int remainingCuts)
    {
        double avg; lock (_gate) avg = _cutAvg;
        if (avg <= 0 || remainingCuts <= 0) return TimeSpan.Zero;
        var lanes = Math.Max(1, _governor?.CurrentLimit ?? 1);
        return TimeSpan.FromSeconds(Math.Round(avg * remainingCuts / lanes));
    }

    // ── 폴링 수명 ──

    /// <summary>폴링 시작(참조 카운트 +1 · 첫 호출만 루프 기동).</summary>
    public void Start(TimeSpan interval)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Increment(ref _refs) != 1) return;
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _cts, cts);
        if (prev is not null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } prev.Dispose(); }
        _ = LoopAsync(interval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : interval, cts.Token);
    }

    /// <summary>폴링 정지(참조 카운트 −1 · 0 이 되면 루프 종료).</summary>
    public void Stop()
    {
        var left = Interlocked.Decrement(ref _refs);
        if (left > 0) return;
        if (left < 0) { Interlocked.Exchange(ref _refs, 0); return; }
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            do { await SampleOnceAsync(ct).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* 정지 */ }
        catch (Exception ex) { Program.Log("HardwareTelemetry.Loop", ex); }
    }

    /// <summary>샘플 1회(예외 격리). 반환: 수집 성공 여부.</summary>
    public async Task<bool> SampleOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var s = await _sampler(ct).ConfigureAwait(false);
            Ingest(s);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("HardwareTelemetry.Sample", ex); return false; }
    }

    /// <summary>샘플 반영 → EWMA · 링버퍼 · OOM 판정(순수 상태 전이 · 테스트 대상).</summary>
    public void Ingest(in HardwareSample s)
    {
        PressureSignal? raise = null;
        lock (_gate)
        {
            _ring[_ringHead] = s;
            _ringHead = (_ringHead + 1) % HistorySize;
            if (_ringCount < HistorySize) _ringCount++;
            _cpu = Ewma(_cpu, s.CpuPct);
            _ram = Ewma(_ram, s.RamUsedRatio);
            if (s.VramUsedRatio is { } v) _vram = Ewma(_vram, v);

            // 순간값으로 판정(EWMA 는 지연이 있어 OOM 직전 급증을 놓침).
            var tight = 1 - Math.Max(s.RamUsedRatio, s.VramUsedRatio ?? 0);
            var surge = !double.IsNaN(_prevTight) && _prevTight - tight >= SurgeDelta && tight < 0.25;
            _prevTight = tight;
            _criticalStreak = tight < CriticalFree ? _criticalStreak + 1 : 0;

            var next = _pressure;
            if (_criticalStreak >= 2) next = MemoryPressure.Critical;
            else if (tight < ElevatedFree || surge) next = _pressure == MemoryPressure.Critical && tight < RecoverFree ? MemoryPressure.Critical : MemoryPressure.Elevated;
            else if (tight > RecoverFree) next = MemoryPressure.Normal;

            if (next > _pressure)
            {
                var limit = _governor?.CurrentLimit ?? 2;
                var suggested = next == MemoryPressure.Critical ? 1 : Math.Max(1, limit - 1);
                var reason = (next == MemoryPressure.Critical ? "OOM 임박" : surge ? "메모리 급증" : "메모리 여유 부족")
                    + " — 여유 " + ((int)Math.Round(tight * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
                _lastSignal = new PressureSignal(next, suggested, reason);
                raise = _lastSignal;
            }
            _pressure = next;
        }
        if (raise is { } sig)
        {
            Interlocked.Increment(ref _signals);
            try { _governor?.SignalPressure(sig.SuggestedConcurrency, PressureHold); }
            catch (Exception ex) { Program.Log("HardwareTelemetry.Governor", ex); }
            try { OnPressure?.Invoke(sig); } catch (Exception ex) { Program.Log("HardwareTelemetry.OnPressure", ex); }
        }
        Raise();
    }

    /// <summary>컷 1개 렌더 소요시간 기록(EWMA · 최소/최대).</summary>
    public void RecordCutRender(double seconds)
    {
        if (!(seconds > 0) || double.IsInfinity(seconds)) return;
        lock (_gate) RecordCutLocked(seconds);
        Raise();
    }

    // 컷 상태 전이 관찰(Generating 진입 시각 → Completed 시 소요시간). 스토리보드 컷 수 ≤ 수십 → 선형 순회.
    private void OnJobsChanged()
    {
        try
        {
            var project = _jobs?.Current;
            if (project is null) return;
            var now = Environment.TickCount64;
            var recorded = false;
            lock (_gate)
            {
                foreach (var scene in project.Scenes)
                {
                    var key = scene.Id;   // 컷 ID(8자 · 안정 참조) — 문자열 결합 0
                    switch (scene.Status)
                    {
                        case SceneStatus.Generating or SceneStatus.Downloading:
                            _cutStarts.TryAdd(key, now);
                            break;
                        case SceneStatus.Completed:
                            if (_cutStarts.Remove(key, out var t0)) { RecordCutLocked((now - t0) / 1000.0); recorded = true; }
                            break;
                        default:
                            _cutStarts.Remove(key);
                            break;
                    }
                }
                if (_cutStarts.Count > 256) _cutStarts.Clear();   // 상한(누수 방지)
            }
            if (recorded) Raise();
        }
        catch (Exception ex) { Program.Log("HardwareTelemetry.Jobs", ex); }
    }

    private void RecordCutLocked(double seconds)
    {
        if (!(seconds > 0)) return;
        _cutAvg = _cutCount == 0 ? seconds : _cutAvg * (1 - Alpha) + seconds * Alpha;
        if (seconds < _cutMin) _cutMin = seconds;
        if (seconds > _cutMax) _cutMax = seconds;
        Interlocked.Increment(ref _cutCount);
    }

    private static double Ewma(double prev, double v) => double.IsNaN(prev) ? v : prev * (1 - Alpha) + v * Alpha;

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("HardwareTelemetry.OnChange", ex); }
    }

    private async Task<HardwareSample> SampleSystemAsync(ResourceMonitorService? monitor, CancellationToken ct)
    {
        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes / (1024 * 1024);
        var used = info.MemoryLoadBytes / (1024 * 1024);
        long ws;
        try { ws = Environment.WorkingSet / (1024 * 1024); } catch { ws = 0; }

        double cpu = 0;
        try
        {
            using var p = Process.GetCurrentProcess();
            var cpuTime = p.TotalProcessorTimeCompat();
            var ticks = Environment.TickCount64;
            if (_lastCpuTicks > 0 && ticks > _lastCpuTicks)
                cpu = Math.Clamp((cpuTime - _lastCpuTime).TotalMilliseconds / (ticks - _lastCpuTicks) / Environment.ProcessorCount * 100, 0, 100);
            _lastCpuTime = cpuTime; _lastCpuTicks = ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException) { }

        double? vu = null, vt = null, gu = null;
        if (monitor is not null)
        {
            var r = await monitor.SampleAsync(ct).ConfigureAwait(false);
            vu = r.VramUsedMb; vt = r.VramTotalMb; gu = r.GpuUtilPct;
        }
        return new HardwareSample(DateTimeOffset.Now, cpu, used, total, vu, vt, gu, ws);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_jobs is not null) _jobs.OnChange -= OnJobsChanged;
        Interlocked.Exchange(ref _refs, 0);
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } cts.Dispose(); }
    }
}
