using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Chart;
using PromptFlowStudio.Services.Export;
using Xunit;

namespace PromptFlowStudio.Tests;

public class ProWorkflowDirectorHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pro_" + Guid.NewGuid().ToString("N"));

    public ProWorkflowDirectorHealthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoNetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("must not call");
    }

    private static Task<IpcProbeResult> HealthyIpc(CancellationToken _) => Task.FromResult(new IpcProbeResult(IntegrationHealth.Healthy, 1.5, "ok"));

    private string Assets => Path.Combine(_dir, "assets");

    private void SeedHealthyAssets()
    {
        Directory.CreateDirectory(Assets);
        var active = Path.Combine(Assets, StockSuiteExporter.DefaultAssetName);
        File.WriteAllBytes(active, new byte[] { 1, 2, 3, 4, 5 });
        var backup = StockSuiteExporter.BackupPathFor(active);
        File.WriteAllBytes(backup, new byte[] { 9, 9, 9 });
        var m = new StockSuiteManifest(StockSuiteExporter.DefaultAssetName, "r1", StockSuiteExporter.ComputeSha256Hex(active), 5, DateTimeOffset.UtcNow,
            Path.GetFileName(backup), "r0", StockSuiteExporter.ComputeSha256Hex(backup), 3);
        File.WriteAllText(Path.Combine(Assets, StockSuiteExporter.ManifestName), StockSuiteExporter.BuildManifestJson(m));
        File.WriteAllText(Path.Combine(Assets, StockSuiteExporter.DisplayConfigName),
            StockSuiteExporter.BuildDisplayConfigJson("bg_screen_1.webm", "bg_screen_2.webm", 40, DateTimeOffset.UtcNow));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task TickerAutoDirector_ExecutesFullPipeline_OnTickerSelected()
    {
        // 실서비스 결선(차트 래스터라이저 + 키 없는 Gemini 로컬 폴백) — 차트 → 바인딩 → 프롬프트 순서 검증
        var order = new List<string>();
        var chartSvc = new ChartStillGeneratorService { UtcNow = static () => new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero) };
        var gemini = new GeminiPromptService(new HttpClient(new NoNetHandler()), () => null);
        string? bound = null;
        var director = new TickerAutoDirector(
            async (t, ct) => { order.Add("chart"); return await chartSvc.GenerateAsync(t, _dir, new ChartStillOptions { Width = 640, Height = 360, Candles = 40 }, ct); },
            async (t, n, s, ct) => { order.Add("prompt"); return await gemini.GenerateTickerPromptAsync(t, n, s, ct); },
            (png, ct) => { order.Add("bind"); bound = png; return Task.CompletedTask; });

        var r = await director.RunAsync("NVDA", "엔비디아", "반도체");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { "chart", "bind", "prompt" }, order);
        Assert.True(r.ChartOk && r.FrameBound);
        Assert.NotNull(r.ChartPath);
        Assert.True(File.Exists(r.ChartPath));
        Assert.Equal(r.ChartPath, bound);
        var head = new byte[8];
        using (var fs = File.OpenRead(r.ChartPath!)) Assert.Equal(8, fs.Read(head, 0, 8));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);
        Assert.Contains("NVDA", r.Prompt!, StringComparison.OrdinalIgnoreCase);
        Assert.True(r.PromptFallback);
        Assert.False(director.IsRunning);

        // [부분 구제] 차트 실패 → 프롬프트는 보존 · 바인딩 미호출
        var bindCalls = 0;
        var partial = new TickerAutoDirector(
            (_, _) => throw new IOException("disk full"),
            (t, n, s, ct) => gemini.GenerateTickerPromptAsync(t, n, s, ct),
            (_, _) => { bindCalls++; return Task.CompletedTask; });
        var p = await partial.RunAsync("TSLA");
        Assert.False(p.Ok);
        Assert.False(p.ChartOk);
        Assert.Equal(0, bindCalls);
        Assert.Contains("TSLA", p.Prompt!, StringComparison.OrdinalIgnoreCase);

        // 빈 티커 즉시 거절 · 취소 전파
        Assert.False((await director.RunAsync("  ")).ChartOk);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => director.RunAsync("AAPL", ct: cts.Token));
        Assert.False(director.IsRunning);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteHealthService_ValidatesHealthyState_WhenAllFilesAreValid()
    {
        SeedHealthyAssets();
        var svc = new StockSuiteHealthService(() => Assets) { IpcProbe = HealthyIpc };
        var r = await svc.ValidateIntegrationHealthAsync();
        Assert.Equal(IntegrationHealth.Healthy, r.Overall);
        Assert.Equal(4, r.Items.Count);
        foreach (var it in r.Items) Assert.Equal(IntegrationHealth.Healthy, it.Status);
        Assert.False(r.NeedsRepair);
        Assert.Equal(1.5, r.IpcLatencyMs);
        Assert.Empty(Directory.GetFiles(Assets, ".pfs-health-*"));   // 권한 프로브 파일 잔존 0

        // 실 Named Pipe 프로브 — 고유 파이프 서버 응답 지연 측정
        var pipe = "PfsHealthTest_" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        var live = new StockSuiteHealthService(() => Assets, null, pipe);
        var lr = await live.ValidateIntegrationHealthAsync();
        Assert.Equal(IntegrationHealth.Healthy, lr.Find(IntegrationCheck.IpcPipeConnection)!.Status);
        Assert.True(lr.IpcLatencyMs >= 0);
        await accept.WaitAsync(TimeSpan.FromSeconds(5));

        // 서버 없는 파이프 → Unhealthy(타임아웃)
        var dead = new StockSuiteHealthService(() => Assets, null, "PfsHealthDead_" + Guid.NewGuid().ToString("N"));
        var dr = await dead.ValidateIntegrationHealthAsync();
        Assert.Equal(IntegrationHealth.Unhealthy, dr.Overall);
        Assert.Equal(-1, dr.IpcLatencyMs);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteHealthService_DiagnosesBrokenConfiguration_AndRepairsSuccessfully()
    {
        SeedHealthyAssets();
        var displayPath = Path.Combine(Assets, StockSuiteExporter.DisplayConfigName);
        var manifestPath = Path.Combine(Assets, StockSuiteExporter.ManifestName);
        File.WriteAllText(displayPath, "{\"mode\":\"dual\",\"screens\":[{\"asset\":\"a<b>.webm\"");   // 잘린 JSON
        File.WriteAllText(manifestPath, "{ not json");
        File.Delete(StockSuiteExporter.BackupPathFor(Path.Combine(Assets, StockSuiteExporter.DefaultAssetName)));

        var svc = new StockSuiteHealthService(() => Assets) { IpcProbe = HealthyIpc };
        var before = await svc.ValidateIntegrationHealthAsync();
        Assert.Equal(IntegrationHealth.Unhealthy, before.Overall);
        Assert.Equal(IntegrationHealth.Healthy, before.Find(IntegrationCheck.AssetDirectory)!.Status);
        Assert.Equal(IntegrationHealth.Unhealthy, before.Find(IntegrationCheck.DisplaySchema)!.Status);
        Assert.Equal(IntegrationHealth.Unhealthy, before.Find(IntegrationCheck.ManifestIntegrity)!.Status);
        Assert.True(before.NeedsRepair);

        var fix = await svc.RepairAsync(before);
        Assert.True(fix.Ok);
        Assert.Equal(2, fix.Repaired);
        Assert.Equal(IntegrationHealth.Healthy, fix.After.Overall);
        Assert.Null(StockSuiteHealthService.ValidateDisplayJson(File.ReadAllBytes(displayPath)));
        var m = StockSuiteExporter.TryReadManifest(Assets);
        Assert.NotNull(m);
        Assert.Equal(StockSuiteExporter.DefaultAssetName, m!.ActiveAsset);
        Assert.Equal(StockSuiteExporter.ComputeSha256Hex(Path.Combine(Assets, StockSuiteExporter.DefaultAssetName)), m.SourceSha256);
        Assert.Null(m.BackupAsset);
        Assert.Equal("{ not json", File.ReadAllText(manifestPath + StockSuiteHealthService.CorruptSuffix));   // 원본 보존
        Assert.True(File.Exists(displayPath + StockSuiteHealthService.CorruptSuffix));

        // 누락 폴더(상위 존재) → 생성 + 디스플레이 기본 규격 · 에셋 없음 = 매니페스트 불필요
        var fresh = Path.Combine(_dir, "fresh");
        var svc2 = new StockSuiteHealthService(() => fresh) { IpcProbe = HealthyIpc };
        var b2 = await svc2.ValidateIntegrationHealthAsync();
        Assert.Equal(IntegrationHealth.Unhealthy, b2.Overall);
        Assert.True(b2.Find(IntegrationCheck.AssetDirectory)!.Repairable);
        var f2 = await svc2.RepairAsync(b2);
        Assert.True(Directory.Exists(fresh));
        Assert.Equal(IntegrationHealth.Healthy, f2.After.Overall);

        // 미설정 경로 → 복구 불가 Unhealthy
        var none = await new StockSuiteHealthService(() => null) { IpcProbe = HealthyIpc }.ValidateIntegrationHealthAsync();
        Assert.Equal(IntegrationHealth.Unhealthy, none.Overall);
        Assert.False(none.Find(IntegrationCheck.AssetDirectory)!.Repairable);
    }
}
