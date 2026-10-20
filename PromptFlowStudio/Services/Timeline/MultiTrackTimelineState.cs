using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Timeline;

/// <summary>NLE 트랙 종류 — 비디오 컷 / 보컬 립싱크 / 3D 자이로 모션 / 프롬프트 오버라이드.</summary>
public enum TimelineTrackKind { Video, Vocal, Gyro, Prompt }

/// <summary>
/// [PromptFlow Studio v7.0.0] 타임라인 클립 1개(살아있는 엔티티 · 제자리 갱신) — 트랙/시작/길이/라벨/액센트.
/// </summary>
public sealed class TimelineClip
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public TimelineTrackKind Kind { get; init; }
    public double StartSec { get; set; }
    public double DurationSec { get; set; }
    public string Label { get; set; } = "";
    public string Accent { get; set; } = "#22d3ee";
    public string? ThumbUrl { get; set; }

    public double EndSec => StartSec + DurationSec;
}

/// <summary>
/// [PromptFlow Studio v7.0.0] 멀티트랙 NLE 타임라인 상태(관찰 가능 싱글턴 · 순수 BCL · 외부 라이브러리 0).
///   ▸ 4트랙(비디오/보컬/자이로/프롬프트) × 클립 목록 + 플레이헤드 + 총 길이. 스냅(0.5초 그리드)으로 정밀 배치.
///   ▸ 추가/이동/리사이즈/삭제는 경계·겹침 클램프 후 <see cref="OnChange"/> 방송. UI 는 델타 드래그로 이 API 를 호출.
///   ⚠ UI 이벤트(포인터) 스레드에서 갱신 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class MultiTrackTimelineState
{
    private const double SnapSec = 0.5;
    private const double MinDurSec = 0.5;
    private readonly object _gate = new();
    private readonly List<TimelineClip> _clips = new();

    public MultiTrackTimelineState() => SeedDemo();

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public double TotalDurationSec { get; private set; } = 30;
    public double PlayheadSec { get; private set; }

    public IReadOnlyList<TimelineClip> Clips { get { lock (_gate) return _clips.ToList(); } }
    public IReadOnlyList<TimelineClip> ClipsFor(TimelineTrackKind kind) { lock (_gate) return _clips.Where(c => c.Kind == kind).ToList(); }
    public int ClipCount { get { lock (_gate) return _clips.Count; } }

    public static double Snap(double sec) => Math.Round(sec / SnapSec) * SnapSec;

    /// <summary>클립 추가 — 경계 클램프 + 스냅. 반환=생성 클립.</summary>
    public TimelineClip AddClip(TimelineTrackKind kind, double startSec, double durationSec, string label, string accent, string? thumb = null)
    {
        var dur = Math.Clamp(Snap(durationSec), MinDurSec, TotalDurationSec);
        var start = Math.Clamp(Snap(startSec), 0, Math.Max(0, TotalDurationSec - dur));
        var clip = new TimelineClip { Kind = kind, StartSec = start, DurationSec = dur, Label = label, Accent = accent, ThumbUrl = thumb };
        lock (_gate) _clips.Add(clip);
        Notify();
        return clip;
    }

    /// <summary>
    /// [v8.2.0 협업] 지정 Id 로 클립을 상향(있으면 제자리 갱신 · 없으면 그 Id 로 추가). 원격 CRDT 델타를 클립 정체성을 보존하며 물질화한다.
    ///   경계·스냅 클램프는 <see cref="AddClip"/> 와 동일. 방송 억제 옵션(대량 동기화 시 마지막에 1회 방송).
    /// </summary>
    public TimelineClip UpsertClip(string id, TimelineTrackKind kind, double startSec, double durationSec, string label, string accent, bool notify = true)
    {
        var dur = Math.Clamp(Snap(durationSec), MinDurSec, TotalDurationSec);
        var start = Math.Clamp(Snap(startSec), 0, Math.Max(0, TotalDurationSec - dur));
        TimelineClip clip;
        lock (_gate)
        {
            clip = _clips.FirstOrDefault(x => x.Id == id) ?? AddTracked(id, kind);
            clip.StartSec = start; clip.DurationSec = dur; clip.Label = label ?? ""; clip.Accent = accent ?? "#22d3ee";
        }
        if (notify) Notify();
        return clip;
    }

    /// <summary>지정 Id 로 빈 클립을 목록에 편입(락 아래 호출 · UpsertClip 전용).</summary>
    private TimelineClip AddTracked(string id, TimelineTrackKind kind)
    {
        var c = new TimelineClip { Id = id, Kind = kind };
        _clips.Add(c);
        return c;
    }

    /// <summary>클립 시작 이동(스냅 + 트랙 경계 클램프).</summary>
    public void MoveClip(string id, double newStartSec)
    {
        lock (_gate)
        {
            var c = _clips.FirstOrDefault(x => x.Id == id);
            if (c is null) return;
            c.StartSec = Math.Clamp(Snap(newStartSec), 0, Math.Max(0, TotalDurationSec - c.DurationSec));
        }
        Notify();
    }

    /// <summary>클립 길이 조정(스냅 + 최소/경계 클램프).</summary>
    public void ResizeClip(string id, double newDurationSec)
    {
        lock (_gate)
        {
            var c = _clips.FirstOrDefault(x => x.Id == id);
            if (c is null) return;
            c.DurationSec = Math.Clamp(Snap(newDurationSec), MinDurSec, TotalDurationSec - c.StartSec);
        }
        Notify();
    }

    public void RemoveClip(string id)
    {
        lock (_gate) _clips.RemoveAll(x => x.Id == id);
        Notify();
    }

    /// <summary>전 클립 제거(오토 디렉팅 재배치 전 초기화). 방송 억제 옵션(대량 재배치 시 마지막에 1회 방송).</summary>
    public void ClearAll(bool notify = true)
    {
        lock (_gate) _clips.Clear();
        if (notify) Notify();
    }

    /// <summary>플레이헤드 이동(0~총길이 클램프).</summary>
    public void SetPlayhead(double sec)
    {
        PlayheadSec = Math.Clamp(sec, 0, TotalDurationSec);
        Notify();
    }

    /// <summary>총 길이 설정(최소 5초 · 초 단위).</summary>
    public void SetTotalDuration(double sec)
    {
        TotalDurationSec = Math.Clamp(Math.Round(sec), 5, 600);
        Notify();
    }

    private void SeedDemo()
    {
        _clips.Add(new TimelineClip { Kind = TimelineTrackKind.Video, StartSec = 0, DurationSec = 5, Label = "컷 1 · 오프닝", Accent = "#22d3ee" });
        _clips.Add(new TimelineClip { Kind = TimelineTrackKind.Video, StartSec = 5, DurationSec = 5, Label = "컷 2 · 전개", Accent = "#22d3ee" });
        _clips.Add(new TimelineClip { Kind = TimelineTrackKind.Vocal, StartSec = 1, DurationSec = 8, Label = "보컬 립싱크", Accent = "#a855f7" });
        _clips.Add(new TimelineClip { Kind = TimelineTrackKind.Gyro, StartSec = 2, DurationSec = 4, Label = "자이로 팬업", Accent = "#34d399" });
        _clips.Add(new TimelineClip { Kind = TimelineTrackKind.Prompt, StartSec = 0, DurationSec = 10, Label = "시네마틱 톤", Accent = "#f59e0b" });
    }
}
