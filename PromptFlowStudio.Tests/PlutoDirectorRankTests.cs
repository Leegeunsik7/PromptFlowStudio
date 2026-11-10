using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🎬 플루토 디렉터 랭크 — XP → 시네마토그래피 티어 · 프롬프트 광학 자동 주입(코파일럿 · 4컷 마일스톤).</summary>
public class PlutoDirectorRankTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0);
    private const string Memo = "오늘 아기가 거실에서 처음으로 뒤집기 성공하고 방긋 웃었어";

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable => true;
        public string? Response { get; init; }
        public string LastUser = "";

        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult(Response);
        }
    }

    [Theory]
    [InlineData(0, 1, "내추럴 비기너", 20)]
    [InlineData(19, 1, "내추럴 비기너", 20)]
    [InlineData(20, 2, "감성 스토리텔러", 50)]
    [InlineData(49, 2, "감성 스토리텔러", 50)]
    [InlineData(50, 3, "시네마틱 마스터", 100)]
    [InlineData(99, 3, "시네마틱 마스터", 100)]
    [InlineData(100, 4, "바이럴 마에스트로", 200)]
    [InlineData(5000, 4, "바이럴 마에스트로", 200)]
    [InlineData(-7, 1, "내추럴 비기너", 20)]
    public void PlutoDirectorRankService_CalculatesCorrectTier_FromXP(int xp, int level, string title, int next)
    {
        using var svc = new PlutoDirectorRankService(() => xp);
        var t = svc.GetCurrentTier();
        Assert.Equal(level, t.Level);
        Assert.Equal(title, t.Title);
        Assert.Equal(next, t.NextTierXp);
        Assert.Same(PlutoDirectorRankService.TierFor(xp), t);
        Assert.Equal(Math.Max(0, xp), svc.CurrentXp);
        Assert.InRange(PlutoDirectorRankService.ProgressPercent(svc.CurrentXp, t), 0, 100);
        Assert.Equal("🪐 Pluto Lv." + level + " " + title + " (" + Math.Max(0, xp) + "/" + next + " XP)",
            PlutoDirectorRankService.BadgeLabel(svc.CurrentXp, t));
    }

    [Fact]
    public void PlutoDirectorRankService_TierTable_IsAscendingAndContiguous()
    {
        var tiers = PlutoDirectorRankService.Tiers;
        Assert.Equal(PlutoDirectorRankService.MaxLevel, tiers.Length);
        Assert.Equal(new[] { 0, 20, 50, 100 }, tiers.Select(t => t.RequiredXp).ToArray());
        Assert.Equal(new[] { 20, 50, 100, 200 }, tiers.Select(t => t.NextTierXp).ToArray());
        for (var i = 0; i < tiers.Length; i++)
        {
            Assert.Equal(i + 1, tiers[i].Level);
            Assert.NotEmpty(tiers[i].CinematographyKeywords);
            Assert.StartsWith(", ", tiers[i].PromptModifier, StringComparison.Ordinal);
        }
        Assert.Equal(33, PlutoDirectorRankService.ProgressPercent(30, tiers[1]));   // (30-20)/30
        Assert.Equal(100, PlutoDirectorRankService.ProgressPercent(999, tiers[3]));
        Assert.True(tiers[3].IsMax);
    }

    [Fact]
    public void PlutoDirectorRankService_EnrichesPrompt_WithActiveTierKeywords()
    {
        using var lv2 = new PlutoDirectorRankService(() => 25);
        var p = lv2.EnrichPromptWithOptics("baby smiling on a playmat.");
        Assert.Equal("baby smiling on a playmat, cinematic 85mm portrait framing, f/1.4 shallow depth of field, warm golden-hour rim backlight, delicate film grain, natural skin tones.", p);
        Assert.Equal(p, lv2.EnrichPromptWithOptics(p));   // 멱등
        Assert.DoesNotContain("anamorphic", p, StringComparison.Ordinal);

        using var lv3 = new PlutoDirectorRankService(() => 60);
        var p3 = lv3.EnrichPromptWithOptics("baby smiling");
        Assert.Contains("anamorphic horizontal lens flare", p3, StringComparison.Ordinal);
        Assert.Contains("Tyndall effect", p3, StringComparison.Ordinal);
        Assert.DoesNotContain("85mm portrait", p3, StringComparison.Ordinal);

        using var lv4 = new PlutoDirectorRankService(() => 150);
        Assert.Contains("0.5s micro whip-zoom hook", lv4.EnrichPromptWithOptics("x"), StringComparison.Ordinal);

        using var lv1 = new PlutoDirectorRankService(() => 0);
        Assert.EndsWith(", standard warm daylight, 50mm lens, clean natural focus", lv1.EnrichPromptWithOptics("baby"), StringComparison.Ordinal);
        Assert.Equal("", lv1.EnrichPromptWithOptics(""));
        Assert.Equal("  ", lv1.EnrichPromptWithOptics("  "));
    }

    [Fact]
    public void PlutoDirectorRankService_ThrowingXpProvider_FallsBackToLevel1()
    {
        using var svc = new PlutoDirectorRankService(() => throw new InvalidOperationException("boom"));
        Assert.Equal(0, svc.CurrentXp);
        Assert.Equal(1, svc.GetCurrentTier().Level);
    }

    [Fact]
    public async Task PlutoDirectorRankService_TracksLearningMemoryXp_AndRaisesChanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-pluto-director-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var learning = new PlutoLearningService(Path.Combine(dir, PlutoLearningService.FileName));
            using var svc = new PlutoDirectorRankService(learning);
            await svc.EnsureLoadedAsync();
            Assert.Equal(1, svc.GetCurrentTier().Level);
            var changed = 0;
            svc.Changed += () => Interlocked.Increment(ref changed);
            await learning.AddBonusXpAsync("social:test", 22);
            Assert.Equal(22, svc.CurrentXp);
            Assert.Equal(2, svc.GetCurrentTier().Level);
            Assert.True(Volatile.Read(ref changed) >= 1);
            svc.Dispose();
            await learning.AddBonusXpAsync("social:test2", 2);   // 해제 후 통지 없음
            Assert.Equal(1, Volatile.Read(ref changed));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task PlutoCopilot_IntegratesDirectorOptics_IntoGeneratedTriplets()
    {
        var copilot = new PlutoCopilot { Director = new PlutoDirectorRankService(() => 60) };
        var local = copilot.DraftTripletLocal(Memo, Now);
        Assert.Equal(3, local.Count);
        foreach (var v in local)
        {
            Assert.Contains("anamorphic horizontal lens flare", v.Prompt, StringComparison.Ordinal);
            Assert.Contains("volumetric light rays with Tyndall effect", v.Prompt, StringComparison.Ordinal);
        }
        Assert.Contains("85mm f/1.4", local[0].Prompt, StringComparison.Ordinal);   // 스타일 룩 보존

        Assert.Contains("anamorphic horizontal lens flare", copilot.Draft(Memo, Now).VideoPrompt, StringComparison.Ordinal);

        var llm = new FakeLlm { Response = "{\"variants\":[{\"style\":\"studio\",\"hookTitle\":\"첫 뒤집기 ✨\",\"act1\":\"baby wriggling on a playmat\",\"act2\":\"baby flips over proudly\",\"act3\":\"baby smiles at camera\",\"camera\":\"dolly-in\",\"bgm\":\"acoustic\"}]}" };
        var withLlm = new PlutoCopilot(null, llm) { Director = new PlutoDirectorRankService(() => 25) };
        var v3 = await withLlm.DraftTripletVariantsAsync(Memo, Now);
        Assert.True(v3[0].UsedLlm);
        Assert.All(v3, v => Assert.Contains("golden-hour rim backlight", v.Prompt, StringComparison.Ordinal));
        Assert.Contains("Director rank Lv.2 Emotional Colorist", llm.LastUser, StringComparison.Ordinal);

        var plain = new PlutoCopilot().DraftTripletLocal(Memo, Now);   // 미주입 → 기존 프롬프트 불변
        Assert.All(plain, v => Assert.DoesNotContain("anamorphic", v.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlutoMilestoneOrchestrator_InjectsDirectorOptics_IntoAllScenes()
    {
        var llm = new FakeLlm { Response = null };
        var orch = new PlutoMilestoneOrchestrator(llm, null, new PlutoDirectorRankService(() => 120));
        var plan = await orch.OrchestrateFourCutPlanAsync("Day100");
        Assert.Equal(PlutoMilestoneOrchestrator.SceneCount, plan.Scenes.Count);
        Assert.All(plan.Scenes, s => Assert.Contains("0.5s micro whip-zoom hook", s.Prompt, StringComparison.Ordinal));
        Assert.Contains("Director rank Lv.4 Viral Maestro", llm.LastUser, StringComparison.Ordinal);

        var again = orch.ApplyDirectorOptics(plan);   // 멱등
        for (var i = 0; i < plan.Scenes.Count; i++) Assert.Equal(plan.Scenes[i].Prompt, again.Scenes[i].Prompt);

        var plain = await new PlutoMilestoneOrchestrator().OrchestrateFourCutPlanAsync("Day100");
        Assert.All(plain.Scenes, s => Assert.DoesNotContain("whip-zoom", s.Prompt, StringComparison.Ordinal));
    }
}
