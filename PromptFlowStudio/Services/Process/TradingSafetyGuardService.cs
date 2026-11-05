using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.ProcessBridge;

/// <summary>
/// 🛡️ 스마트 마켓아워 트레이딩 세이프가드 — 한국 증시 정규장(평일 09:00~15:30 KST · UTC+9 고정, DST 없음 · KRX 휴장일 자동 해제) 또는 사용자 정의 장중 구간 판별.
///   ▸ 안전 모드 ON + 장중 → FFmpeg 인코더 프로세스 <see cref="ProcessPriorityClass.BelowNormal"/> 강제 · 동시 렌더/인코딩 1개 게이트.
///   ▸ <see cref="Attach"/> 시 <see cref="ProcessRunner"/> 전역 훅(우선순위·게이트) 결선 — <see cref="Dispose"/> 에서 해제.
///   ▸ 게이트는 AsyncLocal 재진입 허용(렌더 작업 내부 ffmpeg 호출이 자기 슬롯에 막히는 교착 차단).
/// </summary>
public sealed partial class TradingSafetyGuardService : IDisposable
{
    public static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    public static readonly TimeSpan DefaultOpen = new(9, 0, 0);
    public static readonly TimeSpan DefaultClose = new(15, 30, 0);
    public const ProcessPriorityClass ThrottledPriority = ProcessPriorityClass.BelowNormal;
    public const int ThrottledMaxConcurrency = 1;
    public const string IndicatorText = "🛡️ 장중 안전 모드 가동 중 (GPU 부하 제한)";

    private readonly Func<bool> _enabled;
    private readonly SemaphoreSlim _slot = new(ThrottledMaxConcurrency, ThrottledMaxConcurrency);
    private readonly AsyncLocal<bool> _held = new();
    private Func<string, ProcessPriorityClass?>? _resolver;
    private Func<string, Func<CancellationToken, Task<ProcessResult>>, CancellationToken, Task<ProcessResult>>? _gate;
    private int _attached, _disposed;

    /// <param name="enabledProvider">안전 모드 토글 공급자(null = 항상 ON · 앱 DI 는 환경설정).</param>
    public TradingSafetyGuardService(Func<bool>? enabledProvider = null) => _enabled = enabledProvider ?? (static () => true);

    /// <summary>현재 시각 공급자(테스트 주입).</summary>
    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;
    /// <summary>장 시작(KST 기준 시각).</summary>
    public TimeSpan MarketOpen { get; set; } = DefaultOpen;
    /// <summary>장 마감(KST 기준 시각).</summary>
    public TimeSpan MarketClose { get; set; } = DefaultClose;
    /// <summary>사용자 정의 장중 구간 시 주말 포함 여부(기본 false = 평일만).</summary>
    public bool IncludeWeekends { get; set; }
    /// <summary>📅 KRX 휴장일(공휴일·대체공휴일·근로자의 날·연말 폐장일) 자동 해제 여부(기본 true).</summary>
    public bool RespectKrxHolidays { get; set; } = true;

    public bool IsEnabled
    {
        get
        {
            try { return _enabled(); }
            catch (Exception ex) { Program.Log("TradingGuard.Enabled", ex); return true; }   // 판정 실패 = 안전 측
        }
    }

    /// <summary>순수 판정 — <paramref name="at"/> 이 KST 기준 장중 구간인지.</summary>
    public static bool IsMarketHours(DateTimeOffset at, TimeSpan open, TimeSpan close, bool includeWeekends = false)
    {
        var kst = at.ToOffset(Kst);
        if (!includeWeekends && kst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = kst.TimeOfDay;
        return open <= close ? t >= open && t < close : t >= open || t < close;   // 자정 넘김 구간 지원
    }

    /// <summary>KRX 정규장 판정 — 평일 09:00~15:30(KST) 이면서 휴장일 아님.</summary>
    public static bool IsMarketHours(DateTimeOffset at) => ResolveSession(at).Status == KrxSessionStatus.RegularMarket;   // 📅 krx_closures.json 세션 오버라이드(수능 10:00 개장 등) 반영

    /// <summary>📅 KST 기준 평일 휴장일(<see cref="KrxHolidayCalendar"/>) 여부.</summary>
    public static bool IsKrxHoliday(DateTimeOffset at) => KrxHolidayCalendar.IsHoliday(DateOnly.FromDateTime(at.ToOffset(Kst).DateTime));

    /// <summary>현재 KST 기준 휴장일이라 안전 모드가 자동 해제된 상태인지.</summary>
    public bool IsHolidayNow => RespectKrxHolidays && IsKrxHoliday(UtcNow());

    public bool IsMarketHoursNow
    {
        get
        {
            var now = UtcNow();
            if (RespectKrxHolidays && !IncludeWeekends && MarketOpen == DefaultOpen && MarketClose == DefaultClose)
                return ResolveSession(now).Status == KrxSessionStatus.RegularMarket;   // 기본 정규장 = 4단계 세션 머신(일자별 오버라이드 포함)
            return IsMarketHours(now, MarketOpen, MarketClose, IncludeWeekends) && !(RespectKrxHolidays && IsKrxHoliday(now));
        }
    }

    /// <summary>안전 모드 ON + 장중 = 부하 제한 활성.</summary>
    public bool IsThrottling => IsEnabled && IsMarketHoursNow;

    /// <summary>현재 허용 동시 인코딩 수(제한 중 1 · 아니면 무제한 = <see cref="int.MaxValue"/>).</summary>
    public int MaxConcurrentEncodes => IsThrottling ? ThrottledMaxConcurrency : int.MaxValue;

    /// <summary>인코더 실행 파일(ffmpeg 계열) 판정.</summary>
    public static bool IsEncoderExecutable(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var name = Path.GetFileNameWithoutExtension(exe.Trim().Trim('"'));
        return name.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>인코더 프로세스 우선순위 결정 — 제한 중이면 BelowNormal, 아니면 null(변경 없음).</summary>
    public ProcessPriorityClass? ResolvePriority(string? exe)
        => IsEncoderExecutable(exe) && IsThrottling ? ThrottledPriority : null;

    /// <summary>실행 중 프로세스에 우선순위 적용(예외 격리) — 적용 시 true.</summary>
    public static bool TryApplyPriority(Process proc, ProcessPriorityClass? priority)
    {
        if (priority is null) return false;
        try
        {
            if (proc.HasExited) return false;
            proc.PriorityClass = priority.Value;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or NotSupportedException)
        {
            Program.Log("TradingGuard.Priority", ex);
            return false;
        }
    }

    /// <summary>
    /// 동시 인코딩 게이트 — 제한 중이면 슬롯 1개를 점유한 채 <paramref name="work"/> 실행(재진입 허용), 아니면 즉시 실행.
    /// </summary>
    public async Task<T> RunGatedAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        if (_held.Value || !IsThrottling || Volatile.Read(ref _disposed) == 1) return await work(ct).ConfigureAwait(false);
        await _slot.WaitAsync(ct).ConfigureAwait(false);
        _held.Value = true;
        try { return await work(ct).ConfigureAwait(false); }
        finally
        {
            _held.Value = false;
            try { _slot.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>현재 점유 중 슬롯 수(진단).</summary>
    public int ActiveSlots => ThrottledMaxConcurrency - _slot.CurrentCount;

    /// <summary><see cref="ProcessRunner"/> 전역 훅 결선(멱등).</summary>
    public void Attach()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _attached, 1) == 1) return;
        _resolver = ResolvePriority;
        _gate = GateAsync;
        ProcessRunner.PriorityResolver = _resolver;
        ProcessRunner.EncoderGate = _gate;
    }

    private Task<ProcessResult> GateAsync(string exe, Func<CancellationToken, Task<ProcessResult>> work, CancellationToken ct)
        => IsEncoderExecutable(exe) ? RunGatedAsync(work, ct) : work(ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (Interlocked.Exchange(ref _attached, 0) == 1)
        {
            if (ReferenceEquals(ProcessRunner.PriorityResolver, _resolver)) ProcessRunner.PriorityResolver = null;
            if (ReferenceEquals(ProcessRunner.EncoderGate, _gate)) ProcessRunner.EncoderGate = null;
        }
        _slot.Dispose();
    }
}
