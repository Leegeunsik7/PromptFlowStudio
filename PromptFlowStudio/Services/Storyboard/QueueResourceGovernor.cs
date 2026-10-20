using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.16.0 자원 스냅숏 — 시스템 RAM(가용/총량 MB) · GPU VRAM(사용/총량 MB, 미탐지 시 null).</summary>
public readonly record struct GovernorResources(long RamAvailableMb, long RamTotalMb, double? VramUsedMb, double? VramTotalMb)
{
    public double RamFreeRatio => RamTotalMb <= 0 ? 1 : Math.Clamp(RamAvailableMb / (double)RamTotalMb, 0, 1);
    public double? VramFreeRatio => VramTotalMb is > 0 && VramUsedMb is { } u ? Math.Clamp(1 - u / VramTotalMb.Value, 0, 1) : null;
}

/// <summary>v16.16.0 쿼터 판정 — 다음 시도까지 대기 · 로테이션된 키(null=로테이션 없음) · 연속 히트 수.</summary>
public readonly record struct QuotaDecision(TimeSpan Delay, string? RotatedKey, int ConsecutiveHits)
{
    public bool Rotated => RotatedKey is not null;
}

/// <summary>v16.16.0 동시 실행 슬롯(Dispose 시 반환 · 중복 Dispose 안전).</summary>
public sealed class GovernorLease : IDisposable
{
    private QueueResourceGovernor? _owner;
    internal GovernorLease(QueueResourceGovernor owner) => _owner = owner;
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}

/// <summary>
/// [PromptFlowStudio] v16.16.0 🛡️ 대량 생산 자원 거버너 — API 쿼터(429/503) 백오프 + 키 로테이션 · VRAM/RAM 기반 동적 동시성 제한(순수 BCL).
///   ▸ 쿼터: 429/402 → <see cref="WanTokenVault.MarkExhausted"/>(429 는 <see cref="WanTokenVault.RateLimitCooldown"/> 후 자동 복귀) →
///     <see cref="WanTokenVault.RotateKey"/> 성공 시 즉시 재시도(지연 0), 대체 키 없으면 지수 백오프(2→4→8…≤120초 · Retry-After 우선).
///     503 은 키 문제가 아니므로 로테이션 없이 백오프만. 성공 보고 시 연속 히트 리셋. 쿨다운 동안 신규 디스패치 보류(<see cref="WaitIfCoolingAsync"/>).
///   ▸ 동시성: <see cref="ComputeLimit"/>(VRAM 여유 &lt;10%·RAM 여유 &lt;10% → 1 · &lt;25% → 절반 · 그 외 최대) → <see cref="SetLimit"/>.
///     슬롯 대기열은 FIFO · 한도 축소 시 진행 중 작업은 끝까지 두고 신규 진입만 차단(강제 중단 0).
///   ▸ 모니터: <see cref="StartMonitoring"/> PeriodicTimer 루프(nvidia-smi 는 <see cref="ResourceMonitorService"/> 재사용) · 샘플 예외 격리 · Dispose 시 CTS 해제.
/// </summary>
public sealed class QueueResourceGovernor : IDisposable
{
    public const int DefaultMaxConcurrency = 2;
    public const int BaseBackoffSeconds = 2;
    public const int MaxBackoffSeconds = 120;

    private readonly WanTokenVault? _vault;
    private readonly Func<CancellationToken, Task<GovernorResources>> _sampler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private int _maxConcurrency = DefaultMaxConcurrency;
    private int _limit = DefaultMaxConcurrency;
    private int _active;
    private int _hits;
    private long _coolUntilTicks;
    private long _totalHits;
    private long _rotations;
    private CancellationTokenSource? _monitorCts;
    private GovernorResources _last;
    private int _pressureCap;            // v16.18.0 OOM 징후 상한(0 = 없음)
    private long _pressureUntilTicks;

    public QueueResourceGovernor(WanTokenVault? vault = null, ResourceMonitorService? monitor = null)
        : this(vault, monitor is null ? DefaultSampler : ct => SampleVia(monitor, ct), null) { }

    /// <param name="sampler">자원 샘플러(테스트 주입).</param>
    /// <param name="delay">대기 함수(테스트 주입 · 기본 Task.Delay).</param>
    public QueueResourceGovernor(WanTokenVault? vault, Func<CancellationToken, Task<GovernorResources>> sampler,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _vault = vault;
        _sampler = sampler;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>한도/쿨다운/히트 변경 방송(백그라운드 스레드 · 구독자 예외 격리).</summary>
    public event Action? OnChange;

    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);
    public int CurrentLimit => Volatile.Read(ref _limit);
    public int ActiveCount => Volatile.Read(ref _active);
    public int WaitingCount { get { lock (_gate) return _waiters.Count; } }
    public int ConsecutiveHits => Volatile.Read(ref _hits);
    public long TotalHits => Interlocked.Read(ref _totalHits);
    public long Rotations => Interlocked.Read(ref _rotations);
    public GovernorResources LastResources => _last;
    public bool IsMonitoring => Volatile.Read(ref _monitorCts) is not null;

    /// <summary>쿨다운 잔여(0 = 디스패치 가능).</summary>
    public TimeSpan CooldownRemaining
    {
        get
        {
            var ms = Interlocked.Read(ref _coolUntilTicks) - Environment.TickCount64;
            return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }

    /// <summary>VRAM/RAM 여유 → 허용 동시 실행 수(순수 함수 · 최소 1).</summary>
    public static int ComputeLimit(in GovernorResources r, int max)
    {
        var cap = Math.Max(1, max);
        var ram = r.RamFreeRatio;
        var vram = r.VramFreeRatio ?? 1.0;
        var tight = Math.Min(ram, vram);
        if (tight < 0.10) return 1;
        if (tight < 0.25) return Math.Max(1, cap / 2);
        return cap;
    }

    /// <summary>지수 백오프(초) — Retry-After 가 있으면 우선(상한 내).</summary>
    public static int BackoffSeconds(int consecutiveHits, int? retryAfterSeconds = null)
    {
        if (retryAfterSeconds is > 0) return Math.Min(MaxBackoffSeconds, retryAfterSeconds.Value);
        var n = Math.Clamp(consecutiveHits, 1, 16);
        return (int)Math.Min(MaxBackoffSeconds, (long)BaseBackoffSeconds << (n - 1));
    }

    public static bool IsQuotaStatus(int status) => status is 402 or 429;
    public static bool IsThrottleStatus(int status) => status is 429 or 503 or 402;

    /// <summary>사용자 최대 동시 실행 수(1~8) 설정 — 현재 한도도 그 이내로.</summary>
    public void SetMaxConcurrency(int max)
    {
        Volatile.Write(ref _maxConcurrency, Math.Clamp(max, 1, 8));
        var limit = ComputeLimit(_last, MaxConcurrency);   // 미샘플(총량 0)은 여유 100% 로 간주
        var cap = PressureCap;
        SetLimit(cap > 0 ? Math.Min(limit, cap) : limit);
    }

    /// <summary>동시 실행 한도 변경 — 확대 시 대기자 즉시 진입, 축소 시 신규 진입만 차단.</summary>
    public void SetLimit(int limit)
    {
        var l = Math.Clamp(limit, 1, MaxConcurrency);
        if (Interlocked.Exchange(ref _limit, l) == l) return;
        Pump();
        Raise();
    }

    /// <summary>v16.18.0 활성 OOM 압박 상한(0 = 없음 · 유지 시간 경과 시 자동 해제).</summary>
    public int PressureCap
    {
        get
        {
            var cap = Volatile.Read(ref _pressureCap);
            return cap > 0 && Environment.TickCount64 < Interlocked.Read(ref _pressureUntilTicks) ? cap : 0;
        }
    }

    /// <summary>
    /// v16.18.0 하드웨어 텔레메트리의 OOM 징후 신호 → 유지 시간 동안 동시성 상한 적용(즉시 축소 · 진행 중 작업은 유지).
    /// cap ≤ 0 이면 해제 후 다음 샘플에서 자원 기반 한도로 복귀.
    /// </summary>
    public void SignalPressure(int cap, TimeSpan hold)
    {
        if (cap <= 0)
        {
            Volatile.Write(ref _pressureCap, 0);
            Raise();
            return;
        }
        var c = Math.Clamp(cap, 1, MaxConcurrency);
        Interlocked.Exchange(ref _pressureUntilTicks, Environment.TickCount64 + (long)Math.Max(0, hold.TotalMilliseconds));
        Volatile.Write(ref _pressureCap, c);
        if (CurrentLimit > c) SetLimit(c);
        else Raise();
    }

    /// <summary>동시 실행 슬롯 획득(FIFO · 취소 가능).</summary>
    public async Task<GovernorLease> AcquireAsync(CancellationToken ct = default)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            if (_waiters.Count == 0 && _active < _limit) { _active++; return new GovernorLease(this); }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }
        using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs))
        {
            try { await tcs.Task.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // 취소와 승인이 경합하면 이미 승인된 슬롯은 반환한다.
                if (tcs.Task.Status == TaskStatus.RanToCompletion) Release();
                throw;
            }
        }
        return new GovernorLease(this);
    }

    internal void Release()
    {
        lock (_gate) { if (_active > 0) _active--; }
        Pump();
    }

    private void Pump()
    {
        lock (_gate)
        {
            while (_active < _limit && _waiters.Count > 0)
            {
                var w = _waiters.Dequeue();
                if (w.Task.IsCompleted) continue;          // 취소된 대기자 건너뜀
                _active++;
                if (!w.TrySetResult(true)) _active--;
            }
        }
    }

    /// <summary>
    /// 쿼터/쓰로틀 응답 보고 → 키 소진 표시·로테이션 · 백오프 산출. 로테이션 성공이면 Delay=0(새 키로 즉시 재시도).
    /// </summary>
    public QuotaDecision ReportThrottle(int status, string? provider = null, string? key = null, int? retryAfterSeconds = null)
    {
        var hits = Interlocked.Increment(ref _hits);
        Interlocked.Increment(ref _totalHits);
        string? rotated = null;
        if (IsQuotaStatus(status) && _vault is not null && !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(key))
        {
            _vault.MarkExhausted(provider, key, status == 402 ? null : WanTokenVault.RateLimitCooldown);
            if (_vault.RotateKey(provider, key, out var next) && !string.Equals(next, key, StringComparison.Ordinal))
            {
                rotated = next;
                Interlocked.Increment(ref _rotations);
            }
        }
        var delay = rotated is not null ? TimeSpan.Zero : TimeSpan.FromSeconds(BackoffSeconds(hits, retryAfterSeconds));
        if (delay > TimeSpan.Zero)
        {
            var until = Environment.TickCount64 + (long)delay.TotalMilliseconds;
            long cur;
            do { cur = Interlocked.Read(ref _coolUntilTicks); if (cur >= until) break; }
            while (Interlocked.CompareExchange(ref _coolUntilTicks, until, cur) != cur);
        }
        Raise();
        return new QuotaDecision(delay, rotated, hits);
    }

    /// <summary>정상 응답 — 연속 히트 리셋(쿨다운은 남은 시간만큼 유지).</summary>
    public void ReportSuccess()
    {
        if (Interlocked.Exchange(ref _hits, 0) != 0) Raise();
    }

    /// <summary>쿨다운 중이면 끝날 때까지 대기.</summary>
    public async Task WaitIfCoolingAsync(CancellationToken ct = default)
    {
        var rem = CooldownRemaining;
        if (rem > TimeSpan.Zero) await _delay(rem, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 쿼터 보호 실행 — attempt(key) 가 HTTP 상태를 반환. 2xx 면 성공, 429/503/402 면 로테이션/백오프 후 재시도(최대 maxAttempts).
    /// 반환: (최종 상태, 마지막 사용 키).
    /// </summary>
    public async Task<(int Status, string? Key)> RunWithQuotaAsync(string? provider, string? key,
        Func<string?, CancellationToken, Task<int>> attempt, int maxAttempts = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var status = 0;
        for (var i = 0; i < Math.Max(1, maxAttempts); i++)
        {
            await WaitIfCoolingAsync(ct).ConfigureAwait(false);
            status = await attempt(key, ct).ConfigureAwait(false);
            if (!IsThrottleStatus(status))
            {
                if (status is >= 200 and < 300) ReportSuccess();
                return (status, key);
            }
            var d = ReportThrottle(status, provider, key);
            if (d.RotatedKey is not null) key = d.RotatedKey;
            else await _delay(d.Delay, ct).ConfigureAwait(false);
        }
        return (status, key);
    }

    /// <summary>자원 1회 샘플 → 한도 갱신(예외 격리). 반환: 적용 한도.</summary>
    public async Task<int> SampleOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _sampler(ct).ConfigureAwait(false);
            _last = r;
            var limit = ComputeLimit(r, MaxConcurrency);
            var cap = PressureCap;
            SetLimit(cap > 0 ? Math.Min(limit, cap) : limit);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("QueueResourceGovernor.Sample", ex); }
        return CurrentLimit;
    }

    /// <summary>주기 모니터 시작(중복 호출 무시).</summary>
    public void StartMonitoring(TimeSpan interval)
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _monitorCts, cts, null) is not null) { cts.Dispose(); return; }
        _ = MonitorLoopAsync(interval < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : interval, cts.Token);
    }

    public void StopMonitoring()
    {
        var cts = Interlocked.Exchange(ref _monitorCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            do { await SampleOnceAsync(ct).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* 정지 */ }
        catch (Exception ex) { Program.Log("QueueResourceGovernor.Monitor", ex); }
    }

    private static async Task<GovernorResources> SampleVia(ResourceMonitorService monitor, CancellationToken ct)
    {
        var s = await monitor.SampleAsync(ct).ConfigureAwait(false);
        var sys = await DefaultSampler(ct).ConfigureAwait(false);
        return sys with { VramUsedMb = s.VramUsedMb, VramTotalMb = s.VramTotalMb };
    }

    // 시스템 메모리 부하(GCMemoryInfo.MemoryLoadBytes = OS 전체 사용량) 기반 가용 RAM.
    private static Task<GovernorResources> DefaultSampler(CancellationToken ct)
    {
        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes / (1024 * 1024);
        var used = info.MemoryLoadBytes / (1024 * 1024);
        return Task.FromResult(new GovernorResources(Math.Max(0, total - used), total, null, null));
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        try { h(); } catch (Exception ex) { Program.Log("QueueResourceGovernor.OnChange", ex); }
    }

    public void Dispose()
    {
        StopMonitoring();
        lock (_gate)
        {
            while (_waiters.Count > 0) _waiters.Dequeue().TrySetCanceled();
        }
    }
}
