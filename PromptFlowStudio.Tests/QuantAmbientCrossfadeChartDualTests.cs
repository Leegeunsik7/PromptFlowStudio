using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Chart;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>정방향 크로스페이드 루프 · 티커 차트 스틸(PNG) · 듀얼 모니터 분할 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class QuantAmbientCrossfadeChartDualTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_qa_" + Guid.NewGuid().ToString("N"));

    public QuantAmbientCrossfadeChartDualTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AmbientLoopService_BuildsForwardCrossfadeFilter_WithoutReversingVideo()
    {
        var fc = AmbientLoopService.BuildForwardCrossfadeFilterGraph();
        Assert.Equal("[0:v]setpts=3.3*PTS,fps=30,split=2[v1][v2];[v1]trim=start=0:end=15.0,setpts=PTS-STARTPTS[main];"
                     + "[v2]trim=start=15.0:end=16.5,setpts=PTS-STARTPTS[tail];[tail][main]xfade=transition=fade:duration=1.5:offset=0[outv]", fc);
        Assert.DoesNotContain("reverse", fc);
        Assert.DoesNotContain("concat", fc);

        var args = AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4", LoopMode.ForwardCrossfade);
        Assert.Equal(fc, args[Array.IndexOf(args, "-filter_complex") + 1]);
        Assert.Equal("[outv]", args[Array.IndexOf(args, "-map") + 1]);
        Assert.Equal("15", args[Array.IndexOf(args, "-t") + 1]);
        Assert.Contains("-an", args);
        Assert.DoesNotContain("-vf", args);
        foreach (var a in args) Assert.DoesNotContain("reverse", a);

        // 기본 모드 = 정방향 크로스페이드 · 기존 모드 보존 · 익스포터 위임 동일
        Assert.Equal(LoopMode.ForwardCrossfade, AmbientLoopService.ParseLoopMode(null));
        Assert.Equal(LoopMode.PingPong, AmbientLoopService.ParseLoopMode("pingpong"));
        Assert.Contains("reverse", AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4", LoopMode.PingPong)[Array.IndexOf(AmbientLoopService.BuildAmbientArgs("in.mp4", "o.mp4", LoopMode.PingPong), "-filter_complex") + 1]);
        Assert.Equal(args, StockSuiteExporter.BuildForwardCrossfadeLoopArgs("in.mp4", "o.mp4"));
        Assert.Contains("minterpolate", AmbientLoopService.BuildForwardCrossfadeFilterGraph(highQuality: true));
    }

    [Fact]
    public async Task ChartStillGeneratorService_GeneratesValidChartPng_ForTicker()
    {
        var gen = new ChartStillGeneratorService { UtcNow = () => new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero) };
        var r = await gen.GenerateAsync("005930.ks", _dir);

        Assert.True(r.Ok, r.Message);
        Assert.NotNull(r.Path);
        Assert.True(File.Exists(r.Path));
        Assert.Contains("005930.KS", Path.GetFileName(r.Path));
        var png = File.ReadAllBytes(r.Path!);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(1280, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(720, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
        Assert.True(png.Length > 5000);

        // 결정성 — 동일 티커·일자 = 동일 픽셀, 다른 티커 = 다른 차트 · 임시 파일 잔여 없음
        var a = ChartStillGeneratorService.Render("AAPL", 640, 360, 90, ChartStillGeneratorService.StableSeed("AAPL", gen.UtcNow()));
        var b = ChartStillGeneratorService.Render("AAPL", 640, 360, 90, ChartStillGeneratorService.StableSeed("AAPL", gen.UtcNow()));
        var c = ChartStillGeneratorService.Render("NVDA", 640, 360, 90, ChartStillGeneratorService.StableSeed("NVDA", gen.UtcNow()));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Empty(Directory.GetFiles(_dir, "*.~pfs"));
        Assert.Equal("CHART", ChartStillGeneratorService.SanitizeTicker(" <>/ "));

        var empty = await gen.GenerateAsync("AAPL", "");
        Assert.False(empty.Ok);
    }

    [Fact]
    public async Task StockSuiteExporter_SplitsUltraWideVideo_IntoDualScreenAssets()
    {
        Assert.Equal("scale=3840:1080:force_original_aspect_ratio=increase:flags=lanczos,crop=3840:1080,crop=1920:1080:0:0", StockSuiteExporter.BuildDualMonitorCropFilter(0, 0));
        Assert.Equal("scale=3840:1080:force_original_aspect_ratio=increase:flags=lanczos,crop=3840:1080,crop=1920:1080:1920:0", StockSuiteExporter.BuildDualMonitorCropFilter(1, 0));
        Assert.EndsWith("crop=1920:1080:1960:0", StockSuiteExporter.BuildDualMonitorCropFilter(1, 40));
        Assert.StartsWith("scale=3880:1080", StockSuiteExporter.BuildDualMonitorCropFilter(1, 40));
        Assert.EndsWith("crop=1920:1080:0:0", StockSuiteExporter.BuildDualMonitorCropFilter(0, 40));
        Assert.Equal(StockSuiteExporter.MaxBezelGapPixels, StockSuiteExporter.ClampBezel(9999));

        var src = Path.Combine(_dir, "pano.mp4");
        File.WriteAllText(src, "v");
        var outDir = Path.Combine(_dir, "assets");
        var seen = new List<IReadOnlyList<string>>();
        var exp = new StockSuiteExporter(() => outDir, () => "webm", () => "ffmpeg", (_, a, _) =>
        {
            seen.Add(a);
            File.WriteAllBytes(a[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        });

        var r = await exp.ExportDualMonitorSplitAsync(src, 40);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(Path.Combine(Path.GetFullPath(outDir), "bg_screen_1.webm"), r.LeftPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(outDir), "bg_screen_2.webm"), r.RightPath);
        Assert.True(File.Exists(r.LeftPath) && File.Exists(r.RightPath));
        Assert.Equal(2, seen.Count);
        Assert.EndsWith("crop=1920:1080:0:0", seen[0][IndexOf(seen[0], "-vf") + 1]);
        Assert.EndsWith("crop=1920:1080:1960:0", seen[1][IndexOf(seen[1], "-vf") + 1]);
        Assert.Contains("libvpx-vp9", seen[0]);
        Assert.Contains("-an", seen[1]);
        Assert.Empty(Directory.GetFiles(outDir, "*.~pfs*"));

        Assert.NotNull(r.DisplayConfigPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(r.DisplayConfigPath!));
        Assert.Equal(40, doc.RootElement.GetProperty("bezelGapPixels").GetInt32());
        Assert.Equal("bg_screen_2.webm", doc.RootElement.GetProperty("screens")[1].GetProperty("asset").GetString());

        // 우측 실패 → [부분 구제] 좌측 보존 · 설정 미갱신
        var n = 0;
        var partial = new StockSuiteExporter(() => Path.Combine(_dir, "p"), () => "mp4", () => "ffmpeg", (_, a, _) =>
        {
            if (n++ == 0) File.WriteAllBytes(a[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(n == 1);
        });
        var pr = await partial.ExportDualMonitorSplitAsync(src, 0);
        Assert.False(pr.Ok);
        Assert.NotNull(pr.LeftPath);
        Assert.Null(pr.RightPath);
        Assert.Null(pr.DisplayConfigPath);
        Assert.Contains("[부분 구제]", pr.Message);

        var noDir = await new StockSuiteExporter(() => null).ExportDualMonitorSplitAsync(src);
        Assert.True(noDir.NeedsPathSetup);
    }

    private static int IndexOf(IReadOnlyList<string> a, string v)
    {
        for (var i = 0; i < a.Count; i++) if (a[i] == v) return i;
        return -1;
    }
}
