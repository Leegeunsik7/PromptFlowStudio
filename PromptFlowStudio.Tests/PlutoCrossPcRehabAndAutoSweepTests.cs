using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🌱 Step 29 — 복권 장부 멀티 PC 동기화 · 합집합 병합 · 수집 후 자동 채점/복권 스윕 · 진단서 .md 원자 저장 · 자체 추가 안전장치.</summary>
public sealed class PlutoCrossPcRehabAndAutoSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_xpc_rehab_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string Golden = PlutoColorGradingCompiler.GoldenHourWarmName;
    private const string Hook = "pov-walk";
    private const string Theme = "가을 숲 산책";
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";
    private const double F = PlutoMistakeNotebookService.RehabilitationFactor;

    public PlutoCrossPcRehabAndAutoSweepTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── 픽스처 ──

    private sealed class Clock
    {
        public DateTime Now = T0;
        public DateTime Get() => Now;
    }

    private static string Pair => PlutoPairAttribution.PairKey(Hook, Golden)!;

    /// <summary>PC 별 폴더의 기본 파일명 노트(장부 = 같은 폴더 <c>pluto_rehab_ledger.json</c>).</summary>
    private PlutoMistakeNotebookService Pc(string name, Clock clock)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return new PlutoMistakeNotebookService(Path.Combine(d, PlutoMistakeNotebookService.FileName), clock.Get);
    }

    private string PcFile(string name, string file) => Path.Combine(_dir, name, file);

    /// <summary>사후 분석 벌점 2.0(30일 반감기) 기대 유효 벌점 × 0.35^n.</summary>
    private static double Expected(DateTime penalizedAt, DateTime now, int rehabs) => 2.0 * Math.Pow(0.5, (now - penalizedAt).TotalDays / 30.0) * Math.Pow(F, rehabs);

    private static PlutoYouTubeIngestWatermark Wm(double hook, double comp, double ctr, long views) => new(views, 0, hook, comp, ctr, new DateTimeOffset(T0));

    private static PlutoRenderedClipRecord Measured(string id, string hook, string look, PlutoYouTubeIngestWatermark? wm, DateTime renderedUtc)
        => new PlutoRenderedClipRecord(id, id, "몽타주", hook, look, "Lullaby", new[] { "autumn" }, "", new DateTimeOffset(renderedUtc), false) { YouTubeWatermark = wm };

    private static async Task Put(PlutoRenderCatalogService catalog, PlutoRenderedClipRecord rec, string ytId)
    {
        await catalog.RecordRenderedClipAsync(rec with { YouTubeWatermark = null, YouTubeVideoId = ytId });
        Assert.True(await catalog.UpdateYouTubeWatermarkAsync(rec.VideoId, rec.YouTubeWatermark!));
    }

    private static byte[] Export(string id, long views)
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + id + ",가을 산책 브이로그 #shorts,\"Nov 2, 2026\"," + views.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",60,5000,5.0,66.0,10\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static PlutoRehabilitationLedger.Snapshot Ledger(params (string Key, string Clip, DateTime Rendered, DateTime Applied)[] recs)
    {
        var list = new List<PlutoRehabilitationLedger.Record>();
        foreach (var r in recs) list.Add(new PlutoRehabilitationLedger.Record { Key = r.Key, ClipId = r.Clip, ClipRenderedUtc = r.Rendered, AppliedAtUtc = r.Applied, SurpriseDelta = 12 });
        return new PlutoRehabilitationLedger.Snapshot { Records = list };
    }

    // ── ① 장부 합집합 병합(순수) + Drive 동반 동기화 편입(핵심 허브 9종 · 병합 허브 6종 불변) ──

    [Fact]
    public void CrossPc_LedgerUnionMerge_IsCommutativeIdempotent_AndJoinsDriveCompanionSyncWithoutTouchingNineHubInvariant()
    {
        var a = Ledger((Pair, "clip-x", T0.AddDays(1), T0.AddDays(2)), (Pair, "clip-y", T0.AddDays(1), T0.AddDays(3)));
        var b = Ledger((Pair.ToUpperInvariant(), "CLIP-X", T0.AddDays(1), T0.AddDays(2).AddHours(5)), ("domain_hook:x|y", "clip-z", default, T0.AddDays(4)));
        var ja = JsonSerializer.Serialize(a);
        var jb = JsonSerializer.Serialize(b);

        var ab = PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName, ja, jb);
        var ba = PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName, jb, ja);
        Assert.NotNull(ab);
        Assert.Equal(ab, ba);                                                                         // 교환 — 두 PC 가 같은 바이트로 수렴
        Assert.Equal(ab, PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName, ab, ab));   // 멱등
        var merged = JsonSerializer.Deserialize<PlutoRehabilitationLedger.Snapshot>(ab!)!;
        Assert.Equal(3, merged.Records!.Count);                                                       // (키, 클립) 대소문자 무시 합집합 — clip-x 1건
        var x = merged.Records.Find(r => string.Equals(r.ClipId, "clip-x", StringComparison.OrdinalIgnoreCase))!;
        Assert.Equal(T0.AddDays(2), x.AppliedAtUtc);                                                  // 최초 적용 유지
        Assert.Null(PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName, "{}", jb));   // 형태 불일치 → 병합 거부
        Assert.Null(PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName, "not json", jb));

        // 상한 — 오래된 적용부터 제외
        var many = new List<PlutoRehabilitationLedger.Record>();
        for (var i = 0; i < PlutoRehabilitationLedger.Capacity + 5; i++)
            many.Add(new() { Key = Pair, ClipId = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), AppliedAtUtc = T0.AddMinutes(i) });
        var capped = PlutoRehabilitationLedger.Merge(new[] { new PlutoRehabilitationLedger.Snapshot { Records = many } });
        Assert.Equal(PlutoRehabilitationLedger.Capacity, capped.Records!.Count);
        Assert.Equal("c5", capped.Records[0].ClipId);

        // ☁️ Drive 동반 파일 편입 — 핵심 허브 9종 · 병합 허브 6종 불변
        Assert.Equal(9, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.Equal(PlutoDriveHealthService.CorePlutoFileCount, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.DoesNotContain(PlutoRehabilitationLedger.FileName, PlutoDriveHealthService.PlutoFiles);
        Assert.Contains(PlutoRehabilitationLedger.FileName, PlutoDriveHealthService.CompanionSyncFiles);
        Assert.Contains(PlutoRehabilitationLedger.FileName, PlutoDriveHealthService.SyncFiles);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.True(PlutoConflictMergeService.IsMergeable(PlutoRehabilitationLedger.FileName));
        Assert.Equal(PlutoRehabilitationLedger.FileName, PlutoConflictMergeService.HubFileOf("pluto_rehab_ledger.conflict-20261102010000.json"));
        Assert.True(PlutoHotReload.HasExpectedShape(PlutoRehabilitationLedger.FileName, "{\"Version\":1,\"Records\":[]}"));
        Assert.False(PlutoHotReload.HasExpectedShape(PlutoRehabilitationLedger.FileName, "{}"));
        Assert.Equal(PlutoMistakeNotebookService.FileName, PlutoHotReload.OwnerHubOf(PlutoRehabilitationLedger.FileName));
    }

    // ── ② 두 PC 오프라인 복권 → 노트(최신 승) + 장부(합집합) 병합 → 정확히 1회씩만 0.35 (이중 감쇠 없음 · 누락 재생) ──

    [Fact]
    public async Task CrossPc_OfflineRehabOnBothPcs_ConvergesWithoutDoubleAttenuation_AndReplaysPeerRehabDroppedByNotebookMerge()
    {
        var clock = new Clock();
        var origin = Pc("origin", clock);
        await origin.RecordPostMortemMistakeAsync(new[] { Pair }, 2.0, PlutoMistakeNotebookService.DimensionCompletionDrop, "brief-postmortem: B1 Δ-18.0p");
        var seed = File.ReadAllText(PcFile("origin", PlutoMistakeNotebookService.FileName));
        foreach (var pc in new[] { "pcA", "pcB", "pcA2", "pcB2" }) { Directory.CreateDirectory(Path.Combine(_dir, pc)); File.WriteAllText(PcFile(pc, PlutoMistakeNotebookService.FileName), seed); }

        // 사례 1 — 같은 클립 X 를 두 PC 가 각각 복권 → 병합 후에도 1회
        var a = Pc("pcA", clock);
        var b = Pc("pcB", clock);
        clock.Now = T0.AddDays(2);
        Assert.True((await a.RecordOrganicRecoveryAsync(new[] { Pair }, "clip-x", 20, T0.AddDays(1))).Any);
        clock.Now = T0.AddDays(2).AddHours(1);
        Assert.True((await b.RecordOrganicRecoveryAsync(new[] { Pair }, "clip-x", 20, T0.AddDays(1))).Any);
        Assert.True(File.Exists(a.LedgerStoragePath));
        Assert.Equal(PlutoRehabilitationLedger.FileName, Path.GetFileName(a.LedgerStoragePath));
        await MergeInto("merged1", "pcA", "pcB");
        clock.Now = T0.AddDays(3);
        var m1 = Pc("merged1", clock);
        await m1.EnsureLoadedAsync();
        Assert.Equal(0, m1.LastLedgerReconcile.Replayed);
        Assert.Equal(Expected(T0, clock.Now, 1), m1.GetEffectivePenalty(Pair), 6);          // 0.35 정확히 1회
        Assert.Equal(1, (await m1.RecordOrganicRecoveryAsync(new[] { Pair }, "clip-x", 20, T0.AddDays(1))).SkippedDuplicate);

        // 사례 2 — A 는 X, B 는 Y 복권 · B 가 더 최신 → 노트 병합이 A 의 X 를 버림 → 장부 합집합이 X 를 1회 재생
        clock.Now = T0;
        var a2 = Pc("pcA2", clock);
        var b2 = Pc("pcB2", clock);
        clock.Now = T0.AddDays(2);
        Assert.True((await a2.RecordOrganicRecoveryAsync(new[] { Pair }, "clip-x", 20, T0.AddDays(1))).Any);
        clock.Now = T0.AddDays(2).AddHours(3);
        Assert.True((await b2.RecordOrganicRecoveryAsync(new[] { Pair }, "clip-y", 15, T0.AddDays(1).AddHours(2))).Any);
        var mergedNotebook = await MergeInto("merged2", "pcA2", "pcB2");
        Assert.DoesNotContain("clip-x", mergedNotebook, StringComparison.Ordinal);                // 노트 병합은 최신 쪽 목록만 — 상대 복권 소실
        clock.Now = T0.AddDays(3);
        var m2 = Pc("merged2", clock);
        await m2.EnsureLoadedAsync();
        Assert.Equal(1, m2.LastLedgerReconcile.Replayed);
        Assert.Equal(Expected(T0, clock.Now, 2), m2.GetEffectivePenalty(Pair), 6);          // X · Y 각 1회 = 0.35²
        // 재적재 · 재시작 → 재생 없음(삼중 감쇠 금지)
        var m2b = Pc("merged2", clock);
        await m2b.EnsureLoadedAsync();
        Assert.Equal(0, m2b.LastLedgerReconcile.Replayed);
        Assert.Equal(Expected(T0, clock.Now, 2), m2b.GetEffectivePenalty(Pair), 6);
        Assert.True(await m2b.ReloadFromDiskAsync());
        Assert.Equal(Expected(T0, clock.Now, 2), m2b.GetEffectivePenalty(Pair), 6);
        Assert.True(m2b.LedgerContains(Pair, "clip-x"));
        Assert.True(m2b.LedgerContains(Pair, "CLIP-Y"));
    }

    /// <summary>두 PC 폴더의 노트 · 장부를 충돌 병합 규칙으로 합쳐 대상 폴더에 기록 → 병합 노트 JSON 반환.</summary>
    private async Task<string> MergeInto(string target, string pcA, string pcB)
    {
        Directory.CreateDirectory(Path.Combine(_dir, target));
        var nb = PlutoConflictMergeService.MergeJson(PlutoMistakeNotebookService.FileName,
            await File.ReadAllTextAsync(PcFile(pcA, PlutoMistakeNotebookService.FileName)), await File.ReadAllTextAsync(PcFile(pcB, PlutoMistakeNotebookService.FileName)))!;
        var lg = PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName,
            await File.ReadAllTextAsync(PcFile(pcA, PlutoRehabilitationLedger.FileName)), await File.ReadAllTextAsync(PcFile(pcB, PlutoRehabilitationLedger.FileName)))!;
        Assert.NotNull(nb);
        Assert.NotNull(lg);
        await File.WriteAllTextAsync(PcFile(target, PlutoMistakeNotebookService.FileName), nb);
        await File.WriteAllTextAsync(PcFile(target, PlutoRehabilitationLedger.FileName), lg);
        return nb;
    }

    // ── ③ Drive 저장소 · 장부 충돌 사본 무재시작 병합 → 소유 허브(오답 노트) 재적재가 승격 + 재생 ──

    [Fact]
    public async Task CrossPc_LiveLedgerConflictMerge_HotReloadsOwnerNotebook_AndReplaysPeerRehabOnce()
    {
        var drive = Path.Combine(_dir, "live", "vault", "Pluto");
        var local = Path.Combine(_dir, "live", "local");
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(local);
        var clock = new Clock();
        var storage = new PlutoDriveStorageManager(drive, local);
        var nb = new PlutoMistakeNotebookService(storage, clock.Get);
        await nb.RecordPostMortemMistakeAsync(new[] { Pair }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B2 Δ-20.0p");
        clock.Now = T0.AddDays(1);
        Assert.True((await nb.RecordOrganicRecoveryAsync(new[] { Pair }, "this-pc-clip", 14, T0.AddHours(6))).Any);
        Assert.Equal(storage.ResolveDataPath(PlutoRehabilitationLedger.FileName), nb.LedgerStoragePath);
        Assert.True(File.Exists(nb.LedgerStoragePath));

        // 다른 PC 의 오프라인 복권(peer-clip) 이 충돌 사본으로 보관됨
        var peer = JsonSerializer.Serialize(Ledger((Pair, "peer-clip", T0.AddHours(8), T0.AddDays(1).AddHours(2))));
        var conflictDir = Path.Combine(local, PlutoDriveStorageManager.ConflictFolderName);
        Directory.CreateDirectory(conflictDir);
        var copy = Path.Combine(conflictDir, "pluto_rehab_ledger.conflict-20261102110000.json");
        File.WriteAllText(copy, peer);

        IPlutoDriveHealthService health = new PlutoDriveHealthService(null, null, storage) { HotReloadHubs = () => new IPlutoHotReloadable[] { nb } };
        clock.Now = T0.AddDays(2);
        var r = await health.MergeConflictLiveAsync(copy);
        Assert.Equal(PlutoConflictAction.MergeApplied, r.Action);
        Assert.Equal(2, nb.LedgerCount);
        Assert.Equal(1, nb.LastLedgerReconcile.Replayed);
        Assert.Equal(Expected(T0, clock.Now, 2), nb.GetEffectivePenalty(Pair), 6);
        var onDisk = File.ReadAllText(storage.ResolveDataPath(PlutoRehabilitationLedger.FileName));
        Assert.Contains("peer-clip", onDisk, StringComparison.Ordinal);
        Assert.Contains("this-pc-clip", onDisk, StringComparison.Ordinal);
        Assert.Contains("peer-clip", File.ReadAllText(storage.ResolveDataPath(PlutoMistakeNotebookService.FileName)), StringComparison.Ordinal);   // 재생 → 노트 목록에도 영속

        // 같은 사본 재병합(멱등) — 재적재해도 추가 감쇠 없음
        Assert.True(await nb.ReloadFromDiskAsync());
        Assert.Equal(0, nb.LastLedgerReconcile.Replayed);
        Assert.Equal(Expected(T0, clock.Now, 2), nb.GetEffectivePenalty(Pair), 6);
    }

    // ── ④ 감시기 자동 수집 → 조회 50 돌파 → 기획 채점 + 유기적 복권(페이지 새로고침 없이) ──

    /// <summary>실 수집기 효과 대역 — 호출 시 카탈로그 워터마크를 성숙(조회 500)시킨다.</summary>
    private sealed class MaturingImporter : IPlutoTelemetryBatchImporter
    {
        private readonly PlutoRenderCatalogService _catalog;
        private readonly string _clipId;
        public int Calls;
        public MaturingImporter(PlutoRenderCatalogService catalog, string clipId) { _catalog = catalog; _clipId = clipId; }

        public async Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Assert.True(await _catalog.UpdateYouTubeWatermarkAsync(_clipId, Wm(70, 60, 5.0, 500), ct));
            return new PlutoTelemetryBatchImportResult(1, 0, 0, 0, Array.Empty<string>()) { MaturityDeltaCount = 1, IsYouTubeStudioReport = true };
        }

        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private YouTubeDownloadsWatcherOptions WatchOpt(string name) => new()
    {
        Directory = Path.Combine(_dir, name, "Downloads"),
        StatePath = Path.Combine(_dir, name, "state", "downloads_watcher.json"),
        Debounce = TimeSpan.FromMilliseconds(60),
        SweepInterval = TimeSpan.Zero,
        ReadinessRetries = 30,
        ReadinessRetryDelay = TimeSpan.FromMilliseconds(50),
        StabilityProbe = TimeSpan.FromMilliseconds(20),
        UseFileSystemWatcher = false,
    };

    [Fact]
    public async Task Watcher_AutoIngestCrossingFiftyViews_TriggersBriefGradingAndOrganicRehabilitation()
    {
        var clock = new Clock { Now = T0.AddHours(-1) };
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "auto_nb.json"), clock.Get);
        await nb.RecordPostMortemMistakeAsync(new[] { Pair }, 2.0, PlutoMistakeNotebookService.DimensionCompletionDrop, "brief-postmortem: B0 Δ-20.0p");
        Assert.Contains(Pair, nb.GetSuppressedCompositeKeys());

        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "auto_catalog.json"));
        PlutoAppliedBriefArm? arm = new("brief-29", PlutoTopicBriefKinds.ParetoExploit, "단편", 1, Theme, Hook, Golden, "Lullaby", "Question", 50.0, new DateTimeOffset(T0));
        var at = new DateTimeOffset(T0.AddMinutes(1));
        var stamped = PlutoBriefOutcomeAttributionService.StampProvenance(Measured("brief-clip", Hook, Golden, null, at.UtcDateTime), arm, at, 1, out _, out _);
        await Put(catalog, stamped with { YouTubeWatermark = Wm(70, 60, 5.0, 30) }, "ytAUTOSWEEP1");   // 조회 30 → 미성숙

        using var svc = new PlutoBriefOutcomeAttributionService(catalog, nb);
        clock.Now = T0.AddDays(2);
        Assert.Equal(0, (await svc.SweepAsync()).EvaluatedCount);                                // 50 미만 → 채점 대기
        Assert.Equal(1, (await svc.GetHitRateAsync()).PendingClips);

        var opt = WatchOpt("auto");
        Directory.CreateDirectory(opt.Directory!);
        var importer = new MaturingImporter(catalog, "brief-clip");
        await using var w = new YouTubeDownloadsAutoWatcherService(importer, opt, svc);
        Assert.True(w.Start());
        await File.WriteAllBytesAsync(Path.Combine(opt.Directory!, "Content 2026-11-03.csv"), Export("ytAUTOSWEEP1", 500));
        Assert.Equal(1, await w.SweepNowAsync());

        Assert.Equal(1, importer.Calls);
        Assert.Equal(1, w.PostIngestSweepCount);
        var sweep = w.LastPostIngestSweep!;
        Assert.Equal(1, sweep.EvaluatedCount);
        Assert.Equal(1, sweep.HitCount);
        Assert.Contains(Pair, sweep.RehabilitatedKeys);
        var clip = System.Linq.Enumerable.First(await catalog.GetRecentClipsAsync(10), c => c.VideoId == "brief-clip");
        Assert.True(clip.AppliedBriefEvaluated);
        Assert.Equal(Expected(T0.AddHours(-1), clock.Now, 1), nb.GetEffectivePenalty(Pair), 6);
        Assert.DoesNotContain(Pair, nb.GetSuppressedCompositeKeys());
        Assert.True(nb.LedgerContains(Pair, "brief-clip"));
        Assert.Equal(1, (await svc.GetHitRateAsync()).EvaluatedBriefs);

        // 같은 내보내기 재감지 → 수집 · 스윕 없음(멱등)
        Assert.Equal(0, await w.SweepNowAsync());
        Assert.Equal(1, w.PostIngestSweepCount);
        // 직접 재스윕도 추가 채점 · 감쇠 없음
        var again = await svc.EvaluatePendingBriefOutcomesAndRehabilitationAsync();
        Assert.Equal(0, again.EvaluatedCount);
        Assert.Empty(again.RehabilitatedKeys);
    }

    // ── ⑤ 자체 추가 — 수집 후 스윕 단일 비행 코얼레싱 · 후행 재실행 · 실패 격리 · 수동 가져오기 결선 ──

    private sealed class GatedBriefs : IPlutoBriefOutcomeAttributionService
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Entered, Concurrent, MaxConcurrent;
        public bool ThrowOnSecond = true;

        public async Task<PlutoBriefSweepResult> EvaluatePendingBriefOutcomesAndRehabilitationAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref Entered);
            var c = Interlocked.Increment(ref Concurrent);
            if (c > MaxConcurrent) MaxConcurrent = c;
            try
            {
                if (n == 1) await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                if (n == 2 && ThrowOnSecond) throw new InvalidOperationException("sweep boom");
                return PlutoBriefSweepResult.Empty;
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }

        public void Start() { }
        public Task<PlutoBriefSweepResult> SweepAsync(CancellationToken ct = default) => EvaluatePendingBriefOutcomesAndRehabilitationAsync(ct);
        public Task<PlutoBriefHitRateSummary> GetHitRateAsync(CancellationToken ct = default) => Task.FromResult(PlutoBriefHitRateSummary.Empty);
        public PlutoBriefSweepResult? LastSweep => null;
        public PlutoSetupForecast EvaluateSetupForecast(string? theme, string? hook, string? look, string? bgm, string? renderMode) => PlutoSetupForecast.Prior;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class NoopImporter : IPlutoTelemetryBatchImporter
    {
        public Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
            => Task.FromResult(new PlutoTelemetryBatchImportResult(0, 0, 0, 0, Array.Empty<string>()));
        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private static PlutoTelemetryBatchImportResult Learned(int n) => new(n, n, 0, 0, Array.Empty<string>()) { IsYouTubeStudioReport = true };

    [Fact]
    public async Task CoArchitect_PostIngestSweep_SingleFlightCoalescesBurst_NeverLosesTrigger_AndIsolatesFailure()
    {
        var briefs = new GatedBriefs();
        var opt = WatchOpt("coalesce");
        await using var w = new YouTubeDownloadsAutoWatcherService(new NoopImporter(), opt, briefs);
        var imports = 0;
        Task<PlutoTelemetryBatchImportResult> Manual(int i)
            => w.RunExclusiveImportAsync(Export("ytBURST" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 100 + i), "m" + i + ".csv",
                _ => { Interlocked.Increment(ref imports); return Task.FromResult(Learned(1)); });

        var t1 = Manual(1);
        await WaitUntil(() => Volatile.Read(ref briefs.Entered) == 1);
        var burst = new[] { Manual(2), Manual(3), Manual(4) };
        await WaitUntil(() => Volatile.Read(ref imports) == 4);
        await Task.Delay(150);                                         // 후행 트리거가 '더러움' 표시를 마칠 시간
        Assert.False(t1.IsCompleted);                                  // 수동 가져오기는 스윕 완료까지 대기(결과가 화면에 반영된 뒤 반환)
        briefs.Release.SetResult();
        await t1;
        foreach (var t in burst) Assert.Equal(1, (await t).MatchedAndLearnedCount);   // 2회차 스윕 예외 → 가져오기 결과에는 영향 없음

        Assert.Equal(2, briefs.Entered);                               // 4 트리거 → 1회 + 후행 1회(무한 적층 없음 · 유실 없음)
        Assert.Equal(1, briefs.MaxConcurrent);                         // 동시 스윕 없음
        Assert.Equal(1, w.PostIngestSweepCount);
        Assert.Equal(1, w.PostIngestSweepFailureCount);
        Assert.Equal(0, w.GetHealth().QuarantinedCount);               // 스윕 실패가 수집 장부 · 격리 판정에 번지지 않음
        Assert.True(w.IsKnownHash(YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(Export("ytBURST4", 104))));

        // 학습 0건으로 확인된 수동 결과 → 스윕 생략 · 그 뒤 트리거는 정상 재개
        await w.RunExclusiveImportAsync(Export("ytZERO", 1), "z.csv", _ => Task.FromResult(new PlutoTelemetryBatchImportResult(1, 0, 1, 0, Array.Empty<string>())));
        Assert.Equal(2, briefs.Entered);
        await w.RunExclusiveImportAsync(Export("ytAFTER", 2), "after.csv", _ => Task.FromResult(Learned(1)));
        Assert.Equal(3, briefs.Entered);
        Assert.Equal(2, w.PostIngestSweepCount);

        // 채점 서비스 미연결(기존 생성자) → 무동작
        await using var legacy = new YouTubeDownloadsAutoWatcherService(new NoopImporter(), WatchOpt("legacy"));
        Assert.Equal(1, (await legacy.RunExclusiveImportAsync(Export("ytLEGACY", 3), "l.csv", _ => Task.FromResult(Learned(1)))).MatchedAndLearnedCount);
        Assert.Equal(0, legacy.PostIngestSweepCount);
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    // ── ⑥ 진단서 .md 원자 저장 — BOM · 경로 가림 · 디렉터리/링크 덮어쓰기 거부 · 같은 초 접미 ──

    private sealed class SpacedPathDrive : IPlutoDriveHealthService
    {
        public PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null)
        {
            var items = new List<PlutoDriveFileHealthItem>();
            foreach (var f in PlutoDriveHealthService.PlutoFiles)
                items.Add(new(PlutoDriveHealthService.CategoryPluto, f, @"G:\내 드라이브\DATA\Pluto\" + f, PlutoDriveFileStatus.Missing, 0, null, @"없음: G:\내 드라이브\DATA\Pluto\" + f + " (홍 길동 PC)"));
            return new PlutoDriveHealthReport(true, 0, items.Count, @"설정 미러 실패: C:\Users\홍 길동\AppData\Local\x.json 잠김", null, items, new DateTimeOffset(T0));
        }

        public Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default) => Task.FromResult(CheckHealth());
        public IReadOnlyList<PlutoConflictCopy> ListConflicts() => Array.Empty<PlutoConflictCopy>();
        public int PendingConflictResolutions => 0;
    }

    [Fact]
    public async Task DiagnosticExport_WritesAtomicBomMarkdown_RedactsPaths_AndNeverClobbersDirectoryFileOrSymlink()
    {
        var svc = new PlutoSystemDiagnosticService(new SpacedPathDrive(), null, null, null, () => T0);
        var outDir = Path.Combine(_dir, "diag out");
        var r1 = await svc.ExportSnapshotMarkdownFileAsync(outDir);
        Assert.True(r1.Ok, r1.MessageKorean);
        var stamp = T0.ToLocalTime();
        Assert.Equal(PlutoSystemDiagnosticService.ExportFileNameFor(stamp), r1.FileName);
        Assert.Matches(@"^pluto_diagnostic_\d{8}_\d{6}\.md$", r1.FileName!);
        Assert.DoesNotContain(_dir, r1.MessageKorean, StringComparison.OrdinalIgnoreCase);   // 토스트에 절대 경로 없음
        var bytes = await File.ReadAllBytesAsync(r1.FullPath!);
        Assert.Equal(r1.Bytes, bytes.LongLength);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);   // UTF-8 BOM
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith(PlutoSystemDiagnosticService.Title, text, StringComparison.Ordinal);
        Assert.DoesNotContain("드라이브\\", text, StringComparison.Ordinal);                  // 공백 경로 조각까지 가림
        Assert.DoesNotContain(@"DATA\Pluto", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"G:\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("홍 길동\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));                                         // 임시 파일 정리

        // 같은 초 재저장 → _2 (기존 파일 보존)
        var original = await File.ReadAllBytesAsync(r1.FullPath!);
        var r2 = await svc.ExportSnapshotMarkdownFileAsync(outDir);
        Assert.True(r2.Ok);
        Assert.Equal(PlutoSystemDiagnosticService.ExportFileNameFor(stamp, 2), r2.FileName);
        Assert.Equal(original, await File.ReadAllBytesAsync(r1.FullPath!));

        // 🔒 대상 이름을 디렉터리가 점유 → 건너뜀(디렉터리 덮기/삭제 없음)
        Directory.CreateDirectory(Path.Combine(outDir, PlutoSystemDiagnosticService.ExportFileNameFor(stamp, 3)));
        var r3 = PlutoSystemDiagnosticService.WriteSnapshotFile(outDir, "# x", stamp);
        Assert.True(r3.Ok);
        Assert.Equal(PlutoSystemDiagnosticService.ExportFileNameFor(stamp, 4), r3.FileName);
        Assert.True(Directory.Exists(Path.Combine(outDir, PlutoSystemDiagnosticService.ExportFileNameFor(stamp, 3))));

        // 🔒 대상 이름이 (끊긴) 심볼릭 링크 → 따라가 쓰지 않고 건너뜀 · 저장 폴더 자체가 링크 → 거부(권한 없는 환경은 링크 생성 생략)
        var victim = Path.Combine(_dir, "victim.txt");
        var link5 = Path.Combine(outDir, PlutoSystemDiagnosticService.ExportFileNameFor(stamp, 5));
        var linked = TryCreateSymlink(link5, victim, directory: false);
        var r5 = PlutoSystemDiagnosticService.WriteSnapshotFile(outDir, "# y", stamp);
        Assert.True(r5.Ok);
        Assert.Equal(PlutoSystemDiagnosticService.ExportFileNameFor(stamp, linked ? 6 : 5), r5.FileName);
        Assert.False(File.Exists(victim));                                                         // 링크 대상에 쓰지 않음
        var realDir = Path.Combine(_dir, "real-dir");
        Directory.CreateDirectory(realDir);
        var linkDir = Path.Combine(_dir, "link-dir");
        if (TryCreateSymlink(linkDir, realDir, directory: true))
        {
            var rl = PlutoSystemDiagnosticService.WriteSnapshotFile(linkDir, "# z", stamp);
            Assert.False(rl.Ok);
            Assert.Empty(Directory.GetFiles(realDir));
        }

        // 저장 위치 검증 — 상대 경로 · 파일 경로 거부
        Assert.False(PlutoSystemDiagnosticService.WriteSnapshotFile(@"relative\dir", "# r", stamp).Ok);
        Assert.False(PlutoSystemDiagnosticService.WriteSnapshotFile(r1.FullPath, "# f", stamp).Ok);
        Assert.Equal(original, await File.ReadAllBytesAsync(r1.FullPath!));
    }

    private static bool TryCreateSymlink(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    // ── ⑦ 자체 추가 — 공백 포함 절대 경로 가림(기본 Drive 볼트 'G:\내 드라이브\DATA' 가 조각으로 새던 결함) ──

    [Fact]
    public void CoArchitect_RedactPaths_SpacedAbsolutePathsDoNotLeakDirectorySegments()
    {
        Assert.Equal("없음: ‹path›/pluto_series_arc.json 확인", PlutoSystemDiagnosticService.RedactPaths(@"없음: G:\내 드라이브\DATA\Pluto\pluto_series_arc.json 확인"));
        Assert.Equal("‹path›/x.json 잠김", PlutoSystemDiagnosticService.RedactPaths(@"C:\Users\홍 길동\AppData\Local\x.json 잠김"));
        Assert.Equal("‹path›/b c.md", PlutoSystemDiagnosticService.RedactPaths("/home/a b/b c.md"));   // 슬래시 경로도 디렉터리 조각('a b') 미노출
        Assert.Equal("‹path›/a.json 와 ‹path›/b.json", PlutoSystemDiagnosticService.RedactPaths(@"\\nas\share\a.json 와 /home/me/b.json"));   // Step 28 동작 유지
        Assert.Equal("훅 a/b 그대로", PlutoSystemDiagnosticService.RedactPaths("훅 a/b 그대로"));
        Assert.Equal("‹path›/x.json  다음\\칸", PlutoSystemDiagnosticService.RedactPaths(@"C:\d\x.json  다음\칸"));   // 공백 2칸 → 경로 연속 아님
        Assert.Equal("‹path›/a.json ‹path›/b.json", PlutoSystemDiagnosticService.RedactPaths(@"C:\a.json D:\b.json"));   // 다음 토큰이 절대 경로 → 별도 가림
    }

    // ── ⑧ 자체 추가 — 재생 세대 가드(다른 PC 의 새 벌점이 이김) · 포화 가드(FIFO 밀림 재감쇠 금지) · 레거시 역충전 ──

    [Fact]
    public async Task CoArchitect_LedgerReplay_EpochGuard_FreshPeerPenaltySupersedesOlderRehab()
    {
        var clock = new Clock();
        var nb = Pc("epoch", clock);
        await nb.RecordPostMortemMistakeAsync(new[] { Pair }, 2.0, PlutoMistakeNotebookService.DimensionLowCtr, "brief-postmortem: B5 Δ-15.0p");
        clock.Now = T0.AddDays(5);
        await nb.RecordNegativeFeedbackAsync(new[] { Pair }, 1.0);   // 다른 PC 에서 들어온 '새 벌점'(T0+5d)
        var before = nb.GetEffectivePenalty(Pair);
        // 상대 PC 장부: T0+1d 렌더 클립으로 T0+2d 에 복권(새 벌점 이전 세대) + 렌더 시각 미상 레거시 기록
        File.WriteAllText(PcFile("epoch", PlutoRehabilitationLedger.FileName), JsonSerializer.Serialize(Ledger(
            (Pair, "old-gen-clip", T0.AddDays(1), T0.AddDays(2)), (Pair, "unknown-render", default, T0.AddDays(6)))));

        var reopened = Pc("epoch", clock);
        await reopened.EnsureLoadedAsync();
        Assert.Equal(0, reopened.LastLedgerReconcile.Replayed);
        Assert.Equal(2, reopened.LastLedgerReconcile.SkippedEpoch);
        Assert.Equal(before, reopened.GetEffectivePenalty(Pair), 9);                                // 새 벌점 유지
        // 장부에 있던 이전 세대 클립의 직접 복권도 인과 가드로 차단(같은 규칙)
        Assert.Equal(1, (await reopened.RecordOrganicRecoveryAsync(new[] { Pair }, "old-gen-clip", 20, T0.AddDays(1))).SkippedCausality);
        // 새 세대 클립은 정상 복권 → 장부 기록
        clock.Now = T0.AddDays(7);
        Assert.True((await reopened.RecordOrganicRecoveryAsync(new[] { Pair }, "new-gen-clip", 20, T0.AddDays(6))).Any);
        Assert.True(reopened.LedgerContains(Pair, "new-gen-clip"));
    }

    [Fact]
    public async Task CoArchitect_LedgerReplay_SaturationGuard_AndLegacyStep28NotebookBackfillsLedger()
    {
        var clock = new Clock();
        // 레거시(Step 28) 노트 — 장부 파일 없이 목록만 있음 → 적재 시 역충전 · 재생 없음
        var legacy = Pc("legacy", clock);
        await legacy.RecordPostMortemMistakeAsync(new[] { Pair }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B6 Δ-19.0p");
        clock.Now = T0.AddDays(1);
        Assert.True((await legacy.RecordOrganicRecoveryAsync(new[] { Pair }, "legacy-clip", 20, T0.AddHours(3))).Any);
        File.Delete(legacy.LedgerStoragePath);
        var penalty = legacy.GetEffectivePenalty(Pair);
        var reopened = Pc("legacy", clock);
        await reopened.EnsureLoadedAsync();
        Assert.Equal(1, reopened.LastLedgerReconcile.Backfilled);
        Assert.Equal(0, reopened.LastLedgerReconcile.Replayed);
        Assert.Equal(penalty, reopened.GetEffectivePenalty(Pair), 9);
        Assert.True(File.Exists(reopened.LedgerStoragePath));                                       // 역충전분 영속
        Assert.Contains("legacy-clip", File.ReadAllText(reopened.LedgerStoragePath), StringComparison.Ordinal);

        // 포화 — 엔트리 목록이 FIFO 상한(32)에 닿음 + 장부엔 목록에서 밀려난 클립 → 재생 · 직접 복권 모두 재감쇠 금지
        var path = PcFile("legacy", PlutoMistakeNotebookService.FileName);
        var snap = JsonSerializer.Deserialize<PlutoMistakeNotebookService.Snapshot>(File.ReadAllText(path))!;
        var entry = snap.Entries![Pair];
        entry.RehabilitatedClipIds = new List<string>();
        for (var i = 0; i < PlutoMistakeNotebookService.MaxRehabilitatedClipIds; i++) entry.RehabilitatedClipIds.Add("kept-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        entry.RawPenalty = 1.5;   // 아직 회복 중(감쇠 대상)
        File.WriteAllText(path, JsonSerializer.Serialize(snap));
        File.WriteAllText(reopened.LedgerStoragePath, JsonSerializer.Serialize(Ledger((Pair, "evicted-clip", T0.AddHours(4), T0.AddHours(20)))));
        var saturated = Pc("legacy", clock);
        await saturated.EnsureLoadedAsync();
        Assert.Equal(1, saturated.LastLedgerReconcile.SkippedSaturated);
        Assert.Equal(0, saturated.LastLedgerReconcile.Replayed);
        var held = saturated.GetEffectivePenalty(Pair);
        var direct = await saturated.RecordOrganicRecoveryAsync(new[] { Pair }, "evicted-clip", 20, T0.AddHours(4));
        Assert.False(direct.Any);
        Assert.Equal(1, direct.SkippedDuplicate);
        Assert.Equal(held, saturated.GetEffectivePenalty(Pair), 9);
    }

    // ── ⑨ 마크업 · DI 결선 ──

    [Fact]
    public void Markup_SaveDiagnosticButton_AndWatcherReceivesBriefOutcomeServiceOnBothHosts()
    {
        var root = RepoRoot();
        var st = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        var copy = st.IndexOf("[🩺 플루토 진단 리포트 복사]", StringComparison.Ordinal);
        var save = st.IndexOf("[💾 진단서 파일 저장(.md)]", StringComparison.Ordinal);
        Assert.True(copy > 0 && save > copy);                                                      // 복사 버튼 옆
        Assert.Contains("PlutoDiagnostics.ExportSnapshotMarkdownFileAsync()", st, StringComparison.Ordinal);
        Assert.Contains("data-pluto-diagnostic-save=\"1\"", st, StringComparison.Ordinal);

        foreach (var di in new[] { Path.Combine(root, "PromptFlowStudio", "MainForm.cs"), Path.Combine(root, "PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var text = File.ReadAllText(di);
            var at = text.IndexOf("new PromptFlowStudio.Services.Pluto.YouTubeDownloadsAutoWatcherService(", StringComparison.Ordinal);
            Assert.True(at > 0);
            var line = text.Substring(at, text.IndexOf('\n', at) - at);
            Assert.Contains("IPlutoBriefOutcomeAttributionService", line, StringComparison.Ordinal);
        }
    }
}
