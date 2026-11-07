using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.ViewModels;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>네이티브 울트라와이드 스케일 · 갤러리 일괄 작업 진행/취소 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class StabilityUltrawideBatchProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_uwb_" + Guid.NewGuid().ToString("N"));

    public StabilityUltrawideBatchProgressTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>동기 진행 수집기(<see cref="Progress{T}"/> 의 비동기 게시 회피 — 결정적).</summary>
    private sealed class SyncProgress(Action<BatchProgressReport>? onReport = null) : IProgress<BatchProgressReport>
    {
        public readonly List<BatchProgressReport> Reports = new();
        public void Report(BatchProgressReport value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    private string WriteMedia(string root, string name, int bytes = 256)
    {
        Directory.CreateDirectory(root);
        var p = Path.Combine(root, name);
        var data = new byte[bytes];
        data[0] = (byte)name.Length;
        File.WriteAllBytes(p, data);
        File.WriteAllText(GalleryMediaService.MetaPathFor(p), "{\"title\":\"" + Path.GetFileNameWithoutExtension(name) + "\"}");
        return p;
    }

    [Fact]
    public async Task StockSuiteExporter_ExportsNativeUltrawide_21_9_and_32_9_Correctly()
    {
        // 순수 필터 — 샤프닝 여부와 무관하게 네이티브 크롭 + Lanczos + Unsharp
        foreach (var sharpen in new[] { false, true })
        {
            var f21 = StockSuiteExporter.BuildVideoFilter(sharpen, false, StockSuiteResolution.Ultra21x9, false, null)!;
            var f32 = StockSuiteExporter.BuildVideoFilter(sharpen, false, StockSuiteResolution.Super32x9, false, null)!;
            Assert.Contains("scale=2560:1080:flags=lanczos,unsharp=", f21, StringComparison.Ordinal);
            Assert.Contains("scale=3840:1080:flags=lanczos,unsharp=", f32, StringComparison.Ordinal);
            Assert.DoesNotContain("1920:1080", f21, StringComparison.Ordinal);
            Assert.DoesNotContain("1920:1080", f32, StringComparison.Ordinal);
        }
        Assert.Equal((2560, 1080), StockSuiteExporter.Pixels(StockSuiteResolution.Ultra21x9));
        Assert.Equal((3840, 1080), StockSuiteExporter.Pixels(StockSuiteResolution.Super32x9));

        // 픽셀 비례 비트레이트/버퍼
        Assert.Equal((12_000_000L, 24_000_000L), StockSuiteExporter.RateCapFor(StockSuiteResolution.Fhd1080));
        Assert.Equal((16_000_000L, 32_000_000L), StockSuiteExporter.RateCapFor(StockSuiteResolution.Ultra21x9));
        Assert.Equal((24_000_000L, 48_000_000L), StockSuiteExporter.RateCapFor(StockSuiteResolution.Super32x9));
        Assert.Equal((48_000_000L, 96_000_000L), StockSuiteExporter.RateCapFor(StockSuiteResolution.Uhd4K));

        // 1080p 인자 불변 · 울트라와이드만 VBV 상한 주입(-pix_fmt 앞)
        var baseArgs = StockSuiteExporter.BuildSharpenArgs("in.mp4", "out.mp4", "null", GpuEncoderVendor.Cpu, StockSuiteResolution.Fhd1080);
        Assert.DoesNotContain("-maxrate", baseArgs);
        var uw = StockSuiteExporter.BuildSharpenArgs("in.mp4", "out.mp4", "null", GpuEncoderVendor.Nvenc, StockSuiteResolution.Super32x9);
        Assert.Equal("24000000", uw[Array.IndexOf(uw, "-maxrate") + 1]);
        Assert.Equal("48000000", uw[Array.IndexOf(uw, "-bufsize") + 1]);
        Assert.True(Array.IndexOf(uw, "-bufsize") < Array.IndexOf(uw, "-pix_fmt"));
        Assert.Equal("out.mp4", uw[^1]);

        // UI 키 왕복 · ExportResolution 매핑
        foreach (var r in Enum.GetValues<StockSuiteResolution>())
            Assert.Equal(r, StockSuiteExporter.ParseResolution(StockSuiteExporter.ResolutionKey(r)));
        Assert.Equal(StockSuiteResolution.Fhd1080, StockSuiteExporter.ParseResolution("bogus"));
        Assert.Equal(StockSuiteResolution.Ultra21x9, ExportResolutionInfo.ToStockSuite(ExportResolution.Res21x9));
        Assert.Equal(StockSuiteResolution.Super32x9, ExportResolutionInfo.ToStockSuite(ExportResolution.Res32x9));

        // 실제 결선 — ExportAsync 가 네이티브 스케일 + 비례 비트레이트로 ffmpeg 호출
        var lib = Path.Combine(_dir, "lib");
        var src = WriteMedia(lib, "s.mp4", 4096);
        foreach (var (res, scale, max, label) in new[]
                 {
                     (StockSuiteResolution.Ultra21x9, "scale=2560:1080", "16000000", "21:9"),
                     (StockSuiteResolution.Super32x9, "scale=3840:1080", "24000000", "32:9"),
                 })
        {
            var target = Path.Combine(_dir, "ss_" + StockSuiteExporter.ResolutionKey(res));
            Directory.CreateDirectory(target);
            var calls = new List<string[]>();
            FfmpegRunner runner = (exe, args, ct) =>
            {
                var arr = args.ToArray();
                lock (calls) calls.Add(arr);
                File.WriteAllBytes(arr[^1], new byte[4096]);
                return Task.FromResult(true);
            };
            var exp = new StockSuiteExporter(() => target, null, () => "ffmpeg.exe", runner) { SwapRetryDelay = TimeSpan.FromMilliseconds(1) };
            var r = await exp.ExportAsync(src, sharpen: false, smooth60: false, resolution: res);
            Assert.True(r.Ok, r.Message);
            Assert.Contains(label, r.Message, StringComparison.Ordinal);
            var a = Assert.Single(calls);
            Assert.Contains(scale, a[Array.IndexOf(a, "-vf") + 1], StringComparison.Ordinal);
            Assert.Contains("flags=lanczos", a[Array.IndexOf(a, "-vf") + 1], StringComparison.Ordinal);
            Assert.Equal(max, a[Array.IndexOf(a, "-maxrate") + 1]);
        }
    }

    [Fact]
    public async Task GalleryBatch_ReportsProgress_AndRespectsCancellation()
    {
        // ── 번들 내보내기: 항목별 진행 보고 + 완료 보고 ──
        var root = Path.Combine(_dir, "media");
        var files = new List<string>();
        for (var i = 0; i < 6; i++) files.Add(WriteMedia(root, "clip" + i + ".mp4"));
        var bundles = new AssetBundleService(() => root, static () => null);

        var p = new SyncProgress();
        var ok = await bundles.ExportBundleAsync(Path.Combine(_dir, "out", "all"), files, false, p);
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(6, ok.MediaCount);
        Assert.Equal(7, p.Reports.Count);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(new BatchProgressReport(i, 6, "clip" + i + ".mp4"), p.Reports[i]);
        }
        Assert.Equal(new BatchProgressReport(6, 6, null), p.Reports[^1]);
        Assert.Equal("[██████░░░░░░] 3/6 Processing... (clip3.mp4)", p.Reports[3].Format());
        Assert.Equal("[████████████] 6/6 Processing...", p.Reports[^1].Format());

        // ── 번들 내보내기 취소: 조기 종료 · 임시/완성 파일 미생성 ──
        using (var cts = new CancellationTokenSource())
        {
            var cp = new SyncProgress(r => { if (r.Completed == 2) cts.Cancel(); });
            var outPath = Path.Combine(_dir, "out", "canceled");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bundles.ExportBundleAsync(outPath, files, false, cp, cts.Token));
            Assert.Equal(3, cp.Reports.Count);   // 0,1,2 보고 후 다음 항목 경계에서 중단
            Assert.False(File.Exists(outPath + AssetBundleService.Extension));
            Assert.False(File.Exists(outPath + AssetBundleService.Extension + ".~pfs"));
        }

        // ── 일괄 삭제: 진행 보고 + 완료 ──
        var gallery = new GalleryMediaService();
        var items = gallery.ScanGalleryItems(root);
        Assert.Equal(6, items.Count);
        var dp = new SyncProgress();
        var first3 = new List<Guid> { items[0].ItemId, items[1].ItemId, items[2].ItemId };
        var del = await gallery.DeleteBatchAsync(first3, dp);
        Assert.Equal(new BatchDeleteResult(3, 3, false), del);
        Assert.Equal(4, dp.Reports.Count);
        Assert.Equal(new BatchProgressReport(3, 3, null), dp.Reports[^1]);
        Assert.Equal(3, gallery.Items.Count);
        Assert.False(File.Exists(items[0].FilePath));

        // ── 일괄 삭제 취소: 이미 지운 항목만 반영([부분 구제]) ──
        var remaining = gallery.Items;
        using (var cts = new CancellationTokenSource())
        {
            var cp = new SyncProgress(r => { if (r.Completed == 1) cts.Cancel(); });
            var ids = new List<Guid>();
            foreach (var it in remaining) ids.Add(it.ItemId);
            var cr = await gallery.DeleteBatchAsync(ids, cp, cts.Token);
            Assert.True(cr.Canceled);
            Assert.Equal(3, cr.Requested);
            Assert.Equal(2, cr.Deleted);                  // 항목 0·1 처리 후 경계에서 중단
            Assert.Single(gallery.Items);
            Assert.True(File.Exists(gallery.Items[0].FilePath));
            Assert.DoesNotContain(cp.Reports, r => r.CurrentFileName is null);   // 취소 시 완료 보고 없음
        }

        // ── 사전 취소 토큰: 아무것도 지우지 않음 ──
        using (var pre = new CancellationTokenSource())
        {
            pre.Cancel();
            var none = await gallery.DeleteBatchAsync(new[] { gallery.Items[0].ItemId }, null, pre.Token);
            Assert.Equal(new BatchDeleteResult(0, 1, true), none);
            Assert.Single(gallery.Items);
        }

        // ── VM 결선: 선택 모드 삭제 진행/상태 문구 ──
        var vm = new GalleryViewModel(gallery);
        vm.Refresh(root);
        vm.ToggleSelectMode();
        vm.SelectAll();
        var vr = await vm.DeleteCheckedAsync(new SyncProgress());
        Assert.Equal(1, vr.Deleted);
        Assert.Contains("1개 삭제됨", vm.StatusMessage, StringComparison.Ordinal);
    }
}
