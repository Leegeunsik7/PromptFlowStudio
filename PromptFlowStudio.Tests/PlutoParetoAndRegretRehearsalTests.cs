using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// ⚖️🔁☁️⏱️ Step 15 — Drive 핵심 허브 8종(메타 보정 상태 포함) · 능동 후회 리허설(회복 사전 → 챌린저 B) · 다목적 파레토 프런티어 랭킹 ·
/// 스위트 전역 부하 인지 프레임 예산 · 자율 추가(회로 차단 수동 탐침/해제 + UI 표면화 · 보정 상태 마지막 정상본 복구 + 읽기 전용 세션 자가 치유).
/// </summary>
[Collection("TimingSensitivePerformance")]   // Step 35: CiFrameBudget 실측 탐침(전 코어 스핀) → 병렬 구간 제외
public sealed class PlutoParetoAndRegretRehearsalTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private const string Look = PlutoColorGradingCompiler.GoldenHourWarmName;
    private const string Baby = "BabyStudio", Montage = "Montage";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step15_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;
    private int _seq;

    public PlutoParetoAndRegretRehearsalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };
    private PlutoMistakeNotebookService Notebook(string name = "nb.json") => new(Path.Combine(_dir, name), () => _now);
    private PlutoMetaCalibrationService Calibration(string name = "calibration.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };

    private PlutoVideoPerformanceTelemetry Video(string hook, double hookPct, double completionPct, double ctrPct, string look = Look)
        => new("v15-" + (++_seq).ToString("000"), "YouTube Shorts", hook, look, "Lullaby", Array.Empty<string>(), hookPct, completionPct, ctrPct, DateTimeOffset.UtcNow)
        { Domain = Baby, RenderMode = Montage };

    private static PlutoCalibrationObservation Missed(string label, double realized = 0.30, double alternative = 0.60, bool measured = false)
        => new(0.50, realized, alternative) { BestAlternativeLabel = label, Source = "test", AlternativeMeasured = measured };

    // ── ① ☁️ Drive 핵심 허브 8종 — 검증 · 미러 · 새 PC 자동 복원 · OFF 격리 ──

    [Fact]
    public async Task DriveHub_EightCoreFiles_IncludeCalibrationState_RestoreWithoutOverwritingLocal_AndHonorDriveOffIsolation()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);                                  // Step 17 — 시리즈 아크 편입
        Assert.Equal(PlutoDriveHealthService.CorePlutoFileCount, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.Equal(9, PlutoDriveHealthService.PlutoFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("pluto_calibration_state.json", PlutoMetaCalibrationService.FileName);
        foreach (var f in new[]
                 {
                     PlutoMetaCalibrationService.FileName, PlutoRenderCatalogService.FileName, PlutoExperimentLedgerService.FileName, PlutoMistakeNotebookService.FileName,
                     PlutoLearningService.FileName, PlutoAutonomousTrendHarvester.PatternsFileName, PlutoSocialRadarEngine.FileName, PlutoIdentityVault.FileName,
                 })
            Assert.Contains(f, PlutoDriveHealthService.PlutoFiles);

        var drive = Path.Combine(_dir, "vault", "Pluto");
        Directory.CreateDirectory(drive);
        foreach (var f in PlutoDriveHealthService.PlutoFiles) File.WriteAllText(Path.Combine(drive, f), "{\"origin\":\"drive\"}");

        // 🔁 새 PC(로컬 비어 있음) → 8종 전부 복원 · 멱등
        var freshLocal = Path.Combine(_dir, "local_fresh");
        var fresh = new PlutoDriveStorageManager(drive, freshLocal);
        Assert.True(fresh.IsCloudSynced);
        Assert.Equal(9, fresh.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"origin\":\"drive\"}", File.ReadAllText(Path.Combine(freshLocal, PlutoMetaCalibrationService.FileName)));
        Assert.Equal(0, fresh.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));

        // 🛡️ 로컬에 더 새로운 보정 상태가 있으면 절대 덮어쓰지 않음(나머지 7종만 복원)
        var keptLocal = Path.Combine(_dir, "local_kept");
        Directory.CreateDirectory(keptLocal);
        File.WriteAllText(Path.Combine(keptLocal, PlutoMetaCalibrationService.FileName), "{\"origin\":\"local-newer\"}");
        var kept = new PlutoDriveStorageManager(drive, keptLocal);
        Assert.Equal(8, kept.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"origin\":\"local-newer\"}", File.ReadAllText(Path.Combine(keptLocal, PlutoMetaCalibrationService.FileName)));

        // 🔄 [지금 전체 드라이브 백업 검증] — Drive 에서 사라진 보정 상태를 로컬 사본으로 재시드 + 8종 전부 Healthy
        File.Delete(Path.Combine(drive, PlutoMetaCalibrationService.FileName));
        var verified = await new PlutoDriveHealthService(null, null, kept).ForceSyncAndVerifyAsync();
        var plutoItems = verified.Items.Where(i => i.Category == PlutoDriveHealthService.CategoryPluto).ToList();
        Assert.Equal(9, plutoItems.Count);
        Assert.All(plutoItems, i => Assert.Equal(PlutoDriveFileStatus.Healthy, i.Status));
        Assert.Equal("{\"origin\":\"local-newer\"}", File.ReadAllText(Path.Combine(drive, PlutoMetaCalibrationService.FileName)));

        // 🔒 PROMPTFLOW_PLUTO_DRIVE=off(테스트 하네스 기본) → 실 Drive 경로 비활성 · 복원 0 · 검증은 8종을 '비활성'으로만 보고 · 로컬에 아무것도 만들지 않음
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        Assert.Null(PlutoDriveStorageManager.DefaultPrimaryRoot());
        var offLocal = Path.Combine(_dir, "local_off");
        var off = new PlutoDriveStorageManager(PlutoDriveStorageManager.DefaultPrimaryRoot(), offLocal);
        Assert.False(off.IsCloudSynced);
        Assert.Equal(0, off.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        var offReport = await new PlutoDriveHealthService(null, null, off).ForceSyncAndVerifyAsync();
        var offPluto = offReport.Items.Where(i => i.Category == PlutoDriveHealthService.CategoryPluto).ToList();
        Assert.Equal(9, offPluto.Count);
        Assert.All(offPluto, i => Assert.Equal(PlutoDriveFileStatus.DriveUnavailable, i.Status));
        Assert.Contains(offPluto, i => i.FileName == PlutoMetaCalibrationService.FileName);
        Assert.False(Directory.Exists(offLocal) && Directory.EnumerateFiles(offLocal).Any());

        // 기동 복원 결선 — MainForm 이 8종 목록을 그대로 사용
        var mainForm = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "MainForm.cs"));
        Assert.Contains("IPlutoStartupDriveSyncService>()?.RunOnceAsync()", mainForm, StringComparison.Ordinal);   // ☁️ Step 38 — 단일 비행 서비스 이관
        PlutoStartupSyncWiring.AssertHostDelegatesToSingleFlightStartupSync(RepoRoot());
    }

    // ── ② 🔁 능동 후회 리허설 — 회복 사전 → 챌린저 B · 시간 감쇠 · 오답 노트 차단 · 실측 후 소거 ──

    [Fact]
    public async Task RegretRehearsal_ElevatesHighRegretPassedOverHookIntoChallengerB_DecaysOverTime_AndClearsOnceTested()
    {
        // 순수 함수 — 임계 미만 0 · 선형 · 상한
        Assert.Equal(0.0, PlutoMetaCalibrationService.ComputeRecoveryBoost(PlutoMetaCalibrationService.RecoveryThreshold - 0.01), 9);
        Assert.Equal(0.25, PlutoMetaCalibrationService.ComputeRecoveryBoost(0.25), 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxRecoveryBoost, PlutoMetaCalibrationService.ComputeRecoveryBoost(5.0), 9);
        Assert.Equal(0.0, PlutoMetaCalibrationService.ComputeRecoveryBoost(double.NaN), 9);

        var learning = Learning();
        var notebook = Notebook();
        using var calibration = Calibration();

        // 기준 — 리허설 없는 A/B(메타 보정 미연결)
        var plain = new PlutoAdaptivePatternRanker(learning, notebook);
        var baseline = await new PlutoVariantABGenerator(plain, null, notebook, () => "exp-base").BuildVariantPairAsync("일상");
        Assert.False(baseline.IsRegretRehearsal);
        Assert.Null(baseline.RegretRehearsalLabel);
        var missed = PlutoMontageStoryGenerator.AllTemplateHookLines.Last(h => h != baseline.ChampionA.HookStyle && h != baseline.ChallengerB.HookStyle);
        var label = "hook:" + missed;

        // 같은 대안을 3회 지나침(후회 0.30 × 3 = 0.90) → 회복 사전 상한
        for (var i = 0; i < 3; i++) await calibration.RecordOutcomeAsync(Missed(label));
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = calibration };
        var boost = Assert.Single(ranker.GetRegretRecoveryBoosts());
        Assert.Equal(label, boost.Label);
        Assert.Equal(0.90, boost.OutstandingRegret, 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxRecoveryBoost, boost.Boost, 9);
        Assert.Equal(3, boost.Count);

        // 챌린저 B = 지나친 고후회 훅 · 챔피언 불변 · 유효 실험 · 가설에 명시
        var plan = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-rehearsal").BuildVariantPairAsync("일상");
        Assert.Equal(baseline.ChampionA.HookStyle, plan.ChampionA.HookStyle);
        Assert.Equal(missed, plan.ChallengerB.HookStyle);
        Assert.NotEqual(baseline.ChallengerB.HookStyle, plan.ChallengerB.HookStyle);
        Assert.True(plan.IsValidExperiment && plan.IsRegretRehearsal);
        Assert.Equal(label, plan.RegretRehearsalLabel);
        Assert.Equal(PlutoMetaCalibrationService.MaxRecoveryBoost, plan.RegretRehearsalBoost, 9);
        Assert.Contains("🔁 후회 리허설: " + label, plan.HypothesisSummary, StringComparison.Ordinal);
        Assert.Equal(missed, plan.ChallengerB.Subtitles[0].Text);                                      // Act 1 = 리허설 훅

        // ⏳ 시간 감쇠 — 28일(2 반감기) → 잔액 ¼ · 84일 → 임계 미만으로 자연 소멸
        _now = T0.AddDays(28);
        var decayed = Assert.Single(ranker.GetRegretRecoveryBoosts());
        Assert.Equal(0.225, decayed.OutstandingRegret, 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxRecoveryBoost * 0.225 / PlutoMetaCalibrationService.RecoveryFullRegret, decayed.Boost, 9);
        _now = T0.AddDays(84);
        Assert.Empty(ranker.GetRegretRecoveryBoosts());
        _now = T0;

        // 🛡️ 오답 노트가 차단한 대안은 리허설하지 않음
        var blocker = Notebook("nb_block.json");
        await blocker.RecordNegativeFeedbackAsync(new[] { missed }, PlutoMistakeNotebookService.BlockThreshold + 0.2, "test");
        Assert.Contains(missed, blocker.GetSuppressedTokens());
        var guarded = new PlutoAdaptivePatternRanker(learning, blocker) { MetaCalibration = calibration };
        Assert.Empty(guarded.GetRegretRecoveryBoosts());
        var guardedPlan = await new PlutoVariantABGenerator(guarded, null, blocker, () => "exp-guarded").BuildVariantPairAsync("일상");
        Assert.NotEqual(missed, guardedPlan.ChallengerB.HookStyle);
        Assert.False(guardedPlan.IsRegretRehearsal);

        // ✅ 실측 검증 — 그 훅으로 만든 영상의 성과가 폐루프에 들어오면(예측 기준선이 아직 없어도) 회복 사전 소거
        using var closed = new PlutoClosedLoopReinforcementService(learning, notebook) { MetaCalibration = calibration };
        await closed.IngestPerformanceAsync(Video(missed, 50, 40, 4));
        Assert.Empty(ranker.GetRegretRecoveryBoosts());
        var after = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-after").BuildVariantPairAsync("일상");
        Assert.False(after.IsRegretRehearsal);
        Assert.DoesNotContain("🔁 후회 리허설", after.HypothesisSummary, StringComparison.Ordinal);
        // 누적 귀속(두뇌 리포트 '가장 아쉬운 대안')은 보존 — 소거되는 것은 리허설 잔액뿐
        var top = calibration.GetStatus().TopRegretAlternative;
        Assert.NotNull(top);
        Assert.Equal((label, 3), (top!.Label, top.Count));
        Assert.Equal(0.90, top.CumulativeRegret, 9);

        // 소거는 영속된다(재시작 후에도 리허설이 되살아나지 않음)
        using var reloaded = Calibration();
        await reloaded.EnsureLoadedAsync();
        Assert.Empty(reloaded.GetRegretRecoveryBoosts());
    }

    [Fact]
    public async Task RegretRecovery_MeasuredAlternativesEarnNoCredit_LegacyStateMigrates_AndPairLabelsRehearseHookAndLookTogether()
    {
        var learning = Learning();
        var notebook = Notebook();

        // A/B 실측 반사실(상대 변형은 이미 검증됨) → 후회 귀속은 집계 · 회복 사전은 적립 안 함
        using (var measured = Calibration("measured.json"))
        {
            for (var i = 0; i < 3; i++) await measured.RecordOutcomeAsync(Missed("hook:실측된 승자", measured: true));
            Assert.Empty(measured.GetRegretRecoveryBoosts());
            Assert.Equal(0.90, measured.GetStatus().TopRegretAlternative!.CumulativeRegret, 9);
            // 이후 예측 반사실 1건(0.30)만 잔액으로 적립 — 실측분이 새어 들어오지 않음
            await measured.RecordOutcomeAsync(Missed("hook:실측된 승자"));
            Assert.Equal(0.30, Assert.Single(measured.GetRegretRecoveryBoosts()).OutstandingRegret, 9);
        }

        // Step 14 상태 파일(Recovery · Utc 없음) → 누적 후회를 잔액으로 승계(감쇠 기준 시각 미상 → 감쇠 없음)
        var legacyPath = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(legacyPath, "{\"Version\":1,\"CumulativeRegret\":0.6,\"TotalRecords\":2,\"Window\":[],\"Attributions\":{\"look:" + PlutoColorGradingCompiler.VintageFilm16mmName + "\":{\"Regret\":0.6,\"Count\":2}}}");
        using var legacy = new PlutoMetaCalibrationService(legacyPath) { UtcNowProvider = () => _now };
        await legacy.EnsureLoadedAsync();
        var migrated = Assert.Single(legacy.GetRegretRecoveryBoosts());
        Assert.Equal("look:" + PlutoColorGradingCompiler.VintageFilm16mmName, migrated.Label);
        Assert.Equal(0.60, migrated.OutstandingRegret, 9);

        // look: 라벨 → 챌린저 룩만 교체(챔피언 룩이면 건너뜀)
        var lookRanker = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = legacy };
        var lookPlan = await new PlutoVariantABGenerator(lookRanker, null, notebook, () => "exp-look").BuildVariantPairAsync("일상");
        if (lookPlan.ChampionA.ColorLookId != PlutoColorGradingCompiler.VintageFilm16mmName)
        {
            Assert.Equal(PlutoColorGradingCompiler.VintageFilm16mmName, lookPlan.ChallengerB.ColorLookId);
            Assert.Equal(migrated.Label, lookPlan.RegretRehearsalLabel);
        }
        else Assert.False(lookPlan.IsRegretRehearsal);

        // pair: 라벨 → 훅 + 룩 동시 리허설 · 차단 판정은 조합 구성 요소 단위
        using var pairs = Calibration("pairs.json");
        var baseline = await new PlutoVariantABGenerator(new PlutoAdaptivePatternRanker(learning, notebook), null, notebook, () => "exp-b").BuildVariantPairAsync("일상");
        var hook = PlutoMontageStoryGenerator.AllTemplateHookLines.Last(h => h != baseline.ChampionA.HookStyle && h != baseline.ChallengerB.HookStyle);
        var look = new[] { PlutoColorGradingCompiler.NaturalPastelName, PlutoColorGradingCompiler.GoldenHourWarmName, PlutoColorGradingCompiler.VintageFilm16mmName }
            .Last(l => l != baseline.ChampionA.ColorLookId);
        var pairKey = PlutoPairAttribution.PairKey(hook, look)!;
        Assert.True(PlutoPairAttribution.TrySplitPair(pairKey, out var pairHook, out var pairLook));
        for (var i = 0; i < 2; i++) await pairs.RecordOutcomeAsync(Missed(pairKey));
        var pairRanker = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = pairs };
        var pairPlan = await new PlutoVariantABGenerator(pairRanker, null, notebook, () => "exp-pair").BuildVariantPairAsync("일상");
        Assert.Equal((pairHook, pairLook), (pairPlan.ChallengerB.HookStyle, pairPlan.ChallengerB.ColorLookId));
        Assert.Equal(pairKey, pairPlan.RegretRehearsalLabel);
        Assert.True(PlutoMetaCalibrationService.IsBlockedLabel(pairKey, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pairLook }));
        Assert.True(PlutoMetaCalibrationService.IsBlockedLabel("hook:" + hook, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hook }));
        Assert.False(PlutoMetaCalibrationService.IsBlockedLabel("hook:" + hook, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "다른 훅" }));
        // 그 조합을 실제로 검증한 영상 → 조합 리허설 소거
        using var closed = new PlutoClosedLoopReinforcementService(learning, notebook) { MetaCalibration = pairs };
        Assert.Contains(pairKey, PlutoClosedLoopReinforcementService.RehearsalLabels(Video(hook, 50, 40, 4, look)));
        await closed.IngestPerformanceAsync(Video(hook, 50, 40, 4, look));
        Assert.Empty(pairs.GetRegretRecoveryBoosts());
    }

    // ── ③ ⚖️ 다목적 파레토 프런티어 — 균형 복리형 > 클릭베이트 급등 ──

    [Fact]
    public async Task ParetoFrontier_RanksBalancedCompounderAboveClickbaitSpike_AndLeavesUnevidencedCandidatesUntouched()
    {
        // 순수 함수 — 정규화 · 지배 · 티어 · 배수
        var clickbait = PlutoObjectiveVector.FromPercent(80, 10, 3);
        var balanced = PlutoObjectiveVector.FromPercent(55, 55, 5);
        var dominated = PlutoObjectiveVector.FromPercent(50, 50, 4);
        Assert.Equal((0.80, 0.10, 0.30), (clickbait.Hook, clickbait.Completion, Math.Round(clickbait.Ctr, 9)));
        Assert.Equal(1.0, PlutoObjectiveVector.FromPercent(100, 100, 25).Ctr, 9);                      // CTR × 10 상한
        Assert.True(PlutoParetoFrontier.Dominates(balanced, dominated));
        Assert.False(PlutoParetoFrontier.Dominates(balanced, clickbait));
        Assert.False(PlutoParetoFrontier.Dominates(clickbait, balanced));
        Assert.False(PlutoParetoFrontier.Dominates(balanced, balanced));
        Assert.True(PlutoParetoFrontier.IsClickbaitSpike(clickbait));
        Assert.False(PlutoParetoFrontier.IsClickbaitSpike(PlutoObjectiveVector.FromPercent(64.9, 10, 3)));   // 훅 65% 미만
        Assert.False(PlutoParetoFrontier.IsClickbaitSpike(PlutoObjectiveVector.FromPercent(80, 20, 3)));     // 완주 20% 이상
        var tiers = PlutoParetoFrontier.ComputeTiers(new[] { clickbait, balanced, dominated, default }, new[] { true, true, true, false });
        Assert.Equal(new[] { 0, 0, 1, -1 }, tiers);
        Assert.Equal(1.075, PlutoParetoFrontier.Multiplier(0, balanced), 9);
        Assert.Equal((1.0 + 0.15 * 0.10) * PlutoParetoFrontier.ClickbaitFactor, PlutoParetoFrontier.Multiplier(0, clickbait), 9);
        Assert.Equal(0.95 * (1.0 + 0.15 * 0.40), PlutoParetoFrontier.Multiplier(1, dominated), 9);
        Assert.Equal(1.0, PlutoParetoFrontier.Multiplier(-1, balanced), 9);
        Assert.True(PlutoParetoFrontier.Multiplier(0, balanced) > PlutoParetoFrontier.Multiplier(0, clickbait));

        const string Click = "충격! 3초만 보세요", Bal = "오늘도 한 뼘 자랐어요", Plain = "근거 없는 새 훅";
        var learning = Learning();
        var notebook = Notebook();
        for (var i = 0; i < 2; i++)
        {
            await learning.RecordContextualObservationAsync(Baby, Montage, PlutoContextualBaseline.CompositeScore(80, 10, 3), new[] { "hook:" + Click }, default, clickbait);
            await learning.RecordContextualObservationAsync(Baby, Montage, PlutoContextualBaseline.CompositeScore(55, 55, 5), new[] { "hook:" + Bal }, default, balanced);
            // 3초 유지율 돌파로 클릭베이트가 거듭 강화된 상황(×1.35 × 1.35 ≈ ×1.82)
            await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + Click }, 1.35, "xp:step15-test", 0);
        }
        Assert.Equal(2.0, learning.GetPatternObjectives("hook:" + Click).Samples, 9);
        Assert.Equal(0.80, learning.GetPatternObjectives("hook:" + Click).Objectives.Hook, 9);
        Assert.Equal(0.0, learning.GetPatternObjectives("hook:" + Plain).Samples, 9);

        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        var ranked = ranker.RankOptions(PlutoPatternKind.Hook, new[] { Click, Bal, Plain });        // 클릭베이트가 사전 선호 1순위 + 학습 ×1.82
        var click = ranked.Single(o => o.DisplayLabel == Click);
        var bal = ranked.Single(o => o.DisplayLabel == Bal);
        var plain = ranked.Single(o => o.DisplayLabel == Plain);
        Assert.Equal(Bal, ranked[0].DisplayLabel);                                                    // 균형 복리형이 1위
        Assert.Equal(Click, ranked[^1].DisplayLabel);                                                 // 클릭베이트는 근거 없는 후보보다도 아래
        Assert.True(click.LearnedWeightMultiplier > 1.8 && click.IsReinforced);                       // 학습 배수 자체는 그대로 노출
        Assert.True(click.IsClickbaitSpike && !click.IsParetoFrontier && click.Pareto.Tier == 0);
        Assert.True(bal.IsParetoFrontier && !bal.IsClickbaitSpike);
        Assert.Equal(0.55, bal.Objectives.Completion, 9);
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(click.BaseScore, 1.0 * PlutoParetoFrontier.Multiplier(0, clickbait), 0.0, click.Key), click.FinalScore, 9);   // 부스트 무효 + 강등
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(bal.BaseScore, bal.LearnedWeightMultiplier * 1.075, 0.0, bal.Key), bal.FinalScore, 9);
        // 다목적 근거 없는 후보 → 점수 · 평가 완전 불변
        Assert.Same(PlutoParetoAssessment.None, plain.Pareto);
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(plain.BaseScore, plain.LearnedWeightMultiplier, 0.0, plain.Key), plain.FinalScore, 12);

        // 근거가 전혀 없는 학습 메모리 → 기존 순위 공식 그대로(회귀 가드)
        var untouched = new PlutoAdaptivePatternRanker(Learning("empty.json"), notebook).RankOptions(PlutoPatternKind.Hook, new[] { Click, Bal });
        Assert.All(untouched, o => Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(o.BaseScore, o.LearnedWeightMultiplier, o.MistakePenaltyFactor, o.Key), o.FinalScore, 12));
        Assert.Equal(Click, untouched[0].DisplayLabel);

        // 프런티어 선두 · 두뇌 리포트 본문
        var leader = ranker.GetParetoFrontierLeader();
        Assert.NotNull(leader);
        Assert.Equal((Bal, 1, 2, 1), (leader!.Label, leader.FrontierSize, leader.EvidencedCount, leader.ClickbaitCount));
        Assert.Equal("'" + Bal + "' · 3초 55 · 완주 55 · CTR 5% · 최약 축 0.50 · 프런티어 1/2 · 🎣 클릭베이트 강등 1개", leader.ToLabel());
        var summary = await new PlutoAutonomousBrainSummaryService(null, ranker, notebook).GetSummaryAsync();
        Assert.Equal(leader.ToLabel(), summary.ParetoFrontierLabel);
        Assert.StartsWith("대상 없음", summary.RegretRehearsalLabel, StringComparison.Ordinal);
        Assert.Null(summary.RegretRehearsalTarget);
        Assert.StartsWith("근거 없음", PlutoAutonomousBrainSummaryService.BuildParetoLabel(null), StringComparison.Ordinal);
        Assert.EndsWith("→ 다음 A/B 챌린저 B 배정", PlutoAutonomousBrainSummaryService.BuildRehearsalLabel(new PlutoRegretRecoveryBoost("hook:x", 0.5, 0.9, 3)), StringComparison.Ordinal);

        // 폐루프 결선 — 단일 영상 성과 · A/B 변형 모두 다목적 EWMA 로 귀속
        using var closed = new PlutoClosedLoopReinforcementService(learning, notebook);
        await closed.IngestPerformanceAsync(Video("폐루프 훅", 70, 30, 6));
        var fed = learning.GetPatternObjectives("hook:폐루프 훅");
        Assert.Equal((1.0, 0.70, 0.30, 0.60), (fed.Samples, fed.Objectives.Hook, fed.Objectives.Completion, Math.Round(fed.Objectives.Ctr, 9)));
        Assert.Equal(1.0, learning.GetPatternObjectives("look:" + Look).Samples, 9);

        // Workspace — 접이식 두뇌 리포트 본문에만 표면화 · 상단 3행 헤더 불변
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(strip > 0 && tools > strip);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        foreach (var marker in new[] { "data-brain-pareto=", "data-brain-rehearsal=" })
        {
            Assert.Equal(1, CountOf(ws, marker));
            Assert.True(body.IndexOf(marker, StringComparison.Ordinal) > open);
        }
        Assert.Contains("@(bs.ParetoFrontierLabel)", body, StringComparison.Ordinal);
        Assert.Contains("@(bs.RegretRehearsalLabel)", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        var head = ws.Substring(0, strip);
        foreach (var keyword in new[] { "파레토", "후회 리허설", "data-brain-pareto", "data-brain-rehearsal", "ParetoFrontier", "RegretRehearsal", "data-circuit-strip", "회로 차단" })
            Assert.DoesNotContain(keyword, head, StringComparison.Ordinal);
    }

    // ── ④ ⏱️ 부하 인지 프레임 예산 — 경합 시 비례 완화 · 유휴 시 기준 그대로 ──

    [Fact]
    public void CiFrameBudget_ScalesLimitsProportionallyUnderContention_AndKeepsExactBaseLimitsWhenIdle()
    {
        // 순수 함수 — 유휴 폭 이하 → 정확히 1.0 · 경합 → 비례 · 상한 ×3 · 비유한 → 1.0
        Assert.Equal(1.0, CiFrameBudget.NormalizeFactor(1.0));
        Assert.Equal(1.0, CiFrameBudget.NormalizeFactor(CiFrameBudget.IdleDeadBand));
        Assert.Equal(2.0, CiFrameBudget.NormalizeFactor(2.0));
        Assert.Equal(CiFrameBudget.MaxContentionFactor, CiFrameBudget.NormalizeFactor(9.0));
        Assert.Equal(1.0, CiFrameBudget.NormalizeFactor(double.NaN));
        Assert.Equal(CiFrameBudget.BaseMs, CiFrameBudget.Scale(CiFrameBudget.BaseMs, 1.0));           // 비트 단위로 동일
        Assert.Equal(CiFrameBudget.BaseMs, CiFrameBudget.Scale(CiFrameBudget.BaseMs, 1.10));
        Assert.Equal(CiFrameBudget.BaseMs * 2.0, CiFrameBudget.Scale(CiFrameBudget.BaseMs, 2.0), 12);
        Assert.Equal(1_000 * 1.5, CiFrameBudget.Scale(1_000, 1.5), 9);
        Assert.Equal(CiFrameBudget.BaseMs * 3.0, CiFrameBudget.Scale(CiFrameBudget.BaseMs, 50.0), 12);

        // 유휴(계수 1.0) — 기준 경계 그대로 · 기준 미만 값은 계측조차 하지 않음
        var idleProbes = 0;
        var idle = CiFrameBudget.Custom(CiFrameBudget.BaseMs, () => { idleProbes++; return 1.0; });
        Assert.True(33.2 < idle);
        Assert.False(33.2 >= idle);
        Assert.Equal(0, idleProbes);                                                                  // 정상 경로 비용 0
        Assert.False(33.4 < idle);
        Assert.True(33.4 >= idle);
        Assert.True(33.4 > idle);
        Assert.False(33.3 > idle);                                                                    // 경계값은 초과 아님
        Assert.True(idleProbes > 0);
        Assert.Equal(CiFrameBudget.BaseMs, idle.Current);
        Assert.Equal(CiFrameBudget.BaseMs, idle.Base);

        // 외부 CPU 경합 ×2 — 상한 66.6 ms 로 비례 완화(그 이상의 회귀는 여전히 실패)
        var busy = CiFrameBudget.Custom(CiFrameBudget.BaseMs, () => 2.0);
        Assert.True(50.0 < busy);
        Assert.True(66.5 < busy);
        Assert.False(66.7 < busy);
        Assert.True(66.7 >= busy);
        Assert.False(50.0 > busy);
        Assert.True(busy > 50.0 && !(busy > 70.0));                                                   // 예산이 좌변인 비교
        Assert.Equal(66.6, busy.Current, 9);
        // 하위 예산 · 배수 연산 — 같은 부하 계수를 승계
        Assert.True(1_900.0 < CiFrameBudget.Custom(1_000, () => 2.0));
        Assert.False(1_900.0 < CiFrameBudget.Custom(1_000, () => 1.0));
        var p95 = 2 * busy;
        Assert.Equal(66.6, p95.Base, 9);
        Assert.True(130.0 < p95 && !(134.0 < p95));
        Assert.True(float.MaxValue >= CiFrameBudget.Custom(CiFrameBudget.BaseMsF, () => 3.0));        // 재시도 루프 초기값(MaxValue)은 항상 '예산 초과'

        // 실계측 — 범위 1~3 · 캐시(짧은 간격 재호출은 같은 값)
        var live = CiFrameBudget.CurrentFactor();
        Assert.InRange(live, 1.0, CiFrameBudget.MaxContentionFactor);
        Assert.Equal(live, CiFrameBudget.CurrentFactor());
        Assert.True(1.0 < CiFrameBudget.Ms && 1.0f < CiFrameBudget.MsF);
        Assert.Equal(CiFrameBudget.BaseMs, CiFrameBudget.Ms.Base);
        Assert.Equal((double)CiFrameBudget.BaseMsF, CiFrameBudget.MsF.Base);

        // 스위트 전역 — 남은 프레임 예산 단언은 전부 부하 인지 예산을 거친다(고정 const 게이트 0곳)
        var titan = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio.Tests", "TitanReleaseTests.cs"));
        Assert.Equal(0, CountOf(titan, "const double Gate = CiFrameBudget.Ms"));
        Assert.Equal(0, CountOf(titan, "const double FrameGateMs = CiFrameBudget.Ms"));
        Assert.Equal(0, CountOf(titan, "const double RemuxGate = 10, TickGate"));
        Assert.Equal(0, CountOf(titan, "CiFrameBudget.Ms * "));                                       // 수동 배수 곱(이중 완화) 금지 — 기준값은 BaseMs
        Assert.True(CountOf(titan, "CiFrameBudget.Ms") + CountOf(titan, "CiFrameBudget.MsF") >= 40);
        Assert.True(CountOf(titan, "CiFrameBudget.Scaled(") >= 8);                                    // µs 레이어 · 포스트 하위 예산
        var helper = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio.Tests", "CiFrameBudget.cs"));
        Assert.Contains("public static FrameBudget Ms =>", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("public const double Ms", helper, StringComparison.Ordinal);

        // 조건부 탐침 — 전 코어 40 ms 스핀(ContentionFactor)은 헬퍼 내부에서만, 그것도 기준 상한이 실패했을 때만 호출된다.
        // 테스트 본문이 직접 부르면 통과하는 정상 경로에서도 병렬 테스트를 교란한다.
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "PromptFlowStudio.Tests"), "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name is "CiFrameBudget.cs" or "PlutoParetoAndRegretRehearsalTests.cs") continue;
            Assert.False(File.ReadAllText(file).Contains("CiFrameBudget.ContentionFactor(", StringComparison.Ordinal), name + " 가 ContentionFactor() 를 무조건 호출");
        }
        Assert.Equal(1, CountOf(helper, "ContentionFactor())"));                                      // 유일한 호출부 = CurrentFactor(캐시)
        // 통과하는 값은 몇 번을 비교해도(재시도 루프 · 하위 예산 · 창 초과 수) 탐침 0회 · 실패하는 값만 탐침
        var probes = 0;
        Func<double> counting = () => { probes++; return 1.0; };
        var frame = CiFrameBudget.Custom(CiFrameBudget.BaseMs, counting);
        var layer = CiFrameBudget.Custom(1_000, counting);
        var overrun = CiFrameBudget.Custom(CiFrameBudget.WindowOverrunTolerance, counting);
        for (var i = 0; i < 50; i++)
            Assert.True(12.5 < frame && !(12.5 >= frame) && 640.0 < layer && 6L <= overrun && !(6L > overrun));
        Assert.Equal(0, probes);
        Assert.False(40.0 < frame);
        Assert.Equal(1, probes);
        Assert.False(7L <= overrun);
        Assert.Equal(2, probes);
        Assert.True(7L <= CiFrameBudget.Custom(CiFrameBudget.WindowOverrunTolerance, () => 1.2));     // 경합 ×1.2 → 창 초과 허용 7.2
    }

    [Fact]
    public void TitanReleaseChainSplit_StaysFreeOfSerialOnlyHazards_AndKeepsAtMostTwoHeavyChains()
    {
        // Step 44 — 비병렬 TitanReleaseTests 에서 A/B 사슬로 분리한 클래스는 병렬 구간과 겹친다 → 직렬 전용 위해 요소 재유입 차단
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio.Tests", "TitanReleaseTests.cs"));
        var a = src.IndexOf("public sealed class TitanReleaseChainATests", StringComparison.Ordinal);
        var b = src.IndexOf("public sealed class TitanReleaseChainBTests", StringComparison.Ordinal);
        var end = src.IndexOf("internal sealed class CommerceStubHandler", StringComparison.Ordinal);
        Assert.True(a > 0 && b > a && end > b);
        var serial = src.Substring(0, a);
        foreach (var (name, body) in new[] { ("A", src.Substring(a, b - a)), ("B", src.Substring(b, end - b)) })
        {
            foreach (var banned in new[]
            {
                "SetEnvironmentVariable", "Process.Start", "ProcessStartInfo", "RunBundle(", "RunSign(", "RunProcess(",               // 프로세스 전역 · 실 프로세스
                "GC.CollectionCount", "GC.GetTotalAllocatedBytes", "GC.GetTotalMemory", "GC.GetGCMemoryInfo",                     // 프로세스 GC 지표 단언
                "LatencyMode", "TryStartNoGCRegion", "ZeroGcRegionScope",                                                       // 프로세스 GC 모드
                "GetAllocatedBytesForCurrentThread", "Stopwatch", "CiFrameBudget", "LastTickMs", "TickMsSeries",                  // 단발 무할당 창 · 실 벽시계 게이트
            })
                Assert.False(body.Contains(banned, StringComparison.Ordinal), "사슬 " + name + " 에 직렬 전용 요소 " + banned);
            Assert.True(CountOf(body, "SteadyStateAllocation.MinBytes(") >= 10, "사슬 " + name + " 무할당 단언은 재시도 창만");
            foreach (var line in body.Split('\n'))
                if (line.Contains("GetTempPath()", StringComparison.Ordinal))
                    Assert.True(line.Contains("Guid.NewGuid()", StringComparison.Ordinal), "사슬 " + name + " 임시 경로 GUID 격리: " + line.Trim());
        }
        // 정적 가변 프로브(DomProbeComponent)는 유일 사용처와 함께 A 사슬로 — 직렬 클래스에서 참조 0 · 공유 헬퍼는 internal 로만 노출
        Assert.Equal(0, CountOf(serial, "DomProbeComponent"));
        Assert.Contains("internal sealed class CrewTestProvider", serial, StringComparison.Ordinal);
        Assert.Contains("internal static byte[] SineWav(", serial, StringComparison.Ordinal);
        // 배치 · 동시 중량 사슬 ≤ 2(3 번째 사슬 C 는 실측 폐기 — async 연속 기아)
        static string? CollectionOf(Type t) => t.GetCustomAttributesData().FirstOrDefault(d => d.AttributeType == typeof(CollectionAttribute))?.ConstructorArguments[0].Value as string;
        Assert.Equal("TitanHeavyCompute", CollectionOf(typeof(TitanReleaseChainATests)));
        Assert.Equal("TitanHeavyComputeB", CollectionOf(typeof(TitanReleaseChainBTests)));
        Assert.Equal("ProcessGcMode", CollectionOf(typeof(TitanReleaseTests)));
        var heavy = typeof(TitanReleaseTests).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData().Where(d => d.AttributeType == typeof(CollectionDefinitionAttribute)))
            .Select(d => d.ConstructorArguments[0].Value as string)
            .Where(n => n is not null && n.StartsWith("TitanHeavy", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "TitanHeavyCompute", "TitanHeavyComputeB" }, heavy);
    }

    [Fact]
    public void CiFrameBudget_WindowContention_KeepsPeakWindowFactor_AndStaysExactWhenIdle()
    {
        // Step 44 — 창 동안 경합(×2) → 판정 시점 유휴(×1)여도 창 최대 배수로 판정 · 기준 통과 창은 탐침 0회
        var probes = 0; var now = 1.0;
        var load = new WindowContention(() => { probes++; return now; });
        load.Observe(false);
        Assert.Equal(0, probes);
        Assert.True(12.0 < load.Budget(CiFrameBudget.BaseMs) && 6L <= load.Budget(CiFrameBudget.WindowOverrunTolerance));
        Assert.Equal(0, probes);                                                                      // 정상 경로 비용 0
        Assert.False(40.0 < load.Budget(CiFrameBudget.BaseMs));                                      // 유휴 — 기준 33.3 그대로
        Assert.False(7L <= load.Budget(CiFrameBudget.WindowOverrunTolerance));
        Assert.Equal(1.0, load.PeakFactor);

        now = 2.0; load.Observe(true);                                                                // 기준 초과 창 직후 표본(경합 중)
        now = 1.0;                                                                                     // 판정 시점엔 경합 해소
        Assert.Equal(2.0, load.PeakFactor);
        Assert.Equal(1, load.Samples);
        Assert.True(40.0 < load.Budget(CiFrameBudget.BaseMs) && 12L <= load.Budget(CiFrameBudget.WindowOverrunTolerance));
        Assert.False(70.0 < load.Budget(CiFrameBudget.BaseMs));                                      // ×2 초과 회귀는 그대로 실패
        Assert.False(13L <= load.Budget(CiFrameBudget.WindowOverrunTolerance));
        now = 9.0; load.Observe(true);
        Assert.Equal(CiFrameBudget.MaxContentionFactor, load.PeakFactor);                             // 상한 ×3
        Assert.False(100.0 < load.Budget(CiFrameBudget.BaseMs));
        var idle = new WindowContention(() => 1.10);                                                  // 유휴 폭 이하 → 정확히 1.0
        idle.Observe(true);
        Assert.Equal(1.0, idle.PeakFactor);
        Assert.Throws<ArgumentNullException>(() => new WindowContention(null!));
    }

    // ── ⑤ ⚡ 자율 추가 — 회로 차단 수동 탐침/해제 + 상태 통지 + Workspace 표면화 ──

    [Fact]
    public void CircuitBreaker_ManualProbeSkipsCooldown_ResetClosesImmediately_AndStateChangesNotifyWorkspace()
    {
        var now = new DateTimeOffset(T0);
        var breaker = new ProviderCircuitBreaker { Clock = () => now };
        var events = 0;
        breaker.StateChanged += () => events++;
        breaker.StateChanged += () => throw new InvalidOperationException("UI 핸들러 예외는 격리되어야 한다");

        Assert.False(breaker.RequestProbe("Wan"));                                                    // 추적 전 · 닫힘 → 무동작
        Assert.False(breaker.Reset("Wan"));
        Assert.Empty(breaker.GetTrippedSnapshots());

        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++)
        {
            Assert.True(breaker.TryAcquire("Wan", out _));
            breaker.RecordTransientFailure("Wan", "503 Service Unavailable");
        }
        Assert.Equal(1, events);                                                                      // Closed → Open 1회만 통지
        Assert.False(breaker.TryAcquire("Wan", out var wait));
        Assert.Equal(ProviderCircuitBreaker.BaseCooldown, wait);
        var tripped = Assert.Single(breaker.GetTrippedSnapshots());
        Assert.Equal(("Wan", ProviderCircuitState.Open), (tripped.Provider, tripped.State));

        // 🔎 [지금 탐침] — 냉각 30초를 기다리지 않고 다음 호출 1건이 탐침
        Assert.True(breaker.RequestProbe("wan"));
        Assert.Equal(2, events);
        Assert.Equal(TimeSpan.Zero, breaker.GetSnapshot("Wan").RetryAfter);
        Assert.True(breaker.TryAcquire("Wan", out _));
        Assert.Equal(3, events);                                                                      // Open → HalfOpen
        Assert.Equal(ProviderCircuitState.HalfOpen, breaker.GetSnapshot("Wan").State);
        Assert.False(breaker.TryAcquire("Wan", out _));                                               // 탐침은 1건만
        Assert.False(breaker.RequestProbe("Wan"));                                                    // 반개방 중에는 재요청 무의미

        // 수동 탐침이 실패하면 지수 백오프는 그대로 — 냉각 2배로 재차단
        Assert.True(breaker.RecordTransientFailure("Wan", "504 Gateway Timeout"));
        Assert.Equal(4, events);
        Assert.False(breaker.TryAcquire("Wan", out var doubled));
        Assert.Equal(ProviderCircuitBreaker.CooldownFor(1), doubled);
        Assert.Equal(TimeSpan.FromSeconds(60), doubled);

        // ♻️ [차단 해제] — 즉시 닫힘 · 연속 실패/냉각 단계 초기화 · 누적 통계 보존
        Assert.True(breaker.Reset("Wan"));
        Assert.Equal(5, events);
        var closed = breaker.GetSnapshot("Wan");
        Assert.Equal((ProviderCircuitState.Closed, 0, 2, 4), (closed.State, closed.ConsecutiveFailures, closed.TripCount, closed.TotalFailures));
        Assert.Empty(breaker.GetTrippedSnapshots());
        Assert.True(breaker.TryAcquire("Wan", out _));
        Assert.False(breaker.Reset("Wan"));                                                           // 이미 닫힘 → 통지 없음
        Assert.Equal(5, events);
        // 해제 뒤 재장애 → 냉각은 기본값부터 다시
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
        Assert.False(breaker.TryAcquire("Wan", out var again));
        Assert.Equal(ProviderCircuitBreaker.BaseCooldown, again);
        // 자동 복구(탐침 성공)도 통지
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.True(breaker.TryAcquire("Wan", out _));
        breaker.RecordSuccess("Wan");
        Assert.Equal(8, events);                                                                      // 재차단 · 반개방 · 닫힘

        // Workspace 결선 — 닫히지 않은 회로만 노출하는 배지 1개(헤더 밖) + 버튼 2종 + 구독/해제 쌍
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "data-circuit-strip="));
        Assert.True(ws.IndexOf("data-circuit-strip=", StringComparison.Ordinal) > tools);
        Assert.Equal(1, CountOf(ws, "data-circuit-probe="));
        Assert.Equal(1, CountOf(ws, "data-circuit-reset="));
        Assert.Contains("@if (_circuitRows.Count > 0)", ws, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "_circuitBreaker.StateChanged += OnCircuitStateChanged"));
        Assert.Equal(1, CountOf(ws, "_circuitBreaker.StateChanged -= OnCircuitStateChanged"));
        Assert.Contains("_ = InvokeAsync(StateHasChanged);", ws.Substring(ws.IndexOf("private void OnCircuitStateChanged()", StringComparison.Ordinal), 600), StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "AttachCircuitBreaker();"));

        // DI — 데스크톱 + 모바일 양쪽에 공유 싱글턴 1회 등록 · 렌더 엔진이 그 인스턴스를 주입받고 Workspace 도 DI 에서 먼저 해석
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Render.ProviderCircuitBreaker>()"));
            Assert.Equal(1, CountOf(di, "{ CircuitBreaker = sp.GetRequiredService<PromptFlowStudio.Services.Render.ProviderCircuitBreaker>(), Journal ="));
            Assert.True(di.IndexOf("AddSingleton<PromptFlowStudio.Services.Render.ProviderCircuitBreaker>()", StringComparison.Ordinal)
                < di.IndexOf("AddSingleton<PromptFlowStudio.Services.Render.IRenderExecutionEngine>", StringComparison.Ordinal));
            Assert.DoesNotContain("Drive 7대", di, StringComparison.Ordinal);                         // 허브는 8대(+ 계정·설정 2종)
            Assert.Equal(1, CountOf(di, "Drive 8대 Pluto 핵심 허브"));
        }
        Assert.Contains("EcoSp.GetService(typeof(PromptFlowStudio.Services.Render.ProviderCircuitBreaker)) as PromptFlowStudio.Services.Render.ProviderCircuitBreaker", ws, StringComparison.Ordinal);
        // 주입된 차단기 = 엔진이 쓰는 차단기(같은 참조) → UI 의 탐침/해제가 실제 렌더 경로에 작용
        var shared = new ProviderCircuitBreaker { Clock = () => now };
        var engine = new VideoEngineRenderExecutionEngine(null, null, (_, _) => Task.CompletedTask) { Ambient = null, CircuitBreaker = shared };
        Assert.Same(shared, engine.CircuitBreaker);
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) shared.RecordTransientFailure("Wan", "503");
        Assert.Equal(ProviderCircuitState.Open, engine.CircuitBreaker!.GetSnapshot("Wan").State);
        Assert.True(shared.Reset("Wan"));
        Assert.Empty(engine.CircuitBreaker.GetTrippedSnapshots());
    }

    // ── ⑥ 🛡️ 자율 추가 — 보정 상태 내구성(마지막 정상본 복구 · 읽기 전용 세션 자가 치유) ──

    [Fact]
    public async Task CalibrationState_RecoversFromLastKnownGoodBackup_WhenPrimaryIsTornOrEmptied()
    {
        var path = Path.Combine(_dir, "calibration.json");
        using (var first = Calibration())
        {
            for (var i = 0; i < 3; i++) await first.RecordOutcomeAsync(Missed("hook:놓친 훅"));
            Assert.False(first.RecoveredFromBackup);
            Assert.Equal(path + PlutoMetaCalibrationService.BackupSuffix, first.BackupPath);
        }
        Assert.True(File.Exists(path) && File.Exists(path + PlutoMetaCalibrationService.BackupSuffix));
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(path + PlutoMetaCalibrationService.BackupSuffix));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Drive 동기화가 본 파일을 0 B 자리표시자로 만든 상황 → .bak 에서 전량 복구
        File.WriteAllBytes(path, Array.Empty<byte>());
        using (var healed = Calibration())
        {
            await healed.EnsureLoadedAsync();
            Assert.True(healed.RecoveredFromBackup);
            Assert.False(healed.IsReadOnlySession);
            var s = healed.GetStatus();
            Assert.Equal((3, 3), (s.TotalRecords, s.RegretSamples));
            Assert.Equal(0.90, s.CumulativeRegret, 9);
            Assert.Equal("hook:놓친 훅", s.TopRegretAlternative!.Label);
            Assert.Equal(0.90, Assert.Single(healed.GetRegretRecoveryBoosts()).OutstandingRegret, 9);
            Assert.True(File.Exists(path + ".corrupt"));                                              // 손상본 격리 사본 보존
            // 다음 기록이 본 파일을 정상 JSON 으로 되돌린다
            await healed.RecordOutcomeAsync(Missed("hook:놓친 훅"));
        }
        using (var doc = JsonDocument.Parse(File.ReadAllText(path))) Assert.Equal(4, doc.RootElement.GetProperty("TotalRecords").GetInt32());

        // 중간에서 잘린(찢긴) JSON 도 동일하게 복구
        var full = File.ReadAllText(path);
        File.WriteAllText(path, full.Substring(0, full.Length / 2));
        using (var torn = Calibration())
        {
            await torn.EnsureLoadedAsync();
            Assert.True(torn.RecoveredFromBackup);
            Assert.Equal(4, torn.GetStatus().TotalRecords);
        }

        // 백업도 없으면 기존 동작 — 격리 후 빈 상태로 시작([부분 구제])
        var lonely = Path.Combine(_dir, "lonely.json");
        File.WriteAllText(lonely, "{ not json");
        using var empty = new PlutoMetaCalibrationService(lonely) { UtcNowProvider = () => _now };
        await empty.EnsureLoadedAsync();
        Assert.False(empty.RecoveredFromBackup);
        Assert.Same(PlutoCalibrationStatus.Empty, empty.GetStatus());

        // 사용자가 파일을 지운 초기화는 존중(백업으로 되살리지 않음)
        File.Delete(path);
        using var reset = Calibration();
        await reset.EnsureLoadedAsync();
        Assert.False(reset.RecoveredFromBackup);
        Assert.Same(PlutoCalibrationStatus.Empty, reset.GetStatus());
    }

    [Fact]
    public async Task CalibrationState_ReadOnlySessionSelfHeals_OnceTheDriveLockClears_WithoutLosingDiskOrSessionRecords()
    {
        var path = Path.Combine(_dir, "calibration.json");
        using (var seed = Calibration())
            for (var i = 0; i < 2; i++) await seed.RecordOutcomeAsync(Missed("hook:디스크 대안"));
        var onDisk = File.ReadAllText(path);

        using var session = Calibration();
        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);      // Drive 동기화 · 백신의 배타 잠금
        try
        {
            await session.EnsureLoadedAsync();
            Assert.True(session.IsReadOnlySession);
            // 잠긴 동안의 관측 → 메모리에만(디스크의 기존 학습을 빈 상태로 덮어쓰지 않음)
            await session.RecordOutcomeAsync(Missed("hook:세션 대안"));
            Assert.True(session.IsReadOnlySession);
            Assert.Equal(1, session.GetStatus().TotalRecords);
            Assert.Equal(0, session.HealedReadOnlySessions);
        }
        finally { locker.Dispose(); }
        Assert.Equal(onDisk, File.ReadAllText(path));                                                 // 디스크 불변

        // 잠금 해제 후 다음 기록 → 디스크 상태 적재 + 세션 중 관측 재생 + 이번 관측 = 2 + 1 + 1
        var healed = await session.RecordOutcomeAsync(Missed("hook:세션 대안"));
        Assert.False(session.IsReadOnlySession);
        Assert.Equal(1, session.HealedReadOnlySessions);
        Assert.Equal((4, 4), (healed.TotalRecords, healed.RegretSamples));
        Assert.Equal(1.20, healed.CumulativeRegret, 9);
        var boosts = session.GetRegretRecoveryBoosts();
        Assert.Equal(new[] { "hook:디스크 대안", "hook:세션 대안" }, boosts.Select(b => b.Label).OrderBy(l => l, StringComparer.Ordinal).ToArray());
        Assert.All(boosts, b => Assert.Equal(0.60, b.OutstandingRegret, 9));

        // 영속 확인 — 새 인스턴스가 병합 결과를 그대로 읽는다
        using var reloaded = Calibration();
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(4, reloaded.GetStatus().TotalRecords);
        Assert.Equal(2, reloaded.GetRegretRecoveryBoosts().Count);
        Assert.False(reloaded.IsReadOnlySession || reloaded.RecoveredFromBackup);
    }

    // ── ⑦ 🔍 Step 15 감사 보완 — 검증 끝난 대안의 후회 고착 방지 · 비테마 리허설 챌린저 오버라이드 ──

    [Fact]
    public async Task RegretReport_DoesNotPinTestedOrDecayedAlternativeAsMostRegretted_AndNonThemedRehearsalOverridesThemedChallenger()
    {
        const string big = "hook:큰 후회", small = "hook:작은 후회";
        using var calibration = Calibration("sticky.json");
        for (var i = 0; i < 3; i++) await calibration.RecordOutcomeAsync(Missed(big));                // 누적 0.90
        await calibration.RecordOutcomeAsync(Missed(small));                                           // 누적 0.30
        var top = calibration.GetStatus().TopRegretAlternative!;
        Assert.Equal((big, 3, false), (top.Label, top.Count, top.IsResolved));
        Assert.Equal(0.90, top.OutstandingRegret, 9);
        Assert.Contains("가장 아쉬운 대안: " + big + "(0.90 · 3회)", PlutoMetaCalibrationService.BuildRegretLabel(calibration.GetStatus()), StringComparison.Ordinal);

        // ✅ 큰 후회 대안을 실제로 게시·측정 → 리포트 1위에서 내려오고 아직 검증 안 된 대안이 올라온다(누적 이력은 보존)
        await calibration.RecordOutcomeAsync(new PlutoCalibrationObservation(double.NaN, 0.55, double.NaN) { ChosenLabels = new[] { big }, Source = "test" });
        var status = calibration.GetStatus();
        Assert.Equal(small, status.TopRegretAlternative!.Label);
        Assert.Equal(0.30, status.TopRegretAlternative.OutstandingRegret, 9);
        var label = PlutoMetaCalibrationService.BuildRegretLabel(status);
        Assert.Contains("가장 아쉬운 대안: " + small + "(0.30 · 1회)", label, StringComparison.Ordinal);
        Assert.DoesNotContain(big, label, StringComparison.Ordinal);
        Assert.Equal(1.20, status.CumulativeRegret, 9);                                                // 감사 누계는 불변

        // 전부 검증되면 '가장 아쉬운 대안'을 더는 주장하지 않는다 — 이력(누적 최대)만 참고로 표기
        await calibration.RecordOutcomeAsync(new PlutoCalibrationObservation(double.NaN, 0.55, double.NaN) { ChosenLabels = new[] { small }, Source = "test" });
        var resolved = calibration.GetStatus().TopRegretAlternative!;
        Assert.True(resolved.IsResolved);
        Assert.Equal((big, 0.0), (resolved.Label, resolved.OutstandingRegret));
        Assert.Equal(0.90, resolved.CumulativeRegret, 9);
        var done = PlutoMetaCalibrationService.BuildRegretLabel(calibration.GetStatus());
        Assert.Contains("미해소 대안 없음", done, StringComparison.Ordinal);
        Assert.DoesNotContain("가장 아쉬운 대안", done, StringComparison.Ordinal);
        Assert.Empty(calibration.GetRegretRecoveryBoosts());

        // 재시작 후에도 고착이 되살아나지 않음
        using (var reloaded = Calibration("sticky.json"))
        {
            await reloaded.EnsureLoadedAsync();
            Assert.True(reloaded.GetStatus().TopRegretAlternative!.IsResolved);
        }

        // ⏳ 시간 감쇠 — 오래된 큰 후회(0.90 → 56일 뒤 1/16)는 최근의 작은 후회(0.30)에 1위를 내준다
        using var aging = Calibration("aging.json");
        for (var i = 0; i < 3; i++) await aging.RecordOutcomeAsync(Missed(big));
        _now = T0.AddDays(56);
        await aging.RecordOutcomeAsync(Missed(small));
        var aged = aging.GetStatus().TopRegretAlternative!;
        Assert.Equal(small, aged.Label);
        Assert.Equal(0.30, aged.OutstandingRegret, 9);
        _now = T0;

        // 🎭 비테마 오버라이드 — 테마 훅 우선 배치(PreferThemed) 뒤에도, 테마·템플릿 어디에도 없는 고후회 훅이 챌린저 B 로 올라간다
        const string offTheme = "테마 밖 실험 훅 — 반전 엔딩 먼저";
        var themed = PlutoMontageStoryGenerator.TemplateHookLines("일상");
        Assert.DoesNotContain(offTheme, themed);
        Assert.DoesNotContain(offTheme, PlutoMontageStoryGenerator.AllTemplateHookLines);
        var learning = Learning("offtheme_trend.json");
        var notebook = Notebook("offtheme_nb.json");
        using var missed = Calibration("offtheme.json");
        var plain = await new PlutoVariantABGenerator(new PlutoAdaptivePatternRanker(learning, notebook), null, notebook, () => "exp-themed").BuildVariantPairAsync("일상");
        Assert.Contains(plain.ChallengerB.HookStyle, themed);                                          // 기준 — 리허설 없으면 테마 훅이 챌린저
        for (var i = 0; i < 2; i++) await missed.RecordOutcomeAsync(Missed("hook:" + offTheme));
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = missed };
        var plan = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-offtheme").BuildVariantPairAsync("일상");
        Assert.Equal(offTheme, plan.ChallengerB.HookStyle);
        Assert.Equal(plain.ChampionA.HookStyle, plan.ChampionA.HookStyle);
        Assert.True(plan.IsRegretRehearsal && plan.IsValidExperiment);
        Assert.Equal("hook:" + offTheme, plan.RegretRehearsalLabel);
        Assert.Equal(offTheme, plan.ChallengerB.Subtitles[0].Text);
    }

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
