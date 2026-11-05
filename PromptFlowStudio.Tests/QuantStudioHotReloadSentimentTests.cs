using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Export;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>StockSuite 핫리로드 트리거·세이프 스왑 · 시황 감성 인젝터 · 3-Way 스타일 변형 회귀 잠금.</summary>
public class QuantStudioHotReloadSentimentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_qshr_" + Guid.NewGuid().ToString("N"));

    public QuantStudioHotReloadSentimentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Src(string name, byte fill, int len = 64)
    {
        var p = Path.Combine(_dir, name);
        var bytes = new byte[len];
        Array.Fill(bytes, fill);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    // ffmpeg 미탐지 → 원본 복사 경로(임시 파일 → 원자적 교체)
    private static StockSuiteExporter Exporter(string target) => new(() => target, () => "mp4", () => null);

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_WritesHotReloadTriggerFile_UponSuccessfulExport()
    {
        var src = Src("render.mp4", 7);
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var exp = Exporter(target);
        exp.UtcNow = () => new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.Zero);

        var r = await exp.ExportAsync(src);

        Assert.True(r.Ok, r.Message);
        var trig = Path.Combine(target, StockSuiteExporter.HotReloadTriggerName);
        Assert.True(File.Exists(trig));
        using var doc = JsonDocument.Parse(File.ReadAllText(trig));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("2026-11-05T01:02:03.000Z", root.GetProperty("updatedAt").GetString());
        Assert.Equal(StockSuiteExporter.DefaultAssetName, root.GetProperty("file").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("updatedAt").GetString(), out _));
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));   // 임시 파일 잔존 0

        // 실패 내보내기(원본 부재)는 트리거를 갱신하지 않음
        File.Delete(trig);
        var bad = await exp.ExportAsync(Path.Combine(_dir, "missing.mp4"));
        Assert.False(bad.Ok);
        Assert.False(File.Exists(trig));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_HandlesFileLockGracefully_WithRetry()
    {
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var dest = Path.Combine(target, StockSuiteExporter.DefaultAssetName);
        File.WriteAllBytes(dest, new byte[] { 1, 2, 3 });
        var src = Src("render.mp4", 9, 128);
        var exp = Exporter(target);

        // ① 일시 잠금 → 지연 재시도로 복구
        exp.SwapRetryDelay = TimeSpan.FromMilliseconds(100);
        var lockStream = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(150); lockStream.Dispose(); });
        var r = await exp.ExportAsync(src);
        await release;
        Assert.True(r.Ok, r.Message);
        Assert.Equal(128, new FileInfo(dest).Length);
        Assert.True(File.Exists(Path.Combine(target, StockSuiteExporter.HotReloadTriggerName)));

        // ② 지속 잠금 → throw 없이 실패 반환 · 경로 재설정 오인 없음 · 기존 에셋 보존 · 임시 파일 정리
        File.Delete(Path.Combine(target, StockSuiteExporter.HotReloadTriggerName));
        exp.SwapRetryDelay = TimeSpan.FromMilliseconds(5);
        var src2 = Src("render2.mp4", 3, 256);
        StockSuiteExportResult r2;
        using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read))
            r2 = await exp.ExportAsync(src2);
        Assert.False(r2.Ok);
        Assert.False(r2.NeedsPathSetup);
        Assert.Equal(128, new FileInfo(dest).Length);
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));
        Assert.False(File.Exists(Path.Combine(target, StockSuiteExporter.HotReloadTriggerName)));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void GeminiPromptService_ApplyMarketSentiment_InjectsAccurateVisualMetaphors()
    {
        const string idea = "trader watching multiple monitors at night";

        var bull = GeminiPromptService.ApplyMarketSentiment(idea, MarketMood.BullRally);
        Assert.StartsWith(idea, bull);
        Assert.Contains("neon green", bull);
        Assert.Contains("emerald", bull);
        Assert.Contains("bullish green candlesticks", bull);
        Assert.Contains("buy-side trade ticks", bull);
        Assert.Equal(MarketMood.BullRally, GeminiPromptService.ActiveMarketMood(bull));

        // 무드 전환 — 기존 블록 교체(누적 없음)
        var bear = GeminiPromptService.ApplyMarketSentiment(bull, MarketMood.BearDefense);
        Assert.Contains("crimson red", bear);
        Assert.Contains("blue-hour", bear);
        Assert.Contains("sell orders", bear);
        Assert.DoesNotContain("emerald", bear);
        Assert.DoesNotContain("neon green", bear);
        Assert.Equal(MarketMood.BearDefense, GeminiPromptService.ActiveMarketMood(bear));

        // 멱등 · 원문 복원
        Assert.Equal(bear, GeminiPromptService.ApplyMarketSentiment(bear, MarketMood.BearDefense));
        Assert.Equal(idea, GeminiPromptService.StripMarketSentiment(bear));

        Assert.Contains("Fibonacci fan", GeminiPromptService.ApplyMarketSentiment(idea, MarketMood.HighVolatility));
        var night = GeminiPromptService.ApplyMarketSentiment(idea, MarketMood.MidnightWallStreet);
        Assert.Contains("bokeh", night);
        Assert.Contains("OLED", night);

        // 빈 프롬프트 → 무드 토큰만
        Assert.Equal(GeminiPromptService.MoodOf(MarketMood.BullRally).Tokens, GeminiPromptService.ApplyMarketSentiment("  ", MarketMood.BullRally));
        Assert.Null(GeminiPromptService.ActiveMarketMood(idea));
        Assert.Equal(4, GeminiPromptService.MarketMoods.Length);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task GeminiPromptService_GenerateThreeWayVariants_ReturnsThreeDistinctStyles()
    {
        const string idea = "a lone analyst reviewing a breakout chart";
        var svc = new GeminiPromptService(new System.Net.Http.HttpClient(), () => null);   // 키 없음 → 로컬 매트릭스

        var r = await svc.GenerateThreeWayVariantsAsync(idea);

        Assert.True(r.UsedFallback);
        Assert.Equal(3, r.Variants.Count);
        Assert.Equal("Realistic Dark Quant", r.Variants[0].Title);
        Assert.Equal("Cyber Neon Depth", r.Variants[1].Title);
        Assert.Equal("Minimal Modern Studio", r.Variants[2].Title);
        Assert.Contains("35mm", r.Variants[0].Prompt);
        Assert.Contains("quant trading desk", r.Variants[0].Prompt);
        Assert.Contains("holographic", r.Variants[1].Prompt);
        Assert.Contains("cyberpunk neon", r.Variants[1].Prompt);
        Assert.Contains("minimalist", r.Variants[2].Prompt);
        Assert.Contains("gradient", r.Variants[2].Prompt);
        for (var i = 0; i < 3; i++)
        {
            Assert.StartsWith(idea, r.Variants[i].Prompt);
            Assert.Equal(i + 1, r.Variants[i].Index);
            Assert.True(r.Variants[i].Seed > 0);
            Assert.False(string.IsNullOrWhiteSpace(r.Variants[i].Camera));
            for (var j = i + 1; j < 3; j++)
            {
                Assert.NotEqual(r.Variants[i].Prompt, r.Variants[j].Prompt);
                Assert.NotEqual(r.Variants[i].StyleKey, r.Variants[j].StyleKey);
                Assert.NotEqual(r.Variants[i].Seed, r.Variants[j].Seed);
                Assert.NotEqual(r.Variants[i].Camera, r.Variants[j].Camera);
            }
        }

        // 결정적 시드(재현성) · 빈 입력 거부
        var again = GeminiPromptService.BuildLocalThreeWayVariants(idea);
        for (var i = 0; i < 3; i++) Assert.Equal(r.Variants[i].Seed, again[i].Seed);
        var empty = await svc.GenerateThreeWayVariantsAsync("   ");
        Assert.Empty(empty.Variants);
    }
}
