using System;
using System.Collections.ObjectModel;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Shorts;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] AI 15초 숏폼 스튜디오 뷰모델 — 주제/훅/BGM/자막 → 3컷 시퀀스 생성 · 9:16 렌더 큐 일괄 전송.
///   ▸ 전송마다 새 JobId 로 작업을 만들어 큐의 중복 차단에 걸리지 않음.
/// </summary>
public sealed class AiShortsViewModel
{
    private readonly AiShortsService _shorts;
    private readonly IRenderQueueService _queue;

    public AiShortsViewModel(AiShortsService shorts, IRenderQueueService queue)
    {
        _shorts = shorts ?? throw new ArgumentNullException(nameof(shorts));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public event Action? Changed;

    public string TopicInput { get; set; } = "";
    public ShortsHookType SelectedHook { get; set; } = ShortsHookType.QuestionHook;
    public BgmBeatPreset SelectedBgm { get; set; } = BgmBeatPreset.TrendingPop;
    public CaptionStyle SelectedCaption { get; set; } = CaptionStyle.BoldYellow;
    public string TargetResolution { get; set; } = AiShortsService.DefaultResolution;
    public float TotalDurationSec => AiShortsService.TotalDurationSec;

    public ObservableCollection<ShortsCutSpec> Cuts { get; } = new();
    public int DispatchedCount { get; private set; }
    public string? StatusMessage { get; private set; }

    public void GenerateSequence()
    {
        var seq = _shorts.GenerateShortsSequence(new ShortsRequest(TopicInput, SelectedHook, SelectedBgm, SelectedCaption, TargetResolution));
        Cuts.Clear();
        foreach (var cut in seq) Cuts.Add(cut);
        StatusMessage = "15초 3컷 시퀀스 생성 완료 · " + seq[0].Resolution;
        Raise();
    }

    /// <summary>3컷을 9:16 작업으로 변환해 렌더 큐에 일괄 적재 — 미생성 시 먼저 생성. 실제 적재 건수 반환.</summary>
    public int DispatchToRenderQueue()
    {
        if (Cuts.Count == 0) GenerateSequence();
        var jobs = new BatchRenderJob[Cuts.Count];
        for (var i = 0; i < jobs.Length; i++)
        {
            var cut = Cuts[i];
            jobs[i] = new BatchRenderJob
            {
                Prompt = cut.VisualPrompt,
                Seed = cut.Seed,
                Resolution = AiShortsService.NormalizeVerticalResolution(cut.Resolution),   // 9:16 잠금 — 가로형/빈 값이 섞여도 세로로 강제
                DurationSec = cut.DurationSec,
                CutIndex = cut.CutIndex,
                StartSec = cut.StartSec,
                BgmBpm = cut.BgmBpm,
                TransitionBeatIndex = cut.TransitionBeatIndex,
                TransitionFx = cut.TransitionFx,
            };
        }
        var added = _queue.Enqueue(jobs);
        DispatchedCount += added;
        StatusMessage = added > 0 ? "숏폼 " + added + "컷 렌더 큐 적재 (9:16)" : "렌더 큐 적재 실패";
        Raise();
        return added;
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }
}
