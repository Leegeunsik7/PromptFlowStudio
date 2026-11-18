using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>⚖️ A/B 실험 변형 — 훅/룩/BGM + 프롬프트 접미 + 3막 자막(성과 텔레메트리 HookStyle/ColorLookId/BgmMood와 동일 키).</summary>
public sealed record PlutoExperimentVariant(string VariantLabel, string HookStyle, string ColorLookId, string BgmMood, string PromptSuffix, IReadOnlyList<SubtitleCue> Subtitles);

/// <summary>⚖️ 챔피언(A) vs 챌린저(B) 실험 플랜 — 공유 <see cref="ExperimentId"/>.</summary>
public sealed record PlutoVariantABPlan(string ExperimentId, string ThemeHint, PlutoExperimentVariant ChampionA, PlutoExperimentVariant ChallengerB, string HypothesisSummary)
{
    /// <summary>유효 실험(훅 또는 룩이 다름).</summary>
    public bool IsValidExperiment => !string.Equals(ChampionA.HookStyle, ChallengerB.HookStyle, StringComparison.Ordinal)
                                     || !string.Equals(ChampionA.ColorLookId, ChallengerB.ColorLookId, StringComparison.Ordinal);
    /// <summary>🎰 챌린저 훅/룩 중 최대 UCB1 탐색 보너스(0 → 탐색 이력 없음 · 활용 차순위).</summary>
    public double ChallengerExplorationBonus { get; init; }
    /// <summary>🎰 챌린저가 덜 검증된 후보(탐색 보너스 &gt; 0)로 배정됨.</summary>
    public bool IsExplorationChallenger => ChallengerExplorationBonus > PlutoAdaptivePatternRanker.Epsilon;
    /// <summary>🔗 챔피언 훅 × 룩 조합 시너지 점수(이력 없음 → 1.0).</summary>
    public double ChampionSynergyScore { get; init; } = 1.0;
    /// <summary>🔗 챌린저 훅 × 룩 조합 시너지 점수(이력 없음 → 1.0).</summary>
    public double ChallengerSynergyScore { get; init; } = 1.0;
}

/// <summary>⚖️ Pluto 4.3 통제 A/B 변형 생성기.</summary>
public interface IPlutoVariantABGenerator
{
    /// <summary>
    /// ⚖️ 챔피언 A = 랭커 1위 훅·룩·BGM(강한 조합 시너지 반영) · 챌린저 B = 오답 노트 감점 제외 UCB1 최상위(덜 검증된) 탐색 후보 · 역시너지 조합 회피.
    /// 몽타주 플랜 제공 시 양 변형 자막은 동일 컷 구간(<see cref="PlutoMontageStoryGenerator.ComputeCueWindows"/>)에 정렬 · Act 1은 각 변형 훅.
    /// 호출자 취소만 전파 · 그 외 실패 → 결정론 템플릿.
    /// </summary>
    Task<PlutoVariantABPlan> BuildVariantPairAsync(string themeHint, PlutoMontagePlan? montagePlan = null, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts = null,
        CancellationToken ct = default);
}

/// <summary>⚖️ <see cref="IPlutoVariantABGenerator"/> 구현 — 훅 또는 룩 차이 보장(불변식 <see cref="PlutoVariantABPlan.IsValidExperiment"/>).</summary>
public sealed class PlutoVariantABGenerator : IPlutoVariantABGenerator
{
    public const string ChampionLabel = "A · 챔피언";
    public const string ChallengerLabel = "B · 챌린저";
    public const string ChallengerThemeSuffix = " · alternate angle";

    /// <summary>플랜 없는 실험용 명목 3막 구간(15초 타임라인).</summary>
    internal static readonly (double Start, double End)[] NominalWindows = { (0.20, 4.95), (5.05, 9.95), (10.05, 14.70) };

    private static readonly string[] ExcludedBgmMoods = { "custom", "none" };
    /// <summary>🔗 챔피언 조합 탐색 시 고려할 상위 훅 수.</summary>
    public const int SynergyHookDepth = 4;

    private readonly IPlutoAdaptivePatternRanker? _ranker;
    private readonly IPlutoMontageStoryGenerator? _story;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly Func<string> _idFactory;

    public PlutoVariantABGenerator(IPlutoAdaptivePatternRanker? ranker = null, IPlutoMontageStoryGenerator? story = null, IPlutoMistakeNotebookService? notebook = null,
        Func<string>? experimentIdFactory = null)
    {
        _ranker = ranker;
        _story = story;
        _notebook = notebook;
        _idFactory = experimentIdFactory ?? (static () => "exp-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
    }

    public async Task<PlutoVariantABPlan> BuildVariantPairAsync(string themeHint, PlutoMontagePlan? montagePlan = null, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        themeHint = string.IsNullOrWhiteSpace(themeHint) ? "" : themeHint.Trim();
        var ranker = _ranker ?? new PlutoAdaptivePatternRanker(null, _notebook);
        try { await ranker.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] ranker load: " + ex.Message); }
        var suppressed = SafeSuppressed();

        // 🏆 챔피언 — 1위 훅·룩·BGM
        PlutoAdaptiveRecommendation? rec = null;
        IReadOnlyList<PlutoRankedOption> hooks = Array.Empty<PlutoRankedOption>(), looks = hooks, bgms = hooks;
        try
        {
            rec = ranker.GetCurrentRecommendations();
            hooks = ranker.RankOptions(PlutoPatternKind.Hook);
            looks = ranker.RankOptions(PlutoPatternKind.Look);
            bgms = ranker.RankOptions(PlutoPatternKind.BgmMood);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] ranker: " + ex.Message); }   // [부분 구제] 템플릿 기본값

        // 🎰 탐색 순위(UCB1) — 실패 시 활용 순위로 [부분 구제]
        IReadOnlyList<PlutoRankedOption> exploreHooks = hooks, exploreLooks = looks;
        try
        {
            exploreHooks = ranker.RankForExploration(PlutoPatternKind.Hook);
            exploreLooks = ranker.RankForExploration(PlutoPatternKind.Look);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] explore: " + ex.Message); exploreHooks = hooks; exploreLooks = looks; }

        var domain = PromptDomainClassifier.Classify(themeHint).ToString();
        var themeHooks = PlutoMontageStoryGenerator.TemplateHookLines(themeHint);
        var champHook = rec?.TopHook.DisplayLabel ?? themeHooks[0];
        var champLook = rec?.TopColorLook.DisplayLabel ?? PlutoColorGradingCompiler.NaturalPastelName;
        var champBgm = FirstUsable(bgms, rec?.TopBgmMood.DisplayLabel, ExcludedBgmMoods) ?? BabyBgmTrack.Lullaby.ToString();
        var champSynergy = PromoteSynergisticChampion(ranker, hooks, looks, domain, ref champHook, ref champLook);   // 🔗 강한 조합 시너지 → 챔피언 승격

        // 🧪 챌린저 — 감점·차단 제외 UCB1 최상위(테마 훅 우선 · 덜 검증된 후보 우대) · 역시너지 조합 회피 · 훅/룩 차이 보장
        var challHook = PickChallenger(PreferThemed(WithoutDomainAntiSynergy(ranker, exploreHooks, domain), themeHooks), champHook, null, suppressed) ?? AlternateTemplateHook(themeHooks, champHook);
        var challLook = PickChallengerLook(ranker, exploreLooks, champLook, challHook, suppressed) ?? AlternateLook(champLook);
        var challBgm = PickChallenger(bgms, champBgm, ExcludedBgmMoods, suppressed) ?? champBgm;
        if (string.Equals(champHook, challHook, StringComparison.Ordinal) && string.Equals(champLook, challLook, StringComparison.Ordinal))
            challLook = AlternateLook(champLook);   // 불변식 최종 가드
        var challBonus = Math.Max(BonusOf(exploreHooks, challHook), BonusOf(exploreLooks, challLook));
        var challSynergy = SafeSynergy(ranker, challHook, challLook).Score;

        // 💬 3막 자막 — 동일 컷 구간 · Act 1 = 변형 훅
        var champTask = BuildSubtitlesAsync(montagePlan, curatedCuts, themeHint, champHook, suppressed, ranker, ct);
        var challTask = BuildSubtitlesAsync(montagePlan, curatedCuts, themeHint.Length == 0 ? ChallengerThemeSuffix.TrimStart(' ', '·') : themeHint + ChallengerThemeSuffix,
            challHook, suppressed, ranker, ct);
        await Task.WhenAll(champTask, challTask).ConfigureAwait(false);

        var a = new PlutoExperimentVariant(ChampionLabel, champHook, champLook, champBgm, BuildSuffix(champHook, champLook, champBgm), champTask.Result);
        var b = new PlutoExperimentVariant(ChallengerLabel, challHook, challLook, challBgm, BuildSuffix(challHook, challLook, challBgm), challTask.Result);
        var hypothesis = BuildHypothesis(a, b, rec);
        if (challBonus > PlutoAdaptivePatternRanker.Epsilon) hypothesis += " · 🎰 UCB1 탐색 보너스 +" + challBonus.ToString("0.00", CultureInfo.InvariantCulture) + "(덜 검증된 후보)";
        if (champSynergy >= PlutoPairAttribution.StrongSynergyScore) hypothesis += " · 🔗 챔피언 조합 시너지 ×" + champSynergy.ToString("0.00", CultureInfo.InvariantCulture);
        return new PlutoVariantABPlan(_idFactory(), themeHint, a, b, hypothesis)
        {
            ChallengerExplorationBonus = challBonus,
            ChampionSynergyScore = champSynergy,
            ChallengerSynergyScore = challSynergy,
        };
    }

    // ── 🔗 조합 시너지 · 🎰 탐색 ──

    private static PlutoPairSynergy SafeSynergy(IPlutoAdaptivePatternRanker ranker, string hook, string look)
    {
        try { return ranker.GetPairSynergy(hook, look) ?? PlutoPairSynergy.Neutral; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] synergy: " + ex.Message); return PlutoPairSynergy.Neutral; }
    }

    private static PlutoPairSynergy SafeDomainSynergy(IPlutoAdaptivePatternRanker ranker, string domain, string hook)
    {
        try { return ranker.GetDomainHookSynergy(domain, hook) ?? PlutoPairSynergy.Neutral; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] domain synergy: " + ex.Message); return PlutoPairSynergy.Neutral; }
    }

    /// <summary>
    /// 🔗 챔피언 조합 승격 — 상위 <see cref="SynergyHookDepth"/> 비감점 훅 × 비감점 룩에서 <c>훅 점수 × 룩 점수 × 조합 시너지 × 도메인 시너지</c>가
    /// 현재 챔피언(1위 훅 × 1위 룩)보다 엄격히 큰 조합이 있으면 교체. 조합 이력 없음 → 모든 시너지 1.0 → 무변경. 반환 = 최종 챔피언 조합 시너지 점수.
    /// </summary>
    private static double PromoteSynergisticChampion(IPlutoAdaptivePatternRanker ranker, IReadOnlyList<PlutoRankedOption> hooks, IReadOnlyList<PlutoRankedOption> looks,
        string domain, ref string champHook, ref string champLook)
    {
        var current = SafeSynergy(ranker, champHook, champLook).Score;
        var best = ScoreOf(hooks, champHook) * ScoreOf(looks, champLook) * current * SafeDomainSynergy(ranker, domain, champHook).Score;
        var bestSynergy = current;
        var depth = 0;
        for (var i = 0; i < hooks.Count && depth < SynergyHookDepth; i++)
        {
            var h = hooks[i];
            if (h.IsPenalized) continue;
            depth++;
            var dom = SafeDomainSynergy(ranker, domain, h.DisplayLabel).Score;
            for (var k = 0; k < looks.Count; k++)
            {
                var l = looks[k];
                if (l.IsPenalized) continue;
                var syn = SafeSynergy(ranker, h.DisplayLabel, l.DisplayLabel).Score;
                var score = h.FinalScore * l.FinalScore * syn * dom;
                if (score > best + PlutoAdaptivePatternRanker.Epsilon)
                {
                    best = score;
                    bestSynergy = syn;
                    champHook = h.DisplayLabel;
                    champLook = l.DisplayLabel;
                }
            }
        }
        return bestSynergy;
    }

    private static double ScoreOf(IReadOnlyList<PlutoRankedOption> ranked, string label)
    {
        for (var i = 0; i < ranked.Count; i++) if (string.Equals(ranked[i].DisplayLabel, label, StringComparison.Ordinal)) return ranked[i].FinalScore;
        return 1.0;
    }

    private static double BonusOf(IReadOnlyList<PlutoRankedOption> ranked, string label)
    {
        for (var i = 0; i < ranked.Count; i++) if (string.Equals(ranked[i].DisplayLabel, label, StringComparison.Ordinal)) return ranked[i].ExplorationBonus;
        return 0.0;
    }

    /// <summary>🔗 현재 도메인에서 역시너지(<c>domain_hook:</c> 감점)인 훅 제외 — 전부 제외되면 원본 유지.</summary>
    private static IReadOnlyList<PlutoRankedOption> WithoutDomainAntiSynergy(IPlutoAdaptivePatternRanker ranker, IReadOnlyList<PlutoRankedOption> hooks, string domain)
    {
        List<PlutoRankedOption>? kept = null;
        for (var i = 0; i < hooks.Count; i++)
        {
            if (SafeDomainSynergy(ranker, domain, hooks[i].DisplayLabel).IsAntiSynergy)
            {
                if (kept is null) { kept = new List<PlutoRankedOption>(hooks.Count); for (var k = 0; k < i; k++) kept.Add(hooks[k]); }
                continue;
            }
            kept?.Add(hooks[i]);
        }
        return kept is { Count: > 0 } ? kept : hooks;
    }

    /// <summary>
    /// 🧪 챌린저 룩 — 챔피언 · 감점 · 차단 · (챌린저 훅과) 역시너지 조합 제외 후 <c>UCB1 점수 × 조합 시너지</c> 최대(동점 → 탐색 순위 우선).
    /// 전부 역시너지면 기존 규칙(<see cref="PickChallenger"/>)으로 폴백.
    /// </summary>
    private static string? PickChallengerLook(IPlutoAdaptivePatternRanker ranker, IReadOnlyList<PlutoRankedOption> ranked, string champion, string challengerHook, IReadOnlySet<string>? suppressed)
    {
        string? pick = null;
        var best = double.NegativeInfinity;
        for (var i = 0; i < ranked.Count; i++)
        {
            var o = ranked[i];
            if (string.Equals(o.DisplayLabel, champion, StringComparison.Ordinal)) continue;
            if (o.IsPenalized || (suppressed is { Count: > 0 } && suppressed.Contains(o.DisplayLabel))) continue;
            var syn = SafeSynergy(ranker, challengerHook, o.DisplayLabel);
            if (syn.IsAntiSynergy) continue;
            var score = o.UcbScore * syn.Score;
            if (score > best + PlutoAdaptivePatternRanker.Epsilon) { best = score; pick = o.DisplayLabel; }
        }
        return pick ?? PickChallenger(ranked, champion, null, suppressed);
    }

    // ── 선택 ──

    /// <summary>테마 훅을 랭킹 순서 그대로 앞에 배치(테마 외 후보는 뒤).</summary>
    private static List<PlutoRankedOption> PreferThemed(IReadOnlyList<PlutoRankedOption> ranked, IReadOnlyList<string> themed)
    {
        var first = new List<PlutoRankedOption>(ranked.Count);
        var rest = new List<PlutoRankedOption>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var inTheme = false;
            for (var k = 0; k < themed.Count; k++) if (string.Equals(themed[k], ranked[i].DisplayLabel, StringComparison.Ordinal)) { inTheme = true; break; }
            (inTheme ? first : rest).Add(ranked[i]);
        }
        first.AddRange(rest);
        return first;
    }

    private static string? FirstUsable(IReadOnlyList<PlutoRankedOption> ranked, string? top, string[]? excluded)
    {
        if (top is not null && !IsExcluded(top, excluded)) return top;
        for (var i = 0; i < ranked.Count; i++) if (!IsExcluded(ranked[i].DisplayLabel, excluded)) return ranked[i].DisplayLabel;
        return null;
    }

    private static string? PickChallenger(IReadOnlyList<PlutoRankedOption> ranked, string champion, string[]? excluded, IReadOnlySet<string>? suppressed)
    {
        for (var i = 0; i < ranked.Count; i++)
        {
            var o = ranked[i];
            if (string.Equals(o.DisplayLabel, champion, StringComparison.Ordinal) || IsExcluded(o.DisplayLabel, excluded)) continue;
            if (o.IsPenalized || (suppressed is { Count: > 0 } && suppressed.Contains(o.DisplayLabel))) continue;
            return o.DisplayLabel;
        }
        return null;
    }

    private static bool IsExcluded(string label, string[]? excluded)
    {
        if (excluded is null) return false;
        for (var i = 0; i < excluded.Length; i++) if (string.Equals(label, excluded[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string AlternateTemplateHook(IReadOnlyList<string> themeHooks, string champion)
    {
        for (var i = 0; i < themeHooks.Count; i++) if (!string.Equals(themeHooks[i], champion, StringComparison.Ordinal)) return themeHooks[i];
        var all = PlutoMontageStoryGenerator.AllTemplateHookLines;
        for (var i = 0; i < all.Count; i++) if (!string.Equals(all[i], champion, StringComparison.Ordinal)) return all[i];
        return champion;
    }

    private static string AlternateLook(string champion)
    {
        string[] looks = { PlutoColorGradingCompiler.GoldenHourWarmName, PlutoColorGradingCompiler.NaturalPastelName, PlutoColorGradingCompiler.VintageFilm16mmName };
        for (var i = 0; i < looks.Length; i++) if (!string.Equals(looks[i], champion, StringComparison.Ordinal)) return looks[i];
        return PlutoColorGradingCompiler.NaturalPastelName;
    }

    // ── 자막 ──

    private async Task<IReadOnlyList<SubtitleCue>> BuildSubtitlesAsync(PlutoMontagePlan? plan, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts, string themeHint,
        string hook, IReadOnlySet<string>? suppressed, IPlutoAdaptivePatternRanker ranker, CancellationToken ct)
    {
        IReadOnlyList<SubtitleCue>? cues = null;
        if (plan is not null)
        {
            if (_story is not null)
            {
                try { cues = await _story.GenerateThreeActSubtitlesAsync(plan, curatedCuts, themeHint, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] story fallback: " + ex.Message); cues = null; }
            }
            if (cues is null || cues.Count == 0)
            {
                try { cues = PlutoMontageStoryGenerator.GenerateLocal(plan, themeHint, suppressed, ranker); }
                catch (ArgumentException ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] local: " + ex.Message); cues = null; }
            }
        }
        if (cues is null || cues.Count == 0)
        {
            var lines = PlutoMontageStoryGenerator.BuildTemplateLines(NominalWindows.Length, themeHint, 0u, suppressed);
            var arr = new SubtitleCue[NominalWindows.Length];
            for (var i = 0; i < arr.Length; i++) arr[i] = SubtitleCue.Precise(NominalWindows[i].Start, NominalWindows[i].End, lines[i].Ko, lines[i].En);
            cues = arr;
        }
        return WithHook(cues, hook);
    }

    /// <summary>Act 1 큐 텍스트를 변형 훅으로 교체(구간 불변 · EN은 템플릿 짝 · 미등록 → 기존 EN 제거).</summary>
    internal static IReadOnlyList<SubtitleCue> WithHook(IReadOnlyList<SubtitleCue> cues, string hook)
    {
        if (cues.Count == 0 || string.IsNullOrWhiteSpace(hook) || string.Equals(cues[0].Text, hook, StringComparison.Ordinal)) return cues;
        var arr = new SubtitleCue[cues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = cues[i];
        var c0 = arr[0];
        arr[0] = c0 with { Text = hook, SecondaryText = PlutoMontageStoryGenerator.TemplateHookEnglish(hook) };
        return arr;
    }

    // ── 문구 ──

    internal static string BuildSuffix(string hook, string look, string bgm)
        => ", opening hook: " + hook + ", color look: " + look + ", bgm mood: " + bgm;

    private static string BuildHypothesis(PlutoExperimentVariant a, PlutoExperimentVariant b, PlutoAdaptiveRecommendation? rec)
    {
        var diffs = new List<string>(3);
        if (!string.Equals(a.HookStyle, b.HookStyle, StringComparison.Ordinal)) diffs.Add("훅 '" + b.HookStyle + "'");
        if (!string.Equals(a.ColorLookId, b.ColorLookId, StringComparison.Ordinal)) diffs.Add("룩 " + b.ColorLookId);
        if (!string.Equals(a.BgmMood, b.BgmMood, StringComparison.Ordinal)) diffs.Add("BGM " + b.BgmMood);
        var basis = rec is { HasReinforcedHistory: true } ? "학습 챔피언" : "기본 챔피언";
        return "⚖️ 가설: 챌린저(" + string.Join(" · ", diffs) + ")가 " + basis + "('" + a.HookStyle + "' · " + a.ColorLookId + ") 대비 3초 유지율·CTR을 높인다 — 성과 입력 시 폐루프 강화에 반영";
    }

    private IReadOnlySet<string>? SafeSuppressed()
    {
        try { return _notebook?.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] suppressed: " + ex.Message); return null; }
    }
}
