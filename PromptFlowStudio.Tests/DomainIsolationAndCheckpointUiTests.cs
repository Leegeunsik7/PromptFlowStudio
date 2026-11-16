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

    private static readonly string[] ParentingVocab = { "아기", "엄마", "아빠", "성장", "첫걸음", "육아", "가족", "baby", "family", "little one", "From." };

    private sealed class ScriptedLlm : IPlutoLlmClient
    {
        public string? Response { get; init; }
        public string LastSystem = "";
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            LastSystem = system;
            return Task.FromResult(Response);
        }
    }

    private static void AssertNoParentingVocab(PlutoSubtitleScript s)
    {
        foreach (var leak in ParentingVocab)
        {
            Assert.DoesNotContain(leak, s.SubjectName, StringComparison.OrdinalIgnoreCase);
            foreach (var act in s.Acts)
            {
                Assert.DoesNotContain(leak, act.KoreanText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(leak, act.EnglishText, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain(leak, s.ToFullLetterText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(leak, s.ToBilingualClipboardText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SubtitleDirector_GeneralCinematicPrompt_ProducesEditorialSubtitlesWithoutBabyLeakage()
    {
        var prompts = new[] { "AI neural core pulsing in a dark server room", "brutalist architecture at dusk", "neon city street at night", "premium product turntable shot", "macro dew drops on leaf", "misty alpine landscape", "slow dolly shot, cinematic" };
        var themes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in prompts)
        {
            var domain = PlutoSubtitleDirector.ResolveDomain(p);
            Assert.Equal(PlutoPromptDomain.GeneralCinematic, domain);
            foreach (var name in new[] { "하린", null })
            {
                var s = PlutoSubtitleDirector.GenerateLocal(p, name, domain);
                Assert.Equal(PlutoPromptDomain.GeneralCinematic, s.Domain);
                Assert.False(s.UsedLlm);
                AssertNoParentingVocab(s);
                Assert.DoesNotContain("하린", s.ToFullLetterText(), StringComparison.Ordinal);
                Assert.StartsWith(PlutoSubtitleScript.CinematicHeaderIcon, s.ToFullLetterText(), StringComparison.Ordinal);
                Assert.EndsWith(PlutoSubtitleScript.CinematicSignatureKo, s.ToBilingualClipboardText(), StringComparison.Ordinal);
                foreach (var act in s.Acts)
                {
                    Assert.False(string.IsNullOrWhiteSpace(act.KoreanText));
                    Assert.True(act.KoreanText.Length <= PlutoSubtitleDirector.MaxActLength, act.KoreanText);
                    Assert.True(act.EnglishText.Length <= PlutoSubtitleDirector.MaxEnglishLength, act.EnglishText);
                    Assert.Contains(act.EnglishText, c => char.IsAsciiLetter(c));
                }
                Assert.Equal(3, s.ToSrtFormat().Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.Equal(s.Act1Text, PlutoSubtitleDirector.GenerateLocal(p, name, domain).Act1Text);   // 결정론
            }
            themes.Add(PlutoSubtitleDirector.CinematicThemeOf(p));
        }
        Assert.Equal(prompts.Length, themes.Count);   // 피사체별 테마 정렬
        Assert.Equal("cine-tech", PlutoSubtitleDirector.CinematicThemeOf("AI neural core"));
        Assert.Equal("cine-architecture", PlutoSubtitleDirector.CinematicThemeOf("glass skyscraper"));

        // 🧠 AI 프레이밍 — 에디토리얼 지시문 사용 · 육아 어휘 막은 거부 후 로컬 보강 · 깨끗한 막은 채택
        var llm = new ScriptedLlm { Response = "[{\"act\":1,\"ko\":\"아기의 첫걸음\",\"en\":\"Baby's first step\"},{\"act\":2,\"ko\":\"빛이 유리 위를 미끄러진다\",\"en\":\"Light glides across the glass\"},{\"act\":3,\"ko\":\"우리 가족의 약속\",\"en\":\"A family promise\"}]" };
        var prompt = "glass skyscraper architecture at golden hour";
        var local = PlutoSubtitleDirector.GenerateLocal(prompt, null, PlutoPromptDomain.GeneralCinematic);
        var g = await new PlutoSubtitleDirector(llm).GenerateLetterScriptAsync(prompt, "하린", default, PlutoPromptDomain.GeneralCinematic);
        Assert.Equal(PlutoSubtitleDirector.LlmCinematicInstruction, llm.LastSystem);
        Assert.True(g.UsedLlm);
        Assert.Equal(PlutoPromptDomain.GeneralCinematic, g.Domain);
        Assert.Equal(("빛이 유리 위를 미끄러진다", "Light glides across the glass"), (g.Act2Text, g.Act2En));
        Assert.Equal((local.Act1Text, local.Act1En), (g.Act1Text, g.Act1En));
        Assert.Equal((local.Act3Text, local.Act3En), (g.Act3Text, g.Act3En));
        AssertNoParentingVocab(g);
        Assert.True(PlutoPromptCompressor.IsKnown(PlutoSubtitleDirector.LlmCinematicInstruction));

        // 프로덕션 결선 — Workspace · Publish 모두 도메인 판정 후 전달
        var root = RepoRoot();
        var ws = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("PlutoSubtitleDirector.ResolveDomain(src, _babyExplicit)", ws, StringComparison.Ordinal);
        Assert.Contains("GenerateLetterScriptAsync(src, name, ct, domain)", ws, StringComparison.Ordinal);
        var pub = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "ViewModels", "PublishViewModel.cs"));
        Assert.Contains("GenerateLetterScriptAsync(src, subjectName, ct, domain)", pub, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtitleDirector_BabyStudioPrompt_PreservesWarmParentingLetterSubtitles()
    {
        foreach (var p in new[] { "baby first step in the park", "아기가 처음 웃는 순간", "newborn nap", "first birthday cake baby" })
        {
            var domain = PlutoSubtitleDirector.ResolveDomain(p);
            Assert.Equal(PlutoPromptDomain.BabyStudio, domain);
            var legacy = PlutoSubtitleDirector.GenerateLocal(p, "하린");
            var baby = PlutoSubtitleDirector.GenerateLocal(p, "하린", domain);
            Assert.Equal(PlutoPromptDomain.BabyStudio, baby.Domain);
            Assert.Equal(legacy.Acts.Select(a => (a.KoreanText, a.EnglishText)), baby.Acts.Select(a => (a.KoreanText, a.EnglishText)));
            Assert.StartsWith("하린아,", baby.Act3Text, StringComparison.Ordinal);
            Assert.StartsWith("💌 To. 하린", baby.ToFullLetterText(), StringComparison.Ordinal);
            Assert.EndsWith(PlutoSubtitleScript.LetterSignatureKo, baby.ToFullLetterText(), StringComparison.Ordinal);
            Assert.EndsWith(PlutoSubtitleScript.LetterSignatureEn, baby.ToBilingualClipboardText(), StringComparison.Ordinal);
        }

        // 명시 아기 칩 선택 → 토큰 없는 프롬프트도 감성 편지 · 기본 이름 "우리 아기"
        Assert.Equal(PlutoPromptDomain.BabyStudio, PlutoSubtitleDirector.ResolveDomain("soft window light", explicitBabySelection: true));
        var chip = PlutoSubtitleDirector.GenerateLocal("soft window light", null, PlutoPromptDomain.BabyStudio);
        Assert.Equal(PlutoSubtitleDirector.DefaultSubjectName, chip.SubjectName);
        Assert.StartsWith("My little one,", chip.Act3En, StringComparison.Ordinal);

        // 🧠 BabyStudio LLM 경로 — 기존 편지 지시문 · 육아 어휘 그대로 채택(금칙어 필터 미적용)
        var llm = new ScriptedLlm { Response = "[{\"act\":1,\"ko\":\"아기의 첫걸음\",\"en\":\"Baby's first step\"},{\"act\":2,\"ko\":\"엄마 품의 오후\",\"en\":\"An afternoon in mom's arms\"},{\"act\":3,\"ko\":\"하린아, 사랑해\",\"en\":\"Harin, I love you\"}]" };
        var g = await new PlutoSubtitleDirector(llm).GenerateLetterScriptAsync("baby first step", "하린", default, PlutoPromptDomain.BabyStudio);
        Assert.Equal(PlutoSubtitleDirector.LlmLetterInstruction, llm.LastSystem);
        Assert.Equal(("아기의 첫걸음", "Baby's first step"), (g.Act1Text, g.Act1En));
        Assert.Equal("엄마 품의 오후", g.Act2Text);
        Assert.Equal(PlutoPromptDomain.BabyStudio, g.Domain);
        var none = await new PlutoSubtitleDirector(llm).GenerateLetterScriptAsync("baby first step", "하린");   // domain 생략 = 하위 호환
        Assert.Equal(PlutoSubtitleDirector.LlmLetterInstruction, llm.LastSystem);
        Assert.Equal(g.Act1Text, none.Act1Text);
    }

    [Fact]
    public void Workspace_GeneralTrendBriefOrModeReset_ClearsStickyBabyDomainFlag()
    {
        // 🔭 기획안 판정 — 프롬프트·제목 모두 아기 토큰 0일 때만 GeneralCinematic(플래그 해제)
        Assert.True(PromptDomainClassifier.IsGeneralBrief("AI neural core reveal, cinematic dolly", "AI 코어 리빌"));
        Assert.False(PromptDomainClassifier.IsGeneralBrief("soft light portrait", "아기 첫 미소 챌린지"));
        Assert.False(PromptDomainClassifier.IsGeneralBrief("newborn hands macro", "손끝 매크로"));

        // 🎬 모드 전환 — 무드/성장 단계 토큰 제거 → 칩 하이라이트 해제
        var mood = PromptFlowStudio.Services.AI.GeminiPromptService.BabyMoods[0].Mood;
        var stage = PromptFlowStudio.Services.AI.GeminiPromptService.BabyGrowthStages[0].Stage;
        var seeded = PromptFlowStudio.Services.AI.GeminiPromptService.ApplyGrowthStage(PromptFlowStudio.Services.AI.GeminiPromptService.ApplyBabyMood("glass skyscraper at dusk", mood), stage);
        Assert.NotNull(PromptFlowStudio.Services.AI.GeminiPromptService.ActiveBabyMood(seeded));
        Assert.NotNull(PromptFlowStudio.Services.AI.GeminiPromptService.ActiveGrowthStage(seeded));
        var reset = PromptFlowStudio.Services.AI.GeminiPromptService.StripBabyMood(PromptFlowStudio.Services.AI.GeminiPromptService.StripGrowthStage(seeded));
        Assert.Null(PromptFlowStudio.Services.AI.GeminiPromptService.ActiveBabyMood(reset));
        Assert.Null(PromptFlowStudio.Services.AI.GeminiPromptService.ActiveGrowthStage(reset));
        Assert.Contains("glass skyscraper", reset, StringComparison.Ordinal);

        // 🔎 네거티브 투명화 — 기본 아기 템플릿뿐 → 박스 교체 · 사용자 문구 → 박스 보존 + 실제 전송 미리보기 · BabyStudio → 그대로
        const string general = "glass skyscraper at dusk";
        var template = BabyStudioMatrix.MergeNegative("");
        Assert.True(PromptDomainClassifier.IsDefaultBabyNegative(template));
        Assert.Equal((PromptDomainClassifier.GeneralCinematicNegative, (string?)null), PromptDomainClassifier.SyncNegativeBox(general, template, false));
        var custom = BabyStudioMatrix.MergeNegative("text, logo");
        Assert.False(PromptDomainClassifier.IsDefaultBabyNegative(custom));
        var (box, preview) = PromptDomainClassifier.SyncNegativeBox(general, custom, false);
        Assert.Equal(custom, box);
        Assert.Equal(PromptDomainClassifier.ResolveNegativePrompt(general, custom, false), preview);
        Assert.StartsWith("text, logo", preview, StringComparison.Ordinal);
        Assert.Equal((template, (string?)null), PromptDomainClassifier.SyncNegativeBox(general, template, explicitBabySelection: true));
        Assert.Equal((PromptDomainClassifier.GeneralCinematicNegative, (string?)null), PromptDomainClassifier.SyncNegativeBox(general, PromptDomainClassifier.GeneralCinematicNegative, false));
        Assert.Equal(PromptDomainClassifier.GeneralCinematicNegative, PromptDomainClassifier.SyncNegativeBox(general, "", false).Preview);

        // Workspace 결선 — 기획안 자동 해제 · 전환 배지 · 칩 하이라이트 해제 · 박스 동기화 · 미리보기 줄
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        var apply = ws.IndexOf("private async Task ApplyBriefAsync(", StringComparison.Ordinal);
        Assert.True(apply > 0);
        var applyBody = ws.Substring(apply, ws.IndexOf("Snap(\"🔭 기획안: \"", apply, StringComparison.Ordinal) - apply);
        Assert.Contains("PromptDomainClassifier.IsGeneralBrief(brief.ReadyPrompt, brief.Title)", applyBody, StringComparison.Ordinal);
        Assert.Contains("_babyExplicit = false;", applyBody, StringComparison.Ordinal);
        Assert.Contains("SyncDomainNegativeBox();", applyBody, StringComparison.Ordinal);

        var resetAt = ws.IndexOf("private void ResetToGeneralCinematicMode()", StringComparison.Ordinal);
        Assert.True(resetAt > 0);
        var resetBody = ws.Substring(resetAt, ws.IndexOf("Snap(\"🎬 일반 시네마틱 모드\")", resetAt, StringComparison.Ordinal) - resetAt);
        Assert.Contains("_babyExplicit = false;", resetBody, StringComparison.Ordinal);
        Assert.Contains("StripBabyMood(", resetBody, StringComparison.Ordinal);
        Assert.Contains("StripGrowthStage(", resetBody, StringComparison.Ordinal);
        Assert.Contains("SyncDomainNegativeBox();", resetBody, StringComparison.Ordinal);

        Assert.Contains("@onclick=\"ResetToGeneralCinematicMode\"", ws, StringComparison.Ordinal);
        Assert.Contains("🎬 일반 시네마틱 모드로 전환", ws, StringComparison.Ordinal);
        Assert.Contains("data-domain-reset=\"1\"", ws, StringComparison.Ordinal);
        Assert.Contains("실제 전송 네거티브: @negSent", ws, StringComparison.Ordinal);
        Assert.Contains("data-neg-preview=\"1\"", ws, StringComparison.Ordinal);
        Assert.Contains(".ws-neg-sent", ws, StringComparison.Ordinal);
    }
}
