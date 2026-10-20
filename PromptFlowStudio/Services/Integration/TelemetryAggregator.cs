using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Wan;   // DistributedRenderGrid

namespace PromptFlowStudio.Services.Integration;

/// <summary>1초 롤링 텔레메트리 표본(불변) — RPS·EWMA 지연·Webhook 누적·GPU 슬롯.</summary>
public sealed record TelemetrySample(
    long UnixSec, int Rps, double AvgLatencyMs,
    long WebhookDelivered, long WebhookFailed, int WebhookQueued,
    int GpuInFlight, int GpuUsableNodes);

/// <summary>
/// [PromptFlow Studio v6.2.0] 경량 무의존 텔레메트리 집계기(관찰 가능 싱글턴 · 순수 BCL Ring Buffer · 외부 APM 0 · IDisposable).
///   ▸ <see cref="System.Threading.Timer"/> 로 1초마다 게이트웨이 RPS(총요청 델타)·GPU 슬롯(InFlight/가용노드)·Webhook 성공/실패·WebRTC 지연을 표본화.
///   ▸ 최근 60초를 고정 크기 링 버퍼에 롤링 보관(할당 0회/틱) → HUD 스파크라인·게이지가 폴링 없이 <see cref="OnChange"/> 로 실시간 갱신.
///   ▸ Prometheus/Datadog 등 외부 의존 없음(제약사항 2). 어떤 예외도 삼켜 집계가 앱을 멈추지 않는다(Graceful).
///   ⚠ <see cref="OnChange"/> 는 타이머(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class TelemetryAggregator : IDisposable
{
    public const int Window = 60;   // 최근 60초

    private readonly TelemetrySample[] _ring = new TelemetrySample[Window];
    private int _count;
    private int _head;
    private readonly object _gate = new();

    private readonly ApiGatewayEngine _gateway;
    private readonly DistributedRenderGrid _grid;
    private readonly WebhookDispatcher _webhook;
    private readonly WebRtcStreamer _rtc;
    private readonly System.Threading.Timer _timer;

    private long _lastTotalRequests;
    private double _ewmaLatency;

    public TelemetryAggregator(ApiGatewayEngine gateway, DistributedRenderGrid grid, WebhookDispatcher webhook, WebRtcStreamer rtc)
    {
        _gateway = gateway;
        _grid = grid;
        _webhook = webhook;
        _rtc = rtc;
        _lastTotalRequests = gateway.TotalRequests;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>표본 추가 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;

    private void Tick()
    {
        try
        {
            var total = _gateway.TotalRequests;
            var rps = (int)Math.Max(0, total - _lastTotalRequests);
            _lastTotalRequests = total;

            var inflight = _grid.TotalInFlight;   // v8.1.1: Nodes ToList/LINQ 제거(1초 핫패스 무할당)
            var usable = _grid.UsableNodeCount;

            var lat = _rtc.IsLive ? _rtc.LatencyMs : 0;
            _ewmaLatency = _ewmaLatency <= 0 ? lat : 0.3 * lat + 0.7 * _ewmaLatency;   // 지수 이동 평균

            var s = new TelemetrySample(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), rps, _ewmaLatency,
                _webhook.TotalDelivered, _webhook.TotalFailed, _webhook.QueuedCount,
                inflight, usable);

            lock (_gate)
            {
                _ring[_head] = s;
                _head = (_head + 1) % Window;
                if (_count < Window) _count++;
            }
            OnChange?.Invoke();
        }
        catch (Exception ex) { Program.Log("Telemetry.Tick", ex); }   // 집계 실패는 삼킨다(Graceful)
    }

    /// <summary>최근 표본을 오래된→최신 순으로 반환(스레드 안전 복사본).</summary>
    public IReadOnlyList<TelemetrySample> Samples
    {
        get
        {
            lock (_gate)
            {
                var list = new List<TelemetrySample>(_count);
                for (var i = 0; i < _count; i++)
                {
                    var idx = (_head - _count + i + Window * 2) % Window;
                    list.Add(_ring[idx]);
                }
                return list;
            }
        }
    }

    public TelemetrySample? Latest { get { lock (_gate) return _count == 0 ? null : _ring[(_head - 1 + Window) % Window]; } }

    public int CurrentRps => Latest?.Rps ?? 0;
    public int PeakRps { get { var s = Samples; return s.Count == 0 ? 0 : s.Max(x => x.Rps); } }
    public double AvgLatencyMs => Latest?.AvgLatencyMs ?? 0;
    public int GpuInFlight => Latest?.GpuInFlight ?? 0;
    public int GpuUsableNodes => Latest?.GpuUsableNodes ?? 0;

    /// <summary>Webhook 성공률(0~1) — 표본 없으면 1.</summary>
    public double WebhookSuccessRate
    {
        get
        {
            var l = Latest;
            if (l is null) return 1;
            var t = l.WebhookDelivered + l.WebhookFailed;
            return t == 0 ? 1 : (double)l.WebhookDelivered / t;
        }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { /* 무시 */ }
    }
}
