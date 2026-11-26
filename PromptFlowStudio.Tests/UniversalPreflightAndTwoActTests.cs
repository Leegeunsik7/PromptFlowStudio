using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>🛡️ 도메인 무관 프리플라이트 프롬프트 디렉터(AI 모의 · 오프라인 폴백) · 30초 자동 2막 분할 · 하드 컷 기본 합본 · Pluto 9/6/2.</summary>
[Trait("Category", "FastUnit")]
public class UniversalPreflightAndTwoActTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_preflight_" + Guid.NewGuid().ToString("N"));

    public UniversalPreflightAndTwoActTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return f(request);
        }
    }

    private static HttpResponseMessage Text(string t) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize(t) + "}]}}]}", Encoding.UTF8, "application/json"),
    };

    // ── ① 범용 프리플라이트(오프라인 로컬 규칙) ──

    [Theory]
    [InlineData("a barista pours latte art into a white ceramic cup in a sunlit cafe")]                       // 라이프스타일
    [InlineData("matte black wireless earbuds rotate on a marble pedestal")]                                     // 제품
    [InlineData("a red fox walks through fresh snow in a pine forest")]                                           // 자연
    [InlineData("a silver starship glides past a ringed gas giant")]                                              // SF
    [InlineData("a cartoon robot waves at the camera")]                                                           // 애니메이션
    [InlineData("swirling iridescent liquid gradients, abstract background")]                                    // 추상 배경
    [InlineData("girl in red uniform dribbles past a defender, shoots a three-pointer, celebrates with teammates")] // 스포츠
    public void LocalPreflight_AnyDomain_AddsUniversalAnchors_AndIsIdempotent(string prompt)
    {
        var r = PromptPreflightDirector.RefineLocal(prompt, 5, "16:9", false, null);
        Assert.True(r.UsedFallback);
        Assert.Equal(prompt, r.RawPrompt);
        Assert.Contains(PromptPreflightDirector.SubjectAnchor, r.RefinedPrompt);
        Assert.Contains(PromptPreflightDirector.PhysicsAnchor, r.RefinedPrompt);
        foreach (var term in PromptPreflightDirector.AntiArtifactTerms) Assert.Contains(term, r.MergedNegative);
        Assert.StartsWith("🛡️ 보정: ", r.SummaryKo);
        Assert.DoesNotContain('\n', r.SummaryKo);
        // 스포츠 전용 규칙이 다른 장르에 새지 않음
        if (!prompt.Contains("dribbles", StringComparison.Ordinal)) Assert.DoesNotContain("basketball", r.RefinedPrompt, StringComparison.OrdinalIgnoreCase);

        var again = PromptPreflightDirector.RefineLocal(r.RefinedPrompt, 5, "16:9", false, r.MergedNegative);
        Assert.Equal(r.RefinedPrompt, again.RefinedPrompt);   // 멱등
        Assert.Equal(r.MergedNegative, again.MergedNegative);
    }

    [Fact]
    public void LocalPreflight_CondensesOvercrowdedActions_ToDurationBeats_KeepingFirstAndOutcome()
    {
        const string crowded = "a skateboarder rolls into the park, ollies over a bench, grinds a rail, kickflips down stairs, spins a 360, lands, raises both arms in celebration, golden hour";
        var five = PromptPreflightDirector.RefineLocal(crowded, 5, "9:16", false);
        Assert.Contains("rolls into the park", five.RefinedPrompt);           // 첫 동작 보존
        Assert.Contains("raises both arms in celebration", five.RefinedPrompt); // 결과 동작 보존
        Assert.DoesNotContain("grinds a rail", five.RefinedPrompt);           // 5초엔 과밀 → 압축
        Assert.Contains("golden hour", five.RefinedPrompt);                     // 묘사 절은 보존
        Assert.Contains("비트 압축", five.SummaryKo);
        Assert.Contains(PromptPreflightDirector.OutcomeOrderAnchor, five.RefinedPrompt);   // 축하는 동작 완료 후
        Assert.DoesNotContain(PromptPreflightDirector.LightingAnchor, five.RefinedPrompt); // 조명 이미 지정(golden hour) → 미부착

        var thirty = PromptPreflightDirector.RefineLocal(crowded, 30, "9:16", false);
        Assert.Contains("grinds a rail", thirty.RefinedPrompt);               // 30초는 6비트까지 수용
        Assert.True(PromptPreflightDirector.MaxBeatsFor(5) < PromptPreflightDirector.MaxBeatsFor(15));
        Assert.True(PromptPreflightDirector.MaxBeatsFor(15) < PromptPreflightDirector.MaxBeatsFor(30));
    }

    [Fact]
    public void LocalPreflight_DuplicateActions_AreRemoved_AndFramingRespected()
    {
        var r = PromptPreflightDirector.RefineLocal("close-up, a chef slices a tomato, a chef slices a tomato, a chef slices a tomato", 5, "1:1", false);
        Assert.Equal(1, CountOf(r.RefinedPrompt, "slices a tomato"));   // 반복 동작 제거
        Assert.Contains("동작 3→1비트 압축", r.SummaryKo);
        Assert.DoesNotContain(PromptPreflightDirector.CameraAnchor, r.RefinedPrompt);   // close-up 이미 지정

        var ambient = PromptPreflightDirector.RefineLocal("city skyline at night", 5, "16:9", true);
        Assert.Contains(PromptPreflightDirector.AmbientCameraAnchor, ambient.RefinedPrompt);
        Assert.Contains(PromptPreflightDirector.AmbientLightingAnchor, ambient.RefinedPrompt);
        Assert.Contains("로우키 네거티브 스페이스", ambient.SummaryKo);
    }

    [Fact]
    public void NegativeMerge_NoDuplicates_PreservesExistingOrder()
    {
        var merged = PromptPreflightDirector.MergeNegative("blurry, Watermark, text , extra limbs", out var added);
        Assert.StartsWith("blurry, Watermark, text, extra limbs", merged);
        Assert.Equal(PromptPreflightDirector.AntiArtifactTerms.Length - 3, added);
        Assert.Equal(1, CountOf(merged.ToLowerInvariant(), "watermark"));
        Assert.Equal(merged, PromptPreflightDirector.MergeNegative(merged, out var none));
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task Preflight_WithoutKey_UsesLocalFallback_NoHttp()
    {
        var h = new StubHandler(_ => throw new InvalidOperationException("must not call"));
        var svc = new GeminiPromptService(new HttpClient(h), () => null);
        var r = await svc.AnalyzeAndRefinePromptAsync("a hummingbird hovers over a red flower", 5, "16:9", false);
        Assert.True(r.UsedFallback);
        Assert.Equal(0, h.Calls);
        Assert.Contains("a hummingbird hovers over a red flower", r.RefinedPrompt);
        Assert.True(r.Changed);

        var empty = await svc.AnalyzeAndRefinePromptAsync("  ", 5, "16:9", false);
        Assert.Equal("  ", empty.RefinedPrompt);
        Assert.False(empty.Changed);
    }

    [Fact]
    public async Task Preflight_AiMocked_ValidatesSchema_AndMergesNegative()
    {
        const string json = "{\"prompt\":\"eye-level medium shot of a ceramic artist shaping a clay bowl on a spinning wheel, soft window light\",\"negative\":\"blurry, text\",\"summary_ko\":\"동작 1비트로 정리 · 카메라 고정\"}";
        var h = new StubHandler(_ => Text(json));
        var svc = new GeminiPromptService(new HttpClient(h), () => "k");
        var r = await svc.AnalyzeAndRefinePromptAsync("ceramic artist making a bowl then glazing then firing it in a kiln", 5, "9:16", false, "lowres");
        Assert.False(r.UsedFallback);
        Assert.Equal(1, h.Calls);
        Assert.Contains("Action-to-duration calibration", h.LastBody);
        Assert.Contains("any subject", h.LastBody!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9:16", h.LastBody);
        Assert.StartsWith("eye-level medium shot of a ceramic artist", r.RefinedPrompt);
        Assert.StartsWith("lowres, blurry, text", r.MergedNegative);
        foreach (var term in PromptPreflightDirector.AntiArtifactTerms) Assert.Contains(term, r.MergedNegative);
        Assert.Equal(1, CountOf(r.MergedNegative, "text,") + (r.MergedNegative.EndsWith("text", StringComparison.Ordinal) ? 1 : 0));
        Assert.Equal("🛡️ AI 보정: 동작 1비트로 정리 · 카메라 고정", r.SummaryKo);

        // 스키마 불일치 → 로컬 폴백
        var bad = new GeminiPromptService(new HttpClient(new StubHandler(_ => Text("{\"refined\":\"x\"}"))), () => "k");
        var fb = await bad.AnalyzeAndRefinePromptAsync("a kite flies over a beach", 5, "16:9", false);
        Assert.True(fb.UsedFallback);
        Assert.Contains(PromptPreflightDirector.LocalNotice, fb.SummaryKo);

        // 네트워크 장애 → 로컬 폴백(throw 없음)
        var down = new GeminiPromptService(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))), () => "k") { Resilience = new PromptFlowStudio.Services.Network.ResilientHttpClient { Delay = static (_, _) => Task.CompletedTask } };
        var dr = await down.AnalyzeAndRefinePromptAsync("a kite flies over a beach", 5, "16:9", false);
        Assert.True(dr.UsedFallback);
    }

    // ── ② 30초 자동 2막 분할 ──

    [Fact]
    public void TwoAct_SplitsActions_WithoutRepeatingFullArc_AndSharesAnchors()
    {
        const string p = "a woman in a yellow raincoat walks along a harbor, stops at the pier edge, opens a letter, smiles with relief, soft overcast light";
        var acts = MultiDurationPlanner.SplitTwoActs(p);
        Assert.Contains("a woman in a yellow raincoat", acts.Anchor);
        Assert.Contains("soft overcast light", acts.Anchor);     // 묘사/조명 = 공통 앵커
        Assert.Contains("walks along a harbor", acts.Act1);
        Assert.Contains("smiles with relief", acts.Act2);
        Assert.DoesNotContain("smiles with relief", acts.Act1);  // 결과는 2막에만
        Assert.DoesNotContain("walks along a harbor", acts.Act2);

        var t = new BatchRenderJob { Prompt = p, DurationMode = VideoDurationMode.Sec30_Auto, OutputPath = @"C:\o\h.mp4",
            Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaNativeSeconds, "15") };
        var jobs = MultiDurationPlanner.Expand(t, null, "h");
        Assert.Equal(2, jobs.Length);
        Assert.NotEqual(jobs[0].Prompt, jobs[1].Prompt);
        Assert.DoesNotContain(p, jobs[0].Prompt);   // 전체 프롬프트 반복 금지
        Assert.DoesNotContain(p, jobs[1].Prompt);
        Assert.Contains(MultiDurationPlanner.Act1Directive, jobs[0].Prompt);
        Assert.Contains(MultiDurationPlanner.Act2Directive, jobs[1].Prompt);
        Assert.DoesNotContain("smiles with relief", jobs[0].Prompt);
        Assert.Contains("smiles with relief", jobs[1].Prompt);
        foreach (var j in jobs)
        {
            Assert.Contains("a woman in a yellow raincoat", j.Prompt);   // 동일 피사체/의상 연속성
            Assert.Contains("soft overcast light", j.Prompt);
            Assert.EndsWith(MultiDurationPlanner.ContinuityHint, j.Prompt);
            Assert.Equal(15f, j.DurationSec);
        }
    }

    [Theory]
    [InlineData(15, new[] { 0, 1 })]
    [InlineData(10, new[] { 0, 1, 1 })]
    [InlineData(5, new[] { 0, 0, 0, 1, 1, 1 })]
    public void TwoAct_SegmentToActMapping_FollowsFifteenSecondBoundary(int seg, int[] expected)
    {
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], MultiDurationPlanner.ActOf(i, seg));
    }

    [Fact]
    public void TwoAct_HonorsSubjectAnchor_AndPureSceneStillGetsDistinctActs()
    {
        var withAnchor = MultiDurationPlanner.SplitTwoActs("dribbles fast, scores a goal", "striker in blue kit");
        Assert.Equal("striker in blue kit", withAnchor.Anchor);
        Assert.Equal("dribbles fast", withAnchor.Act1);
        Assert.Equal("scores a goal", withAnchor.Act2);

        var scene = MultiDurationPlanner.SplitTwoActs("swirling iridescent liquid gradients, abstract background");
        Assert.Equal("", scene.Act1);
        Assert.Equal("", scene.Act2);
        var a = MultiDurationPlanner.ActSegmentPrompt(scene, 0, 2, 15);
        var b = MultiDurationPlanner.ActSegmentPrompt(scene, 1, 2, 15);
        Assert.NotEqual(a, b);
        Assert.StartsWith("swirling iridescent liquid gradients, abstract background, ", a);
    }

    [Fact]
    public void OneTakeFallback_StillUsesNarrativeBeats_NotActs()
    {
        var t = new BatchRenderJob { Prompt = "surfer rides a wave", DurationMode = VideoDurationMode.Sec15_OneTake, OutputPath = @"C:\o\s.mp4" };
        var jobs = MultiDurationPlanner.Expand(t, null, "s");
        Assert.Equal(3, jobs.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.StartsWith("surfer rides a wave, " + MultiDurationPlanner.BeatOf(i, 3), jobs[i].Prompt);
            Assert.DoesNotContain("act 1 of 2", jobs[i].Prompt);
            Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.TransitionOf(jobs[i]));
        }
    }

    // ── ③ 하드 컷 기본 합본 ──

    [Fact]
    public void Transition_HardCutDefault_DissolveExplicitOnly()
    {
        Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.DefaultTransition);
        Assert.Equal(0.0, MultiDurationPlanner.TransitionSeconds(CutTransitionMode.IndependentHardCut));
        Assert.Equal(0.25, MultiDurationPlanner.TransitionSeconds(CutTransitionMode.IndependentCrossfade));
        Assert.True(MultiDurationPlanner.IsIndependent(CutTransitionMode.IndependentHardCut));
        Assert.False(MultiDurationPlanner.IsIndependent(CutTransitionMode.LastFrameChain));

        var plain = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec30_Auto };
        Assert.All(MultiDurationPlanner.Expand(plain, null, "x"), j => Assert.Equal(CutTransitionMode.IndependentHardCut, MultiDurationPlanner.TransitionOf(j)));
        var dissolve = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec30_Auto, Metadata = MultiDurationPlanner.WithTransition(null, CutTransitionMode.IndependentCrossfade) };
        Assert.All(MultiDurationPlanner.Expand(dissolve, null, "x"), j => Assert.Equal(CutTransitionMode.IndependentCrossfade, MultiDurationPlanner.TransitionOf(j)));
        var chainAsked = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec30_Auto, Metadata = MultiDurationPlanner.WithTransition(null, CutTransitionMode.LastFrameChain) };
        Assert.All(MultiDurationPlanner.Expand(chainAsked, null, "x"), j => Assert.False(MultiDurationPlanner.ShouldChainLastFrame(j)));   // 단일 프롬프트 장편 = 항상 독립
        var legacy = new BatchRenderJob { Prompt = "p", Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaTransition, "IndependentCrossfade") };
        Assert.Equal(CutTransitionMode.IndependentCrossfade, MultiDurationPlanner.TransitionOf(legacy));   // 이름 직렬화 호환
    }

    [Fact]
    public async Task StitchByTransition_HardCutUsesConcat_DissolveUsesXfade()
    {
        var a = Path.Combine(_dir, "a.mp4"); var b = Path.Combine(_dir, "b.mp4");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var stitcher = new FFmpegVideoStitchService(() => "ffmpeg", (_, args, _) =>
        {
            calls.Enqueue(args);
            File.WriteAllText(args[^1], "x");
            return Task.FromResult(true);
        });
        var cuts = new[] { new StitchCut(a, 5, null), new StitchCut(b, 5, null) };

        var hard = await stitcher.StitchByTransitionAsync(cuts, Path.Combine(_dir, "hard.mp4"), CutTransitionMode.IndependentHardCut);
        Assert.True(hard.Ok, hard.Message);
        Assert.DoesNotContain(calls, c => c.Contains("-filter_complex"));
        Assert.Contains(calls, c => c.Contains("concat"));

        while (calls.TryDequeue(out _)) { }
        var soft = await stitcher.StitchByTransitionAsync(cuts, Path.Combine(_dir, "soft.mp4"), CutTransitionMode.IndependentCrossfade);
        Assert.True(soft.Ok, soft.Message);
        var g = Assert.Single(calls, c => c.Contains("-filter_complex"));
        Assert.Contains("xfade=transition=fade:duration=0.25:offset=4.75", g[g.ToList().IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void Workspace_WiresPreflightToggle_Badge_AndHardCutChoices()
    {
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("🛡️ AI 프롬프트 자동 보정", ws, StringComparison.Ordinal);
        Assert.Contains("private bool _preflightOn = true", ws, StringComparison.Ordinal);   // 기본 ON
        Assert.Contains("GeminiSvc.AnalyzeAndRefinePromptAsync(", ws, StringComparison.Ordinal);
        Assert.Contains("data-preflight-badge", ws, StringComparison.Ordinal);
        Assert.Contains("data-preflight-revert", ws, StringComparison.Ordinal);
        Assert.Contains("CutTransitionMode.IndependentHardCut", ws, StringComparison.Ordinal);
        Assert.Contains("WithTransition(meta, _longTransition)", ws, StringComparison.Ordinal);
        Assert.Contains("EffectiveNegative(anchored)", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Launcher.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
