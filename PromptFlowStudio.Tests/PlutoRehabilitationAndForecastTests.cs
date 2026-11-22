using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🌱 Step 28 — 사후 분석 30일 반감기 · 유기적 복권 · 렌더 전 셋업 예측 · 통합 진단 스냅샷 · 자체 추가 안전장치.</summary>
public sealed class PlutoRehabilitationAndForecastTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_rehab_forecast_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string Golden = PlutoColorGradingCompiler.GoldenHourWarmName;
    private static readonly string Pastel = PlutoColorGradingCompiler.NaturalPastelName;
    private static readonly string Vintage = PlutoColorGradingCompiler.VintageFilm16mmName;
    private const string Hook = "pov-walk";
    private const string Theme = "가을 숲 산책";

    public PlutoRehabilitationAndForecastTests() => Directory.CreateDirectory(_dir);

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

    private PlutoMistakeNotebookService Notebook(Clock clock, string name = "notebook.json") => new(Path.Combine(_dir, name), clock.Get);

    private static string PairOf(string hook, string look) => PlutoPairAttribution.PairKey(hook, look)!;

    private static string DomainHookOf(string theme, string hook) => PlutoPairAttribution.DomainHookKey(PromptDomainClassifier.Classify(theme).ToString(), hook)!;

    private static PlutoYouTubeIngestWatermark Wm(double hook, double comp, double ctr, long views = 1000) => new(views, 0, hook, comp, ctr, new DateTimeOffset(T0));

    /// <summary>측정 완료 커스텀 클립(기획 출처 없음).</summary>
    private static PlutoRenderedClipRecord Measured(string id, string hook, string look, PlutoYouTubeIngestWatermark wm, DateTime renderedUtc, string bgm = "Lullaby")
        => new PlutoRenderedClipRecord(id, id, "몽타주", hook, look, bgm, new[] { "autumn" }, "", new DateTimeOffset(renderedUtc), false) { YouTubeWatermark = wm };

    /// <summary>카탈로그 기록 → YouTube ID 연결 → 워터마크 반영(Step 27 픽스처와 같은 경로).</summary>
    private static async Task Put(PlutoRenderCatalogService catalog, PlutoRenderedClipRecord rec, string ytId)
    {
        await catalog.RecordRenderedClipAsync(rec with { YouTubeWatermark = null, YouTubeVideoId = ytId });
        Assert.True(await catalog.UpdateYouTubeWatermarkAsync(rec.VideoId, rec.YouTubeWatermark!));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ── ① 30일 반감기 · 레거시 JSON 호환 ──

    [Fact]
    public async Task PostMortem_DecaysWith30DayHalfLife_FallsBelowBlockThreshold_WhileFeedbackKeeps21Days()
    {
        var clock = new Clock();
        var nb = Notebook(clock);
        var pair = PairOf(Hook, Golden);
        await nb.RecordPostMortemMistakeAsync(new[] { pair }, PlutoBriefOutcomeAttributionService.PostMortemPenalty, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B1 Δ-20.0p");
        await nb.RecordNegativeFeedbackAsync(new[] { "vintage-grain" }, 2.0);
        Assert.Contains(pair, nb.GetSuppressedCompositeKeys());

        clock.Now = T0.AddDays(30);
        Assert.Equal(1.0, nb.GetEffectivePenalty(pair), 6);                                     // 정확히 1 반감기
        Assert.Equal(2.0 * Math.Pow(0.5, 30.0 / 21.0), nb.GetEffectivePenalty("vintage-grain"), 6);   // 피드백은 21일 유지
        Assert.True(nb.GetEffectivePenalty(pair) < PlutoMistakeNotebookService.BlockThreshold);
        Assert.Contains(pair, nb.GetSuppressedCompositeKeys());                                  // 히스테리시스 — 해제 임계(0.8) 전까지 유지
        var rule = Assert.Single(nb.GetActiveMistakeRules(), r => r.PatternKey == pair);
        Assert.True(rule.IsPostMortem);
        Assert.Equal(PlutoMistakeNotebookService.PostMortemHalfLifeDays, rule.HalfLifeDays);
        Assert.Equal(Math.Round(30.0 * Math.Log2(1.0 / 0.8), 1), rule.DaysUntilRelease, 1);

        clock.Now = T0.AddDays(40);                                                               // 2 × 0.5^(40/30) = 0.79 < 0.8
        Assert.DoesNotContain(pair, nb.GetSuppressedCompositeKeys());

        // 디스크 왕복 — 출처 필드 영속
        var reloaded = Notebook(clock);
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(nb.GetEffectivePenalty(pair), reloaded.GetEffectivePenalty(pair), 9);
    }

    [Fact]
    public async Task LegacyNotebookJson_WithoutStep28Fields_LoadsAndInfersPostMortemHalfLife()
    {
        var pair = PairOf(Hook, Golden);
        var path = Path.Combine(_dir, "legacy.json");
        // Step 27 형식 — Origin · PenalizedAtUtc · RehabilitatedClipIds 없음
        File.WriteAllText(path, "{\"Version\":1,\"Entries\":{"
            + "\"" + pair + "\":{\"Label\":\"" + pair + "\",\"RawPenalty\":2.0,\"LastUtc\":\"2026-11-01T09:00:00Z\",\"IsSuppressed\":true,\"Hits\":1,\"LastReason\":\"brief-postmortem: B9 Δ-15.0p\",\"FailureDimension\":\"HookRetention\",\"DiagnosticReason\":\"x\"},"
            + "\"lofi\":{\"Label\":\"lofi\",\"RawPenalty\":2.0,\"LastUtc\":\"2026-11-01T09:00:00Z\",\"IsSuppressed\":true,\"Hits\":2}}}");
        var clock = new Clock { Now = T0.AddDays(21) };
        var nb = new PlutoMistakeNotebookService(path, clock.Get);
        await nb.EnsureLoadedAsync();

        Assert.Equal(2, nb.TrackedCount);
        Assert.Equal(2.0 * Math.Pow(0.5, 21.0 / 30.0), nb.GetEffectivePenalty(pair), 6);   // 레거시 사후 분석 → 30일
        Assert.Equal(1.0, nb.GetEffectivePenalty("lofi"), 6);                               // 레거시 일반 → 21일
        var rules = nb.GetActiveMistakeRules();
        Assert.True(Assert.Single(rules, r => r.PatternKey == pair).IsPostMortem);
    }

    // ── ② 유기적 복권 ──

    [Fact]
    public async Task OrganicRehabilitation_OutperformingCustomClip_AttenuatesPenalizedPair_IdempotentPerClip()
    {
        var clock = new Clock();
        var nb = Notebook(clock);
        var pair = PairOf(Hook, Golden);
        await nb.RecordPostMortemMistakeAsync(new[] { pair }, 2.0, PlutoMistakeNotebookService.DimensionCompletionDrop, "brief-postmortem: B1 Δ-18.0p");
        Assert.Contains(pair, nb.GetSuppressedCompositeKeys());

        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "catalog.json"));
        await Put(catalog, Measured("base-1", "other hook", Pastel, Wm(40, 20, 2.0), T0.AddDays(-5)), "ytBASEBASE1");   // 합성 28
        await Put(catalog, Measured("base-2", "other hook", Pastel, Wm(40, 20, 2.0), T0.AddDays(-4)), "ytBASEBASE2");
        await Put(catalog, Measured("comeback", Hook, Golden, Wm(70, 50, 6.0), T0.AddDays(1)), "ytCOMEBACK1");          // 합성 60 → Δ +32(leave-one-out)

        clock.Now = T0.AddDays(2);
        using var svc = new PlutoBriefOutcomeAttributionService(catalog, nb);
        var sweep = await svc.SweepAsync();
        Assert.Contains(pair, sweep.RehabilitatedKeys);
        Assert.Equal(2.0 * Math.Pow(0.5, 2.0 / 30.0) * PlutoMistakeNotebookService.RehabilitationFactor, nb.GetEffectivePenalty(pair), 6);
        Assert.DoesNotContain(pair, nb.GetSuppressedCompositeKeys());                     // 0.67 < 0.8 → 즉시 해제

        // 🔁 같은 클립 재스윕 · 직접 재호출 → 추가 감쇠 없음
        var before = nb.GetEffectivePenalty(pair);
        Assert.Empty((await svc.SweepAsync()).RehabilitatedKeys);
        var again = await nb.RecordOrganicRecoveryAsync(new[] { pair }, "comeback", 32.0, T0.AddDays(1));
        Assert.False(again.Any);
        Assert.Equal(1, again.SkippedDuplicate);
        Assert.Equal(before, nb.GetEffectivePenalty(pair), 9);

        // 임계 미만(+7.9p) 은 무시
        var weak = await nb.RecordOrganicRecoveryAsync(new[] { pair }, "other-clip", 7.9, T0.AddDays(1));
        Assert.Same(PlutoRehabilitationResult.None, weak);

        // 복권 클립 ID 영속(재시작 후에도 클립당 1회)
        var reloaded = Notebook(clock);
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(1, (await reloaded.RecordOrganicRecoveryAsync(new[] { pair }, "comeback", 32.0, T0.AddDays(1))).SkippedDuplicate);
    }

    // ── ③ 렌더 전 셋업 예측 ──

    private static IReadOnlyList<PlutoRenderedClipRecord> ForecastClips() => new[]
    {
        Measured("f1", Hook, Golden, Wm(70, 50, 6.0), T0.AddDays(-9)),     // 60
        Measured("f2", Hook, Pastel, Wm(60, 40, 5.0), T0.AddDays(-8)),     // 50
        Measured("f3", "other", Vintage, Wm(40, 20, 2.0), T0.AddDays(-7)), // 28
        Measured("f4", "other", Vintage, Wm(40, 20, 2.0), T0.AddDays(-6)), // 28
    };

    [Fact]
    public async Task EvaluateSetupForecast_ReturnsBoundedScore_AndFlagsActiveBlockWithSafeLook()
    {
        var clock = new Clock();
        var nb = Notebook(clock);
        var clips = ForecastClips();

        var clean = PlutoBriefOutcomeAttributionService.EvaluateSetupForecast(Theme, Hook, Golden, "Lullaby", "몽타주", clips, nb);
        Assert.InRange(clean.PredictedScore, 0.0, 100.0);
        Assert.False(clean.IsBlocked);
        Assert.Equal(clean.BaseScore, clean.PredictedScore);
        Assert.True(clean.PredictedScore > 41.5);                                   // 훅·룩 실측 우위 → 채널 기준선(41.5) 위
        Assert.Equal(7, clean.EvidenceSamples);                                     // 훅 2 + 룩 1 + BGM 4
        Assert.Equal(PlutoForecastConfidence.Medium, clean.ConfidenceTier);         // 3~7 → 보통

        var pair = PairOf(Hook, Golden);
        await nb.RecordPostMortemMistakeAsync(new[] { pair }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B2 Δ-14.0p");
        var risky = PlutoBriefOutcomeAttributionService.EvaluateSetupForecast(Theme, Hook, Golden, "Lullaby", "몽타주", clips, nb);
        Assert.True(risky.IsBlocked);
        Assert.Equal(pair, risky.BlockedKey);
        Assert.True(risky.LookSwapFixesBlock);
        Assert.NotNull(risky.SafeLookId);
        Assert.NotEqual(Golden, risky.SafeLookId);
        Assert.Equal(Pastel, risky.SafeLookId);                                     // 남은 룩 중 실측 리프트 최고(f2 = 50 > Vintage 28)
        Assert.Equal(Math.Round(risky.BaseScore - 10.0, 1), risky.PredictedScore, 1);   // 벌점 2.0 × 5 = −10
        Assert.Contains("🛡️", risky.ToBadgeLabel());
        Assert.Contains(Pastel, risky.WarningKorean);

        // 인스턴스 경로(카탈로그 스냅샷) — 스냅샷 전에는 사전값, 갱신 후 동일 판정
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "fc_catalog.json"));
        await Put(catalog, clips[0], "ytFORECAST1");
        using var svc = new PlutoBriefOutcomeAttributionService(catalog, nb);
        Assert.Equal(PlutoForecastConfidence.Prior, svc.EvaluateSetupForecast(Theme, "unseen", Vintage, null, null).ConfidenceTier);
        await svc.RefreshForecastCacheAsync();
        var live = svc.EvaluateSetupForecast(Theme, Hook, Golden, "Lullaby", "몽타주");
        Assert.True(live.IsBlocked);
        Assert.NotEqual(PlutoForecastConfidence.Prior, live.ConfidenceTier);
    }

    // ── ④ 통합 진단 스냅샷 ──

    private sealed class FakeDrive : IPlutoDriveHealthService
    {
        public PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null)
        {
            var items = new List<PlutoDriveFileHealthItem>();
            for (var i = 0; i < PlutoDriveHealthService.PlutoFiles.Length; i++)
            {
                var f = PlutoDriveHealthService.PlutoFiles[i];
                items.Add(i == 0
                    ? new(PlutoDriveHealthService.CategoryPluto, f, @"C:\Users\someone\secret\" + f, PlutoDriveFileStatus.CorruptOrEmpty, 12, null, @"JSON 손상: C:\Users\someone\secret\" + f)
                    : new(PlutoDriveHealthService.CategoryPluto, f, @"G:\drive\" + f, PlutoDriveFileStatus.Healthy, 100, new DateTimeOffset(T0), null));
            }
            items.Add(new("Studio", "user_settings.json", @"G:\drive\user_settings.json", PlutoDriveFileStatus.Healthy, 10, null, null));
            return new PlutoDriveHealthReport(true, items.Count - 1, items.Count, null, null, items, new DateTimeOffset(T0));
        }

        public Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default) => Task.FromResult(CheckHealth());
        public IReadOnlyList<PlutoConflictCopy> ListConflicts() => new[] { new PlutoConflictCopy("pluto_render_catalog.json", @"G:\drive\conflicts\x.json", T0, 2048, true, true) };
        public int PendingConflictResolutions => 1;
    }

    private sealed class FakeBrain : IPlutoAutonomousBrainSummaryService
    {
        public Task<PlutoBrainSummarySnapshot> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new PlutoBrainSummarySnapshot(0, 0, 0, "", "", "", 0, "")
            {
                ParetoModalities = new[]
                {
                    new PlutoParetoModalityStatus(PlutoPatternKind.Hook, new PlutoParetoLeader(Hook, new PlutoObjectiveVector(70, 50, 6), 4, 2, 3, 0), 3, 2, 0),
                    new PlutoParetoModalityStatus(PlutoPatternKind.Look, new PlutoParetoLeader(Pastel, new PlutoObjectiveVector(60, 40, 5), 3, 1, 2, 1), 2, 1, 1),
                },
            });
    }

    [Fact]
    public async Task SystemDiagnostic_BuildsCompleteMarkdownAcrossAllHubs()
    {
        var clock = new Clock { Now = T0.AddDays(3) };
        var nb = Notebook(clock);
        clock.Now = T0;
        await nb.RecordPostMortemMistakeAsync(new[] { PairOf("a|b hook", Golden) }, 2.0, PlutoMistakeNotebookService.DimensionLowCtr, "brief-postmortem: B3 Δ-13.0p");
        clock.Now = T0.AddDays(3);
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "diag_catalog.json"));
        await Put(catalog, Measured("d1", Hook, Golden, Wm(70, 50, 6.0), T0.AddDays(-2)), "ytDIAGDIAG1");

        var svc = new PlutoSystemDiagnosticService(new FakeDrive(), catalog, nb, new FakeBrain(), clock.Get);
        var md = await svc.BuildSnapshotMarkdownAsync();

        Assert.StartsWith(PlutoSystemDiagnosticService.Title, md);
        foreach (var h in new[] { "## 1. ☁️ Drive 동기화 · 충돌", "## 2. 📺 실측 YouTube 성과 · 기획 적중률", "## 3. ⚖️ 4축 파레토 선두 · 제목 프레이밍", "## 4. 🛡️ 오답 노트 활성 가드" })
            Assert.Contains(h, md);
        Assert.Contains("Pluto 허브 " + (PlutoDriveHealthService.PlutoFiles.Length - 1) + "/" + PlutoDriveHealthService.PlutoFiles.Length + " 정상", md);
        foreach (var f in PlutoDriveHealthService.PlutoFiles) Assert.Contains("| " + f + " |", md);
        Assert.DoesNotContain("user_settings.json |", md);                    // 허브 표는 Pluto 9종만
        Assert.Contains("⚔️ 미해결 충돌: 1건 · 적용 대기 1건", md);
        Assert.Contains("연결 영상 1편 · 실측 1편", md);
        Assert.Contains("🎯 기획 적중률:", md);
        Assert.Contains("| 훅 | " + Hook + " | 70.0 | 50.0 | 6.0 | 2/3 | 0 |", md);
        Assert.Contains("| BGM | 근거 없음 |", md);                         // 4축 모두 행 출력
        Assert.Contains("| 태그 | 근거 없음 |", md);
        Assert.Contains("🩻 사후 분석", md);
        Assert.Contains("| 30 |", md);
        Assert.Contains("a\\|b hook", md);                                      // 셀 이스케이프
        Assert.Contains("2026-11-04 09:00:00 UTC", md);

        // 허브 미연결 → 섹션 제목 유지 · 예외 없음
        var empty = await new PlutoSystemDiagnosticService(null, null, null, null, clock.Get).BuildSnapshotMarkdownAsync();
        Assert.Contains("## 4. 🛡️ 오답 노트 활성 가드", empty);
        Assert.Contains("- 미연결", empty);
    }

    // ── ⑤ 자체 추가 A — ⏱️ 인과 가드(벌점 이전 클립은 복권 불가) ──

    [Fact]
    public async Task CoArchitect_CausalityGuard_ClipRenderedBeforePenaltyCannotRehabilitateIt()
    {
        var clock = new Clock();
        var nb = Notebook(clock);
        var pair = PairOf(Hook, Golden);
        await nb.RecordPostMortemMistakeAsync(new[] { pair }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B4 Δ-20.0p");
        clock.Now = T0.AddDays(1);
        var stale = await nb.RecordOrganicRecoveryAsync(new[] { pair }, "old-hit", 25.0, T0.AddDays(-3));   // 벌점 3일 전 렌더된 히트 클립
        Assert.False(stale.Any);
        Assert.Equal(1, stale.SkippedCausality);
        Assert.Contains(pair, nb.GetSuppressedCompositeKeys());

        // 스윕 경로 — 이전 히트 클립은 매 스윕 복권을 되풀이하지 못함
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "causal_catalog.json"));
        await Put(catalog, Measured("base", "other", Pastel, Wm(40, 20, 2.0), T0.AddDays(-9)), "ytCAUSALBA1");
        await Put(catalog, Measured("old-hit", Hook, Golden, Wm(70, 50, 6.0), T0.AddDays(-3)), "ytCAUSALOL1");
        using var svc = new PlutoBriefOutcomeAttributionService(catalog, nb);
        Assert.Empty((await svc.SweepAsync()).RehabilitatedKeys);
        Assert.Contains(pair, nb.GetSuppressedCompositeKeys());

        // 단일 특성 토큰은 복권 대상 아님(조합 키 전용)
        await nb.RecordNegativeFeedbackAsync(new[] { "lofi" }, 2.0);
        clock.Now = T0.AddDays(2);
        Assert.False((await nb.RecordOrganicRecoveryAsync(new[] { "lofi" }, "x", 30.0, T0.AddDays(2).AddMinutes(1))).Any);
    }

    // ── ⑤-2 자체 추가 A' — 🧮 사후 분석된 기획 결정의 형제 편(+Δ)은 같은 스윕에서 그 판정을 뒤집지 못함 ──

    [Fact]
    public async Task CoArchitect_SiblingOfPostMortemDecision_CannotRehabilitateItsOwnVerdict()
    {
        var clock = new Clock { Now = T0.AddMinutes(-10) };   // 노트 시계가 렌더보다 앞서도(인과 가드 통과) 결정 가드가 막아야 함
        var nb = Notebook(clock);
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "sibling_catalog.json"));
        PlutoAppliedBriefArm? arm = new("brief-sib", PlutoTopicBriefKinds.ParetoExploit, "시리즈 3부작", 2, Theme, Hook, Golden, "Lullaby", "Question", 50.0, new DateTimeOffset(T0));
        var metrics = new[] { (70.0, 60.0, 5.0), (40.0, 25.0, 3.0) };   // 합성 62(Δ +12) · 32(Δ −18)
        for (var ep = 1; ep <= 2; ep++)
        {
            var at = new DateTimeOffset(T0.AddMinutes(ep));
            var c = PlutoBriefOutcomeAttributionService.StampProvenance(Measured("sib" + ep, Hook, Golden, Wm(1, 1, 1), at.UtcDateTime) with { YouTubeWatermark = null }, arm, at, ep, out arm, out _);
            var (h, comp, ctr) = metrics[ep - 1];
            await Put(catalog, c with { YouTubeWatermark = Wm(h, comp, ctr, views: 500) }, "ytSIBLING00" + ep);
        }
        using var svc = new PlutoBriefOutcomeAttributionService(catalog, nb);
        var sweep = await svc.SweepAsync();
        Assert.Equal(1, sweep.PostMortemCount);
        Assert.Empty(sweep.RehabilitatedKeys);
        Assert.Contains(PairOf(Hook, Golden), nb.GetSuppressedCompositeKeys());
        Assert.Equal(PlutoBriefOutcomeAttributionService.PostMortemPenalty, nb.GetEffectivePenalty(PairOf(Hook, Golden)), 9);
    }

    // ── ⑥ 자체 추가 B — 🌱 커스텀 셋업 도달성 + leave-one-out 기대치 ──

    [Fact]
    public void CoArchitect_CustomClipSurprise_UsesLeaveOneOutBaseline_AndBriefClipUsesGradedDelta()
    {
        var hit = Measured("x", Hook, Golden, Wm(50, 30, 4.0), T0);           // 40
        var baseClip = Measured("z", "other", Pastel, Wm(40, 20, 2.0), T0);   // 28
        var clips = new[] { hit, baseClip };

        var loo = PlutoBriefOutcomeAttributionService.OrganicSurpriseOf(hit, clips);
        Assert.Equal(12.0, loo);                                               // 40 − 28(자기 제외 기준선)
        var naive = 40.0 - PlutoBriefOutcomeAttributionService.EstimateExpectedComposite(null, Hook, clips, out _, out _);
        Assert.True(naive < PlutoMistakeNotebookService.RehabilitationDeltaPoints);   // 자기 포함 기대치면 놀람이 8p 미만으로 상쇄 → 복권 불가
        Assert.True(loo >= PlutoMistakeNotebookService.RehabilitationDeltaPoints);

        // 조회 50 미만 → 판정 보류
        Assert.Null(PlutoBriefOutcomeAttributionService.OrganicSurpriseOf(hit with { YouTubeWatermark = Wm(50, 30, 4.0, views: 49) }, clips));

        // 기획 클립 → 채점 SurpriseDelta 그대로(미채점 · 채점값 없음 → null)
        var brief = hit with { AppliedBriefId = "brief-1", AppliedBriefExpectedRetention = 50.0 };
        Assert.Null(PlutoBriefOutcomeAttributionService.OrganicSurpriseOf(brief, clips));
        Assert.Equal(9.5, PlutoBriefOutcomeAttributionService.OrganicSurpriseOf(brief, clips, 9.5));
        Assert.Equal(-3.0, PlutoBriefOutcomeAttributionService.OrganicSurpriseOf(brief with { AppliedBriefEvaluated = true, AppliedBriefSurpriseDelta = -3.0 }, clips));
    }

    // ── ⑦ 자체 추가 C — 레거시 출처 전환 시 유효 벌점 연속(재기준 후 반감기 전환) ──

    [Fact]
    public async Task CoArchitect_OriginSwitch_RebasesWithPreviousHalfLife_NoDiscontinuity()
    {
        var clock = new Clock();
        var nb = Notebook(clock);
        var pair = PairOf(Hook, Golden);
        await nb.RecordPostMortemMistakeAsync(new[] { pair }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B5 Δ-20.0p");
        clock.Now = T0.AddDays(10);
        var carried = 2.0 * Math.Pow(0.5, 10.0 / 30.0);
        Assert.Equal(carried, nb.GetEffectivePenalty(pair), 9);

        await nb.RecordNegativeFeedbackAsync(new[] { pair }, 0.4);   // 크리에이터 수동 스킵 → 피드백 출처
        Assert.Equal(carried + 0.4, nb.GetEffectivePenalty(pair), 9);   // 전환 순간 점프 없음(30일 반감기로 재기준)
        clock.Now = T0.AddDays(31);
        Assert.Equal((carried + 0.4) * 0.5, nb.GetEffectivePenalty(pair), 9);   // 이후 21일 반감기
        Assert.False(Assert.Single(nb.GetActiveMistakeRules(), r => r.PatternKey == pair).IsPostMortem);
    }

    // ── ⑧ 자체 추가 D — 룩 교체로 해소되지 않는 차단엔 교체 버튼을 제안하지 않음 ──

    [Fact]
    public async Task CoArchitect_DomainHookBlock_IsNotOfferedFalseLookSwap()
    {
        var clock = new Clock();
        var nb = Notebook(clock);
        var dh = DomainHookOf(Theme, Hook);
        await nb.RecordPostMortemMistakeAsync(new[] { dh }, 2.0, PlutoMistakeNotebookService.DimensionCompletionDrop, "brief-postmortem: B6 Δ-16.0p");

        var fc = PlutoBriefOutcomeAttributionService.EvaluateSetupForecast(Theme, Hook, Golden, "Lullaby", "몽타주", ForecastClips(), nb);
        Assert.True(fc.IsBlocked);
        Assert.Equal(dh, fc.BlockedKey);
        Assert.False(fc.LookSwapFixesBlock);
        Assert.Null(fc.SafeLookId);
        Assert.Contains("룩 교체로는 해소되지 않아요", fc.WarningKorean);

        // pair 차단 + domain_hook 차단 동시 → 룩만 바꿔도 domain_hook 이 남으므로 교체 제안 없음
        await nb.RecordPostMortemMistakeAsync(new[] { PairOf(Hook, Golden) }, 2.0, PlutoMistakeNotebookService.DimensionHookRetention, "brief-postmortem: B7 Δ-16.0p");
        Assert.Null(PlutoBriefOutcomeAttributionService.EvaluateSetupForecast(Theme, Hook, Golden, "Lullaby", "몽타주", ForecastClips(), nb).SafeLookId);

        // 다른 테마 도메인 → domain_hook 무관 · pair 만 → 교체 제안
        var otherTheme = "주식 시황 브리핑";
        if (!string.Equals(DomainHookOf(otherTheme, Hook), dh, StringComparison.OrdinalIgnoreCase))
            Assert.NotNull(PlutoBriefOutcomeAttributionService.EvaluateSetupForecast(otherTheme, Hook, Golden, "Lullaby", "몽타주", ForecastClips(), nb).SafeLookId);
    }

    // ── ⑨ 자체 추가 E — 진단 스냅샷 공유 안전(경로 가림 · 셀 이스케이프) ──

    [Fact]
    public async Task CoArchitect_DiagnosticSnapshot_RedactsAbsolutePaths()
    {
        Assert.Equal("JSON 손상: ‹path›/pluto_trend_memory.json", PlutoSystemDiagnosticService.RedactPaths(@"JSON 손상: C:\Users\someone\secret\pluto_trend_memory.json"));
        Assert.Equal("‹path›/a.json 와 ‹path›/b.json", PlutoSystemDiagnosticService.RedactPaths(@"\\nas\share\a.json 와 /home/me/b.json"));
        Assert.Equal("훅 a/b 그대로", PlutoSystemDiagnosticService.RedactPaths("훅 a/b 그대로"));   // 상대 토큰은 보존
        Assert.Equal("x\\|y z", PlutoSystemDiagnosticService.Cell("x|y\nz"));

        var md = await new PlutoSystemDiagnosticService(new FakeDrive(), null, null, null, () => T0).BuildSnapshotMarkdownAsync();
        Assert.DoesNotContain(@"C:\Users", md);
        Assert.DoesNotContain("someone", md);
        Assert.DoesNotContain(@"G:\drive", md);
        Assert.Contains("‹path›/" + PlutoDriveHealthService.PlutoFiles[0], md);
    }

    // ── ⑩ 마크업 결선 — Workspace 예측 배지(헤더 밖) · Settings 진단 복사 · DI 양쪽 ──

    [Fact]
    public void Markup_ForecastBadgeOutsideHeader_DiagnosticButton_AndDiRegistered()
    {
        var root = RepoRoot();
        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var headStart = ws.IndexOf("<header class=\"ws-head\">", StringComparison.Ordinal);
        var headEnd = ws.IndexOf("</header>", headStart, StringComparison.Ordinal);
        var badge = ws.IndexOf("data-setup-forecast=", StringComparison.Ordinal);
        Assert.True(badge > headEnd);                                            // 상단 3행 헤더 무변경
        Assert.DoesNotContain("data-setup-forecast", ws.Substring(headStart, headEnd - headStart));
        Assert.Contains("data-setup-forecast-swap=", ws);
        Assert.Contains("ApplySafeLookSwapAsync(safeLook)", ws);
        Assert.Contains("svc.EvaluateSetupForecast(theme, hook, look, bgm, \"몽타주\")", ws);

        var st = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Settings.razor"));
        Assert.Contains("[🩺 플루토 진단 리포트 복사]", st);
        Assert.Contains("PlutoDiagnostics.BuildSnapshotMarkdownAsync()", st);
        Assert.Contains("Clipboard.SetText(md)", st);

        foreach (var di in new[] { Path.Combine(root, "PromptFlowStudio", "MainForm.cs"), Path.Combine(root, "PromptFlowStudio.Mobile", "MauiProgram.cs") })
            Assert.Contains("AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoSystemDiagnosticService>", File.ReadAllText(di));
    }
}
