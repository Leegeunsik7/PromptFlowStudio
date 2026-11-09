using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public sealed class BabyBgmAndGrowthBadgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_babybgm_" + Guid.NewGuid().ToString("N"));

    public BabyBgmAndGrowthBadgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (StockSuiteExporter Exp, List<string[]> Calls, string Src, string Target) Setup()
    {
        var src = Path.Combine(_dir, "render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var bgm = Path.Combine(_dir, "musicbox.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner) { BabyBgmResolver = (_, _) => bgm };
        return (exp, calls, src, target);
    }

    [Fact]
    public async Task StockSuiteExporter_MuxesBabyBgm_WhenCanvasBgmToggleEnabled()
    {
        var (exp, calls, src, target) = Setup();
        exp.BabyBgm = BabyBgmTrack.MusicBox;
        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        var outPath = await exp.TryMuxBabyBgmAsync(src, 10);

        Assert.NotNull(outPath);
        Assert.EndsWith("render_baby_bgm.mp4", outPath, StringComparison.Ordinal);
        var mux = calls[^1];
        Assert.Contains(mux, a => a.EndsWith("musicbox.wav", StringComparison.Ordinal));
        Assert.Contains("1:a:0", mux);
        var af = mux[Array.IndexOf(mux, "-af") + 1];
        Assert.Contains("loudnorm=I=-14", af, StringComparison.Ordinal);
        Assert.Contains("volume=0.25", af, StringComparison.Ordinal);
        Assert.Contains("afade=t=in", af, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=8.5", af, StringComparison.Ordinal);
        Assert.DoesNotContain("-an", mux);
        // 🔇 StockSuite 에셋 자체는 Silent Ambient Guard 유지
        Assert.Contains("-an", calls[0]);
        Assert.False(File.Exists(Path.Combine(target, "render_baby_bgm.mp4")));
    }

    [Fact]
    public async Task StockSuiteExporter_SkipsBabyBgm_WhenToggleOff()
    {
        var (exp, calls, src, _) = Setup();
        exp.BabyBgm = null;
        Assert.Null(await exp.TryMuxBabyBgmAsync(src, 10));
        Assert.Empty(calls);
    }

    [Fact]
    public void Workspace_GrowthMilestoneBadge_GeneratesDynamicOverlayText()
    {
        var today = new DateTime(2026, 11, 9);
        Assert.Equal("🍼 백일 & 터미타임 · D+100", StockSuiteExporter.FormatGrowthBadgeText(BabyGrowthStage.HundredDays, null, today));
        Assert.Equal("🎂 첫돌 축하 · 우리아기 첫돌", StockSuiteExporter.FormatGrowthBadgeText(BabyGrowthStage.FirstBirthday, "  우리아기 첫돌 ", today));
        Assert.Equal("👶 신생아 · D+30", StockSuiteExporter.FormatGrowthBadgeText(BabyGrowthStage.Newborn, "", today));
        Assert.Equal("🏷️ 우리 아기 성장 기록 · 2026.11.09", StockSuiteExporter.FormatGrowthBadgeText(null, null, today));

        var f = StockSuiteExporter.BuildGrowthBadgeFilter("🍼 백일 & 터미타임 · D+100", null);
        Assert.NotNull(f);
        Assert.StartsWith("drawtext=text='백일 & 터미타임 · D+100'", f, StringComparison.Ordinal);
        Assert.Contains("box=1:boxcolor=white", f, StringComparison.Ordinal);
        Assert.Contains(":x=48:y=h-th-48", f, StringComparison.Ordinal);
        Assert.Null(StockSuiteExporter.BuildGrowthBadgeFilter("  ", null));
    }

    [Fact]
    public async Task StockSuiteExporter_BurnsGrowthBadge_IntoSingleExportFilter()
    {
        var (exp, calls, src, _) = Setup();
        exp.GrowthBadgeText = "🎂 첫돌 축하 · D+365";
        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        var vf = calls[0][Array.IndexOf(calls[0], "-vf") + 1];
        Assert.Contains("첫돌 축하 · D+365", vf, StringComparison.Ordinal);
        Assert.Contains(StockSuiteExporter.GrowthBadgeTag, r.Message, StringComparison.Ordinal);
    }
}
