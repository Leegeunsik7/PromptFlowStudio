using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.VerticalTranscode;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 📐🎯🌊 Step 13 — 컨텍스트 잔차 알파(Domain × RenderMode EWMA 기준선) · 베이지안 사후 신뢰 구간 · Page-Hinkley 레짐 드리프트 + 탐색 서지 ·
/// 두뇌 리포트 표면화 · Media Foundation 네이티브 수명 강화(0xc0000005 근본 원인) · 구버전 메모리 파일 호환.
/// </summary>
public sealed class PlutoContextualBayesianAndDriftTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
    private const string Look = PlutoColorGradingCompiler.GoldenHourWarmName;
    private const string Baby = "BabyStudio", Cinematic = "GeneralCinematic";
    private const string Ambient = "Ambient15s", Series = "Series3", Montage = "Montage";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step13_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;
    private int _seq;

    public PlutoContextualBayesianAndDriftTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };
    private PlutoMistakeNotebookService Notebook(string name = "nb.json") => new(Path.Combine(_dir, name), () => _now);

    private PlutoVideoPerformanceTelemetry Video(string hook, double hookPct, double completionPct, double ctrPct, string? domain, string? mode, string look = Look)
        => new("v-" + (++_seq).ToString("000"), "YouTube Shorts", hook, look, "Lullaby", Array.Empty<string>(), hookPct, completionPct, ctrPct, DateTimeOffset.UtcNow)
        { Domain = domain, RenderMode = mode };

    // 포맷별 '평범한' 지표 — 15초 앰비언트는 원시 지표가 구조적으로 높고(절대 돌파선 초과) 3부작 시리즈는 낮다.
    private PlutoVideoPerformanceTelemetry AmbientTypical(string hook, string? domain = Baby, string? mode = Ambient) => Video(hook, 76, 86, 5.0, domain, mode);
    private PlutoVideoPerformanceTelemetry SeriesTypical(string hook, string? domain = Baby, string? mode = Series) => Video(hook, 44, 31, 2.3, domain, mode);
    private PlutoVideoPerformanceTelemetry SeriesStandout(string hook, string? domain = Baby, string? mode = Series) => Video(hook, 60, 50, 5.0, domain, mode);

    // ── ① 📐 컨텍스트 잔차 알파 ──

    [Fact]
    public async Task ContextualResidualAlpha_PreventsHighBaselineFormatFromDistortingPatternRanking()
    {
        const string rider = "포맷 편승 훅", standout = "시리즈 초과 훅";

        // 반사실(Step 12 동작) — 컨텍스트 미지정: 앰비언트의 높은 원시 지표가 그대로 보상되어 편승 훅이 압도
        using (var legacyLearning = Learning("legacy.json"))
        using (var legacyNotebook = Notebook("legacy-nb.json"))
        using (var legacyLoop = new PlutoClosedLoopReinforcementService(legacyLearning, legacyNotebook))
        {
            for (var i = 0; i < 3; i++)
            {
                await legacyLoop.IngestPerformanceAsync(AmbientTypical(rider, null, null));
                await legacyLoop.IngestPerformanceAsync(SeriesStandout(standout, null, null));
            }
            Assert.True(legacyLearning.GetPatternWeight("hook:" + rider) > 1.9);
            Assert.Equal(1.0, legacyLearning.GetPatternWeight("hook:" + standout), 9);   // 절대 돌파선 미달 → 보상 0
        }

        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        // 포맷 기준선 성립(각 3건 · 서로 다른 필러 훅)
        for (var i = 0; i < 3; i++)
        {
            var a = await loop.IngestPerformanceAsync(AmbientTypical("앰비언트 필러 " + i));
            Assert.False(a.ContextualAlphaApplied);                                      // 미성립 컨텍스트 → 기존 동작 그대로
            Assert.False(a.Context!.IsEstablished);
            await loop.IngestPerformanceAsync(SeriesTypical("시리즈 필러 " + i));
        }
        var baselines = learning.GetContextBaselines();
        var amb = Assert.Single(baselines, b => b.RenderMode == Ambient);
        var ser = Assert.Single(baselines, b => b.RenderMode == Series);
        Assert.True(amb.IsEstablished && ser.IsEstablished);
        Assert.Equal(Baby, amb.Domain);
        Assert.Equal(PlutoContextualBaseline.CompositeScore(76, 86, 5.0), amb.Baseline, 9);
        Assert.Equal(PlutoContextualBaseline.CompositeScore(44, 31, 2.3), ser.Baseline, 9);
        Assert.True(amb.Baseline > ser.Baseline + 0.30);                                 // 포맷 수준 편향

        for (var i = 0; i < 3; i++)
        {
            // 편승 훅 — 절대 돌파(완주 86 %)지만 자기 포맷 기준선과 동일 → 잔차 0 → 부스트 감쇄
            var ride = await loop.IngestPerformanceAsync(AmbientTypical(rider));
            Assert.True(ride.IsBreakoutSuccess);
            Assert.True(ride.ContextualAlphaApplied);
            Assert.Equal(0.0, ride.ResidualAlpha, 9);
            var (_, strength, _, _) = PlutoClosedLoopReinforcementService.Evaluate(AmbientTypical(rider));
            var legacyBoost = PlutoClosedLoopReinforcementService.BoostFor(strength);
            Assert.Equal(1.0 + (legacyBoost - 1.0) * PlutoContextualBaseline.BoostGain(0.0, true), ride.PatternWeightMultiplier, 9);
            Assert.True(ride.PatternWeightMultiplier < legacyBoost - 0.15);
            Assert.Contains("포맷 편승 보정", ride.SummaryMessage, StringComparison.Ordinal);

            // 시리즈 초과 훅 — 절대 돌파선 미달이지만 포맷 기준선을 크게 상회 → 알파 부스트
            var win = await loop.IngestPerformanceAsync(SeriesStandout(standout));
            Assert.False(win.IsBreakoutSuccess);
            Assert.True(win.ContextualAlphaApplied);
            Assert.True(win.ResidualAlpha > 0.10);
            Assert.True(win.Context!.ZScore >= PlutoContextualBaseline.OutperformZ);
            Assert.InRange(win.PatternWeightMultiplier, 1.075 - 1e-9, PlutoClosedLoopReinforcementService.MinBoost + 1e-9);
            Assert.Contains("알파 부스트", win.SummaryMessage, StringComparison.Ordinal);
        }

        var wRider = learning.GetPatternWeight("hook:" + rider);
        var wStandout = learning.GetPatternWeight("hook:" + standout);
        Assert.True(wStandout > wRider, $"standout {wStandout:0.000} vs rider {wRider:0.000}");
        var ranked = ranker.RankOptions(PlutoPatternKind.Hook, new[] { rider, standout });   // 편승 훅에 순서 사전 선호를 줘도
        Assert.Equal(standout, ranked[0].DisplayLabel);
        Assert.True(ranked[0].ResidualAlpha > ranked[1].ResidualAlpha);
        Assert.Equal(0.0, ranked[1].ResidualAlpha, 6);
    }

    [Fact]
    public async Task ContextualResidualAlpha_WaivesPenaltyForFormatRelativeOutperformer_AndKeepsLegacyBehaviorUntilEstablished()
    {
        const string hook = "저기준 포맷 선전 훅";
        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);

        // 미성립 컨텍스트의 첫 돌파 — Step 12 배수와 정확히 일치
        var first = Video("첫 돌파 훅", 82, 61, 9.4, Cinematic, Montage);
        var (_, strength, _, _) = PlutoClosedLoopReinforcementService.Evaluate(first);
        var o1 = await loop.IngestPerformanceAsync(first);
        Assert.Equal(PlutoClosedLoopReinforcementService.BoostFor(strength), o1.PatternWeightMultiplier, 12);
        Assert.False(o1.ContextualAlphaApplied);
        Assert.Equal(0.0, o1.ResidualAlpha, 12);                                           // 최초 관측 — 정보 없음

        // 저기준 포맷(시리즈) 기준선 성립: 훅 30 · 완주 20 · CTR 1.0
        for (var i = 0; i < 3; i++) await loop.IngestPerformanceAsync(Video("저기준 필러 " + i, 30, 20, 1.0, Cinematic, Series));

        // 절대 기준 미달(훅 40 % < 42 %)이지만 포맷 기준선을 크게 상회
        var o = await loop.IngestPerformanceAsync(Video(hook, 40, 45, 4.0, Cinematic, Series));
        Assert.False(o.IsBreakoutSuccess);
        Assert.False(o.RecordedMistakePenalty);
        Assert.True(o.ContextualPenaltyWaived);
        Assert.True(o.ContextualAlphaApplied);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(hook), 9);
        Assert.True(learning.GetPatternWeight("hook:" + hook) > 1.0);
        Assert.Contains("벌점 면제", o.SummaryMessage, StringComparison.Ordinal);

        // 같은 지표 · 컨텍스트 없음 → 기존대로 벌점
        using var learning2 = Learning("t2.json");
        using var notebook2 = Notebook("nb2.json");
        using var loop2 = new PlutoClosedLoopReinforcementService(learning2, notebook2);
        var plain = await loop2.IngestPerformanceAsync(Video(hook, 40, 45, 4.0, null, null));
        Assert.True(plain.RecordedMistakePenalty);
        Assert.False(plain.ContextualPenaltyWaived);

        // 순수 함수 불변식
        Assert.Equal(1.0, PlutoContextualBaseline.BoostGain(-5.0, established: false), 12);
        Assert.Equal(0.0, PlutoContextualBaseline.BoostGain(-1.0, true), 12);
        Assert.Equal(0.25, PlutoContextualBaseline.BoostGain(0.0, true), 12);
        Assert.Equal(1.0, PlutoContextualBaseline.BoostGain(3.0, true), 12);
        Assert.Equal(1.0, PlutoContextualBaseline.AlphaBoost(0.99, true), 12);
        Assert.Equal(1.15, PlutoContextualBaseline.AlphaBoost(9.0, true), 12);
        Assert.Equal("ctx:BabyStudio|Any", PlutoContextualBaseline.ContextKey("BabyStudio", " "));
        Assert.True(PlutoContextualBaseline.TrySplit("ctx:A|B", out var d, out var m) && d == "A" && m == "B");
        Assert.False(PlutoContextualBaseline.TrySplit(PlutoContextualBaseline.GlobalContextKey, out _, out _));
    }

    // ── ② 🎯 베이지안 사후 신뢰 ──

    [Fact]
    public async Task BayesianPosterior_DistinguishesOneTrialLuckySpikeFromMultiTrialProvenWinner()
    {
        // 순수 함수 — 사전 · 수축 · 구간 폭
        var prior = PlutoPosteriorConfidence.Prior;
        Assert.Equal(1.0, prior.PosteriorMean, 12);
        Assert.False(prior.IsProvenWinner || prior.IsLuckySpike);
        var spike = PlutoPosteriorConfidence.Compute(1.35, 1);
        var proven = PlutoPosteriorConfidence.Compute(1.75, 4);
        Assert.Equal(1.175, spike.PosteriorMean, 9);                                       // 1 + 0.35·1/2
        Assert.Equal(0.35 / Math.Sqrt(2), spike.PosteriorUncertainty, 9);
        Assert.True(spike.IsLuckySpike && !spike.IsProvenWinner);
        Assert.True(spike.LowerConfidenceBound < 1.0 && spike.UpperConfidenceBound > 1.35);
        Assert.True(proven.IsProvenWinner && !proven.IsLuckySpike);
        Assert.True(proven.PosteriorUncertainty < spike.PosteriorUncertainty);
        Assert.True(proven.LowerConfidenceBound > 1.0);
        Assert.Equal(proven.PosteriorMean - PlutoPosteriorConfidence.ConfidenceZ * proven.PosteriorUncertainty, proven.LowerConfidenceBound, 12);
        Assert.Contains("검증된 승자", proven.ToLabel(), StringComparison.Ordinal);
        Assert.Contains("소표본 급등", spike.ToLabel(), StringComparison.Ordinal);
        for (var n = 0; n < 12; n++)   // 표본이 늘수록 불확실성 단조 감소
            Assert.True(PlutoPosteriorConfidence.Compute(1.3, n + 1).PosteriorUncertainty < PlutoPosteriorConfidence.Compute(1.3, n).PosteriorUncertainty);

        // 통합 — 1회 급등 훅(×1.35 · 시행 1) vs 꾸준한 훅(×1.15 · 시행 4)
        const string lucky = "1회 급등 훅", steady = "꾸준한 검증 훅";
        using var learning = Learning();
        using var notebook = Notebook();
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + lucky }, 1.35, "xp:test", 0);
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + steady }, 1.15, "xp:test", 0);
        for (var i = 0; i < 3; i++) await learning.RecordPatternTrialsAsync(new[] { "hook:" + steady });
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        var ranked = ranker.RankOptions(PlutoPatternKind.Hook, new[] { lucky, steady });
        var l = ranked.Single(o => o.DisplayLabel == lucky);
        var s = ranked.Single(o => o.DisplayLabel == steady);
        Assert.Equal(lucky, ranked[0].DisplayLabel);                                       // 원시 활용 점수는 급등 훅이 1위
        Assert.True(l.Posterior.IsLuckySpike);
        Assert.Equal(1.0, l.ArmTrials, 9);
        Assert.Equal(4.0, s.ArmTrials, 9);
        Assert.True(s.PosteriorUncertainty < l.PosteriorUncertainty);
        Assert.True(s.LowerConfidenceBound > l.LowerConfidenceBound);                      // 신뢰 하한은 꾸준한 훅이 우위
        Assert.True(l.UpperConfidenceBound > s.UpperConfidenceBound);                      // 낙관 상한은 급등 훅 — 챌린저 검증 대상

        // A/B — 챔피언 = 신뢰 하한(위험 회피) → 꾸준한 훅 · 급등 훅은 챔피언에서 내려옴
        var plan = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-posterior").BuildVariantPairAsync("일상");
        Assert.True(plan.IsValidExperiment);
        Assert.Equal(steady, plan.ChampionA.HookStyle);
        Assert.True(plan.ChampionPromotedByConfidence);
        Assert.Equal(s.LowerConfidenceBound, plan.ChampionConfidence.LowerConfidenceBound, 9);
        Assert.Equal(4.0, plan.ChampionConfidence.Trials, 9);
        Assert.Contains("신뢰 하한", plan.HypothesisSummary, StringComparison.Ordinal);

        // 시간 경과 → 유효 시행 감쇠 → 구간이 다시 넓어짐(재확인 없는 승자는 재탐색 대상)
        var before = ranker.RankOptions(PlutoPatternKind.Hook, new[] { steady })[0].PosteriorUncertainty;
        _now = T0.AddDays(28);
        var after = ranker.RankOptions(PlutoPatternKind.Hook, new[] { steady })[0].PosteriorUncertainty;
        Assert.True(after > before);
    }

    // ── ③ 🌊 Page-Hinkley 레짐 드리프트 + 탐색 서지 ──

    [Fact]
    public void PageHinkley_PureDetector_IgnoresSingleOutlier_TriggersOnConsecutiveMisses_ThenDecaysSurge()
    {
        var s = default(PlutoPageHinkleyState);
        for (var i = 0; i < 6; i++) Assert.False(PlutoRegimeDriftDetector.Update(ref s, 0.70, 0.70));
        Assert.Equal(0.0, s.Surge, 12);
        Assert.Equal(0.0, s.Statistic, 12);                                                // 무미스 → m_T 는 계속 최소(M_T)를 갱신

        // 단일 이상치(최대 절단 폭) — 임계 미만
        var one = s;
        Assert.False(PlutoRegimeDriftDetector.Update(ref one, 0.70, 0.05));
        Assert.True(one.Statistic > 0 && one.Statistic < PlutoRegimeDriftDetector.Threshold);
        Assert.False(PlutoRegimeDriftDetector.Update(ref one, 0.70, 0.70));                 // 회복 → 드리프트 아님
        Assert.Equal(0, one.Drifts);

        // 연속 미스 — 2번째에서 m_T − M_T > λ
        Assert.False(PlutoRegimeDriftDetector.Update(ref s, 0.70, 0.20));
        var pressure = s.Statistic;
        Assert.True(pressure > 0);
        Assert.True(PlutoRegimeDriftDetector.Update(ref s, 0.70, 0.20));
        Assert.Equal(1.0, s.Surge, 12);
        Assert.Equal(1, s.Drifts);
        Assert.Equal(0.0, s.Count, 12);                                                    // 새 레짐 기준으로 재시작
        Assert.Equal(3.0, PlutoRegimeDriftDetector.ExplorationTemperature(s.Surge), 12);

        // 미스가 이어지는 동안 서지 유지 · 안정 관측마다 감쇠 → 소멸
        Assert.False(PlutoRegimeDriftDetector.Update(ref s, 0.50, 0.30));
        Assert.Equal(1.0, s.Surge, 12);
        var prev = s.Surge;
        var steps = 0;
        while (s.Surge > 0 && steps < 20)
        {
            Assert.False(PlutoRegimeDriftDetector.Update(ref s, 0.30, 0.30));
            Assert.True(s.Surge < prev);
            prev = s.Surge;
            steps++;
        }
        Assert.Equal(0.0, s.Surge, 12);
        Assert.InRange(steps, 3, 10);
        Assert.Equal(1, s.Drifts);
        Assert.Equal(1.0, PlutoRegimeDriftDetector.ExplorationTemperature(0.0), 12);

        // 비유한 입력 무시 · 시간 감쇠
        var copy = s;
        Assert.False(PlutoRegimeDriftDetector.Update(ref copy, double.NaN, 0.5));
        Assert.Equal(s.Count, copy.Count, 12);
        Assert.Equal(0.5, PlutoRegimeDriftDetector.EffectiveSurge(1.0, T0, T0.AddDays(7)), 9);
        Assert.Equal(0.0, PlutoRegimeDriftDetector.EffectiveSurge(1.0, T0, T0.AddDays(60)), 12);
    }

    [Fact]
    public async Task PageHinkley_ConsecutiveMissesOfDominantPattern_TriggerExplorationSurge_ThenStabilize()
    {
        const string dominant = "지배 훅";
        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var freshHook = PlutoMontageStoryGenerator.AllTemplateHookLines[0];

        // 지배 패턴 형성 — 같은 훅 · 룩으로 4연속 돌파
        for (var i = 0; i < 4; i++)
        {
            var win = await loop.IngestPerformanceAsync(Video(dominant, 82, 61, 9.4, Baby, Montage));
            Assert.True(win.IsBreakoutSuccess);
            Assert.False(win.RegimeDriftDetected);
        }
        Assert.Equal(dominant, ranker.GetCurrentRecommendations().TopHook.DisplayLabel);
        Assert.False(ranker.GetRegimeDriftStatus().IsSurgeActive);
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC, ranker.EffectiveExplorationC(), 12);
        var weightBefore = learning.GetPatternWeight("hook:" + dominant);
        var bonusBefore = ranker.RankForExploration(PlutoPatternKind.Hook).Single(o => o.DisplayLabel == freshHook).ExplorationBonus;
        Assert.True(bonusBefore > 0);

        var engine = new PlutoTrendOpportunityEngine(ranker, notebook, utcNow: () => T0);
        var calm = await engine.GenerateDailyBriefsAsync(3);
        Assert.Equal(3, calm.Count);
        Assert.Equal(1, calm.Count(b => b.IsExploration));                                 // 평시 = 마지막 1건만 탐색

        // 플랫폼 알고리즘 급변 — 같은 지배 패턴이 연속 예측 미스
        var miss1 = await loop.IngestPerformanceAsync(Video(dominant, 20, 10, 0.5, Baby, Montage));
        Assert.False(miss1.RegimeDriftDetected);                                           // 단일 미스는 이상치로 취급
        Assert.True(ranker.GetRegimeDriftStatus().PressurePct > 0);
        var miss2 = await loop.IngestPerformanceAsync(Video(dominant, 20, 10, 0.5, Baby, Montage));
        Assert.True(miss2.RegimeDriftDetected);
        Assert.Contains("레짐 변화 감지", miss2.SummaryMessage, StringComparison.Ordinal);

        var surge = ranker.GetRegimeDriftStatus();
        Assert.True(surge.IsSurgeActive);
        Assert.Equal(1.0, surge.SurgeLevel, 9);
        Assert.Equal(3.0, surge.ExplorationTemperature, 9);
        Assert.Equal(1, surge.DriftCount);
        Assert.Equal(T0, surge.LastDriftUtc);
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC * 3.0, ranker.EffectiveExplorationC(), 9);
        Assert.True(learning.GetPatternWeight("hook:" + dominant) < weightBefore);          // 표적 망각 — 낡은 승자 초과 가중치 삭감
        var bonusSurge = ranker.RankForExploration(PlutoPatternKind.Hook).Single(o => o.DisplayLabel == freshHook).ExplorationBonus;
        Assert.True(bonusSurge > bonusBefore * 2.5, $"surge bonus {bonusSurge:0.000} vs calm {bonusBefore:0.000}");

        // A/B 챌린저 · 트렌드 기획안이 새 후보를 공격적으로 검증
        var plan = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-surge").BuildVariantPairAsync("일상");
        Assert.True(plan.IsExplorationSurge);
        Assert.Equal(3.0, plan.ExplorationTemperature, 9);
        Assert.NotEqual(dominant, plan.ChallengerB.HookStyle);
        Assert.True(plan.IsExplorationChallenger);
        Assert.Contains("탐색 온도", plan.HypothesisSummary, StringComparison.Ordinal);
        var hot = await engine.GenerateDailyBriefsAsync(3);
        Assert.False(hot[0].IsExploration);                                                // 첫 1건은 활용 유지
        Assert.Equal(2, hot.Count(b => b.IsExploration));                                  // 서지 중 = 2번째부터 전부 탐색
        Assert.All(hot.Where(b => b.IsExploration), b => Assert.NotEqual(dominant, b.RecommendedHook));

        // 새 레짐에서 관측이 안정되면 서지 점감 → 소멸(재감지 없음)
        var level = surge.SurgeLevel;
        var settled = false;
        for (var i = 0; i < 16 && !settled; i++)
        {
            var o = await loop.IngestPerformanceAsync(Video(dominant, 20, 10, 0.5, Baby, Montage));
            Assert.False(o.RegimeDriftDetected);
            var st = ranker.GetRegimeDriftStatus();
            Assert.True(st.SurgeLevel <= level + 1e-12);                                   // 단조 비증가
            level = st.SurgeLevel;
            settled = !st.IsSurgeActive;
        }
        Assert.True(settled);
        Assert.Equal(1, ranker.GetRegimeDriftStatus().DriftCount);
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC, ranker.EffectiveExplorationC(), 12);
        var cooled = await engine.GenerateDailyBriefsAsync(3);
        Assert.True(cooled.Count(b => b.IsExploration) <= 1);
    }

    // ── ④ 🧠 두뇌 리포트 · Workspace ──

    [Fact]
    public async Task BrainSummaryAndWorkspace_SurfaceDriftBaselineAndPosterior_WithoutTouchingHeaderRows()
    {
        const string dominant = "리포트 지배 훅";
        using var learning = Learning();
        using var notebook = Notebook();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var svc = new PlutoAutonomousBrainSummaryService(null, ranker, notebook);

        var empty = await svc.GetSummaryAsync();
        Assert.False(empty.RegimeDrift.IsSurgeActive);
        Assert.Contains("안정", empty.RegimeDriftLabel, StringComparison.Ordinal);
        Assert.Contains("기록 없음", empty.ContextBaselineLabel, StringComparison.Ordinal);
        Assert.Empty(empty.ContextBaselines);
        Assert.False(empty.TopHookConfidence.IsProvenWinner);

        for (var i = 0; i < 4; i++) await loop.IngestPerformanceAsync(Video(dominant, 82, 61, 9.4, Baby, Montage));
        var s = await svc.GetSummaryAsync();
        Assert.Equal(dominant, s.TopHookLabel);
        Assert.True(s.TopHookConfidence.IsProvenWinner);
        Assert.True(s.TopHookConfidence.LowerConfidenceBound > 1.0);
        Assert.True(s.TopHookConfidence.UpperConfidenceBound > s.TopHookConfidence.PosteriorMean);
        Assert.Contains("검증된 승자", s.PosteriorConfidenceLabel, StringComparison.Ordinal);
        Assert.Contains("90% [", s.PosteriorConfidenceLabel, StringComparison.Ordinal);
        var ctx = Assert.Single(s.ContextBaselines);
        Assert.Equal(Baby, ctx.Domain);
        Assert.Equal(Montage, ctx.RenderMode);
        Assert.True(ctx.IsEstablished);
        Assert.Contains("BabyStudio·Montage 기준 ", s.ContextBaselineLabel, StringComparison.Ordinal);
        Assert.Contains("n=4", s.ContextBaselineLabel, StringComparison.Ordinal);
        Assert.Contains("안정", s.RegimeDriftLabel, StringComparison.Ordinal);

        await loop.IngestPerformanceAsync(Video(dominant, 20, 10, 0.5, Baby, Montage));
        await loop.IngestPerformanceAsync(Video(dominant, 20, 10, 0.5, Baby, Montage));
        var drifted = await svc.GetSummaryAsync();
        Assert.True(drifted.RegimeDrift.IsSurgeActive);
        Assert.Contains("레짐 변화 감지", drifted.RegimeDriftLabel, StringComparison.Ordinal);
        Assert.Contains("탐색 서지 ×3.00", drifted.RegimeDriftLabel, StringComparison.Ordinal);
        Assert.True(drifted.ExplorationRatePct > s.ExplorationRatePct);

        // 랭커 미연결 → 기본값으로 [부분 구제]
        var bare = await new PlutoAutonomousBrainSummaryService(null, null, null).GetSummaryAsync();
        Assert.Same(PlutoRegimeDriftStatus.Stable, bare.RegimeDrift);
        Assert.Contains("안정", bare.RegimeDriftLabel, StringComparison.Ordinal);

        // Workspace — 접이식 두뇌 리포트 카드 본문에만 표면화 · 상단 3행 헤더 불변
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(strip > 0 && tools > strip);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        Assert.True(open > 0);
        foreach (var marker in new[] { "data-brain-drift=", "data-brain-context=", "data-brain-posterior=" })
        {
            Assert.Equal(1, CountOf(ws, marker));                                          // 본문에 정확히 1회
            Assert.True(body.IndexOf(marker, StringComparison.Ordinal) > open);            // 접힘 상태에선 렌더되지 않음
        }
        Assert.Contains("@(bs.RegimeDriftLabel)", body, StringComparison.Ordinal);
        Assert.Contains("@(bs.ContextBaselineLabel)", body, StringComparison.Ordinal);
        Assert.Contains("@(bs.PosteriorConfidenceLabel)", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));                                    // 헤더 행 수 불변
        var head = ws.Substring(0, strip);
        foreach (var keyword in new[] { "레짐 드리프트", "컨텍스트 기준선", "사후 신뢰", "Page-Hinkley", "data-brain-drift", "RegimeDrift", "PosteriorConfidence" })
            Assert.DoesNotContain(keyword, head, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "[🧠 플루토 두뇌 리포트]"));
    }

    // ── ⑤ 🛡️ Media Foundation 네이티브 수명 강화 ──

    private sealed class CountingSink : IMfSampleSink
    {
        public int Samples;
        public long Bytes;
        public Action? OnFirstSample;
        public void OnSample(ReadOnlySpan<byte> data, long time100ns)
        {
            Bytes += data.Length;
            if (Interlocked.Increment(ref Samples) == 1) OnFirstSample?.Invoke();
        }
    }

    /// <summary>
    /// Step 12 의 testhost 0xc0000005(ntdll!RtlpWaitOnCriticalSection ← nvEncMFTH264x ← RTWorkQ) 재발 방지 —
    /// 병렬 초기화 · Finish 없는 즉시 해제(하드웨어 프로브 패턴) · 인코딩 중 다른 스레드의 Dispose · 콜백 재진입 Dispose 를 한꺼번에 가해도
    /// 프로세스가 살아 있고 수명 불변식(MFStartup ≤ 1 · 누수 0 · 유예 해제 소진)이 유지되어야 한다.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]   // Step 35: 기준 실행 ≥ 10 s — 빠른 루프는 --filter "Speed!=Slow"
    public void MediaFoundation_ParallelInitAbruptDisposeAndDisposeRace_KeepLifetimeInvariants()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int W = 640, H = 360;
        const long Dur = 333_333L;
        var y = new byte[W * H]; var uv = new byte[W * H / 2];
        y.AsSpan().Fill(128); uv.AsSpan().Fill(128);
        var pcm = new byte[1024 * 4];
        var liveBefore = MediaFoundationLifetime.LiveTransforms;
        var deferredBefore = MediaFoundationLifetime.DeferredReleases;
        int created = 0, hardware = 0, encodedOk = 0;

        // ⓐ 병렬 초기화 + Finish 없는 즉시 해제(비디오 HW/SW · AAC · 디코더 혼합)
        MfH264Encoder.ResetHardwareVerification();
        Parallel.For(0, 6, new ParallelOptions { MaxDegreeOfParallelism = 6 }, t =>
        {
            var sink = new CountingSink();
            for (var i = 0; i < 4; i++)
            {
                using (var enc = MfH264Encoder.TryCreate(W, H, 30, 1500, preferHardware: (t + i) % 2 == 0))
                {
                    if (enc is not null)
                    {
                        Interlocked.Increment(ref created);
                        if (enc.Kind == MfEncoderKind.Hardware) Interlocked.Increment(ref hardware);
                        for (var f = 0; f < 1 + (i % 3); f++) if (enc.Encode(y, uv, f * Dur, Dur, sink)) Interlocked.Increment(ref encodedOk);
                    }
                }   // ← Finish 없이 해제: 진행 중 작업 항목이 남은 상태
                using (var aac = MfAacEncoder.TryCreate()) aac?.Encode(pcm, 0, sink);
                using (var dec = MfH264Decoder.TryCreate(W, H, 30)) dec?.OutputSize(out _, out _, out _);
            }
        });
        MfH264Encoder.ResetHardwareVerification();   // 하드웨어 프로브(무출력 MFT 포함) 재실행 경로

        // ⓑ 같은 인스턴스 — 인코딩 스레드 vs 다른 스레드의 Dispose(사용 중 해제 금지 · 해제 후 호출은 false)
        for (var round = 0; round < 6; round++)
        {
            var enc = MfH264Encoder.TryCreate(W, H, 30, 1500, preferHardware: round % 2 == 0);
            if (enc is null) continue;
            Interlocked.Increment(ref created);
            var sink = new CountingSink();
            using var go = new ManualResetEventSlim(false);
            using var disposed = new ManualResetEventSlim(false);
            var afterDisposeAccepted = 0;
            var worker = new Thread(() =>
            {
                go.Wait();
                for (var f = 0; f < 40; f++) enc.Encode(y, uv, f * Dur, Dur, sink);
                // 결정성: 40프레임이 Dispose 보다 먼저 끝나는 빠른 실행(부하·소프트웨어 인코더)에서도 '해제 후 호출'만 검사하도록 해제 완료를 기다린다
                disposed.Wait(TimeSpan.FromSeconds(30));
                if (enc.Encode(y, uv, 41 * Dur, Dur, sink)) Interlocked.Increment(ref afterDisposeAccepted);
                enc.SetBitrate(900);
                enc.Finish(sink);
            });
            worker.Start();
            go.Set();
            Thread.Sleep(2 + round * 3);
            enc.Dispose();
            enc.Dispose();   // 멱등
            disposed.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
            Assert.False(enc.Encode(y, uv, 0, Dur, sink));
            Assert.False(enc.SetBitrate(800));
            enc.Finish(sink);
            Assert.Equal(0, afterDisposeAccepted);
        }

        // ⓒ 싱크 콜백 안에서의 재진입 Dispose — 콜백이 끝난 뒤에 해제(사용 중인 버퍼 보호)
        var reentrant = MfH264Encoder.TryCreate(W, H, 30, 1500, preferHardware: false);
        if (reentrant is not null)
        {
            Interlocked.Increment(ref created);
            var sink = new CountingSink();
            sink.OnFirstSample = () => reentrant.Dispose();
            for (var f = 0; f < 60 && sink.Samples == 0; f++) reentrant.Encode(y, uv, f * Dur, Dur, sink);
            if (sink.Samples == 0) reentrant.Finish(sink);
            if (sink.Samples > 0)
            {
                Assert.True(sink.Bytes > 0);
                Assert.False(reentrant.Encode(y, uv, 99 * Dur, Dur, sink));
            }
            reentrant.Dispose();
        }

        // 수명 불변식
        Assert.True(MediaFoundationLifetime.StartupCalls <= 1);                            // MFStartup 은 프로세스당 1회(MFShutdown 없음 → 참조 균형)
        Assert.Equal(liveBefore, MediaFoundationLifetime.LiveTransforms);                  // 구동기 누수 0
        if (created > 0) Assert.Equal(1, MediaFoundationLifetime.StartupCalls);
        var deferred = MediaFoundationLifetime.DeferredReleases - deferredBefore;
        Assert.True(deferred >= 0);
        Assert.True(MediaFoundationLifetime.PendingDeferredReleases >= 0);
        if (MediaFoundationLifetime.PendingDeferredReleases > 0)
        {
            Thread.Sleep(MediaFoundationLifetime.ReleaseGraceMs + 250);                    // 유예 기간 경과 → 최종 Release 소진
            Assert.Equal(0, MediaFoundationLifetime.Reap());
        }
        Assert.True(encodedOk >= 0 && hardware >= 0);
    }

    // ── ⑥ 💾 구버전 pluto_trend_memory.json 호환 ──

    /// <summary>Step 12 이하 엔트리 형태(선택 필드 없음) — 구버전 앱이 새 파일을 읽는 상황 재현.</summary>
    private sealed class LegacyEntry
    {
        public double Weight { get; set; }
        public int Interactions { get; set; }
        public DateTime LastUtc { get; set; }
        public int Adopted { get; set; }
        public int Exports { get; set; }
        public int ThumbsUp { get; set; }
        public int ThumbsDown { get; set; }
        public int BonusXp { get; set; }
        public double Trials { get; set; }
    }

    [Fact]
    public async Task LegacyTrendMemoryJson_LoadsMigratesAndRoundTripsCleanly()
    {
        var path = Path.Combine(_dir, PlutoLearningService.FileName);
        // Step 12 형식(Trials 있음) + Step 11 형식(Trials 없음) + 트렌드/바이럴/XP 엔트리
        const string legacy = """
        {
          "trend-newborn": {"Weight":0.25,"Interactions":4,"LastUtc":"2026-11-01T09:00:00Z","Adopted":2,"Exports":1,"ThumbsUp":1,"ThumbsDown":0,"BonusXp":0,"Trials":0},
          "viral:#육아브이로그": {"Weight":1.8,"Interactions":0,"LastUtc":"2026-11-18T09:00:00Z","Adopted":0,"Exports":0,"ThumbsUp":0,"ThumbsDown":0,"BonusXp":0,"Trials":0},
          "xp:closed-loop": {"Weight":0,"Interactions":3,"LastUtc":"2026-11-18T09:00:00Z","Adopted":0,"Exports":0,"ThumbsUp":0,"ThumbsDown":0,"BonusXp":60,"Trials":0},
          "pattern:hook:레거시 훅": {"Weight":1.52,"Interactions":3,"LastUtc":"2026-11-19T09:00:00Z","Adopted":0,"Exports":0,"ThumbsUp":0,"ThumbsDown":0,"BonusXp":0,"Trials":3.0},
          "pattern:look:Golden Hour Warm": {"Weight":1.3225,"Interactions":2,"LastUtc":"2026-11-19T09:00:00Z","Adopted":0,"Exports":0,"ThumbsUp":0,"ThumbsDown":0,"BonusXp":0},
          "pattern:pair:레거시 훅+Golden Hour Warm": {"Weight":1.3225,"Interactions":2,"LastUtc":"2026-11-19T09:00:00Z","Adopted":0,"Exports":0,"ThumbsUp":0,"ThumbsDown":0,"BonusXp":0,"Trials":2.0}
        }
        """;
        File.WriteAllText(path, legacy.Replace("Golden Hour Warm", Look, StringComparison.Ordinal));

        int xpBefore;
        using (var learning = new PlutoLearningService(path) { UtcNowProvider = () => _now })
        using (var notebook = Notebook())
        {
            await learning.EnsureLoadedAsync();
            xpBefore = learning.CurrentXP;
            Assert.Equal(2 * 1 + 1 * 3 + 1 * 1 + 60, xpBefore);
            Assert.Equal(1.52, learning.GetPatternWeight("hook:레거시 훅"), 9);
            Assert.Equal(3.0, learning.GetPatternTrials("hook:레거시 훅"), 9);
            Assert.Equal(2.0, learning.GetPatternTrials("look:" + Look), 9);        // Step 11 → 강화 횟수 폴백
            Assert.Single(learning.GetActiveViralTrends());

            // 새 통계는 전부 '없음'에서 시작(마이그레이션 = 무변환 · 기본값)
            Assert.Empty(learning.GetContextBaselines());
            Assert.Null(learning.GetGlobalBaseline());
            Assert.Same(PlutoRegimeDriftStatus.Stable, learning.GetRegimeDriftStatus());
            Assert.Equal(0.0, learning.GetExplorationSurge(), 12);
            Assert.Equal((0.0, 0.0), learning.GetPatternAlpha("hook:레거시 훅"));

            // 구버전 이력만으로도 사후 신뢰 구간 계산
            var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
            var top = ranker.RankOptions(PlutoPatternKind.Hook, new[] { "레거시 훅" })[0];
            Assert.Equal(3.0, top.Posterior.Trials, 9);
            Assert.Equal(1.0 + 0.52 * 3 / 4, top.PosteriorMean, 9);
            Assert.True(top.Posterior.IsProvenWinner);

            // Step 13 반영 → 같은 파일에 선택 필드 추가 기록
            using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
            var o = await loop.IngestPerformanceAsync(Video("레거시 훅", 82, 61, 9.4, Baby, Montage));
            Assert.True(o.IsBreakoutSuccess);
            Assert.NotNull(o.Context);
            Assert.Equal(xpBefore + o.XpDelta, learning.CurrentXP);
        }

        var json = File.ReadAllText(path);
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            // 무관한 구버전 엔트리에는 새 필드가 기록되지 않는다(기본값 생략 → 파일 팽창 없음)
            foreach (var key in new[] { "trend-newborn", "viral:#육아브이로그", "xp:closed-loop" })
            {
                var e = root.GetProperty(key);
                foreach (var field in new[] { "Mean", "Var", "Samples", "PhSum", "PhMin", "Surge", "Drifts", "DriftUtc" })
                    Assert.False(e.TryGetProperty(field, out _), key + "." + field);
            }
            Assert.Equal(0.25, root.GetProperty("trend-newborn").GetProperty("Weight").GetDouble(), 9);
            Assert.True(root.GetProperty("xp:closed-loop").GetProperty("BonusXp").GetInt32() > 60);   // 기존 XP 보존 + 이번 돌파 XP
            Assert.True(root.TryGetProperty("ctx:BabyStudio|Montage", out var ctx));
            Assert.Equal(1.0, ctx.GetProperty("Samples").GetDouble(), 9);
            Assert.True(root.TryGetProperty(PlutoContextualBaseline.GlobalContextKey, out _));
            Assert.Equal(1.0, root.GetProperty("pattern:hook:레거시 훅").GetProperty("Samples").GetDouble(), 9);
        }

        // 구버전 앱(선택 필드 모름)도 새 파일을 그대로 읽는다
        var old = JsonSerializer.Deserialize<Dictionary<string, LegacyEntry>>(json)!;
        Assert.Equal(0.25, old["trend-newborn"].Weight, 9);
        Assert.True(old["pattern:hook:레거시 훅"].Weight > 1.52);
        Assert.Equal(4.0, old["pattern:hook:레거시 훅"].Trials, 9);

        // 재적재 왕복 — 구 엔트리 보존 + 새 통계 복원
        using (var reloaded = new PlutoLearningService(path) { UtcNowProvider = () => _now })
        {
            await reloaded.EnsureLoadedAsync();
            Assert.Equal(0.25, reloaded.GetWeight("trend-newborn"), 9);
            Assert.Equal(4.0, reloaded.GetPatternTrials("hook:레거시 훅"), 9);
            var b = Assert.Single(reloaded.GetContextBaselines());
            Assert.Equal("ctx:BabyStudio|Montage", b.ContextKey);
            Assert.Equal(PlutoContextualBaseline.CompositeScore(82, 61, 9.4), b.Baseline, 9);
            Assert.NotNull(reloaded.GetGlobalBaseline());
            Assert.Equal(1.0, reloaded.GetPatternAlpha("hook:레거시 훅").Samples, 9);
        }

        // 손상된 선택 필드(음수 표본 · 범위 밖 서지)는 적재 시 정규화
        var hostile = Path.Combine(_dir, "hostile.json");
        File.WriteAllText(hostile, """{"ctx:A|B":{"Weight":0,"Mean":0.4,"Var":-3,"Samples":-5},"drift:regime":{"Weight":0,"LastUtc":"2026-11-19T09:00:00Z","Surge":7,"Drifts":-2,"Samples":2}}""");
        using (var h = new PlutoLearningService(hostile) { UtcNowProvider = () => _now })
        {
            await h.EnsureLoadedAsync();
            Assert.Empty(h.GetContextBaselines());
            var st = h.GetRegimeDriftStatus();
            Assert.Equal(1.0, st.SurgeLevel, 9);
            Assert.Equal(0, st.DriftCount);
        }
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
}
