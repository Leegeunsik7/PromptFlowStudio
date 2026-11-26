using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>작업실 패널 정리(중복 완료 플레이어 · 빈 프레임 신호 모니터 제거 · 실제 첫 화면 미리보기) + 앰비언트 단일 산출물.</summary>
public sealed class WorkspacePanelCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_wsclean_" + Guid.NewGuid().ToString("N"));

    public WorkspacePanelCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    private static string Workspace() => File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));

    [Fact]
    [Trait("Category", "FastUnit")]
    public void WorkspaceMarkup_RemovesDuplicatePanels_AndWiresRealFirstFramePreview()
    {
        var src = Workspace();
        Assert.DoesNotContain("ws-mini-q-vid", src, StringComparison.Ordinal);           // 좌측 중복 완료 플레이어 제거
        Assert.DoesNotContain("프레임 신호 대기", src, StringComparison.Ordinal);          // 빈 MMF 모니터 제거
        Assert.DoesNotContain("id=\"ws-live\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("pfsLiveCanvas.draw", src, StringComparison.Ordinal);       // 고아 16ms 폴링 루프 제거
        Assert.DoesNotContain("<FirstFramePreviewDeck", src, StringComparison.Ordinal);   // 정적 예상 프레임 더미 제거
        Assert.Contains("data-first-frame-preview=\"1\"", src, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"GenerateFirstFramePreviewAsync\"", src, StringComparison.Ordinal);
        Assert.Contains("GeminiSvc.GenerateFirstFrameImageAsync(", src, StringComparison.Ordinal);
        Assert.Contains("이 첫 화면으로 영상 생성", src, StringComparison.Ordinal);
        Assert.Contains("BindStartFrameAsync(path,", src, StringComparison.Ordinal);       // 시작 프레임(I2V) 바인딩
        Assert.Contains("PlutoAutonomousActivityFeedService.Redact(r.Message)", src, StringComparison.Ordinal);
        Assert.Contains("_ffpCts", src, StringComparison.Ordinal);                         // CTS 해제 쌍
        Assert.Contains("② 🎬 라이브 비디오 캔버스", src, StringComparison.Ordinal);          // 재생은 캔버스 단일 소유
        Assert.Contains("📂 폴더 열기", src, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData("1280*720", "16:9")]
    [InlineData("720*1280", "9:16")]
    [InlineData("960*960", "1:1")]
    [InlineData("1080x1920", "9:16")]
    [InlineData("", "16:9")]
    [InlineData(null, "16:9")]
    public void AspectRatioOf_MapsWorkspaceSizes(string? size, string expected)
        => Assert.Equal(expected, GeminiPromptService.AspectRatioOf(size));

    [Fact]
    [Trait("Category", "FastUnit")]
    public void FirstFramePayload_RequestsImageModality_AndAspect()
    {
        var p = GeminiPromptService.BuildFirstFrameImagePayload("a baby smiling", "9:16");
        Assert.Contains("\"responseModalities\":[\"IMAGE\"]", p, StringComparison.Ordinal);
        Assert.Contains("\"aspectRatio\":\"9:16\"", p, StringComparison.Ordinal);
        Assert.Contains("a baby smiling", p, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ExtractInlineImage_ValidatesSchema()
    {
        var b64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var ok = GeminiPromptService.ExtractInlineImage("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"x\"},{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"" + b64 + "\"}}]}}]}");
        Assert.NotNull(ok);
        Assert.Equal("image/png", ok!.Value.MimeType);
        Assert.Equal(4, ok.Value.Data.Length);

        Assert.Null(GeminiPromptService.ExtractInlineImage(null));
        Assert.Null(GeminiPromptService.ExtractInlineImage("not json"));
        Assert.Null(GeminiPromptService.ExtractInlineImage("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"only text\"}]}}]}"));
        Assert.Null(GeminiPromptService.ExtractInlineImage("{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"text/plain\",\"data\":\"" + b64 + "\"}}]}}]}"));
        Assert.Null(GeminiPromptService.ExtractInlineImage("{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"@@@\"}}]}}]}"));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task FirstFrameImage_NoKey_FallsBackWithoutNetwork()
    {
        using var http = new System.Net.Http.HttpClient();
        var svc = new GeminiPromptService(http, () => null);
        Assert.False(svc.IsImageGenerationAvailable);
        var r = await svc.GenerateFirstFrameImageAsync("baby", "1280*720", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Null(r.Image);
        Assert.Equal(GeminiPromptService.ImageUnavailableMessage, r.Message);
        Assert.False((await svc.GenerateFirstFrameImageAsync("  ", "1280*720")).Ok);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task AmbientJob_LeavesSingleFinalMp4_NoRawDuplicate()
    {
        var raw = Path.Combine(_dir, "wo_20261126_123204.mp4");
        File.WriteAllBytes(raw, new byte[4096]);
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, a, _) => { File.WriteAllBytes(a[^1], new byte[8192]); return Task.FromResult(true); }),
        };
        var job = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_AmbientSlow };
        job.Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaProject, "wo_20261126_123204");

        var output = await engine.ApplyAmbientLoopAsync(job, raw, CancellationToken.None);

        Assert.Equal(Path.Combine(_dir, "wo_20261126_123204_ambient15s.mp4"), output);
        Assert.False(File.Exists(raw));
        Assert.Equal(new[] { output }, Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task AmbientJob_Failure_KeepsRawOriginal()
    {
        var raw = Path.Combine(_dir, "wo_x.mp4");
        File.WriteAllBytes(raw, new byte[4096]);
        var engine = new VideoEngineRenderExecutionEngine(null, null, null)
        {
            Ambient = new AmbientLoopService(() => "ffmpeg", (_, _, _) => Task.FromResult(false)),
        };
        var job = new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_AmbientSlow };
        var output = await engine.ApplyAmbientLoopAsync(job, raw, CancellationToken.None);
        Assert.Equal(raw, output);
        Assert.True(File.Exists(raw));   // 실패 시 원본 유실 금지
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void DiscardIntermediate_SamePathOrEmptyFinal_IsNoOp()
    {
        var a = Path.Combine(_dir, "a.mp4");
        File.WriteAllBytes(a, new byte[16]);
        Assert.False(VideoEngineRenderExecutionEngine.DiscardIntermediate(a, a));
        var empty = Path.Combine(_dir, "e.mp4");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.False(VideoEngineRenderExecutionEngine.DiscardIntermediate(a, empty));
        Assert.True(File.Exists(a));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoInvariants_RemainNineSixTwo()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }
}
