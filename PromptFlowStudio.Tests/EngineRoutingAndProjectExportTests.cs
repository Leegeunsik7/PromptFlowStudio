using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>☁️ 로컬 ComfyUI 부재 → Cloud Wan 2.1 자동 전환 · 무엔진 시 Mock 무폴백 명시 실패 · 15초 엠비언트 5초→15초 · 📁 StockSuite 프로젝트 폴더 · 툴바 줄바꿈 가드.</summary>
public sealed class EngineRoutingAndProjectExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_route_" + Guid.NewGuid().ToString("N"));

    public EngineRoutingAndProjectExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal sealed class CloudFake(bool enabled) : IVideoGenerationEngine
    {
        public WanVideoRequest? Seen { get; private set; }
        public int Calls { get; private set; }
        public string Key => "Wan";
        public string DisplayName => "Cloud Wan 2.1";
        public bool Enabled => enabled;
        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            Seen = request; Calls++;
            return Task.FromResult(new WanGenerationResult(true, WanTaskStatus.Succeeded, "https://dashscope.example/v.mp4", "완료", "t-1", TimeSpan.Zero));
        }
        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "n/a"));
    }

    private static readonly EngineConfigService EmptyCfg = new(new ConfigurationBuilder().Build());

    /// <summary>ComfyUI 모드(기본) · 로컬 CLI 미구성 ComfyUI 엔진 + Cloud 엔진 · 헬스 체크 오프라인 · Mock 생성 금지.</summary>
    private (VideoEngineRenderExecutionEngine Engine, List<IReadOnlyList<string>> Ffmpeg) LocalModeEngine(CloudFake cloud)
    {
        var comfy = new ComfyUiVideoEngine(EmptyCfg);
        Assert.False(comfy.Enabled);   // 로컬 CLI 미구성
        var calls = new List<IReadOnlyList<string>>();
        var engine = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, cloud }, EmptyCfg), null, null)
        {
            RenderMode = () => RenderEngineKind.ComfyUi,
            ForceSimulation = () => false,
            BlockSilentMock = true,
            ComfyOnline = () => false,
            Downloader = (_, dest, _) => { File.WriteAllBytes(dest, new byte[4096]); return Task.CompletedTask; },
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { calls.Add(a); File.WriteAllBytes(a[^1], new byte[4096]); return Task.FromResult(true); }),
            MockFfmpeg = () => throw new InvalidOperationException("Mock 생성 금지"),
        };
        return (engine, calls);
    }

    [Fact]
    public async Task AmbientMode_LocalComfyOffline_WithDashScopeKey_RoutesToCloud_And_BuildsPingPong15s()
    {
        var cloud = new CloudFake(enabled: true);
        var (engine, ffmpeg) = LocalModeEngine(cloud);
        Assert.True(engine.IsAvailable);
        Assert.Equal(RenderEngineKind.Cloud, engine.EngineKind);

        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var template = new BatchRenderJob
        {
            Prompt = "따뜻한 햇살 아래 잠든 아기, 느린 돌리", Resolution = "1280x720",
            OutputPath = Path.Combine(_dir, "ws_amb.mp4"), DurationMode = VideoDurationMode.Sec15_AmbientSlow,
            DurationSec = MultiDurationPlanner.ShotSeconds,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [VideoEngineRenderExecutionEngine.MetaAmbientPingPong] = "true" },
        };
        var jobs = q.EnqueueJob(template, new string?[3], "ws_amb");
        Assert.Single(jobs);   // 15초 엠비언트 = 단일 컷(5초 마스터)
        Assert.True(jobs[0].IsAmbientLoop);
        Assert.Equal(15, MultiDurationPlanner.TargetSeconds(VideoDurationMode.Sec15_AmbientSlow));

        await q.ProcessQueueAsync(CancellationToken.None);

        var job = jobs[0];
        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(RenderEngineKind.Cloud, job.EngineKind);
        Assert.Equal(1, cloud.Calls);
        Assert.Equal(VideoEngineRenderExecutionEngine.AmbientCloudClipSeconds, cloud.Seen!.Duration);   // 5초 마스터 요청
        Assert.Single(ffmpeg);                                                                            // 핑퐁 15초 조립 1회
        Assert.EndsWith("_ambient15s.mp4", job.ResultPath, StringComparison.Ordinal);
        Assert.True(File.Exists(job.ResultPath));
        Assert.Equal("applied", MultiDurationPlanner.MetaOf(job, VideoEngineRenderExecutionEngine.MetaAmbientApplied));
        Assert.Equal(VideoEngineRenderExecutionEngine.AutoCloudNotice, MultiDurationPlanner.MetaOf(job, VideoEngineRenderExecutionEngine.MetaEngineNotice));
    }

    [Fact]
    public async Task TripleCut_NoLocalAndNoCloud_FailsWithKoreanBanner_WithoutMockFile()
    {
        var cloud = new CloudFake(enabled: false);   // DashScope 키 미구성
        var (engine, ffmpeg) = LocalModeEngine(cloud);
        Assert.True(engine.IsAvailable);   // 큐가 SimulateAsync(Mock)로 빠지지 않도록
        Assert.NotEqual(RenderEngineKind.Mock, engine.EngineKind);

        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var template = new BatchRenderJob
        {
            Prompt = "벚꽃 길 산책", Resolution = "1280x720", OutputPath = Path.Combine(_dir, "ws_tri.mp4"),
            DurationMode = VideoDurationMode.Sec15_Triple, DurationSec = MultiDurationPlanner.ShotSeconds,
        };
        var jobs = q.EnqueueJob(template, new string?[3], "ws_tri");
        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.NotEmpty(jobs);
        Assert.All(jobs, j =>
        {
            Assert.Equal(BatchJobStatus.Failed, j.Status);
            Assert.NotEqual(RenderEngineKind.Mock, j.EngineKind);
            Assert.Null(j.ResultPath);
        });
        Assert.Contains(jobs, j => j.ErrorMessage == VideoEngineRenderExecutionEngine.NoEngineMessage);
        Assert.Equal(0, cloud.Calls);
        Assert.Empty(ffmpeg);
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public void ExplicitMockMode_StillSimulates_AndLegacyAdapterWithoutGuard_Unchanged()
    {
        var mock = new VideoEngineRenderExecutionEngine(null, null, null) { ForceSimulation = () => true, BlockSilentMock = true };
        Assert.False(mock.IsAvailable);   // [환경설정] Mock 명시 → 시뮬레이션 허용
        Assert.False(new VideoEngineRenderExecutionEngine(null, null, null).IsAvailable);   // 가드 미설정(모바일) → 기존 동작
    }

    [Theory]
    [InlineData("PlutoCore Neural", "PlutoCore_Neural")]
    [InlineData("  첫 생일: 여행/브이로그?  ", "첫_생일_여행_브이로그")]
    [InlineData("   ", StockSuiteExporter.DefaultProjectSlug)]
    [InlineData("a.b..c", "a_b_c")]
    [InlineData("CON", "_CON")]
    public void SanitizeProjectName_ProducesSafeSlug(string raw, string expected)
    {
        Assert.Equal(expected, StockSuiteExporter.SanitizeProjectName(raw));
        Assert.Equal(expected + "_20261115", StockSuiteExporter.BuildProjectFolderName(raw, new DateTime(2026, 11, 15, 9, 0, 0)));
    }

    [Fact]
    public async Task StockSuiteExport_WithProjectName_WritesIntoDatedSubfolder_WithProjectManifest()
    {
        var root = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(root);
        var src = Path.Combine(_dir, "ws_amb_ambient15s.mp4");
        File.WriteAllBytes(src, ExportMediaIntegrityGuard.BuildMinimalMp4());
        var exp = new StockSuiteExporter(() => root, () => "mp4", () => null)
        {
            SilentContainerVerifier = static _ => SilentVerdict.Silent,
            LocalNow = () => new DateTime(2026, 11, 15, 10, 30, 0),
        };
        var kit = new PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit(new[] { "첫 생일 여행" }, "캡션", "caption", new[] { "#아기", "#여행" }, "19:00–21:00");

        var r = await exp.ExportWithSubtitlesAsync(src, null, "PlutoCore Neural", CancellationToken.None, kit, "neural glow");

        Assert.True(r.Ok, r.Message);
        var projDir = Path.Combine(root, "PlutoCore_Neural_20261115");
        Assert.Equal(Path.Combine(projDir, Path.GetFileName(src)), r.TargetPath);
        Assert.True(File.Exists(r.TargetPath));
        Assert.False(File.Exists(Path.Combine(root, StockSuiteExporter.DefaultAssetName)));   // 평면 루트에 쌓지 않음
        Assert.Empty(Directory.GetFiles(root, "*.mp4"));
        Assert.Contains("PlutoCore_Neural_20261115", r.Message, StringComparison.Ordinal);

        var manifestPath = Path.Combine(projDir, StockSuiteExporter.ProjectManifestName);
        Assert.True(File.Exists(manifestPath));
        using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            var m = doc.RootElement;
            Assert.Equal("PlutoCore Neural", m.GetProperty("projectName").GetString());
            Assert.Equal("PlutoCore_Neural_20261115", m.GetProperty("folder").GetString());
            Assert.Equal("neural glow", m.GetProperty("prompt").GetString());
            Assert.Equal(2, m.GetProperty("sns").GetProperty("hashtags").GetArrayLength());
            Assert.Equal(Path.GetFileName(src), m.GetProperty("exports")[0].GetProperty("file").GetString());
        }

        // 같은 프로젝트에 두 번째 영상 → 같은 폴더 · 이력 2건
        var src2 = Path.Combine(_dir, "ws_tri_15s.mp4");
        File.WriteAllBytes(src2, ExportMediaIntegrityGuard.BuildMinimalMp4(2048));
        Assert.True((await exp.ExportWithSubtitlesAsync(src2, null, "PlutoCore Neural")).Ok);
        using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            Assert.Equal(2, doc.RootElement.GetProperty("exports").GetArrayLength());

        // 프로젝트명 공백 → 기존 루트 라이브 교체(bg_ambient.mp4) 유지
        var legacy = await exp.ExportWithSubtitlesAsync(src, null, "  ");
        Assert.True(legacy.Ok, legacy.Message);
        Assert.Equal(Path.Combine(root, StockSuiteExporter.DefaultAssetName), legacy.TargetPath);

        // 루트 미설정 → 경로 재설정 안내
        var unset = await new StockSuiteExporter(() => null).ExportWithSubtitlesAsync(src, null, "x");
        Assert.False(unset.Ok);
        Assert.True(unset.NeedsPathSetup);
    }

    private static readonly SubtitleCue[] s_threeActCues =
    {
        SubtitleCue.Precise(0, 4.5, "셔터 3초 전", "3 seconds before"),
        SubtitleCue.Precise(4.5, 10.25, "사진 속 클라이맥스"),
        new(11, 14, ""),                                  // 빈 큐 → 제외
        SubtitleCue.Precise(3661.007, 3662, "1시간 뒤 여운"),
    };

    [Fact]
    public void BuildSubRipContent_FormatsMultiActCues_WithValidSrtTimecodes()
    {
        var srt = StockSuiteExporter.BuildSubRipContent(s_threeActCues);

        Assert.StartsWith("1\r\n00:00:00,000 --> 00:00:04,500\r\n셔터 3초 전\r\n3 seconds before\r\n\r\n", srt, StringComparison.Ordinal);
        Assert.Contains("2\r\n00:00:04,500 --> 00:00:10,250\r\n사진 속 클라이맥스\r\n\r\n", srt, StringComparison.Ordinal);
        Assert.Contains("3\r\n01:01:01,007 --> 01:01:02,000\r\n1시간 뒤 여운\r\n\r\n", srt, StringComparison.Ordinal);
        Assert.DoesNotContain("4\r\n", srt, StringComparison.Ordinal);
        Assert.Equal(0, CountOf(srt.Replace("\r\n", "", StringComparison.Ordinal), "\n"));   // 순수 CRLF
        Assert.Equal("", StockSuiteExporter.BuildSubRipContent(null));
        Assert.Equal("", StockSuiteExporter.BuildSubRipContent(Array.Empty<SubtitleCue>()));
    }

    [Fact]
    public async Task ProjectExport_WithSubtitlesAndPublishingKit_WritesSrtAndSnsCopySidecarsAndManifestFlags()
    {
        var root = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(root);
        var src = Path.Combine(_dir, "montage_15s.mp4");
        File.WriteAllBytes(src, ExportMediaIntegrityGuard.BuildMinimalMp4());
        var exp = new StockSuiteExporter(() => root, () => "mp4", () => null)
        {
            SilentContainerVerifier = static _ => SilentVerdict.Silent,
            LocalNow = () => new DateTime(2026, 11, 16, 9, 0, 0),
        };
        var kit = new PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit(new[] { "첫 생일 여행 브이로그" }, "캡션 한국어", "english caption", new[] { "#아기", "#여행" }, "19:00–21:00");

        var r = await exp.ExportWithSubtitlesAsync(src, null, "Baby Trip", CancellationToken.None, kit, null, s_threeActCues);

        Assert.True(r.Ok, r.Message);
        var projDir = Path.Combine(root, "Baby_Trip_20261116");
        Assert.True(r.HasSubtitlesSrt);
        Assert.True(r.HasSnsCopyText);
        Assert.False(r.HasThumbnailJpg);   // ffmpeg 미탐지
        Assert.Equal(StockSuiteExporter.BuildSubRipContent(s_threeActCues), File.ReadAllText(Path.Combine(projDir, StockSuiteExporter.SubRipFileName)));

        var sns = File.ReadAllText(Path.Combine(projDir, StockSuiteExporter.SnsCopyFileName));
        var shorts = sns.IndexOf(StockSuiteExporter.ShortsCopyHeader, StringComparison.Ordinal);
        var reels = sns.IndexOf(StockSuiteExporter.ReelsCopyHeader, StringComparison.Ordinal);
        Assert.Equal(0, shorts);
        Assert.True(reels > shorts);
        Assert.Contains("첫 생일 여행 브이로그", sns[..reels], StringComparison.Ordinal);
        Assert.Contains("#아기", sns[reels..], StringComparison.Ordinal);
        Assert.Contains("19:00–21:00", sns[reels..], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(projDir, StockSuiteExporter.ThumbnailFileName)));
        Assert.Contains("자막(.srt) + SNS 게시문 저장 완료", StockSuiteExporter.DescribeCreatorBundle(r.HasSubtitlesSrt, r.HasSnsCopyText, r.HasThumbnailJpg), StringComparison.Ordinal);

        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(projDir, StockSuiteExporter.ProjectManifestName))))
        {
            var m = doc.RootElement;
            Assert.True(m.GetProperty("HasSubtitlesSrt").GetBoolean());
            Assert.True(m.GetProperty("HasSnsCopyText").GetBoolean());
            Assert.False(m.GetProperty("HasThumbnailJpg").GetBoolean());
            Assert.Equal(2, m.GetProperty("sidecars").GetArrayLength());
        }

        // 자막 없음 → subtitles.srt 미생성 · 키트 없음 → 템플릿 자동 SNS 게시문
        var src2 = Path.Combine(_dir, "plain_15s.mp4");
        File.WriteAllBytes(src2, ExportMediaIntegrityGuard.BuildMinimalMp4(2048));
        var r2 = await exp.ExportWithSubtitlesAsync(src2, null, "Plain Day");
        Assert.True(r2.Ok, r2.Message);
        var dir2 = Path.Combine(root, "Plain_Day_20261116");
        Assert.False(r2.HasSubtitlesSrt);
        Assert.False(File.Exists(Path.Combine(dir2, StockSuiteExporter.SubRipFileName)));
        Assert.True(r2.HasSnsCopyText);
        Assert.Contains(StockSuiteExporter.ReelsCopyHeader, File.ReadAllText(Path.Combine(dir2, StockSuiteExporter.SnsCopyFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectExport_WithoutFfmpegThumbnailSuccess_CompletesGracefullyWithoutThrowing()
    {
        var root = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(root);
        var fakeFfmpeg = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllBytes(fakeFfmpeg, new byte[] { 0x4D, 0x5A });
        var src = Path.Combine(_dir, "stub_15s.mp4");
        File.WriteAllBytes(src, ExportMediaIntegrityGuard.BuildMinimalMp4());
        var thumbAttempts = new List<string>();
        // 🧪 ffmpeg 는 "있지만" 합성 스텁 입력 → 정상 종료해도 산출물 없음(또는 썸네일 단계에서 예외)
        FfmpegRunner runner = (_, args, _) =>
        {
            var seekAt = -1;
            for (var i = 0; i < args.Count; i++) if (args[i] == "-ss") seekAt = i + 1;
            if (seekAt > 0 && args[^1].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                thumbAttempts.Add(args[seekAt]);
                if (thumbAttempts.Count == 2) throw new InvalidOperationException("synthetic stub");
            }
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => root, () => "mp4", () => fakeFfmpeg, runner)
        {
            SilentContainerVerifier = static _ => SilentVerdict.Silent,
            LocalNow = () => new DateTime(2026, 11, 16, 9, 0, 0),
        };

        var r = await exp.ExportWithSubtitlesAsync(src, null, "Stub Clip", CancellationToken.None, null, null, s_threeActCues);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { "00:00:01.000", "00:00:00.200" }, thumbAttempts);   // 1초 → 짧은 클립 0.2초 재시도
        Assert.False(r.HasThumbnailJpg);
        var projDir = Path.Combine(root, "Stub_Clip_20261116");
        Assert.False(File.Exists(Path.Combine(projDir, StockSuiteExporter.ThumbnailFileName)));
        Assert.Empty(Directory.GetFiles(projDir, "*.~pfs*"));   // 임시 파일 잔여 없음
        Assert.True(r.HasSubtitlesSrt);
        Assert.True(r.HasSnsCopyText);
        Assert.True(File.Exists(r.TargetPath));

        // ffmpeg 경로 해석 자체가 실패해도 비차단
        var exp2 = new StockSuiteExporter(() => root, () => "mp4", () => throw new IOException("locator down"))
        {
            SilentContainerVerifier = static _ => SilentVerdict.Silent,
            LocalNow = () => new DateTime(2026, 11, 16, 9, 0, 0),
        };
        Assert.False(await exp2.TryExtractThumbnailAsync(src, projDir, CancellationToken.None));
    }

    [Fact]
    public async Task ExperimentLedger_FailingChallengerWithLowCompletion_RecordsCompletionDropDimensionInMistakeNotebook()
    {
        using var learning = new PromptFlowStudio.Services.Pluto.PlutoLearningService(Path.Combine(_dir, "trend.json"));
        using var notebook = new PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var loop = new PromptFlowStudio.Services.Pluto.PlutoClosedLoopReinforcementService(learning, notebook);
        using var ledger = new PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService(loop);
        var cues = new[] { SubtitleCue.Precise(0.2, 4.95, "a") };
        const string hookB = "중반에 반전이 와요";
        var a = new PromptFlowStudio.Services.Pluto.PlutoExperimentVariant(PromptFlowStudio.Services.Pluto.PlutoVariantABGenerator.ChampionLabel,
            "이 순간을 기다렸어요", PromptFlowStudio.Services.Pluto.PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", "", cues);
        var b = new PromptFlowStudio.Services.Pluto.PlutoExperimentVariant(PromptFlowStudio.Services.Pluto.PlutoVariantABGenerator.ChallengerLabel,
            hookB, PromptFlowStudio.Services.Pluto.PlutoColorGradingCompiler.GoldenHourWarmName, "Lullaby", "", cues);
        var plan = new PromptFlowStudio.Services.Pluto.PlutoVariantABPlan("exp-step7-comp", "돌잔치", a, b, "h");

        // 챌린저: 3초 5 %(결손 88 % → 벌점 ≥ 차단선) · 완주 1 %(결손 97 %) · CTR 3 %(정상) → 완주 이탈이 최대 결손
        var loser = new PromptFlowStudio.Services.Pluto.PlutoExperimentMetricsInput(5, 1, 3);
        Assert.Equal(PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService.DimensionCompletionDrop, PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService.ClassifyLoserFailureDimension(loser));
        Assert.Equal(PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService.DimensionLowCtr,
            PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService.ClassifyLoserFailureDimension(new PromptFlowStudio.Services.Pluto.PlutoExperimentMetricsInput(40, 29, 0.2)));
        Assert.Equal(PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService.DimensionHookRetention,
            PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService.ClassifyLoserFailureDimension(new PromptFlowStudio.Services.Pluto.PlutoExperimentMetricsInput(10, 25, 1.4)));

        var r = await ledger.EvaluateExperimentAsync(plan, new PromptFlowStudio.Services.Pluto.PlutoExperimentMetricsInput(60, 50, 5), loser);

        Assert.Equal(PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService.WinnerChampion, r.WinnerLabel);
        Assert.True(r.LoserPenalized);
        var rules = notebook.GetActiveMistakeRules();
        Assert.NotEmpty(rules);
        var rule = Assert.Single(rules, x => x.PatternKey.Contains(hookB, StringComparison.Ordinal));
        Assert.Equal(PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService.DimensionCompletionDrop, rule.FailureDimension);
        Assert.Equal(PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService.ReasonCompletionDrop, rule.DiagnosticReasonKorean);
        Assert.DoesNotContain(rules, x => x.FailureDimension == PromptFlowStudio.Services.Pluto.PlutoMistakeNotebookService.DimensionHookRetention);
    }

    [Fact]
    public async Task StockSuiteExporter_DualMasterBundle_CopiesBothAspectVideosAndSidecarsIntoDualFolder()
    {
        var root = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(root);
        var land = Path.Combine(_dir, "master_16x9.mp4");
        var vert = Path.Combine(_dir, "master_9x16.mp4");
        File.WriteAllBytes(land, ExportMediaIntegrityGuard.BuildMinimalMp4());
        File.WriteAllBytes(vert, ExportMediaIntegrityGuard.BuildMinimalMp4(2048));
        var exp = new StockSuiteExporter(() => root, () => "mp4", () => null) { LocalNow = () => new DateTime(2026, 11, 17, 9, 0, 0) };
        var kit = new PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit(new[] { "듀얼 마스터 여행" }, "캡션", "caption", new[] { "#여행" }, "19:00–21:00");

        var r = await exp.ExportDualMasterBundleToStockSuiteAsync(root, "Family Trip", land, vert, s_threeActCues, kit);

        Assert.True(r.Ok, r.Message);
        var dual = Path.Combine(root, "Family_Trip_Dual_20261117");
        Assert.Equal(dual, r.TargetPath);
        Assert.Equal(File.ReadAllBytes(land), File.ReadAllBytes(Path.Combine(dual, "master_16x9.mp4")));
        Assert.Equal(File.ReadAllBytes(vert), File.ReadAllBytes(Path.Combine(dual, "master_9x16.mp4")));
        Assert.True(r.HasSubtitlesSrt);
        Assert.True(r.HasSnsCopyText);
        Assert.False(r.HasThumbnailJpg);   // ffmpeg 미탐지 → best-effort 생략
        Assert.Equal(StockSuiteExporter.BuildSubRipContent(s_threeActCues), File.ReadAllText(Path.Combine(dual, StockSuiteExporter.SubRipFileName)));
        Assert.Contains("듀얼 마스터 여행", File.ReadAllText(Path.Combine(dual, StockSuiteExporter.SnsCopyFileName)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dual, "*.~pfs*"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dual, StockSuiteExporter.ProjectManifestName)));
        var m = doc.RootElement;
        Assert.True(m.GetProperty("IsDualMaster").GetBoolean());
        Assert.Equal("master_16x9.mp4", m.GetProperty("landscape16x9").GetString());
        Assert.Equal("master_9x16.mp4", m.GetProperty("vertical9x16").GetString());
        Assert.True(m.GetProperty("HasSubtitlesSrt").GetBoolean());
        Assert.True(m.GetProperty("HasSnsCopyText").GetBoolean());
        Assert.False(m.GetProperty("HasThumbnailJpg").GetBoolean());
        Assert.Equal(2, m.GetProperty("exports").GetArrayLength());
        Assert.Equal("Family Trip", m.GetProperty("projectName").GetString());
    }

    [Fact]
    public async Task StockSuiteExporter_DualMasterBundle_BlocksCorruptVerticalMp4ViaIntegrityGuard()
    {
        var root = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(root);
        var land = Path.Combine(_dir, "ok_16x9.mp4");
        var vert = Path.Combine(_dir, "broken_9x16.mp4");
        File.WriteAllBytes(land, ExportMediaIntegrityGuard.BuildMinimalMp4());
        var full = ExportMediaIntegrityGuard.BuildMinimalMp4();
        File.WriteAllBytes(vert, full.AsSpan(0, full.Length / 2).ToArray());   // ✂️ 잘린 MP4
        var exp = new StockSuiteExporter(() => root, () => "mp4", () => null) { LocalNow = () => new DateTime(2026, 11, 17, 9, 0, 0) };

        var r = await exp.ExportDualMasterBundleToStockSuiteAsync(root, "Broken", land, vert, s_threeActCues);

        Assert.False(r.Ok);
        Assert.False(r.NeedsPathSetup);
        Assert.Null(r.TargetPath);
        Assert.Contains("9:16", r.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "Broken_Dual_20261117")));   // 폴더 생성 전 차단
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    private static string CssRule(string css, string selector)
    {
        var a = css.IndexOf("    " + selector + " {", StringComparison.Ordinal);
        Assert.True(a >= 0, "CSS 규칙 누락: " + selector);
        var b = css.IndexOf('}', a);
        return css.Substring(a, b - a);
    }

    [Fact]
    public void Workspace_TopBarAndCanvasToolbar_WrapRows_AndKeepKoreanLabelsOnOneLine()
    {
        var ws = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));

        // 3단 헤더: 기본+상태 배지 / Pluto 2열 스트립 / 도구 행
        var primary = ws.IndexOf("data-ws-row=\"primary\"", StringComparison.Ordinal);
        var pluto = ws.IndexOf("data-ws-row=\"pluto\"", StringComparison.Ordinal);
        var tools = ws.IndexOf("data-ws-row=\"tools\"", StringComparison.Ordinal);
        Assert.True(primary > 0 && pluto > primary && tools > pluto);
        var status = ws.IndexOf("data-ws-status=\"1\"", StringComparison.Ordinal);
        Assert.True(status > primary && status < pluto);
        Assert.True(ws.IndexOf("data-plutocore-pill", status, StringComparison.Ordinal) < pluto);
        Assert.True(ws.IndexOf("data-pluto-storage", status, StringComparison.Ordinal) < pluto);
        var brief = ws.IndexOf("<div class=\"ws-brief-strip\"", StringComparison.Ordinal);
        var adaptive = ws.IndexOf("<div class=\"ws-adaptive-strip\"", StringComparison.Ordinal);
        Assert.True(brief > pluto && brief < tools && adaptive > brief && adaptive < tools);

        var navrow = CssRule(ws, ".ws-navrow");
        Assert.Contains("flex-wrap: wrap", navrow, StringComparison.Ordinal);
        Assert.Contains("align-items: center", navrow, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: wrap", CssRule(ws, ".ws-status-badges"), StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0,1fr) minmax(0,1fr)", CssRule(ws, ".ws-pluto-strip"), StringComparison.Ordinal);
        var chips = CssRule(ws, ".ws-head .ws-back, .ws-head .ws-pcore, .ws-head .ws-recover, .ws-head .ws-file-pick, .ws-head select, .ws-head label, .ws-head .ws-eng");
        Assert.Contains("white-space: nowrap", chips, StringComparison.Ordinal);
        Assert.Contains("flex-shrink: 0", chips, StringComparison.Ordinal);

        // ② 라이브 비디오 캔버스 헤더 툴바
        Assert.Contains("flex-wrap: wrap", CssRule(ws, ".ws-card-h"), StringComparison.Ordinal);
        var htsTools = CssRule(ws, ".ws-hts-tools");
        Assert.Contains("flex-wrap: wrap", htsTools, StringComparison.Ordinal);
        Assert.DoesNotContain("inline-flex", htsTools, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", CssRule(ws, ".ws-hts-tools > *"), StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", CssRule(ws, ".ws-hts-tog"), StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", CssRule(ws, ".ws-pluto-fab"), StringComparison.Ordinal);

        // 15초 엠비언트 배지 · 입력 누락 안내 · 프로젝트 폴더 입력 · Cloud 배지
        Assert.Contains("15초 엠비언트 루프 · 5초 마스터 → 무이음새 15초 핑퐁", ws, StringComparison.Ordinal);
        Assert.Contains("data-duration-need-input", ws, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(ws, "data-stock-project=\"1\""));
        Assert.Contains("● Cloud 연결됨", ws, StringComparison.Ordinal);
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
