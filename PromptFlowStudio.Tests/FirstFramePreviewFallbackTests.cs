using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Network;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🖼️ 첫 화면 미리보기 폴백 체인(gemini-2.5-flash-image → imagen-3.0 → 로컬 구도 PNG) + 작업실 컴팩트 레이아웃 마커 + Pluto 9/6/2 불변식.</summary>
public sealed class FirstFramePreviewFallbackTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int PrimaryCalls, FallbackCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var u = request.RequestUri!.AbsoluteUri;
            if (u.Contains(GeminiPromptService.ImageModel, StringComparison.Ordinal)) Interlocked.Increment(ref PrimaryCalls);
            if (u.Contains(GeminiPromptService.FallbackImageModel, StringComparison.Ordinal)) Interlocked.Increment(ref FallbackCalls);
            return Task.FromResult(f(request));
        }
    }

    private static readonly byte[] Tiny = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };

    private static ResilientHttpClient NoWait() => new() { Delay = static (_, _) => Task.CompletedTask, Jitter = static () => 0 };

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ImagenBody(byte[] img) => "{\"predictions\":[{\"mimeType\":\"image/png\",\"bytesBase64Encoded\":\"" + Convert.ToBase64String(img) + "\"}]}";

    private static string GeminiBody(byte[] img) => "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"" + Convert.ToBase64String(img) + "\"}}]}}]}";

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task Primary_Succeeds_NoFallbackCall()
    {
        var h = new StubHandler(_ => Json(HttpStatusCode.OK, GeminiBody(Tiny)));
        using var http = new HttpClient(h);
        var svc = new GeminiPromptService(http, () => "k") { Resilience = NoWait() };
        var r = await svc.GenerateFirstFrameImageAsync("baby smiling", "1280*720");
        Assert.True(r.Ok);
        Assert.False(r.IsLocalFallback);
        Assert.Equal(GeminiPromptService.ImageModel, r.Source);
        Assert.Equal(Tiny, r.Image);
        Assert.Equal(0, h.FallbackCalls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PrimaryFails_FallsBackToImagen()
    {
        var h = new StubHandler(req => req.RequestUri!.AbsoluteUri.Contains(GeminiPromptService.FallbackImageModel, StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, ImagenBody(Tiny))
            : Json(HttpStatusCode.ServiceUnavailable, "{}"));
        using var http = new HttpClient(h);
        var svc = new GeminiPromptService(http, () => "k") { Resilience = NoWait() };
        var r = await svc.GenerateFirstFrameImageAsync("baby", "720*1280");
        Assert.True(r.Ok);
        Assert.False(r.IsLocalFallback);
        Assert.Equal(GeminiPromptService.FallbackImageModel, r.Source);
        Assert.Equal(Tiny, r.Image);
        Assert.True(h.PrimaryCalls >= 1);
        Assert.Equal(1, h.FallbackCalls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task AllRemoteFail_ReturnsLocalPng_MatchingAspect_WithRedactableNote()
    {
        var h = new StubHandler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}"));
        using var http = new HttpClient(h);
        var svc = new GeminiPromptService(http, () => "k") { Resilience = NoWait() };
        var r = await svc.GenerateFirstFrameImageAsync("노을 해변의 아기", "960*960");
        Assert.True(r.Ok);
        Assert.True(r.IsLocalFallback);
        Assert.Equal("image/png", r.MimeType);
        Assert.StartsWith(GeminiPromptService.LocalPreviewPrefix, r.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", r.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(PlutoAutonomousActivityFeedService.Redact(r.Message)));
        var (w, hh) = PngSize(r.Image!);
        Assert.Equal((1024, 1024), (w, hh));
        Assert.Equal(1, h.FallbackCalls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task SchemaMismatch_FallsThroughChain_NoThrow()
    {
        var h = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"unexpected\":true}"));
        using var http = new HttpClient(h);
        var svc = new GeminiPromptService(http, () => "k") { Resilience = NoWait() };
        var r = await svc.GenerateFirstFrameImageAsync("forest walk", "1280*720");
        Assert.True(r.Ok);
        Assert.True(r.IsLocalFallback);
        Assert.Equal((1280, 720), PngSize(r.Image!));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var h = new StubHandler(_ => { cts.Cancel(); return Json(HttpStatusCode.BadRequest, "{}"); });
        using var http = new HttpClient(h);
        var svc = new GeminiPromptService(http, () => "k") { Resilience = NoWait() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GenerateFirstFrameImageAsync("baby", "1280*720", cts.Token));
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData("16:9", 1280, 720)]
    [InlineData("9:16", 720, 1280)]
    [InlineData("1:1", 1024, 1024)]
    public void LocalPng_IsValid_Deterministic_AndAspectExact(string aspect, int w, int h)
    {
        var a = GeminiPromptService.RenderLocalCompositionPng("night city neon", aspect);
        var b = GeminiPromptService.RenderLocalCompositionPng("night city neon", aspect);
        Assert.Equal(a, b);
        Assert.Equal((w, h), PngSize(a));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, a[..8]);
        Assert.Equal("IEND", Encoding.ASCII.GetString(a, a.Length - 8, 4));
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, a[^4..]);   // 표준 IEND CRC32 → CRC 구현 검증
        Assert.NotEqual(a, GeminiPromptService.RenderLocalCompositionPng("forest morning", aspect));   // 테마 팔레트 반영
        AssertPixelDataInflates(a, w, h);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ImagenExtractor_ValidatesSchema()
    {
        Assert.Equal(Tiny, GeminiPromptService.ExtractImagenImage(ImagenBody(Tiny))!.Value.Data);
        Assert.Null(GeminiPromptService.ExtractImagenImage(null));
        Assert.Null(GeminiPromptService.ExtractImagenImage("{\"predictions\":[{\"mimeType\":\"text/plain\",\"bytesBase64Encoded\":\"AAAA\"}]}"));
        Assert.Null(GeminiPromptService.ExtractImagenImage("{\"predictions\":[{\"bytesBase64Encoded\":\"@@@\"}]}"));
        Assert.Null(GeminiPromptService.ExtractImagenImage("not json"));
        Assert.Contains("\"aspectRatio\":\"9:16\"", GeminiPromptService.BuildImagenPayload("x", "9:16"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void WorkspaceMarkup_CompactLayout_AndLocalFallbackGuard()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("<section class=\"ws-grid\" data-compact-layout=\"1\">", src, StringComparison.Ordinal);
        Assert.Contains("<div class=\"ws-empty\" data-canvas-compact=\"1\">", src, StringComparison.Ordinal);
        Assert.Contains(".ws-empty { flex: 0 0 auto;", src, StringComparison.Ordinal);          // 유휴 캔버스가 칼럼을 채우지 않음
        Assert.Contains("height: min(26vh, 216px)", src, StringComparison.Ordinal);            // 16:9 박스 밀착
        Assert.Contains(".ws-tune { margin-top: 0;", src, StringComparison.Ordinal);           // 튜너 하단 밀어내기 제거
        Assert.DoesNotContain(".ws-tune { margin-top: auto;", src, StringComparison.Ordinal);
        Assert.Contains(".ws-ta { width: 100%; min-height: 108px;", src, StringComparison.Ordinal);
        Assert.Contains(".ws-ffp-vp { height: min(20vh, 180px);", src, StringComparison.Ordinal);
        Assert.Contains("@if (_ffpPath is not null && !_ffpLocal)", src, StringComparison.Ordinal);   // 로컬 구도는 I2V 시작 프레임 바인딩 불가
        Assert.Contains("_ffpLocal = r.IsLocalFallback;", src, StringComparison.Ordinal);
        Assert.Contains("PlutoAutonomousActivityFeedService.Redact(r.Message)", src, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoInvariants_Hold()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    private static (int W, int H) PngSize(byte[] png)
        => (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    private static void AssertPixelDataInflates(byte[] png, int w, int h)
    {
        var pos = 8;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal(h * (w * 3 + 1), raw.Length);
    }
}
