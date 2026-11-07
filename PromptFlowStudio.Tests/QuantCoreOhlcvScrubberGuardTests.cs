using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Chart;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.ProcessBridge;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>실시세 OHLCV 이평선 차트 · 1프레임 스냅샷 · 장중 트레이딩 세이프가드 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class QuantCoreOhlcvScrubberGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_qc_" + Guid.NewGuid().ToString("N"));

    public QuantCoreOhlcvScrubberGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static List<OhlcvCandle> Candles(int n)
    {
        var list = new List<OhlcvCandle>(n);
        var t0 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < n; i++)
        {
            var o = 70000 + i * 120 + (i % 7) * 300;
            var c = o + ((i % 3) - 1) * 450;
            list.Add(new OhlcvCandle(t0.AddDays(i), o, Math.Max(o, c) + 200, Math.Min(o, c) - 200, c, 1_000_000 + (i % 5) * 250_000));
        }
        return list;
    }

    private static bool HasColor(byte[] rgb, uint color)
    {
        byte r = (byte)(color >> 16), g = (byte)(color >> 8), b = (byte)color;
        for (var i = 0; i + 2 < rgb.Length; i += 3)
            if (rgb[i] == r && rgb[i + 1] == g && rgb[i + 2] == b) return true;
        return false;
    }

    [Fact]
    public async Task ChartStillGeneratorService_RendersRealOhlcvAndMovingAverages_WhenDataProvided()
    {
        var candles = Candles(120);

        // 이동평균 수치 — MA5 마지막 값 = 마지막 5개 종가 평균
        var closes = new double[candles.Count];
        for (var i = 0; i < closes.Length; i++) closes[i] = candles[i].Close;
        var ma5 = ChartStillGeneratorService.ComputeMovingAverage(closes, 5);
        var expect = (closes[^1] + closes[^2] + closes[^3] + closes[^4] + closes[^5]) / 5;
        Assert.Equal(expect, ma5[^1], 6);
        Assert.Equal(closes[0], ChartStillGeneratorService.ComputeMovingAverage(closes, 60)[0], 6);

        // 실시세 렌더 — MA5(골드)·MA20(레드)·MA60(그린) 픽셀 존재 · 합성 렌더와 상이 · 결정적
        var seed = ChartStillGeneratorService.StableSeed("005930", DateTimeOffset.UnixEpoch);
        var real = ChartStillGeneratorService.Render("005930", 640, 360, 90, seed, candles);
        Assert.Equal(640 * 360 * 3, real.Length);
        Assert.True(HasColor(real, ChartStillGeneratorService.MaShortColor));
        Assert.True(HasColor(real, ChartStillGeneratorService.MaFastColor));
        Assert.True(HasColor(real, ChartStillGeneratorService.MaSlowColor));
        Assert.Equal(real, ChartStillGeneratorService.Render("005930", 640, 360, 90, seed, candles));
        Assert.NotEqual(real, ChartStillGeneratorService.Render("005930", 640, 360, 90, seed));

        // IPC QuotePushed 수신 → 캐시 우선 바인딩
        var sb = new StringBuilder("{\"type\":\"QuotePushed\",\"ticker\":\"005930\",\"candles\":[");
        for (var i = 0; i < candles.Count; i++)
        {
            var k = candles[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"t\":").Append(k.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
              .Append(",\"o\":").Append(k.Open.ToString(CultureInfo.InvariantCulture)).Append(",\"h\":").Append(k.High.ToString(CultureInfo.InvariantCulture))
              .Append(",\"l\":").Append(k.Low.ToString(CultureInfo.InvariantCulture)).Append(",\"c\":").Append(k.Close.ToString(CultureInfo.InvariantCulture))
              .Append(",\"v\":").Append(k.Volume.ToString(CultureInfo.InvariantCulture)).Append('}');
        }
        sb.Append(",{\"t\":1,\"o\":10,\"h\":5,\"l\":1,\"c\":9,\"v\":1}]}");   // 무효 캔들(High < Open) 은 폐기
        var parsed = StockSuiteIpcBridge.ParseQuotePush(sb.ToString());
        Assert.NotNull(parsed);
        Assert.Equal(candles.Count, parsed!.Value.Candles.Length);
        Assert.Null(StockSuiteIpcBridge.ParseQuotePush("{\"type\":\"QuotePushed\",\"ticker\":\"<bad>\",\"candles\":[]}"));

        using var bridge = new StockSuiteIpcBridge(null, "pfs_test_" + Guid.NewGuid().ToString("N"));
        var gen = new ChartStillGeneratorService { UtcNow = static () => new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero) };
        bridge.OnQuotePushed += gen.PushQuotes;
        Assert.True(bridge.DispatchLine(sb.ToString()));
        var r = await gen.GenerateAsync("005930.KS", _dir, new ChartStillOptions { Width = 640, Height = 360 });
        Assert.True(r.Ok, r.Message);
        Assert.True(r.RealData);
        Assert.Contains("실시세", r.Message);

        // quotes.json 파일 경로(StockSuite 에셋 폴더) 바인딩
        var assets = Path.Combine(_dir, "assets");
        Directory.CreateDirectory(assets);
        var fileJson = new StringBuilder("{\"AAPL\":[");
        for (var i = 0; i < 30; i++)
        {
            if (i > 0) fileJson.Append(',');
            var o = 200 + i; var c = o + (i % 2 == 0 ? 1.5 : -1.5);
            fileJson.Append("[\"2026-09-").Append((i + 1).ToString("00", CultureInfo.InvariantCulture)).Append("\",")
                .Append(o.ToString(CultureInfo.InvariantCulture)).Append(',').Append((Math.Max(o, c) + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((Math.Min(o, c) - 1).ToString(CultureInfo.InvariantCulture)).Append(',').Append(c.ToString(CultureInfo.InvariantCulture)).Append(",5000]");
        }
        fileJson.Append("]}");
        File.WriteAllText(Path.Combine(assets, OhlcvQuoteParser.QuotesFileName), fileJson.ToString());
        var fileGen = new ChartStillGeneratorService(() => assets);
        Assert.Equal(30, fileGen.ResolveQuotes("aapl")!.Length);
        var rf = await fileGen.GenerateAsync("AAPL", _dir, new ChartStillOptions { Width = 640, Height = 360 });
        Assert.True(rf.Ok && rf.RealData, rf.Message);

        // 데이터 없음/통신 불가 → 시드 합성 무중단 폴백
        var none = await new ChartStillGeneratorService(() => Path.Combine(_dir, "missing")).GenerateAsync("NVDA", _dir, new ChartStillOptions { Width = 640, Height = 360 });
        Assert.True(none.Ok, none.Message);
        Assert.False(none.RealData);
        Assert.Null(OhlcvQuoteParser.ParseQuotesDocument("{not json", "AAPL"));
    }

    [Fact]
    public async Task StockSuiteExporter_ExtractsSingleFrameSnapshot_WithCorrectTimestamp()
    {
        // 1프레임 스텝 — 30fps / 60fps 격자 · 0·duration 클램프
        Assert.Equal(1.0 + 1.0 / 30, StockSuiteExporter.StepFrame(1.0, 1, 30), 9);
        Assert.Equal(1.0 - 1.0 / 60, StockSuiteExporter.StepFrame(1.0, -1, 60), 9);
        Assert.Equal(0, StockSuiteExporter.StepFrame(0.01, -5, 30));
        Assert.Equal(5.0 - 1.0 / 30, StockSuiteExporter.StepFrame(4.99, 3, 30, 5.0), 9);
        Assert.Equal(StockSuiteExporter.DefaultScrubFps, StockSuiteExporter.NormalizeFps(double.NaN));

        var args = StockSuiteExporter.BuildSnapshotArgs("in.mp4", 12.3456, "out.png");
        Assert.Equal("12.346", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.True(Array.IndexOf(args, "-ss") < Array.IndexOf(args, "-i"));   // 입력 앞 탐색
        Assert.Equal("in.mp4", args[Array.IndexOf(args, "-i") + 1]);
        Assert.Equal("1", args[Array.IndexOf(args, "-vframes") + 1]);
        Assert.Equal("2", args[Array.IndexOf(args, "-q:v") + 1]);
        Assert.Equal("out.png", args[^1]);
        Assert.Equal("0.000", StockSuiteExporter.FormatSeekSeconds(-3));

        var src = Path.Combine(_dir, "clip 01.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 1 });
        IReadOnlyList<string>? captured = null;
        var exp = new StockSuiteExporter(() => null, null, () => "ffmpeg", (_, a, _) =>
        {
            captured = a;
            File.WriteAllBytes(a[^1], new byte[] { 137, 80, 78, 71 });
            return Task.FromResult(true);
        });
        var outDir = Path.Combine(_dir, "_stills");
        var r = await exp.ExtractSnapshotAsync(src, 61.5, outDir);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(61.5, r.Seconds);
        Assert.Equal(Path.Combine(Path.GetFullPath(outDir), "snap_clip_01_01-01-500.png"), r.Path);
        Assert.True(File.Exists(r.Path));
        Assert.NotNull(captured);
        Assert.Equal("61.500", captured![IndexOf(captured, "-ss") + 1]);
        Assert.Equal(Path.GetFullPath(src), captured[IndexOf(captured, "-i") + 1]);
        Assert.EndsWith(".png", captured[^1], StringComparison.Ordinal);

        // 실패 경로 — 원본 없음 · FFmpeg 실패(빈 산출물 정리)
        Assert.False((await exp.ExtractSnapshotAsync(Path.Combine(_dir, "none.mp4"), 1, outDir)).Ok);
        var fail = new StockSuiteExporter(() => null, null, () => "ffmpeg", (_, _, _) => Task.FromResult(false));
        var rf = await fail.ExtractSnapshotAsync(src, 2, outDir);
        Assert.False(rf.Ok);
        Assert.Null(rf.Path);
    }

    private static int IndexOf(IReadOnlyList<string> a, string v)
    {
        for (var i = 0; i < a.Count; i++) if (a[i] == v) return i;
        return -1;
    }

    [Fact]
    public async Task TradingSafetyGuardService_DetectsMarketHours_AndAdjustsPriorityClass()
    {
        var kst = TimeSpan.FromHours(9);
        // 2026-11-05 = 목요일
        Assert.True(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 9, 0, 0, kst)));
        Assert.True(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 15, 29, 59, kst)));
        Assert.False(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 15, 30, 0, kst)));
        Assert.False(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 8, 59, 59, kst)));
        Assert.False(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 7, 10, 0, 0, kst)));   // 토요일
        Assert.True(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero)));   // UTC 01:00 = KST 10:00
        Assert.True(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 23, 0, 0, kst), new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0)));   // 사용자 정의(자정 넘김)

        var enabled = true;
        // ⏱️ 결정적 시계 — 2026-11-03(화) 10:00:00 KST 고정(러너 부하·실행 시각 무관)
        using var guard = new TradingSafetyGuardService(() => enabled, static () => FrozenTuesday10Kst);
        Assert.True(guard.IsThrottling);
        Assert.Equal(1, guard.MaxConcurrentEncodes);
        Assert.Equal(ProcessPriorityClass.BelowNormal, guard.ResolvePriority(@"C:\tools\ffmpeg.exe"));
        Assert.Null(guard.ResolvePriority("notepad.exe"));
        Assert.True(TradingSafetyGuardService.IsEncoderExecutable("/usr/bin/ffmpeg"));
        Assert.False(TradingSafetyGuardService.IsEncoderExecutable(null));

        // 동시 인코딩 1개 게이트 + 재진입 허용(교착 없음) — 벽시계 지연 없이 TCS 로 순서 제어
        var active = 0; var peak = 0; var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Work(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, now);
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct);
            var nested = await guard.RunGatedAsync(static _ => Task.FromResult(true), ct);
            Interlocked.Decrement(ref active);
            return nested;
        }
        var pending = new[] { guard.RunGatedAsync(Work), guard.RunGatedAsync(Work), guard.RunGatedAsync(Work) };
        for (var spin = 0; Volatile.Read(ref entered) == 0 && spin < 6000; spin++) await Task.Yield();
        Assert.Equal(1, Volatile.Read(ref entered));   // 슬롯 1개 — 첫 작업만 진입, 나머지는 대기
        release.SetResult();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.All(results, Assert.True);
        Assert.Equal(1, peak);
        Assert.Equal(0, guard.ActiveSlots);

        // 안전 모드 OFF 또는 장외 → 제한 해제
        enabled = false;
        Assert.False(guard.IsThrottling);
        Assert.Null(guard.ResolvePriority("ffmpeg"));
        Assert.Equal(int.MaxValue, guard.MaxConcurrentEncodes);
        enabled = true;
        guard.UtcNow = static () => new DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.FromHours(9));
        Assert.False(guard.IsThrottling);

        using var me = Process.GetCurrentProcess();
        Assert.False(TradingSafetyGuardService.TryApplyPriority(me, null));   // null = 변경 없음
    }

    private static readonly DateTimeOffset FrozenTuesday10Kst = new(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void TradingSafetyGuardService_WithInjectedTime_ConsistentlyDetectsMarketHours()
    {
        Assert.Equal(DayOfWeek.Tuesday, FrozenTuesday10Kst.DayOfWeek);
        var tp = new FixedTimeProvider(FrozenTuesday10Kst);
        using var byTp = new TradingSafetyGuardService(null, tp);
        using var byFunc = new TradingSafetyGuardService(null, static () => FrozenTuesday10Kst);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(byTp.IsThrottling);
            Assert.True(byFunc.IsMarketHoursNow);
            Assert.Equal(KrxSessionStatus.RegularMarket, byFunc.CurrentSession.Status);
            Assert.Equal(ProcessPriorityClass.BelowNormal, byTp.ResolvePriority("ffmpeg.exe"));
            Assert.Equal(1, byFunc.MaxConcurrentEncodes);
        }

        tp.Now = new DateTimeOffset(2026, 11, 3, 15, 30, 0, TimeSpan.FromHours(9));   // 마감 경계(배타)
        Assert.False(byTp.IsThrottling);
        Assert.Null(byTp.ResolvePriority("ffmpeg.exe"));
        tp.Now = new DateTimeOffset(2026, 11, 3, 0, 59, 59, TimeSpan.Zero);          // UTC → KST 09:59:59
        Assert.True(byTp.IsThrottling);

        byFunc.UtcNow = null!;   // null = 시스템 시계 복귀(예외 없음)
        Assert.NotNull(byFunc.UtcNow);
        Assert.Throws<ArgumentNullException>(() => new TradingSafetyGuardService(null, (TimeProvider)null!));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }
}
