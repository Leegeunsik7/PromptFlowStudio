using System;
using System.Threading;

namespace PromptFlowStudio.Services.Pipeline;

public sealed record TunerLoopView(double Quality, double Concurrency, double VramPct, double LatencyMs, double ErrorSignal, double Integral);

public sealed record TunerReport(long Ticks, long Corrections, long SaturationHits, double Quality, double Concurrency, double AchievedLatencyMs);

/// <summary>
/// v9.9.3 자기 최적화 파이프라인 튜너 — <see cref="Integration.SelfOptimizingMultimodalPipeline"/>(이산 5티어
/// 히스테리시스 스위처)와 달리, VRAM 점유율 오차를 목표치(70%) 대비 <b>PID 제어 루프</b>(비례+적분+미분, 적분
/// 와인드업 클램프)로 연속 스칼라 "품질 노브"(0.05~1.0)와 "동시성 워커 수"(1~16)를 매끄럽게 튜닝한다.
/// 순수 BCL(Timer·Interlocked) · 백엔드 미구성 시 데모 루프로 Graceful.
/// </summary>
public sealed class SelfOptimizingPipelineTuner : IDisposable
{
    public const int TickMs = 200;
    private const double TargetVram = 70.0;
    private const double Kp = 0.006, Ki = 0.0009, Kd = 0.003;
    private const double IntegralClamp = 40.0;

    private readonly object _gate = new();
    private readonly Random _rng = new(9903);
    private System.Threading.Timer? _timer;
    private double _quality = 0.75, _concurrency = 8, _vram = 65, _latency = 30;
    private double _integral, _prevError;
    private double _vramBias, _latBias;
    private long _ticks, _corrections, _saturations;
    private bool _disposed;

    public event Action? OnChange;

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Ticks => Interlocked.Read(ref _ticks);
    public long Corrections => Interlocked.Read(ref _corrections);
    public long SaturationHits => Interlocked.Read(ref _saturations);

    public TunerLoopView State
    {
        get
        {
            lock (_gate)
                return new TunerLoopView(Math.Round(_quality, 3), Math.Round(_concurrency, 2), Math.Round(_vram, 1), Math.Round(_latency, 1), Math.Round(TargetVram - _vram, 2), Math.Round(_integral, 2));
        }
    }

    public TunerReport Report
    {
        get
        {
            lock (_gate)
                return new TunerReport(Interlocked.Read(ref _ticks), Interlocked.Read(ref _corrections), Interlocked.Read(ref _saturations), Math.Round(_quality, 3), Math.Round(_concurrency, 2), Math.Round(_latency, 1));
        }
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = $"PID 튜닝 루프 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "튜닝 루프 정지"; }
        OnChange?.Invoke();
    }

    public void InjectVramPressure() { lock (_gate) { _vramBias += 25; LastAction = "VRAM 압박 주입"; } OnChange?.Invoke(); }

    public void InjectLatencySpike() { lock (_gate) { _latBias += 30; LastAction = "지연시간 스파이크 주입"; } OnChange?.Invoke(); }

    public void ResetAll()
    {
        lock (_gate)
        {
            _quality = 0.75; _concurrency = 8; _vram = 65; _latency = 30;
            _integral = 0; _prevError = 0; _vramBias = 0; _latBias = 0;
            LastAction = "PID 튜너 초기화";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            _ticks++;
            _vramBias *= 0.9; _latBias *= 0.9;

            // 플랜트 모델: 품질/동시성이 높을수록 VRAM·지연 부하 증가(무할당 스칼라 시뮬레이션)
            var demand = (_quality * 0.6 + _concurrency / 16.0 * 0.4);
            _vram = Math.Clamp(30 + demand * 55 + _vramBias + Noise(2.5), 5, 100);
            _latency = Math.Clamp(10 + demand * 45 + _latBias + Noise(2.5), 2, 300);

            var error = TargetVram - _vram;                       // 양수=여유(업스케일 가능), 음수=압박(다운스케일 필요)
            _integral = Math.Clamp(_integral + error * (TickMs / 1000.0), -IntegralClamp, IntegralClamp);
            var saturatedIntegral = Math.Abs(_integral) >= IntegralClamp - 0.01;
            if (saturatedIntegral) _saturations++;
            var derivative = (error - _prevError) / (TickMs / 1000.0);
            var output = Kp * error + Ki * _integral + Kd * derivative;
            _prevError = error;

            if (Math.Abs(output) > 0.0005)
            {
                _corrections++;
                _quality = Math.Clamp(_quality + output, 0.05, 1.0);
                _concurrency = Math.Clamp(_concurrency + output * 12, 1, 16);
                LastAction = output > 0
                    ? $"업튜닝 · 품질 {_quality:0.00} · 동시성 {_concurrency:0.0}"
                    : $"다운튜닝 · 품질 {_quality:0.00} · 동시성 {_concurrency:0.0}";
            }
        }
        OnChange?.Invoke();
    }

    private double Noise(double amp) => (_rng.NextDouble() * 2 - 1) * amp;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
