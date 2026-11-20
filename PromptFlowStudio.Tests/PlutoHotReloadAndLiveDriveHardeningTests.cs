using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// ♻️🎯🏷️🩺 Step 20 — 무재시작 충돌 병합/복원 핫 리로드 · <c>pluto_calibration_state.json</c> 의미 병합 · 장부 RenderMode/StyleTags 영속 ·
/// 라이브 Drive 마운트 프로브 · 자율 추가(실시간 A/B 컨텍스트 알파 태그 정합 · 복원 형태 가드 · 재적재 전 기록 배수 · 보정 .bak 갱신).
/// </summary>
public sealed class PlutoHotReloadAndLiveDriveHardeningTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step20_" + Guid.NewGuid().ToString("N"));

    public PlutoHotReloadAndLiveDriveHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── 헬퍼 ──

    private (string Drive, string Local) Dirs(string name)
    {
        var drive = Path.Combine(_dir, name, "vault", "Pluto");
        var local = Path.Combine(_dir, name, "local");
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(local);
        return (drive, local);
    }

    private static string Conflict(string local, string hubStem, string stamp, string json)
    {
        var dir = Path.Combine(local, PlutoDriveStorageManager.ConflictFolderName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, hubStem + ".conflict-" + stamp + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Ledger(params string[] experiments) => "{\"Version\":1,\"Experiments\":[" + string.Join(",", experiments) + "]}";

    private static string Exp(string id, DateTime registered, bool evaluated, string extra = "")
    {
        var m = evaluated
            ? ",\"ChampionMetrics\":{\"ThreeSecondHookRetentionPct\":70,\"AverageCompletionPct\":40,\"ClickThroughRatePct\":4}"
              + ",\"ChallengerMetrics\":{\"ThreeSecondHookRetentionPct\":40,\"AverageCompletionPct\":40,\"ClickThroughRatePct\":4}"
              + ",\"Evaluation\":{\"ExperimentId\":\"" + id + "\",\"WinnerLabel\":\"ChampionA\",\"ChampionScore\":50,\"ChallengerScore\":40,\"LiftPct\":25,\"TotalXpAwarded\":10,\"SummaryMessage\":\"\",\"EvaluatedAtUtc\":\""
                + registered.AddHours(1).ToString("o") + "\"}"
            : "";
        return "{\"ExperimentId\":\"" + id + "\",\"ThemeHint\":\"아기 첫 걸음마\",\"ChampionHook\":\"h1\",\"ChampionLook\":\"Natural Pastel\",\"ChampionBgm\":\"Lullaby\",\"ChallengerHook\":\"h2\",\"ChallengerLook\":\"Natural Pastel\",\"ChallengerBgm\":\"MusicBox\""
            + ",\"RegisteredAtUtc\":\"" + registered.ToString("o") + "\",\"Status\":\"" + (evaluated ? "Evaluated" : "Pending") + "\"" + m + extra + "}";
    }

    private static string Catalog(params string[] ids)
        => "{\"Version\":1,\"Clips\":[" + string.Join(",", ids.Select((id, i) => "{\"VideoId\":\"" + id + "\",\"Title\":\"" + id + "\",\"RenderedAtUtc\":\"" + Stamp.AddHours(-i).ToString("o") + "\"}")) + "]}";

    private static string Calib(int total, double cumulative, string window, string attributions, string extra = "")
        => "{\"Version\":1,\"CumulativeRegret\":" + cumulative.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"TotalRecords\":" + total
           + ",\"Window\":[" + window + "],\"Attributions\":{" + attributions + "}" + extra + "}";

    private static string Rec(DateTime utc, double? p, double r, double? a, string src)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"P\":" + (p is { } pv ? pv.ToString("R", inv) : "null") + ",\"R\":" + r.ToString("R", inv) + ",\"A\":" + (a is { } av ? av.ToString("R", inv) : "null")
               + ",\"Utc\":\"" + utc.ToString("o") + "\",\"Src\":\"" + src + "\"}";
    }

    private static PlutoVariantABPlan Plan(string id, string? mode = "슬라이드쇼", IReadOnlyList<string>? tags = null)
        => new(id, "아기 첫 걸음마",
            new PlutoExperimentVariant(PlutoVariantABGenerator.ChampionLabel, "h1", "Natural Pastel", "Lullaby", "", Array.Empty<SubtitleCue>()),
            new PlutoExperimentVariant(PlutoVariantABGenerator.ChallengerLabel, "h2", "Natural Pastel", "MusicBox", "", Array.Empty<SubtitleCue>()), "hyp")
        { ContextRenderMode = mode, ContextStyleTags = tags };

    private sealed class CapturingReinforcement : IPlutoClosedLoopReinforcementService
    {
        public PlutoExperimentReinforcementRequest? Last;
        public PlutoReinforcementOutcome? LastOutcome => null;
        public event Action? Changed { add { } remove { } }
        public Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "noop"));
        public Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "captured"));
        }
    }

    // ── ① ♻️ 무재시작 병합/복원 — 실행 중 싱글톤 메모리 즉시 갱신 · 손상 JSON 은 라이브 상태를 비우지 않음 ──

    [Fact]
    public async Task HotReload_MergeAndRestore_UpdateLiveSingletonsWithoutRestart_AndCorruptJsonNeverClobbersLiveState()
    {
        var (drive, local) = Dirs("live");
        File.WriteAllText(Path.Combine(drive, PlutoExperimentLedgerService.FileName), Ledger(Exp("pc-b-1", Stamp, true)));
        File.WriteAllText(Path.Combine(drive, PlutoRenderCatalogService.FileName), Catalog("live-clip"));
        var storage = new PlutoDriveStorageManager(drive, local);
        using var ledger = new PlutoExperimentLedgerService(null, storage);
        using var catalog = new PlutoRenderCatalogService(storage);
        await ledger.EnsureLoadedAsync();
        Assert.Equal(new[] { "live-clip" }, (await catalog.GetRecentClipsAsync(10)).Select(c => c.VideoId).ToArray());
        Assert.Equal(new[] { "pc-b-1" }, ledger.GetRecentEvaluations(10).Select(e => e.ExperimentId).ToArray());

        IPlutoDriveHealthService health = new PlutoDriveHealthService(null, null, storage) { HotReloadHubs = () => new IPlutoHotReloadable[] { ledger, catalog } };
        var reloaded = new List<string>();
        health.HubReloaded += reloaded.Add;

        // 🔀 병합 — 같은 인스턴스(재시작 없음)가 두 PC 의 실험을 모두 본다
        var mergeCopy = Conflict(local, "pluto_experiment_ledger", "20261101010000", Ledger(Exp("pc-a-1", Stamp.AddHours(2), true)));
        var merged = await health.MergeConflictLiveAsync(mergeCopy);
        Assert.Equal(PlutoConflictAction.MergeApplied, merged.Action);
        Assert.Contains("재시작 불필요", merged.MessageKorean, StringComparison.Ordinal);
        Assert.Equal(new[] { "pc-a-1", "pc-b-1" }, ledger.GetRecentEvaluations(10).Select(e => e.ExperimentId).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(0, health.PendingConflictResolutions);
        Assert.False(File.Exists(mergeCopy));
        Assert.Contains("pc-a-1", File.ReadAllText(Path.Combine(drive, PlutoExperimentLedgerService.FileName)), StringComparison.Ordinal);

        // ↩️ 복원 — 카탈로그 메모리가 즉시 사본으로 교체
        var restoreCopy = Conflict(local, "pluto_render_catalog", "20261101020000", Catalog("restored-clip"));
        Assert.Equal(PlutoConflictAction.RestoreApplied, (await health.RestoreConflictLiveAsync(restoreCopy)).Action);
        Assert.Equal(new[] { "restored-clip" }, (await catalog.GetRecentClipsAsync(10)).Select(c => c.VideoId).ToArray());
        Assert.Equal(new[] { PlutoExperimentLedgerService.FileName, PlutoRenderCatalogService.FileName }, reloaded.ToArray());

        // 🛡️ 손상 JSON — 재적재 거절 · 라이브 상태 유지 · 다음 기록이 디스크를 자가 치유
        File.WriteAllText(Path.Combine(drive, PlutoRenderCatalogService.FileName), "{\"Clips\":[");
        Assert.False(await catalog.ReloadFromDiskAsync());
        Assert.Equal(new[] { "restored-clip" }, (await catalog.GetRecentClipsAsync(10)).Select(c => c.VideoId).ToArray());
        File.WriteAllText(Path.Combine(drive, PlutoExperimentLedgerService.FileName), "{\"Experiments\":[{\"Experim");
        Assert.False(await ledger.ReloadFromDiskAsync());
        Assert.Equal(2, ledger.GetRecentEvaluations(10).Count);
        await catalog.RecordRenderedClipAsync(new PlutoRenderedClipRecord("new-clip", "n", "몽타주", "h", "l", "b", Array.Empty<string>(), "", DateTimeOffset.UtcNow, false));
        Assert.True(PlutoConflictMergeService.IsValidJsonText(File.ReadAllText(Path.Combine(drive, PlutoRenderCatalogService.FileName))));

        // 학습 메모리 · 오답 노트 — 손상/부재는 거절, 정상 파일은 즉시 반영
        var learnPath = Path.Combine(_dir, "learn.json");
        using var learning = new PlutoLearningService(learnPath);
        await learning.RecordInteractionAsync("trend-1", InteractionType.Adopted);
        var w = learning.GetWeight("trend-1");
        File.WriteAllText(learnPath, "{\"trend-1\":{\"Weight\":");
        Assert.False(await learning.ReloadFromDiskAsync());
        Assert.Equal(w, learning.GetWeight("trend-1"));
        File.WriteAllText(learnPath, "{\"trend-2\":{\"Weight\":1.5,\"Interactions\":2}}");
        Assert.True(await learning.ReloadFromDiskAsync());
        Assert.Equal((0.0, 1.5), (learning.GetWeight("trend-1"), learning.GetWeight("trend-2")));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb_missing.json"));
        Assert.False(await notebook.ReloadFromDiskAsync());

        // 재적재 불가 허브(아이덴티티 볼트)는 예약 유지 → 다음 적재 때 적용(기존 동작)
        var idCopy = Conflict(local, "pluto_identity_vault", "20261101030000", "{\"Profiles\":[]}");
        var staged = await health.RestoreConflictLiveAsync(idCopy);
        Assert.Equal(PlutoConflictAction.RestoreStaged, staged.Action);
        Assert.Equal(1, health.PendingConflictResolutions);

        // UI 결선 — 환경설정은 라이브 API · 워크스페이스는 재적재 통지 구독/해제 쌍
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        Assert.Contains("await DriveHealth.MergeConflictLiveAsync(c.FullPath)", settings, StringComparison.Ordinal);
        Assert.Contains("await DriveHealth.RestoreConflictLiveAsync(c.FullPath)", settings, StringComparison.Ordinal);
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Equal(1, CountOf(ws, "_hotReloadSource.HubReloaded += OnPlutoHubReloaded"));
        Assert.Equal(1, CountOf(ws, "_hotReloadSource.HubReloaded -= OnPlutoHubReloaded"));
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
            Assert.Equal(1, CountOf(File.ReadAllText(Path.Combine(RepoRoot(), file)), "HotReloadHubs = () => PromptFlowStudio.Services.Pluto.PlutoHotReload.ResolveHubs(sp)"));
    }

    // ── ② 🎯 pluto_calibration_state.json 의미 합집합 병합 ──

    [Fact]
    public void CalibrationStateUnionMerge_DedupesSamples_ReconcilesRegretWithoutDoubleCounting_AndPreservesUnknownFields()
    {
        var f = PlutoMetaCalibrationService.FileName;
        var shared = Rec(Stamp, 0.6, 0.4, 0.6, "ab");                 // 공통 이력(분기 이전)
        var onlyA = Rec(Stamp.AddHours(1), 0.5, 0.5, null, "perf");     // PC-A 고유 · 후회 0
        var onlyB = Rec(Stamp.AddHours(2), 0.7, 0.5, 0.9, "ab");        // PC-B 고유 · 후회 0.4
        var a = Calib(10, 0.30, shared + "," + onlyA, "\"hook:a\":{\"Regret\":0.2,\"Count\":2,\"Recovery\":0.2,\"Utc\":\"2026-11-01T00:00:00Z\",\"Future\":1}", ",\"FutureRoot\":\"keep\"");
        var b = Calib(12, 0.45, shared + "," + onlyB, "\"hook:a\":{\"Regret\":0.5,\"Count\":3,\"Recovery\":0,\"Utc\":\"2026-11-03T00:00:00Z\"},\"look:b\":{\"Regret\":0.1,\"Count\":1,\"Utc\":\"2026-11-02T00:00:00Z\"}");
        Assert.True(PlutoConflictMergeService.IsMergeable(f));
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);

        var merged = JsonNode.Parse(PlutoConflictMergeService.MergeJson(f, a, b)!)!.AsObject();
        var window = merged["Window"]!.AsArray();
        Assert.Equal(3, window.Count);                                                                  // 공통 1 + 각 1 · 중복 없음
        Assert.Equal(new[] { "ab", "perf", "ab" }, window.Select(n => (string)n!["Src"]!).ToArray());     // 시각순
        Assert.Equal(13, (int)merged["TotalRecords"]!);                                                 // max(10+1, 12+1) — 공통 이력 1회
        Assert.Equal(0.70, (double)merged["CumulativeRegret"]!, 9);                                      // max(0.30+0.4, 0.45+0)
        var ha = merged["Attributions"]!["hook:a"]!;
        Assert.Equal((0.5, 3, 0.0), ((double)ha["Regret"]!, (int)ha["Count"]!, (double)ha["Recovery"]!)); // 최댓값 · 잔액 = 최근(리허설 검증 0)
        Assert.NotNull(merged["Attributions"]!["look:b"]);
        Assert.Equal("keep", (string)merged["FutureRoot"]!);                                            // 모르는 필드 보존

        // 재병합 멱등 — 이미 합친 결과에 어느 쪽을 다시 합쳐도 집계가 부풀지 않는다
        foreach (var side in new[] { a, b })
        {
            var again = JsonNode.Parse(PlutoConflictMergeService.MergeJson(f, merged.ToJsonString(), side)!)!.AsObject();
            Assert.Equal((13, 3), ((int)again["TotalRecords"]!, again["Window"]!.AsArray().Count));
            Assert.Equal(0.70, (double)again["CumulativeRegret"]!, 9);
        }

        // 창 용량 · 귀속 용량(서비스 축출 규칙) · 손상 거절
        var many = string.Join(",", Enumerable.Range(0, 70).Select(i => Rec(Stamp.AddMinutes(i), 0.5, 0.5, null, "s" + i)));
        var attrs = string.Join(",", Enumerable.Range(0, 40).Select(i => "\"k" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "\":{\"Regret\":" + i + ",\"Count\":1}"));
        var big = JsonNode.Parse(PlutoConflictMergeService.MergeJson(f, Calib(70, 0, many, attrs), Calib(0, 0, "", ""))!)!.AsObject();
        Assert.Equal(PlutoMetaCalibrationService.WindowCapacity, big["Window"]!.AsArray().Count);
        Assert.Equal("s69", (string)big["Window"]!.AsArray()[^1]!["Src"]!);
        Assert.Equal(PlutoMetaCalibrationService.MaxAttributions, big["Attributions"]!.AsObject().Count);
        Assert.Null(big["Attributions"]!["k00"]);                                                       // 누적 후회 최소부터 축출
        Assert.Null(PlutoConflictMergeService.MergeJson(f, a, "{\"Window\":["));
    }

    // ── ③ 🏷️ 장부 RenderMode · StyleTags 영속 + 소급 존중 + 구 장부 하위 호환 ──

    [Fact]
    public async Task LedgerSchema_PersistsRenderModeAndStyleTags_BackfillHonorsThem_LegacyLedgerStaysCompatible()
    {
        var (_, local) = Dirs("schema");
        var storage = new PlutoDriveStorageManager(null, local);
        var file = Path.Combine(local, PlutoExperimentLedgerService.FileName);
        using (var ledger = new PlutoExperimentLedgerService(null, storage))
        {
            ledger.RegisterExperiment(Plan("exp-s", "슬라이드쇼", new[] { "#봄", " #봄 ", "" }));
            ledger.AttachVariantStyleTags("exp-s", new[] { "#햇살" }, new[] { "#비" });
            await ledger.WhenRegisteredPersistedAsync();
        }
        var entry = JsonNode.Parse(File.ReadAllText(file))!["Experiments"]![0]!;
        Assert.Equal("슬라이드쇼", (string)entry["RenderMode"]!);
        Assert.Equal(new[] { "#봄" }, entry["StyleTags"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(new[] { "#햇살" }, entry["ChampionStyleTags"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(new[] { "#비" }, entry["ChallengerStyleTags"]!.AsArray().Select(n => (string)n!).ToArray());

        // 재시작 후 — 대기 실험이 포맷 · 태그까지 복원(ToPlan 왕복)
        var reinforcement = new CapturingReinforcement();
        using (var reopened = new PlutoExperimentLedgerService(reinforcement, new PlutoDriveStorageManager(null, local)))
        {
            await reopened.EnsureLoadedAsync();
            var pending = Assert.Single(reopened.GetPendingExperiments(10));
            Assert.Equal("슬라이드쇼", pending.ContextRenderMode);
            Assert.Equal(new[] { "#봄" }, pending.ContextStyleTags!.ToArray());
            var ctx = reopened.GetExperimentContext("exp-s")!;
            Assert.Equal(new[] { "#햇살", "#봄" }, ctx.ChampionStyleTags!.ToArray());
            // 포맷이 빠진 플랜으로 판정해도 장부 기록이 우선
            await reopened.EvaluateExperimentAsync(pending with { ContextRenderMode = null, ContextStyleTags = null },
                new PlutoExperimentMetricsInput(70, 40, 4), new PlutoExperimentMetricsInput(40, 40, 4));
        }
        Assert.Equal("슬라이드쇼", reinforcement.Last!.ContextRenderMode);
        Assert.Equal(new[] { "#햇살", "#봄" }, reinforcement.Last.WinnerContextStyleTags!.ToArray());
        Assert.Equal(new[] { "#비", "#봄" }, reinforcement.Last.LoserContextStyleTags!.ToArray());

        // 🧬 소급 — 기록된 포맷 · 태그 사용(카탈로그 없이도) · 공통 태그(#봄)는 실험 변수 아님
        var samples = PlutoContextAlphaBackfill.FromLedgerJson(File.ReadAllText(file), null, out var n)!;
        Assert.Equal(1, n);
        Assert.All(samples, s => Assert.Equal("슬라이드쇼", s.RenderMode));
        Assert.Equal(new[] { "bgm:Lullaby", "bgm:MusicBox", "style:#비", "style:#햇살" }, samples.Select(s => s.Token).OrderBy(t => t, StringComparer.Ordinal).ToArray());

        // 구 장부(필드 없음) — 몽타주 + 카탈로그 조인 폴백 · 읽고 다시 써도 새 필드를 만들지 않음
        var legacy = Ledger(Exp("old-1", Stamp, true, ",\"ChampionVideoPath\":\"C:\\\\out\\\\old-1-A.mp4\""));
        var clip = new PlutoRenderedClipRecord("old-1-A", "t", "몽타주", "h1", "Natural Pastel", "Lullaby", new[] { "#가을" }, @"C:\out\old-1-A.mp4", new DateTimeOffset(Stamp), false);
        var legacySamples = PlutoContextAlphaBackfill.FromLedgerJson(legacy, new[] { clip }, out _)!;
        Assert.All(legacySamples, s => Assert.Equal(PlutoContextAlphaBackfill.DefaultRenderMode, s.RenderMode));
        Assert.Contains(legacySamples, s => s.Token == "style:#가을");
        var (_, local2) = Dirs("legacy");
        File.WriteAllText(Path.Combine(local2, PlutoExperimentLedgerService.FileName), Ledger(Exp("old-2", Stamp, false)));
        using (var old = new PlutoExperimentLedgerService(null, new PlutoDriveStorageManager(null, local2)))
        {
            await old.EnsureLoadedAsync();
            var p = Assert.Single(old.GetPendingExperiments(10));
            Assert.Null(p.ContextRenderMode);
            Assert.Null(p.ContextStyleTags);
            old.AttachVariantArtifacts("old-2", @"C:\out\old-2-A.mp4", null);
            await old.WhenRegisteredPersistedAsync();
        }
        var rewritten = File.ReadAllText(Path.Combine(local2, PlutoExperimentLedgerService.FileName));
        Assert.DoesNotContain("RenderMode", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("StyleTags", rewritten, StringComparison.Ordinal);
    }

    // ── ④ 🩺 라이브 Drive 마운트 프로브 ──

    [Fact]
    public async Task LiveDriveProbe_VerifiesAtomicWriteShaReadAndCleanup_ReportsLatency_AndStrictlySkipsWhenDriveOff()
    {
        var (drive, local) = Dirs("probe");
        var storage = new PlutoDriveStorageManager(drive, local);
        var probe = storage.ProbeLiveDrive();
        Assert.True(probe.Attempted && probe.Ok, probe.Detail);
        Assert.True(probe.RoundTripMs >= probe.WriteMs && probe.WriteMs >= 0 && probe.ReadMs >= 0);
        Assert.StartsWith("✅ 라이브 Drive 왕복 ", probe.StatusKorean, StringComparison.Ordinal);
        Assert.Contains("SHA-256 일치", probe.StatusKorean, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(drive));                                       // finally 정리(.tmp · 스테이징 .w 모두)

        // [🔄 지금 전체 드라이브 백업 검증] 에 실림 · 프로브 파일은 핵심 허브 점검에 섞이지 않음
        var report = await new PlutoDriveHealthService(null, null, storage).ForceSyncAndVerifyAsync();
        Assert.True(report.LiveProbe is { Attempted: true, Ok: true });
        Assert.Empty(Directory.EnumerateFiles(drive, PlutoDriveStorageManager.LiveProbePrefix + "*"));
        Assert.DoesNotContain(report.Items, i => i.FileName.StartsWith(PlutoDriveStorageManager.LiveProbePrefix, StringComparison.Ordinal));

        // 🔒 PROMPTFLOW_PLUTO_DRIVE=off(테스트 하네스 기본) → Primary 없음 → 디스크 무접촉 생략
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        Assert.Null(PlutoDriveStorageManager.DefaultPrimaryRoot());
        var (_, offLocal) = Dirs("probe_off");
        var off = new PlutoDriveStorageManager(PlutoDriveStorageManager.DefaultPrimaryRoot(), offLocal);
        var skipped = off.ProbeLiveDrive();
        Assert.False(skipped.Attempted);
        Assert.StartsWith("⏭️", skipped.StatusKorean, StringComparison.Ordinal);
        var offReport = await new PlutoDriveHealthService(null, null, off).ForceSyncAndVerifyAsync();
        Assert.False(offReport.LiveProbe!.Attempted);
        Assert.Empty(Directory.EnumerateFiles(offLocal, PlutoDriveStorageManager.LiveProbePrefix + "*", SearchOption.AllDirectories));

        // 미마운트(루트 없음) → 생략 · 폴더를 만들지 않음
        var unmounted = Path.Combine(_dir, "no_drive", "Pluto");
        Assert.False(new PlutoDriveStorageManager(unmounted, offLocal).ProbeLiveDrive().Attempted);
        Assert.False(Directory.Exists(unmounted));

        // UI — 환경설정 Drive 헬스 요약에 프로브 상태 · 지연
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        Assert.Contains("data-drive-live-probe=", settings, StringComparison.Ordinal);
        Assert.Contains("@(lp.StatusKorean)", settings, StringComparison.Ordinal);
    }

    // ── ⑤ 자율 추가 A — 실시간 A/B 컨텍스트 알파가 소급과 같은 태그 근거를 쓴다 ──

    [Fact]
    public async Task SelfInitiated_LiveAbContextAlpha_IncludesRecordedStyleTags_AndMatchesHistoricalBackfillExactly()
    {
        var (_, local) = Dirs("live_alpha");
        var reinforcement = new CapturingReinforcement();
        using (var ledger = new PlutoExperimentLedgerService(reinforcement, new PlutoDriveStorageManager(null, local)))
        {
            var plan = Plan("exp-live", "몽타주", new[] { "#공통" });
            ledger.RegisterExperiment(plan);
            ledger.AttachVariantStyleTags("exp-live", new[] { "#햇살" }, new[] { "#비" });
            await ledger.WhenRegisteredPersistedAsync();
            await ledger.EvaluateExperimentAsync(plan, new PlutoExperimentMetricsInput(70, 40, 4), new PlutoExperimentMetricsInput(40, 40, 4));
        }
        var r = reinforcement.Last!;
        Assert.Empty(r.Winner.StyleTags);                                                                // 차등 텔레메트리는 태그를 싣지 않는다(패턴 강화 불변)
        var winTokens = PlutoClosedLoopReinforcementService.ContextAlphaTokens(r.Winner, r.WinnerContextStyleTags);
        Assert.Contains("style:#햇살", winTokens);
        Assert.Equal(PlutoClosedLoopReinforcementService.ModalityObjectiveTokens(r.Winner), PlutoClosedLoopReinforcementService.ContextAlphaTokens(r.Winner, null));

        // 실시간 표본(폐루프와 같은 공식) == 장부 소급 표본(토큰 · 알파 · 포맷 · 도메인)
        var live = PlutoContextAlphaBackfill.PairSamples(r.ExperimentId, r.ContextDomain!, r.ContextRenderMode!, PlutoContextualBaseline.CompositeScore(r.Winner), PlutoContextualBaseline.CompositeScore(r.Loser),
            winTokens, PlutoClosedLoopReinforcementService.ContextAlphaTokens(r.Loser, r.LoserContextStyleTags), DateTimeOffset.UtcNow);
        var historical = PlutoContextAlphaBackfill.FromLedgerJson(File.ReadAllText(Path.Combine(local, PlutoExperimentLedgerService.FileName)), null, out _)!;
        static string[] Key(IEnumerable<PlutoContextAlphaSample> s) => s.Select(x => x.Token + "|" + x.Domain + "|" + x.RenderMode + "|" + Math.Round(x.Alpha, 9).ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(Key(historical), Key(live));
        Assert.Contains(live, s => s.Token == "style:#비" && s.Alpha < 0);
        Assert.DoesNotContain(live, s => s.Token == "style:#공통");                                        // 공통 태그 = 실험 변수 아님
    }

    // ── ⑥ 자율 추가 B — 복원 형태 가드: '{}' · 다른 허브 파일로 학습 상태 전체 소거 차단 ──

    [Fact]
    public async Task SelfInitiated_RestoreShapeGuard_RejectsEmptyOrWrongHubCopies_AndLiveStateSurvives()
    {
        var (drive, local) = Dirs("shape");
        File.WriteAllText(Path.Combine(drive, PlutoExperimentLedgerService.FileName), Ledger(Exp("keep-me", Stamp, true)));
        var storage = new PlutoDriveStorageManager(drive, local);
        using var ledger = new PlutoExperimentLedgerService(null, storage);
        await ledger.EnsureLoadedAsync();
        var health = new PlutoDriveHealthService(null, null, storage) { HotReloadHubs = () => new IPlutoHotReloadable[] { ledger } };

        foreach (var bad in new[] { "{}", Catalog("x"), "{\"Experiments\":{}}" })
        {
            var copy = Conflict(local, "pluto_experiment_ledger", "20261101000000", bad);
            var r = await health.RestoreConflictLiveAsync(copy);
            Assert.Equal(PlutoConflictAction.None, r.Action);
            Assert.Contains("형식이 아닌 사본", r.MessageKorean, StringComparison.Ordinal);
            Assert.True(File.Exists(copy));                                                                  // 사본 보존(무시만 가능)
            Assert.Equal(PlutoConflictAction.Dismissed, health.DismissConflict(copy).Action);
        }
        Assert.Equal(new[] { "keep-me" }, ledger.GetRecentEvaluations(10).Select(e => e.ExperimentId).ToArray());
        Assert.Equal(0, health.PendingConflictResolutions);

        Assert.True(PlutoHotReload.HasExpectedShape(PlutoExperimentLedgerService.FileName, Ledger()));
        Assert.False(PlutoHotReload.HasExpectedShape(PlutoLearningService.FileName, "{}"));
        Assert.False(PlutoHotReload.HasExpectedShape(PlutoLearningService.FileName, "{\"t\":1}"));
        Assert.True(PlutoHotReload.HasExpectedShape(PlutoLearningService.FileName, "{\"t\":{\"Weight\":1}}"));
        Assert.False(PlutoHotReload.HasExpectedShape(PlutoMetaCalibrationService.FileName, "{\"Version\":1}"));
        Assert.True(PlutoHotReload.HasExpectedShape(PlutoMistakeNotebookService.FileName, "{\"Version\":1,\"Entries\":{}}"));
        Assert.True(PlutoHotReload.HasExpectedShape(PlutoIdentityVault.FileName, "{}"));                         // 형태 미정의 허브 → 객체 루트면 통과
        Assert.False(PlutoHotReload.HasExpectedShape(PlutoSeriesArcMemoryService.FileName, "[]"));
    }

    // ── ⑦ 자율 추가 C — 재적재는 진행 중 기록을 먼저 배수: 막 등록된 실험도 병합 결과도 잃지 않는다 ──

    [Fact]
    public async Task SelfInitiated_HotReloadDrainsInFlightPersists_SoQueuedWritesNeverClobberTheMerge()
    {
        var (drive, local) = Dirs("drain");
        File.WriteAllText(Path.Combine(drive, PlutoExperimentLedgerService.FileName), Ledger(Exp("disk-1", Stamp, true)));
        var storage = new PlutoDriveStorageManager(drive, local);
        using var ledger = new PlutoExperimentLedgerService(null, storage);
        await ledger.EnsureLoadedAsync();
        var health = new PlutoDriveHealthService(null, null, storage) { HotReloadHubs = () => new IPlutoHotReloadable[] { ledger } };

        var copy = Conflict(local, "pluto_experiment_ledger", "20261101000000", Ledger(Exp("other-pc", Stamp.AddHours(3), true)));
        for (var i = 0; i < 5; i++) ledger.RegisterExperiment(Plan("fresh-" + i));                       // 기록 체인 진행 중 — 기다리지 않고 바로 병합
        Assert.Equal(PlutoConflictAction.MergeApplied, (await health.MergeConflictLiveAsync(copy)).Action);
        await ledger.WhenRegisteredPersistedAsync();

        var ids = new HashSet<string>(ledger.GetRecentEvaluations(10).Select(e => e.ExperimentId).Concat(ledger.GetPendingExperiments(10).Select(p => p.ExperimentId)), StringComparer.Ordinal);
        Assert.True(ids.SetEquals(new[] { "disk-1", "other-pc", "fresh-0", "fresh-1", "fresh-2", "fresh-3", "fresh-4" }), string.Join(",", ids));
        var disk = File.ReadAllText(Path.Combine(drive, PlutoExperimentLedgerService.FileName));
        foreach (var id in ids) Assert.Contains("\"" + id + "\"", disk, StringComparison.Ordinal);       // 뒤이은 기록도 병합 결과를 보존

        // 시리즈 아크 — 예약된 백그라운드 기록이 있어도 재적재 후 디스크 = 메모리
        var arcPath = Path.Combine(_dir, "arc.json");
        using var arc = new PlutoSeriesArcMemoryService(arcPath);
        await arc.EnsureLoadedAsync();
        Assert.False(await arc.ReloadFromDiskAsync());                                                   // 파일 없음 → 거절(라이브 유지)
        File.WriteAllText(arcPath, "{\"Version\":1,\"Series\":[{\"Key\":\"s1\",\"Title\":\"봄\",\"Total\":3,\"UpdatedUtc\":\"2026-11-01T00:00:00Z\",\"Episodes\":[{\"N\":1,\"VideoId\":\"v1\"}]}]}");
        Assert.True(await arc.ReloadFromDiskAsync());
        await arc.FlushAsync();
        Assert.Contains("\"s1\"", File.ReadAllText(arcPath), StringComparison.Ordinal);
    }

    // ── ⑧ 자율 추가 D — 보정 상태 재적재 시 .bak 갱신: 이후 본 파일이 깨져도 병합 이전으로 되돌아가지 않는다 ──

    [Fact]
    public async Task SelfInitiated_CalibrationLiveMerge_RefreshesBackup_SoLaterCorruptionCannotRollBackTheMerge()
    {
        var (drive, local) = Dirs("calib");
        var f = PlutoMetaCalibrationService.FileName;
        var shared = Rec(Stamp, 0.6, 0.4, 0.6, "ab");
        var a = Calib(10, 0.30, shared, "");
        File.WriteAllText(Path.Combine(drive, f), a);
        File.WriteAllText(Path.Combine(drive, f + PlutoMetaCalibrationService.BackupSuffix), a);
        var storage = new PlutoDriveStorageManager(drive, local);
        using var calib = new PlutoMetaCalibrationService(storage);
        await calib.EnsureLoadedAsync();
        Assert.Equal(10, calib.GetStatus().TotalRecords);

        var health = new PlutoDriveHealthService(null, null, storage) { HotReloadHubs = () => new IPlutoHotReloadable[] { calib } };
        var copy = Conflict(local, "pluto_calibration_state", "20261101000000",
            Calib(12, 0.45, shared + "," + Rec(Stamp.AddHours(2), 0.7, 0.5, 0.9, "ab"), "\"hook:x\":{\"Regret\":0.4,\"Count\":1,\"Recovery\":0.4,\"Utc\":\"2026-11-01T02:00:00Z\"}"));
        Assert.True(health.ListConflicts().Single().IsMergeable);
        Assert.Equal(PlutoConflictAction.MergeApplied, (await health.MergeConflictLiveAsync(copy)).Action);
        var live = calib.GetStatus();
        Assert.Equal((12, 2), (live.TotalRecords, live.CalibrationSamples));                                // 즉시 반영 · max(10+1, 12+0)
        Assert.Equal(0.70, live.CumulativeRegret, 9);
        Assert.Equal("hook:x", live.TopRegretAlternative!.Label);
        Assert.Equal(12, (int)JsonNode.Parse(File.ReadAllText(Path.Combine(drive, f + PlutoMetaCalibrationService.BackupSuffix)))!["TotalRecords"]!);

        // 본 파일 손상 → 새 세션은 .bak(= 병합 결과)에서 복구
        File.WriteAllText(Path.Combine(drive, f), "{\"Window\":[");
        using var next = new PlutoMetaCalibrationService(new PlutoDriveStorageManager(drive, local));
        await next.EnsureLoadedAsync();
        Assert.True(next.RecoveredFromBackup);
        Assert.Equal(12, next.GetStatus().TotalRecords);
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
