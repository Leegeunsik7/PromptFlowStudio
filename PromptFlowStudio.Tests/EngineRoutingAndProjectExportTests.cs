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

    private sealed class CloudFake(bool enabled) : IVideoGenerationEngine
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
    public async Task RenderEngine_UnprobedOfflineComfyUIWithDashScopeKey_FastProbesAndRoutesToCloudOnFirstClick()
    {
        var exe = Path.Combine(_dir, "comfy_cli.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        var cfg = new EngineConfigService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Engines:VideoCli:Exe"] = exe }).Build());
        var comfy = new ComfyUiVideoEngine(cfg);
        Assert.True(comfy.Enabled);   // 로컬 CLI 구성됨 — 서버만 오프라인
        var cloud = new CloudFake(enabled: true);   // DashScope 키 저장됨
        var probes = new List<string>();
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var engine = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, cloud }, cfg), null, null)
        {
            RenderMode = () => RenderEngineKind.ComfyUi,
            ForceSimulation = () => false,
            BlockSilentMock = true,
            ComfyOnline = () => null,   // 앱 기동 직후 — 헬스 체크 미완료
            ComfyBaseUrl = () => "http://127.0.0.1:59998/",
            ComfyProbe = async (url, ct) => { probes.Add(url); await Task.Delay(Timeout.Infinite, ct); return true; },   // 무응답 → 400ms 컷
            Clock = () => now,
            Downloader = (_, dest, _) => { File.WriteAllBytes(dest, new byte[4096]); return Task.CompletedTask; },
            Ambient = null,
            MockFfmpeg = () => throw new InvalidOperationException("Mock 생성 금지"),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = new BatchRenderJob { Prompt = "첫 클릭", Resolution = "1280x720", OutputPath = Path.Combine(_dir, "first.mp4") };
        var r = await engine.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);
        sw.Stop();

        Assert.True(r.Ok, r.Message);
        Assert.Equal(RenderEngineKind.Cloud, r.EngineKind);
        Assert.Equal(1, cloud.Calls);                                    // 첫 클릭에 즉시 Cloud Wan 2.1
        Assert.Equal(new[] { "http://127.0.0.1:59998" }, probes);        // 설정 엔드포인트로 1회 프로브
        Assert.Equal(1, engine.FastProbeCount);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, VideoEngineRenderExecutionEngine.FastProbeTimeout.TotalMilliseconds - 50, 5000);
        Assert.Equal(VideoEngineRenderExecutionEngine.AutoCloudNotice, MultiDurationPlanner.MetaOf(job, VideoEngineRenderExecutionEngine.MetaEngineNotice));

        // 15초 이내 재클릭 → 캐시 결과 재사용(재프로브 없음)
        now = now.AddSeconds(10);
        Assert.True((await engine.ExecuteAsync(new BatchRenderJob { Prompt = "2", OutputPath = Path.Combine(_dir, "second.mp4") }, new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(1, engine.FastProbeCount);
        Assert.Equal(2, cloud.Calls);

        // 15초 경과 → 재프로브
        now = now.AddSeconds(6);
        await engine.EnsureFastProbeAsync(CancellationToken.None);
        Assert.Equal(2, engine.FastProbeCount);

        // 신선한 헬스 체크 결과가 있으면 프로브 생략 · Cloud 키 미구성이면 프로브 생략
        var fresh = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, cloud }, cfg), null, null)
        { RenderMode = () => RenderEngineKind.ComfyUi, ComfyOnline = () => false, ComfyCheckedAt = () => now.AddSeconds(-3), Clock = () => now, ComfyProbe = (_, _) => throw new InvalidOperationException("프로브 금지") };
        await fresh.EnsureFastProbeAsync(CancellationToken.None);
        Assert.Equal(0, fresh.FastProbeCount);
        var noKey = new VideoEngineRenderExecutionEngine(new VideoEngineSelector(new IVideoGenerationEngine[] { comfy, new CloudFake(enabled: false) }, cfg), null, null)
        { RenderMode = () => RenderEngineKind.ComfyUi, ComfyProbe = (_, _) => throw new InvalidOperationException("프로브 금지") };
        await noKey.EnsureFastProbeAsync(CancellationToken.None);
        Assert.Equal(0, noKey.FastProbeCount);
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
