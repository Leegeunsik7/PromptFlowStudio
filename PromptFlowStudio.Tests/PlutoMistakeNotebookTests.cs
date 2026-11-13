using System.Text.Json;
using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🛡️ 플루토 오답 노트 — 히스테리시스(1.8/0.8) · 14일 반감기 · 분석기/압축기 회피 · 원자적 영속.</summary>
public class PlutoMistakeNotebookTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-mistake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task MistakeNotebook_AppliesHysteresisThresholds_WithoutSingleClickChattering()
    {
        var dir = TempDir();
        try
        {
            using var nb = new PlutoMistakeNotebookService(Path.Combine(dir, PlutoMistakeNotebookService.FileName), () => T0);
            var tok = new[] { "top-down slide" };

            await nb.RecordNegativeFeedbackAsync(tok, 1.0, "👎");   // 1.0 < 1.8 → 미차단
            Assert.Empty(nb.GetSuppressedTokens());
            Assert.Equal(string.Empty, nb.BuildNegativeConstraintPrompt());

            await nb.RecordNegativeFeedbackAsync(tok, 1.0, "👎");   // 2.0 ≥ 1.8 → 차단
            Assert.Contains("top-down slide", nb.GetSuppressedTokens());
            Assert.Equal("Avoid motifs/styles: top-down slide", nb.BuildNegativeConstraintPrompt());

            await nb.RecordPositiveReliefAsync(tok, 1.0);   // 1.0 — 차단 임계 미만이지만 해제 임계(0.8) 이상 → 차단 유지(채터링 없음)
            Assert.Equal(1.0, nb.GetEffectivePenalty("top-down slide"), 6);
            Assert.Contains("top-down slide", nb.GetSuppressedTokens());

            await nb.RecordPositiveReliefAsync(tok, 1.2);   // 0.0 < 0.8 → 해제(하한 0)
            Assert.Equal(0.0, nb.GetEffectivePenalty("top-down slide"), 6);
            Assert.Empty(nb.GetSuppressedTokens());

            await nb.RecordNegativeFeedbackAsync(tok, 1.0);   // 해제 후 단일 👎 → 재차단 안 됨
            Assert.Empty(nb.GetSuppressedTokens());

            Assert.False(PlutoMistakeNotebookService.NextSuppressedState(false, 1.79));
            Assert.True(PlutoMistakeNotebookService.NextSuppressedState(false, 1.8));
            Assert.True(PlutoMistakeNotebookService.NextSuppressedState(true, 0.8));
            Assert.False(PlutoMistakeNotebookService.NextSuppressedState(true, 0.79));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task MistakeNotebook_AppliesHalfLifeDecay_OverFourteenDays()
    {
        var dir = TempDir();
        try
        {
            var now = T0;
            using var nb = new PlutoMistakeNotebookService(Path.Combine(dir, PlutoMistakeNotebookService.FileName), () => now);

            await nb.RecordNegativeFeedbackAsync(new[] { "Playful ukulele" }, 1.0);
            Assert.Equal(1.0, nb.GetEffectivePenalty("Playful ukulele"), 6);
            now = T0.AddDays(14);
            Assert.Equal(0.5, nb.GetEffectivePenalty("Playful ukulele"), 6);
            now = T0.AddDays(28);
            Assert.Equal(0.25, nb.GetEffectivePenalty("playful UKULELE"), 6);   // 대소문자 무시

            Assert.Equal(1.0, PlutoMistakeNotebookService.Decay(2.0, T0, T0.AddDays(14)), 6);
            Assert.Equal(2.0, PlutoMistakeNotebookService.Decay(2.0, T0, T0.AddDays(-3)), 6);   // 시계 역행 → 감쇠 없음

            // 차단 상태는 반감기 경과로 자연 해제(2.0 → 14일 1.0 유지 · 28일 0.5 해제)
            now = T0;
            await nb.RecordNegativeFeedbackAsync(new[] { "#POV" }, 1.0);
            await nb.RecordNegativeFeedbackAsync(new[] { "#POV" }, 1.0);
            Assert.Contains("#POV", nb.GetSuppressedTokens());
            now = T0.AddDays(14);
            Assert.Contains("#POV", nb.GetSuppressedTokens());
            now = T0.AddDays(28);
            Assert.DoesNotContain("#POV", nb.GetSuppressedTokens());

            // 감쇠 후 가산: 14일 경과 0.5 + 1.0 = 1.5
            now = T0;
            using var fresh = new PlutoMistakeNotebookService(Path.Combine(dir, "decay2.json"), () => now);
            await fresh.RecordNegativeFeedbackAsync(new[] { "soft piano" }, 1.0);
            now = T0.AddDays(14);
            await fresh.RecordNegativeFeedbackAsync(new[] { "soft piano" }, 1.0);
            Assert.Equal(1.5, fresh.GetEffectivePenalty("soft piano"), 6);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task TrendAnalyzer_ExcludesSuppressedTokens_FromSynthesizedHooks()
    {
        var dir = TempDir();
        try
        {
            using var nb = new PlutoMistakeNotebookService(Path.Combine(dir, PlutoMistakeNotebookService.FileName), () => T0);
            var baseline = PlutoTrendAnalyzer.Analyze(StudioThemeCategory.BabyParenting);
            Assert.Equal(PlutoTrendAnalyzer.ModifierFor(PlutoTrendAnalyzer.MicroZoomHook), baseline.HookStructure);

            await nb.RecordNegativeFeedbackAsync(new[] { PlutoTrendAnalyzer.MicroZoomHook }, 1.0);
            await nb.RecordNegativeFeedbackAsync(new[] { PlutoTrendAnalyzer.MicroZoomHook }, 1.0);

            var guarded = PlutoTrendAnalyzer.Analyze(StudioThemeCategory.BabyParenting, null, nb);
            Assert.DoesNotContain(guarded.Tokens, t => t.Tag == PlutoTrendAnalyzer.MicroZoomHook);
            Assert.Equal(baseline.Tokens.Count - 1, guarded.Tokens.Count);
            Assert.Equal(PlutoTrendAnalyzer.ModifierFor(PlutoTrendAnalyzer.Pov), guarded.HookStructure);   // 차순위 훅으로 대체

            var mods = new[] { PlutoTrendAnalyzer.ModifierFor(PlutoTrendAnalyzer.MicroZoomHook)!, PlutoTrendAnalyzer.ModifierFor(PlutoTrendAnalyzer.GoldenHourGaze)! };
            var blended = PlutoPromptCompressor.BlendViralModifiers("Act 1: baby smiles", mods, nb);
            Assert.DoesNotContain(mods[0], blended);
            Assert.Contains(mods[1], blended);
            Assert.Contains(mods[0], PlutoPromptCompressor.BlendViralModifiers("Act 1: baby smiles", mods));   // 미주입 → 기존 동작

            var compact = PlutoPromptCompressor.Compose(PlutoCopilot.LlmTripletInstruction, "Memo (Korean): 뒤집기 성공", nb);
            Assert.Contains("avoid: " + PlutoTrendAnalyzer.MicroZoomHook, compact.User);
            Assert.Equal(compact.User, PlutoPromptCompressor.Compose(PlutoCopilot.LlmTripletInstruction,
                PlutoPromptCompressor.AppendNegativeConstraint("Memo (Korean): 뒤집기 성공", nb), nb).User);   // 멱등
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task MistakeNotebook_PersistsAtomically_ToStorageManager()
    {
        var dir = TempDir();
        try
        {
            var storage = new PlutoDriveStorageManager(null, dir);
            using (var nb = new PlutoMistakeNotebookService(storage, () => T0))
            {
                await nb.RecordNegativeFeedbackAsync(new[] { "top-down slide", "Playful ukulele" }, 1.0, "👎 t1");
                await nb.RecordNegativeFeedbackAsync(new[] { "top-down slide" }, 1.0, "👎 t2");
            }

            var path = storage.ResolveDataPath(PlutoMistakeNotebookService.FileName);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            var snap = JsonSerializer.Deserialize<PlutoMistakeNotebookService.Snapshot>(await File.ReadAllTextAsync(path));
            Assert.NotNull(snap?.Entries);
            Assert.Equal(2, snap!.Entries!.Count);

            using (var reload = new PlutoMistakeNotebookService(storage, () => T0))
            {
                await reload.EnsureLoadedAsync();
                Assert.Contains("TOP-DOWN SLIDE", reload.GetSuppressedTokens());
                Assert.Equal(1.0, reload.GetEffectivePenalty("Playful ukulele"), 6);
                Assert.Equal("Avoid motifs/styles: top-down slide", reload.BuildNegativeConstraintPrompt());
                await reload.ClearAllAsync();
                Assert.Empty(reload.GetSuppressedTokens());
            }

            using var cleared = new PlutoMistakeNotebookService(storage, () => T0);
            await cleared.EnsureLoadedAsync();
            Assert.Equal(0, cleared.TrackedCount);
        }
        finally { TryDelete(dir); }
    }
}
