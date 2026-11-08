using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>WebM(VP9) 1080p 샤프닝 일관화 · StockSuite 팩 일괄 넘버링 내보내기 회귀 잠금.</summary>
public class WebMSharpenAndBatchPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_wsbp_" + Guid.NewGuid().ToString("N"));

    public WebMSharpenAndBatchPackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Src(string name, byte fill)
    {
        var p = Path.Combine(_dir, name);
        var bytes = new byte[64];
        Array.Fill(bytes, fill);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static FfmpegRunner FakeRunner(List<string[]> calls) => (exe, args, ct) =>
    {
        var arr = args.ToArray();
        calls.Add(arr);
        File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
        return Task.FromResult(true);
    };

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_AppliesSharpenFilter_ToWebMExport()
    {
        // 인자 빌더 — 샤프닝 ON: -vf Lanczos/Unsharp + VP9 + -an · OFF: 기존 인자 그대로
        var a = StockSuiteExporter.BuildWebMArgs("in.mp4", "out.webm", sharpen: true);
        Assert.Equal(StockSuiteExporter.SharpenFilter, a[Array.IndexOf(a, "-vf") + 1]);
        Assert.Equal("libvpx-vp9", a[Array.IndexOf(a, "-c:v") + 1]);
        Assert.Contains("-an", a);
        Assert.Equal("out.webm", a[^1]);
        Assert.Equal(StockSuiteExporter.BuildWebMArgs("in.mp4", "out.webm"), StockSuiteExporter.BuildWebMArgs("in.mp4", "out.webm", sharpen: false));
        Assert.DoesNotContain("-vf", StockSuiteExporter.BuildWebMArgs("in.mp4", "out.webm"));

        var src = Src("render.mp4", 1);
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var calls = new List<string[]>();
        var exp = new StockSuiteExporter(() => target, () => "webm", () => "ffmpeg.exe", FakeRunner(calls), sharpenProvider: () => true);

        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Path.Combine(target, StockSuiteExporter.WebMAssetName), r.TargetPath);
        Assert.Single(calls);
        Assert.Equal(StockSuiteExporter.SharpenFilter, calls[0][Array.IndexOf(calls[0], "-vf") + 1]);
        Assert.Contains("libvpx-vp9", calls[0]);
        Assert.Contains("-an", calls[0]);
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));

        // 샤프닝 OFF → 필터 없는 VP9
        calls.Clear();
        Assert.True((await exp.ExportAsync(src, sharpen: false)).Ok);
        Assert.Single(calls);
        Assert.DoesNotContain("-vf", calls[0]);
        Assert.Contains("-an", calls[0]);

        // 샤프닝 실패 → 일반 VP9 폴백
        calls.Clear();
        FfmpegRunner failSharpen = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            if (arr.Contains("-vf")) return Task.FromResult(false);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var fb = await new StockSuiteExporter(() => target, () => "webm", () => "ffmpeg.exe", failSharpen, sharpenProvider: () => true).ExportAsync(src);
        Assert.True(fb.Ok, fb.Message);
        Assert.Equal(2, calls.Count);
        Assert.DoesNotContain("-vf", calls[1]);
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_ExportBatchPack_ExportsIndexedFilesSuccessfully()
    {
        Assert.Equal("bg_ambient_01.mp4", StockSuiteExporter.BatchPackAssetName(1, StockSuiteExportFormat.Mp4));
        Assert.Equal("bg_ambient_12.webm", StockSuiteExporter.BatchPackAssetName(12, StockSuiteExportFormat.WebMVp9));

        var v1 = Src("a.mp4", 1);
        var v2 = Src("b.mp4", 2);
        var v3 = Src("c.mp4", 3);
        var target = Path.Combine(_dir, "Pack");

        // ffmpeg 부재 → 원본 복사 폴백으로 넘버링 정규화(누락·중복은 건너뜀)
        var exp = new StockSuiteExporter(null, () => "mp4", () => null) { SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent };
        var r = await exp.ExportBatchPackAsync(new[] { v1, Path.Combine(_dir, "missing.mp4"), v2, v1, v3 }, target, sharpen: false);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.Exported.Count);
        Assert.Equal(1, r.Failed);
        Assert.Equal(Path.Combine(target, "bg_ambient_01.mp4"), r.Exported[0]);
        Assert.Equal(Path.Combine(target, "bg_ambient_02.mp4"), r.Exported[1]);
        Assert.Equal(Path.Combine(target, "bg_ambient_03.mp4"), r.Exported[2]);
        Assert.Equal(File.ReadAllBytes(v2), File.ReadAllBytes(r.Exported[1]));
        Assert.Equal(File.ReadAllBytes(v3), File.ReadAllBytes(r.Exported[2]));
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));

        // WebM + 샤프닝 → .webm 넘버링 · 각 호출에 샤프닝 필터
        var calls = new List<string[]>();
        var webm = new StockSuiteExporter(null, () => "webm", () => "ffmpeg.exe", FakeRunner(calls)) { SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent };
        var w = await webm.ExportBatchPackAsync(new[] { v1, v2 }, target, sharpen: true);
        Assert.True(w.Ok, w.Message);
        Assert.Equal(new[] { Path.Combine(target, "bg_ambient_01.webm"), Path.Combine(target, "bg_ambient_02.webm") }, w.Exported);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(StockSuiteExporter.SharpenFilter, c[Array.IndexOf(c, "-vf") + 1]));

        // 경로 미설정 → NeedsPathSetup
        var none = await new StockSuiteExporter() { SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent }.ExportBatchPackAsync(new[] { v1 }, "", sharpen: false);
        Assert.False(none.Ok);
        Assert.True(none.NeedsPathSetup);
    }
}
