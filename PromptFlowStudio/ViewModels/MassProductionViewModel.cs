using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 3번 워크플로우 【대량 양산 &amp; 비용】 뷰모델 — 다중 줄 프롬프트 입력 · 비용/VRAM 실시간 추산 · 렌더 큐 일괄 전송.
///   ▸ 프롬프트/해상도/길이/FPS 변경 시 <see cref="RenderCostEstimatorService.Estimate"/> 재호출(작업 0건이면 지표 0).
///   ▸ <see cref="GeneratedJobs"/> 는 입력·시드 변경마다 재전개되는 미리보기(Pending), 디스패치 직후엔 전송분(Queued)을 표시.
///   ▸ <see cref="DispatchAllToRenderQueue"/> 는 작업을 <see cref="IRenderQueueService.Enqueue"/> 로 전역 렌더 큐에 직접 적재(Queued).
///   ▸ 비용 트래커 주입 시: 디스패치 전 예산 한도 가드(초과 시 <see cref="BudgetWarning"/> · 명시 승인 후 진행) → 작업별 예상 USD 예약,
///     워커가 작업을 집어 가면(Queued 이탈) 예약 해제 — 실지출은 엔진 제출 시 원장 기록(이중 계상 방지).
///   ⚠ PropertyChanged 는 호출 스레드에서 발생 — Blazor 구독부는 InvokeAsync(StateHasChanged)로 마샬링한다.
/// </summary>
public sealed class MassProductionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BatchMassProductionService _batch;
    private readonly RenderCostEstimatorService _estimator;
    private readonly IRenderQueueService _queue;
    private readonly ProductionCostTrackerService? _cost;
    private readonly List<BatchRenderJob> _reserved = new();

    public MassProductionViewModel(BatchMassProductionService batch, RenderCostEstimatorService estimator, IRenderQueueService queue,
        ProductionCostTrackerService? cost = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _cost = cost;
        if (_cost is not null) _queue.OnChange += OnQueueChanged;
        Recalculate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>전역 렌더 큐(<see cref="IRenderQueueService.Queue"/>) — 상태는 워커가 Processing/Completed/Failed 로 갱신.</summary>
    public ObservableCollection<BatchRenderJob> RenderQueue => _queue.Queue;

    private string _promptInputText = "";
    public string PromptInputText { get => _promptInputText; set { value ??= ""; if (_promptInputText == value) return; _promptInputText = value; OnPropertyChanged(nameof(PromptInputText)); Recalculate(); } }

    private string _selectedResolution = RenderCostEstimatorService.DefaultResolution;
    public string SelectedResolution { get => _selectedResolution; set { value = string.IsNullOrWhiteSpace(value) ? RenderCostEstimatorService.DefaultResolution : value; if (_selectedResolution == value) return; _selectedResolution = value; OnPropertyChanged(nameof(SelectedResolution)); Recalculate(); } }

    private float _durationSec = RenderCostEstimatorService.DefaultDurationSec;
    public float DurationSec { get => _durationSec; set { if (_durationSec == value) return; _durationSec = value; OnPropertyChanged(nameof(DurationSec)); Recalculate(); } }

    private int _fps = RenderCostEstimatorService.DefaultFps;
    public int Fps { get => _fps; set { if (_fps == value) return; _fps = value; OnPropertyChanged(nameof(Fps)); Recalculate(); } }

    private long _baseSeed;
    public long BaseSeed { get => _baseSeed; set { if (_baseSeed == value) return; _baseSeed = value; OnPropertyChanged(nameof(BaseSeed)); RefreshPreview(); } }

    private bool _incrementSeed = true;
    public bool IncrementSeed { get => _incrementSeed; set { if (_incrementSeed == value) return; _incrementSeed = value; OnPropertyChanged(nameof(IncrementSeed)); RefreshPreview(); } }

    public int JobCount { get; private set; }
    public int TotalEstimatedCredits { get; private set; }
    public string EstimatedVramDisplay { get; private set; } = "0.0 GB";
    public double EstimatedRenderTimeSec { get; private set; }
    /// <summary>현재 입력 전체의 예상 USD(비용 트래커 요금표 · 트래커 미주입 시 0).</summary>
    public double EstimatedUsd { get; private set; }
    /// <summary>예산 한도 초과 경고 — 직전 디스패치가 차단됨(승인 시 <c>DispatchAllToRenderQueue(true)</c>).</summary>
    public bool BudgetWarning { get; private set; }
    public string? BudgetWarningMessage { get; private set; }

    /// <summary>배치 작업 미리보기 — 현재 입력 전개분(Pending) 또는 직전 디스패치분(Queued).</summary>
    public IReadOnlyList<BatchRenderJob> GeneratedJobs { get; private set; } = Array.Empty<BatchRenderJob>();

    /// <summary>현재 입력을 작업 목록으로 전개해 렌더 큐에 적재 — 적재 건수 반환(빈 입력 · 예산 차단이면 0).</summary>
    /// <param name="approveOverBudget">예산 한도 초과 경고를 사용자가 명시 승인한 경우 true.</param>
    public int DispatchAllToRenderQueue(bool approveOverBudget = false)
    {
        var jobs = _batch.GenerateBatchJobs(_promptInputText, _baseSeed, _incrementSeed, _selectedResolution, _durationSec);
        var usdPerJob = JobUsd(_durationSec);
        if (_cost is not null && jobs.Count > 0 && !approveOverBudget
            && (_cost.IsVideoGenerationBlocked || _cost.WouldExceedBudget(usdPerJob * jobs.Count)))
        {
            SetBudgetWarning("💸 예산 한도 초과 경고 — 예상 $" + ProductionCostTrackerService.Usd(usdPerJob * jobs.Count)
                + " (이번 달 $" + ProductionCostTrackerService.Usd(_cost.MonthUsd) + " + 예약 $" + ProductionCostTrackerService.Usd(_cost.ReservedUsd)
                + "). 승인 후 진행하세요.");
            return 0;
        }

        var added = _queue.Enqueue(jobs);
        if (_cost is not null && added > 0)
        {
            lock (_reserved)
            {
                foreach (var job in jobs)
                {
                    if (job.Status != BatchJobStatus.Queued) continue;
                    if (_cost.ReserveEstimate(job.JobId, usdPerJob)) _reserved.Add(job);
                }
            }
        }
        SetBudgetWarning(null);
        GeneratedJobs = jobs;
        OnPropertyChanged(nameof(GeneratedJobs));
        return added;
    }

    /// <summary>작업 1건 예상 USD — 클라우드 기본 요금(DashScope plus 초당) × 길이.</summary>
    private double JobUsd(float durationSec)
    {
        if (_cost is null) return 0;
        var sec = float.IsFinite(durationSec) && durationSec > 0f ? durationSec : RenderCostEstimatorService.DefaultDurationSec;
        return ProductionCostTrackerService.WanUsd(_cost.Rates, CostService.DashScope, null, sec);
    }

    private void SetBudgetWarning(string? message)
    {
        var warn = message is not null;
        if (BudgetWarning == warn && BudgetWarningMessage == message) return;
        BudgetWarning = warn;
        BudgetWarningMessage = message;
        OnPropertyChanged(nameof(BudgetWarning));
        OnPropertyChanged(nameof(BudgetWarningMessage));
    }

    /// <summary>워커가 집어 간(Queued 이탈) · 취소된 작업의 예약 해제 — 워커 스레드 가능 · 예외 격리.</summary>
    private void OnQueueChanged()
    {
        try
        {
            lock (_reserved)
            {
                for (var i = _reserved.Count - 1; i >= 0; i--)
                {
                    var job = _reserved[i];
                    if (job.Status is BatchJobStatus.Queued or BatchJobStatus.Pending) continue;
                    _cost?.ReleaseReservation(job.JobId);
                    _reserved.RemoveAt(i);
                }
            }
        }
        catch (Exception ex) { Program.Log("MassProduction.Reservation", ex); }
    }

    public void Dispose()
    {
        if (_cost is not null) _queue.OnChange -= OnQueueChanged;
    }

    private void Recalculate()
    {
        var count = BatchMassProductionService.CountPrompts(_promptInputText);
        int credits;
        double vram, sec;
        if (count == 0) { credits = 0; vram = 0; sec = 0; }
        else
        {
            var r = _estimator.Estimate(new CostEstimateRequest(_selectedResolution, _durationSec, count, _fps));
            credits = r.EstimatedCredits;
            vram = r.EstimatedVramGb;
            sec = r.EstimatedRenderTimeSec;
        }
        var vramText = vram.ToString("0.0", CultureInfo.InvariantCulture) + " GB";

        if (JobCount != count) { JobCount = count; OnPropertyChanged(nameof(JobCount)); }
        if (TotalEstimatedCredits != credits) { TotalEstimatedCredits = credits; OnPropertyChanged(nameof(TotalEstimatedCredits)); }
        if (EstimatedVramDisplay != vramText) { EstimatedVramDisplay = vramText; OnPropertyChanged(nameof(EstimatedVramDisplay)); }
        if (EstimatedRenderTimeSec != sec) { EstimatedRenderTimeSec = sec; OnPropertyChanged(nameof(EstimatedRenderTimeSec)); }
        var usd = count == 0 ? 0 : JobUsd(_durationSec) * count;
        if (EstimatedUsd != usd) { EstimatedUsd = usd; OnPropertyChanged(nameof(EstimatedUsd)); }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        GeneratedJobs = _batch.GenerateBatchJobs(_promptInputText, _baseSeed, _incrementSeed, _selectedResolution, _durationSec);
        OnPropertyChanged(nameof(GeneratedJobs));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
