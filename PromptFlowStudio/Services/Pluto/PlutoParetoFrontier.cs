using System;
using System.Collections.Generic;
using System.Globalization;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// ⚖️ 다목적 성과 벡터(각 축 0~1) — 3초 훅 유지율 · 완주율 · CTR(합성 점수와 동일하게 CTR × <see cref="PlutoExperimentLedgerService.CtrScale"/> 정규화).
/// </summary>
public readonly record struct PlutoObjectiveVector(double Hook, double Completion, double Ctr)
{
    /// <summary>원시 퍼센트 지표 → 정규화 벡터(비유한 · 범위 밖 → 클램프).</summary>
    public static PlutoObjectiveVector FromPercent(double hookPct, double completionPct, double ctrPct)
        => new(Unit(hookPct), Unit(completionPct), Math.Min(1.0, Unit(ctrPct) * PlutoExperimentLedgerService.CtrScale));

    private static double Unit(double pct) => double.IsFinite(pct) ? Math.Clamp(pct, 0.0, 100.0) / 100.0 : 0.0;

    public bool IsFinite => double.IsFinite(Hook) && double.IsFinite(Completion) && double.IsFinite(Ctr);
    /// <summary>최약 축(하방) — 균형 잡힌 패턴일수록 높다.</summary>
    public double Worst => Math.Min(Hook, Math.Min(Completion, Ctr));
    public double Mean => (Hook + Completion + Ctr) / 3.0;
}

/// <summary>⚖️ 후보 1건의 파레토 평가 — 티어(0 = 프런티어 · −1 = 근거 없음) · 점수 배수 · 클릭베이트 여부 · 하방(최약 축) 점수.</summary>
public sealed record PlutoParetoAssessment(int Tier, double Multiplier, bool IsClickbaitSpike, double DownsideScore)
{
    public static readonly PlutoParetoAssessment None = new(-1, 1.0, false, 0.0);
    public bool HasEvidence => Tier >= 0;
    public bool IsFrontier => Tier == 0;
}

/// <summary>⚖️ 파레토 프런티어 선두(훅) — 두뇌 리포트 표기용.</summary>
public sealed record PlutoParetoLeader(string Label, PlutoObjectiveVector Objectives, double Samples, int FrontierSize, int EvidencedCount, int ClickbaitCount)
{
    /// <summary>⚖️ 1줄(결정론 한국어).</summary>
    public string ToLabel()
    {
        var inv = CultureInfo.InvariantCulture;
        var s = "'" + Label + "' · 3초 " + (Objectives.Hook * 100).ToString("0.#", inv) + " · 완주 " + (Objectives.Completion * 100).ToString("0.#", inv)
            + " · CTR " + (Objectives.Ctr * 100 / PlutoExperimentLedgerService.CtrScale).ToString("0.##", inv) + "% · 최약 축 " + Objectives.Worst.ToString("0.00", inv)
            + " · 프런티어 " + FrontierSize.ToString(inv) + "/" + EvidencedCount.ToString(inv);
        if (ClickbaitCount > 0) s += " · 🎣 클릭베이트 강등 " + ClickbaitCount.ToString(inv) + "개";
        return s;
    }
}

/// <summary>
/// ⚖️ 다목적 파레토 프런티어(순수 함수 · 결정론) — 단일 지표 급등(3초 유지율만 높고 완주율이 붕괴한 클릭베이트)이
/// 세 축 모두 고른 '복리형' 패턴을 누르지 못하게 한다.
///   ▸ 비지배 정렬: 티어 0 = 어떤 후보에게도 세 축 모두에서 지배당하지 않는 프런티어 · 티어 k = 상위 티어 제거 후 프런티어.
///   ▸ 배수 = 티어 계수(0 → 1.0 · k → max(<see cref="MinTierFactor"/>, 1 − <see cref="TierStep"/>·k)) × (1 + <see cref="DownsideBonusMax"/> × 최약 축)
///     × (클릭베이트 → <see cref="ClickbaitFactor"/>). 근거 없음(표본 &lt; <see cref="MinObjectiveSamples"/>) → 정확히 1.0(기존 순위 불변).
///   ▸ 클릭베이트 = 훅 ≥ <see cref="ClickbaitHook"/> ∧ 완주 &lt; <see cref="ClickbaitCompletion"/> — 랭커는 이 후보의 학습 부스트(배수 &gt; 1)도 무효화한다.
/// </summary>
public static class PlutoParetoFrontier
{
    public const double MinObjectiveSamples = 2.0;
    public const double ClickbaitHook = 0.65, ClickbaitCompletion = 0.20;
    public const double ClickbaitFactor = 0.70;
    public const double DownsideBonusMax = 0.15;
    public const double TierStep = 0.05, MinTierFactor = 0.85;
    public const double DominanceEpsilon = 1e-9;

    /// <summary>a 가 b 를 지배(세 축 모두 ≥ · 한 축 이상 &gt;).</summary>
    public static bool Dominates(PlutoObjectiveVector a, PlutoObjectiveVector b)
    {
        if (a.Hook < b.Hook - DominanceEpsilon || a.Completion < b.Completion - DominanceEpsilon || a.Ctr < b.Ctr - DominanceEpsilon) return false;
        return a.Hook > b.Hook + DominanceEpsilon || a.Completion > b.Completion + DominanceEpsilon || a.Ctr > b.Ctr + DominanceEpsilon;
    }

    public static bool IsClickbaitSpike(PlutoObjectiveVector v) => v.IsFinite && v.Hook >= ClickbaitHook && v.Completion < ClickbaitCompletion;

    /// <summary>비지배 정렬 — 반환[i] = 티어(0 = 프런티어). <paramref name="hasEvidence"/>[i] = false → −1(정렬 불참).</summary>
    public static int[] ComputeTiers(IReadOnlyList<PlutoObjectiveVector> points, IReadOnlyList<bool>? hasEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        var n = points.Count;
        var tiers = new int[n];
        var remaining = 0;
        for (var i = 0; i < n; i++)
        {
            var ok = points[i].IsFinite && (hasEvidence is null || (i < hasEvidence.Count && hasEvidence[i]));
            tiers[i] = ok ? int.MaxValue : -1;
            if (ok) remaining++;
        }
        for (var tier = 0; remaining > 0; tier++)
        {
            var assigned = 0;
            for (var i = 0; i < n; i++)
            {
                if (tiers[i] != int.MaxValue) continue;
                var dominated = false;
                for (var k = 0; k < n && !dominated; k++)
                    if (k != i && (tiers[k] == int.MaxValue || tiers[k] == -2) && Dominates(points[k], points[i])) dominated = true;
                if (!dominated) { tiers[i] = -2; assigned++; }   // −2 = 이번 티어 확정 대기(같은 패스의 비교에는 계속 참여)
            }
            for (var i = 0; i < n; i++) if (tiers[i] == -2) tiers[i] = tier;
            remaining -= assigned;
            if (assigned == 0) break;   // 방어(비유한 입력은 위에서 제외되므로 도달 불가)
        }
        for (var i = 0; i < n; i++) if (tiers[i] == int.MaxValue) tiers[i] = -1;
        return tiers;
    }

    /// <summary>점수 배수(순수 함수) — 근거 없음(<paramref name="tier"/> &lt; 0) → 1.0.</summary>
    public static double Multiplier(int tier, PlutoObjectiveVector v)
    {
        if (tier < 0 || !v.IsFinite) return 1.0;
        var tierFactor = tier == 0 ? 1.0 : Math.Max(MinTierFactor, 1.0 - TierStep * tier);
        var m = tierFactor * (1.0 + DownsideBonusMax * Math.Clamp(v.Worst, 0.0, 1.0));
        return IsClickbaitSpike(v) ? m * ClickbaitFactor : m;
    }

    public static PlutoParetoAssessment Assess(int tier, PlutoObjectiveVector v)
        => tier < 0 || !v.IsFinite ? PlutoParetoAssessment.None : new PlutoParetoAssessment(tier, Multiplier(tier, v), IsClickbaitSpike(v), Math.Clamp(v.Worst, 0.0, 1.0));
}
