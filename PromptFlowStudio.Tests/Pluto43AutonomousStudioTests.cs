using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🔭⚖️🎬 Pluto 4.3 — 자율 트렌드 기획안 · A/B 변형 · 16:9 + 9:16 듀얼 마스터.</summary>
public sealed class Pluto43AutonomousStudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto43_" + Guid.NewGuid().ToString("N"));

    public Pluto43AutonomousStudioTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeWebFetcher : IPlutoWebTrendFetcher
    {
        private readonly IReadOnlyList<PlutoLiveTrendKeyword> _items;
        public FakeWebFetcher(params string[] keywords) => _items = keywords.Select(k => new PlutoLiveTrendKeyword(k, 1000)).ToArray();
        public Task<IReadOnlyList<PlutoLiveTrendKeyword>> FetchLiveTrendingKeywordsAsync(CancellationToken ct = default) => Task.FromResult(_items);
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        private readonly string? _json;
        public int Calls;
        public FakeLlm(string? json) => _json = json;
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(_json); }
    }

    private static readonly DateTime AprilUtc = new(2026, 4, 10, 1, 0, 0, DateTimeKind.Utc);   // KST 4월 — 시즌 폴백에 '벚꽃 피크닉' 포함

    [Fact]
    public async Task TrendOpportunityEngine_GeneratesThreeBriefs_AvoidingMistakeNotebookTokens()
    {
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        await notebook.RecordNegativeFeedbackAsync(new[] { "벚꽃" });
        await notebook.RecordNegativeFeedbackAsync(new[] { "벚꽃" });   // 2.0 ≥ 차단 임계 1.8
        Assert.Contains("벚꽃", notebook.GetSuppressedTokens());
        var ranker = new PlutoAdaptivePatternRanker(null, notebook);
        var web = new FakeWebFetcher("벚꽃 엔딩 챌린지", "아기 첫 이유식", "벚꽃 명소", "주말 가족 캠핑");

        // ① 템플릿 경로(LLM 없음)
        var engine = new PlutoTrendOpportunityEngine(ranker, notebook, llm: null, webFetcher: web, utcNow: () => AprilUtc);
        var briefs = await engine.GenerateDailyBriefsAsync();
        Assert.Equal(3, briefs.Count);
        Assert.False(engine.LastUsedLlm);
        Assert.Equal(new[] { "아기 첫 이유식", "주말 가족 캠핑" }, briefs.Take(2).Select(b => b.TrendKeyword).ToArray());   // 실시간 우선 · 차단 2건 제외
        Assert.Equal(PlutoTrendOpportunityEngine.SourceLive, briefs[0].Source);
        Assert.Equal(PlutoTrendOpportunityEngine.SourceSeasonal, briefs[2].Source);
        Assert.Equal(3, briefs.Select(b => b.BriefId).Distinct(StringComparer.Ordinal).Count());
        var rec = ranker.GetCurrentRecommendations();
        foreach (var b in briefs)
        {
            foreach (var text in new[] { b.TrendKeyword, b.Title, b.ReadyPrompt.Replace("Avoid motifs/styles: 벚꽃", "", StringComparison.Ordinal), b.RecommendedHook })
                Assert.DoesNotContain("벚꽃", text, StringComparison.Ordinal);
            Assert.True(b.AvoidedMistakeCount >= 2, b.BriefId);   // '벚꽃 엔딩 챌린지' · '벚꽃 명소'(+ 시즌 '벚꽃 피크닉')
            Assert.StartsWith("brief-20260410-", b.BriefId, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(b.RecommendedHook));
            Assert.Equal(rec.TopColorLook.DisplayLabel, b.RecommendedColorLook);
            Assert.NotEqual("none", b.RecommendedBgmMood);
            Assert.NotEqual("custom", b.RecommendedBgmMood);
            Assert.Contains("opening hook: " + b.RecommendedHook, b.ReadyPrompt, StringComparison.Ordinal);
            Assert.Contains("color look: " + b.RecommendedColorLook, b.ReadyPrompt, StringComparison.Ordinal);
            Assert.Contains("bgm mood: " + b.RecommendedBgmMood, b.ReadyPrompt, StringComparison.Ordinal);
            Assert.EndsWith("템플릿", b.RationaleBadge, StringComparison.Ordinal);
        }
        Assert.StartsWith("🔥 실시간 급상승", briefs[0].RationaleBadge, StringComparison.Ordinal);
        Assert.StartsWith("🌱", briefs[2].RationaleBadge, StringComparison.Ordinal);

        // ② LLM 보강 — 차단 토큰 포함 필드는 거부(템플릿 유지) · 유효 필드는 채택 + 실행 지시 보강
        var llm = new FakeLlm("```json\n{\"briefs\":[{\"i\":1,\"title\":\"첫 이유식 리액션 모음\",\"prompt\":\"baby tasting first puree, close-up reactions\"},"
                              + "{\"i\":2,\"title\":\"벚꽃 아래 캠핑\",\"prompt\":\"family camping under cherry trees 벚꽃\"},{\"i\":9,\"title\":\"범위 밖\"}]}\n```");
        var aiEngine = new PlutoTrendOpportunityEngine(ranker, notebook, llm, webFetcher: web, utcNow: () => AprilUtc);
        var ai = await aiEngine.GenerateDailyBriefsAsync(3);
        Assert.Equal(1, llm.Calls);
        Assert.True(aiEngine.LastUsedLlm);
        Assert.Equal("첫 이유식 리액션 모음", ai[0].Title);
        Assert.True(ai[0].UsedLlm);
        Assert.StartsWith("baby tasting first puree", ai[0].ReadyPrompt, StringComparison.Ordinal);
        Assert.Contains("opening hook: " + ai[0].RecommendedHook, ai[0].ReadyPrompt, StringComparison.Ordinal);
        Assert.EndsWith("AI", ai[0].RationaleBadge, StringComparison.Ordinal);
        Assert.False(ai[1].UsedLlm);   // 차단 토큰 포함 → 템플릿 유지
        Assert.Equal(briefs[1].Title, ai[1].Title);
        Assert.Equal(briefs[2], ai[2]);   // 범위 밖 인덱스 무시

        // ③ 오프라인(실시간 · 메모리 없음) → 시즌/상시 폴백 3선 · 개수 클램프
        var offline = new PlutoTrendOpportunityEngine(ranker, notebook, utcNow: () => AprilUtc);
        var fb = await offline.GenerateDailyBriefsAsync(99);
        Assert.Equal(PlutoTrendOpportunityEngine.MaxCount, fb.Count);
        Assert.All(fb, b => Assert.Equal(PlutoTrendOpportunityEngine.SourceSeasonal, b.Source));
        Assert.DoesNotContain(fb, b => b.TrendKeyword.Contains("벚꽃", StringComparison.Ordinal));
    }

    private static string BirthdayHook1 => PlutoMontageStoryGenerator.TemplateHookLines("생일")[0];
    private static string BirthdayHook2 => PlutoMontageStoryGenerator.TemplateHookLines("생일")[1];

    private async Task<(PlutoLearningService Learning, PlutoMistakeNotebookService Notebook, PlutoAdaptivePatternRanker Ranker)> LearnedSetupAsync()
    {
        var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        await learning.ApplyPatternReinforcementAsync(new[] { "hook:" + BirthdayHook1, "look:" + PlutoColorGradingCompiler.VintageFilm16mmName, "bgm:Lullaby" }, 1.3, "test:ab", 0);
        await notebook.RecordNegativeFeedbackAsync(new[] { BirthdayHook2 });   // 감점(차단 미만) — 챌린저 후보에서 제외
        var ranker = new PlutoAdaptivePatternRanker(learning, notebook);
        await ranker.EnsureLoadedAsync();
        return (learning, notebook, ranker);
    }

    [Fact]
    public async Task VariantABGenerator_ProducesDistinctChampionAndChallenger_WithSharedExperimentId()
    {
        var (learning, notebook, ranker) = await LearnedSetupAsync();
        using var _l = learning;
        using var _n = notebook;
        var gen = new PlutoVariantABGenerator(ranker, story: null, notebook, experimentIdFactory: () => "exp-test-001");

        var ab = await gen.BuildVariantPairAsync("돌잔치 생일 파티");
        Assert.Equal("exp-test-001", ab.ExperimentId);
        Assert.Equal("돌잔치 생일 파티", ab.ThemeHint);
        Assert.True(ab.IsValidExperiment);
        Assert.True(ab.ChampionA.HookStyle != ab.ChallengerB.HookStyle || ab.ChampionA.ColorLookId != ab.ChallengerB.ColorLookId);

        // 🏆 챔피언 = 학습 1위
        var rec = ranker.GetCurrentRecommendations();
        Assert.Equal(BirthdayHook1, ab.ChampionA.HookStyle);
        Assert.Equal(rec.TopHook.DisplayLabel, ab.ChampionA.HookStyle);
        Assert.Equal(PlutoColorGradingCompiler.VintageFilm16mmName, ab.ChampionA.ColorLookId);
        Assert.Equal("Lullaby", ab.ChampionA.BgmMood);
        Assert.Equal(PlutoVariantABGenerator.ChampionLabel, ab.ChampionA.VariantLabel);

        // 🧪 챌린저 = 감점 제외 · 챔피언과 다른 탐색 후보
        Assert.Equal(PlutoVariantABGenerator.ChallengerLabel, ab.ChallengerB.VariantLabel);
        Assert.NotEqual(ab.ChampionA.HookStyle, ab.ChallengerB.HookStyle);
        Assert.NotEqual(BirthdayHook2, ab.ChallengerB.HookStyle);
        Assert.NotEqual(ab.ChampionA.ColorLookId, ab.ChallengerB.ColorLookId);
        Assert.NotEqual(ab.ChampionA.BgmMood, ab.ChallengerB.BgmMood);
        Assert.DoesNotContain(ab.ChallengerB.BgmMood, new[] { "custom", "none" });
        foreach (var kind in new[] { (PlutoPatternKind.Hook, ab.ChallengerB.HookStyle), (PlutoPatternKind.Look, ab.ChallengerB.ColorLookId), (PlutoPatternKind.BgmMood, ab.ChallengerB.BgmMood) })
            Assert.False(ranker.RankOptions(kind.Item1).First(o => o.DisplayLabel == kind.Item2).IsPenalized);

        // 프롬프트 접미 · 자막(플랜 없음 → 명목 3막) · Act 1 = 변형 훅
        foreach (var v in new[] { ab.ChampionA, ab.ChallengerB })
        {
            Assert.Equal(PlutoVariantABGenerator.BuildSuffix(v.HookStyle, v.ColorLookId, v.BgmMood), v.PromptSuffix);
            Assert.Equal(3, v.Subtitles.Count);
            Assert.Equal(v.HookStyle, v.Subtitles[0].Text);
        }
        Assert.NotNull(ab.ChampionA.Subtitles[0].SecondaryText);
        Assert.Equal(PlutoMontageStoryGenerator.TemplateHookEnglish(BirthdayHook1), ab.ChampionA.Subtitles[0].SecondaryText);   // 템플릿 EN 짝
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ab.ChampionA.Subtitles[i].StartPreciseSec, ab.ChallengerB.Subtitles[i].StartPreciseSec);
            Assert.Equal(ab.ChampionA.Subtitles[i].EndPreciseSec, ab.ChallengerB.Subtitles[i].EndPreciseSec);
        }
        Assert.Contains(ab.ChallengerB.HookStyle, ab.HypothesisSummary, StringComparison.Ordinal);
        Assert.Contains("학습 챔피언", ab.HypothesisSummary, StringComparison.Ordinal);

        // 콜드 스타트(랭커 미주입)도 유효 실험 · 기본 ID 형식
        var cold = await new PlutoVariantABGenerator().BuildVariantPairAsync("");
        Assert.True(cold.IsValidExperiment);
        Assert.StartsWith("exp-", cold.ExperimentId, StringComparison.Ordinal);
        Assert.Contains("기본 챔피언", cold.HypothesisSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariantABGenerator_WithMontagePlan_GeneratesAlignedSubtitlesForBothVariants()
    {
        var (learning, notebook, ranker) = await LearnedSetupAsync();
        using var _l = learning;
        using var _n = notebook;
        var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(_dir, "ab" + i.ToString(CultureInfo.InvariantCulture) + ".jpg")).ToArray();
        var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(paths, null, new[] { 3.9, 7.6, 11.2 }, null, PlutoMotionCompiler.FourActDurationSec);
        var windows = PlutoMontageStoryGenerator.ComputeCueWindows(plan);
        Assert.Equal(4, windows.Length);

        // LLM 응답(4컷) — 챔피언/챌린저 두 번 호출 · Act 1은 각 변형 훅으로 교체
        var llm = new FakeLlm("{\"cuts\":[{\"cut\":1,\"act\":1,\"ko\":\"AI 훅\",\"en\":\"AI hook\"},{\"cut\":2,\"act\":2,\"ko\":\"케이크 앞 미소\",\"en\":\"Smile by the cake\"},"
                              + "{\"cut\":3,\"act\":2,\"ko\":\"모두의 박수\",\"en\":\"Everyone claps\"},{\"cut\":4,\"act\":3,\"ko\":\"사랑해 우리 아가\",\"en\":\"We love you baby\"}]}");
        var story = new PlutoMontageStoryGenerator(llm, notebook, ranker);
        var gen = new PlutoVariantABGenerator(ranker, story, notebook, () => "exp-plan");
        var ab = await gen.BuildVariantPairAsync("생일 파티", plan);

        Assert.Equal(2, llm.Calls);
        Assert.True(ab.IsValidExperiment);
        foreach (var v in new[] { ab.ChampionA, ab.ChallengerB })
        {
            Assert.Equal(plan.Clips.Count, v.Subtitles.Count);
            for (var i = 0; i < windows.Length; i++)
            {
                Assert.Equal(windows[i].Start, v.Subtitles[i].StartPreciseSec);   // 컷 경계 정렬(0ms 드리프트)
                Assert.Equal(windows[i].End, v.Subtitles[i].EndPreciseSec);
                if (i > 0) Assert.True(v.Subtitles[i].StartPreciseSec >= v.Subtitles[i - 1].EndPreciseSec);
            }
            Assert.Equal(v.HookStyle, v.Subtitles[0].Text);
            Assert.Equal("사랑해 우리 아가", v.Subtitles[^1].Text);
        }
        Assert.NotEqual(ab.ChampionA.Subtitles[0].Text, ab.ChallengerB.Subtitles[0].Text);

        // 스토리 생성기 미주입 → 결정론 템플릿(같은 구간)
        var local = await new PlutoVariantABGenerator(ranker, null, notebook, () => "exp-local").BuildVariantPairAsync("생일 파티", plan);
        Assert.Equal(windows.Length, local.ChallengerB.Subtitles.Count);
        Assert.Equal(windows[^1].End, local.ChampionA.Subtitles[^1].EndPreciseSec);
        Assert.Equal(local.ChallengerB.HookStyle, local.ChallengerB.Subtitles[0].Text);
        Assert.DoesNotContain(local.ChallengerB.Subtitles, c => c.Text.Contains(BirthdayHook2, StringComparison.Ordinal));
    }

    private static byte[] PngHeader(int w, int h)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16, 4), w);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20, 4), h);
        return b;
    }

    [Fact]
    public async Task DualMasterExport_SharesIdenticalLockedTimeline_Across16x9And9x16()
    {
        var photos = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(_dir, "dual" + i.ToString(CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(p, PngHeader(1920, 1080));
            photos.Add(p);
        }
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            lock (calls) calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => null, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = Path.Combine(_dir, "sfx"),
            MasteringTempDir = Path.Combine(_dir, "master"),
        };

        var full = photos.Select(Path.GetFullPath).ToArray();
        var focals = new (double X, double Y)[] { (0.2, 0.4), (0.5, 0.5), (0.85, 0.3) };
        var locked = PlutoMontageCompiler.Shared.CompileMontagePlan(full, focals, new[] { 4.9, 10.15 }, null, PlutoMotionCompiler.FourActDurationSec);
        var cues = PlutoMontageStoryGenerator.GenerateLocal(locked, "생일");
        var packager = new PlutoMultiFormatPackager(null, null);
        var outDir = Path.Combine(_dir, "dual");

        var r = await exp.ExportDualAspectMontageMastersAsync(photos, focals, new ExportOptions { OutputDirectory = outDir }, locked, cues,
            packager: packager, themeHint: "생일", activeTags: new[] { "#돌잔치" });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("0ms 드리프트", r.Message, StringComparison.Ordinal);
        Assert.EndsWith(StockSuiteExporter.DualLandscapeSuffix, r.Landscape16x9Path, StringComparison.Ordinal);
        Assert.EndsWith(StockSuiteExporter.DualVerticalSuffix, r.Vertical9x16Path, StringComparison.Ordinal);
        Assert.True(File.Exists(r.Landscape16x9Path));
        Assert.True(File.Exists(r.Vertical9x16Path));
        Assert.NotNull(r.PublishingKit);
        Assert.Same(cues, r.SharedSubtitles);

        // 🔒 두 마스터 모두 잠금 플랜과 정확 일치(0ms 드리프트)
        foreach (var plan in new[] { r.LandscapePlan!, r.VerticalPlan! })
        {
            Assert.Equal(locked.TransitionOffsetsSec, plan.TransitionOffsetsSec);
            Assert.Equal(locked.CutPointsSec, plan.CutPointsSec);
            Assert.Equal(locked.TransitionSec, plan.TransitionSec);
            Assert.Equal(locked.TotalDurationSec, plan.TotalDurationSec);
            for (var i = 0; i < locked.Clips.Count; i++)
            {
                Assert.Equal(locked.Clips[i].StartSec, plan.Clips[i].StartSec);
                Assert.Equal(locked.Clips[i].DurationSec, plan.Clips[i].DurationSec);
                Assert.Equal(locked.Clips[i].Frames, plan.Clips[i].Frames);
            }
        }

        // 🎞 렌더 인자 — 16:9(1920×1080 · 크롭 없음) vs 9:16(1080×1920 · 초점 선크롭) · 같은 xfade offset · 같은 프레임 수
        var video = calls.Where(c => Array.IndexOf(c, "-filter_complex") >= 0 && Array.IndexOf(c, "-frames:v") >= 0).ToList();
        Assert.Equal(2, video.Count);
        string Graph(string[] c) => c[Array.IndexOf(c, "-filter_complex") + 1];
        string Frames(string[] c) => c[Array.IndexOf(c, "-frames:v") + 1];
        var land = video.Single(c => c[^1].Contains("_master_16x9", StringComparison.Ordinal));
        var vert = video.Single(c => c[^1].Contains("_master_9x16", StringComparison.Ordinal));
        Assert.Contains(":s=1920x1080", Graph(land), StringComparison.Ordinal);
        Assert.DoesNotContain(":v]crop=", Graph(land), StringComparison.Ordinal);
        Assert.Contains(":s=1080x1920", Graph(vert), StringComparison.Ordinal);
        Assert.Contains("[0:v]crop=606:1080:81:0,scale=", Graph(vert), StringComparison.Ordinal);
        Assert.Equal(Frames(land), Frames(vert));
        foreach (var o in locked.TransitionOffsetsSec)
        {
            var token = ":offset=" + o.ToString("0.000", CultureInfo.InvariantCulture) + "[";
            Assert.Contains(token, Graph(land), StringComparison.Ordinal);
            Assert.Contains(token, Graph(vert), StringComparison.Ordinal);
        }

        // 잠금 플랜 손상(컷 수 불일치) → 즉시 거부(렌더 없음)
        calls.Clear();
        var two = PlutoMontageCompiler.Shared.CompileMontagePlan(full.Take(2).ToArray(), null, null, null);
        var bad = await exp.ExportDualAspectMontageMastersAsync(photos, focals, new ExportOptions { OutputDirectory = outDir }, two, cues);
        Assert.False(bad.AnyOk);
        Assert.Empty(calls);
    }
}
