using System;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>튜너 실시간 스냅샷 — EWMA 프레임 지연시간/목표/현재 양자화/조정 카운터.</summary>
public sealed record TunerSnapshot(double EwmaMs, double TargetMs, WanQuantMode CurrentQuant, long FramesObserved, long Downgrades, long Upgrades);

/// <summary>
/// v10.2.2 라이브 파이프라인 자동 튜너 — <see cref="WanPipelineOrchestrator"/> 의 실제 타일 디코드 루프에서
/// 타일(프레임) 1건이 끝날 때마다 <see cref="ObserveFrame"/> 로 소요시간을 통보받아 EWMA(지수가중이동평균)를
/// 갱신하고, 목표 프레임 예산(<see cref="TargetFrameMs"/>) 대비 지속 초과/여유를 히스테리시스(4틱)로 판정해
/// <b>같은 작업 실행 도중에도</b> 양자화(Fp16→Fp8→Int8)를 즉시 내리거나(지연 초과) 올린다(여유 지속). 사전 계획
/// 단계의 <see cref="AdaptiveHardwareProfiler"/> 다운스케일(해상도/프레임까지 포함)과 달리, 이 튜너는 <b>실행 중</b>
/// 양자화 축만 실시간 보정하는 경량 피드백 루프다. 순수 BCL · 하드웨어 비의존이라 모바일 등록 가능.
/// </summary>
public sealed class LivePipelineAutoTuner
{
    /// <summary>목표 타일(프레임) 처리 예산(ms) — 이보다 지속 초과하면 다운그레이드.</summary>
    public const double TargetFrameMs = 120;
    private const double DowngradeRatio = 1.15;      // EWMA > 목표×1.15 지속 → 다운그레이드
    private const double UpgradeHeadroomRatio = 0.55; // EWMA < 목표×0.55 지속 → 업그레이드
    private const int HysteresisTicks = 4;
    private const double EwmaAlpha = 0.25;

    private readonly object _gate = new();
    private readonly AutoTunerHardwareCalibrator _calibrator;
    private double _ewmaMs;
    private int _sampleCount;
    private int _overCount, _underCount;
    private WanQuantMode _quant = WanQuantMode.Fp16;
    private long _observed, _downgrades, _upgrades;

    public event Action? OnChange;

    // v10.5.0-F3 자율 결선: TargetFrameMs 자체는 "원하는 실시간 SLA"(사용자 의도 · 캘리브레이션 대상 아님)이므로
    // 불변 유지 — 대신 이 머신의 캘리브레이션 배율을 진단/HUD 용으로 노출(관측 스케일이 얼마나 재정규화됐는지 참고).
    public AutoTunerHardwareCalibrator Calibrator => _calibrator;
    /// <summary>이 머신의 캘리브레이션 배율(1.0=기준 머신과 동일 스케일) — 진단 표시 전용, ObserveFrame 계산엔 미사용.</summary>
    public double CalibratedHostScale => _calibrator.GetMultiplier();

    public LivePipelineAutoTuner(AutoTunerHardwareCalibrator calibrator) => _calibrator = calibrator;

    /// <summary>현재 권고 양자화 — 다음 타일부터 즉시 적용(호출측이 반영).</summary>
    public WanQuantMode CurrentQuant { get { lock (_gate) return _quant; } }
    public long FramesObserved => Interlocked.Read(ref _observed);
    public long Downgrades => Interlocked.Read(ref _downgrades);
    public long Upgrades => Interlocked.Read(ref _upgrades);
    public string? LastAction { get; private set; }

    public TunerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new TunerSnapshot(Math.Round(_ewmaMs, 2), TargetFrameMs, _quant, Interlocked.Read(ref _observed), Interlocked.Read(ref _downgrades), Interlocked.Read(ref _upgrades));
        }
    }

    /// <summary>
    /// 타일 1건 처리시간(ms) 통보 → EWMA 갱신 → 히스테리시스 판정 → 필요 시 양자화 즉시 보정.
    /// 반환값을 호출측(오케스트레이터)이 다음 타일의 실제 양자화로 적용해야 "실시간 보정"이 성립한다.
    /// </summary>
    public WanQuantMode ObserveFrame(double elapsedMs)
    {
        WanQuantMode result;
        bool changed;
        lock (_gate)
        {
            _observed++;
            _ewmaMs = _sampleCount == 0 ? elapsedMs : _ewmaMs * (1 - EwmaAlpha) + elapsedMs * EwmaAlpha;
            _sampleCount++;
            var before = _quant;

            if (_ewmaMs > TargetFrameMs * DowngradeRatio)
            {
                _underCount = 0;
                if (++_overCount >= HysteresisTicks) { _overCount = 0; DowngradeLocked(); }
            }
            else if (_ewmaMs < TargetFrameMs * UpgradeHeadroomRatio)
            {
                _overCount = 0;
                if (++_underCount >= HysteresisTicks) { _underCount = 0; UpgradeLocked(); }
            }
            else { _overCount = 0; _underCount = 0; }

            result = _quant;
            changed = before != _quant;
        }
        if (changed) OnChange?.Invoke();   // 락 해제 후 통지(구독자 재진입/경합 회피 · 프로젝트 공통 관례)
        return result;
    }

    private void DowngradeLocked()
    {
        if (_quant == WanQuantMode.Int8) return;
        _quant = _quant == WanQuantMode.Fp16 ? WanQuantMode.Fp8 : WanQuantMode.Int8;
        _downgrades++;
        LastAction = $"EWMA {_ewmaMs:0.#}ms > 목표×{DowngradeRatio:0.00} 지속 — {_quant} 로 다운그레이드";
    }

    private void UpgradeLocked()
    {
        if (_quant == WanQuantMode.Fp16) return;
        _quant = _quant == WanQuantMode.Int8 ? WanQuantMode.Fp8 : WanQuantMode.Fp16;
        _upgrades++;
        LastAction = $"EWMA {_ewmaMs:0.#}ms < 목표×{UpgradeHeadroomRatio:0.00} 지속 여유 — {_quant} 로 업그레이드";
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _ewmaMs = 0; _sampleCount = 0; _overCount = 0; _underCount = 0; _quant = WanQuantMode.Fp16;
            _observed = 0; _downgrades = 0; _upgrades = 0;
            LastAction = "라이브 튜너 초기화";
        }
        OnChange?.Invoke();
    }
}
