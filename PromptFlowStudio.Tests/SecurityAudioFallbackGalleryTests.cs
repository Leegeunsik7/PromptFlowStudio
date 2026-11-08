using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🔇 StockSuite 원본 복사 폴백 차단 · 🎵 쇼츠 BGM 격리 · 🎞️ 갤러리 미리보기 시각 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class SecurityAudioFallbackGalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_saf_" + Guid.NewGuid().ToString("N"));

    public SecurityAudioFallbackGalleryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NullYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, "x", "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }

    /// <summary>MP4 <c>hdlr</c>(soun) 박스를 품은 최소 바이트.</summary>
    private static byte[] Mp4WithAudio()
    {
        var b = new byte[4096];
        byte[] hdlr = [0, 0, 0, 0x21, (byte)'h', (byte)'d', (byte)'l', (byte)'r', 0, 0, 0, 0, 0, 0, 0, 0, (byte)'s', (byte)'o', (byte)'u', (byte)'n'];
        hdlr.CopyTo(b, 1000);
        return b;
    }

    [Fact]
    public async Task StockSuiteExporter_RejectsRawCopyFallback_WhenAudioCannotBeStripped()
    {
        var dest = Path.Combine(_dir, "stock");
        Directory.CreateDirectory(dest);
        var src = Path.Combine(_dir, "loud.mp4");
        File.WriteAllBytes(src, Mp4WithAudio());
        Assert.True(StockSuiteExporter.ContainsAudioTrack(src));

        // ffmpeg 부재 → 오디오 동반 원본 복사 거부
        var missing = new StockSuiteExporter(() => dest, null, () => null);
        var r = await missing.ExportAsync(src, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        Assert.False(r.Ok);
        Assert.Equal(StockSuiteExporter.SilentFallbackRejectedMessage, r.Message);
        Assert.Empty(Directory.GetFiles(dest, "*.mp4"));

        // ffmpeg 실패 → 동일 거부(WebM → MP4 폴백 경로 포함)
        FfmpegRunner fail = (_, _, _) => Task.FromResult(false);
        var failing = new StockSuiteExporter(() => dest, null, () => "ff", fail);
        var r2 = await failing.ExportAsync(src, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        Assert.False(r2.Ok);
        Assert.Equal(StockSuiteExporter.SilentFallbackRejectedMessage, r2.Message);
        var r3 = await failing.ExportAsync(src, format: StockSuiteExportFormat.WebMVp9, sharpen: false, smooth60: false);
        Assert.False(r3.Ok);
        Assert.Equal(StockSuiteExporter.SilentFallbackRejectedMessage, r3.Message);
        Assert.Empty(Directory.GetFiles(dest, "*.mp4"));

        // 오디오 미탐지 원본은 복사 폴백 유지(무음 보장)
        var silent = Path.Combine(_dir, "silent.mp4");
        File.WriteAllBytes(silent, new byte[4096]);
        Assert.False(StockSuiteExporter.ContainsAudioTrack(silent));
        var ok = await missing.ExportAsync(silent, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        Assert.True(ok.Ok, ok.Message);
        Assert.False(ok.HasAudio);

        // 주입 감지기로도 거부 확인
        missing.SourceAudioProbe = _ => true;
        Assert.False((await missing.ExportAsync(silent, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false)).Ok);
    }

    [Fact]
    public async Task StockSuiteExporter_PreservesShortsBgm_EvenInsideStockSuiteSubFolder()
    {
        var calls = new List<string[]>();
        FfmpegRunner runner = (_, args, _) =>
        {
            var a = args.ToArray();
            lock (calls) calls.Add(a);
            File.WriteAllBytes(a[^1], new byte[8192]);
            return Task.FromResult(true);
        };
        var dest = Path.Combine(_dir, "stock");
        Directory.CreateDirectory(dest);
        var ex = new StockSuiteExporter(() => dest, null, () => "ff", runner);
        var sub = Path.Combine(dest, "shorts", "clip.mp4");
        Assert.True(ex.IsStockSuiteTarget(sub, ExportProfile.HtsDesktop));
        Assert.True(ex.IsStockSuiteTarget(sub, ExportProfile.DualMonitorSplit));
        Assert.False(ex.IsStockSuiteTarget(sub, ExportProfile.SnsShorts));
        Assert.False(ex.IsStockSuiteTarget(Path.Combine(dest, "mobile", "clip.mp4"), ExportProfile.MobileVertical));
        Assert.Equal(ExportProfile.SnsShorts, StockSuiteExporter.ProfileOf("shorts"));
        Assert.Equal(ExportProfile.MobileVertical, StockSuiteExporter.ProfileOf("mobile"));
        Assert.Equal(ExportProfile.HtsDesktop, StockSuiteExporter.ProfileOf("hts"));

        var src = Path.Combine(_dir, "src.mp4");
        File.WriteAllBytes(src, new byte[4096]);
        var bgm = Path.Combine(_dir, "bgm.mp3");
        File.WriteAllBytes(bgm, new byte[2048]);
        var m = await ex.ExportMultiTargetPackageAsync(src, new MultiExportOptions
        {
            OutputDirectory = Path.Combine(dest, "pkg"),
            BgmPreset = SnsBgmPreset.CustomFile,
            BgmPath = bgm,
            DurationSec = 15,
        });
        Assert.Equal(3, m.Generated.Count);
        var shorts = calls.Single(c => c[^1].EndsWith("_shorts_9x16.mp4", StringComparison.Ordinal));
        Assert.DoesNotContain("-an", shorts);
        Assert.Contains("1:a:0", shorts);
        Assert.Contains("-c:a", shorts);
        var hts = calls.Single(c => c[^1].Contains("_hts_", StringComparison.Ordinal));
        Assert.Contains("-an", hts);
        Assert.DoesNotContain("-c:a", hts);
    }

    [Fact]
    public void PublishViewModel_InitializesWithGalleryTimestamp_WhenOpenedFromGallery()
    {
        var src = Path.Combine(_dir, "g.mp4");
        File.WriteAllBytes(src, new byte[4096]);
        using var vm = new PublishViewModel(new NullYt(), new NullIg());
        vm.Initialize(src, 7.25, "p");
        Assert.True(vm.IsOpen);
        Assert.Equal(7.25, vm.PreviewTimestampSec);
        Assert.Equal(7.25, vm.CapturePreviewOptions().AtSeconds);
        vm.Initialize(src, double.NaN, "p");
        Assert.Equal(1.0, vm.PreviewTimestampSec);   // 무효 → 1초

        // 갤러리 VM: 호버 위치(2초 루프) → 이탈 후 보존 → 중간점 → 1초
        var gvm = new GalleryViewModel(new GalleryMediaService());
        long now = 10_000;
        gvm.ClockMs = () => now;
        var item = new GalleryItem { ItemId = Guid.NewGuid(), FilePath = src, DurationSeconds = 30 };
        Assert.Equal(15.0, gvm.ResolvePreviewTimestamp(item));
        gvm.BeginHoverPreview(item.ItemId);
        now += 3_500;   // 3.5초 → 루프 1.5초
        Assert.Equal(1.5, gvm.ResolvePreviewTimestamp(item), 3);
        gvm.EndHoverPreview(item.ItemId);
        now += 10_000;
        Assert.Equal(1.5, gvm.ResolvePreviewTimestamp(item), 3);
        Assert.Equal(1.0, gvm.ResolvePreviewTimestamp(new GalleryItem { ItemId = Guid.NewGuid(), FilePath = src }));
        Assert.Equal(1.0, gvm.ResolvePreviewTimestamp((GalleryItem?)null));

        vm.Initialize(src, gvm.ResolvePreviewTimestamp(item), "p");
        Assert.Equal(1.5, vm.PreviewTimestampSec, 3);
    }
}
