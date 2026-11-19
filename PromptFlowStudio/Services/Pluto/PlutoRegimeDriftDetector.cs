using System;
using System.Globalization;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🌊 Page-Hinkley 누적 통계 상태(값 형식 · 영속 가능) — <c>x_t = expectedScore − observedScore</c>(예측 미스 &gt; 0).
///   ▸ <see cref="Mean"/> = x̄_T(재시작 이후 누적 평균 · 0 방향 수축) · <see cref="Cumulative"/> = m_T = Σ(x_t − x̄_t − δ) · <see cref="Minimum"/> = M_T = min m_t.
///   ▸ <see cref="Surge"/> = 탐색 서지 수준 0~1(드리프트 감지 시 1 · 안정 관측마다 감쇠).
/// </summary>
public struct PlutoPageHinkleyState
{
    public double Count;
    public double Mean;
    public double Cumulative;
    public double Minimum;
    public double Surge;
    public int Drifts;
    /// <summary>m_T − M_T(드리프트 판정 통계량 · ≥ 0).</summary>
    public readonly double Statistic => Math.Max(0.0, Cumulative - Minimum);
}

/// <summary>🌊 레짐 드리프트 현황 스냅샷(두뇌 리포트 · A/B · 기획안 엔진 공용).</summary>
public sealed record PlutoRegimeDriftStatus(bool IsSurgeActive, double SurgeLevel, double ExplorationTemperature, double Statistic, double Threshold,
    int DriftCount, double ObservationsSinceReset, DateTime? LastDriftUtc)
{
    /// <summary>안정 레짐(이력 없음).</summary>
    public static readonly PlutoRegimeDriftStatus Stable = new(false, 0.0, 1.0, 0.0, PlutoRegimeDriftDetector.Threshold, 0, 0.0, null);
    /// <summary>드리프트 임계까지의 진행률 0~1.</summary>
    public double PressurePct => Threshold > 0 ? Math.Round(Math.Clamp(Statistic / Threshold, 0.0, 1.0) * 100.0, 1, MidpointRounding.AwayFromZero) : 0.0;
}

/// <summary>
/// 🌊 Pluto 레짐 드리프트 감지기(순수 함수 · 결정론 · 무상태) — Page-Hinkley 검정으로 예측 잔차의 평균 상승(= 기존 승자의 연속 예측 미스)을 감지.
///   ▸ 드리프트: <c>m_T − M_T &gt; λ</c> → 통계 재시작 + 탐색 서지 1.0(UCB1 탐색 온도 ×(1 + <see cref="SurgeGain"/>)).
///   ▸ 잔차는 ±<see cref="ResidualClamp"/>로 절단 → 단일 이상치로는 임계(λ)를 넘지 못함(연속 미스만 감지).
///   ▸ 안정 관측(|x| ≤ <see cref="StableBand"/>)마다 서지 ×<see cref="SurgeDecay"/> · <see cref="SurgeFloor"/> 미만 → 0.
///     미스가 계속되는 동안에는 서지를 유지(새 레짐이 안정될 때까지 탐색 지속).
///   ▸ 시간 감쇠: 관측이 끊겨도 <see cref="SurgeHalfLifeDays"/>일 반감기로 소멸(영구 서지 방지).
/// 반감기 감쇠(14일/21일)는 완만한 드리프트를, 이 감지기는 급격한 레짐 전환을 담당한다.
/// </summary>
public static class PlutoRegimeDriftDetector
{
    /// <summary>δ — 허용 잔차 크기(이 이하의 평균 상승은 무시).</summary>
    public const double Tolerance = 0.02;
    /// <summary>λ — 드리프트 임계(합성 점수 0~1 척도).</summary>
    public const double Threshold = 0.45;
    /// <summary>잔차 절단 폭 — 단일 관측 최대 기여 = 0.40 − δ &lt; λ.</summary>
    public const double ResidualClamp = 0.40;
    /// <summary>안정 판정 잔차 폭.</summary>
    public const double StableBand = 0.10;
    public const double SurgeDecay = 0.60, SurgeFloor = 0.05;
    /// <summary>서지 1.0 에서 탐색 온도 = 1 + 2.0 = ×3.</summary>
    public const double SurgeGain = 2.0;
    public const double SurgeHalfLifeDays = 7.0;
    /// <summary>
    /// x̄_T 수축 사전 의사 관측 수 K — 예측기(기준선 + 패턴 기대치 EWMA)는 구성상 무편향이므로 x̄ 의 사전 평균은 0.
    /// 표준 PH 의 x̄ 는 이력이 짧을 때 첫 미스를 곧바로 흡수해 연속 미스를 가려 버린다(이중 중심화) → K 개의 0 관측으로 수축.
    /// </summary>
    public const double MeanPriorCount = 10.0;
    /// <summary>드리프트 감지 시 연루 패턴 초과 가중치 잔존율(표적 망각 — 낡은 승자의 활용 순위도 즉시 하향).</summary>
    public const double RegimeHaircut = 0.5;

    /// <summary>
    /// 관측 1건 반영 — 반환 true = 이번 관측에서 드리프트 감지(상태는 재시작 · 서지 1.0).
    /// 비유한 입력은 무시(상태 불변 · false).
    /// </summary>
    public static bool Update(ref PlutoPageHinkleyState s, double expectedScore, double observedScore)
    {
        if (!double.IsFinite(expectedScore) || !double.IsFinite(observedScore)) return false;
        var x = Math.Clamp(expectedScore - observedScore, -ResidualClamp, ResidualClamp);
        s.Count += 1.0;
        s.Mean += (x - s.Mean) / (s.Count + MeanPriorCount);   // x̄ = Σx / (n + K) — 0 방향 수축
        s.Cumulative += x - s.Mean - Tolerance;
        if (s.Cumulative < s.Minimum) s.Minimum = s.Cumulative;

        if (s.Cumulative - s.Minimum > Threshold)
        {
            s.Count = 0.0; s.Mean = 0.0; s.Cumulative = 0.0; s.Minimum = 0.0;   // 새 레짐 기준으로 재시작
            s.Surge = 1.0;
            s.Drifts++;
            return true;
        }
        if (s.Surge > 0.0 && Math.Abs(x) <= StableBand)
        {
            s.Surge *= SurgeDecay;
            if (s.Surge < SurgeFloor) s.Surge = 0.0;
        }
        return false;
    }

    /// <summary>서지 수준(0~1) → UCB1 탐색 온도 배수(1 ~ 1 + <see cref="SurgeGain"/>).</summary>
    public static double ExplorationTemperature(double surge)
        => 1.0 + SurgeGain * (double.IsFinite(surge) ? Math.Clamp(surge, 0.0, 1.0) : 0.0);

    /// <summary>시간 감쇠 반영 서지 — <c>surge × 0.5^(Δt / 7일)</c> · 바닥 미만 → 0.</summary>
    public static double EffectiveSurge(double surge, DateTime lastUtc, DateTime nowUtc)
    {
        if (!double.IsFinite(surge) || surge <= 0) return 0.0;
        var v = Math.Clamp(surge, 0.0, 1.0) * PlutoLearningService.HalfLifeFactor(lastUtc, nowUtc, SurgeHalfLifeDays);
        return v < SurgeFloor ? 0.0 : v;
    }

    /// <summary>🌊 드리프트 상태 1줄(결정론 한국어).</summary>
    public static string BuildLabel(PlutoRegimeDriftStatus? status)
    {
        var inv = CultureInfo.InvariantCulture;
        if (status is null || (status.DriftCount == 0 && status.ObservationsSinceReset <= 0)) return "안정 · 관측 없음";
        if (status.IsSurgeActive)
            return "레짐 변화 감지 · 탐색 서지 ×" + status.ExplorationTemperature.ToString("0.00", inv) + "(수준 " + (status.SurgeLevel * 100).ToString("0", inv) + "%) · 누적 " + status.DriftCount.ToString(inv) + "회";
        return "안정 · PH 압력 " + status.PressurePct.ToString("0.#", inv) + "% · 관측 " + status.ObservationsSinceReset.ToString("0", inv) + "건"
            + (status.DriftCount > 0 ? " · 누적 감지 " + status.DriftCount.ToString(inv) + "회" : "");
    }
}

/// <summary>📐 컨텍스트(Domain × RenderMode) EWMA 기준선 스냅샷 — 합성 점수 0~1.</summary>
public sealed record PlutoContextBaselineStat(string ContextKey, string Domain, string RenderMode, double Baseline, double StdDev, double Samples)
{
    /// <summary>표본 ≥ <see cref="PlutoContextualBaseline.MinContextSamples"/> — 잔차 알파 보정에 사용 가능.</summary>
    public bool IsEstablished => Samples >= PlutoContextualBaseline.MinContextSamples;
}

/// <summary>📐 컨텍스트 관측 결과 — 관측 <b>이전</b> 기준선 대비 잔차 알파(관측이 자기 기준선을 오염시키지 않음).</summary>
public sealed record PlutoContextualObservation(string ContextKey, double Score, double Baseline, double ResidualAlpha, double ZScore, double ContextSamples,
    bool IsEstablished, bool DriftDetected, double SurgeLevel)
{
    public static readonly PlutoContextualObservation None = new(string.Empty, 0.0, 0.0, 0.0, 0.0, 0.0, false, false, 0.0);
}

/// <summary>
/// 📐 컨텍스트 기준선 정규화(순수 함수) — 포맷(15초 앰비언트 · 몽타주 · 3부작 시리즈)과 도메인(BabyStudio · GeneralCinematic)마다
/// 유지율 · 완주율 · CTR 기준선이 다르므로, 패턴 보상은 원시 지표가 아니라 <c>잔차 알파 = 관측 − 컨텍스트 기준선</c>으로 평가한다.
///   ▸ 기준선 = EWMA(α = <see cref="Alpha"/> · 초기 n &lt; 1/α 구간은 단순 평균으로 편향 제거).
///   ▸ 부분 풀링: 유효 기준선 = (n·b_ctx + κ·b_global) / (n + κ) — 표본이 적은 컨텍스트는 전역 기준선으로 수축.
///   ▸ 보정은 명시 컨텍스트 + 표본 ≥ <see cref="MinContextSamples"/>일 때만(미만 → 기존 동작과 완전 동일).
/// </summary>
public static class PlutoContextualBaseline
{
    public const string ContextKeyPrefix = "ctx:";
    public const string GlobalContextKey = "ctx:*";
    public const string DriftStateKey = "drift:regime";
    public const string AnyPart = "Any";
    public const char Separator = '|';
    public const double Alpha = 0.2;
    public const double PatternAlpha = 0.3;
    public const double PoolingKappa = 2.0;
    public const double MinContextSamples = 3.0;
    public const double MinStdDev = 0.05;
    /// <summary>패턴 기대치(알파 EWMA) 성립 최소 표본 — 미만 패턴은 드리프트 검정에 불참.</summary>
    public const double MinExpectancySamples = 2.0;
    /// <summary>포맷 대비 초과 성과 판정 z(이상 → 절대 기준 미달이어도 벌점 면제 · 평균 성과면 알파 부스트).</summary>
    public const double OutperformZ = 1.0;
    public const int MaxPartLength = 32;

    /// <summary>합성 참여 점수 0~1 — A/B 장부와 동일 가중(훅 0.5 · 완주 0.3 · CTR×10 0.2).</summary>
    public static double CompositeScore(double hookPct, double completionPct, double ctrPct)
        => (PlutoExperimentLedgerService.HookWeight * Pct(hookPct) + PlutoExperimentLedgerService.CompletionWeight * Pct(completionPct)
            + PlutoExperimentLedgerService.CtrWeight * Math.Min(100.0, Pct(ctrPct) * PlutoExperimentLedgerService.CtrScale)) / 100.0;

    public static double CompositeScore(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return CompositeScore(t.ThreeSecondHookRetentionPct, t.AverageCompletionPct, t.ClickThroughRatePct);
    }

    /// <summary>컨텍스트 지정 여부(도메인 또는 렌더 모드 중 하나 이상).</summary>
    public static bool IsExplicit(string? domain, string? renderMode) => !string.IsNullOrWhiteSpace(domain) || !string.IsNullOrWhiteSpace(renderMode);

    /// <summary><c>ctx:{Domain}|{RenderMode}</c> — 누락 부분은 <c>Any</c> · 구분자/길이 정규화.</summary>
    public static string ContextKey(string? domain, string? renderMode) => ContextKeyPrefix + Part(domain) + Separator + Part(renderMode);

    public static string Part(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AnyPart;
        var v = raw.Trim().Replace(Separator, '/');
        return v.Length > MaxPartLength ? v[..MaxPartLength] : v;
    }

    public static bool TrySplit(string contextKey, out string domain, out string renderMode)
    {
        domain = renderMode = AnyPart;
        if (string.IsNullOrEmpty(contextKey) || !contextKey.StartsWith(ContextKeyPrefix, StringComparison.Ordinal)) return false;
        var body = contextKey.AsSpan(ContextKeyPrefix.Length);
        var i = body.IndexOf(Separator);
        if (i <= 0 || i == body.Length - 1) return false;
        domain = body[..i].ToString();
        renderMode = body[(i + 1)..].ToString();
        return true;
    }

    /// <summary>
    /// 잔차 z → 돌파 부스트 이득 0~1 — z ≤ −⅓ → 0(포맷 편승 · 보상 없음) · z = 0 → 0.25 · z ≥ 1 → 1(포맷 기준선을 실제로 상회).
    /// 미성립 컨텍스트 → 1(기존 동작).
    /// </summary>
    public static double BoostGain(double z, bool established)
        => !established || !double.IsFinite(z) ? 1.0 : Math.Clamp(0.25 + 0.75 * z, 0.0, 1.0);

    /// <summary>평균 성과(절대 돌파 아님)지만 포맷 기준선을 z ≥ 1 상회 → 알파 부스트 ×1.075(z=1) ~ ×1.15(z≥2) · 그 외 1.0.</summary>
    public static double AlphaBoost(double z, bool established)
        => established && double.IsFinite(z) && z >= OutperformZ
            ? 1.0 + (PlutoClosedLoopReinforcementService.MinBoost - 1.0) * Math.Clamp(z / 2.0, 0.5, 1.0)
            : 1.0;

    /// <summary>EWMA 스텝 계수 — 초기 구간은 1/n(단순 평균) · 이후 α.</summary>
    public static double StepFor(double samplesAfter, double alpha) => samplesAfter > 0 ? Math.Max(alpha, 1.0 / samplesAfter) : 1.0;

    private static double Pct(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 100.0) : 0.0;
}

/// <summary>
/// 🎯 베이지안 사후 신뢰 구간(Normal–Normal 켤레 · 배수 척도) — 패턴의 참 리프트 θ = 배수 − 1.
///   ▸ 사전: θ ~ N(0, σ₀²/κ₀) · κ₀ = <see cref="PriorPseudoTrials"/> 의사 시행 · σ₀ = <see cref="LiftSigma"/>(단일 시행 리프트 표준편차 = 최대 부스트 폭).
///   ▸ 사후 평균 = 1 + (w − 1)·n/(n + κ₀) — 표본이 적을수록 기준선(1.0)으로 수축.
///   ▸ 사후 표준편차 = σ₀/√(n + κ₀) · 신뢰 구간 = 평균 ± z·표준편차(z = <see cref="ConfidenceZ"/> · 양측 90%).
/// n(유효 시행)은 14일 반감기로 감쇠하므로 재확인 없는 과거 승자는 시간이 갈수록 구간이 다시 넓어진다(자동 재탐색 유도).
/// </summary>
public sealed record PlutoPosteriorConfidence(double PosteriorMean, double PosteriorUncertainty, double LowerConfidenceBound, double UpperConfidenceBound, double Trials)
{
    public const double PriorPseudoTrials = 1.0;
    public const double LiftSigma = PlutoClosedLoopReinforcementService.MaxBoost - 1.0;
    public const double ConfidenceZ = 1.645;

    /// <summary>사전 분포(이력 없음).</summary>
    public static readonly PlutoPosteriorConfidence Prior = Compute(PlutoLearningService.PatternBaseWeight, 0.0);

    /// <summary>검증된 고신뢰 승자 — 하한이 기준선(1.0)을 넘음.</summary>
    public bool IsProvenWinner => LowerConfidenceBound > PlutoLearningService.PatternBaseWeight + PlutoAdaptivePatternRanker.Epsilon;
    /// <summary>운 좋은 소표본 급등 — 평균은 기준선 위지만 하한이 기준선 이하이고 시행 &lt; <see cref="PlutoAdaptivePatternRanker.UnderSampledTrials"/>.</summary>
    public bool IsLuckySpike => PosteriorMean > PlutoLearningService.PatternBaseWeight + PlutoAdaptivePatternRanker.Epsilon && !IsProvenWinner
                                && Trials < PlutoAdaptivePatternRanker.UnderSampledTrials;

    public static PlutoPosteriorConfidence Compute(double learnedWeight, double effectiveTrials)
    {
        var w = double.IsFinite(learnedWeight) && learnedWeight > 0 ? learnedWeight : PlutoLearningService.PatternBaseWeight;
        var n = double.IsFinite(effectiveTrials) && effectiveTrials > 0 ? effectiveTrials : 0.0;
        var mean = PlutoLearningService.PatternBaseWeight + (w - PlutoLearningService.PatternBaseWeight) * n / (n + PriorPseudoTrials);
        var sd = LiftSigma / Math.Sqrt(n + PriorPseudoTrials);
        return new PlutoPosteriorConfidence(mean, sd, mean - ConfidenceZ * sd, mean + ConfidenceZ * sd, n);
    }

    /// <summary>🎯 신뢰 배지 1줄(결정론 한국어).</summary>
    public string ToLabel()
    {
        var inv = CultureInfo.InvariantCulture;
        var verdict = IsProvenWinner ? "검증된 승자" : IsLuckySpike ? "소표본 급등(미검증)" : Trials <= 0 ? "미검증" : "판단 유보";
        return verdict + " · 사후 ×" + PosteriorMean.ToString("0.00", inv) + " ±" + PosteriorUncertainty.ToString("0.00", inv)
            + " · 90% [" + LowerConfidenceBound.ToString("0.00", inv) + ", " + UpperConfidenceBound.ToString("0.00", inv) + "] · 시행 " + Trials.ToString("0.#", inv);
    }
}
