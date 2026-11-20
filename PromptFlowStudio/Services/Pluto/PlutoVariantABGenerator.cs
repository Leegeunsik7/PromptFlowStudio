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
    /// <summary>🎯 챔피언 훅 사후 신뢰 구간(이력 없음 → 사전 분포).</summary>
    public PlutoPosteriorConfidence ChampionConfidence { get; init; } = PlutoPosteriorConfidence.Prior;
    /// <summary>🎯 챌린저 훅 사후 신뢰 구간.</summary>
    public PlutoPosteriorConfidence ChallengerConfidence { get; init; } = PlutoPosteriorConfidence.Prior;
    /// <summary>🎯 1위 훅이 소표본 급등이라 신뢰 하한이 더 높은 검증 후보를 챔피언으로 교체함.</summary>
    public bool ChampionPromotedByConfidence { get; init; }
    /// <summary>🌊 레짐 드리프트 탐색 서지 중 생성된 실험(챌린저 = 탐색 온도 상향 UCB1).</summary>
    public bool IsExplorationSurge { get; init; }
    /// <summary>🌊 생성 시점 탐색 온도 배수(서지 없음 → 1.0).</summary>
    public double ExplorationTemperature { get; init; } = 1.0;
    /// <summary>🔁 챌린저에 배정된 후회 리허설 대상(<c>hook:…</c> · <c>look:…</c> · <c>pair:…</c>) — 없음 → null.</summary>
    public string? RegretRehearsalLabel { get; init; }
    /// <summary>🔁 리허설 대상의 회복 사전 부스트(없음 → 0).</summary>
    public double RegretRehearsalBoost { get; init; }
    /// <summary>🔁 챌린저 B 가 '거듭 지나친 고후회 대안'의 실측 재검증에 쓰임.</summary>
    public bool IsRegretRehearsal => RegretRehearsalLabel is not null;
    /// <summary>🎲 챌린저 탐색 정책 — <c>UCB1</c>(결정론) · <c>Thompson-UCB</c>(사후 추첨 혼합).</summary>
    public string ExplorationPolicy { get; init; } = PlutoThompsonUcbSampler.PolicyUcb;
    public bool IsThompsonSampled => string.Equals(ExplorationPolicy, PlutoThompsonUcbSampler.PolicyHybrid, StringComparison.Ordinal);
    /// <summary>🧬 실험 컨텍스트의 렌더 포맷(미지정 → null · 도메인만으로 컨텍스트 구성).</summary>
    public string? ContextRenderMode { get; init; }
    /// <summary>🧬 챔피언 훅의 (Domain, RenderMode) 경험적 베이즈 추정(근거 없음 → None).</summary>
    public PlutoEmpiricalBayesEstimate ChampionContextTransfer { get; init; } = PlutoEmpiricalBayesEstimate.None;
    /// <summary>🧬 챔피언 BGM 무드의 (Domain, RenderMode) 경험적 베이즈 추정(근거 없음 → None).</summary>
    public PlutoEmpiricalBayesEstimate ChampionBgmContextTransfer { get; init; } = PlutoEmpiricalBayesEstimate.None;
    /// <summary>🧬 지역(컨텍스트) 근거가 전역 순위를 실제로 바꿔 챔피언/챌린저 선정에 반영됨.</summary>
    public bool IsContextSpecialized { get; init; }
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

    /// <summary>
    /// 🧬 렌더 포맷을 명시한 컨텍스트 실험 — (테마에서 분류한 Domain, <paramref name="contextRenderMode"/>) 컨텍스트의 지역 근거를 경험적 베이즈로 수축해 반영
    /// (기본 구현 → 컨텍스트 미사용).
    /// </summary>
    Task<PlutoVariantABPlan> BuildVariantPairAsync(string themeHint, string? contextRenderMode, PlutoMontagePlan? montagePlan, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts,
        CancellationToken ct)
        => BuildVariantPairAsync(themeHint, montagePlan, curatedCuts, ct);
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
    private readonly Random? _explorationRandom;

    /// <param name="explorationRandom">
    /// 🎲 톰슨–UCB 하이브리드 탐색용 난수원(시드 주입 → 재현 가능) · null → 기존 결정론 UCB1(같은 상태 = 같은 챌린저).
    /// </param>
    public PlutoVariantABGenerator(IPlutoAdaptivePatternRanker? ranker = null, IPlutoMontageStoryGenerator? story = null, IPlutoMistakeNotebookService? notebook = null,
        Func<string>? experimentIdFactory = null, Random? explorationRandom = null)
    {
        _explorationRandom = explorationRandom;
        _ranker = ranker;
        _story = story;
        _notebook = notebook;
        _idFactory = experimentIdFactory ?? (static () => "exp-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
    }

    public Task<PlutoVariantABPlan> BuildVariantPairAsync(string themeHint, PlutoMontagePlan? montagePlan = null, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts = null,
        CancellationToken ct = default)
        => BuildVariantPairAsync(themeHint, null, montagePlan, curatedCuts, ct);

    public async Task<PlutoVariantABPlan> BuildVariantPairAsync(string themeHint, string? contextRenderMode, PlutoMontagePlan? montagePlan, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts,
        CancellationToken ct)
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

        // 🧬 (Domain, RenderMode) 컨텍스트 랭킹 — 지역 근거가 전역 순위를 실제로 바꿀 때만 채택(콜드스타트는 전역 사후로 수축 → 단발 지역 관측에 휘둘리지 않음).
        //    지역 근거 없음/배수 1.0 → 아래 전역 경로 그대로(기존 동작 불변).
        contextRenderMode = string.IsNullOrWhiteSpace(contextRenderMode) ? null : contextRenderMode.Trim();
        var contextual = false;
        IReadOnlyList<PlutoRankedOption> ctxHooks = Array.Empty<PlutoRankedOption>(), ctxBgms = ctxHooks;
        string? ctxChampBgm = null;
        try
        {
            ctxHooks = ranker.RankForContext(PlutoPatternKind.Hook, domain, contextRenderMode) ?? Array.Empty<PlutoRankedOption>();
            var ctxLooks = ranker.RankForContext(PlutoPatternKind.Look, domain, contextRenderMode) ?? Array.Empty<PlutoRankedOption>();
            ctxBgms = ranker.RankForContext(PlutoPatternKind.BgmMood, domain, contextRenderMode) ?? Array.Empty<PlutoRankedOption>();
            if (IsContextSpecialized(ctxHooks) || IsContextSpecialized(ctxLooks))
            {
                contextual = true;
                if (ctxHooks.Count > 0) { hooks = ctxHooks; exploreHooks = SortByUcb(ctxHooks); champHook = ctxHooks[0].DisplayLabel; }
                if (ctxLooks.Count > 0) { looks = ctxLooks; exploreLooks = SortByUcb(ctxLooks); champLook = ctxLooks[0].DisplayLabel; }
            }
            // 🧬 Step 18 — BGM 무드도 같은 컨텍스트 수축(지역 근거가 순위를 바꿀 때만 · 'custom'/'none' 제외)
            if (IsContextSpecialized(ctxBgms) && FirstUsable(ctxBgms, null, ExcludedBgmMoods) is { } cb)
            {
                contextual = true;
                bgms = ctxBgms;
                ctxChampBgm = cb;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] context rank: " + ex.Message); }   // [부분 구제] 전역 순위

        // 🎲 하이브리드 톰슨–UCB — 난수원이 주입됐을 때만(사후 추첨 편차 + UCB1 보너스 + 후회 회복 사전). 미주입 → 결정론 UCB1.
        var policy = PlutoThompsonUcbSampler.PolicyUcb;
        if (_explorationRandom is { } rng)
        {
            try
            {
                var priors = RecoveryPriors(ranker);
                lock (rng)   // System.Random 은 스레드 안전하지 않다
                {
                    exploreHooks = PlutoThompsonUcbSampler.Rescore(exploreHooks, rng, priors);
                    exploreLooks = PlutoThompsonUcbSampler.Rescore(exploreLooks, rng, priors);
                }
                policy = PlutoThompsonUcbSampler.PolicyHybrid;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] thompson: " + ex.Message); }   // [부분 구제] UCB1 순위
        }
        var champBgm = ctxChampBgm ?? FirstUsable(bgms, rec?.TopBgmMood.DisplayLabel, ExcludedBgmMoods) ?? BabyBgmTrack.Lullaby.ToString();
        var promotedByConfidence = PromoteProvenChampion(hooks, ref champHook);   // 🎯 소표본 급등 1위 → 신뢰 하한이 더 높은 검증 후보
        var drift = SafeDrift(ranker);                                             // 🌊 레짐 드리프트 → 탐색 서지
        var champSynergy = PromoteSynergisticChampion(ranker, hooks, looks, domain, ref champHook, ref champLook, promotedByConfidence);   // 🔗 강한 조합 시너지 → 챔피언 승격

        // 🧪 챌린저 — 감점·차단 제외 UCB1 최상위(테마 훅 우선 · 덜 검증된 후보 우대) · 역시너지 조합 회피 · 훅/룩 차이 보장
        var challHook = PickChallenger(PreferThemed(WithoutDomainAntiSynergy(ranker, exploreHooks, domain), themeHooks), champHook, null, suppressed) ?? AlternateTemplateHook(themeHooks, champHook);
        var challLook = PickChallengerLook(ranker, exploreLooks, champLook, challHook, suppressed) ?? AlternateLook(champLook);
        var challBgm = PickChallenger(bgms, champBgm, ExcludedBgmMoods, suppressed) ?? champBgm;
        // 🔁 능동 후회 리허설 — 거듭 지나쳐 반사실 후회가 쌓인 대안을 챌린저로 실측 검증(검증되면 폐루프가 회복 사전을 소거)
        var rehearsal = ApplyRegretRehearsal(ranker, hooks, exploreLooks, domain, champHook, champLook, suppressed, ref challHook, ref challLook);
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
        var thompson = string.Equals(policy, PlutoThompsonUcbSampler.PolicyHybrid, StringComparison.Ordinal);
        if (thompson) hypothesis += " · 🎲 톰슨–UCB 하이브리드 탐색(챌린저 탐색 점수 " + (challBonus >= 0 ? "+" : "−") + Math.Abs(challBonus).ToString("0.00", CultureInfo.InvariantCulture) + ")";
        else if (challBonus > PlutoAdaptivePatternRanker.Epsilon) hypothesis += " · 🎰 UCB1 탐색 보너스 +" + challBonus.ToString("0.00", CultureInfo.InvariantCulture) + "(덜 검증된 후보)";
        var champTransfer = TransferOf(ctxHooks, champHook);
        if (champTransfer.IsTransferActive)
            hypothesis += " · 🧬 콜드스타트 컨텍스트 — 전역 검증 사전 이식(전역 가중 " + (champTransfer.GlobalWeight * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%)";
        else if (contextual) hypothesis += " · 🧬 컨텍스트 특화 순위 반영";
        if (champSynergy >= PlutoPairAttribution.StrongSynergyScore) hypothesis += " · 🔗 챔피언 조합 시너지 ×" + champSynergy.ToString("0.00", CultureInfo.InvariantCulture);
        var champConf = ConfidenceOf(hooks, champHook);
        var challConf = ConfidenceOf(exploreHooks, challHook);
        if (promotedByConfidence) hypothesis += " · 🎯 챔피언 = 신뢰 하한 최대(" + champConf.LowerConfidenceBound.ToString("0.00", CultureInfo.InvariantCulture) + ") — 소표본 급등 1위는 챌린저 검증 대상으로";
        else if (champConf.IsProvenWinner) hypothesis += " · 🎯 챔피언 검증됨(90% 하한 ×" + champConf.LowerConfidenceBound.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        if (rehearsal is not null)
            hypothesis += " · 🔁 후회 리허설: " + rehearsal.Label + "(미해소 후회 " + rehearsal.OutstandingRegret.ToString("0.00", CultureInfo.InvariantCulture)
                + " · 회복 사전 +" + rehearsal.Boost.ToString("0.00", CultureInfo.InvariantCulture) + ") — 지나친 대안을 실측으로 재검증";
        if (drift.IsSurgeActive) hypothesis += " · 🌊 레짐 변화 감지 — 탐색 온도 ×" + drift.ExplorationTemperature.ToString("0.00", CultureInfo.InvariantCulture) + "(낡은 승자 대신 새 후보 공격 검증)";
        return new PlutoVariantABPlan(_idFactory(), themeHint, a, b, hypothesis)
        {
            ChallengerExplorationBonus = challBonus,
            ChampionSynergyScore = champSynergy,
            ChallengerSynergyScore = challSynergy,
            ChampionConfidence = champConf,
            ChallengerConfidence = challConf,
            ChampionPromotedByConfidence = promotedByConfidence,
            IsExplorationSurge = drift.IsSurgeActive,
            ExplorationTemperature = drift.ExplorationTemperature,
            RegretRehearsalLabel = rehearsal?.Label,
            RegretRehearsalBoost = rehearsal?.Boost ?? 0.0,
            ExplorationPolicy = policy,
            ContextRenderMode = contextRenderMode,
            ChampionContextTransfer = champTransfer,
            ChampionBgmContextTransfer = TransferOf(ctxBgms, champBgm),
            IsContextSpecialized = contextual,
        };
    }

    // ── 🧬 컨텍스트 랭킹 · 🎲 톰슨–UCB ──

    private static bool IsContextSpecialized(IReadOnlyList<PlutoRankedOption> ranked)
    {
        for (var i = 0; i < ranked.Count; i++) if (Math.Abs(ranked[i].ContextMultiplier - 1.0) > PlutoAdaptivePatternRanker.Epsilon) return true;
        return false;
    }

    private static PlutoEmpiricalBayesEstimate TransferOf(IReadOnlyList<PlutoRankedOption> ranked, string label)
    {
        for (var i = 0; i < ranked.Count; i++) if (string.Equals(ranked[i].DisplayLabel, label, StringComparison.Ordinal)) return ranked[i].ContextTransfer ?? PlutoEmpiricalBayesEstimate.None;
        return PlutoEmpiricalBayesEstimate.None;
    }

    private static List<PlutoRankedOption> SortByUcb(IReadOnlyList<PlutoRankedOption> ranked)
    {
        var list = new List<PlutoRankedOption>(ranked);
        list.Sort(static (a, b) =>
        {
            var c = b.UcbScore.CompareTo(a.UcbScore);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return list;
    }

    /// <summary>🔁 후회 회복 사전 → 후보 키(<c>hook:…</c> · <c>look:…</c>)별 부스트(조합 <c>pair:</c> 는 리허설 단계가 따로 처리).</summary>
    private static Dictionary<string, double>? RecoveryPriors(IPlutoAdaptivePatternRanker ranker)
    {
        var boosts = SafeRecoveryBoosts(ranker);
        if (boosts.Count == 0) return null;
        var map = new Dictionary<string, double>(boosts.Count, StringComparer.Ordinal);
        for (var i = 0; i < boosts.Count; i++)
        {
            var b = boosts[i];
            if (b is null || string.IsNullOrWhiteSpace(b.Label) || !(b.Boost > 0)) continue;
            if (!b.Label.StartsWith(PlutoAdaptivePatternRanker.HookPrefix, StringComparison.Ordinal) && !b.Label.StartsWith(PlutoAdaptivePatternRanker.LookPrefix, StringComparison.Ordinal)) continue;
            if (!map.TryGetValue(b.Label, out var cur) || b.Boost > cur) map[b.Label] = b.Boost;
        }
        return map;
    }

    // ── 🔁 능동 후회 리허설 ──

    private static IReadOnlyList<PlutoRegretRecoveryBoost> SafeRecoveryBoosts(IPlutoAdaptivePatternRanker ranker)
    {
        try { return ranker.GetRegretRecoveryBoosts() ?? Array.Empty<PlutoRegretRecoveryBoost>(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] regret recovery: " + ex.Message); return Array.Empty<PlutoRegretRecoveryBoost>(); }
    }

    /// <summary>
    /// 🔁 회복 사전(부스트 내림차순) 중 첫 '적용 가능' 대안을 챌린저에 배정 — 실험 1회 = 리허설 대상 1개(변수 통제).
    ///   ▸ <c>hook:X</c> → 챌린저 훅 = X(룩은 X 와 역시너지가 아닌 탐색 최상위로 재선정) · <c>look:Y</c> → 챌린저 룩 = Y · <c>pair:X+Y</c> → 둘 다.
    ///   ▸ 제외: 이미 챔피언(곧 실측됨) · 오답 감점/차단 · 도메인 역시너지 훅 · 챌린저 훅과 역시너지인 룩 · 챔피언과 완전히 같은 조합.
    /// 적용 대상 없음 → null(기존 UCB1 챌린저 그대로).
    /// </summary>
    private static PlutoRegretRecoveryBoost? ApplyRegretRehearsal(IPlutoAdaptivePatternRanker ranker, IReadOnlyList<PlutoRankedOption> hooks, IReadOnlyList<PlutoRankedOption> looks,
        string domain, string champHook, string champLook, IReadOnlySet<string>? suppressed, ref string challHook, ref string challLook)
    {
        var boosts = SafeRecoveryBoosts(ranker);
        for (var i = 0; i < boosts.Count; i++)
        {
            var b = boosts[i];
            if (b is null || string.IsNullOrWhiteSpace(b.Label) || !(b.Boost > 0)) continue;
            if (PlutoPairAttribution.TrySplitPair(b.Label, out var pairHook, out var pairLook))
            {
                if (string.Equals(pairHook, champHook, StringComparison.Ordinal) && string.Equals(pairLook, champLook, StringComparison.Ordinal)) continue;
                if (!HookEligible(ranker, hooks, domain, pairHook, suppressed) || !Usable(looks, pairLook, suppressed)) continue;
                if (SafeSynergy(ranker, pairHook, pairLook).IsAntiSynergy) continue;
                challHook = pairHook; challLook = pairLook;
                return b;
            }
            if (b.Label.StartsWith(PlutoAdaptivePatternRanker.HookPrefix, StringComparison.Ordinal))
            {
                var hook = b.Label[PlutoAdaptivePatternRanker.HookPrefix.Length..];
                if (hook.Length == 0 || string.Equals(hook, champHook, StringComparison.Ordinal) || !HookEligible(ranker, hooks, domain, hook, suppressed)) continue;
                if (!string.Equals(hook, challHook, StringComparison.Ordinal))
                {
                    challHook = hook;
                    // 새 훅과 역시너지인 룩은 피한다(변경은 그때만 — 리허설 변수는 훅 하나)
                    if (SafeSynergy(ranker, hook, challLook).IsAntiSynergy)
                        challLook = PickChallengerLook(ranker, looks, champLook, hook, suppressed) ?? challLook;
                }
                return b;
            }
            if (b.Label.StartsWith(PlutoAdaptivePatternRanker.LookPrefix, StringComparison.Ordinal))
            {
                var look = b.Label[PlutoAdaptivePatternRanker.LookPrefix.Length..];
                if (look.Length == 0 || string.Equals(look, champLook, StringComparison.Ordinal) || !Usable(looks, look, suppressed)) continue;
                if (SafeSynergy(ranker, challHook, look).IsAntiSynergy) continue;
                challLook = look;
                return b;
            }
        }
        return null;
    }

    private static bool HookEligible(IPlutoAdaptivePatternRanker ranker, IReadOnlyList<PlutoRankedOption> hooks, string domain, string hook, IReadOnlySet<string>? suppressed)
        => Usable(hooks, hook, suppressed) && !SafeDomainSynergy(ranker, domain, hook).IsAntiSynergy;

    /// <summary>감점 · 차단이 아닌 후보(랭킹 목록에 없는 라벨은 차단 목록만 확인).</summary>
    private static bool Usable(IReadOnlyList<PlutoRankedOption> ranked, string label, IReadOnlySet<string>? suppressed)
    {
        if (suppressed is { Count: > 0 } && suppressed.Contains(label)) return false;
        for (var i = 0; i < ranked.Count; i++)
            if (string.Equals(ranked[i].DisplayLabel, label, StringComparison.Ordinal)) return !ranked[i].IsPenalized;
        return true;
    }

    // ── 🎯 사후 신뢰 · 🌊 레짐 드리프트 ──

    private static PlutoRegimeDriftStatus SafeDrift(IPlutoAdaptivePatternRanker ranker)
    {
        try { return ranker.GetRegimeDriftStatus() ?? PlutoRegimeDriftStatus.Stable; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.AB] drift: " + ex.Message); return PlutoRegimeDriftStatus.Stable; }
    }

    private static PlutoPosteriorConfidence ConfidenceOf(IReadOnlyList<PlutoRankedOption> ranked, string label)
    {
        for (var i = 0; i < ranked.Count; i++) if (string.Equals(ranked[i].DisplayLabel, label, StringComparison.Ordinal)) return ranked[i].Posterior ?? PlutoPosteriorConfidence.Prior;
        return PlutoPosteriorConfidence.Prior;
    }

    /// <summary>
    /// 🎯 챔피언 = 위험 회피(신뢰 하한) · 챌린저 = 낙관(UCB) — 현재 챔피언 훅이 덜 검증(시행 &lt; <see cref="PlutoAdaptivePatternRanker.UnderSampledTrials"/>)이고
    /// 비감점 · 다회 검증(시행 ≥ 기준) 훅 중 신뢰 하한이 엄격히 더 높은 후보가 있으면 그 후보로 교체(소표본 급등은 챌린저 슬롯에서 검증받는다).
    /// 이력 없음(전 후보 시행 0) → 무변경. 반환 = 교체 여부.
    /// </summary>
    private static bool PromoteProvenChampion(IReadOnlyList<PlutoRankedOption> hooks, ref string champHook)
    {
        PlutoRankedOption? current = null;
        for (var i = 0; i < hooks.Count; i++) if (string.Equals(hooks[i].DisplayLabel, champHook, StringComparison.Ordinal)) { current = hooks[i]; break; }
        if (current is null || current.ArmTrials >= PlutoAdaptivePatternRanker.UnderSampledTrials) return false;
        PlutoRankedOption? best = null;
        for (var i = 0; i < hooks.Count; i++)
        {
            var o = hooks[i];
            if (o.IsPenalized || o.ArmTrials < PlutoAdaptivePatternRanker.UnderSampledTrials) continue;
            if (o.LowerConfidenceBound <= current.LowerConfidenceBound + PlutoAdaptivePatternRanker.Epsilon) continue;
            if (best is null || o.LowerConfidenceBound > best.LowerConfidenceBound) best = o;
        }
        if (best is null) return false;
        champHook = best.DisplayLabel;
        return true;
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
        string domain, ref string champHook, ref string champLook, bool provenHooksOnly = false)
    {
        var current = SafeSynergy(ranker, champHook, champLook).Score;
        var best = ScoreOf(hooks, champHook) * ScoreOf(looks, champLook) * current * SafeDomainSynergy(ranker, domain, champHook).Score;
        var bestSynergy = current;
        var depth = 0;
        for (var i = 0; i < hooks.Count && depth < SynergyHookDepth; i++)
        {
            var h = hooks[i];
            if (h.IsPenalized) continue;
            if (provenHooksOnly && h.ArmTrials < PlutoAdaptivePatternRanker.UnderSampledTrials) continue;   // 🎯 신뢰 승격 후엔 소표본 훅으로 되돌리지 않음
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
