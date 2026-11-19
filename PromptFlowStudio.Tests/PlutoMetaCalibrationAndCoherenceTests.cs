using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🎯🧩🩹⚡ Step 14 — 메타 보정(Brier/RMSE · 반사실 후회 → UCB1 탐색 배수) · 렌더 직전 교차 모달 정합성 게이트 ·
/// 컷 체크포인트 자가 치유 + TTL/LRU 정리 · 두뇌 리포트 표면화 · 자율 추가(프로바이더 회로 차단기 · 후회 귀속 · 부하 무관 기동 예산).
/// </summary>
public sealed class PlutoMetaCalibrationAndCoherenceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
    private const string Look = PlutoColorGradingCompiler.GoldenHourWarmName;
    private const string Baby = "BabyStudio", Montage = "Montage";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step14_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;
    private int _seq;

    public PlutoMetaCalibrationAndCoherenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };
    private PlutoMistakeNotebookService Notebook(string name = "nb.json") => new(Path.Combine(_dir, name), () => _now);
    private PlutoMetaCalibrationService Calibration(string name = "calibration.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };

    private PlutoVideoPerformanceTelemetry Video(string hook, double hookPct, double completionPct, double ctrPct, string? domain = Baby, string? mode = Montage, string look = Look)
        => new("v-" + (++_seq).ToString("000"), "YouTube Shorts", hook, look, "Lullaby", Array.Empty<string>(), hookPct, completionPct, ctrPct, DateTimeOffset.UtcNow)
        { Domain = domain, RenderMode = mode };

    private static PlutoCalibrationObservation Obs(double predicted, double realized, double alternative = double.NaN, string? label = null)
        => new(predicted, realized, alternative) { BestAlternativeLabel = label, Source = "test" };

    // ── ① 🎯 메타 보정 → UCB1 탐색 배수 ──

    [Fact]
    public async Task MetaCalibration_OverconfidenceAndRegret_AmplifyUcbExploration_WhileGoodCalibrationTightensIt()
    {
        // 순수 함수 — 표본 부족 → 1 · 과신 → 증폭 · 후회 → 증폭 · 양호 → 수축 · 편차만 큼(무편향) → 중립
        Assert.Equal(1.0, PlutoMetaCalibrationService.ComputeExplorationScale(2, 0.30, 0.30, 2, 0.30), 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxScale, PlutoMetaCalibrationService.ComputeExplorationScale(5, 0.30, 0.30, 0, 0.0), 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxScale, PlutoMetaCalibrationService.ComputeExplorationScale(0, 0.0, 0.0, 4, 0.20), 9);
        Assert.Equal(PlutoMetaCalibrationService.MinScale, PlutoMetaCalibrationService.ComputeExplorationScale(10, 0.02, 0.0, 10, 0.0), 9);
        Assert.Equal(1.0, PlutoMetaCalibrationService.ComputeExplorationScale(10, 0.20, 0.0, 10, 0.0), 9);
        Assert.InRange(PlutoMetaCalibrationService.ComputeExplorationScale(5, 0.10, 0.095, 0, 0.0), 1.49, 1.51);   // 허용 폭 초과분 선형
        Assert.Equal(1.0, PlutoMetaCalibrationService.ComputeExplorationScale(5, 0.10, -0.30, 0, 0.0), 9);          // 과소 예측은 증폭 사유 아님

        var (brier, bias, n) = PlutoMetaCalibrationService.ComputeCalibration(new[] { (0.8, 0.5), (0.6, 0.6), (double.NaN, 0.4) });
        Assert.Equal(2, n);
        Assert.Equal(0.045, brier, 9);
        Assert.Equal(0.15, bias, 9);

        // 드리프트 온도 결합 — 서지 중 수축 금지 · 상한 ×4
        Assert.Equal(3.0, PlutoAdaptivePatternRanker.CombineExplorationMultiplier(3.0, 0.6), 9);
        Assert.Equal(0.6, PlutoAdaptivePatternRanker.CombineExplorationMultiplier(1.0, 0.6), 9);
        Assert.Equal(PlutoAdaptivePatternRanker.MaxExplorationMultiplier, PlutoAdaptivePatternRanker.CombineExplorationMultiplier(3.0, 2.0), 9);
        Assert.Equal(1.0, PlutoAdaptivePatternRanker.CombineExplorationMultiplier(double.NaN, -1.0), 9);

        using var learning = Learning();
        using var notebook = Notebook();
        await learning.RecordPatternTrialsAsync(new[] { "hook:검증된 훅", "hook:검증된 훅" });
        var candidates = new[] { "검증된 훅", "미검증 훅" };
        var plain = new PlutoAdaptivePatternRanker(learning, notebook);
        var baselineBonus = plain.RankOptions(PlutoPatternKind.Hook, candidates).Single(o => o.DisplayLabel == "미검증 훅").ExplorationBonus;
        Assert.True(baselineBonus > 0);

        // 과신(예측 0.80 → 실현 0.50) 누적 → 탐색 상수 ×2
        using var over = Calibration("over.json");
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = over };
        await ranker.EnsureLoadedAsync();
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC, ranker.EffectiveExplorationC(), 9);   // 기록 없음 → 기존 동작
        for (var i = 0; i < 2; i++) await over.RecordOutcomeAsync(Obs(0.80, 0.50));
        Assert.Equal(1.0, over.GetCalibratedExplorationScale(), 9);                                 // 표본 2 < 3
        var status = await over.RecordOutcomeAsync(Obs(0.80, 0.50));
        Assert.True(status.IsOverconfident && status.IsAmplifying);
        Assert.Equal(0.30, status.Bias, 9);
        Assert.Equal(0.30, status.Rmse, 9);
        Assert.Equal(0.09, status.BrierScore, 9);
        Assert.Equal(70.0, status.AccuracyPct, 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxScale, over.GetCalibratedExplorationScale(), 9);
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC * 2.0, ranker.EffectiveExplorationC(), 9);
        var amplified = ranker.RankOptions(PlutoPatternKind.Hook, candidates).Single(o => o.DisplayLabel == "미검증 훅");
        Assert.Equal(baselineBonus * 2.0, amplified.ExplorationBonus, 9);
        Assert.Equal(plain.RankOptions(PlutoPatternKind.Hook, candidates)[0].Key, ranker.RankOptions(PlutoPatternKind.Hook, candidates)[0].Key);   // 활용 순위 불변

        // 반사실 후회만 상승(예측 없음) → 증폭
        using var regret = Calibration("regret.json");
        for (var i = 0; i < 3; i++) await regret.RecordOutcomeAsync(Obs(double.NaN, 0.40, 0.62, "hook:놓친 훅"));
        var rs = regret.GetStatus();
        Assert.Equal(0, rs.CalibrationSamples);
        Assert.Equal(3, rs.RegretSamples);
        Assert.Equal(0.22, rs.MeanRegret, 9);
        Assert.Equal(0.66, rs.CumulativeRegret, 9);
        Assert.Equal(PlutoMetaCalibrationService.MaxScale, rs.ExplorationScale, 9);
        Assert.Equal("hook:놓친 훅", rs.TopRegretAlternative!.Label);
        Assert.Equal(3, rs.TopRegretAlternative.Count);

        // 보정 양호 + 후회 0 → 활용 집중(표본이 쌓일수록 0.6 으로)
        using var good = Calibration("good.json");
        var tight = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = good };
        var prev = 1.0;
        for (var i = 0; i < 10; i++)
        {
            await good.RecordOutcomeAsync(Obs(0.60, 0.61, 0.55, "hook:열등 대안"));
            var scale = good.GetCalibratedExplorationScale();
            Assert.True(scale <= prev + 1e-12);
            prev = scale;
        }
        Assert.Equal(PlutoMetaCalibrationService.MinScale, prev, 9);
        Assert.True(good.GetStatus().IsTightening);
        Assert.Null(good.GetStatus().TopRegretAlternative);                                          // 후회 0 → 귀속 없음
        Assert.Equal(PlutoAdaptivePatternRanker.ExplorationC * PlutoMetaCalibrationService.MinScale, tight.EffectiveExplorationC(), 9);
        Assert.Equal(baselineBonus * PlutoMetaCalibrationService.MinScale,
            tight.RankOptions(PlutoPatternKind.Hook, candidates).Single(o => o.DisplayLabel == "미검증 훅").ExplorationBonus, 9);

        // 근거 없는 관측(예측 · 대안 모두 NaN) → 무변경
        var before = good.GetStatus().TotalRecords;
        await good.RecordOutcomeAsync(Obs(double.NaN, 0.5));
        Assert.Equal(before, good.GetStatus().TotalRecords);
    }

    // ── ② 💾 영속 · 하위 호환 · 손상/잠금 보호 ──

    [Fact]
    public async Task MetaCalibration_PersistsRollingWindow_LoadsLegacyAndCorruptFilesSafely()
    {
        var path = Path.Combine(_dir, "calibration.json");
        using (var svc = Calibration())
        {
            for (var i = 0; i < PlutoMetaCalibrationService.WindowCapacity + 6; i++)
            {
                _now = T0.AddMinutes(i);
                await svc.RecordOutcomeAsync(Obs(0.70, 0.50, 0.60, i % 2 == 0 ? "hook:대안 A" : "look:대안 B"));
            }
            var s = svc.GetStatus();
            Assert.Equal(PlutoMetaCalibrationService.WindowCapacity, s.CalibrationSamples);          // 롤링 창
            Assert.Equal(PlutoMetaCalibrationService.WindowCapacity + 6, s.TotalRecords);            // 누계는 창과 무관
            Assert.Equal(0.10 * (PlutoMetaCalibrationService.WindowCapacity + 6), s.CumulativeRegret, 6);
            Assert.Equal(0.10, s.MeanRegret, 9);
            Assert.Equal("hook:대안 A", s.TopRegretAlternative!.Label);                               // 동률 → 키 사전순
        }
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // 재적재 — 동일 상태 복원
        using (var reloaded = Calibration())
        {
            await reloaded.EnsureLoadedAsync();
            var s = reloaded.GetStatus();
            Assert.Equal(PlutoMetaCalibrationService.WindowCapacity, s.CalibrationSamples);
            Assert.Equal(PlutoMetaCalibrationService.WindowCapacity + 6, s.TotalRecords);
            Assert.Equal(0.20, s.Bias, 9);
            Assert.True(s.IsAmplifying);
            Assert.Equal(35, s.TopRegretAlternative!.Count);
        }

        // 상위 버전이 쓴 미지 필드 · 누락 필드 · null/비유한 값 → 무시하고 적재(상·하위 호환)
        var legacy = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(legacy, "{\"Version\":7,\"FutureField\":{\"x\":1},\"TotalRecords\":2,\"Window\":[{\"P\":0.9,\"R\":0.4,\"Extra\":true},null,{\"R\":0.5,\"A\":0.8}]}");
        using (var old = Calibration("legacy.json"))
        {
            await old.EnsureLoadedAsync();
            var s = old.GetStatus();
            Assert.Equal(1, s.CalibrationSamples);
            Assert.Equal(1, s.RegretSamples);
            Assert.Equal(0.30, s.MeanRegret, 9);
            Assert.Equal(1.0, s.ExplorationScale, 9);                                                // 표본 부족
            Assert.False(old.IsReadOnlySession);
        }

        // 손상 JSON + 마지막 정상본(.bak) 없음 → .corrupt 격리 후 빈 상태로 시작(기록 가능) · .bak 이 있으면 복구(Step 15 전용 테스트)
        File.Delete(path + PlutoMetaCalibrationService.BackupSuffix);
        File.WriteAllText(path, "{\"Window\":[{\"P\":0.9");
        using (var broken = Calibration())
        {
            await broken.EnsureLoadedAsync();
            Assert.Same(PlutoCalibrationStatus.Empty, broken.GetStatus());
            Assert.True(File.Exists(path + ".corrupt"));
            await broken.RecordOutcomeAsync(Obs(0.5, 0.5));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, doc.RootElement.GetProperty("Window").GetArrayLength());
        }

        // 읽기 불가(배타 잠금) 세션 → 디스크 덮어쓰기 금지 · 메모리만 갱신
        var original = File.ReadAllText(path);
        using (var locked = Calibration())
        {
            // 잠금이 유지되는 동안의 기록(잠금 해제 뒤 자가 치유 · 병합은 Step 15 전용 테스트)
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await locked.EnsureLoadedAsync();
                Assert.True(locked.IsReadOnlySession);
                await locked.RecordOutcomeAsync(Obs(0.9, 0.1));
                Assert.True(locked.IsReadOnlySession);
                Assert.Equal(1, locked.GetStatus().CalibrationSamples);
            }
        }
        Assert.Equal(original, File.ReadAllText(path));

        // Drive 백업 허브 등록(새 PC 복원 대상)
        Assert.Contains(PlutoMetaCalibrationService.FileName, PlutoDriveHealthService.PlutoFiles);
    }

    // ── ③ 📈 폐루프 · A/B → (예측 · 실현 · 최선 대안) 기록 ──

    [Fact]
    public async Task ClosedLoopAndAbEvaluation_RecordPredictedRealizedAndBestAlternative_WithRegretAttribution()
    {
        const string strong = "강한 훅", weak = "약한 훅";
        using var learning = Learning();
        using var notebook = Notebook();
        using var calibration = Calibration();
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook) { MetaCalibration = calibration };

        // 최초 관측 — 기준선 없음 → 예측 불가 → 감사 생략
        var first = await loop.IngestPerformanceAsync(Video(strong, 82, 61, 9.4));
        Assert.Null(first.CalibrationObservation);
        Assert.Same(PlutoCalibrationStatus.Empty, calibration.GetStatus());

        // 두 번째 — 기준선만 있음(패턴 기대치 미성립) → 예측 = 기준선 · 대안 없음
        var second = await loop.IngestPerformanceAsync(Video(weak, 40, 28, 2.0));
        Assert.NotNull(second.CalibrationObservation);
        Assert.True(second.CalibrationObservation!.HasPrediction);
        Assert.False(second.CalibrationObservation.HasAlternative);
        Assert.Equal(PlutoContextualBaseline.CompositeScore(82, 61, 9.4), second.CalibrationObservation.PredictedExpectedScore, 9);
        Assert.Equal(PlutoContextualBaseline.CompositeScore(40, 28, 2.0), second.CalibrationObservation.RealizedScore, 9);

        for (var i = 0; i < 2; i++)
        {
            await loop.IngestPerformanceAsync(Video(strong, 82, 61, 9.4));
            await loop.IngestPerformanceAsync(Video(weak, 40, 28, 2.0));
        }

        // 예측 스냅샷 — 약한 훅을 고르면 '선택하지 않은' 강한 훅이 최선 대안
        var forecast = learning.ForecastExpectancy(Baby, Montage, weak, Look);
        Assert.True(forecast.HasBaseline && forecast.HasAlternative);
        Assert.Equal("hook:" + strong, forecast.BestAlternativeLabel);
        Assert.True(forecast.BestAlternativeScore > forecast.PredictedScore + 0.10);
        Assert.Equal(2, forecast.EstablishedPatterns);
        var strongForecast = learning.ForecastExpectancy(Baby, Montage, strong, Look);
        Assert.Equal("hook:" + weak, strongForecast.BestAlternativeLabel);                           // 차선 대안
        Assert.True(strongForecast.BestAlternativeScore < strongForecast.PredictedScore);

        // 약한 훅을 또 선택해 부진 → 반사실 후회 기록 + 요약 노출 + 대안 귀속
        var regretBefore = calibration.GetStatus().CumulativeRegret;
        var poor = await loop.IngestPerformanceAsync(Video(weak, 40, 28, 2.0));
        var obs = poor.CalibrationObservation!;
        Assert.Equal(forecast.PredictedScore, obs.PredictedExpectedScore, 9);                        // 관측 반영 '이전' 예측
        Assert.Equal(forecast.BestAlternativeScore, obs.BestAlternativeExpectedScore, 9);
        Assert.Equal("hook:" + strong, obs.BestAlternativeLabel);
        Assert.True(poor.CounterfactualRegret > 0.10);
        Assert.Equal(obs.BestAlternativeExpectedScore - obs.RealizedScore, poor.CounterfactualRegret, 9);
        Assert.Contains("💸 반사실 후회", poor.SummaryMessage, StringComparison.Ordinal);
        Assert.Contains("(대안 hook:" + strong + ")", poor.SummaryMessage, StringComparison.Ordinal);
        var status = calibration.GetStatus();
        Assert.Equal(regretBefore + poor.CounterfactualRegret, status.CumulativeRegret, 9);
        Assert.Equal("hook:" + strong, status.TopRegretAlternative!.Label);
        Assert.Contains("가장 아쉬운 대안: hook:" + strong, PlutoMetaCalibrationService.BuildRegretLabel(status), StringComparison.Ordinal);

        // 중복 반영 차단 → 감사도 중복 없음
        var total = status.TotalRecords;
        var dup = await loop.IngestPerformanceAsync(Video(weak, 40, 28, 2.0) with { VideoId = "v-001" });
        Assert.True(dup.AlreadyRegistered);
        Assert.Equal(total, calibration.GetStatus().TotalRecords);

        // ⚖️ A/B — 반사실이 실측된다: 패자 후회 = 승자 − 패자 · 승자 후회 = 0 · 변형당 1건
        var winner = Video("챌린저 훅", 70, 50, 6.0);
        var loser = Video("챔피언 훅", 45, 32, 2.5);
        var regretSamples = calibration.GetStatus().RegretSamples;
        var cumulative = calibration.GetStatus().CumulativeRegret;
        var ab = await loop.IngestExperimentOutcomeAsync(new PlutoExperimentReinforcementRequest("exp-1", winner, loser, 40.0, false));
        var gap = PlutoContextualBaseline.CompositeScore(winner) - PlutoContextualBaseline.CompositeScore(loser);
        Assert.Equal(gap, ab.CounterfactualRegret, 9);
        Assert.Equal("hook:챌린저 훅", ab.CalibrationObservation!.BestAlternativeLabel);
        Assert.True(ab.CalibrationObservation.HasPrediction);
        Assert.Equal(regretSamples + 2, calibration.GetStatus().RegretSamples);
        Assert.Equal(cumulative + gap, calibration.GetStatus().CumulativeRegret, 9);
        var abDup = await loop.IngestExperimentOutcomeAsync(new PlutoExperimentReinforcementRequest("exp-1", winner, loser, 40.0, false));
        Assert.True(abDup.AlreadyRegistered);
        Assert.Equal(regretSamples + 2, calibration.GetStatus().RegretSamples);

        // 메타 보정 미연결(기존 구성) → 감사 없음 · 결과 동일 계약
        using var legacyLearning = Learning("legacy.json");
        using var legacyLoop = new PlutoClosedLoopReinforcementService(legacyLearning, null);
        await legacyLoop.IngestPerformanceAsync(Video(strong, 82, 61, 9.4));
        var legacy = await legacyLoop.IngestPerformanceAsync(Video(weak, 40, 28, 2.0));
        Assert.Null(legacy.CalibrationObservation);
        Assert.Equal(0.0, legacy.CounterfactualRegret);
        Assert.DoesNotContain("반사실", legacy.SummaryMessage, StringComparison.Ordinal);
    }

    // ── ④ 🧩 렌더 직전 교차 모달 정합성 게이트 ──

    [Fact]
    public async Task CoherenceGuard_DetectsAndAutoSanitizes_DomainNegativeSubtitleAndAntiSynergyContradictions()
    {
        const string hook = "첫 3초에 눈 맞춤";
        using var notebook = Notebook();
        var guard = new PlutoPreRenderCoherenceGuard(notebook);

        // 모순 없음 → 100점 · 배지 없음
        var clean = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.GeneralCinematic, "neon city drone flythrough at dusk",
            PromptDomainClassifier.GeneralCinematicNegative, new[] { new SubtitleCue(0, 3, "도시의 밤"), new SubtitleCue(3, 6, "네온이 깨어난다") }, hook, Look, null, "16:9"));
        Assert.Equal(100, clean.CoherenceScore0To100);
        Assert.False(clean.WasAutoSanitized);
        Assert.False(clean.HasFindings);
        Assert.Empty(clean.WarningsKorean);
        Assert.Equal(PromptDomainClassifier.GeneralCinematicNegative, clean.SanitizedNegativePrompt);
        Assert.Equal(2, clean.SanitizedSubtitleCues.Count);
        Assert.Contains("모순 없음", clean.SummaryKorean, StringComparison.Ordinal);

        // GeneralCinematic 네거티브에 아기 가드 + 아기 토큰 구문 누출 · 중복 구문 → 자동 보정
        var leakedNegative = BabyStudioMatrix.FacialSafetyNegative + ", blurry, baby face distortion, watermark, Blurry, 아기 얼굴 왜곡";
        var leak = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.GeneralCinematic, "glass skyscraper timelapse", leakedNegative, null, null, null, null, "1280*720"));
        Assert.True(leak.WasAutoSanitized);
        Assert.Equal("blurry, watermark", leak.SanitizedNegativePrompt);
        Assert.False(PromptDomainClassifier.ContainsBabyToken(leak.SanitizedNegativePrompt));
        Assert.DoesNotContain(BabyStudioMatrix.FacialSafetyNegative, leak.SanitizedNegativePrompt, StringComparison.Ordinal);
        Assert.Equal(2, leak.SanitizationsKorean.Count);
        Assert.Contains(leak.SanitizationsKorean, f => f.Contains("아기 도메인 구문 3건", StringComparison.Ordinal));
        Assert.Contains(leak.SanitizationsKorean, f => f.Contains("중복 네거티브 구문 1건", StringComparison.Ordinal));
        Assert.Equal(100 - PlutoPreRenderCoherenceGuard.PerCheckPenaltyCap - PlutoPreRenderCoherenceGuard.PenaltyDuplicateNegative, leak.CoherenceScore0To100);
        Assert.Contains("자동 보정 2건", leak.SummaryKorean, StringComparison.Ordinal);

        // BabyStudio 는 같은 가드가 정상 구성 → 보정 없음
        var baby = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기가 처음 웃는 순간", BabyStudioMatrix.FacialSafetyNegative, null, null, null, "Lullaby", "9:16"));
        Assert.False(baby.WasAutoSanitized);
        Assert.Equal(100, baby.CoherenceScore0To100);
        Assert.Equal(BabyStudioMatrix.FacialSafetyNegative, baby.SanitizedNegativePrompt);

        // 프롬프트 ↔ 네거티브 자기모순 · 도메인 ↔ BGM 불일치 → 경고(자동 보정 불가)
        var contradiction = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.GeneralCinematic, "warm golden light over the harbor",
            "golden light, lowres", null, null, null, "Lullaby", "16:9"));
        Assert.False(contradiction.WasAutoSanitized);
        Assert.Equal(2, contradiction.WarningsKorean.Count);
        Assert.Contains(contradiction.WarningsKorean, w => w.Contains("'golden light'", StringComparison.Ordinal));
        Assert.Contains(contradiction.WarningsKorean, w => w.Contains("아기용 BGM", StringComparison.Ordinal));
        Assert.Equal(100 - PlutoPreRenderCoherenceGuard.PenaltySelfContradiction - PlutoPreRenderCoherenceGuard.PenaltyBgmMismatch, contradiction.CoherenceScore0To100);
        Assert.Equal("golden light, lowres", contradiction.SanitizedNegativePrompt);                  // 사용자 문구 보존

        // 자막 — 빈 큐 제거 · 연속 중복 병합(표시 구간 연장) · 9:16 폭 초과 경고
        var longLine = new string('가', 40);   // 폭 80 > 72
        var cues = new[]
        {
            new SubtitleCue(0, 2, "안녕, 우리 아기"), new SubtitleCue(2, 4, "  "), new SubtitleCue(4, 6, "안녕, 우리 아기 "),
            SubtitleCue.Precise(6.0, 9.5, longLine), new SubtitleCue(9, 9, "끝"),
        };
        var subs = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기 성장 일기", null, cues, null, null, null, "720*1280"));
        Assert.True(subs.WasAutoSanitized);
        Assert.Equal(3, subs.SanitizedSubtitleCues.Count);
        Assert.Equal((0, 6, "안녕, 우리 아기"), (subs.SanitizedSubtitleCues[0].StartSec, subs.SanitizedSubtitleCues[0].EndSec, subs.SanitizedSubtitleCues[0].Text));
        Assert.Contains(subs.SanitizationsKorean, f => f.Contains("빈 자막 큐 1건", StringComparison.Ordinal));
        Assert.Contains(subs.SanitizationsKorean, f => f.Contains("연속 중복 자막 큐 1건", StringComparison.Ordinal));
        Assert.Contains(subs.WarningsKorean, w => w.Contains("9:16 세로 화면", StringComparison.Ordinal) && w.Contains("폭 80 > 72", StringComparison.Ordinal));
        Assert.Contains(subs.WarningsKorean, w => w.Contains("역전되거나", StringComparison.Ordinal));   // 9→9 길이 0 + 앞 큐(9.5)와 겹침
        Assert.True(PlutoPreRenderCoherenceGuard.IsVertical("9:16") && PlutoPreRenderCoherenceGuard.IsVertical("1080x1920") && !PlutoPreRenderCoherenceGuard.IsVertical("1280*720"));
        Assert.Equal(80, PlutoPreRenderCoherenceGuard.DisplayWidth(longLine));
        Assert.Equal(5, PlutoPreRenderCoherenceGuard.DisplayWidth("abc 1"));
        // 같은 자막도 16:9 에서는 폭 경고 없음
        var wide = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기 성장 일기", null, new[] { SubtitleCue.Precise(0, 5, longLine) }, null, null, null, "16:9"));
        Assert.DoesNotContain(wide.WarningsKorean, w => w.Contains("안전 폭", StringComparison.Ordinal));

        // 오답 노트 역시너지 — 차단된 훅×룩 조합 · 차단된 훅 → 경고 + 플래그(자동 교체 안 함)
        var before = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기 성장 일기", null, null, hook, Look, null, "9:16"));
        Assert.False(before.HasAntiSynergyConflict);
        await notebook.RecordDiagnosedMistakeAsync(new[] { PlutoPairAttribution.PairKey(hook, Look)! }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention);
        var pair = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기 성장 일기", null, null, hook, Look, null, "9:16"));
        Assert.True(pair.HasAntiSynergyConflict);
        Assert.False(pair.WasAutoSanitized);
        Assert.Equal(100 - PlutoPreRenderCoherenceGuard.PenaltyAntiSynergy, pair.CoherenceScore0To100);
        Assert.Contains("오답 노트가 차단한 조합", Assert.Single(pair.WarningsKorean), StringComparison.Ordinal);
        // 같은 훅 + 다른 룩 → 조합 격리이므로 통과
        Assert.False(guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기 성장 일기", null, null, hook, PlutoColorGradingCompiler.NaturalPastelName, null, "9:16")).HasAntiSynergyConflict);
        await notebook.RecordDiagnosedMistakeAsync(new[] { hook }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention);
        var blocked = guard.Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "아기 성장 일기", null, null, hook, Look, null, "9:16"));
        Assert.Equal(100 - PlutoPreRenderCoherenceGuard.PenaltyAntiSynergy - PlutoPreRenderCoherenceGuard.PenaltyBlockedHook, blocked.CoherenceScore0To100);
        Assert.Equal(2, blocked.WarningsKorean.Count);

        // 오답 노트 미연결 → 해당 검사만 생략
        Assert.False(new PlutoPreRenderCoherenceGuard().Audit(new PlutoPreRenderCoherenceRequest(PlutoPromptDomain.BabyStudio, "p", null, null, hook, Look, null, null)).HasAntiSynergyConflict);
    }

    // ── ⑤ 🩹 체크포인트 자가 치유 · 🧹 TTL/LRU ──

    private string WriteMp4(string name, int bytes = 2048)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, ExportMediaIntegrityGuard.BuildMinimalMp4(bytes));
        return p;
    }

    [Fact]
    public async Task CutCheckpoint_RejectsAndQuarantinesCorruptCachedMp4_ThenRegeneratesInsteadOfPoisoningMontage()
    {
        var root = Path.Combine(_dir, "ckpt");
        var ckpt = new CutRenderCheckpointService(() => root);

        // 서비스 단위 — 손상(유효 길이 · 잘못된 컨테이너) → 격리 · 0바이트 → 삭제 · 잘림 → 격리
        var k1 = ckpt.ComputeCutKey("corrupt", 1, 5, "1280*720", "wan");
        var k2 = ckpt.ComputeCutKey("zero", 1, 5, "1280*720", "wan");
        var k3 = ckpt.ComputeCutKey("truncated", 1, 5, "1280*720", "wan");
        var src = WriteMp4("src.mp4", 4096);
        foreach (var k in new[] { k1, k2, k3 }) ckpt.SaveCheckpoint(k, src);
        Assert.True(ckpt.TryGetValidCheckpoint(k1, out _));
        File.WriteAllBytes(Path.Combine(root, k1 + ".mp4"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(root, k2 + ".mp4"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(root, k3 + ".mp4"), File.ReadAllBytes(src).AsSpan(0, 1500).ToArray());   // free 박스가 파일 끝을 넘음
        Assert.False(ckpt.TryGetValidCheckpoint(k1, out var p1));
        Assert.Equal(string.Empty, p1);
        Assert.Contains("ftyp", ckpt.LastIntegrityError, StringComparison.Ordinal);
        Assert.False(ckpt.TryGetValidCheckpoint(k2, out _));
        Assert.False(ckpt.TryGetValidCheckpoint(k3, out _));
        Assert.Equal(3, ckpt.IntegrityRejections);
        Assert.Empty(Directory.GetFiles(root, "*.mp4"));
        var quarantine = Path.Combine(root, CutRenderCheckpointService.QuarantineFolderName);
        var quarantined = Directory.GetFiles(quarantine, "*" + CutRenderCheckpointService.QuarantineExtension).Select(Path.GetFileName).ToArray();
        Assert.Equal(2, quarantined.Length);                                                         // 0바이트는 진단 가치 없음 → 삭제
        Assert.Contains(quarantined, n => n!.StartsWith(k1 + ".", StringComparison.Ordinal));
        Assert.Contains(quarantined, n => n!.StartsWith(k3 + ".", StringComparison.Ordinal));
        Assert.False(ckpt.TryGetValidCheckpoint(k1, out _));                                         // 격리 후엔 단순 미스
        Assert.Equal(3, ckpt.IntegrityRejections);

        // 격리 폴더 상한(최근 5개)
        for (var i = 0; i < CutRenderCheckpointService.MaxQuarantineEntries + 3; i++)
        {
            var k = ckpt.ComputeCutKey("flood " + i, 1, 5, "1280*720", "wan");
            ckpt.SaveCheckpoint(k, src);
            File.WriteAllBytes(Path.Combine(root, k + ".mp4"), new byte[2048]);
            Assert.False(ckpt.TryGetValidCheckpoint(k, out _));
        }
        Assert.Equal(CutRenderCheckpointService.MaxQuarantineEntries, Directory.GetFiles(quarantine).Length);

        // 엔진 단위 — 손상된 캐시 컷은 재사용하지 않고 프로바이더로 새로 생성 → 체크포인트 자가 복구
        var engineCkpt = new CutRenderCheckpointService(() => Path.Combine(_dir, "ckpt-engine"));
        var template = new BatchRenderJob
        {
            Prompt = "neon city drone flythrough", Seed = 42, Resolution = "1280*720",
            OutputPath = Path.Combine(_dir, "out", "city.mp4"), DurationMode = VideoDurationMode.Sec15_Triple,
        };
        var cut = MultiDurationPlanner.Expand(template, null, "city")[0];
        var provider = new ToggleEngine(_dir);
        var engine = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(provider), null, (_, _) => Task.CompletedTask, engineCkpt)
        { RetryDelay = static (_, _) => Task.CompletedTask, Ambient = null };

        Assert.True((await engine.ExecuteAsync(cut, new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(1, provider.Calls);
        var cached = Assert.Single(Directory.GetFiles(engineCkpt.Root, "*.mp4"));
        File.WriteAllBytes(cached, new byte[8192]);                                                  // 디스크 오류 · 중단된 동기화 흉내
        File.Delete(cut.OutputPath!);

        var regenerated = await engine.ExecuteAsync(cut, new Progress<int>(), CancellationToken.None);
        Assert.True(regenerated.Ok);
        Assert.Equal(2, provider.Calls);                                                             // 재사용 대신 새로 생성
        Assert.NotEqual(VideoEngineRenderExecutionEngine.CheckpointReuseMessage(1), regenerated.Message);
        Assert.Null(VideoEngineRenderExecutionEngine.CheckpointNoticeOf(cut));
        Assert.True(ExportMediaIntegrityGuard.TryValidateMp4(regenerated.OutputPath, out _));
        Assert.Equal(1, engineCkpt.IntegrityRejections);
        Assert.True(ExportMediaIntegrityGuard.TryValidateMp4(Assert.Single(Directory.GetFiles(engineCkpt.Root, "*.mp4")), out _));   // 새 유효 체크포인트

        File.Delete(cut.OutputPath!);
        var reused = await engine.ExecuteAsync(cut, new Progress<int>(), CancellationToken.None);
        Assert.Equal(2, provider.Calls);                                                             // 이제는 정상 재사용
        Assert.Equal(VideoEngineRenderExecutionEngine.CheckpointReuseMessage(1), reused.Message);
    }

    [Fact]
    public void CutCheckpoint_PrunesByTtlThenLeastRecentlyUsedWithinByteBudget_Deterministically()
    {
        // 실제 시각보다 미래의 주입 시계 → OS/백신이 갱신하는 접근 시각이 결과에 끼어들 수 없음
        var t0 = DateTimeOffset.UtcNow.AddDays(2);
        var clock = t0;
        var root = Path.Combine(_dir, "ckpt-lru");
        var svc = new CutRenderCheckpointService(() => root) { Clock = () => clock };
        var src = WriteMp4("lru.mp4", 2048);
        var keys = new string[4];
        for (var i = 0; i < keys.Length; i++)
        {
            clock = t0.AddMinutes(i);
            keys[i] = svc.ComputeCutKey("lru " + i, 1, 5, "1280*720", "wan");
            svc.SaveCheckpoint(keys[i], src);
        }
        Assert.Equal(4, svc.LastPruneReport.RemainingCount);
        Assert.Equal(4 * 2048L, svc.LastPruneReport.RemainingBytes);
        Assert.Equal(0, svc.LastPruneReport.TotalRemoved);

        // 가장 오래된 #0 을 재사용(적중) → 최근 사용으로 승격
        clock = t0.AddMinutes(10);
        Assert.True(svc.TryGetValidCheckpoint(keys[0], out _));

        // 용량 예산 = 2개분 → LRU 순(#1 → #2) 축출 · #0(방금 적중) · #3(최신 기록) 보존
        var report = svc.PruneStaleCheckpoints(CutRenderCheckpointService.MaxAge, 2 * 2048L + 100);
        Assert.Equal((4, 0, 2, 0, 2, 2 * 2048L), (report.ScannedCount, report.ExpiredRemoved, report.EvictedForBudget, report.OrphanTempRemoved, report.RemainingCount, report.RemainingBytes));
        Assert.Same(report, svc.LastPruneReport);
        Assert.True(File.Exists(Path.Combine(root, keys[0] + ".mp4")) && File.Exists(Path.Combine(root, keys[3] + ".mp4")));
        Assert.False(File.Exists(Path.Combine(root, keys[1] + ".mp4")) || File.Exists(Path.Combine(root, keys[2] + ".mp4")));
        Assert.Equal(0, svc.PruneStaleCheckpoints(CutRenderCheckpointService.MaxAge, 2 * 2048L + 100).TotalRemoved);   // 멱등

        // 예산 0/음수 → 용량 기준 생략 · TTL 은 '기록 시각' 기준(적중이 수명을 늘리지 않음)
        Assert.Equal(0, svc.PruneStaleCheckpoints(TimeSpan.Zero, 0).TotalRemoved);
        clock = t0.AddHours(24).AddMinutes(1);   // #0 기록 후 24h 초과 · #3 은 24h 미만
        var orphan = Path.Combine(root, keys[1] + ".deadbeef.tmp");
        var young = Path.Combine(root, keys[2] + ".cafef00d.tmp");
        File.WriteAllBytes(orphan, new byte[16]);
        File.WriteAllBytes(young, new byte[16]);
        File.SetLastWriteTimeUtc(orphan, clock.UtcDateTime - CutRenderCheckpointService.OrphanTempAge - TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(young, clock.UtcDateTime);
        var ttl = svc.PruneStaleCheckpoints(CutRenderCheckpointService.MaxAge, CutRenderCheckpointService.MaxTotalBytes);
        Assert.Equal((1, 0, 1, 1), (ttl.ExpiredRemoved, ttl.EvictedForBudget, ttl.OrphanTempRemoved, ttl.RemainingCount));
        Assert.False(File.Exists(Path.Combine(root, keys[0] + ".mp4")));
        Assert.True(File.Exists(Path.Combine(root, keys[3] + ".mp4")));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(young));                                                             // 진행 중일 수 있는 임시 파일 보호

        // 저장 직후 자동 정리 — 방금 저장한 파일은 예산을 넘어도 보호 · 개수 상한 유지
        for (var i = 0; i < CutRenderCheckpointService.MaxEntries + 3; i++)
        {
            clock = clock.AddSeconds(1);
            svc.SaveCheckpoint(svc.ComputeCutKey("fill " + i, 1, 5, "1280*720", "wan"), src);
        }
        Assert.Equal(CutRenderCheckpointService.MaxEntries, Directory.GetFiles(root, "*.mp4").Length);
        Assert.Equal(CutRenderCheckpointService.MaxEntries, svc.LastPruneReport.RemainingCount);
        Assert.False(File.Exists(Path.Combine(root, keys[3] + ".mp4")));                             // 가장 오래 안 쓴 항목부터

        // 폴더 없음 · 무동작 구현 → 빈 보고(예외 없음)
        Assert.Same(CutCheckpointPruneReport.Empty, new CutRenderCheckpointService(() => Path.Combine(_dir, "none")).PruneStaleCheckpoints(TimeSpan.FromHours(1), 1));
        Assert.Same(CutCheckpointPruneReport.Empty, NullCutRenderCheckpointService.Instance.PruneStaleCheckpoints(TimeSpan.FromHours(1), 1));
    }

    // ── ⑥ 🧠 두뇌 리포트 · Workspace ──

    [Fact]
    public async Task BrainSummaryAndWorkspace_SurfaceCalibrationAndRegret_WithoutTouchingHeaderRows()
    {
        using var learning = Learning();
        using var notebook = Notebook();
        using var calibration = Calibration();
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook) { MetaCalibration = calibration };
        var svc = new PlutoAutonomousBrainSummaryService(null, ranker, notebook);

        var empty = await svc.GetSummaryAsync();
        Assert.Same(PlutoCalibrationStatus.Empty, empty.Calibration);
        Assert.Contains("기록 없음", empty.CalibrationLabel, StringComparison.Ordinal);
        Assert.Equal("기록 없음", empty.CounterfactualRegretLabel);

        for (var i = 0; i < 4; i++) await calibration.RecordOutcomeAsync(Obs(0.75, 0.45, 0.70, "hook:놓친 훅"));
        var s = await svc.GetSummaryAsync();
        Assert.True(s.Calibration.IsOverconfident);
        Assert.Equal(4, s.Calibration.CalibrationSamples);
        Assert.Equal("과신 · 정확도 70% · RMSE 0.300 · Brier 0.090 · 편향 +0.300 · n=4 · 탐색 배수 ×2.00(증폭)", s.CalibrationLabel);
        Assert.Equal("평균 0.250 · 누적 1.00 · n=4 · 상승(탐색 증폭) · 가장 아쉬운 대안: hook:놓친 훅(1.00 · 4회)", s.CounterfactualRegretLabel);

        // 랭커 미연결 · 메타 보정 미연결 → 기본값으로 [부분 구제]
        var bare = await new PlutoAutonomousBrainSummaryService(null, null, null).GetSummaryAsync();
        Assert.Same(PlutoCalibrationStatus.Empty, bare.Calibration);
        var unwired = await new PlutoAutonomousBrainSummaryService(null, new PlutoAdaptivePatternRanker(learning, notebook), notebook).GetSummaryAsync();
        Assert.Contains("기록 없음", unwired.CalibrationLabel, StringComparison.Ordinal);

        // Workspace — 접이식 두뇌 리포트 카드 본문에만 표면화 · 상단 3행 헤더 불변
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(strip > 0 && tools > strip);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        Assert.True(open > 0);
        foreach (var marker in new[] { "data-brain-calibration=", "data-brain-regret=" })
        {
            Assert.Equal(1, CountOf(ws, marker));
            Assert.True(body.IndexOf(marker, StringComparison.Ordinal) > open);                      // 접힘 상태에선 렌더되지 않음
        }
        Assert.Contains("@(bs.CalibrationLabel)", body, StringComparison.Ordinal);
        Assert.Contains("@(bs.CounterfactualRegretLabel)", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));                                              // 헤더 행 수 불변
        Assert.Equal(1, CountOf(ws, "[🧠 플루토 두뇌 리포트]"));
        var head = ws.Substring(0, strip);
        foreach (var keyword in new[] { "보정 정확도", "반사실 후회", "data-brain-calibration", "data-brain-regret", "MetaCalibration", "CounterfactualRegret", "정합성", "data-coherence-badge", "CoherenceGuard" })
            Assert.DoesNotContain(keyword, head, StringComparison.Ordinal);
        // Step 13 본문 마커 보존
        foreach (var marker in new[] { "data-brain-drift=", "data-brain-context=", "data-brain-posterior=", "data-brain-synergy=", "data-mistake-inspector=" })
            Assert.Equal(1, CountOf(body, marker));

        // 🧩 정합성 게이트 — 렌더 3경로(단일 · 멀티 컷 큐 · 몽타주) 결선 + 배지 1개(헤더 밖)
        Assert.Equal(1, CountOf(ws, "data-coherence-badge="));
        Assert.True(ws.IndexOf("data-coherence-badge=", StringComparison.Ordinal) > tools);
        Assert.Equal(4, CountOf(ws, "RunCoherenceGate("));                                           // 정의 1 + 호출 3
        Assert.Contains("negative = gate.SanitizedNegativePrompt", ws, StringComparison.Ordinal);
        Assert.Contains("domainNeg = gate.SanitizedNegativePrompt", ws, StringComparison.Ordinal);
        Assert.Contains("cues = montageGate.SanitizedSubtitleCues", ws, StringComparison.Ordinal);

        // DI — 데스크톱 + 모바일 양쪽 등록 · 랭커/폐루프 결선
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoMetaCalibrationService>"));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoPreRenderCoherenceGuard>"));
            Assert.Equal(2, CountOf(di, "{ MetaCalibration = sp.GetService("));
        }
    }

    // ── ⑦ ⚡ 자율 추가 — 프로바이더 회로 차단기 ──

    [Fact]
    public void ProviderCircuitBreaker_OpensAfterConsecutiveTransientFailures_ProbesAfterCooldown_AndBacksOffExponentially()
    {
        var now = new DateTimeOffset(T0);
        var breaker = new ProviderCircuitBreaker { Clock = () => now };
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot("Wan").State);
        Assert.Empty(breaker.GetSnapshots());

        // 임계 미만 → 닫힘 유지 · 성공이 연속 실패를 초기화
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold - 1; i++)
        {
            Assert.True(breaker.TryAcquire("Wan", out _));
            Assert.False(breaker.RecordTransientFailure("Wan", "503 Service Unavailable"));
        }
        Assert.True(breaker.TryAcquire("wan", out _));                                               // 키 대소문자 무시
        breaker.RecordSuccess("Wan");
        Assert.Equal(0, breaker.GetSnapshot("Wan").ConsecutiveFailures);

        // 3회 연속 → 열림 · 냉각 중 호출 생략
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++)
        {
            Assert.True(breaker.TryAcquire("Wan", out _));
            Assert.Equal(i == ProviderCircuitBreaker.FailureThreshold - 1, breaker.RecordTransientFailure("Wan", "504 Gateway Timeout"));
        }
        Assert.False(breaker.TryAcquire("Wan", out var retry));
        Assert.Equal(ProviderCircuitBreaker.BaseCooldown, retry);
        Assert.True(breaker.TryAcquire("ComfyUI", out _));                                           // 프로바이더별 격리
        var open = breaker.GetSnapshot("Wan");
        Assert.Equal((ProviderCircuitState.Open, 3, 1, 1), (open.State, open.ConsecutiveFailures, open.TripCount, open.ShortCircuitedCalls));
        Assert.Equal("504 Gateway Timeout", open.LastError);
        Assert.Contains("차단(약 30초 후 탐침)", open.ToLabel(), StringComparison.Ordinal);

        // 냉각 경과 → 탐침 1건만 통과 · 탐침 실패 → 냉각 2배
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.True(breaker.TryAcquire("Wan", out _));
        Assert.Equal(ProviderCircuitState.HalfOpen, breaker.GetSnapshot("Wan").State);
        Assert.False(breaker.TryAcquire("Wan", out var probing));                                    // 동시 호출은 탐침 결과 대기
        Assert.Equal(TimeSpan.Zero, probing);
        Assert.True(breaker.RecordTransientFailure("Wan", "503"));
        Assert.False(breaker.TryAcquire("Wan", out retry));
        Assert.Equal(TimeSpan.FromSeconds(60), retry);
        Assert.Equal(2, breaker.GetSnapshot("Wan").TripCount);

        // 취소된 탐침 → 슬롯 반납(다음 호출이 다시 탐침) · 성공 → 닫힘 + 냉각 단계 초기화
        now += TimeSpan.FromSeconds(60);
        Assert.True(breaker.TryAcquire("Wan", out _));
        breaker.RecordAbandoned("Wan");
        Assert.True(breaker.TryAcquire("Wan", out _));
        breaker.RecordSuccess("Wan");
        var closed = breaker.GetSnapshot("Wan");
        Assert.Equal((ProviderCircuitState.Closed, 0, 2), (closed.State, closed.ConsecutiveFailures, closed.TripCount));
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) { breaker.TryAcquire("Wan", out _); breaker.RecordTransientFailure("Wan", "429"); }
        Assert.False(breaker.TryAcquire("Wan", out retry));
        Assert.Equal(ProviderCircuitBreaker.BaseCooldown, retry);                                    // 복구 후엔 다시 30초부터

        // 영구 오류(인증) = 프로바이더 생존 증거 → 닫힘 · 냉각 상한 5분 · 시계 역행 → 영구 차단 방지
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.True(breaker.TryAcquire("Wan", out _));
        breaker.RecordReachable("Wan");
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot("Wan").State);
        Assert.Equal(ProviderCircuitBreaker.MaxCooldown, ProviderCircuitBreaker.CooldownFor(10));
        Assert.Equal(TimeSpan.FromSeconds(120), ProviderCircuitBreaker.CooldownFor(2));
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) { breaker.TryAcquire("Wan", out _); breaker.RecordTransientFailure("Wan", "503"); }
        now -= TimeSpan.FromHours(1);
        Assert.True(breaker.TryAcquire("Wan", out _));

        // 차단 메시지는 재시도 분류기에 '일시 장애'로 잡히지 않는다(큐가 차단 중 재시도 폭주하지 않게)
        for (var sec = 1; sec <= 300; sec++)
        {
            var msg = ProviderCircuitBreaker.BuildOpenMessage("Wan", TimeSpan.FromSeconds(sec));
            Assert.True(ProviderCircuitBreaker.IsOpenMessage(msg));
            Assert.False(CutRetryPolicy.IsTransientMessage(msg), msg);
        }
        Assert.Contains("복구 탐침 진행 중", ProviderCircuitBreaker.BuildOpenMessage(null, TimeSpan.Zero), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderEngine_ShortCircuitsProviderCallsWhileCircuitOpen_ButStillServesCheckpointsAndRecovers()
    {
        var now = new DateTimeOffset(T0);
        var breaker = new ProviderCircuitBreaker { Clock = () => now };
        var ckpt = new CutRenderCheckpointService(() => Path.Combine(_dir, "ckpt-cb"));
        var provider = new ToggleEngine(_dir);
        var engine = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(provider), null, (_, _) => Task.CompletedTask, ckpt)
        { RetryDelay = static (_, _) => Task.CompletedTask, Ambient = null, CircuitBreaker = breaker };
        BatchRenderJob Job(string name) => new()
        {
            Prompt = "harbor sunrise " + name, Seed = 7, Resolution = "1280*720", OutputPath = Path.Combine(_dir, "cb", name + ".mp4"),
        };
        var template = new BatchRenderJob
        {
            Prompt = "harbor sunrise series", Seed = 7, Resolution = "1280*720", OutputPath = Path.Combine(_dir, "cb", "series.mp4"),
            DurationMode = VideoDurationMode.Sec15_Triple,
        };
        var cuts = MultiDurationPlanner.Expand(template, null, "series");

        // 정상 구간 — 컷 1 완료(체크포인트 저장)
        Assert.True((await engine.ExecuteAsync(cuts[0], new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(1, provider.Calls);

        // 프로바이더 다운 — 작업 3건 연속 일시 실패(컷은 건당 1 + 재시도 2회) → 회로 열림
        provider.Failure = "503 Service Unavailable";
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++)
            Assert.False((await engine.ExecuteAsync(Job("down" + i), new Progress<int>(), CancellationToken.None)).Ok);
        var callsAtTrip = provider.Calls;
        Assert.Equal(1 + ProviderCircuitBreaker.FailureThreshold, callsAtTrip);
        Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot("Fake").State);

        // 차단 중 — 프로바이더 호출 0 · 즉시 실패 메시지 · 완료된 컷은 체크포인트로 계속 제공
        var blocked = await engine.ExecuteAsync(cuts[1], new Progress<int>(), CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.True(ProviderCircuitBreaker.IsOpenMessage(blocked.Message));
        Assert.Contains("약 30초 뒤 자동 탐침", blocked.Message, StringComparison.Ordinal);
        File.Delete(cuts[0].OutputPath!);
        var served = await engine.ExecuteAsync(cuts[0], new Progress<int>(), CancellationToken.None);
        Assert.True(served.Ok);
        Assert.Equal(VideoEngineRenderExecutionEngine.CheckpointReuseMessage(1), served.Message);
        Assert.Equal(callsAtTrip, provider.Calls);
        Assert.Equal(1, breaker.GetSnapshot("Fake").ShortCircuitedCalls);

        // 냉각 경과 → 탐침 1건(컷이므로 재시도 포함 3회 호출) 실패 → 냉각 60초
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.False((await engine.ExecuteAsync(cuts[1], new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(callsAtTrip + 1 + CutRetryPolicy.MaxRetries, provider.Calls);
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.True(ProviderCircuitBreaker.IsOpenMessage((await engine.ExecuteAsync(cuts[1], new Progress<int>(), CancellationToken.None)).Message));

        // 프로바이더 복구 → 탐침 성공 → 닫힘 → 이후 정상
        provider.Failure = null;
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.True((await engine.ExecuteAsync(cuts[1], new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot("Fake").State);
        Assert.True((await engine.ExecuteAsync(cuts[2], new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(2, breaker.GetSnapshot("Fake").TripCount);

        // 인증 오류는 몇 번이 와도 회로를 열지 않는다(프로바이더는 응답 중 — 사용자 설정 문제)
        provider.Failure = "InvalidApiKey: 401 invalid key";
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold + 2; i++)
        {
            var auth = await engine.ExecuteAsync(Job("auth" + i), new Progress<int>(), CancellationToken.None);
            Assert.False(auth.Ok);
            Assert.False(ProviderCircuitBreaker.IsOpenMessage(auth.Message));
        }
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot("Fake").State);

        // 차단기 미사용(null) → 기존 동작(매번 호출)
        var legacyProvider = new ToggleEngine(_dir) { Failure = "503 Service Unavailable" };
        var legacy = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(legacyProvider), null, (_, _) => Task.CompletedTask) { Ambient = null, CircuitBreaker = null };
        for (var i = 0; i < 5; i++) await legacy.ExecuteAsync(Job("legacy" + i), new Progress<int>(), CancellationToken.None);
        Assert.Equal(5, legacyProvider.Calls);
    }

    // ── ⑧ ⏱️ 자율 추가 — 부하 무관 기동 예산(타이밍 플레이크 근본 원인) ──

    [Fact]
    public void SmokeBootstrapBudget_IsMeasuredInChildCpuTime_NotLoadSensitiveWallClock()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio.Tests", "TitanReleaseTests.cs"));
        var start = src.IndexOf("public async Task SingleFileBinaryProcessBootstrap_Check()", StringComparison.Ordinal);
        var end = src.IndexOf("public void ReleaseBundleChecksumGenerator_Check()", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var body = src.Substring(start, end - start);
        Assert.Contains("p.TotalProcessorTime", body, StringComparison.Ordinal);
        Assert.Contains("cpuMs < SmokeCpuBudgetMs", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sw.ElapsedMilliseconds <", body, StringComparison.Ordinal);            // 벽시계 성능 단언 금지
        Assert.DoesNotContain("SmokeTimeoutMs", src, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(SmokeHangTimeoutMs)", body, StringComparison.Ordinal);            // 교착 판정은 유지
    }

    /// <summary>호출 수를 세고 <see cref="Failure"/> 가 설정된 동안 그 메시지로 실패하는 엔진.</summary>
    private sealed class ToggleEngine(string dir) : IVideoGenerationEngine
    {
        public string? Failure { get; set; }
        public int Calls { get; private set; }
        public string Key => "Fake";
        public string DisplayName => "Fake";
        public bool Enabled => true;

        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            if (Failure is { } fail) return Task.FromResult(new WanGenerationResult(false, WanTaskStatus.Failed, null, fail, null, TimeSpan.Zero));
            var path = Path.Combine(dir, "gen_" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(path, ExportMediaIntegrityGuard.BuildMinimalMp4(4096));
            return Task.FromResult(new WanGenerationResult(true, WanTaskStatus.Succeeded, path, "ok", null, TimeSpan.Zero));
        }

        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "n/a"));
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
