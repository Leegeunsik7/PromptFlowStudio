using System.Globalization;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Tests;

public sealed class DomainIsolationAndCheckpointUiTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => "domain" + i.ToString(CultureInfo.InvariantCulture) + ".jpg").ToArray();

    [Fact]
    public void PromptDomainClassifier_DistinguishesGeneralCinematicPromptsFromBabyStudioPrompts()
    {
        foreach (var general in new[] { "AI neural core pulsing in a dark server room", "brutalist architecture at dusk", "misty alpine landscape", "premium product turntable shot", "macro dew drops on leaf" })
        {
            Assert.Equal(PlutoPromptDomain.GeneralCinematic, PromptDomainClassifier.Classify(general));
            var neg = PromptDomainClassifier.ResolveNegativePrompt(general, "", explicitBabySelection: false);
            Assert.Equal(PromptDomainClassifier.GeneralCinematicNegative, neg);
            Assert.DoesNotContain("distorted face", neg, StringComparison.Ordinal);
        }
        foreach (var baby in new[] { "아기가 처음 웃는 순간", "신생아 손발 클로즈업", "백일 기념 촬영", "첫돌 잔치", "육아 브이로그", "맘 카페 감성", "Baby first steps", "NEWBORN sleeping", "toddler in the park", "infant yawning", "growth milestone montage" })
            Assert.Equal(PlutoPromptDomain.BabyStudio, PromptDomainClassifier.Classify(baby));

        // 명시적 아기 칩 선택 → 토큰이 없어도 BabyStudio · 사용자 네거티브 그대로
        Assert.Equal(PlutoPromptDomain.BabyStudio, PromptDomainClassifier.Classify("soft window light", explicitBabySelection: true));
        var guarded = BabyStudioMatrix.MergeNegative("text");
        Assert.Equal(guarded, PromptDomainClassifier.ResolveNegativePrompt("soft window light", guarded, explicitBabySelection: true));

        // 잔류 아기 얼굴 가드는 일반 프롬프트에서 제거 · 사용자 문구 보존 · 중복 결합 0
        var cleaned = PromptDomainClassifier.ResolveNegativePrompt("AI neural core", guarded, explicitBabySelection: false);
        Assert.DoesNotContain(BabyStudioMatrix.FacialSafetyNegative, cleaned, StringComparison.Ordinal);
        Assert.StartsWith("text, ", cleaned, StringComparison.Ordinal);
        Assert.EndsWith(PromptDomainClassifier.GeneralCinematicNegative, cleaned, StringComparison.Ordinal);
        Assert.Equal(cleaned, PromptDomainClassifier.ResolveNegativePrompt("AI neural core", cleaned, false));
        Assert.Equal(PromptDomainClassifier.GeneralCinematicNegative, PromptDomainClassifier.ResolveNegativePrompt("AI neural core", BabyStudioMatrix.FacialSafetyNegative, false));

        // Workspace 결선 — 명시 선택 플래그 + 도메인 네거티브 사용
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("_babyExplicit = true;", ws, StringComparison.Ordinal);
        Assert.Contains("PromptDomainClassifier.ResolveNegativePrompt(", ws, StringComparison.Ordinal);
        Assert.Contains("EffectiveNegative(anchored)", ws, StringComparison.Ordinal);
        Assert.Contains("EffectiveNegative(prompt)", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void MontageStoryGenerator_GeneralCinematicDomain_UsesNeutralSubtitlesWithoutBabyLeakage()
    {
        var babyLeak = new[] { "아기", "첫 미소", "성장", "태어나", "사랑해", "가족", "baby", "born", "love", "family", "little one" };
        Assert.Equal(PlutoMontageStoryGenerator.CinematicThemeId, PlutoMontageStoryGenerator.DetectThemeId("AI neural core"));
        Assert.Equal(PlutoMontageStoryGenerator.CinematicThemeId, PlutoMontageStoryGenerator.DetectThemeId("glass skyscraper architecture"));
        Assert.Equal("everyday", PlutoMontageStoryGenerator.DetectThemeId(null));
        Assert.Equal("everyday", PlutoMontageStoryGenerator.DetectThemeId("아기 낮잠"));
        Assert.Equal("birthday", PlutoMontageStoryGenerator.DetectThemeId("생일"));

        for (var n = 2; n <= 5; n++)
        {
            var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(n), null, null, null);
            var cues = PlutoMontageStoryGenerator.GenerateLocal(plan, "AI neural core, cinematic product macro");
            Assert.Equal(n, cues.Count);
            foreach (var c in cues)
                foreach (var leak in babyLeak)
                {
                    Assert.DoesNotContain(leak, c.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(leak, c.SecondaryText ?? "", StringComparison.OrdinalIgnoreCase);
                }
            var hooks = PlutoMontageStoryGenerator.TemplateHookLines("AI neural core");
            Assert.Contains(cues[0].Text, hooks);
        }

        // BabyStudio 힌트는 기존 일상 테마 유지(회귀 0)
        var babyPlan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(3), null, null, null);
        Assert.Equal(PlutoMontageStoryGenerator.GenerateLocal(babyPlan)[0].Text, PlutoMontageStoryGenerator.GenerateLocal(babyPlan, "baby nap")[0].Text);
    }

    [Fact]
    public void RenderQueueMarkup_RendersCheckpointNoticeBadge_WhenJobMetadataContainsReuseNotice()
    {
        var note = VideoEngineRenderExecutionEngine.CheckpointReuseMessage(2);
        var reused = new BatchRenderJob { Prompt = "p", CutIndex = 2, Metadata = MultiDurationPlanner.WithMeta(null, VideoEngineRenderExecutionEngine.MetaCheckpointNotice, note) };
        var reused2 = new BatchRenderJob { Prompt = "p", CutIndex = 1, Metadata = MultiDurationPlanner.WithMeta(null, VideoEngineRenderExecutionEngine.MetaCheckpointNotice, VideoEngineRenderExecutionEngine.CheckpointReuseMessage(1)) };
        var fresh = new BatchRenderJob { Prompt = "p", CutIndex = 3 };

        Assert.Equal(note, VideoEngineRenderExecutionEngine.CheckpointNoticeOf(reused));
        Assert.Null(VideoEngineRenderExecutionEngine.CheckpointNoticeOf(fresh));
        Assert.StartsWith(VideoEngineRenderExecutionEngine.CheckpointBadgeLabel, note, StringComparison.Ordinal);

        Assert.Null(VideoEngineRenderExecutionEngine.SummarizeCheckpointReuse(new[] { fresh }));
        Assert.Equal(note, VideoEngineRenderExecutionEngine.SummarizeCheckpointReuse(new[] { reused, fresh }));
        Assert.Equal("♻️ 체크포인트 컷 재사용 2컷 (#1, #2)", VideoEngineRenderExecutionEngine.SummarizeCheckpointReuse(new[] { reused, fresh, reused2 }));
        Assert.Equal(note, VideoEngineRenderExecutionEngine.SummarizeCheckpointReuse(new[] { reused, fresh, reused2 }, new[] { reused.JobId, fresh.JobId }));

        var root = RepoRoot();
        var queue = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "RenderQueue.razor"));
        Assert.Contains("VideoEngineRenderExecutionEngine.CheckpointNoticeOf(job) is { } ckpt", queue, StringComparison.Ordinal);
        Assert.Contains("data-checkpoint-reuse=\"1\"", queue, StringComparison.Ordinal);
        Assert.Contains("VideoEngineRenderExecutionEngine.CheckpointBadgeLabel", queue, StringComparison.Ordinal);
        Assert.Contains(".q-badge.ckpt", queue, StringComparison.Ordinal);

        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("SummarizeCheckpointReuse(RenderQueue.Snapshot(), _miniIds)", ws, StringComparison.Ordinal);
        Assert.Contains("data-checkpoint-reuse=\"1\"", ws, StringComparison.Ordinal);
        Assert.Contains(".ws-ckpt-badge", ws, StringComparison.Ordinal);
    }
}
