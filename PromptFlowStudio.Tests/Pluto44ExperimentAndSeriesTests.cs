using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🏆📚 Pluto 4.4 — A/B 실험 평가 장부(차등 강화 · 패자 보호) · 멀티 에피소드 시리즈 분할 · Drive 헬스 장부 추적.</summary>
public sealed class Pluto44ExperimentAndSeriesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto44_" + Guid.NewGuid().ToString("N"));

    public Pluto44ExperimentAndSeriesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string HookA = "이 순간을 기다렸어요";
    private const string HookB = "3초만 보면 웃음 터져요";

    private static PlutoVariantABPlan Plan(string id)
    {
        var cues = new[] { SubtitleCue.Precise(0.2, 4.95, "a") };
        var a = new PlutoExperimentVariant(PlutoVariantABGenerator.ChampionLabel, HookA, PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", "", cues);
        var b = new PlutoExperimentVariant(PlutoVariantABGenerator.ChallengerLabel, HookB, PlutoColorGradingCompiler.GoldenHourWarmName, "Lullaby", "", cues);
        return new PlutoVariantABPlan(id, "돌잔치", a, b, "h");
    }

    private PlutoDriveStorageManager Storage(string name)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        return new PlutoDriveStorageManager(null, root, Path.Combine(_dir, name + "_legacy"));
    }

    [Fact]
    public async Task ExperimentLedger_ChallengerOutperformsChampion_CrownsChallengerAndBoostsTrait()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        var storage = Storage("vault");
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook, storage);
        using var ledger = new PlutoExperimentLedgerService(loop, storage);
        var plan = Plan("exp-44-001");
        ledger.RegisterExperiment(plan);
        Assert.Equal(1, ledger.RegisteredCount);
        var hookBefore = learning.GetPatternWeight("hook:" + HookB);
        var lookBefore = learning.GetPatternWeight("look:" + PlutoColorGradingCompiler.GoldenHourWarmName);
        var champBefore = learning.GetPatternWeight("hook:" + HookA);
        var bgmBefore = learning.GetPatternWeight("bgm:Lullaby");

        var r = await ledger.EvaluateExperimentAsync(plan, new PlutoExperimentMetricsInput(50, 40, 3), new PlutoExperimentMetricsInput(70, 55, 6));

        Assert.Equal(PlutoExperimentLedgerService.WinnerChallenger, r.WinnerLabel);
        Assert.Equal(43.0, r.ChampionScore, 6);    // 25 + 12 + 6
        Assert.Equal(63.5, r.ChallengerScore, 6);  // 35 + 16.5 + 12
        Assert.Equal(47.67, r.LiftPct, 2);
        Assert.InRange(r.TotalXpAwarded, PlutoClosedLoopReinforcementService.MinXp, PlutoClosedLoopReinforcementService.MaxXp);
        Assert.False(r.LoserPenalized);   // 챔피언 3초 50 % ≥ 42 % → 보호
        Assert.False(string.IsNullOrWhiteSpace(r.SummaryMessage));

        // 승자 차등 특성(훅 · 룩)만 부스트 — 공통 BGM · 패자 훅 불변
        Assert.True(learning.GetPatternWeight("hook:" + HookB) > hookBefore);
        Assert.True(learning.GetPatternWeight("look:" + PlutoColorGradingCompiler.GoldenHourWarmName) > lookBefore);
        Assert.Equal(champBefore, learning.GetPatternWeight("hook:" + HookA), 9);
        Assert.Equal(bgmBefore, learning.GetPatternWeight("bgm:Lullaby"), 9);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(HookA), 9);

        // 재판정 → 기존 결과 · 추가 XP 없음
        var weight = learning.GetPatternWeight("hook:" + HookB);
        var again = await ledger.EvaluateExperimentAsync(plan, new PlutoExperimentMetricsInput(10, 10, 1), new PlutoExperimentMetricsInput(90, 90, 9));
        Assert.True(again.AlreadyRegistered);
        Assert.Equal(r.LiftPct, again.LiftPct);
        Assert.Equal(weight, learning.GetPatternWeight("hook:" + HookB), 9);

        // 장부 재적재(새 인스턴스)
        using var reloaded = new PlutoExperimentLedgerService(null, storage);
        await reloaded.EnsureLoadedAsync();
        var recent = reloaded.GetRecentEvaluations();
        Assert.Single(recent);
        Assert.Equal("exp-44-001", recent[0].ExperimentId);
        Assert.Equal(PlutoExperimentLedgerService.WinnerChallenger, recent[0].WinnerLabel);
    }

    [Fact]
    public async Task ExperimentLedger_CloseChallengerLossAboveFailureThreshold_AvoidsMistakePenalty()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook);
        using var ledger = new PlutoExperimentLedgerService(loop);

        // ① 근소 패배(3초 55 % ≥ 42 %) — CTR 1.5 %(단일 영상 기준이면 부진) 여도 오답 노트 면제
        var close = await ledger.EvaluateExperimentAsync(Plan("exp-close"), new PlutoExperimentMetricsInput(60, 50, 5), new PlutoExperimentMetricsInput(55, 48, 1.5));
        Assert.Equal(PlutoExperimentLedgerService.WinnerChampion, close.WinnerLabel);
        Assert.False(close.LoserPenalized);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(HookB), 9);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(PlutoColorGradingCompiler.GoldenHourWarmName), 9);
        Assert.True(learning.GetPatternWeight("hook:" + HookA) > learning.GetPatternWeight("hook:" + HookB));

        // ② 절대 실패(3초 30 % < 42 %) → 패자 차등 훅만 벌점
        var fail = await ledger.EvaluateExperimentAsync(Plan("exp-fail"), new PlutoExperimentMetricsInput(60, 50, 5), new PlutoExperimentMetricsInput(30, 20, 2));
        Assert.Equal(PlutoExperimentLedgerService.WinnerChampion, fail.WinnerLabel);
        Assert.True(fail.LoserPenalized);
        Assert.True(notebook.GetEffectivePenalty(HookB) >= PlutoClosedLoopReinforcementService.MinPenalty - 1e-6);
        Assert.Equal(0.0, notebook.GetEffectivePenalty(HookA), 9);

        // ③ 무승부 → 강화 · 벌점 없음
        var tie = await ledger.EvaluateExperimentAsync(Plan("exp-tie"), new PlutoExperimentMetricsInput(60, 50, 5), new PlutoExperimentMetricsInput(60.2, 50, 5));
        Assert.Equal(PlutoExperimentLedgerService.WinnerTie, tie.WinnerLabel);
        Assert.Equal(0, tie.TotalXpAwarded);
        Assert.Equal(0.0, tie.LiftPct);
        Assert.Equal(new[] { "exp-tie", "exp-fail", "exp-close" }, ledger.GetRecentEvaluations().Select(e => e.ExperimentId).ToArray());
        Assert.Equal(2, ledger.GetRecentEvaluations(2).Count);
    }

    /// <summary>시드별 9×8 블록 휘도 + 2px 체커(선명 · dHash 상이) 회색 장면.</summary>
    private string MakeScene(string name, int seed)
    {
        const int size = 128;
        var rng = new Random(seed);
        var levels = new double[8, 9];
        for (var gy = 0; gy < 8; gy++)
            for (var gx = 0; gx < 9; gx++)
                levels[gy, gx] = 0.25 + 0.5 * rng.NextDouble();
        var path = Path.Combine(_dir, name);
        using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var v = levels[y * 8 / size, x * 9 / size] + ((((x >> 1) + (y >> 1)) & 1) == 0 ? 0.15 : -0.15);
                var b = (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(255, b, b, b));
            }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    [Fact]
    public async Task SeriesBatchPlanner_SplitsTwentyPhotosIntoDisjointEpisodes_WithContinuousSubtitles()
    {
        var photos = new List<string>(20);
        for (var i = 0; i < 18; i++) photos.Add(MakeScene("p" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".png", 1000 + i * 37));
        photos.Add(photos[3]);   // 동일 사진 2장 → dHash 중복 제거
        photos.Add(photos[7]);
        var planner = new PlutoSeriesBatchPlanner(new PlutoPhotoCurationEngine());

        var r = await planner.BuildSeriesAsync(photos, "첫 생일 여행", 3);

        Assert.Equal("첫 생일 여행", r.SeriesTheme);
        Assert.Equal(20, r.TotalInputPhotos);
        Assert.Equal(3, r.Episodes.Count);
        var indices = r.Episodes.SelectMany(e => e.Cuts).Select(c => c.OriginalIndex).ToArray();
        Assert.Equal(indices.Length, indices.Distinct().Count());                                   // 편 간 OriginalIndex 중복 0
        Assert.Equal(indices.Length, r.Episodes.SelectMany(e => e.Cuts).Select(c => c.ImagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());   // 동일 파일 중복 0
        Assert.Equal(r.TotalUsedUniquePhotos, indices.Length);
        Assert.Equal(15, indices.Length);   // 고유 18 → 3편 × 5컷
        for (var e = 0; e < r.Episodes.Count; e++)
        {
            var ep = r.Episodes[e];
            var tag = (e + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/3편";
            Assert.Equal(e + 1, ep.EpisodeNumber);
            Assert.Equal(3, ep.TotalEpisodes);
            Assert.InRange(ep.Cuts.Count, PlutoSeriesBatchPlanner.MinCutsPerEpisode, PlutoSeriesBatchPlanner.MaxCutsPerEpisode);
            Assert.Contains(tag, ep.EpisodeTitle, StringComparison.Ordinal);
            Assert.Equal(ep.Cuts.Count, ep.MontagePlan.Clips.Count);
            for (var i = 0; i < ep.Cuts.Count; i++)
            {
                var c = ep.Cuts[i];
                Assert.True(c.IsSelected);
                Assert.Equal(i + 1, c.NarrativeActOrder);
                Assert.InRange(c.FocalPoint.X, 0.20, 0.80);
                Assert.InRange(c.FocalPoint.Y, 0.20, 0.80);
                Assert.Equal(c.ImagePath, ep.MontagePlan.Clips[i].ImagePath);
                if (i > 0) Assert.True(c.OriginalIndex > ep.Cuts[i - 1].OriginalIndex);   // 촬영 순서
            }
            if (e > 0) Assert.True(ep.Cuts[0].OriginalIndex > r.Episodes[e - 1].Cuts[^1].OriginalIndex);   // 편 간 서사 연속

            Assert.Equal(ep.Cuts.Count, ep.Subtitles.Count);
            Assert.StartsWith(tag + " · ", ep.Subtitles[0].Text, StringComparison.Ordinal);
            Assert.EndsWith(e < 2 ? PlutoSeriesBatchPlanner.ContinueSuffix : PlutoSeriesBatchPlanner.FinaleSuffix, ep.Subtitles[^1].Text, StringComparison.Ordinal);
            for (var i = 1; i < ep.Subtitles.Count; i++) Assert.True(ep.Subtitles[i].EffectiveStartSec >= ep.Subtitles[i - 1].EffectiveEndSec);

            Assert.NotEmpty(ep.PublishingKit.Titles);
            Assert.All(ep.PublishingKit.Titles, t => Assert.Contains(tag, t, StringComparison.Ordinal));
            Assert.NotEmpty(ep.PublishingKit.Hashtags);
        }
        Assert.Contains("다음 편: 2/3편", r.Episodes[0].PublishingKit.CaptionKo, StringComparison.Ordinal);

        // 고유 컷 부족 → 2편 축소 · 2편도 불가 → 예외
        var seven = await planner.BuildSeriesAsync(photos.Take(7).ToArray(), "짧은 이야기", 3);
        Assert.Equal(2, seven.Episodes.Count);
        Assert.Equal(7, seven.TotalUsedUniquePhotos);
        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.BuildSeriesAsync(photos.Take(5).ToArray(), "x"));
    }

    [Fact]
    public async Task DriveHealthService_TracksExperimentLedgerFile_Cleanly()
    {
        Assert.Contains(PlutoExperimentLedgerService.FileName, PlutoDriveHealthService.PlutoFiles);
        Assert.Equal("pluto_experiment_ledger.json", PlutoExperimentLedgerService.FileName);

        var storage = Storage("drive");
        using var ledger = new PlutoExperimentLedgerService(null, storage);
        var r = await ledger.EvaluateExperimentAsync(Plan("exp-drive"), new PlutoExperimentMetricsInput(70, 60, 8), new PlutoExperimentMetricsInput(50, 40, 4));
        Assert.Equal(PlutoExperimentLedgerService.WinnerChampion, r.WinnerLabel);
        Assert.Contains("미연결", r.SummaryMessage, StringComparison.Ordinal);   // [부분 구제] 판정 · 기록은 보존

        var plutoDir = storage.ActiveRoot;
        Assert.True(File.Exists(Path.Combine(plutoDir, PlutoExperimentLedgerService.FileName)));
        var studio = Path.Combine(_dir, "studio");
        Directory.CreateDirectory(studio);
        var rep = new PlutoDriveHealthService(storage: storage).CheckHealth(plutoDir, studio);
        var item = rep.Items.Single(i => i.FileName == PlutoExperimentLedgerService.FileName);
        Assert.Equal(PlutoDriveHealthService.CategoryPluto, item.Category);
        Assert.Equal(PlutoDriveFileStatus.Healthy, item.Status);
        Assert.True(item.SizeBytes > 0);
        Assert.Null(item.ErrorDetail);

        // 손상 → CorruptOrEmpty
        File.WriteAllText(Path.Combine(plutoDir, PlutoExperimentLedgerService.FileName), "{\"Experiments\":[");
        var bad = new PlutoDriveHealthService().CheckHealth(plutoDir, studio).Items.Single(i => i.FileName == PlutoExperimentLedgerService.FileName);
        Assert.Equal(PlutoDriveFileStatus.CorruptOrEmpty, bad.Status);
    }
}
