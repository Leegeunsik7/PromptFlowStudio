using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// ⚖️📺⏱️ Step 16 — 전 모달리티(훅 · 룩 · BGM · 태그) 3축 파레토 귀속 · 시리즈 아크/편 간 유지율 연속성 메모리 · 자가 종료 회로 카운트다운 ·
/// 자율 추가(다목적 근거 신선도 감쇠 — 클릭베이트 영구 낙인 방지 · 반개방 탐침 임대 회수 — 회로 영구 고착 방지).
/// </summary>
public sealed class PlutoFullModalityParetoAndSeriesArcTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
    private const string Golden = PlutoColorGradingCompiler.GoldenHourWarmName, Vintage = PlutoColorGradingCompiler.VintageFilm16mmName;
    private const string Baby = "BabyStudio", Montage = "Montage";
    private const string ClickHook = "충격! 3초만 보세요", BalHook = "오늘도 한 뼘 자랐어요";
    private const string ClickTag = "#자극", BalTag = "#성장기록";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_step16_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;
    private int _seq;

    public PlutoFullModalityParetoAndSeriesArcTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoLearningService Learning(string name = "trend.json") => new(Path.Combine(_dir, name)) { UtcNowProvider = () => _now };
    private PlutoMistakeNotebookService Notebook(string name = "nb.json") => new(Path.Combine(_dir, name), () => _now);
    private PlutoRenderCatalogService Catalog(string name = "catalog.json") => new(Path.Combine(_dir, name));
    private PlutoSeriesArcMemoryService Arc(IPlutoRenderCatalogService? catalog, IPlutoMistakeNotebookService? notebook = null, string name = "arc.json")
        => new(Path.Combine(_dir, name), catalog, notebook) { UtcNowProvider = () => new DateTimeOffset(_now) };

    private PlutoVideoPerformanceTelemetry Video(string hook, string look, string bgm, string tag, double hookPct, double completionPct, double ctrPct, string? id = null, string? mode = null)
        => new(id ?? "v16-" + (++_seq).ToString("000"), "YouTube Shorts", hook, look, bgm, tag.Length == 0 ? Array.Empty<string>() : new[] { tag }, hookPct, completionPct, ctrPct, DateTimeOffset.UtcNow)
        { Domain = Baby, RenderMode = mode ?? Montage };

    private static PlutoRenderedClipRecord Clip(string id, string title, string mode, string look, string bgm, DateTime renderedUtc, string hook = "편 훅")
        => new(id, title, mode, hook, look, bgm, Array.Empty<string>(), "", new DateTimeOffset(renderedUtc), false);

    /// <summary>클릭베이트(3초 80 · 완주 10) 2편 + 균형(55 · 55) 2편을 폐루프로 반영 — 모달리티마다 서로 다른 값.</summary>
    private async Task SeedContrastAsync(PlutoClosedLoopReinforcementService closed)
    {
        for (var i = 0; i < 2; i++)
        {
            await closed.IngestPerformanceAsync(Video(ClickHook, Vintage, "MusicBox", ClickTag, 80, 10, 3));
            await closed.IngestPerformanceAsync(Video(BalHook, Golden, "Lullaby", BalTag, 55, 55, 5));
        }
    }

    // ── ① ⚖️ 전 모달리티 파레토 — BGM 무드 · 스타일 태그도 클릭베이트 강등 · 구버전 JSON 호환 ──

    [Fact]
    public async Task FullModalityPareto_BgmAndTags_RankBalancedCompounderAboveClickbaitSpike_AndLegacyMemoryStaysNeutral()
    {
        var learning = Learning();
        var notebook = Notebook();
        using var closed = new PlutoClosedLoopReinforcementService(learning, notebook);
        await SeedContrastAsync(closed);

        // 폐루프가 BGM · 태그에도 (3초 · 완주 · CTR) 를 귀속 — 기대치/드리프트 통계에는 불참(예측 잔차 불변)
        var bgmClick = learning.GetPatternObjectives("bgm:MusicBox");
        Assert.Equal((2.0, 0.80, 0.10, 0.30), (bgmClick.Samples, bgmClick.Objectives.Hook, bgmClick.Objectives.Completion, Math.Round(bgmClick.Objectives.Ctr, 9)));
        var tagBal = learning.GetPatternObjectives("style:" + BalTag);
        Assert.Equal((2.0, 0.55, 0.55), (tagBal.Samples, tagBal.Objectives.Hook, tagBal.Objectives.Completion));
        Assert.Equal(2.0, learning.GetPatternAlpha("bgm:MusicBox").Samples, 9);                     // Step 18 — BGM · 태그도 잔차 알파 EWMA(경험적 베이즈) 보유
        Assert.Equal(2.0, learning.GetPatternAlpha("style:" + ClickTag).Samples, 9);
        Assert.Equal(0, learning.GetRegimeDriftStatus().DriftCount);                                  // 드리프트 검정 · 반사실 예측에는 여전히 불참
        Assert.Equal(2.0, learning.GetPatternAlpha("hook:" + ClickHook).Samples, 9);                 // 훅 · 룩은 기존대로 기대치 참여
        Assert.Equal(new[] { "bgm:MusicBox", "style:" + ClickTag }, PlutoClosedLoopReinforcementService.ModalityObjectiveTokens(Video("h", Vintage, "MusicBox", ClickTag, 1, 1, 1)));

        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        // 🎵 BGM — 3초 돌파로 학습 부스트까지 받은 클릭베이트 무드가 사전 선호 1순위여도 균형 무드 · 근거 없는 무드 아래로
        var bgms = ranker.RankOptions(PlutoPatternKind.BgmMood, new[] { "MusicBox", "Lullaby", "Acoustic" });
        var click = bgms.Single(o => o.DisplayLabel == "MusicBox");
        var bal = bgms.Single(o => o.DisplayLabel == "Lullaby");
        var plain = bgms.Single(o => o.DisplayLabel == "Acoustic");
        Assert.Equal(new[] { "Lullaby", "Acoustic", "MusicBox" }, bgms.Select(o => o.DisplayLabel).ToArray());
        Assert.True(click.IsReinforced && click.LearnedWeightMultiplier > 1.1);                       // 학습 배수는 그대로 노출
        Assert.True(click.IsClickbaitSpike && !click.IsParetoFrontier);
        Assert.True(bal.IsParetoFrontier && !bal.IsClickbaitSpike);
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(click.BaseScore, 1.0 * PlutoParetoFrontier.Multiplier(0, click.Objectives), 0.0, click.Key), click.FinalScore, 9);   // 부스트 무효 + ×0.70
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(bal.BaseScore, bal.LearnedWeightMultiplier * 1.075, 0.0, bal.Key), bal.FinalScore, 9);
        Assert.Same(PlutoParetoAssessment.None, plain.Pareto);
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(plain.BaseScore, plain.LearnedWeightMultiplier, 0.0, plain.Key), plain.FinalScore, 12);
        Assert.Equal("bgm:Lullaby", ranker.GetCurrentRecommendations().TopBgmMood.Key);              // 순방향 추천도 균형 무드

        // 🏷️ 태그 — '#' 유무 변형이 같은 근거를 공유(가중치 · 시행과 같은 변형 규칙)
        var tags = ranker.RankOptions(PlutoPatternKind.StyleTag, new[] { "자극", BalTag, "#새태그" });
        Assert.Equal(BalTag, tags[0].DisplayLabel);
        Assert.Equal("자극", tags[^1].DisplayLabel);
        Assert.True(tags[^1].IsClickbaitSpike);
        Assert.Equal(2.0, tags[^1].ObjectiveSamples, 9);
        Assert.Equal(new[] { BalTag, "#새태그", "자극" }, ranker.RankTags(new[] { "자극", BalTag, "#새태그" }).ToArray());

        // 전 모달리티 현황(훅 · 룩 · BGM · 태그 순) — 각 1/2 프런티어 + 클릭베이트 강등 1
        var status = ranker.GetParetoModalityStatus();
        Assert.Equal(new[] { PlutoPatternKind.Hook, PlutoPatternKind.Look, PlutoPatternKind.BgmMood, PlutoPatternKind.StyleTag }, status.Select(s => s.Kind).ToArray());
        Assert.All(status, s => Assert.Equal((2, 1, 1), (s.EvidencedCount, s.FrontierSize, s.ClickbaitCount)));
        Assert.Equal(new[] { BalHook, Golden, "Lullaby", BalTag }, status.Select(s => s.Leader!.Label).ToArray());
        Assert.Equal("훅 1/2(🎣1) '" + BalHook + "' · 룩 1/2(🎣1) '" + Golden + "' · BGM 1/2(🎣1) 'Lullaby' · 태그 1/2(🎣1) '" + BalTag + "'", PlutoParetoFrontier.BuildModalityLabel(status));
        Assert.Equal(ranker.GetParetoFrontierLeader(), status[0].Leader);                             // 기존 훅 선두 API 와 동일 결과

        // A/B 변형 경로도 차이 나는 BGM 을 귀속
        using var ab = new PlutoClosedLoopReinforcementService(learning, notebook);
        await ab.IngestExperimentOutcomeAsync(new PlutoExperimentReinforcementRequest("exp-16",
            Video("", "", "Acoustic", "", 60, 50, 5), Video("", "", "MusicBox", "", 40, 30, 2), 30.0, false));
        Assert.Equal(1.0, learning.GetPatternObjectives("bgm:Acoustic").Samples, 9);
        Assert.Equal(3.0, learning.GetPatternObjectives("bgm:MusicBox").Samples, 9);

        // 🗃️ 구버전 pluto_trend_memory.json — BGM/태그에 다목적 필드가 없는 항목(Step 14 이하) + ObjUtc 없는 Step 15 훅 항목
        var legacyPath = Path.Combine(_dir, "legacy_trend.json");
        var stamp = T0.AddDays(-400).ToString("o");
        File.WriteAllText(legacyPath, "{"
            + "\"pattern:bgm:MusicBox\":{\"Weight\":1.6,\"Interactions\":3,\"LastUtc\":\"" + T0.ToString("o") + "\",\"Trials\":3},"
            + "\"pattern:style:#자극\":{\"Weight\":1.4,\"Interactions\":2,\"LastUtc\":\"" + T0.ToString("o") + "\",\"Trials\":2},"
            + "\"pattern:hook:구버전 훅\":{\"Weight\":1.0,\"Interactions\":0,\"LastUtc\":\"" + stamp + "\",\"ObjH\":0.8,\"ObjC\":0.1,\"ObjT\":0.3,\"ObjN\":4}"
            + "}");
        var legacy = new PlutoLearningService(legacyPath) { UtcNowProvider = () => _now };
        await legacy.EnsureLoadedAsync();
        var legacyRanker = new PlutoAdaptivePatternRanker(legacy, notebook);
        var legacyBgms = legacyRanker.RankOptions(PlutoPatternKind.BgmMood, new[] { "MusicBox", "Lullaby" });
        Assert.Equal("MusicBox", legacyBgms[0].DisplayLabel);                                         // 기존 순위 그대로(학습 ×1.6)
        Assert.All(legacyBgms, o => Assert.Same(PlutoParetoAssessment.None, o.Pareto));
        Assert.All(legacyBgms, o => Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(o.BaseScore, o.LearnedWeightMultiplier, o.MistakePenaltyFactor, o.Key), o.FinalScore, 12));
        Assert.All(legacyRanker.RankOptions(PlutoPatternKind.StyleTag, new[] { "#자극", "자극" }), o => Assert.Same(PlutoParetoAssessment.None, o.Pareto));
        Assert.Equal(4.0, legacy.GetPatternObjectives("hook:구버전 훅").Samples, 9);                  // 관측 시각 미상 → 신선도 감쇠 없음(판정 유지)
        Assert.True(legacyRanker.RankOptions(PlutoPatternKind.Hook, new[] { "구버전 훅" })[0].IsClickbaitSpike);
        // 새 기록 뒤에도 구버전 항목은 온전 + 다목적 필드가 없던 항목에는 기본값을 쓰지 않는다(양방향 호환)
        await legacy.RecordPatternObjectivesAsync(new[] { "bgm:Lullaby" }, PlutoObjectiveVector.FromPercent(50, 50, 5));
        var json = File.ReadAllText(legacyPath);
        Assert.Contains("\"pattern:bgm:Lullaby\"", json, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(json, "\"ObjUtc\""));                                                 // 방금 기록한 1건만
        Assert.Equal(2, CountOf(json, "\"ObjN\""));
        var reloaded = new PlutoLearningService(legacyPath) { UtcNowProvider = () => _now };
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(1.6, reloaded.GetPatternWeight("bgm:MusicBox"), 9);
        Assert.Equal(0.0, reloaded.GetPatternObjectives("bgm:MusicBox").Samples, 9);
        Assert.Equal(1.0, reloaded.GetPatternObjectives("bgm:Lullaby").Samples, 9);
    }

    // ── ② ⏳ 자율 추가 A — 다목적 근거 신선도: 한 번의 클릭베이트 판정이 영구 낙인이 되지 않는다 ──

    [Fact]
    public async Task ParetoEvidence_GoesStaleAfterGraceAndHalfLife_SoClickbaitVerdictIsNotPermanent_AndFreshDataRebasesQuickly()
    {
        // 순수 함수 — 유예 14일 이내 1.0 · 이후 28일 반감기 · 시각 미상/시계 역행 → 1.0
        Assert.Equal(1.0, PlutoParetoFrontier.EvidenceFreshness(T0, T0));
        Assert.Equal(1.0, PlutoParetoFrontier.EvidenceFreshness(T0, T0.AddDays(PlutoParetoFrontier.EvidenceGraceDays)));
        Assert.Equal(0.5, PlutoParetoFrontier.EvidenceFreshness(T0, T0.AddDays(PlutoParetoFrontier.EvidenceGraceDays + PlutoParetoFrontier.EvidenceHalfLifeDays)), 12);
        Assert.Equal(0.25, PlutoParetoFrontier.EvidenceFreshness(T0, T0.AddDays(14 + 56)), 12);
        Assert.Equal(1.0, PlutoParetoFrontier.EvidenceFreshness(default, T0.AddYears(5)));
        Assert.Equal(1.0, PlutoParetoFrontier.EvidenceFreshness(T0, T0.AddDays(-30)));

        var learning = Learning();
        var notebook = Notebook();
        var clickbait = PlutoObjectiveVector.FromPercent(80, 10, 3);
        for (var i = 0; i < 2; i++)
            await learning.RecordContextualObservationAsync(Baby, Montage, PlutoContextualBaseline.CompositeScore(80, 10, 3), new[] { "hook:" + ClickHook }, default, clickbait, new[] { "bgm:MusicBox", "bgm:MusicBox" });
        Assert.Equal(2.0, learning.GetPatternObjectives("bgm:MusicBox").Samples, 9);                 // 같은 호출의 중복 토큰은 1회만
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        // 유예 기간 끝까지는 판정 유지
        _now = T0.AddDays(PlutoParetoFrontier.EvidenceGraceDays);
        Assert.True(ranker.RankOptions(PlutoPatternKind.Hook, new[] { ClickHook })[0].IsClickbaitSpike);
        Assert.True(ranker.RankOptions(PlutoPatternKind.BgmMood, new[] { "MusicBox" })[0].IsClickbaitSpike);

        // 유예 + 1 반감기 → 유효 표본 1.0 < 2 → '근거 없음'으로 복귀(강등 해제 · 기존 점수 공식 그대로)
        _now = T0.AddDays(PlutoParetoFrontier.EvidenceGraceDays + PlutoParetoFrontier.EvidenceHalfLifeDays);
        Assert.Equal(1.0, learning.GetPatternObjectives("hook:" + ClickHook).Samples, 9);
        Assert.Equal(2.0, learning.GetPatternObjectives("hook:" + ClickHook, T0).Samples, 9);        // asOf 지정 조회
        foreach (var (kind, label) in new[] { (PlutoPatternKind.Hook, ClickHook), (PlutoPatternKind.BgmMood, "MusicBox") })
        {
            var stale = ranker.RankOptions(kind, new[] { label })[0];
            Assert.Same(PlutoParetoAssessment.None, stale.Pareto);
            Assert.False(stale.IsClickbaitSpike);
            Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(stale.BaseScore, stale.LearnedWeightMultiplier, 0.0, stale.Key), stale.FinalScore, 12);
        }
        Assert.All(ranker.GetParetoModalityStatus(), s => Assert.False(s.HasEvidence));

        // 공백 뒤의 새 관측 — 묵은 표본을 신선도만큼 줄여 재기준(2 × 0.5 + 1 = 2) → 스텝 ½ 로 낡은 평균을 빠르게 대체
        var balanced = PlutoObjectiveVector.FromPercent(50, 50, 5);
        await learning.RecordPatternObjectivesAsync(new[] { "hook:" + ClickHook }, balanced);
        var rebased = learning.GetPatternObjectives("hook:" + ClickHook);
        var step = PlutoContextualBaseline.StepFor(2.0, PlutoContextualBaseline.PatternAlpha);
        Assert.Equal(2.0, rebased.Samples, 9);
        Assert.Equal(0.80 + step * (0.50 - 0.80), rebased.Objectives.Hook, 9);
        Assert.Equal(0.10 + step * (0.50 - 0.10), rebased.Objectives.Completion, 9);
        Assert.True(step > PlutoContextualBaseline.StepFor(3.0, PlutoContextualBaseline.PatternAlpha) - 1e-12);   // 감쇠 없는 n=3 보다 느리지 않다
        var fresh = ranker.RankOptions(PlutoPatternKind.Hook, new[] { ClickHook })[0];
        Assert.True(fresh.Pareto.HasEvidence);
        Assert.Equal(fresh.Objectives.Completion >= PlutoParetoFrontier.ClickbaitCompletion, !fresh.IsClickbaitSpike);

        // 관측 시각은 영속된다(재시작 후에도 같은 유효 표본)
        var reloaded = Learning();
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(2.0, reloaded.GetPatternObjectives("hook:" + ClickHook).Samples, 9);
        Assert.Equal(0.5, reloaded.GetPatternObjectives("bgm:MusicBox").Samples / 2.0, 9);
    }

    // ── ③ 📺 시리즈 아크 — 1편 → 2편 이탈 감지 · 클리프행어/궁금증 훅 에스컬레이션 · 앵커 모티프 유지 ──

    [Fact]
    public async Task SeriesArc_DetectsEp1ToEp2RetentionDropOff_RecommendsCliffhangerHookEscalation_AndPreservesAnchorMotifs()
    {
        // 순수 함수 — 편 표기 · 테마 · 키
        Assert.True(PlutoSeriesArcMemoryService.TryParseEpisodeMode("시리즈 2/3편", out var pn, out var pt));
        Assert.Equal((2, 3), (pn, pt));
        Assert.False(PlutoSeriesArcMemoryService.TryParseEpisodeMode("몽타주", out _, out _));
        Assert.False(PlutoSeriesArcMemoryService.TryParseEpisodeMode("시리즈 4/3편", out _, out _));
        Assert.False(PlutoSeriesArcMemoryService.TryParseEpisodeMode("16/9 화면", out _, out _));
        Assert.Equal("우리 여름", PlutoSeriesArcMemoryService.SeriesThemeOf("우리 여름 2/3편 — 전개"));
        Assert.Equal("우리 여름", PlutoSeriesArcMemoryService.SeriesThemeOf("[2/3편] 우리 여름"));
        Assert.Equal(PlutoSeriesArcMemoryService.SeriesKeyFor("우리 여름", 3), PlutoSeriesArcMemoryService.SeriesKeyFor("우리 여름 1/3편 — 시작", 3));
        Assert.NotEqual(PlutoSeriesArcMemoryService.SeriesKeyFor("우리 여름", 3), PlutoSeriesArcMemoryService.SeriesKeyFor("우리 여름", 2));
        Assert.Equal("2/3편", PlutoSeriesBatchPlanner.PartTag(2, 3));
        Assert.Equal(37.5, PlutoSeriesArcMemoryService.RetentionIndexOf(45, 30), 9);

        using var catalog = Catalog();
        await catalog.RecordRenderedClipAsync(Clip("summer_ep1", "우리 여름 1/3편 — 시작", "시리즈 1/3편", Golden, "Lullaby", T0.AddHours(-3)));
        await catalog.RecordRenderedClipAsync(Clip("summer_ep2", "우리 여름 2/3편 — 전개", "시리즈 2/3편", Golden, "Lullaby", T0.AddHours(-2)));
        await catalog.RecordRenderedClipAsync(Clip("single_clip", "단편 몽타주", "몽타주", Vintage, "MusicBox", T0.AddHours(-1)));

        var learning = Learning();
        var notebook = Notebook();
        using var arc = Arc(catalog, notebook);
        var changed = 0;
        arc.Changed += () => changed++;
        using var closed = new PlutoClosedLoopReinforcementService(learning, notebook) { SeriesArc = arc };

        // 렌더만 된 상태 — 앵커는 이미 유도되지만 편 간 비교는 아직 없음
        Assert.Equal(2, await arc.SyncFromCatalogAsync());
        Assert.Equal(0, await arc.SyncFromCatalogAsync());                                            // 멱등
        var before = arc.GetContinuityDirective("우리 여름", 3, 2);
        Assert.Equal((Golden, "Lullaby", false), (before.AnchorColorLookId, before.AnchorBgmMood, before.EscalateHook));
        Assert.Same(PlutoSeriesContinuityDirective.None, arc.GetContinuityDirective("우리 여름", 3, 1));      // 1편은 이어받을 편이 없다
        Assert.Same(PlutoSeriesContinuityDirective.None, arc.GetContinuityDirective("없는 시리즈", 3, 2));

        // 성과 유입(폐루프 경유) — 1편 유지 지수 55 → 2편 37.5(−17.5p · 3초 −15 · 완주 −20)
        await closed.IngestPerformanceAsync(Video("1편 훅", Golden, "Lullaby", "", 60, 50, 5, "summer_ep1"));
        await closed.IngestPerformanceAsync(Video("2편 훅", Golden, "Lullaby", "", 45, 30, 4, "summer_ep2"));
        await closed.IngestPerformanceAsync(Video("단편 훅", Vintage, "MusicBox", "", 20, 10, 1, "single_clip"));   // 비시리즈 → 아크 무영향
        Assert.False(await arc.RecordEpisodePerformanceAsync(Video("단편 훅", Vintage, "MusicBox", "", 20, 10, 1, "single_clip")));
        Assert.False(await arc.RecordEpisodePerformanceAsync(Video("x", Golden, "Lullaby", "", double.NaN, 30, 4, "summer_ep2")));
        Assert.True(changed >= 3);

        var report = Assert.Single(arc.GetSeriesArcs());
        Assert.Equal(("우리 여름", 3, 2, true), (report.SeriesTitle, report.TotalEpisodes, report.MeasuredCount, report.HasDropOff));
        var delta = Assert.Single(report.EpisodeRetentionDeltas);
        Assert.Equal((1, 2), (delta.FromEpisode, delta.ToEpisode));
        Assert.Equal((-17.5, -15.0, -20.0), (delta.DeltaPts, delta.HookDeltaPts, delta.CompletionDeltaPts));
        Assert.Equal(-17.5, report.EpisodeRetentionDelta, 9);
        Assert.Equal(-17.5, report.RetentionSlope, 9);
        Assert.Equal((PlutoSeriesDropAxis.Both, 2, false), (report.DropAxis, report.EscalationLevel, report.HookOverheated));
        Assert.Equal((Golden, "Lullaby"), (report.AnchorColorLookId, report.AnchorBgmMood));

        // 3편(완결) 지시 — 앵커 유지 + 궁금증 훅 Lv2 · 완결편에는 '다음 편' 아웃트로 없음
        var directive = await arc.GetContinuityDirectiveAsync("우리 여름", 3, 3);
        Assert.True(directive.EscalateHook && directive.HasAnchor && directive.HasGuidance);
        Assert.Equal((3, 3, 2, PlutoSeriesDropAxis.Both), (directive.NextEpisodeNumber, directive.TotalEpisodes, directive.EscalationLevel, directive.DropAxis));
        Assert.Equal((Golden, "Lullaby"), (directive.AnchorColorLookId, directive.AnchorBgmMood));
        Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooks[1], directive.RecommendedHookLine);
        Assert.Equal(string.Empty, directive.CliffhangerOutroLine);
        Assert.Equal("📉 1편→2편 유지 지수 −17.5p(3초 −15 · 완주 −20) → 궁금증 훅 + 클리프행어 Lv2", directive.ReasonKorean);
        Assert.Equal(-17.5, directive.EpisodeRetentionDelta, 9);
        // 편 제목으로 조회해도 같은 시리즈
        Assert.Equal(directive, arc.GetContinuityDirective("우리 여름 3/3편 — 완결", 3, 3));

        // 3막 자막 반영 — 1막 = 궁금증 훅 · 멱등 · 이후 "n/N편" 장식과 합성
        var cues = new[] { new SubtitleCue(0, 5, "원래 오프닝"), new SubtitleCue(5, 10, "전개"), new SubtitleCue(10, 15, "마무리") };
        var applied = PlutoSeriesArcMemoryService.ApplyDirective(cues, directive);
        Assert.Equal(new[] { directive.RecommendedHookLine, "전개", "마무리" }, applied.Select(c => c.Text).ToArray());
        Assert.Equal((0, 5), (applied[0].StartSec, applied[0].EndSec));
        Assert.Equal(applied.Select(c => c.Text), PlutoSeriesArcMemoryService.ApplyDirective(applied, directive).Select(c => c.Text));
        Assert.Equal("3/3편 · " + directive.RecommendedHookLine, PlutoSeriesBatchPlanner.DecorateSubtitles(applied, 3, 3)[0].Text);
        Assert.Same(cues, PlutoSeriesArcMemoryService.ApplyDirective(cues, before));                  // 이탈 없음 → 원본 그대로
        Assert.Same(cues, PlutoSeriesArcMemoryService.ApplyDirective(cues, null));

        // 측정된 편은 단순 재렌더로 덮지 않는다 · 미측정 편은 최신 렌더로 갱신
        _now = T0.AddHours(1);
        await catalog.RecordRenderedClipAsync(Clip("summer_ep1_v2", "우리 여름 1/3편 — 시작", "시리즈 1/3편", Vintage, "MusicBox", T0.AddHours(1)));
        await catalog.RecordRenderedClipAsync(Clip("summer_ep3", "우리 여름 3/3편 — 완결", "시리즈 3/3편", Vintage, "Acoustic", T0.AddHours(2)));
        Assert.Equal(1, await arc.SyncFromCatalogAsync());
        var eps = Assert.Single(arc.GetSeriesArcs()).Episodes;
        Assert.Equal(new[] { 1, 2, 3 }, eps.Select(e => e.EpisodeNumber).ToArray());
        Assert.Equal(("summer_ep1", Golden, true), (eps[0].VideoId, eps[0].ColorLookId, eps[0].IsMeasured));
        Assert.Equal(("summer_ep3", Vintage, false), (eps[2].VideoId, eps[2].ColorLookId, eps[2].IsMeasured));
        await catalog.RecordRenderedClipAsync(Clip("summer_ep3_v2", "우리 여름 3/3편 — 완결", "시리즈 3/3편", Golden, "Lullaby", T0.AddHours(3)));
        Assert.Equal(1, await arc.SyncFromCatalogAsync());
        Assert.Equal(("summer_ep3_v2", Golden), (arc.GetSeriesArcs()[0].Episodes[2].VideoId, arc.GetSeriesArcs()[0].Episodes[2].ColorLookId));

        // 영속 — 카탈로그 없이(100건 상한에서 밀려난 뒤에도) 아크는 그대로
        using (var reloaded = Arc(null))
        {
            await reloaded.EnsureLoadedAsync();
            var again = Assert.Single(reloaded.GetSeriesArcs());
            Assert.Equal((2, true, 2, -17.5), (again.MeasuredCount, again.HasDropOff, again.EscalationLevel, again.EpisodeRetentionDelta));
            Assert.Equal(directive.RecommendedHookLine, reloaded.GetContinuityDirective("우리 여름", 3, 3).RecommendedHookLine);
            var status = reloaded.GetStatus();
            Assert.Equal((1, 2, 1), (status.TrackedSeriesCount, status.MeasuredEpisodeCount, status.DropOffSeriesCount));
            Assert.Equal("'우리 여름' 2/3편 측정 · 1→2편 −17.5p · 기울기 −17.5p/편 · 📉 이탈 → 훅 에스컬레이션 Lv2 · 앵커 " + Golden + " / Lullaby", PlutoSeriesArcMemoryService.BuildArcLabel(status));
        }

        // 🗃️ Step 9~15 카탈로그 JSON(신규 필드 없음)에서 그대로 유도 — 스키마 변경 불필요
        var legacyCatalogPath = Path.Combine(_dir, "legacy_catalog.json");
        File.WriteAllText(legacyCatalogPath, "{\"Version\":1,\"Clips\":["
            + "{\"VideoId\":\"winter_ep2\",\"Title\":\"겨울 이야기 2/2편 — 완결\",\"RenderMode\":\"시리즈 2/2편\",\"HookStyle\":\"h2\",\"ColorLookId\":\"" + Vintage + "\",\"BgmMood\":\"none\",\"Tags\":[],\"OutputPath\":\"\",\"RenderedAtUtc\":\"2026-11-02T00:00:00+00:00\",\"TelemetryLogged\":true},"
            + "{\"VideoId\":\"winter_ep1\",\"Title\":\"겨울 이야기 1/2편 — 시작\",\"RenderMode\":\"시리즈 1/2편\",\"HookStyle\":\"h1\",\"ColorLookId\":\"" + Vintage + "\",\"BgmMood\":\"MusicBox\",\"Tags\":[],\"OutputPath\":\"\",\"RenderedAtUtc\":\"2026-11-01T00:00:00+00:00\",\"TelemetryLogged\":true}]}");
        using var legacyCatalog = new PlutoRenderCatalogService(legacyCatalogPath);
        using var legacyArc = Arc(legacyCatalog, null, "legacy_arc.json");
        Assert.Equal(2, await legacyArc.SyncFromCatalogAsync());
        var winter = Assert.Single(legacyArc.GetSeriesArcs());
        Assert.Equal((PlutoSeriesArcMemoryService.SeriesKeyFor("겨울 이야기", 2), "겨울 이야기", 2), (winter.SeriesKey, winter.SeriesTitle, winter.TotalEpisodes));
        Assert.Equal((Vintage, "MusicBox"), (winter.AnchorColorLookId, winter.AnchorBgmMood));       // 'none' 은 BGM 앵커가 아니다
        Assert.StartsWith("'겨울 이야기' 0/2편 측정 · 편 간 비교 대기", PlutoSeriesArcMemoryService.BuildArcLabel(legacyArc.GetStatus()), StringComparison.Ordinal);
        Assert.StartsWith("기록 없음", PlutoSeriesArcMemoryService.BuildArcLabel(PlutoSeriesArcStatus.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriesArc_EscalationScalesWithConsecutiveDrops_SkipsHookWhenOverheated_RespectsMistakeGuards_AndStaysQuietWhenRetentionHolds()
    {
        static PlutoSeriesEpisodeStat Ep(int n, double hook, double completion, string look = Golden, string bgm = "Lullaby", bool measured = true)
            => new(n, "v" + n, "h" + n, look, bgm, measured, hook, completion, 4);

        // 연속 이탈(−10p · −10p) → Lv2 + 1 = Lv3 · 5편 중 4편 지시 → 궁금증 훅 + 클리프행어 아웃트로
        var sliding = PlutoSeriesArcMemoryService.Analyze("k", "연속 하락", 5, new[] { Ep(3, 50, 40), Ep(1, 70, 60), Ep(2, 60, 50) });
        Assert.Equal(new[] { 1, 2, 3 }, sliding.Episodes.Select(e => e.EpisodeNumber).ToArray());    // 입력 순서 무관
        Assert.Equal(new[] { -10.0, -10.0 }, sliding.EpisodeRetentionDeltas.Select(d => d.DeltaPts).ToArray());
        Assert.Equal((true, 3, -10.0), (sliding.HasDropOff, sliding.EscalationLevel, sliding.RetentionSlope));
        var d4 = PlutoSeriesArcMemoryService.BuildDirective(sliding, 4);
        Assert.Equal((PlutoSeriesArcMemoryService.CuriosityGapHooks[2], PlutoSeriesArcMemoryService.CliffhangerOutros[2]), (d4.RecommendedHookLine, d4.CliffhangerOutroLine));
        var cues = new[] { new SubtitleCue(0, 5, "오프닝"), new SubtitleCue(5, 15, "엔딩") };
        var applied = PlutoSeriesArcMemoryService.ApplyDirective(cues, d4);
        Assert.Equal(d4.RecommendedHookLine, applied[0].Text);
        Assert.Equal("엔딩" + PlutoSeriesArcMemoryService.OutroJoiner + d4.CliffhangerOutroLine, applied[1].Text);
        Assert.Equal(applied[1].Text, PlutoSeriesArcMemoryService.ApplyDirective(applied, d4)[1].Text);          // 멱등(아웃트로 중복 없음)

        // 낙폭 경계 — 5p 미만 무시 · 5p Lv1 · 20p Lv3
        Assert.False(PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 50, 40), Ep(2, 46, 36) }).HasDropOff);
        Assert.Equal(1, PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 50, 40), Ep(2, 45, 35) }).EscalationLevel);
        Assert.Equal(3, PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 70, 60), Ep(2, 50, 40) }).EscalationLevel);

        // 🎣 직전 편이 이미 클릭베이트(3초 80 · 완주 10) — 훅을 더 올리지 않고 아웃트로만(완주 축 이탈)
        var overheated = PlutoSeriesArcMemoryService.Analyze("k", "과열", 4, new[] { Ep(1, 70, 40), Ep(2, 80, 10) });
        Assert.Equal((true, true, PlutoSeriesDropAxis.Completion, 2), (overheated.HasDropOff, overheated.HookOverheated, overheated.DropAxis, overheated.EscalationLevel));
        var d3 = PlutoSeriesArcMemoryService.BuildDirective(overheated, 3);
        Assert.Equal((string.Empty, PlutoSeriesArcMemoryService.CliffhangerOutros[1]), (d3.RecommendedHookLine, d3.CliffhangerOutroLine));
        Assert.Contains("훅 과열 — 클리프행어 아웃트로만", d3.ReasonKorean, StringComparison.Ordinal);
        Assert.Equal("오프닝", PlutoSeriesArcMemoryService.ApplyDirective(cues, d3)[0].Text);          // 오프닝은 그대로
        Assert.Equal(PlutoSeriesDropAxis.HookRetention, PlutoSeriesArcMemoryService.Analyze("k", "t", 3, new[] { Ep(1, 60, 40), Ep(2, 40, 42) }).DropAxis);

        // 회복(55 → 45 → 47 · 기울기 −4) → 이탈 아님 · 3편 연속 하락 추세(기울기 ≤ −5)는 최근 변화가 작아도 이탈
        var recovered = PlutoSeriesArcMemoryService.Analyze("k", "회복", 4, new[] { Ep(1, 60, 50), Ep(2, 50, 40), Ep(3, 52, 42) });
        Assert.Equal((false, 0, -4.0, 2.0), (recovered.HasDropOff, recovered.EscalationLevel, recovered.RetentionSlope, recovered.EpisodeRetentionDelta));
        var quiet = PlutoSeriesArcMemoryService.BuildDirective(recovered, 4);
        Assert.Equal((false, true, string.Empty, string.Empty), (quiet.EscalateHook, quiet.HasAnchor, quiet.RecommendedHookLine, quiet.CliffhangerOutroLine));
        Assert.StartsWith("✅ 편 간 유지 양호(+2p)", quiet.ReasonKorean, StringComparison.Ordinal);
        Assert.True(PlutoSeriesArcMemoryService.Analyze("k", "추세", 4, new[] { Ep(1, 60, 50), Ep(2, 48, 38), Ep(3, 45, 35) }).HasDropOff);
        // 미측정 편은 추세에서 제외 · beforeEpisode 는 그 편 이전만 본다
        var partial = PlutoSeriesArcMemoryService.Analyze("k", "부분", 3, new[] { Ep(1, 60, 50), Ep(2, 0, 0, measured: false), Ep(3, 40, 30) }, default, 3);
        Assert.Equal((1, false), (partial.MeasuredCount, partial.HasDropOff));
        Assert.Same(PlutoSeriesContinuityDirective.None, PlutoSeriesArcMemoryService.BuildDirective(PlutoSeriesArcMemoryService.Analyze("k", "빈", 3, Array.Empty<PlutoSeriesEpisodeStat>()), 2));

        // 앵커 = 앞선 편 최빈값(동률 → 가장 앞선 편) · custom/none BGM 제외
        var motifs = new[] { Ep(1, 60, 50, Golden, "custom"), Ep(2, 55, 45, Vintage, "MusicBox"), Ep(3, 50, 40, Vintage, "Lullaby") };
        Assert.Equal(Vintage, PlutoSeriesArcMemoryService.AnchorOf(motifs, 0, bgm: false));
        Assert.Equal(Golden, PlutoSeriesArcMemoryService.AnchorOf(motifs, 3, bgm: false));            // 1 : 1 동률 → 1편
        Assert.Equal("MusicBox", PlutoSeriesArcMemoryService.AnchorOf(motifs, 0, bgm: true));
        Assert.Equal(string.Empty, PlutoSeriesArcMemoryService.AnchorOf(motifs, 2, bgm: true));

        // 🛡️ 오답 노트가 차단한 문구는 낮은 단계로 대체 · 전부 차단 → 생략
        var twoDrop = PlutoSeriesArcMemoryService.Analyze("k", "t", 4, new[] { Ep(1, 60, 50), Ep(2, 45, 40) });
        Assert.Equal(2, twoDrop.EscalationLevel);
        var blockedOne = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PlutoSeriesArcMemoryService.CuriosityGapHooks[1] };
        Assert.Equal(PlutoSeriesArcMemoryService.CuriosityGapHooks[0], PlutoSeriesArcMemoryService.BuildDirective(twoDrop, 3, blockedOne).RecommendedHookLine);
        var blockedAll = new HashSet<string>(PlutoSeriesArcMemoryService.CuriosityGapHooks, StringComparer.OrdinalIgnoreCase);
        var noHook = PlutoSeriesArcMemoryService.BuildDirective(twoDrop, 3, blockedAll);
        Assert.Equal((string.Empty, PlutoSeriesArcMemoryService.CliffhangerOutros[1]), (noHook.RecommendedHookLine, noHook.CliffhangerOutroLine));
        // 서비스 경유 — 주입된 오답 노트의 차단 토큰을 그대로 존중
        var notebook = Notebook("nb_guard.json");
        await notebook.RecordNegativeFeedbackAsync(new[] { PlutoSeriesArcMemoryService.CuriosityGapHooks[1] }, PlutoMistakeNotebookService.BlockThreshold + 0.2, "test");
        using var catalog = Catalog("guard_catalog.json");
        await catalog.RecordRenderedClipAsync(Clip("g1", "가드 1/4편 — 시작", "시리즈 1/4편", Golden, "Lullaby", T0));
        await catalog.RecordRenderedClipAsync(Clip("g2", "가드 2/4편 — 전개", "시리즈 2/4편", Golden, "Lullaby", T0));
        using var arc = Arc(catalog, notebook, "guard_arc.json");
        Assert.True(await arc.RecordEpisodePerformanceAsync(Video("h", Golden, "Lullaby", "", 60, 50, 4, "g1")));
        Assert.True(await arc.RecordEpisodePerformanceAsync(Video("h", Golden, "Lullaby", "", 45, 40, 4, "g2")));
        var guarded = arc.GetContinuityDirective("가드", 4, 3);
        Assert.Equal((2, PlutoSeriesArcMemoryService.CuriosityGapHooks[0], PlutoSeriesArcMemoryService.CliffhangerOutros[1]), (guarded.EscalationLevel, guarded.RecommendedHookLine, guarded.CliffhangerOutroLine));
        // 카탈로그 조회가 안 돼도 텔레메트리 RenderMode 로 편 판정([부분 구제])
        using var loose = Arc(null, null, "loose_arc.json");
        Assert.True(await loose.RecordEpisodePerformanceAsync(Video("h", Golden, "Lullaby", "", 50, 40, 4, "봄나들이 1/2편", "시리즈 1/2편")));
        Assert.Equal("봄나들이", Assert.Single(loose.GetSeriesArcs()).SeriesTitle);
    }

    // ── ④ ⏱️ 회로 차단 — 냉각 만료 자동 반개방 전이 · 상태 통지 구동 카운트다운 · 유휴 시 타이머 없음 ──

    [Fact]
    public async Task CircuitBreaker_AutoPromotesExpiredOpenToHalfOpen_AndCountdownTimerRunsOnlyWhileTripped_ThenSelfTerminates()
    {
        // 주입 시계 — 타이머 스레드가 읽으므로 틱(long)을 원자적으로 갱신
        var nowTicks = T0.Ticks;
        void Advance(TimeSpan by) => System.Threading.Interlocked.Add(ref nowTicks, by.Ticks);
        var breaker = new ProviderCircuitBreaker { Clock = () => new DateTimeOffset(System.Threading.Volatile.Read(ref nowTicks), TimeSpan.Zero) };
        var events = 0;
        breaker.StateChanged += () => System.Threading.Interlocked.Increment(ref events);

        // 순수 전이 — PromoteExpired 는 호출(TryAcquire) 없이 Open → HalfOpen · 탐침 슬롯은 비워 둔다
        Assert.False(breaker.HasTrippedCircuits);
        Assert.Equal(0, breaker.PromoteExpired());
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
        Assert.True(breaker.HasTrippedCircuits);
        Assert.Equal(1, events);
        Advance(ProviderCircuitBreaker.BaseCooldown - TimeSpan.FromSeconds(1));
        Assert.Equal(0, breaker.PromoteExpired());                                                    // 냉각 1초 남음
        Assert.Equal(TimeSpan.FromSeconds(1), breaker.GetSnapshot("Wan").RetryAfter);
        Assert.Contains("차단(약 1초 후 탐침)", breaker.GetSnapshot("Wan").ToLabel(), StringComparison.Ordinal);
        Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, breaker.PromoteExpired());
        Assert.Equal(2, events);
        var half = breaker.GetSnapshot("Wan");
        Assert.Equal((ProviderCircuitState.HalfOpen, false), (half.State, half.ProbeInFlight));
        Assert.Contains("반개방(다음 렌더가 복구 탐침)", half.ToLabel(), StringComparison.Ordinal);
        Assert.Equal(0, breaker.PromoteExpired());                                                    // 멱등 · 통지 없음
        Assert.Equal(2, events);
        Assert.True(breaker.TryAcquire("Wan", out _));                                                // 다음 호출 1건이 탐침
        Assert.True(breaker.GetSnapshot("Wan").ProbeInFlight);
        Assert.Contains("반개방(탐침 중)", breaker.GetSnapshot("Wan").ToLabel(), StringComparison.Ordinal);
        Assert.False(breaker.TryAcquire("Wan", out _));
        breaker.RecordSuccess("Wan");
        Assert.False(breaker.HasTrippedCircuits);
        Assert.Equal(3, events);

        // ⏱️ 카운트다운 구동기 — 정상 상태에서는 타이머가 아예 없다
        var interval = TimeSpan.FromMilliseconds(20);
        var ticks = new List<IReadOnlyList<ProviderCircuitSnapshot>>();
        var countdown = new ProviderCircuitCountdown(breaker, rows => { lock (ticks) ticks.Add(rows); }, interval);
        await using (countdown)
        {
            Assert.False(countdown.IsRunning);
            await Task.Delay(120);
            Assert.Equal((0, 0, 0), (countdown.TimerStarts, countdown.TickCount, countdown.NotifyCount));

            // 차단 → 상태 통지만으로 타이머 시작 · 남은 냉각이 틱마다 갱신
            for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
            Assert.True(countdown.IsRunning);
            Assert.Equal(1, countdown.TimerStarts);
            Assert.True(await WaitUntilAsync(() => countdown.NotifyCount >= 2));
            Advance(TimeSpan.FromSeconds(10));
            Assert.True(await WaitUntilAsync(() => { lock (ticks) return ticks.Count > 0 && ticks[^1].Count == 1 && ticks[^1][0].RetryAfter == TimeSpan.FromSeconds(20); }));
            Assert.Equal(ProviderCircuitState.Open, breaker.GetSnapshot("Wan").State);

            // 냉각 만료 → 렌더 호출 · UI 클릭 없이 구동기가 HalfOpen 으로 전이
            var eventsBefore = System.Threading.Volatile.Read(ref events);
            Advance(TimeSpan.FromSeconds(20));
            Assert.True(await WaitUntilAsync(() => breaker.GetSnapshot("Wan").State == ProviderCircuitState.HalfOpen));
            Assert.True(await WaitUntilAsync(() => System.Threading.Volatile.Read(ref events) == eventsBefore + 1));
            Assert.True(countdown.IsRunning);                                                         // HalfOpen 동안은 계속 구동(재차단 대비)
            Assert.True(await WaitUntilAsync(() => { lock (ticks) return ticks.Count > 0 && ticks[^1].Count == 1 && ticks[^1][0].State == ProviderCircuitState.HalfOpen; }));
            // HalfOpen 대기 중에는 줄어들 숫자가 없다 → 틱은 돌지만 UI 통지는 멈춘다
            await Task.Delay(60);
            var notified = countdown.NotifyCount;
            var ticked = countdown.TickCount;
            Assert.True(await WaitUntilAsync(() => countdown.TickCount >= ticked + 3));
            Assert.Equal(notified, countdown.NotifyCount);

            // 탐침 성공 → 전부 Closed → 즉시 정지 · 이후 틱 0
            Assert.True(breaker.TryAcquire("Wan", out _));
            breaker.RecordSuccess("Wan");
            Assert.False(countdown.IsRunning);
            await Task.Delay(60);
            var settled = countdown.TickCount;
            await Task.Delay(150);
            Assert.Equal(settled, countdown.TickCount);
            Assert.Equal(1, countdown.TimerStarts);

            // 재차단 → 새 타이머 · 수동 해제(Reset) → 다시 정지
            for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
            Assert.True(countdown.IsRunning);
            Assert.Equal(2, countdown.TimerStarts);
            Assert.True(breaker.Reset("Wan"));
            Assert.False(countdown.IsRunning);

            // 여러 프로바이더 — 하나라도 열려 있으면 유지 · 마지막 회로가 닫힐 때 정지
            for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) { breaker.RecordTransientFailure("Wan", "503"); breaker.RecordTransientFailure("Kling", "503"); }
            Assert.Equal(3, countdown.TimerStarts);
            breaker.Reset("Wan");
            Assert.True(countdown.IsRunning);
            breaker.Reset("Kling");
            Assert.False(countdown.IsRunning);
            for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
            Assert.True(countdown.IsRunning);
        }
        // 폐기 — 타이머 정지 + 구독 해제(이후 차단에도 재시작하지 않음) · 멱등
        Assert.False(countdown.IsRunning);
        var startsAtDispose = countdown.TimerStarts;
        breaker.Reset("Wan");
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
        Assert.False(countdown.IsRunning);
        Assert.Equal(startsAtDispose, countdown.TimerStarts);
        countdown.Dispose();
        await countdown.DisposeAsync();

        // 이미 차단 중인 차단기에 붙이면 즉시 구동 · 통지 핸들러 예외는 격리
        using (var late = new ProviderCircuitCountdown(breaker, _ => throw new InvalidOperationException("UI 예외"), interval))
        {
            Assert.True(late.IsRunning);
            Assert.True(await WaitUntilAsync(() => late.NotifyCount >= 2));
            Assert.True(late.IsRunning);
        }

        // Workspace 결선 — 구동기 1개 생성 + Dispose 1회 · 페이지에 상시 1초 타이머 없음 · 헤더 밖
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Equal(1, CountOf(ws, "new PromptFlowStudio.Services.Render.ProviderCircuitCountdown(_circuitBreaker, OnCircuitCountdownTick)"));
        Assert.Equal(1, CountOf(ws, "_circuitCountdown?.Dispose();"));
        Assert.True(ws.IndexOf("_circuitCountdown?.Dispose();", StringComparison.Ordinal) > ws.IndexOf("public void Dispose()", StringComparison.Ordinal));
        Assert.Contains("_ = InvokeAsync(StateHasChanged);", ws.Substring(ws.IndexOf("private void OnCircuitCountdownTick(", StringComparison.Ordinal), 500), StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodicTimer(TimeSpan.FromSeconds(1))", ws, StringComparison.Ordinal);
        var helper = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Services", "Render", "ProviderCircuitCountdown.cs"));
        Assert.Equal(1, CountOf(helper, "new PeriodicTimer("));
        Assert.Contains("_breaker.StateChanged += OnStateChanged;", helper, StringComparison.Ordinal);
        Assert.Contains("_breaker.StateChanged -= OnStateChanged;", helper, StringComparison.Ordinal);
    }

    // ── ⑤ 🧯 자율 추가 B — 반개방 탐침 임대 회수: 결과 보고가 누락돼도 회로가 영구 고착되지 않는다 ──

    [Fact]
    public void CircuitBreaker_ReclaimsLeakedHalfOpenProbeLease_SoAnUnreportedProbeCannotWedgeTheCircuitForever()
    {
        var now = new DateTimeOffset(T0);
        var breaker = new ProviderCircuitBreaker { Clock = () => now, ProbeLeaseTimeout = TimeSpan.FromMinutes(2) };
        Assert.Equal(TimeSpan.FromMinutes(10), new ProviderCircuitBreaker().ProbeLeaseTimeout);      // 기본 = 최대 냉각의 2배
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.True(breaker.TryAcquire("Wan", out _));                                                // 탐침 발급 — 호출자가 결과를 보고하지 않고 사라짐

        // 임대 시간 안에는 기존 동작 그대로(탐침 1건만)
        now += TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1);
        Assert.False(breaker.TryAcquire("Wan", out var wait));
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal((ProviderCircuitState.HalfOpen, 0), (breaker.GetSnapshot("Wan").State, breaker.GetSnapshot("Wan").ReclaimedProbeLeases));

        // 임대 만료 → 다음 호출이 슬롯을 회수해 새 탐침(이전에는 수동 [차단 해제] 전까지 모든 호출이 즉시 실패)
        now += TimeSpan.FromSeconds(1);
        Assert.True(breaker.TryAcquire("Wan", out _));
        Assert.Equal(1, breaker.GetSnapshot("Wan").ReclaimedProbeLeases);
        Assert.False(breaker.TryAcquire("Wan", out _));                                               // 새 임대도 1건만
        breaker.RecordSuccess("Wan");
        Assert.Equal(ProviderCircuitState.Closed, breaker.GetSnapshot("Wan").State);
        Assert.True(breaker.TryAcquire("Wan", out _));

        // 자동 승격(PromoteExpired) 뒤의 탐침도 같은 임대 규칙 · 정상 보고(취소 반납)는 회수로 세지 않는다
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure("Wan", "503");
        now += ProviderCircuitBreaker.BaseCooldown;
        Assert.Equal(1, breaker.PromoteExpired());
        Assert.True(breaker.TryAcquire("Wan", out _));
        breaker.RecordAbandoned("Wan");
        Assert.True(breaker.TryAcquire("Wan", out _));
        Assert.Equal(1, breaker.GetSnapshot("Wan").ReclaimedProbeLeases);
        now += TimeSpan.FromMinutes(3);
        Assert.True(breaker.TryAcquire("Wan", out _));
        Assert.Equal(2, breaker.GetSnapshot("Wan").ReclaimedProbeLeases);
        // 회수된 탐침이 실패하면 지수 백오프는 그대로
        Assert.True(breaker.RecordTransientFailure("Wan", "504"));
        Assert.False(breaker.TryAcquire("Wan", out var doubled));
        Assert.Equal(ProviderCircuitBreaker.CooldownFor(1), doubled);

        // 근본 원인도 차단 — 렌더 엔진은 요청 구성(예외 가능)을 회로 허가 '이전'에 끝낸다
        var engine = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Services", "Render", "VideoEngineRenderExecutionEngine.cs"));
        var build = engine.IndexOf("var request = BuildRequest(job);", StringComparison.Ordinal);
        var acquire = engine.IndexOf("breaker.TryAcquire(provider, out var retryAfter)", StringComparison.Ordinal);
        Assert.True(build > 0 && acquire > build);
        Assert.Equal(1, CountOf(engine, "var request = BuildRequest(job);"));
    }

    // ── ⑥ 🧠 두뇌 리포트 · Workspace — 전 모달리티 파레토 + 시리즈 아크 표면화 · 상단 3행 헤더 불변 ──

    [Fact]
    public async Task BrainSummaryAndWorkspace_SurfaceFullModalityParetoAndSeriesArc_InsideCollapsibleBody_WithoutBreakingHeaderInvariants()
    {
        var learning = Learning();
        var notebook = Notebook();
        using var catalog = Catalog();
        await catalog.RecordRenderedClipAsync(Clip("b1", "봄 산책 1/3편 — 시작", "시리즈 1/3편", Golden, "Lullaby", T0.AddHours(-2)));
        await catalog.RecordRenderedClipAsync(Clip("b2", "봄 산책 2/3편 — 전개", "시리즈 2/3편", Golden, "Lullaby", T0.AddHours(-1)));
        using var arc = Arc(catalog, notebook);
        using var closed = new PlutoClosedLoopReinforcementService(learning, notebook) { SeriesArc = arc };
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        // 근거 없음 — 결정론 안내 문구 · 기본값은 서비스 미연결과 동일
        var empty = await new PlutoAutonomousBrainSummaryService(null, ranker, notebook).GetSummaryAsync();
        Assert.StartsWith("근거 없음 · 훅 · 룩 · BGM · 태그별 성과 2건부터", empty.ParetoModalityLabel, StringComparison.Ordinal);
        Assert.Equal(0, empty.ParetoEvidencedModalityCount);
        Assert.Same(PlutoSeriesArcStatus.Empty, empty.SeriesArc);
        Assert.StartsWith("기록 없음", empty.SeriesArcLabel, StringComparison.Ordinal);
        Assert.Equal("훅 — · 룩 1/1 'A' · BGM — · 태그 —", PlutoParetoFrontier.BuildModalityLabel(new[]
        {
            new PlutoParetoModalityStatus(PlutoPatternKind.Hook, null, 0, 0, 0),
            new PlutoParetoModalityStatus(PlutoPatternKind.Look, new PlutoParetoLeader("A", default, 2, 1, 1, 0), 1, 1, 0),
            new PlutoParetoModalityStatus(PlutoPatternKind.BgmMood, null, 0, 0, 0), new PlutoParetoModalityStatus(PlutoPatternKind.StyleTag, null, 0, 0, 0),
        }));

        await SeedContrastAsync(closed);
        await closed.IngestPerformanceAsync(Video("1편 훅", Golden, "Lullaby", "", 60, 50, 5, "b1"));
        await closed.IngestPerformanceAsync(Video("2편 훅", Golden, "Lullaby", "", 58, 50, 5, "b2"));

        var summary = await new PlutoAutonomousBrainSummaryService(null, ranker, notebook) { SeriesArc = arc }.GetSummaryAsync();
        Assert.Equal(4, summary.ParetoModalities.Count);
        Assert.Equal(4, summary.ParetoEvidencedModalityCount);
        Assert.Equal(PlutoParetoFrontier.BuildModalityLabel(ranker.GetParetoModalityStatus()), summary.ParetoModalityLabel);
        Assert.Contains("BGM ", summary.ParetoModalityLabel, StringComparison.Ordinal);
        Assert.Contains("(🎣1)", summary.ParetoModalityLabel, StringComparison.Ordinal);
        Assert.Contains("태그 1/2(🎣1) '" + BalTag + "'", summary.ParetoModalityLabel, StringComparison.Ordinal);
        Assert.Equal(summary.ParetoLeader, summary.ParetoModalities[0].Leader);                       // 기존 '파레토 선두(훅)' 과 일관
        Assert.Equal((1, 2, 0), (summary.SeriesArc.TrackedSeriesCount, summary.SeriesArc.MeasuredEpisodeCount, summary.SeriesArc.DropOffSeriesCount));
        Assert.Equal("'봄 산책' 2/3편 측정 · 1→2편 −1p · 기울기 −1p/편 · ✅ 유지 · 앵커 " + Golden + " / Lullaby", summary.SeriesArcLabel);
        // 아크 소스 실패는 격리([부분 구제] 나머지 리포트 유지)
        var faulty = await new PlutoAutonomousBrainSummaryService(null, ranker, notebook) { SeriesArc = new ThrowingArc() }.GetSummaryAsync();
        Assert.Same(PlutoSeriesArcStatus.Empty, faulty.SeriesArc);
        Assert.Equal(summary.ParetoModalityLabel, faulty.ParetoModalityLabel);

        // Workspace — 접이식 두뇌 리포트 본문에만 · 상단 3행 헤더 불변 · 제한 키워드 없음
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var strip = ws.IndexOf("<div class=\"ws-brain-strip\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(strip > 0 && tools > strip);
        var body = ws.Substring(strip, tools - strip);
        var open = body.IndexOf("_brainOpen && _brainSummary is", StringComparison.Ordinal);
        foreach (var marker in new[] { "data-brain-pareto-modality=", "data-brain-series-arc=" })
        {
            Assert.Equal(1, CountOf(ws, marker));
            Assert.True(body.IndexOf(marker, StringComparison.Ordinal) > open);
        }
        Assert.Contains("@(bs.ParetoModalityLabel)", body, StringComparison.Ordinal);
        Assert.Contains("@(bs.SeriesArcLabel)", body, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(ws, "data-brain-pareto="));                                           // Step 15 훅 선두 배지는 그대로 1개
        Assert.Equal(3, CountOf(ws, "data-ws-row=\""));
        var head = ws.Substring(0, strip);
        foreach (var keyword in new[]
                 {
                     "전 모달리티", "시리즈 아크", "data-brain-pareto-modality", "data-brain-series-arc", "data-series-continuity", "SeriesArc", "ParetoModality",
                     "ProviderCircuitCountdown", "카운트다운", "클리프행어", "파레토", "회로 차단",
                 })
            Assert.DoesNotContain(keyword, head, StringComparison.Ordinal);

        // 시리즈 편 조립 결선 — 연속성 지시 조회 → 앵커 룩/BGM 유지 → 1막 훅/아웃트로 반영(장식 이전) · 표시 배지는 도구 행 아래
        Assert.Equal(1, CountOf(ws, "arc.GetContinuityDirectiveAsync(s.SeriesTheme, s.Episodes.Count, ep.EpisodeNumber, _mediaCts.Token)"));
        Assert.Equal(1, CountOf(ws, "var directive = await ResolveSeriesContinuityAsync(ep);"));
        Assert.Equal(2, CountOf(ws, "ApplySeriesContinuityAnchors(directive);"));                       // Step 18 — 편 불러오기 + 새 시즌 분리(재조립 없이 앵커만 교체)
        // Step 18 — 덧입힘(ApplyDirective) → 장식(DecorateSubtitles) 순서는 순수 합성기(PlutoSeriesSubtitleOverlay.Compose)가 보장 · 페이지는 원문 포착 후 재합성만
        var decorate = ws.IndexOf("private void ApplySeriesCueDecoration()", StringComparison.Ordinal);
        Assert.True(ws.IndexOf("PromptFlowStudio.Services.Pluto.PlutoSubtitleOverlayState.FromBase(cues);", decorate, StringComparison.Ordinal) > decorate);
        Assert.Equal(1, CountOf(ws, "PromptFlowStudio.Services.Pluto.PlutoSeriesSubtitleOverlay.Compose(st, ActiveSeriesDirective(), SeriesEscalationMode, _seriesActiveEpisode, s.Episodes.Count);"));
        Assert.Equal(1, CountOf(ws, "data-series-continuity="));
        Assert.True(ws.IndexOf("data-series-continuity=", StringComparison.Ordinal) > tools);

        // DI — 데스크톱 + 모바일 양쪽 싱글턴 1회 · 폐루프와 두뇌 리포트가 같은 인스턴스를 주입받는다
        foreach (var file in new[] { Path.Combine("PromptFlowStudio", "MainForm.cs"), Path.Combine("PromptFlowStudio.Mobile", "MauiProgram.cs") })
        {
            var di = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Equal(1, CountOf(di, "AddSingleton<PromptFlowStudio.Services.Pluto.IPlutoSeriesArcMemoryService>(sp => new PromptFlowStudio.Services.Pluto.PlutoSeriesArcMemoryService("));
            Assert.Equal(4, CountOf(di, "SeriesArc = sp.GetService(typeof(PromptFlowStudio.Services.Pluto.IPlutoSeriesArcMemoryService)) as PromptFlowStudio.Services.Pluto.IPlutoSeriesArcMemoryService"));   // + Step 26 주제 기획기 + Step 38 진단서 용어집
        }
        Assert.Equal("pluto_series_arc.json", PlutoSeriesArcMemoryService.FileName);
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);                                  // Step 17 — 아크가 핵심 허브(검증 · 미러 · 새 PC 복원)에 편입
    }

    /// <summary>항상 실패하는 아크 소스(두뇌 리포트 격리 검증용).</summary>
    private sealed class ThrowingArc : IPlutoSeriesArcMemoryService
    {
        public event Action? Changed { add { } remove { } }
        public Task EnsureLoadedAsync(System.Threading.CancellationToken ct = default) => throw new IOException("arc down");
        public Task<int> SyncFromCatalogAsync(System.Threading.CancellationToken ct = default) => throw new IOException("arc down");
        public Task<bool> RecordEpisodePerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, System.Threading.CancellationToken ct = default) => throw new IOException("arc down");
        public IReadOnlyList<PlutoSeriesArcReport> GetSeriesArcs(int maxCount = 8) => throw new IOException("arc down");
        public PlutoSeriesContinuityDirective GetContinuityDirective(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber) => throw new IOException("arc down");
        public Task<PlutoSeriesContinuityDirective> GetContinuityDirectiveAsync(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber, System.Threading.CancellationToken ct = default) => throw new IOException("arc down");
        public PlutoSeriesArcStatus GetStatus() => throw new IOException("arc down");
    }

    [Fact]
    public async Task ClosedLoop_KeepsReinforcementOutcome_WhenSeriesArcSourceFails()
    {
        var learning = Learning();
        using var closed = new PlutoClosedLoopReinforcementService(learning, Notebook()) { SeriesArc = new ThrowingArc() };
        var outcome = await closed.IngestPerformanceAsync(Video("훅", Golden, "Lullaby", BalTag, 70, 60, 8));
        Assert.True(outcome.IsBreakoutSuccess && !outcome.AlreadyRegistered);                         // [부분 구제] 강화 결과 유지
        Assert.Equal(1.0, learning.GetPatternObjectives("bgm:Lullaby").Samples, 9);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
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
