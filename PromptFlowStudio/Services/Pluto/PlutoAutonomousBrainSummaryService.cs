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
    /// <summary>⚖️ 전 모달리티(훅 · 룩 · BGM · 태그) 파레토 현황.</summary>
    public IReadOnlyList<PlutoParetoModalityStatus> ParetoModalities { get; init; } = Array.Empty<PlutoParetoModalityStatus>();
    /// <summary>⚖️ 다목적 근거가 있는 모달리티 수(0~4).</summary>
    public int ParetoEvidencedModalityCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < ParetoModalities.Count; i++) if (ParetoModalities[i].HasEvidence) n++;
            return n;
        }
    }
    /// <summary>⚖️ 전 모달리티 파레토 1줄(결정론 한국어).</summary>
    public string ParetoModalityLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>📺 시리즈 아크 · 편 간 유지율 현황.</summary>
    public PlutoSeriesArcStatus SeriesArc { get; init; } = PlutoSeriesArcStatus.Empty;
    /// <summary>📺 시리즈 아크 1줄(결정론 한국어).</summary>
    public string SeriesArcLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🧬 콜드스타트 경험적 베이즈 전이 현황.</summary>
    public PlutoColdStartTransferStatus ColdStartTransfer { get; init; } = PlutoColdStartTransferStatus.Empty;
    /// <summary>🧬 콜드스타트 전이 1줄(결정론 한국어).</summary>
    public string ColdStartTransferLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>⚔️ 미해결 Drive 분기 충돌 사본 수.</summary>
    public int UnresolvedConflictCount { get; init; }
    /// <summary>📥 다음 적재 때 적용될 충돌 해결 수.</summary>
    public int PendingConflictResolutions { get; init; }
    /// <summary>⚔️ 동기화 충돌 1줄(결정론 한국어).</summary>
    public string ConflictLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🧬 BGM · 태그 과거 이력 소급 결과(미연결 → null).</summary>
    public PlutoContextBackfillReport? Backfill { get; init; }
    /// <summary>📺 Step 26 — 실측 유튜브 성과(연결 영상 · 조회수 가중 CTR/유지율 · 선두 제목 프레이밍).</summary>
    public PlutoRealizedYouTubeSummary RealizedYouTube { get; init; } = PlutoRealizedYouTubeSummary.Empty;
    /// <summary>📺 Step 26 — 실측 유튜브 성과 요약 1줄(결정론 한국어).</summary>
    public string RealizedYouTubeLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>📐 Step 27 — 적용 기획안 적중률(카탈로그 채점 결과 파생).</summary>
    public PlutoBriefHitRateSummary BriefHitRate { get; init; } = PlutoBriefHitRateSummary.Empty;
    /// <summary>📐 Step 27 — 기획안 적중률 요약 1줄(평가 1건 이상일 때만 본문 노출).</summary>
    public string BriefHitRateLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
    /// <summary>🌐 Step 32 — 로캘별 성과(KO · EN · JA · ES · 연결/가중 CTR · 3초 · 완주 · 로캘 선두 제목).</summary>
    public IReadOnlyList<PlutoLocalePerformanceRow> LocaleBreakdown => RealizedYouTube.Locales;
    /// <summary>🌐 Step 32 — 로캘 분해 1줄(결정론 한국어).</summary>
    public string LocaleBreakdownLabel { get; init; } = PlutoAutonomousBrainSummaryService.NoneLabel;
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

    /// <summary>📺 시리즈 아크 메모리(미연결 → '기록 없음').</summary>
    public IPlutoSeriesArcMemoryService? SeriesArc { get; init; }

    /// <summary>⚔️ Drive 분기 충돌 현황(미연결 → 0).</summary>
    public IPlutoDriveHealthService? DriveHealth { get; init; }

    /// <summary>🧬 BGM · 태그 컨텍스트 알파 과거 이력 소급(미연결 → 생략) — 랭커 집계 '이전'에 1회 보장.</summary>
    public IPlutoContextAlphaBackfillService? Backfill { get; init; }

    /// <summary>📺 Step 26 — 렌더 카탈로그(YouTube 연결 · 워터마크 → 실측 성과 요약 · 미연결 → '연결 영상 없음').</summary>
    public IPlutoRenderCatalogService? Catalog { get; init; }

    /// <summary>⚔️ 충돌 1줄(결정론).</summary>
    public static string BuildConflictLabel(int unresolved, int pending)
    {
        if (unresolved <= 0 && pending <= 0) return "없음 · 두 PC 의 오프라인 기록이 갈라지면 덮기 전에 사본을 보관해요";
        var s = unresolved > 0 ? "미해결 " + unresolved.ToString(Inv) + "건 — 환경설정 › Drive 백업에서 병합/복원/무시" : "미해결 없음";
        return pending > 0 ? s + " · 적용 대기 " + pending.ToString(Inv) + "건(다음 실행 시 반영)" : s;
    }

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
        IReadOnlyList<PlutoParetoModalityStatus> modalities = Array.Empty<PlutoParetoModalityStatus>();
        var seriesArc = PlutoSeriesArcStatus.Empty;
        var coldStart = PlutoColdStartTransferStatus.Empty;
        PlutoContextBackfillReport? backfill = null;
        int conflicts = 0, pendingResolutions = 0;
        var realized = PlutoRealizedYouTubeSummary.Empty;
        var briefHitRate = PlutoBriefHitRateSummary.Empty;

        if (Backfill is not null)
        {
            try { backfill = await Backfill.EnsureBackfilledAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Backfill", ex); }   // [부분 구제]
        }
        if (DriveHealth is not null)
        {
            try { conflicts = DriveHealth.ListConflicts().Count; pendingResolutions = DriveHealth.PendingConflictResolutions; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Conflicts", ex); }
        }

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
                modalities = _ranker.GetParetoModalityStatus() ?? Array.Empty<PlutoParetoModalityStatus>();
                coldStart = _ranker.GetColdStartTransferStatus() ?? PlutoColdStartTransferStatus.Empty;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Ranker", ex); }
        }

        if (SeriesArc is not null)
        {
            try
            {
                await SeriesArc.EnsureLoadedAsync(ct).ConfigureAwait(false);
                await SeriesArc.SyncFromCatalogAsync(ct).ConfigureAwait(false);
                seriesArc = SeriesArc.GetStatus() ?? PlutoSeriesArcStatus.Empty;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.SeriesArc", ex); }   // [부분 구제]
        }

        if (Catalog is not null)
        {
            try
            {
                var clips = await Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false);
                realized = PlutoRealizedYouTubeSummary.Compute(clips);
                briefHitRate = PlutoBriefHitRateSummary.Compute(clips);   // 📐 Step 27 — 같은 스냅샷에서 파생(별도 카운터 없음)
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Realized", ex); }   // [부분 구제]
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
            ParetoModalities = modalities,
            ParetoModalityLabel = PlutoParetoFrontier.BuildModalityLabel(modalities),
            SeriesArc = seriesArc,
            SeriesArcLabel = PlutoSeriesArcMemoryService.BuildArcLabel(seriesArc),
            ColdStartTransfer = coldStart,
            ColdStartTransferLabel = PlutoEmpiricalBayes.BuildLabel(coldStart),
            UnresolvedConflictCount = conflicts,
            PendingConflictResolutions = pendingResolutions,
            ConflictLabel = BuildConflictLabel(conflicts, pendingResolutions),
            Backfill = backfill,
            RealizedYouTube = realized,
            RealizedYouTubeLabel = realized.ToLabel(),
            BriefHitRate = briefHitRate,
            BriefHitRateLabel = briefHitRate.ToLabel(),
            LocaleBreakdownLabel = realized.ToLocaleLabel(),
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
