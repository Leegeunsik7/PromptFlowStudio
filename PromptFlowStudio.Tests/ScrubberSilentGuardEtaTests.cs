using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🎞️ 스크러버 동기 미리보기 · 🔇 StockSuite Silent Ambient Guard · ⏱️ 큐 ETA 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class ScrubberSilentGuardEtaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_sse_" + Guid.NewGuid().ToString("N"));

    public ScrubberSilentGuardEtaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NullYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, "x", "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }

    private (StockSuiteExporter Ex, List<string[]> Calls, string Dest) Exporter(string name)
    {
        var calls = new List<string[]>();
        FfmpegRunner runner = (_, args, _) =>
        {
            var a = args.ToArray();
            lock (calls) calls.Add(a);
            File.WriteAllBytes(a[^1], new byte[8192]);
            return Task.FromResult(true);
        };
        var dest = Path.Combine(_dir, "dest_" + name);
        Directory.CreateDirectory(dest);
        return (new StockSuiteExporter(() => dest, null, () => "ff", runner), calls, dest);
    }

    private string Source(string name)
    {
        var src = Path.Combine(_dir, name + ".mp4");
        File.WriteAllBytes(src, new byte[4096]);
        return src;
    }

    [Fact]
    public async Task StockSuiteExporter_PreviewFrame_UsesSpecifiedTimestamp_AndCleansUpTempFiles()
    {
        var (ex, calls, _) = Exporter("pv");
        var src = Source("pv");
        using var vm = new PublishViewModel(new NullYt(), new NullIg(), stockSuite: ex);
        vm.OpenForMedia(src, "p");
        Assert.Equal(1.0, vm.PreviewTimestampSec);   // 미지정 = 1초 기본

        // 스크러버 위치 12.34초 → -ss 12.340
        vm.PreviewTimestampSec = 12.34;
        Assert.Equal(12.34, vm.CapturePreviewOptions().AtSeconds);
        Assert.True(await vm.GeneratePreviewFrameAsync());
        var a = calls[^1];
        Assert.Equal("12.340", a[Array.IndexOf(a, "-ss") + 1]);
        var first = vm.PreviewFramePath!;
        Assert.True(File.Exists(first));
        Assert.Contains("00-12-340", Path.GetFileName(first));
        Assert.Contains("12.340s", vm.PreviewMessage);
        Assert.Single(ex.PreviewSessionFiles);

        // 새 미리보기 요청 → 직전 임시 파일 삭제
        vm.PreviewTimestampSec = 75.5;
        Assert.True(await vm.GeneratePreviewFrameAsync());
        a = calls[^1];
        Assert.Equal("75.500", a[Array.IndexOf(a, "-ss") + 1]);
        var second = vm.PreviewFramePath!;
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(new[] { second }, ex.PreviewSessionFiles);

        // 모달 닫기 → 세션 임시 파일 정리
        vm.Close();
        Assert.False(File.Exists(second));
        Assert.Empty(ex.PreviewSessionFiles);
        Assert.Null(vm.PreviewFramePath);

        // 재오픈 → 기본 1초 복원 · 무효 값 = 0
        vm.OpenForMedia(src, "p");
        Assert.Equal(1.0, vm.PreviewTimestampSec);
        vm.PreviewTimestampSec = double.NaN;
        Assert.Equal(0, vm.PreviewTimestampSec);
    }

    [Fact]
    public async Task StockSuiteExporter_EnforcesSilentAmbient_WhenExportingToStockSuite()
    {
        // 순수 인자 가드: BGM 매핑/코덱/필터 제거 + 출력 직전 -an
        string[] bgmArgs = ["-y", "-i", "in.mp4", "-stream_loop", "-1", "-i", "bgm.mp3", "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264",
            "-c:a", "aac", "-b:a", "192k", "-af", "afade=t=out:st=10:d=2", "-shortest", "out.mp4"];
        var g = StockSuiteExporter.EnforceSilentAmbient(bgmArgs);
        Assert.Equal("out.mp4", g[^1]);
        Assert.Equal("-an", g[^2]);
        Assert.DoesNotContain("-c:a", g);
        Assert.DoesNotContain("-b:a", g);
        Assert.DoesNotContain("-af", g);
        Assert.DoesNotContain("1:a:0", g);
        Assert.Contains("0:v:0", g);
        var already = StockSuiteExporter.BuildZeroAudioArgs("in.mp4", "out.mp4");
        Assert.Same(already, StockSuiteExporter.EnforceSilentAmbient(already));   // 이미 무음 = 무할당

        var (ex, calls, dest) = Exporter("sg");
        Assert.True(ex.IsStockSuiteTarget(Path.Combine(dest, "bg_ambient.mp4")));
        Assert.False(ex.IsStockSuiteTarget(Path.Combine(_dir, "other.mp4")));

        // 단일 내보내기 → 모든 ffmpeg 호출 -an · 메타 hasAudio:false
        var src = Source("sg");
        var r = await ex.ExportAsync(src, format: StockSuiteExportFormat.Mp4, sharpen: true, smooth60: false);
        Assert.True(r.Ok, r.Message);
        Assert.False(r.HasAudio);
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Contains("-an", c));
        Assert.False(ExportMetadata.ReadHasAudio(src));

        // BGM 이 선택된 쇼츠 산출물이라도 StockSuite 폴더 대상이면 오디오 스트립
        var bgm = Path.Combine(_dir, "bgm.mp3");
        File.WriteAllBytes(bgm, new byte[2048]);
        calls.Clear();
        var m = await ex.ExportMultiTargetPackageAsync(src, new MultiExportOptions
        {
            OutputDirectory = Path.Combine(dest, "pkg"),
            BgmPreset = SnsBgmPreset.CustomFile,
            BgmPath = bgm,
            DurationSec = 15,
        });
        Assert.NotEmpty(m.Generated);
        var shorts = calls.Single(c => c[^1].EndsWith("_shorts_9x16.mp4", StringComparison.Ordinal));
        Assert.Contains("-an", shorts);
        Assert.DoesNotContain("-c:a", shorts);
        Assert.DoesNotContain("1:a:0", shorts);

        // StockSuite 외 폴더는 BGM 유지(가드 비적용)
        calls.Clear();
        await ex.ExportMultiTargetPackageAsync(src, new MultiExportOptions
        {
            OutputDirectory = Path.Combine(_dir, "sns_pkg"),
            BgmPreset = SnsBgmPreset.CustomFile,
            BgmPath = bgm,
            DurationSec = 15,
        });
        var snsShorts = calls.Single(c => c[^1].EndsWith("_shorts_9x16.mp4", StringComparison.Ordinal));
        Assert.Contains("-c:a", snsShorts);

        // PublishModal 경고
        using var vm = new PublishViewModel(new NullYt(), new NullIg(), stockSuite: ex);
        vm.OpenForMedia(src, "p");
        Assert.Null(vm.StockSuiteSilentWarning);
        vm.Bgm = SnsBgmPreset.FintechSynthwave;
        Assert.Equal("⚠️ StockSuite backgrounds must be silent. BGM will be stripped.", vm.StockSuiteSilentWarning);
    }

    [Fact]
    public void RenderQueueService_CalculatesAccurateEta_BasedOnResolutionWeights()
    {
        Assert.Equal(1.8, RenderQueueService.ResolutionWeight("3840x2160"));
        Assert.Equal(1.8, RenderQueueService.ResolutionWeight("5120x1440"));   // 32:9
        Assert.Equal(1.8, RenderQueueService.ResolutionWeight("4K"));
        Assert.Equal(1.0, RenderQueueService.ResolutionWeight("1920x1080"));
        Assert.Equal(0.7, RenderQueueService.ResolutionWeight("1920x1080", fps: 30));
        Assert.Equal(0.7, RenderQueueService.ResolutionWeight("1080x1920"));   // 9:16
        Assert.Equal(1.0, RenderQueueService.ResolutionWeight("1080p"));

        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var now = new DateTimeOffset(2026, 11, 9, 8, 30, 0, TimeSpan.FromHours(9));
        q.EtaClock = () => now;
        Assert.Null(q.EstimatedRemainingTime);   // 표본 없음

        // 롤링 평균: 4K 180s(→ 기준 100s) + 1080p 100s(→ 100s) + 9:16 56s(→ 80s) = 평균 기준 ≈ 93.33s
        q.RecordThroughputSample(180_000, RenderQueueService.ResolutionWeight("3840x2160"));
        q.RecordThroughputSample(100_000, RenderQueueService.ResolutionWeight("1920x1080"));
        q.RecordThroughputSample(new EncoderTelemetry("h264_nvenc", false, 56_000), "1080x1920");
        var baseline = (100.0 + 100.0 + 80.0) / 3;
        Assert.Equal(baseline, q.BaselineSecondsPerJob!.Value, 6);
        Assert.Null(q.EstimatedRemainingTime);   // 남은 작업 없음

        var j4k = new BatchRenderJob { Prompt = "a", Resolution = "3840x2160" };
        var jFhd = new BatchRenderJob { Prompt = "b", Resolution = "1920x1080" };
        var jVert = new BatchRenderJob { Prompt = "c", Resolution = "1080x1920" };
        Assert.Equal(3, q.Enqueue(new[] { j4k, jFhd, jVert }));
        j4k.Status = BatchJobStatus.Processing;   // 진행 중 50% → 잔여 절반
        j4k.ProgressPercent = 50;

        var expected = baseline * (1.8 * 0.5 + 1.0 + 0.7);
        Assert.Equal(expected, q.EstimatedRemainingTime!.Value.TotalSeconds, 3);
        Assert.Equal(now + TimeSpan.FromSeconds(expected), q.EstimatedCompletionTime!.Value);

        // 롤링 윈도: 표본 8개 초과 시 오래된 표본 탈락
        for (var i = 0; i < RenderQueueService.EtaSampleWindow; i++) q.RecordThroughputSample(60_000, 1.0);
        Assert.Equal(60.0, q.BaselineSecondsPerJob!.Value, 6);
        Assert.Equal(60.0 * 2.6, q.EstimatedRemainingTime!.Value.TotalSeconds, 3);

        // UI 라벨: 156s → 02:36 · KST
        var label = RenderQueueService.FormatEta(TimeSpan.FromSeconds(156), new DateTimeOffset(2026, 11, 8, 23, 32, 36, TimeSpan.Zero));
        Assert.Equal("⏱️ ETA: ~02:36 (Est. 08:32:36 KST)", label);

        using var vm = new RenderQueueViewModel(q);
        vm.Refresh();
        Assert.Equal(RenderQueueService.FormatEta(q.EstimatedRemainingTime!.Value, q.EstimatedCompletionTime!.Value), vm.EtaLabel);
    }
}
