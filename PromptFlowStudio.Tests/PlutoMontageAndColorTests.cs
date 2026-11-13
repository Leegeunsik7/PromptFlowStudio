using System;
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

/// <summary>🎞️🎨 멀티컷 몽타주 컴파일러 · 색 분석/정합 · FFmpeg 색보정 필터 · 익스포터 다중 입력 그래프.</summary>
public sealed class PlutoMontageAndColorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_montage_" + Guid.NewGuid().ToString("N"));

    public PlutoMontageAndColorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => "photo" + i.ToString(CultureInfo.InvariantCulture) + ".jpg").ToArray();

    private sealed class FakeNotebook : IPlutoMistakeNotebookService
    {
        public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = 1.0, string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordPositiveReliefAsync(IEnumerable<string> tokens, double reliefWeight = 1.2, CancellationToken ct = default) => Task.CompletedTask;
        public double GetEffectivePenalty(string token) => Blocked.Contains(token) ? 2.0 : 0.0;
        public IReadOnlySet<string> GetSuppressedTokens() => Blocked;
        public string BuildNegativeConstraintPrompt(int maxTokens = 3) => "";
        public Task ClearAllAsync(CancellationToken ct = default) { Blocked.Clear(); return Task.CompletedTask; }
#pragma warning disable CS0067
        public event Action? Changed;
#pragma warning restore CS0067
    }

    [Fact]
    public void MontageCompiler_EnforcesExact15SecondDuration_Across2To5Clips()
    {
        var beats = new List<double>();
        for (var t = 0.13; t < 15; t += 0.47) beats.Add(t);
        foreach (var beatSet in new IReadOnlyList<double>?[] { null, beats, new[] { 3.3, 7.77, 11.1 } })
            for (var n = 2; n <= 5; n++)
            {
                var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(n), null, beatSet, null);
                Assert.Equal(n, plan.Clips.Count);
                var d = plan.TransitionSec;
                Assert.Equal(0.35, d, 9);
                var sum = plan.Clips.Sum(c => c.DurationSec);
                Assert.InRange(sum - (n - 1) * d, 15.0 - 0.001, 15.0 + 0.001);
                Assert.Equal(15.0, plan.TotalDurationSec);

                var offs = plan.TransitionOffsetsSec;
                Assert.Equal(n - 1, offs.Count);
                for (var i = 0; i < offs.Count; i++)
                {
                    Assert.True(offs[i] > (i == 0 ? 0 : offs[i - 1]), "xfade offset 순증가 위반");
                    Assert.Equal(plan.Clips[i + 1].StartSec, offs[i], 9);
                }
                foreach (var c in plan.Clips)
                {
                    Assert.True(c.DurationSec >= PlutoMontageCompiler.MinClipSec - 1e-9, "최소 클립 길이 위반");
                    Assert.Equal(c.DurationSec * 60, c.Frames, 6);   // 프레임 그리드 정렬
                    Assert.Equal(c.Frames, PlutoMotionCompiler.TotalFrames(c.Trajectory));
                }
                // 카메라 벡터 순환: Push-In(줌 증가) → Drift → Pull-Back(줌 감소)
                Assert.True(plan.Clips[0].Trajectory.Segments[0].EndZoom > plan.Clips[0].Trajectory.Segments[0].StartZoom);
                if (n >= 3) Assert.True(plan.Clips[2].Trajectory.Segments[0].EndZoom < plan.Clips[2].Trajectory.Segments[0].StartZoom);

                var g = plan.FilterComplexGraph;
                for (var i = 0; i < n; i++) Assert.Contains("[" + i.ToString(CultureInfo.InvariantCulture) + ":v]scale=", g, StringComparison.Ordinal);
                Assert.Equal(n - 1, CountOf(g, "xfade=transition="));
                Assert.Contains("[v0][v1]xfade=transition=fade:duration=0.350:offset=" + offs[0].ToString("0.000", CultureInfo.InvariantCulture), g, StringComparison.Ordinal);
                Assert.EndsWith("[vout]", g, StringComparison.Ordinal);
                Assert.Equal("vout", plan.FinalVideoPadLabel);
                Assert.Equal(1, CountOf(g, "[vout]"));
            }

        Assert.Throws<ArgumentException>(() => PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(1), null, null, null));
        Assert.Throws<ArgumentException>(() => PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(6), null, null, null));
    }

    private static int CountOf(string s, string token)
    {
        int c = 0, i = 0;
        while ((i = s.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { c++; i += token.Length; }
        return c;
    }

    [Fact]
    public void MontageCompiler_SnapsCutBoundaries_ToMusicalBeats()
    {
        // N=3 균등 컷 중앙 ≈ 5.058 · 9.942 → ±0.25 내 비트 5.20 · 9.80 로 스냅.
        var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(3), null, new[] { 1.0, 5.20, 9.80, 14.0 }, null);
        Assert.Equal(2, plan.SnappedCuts);
        Assert.InRange(plan.CutPointsSec[0], 5.20 - 0.02, 5.20 + 0.02);
        Assert.InRange(plan.CutPointsSec[1], 9.80 - 0.02, 9.80 + 0.02);

        // N=4 · 모든 내부 컷이 비트 격자에 정렬.
        var grid = new List<double>();
        for (var t = 0.0; t < 15; t += 0.5) grid.Add(t);
        var p4 = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(4), null, grid, null);
        Assert.Equal(3, p4.SnappedCuts);
        foreach (var c in p4.CutPointsSec) Assert.True(grid.Any(b => Math.Abs(b - c) <= 0.02), "컷 " + c.ToString(CultureInfo.InvariantCulture) + " 비트 미정렬");

        // 허용 오차(±0.25s) 밖 비트 → 스냅 생략(균등 컷 유지 7.5s).
        var far = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(2), null, new[] { 4.0, 11.0 }, null);
        Assert.Equal(0, far.SnappedCuts);
        Assert.InRange(far.CutPointsSec[0], 7.5 - 0.02, 7.5 + 0.02);
    }

    [Fact]
    public void ColorAnalyzer_HarmonizesDisparateExposures_AndRespectsMistakeNotebook()
    {
        // 픽셀 통계: 어두운 프레임 · 역광(섀도 + 하이라이트 양봉) · 과노출 · 웜 편향.
        var dark = PlutoImageColorAnalyzer.AnalyzePixels(Enumerable.Repeat(Rgb(30, 28, 25), 256).ToArray());
        Assert.True(dark.IsLowLight);
        Assert.InRange(dark.MeanLuminance, 0.0, 0.15);
        var backlitPx = Enumerable.Repeat(Rgb(8, 8, 8), 128).Concat(Enumerable.Repeat(Rgb(250, 250, 250), 128)).ToArray();
        var backlit = PlutoImageColorAnalyzer.AnalyzePixels(backlitPx);
        Assert.True(backlit.IsBacklit);
        Assert.True(backlit.ContrastStdDev > 0.4);
        var bright = PlutoImageColorAnalyzer.AnalyzePixels(Enumerable.Repeat(Rgb(240, 238, 236), 256).ToArray());
        Assert.False(bright.IsLowLight);
        var warm = PlutoImageColorAnalyzer.AnalyzePixels(Enumerable.Repeat(Rgb(200, 140, 80), 64).ToArray());
        Assert.True(warm.WarmCoolBias > 0.4);

        var analyzer = PlutoImageColorAnalyzer.Shared;
        var profiles = analyzer.BuildHarmonizedProfiles(new[] { dark, backlit, bright, PlutoImageColorMetrics.Neutral }, "GoldenHourWarm");
        Assert.Equal(4, profiles.Count);
        Assert.All(profiles, p => Assert.Equal(PlutoColorGradingCompiler.GoldenHourWarmName, p.PresetName));
        Assert.True(profiles[0].BrightnessOffset > 0, "어두운 프레임 리프트");
        Assert.True(profiles[1].BrightnessOffset > 0, "역광 섀도 리프트");
        Assert.True(profiles[2].BrightnessOffset < 0, "과노출 트림");
        Assert.All(profiles, p => Assert.InRange(p.BrightnessOffset, -0.12, 0.15));
        Assert.Equal(0.15, profiles[0].BrightnessOffset, 9);   // 상한 클램프
        Assert.Equal(-0.12, profiles[2].BrightnessOffset, 9);  // 하한 클램프
        Assert.Equal(0.0, profiles[3].BrightnessOffset, 9);    // 목표 휘도 = 보정 없음

        // 웜 편향 → 섀도 레드 감산(색온도 중화).
        var warmProfile = PlutoImageColorAnalyzer.Harmonize(warm, PlutoColorGradingCompiler.GoldenHourWarm);
        Assert.True(warmProfile.RedShadowShift < PlutoColorGradingCompiler.GoldenHourWarm.RedShadowShift);

        // 🛡️ 오답 노트 차단 룩 → NaturalPastel 폴백(별칭 토큰 포함).
        var nb = new FakeNotebook();
        nb.Blocked.Add("GoldenHourWarm");
        Assert.All(analyzer.BuildHarmonizedProfiles(new[] { dark, bright }, "GoldenHourWarm", nb), p => Assert.Equal(PlutoColorGradingCompiler.NaturalPastelName, p.PresetName));
        nb.Blocked.Clear();
        nb.Blocked.Add("16mm");
        Assert.Equal(PlutoColorGradingCompiler.NaturalPastelName, analyzer.BuildHarmonizedProfiles(new[] { dark }, "VintageFilm16mm", nb)[0].PresetName);
        Assert.Equal(PlutoColorGradingCompiler.GoldenHourWarmName, analyzer.BuildHarmonizedProfiles(new[] { dark }, "GoldenHourWarm", nb)[0].PresetName);
        Assert.Equal(PlutoColorGradingCompiler.NaturalPastelName, analyzer.BuildHarmonizedProfiles(new[] { dark }, "unknown-look")[0].PresetName);

        // 룩 선택: 바이럴 태그 우선 · 무신호 → 디렉터 레벨.
        Assert.Equal("GoldenHourWarm", PlutoColorGradingCompiler.SelectLook(new[] { "#노을산책" }, 1));
        Assert.Equal("VintageFilm16mm", PlutoColorGradingCompiler.SelectLook(new[] { "retro vlog" }, 1));
        Assert.Equal("VintageFilm16mm", PlutoColorGradingCompiler.SelectLook(null, 3));
        Assert.Equal("NaturalPastel", PlutoColorGradingCompiler.SelectLook(Array.Empty<string>(), 1));

        // 판독 불가 파일 → 중립 기준.
        var junk = Path.Combine(_dir, "junk.jpg");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
        Assert.Same(PlutoImageColorMetrics.Neutral, analyzer.AnalyzeImage(junk));
        Assert.Same(PlutoImageColorMetrics.Neutral, analyzer.AnalyzeImage(Path.Combine(_dir, "missing.jpg")));
    }

    private static int Rgb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    [Fact]
    public void ColorGradingCompiler_FormatsInvariantFfmpegFilter()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // 쉼표 소수 구분자 환경
            var golden = PlutoColorGradingCompiler.GoldenHourWarm with { BrightnessOffset = 0.04 };
            Assert.Equal("eq=brightness=0.040:contrast=1.060:saturation=1.120,colorbalance=rs=0.050:bs=-0.040:bh=-0.030,vignette=angle=PI/5",
                PlutoColorGradingCompiler.Compile(golden));

            var pastel = PlutoColorGradingCompiler.Compile(PlutoColorGradingCompiler.NaturalPastel with { BrightnessOffset = -0.0001 });
            Assert.StartsWith("eq=brightness=0.000:contrast=0.960:saturation=0.920,colorbalance=rs=0.000:bs=0.000:bh=0.020", pastel, StringComparison.Ordinal);
            Assert.DoesNotContain("vignette", pastel, StringComparison.Ordinal);

            var nan = PlutoColorGradingCompiler.Compile(new PlutoColorGradeProfile("x", double.NaN, double.PositiveInfinity, 9, -5, double.NaN, false));
            Assert.Equal("eq=brightness=0.000:contrast=1.000:saturation=3.000,colorbalance=rs=-1.000:bs=0.800:bh=0.000", nan);

            // 몽타주 그래프에도 클립별 색보정이 그대로 삽입.
            var plan = PlutoMontageCompiler.Shared.CompileMontagePlan(Paths(2), null, null, new[] { golden, PlutoColorGradingCompiler.NaturalPastel });
            Assert.Contains("fps=60," + PlutoColorGradingCompiler.Compile(golden) + ",format=yuv420p,setpts=PTS-STARTPTS[v0]", plan.FilterComplexGraph, StringComparison.Ordinal);
            Assert.DoesNotContain("0,350", plan.FilterComplexGraph, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public async Task StockSuiteExporter_MontageMethod_BuildsMultiInputFfmpegGraph()
    {
        var photos = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(_dir, "shot" + i.ToString(CultureInfo.InvariantCulture) + ".jpg");
            File.WriteAllBytes(p, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            photos.Add(p);
        }
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var outDir = Path.Combine(_dir, "out");
        var calls = new List<string[]>();
        var failSubtitles = true;
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            if (failSubtitles && arr.Any(a => a.Contains("drawtext", StringComparison.Ordinal))) return Task.FromResult(false);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var nb = new FakeNotebook();
        nb.Blocked.Add("VintageFilm16mm");
        var exp = new StockSuiteExporter(() => null, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = Path.Combine(_dir, "sfx"),
            MasteringTempDir = Path.Combine(_dir, "master"),
            MistakeNotebook = nb,
        };
        var cues = new[] { new SubtitleCue(1, 4, "안녕, 우리 아기", "Hello, my little one") };

        var r = await exp.ExportMontageVideoFromPhotosAsync(photos, null, new ExportOptions { OutputDirectory = outDir }, cues, preferredLook: "VintageFilm16mm");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(Path.Combine(outDir, "shot0_montage.mp4"), r.TargetPath);
        Assert.True(File.Exists(r.TargetPath));
        Assert.Contains(StockSuiteExporter.MontageTag + "3컷 · 900프레임 @60fps · 🎨 NaturalPastel", r.Message, StringComparison.Ordinal);
        Assert.Contains("⚠ 자막 생략", r.Message, StringComparison.Ordinal);   // [부분 구제] 번인 실패 → 자막 없이 재시도
        Assert.True(r.HasAudio);
        Assert.False(exp.IsMontageExporting);
        Assert.NotNull(exp.LastMontagePlan);

        Assert.Equal(3, calls.Count);   // ① 자막 번인(실패) ② 재렌더 ③ BGM+SFX 믹스
        var burned = calls[0][Array.IndexOf(calls[0], "-filter_complex") + 1];
        Assert.Contains("[vmain];[vmain]drawtext=", burned, StringComparison.Ordinal);
        Assert.EndsWith("[vout]", burned, StringComparison.Ordinal);

        var render = calls[1];
        var inputs = render.Select((a, i) => (a, i)).Where(x => x.a == "-i").Select(x => render[x.i + 1]).ToArray();
        Assert.Equal(photos.Select(Path.GetFullPath).ToArray(), inputs);
        var graph = render[Array.IndexOf(render, "-filter_complex") + 1];
        Assert.Equal(exp.LastMontagePlan!.FilterComplexGraph, graph);
        Assert.Contains("[2:v]", graph, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(graph, "xfade="));
        Assert.Equal("[vout]", render[Array.IndexOf(render, "-map") + 1]);
        Assert.Equal("900", render[Array.IndexOf(render, "-frames:v") + 1]);
        Assert.Equal("15", render[Array.IndexOf(render, "-t") + 1]);
        Assert.Contains("-an", render);

        var mix = calls[2];
        Assert.Contains("amix=inputs=", mix[Array.IndexOf(mix, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Null(exp.BabyBgm);
        Assert.False(exp.IncludeSfx);

        // 범위 밖(1장) → 실패 결과(예외 없음).
        var one = await exp.ExportMontageVideoFromPhotosAsync(photos.Take(1).ToArray(), null, new ExportOptions { OutputDirectory = outDir });
        Assert.False(one.Ok);
    }
}
