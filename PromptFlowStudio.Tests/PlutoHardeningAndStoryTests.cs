using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎬 Pluto 4.1 하드닝 — 몽타주 3막 스토리 자막(클립 경계 정렬 · 오답 노트 제약).</summary>
public sealed class PlutoHardeningAndStoryTests
{
    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => "story" + i.ToString(CultureInfo.InvariantCulture) + ".jpg").ToArray();

    private sealed class FakeNotebook : IPlutoMistakeNotebookService
    {
        public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = 1.0, string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordPositiveReliefAsync(IEnumerable<string> tokens, double reliefWeight = 1.2, CancellationToken ct = default) => Task.CompletedTask;
        public double GetEffectivePenalty(string token) => Blocked.Contains(token) ? 2.0 : 0.0;
        public IReadOnlySet<string> GetSuppressedTokens() => Blocked;
        public string BuildNegativeConstraintPrompt(int maxTokens = 3) => Blocked.Count == 0 ? "" : "Avoid motifs/styles: " + string.Join(", ", Blocked.OrderBy(static s => s, StringComparer.Ordinal));
        public Task ClearAllAsync(CancellationToken ct = default) { Blocked.Clear(); return Task.CompletedTask; }
#pragma warning disable CS0067
        public event Action? Changed;
#pragma warning restore CS0067
    }

    private sealed class CapturingLlm : IPlutoLlmClient
    {
        public string? Response { get; init; }
        public string? LastUser { get; private set; }
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public async Task MontageStoryGenerator_ProducesAlignedThreeActSubtitles_Within15Seconds()
    {
        var beats = new List<double>();
        for (var t = 0.21; t < 15; t += 0.53) beats.Add(t);
        var gen = new PlutoMontageStoryGenerator();
        foreach (var beatSet in new[] { null, beats })
        {
            for (var n = 2; n <= 5; n++)
            {
                var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(n), null, beatSet, null);
                var cues = await gen.GenerateThreeActSubtitlesAsync(plan, themeHint: "가족 여행 바다");
                Assert.Equal(n, cues.Count);
                Assert.False(gen.LastUsedLlm);
                for (var i = 0; i < n; i++)
                {
                    var c = cues[i];
                    Assert.NotNull(c.StartPreciseSec);
                    Assert.NotNull(c.EndPreciseSec);
                    Assert.InRange(c.EffectiveStartSec, 0.0, 15.0);
                    Assert.InRange(c.EffectiveEndSec, 0.0, 15.0);
                    Assert.True(c.EffectiveEndSec - c.EffectiveStartSec >= PlutoMontageStoryGenerator.MinCueSec - 1e-9, "cue too short");
                    Assert.False(string.IsNullOrWhiteSpace(c.Text));
                    Assert.Contains(c.SecondaryText!, ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
                    if (i > 0) Assert.True(c.EffectiveStartSec >= cues[i - 1].EffectiveEndSec, "overlap at cut " + i.ToString(CultureInfo.InvariantCulture));
                    // 클립 경계 안쪽(시작 패딩 · Act1 0.20s)
                    var clip = plan.Clips[i];
                    Assert.True(c.EffectiveStartSec >= (i == 0 ? PlutoMontageStoryGenerator.HookStartSec : clip.StartSec + PlutoMontageStoryGenerator.EdgePadSec) - 1e-6);
                }
                Assert.Equal(PlutoMontageStoryGenerator.HookStartSec, cues[0].EffectiveStartSec, 3);
                Assert.Equal(PlutoMontageStoryGenerator.ClimaxEndSec, cues[^1].EffectiveEndSec, 3);
                Assert.True(cues[0].EffectiveEndSec <= plan.Clips[0].StartSec + plan.Clips[0].DurationSec - PlutoMontageStoryGenerator.EdgePadSec + 1e-6);

                // 번인 · SRT 는 정밀 초를 사용
                var filter = StockSuiteExporter.BuildTimedSubtitleFilter(cues, null);
                Assert.NotNull(filter);
                Assert.Contains("between(t,0.2,", filter, StringComparison.Ordinal);
                Assert.Contains("00:00:00,200 --> ", StockSuiteExporter.BuildSrt(cues), StringComparison.Ordinal);
                Assert.Contains(",14.7)", filter, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task MontageStoryGenerator_RespectsMistakeNotebookConstraints_InPrompt()
    {
        var notebook = new FakeNotebook();
        notebook.Blocked.Add("neon");
        notebook.Blocked.Add("바다");
        var llm = new CapturingLlm
        {
            Response = "{\"cuts\":[{\"cut\":1,\"act\":1,\"ko\":\"네온 바다로 출발\",\"en\":\"Off to the neon sea\"},"
                     + "{\"cut\":2,\"act\":2,\"ko\":\"함께 걷는 오후\",\"en\":\"An afternoon walk together\"},"
                     + "{\"cut\":3,\"act\":3,\"ko\":\"고마워, 오늘도\",\"en\":\"Thank you, again today\"}]}",
        };
        var gen = new PlutoMontageStoryGenerator(llm, notebook);
        var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(3), null, null, null);
        var cues = await gen.GenerateThreeActSubtitlesAsync(plan, themeHint: "바다 여행");

        Assert.NotNull(llm.LastUser);
        Assert.Contains("Avoid motifs/styles:", llm.LastUser!, StringComparison.Ordinal);
        Assert.Contains("neon", llm.LastUser!, StringComparison.Ordinal);
        Assert.Contains("바다", llm.LastUser!, StringComparison.Ordinal);
        Assert.Contains("Cut count: 3", llm.LastUser!, StringComparison.Ordinal);

        Assert.True(gen.LastUsedLlm);
        Assert.Equal(3, cues.Count);
        // 차단 토큰 포함 컷 1 → 템플릿으로 대체 · 컷 2/3 → LLM 채택
        Assert.DoesNotContain("neon", cues[0].SecondaryText ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("바다", cues[0].Text, StringComparison.Ordinal);
        Assert.Equal("함께 걷는 오후", cues[1].Text);
        Assert.Equal("고마워, 오늘도", cues[2].Text);
        foreach (var c in cues)
        {
            Assert.DoesNotContain("바다", c.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("neon", c.SecondaryText ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // 결정론 템플릿도 차단 토큰 회피
        var local = PlutoMontageStoryGenerator.GenerateLocal(plan, "바다 여행", notebook.Blocked);
        foreach (var c in local) Assert.DoesNotContain("바다", c.Text, StringComparison.Ordinal);
    }
    private static PlutoCuratedPhotoCandidate Cand(int idx, bool sel, int order, string label, (double X, double Y) focal, PlutoCurationRejectionReason reason)
        => new("cur" + idx.ToString(CultureInfo.InvariantCulture) + ".jpg", idx, sel, order, label, 0.5, 0.5, 0.5, 0.5, (ulong)idx, focal, reason);

    [Fact]
    public void CurationEngine_SwapAndReorder_PreservesValidFocalPointsAndActLabels()
    {
        var a1 = PlutoPhotoCurationEngine.Act1Label; var a2 = PlutoPhotoCurationEngine.Act2Label; var a3 = PlutoPhotoCurationEngine.Act3Label;
        var all = new[]
        {
            Cand(0, true, 1, a1, (0.5, 0.5), PlutoCurationRejectionReason.None),
            Cand(1, true, 2, a2, (0.4, 0.6), PlutoCurationRejectionReason.None),
            Cand(2, true, 3, a2, (0.6, 0.4), PlutoCurationRejectionReason.None),
            Cand(3, true, 4, a3, (0.5, 0.3), PlutoCurationRejectionReason.None),
            Cand(4, false, 0, "", (0.02, 0.97), PlutoCurationRejectionReason.BlurryOutOfFocus),
            Cand(5, false, 0, "", (0.5, 0.5), PlutoCurationRejectionReason.DuplicateBurstShot),
        };
        var current = new PlutoBatchCurationResult(new[] { all[0], all[1], all[2], all[3] }, all, 6, 1, 0, 1);
        var engine = new PlutoPhotoCurationEngine();

        // 🔁 Swap — 흐림 후보(4) → 마지막 슬롯(클라이맥스)
        var swapped = engine.SwapCandidate(current, 3, 4);
        Assert.Equal(4, swapped.SelectedCuts.Count);
        var promoted = swapped.SelectedCuts[3];
        Assert.Equal(4, promoted.OriginalIndex);
        Assert.True(promoted.IsSelected);
        Assert.Equal(PlutoCurationRejectionReason.None, promoted.RejectionReason);
        Assert.Equal(4, promoted.NarrativeActOrder);
        Assert.Equal(a3, promoted.NarrativeRoleLabel);
        Assert.InRange(promoted.FocalPoint.X, 0.20, 0.80);
        Assert.InRange(promoted.FocalPoint.Y, 0.20, 0.80);
        var demoted = swapped.AllEvaluated[3];
        Assert.False(demoted.IsSelected);
        Assert.Equal(0, demoted.NarrativeActOrder);
        Assert.Equal(string.Empty, demoted.NarrativeRoleLabel);
        Assert.Equal(PlutoCurationRejectionReason.LowerRankCut, demoted.RejectionReason);
        Assert.Same(promoted, swapped.AllEvaluated[4]);
        Assert.Equal(0, swapped.BlurryRejectedCount);
        Assert.Equal(1, swapped.DuplicateRejectedCount);
        Assert.Equal(0, swapped.ExposureRejectedCount);
        Assert.Equal(1, current.BlurryRejectedCount);           // 원본 불변
        Assert.True(current.AllEvaluated[3].IsSelected);

        // ⛔ 잘못된 입력
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SwapCandidate(current, 4, 4));
        Assert.ThrowsAny<ArgumentException>(() => engine.SwapCandidate(current, 0, 1));   // 이미 선택된 컷
        Assert.ThrowsAny<ArgumentException>(() => engine.SwapCandidate(current, 0, 99));

        // ↕ Move — 클라이맥스(승격 컷) → 맨 앞
        var moved = engine.MoveSelectedCut(swapped, 3, 0);
        Assert.Equal(new[] { 4, 0, 1, 2 }, moved.SelectedCuts.Select(c => c.OriginalIndex).ToArray());
        for (var i = 0; i < moved.SelectedCuts.Count; i++)
        {
            Assert.Equal(i + 1, moved.SelectedCuts[i].NarrativeActOrder);
            Assert.InRange(moved.SelectedCuts[i].FocalPoint.X, 0.20, 0.80);
            Assert.InRange(moved.SelectedCuts[i].FocalPoint.Y, 0.20, 0.80);
        }
        Assert.Equal(a1, moved.SelectedCuts[0].NarrativeRoleLabel);
        Assert.Equal(a2, moved.SelectedCuts[1].NarrativeRoleLabel);
        Assert.Equal(a2, moved.SelectedCuts[2].NarrativeRoleLabel);
        Assert.Equal(a3, moved.SelectedCuts[3].NarrativeRoleLabel);
        Assert.Equal(1, moved.AllEvaluated[4].NarrativeActOrder);
        Assert.Equal(a1, moved.AllEvaluated[4].NarrativeRoleLabel);
        Assert.Equal(4, moved.AllEvaluated[2].NarrativeActOrder);
        Assert.Equal(a3, moved.AllEvaluated[2].NarrativeRoleLabel);
        Assert.Equal(0, moved.AllEvaluated[3].NarrativeActOrder);
        Assert.Same(swapped, engine.MoveSelectedCut(swapped, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.MoveSelectedCut(swapped, 0, 4));
    }

    [Fact]
    public void DriveHealthService_ReportsAll7Files_AndDetectsMissingOrHealthyState()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfs_drivehealth_" + Guid.NewGuid().ToString("N"));
        var pluto = System.IO.Path.Combine(root, "Pluto");
        var studio = System.IO.Path.Combine(root, "PromptFlowStudio");
        System.IO.Directory.CreateDirectory(pluto);
        System.IO.Directory.CreateDirectory(studio);
        try
        {
            var svc = new PlutoDriveHealthService();

            // 1) 빈 폴더 → 7개 모두 Missing
            var empty = svc.CheckHealth(pluto, studio);
            Assert.True(empty.IsDriveMounted);
            Assert.Equal(7, empty.TotalCount);
            Assert.Equal(7, empty.Items.Count);
            Assert.Equal(0, empty.HealthyCount);
            Assert.All(empty.Items, i => Assert.Equal(PlutoDriveFileStatus.Missing, i.Status));
            Assert.Equal(5, empty.Items.Count(i => i.Category == PlutoDriveHealthService.CategoryPluto));
            Assert.Equal(2, empty.Items.Count(i => i.Category == PlutoDriveHealthService.CategoryStudio));

            // 2) 정상 JSON 5개 + 손상 JSON 1개 + 빈 파일 1개
            foreach (var f in PlutoDriveHealthService.PlutoFiles)
                System.IO.File.WriteAllText(System.IO.Path.Combine(pluto, f), "{\"ok\":true}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(studio, "members.json"), "[{\"Id\":\"a\"");   // 잘린 JSON
            System.IO.File.WriteAllText(System.IO.Path.Combine(studio, "user_settings.json"), "");

            var rep = svc.CheckHealth(pluto, studio);
            Assert.Equal(5, rep.HealthyCount);
            Assert.All(rep.Items.Where(i => i.Category == PlutoDriveHealthService.CategoryPluto), i =>
            {
                Assert.Equal(PlutoDriveFileStatus.Healthy, i.Status);
                Assert.True(i.SizeBytes > 0);
                Assert.NotNull(i.LastModifiedUtc);
            });
            var members = rep.Items.Single(i => i.FileName == "members.json");
            Assert.Equal(PlutoDriveFileStatus.CorruptOrEmpty, members.Status);
            Assert.False(string.IsNullOrEmpty(members.ErrorDetail));
            Assert.Equal(PlutoDriveFileStatus.CorruptOrEmpty, rep.Items.Single(i => i.FileName == "user_settings.json").Status);

            // 3) 복구 → 7/7, 4) 드라이브 폴더 부재 → DriveUnavailable
            System.IO.File.WriteAllText(System.IO.Path.Combine(studio, "members.json"), "[]");
            System.IO.File.WriteAllText(System.IO.Path.Combine(studio, "user_settings.json"), "{}");
            Assert.Equal(7, svc.CheckHealth(pluto, studio).HealthyCount);

            var gone = svc.CheckHealth(System.IO.Path.Combine(root, "nope1"), System.IO.Path.Combine(root, "nope2"));
            Assert.False(gone.IsDriveMounted);
            Assert.All(gone.Items, i => Assert.Equal(PlutoDriveFileStatus.DriveUnavailable, i.Status));
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
