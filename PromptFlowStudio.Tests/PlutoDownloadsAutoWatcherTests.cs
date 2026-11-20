using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>📥 Step 23 — Downloads 자동 감시 · 디바운스/잠금 준비 게이트 · 비 YouTube 무시 · SHA-256 영속 중복 차단 · 자체 추가 안전장치.</summary>
public sealed class PlutoDownloadsAutoWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pfs_yt_watch_" + Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly string _state;
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";

    public PlutoDownloadsAutoWatcherTests()
    {
        _watch = Path.Combine(_root, "Downloads");
        _state = Path.Combine(_root, "state", "downloads_watcher.json");
        Directory.CreateDirectory(_watch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private YouTubeDownloadsWatcherOptions Opt(bool fsw = true) => new()
    {
        Directory = _watch,
        StatePath = _state,
        Debounce = TimeSpan.FromMilliseconds(120),
        SweepInterval = TimeSpan.Zero,
        ReadinessRetries = 30,
        ReadinessRetryDelay = TimeSpan.FromMilliseconds(100),
        StabilityProbe = TimeSpan.FromMilliseconds(40),
        UseFileSystemWatcher = fsw,
    };

    private static byte[] Snap(long views, double avg, string id = "WalkClipYT1", string title = "가을 산책 브이로그 #shorts")
        => Encoding.UTF8.GetBytes(EnHeader + "\n" + id + "," + title + ",\"Nov 2, 2026\"," + views.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "," + avg.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",5000,5.0,66.0,10\n");

    /// <summary>수집기 대역 — 호출 기록 · 첫 호출 학습 1건 · 선택적 지연/예외.</summary>
    private sealed class FakeImporter : IPlutoTelemetryBatchImporter
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public int Concurrent, MaxConcurrent;
        public TimeSpan Delay = TimeSpan.Zero;
        public int LearnedPerCall = 1;
        public int ThrowTimes;

        public async Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
        {
            var c = Interlocked.Increment(ref Concurrent);
            int m;
            while ((m = Volatile.Read(ref MaxConcurrent)) < c && Interlocked.CompareExchange(ref MaxConcurrent, c, m) != m) { }
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                if (ThrowTimes > 0) { ThrowTimes--; throw new IOException("transient"); }
                Calls.Enqueue(fileName ?? "?");
                return new PlutoTelemetryBatchImportResult(1, LearnedPerCall, 0, 0, Array.Empty<string>()) { IsYouTubeStudioReport = true };
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }

        public Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    /// <summary>폐루프 대역(Step 22 테스트와 같은 멱등 규칙).</summary>
    private sealed class CountingLoop : IPlutoClosedLoopReinforcementService
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PlutoVideoPerformanceTelemetry> Ingested = new();
        public readonly List<(string VideoId, int Stage)> Deltas = new();
        public PlutoReinforcementOutcome? LastOutcome => null;
        public event Action? Changed { add { } remove { } }

        public Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct = default)
        {
            lock (_keys)
            {
                if (!_keys.Add(t.Platform + "|" + t.VideoId)) return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "dup") { AlreadyRegistered = true });
                Ingested.Add(t);
            }
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "ok"));
        }

        public Task<PlutoReinforcementOutcome> IngestMaturityDeltaAsync(PlutoVideoPerformanceTelemetry matured, PlutoVideoPerformanceTelemetry previous, int stage, double weight, CancellationToken ct = default)
        {
            lock (_keys)
            {
                if (!_keys.Add("mat|" + matured.VideoId + "|" + stage)) return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "dup") { AlreadyRegistered = true });
                Deltas.Add((matured.VideoId, stage));
            }
            return Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "delta"));
        }

        public Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "noop"));
    }

    private static TaskCompletionSource<PlutoDownloadsAutoIngestEvent> Await(IYouTubeDownloadsAutoWatcherService w)
    {
        var tcs = new TaskCompletionSource<PlutoDownloadsAutoIngestEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        w.OnExportAutoIngested += e => tcs.TrySetResult(e);
        return tcs;
    }

    private static async Task WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("condition not met");
            await Task.Delay(50);
        }
    }

    // ── ① 브라우저 다운로드(.crdownload → 최종 이름) 자동 감지 → 수집 → 완료 이벤트 ──

    [Fact]
    public async Task Watcher_DetectsCrdownloadRenamedToStudioCsv_IngestsAndRaisesCompletionEvent()
    {
        var imp = new FakeImporter();
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, Opt());
        var done = Await(w);
        Assert.True(w.Start());
        Assert.True(w.IsRunning);

        var partial = Path.Combine(_watch, "Unconfirmed 4821.crdownload");
        await File.WriteAllBytesAsync(partial, Snap(300, 48));
        await Task.Delay(400);
        Assert.True(imp.Calls.IsEmpty);   // 받는 중 파일 무시
        File.Move(partial, Path.Combine(_watch, "Table data.csv"));

        var evt = await done.Task.WaitAsync(Wait);
        Assert.Equal("Table data.csv", evt.FileName);
        Assert.True(evt.HasImpact);
        Assert.Equal(1, evt.NewlyLearned);
        Assert.Contains("📥 유튜브 자동 수집 완료: 신규 1건 · 성숙 보정 0건 (Table data.csv)", evt.ToastKorean);
        Assert.Equal(64, evt.Sha256Hex.Length);
        Assert.Single(imp.Calls);
        Assert.True(w.IsKnownHash(evt.Sha256Hex));
        Assert.False(YouTubeDownloadsAutoWatcherService.IsCandidateName("a.crdownload"));
        Assert.False(YouTubeDownloadsAutoWatcherService.IsCandidateName("b.csv.part"));
        Assert.False(YouTubeDownloadsAutoWatcherService.IsCandidateName("c.tmp"));
        Assert.True(YouTubeDownloadsAutoWatcherService.IsCandidateName("YouTube Studio.ZIP"));
    }

    // ── ② 디바운스 · 쓰기 잠금 경합 → 잠금 해제 후 수집(오류 이벤트 없음) ──

    [Fact]
    public async Task ReadinessGate_WaitsWhileFileIsLockedForWriting_ThenIngestsAfterRelease()
    {
        var imp = new FakeImporter();
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, Opt());
        var done = Await(w);
        Assert.True(w.Start());

        var path = Path.Combine(_watch, "YouTube Studio 콘텐츠.csv");
        var bytes = Snap(300, 48);
        var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);   // 브라우저처럼 쓰기 핸들 유지
        await writer.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
        await writer.FlushAsync();
        await Task.Delay(700);
        Assert.True(imp.Calls.IsEmpty);   // 잠긴 동안 읽지 않음(공유 위반 → 재시도)
        Assert.False(done.Task.IsCompleted);

        // 직접 준비 게이트도 잠김 중에는 null(예외 없음)
        var quick = new YouTubeDownloadsAutoWatcherService(imp, new YouTubeDownloadsWatcherOptions { Directory = _watch, StatePath = _state + ".q", ReadinessRetries = 2, ReadinessRetryDelay = TimeSpan.FromMilliseconds(20), StabilityProbe = TimeSpan.FromMilliseconds(10) });
        Assert.Null(await quick.ReadWhenReadyAsync(path, CancellationToken.None));
        quick.Dispose();

        await writer.WriteAsync(bytes.AsMemory(bytes.Length / 2));
        await writer.DisposeAsync();   // 다운로드 완료

        var evt = await done.Task.WaitAsync(Wait);
        Assert.Equal("YouTube Studio 콘텐츠.csv", evt.FileName);
        Assert.Equal(YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(bytes), evt.Sha256Hex);   // 완성본만 해시
        Assert.Single(imp.Calls);
    }

    // ── ③ 무관한 CSV/ZIP · 0바이트 · 숨김 → 조용히 무시 ──

    [Fact]
    public async Task UnrelatedCsvAndZip_AreSilentlyIgnored_NoEventsNoIngestion()
    {
        var imp = new FakeImporter();
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, Opt(fsw: false));
        var events = 0;
        w.OnExportAutoIngested += _ => Interlocked.Increment(ref events);
        Assert.True(w.Start());

        await File.WriteAllTextAsync(Path.Combine(_watch, "bank_statement.csv"), "Date,Amount,Memo\n2026-11-01,1000,coffee\n");
        await File.WriteAllTextAsync(Path.Combine(_watch, "content_budget.csv"), "Item,Cost\nmic,100\n");   // 이름 서명은 있으나 스키마 불일치
        using (var ms = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            using (var s = z.CreateEntry("photo.txt").Open()) s.Write(Encoding.UTF8.GetBytes("hello"));
            await File.WriteAllBytesAsync(Path.Combine(_watch, "holiday_photos.zip"), ms.ToArray());
        }
        await File.WriteAllBytesAsync(Path.Combine(_watch, "youtube_empty.csv"), Array.Empty<byte>());
        var hidden = Path.Combine(_watch, "youtube_hidden.csv");
        await File.WriteAllBytesAsync(hidden, Snap(300, 48));
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Equal(0, await w.SweepNowAsync());
        Assert.Equal(0, await w.SweepNowAsync());   // 재스윕도 무소음
        Assert.True(imp.Calls.IsEmpty);
        Assert.Equal(0, events);
        Assert.Equal(0, w.IngestedCount);
        Assert.False(YouTubeDownloadsAutoWatcherService.LooksLikeStudioExport(Encoding.UTF8.GetBytes("Date,Amount\n1,2\n"), "data.csv"));
        Assert.True(YouTubeDownloadsAutoWatcherService.LooksLikeStudioExport(Snap(300, 48), "anything.csv"));   // 이름 무관 · 스키마로 인식
    }

    // ── ④ SHA-256 영속 중복 차단(재시작) · 새 해시 성숙 스냅샷 → 성숙 증분 ──

    [Fact]
    public async Task Sha256Ledger_SurvivesRestart_SameExportSkipped_MaturedReExportFlowsIntoMaturityDelta()
    {
        var storageDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(storageDir);
        using var cat = new PlutoRenderCatalogService(new PlutoDriveStorageManager(null, storageDir));
        await cat.RecordRenderedClipAsync(new PlutoRenderedClipRecord("walk_clip", "가을 산책 브이로그", "Ambient15s", "훅", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "t" }, @"C:\out\walk_clip.mp4", T0, false));
        var loop = new CountingLoop();
        var importer = new PlutoTelemetryBatchImporter(cat, loop);
        var day1 = Path.Combine(_watch, "Table data.csv");
        await File.WriteAllBytesAsync(day1, Snap(300, 48));

        string sha1;
        await using (var w1 = new YouTubeDownloadsAutoWatcherService(importer, Opt(fsw: false)))
        {
            var done = Await(w1);
            Assert.True(w1.Start());   // 시작 스윕 → 수집
            var e1 = await done.Task.WaitAsync(Wait);
            sha1 = e1.Sha256Hex;
            Assert.Equal(1, e1.NewlyLearned);
        }
        Assert.Single(loop.Ingested);
        Assert.True(File.Exists(_state));

        // ♻️ 재시작 — 같은 파일(해시) → 무접촉 · 무토스트
        var imp2 = new FakeImporter();
        await using (var w2 = new YouTubeDownloadsAutoWatcherService(imp2, Opt(fsw: false)))
        {
            Assert.True(w2.IsKnownHash(sha1));
            var events = 0;
            w2.OnExportAutoIngested += _ => Interlocked.Increment(ref events);
            Assert.True(w2.Start());
            Assert.Equal(0, await w2.SweepNowAsync());
            Assert.True(imp2.Calls.IsEmpty);
            Assert.Equal(0, events);
        }

        // 📈 성숙 재내보내기(새 해시) → 성숙 증분 1단계
        await using (var w3 = new YouTubeDownloadsAutoWatcherService(importer, Opt(fsw: false)))
        {
            var done = Await(w3);
            Assert.True(w3.Start());
            await File.WriteAllBytesAsync(Path.Combine(_watch, "Table data (1).csv"), Snap(900, 58));
            Assert.Equal(1, await w3.SweepNowAsync());
            var e3 = await done.Task.WaitAsync(Wait);
            Assert.NotEqual(sha1, e3.Sha256Hex);
            Assert.Equal(1, e3.Matured);
            Assert.Equal(0, e3.NewlyLearned);
            Assert.Contains("성숙 보정 1건", e3.ToastKorean);
        }
        Assert.Equal(("walk_clip", 1), Assert.Single(loop.Deltas));
        Assert.Single(loop.Ingested);
    }

    // ── ⑤ 토글 영속 · 격리(실 Downloads 무접촉) ──

    [Fact]
    public async Task Toggle_PersistsAcrossRestart_AndIsolationEnvBlocksRealDownloads()
    {
        var imp = new FakeImporter();
        await using (var w = new YouTubeDownloadsAutoWatcherService(imp, Opt()))
        {
            Assert.True(w.IsEnabled);   // 기본 ON
            var changes = 0;
            w.StateChanged += () => Interlocked.Increment(ref changes);
            Assert.True(w.Start());
            await w.SetEnabledAsync(false);
            Assert.False(w.IsRunning);
            Assert.False(w.Start());   // OFF → 시작 거부
            Assert.True(changes >= 2);
            await File.WriteAllBytesAsync(Path.Combine(_watch, "youtube.csv"), Snap(300, 48));
            Assert.Equal(0, await w.SweepNowAsync());   // 꺼진 동안 무수집
        }
        await using (var w = new YouTubeDownloadsAutoWatcherService(imp, Opt()))
        {
            Assert.False(w.IsEnabled);   // 영속
            await w.SetEnabledAsync(true);
            Assert.True(w.IsRunning);
            await WaitUntilAsync(() => imp.Calls.Count == 1, Wait);   // 재개 시작 스윕이 놓친 파일 수집
        }
        Assert.Single(imp.Calls);

        // 🔒 테스트 하네스(PROMPTFLOW_PLUTO_DRIVE=off) → 명시 폴더 없는 기본 인스턴스는 실 Downloads를 열지 않음
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));
        Assert.True(YouTubeDownloadsAutoWatcherService.IsIsolatedByEnvironment());
        var realState = Path.Combine(_root, "real_state.json");
        await using (var real = new YouTubeDownloadsAutoWatcherService(imp, new YouTubeDownloadsWatcherOptions { StatePath = realState }))
        {
            Assert.True(real.IsIsolationBlocked);
            Assert.Null(real.WatchedDirectory);   // 실 Downloads 경로 해석조차 생략
            Assert.False(real.Start());
            Assert.False(real.IsRunning);
            Assert.Equal(0, await real.SweepNowAsync());
        }
        Assert.False(File.Exists(realState));   // 아무것도 쓰지 않음
        Assert.Single(imp.Calls);
    }

    // ── ⑥-A 자체 추가 ① — FileSystemWatcher 오류/버퍼 오버플로 → 즉시 재스캔으로 유실 이벤트 복구 ──

    [Fact]
    public async Task CoArchitect_BufferOverflowFault_TriggersImmediateRescan_RecoversMissedFile()
    {
        var imp = new FakeImporter();
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, Opt(fsw: false));   // 이벤트 없음 = OS 이벤트 전량 유실 상황
        var done = Await(w);
        Assert.True(w.Start());
        await File.WriteAllBytesAsync(Path.Combine(_watch, "YouTube Analytics.csv"), Snap(300, 48));
        await Task.Delay(400);
        Assert.True(imp.Calls.IsEmpty);   // 주기 스윕 꺼짐 · 이벤트 없음 → 아직 모름

        w.HandleWatcherFault(new InternalBufferOverflowException("overflow"));
        var evt = await done.Task.WaitAsync(Wait);
        Assert.Equal("YouTube Analytics.csv", evt.FileName);
        Assert.Equal(1, w.OverflowRecoveryCount);

        w.HandleWatcherFault(new IOException("watcher died"));   // 일반 오류 → 감시기 재구성 경로도 예외 없음
        w.HandleWatcherFault(null);
        Assert.Equal(3, w.OverflowRecoveryCount);
        await Task.Delay(300);
        Assert.Single(imp.Calls);   // 재스캔이 이미 수집한 파일을 다시 넣지 않음
    }

    // ── ⑥-B 자체 추가 ② — 수동 가져오기 · 이벤트 · 스윕 경합 → 단일 게이트 · 정확히 1회 반영 ──

    [Fact]
    public async Task CoArchitect_ManualImportAndConcurrentSweeps_ShareSingleGate_IngestExactlyOnce()
    {
        var imp = new FakeImporter { Delay = TimeSpan.FromMilliseconds(150) };
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, Opt(fsw: false));
        var events = 0;
        w.OnExportAutoIngested += _ => Interlocked.Increment(ref events);
        Assert.True(w.Start());

        var bytes = Snap(300, 48);
        var path = Path.Combine(_watch, "YouTube Studio export.csv");
        await File.WriteAllBytesAsync(path, bytes);

        // 수동 클릭(같은 바이트) + 백그라운드 스윕 3중 동시 진입
        var manual = w.RunExclusiveImportAsync(bytes, "YouTube Studio export.csv", c => imp.ImportYouTubeStudioFileAsync(bytes, "manual", "YouTube Shorts", c));
        var s1 = w.SweepNowAsync();
        var s2 = w.SweepNowAsync();
        var s3 = w.SweepNowAsync();
        await Task.WhenAll(manual, s1, s2, s3).WaitAsync(Wait);

        Assert.Equal(1, imp.MaxConcurrent);   // 게이트 직렬화 — 동시 수집 0
        Assert.Equal("manual", Assert.Single(imp.Calls));   // 정확히 1회(수동) · 대기하던 스윕은 게이트 안 재확인으로 생략
        Assert.True(w.IsKnownHash(YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(bytes)));
        Assert.Equal(0, await w.SweepNowAsync());
        Assert.Single(imp.Calls);
        Assert.Equal(0, Volatile.Read(ref events));   // 수동 반영분은 감시기가 뒤늦게 재토스트하지 않음
    }

    // ── ⑥-C 자체 추가 ③ — 폐기된 구독자(회로 종료) 예외 격리 · 0건 학습 토스트 억제 · 일시 실패 유계 재시도 후 격리 ──

    [Fact]
    public async Task CoArchitect_DisposedSubscriberIsolated_ZeroImpactSuppressed_TransientFailuresBoundedThenQuarantined()
    {
        var imp = new FakeImporter();
        await using var w = new YouTubeDownloadsAutoWatcherService(imp, Opt(fsw: false));
        w.OnExportAutoIngested += _ => throw new ObjectDisposedException("circuit");   // 회로가 끊긴 Blazor 페이지
        var healthy = Await(w);
        Assert.True(w.Start());
        await File.WriteAllBytesAsync(Path.Combine(_watch, "youtube_a.csv"), Snap(300, 48, "AaaaaaaaaA1"));
        Assert.Equal(1, await w.SweepNowAsync());   // 예외가 감시 루프를 깨지 않음
        Assert.Equal("youtube_a.csv", (await healthy.Task.WaitAsync(Wait)).FileName);   // 다른 구독자는 정상 수신

        // 🔕 0건 학습(이미 반영 스냅샷) → 장부엔 기록 · 이벤트 없음
        imp.LearnedPerCall = 0;
        var noisy = 0;
        w.OnExportAutoIngested += _ => Interlocked.Increment(ref noisy);
        await File.WriteAllBytesAsync(Path.Combine(_watch, "youtube_b.csv"), Snap(301, 48, "BbbbbbbbbB2"));
        Assert.Equal(0, await w.SweepNowAsync());
        Assert.Equal(0, noisy);
        Assert.Equal(1, w.SuppressedNoImpactCount);

        // ⏳ 일시 실패 → 재시도 · 상한(3) 초과 시 격리 → 이후 스윕 폭주 없음
        imp.LearnedPerCall = 1;
        imp.ThrowTimes = 100;
        var c = Path.Combine(_watch, "youtube_c.csv");
        await File.WriteAllBytesAsync(c, Snap(302, 48, "CccccccccC3"));
        for (var i = 0; i < 6; i++) await w.SweepNowAsync();
        Assert.Equal(100 - 3, imp.ThrowTimes);   // 정확히 3회 시도 후 격리
        Assert.True(w.IsKnownHash(YouTubeDownloadsAutoWatcherService.ComputeSha256Hex(await File.ReadAllBytesAsync(c))));
        Assert.Equal(0, noisy);

        // 폐기 후 이벤트/시작 무해
        await w.DisposeAsync();
        Assert.False(w.Start());
        w.HandleWatcherFault(new InternalBufferOverflowException());
    }
}
