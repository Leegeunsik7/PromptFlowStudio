using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧠 Pluto 4.6 두뇌 리포트 스냅샷 — 실험 판정/대기 · 챌린저 승률 · 학습 1위 훅/룩/BGM · 오답 노트 차단 수 · 다음 가설 배너.</summary>
public sealed record PlutoBrainSummarySnapshot(int TotalEvaluatedExperiments, int PendingExperimentsCount, double ChallengerWinRatePct, string TopHookLabel, string TopLookLabel,
    string TopBgmLabel, int ActiveMistakeGuardCount, string NextHypothesisBanner);

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
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BrainSummary.Ranker", ex); }
        }

        var winRate = evaluated > 0 ? Math.Round(challengerWins * 100.0 / evaluated, 1, MidpointRounding.AwayFromZero) : 0.0;
        return new PlutoBrainSummarySnapshot(evaluated, pending, winRate, hook, look, bgm, guards, BuildHypothesisBanner(evaluated, pending, winRate, hook, look, bgm, guards));
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
