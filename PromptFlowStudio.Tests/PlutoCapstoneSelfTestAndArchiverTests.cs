using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🧪 Step 30 — 동반 허브 관측 · 수집 후 폴더 정리 · 샌드박스 플라이휠 자가진단 · 자체 추가 안전장치.</summary>
public sealed class PlutoCapstoneSelfTestAndArchiverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_capstone_" + Guid.NewGuid().ToString("N"));
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoCapstoneSelfTestAndArchiverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Sub(params string[] parts)
    {
        var p = _dir;
        foreach (var s in parts) p = Path.Combine(p, s);
        Directory.CreateDirectory(p);
        return p;
    }

    private static byte[] Export(string id, long views)
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + id + ",가을 산책 브이로그 #shorts,\"Nov 2, 2026\"," + views.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",60,5000,5.0,66.0,10\n");

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

    private const string QueueJson2 = "{\"Version\":2,\"Rows\":[{\"YouTubeVideoId\":\"ytAAAAAAAA1\",\"Title\":\"a\"},{\"YouTubeVideoId\":\"ytAAAAAAAA2\",\"Title\":\"b\"}],\"Resolved\":[]}";
    private const string LedgerJson1 = "{\"Version\":1,\"Records\":[{\"Key\":\"pair:x\",\"ClipId\":\"c1\",\"ClipRenderedUtc\":\"2026-11-01T00:00:00Z\",\"AppliedAtUtc\":\"2026-11-02T00:00:00Z\",\"SurpriseDelta\":12}]}";
    private const string LedgerJson3 = "{\"Version\":1,\"Records\":[{\"Key\":\"pair:x\",\"ClipId\":\"c1\"},{\"Key\":\"pair:x\",\"ClipId\":\"c2\"},{\"Key\":\"pair:y\",\"ClipId\":\"c3\"}]}";

    // ── ① 동반 허브 관측 — 9 핵심 허브 불변 · 동기화 상태 · 항목 수 · 진단서 소절 · 경로 가림 ──

    [Fact]
    public void CompanionObservability_ReportsSyncStateAndEntryCounts_InHealthAndRedactedDiagnostic_KeepingNineCoreHubs()
    {
        var drive = Sub("vault", "Pluto");
        var local = Sub("local");
        var studio = Sub("studio");
        File.WriteAllText(Path.Combine(drive, PlutoYouTubeUnmatchedQueueService.FileName), QueueJson2);
        File.WriteAllText(Path.Combine(local, PlutoYouTubeUnmatchedQueueService.FileName), QueueJson2);
        File.WriteAllText(Path.Combine(drive, PlutoRehabilitationLedger.FileName), LedgerJson3);
        File.WriteAllText(Path.Combine(local, PlutoRehabilitationLedger.FileName), LedgerJson1);

        var svc = new PlutoDriveHealthService { LocalMirrorRoot = local };
        var rep = svc.CheckHealth(drive, studio);
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(PlutoDriveHealthService.CorePlutoFileCount + PlutoDriveHealthService.StudioFiles.Length, rep.TotalCount);   // 동반 파일은 집계 밖
        Assert.DoesNotContain(rep.Items, i => Array.IndexOf(PlutoDriveHealthService.CompanionSyncFiles, i.FileName) >= 0);
        Assert.Equal(2, rep.Companions.Count);

        var q = rep.Companions[0];
        Assert.Equal(PlutoYouTubeUnmatchedQueueService.FileName, q.FileName);
        Assert.Equal(PlutoCompanionSyncState.InSync, q.State);
        Assert.True(q.LocalPresent && q.DrivePresent);
        Assert.Equal(2, q.EntryCount);
        Assert.Equal(q.LocalSha256, q.DriveSha256);
        Assert.Equal(64, q.DriveSha256!.Length);

        var l = rep.Companions[1];
        Assert.Equal(PlutoRehabilitationLedger.FileName, l.FileName);
        Assert.Equal(PlutoCompanionSyncState.Diverged, l.State);   // 기준선 없음 + 해시 다름 → 분기
        Assert.Equal(3, l.EntryCount);                             // Drive 사본 기준
        Assert.Equal(1, rep.CompanionInSyncCount);
        Assert.Contains("1/2 일치", rep.CompanionSummaryKorean, StringComparison.Ordinal);

        var md = PlutoSystemDiagnosticService.BuildMarkdown(new PlutoDiagnosticInputs(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc)) { Drive = rep });
        var sec = md.Substring(md.IndexOf(PlutoSystemDiagnosticService.CompanionHeading, StringComparison.Ordinal));
        Assert.True(md.IndexOf("Pluto 허브 0/9 정상", StringComparison.Ordinal) >= 0);
        Assert.Contains("| " + PlutoYouTubeUnmatchedQueueService.FileName + " | ✅ 일치 |", sec, StringComparison.Ordinal);
        Assert.Contains("| " + PlutoRehabilitationLedger.FileName + " | ⚔️ 분기 |", sec, StringComparison.Ordinal);
        Assert.Contains(" | 3 | ", sec, StringComparison.Ordinal);
        Assert.Contains(q.DriveSha256.Substring(0, 12), sec, StringComparison.Ordinal);
        Assert.DoesNotContain(q.DriveSha256, md, StringComparison.Ordinal);   // 전체 해시 미노출(12자)
        Assert.DoesNotContain(_dir, md, StringComparison.OrdinalIgnoreCase);   // 🔒 경로 가림
        Assert.Equal(1, CountOf(md, "## 1. ☁️ Drive"));

        // 미생성 · 손상 · 드라이브 없음
        File.Delete(Path.Combine(drive, PlutoYouTubeUnmatchedQueueService.FileName));
        File.Delete(Path.Combine(local, PlutoYouTubeUnmatchedQueueService.FileName));
        File.WriteAllText(Path.Combine(drive, PlutoRehabilitationLedger.FileName), "{ broken");
        var rep2 = svc.CheckHealth(drive, studio);
        Assert.Equal(PlutoCompanionSyncState.NotCreated, rep2.Companions[0].State);
        Assert.Equal(PlutoCompanionSyncState.Corrupt, rep2.Companions[1].State);
        Assert.False(rep2.Companions[1].IsHealthy);
        var rep3 = svc.CheckHealth(Path.Combine(_dir, "nope"), studio);
        Assert.Equal(PlutoCompanionSyncState.DriveUnavailable, rep3.Companions[0].State);
        Assert.Equal(1, rep3.Companions[1].EntryCount);   // 로컬 사본만 점검
    }

    // ── ② 자체 추가 A — 🧾 동기화 장부 기준선으로 '단방향 대기'와 '진짜 분기' 구분 ──

    [Fact]
    public void CoArchitect_CompanionBaseline_DistinguishesPendingOneWaySyncFromTrueDivergence()
    {
        var drive = Sub("g", "DATA", "Pluto");
        var local = Sub("fallback");
        var storage = new PlutoDriveStorageManager(drive, local, null);
        foreach (var d in new[] { drive, local }) File.WriteAllText(Path.Combine(d, PlutoRehabilitationLedger.FileName), LedgerJson1);
        Assert.Equal(1, storage.BootstrapSyncManifest(new[] { PlutoRehabilitationLedger.FileName }));   // 기준선 = 같은 내용
        var svc = new PlutoDriveHealthService(null, null, storage);
        Assert.Equal(PlutoCompanionSyncState.InSync, svc.CheckHealth(drive, Sub("st")).Companions[1].State);

        File.WriteAllText(Path.Combine(local, PlutoRehabilitationLedger.FileName), LedgerJson3);   // 오프라인 로컬 복권
        var ahead = svc.CheckHealth(drive, Sub("st")).Companions[1];
        Assert.Equal(PlutoCompanionSyncState.LocalAhead, ahead.State);
        Assert.True(ahead.IsHealthy);
        Assert.Equal(1, ahead.EntryCount);

        File.WriteAllText(Path.Combine(local, PlutoRehabilitationLedger.FileName), LedgerJson1);
        File.WriteAllText(Path.Combine(drive, PlutoRehabilitationLedger.FileName), LedgerJson3);   // 다른 PC 가 Drive 갱신
        Assert.Equal(PlutoCompanionSyncState.DriveAhead, svc.CheckHealth(drive, Sub("st")).Companions[1].State);

        File.WriteAllText(Path.Combine(local, PlutoRehabilitationLedger.FileName), QueueJson2);    // 양쪽 모두 기준선 이탈
        var div = svc.CheckHealth(drive, Sub("st")).Companions[1];
        Assert.Equal(PlutoCompanionSyncState.Diverged, div.State);
        Assert.False(div.IsHealthy);
        Assert.Equal(3, PlutoDriveHealthService.CountEntries(Encoding.UTF8.GetBytes("﻿" + LedgerJson3)));   // BOM 허용
    }

    // ── ③ 수집 후 폴더 정리 — ON: 수집 완료분만 접미 충돌 회피 이동 · 실패/격리/무관/중복은 그대로 · OFF: 무동작 ──

    private sealed class ScriptedImporter : IPlutoTelemetryBatchImporter
    {
        public int Calls;
        public bool QueueFailed;
        public async Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            if (fileName is not null && fileName.Contains("corrupt", StringComparison.Ordinal)) throw new InvalidDataException("형식 오류");
            if (fileName is not null && fileName.Contains("flaky", StringComparison.Ordinal)) throw new IOException("일시 실패");
            return new PlutoTelemetryBatchImportResult(1, 1, 0, 0, Array.Empty<string>()) { IsYouTubeStudioReport = true, UnmatchedQueuePersistFailed = QueueFailed };
        }

        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private YouTubeDownloadsWatcherOptions WatchOpt(string name) => new()
    {
        Directory = Sub(name, "Downloads"),
        StatePath = Path.Combine(_dir, name, "state", "downloads_watcher.json"),
        Debounce = TimeSpan.FromMilliseconds(60),
        SweepInterval = TimeSpan.Zero,
        ReadinessRetries = 30,
        ReadinessRetryDelay = TimeSpan.FromMilliseconds(30),
        StabilityProbe = TimeSpan.FromMilliseconds(15),
        UseFileSystemWatcher = false,
    };

    [Fact]
    public async Task Archiver_WhenEnabled_MovesOnlyIngestedExportsWithCollisionSafeSuffix_LeavesOthers_AndDoesNothingWhenDisabled()
    {
        // OFF(기본) — 수집은 되지만 원본 그대로
        var off = WatchOpt("off");
        await using (var w0 = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), off))
        {
            Assert.False(w0.ArchiveIngestedExports);
            Assert.True(w0.Start());
            File.WriteAllBytes(Path.Combine(off.Directory!, "Content 2026-11-03.csv"), Export("ytOFFOFFOF1", 500));
            Assert.Equal(1, await w0.SweepNowAsync());
            Assert.True(File.Exists(Path.Combine(off.Directory!, "Content 2026-11-03.csv")));
            Assert.False(Directory.Exists(Path.Combine(off.Directory!, YouTubeDownloadsAutoWatcherService.ArchiveFolderName)));
            Assert.Null(w0.LastArchiveOutcome);
        }
        Assert.Contains("\"archive\":false", File.ReadAllText(off.StatePath!), StringComparison.Ordinal);

        // ON — 정리 폴더에 같은 이름 파일 · 같은 이름_2 '폴더'가 이미 있음 → _3
        var opt = WatchOpt("on");
        var dl = opt.Directory!;
        var arch = Path.Combine(dl, YouTubeDownloadsAutoWatcherService.ArchiveFolderName);
        Directory.CreateDirectory(arch);
        File.WriteAllText(Path.Combine(arch, "Content 2026-11-03.csv"), "previous");
        Directory.CreateDirectory(Path.Combine(arch, "Content 2026-11-03_2.csv"));
        var importer = new ScriptedImporter();
        await using (var w = new YouTubeDownloadsAutoWatcherService(importer, opt))
        {
            await w.SetArchiveIngestedExportsAsync(true);
            Assert.True(w.ArchiveIngestedExports);
            Assert.True(w.Start());
            var good = Export("ytGOODGOOD1", 500);
            File.WriteAllBytes(Path.Combine(dl, "Content 2026-11-03.csv"), good);
            File.WriteAllBytes(Path.Combine(dl, "Content 2026-11-03 corrupt.csv"), Export("ytCORRUPT01", 500));   // 격리
            File.WriteAllBytes(Path.Combine(dl, "Content 2026-11-03 flaky.csv"), Export("ytFLAKYFLA1", 500));     // 일시 실패
            File.WriteAllText(Path.Combine(dl, "groceries.csv"), "item,qty\nmilk,2\n");                         // 무관
            File.WriteAllText(Path.Combine(dl, "readme.txt"), "hello");                                         // 비 대상 확장자
            Assert.Equal(1, await w.SweepNowAsync());

            Assert.False(File.Exists(Path.Combine(dl, "Content 2026-11-03.csv")));
            var moved = Path.Combine(arch, "Content 2026-11-03_3.csv");
            Assert.Equal(good, File.ReadAllBytes(moved));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(arch, "Content 2026-11-03.csv")));   // 기존 파일 · 폴더 덮어쓰기 없음
            Assert.True(Directory.Exists(Path.Combine(arch, "Content 2026-11-03_2.csv")));
            foreach (var stay in new[] { "Content 2026-11-03 corrupt.csv", "Content 2026-11-03 flaky.csv", "groceries.csv", "readme.txt" })
                Assert.True(File.Exists(Path.Combine(dl, stay)), stay);
            Assert.Equal(1, w.ArchivedCount);
            Assert.Equal(PlutoIngestArchiveOutcome.Archived, w.LastArchiveOutcome);
            Assert.Equal(1, w.GetHealth().QuarantinedCount);

            // 같은 내용 사본(중복 다운로드) → 수집 안 됨 → 옮기지 않음 · 정리 폴더는 재수집 대상 아님
            File.WriteAllBytes(Path.Combine(dl, "Content 2026-11-03 (1).csv"), good);
            Assert.Equal(0, await w.SweepNowAsync());
            Assert.True(File.Exists(Path.Combine(dl, "Content 2026-11-03 (1).csv")));
            Assert.Equal(1, w.ArchivedCount);
            Assert.True(w.GetHealth().ArchiveEnabled);
        }
        Assert.Equal(4, importer.Calls);   // good · corrupt · flaky · flaky 재시도(일시 실패는 다음 스윕 재시도) — 무관/중복은 수집기 미호출

        // 토글 영속 — 재시작 후에도 ON
        await using var again = new YouTubeDownloadsAutoWatcherService(new ScriptedImporter(), opt);
        Assert.True(again.ArchiveIngestedExports);
    }

    // ── ④ 자체 추가 B — 🧬 내용 동일성 재검증 · 🧯 영속 실패 거부권 · 🔗 링크/경로 안전 ──

    [Fact]
    public async Task CoArchitect_Archiver_RefusesChangedContent_NonDurableIngest_LockedFiles_AndUnsafeLocations()
    {
        var dl = Sub("guard", "Downloads");
        var path = Path.Combine(dl, "Table data.csv");
        var bytes = Export("ytGUARDGUA1", 500);
        File.WriteAllBytes(path, bytes);
        var sha = YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(bytes);

        // 🧬 수집 후 브라우저가 같은 이름으로 새 내보내기를 덮어씀 → 해시 불일치 → 이동 거부
        var newer = Export("ytGUARDGUA2", 900);
        File.WriteAllBytes(path, newer);
        Assert.Equal(PlutoIngestArchiveOutcome.ContentChanged, YouTubeDownloadsAutoWatcherService.TryArchiveIngestedFile(dl, path, sha, out var none));
        Assert.Null(none);
        Assert.Equal(newer, File.ReadAllBytes(path));

        // 🔒 다른 프로세스가 쓰기 핸들 보유 → 잠김 → 그대로
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.Equal(PlutoIngestArchiveOutcome.Locked, YouTubeDownloadsAutoWatcherService.TryArchiveIngestedFile(dl, path, YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(newer), out _));
        Assert.True(File.Exists(path));

        // 🚧 감시 폴더 밖 · 하위 폴더 · 정리 폴더 자리가 파일 → 거부
        var outside = Path.Combine(Sub("elsewhere"), "Table data.csv");
        File.WriteAllBytes(outside, newer);
        Assert.Equal(PlutoIngestArchiveOutcome.Unsafe, YouTubeDownloadsAutoWatcherService.TryArchiveIngestedFile(dl, outside, YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(newer), out _));
        var dl2 = Sub("guard2", "Downloads");
        File.WriteAllText(Path.Combine(dl2, YouTubeDownloadsAutoWatcherService.ArchiveFolderName), "i am a file");
        var p2 = Path.Combine(dl2, "Table data.csv");
        File.WriteAllBytes(p2, newer);
        Assert.Equal(PlutoIngestArchiveOutcome.Unsafe, YouTubeDownloadsAutoWatcherService.TryArchiveIngestedFile(dl2, p2, YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(newer), out _));
        Assert.True(File.Exists(p2));

        // ✅ 정상 — 같은 해시면 이동
        Assert.Equal(PlutoIngestArchiveOutcome.Archived, YouTubeDownloadsAutoWatcherService.TryArchiveIngestedFile(dl, path, YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(newer), out var moved));
        Assert.Equal(Path.Combine(dl, YouTubeDownloadsAutoWatcherService.ArchiveFolderName, "Table data.csv"), moved);
        Assert.Equal(PlutoIngestArchiveOutcome.SourceMissing, YouTubeDownloadsAutoWatcherService.TryArchiveIngestedFile(dl, path, sha, out _));

        // 🧯 미매칭 대기열 영속 실패([부분 구제]로 삼켜짐) → 감시기가 이동 거부
        var opt = WatchOpt("veto");
        var importer = new ScriptedImporter { QueueFailed = true };
        await using var w = new YouTubeDownloadsAutoWatcherService(importer, opt);
        await w.SetArchiveIngestedExportsAsync(true);
        Assert.True(w.Start());
        File.WriteAllBytes(Path.Combine(opt.Directory!, "Content 2026-11-04.csv"), Export("ytVETOVETO1", 500));
        Assert.Equal(1, await w.SweepNowAsync());
        Assert.Equal(PlutoIngestArchiveOutcome.NotDurable, w.LastArchiveOutcome);
        Assert.True(File.Exists(Path.Combine(opt.Directory!, "Content 2026-11-04.csv")));
        Assert.Equal(0, w.ArchivedCount);
        Assert.Equal(1, w.ArchiveSkippedCount);

        // 실 수집기 — 대기열 파일 자리를 읽을 수 없음(폴더가 점유) → [부분 구제]로 학습은 유지 · 결과에 영속 실패 표식
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "veto_catalog.json"));
        var nb = new PlutoMistakeNotebookService(Path.Combine(_dir, "veto_nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(null, nb, null);
        var qPath = Sub("veto_queue.json");
        using var queue = new PlutoYouTubeUnmatchedQueueService(qPath, catalog, loop);
        var real = new PlutoTelemetryBatchImporter(catalog, loop, queue);
        var r = await real.ImportYouTubeStudioFileAsync(Encoding.UTF8.GetBytes(EnHeader + "\nytSTRAYSTR1,무관한 영상 제목입니다,\"Nov 2, 2026\",300,40,900,3.0,50,2\n"), "Table data.csv");
        Assert.Equal(1, r.UnmatchedCount);
        Assert.True(r.UnmatchedQueuePersistFailed);
        Assert.True(Directory.Exists(qPath));

        // 정상 대기열 → 표식 없음
        using var okQueue = new PlutoYouTubeUnmatchedQueueService(Path.Combine(_dir, "ok_queue.json"), catalog, loop);
        var r2 = await new PlutoTelemetryBatchImporter(catalog, loop, okQueue).ImportYouTubeStudioFileAsync(Encoding.UTF8.GetBytes(EnHeader + "\nytSTRAYSTR2,또 다른 무관한 영상,\"Nov 2, 2026\",300,40,900,3.0,50,2\n"), "Table data.csv");
        Assert.Equal(1, r2.QueuedUnmatchedCount);
        Assert.False(r2.UnmatchedQueuePersistFailed);
        Assert.False(okQueue.LastPersistFailed);
    }

    // ── ⑤ 샌드박스 전 단계(Step 32 = 8단계) 플라이휠 자가진단 — 전 단계 PASS · 임시 폴더 삭제 · 라이브 무접촉 ──

    [Fact]
    public async Task FlywheelSelfTest_PassesAllSevenStagesInIsolatedSandbox_DeletesIt_AndLeavesLiveStateUntouched()
    {
        var live = Sub("live");
        var liveFile = Path.Combine(live, PlutoMistakeNotebookService.FileName);
        File.WriteAllText(liveFile, "{\"live\":true}");
        var stamp = File.GetLastWriteTimeUtc(liveFile);
        var parent = Sub("tmp");
        var svc = new PlutoFlywheelSelfTestService(parent, new[] { live });

        var t1 = svc.RunAsync();
        var t2 = svc.RunAsync();   // 🧷 단일 비행
        Assert.Same(t1, t2);
        var rep = await t1;
        Assert.False(svc.IsRunning);

        var why = string.Join(" / ", Array.ConvertAll(new List<PlutoFlywheelStageResult>(rep.Stages).ToArray(), s => s.Index + ":" + s.StatusLabel + ":" + s.Detail));
        Assert.True(rep.AllPassed, why);
        Assert.Equal(PlutoFlywheelSelfTestService.StageCount, rep.Stages.Count);
        for (var i = 0; i < rep.Stages.Count; i++)
        {
            Assert.Equal(i + 1, rep.Stages[i].Index);
            Assert.Equal(PlutoFlywheelSelfTestService.StageNames[i], rep.Stages[i].Name);
            Assert.True(rep.Stages[i].Passed);
            Assert.True(rep.Stages[i].ElapsedMs >= 0);
            Assert.DoesNotContain(parent, rep.Stages[i].Detail, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(rep.SandboxCleaned);
        Assert.True(rep.LiveStateUntouched);
        Assert.NotNull(svc.LastSandboxPath);
        Assert.StartsWith(parent, svc.LastSandboxPath!, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(svc.LastSandboxPath));
        Assert.Empty(Directory.GetFileSystemEntries(parent));
        Assert.Equal("{\"live\":true}", File.ReadAllText(liveFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(liveFile));
        Assert.Single(Directory.GetFileSystemEntries(live));
        Assert.Contains("PASS " + PlutoFlywheelSelfTestService.StageCount + "/" + PlutoFlywheelSelfTestService.StageCount, rep.SummaryKorean, StringComparison.Ordinal);
        Assert.Same(rep, svc.LastReport);

        // 반복 실행도 결정론 PASS(전역 상태 누수 없음 · 매번 새 샌드박스)
        var first = svc.LastSandboxPath;
        var rep2 = await svc.RunAsync();
        Assert.True(rep2.AllPassed);
        Assert.NotEqual(first, svc.LastSandboxPath);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    // ── ⑥ 자체 추가 C — 🧹 샌드박스 소유 증명 삭제(표식 토큰 · 접두 · 부모 · 링크) + 🔍 라이브 지문 ──

    [Fact]
    public void CoArchitect_SandboxDeletion_RequiresOwnershipProof_AndLiveFingerprintDetectsChanges()
    {
        var parent = Sub("p");
        var token = Guid.NewGuid().ToString("N");
        var root = PlutoFlywheelSelfTestService.CreateSandbox(parent, token);
        File.WriteAllText(Path.Combine(root, "x.json"), "{}");

        Assert.False(PlutoFlywheelSelfTestService.TryDeleteSandbox(parent, root, "wrong-token"));     // 표식 불일치
        Assert.False(PlutoFlywheelSelfTestService.TryDeleteSandbox(Sub("other"), root, token));       // 부모 밖
        Assert.True(Directory.Exists(root));

        var impostor = Sub("p", "user_data");                                                          // 접두 없음(사용자 폴더 오인 삭제 방지)
        File.WriteAllText(Path.Combine(impostor, PlutoFlywheelSelfTestService.SandboxMarkerFileName), token);
        Assert.False(PlutoFlywheelSelfTestService.TryDeleteSandbox(parent, impostor, token));
        Assert.True(Directory.Exists(impostor));

        var noMarker = Sub("p", PlutoFlywheelSelfTestService.SandboxPrefix + "nomarker");
        Assert.False(PlutoFlywheelSelfTestService.TryDeleteSandbox(parent, noMarker, token));
        Assert.True(Directory.Exists(noMarker));

        Assert.True(PlutoFlywheelSelfTestService.TryDeleteSandbox(parent, root, token));
        Assert.False(Directory.Exists(root));
        Assert.True(PlutoFlywheelSelfTestService.TryDeleteSandbox(parent, root, token));   // 이미 없음 → 정리됨

        var live = Sub("livefp");
        File.WriteAllText(Path.Combine(live, "a.json"), "1");
        var before = PlutoFlywheelSelfTestService.Fingerprint(new[] { live, Path.Combine(_dir, "missing") });
        Assert.True(PlutoFlywheelSelfTestService.FingerprintEquals(before, PlutoFlywheelSelfTestService.Fingerprint(new[] { live })));
        File.WriteAllText(Path.Combine(live, "a.json"), "12");
        Assert.False(PlutoFlywheelSelfTestService.FingerprintEquals(before, PlutoFlywheelSelfTestService.Fingerprint(new[] { live })));
        File.WriteAllText(Path.Combine(live, "b.json"), "1");
        Assert.False(PlutoFlywheelSelfTestService.FingerprintEquals(before, PlutoFlywheelSelfTestService.Fingerprint(new[] { live })));

        // 기본 라이브 루트는 로컬 앱 데이터만(Drive 경로 미포함)
        foreach (var r in PlutoFlywheelSelfTestService.DefaultLiveRoots())
            Assert.DoesNotContain(PlutoDriveStorageManager.DriveVaultRoot, r, StringComparison.OrdinalIgnoreCase);
    }

    // ── ⑦ 마크업 · DI 결선 — Settings 동반 요약(9 허브 바로 아래) · 자가진단 버튼 · Workspace 정리 토글(헤더 밖) ──

    [Fact]
    public void Markup_CompanionSummaryBelowHubList_SelfTestButton_ArchiveToggleOutsideHeader_AndDiRegisteredInBothHosts()
    {
        var root = RepoRoot();
        var settings = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        var hubList = settings.IndexOf("<div class=\"dh-list\">", StringComparison.Ordinal);
        var comp = settings.IndexOf("data-drive-companions=", StringComparison.Ordinal);
        var self = settings.IndexOf("data-flywheel-selftest-run=\"1\"", StringComparison.Ordinal);
        Assert.True(hubList > 0 && comp > hubList && self > comp);
        Assert.Equal(1, CountOf(settings, "[🧪 플라이휠 자가진단 실행]"));
        Assert.Contains("@inject PromptFlowStudio.Services.Pluto.IPlutoFlywheelSelfTestService FlywheelSelfTest", settings, StringComparison.Ordinal);
        Assert.Contains("data-flywheel-stage-status=", settings, StringComparison.Ordinal);

        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Equal(1, CountOf(ws, "[📦 수집 후 폴더 정리 ON]"));
        Assert.Equal(1, CountOf(ws, "data-yt-archive="));
        Assert.True(ws.IndexOf("data-yt-archive=", StringComparison.Ordinal) > ws.IndexOf("data-yt-autowatch=", StringComparison.Ordinal));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        Assert.True(strip > 0);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        foreach (var keyword in new[] { "수집 후 폴더 정리", "data-yt-archive", "Pluto_Ingested", "자가진단" })
            Assert.DoesNotContain(keyword, ws.Substring(0, strip), StringComparison.Ordinal);

        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(root, file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoFlywheelSelfTestService>(_ => new PromptFlowStudio.Services.Pluto.PlutoFlywheelSelfTestService());"));
        }
    }
}
