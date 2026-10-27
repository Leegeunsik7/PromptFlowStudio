using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>컷 사이 전환 효과.</summary>
public enum TransitionType { Cut, Dissolve, WhipPan, ZoomIn }

/// <summary>
/// [PromptFlowStudio] 2번 워크플로우 【스토리보드(멀티 컷)】 컷 카드 1장.
///   ⚠ PropertyChanged 는 호출 스레드에서 발생 — Blazor 구독부는 InvokeAsync(StateHasChanged)로 마샬링한다.
/// </summary>
public sealed class StoryboardCut : INotifyPropertyChanged
{
    public const float DefaultDurationSec = 5.0f;

    private int _cutIndex;
    private string _rawPrompt = "";
    private float _durationSec = DefaultDurationSec;
    private string _cameraMotion = "";
    private TransitionType _transitionType;
    private long _seed;
    private bool _isSubjectLocked;
    private string? _subjectIdentity;
    private string _script = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>컷 순서(1부터).</summary>
    public int CutIndex { get => _cutIndex; set => Set(ref _cutIndex, value); }
    public string RawPrompt { get => _rawPrompt; set => Set(ref _rawPrompt, value ?? ""); }
    /// <summary>컷 길이(초) — 음수/NaN 은 0 으로 보정.</summary>
    public float DurationSec { get => _durationSec; set => Set(ref _durationSec, float.IsFinite(value) && value > 0f ? value : 0f); }
    public string CameraMotion { get => _cameraMotion; set => Set(ref _cameraMotion, value ?? ""); }
    public TransitionType TransitionType { get => _transitionType; set => Set(ref _transitionType, value); }
    /// <summary>컷별 렌더링 시드.</summary>
    public long Seed { get => _seed; set => Set(ref _seed, value); }
    /// <summary>1번 컷 피사체 고정 상속 여부(후속 컷만 true).</summary>
    public bool IsSubjectLocked { get => _isSubjectLocked; set => Set(ref _isSubjectLocked, value); }
    /// <summary>상속된 피사체 정체성(1번 컷 기준).</summary>
    public string? SubjectIdentity { get => _subjectIdentity; set => Set(ref _subjectIdentity, value); }
    /// <summary>컷 대본(나레이션/대사) — 프로젝트 씬 VoiceText 와 동기화.</summary>
    public string Script { get => _script; set => Set(ref _script, value ?? ""); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        var h = PropertyChanged;
        if (h is null) return;
        try { h(this, new PropertyChangedEventArgs(name)); }
        catch (Exception) { }   // 구독자 예외 격리
    }
}

/// <summary>
/// [PromptFlowStudio] 2번 워크플로우 【스토리보드(멀티 컷)】 시퀀서 — 대본 → N 컷 분할 · 카메라 모션 자동 배정 · 시드/피사체 일관성 전파.
///   ▸ 분할: 줄바꿈 → 문장 부호(. ! ? 。 ！ ？ …) 순으로 조각낸 뒤 N 그룹으로 균등 병합, 부족하면 가장 긴 조각을 쉼표/어절 경계에서 반분.
///   ▸ 카메라: 첫 컷 설정 샷 · 마지막 컷 풀백 · 중간 컷 미디엄/클로즈업/오빗 순환.
/// </summary>
public sealed class StoryboardSequencerService
{
    public const int MaxCutCount = 24;

    public const string OpeningMotion = "wide establishing shot, slow dolly in";
    public const string ClosingMotion = "slow pull back, wide reveal";
    private static readonly string[] MiddleMotions =
    {
        "medium tracking shot",
        "close-up, slow push in",
        "orbit around subject",
    };

    /// <summary>대본을 최대 <paramref name="targetCutCount"/>(1~24) 컷으로 분할. 분할 가능한 조각이 부족하면 그만큼 적게 반환, 빈 대본은 빈 리스트.</summary>
    public List<StoryboardCut> BreakdownScript(string? fullScript, int targetCutCount = 4)
    {
        var cuts = new List<StoryboardCut>();
        if (string.IsNullOrWhiteSpace(fullScript)) return cuts;
        var target = Math.Clamp(targetCutCount, 1, MaxCutCount);

        var segments = SplitLines(fullScript);
        if (segments.Count < target) segments = SplitSentences(segments);
        while (segments.Count < target && TrySplitLongest(segments)) { }

        var groups = Math.Min(target, segments.Count);
        for (var g = 0; g < groups; g++)
        {
            var from = g * segments.Count / groups;
            var to = (g + 1) * segments.Count / groups;
            var prompt = segments[from];
            for (var i = from + 1; i < to; i++) prompt = prompt + " " + segments[i];

            cuts.Add(new StoryboardCut
            {
                CutIndex = g + 1,
                RawPrompt = prompt,
                CameraMotion = PickCameraMotion(g, groups),
                TransitionType = g == 0 ? TransitionType.Cut : TransitionType.Dissolve,
            });
        }
        return cuts;
    }

    /// <summary>1번 컷에 <paramref name="masterSeed"/>·<paramref name="subjectIdentity"/>를 고정하고 후속 컷 전체에 상속(IsSubjectLocked=true).</summary>
    public void PropagateConsistency(List<StoryboardCut> cuts, long masterSeed, string? subjectIdentity)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        var identity = string.IsNullOrWhiteSpace(subjectIdentity) ? null : subjectIdentity.Trim();
        for (var i = 0; i < cuts.Count; i++)
        {
            var c = cuts[i];
            if (c is null) continue;
            c.Seed = masterSeed;
            c.SubjectIdentity = identity;
            c.IsSubjectLocked = i > 0;
        }
    }

    public static string PickCameraMotion(int position, int count)
    {
        if (position <= 0) return OpeningMotion;
        if (position >= count - 1) return ClosingMotion;
        return MiddleMotions[(position - 1) % MiddleMotions.Length];
    }

    private static List<string> SplitLines(string script)
    {
        var list = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    private static List<string> SplitSentences(List<string> lines)
    {
        var list = new List<string>();
        foreach (var line in lines)
        {
            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (!IsSentenceEnd(line[i])) continue;
                if (line[i] == '.' && i > 0 && i + 1 < line.Length && char.IsDigit(line[i - 1]) && char.IsDigit(line[i + 1])) continue;   // 소수점 "3.5"
                while (i + 1 < line.Length && IsSentenceEnd(line[i + 1])) i++;   // "?!" · "..." 묶음
                AddTrimmed(list, line, start, i + 1);
                start = i + 1;
            }
            AddTrimmed(list, line, start, line.Length);
        }
        return list;
    }

    /// <summary>가장 긴 조각을 중앙에 가까운 쉼표(없으면 공백) 경계에서 반분. 경계가 없으면 false.</summary>
    private static bool TrySplitLongest(List<string> segments)
    {
        var idx = 0;
        for (var i = 1; i < segments.Count; i++)
            if (segments[i].Length > segments[idx].Length) idx = i;

        var s = segments[idx];
        var cut = FindNearestBoundary(s, ',');
        if (cut < 0) cut = FindNearestBoundary(s, ' ');
        if (cut < 0) return false;

        var left = s[..(cut + 1)].Trim();
        var right = s[(cut + 1)..].Trim();
        if (left.Length == 0 || right.Length == 0) return false;
        segments[idx] = left;
        segments.Insert(idx + 1, right);
        return true;
    }

    private static int FindNearestBoundary(string s, char sep)
    {
        var mid = s.Length / 2;
        var best = -1;
        for (var i = 1; i < s.Length - 1; i++)
            if (s[i] == sep && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
        return best;
    }

    private static bool IsSentenceEnd(char c) =>
        c is '.' or '!' or '?' or '。' or '！' or '？' or '…';

    private static void AddTrimmed(List<string> list, string s, int from, int to)
    {
        var t = s[from..to].Trim();
        if (t.Length > 0) list.Add(t);
    }
}
