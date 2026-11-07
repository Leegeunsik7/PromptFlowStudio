using System.IO.Compression;
using PromptFlowStudio.Services.Chart;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>티커 배지 시세 자동 채움 · 기본 해상도(벤치마크 Ultra → 4K) 영속 · 갤러리 다중 선택 번들 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class SuiteRefineQuoteResolutionSelectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_refine_" + Guid.NewGuid().ToString("N"));

    public SuiteRefineQuoteResolutionSelectTests() => Directory.CreateDirectory(_dir);

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

    private static OhlcvCandle[] Candles(params double[] closes)
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var a = new OhlcvCandle[closes.Length];
        for (var i = 0; i < closes.Length; i++)
            a[i] = new OhlcvCandle(t0.AddDays(i), closes[i], closes[i] * 1.01, closes[i] * 0.99, closes[i], 1000);
        return a;
    }

    [Fact]
    public void PublishViewModel_AutoFillsQuote_WhenTickerMatchesCachedData()
    {
        var chart = new ChartStillGeneratorService();
        chart.PushQuotes("AAPL", Candles(170, 175, 178, 180, 181.15, 185.50));
        chart.PushQuotes("005930", Candles(70000, 71000, 72000, 73000, 75400, 74500));
        using var vm = new PublishViewModel(new NullYt(), new NullIg(), quotes: chart);

        vm.SetTicker("aapl");
        Assert.Equal(185.50m, vm.TickerPrice);
        Assert.Equal(2.4, vm.TickerChangePercent, 1);
        Assert.Equal("$185.50", vm.TickerPriceText);
        Assert.Equal("+2.4%", vm.TickerChangeText);
        Assert.NotNull(vm.TickerQuoteMessage);

        vm.SetTicker("005930.KS");
        Assert.Equal(74500m, vm.TickerPrice);
        Assert.Equal("74,500원", vm.TickerPriceText);
        Assert.Equal("-1.2%", vm.TickerChangeText);

        // 수동 편집 유지 · 캐시 미스 → 기존 입력 보존
        vm.TickerPrice = 1m;
        vm.SetTicker("ZZZZ");
        Assert.Equal(1m, vm.TickerPrice);
        Assert.Null(vm.TickerQuoteMessage);

        // 시세 소스 미주입 → false
        using var bare = new PublishViewModel(new NullYt(), new NullIg());
        bare.TickerSymbol = "AAPL";
        Assert.False(bare.TryAutoFillQuote());
    }

    [Fact]
    public async Task HardwareBenchmark_AppliesUltraProfile_Persisting4KResolution()
    {
        var prefs = new UserPreferenceService();
        Assert.Equal(ExportResolution.Res1080p, prefs.DefaultResolution);

        await HardwareBenchmarkService.ApplyRecommendationAsync(new HardwareBenchmarkResult(true, GpuEncoderVendor.Cpu, 120, 4.0, RenderProfileTier.Ultra, "ultra"), prefs);
        Assert.Equal(ExportResolution.Res4K_UHD, prefs.DefaultResolution);
        Assert.True(prefs.StockSuiteSharpen);
        Assert.True(prefs.StockSuiteSmooth60);
        Assert.Equal(nameof(ExportResolution.Res4K_UHD), prefs.Get(UserPreferenceService.Keys.DefaultResolution));

        // 모달 열기 시 기본 해상도 → StockSuite 4K 프로필
        var video = Path.Combine(_dir, "v.mp4");
        File.WriteAllBytes(video, new byte[16]);
        using var vm = new PublishViewModel(new NullYt(), new NullIg(), prefs: prefs);
        vm.OpenForMedia(video, "p");
        Assert.Equal(StockSuiteResolution.Uhd4K, vm.StockSuiteResolution);

        await HardwareBenchmarkService.ApplyRecommendationAsync(new HardwareBenchmarkResult(true, GpuEncoderVendor.Cpu, 60, 2.0, RenderProfileTier.Balanced, "bal"), prefs);
        Assert.Equal(ExportResolution.Res1080p, prefs.DefaultResolution);
        Assert.True(prefs.StockSuiteSmooth60);

        await HardwareBenchmarkService.ApplyRecommendationAsync(new HardwareBenchmarkResult(false, GpuEncoderVendor.Cpu, 0, 0, RenderProfileTier.Eco, "eco"), prefs);
        Assert.Equal(ExportResolution.Res1080p, prefs.DefaultResolution);
        Assert.False(prefs.StockSuiteSmooth60);

        // 울트라와이드 → StockSuite 네이티브 21:9/32:9 · 손상 값 → 1080p
        Assert.Equal(StockSuiteResolution.Super32x9, ExportResolutionInfo.ToStockSuite(ExportResolution.Res32x9));
        Assert.Equal(StockSuiteResolution.Ultra21x9, ExportResolutionInfo.ToStockSuite(ExportResolution.Res21x9));
        Assert.Equal((2560, 1080), ExportResolutionInfo.Pixels(ExportResolution.Res21x9));
        prefs.Set(UserPreferenceService.Keys.DefaultResolution, "garbage");
        Assert.Equal(ExportResolution.Res1080p, prefs.DefaultResolution);
    }

    [Fact]
    public async Task GalleryMultiSelect_ExportsOnlySelectedMedia_ToBundle()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        string Clip(string n) { var p = Path.Combine(media, n); File.WriteAllBytes(p, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 }); return p; }
        var a = Clip("a.mp4");
        var b = Clip("b.mp4");
        var c = Clip("c.mp4");

        var svc = new GalleryMediaService();
        var vm = new GalleryViewModel(svc);
        vm.Refresh(media);
        Assert.False(vm.IsSelectModeActive);

        vm.ToggleSelectMode();
        Assert.True(vm.IsSelectModeActive);
        vm.ToggleCheck(svc.FindByPath(a)!);
        vm.ToggleCheck(svc.FindByPath(c)!);
        Assert.Equal(2, vm.CheckedCount);
        var ids = vm.SelectedMediaIds;
        Assert.Equal(2, ids.Count);
        Assert.Contains(Path.GetFullPath(a), ids, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(b), ids, StringComparer.OrdinalIgnoreCase);

        var bundles = new AssetBundleService(() => media, () => null);
        var outPath = Path.Combine(_dir, "out", "sel");
        var r = await vm.ExportSelectedBundleAsync(bundles, outPath, includePresets: false);
        Assert.NotNull(r);
        Assert.True(r!.Ok, r.Message);
        Assert.Equal(2, r.MediaCount);
        using (var zip = ZipFile.OpenRead(r.BundlePath!))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains(names, n => n.EndsWith("a.mp4", StringComparison.Ordinal));
            Assert.Contains(names, n => n.EndsWith("c.mp4", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.EndsWith("b.mp4", StringComparison.Ordinal));
        }

        // Select All / Deselect All / 모드 해제 → 선택 초기화
        vm.SelectAll();
        Assert.Equal(3, vm.CheckedCount);
        vm.DeselectAll();
        Assert.Equal(0, vm.CheckedCount);
        Assert.Null(await vm.ExportSelectedBundleAsync(bundles, outPath));
        vm.ToggleCheck(svc.FindByPath(b)!);
        vm.ToggleSelectMode();
        Assert.False(vm.IsSelectModeActive);
        Assert.Equal(0, vm.CheckedCount);
    }
}
