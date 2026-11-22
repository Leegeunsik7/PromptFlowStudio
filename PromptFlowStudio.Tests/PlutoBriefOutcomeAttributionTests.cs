using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎯 Step 27 — 적용 기획안 출처 · 실측 채점 · 자동 사후 분석 · 적중률 피드백 · 두뇌 리포트 · 자체 추가 안전장치.</summary>
public sealed class PlutoBriefOutcomeAttributionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_brief_outcome_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    public PlutoBriefOutcomeAttributionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── 픽스처 ──

    private static PlutoRankedOption Opt(PlutoPatternKind kind, string label, double final, double trials = 0, double mult = 1.0, int paretoTier = -1, double bonus = 0.0)
        => new(PlutoAdaptivePatternRanker.PrefixOf(kind) + label, label, 1.0, mult, 0.0, final, "")
        {
            ArmTrials = trials,
            ExplorationBonus = bonus,
            Posterior = PlutoPosteriorConfidence.Compute(mult, trials),
            Pareto = paretoTier < 0 ? PlutoParetoAssessment.None : new PlutoParetoAssessment(paretoTier, 1.0, false, 0.0),
            ObjectiveSamples = paretoTier >= 0 ? 4.0 : 0.0,
        };

    private static readonly string Golden = PlutoColorGradingCompiler.GoldenHourWarmName;
    private static readonly string Pastel = PlutoColorGradingCompiler.NaturalPastelName;
    private static readonly string Vintage = PlutoColorGradingCompiler.VintageFilm16mmName;

    private static PlutoTopicPlannerInputs Inputs(string seed = "가을 숲 산책") => new()
    {
        Hooks = new[]
        {
            Opt(PlutoPatternKind.Hook, "pov-walk", 1.6, trials: 12, mult: 1.6, paretoTier: 0),
            Opt(PlutoPatternKind.Hook, "close-up reveal", 1.1, trials: 4, mult: 1.1),
            Opt(PlutoPatternKind.Hook, "question opener", 0.99, trials: 1, bonus: 0.6),
            Opt(PlutoPatternKind.Hook, "slow pan", 0.98, bonus: 0.2),
        },
        Looks = new[] { Opt(PlutoPatternKind.Look, Golden, 1.2), Opt(PlutoPatternKind.Look, Pastel, 1.0), Opt(PlutoPatternKind.Look, Vintage, 0.99) },
        BgmMoods = new[] { Opt(PlutoPatternKind.BgmMood, "Lullaby", 1.1), Opt(PlutoPatternKind.BgmMood, "Upbeat", 1.0), Opt(PlutoPatternKind.BgmMood, "Cinematic", 0.99) },
        StyleTags = new[] { Opt(PlutoPatternKind.StyleTag, "autumn", 1.1) },
        FramingRanking = PlutoAutonomousTopicPlannerService.OrderFraming(null), NowUtc = Now, SeedTheme = seed,
    };

    private static PlutoRenderedClipRecord Clip(string id, string look, string bgm, DateTimeOffset? at = null)
        => new(id, id, "몽타주", "첫 장면 자막", look, bgm, new[] { "autumn" }, "", at ?? Now, false);

    private static PlutoYouTubeIngestWatermark Wm(long views, double hook, double comp, double ctr, int stage = 0) => new(views, stage, hook, comp, ctr, Now);

    /// <summary>활용 1위 기획안 → 무장 → 클립에 출처 찍기.</summary>
    private static (PlutoTopicBrief Brief, PlutoRenderedClipRecord Clip, PlutoAppliedBriefArm? Arm) Stamped(string id, double expected = 50.0, PlutoTopicBrief? brief = null)
    {
        brief ??= PlutoAutonomousTopicPlannerService.Synthesize(Inputs())[0];
        brief = brief with { ExpectedComposite = expected };
        var arm = PlutoAppliedBriefArm.From(brief, Now);
        var rec = PlutoBriefOutcomeAttributionService.StampProvenance(Clip(id, brief.ColorLookId, brief.BgmMood), arm, Now.AddMinutes(10), 0, out var next, out var decision);
        Assert.Equal(PlutoBriefStampDecision.Stamped, decision);
        return (brief, rec, next);
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

    private static string Workspace() => File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));

    private static string MethodBody(string src, string signature)
    {
        var s = src.Substring(src.IndexOf(signature, StringComparison.Ordinal));
        return s.Substring(0, s.IndexOf("\n    }", StringComparison.Ordinal));
    }

    // ── ① [기획 즉시 적용] 출처 영속 · 레거시 호환 · 멀티 PC 병합 보존 ──

    [Fact]
    public async Task ApplyBrief_PersistsProvenanceOnClipRecord_LegacyCatalogLoads_AndUnionMergePreservesEvaluationMarkers()
    {
        var (brief, rec, _) = Stamped("clip-a", expected: 48.5);
        var path = Path.Combine(_dir, "catalog.json");
        var catalog = new PlutoRenderCatalogService(path);
        await catalog.RecordRenderedClipAsync(rec);

        var reloaded = (await new PlutoRenderCatalogService(path).GetRecentClipsAsync())[0];   // 디스크 왕복
        Assert.Equal(brief.BriefId, reloaded.AppliedBriefId);
        Assert.Equal(brief.Format, reloaded.AppliedBriefFormat);
        Assert.Equal(48.5, reloaded.AppliedBriefExpectedRetention);
        Assert.Equal(brief.TitleFraming, reloaded.AppliedBriefTitleFraming);
        Assert.Equal(brief.Kind, reloaded.AppliedBriefKind);
        Assert.Equal(brief.HookStyle, reloaded.AppliedBriefHookStyle);
        Assert.Equal(Now, reloaded.AppliedBriefAppliedAtUtc);
        Assert.False(reloaded.AppliedBriefEvaluated);

        // 레거시 카탈로그(필드 없음) → 깨끗하게 적재
        var legacy = Path.Combine(_dir, "legacy.json");
        File.WriteAllText(legacy, "{\"Version\":1,\"Clips\":[{\"VideoId\":\"old\",\"Title\":\"t\",\"RenderedAtUtc\":\"2026-01-01T00:00:00+00:00\",\"TelemetryLogged\":true}]}");
        var old = Assert.Single(await new PlutoRenderCatalogService(legacy).GetRecentClipsAsync());
        Assert.False(old.HasAppliedBrief);
        Assert.False(old.AppliedBriefEvaluated);
        Assert.Null(old.AppliedBriefExpectedRetention);

        // PC A = 태깅만 · PC B = 같은 클립 채점 완료 → 어느 순서로 병합해도 채점 표식 + 결과 유지
        var pathB = Path.Combine(_dir, "catalogB.json");
        var b = new PlutoRenderCatalogService(pathB);
        await b.RecordRenderedClipAsync(rec);
        Assert.True(await b.TryRecordBriefOutcomeAsync("clip-a", -3.25));
        var jsonA = File.ReadAllText(path);
        var jsonB = File.ReadAllText(pathB);
        foreach (var (cur, other) in new[] { (jsonA, jsonB), (jsonB, jsonA) })
        {
            var merged = PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, cur, other);
            Assert.NotNull(merged);
            var mp = Path.Combine(_dir, "m" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(mp, merged);
            var m = Assert.Single(await new PlutoRenderCatalogService(mp).GetRecentClipsAsync());
            Assert.Equal(brief.BriefId, m.AppliedBriefId);
            Assert.True(m.AppliedBriefEvaluated);
            Assert.Equal(-3.25, m.AppliedBriefSurpriseDelta);
            Assert.Equal(48.5, m.AppliedBriefExpectedRetention);
        }

        // Workspace — 적용 시 무장 · 렌더 기록 직전 출처 찍기
        var ws = Workspace();
        Assert.Contains("_appliedBriefArm = PromptFlowStudio.Services.Pluto.PlutoAppliedBriefArm.From(brief, DateTimeOffset.UtcNow);", MethodBody(ws, "private async Task ApplyTopicBriefAsync("), StringComparison.Ordinal);
        var capture = MethodBody(ws, "private void CapturePerfContext(");
        Assert.True(capture.IndexOf("StampProvenance(record, _appliedBriefArm", StringComparison.Ordinal) < capture.IndexOf("RecordCatalogClipAsync(record)", StringComparison.Ordinal));
    }

    // ── ② 심각 미달(≥ 12p) → 자동 사후 분석 → 다음 기획에서 그 조합 강등 ──

    [Fact]
    public async Task SevereUnderperformance_RecordsPostMortemInMistakeNotebook_AndPlannerDemotesThatCombination()
    {
        var (brief, rec, _) = Stamped("clip-bad", expected: 50.0);
        Assert.Equal("pov-walk", brief.HookStyle);
        Assert.Equal(Golden, brief.ColorLookId);
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "catalog.json"));
        var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "notebook.json"), () => Now.UtcDateTime);
        await catalog.RecordRenderedClipAsync(rec with { YouTubeVideoId = "ytBADBADBA1" });
        await catalog.UpdateYouTubeWatermarkAsync("clip-bad", Wm(300, 30, 15, 2.0));   // 합성 = 12 + 6 + 4 = 22 → Δ −28

        using var svc = new PlutoBriefOutcomeAttributionService(catalog, notebook);
        var sweep = await svc.SweepAsync();
        var v = Assert.Single(sweep.Verdicts);
        Assert.Equal(22.0, v.RealizedComposite, 6);
        Assert.Equal(-28.0, v.SurpriseDelta, 6);
        Assert.True(v.IsSevere);
        Assert.True(v.PostMortemRecorded);
        Assert.Equal(1, sweep.PostMortemCount);
        var pair = PlutoPairAttribution.PairKey("pov-walk", Golden)!;
        Assert.Contains(pair, v.PostMortemKeys);
        Assert.Contains(pair, notebook.GetSuppressedCompositeKeys());
        Assert.DoesNotContain("pov-walk", notebook.GetSuppressedTokens());   // 훅 자체는 전역 차단하지 않음(정확한 조합만)
        var rule = Assert.Single(notebook.GetActiveMistakeRules(), r => r.PatternKey == pair);
        Assert.Equal(PlutoMistakeNotebookService.DimensionCompletionDrop, rule.FailureDimension);   // 결손: 훅 0.29 · 완주 0.50 · CTR 0 → 완주 이탈

        // 다음 기획 — 같은 (훅, 룩) 조합은 다시 제안되지 않음(다른 테마 도메인에서도 쌍은 차단)
        var composites = notebook.GetSuppressedCompositeKeys();
        foreach (var seed in new[] { "가을 숲 산책", "도심 야경 드라이브" })
        {
            var next = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(seed) with { SuppressedComposites = composites });
            Assert.NotEmpty(next);
            Assert.DoesNotContain(next, b => b.HookStyle == "pov-walk" && b.ColorLookId == Golden);
        }

        // 재스윕 → 0건(정확히 1회) · 노트 적중 수 불변
        Assert.Empty((await svc.SweepAsync()).Verdicts);
        Assert.Single(notebook.GetActiveMistakeRules(), r => r.PatternKey == pair);
    }

    // ── ③ 기대 충족 → 적중률 정확히 1회(재가져오기 멱등) · 두뇌 리포트 본문 · 헤더 불변 ──

    [Fact]
    public async Task MeetingExpectation_IncrementsHitRateExactlyOnceAcrossReimports_AndSurfacesInBrainReportBodyOnly()
    {
        var (_, rec, _) = Stamped("clip-good", expected: 40.0);
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "catalog.json"));
        await catalog.RecordRenderedClipAsync(rec with { YouTubeVideoId = "ytGOODGOOD1" });
        await catalog.UpdateYouTubeWatermarkAsync("clip-good", Wm(40, 70, 50, 6.0));   // 조회 40 < 50 → 채점 보류

        using var svc = new PlutoBriefOutcomeAttributionService(catalog, null);
        Assert.Empty((await svc.SweepAsync()).Verdicts);
        Assert.Equal(1, (await svc.GetHitRateAsync()).PendingClips);

        // 자동 경로 — 카탈로그 변경(워터마크 성숙) → 백그라운드 스윕
        svc.Start();
        await catalog.UpdateYouTubeWatermarkAsync("clip-good", Wm(120, 70, 50, 6.0, stage: 1));   // 합성 = 28 + 20 + 12 = 60 → Δ +20
        var sw = Stopwatch.StartNew();
        while (svc.LastSweep is null && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        var auto = Assert.IsType<PlutoBriefSweepResult>(svc.LastSweep);
        var v = Assert.Single(auto.Verdicts);
        Assert.True(v.IsHit);
        Assert.Equal(20.0, v.SurpriseDelta, 6);

        // 재가져오기(더 성숙한 스냅샷) · 수동 재스윕 → 재채점 없음
        await catalog.UpdateYouTubeWatermarkAsync("clip-good", Wm(900, 40, 20, 2.0, stage: 2));
        Assert.Empty((await svc.SweepAsync()).Verdicts);
        Assert.False(await catalog.TryRecordBriefOutcomeAsync("clip-good", -50));   // CAS — 이미 채점
        var hr = await svc.GetHitRateAsync();
        Assert.Equal(1, hr.EvaluatedBriefs);
        Assert.Equal(1, hr.Hits);
        Assert.Equal(100.0, hr.HitRatePct);
        Assert.Equal(1, hr.ExploitEvaluated);

        var snap = await new PlutoAutonomousBrainSummaryService(null, null, null) { Catalog = catalog }.GetSummaryAsync();
        Assert.True(snap.BriefHitRate.HasEvidence);
        Assert.Contains("평가 1건 · 적중 1건(100%)", snap.BriefHitRateLabel, StringComparison.Ordinal);
        Assert.Contains("+20.0p", snap.BriefHitRateLabel, StringComparison.Ordinal);
        Assert.False((await new PlutoAutonomousBrainSummaryService(null, null, null).GetSummaryAsync()).BriefHitRate.HasEvidence);

        // Workspace — 접이식 본문에만(평가 ≥ 1 조건) · 상단 3행 헤더 불변 · 제한 키워드 없음
        var ws = Workspace();
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "data-brain-brief-hitrate="));
        Assert.True(body.IndexOf("@if (bs.BriefHitRate.HasEvidence)", StringComparison.Ordinal) > open);
        Assert.True(body.IndexOf("data-brain-brief-hitrate=", StringComparison.Ordinal) > body.IndexOf("@if (bs.BriefHitRate.HasEvidence)", StringComparison.Ordinal));
        Assert.Contains("기획안 적중률 요약: @(bs.BriefHitRateLabel)", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        foreach (var keyword in new[] { "기획안 적중률", "data-brain-brief-hitrate", "BriefHitRate", "실측 유튜브", "기획 즉시 적용" })
            Assert.DoesNotContain(keyword, ws.Substring(0, strip), StringComparison.Ordinal);
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoBriefOutcomeAttributionService>(sp => new PromptFlowStudio.Services.Pluto.PlutoBriefOutcomeAttributionService("));
        }
        Assert.Contains("IPlutoBriefOutcomeAttributionService)?.Start();", ws, StringComparison.Ordinal);
    }

    // ── ④ 자체 추가 A — 🎯 출처 무장 충실도 · TTL · 편수 예산(오귀속 차단) ──

    [Fact]
    public void SelfAddition_ArmFidelityTtlAndEpisodeBudget_PreventStaleOrOverriddenRendersFromBlamingThePlanner()
    {
        var brief = PlutoAutonomousTopicPlannerService.Synthesize(Inputs())[0] with { ExpectedComposite = 45 };
        var arm = PlutoAppliedBriefArm.From(brief, Now)!;
        var t = Now.AddMinutes(5);

        // 크리에이터가 룩을 다른 프리셋으로 바꿈 → 출처 없음 · 무장 유지
        var r = PlutoBriefOutcomeAttributionService.StampProvenance(Clip("x1", Vintage, brief.BgmMood), arm, t, 0, out var next, out var d);
        Assert.Equal(PlutoBriefStampDecision.Diverged, d);
        Assert.False(r.HasAppliedBrief);
        Assert.Same(arm, next);
        // BGM 변경도 동일 · 엔진 네이티브(프리셋 아님) 룩은 모순 아님
        Assert.Equal(PlutoBriefStampDecision.Diverged, Decide(Clip("x2", brief.ColorLookId, "Upbeat"), arm, t));
        Assert.Equal(PlutoBriefStampDecision.Stamped, Decide(Clip("x3", "engine-native", brief.BgmMood), arm, t));

        // TTL 48시간 경과 → 해제
        PlutoBriefOutcomeAttributionService.StampProvenance(Clip("x4", brief.ColorLookId, brief.BgmMood), arm, Now.AddHours(49), 0, out var expired, out d);
        Assert.Equal(PlutoBriefStampDecision.Expired, d);
        Assert.Null(expired);

        // 단편 예산 1 — 같은 VideoId 재렌더는 소비 안 함 · 다른 영상 → 소진 해제
        var single = brief with { TotalEpisodes = 1 };
        var a1 = PlutoAppliedBriefArm.From(single, Now);
        PlutoBriefOutcomeAttributionService.StampProvenance(Clip("s1", single.ColorLookId, single.BgmMood), a1, t, 0, out a1, out d);
        Assert.Equal(PlutoBriefStampDecision.Stamped, d);
        PlutoBriefOutcomeAttributionService.StampProvenance(Clip("s1", single.ColorLookId, single.BgmMood), a1, t, 0, out a1, out d);
        Assert.Equal(PlutoBriefStampDecision.Stamped, d);
        var over = PlutoBriefOutcomeAttributionService.StampProvenance(Clip("s2", single.ColorLookId, single.BgmMood), a1, t, 0, out a1, out d);
        Assert.Equal(PlutoBriefStampDecision.BudgetExhausted, d);
        Assert.False(over.HasAppliedBrief);
        Assert.Null(a1);

        // 시리즈 3부작 — 3편까지 같은 기획 결정으로 · 4번째 소진
        var series = brief with { TotalEpisodes = 3 };
        var sa = PlutoAppliedBriefArm.From(series, Now);
        for (var ep = 1; ep <= 3; ep++)
        {
            var e = PlutoBriefOutcomeAttributionService.StampProvenance(Clip("ep" + ep, series.ColorLookId, series.BgmMood), sa, t, ep, out sa, out d);
            Assert.Equal(PlutoBriefStampDecision.Stamped, d);
            Assert.Equal(Now, e.AppliedBriefAppliedAtUtc);
        }
        PlutoBriefOutcomeAttributionService.StampProvenance(Clip("ep4", series.ColorLookId, series.BgmMood), sa, t, 4, out sa, out d);
        Assert.Equal(PlutoBriefStampDecision.BudgetExhausted, d);

        // 무장 없음 → 그대로 · 다른 기획(트렌드 기획안) 적용 → 무장 해제
        Assert.Equal(PlutoBriefStampDecision.NoArm, Decide(Clip("n", Golden, "Lullaby"), null, t));
        Assert.Contains("_appliedBriefArm = null;", MethodBody(Workspace(), "private async Task ApplyBriefAsync("), StringComparison.Ordinal);

        static PlutoBriefStampDecision Decide(PlutoRenderedClipRecord c, PlutoAppliedBriefArm? a, DateTimeOffset at)
        {
            PlutoBriefOutcomeAttributionService.StampProvenance(c, a, at, 0, out _, out var dd);
            return dd;
        }
    }

    // ── ⑤ 자체 추가 B — 🧮 기획 결정 단위 집계 · 사후 분석 1회 · 멀티 PC 이중 채점 차단 ──

    [Fact]
    public async Task SelfAddition_DecisionLevelAggregation_SeriesCountsOnce_PostMortemOnce_AndMergedEvaluationNeverRegrades()
    {
        var brief = PlutoAutonomousTopicPlannerService.Synthesize(Inputs())[0] with { TotalEpisodes = 3, ExpectedComposite = 50 };
        var arm = PlutoAppliedBriefArm.From(brief, Now);
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "catalog.json"));
        var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "notebook.json"), () => Now.UtcDateTime);
        // 3편: 합성 62 · 32 · 37 → Δ +12 · −18 · −13 (두 편이 심각 미달 · 평균 −6.3)
        var metrics = new[] { (70.0, 60.0, 5.0), (40.0, 25.0, 3.0), (45.0, 30.0, 3.5) };
        for (var ep = 1; ep <= 3; ep++)
        {
            var c = PlutoBriefOutcomeAttributionService.StampProvenance(Clip("ep" + ep, brief.ColorLookId, brief.BgmMood, Now.AddMinutes(ep)), arm, Now.AddMinutes(ep), ep, out arm, out _);
            await catalog.RecordRenderedClipAsync(c with { YouTubeVideoId = "ytSERIES000" + ep });
            var (h, comp, ctr) = metrics[ep - 1];
            await catalog.UpdateYouTubeWatermarkAsync("ep" + ep, Wm(500, h, comp, ctr));
        }
        using var svc = new PlutoBriefOutcomeAttributionService(catalog, notebook);
        var sweep = await svc.SweepAsync();
        Assert.Equal(3, sweep.EvaluatedCount);
        Assert.Equal(1, sweep.PostMortemCount);   // 같은 기획 결정 → 사후 분석 1회
        var entry = Assert.Single(notebook.GetActiveMistakeRules(), r => r.PatternKey == PlutoPairAttribution.PairKey(brief.HookStyle, brief.ColorLookId));
        Assert.True(notebook.GetEffectivePenalty(entry.PatternKey) <= PlutoBriefOutcomeAttributionService.PostMortemPenalty + 1e-9);   // 이중 벌점 없음

        var hr = await svc.GetHitRateAsync();
        Assert.Equal(1, hr.EvaluatedBriefs);   // 시리즈 3편 = 기획 1건
        Assert.Equal(3, hr.EvaluatedClips);
        Assert.Equal(0, hr.Hits);              // 평균 Δ < 0
        Assert.Equal(1, hr.SevereMisses);

        // 멀티 PC — 상대 PC 는 아직 미채점 사본 → 병합 후 적재 → 재채점 없음
        var stale = Path.Combine(_dir, "stale.json");
        var otherPc = new PlutoRenderCatalogService(stale);
        foreach (var c in await catalog.GetRecentClipsAsync()) await otherPc.RecordRenderedClipAsync(c with { AppliedBriefEvaluated = false, AppliedBriefSurpriseDelta = null });
        var merged = PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, File.ReadAllText(stale), File.ReadAllText(Path.Combine(_dir, "catalog.json")));
        var mp = Path.Combine(_dir, "merged.json");
        File.WriteAllText(mp, merged);
        var mergedCatalog = new PlutoRenderCatalogService(mp);
        using var svc2 = new PlutoBriefOutcomeAttributionService(mergedCatalog, notebook);
        Assert.Empty((await svc2.SweepAsync()).Verdicts);
        Assert.Equal(1, (await svc2.GetHitRateAsync()).EvaluatedBriefs);
        Assert.Single(notebook.GetActiveMistakeRules(), r => r.PatternKey == entry.PatternKey);
    }

    // ── ⑥ 자체 추가 C — 🆔 기획안 ID 정체성(최종 테마 · 다양성 교체 · 편 번호 반영) ──

    [Fact]
    public void SelfAddition_BriefIdReflectsFinalContent_SoDifferentBriefsNeverShareOutcomeAttribution()
    {
        var a = PlutoAutonomousTopicPlannerService.Synthesize(Inputs("가을 숲 산책"));
        var b = PlutoAutonomousTopicPlannerService.Synthesize(Inputs("도심 야경 드라이브"));
        // 같은 훅·룩·BGM 이라도 테마가 다르면 다른 기획 → 다른 ID(이전엔 빈 테마로 해시돼 충돌)
        Assert.Equal(a[0].HookStyle, b[0].HookStyle);
        Assert.NotEqual(a[0].Theme, b[0].Theme);
        Assert.NotEqual(a[0].BriefId, b[0].BriefId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in a)
        {
            Assert.True(ids.Add(x.BriefId));
            Assert.Equal(PlutoAutonomousTopicPlannerService.BriefIdOf(x.Kind, x.Theme, x.HookStyle, x.ColorLookId, x.BgmMood, x.SeriesKey, x.NextEpisodeNumber), x.BriefId);
        }
        Assert.Equal(a[0].BriefId, PlutoAutonomousTopicPlannerService.Synthesize(Inputs("가을 숲 산책"))[0].BriefId);   // 결정론
        Assert.NotEqual(PlutoAutonomousTopicPlannerService.BriefIdOf("k", "t", "h", "l", "m", "s", 2), PlutoAutonomousTopicPlannerService.BriefIdOf("k", "t", "h", "l", "m", "s", 3));   // 편 번호
    }

    // ── ⑦ 📐 적중률 보정(활용 ↔ 경험적 베이즈 탐색) · 📏 기대치 수축 ──

    [Fact]
    public void HitRateCalibration_ShiftsConfidenceFromExploitToExploration_AndExpectationShrinksTowardChannelBaseline()
    {
        // 활용 0/3 실패 → 활용 배수 < 1 · 탐색 배수 = 1(표본 없음 → Beta(1,1))
        var cold = new PlutoBriefHitRateSummary(3, 0, 1, 3, 0, 0, 0, -9.0, 3, 0);
        Assert.True(cold.IsCalibrating);
        Assert.Equal(0.8 + 0.4 * 0.2, cold.ExploitFactor, 6);
        Assert.Equal(1.0, cold.ExploreFactor, 6);
        var baseInputs = Inputs();
        var plain = PlutoAutonomousTopicPlannerService.Synthesize(baseInputs with { HitRate = PlutoBriefHitRateSummary.Empty });
        var tuned = PlutoAutonomousTopicPlannerService.Synthesize(baseInputs with { HitRate = cold });
        var exploitPlain = Assert.Single(plain, x => x.Kind == PlutoTopicBriefKinds.ParetoExploit && x.HookStyle == "pov-walk");
        var exploitTuned = Assert.Single(tuned, x => x.Kind == PlutoTopicBriefKinds.ParetoExploit && x.HookStyle == "pov-walk");
        Assert.Equal(exploitPlain.Score * cold.ExploitFactor, exploitTuned.Score, 6);
        Assert.Contains("📐 기획 적중률 0/3", exploitTuned.RationaleKorean, StringComparison.Ordinal);
        Assert.DoesNotContain("📐", exploitPlain.RationaleKorean, StringComparison.Ordinal);
        Assert.Equal(1.0, (cold with { EvaluatedBriefs = 2 }).ExploitFactor);   // 3건 미만 → 무보정

        // 기대치 — 실측 없음 → 사전 40 · 채널 기준선 · 훅 실측은 기준선으로 수축
        Assert.Equal(PlutoBriefOutcomeAttributionService.PriorComposite, PlutoBriefOutcomeAttributionService.EstimateExpectedComposite("x", "pov-walk", null, out var ev, out _));
        Assert.False(ev);
        var clips = new[]
        {
            Clip("m1", Golden, "Lullaby") with { HookStyle = "pov-walk", YouTubeWatermark = Wm(1000, 70, 50, 6) },   // 60
            Clip("m2", Golden, "Lullaby") with { HookStyle = "slow pan", YouTubeWatermark = Wm(1000, 40, 20, 2) },   // 28
        };
        var exp = PlutoBriefOutcomeAttributionService.EstimateExpectedComposite(PlutoTopicBriefKinds.ColdStartPrior, "pov-walk", clips, out ev, out var n);
        Assert.True(ev);
        Assert.Equal(1, n);
        Assert.Equal(Math.Round((60.0 + 2.0 * 44.0) / 3.0, 1), exp, 6);   // 기준선 44 로 수축
        var brief = PlutoAutonomousTopicPlannerService.Synthesize(baseInputs with { RecentClips = clips })[0];
        Assert.True(brief.ExpectedEvidenced);
        Assert.Contains("📏 기대 합성", brief.RationaleKorean, StringComparison.Ordinal);
        Assert.Equal(brief.ExpectedComposite, PlutoAppliedBriefArm.From(brief, Now)!.ExpectedComposite);
    }
}
