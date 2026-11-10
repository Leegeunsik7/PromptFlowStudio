using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>하드웨어 벤치마크 오토튠 · 라이브 티커 배지 · 포터블 에셋 번들러 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class FintechSuiteBenchmarkBadgeBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_fs_" + Guid.NewGuid().ToString("N"));

    public FintechSuiteBenchmarkBadgeBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public readonly List<string[]> Calls = new();

        public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
            Action<bool, string>? onLine = null, CancellationToken ct = default)
        {
            var a = args.ToArray();
            lock (Calls) Calls.Add(a);
            return Task.FromResult(respond(a));
        }
    }

    [Fact]
    public async Task HardwareBenchmarkService_CalculatesSpeedRatio_AndRecommendsProfile()
    {
        // 임계 분류
        Assert.Equal(RenderProfileTier.Ultra, HardwareBenchmarkService.Classify(3.0));
        Assert.Equal(RenderProfileTier.Ultra, HardwareBenchmarkService.Classify(7.5));
        Assert.Equal(RenderProfileTier.Balanced, HardwareBenchmarkService.Classify(2.99));
        Assert.Equal(RenderProfileTier.Balanced, HardwareBenchmarkService.Classify(1.5));
        Assert.Equal(RenderProfileTier.Eco, HardwareBenchmarkService.Classify(1.49));
        Assert.Equal(RenderProfileTier.Eco, HardwareBenchmarkService.Classify(double.NaN));
        Assert.Equal("Ultra (4K UHD @ 60fps)", HardwareBenchmarkService.ProfileLabel(RenderProfileTier.Ultra));
        Assert.Equal("Balanced (1080p @ 60fps)", HardwareBenchmarkService.ProfileLabel(RenderProfileTier.Balanced));
        Assert.Equal("Eco (1080p @ 30fps)", HardwareBenchmarkService.ProfileLabel(RenderProfileTier.Eco));

        // 지표 계산: 30프레임 / 0.25s = 120fps → 4.0×
        Assert.Equal(120.0, HardwareBenchmarkService.ComputeEncodeFps(30, 0.25), 6);
        Assert.Equal(4.0, HardwareBenchmarkService.ComputeSpeedRatio(120.0), 6);
        Assert.Equal(0, HardwareBenchmarkService.ComputeEncodeFps(30, 0));
        Assert.Equal(2.25, HardwareBenchmarkService.ParseSpeed("frame=10 speed=1.1x\rframe=30 fps=67 speed=2.25x"));
        Assert.Null(HardwareBenchmarkService.ParseSpeed("no stats"));

        var args = HardwareBenchmarkService.BuildBenchmarkArgs(GpuEncoderVendor.Nvenc);
        Assert.Contains("testsrc2=size=1920x1080:rate=30", args);
        Assert.Contains("h264_nvenc", args);
        Assert.Equal("-", args[^1]);
        Assert.Contains("libx264", HardwareBenchmarkService.BuildBenchmarkArgs(GpuEncoderVendor.Cpu));

        // 실행: ffmpeg speed=2.0x → Balanced(60fps ON · 1080p)
        var runner = new FakeRunner(_ => new ProcessResult(true, 0, "", "frame=30 fps=60 speed=2.0x"));
        var r = await new HardwareBenchmarkService(runner, () => "ffmpeg").RunAsync();
        Assert.True(r.Ok);
        Assert.Equal(2.0, r.SpeedRatio, 6);
        Assert.Equal(60.0, r.EncodeFps, 6);
        Assert.Equal(RenderProfileTier.Balanced, r.Profile);
        Assert.True(r.Smooth60);
        Assert.Equal(StockSuiteResolution.Fhd1080, r.Resolution);
        Assert.Single(runner.Calls);

        var ultra = await new HardwareBenchmarkService(new FakeRunner(_ => new ProcessResult(true, 0, "", "speed=3.4x")), () => "ffmpeg").RunAsync();
        Assert.Equal(RenderProfileTier.Ultra, ultra.Profile);
        Assert.Equal(StockSuiteResolution.Uhd4K, ultra.Resolution);

        // 실패/부재 → Eco 안전 폴백(throw 없음)
        var fail = await new HardwareBenchmarkService(new FakeRunner(_ => new ProcessResult(true, 1, "", "err")), () => "ffmpeg").RunAsync();
        Assert.False(fail.Ok);
        Assert.Equal(RenderProfileTier.Eco, fail.Profile);
        Assert.False(fail.Smooth60);
        var none = await new HardwareBenchmarkService(new FakeRunner(_ => throw new InvalidOperationException()), () => null).RunAsync();
        Assert.False(none.Ok);
        Assert.Equal(RenderProfileTier.Eco, none.Profile);
    }

    [Fact]
    public async Task StockSuiteExporter_ConstructsTickerBadgeFilter_WithColorCoding()
    {
        var up = StockSuiteExporter.BuildTickerBadgeFilter(new TickerBadgeData("aapl", "Apple Inc.", 185.5m, 2.4), "C:\\Windows\\Fonts\\arialbd.ttf");
        Assert.NotNull(up);
        Assert.StartsWith("drawbox=", up);
        Assert.Contains("color=black@0.55:t=fill", up);
        Assert.Contains("box=1:boxcolor=black@0.55:boxborderw=8", up);
        Assert.Contains("text='AAPL · Apple Inc.'", up);
        Assert.Contains("fontsize=20:fontcolor=white", up);
        Assert.Contains("fontcolor=" + StockSuiteExporter.TickerUpColor, up);
        Assert.DoesNotContain(StockSuiteExporter.TickerDownColor, up);
        Assert.Contains("$185.50 ▲ +2.4\\\\%", up);   // 3단계 이스케이프 — 옵션 \\ → drawtext \% → 리터럴 %
        Assert.Contains("fontfile='C\\:/Windows/Fonts/arialbd.ttf'", up);
        Assert.Contains("x=iw-360-40", up);   // 우하단 카드

        var down = StockSuiteExporter.BuildTickerBadgeFilter(new TickerBadgeData("TSLA", null, 201m, -1.26), null, StockSuiteResolution.Uhd4K);
        Assert.NotNull(down);
        Assert.Contains("fontcolor=" + StockSuiteExporter.TickerDownColor, down);
        Assert.Contains("▼ -1.3\\\\%", down);   // InvariantCulture 반올림(0.0)
        Assert.Contains("text='TSLA'", down);
        Assert.DoesNotContain("fontfile", down);   // 폰트 부재 → 생략 폴백
        Assert.Contains("fontsize=40", down);      // 4K 2배
        Assert.Equal("$201.00 ▼ -1.3%", StockSuiteExporter.FormatTickerPriceLine(new TickerBadgeData("TSLA", null, 201m, -1.26)));

        // null / 빈 티커 / NaN → 배지 생략
        Assert.Null(StockSuiteExporter.BuildTickerBadgeFilter(null, null));
        Assert.Null(StockSuiteExporter.BuildTickerBadgeFilter(new TickerBadgeData(" ", "X", 1m, 1), null));
        Assert.Null(StockSuiteExporter.BuildTickerBadgeFilter(new TickerBadgeData("X", null, 1m, double.NaN), null));

        // 내보내기 결선: 배지 ON → -vf 체인에 drawbox 포함 · drawtext 실패 → 무배지 재시도
        var src = Path.Combine(_dir, "src.mp4");
        File.WriteAllText(src, "v");
        var outDir = Path.Combine(_dir, "assets");
        var vfs = new List<string>();
        var exp = new StockSuiteExporter(() => outDir, () => "mp4", () => "ffmpeg", (_, a, _) =>
        {
            var al = a.ToList(); var vf = al.IndexOf("-vf") is var i and >= 0 ? al[i + 1] : "";
            lock (vfs) vfs.Add(vf);
            if (vf.Contains("drawbox", StringComparison.Ordinal)) return Task.FromResult(false);   // 배지 렌더 실패 시뮬
            File.WriteAllBytes(a[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        }, sharpenProvider: () => false, smooth60Provider: () => false)
        {
            BurnTickerBadge = true,
            TickerBadge = new TickerBadgeData("NVDA", "NVIDIA", 120m, 3.1),
        };
        var r = await exp.ExportAsync(src, sharpen: false, smooth60: false);
        Assert.True(r.Ok, r.Message);
        Assert.Contains(vfs, v => v.Contains("drawbox", StringComparison.Ordinal) && v.Contains(StockSuiteExporter.TickerUpColor, StringComparison.Ordinal));
        Assert.Contains("null", vfs);   // 무배지 폴백
        Assert.Contains("⚠ 배지 생략", r.Message);

        // 데이터 없음 → 배지 무시(스트림 복사 경로 · drawbox 미사용)
        vfs.Clear();
        exp.TickerBadge = null;
        var r2 = await exp.ExportAsync(src, sharpen: false, smooth60: false);
        Assert.True(r2.Ok, r2.Message);
        Assert.DoesNotContain(vfs, v => v.Contains("drawbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AssetBundleService_ExportsAndImportsBundle_PreservingMetadata()
    {
        var srcRoot = Path.Combine(_dir, "pcA", "media");
        var srcPresets = Path.Combine(_dir, "pcA", "stock");
        Directory.CreateDirectory(Path.Combine(srcRoot, "proj1"));
        Directory.CreateDirectory(srcPresets);
        var v1 = Path.Combine(srcRoot, "proj1", "clip.mp4");
        var v2 = Path.Combine(srcRoot, "loose.mp4");
        File.WriteAllBytes(v1, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(v2, new byte[] { 9, 8, 7, 6 });
        File.WriteAllText(ExportMetadata.MetaPathFor(v1), "{\"prompt\":\"neon skyline\",\"seed\":42}");
        StockSuitePlaylistService.WritePlaylist(srcPresets, new StockSuitePlaylist(true,
        [
            new PlaylistSlot("open", "09:00-10:00", "장초반", "bg_open.mp4"),
            new PlaylistSlot("night", "18:00-23:59", "야간", "bg_night.mp4"),
        ]));
        File.WriteAllText(Path.Combine(srcPresets, StockSuiteExporter.DisplayConfigName), "{\"screens\":2}");

        var exporter = new AssetBundleService(() => srcRoot, () => srcPresets);
        var bundle = Path.Combine(_dir, "out", "move");
        var ex = await exporter.ExportBundleAsync(bundle, [v1, "loose.mp4", "missing.mp4", v1], includePresets: true);
        Assert.True(ex.Ok, ex.Message);
        Assert.EndsWith(AssetBundleService.Extension, ex.BundlePath);
        Assert.Equal(2, ex.MediaCount);
        Assert.Equal(2, ex.PresetCount);
        Assert.Equal(2, ex.Skipped.Count);   // 미존재 + 중복 ID

        // 표준 ZIP 구조
        using (var zip = ZipFile.OpenRead(ex.BundlePath!))
        {
            Assert.NotNull(zip.GetEntry(AssetBundleService.ManifestEntry));
            Assert.NotNull(zip.GetEntry("media/proj1/clip.mp4"));
            Assert.NotNull(zip.GetEntry("media/proj1/clip.meta.json"));
            Assert.NotNull(zip.GetEntry("presets/playlist.json"));
        }

        // 대상 PC: 기존 'open' 슬롯 보유 + loose.mp4 동일 콘텐츠 존재(중복)
        var dstRoot = Path.Combine(_dir, "pcB", "media");
        var dstPresets = Path.Combine(_dir, "pcB", "stock");
        Directory.CreateDirectory(dstRoot);
        Directory.CreateDirectory(dstPresets);
        File.WriteAllBytes(Path.Combine(dstRoot, "loose.mp4"), new byte[] { 9, 8, 7, 6 });
        StockSuitePlaylistService.WritePlaylist(dstPresets, new StockSuitePlaylist(true, [new PlaylistSlot("open", "09:00-10:00", "내 장초반", "mine.mp4")]));

        var importer = new AssetBundleService(() => dstRoot, () => dstPresets);
        var im = await importer.ImportBundleAsync(ex.BundlePath!);
        Assert.True(im.Ok, im.Message);
        Assert.Single(im.Imported);
        Assert.Equal(1, im.DuplicatesSkipped);
        Assert.Equal(1, im.PlaylistSlotsMerged);
        Assert.Equal(1, im.PresetsRestored);

        var copied = Path.Combine(dstRoot, "proj1", "clip.mp4");
        Assert.Equal(File.ReadAllBytes(v1), File.ReadAllBytes(copied));   // 무결성
        Assert.Equal("{\"prompt\":\"neon skyline\",\"seed\":42}", File.ReadAllText(ExportMetadata.MetaPathFor(copied)));   // 메타 복원
        var pl = StockSuitePlaylistService.TryParse(File.ReadAllText(Path.Combine(dstPresets, StockSuitePlaylistService.PlaylistFileName)));
        Assert.NotNull(pl);
        Assert.Equal(2, pl!.Slots.Count);
        Assert.Equal("mine.mp4", pl.Slots.Single(s => s.Time == "09:00-10:00").File);   // 기존 슬롯 덮어쓰기 금지
        Assert.True(File.Exists(Path.Combine(dstPresets, StockSuiteExporter.DisplayConfigName)));

        // 재가져오기 = 전부 중복(idempotent) · 이름 충돌(다른 콘텐츠) → 접미사 보존
        File.WriteAllBytes(copied, new byte[] { 1, 2, 3 });
        var again = await importer.ImportBundleAsync(ex.BundlePath!);
        Assert.Equal(1, again.DuplicatesSkipped);
        Assert.Single(again.Imported);
        Assert.Contains("(bundle-1)", again.Imported[0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(copied));

        // 매니페스트 검증: 위조/Zip-Slip 거부
        Assert.Null(AssetBundleService.TryParseManifest("{\"format\":\"other\",\"version\":1,\"media\":[]}"));
        Assert.Null(AssetBundleService.TryParseManifest("{\"format\":\"pfsbundle\",\"version\":1,\"media\":[{\"rel\":\"../evil.exe\",\"bytes\":1,\"sha256\":\"" + new string('0', 64) + "\"}]}"));
        Assert.False(AssetBundleService.IsSafeRelative("C:/x.mp4"));
        Assert.False(AssetBundleService.IsSafeRelative("a/../b.mp4"));
        Assert.True(AssetBundleService.IsSafeRelative("proj1/clip.mp4"));
        var bogus = Path.Combine(_dir, "bogus.pfsbundle");
        using (var z = ZipFile.Open(bogus, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
        Assert.False((await importer.ImportBundleAsync(bogus)).Ok);
        Assert.False((await importer.ImportBundleAsync(Path.Combine(_dir, "nope.pfsbundle"))).Ok);
    }
}
