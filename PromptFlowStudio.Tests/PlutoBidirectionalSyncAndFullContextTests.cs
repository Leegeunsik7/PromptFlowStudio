using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// ☁️🧬💬 Step 18 — 양방향 Drive 조정(오프라인 쓰기 반환) · BGM 무드 · 스타일 태그 계층적 경험적 베이즈 컨텍스트 특화 · 비파괴 자막 모드/시즌 전환 ·
/// 자율 추가(적재 전 첫 해석 쓰기 반환 · 분기 충돌 보관 · 자막 편집 무결성).
/// </summary>
public sealed class PlutoBidirectionalSyncAndFullContextTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string DomA = "GeneralCinematic", ModeA = "Montage", DomB = "BabyStudio", ModeB = "Series3";
    private const string Lullaby = "Lullaby", MusicBox = "MusicBox";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step18_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    public PlutoBidirectionalSyncAndFullContextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };

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

    // ── ① ☁️⬆️ 양방향 조정 — 더 새로운 유효 로컬 → Drive · 더 새로운 Drive 보호 · OFF 격리 ──

    [Fact]
    public async Task WriteBack_PushesNewerValidLocalCoreFilesToDrive_ProtectsNewerDrive_NoPingPong_AndHonorsDriveOff()
    {
        var (drive, local) = Dirs("wb");
        string D(string f) => Path.Combine(drive, f);
        string L(string f) => Path.Combine(local, f);
        const string learn = PlutoLearningService.FileName, catalog = PlutoRenderCatalogService.FileName, arc = PlutoSeriesArcMemoryService.FileName,
            notebook = PlutoMistakeNotebookService.FileName, ledger = PlutoExperimentLedgerService.FileName, calib = PlutoMetaCalibrationService.FileName;
        Put(D(learn), "{\"v\":\"drive-old\"}", Stamp); Put(L(learn), "{\"v\":\"offline-newer\"}", Stamp.AddHours(3));          // ⬆️ 반환
        Put(D(catalog), "{\"v\":\"drive-newer\"}", Stamp.AddHours(5)); Put(L(catalog), "{\"v\":\"local-older\"}", Stamp);       // 🛡️ 더 새로운 Drive 보호
        Put(L(arc), "{\"v\":\"arc-offline\"}", Stamp.AddHours(1));                                                              // ⬆️ Drive 부재
        Put(D(notebook), "{\"v\":\"drive\"}", Stamp); Put(L(notebook), "{\"v\":", Stamp.AddHours(3));                          // 손상 로컬 → 반환 안 함
        Put(D(ledger), "{\"v\":\"drive\"}", Stamp); Put(L(ledger), "", Stamp.AddHours(3));                                     // 0 B 로컬 → 반환 안 함
        Put(D(calib), "{\"v\":\"d\"}", Stamp.AddSeconds(1)); Put(L(calib), "{\"v\":\"l\"}", Stamp.AddSeconds(2));               // 허용 오차 이내

        Assert.True(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(learn), D(learn)));
        Assert.True(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(arc), D(arc)));
        Assert.False(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(catalog), D(catalog)));
        Assert.False(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(notebook), D(notebook)));
        Assert.False(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(ledger), D(ledger)));
        Assert.False(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L(calib), D(calib)));
        Assert.False(PlutoDriveStorageManager.IsLocalNewerForWriteBack(L("missing.json"), D(learn)));

        var storage = new PlutoDriveStorageManager(drive, local);
        Assert.Equal(2, storage.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"v\":\"offline-newer\"}", File.ReadAllText(D(learn)));
        Assert.Equal(Stamp.AddHours(3), File.GetLastWriteTimeUtc(D(learn)));                                                // 수정 시각 보존
        Assert.Equal("{\"v\":\"arc-offline\"}", File.ReadAllText(D(arc)));
        Assert.Equal("{\"v\":\"drive-newer\"}", File.ReadAllText(D(catalog)));
        Assert.Equal("{\"v\":\"drive\"}", File.ReadAllText(D(notebook)));
        Assert.Equal("{\"v\":\"drive\"}", File.ReadAllText(D(ledger)));
        Assert.Equal("{\"v\":\"d\"}", File.ReadAllText(D(calib)));
        Assert.Empty(Directory.EnumerateFiles(drive, "*.tmp"));
        Assert.Equal(Stamp.AddHours(3), storage.GetLastSyncedUtc(learn));
        Assert.Equal(0, storage.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));                             // 멱등

        // 반대 방향(복원)은 더 새로운 Drive 카탈로그만 로컬로 — 방금 반환한 파일과 핑퐁 없음
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"v\":\"drive-newer\"}", File.ReadAllText(L(catalog)));
        Assert.Equal(0, storage.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal(0, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.False(File.Exists(Path.Combine(drive, PlutoDriveStorageManager.SyncManifestFileName)));                        // 장부는 로컬 전용

        // 🔄 [지금 전체 드라이브 백업 검증] — 오프라인 학습분 반환 후 9종 검증
        var (drive2, local2) = Dirs("verify");
        foreach (var f in PlutoDriveHealthService.PlutoFiles) Put(Path.Combine(drive2, f), "{\"origin\":\"drive\"}", Stamp);
        Put(Path.Combine(local2, PlutoSocialRadarEngine.FileName), "{\"origin\":\"offline-radar\"}", Stamp.AddDays(1));
        var verified = await new PlutoDriveHealthService(null, null, new PlutoDriveStorageManager(drive2, local2)).ForceSyncAndVerifyAsync();
        Assert.Equal("{\"origin\":\"offline-radar\"}", File.ReadAllText(Path.Combine(drive2, PlutoSocialRadarEngine.FileName)));
        Assert.All(verified.Items.Where(i => i.Category == PlutoDriveHealthService.CategoryPluto), i => Assert.Equal(PlutoDriveFileStatus.Healthy, i.Status));
        Assert.Equal("{\"origin\":\"offline-radar\"}", File.ReadAllText(Path.Combine(local2, PlutoSocialRadarEngine.FileName)));   // 로컬은 그대로

        // 🔒 PROMPTFLOW_PLUTO_DRIVE=off → 반환 0 · 아무 파일도 만들지 않음
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        var offLocal = Path.Combine(_dir, "off_local");
        Put(Path.Combine(offLocal, learn), "{\"v\":\"offline\"}", Stamp.AddDays(1));
        var off = new PlutoDriveStorageManager(PlutoDriveStorageManager.DefaultPrimaryRoot(), offLocal);
        Assert.Equal(0, off.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal(new[] { learn }, Directory.EnumerateFiles(offLocal).Select(Path.GetFileName).ToArray());

        // 결선 — 기동 조정(쓰기 반환 → 복원 순서)
        var mainForm = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "MainForm.cs"));
        Assert.Contains("plutoStore.WriteBackNewerLocalToDrive(PromptFlowStudio.Services.Pluto.PlutoDriveHealthService.PlutoFiles); plutoStore.RestoreCoreHubsToLocal(PromptFlowStudio.Services.Pluto.PlutoDriveHealthService.PlutoFiles);", mainForm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteBack_OnReconnect_PushesFilesStrandedInLocalFallbackDuringOutage()
    {
        var (drive, local) = Dirs("reconnect");
        var storage = new PlutoDriveStorageManager(drive, local) { ProbeWindow = TimeSpan.FromMilliseconds(40) };
        Assert.True(await storage.WriteAllTextAtomicAsync("mem.json", "{\"v\":1}"));
        var target = Path.Combine(drive, "mem.json");

        // 📴 Drive 잠김(동기화 클라이언트 · 오프라인) → 로컬 폴백에만 기록 → 고립 1건
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await storage.WriteAllTextAtomicAsync("mem.json", "{\"v\":2}"));
        Assert.Equal(1, storage.StrandedCount);
        Assert.False(storage.IsCloudSynced);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddHours(-1));   // 장시간 오프라인 모사

        // ☁️ 재연결 감지 → 고립 파일 즉시 반환(다음 기록 · 재시작을 기다리지 않음)
        await Task.Delay(120);
        Assert.True(storage.IsCloudSynced);
        await storage.WaitForStrandedPushAsync();
        Assert.Equal("{\"v\":2}", File.ReadAllText(target));
        Assert.Equal(0, storage.StrandedCount);

        // 재연결 사이 다른 PC 가 Drive 를 더 새롭게 갱신했다면 반환하지 않는다
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(await storage.WriteAllTextAtomicAsync("mem.json", "{\"v\":3}"));
        File.WriteAllText(target, "{\"v\":\"other-pc\"}");
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddHours(1));
        await Task.Delay(120);
        Assert.True(storage.IsCloudSynced);
        await storage.WaitForStrandedPushAsync();
        Assert.Equal("{\"v\":\"other-pc\"}", File.ReadAllText(target));
    }

    // ── ② 🧬 BGM 무드 · 스타일 태그 계층적 경험적 베이즈 컨텍스트 특화 ──

    private static Task Obs(PlutoLearningService l, string domain, string mode, double score, params string[] objectiveTokens)
        => l.RecordContextualObservationAsync(domain, mode, score, null, default, null, objectiveTokens);

    [Fact]
    public async Task BgmAndTags_ColdStartShrinkTowardProvenGlobalPriors_MatureIntoLocalSpecialization_AndStayLegacyCompatible()
    {
        var learning = Learning();
        var ranker = new PlutoAdaptivePatternRanker(learning, null);
        for (var i = 0; i < 5; i++)
        {
            await Obs(learning, DomA, ModeA, 0.70, "bgm:" + Lullaby, "style:#봄");
            await Obs(learning, DomA, ModeA, 0.50, "bgm:" + MusicBox, "style:가을");
        }
        await learning.ApplyPatternReinforcementAsync(new[] { "bgm:" + Lullaby }, 1.14, "xp:test", 0);
        await learning.ApplyPatternReinforcementAsync(new[] { "bgm:" + Lullaby }, 1.14, "xp:test", 0);
        var g = learning.GetPatternAlphaStats("bgm:" + Lullaby);
        Assert.True(g.Alpha > 0.05 && g.Samples >= 5);                                                   // BGM 도 전역 잔차 알파 보유
        Assert.True(learning.GetPatternAlphaStats("bgm:" + MusicBox).Alpha < -0.05);
        Assert.Equal(5.0, learning.GetContextPatternAlpha(DomA, ModeA, "bgm:" + Lullaby).Samples);
        Assert.Equal(PlutoRegimeDriftStatus.Stable, learning.GetRegimeDriftStatus());                    // 기대치 · 드리프트 검정에는 여전히 불참
        var bgmCandidates = new[] { Lullaby, MusicBox };
        Assert.Equal(Lullaby, ranker.RankOptions(PlutoPatternKind.BgmMood, bgmCandidates)[0].DisplayLabel);

        // ❄️ 콜드스타트 B — 관측 0 → 전역 사전 100% · 단발 부진 1회 → 수축 덕분에 1위 유지
        var untouched = ranker.RankForContext(PlutoPatternKind.BgmMood, DomB, ModeB, bgmCandidates);
        Assert.Equal((Lullaby, 1.0, true), (untouched[0].DisplayLabel, untouched[0].ContextMultiplier, untouched[0].ContextTransfer.IsTransferActive));
        await Obs(learning, DomB, ModeB, 0.40, "bgm:" + Lullaby, "style:봄");
        var local = learning.GetContextPatternAlpha(DomB, ModeB, "bgm:" + Lullaby);
        Assert.Equal(1.0, local.Samples);
        g = learning.GetPatternAlphaStats("bgm:" + Lullaby);
        var expected = PlutoEmpiricalBayes.Shrink(local.Alpha, local.Samples, g.Alpha, g.Samples, g.Variance);
        var cold = ranker.RankForContext(PlutoPatternKind.BgmMood, DomB, ModeB, bgmCandidates);
        var lul = cold.Single(o => o.DisplayLabel == Lullaby);
        Assert.Equal(expected, lul.ContextTransfer);
        Assert.True(lul.ContextTransfer.IsColdStart && lul.ContextTransfer.IsTransferActive);
        Assert.Equal(1.0 / (1.0 + expected.PriorStrength), lul.ContextTransfer.LocalWeight, 12);
        Assert.Equal(Lullaby, cold[0].DisplayLabel);
        var naive = Math.Clamp(1.0 + PlutoEmpiricalBayes.ContextAlphaGain * (local.Alpha - g.Alpha), PlutoEmpiricalBayes.MinContextMultiplier, PlutoEmpiricalBayes.MaxContextMultiplier);
        Assert.True(lul.FinalScore / lul.ContextMultiplier * naive < cold.Single(o => o.DisplayLabel == MusicBox).FinalScore);   // 수축 없으면 역전

        // 🏷️ 태그 — '#봄'(전역) · '봄'(지역 1회)은 같은 태그로 병합
        var tagCandidates = new[] { "봄", "가을" };
        var tags = ranker.RankForContext(PlutoPatternKind.StyleTag, DomB, ModeB, tagCandidates);
        var spring = tags.Single(o => o.DisplayLabel == "봄").ContextTransfer;
        Assert.True(spring.IsColdStart && spring.IsGlobalProven);
        Assert.Equal(1.0, spring.LocalTrials);
        Assert.True(spring.GlobalTrials >= 6);                                                           // '#봄' 5 + '봄' 1
        var hashed = ranker.RankForContext(PlutoPatternKind.StyleTag, DomB, ModeB, new[] { "#봄" }).Single().ContextTransfer;
        Assert.Equal((spring.LocalTrials, spring.GlobalTrials), (hashed.LocalTrials, hashed.GlobalTrials));
        var merged = PlutoEmpiricalBayes.MergeVariants(0.2, 0.01, 3, -0.1, 0.03, 1);
        Assert.Equal((0.125, 0.015, 4.0), (Math.Round(merged.Mean, 12), Math.Round(merged.Variance, 12), merged.Samples));
        Assert.Equal((0.5, 0.02, 2.0), PlutoEmpiricalBayes.MergeVariants(0.5, 0.02, 2, double.NaN, 1, 0));

        // 🧠 두뇌 리포트 — 모달리티 분해(BGM · 태그 사전이 있을 때만 꼬리)
        var status = ranker.GetColdStartTransferStatus();
        Assert.Equal((0, 0, 2, 2), (status.ProvenHookPriors, status.ProvenLookPriors, status.ProvenBgmPriors, status.ProvenTagPriors));
        Assert.Equal(4, status.ProvenPriorCount);
        var summary = await new PlutoAutonomousBrainSummaryService(null, ranker, null).GetSummaryAsync();
        Assert.EndsWith(" (훅 0 · 룩 0 · BGM 2 · 태그 2)", summary.ColdStartTransferLabel, StringComparison.Ordinal);
        Assert.StartsWith("콜드스타트 1/2개 컨텍스트 · 전역 검증 사전 4개 이식 중", summary.ColdStartTransferLabel, StringComparison.Ordinal);

        // 🌡️ 성숙(지역 시행 ≥ 3) — B 에서는 MusicBox · '가을'이 실제로 더 낫다 → 지역 데이터가 전역 사전을 이긴다
        for (var i = 0; i < 4; i++)
        {
            await Obs(learning, DomA, ModeA, 0.70, "bgm:" + Lullaby, "style:#봄");
            await Obs(learning, DomA, ModeA, 0.50, "bgm:" + MusicBox, "style:가을");
            await Obs(learning, DomB, ModeB, 0.40, "bgm:" + Lullaby, "style:봄");
            await Obs(learning, DomB, ModeB, 0.60, "bgm:" + MusicBox, "style:가을");
        }
        var mature = ranker.RankForContext(PlutoPatternKind.BgmMood, DomB, ModeB, bgmCandidates);
        Assert.Equal(MusicBox, mature[0].DisplayLabel);
        Assert.All(mature, o => Assert.Equal((false, 1.0), (o.ContextTransfer.IsColdStart, o.ContextTransfer.LocalWeight)));
        Assert.Equal(Lullaby, ranker.RankForContext(PlutoPatternKind.BgmMood, DomA, ModeA, bgmCandidates)[0].DisplayLabel);
        Assert.Equal(Lullaby, ranker.RankOptions(PlutoPatternKind.BgmMood, bgmCandidates)[0].DisplayLabel);   // 전역 API 불변
        var matureTags = ranker.RankForContext(PlutoPatternKind.StyleTag, DomB, ModeB, tagCandidates);
        Assert.Equal("가을", matureTags[0].DisplayLabel);
        Assert.Equal("봄", ranker.RankForContext(PlutoPatternKind.StyleTag, DomA, ModeA, tagCandidates)[0].DisplayLabel);
        Assert.Equal(2, ranker.GetColdStartTransferStatus().ProvenTagPriors);                            // '#봄' · '봄' 둘 다 검증돼도 태그 1개

        // 🗃️ 구버전 학습 파일(BGM · 태그에 컨텍스트 이력 · 알파 없음) — 다목적 EWMA 만 있어도 순위 완전 불변
        var legacyPath = Path.Combine(_dir, "legacy_trend.json");
        File.WriteAllText(legacyPath, "{\"pattern:bgm:" + Lullaby + "\":{\"Weight\":1.2,\"Interactions\":1,\"LastUtc\":\"2026-11-19T00:00:00Z\",\"ObjH\":0.6,\"ObjC\":0.5,\"ObjT\":0.4,\"ObjN\":3,\"ObjUtc\":\"2026-11-19T00:00:00Z\"},"
            + "\"pattern:style:#봄\":{\"Weight\":1.1,\"Interactions\":1,\"LastUtc\":\"2026-11-19T00:00:00Z\"}}");
        var legacy = new PlutoLearningService(legacyPath) { UtcNowProvider = () => _now };
        await legacy.EnsureLoadedAsync();
        var legacyRanker = new PlutoAdaptivePatternRanker(legacy, null);
        foreach (var kind in new[] { PlutoPatternKind.BgmMood, PlutoPatternKind.StyleTag })
        {
            var cands = kind == PlutoPatternKind.BgmMood ? bgmCandidates : tagCandidates;
            var ctx = legacyRanker.RankForContext(kind, DomB, ModeB, cands);
            var glob = legacyRanker.RankOptions(kind, cands);
            Assert.Equal(glob.Select(o => (o.Key, o.FinalScore)), ctx.Select(o => (o.Key, o.FinalScore)));
            Assert.All(ctx, o => Assert.Equal((1.0, false), (o.ContextMultiplier, o.ContextTransfer.HasEvidence)));
        }
        Assert.Equal(0, legacyRanker.GetColdStartTransferStatus().ProvenPriorCount);
        await legacy.RecordPatternObjectivesAsync(new[] { "bgm:" + MusicBox }, new PlutoObjectiveVector(0.5, 0.5, 0.5));   // 다목적 전용 경로는 알파를 만들지 않는다
        Assert.Equal(0.0, legacy.GetPatternAlphaStats("bgm:" + MusicBox).Samples);
    }

    [Fact]
    public async Task VariantGenerator_ContextSpecializesChampionBgm_WhenLocalBgmEvidenceIsMature()
    {
        const string theme = "아기 첫 걸음마";
        var domain = PromptDomainClassifier.Classify(theme).ToString();
        var other = domain == DomA ? DomB : DomA;
        var learning = Learning("ab_bgm.json");
        var ranker = new PlutoAdaptivePatternRanker(learning, null);
        for (var i = 0; i < 4; i++)
        {
            await Obs(learning, other, "Ambient15s", 0.70, "bgm:" + Lullaby);
            await Obs(learning, other, "Ambient15s", 0.50, "bgm:" + MusicBox);
        }
        await learning.ApplyPatternReinforcementAsync(new[] { "bgm:" + Lullaby }, 1.14, "xp:test", 0);
        await learning.ApplyPatternReinforcementAsync(new[] { "bgm:" + Lullaby }, 1.14, "xp:test", 0);

        // 콜드스타트 몽타주 컨텍스트 → 전역 챔피언 BGM 그대로 · 전이 추정 노출
        var cold = await new PlutoVariantABGenerator(ranker, null, null, () => "exp-bgm-cold").BuildVariantPairAsync(theme, "몽타주", null, null, default);
        Assert.Equal(Lullaby, cold.ChampionA.BgmMood);
        Assert.False(cold.IsContextSpecialized);
        Assert.True(cold.ChampionBgmContextTransfer.IsTransferActive);

        // 몽타주 컨텍스트가 성숙 — 이 포맷에서는 MusicBox 가 낫다 → 챔피언 BGM 교체(훅 · 룩 근거 없이 BGM 만으로도 특화)
        for (var i = 0; i < 4; i++)
        {
            await Obs(learning, other, "Ambient15s", 0.70, "bgm:" + Lullaby);
            await Obs(learning, other, "Ambient15s", 0.50, "bgm:" + MusicBox);
            await Obs(learning, domain, "몽타주", 0.35, "bgm:" + Lullaby);
            await Obs(learning, domain, "몽타주", 0.75, "bgm:" + MusicBox);
        }
        var plan = await new PlutoVariantABGenerator(ranker, null, null, () => "exp-bgm").BuildVariantPairAsync(theme, "몽타주", null, null, default);
        Assert.True(plan.IsContextSpecialized);
        Assert.Equal(MusicBox, plan.ChampionA.BgmMood);
        Assert.False(plan.ChampionBgmContextTransfer.IsColdStart);
        Assert.True(plan.ChampionBgmContextTransfer.LocalTrials >= 3);
        Assert.NotEqual(plan.ChampionA.BgmMood, plan.ChallengerB.BgmMood);
        Assert.Equal(Lullaby, (await new PlutoVariantABGenerator(ranker, null, null, () => "exp-bgm-g").BuildVariantPairAsync(theme)).ChampionA.BgmMood);   // 컨텍스트 미지정 → 전역
    }

    // ── ③ 💬 비파괴 자막 모드 전환 · 시즌 분리 ──

    private static readonly SubtitleCue[] Base =
    {
        SubtitleCue.Precise(0.2, 4.9, "우리 아기 첫 봄나들이", "Our baby's first spring outing"),
        SubtitleCue.Precise(5.1, 9.9, "꽃잎을 만져 봐요", "Touching the petals"),
        SubtitleCue.Precise(10.1, 14.7, "다시 또 오자", "Let's come again"),
    };

    private static PlutoSeriesContinuityDirective Escalation(int nextEpisode = 2)
        => new("봄#3", nextEpisode, 3, "", "", 2, PlutoSeriesDropAxis.Both, PlutoSeriesArcMemoryService.CuriosityGapHooks[1], PlutoSeriesArcMemoryService.CliffhangerOutros[1], -12, "이탈");

    private static string[] Texts(IReadOnlyList<SubtitleCue> cues) => cues.Select(c => c.Text).ToArray();

    [Fact]
    public void SubtitleOverlay_ModeToggleAndSeasonSplit_PreserveManualEdits_WithoutRegeneration()
    {
        const PlutoSeriesEscalationMode Auto = PlutoSeriesEscalationMode.AutoRewrite, Rec = PlutoSeriesEscalationMode.RecommendOnly;
        var d = Escalation();
        var hook = d.RecommendedHookLine;
        var outroTail = PlutoSeriesArcMemoryService.OutroJoiner + d.CliffhangerOutroLine;
        var state = PlutoSubtitleOverlayState.FromBase(Base);

        // 자동 각색 — 원문 위 덧입힘 + 장식
        var shown = PlutoSeriesSubtitleOverlay.Compose(state, d, Auto, 2, 3);
        Assert.Equal(new[] { "2/3편 · " + hook, "꽃잎을 만져 봐요", "다시 또 오자" + outroTail + PlutoSeriesBatchPlanner.ContinueSuffix }, Texts(shown));
        Assert.Equal(Base.Select(c => (c.EffectiveStartSec, c.EffectiveEndSec)), shown.Select(c => (c.EffectiveStartSec, c.EffectiveEndSec)));

        // ✏️ 가운데 줄 수동 편집 → 원문 반영(장식 · 덧입힘은 원문에 들어가지 않음)
        state = PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, 1, "꽃잎에 살짝 손을 대요");
        Assert.Equal(new[] { "우리 아기 첫 봄나들이", "꽃잎에 살짝 손을 대요", "다시 또 오자" }, Texts(state.BaseCues));
        Assert.Equal(1, state.ManualEditCount);
        // ✏️ 마지막 줄 — 아웃트로 꼬리 · 장식을 그대로 둔 채 앞부분만 고침 → 원문 앞부분만 갱신 · 아웃트로는 계속 덧입힘
        state = PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, 2, "내일 또 오자" + outroTail + PlutoSeriesBatchPlanner.ContinueSuffix);
        Assert.Equal("내일 또 오자", state.BaseCues[2].Text);
        Assert.False(state.OutroPinnedByUser);

        // 💡 추천만 표시 → 사용자 원문 그대로(재생성 없음 · 훅/아웃트로 없음)
        var rec = PlutoSeriesSubtitleOverlay.Compose(state, d, Rec, 2, 3);
        Assert.Equal(new[] { "2/3편 · 우리 아기 첫 봄나들이", "꽃잎에 살짝 손을 대요", "내일 또 오자" + PlutoSeriesBatchPlanner.ContinueSuffix }, Texts(rec));
        Assert.Same(state.BaseCues, PlutoSeriesSubtitleOverlay.Overlay(state, d, Rec));
        // ✍️ 다시 자동 각색 → 현재 원문 위에 깨끗이 덧입힘
        Assert.Equal(new[] { "2/3편 · " + hook, "꽃잎에 살짝 손을 대요", "내일 또 오자" + outroTail + PlutoSeriesBatchPlanner.ContinueSuffix }, Texts(PlutoSeriesSubtitleOverlay.Compose(state, d, Auto, 2, 3)));
        // 왕복 여러 번 — 원문 불변
        var before = Texts(state.BaseCues);
        for (var i = 0; i < 3; i++) { _ = PlutoSeriesSubtitleOverlay.Compose(state, d, Rec, 2, 3); _ = PlutoSeriesSubtitleOverlay.Compose(state, d, Auto, 2, 3); }
        Assert.Equal(before, Texts(state.BaseCues));

        // ✏️ 덧입힌 훅 자리를 직접 고쳐 씀 → 그 자리는 사용자 고정(자동 각색이 다시 덮지 않음) · 아웃트로는 계속
        state = PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, 0, "2/3편 · 오늘은 어디로 갈까?");
        Assert.True(state.HookPinnedByUser);
        Assert.Equal("오늘은 어디로 갈까?", state.BaseCues[0].Text);
        Assert.Equal(new[] { "2/3편 · 오늘은 어디로 갈까?", "꽃잎에 살짝 손을 대요", "내일 또 오자" + outroTail + PlutoSeriesBatchPlanner.ContinueSuffix }, Texts(PlutoSeriesSubtitleOverlay.Compose(state, d, Auto, 2, 3)));
        // ✏️ 아웃트로 꼬리를 지움 → 아웃트로 고정
        state = PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, 2, "내일 또 오자!" + PlutoSeriesBatchPlanner.ContinueSuffix);
        Assert.True(state.OutroPinnedByUser);
        Assert.Equal("내일 또 오자!", state.BaseCues[2].Text);
        Assert.Null(PlutoSeriesSubtitleOverlay.EffectiveDirective(state, d, Auto));                         // 덧입힐 자리가 남지 않음
        Assert.Equal(4, state.ManualEditCount);

        // 변화 없는 편집 · 범위 밖 → 같은 인스턴스
        var shownNow = PlutoSeriesSubtitleOverlay.Compose(state, d, Auto, 2, 3);
        Assert.Same(state, PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, 1, shownNow[1].Text));
        Assert.Same(state, PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, 5, "x"));
        Assert.Same(state, PlutoSeriesSubtitleOverlay.ApplyEdit(state, d, Auto, 2, 3, -1, "x"));

        // 🆕 새 시즌 분리 — 새 아크 지시 None → 덧입힘만 사라지고 수동 편집 원문 그대로(재생성 없음)
        var fresh = PlutoSubtitleOverlayState.FromBase(Base);
        fresh = PlutoSeriesSubtitleOverlay.ApplyEdit(fresh, d, Auto, 2, 3, 1, "꽃잎에 살짝 손을 대요");
        var newSeason = PlutoSeriesSubtitleOverlay.Compose(fresh, PlutoSeriesContinuityDirective.None, Auto, 2, 3);
        Assert.Equal(new[] { "2/3편 · 우리 아기 첫 봄나들이", "꽃잎에 살짝 손을 대요", "다시 또 오자" + PlutoSeriesBatchPlanner.ContinueSuffix }, Texts(newSeason));
        Assert.Equal(newSeason.Select(c => c.Text), PlutoSeriesSubtitleOverlay.Compose(fresh, null, Rec, 2, 3).Select(c => c.Text));
        // 다른 편 지시(NextEpisodeNumber 불일치)는 페이지가 걸러 null 로 넘긴다 · 장식 없는 단편(episode 0)
        Assert.Equal(Texts(fresh.BaseCues), Texts(PlutoSeriesSubtitleOverlay.Compose(fresh, null, Auto, 0, 0)));

        // 장식 제거 · 완결편 접미
        Assert.Equal("본문", PlutoSeriesSubtitleOverlay.StripDecoration("3/3편 · 본문" + PlutoSeriesBatchPlanner.FinaleSuffix, 0, 1, 3, 3));
        Assert.Equal("2/3편 · 본문", PlutoSeriesSubtitleOverlay.StripDecoration("2/3편 · 본문", 1, 3, 2, 3));   // 중간 줄은 그대로
        Assert.Equal("본문", PlutoSeriesSubtitleOverlay.StripDecoration("본문", 0, 3, 0, 0));

        // 단일 큐 · 빈 원문 경계
        var one = PlutoSubtitleOverlayState.FromBase(new[] { new SubtitleCue(0, 15, "한 줄") });
        Assert.Equal(new[] { "2/3편 · " + hook + outroTail + PlutoSeriesBatchPlanner.ContinueSuffix }, Texts(PlutoSeriesSubtitleOverlay.Compose(one, d, Auto, 2, 3)));
        Assert.Same(one, PlutoSeriesSubtitleOverlay.ApplyEdit(one, d, Auto, 2, 3, 0, "2/3편 · " + hook + outroTail + PlutoSeriesBatchPlanner.ContinueSuffix));
        var blank = PlutoSubtitleOverlayState.FromBase(new[] { new SubtitleCue(0, 5, "오프닝"), new SubtitleCue(5, 15, " ") });
        var blankShown = PlutoSeriesSubtitleOverlay.Compose(blank, d with { RecommendedHookLine = "" }, Auto, 0, 0);
        Assert.Equal(d.CliffhangerOutroLine, blankShown[1].Text);
        Assert.Same(blank, PlutoSeriesSubtitleOverlay.ApplyEdit(blank, d with { RecommendedHookLine = "" }, Auto, 0, 0, 1, d.CliffhangerOutroLine));

        // Workspace 결선 — 전환 · 시즌 분리는 재합성만(편 재조립 · 자막 재생성 없음) · 편집기 · 편집은 원문 레이어로
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var toggle = Body(ws, "private async Task ToggleSeriesEscalationModeAsync()");
        Assert.Contains("RecomposeSeriesCues();", toggle, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadSeriesEpisodeAsync", toggle, StringComparison.Ordinal);
        var season = Body(ws, "private async Task StartNewSeriesSeasonAsync()");
        Assert.DoesNotContain("LoadSeriesEpisodeAsync", season, StringComparison.Ordinal);
        Assert.Contains("RecomposeSeriesCues();", season, StringComparison.Ordinal);
        Assert.Contains("await RefreshMontagePreviewKeepingCuesAsync();", season, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "[✏️ 자막 편집]"));
        Assert.Equal(1, CountOf(ws, "data-cue-edit=\"@(cueIdx.ToString(CultureInfo.InvariantCulture))\""));
        Assert.Equal(1, CountOf(ws, "_seriesCueState = PromptFlowStudio.Services.Pluto.PlutoSeriesSubtitleOverlay.ApplyEdit(st, ActiveSeriesDirective(), SeriesEscalationMode, _seriesActiveEpisode, s.Episodes.Count, index, text);"));
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(ws.IndexOf("data-cue-editor-toggle=", StringComparison.Ordinal) > tools);
        Assert.DoesNotContain("자막 편집", ws.Substring(0, ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    // ── ④ 자율 추가 ① — ⚡ 서비스 적재 '이전' 첫 해석 쓰기 반환(기동 경합) · 검증된 원자 시드 ──

    [Fact]
    public async Task SelfInitiated_FirstResolveWriteBack_RunsBeforeServiceLoad_SoOfflineLearningIsNotOverwrittenOnStartup()
    {
        var (drive, local) = Dirs("race");
        // 어제 오프라인 세션: 로컬 폴백에 최신 학습(XP 보너스 42) · Drive 에는 그 이전 상태(XP 5)
        var offline = new PlutoLearningService(Path.Combine(local, PlutoLearningService.FileName));
        await offline.AddBonusXpAsync("xp:offline", 42);
        offline.Dispose();
        File.SetLastWriteTimeUtc(Path.Combine(local, PlutoLearningService.FileName), Stamp.AddDays(1));
        var stale = new PlutoLearningService(Path.Combine(drive, PlutoLearningService.FileName));
        await stale.AddBonusXpAsync("xp:old", 5);
        stale.Dispose();
        File.SetLastWriteTimeUtc(Path.Combine(drive, PlutoLearningService.FileName), Stamp);

        // 오늘 온라인 기동 — 백그라운드 기동 조정을 기다리지 않고 서비스가 먼저 적재되어도 최신 학습을 본다
        var storage = new PlutoDriveStorageManager(drive, local);
        using (var learning = new PlutoLearningService(storage))
        {
            await learning.EnsureLoadedAsync();
            Assert.Equal(42, learning.CurrentXP);
            Assert.Equal(PlutoLearningService.FileName, Path.GetFileName(learning.StoragePath));
            Assert.StartsWith(drive, learning.StoragePath, StringComparison.OrdinalIgnoreCase);
            await learning.AddBonusXpAsync("xp:today", 3);                                              // 다음 기록도 오프라인 학습 위에 쌓인다
        }
        using (var reread = new PlutoLearningService(Path.Combine(drive, PlutoLearningService.FileName)))
        {
            await reread.EnsureLoadedAsync();
            Assert.Equal(45, reread.CurrentXP);
        }

        // 같은 온라인 구간에서는 파일마다 한 번만 검사(이후 해석은 기존처럼 경로만)
        Put(Path.Combine(local, PlutoLearningService.FileName), "{\"late\":{\"BonusXp\":1}}", DateTime.UtcNow.AddDays(1));
        _ = storage.ResolveDataPath(PlutoLearningService.FileName);
        Assert.DoesNotContain("late", File.ReadAllText(Path.Combine(drive, PlutoLearningService.FileName)), StringComparison.Ordinal);

        // 🛡️ Drive 부재 시드 — 손상/0 B 로컬 사본은 Drive 로 퍼뜨리지 않는다(이전: 무검증 복사)
        Put(Path.Combine(local, "corrupt.json"), "{\"half", Stamp);
        Put(Path.Combine(local, "empty.json"), "", Stamp);
        Put(Path.Combine(local, "good.json"), "{\"ok\":true}", Stamp);
        _ = storage.ResolveDataPath("corrupt.json");
        _ = storage.ResolveDataPath("empty.json");
        _ = storage.ResolveDataPath("good.json");
        Assert.False(File.Exists(Path.Combine(drive, "corrupt.json")));
        Assert.False(File.Exists(Path.Combine(drive, "empty.json")));
        Assert.Equal("{\"ok\":true}", File.ReadAllText(Path.Combine(drive, "good.json")));
        Assert.Empty(Directory.EnumerateFiles(drive, "*.tmp"));
    }

    // ── ⑤ 자율 추가 ② — ⚔️ 분기(오프라인 로컬 변경 + 다른 PC 의 더 새로운 Drive) 충돌 사본 보관 ──

    [Fact]
    public void SelfInitiated_DivergedOfflineEdits_ArePreservedAsConflictCopies_BeforeNewerDriveRefreshesMirror()
    {
        var (drive, local) = Dirs("conflict");
        const string f = PlutoSeriesArcMemoryService.FileName;
        string D(string n) => Path.Combine(drive, n);
        string L(string n) => Path.Combine(local, n);

        Assert.False(PlutoDriveStorageManager.IsDiverged(null, Stamp.AddDays(9)));                          // 기록 없음 → 판정 불가(구버전 호환)
        Assert.False(PlutoDriveStorageManager.IsDiverged(Stamp, Stamp.AddSeconds(1)));
        Assert.True(PlutoDriveStorageManager.IsDiverged(Stamp, Stamp.AddHours(1)));

        // 1) 새 PC 복원 → 동기화 장부에 기록
        Put(D(f), "{\"v\":\"sync-1\"}", Stamp);
        var storage = new PlutoDriveStorageManager(drive, local);
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal(Stamp, storage.GetLastSyncedUtc(f));

        // 2) 이 PC 오프라인 편집(T+1h) · 그사이 다른 PC 가 Drive 를 갱신(T+2h) → 분기
        Put(L(f), "{\"v\":\"offline-edit\"}", Stamp.AddHours(1));
        Put(D(f), "{\"v\":\"other-pc\"}", Stamp.AddHours(2));
        Assert.Equal(0, storage.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles));          // 더 새로운 Drive 는 보호
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"v\":\"other-pc\"}", File.ReadAllText(L(f)));
        var conflicts = Path.Combine(local, PlutoDriveStorageManager.ConflictFolderName);
        var copy = Assert.Single(Directory.GetFiles(conflicts));
        Assert.Equal("{\"v\":\"offline-edit\"}", File.ReadAllText(copy));                                 // 오프라인 학습분은 버려지지 않는다
        Assert.Equal("pluto_series_arc.conflict-20261101010000.json", Path.GetFileName(copy));
        Assert.Equal(1, storage.ConflictCopyCount);
        Assert.Equal(Stamp.AddHours(2), storage.GetLastSyncedUtc(f));

        // 3) 분기 없는 단순 갱신(로컬 미변경) → 충돌 사본 없음
        Put(D(f), "{\"v\":\"other-pc-2\"}", Stamp.AddHours(3));
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Single(Directory.GetFiles(conflicts));

        // 4) 분기가 반복돼도 파일당 최근 3개만 유지
        for (var i = 0; i < 4; i++)
        {
            var t = Stamp.AddDays(1 + i);
            Put(L(f), "{\"v\":\"offline-" + i + "\"}", t);
            Put(D(f), "{\"v\":\"remote-" + i + "\"}", t.AddHours(1));
            Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        }
        var kept = Directory.GetFiles(conflicts).Select(n => Path.GetFileName(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(PlutoDriveStorageManager.MaxConflictCopiesPerFile, kept.Length);
        Assert.Equal("{\"v\":\"offline-3\"}", File.ReadAllText(Path.Combine(conflicts, kept[^1]!)));
        Assert.Equal(5, storage.ConflictCopyCount);

        // 5) 장부 없는 구버전 설치 — 사본을 만들지 않고 기존 Step 17 동작 그대로
        var (drive2, local2) = Dirs("conflict_legacy");
        Put(Path.Combine(local2, f), "{\"v\":\"old\"}", Stamp);
        Put(Path.Combine(drive2, f), "{\"v\":\"new\"}", Stamp.AddDays(1));
        Assert.Equal(1, new PlutoDriveStorageManager(drive2, local2).RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.False(Directory.Exists(Path.Combine(local2, PlutoDriveStorageManager.ConflictFolderName)));
    }

    // ── ⑥ 자율 추가 ③ — ✏️ 자막 편집 무결성(이중 언어 · 재타이밍 · 진행 중 생성 · A/B 장식) ──

    [Fact]
    public void SelfInitiated_SubtitleEditIntegrity_ClearsStaleTranslation_RetimesEdits_AndGuardsAgainstLateGeneration()
    {
        // 🌐 본문을 손으로 바꾸면 옛 영문 줄은 더 이상 번역이 아니다 → 비움(바꾸지 않은 줄의 영문은 유지)
        var state = PlutoSeriesSubtitleOverlay.ApplyEdit(PlutoSubtitleOverlayState.FromBase(Base), null, PlutoSeriesEscalationMode.AutoRewrite, 1, 3, 1, "직접 쓴 문장");
        Assert.Null(state.BaseCues[1].SecondaryText);
        Assert.Equal("Our baby's first spring outing", state.BaseCues[0].SecondaryText);
        var plain = PlutoSeriesSubtitleOverlay.EditPlain(Base, 0, "단편 수정");
        Assert.Equal(("단편 수정", (string?)null), (plain[0].Text, plain[0].SecondaryText));
        Assert.Same(Base, PlutoSeriesSubtitleOverlay.EditPlain(Base, 0, Base[0].Text));
        Assert.Same(Base, PlutoSeriesSubtitleOverlay.EditPlain(Base, 9, "x"));

        // ⏱️ 앵커 BGM 교체로 비트 · 컷 경계가 바뀌어도 수동 편집 문구는 새 구간으로 이동(고정 상태 · 편집 횟수 유지)
        state = state with { HookPinnedByUser = true };
        var retimed = new[] { SubtitleCue.Precise(0.0, 5.5, "템플릿 1"), SubtitleCue.Precise(5.5, 10.2, "템플릿 2"), SubtitleCue.Precise(10.2, 15.0, "템플릿 3") };
        var moved = PlutoSeriesSubtitleOverlay.RetimeOnto(state, retimed);
        Assert.NotNull(moved);
        Assert.Equal(Texts(state.BaseCues), Texts(moved!.BaseCues));
        Assert.Equal(retimed.Select(c => (c.EffectiveStartSec, c.EffectiveEndSec)), moved.BaseCues.Select(c => (c.EffectiveStartSec, c.EffectiveEndSec)));
        Assert.Equal((true, state.ManualEditCount), (moved.HookPinnedByUser, moved.ManualEditCount));
        Assert.Null(PlutoSeriesSubtitleOverlay.RetimeOnto(state, retimed.Take(2).ToArray()));            // 큐 수가 다르면 대응 불가 → 새 결과 채택

        // Workspace — 수동 편집은 진행 중 AI 3막 생성 결과를 무효화(늦게 도착한 생성이 편집을 덮지 않음) ·
        //              A/B 변형 적용도 시리즈 장식/레이어를 거침(이전: “n/N편” 장식이 사라짐) · 편 전환 시 레이어 초기화
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var edit = Body(ws, "private void EditMontageCue(int index, string? text)");
        Assert.Contains("_montageStorySeq++;", edit, StringComparison.Ordinal);
        Assert.True(edit.IndexOf("_montageStorySeq++;", StringComparison.Ordinal) < edit.IndexOf("ApplyEdit(", StringComparison.Ordinal));
        var story = Body(ws, "private async Task GenerateMontageStoryAsync()");
        Assert.Contains("seq != _montageStorySeq", story, StringComparison.Ordinal);                     // 생성 결과는 세대가 같을 때만 적용
        Assert.Contains("if (v.Subtitles.Count > 0) { _montageCues = v.Subtitles; ApplySeriesCueDecoration(); }", ws, StringComparison.Ordinal);
        var load = Body(ws, "private async Task LoadSeriesEpisodeAsync(PromptFlowStudio.Services.Pluto.PlutoSeriesEpisodePlan ep)");
        Assert.Contains("_seriesCueState = null;", load, StringComparison.Ordinal);
        var deco = Body(ws, "private void ApplySeriesCueDecoration()");
        Assert.Contains("ReferenceEquals(cues, _seriesComposedCues)) { RecomposeSeriesCues(); return; }", deco, StringComparison.Ordinal);   // 이중 장식 방지
    }

    /// <summary>메서드 시그니처부터 다음 'private ' 멤버 직전까지(결선 단언용).</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
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
