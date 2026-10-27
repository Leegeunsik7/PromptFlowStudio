using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>갤러리 아카이브 — 빈 폴더 샘플 폴백 · 종횡비 필터 · 복원 번들 보존 · 안전 삭제 회귀 잠금.</summary>
public class GalleryMediaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs-gallery-" + Guid.NewGuid().ToString("N"));

    public GalleryMediaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 무시 */ }
    }

    private string WriteMedia(string name, string? metaJson)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
        if (metaJson is not null) File.WriteAllText(GalleryMediaService.MetaPathFor(path), metaJson);
        return path;
    }

    [Fact]
    public void ScanGalleryItems_ReturnsDefaultItems_WhenDirectoryEmpty()
    {
        var svc = new GalleryMediaService();

        var empty = svc.ScanGalleryItems(_dir);
        Assert.NotEmpty(empty);
        Assert.All(empty, i => Assert.True(i.IsSample));
        Assert.Contains(empty, i => i.AspectCategory == AspectCategory.Vertical9x16);
        Assert.Contains(empty, i => i.AspectCategory == AspectCategory.Landscape16x9);
        Assert.All(empty, i => Assert.StartsWith("data:image/svg+xml;base64,", i.ThumbnailUrl));
        Assert.Equal(empty.Count, empty.Select(i => i.ItemId).Distinct().Count());

        // 존재하지 않는 폴더 · 빈 경로도 샘플 반환
        Assert.All(svc.ScanGalleryItems(Path.Combine(_dir, "missing")), i => Assert.True(i.IsSample));
        Assert.All(svc.ScanGalleryItems(""), i => Assert.True(i.IsSample));

        // 미디어가 있으면 샘플 대신 실제 항목 + 사이드카 메타 파싱
        WriteMedia("clip.mp4", "{\"title\":\"네온\",\"prompt\":\"neon city\",\"seed\":123456789012,\"resolution\":\"720x1280\",\"renderDurationSec\":12.5}");
        var real = Assert.Single(svc.ScanGalleryItems(_dir));
        Assert.False(real.IsSample);
        Assert.Equal("네온", real.Title);
        Assert.Equal("neon city", real.Prompt);
        Assert.Equal(123456789012L, real.Seed);
        Assert.Equal(AspectCategory.Vertical9x16, real.AspectCategory);
        Assert.Equal(12.5f, real.RenderDurationSec);
    }

    [Fact]
    public void FilterMedia_CorrectlyFiltersByAspectCategory()
    {
        WriteMedia("v.mp4", "{\"title\":\"세로 쇼츠\",\"prompt\":\"dance\",\"resolution\":\"720x1280\"}");
        WriteMedia("h.mp4", "{\"title\":\"와이드\",\"prompt\":\"desert sunset\",\"resolution\":\"1280x720\"}");
        WriteMedia("sq_960x960.mp4", null);   // 메타 없음 → 파일명 토큰 추정
        var vm = new GalleryViewModel(new GalleryMediaService());
        vm.Refresh(_dir);
        Assert.Equal(3, vm.MediaItems.Count);

        vm.SetFilter(GalleryFilter.Vertical9x16);
        Assert.Equal("세로 쇼츠", Assert.Single(vm.MediaItems).Title);

        vm.SetFilter(GalleryFilter.Landscape16x9);
        Assert.Equal("와이드", Assert.Single(vm.MediaItems).Title);

        vm.SetFilter(GalleryFilter.All);
        Assert.Contains(vm.MediaItems, i => i.AspectCategory == AspectCategory.Square1x1);

        vm.SearchQuery = "SUNSET";   // 프롬프트 대소문자 무시 검색
        vm.ApplyFilter();
        Assert.Equal("와이드", Assert.Single(vm.MediaItems).Title);

        // 필터에서 빠진 선택은 해제
        vm.SearchQuery = "";
        vm.ApplyFilter();
        vm.SelectMedia(vm.MediaItems.First(i => i.Title == "세로 쇼츠"));
        vm.SetFilter(GalleryFilter.Landscape16x9);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public void PackageRestoreContext_PreservesPromptAndSeed()
    {
        var svc = new GalleryMediaService();
        var item = svc.ScanGalleryItems(_dir).First(i => i.AspectCategory == AspectCategory.Vertical9x16);

        var ctx = svc.PackageRestoreContext(item);
        Assert.Equal(item.ItemId, ctx.SourceItemId);
        Assert.Equal(item.Prompt, ctx.Prompt);
        Assert.Equal(item.Seed, ctx.Seed);
        Assert.Equal(item.Resolution, ctx.Resolution);
        Assert.Equal("720*1280", ctx.WorkspaceSize);

        // VM 복원 → 큐잉 + 이동 이벤트 · Workspace 1회 소비
        var vm = new GalleryViewModel(svc);
        vm.Refresh(_dir);
        int nav = 0;
        vm.RestoreRequested += () => nav++;
        vm.RestoreToWorkspace(item);
        Assert.Equal(1, nav);
        Assert.True(svc.HasPendingRestore);
        var taken = svc.TakePendingRestore();
        Assert.NotNull(taken);
        Assert.Equal(item.Prompt, taken!.Prompt);
        Assert.Equal(item.Seed, taken.Seed);
        Assert.Null(svc.TakePendingRestore());
    }

    [Fact]
    public void DeleteGalleryItem_RemovesTargetItemCorrectly()
    {
        var keep = WriteMedia("keep.mp4", "{\"title\":\"keep\"}");
        var drop = WriteMedia("drop.mp4", "{\"title\":\"drop\"}");
        var svc = new GalleryMediaService();
        var items = svc.ScanGalleryItems(_dir);
        Assert.Equal(2, items.Count);
        var target = items.First(i => i.Title == "drop");

        Assert.True(svc.DeleteGalleryItem(target.ItemId));
        Assert.False(File.Exists(drop));
        Assert.False(File.Exists(GalleryMediaService.MetaPathFor(drop)));
        Assert.True(File.Exists(keep));
        Assert.Equal("keep", Assert.Single(svc.Items).Title);

        Assert.False(svc.DeleteGalleryItem(target.ItemId));   // 중복 삭제 차단
        Assert.False(svc.DeleteGalleryItem(Guid.NewGuid()));  // 미등록 ID

        // 샘플은 목록에서만 제거
        var sampleSvc = new GalleryMediaService();
        var samples = sampleSvc.ScanGalleryItems(Path.Combine(_dir, "none"));
        Assert.True(sampleSvc.DeleteGalleryItem(samples[0].ItemId));
        Assert.Equal(samples.Count - 1, sampleSvc.Items.Count);
    }

    [Fact]
    public async Task RenderQueue_Completed_WritesMetaJson_RestorableInGallery()
    {
        var output = WriteMedia("render_out.mp4", metaJson: null);
        using var queue = new PromptFlowStudio.Services.Render.RenderQueueService(TimeSpan.Zero, autoStart: false);
        var job = new PromptFlowStudio.Services.Batch.BatchRenderJob { Prompt = "neon city drift", Seed = 424242, Resolution = "1080x1920", DurationSec = 6f, OutputPath = output };
        var noMeta = new PromptFlowStudio.Services.Batch.BatchRenderJob { Prompt = "no output" };
        queue.Enqueue(new[] { job, noMeta });

        await queue.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(PromptFlowStudio.Services.Batch.BatchJobStatus.Completed, job.Status);
        Assert.Equal(PromptFlowStudio.Services.Batch.BatchJobStatus.Completed, noMeta.Status);
        Assert.True(File.Exists(GalleryMediaService.MetaPathFor(output)));

        var item = Assert.Single(new GalleryMediaService().ScanGalleryItems(_dir));
        Assert.Equal("neon city drift", item.Prompt);
        Assert.Equal(424242, item.Seed);
        Assert.Equal("1080x1920", item.Resolution);
        Assert.Equal(AspectCategory.Vertical9x16, item.AspectCategory);
    }
}
