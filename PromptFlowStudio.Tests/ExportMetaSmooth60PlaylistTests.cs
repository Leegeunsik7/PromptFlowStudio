using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>📦 내보내기 메타 규격 통일/역방향 치유 · ⚡ 60fps 보간 · 📅 장중 스케줄러 회귀 잠금.</summary>
public class ExportMetaSmooth60PlaylistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_emsp_" + Guid.NewGuid().ToString("N"));

    public ExportMetaSmooth60PlaylistTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string rel, int len = 64, byte fill = 0)
    {
        var p = Path.GetFullPath(Path.Combine(_dir, rel));   // 구분자 정규화(스캔 경로와 동일 표기)
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var b = new byte[len];
        Array.Fill(b, fill);
        File.WriteAllBytes(p, b);
        return p;
    }

    private static FfmpegRunner Runner(List<string[]> calls) => (exe, args, ct) =>
    {
        var a = args.ToArray();
        lock (calls) calls.Add(a);
        File.WriteAllBytes(a[^1], new byte[4096]);
        return Task.FromResult(true);
    };

    private static JsonElement Meta(string media)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GalleryMediaService.MetaPathFor(media)));
        return doc.RootElement.Clone();
    }

    private static void AssertExportedMeta(string media, string expectedTarget)
    {
        var m = Meta(media);
        Assert.True(m.GetProperty("isExported").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(m.GetProperty("exportedAt").GetString(), out _));
        var targets = m.GetProperty("exportedTargets").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(targets, t => string.Equals(t, expectedTarget, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_UnifiesExportMetadata_AcrossAllExportMethods()
    {
        var target = Path.Combine(_dir, "ss");
        var calls = new List<string[]>();
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", Runner(calls));

        // 단일 MP4 (기존 필드 보존 확인)
        var single = Write("out/a.mp4");
        File.WriteAllText(GalleryMediaService.MetaPathFor(single), "{\"prompt\":\"keep\",\"isFavorite\":true}");
        var r1 = await exp.ExportAsync(single);
        Assert.True(r1.Ok, r1.Message);
        AssertExportedMeta(single, r1.TargetPath!);
        Assert.Equal("keep", Meta(single).GetProperty("prompt").GetString());
        Assert.True(Meta(single).GetProperty("isFavorite").GetBoolean());

        // WebM
        var web = Write("out/b.mp4");
        var r2 = await exp.ExportAsync(web, format: StockSuiteExportFormat.WebMVp9);
        Assert.True(r2.Ok, r2.Message);
        AssertExportedMeta(web, r2.TargetPath!);

        // 재내보내기 → exportedTargets 누적(중복 제거)
        var r1b = await exp.ExportAsync(single, assetName: "bg_alt.mp4");
        var targets = Meta(single).GetProperty("exportedTargets").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Contains(r1b.TargetPath, targets);

        // 배치 팩
        var p1 = Write("out/c.mp4");
        var p2 = Write("out/d.mp4");
        var pack = await exp.ExportBatchPackAsync(new[] { p1, p2 }, target, sharpen: false);
        Assert.Equal(2, pack.Exported.Count);
        AssertExportedMeta(p1, pack.Exported[0]);
        AssertExportedMeta(p2, pack.Exported[1]);

        // 멀티 타겟 패키지 — 원본 + 산출물 모두 표준 필드
        var multi = Write("out/e.mp4");
        exp.BgmResolver = (_, _) => null;
        var mr = await exp.ExportMultiTargetPackageAsync(multi, new MultiExportOptions());
        Assert.True(mr.Ok, mr.Message);
        foreach (var g in mr.Generated)
        {
            AssertExportedMeta(multi, g);
            AssertExportedMeta(g, g);
        }

        // 갤러리 📦 노출
        var items = new GalleryMediaService().ScanGalleryItems(Path.Combine(_dir, "out"));
        foreach (var src in new[] { single, web, p1, p2, multi })
            Assert.True(items.Single(i => string.Equals(i.FilePath, src, StringComparison.OrdinalIgnoreCase)).IsExported, src);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task GalleryMediaService_RetroactivelyHealsExportedFlag_FromStockSuiteTargets()
    {
        var target = Path.Combine(_dir, "ss");
        var root = Path.Combine(_dir, "lib");
        var byMarker = Write("lib/m.mp4", 100, 1);
        var byHash = Write("lib/h.mp4", 200, 2);
        var untouched = Write("lib/u.mp4", 300, 3);
        Directory.CreateDirectory(target);
        // 마커 기반(이전 버전 내보내기 — 메타 미기록)
        var mt = Path.Combine(target, "bg_ambient.mp4");
        File.Copy(byMarker, mt);
        File.WriteAllText(mt + StockSuiteExporter.SourceMarkerSuffix, Path.GetFullPath(byMarker) + "|100|0");
        // 해시 기반(마커 없음 · 이름 다름)
        File.Copy(byHash, Path.Combine(target, "bg_ambient_01.mp4"));

        var svc = new GalleryMediaService(() => target);
        var items = svc.ScanGalleryItems(root);
        Assert.Equal(2, svc.LastHealedCount);
        Assert.True(items.Single(i => i.FilePath == byMarker).IsExported);
        Assert.True(items.Single(i => i.FilePath == byHash).IsExported);
        Assert.False(items.Single(i => i.FilePath == untouched).IsExported);
        AssertExportedMeta(byMarker, mt);

        // 재스캔은 멱등(이미 보정됨)
        svc.ScanGalleryItems(root);
        Assert.Equal(0, svc.LastHealedCount);

        // 대상 미설정/부재 → 치유 없음, 예외 없음
        var none = new GalleryMediaService(() => Path.Combine(_dir, "missing"));
        none.ScanGalleryItems(root);
        Assert.Equal(0, none.LastHealedCount);
        await Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_Applies60FpsInterpolationFilter_WhenRequested()
    {
        Assert.Null(StockSuiteExporter.BuildVideoFilter(false, false));
        Assert.Equal(StockSuiteExporter.SharpenFilter, StockSuiteExporter.BuildVideoFilter(true, false));
        Assert.Equal(StockSuiteExporter.SharpenFilter + ",minterpolate=fps=60:mi_mode=mci:mc_mode=aobmc:me_mode=bidir", StockSuiteExporter.BuildVideoFilter(true, true));
        Assert.Equal("fps=60", StockSuiteExporter.BuildVideoFilter(false, true, lowSpec: true));
        Assert.EndsWith(",minterpolate=fps=60:mi_mode=mci:mc_mode=aobmc:me_mode=bidir", StockSuiteExporter.BuildHtsFilter(HtsUltraAspect.Ultra21x9, smooth60: true));
        Assert.DoesNotContain("minterpolate", StockSuiteExporter.BuildHtsFilter(HtsUltraAspect.Ultra21x9, smooth60: false));

        var target = Path.Combine(_dir, "ss");
        var src = Write("v.mp4");
        foreach (var (fmt, sharpen) in new[] { (StockSuiteExportFormat.Mp4, true), (StockSuiteExportFormat.Mp4, false), (StockSuiteExportFormat.WebMVp9, true), (StockSuiteExportFormat.WebMVp9, false) })
        {
            var calls = new List<string[]>();
            var exp = new StockSuiteExporter(() => target, resolveFfmpeg: () => "ffmpeg.exe", runner: Runner(calls));
            var r = await exp.ExportAsync(src, format: fmt, sharpen: sharpen, smooth60: true);
            Assert.True(r.Ok, r.Message);
            var a = Assert.Single(calls);
            var vf = a[Array.IndexOf(a, "-vf") + 1];
            Assert.EndsWith(StockSuiteExporter.MotionInterpolateFilter, vf);
            Assert.Equal(sharpen, vf.StartsWith(StockSuiteExporter.SharpenFilter, StringComparison.Ordinal));
            Assert.DoesNotContain("copy", a);
            Assert.Contains("60fps", r.Message);
        }

        // 공급자 기본값 · OFF 면 스트림 복사 유지
        var off = new List<string[]>();
        var e2 = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", Runner(off));
        Assert.False(e2.DefaultSmooth60);
        await e2.ExportAsync(src, sharpen: false);
        Assert.DoesNotContain("-vf", off.Single());
        var on = new List<string[]>();
        var e3 = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", Runner(on), smooth60Provider: () => true);
        await e3.ExportAsync(src, sharpen: false);
        Assert.Equal(StockSuiteExporter.MotionInterpolateFilter, on.Single()[Array.IndexOf(on.Single(), "-vf") + 1]);
        Assert.False(new StockSuiteExporter(() => target, smooth60Provider: () => throw new InvalidOperationException()).DefaultSmooth60);

        // 멀티 패키지 HTS 규격에만 보간 결합
        var profiles = StockSuiteExporter.BuildMultiTargetProfiles(src, new MultiExportOptions { Smooth60 = true }, _dir, null);
        Assert.Contains("minterpolate", profiles.Single(p => p.Key == "hts").Args[Array.IndexOf(profiles.Single(p => p.Key == "hts").Args, "-vf") + 1]);
        Assert.DoesNotContain(profiles.Single(p => p.Key == "mobile").Args, s => s.Contains("minterpolate"));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuitePlaylistService_GeneratesValidPlaylistSchedule_AndWritesJson()
    {
        var target = Path.Combine(_dir, "ss");
        var calls = new List<string[]>();
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", Runner(calls))
        {
            UtcNow = () => new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero),
        };
        var svc = new StockSuitePlaylistService(exp);
        var assign = new Dictionary<string, string?>
        {
            ["morning"] = Write("a.mp4"),
            ["midday"] = Write("b.mp4"),
            ["closing"] = Write("c.mp4"),
            ["night"] = Write("d.mp4"),
        };

        var r = await svc.PublishAsync(assign);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(4, r.Published.Count);
        Assert.Equal(Path.Combine(Path.GetFullPath(target), StockSuitePlaylistService.PlaylistFileName), r.PlaylistPath);

        using (var doc = JsonDocument.Parse(File.ReadAllText(r.PlaylistPath!)))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("enabled").GetBoolean());
            var slots = root.GetProperty("slots").EnumerateArray().ToList();
            Assert.Equal(4, slots.Count);
            Assert.Equal(new[] { "08:30-10:00", "10:00-14:30", "14:30-16:00", "16:00-08:30" }, slots.Select(s => s.GetProperty("time").GetString()));
            Assert.Equal(new[] { "bg_morning.mp4", "bg_midday.mp4", "bg_closing.mp4", "bg_night.mp4" }, slots.Select(s => s.GetProperty("file").GetString()));
            Assert.Equal("장초반 개장 랠리", slots[0].GetProperty("label").GetString());
            foreach (var s in slots) Assert.True(File.Exists(Path.Combine(target, s.GetProperty("file").GetString()!)));
        }
        using (var trig = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, StockSuiteExporter.HotReloadTriggerName))))
            Assert.Equal("playlist.json", trig.RootElement.GetProperty("file").GetString());
        AssertExportedMeta(assign["morning"]!, Path.Combine(Path.GetFullPath(target), "bg_morning.mp4"));

        // 라운드트립 + 활성 슬롯(자정 넘김 포함)
        var loaded = svc.Load();
        Assert.NotNull(loaded);
        Assert.Equal("morning", loaded!.Slots[0].Key);
        Assert.Equal("morning", StockSuitePlaylistService.ResolveActiveSlot(loaded.Slots, new TimeOnly(8, 30))!.Key);
        Assert.Equal("midday", StockSuitePlaylistService.ResolveActiveSlot(loaded.Slots, new TimeOnly(10, 0))!.Key);
        Assert.Equal("closing", StockSuitePlaylistService.ResolveActiveSlot(loaded.Slots, new TimeOnly(15, 59))!.Key);
        Assert.Equal("night", StockSuitePlaylistService.ResolveActiveSlot(loaded.Slots, new TimeOnly(23, 0))!.Key);
        Assert.Equal("night", StockSuitePlaylistService.ResolveActiveSlot(loaded.Slots, new TimeOnly(3, 0))!.Key);

        // 부분 재발행 — 미지정 슬롯은 기존 파일 유지
        var partial = await svc.PublishAsync(new Dictionary<string, string?> { ["night"] = Write("e.mp4") });
        Assert.True(partial.Ok, partial.Message);
        Assert.Equal(4, svc.Load()!.Slots.Count);

        // 스키마 검증 · 경로 미설정
        Assert.Null(StockSuitePlaylistService.TryParse("{\"slots\":[{\"time\":\"bad\",\"file\":\"x.mp4\"}]}"));
        Assert.Null(StockSuitePlaylistService.TryParse("not json"));
        var unset = await new StockSuitePlaylistService(new StockSuiteExporter(() => null)).PublishAsync(assign);
        Assert.True(unset.NeedsPathSetup);
        Assert.False(unset.Ok);
    }
}
