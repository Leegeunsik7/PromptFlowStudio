using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Integration;   // TelemetryAggregator · TelemetrySample

namespace PromptFlowStudio.Services.Agents;

/// <summary>자가 치유 조치 — 스케일아웃/노드 격리/스로틀/회복 대기.</summary>
public enum HealAction { ScaleOut, Isolate, Throttle, Recover }

/// <summary>시스템 건강 등급 — 정상/성능 저하/위험.</summary>
public enum HealthGrade { Healthy, Degraded, Critical }

/// <summary>
/// [PromptFlow Studio v8.3.2] 자가 진단 조치 지시(불변) — 조치·징후 범주·근거·측정치.
/// </summary>
public sealed record HealDirective(HealAction Action, string Category, string Reason, double Metric, DateTimeOffset At = default)
{
    public HealDirective Stamped() => this with { At = DateTimeOffset.Now };
}

/// <summary>
/// [PromptFlow Studio v8.3.2] AI 자가 진단 &amp; 자가 치유 오케스트레이터(관찰 가능 싱글턴 · 순수 BCL · <see cref="Channel{T}"/> · IDisposable).
///   ▸ <see cref="TelemetryAggregator"/> 링버퍼 표본을 <see cref="TelemetryAggregator.OnChange"/> 구독으로 실시간 모니터링.
///   ▸ 적응형 EWMA 기준선 대비 지연 급증(<see cref="SpikeFactor"/>)·GPU 슬롯 포화·가용 노드 소실·Webhook 실패율을 <b>사전</b> 감지.
///   ▸ 이상 징후 시 <see cref="HealDirective"/>(ScaleOut/Isolate/Throttle/Recover)를 비동기 채널로 발화 → 그리드/스웜/HUD 가 <see cref="OnHealDirective"/> 구독해 조치.
///   ▸ 범주별 쿨다운으로 동일 징후 중복 발화를 억제(멱등 · 알람 폭주 방지) · <see cref="AgentSwarmOrchestrator"/> 교정 중이면 근거에 병기.
///   ⚠ 감지/발화는 타이머(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링. 어떤 예외도 삼켜 진단이 앱을 멈추지 않는다(Graceful).
/// </summary>
public sealed class AiSelfDiagnosticEngine : IDisposable
{
    private const int LogCap = 60;
    private const double SpikeFactor = 1.8;       // 기준선 대비 이 배수 이상이면 지연 급증
    private const double LatencyFloorMs = 40;     // 기준선이 낮을 때의 급증 판정 최소 바닥값
    private const double SaturationRatio = 0.9;   // InFlight ≥ 가용노드×이 비율 → 슬롯 포화
    private const double WebhookFloor = 0.75;     // Webhook 성공률 이 값 미만 → 스로틀 권고
    private const double BaselineAlpha = 0.15;    // 기준선 EWMA 반영률(느린 추종)
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(8);

    private readonly TelemetryAggregator _telemetry;
    private readonly AgentSwarmOrchestrator? _swarm;   // 선택 결선 — 교정 상태 상관
    private readonly Channel<HealDirective> _bus = Channel.CreateUnbounded<HealDirective>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly object _gate = new();
    private readonly List<HealDirective> _log = new();
    private readonly Dictionary<string, DateTimeOffset> _lastEmit = new();

    private double _baselineLatency;
    private bool _disposed;

    public AiSelfDiagnosticEngine(TelemetryAggregator telemetry, AgentSwarmOrchestrator? swarm = null)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _swarm = swarm;
        _telemetry.OnChange += OnTelemetry;                       // 링버퍼 갱신 구독
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    public event Action? OnChange;
    /// <summary>자가 치유 지시 발화(그리드 스케일아웃·노드 격리 · HUD 자가진단 카드 소비).</summary>
    public event Action<HealDirective>? OnHealDirective;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long SamplesInspected { get; private set; }
    public long AnomaliesDetected { get; private set; }
    public long DirectivesIssued { get; private set; }
    public HealthGrade Health { get; private set; } = HealthGrade.Healthy;
    public string? LastDirective { get; private set; }
    public double BaselineLatencyMs => _baselineLatency;
    public IReadOnlyList<HealDirective> RecentDirectives { get { lock (_gate) return _log.Take(24).ToList(); } }

    // ── 모니터링(백그라운드) ──
    private void OnTelemetry()
    {
        if (_disposed) return;
        try
        {
            var s = _telemetry.Latest;
            if (s is null) return;
            SamplesInspected++;
            Inspect(s);
        }
        catch (Exception ex) { Program.Log("SelfDiagnostic.OnTelemetry", ex); }   // 감지 실패는 삼킨다(Graceful)
    }

    /// <summary>표본 1건 진단 — 지연 급증·슬롯 포화·노드 소실·Webhook 실패를 평가하고 건강 등급을 갱신한다.</summary>
    private void Inspect(TelemetrySample s)
    {
        var grade = HealthGrade.Healthy;

        // (1) 가용 GPU 노드 소실 — 인플라이트가 있는데 처리 노드가 0 → 위험(회복/스케일아웃 시급).
        if (s.GpuUsableNodes == 0 && s.GpuInFlight > 0)
        {
            grade = HealthGrade.Critical;
            Emit(HealAction.ScaleOut, "node-starvation",
                $"가용 GPU 노드 0 · 대기 렌더 {s.GpuInFlight} — 즉시 스케일아웃 필요", s.GpuInFlight);
        }
        // (2) GPU 슬롯 포화 — 인플라이트가 가용 노드 용량에 근접 → 병목 사전 스케일아웃.
        else if (s.GpuUsableNodes > 0 && s.GpuInFlight >= s.GpuUsableNodes * SaturationRatio)
        {
            grade = Worse(grade, HealthGrade.Degraded);
            Emit(HealAction.ScaleOut, "slot-saturation",
                $"GPU 슬롯 포화 — 인플라이트 {s.GpuInFlight}/{s.GpuUsableNodes} 노드 · 병목 임박", s.GpuInFlight);
        }

        // (3) EWMA 지연 급증 — 적응형 기준선 대비 급증 → 병목 노드 격리 후 재배정.
        var lat = s.AvgLatencyMs;
        if (lat > 0)
        {
            var threshold = Math.Max(LatencyFloorMs, _baselineLatency * SpikeFactor);
            if (_baselineLatency > 0 && lat > threshold)
            {
                grade = Worse(grade, HealthGrade.Degraded);
                Emit(HealAction.Isolate, "latency-spike",
                    $"EWMA 지연 {lat:0}ms > 기준 {_baselineLatency:0}ms×{SpikeFactor:0.0} — 병목 노드 격리", lat);
            }
            // 기준선은 느리게 추종(급증분에 즉시 오염되지 않도록 EWMA).
            _baselineLatency = _baselineLatency <= 0 ? lat : BaselineAlpha * lat + (1 - BaselineAlpha) * _baselineLatency;
        }

        // (4) Webhook 실패율 급증 — 성공률 하락 → 발송 스로틀 권고.
        var successRate = _telemetry.WebhookSuccessRate;
        if (successRate < WebhookFloor)
        {
            grade = Worse(grade, HealthGrade.Degraded);
            Emit(HealAction.Throttle, "webhook-degraded",
                $"Webhook 성공률 {successRate:P0} < {WebhookFloor:P0} — 발송 스로틀 권고", successRate);
        }

        if (grade != Health) { Health = grade; }
        Notify();
    }

    private static HealthGrade Worse(HealthGrade a, HealthGrade b) => (HealthGrade)Math.Max((int)a, (int)b);

    /// <summary>쿨다운 통과 시에만 지시를 채널로 발화(범주별 중복 억제 · 멱등).</summary>
    private void Emit(HealAction action, string category, string reason, double metric)
    {
        AnomaliesDetected++;
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_lastEmit.TryGetValue(category, out var last) && now - last < Cooldown) return;   // 쿨다운 내 중복 억제
            _lastEmit[category] = now;
        }
        if (_swarm?.IsRunning == true) reason += " · (스웜 교정 병행 중)";   // 교정 상태 상관
        _bus.Writer.TryWrite(new HealDirective(action, category, reason, metric).Stamped());
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var d in _bus.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                lock (_gate) { _log.Insert(0, d); if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap); }
                DirectivesIssued++;
                LastDirective = $"[{d.Action}] {d.Category} · {d.Reason}";
                try { OnHealDirective?.Invoke(d); } catch (Exception ex) { Program.Log("SelfDiagnostic.Directive", ex); }
                Notify();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("SelfDiagnostic.Pump", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _telemetry.OnChange -= OnTelemetry; } catch { /* 무시 */ }
        try { _cts.Cancel(); _bus.Writer.TryComplete(); _cts.Dispose(); } catch { /* 무시 */ }
    }
}
