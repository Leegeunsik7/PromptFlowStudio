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

/// <summary>🌐 Step 32 — 로캘 투영 큐 1건의 자막 · TTS 적합도(원문 큐 인덱스 기준).</summary>
/// <param name="Index">원문(편집 대상) 큐 인덱스.</param>
/// <param name="LineCount">로캘 줄바꿈 후 줄 수.</param>
/// <param name="ExceedsSafeZone">하단 안전 영역(2줄) 초과 → 렌더 시 시간 비례 분할.</param>
/// <param name="SegmentSeconds">큐 구간(초).</param>
/// <param name="Tts">낭독 예산(로캘 cps · 0.85x~1.30x).</param>
/// <param name="Untranslated">로캘이 한국어가 아닌데 본문이 한글(번역 누락).</param>
public sealed record PlutoLocalizedCueFit(int Index, int LineCount, bool ExceedsSafeZone, double SegmentSeconds, PlutoTtsBudget Tts, bool Untranslated)
{
    /// <summary>1.30x 로도 장면 안에 끝나지 않을 때 줄여야 할 정확한 낭독 글자 수(0 = 적합).</summary>
    public int ExcessChars => Tts.RequiresCondensation ? Math.Max(1, Tts.SpeechUnits - Tts.MaxUnitsAtMaxRate) : 0;
}

/// <summary>🌐 Step 32 — 렌더/낭독용 로캘 투영(편집 원문은 불변 · 렌더 직전에만 적용).</summary>
public sealed record PlutoLocalizedSubtitleProjection(string Locale, IReadOnlyList<SubtitleCue> Cues, IReadOnlyList<PlutoLocalizedCueFit> Fits, string VoiceId, double SpeakingRate)
{
    /// <summary>기본 로캘(ko-KR) · 미등록 로캘 → 원문 그대로.</summary>
    public bool IsIdentity => Fits.Count == 0;
    public int TtsOverflowCueCount { get { var n = 0; foreach (var f in Fits) if (f.ExcessChars > 0) n++; return n; } }
    public int TotalExcessChars { get { var n = 0; foreach (var f in Fits) n += f.ExcessChars; return n; } }
    public int SafeZoneSplitCount { get { var n = 0; foreach (var f in Fits) if (f.ExceedsSafeZone) n++; return n; } }
    public int UntranslatedCueCount { get { var n = 0; foreach (var f in Fits) if (f.Untranslated) n++; return n; } }
    public bool HasTtsOverflow => TtsOverflowCueCount > 0;

    /// <summary>📖 Step 34 — 비파괴 로캘 번역 트랙이 본문으로 쓰였다.</summary>
    public bool UsedTranslationTrack { get; init; }

    /// <summary>🕰️ Step 34 — 같은 로캘 트랙이 있었지만 원문(본문 · 구간)이 번역 이후 바뀌어 쓰지 않았다(다시 번역 필요).</summary>
    public bool StaleTranslationTrack { get; init; }

    /// <summary>⚠️ 간결 경고 1줄(없음 → 빈 문자열) — 최악 컷 · 줄일 글자 수 정확 표기.</summary>
    public string WarningKorean
    {
        get
        {
            if (IsIdentity) return string.Empty;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(96);
            if (HasTtsOverflow)
            {
                PlutoLocalizedCueFit? worst = null;
                foreach (var f in Fits) if (f.ExcessChars > 0 && (worst is null || f.ExcessChars > worst.ExcessChars)) worst = f;
                sb.Append("🎙️ ").Append(Locale).Append(" TTS 1.30x 초과 ").Append(TtsOverflowCueCount.ToString(inv)).Append("컷 · 렌더 전 ")
                  .Append(TotalExcessChars.ToString(inv)).Append("자 줄이기(#").Append((worst!.Index + 1).ToString(inv)).Append(' ')
                  .Append(worst.ExcessChars.ToString(inv)).Append("자)");
            }
            if (UntranslatedCueCount > 0)
                sb.Append(sb.Length > 0 ? " · " : "").Append("🌐 번역 안 된 한글 자막 ").Append(UntranslatedCueCount.ToString(inv)).Append("컷")
                  .Append(StaleTranslationTrack ? " (원문 수정됨 — 다시 번역)" : "");
            return sb.ToString();
        }
    }
}

/// <summary>🌐 Step 32 — 로캘 낭독 계획(음성 · 적합 속도 · 예산).</summary>
public sealed record PlutoLocalizedNarrationPlan(string Locale, string VoiceId, double SpeakingRate, PlutoTtsBudget Budget)
{
    /// <summary>1.30x 초과 시 줄일 글자 수(0 = 적합).</summary>
    public int ExcessChars => Budget.RequiresCondensation ? Math.Max(1, Budget.SpeechUnits - Budget.MaxUnitsAtMaxRate) : 0;

    /// <summary>TTS 엔진 운율(Edge rate % · ElevenLabs speed ≤ 1.2 상한).</summary>
    public PromptFlowStudio.Services.Media.NarrationProsody ToProsody()
    {
        var pct = (int)Math.Round((SpeakingRate - 1.0) * 100.0, MidpointRounding.AwayFromZero);
        var rate = (pct >= 0 ? "+" : "") + pct.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        return new PromptFlowStudio.Services.Media.NarrationProsody(VoiceId, rate, "+0Hz", Math.Round(Math.Clamp(SpeakingRate, 0.7, 1.2), 2), 0.55, 0.75, 0.45);
    }
}

/// <summary>
/// 🌐 Step 32 — 런타임 로캘 자막/TTS 적합기(순수 · 결정론). 편집 원문(<see cref="PlutoSubtitleOverlayState"/>)은 절대 바꾸지 않고 렌더 직전 투영만 만든다
/// → 수동 편집 인덱스 · 재타이밍(<see cref="PlutoSeriesSubtitleOverlay.RetimeOnto"/>) 불변식 유지.
///   ▸ 한국어 편 장식(<c>1/3편 · </c> · <c>(다음 편에 계속)</c>)을 걷어내고 로캘 편 토큰(<c>第1/3話 · </c>) · 로캘 꼬리로 재장식.
///   ▸ 로캘 <see cref="PlutoChannelLocaleProfile.MaxCharsPerLine"/> · 금칙/문자소 줄바꿈 → 줄은 <c>\n</c> 결합 · 2줄 초과 큐는 문자소 비례로 구간 분할(전체 구간 불변).
///   ▸ 낭독 예산은 장식 없는 본문 · 큐 정밀 구간 기준(로캘 음성 · 0.85x~1.30x · 초과 글자 수 정확 보고).
/// </summary>
public static class PlutoLocaleSubtitleFitter
{
    private static readonly (string Locale, string Continue, string Finale)[] Tails =
    {
        ("en-US", " (to be continued)", " (finale)"),
        ("ja-JP", "（次回へ続く）", "（完結）"),
        ("es-ES", " (continuará)", " (final)"),
    };

    /// <summary>로캘 시리즈 꼬리(계속/완결) — 사용자 정의 로캘 → 영어.</summary>
    public static string SeriesTail(string? locale, bool finale)
    {
        var norm = PlutoMultiLanguageChannelService.NormalizeLocale(locale);
        foreach (var t in Tails) if (string.Equals(t.Locale, norm, StringComparison.Ordinal)) return finale ? t.Finale : t.Continue;
        return finale ? Tails[0].Finale : Tails[0].Continue;
    }

    /// <summary>기본 로캘(ko-KR) · 형식 위반 · 미등록 → null(투영 불필요).</summary>
    public static PlutoChannelLocaleProfile? ActiveProfile(IPlutoMultiLanguageChannelService? channels, string? locale)
    {
        if (channels is null) return null;
        var norm = PlutoMultiLanguageChannelService.NormalizeLocale(locale);
        if (norm is null || string.Equals(norm, PlutoMultiLanguageChannelService.DefaultLocale, StringComparison.Ordinal)) return null;
        return channels.TryGetProfile(norm, out var p) ? p : null;
    }

    /// <summary>🎞️ 표시 자막(장식 포함 · <see cref="PlutoSeriesSubtitleOverlay.Compose"/> 결과) → 로캘 렌더 투영.</summary>
    public static PlutoLocalizedSubtitleProjection Project(IReadOnlyList<SubtitleCue> displayed, IPlutoMultiLanguageChannelService? channels, string? locale, int episode = 0, int total = 0)
        => Project(displayed, channels, locale, episode, total, null);

    /// <summary>
    /// 📖 Step 34 — 비파괴 번역 트랙 적용 투영: 트랙 로캘 = 대상 로캘 ∧ 원문 지문 일치일 때만 본문을 번역문으로 대체(원문 · 편집 인덱스 불변).
    /// 낡은 트랙(원문 수정 · 재타이밍)은 쓰지 않는다 → 한글 잔존 경고가 정직하게 되살아남.
    /// </summary>
    public static PlutoLocalizedSubtitleProjection Project(IReadOnlyList<SubtitleCue> displayed, IPlutoMultiLanguageChannelService? channels, string? locale, int episode, int total, PlutoLocaleSubtitleTrack? track)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        var p = ActiveProfile(channels, locale);
        if (p is null || displayed.Count == 0)
        {
            var dp = PlutoMultiLanguageChannelService.BuiltInProfiles[0];
            return new PlutoLocalizedSubtitleProjection(p?.LocaleCode ?? dp.LocaleCode, displayed, Array.Empty<PlutoLocalizedCueFit>(), p?.DefaultTtsVoiceId ?? dp.DefaultTtsVoiceId, dp.BaseSpeakingRate);
        }
        var loc = p.LocaleCode;
        var count = displayed.Count;
        var series = episode > 0 && total > 0 && episode <= total;
        var token = series ? channels!.FormatEpisodeToken(loc, episode, total) : string.Empty;
        var cues = new List<SubtitleCue>(count + 4);
        var fits = new PlutoLocalizedCueFit[count];
        var rate = Math.Clamp(p.BaseSpeakingRate, PlutoMultiLanguageChannelService.MinSpeakingRate, PlutoMultiLanguageChannelService.MaxSpeakingRate);
        var sameLocale = track is not null && string.Equals(track.Locale, loc, StringComparison.Ordinal);
        var useTrack = sameLocale && track!.IsFreshFor(displayed, episode, total);
        for (var i = 0; i < count; i++)
        {
            var c = displayed[i];
            var core = PlutoSeriesSubtitleOverlay.StripDecoration(c.Text, i, count, episode, total);
            var secondary = c.SecondaryText;
            if (useTrack && track!.Texts[i] is { Length: > 0 } translated && !string.Equals(translated, core, StringComparison.Ordinal))
            {
                core = translated;
                secondary = null;   // 한국어/영어 보조 줄은 번역 본문과 어긋남 → 번인하지 않음
            }
            // 🌐 이중 언어 큐 승격 — en-US 채널인데 본문이 한글이고 영문 보조 줄이 있으면 영문을 주 자막으로(보조 줄의 Part/꼬리 장식은 제거)
            if (string.Equals(loc, "en-US", StringComparison.Ordinal) && PlutoYouTubePublishPackService.ContainsHangul(core) && !string.IsNullOrWhiteSpace(secondary))
            {
                core = StripEnglishDecoration(secondary, i, count, episode, total);
                secondary = null;
            }
            var untranslated = PlutoYouTubePublishPackService.ContainsHangul(core);
            var shown = core;
            if (series && i == 0 && token.Length > 0 && core.Length > 0) shown = token + " · " + shown;
            if (series && i == count - 1 && shown.Length > 0) shown += SeriesTail(loc, episode >= total);
            var wrap = channels!.WrapSubtitle(loc, shown);
            var seg = c.EffectiveEndSec - c.EffectiveStartSec;
            var tts = channels.ComputeTtsBudget(loc, core, seg);
            if (tts.SpeechUnits > 0 && tts.SpeakingRate > rate) rate = tts.SpeakingRate;
            fits[i] = new PlutoLocalizedCueFit(i, wrap.Lines.Count, wrap.ExceedsSafeZone, seg > 0 ? seg : 0, tts, untranslated);
            AppendWrapped(cues, c, wrap.Lines, secondary);
        }
        return new PlutoLocalizedSubtitleProjection(loc, cues, fits, p.DefaultTtsVoiceId, Math.Round(rate, 3, MidpointRounding.AwayFromZero))
        {
            UsedTranslationTrack = useTrack,
            StaleTranslationTrack = sameLocale && !useTrack,
        };
    }

    /// <summary>🎙️ 단일 낭독(립싱크 대사 등) 계획 — 구간 미상(≤0 · 비유한) → 로캘 기본 속도 · 초과 없음. 기본 로캘 → null.</summary>
    public static PlutoLocalizedNarrationPlan? PlanNarration(IPlutoMultiLanguageChannelService? channels, string? locale, string? narration, double segmentSeconds)
    {
        var p = ActiveProfile(channels, locale);
        if (p is null) return null;
        PlutoTtsBudget budget;
        if (!double.IsFinite(segmentSeconds) || segmentSeconds <= 0)
        {
            var baseRate = Math.Clamp(p.BaseSpeakingRate, PlutoMultiLanguageChannelService.MinSpeakingRate, PlutoMultiLanguageChannelService.MaxSpeakingRate);
            var units = PlutoMultiLanguageChannelService.CountSpeechUnits(narration);
            budget = new PlutoTtsBudget(baseRate, baseRate, units, 0, units / p.TargetCharsPerSecond / baseRate, 0, int.MaxValue);
        }
        else budget = channels!.ComputeTtsBudget(p.LocaleCode, narration, segmentSeconds);
        return new PlutoLocalizedNarrationPlan(p.LocaleCode, p.DefaultTtsVoiceId, budget.SpeakingRate, budget);
    }

    private static string StripEnglishDecoration(string text, int index, int count, int episode, int total)
    {
        var s = text;
        if (episode <= 0 || total <= 0) return s;
        if (index == 0)
        {
            var prefix = "Part " + episode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · ";
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        }
        if (index == count - 1)
        {
            if (s.EndsWith(" (to be continued)", StringComparison.Ordinal)) s = s[..^" (to be continued)".Length];
            else if (s.EndsWith(" (finale)", StringComparison.Ordinal)) s = s[..^" (finale)".Length];
        }
        return s;
    }

    /// <summary>줄 → 큐(2줄 단위 · 초과분은 문자소 비례 시간 분할 · 첫 시작/마지막 끝 정확 보존).</summary>
    private static void AppendWrapped(List<SubtitleCue> output, SubtitleCue source, IReadOnlyList<string> lines, string? secondary)
    {
        var max = PlutoMultiLanguageChannelService.MaxSafeZoneLines;
        if (lines.Count <= max)
        {
            output.Add(source with { Text = lines.Count == 0 ? string.Empty : string.Join("\n", lines), SecondaryText = secondary });
            return;
        }
        var start = source.EffectiveStartSec;
        var end = source.EffectiveEndSec;
        var span = end > start ? end - start : 0.0;
        var totalUnits = 0;
        foreach (var l in lines) totalUnits += Math.Max(1, PlutoMultiLanguageChannelService.TextLength(l));
        var acc = 0;
        for (var i = 0; i < lines.Count; i += max)
        {
            var n = Math.Min(max, lines.Count - i);
            var units = 0;
            for (var j = i; j < i + n; j++) units += Math.Max(1, PlutoMultiLanguageChannelService.TextLength(lines[j]));
            var s = start + span * acc / totalUnits;
            acc += units;
            var e = i + n >= lines.Count ? end : start + span * acc / totalUnits;
            var text = n == 1 ? lines[i] : lines[i] + "\n" + lines[i + 1];
            output.Add(SubtitleCue.Precise(s, e, text, i == 0 ? secondary : null));
        }
    }
}
