using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🎰🔗⏳ Step 12 — UCB1 밴딧 탐색 · 반감기 시간 감쇠(보상 14일 · 벌점 21일) · 2-way 조합 귀속(pair: / domain_hook:) · 두뇌 리포트 표면화 ·
/// 자가 감사 수정(폐루프 중복 차단 장부 보존 · 오답 노트 낡은 차단 래치 정산).
/// </summary>
public sealed class PlutoBanditAndSynergyIntelligenceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private const string LookX = PlutoColorGradingCompiler.GoldenHourWarmName;
    private const string LookY = PlutoColorGradingCompiler.VintageFilm16mmName;
    private const string HookA = "0.5초 휩줌 훅";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step12_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;

    public PlutoBanditAndSynergyIntelligenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };
    private PlutoMistakeNotebookService Notebook(string name = "nb.json") => new(Path.Combine(_dir, name), () => _now);

    private static PlutoVideoPerformanceTelemetry Breakout(string id, string hook, string look, string? domain = null)
        => new(id, "YouTube Shorts", hook, look, "Lullaby", Array.Empty<string>(), 82, 61, 9.4, DateTimeOffset.UtcNow) { Domain = domain };

    private static PlutoVideoPerformanceTelemetry Poor(string id, string hook, string look, string? domain = null)
        => new(id, "YouTube Shorts", hook, look, "Lullaby", Array.Empty<string>(), 20, 10, 0.5, DateTimeOffset.UtcNow) { Domain = domain };

    // ── ① ⏳ 반감기 감쇠 ──

    [Fact]
    public async Task HalfLifeDecay_RehabilitatesAgedMistakePenalty_WhileFreshPenaltyStaysActive()
    {
        using var notebook = Notebook();
        var ranker = new PlutoAdaptivePatternRanker(null, notebook);

        await notebook.RecordNegativeFeedbackAsync(new[] { "낡은 실패 훅" }, 2.0);
        Assert.Contains("낡은 실패 훅", notebook.GetSuppressedTokens());
        Assert.True(ranker.RankOptions(PlutoPatternKind.Hook, new[] { "낡은 실패 훅" })[0].MistakePenaltyFactor >= PlutoAdaptivePatternRanker.MaxPenaltyFactor);

        // AsOfUtc — 시계를 움직이지 않고도 재현 가능한 감쇠 평가(21일 반감기)
        Assert.Equal(21.0, PlutoMistakeNotebookService.HalfLifeDays);
        Assert.Equal(1.0, notebook.GetEffectivePenalty("낡은 실패 훅", T0.AddDays(21)), 9);
        Assert.Equal(0.5, notebook.GetEffectivePenalty("낡은 실패 훅", T0.AddDays(42)), 9);
        Assert.Equal(2.0, notebook.GetEffectivePenalty("낡은 실패 훅"), 9);   // 주입 시계는 아직 T0

        // 42일(반감기 2회) 경과 — 재범 없음 → 0.5 < 해제 임계 0.8 → 자연 복권 · 같은 날 새 실패는 차단 유지
        _now = T0.AddDays(42);
        await notebook.RecordNegativeFeedbackAsync(new[] { "방금 실패한 훅" }, 2.0);
        var suppressed = notebook.GetSuppressedTokens();
        Assert.DoesNotContain("낡은 실패 훅", suppressed);
        Assert.Contains("방금 실패한 훅", suppressed);

        var ranked = ranker.RankOptions(PlutoPatternKind.Hook, new[] { "낡은 실패 훅", "방금 실패한 훅" });
        Assert.Equal("낡은 실패 훅", ranked[0].DisplayLabel);                                  // 복권된 후보가 앞섬
        Assert.Equal(0.25, ranked[0].MistakePenaltyFactor, 6);                                 // 0.5 / 2 — 잔여 감점만
        Assert.True(ranked[1].MistakePenaltyFactor >= PlutoAdaptivePatternRanker.MaxPenaltyFactor);

        var status = notebook.GetDecayStatus();
        Assert.Equal(2, status.TrackedCount);
        Assert.Equal(1, status.ActiveGuardCount);
        Assert.Equal(1, status.RecoveringCount);
        Assert.Equal(37.5, status.DecayedAwayPct, 1);                                          // (4.0 − 2.5) / 4.0
        Assert.Equal(Math.Round(21.0 * Math.Log2(2.0 / 0.8), 1), status.DaysUntilNextRelease!.Value, 1);

        // 성과 패턴 보상도 14일 반감기 — 재확인 없는 과거 승자는 기본(1.0)으로 수렴
        _now = T0;
        using var learning = Learning();
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:과거 승자" }, 1.30, "xp:test", 0);
        Assert.Equal(1.30, learning.GetPatternWeight("hook:과거 승자"), 9);
        Assert.Equal(1.15, learning.GetPatternWeight("hook:과거 승자", T0.AddDays(14)), 9);
        Assert.Equal(1.075, learning.GetPatternWeight("hook:과거 승자", T0.AddDays(28)), 9);
        Assert.Equal(0.5, learning.GetPatternTrials("hook:과거 승자", T0.AddDays(14)), 9);     // 시행 수도 감쇠 → 다시 탐색 대상
        _now = T0.AddDays(14);
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:과거 승자" }, 1.20, "xp:test", 0);
        Assert.Equal(1.15 * 1.20, learning.GetPatternWeight("hook:과거 승자"), 9);             // 감쇠 후 재강화(누적 고착 방지)
    }

    // ── ② 🎰 UCB1 ──

    [Fact]
    public async Task Ucb1_ExplorationBonus_ElevatesUnderSampledChallenger_OverOverSampledMediocreArm()
    {
        // 순수 수식 — posteriorMean + C × √(ln(total + 1) / (arm + 1))
        Assert.Equal(1.0 + 0.5 * Math.Sqrt(Math.Log(21.0) / 1.0), PlutoAdaptivePatternRanker.ComputeUcbScore(1.0, 0.5, 20, 0), 12);
        Assert.Equal(1.15 + 0.5 * Math.Sqrt(Math.Log(21.0) / 16.0), PlutoAdaptivePatternRanker.ComputeUcbScore(1.15, 0.5, 20, 15), 12);
        Assert.True(PlutoAdaptivePatternRanker.ComputeUcbScore(1.0, 0.5, 20, 0) > PlutoAdaptivePatternRanker.ComputeUcbScore(1.15, 0.5, 20, 15));
        Assert.Equal(1.0, PlutoAdaptivePatternRanker.ComputeUcbScore(1.0, 0.5, 0, 0), 12);    // 이력 없는 시스템 → 보너스 0(활용 순위 불변)

        const string theme = "여행";
        var themed = PlutoMontageStoryGenerator.TemplateHookLines(theme);
        var mediocreHook = themed[0];                                                          // 12회 검증 · ×1.15 — 평범
        var freshHook = themed[1];                                                             // 미검증
        var strongHook = PlutoMontageStoryGenerator.TemplateHookLines("생일")[0];              // 강한 챔피언
        const string strongLook = PlutoColorGradingCompiler.NaturalPastelName;
        const string mediocreLook = PlutoColorGradingCompiler.GoldenHourWarmName;
        const string freshLook = PlutoColorGradingCompiler.VintageFilm16mmName;

        using var learning = Learning();
        using var notebook = Notebook();
        for (var i = 0; i < 3; i++) await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + strongHook, "look:" + strongLook }, 1.35, "xp:test", 0);
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + mediocreHook, "look:" + mediocreLook }, 1.15, "xp:test", 0);
        for (var i = 0; i < 11; i++) await learning.RecordPatternTrialsAsync(new[] { "hook:" + mediocreHook, "look:" + mediocreLook });
        Assert.Equal(12.0, learning.GetPatternTrials("hook:" + mediocreHook), 9);
        Assert.Equal(1.15, learning.GetPatternWeight("hook:" + mediocreHook), 9);              // 시행 기록은 가중치 불변

        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        // 활용(그리디) 순위 — 평범하지만 검증된 후보가 미검증 후보를 앞선다
        var exploit = ranker.RankOptions(PlutoPatternKind.Hook);
        Assert.Equal(strongHook, exploit[0].DisplayLabel);
        var exploitList = exploit.Select(o => o.DisplayLabel).ToList();
        Assert.True(exploitList.IndexOf(mediocreHook) < exploitList.IndexOf(freshHook));
        var exploitLooks = ranker.RankOptions(PlutoPatternKind.Look).Select(o => o.DisplayLabel).ToList();
        Assert.Equal(new[] { strongLook, mediocreLook, freshLook }, exploitLooks);

        // 탐색(UCB1) 순위 — 미검증 후보가 과다 검증된 평범한 후보를 넘고 · 강한 승자는 그대로 1위
        var explore = ranker.RankForExploration(PlutoPatternKind.Hook);
        var exploreList = explore.Select(o => o.DisplayLabel).ToList();
        Assert.Equal(strongHook, explore[0].DisplayLabel);
        Assert.True(exploreList.IndexOf(freshHook) < exploreList.IndexOf(mediocreHook));
        var fresh = explore.First(o => o.DisplayLabel == freshHook);
        var mediocre = explore.First(o => o.DisplayLabel == mediocreHook);
        Assert.Equal(0.0, fresh.ArmTrials, 9);
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC * Math.Sqrt(Math.Log(16.0) / 1.0), fresh.ExplorationBonus, 9);
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC * Math.Sqrt(Math.Log(16.0) / 13.0), mediocre.ExplorationBonus, 9);
        Assert.Equal(fresh.FinalScore + fresh.ExplorationBonus, fresh.UcbScore, 12);
        Assert.Equal(new[] { strongLook, freshLook, mediocreLook }, ranker.RankForExploration(PlutoPatternKind.Look).Select(o => o.DisplayLabel).ToArray());

        // A/B 생성기 — 챌린저 B는 그리디 차순위(평범)가 아니라 덜 검증된 훅·룩
        var gen = new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-ucb");
        var plan = await gen.BuildVariantPairAsync(theme);
        Assert.Equal(strongHook, plan.ChampionA.HookStyle);
        Assert.Equal(strongLook, plan.ChampionA.ColorLookId);
        Assert.Equal(freshHook, plan.ChallengerB.HookStyle);
        Assert.Equal(freshLook, plan.ChallengerB.ColorLookId);
        Assert.True(plan.IsValidExperiment);
        Assert.True(plan.IsExplorationChallenger);
        Assert.Contains("UCB1 탐색 보너스", plan.HypothesisSummary, StringComparison.Ordinal);

        var telemetry = ranker.GetExplorationTelemetry();
        Assert.Equal(30.0, telemetry.TotalTrials, 6);                                          // 훅 15 + 룩 15
        Assert.True(telemetry.ExplorationCandidateCount > 0);
        Assert.InRange(telemetry.ExplorationRatePct, 0.1, 99.9);
    }

    [Fact]
    public async Task TrendOpportunity_ReservesLastBriefForExploration_OnlyWhenBanditHasHistory()
    {
        using var learning = Learning();
        using var notebook = Notebook();
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var engine = new PlutoTrendOpportunityEngine(ranker, notebook, utcNow: () => T0);

        var cold = await engine.GenerateDailyBriefsAsync(3);
        Assert.Equal(3, cold.Count);
        Assert.All(cold, b => Assert.False(b.IsExploration));                                  // 이력 없음 → 전부 활용(기존 동작)

        var provenHook = PlutoMontageStoryGenerator.TemplateHookLines("생일")[0];
        for (var i = 0; i < 3; i++) await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + provenHook, "look:" + PlutoColorGradingCompiler.NaturalPastelName }, 1.35, "xp:test", 0);

        var briefs = await engine.GenerateDailyBriefsAsync(3);
        Assert.Equal(provenHook, briefs[0].RecommendedHook);                                   // 검증된 승자 활용
        Assert.Equal(provenHook, briefs[1].RecommendedHook);
        Assert.False(briefs[0].IsExploration || briefs[1].IsExploration);
        Assert.True(briefs[2].IsExploration);                                                  // 마지막 1건 = 탐색 기획안
        Assert.NotEqual(provenHook, briefs[2].RecommendedHook);
        Assert.NotEqual(PlutoColorGradingCompiler.NaturalPastelName, briefs[2].RecommendedColorLook);
        Assert.Contains("🎰 탐색 기획", briefs[2].RationaleBadge, StringComparison.Ordinal);
        Assert.Contains("opening hook: " + briefs[2].RecommendedHook, briefs[2].ReadyPrompt, StringComparison.Ordinal);
    }

    // ── ③ 🔗 2-way 조합 귀속 ──

    [Fact]
    public async Task PairAttribution_IsolatesFailingHookLookPair_WithoutPoisoningHookInOtherPairing()
    {
        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var pairX = PlutoPairAttribution.PairKey(HookA, LookX)!;
        var pairY = PlutoPairAttribution.PairKey(HookA, LookY)!;
        Assert.Equal("pair:" + HookA + "+" + LookX, pairX);

        // Hook_A + Look_X 돌파 → 단일 특성 + 조합 키 동시 강화
        var win = await loop.IngestPerformanceAsync(Breakout("vid-ax", HookA, LookX));
        Assert.True(win.IsBreakoutSuccess);
        Assert.Equal(win.PatternWeightMultiplier, learning.GetPatternWeight(pairX), 9);
        Assert.True(ranker.GetPairSynergy(HookA, LookX).IsStrongSynergy);

        // Hook_A + Look_Y 부진 → 벌점은 조합 키에만 격리(Hook_A 전역 오염 없음)
        var fail = await loop.IngestPerformanceAsync(Poor("vid-ay", HookA, LookY));
        Assert.True(fail.RecordedMistakePenalty);
        Assert.Equal(pairY, fail.IsolatedPairKey);
        Assert.Contains("조합 격리", fail.SummaryMessage, StringComparison.Ordinal);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(HookA), 9);
        Assert.Equal(fail.PenaltySeverity, notebook.GetEffectivePenalty(pairY), 9);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(pairX), 9);

        // 조합 가드는 조합 집합에만 — 단일 특성 차단 · LLM 부정 제약 · 부분 일치 차단으로 새지 않는다
        Assert.True(fail.GuardActivated);
        Assert.Contains(pairY, notebook.GetSuppressedCompositeKeys());
        Assert.Empty(notebook.GetSuppressedTokens());
        Assert.Equal(string.Empty, notebook.BuildNegativeConstraintPrompt());
        Assert.Contains(notebook.GetActiveMistakeRules(), r => r.PatternKey == pairY);         // 인스펙터에서 [해제] 가능

        // 랭커 — Hook_A는 여전히 1위(비감점) · 조합별 시너지/역시너지 분리
        var top = ranker.GetCurrentRecommendations().TopHook;
        Assert.Equal(HookA, top.DisplayLabel);
        Assert.False(top.IsPenalized);
        Assert.True(ranker.GetPairSynergy(HookA, LookY).IsAntiSynergy);
        Assert.False(ranker.GetPairSynergy(HookA, LookX).IsAntiSynergy);
        Assert.True(ranker.GetPairSynergy(HookA, LookX).Score > ranker.GetPairSynergy(HookA, LookY).Score);
        Assert.Equal(pairX, ranker.GetTopSynergyPair()!.PairKey);

        // A/B 합성 — 챔피언은 시너지 조합(A × X) · 챌린저는 역시너지 조합(챌린저 훅 × 감점 룩)을 피함
        var plan = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-pair").BuildVariantPairAsync("여행");
        Assert.Equal(HookA, plan.ChampionA.HookStyle);
        Assert.Equal(LookX, plan.ChampionA.ColorLookId);
        Assert.True(plan.ChampionSynergyScore >= PlutoPairAttribution.StrongSynergyScore);
        Assert.False(ranker.GetPairSynergy(plan.ChallengerB.HookStyle, plan.ChallengerB.ColorLookId).IsAntiSynergy);

        // 대조군 — 성공 이력 없는 훅의 실패는 기존대로 전역 벌점
        var fresh = await loop.IngestPerformanceAsync(Poor("vid-by", "검증 안 된 훅", LookY));
        Assert.Null(fresh.IsolatedPairKey);
        Assert.True(notebook.GetEffectivePenalty("검증 안 된 훅") > 0);

        // 용서(해제) → 조합 감점 즉시 소멸
        Assert.True(await notebook.RemoveMistakeRuleAsync(pairY));
        Assert.False(ranker.GetPairSynergy(HookA, LookY).IsAntiSynergy);
    }

    [Fact]
    public async Task PairAttribution_IsolatesDomainHookFailure_WhenHookSucceedsInAnotherDomain()
    {
        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var baby = PlutoPromptDomain.BabyStudio.ToString();
        var general = PlutoPromptDomain.GeneralCinematic.ToString();

        Assert.True((await loop.IngestPerformanceAsync(Breakout("vid-baby", HookA, LookX, baby))).IsBreakoutSuccess);
        var fail = await loop.IngestPerformanceAsync(Poor("vid-general", HookA, LookX, general));   // 같은 훅·룩, 다른 도메인

        var domainKey = PlutoPairAttribution.DomainHookKey(general, HookA)!;
        Assert.Equal("domain_hook:" + general + "+" + HookA, domainKey);
        Assert.Equal(domainKey, fail.IsolatedPairKey);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(HookA), 9);                             // 훅 전역 무오염
        Assert.Equal(0.0, notebook.GetEffectivePenalty(PlutoPairAttribution.PairKey(HookA, LookX)!), 9);   // 다른 도메인에서 성공한 조합도 무오염
        Assert.True(notebook.GetEffectivePenalty(domainKey) > 0);
        Assert.True(ranker.GetDomainHookSynergy(general, HookA).IsAntiSynergy);
        Assert.True(ranker.GetDomainHookSynergy(baby, HookA).IsStrongSynergy);

        Assert.True(PlutoPairAttribution.TrySplitDomainHook(domainKey, out var d, out var h));
        Assert.Equal(general, d);
        Assert.Equal(HookA, h);
        Assert.True(PlutoPairAttribution.TrySplitPair(PlutoPairAttribution.PairKey("A+B 훅", LookX), out var ph, out var pl));   // 훅에 '+'가 있어도 왕복
        Assert.Equal("A+B 훅", ph);
        Assert.Equal(LookX, pl);
    }

    // ── ④ 🧠 두뇌 리포트 ──

    [Fact]
    public async Task BrainSummary_SurfacesTopSynergyPair_ExplorationTelemetry_AndDecayStatus()
    {
        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        using var ledger = new PlutoExperimentLedgerService(null);
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var svc = new PlutoAutonomousBrainSummaryService(ledger, ranker, notebook);

        var cold = await svc.GetSummaryAsync();
        Assert.Equal(PlutoAutonomousBrainSummaryService.NoneLabel, cold.TopSynergyPairLabel);
        Assert.Equal(0.0, cold.ExplorationRatePct, 9);
        Assert.Contains("기록 없음", cold.MistakeDecayLabel, StringComparison.Ordinal);

        await loop.IngestPerformanceAsync(Breakout("vid-ax", HookA, LookX));
        await loop.IngestPerformanceAsync(Poor("vid-ay", HookA, LookY));                       // 조합 격리 가드 1
        await notebook.RecordNegativeFeedbackAsync(new[] { "잔잔한 피아노" }, 1.0);            // 회복 중 1

        var s = await svc.GetSummaryAsync();
        Assert.Equal(HookA + " × " + LookX, s.TopSynergyPairLabel);
        Assert.True(s.TopSynergyScore >= PlutoPairAttribution.StrongSynergyScore);
        Assert.True(s.TotalBanditTrials >= 4.0);                                               // 훅 2 + 룩 2
        Assert.True(s.ExplorationCandidateCount > 0);
        Assert.True(s.ExplorationRatePct > 0);
        Assert.Equal(0, s.ActiveMistakeGuardCount);                                            // 단일 특성 가드 수는 조합 가드와 분리
        Assert.Equal(1, s.MistakeDecay.ActiveGuardCount);
        Assert.Equal(1, s.MistakeDecay.CompositeGuardCount);
        Assert.Equal(1, s.MistakeDecay.RecoveringCount);
        Assert.Contains("가드 1(조합 1)", s.MistakeDecayLabel, StringComparison.Ordinal);
        Assert.Contains("반감기 21일", s.MistakeDecayLabel, StringComparison.Ordinal);
        Assert.Contains("다음 자연 해제", s.MistakeDecayLabel, StringComparison.Ordinal);

        // 시간 경과 → 감쇠 반영(조합 가드 자연 해제 · 시너지 감쇠)
        _now = T0.AddDays(63);
        var aged = await svc.GetSummaryAsync();
        Assert.Equal(0, aged.MistakeDecay.ActiveGuardCount);
        Assert.True(aged.MistakeDecay.DecayedAwayPct > 80.0);
        Assert.True(aged.TopSynergyScore < s.TopSynergyScore);

        // Workspace — 접이식 두뇌 리포트 카드 본문에만 표면화(상단 3행 헤더 행 수 불변)
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(strip > 0 && tools > strip);
        var body = ws.Substring(strip, tools - strip);
        Assert.Contains("최강 시너지 조합", body, StringComparison.Ordinal);
        Assert.Contains("data-brain-synergy=", body, StringComparison.Ordinal);
        Assert.Contains("data-brain-explore=", body, StringComparison.Ordinal);
        Assert.Contains("data-brain-decay=", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        Assert.True(body.IndexOf("data-brain-synergy=", StringComparison.Ordinal) > body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal));
    }

    // ── ⑤ 🩹 자가 감사 수정 ──

    /// <summary>저장소가 '읽기 실패'를 빈 문자열로 돌려줄 때(Drive 동기화 잠금) 기존 중복 차단 장부를 덮어쓰지 않는다.</summary>
    [Fact]
    public async Task SelfAudit_ClosedLoopLedger_UnreadableRead_DoesNotWipeExistingDedupLedger()
    {
        var storage = new LockableStorage(_dir);
        var ledgerPath = storage.ResolveDataPath(PlutoClosedLoopReinforcementService.LedgerFileName);
        const string original = "[\"YouTube Shorts|old-1\",\"YouTube Shorts|old-2\"]";
        File.WriteAllText(ledgerPath, original);

        storage.Locked = true;   // 잠금 세션 — ReadAllTextAsync → ""
        using (var loop = new PlutoClosedLoopReinforcementService(null, null, storage))
        {
            var r = await loop.IngestPerformanceAsync(Breakout("new-1", HookA, LookX));
            Assert.False(r.AlreadyRegistered);
            Assert.True(loop.IsLedgerReadOnly);
            Assert.Equal(0, storage.Writes);                                                   // 디스크 장부 보존(수정 전: ["…|new-1"] 로 덮어씀)
            Assert.Equal(original, File.ReadAllText(ledgerPath));
            Assert.True((await loop.IngestPerformanceAsync(Breakout("new-1", HookA, LookX))).AlreadyRegistered);   // 세션 내 중복 차단은 유지
        }

        storage.Locked = false;  // 잠금 해제 후 — 과거 반영 이력이 살아 있어 재반영(이중 강화) 차단
        using (var loop = new PlutoClosedLoopReinforcementService(null, null, storage))
        {
            Assert.True((await loop.IngestPerformanceAsync(Breakout("old-1", HookA, LookX))).AlreadyRegistered);
            Assert.False(loop.IsLedgerReadOnly);
            Assert.False((await loop.IngestPerformanceAsync(Breakout("new-2", HookA, LookX))).AlreadyRegistered);
            Assert.Equal(1, storage.Writes);
            var saved = File.ReadAllText(ledgerPath);
            Assert.Contains("old-1", saved, StringComparison.Ordinal);
            Assert.Contains("old-2", saved, StringComparison.Ordinal);
            Assert.Contains("new-2", saved, StringComparison.Ordinal);
        }

        // 파일이 실제로 없으면(첫 실행) 읽기 전용이 아님 — 정상 기록
        var freshStorage = new LockableStorage(Path.Combine(_dir, "fresh"));
        using var first = new PlutoClosedLoopReinforcementService(null, null, freshStorage);
        await first.IngestPerformanceAsync(Breakout("first-1", HookA, LookX));
        Assert.False(first.IsLedgerReadOnly);
        Assert.Equal(1, freshStorage.Writes);
    }

    /// <summary>감쇠로 이미 해제 임계 아래로 내려간 차단 래치는, 중간 조회가 없었더라도 새 벌점 가산 전에 정산된다.</summary>
    [Fact]
    public async Task SelfAudit_MistakeNotebook_StaleSuppressionLatch_IsSettledBeforeNewPenalty()
    {
        using var unread = Notebook("unread.json");
        using var read = Notebook("read.json");
        var tok = new[] { "top-down slide" };
        await unread.RecordNegativeFeedbackAsync(tok, 2.0);
        await read.RecordNegativeFeedbackAsync(tok, 2.0);
        Assert.True(unread.IsSuppressed("top-down slide"));

        _now = T0.AddDays(42);                                                                 // 2.0 → 0.5 (< 해제 임계 0.8)
        Assert.Empty(read.GetSuppressedTokens());                                              // read만 중간 조회로 래치 해제

        // 소프트 스킵 0.4 → 유효 0.9: 해제 상태에서는 차단 임계 1.8 미만이므로 차단되면 안 된다
        await unread.RecordNegativeFeedbackAsync(tok, PlutoMistakeNotebookService.SoftSkipPenalty);
        await read.RecordNegativeFeedbackAsync(tok, PlutoMistakeNotebookService.SoftSkipPenalty);
        Assert.Equal(0.9, unread.GetEffectivePenalty("top-down slide"), 9);
        Assert.False(read.IsSuppressed("top-down slide"));
        Assert.False(unread.IsSuppressed("top-down slide"));                                   // 수정 전: 낡은 래치(true) × 0.9 ≥ 0.8 → 재차단
        Assert.Empty(unread.GetSuppressedTokens());
        Assert.Equal(string.Empty, unread.BuildNegativeConstraintPrompt());

        // 영속 상태도 해제로 기록 → 재시작 후에도 동일
        using var reloaded = Notebook("unread.json");
        await reloaded.EnsureLoadedAsync();
        Assert.Empty(reloaded.GetSuppressedTokens());

        // 실제 재범(누적 ≥ 1.8)은 여전히 차단
        await unread.RecordNegativeFeedbackAsync(tok, 1.0);
        Assert.True(unread.IsSuppressed("top-down slide"));
    }

    // ── 헬퍼 ──

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>실제 파일 기반 저장소 — <see cref="Locked"/> 동안 읽기는 실패를 뜻하는 빈 문자열(IPlutoStorageManager 계약)만 돌려준다.</summary>
    private sealed class LockableStorage : IPlutoStorageManager
    {
        private readonly string _root;

        public LockableStorage(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
        }

        public bool Locked { get; set; }
        public int Writes { get; private set; }

        public string ResolveDataPath(string filename) => Path.Combine(_root, filename);

        public Task<string> ReadAllTextAsync(string filename, CancellationToken ct = default)
        {
            var path = ResolveDataPath(filename);
            return Task.FromResult(Locked || !File.Exists(path) ? string.Empty : File.ReadAllText(path));
        }

        public Task<bool> WriteAllTextAtomicAsync(string filename, string content, CancellationToken ct = default)
        {
            Writes++;
            File.WriteAllText(ResolveDataPath(filename), content);
            return Task.FromResult(true);
        }

        public bool IsCloudSynced => false;
        public string ActiveRoot => _root;
        public string StatusLabel => "test";
        public event Action? Changed { add { } remove { } }
    }
}
