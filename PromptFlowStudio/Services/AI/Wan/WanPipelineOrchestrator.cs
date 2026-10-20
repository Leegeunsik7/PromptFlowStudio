using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>파이프라인 서킷 상태 — Closed(정상)→Open(연속 실패 차단)→HalfOpen(최소 스펙 프로브)→Closed 복귀.
///   ⚠ <see cref="Orchestration.BreakerState"/>(v10.0 헬스 오케스트레이터)와 별개 열거형 — CS0104 회피 접두.</summary>
public enum WanCircuitState { Closed, Open, HalfOpen }

/// <summary>큐 진입 요청 — 순번/스펙.</summary>
public sealed record WanJobRequest(string Id, WanJobSpec Spec);

/// <summary>진행률 스트리밍 1건 — 스테이지/퍼센트/완료 여부/결과.</summary>
public sealed record WanJobProgress(string JobId, string Stage, int PercentComplete, bool Completed, bool Ok, string Message);

/// <summary>파이프라인 집계 리포트.</summary>
public sealed record WanPipelineReport(int QueueDepth, bool Processing, WanCircuitState Circuit, long Enqueued, long Completed, long Failed, long CircuitTrips, long DownscaleSteps);

/// <summary>
/// v10.1.3 Wan 파이프라인 오케스트레이터 — 저사양 단일 GPU 전제(동시 다중 작업 = VRAM 경합 유발) 하 <b>단일 워커
/// 순차 큐</b>(<see cref="Channel{T}"/>, 포화 시 최오래된 항목 폐기)로 생성 작업을 직렬화하고, 각 작업을
/// <see cref="AdaptiveHardwareProfiler.AutoDownscaleAsync"/> → <see cref="AlibabaWanLowSpecEngine"/> 타일 단위
/// 순차 실행으로 처리하며 타일마다 진행률을 스트리밍한다. 연속 실패(다운스케일 바닥에도 OOM)가 임계를 넘으면
/// 서킷을 Open 해 신규 작업을 즉시 실패 처리(무의미한 GPU 재시도 차단), 쿨다운 경과 후 최소 스펙 프로브로
/// HalfOpen 자율 재개. v10.2.2: 타일마다 실측 소요시간을 <see cref="LivePipelineAutoTuner"/> 에 통보해 반환된
/// 양자화를 다음 타일에 즉시 적용(작업 실행 도중 실시간 보정 — 사전 계획 단계 다운스케일과 별개 축).
/// 순수 BCL(<c>System.Threading.Channels</c>) · 백그라운드 소비 루프는 생성 시 1회 기동.
///   ⚠ <see cref="AdaptiveHardwareProfiler"/> 의존(Process 기반 VRAM 탐지) 때문에 <b>데스크톱 전용</b> — Mobile 미등록.
/// </summary>
public sealed class WanPipelineOrchestrator : IDisposable
{
    private const int QueueCapacity = 32;
    private const int FailureThreshold = 3;
    private static readonly TimeSpan CooldownWindow = TimeSpan.FromSeconds(10);
    private const int MaxRecent = 24;

    private readonly AlibabaWanLowSpecEngine _engine;
    private readonly AdaptiveHardwareProfiler _profiler;
    private readonly LivePipelineAutoTuner _tuner;
    private readonly Channel<WanJobRequest> _queue;
    private readonly object _gate = new();
    private readonly Queue<WanJobProgress> _recent = new();
    private readonly Random _rng = new(10101);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private System.Threading.Timer? _demoTimer;
    private WanCircuitState _circuit = WanCircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedUntil;
    private bool _processing;
    private long _enqueued, _completed, _failed, _circuitTrips;
    private bool _disposed;

    public event Action? OnChange;

    public WanPipelineOrchestrator(AlibabaWanLowSpecEngine engine, AdaptiveHardwareProfiler profiler, LivePipelineAutoTuner tuner)
    {
        _engine = engine;
        _profiler = profiler;
        _tuner = tuner;
        _queue = Channel.CreateBounded<WanJobRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _ = ConsumeLoopAsync(_lifetimeCts.Token);   // 백그라운드 소비 루프 상시 기동(데모 타이머와 독립)
    }

    public bool DemoRunning => _demoTimer is not null;
    public string? LastAction { get; private set; }
    public int QueueDepth => _queue.Reader.Count;
    public bool Processing { get { lock (_gate) return _processing; } }
    public WanCircuitState Circuit { get { lock (_gate) return _circuit; } }
    public double CircuitCooldownSec { get { lock (_gate) return _circuit == WanCircuitState.Open ? Math.Max(0, (_openedUntil - DateTimeOffset.UtcNow).TotalSeconds) : 0; } }
    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failed => Interlocked.Read(ref _failed);
    public long CircuitTrips => Interlocked.Read(ref _circuitTrips);

    public IReadOnlyList<WanJobProgress> RecentJobs { get { lock (_gate) return new List<WanJobProgress>(_recent); } }

    public WanPipelineReport Report
    {
        get
        {
            lock (_gate)
                return new WanPipelineReport(QueueDepth, _processing, _circuit, Interlocked.Read(ref _enqueued), Interlocked.Read(ref _completed),
                    Interlocked.Read(ref _failed), Interlocked.Read(ref _circuitTrips), _profiler.DownscaleCount);
        }
    }

    /// <summary>작업 큐 진입(포화 시 최오래된 대기분 폐기) — 서킷 Open 이어도 큐잉은 허용(HalfOpen 전환 시 순차 소진).</summary>
    public string Enqueue(WanJobSpec spec)
    {
        var id = $"wan-{DateTimeOffset.UtcNow.Ticks % 100000}";
        _queue.Writer.TryWrite(new WanJobRequest(id, spec));
        Interlocked.Increment(ref _enqueued);
        lock (_gate) LastAction = $"작업 {id} 큐 진입({spec.Width}x{spec.Height}@{spec.Frames}f {spec.Quant})";
        OnChange?.Invoke();
        return id;
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var req in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { await ProcessJobAsync(req, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }   // 상위 catch 로 전파 — 정상 종료 경로
                catch (Exception ex) { FinishLocked(req.Id, false, "예외: " + ex.Message); }   // 단일 작업 예외가 워커 루프 자체를 죽이지 않도록 격리
            }
        }
        catch (OperationCanceledException) { /* Dispose 경로 — 정상 종료 */ }
    }

    private async Task ProcessJobAsync(WanJobRequest req, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_circuit == WanCircuitState.Open && DateTimeOffset.UtcNow < _openedUntil)
            {
                RecordLocked(new WanJobProgress(req.Id, "서킷 Open", 0, true, false, "서킷 개방 — 즉시 실패(GPU 재시도 차단)"));
                _failed++;
                LastAction = $"작업 {req.Id} 서킷 Open — 즉시 실패";
            }
            else
            {
                if (_circuit == WanCircuitState.Open) _circuit = WanCircuitState.HalfOpen;
                _processing = true;
            }
        }
        OnChange?.Invoke();
        if (!Processing) return;   // 서킷 Open 즉시실패 경로면 실행 생략

        ReportProgress(req.Id, "프로파일링", 5);
        var down = await _profiler.AutoDownscaleAsync(req.Spec, ct).ConfigureAwait(false);

        if (!down.Feasible)
        {
            FinishLocked(req.Id, false, $"다운스케일 바닥에도 예산 초과 — {down.Plan.Note}");
            return;
        }

        ReportProgress(req.Id, $"계획 확정({down.StepsApplied}단계 다운스케일)", 15);
        var tiling = down.Plan.Tiling;
        var liveQuant = down.Plan.Quant;   // v10.2.2: 사전 계획 양자화로 시작 — 이후 타일마다 라이브 튜너가 실시간 보정
        for (var i = 0; i < tiling.TileCount; i++)
        {
            var tileMs = Math.Max(5, 80 * AlibabaWanLowSpecEngine.QuantFactor(liveQuant));   // 양자화가 낮을수록(가벼울수록) 타일 처리 실측이 빨라짐(정직한 상대 모델)
            var tsw = Stopwatch.StartNew();
            try { await Task.Delay((int)tileMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { FinishLocked(req.Id, false, "취소됨"); return; }
            tsw.Stop();
            liveQuant = _tuner.ObserveFrame(tsw.Elapsed.TotalMilliseconds);   // 실측 타일 소요시간 통보 → 다음 타일 양자화 즉시 반영
            var pct = 15 + (int)((i + 1) / (double)tiling.TileCount * 80);
            ReportProgress(req.Id, $"타일 디코드 {i + 1}/{tiling.TileCount} · {liveQuant}", pct);
        }

        FinishLocked(req.Id, true, $"완료 · {down.FinalSpec.Width}x{down.FinalSpec.Height}@{down.FinalSpec.Frames}f 계획={down.Plan.Quant}→라이브={liveQuant} · {down.Plan.Note}");
    }

    private void ReportProgress(string jobId, string stage, int pct)
    {
        lock (_gate) RecordLocked(new WanJobProgress(jobId, stage, pct, false, true, stage));
        OnChange?.Invoke();
    }

    private void FinishLocked(string jobId, bool ok, string message)
    {
        lock (_gate)
        {
            RecordLocked(new WanJobProgress(jobId, ok ? "완료" : "실패", 100, true, ok, message));
            _processing = false;
            if (ok) { _completed++; _consecutiveFailures = 0; if (_circuit != WanCircuitState.Closed) LastAction = $"작업 {jobId} HalfOpen 프로브 성공 — Closed 복귀"; else LastAction = $"작업 {jobId} 완료"; _circuit = WanCircuitState.Closed; }
            else
            {
                _failed++; _consecutiveFailures++;
                if (_consecutiveFailures >= FailureThreshold && _circuit != WanCircuitState.Open)
                {
                    _circuit = WanCircuitState.Open; _openedUntil = DateTimeOffset.UtcNow + CooldownWindow; _circuitTrips++;
                    LastAction = $"연속 실패 {_consecutiveFailures}회 — 서킷 Open({CooldownWindow.TotalSeconds:0}s)";
                }
                else LastAction = $"작업 {jobId} 실패 — {message}";
            }
        }
        OnChange?.Invoke();
    }

    private void RecordLocked(WanJobProgress p)
    {
        _recent.Enqueue(p);
        while (_recent.Count > MaxRecent) _recent.Dequeue();
    }

    /// <summary>오토파일럿 — 다양한 스펙(일부는 의도적 과대 스펙)의 합성 작업을 주기 투입해 다운스케일/서킷 경로를 상시 실증.</summary>
    public void StartDemo()
    {
        lock (_gate)
        {
            if (_demoTimer is not null || _disposed) return;
            _demoTimer = new System.Threading.Timer(_ => EnqueueSynthetic(), null, 0, 1500);
            LastAction = "Wan 오토파일럿 기동";
        }
        OnChange?.Invoke();
    }

    private void EnqueueSynthetic()
    {
        var presets = new (int W, int H, int F, WanQuantMode Q)[]
        {
            (832, 480, 33, WanQuantMode.Fp16),
            (1280, 720, 49, WanQuantMode.Fp16),   // 고VRAM 요구(다운스케일 유발 의도)
            (512, 288, 17, WanQuantMode.Int8),
            (1920, 1080, 81, WanQuantMode.Fp16),  // 의도적 과대 스펙(서킷/다운스케일 극한 테스트)
        };
        var p = presets[_rng.Next(presets.Length)];
        Enqueue(new WanJobSpec("wan1.3b_fp8", p.W, p.H, p.F, 16, p.Q));
    }

    public void StopDemo()
    {
        lock (_gate) { _demoTimer?.Dispose(); _demoTimer = null; LastAction = "Wan 오토파일럿 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _recent.Clear(); _circuit = WanCircuitState.Closed; _consecutiveFailures = 0; _processing = false;
            _enqueued = _completed = _failed = _circuitTrips = 0;
            LastAction = "Wan 파이프라인 초기화";
        }
        _profiler.ResetCounters();
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _demoTimer?.Dispose(); _demoTimer = null; }
        _queue.Writer.TryComplete();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
