using System;
using System.Collections.Generic;
using System.Globalization;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🧬 계층적 경험적 베이즈 추정 1건(잔차 알파 척도 · 합성 점수 0~1) — 지역 = (Domain, RenderMode) 컨텍스트 안의 패턴 성과 · 전역 = 전 컨텍스트 패턴 사후.
///   ▸ <see cref="LocalWeight"/> = α = localTrials / (localTrials + priorStrength) · <see cref="ShrunkMean"/> = α·지역 + (1 − α)·사전 평균.
///   ▸ 성숙 컨텍스트(지역 시행 ≥ <see cref="PlutoEmpiricalBayes.ColdStartTrials"/>) → α = 1(지역 데이터를 전역이 덮지 않는다).
/// </summary>
public sealed record PlutoEmpiricalBayesEstimate(double LocalMean, double LocalTrials, double GlobalMean, double GlobalTrials, double PriorStrength, double LocalWeight,
    double ShrunkMean, bool IsColdStart)
{
    /// <summary>정보 없음(지역 · 전역 모두 미관측).</summary>
    public static readonly PlutoEmpiricalBayesEstimate None = new(0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, false);
    public double GlobalWeight => 1.0 - LocalWeight;
    /// <summary>전역 사후가 '검증됨'(표본 ≥ <see cref="PlutoEmpiricalBayes.MinProvenSamples"/>).</summary>
    public bool IsGlobalProven => GlobalTrials >= PlutoEmpiricalBayes.MinProvenSamples;
    /// <summary>콜드스타트 컨텍스트에 검증된 전역 사전이 실제로 이식되는 중.</summary>
    public bool IsTransferActive => IsColdStart && IsGlobalProven && GlobalWeight > PlutoAdaptivePatternRanker.Epsilon;
    /// <summary>전역 순위에 더해지는 지역 특화분 = 수축 추정 − 전역 평균(전역 근거의 이중 계상 방지 · 지역 시행 0 → 0).</summary>
    public double ContextDelta => ShrunkMean - (IsGlobalProven ? GlobalMean : 0.0);
    public bool HasEvidence => LocalTrials > 0 || IsGlobalProven;
}

/// <summary>🧬 콜드스타트 전이 현황(두뇌 리포트) — 컨텍스트 수 · 콜드스타트 수 · 이식 가능한 전역 검증 사전 수 · 대표 콜드스타트 컨텍스트.</summary>
public sealed record PlutoColdStartTransferStatus(int ContextCount, int ColdStartContextCount, int ProvenPriorCount, string FocusContext, double FocusContextSamples, double FocusGlobalWeightPct)
{
    public static readonly PlutoColdStartTransferStatus Empty = new(0, 0, 0, string.Empty, 0.0, 0.0);
    public bool IsTransferActive => ColdStartContextCount > 0 && ProvenPriorCount > 0;
    /// <summary>🧬 모달리티별 전역 검증 사전 수(훅 · 룩 · BGM · 태그 — 태그는 '#' 변형 병합).</summary>
    public int ProvenHookPriors { get; init; }
    public int ProvenLookPriors { get; init; }
    public int ProvenBgmPriors { get; init; }
    public int ProvenTagPriors { get; init; }
}

/// <summary>
/// 🧬 계층적 경험적 베이즈(순수 함수 · 결정론) — 표본이 적은 (Domain, RenderMode) 컨텍스트의 지역 추정을 검증된 전역 패턴 사후로 수축.
///   ▸ Normal–Normal: 사후 = (n/σ²·지역 + 1/τ²·전역) / (n/σ² + 1/τ²) → 지역 시행 단위 사전 강도 k = σ² / (τ₀² + σ²/n_g).
///     σ² = 전역 관측 분산(잡음이 클수록 더 강하게 수축) · τ₀ = <see cref="BetweenContextStdDev"/>(컨텍스트 간 실제 차이) · σ²/n_g = 전역 평균 자체의 불확실성(표본이 적은 전역은 약한 사전).
///   ▸ 전역 미검증(표본 &lt; <see cref="MinProvenSamples"/>) → 사전 평균 0(중립) · 강도 <see cref="NeutralPriorStrength"/>(단발 지역 관측의 급변만 완충).
///   ▸ 콜드스타트 게이트: 지역 시행 &lt; <see cref="ColdStartTrials"/> 일 때만 수축 — 성숙한 지역 데이터는 절대 덮지 않는다.
/// </summary>
public static class PlutoEmpiricalBayes
{
    /// <summary>학습 메모리 키 접두사 — <c>cpat:{Domain}|{RenderMode}|{token}</c>(컨텍스트 기준선 <c>ctx:</c> 와 분리).</summary>
    public const string LocalKeyPrefix = "cpat:";
    public const double ColdStartTrials = 3.0;
    public const double MinProvenSamples = PlutoContextualBaseline.MinExpectancySamples;
    public const double BetweenContextStdDev = 0.05;
    public const double NeutralPriorStrength = 1.0;
    public const double MaxPriorStrength = 6.0;
    /// <summary>잔차 알파(합성 점수) → 순위 배수 이득 — 컨텍스트 특화 +0.10 → ×1.20.</summary>
    public const double ContextAlphaGain = 2.0;
    public const double MinContextMultiplier = 0.6, MaxContextMultiplier = 1.4;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>지역 패턴 키 접두(<c>cpat:{Domain}|{RenderMode}|</c>) — 도메인/포맷 안의 구분자는 <see cref="PlutoContextualBaseline.Part"/> 가 정규화.</summary>
    public static string LocalPrefix(string? domain, string? renderMode)
        => LocalKeyPrefix + PlutoContextualBaseline.Part(domain) + PlutoContextualBaseline.Separator + PlutoContextualBaseline.Part(renderMode) + PlutoContextualBaseline.Separator;

    public static string LocalKey(string? domain, string? renderMode, string patternToken) => LocalPrefix(domain, renderMode) + patternToken;

    /// <summary>사전 강도 k(지역 시행 단위) — 전역 미검증 → <see cref="NeutralPriorStrength"/> · 검증됨 → clamp(σ²/(τ₀² + σ²/n_g), 중립, 상한).</summary>
    public static double PriorStrength(double globalTrials, double globalVariance)
    {
        var n = double.IsFinite(globalTrials) && globalTrials > 0 ? globalTrials : 0.0;
        if (n < MinProvenSamples) return NeutralPriorStrength;
        const double floor = PlutoContextualBaseline.MinStdDev * PlutoContextualBaseline.MinStdDev;
        var s2 = double.IsFinite(globalVariance) && globalVariance > floor ? globalVariance : floor;
        var k = s2 / (BetweenContextStdDev * BetweenContextStdDev + s2 / n);
        return Math.Clamp(k, NeutralPriorStrength, MaxPriorStrength);
    }

    /// <summary>🧬 지역 추정을 전역 사후로 수축 — 비유한 입력은 0 으로 정규화.</summary>
    public static PlutoEmpiricalBayesEstimate Shrink(double localMean, double localTrials, double globalMean, double globalTrials, double globalVariance)
    {
        var nl = double.IsFinite(localTrials) && localTrials > 0 ? localTrials : 0.0;
        var ng = double.IsFinite(globalTrials) && globalTrials > 0 ? globalTrials : 0.0;
        var lm = nl > 0 && double.IsFinite(localMean) ? localMean : 0.0;
        var gm = ng > 0 && double.IsFinite(globalMean) ? globalMean : 0.0;
        var k = PriorStrength(ng, globalVariance);
        if (nl >= ColdStartTrials) return new PlutoEmpiricalBayesEstimate(lm, nl, gm, ng, k, 1.0, lm, false);
        var priorMean = ng >= MinProvenSamples ? gm : 0.0;
        var w = nl / (nl + k);
        return new PlutoEmpiricalBayesEstimate(lm, nl, gm, ng, k, w, w * lm + (1.0 - w) * priorMean, true);
    }

    /// <summary>컨텍스트 순위 배수 = clamp(1 + 이득 × 지역 특화분) — 근거 없음/지역 시행 0 → 1.0(전역 순위 그대로 = 사전 100% 이식).</summary>
    public static double ContextMultiplier(PlutoEmpiricalBayesEstimate? estimate)
    {
        if (estimate is null || !(estimate.LocalTrials > 0)) return 1.0;
        var d = estimate.ContextDelta;
        return double.IsFinite(d) ? Math.Clamp(1.0 + ContextAlphaGain * d, MinContextMultiplier, MaxContextMultiplier) : 1.0;
    }

    /// <summary>🧬 두뇌 리포트 1줄(결정론 한국어).</summary>
    public static string BuildLabel(PlutoColdStartTransferStatus? status)
    {
        if (status is null || status.ContextCount == 0)
            return "기록 없음 · 새 (도메인·포맷) 컨텍스트는 검증된 전역 패턴 사전에서 출발(지역 시행 " + ColdStartTrials.ToString("0", Inv) + "건부터 독립)";
        var ctx = status.ContextCount.ToString(Inv);
        var priors = status.ProvenPriorCount.ToString(Inv);
        if (status.ColdStartContextCount == 0)
            return "전 컨텍스트 성숙(" + ctx + "개) — 지역 데이터 우선 · 전역 검증 사전 " + priors + "개 대기" + ModalityBreakdown(status);
        var head = "콜드스타트 " + status.ColdStartContextCount.ToString(Inv) + "/" + ctx + "개 컨텍스트";
        if (status.ProvenPriorCount == 0)
            return head + " · 이식할 전역 검증 사전 없음(패턴 표본 " + MinProvenSamples.ToString("0", Inv) + "건부터)";
        var s = head + " · 전역 검증 사전 " + priors + "개 이식 중";
        if (status.FocusContext.Length > 0)
            s += " · '" + status.FocusContext + "' n=" + status.FocusContextSamples.ToString("0.#", Inv) + " → 전역 가중 " + status.FocusGlobalWeightPct.ToString("0", Inv) + "%";
        return s + ModalityBreakdown(status);
    }

    /// <summary>🧬 BGM · 태그 사전이 있을 때만 모달리티 분해 꼬리(훅 · 룩만이면 Step 17 문구 그대로).</summary>
    private static string ModalityBreakdown(PlutoColdStartTransferStatus s)
        => s.ProvenBgmPriors + s.ProvenTagPriors == 0 ? string.Empty
            : " (훅 " + s.ProvenHookPriors.ToString(Inv) + " · 룩 " + s.ProvenLookPriors.ToString(Inv) + " · BGM " + s.ProvenBgmPriors.ToString(Inv) + " · 태그 " + s.ProvenTagPriors.ToString(Inv) + ")";

    /// <summary>
    /// 🧬 '#' 유무 태그 변형 병합(순수) — 지역/전역 각각 표본 가중 평균 · 표본 합 · 분산은 표본 가중 평균(근사). 한쪽만 있으면 그대로.
    /// </summary>
    public static (double Mean, double Variance, double Samples) MergeVariants(double meanA, double varA, double nA, double meanB, double varB, double nB)
    {
        var a = double.IsFinite(nA) && nA > 0 ? nA : 0.0;
        var b = double.IsFinite(nB) && nB > 0 ? nB : 0.0;
        if (a + b <= 0) return (0.0, 0.0, 0.0);
        if (b <= 0) return (Finite(meanA), Finite(varA), a);
        if (a <= 0) return (Finite(meanB), Finite(varB), b);
        var n = a + b;
        return ((a * Finite(meanA) + b * Finite(meanB)) / n, (a * Finite(varA) + b * Finite(varB)) / n, n);
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0.0;
}

/// <summary>
/// 🎲 하이브리드 톰슨–UCB 탐색(순수 함수 · 주입 <see cref="Random"/> 으로 재현 가능).
///   ▸ 점수 = 활용 점수 + (1 − λ)·UCB1 보너스 + λ·톰슨 편차 + 후회 회복 사전.
///   ▸ 톰슨 편차 = 사후 표준편차 × z(Normal · 리프트 불확실성) — 다목적 근거가 있는 후보는 참여율 Beta 사후 편차와 반반 혼합.
///   ▸ UCB1 은 같은 상태에서 항상 같은 후보를 고른다(결정론 동률 고착) → 톰슨 편차가 불확실성에 비례한 무작위성으로 고착을 깨고, 불확실성이 큰 후보일수록 상방 추첨 폭이 넓다.
///   ▸ 추첨 순서는 키 사전순(입력 순서와 무관 → 같은 시드 = 같은 결과).
/// </summary>
public static class PlutoThompsonUcbSampler
{
    /// <summary>λ — 톰슨 비중(0 = 순수 UCB1 · 1 = 순수 톰슨).</summary>
    public const double ThompsonBlend = 0.5;
    public const string PolicyUcb = "UCB1", PolicyHybrid = "Thompson-UCB";

    /// <summary>표준 정규 추첨(Box–Muller).</summary>
    public static double NextGaussian(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var u1 = 1.0 - rng.NextDouble();   // (0, 1]
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Gamma(shape, 1) 추첨(Marsaglia–Tsang · shape &lt; 1 은 부스팅).</summary>
    public static double NextGamma(Random rng, double shape)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (!double.IsFinite(shape) || shape <= 0) return 0.0;
        if (shape < 1.0) return NextGamma(rng, shape + 1.0) * Math.Pow(1.0 - rng.NextDouble(), 1.0 / shape);
        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);
        for (var guard = 0; guard < 256; guard++)
        {
            var x = NextGaussian(rng);
            var v = 1.0 + c * x;
            if (v <= 0) continue;
            v = v * v * v;
            var u = 1.0 - rng.NextDouble();
            if (Math.Log(u) < 0.5 * x * x + d - d * v + d * Math.Log(v)) return d * v;
        }
        return d;   // 수치 가드(사실상 도달 불가) — 평균 근방 값
    }

    /// <summary>Beta(a, b) 추첨 — 비유한/비양수 모수 → 0.5.</summary>
    public static double NextBeta(Random rng, double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || a <= 0 || b <= 0) return 0.5;
        var x = NextGamma(rng, a);
        var y = NextGamma(rng, b);
        return x + y > 0 ? x / (x + y) : 0.5;
    }

    /// <summary>다목적 성과 → 합성 참여율 0~1(A/B 장부와 같은 가중 · CTR 축은 이미 ×10 정규화).</summary>
    public static double CompositeRate(PlutoObjectiveVector v)
        => Math.Clamp((PlutoExperimentLedgerService.HookWeight * v.Hook + PlutoExperimentLedgerService.CompletionWeight * v.Completion + PlutoExperimentLedgerService.CtrWeight * v.Ctr)
            / (PlutoExperimentLedgerService.HookWeight + PlutoExperimentLedgerService.CompletionWeight + PlutoExperimentLedgerService.CtrWeight), 0.0, 1.0);

    /// <summary>하이브리드 점수(순수 함수) — <paramref name="thompsonDeviation"/> = 사후 평균 기준 추첨 편차.</summary>
    public static double HybridScore(double exploitScore, double ucbBonus, double thompsonDeviation, double recoveryPrior, double blend = ThompsonBlend)
    {
        var l = double.IsFinite(blend) ? Math.Clamp(blend, 0.0, 1.0) : ThompsonBlend;
        var bonus = double.IsFinite(ucbBonus) ? ucbBonus : 0.0;
        var dev = double.IsFinite(thompsonDeviation) ? thompsonDeviation : 0.0;
        var prior = double.IsFinite(recoveryPrior) && recoveryPrior > 0 ? recoveryPrior : 0.0;
        return exploitScore + (1.0 - l) * bonus + l * dev + prior;
    }

    /// <summary>후보 1건의 톰슨 편차 추첨 — Normal(0, 사후 σ²) · 다목적 근거 후보는 Beta(1 + p·n, 1 + (1 − p)·n) 편차와 반반.</summary>
    public static double DrawDeviation(PlutoRankedOption option, Random rng)
    {
        ArgumentNullException.ThrowIfNull(option);
        var normal = option.PosteriorUncertainty * NextGaussian(rng);
        if (!option.Pareto.HasEvidence || !(option.ObjectiveSamples > 0)) return normal;
        var p = CompositeRate(option.Objectives);
        double a = 1.0 + p * option.ObjectiveSamples, b = 1.0 + (1.0 - p) * option.ObjectiveSamples;
        return 0.5 * normal + 0.5 * (NextBeta(rng, a, b) - a / (a + b));
    }

    /// <summary>
    /// 🎲 탐색 순위 재산정 — 각 후보의 <see cref="PlutoRankedOption.ExplorationBonus"/> 를 (하이브리드 점수 − 활용 점수)로 바꿔 <see cref="PlutoRankedOption.UcbScore"/> 가
    /// 곧 하이브리드 점수가 되게 한 뒤 내림차순 정렬(동점 → 키 사전순). <paramref name="recoveryPriors"/> 키 = 후보 키(<c>hook:…</c> · <c>look:…</c>).
    /// </summary>
    public static List<PlutoRankedOption> Rescore(IReadOnlyList<PlutoRankedOption> options, Random rng, IReadOnlyDictionary<string, double>? recoveryPriors = null,
        double blend = ThompsonBlend)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rng);
        var list = new List<PlutoRankedOption>(options.Count);
        for (var i = 0; i < options.Count; i++) if (options[i] is not null) list.Add(options[i]);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));   // 추첨 순서 고정
        for (var i = 0; i < list.Count; i++)
        {
            var o = list[i];
            var prior = recoveryPriors is not null && recoveryPriors.TryGetValue(o.Key, out var boost) ? boost : 0.0;
            var hybrid = HybridScore(o.FinalScore, o.ExplorationBonus, DrawDeviation(o, rng), prior, blend);
            list[i] = o with { ExplorationBonus = hybrid - o.FinalScore };
        }
        list.Sort(static (a, b) =>
        {
            var c = b.UcbScore.CompareTo(a.UcbScore);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return list;
    }
}
