using System;
using System.Globalization;
using System.Threading;
using PromptFlowStudio.Services.Integration;

namespace PromptFlowStudio.Services.Agents;

/// <summary>[v9.1.2] 저전력 거버너 프로파일(위→아래로 갈수록 절약 강도가 높다).</summary>
public enum MobilePowerProfile
{
    Unknown,
    Performance,
    Balanced,
    Saver,
    Critical,
}

/// <summary>
/// [v9.1.2] 거버너가 산출한 실행 계획(권고값).
///   ▸ <c>UiFps</c> 0 은 화면 꺼짐 → 렌더 정지 권고를 뜻한다.
/// </summary>
public sealed record MobilePowerPlan(
    MobilePowerProfile Profile,
    int UiFps,
    int RenderIntervalMs,
    int SwarmThreads,
    bool AllowBackgroundInference,
    bool AllowPrefetch,
    string Reason,
    DateTimeOffset At)
{
    /// <summary>HUD 한 줄 요약(InvariantCulture 고정).</summary>
    public string Summary =>
        Profile.ToString() + " · " + UiFps.ToString(CultureInfo.InvariantCulture) + "fps · 스레드 "
        + SwarmThreads.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// [PromptFlow Studio v9.1.2] 모바일 저전력 최적화 거버너.
///   ▸ 배터리 잔량 + 충전 여부 + 화면 상태를 감지해 UI 렌더 FPS 와 스웜 추론 스레드 수를 <b>권고</b>한다.
///   ▸ 제1헌장(외과적 수정) 준수: <c>AgentSwarmOrchestrator</c> 등 검증된 실행 로직을 직접 변경하지 않고,
///     구독자가 <see cref="CurrentPlan"/> 을 읽어 스스로 조절하는 관측형 계약만 제공한다.
///   ▸ 브리지 미결선·배터리 미상(-1) 은 실패가 아니라 <see cref="MobilePowerProfile.Balanced"/> 로 Graceful 수렴한다.
/// </summary>
public sealed class MobileLowPowerOptimizer : IDisposable
{
    private const int DefaultPollMs = 15_000;
    private const double SaverThreshold = 0.30d;
    private const double CriticalThreshold = 0.15d;
    private const double PerformanceThreshold = 0.60d;

    private readonly MauiMobileBridgeAdapter? _bridge;
    private readonly object _sync = new();
    // CS0104 회피: WinForms 헤드에서 System.Windows.Forms.Timer 와 충돌하므로 완전 한정명 고정.
    private readonly System.Threading.Timer _timer;

    private MobilePowerProfile? _forced;
    private int _transitionCount;
    private bool _disposed;

    public MobileLowPowerOptimizer(MauiMobileBridgeAdapter? bridge = null)
    {
        _bridge = bridge;
        CurrentPlan = BuildPlan(MobilePowerProfile.Balanced, "초기 기본값(측정 전)");
        _timer = new System.Threading.Timer(_ => Evaluate(), null, DefaultPollMs, DefaultPollMs);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>프로파일이 실제로 바뀐 순간에만 통지(동일 프로파일 재평가는 침묵).</summary>
    public event Action<MobilePowerPlan>? OnPlanChanged;

    /// <summary>현재 권고 계획.</summary>
    public MobilePowerPlan CurrentPlan { get; private set; }

    /// <summary>현재 프로파일.</summary>
    public MobilePowerProfile Profile => CurrentPlan.Profile;

    /// <summary>권고 UI 렌더 FPS(0 = 렌더 정지).</summary>
    public int RecommendedUiFps => CurrentPlan.UiFps;

    /// <summary>권고 렌더 간격(ms · FPS 0 이면 0).</summary>
    public int RecommendedRenderIntervalMs => CurrentPlan.RenderIntervalMs;

    /// <summary>권고 스웜 추론 병렬 스레드 수(1 이상).</summary>
    public int RecommendedSwarmThreads => CurrentPlan.SwarmThreads;

    /// <summary>백그라운드 추론 허용 권고.</summary>
    public bool AllowBackgroundInference => CurrentPlan.AllowBackgroundInference;

    /// <summary>프리페치(썸네일·모델 워밍업) 허용 권고.</summary>
    public bool AllowPrefetch => CurrentPlan.AllowPrefetch;

    /// <summary>마지막 판정 사유.</summary>
    public string LastReason => CurrentPlan.Reason;

    /// <summary>마지막 평가 시각.</summary>
    public DateTimeOffset? LastEvaluatedAt { get; private set; }

    /// <summary>프로파일 전환 누적 횟수.</summary>
    public int TransitionCount => Volatile.Read(ref _transitionCount);

    /// <summary>마지막으로 관측한 배터리 잔량(0~1 · 미상이면 -1).</summary>
    public double BatteryLevel { get; private set; } = -1d;

    /// <summary>배터리 퍼센트 표기(미상이면 -1).</summary>
    public int BatteryPercent => BatteryLevel < 0d ? -1 : (int)Math.Round(BatteryLevel * 100d);

    /// <summary>충전 중 여부.</summary>
    public bool IsCharging { get; private set; }

    /// <summary>화면 켜짐 여부(헤드가 <see cref="SetScreenState"/> 로 통지).</summary>
    public bool ScreenOn { get; private set; } = true;

    /// <summary>수동 프로파일 고정 여부.</summary>
    public bool IsForced => _forced.HasValue;

    /// <summary>브리지가 결선돼 실제 배터리를 읽고 있는지.</summary>
    public bool IsBridgeBound => _bridge is not null;

    /// <summary>기기 논리 코어 수(스레드 상한의 근거).</summary>
    public int CoreCount { get; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>화면 On/Off 통지(MAUI 라이프사이클 훅에서 호출). 값이 바뀔 때만 재평가.</summary>
    public void SetScreenState(bool on)
    {
        if (_disposed || ScreenOn == on) return;

        ScreenOn = on;
        Evaluate();
    }

    /// <summary>배터리 상태 직접 주입(브리지 미결선 환경·테스트용). 잔량은 0~1, 음수는 미상.</summary>
    public void ReportBattery(double level, bool charging)
    {
        if (_disposed) return;

        BatteryLevel = level < 0d ? -1d : Math.Min(1d, level);
        IsCharging = charging;
        Evaluate();
    }

    /// <summary>프로파일 수동 고정(null 이면 자동 복귀).</summary>
    public void ForceProfile(MobilePowerProfile? profile)
    {
        if (_disposed) return;

        _forced = profile == MobilePowerProfile.Unknown ? null : profile;
        Evaluate();
    }

    /// <summary>즉시 재평가(브리지에서 배터리를 갱신한 뒤 계획을 다시 산출).</summary>
    public MobilePowerPlan Evaluate()
    {
        if (_disposed) return CurrentPlan;

        RefreshFromBridge();

        MobilePowerProfile profile;
        string reason;

        if (_forced.HasValue)
        {
            profile = _forced.Value;
            reason = "수동 고정";
        }
        else if (!ScreenOn)
        {
            profile = MobilePowerProfile.Critical;
            reason = "화면 꺼짐 → 렌더 정지";
        }
        else if (BatteryLevel < 0d)
        {
            profile = MobilePowerProfile.Balanced;
            reason = IsBridgeBound ? "배터리 미상(네이티브 훅 미결선)" : "브리지 미주입";
        }
        else if (IsCharging)
        {
            profile = MobilePowerProfile.Performance;
            reason = "충전 중";
        }
        else if (BatteryLevel < CriticalThreshold)
        {
            profile = MobilePowerProfile.Critical;
            reason = "잔량 위험";
        }
        else if (BatteryLevel < SaverThreshold)
        {
            profile = MobilePowerProfile.Saver;
            reason = "잔량 부족";
        }
        else if (BatteryLevel < PerformanceThreshold)
        {
            profile = MobilePowerProfile.Balanced;
            reason = "잔량 보통";
        }
        else
        {
            profile = MobilePowerProfile.Performance;
            reason = "잔량 충분";
        }

        var plan = BuildPlan(profile, reason);
        var changed = false;

        lock (_sync)
        {
            if (CurrentPlan.Profile != plan.Profile
                || CurrentPlan.UiFps != plan.UiFps
                || CurrentPlan.SwarmThreads != plan.SwarmThreads)
            {
                changed = true;
                Interlocked.Increment(ref _transitionCount);
            }

            CurrentPlan = plan;
            LastEvaluatedAt = plan.At;
        }

        if (changed) OnPlanChanged?.Invoke(plan);
        OnChange?.Invoke();
        return plan;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Dispose();
        OnChange = null;
        OnPlanChanged = null;
    }

    // ── 내부 ──

    /// <summary>브리지에서 배터리를 읽어온다. 훅 미결선/예외는 미상(-1) 으로 흡수한다.</summary>
    private void RefreshFromBridge()
    {
        if (_bridge is null) return;

        try
        {
            _bridge.RefreshBattery();
            BatteryLevel = _bridge.BatteryLevel < 0d ? -1d : Math.Min(1d, _bridge.BatteryLevel);
            IsCharging = _bridge.BatteryCharging;
        }
        catch
        {
            BatteryLevel = -1d;   // 제5헌장: 외부 훅 실패는 즉시 폴백, 상위 전파 금지
        }
    }

    private MobilePowerPlan BuildPlan(MobilePowerProfile profile, string reason)
    {
        // 화면이 꺼져 있으면 프로파일과 무관하게 렌더 FPS 는 0(배터리 최대 보존).
        var screenOff = !ScreenOn;

        var fps = profile switch
        {
            MobilePowerProfile.Performance => 60,
            MobilePowerProfile.Balanced => 30,
            MobilePowerProfile.Saver => 15,
            MobilePowerProfile.Critical => 5,
            _ => 30,
        };

        if (screenOff) fps = 0;

        var threads = profile switch
        {
            MobilePowerProfile.Performance => Math.Max(2, Math.Min(4, CoreCount)),
            MobilePowerProfile.Balanced => Math.Max(1, Math.Min(3, CoreCount)),
            MobilePowerProfile.Saver => Math.Max(1, Math.Min(2, CoreCount)),
            MobilePowerProfile.Critical => 1,
            _ => Math.Max(1, Math.Min(2, CoreCount)),
        };

        var bg = profile is MobilePowerProfile.Performance or MobilePowerProfile.Balanced;
        var prefetch = profile == MobilePowerProfile.Performance && !screenOff;

        return new MobilePowerPlan(
            profile,
            fps,
            fps <= 0 ? 0 : (int)Math.Round(1000d / fps),
            threads,
            bg,
            prefetch,
            reason,
            DateTimeOffset.UtcNow);
    }
}
