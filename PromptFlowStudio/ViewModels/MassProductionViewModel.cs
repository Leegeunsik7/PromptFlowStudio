using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 3번 워크플로우 【대량 양산 &amp; 비용】 뷰모델 — 다중 줄 프롬프트 입력 · 비용/VRAM 실시간 추산 · 렌더 큐 일괄 전송.
///   ▸ 프롬프트/해상도/길이/FPS 변경 시 <see cref="RenderCostEstimatorService.Estimate"/> 재호출(작업 0건이면 지표 0).
///   ▸ <see cref="GeneratedJobs"/> 는 입력·시드 변경마다 재전개되는 미리보기(Pending), 디스패치 직후엔 전송분(Queued)을 표시.
///   ▸ <see cref="DispatchAllToRenderQueue"/> 는 작업을 Queued 로 표시해 <see cref="RenderQueue"/> 에 적재.
///   ⚠ PropertyChanged 는 호출 스레드에서 발생 — Blazor 구독부는 InvokeAsync(StateHasChanged)로 마샬링한다.
/// </summary>
public sealed class MassProductionViewModel : INotifyPropertyChanged
{
    private readonly BatchMassProductionService _batch;
    private readonly RenderCostEstimatorService _estimator;

    public MassProductionViewModel(BatchMassProductionService batch, RenderCostEstimatorService estimator)
    {
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        Recalculate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>렌더 큐 — 전송된 작업 누적(상태는 렌더러가 Processing/Completed/Failed 로 갱신).</summary>
    public ObservableCollection<BatchRenderJob> RenderQueue { get; } = new();

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

    /// <summary>배치 작업 미리보기 — 현재 입력 전개분(Pending) 또는 직전 디스패치분(Queued).</summary>
    public IReadOnlyList<BatchRenderJob> GeneratedJobs { get; private set; } = Array.Empty<BatchRenderJob>();

    /// <summary>현재 입력을 작업 목록으로 전개해 렌더 큐에 적재 — 적재 건수 반환(빈 입력이면 0).</summary>
    public int DispatchAllToRenderQueue()
    {
        var jobs = _batch.GenerateBatchJobs(_promptInputText, _baseSeed, _incrementSeed, _selectedResolution, _durationSec);
        foreach (var job in jobs)
        {
            job.Status = BatchJobStatus.Queued;
            RenderQueue.Add(job);
        }
        GeneratedJobs = jobs;
        OnPropertyChanged(nameof(GeneratedJobs));
        return jobs.Count;
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
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        GeneratedJobs = _batch.GenerateBatchJobs(_promptInputText, _baseSeed, _incrementSeed, _selectedResolution, _durationSec);
        OnPropertyChanged(nameof(GeneratedJobs));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
