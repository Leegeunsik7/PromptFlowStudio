using System.Text.Json;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>⚡ 인코더 텔레메트리 메타 영속 · 핀테크 퀵 프리셋 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class SuiteCompleteTelemetryPresetTests : IDisposable
{
    private const string Listing = "Encoders:\n V....D libx264  libx264 H.264\n V....D h264_nvenc  NVIDIA NVENC H.264 encoder\n";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_suitecomplete_" + Guid.NewGuid().ToString("N"));

    public SuiteCompleteTelemetryPresetTests() => Directory.CreateDirectory(_dir);

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

    private async Task<(StockSuiteExportResult R, string Src)> ExportAsync(string name, bool gpuFails)
    {
        var src = Path.Combine(_dir, name + ".mp4");
        File.WriteAllBytes(src, new byte[4096]);
        FfmpegRunner runner = (_, args, _) =>
        {
            var a = (string[])args;
            if (gpuFails && a[Array.IndexOf(a, "-c:v") + 1] == "h264_nvenc") return Task.FromResult(false);
            File.WriteAllBytes(a[^1], new byte[8192]);
            return Task.FromResult(true);
        };
        var accel = new HardwareAccelDetector(() => "ff", (_, _) => Task.FromResult<string?>(Listing), (_, _, _) => Task.FromResult(true));
        var ex = new StockSuiteExporter(() => Path.Combine(_dir, "dest_" + name), null, () => "ff", runner, sharpenProvider: () => true) { Accel = accel };
        return (await ex.ExportAsync(src), src);
    }

    [Fact]
    public async Task StockSuiteExporter_PersistsEncoderTelemetry_InMetadata()
    {
        // GPU 성공 경로
        var (gpu, gpuSrc) = await ExportAsync("gpu", gpuFails: false);
        Assert.True(gpu.Ok, gpu.Message);
        using (var doc = JsonDocument.Parse(File.ReadAllText(ExportMetadata.MetaPathFor(gpuSrc))))
        {
            Assert.Equal("h264_nvenc", doc.RootElement.GetProperty("actualEncoder").GetString());
            Assert.False(doc.RootElement.GetProperty("isFallback").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("encodeElapsedMs").GetInt64() >= 0);
            Assert.True(doc.RootElement.GetProperty("isExported").GetBoolean());
        }
        var t1 = ExportMetadata.ReadEncoderTelemetry(gpuSrc);
        Assert.NotNull(t1);
        Assert.True(t1!.IsHardware);
        Assert.Equal("⚡ NVENC", t1.Badge);

        // CPU 폴백 경로
        var (cpu, cpuSrc) = await ExportAsync("cpu", gpuFails: true);
        Assert.True(cpu.Ok, cpu.Message);
        using (var doc = JsonDocument.Parse(File.ReadAllText(ExportMetadata.MetaPathFor(cpuSrc))))
        {
            Assert.Equal("libx264", doc.RootElement.GetProperty("actualEncoder").GetString());
            Assert.True(doc.RootElement.GetProperty("isFallback").GetBoolean());
        }
        var t2 = ExportMetadata.ReadEncoderTelemetry(cpuSrc);
        Assert.NotNull(t2);
        Assert.False(t2!.IsHardware);
        Assert.True(t2.IsFallback);
        Assert.Equal("🖥️ CPU (libx264)", t2.Badge);

        // 메타 부재/손상 → null(예외 없음)
        Assert.Null(ExportMetadata.ReadEncoderTelemetry(Path.Combine(_dir, "none.mp4")));
        var bad = Path.Combine(_dir, "bad.mp4");
        File.WriteAllText(ExportMetadata.MetaPathFor(bad), "{not json");
        Assert.Null(ExportMetadata.ReadEncoderTelemetry(bad));
    }

    [Fact]
    public void PublishViewModel_AppliesFintechPresets_ConfiguringAllSwitches()
    {
        var video = Path.Combine(_dir, "v.mp4");
        File.WriteAllBytes(video, new byte[16]);
        var prefs = new UserPreferenceService();
        using var vm = new PublishViewModel(new NullYt(), new NullIg(), prefs: prefs);
        vm.OpenForMedia(video, "p");

        Assert.True(vm.ApplyPreset(FintechPresetKind.OledHts));
        Assert.Equal(StockSuiteResolution.Fhd1080, vm.StockSuiteResolution);
        Assert.True(vm.StockSuiteSmooth60);
        Assert.Equal(LutProfile.PureOledBlack, vm.StockSuiteLut);
        Assert.False(vm.StockSuiteWatermark);
        Assert.True(vm.StockSuiteTickerBadge);
        Assert.Equal(SnsBgmPreset.None, vm.Bgm);
        Assert.Equal(FintechPresetKind.OledHts, vm.ActivePreset);

        Assert.True(vm.ApplyPreset(FintechPresetKind.MobileShorts));
        Assert.Equal(StockSuiteResolution.Fhd1080, vm.StockSuiteResolution);
        Assert.False(vm.StockSuiteSmooth60);
        Assert.Equal(LutProfile.None, vm.StockSuiteLut);
        Assert.True(vm.StockSuiteWatermark);
        Assert.False(vm.StockSuiteTickerBadge);
        Assert.Equal(SnsBgmPreset.FintechSynthwave, vm.Bgm);   // BGM 합성 = loudnorm -14 LUFS
        Assert.True(vm.ToYouTube);
        Assert.True(vm.ToInstagram);

        Assert.True(vm.ApplyPreset(FintechPresetKind.Ultrawide32x9));
        Assert.Equal(StockSuiteResolution.Super32x9, vm.StockSuiteResolution);
        Assert.Equal((3840, 1080), StockSuiteExporter.Pixels(vm.StockSuiteResolution));
        Assert.True(vm.StockSuiteSmooth60);
        Assert.Equal(LutProfile.MatrixNeon, vm.StockSuiteLut);
        Assert.Equal(HtsUltraAspect.Super32x9, vm.MultiHtsAspect);
        Assert.Equal(StockSuiteExportFormat.Mp4, vm.StockSuiteFormat);

        // 💾 사용자 프리셋 저장 → 다른 프리셋 적용 → 복원
        vm.StockSuiteResolution = StockSuiteResolution.Uhd4K;
        vm.StockSuiteWatermark = true;
        vm.StockSuiteLut = LutProfile.WarmAmber;
        vm.StockSuiteFormat = StockSuiteExportFormat.WebMVp9;
        var snap = vm.CapturePreset();
        Assert.True(vm.SaveCustomPreset());
        Assert.True(vm.HasCustomPreset);
        vm.ApplyPreset(FintechPresetKind.OledHts);
        Assert.True(vm.ApplyPreset(FintechPresetKind.Custom));
        Assert.Equal(snap, vm.CapturePreset());
        Assert.Equal(StockSuiteResolution.Uhd4K, vm.StockSuiteResolution);
        Assert.Equal(LutProfile.WarmAmber, vm.StockSuiteLut);

        // 직렬화 왕복 · 손상 JSON → null
        Assert.Equal(FintechExportPreset.Ultrawide32x9, FintechExportPreset.TryDeserialize(FintechExportPreset.Ultrawide32x9.Serialize()));
        Assert.Null(FintechExportPreset.TryDeserialize("{oops"));
        Assert.Equal(FintechPresetKind.MobileShorts, FintechExportPreset.ParseKind(FintechExportPreset.Key(FintechPresetKind.MobileShorts)));

        // 저장소 없는 VM — 사용자 프리셋 미지원(false)
        using var bare = new PublishViewModel(new NullYt(), new NullIg());
        Assert.False(bare.ApplyPreset(FintechPresetKind.Custom));
        Assert.False(bare.SaveCustomPreset());
    }
}
