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

    [Fact]
    public async Task StockSuiteExporter_SeriesBatch_CreatesEpisodeSubfoldersAndSeriesManifest()
    {
        var photos = new List<string>(12);
        for (var i = 0; i < 12; i++) photos.Add(MakeScene("s" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".png", 2000 + i * 41));
        var series = await new PlutoSeriesBatchPlanner(new PlutoPhotoCurationEngine()).BuildSeriesAsync(photos, "첫 생일: 여행/기록", 3);
        Assert.Equal(3, series.Episodes.Count);

        var videos = new string[series.Episodes.Count];
        for (var i = 0; i < videos.Length; i++)
        {
            videos[i] = Path.Combine(_dir, "montage_ep" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp4");
            File.WriteAllBytes(videos[i], ExportMediaIntegrityGuard.BuildMinimalMp4(1024 + i));
        }
        var root = Path.Combine(_dir, "stocksuite");
        Directory.CreateDirectory(root);
        var exporter = new StockSuiteExporter(() => root) { LocalNow = () => new DateTime(2026, 11, 15, 10, 0, 0) };

        var r = await exporter.ExportSeriesBatchToStockSuiteAsync(root, series, videos);

        Assert.True(r.Ok, r.Message);
        var seriesDir = Path.Combine(root, StockSuiteExporter.SanitizeProjectName(series.SeriesTheme) + "_20261115");
        Assert.Equal(seriesDir, r.TargetPath);
        Assert.DoesNotContain(':', Path.GetFileName(seriesDir));
        Assert.DoesNotContain('/', Path.GetFileName(seriesDir));
        for (var i = 0; i < 3; i++)
        {
            var ep = series.Episodes[i];
            var epDir = Path.Combine(seriesDir, "Ep0" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Path.GetFileName(epDir), StockSuiteExporter.EpisodeFolderName(ep.EpisodeNumber));
            var copied = Path.Combine(epDir, Path.GetFileName(videos[i]));
            Assert.True(File.Exists(copied));
            Assert.Equal(1024 + i, new FileInfo(copied).Length);
            Assert.Empty(Directory.GetFiles(epDir, "*.~pfs"));   // 임시 파일 잔존 0

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(epDir, StockSuiteExporter.ProjectManifestName)));
            var m = doc.RootElement;
            Assert.Equal(ep.EpisodeNumber, m.GetProperty("episodeNumber").GetInt32());
            Assert.Equal(3, m.GetProperty("totalEpisodes").GetInt32());
            Assert.Equal(ep.EpisodeTitle, m.GetProperty("projectName").GetString());
            var subs = m.GetProperty("subtitles");
            Assert.Equal(ep.Subtitles.Count, subs.GetArrayLength());
            Assert.Equal(ep.Subtitles[0].Text, subs[0].GetProperty("text").GetString());
            Assert.StartsWith(PlutoSeriesBatchPlanner.PartTag(i + 1, 3), subs[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            var sns = m.GetProperty("sns");
            Assert.Equal(ep.PublishingKit.Titles.Count, sns.GetProperty("titles").GetArrayLength());
            Assert.Equal(ep.PublishingKit.CaptionKo, sns.GetProperty("captionKo").GetString());
            Assert.Equal(Path.GetFileName(videos[i]), m.GetProperty("exports")[0].GetProperty("file").GetString());
        }

        using (var sdoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(seriesDir, StockSuiteExporter.SeriesManifestName))))
        {
            var s = sdoc.RootElement;
            Assert.Equal(series.SeriesTheme, s.GetProperty("seriesTheme").GetString());
            Assert.Equal(3, s.GetProperty("totalEpisodes").GetInt32());
            Assert.Equal(3, s.GetProperty("exportedEpisodes").GetInt32());
            Assert.Equal(series.TotalUsedUniquePhotos, s.GetProperty("totalUsedUniquePhotos").GetInt32());
            var eps = s.GetProperty("episodes");
            Assert.Equal(3, eps.GetArrayLength());
            Assert.Equal("Ep02", eps[1].GetProperty("folder").GetString());
            Assert.Equal("Ep02/" + StockSuiteExporter.ProjectManifestName, eps[1].GetProperty("manifest").GetString());
        }

        // [부분 구제] — 2편 미렌더 → 1 · 3편만 · 누락 고지
        var partialRoot = Path.Combine(_dir, "stocksuite_partial");
        Directory.CreateDirectory(partialRoot);
        var partial = await exporter.ExportSeriesBatchToStockSuiteAsync(partialRoot, series, new[] { videos[0], "", videos[2] });
        Assert.True(partial.Ok);
        Assert.Contains("[부분 구제]", partial.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(partial.TargetPath!, "Ep02")));
        Assert.True(Directory.Exists(Path.Combine(partial.TargetPath!, "Ep03")));

        // 렌더 0편 · 루트 부재 → 실패(예외 없음)
        Assert.False((await exporter.ExportSeriesBatchToStockSuiteAsync(root, series, Array.Empty<string>())).Ok);
        var missing = await exporter.ExportSeriesBatchToStockSuiteAsync(Path.Combine(_dir, "nope"), series, videos);
        Assert.False(missing.Ok);
        Assert.True(missing.NeedsPathSetup);
    }

    [Fact]
    public void Workspace_MountsWithAllPluto44Panels_WithoutExceptions()
    {
        // ① 컴포넌트 인스턴스화(필드 초기화 · 생성자) 예외 0
        var type = typeof(StockSuiteExporter).Assembly.GetType("PromptFlowStudio.Blazor.Pages.Workspace", throwOnError: true)!;
        Assert.True(typeof(Microsoft.AspNetCore.Components.ComponentBase).IsAssignableFrom(type));
        var ws = Activator.CreateInstance(type)!;
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        // ② Pluto 4.4/4.4.1 핸들러 결선
        foreach (var name in new[] { "BuildSeriesAsync", "LoadSeriesEpisodeAsync", "ExportSeriesSequentiallyAsync", "ExportSeriesBatchAsync", "RecordSeriesEpisodeOutput",
                                     "BuildAbExperimentAsync", "SubmitPerformanceAsync", "TogglePerfPanel", "CapturePerfContext" })
            Assert.NotNull(type.GetMethod(name, F));

        // ③ 렌더 완료 스냅샷 → 성과 피드백 입력 자동 채움(서비스 미주입 상태에서도 예외 0)
        type.GetMethod("CapturePerfContext", F)!.Invoke(ws, new object?[] { Path.Combine(_dir, "ws_20261115_ambient15s.mp4"), "15초 앰비언트 루프", "느린 돌리 훅", "engine-native" });
        string Field(string n) => (string)type.GetField(n, F)!.GetValue(ws)!;
        Assert.Equal("ws_20261115_ambient15s", Field("_perfVideoId"));
        Assert.Equal("느린 돌리 훅", Field("_perfHookStyle"));
        Assert.Equal("engine-native", Field("_perfLookId"));
        Assert.False(string.IsNullOrWhiteSpace(Field("_perfBgmMood")));
        Assert.Equal("15초 앰비언트 루프", Field("_perfContextSource"));
        type.GetMethod("TogglePerfPanel", F)!.Invoke(ws, null);
        Assert.True((bool)type.GetField("_perfOpen", F)!.GetValue(ws)!);
        Assert.Equal("ws_20261115_ambient15s", Field("_perfVideoId"));   // 토글이 스냅샷을 덮어쓰지 않음

        // ④ 마크업 — 4.4 패널 · 4.4.1 결선점 존재 및 피드백 패널이 단일 영상에서도 열림
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var razor = File.ReadAllText(Path.Combine(dir!.FullName, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        foreach (var marker in new[] { "data-series-run=\"1\"", "data-series-panel=", "data-series-export=\"1\"", "data-series-batch-export=\"1\"",
                                       "data-ab-build=\"1\"", "data-ab-panel=", "data-perf-panel=\"1\"", "data-perf-toggle-solo=\"1\"",
                                       "data-perf-hookstyle=\"1\"", "data-perf-look=\"1\"", "data-perf-bgm=\"1\"", "data-perf-tags=\"1\"", "data-perf-context=" })
            Assert.Contains(marker, razor, StringComparison.Ordinal);
        Assert.Contains("@if (_perfOpen && (_montagePhotos.Count >= 2 || _perfContextSource is not null))", razor, StringComparison.Ordinal);
        var panel = razor.IndexOf("data-series-panel=", StringComparison.Ordinal);
        var batch = razor.IndexOf("data-series-batch-export=", StringComparison.Ordinal);
        Assert.True(batch > panel && batch < razor.IndexOf("</div>", panel, StringComparison.Ordinal));   // 활성 시리즈 패널 내부
    }

    // ── Pluto 4.5 ──

    [Fact]
    public async Task ExperimentLedger_PendingExperiment_PersistsAcrossServiceReloadAndTransitionsToEvaluated()
    {
        var storage = Storage("pending");
        using (var first = new PlutoExperimentLedgerService(null, storage))
        {
            first.RegisterExperiment(Plan("exp-old"));
            first.RegisterExperiment(Plan("exp-new"));
            Assert.Equal(new[] { "exp-new", "exp-old" }, first.GetPendingExperiments().Select(p => p.ExperimentId).ToArray());   // 최신 등록 우선
            await first.WhenRegisteredPersistedAsync();
        }
        var file = Path.Combine(storage.ActiveRoot, PlutoExperimentLedgerService.FileName);
        Assert.True(File.Exists(file));
        Assert.Contains("\"Status\":\"" + PlutoExperimentLedgerService.StatusPending + "\"", File.ReadAllText(file), StringComparison.Ordinal);

        // 앱 재시작(새 인스턴스) → 대기 실험 복원 · 플랜 필드 보존
        using (var second = new PlutoExperimentLedgerService(null, storage))
        {
            Assert.Empty(second.GetPendingExperiments());   // 적재 전
            await second.EnsureLoadedAsync();
            var pending = second.GetPendingExperiments();
            Assert.Equal(2, pending.Count);
            var restored = pending.Single(p => p.ExperimentId == "exp-old");
            Assert.Equal(HookA, restored.ChampionA.HookStyle);
            Assert.Equal(HookB, restored.ChallengerB.HookStyle);
            Assert.Equal(PlutoColorGradingCompiler.GoldenHourWarmName, restored.ChallengerB.ColorLookId);
            Assert.Equal("돌잔치", restored.ThemeHint);
            Assert.Equal(PlutoVariantABGenerator.ChampionLabel, restored.ChampionA.VariantLabel);
            Assert.Single(restored.ChampionA.Subtitles);
            Assert.Equal(4.95, restored.ChampionA.Subtitles[0].EffectiveEndSec, 3);
            Assert.True(restored.IsValidExperiment);

            var r = await second.EvaluateExperimentAsync(restored, new PlutoExperimentMetricsInput(40, 30, 2), new PlutoExperimentMetricsInput(70, 60, 6));
            Assert.Equal(PlutoExperimentLedgerService.WinnerChallenger, r.WinnerLabel);
            Assert.False(r.AlreadyRegistered);
            Assert.Equal(new[] { "exp-new" }, second.GetPendingExperiments().Select(p => p.ExperimentId).ToArray());   // Pending → Evaluated
        }

        // 재시작 후에도 전이 유지 · 판정 완료 실험 재등록은 Pending 복귀 불가
        using (var third = new PlutoExperimentLedgerService(null, storage))
        {
            await third.EnsureLoadedAsync();
            Assert.Equal(new[] { "exp-new" }, third.GetPendingExperiments().Select(p => p.ExperimentId).ToArray());
            Assert.Single(third.GetRecentEvaluations());
            third.RegisterExperiment(Plan("exp-old"));
            await third.WhenRegisteredPersistedAsync();
            Assert.Equal(new[] { "exp-new" }, third.GetPendingExperiments().Select(p => p.ExperimentId).ToArray());
        }
        var json = File.ReadAllText(file);
        Assert.Contains("\"Status\":\"" + PlutoExperimentLedgerService.StatusEvaluated + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Status\":\"" + PlutoExperimentLedgerService.StatusPending + "\"", json, StringComparison.Ordinal);

        // 새 인스턴스가 적재 전에 등록해도 디스크의 기존 실험을 덮어쓰지 않음(병합 후 기록)
        using (var fourth = new PlutoExperimentLedgerService(null, storage))
        {
            fourth.RegisterExperiment(Plan("exp-third"));
            await fourth.WhenRegisteredPersistedAsync();
        }
        using var fifth = new PlutoExperimentLedgerService(null, storage);
        await fifth.EnsureLoadedAsync();
        Assert.Equal(new[] { "exp-third", "exp-new" }, fifth.GetPendingExperiments().Select(p => p.ExperimentId).ToArray());
        Assert.Single(fifth.GetRecentEvaluations());
    }

    [Fact]
    public void ExportMediaIntegrityGuard_RejectsTruncatedOrInvalidMp4_AndAcceptsValidIsoBmffHeader()
    {
        var valid = Path.Combine(_dir, "ok.mp4");
        File.WriteAllBytes(valid, ExportMediaIntegrityGuard.BuildMinimalMp4(4096));
        ExportMediaIntegrityGuard.EnsureValidMp4(valid);   // 예외 없음
        Assert.True(ExportMediaIntegrityGuard.TryValidateMp4(valid, out var none));
        Assert.Equal("", none);

        // mdat 뒤 moov(비 faststart) + 64비트 largesize 박스 → 허용
        var tail = Path.Combine(_dir, "moov_tail.mp4");
        var bytes = new byte[2048];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, 16);
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 1);
        "mdat"u8.CopyTo(bytes.AsSpan(20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), 2000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(2016), 32);
        "moov"u8.CopyTo(bytes.AsSpan(2020));
        File.WriteAllBytes(tail, bytes);
        ExportMediaIntegrityGuard.EnsureValidMp4(tail);

        void Rejects(string name, byte[] data, string expected)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, data);
            var ex = Assert.Throws<InvalidOperationException>(() => ExportMediaIntegrityGuard.EnsureValidMp4(path));
            Assert.Contains("무결성", ex.Message, StringComparison.Ordinal);
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
            Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        }

        Rejects("tiny.mp4", ExportMediaIntegrityGuard.BuildMinimalMp4(40), "너무 작습니다");                              // < 1024 B
        Rejects("zeros.mp4", new byte[2048], "ftyp");                                                                       // 헤더 없음
        var noMoov = ExportMediaIntegrityGuard.BuildMinimalMp4(2048);
        "free"u8.CopyTo(noMoov.AsSpan(20));                                                                                 // moov → free
        Rejects("no_moov.mp4", noMoov, "moov");
        var truncated = ExportMediaIntegrityGuard.BuildMinimalMp4(2048);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(truncated.AsSpan(24), 50_000);                          // 마지막 박스가 EOF 초과
        Rejects("truncated.mp4", truncated, "잘린");
        var badSize = ExportMediaIntegrityGuard.BuildMinimalMp4(2048);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(badSize.AsSpan(16), 4);                                // 크기 < 헤더
        Rejects("bad_size.mp4", badSize, "손상");

        var missing = Assert.Throws<InvalidOperationException>(() => ExportMediaIntegrityGuard.EnsureValidMp4(Path.Combine(_dir, "nope.mp4")));
        Assert.Contains("찾을 수 없습니다", missing.Message, StringComparison.Ordinal);
        Assert.True(ExportMediaIntegrityGuard.IsIsoBmffPath("a.MP4"));
        Assert.True(ExportMediaIntegrityGuard.IsIsoBmffPath("a.mov"));
        Assert.False(ExportMediaIntegrityGuard.IsIsoBmffPath("a.webm"));
    }

    [Fact]
    public async Task StockSuiteExporter_CorruptVideoFile_BlockedBeforeProjectFolderCopy()
    {
        // ① 단일 프로젝트 — 잘린 MP4 → 프로젝트 폴더 미생성
        var root = Path.Combine(_dir, "ss_root");
        Directory.CreateDirectory(root);
        var corrupt = Path.Combine(_dir, "broken_render.mp4");
        var bytes = ExportMediaIntegrityGuard.BuildMinimalMp4(4096);
        File.WriteAllBytes(corrupt, bytes.AsSpan(0, 20).ToArray().Concat(new byte[2000]).ToArray());   // ftyp 뒤 0 패딩 → moov 없음/박스 손상
        var exp = new StockSuiteExporter(() => root) { LocalNow = () => new DateTime(2026, 11, 16, 9, 0, 0) };

        var r = await exp.ExportWithSubtitlesAsync(corrupt, null, "Broken Project");

        Assert.False(r.Ok);
        Assert.False(r.NeedsPathSetup);
        Assert.Contains("무결성", r.Message, StringComparison.Ordinal);
        Assert.Null(r.TargetPath);
        Assert.False(Directory.Exists(Path.Combine(root, "Broken_Project_20261116")));
        Assert.Empty(Directory.GetFileSystemEntries(root));

        // ② 시리즈 — 손상 편만 차단 · 나머지 [부분 구제]
        var photos = new List<string>(12);
        for (var i = 0; i < 12; i++) photos.Add(MakeScene("c" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".png", 3000 + i * 43));
        var series = await new PlutoSeriesBatchPlanner(new PlutoPhotoCurationEngine()).BuildSeriesAsync(photos, "무결성 시리즈", 3);
        Assert.Equal(3, series.Episodes.Count);
        var videos = new string[3];
        for (var i = 0; i < 3; i++)
        {
            videos[i] = Path.Combine(_dir, "ep" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp4");
            File.WriteAllBytes(videos[i], ExportMediaIntegrityGuard.BuildMinimalMp4(2048));
        }
        File.WriteAllBytes(videos[1], new byte[300]);   // 2편 잘림

        var sr = await exp.ExportSeriesBatchToStockSuiteAsync(root, series, videos);
        Assert.True(sr.Ok, sr.Message);
        Assert.Contains("[부분 구제]", sr.Message, StringComparison.Ordinal);
        Assert.Contains("손상 1편", sr.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sr.TargetPath!, "Ep01", "ep1.mp4")));
        Assert.False(Directory.Exists(Path.Combine(sr.TargetPath!, "Ep02")));
        Assert.True(File.Exists(Path.Combine(sr.TargetPath!, "Ep03", "ep3.mp4")));

        // ③ 전 편 손상 → 실패 · 빈 시리즈 폴더 미잔존
        var allRoot = Path.Combine(_dir, "ss_all_bad");
        Directory.CreateDirectory(allRoot);
        var bad = new[] { videos[1], videos[1], videos[1] };
        var all = await exp.ExportSeriesBatchToStockSuiteAsync(allRoot, series, bad);
        Assert.False(all.Ok);
        Assert.Contains("손상", all.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(allRoot));
    }
}
