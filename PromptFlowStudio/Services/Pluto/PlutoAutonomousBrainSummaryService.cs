using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧠 Pluto 4.6 두뇌 리포트 스냅샷 — 실험 판정/대기 · 챌린저 승률 · 학습 1위 훅/룩/BGM · 오답 노트 차단 수 · 다음 가설 배너.</summary>
public sealed record PlutoBrainSummarySnapshot(int TotalEvaluatedExperiments, int PendingExperimentsCount, double ChallengerWinRatePct, string TopHookLabel, string TopLookLabel,
    string TopBgmLabel, int ActiveMistakeGuardCount, string NextHypothesisBanner)
{
    /// <summary>🔗 최강 시너지 조합 라벨(<c>훅 × 룩</c>) — 없음 → <see cref="PlutoAutonomousBrainSummaryService.NoneLabel"/>.</summary>
    public string TopSynergyPairLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🔗 최강 시너지 조합 점수(조합 배수 × 감점 반영 · 없음 → 1.0).</summary>
    public double TopSynergyScore { get; init; } = 1.0;
    /// <summary>🎰 UCB1 탐색 비중(%) — 훅 + 룩 결정 점수 중 불확실성 보너스가 차지하는 몫.</summary>
    public double ExplorationRatePct { get; init; }
    /// <summary>🎰 탐색 후보 수(비감점 · 덜 검증된 훅 + 룩).</summary>
    public int ExplorationCandidateCount { get; init; }
    /// <summary>🎰 누적 유효 시행 수(14일 반감기 감쇠 반영).</summary>
    public double TotalBanditTrials { get; init; }
    /// <summary>⏳ 오답 노트 시간 감쇠 현황.</summary>
    public PlutoMistakeDecayStatus MistakeDecay { get; init; } = PlutoMistakeDecayStatus.Empty;
    /// <summary>⏳ 오답 노트 감쇠 상태 1줄(결정론 한국어).</summary>
    public string MistakeDecayLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🌊 레짐 드리프트 · 탐색 서지 현황.</summary>
    public PlutoRegimeDriftStatus RegimeDrift { get; init; } = PlutoRegimeDriftStatus.Stable;
    /// <summary>🌊 드리프트 상태 1줄(결정론 한국어).</summary>
    public string RegimeDriftLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>📐 컨텍스트(Domain × RenderMode) 기준선(표본 내림차순).</summary>
    public IReadOnlyList<PlutoContextBaselineStat> ContextBaselines { get; init; } = Array.Empty<PlutoContextBaselineStat>();
    /// <summary>📐 컨텍스트 기준선 1줄(상위 <see cref="PlutoAutonomousBrainSummaryService.MaxContextLabels"/>개).</summary>
    public string ContextBaselineLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🎯 1위 훅 사후 신뢰 구간.</summary>
    public PlutoPosteriorConfidence TopHookConfidence { get; init; } = PlutoPosteriorConfidence.Prior;
    /// <summary>🎯 1위 룩 사후 신뢰 구간.</summary>
    public PlutoPosteriorConfidence TopLookConfidence { get; init; } = PlutoPosteriorConfidence.Prior;
    /// <summary>🎯 사후 신뢰 1줄(훅 · 룩).</summary>
    public string PosteriorConfidenceLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🎯 메타 보정(예측 vs 실현 · 반사실 후회 · 탐색 배수) 현황.</summary>
    public PlutoCalibrationStatus Calibration { get; init; } = PlutoCalibrationStatus.Empty;
    /// <summary>🎯 보정 정확도 1줄(결정론 한국어).</summary>
    public string CalibrationLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>💸 반사실 후회 1줄(결정론 한국어).</summary>
    public string CounterfactualRegretLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>⚖️ 파레토 프런티어 선두 훅(다목적 근거 없음 → null).</summary>
    public PlutoParetoLeader? ParetoLeader { get; init; }
    /// <summary>⚖️ 파레토 프런티어 1줄(결정론 한국어).</summary>
    public string ParetoFrontierLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🔁 능동 후회 리허설 대상(없음 → null).</summary>
    public PlutoRegretRecoveryBoost? RegretRehearsalTarget { get; init; }
    /// <summary>🔁 후회 리허설 1줄(결정론 한국어).</summary>
    public string RegretRehearsalLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
}

/// <summary>🧠 Pluto 4.6 자율 두뇌 텔레메트리 요약 — 실험 장부 × 적응형 랭커 × 오답 노트 집계.</summary>
public interface IPlutoAutonomousBrainSummaryService
{
    /// <summary>현재 학습 상태 집계(각 소스 실패 → 해당 필드 기본값으로 [부분 구제] · 취소만 전파).</summary>
    Task<PlutoBrainSummarySnapshot> GetSummaryAsync(CancellationToken ct = default);
}

/// <summary>🧠 <see cref="IPlutoAutonomousBrainSummaryService"/> 구현(순수 BCL · 결정론 배너).</summary>
public sealed class PlutoAutonomousBrainSummaryService : IPlutoAutonomousBrainSummaryService
{
    public const string NoneLabel = "—";
    /// <summary>집계 대상 판정 상한(= 장부 용량).</summary>
    public const int MaxEvaluations = PlutoExperimentLedgerService.Capacity;
    /// <summary>두뇌 리포트 1줄에 표기할 컨텍스트 기준선 최대 수.</summary>
    public const int MaxContextLabels = 3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPlutoExperimentLedgerService? _ledger;
    private readonly IPlutoAdaptivePatternRanker? _ranker;
    private readonly IPlutoMistakeNotebookService? _notebook;

    public PlutoAutonomousBrainSummaryService(IPlutoExperimentLedgerService? ledger, IPlutoAdaptivePatternRanker? ranker, IPlutoMistakeNotebookService? notebook)
    {
        _ledger = ledger;
        _ranker = ranker;
        _notebook = notebook;
    }

    public async Task<PlutoBrainSummarySnapshot> GetSummaryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int evaluated = 0, pending = 0, challengerWins = 0, guards = 0;
        string hook = NoneLabel, look = NoneLabel, bgm = NoneLabel;
        PlutoPairSynergy? topPair = null;
        var explore = PlutoExplorationTelemetry.Empty;
        var decay = PlutoMistakeDecayStatus.Empty;
        var drift = PlutoRegimeDriftStatus.Stable;
        IReadOnlyList<PlutoContextBaselineStat> baselines = Array.Empty<PlutoContextBaselineStat>();
        PlutoPosteriorConfidence hookConf = PlutoPosteriorConfidence.Prior, lookConf = PlutoPosteriorConfidence.Prior;
        var calibration = PlutoCalibrationStatus.Empty;
        PlutoParetoLeader? pareto = null;
        PlutoRegretRecoveryBoost? rehearsal = null;

        if (_ledger is not null)
        {
            try
            {
                await _ledger.EnsureLoadedAsync(ct).ConfigureAwait(false);
                var evals = _ledger.GetRecentEvaluations(MaxEvaluations);
                evaluated = evals.Count;
                for (var i = 0; i < evals.Count; i++)
                    if (string.Equals(evals[i].WinnerLabel, PlutoExperimentLedgerService.WinnerChallenger, StringComparison.Ordinal)) challengerWins++;
                pending = _ledger.GetPendingExperiments(MaxEvaluations).Count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Ledger", ex); }   // [부분 구제]
        }

        if (_notebook is not null)
        {
            try
            {
                await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false);
                guards = _notebook.GetSuppressedTokens().Count;
                decay = _notebook.GetDecayStatus() ?? PlutoMistakeDecayStatus.Empty;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Notebook", ex); }
        }

        if (_ranker is not null)
        {
            try
            {
                await _ranker.EnsureLoadedAsync(ct).ConfigureAwait(false);
                var rec = _ranker.GetCurrentRecommendations();
                hook = Label(rec.TopHook);
                look = Label(rec.TopColorLook);
                bgm = Label(rec.TopBgmMood);
                topPair = _ranker.GetTopSynergyPair();
                explore = _ranker.GetExplorationTelemetry() ?? PlutoExplorationTelemetry.Empty;
                hookConf = rec.TopHook?.Posterior ?? PlutoPosteriorConfidence.Prior;
                lookConf = rec.TopColorLook?.Posterior ?? PlutoPosteriorConfidence.Prior;
                drift = _ranker.GetRegimeDriftStatus() ?? PlutoRegimeDriftStatus.Stable;
                baselines = _ranker.GetContextBaselines() ?? Array.Empty<PlutoContextBaselineStat>();
                calibration = _ranker.GetCalibrationStatus() ?? PlutoCalibrationStatus.Empty;
                pareto = _ranker.GetParetoFrontierLeader();
                rehearsal = _ranker.GetRegretRecoveryBoosts() is { Count: > 0 } boosts ? boosts[0] : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Ranker", ex); }
        }

        var winRate = evaluated > 0 ? Math.Round(challengerWins * 100.0 / evaluated, 1, MidpointRounding.AwayFromZero) : 0.0;
        return new PlutoBrainSummarySnapshot(evaluated, pending, winRate, hook, look, bgm, guards, BuildHypothesisBanner(evaluated, pending, winRate, hook, look, bgm, guards))
        {
            TopSynergyPairLabel = topPair is null ? NoneLabel : topPair.First + " × " + topPair.Second,
            TopSynergyScore = topPair?.Score ?? 1.0,
            ExplorationRatePct = explore.ExplorationRatePct,
            ExplorationCandidateCount = explore.ExplorationCandidateCount,
            TotalBanditTrials = explore.TotalTrials,
            MistakeDecay = decay,
            MistakeDecayLabel = BuildDecayLabel(decay),
            RegimeDrift = drift,
            RegimeDriftLabel = PlutoRegimeDriftDetector.BuildLabel(drift),
            ContextBaselines = baselines,
            ContextBaselineLabel = BuildContextLabel(baselines),
            TopHookConfidence = hookConf,
            TopLookConfidence = lookConf,
            PosteriorConfidenceLabel = "훅 " + hookConf.ToLabel() + " / 룩 " + lookConf.ToLabel(),
            Calibration = calibration,
            CalibrationLabel = PlutoMetaCalibrationService.BuildCalibrationLabel(calibration),
            CounterfactualRegretLabel = PlutoMetaCalibrationService.BuildRegretLabel(calibration),
            ParetoLeader = pareto,
            ParetoFrontierLabel = BuildParetoLabel(pareto),
            RegretRehearsalTarget = rehearsal,
            RegretRehearsalLabel = BuildRehearsalLabel(rehearsal),
        };
    }

    /// <summary>⚖️ 파레토 프런티어 1줄(결정론) — 근거 없음 → 수집 안내.</summary>
    public static string BuildParetoLabel(PlutoParetoLeader? leader)
        => leader is null
            ? "근거 없음 · 훅별 성과 " + PlutoParetoFrontier.MinObjectiveSamples.ToString("0", Inv) + "건부터 (3초 · 완주 · CTR) 균형 평가"
            : leader.ToLabel();

    /// <summary>🔁 후회 리허설 1줄(결정론) — 대상 없음 → 대기.</summary>
    public static string BuildRehearsalLabel(PlutoRegretRecoveryBoost? target)
        => target is null
            ? "대상 없음 · 미해소 후회 " + PlutoMetaCalibrationService.RecoveryThreshold.ToString("0.00", Inv) + " 이상부터 챌린저 B 로 재검증"
            : target.ToLabel() + " → 다음 A/B 챌린저 B 배정";

    /// <summary>📐 컨텍스트 기준선 1줄(결정론) — <c>도메인·포맷 기준 62.5(±4.1 · n=7)</c> 상위 <see cref="MaxContextLabels"/>개 · 없음 → "기록 없음".</summary>
    public static string BuildContextLabel(IReadOnlyList<PlutoContextBaselineStat>? baselines)
    {
        if (baselines is null || baselines.Count == 0) return "기록 없음 · 표본 " + PlutoContextualBaseline.MinContextSamples.ToString("0", Inv) + "건부터 잔차 보정";
        var sb = new System.Text.StringBuilder(96);
        var n = Math.Min(MaxContextLabels, baselines.Count);
        for (var i = 0; i < n; i++)
        {
            var b = baselines[i];
            if (i > 0) sb.Append(" / ");
            sb.Append(b.Domain).Append('·').Append(b.RenderMode).Append(" 기준 ").Append((b.Baseline * 100).ToString("0.#", Inv))
              .Append("(±").Append((b.StdDev * 100).ToString("0.#", Inv)).Append(" · n=").Append(b.Samples.ToString("0", Inv)).Append(b.IsEstablished ? ")" : " · 수집 중)");
        }
        if (baselines.Count > n) sb.Append(" 외 ").Append((baselines.Count - n).ToString(Inv)).Append('개');
        return sb.ToString();
    }

    /// <summary>⏳ 오답 노트 감쇠 상태 1줄(결정론) — 추적 0 → "기록 없음".</summary>
    public static string BuildDecayLabel(PlutoMistakeDecayStatus? status)
    {
        if (status is null || status.TrackedCount == 0) return "기록 없음 · 반감기 " + PlutoMistakeNotebookService.HalfLifeDays.ToString("0", Inv) + "일";
        var s = "추적 " + status.TrackedCount.ToString(Inv) + " · 가드 " + status.ActiveGuardCount.ToString(Inv);
        if (status.CompositeGuardCount > 0) s += "(조합 " + status.CompositeGuardCount.ToString(Inv) + ")";
        s += " · 회복 중 " + status.RecoveringCount.ToString(Inv) + " · 감쇠 −" + status.DecayedAwayPct.ToString("0.#", Inv) + "% · 반감기 " + status.HalfLifeDays.ToString("0", Inv) + "일";
        if (status.DaysUntilNextRelease is { } d) s += " · 다음 자연 해제 ≈ " + d.ToString("0.#", Inv) + "일";
        return s;
    }

    private static string Label(PlutoRankedOption? o)
        => o is null || string.IsNullOrWhiteSpace(o.DisplayLabel) ? (o is null || string.IsNullOrWhiteSpace(o.Key) ? NoneLabel : o.Key) : o.DisplayLabel;

    /// <summary>💡 다음 가설 배너(결정론) — 실험 0건 → 첫 실험 유도 · 대기 실험 → 지표 입력 유도 · 승률 ≥ 50% → 챌린저 공격 · 미만 → 챔피언 방어 + 단일 변수 실험.</summary>
    public static string BuildHypothesisBanner(int evaluated, int pending, double winRatePct, string hook, string look, string bgm, int guards)
    {
        var guardNote = guards > 0 ? " · 🛡️ 오답 가드 " + guards.ToString(Inv) + "개 회피 중" : "";
        if (evaluated == 0)
            return pending > 0
                ? "⏳ 대기 실험 " + pending.ToString(Inv) + "건 — 게시 후 지표를 입력하면 첫 승자 판정과 학습이 시작돼요" + guardNote
                : "🧪 첫 A/B 실험을 등록해 보세요 — 훅 '" + hook + "' × 룩 '" + look + "' 챔피언으로 시작" + guardNote;
        var rate = winRatePct.ToString("0.#", Inv) + "%";
        var core = winRatePct >= 50.0
            ? "🚀 챌린저 우세(" + rate + ") — 다음 가설: 훅 '" + hook + "' 유지 + BGM '" + bgm + "' 대체 무드로 더 과감한 변형"
            : "🛡️ 챔피언 방어(챌린저 " + rate + ") — 다음 가설: 룩 '" + look + "' 고정 · 훅만 바꾼 단일 변수 실험";
        if (pending > 0) core += " · ⏳ 대기 " + pending.ToString(Inv) + "건";
        return core + guardNote;
    }
}
