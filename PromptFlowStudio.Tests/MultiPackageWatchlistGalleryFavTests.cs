using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>📦 멀티 타겟 패키지 · 📊 관심종목 연동 · ⭐ 갤러리 즐겨찾기/필터 회귀 잠금.</summary>
public class MultiPackageWatchlistGalleryFavTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_mpwg_" + Guid.NewGuid().ToString("N"));

    public MultiPackageWatchlistGalleryFavTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, int len = 64)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[len]);
        return p;
    }

    private static string ArgAfter(string[] args, string flag) => args[Array.IndexOf(args, flag) + 1];

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_ExportsMultiTargetPackage_GeneratesAllProfiles()
    {
        var src = Write("render.mp4");
        var bgm = Write("bgm.wav");
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var a = args.ToArray();
            lock (calls) calls.Add(a);
            File.WriteAllBytes(a[^1], new byte[4096]);   // 유효 출력(> MinValidOutputBytes)
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => null, resolveFfmpeg: () => "ffmpeg", runner: runner);
        exp.BgmResolver = (_, _) => throw new InvalidOperationException("명시 BGM 이 있으면 프리셋 합성 금지");

        var r = await exp.ExportMultiTargetPackageAsync(src, new MultiExportOptions
        {
            HtsAspect = HtsUltraAspect.Ultra21x9, HtsFormat = StockSuiteExportFormat.WebMVp9, BgmPath = bgm, DurationSec = 10,
        });

        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.Generated.Count);
        Assert.Empty(r.Failed);
        Assert.Equal(3, calls.Count);
        foreach (var g in r.Generated)
        {
            Assert.True(File.Exists(g));
            Assert.StartsWith(r.PackageDirectory!, g, StringComparison.OrdinalIgnoreCase);
            using var meta = JsonDocument.Parse(File.ReadAllText(GalleryMediaService.MetaPathFor(g)));
            Assert.True(meta.RootElement.GetProperty("isExported").GetBoolean());
        }

        // 1) HTS 21:9 — 중앙 스마트 크롭 + 2560x1080 Lanczos + 샤프닝 · 무음 WebM
        var hts = calls[0];
        Assert.EndsWith("_hts_21x9.webm", hts[^1], StringComparison.Ordinal);
        var hvf = ArgAfter(hts, "-vf");
        Assert.Contains("crop=min(iw\\,ih*2560/1080)", hvf, StringComparison.Ordinal);
        Assert.Contains("scale=2560:1080:flags=lanczos", hvf, StringComparison.Ordinal);
        Assert.Contains("unsharp=", hvf, StringComparison.Ordinal);
        Assert.Contains("-an", hts);
        Assert.Equal("libvpx-vp9", ArgAfter(hts, "-c:v"));

        // 2) 모바일 9:16 — crop=ih*9/16:ih,scale=1080:1920 경량 MP4 · 무음
        var mob = calls[1];
        Assert.EndsWith("_mobile_9x16.mp4", mob[^1], StringComparison.Ordinal);
        Assert.StartsWith("crop=ih*9/16:ih,scale=1080:1920", ArgAfter(mob, "-vf"), StringComparison.Ordinal);
        Assert.Contains("-an", mob);
        Assert.Equal("veryfast", ArgAfter(mob, "-preset"));

        // 3) 쇼츠 9:16 + BGM 루프 믹싱 + 1.5초 페이드아웃(10초 → st=8.5)
        var sh = calls[2];
        Assert.EndsWith("_shorts_9x16.mp4", sh[^1], StringComparison.Ordinal);
        Assert.StartsWith("crop=ih*9/16:ih,scale=1080:1920", ArgAfter(sh, "-vf"), StringComparison.Ordinal);
        Assert.Contains(bgm, sh);
        Assert.Equal("-1", ArgAfter(sh, "-stream_loop"));
        Assert.Equal("afade=t=out:st=8.5:d=1.5", ArgAfter(sh, "-af"));
        Assert.Equal("aac", ArgAfter(sh, "-c:a"));
        Assert.DoesNotContain("-an", sh);

        // 해상도 프로파일 표 · 16:9/32:9
        Assert.Equal((1920, 1080), StockSuiteExporter.HtsResolution(HtsUltraAspect.Wide16x9));
        Assert.Equal((3840, 1080), StockSuiteExporter.HtsResolution(HtsUltraAspect.Super32x9));
        var p32 = StockSuiteExporter.BuildMultiTargetProfiles(src, new MultiExportOptions { HtsAspect = HtsUltraAspect.Super32x9, HtsFormat = StockSuiteExportFormat.Mp4 }, _dir, null);
        Assert.Equal(3840, p32[0].Width);
        Assert.EndsWith("_hts_32x9.mp4", p32[0].OutputPath, StringComparison.Ordinal);
        Assert.Contains("-an", p32[2].Args);   // BGM 없음 → 무음 쇼츠 폴백
        Assert.Equal((1080, 1920), (p32[1].Width, p32[1].Height));

        // [부분 구제] — 쇼츠만 실패해도 나머지 보존
        FfmpegRunner partial = (exe, args, ct) =>
        {
            var a = args.ToArray();
            if (a[^1].Contains("_shorts_", StringComparison.Ordinal)) return Task.FromResult(false);
            File.WriteAllBytes(a[^1], new byte[4096]);
            return Task.FromResult(true);
        };
        var exp2 = new StockSuiteExporter(() => null, resolveFfmpeg: () => "ffmpeg", runner: partial) { BgmResolver = (_, _) => null };
        var r2 = await exp2.ExportMultiTargetPackageAsync(src, new MultiExportOptions { OutputDirectory = Path.Combine(_dir, "pkg2") });
        Assert.False(r2.Ok);
        Assert.Equal(2, r2.Generated.Count);
        Assert.Equal(new[] { "shorts" }, r2.Failed);

        // 원본 부재 → 실패(throw 없음)
        var r3 = await exp.ExportMultiTargetPackageAsync(Path.Combine(_dir, "nope.mp4"), new MultiExportOptions());
        Assert.Empty(r3.Generated);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void StockSuiteWatchlistService_ParsesWatchlistJson_AndProvidesFallback()
    {
        // 파일 부재 → 코스피 TOP 5 + 매그니피센트 7
        var empty = new StockSuiteWatchlistService(() => new[] { _dir, null, "" }).Load();
        Assert.False(empty.FromFile);
        Assert.Equal(12, empty.Stocks.Count);
        Assert.Contains(empty.Stocks, s => s.Ticker == "005930" && s.Name == "삼성전자");
        Assert.Contains(empty.Stocks, s => s.Ticker == "000660");
        Assert.Contains(empty.Stocks, s => s.Ticker == "NVDA");
        Assert.Equal("005930 삼성전자", empty.Stocks[0].ChipLabel);

        // config/favorites.json — 객체 스키마 · 숫자 코드 0-패딩 · 중복 차단 · 문자열 티커 보강
        var cfg = Path.Combine(_dir, "config");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "favorites.json"),
            "{\"favorites\":[{\"code\":5930,\"name\":\"삼성전자\",\"sector\":\"반도체\"},{\"symbol\":\"nvda\"},\"005930\",{\"ticker\":\"035420\",\"stockName\":\"NAVER\",\"industry\":\"인터넷\"},{\"name\":\"티커없음\"}]}");
        var svc = new StockSuiteWatchlistService(() => new[] { _dir });
        var r = svc.Load();
        Assert.True(r.FromFile);
        Assert.EndsWith("favorites.json", r.SourcePath, StringComparison.Ordinal);
        Assert.Equal(new[] { "005930", "NVDA", "035420" }, r.Stocks.Select(s => s.Ticker).ToArray());
        Assert.Equal("엔비디아", r.Stocks[1].Name);
        Assert.Equal("인터넷", r.Stocks[2].Sector);

        // watchlist.json 이 우선 · 배열 루트
        File.WriteAllText(Path.Combine(_dir, "watchlist.json"), "[{\"ticker\":\"000660\",\"name\":\"SK하이닉스\",\"sector\":\"반도체\"}]");
        var w = svc.Load();
        Assert.Single(w.Stocks);
        Assert.Equal("SK하이닉스", svc.Current.Stocks[0].Name);

        // 손상 JSON → 빈 목록 → 다음 후보(config/favorites.json)로 진행
        File.WriteAllText(Path.Combine(_dir, "watchlist.json"), "{ broken");
        Assert.Empty(StockSuiteWatchlistService.Parse("{ broken"));
        Assert.Equal(3, svc.Load().Stocks.Count);

        // 탐색 루트 구성 + Gemini 로컬 티커 프롬프트(메타포 + 티커 HUD)
        Assert.Contains(StockSuiteWatchlistService.RootsFrom(cfg, null), x => string.Equals(x, _dir, StringComparison.OrdinalIgnoreCase));
        var p = GeminiPromptService.BuildLocalTickerPrompt("005930", "삼성전자", "반도체");
        Assert.Contains("HBM", p, StringComparison.Ordinal);
        Assert.Contains("\"005930\"", p, StringComparison.Ordinal);
        Assert.Contains("HUD", p, StringComparison.Ordinal);
        Assert.Contains("GPU", GeminiPromptService.BuildLocalTickerPrompt("NVDA", "엔비디아", null), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void GalleryService_ManagesFavorites_AndFiltersCorrectly()
    {
        var ai = Write("ai_render.mp4");
        File.WriteAllText(GalleryMediaService.MetaPathFor(ai), "{\"title\":\"AI\",\"prompt\":\"neon city\",\"seed\":7}");
        var exported = Write(Path.Combine("pkg", "clip_hts_21x9.webm"));
        File.WriteAllText(GalleryMediaService.MetaPathFor(exported), "{\"title\":\"HTS\",\"isExported\":true,\"resolution\":\"2560x1080\"}");
        var plain = Write("plain.mp4");
        Write("plain.poster.jpg");   // 포스터는 갤러리 항목이 아님

        var svc = new GalleryMediaService();
        var items = svc.ScanGalleryItems(_dir);
        Assert.Equal(3, items.Count);
        var itAi = items.Single(i => i.FilePath == ai);
        var itPlain = items.Single(i => i.FilePath == plain);
        Assert.Equal(GalleryMediaService.PosterPathFor(plain), itPlain.PosterPath);
        Assert.Null(itAi.PosterPath);
        Assert.True(items.Single(i => i.FilePath == exported).IsExported);

        // 토글 → 기존 메타 필드 보존 + isFavorite 기록
        Assert.True(svc.ToggleFavorite(itAi.ItemId));
        Assert.True(itAi.IsFavorite);
        using (var doc = JsonDocument.Parse(File.ReadAllText(GalleryMediaService.MetaPathFor(ai))))
        {
            Assert.True(doc.RootElement.GetProperty("isFavorite").GetBoolean());
            Assert.Equal("neon city", doc.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(7, doc.RootElement.GetProperty("seed").GetInt32());
        }
        // 메타 없는 항목 → 사이드카 신규 생성
        Assert.True(svc.SetFavorite(itPlain.ItemId, true));
        Assert.True(File.Exists(GalleryMediaService.MetaPathFor(plain)));

        // 재스캔 시 즐겨찾기 복원
        var rescanned = svc.ScanGalleryItems(_dir);
        Assert.True(rescanned.Single(i => i.FilePath == ai).IsFavorite);

        // 필터 — 전체 · ⭐ · 🎬 AI Render · 📦 내보냄
        var vm = new GalleryViewModel(svc);
        vm.SetFilter(GalleryFilter.All);
        Assert.Equal(3, vm.MediaItems.Count);
        vm.SetFilter(GalleryFilter.Favorites);
        Assert.Equal(2, vm.MediaItems.Count);
        vm.SetFilter(GalleryFilter.Exported);
        Assert.Equal(exported, Assert.Single(vm.MediaItems).FilePath);
        vm.SetFilter(GalleryFilter.AiRender);
        Assert.Equal(2, vm.MediaItems.Count);
        Assert.DoesNotContain(vm.MediaItems, i => i.IsExported);

        // ⭐ 탭에서 해제 → 즉시 제외 + 메타 false
        vm.SetFilter(GalleryFilter.Favorites);
        var fav = vm.MediaItems.Single(i => i.FilePath == ai);
        Assert.True(vm.ToggleFavorite(fav));
        Assert.Single(vm.MediaItems);
        using (var doc = JsonDocument.Parse(File.ReadAllText(GalleryMediaService.MetaPathFor(ai))))
            Assert.False(doc.RootElement.GetProperty("isFavorite").GetBoolean());

        // 미존재 ID → false
        Assert.False(svc.SetFavorite(Guid.NewGuid(), true));
    }
}
