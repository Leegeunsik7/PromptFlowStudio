using System;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

public enum TuneTier { Ultra, High, Balanced, Eco, Survival }

public sealed record TuneStateView(TuneTier Tier, int TileRes, int InterpFrames, int TargetFps, double VramPct, double RamPct, double GpuLatencyMs, double AchievedFps);

public sealed record TuneReport(long Adjusts, long Upshifts, long Downshifts, long Throttles, double AchievedFps, TuneTier Tier);

/// <summary>
/// v9.7.3 자기 자율 최적화 멀티모달 파이프라인 — VRAM/RAM 압박과 GPU 지연시간 피드백 루프로
/// 타일 해상도·보간 프레임·목표 FPS 를 5티어(Ultra~Survival) 적응형 동적 스케일링한다.
/// 히스테리시스(연속 초과/여유 카운트)로 진동을 억제, 최저 티어 초과 압박은 프레임 스킵 스로틀링으로 Graceful.
/// 순수 BCL(Timer·Interlocked).
/// </summary>
public sealed class SelfOptimizingMultimodalPipeline : IDisposable
{
    public const int TickMs = 250;
    private const double VramHigh = 90, VramLow = 55, LatHigh = 60, LatLow = 25;
    private const int Hysteresis = 3;

    // 티어별 (타일 해상도, 보간 프레임, 목표 FPS) — Ultra(0) → Survival(4)
    private static readonly (int Tile, int Interp, int Fps)[] Presets =
    {
        (2160, 4, 60),
        (1440, 3, 48),
        (1080, 2, 30),
        (720, 1, 24),
        (480, 0, 15),
    };

    private readonly object _gate = new();
    private readonly Random _rng = new(9703);
    private System.Threading.Timer? _timer;
    private int _tier = 2;   // Balanced
    private double _vram = 62, _ram = 58, _lat = 32, _fpsEwma = 30;
    private int _overCount, _underCount;
    private double _vramBias, _latBias;   // 주입 압박(서서히 감쇠)
    private long _adjusts, _upshifts, _downshifts, _throttles;
    private bool _disposed;

    public event Action? OnChange;

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Adjusts => Interlocked.Read(ref _adjusts);
    public long Upshifts => Interlocked.Read(ref _upshifts);
    public long Downshifts => Interlocked.Read(ref _downshifts);
    public long Throttles => Interlocked.Read(ref _throttles);

    public TuneStateView State
    {
        get
        {
            lock (_gate)
            {
                var p = Presets[_tier];
                return new TuneStateView((TuneTier)_tier, p.Tile, p.Interp, p.Fps, Math.Round(_vram, 1), Math.Round(_ram, 1), Math.Round(_lat, 1), Math.Round(_fpsEwma, 1));
            }
        }
    }

    public TuneReport Report
    {
        get
        {
            lock (_gate)
                return new TuneReport(Interlocked.Read(ref _adjusts), Interlocked.Read(ref _upshifts), Interlocked.Read(ref _downshifts), Interlocked.Read(ref _throttles), Math.Round(_fpsEwma, 1), (TuneTier)_tier);
        }
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = $"적응형 튜닝 루프 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "튜닝 정지"; }
        OnChange?.Invoke();
    }

    public void InjectVramPressure() { lock (_gate) { _vramBias += 30; LastAction = "VRAM 압박 주입"; } OnChange?.Invoke(); }

    public void InjectLatencySpike() { lock (_gate) { _latBias += 35; LastAction = "GPU 지연 스파이크 주입"; } OnChange?.Invoke(); }

    public void ResetAll()
    {
        lock (_gate)
        {
            _tier = 2; _vram = 62; _ram = 58; _lat = 32; _fpsEwma = 30;
            _overCount = _underCount = 0; _vramBias = _latBias = 0;
            LastAction = "파이프라인 튜너 초기화";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            _vramBias *= 0.9; _latBias *= 0.9;                 // 주입 압박 자율 감쇠
            var demand = (Presets.Length - 1 - _tier) / (double)(Presets.Length - 1);   // 상위 티어일수록 자원 소모↑
            _vram = Math.Clamp(45 + demand * 45 + _vramBias + Noise(3), 5, 100);
            _ram = Math.Clamp(40 + demand * 35 + _vramBias * 0.4 + Noise(3), 5, 100);
            _lat = Math.Clamp(18 + demand * 40 + _latBias + Noise(3), 4, 250);
            var targetFps = Presets[_tier].Fps;
            var achieved = _lat > LatHigh ? targetFps * (LatHigh / _lat) : targetFps;
            _fpsEwma += 0.3 * (achieved - _fpsEwma);

            if (_vram >= VramHigh || _lat >= LatHigh)
            {
                _underCount = 0;
                if (++_overCount >= Hysteresis) { _overCount = 0; Downshift(); }
            }
            else if (_vram <= VramLow && _lat <= LatLow)
            {
                _overCount = 0;
                if (++_underCount >= Hysteresis) { _underCount = 0; Upshift(); }
            }
            else { _overCount = 0; _underCount = 0; }
        }
        OnChange?.Invoke();
    }

    private void Downshift()
    {
        if (_tier >= Presets.Length - 1) { _throttles++; LastAction = "최저 티어 — 프레임 스킵 스로틀링"; return; }
        _tier++; _adjusts++; _downshifts++;
        LastAction = $"다운시프트 → {(TuneTier)_tier} ({Presets[_tier].Tile}p/{Presets[_tier].Fps}fps)";
    }

    private void Upshift()
    {
        if (_tier <= 0) return;
        _tier--; _adjusts++; _upshifts++;
        LastAction = $"업시프트 → {(TuneTier)_tier} ({Presets[_tier].Tile}p/{Presets[_tier].Fps}fps)";
    }

    private double Noise(double amp) => (_rng.NextDouble() * 2 - 1) * amp;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
