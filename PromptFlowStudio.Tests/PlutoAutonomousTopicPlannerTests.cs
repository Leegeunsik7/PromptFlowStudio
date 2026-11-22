using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎯 Step 26 — 자율 렌더 전 주제 · 시리즈 기획기 · 실측 유튜브 성과 요약 · 1클릭 기획 적용 · 자체 추가 안전장치.</summary>
public sealed class PlutoAutonomousTopicPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_topic_plan_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    public PlutoAutonomousTopicPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── 픽스처 ──

    private static PlutoRankedOption Opt(PlutoPatternKind kind, string label, double final, double trials = 0, double mult = 1.0, double pf = 0.0, int paretoTier = -1,
        bool clickbait = false, double bonus = 0.0)
        => new(PlutoAdaptivePatternRanker.PrefixOf(kind) + label, label, 1.0, mult, pf, final, "")
        {
            ArmTrials = trials,
            ExplorationBonus = bonus,
            Posterior = PlutoPosteriorConfidence.Compute(mult, trials),
            Pareto = paretoTier < 0 && !clickbait ? PlutoParetoAssessment.None : new PlutoParetoAssessment(Math.Max(0, paretoTier), 1.0, clickbait, 0.0),
            ObjectiveSamples = paretoTier >= 0 ? 4.0 : 0.0,
        };

    private static PlutoRankedOption[] Hooks(bool warm) => new[]
    {
        Opt(PlutoPatternKind.Hook, "pov-walk", 1.6, trials: warm ? 12 : 0, mult: warm ? 1.6 : 1.0, paretoTier: warm ? 0 : -1),
        Opt(PlutoPatternKind.Hook, "close-up reveal", 1.1, trials: warm ? 4 : 0, mult: warm ? 1.1 : 1.0),
        Opt(PlutoPatternKind.Hook, "question opener", 0.99, trials: warm ? 1 : 0, bonus: 0.6),
        Opt(PlutoPatternKind.Hook, "slow pan", 0.98, trials: 0, bonus: 0.2),
    };

    private static PlutoRankedOption[] Looks() => new[]
    {
        Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.GoldenHourWarmName, 1.2),
        Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.NaturalPastelName, 1.0),
        Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.VintageFilm16mmName, 0.99),
    };

    private static PlutoRankedOption[] Bgms() => new[]
    {
        Opt(PlutoPatternKind.BgmMood, "Lullaby", 1.1),
        Opt(PlutoPatternKind.BgmMood, "Upbeat", 1.0),
        Opt(PlutoPatternKind.BgmMood, "Cinematic", 0.99),
    };

    private static PlutoRankedOption[] Tags() => new[] { Opt(PlutoPatternKind.StyleTag, "autumn", 1.1), Opt(PlutoPatternKind.StyleTag, "cozy", 1.0) };

    private static PlutoTitleFramingStat[] Framing(int curiositySamples)
        => new[]
        {
            new PlutoTitleFramingStat(PlutoTitleFramings.OutcomeProof, 0, 0, 0, 3.0),
            new PlutoTitleFramingStat(PlutoTitleFramings.CuriosityQuestion, curiositySamples, curiositySamples * 800L, 7.8, 7.5),
            new PlutoTitleFramingStat(PlutoTitleFramings.EndingPrompt, 0, 0, 0, 3.0),
        };

    private static PlutoRenderedClipRecord Clip(string id, string title, DateTimeOffset at, string hook = "pov-walk", string? look = null, string bgm = "Lullaby", string mode = "Ambient15s")
        => new(id, title, mode, hook, look ?? PlutoColorGradingCompiler.NaturalPastelName, bgm, new[] { "autumn" }, "", at, false);

    private static PlutoSeriesEpisodeStat Ep(int n, string hook = "pov-walk", bool measured = false, double h = 0, double c = 0)
        => new(n, "s" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), hook, PlutoColorGradingCompiler.VintageFilm16mmName, "Cinematic", measured, h, c, 0);

    private static PlutoSeriesArcReport Arc(string title, int total, PlutoSeriesEpisodeStat[] eps, DateTimeOffset updated, bool dropOff = false, int level = 0, int measured = 0, double delta = 0)
        => new(PlutoSeriesArcMemoryService.SeriesKeyFor(title, total), title, total, eps, Array.Empty<PlutoEpisodeRetentionDelta>(), 0, delta, measured, dropOff,
            dropOff ? PlutoSeriesDropAxis.Both : PlutoSeriesDropAxis.None, level, false, PlutoColorGradingCompiler.VintageFilm16mmName, "Cinematic", updated);

    private static PlutoTopicPlannerInputs Inputs(bool warm, int framingSamples = 3) => new()
    {
        Hooks = Hooks(warm), Looks = Looks(), BgmMoods = Bgms(), StyleTags = Tags(),
        FramingRanking = PlutoAutonomousTopicPlannerService.OrderFraming(Framing(framingSamples)), NowUtc = Now, SeedTheme = "가을 숲 산책",
    };

    private sealed class StubRanker : IPlutoAdaptivePatternRanker
    {
        private readonly bool _warm;
        public StubRanker(bool warm) => _warm = warm;
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public PlutoAdaptiveRecommendation GetCurrentRecommendations(IEnumerable<string>? candidateHooks = null, IEnumerable<string>? candidateLooks = null,
            IEnumerable<string>? candidateBgmMoods = null, IEnumerable<string>? candidateTags = null)
            => new(Hooks(_warm)[0], Looks()[0], Bgms()[0], Tags(), _warm);
        public IReadOnlyList<PlutoRankedOption> RankOptions(PlutoPatternKind kind, IEnumerable<string>? candidates = null) => kind switch
        {
            PlutoPatternKind.Hook => Hooks(_warm),
            PlutoPatternKind.Look => Looks(),
            PlutoPatternKind.BgmMood => Bgms(),
            _ => Tags(),
        };
        public IReadOnlyList<string> RankTags(IEnumerable<string> tags, int maxCount = 8) => new List<string>(tags);
        public string ResolveRecommendedBgmTrackId(string? topBgmMoodKeyOrLabel, IReadOnlyList<string> availableTrackIds, string fallbackTrackId) => fallbackTrackId;
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

    // ── ① 4축 파레토 승자 × 선두 제목 프레이밍 × 콜드스타트 경험적 베이즈 폴백 ──

    [Fact]
    public async Task Planner_SynthesizesRankedBriefs_FromParetoWinnersAndTopFraming_AndFallsBackToEmpiricalBayesPriorsWhenCold()
    {
        var warm = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true));
        Assert.Equal(3, warm.Count);
        for (var i = 0; i < warm.Count; i++) Assert.Equal(i + 1, warm[i].Rank);
        var top = warm[0];
        Assert.Equal(PlutoTopicBriefKinds.ParetoExploit, top.Kind);
        Assert.Equal("pov-walk", top.HookStyle);
        Assert.Equal(PlutoColorGradingCompiler.GoldenHourWarmName, top.ColorLookId);
        Assert.Equal("Lullaby", top.BgmMood);
        Assert.Equal(new[] { "autumn", "cozy" }, top.StyleTags);
        Assert.Equal("시리즈 3부작", top.Format);   // 검증된 파레토 승자 → 시리즈 투자
        Assert.Equal(3, top.TotalEpisodes);
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, top.TitleFraming);   // 실측 CTR 선두 프레이밍
        Assert.True(top.TitleFramingEvidenced);
        Assert.Contains("검증된 승자", top.RationaleKorean, StringComparison.Ordinal);
        Assert.Contains("실측 CTR 7.5% · n=3", top.RationaleKorean, StringComparison.Ordinal);
        Assert.Equal("가을 숲 산책", top.Theme);
        Assert.False(top.IsColdStart);
        Assert.Contains(warm, b => b.Kind == PlutoTopicBriefKinds.UpsideExplore && b.HookStyle == "question opener" && b.Format == "단편");
        Assert.Equal(warm.Count, new HashSet<string>(new[] { warm[0].BriefId, warm[1].BriefId, warm[2].BriefId }).Count);
        Assert.Equal(warm[0].BriefId, PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true))[0].BriefId);   // 결정론

        var cold = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: false, framingSamples: 0));
        Assert.NotEmpty(cold);
        Assert.All(cold, b => Assert.True(b.IsColdStart));
        Assert.Equal(PlutoTopicBriefKinds.ColdStartPrior, cold[0].Kind);
        Assert.Equal("단편", cold[0].Format);   // 근거 없는 패턴에 시리즈 투자 금지
        Assert.Contains("경험적 베이즈 사전", cold[0].RationaleKorean, StringComparison.Ordinal);
        Assert.DoesNotContain("검증된 승자", cold[0].RationaleKorean, StringComparison.Ordinal);
        Assert.Equal(PlutoTitleFramings.OutcomeProof, cold[0].TitleFraming);   // 근거 부족 → 기본 순서
        Assert.False(cold[0].TitleFramingEvidenced);

        // 서비스 경로 — 랭커 + 오답 노트 + 카탈로그 미연결 [부분 구제]
        var svc = new PlutoAutonomousTopicPlannerService(new StubRanker(warm: true), null, null) { UtcNowProvider = () => Now };
        var viaService = await svc.ProposeAsync("가을 숲 산책");
        Assert.Equal(3, viaService.Count);
        Assert.Equal("pov-walk", viaService[0].HookStyle);
        Assert.Empty(await new PlutoAutonomousTopicPlannerService(null, null, null).ProposeAsync(null, 3));   // 소스 전무 → 빈 제안(예외 없음)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ProposeAsync("x", 3, new CancellationToken(true)));
    }

    // ── ② 오답 노트 차단 제외 · 주제/훅 피로 가드 ──

    [Fact]
    public void Planner_ExcludesMistakeNotebookBlockedPatterns_AndTopicFatigueGuardPenalizesRecentlyRepeatedHooksAndThemes()
    {
        var hooks = Hooks(warm: true);
        var looks = Looks();
        looks[0] = Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.GoldenHourWarmName, 1.2, pf: PlutoAdaptivePatternRanker.MaxPenaltyFactor);   // 감점 상한 = 차단
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pov-walk", "Lullaby" };
        var recent = new[]
        {
            Clip("r1", "벚꽃 산책 브이로그", Now.AddHours(-1), hook: "close-up reveal"),
            Clip("r2", "벚꽃 산책 브이로그", Now.AddHours(-5), hook: "close-up reveal"),
            Clip("r3", "벚꽃 산책", Now.AddDays(-1), hook: "close-up reveal"),
            Clip("r4", "겨울 바다 캠핑", Now.AddDays(-9), hook: "slow pan"),
            Clip("r5", "도시 야경 드라이브", Now.AddDays(-12), hook: "slow pan"),
        };
        var briefs = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true) with
        {
            Hooks = hooks, Looks = looks, Suppressed = suppressed, RecentClips = recent, SeedTheme = "벚꽃 산책 브이로그",
        });
        Assert.NotEmpty(briefs);
        foreach (var b in briefs)
        {
            Assert.NotEqual("pov-walk", b.HookStyle);
            Assert.NotEqual(PlutoColorGradingCompiler.GoldenHourWarmName, b.ColorLookId);
            Assert.NotEqual("Lullaby", b.BgmMood);
            Assert.Contains("오답 가드 3개 제외", b.RationaleKorean, StringComparison.Ordinal);
            Assert.NotEqual(PlutoAutonomousTopicPlannerService.ThemeKeyOf("벚꽃 산책 브이로그"), PlutoAutonomousTopicPlannerService.ThemeKeyOf(b.Theme));   // 직전 테마 재탕 금지
        }
        // 1위(차단 제외 후) 훅 'close-up reveal' 을 3연속 사용 → 활용 기획은 다른 훅 + 피로 회피 근거
        Assert.NotEqual("close-up reveal", briefs[0].HookStyle);
        Assert.Contains("피로 회피", briefs[0].RationaleKorean, StringComparison.Ordinal);
        Assert.True(PlutoAutonomousTopicPlannerService.FatigueOf(recent, static c => c.HookStyle, "close-up reveal") > 1.7);
        Assert.Equal(0.0, PlutoAutonomousTopicPlannerService.FatigueOf(recent, static c => c.HookStyle, "question opener"));
        // 쿨다운 밖 과거 테마가 새 기획 테마로 재활용
        Assert.Contains(briefs, b => b.Theme is "겨울 바다 캠핑" or "도시 야경 드라이브");
        Assert.True(PlutoAutonomousTopicPlannerService.IsBlocked(Opt(PlutoPatternKind.Hook, "spike", 1.5, clickbait: true), null));   // 🎣 클릭베이트 급등도 제외
    }

    // ── ③ 미완 시리즈 이어가기 최우선 ──

    [Fact]
    public void Planner_PrioritizesUnfinishedMultiEpisodeSeries_ForContinuationBrief_WithAnchorsAndEscalationHook()
    {
        var arcs = new[]
        {
            Arc("여름 제주 여행", 3, new[] { Ep(1, measured: true, h: 60, c: 40), Ep(2, measured: true, h: 52, c: 30) }, Now.AddDays(-2), dropOff: true, level: 2, measured: 2, delta: -9),
            Arc("완결 시리즈", 2, new[] { Ep(1), Ep(2) }, Now.AddDays(-1)),   // 완결 → 대상 아님
        };
        var briefs = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true) with { SeriesArcs = arcs });
        var top = briefs[0];
        Assert.Equal(PlutoTopicBriefKinds.SeriesContinuation, top.Kind);
        Assert.Equal("여름 제주 여행", top.Theme);
        Assert.Equal(3, top.TotalEpisodes);
        Assert.Equal(3, top.NextEpisodeNumber);
        Assert.Equal("시리즈 3부작", top.Format);
        Assert.Equal(PlutoColorGradingCompiler.VintageFilm16mmName, top.ColorLookId);   // 앵커 유지
        Assert.Equal("Cinematic", top.BgmMood);
        Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooks[1], top.HookStyle);   // 이탈 단계 2 → 궁금증 훅
        Assert.Contains("2/3편까지 제작", top.RationaleKorean, StringComparison.Ordinal);
        Assert.Contains("-9.0p", top.RationaleKorean, StringComparison.Ordinal);
        Assert.Equal(arcs[0].SeriesKey, top.SeriesKey);
        Assert.DoesNotContain(briefs, b => b.Theme == "완결 시리즈");
        // 이어가기가 있으면 새 기획은 시리즈로 벌리지 않음(단편 우선)
        Assert.All(briefs, b => Assert.True(b.Kind == PlutoTopicBriefKinds.SeriesContinuation || b.Format == "단편"));
        Assert.Contains("series 3/3", PlutoAutonomousTopicPlannerService.BuildReadyPrompt(top), StringComparison.Ordinal);
    }

    // ── ④ 두뇌 리포트 실측 유튜브 성과 요약 · 헤더 불변 ──

    [Fact]
    public async Task BrainSummary_SurfacesRealizedYouTubeCtrRetentionAndFramingLeader_InsideCollapsibleBodyOnly()
    {
        var catalog = new PlutoRenderCatalogService(Path.Combine(_dir, "catalog.json"));
        static PlutoRenderedClipRecord Linked(string id, string yt, long views, double ctr, double hook, double comp, string framing)
            => Clip(id, id, Now.AddDays(-3)) with
            {
                YouTubeVideoId = yt, YouTubeWatermark = new PlutoYouTubeIngestWatermark(views, 1, hook, comp, ctr, Now.AddDays(-1)),
                SelectedTitleVariant = "A", PublishedTitleText = id + " title", PublishedTitleFraming = framing, TitleAttribution = PlutoTitleAttributionStates.Confirmed,
            };
        await catalog.RecordRenderedClipAsync(Linked("c1", "ytAAAAAAAA1", 2000, 6.0, 60, 40, PlutoTitleFramings.CuriosityQuestion));
        await catalog.RecordRenderedClipAsync(Linked("c2", "ytAAAAAAAA2", 500, 4.0, 50, 30, PlutoTitleFramings.CuriosityQuestion));
        await catalog.RecordRenderedClipAsync(Linked("c3", "ytAAAAAAAA3", 1000, 3.0, 45, 20, PlutoTitleFramings.OutcomeProof));
        await catalog.RecordRenderedClipAsync(Clip("c4", "unlinked", Now));

        var snap = await new PlutoAutonomousBrainSummaryService(null, null, null) { Catalog = catalog }.GetSummaryAsync();
        var r = snap.RealizedYouTube;
        Assert.Equal(3, r.LinkedVideoCount);
        Assert.Equal(3, r.MeasuredVideoCount);
        Assert.Equal(3500, r.TotalViews);
        Assert.Equal(4.4, r.WeightedCtrPct, 6);                 // (6·1 + 4·0.5 + 3·1) / 2.5
        Assert.Equal(52.0, r.WeightedHookPct, 6);               // (60 + 25 + 45) / 2.5
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, r.LeadingFraming);
        Assert.Equal(2, r.LeadingFramingSamples);
        Assert.Contains("연결 3편", snap.RealizedYouTubeLabel, StringComparison.Ordinal);
        Assert.Contains("CTR 4.4%", snap.RealizedYouTubeLabel, StringComparison.Ordinal);
        Assert.Contains("선두 제목 curiosity-question", snap.RealizedYouTubeLabel, StringComparison.Ordinal);
        Assert.Contains("연결 영상 없음", (await new PlutoAutonomousBrainSummaryService(null, null, null).GetSummaryAsync()).RealizedYouTubeLabel, StringComparison.Ordinal);

        // Workspace — 접이식 두뇌 리포트 본문에만 · 상단 3행 헤더 불변 · 제한 키워드 없음
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "data-brain-realized="));
        Assert.True(body.IndexOf("data-brain-realized=", StringComparison.Ordinal) > open);
        Assert.True(body.IndexOf("data-topic-plan-apply=", StringComparison.Ordinal) > open);
        Assert.Contains("@(bs.RealizedYouTubeLabel)", body, StringComparison.Ordinal);
        Assert.Contains("[🎯 기획 즉시 적용]", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        foreach (var keyword in new[] { "실측 유튜브", "data-brain-realized", "RealizedYouTube", "기획 즉시 적용", "data-topic-plan", "_topicBriefs" })
            Assert.DoesNotContain(keyword, ws.Substring(0, strip), StringComparison.Ordinal);
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoAutonomousTopicPlannerService>(sp => new PromptFlowStudio.Services.Pluto.PlutoAutonomousTopicPlannerService("));
            Assert.Equal(1, CountOf(di, "Catalog = sp.GetService(typeof(PromptFlowStudio.Services.Pluto.IPlutoRenderCatalogService)) as PromptFlowStudio.Services.Pluto.IPlutoRenderCatalogService });   // 🧠 Pluto 4.6"));
        }
    }

    // ── ⑤ 1클릭 적용 — 테마 · 시리즈 편수 · 패턴 토큰 · 수동 자막 보존 ──

    [Fact]
    public void OneClickApply_PopulatesThemeSeriesAndPatternTokens_WithoutClobberingManualSubtitleEdits()
    {
        var brief = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true))[0];
        var prompt = PlutoAutonomousTopicPlannerService.BuildReadyPrompt(brief);
        Assert.StartsWith("가을 숲 산책", prompt, StringComparison.Ordinal);
        foreach (var token in new[] { "opening hook: pov-walk", "color look: " + PlutoColorGradingCompiler.GoldenHourWarmName, "bgm mood: Lullaby", "style: autumn cozy", "series 1/3" })
            Assert.Contains(token, prompt, StringComparison.Ordinal);

        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var apply = ws.Substring(ws.IndexOf("private async Task ApplyTopicBriefAsync(", StringComparison.Ordinal));
        apply = apply.Substring(0, apply.IndexOf("\n    }", StringComparison.Ordinal));
        foreach (var marker in new[] { "BuildReadyPrompt(brief)", "_plannedSeriesEpisodes = brief.IsSeries ? brief.TotalEpisodes : 0;", "_learnedLookPinned = brief.ColorLookId", "ApplyBgmMoodAsync(brief.BgmMood)", "if (CuesManuallyEdited)", "Snap(\"기획 적용 전\")" })
            Assert.Contains(marker, apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_montageCues =", apply, StringComparison.Ordinal);   // 자막을 직접 덮지 않음
        Assert.Equal(1, CountOf(ws, "_cuesManualEditRef = _montageCues;"));
        Assert.Contains("_plannedSeriesEpisodes >= 2 ? _plannedSeriesEpisodes : 3", ws, StringComparison.Ordinal);
    }

    // ── ⑥ 자체 추가 A — 🧯 시리즈 피로 분류(붕괴 · 방치 아크 → 새 시즌 재시작) ──

    [Fact]
    public void SelfAddition_SeriesFatigueTriage_RebootsCollapsedOrStaleArcsInsteadOfBlindContinuation()
    {
        var collapsed = Arc("아기 첫 캠핑", 3, new[] { Ep(1, "close-up reveal", true, 70, 50), Ep(2, "close-up reveal", true, 45, 20) }, Now.AddDays(-1),
            dropOff: true, level: PlutoSeriesArcMemoryService.MaxEscalationLevel, measured: 2, delta: -27.5);
        var stale = Arc("봄 피크닉", 3, new[] { Ep(1) }, Now.AddDays(-(PlutoAutonomousTopicPlannerService.StaleSeriesDays + 15)));
        var briefs = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true) with { SeriesArcs = new[] { collapsed, stale } });
        Assert.DoesNotContain(briefs, b => b.Kind == PlutoTopicBriefKinds.SeriesContinuation);
        var reboot = Assert.Single(briefs, b => b.Kind == PlutoTopicBriefKinds.SeriesReboot && b.Theme == "아기 첫 캠핑");
        Assert.Equal(1, reboot.NextEpisodeNumber);
        Assert.Equal("시리즈 3부작", reboot.Format);
        Assert.NotEqual("close-up reveal", reboot.HookStyle);   // 지친 훅 회피
        Assert.Contains("유지율 붕괴", reboot.RationaleKorean, StringComparison.Ordinal);
        Assert.Contains(briefs, b => b.Kind == PlutoTopicBriefKinds.SeriesReboot && b.Theme == "봄 피크닉" && b.RationaleKorean.Contains("일 방치", StringComparison.Ordinal));

        // 같은 아크가 신선하고 붕괴 전이면 그대로 이어가기
        var healthy = Arc("봄 피크닉", 3, new[] { Ep(1) }, Now.AddDays(-3));
        Assert.Equal(PlutoTopicBriefKinds.SeriesContinuation, PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true) with { SeriesArcs = new[] { healthy } })[0].Kind);
    }

    // ── ⑦ 자체 추가 B — 🧩 포트폴리오 다양성 가드(복제 기획 방지) ──

    [Fact]
    public void SelfAddition_PortfolioDiversityGuard_PreventsCloneBriefsSharingHookOrLookBgmPair()
    {
        var oneLook = new[] { Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.NaturalPastelName, 1.0) };
        var twoBgm = new[] { Opt(PlutoPatternKind.BgmMood, "Lullaby", 1.1), Opt(PlutoPatternKind.BgmMood, "Upbeat", 1.0) };
        var briefs = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true) with { Looks = oneLook, BgmMoods = twoBgm });
        Assert.Equal(2, briefs.Count);   // (룩 · BGM) 조합이 2개뿐 → 복제 3번째 기획 대신 2건
        Assert.NotEqual(briefs[0].HookStyle, briefs[1].HookStyle);
        Assert.NotEqual(briefs[0].BgmMood, briefs[1].BgmMood);

        var full = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true));
        var hooks = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in full) { Assert.True(hooks.Add(b.HookStyle)); Assert.True(pairs.Add(b.ColorLookId + "|" + b.BgmMood)); }

        var first = full[0];
        Assert.Null(PlutoAutonomousTopicPlannerService.Diversify(first with { BriefId = "x" }, new[] { first }, Looks(), Bgms()));   // 같은 훅 → 탈락
        var swapped = PlutoAutonomousTopicPlannerService.Diversify(first with { HookStyle = "other" }, new[] { first }, Looks(), Bgms());
        Assert.NotNull(swapped);
        Assert.NotEqual(first.ColorLookId, swapped!.ColorLookId);   // 같은 (룩, BGM) → 미사용 대안 룩으로 교체
    }

    // ── ⑧ 자체 추가 C — 🔬 근거 정직성(표본 미달 수치 인용 금지) · 바이럴 독점 방지 ──

    [Fact]
    public void SelfAddition_EvidenceHonesty_NeverCitesUnderSampledNumbers_AndRealizedSummaryDampensViralOutliers()
    {
        var thin = PlutoAutonomousTopicPlannerService.Synthesize(Inputs(warm: true, framingSamples: 1));
        Assert.All(thin, b =>
        {
            Assert.False(b.TitleFramingEvidenced);
            Assert.DoesNotContain("실측 CTR", b.RationaleKorean, StringComparison.Ordinal);
            Assert.Contains("사전 순서(실측 귀속 2건 미만)", b.RationaleKorean, StringComparison.Ordinal);
        });
        Assert.Equal(PlutoTitleFramings.OutcomeProof, thin[0].TitleFraming);   // 1표본 급등에 순위를 넘겨주지 않음
        var explore = Assert.Single(thin, b => b.Kind == PlutoTopicBriefKinds.UpsideExplore);
        Assert.Contains("미검증", explore.RationaleKorean, StringComparison.Ordinal);
        Assert.DoesNotContain("검증된 승자", explore.RationaleKorean, StringComparison.Ordinal);

        PlutoRenderedClipRecord Yt(string id, long views, double ctr, string? framing = null) => Clip(id, id, Now) with
        {
            YouTubeVideoId = "yt" + id, YouTubeWatermark = new PlutoYouTubeIngestWatermark(views, 1, 50, 30, ctr, Now),
            PublishedTitleFraming = framing, TitleAttribution = framing is null ? null : PlutoTitleAttributionStates.Confirmed,
        };
        var clips = new[] { Yt("v", 1_000_000, 20.0, PlutoTitleFramings.EndingPrompt), Yt("a", 1000, 4.0), Yt("b", 1000, 4.0), Yt("c", 1000, 4.0), Yt("a", 1000, 99.0) };
        var s = PlutoRealizedYouTubeSummary.Compute(clips);
        Assert.Equal(4, s.LinkedVideoCount);                 // 중복 YouTube ID 1회
        Assert.Equal(8.0, s.WeightedCtrPct, 6);              // (20 + 4·3) / 4 — 바이럴 1편 가중치 상한 1
        Assert.False(s.HasFramingLeader);                    // 귀속 1건 → 선두 판정 보류
        Assert.Contains("선두 제목 근거 부족", s.ToLabel(), StringComparison.Ordinal);
        Assert.DoesNotContain(PlutoTitleFramings.EndingPrompt, s.ToLabel(), StringComparison.Ordinal);
    }
}
