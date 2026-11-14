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

/// <summary>🪐 Pluto 4.2 — 폐루프 성과 강화 · 9:16 스마트 크롭/SNS 키트 · Zero-Drift 몽타주 타임라인 잠금.</summary>
public sealed class Pluto42FeatureUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto42_" + Guid.NewGuid().ToString("N"));

    public Pluto42FeatureUpgradeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PlutoDriveStorageManager Storage() => new(null, Path.Combine(_dir, "vault"));

    private static PlutoVideoPerformanceTelemetry Telemetry(string id, double hook, double completion, double ctr, string hookStyle = "0.5초 휩줌 훅")
        => new(id, "YouTube Shorts", hookStyle, "GoldenHourGlow", "Lullaby", new[] { "#첫걸음", "#babyreels" }, hook, completion, ctr, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ClosedLoopReinforcement_BreakoutMetrics_BoostsPatternAndAwardsXp()
    {
        var learningPath = Path.Combine(_dir, "trend.json");
        var storage = Storage();
        using var learning = new PlutoLearningService(learningPath);
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var svc = new PlutoClosedLoopReinforcementService(learning, notebook, storage);
        var raised = 0;
        svc.Changed += () => Interlocked.Increment(ref raised);

        var r = await svc.IngestPerformanceAsync(Telemetry("vid-001", hook: 78, completion: 60, ctr: 9));

        Assert.True(r.IsBreakoutSuccess);
        Assert.False(r.RecordedMistakePenalty);
        Assert.InRange(r.XpDelta, PlutoClosedLoopReinforcementService.MinXp, PlutoClosedLoopReinforcementService.MaxXp);
        Assert.InRange(r.PatternWeightMultiplier, 1.15, 1.35);
        Assert.Equal(PlutoClosedLoopReinforcementService.XpFor((78 - 65) / 35.0), r.XpDelta);   // 최강 신호 = 3초 유지율
        Assert.Equal(r.XpDelta, learning.CurrentXP);
        Assert.Equal(r.PatternWeightMultiplier, learning.GetPatternWeight("hook:0.5초 휩줌 훅"), 9);
        Assert.Equal(r.PatternWeightMultiplier, learning.GetPatternWeight("look:GoldenHourGlow"), 9);
        Assert.Equal(r.PatternWeightMultiplier, learning.GetPatternWeight("bgm:Lullaby"), 9);
        Assert.Equal(r.PatternWeightMultiplier, learning.GetPatternWeight("style:#babyreels"), 9);
        Assert.Equal(PlutoLearningService.PatternBaseWeight, learning.GetPatternWeight("hook:unknown"));
        Assert.Contains("돌파", r.SummaryMessage, StringComparison.Ordinal);
        Assert.Same(r, svc.LastOutcome);
        Assert.Equal(1, raised);

        // alreadyRegistered — 같은 플랫폼+영상 재반영 차단(XP 불변) · 장부 영속 → 새 인스턴스에서도 차단
        var dup = await svc.IngestPerformanceAsync(Telemetry("vid-001", hook: 99, completion: 99, ctr: 30));
        Assert.True(dup.AlreadyRegistered);
        Assert.Equal(0, dup.XpDelta);
        Assert.Equal(r.XpDelta, learning.CurrentXP);
        using (var again = new PlutoClosedLoopReinforcementService(learning, notebook, storage))
            Assert.True((await again.IngestPerformanceAsync(Telemetry("vid-001", 90, 90, 20))).AlreadyRegistered);

        // 최대 신호 → 1.35 · 35 XP, 반복 부스트는 2.5 상한
        for (var i = 0; i < 6; i++)
        {
            var max = await svc.IngestPerformanceAsync(Telemetry("vid-max-" + i.ToString(CultureInfo.InvariantCulture), 100, 100, 15));
            Assert.Equal(PlutoClosedLoopReinforcementService.MaxBoost, max.PatternWeightMultiplier, 9);
            Assert.Equal(PlutoClosedLoopReinforcementService.MaxXp, max.XpDelta);
        }
        Assert.Equal(PlutoLearningService.PatternMaxWeight, learning.GetPatternWeight("hook:0.5초 휩줌 훅"), 9);
        Assert.Equal(r.XpDelta + 6 * PlutoClosedLoopReinforcementService.MaxXp, learning.CurrentXP);

        // 원자 영속 → 재적재 시 가중치 · XP 보존
        using var reloaded = new PlutoLearningService(learningPath);
        await reloaded.EnsureLoadedAsync();
        Assert.Equal(PlutoLearningService.PatternMaxWeight, reloaded.GetPatternWeight("hook:0.5초 휩줌 훅"), 9);
        Assert.Equal(learning.CurrentXP, reloaded.CurrentXP);
    }

    [Fact]
    public async Task ClosedLoopReinforcement_PoorHookRetention_RecordsMistakeNotebookPenalty()
    {
        using var learning = new PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var svc = new PlutoClosedLoopReinforcementService(learning, notebook, Storage());

        // 경미한 부진 → 결손 비례 벌점(차단 임계 미만)
        var mild = await svc.IngestPerformanceAsync(Telemetry("vid-mild", hook: 38, completion: 30, ctr: 3.0, hookStyle: "느린 페이드 인트로"));
        Assert.False(mild.IsBreakoutSuccess);
        Assert.True(mild.RecordedMistakePenalty);
        Assert.Equal(0, mild.XpDelta);
        Assert.Equal(PlutoClosedLoopReinforcementService.PenaltyFor((42 - 38) / 42.0), mild.PenaltySeverity, 9);
        Assert.InRange(mild.PenaltySeverity, 1.0, 1.2);
        Assert.Equal(mild.PenaltySeverity, notebook.GetEffectivePenalty("느린 페이드 인트로"), 3);
        Assert.DoesNotContain("느린 페이드 인트로", notebook.GetSuppressedTokens());

        // 심각한 부진(3초 5% · CTR 0.2%) → 벌점 ≥ 1.8 → 즉시 차단 · 부정 제약 프롬프트 반영
        var severe = await svc.IngestPerformanceAsync(Telemetry("vid-severe", hook: 5, completion: 10, ctr: 0.2, hookStyle: "정적 와이드 오프닝"));
        Assert.True(severe.RecordedMistakePenalty);
        Assert.True(severe.PenaltySeverity >= PlutoMistakeNotebookService.BlockThreshold, severe.PenaltySeverity.ToString(CultureInfo.InvariantCulture));
        Assert.Contains("정적 와이드 오프닝", notebook.GetSuppressedTokens());
        Assert.Contains("#첫걸음", notebook.GetSuppressedTokens());
        Assert.Contains("정적 와이드 오프닝", notebook.BuildNegativeConstraintPrompt(5), StringComparison.Ordinal);
        Assert.Contains("오답 노트", severe.SummaryMessage, StringComparison.Ordinal);
        Assert.Equal(0, learning.CurrentXP);
        Assert.Equal(PlutoLearningService.PatternBaseWeight, learning.GetPatternWeight("hook:정적 와이드 오프닝"));

        // 평균 성과 → 변경 없음
        var flat = await svc.IngestPerformanceAsync(Telemetry("vid-flat", hook: 50, completion: 40, ctr: 4));
        Assert.False(flat.IsBreakoutSuccess);
        Assert.False(flat.RecordedMistakePenalty);
        Assert.Equal(1.0, flat.PatternWeightMultiplier);

        // 돌파 우선(CTR 돌파 + 3초 부진 혼합) → 벌점 없음
        var (breakout, _, poor, _) = PlutoClosedLoopReinforcementService.Evaluate(Telemetry("mixed", hook: 30, completion: 20, ctr: 8));
        Assert.True(breakout);
        Assert.False(poor);
    }

    [Fact]
    public void MultiFormatPackager_VerticalSmartCrop_CentersFocalPointAndClampsBounds()
    {
        var p = new PlutoMultiFormatPackager(null, null);
        const double target = 9.0 / 16.0;
        foreach (var (w, h) in new[] { (1920, 1080), (1440, 1080), (4032, 3024), (1080, 2400), (1080, 1920) })
        {
            foreach (var f in new[] { new FocalPoint(0.05, 0.05), new FocalPoint(0.95, 0.95), new FocalPoint(0.5, 0.5), new FocalPoint(0.3, 0.7), new FocalPoint(double.NaN, 2.0) })
            {
                var r = p.ComputeVerticalSmartCropRect(w, h, f);
                Assert.InRange(r.Width, 0.0, 1.0);
                Assert.InRange(r.Height, 0.0, 1.0);
                Assert.InRange(r.X, 0.0, 1.0 - r.Width + 1e-12);
                Assert.InRange(r.Y, 0.0, 1.0 - r.Height + 1e-12);
                Assert.True(r.Width == 1.0 || r.Height == 1.0, "최대 면적 유지 위반");
                Assert.Equal(target, r.Width * w / (r.Height * h), 6);   // 픽셀 기준 9:16

                // 초점이 클램프 범위 내면 정확히 중앙
                var fx = double.IsFinite(f.X) ? Math.Clamp(f.X, 0, 1) : 0.5;
                if (fx - r.Width / 2 >= 0 && fx + r.Width / 2 <= 1) Assert.Equal(fx, r.X + r.Width / 2, 9);

                var px = PlutoMultiFormatPackager.ToPixelRect(r, w, h);
                Assert.True(px.X >= 0 && px.Y >= 0 && px.X + px.Width <= w && px.Y + px.Height <= h, "픽셀 경계 이탈");
                Assert.Equal(0, px.Width % 2);
                Assert.Equal(0, px.Height % 2);
            }
        }

        // 16:9 극단 초점 → 좌/우 경계에 정확히 붙음
        var tl = PlutoMultiFormatPackager.ComputeCrop(1920, 1080, new FocalPoint(0.05, 0.05));
        Assert.Equal(0.0, tl.X);
        Assert.Equal(0.0, tl.Y);
        Assert.Equal(target / (16.0 / 9.0), tl.Width, 12);
        Assert.Equal(1.0, tl.Height);
        var br = PlutoMultiFormatPackager.ComputeCrop(1920, 1080, new FocalPoint(0.95, 0.95));
        Assert.Equal(1.0 - br.Width, br.X, 12);
        Assert.Equal(0.0, br.Y);

        // 4:3 극단 초점
        var tl43 = PlutoMultiFormatPackager.ComputeCrop(1440, 1080, new FocalPoint(0.05, 0.05));
        Assert.Equal(0.421875, tl43.Width, 12);
        Assert.Equal(0.0, tl43.X);
        var br43 = PlutoMultiFormatPackager.ComputeCrop(1440, 1080, new FocalPoint(0.95, 0.95));
        Assert.Equal(1.0 - 0.421875, br43.X, 12);

        // 세로로 더 긴 원본 → 높이만 축소 · Y 클램프
        var tall = PlutoMultiFormatPackager.ComputeCrop(1080, 2400, new FocalPoint(0.5, 0.98));
        Assert.Equal(1.0, tall.Width);
        Assert.Equal(1.0 - tall.Height, tall.Y, 12);

        // 초점 재매핑 · 그래프 주입
        var remap = PlutoMultiFormatPackager.RemapFocal((0.5, 0.5), PlutoMultiFormatPackager.ComputeCrop(1920, 1080, new FocalPoint(0.5, 0.5)));
        Assert.Equal(0.5, remap.X, 9);
        Assert.Equal(0.5, remap.Y, 9);
        var g = PlutoMultiFormatPackager.InjectSourceCrops("[0:v]scale=1[v0];[1:v]scale=1[v1]", new PixelCropRect?[] { new PixelCropRect(10, 0, 606, 1080), null });
        Assert.Equal("[0:v]crop=606:1080:10:0,scale=1[v0];[1:v]scale=1[v1]", g);

        Assert.Throws<ArgumentOutOfRangeException>(() => p.ComputeVerticalSmartCropRect(0, 1080, new FocalPoint(0.5, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.ComputeVerticalSmartCropRect(1920, 1080, new FocalPoint(0.5, 0.5), double.NaN));
    }

    private sealed class FakeNotebook : IPlutoMistakeNotebookService
    {
        public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = 1.0, string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordPositiveReliefAsync(IEnumerable<string> tokens, double reliefWeight = 1.2, CancellationToken ct = default) => Task.CompletedTask;
        public double GetEffectivePenalty(string token) => Blocked.Contains(token) ? 2.0 : 0.0;
        public IReadOnlySet<string> GetSuppressedTokens() => Blocked;
        public string BuildNegativeConstraintPrompt(int maxTokens = 3) => Blocked.Count == 0 ? "" : PlutoMistakeNotebookService.ConstraintPrefix + string.Join(", ", Blocked);
        public Task ClearAllAsync(CancellationToken ct = default) { Blocked.Clear(); return Task.CompletedTask; }
#pragma warning disable CS0067
        public event Action? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public string? Response { get; init; }
        public string LastUser = "";
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public async Task MultiFormatPackager_GeneratesPublishingKit_WithTitlesCaptionAndHashtags()
    {
        var nb = new FakeNotebook();
        nb.Blocked.Add("육아스타그램");   // '#' 없이 기록된 차단 토큰도 매칭
        nb.Blocked.Add("#viral");
        var cues = new[] { SubtitleCue.Precise(0, 4.2, "첫 걸음마, 드디어!", "First steps, finally!"), SubtitleCue.Precise(4.2, 9.8, "넘어져도 다시", "Up again") };
        var fixedNow = new DateTime(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);   // 수요일 KST 12:00

        // ① 오프라인(LLM 없음) → 큐레이션 템플릿
        var offline = new PlutoMultiFormatPackager(null, nb, () => fixedNow);
        var kit = await offline.GeneratePublishingKitAsync("아기 첫걸음 성장일기", new[] { "#첫걸음", "#viral" }, cues);
        Assert.False(kit.UsedLlm);
        Assert.Equal(PlutoMultiFormatPackager.TitleCount, kit.Titles.Count);
        Assert.Equal(kit.Titles.Count, kit.Titles.Distinct().Count());
        Assert.Contains("첫 걸음마", kit.Titles[0], StringComparison.Ordinal);   // 3막 Hook 자막 → 첫 제목
        Assert.False(string.IsNullOrWhiteSpace(kit.CaptionKo));
        Assert.False(string.IsNullOrWhiteSpace(kit.CaptionEn));
        Assert.Equal(PlutoMultiFormatPackager.HashtagCount, kit.Hashtags.Count);
        Assert.All(kit.Hashtags, t => { Assert.StartsWith("#", t, StringComparison.Ordinal); Assert.DoesNotContain(" ", t, StringComparison.Ordinal); });
        Assert.Equal(kit.Hashtags.Count, kit.Hashtags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("#첫걸음", kit.Hashtags[0]);
        Assert.DoesNotContain(kit.Hashtags, t => t.Equals("#육아스타그램", StringComparison.OrdinalIgnoreCase) || t.Equals("#viral", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("KST", kit.RecommendedPostingWindow, StringComparison.Ordinal);
        Assert.Contains("#첫걸음", kit.FullText, StringComparison.Ordinal);

        // ② LLM 응답 → 스키마 검증 · 차단 태그 제거 · 12개 보충
        var llm = new FakeLlm
        {
            Response = "{\"titles\":[\"👣 첫 걸음마 3초 전\",\"🥹 엄마 울린 한 걸음\",\"✨ 15초 성장 기적\"],\"captionKo\":\"오늘 드디어 첫 걸음! 👣\",\"captionEn\":\"First steps today! 👣\"," +
                       "\"hashtags\":[\"#첫걸음마\",\"#육아스타그램\",\"#babyfirststeps\",\"#성장기록\",\"#viral\",\"#momlife\",\"#아기일상\"],\"postingWindow\":\"21:00–22:00 KST\"}",
        };
        var online = new PlutoMultiFormatPackager(llm, nb, () => fixedNow);
        var ai = await online.GeneratePublishingKitAsync("아기 첫걸음 성장일기", Array.Empty<string>(), cues);
        Assert.True(ai.UsedLlm);
        Assert.Equal("👣 첫 걸음마 3초 전", ai.Titles[0]);
        Assert.Equal(3, ai.Titles.Count);
        Assert.Equal("오늘 드디어 첫 걸음! 👣", ai.CaptionKo);
        Assert.Equal("21:00–22:00 KST", ai.RecommendedPostingWindow);
        Assert.Equal(PlutoMultiFormatPackager.HashtagCount, ai.Hashtags.Count);
        Assert.Equal("#첫걸음마", ai.Hashtags[0]);
        Assert.DoesNotContain(ai.Hashtags, t => t.Equals("#육아스타그램", StringComparison.OrdinalIgnoreCase) || t.Equals("#viral", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Avoid motifs/styles", llm.LastUser, StringComparison.Ordinal);   // 오답 노트 부정 제약 전달
        Assert.Contains("첫 걸음마, 드디어!", llm.LastUser, StringComparison.Ordinal);

        // ③ 스키마 불일치 → 템플릿 폴백(예외 없음)
        var broken = new PlutoMultiFormatPackager(new FakeLlm { Response = "{\"titles\":[]}" }, nb, () => fixedNow);
        var fb = await broken.GeneratePublishingKitAsync("강아지 산책", new[] { "#산책" });
        Assert.False(fb.UsedLlm);
        Assert.Equal(12, fb.Hashtags.Count);
        Assert.Contains("🐾", fb.Titles[0], StringComparison.Ordinal);
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
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
    public async Task MontageExport_WithLockedPlan_PreservesExactClipCutBoundaries()
    {
        var photos = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(_dir, "cut" + i.ToString(CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(p, PngHeader(1920, 1080));
            photos.Add(p);
        }
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var outDir = Path.Combine(_dir, "out");
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        StockSuiteExporter NewExporter() => new(() => null, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = Path.Combine(_dir, "sfx"),
            MasteringTempDir = Path.Combine(_dir, "master"),
        };

        // 프리뷰가 커스텀 BGM 비트(4.9 · 10.15)로 스냅한 플랜 — 렌더 측 비트 재검출과 다름
        var full = photos.Select(Path.GetFullPath).ToArray();
        var focals = new (double X, double Y)[] { (0.2, 0.4), (0.5, 0.5), (0.85, 0.3) };
        var locked = PlutoMontageCompiler.Shared.CompileMontagePlan(full, focals, new[] { 4.9, 10.15 }, null, PlutoMotionCompiler.FourActDurationSec);
        var unlocked = PlutoMontageCompiler.Shared.CompileMontagePlan(full, focals, null, null, PlutoMotionCompiler.FourActDurationSec);
        Assert.NotEqual(locked.TransitionOffsetsSec, unlocked.TransitionOffsetsSec);

        var exp = NewExporter();
        var r = await exp.ExportMontageVideoFromPhotosAsync(photos, focals, new ExportOptions { OutputDirectory = outDir }, lockedPlan: locked);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("🔒 타임라인 잠금", r.Message, StringComparison.Ordinal);
        var plan = exp.LastMontagePlan!;
        Assert.Equal(locked.TransitionOffsetsSec, plan.TransitionOffsetsSec);   // 0ms 드리프트(정확 일치)
        Assert.Equal(locked.CutPointsSec, plan.CutPointsSec);
        Assert.Equal(locked.TransitionSec, plan.TransitionSec);
        Assert.Equal(locked.TotalDurationSec, plan.TotalDurationSec);
        for (var i = 0; i < locked.Clips.Count; i++)
        {
            Assert.Equal(locked.Clips[i].StartSec, plan.Clips[i].StartSec);
            Assert.Equal(locked.Clips[i].DurationSec, plan.Clips[i].DurationSec);
            Assert.Equal(locked.Clips[i].Frames, plan.Clips[i].Frames);
        }
        var graph = calls[0][Array.IndexOf(calls[0], "-filter_complex") + 1];
        Assert.Equal(plan.FilterComplexGraph, graph);
        foreach (var o in locked.TransitionOffsetsSec)
            Assert.Contains(":offset=" + o.ToString("0.000", CultureInfo.InvariantCulture) + "[", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("crop=606", graph, StringComparison.Ordinal);

        // 📱 9:16 스마트 크롭 + 잠금 — 경계 유지 · 입력별 crop 선삽입(1920×1080 → 606×1080)
        calls.Clear();
        var v = await NewExporter().ExportMontageVideoFromPhotosAsync(photos, focals, new ExportOptions { OutputDirectory = Path.Combine(_dir, "out916") }, lockedPlan: locked, vertical9x16SmartCrop: true);
        Assert.True(v.Ok, v.Message);
        Assert.Contains("📱 9:16 스마트 크롭", v.Message, StringComparison.Ordinal);
        var vg = calls[0][Array.IndexOf(calls[0], "-filter_complex") + 1];
        Assert.Equal(3, CountOf(vg, ":v]crop=606:1080:"));
        Assert.Contains("[0:v]crop=606:1080:81:0,scale=", vg, StringComparison.Ordinal);      // fx 0.2 → 중심 384px − 606/2 = 81(짝수폭 재중심)
        Assert.Contains("[2:v]crop=606:1080:1314:0,scale=", vg, StringComparison.Ordinal);    // fx 0.85 → 우측 경계 클램프(1920 − 606)
        foreach (var o in locked.TransitionOffsetsSec)
            Assert.Contains(":offset=" + o.ToString("0.000", CultureInfo.InvariantCulture) + "[", vg, StringComparison.Ordinal);

        // 손상 잠금 플랜(클립 수 불일치) → 재계산 폴백(예외 없음)
        var two = PlutoMontageCompiler.Shared.CompileMontagePlan(full.Take(2).ToArray(), null, null, null);
        var fb = await NewExporter().ExportMontageVideoFromPhotosAsync(photos, focals, new ExportOptions { OutputDirectory = Path.Combine(_dir, "outfb") }, lockedPlan: two);
        Assert.True(fb.Ok, fb.Message);
        Assert.DoesNotContain("🔒", fb.Message, StringComparison.Ordinal);

        // 컴파일러 직접 — 비단조 offset → ArgumentException
        var bad = locked with { TransitionOffsetsSec = new[] { 8.0, 4.0 } };
        Assert.Throws<ArgumentException>(() => PlutoMontageCompiler.Shared.CompileLockedMontagePlan(bad, full, null, null));
    }
}
