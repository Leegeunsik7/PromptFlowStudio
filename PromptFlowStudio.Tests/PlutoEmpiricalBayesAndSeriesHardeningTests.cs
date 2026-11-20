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
/// 🧬📺☁️ Step 17 — Drive 핵심 허브 9종(시리즈 아크 편입) · 무지연 앵커 write-through · 시즌 분리 · 단일 편 기준선 이탈(3편 시리즈 클리프행어) ·
/// 추천만 표시 모드 · 계층적 경험적 베이즈 콜드스타트 전이 · 하이브리드 톰슨–UCB ·
/// 자율 추가(이중 언어 자막 어긋남 방지 · 얼어붙은 로컬 미러 갱신 · 적재 전 write-through 내구성).
/// </summary>
public sealed class PlutoEmpiricalBayesAndSeriesHardeningTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private const string Golden = PlutoColorGradingCompiler.GoldenHourWarmName, Vintage = PlutoColorGradingCompiler.VintageFilm16mmName;
    private const string Baby = "BabyStudio";
    private const string Ep1Mode = "시리즈 1/3편", Ep2Mode = "시리즈 2/3편";
    private const string GoodHook = "검증된 전역 승자 훅", BaseHook = "평범한 기준 훅";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step17_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;

    public PlutoEmpiricalBayesAndSeriesHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };
    private PlutoRenderCatalogService Catalog(string name = "catalog.json") => new(Path.Combine(_dir, name));
    private PlutoSeriesArcMemoryService Arc(IPlutoRenderCatalogService? catalog, string name = "arc.json")
        => new(Path.Combine(_dir, name), catalog) { UtcNowProvider = () => new DateTimeOffset(_now) };

    private static PlutoRenderedClipRecord Clip(string id, string title, string mode, string look, string bgm, DateTime renderedUtc, string hook = "편 훅")
        => new(id, title, mode, hook, look, bgm, Array.Empty<string>(), "", new DateTimeOffset(renderedUtc), false);

    private static PlutoVideoPerformanceTelemetry Tel(string id, double hookPct, double completionPct, string? mode, string look = Golden, string bgm = "Lullaby", string? domain = Baby)
        => new(id, "YouTube Shorts", "편 훅", look, bgm, Array.Empty<string>(), hookPct, completionPct, 4.0, DateTimeOffset.UtcNow) { Domain = domain, RenderMode = mode };

    private static PlutoSeriesEpisodeStat Ep(int n, double hook, double completion, double baseHook = 0, double baseCompletion = 0, double baseSamples = 0, bool measured = true)
        => new(n, "v" + n, "h" + n, Golden, "Lullaby", measured, hook, completion, 4) { BaselineHookPct = baseHook, BaselineCompletionPct = baseCompletion, BaselineSamples = baseSamples };

    // ── ① ☁️ Drive 핵심 허브 9종 — pluto_series_arc.json 검증 · 미러 · 새 PC 자동 복원 · OFF 격리 ──

    [Fact]
    public async Task DriveHub_NineCoreFiles_IncludeSeriesArc_RestoreOnNewPc_VerifyAll_AndHonorDriveOffIsolation()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(PlutoDriveHealthService.CorePlutoFileCount, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.Equal(9, PlutoDriveHealthService.PlutoFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("pluto_series_arc.json", PlutoSeriesArcMemoryService.FileName);
        Assert.Contains(PlutoSeriesArcMemoryService.FileName, PlutoDriveHealthService.PlutoFiles);
        foreach (var f in new[]
                 {
                     PlutoMetaCalibrationService.FileName, PlutoRenderCatalogService.FileName, PlutoExperimentLedgerService.FileName, PlutoMistakeNotebookService.FileName,
                     PlutoLearningService.FileName, PlutoAutonomousTrendHarvester.PatternsFileName, PlutoSocialRadarEngine.FileName, PlutoIdentityVault.FileName,
                 })
            Assert.Contains(f, PlutoDriveHealthService.PlutoFiles);                                    // 기존 8종 그대로

        var drive = Path.Combine(_dir, "vault", "Pluto");
        Directory.CreateDirectory(drive);
        foreach (var f in PlutoDriveHealthService.PlutoFiles.Where(f => f != PlutoSeriesArcMemoryService.FileName))
            File.WriteAllText(Path.Combine(drive, f), "{\"origin\":\"drive\"}");

        // 아크 서비스가 저장소 관리자를 통해 실제로 Drive 볼트에 기록(write-through → 백그라운드 플러시)
        var writerLocal = Path.Combine(_dir, "local_writer");
        var writerStore = new PlutoDriveStorageManager(drive, writerLocal);
        Assert.True(writerStore.IsCloudSynced);
        using (var arc = new PlutoSeriesArcMemoryService(writerStore) { UtcNowProvider = () => new DateTimeOffset(_now) })
        {
            Assert.True(arc.RecordRenderedEpisode(Clip("hub_ep1", "허브 검증 1/3편 — 시작", Ep1Mode, Golden, "Lullaby", T0)));
            await arc.FlushAsync();
        }
        var driveArc = Path.Combine(drive, PlutoSeriesArcMemoryService.FileName);
        Assert.True(File.Exists(driveArc));
        Assert.Contains("hub_ep1", File.ReadAllText(driveArc), StringComparison.Ordinal);

        // 🔁 새 PC(로컬 비어 있음) → 9종 전부 복원(아크 포함) · 멱등
        var freshLocal = Path.Combine(_dir, "local_fresh");
        var fresh = new PlutoDriveStorageManager(drive, freshLocal);
        Assert.Equal(9, fresh.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal(File.ReadAllText(driveArc), File.ReadAllText(Path.Combine(freshLocal, PlutoSeriesArcMemoryService.FileName)));
        Assert.Equal(0, fresh.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));

        // 🛡️ 로컬에 더 새로운 아크가 있으면 절대 덮어쓰지 않음(나머지 8종만 복원)
        var keptLocal = Path.Combine(_dir, "local_kept");
        Directory.CreateDirectory(keptLocal);
        var keptArc = Path.Combine(keptLocal, PlutoSeriesArcMemoryService.FileName);
        File.WriteAllText(keptArc, "{\"origin\":\"local-newer\"}");
        File.SetLastWriteTimeUtc(keptArc, File.GetLastWriteTimeUtc(driveArc).AddHours(1));
        var kept = new PlutoDriveStorageManager(drive, keptLocal);
        Assert.Equal(8, kept.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"origin\":\"local-newer\"}", File.ReadAllText(keptArc));

        // 🔄 [지금 전체 드라이브 백업 검증] — Drive 에서 사라진 아크를 로컬 사본으로 재시드 + 9종 전부 Healthy(+ 계정·설정 2종 = 11항목)
        File.Delete(driveArc);
        var verified = await new PlutoDriveHealthService(null, null, kept).ForceSyncAndVerifyAsync();
        var plutoItems = verified.Items.Where(i => i.Category == PlutoDriveHealthService.CategoryPluto).ToList();
        Assert.Equal(9, plutoItems.Count);
        Assert.All(plutoItems, i => Assert.Equal(PlutoDriveFileStatus.Healthy, i.Status));
        Assert.Equal(PlutoDriveHealthService.PlutoFiles.Length + PlutoDriveHealthService.StudioFiles.Length, verified.TotalCount);
        Assert.Equal("{\"origin\":\"local-newer\"}", File.ReadAllText(driveArc));

        // 🔒 PROMPTFLOW_PLUTO_DRIVE=off(테스트 하네스 기본) → 실 Drive 비활성 · 복원 0 · 9종을 '비활성'으로만 보고 · 로컬에 아무것도 만들지 않음
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        Assert.Null(PlutoDriveStorageManager.DefaultPrimaryRoot());
        var offLocal = Path.Combine(_dir, "local_off");
        var off = new PlutoDriveStorageManager(PlutoDriveStorageManager.DefaultPrimaryRoot(), offLocal);
        Assert.False(off.IsCloudSynced);
        Assert.Equal(0, off.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        var offReport = await new PlutoDriveHealthService(null, null, off).ForceSyncAndVerifyAsync();
        var offPluto = offReport.Items.Where(i => i.Category == PlutoDriveHealthService.CategoryPluto).ToList();
        Assert.Equal(9, offPluto.Count);
        Assert.All(offPluto, i => Assert.Equal(PlutoDriveFileStatus.DriveUnavailable, i.Status));
        Assert.Contains(offPluto, i => i.FileName == PlutoSeriesArcMemoryService.FileName);
        Assert.False(Directory.Exists(offLocal) && Directory.EnumerateFiles(offLocal).Any());

        // 결선 — MainForm 기동 복원이 9종 목록을 그대로 사용 · 설정 화면 문구
        var mainForm = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "MainForm.cs"));
        Assert.Contains("plutoStore.RestoreCoreHubsToLocal(PromptFlowStudio.Services.Pluto.PlutoDriveHealthService.PlutoFiles)", mainForm, StringComparison.Ordinal);
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        Assert.Contains("Pluto 핵심 허브 9종", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("허브 8종", settings, StringComparison.Ordinal);
    }

    // ── ② ⚡ 무지연 앵커 write-through(연속 렌더) · 🆕 시즌 경계 격리 ──

    [Fact]
    public async Task SeriesArc_WriteThrough_GivesEpisode2TheAnchorWithZeroLag_AndSeasonBoundaryStartsFreshArcForSameTheme()
    {
        var catalog = Catalog();
        using var arc = Arc(catalog);

        // 연속 렌더: 1편 렌더 완료 직후(카탈로그 기록 · 디스크 플러시 이전)에 2편을 불러온다
        var ep1 = Clip("s1_ep1", "봄 산책 1/3편 — 시작", Ep1Mode, Golden, "Lullaby", T0);
        Assert.False(arc.GetContinuityDirective("봄 산책", 3, 2).HasGuidance);
        Assert.True(arc.RecordRenderedEpisode(ep1));                                                   // 동기 · 적재 이전에도 동작
        var instant = arc.GetContinuityDirective("봄 산책", 3, 2);
        Assert.Equal((Golden, "Lullaby", false), (instant.AnchorColorLookId, instant.AnchorBgmMood, instant.EscalateHook));
        var viaAsync = await arc.GetContinuityDirectiveAsync("봄 산책", 3, 2);                          // 카탈로그는 아직 비어 있음 → 메모리 write-through 가 근거
        Assert.Equal((Golden, "Lullaby"), (viaAsync.AnchorColorLookId, viaAsync.AnchorBgmMood));
        Assert.Empty(await catalog.GetRecentClipsAsync());
        Assert.False(arc.RecordRenderedEpisode(ep1));                                                  // 멱등
        Assert.False(arc.RecordRenderedEpisode(Clip("single", "단편 영상", "몽타주", Vintage, "MusicBox", T0)));   // 시리즈 편이 아님

        // 늦게 도착한 더 오래된 카탈로그 항목은 새 렌더를 되돌리지 못한다 · 같은 영상의 카탈로그 기록은 무변경
        await catalog.RecordRenderedClipAsync(Clip("s1_ep1_old", "봄 산책 1/3편 — 시작", Ep1Mode, Vintage, "MusicBox", T0.AddHours(-1)));
        Assert.Equal(0, await arc.SyncFromCatalogAsync());
        Assert.Equal(Golden, arc.GetContinuityDirective("봄 산책", 3, 2).AnchorColorLookId);
        await catalog.RecordRenderedClipAsync(ep1);
        Assert.Equal(0, await arc.SyncFromCatalogAsync());

        // 백그라운드 플러시 → 재시작해도 앵커 유지
        await arc.FlushAsync();
        using (var reloaded = Arc(catalog))
        {
            await reloaded.EnsureLoadedAsync();
            Assert.Equal((Golden, "Lullaby"), (reloaded.GetContinuityDirective("봄 산책", 3, 2).AnchorColorLookId, reloaded.GetContinuityDirective("봄 산책", 3, 2).AnchorBgmMood));
        }

        // 1시즌 성과(2편 이탈) 기록
        var ep2 = Clip("s1_ep2", "봄 산책 2/3편 — 전개", Ep2Mode, Golden, "Lullaby", T0.AddMinutes(5));
        Assert.True(arc.RecordRenderedEpisode(ep2));
        await catalog.RecordRenderedClipAsync(ep2);
        Assert.True(await arc.RecordEpisodePerformanceAsync(Tel("s1_ep1", 60, 50, Ep1Mode)));
        Assert.True(await arc.RecordEpisodePerformanceAsync(Tel("s1_ep2", 45, 35, Ep2Mode)));
        Assert.True(arc.GetContinuityDirective("봄 산책", 3, 3).EscalateHook);
        var season1 = arc.GetSeasonInfo("봄 산책", 3);
        Assert.Equal((1, 2, 2, true), (season1.Season, season1.EpisodeCount, season1.MeasuredCount, season1.HasArc));
        Assert.Equal(PlutoSeriesArcMemoryService.SeriesKeyFor("봄 산책", 3), PlutoSeriesArcMemoryService.SeasonKeyFor("봄 산책", 3, 1));   // 1시즌 키 = Step 16 키(파일 호환)
        Assert.EndsWith(PlutoSeriesArcMemoryService.SeasonKeyMarker + "2", PlutoSeriesArcMemoryService.SeasonKeyFor("봄 산책", 3, 2), StringComparison.Ordinal);

        // 🆕 새 시즌 분리 — 같은 테마 · 같은 편수 → 깨끗한 아크(앵커 · 이탈 지시 없음) · 빈 시즌 위 연타는 멱등
        _now = T0.AddDays(1);
        var season2 = await arc.StartNewSeasonAsync("봄 산책", 3);
        Assert.Equal((2, 0, 0, false), (season2.Season, season2.EpisodeCount, season2.MeasuredCount, season2.HasArc));
        Assert.Equal(2, (await arc.StartNewSeasonAsync("봄 산책", 3)).Season);
        Assert.Same(PlutoSeriesContinuityDirective.None, arc.GetContinuityDirective("봄 산책", 3, 2));
        Assert.Same(PlutoSeriesContinuityDirective.None, arc.GetContinuityDirective("봄 산책", 3, 3));
        Assert.Equal(0, await arc.SyncFromCatalogAsync());                                             // 카탈로그에 남은 1시즌 렌더가 새 시즌으로 새지 않는다
        Assert.Equal(0, arc.GetSeasonInfo("봄 산책", 3).EpisodeCount);

        // 새 시즌 1편(다른 룩/BGM) → 새 시즌 2편은 새 앵커만 상속
        var n1 = Clip("s2_ep1", "봄 산책 1/3편 — 시작", Ep1Mode, Vintage, "MusicBox", T0.AddDays(1).AddMinutes(1));
        Assert.True(arc.RecordRenderedEpisode(n1));
        var fresh = arc.GetContinuityDirective("봄 산책", 3, 2);
        Assert.Equal((Vintage, "MusicBox", false), (fresh.AnchorColorLookId, fresh.AnchorBgmMood, fresh.EscalateHook));

        // 이전 시즌 텔레메트리 보존 · 늦게 들어온 1시즌 영상 성과는 1시즌으로, 새 시즌 영상 성과는 2시즌으로 귀속
        await catalog.RecordRenderedClipAsync(n1);
        Assert.True(await arc.RecordEpisodePerformanceAsync(Tel("s1_ep1", 62, 52, Ep1Mode)));
        Assert.True(await arc.RecordEpisodePerformanceAsync(Tel("s2_ep1", 70, 60, Ep1Mode, Vintage, "MusicBox")));
        var arcs = arc.GetSeriesArcs();
        Assert.Equal(2, arcs.Count);
        var old = Assert.Single(arcs, a => a.Season == 1);
        var cur = Assert.Single(arcs, a => a.Season == 2);
        Assert.Equal((2, 2, true), (old.Episodes.Count, old.MeasuredCount, old.HasDropOff));
        Assert.Equal(62.0, old.Episodes.Single(e => e.EpisodeNumber == 1).HookRetentionPct);
        Assert.Equal((1, 1, false), (cur.Episodes.Count, cur.MeasuredCount, cur.HasDropOff));
        Assert.Equal("s2_ep1", cur.Episodes[0].VideoId);
        Assert.EndsWith(PlutoSeriesArcMemoryService.SeasonKeyMarker + "2", cur.SeriesKey, StringComparison.Ordinal);
        Assert.Contains("🆕 시즌 2", PlutoSeriesArcMemoryService.BuildArcLabel(new PlutoSeriesArcStatus(2, 3, 1, cur)), StringComparison.Ordinal);
        Assert.DoesNotContain("시즌", PlutoSeriesArcMemoryService.BuildArcLabel(new PlutoSeriesArcStatus(2, 3, 1, old)), StringComparison.Ordinal);

        // 영속 왕복 — 시즌 경계가 재시작 후에도 유지
        await arc.FlushAsync();
        using var again = Arc(catalog);
        await again.EnsureLoadedAsync();
        Assert.Equal(2, again.GetSeasonInfo("봄 산책", 3).Season);
        Assert.Equal(Vintage, again.GetContinuityDirective("봄 산책", 3, 2).AnchorColorLookId);
        Assert.Equal(2, again.GetSeriesArcs().Single(a => a.Season == 1).MeasuredCount);

        // Workspace 결선 — 렌더 직후 동기 write-through(카탈로그 비동기 기록보다 먼저) · [🆕 새 시즌 분리]
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var writeThrough = ws.IndexOf("arcMem.RecordRenderedEpisode(record)", StringComparison.Ordinal);
        Assert.True(writeThrough > 0 && writeThrough < ws.IndexOf("_ = RecordCatalogClipAsync(record);", StringComparison.Ordinal));
        Assert.Equal(1, CountOf(ws, "[🆕 새 시즌 분리]"));
        Assert.Equal(1, CountOf(ws, "data-series-new-season=\"1\""));
        Assert.Equal(1, CountOf(ws, "await arc.StartNewSeasonAsync(s.SeriesTheme, s.Episodes.Count, _mediaCts.Token)"));
        Assert.Contains("@if (ssn.HasArc) { <button class=\"ws-back\" data-series-new-season=\"1\"", ws, StringComparison.Ordinal);   // 아크 기록이 있을 때만 노출
    }

    // ── ③ 📐 1편 기준선 미달 → 3편 시리즈의 2편에 궁금증 훅 + 클리프행어 · 추천만 표시 모드 ──

    [Fact]
    public async Task Episode1BelowContextBaseline_EscalatesEpisode2OfThreeWithCliffhanger_AndRecommendOnlyPreservesSubtitles()
    {
        // 순수 판정 — 기준선 대비 −5p 이상 미달일 때만 · 경계 포함 · 편 간 비교가 가능해지면 기존 규칙이 우선
        Assert.False(PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 50, 40) }).HasDropOff);                        // 기준선 없음
        Assert.False(PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 56, 46, 60, 50, 3) }).HasDropOff);            // −4p
        var edge = PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 55, 45, 60, 50, 3) });                          // −5p
        Assert.Equal((true, true, 1, 1, -5.0), (edge.HasDropOff, edge.IsBaselineDropOff, edge.EscalationLevel, edge.BaselineEpisode, edge.BaselineGapPts));
        Assert.Equal(3, PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 40, 30, 60, 50, 3) }).EscalationLevel);    // −20p
        Assert.Equal(PlutoSeriesDropAxis.Completion, PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 60, 30, 60, 50, 3) }).DropAxis);
        var twoMeasured = PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 40, 30, 60, 50, 3), Ep(2, 41, 31, 60, 50, 3) });
        Assert.Equal((false, false), (twoMeasured.HasDropOff, twoMeasured.IsBaselineDropOff));                                    // 2편부터는 편 간 변화(+1p)로 판정
        Assert.False(PlutoSeriesArcMemoryService.TryEffectiveBaseline(2, 60, 50, 2, 60, 50, out _, out _, out _));                // 컨텍스트 · 전역 모두 미성립
        Assert.True(PlutoSeriesArcMemoryService.TryEffectiveBaseline(3, 60, 50, 9, 40, 30, out var eh, out var ec, out var en));  // 성립 컨텍스트 = 자기 기준선
        Assert.Equal((60.0, 50.0, 3.0), (eh, ec, en));
        Assert.True(PlutoSeriesArcMemoryService.TryEffectiveBaseline(1, 60, 50, 4, 40, 30, out var ph, out var pc, out _));       // 미성립 → 전역으로 부분 풀링
        Assert.Equal((1 * 60 + 2 * 40) / 3.0, ph, 9);
        Assert.Equal((1 * 50 + 2 * 30) / 3.0, pc, 9);

        // 서비스 — 같은 (BabyStudio, 시리즈 1/3편) 컨텍스트의 앞선 1편 3건이 기준선(60 · 50)을 만든다
        using var arc = Arc(null);
        foreach (var theme in new[] { "가을 소풍", "겨울 캠핑", "여름 바다" })
            Assert.True(await arc.RecordEpisodePerformanceAsync(Tel(theme + " 1/3편", 60, 50, Ep1Mode)));
        Assert.Equal(0, arc.GetStatus().DropOffSeriesCount);                                           // 기준선을 만든 편들 자신은 이탈 아님
        Assert.False(arc.GetContinuityDirective("여름 바다", 3, 2).EscalateHook);                       // 앵커만 유지

        // 새 3편 시리즈의 1편이 기준선을 15p 밑돈다 → 2편 = 궁금증 훅 + (3편으로 잇는) 클리프행어 아웃트로
        Assert.True(await arc.RecordEpisodePerformanceAsync(Tel("봄 산책 1/3편", 45, 35, Ep1Mode)));
        var report = arc.GetSeriesArcs(8).Single(a => a.SeriesTitle == "봄 산책");
        Assert.Equal((1, true, true, 2, -15.0, PlutoSeriesDropAxis.Both), (report.MeasuredCount, report.HasDropOff, report.IsBaselineDropOff, report.EscalationLevel, report.BaselineGapPts, report.DropAxis));
        Assert.Empty(report.EpisodeRetentionDeltas);
        var d2 = arc.GetContinuityDirective("봄 산책", 3, 2);
        Assert.True(d2.EscalateHook);
        Assert.Equal(2, d2.EscalationLevel);
        Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooks[1], d2.RecommendedHookLine);
        Assert.Equal(PlutoSeriesArcMemoryService.CliffhangerOutros[1], d2.CliffhangerOutroLine);        // 3편 시리즈의 2편이 실제로 클리프행어를 받는다
        Assert.Equal(-15.0, d2.EpisodeRetentionDelta);
        Assert.Contains("1편 유지 지수가 컨텍스트 기준선 대비 −15p", d2.ReasonKorean, StringComparison.Ordinal);
        Assert.Contains("궁금증 훅 + 클리프행어 Lv2", d2.ReasonKorean, StringComparison.Ordinal);
        Assert.Contains("📉 1편 기준선 대비 −15p → 훅 에스컬레이션 Lv2", PlutoSeriesArcMemoryService.BuildArcLabel(new PlutoSeriesArcStatus(1, 1, 1, report)), StringComparison.Ordinal);
        Assert.Equal(1, arc.GetStatus().DropOffSeriesCount);
        // 관측은 자기 기준선을 오염시키지 않는다(측정 시점 스냅샷 = 60 · 50) · 같은 영상 재측정은 기준선에 다시 들어가지 않는다
        var m1 = report.Episodes.Single();
        Assert.Equal((60.0, 50.0, 3.0), (m1.BaselineHookPct, m1.BaselineCompletionPct, m1.BaselineSamples));
        Assert.True(await arc.RecordEpisodePerformanceAsync(Tel("봄 산책 1/3편", 44, 34, Ep1Mode)));
        Assert.Equal((60.0, 50.0), (arc.GetSeriesArcs(8).Single(a => a.SeriesTitle == "봄 산책").Episodes.Single().BaselineHookPct, arc.GetSeriesArcs(8).Single(a => a.SeriesTitle == "봄 산책").Episodes.Single().BaselineCompletionPct));

        // 영속 왕복 — 기준선 스냅샷과 판정이 재시작 후에도 동일
        using (var re = Arc(null))
        {
            await re.EnsureLoadedAsync();
            Assert.Equal(2, re.GetContinuityDirective("봄 산책", 3, 2).EscalationLevel);
        }

        // 💬 자동 각색(기본) vs 추천만 표시 — 추천만: 자막 인스턴스 그대로 · 추천 문구는 라벨로 노출
        var cues = new[] { new SubtitleCue(0, 5, "오프닝"), new SubtitleCue(5, 10, "전개"), new SubtitleCue(10, 15, "엔딩") };
        Assert.Equal(PlutoSeriesEscalationMode.AutoRewrite, arc.EscalationMode);                        // 기본 = 기존 동작
        var auto = PlutoSeriesArcMemoryService.ApplyDirective(cues, d2, PlutoSeriesEscalationMode.AutoRewrite);
        Assert.Equal(new[] { d2.RecommendedHookLine, "전개", "엔딩" + PlutoSeriesArcMemoryService.OutroJoiner + d2.CliffhangerOutroLine }, auto.Select(c => c.Text).ToArray());
        var kept = PlutoSeriesArcMemoryService.ApplyDirective(cues, d2, PlutoSeriesEscalationMode.RecommendOnly);
        Assert.Same(cues, kept);
        Assert.Equal(new[] { "오프닝", "전개", "엔딩" }, kept.Select(c => c.Text).ToArray());
        var label = PlutoSeriesArcMemoryService.BuildRecommendationLabel(d2);
        Assert.StartsWith("💡 추천(자막 미변경): ", label, StringComparison.Ordinal);
        Assert.Contains(d2.RecommendedHookLine, label, StringComparison.Ordinal);
        Assert.Contains(d2.CliffhangerOutroLine, label, StringComparison.Ordinal);
        Assert.Equal(string.Empty, PlutoSeriesArcMemoryService.BuildRecommendationLabel(PlutoSeriesContinuityDirective.None));
        Assert.Equal(string.Empty, PlutoSeriesArcMemoryService.BuildRecommendationLabel(null));

        // 모드는 영속 · 두뇌 리포트 상태에 반영
        await arc.SetEscalationModeAsync(PlutoSeriesEscalationMode.RecommendOnly);
        Assert.Equal(PlutoSeriesEscalationMode.RecommendOnly, arc.GetStatus().EscalationMode);
        Assert.Contains("추천만 표시", PlutoSeriesArcMemoryService.BuildArcLabel(arc.GetStatus()), StringComparison.Ordinal);
        using (var re = Arc(null))
        {
            await re.EnsureLoadedAsync();
            Assert.Equal(PlutoSeriesEscalationMode.RecommendOnly, re.EscalationMode);
            await re.SetEscalationModeAsync(PlutoSeriesEscalationMode.AutoRewrite);
        }
        using (var re = Arc(null))
        {
            await re.EnsureLoadedAsync();
            Assert.Equal(PlutoSeriesEscalationMode.AutoRewrite, re.EscalationMode);
            Assert.DoesNotContain("추천만", PlutoSeriesArcMemoryService.BuildArcLabel(re.GetStatus()), StringComparison.Ordinal);
        }

        // Workspace 결선 — 기본 자동 각색 · 추천만 모드는 ApplyDirective 를 건너뛰고 추천 배지만 노출 · 토글은 도구 행 아래
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Equal(1, CountOf(ws, "private bool _seriesAutoRewrite = true;"));
        Assert.Equal(1, CountOf(ws, "SeriesEscalationMode => _seriesAutoRewrite ? PromptFlowStudio.Services.Pluto.PlutoSeriesEscalationMode.AutoRewrite : PromptFlowStudio.Services.Pluto.PlutoSeriesEscalationMode.RecommendOnly;"));   // Step 18 — 추천만 모드는 합성기에서 덧입힘 생략
        Assert.Equal(1, CountOf(ws, "data-series-escalation-mode="));
        Assert.Contains("\"[✍️ 자동 각색 적용]\" : \"[💡 추천만 표시]\"", ws, StringComparison.Ordinal);
        Assert.Contains("자동 각색 적용(Auto-Rewrite)", ws, StringComparison.Ordinal);
        Assert.Contains("추천만 표시(Recommend-Only)", ws, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "data-series-recommendation=\"1\""));
        Assert.Contains("@if (!_seriesAutoRewrite && _seriesDirective is { EscalateHook: true } rdir", ws, StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        foreach (var marker in new[] { "data-series-escalation-mode=", "data-series-new-season=", "data-series-recommendation=", "data-series-season=" })
            Assert.True(ws.IndexOf(marker, StringComparison.Ordinal) > tools);
    }

    // ── ④ 🧬 계층적 경험적 베이즈 콜드스타트 전이 · 🎲 하이브리드 톰슨–UCB ──

    /// <summary>성숙 컨텍스트 A(General · Montage)에서 GoodHook = 기준선 +10p · BaseHook = −10p 를 5회씩 관측.</summary>
    private static async Task SeedProvenGlobalAsync(PlutoLearningService learning, string domain, string mode, int rounds = 5)
    {
        for (var i = 0; i < rounds; i++)
        {
            await learning.RecordContextualObservationAsync(domain, mode, 0.70, new[] { "hook:" + GoodHook });
            await learning.RecordContextualObservationAsync(domain, mode, 0.50, new[] { "hook:" + BaseHook });
        }
    }

    [Fact]
    public async Task EmpiricalBayes_TransfersProvenGlobalPriorIntoColdStartContext_WithoutOverridingMatureLocalData()
    {
        // 순수 수축 — α = n / (n + k) · k = σ² / (τ₀² + σ²/n_g)
        const double tau2 = PlutoEmpiricalBayes.BetweenContextStdDev * PlutoEmpiricalBayes.BetweenContextStdDev;
        var k = 0.01 / (tau2 + 0.01 / 10.0);
        Assert.Equal(k, PlutoEmpiricalBayes.PriorStrength(10, 0.01), 12);
        var cold = PlutoEmpiricalBayes.Shrink(-0.20, 1, 0.10, 10, 0.01);
        Assert.True(cold.IsColdStart && cold.IsTransferActive && cold.IsGlobalProven);
        Assert.Equal(1.0 / (1.0 + k), cold.LocalWeight, 12);
        Assert.Equal(cold.LocalWeight * -0.20 + (1 - cold.LocalWeight) * 0.10, cold.ShrunkMean, 12);
        Assert.True(cold.ShrunkMean > 0);                                                              // 단발 지역 부진이 검증된 전역 승자를 뒤집지 못한다
        Assert.True(PlutoEmpiricalBayes.Shrink(-0.20, 2, 0.10, 10, 0.01).LocalWeight > cold.LocalWeight);   // 지역 시행 ↑ → 지역 비중 ↑
        var mature = PlutoEmpiricalBayes.Shrink(-0.20, 3, 0.10, 10, 0.01);
        Assert.Equal((false, 1.0, -0.20, false), (mature.IsColdStart, mature.LocalWeight, mature.ShrunkMean, mature.IsTransferActive));   // 성숙 → 지역 그대로
        var unproven = PlutoEmpiricalBayes.Shrink(0.30, 1, 0.50, 1, 0.01);                             // 전역 미검증 → 중립(0) 사전 · 강도 1
        Assert.Equal((PlutoEmpiricalBayes.NeutralPriorStrength, 0.5, 0.15, false), (unproven.PriorStrength, unproven.LocalWeight, unproven.ShrunkMean, unproven.IsTransferActive));
        Assert.Equal(PlutoEmpiricalBayes.MaxPriorStrength, PlutoEmpiricalBayes.PriorStrength(1e6, 0.5));
        Assert.Equal(1.0, PlutoEmpiricalBayes.ContextMultiplier(PlutoEmpiricalBayes.Shrink(0, 0, 0.10, 10, 0.01)));   // 지역 시행 0 → 전역 순위 그대로
        Assert.Equal(1.0, PlutoEmpiricalBayes.ContextMultiplier(PlutoEmpiricalBayesEstimate.None));
        Assert.Equal(PlutoEmpiricalBayes.MinContextMultiplier, PlutoEmpiricalBayes.ContextMultiplier(PlutoEmpiricalBayes.Shrink(-0.9, 5, 0.3, 10, 0.01)));
        Assert.False(PlutoEmpiricalBayes.Shrink(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN).HasEvidence);

        // 전역 사전 구축 — 컨텍스트 A 에서 GoodHook 은 검증된 승자(학습 배수 ×1.3)
        const string domA = "GeneralCinematic", modeA = "Montage", domB = "BabyStudio", modeB = "Series3";
        var learning = Learning();
        var ranker = new PlutoAdaptivePatternRanker(learning, null);
        Assert.Same(PlutoColdStartTransferStatus.Empty, ranker.GetColdStartTransferStatus());
        await SeedProvenGlobalAsync(learning, domA, modeA);
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + GoodHook }, 1.14, "xp:test", 0);
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + GoodHook }, 1.14, "xp:test", 0);
        var candidates = new[] { GoodHook, BaseHook };
        var goodGlobal = learning.GetPatternAlphaStats("hook:" + GoodHook);
        Assert.True(goodGlobal.Alpha > 0.05 && goodGlobal.Samples >= 5);
        Assert.True(learning.GetPatternAlphaStats("hook:" + BaseHook).Alpha < -0.05);
        Assert.Equal(GoodHook, ranker.RankOptions(PlutoPatternKind.Hook, candidates)[0].DisplayLabel);

        // ❄️ 콜드스타트 컨텍스트 B(관측 0) — 전역 사전 100% 이식(순위 = 전역 순위 · 배수 1.0)
        var untouched = ranker.RankForContext(PlutoPatternKind.Hook, domB, modeB, candidates);
        Assert.Equal(GoodHook, untouched[0].DisplayLabel);
        Assert.Equal((true, 0.0, 1.0, 1.0), (untouched[0].ContextTransfer.IsTransferActive, untouched[0].ContextTransfer.LocalTrials, untouched[0].ContextTransfer.GlobalWeight, untouched[0].ContextMultiplier));
        Assert.Equal(goodGlobal.Alpha, untouched[0].ContextTransfer.ShrunkMean, 12);

        // B 에서 GoodHook 이 단 1회 부진 — 수축 덕분에 여전히 1위(지역 단독 추정이었다면 뒤집힌다)
        await learning.RecordContextualObservationAsync(domB, modeB, 0.40, new[] { "hook:" + GoodHook });
        var local1 = learning.GetContextPatternAlpha(domB, modeB, "hook:" + GoodHook);
        Assert.True(local1.Alpha < -0.15);
        Assert.Equal(1.0, local1.Samples);
        var oneBad = ranker.RankForContext(PlutoPatternKind.Hook, domB, modeB, candidates);
        var good = oneBad.Single(o => o.DisplayLabel == GoodHook);
        var g = learning.GetPatternAlphaStats("hook:" + GoodHook);
        var expected = PlutoEmpiricalBayes.Shrink(local1.Alpha, local1.Samples, g.Alpha, g.Samples, g.Variance);
        Assert.Equal(expected, good.ContextTransfer);
        Assert.True(good.ContextTransfer.IsColdStart && good.ContextTransfer.IsTransferActive);
        Assert.Equal(1.0 / (1.0 + expected.PriorStrength), good.ContextTransfer.LocalWeight, 12);
        Assert.True(good.ContextTransfer.ShrunkMean > local1.Alpha + 0.10);                            // 전역 쪽으로 크게 수축
        Assert.InRange(good.ContextMultiplier, 0.80, 0.999);
        Assert.Equal(GoodHook, oneBad[0].DisplayLabel);
        var naive = Math.Clamp(1.0 + PlutoEmpiricalBayes.ContextAlphaGain * (local1.Alpha - g.Alpha), PlutoEmpiricalBayes.MinContextMultiplier, PlutoEmpiricalBayes.MaxContextMultiplier);
        var baseOption = oneBad.Single(o => o.DisplayLabel == BaseHook);
        Assert.True(good.FinalScore / good.ContextMultiplier * naive < baseOption.FinalScore);         // 수축 없는 지역 단독 추정 → 순위 역전(= 콜드스타트 오판)
        Assert.Equal(GoodHook, ranker.RankOptions(PlutoPatternKind.Hook, candidates)[0].DisplayLabel);  // 전역 API 는 불변
        Assert.Equal(1.0, ranker.RankOptions(PlutoPatternKind.Hook, candidates)[0].ContextMultiplier);
        // 컨텍스트 미지정 · 훅/룩 이외 종류 → 전역 순위 그대로
        Assert.All(ranker.RankForContext(PlutoPatternKind.Hook, null, null, candidates), o => Assert.Equal(1.0, o.ContextMultiplier));
        Assert.All(ranker.RankForContext(PlutoPatternKind.BgmMood, domB, modeB), o => Assert.Same(PlutoEmpiricalBayesEstimate.None, o.ContextTransfer));

        // 🧠 두뇌 리포트 — 콜드스타트 1/2 컨텍스트 · 전역 검증 사전 2개 이식 중
        var status = ranker.GetColdStartTransferStatus();
        Assert.Equal((2, 1, 2, domB + "·" + modeB, 1.0, true), (status.ContextCount, status.ColdStartContextCount, status.ProvenPriorCount, status.FocusContext, status.FocusContextSamples, status.IsTransferActive));
        Assert.InRange(status.FocusGlobalWeightPct, 50.0, 100.0);
        var summary = await new PlutoAutonomousBrainSummaryService(null, ranker, null).GetSummaryAsync();
        Assert.Equal(status, summary.ColdStartTransfer);
        Assert.StartsWith("콜드스타트 1/2개 컨텍스트 · 전역 검증 사전 2개 이식 중 · '" + domB + "·" + modeB + "' n=1 → 전역 가중 ", summary.ColdStartTransferLabel, StringComparison.Ordinal);
        var blank = await new PlutoAutonomousBrainSummaryService(null, new PlutoAdaptivePatternRanker(Learning("blank.json"), null), null).GetSummaryAsync();
        Assert.Same(PlutoColdStartTransferStatus.Empty, blank.ColdStartTransfer);
        Assert.StartsWith("기록 없음 · 새 (도메인·포맷) 컨텍스트는 검증된 전역 패턴 사전에서 출발", blank.ColdStartTransferLabel, StringComparison.Ordinal);
        Assert.Equal("콜드스타트 1/3개 컨텍스트 · 이식할 전역 검증 사전 없음(패턴 표본 2건부터)", PlutoEmpiricalBayes.BuildLabel(new PlutoColdStartTransferStatus(3, 1, 0, "A·B", 1, 0)));
        Assert.Equal("전 컨텍스트 성숙(2개) — 지역 데이터 우선 · 전역 검증 사전 4개 대기", PlutoEmpiricalBayes.BuildLabel(new PlutoColdStartTransferStatus(2, 0, 4, "", 0, 0)));

        // 🌡️ B 가 성숙(지역 시행 ≥ 3) — B 에서는 BaseHook 이 실제로 더 잘 된다 → 지역 데이터가 전역 사전을 이긴다(A 는 여전히 GoodHook)
        for (var i = 0; i < 4; i++)
        {
            await learning.RecordContextualObservationAsync(domA, modeA, 0.70, new[] { "hook:" + GoodHook });
            await learning.RecordContextualObservationAsync(domA, modeA, 0.50, new[] { "hook:" + BaseHook });
            await learning.RecordContextualObservationAsync(domB, modeB, 0.40, new[] { "hook:" + GoodHook });
            await learning.RecordContextualObservationAsync(domB, modeB, 0.60, new[] { "hook:" + BaseHook });
        }
        var matureB = ranker.RankForContext(PlutoPatternKind.Hook, domB, modeB, candidates);
        Assert.Equal(BaseHook, matureB[0].DisplayLabel);
        Assert.All(matureB, o => Assert.Equal((false, 1.0), (o.ContextTransfer.IsColdStart, o.ContextTransfer.LocalWeight)));
        Assert.True(matureB.Single(o => o.DisplayLabel == GoodHook).ContextMultiplier < 1.0 && matureB[0].ContextMultiplier > 1.0);
        Assert.Equal(GoodHook, ranker.RankForContext(PlutoPatternKind.Hook, domA, modeA, candidates)[0].DisplayLabel);
        Assert.Equal(0, ranker.GetColdStartTransferStatus().ColdStartContextCount);
        Assert.StartsWith("전 컨텍스트 성숙(2개)", PlutoEmpiricalBayes.BuildLabel(ranker.GetColdStartTransferStatus()), StringComparison.Ordinal);

        // 영속 왕복 — 지역 통계는 같은 학습 메모리 파일에 저장(구버전 파일과 키 공간 분리)
        var reopened = Learning();
        await reopened.EnsureLoadedAsync();
        Assert.Equal(learning.GetContextPatternAlpha(domB, modeB, "hook:" + GoodHook), reopened.GetContextPatternAlpha(domB, modeB, "hook:" + GoodHook));
        Assert.Equal(2, reopened.GetContextBaselines().Count);                                         // cpat: 키는 컨텍스트 기준선(ctx:) 목록에 섞이지 않는다
        Assert.StartsWith(PlutoEmpiricalBayes.LocalKeyPrefix + domB + "|" + modeB + "|", PlutoEmpiricalBayes.LocalKey(domB, modeB, "hook:x"), StringComparison.Ordinal);

        // Workspace — 두뇌 리포트 본문에만 · 상단 3행 헤더 불변 · 제한 키워드 없음
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(strip > 0 && tools > strip);
        var body = ws.Substring(strip, tools - strip);
        Assert.Equal(1, CountOf(ws, "data-brain-coldstart="));
        Assert.True(body.IndexOf("data-brain-coldstart=", StringComparison.Ordinal) > body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal));
        Assert.Contains("@(bs.ColdStartTransferLabel)", body, StringComparison.Ordinal);
        Assert.Contains("@(bs.SeriesArcLabel)", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        var head = ws.Substring(0, strip);
        foreach (var keyword in new[]
                 {
                     "콜드스타트", "경험적 베이즈", "톰슨", "data-brain-coldstart", "ColdStartTransfer", "새 시즌 분리", "data-series-new-season", "data-series-escalation-mode",
                     "추천만 표시", "자동 각색", "시리즈 아크",
                 })
            Assert.DoesNotContain(keyword, head, StringComparison.Ordinal);
    }

    private static PlutoRankedOption Arm(string label, double score, double trials, double ucbBonus)
        => new("hook:" + label, label, 1.0, 1.0, 0.0, score, "") { ArmTrials = trials, ExplorationBonus = ucbBonus, Posterior = PlutoPosteriorConfidence.Compute(1.0, trials) };

    [Fact]
    public async Task HybridThompsonUcb_IsReproducibleWithSeed_BreaksDeterministicTieLoops_AndFavorsUncertainUpside()
    {
        // 순수 표본기 — 같은 시드 = 같은 결과 · 입력 순서 무관
        var arms = new[] { Arm("미검증", 1.0, 0, 0.20), Arm("검증됨", 1.0, 30, 0.05) };
        var a = PlutoThompsonUcbSampler.Rescore(arms, new Random(17));
        var b = PlutoThompsonUcbSampler.Rescore(arms.Reverse().ToArray(), new Random(17));
        Assert.Equal(a.Select(o => (o.Key, o.UcbScore)), b.Select(o => (o.Key, o.UcbScore)));
        Assert.All(a, o => Assert.Equal(1.0, o.FinalScore));                                           // 활용 점수는 불변(탐색 점수만 재산정)
        // λ = 0 → 순수 UCB1 과 동일
        var pure = PlutoThompsonUcbSampler.Rescore(arms, new Random(3), null, blend: 0.0);
        Assert.Equal(new[] { ("hook:미검증", 1.20), ("hook:검증됨", 1.05) }, pure.Select(o => (o.Key, Math.Round(o.UcbScore, 9))).ToArray());
        Assert.Equal(1.0 + 0.5 * 0.20 + 0.5 * 0.1 + 0.3, PlutoThompsonUcbSampler.HybridScore(1.0, 0.20, 0.1, 0.3), 12);
        Assert.Equal(1.0, PlutoThompsonUcbSampler.HybridScore(1.0, double.NaN, double.NaN, -5));

        // 불확실성이 큰 후보를 더 자주(그러나 항상은 아니게) 끌어올린다 — UCB1 의 100% 고착을 깬다
        var firstUncertain = 0;
        for (var seed = 0; seed < 400; seed++)
            if (PlutoThompsonUcbSampler.Rescore(arms, new Random(seed))[0].DisplayLabel == "미검증") firstUncertain++;
        Assert.InRange(firstUncertain, 210, 385);
        // 후회 회복 사전은 추첨 잡음을 압도한다(거듭 지나친 고후회 대안 우선 검증)
        var priors = new Dictionary<string, double> { ["hook:검증됨"] = 1.0 };
        for (var seed = 0; seed < 50; seed++)
            Assert.Equal("검증됨", PlutoThompsonUcbSampler.Rescore(arms, new Random(seed), priors)[0].DisplayLabel);

        // Beta/Gamma 추첨 — 범위 · 평균
        var rng = new Random(5);
        double sum = 0;
        for (var i = 0; i < 4000; i++)
        {
            var x = PlutoThompsonUcbSampler.NextBeta(rng, 8, 2);
            Assert.InRange(x, 0.0, 1.0);
            sum += x;
        }
        Assert.InRange(sum / 4000, 0.77, 0.83);
        Assert.Equal(0.5, PlutoThompsonUcbSampler.NextBeta(rng, 0, 1));
        double gsum = 0;
        for (var i = 0; i < 4000; i++) gsum += PlutoThompsonUcbSampler.NextGamma(rng, 0.5);
        Assert.InRange(gsum / 4000, 0.44, 0.56);
        // 다목적 근거가 있는 후보는 Beta 사후 편차가 섞인다(같은 시드에서 근거 없는 후보와 다른 편차)
        var evidenced = Arm("근거", 1.0, 4, 0.1) with { Pareto = PlutoParetoFrontier.Assess(0, new PlutoObjectiveVector(0.6, 0.5, 0.4)), Objectives = new PlutoObjectiveVector(0.6, 0.5, 0.4), ObjectiveSamples = 4 };
        Assert.True(evidenced.Pareto.HasEvidence);
        Assert.NotEqual(PlutoThompsonUcbSampler.DrawDeviation(Arm("근거", 1.0, 4, 0.1), new Random(9)), PlutoThompsonUcbSampler.DrawDeviation(evidenced, new Random(9)));
        Assert.InRange(PlutoThompsonUcbSampler.CompositeRate(new PlutoObjectiveVector(0.6, 0.5, 0.4)), 0.52, 0.54);

        // 생성기 — 난수원 미주입: 결정론 UCB1(같은 상태 = 같은 챌린저 = 고착) · 주입: 시드 재현 + 시드 간 다양성
        const string theme = "아기 첫 걸음마";
        var ranker = new PlutoAdaptivePatternRanker(Learning("ab.json"), null);
        var ucbPairs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 4; i++)
        {
            var plan = await new PlutoVariantABGenerator(ranker, null, null, () => "exp-ucb").BuildVariantPairAsync(theme);
            Assert.Equal(PlutoThompsonUcbSampler.PolicyUcb, plan.ExplorationPolicy);
            Assert.False(plan.IsThompsonSampled);
            ucbPairs.Add(plan.ChallengerB.HookStyle + "|" + plan.ChallengerB.ColorLookId);
        }
        Assert.Single(ucbPairs);

        var first = await new PlutoVariantABGenerator(ranker, null, null, () => "exp-ts", new Random(42)).BuildVariantPairAsync(theme);
        var second = await new PlutoVariantABGenerator(ranker, null, null, () => "exp-ts", new Random(42)).BuildVariantPairAsync(theme);
        Assert.Equal((first.ChallengerB.HookStyle, first.ChallengerB.ColorLookId, first.ChallengerB.BgmMood), (second.ChallengerB.HookStyle, second.ChallengerB.ColorLookId, second.ChallengerB.BgmMood));
        Assert.Equal(first.ChallengerExplorationBonus, second.ChallengerExplorationBonus);
        Assert.Equal(first.HypothesisSummary, second.HypothesisSummary);
        Assert.True(first.IsThompsonSampled && first.IsValidExperiment);
        Assert.Contains("🎲 톰슨–UCB 하이브리드 탐색", first.HypothesisSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("🎰 UCB1 탐색 보너스", first.HypothesisSummary, StringComparison.Ordinal);

        var sampledPairs = new HashSet<string>(StringComparer.Ordinal);
        var champions = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 1; seed <= 40; seed++)
        {
            var plan = await new PlutoVariantABGenerator(ranker, null, null, () => "exp-ts", new Random(seed)).BuildVariantPairAsync(theme);
            Assert.True(plan.IsValidExperiment);
            sampledPairs.Add(plan.ChallengerB.HookStyle + "|" + plan.ChallengerB.ColorLookId);
            champions.Add(plan.ChampionA.HookStyle + "|" + plan.ChampionA.ColorLookId);
        }
        Assert.True(sampledPairs.Count >= 2, "Thompson sampling should diversify the challenger across seeds");
        Assert.Single(champions);                                                                      // 챔피언(활용)은 추첨과 무관하게 고정
        // 한 생성기 인스턴스를 연속 호출 — 난수 상태가 이어져 같은 상태에서도 챌린저가 순환 고착되지 않는다
        var shared = new PlutoVariantABGenerator(ranker, null, null, () => "exp-ts", new Random(7));
        var streak = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 30; i++)
        {
            var plan = await shared.BuildVariantPairAsync(theme);
            streak.Add(plan.ChallengerB.HookStyle + "|" + plan.ChallengerB.ColorLookId);
        }
        Assert.True(streak.Count >= 2);

        // 🧬 생성기 × 콜드스타트 — (테마 도메인, 몽타주) 컨텍스트가 비어 있어도 검증된 전역 승자가 챔피언 · 가설에 전이 표기
        var domain = PromptDomainClassifier.Classify(theme).ToString();
        var learning = Learning("ab_ctx.json");
        var ctxRanker = new PlutoAdaptivePatternRanker(learning, null);
        await SeedProvenGlobalAsync(learning, domain == "GeneralCinematic" ? "BabyStudio" : "GeneralCinematic", "Ambient15s");
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + GoodHook }, 1.14, "xp:test", 0);
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + GoodHook }, 1.14, "xp:test", 0);
        var coldPlan = await new PlutoVariantABGenerator(ctxRanker, null, null, () => "exp-cold", new Random(1)).BuildVariantPairAsync(theme, "몽타주", null, null, default);
        Assert.Equal(GoodHook, coldPlan.ChampionA.HookStyle);
        Assert.Equal("몽타주", coldPlan.ContextRenderMode);
        Assert.True(coldPlan.ChampionContextTransfer.IsTransferActive);
        Assert.Equal(1.0, coldPlan.ChampionContextTransfer.GlobalWeight);
        Assert.False(coldPlan.IsContextSpecialized);
        Assert.Contains("🧬 콜드스타트 컨텍스트 — 전역 검증 사전 이식(전역 가중 100%)", coldPlan.HypothesisSummary, StringComparison.Ordinal);
        // 그 컨텍스트가 성숙해 지역 근거가 순위를 바꾸면(이 컨텍스트에서는 GoodHook 이 부진 · 다른 도메인에서는 여전히 승자) 컨텍스트 특화 순위가 챔피언을 정한다
        var otherDomain = domain == "GeneralCinematic" ? "BabyStudio" : "GeneralCinematic";
        var both = new[] { "hook:" + GoodHook, "hook:" + BaseHook };
        for (var i = 0; i < 4; i++)
        {
            await learning.RecordContextualObservationAsync(otherDomain, "Ambient15s", 0.70, new[] { "hook:" + GoodHook });
            await learning.RecordContextualObservationAsync(otherDomain, "Ambient15s", 0.50, new[] { "hook:" + BaseHook });
            await learning.RecordContextualObservationAsync(domain, "몽타주", 0.35, new[] { "hook:" + GoodHook });
            await learning.RecordContextualObservationAsync(domain, "몽타주", 0.75, new[] { "hook:" + BaseHook });
            await learning.RecordPatternTrialsAsync(both);                                             // 폐루프와 같이 시행 수도 누적(챔피언 신뢰 승격 대상에서 제외되지 않게)
        }
        var localTop = ctxRanker.RankForContext(PlutoPatternKind.Hook, domain, "몽타주")[0];
        Assert.NotEqual(GoodHook, localTop.DisplayLabel);
        Assert.Equal(GoodHook, ctxRanker.RankOptions(PlutoPatternKind.Hook)[0].DisplayLabel);           // 전역 1위는 여전히 GoodHook
        var specialized = await new PlutoVariantABGenerator(ctxRanker, null, null, () => "exp-local").BuildVariantPairAsync(theme, "몽타주", null, null, default);
        Assert.True(specialized.IsContextSpecialized);
        Assert.Equal(localTop.DisplayLabel, specialized.ChampionA.HookStyle);
        Assert.False(specialized.ChampionContextTransfer.IsColdStart);
        Assert.Contains("🧬 컨텍스트 특화 순위 반영", specialized.HypothesisSummary, StringComparison.Ordinal);
        // 같은 학습 상태라도 컨텍스트를 주지 않으면(기존 호출) 전역 챔피언 그대로
        Assert.Equal(GoodHook, (await new PlutoVariantABGenerator(ctxRanker, null, null, () => "exp-global").BuildVariantPairAsync(theme)).ChampionA.HookStyle);

        // DI — 데스크톱 + 모바일 양쪽에 난수원 주입(운영 = 하이브리드) · Workspace 는 (도메인, 몽타주) 컨텍스트로 호출
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoVariantABGenerator>(sp => new PromptFlowStudio.Services.Pluto.PlutoVariantABGenerator("));
            Assert.Equal(1, CountOf(di, "explorationRandom: new Random()));"));
        }
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Equal(1, CountOf(ws, "gen.BuildVariantPairAsync(MontageThemeHint(), \"몽타주\", plan, cuts, _mediaCts.Token)"));
    }

    // ── ⑤ 자율 추가 ① — 🌐 이중 언어 자막: 에스컬레이션이 본문만 바꿔 영문 줄이 어긋나는 문제 ──

    [Fact]
    public void SelfInitiated_SeriesEscalation_KeepsBilingualSubtitlesInSync_InsteadOfLeavingStaleTranslation()
    {
        Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooks.Count, PlutoSeriesArcMemoryService.CuriosityGapHooksEnglish.Count);
        Assert.Equal(PlutoSeriesArcMemoryService.CliffhangerOutros.Count, PlutoSeriesArcMemoryService.CliffhangerOutrosEnglish.Count);
        for (var i = 0; i < PlutoSeriesArcMemoryService.CuriosityGapHooks.Count; i++)
        {
            Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooksEnglish[i], PlutoSeriesArcMemoryService.EnglishLineOf(PlutoSeriesArcMemoryService.CuriosityGapHooks[i]));
            Assert.Equal(PlutoSeriesArcMemoryService.CliffhangerOutrosEnglish[i], PlutoSeriesArcMemoryService.EnglishLineOf(PlutoSeriesArcMemoryService.CliffhangerOutros[i]));
            Assert.False(string.IsNullOrWhiteSpace(PlutoSeriesArcMemoryService.CuriosityGapHooksEnglish[i]) || string.IsNullOrWhiteSpace(PlutoSeriesArcMemoryService.CliffhangerOutrosEnglish[i]));
        }
        Assert.Null(PlutoSeriesArcMemoryService.EnglishLineOf("등록되지 않은 문구"));
        Assert.Null(PlutoSeriesArcMemoryService.EnglishLineOf(null));

        var hook = PlutoSeriesArcMemoryService.CuriosityGapHooks[1];
        var outro = PlutoSeriesArcMemoryService.CliffhangerOutros[1];
        var directive = new PlutoSeriesContinuityDirective("k#3", 2, 3, Golden, "Lullaby", 2, PlutoSeriesDropAxis.Both, hook, outro, -12, "이탈");

        // 이중 언어 큐 — 1막 영문 줄은 새 훅의 짝으로 교체(옛 문장의 번역이 남지 않음) · 마지막 큐는 양쪽 줄 모두 아웃트로 확장 · 구간 불변
        var bilingual = new[]
        {
            SubtitleCue.Precise(0.2, 4.9, "우리 아기 첫 봄나들이", "Our baby's first spring outing"),
            SubtitleCue.Precise(5.1, 9.9, "전개", "Middle"),
            SubtitleCue.Precise(10.1, 14.7, "엔딩", "Ending"),
        };
        var applied = PlutoSeriesArcMemoryService.ApplyDirective(bilingual, directive);
        Assert.Equal(hook, applied[0].Text);
        Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooksEnglish[1], applied[0].SecondaryText);
        Assert.NotEqual("Our baby's first spring outing", applied[0].SecondaryText);
        Assert.Equal(bilingual[1], applied[1]);
        Assert.Equal("엔딩" + PlutoSeriesArcMemoryService.OutroJoiner + outro, applied[2].Text);
        Assert.Equal("Ending" + PlutoSeriesArcMemoryService.OutroJoiner + PlutoSeriesArcMemoryService.CliffhangerOutrosEnglish[1], applied[2].SecondaryText);
        Assert.Equal((bilingual[0].EffectiveStartSec, bilingual[2].EffectiveEndSec), (applied[0].EffectiveStartSec, applied[2].EffectiveEndSec));
        // 멱등 — 다시 적용해도 양쪽 줄 모두 중복 없음
        var twice = PlutoSeriesArcMemoryService.ApplyDirective(applied, directive);
        Assert.Equal(applied.Select(c => (c.Text, c.SecondaryText)), twice.Select(c => (c.Text, c.SecondaryText)));

        // 단일 언어 큐는 단일 언어 그대로(영문 줄을 새로 만들지 않는다)
        var mono = new[] { new SubtitleCue(0, 5, "오프닝"), new SubtitleCue(5, 15, "엔딩") };
        var monoApplied = PlutoSeriesArcMemoryService.ApplyDirective(mono, directive);
        Assert.All(monoApplied, c => Assert.Null(c.SecondaryText));
        Assert.Equal(hook, monoApplied[0].Text);

        // 영문 짝이 없는 사용자 정의 문구 — 어긋난 번역을 남기느니 1막 보조 줄을 비우고, 마지막 큐의 기존 번역은 건드리지 않는다
        var custom = directive with { RecommendedHookLine = "사용자 정의 훅", CliffhangerOutroLine = "사용자 정의 아웃트로" };
        var customApplied = PlutoSeriesArcMemoryService.ApplyDirective(bilingual, custom);
        Assert.Equal("사용자 정의 훅", customApplied[0].Text);
        Assert.Null(customApplied[0].SecondaryText);
        Assert.Equal("Ending", customApplied[2].SecondaryText);
        // 빈 마지막 큐(텍스트 없음 · 영문만 있던 경우) → 양쪽 모두 아웃트로로 채움
        var blankLast = new[] { bilingual[0], SubtitleCue.Precise(10.1, 14.7, " ", "Ending") };
        var blankApplied = PlutoSeriesArcMemoryService.ApplyDirective(blankLast, directive);
        Assert.Equal((outro, PlutoSeriesArcMemoryService.CliffhangerOutrosEnglish[1]), (blankApplied[1].Text, blankApplied[1].SecondaryText));
    }

    // ── ⑥ 자율 추가 ② — 🪞 얼어붙은 로컬 미러: Drive 가 확실히 더 새로울 때만 갱신 ──

    [Fact]
    public void SelfInitiated_StaleLocalMirror_IsRefreshedOnlyWhenDriveCopyIsStrictlyNewerAndValid()
    {
        var drive = Path.Combine(_dir, "vault2", "Pluto");
        var local = Path.Combine(_dir, "local_mirror");
        Directory.CreateDirectory(drive);
        Directory.CreateDirectory(local);
        var stamp = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        string D(string f) => Path.Combine(drive, f);
        string L(string f) => Path.Combine(local, f);
        void Put(string path, string content, DateTime utc) { File.WriteAllText(path, content); File.SetLastWriteTimeUtc(path, utc); }

        const string stale = PlutoSeriesArcMemoryService.FileName, newerLocal = PlutoLearningService.FileName, sameAge = PlutoRenderCatalogService.FileName,
            corrupt = PlutoMistakeNotebookService.FileName, empty = PlutoExperimentLedgerService.FileName;
        Put(D(stale), "{\"v\":\"drive-3-weeks-later\"}", stamp.AddDays(21)); Put(L(stale), "{\"v\":\"first-restore\"}", stamp);           // 얼어붙은 미러
        Put(D(newerLocal), "{\"v\":\"drive\"}", stamp); Put(L(newerLocal), "{\"v\":\"offline-writes\"}", stamp.AddDays(2));                // 오프라인 중 기록(로컬이 더 새로움)
        Put(D(sameAge), "{\"v\":\"drive\"}", stamp.AddSeconds(1)); Put(L(sameAge), "{\"v\":\"local\"}", stamp);                            // 타임스탬프 해상도 이내
        Put(D(corrupt), "{\"v\":\"half-synced", stamp.AddDays(5)); Put(L(corrupt), "{\"v\":\"good-local\"}", stamp);                       // 동기화 도중 손상
        Put(D(empty), "", stamp.AddDays(5)); Put(L(empty), "{\"v\":\"good-local\"}", stamp);                                               // 0 B

        Assert.True(PlutoDriveStorageManager.IsMirrorStale(D(stale), L(stale)));
        Assert.False(PlutoDriveStorageManager.IsMirrorStale(D(newerLocal), L(newerLocal)));
        Assert.False(PlutoDriveStorageManager.IsMirrorStale(D(sameAge), L(sameAge)));
        Assert.False(PlutoDriveStorageManager.IsMirrorStale(D(corrupt), L(corrupt)));
        Assert.False(PlutoDriveStorageManager.IsMirrorStale(D(empty), L(empty)));
        Assert.False(PlutoDriveStorageManager.IsMirrorStale(D("missing.json"), L(stale)));

        var storage = new PlutoDriveStorageManager(drive, local);
        Assert.True(storage.IsCloudSynced);
        Assert.Equal(1, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));           // 낡은 미러 1건만 갱신(부재 파일은 Drive 에도 없음)
        Assert.Equal("{\"v\":\"drive-3-weeks-later\"}", File.ReadAllText(L(stale)));
        Assert.Equal(stamp.AddDays(21), File.GetLastWriteTimeUtc(L(stale)));                           // 수정 시각 보존 → 멱등
        Assert.Equal("{\"v\":\"offline-writes\"}", File.ReadAllText(L(newerLocal)));                   // 더 새로운 로컬은 절대 덮어쓰지 않는다
        Assert.Equal("{\"v\":\"local\"}", File.ReadAllText(L(sameAge)));
        Assert.Equal("{\"v\":\"good-local\"}", File.ReadAllText(L(corrupt)));
        Assert.Equal("{\"v\":\"good-local\"}", File.ReadAllText(L(empty)));
        Assert.Equal(0, storage.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Empty(Directory.EnumerateFiles(local, "*.tmp"));
        Assert.Equal("{\"v\":\"drive\"}", File.ReadAllText(D(newerLocal)));                            // 복원은 Drive 쪽을 건드리지 않는다

        // Drive 비활성 → 갱신 없음(테스트 격리)
        Put(L(stale), "{\"v\":\"first-restore\"}", stamp);
        Assert.Equal(0, new PlutoDriveStorageManager(null, local).RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles));
        Assert.Equal("{\"v\":\"first-restore\"}", File.ReadAllText(L(stale)));
    }

    // ── ⑦ 자율 추가 ③ — ⚡ 적재 전 write-through 내구성: 부분 스냅샷이 기존 이력을 덮지 않는다 · 동시 기록 ──

    [Fact]
    public async Task SelfInitiated_PreLoadWriteThrough_MergesOntoDiskHistory_AndSurvivesConcurrentRendersAndDispose()
    {
        // 디스크에 기존 이력(측정된 2편 시리즈 + 시즌 2)이 있는 상태
        using (var seed = Arc(null))
        {
            Assert.True(await seed.RecordEpisodePerformanceAsync(Tel("옛 여행 1/2편", 60, 50, "시리즈 1/2편")));
            Assert.True(await seed.RecordEpisodePerformanceAsync(Tel("옛 여행 2/2편", 58, 48, "시리즈 2/2편")));
        }

        // 새 세션 — 아크를 적재하기도 전에 1편 렌더가 끝난다(write-through 는 즉시 동작 · 백그라운드 기록은 적재 후 병합)
        using (var session = Arc(null))
        {
            Assert.True(session.RecordRenderedEpisode(Clip("new_ep1", "새 시리즈 1/3편 — 시작", Ep1Mode, Vintage, "MusicBox", T0)));
            Assert.Equal(Vintage, session.GetContinuityDirective("새 시리즈", 3, 2).AnchorColorLookId);  // 적재 전에도 무지연
            // 디스크에 이미 측정된 편은 적재 전 write-through(다른 영상의 재렌더)로 덮이지 않는다
            Assert.True(session.RecordRenderedEpisode(Clip("rerender", "옛 여행 1/2편", "시리즈 1/2편", Vintage, "MusicBox", T0)));   // 적재 전 메모리에는 일단 반영
            await session.FlushAsync();
            var arcs = session.GetSeriesArcs(8);
            Assert.Equal(2, arcs.Count);
            var oldArc = arcs.Single(a => a.SeriesTitle == "옛 여행");
            Assert.Equal(2, oldArc.MeasuredCount);                                                     // 기존 이력 보존(부분 스냅샷이 덮지 않음)
            Assert.Equal(60.0, oldArc.Episodes.Single(e => e.EpisodeNumber == 1).HookRetentionPct);
            Assert.NotEqual("rerender", oldArc.Episodes.Single(e => e.EpisodeNumber == 1).VideoId);    // 측정된 편 보호 규칙이 재적용 시에도 유지
            Assert.Equal(Vintage, session.GetContinuityDirective("새 시리즈", 3, 2).AnchorColorLookId);
        }
        using (var verify = Arc(null))
        {
            await verify.EnsureLoadedAsync();
            Assert.Equal(2, verify.GetSeriesArcs(8).Count);
            Assert.Equal(2, verify.GetSeriesArcs(8).Single(a => a.SeriesTitle == "옛 여행").MeasuredCount);
            Assert.Equal(Vintage, verify.GetContinuityDirective("새 시리즈", 3, 2).AnchorColorLookId);
        }

        // 동시 렌더(여러 스레드 · 적재 전후 혼재) — 전부 메모리에 즉시 반영되고 병합 플러시 뒤 전부 영속
        using (var busy = Arc(null, "busy.json"))
        {
            var changed = 0;
            busy.Changed += () => System.Threading.Interlocked.Increment(ref changed);
            Parallel.For(0, 24, i => Assert.True(busy.RecordRenderedEpisode(Clip("p" + i, "동시 " + i + " 1/3편", Ep1Mode, i % 2 == 0 ? Golden : Vintage, "Lullaby", T0.AddSeconds(i)))));
            Assert.Equal(24, busy.GetSeriesArcs(50).Count);
            Assert.Equal(24, System.Threading.Volatile.Read(ref changed));
            for (var i = 0; i < 24; i++) Assert.Equal(i % 2 == 0 ? Golden : Vintage, busy.GetContinuityDirective("동시 " + i, 3, 2).AnchorColorLookId);
            await busy.FlushAsync();
        }
        using (var reread = Arc(null, "busy.json"))
        {
            await reread.EnsureLoadedAsync();
            Assert.Equal(24, reread.GetSeriesArcs(50).Count);
        }

        // 백그라운드 기록이 걸린 채 Dispose — 예외 없이 종료 · 이후 호출은 무동작
        var late = Arc(null, "late.json");
        Assert.True(late.RecordRenderedEpisode(Clip("late1", "종료 경합 1/3편", Ep1Mode, Golden, "Lullaby", T0)));
        late.Dispose();
        await late.FlushAsync();
        Assert.False(late.RecordRenderedEpisode(Clip("late2", "종료 경합 2/3편", Ep2Mode, Golden, "Lullaby", T0)));

        // 읽기 불가(손상) 파일 세션 — write-through 는 메모리에서 동작하되 디스크 파일은 보존
        var lockedPath = Path.Combine(_dir, "locked.json");
        File.WriteAllText(lockedPath, "{\"Version\":1,\"Series\":[{\"Key\":\"x#3\",\"Title\":\"보존\",\"Total\":3,\"Episodes\":[{\"N\":1,\"Look\":\"" + Golden + "\"}]}]}");
        using (var fs = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var guarded = Arc(null, "locked.json");
            Assert.True(guarded.RecordRenderedEpisode(Clip("g1", "잠금 세션 1/3편", Ep1Mode, Vintage, "MusicBox", T0)));
            await guarded.FlushAsync();
            Assert.Equal(Vintage, guarded.GetContinuityDirective("잠금 세션", 3, 2).AnchorColorLookId);
            Assert.True(guarded.IsReadOnlySession);
        }
        Assert.Contains("보존", File.ReadAllText(lockedPath), StringComparison.Ordinal);
        Assert.DoesNotContain("g1", File.ReadAllText(lockedPath), StringComparison.Ordinal);
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
