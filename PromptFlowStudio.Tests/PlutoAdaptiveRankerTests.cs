using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🧠 Pluto 4.2.1 — 폐루프 강화 가중치 → 훅/룩/태그 순방향 랭킹(읽기 경로 폐루프).</summary>
public sealed class PlutoAdaptiveRankerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto421_" + Guid.NewGuid().ToString("N"));

    public PlutoAdaptiveRankerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoDriveStorageManager Storage() => new(null, Path.Combine(_dir, "vault"));

    private static PlutoVideoPerformanceTelemetry Telemetry(string id, string hookStyle, string look, string bgm, string[] tags, double hook, double completion, double ctr)
        => new(id, "YouTube Shorts", hookStyle, look, bgm, tags, hook, completion, ctr, DateTimeOffset.UtcNow);

    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => "adaptive" + i.ToString(CultureInfo.InvariantCulture) + ".jpg").ToArray();

    [Fact]
    public async Task EndToEndClosedLoop_BreakoutTelemetry_PromotesHookAndLookToRank1()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook, Storage());
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        await ranker.EnsureLoadedAsync();

        // 학습 이력 없음 → 기본 후보 순서(사전 선호) 유지 · ✨ 기본 탐색
        var cold = ranker.GetCurrentRecommendations();
        Assert.False(cold.HasReinforcedHistory);
        Assert.Equal(PlutoColorGradingCompiler.NaturalPastelName, cold.TopColorLook.DisplayLabel);
        Assert.Equal(PlutoMontageStoryGenerator.AllTemplateHookLines[0], cold.TopHook.DisplayLabel);
        Assert.Equal("✨ 기본 탐색", cold.TopHook.ReasonBadge);
        Assert.Empty(cold.TopTags);

        const string hook = "0.5초 휩줌 훅";
        var r = await loop.IngestPerformanceAsync(Telemetry("vid-break", hook, PlutoColorGradingCompiler.VintageFilm16mmName, "Lullaby", new[] { "#첫걸음", "#babyreels" }, 82, 61, 9.4));
        Assert.True(r.IsBreakoutSuccess);

        var rec = ranker.GetCurrentRecommendations();
        Assert.True(rec.HasReinforcedHistory);
        Assert.Equal(hook, rec.TopHook.DisplayLabel);                                         // 기본 후보 밖 학습 훅도 1위
        Assert.Equal("hook:" + hook, rec.TopHook.Key);                                       // 강화 서비스와 동일 키
        Assert.Equal(PlutoColorGradingCompiler.VintageFilm16mmName, rec.TopColorLook.DisplayLabel);
        Assert.Equal("Lullaby", rec.TopBgmMood.DisplayLabel);
        Assert.Equal(r.PatternWeightMultiplier, rec.TopHook.LearnedWeightMultiplier, 9);
        Assert.Equal(r.PatternWeightMultiplier, rec.TopColorLook.LearnedWeightMultiplier, 9);
        Assert.StartsWith("🔥 학습 ×" + r.PatternWeightMultiplier.ToString("0.00", CultureInfo.InvariantCulture), rec.TopHook.ReasonBadge, StringComparison.Ordinal);
        Assert.Equal(0.0, rec.TopHook.MistakePenaltyFactor);
        Assert.Contains(rec.TopTags, t => t.DisplayLabel == "#babyreels" && t.IsReinforced);

        // 명시 후보에서도 강화 후보가 사전 선호(1순위 후보)를 앞섬
        var looks = ranker.RankOptions(PlutoPatternKind.Look, new[] { PlutoColorGradingCompiler.NaturalPastelName, PlutoColorGradingCompiler.GoldenHourWarmName, PlutoColorGradingCompiler.VintageFilm16mmName });
        Assert.Equal(PlutoColorGradingCompiler.VintageFilm16mmName, looks[0].DisplayLabel);
        Assert.Equal(PlutoColorGradingCompiler.NaturalPastelName, looks[1].DisplayLabel);
        Assert.Equal(PlutoAdaptivePatternRanker.ComputeFinalScore(looks[0].BaseScore, looks[0].LearnedWeightMultiplier, 0, looks[0].Key), looks[0].FinalScore, 12);

        // 태그 정규화 — '#' 유무 무관하게 학습 가중치 적용 · 상한 개수
        var tags = ranker.RankTags(new[] { "#plain", "#other", "babyreels" }, 2);
        Assert.Equal(new[] { "babyreels", "#plain" }, tags);

        // 재적재(새 인스턴스) 후에도 동일 랭킹 — 영속 가중치를 읽음
        using var reloaded = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        var ranker2 = new PlutoAdaptivePatternRanker(reloaded, notebook);
        await ranker2.EnsureLoadedAsync();
        Assert.Equal(hook, ranker2.GetCurrentRecommendations().TopHook.DisplayLabel);
    }

    [Fact]
    public async Task EndToEndClosedLoop_UnderperformingTelemetry_DemotesHookBelowHealthyAlternative()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook, Storage());
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        const string weak = "느린 풍경 인트로";
        var r = await loop.IngestPerformanceAsync(Telemetry("vid-poor", weak, PlutoColorGradingCompiler.NaturalPastelName, "none", new[] { "#slowburn" }, 18, 20, 0.9));
        Assert.True(r.RecordedMistakePenalty);
        Assert.False(r.IsBreakoutSuccess);

        await ranker.EnsureLoadedAsync();
        var ranked = ranker.RankOptions(PlutoPatternKind.Hook, new[] { weak, "클린 훅 A", "클린 훅 B" });   // 약한 훅이 사전 선호 1순위여도
        Assert.Equal(3, ranked.Count);
        Assert.Equal(weak, ranked[^1].DisplayLabel);
        var demoted = ranked[^1];
        Assert.True(demoted.IsPenalized);
        Assert.InRange(demoted.MistakePenaltyFactor, PlutoClosedLoopReinforcementService.MinPenalty / PlutoAdaptivePatternRanker.PenaltyNormalizer - 1e-6, double.MaxValue);
        Assert.StartsWith("⚠️ 오답 감점", demoted.ReasonBadge, StringComparison.Ordinal);
        Assert.True(demoted.FinalScore < ranked[0].FinalScore * 0.6);
        Assert.Equal("✨ 기본 탐색", ranked[0].ReasonBadge);
        // 감점 상한 0.85 — 점수 하한 BaseScore × 0.15
        Assert.True(demoted.FinalScore >= demoted.BaseScore * (1 - PlutoAdaptivePatternRanker.MaxPenaltyFactor) - 1e-9);

        var rec = ranker.GetCurrentRecommendations(candidateHooks: new[] { weak, "클린 훅 A" });
        Assert.Equal("클린 훅 A", rec.TopHook.DisplayLabel);
        Assert.False(rec.HasReinforcedHistory);

        Assert.Equal(new[] { "#fresh", "#slowburn" }, ranker.RankTags(new[] { "#slowburn", "#fresh" }));
    }

    [Fact]
    public async Task StoryGeneratorAndPackager_ConsumeRankedHookAndTags()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook, Storage());
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);

        var everydayHooks = PlutoMontageStoryGenerator.TemplateHookLines(null);
        Assert.True(everydayHooks.Count >= 2);
        var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(3), null, null, null);
        var baseline = PlutoMontageStoryGenerator.GenerateLocal(plan)[0].Text;
        var target = everydayHooks[0] == baseline ? everydayHooks[1] : everydayHooks[0];   // 학습 전 시드 선택과 다른 줄을 강화

        var r = await loop.IngestPerformanceAsync(Telemetry("vid-story", target, PlutoColorGradingCompiler.GoldenHourWarmName, "MusicBox", new[] { "#cozymood" }, 88, 70, 11));
        Assert.True(r.IsBreakoutSuccess);

        // 🎬 템플릿 Act1 = 강화된 훅 줄 · LLM 프롬프트에 검증 훅 + 강화 태그
        Assert.Equal(target, PlutoMontageStoryGenerator.GenerateLocal(plan, null, null, ranker)[0].Text);
        var llm = new CapturingLlm();
        var gen = new PlutoMontageStoryGenerator(llm, notebook, ranker);
        var cues = await gen.GenerateThreeActSubtitlesAsync(plan);
        Assert.Equal(3, cues.Count);
        Assert.Equal(target, cues[0].Text);
        Assert.False(gen.LastUsedLlm);
        Assert.Contains(PlutoMontageStoryGenerator.ProvenHookPrefix + target, llm.LastUser, StringComparison.Ordinal);
        Assert.Contains(PlutoMontageStoryGenerator.BoostedTagsPrefix + "#cozymood", llm.LastUser, StringComparison.Ordinal);
        // 랭커 미주입 → 기존 동작(하위 호환)
        Assert.Equal(baseline, (await new PlutoMontageStoryGenerator(null, notebook).GenerateThreeActSubtitlesAsync(plan))[0].Text);

        // 📱 패키저 — 강화 태그 선두(활성 태그 밖에서도 합류) · 강화 훅 프레이밍 첫 제목 · LLM 프롬프트 반영
        var now = new DateTime(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);
        var pllm = new CapturingLlm();
        var packager = new PlutoMultiFormatPackager(pllm, notebook, () => now, ranker);
        var kit = await packager.GeneratePublishingKitAsync("일상 브이로그", new[] { "#daily", "#fyp" });
        Assert.False(kit.UsedLlm);
        Assert.Equal("#cozymood", kit.Hashtags[0]);
        Assert.Equal("🔥 " + target, kit.Titles[0]);
        Assert.Equal(PlutoMultiFormatPackager.TitleCount, kit.Titles.Count);
        Assert.Contains("Active trend tags: #cozymood", pllm.LastUser, StringComparison.Ordinal);
        Assert.Contains(PlutoMultiFormatPackager.ProvenHookPrefix + target, pllm.LastUser, StringComparison.Ordinal);

        var plain = await new PlutoMultiFormatPackager(null, notebook, () => now).GeneratePublishingKitAsync("일상 브이로그", new[] { "#daily", "#fyp" });
        Assert.Equal("#daily", plain.Hashtags[0]);
        Assert.DoesNotContain(plain.Titles, t => t.StartsWith("🔥 ", StringComparison.Ordinal));
    }

    private static readonly string[] PresetTracks = Enum.GetNames<PromptFlowStudio.Services.Export.BabyBgmTrack>();

    [Fact]
    public async Task EndToEndClosedLoop_BreakoutBgmMood_ResolvesMatchingTrackId()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PlutoClosedLoopReinforcementService(learning, notebook, Storage());
        IPlutoAdaptivePatternRanker ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        await ranker.EnsureLoadedAsync();

        // 학습 전 1위 무드 = 사전 선호 1순위(미강화) — 기본 탐색
        Assert.False(ranker.GetCurrentRecommendations().TopBgmMood.IsReinforced);

        var r = await loop.IngestPerformanceAsync(Telemetry("vid-bgm", "0.3초 웃음 훅", PlutoColorGradingCompiler.GoldenHourWarmName, "Acoustic", new[] { "#warmvibes" }, 85, 66, 10.2));
        Assert.True(r.IsBreakoutSuccess);

        var rec = ranker.GetCurrentRecommendations();
        Assert.True(rec.HasReinforcedHistory);
        Assert.Equal("Acoustic", rec.TopBgmMood.DisplayLabel);
        Assert.Equal("bgm:Acoustic", rec.TopBgmMood.Key);
        Assert.True(rec.TopBgmMood.IsReinforced);
        var bgms = ranker.RankOptions(PlutoPatternKind.BgmMood);
        Assert.Equal("Acoustic", bgms[0].DisplayLabel);   // 사전 선호 1순위(Lullaby)를 앞섬

        // 키 · 라벨 모두 동일 트랙으로 해석
        Assert.Equal("Acoustic", ranker.ResolveRecommendedBgmTrackId(rec.TopBgmMood.Key, PresetTracks, "MusicBox"));
        Assert.Equal("Acoustic", ranker.ResolveRecommendedBgmTrackId(rec.TopBgmMood.DisplayLabel, PresetTracks, "MusicBox"));
        Assert.True(Enum.TryParse<PromptFlowStudio.Services.Export.BabyBgmTrack>(ranker.ResolveRecommendedBgmTrackId(rec.TopBgmMood.Key, PresetTracks, "MusicBox"), out var track));
        Assert.Equal(PromptFlowStudio.Services.Export.BabyBgmTrack.Acoustic, track);

        // 빈 목록 · 무드 없음 → 안전 폴백
        Assert.Equal("MusicBox", ranker.ResolveRecommendedBgmTrackId(rec.TopBgmMood.Key, Array.Empty<string>(), "MusicBox"));
        Assert.Equal("MusicBox", ranker.ResolveRecommendedBgmTrackId(null, PresetTracks, "MusicBox"));
        Assert.Equal("MusicBox", ranker.ResolveRecommendedBgmTrackId("   ", PresetTracks, "MusicBox"));
    }

    [Fact]
    public void ResolveRecommendedBgmTrackId_HandlesAliasesAndUnknownMoodsGracefully()
    {
        const string fb = "MusicBox";
        // 정확 일치(대소문자 · 접두 · 공백 무시) — 원본 ID 대소문자로 반환
        Assert.Equal("Lullaby", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("bgm:lullaby", PresetTracks, fb));
        Assert.Equal("Lullaby", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("  BGM:LULLABY ", PresetTracks, fb));
        Assert.Equal("Acoustic", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("acoustic", PresetTracks, fb));

        // 무드 별칭 → 그룹 트랙
        Assert.Equal("Lullaby", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Calm Piano", PresetTracks, fb));
        Assert.Equal("Lullaby", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("bgm:calm", PresetTracks, "Acoustic"));
        Assert.Equal("MusicBox", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Upbeat Playful", PresetTracks, "Lullaby"));
        Assert.Equal("MusicBox", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("bright", PresetTracks, "Acoustic"));
        Assert.Equal("Acoustic", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Cinematic", PresetTracks, fb));
        Assert.Equal("Acoustic", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("warm emotional", PresetTracks, fb));
        // Energetic 전용 트랙 없음 → Bright 근사 · 전용 트랙 있으면 우선
        Assert.Equal("MusicBox", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("energetic pop", PresetTracks, "Lullaby"));
        Assert.Equal("DancePop", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Energetic", new[] { "Lullaby", "MusicBox", "DancePop" }, "Lullaby"));

        // 미지 · custom · none · 빈 목록 → 폴백
        Assert.Equal(fb, PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("custom", PresetTracks, fb));
        Assert.Equal(fb, PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("none", PresetTracks, fb));
        Assert.Equal(fb, PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("bgm:", PresetTracks, fb));
        Assert.Equal(fb, PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Heavy Metal", PresetTracks, fb));
        Assert.Equal(fb, PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Lullaby", Array.Empty<string>(), fb));
        Assert.Equal("Lullaby", PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("Lullaby", new[] { "", "  ", "Lullaby" }, fb));   // 공백 ID 건너뜀

        // 결정론 — 동일 입력 동일 출력
        Assert.Equal(PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("playful", PresetTracks, fb), PlutoAdaptivePatternRanker.ResolveRecommendedBgmTrackId("playful", PresetTracks, fb));
    }

    private sealed class CapturingLlm : IPlutoLlmClient
    {
        public string LastUser = "";
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult<string?>(null);
        }
    }
}
