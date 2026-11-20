using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>📊 Step 22 — YouTube Studio CSV/ZIP 수집 · Shorts 정규화 · 카탈로그 연결 · 성숙도 증분 학습.</summary>
public sealed class PlutoYouTubeAnalyticsIngestionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_yt_ingest_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    public PlutoYouTubeAnalyticsIngestionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string KoHeader = "동영상,동영상 제목,동영상 게시 시간,조회수,평균 조회율(%),노출수,노출 클릭률(%),스치듯 넘기지 않고 시청함(%),좋아요";
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    private static PlutoRenderedClipRecord Clip(string id, string title, DateTimeOffset renderedAt, string hook = "훅")
        => new(id, title, "Ambient15s", hook, PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "tag-" + id }, @"C:\out\" + id + ".mp4", renderedAt, false);

    private static byte[] Utf8Bom(string text) => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                var b = Utf8Bom(text);
                s.Write(b, 0, b.Length);
            }
        return ms.ToArray();
    }

    private PlutoDriveStorageManager Storage(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return new PlutoDriveStorageManager(null, d);
    }

    /// <summary>폐루프 대역 — 최초 반영은 (플랫폼, 영상) 장부 · 증분은 (영상, 단계) 장부로 멱등.</summary>
    private sealed class CountingLoop : IPlutoClosedLoopReinforcementService
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PlutoVideoPerformanceTelemetry> Ingested = new();
        public readonly List<(string VideoId, int Stage, double Weight, double Completion)> Deltas = new();
        public PlutoReinforcementOutcome? LastOutcome => null;
        public event Action? Changed { add { } remove { } }

        public Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct = default)
        {
            if (!_keys.Add(t.Platform + "|" + t.VideoId)) return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "dup") { AlreadyRegistered = true });
            Ingested.Add(t);
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "ok"));
        }

        public Task<PlutoReinforcementOutcome> IngestMaturityDeltaAsync(PlutoVideoPerformanceTelemetry matured, PlutoVideoPerformanceTelemetry previous, int stage, double weight, CancellationToken ct = default)
        {
            if (!_keys.Add("mat|" + matured.VideoId + "|" + stage)) return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "dup") { AlreadyRegistered = true });
            Deltas.Add((matured.VideoId, stage, weight, matured.AverageCompletionPct));
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "delta"));
        }

        public Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "noop"));
    }

    // ── ① 한/영 CSV · ZIP 파싱 · Shorts 정규화 ──

    [Fact]
    public void Normalizer_ParsesKoreanAndEnglishCsvAndZip_MapsStayedToHook_SaturatesLooping_FiltersNoise()
    {
        var ko = KoHeader + "\n"
            + "합계,,,\"12,345\",80.1,\"50,000\",4.1,70.0,900\n"
            + "aaaaaaaaaa1,\"봄 소풍, 2/3편 점심\",2026. 11. 3.,\"1,234\",115.0%,\"20,000\",6.5%,72.5%,120\n"
            + "bbbbbbbbbb2,고양이 낮잠,2026. 11. 4.,\"2,000\",165.0,\"10,000\",3.0,61.0,80\n"
            + "ccccccccccc,노이즈 영상,2026. 11. 5.,5,40.0,100,1.0,30.0,0\n";
        var en = EnHeader + "\n"
            + "Total,,,3000,50,9000,3.3,60,10\n"
            + "aaaaaaaaaa1,\"Spring picnic 2/3\",\"Nov 3, 2026\",\"1,234\",115.0,20000,6.5,72.5,120\n"
            + "ddddddddddd,One view clip,\"Nov 5, 2026\",1,100,10,0,100,0\n";

        var rk = YouTubeStudioAnalyticsNormalizer.Parse(Utf8Bom(ko), "표 데이터.csv");
        Assert.True(rk.HeaderRecognized);
        Assert.Equal("ko", rk.Language);
        Assert.Equal(2, rk.Rows.Count);   // 합계 제외 · 노이즈 1 제외
        Assert.Equal(1, rk.SkippedNoiseCount);
        var a = rk.Rows[0];
        Assert.Equal("aaaaaaaaaa1", a.YouTubeVideoId);
        Assert.Equal("봄 소풍, 2/3편 점심", a.Title);
        Assert.Equal(1234, a.Views);
        Assert.Equal(20000, a.Impressions);
        Assert.Equal(120, a.Likes);
        Assert.Equal(72.5, a.Hook3sRetention, 6);   // 스치듯 넘기지 않고 시청함 → 3초 훅
        Assert.False(a.HookEstimated);
        Assert.Equal(6.5, a.Ctr, 6);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), a.PublishedAtUtc);
        // 반복 재생 포화 — 115 % < 165 % 우열 유지 · 둘 다 100 미만 · 무릎점 이하는 항등
        var b = rk.Rows[1];
        Assert.Equal(115.0, a.RawAveragePercentViewed, 6);
        Assert.InRange(a.CompletionRate, YouTubeStudioAnalyticsNormalizer.CompletionKneePct, 100.0 - 1e-9);
        Assert.True(b.CompletionRate > a.CompletionRate && b.CompletionRate < 100.0);
        Assert.Equal(55.0, YouTubeStudioAnalyticsNormalizer.SaturateCompletion(55.0), 9);
        double prev = 0;
        for (var p = 0.0; p <= 400.0; p += 5.0)
        {
            var v = YouTubeStudioAnalyticsNormalizer.SaturateCompletion(p);
            Assert.True(v >= prev && v < 100.0);   // 단조 · 상한 미도달
            prev = v;
        }

        var re = YouTubeStudioAnalyticsNormalizer.Parse(Encoding.UTF8.GetBytes(en), "Table data.csv");
        Assert.Equal("en", re.Language);
        var e = Assert.Single(re.Rows);
        Assert.Equal(1, re.SkippedNoiseCount);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), e.PublishedAtUtc);
        Assert.Equal(a.CompletionRate, e.CompletionRate, 9);

        // 🗜️ ZIP(한/영 파일명) — 메모리 안에서 표 데이터 선택
        var zko = YouTubeStudioAnalyticsNormalizer.Parse(Zip(("표 데이터.csv", ko)), "export.zip");
        Assert.Equal("표 데이터.csv", zko.SourceEntryName);
        Assert.Equal(2, zko.Rows.Count);
        var zen = YouTubeStudioAnalyticsNormalizer.Parse(Zip(("Table data.csv", en)), "export.zip");
        Assert.Equal("aaaaaaaaaa1", Assert.Single(zen.Rows).YouTubeVideoId);

        // Stayed 열 없음 → 완주율로 보수 추정(hook 추정 플래그)
        var noStayed = YouTubeStudioAnalyticsNormalizer.ParseCsv("Content,Video title,Video publish time,Views,Average percentage viewed (%),Impressions click-through rate (%)\nxxxxxxxxxx1,t,\"Nov 3, 2026\",500,40,5\n");
        var ns = Assert.Single(noStayed.Rows);
        Assert.True(ns.HookEstimated);
        Assert.Equal(PlutoTelemetryBatchImporter.EstimateHookFromCompletion(40), ns.Hook3sRetention, 9);
    }

    // ── ② 퍼지 카탈로그 연결 — 제목 + 편 + 게시 창 · 연결 영속 · 오탐 회피 ──

    [Fact]
    public async Task Linker_MatchesByTitleEpisodeAndPublishWindow_PersistsBinding_AndRejectsUnrelatedOrStale()
    {
        var storage = Storage("link");
        using (var cat = new PlutoRenderCatalogService(storage))
        {
            await cat.RecordRenderedClipAsync(Clip("picnic_ep1", "봄 소풍 1/3편 출발", T0));
            await cat.RecordRenderedClipAsync(Clip("picnic_ep2", "봄 소풍 2/3편 점심", T0.AddHours(1)));
            await cat.RecordRenderedClipAsync(Clip("cat_nap", "고양이 낮잠 브이로그", T0));
            var linker = new PlutoYouTubeCatalogLinker(cat);
            var rows = new[]
            {
                new YouTubeAnalyticsRow("PicnicEp2XX", "봄 소풍 2/3편 점심 #shorts", T0.AddDays(2), 900, 60, false, 50, 50, 5, false),
                new YouTubeAnalyticsRow("CookingXXXX", "완전히 다른 요리 레시피", T0.AddDays(1), 900, 60, false, 50, 50, 5, false),
                new YouTubeAnalyticsRow("CatNapLateX", "고양이 낮잠 브이로그 #shorts", T0.AddDays(20), 900, 60, false, 50, 50, 5, false),   // 게시 창(7일) 밖
            };
            var res = await linker.LinkAsync(rows);
            Assert.Equal("picnic_ep2", res[0].Clip?.VideoId);   // 1/3편이 아니라 2/3편
            Assert.Equal(PlutoYouTubeLinkTier.Fuzzy, res[0].Tier);
            Assert.True(res[0].NewlyBound);
            Assert.False(res[1].IsMatched);
            Assert.False(res[2].IsMatched);
            Assert.NotNull(res[2].Reason);
        }
        Assert.Equal(0.0, PlutoYouTubeCatalogLinker.TitleSimilarity("봄 소풍 1/3편 출발", "봄 소풍 2/3편 출발"));   // 편 충돌 → 0
        Assert.Equal(2, PlutoYouTubeCatalogLinker.ExtractEpisode("봄 소풍 2/3편"));

        // ♻️ 재시작 후 Tier 1(O(1)) 재매칭 — 제목이 바뀌어도 ID로 연결
        using var reloaded = new PlutoRenderCatalogService(storage);
        Assert.Equal("PicnicEp2XX", (await reloaded.GetRecentClipsAsync(10)).Single(c => c.VideoId == "picnic_ep2").YouTubeVideoId);
        Assert.Null((await reloaded.GetRecentClipsAsync(10)).Single(c => c.VideoId == "cat_nap").YouTubeVideoId);
        var again = await new PlutoYouTubeCatalogLinker(reloaded).LinkAsync(new[]
        {
            new YouTubeAnalyticsRow("PicnicEp2XX", "제목 수정됨", T0.AddDays(2), 2000, 60, false, 50, 50, 5, false),
        });
        Assert.Equal(PlutoYouTubeLinkTier.ExactBinding, again[0].Tier);
        Assert.Equal("picnic_ep2", again[0].Clip?.VideoId);
        Assert.False(again[0].NewlyBound);
    }

    // ── ③ 같은 스냅샷 재반영 멱등 · 성숙 스냅샷 체감 증분 ──

    [Fact]
    public async Task Importer_IdenticalReimportIsIdempotent_MaturedSnapshotsApplyBoundedDiminishingDeltas()
    {
        var storage = Storage("maturity");
        using var cat = new PlutoRenderCatalogService(storage);
        await cat.RecordRenderedClipAsync(Clip("walk_clip", "가을 산책 브이로그", T0));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(cat, loop);
        static byte[] Snap(long views, double avg) => Encoding.UTF8.GetBytes(EnHeader + "\nWalkClipYT1,가을 산책 브이로그 #shorts,\"Nov 2, 2026\","
            + views.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + avg.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",5000,5.0,66.0,10\n");

        var r1 = await importer.ImportYouTubeStudioFileAsync(Snap(300, 48), "day1.csv");
        Assert.Equal(1, r1.MatchedAndLearnedCount);
        Assert.Equal(1, r1.NewlyLinkedCount);
        Assert.True(r1.IsYouTubeStudioReport);
        Assert.Single(loop.Ingested);

        var r2 = await importer.ImportYouTubeStudioFileAsync(Snap(300, 48), "day1.csv");   // 같은 스냅샷
        Assert.Equal(0, r2.MatchedAndLearnedCount);
        Assert.Equal(0, r2.MaturityDeltaCount);
        Assert.Equal(1, r2.AlreadyRegisteredCount);
        Assert.Single(loop.Ingested);
        Assert.Empty(loop.Deltas);

        var r3 = await importer.ImportYouTubeStudioFileAsync(Snap(450, 52), "day2.csv");   // +150 · ×1.5 → 미성숙
        Assert.Equal(0, r3.MaturityDeltaCount);
        Assert.Empty(loop.Deltas);

        var r4 = await importer.ImportYouTubeStudioFileAsync(Snap(900, 58), "day7.csv");   // ×3 · +600 → 1단계
        Assert.Equal(1, r4.MaturityDeltaCount);
        var d1 = Assert.Single(loop.Deltas);
        Assert.Equal((1, PlutoClosedLoopReinforcementService.MaturityBaseWeight), (d1.Stage, d1.Weight));
        Assert.Equal(58.0, d1.Completion, 6);

        var r5 = await importer.ImportYouTubeStudioFileAsync(Snap(900, 58), "day7.csv");   // 성숙 스냅샷 재반영 → 0
        Assert.Equal(0, r5.MaturityDeltaCount);
        Assert.Single(loop.Deltas);

        await importer.ImportYouTubeStudioFileAsync(Snap(2000, 60), "day30.csv");   // 2단계 — 가중치 절반
        Assert.Equal(2, loop.Deltas.Count);
        Assert.Equal(2, loop.Deltas[1].Stage);
        Assert.True(loop.Deltas[1].Weight < loop.Deltas[0].Weight);
        Assert.Equal(PlutoClosedLoopReinforcementService.MaturityBaseWeight / 2, loop.Deltas[1].Weight, 9);

        var wm = (await cat.GetRecentClipsAsync(1))[0].YouTubeWatermark;
        Assert.Equal((2000L, 2), (wm!.Views, wm.Stage));
        Assert.Single(loop.Ingested);   // 누적 전체 보상 재반영 없음
    }

    [Fact]
    public async Task ClosedLoop_MaturityDelta_IsBoundedByStageWeight_AndIdempotentPerStage()
    {
        var storage = Storage("loop");
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var nb = new PlutoMistakeNotebookService(storage);
        using var loop = new PlutoClosedLoopReinforcementService(learning, nb, storage);
        PlutoVideoPerformanceTelemetry T(double hook, double comp, double ctr) => new("clip_m", "YouTube Shorts", "훅", "look", "bgm", new[] { "t" }, hook, comp, ctr, T0);

        var prev = T(50, 40, 3);    // 평균
        var matured = T(80, 70, 9);   // 돌파
        var o1 = await loop.IngestMaturityDeltaAsync(matured, prev, 1, PlutoClosedLoopReinforcementService.MaturityWeightForStage(1));
        Assert.True(o1.IsBreakoutSuccess);
        var fullBoost = PlutoClosedLoopReinforcementService.BoostFor(PlutoClosedLoopReinforcementService.Evaluate(matured).Strength);
        Assert.InRange(o1.PatternWeightMultiplier, 1.0 + 1e-9, 1.0 + (fullBoost - 1.0) * PlutoClosedLoopReinforcementService.MaturityBaseWeight + 1e-9);   // 전체 보상의 절반 이하
        Assert.True((await loop.IngestMaturityDeltaAsync(matured, prev, 1, 0.5)).AlreadyRegistered);   // 같은 단계 재반영 차단

        var o4 = await loop.IngestMaturityDeltaAsync(matured, prev, PlutoClosedLoopReinforcementService.MaxMaturityStages + 1, 0.5);   // 단계 한도 초과
        Assert.Equal(1.0, o4.PatternWeightMultiplier);
        Assert.Equal(0.0, PlutoClosedLoopReinforcementService.MaturityWeightForStage(PlutoClosedLoopReinforcementService.MaxMaturityStages + 1));

        // 성숙하며 부진이 드러남 → 결손 증분 × 가중치 벌점(전체 벌점보다 작음)
        var poor = T(30, 20, 1);
        var o2 = await loop.IngestMaturityDeltaAsync(poor, prev, 2, PlutoClosedLoopReinforcementService.MaturityWeightForStage(2));
        Assert.True(o2.RecordedMistakePenalty);
        Assert.True(o2.PenaltySeverity < PlutoClosedLoopReinforcementService.MaxPenalty * PlutoClosedLoopReinforcementService.MaturityWeightForStage(2) * PlutoClosedLoopReinforcementService.NonHookDeficitGain + 1e-9);
    }

    // ── ④ 멀티 PC 병합이 YouTubeVideoId 연결을 보존 ──

    [Fact]
    public async Task ConflictMerge_PreservesYouTubeVideoIdBinding_AcrossPcCatalogs()
    {
        var pcA = Storage("pcA");
        var pcB = Storage("pcB");
        using (var a = new PlutoRenderCatalogService(pcA))
        {
            await a.RecordRenderedClipAsync(Clip("shared_clip", "공유 클립", T0));
            Assert.Equal(PlutoYouTubeBindResult.Bound, await a.TryBindYouTubeVideoIdAsync("shared_clip", "SharedClipY"));
            await a.RecordRenderedClipAsync(Clip("only_a", "A 전용", T0.AddMinutes(1)));
        }
        using (var b = new PlutoRenderCatalogService(pcB))
            await b.RecordRenderedClipAsync(Clip("shared_clip", "공유 클립(재렌더)", T0.AddHours(3)));   // 더 최근 렌더 · 미연결

        var ja = File.ReadAllText(pcA.ResolveDataPath(PlutoRenderCatalogService.FileName));
        var jb = File.ReadAllText(pcB.ResolveDataPath(PlutoRenderCatalogService.FileName));
        foreach (var (cur, other) in new[] { (ja, jb), (jb, ja) })   // 병합 방향 무관
        {
            var merged = PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, cur, other)!;
            var clips = JsonNode.Parse(merged)!["Clips"]!.AsArray().Select(n => n!.AsObject()).ToList();
            var shared = clips.Single(c => (string?)c["VideoId"] == "shared_clip");
            Assert.Equal("SharedClipY", (string?)shared["YouTubeVideoId"]);
            Assert.Equal("공유 클립(재렌더)", (string?)shared["Title"]);   // 최신 렌더 메타 유지
            Assert.Contains(clips, c => (string?)c["VideoId"] == "only_a");
        }

        var pcC = Storage("pcC");
        File.WriteAllText(pcC.ResolveDataPath(PlutoRenderCatalogService.FileName), PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, jb, ja)!);
        using var c2 = new PlutoRenderCatalogService(pcC);
        Assert.Equal("shared_clip", (await c2.FindByYouTubeVideoIdAsync("SharedClipY"))?.VideoId);
    }

    // ── ⑤-A 자체 추가: 멀티 PC 성숙도 워터마크 병합 — 다른 PC가 이미 반영한 증분 재적용 차단 ──

    [Fact]
    public async Task CoArch_WatermarkMergeKeepsFurthestAhead_SoSecondPcNeverReappliesMaturityDelta()
    {
        var pcA = Storage("wmA");
        var pcB = Storage("wmB");
        using (var a = new PlutoRenderCatalogService(pcA))
        {
            await a.RecordRenderedClipAsync(Clip("wm_clip", "워터마크 클립", T0));
            await a.TryBindYouTubeVideoIdAsync("wm_clip", "WatermarkYT");
            Assert.True(await a.UpdateYouTubeWatermarkAsync("wm_clip", new PlutoYouTubeIngestWatermark(900, 1, 70, 58, 6, T0.AddDays(7))));
            Assert.False(await a.UpdateYouTubeWatermarkAsync("wm_clip", new PlutoYouTubeIngestWatermark(300, 0, 60, 48, 5, T0.AddDays(8))));   // 되감기 거부
        }
        using (var b = new PlutoRenderCatalogService(pcB))
        {
            await b.RecordRenderedClipAsync(Clip("wm_clip", "워터마크 클립", T0.AddHours(2)));   // 더 최근 렌더 메타
            await b.TryBindYouTubeVideoIdAsync("wm_clip", "WatermarkYT");
            await b.UpdateYouTubeWatermarkAsync("wm_clip", new PlutoYouTubeIngestWatermark(300, 0, 60, 48, 5, T0.AddDays(1)));
        }
        var merged = PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName,
            File.ReadAllText(pcB.ResolveDataPath(PlutoRenderCatalogService.FileName)), File.ReadAllText(pcA.ResolveDataPath(PlutoRenderCatalogService.FileName)))!;
        File.WriteAllText(pcB.ResolveDataPath(PlutoRenderCatalogService.FileName), merged);

        using var b2 = new PlutoRenderCatalogService(pcB);
        var wm = (await b2.FindByYouTubeVideoIdAsync("WatermarkYT"))!.YouTubeWatermark!;
        Assert.Equal((900L, 1), (wm.Views, wm.Stage));   // PC A 의 앞선 워터마크

        // PC B 가 PC A 와 같은 7일 스냅샷을 다시 가져와도 증분 0
        var loop = new CountingLoop();
        var r = await new PlutoTelemetryBatchImporter(b2, loop).ImportYouTubeStudioFileAsync(Encoding.UTF8.GetBytes(EnHeader + "\nWatermarkYT,워터마크 클립,\"Nov 2, 2026\",900,58,5000,6,70,10\n"), "day7.csv");
        Assert.Equal(0, r.MaturityDeltaCount);
        Assert.Equal(1, r.AlreadyRegisteredCount);
        Assert.Empty(loop.Deltas);
        Assert.Empty(loop.Ingested);
    }

    // ── ⑤-B 자체 추가: 1:1 연결 가드 · 모호 후보 거부 · 병합 후 중복 연결 해소 ──

    [Fact]
    public async Task CoArch_OneToOneBindingGuard_RejectsAmbiguousCandidates_AndMergeDedupesDuplicateBindings()
    {
        var storage = Storage("guard");
        using var cat = new PlutoRenderCatalogService(storage);
        await cat.RecordRenderedClipAsync(Clip("dup_a", "아기 첫 걸음마 영상", T0));
        await cat.RecordRenderedClipAsync(Clip("dup_b", "아기 첫 걸음마 영상!", T0));   // 재렌더 사본 — 같은 제목
        await cat.RecordRenderedClipAsync(Clip("solo", "강아지 산책", T0));

        // 모호 → 추측 금지
        var res = await new PlutoYouTubeCatalogLinker(cat).LinkAsync(new[] { new YouTubeAnalyticsRow("BabyStepsYT", "아기 첫 걸음마 영상 #shorts", T0.AddDays(1), 500, 60, false, 50, 50, 5, false) });
        Assert.False(res[0].IsMatched);
        Assert.Contains("모호", res[0].Reason);
        Assert.Null((await cat.FindByYouTubeVideoIdAsync("BabyStepsYT")));

        // 1:1 — 같은 ID 를 다른 클립에 · 이미 연결된 클립을 다른 ID 로 덮어쓰기 모두 거부
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("solo", "DogWalkYT01"));
        Assert.Equal(PlutoYouTubeBindResult.AlreadyBound, await cat.TryBindYouTubeVideoIdAsync("solo", "DogWalkYT01"));
        Assert.Equal(PlutoYouTubeBindResult.IdTakenByOtherClip, await cat.TryBindYouTubeVideoIdAsync("dup_a", "DogWalkYT01"));
        Assert.Equal(PlutoYouTubeBindResult.ClipBoundElsewhere, await cat.TryBindYouTubeVideoIdAsync("solo", "OtherIdYT01"));
        Assert.Equal(PlutoYouTubeBindResult.NotFound, await cat.TryBindYouTubeVideoIdAsync("solo", "bad id"));

        // 같은 내보내기 안에서 한 클립은 한 행에만
        var two = await new PlutoYouTubeCatalogLinker(cat).LinkAsync(new[]
        {
            new YouTubeAnalyticsRow("DogWalkYT01", "강아지 산책", T0.AddDays(1), 500, 60, false, 50, 50, 5, false),
            new YouTubeAnalyticsRow("DogWalkYT02", "강아지 산책", T0.AddDays(1), 500, 60, false, 50, 50, 5, false),
        });
        Assert.True(two[0].IsMatched);
        Assert.False(two[1].IsMatched);

        // 두 PC 가 같은 YouTube ID 를 서로 다른 클립에 연결 → 병합은 최신 렌더 쪽만 유지
        static string Cat(string clipId, string yt, DateTimeOffset at) => "{\"Version\":1,\"Clips\":[{\"VideoId\":\"" + clipId + "\",\"Title\":\"t\",\"RenderedAtUtc\":\""
            + at.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\",\"TelemetryLogged\":false,\"YouTubeVideoId\":\"" + yt + "\"}]}";
        var merged = JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, Cat("x_old", "SameIdYT001", T0), Cat("x_new", "SameIdYT001", T0.AddHours(1)))!)!["Clips"]!.AsArray();
        Assert.Equal("SameIdYT001", (string?)merged.Single(n => (string?)n!["VideoId"] == "x_new")!["YouTubeVideoId"]);
        Assert.Null(merged.Single(n => (string?)n!["VideoId"] == "x_old")!["YouTubeVideoId"]);
    }

    // ── ⑤-C 자체 추가: ZIP 내용 기반 표 선택(차트/합계 CSV 제외) · ZIP 폭탄 상한 · 붙여넣기 경로 라우팅 · CTR 결측 중립 ──

    [Fact]
    public async Task CoArch_ZipPicksTableByContent_RejectsChartAndTotals_AndPastedStudioCsvUsesNormalizerPath()
    {
        var table = EnHeader + "\nZipVideoYT1,Zip clip,\"Nov 2, 2026\",400,130,4000,5,70,3\n";
        // 차트 데이터(날짜별 행)를 이름만 'Table'로 위장해도 날짜 열로 걸러냄 · 합계 CSV 는 동영상 행 없음
        var chart = "Date,Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions click-through rate (%)\n"
            + "2026-11-02,ZipVideoYT1,Zip clip,\"Nov 2, 2026\",200,120,5\n2026-11-03,ZipVideoYT1,Zip clip,\"Nov 2, 2026\",200,140,5\n";
        var totals = EnHeader + "\nTotal,,,400,130,4000,5,70,3\n";
        var zip = Zip(("Table data (chart).csv", chart), ("Totals.csv", totals), ("Chart data.csv", chart), ("Table data.csv", table));
        var report = YouTubeStudioAnalyticsNormalizer.Parse(zip, "studio.zip");
        Assert.Equal("Table data.csv", report.SourceEntryName);
        Assert.Equal(400, Assert.Single(report.Rows).Views);

        // ZIP 폭탄 — 해제 상한 초과 항목은 건너뜀 → 표 미인식
        var huge = new StringBuilder(EnHeader).Append('\n');
        var line = "BombVideoY1,bomb,\"Nov 2, 2026\",400,50,4000,5,70,3\n";
        while (huge.Length < YouTubeStudioAnalyticsNormalizer.MaxZipEntryBytes + 1024) huge.Append(line);
        Assert.False(YouTubeStudioAnalyticsNormalizer.Parse(Zip(("Table data.csv", huge.ToString())), "bomb.zip").HeaderRecognized);

        // CTR 결측(Shorts 피드) → 중립값(벌점/돌파 없음)
        var noCtr = YouTubeStudioAnalyticsNormalizer.ParseCsv("Content,Video title,Video publish time,Views,Average percentage viewed (%),Stayed to watch (%)\nNoCtrVidY1,t,\"Nov 2, 2026\",500,45,60\n");
        var nr = Assert.Single(noCtr.Rows);
        Assert.True(nr.CtrEstimated);
        Assert.Equal(YouTubeStudioAnalyticsNormalizer.NeutralCtrPct, nr.Ctr);

        // 붙여넣기 경로 — 조회수 열이 있는 Studio 표는 정규화기 경로(100 % 초과를 클램프하지 않고 포화 · 노이즈 제외)
        var storage = Storage("paste");
        using var cat = new PlutoRenderCatalogService(storage);
        await cat.RecordRenderedClipAsync(Clip("zip_clip", "Zip clip", T0.AddDays(1)));
        var loop = new CountingLoop();
        var r = await new PlutoTelemetryBatchImporter(cat, loop).ImportCsvAsync(table + "NoiseVidYT1,noise,\"Nov 2, 2026\",3,90,10,1,10,0\n");
        Assert.True(r.IsYouTubeStudioReport);
        Assert.Equal(1, r.SkippedNoiseCount);
        Assert.Equal(1, r.MatchedAndLearnedCount);
        var t = Assert.Single(loop.Ingested);
        Assert.Equal(YouTubeStudioAnalyticsNormalizer.SaturateCompletion(130), t.AverageCompletionPct, 9);
        Assert.True(t.AverageCompletionPct < 100.0);
        Assert.Equal("ZipVideoYT1", (await cat.FindByVideoIdOrTitleAsync("zip_clip"))!.YouTubeVideoId);   // 정확 제목 → 연결 영속

        // 📂 다운로드 폴더 자동 탐색 — 최신 Studio 내보내기 · 받는 중 파일/무관 파일 제외
        var dl = Path.Combine(_dir, "Downloads");
        Directory.CreateDirectory(dl);
        File.WriteAllBytes(Path.Combine(dl, "YouTube Studio old.zip"), zip);
        File.SetLastWriteTimeUtc(Path.Combine(dl, "YouTube Studio old.zip"), DateTime.UtcNow.AddDays(-2));
        File.WriteAllBytes(Path.Combine(dl, "콘텐츠 2026-11-20.zip"), zip);
        File.WriteAllBytes(Path.Combine(dl, "YouTube Studio new.zip.crdownload"), zip);
        File.WriteAllBytes(Path.Combine(dl, "invoice.csv"), zip);
        File.SetLastWriteTimeUtc(Path.Combine(dl, "invoice.csv"), DateTime.UtcNow.AddDays(1));
        Assert.Equal("콘텐츠 2026-11-20.zip", Path.GetFileName(YouTubeStudioAnalyticsNormalizer.FindNewestExport(dl)));
        Assert.Null(YouTubeStudioAnalyticsNormalizer.FindNewestExport(Path.Combine(_dir, "missing")));
    }
}
