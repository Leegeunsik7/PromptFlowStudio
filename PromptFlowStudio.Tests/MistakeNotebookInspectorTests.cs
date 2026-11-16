using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🛡️🩺 오답 노트 사후 진단 · 활성 가드 인스펙터 · 원클릭 오탐 용서.</summary>
public sealed class MistakeNotebookInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_mistake_inspector_" + Guid.NewGuid().ToString("N"));

    public MistakeNotebookInspectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PlutoVideoPerformanceTelemetry Telemetry(string hook, string tag, double hookPct, double completionPct, double ctrPct)
        => new("vid-" + Guid.NewGuid().ToString("N"), "YouTube", hook, PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { tag },
            hookPct, completionPct, ctrPct, DateTimeOffset.UtcNow);

    [Fact]
    public async Task MistakeNotebook_RecordsStructuredDiagnosticReason_AndListsActiveRules()
    {
        var now = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        using var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"), () => now);
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, new PlutoDriveStorageManager(null, _dir));

        Assert.Equal(PlutoMistakeNotebookService.DimensionHookRetention, PlutoClosedLoopReinforcementService.ClassifyFailureDimension(Telemetry("h", "t", 5, 40, 5)));
        Assert.Equal(PlutoMistakeNotebookService.DimensionLowCtr, PlutoClosedLoopReinforcementService.ClassifyFailureDimension(Telemetry("h", "t", 50, 40, 0.2)));
        Assert.Equal(PlutoMistakeNotebookService.DimensionCompletionDrop, PlutoClosedLoopReinforcementService.ClassifyFailureDimension(Telemetry("h", "t", 40, 2, 5)));
        Assert.Equal(PlutoMistakeNotebookService.DimensionGeneral, PlutoClosedLoopReinforcementService.ClassifyFailureDimension(Telemetry("h", "t", 50, 40, 5)));

        var o1 = await loop.IngestPerformanceAsync(Telemetry("지루한 인트로 훅", "slow-pan", 5, 40, 5));   // 결손 0.88 → 벌점 1.88 ≥ 1.8
        Assert.True(o1.RecordedMistakePenalty);
        now = now.AddMinutes(5);
        var o2 = await loop.IngestPerformanceAsync(Telemetry("무난한 타이틀 훅", "plain-thumb", 50, 40, 0.2));   // CTR 결손 → LowCtr
        Assert.True(o2.RecordedMistakePenalty);
        now = now.AddMinutes(5);
        await nb.RecordDiagnosedMistakeAsync(new[] { "long-middle" }, 2.0, "completiondrop", "manual");
        now = now.AddMinutes(5);
        await nb.RecordNegativeFeedbackAsync(new[] { "loud-bgm" }, 2.0, "👎 t1");   // 일반 벌점

        var rules = nb.GetActiveMistakeRules();
        Assert.Equal(new[] { "loud-bgm", "long-middle" }, rules.Take(2).Select(r => r.PatternKey).ToArray());   // 최신순
        var hook = Assert.Single(rules, r => r.PatternKey == "지루한 인트로 훅");
        Assert.Equal(PlutoMistakeNotebookService.DimensionHookRetention, hook.FailureDimension);
        Assert.Equal("초반 3초 훅 이탈 (유지율 기준 미달)", hook.DiagnosticReasonKorean);
        Assert.True(hook.PenaltyWeight >= PlutoMistakeNotebookService.BlockThreshold);
        Assert.Equal("썸네일·제목 클릭 유인 부족 (CTR 미달)", Assert.Single(rules, r => r.PatternKey == "plain-thumb").DiagnosticReasonKorean);
        Assert.Equal("중반 전개 이탈 (완주율 기준 미달)", Assert.Single(rules, r => r.PatternKey == "long-middle").DiagnosticReasonKorean);
        Assert.Equal(PlutoMistakeNotebookService.DimensionGeneral, Assert.Single(rules, r => r.PatternKey == "loud-bgm").FailureDimension);
        Assert.Equal(nb.GetSuppressedTokens().Count, rules.Count);
        Assert.Equal(2, nb.GetActiveMistakeRules(2).Count);

        // 💾 진단 필드 영속 → 재적재 후 동일
        using var reloaded = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"), () => now);
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(PlutoMistakeNotebookService.DimensionHookRetention, reloaded.GetActiveMistakeRules().Single(r => r.PatternKey == "지루한 인트로 훅").FailureDimension);

        // ↩️ 구버전 파일(진단 필드 없음) 호환 → GeneralPenalty
        var legacy = Path.Combine(_dir, "legacy.json");
        await File.WriteAllTextAsync(legacy, "{\"Version\":1,\"Entries\":{\"old-tag\":{\"Label\":\"old-tag\",\"RawPenalty\":2.5,\"LastUtc\":\"2026-11-01T09:00:00Z\",\"IsSuppressed\":true,\"Hits\":3}}}");
        using var old = new PlutoMistakeNotebookService(legacy, () => now);
        await old.EnsureLoadedAsync();
        var oldRule = Assert.Single(old.GetActiveMistakeRules());
        Assert.Equal(PlutoMistakeNotebookService.DimensionGeneral, oldRule.FailureDimension);
        Assert.False(string.IsNullOrWhiteSpace(oldRule.DiagnosticReasonKorean));
    }

    [Fact]
    public async Task MistakeNotebook_RemoveMistakeRule_PersistsRemovalAndRestoresPatternEligibility()
    {
        var storage = new PlutoDriveStorageManager(null, _dir);   // Drive 비활성 · 임시 폴백
        using var nb = new PlutoMistakeNotebookService(storage);
        const string weak = "느린 페이드 훅";
        await nb.RecordDiagnosedMistakeAsync(new[] { weak, "keep-me" }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "ab-experiment: x");

        var ranker = new PlutoAdaptivePatternRanker(null, nb);
        Assert.Contains(weak, nb.GetSuppressedTokens());
        Assert.True(ranker.RankOptions(PlutoPatternKind.Hook, new[] { weak, "클린 훅" }).First(o => o.Key == weak || o.DisplayLabel == weak).IsPenalized);
        Assert.Contains(weak, PlutoTrendAnalyzer.SafeSuppressed(nb)!);

        Assert.True(await nb.RemoveMistakeRuleAsync("  " + weak + " "));
        Assert.False(await nb.RemoveMistakeRuleAsync(weak));   // 멱등 — 이미 해제
        Assert.False(await nb.RemoveMistakeRuleAsync(""));

        Assert.DoesNotContain(weak, nb.GetSuppressedTokens());
        Assert.Equal(0, nb.GetEffectivePenalty(weak));
        var hooks = ranker.RankOptions(PlutoPatternKind.Hook, new[] { weak, "클린 훅" });
        Assert.True(hooks[0].Key == weak || hooks[0].DisplayLabel == weak);   // 사전 선호 1순위 복귀
        Assert.False(hooks[0].IsPenalized);
        Assert.Single(nb.GetActiveMistakeRules(), r => r.PatternKey == "keep-me");

        // 💾 원자 영속 → 새 인스턴스에서도 해제 유지
        Assert.True(File.Exists(Path.Combine(_dir, PlutoMistakeNotebookService.FileName)));
        using var reloaded = new PlutoMistakeNotebookService(storage);
        await reloaded.EnsureLoadedAsync();
        Assert.DoesNotContain(weak, reloaded.GetSuppressedTokens());
        Assert.Contains("keep-me", reloaded.GetSuppressedTokens());
    }

    [Fact]
    public async Task BrainSummary_ReflectsUpdatedGuardCount_AfterMistakeRuleForgiveness()
    {
        using var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        await nb.RecordDiagnosedMistakeAsync(new[] { "sad-face", "dark-film", "slow-intro" }, 2.0, PlutoMistakeNotebookService.DimensionLowCtr);
        var svc = new PlutoAutonomousBrainSummaryService(null, new PlutoAdaptivePatternRanker(null, nb), nb);

        Assert.Equal(3, (await svc.GetSummaryAsync()).ActiveMistakeGuardCount);
        var changed = 0;
        nb.Changed += () => changed++;

        Assert.True(await nb.RemoveMistakeRuleAsync("dark-film"));
        Assert.Equal(1, changed);
        var after = await svc.GetSummaryAsync();
        Assert.Equal(2, after.ActiveMistakeGuardCount);
        Assert.Equal(after.ActiveMistakeGuardCount, nb.GetActiveMistakeRules().Count);
        Assert.DoesNotContain(nb.GetActiveMistakeRules(), r => r.PatternKey == "dark-film");
    }

    private static PlutoVariantABPlan StepEightPlan(string id, string hookB)
    {
        var cues = new[] { PromptFlowStudio.Services.Export.SubtitleCue.Precise(0.2, 4.95, "a") };
        var a = new PlutoExperimentVariant(PlutoVariantABGenerator.ChampionLabel, "이 순간을 기다렸어요", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", "", cues);
        var b = new PlutoExperimentVariant(PlutoVariantABGenerator.ChallengerLabel, hookB, PlutoColorGradingCompiler.GoldenHourWarmName, "Lullaby", "", cues);
        return new PlutoVariantABPlan(id, "돌잔치", a, b, "h");
    }

    [Fact]
    public async Task ExperimentLedger_LosingChallengerWithHealthyHookButLowCompletion_ActivatesCompletionDropGuard()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        using var ledger = new PlutoExperimentLedgerService(loop);
        const string hookB = "중반 설명이 길어요";

        // 챌린저: 3초 48 %(정상) · 완주 10 %(결손 66.7 %) · CTR 3 %(정상) → 기존엔 훅 정상이라 면제되던 패자
        var loser = new PlutoExperimentMetricsInput(48, 10, 3);
        var (deficit, dim) = PlutoExperimentLedgerService.LoserSteepestDeficit(loser);
        Assert.Equal(PlutoMistakeNotebookService.DimensionCompletionDrop, dim);
        Assert.Equal(20.0 / 30.0, deficit, 9);

        var r = await ledger.EvaluateExperimentAsync(StepEightPlan("exp-step8-comp", hookB), new PlutoExperimentMetricsInput(60, 50, 5), loser);

        Assert.Equal(PlutoExperimentLedgerService.WinnerChampion, r.WinnerLabel);
        Assert.True(r.LoserPenalized);
        Assert.Contains("완주 이탈 가드 활성화", r.SummaryMessage, StringComparison.Ordinal);
        Assert.True(notebook.GetEffectivePenalty(hookB) >= PlutoMistakeNotebookService.BlockThreshold);
        var rule = Assert.Single(notebook.GetActiveMistakeRules(), x => x.PatternKey.Contains(hookB, StringComparison.Ordinal));
        Assert.Equal(PlutoMistakeNotebookService.DimensionCompletionDrop, rule.FailureDimension);
        Assert.Equal(PlutoMistakeNotebookService.ReasonCompletionDrop, rule.DiagnosticReasonKorean);
        Assert.Equal(PlutoClosedLoopReinforcementService.PenaltyForSteepestDeficit(deficit, dim), rule.PenaltyWeight, 2);
    }

    [Fact]
    public async Task ExperimentLedger_LosingChallengerWithHealthyHookButLowCtr_ActivatesLowCtrGuard()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        using var ledger = new PlutoExperimentLedgerService(loop);
        const string hookB = "썸네일이 흐릿해요";

        // 챌린저: 3초 48 % · 완주 40 % · CTR 0.4 %(결손 73.3 %) → 저 CTR 가드
        var loser = new PlutoExperimentMetricsInput(48, 40, 0.4);
        var (deficit, dim) = PlutoExperimentLedgerService.LoserSteepestDeficit(loser);
        Assert.Equal(PlutoMistakeNotebookService.DimensionLowCtr, dim);
        Assert.Equal(1.1 / 1.5, deficit, 9);

        var r = await ledger.EvaluateExperimentAsync(StepEightPlan("exp-step8-ctr", hookB), new PlutoExperimentMetricsInput(60, 50, 5), loser);

        Assert.True(r.LoserPenalized);
        Assert.Contains("저 CTR 가드 활성화", r.SummaryMessage, StringComparison.Ordinal);
        var rule = Assert.Single(notebook.GetActiveMistakeRules(), x => x.PatternKey.Contains(hookB, StringComparison.Ordinal));
        Assert.Equal(PlutoMistakeNotebookService.DimensionLowCtr, rule.FailureDimension);
        Assert.Equal(PlutoMistakeNotebookService.ReasonLowCtr, rule.DiagnosticReasonKorean);
        Assert.True(rule.PenaltyWeight >= PlutoMistakeNotebookService.BlockThreshold);
        Assert.Equal(0.0, notebook.GetEffectivePenalty("이 순간을 기다렸어요"), 9);   // 승자 훅 무벌점
    }

    [Fact]
    public async Task ClosedLoopReinforcement_LowCompletionWithHealthyHook_ScalesPenaltyProportionallyToDeficit()
    {
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var svc = new PlutoClosedLoopReinforcementService(null, notebook);

        // 훅 결손 최대 → 기존 선형 벌점 그대로(하위 호환)
        Assert.Equal(PlutoClosedLoopReinforcementService.PenaltyFor(0.5),
            PlutoClosedLoopReinforcementService.PenaltyForSteepestDeficit(0.5, PlutoMistakeNotebookService.DimensionHookRetention), 9);
        Assert.Equal(0.0, PlutoClosedLoopReinforcementService.PenaltyForSteepestDeficit(0.0, PlutoMistakeNotebookService.DimensionCompletionDrop), 9);

        // 경미한 완주 결손(20 % → 결손 1/3) → 비례 벌점 · 차단 미만
        var mild = await svc.IngestPerformanceAsync(Telemetry("완만한 전개", "#일상", 48, 20, 3));
        Assert.True(mild.RecordedMistakePenalty);
        Assert.Equal(PlutoMistakeNotebookService.DimensionCompletionDrop, mild.FailureDimension);
        Assert.Equal(1.0 + (10.0 / 30.0) * PlutoClosedLoopReinforcementService.NonHookDeficitGain, mild.PenaltySeverity, 9);
        Assert.False(mild.GuardActivated);
        Assert.DoesNotContain(notebook.GetActiveMistakeRules(), x => x.PatternKey.Contains("완만한 전개", StringComparison.Ordinal));

        // 심각한 완주 결손(10 % → 결손 2/3) → 더 큰 벌점 · 즉시 가드
        var severe = await svc.IngestPerformanceAsync(Telemetry("긴 내레이션", "#브이로그", 48, 10, 3));
        Assert.True(severe.PenaltySeverity > mild.PenaltySeverity);
        Assert.Equal(1.0 + (20.0 / 30.0) * PlutoClosedLoopReinforcementService.NonHookDeficitGain, severe.PenaltySeverity, 9);
        Assert.True(severe.GuardActivated);
        Assert.Contains("완주 이탈 가드 활성화", severe.SummaryMessage, StringComparison.Ordinal);
        var rule = Assert.Single(notebook.GetActiveMistakeRules(), x => x.PatternKey.Contains("긴 내레이션", StringComparison.Ordinal));
        Assert.Equal(PlutoMistakeNotebookService.DimensionCompletionDrop, rule.FailureDimension);

        // 결손 없음(훅 · 완주 · CTR 모두 기준 이상) → 벌점 없음
        var ok = await svc.IngestPerformanceAsync(Telemetry("무난한 훅", "#무난", 48, 40, 3));
        Assert.False(ok.RecordedMistakePenalty);
        Assert.Null(ok.FailureDimension);
    }
}
