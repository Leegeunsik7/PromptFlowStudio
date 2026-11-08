using System.Text.Json;
using Microsoft.JSInterop;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>📱 9:16 단일 내보내기 · ⚡ WebM 텔레메트리 · 💾 사용자 프리셋 영속 · 👁️ 1프레임 미리보기 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class SuiteRefineVerticalWebmPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_vwp_" + Guid.NewGuid().ToString("N"));

    public SuiteRefineVerticalWebmPreviewTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>localStorage 대역 — siteMaticStore.get/set 을 공유 사전에 기록(재시작 시뮬레이션).</summary>
    private sealed class DictJs : IJSRuntime
    {
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var key = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
            if (identifier == "siteMaticStore.set" && args is { Length: > 1 }) { lock (Store) Store[key] = args[1]?.ToString() ?? ""; return default; }
            if (identifier == "siteMaticStore.get")
            {
                string? v;
                lock (Store) Store.TryGetValue(key, out v);
                return new((TValue)(object?)v!);
            }
            return default;
        }
    }

    private (StockSuiteExporter Ex, List<string[]> Calls) Exporter(string name)
    {
        var calls = new List<string[]>();
        FfmpegRunner runner = (_, args, _) =>
        {
            var a = args.ToArray();
            lock (calls) calls.Add(a);
            File.WriteAllBytes(a[^1], new byte[8192]);
            return Task.FromResult(true);
        };
        return (new StockSuiteExporter(() => Path.Combine(_dir, "dest_" + name), null, () => "ff", runner), calls);
    }

    private string Source(string name)
    {
        var src = Path.Combine(_dir, name + ".mp4");
        File.WriteAllBytes(src, new byte[4096]);
        return src;
    }

    [Fact]
    public async Task StockSuiteExporter_ExportsVertical9_16_WithCorrectCropAndScaleFilters()
    {
        Assert.Equal((1080, 1920), StockSuiteExporter.Pixels(StockSuiteResolution.Vertical9x16));
        Assert.Equal(StockSuiteResolution.Vertical9x16, StockSuiteExporter.ParseResolution("9x16"));
        Assert.Equal(StockSuiteResolution.Vertical9x16, StockSuiteExporter.ParseResolution(StockSuiteExporter.ResolutionKey(StockSuiteResolution.Vertical9x16)));
        Assert.Equal(StockSuiteResolution.Vertical9x16, ExportResolutionInfo.ToStockSuite(ExportResolution.Res9_16));
        Assert.Equal((1080, 1920), ExportResolutionInfo.Pixels(ExportResolution.Res9_16));
        Assert.Equal(StockSuiteResolution.Vertical9x16, FintechExportPreset.MobileShorts.Resolution);

        // 샤프닝 OFF 여도 세로 크롭 체인은 항상 적용.
        Assert.Equal("crop=ih*9/16:ih,scale=1080:1920:flags=lanczos,unsharp=5:5:0.8:3:3:0.4", StockSuiteExporter.BuildScaleFilter(false, StockSuiteResolution.Vertical9x16));

        var (ex, calls) = Exporter("v916");
        var r = await ex.ExportAsync(Source("v916"), format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false, resolution: StockSuiteResolution.Vertical9x16);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("9:16", r.Message);
        var vf = calls[0][Array.IndexOf(calls[0], "-vf") + 1];
        Assert.StartsWith("crop=ih*9/16:ih,scale=1080:1920", vf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockSuiteExporter_RecordsTelemetry_ForWebMExports()
    {
        foreach (var sharpen in new[] { true, false })
        {
            var name = "webm_" + (sharpen ? "f" : "p");
            var (ex, calls) = Exporter(name);
            var src = Source(name);
            var r = await ex.ExportAsync(src, format: StockSuiteExportFormat.WebMVp9, sharpen: sharpen, smooth60: false);
            Assert.True(r.Ok, r.Message);
            Assert.EndsWith(".webm", r.TargetPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("libvpx-vp9", calls[0]);
            Assert.NotNull(r.Telemetry);
            using (var doc = JsonDocument.Parse(File.ReadAllText(ExportMetadata.MetaPathFor(src))))
            {
                Assert.Equal("libvpx-vp9", doc.RootElement.GetProperty("actualEncoder").GetString());
                Assert.False(doc.RootElement.GetProperty("isFallback").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("encodeElapsedMs").GetInt64() >= 0);
            }
            var t = ExportMetadata.ReadEncoderTelemetry(src);
            Assert.NotNull(t);
            Assert.Equal("🖥️ CPU (libvpx-vp9)", t!.Badge);
        }
    }

    [Fact]
    public async Task PublishViewModel_CustomPresets_PersistAndReloadFromPreferences()
    {
        var video = Path.Combine(_dir, "v.mp4");
        File.WriteAllBytes(video, new byte[16]);
        var js = new DictJs();
        var prefs = new UserPreferenceService();
        await prefs.InitAsync(js);
        FintechExportPreset saved;
        using (var vm = new PublishViewModel(new NullYt(), new NullIg(), prefs: prefs))
        {
            vm.OpenForMedia(video, "p");
            vm.ApplyPreset(FintechPresetKind.MobileShorts);
            vm.StockSuiteLut = LutProfile.WarmAmber;
            vm.StockSuiteTickerBadge = true;
            Assert.True(vm.SaveCustomPreset("Night Shorts"));
            saved = vm.CapturePreset();
            vm.ApplyPreset(FintechPresetKind.OledHts);
            vm.StockSuiteResolution = StockSuiteResolution.Ultra21x9;
            Assert.True(vm.SaveCustomPreset("night shorts"));   // 대소문자 무시 업서트
            Assert.True(vm.SaveCustomPreset("Desk"));
            Assert.Equal(2, vm.CustomPresets.Count);
        }

        // 🔁 재시작: 새 서비스 인스턴스가 동일 저장소에서 복원.
        var prefs2 = new UserPreferenceService();
        await prefs2.InitAsync(js);
        Assert.Equal(new[] { "night shorts", "Desk" }, prefs2.CustomPresets.Select(p => p.Name).ToArray());
        Assert.True(prefs2.SaveCustomPreset("Night Shorts", saved));
        Assert.Null(FintechExportPreset.TryDeserialize(null));
        Assert.Empty(FintechExportPreset.TryDeserializeList("{broken"));

        var prefs3 = new UserPreferenceService();
        await prefs3.InitAsync(js);
        using var vm2 = new PublishViewModel(new NullYt(), new NullIg(), prefs: prefs3);
        vm2.OpenForMedia(video, "p");
        Assert.True(vm2.HasCustomPreset);   // 레거시 단일 슬롯도 재시작 후 복원
        Assert.True(vm2.ApplyCustomPreset("NIGHT SHORTS"));
        Assert.Equal(saved, vm2.CapturePreset());
        Assert.Equal(StockSuiteResolution.Vertical9x16, vm2.StockSuiteResolution);
        Assert.Equal("Night Shorts", vm2.ActiveCustomPresetName);
        Assert.False(vm2.ApplyCustomPreset("missing"));
    }

    [Fact]
    public async Task StockSuiteExporter_GeneratesPreviewFrame_WithComposedFilterChain()
    {
        var (ex, calls) = Exporter("pv");
        var src = Source("pv");
        var opt = new ExportOptions
        {
            Resolution = StockSuiteResolution.Vertical9x16,
            Lut = LutProfile.MatrixNeon,
            Watermark = true,
            TickerBadge = new TickerBadgeData("AAPL", "Apple", 190.5m, 1.2),
            OutputDirectory = Path.Combine(_dir, "pv_out"),
        };
        var path = await ex.GeneratePreviewFrameAsync(src, opt);
        Assert.True(File.Exists(path));
        Assert.EndsWith(".jpg", path, StringComparison.OrdinalIgnoreCase);
        var a = calls.Single();
        Assert.Equal("1", a[Array.IndexOf(a, "-vframes") + 1]);
        Assert.Equal("1.000", a[Array.IndexOf(a, "-ss") + 1]);
        Assert.Equal(PromptFlowStudio.Services.Engines.ProcessCategory.LightweightProbe, PromptFlowStudio.Services.Engines.ProcessRunner.Classify("ffmpeg", a));
        var vf = a[Array.IndexOf(a, "-vf") + 1];
        Assert.StartsWith("crop=ih*9/16:ih", vf, StringComparison.Ordinal);
        Assert.Contains(ColorGradingService.BuildFfmpegEqFilter(LutProfile.MatrixNeon)!, vf);
        Assert.Contains("drawtext", vf);
        Assert.Contains("AAPL", vf);

        // 원본 없음 → 빈 문자열 + 사유.
        Assert.Equal(string.Empty, await ex.GeneratePreviewFrameAsync(Path.Combine(_dir, "none.mp4"), opt));
        Assert.NotNull(ex.LastPreviewError);
    }
}
