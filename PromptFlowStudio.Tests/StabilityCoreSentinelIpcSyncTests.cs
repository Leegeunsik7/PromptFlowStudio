using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.ProcessBridge;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>사전 디스크 센티넬 · IPC 재접속 워치독 · 듀얼 스크린 동기 매니페스트 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class StabilityCoreSentinelIpcSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_sc_" + Guid.NewGuid().ToString("N"));

    public StabilityCoreSentinelIpcSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const long Mb = 1024 * 1024;

    [Fact]
    public async Task PreFlightResourceSentinel_PreventsEncoding_WhenDiskSpaceIsLow()
    {
        var low = new PreFlightResourceSentinel { FreeSpaceProvider = _ => 1024 * Mb };
        var r = low.CheckDiskSpace(_dir);
        Assert.False(r.Ok);
        Assert.Equal(1024, r.FreeMb);
        Assert.Equal(5120, r.RequiredMb);
        Assert.Equal("Insufficient disk space: 1024MB available, 5120MB required.", r.Message);

        Assert.True(low.CheckDiskSpace(_dir, 512 * Mb).Ok);
        Assert.True(new PreFlightResourceSentinel { FreeSpaceProvider = _ => null }.CheckDiskSpace(_dir).Ok);          // 조회 불가 = 통과
        Assert.True(new PreFlightResourceSentinel { FreeSpaceProvider = _ => throw new IOException("x") }.CheckDiskSpace(_dir).Ok);
        Assert.NotNull(PreFlightResourceSentinel.QueryFreeSpace(_dir));

        // 4K 내보내기 → ffmpeg 착수 전 Fail-Fast(러너 미호출 · 대상 미생성)
        var src = Path.Combine(_dir, "src.mp4");
        File.WriteAllText(src, "v");
        var outDir = Path.Combine(_dir, "assets");
        var calls = 0;
        var exp = new StockSuiteExporter(() => outDir, () => "mp4", () => "ffmpeg", (_, a, _) =>
        {
            Interlocked.Increment(ref calls);
            File.WriteAllBytes(a[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        }) { PreFlight = low };
        Assert.False(exp.CanSafelyEncode(5120 * Mb));
        Assert.True(exp.CanSafelyEncode(100 * Mb));

        var ex = await exp.ExportAsync(src, resolution: StockSuiteResolution.Uhd4K);
        Assert.False(ex.Ok);
        Assert.StartsWith("Insufficient disk space", ex.Message);
        Assert.Equal(0, calls);

        var dual = await exp.ExportDualMonitorSplitAsync(src, 40);
        Assert.False(dual.Ok);
        Assert.StartsWith("Insufficient disk space", dual.Message);
        Assert.Equal(0, calls);
        Assert.Empty(Directory.GetFiles(outDir));
    }

    [Fact]
    public async Task StockSuiteIpcBridge_TransitionsToReconnecting_AndRestoresConnection()
    {
        var pipeName = "pfs_test_" + Guid.NewGuid().ToString("N");
        using var bridge = new StockSuiteIpcBridge(() => _dir, pipeName) { ReconnectInterval = TimeSpan.FromMilliseconds(50) };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawReconnecting = 0;
        bridge.OnConnectionStateChanged += s =>
        {
            if (s == IpcConnectionState.Reconnecting) { Volatile.Write(ref sawReconnecting, 1); reconnecting.TrySetResult(); }
            else if (s == IpcConnectionState.Connected)
            {
                if (Volatile.Read(ref sawReconnecting) == 1) restored.TrySetResult(); else connected.TrySetResult();
            }
            throw new InvalidOperationException("구독자 예외 격리 확인");
        };
        Assert.Equal(IpcConnectionState.Disconnected, bridge.ConnectionState);
        Assert.False(await bridge.BroadcastMasterClockSyncAsync("s", 0, 15, 60));   // 미연결 = false

        bridge.Start();
        var c1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c1.ConnectAsync(5000);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IpcConnectionState.Connected, bridge.ConnectionState);

        // StockSuite 종료 → 🟡 Reconnecting · 크래시 없이 파일 폴백 유지
        c1.Dispose();
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(bridge.IsRunning);
        Assert.Equal(IpcSendRoute.FileFallback, await bridge.SendThemeAsync(HtsHudTheme.OledBlack, 10));

        // StockSuite 재시작 → 자동 재접속 → Connected 복귀 + MasterClockSync 송신
        using var c2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c2.ConnectAsync(5000);
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(bridge.IsConnected);
        Assert.True(await bridge.BroadcastMasterClockSyncAsync("sync_x", 0, 15.0, 60));
        using var reader = new StreamReader(c2, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(line!);
        Assert.Equal("MasterClockSync", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("timestamp").GetInt64() > 0);

        // 프레임 오프셋 산술(음수 경과·무효 길이 방어)
        Assert.Equal((2.5, 150L), StockSuiteIpcBridge.ComputeSyncOffset(1_000, 1_000 + 32_500, 15.0, 60));
        Assert.Equal((14.0, 840L), StockSuiteIpcBridge.ComputeSyncOffset(1_000, 0, 15.0, 60));
        Assert.Equal((0.0, 0L), StockSuiteIpcBridge.ComputeSyncOffset(0, 5_000, 0, 60));
    }

    [Fact]
    public async Task DualMonitorExport_GeneratesSyncManifest_WithPreciseTimingData()
    {
        var src = Path.Combine(_dir, "wide.mp4");
        File.WriteAllText(src, "v");
        var outDir = Path.Combine(_dir, "assets");
        var at = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var exp = new StockSuiteExporter(() => outDir, () => "mp4", () => "ffmpeg", (_, a, _) =>
        {
            File.WriteAllBytes(a[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        })
        {
            UtcNow = () => at,
            PreFlight = new PreFlightResourceSentinel { FreeSpaceProvider = _ => 50L * 1024 * Mb },
        };

        var r = await exp.ExportDualMonitorSplitAsync(src, 40);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(Path.Combine(Path.GetFullPath(outDir), StockSuiteExporter.DisplaySyncName), r.SyncManifestPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(r.SyncManifestPath!));
        var root = doc.RootElement;
        Assert.Equal("sync_20261105_120000", root.GetProperty("syncId").GetString());
        Assert.Equal(15.0, root.GetProperty("loopDurationSec").GetDouble());
        Assert.Equal(60, root.GetProperty("fps").GetInt32());
        Assert.Equal("bg_screen_1.mp4", root.GetProperty("leftScreen").GetString());
        Assert.Equal("bg_screen_2.mp4", root.GetProperty("rightScreen").GetString());
        Assert.Equal(at.ToUnixTimeMilliseconds(), root.GetProperty("masterEpochUtc").GetInt64());
        Assert.Equal(1793880000000L, root.GetProperty("masterEpochUtc").GetInt64());
        Assert.Empty(Directory.GetFiles(outDir, "*.~pfs*"));

        // 무효 루프/FPS → 기본·클램프
        using var d2 = JsonDocument.Parse(StockSuiteExporter.BuildDisplaySyncJson("a", "b", double.NaN, 9999, at));
        Assert.Equal(15.0, d2.RootElement.GetProperty("loopDurationSec").GetDouble());
        Assert.Equal(240, d2.RootElement.GetProperty("fps").GetInt32());
    }
}
