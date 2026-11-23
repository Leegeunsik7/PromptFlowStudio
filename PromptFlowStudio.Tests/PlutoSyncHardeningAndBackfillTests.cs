using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// #️⃣🔒⚔️🧬 Step 19 — SHA-256 내용 동일성 가드 · 기준선 부트스트랩 · Drive 데몬 일시 잠금 재시도 · 의미 기반 충돌 합집합 병합 + 해결 UI ·
/// BGM/태그 컨텍스트 알파 과거 이력 소급(1회) · 자율 추가(적재 직전 해결 적용 · 검증된 원자 첫 복원 + 충돌 사본 중복 방지 · A/B 실시간 컨텍스트 알파 정확히 1회).
/// </summary>
[Collection("TimingSensitivePerformance")]   // Step 35: 벽시계 상한 단언(초 단위 폴백/종료 지연) → 병렬 구간 제외
public sealed class PlutoSyncHardeningAndBackfillTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Mode = PlutoContextAlphaBackfill.DefaultRenderMode;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step19_" + Guid.NewGuid().ToString("N"));

    public PlutoSyncHardeningAndBackfillTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Put(string path, string content, DateTime utc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, utc);
    }

    private (string Drive, string Local) Dirs(string name)
    {
        var drive = Path.Combine(_dir, name, "vault", "Pluto");
        var local = Path.Combine(_dir, name, "local");
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(local);
        return (drive, local);
    }

    private static string ConflictDir(string local) => Path.Combine(local, PlutoDriveStorageManager.ConflictFolderName);

    // ── ① #️⃣ 내용 동일성 가드 · 🧾 기준선 부트스트랩 ──

    [Fact]
    public async Task Sha256Guard_SuppressesFalseConflictsAndRedundantWrites_OnMtimeJitter_AndBootstrapProtectsFirstDivergence()
    {
        const string f = PlutoSeriesArcMemoryService.FileName;
        var (drive, local) = Dirs("sha");
        string D(string n) => Path.Combine(drive, n);
        string L(string n) => Path.Combine(local, n);

        // Drive 데스크톱 mtime 흔들림 — 내용은 같고 Drive 시각만 1시간 앞섬
        Put(L(f), "{\"v\":\"same\"}", Stamp);
        Put(D(f), "{\"v\":\"same\"}", Stamp.AddHours(1));
        var storage = new PlutoDriveStorageManager(drive, local);
        Assert.Null(storage.GetSyncRecord(f));
        Assert.Equal(1, storage.BootstrapSyncManifest(PlutoDriveHealthService.PlutoFiles));            // 내용 같은 파일만 기준선
        var rec = storage.GetSyncRecord(f)!;
        Assert.Equal(storage.Sha256Of(L(f)), rec.Sha256);
        Assert.Equal(64, rec.Sha256!.Length);
        Assert.Equal(0, storage.BootstrapSyncManifest(PlutoDriveHealthService.PlutoFiles));            // 멱등
        Assert.True(PlutoDriveStorageManager.IsMirrorStale(D(f), L(f)));                               // 시각만 보면 '낡음'
        Assert.Equal(0, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));           // 내용 같음 → 쓰기 없음
        Assert.Equal(Stamp, File.GetLastWriteTimeUtc(L(f)));
        Assert.False(Directory.Exists(ConflictDir(local)));
        Assert.Equal(Stamp.AddHours(1), storage.GetSyncRecord(f)!.DriveUtc);                            // 장부만 현재 시각으로

        // 반대 방향 흔들림(로컬이 1시간 앞섬 · 내용 동일) → 쓰기 반환 없음 · Drive 시각 불변
        File.SetLastWriteTimeUtc(L(f), Stamp.AddHours(5));
        Assert.True(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(f), D(f)));
        Assert.Equal(0, storage.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal(Stamp.AddHours(1), File.GetLastWriteTimeUtc(D(f)));
        Assert.Empty(Directory.EnumerateFiles(drive, "*.tmp"));

        // 🧾 부트스트랩 덕분에 '첫' 분기도 감지 — 이 PC 오프라인 편집 + 다른 PC 의 더 새로운 Drive
        Put(L(f), "{\"v\":\"offline-edit\"}", Stamp.AddHours(6));
        Put(D(f), "{\"v\":\"other-pc\"}", Stamp.AddHours(7));
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"v\":\"offline-edit\"}", File.ReadAllText(Assert.Single(Directory.GetFiles(ConflictDir(local)))));
        Assert.Equal("{\"v\":\"other-pc\"}", File.ReadAllText(L(f)));

        // 대조군 — 부트스트랩 없는 같은 상황(Step 18 이하 설치)은 판정 근거가 없어 사본이 남지 않는다
        var (drive2, local2) = Dirs("sha_nobootstrap");
        Put(Path.Combine(local2, f), "{\"v\":\"offline-edit\"}", Stamp.AddHours(6));
        Put(Path.Combine(drive2, f), "{\"v\":\"other-pc\"}", Stamp.AddHours(7));
        Assert.Equal(1, new PlutoDriveStorageManager(drive2, local2).RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.False(Directory.Exists(ConflictDir(local2)));

        // 내용이 이미 다른 파일은 부트스트랩하지 않는다(공통 조상 미상 — 추측 기준선이 진짜 분기를 가리지 않게)
        var (drive3, local3) = Dirs("sha_differ");
        Put(Path.Combine(local3, f), "{\"v\":1}", Stamp);
        Put(Path.Combine(drive3, f), "{\"v\":2}", Stamp);
        var s3 = new PlutoDriveStorageManager(drive3, local3);
        Assert.Equal(0, s3.BootstrapSyncManifest(PlutoDriveHealthService.PlutoFiles));
        Assert.Null(s3.GetSyncRecord(f));

        // Step 18 장부(파일 → 시각) 그대로 읽힘 — 해시 없음 → 시각 규칙으로 분기 판정
        var (drive4, local4) = Dirs("sha_legacy_manifest");
        File.WriteAllText(Path.Combine(local4, PlutoDriveStorageManager.SyncManifestFileName), "{\"" + f + "\":\"2026-11-01T00:00:00Z\"}");
        var s4 = new PlutoDriveStorageManager(drive4, local4);
        var legacyRec = s4.GetSyncRecord(f)!;
        Assert.Equal((Stamp, (string?)null), (legacyRec.LocalUtc, legacyRec.Sha256));
        Assert.Equal(Stamp, s4.GetLastSyncedUtc(f));
        Put(Path.Combine(local4, f), "{\"v\":\"offline\"}", Stamp.AddHours(1));
        Put(Path.Combine(drive4, f), "{\"v\":\"remote\"}", Stamp.AddHours(2));
        Assert.Equal(1, s4.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Single(Directory.GetFiles(ConflictDir(local4)));
        Assert.NotNull(s4.GetSyncRecord(f)!.Sha256);                                                   // 이후 새 형식으로 기록

        // 🔄 [지금 전체 드라이브 백업 검증] 도 부트스트랩 · OFF 격리
        var (drive5, local5) = Dirs("sha_verify");
        foreach (var n in PlutoDriveHealthService.PlutoFiles) { Put(Path.Combine(drive5, n), "{\"k\":\"" + n + "\"}", Stamp); Put(Path.Combine(local5, n), "{\"k\":\"" + n + "\"}", Stamp); }
        var s5 = new PlutoDriveStorageManager(drive5, local5);
        await new PlutoDriveHealthService(null, null, s5).ForceSyncAndVerifyAsync();
        Assert.All(PlutoDriveHealthService.PlutoFiles, n => Assert.NotNull(s5.GetSyncRecord(n)?.Sha256));
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        var offLocal = Path.Combine(_dir, "sha_off");
        var off = new PlutoDriveStorageManager(PlutoDriveStorageManager.DefaultPrimaryRoot(), offLocal);
        Assert.Equal(0, off.BootstrapSyncManifest(PlutoDriveHealthService.PlutoFiles));
        Assert.False(Directory.Exists(offLocal) && Directory.EnumerateFiles(offLocal).Any());

        var mainForm = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "MainForm.cs"));
        Assert.Contains("plutoStore.BootstrapSyncManifest(PromptFlowStudio.Services.Pluto.PlutoDriveHealthService.PlutoFiles); plutoStore.WriteBackNewerLocalToDrive(", mainForm, StringComparison.Ordinal);
    }

    // ── ② 🔒 Drive 데몬 일시 잠금 재시도 ──

    [Fact]
    public async Task LockRetry_RecoversWhenDriveSyncLockReleases_AndGivesUpBoundedOnPermanentLock()
    {
        Assert.True(PlutoDriveStorageManager.IsTransientLock(new IOException("sharing", unchecked((int)0x80070020))));
        Assert.True(PlutoDriveStorageManager.IsTransientLock(new IOException("lock", unchecked((int)0x80070021))));
        Assert.True(PlutoDriveStorageManager.IsTransientLock(new UnauthorizedAccessException()));
        Assert.False(PlutoDriveStorageManager.IsTransientLock(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.False(PlutoDriveStorageManager.IsTransientLock(new FileNotFoundException()));

        const string f = PlutoLearningService.FileName;
        var (drive, local) = Dirs("lock");
        Put(Path.Combine(local, f), "{\"v\":\"offline-newer\"}", Stamp.AddHours(3));
        Put(Path.Combine(drive, f), "{\"v\":\"drive-old\"}", Stamp);
        var storage = new PlutoDriveStorageManager(drive, local) { LockRetryAttempts = 8, LockRetryBaseDelay = TimeSpan.FromMilliseconds(20) };

        // ⬆️ 쓰기 반환 — 데몬이 Drive 파일을 배타 잠금 → 80ms 뒤 해제 → 재시도로 성공
        int pushed;
        using (var hold = new FileStream(Path.Combine(drive, f), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var run = Task.Run(() => storage.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));
            await Task.Delay(80);
            hold.Dispose();
            pushed = await run;
        }
        Assert.Equal(1, pushed);
        Assert.Equal("{\"v\":\"offline-newer\"}", File.ReadAllText(Path.Combine(drive, f)));
        Assert.True(storage.LockRetryCount > 0);
        Assert.Empty(Directory.EnumerateFiles(drive, "*.tmp"));

        // 🪞 미러 갱신 — 로컬 사본이 잠겨 있다가 풀림
        const string g = PlutoRenderCatalogService.FileName;
        Put(Path.Combine(drive, g), "{\"v\":\"drive-newer\"}", Stamp.AddHours(9));
        Put(Path.Combine(local, g), "{\"v\":\"local-old\"}", Stamp);
        var before = storage.LockRetryCount;
        int restored;
        using (var hold = new FileStream(Path.Combine(local, g), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var run = Task.Run(() => storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
            await Task.Delay(80);
            hold.Dispose();
            restored = await run;
        }
        Assert.Equal(1, restored);
        Assert.Equal("{\"v\":\"drive-newer\"}", File.ReadAllText(Path.Combine(local, g)));
        Assert.True(storage.LockRetryCount > before);

        // 영구 잠금 — 유한 시도 후 포기 · 원본 무손상 · 임시 파일 잔존 없음
        var (drive2, local2) = Dirs("lock_permanent");
        Put(Path.Combine(local2, f), "{\"v\":\"offline\"}", Stamp.AddHours(3));
        Put(Path.Combine(drive2, f), "{\"v\":\"drive\"}", Stamp);
        var stubborn = new PlutoDriveStorageManager(drive2, local2) { LockRetryAttempts = 3, LockRetryBaseDelay = TimeSpan.FromMilliseconds(5) };
        var sw = Stopwatch.StartNew();
        using (new FileStream(Path.Combine(drive2, f), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(0, await Task.Run(() => stubborn.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles)));
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.Equal("{\"v\":\"drive\"}", File.ReadAllText(Path.Combine(drive2, f)));
        Assert.Equal("{\"v\":\"offline\"}", File.ReadAllText(Path.Combine(local2, f)));
        Assert.Empty(Directory.EnumerateFiles(drive2, "*.tmp"));
        Assert.Equal(1, stubborn.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));     // 잠금이 풀리면 다음 조정에서 반환
    }

    // ── ③ 🔀 의미 기반 합집합 병합(순수) ──

    private static string Ledger(params string[] experiments) => "{\"Version\":1,\"Experiments\":[" + string.Join(",", experiments) + "]}";

    private static string Exp(string id, DateTime registered, bool evaluated, string? video = null, string champBgm = "Lullaby", string challBgm = "MusicBox", string theme = "아기 첫 걸음마",
        double ah = 60, double bh = 40, DateTime? evaluatedAt = null, string extra = "")
    {
        var m = evaluated
            ? ",\"ChampionMetrics\":{\"ThreeSecondHookRetentionPct\":" + ah.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"AverageCompletionPct\":40,\"ClickThroughRatePct\":4}"
              + ",\"ChallengerMetrics\":{\"ThreeSecondHookRetentionPct\":" + bh.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"AverageCompletionPct\":40,\"ClickThroughRatePct\":4}"
              + ",\"Evaluation\":{\"ExperimentId\":\"" + id + "\",\"WinnerLabel\":\"ChampionA\",\"ChampionScore\":50,\"ChallengerScore\":40,\"LiftPct\":25,\"TotalXpAwarded\":10,\"SummaryMessage\":\"\",\"EvaluatedAtUtc\":\""
                + (evaluatedAt ?? registered.AddHours(1)).ToString("o") + "\"}"
            : "";
        return "{\"ExperimentId\":\"" + id + "\",\"ThemeHint\":\"" + theme + "\",\"ChampionHook\":\"h1\",\"ChampionLook\":\"Natural Pastel\",\"ChampionBgm\":\"" + champBgm + "\",\"ChallengerHook\":\"h2\",\"ChallengerLook\":\"Natural Pastel\",\"ChallengerBgm\":\""
            + challBgm + "\",\"RegisteredAtUtc\":\"" + registered.ToString("o") + "\",\"Status\":\"" + (evaluated ? "Evaluated" : "Pending") + "\""
            + (video is null ? "" : ",\"ChampionVideoPath\":\"" + video.Replace("\\", "\\\\") + "\"") + m + extra + "}";
    }

    [Fact]
    public void ConflictUnionMerge_CombinesDivergentLedgerAndNotebookWithoutLosingEitherSide_AndPreservesUnknownFields()
    {
        // 🏆 실험 장부 — PC1: exp1 판정 · exp2 대기 / PC2: exp2 판정(지표) · exp3 새 실험(영상 경로)
        var pc1 = Ledger(Exp("exp1", Stamp, true, extra: ",\"FutureField\":7"), Exp("exp2", Stamp.AddHours(1), false));
        var pc2 = Ledger(Exp("exp2", Stamp.AddHours(1), true), Exp("exp3", Stamp.AddHours(2), false, video: @"C:\v\exp3-A.mp4"));
        var merged = PlutoConflictMergeService.MergeJson(PlutoExperimentLedgerService.FileName, pc1, pc2)!;
        var exps = JsonNode.Parse(merged)!["Experiments"]!.AsArray();
        Assert.Equal(new[] { "exp1", "exp2", "exp3" }, exps.Select(e => (string)e!["ExperimentId"]!).ToArray());   // 등록 시각순
        Assert.Equal("Evaluated", (string)exps[1]!["Status"]!);                                                // 진행이 더 된 쪽
        Assert.NotNull(exps[1]!["Evaluation"]);
        Assert.Equal(7, (int)exps[0]!["FutureField"]!);                                                        // 모르는 필드 보존
        Assert.Equal(@"C:\v\exp3-A.mp4", (string)exps[2]!["ChampionVideoPath"]!);
        var reverse = JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoExperimentLedgerService.FileName, pc2, pc1)!)!["Experiments"]!.AsArray();
        Assert.Equal(exps.Select(e => ((string)e!["ExperimentId"]!, (string)e["Status"]!)), reverse.Select(e => ((string)e!["ExperimentId"]!, (string)e["Status"]!)));   // 순서 무관 같은 결과
        // 판정된 쪽이 영상 경로가 없으면 대기 쪽 경로로 채움
        var withVideo = PlutoConflictMergeService.MergeJson(PlutoExperimentLedgerService.FileName, Ledger(Exp("x", Stamp, false, video: @"C:\v\x-A.mp4")), Ledger(Exp("x", Stamp, true)))!;
        var x = JsonNode.Parse(withVideo)!["Experiments"]![0]!;
        Assert.Equal(("Evaluated", @"C:\v\x-A.mp4"), ((string)x["Status"]!, (string)x["ChampionVideoPath"]!));

        // 🛡️ 오답 노트 — 라벨 단위 · 더 최근 기록 기준 · 적중 수는 최댓값(합 아님)
        var nb1 = "{\"Version\":1,\"Entries\":{\"A\":{\"Label\":\"A\",\"RawPenalty\":1.5,\"LastUtc\":\"2026-11-05T00:00:00Z\",\"IsSuppressed\":false,\"Hits\":2},"
                  + "\"C\":{\"Label\":\"C\",\"RawPenalty\":1.0,\"LastUtc\":\"2026-11-02T00:00:00Z\",\"IsSuppressed\":false,\"Hits\":5}}}";
        var nb2 = "{\"Version\":1,\"Entries\":{\"B\":{\"Label\":\"B\",\"RawPenalty\":2.0,\"LastUtc\":\"2026-11-03T00:00:00Z\",\"IsSuppressed\":true,\"Hits\":1},"
                  + "\"C\":{\"Label\":\"C\",\"RawPenalty\":1.9,\"LastUtc\":\"2026-11-04T00:00:00Z\",\"IsSuppressed\":true,\"Hits\":3}}}";
        var nb = JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoMistakeNotebookService.FileName, nb1, nb2)!)!["Entries"]!.AsObject();
        Assert.Equal(new[] { "A", "B", "C" }, nb.Select(kv => kv.Key).ToArray());
        Assert.Equal((1.9, true, 5), ((double)nb["C"]!["RawPenalty"]!, (bool)nb["C"]!["IsSuppressed"]!, (int)nb["C"]!["Hits"]!));
        Assert.True((bool)nb["B"]!["IsSuppressed"]!);

        // 🎞️ 카탈로그 · 📺 아크 · 🧠 학습 메모리
        var cat = JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName,
            "{\"Version\":1,\"Clips\":[{\"VideoId\":\"v1\",\"RenderedAtUtc\":\"2026-11-01T00:00:00Z\",\"TelemetryLogged\":true},{\"VideoId\":\"v2\",\"RenderedAtUtc\":\"2026-11-02T00:00:00Z\",\"TelemetryLogged\":false}]}",
            "{\"Version\":1,\"Clips\":[{\"VideoId\":\"V1\",\"RenderedAtUtc\":\"2026-11-03T00:00:00Z\",\"TelemetryLogged\":false},{\"VideoId\":\"v3\",\"RenderedAtUtc\":\"2026-10-01T00:00:00Z\"}]}")!)!["Clips"]!.AsArray();
        Assert.Equal(new[] { "V1", "v2", "v3" }, cat.Select(c => (string)c!["VideoId"]!).ToArray());            // 최신순 · 영상 ID 대소문자 무시
        Assert.True((bool)cat[0]!["TelemetryLogged"]!);                                                         // 학습 완료는 어느 쪽이든 유지
        var arc = JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoSeriesArcMemoryService.FileName,
            "{\"Version\":1,\"Series\":[{\"Key\":\"s#3\",\"Total\":3,\"UpdatedUtc\":\"2026-11-02T00:00:00Z\",\"Episodes\":[{\"N\":1,\"Measured\":true,\"HookPct\":60,\"MeasuredUtc\":\"2026-11-02T00:00:00Z\"},{\"N\":2,\"Measured\":false}]}],\"Baselines\":{\"ctx:A|B\":{\"H\":50,\"C\":40,\"N\":3}}}",
            "{\"Version\":1,\"Series\":[{\"Key\":\"s#3\",\"Total\":3,\"UpdatedUtc\":\"2026-11-01T00:00:00Z\",\"Episodes\":[{\"N\":2,\"Measured\":true,\"HookPct\":45,\"MeasuredUtc\":\"2026-11-01T00:00:00Z\"},{\"N\":3,\"Measured\":false}]}],\"Baselines\":{\"ctx:A|B\":{\"H\":55,\"C\":45,\"N\":5}}}")!)!;
        var eps = arc["Series"]![0]!["Episodes"]!.AsArray();
        Assert.Equal(new[] { 1, 2, 3 }, eps.Select(e => (int)e!["N"]!).ToArray());
        Assert.True((bool)eps[1]!["Measured"]!);                                                                // 측정 > 미측정
        Assert.Equal(5.0, (double)arc["Baselines"]!["ctx:A|B"]!["N"]!);
        Assert.Equal("2026-11-02T00:00:00Z", (string)arc["Series"]![0]!["UpdatedUtc"]!);
        var trend = JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoLearningService.FileName,
            "{\"t1\":{\"Weight\":0.4,\"Interactions\":3,\"Adopted\":2,\"LastUtc\":\"2026-11-02T00:00:00Z\"},\"only1\":{\"Weight\":1}}",
            "{\"t1\":{\"Weight\":0.9,\"Interactions\":5,\"Adopted\":1,\"LastUtc\":\"2026-11-03T00:00:00Z\"},\"only2\":{\"Weight\":2}}")!)!.AsObject();
        Assert.Equal((0.9, 5, 2), ((double)trend["t1"]!["Weight"]!, (int)trend["t1"]!["Interactions"]!, (int)trend["t1"]!["Adopted"]!));
        Assert.True(trend.ContainsKey("only1") && trend.ContainsKey("only2"));

        // 거절 — 손상 · 비병합 허브 · 배열 루트
        Assert.Null(PlutoConflictMergeService.MergeJson(PlutoExperimentLedgerService.FileName, pc1, "{\"Experiments\":["));
        Assert.Null(PlutoConflictMergeService.MergeJson(PlutoIdentityVault.FileName, "{}", "{}"));
        Assert.Null(PlutoConflictMergeService.MergeJson(PlutoLearningService.FileName, "[]", "{}"));
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);   // Step 20 — 보정 상태 편입
        Assert.Equal(PlutoSeriesArcMemoryService.FileName, PlutoConflictMergeService.HubFileOf("pluto_series_arc.conflict-20261101010000-2.json"));
        Assert.Null(PlutoConflictMergeService.HubFileOf("pluto_series_arc.json"));
    }

    // ── ④ ⚔️ 충돌 목록 → 병합/복원/무시 예약 → 다음 적재 직전 적용 · UI ──

    [Fact]
    public async Task ConflictResolution_StagedMergeAppliesOnNextLoad_ServicesSeeBothMachinesEntries_AndUiSurfacesIt()
    {
        var (drive, local) = Dirs("resolve");
        var ledgerFile = PlutoExperimentLedgerService.FileName;
        Put(Path.Combine(drive, ledgerFile), Ledger(Exp("pc-b-1", Stamp, true), Exp("shared", Stamp.AddHours(1), false)), Stamp.AddDays(1));
        var conflicts = ConflictDir(local);
        Put(Path.Combine(conflicts, "pluto_experiment_ledger.conflict-20261101010000.json"), Ledger(Exp("pc-a-1", Stamp.AddHours(2), true), Exp("shared", Stamp.AddHours(1), true)), Stamp.AddHours(3));
        Put(Path.Combine(conflicts, "pluto_identity_vault.conflict-20261101020000.json"), "{\"a\":1}", Stamp.AddHours(4));
        Put(Path.Combine(conflicts, "pluto_mistake_notebook.conflict-20261101030000.json"), "{\"half", Stamp.AddHours(5));

        var storage = new PlutoDriveStorageManager(drive, local);
        var health = new PlutoDriveHealthService(null, null, storage);
        var list = health.ListConflicts();
        Assert.Equal(3, list.Count);
        Assert.Equal(PlutoMistakeNotebookService.FileName, list[0].HubFileName);                        // 최신순
        Assert.False(list[0].IsValidJson);
        var calib = list.Single(c => c.HubFileName == PlutoIdentityVault.FileName);
        Assert.False(calib.IsMergeable);
        var ledgerCopy = list.Single(c => c.HubFileName == ledgerFile);
        Assert.True(ledgerCopy.IsMergeable && ledgerCopy.IsValidJson);

        Assert.False(health.MergeConflict(calib.FullPath).Ok);                                         // 비병합 허브는 병합 불가(사본 유지)
        Assert.False(health.MergeConflict(list[0].FullPath).Ok);                                       // 손상 사본은 병합 · 복원 불가
        Assert.False(health.RestoreConflict(list[0].FullPath).Ok);
        Assert.False(health.DismissConflict(Path.Combine(drive, ledgerFile)).Ok);                      // 충돌 폴더 밖 경로 거절
        Assert.True(File.Exists(Path.Combine(drive, ledgerFile)));
        var merged = health.MergeConflict(ledgerCopy.FullPath);
        Assert.Equal(PlutoConflictAction.MergeStaged, merged.Action);
        Assert.Equal(1, health.PendingConflictResolutions);
        Assert.Equal(PlutoConflictAction.Dismissed, health.DismissConflict(list[0].FullPath).Action);
        Assert.Equal(PlutoConflictAction.RestoreStaged, health.RestoreConflict(calib.FullPath).Action);
        Assert.Empty(health.ListConflicts());
        Assert.Equal(2, health.PendingConflictResolutions);

        // 🧠 두뇌 리포트 — 미해결 0 · 적용 대기 2
        var summary = await new PlutoAutonomousBrainSummaryService(null, null, null) { DriveHealth = health }.GetSummaryAsync();
        Assert.Equal((0, 2), (summary.UnresolvedConflictCount, summary.PendingConflictResolutions));
        Assert.Equal("미해결 없음 · 적용 대기 2건(다음 실행 시 반영)", summary.ConflictLabel);
        Assert.StartsWith("미해결 1건", PlutoAutonomousBrainSummaryService.BuildConflictLabel(1, 0), StringComparison.Ordinal);
        Assert.StartsWith("없음", PlutoAutonomousBrainSummaryService.BuildConflictLabel(0, 0), StringComparison.Ordinal);

        // 다음 실행 — 장부 서비스가 적재되기 직전에 합집합 적용 → 두 PC 의 실험이 모두 보인다
        var next = new PlutoDriveStorageManager(drive, local);
        using (var ledger = new PlutoExperimentLedgerService(null, next))
        {
            await ledger.EnsureLoadedAsync();
            var evaluated = ledger.GetRecentEvaluations(10).Select(e => e.ExperimentId).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "pc-a-1", "pc-b-1", "shared" }, evaluated);
            Assert.Empty(ledger.GetPendingExperiments(10));
        }
        Assert.Equal(File.ReadAllText(Path.Combine(drive, ledgerFile)), File.ReadAllText(Path.Combine(local, ledgerFile)));   // 로컬 미러 동기
        Assert.NotNull(next.GetSyncRecord(ledgerFile)?.Sha256);
        Assert.Equal(1, next.PendingResolutionCount);                                                   // 보정 상태 복원은 그 허브 적재 때
        _ = next.ResolveDataPath(PlutoIdentityVault.FileName);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(drive, PlutoIdentityVault.FileName)));
        Assert.Equal(0, next.PendingResolutionCount);

        // 대기 병합이 손상됐으면 현재 기록은 그대로 · .rejected 로 보존
        Directory.CreateDirectory(next.PendingRoot);
        File.WriteAllText(Path.Combine(next.PendingRoot, PlutoRenderCatalogService.FileName + PlutoDriveStorageManager.PendingMergeExt), "{\"Clips\":[");
        Put(Path.Combine(drive, PlutoRenderCatalogService.FileName), "{\"Version\":1,\"Clips\":[]}", Stamp);
        Assert.False(new PlutoDriveStorageManager(drive, local).ApplyPendingResolution(PlutoRenderCatalogService.FileName));
        Assert.Equal("{\"Version\":1,\"Clips\":[]}", File.ReadAllText(Path.Combine(drive, PlutoRenderCatalogService.FileName)));
        Assert.True(File.Exists(Path.Combine(next.PendingRoot, PlutoRenderCatalogService.FileName + PlutoDriveStorageManager.PendingMergeExt + ".rejected")));

        // UI — 환경설정 충돌 섹션(병합 · 복원 · 무시) · 두뇌 리포트 본문 표시(상단 3행 헤더 불변)
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        foreach (var marker in new[] { "data-drive-conflicts=", "data-conflict-merge=\"1\"", "data-conflict-restore=\"1\"", "data-conflict-dismiss=\"1\"", "DriveHealth.MergeConflictLiveAsync(c.FullPath)", "DriveHealth.RestoreConflictLiveAsync(c.FullPath)", "DriveHealth.DismissConflict(c.FullPath)" })
            Assert.Contains(marker, settings, StringComparison.Ordinal);
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        var body = ws.Substring(strip, tools - strip);
        Assert.Equal(1, CountOf(ws, "data-brain-conflicts="));
        Assert.True(body.IndexOf("data-brain-conflicts=", StringComparison.Ordinal) > body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal));
        Assert.Contains("@(bs.ConflictLabel)", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        foreach (var keyword in new[] { "충돌", "data-brain-conflicts", "ConflictLabel", "병합" })
            Assert.DoesNotContain(keyword, ws.Substring(0, strip), StringComparison.Ordinal);
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoContextAlphaBackfillService>(sp => new PromptFlowStudio.Services.Pluto.PlutoContextAlphaBackfillService("));
            Assert.Equal(1, CountOf(di, "DriveHealth = sp.GetService(typeof(PromptFlowStudio.Services.Pluto.IPlutoDriveHealthService)) as PromptFlowStudio.Services.Pluto.IPlutoDriveHealthService, Backfill = sp.GetService("));
        }
    }

    // ── ⑤ 🧬 BGM · 태그 컨텍스트 알파 과거 이력 소급(정확히 1회) ──

    [Fact]
    public async Task HistoricalBackfill_PopulatesBgmAndTagContextAlphasFromLegacyLedgerExactlyOnce_WithoutTouchingHookLookOrBaselines()
    {
        var domain = PromptDomainClassifier.Classify("아기 첫 걸음마").ToString();
        var ledgerPath = Path.Combine(_dir, "bf_ledger.json");
        File.WriteAllText(ledgerPath, Ledger(
            Exp("e1", Stamp, true, video: @"C:\out\e1-A.mp4", ah: 70, bh: 40),
            Exp("e2", Stamp.AddDays(1), true, ah: 65, bh: 45),
            Exp("e3", Stamp.AddDays(2), true, champBgm: "Lullaby", challBgm: "Lullaby", ah: 60, bh: 50),   // 같은 BGM → 실험 변수 아님
            Exp("e4", Stamp.AddDays(3), false)));                                                         // 대기 → 지표 없음
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "bf_catalog.json"));
        await catalog.RecordRenderedClipAsync(new PlutoRenderedClipRecord("e1-A", "e1 챔피언", "몽타주", "h1", "Natural Pastel", "Lullaby", new[] { "#봄나들이" }, @"C:\out\e1-A.mp4", new DateTimeOffset(Stamp), false));

        // 순수 빌더 — 짝 평균 기준선
        var samples = PlutoContextAlphaBackfill.FromLedgerJson(File.ReadAllText(ledgerPath), await catalog.GetRecentClipsAsync(10), out var experiments)!;
        Assert.Equal(2, experiments);
        Assert.Equal(new[] { ("e1", "bgm:Lullaby"), ("e1", "bgm:MusicBox"), ("e1", "style:#봄나들이"), ("e2", "bgm:Lullaby"), ("e2", "bgm:MusicBox") }, samples.Select(s => (s.ExperimentId, s.Token)).ToArray());
        double sa = PlutoContextualBaseline.CompositeScore(70, 40, 4), sb = PlutoContextualBaseline.CompositeScore(40, 40, 4);
        Assert.Equal((sa - sb) / 2.0, samples[0].Alpha, 12);
        Assert.Equal(-(sa - sb) / 2.0, samples[1].Alpha, 12);
        Assert.All(samples, s => Assert.Equal((domain, Mode), (s.Domain, s.RenderMode)));
        Assert.Null(PlutoContextAlphaBackfill.FromLedgerJson("{\"Experiments\":[", null, out _));
        Assert.Empty(PlutoContextAlphaBackfill.FromLedgerJson("{}", null, out _)!);
        Assert.False(PlutoContextAlphaBackfill.IsContextAlphaToken("bgm:custom"));
        Assert.False(PlutoContextAlphaBackfill.IsContextAlphaToken("hook:x"));

        // 장부 부재 → 표식 없이 보류(다음 기동에 재시도)
        var learnPath = Path.Combine(_dir, "bf_trend.json");
        var learning = new PlutoLearningService(learnPath);
        var missing = await new PlutoContextAlphaBackfillService(learning, null, catalog, Path.Combine(_dir, "nope.json")).EnsureBackfilledAsync();
        Assert.False(missing.Completed);
        Assert.False(learning.HasContextAlphaBackfill);
        var corruptPath = Path.Combine(_dir, "bf_corrupt.json");
        File.WriteAllText(corruptPath, "{\"Experiments\":[");
        Assert.False((await new PlutoContextAlphaBackfillService(learning, null, catalog, corruptPath).EnsureBackfilledAsync()).Completed);
        Assert.False(learning.HasContextAlphaBackfill);

        var svc = new PlutoContextAlphaBackfillService(learning, null, catalog, ledgerPath);
        var report = await svc.EnsureBackfilledAsync();
        Assert.Equal((true, false, 2, 5), (report.Completed, report.AlreadyDone, report.Experiments, report.Samples));
        Assert.True(learning.HasContextAlphaBackfill);
        Assert.True(learning.HasContextAlphaExperiment("e1") && learning.HasContextAlphaExperiment("e2") && !learning.HasContextAlphaExperiment("e3"));
        var lul = learning.GetContextPatternAlpha(domain, Mode, "bgm:Lullaby");
        Assert.Equal(2.0, lul.Samples);
        Assert.True(lul.Alpha > 0);
        Assert.True(learning.GetContextPatternAlpha(domain, Mode, "bgm:MusicBox").Alpha < 0);
        Assert.Equal(1.0, learning.GetContextPatternAlpha(domain, Mode, "style:#봄나들이").Samples);
        Assert.Equal(2.0, learning.GetPatternAlphaStats("bgm:Lullaby").Samples);
        // 이중 계상 없음 — 훅 · 룩 · 컨텍스트 기준선 · 다목적 · XP 불변
        Assert.Equal(0.0, learning.GetPatternAlphaStats("hook:h1").Samples);
        Assert.Equal(0.0, learning.GetPatternAlphaStats("look:Natural Pastel").Samples);
        Assert.Empty(learning.GetContextBaselines());
        Assert.Equal(0.0, learning.GetPatternObjectives("bgm:Lullaby").Samples);
        Assert.Equal((0, 0), (learning.CurrentXP, learning.TotalInteractions));
        var snapshot = (lul, learning.GetPatternAlphaStats("bgm:Lullaby"));

        // 같은 인스턴스 · 재적재 후 새 서비스 — 다시 돌지 않는다
        Assert.True((await svc.EnsureBackfilledAsync()).Completed);
        learning.Dispose();
        var reloaded = new PlutoLearningService(learnPath);
        var again = await new PlutoContextAlphaBackfillService(reloaded, null, catalog, ledgerPath).EnsureBackfilledAsync();
        Assert.True(again.AlreadyDone);
        Assert.Equal(snapshot, (reloaded.GetContextPatternAlpha(domain, Mode, "bgm:Lullaby"), reloaded.GetPatternAlphaStats("bgm:Lullaby")));
        Assert.Equal(-1, await reloaded.RecordContextAlphaSamplesAsync(samples, true, PlutoContextAlphaBackfill.MarkerKey));

        // 랭커 — 콜드스타트 몽타주 컨텍스트에 소급 근거가 실린다
        var ranker = new PlutoAdaptivePatternRanker(reloaded, null);
        var bgm = ranker.RankForContext(PlutoPatternKind.BgmMood, domain, Mode, new[] { "Lullaby", "MusicBox" });
        Assert.Equal(2.0, bgm.Single(o => o.DisplayLabel == "Lullaby").ContextTransfer.LocalTrials);
        Assert.True(bgm.Single(o => o.DisplayLabel == "Lullaby").ContextTransfer.IsColdStart);

        // 🧠 두뇌 리포트가 소급을 보장(랭커 집계 이전)
        var fresh = new PlutoLearningService(Path.Combine(_dir, "bf_trend2.json"));
        var viaSummary = await new PlutoAutonomousBrainSummaryService(null, new PlutoAdaptivePatternRanker(fresh, null), null)
            { Backfill = new PlutoContextAlphaBackfillService(fresh, null, catalog, ledgerPath) }.GetSummaryAsync();
        Assert.True(viaSummary.Backfill!.Completed);
        Assert.True(fresh.HasContextAlphaBackfill);
    }

    // ── ⑥ 자율 추가 ① — 📥 해결은 적재 직전에만: 실행 중 서비스의 메모리 상태가 병합 결과를 되덮지 않는다 ──

    [Fact]
    public async Task SelfInitiated_StagedResolution_IsNotClobberedByAServiceAlreadyHoldingStateInMemory()
    {
        var (drive, local) = Dirs("clobber");
        var file = PlutoRenderCatalogService.FileName;
        Put(Path.Combine(drive, file), "{\"Version\":1,\"Clips\":[{\"VideoId\":\"pc-b\",\"Title\":\"b\",\"RenderedAtUtc\":\"2026-11-02T00:00:00Z\"}]}", Stamp);
        var session1 = new PlutoDriveStorageManager(drive, local);
        using (var catalog = new PlutoRenderCatalogService(session1))
        {
            Assert.Single(await catalog.GetRecentClipsAsync(10));                                          // 이 세션은 이미 메모리에 적재
            Put(Path.Combine(ConflictDir(local), "pluto_render_catalog.conflict-20261101000000.json"),
                "{\"Version\":1,\"Clips\":[{\"VideoId\":\"pc-a-offline\",\"Title\":\"a\",\"RenderedAtUtc\":\"2026-11-01T00:00:00Z\"}]}", Stamp);
            var conflict = new PlutoConflictMergeService(session1);
            Assert.Equal(PlutoConflictAction.MergeStaged, conflict.StageMerge(conflict.ListConflicts()[0].FullPath).Action);
            _ = session1.ResolveDataPath(file);                                                                // 같은 세션 재해석 — 적용하지 않음
            Assert.DoesNotContain("pc-a-offline", File.ReadAllText(Path.Combine(drive, file)), StringComparison.Ordinal);
            // 실행 중 서비스가 새 렌더를 기록(메모리 상태 전체를 씀) — 즉시 적용했다면 여기서 병합 결과가 사라졌을 것
            await catalog.RecordRenderedClipAsync(new PlutoRenderedClipRecord("pc-b-new", "n", "몽타주", "h", "l", "b", Array.Empty<string>(), "", DateTimeOffset.UtcNow, false));
        }
        Assert.Equal(1, session1.PendingResolutionCount);

        // 다음 세션 — 적재 직전에 '그때의' 현재 기록과 합쳐짐 → 세 영상 모두
        using var reopened = new PlutoRenderCatalogService(new PlutoDriveStorageManager(drive, local));
        var ids = (await reopened.GetRecentClipsAsync(10)).Select(c => c.VideoId).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "pc-a-offline", "pc-b", "pc-b-new" }, ids);
    }

    // ── ⑦ 자율 추가 ② — 🛡️ 첫 복원 검증 + 원자 · ⚔️ 충돌 사본 같은 초 덮어쓰기/중복 방지 ──

    [Fact]
    public void SelfInitiated_FirstRestoreRejectsHalfSyncedDrivePlaceholders_AndConflictCopiesNeverOverwriteOrDuplicate()
    {
        var (drive, local) = Dirs("first_restore");
        Put(Path.Combine(drive, PlutoLearningService.FileName), "", Stamp);                                  // 0 B 자리표시
        Put(Path.Combine(drive, PlutoMistakeNotebookService.FileName), "{\"Entries\":{\"x\":", Stamp);      // 내려받는 중(잘림)
        Put(Path.Combine(drive, PlutoRenderCatalogService.FileName), "{\"Version\":1,\"Clips\":[]}", Stamp);
        var storage = new PlutoDriveStorageManager(drive, local);
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.False(File.Exists(Path.Combine(local, PlutoLearningService.FileName)));
        Assert.False(File.Exists(Path.Combine(local, PlutoMistakeNotebookService.FileName)));
        Assert.Equal(Stamp, File.GetLastWriteTimeUtc(Path.Combine(local, PlutoRenderCatalogService.FileName)));
        Assert.Equal(storage.Sha256Of(Path.Combine(drive, PlutoRenderCatalogService.FileName)), storage.GetSyncRecord(PlutoRenderCatalogService.FileName)?.Sha256);
        Assert.Empty(Directory.EnumerateFiles(local, "*.tmp"));
        // 동기화가 끝나면 다음 조정에서 복원
        Put(Path.Combine(drive, PlutoMistakeNotebookService.FileName), "{\"Entries\":{}}", Stamp.AddMinutes(1));
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));

        // ⚔️ 같은 초(수정 시각)의 서로 다른 분기 → 접미 번호 · 같은 내용 → 중복 사본 없음
        const string f = PlutoSeriesArcMemoryService.FileName;
        string D(string n) => Path.Combine(drive, n);
        string L(string n) => Path.Combine(local, n);
        Put(L(f), "{\"v\":0}", Stamp); Put(D(f), "{\"v\":0}", Stamp);
        Assert.Equal(1, storage.BootstrapSyncManifest(new[] { f }));
        var t1 = Stamp.AddHours(1);
        Put(L(f), "{\"v\":\"offline-1\"}", t1); Put(D(f), "{\"v\":\"remote-1\"}", t1.AddHours(1));
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(new[] { f }));
        Put(L(f), "{\"v\":\"offline-2\"}", t1); Put(D(f), "{\"v\":\"remote-2\"}", t1.AddHours(2));
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(new[] { f }));
        Put(L(f), "{\"v\":\"offline-2\"}", t1); Put(D(f), "{\"v\":\"remote-3\"}", t1.AddHours(3));
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(new[] { f }));
        var copies = Directory.GetFiles(ConflictDir(local)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "pluto_series_arc.conflict-20261101010000-2.json", "pluto_series_arc.conflict-20261101010000.json" }, copies);
        Assert.Equal("{\"v\":\"offline-1\"}", File.ReadAllText(Path.Combine(ConflictDir(local), copies[1]!)));
        Assert.Equal("{\"v\":\"offline-2\"}", File.ReadAllText(Path.Combine(ConflictDir(local), copies[0]!)));
        Assert.Equal(3, storage.ConflictCopyCount);                                                          // 3회 보존 판정 · 파일은 2개(같은 내용 중복 없음)
    }

    // ── ⑧ 자율 추가 ③ — 🧬 이후 A/B 판정도 같은 공식으로 실시간 기록 · 소급과 정확히 1회 ──

    private static PlutoVariantABPlan Plan(string id, string champBgm, string challBgm)
    {
        var cues = new[] { new SubtitleCue(0, 5, "훅") };
        return new PlutoVariantABPlan(id, "아기 첫 걸음마", new PlutoExperimentVariant("A", "h1", "Natural Pastel", champBgm, "", cues),
            new PlutoExperimentVariant("B", "h2", "Natural Pastel", challBgm, "", cues), "hyp") { ContextRenderMode = Mode };
    }

    [Fact]
    public async Task SelfInitiated_LiveAbEvaluationRecordsSameContextAlpha_AndBackfillNeverDoubleCountsIt()
    {
        var domain = PromptDomainClassifier.Classify("아기 첫 걸음마").ToString();
        var store = new PlutoDriveStorageManager(null, Path.Combine(_dir, "live"));
        var learning = new PlutoLearningService(store);
        using var closed = new PlutoClosedLoopReinforcementService(learning, null, store);
        using var ledger = new PlutoExperimentLedgerService(closed, store);
        var a = new PlutoExperimentMetricsInput(70, 40, 4);
        var b = new PlutoExperimentMetricsInput(40, 40, 4);
        await ledger.EvaluateExperimentAsync(Plan("live-1", "Lullaby", "MusicBox"), a, b);

        // 실시간 판정 → 소급과 같은 짝 평균 공식
        Assert.True(learning.HasContextAlphaExperiment("live-1"));
        var expected = (PlutoContextualBaseline.CompositeScore(70, 40, 4) - PlutoContextualBaseline.CompositeScore(40, 40, 4)) / 2.0;
        var lul = learning.GetContextPatternAlpha(domain, Mode, "bgm:Lullaby");
        Assert.Equal(expected, lul.Alpha, 12);
        Assert.Equal(1.0, lul.Samples);
        Assert.Equal(1.0, learning.GetContextPatternAlpha(domain, Mode, "bgm:MusicBox").Samples);
        // 재판정은 장부가 막고 · 같은 실험을 다시 넣어도 실험 ID 표식이 막는다
        await ledger.EvaluateExperimentAsync(Plan("live-1", "Lullaby", "MusicBox"), a, b);
        Assert.Equal(0, await learning.RecordContextAlphaSamplesAsync(PlutoContextAlphaBackfill.PairSamples("live-1", domain, Mode, 0.9, 0.1, new[] { "bgm:Lullaby" }, new[] { "bgm:MusicBox" }, DateTimeOffset.UtcNow), false));
        Assert.Equal(1.0, learning.GetContextPatternAlpha(domain, Mode, "bgm:Lullaby").Samples);
        // 같은 BGM 짝(BGM 이 실험 변수가 아님) → 기록 없음
        await ledger.EvaluateExperimentAsync(Plan("live-same", "Acoustic", "Acoustic"), a, b);
        Assert.Equal(0.0, learning.GetContextPatternAlpha(domain, Mode, "bgm:Acoustic").Samples);

        // 이후 첫 기동 소급 — 장부에 있는 live-1 은 건너뛰고 과거 실험만
        var ledgerPath = store.ResolveDataPath(PlutoExperimentLedgerService.FileName);
        var json = JsonNode.Parse(File.ReadAllText(ledgerPath))!.AsObject();
        json["Experiments"]!.AsArray().Add(JsonNode.Parse(Exp("old-1", Stamp, true, ah: 65, bh: 45)));
        File.WriteAllText(ledgerPath, json.ToJsonString());
        var report = await new PlutoContextAlphaBackfillService(learning, store).EnsureBackfilledAsync();
        Assert.Equal(1, report.Experiments);                                                                  // old-1 만
        Assert.Equal(2.0, learning.GetContextPatternAlpha(domain, Mode, "bgm:Lullaby").Samples);
        Assert.True(learning.HasContextAlphaExperiment("old-1"));

        // 컨텍스트 미지정 요청(구 호출자) → 기록 생략(기존 동작)
        var w = new PlutoVideoPerformanceTelemetry("x-W", "YouTube", "", "", "Lullaby", Array.Empty<string>(), 70, 40, 4, DateTimeOffset.UtcNow);
        var l = w with { VideoId = "x-L", BgmMood = "MusicBox", ThreeSecondHookRetentionPct = 40 };
        await closed.IngestExperimentOutcomeAsync(new PlutoExperimentReinforcementRequest("legacy-x", w, l, 10, false));
        Assert.False(learning.HasContextAlphaExperiment("legacy-x"));
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
