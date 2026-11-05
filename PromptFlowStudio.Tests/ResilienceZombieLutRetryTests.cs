using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Network;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;
using Xunit;

namespace PromptFlowStudio.Tests;

public class ResilienceZombieLutRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_res_" + Guid.NewGuid().ToString("N"));

    public ResilienceZombieLutRetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Task FakeDownload(string url, string dest, CancellationToken ct)
    {
        File.WriteAllBytes(dest, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 });
        return Task.CompletedTask;
    }

    private static Task<WanTaskResult> Succeeded(string id, CancellationToken ct)
        => Task.FromResult(new WanTaskResult(true, WanTaskStatus.Succeeded, "https://dashscope.example/" + id + ".mp4", null, "ok"));

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task TaskJournalService_UserCanceledTask_IsNeverAutoDownloaded()
    {
        var path = Path.Combine(_dir, "j", TaskJournalService.FileName);
        var now = DateTimeOffset.UtcNow;
        using (var j = new TaskJournalService(path))
        {
            Assert.True(j.Record(new CloudTaskJournalEntry("t-cancel", "Cloud", "p1", "Sec5_Single", TaskJournalService.StatusProcessing, now)));
            Assert.True(j.Record(new CloudTaskJournalEntry("t-int", "Cloud", "p2", "Sec5_Single", TaskJournalService.StatusProcessing, now)));
            Assert.True(j.MarkUserCanceled("t-cancel"));
            Assert.False(j.MarkUserCanceled("t-cancel"));   // 멱등
            Assert.True(j.MarkStatus("t-int", TaskJournalService.StatusInterrupted));
            Assert.Equal(2, j.Count);
        }

        // 앱 재시작 — 원격은 둘 다 성공 상태여도 UserCanceled 는 회수 금지
        var queried = new List<string>();
        using var restarted = new TaskJournalService(path)
        {
            Downloader = FakeDownload,
            MediaRootProvider = () => _dir,
            Query = (id, ct) => { lock (queried) queried.Add(id); return Succeeded(id, ct); },
        };
        Assert.False(TaskJournalService.IsAutoRecoverable(restarted.LoadAll()[0]));
        var r = await restarted.RecoverAsync();
        Assert.Equal(1, r.Scanned);
        Assert.Equal(1, r.Recovered);
        Assert.DoesNotContain("t-cancel", queried);
        Assert.False(File.Exists(Path.Combine(_dir, "recovered_t-cancel.mp4")));
        var left = restarted.LoadAll();
        Assert.Single(left);
        Assert.Equal("t-cancel", left[0].TaskId);
        Assert.Equal(TaskJournalService.StatusUserCanceled, left[0].Status);
        Assert.Equal(1, restarted.Count);

        // 반복 자동 복구에도 계속 제외
        Assert.Equal(0, (await restarted.RecoverAsync()).Scanned);

        // 인스펙터 [수동 회수/다운로드] — 상태 무관 회수 허용
        var manual = await restarted.RecoverTaskAsync("t-cancel");
        Assert.Equal(1, manual.Recovered);
        Assert.True(File.Exists(Path.Combine(_dir, "recovered_t-cancel.mp4")));
        Assert.Equal(0, restarted.Count);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task TaskJournalService_PurgeAndRetryJournalItems_WorksCorrectly()
    {
        var path = Path.Combine(_dir, "j2", TaskJournalService.FileName);
        var now = DateTimeOffset.UtcNow;
        using var j = new TaskJournalService(path) { Downloader = FakeDownload, MediaRootProvider = () => _dir, Query = Succeeded };
        var changed = 0;
        j.OnChanged += () => Interlocked.Increment(ref changed);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.True(j.Record(new CloudTaskJournalEntry(id, "Cloud", "p-" + id, "Sec5_Single", TaskJournalService.StatusProcessing, now)));
        Assert.True(j.MarkUserCanceled("a"));
        Assert.True(j.MarkStatus("b", TaskJournalService.StatusFailed));

        // [재시도] — UserCanceled → Processing 복귀 · 자동 회수 편입
        Assert.True(j.Retry("a"));
        Assert.False(j.Retry("a"));
        Assert.False(j.Retry("nope"));
        Assert.True(TaskJournalService.IsAutoRecoverable(j.LoadAll()[0]));
        Assert.Equal(TaskJournalService.StatusProcessing, j.LoadAll()[0].Status);

        // [저널에서 영구 삭제]
        Assert.True(j.Purge("c"));
        Assert.False(j.Purge("c"));
        Assert.False(j.Purge(null));
        Assert.Equal(3, j.Count);

        // 재시도 후 자동 회수 — a(재시도) · b(Failed 표기, 원격 성공) · d 회수
        Assert.True(j.MarkUserCanceled("d"));
        var r = await j.RecoverAsync();
        Assert.Equal(2, r.Recovered);
        Assert.Single(j.LoadAll());
        Assert.Equal("d", j.LoadAll()[0].TaskId);

        // [전체 비우기]
        Assert.Equal(1, j.PurgeAll());
        Assert.Equal(0, j.PurgeAll());
        Assert.Equal(0, j.Count);
        Assert.False(File.Exists(path));
        Assert.True(changed > 0);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ColorGradingService_BuildsCorrectFfmpegEqFilters_ForProfiles()
    {
        Assert.Null(ColorGradingService.BuildFfmpegEqFilter(LutProfile.None));
        Assert.Equal("eq=contrast=1.2:brightness=-0.08:saturation=1.1", ColorGradingService.BuildFfmpegEqFilter(LutProfile.PureOledBlack));
        Assert.Equal("eq=contrast=1.15:saturation=1.35:gamma_g=1.1", ColorGradingService.BuildFfmpegEqFilter(LutProfile.MatrixNeon));
        Assert.Equal("eq=saturation=0.5:contrast=1.1", ColorGradingService.BuildFfmpegEqFilter(LutProfile.QuantMono));
        Assert.Equal("eq=gamma_r=1.15:gamma_b=0.85:contrast=1.05", ColorGradingService.BuildFfmpegEqFilter(LutProfile.WarmAmber));
        Assert.Equal(5, ColorGradingService.All.Length);
        foreach (var p in ColorGradingService.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(ColorGradingService.Label(p)));
            Assert.False(string.IsNullOrWhiteSpace(ColorGradingService.CssFilter(p)));
        }
        Assert.Equal("none", ColorGradingService.CssFilter(LutProfile.None));
        Assert.Contains("contrast(1.2)", ColorGradingService.CssFilter(LutProfile.PureOledBlack));

        // 체인 결합 — 단독 · 기존 체인 뒤 · None 무변경
        Assert.Equal(ColorGradingService.QuantMonoEq, ColorGradingService.AppendTo(null, LutProfile.QuantMono));
        Assert.Equal("fps=60," + ColorGradingService.QuantMonoEq, ColorGradingService.AppendTo("fps=60", LutProfile.QuantMono));
        Assert.Equal("fps=60", ColorGradingService.AppendTo("fps=60", LutProfile.None));
        Assert.Null(ColorGradingService.AppendTo(null, LutProfile.None));

        // StockSuite -vf — 스케일 → 60fps → LUT → 워터마크 순(워터마크 원색 유지)
        Assert.Equal(ColorGradingService.WarmAmberEq, StockSuiteExporter.BuildVideoFilter(false, false, StockSuiteResolution.Fhd1080, false, null, lut: LutProfile.WarmAmber));
        Assert.Null(StockSuiteExporter.BuildVideoFilter(false, false, StockSuiteResolution.Fhd1080, false, null, lut: LutProfile.None));
        var full = StockSuiteExporter.BuildVideoFilter(true, true, StockSuiteResolution.Uhd4K, true, null, lut: LutProfile.MatrixNeon)!;
        var iEq = full.IndexOf(ColorGradingService.MatrixNeonEq, StringComparison.Ordinal);
        Assert.True(iEq > full.IndexOf("minterpolate", StringComparison.Ordinal));
        Assert.True(iEq < full.IndexOf("drawtext", StringComparison.Ordinal));

        // 멀티 패키지 HTS 규격에도 번인
        var src = Path.Combine(_dir, "src.mp4");
        var profiles = StockSuiteExporter.BuildMultiTargetProfiles(src, new MultiExportOptions { Lut = LutProfile.PureOledBlack }, _dir, null);
        MultiExportProfile? hts = null;
        foreach (var p in profiles) if (p.Key == "hts") hts = p;
        Assert.NotNull(hts);
        var vf = hts.Args[Array.IndexOf(hts.Args, "-vf") + 1];
        Assert.EndsWith("," + ColorGradingService.PureOledBlackEq, vf);
    }

    private sealed class ScriptedHandler(Queue<Func<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(script.Dequeue()());
        }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ResilientHttpClient_RetriesOnTransientErrors_AndSucceeds()
    {
        var delays = new List<TimeSpan>();
        var notices = new List<(int Cur, int Max, TimeSpan D)>();
        var rc = new ResilientHttpClient { Delay = (d, ct) => { delays.Add(d); return Task.CompletedTask; }, Jitter = static () => 250 };
        rc.OnRetryAttempt += (c, m, d) => notices.Add((c, m, d));
        rc.OnRetryAttempt += (_, _, _) => throw new InvalidOperationException("구독자 예외 격리");

        // 429 → 네트워크 순단 → 503 → 200
        var handler = new ScriptedHandler(new Queue<Func<HttpResponseMessage>>(new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => throw new HttpRequestException("connection reset"),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") },
        }));
        using var http = new HttpClient(handler);
        var built = 0;
        using var resp = await rc.SendAsync(http, () => { built++; return new HttpRequestMessage(HttpMethod.Get, "https://dashscope.example/api/v1/tasks/x"); });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(4, built);   // 매 시도 요청 재생성
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(2250), TimeSpan.FromMilliseconds(4250) }, delays);
        Assert.Equal(3, notices.Count);
        Assert.Equal((1, ResilientHttpClient.MaxRetries), (notices[0].Cur, notices[0].Max));
        Assert.Equal(3, notices[2].Cur);

        // 비일시 오류(400) — 재시도 없음
        var h2 = new ScriptedHandler(new Queue<Func<HttpResponseMessage>>(new Func<HttpResponseMessage>[] { () => new HttpResponseMessage(HttpStatusCode.BadRequest) }));
        using var http2 = new HttpClient(h2);
        using var r2 = await rc.SendAsync(http2, () => new HttpRequestMessage(HttpMethod.Get, "https://x.example/"));
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
        Assert.Equal(1, h2.Calls);

        // 재시도 소진 — 마지막 504 응답 반환(최대 3회 재시도 = 4회 호출)
        var q = new Queue<Func<HttpResponseMessage>>();
        for (var i = 0; i < 4; i++) q.Enqueue(() => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        var h3 = new ScriptedHandler(q);
        using var http3 = new HttpClient(h3);
        using var r3 = await rc.SendAsync(http3, () => new HttpRequestMessage(HttpMethod.Get, "https://x.example/"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, r3.StatusCode);
        Assert.Equal(4, h3.Calls);

        // 지연 공식 · 지터 클램프 · 일시 오류 판정
        Assert.Equal(TimeSpan.FromSeconds(1), ResilientHttpClient.ComputeDelay(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(4500), ResilientHttpClient.ComputeDelay(3, 9999));
        Assert.True(ResilientHttpClient.IsTransient(HttpStatusCode.BadGateway));
        Assert.False(ResilientHttpClient.IsTransient(HttpStatusCode.InternalServerError));
    }
}
