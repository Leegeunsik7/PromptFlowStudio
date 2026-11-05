using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.ProcessBridge;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>듀얼 트랙 마켓 게이트 · Process.Start 단일 관문 · Win32 런타임 플랫폼 가드 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class GovernanceOptTests
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    [Fact]
    public async Task LightweightProbe_BypassesHeavyMarketGate_DuringMarketHours()
    {
        // 2026-11-05(목) 10:00 KST = 정규장
        using var guard = new TradingSafetyGuardService(static () => true) { UtcNow = static () => new DateTimeOffset(2026, 11, 5, 10, 0, 0, Kst) };
        Assert.True(guard.IsThrottling);

        var heavyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeavy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heavy = guard.RunCategorizedAsync(ProcessCategory.HeavyEncoding, async _ =>
        {
            heavyEntered.SetResult();
            await releaseHeavy.Task;
            return true;
        });
        await heavyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, guard.ActiveSlots);

        // 경량 프로브: 중량 슬롯 점유 중에도 즉시 완료(독립 퍼밋)
        var probeRanDuringHeavy = false;
        var probe = await guard.RunCategorizedAsync(ProcessCategory.LightweightProbe, _ =>
        {
            probeRanDuringHeavy = guard.ActiveSlots == 1 && guard.ActiveProbeSlots == 1;
            return Task.FromResult(true);
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(probe);
        Assert.True(probeRanDuringHeavy);
        Assert.Equal(0, guard.ActiveProbeSlots);

        // 두 번째 중량 작업은 여전히 대기(단일 게이트 유지)
        var heavy2 = guard.RunCategorizedAsync(ProcessCategory.HeavyEncoding, static _ => Task.FromResult(true));
        await Task.Delay(100);
        Assert.False(heavy2.IsCompleted);

        releaseHeavy.SetResult();
        Assert.True(await heavy.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await heavy2.WaitAsync(TimeSpan.FromSeconds(5)));

        // 자동 분류: ffprobe · 1프레임 = 경량, 일반 인코딩 = 중량
        Assert.Equal(ProcessCategory.LightweightProbe, ProcessRunner.Classify(@"C:\tools\ffprobe.exe", new[] { "-v", "error", "a.mp4" }));
        Assert.Equal(ProcessCategory.LightweightProbe, ProcessRunner.Classify("ffmpeg", new[] { "-y", "-ss", "1", "-i", "a.mp4", "-frames:v", "1", "o.png" }));
        Assert.Equal(ProcessCategory.LightweightProbe, ProcessRunner.Classify("ffmpeg", new[] { "-i", "a.mp4", "-vframes", "1", "o.png" }));
        Assert.Equal(ProcessCategory.HeavyEncoding, ProcessRunner.Classify("ffmpeg", new[] { "-i", "a.mp4", "-c:v", "libvpx-vp9", "o.webm" }));
        Assert.Equal(ProcessCategory.HeavyEncoding, ProcessRunner.Classify("ffmpeg", new[] { "-i", "a.mp4", "-frames:v", "120", "o.mp4" }));
    }

    [Fact]
    public async Task ProcessRunner_LaunchExternalAsync_HandlesValidAndInvalidTargets()
    {
        IProcessRunner runner = SystemProcessRunner.Gateway;

        // 검증 통과 대상(실행 없이 판정)
        Assert.True(ProcessRunner.ValidateExternalTarget("https://example.com/path?q=1"));
        Assert.True(ProcessRunner.ValidateExternalTarget(Path.GetTempPath()));
        Assert.True(ProcessRunner.ValidateExternalTarget("explorer.exe", "/select,\"C:\\a b.txt\""));

        // 무효 대상 → false(throw 없음 · 실행 없음)
        Assert.False(await runner.LaunchExternalAsync(""));
        Assert.False(await runner.LaunchExternalAsync("   "));
        Assert.False(await runner.LaunchExternalAsync("javascript://alert(1)"));
        Assert.False(await runner.LaunchExternalAsync("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(await runner.LaunchExternalAsync(Path.Combine(Path.GetTempPath(), "pfs_missing_" + Guid.NewGuid().ToString("N") + ".exe")));
        Assert.False(await runner.LaunchExternalAsync("https://example.com\n--evil"));
        Assert.False(await runner.LaunchExternalAsync("notepad.exe", "a\0b"));
        Assert.False(await runner.LaunchExternalAsync("pfs_no_such_tool_" + Guid.NewGuid().ToString("N") + ".exe", "", openWithShell: false));   // PATH 부재 → Win32Exception 격리
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.False(await runner.LaunchExternalAsync("https://example.com", ct: cts.Token));
        }

        // 유효 실행 파일(무창 · 즉시 종료) → true
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(await runner.LaunchExternalAsync(cmd, "/c exit 0", openWithShell: false));

        // 커스텀 대역: 기본 LaunchExternalAsync 가 인스턴스 LaunchExternal 로 위임
        var fake = new FakeLauncher();
        Assert.True(await ((IProcessRunner)fake).LaunchExternalAsync("https://example.com", "x"));
        Assert.Equal(new[] { "https://example.com|x|True" }, fake.Calls);
    }

    [Fact]
    public void PlatformGuards_PreventNativeExceptions_OnNonWindows()
    {
        var png = Path.Combine(Path.GetTempPath(), "pfs_wp_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        try
        {
            using var monitor = new StockSuiteProcessMonitor(
                static () => @"C:\StockSuite\StockSuite.exe",
                static _ => new StockSuiteProcessSnapshot(true, 4242, 1024, new IntPtr(0x1234)));

            using (NativePlatformGuard.SimulateNonWindows())
            {
                Assert.False(NativePlatformGuard.IsWindows);
                Assert.False(StockSuiteExporter.TrySetDesktopWallpaper(png));   // SystemParametersInfo 미호출
                Assert.False(monitor.BringStockSuiteToFront());                 // ShowWindow/SetForegroundWindow 미호출
            }
            Assert.Equal(OperatingSystem.IsWindows(), NativePlatformGuard.IsWindows);   // 범위 종료 시 복원
            Assert.False(StockSuiteExporter.TrySetDesktopWallpaper(null));
        }
        finally { try { File.Delete(png); } catch (IOException) { } }
    }

    private sealed class FakeLauncher : IProcessRunner
    {
        public readonly List<string> Calls = new();

        public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
            Action<bool, string>? onLine = null, CancellationToken ct = default)
            => Task.FromResult(new ProcessResult(false, -1, "", ""));

        public bool LaunchExternal(string target, string arguments = "", bool openWithShell = true)
        {
            Calls.Add(target + "|" + arguments + "|" + openWithShell);
            return true;
        }
    }
}
