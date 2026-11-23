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

/// <summary>듀얼 트랙 마켓 게이트 · Process.Start 단일 관문 · Win32 런타임 플랫폼 가드 회귀 잠금.
///   Step 41 — 실 <c>cmd.exe</c> 생성은 전체 스위트 병렬 구간의 프로세스 생성 경합에서 1회 실패한 이력이 있어
///   <c>ProcessEnvironment</c>(DisableParallelization) 컬렉션으로 병렬 구간 뒤 단독 실행 + 유효 대상만 유한 재시도.</summary>
[Trait("Category", "FastUnit")]
[Collection("ProcessEnvironment")]
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

        // 유효 실행 파일(무창 · 즉시 종료) → true — 검증 통과를 먼저 단언(검증 회귀는 재시도 없이 즉시 실패)한 뒤
        // 전체 스위트 부하의 일시적 cmd.exe 생성 실패(Win32Exception → 격리 false)만 유한 재시도로 흡수
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(ProcessRunner.ValidateExternalTarget(cmd, "/c exit 0"), "유효 대상 검증 회귀");
        var (launched, attempts) = await LaunchValidWithTransientRetryAsync(runner, cmd, "/c exit 0");
        Assert.True(launched, "유효 대상 실행 실패(" + attempts + "회 시도)");

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

    /// <summary>Step 41 — 유효 대상 실행 재시도 상한(일시적 프로세스 생성 경합 흡수 · 무효 대상 단언에는 쓰지 않음).</summary>
    internal const int TransientLaunchAttempts = 3;

    /// <summary>검증을 통과한 유효 대상만 최대 <see cref="TransientLaunchAttempts"/> 회 지수 백오프(100 · 200 ms) 재시도. 반환 = (성공, 시도 횟수).</summary>
    internal static async Task<(bool Launched, int Attempts)> LaunchValidWithTransientRetryAsync(IProcessRunner runner, string target, string arguments, int maxAttempts = TransientLaunchAttempts)
    {
        if (!ProcessRunner.ValidateExternalTarget(target, arguments)) return (false, 0);   // 무효 대상은 재시도로 가리지 않음
        for (var attempt = 1; ; attempt++)
        {
            if (await runner.LaunchExternalAsync(target, arguments, openWithShell: false)) return (true, attempt);
            if (attempt >= maxAttempts) return (false, attempt);
            await Task.Delay(100 << (attempt - 1));
        }
    }

    [Fact]
    public async Task TransientLaunchRetry_RecoversFromSpawnContention_ButNeverRetriesInvalidTargets()
    {
        // 처음 2회 생성 실패 → 3회째 성공
        var flaky = new FlakyLauncher(failures: 2);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Equal((true, 3), await LaunchValidWithTransientRetryAsync(flaky, cmd, "/c exit 0"));
        Assert.Equal(3, flaky.Calls);

        // 상한 초과 → 실패 보고(무한 재시도 없음)
        var dead = new FlakyLauncher(failures: int.MaxValue);
        Assert.Equal((false, TransientLaunchAttempts), await LaunchValidWithTransientRetryAsync(dead, cmd, "/c exit 0"));
        Assert.Equal(TransientLaunchAttempts, dead.Calls);

        // 무효 대상 → 실행 시도 0 회(재시도가 검증 실패를 가리지 않음)
        var never = new FlakyLauncher(failures: 0);
        Assert.Equal((false, 0), await LaunchValidWithTransientRetryAsync(never, "javascript://alert(1)", ""));
        Assert.Equal((false, 0), await LaunchValidWithTransientRetryAsync(never, cmd, "a\0b"));
        Assert.Equal(0, never.Calls);
    }

    private sealed class FlakyLauncher(int failures) : IProcessRunner
    {
        public int Calls;

        public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
            Action<bool, string>? onLine = null, CancellationToken ct = default)
            => Task.FromResult(new ProcessResult(false, -1, "", ""));

        public bool LaunchExternal(string target, string arguments = "", bool openWithShell = true) => ++Calls > failures;
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
