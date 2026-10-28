using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Telemetry;
using PromptFlowStudio.Services.Operations;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.AI.Vision;
using PromptFlowStudio.Services.Integration;

namespace PromptFlowStudio.Tests;

[Trait("Category", "FastUnit")]
public class SecureSecretVaultTests
{
    [Fact]
    public void ResolveOrCreate_SameKeyName_ReturnsByteForByteIdenticalKey()
    {
        var vault = new SecureSecretVault();
        var k1 = vault.ResolveOrCreate("xunit-roundtrip-key");
        var k2 = vault.ResolveOrCreate("xunit-roundtrip-key");
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void ResolveOrCreate_DifferentKeyNames_ReturnDifferentKeys()
    {
        var vault = new SecureSecretVault();
        var k1 = vault.ResolveOrCreate("xunit-key-a");
        var k2 = vault.ResolveOrCreate("xunit-key-b");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void Wipe_ByteSpan_ZeroesEveryElement()
    {
        var buf = new byte[32];
        new Random(1).NextBytes(buf);
        Assert.Contains(buf, b => b != 0);   // 사전조건: 실제 비영 데이터가 있어야 의미 있는 검증
        SecureSecretVault.Wipe(buf.AsSpan());
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Wipe_CharSpan_ZeroesEveryElement()
    {
        var buf = "sensitive-data-1234".ToCharArray();
        SecureSecretVault.Wipe(buf.AsSpan());
        Assert.All(buf, c => Assert.Equal('\0', c));
    }
}

[Trait("Category", "FastUnit")]
public class ProductionTelemetryExporterTests
{
    private static (ProductionTelemetryExporter Exporter, OtlpTelemetryVisualizerProvider Visualizer) Build()
    {
        var vault = new SecureSecretVault();
        var agg = new EnterpriseAuditTelemetryAggregator();
        var exporter = new ProductionTelemetryExporter(agg, vault);
        var visualizer = new OtlpTelemetryVisualizerProvider(exporter);
        return (exporter, visualizer);
    }

    [Fact]
    public void Flush_RealBatch_MasksAllPiiFieldsAndReflectsInVisualizer()
    {
        var (exporter, visualizer) = Build();
        exporter.SubmitPiiSample(); exporter.SubmitPiiSample(); exporter.SubmitPiiSample();

        var r = exporter.Flush();
        Assert.Equal(3, r.RecordCount);
        Assert.Equal(9, r.MaskedFields);   // 엔트리당 email+ip+phone 3종 × 3건
        Assert.Equal(1, visualizer.Snapshot.WindowSize);
        Assert.Equal(9, visualizer.Snapshot.TotalMaskedFields);
    }

    [Fact]
    public void Flush_EmptyBatch_ExcludedFromAggregation_ButKeptInRawHistory()
    {
        var (exporter, visualizer) = Build();
        exporter.SubmitPiiSample();
        exporter.Flush();   // 실배치 1건
        var beforeWindow = visualizer.Snapshot.WindowSize;
        var beforeRawCount = exporter.RecentBatches.Count;

        var empty = exporter.Flush();   // 신규 제출 없음 → 빈 플러시(RecordCount=0)
        Assert.Equal(0, empty.RecordCount);
        Assert.Equal(beforeWindow, visualizer.Snapshot.WindowSize);         // 집계엔 영향 없음(F4 버그 수정 회귀 방지)
        Assert.Equal(beforeRawCount + 1, exporter.RecentBatches.Count);    // 원시 이력은 빈 플러시도 완전 기록
    }

    [Fact]
    public void RecentBatches_ExceedsCapacity_RetainsOnlyBoundedNewestWindow()
    {
        var (exporter, _) = Build();
        for (var i = 0; i < 80; i++) { exporter.SubmitPiiSample(); exporter.Flush(); }
        Assert.True(exporter.RecentBatches.Count <= 64);
    }
}

[Trait("Category", "Integration")]
public class HotfixAutoSignerEngineTests
{
    [Fact]
    public void SignAndExport_ProducesManifestAndSigFiles_WithConsistentDigestAndSignature()
    {
        var vault = new SecureSecretVault();
        var sealer = new QuantumResistantStateSealer(vault);
        var patcher = new LtsHotfixPatcher(sealer);
        var validator = new ReleaseIntegrityValidator(sealer);
        var engine = new HotfixAutoSignerEngine(patcher, validator);

        var patchPath = Path.Combine(Path.GetTempPath(), $"xunit_patch_{Guid.NewGuid():N}.zip");
        var payload = new byte[128];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(patchPath, payload);

        try
        {
            var result = engine.SignAndExport(patchPath, "10.7.0", $"xunit-hotfix-{Guid.NewGuid():N}");
            Assert.True(result.Ok, result.Note);
            Assert.True(File.Exists(result.ManifestPath));
            Assert.True(File.Exists(result.SigPath));

            var json = File.ReadAllText(result.ManifestPath!);
            var manifest = JsonSerializer.Deserialize<HotfixManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(manifest);
            Assert.Equal(64, manifest!.DigestHex.Length);      // 32바이트 다이제스트(SHA3-256 또는 SHA-256 폴백 모두 32바이트)
            Assert.Equal(64, manifest.SignatureHex.Length);    // HMAC-SHA256 서명은 항상 32바이트
            Assert.Equal(8, manifest.Digest8.Length);

            var sigContent = File.ReadAllText(result.SigPath!).Trim();
            Assert.Equal(manifest.SignatureHex, sigContent);   // .sig 파일 내용이 매니페스트의 서명과 정확히 일치

            File.Delete(result.ManifestPath!);
            File.Delete(result.SigPath!);
        }
        finally { File.Delete(patchPath); }
    }

    [Fact]
    public void SignAndExport_MissingFile_ReturnsGracefulFailure_NoException()
    {
        var vault = new SecureSecretVault();
        var sealer = new QuantumResistantStateSealer(vault);
        var patcher = new LtsHotfixPatcher(sealer);
        var validator = new ReleaseIntegrityValidator(sealer);
        var engine = new HotfixAutoSignerEngine(patcher, validator);

        var result = engine.SignAndExport(@"C:\definitely\not\real\ghost.zip", "1.0.0", "xunit-ghost");
        Assert.False(result.Ok);
        Assert.Null(result.ManifestPath);
        Assert.Null(result.SigPath);
    }
}

[Trait("Category", "Integration")]
public class OperationsCanaryLogRetentionCleanerTests
{
    [Fact]
    public void LogRetentionCleaner_DeletesExpiredFiles_Successfully()
    {
        var tempDir = Directory.CreateTempSubdirectory("pfs-retention-test-");
        try
        {
            var oldFile = Path.Combine(tempDir.FullName, "canary-old.log");
            var freshFile = Path.Combine(tempDir.FullName, "canary-fresh.log");
            var unrelatedFile = Path.Combine(tempDir.FullName, "shell.log");   // 패턴 미매칭 - 보존돼야 함
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(freshFile, "fresh");
            File.WriteAllText(unrelatedFile, "unrelated");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));

            using var cleaner = new OperationsCanaryLogRetentionCleaner("Local\\pfs_test_retention_" + Guid.NewGuid().ToString("N")[..8]);
            var result = cleaner.RunCleanup(logsRootOverride: tempDir.FullName);

            Assert.Equal(1, result.FilesDeleted);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(freshFile));
            Assert.True(File.Exists(unrelatedFile));
            Assert.Equal(1, cleaner.TotalRuns);
            Assert.Equal(1, cleaner.TotalDeleted);
            Assert.Empty(result.Errors);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public void RunCleanup_LogsFolderMissing_ReturnsZeroGracefully()
    {
        var tempDir = Directory.CreateTempSubdirectory("pfs-retention-missing-");
        var neverCreated = Path.Combine(tempDir.FullName, "does-not-exist");
        try
        {
            using var cleaner = new OperationsCanaryLogRetentionCleaner("Local\\pfs_test_retention_" + Guid.NewGuid().ToString("N")[..8]);
            var result = cleaner.RunCleanup(logsRootOverride: neverCreated);

            Assert.Equal(0, result.FilesScanned);
            Assert.Equal(0, result.FilesDeleted);
            Assert.Empty(result.Errors);
        }
        finally { tempDir.Delete(recursive: true); }
    }

    [Fact]
    public void RunCleanup_MultipleRuns_RingBufferAndCountersAccumulateCorrectly()
    {
        var tempDir = Directory.CreateTempSubdirectory("pfs-retention-multi-");
        try
        {
            using var cleaner = new OperationsCanaryLogRetentionCleaner("Local\\pfs_test_retention_" + Guid.NewGuid().ToString("N")[..8]);
            for (var i = 0; i < 3; i++)
            {
                var f = Path.Combine(tempDir.FullName, $"canary-{i}.log");
                File.WriteAllText(f, "x");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-8));
                cleaner.RunCleanup(logsRootOverride: tempDir.FullName);
            }

            Assert.Equal(3, cleaner.TotalRuns);
            Assert.Equal(3, cleaner.TotalDeleted);
            Assert.NotNull(cleaner.LastResult);
        }
        finally { tempDir.Delete(recursive: true); }
    }
}
[Trait("Category", "Integration")]
public class MobileDiRegistrationScanTests
{
    // MauiProgram.cs 의 Wan/텔레메트리/보안 플랫폼 비의존 등록 구간(#if ANDROID/IOS 없는 v10.x~v11.x 블록)을
    //   미러링한다. PromptFlowStudio.Mobile 은 net8.0-android/net8.0-ios 전용이라 이 테스트 프로젝트
    //   (net8.0-windows)가 TFM 불일치로 직접 참조할 수 없어 - 목록을 수동 동기화한다(MauiProgram.cs 갱신 시
    //   이 목록도 함께 갱신). 목적: "데스크톱엔 등록했는데 모바일에 빠뜨림" 회귀 방지 - MauiProgram.cs 자체
    //   주석이 이미 이 사고(HeterogeneousTaskScheduler/WanDynamicMotionController/LoRAHotSwapManager 누락 시
    //   WanPostProcessingEngine DI 파괴)를 2회 기록하고 있다.
    [Fact]
    public void MobileWanTelemetrySecurityChain_AllReflectivelyResolve_NoMissingDependency()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SecureSecretVault>();
        services.AddSingleton<EnterpriseAuditTelemetryAggregator>();
        services.AddSingleton<ProductionTelemetryExporter>();
        services.AddSingleton<OtlpTelemetryVisualizerProvider>();
        services.AddSingleton<AutoTunerHardwareCalibrator>();
        services.AddSingleton<LivePipelineAutoTuner>();
        services.AddSingleton<HeterogeneousTaskScheduler>();
        services.AddSingleton<WanDynamicMotionController>();
        services.AddSingleton<LoRAHotSwapManager>();
        services.AddSingleton<AutonomousNeuralTilingUpscaler>();
        services.AddSingleton<MultimodalAudioSyncEngine>();
        services.AddSingleton<WanPostProcessingEngine>();
        services.AddSingleton<WanProgressivePreviewer>();
        services.AddSingleton<OperationsCanaryHealthChecker>();
        services.AddSingleton<AlibabaWanLowSpecEngine>();
        services.AddSingleton<WanPerformanceBenchmarker>();
        services.AddSingleton<BenchmarkingReportGenerator>();
        services.AddSingleton<ComfyUiWanConnector>();

        using var provider = services.BuildServiceProvider();

        var failures = new List<string>();
        foreach (var descriptor in services)
        {
            try { provider.GetRequiredService(descriptor.ServiceType); }
            catch (Exception ex) { failures.Add($"{descriptor.ServiceType.Name}: {ex.GetType().Name} - {ex.Message}"); }
        }

        Assert.True(failures.Count == 0, "DI 해석 실패(리플렉션 스캔):" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}

[Trait("Category", "Integration")]
public class ProductionArtifactPackagerTests
{
    [Fact]
    public void ResolveActualOutputDir_FilesDirectlyPresent_ReturnsSameDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("pfs-pkgtest-flat-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "app.dll"), "x");
            Assert.Equal(dir.FullName, ProductionArtifactPackager.ResolveActualOutputDir(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ResolveActualOutputDir_SingleRidSubfolderWithFiles_DescendsOneLevel()
    {
        var dir = Directory.CreateTempSubdirectory("pfs-pkgtest-rid-");
        try
        {
            var ridDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "android-arm64"));
            File.WriteAllText(Path.Combine(ridDir.FullName, "app.dll"), "x");
            Assert.Equal(ridDir.FullName, ProductionArtifactPackager.ResolveActualOutputDir(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ResolveActualOutputDir_TrulyEmpty_ReturnsOriginalPathForGracefulSkip()
    {
        var dir = Directory.CreateTempSubdirectory("pfs-pkgtest-empty-");
        try { Assert.Equal(dir.FullName, ProductionArtifactPackager.ResolveActualOutputDir(dir.FullName)); }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ResolveActualOutputDir_MultipleSubfoldersAmbiguous_ReturnsOriginalPath()
    {
        var dir = Directory.CreateTempSubdirectory("pfs-pkgtest-ambig-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "a"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "b"));
            Assert.Equal(dir.FullName, ProductionArtifactPackager.ResolveActualOutputDir(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }
}
[Trait("Category", "FastUnit")]
public class OperationsCanaryHealthCheckerTests
{
    [Fact]
    public void CanaryHealthCheck_ReturnsSuccessOnNormalState()
    {
        var vault = new SecureSecretVault();
        var agg = new EnterpriseAuditTelemetryAggregator();
        var exporter = new ProductionTelemetryExporter(agg, vault);
        var visualizer = new OtlpTelemetryVisualizerProvider(exporter);
        var tuner = new LivePipelineAutoTuner(new AutoTunerHardwareCalibrator());

        using var canary = new OperationsCanaryHealthChecker(vault, visualizer, tuner);
        var report = canary.RunProbe();

        Assert.Equal(CanaryStatus.Healthy, report.Status);
        Assert.Equal(3, report.Signals.Count);
        Assert.All(report.Signals, s => Assert.True(s.Ok, s.Name + ": " + s.Detail));
        Assert.Equal(1, canary.Probes);
        Assert.Equal(0, canary.Alerts);
    }

    [Fact]
    public void CanaryHealthCheck_TunerStuckOverBudget_DegradesAfterThreeStreaks()
    {
        var vault = new SecureSecretVault();
        var agg = new EnterpriseAuditTelemetryAggregator();
        var exporter = new ProductionTelemetryExporter(agg, vault);
        var visualizer = new OtlpTelemetryVisualizerProvider(exporter);
        var tuner = new LivePipelineAutoTuner(new AutoTunerHardwareCalibrator());

        using var canary = new OperationsCanaryHealthChecker(vault, visualizer, tuner);
        for (var i = 0; i < 40; i++) tuner.ObserveFrame(LivePipelineAutoTuner.TargetFrameMs * 3);   // 지속 초과 유발(Int8 도달 전 구간 존재)

        CanaryReport? last = null;
        for (var i = 0; i < 4; i++) last = canary.RunProbe();   // 1회차는 내부 시퀀스 초기화(-1→실측치) 매칭이라 스트릭 미증가 · 이후 3회 연속 매칭이 필요

        Assert.NotNull(last);
        Assert.True(canary.Alerts >= 1, "3틱 연속 초과 관측 후에는 최소 1회 저하 판정이 있어야 한다.");
    }
}

/// <summary>v13.0.1: 디바운스/지연 실행 크래시 안전성(async void 제거) - 예외 격리·원자 대체·파기 안전을 신호로만 검증.</summary>
[Trait("Category", "Integration")]
public class SafeDebouncerTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Forever = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task FaultingWork_NeverEscapes_AndIsReportedToHandler()
    {
        Exception? seen = null;
        using var d = new PromptFlowStudio.Services.Ui.SafeDebouncer(ex => seen = ex);
        var run = d.Schedule(TimeSpan.Zero, _ => throw new InvalidOperationException("주소 API 실패"));
        await run.WaitAsync(SafetyNet);   // 예외로 끝나지 않는다(=스레드풀 재던짐·프로세스 종료 경로 없음)
        Assert.True(run.IsCompletedSuccessfully);
        Assert.IsType<InvalidOperationException>(seen);
        Assert.Equal(1, d.Faulted);
    }

    [Fact]
    public async Task ThrowingErrorHandler_IsAlsoContained()
    {
        using var d = new PromptFlowStudio.Services.Ui.SafeDebouncer(_ => throw new Exception("핸들러 결함"));
        var run = d.Schedule(TimeSpan.Zero, _ => throw new InvalidOperationException());
        await run.WaitAsync(SafetyNet);
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RapidReschedule_OnlyLastRuns_EarlierAreCanceled()
    {
        using var d = new PromptFlowStudio.Services.Ui.SafeDebouncer();
        var ran = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var runs = new List<Task>();
        for (var i = 0; i < 49; i++) { var n = i; runs.Add(d.Schedule(Forever, _ => { ran.Enqueue(n); return Task.CompletedTask; })); }
        runs.Add(d.Schedule(TimeSpan.Zero, _ => { ran.Enqueue(49); return Task.CompletedTask; }));

        await Task.WhenAll(runs).WaitAsync(SafetyNet);   // 대체된 49건도 전부 즉시 종료(10분 대기 없음)
        Assert.Equal(new[] { 49 }, ran.ToArray());
        Assert.Equal(1, d.Executed);
        Assert.Equal(49, d.Superseded);
    }

    [Fact]
    public async Task SupersededWork_ReceivesCancellation_MidFlight()
    {
        using var d = new PromptFlowStudio.Services.Ui.SafeDebouncer();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = d.Schedule(TimeSpan.Zero, async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await started.Task.WaitAsync(SafetyNet);
        var second = d.Schedule(Forever, _ => Task.CompletedTask);   // 새 예약 → 진행 중인 첫 작업에 취소 전파
        await first.WaitAsync(SafetyNet);
        Assert.Equal(1, d.Superseded);
        d.Cancel();
        await second.WaitAsync(SafetyNet);
        Assert.Equal(2, d.Superseded);
    }

    [Fact]
    public async Task Dispose_CancelsPending_AndIgnoresLaterSchedules()
    {
        var d = new PromptFlowStudio.Services.Ui.SafeDebouncer();
        var invoked = 0;
        var pending = d.Schedule(Forever, _ => { Interlocked.Increment(ref invoked); return Task.CompletedTask; });
        d.Dispose();
        d.Dispose();   // 중복 Dispose 무해
        await pending.WaitAsync(SafetyNet);
        var after = d.Schedule(TimeSpan.Zero, _ => { Interlocked.Increment(ref invoked); return Task.CompletedTask; });
        Assert.True(after.IsCompleted);
        await Task.Yield();
        Assert.Equal(0, Volatile.Read(ref invoked));
        Assert.True(d.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentScheduleAndDispose_FromManyThreads_NoExceptionAndAllTasksComplete()
    {
        var d = new PromptFlowStudio.Services.Ui.SafeDebouncer();
        var tasks = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var go = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            go.Wait();
            for (var i = 0; i < 200; i++)
                tasks.Add(d.Schedule(i % 3 == 0 ? TimeSpan.Zero : Forever, _ => (i & 1) == 0 ? Task.CompletedTask : throw new InvalidOperationException()));
        })).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        Thread.Sleep(1);
        d.Dispose();
        threads.ForEach(t => t.Join());
        await Task.WhenAll(tasks).WaitAsync(SafetyNet);   // 파기 이후 대기 예약도 전부 종료, 어떤 Task 도 예외로 끝나지 않음
        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully));
    }
}

/// <summary>v13.0.1: 모바일 DI 미등록 회귀 잠금 - 모바일 루트 트리 @inject 가 MauiProgram 계열 등록에 모두 있는지 + 신규 7종 실해소.</summary>
[Trait("Category", "Integration")]
public class MobileRootDiRegistrationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "PromptFlowStudio.Mobile"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("저장소 루트(PromptFlowStudio.Mobile)를 찾지 못함");
    }

    private static readonly System.Text.RegularExpressions.Regex InjectRx = new(@"^\s*@inject\s+([\w\.<>]+)\s+\w+", System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex RegRx = new(@"(?:Try)?Add(?:Singleton|Scoped|Transient)<([^>]+(?:<[^>]*>)?)>", System.Text.RegularExpressions.RegexOptions.None);

    private static string Simple(string typeName) { var t = typeName.Trim(); var lt = t.IndexOf('<'); if (lt >= 0) t = t[..lt]; var dot = t.LastIndexOf('.'); return dot >= 0 ? t[(dot + 1)..] : t; }

    [Fact]
    public void MobileRootTree_EveryInjectedService_IsRegisteredForMobile()
    {
        var root = RepoRoot();
        string[] registrySources =
        {
            Path.Combine(root, "PromptFlowStudio.Mobile", "MauiProgram.cs"),
            Path.Combine(root, "PromptFlowStudio.Mobile", "Services", "MobileServiceRegistry.cs"),
            Path.Combine(root, "PromptFlowStudio.Shared", "SharedServiceRegistry.cs"),
        };
        var registered = new HashSet<string>(StringComparer.Ordinal) { "NavigationManager", "IJSRuntime" };   // Blazor 호스트 제공
        foreach (var src in registrySources)
            foreach (System.Text.RegularExpressions.Match m in RegRx.Matches(File.ReadAllText(src)))
                foreach (var part in m.Groups[1].Value.Split(',')) registered.Add(Simple(part));

        // 모바일 Main.razor 가 호스팅하는 루트 덱과 그 직속 자식(드로어·캔버스)
        string[] rootTree = { "MobileStudioDeck.razor", "TrueFlowCanvas.razor", "MobileNavDrawer.razor" };
        var missing = new List<string>();
        foreach (var file in rootTree)
        {
            var text = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Components", "Common", file));
            foreach (System.Text.RegularExpressions.Match m in InjectRx.Matches(text))
            {
                var name = Simple(m.Groups[1].Value);
                if (!registered.Contains(name)) missing.Add($"{file}: @inject {m.Groups[1].Value}");
            }
        }
        Assert.True(missing.Count == 0, "모바일 DI 미등록(루트 마운트 시 UI 전체 붕괴):" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void SevenPreviouslyOrphanedServices_ResolveWithTheirConstructorDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());   // MAUI 빌더 구성 대역
        services.AddSingleton<IVideoMergeService, VideoMergeService>();
        services.AddSingleton<ILocalMediaService, LocalMediaService>();
        services.AddSingleton<PromptFlowStudio.Services.Ui.SidebarGuideService>();
        services.AddSingleton<PromptFlowStudio.Services.Wan.NodeWorkerService>();
        services.AddSingleton<PromptFlowStudio.Services.Wan.DistributedRenderGrid>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.WebhookDispatcher>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.WebRtcStreamer>();
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(sp.GetRequiredService<ILocalMediaService>());
        Assert.NotNull(sp.GetRequiredService<PromptFlowStudio.Services.Wan.DistributedRenderGrid>());
        Assert.NotNull(sp.GetRequiredService<PromptFlowStudio.Services.Ui.SidebarGuideService>());
        Assert.NotNull(sp.GetRequiredService<PromptFlowStudio.Services.Integration.WebhookDispatcher>());
        Assert.NotNull(sp.GetRequiredService<PromptFlowStudio.Services.Integration.WebRtcStreamer>());
    }

    [Fact]
    public void McpConfig_RestoresBothServers_AsValidJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), ".mcp.json")));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("playwright", out _));
        Assert.True(servers.TryGetProperty("chrome-devtools", out _));
    }
}

/// <summary>v13.0.2: MCP 공급망 고정 - .mcp.json 의 npx 패키지가 정확한 semver 로 핀돼 있는지(latest/next/범위/무버전 금지) 정적 진단.</summary>
[Trait("Category", "Integration")]
public class McpSupplyChainTests
{
    private static readonly System.Text.RegularExpressions.Regex ExactPin =
        new(@"^(?:@[a-z0-9][\w.-]*/)?[a-z0-9][\w.-]*@\d+\.\d+\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string McpPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".mcp.json"))) dir = dir.Parent;
        return dir is null ? throw new FileNotFoundException(".mcp.json 을 찾지 못함") : Path.Combine(dir.FullName, ".mcp.json");
    }

    /// <summary>npx 실행 인자에서 패키지 스펙만 추려 정확 핀 여부를 판정(위반 사유 목록 반환).</summary>
    internal static List<string> Diagnose(JsonElement servers)
    {
        var problems = new List<string>();
        foreach (var server in servers.EnumerateObject())
        {
            var cmd = server.Value.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var args = server.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
            var npxAt = cmd.Equals("npx", StringComparison.OrdinalIgnoreCase) ? -1 : args.FindIndex(x => x.Equals("npx", StringComparison.OrdinalIgnoreCase));
            if (!cmd.Equals("npx", StringComparison.OrdinalIgnoreCase) && npxAt < 0) continue;   // npx 가 아닌 서버는 대상 외
            var specs = args.Skip(npxAt + 1).Where(x => !x.StartsWith('-')).ToList();
            if (specs.Count == 0) { problems.Add($"{server.Name}: npx 패키지 스펙 없음"); continue; }
            var spec = specs[0];   // npx 뒤 첫 비옵션 인자 = 실행 패키지
            if (spec.EndsWith("@latest", StringComparison.OrdinalIgnoreCase) || spec.EndsWith("@next", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{server.Name}: 유동 태그 '{spec}'");
            else if (!ExactPin.IsMatch(spec))
                problems.Add($"{server.Name}: 정확한 버전 고정 아님 '{spec}'(범위·무버전·태그 금지)");
        }
        return problems;
    }

    [Fact]
    public void McpConfig_IsValidJson_AndEveryNpxServerIsPinnedToExactVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(McpPath()));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("playwright", out _));
        Assert.True(servers.TryGetProperty("chrome-devtools", out _));
        var problems = Diagnose(servers);
        Assert.True(problems.Count == 0, "MCP 공급망 고정 위반:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void McpConfig_NpxServers_PreferOfflineCache()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(McpPath()));
        foreach (var server in doc.RootElement.GetProperty("mcpServers").EnumerateObject())
        {
            Assert.True(server.Value.TryGetProperty("env", out var env) && env.TryGetProperty("npm_config_prefer_offline", out var po) && po.GetString() == "true",
                $"{server.Name}: npm_config_prefer_offline=true 누락(캐시 우선 - 불필요한 레지스트리 재조회 차단)");
        }
    }

    [Theory]
    [InlineData("@playwright/mcp@latest", false)]
    [InlineData("chrome-devtools-mcp@next", false)]
    [InlineData("chrome-devtools-mcp@^1.10.1", false)]
    [InlineData("chrome-devtools-mcp@~1.10", false)]
    [InlineData("chrome-devtools-mcp", false)]
    [InlineData("@playwright/mcp@0.0", false)]
    [InlineData("@playwright/mcp@0.0.83", true)]
    [InlineData("chrome-devtools-mcp@1.10.1", true)]
    public void Diagnose_FlagsFloatingOrRangedSpecs(string spec, bool ok)
    {
        using var doc = JsonDocument.Parse("{\"s\":{\"command\":\"cmd\",\"args\":[\"/c\",\"npx\",\"-y\",\"" + spec + "\"]}}");
        Assert.Equal(ok, Diagnose(doc.RootElement).Count == 0);
    }
}

/// <summary>v13.2.0: MCP 격리 설치 - tools/mcp 의 package-lock(전 트리 sha512·레지스트리 출처·정확 핀)과 .mcp.json 실행 경로 정합성 정적 검증.
///   node_modules 존재는 요구하지 않는다(CI 등 미설치 환경에서도 lock·설정만으로 판정).</summary>
[Trait("Category", "Integration")]
public class McpLockfileIsolationTests
{
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        ["@playwright/mcp"] = "0.0.83",
        ["chrome-devtools-mcp"] = "1.10.1",
    };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".mcp.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("저장소 루트(.mcp.json)를 찾지 못함");
    }

    private static JsonDocument Read(params string[] parts) => JsonDocument.Parse(File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray())));

    /// <summary>lock 의 모든 설치 패키지 무결성 진단(위반 목록). 링크(workspace) 항목은 대상 외.</summary>
    internal static List<string> DiagnoseLock(JsonElement lockRoot)
    {
        var problems = new List<string>();
        if (!lockRoot.TryGetProperty("lockfileVersion", out var lv) || lv.GetInt32() < 3) problems.Add("lockfileVersion 3 미만(전 트리 packages 맵 필요)");
        if (!lockRoot.TryGetProperty("packages", out var packages)) { problems.Add("packages 맵 없음"); return problems; }
        var count = 0;
        foreach (var p in packages.EnumerateObject())
        {
            if (p.Name.Length == 0) continue;   // 루트 항목
            if (p.Value.TryGetProperty("link", out var link) && link.GetBoolean()) continue;
            count++;
            var integrity = p.Value.TryGetProperty("integrity", out var i) ? i.GetString() ?? "" : "";
            var resolved = p.Value.TryGetProperty("resolved", out var r) ? r.GetString() ?? "" : "";
            if (!integrity.StartsWith("sha512-", StringComparison.Ordinal) || integrity.Length < 20) problems.Add($"{p.Name}: sha512 integrity 없음");
            if (!resolved.StartsWith("https://registry.npmjs.org/", StringComparison.Ordinal)) problems.Add($"{p.Name}: 공식 레지스트리 외 출처 '{resolved}'");
        }
        if (count == 0) problems.Add("설치 패키지 0건");
        return problems;
    }

    [Fact]
    public void Lock_EveryPackage_HasSha512Integrity_FromOfficialRegistry()
    {
        using var doc = Read("tools", "mcp", "package-lock.json");
        var problems = DiagnoseLock(doc.RootElement);
        Assert.True(problems.Count == 0, "lock 무결성 위반:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void PackageJsonAndLock_TopLevelPins_AreExactAndAgree()
    {
        using var pkg = Read("tools", "mcp", "package.json");
        using var lk = Read("tools", "mcp", "package-lock.json");
        var deps = pkg.RootElement.GetProperty("dependencies");
        var lockRootDeps = lk.RootElement.GetProperty("packages").GetProperty("").GetProperty("dependencies");
        foreach (var (name, version) in Pins)
        {
            Assert.Equal(version, deps.GetProperty(name).GetString());                       // 범위(^·~) 금지 - 정확 버전
            Assert.Equal(version, lockRootDeps.GetProperty(name).GetString());
            Assert.Equal(version, lk.RootElement.GetProperty("packages").GetProperty("node_modules/" + name).GetProperty("version").GetString());
        }
        Assert.Equal(Pins.Count, deps.EnumerateObject().Count());   // 핀 외 추가 의존 없음
    }

    [Fact]
    public void Npmrc_BlocksInstallScripts_AndSavesExact()
    {
        var npmrc = File.ReadAllText(Path.Combine(Root(), "tools", "mcp", ".npmrc"));
        Assert.Contains("ignore-scripts=true", npmrc);   // 설치 스크립트(postinstall 등) 실행 차단
        Assert.Contains("save-exact=true", npmrc);
    }

    [Fact]
    public void McpConfig_RunsFromLockedTree_ViaNodeBin_NotNpx_WithOfflineOverrides()
    {
        using var mcp = Read(".mcp.json");
        using var lk = Read("tools", "mcp", "package-lock.json");
        var packages = lk.RootElement.GetProperty("packages");
        var servers = mcp.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("playwright", out _));
        Assert.True(servers.TryGetProperty("chrome-devtools", out _));

        foreach (var s in servers.EnumerateObject())
        {
            Assert.Equal("node", s.Value.GetProperty("command").GetString());
            var args = s.Value.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToList();
            Assert.DoesNotContain(args, a => a.Equals("npx", StringComparison.OrdinalIgnoreCase) || a.Contains("@latest", StringComparison.OrdinalIgnoreCase));

            const string Prefix = "tools/mcp/node_modules/";
            var script = args[0];
            Assert.StartsWith(Prefix, script);
            // 스크립트 경로 = node_modules/<패키지>/<bin 상대경로> 이고 그 bin 이 lock 에 기록된 패키지의 bin 과 일치해야 한다
            var match = packages.EnumerateObject()
                .Where(p => p.Name.StartsWith("node_modules/", StringComparison.Ordinal) && script.StartsWith("tools/mcp/" + p.Name + "/", StringComparison.Ordinal))
                .OrderByDescending(p => p.Name.Length).FirstOrDefault();
            Assert.False(match.Value.ValueKind == JsonValueKind.Undefined, $"{s.Name}: 실행 경로가 lock 된 패키지 밖 '{script}'");
            var rel = script["tools/mcp/".Length..][(match.Name.Length + 1)..];
            var bins = match.Value.GetProperty("bin").EnumerateObject().Select(b => (b.Value.GetString() ?? "").TrimStart('.', '/')).ToList();
            Assert.Contains(rel, bins);
            var pkgName = match.Name["node_modules/".Length..];
            Assert.True(Pins.ContainsKey(pkgName), $"{s.Name}: 핀 대상이 아닌 패키지 '{pkgName}'");

            var env = s.Value.GetProperty("env");
            Assert.Equal("true", env.GetProperty("npm_config_prefer_offline").GetString());
            Assert.Equal("true", env.GetProperty("npm_config_offline").GetString());
        }
    }

    [Theory]
    [InlineData("{\"lockfileVersion\":3,\"packages\":{\"\":{},\"node_modules/a\":{\"version\":\"1.0.0\",\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\",\"integrity\":\"sha512-AAAAAAAAAAAAAAAAAAAAAAAAAA==\"}}}", true)]
    [InlineData("{\"lockfileVersion\":3,\"packages\":{\"\":{},\"node_modules/a\":{\"version\":\"1.0.0\",\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\"}}}", false)]
    [InlineData("{\"lockfileVersion\":3,\"packages\":{\"\":{},\"node_modules/a\":{\"version\":\"1.0.0\",\"resolved\":\"https://evil.example/a.tgz\",\"integrity\":\"sha512-AAAAAAAAAAAAAAAAAAAAAAAAAA==\"}}}", false)]
    [InlineData("{\"lockfileVersion\":3,\"packages\":{\"\":{},\"node_modules/a\":{\"version\":\"1.0.0\",\"resolved\":\"https://registry.npmjs.org/a/-/a-1.0.0.tgz\",\"integrity\":\"sha1-AAAAAAAAAAAAAAAAAAAAAAAAAA==\"}}}", false)]
    [InlineData("{\"lockfileVersion\":1,\"dependencies\":{}}", false)]
    public void DiagnoseLock_RejectsMissingWeakOrForeignIntegrity(string lockJson, bool ok)
    {
        using var doc = JsonDocument.Parse(lockJson);
        Assert.Equal(ok, DiagnoseLock(doc.RootElement).Count == 0);
    }
}

/// <summary>v14.0.0: 릴리스 번들 배포 제외 규칙 - 디버그 심볼·publish 중간산출·최상위 RID 폴더는 제외, runtimes/&lt;rid&gt;/native 는 유지.</summary>
[Trait("Category", "Integration")]
public class ProductionArtifactPackagerExclusionTests
{
    [Theory]
    [InlineData("PromptFlowStudio.dll", true)]
    [InlineData("PromptFlowStudio.pdb", false)]
    [InlineData("PromptFlowStudio.Shared.PDB", false)]
    [InlineData("win-x64/PromptFlowStudio.dll", false)]
    [InlineData("win-x64/publish/PromptFlowStudio.exe", false)]
    [InlineData("publish/PromptFlowStudio.exe", false)]
    [InlineData("runtimes/win-x64/native/WebView2Loader.dll", true)]
    [InlineData("wwwroot/css/app.css", true)]
    [InlineData("ko/PromptFlowStudio.resources.dll", true)]
    [InlineData(@"win-arm64\foo.dll", false)]
    [InlineData("publisher.json", true)]   // 'publish' 로 시작하는 파일명은 폴더 세그먼트가 아니므로 유지
    public void IsShippable_FiltersDebugAndStaleOutputs(string rel, bool shippable)
        => Assert.Equal(shippable, ProductionArtifactPackager.IsShippable(rel));

    [Fact]
    public async Task PackageReleaseZip_ExcludesPdbPublishAndRidFolders_KeepsRuntimeAssets()
    {
        var src = Path.Combine(Path.GetTempPath(), "pfs_pkg_excl_" + Guid.NewGuid().ToString("N")[..8]);
        void Touch(string rel) { var p = Path.Combine(src, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, rel); }
        foreach (var rel in new[] { "App.dll", "App.pdb", "win-x64/App.dll", "win-x64/publish/App.exe", "runtimes/win-x64/native/WebView2Loader.dll", "wwwroot/index.html" }) Touch(rel);
        var version = "test-excl-" + Guid.NewGuid().ToString("N")[..8];
        var packager = new ProductionArtifactPackager();
        string? zipPath = null;
        try
        {
            var report = await packager.PackageReleaseZipAsync(version, windowsSourceDirOverride: src);
            zipPath = report.ZipPath;
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("windows/", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToArray();   // 형제 슬롯(shared 등)은 실행 위치에 따라 포함될 수 있음
            Assert.Equal(new[] { "windows/App.dll", "windows/runtimes/win-x64/native/WebView2Loader.dll", "windows/wwwroot/index.html" }, names);
            Assert.Equal(3, report.Included.Single(s => s.Platform == PackagePlatform.WindowsDesktop).FileCount);
            Assert.True(packager.ExcludedFileCount >= 3);
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { }
            if (zipPath is not null) try { File.Delete(zipPath); } catch { }
        }
    }
}
/// <summary>v14.0.1: 카나리 키 Resolve 는 32건 감사 링버퍼를 잠식하지 않고 전용 카운터로만 집계.</summary>
[Trait("Category", "Integration")]
public class SecureSecretVaultCanaryIsolationTests
{
    [Fact]
    public void CanaryResolve_DoesNotEvictUserAuditHistory()
    {
        var vault = new SecureSecretVault();
        for (var i = 0; i < 32; i++) vault.TryGetExternalCredential("ISO_NONEXIST_" + i);   // env 없음 → 미기록
        Environment.SetEnvironmentVariable("PROMPTFLOW_SECRET_ISO_USER_KEY", "dGVzdA==");
        try
        {
            vault.ResolveOrCreate("ISO_USER_KEY");
            for (var i = 0; i < 100; i++) vault.ResolveOrCreate(SecureSecretVault.CanaryKeyName);
            Assert.Equal(1, vault.History.Count(h => h.KeyName == "ISO_USER_KEY"));
            Assert.DoesNotContain(vault.History, h => h.KeyName == SecureSecretVault.CanaryKeyName);
            Assert.Equal(100, vault.CanaryResolved);
            Assert.Equal(1, vault.Resolved);
        }
        finally { Environment.SetEnvironmentVariable("PROMPTFLOW_SECRET_ISO_USER_KEY", null); }
    }
}

/// <summary>v14.2.0: 장기 운용 스트레스 — 프레임 Blit·VLM 백프레셔·P2P 메시 재라우팅 동시 10,000회 후 잔류 메모리·Gen2 GC 0 검증.
///   프로세스 전역 GC 지표를 쓰므로 병렬 비활성 컬렉션에서 단독 실행(타 테스트 할당 오염 차단).</summary>
[Collection("ProcessGcMode")]
[Trait("Category", "Integration")]
public class LongRunSustainedLoadTests
{
    private const int Iterations = 10_000;
    private const int VlmReleaseEvery = 500;

    // 게이트형 VLM — 테스트가 Release() 할 때까지 응답을 보류해 백프레셔(드롭) 경로를 결정적으로 유도.
    private sealed class GatedVlm : IVisionLanguageClient
    {
        private TaskCompletionSource<VisionVlmReply?>? _pending;
        public bool Enabled => true;
        public Task<VisionVlmReply?> DescribeAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<VisionVlmReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, tcs);
            return tcs.Task;
        }
        public void Release() => Interlocked.Exchange(ref _pending, null)?.TrySetResult(new VisionVlmReply(VisionScene.HighAction, 0.9, "stress"));
    }

    private static long FullCollectRetained()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Fact]
    public async Task LongRun_SustainedLoad_ZeroMemoryLeak_Check()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var pipe = new LiveStreamVirtualCamPipe("Local\\pfs_test_lr_map_" + tag, "Local\\pfs_test_lr_mtx_" + tag);
        var vlm = new GatedVlm();
        using var analyzer = new VisionContextAnalyzer(pipe, vlm, TimeSpan.FromSeconds(30));
        var mesh = new QuantumP2pMeshRouter();
        mesh.SeedDemoMesh(9);
        var frameA = new byte[64 * 36 * 4]; Array.Fill(frameA, (byte)60);
        var frameB = new byte[64 * 36 * 4]; Array.Fill(frameB, (byte)180);

        void Blit(int i)
        {
            pipe.SubmitFrame((i & 1) == 0 ? frameA : frameB, 64, 36, VirtualCamPixelFormat.Rgba32);
            analyzer.Tick();
            if (i % VlmReleaseEvery == 0) vlm.Release();
        }
        void Route(int i)
        {
            mesh.Route("edge-02", "p", chaosMidRoute: (i & 1) == 0);   // v15.0.0: Zero-Alloc 코어(봉투 미물질화)
            if ((i & 1) == 0) mesh.ReviveAll();
        }
        void DrainVlm()
        {
            // 보류 응답 해제 후 in-flight 해제까지 상한 대기(무한 대기 금지).
            Assert.True(SpinWait.SpinUntil(() => { vlm.Release(); return analyzer.VlmHealthState != VlmHealth.InFlight; }, 10_000));
        }

        // 워밍업: JIT·ArrayPool 대여(8MB LOH)·메시 키·멱등 집합 초기화를 기준선 밖으로.
        for (var i = 0; i < 300; i++) { Blit(i); Route(i); }
        DrainVlm();
        var samples0 = analyzer.Samples; var requests0 = analyzer.VlmRequests; var dropped0 = analyzer.VlmDropped;

        var retainedBefore = FullCollectRetained();
        var gen2Before = GC.CollectionCount(2);
        var blockingFullBefore = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;   // 기준선 FullCollectRetained 가 마지막 블로킹 풀 GC
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);

        long blitThreadAlloc = 0;
        var blitTask = Task.Factory.StartNew(() =>
        {
            var t0 = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 1; i <= Iterations; i++) Blit(i);
            blitThreadAlloc = GC.GetAllocatedBytesForCurrentThread() - t0;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var meshTask = Task.Factory.StartNew(() => { for (var i = 1; i <= Iterations; i++) Route(i); },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(blitTask, meshTask).WaitAsync(TimeSpan.FromMinutes(2));
        DrainVlm();

        var gen2During = GC.CollectionCount(2) - gen2Before;
        var blockingFull = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var bgGen2 = GC.GetGCMemoryInfo(GCKind.Background);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        var retainedGrowth = FullCollectRetained() - retainedBefore;

        var requests = analyzer.VlmRequests - requests0;
        var dropped = analyzer.VlmDropped - dropped0;
        Assert.Equal(Iterations, analyzer.Samples - samples0);                  // 프레임 손실 없음(모두 새 시퀀스)
        Assert.Equal(Iterations, requests + dropped);                           // 매 틱은 VLM 요청 또는 백프레셔 드롭 중 정확히 하나
        Assert.InRange(requests, 1, Iterations / VlmReleaseEvery + 2);          // 느린 VLM 이 요청을 누적시키지 않음
        Assert.True(mesh.RerouteCount >= Iterations / 2, "chaos 송신마다 자가치유 재라우팅 발생");
        Assert.True(blitThreadAlloc <= (requests + 1) * 4096, $"Blit 핫패스 할당 {blitThreadAlloc}B (VLM 기동 {requests}회분만 허용)");
        // v14.3.1: 부하 중 블로킹 Gen2(프레임 정지) 0건. 백그라운드(동시) Gen2 는 진단용으로만 표기.
        //   v15.0.0: 메시 경로는 Route(0B)로 전환 - 과거 ~200MB 승격원 제거(엄격 0B·Gen2 0 단언은 MeshRouterZeroAllocTests).
        Assert.True(blockingFull.Index == blockingFullBefore,
            $"부하 중 블로킹 Gen2 발생(idx {blockingFullBefore}→{blockingFull.Index}) · Gen2 총 {gen2During} · 백그라운드 idx {bgGen2.Index} · 총 할당 {allocated}B");
        Assert.True(retainedGrowth < 2 * 1024 * 1024, $"잔류 메모리 증가 {retainedGrowth}B (총 할당 {allocated}B)");
    }
}

/// <summary>v14.2.0: 장기 운용 보조 하네스 — 디바운서 대량 대체 시 CTS 잔류 0 · 카나리 반복 프로브의 감사 이력 비잠식.</summary>
[Trait("Category", "FastUnit")]
public class LongRunResourceHygieneTests
{
    [Fact]
    public async Task SafeDebouncer_10kSupersededSchedules_AllAccounted_LastOnlyExecuted()
    {
        using var deb = new PromptFlowStudio.Services.Ui.SafeDebouncer();
        var runs = 0;
        var tasks = new Task[10_000];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = deb.Schedule(TimeSpan.FromMilliseconds(500), _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, runs);                                        // 마지막 예약만 실행
        Assert.Equal(10_000, deb.Scheduled);
        Assert.Equal(deb.Scheduled, deb.Executed + deb.Superseded);   // 모든 예약이 실행 또는 취소로 종결(미아 CTS 0)
        Assert.Equal(0, deb.Faulted);
    }

    [Fact]
    public void Canary_1kProbes_NeverEnterVaultAuditRing()
    {
        var vault = new SecureSecretVault();
        var visualizer = new OtlpTelemetryVisualizerProvider(new ProductionTelemetryExporter(new EnterpriseAuditTelemetryAggregator(), vault));
        using var canary = new OperationsCanaryHealthChecker(vault, visualizer, new LivePipelineAutoTuner(new AutoTunerHardwareCalibrator()));
        var userBefore = vault.History.Count;

        for (var i = 0; i < 1_000; i++) canary.RunProbe();

        Assert.Equal(1_000, vault.CanaryResolved);
        Assert.DoesNotContain(vault.History, h => h.KeyName == SecureSecretVault.CanaryKeyName);
        Assert.True(vault.History.Count <= 32);
        Assert.True(vault.History.Count >= userBefore);               // 프로브가 기존 사용자 이력을 밀어내지 않음
    }
}

/// <summary>v15.0.0: 메시 라우터 Zero-Alloc 코어 — 지속 부하에서 스레드 힙 할당 0B · Gen2 GC 0건(프로세스 전역 지표 → 단독 컬렉션).</summary>
[Collection("ProcessGcMode")]
[Trait("Category", "Integration")]
public class MeshRouterZeroAllocTests
{
    private const int Iterations = 10_000;

    [Fact]
    public void MeshRouter_Send_UnderSustainedLoad_ZeroAllocation_Check()
    {
        var mesh = new QuantumP2pMeshRouter();
        mesh.SeedDemoMesh(9);
        void Route(int i)
        {
            mesh.Route("edge-02", "p", chaosMidRoute: (i & 1) == 0);
            if ((i & 1) == 0) mesh.ReviveAll();
        }

        for (var i = 0; i < 300; i++) Route(i);   // 워밍업: JIT·ArrayPool 스레드 캐시·멱등 집합 버킷
        var delivered0 = mesh.DeliveredCount; var reroutes0 = mesh.RerouteCount;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for (var i = 0; i < 4; i++) Route(i);     // v16.9.0: Gen2 콜백의 ArrayPool 트림(메모리 압박 시 전량) 후 대여 버퍼 재확보 — 정상 상태 측정
        delivered0 = mesh.DeliveredCount; reroutes0 = mesh.RerouteCount;

        // v16.12.0: 측정 창 중 외부 Gen2(ArrayPool 트림)가 끼면 재대여 수십 B 가 잡힘 → 최소값 창 3회(AllocationProbe 관례). Gen2 0 은 블로킹 GC 인덱스로 판정(rules_testing).
        var windows = 0;
        var gen2Before = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        var allocated = AllocationProbe.MinAllocated(() => { windows++; for (var i = 1; i <= Iterations; i++) Route(i); });
        var gen2 = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index - gen2Before;

        Assert.Equal(0L, allocated);
        Assert.Equal(0L, gen2);
        Assert.Equal((long)Iterations * windows, mesh.DeliveredCount - delivered0);   // 카오스 포함 전량 전달(자가치유)
        Assert.True(mesh.RerouteCount - reroutes0 >= (long)Iterations * windows / 2, "chaos 송신마다 자가치유 재라우팅 발생");
        Assert.Equal(0, mesh.RejectedCount);
        Assert.StartsWith("복귀 ", mesh.LastAction);                              // 마지막 반복 = ReviveAll(지연 포맷)
        mesh.Route("edge-02", "p");
        Assert.StartsWith("전달 studio→edge-02", mesh.LastAction);                 // 라우팅 HUD 1줄 지연 포맷
    }
}

/// <summary>v15.0.0: 바이트 정규화(Route 코어)와 문자열 정규화(VerifyEnvelope) 비트 동일 · SIMD 커널과 스칼라 비트 정합.</summary>
[Trait("Category", "FastUnit")]
public class ZeroAllocSimdParityTests
{
    [Fact]
    public void MeshRouter_SendEnvelope_ByteCanonical_MatchesStringVerifier()
    {
        var mesh = new QuantumP2pMeshRouter();
        mesh.SeedDemoMesh(9);
        mesh.JoinNode("노드-한글");
        mesh.Connect("edge-02", "노드-한글", 5);

        foreach (var chaos in new[] { false, true })
        {
            var r = mesh.Send("노드-한글", "페이로드 ✓", chaos);
            Assert.True(r.Delivered, r.Reason);
            Assert.NotNull(r.Envelope);
            Assert.Equal(r.Path.Count - 1, r.Envelope!.HopCount);
            Assert.True(mesh.VerifyEnvelope(r.Envelope));                         // 코어가 바이트로 서명한 체인을 문자열 검증기가 승인
            var forged = r.Envelope with { Hops = r.Envelope.Hops.Take(r.Envelope.Hops.Count - 1).ToArray() };
            Assert.False(mesh.VerifyEnvelope(forged));                            // 홉 절단 위조 거부 유지
            mesh.ReviveAll();
        }
        Assert.Equal(2, mesh.RejectedCount);                                       // 위조 2건만 거부(정상 송신 거부 0)
    }

    [Fact]
    public void RgbaSimdKernels_AllPaths_BitExactWithScalar()
    {
        var rng = new Random(15_000);
        for (var len = 0; len <= 300; len++)
        {
            var src = new byte[len];
            rng.NextBytes(src);
            var pattern = (uint)rng.Next() ^ ((uint)rng.Next() << 16);
            if (len % 7 == 0) pattern |= 0xFF000000u;                             // A=255 (RenderInto 불투명 고정 경로)

            var scalar = (byte[])src.Clone(); RgbaSimdKernels.AddSaturateScalar(scalar, pattern);
            var best = (byte[])src.Clone(); RgbaSimdKernels.AddSaturate(best, pattern);
            var vec = (byte[])src.Clone(); RgbaSimdKernels.AddSaturateVector(vec, pattern);

            Assert.Equal(scalar, best);
            Assert.Equal(scalar, vec);
            for (var t = len & ~3; t < len; t++) Assert.Equal(src[t], best[t]);   // 픽셀 미만 꼬리 바이트 불변
        }

        var px = new byte[] { 250, 10, 0, 7 };
        RgbaSimdKernels.AddSaturate(px, RgbaSimdKernels.PackRgba(10, 10, 10, 255));
        Assert.Equal(new byte[] { 255, 20, 10, 255 }, px);
    }
}
