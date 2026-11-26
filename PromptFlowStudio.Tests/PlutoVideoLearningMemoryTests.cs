using System.Net;
using System.Net.Http;
using System.Text;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🧠 플루토 자기학습 비디오 디렉터 메모리 — 격리 경로 영속 · 👎 회피 규칙 → 프리플라이트 반영 · 👍 강화 · Pluto 9/6/2 · DI 패리티.</summary>
[Trait("Category", "FastUnit")]
public class PlutoVideoLearningMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto_learn_" + Guid.NewGuid().ToString("N"));
    private string MemPath => Path.Combine(_dir, PlutoVideoLearningMemoryService.FileName);

    public PlutoVideoLearningMemoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Toddler = "a toddler runs across the park chasing soap bubbles";
    private const string ToddlerSimilar = "toddler chasing bubbles in a sunny park";
    private const string Unrelated = "matte black wireless earbuds rotate on a marble pedestal";

    [Fact]
    public void SeedsPersist_AndSaveLoad_RoundTripsInIsolatedPath()
    {
        var a = new PlutoVideoLearningMemoryService(MemPath);
        Assert.True(File.Exists(MemPath));
        Assert.NotEqual(PlutoVideoLearningMemoryService.DefaultPath(), a.FilePath);
        var seeds = a.Snapshot();
        Assert.Contains(seeds, r => r.Id == "seed-subject");
        Assert.Contains(seeds, r => r.Id == "seed-ambient" && r.AmbientOnly);
        Assert.Equal(0, a.LearnedRuleCount);

        a.RecordFailureAndGetRefinement(Toddler, PlutoFeedbackReason.BodyMotionDistortion);
        a.RecordPositiveOutcome(Unrelated, Unrelated + ", soft studio rim light, slow turntable reveal");

        var b = new PlutoVideoLearningMemoryService(MemPath);   // 새 인스턴스 = 재시작
        Assert.Equal(2, b.LearnedRuleCount);
        var avoid = Assert.Single(b.Snapshot(), r => r.Kind == "avoid");
        Assert.Equal("인체/동작 왜곡", avoid.Reason);
        Assert.Contains("toddler", avoid.Keywords);
        var recipe = Assert.Single(b.Snapshot(), r => r.Kind == "recipe");
        Assert.Contains("soft studio rim light", recipe.PromptAnchor);
        Assert.Equal(seeds.Count + 2, b.Snapshot().Count);   // seed 중복 없음
    }

    [Fact]
    public void CorruptFile_FallsBackToSeeds_WithoutThrowing()
    {
        File.WriteAllText(MemPath, "{ not json");
        var m = new PlutoVideoLearningMemoryService(MemPath);
        Assert.Contains(m.Snapshot(), r => r.Id == "seed-physics");
        Assert.Equal(0, m.LearnedRuleCount);
    }

    [Fact]
    public void FailureFeedback_ChangesSubsequentPreflight_ForSimilarPromptsOnly()
    {
        var m = new PlutoVideoLearningMemoryService(MemPath);
        var (anchor, neg) = PlutoVideoLearningMemoryService.AvoidanceFor(PlutoFeedbackReason.BodyMotionDistortion);

        var before = PromptPreflightDirector.RefineLocal(ToddlerSimilar, 5, "16:9", false, null, m.GetLearnedGuidanceForPrompt(ToddlerSimilar));
        Assert.Equal(0, before.LearnedRulesApplied);
        Assert.DoesNotContain(anchor, before.RefinedPrompt);

        var immediate = m.RecordFailureAndGetRefinement(Toddler, "👎 어색함 · 인체/동작 왜곡", 5, "16:9", false);
        Assert.Contains(anchor, immediate.RefinedPrompt);
        foreach (var t in neg) Assert.Contains(t, immediate.MergedNegative);
        Assert.Equal(1, immediate.LearnedRulesApplied);

        var after = PromptPreflightDirector.RefineLocal(ToddlerSimilar, 5, "16:9", false, null, m.GetLearnedGuidanceForPrompt(ToddlerSimilar));
        Assert.Equal(1, after.LearnedRulesApplied);
        Assert.Contains(anchor, after.RefinedPrompt);
        Assert.Contains("deformed body", after.MergedNegative);
        Assert.Contains(PromptPreflightDirector.LearnedLabel(1), after.SummaryKo);
        Assert.NotEqual(before.RefinedPrompt, after.RefinedPrompt);

        // 멱등 — 보정본 재투입 시 학습 앵커 중복 없음
        var again = PromptPreflightDirector.RefineLocal(after.RefinedPrompt, 5, "16:9", false, after.MergedNegative, m.GetLearnedGuidanceForPrompt(ToddlerSimilar));
        Assert.Equal(1, CountOf(again.RefinedPrompt, anchor));

        // 무관 프롬프트에는 학습 회피 규칙 미적용
        var other = PromptPreflightDirector.RefineLocal(Unrelated, 5, "16:9", false, null, m.GetLearnedGuidanceForPrompt(Unrelated));
        Assert.Equal(0, other.LearnedRulesApplied);
        Assert.DoesNotContain(anchor, other.RefinedPrompt);
    }

    [Fact]
    public void RepeatedFailure_StrengthensSameRule_InsteadOfDuplicating()
    {
        var m = new PlutoVideoLearningMemoryService(MemPath);
        m.RecordFailureAndGetRefinement(Toddler, PlutoFeedbackReason.FramingJump);
        m.RecordFailureAndGetRefinement(ToddlerSimilar, PlutoFeedbackReason.FramingJump);
        var avoid = Assert.Single(m.Snapshot(), r => r.Kind == "avoid");
        Assert.Equal(2, avoid.Hits);
        Assert.Equal(2.0, avoid.Weight);
        m.RecordFailureAndGetRefinement(Toddler, PlutoFeedbackReason.LightingReadability);   // 다른 사유 → 별도 규칙
        Assert.Equal(2, m.LearnedRuleCount);
    }

    [Fact]
    public void PositiveOutcome_ReinforcesRecipe_AndInjectsItIntoSimilarPrompts()
    {
        var m = new PlutoVideoLearningMemoryService(MemPath);
        var id = m.RecordPositiveOutcome(Toddler, Toddler + ", warm backlit golden hour haze, shallow depth of field");
        Assert.NotNull(id);
        var r1 = Assert.Single(m.Snapshot(), r => r.Id == id);
        Assert.Equal(1.0, r1.Weight);

        var id2 = m.RecordPositiveOutcome(ToddlerSimilar, null);   // 유사 프롬프트 재성공 → 같은 레시피 강화
        Assert.Equal(id, id2);
        var r2 = Assert.Single(m.Snapshot(), r => r.Id == id);
        Assert.Equal(2.0, r2.Weight);
        Assert.Equal(2, r2.Hits);
        Assert.Contains("warm backlit golden hour haze", r2.PromptAnchor);

        var g = m.GetLearnedGuidanceForPrompt("a toddler in the park");
        Assert.Equal(1, g.LearnedCount);
        var refined = PromptPreflightDirector.RefineLocal("a toddler in the park", 5, "16:9", false, null, g);
        Assert.Contains("warm backlit golden hour haze", refined.RefinedPrompt);

        // 👍 은 연관 회피 규칙 가중치도 올림
        m.RecordFailureAndGetRefinement(Toddler, PlutoFeedbackReason.NarrativeRepetition);
        var w0 = Assert.Single(m.Snapshot(), r => r.Kind == "avoid").Weight;
        m.RecordPositiveOutcome(Toddler, null);
        Assert.True(Assert.Single(m.Snapshot(), r => r.Kind == "avoid").Weight > w0);
    }

    [Fact]
    public async Task GeminiPreflight_InjectsLearnedGuidance_IntoInstruction_AndOfflineFallback()
    {
        var m = new PlutoVideoLearningMemoryService(MemPath);
        m.RecordFailureAndGetRefinement(Toddler, PlutoFeedbackReason.BodyMotionDistortion);
        var g = m.GetLearnedGuidanceForPrompt(ToddlerSimilar);
        var (anchor, _) = PlutoVideoLearningMemoryService.AvoidanceFor(PlutoFeedbackReason.BodyMotionDistortion);

        var offline = await new GeminiPromptService(new HttpClient(new StubHandler(_ => throw new HttpRequestException())), () => null)
            .AnalyzeAndRefinePromptAsync(ToddlerSimilar, 5, "16:9", false, null, g);
        Assert.True(offline.UsedFallback);
        Assert.Contains(anchor, offline.RefinedPrompt);

        var h = new StubHandler(_ => Text("{\"prompt\":\"a toddler happily chases bubbles in a park, eye-level\",\"negative\":\"blurry\",\"summary_ko\":\"정리\"}"));
        var ai = await new GeminiPromptService(new HttpClient(h), () => "k").AnalyzeAndRefinePromptAsync(ToddlerSimilar, 5, "16:9", false, null, g);
        Assert.False(ai.UsedFallback);
        Assert.Contains(anchor, h.LastBody);                 // 지시문에 학습 규칙 포함
        Assert.Contains(anchor, ai.RefinedPrompt);           // AI 가 누락해도 부착
        Assert.Contains("deformed body", ai.MergedNegative);
        Assert.Equal(1, ai.LearnedRulesApplied);
        Assert.Contains("🧠", ai.SummaryKo);
    }

    [Theory]
    [InlineData("인체/동작 왜곡", PlutoFeedbackReason.BodyMotionDistortion)]
    [InlineData("구도/시점 튐", PlutoFeedbackReason.FramingJump)]
    [InlineData("조명/가독성 문제", PlutoFeedbackReason.LightingReadability)]
    [InlineData("서사 중복", PlutoFeedbackReason.NarrativeRepetition)]
    [InlineData("FramingJump", PlutoFeedbackReason.FramingJump)]
    public void ParseReason_RecognizesKoreanTags(string tag, PlutoFeedbackReason expected)
        => Assert.Equal(expected, PlutoVideoLearningMemoryService.ParseReason(tag));

    [Fact]
    public void Workspace_WiresLearningIndicator_AndFeedbackControls()
    {
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("@inject PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService PlutoLearn", ws, StringComparison.Ordinal);
        Assert.Contains("🧠 플루토 학습 패턴 @(pf.LearnedRulesApplied", ws, StringComparison.Ordinal);
        Assert.Contains("👍 잘 나옴 (학습)", ws, StringComparison.Ordinal);
        Assert.Contains("👎 어색함 (보완 재생성)", ws, StringComparison.Ordinal);
        Assert.Contains("PlutoLearn.GetLearnedGuidanceForPrompt(", ws, StringComparison.Ordinal);
        Assert.Contains("PlutoLearn.RecordFailureAndGetRefinement(", ws, StringComparison.Ordinal);
        Assert.Contains("PlutoLearn.RecordPositiveOutcome(", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void DiParity_MainFormAndMauiProgram_RegisterLearningMemory()
    {
        var root = RepoRoot();
        const string reg = "AddSingleton(_ => new PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService())";
        var desktop = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "MainForm.cs"));
        var mobile = File.ReadAllText(Path.Combine(root, "PromptFlowStudio.Mobile", "MauiProgram.cs"));
        Assert.Equal(1, CountOf(desktop, reg));
        Assert.Equal(1, CountOf(mobile, reg));
    }

    [Fact]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return f(request);
        }
    }

    private static HttpResponseMessage Text(string t) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize(t) + "}]}}]}", Encoding.UTF8, "application/json"),
    };

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
