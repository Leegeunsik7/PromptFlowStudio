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
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner) { BabyBgmResolver = (_, _) => bgm, DurationProbe = (_, _) => Task.FromResult(10.0) };
        return (exp, calls, src, target);
    }

    [Fact]
    public async Task StockSuiteExporter_BabyStudio_MuxesAudioIntoPrimaryFile_WithoutSidecar()
    {
        var (exp, calls, src, target) = Setup();
        exp.BabyBgm = BabyBgmTrack.MusicBox;
        Assert.True(exp.IsBabyBgmEnabled);
        var r = await exp.ExportAsync(src);

        Assert.True(r.Ok, r.Message);
        Assert.True(r.HasAudio);
        Assert.Contains(StockSuiteExporter.BabyBgmTag, r.Message, StringComparison.Ordinal);
        var primary = Path.Combine(target, StockSuiteExporter.DefaultAssetName);
        Assert.Equal(primary, r.TargetPath);
        Assert.True(File.Exists(primary));
        // 1차 = 무음 HTS 인코딩 → 2차 = 주 산출물에 BGM 먹싱(비디오 + 오디오 스트림 · 가드 면제)
        Assert.Contains("-an", calls[0]);
        var mux = calls[^1];
        Assert.Equal(primary, mux[Array.IndexOf(mux, "-i") + 1]);
        Assert.Contains(mux, a => a.EndsWith("musicbox.wav", StringComparison.Ordinal));
        Assert.Contains("0:v:0", mux);
        Assert.Contains("1:a:0", mux);
        Assert.Equal("aac", mux[Array.IndexOf(mux, "-c:a") + 1]);
        Assert.DoesNotContain("-an", mux);
        // 동반 파일·임시 파일 잔존 0
        Assert.Empty(Directory.GetFiles(_dir, "*_baby_bgm*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(target, "*.~bgm"));
    }

    [Fact]
    public async Task StockSuiteExporter_CalculatesAudioFadeOut_BasedOnProbedDuration()
    {
        var (exp, calls, src, _) = Setup();
        string? probed = null;
        exp.DurationProbe = (p, _) => { probed = p; return Task.FromResult(12.34); };
        exp.BabyBgm = BabyBgmTrack.Lullaby;
        var r = await exp.ExportAsync(src);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(r.TargetPath, probed);
        var mux = calls[^1];
        var af = mux[Array.IndexOf(mux, "-af") + 1];
        Assert.Contains("loudnorm=I=-14", af, StringComparison.Ordinal);
        Assert.Contains("volume=0.25,afade=t=in:st=0:d=1.5,afade=t=out:st=10.84:d=1.5", af, StringComparison.Ordinal);
        Assert.Equal("12.34", mux[Array.IndexOf(mux, "-t") + 1]);
        // 짧은 영상 → 시작점 0 클램프 · 길이 미상 → 페이드 아웃 생략
        Assert.Contains("afade=t=out:st=0.00:d=1.5", StockSuiteExporter.BuildBabyBgmAudioFilter(1.0), StringComparison.Ordinal);
        Assert.DoesNotContain("afade=t=out", StockSuiteExporter.BuildBabyBgmAudioFilter(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockSuiteExporter_SkipsBabyBgm_WhenToggleOff()
    {
        var (exp, calls, src, _) = Setup();
        exp.BabyBgm = null;
        Assert.False(await exp.TryMuxBabyBgmIntoPrimaryAsync(src));
        Assert.Empty(calls);
        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        Assert.False(r.HasAudio);
        Assert.Single(calls);
        Assert.Contains("-an", calls[0]);
    }

    [Fact]
    public void ExportProfile_BabyCinematic_IsExemptFromSilentAmbientGuard()
    {
        var (exp, _, _, target) = Setup();
        var p = Path.Combine(target, "x.mp4");
        Assert.True(exp.IsStockSuiteTarget(p, ExportProfile.HtsDesktop));
        Assert.False(exp.IsStockSuiteTarget(p, ExportProfile.BabyCinematic));
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
