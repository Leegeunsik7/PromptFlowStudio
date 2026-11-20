using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 💬 시리즈 편 자막 레이어 상태(불변) — <see cref="BaseCues"/> = 크리에이터 소유 원문(생성 결과 + 수동 편집 · 에스컬레이션/“n/N편” 장식 없음).
/// <see cref="HookPinnedByUser"/> · <see cref="OutroPinnedByUser"/> = 사용자가 덧입혀진 훅/아웃트로 자리를 직접 고쳐 쓴 경우(이후 덧입힘이 그 자리를 다시 덮지 않음).
/// </summary>
public sealed record PlutoSubtitleOverlayState(IReadOnlyList<SubtitleCue> BaseCues, bool HookPinnedByUser, bool OutroPinnedByUser, int ManualEditCount)
{
    public static PlutoSubtitleOverlayState FromBase(IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var arr = new SubtitleCue[cues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = cues[i];
        return new PlutoSubtitleOverlayState(arr, false, false, 0);
    }

    public bool HasManualEdits => ManualEditCount > 0;
}

/// <summary>
/// 💬 비파괴 자막 합성(순수 함수 · 결정론) — 표시 자막 = 장식(n/N편) ∘ 덧입힘(궁금증 훅 · 클리프행어, 자동 각색일 때만) ∘ 원문.
///   ▸ 자동 각색 ↔ 추천만 표시 전환 · 새 시즌 분리(지시 None) = 같은 원문을 다시 합성할 뿐 → 재생성 없음 · 수동 편집 보존.
///   ▸ 표시 자막을 고치면 <see cref="ApplyEdit"/> 가 장식 · 덧입힘을 걷어내고 원문에 반영(덧입힌 자리를 고쳐 쓰면 그 자리는 사용자 고정).
///   ▸ 이중 언어 큐의 본문을 손으로 바꾸면 옛 영문 줄은 더 이상 번역이 아니므로 비운다(어긋난 번역 번인 방지).
/// </summary>
public static class PlutoSeriesSubtitleOverlay
{
    /// <summary>덧입힘에 실제로 쓸 지시(추천만 표시 · 사용자 고정 자리 제외) — 덧입힐 것이 없으면 null.</summary>
    public static PlutoSeriesContinuityDirective? EffectiveDirective(PlutoSubtitleOverlayState state, PlutoSeriesContinuityDirective? directive, PlutoSeriesEscalationMode mode)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (mode == PlutoSeriesEscalationMode.RecommendOnly || directive is null || !directive.EscalateHook) return null;
        var hook = state.HookPinnedByUser ? string.Empty : directive.RecommendedHookLine;
        var outro = state.OutroPinnedByUser ? string.Empty : directive.CliffhangerOutroLine;
        if (hook.Length == 0 && outro.Length == 0) return null;
        return hook == directive.RecommendedHookLine && outro == directive.CliffhangerOutroLine ? directive : directive with { RecommendedHookLine = hook, CliffhangerOutroLine = outro };
    }

    /// <summary>장식 이전 단계(원문 + 덧입힘).</summary>
    public static IReadOnlyList<SubtitleCue> Overlay(PlutoSubtitleOverlayState state, PlutoSeriesContinuityDirective? directive, PlutoSeriesEscalationMode mode)
    {
        ArgumentNullException.ThrowIfNull(state);
        var eff = EffectiveDirective(state, directive, mode);
        return eff is null ? state.BaseCues : PlutoSeriesArcMemoryService.ApplyDirective(state.BaseCues, eff);
    }

    /// <summary>💬 표시 자막 합성 — <paramref name="episode"/> ≤ 0 이면 장식 없음.</summary>
    public static IReadOnlyList<SubtitleCue> Compose(PlutoSubtitleOverlayState state, PlutoSeriesContinuityDirective? directive, PlutoSeriesEscalationMode mode, int episode, int total)
    {
        var overlaid = Overlay(state, directive, mode);
        return episode > 0 && total > 0 && overlaid.Count > 0 ? PlutoSeriesBatchPlanner.DecorateSubtitles(overlaid, episode, total) : overlaid;
    }

    /// <summary>표시 문구에서 “n/N편 · ” 접두(첫 큐) · 계속/완결 접미(마지막 큐) 제거.</summary>
    public static string StripDecoration(string? text, int index, int count, int episode, int total)
    {
        var s = text ?? string.Empty;
        if (episode <= 0 || total <= 0 || count <= 0) return s;
        if (index == 0)
        {
            var prefix = PlutoSeriesBatchPlanner.PartTag(episode, total) + " · ";
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        }
        if (index == count - 1)
        {
            var suffix = episode < total ? PlutoSeriesBatchPlanner.ContinueSuffix : PlutoSeriesBatchPlanner.FinaleSuffix;
            if (s.EndsWith(suffix, StringComparison.Ordinal)) s = s[..^suffix.Length];
        }
        return s;
    }

    /// <summary>
    /// ✏️ 표시 자막 1줄 수동 편집 → 원문 반영(범위 밖 · 변화 없음 → 같은 상태 인스턴스).
    /// 덧입힌 훅 자리를 다른 문구로 고치면 훅 고정 · 아웃트로 꼬리를 지우거나 바꾸면 아웃트로 고정 — 그 자리에는 다시 덧입히지 않는다.
    /// </summary>
    public static PlutoSubtitleOverlayState ApplyEdit(PlutoSubtitleOverlayState state, PlutoSeriesContinuityDirective? directive, PlutoSeriesEscalationMode mode,
        int episode, int total, int index, string? displayedText)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = state.BaseCues.Count;
        if (index < 0 || index >= count) return state;
        var shown = Compose(state, directive, mode, episode, total);
        var text = displayedText ?? string.Empty;
        if (string.Equals(text, shown[index].Text, StringComparison.Ordinal)) return state;

        var core = StripDecoration(text, index, count, episode, total);
        var eff = EffectiveDirective(state, directive, mode);
        var baseText = state.BaseCues[index].Text ?? string.Empty;
        bool hookPin = state.HookPinnedByUser, outroPin = state.OutroPinnedByUser;
        var last = count - 1;

        // 아웃트로 꼬리(마지막 큐)
        if (index == last && eff is { CliffhangerOutroLine.Length: > 0 } eo)
        {
            var outro = eo.CliffhangerOutroLine;
            var suffix = PlutoSeriesArcMemoryService.OutroJoiner + outro;
            var baseBlank = !(index == 0 && eff.RecommendedHookLine.Length > 0) && string.IsNullOrWhiteSpace(baseText);   // 빈 원문 → ApplyDirective 는 접합 없이 아웃트로만
            if (baseBlank && string.Equals(core, outro, StringComparison.Ordinal)) core = baseText;          // 빈 원문 + 아웃트로 그대로 → 원문 불변
            else if (core.EndsWith(suffix, StringComparison.Ordinal)) core = core[..^suffix.Length];
            else outroPin = true;                                                                            // 꼬리를 지우거나 바꿈 → 사용자 고정
        }
        // 훅 자리(첫 큐)
        if (index == 0 && eff is { RecommendedHookLine.Length: > 0 } eh)
        {
            if (string.Equals(core, eh.RecommendedHookLine, StringComparison.Ordinal)) core = baseText;      // 훅 그대로 → 원문 불변
            else hookPin = true;                                                                             // 훅을 고쳐 씀 → 사용자 문구가 원문
        }

        var arr = new SubtitleCue[count];
        for (var i = 0; i < count; i++) arr[i] = state.BaseCues[i];
        if (!string.Equals(core, baseText, StringComparison.Ordinal))
            arr[index] = arr[index] with { Text = core, SecondaryText = null };   // 🌐 옛 영문 줄은 이제 번역이 아니다
        var changed = !string.Equals(core, baseText, StringComparison.Ordinal) || hookPin != state.HookPinnedByUser || outroPin != state.OutroPinnedByUser;
        return changed ? new PlutoSubtitleOverlayState(arr, hookPin, outroPin, state.ManualEditCount + 1) : state;
    }

    /// <summary>
    /// ⏱️ 원문 문구 · 고정 상태는 그대로 두고 구간만 새 컷 경계(<paramref name="freshCues"/>)로 옮김 — 앵커 BGM 교체로 비트가 바뀌어 프리뷰를 다시 만들 때
    /// 수동 편집을 잃지 않기 위함. 큐 수가 다르면 대응 불가 → null(호출자는 새 생성 결과를 원문으로 채택).
    /// </summary>
    public static PlutoSubtitleOverlayState? RetimeOnto(PlutoSubtitleOverlayState state, IReadOnlyList<SubtitleCue> freshCues)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(freshCues);
        if (freshCues.Count != state.BaseCues.Count || freshCues.Count == 0) return null;
        var arr = new SubtitleCue[freshCues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = freshCues[i] with { Text = state.BaseCues[i].Text, SecondaryText = state.BaseCues[i].SecondaryText };
        return state with { BaseCues = arr };
    }

    /// <summary>✏️ 시리즈가 아닌 자막의 1줄 수동 편집(범위 밖 · 변화 없음 → 같은 인스턴스) — 본문이 바뀌면 옛 영문 줄을 비운다.</summary>
    public static IReadOnlyList<SubtitleCue> EditPlain(IReadOnlyList<SubtitleCue> cues, int index, string? text)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (index < 0 || index >= cues.Count) return cues;
        var t = text ?? string.Empty;
        if (string.Equals(t, cues[index].Text, StringComparison.Ordinal)) return cues;
        var arr = new SubtitleCue[cues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = cues[i];
        arr[index] = arr[index] with { Text = t, SecondaryText = null };
        return arr;
    }
}
