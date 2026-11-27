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
    private readonly PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService? _memory;

    /// <param name="memory">🧠 플루토 학습 메모리(선택) — 주제 기준 학습 앵커를 3컷 공통 부착. null 이면 생략.</param>
    public AiShortsViewModel(AiShortsService shorts, IRenderQueueService queue, PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService? memory = null)
    {
        _shorts = shorts ?? throw new ArgumentNullException(nameof(shorts));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _memory = memory;
    }

    /// <summary>마지막 전송 그룹에 대기/렌더 중 컷이 남아 있는지 — UI 중복 전송 차단.</summary>
    public bool IsLastDispatchInFlight => MultiCutContinuity.IsGroupInFlight(_queue.Snapshot(), LastDispatchGroupId);

    /// <summary>마지막 전송 그룹의 컷별 QC 상태.</summary>
    public List<CutStatus> CutStatuses() => MultiCutContinuity.GroupStatuses(_queue.Snapshot(), LastDispatchGroupId);

    /// <summary>🔁 결함 컷 1건만 새 시드로 재렌더(나머지 컷 유지).</summary>
    public bool RegenerateCut(int cutIndex)
    {
        var ok = MultiCutContinuity.RegenerateCut(_queue, LastDispatchGroupId, cutIndex);
        StatusMessage = ok ? "컷 " + cutIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 재생성 적재 — 나머지 컷 유지" : "재생성 불가(진행 중이거나 대상 없음)";
        Raise();
        return ok;
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
    /// <summary>마지막 숏폼 세션 합본 그룹 — 3컷 Completed 시 최종 합본 대상.</summary>
    public string? LastDispatchGroupId { get; private set; }
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
        var group = FFmpegVideoStitchService.NewGroupId();
        var shared = MultiCutContinuity.Extract(Cuts[0].VisualPrompt, string.IsNullOrWhiteSpace(TopicInput) ? null : TopicInput.Trim());   // 주제 = 피사체 앵커
        PromptFlowStudio.Services.AI.PlutoLearnedGuidance? guidance = null;
        try { guidance = _memory?.GetLearnedGuidanceForPrompt(TopicInput); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PromptFlowStudio.Program.Log("AiShorts.LearnedAnchor", ex); }
        for (var i = 0; i < jobs.Length; i++)
        {
            var cut = Cuts[i];
            jobs[i] = new BatchRenderJob
            {
                Prompt = MultiCutContinuity.Apply(cut.VisualPrompt, shared, cut.CutIndex, guidance),   // 학습 앵커(전 컷) + 피사체/의상/장소(컷 2~)
                Seed = cut.Seed,
                Resolution = AiShortsService.NormalizeVerticalResolution(cut.Resolution),   // 9:16 잠금 — 가로형/빈 값이 섞여도 세로로 강제
                DurationSec = cut.DurationSec,
                CutIndex = cut.CutIndex,
                StartSec = cut.StartSec,
                BgmBpm = cut.BgmBpm,
                TransitionBeatIndex = cut.TransitionBeatIndex,
                TransitionFx = cut.TransitionFx,
                Metadata = FFmpegVideoStitchService.WithGroup(MultiCutContinuity.WithHardCut(null), group, cut.Script),   // 하드 컷 기본 + 나레이션 대본 → 합본 TTS·SRT
            };
        }
        var added = _queue.Enqueue(jobs);
        if (added > 0) LastDispatchGroupId = group;
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
