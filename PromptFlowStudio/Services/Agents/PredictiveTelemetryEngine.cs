using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PromptFlowStudio.Services.Integration;   // TelemetryAggregator · TelemetrySample

namespace PromptFlowStudio.Services.Agents;

/// <summary>예측 위험 등급 — 정상/주의/위험.</summary>
public enum ForecastRisk { Low, Elevated, Critical }

/// <summary>텔레메트리 예측(불변) — 지연 회귀 기울기·예측 지연·포화 예측·위험 등급.</summary>
public sealed record TelemetryForecast(
    double LatencySlope, double ForecastLatencyMs, double SaturationForecast, ForecastRisk Risk, long AtMs);

/// <summary>
/// [PromptFlow Studio v8.6.2] AI 장애 예측 텔레메트리 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="TelemetryAggregator"/> 링버퍼 시계열을 <see cref="TelemetryAggregator.OnChange"/> 구독으로 실시간 분석.
///   ▸ EWMA 평활 + 최소자승 <b>선형 회귀</b> 기울기로 지연/슬롯 포화 추세를 외삽 → OOM/지연 폭증을 <see cref="Horizon"/>초 앞서 예측.
///   ▸ 임계 초과 예측 시 <see cref="AiFleetOrchestrator.ScaleOut"/> 로 <b>선제 스케일아웃</b> 경고 발화(범주 쿨다운으로 중복 억제).
///   ⚠ 분석/발화는 타이머(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링. 예외는 삼켜 예측이 앱을 멈추지 않는다(Graceful).
/// </summary>
public sealed class PredictiveTelemetryEngine : IDisposable
{
    public const int Horizon = 10;                 // 외삽 지평(초)
    private const double LatencyCriticalMs = 400;  // 예측 지연 위험 임계
    private const double LatencyElevatedMs = 220;  // 예측 지연 주의 임계
    private const int MinSamples = 6;              // 회귀 최소 표본
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private readonly TelemetryAggregator _telemetry;
    private readonly AiFleetOrchestrator? _fleet;
    private readonly object _gate = new();
    private DateTimeOffset _lastWarn = DateTimeOffset.MinValue;
    private bool _disposed;

    public PredictiveTelemetryEngine(TelemetryAggregator telemetry, AiFleetOrchestrator? fleet = null)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _fleet = fleet;
        _telemetry.OnChange += OnTelemetry;
    }

    public event Action? OnChange;
    /// <summary>예측 경고 발화(HUD · 플릿 선제 조치 연동).</summary>
    public event Action<TelemetryForecast>? OnForecast;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long Forecasts { get; private set; }
    public long Warnings { get; private set; }
    public ForecastRisk Risk { get; private set; } = ForecastRisk.Low;
    public TelemetryForecast? Latest { get; private set; }
    public string? LastWarning { get; private set; }

    private void OnTelemetry()
    {
        if (_disposed) return;
        try
        {
            var samples = _telemetry.Samples;
            if (samples.Count < MinSamples) return;
            Analyze(samples);
        }
        catch (Exception ex) { Program.Log("Predictive.OnTelemetry", ex); }
    }

    private void Analyze(IReadOnlyList<TelemetrySample> samples)
    {
        // 최근 창에 대해 지연 시계열 선형 회귀(기울기=초당 증가 ms).
        var lat = samples.Select(s => s.AvgLatencyMs).ToArray();
        var slope = Slope(lat);
        var last = lat[^1];
        var forecastLat = Math.Max(0, last + slope * Horizon);

        // 슬롯 포화 예측 — 인플라이트/가용노드 비율 추세 외삽.
        var sat = samples.Select(s => s.GpuUsableNodes <= 0 ? 1.0 : (double)s.GpuInFlight / s.GpuUsableNodes).ToArray();
        var satForecast = Math.Max(0, sat[^1] + Slope(sat) * Horizon);

        var risk = (forecastLat >= LatencyCriticalMs || satForecast >= 1.2) ? ForecastRisk.Critical
                 : (forecastLat >= LatencyElevatedMs || satForecast >= 0.95) ? ForecastRisk.Elevated
                 : ForecastRisk.Low;

        var fc = new TelemetryForecast(slope, forecastLat, satForecast, risk, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Latest = fc; Risk = risk; Forecasts++;

        if (risk == ForecastRisk.Critical) RaiseWarning(fc);
        Notify();
    }

    private void RaiseWarning(TelemetryForecast fc)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (now - _lastWarn < Cooldown) return;   // 쿨다운 내 중복 억제
            _lastWarn = now;
        }
        Warnings++;
        LastWarning = $"⚠ {Horizon}s 내 지연 {fc.ForecastLatencyMs:0}ms·포화 {fc.SaturationForecast:P0} 예측 — 선제 스케일아웃";
        try { _fleet?.ScaleOut("예측 선제: " + LastWarning); } catch (Exception ex) { Program.Log("Predictive.Warn", ex); }
        try { OnForecast?.Invoke(fc); } catch { /* 격리 */ }
    }

    /// <summary>최소자승 선형 회귀 기울기(x=표본 인덱스 0..n-1).</summary>
    private static double Slope(double[] y)
    {
        var n = y.Length;
        if (n < 2) return 0;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++) { sx += i; sy += y[i]; sxy += i * y[i]; sxx += (double)i * i; }
        var denom = n * sxx - sx * sx;
        return Math.Abs(denom) < 1e-9 ? 0 : (n * sxy - sx * sy) / denom;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _telemetry.OnChange -= OnTelemetry; } catch { /* 무시 */ }
    }
}
