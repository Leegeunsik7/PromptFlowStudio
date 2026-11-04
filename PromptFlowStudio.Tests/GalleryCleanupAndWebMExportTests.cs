using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>갤러리 Mock 태깅·일괄 정화 · StockSuite WebM(VP9) 변환 인자 · 적용 상태 판정 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class GalleryCleanupAndWebMExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_gclean_" + Guid.NewGuid().ToString("N"));

    public GalleryCleanupAndWebMExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Media(string name, string? metaJson)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        if (metaJson is not null) File.WriteAllText(GalleryMediaService.MetaPathFor(p), metaJson);
        return p;
    }

    [Fact]
    public void GalleryService_IdentifiesMockVideosCorrectly()
    {
        Media("mock_flag.mp4", """{"title":"a","isMock":true}""");
        Media("mock_engine.mp4", """{"title":"b","engine":"Mock"}""");
        Media("mock_meta.mp4", """{"title":"c","metadata":{"src":"lavfi testsrc=duration=5"}}""");
        Media("real.mp4", """{"title":"d","engine":"ComfyUI","isMock":false,"prompt":"sunset beach"}""");
        Media("nometa.mp4", null);

        var svc = new GalleryMediaService();
        var items = svc.ScanGalleryItems(_dir);
        Assert.Equal(5, items.Count);
        Assert.Equal(3, svc.MockCount);
        foreach (var it in items)
        {
            var mock = it.Title is "a" or "b" or "c";
            Assert.Equal(mock, it.IsMock);
            Assert.Equal(mock ? "🧪 Mock" : "🎬 AI Render", it.AssetTag);
        }
    }

    [Fact]
    public void GalleryService_DeleteMockAssets_CleansFilesSuccessfully()
    {
        var m1 = Media("m1.mp4", """{"isMock":true}""");
        var m2 = Media("m2_ambient15s.mp4", """{"engine":"Mock"}""");
        var real = Media("real.mp4", """{"engine":"ComfyUI"}""");
        var real2 = Media("real2.mp4", null);

        var svc = new GalleryMediaService();
        var vm = new GalleryViewModel(svc);
        vm.Refresh(_dir);
        Assert.Equal(2, vm.MockCount);

        Assert.Equal(2, vm.DeleteMockAssets());
        Assert.False(File.Exists(m1));
        Assert.False(File.Exists(GalleryMediaService.MetaPathFor(m1)));
        Assert.False(File.Exists(m2));
        Assert.False(File.Exists(GalleryMediaService.MetaPathFor(m2)));
        Assert.True(File.Exists(real));
        Assert.True(File.Exists(GalleryMediaService.MetaPathFor(real)));
        Assert.Equal(0, vm.MockCount);
        Assert.Equal(2, vm.MediaItems.Count);

        // 다중 선택 삭제
        var target = svc.FindByPath(real2)!;
        vm.ToggleCheck(target);
        Assert.Equal(1, vm.CheckedCount);
        Assert.Equal(1, vm.DeleteChecked());
        Assert.False(File.Exists(real2));
        Assert.True(File.Exists(real));
        Assert.Equal(0, vm.CheckedCount);
        Assert.Single(vm.MediaItems);
    }

    [Fact]
    public async Task StockSuiteExporter_ExportsWebMFormat_WhenRequested()
    {
        var args = StockSuiteExporter.BuildWebMArgs("in.mp4", "out.webm");
        var joined = string.Join(" ", args);
        Assert.Contains("-i in.mp4 -c:v libvpx-vp9 -crf 32 -b:v 0 -an out.webm", joined);
        Assert.Equal("out.webm", args[^1]);

        var src = Media("render.mp4", null);
        var target = Path.Combine(_dir, "stock");
        IReadOnlyList<string>? captured = null;
        var exporter = new StockSuiteExporter(() => target, () => "webm", () => "ffmpeg.exe", (exe, a, ct) =>
        {
            captured = a;
            File.WriteAllBytes(a[^1], new byte[4096]);   // 가짜 VP9 산출물(>1KB)
            return Task.FromResult(true);
        });
        Assert.Equal(StockSuiteExportFormat.WebMVp9, exporter.DefaultFormat);

        var r = await exporter.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Path.Combine(target, StockSuiteExporter.WebMAssetName), r.TargetPath);
        Assert.NotNull(captured);
        Assert.Contains("libvpx-vp9", captured!);
        Assert.Equal(Path.GetFullPath(src), captured![Array.IndexOf(captured.ToArray(), "-i") + 1]);
        Assert.True(File.Exists(r.TargetPath));
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));
        Assert.True(exporter.IsApplied(src));

        // ffmpeg 부재 → MP4 복사 폴백
        var noFfmpeg = new StockSuiteExporter(() => target, () => "webm", () => null);
        var fb = await noFfmpeg.ExportAsync(src);
        Assert.True(fb.Ok);
        Assert.Equal(Path.Combine(target, StockSuiteExporter.DefaultAssetName), fb.TargetPath);

        // MP4 명시 → 해시 일치 적용 판정 · 다른 원본은 미적용
        var mp4 = new StockSuiteExporter(() => target);
        Assert.True((await mp4.ExportAsync(src, format: StockSuiteExportFormat.Mp4)).Ok);
        Assert.True(mp4.IsApplied(src));
        var other = Media("other.mp4", null);
        File.WriteAllBytes(other, new byte[] { 9, 9, 9 });
        Assert.False(mp4.IsApplied(other));
    }
}
