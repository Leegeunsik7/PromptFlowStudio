using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 2번 워크플로우 【스토리보드(멀티 컷)】 뷰모델 — 컷 카드 컬렉션 · 추가/삭제/순서 변경 · 대본 수신 자동 전개 · 총 재생 시간.
///   ▸ 컬렉션 변경 시 CutIndex 를 1부터 재번호, 컷 DurationSec 변경까지 구독해 <see cref="TotalDurationSec"/> 실시간 갱신.
///   ▸ MasterSeed/SubjectIdentity 지정 시 전개·재정렬 후 1번 컷 기준 일관성 재전파.
///   ⚠ PropertyChanged 는 호출 스레드에서 발생 — Blazor 구독부는 InvokeAsync(StateHasChanged)로 마샬링한다.
/// </summary>
public sealed class StoryboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly StoryboardSequencerService _sequencer;
    private readonly List<StoryboardCut> _tracked = new();
    private bool _suppress, _disposed;

    public StoryboardViewModel(StoryboardSequencerService sequencer)
    {
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        Cuts.CollectionChanged += OnCutsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<StoryboardCut> Cuts { get; } = new();

    private float _totalDurationSec;
    public float TotalDurationSec => _totalDurationSec;

    private int _targetCutCount = 4;
    /// <summary>대본 자동 전개 시 목표 컷 수(1~24).</summary>
    public int TargetCutCount
    {
        get => _targetCutCount;
        set
        {
            var v = Math.Clamp(value, 1, StoryboardSequencerService.MaxCutCount);
            if (v == _targetCutCount) return;
            _targetCutCount = v;
            OnPropertyChanged(nameof(TargetCutCount));
        }
    }

    private long? _masterSeed;
    public long? MasterSeed { get => _masterSeed; set { if (_masterSeed == value) return; _masterSeed = value; OnPropertyChanged(nameof(MasterSeed)); Repropagate(); } }

    private string? _subjectIdentity;
    public string? SubjectIdentity { get => _subjectIdentity; set { if (_subjectIdentity == value) return; _subjectIdentity = value; OnPropertyChanged(nameof(SubjectIdentity)); Repropagate(); } }

    /// <summary>"대본 자동 생성" 수신 — 대본을 컷 카드로 즉시 전개(기존 컷 교체). 빈 대본이면 false(상태 불변).</summary>
    public bool ReceiveGeneratedScript(string? script)
    {
        var cuts = _sequencer.BreakdownScript(script, _targetCutCount);
        if (cuts.Count == 0) return false;
        if (_masterSeed is long seed) _sequencer.PropagateConsistency(cuts, seed, _subjectIdentity);

        _suppress = true;
        try
        {
            Cuts.Clear();
            foreach (var c in cuts) Cuts.Add(c);
        }
        finally { _suppress = false; }
        AfterStructureChanged();
        return true;
    }

    /// <summary>컷 추가(끝) — 카메라 모션은 위치 기반 기본값.</summary>
    public StoryboardCut AddCut(string? rawPrompt = null)
    {
        var cut = new StoryboardCut
        {
            RawPrompt = rawPrompt ?? "",
            CameraMotion = StoryboardSequencerService.PickCameraMotion(Cuts.Count, Cuts.Count + 1),
            TransitionType = Cuts.Count == 0 ? TransitionType.Cut : TransitionType.Dissolve,
        };
        Cuts.Add(cut);
        return cut;
    }

    public bool RemoveCut(int position)
    {
        if ((uint)position >= (uint)Cuts.Count) return false;
        Cuts.RemoveAt(position);
        return true;
    }

    public bool RemoveCut(StoryboardCut cut) => Cuts.Remove(cut);

    public bool MoveUp(int position)
    {
        if (position <= 0 || position >= Cuts.Count) return false;
        Cuts.Move(position, position - 1);
        return true;
    }

    public bool MoveDown(int position)
    {
        if (position < 0 || position >= Cuts.Count - 1) return false;
        Cuts.Move(position, position + 1);
        return true;
    }

    private void OnCutsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppress) return;
        AfterStructureChanged();
    }

    private void AfterStructureChanged()
    {
        Resubscribe();
        for (var i = 0; i < Cuts.Count; i++) Cuts[i].CutIndex = i + 1;
        Repropagate();
        RecalculateTotal();
    }

    /// <summary>Reset/Replace 포함 모든 변경을 단순 처리 — 이전 구독 전량 해제 후 현재 컷 재구독.</summary>
    private void Resubscribe()
    {
        foreach (var c in _tracked) c.PropertyChanged -= OnCutPropertyChanged;
        _tracked.Clear();
        foreach (var c in Cuts)
        {
            c.PropertyChanged += OnCutPropertyChanged;
            _tracked.Add(c);
        }
    }

    private void OnCutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoryboardCut.DurationSec)) RecalculateTotal();
    }

    private void Repropagate()
    {
        if (_masterSeed is not long seed || Cuts.Count == 0) return;
        _sequencer.PropagateConsistency(_tracked, seed, _subjectIdentity);
    }

    private void RecalculateTotal()
    {
        var sum = 0f;
        for (var i = 0; i < Cuts.Count; i++) sum += Cuts[i].DurationSec;
        if (sum == _totalDurationSec) return;
        _totalDurationSec = sum;
        OnPropertyChanged(nameof(TotalDurationSec));
    }

    private void OnPropertyChanged(string? name)
    {
        var h = PropertyChanged;
        if (h is null) return;
        try { h(this, new PropertyChangedEventArgs(name)); }
        catch (Exception ex) { Program.Log("StoryboardViewModel.PropertyChanged", ex); }   // 구독자 예외 격리
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cuts.CollectionChanged -= OnCutsChanged;
        foreach (var c in _tracked) c.PropertyChanged -= OnCutPropertyChanged;
        _tracked.Clear();
    }
}
