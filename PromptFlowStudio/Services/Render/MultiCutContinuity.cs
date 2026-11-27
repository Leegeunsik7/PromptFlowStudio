using System;
using System.Collections.Generic;
using System.Globalization;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Render;

/// <summary>컷 간 공유 시각 앵커 — 피사체 · 의상 · 장소(각각 없으면 null).</summary>
public readonly record struct MultiCutAnchor(string? Subject, string? Wardrobe, string? Setting)
{
    public bool IsEmpty => Subject is null && Wardrobe is null && Setting is null;
}

/// <summary>컷 단위 QC 상태 — 합본 전 컷별 배지.</summary>
public enum CutQcState : byte { Waiting, Rendering, Healing, Passed, SelfHealed, Degraded, Failed }

/// <summary>합본 그룹 내 컷 1건의 상태 스냅샷.</summary>
public readonly record struct CutStatus(int CutIndex, Guid JobId, CutQcState State, string? Diagnosis, int Regenerations);

/// <summary>
/// 🎞️ 멀티 컷(4컷 성장 일기 · 15초 릴스) 연속성 — 컷 1 피사체/의상/장소 앵커를 전 컷에 전파하고 🧠 플루토 학습 앵커를 공통 부착,
/// 하드 컷 전환을 기본으로 기록. 렌더 큐 스냅샷에서 컷별 QC 상태를 산출하고 결함 컷 1건만 재생성(전체 재렌더 없음).
/// 순수 BCL · 무상태 · throw 없음(인자 null 허용).
/// </summary>
public static class MultiCutContinuity
{
    public const string SubjectPrefix = "same subject as cut 1: ";
    public const string WardrobePrefix = "same wardrobe as cut 1: ";
    public const string SettingPrefix = "same setting as cut 1: ";
    /// <summary>컷 공통 학습 앵커 상한 — 프롬프트 비대화 방지.</summary>
    public const int MaxLearnedAnchors = 2;
    /// <summary>앵커 절 최대 길이(문자).</summary>
    public const int MaxClauseLength = 80;
    /// <summary>재생성 횟수 메타.</summary>
    public const string MetaRegenCount = "cut.regen";
    /// <summary>재생성 시드 간격 — 원 시드 계열(MasterSeed + i)과 겹치지 않게.</summary>
    public const long RegenSeedStride = 7919;

    private static readonly string[] WardrobeKeys =
    {
        "wearing", "outfit", "dress", "shirt", "jacket", "raincoat", "coat", "uniform", "hoodie", "sweater", "onesie", "romper",
        "hanbok", "scarf", "bib", "apron",
        "옷", "의상", "원피스", "한복", "모자", "우비", "턱받이", "배냇저고리", "유니폼", "셔츠", "드레스", "점퍼", "니트", "내복",
    };

    private static readonly string[] SettingKeys =
    {
        "room", "nursery", "kitchen", "garden", "park", "beach", "studio", "background", "backdrop", "indoors", "outdoors", "street",
        "cafe", "forest", "meadow", "bedroom", "playground",
        "배경", "거실", "공원", "아기방", "실내", "야외", "해변", "스튜디오", "정원", "부엌", "놀이터", "숲", "카페", "침실",
    };

    /// <summary>
    /// 컷 1 프롬프트에서 의상 · 장소 절을 추출(피사체는 호출자가 정함). 의상 우선 — 같은 절은 장소로 중복 사용하지 않음.
    /// 피사체 문구에 이미 들어 있는 절은 생략(중복 부착 방지).
    /// </summary>
    public static MultiCutAnchor Extract(string? firstPrompt, string? subject)
    {
        var subj = string.IsNullOrWhiteSpace(subject) ? null : Clip(subject.Trim());
        string? wardrobe = null, setting = null;
        var clauses = MultiDurationPlanner.SplitClauses(firstPrompt, true, out _, alwaysComma: true);
        for (var i = 0; i < clauses.Count; i++)
        {
            var c = clauses[i];
            if (subj is not null && subj.Contains(c, StringComparison.OrdinalIgnoreCase)) continue;
            if (wardrobe is null && ContainsAny(c, WardrobeKeys)) { wardrobe = Clip(c); continue; }
            if (setting is null && ContainsAny(c, SettingKeys)) setting = Clip(c);
        }
        return new MultiCutAnchor(subj, wardrobe, setting);
    }

    /// <summary>
    /// 컷 프롬프트에 공유 앵커 부착(멱등 · 대소문자 무시 포함 검사). 학습 앵커는 전 컷 공통(최대 <see cref="MaxLearnedAnchors"/>),
    /// 의상/장소는 컷 2부터, 피사체 구문은 항상 마지막(기존 계약: <c>…, same subject as cut 1: X</c>).
    /// </summary>
    public static string Apply(string? prompt, MultiCutAnchor anchor, int cutIndex, PlutoLearnedGuidance? guidance = null)
    {
        var p = (prompt ?? "").Trim().TrimEnd(',', ' ');
        if (guidance is { } g)
        {
            var n = 0;
            for (var i = 0; i < g.PromptAnchors.Count && n < MaxLearnedAnchors; i++)
            {
                var a = g.PromptAnchors[i];
                if (string.IsNullOrWhiteSpace(a)) continue;
                n++;
                p = AppendOnce(p, a.Trim(), null);
            }
        }
        if (cutIndex <= 1) return p;
        if (anchor.Wardrobe is { } w && !ContainsIc(anchor.Subject, w)) p = AppendOnce(p, w, WardrobePrefix);
        if (anchor.Setting is { } s && !ContainsIc(anchor.Subject, s)) p = AppendOnce(p, s, SettingPrefix);
        if (anchor.Subject is { } subj) p = AppendOnce(p, subj, SubjectPrefix);
        return p;
    }

    /// <summary>작업 메타에 하드 컷 전환을 명시(기존 키 보존).</summary>
    public static Dictionary<string, string> WithHardCut(IReadOnlyDictionary<string, string>? meta)
        => MultiDurationPlanner.WithTransition(meta, CutTransitionMode.IndependentHardCut);

    /// <summary>작업 1건 → 컷 QC 상태.</summary>
    public static CutQcState StateOf(BatchRenderJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var healed = MultiDurationPlanner.MetaOf(job, RenderQueueService.MetaQcSelfHealed) == "true";
        return job.Status switch
        {
            BatchJobStatus.Processing => CutQcState.Rendering,
            BatchJobStatus.Queued or BatchJobStatus.Pending => healed ? CutQcState.Healing : CutQcState.Waiting,
            BatchJobStatus.Completed => MultiDurationPlanner.MetaOf(job, RenderQueueService.MetaQcDegraded) is { Length: > 0 } ? CutQcState.Degraded
                                      : healed ? CutQcState.SelfHealed : CutQcState.Passed,
            _ => CutQcState.Failed,
        };
    }

    /// <summary>합본 그룹의 컷별 상태(CutIndex 오름차순). 그룹 없음 → 빈 목록.</summary>
    public static List<CutStatus> GroupStatuses(IReadOnlyList<BatchRenderJob>? jobs, string? groupId)
    {
        var list = new List<CutStatus>(4);
        if (jobs is null || string.IsNullOrEmpty(groupId)) return list;
        for (var i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (!InGroup(j, groupId)) continue;
            var regen = int.TryParse(MultiDurationPlanner.MetaOf(j, MetaRegenCount), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
            var state = StateOf(j);
            var diag = state is CutQcState.Failed ? j.ErrorMessage : MultiDurationPlanner.MetaOf(j, RenderQueueService.MetaQcDiagnosis);
            list.Add(new CutStatus(j.CutIndex ?? list.Count + 1, j.JobId, state, diag, regen));
        }
        list.Sort(static (a, b) => a.CutIndex.CompareTo(b.CutIndex));
        return list;
    }

    /// <summary>
    /// 그룹 컷 상태 지문(무할당) — 컷 상태/재생성 횟수가 바뀔 때만 값이 바뀜. 진행률 틱에는 불변 → UI 렌더 폭주 방지.
    /// </summary>
    public static int StatusSignature(IReadOnlyList<BatchRenderJob>? jobs, string? groupId)
    {
        if (jobs is null || string.IsNullOrEmpty(groupId)) return 0;
        var h = 17;
        for (var i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (!InGroup(j, groupId)) continue;
            h = unchecked(h * 31 + (j.CutIndex ?? 0));
            h = unchecked(h * 31 + (int)StateOf(j));
            h = unchecked(h * 31 + (int)(j.Seed & 0x7FFFFFFF));
        }
        return h;
    }

    /// <summary>그룹에 대기/진행 중 컷이 있는지 — 중복 전송 · 합본 차단용.</summary>
    public static bool IsGroupInFlight(IReadOnlyList<BatchRenderJob>? jobs, string? groupId)
    {
        if (jobs is null || string.IsNullOrEmpty(groupId)) return false;
        for (var i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (InGroup(j, groupId) && j.Status is BatchJobStatus.Queued or BatchJobStatus.Pending or BatchJobStatus.Processing) return true;
        }
        return false;
    }

    /// <summary>재생성 가능 — 끝난 컷(통과·복구·경고·실패)만. 대기/진행 중은 불가.</summary>
    public static bool CanRegenerate(CutQcState s) => s is CutQcState.Passed or CutQcState.SelfHealed or CutQcState.Degraded or CutQcState.Failed;

    /// <summary>재생성 시드 — 원 시드에서 결정적으로 이동(시도마다 다름 · 양수 유지).</summary>
    public static long RegenSeed(long seed, int attempt)
    {
        var v = unchecked(seed + RegenSeedStride * Math.Max(1, attempt)) & int.MaxValue;
        return v == 0 ? 1 : v;
    }

    /// <summary>
    /// 결함 컷 1건만 재렌더 — 그룹/CutIndex 로 작업을 찾아 새 시드로 큐에 재적재(같은 작업 · 같은 그룹 → 합본 순서 유지).
    /// 대상 없음/진행 중/큐 미지원이면 false.
    /// </summary>
    public static bool RegenerateCut(IRenderQueueService? queue, string? groupId, int cutIndex)
    {
        if (queue is null || string.IsNullOrEmpty(groupId)) return false;
        var snap = queue.Snapshot();
        for (var i = 0; i < snap.Length; i++)
        {
            var j = snap[i];
            if (!InGroup(j, groupId) || j.CutIndex != cutIndex) continue;
            if (!CanRegenerate(StateOf(j))) return false;
            var attempt = (int.TryParse(MultiDurationPlanner.MetaOf(j, MetaRegenCount), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0) + 1;
            return queue.RegenerateJob(j.JobId, RegenSeed(j.Seed, attempt));
        }
        return false;
    }

    /// <summary>배지 문구.</summary>
    public static string Label(CutQcState s) => s switch
    {
        CutQcState.Waiting => "⏳ 대기",
        CutQcState.Rendering => "🎬 렌더 중",
        CutQcState.Healing => "🩺 자가 복구 중",
        CutQcState.Passed => "✅ QC 통과",
        CutQcState.SelfHealed => "🩺 복구 완료",
        CutQcState.Degraded => "⚠ QC 경고",
        _ => "⛔ 실패",
    };

    /// <summary>배지 CSS 클래스(ok · warn · bad · run).</summary>
    public static string Css(CutQcState s) => s switch
    {
        CutQcState.Passed or CutQcState.SelfHealed => "ok",
        CutQcState.Degraded => "warn",
        CutQcState.Failed => "bad",
        _ => "run",
    };

    private static bool InGroup(BatchRenderJob j, string groupId)
        => string.Equals(MultiDurationPlanner.MetaOf(j, FFmpegVideoStitchService.MetaGroup), groupId, StringComparison.Ordinal);   // GroupProgress 와 동일 판정

    private static string AppendOnce(string p, string text, string? prefix)
    {
        if (p.Contains(text, StringComparison.OrdinalIgnoreCase)) return p;
        var piece = prefix is null ? text : prefix + text;
        return p.Length == 0 ? piece : p + ", " + piece;
    }

    private static bool ContainsIc(string? hay, string needle) => hay is not null && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, string[] keys)
    {
        for (var i = 0; i < keys.Length; i++) if (text.Contains(keys[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Clip(string s) => s.Length <= MaxClauseLength ? s : s[..MaxClauseLength].TrimEnd();
}
