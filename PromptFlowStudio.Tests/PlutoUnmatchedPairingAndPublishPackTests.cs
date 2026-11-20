using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>📌 Step 24 — 미매칭 YouTube 행 원클릭 페어링 · 게시 전 업로드 패키지 · 감시 장부 유계 정리 · 자체 추가 안전장치.</summary>
public sealed class PlutoUnmatchedPairingAndPublishPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_yt_pair_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoUnmatchedPairingAndPublishPackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoRenderCatalogService Catalog(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return new PlutoRenderCatalogService(new PlutoDriveStorageManager(null, d));
    }

    private static PlutoRenderedClipRecord Clip(string id, string title, DateTimeOffset at, string mode = "Ambient15s")
        => new(id, title, mode, "훅", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "tag-" + id }, @"C:\out\" + id + ".mp4", at, false);

    private static string Row(string ytId, string title, long views, double avg = 48, double stayed = 66, string date = "Nov 2, 2026")
        => ytId + ",\"" + title + "\",\"" + date + "\"," + views.ToString(CultureInfo.InvariantCulture) + "," + avg.ToString(CultureInfo.InvariantCulture) + ",5000,5.0,"
            + stayed.ToString(CultureInfo.InvariantCulture) + ",10\n";

    private static YouTubeAnalyticsRow YtRow(string ytId, string title, long views, DateTimeOffset? published)
        => new(ytId, title, published, views, 66, false, 48, 48, 5.0, false);

    /// <summary>폐루프 대역 — 최초 반영 (플랫폼, 영상) · 증분 (영상, 단계) 멱등.</summary>
    private sealed class CountingLoop : IPlutoClosedLoopReinforcementService
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PlutoVideoPerformanceTelemetry> Ingested = new();
        public int ThrowTimes;
        public PlutoReinforcementOutcome? LastOutcome => null;
        public event Action? Changed { add { } remove { } }

        public Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct = default)
        {
            if (ThrowTimes > 0) { ThrowTimes--; throw new IOException("ledger locked"); }
            if (!_keys.Add(t.Platform + "|" + t.VideoId)) return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "dup") { AlreadyRegistered = true });
            Ingested.Add(t);
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "ok"));
        }

        public Task<PlutoReinforcementOutcome> IngestMaturityDeltaAsync(PlutoVideoPerformanceTelemetry matured, PlutoVideoPerformanceTelemetry previous, int stage, double weight, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "delta"));

        public Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "noop"));
    }

    // ── ① 미매칭 행 보존 → 원클릭 페어링 → YouTube ID 영속 + 폐루프 반영 ──

    [Fact]
    public async Task UnmatchedRow_IsPersistedWithFullTelemetry_AndOneClickPairBindsIdAndReinforces()
    {
        using var cat = Catalog("c1");
        await cat.RecordRenderedClipAsync(Clip("walk_clip", "가을 산책 브이로그", T0));
        var loop = new CountingLoop();
        var qPath = Path.Combine(_dir, "q1", PlutoYouTubeUnmatchedQueueService.FileName);
        using var queue = new PlutoYouTubeUnmatchedQueueService(qPath, cat, loop);
        var importer = new PlutoTelemetryBatchImporter(cat, loop, queue);

        // YouTube에서 제목을 통째로 바꾼 영상 → 자동 연결 실패
        var csv = Encoding.UTF8.GetBytes(EnHeader + "\n" + Row("EditedYT001", "역대급 단풍 명소 공개", 4321, avg: 52.5, stayed: 71.0));
        var r1 = await importer.ImportYouTubeStudioFileAsync(csv, "Table data.csv");
        Assert.Equal(0, r1.MatchedAndLearnedCount);
        Assert.Equal(1, r1.UnmatchedCount);
        Assert.Equal(1, r1.QueuedUnmatchedCount);
        Assert.Contains("페어링 대기 1건", r1.SummaryKorean, StringComparison.Ordinal);
        Assert.Empty(loop.Ingested);

        // 텔레메트리 손실 없음 · 재시작 후에도 유지
        using (var reopened = new PlutoYouTubeUnmatchedQueueService(qPath, cat, loop))
        {
            await reopened.EnsureLoadedAsync();
            var p = Assert.Single(reopened.GetPending());
            Assert.Equal("EditedYT001", p.YouTubeVideoId);
            Assert.Equal(4321, p.Row.Views);
            Assert.Equal(71.0, p.Row.Hook3sRetention, 3);
            Assert.Equal(52.5, p.Row.CompletionRate, 3);
            Assert.Equal(5.0, p.Row.Ctr, 3);
            Assert.NotNull(p.Reason);
        }

        var sugs = await queue.SuggestClipsAsync("EditedYT001");
        Assert.Equal("walk_clip", Assert.Single(sugs).Clip.VideoId);   // 제목 유사도 0이어도 게시 창 안의 렌더 → 후보

        var changed = 0;
        queue.Changed += () => Interlocked.Increment(ref changed);
        var pr = await queue.PairAsync("EditedYT001", "walk_clip");
        Assert.True(pr.Succeeded, pr.MessageKorean);
        Assert.Equal(PlutoYouTubeRowApplyKind.Learned, pr.Applied!.Kind);
        Assert.Equal(1, changed);
        Assert.Equal(0, queue.Count);

        var bound = await cat.FindByYouTubeVideoIdAsync("EditedYT001");
        Assert.NotNull(bound);
        Assert.Equal("walk_clip", bound!.VideoId);
        Assert.True(bound.TelemetryLogged);
        Assert.Equal(4321, bound.YouTubeWatermark!.Views);
        var t = Assert.Single(loop.Ingested);
        Assert.Equal("walk_clip", t.VideoId);
        Assert.Equal(71.0, t.ThreeSecondHookRetentionPct, 3);
        Assert.Equal(52.5, t.AverageCompletionPct, 3);

        // 더블 클릭 → 두 번째는 NotQueued(이중 학습 없음)
        Assert.Equal(PlutoUnmatchedPairStatus.NotQueued, (await queue.PairAsync("EditedYT001", "walk_clip")).Status);

        // 같은 내보내기 재수집 → 이제 Tier 1 정확 연결 · 같은 스냅샷 → 0건 강화 · 재대기 없음
        var r2 = await importer.ImportYouTubeStudioFileAsync(csv, "Table data.csv");
        Assert.Equal(0, r2.UnmatchedCount);
        Assert.Equal(1, r2.AlreadyRegisteredCount);
        Assert.Equal(0, r2.QueuedUnmatchedCount);
        Assert.Single(loop.Ingested);
        Assert.Equal(0, queue.Count);
    }

    // ── ② 대기열 유계 · YouTube ID 중복 제거(더 성숙한 스냅샷 우선) · 연결된 ID 정리 ──

    [Fact]
    public async Task UnmatchedQueue_IsBoundedDedupedByVideoId_KeepsMaturerSnapshot_AndResolvesLinkedIds()
    {
        using var cat = Catalog("c2");
        var loop = new CountingLoop();
        var qPath = Path.Combine(_dir, "q2", PlutoYouTubeUnmatchedQueueService.FileName);
        using var queue = new PlutoYouTubeUnmatchedQueueService(qPath, cat, loop);

        var rows = new List<PlutoUnmatchedYouTubeRow>();
        for (var i = 0; i < 60; i++) rows.Add(new PlutoUnmatchedYouTubeRow(YtRow("Vid" + i.ToString("D8", CultureInfo.InvariantCulture), "영상 " + i.ToString(CultureInfo.InvariantCulture), 100 + i, T0), "x", "a.csv", T0));
        rows.Add(new PlutoUnmatchedYouTubeRow(YtRow("bad id", "형식 오류", 100, T0), null, null, T0));             // ID 형식 오류 → 거부
        rows.Add(new PlutoUnmatchedYouTubeRow(YtRow("NoTitle0001", "  ", 100, T0), null, null, T0));             // 제목 없음 → 거부
        Assert.Equal(60, await queue.EnqueueAsync(rows));
        Assert.Equal(PlutoYouTubeUnmatchedQueueService.Capacity, queue.Count);
        var pending = queue.GetPending();
        Assert.Equal("Vid00000059", pending[0].YouTubeVideoId);   // 최신 대기 맨 앞
        Assert.DoesNotContain(pending, p => p.YouTubeVideoId == "Vid00000000");   // 가장 오래된 10건 축출

        // 덜 성숙한(조회수 낮은) 재수집 → 무변경 · 더 성숙 → 갱신
        Assert.Equal(0, await queue.EnqueueAsync(new[] { new PlutoUnmatchedYouTubeRow(YtRow("Vid00000059", "영상 59", 10, T0), "x", "old.csv", T0) }));
        Assert.Equal(1, await queue.EnqueueAsync(new[] { new PlutoUnmatchedYouTubeRow(YtRow("Vid00000030", "영상 30", 9999, T0), "x", "new.csv", T0) }));
        Assert.Equal(PlutoYouTubeUnmatchedQueueService.Capacity, queue.Count);
        Assert.Equal("Vid00000030", queue.GetPending(1)[0].YouTubeVideoId);
        Assert.Equal(9999, queue.GetPending(1)[0].Row.Views);

        Assert.Equal(2, await queue.ResolveLinkedAsync(new[] { "Vid00000030", "Vid00000031", "NotQueued01" }));
        Assert.Equal(PlutoYouTubeUnmatchedQueueService.Capacity - 2, queue.Count);
        Assert.True(await queue.DismissAsync("Vid00000040"));
        Assert.False(await queue.DismissAsync("Vid00000040"));

        using var reopened = new PlutoYouTubeUnmatchedQueueService(qPath, cat, loop);
        await reopened.EnsureLoadedAsync();
        Assert.Equal(PlutoYouTubeUnmatchedQueueService.Capacity - 3, reopened.Count);
    }

    // ── ③ 업로드 패키지 — A/B 제목 n/N편 보존 · 자동 연결 가능 · 훅 정렬 설명 · 파레토 해시태그 · 오답 노트 차단 제외 ──

    [Fact]
    public async Task PublishPack_PreservesEpisodeToken_IsAutoLinkable_AlignsHook_AndExcludesMistakeNotebookBlocks()
    {
        var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb", "mistakes.json"));
        await notebook.RecordNegativeFeedbackAsync(new[] { "clickbait", "충격반전" }, 2.0);
        Assert.Contains("clickbait", notebook.GetSuppressedTokens());
        var svc = new PlutoYouTubePublishPackService(null, null, notebook);

        var req = new PlutoPublishPackRequest("가을 산책", "가을 산책 단풍길", 2, 3, "충격반전", new[] { "단풍", "Clickbait", "#단풍", "cozy vlog", "autumn" });
        var pack = await svc.GenerateAsync(req);

        foreach (var c in new[] { pack.TitleA, pack.TitleB })
        {
            Assert.Contains("2/3편", c.Title, StringComparison.Ordinal);
            Assert.True(c.Title.Length <= PlutoYouTubePublishPackService.MaxTitleLength);
            Assert.True(c.AutoLinkable, c.Title + " → " + c.LinkabilityScore.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(2, PlutoYouTubeCatalogLinker.ExtractEpisode(c.Title));
            Assert.DoesNotContain("충격반전", c.Title, StringComparison.Ordinal);
        }
        Assert.NotEqual(pack.TitleA.Title, pack.TitleB.Title);
        Assert.True(pack.IsKorean);

        // 해시태그: #shorts 선두 · 중복 제거 · 상한 · 차단 토큰 제외(대소문자 무시)
        Assert.Equal("#shorts", pack.Hashtags[0]);
        Assert.True(pack.Hashtags.Count <= PlutoYouTubePublishPackService.MaxHashtags);
        Assert.Equal(pack.Hashtags.Count, new HashSet<string>(pack.Hashtags, StringComparer.OrdinalIgnoreCase).Count);
        Assert.Contains("#단풍", pack.Hashtags);
        Assert.Contains("#cozyvlog", pack.Hashtags);
        Assert.DoesNotContain(pack.Hashtags, h => h.Equals("#clickbait", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pack.BlockedRemoved, b => b.Equals("충격반전", StringComparison.Ordinal));
        Assert.Contains(pack.BlockedRemoved, b => b.Equals("Clickbait", StringComparison.OrdinalIgnoreCase));

        // 설명 첫 줄 = 훅 · 시리즈 문맥 · 해시태그 · 고정 댓글 CTA(다음 편)
        var lines = pack.Description.Split('\n');
        Assert.Equal(pack.HookLine, lines[0]);
        Assert.Contains("시리즈 2/3편", pack.Description, StringComparison.Ordinal);
        Assert.Contains("다음 3편", pack.Description, StringComparison.Ordinal);
        Assert.Contains("#shorts", pack.Description, StringComparison.Ordinal);
        Assert.Contains("3편", pack.PinnedComment, StringComparison.Ordinal);
        Assert.Contains(pack.TitleA.Title, pack.ToClipboardText(), StringComparison.Ordinal);

        // 🔗 게시 후 내보내기 → 카탈로그 자동 연결(Tier 2 · 편 가드 통과)
        using var cat = Catalog("c3");
        await cat.RecordRenderedClipAsync(Clip("ep1", "가을 산책 단풍길", T0, "시리즈 1/3편"));
        await cat.RecordRenderedClipAsync(Clip("ep2", "가을 산책 단풍길", T0.AddMinutes(5), "시리즈 2/3편"));
        var links = await new PlutoYouTubeCatalogLinker(cat).LinkAsync(new[] { YtRow("PubPackYT02", pack.TitleB.Title, 500, T0.AddDays(1)) });
        Assert.Equal("ep2", links[0].Clip?.VideoId);
        Assert.Equal(PlutoYouTubeLinkTier.Fuzzy, links[0].Tier);

        // 영어 · 비시리즈 · 완결편
        var en = svc.Generate(new PlutoPublishPackRequest("Cozy cabin morning", null, 0, 0, null, new[] { "cabin" }) { IsShorts = false });
        Assert.False(en.IsKorean);
        Assert.DoesNotContain("편", en.TitleA.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("#shorts", en.Hashtags);
        var fin = svc.Generate(new PlutoPublishPackRequest("가을 산책", "가을 산책 단풍길", 3, 3, null, null));
        Assert.Contains("완결", fin.PinnedComment, StringComparison.Ordinal);
    }

    // ── ④ 감시 장부 TTL/상한 정리 · v1 이관 · 격리 해제 후 재시도 ──

    private sealed class FlakyImporter : IPlutoTelemetryBatchImporter
    {
        public int InvalidTimes = 1;
        public int Calls;

        public Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (InvalidTimes > 0) { InvalidTimes--; throw new InvalidDataException("broken export"); }
            return Task.FromResult(new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>()) { IsYouTubeStudioReport = true });
        }

        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private static string Hash(int i) => i.ToString("X64", CultureInfo.InvariantCulture);

    [Fact]
    public async Task WatcherLedger_PrunesByTtlAndLru_MigratesV1_AndPersistsBoundedState()
    {
        var now = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var state = Path.Combine(_dir, "w1", "downloads_watcher.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        var ing = new JsonArray();
        for (var i = 0; i < 3; i++) ing.Add(new JsonObject { ["h"] = Hash(i), ["t"] = now.AddDays(-100).UtcTicks });          // TTL 초과
        for (var i = 10; i < 16; i++) ing.Add(new JsonObject { ["h"] = Hash(i), ["t"] = now.AddDays(-(16 - i)).UtcTicks });   // 최근 6건(10이 가장 오래됨)
        var rej = new JsonArray { new JsonObject { ["h"] = Hash(100), ["t"] = now.AddDays(-1).UtcTicks, ["q"] = true }, Hash(101) };   // 격리 1 · v1 문자열 1
        await File.WriteAllTextAsync(state, new JsonObject { ["v"] = 2, ["enabled"] = true, ["ingested"] = ing, ["rejected"] = rej }.ToJsonString());

        var opt = new YouTubeDownloadsWatcherOptions { Directory = Path.Combine(_dir, "w1dl"), StatePath = state, LedgerMaxEntries = 4, LedgerTtl = TimeSpan.FromDays(90), Clock = () => now, SweepInterval = TimeSpan.Zero };
        using (var w = new YouTubeDownloadsAutoWatcherService(new FlakyImporter(), opt))
        {
            var h = w.GetHealth();
            Assert.Equal(4, h.IngestedLedgerCount);   // 3 TTL 제거 + 2 LRU 축출
            Assert.Equal(5, h.PrunedTotal);
            Assert.Equal(1, h.QuarantinedCount);
            Assert.Equal(1, h.IgnoredLedgerCount);
            Assert.False(w.IsKnownHash(Hash(0)));
            Assert.False(w.IsKnownHash(Hash(10)));   // 가장 오래된 최근 항목 → LRU 축출
            Assert.True(w.IsKnownHash(Hash(15)));
            Assert.True(w.IsKnownHash(Hash(101)));   // v1 → 적재 시각으로 이관
            Assert.Equal(0, await w.PruneLedgerAsync());   // 이미 정리됨 · 하지만 적재 정리분은 디스크에 반영
        }
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(state))!.AsObject();
        Assert.Equal(YouTubeDownloadsAutoWatcherService.StateSchemaVersion, saved["v"]!.GetValue<int>());
        Assert.Equal(4, saved["ingested"]!.AsArray().Count);
        Assert.All(saved["rejected"]!.AsArray(), n => Assert.NotNull(n!["t"]));

        // 시간 경과 → 재적재 시 TTL 재정리
        var later = now.AddDays(200);
        using (var w2 = new YouTubeDownloadsAutoWatcherService(new FlakyImporter(), new YouTubeDownloadsWatcherOptions { Directory = opt.Directory, StatePath = state, Clock = () => later, SweepInterval = TimeSpan.Zero }))
        {
            Assert.Equal(0, w2.GetHealth().IngestedLedgerCount);
            Assert.Equal(0, w2.GetHealth().QuarantinedCount);
        }

        // 🛡️ TTL 하한: 스윕 최대 경과(30일) + 1일 미만으로 내려가지 않음
        using var w3 = new YouTubeDownloadsAutoWatcherService(new FlakyImporter(), new YouTubeDownloadsWatcherOptions { Directory = opt.Directory, StatePath = state + ".3", LedgerTtl = TimeSpan.FromDays(1), MaxFileAge = TimeSpan.FromDays(30) });
        Assert.Equal(TimeSpan.FromDays(31), w3.EffectiveLedgerTtl);
    }

    [Fact]
    public async Task ClearQuarantined_ReleasesBrokenExport_AndNextSweepReingestsFixedFile()
    {
        var dl = Path.Combine(_dir, "w2dl");
        Directory.CreateDirectory(dl);
        var state = Path.Combine(_dir, "w2", "downloads_watcher.json");
        var imp = new FlakyImporter();
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, new YouTubeDownloadsWatcherOptions
        {
            Directory = dl, StatePath = state, Debounce = TimeSpan.FromMilliseconds(80), SweepInterval = TimeSpan.Zero,
            ReadinessRetries = 20, ReadinessRetryDelay = TimeSpan.FromMilliseconds(50), StabilityProbe = TimeSpan.FromMilliseconds(30), UseFileSystemWatcher = false,
        });
        var ingested = new TaskCompletionSource<PlutoDownloadsAutoIngestEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        w.OnExportAutoIngested += e => ingested.TrySetResult(e);
        await File.WriteAllBytesAsync(Path.Combine(dl, "Table data.csv"), Encoding.UTF8.GetBytes(EnHeader + "\n" + Row("WalkClipYT1", "가을 산책", 300)));
        Assert.True(w.Start());

        var deadline = DateTime.UtcNow + Wait;
        while (w.GetHealth().QuarantinedCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
        var h = w.GetHealth();
        Assert.Equal(1, h.QuarantinedCount);
        Assert.Contains("broken export", h.LastError, StringComparison.Ordinal);
        Assert.Contains("격리 1건", h.StatusKorean, StringComparison.Ordinal);
        Assert.Equal(0, await w.SweepNowAsync());   // 격리 중 → 재시도 없음
        Assert.Equal(1, imp.Calls);

        Assert.Equal(1, await w.ClearQuarantinedAsync());   // 사용자가 파일을 고침 → 해제 → 즉시 재스캔
        var evt = await ingested.Task.WaitAsync(Wait);
        Assert.Equal(1, evt.NewlyLearned);
        Assert.Equal(2, imp.Calls);
        var after = w.GetHealth();
        Assert.Equal(0, after.QuarantinedCount);
        Assert.Null(after.LastError);
        Assert.Equal(1, after.IngestedLedgerCount);
        Assert.NotNull(after.LastIngestUtc);
    }

    // ── ⑤ 자체 추가 A — 시리즈 편 가드가 카탈로그 RenderMode(시리즈 n/N편)까지 본다 ──

    [Fact]
    public void SelfAddition_EpisodeGuard_UsesCatalogRenderMode_SoSiblingEpisodesNeverCrossLink()
    {
        var ep1 = Clip("ep1", "단풍길 산책", T0, "시리즈 1/3편");
        var ep2 = Clip("ep2", "단풍길 산책", T0.AddMinutes(3), "시리즈 2/3편");
        var ep3 = Clip("ep3", "단풍길 산책", T0.AddMinutes(6), "시리즈 3/3편");
        Assert.Equal(1, PlutoYouTubeCatalogLinker.CatalogEpisode(ep1));
        Assert.True(PlutoYouTubeCatalogLinker.EpisodeConflict("단풍길 산책 2/3편", ep1));
        Assert.False(PlutoYouTubeCatalogLinker.EpisodeConflict("단풍길 산책", ep1));   // 편 표기 없는 YouTube 제목 → 가드 비적용

        // 편 제목이 같아도 2/3편 행은 2편에만 연결(이전: 3개가 동점 → 모호 거부 또는 오연결)
        var row = YtRow("SeriesYT002", "단풍길 산책 2/3편", 800, T0.AddDays(1));
        var (clip, _, reason) = PlutoYouTubeCatalogLinker.SelectFuzzyCandidate(row, new[] { ep3, ep2, ep1 });
        Assert.Null(reason);
        Assert.Equal("ep2", clip!.VideoId);

        // 페어링 후보도 편 충돌 · 게시 후 렌더 · 이미 연결된 클립을 제외
        var later = Clip("later", "단풍길 산책", T0.AddDays(5));
        var taken = Clip("taken", "단풍길 산책", T0) with { YouTubeVideoId = "TakenYT0001" };
        var sugs = PlutoYouTubeCatalogLinker.RankPairSuggestions(row, new[] { later, taken, ep1, ep2, ep3 }, 5);
        Assert.Equal("ep2", Assert.Single(sugs).Clip.VideoId);
    }

    // ── ⑥ 자체 추가 B — 업로드 제목 연결성 자가 검사(훅 문구가 유사도를 희석하면 안전 제목으로 복귀) ──

    [Fact]
    public void SelfAddition_LinkabilityGuard_FallsBackToCatalogCentricTitle_WhenHookDilutesSimilarity()
    {
        var svc = new PlutoYouTubePublishPackService();
        var longHook = "이 장면 끝까지 보면 진짜 깜짝 놀랄 수밖에 없는 이유";   // 짧은 카탈로그 제목 대비 토큰 대부분이 훅
        var pack = svc.Generate(new PlutoPublishPackRequest("고양이", "고양이 낮잠", 1, 2, longHook, null));
        var naive = "고양이 낮잠 1/2편 · " + PlutoYouTubePublishPackService.HookPhrase(longHook, true);
        Assert.True(PlutoYouTubeCatalogLinker.TitleSimilarity(naive, "고양이 낮잠") < PlutoYouTubeCatalogLinker.MinFuzzyScore);   // 그대로 쓰면 미매칭 대기열행
        Assert.True(pack.TitleA.AutoLinkable);
        Assert.True(pack.TitleB.AutoLinkable);
        Assert.Contains("1/2편", pack.TitleA.Title, StringComparison.Ordinal);
        Assert.Contains("1/2편", pack.TitleB.Title, StringComparison.Ordinal);
        Assert.NotEqual(pack.TitleA.Title, pack.TitleB.Title);   // 둘 다 안전 형태로 수렴해도 A/B 차별화 유지

        // 100자 상한에서도 편 토큰은 잘리지 않음
        var huge = svc.Generate(new PlutoPublishPackRequest(new string('가', 150), null, 3, 4, null, null));
        Assert.True(huge.TitleA.Title.Length <= PlutoYouTubePublishPackService.MaxTitleLength);
        Assert.EndsWith("3/4편", huge.TitleA.Title, StringComparison.Ordinal);
    }

    // ── ⑦ 자체 추가 C — 대기열 정합성: 이미 연결된 ID 미보존 · 선점 충돌 시 정리 · 학습 실패 시 보존 후 멱등 재시도 ──

    [Fact]
    public async Task SelfAddition_QueueConsistency_SkipsBoundIds_CleansStaleOnConflict_AndKeepsRowWhenLearningFails()
    {
        using var cat = Catalog("c7");
        await cat.RecordRenderedClipAsync(Clip("a_clip", "봄 소풍", T0));
        await cat.RecordRenderedClipAsync(Clip("b_clip", "여름 바다", T0));
        await cat.RecordRenderedClipAsync(Clip("c_clip", "겨울 눈", T0));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("a_clip", "AlreadyYT01"));
        var loop = new CountingLoop();
        using var queue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "q7", PlutoYouTubeUnmatchedQueueService.FileName), cat, loop);

        // 이미 다른 경로로 연결된 ID → 보존하지 않음
        Assert.Equal(1, await queue.EnqueueAsync(new[]
        {
            new PlutoUnmatchedYouTubeRow(YtRow("AlreadyYT01", "봄 소풍 편집본", 300, T0.AddDays(1)), null, null, T0),
            new PlutoUnmatchedYouTubeRow(YtRow("FreshYT0001", "바다 영상", 300, T0.AddDays(1)), null, null, T0),
        }));

        // 다른 PC가 같은 ID를 c_clip에 먼저 연결 → 페어링 시 대기 항목은 정리(이중 학습 없음)
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("c_clip", "FreshYT0001"));
        var conflict = await queue.PairAsync("FreshYT0001", "b_clip");
        Assert.Equal(PlutoUnmatchedPairStatus.AlreadyLinkedElsewhere, conflict.Status);
        Assert.Equal(0, queue.Count);
        Assert.Empty(loop.Ingested);

        // 클립이 이미 다른 ID에 연결 → 거부 · 대기 유지
        await queue.EnqueueAsync(new[] { new PlutoUnmatchedYouTubeRow(YtRow("RetryYT0001", "바다", 300, T0.AddDays(1)), null, null, T0) });
        Assert.Equal(PlutoUnmatchedPairStatus.ClipBoundElsewhere, (await queue.PairAsync("RetryYT0001", "a_clip")).Status);
        Assert.Equal(PlutoUnmatchedPairStatus.ClipNotFound, (await queue.PairAsync("RetryYT0001", "nope")).Status);
        Assert.Equal(1, queue.Count);

        // 학습 반영 실패 → 연결은 유지 · 대기 유지 → 재시도에서 정확히 1회 학습
        loop.ThrowTimes = 1;
        var failed = await queue.PairAsync("RetryYT0001", "b_clip");
        Assert.Equal(PlutoUnmatchedPairStatus.LearnFailed, failed.Status);
        Assert.Equal(1, queue.Count);
        Assert.Equal("b_clip", (await cat.FindByYouTubeVideoIdAsync("RetryYT0001"))!.VideoId);
        var ok = await queue.PairAsync("RetryYT0001", "b_clip");
        Assert.True(ok.Succeeded, ok.MessageKorean);
        Assert.Equal("b_clip", Assert.Single(loop.Ingested).VideoId);
        Assert.Equal(0, queue.Count);
    }
}
