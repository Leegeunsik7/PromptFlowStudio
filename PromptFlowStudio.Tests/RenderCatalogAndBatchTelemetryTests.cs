using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎞️ 영속 렌더 카탈로그 · 📥 CSV/다건 성과 일괄 반영.</summary>
public sealed class RenderCatalogAndBatchTelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_render_catalog_" + Guid.NewGuid().ToString("N"));

    public RenderCatalogAndBatchTelemetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PlutoRenderedClipRecord Clip(string id, string title, string mode, string hook, string tag, int minutesAgo = 0)
        => new(id, title, mode, hook, PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { tag }, @"C:\out\" + id + ".mp4",
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), false);

    [Fact]
    public async Task RenderCatalog_PersistsSeriesEpisodesAcrossReload_AndMarksTelemetryLogged()
    {
        var storage = new PlutoDriveStorageManager(null, _dir);
        using (var cat = new PlutoRenderCatalogService(storage))
        {
            await cat.RecordRenderedClipAsync(Clip("series_ep1", "봄 소풍 1/3편 — 출발", "시리즈 1/3편", "출발 훅", "picnic", 3));
            await cat.RecordRenderedClipAsync(Clip("series_ep2", "봄 소풍 2/3편 — 점심", "시리즈 2/3편", "점심 훅", "lunch", 2));
            await cat.RecordRenderedClipAsync(Clip("series_ep3", "봄 소풍 3/3편 — 귀가", "시리즈 3/3편", "귀가 훅", "home", 1));
            await cat.RecordRenderedClipAsync(Clip("single_clip", "단일", "단일 영상", "prompt-hook", "solo"));
            await cat.MarkTelemetryLoggedAsync("SERIES_EP2");   // 대소문자 무시
            await cat.MarkTelemetryLoggedAsync("ghost");        // 미추적 → 무시
        }
        Assert.True(File.Exists(storage.ResolveDataPath(PlutoRenderCatalogService.FileName)));

        using var reloaded = new PlutoRenderCatalogService(storage);
        var clips = await reloaded.GetRecentClipsAsync();
        Assert.Equal(new[] { "single_clip", "series_ep3", "series_ep2", "series_ep1" }, clips.Select(c => c.VideoId).ToArray());   // 편별 보존 · 최신순
        var ep2 = Assert.Single(clips, c => c.VideoId == "series_ep2");
        Assert.True(ep2.TelemetryLogged);
        Assert.Equal("점심 훅", ep2.HookStyle);
        Assert.Equal("시리즈 2/3편", ep2.RenderMode);
        Assert.Equal(new[] { "lunch" }, ep2.Tags.ToArray());
        Assert.False(clips.Single(c => c.VideoId == "series_ep1").TelemetryLogged);

        // 🔎 VideoId · 정규화 제목 · 확장자 포함 파일명 조회
        Assert.Equal("series_ep3", (await reloaded.FindByVideoIdOrTitleAsync("봄 소풍 3/3편 - 귀가!"))?.VideoId);
        Assert.Equal("series_ep1", (await reloaded.FindByVideoIdOrTitleAsync("Series_Ep1.mp4"))?.VideoId);
        Assert.Null(await reloaded.FindByVideoIdOrTitleAsync("unknown"));
        Assert.Equal(2, (await reloaded.GetRecentClipsAsync(2)).Count);

        // ♻️ 같은 VideoId 재렌더 → 교체 후 맨 앞(중복 없음) · 상한 100
        await reloaded.RecordRenderedClipAsync(Clip("series_ep1", "봄 소풍 1/3편 — 출발(재렌더)", "시리즈 1/3편", "새 훅", "picnic"));
        var after = await reloaded.GetRecentClipsAsync(500);
        Assert.Equal("series_ep1", after[0].VideoId);
        Assert.Equal(4, after.Count);
        for (var i = 0; i < PlutoRenderCatalogService.Capacity + 10; i++)
            await reloaded.RecordRenderedClipAsync(Clip("bulk_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "b", "몽타주", "h", "t"));
        Assert.Equal(PlutoRenderCatalogService.Capacity, (await reloaded.GetRecentClipsAsync(500)).Count);
    }

    [Fact]
    public async Task TelemetryBatchImporter_ParsesCompactCsvAndYouTubeHeaders_AndUpdatesClosedLoop()
    {
        var storage = new PlutoDriveStorageManager(null, _dir);
        using var nb = new PlutoMistakeNotebookService(storage);
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, storage);
        using var cat = new PlutoRenderCatalogService(storage);
        await cat.RecordRenderedClipAsync(Clip("clip_a", "Clip A", "몽타주", "느린 인트로 훅", "slow-pan"));
        await cat.RecordRenderedClipAsync(Clip("clip_b", "Clip B", "모션 영상", "강한 훅", "punchy"));
        await cat.RecordRenderedClipAsync(Clip("ep_kr", "가을 산책, \"단풍\" 편", "시리즈 1/2편", "단풍 훅", "maple"));
        await cat.RecordRenderedClipAsync(Clip("ep_en", "Autumn Walk Part 2", "시리즈 2/2편", "walk hook", "walk"));
        var importer = new PlutoTelemetryBatchImporter(cat, loop);

        // 1) 압축 4열(헤더 포함 · 따옴표 필드)
        var compact = "VideoId,HookRetentionPct,CompletionPct,CtrPct\r\nclip_a,5,40,5\r\n\"clip_b\",70,60,8.1%\r\n";
        var r1 = await importer.ImportCsvAsync(compact);
        Assert.Equal(2, r1.TotalRows);
        Assert.Equal(2, r1.MatchedAndLearnedCount);
        Assert.Equal(0, r1.UnmatchedCount);
        Assert.Equal(1, r1.MistakeGuardsTriggered);   // clip_a 훅 결손 0.88 → 벌점 1.88 ≥ 1.8
        Assert.Contains("느린 인트로 훅", nb.GetSuppressedTokens());

        // 헤더 없는 압축 행도 인식(중복 반영 → 기반영)
        var r1b = await importer.ImportCsvAsync("clip_a,5,40,5");
        Assert.Equal(1, r1b.TotalRows);
        Assert.Equal(0, r1b.MatchedAndLearnedCount);
        Assert.Equal(1, r1b.AlreadyRegisteredCount);

        // 2) YouTube Studio 한국어 헤더(3초 열 없음 → 완주율 기반 보수 추정 · 합계 행 제외 · 제목 매칭 · 따옴표 내 콤마)
        var korean = "콘텐츠,동영상 제목,동영상 게시 시간,평균 조회율 (%),노출 클릭률 (%)\n합계,,,45.0,4.0\nyt_abc123,\"가을 산책, \"\"단풍\"\" 편\",2026. 11. 1.,48.5,6.2\n";
        var r2 = await importer.ImportCsvAsync(korean);
        Assert.Equal(1, r2.TotalRows);
        Assert.Equal(1, r2.MatchedAndLearnedCount);

        // 3) YouTube Studio 영어 헤더(TSV)
        var english = "Content\tVideo title\tAverage percentage viewed (%)\tImpressions click-through rate (%)\nyt_xyz\tAutumn Walk — Part 2\t12.0\t1.1\n";
        var r3 = await importer.ImportCsvAsync(english);
        Assert.Equal(1, r3.MatchedAndLearnedCount);
        Assert.True(nb.GetEffectivePenalty("walk hook") > 0);   // 부진 → 오답 노트 기록

        var cats = await cat.GetRecentClipsAsync();
        Assert.All(cats, c => Assert.True(c.TelemetryLogged));

        // 파서 단위 검증
        var h = PlutoTelemetryBatchImporter.TryMapHeader(new[] { "콘텐츠", "동영상 제목", "평균 조회율 (%)", "노출 클릭률 (%)" });
        Assert.NotNull(h);
        Assert.Equal((0, 1, -1, 2, 3), (h!.Value.Id, h.Value.Title, h.Value.Hook, h.Value.Completion, h.Value.Ctr));
        Assert.Equal(15.0, PlutoTelemetryBatchImporter.EstimateHookFromCompletion(3));
        Assert.Equal(85.0, PlutoTelemetryBatchImporter.EstimateHookFromCompletion(99));
        Assert.Equal(48.5, PlutoTelemetryBatchImporter.EstimateHookFromCompletion(48.5));
        Assert.False(r2.UnmatchedIdentifiers.Any());
    }

    [Fact]
    public async Task TelemetryBatchImporter_HandlesUnmatchedRowsGracefully_WithoutAbortingBatch()
    {
        var storage = new PlutoDriveStorageManager(null, _dir);
        using var nb = new PlutoMistakeNotebookService(storage);
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, storage);
        using var cat = new PlutoRenderCatalogService(storage);
        await cat.RecordRenderedClipAsync(Clip("known_1", "Known One", "몽타주", "hook-1", "t1"));
        await cat.RecordRenderedClipAsync(Clip("known_2", "Known Two", "몽타주", "hook-2", "t2"));
        var importer = new PlutoTelemetryBatchImporter(cat, loop);

        var csv = "VideoId,HookRetentionPct,CompletionPct,CtrPct\n"
            + "ghost_video,50,40,4\n"
            + "known_1,55,45,5\n"
            + "bad_numbers,abc,xyz,??\n"
            + "\n"
            + "known_2,60,50,6\n"
            + "another_ghost,10,10,1\n";
        var r = await importer.ImportCsvAsync(csv);

        Assert.Equal(5, r.TotalRows);
        Assert.Equal(2, r.MatchedAndLearnedCount);
        Assert.Equal(3, r.UnmatchedCount);
        Assert.Contains("ghost_video", r.UnmatchedIdentifiers);
        Assert.Contains("another_ghost", r.UnmatchedIdentifiers);
        Assert.Contains(r.UnmatchedIdentifiers, u => u.StartsWith("bad_numbers", StringComparison.Ordinal));
        Assert.Equal(0, r.MistakeGuardsTriggered);
        Assert.Contains("미매칭 3건", r.SummaryKorean);

        var clips = await cat.GetRecentClipsAsync();
        Assert.All(clips, c => Assert.True(c.TelemetryLogged));

        // 빈 입력 · 헤더만 → 0행(예외 없음)
        Assert.Equal(0, (await importer.ImportCsvAsync("")).TotalRows);
        Assert.Equal(0, (await importer.ImportCsvAsync("VideoId,HookRetentionPct,CompletionPct,CtrPct\n")).TotalRows);
    }

    [Fact]
    public async Task DriveStorageManager_CoreBackupVerification_IncludesRenderCatalogJson()
    {
        Assert.Contains(PlutoRenderCatalogService.FileName, PlutoDriveHealthService.PlutoFiles);
        foreach (var f in new[] { PlutoExperimentLedgerService.FileName, PlutoMistakeNotebookService.FileName, PlutoLearningService.FileName })
            Assert.Contains(f, PlutoDriveHealthService.PlutoFiles);

        // 🔁 새 PC 복원 — Drive 에만 있는 카탈로그 → 로컬 폴백으로 복사(기존 로컬 파일 덮어쓰기 없음)
        var vault = Path.Combine(_dir, "vault");
        var drive = Path.Combine(vault, "Pluto");
        var local = Path.Combine(_dir, "local");
        Directory.CreateDirectory(drive);
        File.WriteAllText(Path.Combine(drive, PlutoRenderCatalogService.FileName), "{\"Clips\":[]}");
        File.WriteAllText(Path.Combine(drive, PlutoLearningService.FileName), "{\"drive\":true}");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, PlutoLearningService.FileName), "{\"local\":true}");
        var storage = new PlutoDriveStorageManager(drive, local);
        Assert.True(storage.IsCloudSynced);
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.True(File.Exists(Path.Combine(local, PlutoRenderCatalogService.FileName)));
        Assert.Equal("{\"local\":true}", File.ReadAllText(Path.Combine(local, PlutoLearningService.FileName)));
        Assert.Equal(0, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));   // 멱등

        // 검증 리포트에 카탈로그 항목 포함(Healthy)
        var rep = new PlutoDriveHealthService(null, null, storage).CheckHealth(drive, Path.Combine(_dir, "nostudio"));
        var item = Assert.Single(rep.Items, i => i.FileName == PlutoRenderCatalogService.FileName);
        Assert.Equal(PlutoDriveHealthService.CategoryPluto, item.Category);
        Assert.Equal(PlutoDriveFileStatus.Healthy, item.Status);

        // Drive 비활성(PROMPTFLOW_PLUTO_DRIVE=off 등 primary null) → 복원 0 · 예외 없음
        var off = new PlutoDriveStorageManager(null, Path.Combine(_dir, "local_off"));
        Assert.Equal(0, off.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        var forced = await new PlutoDriveHealthService(null, null, off).ForceSyncAndVerifyAsync();
        Assert.Contains(forced.Items, i => i.FileName == PlutoRenderCatalogService.FileName);
    }

    [Fact]
    public async Task TelemetryBatchImporter_BuildUnlearnedCsvTemplate_IncludesOnlyUnlearnedClips()
    {
        var storage = new PlutoDriveStorageManager(null, _dir);
        using var nb = new PlutoMistakeNotebookService(storage);
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, storage);
        using var cat = new PlutoRenderCatalogService(storage);
        var importer = new PlutoTelemetryBatchImporter(cat, loop);

        Assert.Equal("VideoId,HookRetentionPct,CompletionPct,CtrPct\n", await importer.BuildUnlearnedCsvTemplateAsync());   // 빈 카탈로그 → 헤더만

        for (var i = 0; i < 20; i++)
            await cat.RecordRenderedClipAsync(Clip("u_" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), "t", "몽타주", "h", "x", 100 - i));
        await cat.MarkTelemetryLoggedAsync("u_19");
        await cat.MarkTelemetryLoggedAsync("u_05");

        var csv = await importer.BuildUnlearnedCsvTemplateAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("VideoId,HookRetentionPct,CompletionPct,CtrPct", lines[0]);
        Assert.Equal(16, lines.Length);   // 헤더 + 15
        Assert.Equal("u_18,42.0,30.0,1.5", lines[1]);   // 최신순 · 학습 완료(u_19) 제외
        Assert.DoesNotContain(lines, l => l.StartsWith("u_19,", StringComparison.Ordinal) || l.StartsWith("u_05,", StringComparison.Ordinal));
        Assert.All(lines.Skip(1), l => Assert.EndsWith(",42.0,30.0,1.5", l, StringComparison.Ordinal));

        Assert.Equal(4, (await importer.BuildUnlearnedCsvTemplateAsync(3)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // 전량 학습 → 헤더만
        foreach (var c in await cat.GetRecentClipsAsync(100)) await cat.MarkTelemetryLoggedAsync(c.VideoId);
        Assert.Equal("VideoId,HookRetentionPct,CompletionPct,CtrPct\n", await importer.BuildUnlearnedCsvTemplateAsync());
    }

    [Fact]
    public async Task TelemetryBatchImporter_RoundTripsUnlearnedTemplateRows_IntoClosedLoopLearning()
    {
        var storage = new PlutoDriveStorageManager(null, _dir);
        using var nb = new PlutoMistakeNotebookService(storage);
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, storage);
        using var cat = new PlutoRenderCatalogService(storage);
        await cat.RecordRenderedClipAsync(Clip("rt_a", "A", "몽타주", "hook-a", "a", 3));
        await cat.RecordRenderedClipAsync(Clip("rt_b", "B", "몽타주", "hook-b", "b", 2));
        await cat.RecordRenderedClipAsync(Clip("rt_c", "C", "몽타주", "hook-c", "c", 1));
        await cat.MarkTelemetryLoggedAsync("rt_b");
        var importer = new PlutoTelemetryBatchImporter(cat, loop);

        var template = await importer.BuildUnlearnedCsvTemplateAsync();
        var r = await importer.ImportCsvAsync(template);
        Assert.Equal(2, r.TotalRows);
        Assert.Equal(2, r.MatchedAndLearnedCount);
        Assert.Equal(0, r.UnmatchedCount);
        Assert.All(await cat.GetRecentClipsAsync(), c => Assert.True(c.TelemetryLogged));

        // 반영 후 재생성 → 헤더만 · 같은 템플릿 재반영 → 기반영(중복 차단)
        Assert.Equal("VideoId,HookRetentionPct,CompletionPct,CtrPct\n", await importer.BuildUnlearnedCsvTemplateAsync());
        var again = await importer.ImportCsvAsync(template);
        Assert.Equal(0, again.MatchedAndLearnedCount);
        Assert.Equal(2, again.AlreadyRegisteredCount);
    }
}
