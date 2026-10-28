using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI.Vision;
using PromptFlowStudio.Services.AI.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>v13.0.0: 비전 컨텍스트 분석기 - 축소/휴리스틱/무할당 샘플링/VLM 보강 순서(신호 기반, 벽시계 단언 없음).</summary>
[Collection("ProcessGcMode")]   // v16.9.0: 스레드 할당 0B 단언 — 병렬 GC 중 ArrayPool 트림 오염 차단
[Trait("Category", "Integration")]
public class VisionContextAnalyzerTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);

    private static (string map, string mutex) UniqueNames()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        return ("Local\\pfs_test_vis_map_" + tag, "Local\\pfs_test_vis_mtx_" + tag);
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var f = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { f[i * 4] = r; f[i * 4 + 1] = g; f[i * 4 + 2] = b; f[i * 4 + 3] = 255; }
        return f;
    }

    private sealed class GatedVlm : IVisionLanguageClient
    {
        public readonly TaskCompletionSource<VisionVlmReply?> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Enabled => true;
        public Task<VisionVlmReply?> DescribeAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Reply.Task;
        }
    }

    [Fact]
    public void Downscale_UniformAndCheckerFrames_BoxAverageIsExact()
    {
        var dst = new byte[VisionContextAnalyzer.MaxSide * VisionContextAnalyzer.MaxSide * 3];
        Assert.True(VisionContextAnalyzer.Downscale(Solid(448, 448, 10, 20, 30), 448, 448, VirtualCamPixelFormat.Rgba32, dst, out var w, out var h));
        Assert.Equal((224, 224), (w, h));
        for (var i = 0; i < w * h; i++) Assert.Equal((10, 20, 30), (dst[i * 3], dst[i * 3 + 1], dst[i * 3 + 2]));

        var checker = new byte[448 * 448 * 4];
        for (var y = 0; y < 448; y++)
            for (var x = 0; x < 448; x++)
            {
                var v = (byte)(((x + y) & 1) == 0 ? 0 : 254);
                var o = (y * 448 + x) * 4; checker[o] = checker[o + 1] = checker[o + 2] = v;
            }
        Assert.True(VisionContextAnalyzer.Downscale(checker, 448, 448, VirtualCamPixelFormat.Rgba32, dst, out _, out _));
        Assert.Equal(127, dst[0]);   // 2x2 블록 평균 (0+254+254+0)/4
    }

    [Fact]
    public void Downscale_Nv12_UsesLumaPlaneAsGray_AndSmallFrameIsNotUpscaled()
    {
        var nv12 = new byte[64 * 32 * 3 / 2];
        Array.Fill(nv12, (byte)100, 0, 64 * 32);
        var dst = new byte[VisionContextAnalyzer.MaxSide * VisionContextAnalyzer.MaxSide * 3];
        Assert.True(VisionContextAnalyzer.Downscale(nv12, 64, 32, VirtualCamPixelFormat.Nv12, dst, out var w, out var h));
        Assert.Equal((64, 32), (w, h));
        Assert.Equal((100, 100, 100), (dst[0], dst[1], dst[2]));
    }

    [Theory]
    [InlineData(20, 0, 0.0, 0, VisionScene.DarkScene)]
    [InlineData(170, 150, 0.8, 5, VisionScene.ExplosionFlash)]
    [InlineData(170, 150, 0.1, 5, VisionScene.Calm)]      // 밝아졌지만 난색이 아님 → 섬광 아님
    [InlineData(120, 2, 0.0, 60, VisionScene.HighAction)]
    [InlineData(120, 0, 0.0, 3, VisionScene.Calm)]
    public void Classify_DeterministicRules(double luma, double jump, double warm, double motion, VisionScene expected)
        => Assert.Equal(expected, VisionContextAnalyzer.Classify(luma, jump, warm, motion).Scene);

    [Fact]
    public void Tick_DarkThenFlashThenNoise_ClassifiesSequence_AndSkipsUnchangedFrame()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var analyzer = new VisionContextAnalyzer(pipe, vlm: null);

        pipe.SubmitFrame(Solid(320, 180, 8, 8, 12), 320, 180, VirtualCamPixelFormat.Rgba32);
        Assert.True(analyzer.Tick());
        Assert.Equal(VisionScene.DarkScene, analyzer.LastResult!.Value.Scene);
        Assert.Equal(VisionSource.Heuristic, analyzer.LastResult!.Value.Source);
        Assert.Equal((224, 126), analyzer.LastSampleSize);   // 긴 변 224 로 비율 유지 축소

        Assert.False(analyzer.Tick());   // 새 프레임 없음 → 작업 생략
        Assert.Equal(1, analyzer.SkippedNoNewFrame);

        pipe.SubmitFrame(Solid(320, 180, 255, 160, 40), 320, 180, VirtualCamPixelFormat.Rgba32);   // 주황 섬광
        Assert.True(analyzer.Tick());
        Assert.Equal(VisionScene.ExplosionFlash, analyzer.LastResult!.Value.Scene);

        var rng = new Random(3);
        var n1 = new byte[320 * 180 * 4]; rng.NextBytes(n1);
        var n2 = new byte[320 * 180 * 4]; rng.NextBytes(n2);
        pipe.SubmitFrame(n1, 320, 180, VirtualCamPixelFormat.Rgba32); analyzer.Tick();
        pipe.SubmitFrame(n2, 320, 180, VirtualCamPixelFormat.Rgba32); analyzer.Tick();
        Assert.Equal(VisionScene.HighAction, analyzer.LastResult!.Value.Scene);
        Assert.Equal(4, analyzer.Samples);
    }

    [Fact]
    public void SampleDownscaleHeuristic_HotPath_DoesNotAllocate()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var analyzer = new VisionContextAnalyzer(pipe, vlm: null);
        var a = Solid(640, 360, 30, 30, 30);
        var b = Solid(640, 360, 200, 120, 40);

        void Iteration(int i)
        {
            pipe.SubmitFrame((i & 1) == 0 ? a : b, 640, 360, VirtualCamPixelFormat.Rgba32);
            analyzer.Tick();
        }

        for (var i = 0; i < 20; i++) Iteration(i);   // 워밍업(수신 버퍼 대여·JIT)
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { for (var i = 0; i < 120; i++) Iteration(i); }));   // 1FPS × 2분 분량
        Assert.True(analyzer.Samples >= 140);
    }

    [Fact]
    public async Task VlmReply_ArrivesAfterImmediateHeuristic_AndConfirmsScene()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        var vlm = new GatedVlm();
        using var analyzer = new VisionContextAnalyzer(pipe, vlm);
        var sources = new System.Collections.Concurrent.ConcurrentQueue<VisionSource>();
        var vlmSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.OnScene += r => { sources.Enqueue(r.Source); if (r.Source == VisionSource.Vlm) vlmSeen.TrySetResult(); };

        pipe.SubmitFrame(Solid(160, 90, 120, 120, 120), 160, 90, VirtualCamPixelFormat.Rgba32);
        Assert.True(analyzer.Tick());
        Assert.Equal(new[] { VisionSource.Heuristic }, sources.ToArray());   // VLM 응답 전에 휴리스틱이 즉시 게시(폴백 지연 0)

        pipe.SubmitFrame(Solid(160, 90, 121, 121, 121), 160, 90, VirtualCamPixelFormat.Rgba32);
        analyzer.Tick();
        Assert.Equal(1, vlm.Calls);   // 진행 중인 VLM 요청이 있으면 새 샘플은 VLM 생략(동시 1건)

        vlm.Reply.SetResult(new VisionVlmReply(VisionScene.ExplosionFlash, 0.93, "big explosion"));
        await vlmSeen.Task.WaitAsync(SafetyNet);

        Assert.Equal(1, analyzer.VlmAccepted);
        Assert.Equal("big explosion", analyzer.LastVlmSummary);
    }
}

[Trait("Category", "Integration")]
public class VisionReactionBinderTests
{
    private sealed class FakeClock { public long Now = 1_000_000; public void AdvanceMs(double ms) => Now += (long)(ms * Stopwatch.Frequency / 1000.0); }

    private static (VisionReactionBinder binder, StreamerReactionEngine fx, FakeClock clk, VisionContextAnalyzer analyzer, LiveStreamVirtualCamPipe pipe) Build()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var pipe = new LiveStreamVirtualCamPipe("Local\\pfs_test_vb_map_" + tag, "Local\\pfs_test_vb_mtx_" + tag);
        var analyzer = new VisionContextAnalyzer(pipe, vlm: null);
        var fx = new StreamerReactionEngine(new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler()), new ReactionOverlayPipeline(), () => 0);
        var clk = new FakeClock();
        var binder = new VisionReactionBinder(analyzer, fx, new WanDynamicMotionController(), () => clk.Now);
        return (binder, fx, clk, analyzer, pipe);
    }

    private static VisionSceneResult R(VisionScene s, double c) => new(s, c, VisionSource.Vlm, 1, 100, 0);

    [Fact]
    public void Explosion_TriggersHypeImpulseAndPromptModifier_ThenCooldownSuppressesRepeat()
    {
        var (binder, fx, clk, analyzer, pipe) = Build();
        using var _p = pipe; using var _a = analyzer; using var _f = fx; using var _b = binder;

        Assert.True(binder.Apply(R(VisionScene.ExplosionFlash, 0.9)));
        Assert.Equal(1, fx.ImpulsesQueued);
        Assert.Equal(VisionScene.ExplosionFlash, binder.ActiveModifierScene);
        Assert.Contains("explosive burst", binder.ApplyPromptModulation("a castle at dusk"));

        Assert.False(binder.Apply(R(VisionScene.ExplosionFlash, 0.9)));   // 1FPS 연속 분류 → 쿨다운 억제
        Assert.Equal(1, binder.SuppressedByCooldown);
        clk.AdvanceMs(VisionReactionBinder.CooldownMs + 1);
        Assert.True(binder.Apply(R(VisionScene.ExplosionFlash, 0.9)));
        Assert.Equal(2, fx.ImpulsesQueued);
    }

    [Fact]
    public void LowConfidenceOrCalm_IsIgnored_AndModifierExpiresAfterTtl()
    {
        var (binder, fx, clk, analyzer, pipe) = Build();
        using var _p = pipe; using var _a = analyzer; using var _f = fx; using var _b = binder;

        Assert.False(binder.Apply(R(VisionScene.ExplosionFlash, 0.3)));
        Assert.Equal(1, binder.IgnoredLowConfidence);
        Assert.False(binder.Apply(R(VisionScene.Calm, 0.99)));
        Assert.Equal(0, fx.ImpulsesQueued);

        Assert.True(binder.Apply(R(VisionScene.DarkScene, 0.8)));
        Assert.Equal(0, fx.ImpulsesQueued);   // 어두운 장면은 파티클 분출 없이 모션·프롬프트만
        Assert.NotNull(binder.CurrentModifier);
        clk.AdvanceMs(VisionReactionBinder.ModifierTtlMs + 1);
        Assert.Null(binder.CurrentModifier);
        Assert.Equal("prompt", binder.ApplyPromptModulation("prompt"));
    }

    [Fact]
    public void AnalyzerOnScene_IsWiredToBinder_EndToEnd()
    {
        var (binder, fx, _, analyzer, pipe) = Build();
        using var _p = pipe; using var _a = analyzer; using var _f = fx; using var _b = binder;
        pipe.SubmitFrame(new byte[64 * 36 * 4], 64, 36, VirtualCamPixelFormat.Rgba32);   // 검정 = 어두운 장면
        analyzer.Tick();
        Assert.Equal(1, binder.Reactions);
        Assert.Equal(VisionScene.DarkScene, binder.ActiveModifierScene);
    }

    [Fact]
    public void Modulate_IsIdempotent_AndRemovesStaleSegment()
    {
        const string mod = " [vision-ctx]x[/vision-ctx]";
        var once = VisionReactionBinder.Modulate("cat", mod);
        Assert.Equal("cat [vision-ctx]x[/vision-ctx]", once);
        Assert.Equal(once, VisionReactionBinder.Modulate(once, mod));
        Assert.Equal("cat", VisionReactionBinder.Modulate(once, null));
        var original = "dog";
        Assert.Same(original, VisionReactionBinder.Modulate(original, null));   // 변조 없음 = 원본 그대로(할당 0)
    }
}

[Trait("Category", "Integration")]
public class LocalVlmHttpClientTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static VlmClientOptions Opt(int port, int attempts = 2, int timeoutMs = 3000, VlmApiFlavor flavor = VlmApiFlavor.Ollama)
        => new(new Uri($"http://127.0.0.1:{port}"), "llava", flavor, null, TimeSpan.FromMilliseconds(timeoutMs), attempts, false);

    // 요청마다 respond(n) 이 (상태코드, 본문, 지연ms) 반환.
    private static async Task RunStubAsync(HttpListener listener, Func<int, (int Status, string Body, int DelayMs)> respond, CancellationToken ct)
    {
        var n = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(ct);
                using (var r = new StreamReader(ctx.Request.InputStream)) await r.ReadToEndAsync(ct);
                var (status, body, delay) = respond(++n);
                if (delay > 0) { try { await Task.Delay(delay, ct); } catch { } }
                ctx.Response.StatusCode = status;
                var bytes = Encoding.UTF8.GetBytes(body);
                try { await ctx.Response.OutputStream.WriteAsync(bytes, ct); ctx.Response.Close(); } catch { /* 클라이언트가 먼저 끊음 */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    private static HttpListener StartListener(int port)
    {
        var l = new HttpListener();
        l.Prefixes.Add($"http://127.0.0.1:{port}/");
        l.Start();
        return l;
    }

    private const string OllamaExplosion = "{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"scene\\\":\\\"explosion\\\",\\\"confidence\\\":0.9,\\\"summary\\\":\\\"fireball\\\"}\"}}";

    [Fact]
    public async Task Transient500_ThenSuccess_RetriesWithBackoffAndParsesReply()
    {
        var port = FreeTcpPort();
        using var listener = StartListener(port);
        using var cts = new CancellationTokenSource(SafetyNet);
        var server = RunStubAsync(listener, n => n == 1 ? (500, "boom", 0) : (200, OllamaExplosion, 0), cts.Token);
        using var client = new LocalVlmHttpClient(Opt(port));

        var reply = await client.DescribeAsync(new byte[4 * 4 * 3], 4, 4, cts.Token);

        Assert.NotNull(reply);
        Assert.Equal(VisionScene.ExplosionFlash, reply!.Value.Scene);
        Assert.Equal(0.9, reply.Value.Confidence, 3);
        Assert.Equal("fireball", reply.Value.Summary);
        Assert.Equal(2, client.Requests);
        cts.Cancel(); listener.Stop();
        await server;
    }

    [Fact]
    public async Task SchemaMismatch_ReturnsNullForHeuristicFallback()
    {
        var port = FreeTcpPort();
        using var listener = StartListener(port);
        using var cts = new CancellationTokenSource(SafetyNet);
        var server = RunStubAsync(listener, _ => (200, "{\"message\":{\"content\":\"I think it is a cat\"}}", 0), cts.Token);
        using var client = new LocalVlmHttpClient(Opt(port));

        Assert.Null(await client.DescribeAsync(new byte[2 * 2 * 3], 2, 2, cts.Token));
        Assert.Equal(1, client.SchemaRejects);
        cts.Cancel(); listener.Stop();
        await server;
    }

    [Fact]
    public async Task Timeout_ReturnsNull_AndRepeatedFailuresOpenCircuitWithoutFurtherRequests()
    {
        var port = FreeTcpPort();
        using var listener = StartListener(port);
        using var cts = new CancellationTokenSource(SafetyNet);
        var server = RunStubAsync(listener, _ => (200, OllamaExplosion, 5000), cts.Token);
        using var client = new LocalVlmHttpClient(Opt(port, attempts: 1, timeoutMs: 150));

        for (var i = 0; i < 3; i++) Assert.Null(await client.DescribeAsync(new byte[2 * 2 * 3], 2, 2, cts.Token));
        Assert.True(client.CircuitOpen);
        Assert.Equal(3, client.Requests);
        Assert.Null(await client.DescribeAsync(new byte[2 * 2 * 3], 2, 2, cts.Token));
        Assert.Equal(3, client.Requests);   // 서킷 개방 중엔 네트워크 호출 자체를 하지 않음
        cts.Cancel(); listener.Stop();
        await server;
    }

    [Fact]
    public void RemoteEndpoint_WithoutExplicitOptIn_IsDisabled()
    {
        using var remote = new LocalVlmHttpClient(new VlmClientOptions(new Uri("http://10.20.30.40:11434"), "llava", VlmApiFlavor.Ollama, null, TimeSpan.FromSeconds(1), 1, AllowRemote: false));
        Assert.False(remote.Enabled);
        using var optedIn = new LocalVlmHttpClient(new VlmClientOptions(new Uri("http://10.20.30.40:11434"), "llava", VlmApiFlavor.Ollama, null, TimeSpan.FromSeconds(1), 1, AllowRemote: true));
        Assert.True(optedIn.Enabled);
        using var local = new LocalVlmHttpClient(new VlmClientOptions(new Uri("http://localhost:1234"), "m", VlmApiFlavor.OpenAiCompatible, null, TimeSpan.FromSeconds(1), 1, false));
        Assert.True(local.Enabled);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"scene\\\":\\\"dark\\\",\\\"confidence\\\":1.7}\\n```\"}}]}", VisionScene.DarkScene, 1.0)]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"scene\\\":\\\"fast action fight\\\"}\"}}]}", VisionScene.HighAction, 0.5)]
    public void ParseEnvelope_OpenAiCompatible_ToleratesFencesAndClampsConfidence(string json, VisionScene scene, double conf)
    {
        var r = LocalVlmHttpClient.ParseEnvelope(json, VlmApiFlavor.OpenAiCompatible);
        Assert.NotNull(r);
        Assert.Equal(scene, r!.Value.Scene);
        Assert.Equal(conf, r.Value.Confidence, 3);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"message\":{\"content\":\"{\\\"confidence\\\":0.9}\"}}")]
    [InlineData("{\"unexpected\":true}")]
    public void ParseEnvelope_InvalidSchema_IsNull(string json)
        => Assert.Null(LocalVlmHttpClient.ParseEnvelope(json, VlmApiFlavor.Ollama));

    [Fact]
    public void PngEncoder_ProducesStructurallyValidPng_WithCorrectCrcAndPixels()
    {
        var rgb = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };   // 3x2
        var png = PngRgbEncoder.Encode(rgb, 3, 2);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        var pos = 8;
        byte[]? idat = null;
        var types = new System.Collections.Generic.List<string>();
        while (pos < png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = png.AsSpan(pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(PngRgbEncoder.Crc32(PngRgbEncoder.Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu, crc);
            var t = Encoding.ASCII.GetString(type);
            types.Add(t);
            if (t == "IHDR") { Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(data)); Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(data[4..])); Assert.Equal(2, data[9]); }
            if (t == "IDAT") idat = data.ToArray();
            pos += 12 + len;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types.ToArray());

        using var z = new ZLibStream(new MemoryStream(idat!), CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        var raw = outMs.ToArray();
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 10, 11, 12, 13, 14, 15, 16, 17, 18 }, raw);   // 행마다 필터 0 + RGB
    }

    [Fact]
    public void PngCrc32_MatchesKnownVector()
        => Assert.Equal(0xCBF43926u, PngRgbEncoder.Crc32(0xFFFFFFFFu, "123456789"u8) ^ 0xFFFFFFFFu);
}
