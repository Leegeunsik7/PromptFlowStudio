using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.ProcessBridge;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;
using Xunit;

namespace PromptFlowStudio.Tests;

public class EnterpriseHubJournalIpcGalleryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_hub_" + Guid.NewGuid().ToString("N"));

    public EnterpriseHubJournalIpcGalleryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Task FakeDownload(string url, string dest, CancellationToken ct)
    {
        File.WriteAllBytes(dest, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 });
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task TaskJournalService_RecordsAndRecoversUnfinishedTasksSuccessfully()
    {
        var journalPath = Path.Combine(_dir, "journal", TaskJournalService.FileName);
        using var journal = new TaskJournalService(journalPath) { Downloader = FakeDownload, MediaRootProvider = () => _dir };
        var now = DateTimeOffset.UtcNow;
        var okOut = Path.Combine(_dir, "media", "cut_ok.mp4");

        // ① 발번 즉시 저널링 · 동일 task_id 중복 차단(갱신)
        Assert.True(journal.Record(new CloudTaskJournalEntry("t-ok", "Cloud", "neon kospi chart", TaskJournalService.AmbientModeLabel, "Processing", now, okOut, "1920x1080", 7)));
        Assert.True(journal.Record(new CloudTaskJournalEntry("t-run", "Cloud", "p2", "Sec5_Single", "Processing", now)));
        Assert.True(journal.Record(new CloudTaskJournalEntry("t-fail", "Cloud", "p3", "Sec5_Single", "Processing", now)));
        Assert.True(journal.Record(new CloudTaskJournalEntry("t-net", "Cloud", "p4", "Sec5_Single", "Processing", now)));
        Assert.True(journal.Record(new CloudTaskJournalEntry("t-ok", "Cloud", "neon kospi chart", TaskJournalService.AmbientModeLabel, "Processing", now, okOut, "1920x1080", 7)));
        Assert.True(File.Exists(journalPath));
        Assert.Contains("\"taskId\": \"t-ok\"", File.ReadAllText(journalPath));
        Assert.Contains("\"durationMode\": \"15s-Ambient\"", File.ReadAllText(journalPath));
        Assert.Equal(4, journal.LoadAll().Count);
        Assert.True(journal.LoadAll()[0].IsAmbient);

        // ② 앱 재시작 시뮬레이션 — 새 인스턴스가 같은 저널을 읽어 복구
        using var restarted = new TaskJournalService(journalPath) { Downloader = FakeDownload, MediaRootProvider = () => _dir };
        restarted.Query = (id, ct) => id switch
        {
            "t-ok" => Task.FromResult(new WanTaskResult(true, WanTaskStatus.Succeeded, "https://dashscope.example/v.mp4", null, "ok")),
            "t-run" => Task.FromResult(new WanTaskResult(true, WanTaskStatus.Running, null, null, "running")),
            "t-fail" => Task.FromResult(new WanTaskResult(true, WanTaskStatus.Failed, null, "DataInspectionFailed", "failed")),
            _ => throw new System.Net.Http.HttpRequestException("network down"),
        };
        var recovered = new List<string>();
        restarted.OnRecovered += recovered.Add;
        var r = await restarted.RecoverAsync();
        Assert.Equal(4, r.Scanned);
        Assert.Equal(1, r.Recovered);
        Assert.Equal(2, r.Pending);   // 원격 진행 중 + 네트워크 단절
        Assert.Equal(1, r.Dropped);   // 원격 실패
        Assert.Single(recovered);
        Assert.Equal(0, restarted.RecoveringCount);
        Assert.False(restarted.IsRecovering);

        // 완료분: MP4 다운로드 + 갤러리 메타 기록(앰비언트 후처리기 미구성 → 원본 유지)
        Assert.True(File.Exists(okOut));
        var meta = File.ReadAllText(GalleryMediaService.MetaPathFor(okOut));
        Assert.Contains("\"cloudTaskId\":\"t-ok\"", meta);
        Assert.Contains("\"recovered\":true", meta);
        Assert.Contains("neon kospi chart", meta);

        var left = restarted.LoadAll();
        Assert.Equal(2, left.Count);
        Assert.Contains(left, e => e.TaskId == "t-run");
        Assert.Contains(left, e => e.TaskId == "t-net");

        // 출력 경로 미기록 항목 → 미디어 루트 recovered_{id}.mp4
        Assert.Equal(Path.Combine(_dir, "recovered_t-run.mp4"), restarted.ResolveOutputPath(left[0].TaskId == "t-run" ? left[0] : left[1]));

        // ③ 완료 시 정리 — 마지막 항목 제거 시 파일 삭제
        Assert.True(restarted.Remove("t-run"));
        Assert.False(restarted.Remove("t-run"));
        Assert.True(restarted.Remove("t-net"));
        Assert.False(File.Exists(journalPath));
        Assert.Same(TaskRecoveryResult.Empty, await restarted.RecoverAsync());

        // 조회기 미구성 → 복구 비활성(저널 유지)
        using var noQuery = new TaskJournalService(journalPath);
        noQuery.Record(new CloudTaskJournalEntry("t-x", "Cloud", "p", "Sec5_Single", "Processing", now));
        Assert.Same(TaskRecoveryResult.Empty, await noQuery.RecoverAsync());
        Assert.Single(noQuery.LoadAll());

        // 손상 저널 → 빈 목록 폴백(예외 없음)
        File.WriteAllText(journalPath, "{ not json");
        Assert.Empty(noQuery.LoadAll());

        // ④ 렌더 엔진 결선 — 클라우드 발번 즉시 저널 기록 → 로컬 저장 성공 시 제거
        File.Delete(journalPath);
        var engineJournal = new TaskJournalService(journalPath);
        var fake = new JournalProbeEngine(engineJournal);
        var exec = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(fake), null, (_, _) => Task.CompletedTask)
        {
            Journal = engineJournal, Downloader = FakeDownload, Ambient = null,
        };
        var job = new BatchRenderJob { Prompt = "cloud cut", OutputPath = Path.Combine(_dir, "media", "cloud_cut.mp4") };
        var res = await exec.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);
        Assert.True(res.Ok, res.Message);
        Assert.True(fake.JournaledDuringRun);
        Assert.Empty(engineJournal.LoadAll());

        // 원격 실패 종료 → 저널 제거 / 비종료 실패(단절) → 저널 유지
        fake.NextStatus = WanTaskStatus.Failed;
        Assert.False((await exec.ExecuteAsync(job, new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Empty(engineJournal.LoadAll());
        fake.NextStatus = WanTaskStatus.Unknown;
        Assert.False((await exec.ExecuteAsync(job, new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Single(engineJournal.LoadAll());
        engineJournal.Dispose();
    }

    /// <summary>클라우드형 가짜 엔진 — task_id 진행 보고 후 저널 기록 여부 관측.</summary>
    private sealed class JournalProbeEngine(TaskJournalService journal) : IVideoGenerationEngine
    {
        private int _n;
        public string Key => "Wan";
        public string DisplayName => "Wan probe";
        public bool Enabled => true;
        public bool JournaledDuringRun { get; private set; }
        public WanTaskStatus NextStatus { get; set; } = WanTaskStatus.Succeeded;

        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            var id = "task-" + Interlocked.Increment(ref _n).ToString(System.Globalization.CultureInfo.InvariantCulture);
            progress?.Report(new WanProgress(WanTaskStatus.Running, "run", 10, 1, TimeSpan.Zero, id));
            JournaledDuringRun = journal.LoadAll().Count == 1 && journal.LoadAll()[0].TaskId == id;
            var ok = NextStatus == WanTaskStatus.Succeeded;
            return Task.FromResult(new WanGenerationResult(ok, NextStatus, ok ? "https://dashscope.example/" + id + ".mp4" : null, ok ? "ok" : "err", id, TimeSpan.Zero));
        }

        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "n/a"));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteIpcBridge_DispatchesTickerEvent_AndHandlesConnectionFailure()
    {
        // ① 메시지 파싱 · 스키마 검증
        var m = StockSuiteIpcBridge.ParseMessage("{\"type\":\"TickerSelected\",\"ticker\":\"005930\",\"name\":\"삼성전자\",\"sector\":\"반도체\"}");
        Assert.NotNull(m);
        Assert.Equal(StockSuiteIpcKind.TickerSelected, m!.Kind);
        Assert.Equal("005930", m.Ticker);
        Assert.Equal("삼성전자", m.Name);
        Assert.Null(StockSuiteIpcBridge.ParseMessage("{\"type\":\"TickerSelected\",\"ticker\":\"bad ticker!\"}"));
        Assert.Null(StockSuiteIpcBridge.ParseMessage("{\"type\":\"TickerSelected\"}"));
        Assert.Null(StockSuiteIpcBridge.ParseMessage("{\"type\":\"Nope\",\"ticker\":\"AAPL\"}"));
        Assert.Null(StockSuiteIpcBridge.ParseMessage("not json"));
        Assert.Null(StockSuiteIpcBridge.ParseMessage(""));
        Assert.Null(StockSuiteIpcBridge.ParseMessage(new string('x', StockSuiteIpcBridge.MaxLineLength + 1)));
        Assert.True(StockSuiteIpcBridge.IsValidTicker("BRK.B"));

        // ② 디스패치 — 이벤트 발행 · 구독자 예외 격리
        using var bridge = new StockSuiteIpcBridge(null, "pfs_test_" + Guid.NewGuid().ToString("N"));
        StockSuiteIpcMessage? got = null;
        bridge.OnTickerSelected += x => got = x;
        bridge.OnTickerSelected += _ => throw new InvalidOperationException("ui boom");
        Assert.True(bridge.DispatchLine("{\"type\":\"TickerSelected\",\"ticker\":\"NVDA\"}"));
        Assert.Equal("NVDA", got?.Ticker);
        Assert.False(bridge.DispatchLine("{\"type\":\"Ping\"}"));

        // ③ 파이프 미연결 + 폴백 폴더 없음 → Failed(예외 없음)
        Assert.False(bridge.IsConnected);
        Assert.Equal(IpcSendRoute.Failed, await bridge.SendThemeAsync(HtsHudTheme.NavyQuant, 40));

        // ④ 파이프 미연결 → hts_theme.json + hotreload.trigger 파일 폴백
        using var fb = new StockSuiteIpcBridge(() => _dir, "pfs_test_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(IpcSendRoute.FileFallback, await fb.SendThemeAsync(HtsHudTheme.ModernMinimal, 150));
        var theme = File.ReadAllText(Path.Combine(_dir, StockSuiteIpcBridge.ThemeFileName));
        Assert.Contains("\"type\":\"ThemeChanged\"", theme);
        Assert.Contains("\"theme\":\"ModernMinimal\"", theme);
        Assert.Contains("\"dimmer\":100", theme);   // 0~100 클램프
        Assert.Contains(StockSuiteIpcBridge.ThemeFileName, File.ReadAllText(Path.Combine(_dir, StockSuiteExporter.HotReloadTriggerName)));
        Assert.Equal(IpcSendRoute.FileFallback, fb.LastRoute);

        // ⑤ 실제 Named Pipe 왕복 — 서버 시작 → 클라이언트 접속 → 종목 수신 + 테마 패킷 수신
        var pipeName = "pfs_test_" + Guid.NewGuid().ToString("N");
        using var live = new StockSuiteIpcBridge(() => _dir, pipeName);
        var tickerTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live.OnTickerSelected += x => tickerTcs.TrySetResult(x.Ticker);
        live.OnConnectionChanged += () => { if (live.IsConnected) connTcs.TrySetResult(); };
        live.Start();
        live.Start();   // 멱등
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await connTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var line = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"TickerSelected\",\"ticker\":\"TSLA\"}\n");
        await client.WriteAsync(line);
        await client.FlushAsync();
        Assert.Equal("TSLA", await tickerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var route = await live.SendThemeAsync(HtsHudTheme.OledBlack, 25);
        Assert.True(route == IpcSendRoute.Pipe, route + " / " + live.LastError + " / connected=" + live.IsConnected);
        using var reader = new StreamReader(client, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
        var echoed = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"theme\":\"OledBlack\"", echoed);
        Assert.Contains("\"dimmer\":25", echoed);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void GalleryMediaService_FiltersByFullTextAndResolutionCorrectly()
    {
        void Make(string name, string prompt, string resolution, int bytes)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.WriteAllText(GalleryMediaService.MetaPathFor(path),
                "{\"title\":\"" + Path.GetFileNameWithoutExtension(name) + "\",\"prompt\":\"" + prompt + "\",\"resolution\":\"" + resolution + "\",\"createdAt\":\"2026-11-05T00:00:00Z\"}");
        }
        Make("nvda_uhd.mp4", "NVDA neon OLED chart 엔비디아 반도체", "3840x2160", 4000);
        Make("kospi_fhd.mp4", "코스피 네이비 퀀트 호가창 night", "1920x1080", 2000);
        Make("ultra_219.mp4", "Wall Street rain bokeh", "2560x1080", 1500);
        Make("super_329.webm", "samsung 005930 ambient loop", "3840x1080", 500);
        Make("shorts_916.mp4", "Tesla vertical shorts", "1080x1920", 700);

        var svc = new GalleryMediaService();
        svc.ScanGalleryItems(_dir);
        Assert.Equal(5, svc.Items.Count);

        IReadOnlyList<GalleryItem> S(GallerySearchQuery q) => svc.Search(q);

        // 풀텍스트 — 영문/한글 프롬프트 · 티커 · 파일명 · 다중 키워드 AND · 대소문자 무시
        Assert.Single(S(new("nvda")));
        Assert.Single(S(new("엔비디아")));
        Assert.Single(S(new("005930")));
        Assert.Single(S(new("ultra_219")));
        Assert.Single(S(new("neon   chart")));
        Assert.Empty(S(new("neon kospi")));
        Assert.Equal(5, S(new("  ")).Count);

        // 화면비 칩
        Assert.Equal("nvda_uhd", Assert.Single(S(new(null, GalleryAspectChip.W16x9, GalleryResolutionChip.Uhd4K))).Title);
        Assert.Equal(2, S(new(null, GalleryAspectChip.W16x9)).Count);
        Assert.Equal("ultra_219", Assert.Single(S(new(null, GalleryAspectChip.W21x9))).Title);
        Assert.Equal("super_329", Assert.Single(S(new(null, GalleryAspectChip.W32x9))).Title);
        Assert.Equal("shorts_916", Assert.Single(S(new(null, GalleryAspectChip.V9x16))).Title);

        // 해상도 칩(짧은 변 기준 — 3840x1080 울트라와이드는 1080p)
        Assert.Single(S(new(null, Resolution: GalleryResolutionChip.Uhd4K)));
        Assert.Equal(4, S(new(null, Resolution: GalleryResolutionChip.Fhd1080)).Count);

        // 코덱 칩 + 복합 결합
        Assert.Equal("super_329", Assert.Single(S(new(null, Codec: GalleryCodecChip.WebM))).Title);
        Assert.Equal(4, S(new(null, Codec: GalleryCodecChip.Mp4)).Count);
        Assert.Equal("kospi_fhd", Assert.Single(S(new("호가창", GalleryAspectChip.W16x9, GalleryResolutionChip.Fhd1080, GalleryCodecChip.Mp4))).Title);
        Assert.Empty(S(new("호가창", Resolution: GalleryResolutionChip.Uhd4K)));

        // 스토리지 점유율 — 4K 4000 · 1080p MP4 2000+1500+700 · WebM 500
        var st = svc.ComputeStorage();
        Assert.Equal(5, st.Count);
        Assert.Equal(8700, st.TotalBytes);
        Assert.Equal(4000, st.Uhd4KBytes);
        Assert.Equal(4200, st.Fhd1080Mp4Bytes);
        Assert.Equal(500, st.WebMBytes);
        Assert.Equal(0, st.OtherBytes);
        Assert.True(st.FreeBytes > 0);
        Assert.Equal(50, Math.Round(st.Percent(4350)));

        // VM — 칩 토글 · 검색 결과 일괄 내보내기 · 일괄 삭제
        var vm = new PromptFlowStudio.ViewModels.GalleryViewModel(svc);
        vm.Refresh(_dir);
        vm.SearchQuery = "night";
        vm.SetResolutionChip(GalleryResolutionChip.Fhd1080);
        Assert.True(vm.HasActiveSearch);
        Assert.Equal(1, vm.SearchResultCount);
        var outDir = Path.Combine(Path.GetTempPath(), "pfs_hub_exp_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(1, vm.ExportSearchResults(outDir));
            Assert.True(File.Exists(Path.Combine(outDir, "kospi_fhd.mp4")));
            Assert.True(File.Exists(GalleryMediaService.MetaPathFor(Path.Combine(outDir, "kospi_fhd.mp4"))));
        }
        finally { try { Directory.Delete(outDir, true); } catch (IOException) { } }
        Assert.Equal(1, vm.DeleteSearchResults());
        Assert.False(File.Exists(Path.Combine(_dir, "kospi_fhd.mp4")));
        vm.SetResolutionChip(GalleryResolutionChip.Fhd1080);   // 재클릭 = 해제
        Assert.Equal(GalleryResolutionChip.Any, vm.ResolutionChip);
        Assert.Equal(4, vm.TotalCount);
    }
}
