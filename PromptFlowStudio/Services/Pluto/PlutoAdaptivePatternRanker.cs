using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧠 학습 패턴 종류 — 키 접두사는 <see cref="PlutoClosedLoopReinforcementService.PatternTokens"/>와 동일.</summary>
public enum PlutoPatternKind { Hook, Look, BgmMood, StyleTag }

/// <summary>🧠 랭킹된 후보 — FinalScore = BaseScore × 학습 배수 × (1 − clamp(감점, 0, 0.85)) + 결정론 타이브레이커.</summary>
public sealed record PlutoRankedOption(string Key, string DisplayLabel, double BaseScore, double LearnedWeightMultiplier, double MistakePenaltyFactor, double FinalScore, string ReasonBadge)
{
    /// <summary>폐루프 돌파로 강화된 후보(배수 &gt; 1).</summary>
    public bool IsReinforced => LearnedWeightMultiplier > 1.0 + PlutoAdaptivePatternRanker.Epsilon;
    /// <summary>오답 노트 감점 중인 후보.</summary>
    public bool IsPenalized => MistakePenaltyFactor > PlutoAdaptivePatternRanker.Epsilon;
    /// <summary>🎰 유효 시행 수(반감기 감쇠 반영 · 미검증 → 0).</summary>
    public double ArmTrials { get; init; }
    /// <summary>🎰 UCB1 탐색 보너스 = C × √(ln(전체 시행 + 1) / (시행 + 1)).</summary>
    public double ExplorationBonus { get; init; }
    /// <summary>🎰 UCB1 점수 = 사후 평균(<see cref="FinalScore"/>) + 탐색 보너스 — 챌린저/탐색 기획안 선정 기준.</summary>
    public double UcbScore => FinalScore + ExplorationBonus;
    /// <summary>🎯 베이지안 사후 신뢰(학습 배수 척도 · 이력 없음 → 사전 분포).</summary>
    public PlutoPosteriorConfidence Posterior { get; init; } = PlutoPosteriorConfidence.Prior;
    /// <summary>🎯 사후 평균 배수(소표본일수록 1.0 으로 수축).</summary>
    public double PosteriorMean => Posterior.PosteriorMean;
    /// <summary>🎯 사후 표준편차.</summary>
    public double PosteriorUncertainty => Posterior.PosteriorUncertainty;
    /// <summary>🎯 90% 신뢰 하한 — 챔피언(위험 회피) 선정 기준.</summary>
    public double LowerConfidenceBound => Posterior.LowerConfidenceBound;
    /// <summary>🎯 90% 신뢰 상한.</summary>
    public double UpperConfidenceBound => Posterior.UpperConfidenceBound;
    /// <summary>📐 컨텍스트 잔차 알파 기대치(합성 점수 0~1 척도 · 포맷 기준선 대비 평균 초과분 · 미추적 → 0).</summary>
    public double ResidualAlpha { get; init; }
    /// <summary>⚖️ 다목적(훅 · 완주 · CTR) 파레토 평가 — 근거 없음 → <see cref="PlutoParetoAssessment.None"/>(배수 1.0 · 기존 점수 불변).</summary>
    public PlutoParetoAssessment Pareto { get; init; } = PlutoParetoAssessment.None;
    /// <summary>⚖️ 다목적 성과 EWMA(근거 없음 → default).</summary>
    public PlutoObjectiveVector Objectives { get; init; }
    /// <summary>⚖️ 다목적 표본 수.</summary>
    public double ObjectiveSamples { get; init; }
    /// <summary>⚖️ 파레토 프런티어(티어 0) · 비클릭베이트.</summary>
    public bool IsParetoFrontier => Pareto.IsFrontier && !Pareto.IsClickbaitSpike;
    /// <summary>🎣 3초 유지율만 높고 완주율이 붕괴한 클릭베이트 급등(학습 부스트 무효 + 강등).</summary>
    public bool IsClickbaitSpike => Pareto.IsClickbaitSpike;
    /// <summary>🧬 (Domain, RenderMode) 컨텍스트 경험적 베이즈 추정(컨텍스트 랭킹 전용 · 그 외 → <see cref="PlutoEmpiricalBayesEstimate.None"/>).</summary>
    public PlutoEmpiricalBayesEstimate ContextTransfer { get; init; } = PlutoEmpiricalBayesEstimate.None;
    /// <summary>🧬 컨텍스트 순위 배수(전역 순위 대비 지역 특화 · 기본 1.0).</summary>
    public double ContextMultiplier { get; init; } = 1.0;
}

/// <summary>
/// 🎰 UCB1 탐색 텔레메트리 — 전체 유효 시행 · 탐색 후보 수(비감점 · 시행 &lt; <see cref="PlutoAdaptivePatternRanker.UnderSampledTrials"/>) ·
/// 탐색 비중(%) = Σ탐색 보너스 / ΣUCB 점수(훅 + 룩 비감점 후보) · UCB 1위 훅/룩.
/// </summary>
public sealed record PlutoExplorationTelemetry(double TotalTrials, int ArmCount, int ExplorationCandidateCount, double ExplorationRatePct, string TopExplorationHook, string TopExplorationLook)
{
    public static readonly PlutoExplorationTelemetry Empty = new(0.0, 0, 0, 0.0, string.Empty, string.Empty);
}

/// <summary>🧠 현재 학습 상태 기준 최적 훅 · 룩 · BGM 무드 · 태그.</summary>
public sealed record PlutoAdaptiveRecommendation(PlutoRankedOption TopHook, PlutoRankedOption TopColorLook, PlutoRankedOption TopBgmMood, IReadOnlyList<PlutoRankedOption> TopTags, bool HasReinforcedHistory);

/// <summary>🧠 Pluto 4.2.1 적응형 패턴 랭커 — 폐루프 강화 가중치(<c>pattern:hook:/look:/bgm:/style:</c>)와 오답 노트 벌점을 순방향 생성 랭킹에 반영.</summary>
public interface IPlutoAdaptivePatternRanker
{
    /// <summary>학습 메모리 · 오답 노트 선적재(멱등 · 실패 → 기본 가중치로 [부분 구제]).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>후보(null → 기본 후보 + 학습된 토큰) 랭킹 → 종류별 1위 + 상위 태그.</summary>
    PlutoAdaptiveRecommendation GetCurrentRecommendations(IEnumerable<string>? candidateHooks = null, IEnumerable<string>? candidateLooks = null,
        IEnumerable<string>? candidateBgmMoods = null, IEnumerable<string>? candidateTags = null);

    /// <summary>한 종류의 후보 전체를 점수 내림차순으로(null → 기본 후보 + 학습된 토큰).</summary>
    IReadOnlyList<PlutoRankedOption> RankOptions(PlutoPatternKind kind, IEnumerable<string>? candidates = null);

    /// <summary>태그를 (학습 가중치 − 오답 감점) 내림차순으로 · 최대 <paramref name="maxCount"/>개.</summary>
    IReadOnlyList<string> RankTags(IEnumerable<string> tags, int maxCount = 8);

    /// <summary>🎵 1위 BGM 무드(키 · 라벨 · <c>bgm:</c> 접두 허용) → 가용 트랙 ID(정확 일치 → 무드 별칭) · 불일치/빈 목록 → <paramref name="fallbackTrackId"/>.</summary>
    string ResolveRecommendedBgmTrackId(string? topBgmMoodKeyOrLabel, IReadOnlyList<string> availableTrackIds, string fallbackTrackId);

    /// <summary>🎰 UCB1 점수(사후 평균 + 탐색 보너스) 내림차순 — 덜 검증된 후보를 체계적으로 끌어올림(기본 구현 → 활용 순위).</summary>
    IReadOnlyList<PlutoRankedOption> RankForExploration(PlutoPatternKind kind, IEnumerable<string>? candidates = null) => RankOptions(kind, candidates);

    /// <summary>🔗 훅 × 룩 조합 시너지(<c>pair:{hook}+{look}</c>) — 이력 없음 → 중립(1.0).</summary>
    PlutoPairSynergy GetPairSynergy(string? hook, string? look) => PlutoPairSynergy.Neutral;

    /// <summary>🔗 도메인 × 훅 시너지(<c>domain_hook:{domain}+{hook}</c>) — 이력 없음 → 중립(1.0).</summary>
    PlutoPairSynergy GetDomainHookSynergy(string? domain, string? hook) => PlutoPairSynergy.Neutral;

    /// <summary>🔗 최강 시너지 조합(Score 최대 · 비감점 · 강화 이력 있음) — 없음 → null.</summary>
    PlutoPairSynergy? GetTopSynergyPair() => null;

    /// <summary>🎰 현재 UCB1 탐색 텔레메트리(훅 + 룩).</summary>
    PlutoExplorationTelemetry GetExplorationTelemetry() => PlutoExplorationTelemetry.Empty;

    /// <summary>🌊 레짐 드리프트 · 탐색 서지 현황(기본 구현 → 안정).</summary>
    PlutoRegimeDriftStatus GetRegimeDriftStatus() => PlutoRegimeDriftStatus.Stable;

    /// <summary>📐 컨텍스트(Domain × RenderMode) EWMA 기준선(기본 구현 → 없음).</summary>
    IReadOnlyList<PlutoContextBaselineStat> GetContextBaselines() => Array.Empty<PlutoContextBaselineStat>();

    /// <summary>🎯 메타 보정(Brier/RMSE · 반사실 후회 · 탐색 배수) 현황(기본 구현 → 기록 없음).</summary>
    PlutoCalibrationStatus GetCalibrationStatus() => PlutoCalibrationStatus.Empty;

    /// <summary>🔁 능동 후회 리허설 대상(오답 노트 차단 제외 · 부스트 내림차순 · 기본 구현 → 없음).</summary>
    IReadOnlyList<PlutoRegretRecoveryBoost> GetRegretRecoveryBoosts() => Array.Empty<PlutoRegretRecoveryBoost>();

    /// <summary>⚖️ 파레토 프런티어 선두 훅(다목적 근거 없음 → null).</summary>
    PlutoParetoLeader? GetParetoFrontierLeader() => null;

    /// <summary>⚖️ 전 모달리티(훅 · 룩 · BGM · 태그 순) 파레토 현황(기본 구현 → 없음).</summary>
    IReadOnlyList<PlutoParetoModalityStatus> GetParetoModalityStatus() => Array.Empty<PlutoParetoModalityStatus>();

    /// <summary>
    /// 🧬 (Domain, RenderMode) 컨텍스트 랭킹 — 콜드스타트(지역 시행 &lt; 3) 컨텍스트는 지역 추정을 검증된 전역 사후로 수축해 반영(기본 구현 → 전역 순위).
    /// 지역 근거가 없으면 전역 순위와 완전히 같다(사전 100% 이식).
    /// </summary>
    IReadOnlyList<PlutoRankedOption> RankForContext(PlutoPatternKind kind, string? domain, string? renderMode, IEnumerable<string>? candidates = null) => RankOptions(kind, candidates);

    /// <summary>🧬 콜드스타트 전이 현황(기본 구현 → 기록 없음).</summary>
    PlutoColdStartTransferStatus GetColdStartTransferStatus() => PlutoColdStartTransferStatus.Empty;
}

/// <summary>
/// 🧠 <see cref="IPlutoAdaptivePatternRanker"/> 구현(순수 BCL · 결정론).
///   ▸ BaseScore = 1 − 0.005 × min(순번, 20)(호출자 후보 순서 = 사전 선호 · 최소 0.9) → 학습 배수 ≥ 1.15 후보는 항상 미학습 후보를 앞섬.
///   ▸ 감점 계수 = 오답 노트 유효 벌점 / 2(차단 토큰은 ≥ 0.85) · 점수에는 [0, 0.85] 클램프로 반영.
///   ▸ 타이브레이커 = FNV-1a(키) → [0, 1e-6) — 완전 동점만 결정론적으로 분리.
///   ▸ 🎰 UCB1 = FinalScore + C × √(ln(전체 시행 + 1) / (시행 + 1)) — 활용 순위(FinalScore)는 불변 · 탐색 순위는 <see cref="RankForExploration"/>.
///   ▸ ⏳ 학습 배수 · 시행 수는 14일 반감기 · 오답 감점은 21일 반감기(각 서비스의 주입 시계 기준 → 재현 가능).
///   ▸ 🔗 조합 시너지 = <c>pair:</c>/<c>domain_hook:</c> 학습 배수 × (1 − clamp(조합 감점, 0, 0.85)).
///   ▸ 🌊 레짐 드리프트 감지 시 탐색 상수 C × 탐색 온도(1~3) — 활용 순위(FinalScore)는 불변 · 탐색 순위만 공격적으로.
///   ▸ 🎯 후보마다 Normal–Normal 사후 신뢰 구간(<see cref="PlutoPosteriorConfidence"/>) 동봉 — '검증된 승자'와 '1회 급등' 구분.
///   ▸ ⚖️ 전 모달리티(훅 · 룩 · BGM 무드 · 스타일 태그): 다목적 근거(신선도 반영 표본 ≥ <see cref="PlutoParetoFrontier.MinObjectiveSamples"/>)가 있는 후보는 FinalScore × 파레토 배수(티어 · 최약 축 보너스) —
///     클릭베이트 급등은 학습 부스트(배수 &gt; 1)를 무효화하고 ×<see cref="PlutoParetoFrontier.ClickbaitFactor"/> 강등. 근거 없는 후보는 완전 불변.
///   ▸ 🧬 <see cref="RankForContext"/>: 훅 · 룩을 (Domain, RenderMode) 지역 잔차 알파로 특화 — 콜드스타트는 <see cref="PlutoEmpiricalBayes"/> 수축(전역 순위 · 기존 API 는 불변).
/// </summary>
public sealed class PlutoAdaptivePatternRanker : IPlutoAdaptivePatternRanker
{
    public const double Epsilon = 1e-9;
    public const double MaxPenaltyFactor = 0.85;
    public const double PenaltyNormalizer = 2.0;
    public const double OrderPriorStep = 0.005;
    public const int OrderPriorCap = 20;
    public const double TieBreakerScale = 1e-6;
    public const int DefaultMaxTags = 8;
    /// <summary>🎰 UCB1 탐색 상수 C — 미검증 후보가 평범한(×1.15대) 다회 검증 후보는 넘고 강한 승자(×1.5+)는 넘지 못하는 크기.</summary>
    public const double ExplorationC = 0.5;
    /// <summary>🎰 탐색 후보 판정 시행 상한(미만 → 덜 검증됨).</summary>
    public const double UnderSampledTrials = 2.0;
    /// <summary>🎚️ 탐색 상수 합산 배수(드리프트 온도 × 메타 보정 배수) 상한.</summary>
    public const double MaxExplorationMultiplier = 4.0;

    /// <summary>🎯 메타 보정 서비스(미연결 → 배수 1.0 · 기존 동작과 동일).</summary>
    public IPlutoMetaCalibrationService? MetaCalibration { get; init; }

    public const string HookPrefix = "hook:", LookPrefix = "look:", BgmPrefix = "bgm:", StylePrefix = "style:";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] DefaultLooks =
    {
        PlutoColorGradingCompiler.NaturalPastelName, PlutoColorGradingCompiler.GoldenHourWarmName, PlutoColorGradingCompiler.VintageFilm16mmName,
    };
    private static readonly string[] DefaultBgmMoods = BuildDefaultBgm();

    private readonly PlutoLearningService? _learning;
    private readonly IPlutoMistakeNotebookService? _notebook;

    public PlutoAdaptivePatternRanker(PlutoLearningService? learning, IPlutoMistakeNotebookService? notebook)
    {
        _learning = learning;
        _notebook = notebook;
    }

    public static string PrefixOf(PlutoPatternKind kind) => kind switch
    {
        PlutoPatternKind.Hook => HookPrefix,
        PlutoPatternKind.Look => LookPrefix,
        PlutoPatternKind.BgmMood => BgmPrefix,
        _ => StylePrefix,
    };

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        try { if (_learning is not null) await _learning.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] learning load: " + ex.Message); }   // [부분 구제] 기본 가중치
        try { if (_notebook is not null) await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] notebook load: " + ex.Message); }
        try { if (MetaCalibration is not null) await MetaCalibration.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] calibration load: " + ex.Message); }   // [부분 구제] 배수 1.0
    }

    public PlutoAdaptiveRecommendation GetCurrentRecommendations(IEnumerable<string>? candidateHooks = null, IEnumerable<string>? candidateLooks = null,
        IEnumerable<string>? candidateBgmMoods = null, IEnumerable<string>? candidateTags = null)
    {
        var suppressed = SafeSuppressed();
        var hooks = Rank(PlutoPatternKind.Hook, candidateHooks, suppressed);
        var looks = Rank(PlutoPatternKind.Look, candidateLooks, suppressed);
        var bgms = Rank(PlutoPatternKind.BgmMood, candidateBgmMoods, suppressed);
        var tags = Rank(PlutoPatternKind.StyleTag, candidateTags, suppressed);
        var topTags = tags.Count <= DefaultMaxTags ? tags : tags.GetRange(0, DefaultMaxTags);
        var history = AnyReinforced(hooks) || AnyReinforced(looks) || AnyReinforced(bgms) || AnyReinforced(tags) || HasLearnedReinforcement();
        return new PlutoAdaptiveRecommendation(
            hooks.Count > 0 ? hooks[0] : Neutral(HookPrefix, "montage-hook"),
            looks.Count > 0 ? looks[0] : Neutral(LookPrefix, PlutoColorGradingCompiler.NaturalPastelName),
            bgms.Count > 0 ? bgms[0] : Neutral(BgmPrefix, "none"),
            topTags, history);
    }

    public IReadOnlyList<PlutoRankedOption> RankOptions(PlutoPatternKind kind, IEnumerable<string>? candidates = null)
        => Rank(kind, candidates, SafeSuppressed());

    public IReadOnlyList<PlutoRankedOption> RankForExploration(PlutoPatternKind kind, IEnumerable<string>? candidates = null)
    {
        var list = Rank(kind, candidates, SafeSuppressed());
        list.Sort(static (a, b) =>
        {
            var c = b.UcbScore.CompareTo(a.UcbScore);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return list;
    }

    /// <summary>
    /// 🎰 UCB1(순수 함수) — <c>posteriorMean + explorationC × √(ln(totalTrials + 1) / (armTrials + 1))</c>.
    /// 전체 시행 0 → 보너스 0(이력 없는 시스템은 활용 순위 그대로) · 비유한/음수 입력은 0으로 정규화.
    /// </summary>
    public static double ComputeUcbScore(double posteriorMean, double explorationC, double totalTrials, double armTrials)
        => posteriorMean + ExplorationBonusFor(explorationC, totalTrials, armTrials);

    /// <summary>🎰 UCB1 탐색 보너스 항.</summary>
    public static double ExplorationBonusFor(double explorationC, double totalTrials, double armTrials)
    {
        var total = double.IsFinite(totalTrials) && totalTrials > 0 ? totalTrials : 0.0;
        var arm = double.IsFinite(armTrials) && armTrials > 0 ? armTrials : 0.0;
        var c = double.IsFinite(explorationC) && explorationC > 0 ? explorationC : 0.0;
        return c * Math.Sqrt(Math.Log(total + 1.0) / (arm + 1.0));
    }

    public PlutoPairSynergy GetPairSynergy(string? hook, string? look)
        => PlutoPairAttribution.PairKey(hook, look) is { } key
            ? Synergy(key, PlutoPairAttribution.HookPart(hook!), PlutoPairAttribution.SidePart(look!), SafeCompositeSuppressed())
            : PlutoPairSynergy.Neutral;

    public PlutoPairSynergy GetDomainHookSynergy(string? domain, string? hook)
        => PlutoPairAttribution.DomainHookKey(domain, hook) is { } key
            ? Synergy(key, PlutoPairAttribution.SidePart(domain!), PlutoPairAttribution.HookPart(hook!), SafeCompositeSuppressed())
            : PlutoPairSynergy.Neutral;

    public PlutoPairSynergy? GetTopSynergyPair()
    {
        var learned = SafeLearned(PlutoPairAttribution.PairPrefix);
        if (learned.Count == 0) return null;
        var suppressed = SafeCompositeSuppressed();
        PlutoPairSynergy? best = null;
        for (var i = 0; i < learned.Count; i++)
        {
            var key = PlutoPairAttribution.PairPrefix + learned[i].Key;
            if (!PlutoPairAttribution.TrySplitPair(key, out var hook, out var look)) continue;
            var s = Synergy(key, hook, look, suppressed);
            if (s.IsAntiSynergy || s.Score <= 1.0 + Epsilon) continue;
            if (best is null || s.Score > best.Score || (s.Score == best.Score && string.CompareOrdinal(s.PairKey, best.PairKey) < 0)) best = s;
        }
        return best;
    }

    public PlutoRegimeDriftStatus GetRegimeDriftStatus()
    {
        try { return _learning?.GetRegimeDriftStatus() ?? PlutoRegimeDriftStatus.Stable; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] drift: " + ex.Message); return PlutoRegimeDriftStatus.Stable; }
    }

    public IReadOnlyList<PlutoContextBaselineStat> GetContextBaselines()
    {
        try { return _learning?.GetContextBaselines() ?? Array.Empty<PlutoContextBaselineStat>(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] baselines: " + ex.Message); return Array.Empty<PlutoContextBaselineStat>(); }
    }

    /// <summary>
    /// 🌊🎚️ 현재 유효 탐색 상수 = <see cref="ExplorationC"/> × min(<see cref="MaxExplorationMultiplier"/>, 탐색 온도 × 메타 보정 배수).
    /// 서지 없음 + 메타 보정 미연결/표본 부족 → C 그대로. 레짐 서지 중에는 메타 수축(&lt; 1)을 무시(탐색을 깎지 않음).
    /// </summary>
    public double EffectiveExplorationC()
    {
        var temperature = 1.0;
        try { temperature = PlutoRegimeDriftDetector.ExplorationTemperature(_learning?.GetExplorationSurge() ?? 0.0); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] surge: " + ex.Message); }
        return ExplorationC * CombineExplorationMultiplier(temperature, SafeCalibrationScale());
    }

    /// <summary>🎚️ 드리프트 온도 × 메타 보정 배수 결합(순수 함수) — 비유한/비양수 → 1 · 서지(온도 &gt; 1) 중 수축 금지 · 상한 ×4.</summary>
    public static double CombineExplorationMultiplier(double temperature, double calibrationScale)
    {
        var t = double.IsFinite(temperature) && temperature > 0 ? temperature : 1.0;
        var s = double.IsFinite(calibrationScale) && calibrationScale > 0 ? calibrationScale : 1.0;
        if (t > 1.0 + Epsilon) s = Math.Max(1.0, s);
        return Math.Min(MaxExplorationMultiplier, t * s);
    }

    private double SafeCalibrationScale()
    {
        if (MetaCalibration is null) return 1.0;
        try { return MetaCalibration.GetCalibratedExplorationScale(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] calibration scale: " + ex.Message); return 1.0; }
    }

    public IReadOnlyList<PlutoRegretRecoveryBoost> GetRegretRecoveryBoosts()
    {
        if (MetaCalibration is null) return Array.Empty<PlutoRegretRecoveryBoost>();
        try
        {
            HashSet<string>? blocked = null;
            if (SafeSuppressed() is { Count: > 0 } s) blocked = new HashSet<string>(s, StringComparer.OrdinalIgnoreCase);
            if (SafeCompositeSuppressed() is { Count: > 0 } c) { blocked ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase); blocked.UnionWith(c); }
            return MetaCalibration.GetRegretRecoveryBoosts(blocked) ?? Array.Empty<PlutoRegretRecoveryBoost>();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] regret recovery: " + ex.Message); return Array.Empty<PlutoRegretRecoveryBoost>(); }
    }

    public PlutoParetoLeader? GetParetoFrontierLeader() => ParetoStatus(PlutoPatternKind.Hook, SafeSuppressed()).Leader;

    public IReadOnlyList<PlutoParetoModalityStatus> GetParetoModalityStatus()
    {
        var suppressed = SafeSuppressed();
        return new[]
        {
            ParetoStatus(PlutoPatternKind.Hook, suppressed), ParetoStatus(PlutoPatternKind.Look, suppressed),
            ParetoStatus(PlutoPatternKind.BgmMood, suppressed), ParetoStatus(PlutoPatternKind.StyleTag, suppressed),
        };
    }

    /// <summary>⚖️ 모달리티 1종의 프런티어 집계 — 후보 = 기본 후보 + 학습된 토큰(태그는 학습된 토큰만).</summary>
    private PlutoParetoModalityStatus ParetoStatus(PlutoPatternKind kind, IReadOnlySet<string>? suppressed)
    {
        var hooks = Rank(kind, null, suppressed);
        PlutoRankedOption? leader = null;
        int frontier = 0, evidenced = 0, clickbait = 0;
        HashSet<string>? bare = null;   // 태그 '#x' · 'x' 변형은 같은 근거를 공유 → 1개로 집계
        if (kind == PlutoPatternKind.StyleTag)
        {
            bare = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < hooks.Count; i++) if (hooks[i].DisplayLabel is { Length: > 0 } l && l[0] != '#') bare.Add(l);
        }
        for (var i = 0; i < hooks.Count; i++)
        {
            var o = hooks[i];
            if (!o.Pareto.HasEvidence) continue;
            if (bare is not null && o.DisplayLabel.Length > 1 && o.DisplayLabel[0] == '#' && bare.Contains(o.DisplayLabel[1..])) continue;
            evidenced++;
            if (o.IsClickbaitSpike) { clickbait++; continue; }
            if (!o.Pareto.IsFrontier) continue;
            frontier++;
            if (!o.IsPenalized && leader is null) leader = o;   // 목록은 FinalScore 내림차순 → 첫 프런티어 = 선두
        }
        return new PlutoParetoModalityStatus(kind,
            leader is null ? null : new PlutoParetoLeader(leader.DisplayLabel, leader.Objectives, leader.ObjectiveSamples, frontier, evidenced, clickbait),
            evidenced, frontier, clickbait);
    }

    /// <summary>⚖️ 다목적 성과 조회 — 태그는 '#' 유무 변형을 표본 가중 평균으로 병합(가중치 · 시행 수와 같은 변형 규칙).</summary>
    private (PlutoObjectiveVector Objectives, double Samples) SafeObjectives(string prefix, string label, bool tagVariants = false)
    {
        if (_learning is null) return (default, 0.0);
        try
        {
            var (v, n) = _learning.GetPatternObjectives(prefix + label);
            if (!tagVariants || TagVariant(label) is not { } alt) return (v, n);
            var (v2, n2) = _learning.GetPatternObjectives(prefix + alt);
            if (!(n2 > 0)) return (v, n);
            if (!(n > 0)) return (v2, n2);
            var total = n + n2;
            return (new PlutoObjectiveVector((v.Hook * n + v2.Hook * n2) / total, (v.Completion * n + v2.Completion * n2) / total, (v.Ctr * n + v2.Ctr * n2) / total), total);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] objectives: " + ex.Message); return (default, 0.0); }
    }

    // ── 🧬 계층적 경험적 베이즈 — 컨텍스트 랭킹 · 콜드스타트 전이 ──

    public IReadOnlyList<PlutoRankedOption> RankForContext(PlutoPatternKind kind, string? domain, string? renderMode, IEnumerable<string>? candidates = null)
    {
        var list = Rank(kind, candidates, SafeSuppressed());
        if (_learning is null || kind is not (PlutoPatternKind.Hook or PlutoPatternKind.Look) || !PlutoContextualBaseline.IsExplicit(domain, renderMode)) return list;
        var prefix = PrefixOf(kind);
        var adjusted = false;
        for (var i = 0; i < list.Count; i++)
        {
            var o = list[i];
            var est = SafeContextEstimate(prefix + o.DisplayLabel, domain, renderMode);
            if (!est.HasEvidence) continue;
            var mult = PlutoEmpiricalBayes.ContextMultiplier(est);
            var changed = Math.Abs(mult - 1.0) > Epsilon;
            list[i] = o with { ContextTransfer = est, ContextMultiplier = mult, FinalScore = changed ? o.FinalScore * mult : o.FinalScore };
            adjusted |= changed;
        }
        if (adjusted)
            list.Sort(static (a, b) =>
            {
                var c = b.FinalScore.CompareTo(a.FinalScore);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
        return list;
    }

    private PlutoEmpiricalBayesEstimate SafeContextEstimate(string token, string? domain, string? renderMode)
    {
        if (_learning is null) return PlutoEmpiricalBayesEstimate.None;
        try
        {
            var (localMean, localN) = _learning.GetContextPatternAlpha(domain, renderMode, token);
            var (globalMean, globalVar, globalN) = _learning.GetPatternAlphaStats(token);
            return PlutoEmpiricalBayes.Shrink(localMean, localN, globalMean, globalN, globalVar);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] context estimate: " + ex.Message); return PlutoEmpiricalBayesEstimate.None; }
    }

    public PlutoColdStartTransferStatus GetColdStartTransferStatus()
    {
        if (_learning is null) return PlutoColdStartTransferStatus.Empty;
        try
        {
            var contexts = _learning.GetContextBaselines();
            if (contexts.Count == 0) return PlutoColdStartTransferStatus.Empty;
            var priors = _learning.GetProvenPatternPriors(PlutoEmpiricalBayes.MinProvenSamples);
            var cold = 0;
            PlutoContextBaselineStat? focus = null;   // 표본 내림차순 목록 → 가장 덜 차가운 콜드스타트 컨텍스트(곧 독립할 후보)
            for (var i = 0; i < contexts.Count; i++)
            {
                if (contexts[i].Samples >= PlutoEmpiricalBayes.ColdStartTrials) continue;
                cold++;
                focus ??= contexts[i];
            }
            var weightPct = 0.0;
            if (focus is not null && priors.Count > 0)
            {
                var sum = 0.0;
                for (var i = 0; i < priors.Count; i++)
                {
                    var (localMean, localN) = _learning.GetContextPatternAlpha(focus.Domain, focus.RenderMode, priors[i].Token);
                    sum += PlutoEmpiricalBayes.Shrink(localMean, localN, priors[i].Alpha, priors[i].Samples, priors[i].Variance).GlobalWeight;
                }
                weightPct = Math.Round(sum / priors.Count * 100.0, 0, MidpointRounding.AwayFromZero);
            }
            return new PlutoColdStartTransferStatus(contexts.Count, cold, priors.Count, focus is null ? string.Empty : focus.Domain + "·" + focus.RenderMode, focus?.Samples ?? 0.0, weightPct);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] cold start: " + ex.Message); return PlutoColdStartTransferStatus.Empty; }
    }

    public PlutoCalibrationStatus GetCalibrationStatus()
    {
        try { return MetaCalibration?.GetStatus() ?? PlutoCalibrationStatus.Empty; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] calibration: " + ex.Message); return PlutoCalibrationStatus.Empty; }
    }

    private double SafeResidualAlpha(string prefix, string label)
    {
        if (_learning is null) return 0.0;
        try { var (a, n) = _learning.GetPatternAlpha(prefix + label); return n > 0 && double.IsFinite(a) ? a : 0.0; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] alpha: " + ex.Message); return 0.0; }
    }

    public PlutoExplorationTelemetry GetExplorationTelemetry()
    {
        var suppressed = SafeSuppressed();
        var hooks = Rank(PlutoPatternKind.Hook, null, suppressed);
        var looks = Rank(PlutoPatternKind.Look, null, suppressed);
        double total = 0, bonus = 0, ucb = 0;
        int arms = 0, candidates = 0;
        Accumulate(hooks, ref total, ref bonus, ref ucb, ref arms, ref candidates);
        Accumulate(looks, ref total, ref bonus, ref ucb, ref arms, ref candidates);
        var rate = ucb > 0 ? Math.Round(bonus / ucb * 100.0, 1, MidpointRounding.AwayFromZero) : 0.0;
        return new PlutoExplorationTelemetry(Math.Round(total, 2, MidpointRounding.AwayFromZero), arms, candidates, rate, TopByUcb(hooks), TopByUcb(looks));
    }

    private static void Accumulate(List<PlutoRankedOption> list, ref double total, ref double bonus, ref double ucb, ref int arms, ref int candidates)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var o = list[i];
            total += o.ArmTrials;
            if (o.IsPenalized) continue;
            arms++;
            bonus += o.ExplorationBonus;
            ucb += o.UcbScore;
            if (o.ArmTrials < UnderSampledTrials) candidates++;
        }
    }

    private static string TopByUcb(List<PlutoRankedOption> list)
    {
        PlutoRankedOption? best = null;
        for (var i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o.IsPenalized) continue;
            if (best is null || o.UcbScore > best.UcbScore) best = o;
        }
        return best?.DisplayLabel ?? string.Empty;
    }

    private PlutoPairSynergy Synergy(string key, string first, string second, IReadOnlySet<string>? compositeSuppressed)
    {
        var mult = PlutoLearningService.PatternBaseWeight;
        var pf = 0.0;
        try
        {
            if (_learning is not null)
            {
                var w = _learning.GetPatternWeight(key);
                if (double.IsFinite(w) && w > 0) mult = w;
            }
            if (_notebook is not null)
            {
                var p = _notebook.GetEffectivePenalty(key);
                if (double.IsFinite(p) && p > 0) pf = p / PenaltyNormalizer;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] synergy: " + ex.Message); }
        if (compositeSuppressed is { Count: > 0 } && compositeSuppressed.Contains(key)) pf = Math.Max(pf, MaxPenaltyFactor);
        return new PlutoPairSynergy(key, first, second, mult, pf, mult * (1.0 - Math.Clamp(pf, 0.0, MaxPenaltyFactor)));
    }

    private IReadOnlySet<string>? SafeCompositeSuppressed()
    {
        try { return _notebook?.GetSuppressedCompositeKeys(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] composite suppressed: " + ex.Message); return null; }
    }

    public IReadOnlyList<string> RankTags(IEnumerable<string> tags, int maxCount = DefaultMaxTags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (maxCount <= 0) return Array.Empty<string>();
        var ranked = Rank(PlutoPatternKind.StyleTag, tags, SafeSuppressed());
        var n = Math.Min(maxCount, ranked.Count);
        var result = new string[n];
        for (var i = 0; i < n; i++) result[i] = ranked[i].DisplayLabel;
        return result;
    }

    string IPlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId(string? topBgmMoodKeyOrLabel, IReadOnlyList<string> availableTrackIds, string fallbackTrackId)
        => ResolveRecommendedBgmTrackId(topBgmMoodKeyOrLabel, availableTrackIds, fallbackTrackId);

    // ── 🎵 무드 → 트랙 ──

    private enum BgmMoodGroup { None, Calm, Bright, Warm, Energetic }

    /// <summary>무드 별칭(소문자 부분 일치 · 선언 순서 우선).</summary>
    private static readonly (string Keyword, BgmMoodGroup Group)[] BgmMoodAliases =
    {
        ("lullaby", BgmMoodGroup.Calm), ("calm", BgmMoodGroup.Calm), ("piano", BgmMoodGroup.Calm), ("sleep", BgmMoodGroup.Calm), ("soft", BgmMoodGroup.Calm), ("자장가", BgmMoodGroup.Calm), ("잔잔", BgmMoodGroup.Calm),
        ("musicbox", BgmMoodGroup.Bright), ("music box", BgmMoodGroup.Bright), ("upbeat", BgmMoodGroup.Bright), ("playful", BgmMoodGroup.Bright), ("bright", BgmMoodGroup.Bright), ("오르골", BgmMoodGroup.Bright), ("경쾌", BgmMoodGroup.Bright),
        ("acoustic", BgmMoodGroup.Warm), ("cinematic", BgmMoodGroup.Warm), ("warm", BgmMoodGroup.Warm), ("emotional", BgmMoodGroup.Warm), ("guitar", BgmMoodGroup.Warm), ("어쿠스틱", BgmMoodGroup.Warm), ("감성", BgmMoodGroup.Warm),
        ("energetic", BgmMoodGroup.Energetic), ("pop", BgmMoodGroup.Energetic), ("dance", BgmMoodGroup.Energetic), ("hype", BgmMoodGroup.Energetic), ("신나", BgmMoodGroup.Energetic),
    };

    /// <summary>
    /// 🎵 결정론 무드 → 트랙 해석 — 접두 <c>bgm:</c> 제거 · 대소문자 무시 정확 일치 → 별칭 그룹 일치(Energetic 전용 트랙 없으면 Bright 근사).
    /// 무드 없음 · <c>custom</c>/<c>none</c> · 불일치 · 빈 목록 → <paramref name="fallbackTrackId"/>.
    /// </summary>
    public static string ResolveRecommendedBgmTrackId(string? topBgmMoodKeyOrLabel, IReadOnlyList<string> availableTrackIds, string fallbackTrackId)
    {
        if (availableTrackIds is null || availableTrackIds.Count == 0 || string.IsNullOrWhiteSpace(topBgmMoodKeyOrLabel)) return fallbackTrackId;
        var mood = topBgmMoodKeyOrLabel.Trim();
        if (mood.StartsWith(BgmPrefix, StringComparison.OrdinalIgnoreCase)) mood = mood[BgmPrefix.Length..].Trim();
        if (mood.Length == 0) return fallbackTrackId;

        for (var i = 0; i < availableTrackIds.Count; i++)
        {
            var id = availableTrackIds[i];
            if (!string.IsNullOrWhiteSpace(id) && string.Equals(id.Trim(), mood, StringComparison.OrdinalIgnoreCase)) return id;
        }

        var group = MoodGroupOf(mood);
        if (group == BgmMoodGroup.None) return fallbackTrackId;
        var match = FirstTrackInGroup(availableTrackIds, group);
        if (match is null && group == BgmMoodGroup.Energetic) match = FirstTrackInGroup(availableTrackIds, BgmMoodGroup.Bright);   // 근사 폴백
        return match ?? fallbackTrackId;
    }

    private static string? FirstTrackInGroup(IReadOnlyList<string> ids, BgmMoodGroup group)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (!string.IsNullOrWhiteSpace(id) && MoodGroupOf(id) == group) return id;
        }
        return null;
    }

    private static BgmMoodGroup MoodGroupOf(string text)
    {
        for (var i = 0; i < BgmMoodAliases.Length; i++)
            if (text.Contains(BgmMoodAliases[i].Keyword, StringComparison.OrdinalIgnoreCase)) return BgmMoodAliases[i].Group;
        return BgmMoodGroup.None;
    }

    // ── 🧮 점수 ──

    /// <summary>🧮 결정론 점수 — BaseScore × 배수 × (1 − clamp(감점, 0, 0.85)) + 타이브레이커.</summary>
    public static double ComputeFinalScore(double baseScore, double multiplier, double penaltyFactor, string key)
        => baseScore * multiplier * (1.0 - Math.Clamp(penaltyFactor, 0.0, MaxPenaltyFactor)) + TieBreaker(key);

    /// <summary>순번 → 사전 선호 점수(1.0 → 0.9).</summary>
    public static double BaseScoreFor(int index) => 1.0 - OrderPriorStep * Math.Min(Math.Max(0, index), OrderPriorCap);

    /// <summary>🎲 결정론 탐색 타이브레이커(FNV-1a) — [0, <see cref="TieBreakerScale"/>).</summary>
    public static double TieBreaker(string key)
    {
        var h = 2166136261u;
        for (var i = 0; i < key.Length; i++) { h ^= key[i]; h *= 16777619u; }
        return (h % 1_000_000u) / 1_000_000.0 * TieBreakerScale;
    }

    /// <summary>🏷️ 근거 배지 — 감점 우선 · 강화 · 기본 탐색.</summary>
    public static string BadgeFor(double multiplier, double penaltyFactor)
    {
        var reinforced = multiplier > 1.0 + Epsilon;
        var penalized = penaltyFactor > Epsilon;
        if (reinforced && penalized) return "🔥 학습 ×" + multiplier.ToString("0.00", Inv) + " · ⚠️ 오답 감점 −" + PenaltyPct(penaltyFactor);
        if (penalized) return "⚠️ 오답 감점 −" + PenaltyPct(penaltyFactor);
        if (reinforced) return "🔥 학습 ×" + multiplier.ToString("0.00", Inv) + " 반영";
        return "✨ 기본 탐색";
    }

    private static string PenaltyPct(double f) => (Math.Clamp(f, 0.0, MaxPenaltyFactor) * 100).ToString("0", Inv) + "%";

    private List<PlutoRankedOption> Rank(PlutoPatternKind kind, IEnumerable<string>? candidates, IReadOnlySet<string>? suppressed)
    {
        var prefix = PrefixOf(kind);
        var labels = new List<string>(16);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (candidates is not null)
        {
            foreach (var c in candidates) AddCandidate(labels, seen, c);
        }
        else
        {
            switch (kind)
            {
                case PlutoPatternKind.Hook: foreach (var h in PlutoMontageStoryGenerator.AllTemplateHookLines) AddCandidate(labels, seen, h); break;
                case PlutoPatternKind.Look: foreach (var l in DefaultLooks) AddCandidate(labels, seen, l); break;
                case PlutoPatternKind.BgmMood: foreach (var b in DefaultBgmMoods) AddCandidate(labels, seen, b); break;
            }
            foreach (var kv in SafeLearned(prefix)) AddCandidate(labels, seen, kv.Key);   // 학습된 토큰(기본 후보 밖)도 후보 편입
        }

        var isTag = kind == PlutoPatternKind.StyleTag;
        var trials = new double[labels.Count];
        var totalTrials = 0.0;
        for (var i = 0; i < labels.Count; i++)
        {
            trials[i] = ArmTrials(prefix, labels[i], isTag);
            totalTrials += trials[i];
        }
        var list = new List<PlutoRankedOption>(labels.Count);
        var explorationC = EffectiveExplorationC();   // 🌊 레짐 드리프트 → 탐색 서지
        // ⚖️ 다목적 파레토 티어 — 전 모달리티(훅 · 룩 · BGM 무드 · 스타일 태그) · 같은 종류의 근거 있는 후보끼리 비지배 정렬
        PlutoObjectiveVector[]? objectives = null; double[]? objectiveN = null; int[]? tiers = null;
        if (_learning is not null && labels.Count > 0)
        {
            objectives = new PlutoObjectiveVector[labels.Count];
            objectiveN = new double[labels.Count];
            var evidence = new bool[labels.Count];
            var any = false;
            for (var i = 0; i < labels.Count; i++)
            {
                (objectives[i], objectiveN[i]) = SafeObjectives(prefix, labels[i], isTag);
                evidence[i] = objectiveN[i] >= PlutoParetoFrontier.MinObjectiveSamples;
                any |= evidence[i];
            }
            if (any) tiers = PlutoParetoFrontier.ComputeTiers(objectives, evidence);
        }
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var key = prefix + label;
            var mult = LearnedWeight(prefix, label, isTag);
            var pf = PenaltyFactor(label, isTag, suppressed);
            var b = BaseScoreFor(i);
            var pareto = tiers is null ? PlutoParetoAssessment.None : PlutoParetoFrontier.Assess(tiers[i], objectives![i]);
            // 🎣 클릭베이트 급등은 학습 부스트를 점수에 싣지 않는다(배수 자체 · 사후 신뢰는 그대로 노출)
            var scoreMult = (pareto.IsClickbaitSpike ? Math.Min(mult, PlutoLearningService.PatternBaseWeight) : mult) * pareto.Multiplier;
            list.Add(new PlutoRankedOption(key, label, b, mult, pf, ComputeFinalScore(b, scoreMult, pf, key), BadgeFor(mult, pf))
            {
                ArmTrials = trials[i],
                ExplorationBonus = ExplorationBonusFor(explorationC, totalTrials, trials[i]),
                Posterior = PlutoPosteriorConfidence.Compute(mult, trials[i]),
                ResidualAlpha = SafeResidualAlpha(prefix, label),
                Pareto = pareto,
                Objectives = pareto.HasEvidence ? objectives![i] : default,
                ObjectiveSamples = pareto.HasEvidence ? objectiveN![i] : 0.0,
            });
        }
        list.Sort(static (a, b) =>
        {
            var c = b.FinalScore.CompareTo(a.FinalScore);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return list;
    }

    private static void AddCandidate(List<string> labels, HashSet<string> seen, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var v = raw.Trim();
        if (v.Length > PlutoMistakeNotebookService.MaxTokenLength) v = v[..PlutoMistakeNotebookService.MaxTokenLength];   // 강화 서비스와 동일 절단
        if (seen.Add(v)) labels.Add(v);
    }

    /// <summary>학습 배수 — 태그는 '#' 유무 변형 중 최대.</summary>
    private double LearnedWeight(string prefix, string label, bool tagVariants)
    {
        if (_learning is null) return PlutoLearningService.PatternBaseWeight;
        try
        {
            var w = _learning.GetPatternWeight(prefix + label);
            if (tagVariants && TagVariant(label) is { } alt) w = Math.Max(w, _learning.GetPatternWeight(prefix + alt));
            return double.IsFinite(w) && w > 0 ? w : PlutoLearningService.PatternBaseWeight;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] weight: " + ex.Message); return PlutoLearningService.PatternBaseWeight; }
    }

    /// <summary>🎰 유효 시행 수(반감기 감쇠) — 태그는 '#' 유무 변형 합산.</summary>
    private double ArmTrials(string prefix, string label, bool tagVariants)
    {
        if (_learning is null) return 0.0;
        try
        {
            var n = _learning.GetPatternTrials(prefix + label);
            if (tagVariants && TagVariant(label) is { } alt) n += _learning.GetPatternTrials(prefix + alt);
            return double.IsFinite(n) && n > 0 ? n : 0.0;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] trials: " + ex.Message); return 0.0; }
    }

    /// <summary>감점 계수 = 유효 벌점 / 2 · 차단 토큰 ≥ 0.85(룩은 프리셋 별칭 차단 포함).</summary>
    private double PenaltyFactor(string label, bool tagVariants, IReadOnlySet<string>? suppressed)
    {
        if (_notebook is null) return 0.0;
        double p;
        try
        {
            p = _notebook.GetEffectivePenalty(label);
            if (tagVariants && TagVariant(label) is { } alt) p = Math.Max(p, _notebook.GetEffectivePenalty(alt));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] penalty: " + ex.Message); return 0.0; }
        var f = double.IsFinite(p) && p > 0 ? p / PenaltyNormalizer : 0.0;
        if (suppressed is { Count: > 0 } && (suppressed.Contains(label) || (tagVariants && TagVariant(label) is { } a2 && suppressed.Contains(a2))))
            f = Math.Max(f, MaxPenaltyFactor);
        return f;
    }

    private static string? TagVariant(string label)
    {
        if (label.Length > 1 && label[0] == '#') return label[1..];
        return label.Length > 0 && label[0] != '#' ? "#" + label : null;
    }

    private IReadOnlySet<string>? SafeSuppressed()
    {
        try { return _notebook?.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] suppressed: " + ex.Message); return null; }
    }

    private IReadOnlyList<KeyValuePair<string, double>> SafeLearned(string prefix)
    {
        if (_learning is null) return Array.Empty<KeyValuePair<string, double>>();
        try { return _learning.GetPatternWeights(prefix); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Ranker] learned: " + ex.Message); return Array.Empty<KeyValuePair<string, double>>(); }
    }

    private bool HasLearnedReinforcement()
    {
        for (var k = 0; k < 4; k++)
        {
            var learned = SafeLearned(PrefixOf((PlutoPatternKind)k));
            for (var i = 0; i < learned.Count; i++) if (learned[i].Value > PlutoLearningService.PatternBaseWeight + Epsilon) return true;
        }
        return false;
    }

    private static bool AnyReinforced(List<PlutoRankedOption> list)
    {
        for (var i = 0; i < list.Count; i++) if (list[i].IsReinforced) return true;
        return false;
    }

    private static PlutoRankedOption Neutral(string prefix, string label)
        => new(prefix + label, label, 1.0, 1.0, 0.0, 1.0, BadgeFor(1.0, 0.0));

    private static string[] BuildDefaultBgm()
    {
        var names = Enum.GetNames<PromptFlowStudio.Services.Export.BabyBgmTrack>();
        var arr = new string[names.Length + 2];
        Array.Copy(names, arr, names.Length);
        arr[^2] = "custom";
        arr[^1] = "none";
        return arr;
    }
}
