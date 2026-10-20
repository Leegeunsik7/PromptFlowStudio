using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PromptFlowStudio.Services.Operations;

/// <summary>카나리 프로브 1건의 개별 판정 — 이름/정상여부/사람이 읽을 상세.</summary>
public sealed record CanarySignal(string Name, bool Ok, string Detail);

/// <summary>카나리 종합 판정 — 전체 상태(정상/저하/위험) + 개별 신호 목록(불변 스냅샷).</summary>
public sealed record CanaryReport(CanaryStatus Status, DateTimeOffset At, IReadOnlyList<CanarySignal> Signals);

/// <summary>카나리 종합 상태 — 전 신호 정상/신호 1건 이상 저하/신호 2건 이상 위험.</summary>
public enum CanaryStatus { Healthy, Degraded, Unhealthy }

/// <summary>
/// [PromptFlow Studio v10.7.1-LTS-OpsCanary] 운영 카나리 헬스 체크 — v10.7.0 LTS 실운영 3대 핵심 서브시스템을
///   10초 주기로 자동 프로빙해 종합 상태(<see cref="CanaryReport"/>)를 HUD/감사용으로 노출한다.
///   ▸ <see cref="Services.Security.SecureSecretVault"/>: 전용 카나리 키로 실제 Resolve 왕복(DPAPI 봉인/복호화
///     경로 실사용)을 수행해 <c>InsecureFallbacks</c> 증가 여부로 DPAPI 봉인이 여전히 정상 작동하는지 판정.
///   ▸ <see cref="Services.Telemetry.OtlpTelemetryVisualizerProvider"/>: 최근 50건 윈도우 실패율이 10%
///     초과이거나 서킷브레이커가 Open 이면 저하로 판정(기존 스냅샷 그대로 재사용 — 별도 계산 없음).
///   ▸ <see cref="Services.AI.Wan.LivePipelineAutoTuner"/>: EWMA 가 목표(120ms)×1.15 를 지속 초과했는데도
///     다운그레이드 카운터가 뒤따라 증가하지 않는 "정체"를 연속 3틱(30초) 감지 시 저하로 판정 — 실제 다운그레이드
///     발생 자체가 아니라 <b>피드백 루프가 살아있는지</b>를 검증(튜너 내부 히스테리시스 로직은 그대로 신뢰).
///   ▸ 순수 BCL · 관찰 가능 싱글턴(OnChange) · System.Threading.Timer 는 Dispose 시 정리(규칙3).
/// </summary>
public sealed class OperationsCanaryHealthChecker : IDisposable
{
    public const int ProbeIntervalMs = 10_000;
    private const string CanaryKeyName = Security.SecureSecretVault.CanaryKeyName;
    private const double OtlpFailureRateAlertPct = 10.0;
    private const double TunerStuckOverBudgetRatio = 1.15;   // LivePipelineAutoTuner 의 DowngradeRatio 와 동일 기준(공개 상수 없어 재정의 · 근거: 동일 초과 판정선 사용)
    private const int TunerStuckConsecutiveTicks = 3;         // 3틱(30초) 연속 초과+다운그레이드無 → Stuck

    private readonly Security.SecureSecretVault _vault;
    private readonly Telemetry.OtlpTelemetryVisualizerProvider _otlp;
    private readonly AI.Wan.LivePipelineAutoTuner _tuner;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();

    private CanaryReport _last;
    private long _tunerDowngradesSeenAtLastProbe = -1;
    private int _tunerStuckStreak;
    private long _probes, _alerts;
    private bool _disposed;
    private int _inProbe;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public long Probes => Interlocked.Read(ref _probes);
    public long Alerts => Interlocked.Read(ref _alerts);
    public CanaryReport Last { get { lock (_gate) return _last; } }

    public OperationsCanaryHealthChecker(Security.SecureSecretVault vault, Telemetry.OtlpTelemetryVisualizerProvider otlp, AI.Wan.LivePipelineAutoTuner tuner)
    {
        _vault = vault;
        _otlp = otlp;
        _tuner = tuner;
        _last = new CanaryReport(CanaryStatus.Healthy, DateTimeOffset.UtcNow, Array.Empty<CanarySignal>());
        _timer = new System.Threading.Timer(_ => OnTimer(), null, ProbeIntervalMs, ProbeIntervalMs);
    }

    // v14.0.1: 타이머 스레드 예외는 프로세스 종료 경로 - 격리 후 로그. 느린 DPAPI 로 틱이 겹치면 건너뜀(스트릭 필드 경합 방지).
    private void OnTimer()
    {
        if (Volatile.Read(ref _disposed) || Interlocked.CompareExchange(ref _inProbe, 1, 0) != 0) return;
        try { RunProbe(); }
        catch (Exception ex) { Program.Log("OperationsCanary.Probe", ex); }
        finally { Volatile.Write(ref _inProbe, 0); }
    }

    /// <summary>3대 서브시스템을 즉시 1회 프로빙(타이머 틱과 동일 로직 — 수동 호출/테스트 겸용).</summary>
    public CanaryReport RunProbe()
    {
        var signals = new List<CanarySignal>(3)
        {
            Guard("SecureSecretVault", ProbeVault),
            Guard("OtlpExportPipeline", ProbeOtlp),
            Guard("LivePipelineAutoTuner", ProbeTuner),
        };

        var badCount = signals.Count(s => !s.Ok);
        var status = badCount == 0 ? CanaryStatus.Healthy : badCount == 1 ? CanaryStatus.Degraded : CanaryStatus.Unhealthy;
        var report = new CanaryReport(status, DateTimeOffset.UtcNow, signals);

        lock (_gate) _last = report;
        Interlocked.Increment(ref _probes);
        if (status != CanaryStatus.Healthy) Interlocked.Increment(ref _alerts);
        LastAction = status == CanaryStatus.Healthy
            ? $"프로브 #{Probes} · Healthy"
            : $"프로브 #{Probes} · {status} · " + string.Join(", ", signals.Where(s => !s.Ok).Select(s => s.Name));
        RaiseChange();
        return report;
    }

    private CanarySignal ProbeVault()
    {
        var fallbacksBefore = _vault.InsecureFallbacks;
        try
        {
            var probeKey = _vault.ResolveOrCreate(CanaryKeyName);
            var ok = probeKey.Length > 0 && _vault.InsecureFallbacks == fallbacksBefore;
            Security.SecureSecretVault.Wipe(probeKey);   // 카나리 키도 사용 즉시 세척(규칙6)
            return new CanarySignal("SecureSecretVault", ok, ok ? "DPAPI 왕복 정상" : "⚠ 비보안 폴백으로 강등됨(DPAPI 불가)");
        }
        catch (Exception ex)
        {
            return new CanarySignal("SecureSecretVault", false, "예외: " + ex.GetType().Name);
        }
    }

    private CanarySignal ProbeOtlp()
    {
        var snap = _otlp.Snapshot;
        var failRatePct = 100.0 - snap.SuccessRatePct;
        var ok = failRatePct <= OtlpFailureRateAlertPct && snap.CurrentState != Telemetry.ExportCircuitState.Open;
        return new CanarySignal("OtlpExportPipeline", ok, $"실패율 {failRatePct:0.#}% · 서킷 {snap.CurrentState}(윈도우 {snap.WindowSize}건)");
    }

    private CanarySignal ProbeTuner()
    {
        var snap = _tuner.Snapshot;
        var overBudget = snap.EwmaMs > snap.TargetMs * TunerStuckOverBudgetRatio;
        _tunerStuckStreak = overBudget && _tunerDowngradesSeenAtLastProbe == snap.Downgrades ? _tunerStuckStreak + 1 : 0;
        _tunerDowngradesSeenAtLastProbe = snap.Downgrades;

        var ok = _tunerStuckStreak < TunerStuckConsecutiveTicks;
        var detail = $"EWMA {snap.EwmaMs:0.#}ms / 목표 {snap.TargetMs:0}ms · 다운그레이드 {snap.Downgrades}회";
        if (overBudget) detail += $" · 초과지속 {_tunerStuckStreak}틱";
        return new CanarySignal("LivePipelineAutoTuner", ok, detail);
    }

    // 프로브 1건의 예외가 나머지 신호·보고를 끊지 않게 - 실패 신호로 강등
    private static CanarySignal Guard(string name, Func<CanarySignal> probe)
    {
        try { return probe(); }
        catch (Exception ex) { return new CanarySignal(name, false, "예외: " + ex.GetType().Name); }
    }

    // 구독자(HUD 등) 예외 격리 - 한 구독자가 던져도 나머지 구독자 통지·타이머 지속
    private void RaiseChange()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { Program.Log("OperationsCanary.OnChange", ex); }
        }
    }

    public void ResetAll()
    {
        lock (_gate) _last = new CanaryReport(CanaryStatus.Healthy, DateTimeOffset.UtcNow, Array.Empty<CanarySignal>());
        _tunerStuckStreak = 0;
        _tunerDowngradesSeenAtLastProbe = -1;
        LastAction = "카나리 초기화";
        RaiseChange();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}