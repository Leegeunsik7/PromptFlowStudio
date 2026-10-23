using System.Diagnostics;
using System.Globalization;

namespace PromptFlowStudio.Services.OmniViral;

/// <summary>
/// [PromptFlow Studio v17.90.0] 👑 옴니 바이럴 그로스 마스터 콕핏 러너(HUD <c>OvgRun</c>).
///   ▸ 원클릭: <see cref="OneClickAutonomousViralOrchestratorEngine"/> 7 노드 DAG 를 백그라운드 1 개(in-flight 1)로 슬라이스 진행 · 이벤트 채널 드레인 → 최근 이벤트 링.
///   ▸ 인터락: 저점/토큰 만료/예외 → 승인 대기(<see cref="Approve"/> · <see cref="Reject"/>) — 완료 노드 산출물은 체크포인트로 보존.
///   ▸ 배포 후: 100 ms 타이머로 4 플랫폼 성장 곡선 수집 → <see cref="MultiPlatformGrowthTelemetryAggregator"/> K ≥ 1.2 → 추가 신디케이션 웨이브.
///   ▸ 연동 서브시스템(v17.78~v17.88 러너)은 DI 에 있으면 노드 출처로 표시만 하고 상태를 변경하지 않는다.
/// </summary>
public sealed class OmniViralGrowthStudioRunner : IDisposable
{
    public const int LogSize = 16, TelemetryPeriodMs = 100, MaxTelemetryTicks = 600, MinMinutes = 2, MaxMinutes = 60;
    private const long NotifyIntervalMs = 250;

    private static readonly Type[] LinkedTypes =
    {
        typeof(PromptFlowStudio.Services.LiveClipper.LiveToShortsAutoClipperStudioRunner),
        typeof(PromptFlowStudio.Services.ViralExport.ViralPredictorShortsExportStudioRunner),
        typeof(PromptFlowStudio.Services.KineticBRoll.KineticTypographyBRollStudioRunner),
        typeof(PromptFlowStudio.Services.MatrixSyndication.MatrixSyndicationStudioRunner),
        typeof(PromptFlowStudio.Services.LivePublish.ProductionLivePublisherStudioRunner),
        typeof(PromptFlowStudio.Services.VodRepurposing.VodRepurposingStudioRunner),
        typeof(PromptFlowStudio.Services.LiveArchive.LiveArchiveCloudVaultStudioRunner),
    };

    private readonly CancellationTokenSource _life = new();
    private readonly StageEvent[] _log = new StageEvent[LogSize];
    private readonly bool[] _linked = new bool[OneClickAutonomousViralOrchestratorEngine.StageCount];
    private readonly object _gate = new();
    private System.Threading.Timer? _telemetry;
    private Task? _pipeline;
    private int _busy, _logHead, _logCount, _ticks, _tickBusy;
    private long _lastNotify;
    private double _growth = 1;
    private bool _reload;
    private uint _simSeed = 0xA5A5F00Du;

    public OmniViralGrowthStudioRunner(IServiceProvider sp) : this()
    {
        for (var s = 0; s < LinkedTypes.Length; s++)
            _linked[s] = sp.GetService(LinkedTypes[s]) is not null;
    }

    public OmniViralGrowthStudioRunner()
    {
        Engine = new OneClickAutonomousViralOrchestratorEngine();
        Context.RefreshToken(NowMs());
        Context.Load(Minutes * 60, 0x0C0FFEEu, Highlight);
    }

    public event Action? OnChange;
    public OneClickAutonomousViralOrchestratorEngine Engine { get; }
    public ViralPipelineContext Context => Engine.Context;
    public PipelineSafetyInterlockCircuitBreakerGuard Guard => Engine.Guard;
    public MultiPlatformGrowthTelemetryAggregator Telemetry { get; } = new();
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public bool Tracking => _telemetry is not null;
    public bool Autopilot { get; private set; }
    public bool SimulateTokenExpiry { get; private set; }
    public bool Highlight { get; private set; } = true;
    public int Minutes { get; private set; } = 12;
    public int SliceDelayMs { get; set; } = 12;
    public long Runs { get; private set; }
    public long Failures { get; private set; }
    public long SyndicationWaves { get; private set; }
    public double LastPipelineMs { get; private set; }
    public string? LastAction { get; private set; }
    public int LinkedCount { get { var n = 0; foreach (var l in _linked) if (l) n++; return n; } }
    public bool IsLinked(int stage) => _linked[stage];
    public int EventCount => _logCount;

    /// <summary>최근 이벤트(0 = 최신).</summary>
    public StageEvent RecentEvent(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    // ══════════════════════ 설정(비핫패스) ══════════════════════

    public void SetSource(bool highlight)
    {
        Highlight = highlight;
        Reload();
        LastAction = highlight ? "🔥 소스: 하이라이트 라이브(흥분도 정점 0.95)" : "😐 소스: 평범한 토크(완만한 굴곡 — C 등급 예상)";
        Raise(true);
    }

    public void SetMinutes(int minutes)
    {
        Minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        Reload();
        LastAction = "⏱ 소스 길이 " + Minutes.ToString(CultureInfo.InvariantCulture) + "분";
        Raise(true);
    }

    /// <summary>소스 재적재 — 가동/승인 대기 중이면 컨텍스트를 건드리지 않고 다음 실행 시작 시점으로 미룬다.</summary>
    private void Reload()
    {
        if (Busy || Engine.RunState == PipelineRunState.AwaitingApproval) { _reload = true; return; }
        _reload = false;
        Context.Load(Minutes * 60, Context.Seed, Highlight);
    }

    public void ToggleTokenExpiry()
    {
        SimulateTokenExpiry = !SimulateTokenExpiry;
        LastAction = SimulateTokenExpiry ? "🧪 토큰 만료 시뮬레이션 ON — ⑤ 예약 단계에서 인터락" : "🧪 토큰 만료 시뮬레이션 OFF";
        Raise(true);
    }

    /// <summary>오토파일럿 — 켜면 즉시 원클릭 실행, 완료 시 성장 추적 · K 목표 달성 시 자동 재신디케이션.</summary>
    public void ToggleAutopilot()
    {
        Autopilot = !Autopilot;
        LastAction = Autopilot ? "🤖 오토파일럿 가동 — 7 단계 전자동 + 성장 추적" : "🤖 오토파일럿 해제";
        Raise(true);
        if (Autopilot && !Busy && Engine.RunState != PipelineRunState.AwaitingApproval) _ = RunAsync();
    }

    // ══════════════════════ 파이프라인 ══════════════════════

    /// <summary>원클릭 7 단계 실행(승인 대기 · 완료 · 중단까지) — 완료 여부.</summary>
    public async Task<bool> RunAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) { var p = _pipeline; if (p is not null) await p.ConfigureAwait(false); return false; }
        var ok = false;
        try
        {
            if (_reload && Engine.RunState != PipelineRunState.AwaitingApproval) { _reload = false; Context.Load(Minutes * 60, Context.Seed, Highlight); }
            Context.NowUnixMs = NowMs();
            if (SimulateTokenExpiry) Context.TokenExpiresUnixMs = Context.NowUnixMs - 1;
            if (!Engine.Start())
            {
                LastAction = Guard.State == GuardState.Tripped ? "⛔ 차단기 개방 — 쿨다운 후 재시도" : "⏸ 승인 대기 중 — 승인/중단을 먼저 선택";
                return false;
            }
            Runs++;
            StopTracking();
            LastAction = "🚀 원클릭 파이프라인 가동 — ① 킬러클립 절삭부터";
            Raise(true);
            ok = await Drive().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Failures++;
            LastAction = "⚠️ 파이프라인 실패 — " + ex.Message;
            PromptFlowStudio.Program.Log("OmniViral.Run", ex);
        }
        finally { Volatile.Write(ref _busy, 0); Raise(true); }
        return ok;
    }

    /// <summary>승인 후 재개 — 토큰 만료 사유면 토큰을 갱신하고 해당 노드만 재실행.</summary>
    public async Task<bool> Approve()
    {
        if (Engine.RunState != PipelineRunState.AwaitingApproval || Interlocked.Exchange(ref _busy, 1) != 0) return false;
        var ok = false;
        try
        {
            if (Guard.Reason == HaltReason.TokenExpired) { Context.RefreshToken(NowMs()); SimulateTokenExpiry = false; }
            var stage = (int)Guard.HaltedStage;
            if (!Engine.ApproveAndResume()) return false;
            LastAction = "✅ 승인 — " + OneClickAutonomousViralOrchestratorEngine.StageNames[stage] + "부터 재개(완료 단계 재렌더 없음)";
            Raise(true);
            ok = await Drive().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Failures++;
            LastAction = "⚠️ 재개 실패 — " + ex.Message;
            PromptFlowStudio.Program.Log("OmniViral.Approve", ex);
        }
        finally { Volatile.Write(ref _busy, 0); Raise(true); }
        return ok;
    }

    public void Reject()
    {
        if (Engine.RunState != PipelineRunState.AwaitingApproval) return;
        Engine.Abort();
        Drain();
        LastAction = "⛔ 사용자 중단 — 완료 단계 산출물 " + Engine.CompletedCount.ToString(CultureInfo.InvariantCulture) + "개 보존";
        Raise(true);
    }

    private async Task<bool> Drive()
    {
        var t0 = Stopwatch.GetTimestamp();
        var token = _life.Token;
        var task = Task.Run(async () =>
        {
            while (Engine.Step())
            {
                token.ThrowIfCancellationRequested();
                if (Drain()) Raise(true); else Raise(false);
                if (SliceDelayMs > 0) await Task.Delay(SliceDelayMs, token).ConfigureAwait(false);
            }
            Drain();
        }, token);
        _pipeline = task;
        await task.ConfigureAwait(false);
        LastPipelineMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        switch (Engine.RunState)
        {
            case PipelineRunState.Completed:
                LastAction = "🏁 7 단계 완료 — " + Grade() + " · 예약 " + ViralPipelineContext.Platforms.ToString(CultureInfo.InvariantCulture) + "개 · 볼트 " +
                             Context.VaultParts.ToString(CultureInfo.InvariantCulture) + " 파트 · 캐시 재사용 " + Engine.ReusedStages.ToString(CultureInfo.InvariantCulture);
                if (Autopilot) StartTracking();
                return true;
            case PipelineRunState.AwaitingApproval:
                LastAction = "⏸ 인터락 — " + PipelineSafetyInterlockCircuitBreakerGuard.ReasonText(Guard.Reason);
                return false;
            default:
                Failures++;
                LastAction = "⛔ 파이프라인 중단 — " + PipelineSafetyInterlockCircuitBreakerGuard.ReasonText(Guard.Reason) + (Engine.LastError is null ? null : " · " + Engine.LastError);
                return false;
        }
    }

    /// <summary>이벤트 채널 → 최근 이벤트 링(무할당). 상태 전이(진행 외) 포함 시 true.</summary>
    private bool Drain()
    {
        var transition = false;
        lock (_gate)
        {
            while (Engine.Events.TryRead(out var e))
            {
                if (e.State != StageState.Running || e.Percent == 0) transition = true;
                _log[_logHead] = e;
                _logHead = (_logHead + 1) % LogSize;
                if (_logCount < LogSize) _logCount++;
            }
        }
        return transition;
    }

    public string Grade() =>
        PromptFlowStudio.Services.ViralExport.AiViralPotentialScoringEngine.GradeName(Context.Grade) + " 등급 " + Context.Total.ToString("0", CultureInfo.InvariantCulture) + "점";

    // ══════════════════════ 성장 텔레메트리 ══════════════════════

    public void ToggleTracking()
    {
        if (Tracking) { StopTracking(); LastAction = "📡 성장 추적 정지"; }
        else StartTracking();
        Raise(true);
    }

    private void StartTracking()
    {
        StopTracking();
        Telemetry.Reset();
        Telemetry.ClipSeconds = Math.Max(1, Context.ClipSeconds);
        _ticks = 0;
        _growth = 1;
        LastAction = "📡 성장 추적 시작 — 100 ms 수집 · K ≥ " + MultiPlatformGrowthTelemetryAggregator.TargetK.ToString("0.0", CultureInfo.InvariantCulture) + " 목표";
        _telemetry = new System.Threading.Timer(static s => ((OmniViralGrowthStudioRunner)s!).TelemetryTick(), this, TelemetryPeriodMs, TelemetryPeriodMs);
    }

    private void StopTracking()
    {
        var t = Interlocked.Exchange(ref _telemetry, null);
        t?.Dispose();
    }

    /// <summary>100 ms 틱(무할당) — 스코어 기반 성장 곡선 합성 → 수집 → K 판정.</summary>
    public void TelemetryTick()
    {
        if (Interlocked.Exchange(ref _tickBusy, 1) != 0) return;
        try
        {
            SimulateGrowth();
            if (Telemetry.Evaluate())
            {
                SyndicationWaves++;
                _growth *= 1.15;   // 추가 신디케이션 → 신규 노출 유입
                LastAction = "🚀 K-Factor " + Telemetry.KFactor.ToString("0.00", CultureInfo.InvariantCulture) + " ≥ 1.2 — 추가 신디케이션 웨이브 #" + SyndicationWaves.ToString(CultureInfo.InvariantCulture);
                Raise(true);
            }
            else Raise(false);
            if (++_ticks >= MaxTelemetryTicks) { StopTracking(); LastAction = "📡 성장 추적 종료(60 s) — 피크 K " + Telemetry.PeakK.ToString("0.00", CultureInfo.InvariantCulture); Raise(true); }
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("OmniViral.Telemetry", ex); }
        finally { Volatile.Write(ref _tickBusy, 0); }
    }

    private void SimulateGrowth()
    {
        var c = Context;
        var q = Math.Clamp((c.Total - 40) / 60.0, 0.05, 1.0);   // 스코어 → 공유 성향
        for (var p = 0; p < MultiPlatformGrowthTelemetryAggregator.Platforms; p++)
        {
            _simSeed ^= _simSeed << 13; _simSeed ^= _simSeed >> 17; _simSeed ^= _simSeed << 5;
            var jitter = 0.9 + (_simSeed & 0xFF) / 1275.0;
            var prev = Telemetry.Views(p);
            var add = (long)((40 + prev * 0.02 * q * _growth) * jitter * (p == 0 ? 1.4 : 1.0));
            var views = prev + add;
            var shares = (long)(views * (0.002 + 0.070 * q * q) * jitter);
            var likes = (long)(views * (0.03 + 0.06 * q));
            var watch = Telemetry.ClipSeconds * (0.35 + 0.55 * q);
            Telemetry.Ingest(p, views, likes, shares, watch);
        }
    }

    // ══════════════════════ 공용 ══════════════════════

    public void Reset()
    {
        if (Busy) return;
        StopTracking();
        Engine.Reset();
        Telemetry.Reset();
        Context.RefreshToken(NowMs());
        Context.Load(Minutes * 60, Context.Seed, Highlight);
        lock (_gate) { _logHead = _logCount = 0; }
        Runs = Failures = SyndicationWaves = 0;
        LastPipelineMs = 0;
        LastAction = "♻ 초기화";
        Raise(true);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Volatile.Read(ref _lastNotify) < NotifyIntervalMs) return;
        Volatile.Write(ref _lastNotify, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("OmniViral.OnChange", ex); }
    }

    public string Summary() =>
        "단계 " + Engine.CompletedCount.ToString(CultureInfo.InvariantCulture) + "/7 · " + Grade() + " · K " + Telemetry.KFactor.ToString("0.00", CultureInfo.InvariantCulture) +
        " · 웨이브 " + SyndicationWaves.ToString(CultureInfo.InvariantCulture) + " · 실행 " + Runs.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _life.Cancel();
        StopTracking();
        try { _pipeline?.Wait(2000); } catch (AggregateException) { }
        _life.Dispose();
        OnChange = null;
    }
}
