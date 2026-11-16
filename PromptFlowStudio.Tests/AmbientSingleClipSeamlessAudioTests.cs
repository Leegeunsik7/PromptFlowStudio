using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🔁 15초 엠비언트 단일 클립 — 9:16 과 동일 0.3s in · 0.6s out 심리스 루프 오디오 경계 페이드.</summary>
public sealed class AmbientSingleClipSeamlessAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ambloop_" + Guid.NewGuid().ToString("N"));

    public AmbientSingleClipSeamlessAudioTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(StockSuiteExportResult R, string Mux, StockSuiteExporter Exp)> ExportAsync(string srcName)
    {
        var src = Path.Combine(_dir, srcName);
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            BabyBgm = BabyBgmTrack.Lullaby,
        };
        var r = await exp.ExportAsync(src);
        return (r, string.Join(" ", calls[^1]), exp);
    }

    [Fact]
    public void IsAmbientLoopClip_MatchesPlannerAmbientOutputOnly()
    {
        Assert.True(StockSuiteExporter.IsAmbientLoopClip(MultiDurationPlanner.BuildOutputPath(_dir, "돌잔치", VideoDurationMode.Sec15_AmbientSlow)));
        Assert.True(StockSuiteExporter.IsAmbientLoopClip(@"C:\x\Baby_AMBIENT15S.MP4"));
        Assert.False(StockSuiteExporter.IsAmbientLoopClip(MultiDurationPlanner.BuildOutputPath(_dir, "돌잔치", VideoDurationMode.Sec15_Triple)));
        Assert.False(StockSuiteExporter.IsAmbientLoopClip(null));
        Assert.False(StockSuiteExporter.IsAmbientLoopClip("  "));
    }

    [Fact]
    public async Task ExportAsync_AmbientSingleClip_AppliesSeamlessLoopFadesAndRestoresFlag()
    {
        var (r, mux, exp) = await ExportAsync("baby_ambient15s.mp4");

        Assert.True(r.Ok, r.Message);
        Assert.True(r.HasAudio);
        Assert.Contains("afade=t=in:st=0:d=0.3", mux, StringComparison.Ordinal);
        Assert.Contains("afade=t=out:st=14.40:d=0.6", mux, StringComparison.Ordinal);
        Assert.Contains(StockSuiteExporter.SeamlessLoopTag, r.Message, StringComparison.Ordinal);
        Assert.False(exp.SeamlessLoopAudio);   // 호출 범위 한정 → 복원
    }

    [Fact]
    public async Task ExportAsync_RegularClip_KeepsDefaultBgmFades()
    {
        var (r, mux, exp) = await ExportAsync("baby_15s.mp4");

        Assert.True(r.Ok, r.Message);
        Assert.Contains("afade=t=in:st=0:d=1.5", mux, StringComparison.Ordinal);
        Assert.DoesNotContain("d=0.3", mux, StringComparison.Ordinal);
        Assert.DoesNotContain(StockSuiteExporter.SeamlessLoopTag, r.Message, StringComparison.Ordinal);
        Assert.False(exp.SeamlessLoopAudio);
    }
}
