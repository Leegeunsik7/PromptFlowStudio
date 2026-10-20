using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI.Vision;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>v13.1.0: VLM 백프레셔(즉시 드롭)·5초 데드라인·E2E 지연·오프라인 폴백 - HttpMessageHandler 모의로 결정적 재현.</summary>
public class VisionIntelligenceTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);

    private const string OllamaExplosion = "{\"message\":{\"content\":\"{\\\"scene\\\":\\\"explosion\\\",\\\"confidence\\\":0.9,\\\"summary\\\":\\\"fireball\\\"}\"}}";

    private sealed class ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        public readonly ConcurrentQueue<long> CallTimestamps = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallTimestamps.Enqueue(Stopwatch.GetTimestamp());
            return respond(Interlocked.Increment(ref Calls), ct);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LocalVlmHttpClient Client(HttpMessageHandler handler, int attempts = 1)
        => new(new VlmClientOptions(new Uri("http://127.0.0.1:11434"), "llava", VlmApiFlavor.Ollama, null, TimeSpan.FromSeconds(30), attempts, AllowRemote: false),
               new HttpClient(handler));

    private static LiveStreamVirtualCamPipe Pipe()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        return new LiveStreamVirtualCamPipe("Local\\pfs_test_vi_map_" + tag, "Local\\pfs_test_vi_mtx_" + tag);
    }

    private static byte[] Solid(int w, int h, byte v)
    {
        var f = new byte[w * h * 4];
        Array.Fill(f, v);
        return f;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + SafetyNet;   // 안전망(단언 아님) - 조건 충족 즉시 탈출
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건 대기 초과");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task SlowVlm_DropsSamplesImmediatelyWithoutQueue_AtZeroAllocation_ThenAcceptsAndMeasuresE2E()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Ok(OllamaExplosion); });
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, Client(handler));
        var a = Solid(160, 90, 90); var b = Solid(160, 90, 140);

        pipe.SubmitFrame(a, 160, 90, VirtualCamPixelFormat.Rgba32);
        Assert.True(analyzer.Tick());
        await entered.Task.WaitAsync(SafetyNet);
        Assert.Equal(VlmHealth.InFlight, analyzer.VlmHealthState);

        for (var i = 0; i < 4; i++) { pipe.SubmitFrame((i & 1) == 0 ? b : a, 160, 90, VirtualCamPixelFormat.Rgba32); analyzer.Tick(); }
        Assert.Equal(4, analyzer.VlmDropped);      // 처리 중이던 동안의 샘플은 전부 즉시 드롭
        Assert.Equal(1, handler.Calls);            // 대기열 없음 - 서버로 간 요청은 여전히 1건

        // 드롭 경로 무할당: 추론이 걸려 있는 동안 샘플→축소→휴리스틱→드롭 반복
        var n = 0;
        var allocated = AllocationProbe.MinAllocated(() =>
        {
            for (var i = 0; i < 50; i++) { pipe.SubmitFrame((n++ & 1) == 0 ? a : b, 160, 90, VirtualCamPixelFormat.Rgba32); analyzer.Tick(); }
        });
        Assert.Equal(0, allocated);
        Assert.True(analyzer.VlmDropped >= 54);
        Assert.Equal(1, handler.Calls);

        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.OnScene += r => { if (r.Source == VisionSource.Vlm) accepted.TrySetResult(); };
        release.SetResult();
        await accepted.Task.WaitAsync(SafetyNet);
        await Until(() => analyzer.VlmHealthState != VlmHealth.InFlight);

        Assert.Equal(1, analyzer.VlmAccepted);
        Assert.True(analyzer.LastVlmLatencyMs > 0);
        Assert.True(analyzer.MaxVlmLatencyMs >= analyzer.LastVlmLatencyMs);
        Assert.Equal(VlmHealth.Online, analyzer.VlmHealthState);
        Assert.Equal("fireball", analyzer.LastVlmSummary);

        pipe.SubmitFrame(a, 160, 90, VirtualCamPixelFormat.Rgba32); analyzer.Tick();   // 슬롯이 비었으니 다시 요청
        await Until(() => handler.Calls == 2);
    }

    [Fact]
    public async Task HungVlm_DeadlineCancelsRequest_ReleasesInFlightSlot_AndNextSampleRetries()
    {
        var sawCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancel.TrySetResult(); throw; }
            return Ok(OllamaExplosion);
        });
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, Client(handler), vlmDeadline: TimeSpan.FromMilliseconds(150));

        pipe.SubmitFrame(Solid(64, 36, 100), 64, 36, VirtualCamPixelFormat.Rgba32);
        analyzer.Tick();
        await sawCancel.Task.WaitAsync(SafetyNet);                 // 데드라인이 서버 요청까지 실제로 취소(소켓/타이머 잔류 없음)
        await Until(() => analyzer.VlmTimeouts == 1 && analyzer.VlmHealthState != VlmHealth.InFlight);

        Assert.Equal(VlmHealth.Degraded, analyzer.VlmHealthState);
        Assert.Equal(1, analyzer.VlmFallbacks);
        Assert.Equal(VisionSource.Heuristic, analyzer.LastResult!.Value.Source);   // 휴리스틱 결과 유지

        pipe.SubmitFrame(Solid(64, 36, 101), 64, 36, VirtualCamPixelFormat.Rgba32);
        analyzer.Tick();
        await Until(() => handler.Calls == 2);   // 슬롯 해제 → 다음 샘플 재시도
    }

    [Fact]
    public async Task OfflineEndpoint_ExponentialBackoff_OpensCircuit_SkipsNetwork_AndHudShowsOffline()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("대상 컴퓨터에서 연결을 거부했으므로 연결하지 못했습니다. (127.0.0.1:11434)"));
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, Client(handler, attempts: 2));

        for (byte i = 0; i < 3; i++)
        {
            pipe.SubmitFrame(Solid(64, 36, (byte)(60 + i)), 64, 36, VirtualCamPixelFormat.Rgba32);
            analyzer.Tick();
            var expected = (i + 1) * 2;
            await Until(() => handler.Calls == expected && analyzer.VlmHealthState != VlmHealth.InFlight);
        }

        // 요청 1건 = 시도 2회, 사이 200ms 지수 백오프(하한만 확인 - 지연은 늘어날 수는 있어도 줄지 않는다)
        var ts = handler.CallTimestamps.ToArray();
        var gapMs = (ts[1] - ts[0]) * 1000.0 / Stopwatch.Frequency;
        Assert.True(gapMs >= 150, $"백오프 간격 {gapMs:0}ms");

        var vlm = (LocalVlmHttpClient)analyzer.Vlm!;
        Assert.True(vlm.CircuitOpen);                               // 연속 3회 실패 → 서킷 개방
        Assert.Equal(VlmHealth.Offline, analyzer.VlmHealthState);   // HUD "VLM Offline"
        Assert.Equal(3, analyzer.VlmFallbacks);

        pipe.SubmitFrame(Solid(64, 36, 99), 64, 36, VirtualCamPixelFormat.Rgba32);
        Assert.True(analyzer.Tick());                               // 휴리스틱은 계속 동작
        Assert.Equal(1, analyzer.VlmSkippedOffline);
        Assert.Equal(6, handler.Calls);                             // 서킷 개방 중엔 네트워크 시도 0
    }

    [Fact]
    public async Task RealSocket_ConnectionRefused_FallsBackQuicklyAndReportsOffline()
    {
        // 실 소켓 경로: 방금 비운 포트(리스너 없음)로 접속 → 연결 거부 → 휴리스틱 폴백(모의 핸들러 아님)
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        using var client = new LocalVlmHttpClient(new VlmClientOptions(new Uri($"http://127.0.0.1:{port}"), "llava", VlmApiFlavor.Ollama, null, TimeSpan.FromSeconds(3), 2, false));
        var reply = await client.DescribeAsync(new byte[4 * 4 * 3], 4, 4, CancellationToken.None).WaitAsync(SafetyNet);

        Assert.Null(reply);
        Assert.True(client.LastCallFailed);
        Assert.Equal(2, client.Requests);   // 1회 재시도(백오프) 후 포기
        Assert.Equal(1, client.Failures);
    }

    // ── v14.3.0 가변 VLM 샘플링 ──

    [Fact]
    public void AdaptiveFrameSampler_DynamicScale_OnLatencySpike()
    {
        var s = new AdaptiveFrameSampler();
        Assert.Equal(AdaptiveFrameSampler.DefaultIntervalMs, s.IntervalMs);

        for (var i = 0; i < 20; i++) s.RecordLatency(50);                 // 빠른 VLM → 10 FPS 상한
        Assert.Equal(AdaptiveFrameSampler.MinIntervalMs, s.IntervalMs);

        for (var i = 0; i < 10; i++) s.RecordLatency(1200);               // 지연 스파이크 → p95 추종 완화
        Assert.InRange(s.IntervalMs, 1200, AdaptiveFrameSampler.MaxIntervalMs);
        var afterSpike = s.IntervalMs;

        Assert.Equal(AdaptiveFrameSampler.MaxIntervalMs, s.RecordTimeout());   // 타임아웃 1회 = 즉시 2초(GPU 보호)
        Assert.Equal(AdaptiveFrameSampler.MaxIntervalMs, s.IntervalMs);
        Assert.True(s.ScaleChanges >= 3);
        Assert.True(afterSpike <= AdaptiveFrameSampler.MaxIntervalMs);

        s.Reset();
        for (var i = 0; i < 80; i++) s.RecordLatency(60);                 // 창 전체가 빠른 응답으로 교체 → 회복
        Assert.Equal(AdaptiveFrameSampler.MinIntervalMs, s.IntervalMs);
        Assert.Equal(AdaptiveFrameSampler.MaxIntervalMs, AdaptiveFrameSampler.ComputeIntervalMs(0, 0, AdaptiveFrameSampler.TimeoutRateThreshold));
        Assert.Equal(AdaptiveFrameSampler.MinIntervalMs, AdaptiveFrameSampler.ComputeIntervalMs(float.NaN, 0, 0));
    }

    [Fact]
    public void AdaptiveFrameSampler_LatencyDistribution_PercentilesHistogramEwma_ZeroAlloc()
    {
        ReadOnlySpan<float> seq = stackalloc float[] { 100, 200, 300 };
        Assert.Equal(100f, AdaptiveFrameSampler.Ewma(seq.Slice(0, 1), 0.5f, 0));
        Assert.Equal(225f, AdaptiveFrameSampler.Ewma(seq, 0.5f, 0), 3);   // 100 → 150 → 225

        var s = new AdaptiveFrameSampler();
        for (var i = 1; i <= 100; i++) s.RecordLatency(i * 10);           // 창 64 = 370..1000ms
        var st = s.Snapshot();
        Assert.Equal(AdaptiveFrameSampler.WindowSize, st.Count);
        Assert.Equal(680f, st.P50Ms);
        Assert.Equal(940f, st.P90Ms);
        Assert.Equal(1000f, st.P99Ms);
        Assert.True(st.P50Ms <= st.P90Ms && st.P90Ms <= st.P95Ms && st.P95Ms <= st.P99Ms);

        var hist = new int[AdaptiveFrameSampler.BucketCount];
        var max = s.CopyHistogram(hist);
        Assert.Equal(AdaptiveFrameSampler.WindowSize, hist.Sum());
        Assert.Equal(hist.Max(), max);
        Assert.Equal(0, hist[0] + hist[1] + hist[2]);                     // ≤300ms 없음

        void Hot() { s.RecordLatency(420); s.RecordTimeout(); _ = s.Snapshot(); _ = s.CopyHistogram(hist); }
        for (var i = 0; i < 10; i++) Hot();   // 워밍업(JIT)
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { for (var i = 0; i < 500; i++) Hot(); }));
    }

    [Fact]
    public async Task VisionContextAnalyzer_VlmTimeout_RelaxesAdaptiveSamplerToMaxInterval()
    {
        var handler = new ScriptedHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(OllamaExplosion); });
        using var pipe = Pipe();
        using var analyzer = new VisionContextAnalyzer(pipe, Client(handler), vlmDeadline: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 10; i++) analyzer.Sampler.RecordLatency(40);
        Assert.Equal(AdaptiveFrameSampler.MinIntervalMs, analyzer.Sampler.IntervalMs);

        pipe.SubmitFrame(Solid(64, 36, 100), 64, 36, VirtualCamPixelFormat.Rgba32);
        analyzer.Tick();
        await Until(() => analyzer.VlmTimeouts == 1 && analyzer.VlmHealthState != VlmHealth.InFlight);

        Assert.Equal(1, analyzer.Sampler.Timeouts);
        Assert.Equal(AdaptiveFrameSampler.MaxIntervalMs, analyzer.Sampler.IntervalMs);
    }
}

/// <summary>v13.2.0: Ollama 비전 모델 프로빙(/api/tags + /api/show capabilities) · 자동 모델 적용 · 합성 벤치마크 - 모의 HTTP 로 결정적 검증.</summary>
public class VisionModelProbeEngineTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Bodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Enqueue(request.RequestUri!.AbsolutePath + " " + body);
            return await route(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static VlmClientOptions Opt() => new(new Uri("http://127.0.0.1:11434"), "llava", VlmApiFlavor.Ollama, null, TimeSpan.FromSeconds(5), 1, false);

    private static string Tags(params string[] names) => "{\"models\":[" + string.Join(",", names.Select(n => "{\"name\":\"" + n + "\",\"details\":{}}")) + "]}";

    private static RouteHandler Ollama(string[] models, Func<string, string[]?> caps)
        => new((req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/tags") return Task.FromResult(Json(Tags(models)));
            if (path == "/api/show")
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
                var c = caps(name);
                return Task.FromResult(c is null ? Json("{\"error\":\"not found\"}", HttpStatusCode.NotFound)
                    : Json("{\"capabilities\":[" + string.Join(",", c.Select(x => "\"" + x + "\"")) + "]}"));
            }
            if (path == "/api/chat") return Task.FromResult(Json("{\"message\":{\"content\":\"{\\\"scene\\\":\\\"calm\\\",\\\"confidence\\\":0.8}\"}}"));
            return Task.FromResult(Json("{}", HttpStatusCode.NotFound));
        });

    [Fact]
    public async Task TextOnlyModels_ReportNoVisionModel_WithPullGuide()
    {
        // 이 PC 의 실제 설치 구성(gemma2 / qwen2.5 계열 = 텍스트 전용)을 재현
        var h = Ollama(new[] { "gemma2:2b", "qwen2.5:3b", "qwen2.5:7b", "qwen2.5:latest" }, _ => new[] { "completion" });
        using var probe = new VisionModelProbeEngine(Opt(), new HttpClient(h));
        var r = await probe.ProbeAsync();
        Assert.Equal(VisionProbeStatus.NoVisionModel, r.Status);
        Assert.Null(r.SelectedModel);
        Assert.Equal(4, r.Models.Count);
        Assert.All(r.Models, m => Assert.False(m.IsVision));
        Assert.Equal(VisionModelProbeEngine.NoVisionModelGuide, probe.Badge);
        Assert.Equal("⚠️ No Vision Model Pulled (ollama pull qwen2.5vl:3b)", probe.Badge);
    }

    [Fact]
    public async Task VisionCapability_IsDetected_AndPreferredFamilyWins()
    {
        var h = Ollama(new[] { "llava:7b", "gemma2:2b", "qwen2.5vl:3b" }, n => n.StartsWith("gemma2") ? new[] { "completion" } : new[] { "completion", "vision" });
        using var probe = new VisionModelProbeEngine(Opt(), new HttpClient(h));
        var r = await probe.ProbeAsync();
        Assert.Equal(VisionProbeStatus.Ready, r.Status);
        Assert.Equal("qwen2.5vl:3b", r.SelectedModel);   // 선호 순위: qwen2.5vl > llava
        Assert.Contains(r.Models, m => m.Name == "llava:7b" && m.IsVision && m.Evidence.StartsWith("capabilities"));
        Assert.StartsWith("🟢", probe.Badge);
    }

    [Fact]
    public async Task OllamaWithoutCapabilities_FallsBackToNameHeuristic()
    {
        var h = Ollama(new[] { "qwen2.5:7b", "llava:13b" }, _ => null);   // /api/show 404 = 구버전(capabilities 미제공)
        using var probe = new VisionModelProbeEngine(Opt(), new HttpClient(h));
        var r = await probe.ProbeAsync();
        Assert.Equal("llava:13b", r.SelectedModel);
        Assert.Contains(r.Models, m => m.Name == "qwen2.5:7b" && !m.IsVision);
        Assert.All(r.Models, m => Assert.Contains("휴리스틱", m.Evidence));
    }

    [Fact]
    public async Task ServerOffline_And_InvalidSchema_AreReportedWithoutThrowing()
    {
        using var off = new VisionModelProbeEngine(Opt(), new HttpClient(new RouteHandler((_, _) => throw new HttpRequestException("연결 거부"))));
        Assert.Equal(VisionProbeStatus.ServerOffline, (await off.ProbeAsync()).Status);
        Assert.Equal(VisionModelProbeEngine.ServerOfflineGuide, off.Badge);

        using var bad = new VisionModelProbeEngine(Opt(), new HttpClient(new RouteHandler((_, _) => Task.FromResult(Json("{\"unexpected\":1}")))));
        Assert.Equal(VisionProbeStatus.InvalidResponse, (await bad.ProbeAsync()).Status);

        using var remote = new VisionModelProbeEngine(new VlmClientOptions(new Uri("http://10.0.0.5:11434"), "llava", VlmApiFlavor.Ollama, null, TimeSpan.FromSeconds(1), 1, AllowRemote: false), null);
        var rr = await remote.ProbeAsync();   // 비루프백 + 동의 없음 → 네트워크 호출 없이 거부
        Assert.Equal(VisionProbeStatus.ServerOffline, rr.Status);
        Assert.Contains("PFS_VLM_ALLOW_REMOTE", rr.Message);
    }

    [Fact]
    public async Task ApplyTo_SwitchesDefaultModel_ToDetected_ButRespectsExplicitConfig_AndRequestsUseIt()
    {
        var h = Ollama(new[] { "qwen2.5vl:3b" }, _ => new[] { "vision" });
        using var probe = new VisionModelProbeEngine(Opt(), new HttpClient(h));
        await probe.ProbeAsync();

        using var auto = new LocalVlmHttpClient(Opt(), new HttpClient(h));
        Assert.Equal("llava", auto.Model);   // 설정 기본값(이 PC 에 미설치)
        Assert.True(probe.ApplyTo(auto));
        Assert.Equal("qwen2.5vl:3b", auto.Model);
        Assert.NotNull(await auto.DescribeAsync(new byte[4 * 4 * 3], 4, 4, CancellationToken.None));
        Assert.Contains(h.Bodies, b => b.StartsWith("/api/chat") && b.Contains("\"model\":\"qwen2.5vl:3b\""));   // 실제 요청 본문에 반영

        using var pinned = new LocalVlmHttpClient(Opt(), new HttpClient(h)) { ModelExplicitlyConfigured = true };
        Assert.False(probe.ApplyTo(pinned));   // PFS_VLM_MODEL 명시값은 덮어쓰지 않음
        Assert.Equal("llava", pinned.Model);
    }

    private sealed class FixedVlm(bool ok, TaskCompletionSource? gate = null) : IVisionLanguageClient
    {
        public bool Enabled => true;
        public async Task<VisionVlmReply?> DescribeAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct)
        {
            if (gate is not null) await gate.Task.WaitAsync(ct);
            else await Task.Yield();
            return ok ? new VisionVlmReply(VisionScene.Calm, 0.7, null) : null;
        }
    }

    [Fact]
    public async Task SyntheticBenchmark_ReportsLatencyDistributionAndFps_AndRejectsConcurrentRun()
    {
        using var probe = new VisionModelProbeEngine(Opt(), new HttpClient(new RouteHandler((_, _) => Task.FromResult(Json("{}")))));
        var r = await probe.RunSyntheticBenchmarkAsync(new FixedVlm(true), frames: 5);
        Assert.NotNull(r);
        Assert.Equal(5, r!.Succeeded);
        Assert.Equal(0, r.Failed);
        Assert.True(r.P95Ms >= r.P50Ms);
        Assert.True(r.Fps > 0);
        Assert.Same(r, probe.LastBenchmark);

        var failing = await probe.RunSyntheticBenchmarkAsync(new FixedVlm(false), frames: 3);
        Assert.Equal(0, failing!.Succeeded);
        Assert.Equal(3, failing.Failed);
        Assert.Equal(0, failing.Fps);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = probe.RunSyntheticBenchmarkAsync(new FixedVlm(true, gate), frames: 1);
        Assert.True(probe.Busy);
        Assert.Null(await probe.RunSyntheticBenchmarkAsync(new FixedVlm(true), frames: 1));   // 동시 실행 1건 - 즉시 거부
        gate.SetResult();
        Assert.NotNull(await running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(probe.Busy);
    }

    [Fact]
    public void SyntheticFrames_DifferPerFrame_AndContainFlashPattern()
    {
        var a = new byte[64 * 36 * 3]; var b = new byte[64 * 36 * 3];
        VisionModelProbeEngine.FillSynthetic(a, 64, 36, 0);
        VisionModelProbeEngine.FillSynthetic(b, 64, 36, 1);
        Assert.NotEqual(a, b);
        var center = (18 * 64 + 32) * 3;
        Assert.Equal((255, 150, 30), (a[center], a[center + 1], a[center + 2]));   // 짝수 프레임 = 주황 섬광
    }

    [Theory]
    [InlineData("qwen2.5vl:3b", true)]
    [InlineData("qwen2.5:7b", false)]
    [InlineData("qwen2.5:latest", false)]
    [InlineData("gemma2:2b", false)]
    [InlineData("gemma3:4b", true)]
    [InlineData("gemma3:1b", false)]
    [InlineData("llava:13b", true)]
    [InlineData("bakllava:latest", true)]
    [InlineData("llama3.2-vision:11b", true)]
    [InlineData("moondream:latest", true)]
    [InlineData("minicpm-v:8b", true)]
    [InlineData("llama3.1:8b", false)]
    public void NameHeuristic_SeparatesVisionFromTextOnly(string name, bool vision)
        => Assert.Equal(vision, VisionModelProbeEngine.LooksLikeVisionName(name));
}

/// <summary>v13.3.0: VLM 요약문 → 액션 매퍼 - Span 파싱 0-Alloc · 모션/스타일 LoRA 스왑 · 원자 쿨다운 · 장면 바인더와의 중복 방지 · UI 컨텍스트 비점유.</summary>
[Collection("ProcessGcMode")]   // v16.9.0: 스레드 할당 0B 단언 — 병렬 GC 중 ArrayPool 트림 오염 차단
public class VisionActionMapperTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);
    private sealed class Clock { public long Now = 1_000_000; }

    private static (VisionActionMapper map, WanDynamicMotionController motion, LoRAHotSwapManager lora, Clock clk) Build()
    {
        var motion = new WanDynamicMotionController();
        var lora = new LoRAHotSwapManager(_ => Task.CompletedTask);   // 병합 스텁 즉시 완료(결정적)
        var clk = new Clock();
        return (new VisionActionMapper(motion, lora, () => clk.Now, VisionActionMapper.DefaultCooldownMs), motion, lora, clk);
    }

    [Fact]
    public void VisionActionMapper_ParseSpan_DoesNotAllocateMemory()
    {
        string[] samples =
        {
            "A massive explosion rocks the arena", "GAME OVER screen with red text", "neon cyberpunk street at night",
            "calm talking head in a studio", "빠른 질주 장면", "players celebrating victory with confetti",
        };
        var sink = 0;
        foreach (var s in samples) sink += (int)VisionActionMapper.Parse(s).Motion;   // 워밍업
        var allocated = AllocationProbe.MinAllocated(() =>
        {
            for (var i = 0; i < 20_000; i++) sink += (int)VisionActionMapper.Parse(samples[i % samples.Length].AsSpan()).Style;
        });
        Assert.Equal(0, allocated);
        Assert.True(sink > 0);
    }

    [Theory]
    [InlineData("A massive EXPLOSION rocks the arena", VisionAction.MotionSpike, VisionAction.None)]
    [InlineData("two players in close combat", VisionAction.MotionSpike, VisionAction.None)]
    [InlineData("GAME OVER screen", VisionAction.GameOver, VisionAction.None)]
    [InlineData("team celebration with confetti", VisionAction.Celebration, VisionAction.None)]
    [InlineData("fast motion racing through turns", VisionAction.SpeedRamp, VisionAction.None)]
    [InlineData("a dark room", VisionAction.None, VisionAction.StyleNoir)]
    [InlineData("explosion in a dark cyberpunk city", VisionAction.MotionSpike, VisionAction.StyleCyberpunk)]   // 구체 스타일 우선
    [InlineData("fantasy castle with a dragon", VisionAction.None, VisionAction.StyleFantasy)]
    [InlineData("건물 폭발, 어두운 밤거리", VisionAction.MotionSpike, VisionAction.StyleNoir)]
    [InlineData("calm talking head", VisionAction.None, VisionAction.None)]
    [InlineData("", VisionAction.None, VisionAction.None)]
    public void Parse_MapsKeywordsToActions(string text, VisionAction motion, VisionAction style)
    {
        var m = VisionActionMapper.Parse(text);
        Assert.Equal(motion, m.Motion);
        Assert.Equal(style, m.Style);
    }

    [Fact]
    public async Task Explosion_InjectsZoomShakeSpike_AndCooldownBlocksRepeat_ThenRecovers()
    {
        var (map, motion, _, clk) = Build();
        var r1 = await map.DispatchActionAsync("huge explosion on stage", VisionScene.Calm);
        Assert.True(r1.MotionInjected);
        Assert.Equal(1, motion.Injected);
        Assert.Equal(ZoomDirection.In, motion.LastApplied!.Zoom);
        Assert.Equal(0.9, motion.LastApplied.ZoomMagnitude, 3);
        Assert.Equal(8, motion.LastApplied.RollDegrees, 3);

        var r2 = await map.DispatchActionAsync("another explosion", VisionScene.Calm);   // 1FPS 연속 → 3초 창 안
        Assert.True(r2.MotionSuppressed);
        Assert.Equal(1, motion.Injected);
        Assert.Equal(1, map.SuppressedByCooldown);
        Assert.True(map.CooldownRemainingMs(VisionAction.MotionSpike) > 0);

        clk.Now += VisionActionMapper.DefaultCooldownMs;   // 쿨다운 경과
        Assert.Equal(0, map.CooldownRemainingMs(VisionAction.MotionSpike));
        Assert.True((await map.DispatchActionAsync("explosion again", VisionScene.Calm)).MotionInjected);
        Assert.Equal(2, motion.Injected);
    }

    [Fact]
    public async Task StyleKeywords_SwapLoraWithoutStacking_PreviousStyleUnmerged()
    {
        var (map, _, lora, clk) = Build();
        Assert.True((await map.DispatchActionAsync("a dark alley", VisionScene.Calm)).StyleSwapped);
        Assert.Equal("style-noir", map.ActiveStyleLora);
        Assert.Contains(lora.ActiveLoRAs, l => l.Id == "style-noir");

        clk.Now += VisionActionMapper.DefaultCooldownMs;
        Assert.True((await map.DispatchActionAsync("neon cyberpunk skyline", VisionScene.Calm)).StyleSwapped);
        Assert.Equal("style-cyberpunk", map.ActiveStyleLora);
        Assert.Single(lora.ActiveLoRAs);                                     // 누적 병합 아님 - 교체
        Assert.Equal("style-cyberpunk", lora.ActiveLoRAs[0].Id);
        Assert.Equal(1, lora.Unmerges);
        Assert.Equal(SamplingLoopState.Running, lora.LoopState);             // 스왑 후 샘플링 루프 즉시 재개

        clk.Now += VisionActionMapper.DefaultCooldownMs;
        Assert.False((await map.DispatchActionAsync("cyberpunk again", VisionScene.Calm)).StyleSwapped);   // 이미 적용 중 - 재병합 없음
        Assert.Equal(2, map.StyleSwaps);
    }

    [Fact]
    public async Task SceneBinderAlreadyMoved_ExplosionScene_MapperDefersMotion_ButStillAppliesStyle()
    {
        var (map, motion, lora, _) = Build();
        var r = await map.DispatchActionAsync("explosion in a fantasy castle", VisionScene.ExplosionFlash);
        Assert.True(r.MotionDeferredToSceneBinder);   // VisionReactionBinder 가 같은 응답으로 이미 줌/롤 주입 → 이중 주입 방지
        Assert.False(r.MotionInjected);
        Assert.Equal(0, motion.Injected);
        Assert.True(r.StyleSwapped);
        Assert.Contains(lora.ActiveLoRAs, l => l.Id == "style-fantasy");
    }

    [Fact]
    public void CooldownRegistry_ConcurrentEntrants_ExactlyOneWinsPerWindow()
    {
        var (map, _, _, _) = Build();
        var wins = 0;
        using var go = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() => { go.Wait(); if (map.TryEnter(VisionAction.MotionSpike)) Interlocked.Increment(ref wins); })).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());
        Assert.Equal(1, wins);
    }

    // 게시만 받고 절대 실행하지 않는 UI 컨텍스트 대역 - 매퍼가 호출자 컨텍스트로 복귀하려 하면 영원히 끝나지 않는다.
    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    }

    [Fact]
    public async Task Dispatch_DoesNotResumeOnCallerUiContext()
    {
        var motion = new WanDynamicMotionController();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lora = new LoRAHotSwapManager(_ => gate.Task);   // 병합이 실제로 비동기 대기하도록
        var map = new VisionActionMapper(motion, lora, () => Environment.TickCount64, 0);
        var ui = new NeverPumpedContext();

        Task<VisionActionDispatch> running;
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        try { running = map.DispatchActionAsync("dark cyberpunk street", VisionScene.Calm); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }

        gate.SetResult();
        var result = await running.WaitAsync(SafetyNet);   // 컨텍스트를 캡처했다면 Post 만 쌓이고 여기서 타임아웃
        Assert.True(result.StyleSwapped);
        Assert.Equal(0, ui.Posts);
    }

    private sealed class SummaryVlm(string summary) : IVisionLanguageClient
    {
        public bool Enabled => true;
        public async Task<VisionVlmReply?> DescribeAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct)
        {
            await Task.Yield();
            return new VisionVlmReply(VisionScene.Calm, 0.9, summary);
        }
    }

    [Fact]
    public async Task Analyzer_VlmConfirmedSummary_FlowsIntoMapper_EndToEnd()
    {
        var (map, _, lora, _) = Build();
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var pipe = new LiveStreamVirtualCamPipe("Local\\pfs_test_am_map_" + tag, "Local\\pfs_test_am_mtx_" + tag);
        using var analyzer = new VisionContextAnalyzer(pipe, new SummaryVlm("rainy neon cyberpunk alley"));
        analyzer.AttachActionMapper(map);
        Assert.True(analyzer.ActionMapperAttached);

        var frame = new byte[64 * 36 * 4]; Array.Fill(frame, (byte)120);
        pipe.SubmitFrame(frame, 64, 36, VirtualCamPixelFormat.Rgba32);
        analyzer.Tick();

        var deadline = DateTime.UtcNow + SafetyNet;
        while (map.StyleSwaps == 0) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(5); }
        Assert.Equal("style-cyberpunk", map.ActiveStyleLora);
        Assert.Contains(lora.ActiveLoRAs, l => l.Id == "style-cyberpunk");
        Assert.Equal(VisionAction.StyleCyberpunk, map.LastDispatch!.Value.Match.Style);
    }

    // ── v15.2.0 아기 스튜디오 원클릭 마스터피스 ──

    [Fact]
    public void BabyPromptPresets_OneClickInject_AutoFillsInput_Check()
    {
        var presets = PromptPresets.Baby;
        Assert.Equal(5, presets.Length);
        string[] icons = { "💤", "🫧", "🧸", "🥣", "🧚" };
        for (var i = 0; i < presets.Length; i++)
        {
            Assert.Equal(icons[i], presets[i].Icon);
            Assert.False(string.IsNullOrWhiteSpace(presets[i].Label));
            Assert.Contains("아기", presets[i].Snippet);

            // 기존 입력은 버리고 프리셋 문장이 그대로(동일 참조) 수신되어야 한다.
            var filled = PromptPresets.InjectMasterpiece(presets, i, "기존 사용자 입력");
            Assert.Same(presets[i].Snippet, filled);
        }
        Assert.StartsWith("부드러운 아침 햇살", PromptPresets.InjectMasterpiece(presets, 0, ""));
        Assert.EndsWith("스튜디오 컨셉 촬영", PromptPresets.InjectMasterpiece(presets, 4, ""));
        Assert.Equal(presets.Length, presets.Select(p => p.Label).Distinct().Count());
    }

    [Fact]
    public void BabyPromptPresets_OutOfRangeIndex_KeepsCurrentInput()
    {
        const string current = "유지되어야 할 입력";
        Assert.Same(current, PromptPresets.InjectMasterpiece(PromptPresets.Baby, -1, current));
        Assert.Same(current, PromptPresets.InjectMasterpiece(PromptPresets.Baby, PromptPresets.Baby.Length, current));
        Assert.Same(current, PromptPresets.InjectMasterpiece(Array.Empty<PromptChip>(), 0, current));
    }

    [Fact]
    public void BabyPromptPresets_Inject_IsZeroAlloc()
    {
        var presets = PromptPresets.Baby;
        var cur = "";
        for (var i = 0; i < 100; i++) cur = PromptPresets.InjectMasterpiece(presets, i % presets.Length, cur);   // JIT 워밍업

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) cur = PromptPresets.InjectMasterpiece(presets, i % presets.Length, cur);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Same(presets[(10_000 - 1) % presets.Length].Snippet, cur);
    }

    // ── v15.2.1 아기 스튜디오 울티메이트 매트릭스 ──

    [Fact]
    public void BabyStudio_Matrix_OneClickInject_AutoAppendsModifiersAndNegative_Check()
    {
        Assert.Equal(5, BabyStudioMatrix.Growth.Length);
        Assert.Equal(4, BabyStudioMatrix.Fantasy.Length);
        Assert.Equal(4, BabyStudioMatrix.Modifiers.Length);
        Assert.Equal(new[] { "💤", "🫧", "🧸", "🥣", "👶" }, BabyStudioMatrix.Growth.Select(c => c.Icon));
        Assert.Equal(new[] { "☁️", "🌲", "🌟", "🎈" }, BabyStudioMatrix.Fantasy.Select(c => c.Icon));
        Assert.Equal(new[] { "✨", "🎬", "🔍", "🕊️" }, BabyStudioMatrix.Modifiers.Select(c => c.Icon));
        Assert.All(BabyStudioMatrix.Growth.Concat(BabyStudioMatrix.Fantasy), c => Assert.Contains("아기", c.Snippet));

        // 1) 테마 원클릭: 입력 교체(리터럴 동일 참조) + 사용자 네거티브 보존 뒤 얼굴 보호 가드 결합.
        var s = new BabyStudioSession { NegativePrompt = "watermark, " };
        var prompt = s.ApplyTheme(BabyStudioMatrix.Growth, 0, "기존 입력");
        Assert.Same(BabyStudioMatrix.Growth[0].Snippet, prompt);
        Assert.Equal("watermark, " + BabyStudioMatrix.FacialSafetyNegative, s.NegativePrompt);
        Assert.True(s.GuardActive);
        Assert.Equal(1, s.GuardHits);

        // 재클릭·다른 카테고리 클릭은 가드 중복 누적 없음(동일 참조 유지).
        var neg = s.NegativePrompt;
        prompt = s.ApplyTheme(BabyStudioMatrix.Fantasy, 2, prompt);
        Assert.Same(BabyStudioMatrix.Fantasy[2].Snippet, prompt);
        Assert.Same(neg, s.NegativePrompt);
        Assert.Equal(1, s.GuardHits);

        // 빈 네거티브 → 가드 리터럴 그대로 · 범위 밖 인덱스 → 프롬프트·네거티브 불변.
        var empty = new BabyStudioSession();
        Assert.Same("유지", empty.ApplyTheme(BabyStudioMatrix.Growth, 99, "유지"));
        Assert.Equal("", empty.NegativePrompt);
        empty.ApplyTheme(BabyStudioMatrix.Fantasy, 0, "");
        Assert.Same(BabyStudioMatrix.FacialSafetyNegative, empty.NegativePrompt);

        // 2) 스마트 모디파이어 누적: 꼬리 쉼표/공백 정리 후 ", " 결합, 같은 모디파이어 재클릭은 무시.
        var p = BabyStudioMatrix.AppendModifier("잠든 아기 ,  ", 0);
        Assert.Equal("잠든 아기, " + BabyStudioMatrix.Modifiers[0].Snippet, p);
        p = BabyStudioMatrix.AppendModifier(p, 1);
        Assert.Equal("잠든 아기, " + BabyStudioMatrix.Modifiers[0].Snippet + ", " + BabyStudioMatrix.Modifiers[1].Snippet, p);
        Assert.Same(p, BabyStudioMatrix.AppendModifier(p, 0));
        Assert.Same(BabyStudioMatrix.Modifiers[3].Snippet, BabyStudioMatrix.AppendModifier("  ", 3));
        Assert.Same(p, BabyStudioMatrix.AppendModifier(p, -1));

        // 3) 할당: 무변경 경로 0B, 결합 경로는 결과 문자열 1개뿐(중간 문자열 없음).
        var cur = p;
        for (var i = 0; i < 100; i++) { cur = BabyStudioMatrix.AppendModifier(cur, i & 1); s.ApplyTheme(BabyStudioMatrix.Growth, i % 5, cur); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) { cur = BabyStudioMatrix.AppendModifier(cur, i & 1); s.ApplyTheme(BabyStudioMatrix.Growth, i % 5, cur); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        const string head = "웃는 아기";
        var tail = BabyStudioMatrix.Modifiers[2].Snippet;
        _ = BabyStudioMatrix.AppendModifier(head, tail);
        before = GC.GetAllocatedBytesForCurrentThread();
        var joined = BabyStudioMatrix.AppendModifier(head, tail);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(head.Length + 2 + tail.Length, joined.Length);
        Assert.True(bytes <= 2L * joined.Length + 40, $"결합 1회 할당 {bytes}B — 결과 문자열 외 할당 의심");

        // 4) 요청 결선: 네거티브가 WanVideoRequest 로 그대로 전달.
        var req = new WanVideoRequest { Prompt = prompt, NegativePrompt = s.NegativePrompt };
        Assert.EndsWith(BabyStudioMatrix.FacialSafetyNegative, req.NegativePrompt);
    }

    // ── v15.3.0 제미나이 AI 추천 스튜디오 ──

    [Fact]
    public void GeminiRecommendation_InjectAndEnhance_Check()
    {
        var picks = GeminiRecommendationMatrix.Picks;
        Assert.Equal(new[] { "📸", "🎨", "🎞️", "🌌" }, picks.Select(c => c.Icon));
        Assert.Equal(picks.Length, picks.Select(c => c.Label).Distinct().Count());

        // 픽 주입: 리터럴 동일 참조 교체 · 범위 밖은 현재 유지.
        for (var i = 0; i < picks.Length; i++) Assert.Same(picks[i].Snippet, GeminiRecommendationMatrix.InjectPick(i, "기존"));
        Assert.Same("유지", GeminiRecommendationMatrix.InjectPick(-1, "유지"));
        Assert.Same("유지", GeminiRecommendationMatrix.InjectPick(picks.Length, "유지"));

        // 강화: 꼬리 쉼표/공백 정리 후 품질 토큰 1회 결합, 재강화는 동일 참조(중복 0), 빈 입력은 토큰 리터럴.
        const string tokens = "masterpiece quality, 8k UHD, soft volumetric lighting, professional color grading";
        Assert.Equal(tokens, GeminiRecommendationMatrix.QualityTokens);
        var e = GeminiRecommendationMatrix.EnhancePromptWithGemini("잠든 아기 ,  ");
        Assert.Equal("잠든 아기, " + tokens, e);
        Assert.Same(e, GeminiRecommendationMatrix.EnhancePromptWithGemini(e));
        Assert.Same(GeminiRecommendationMatrix.QualityTokens, GeminiRecommendationMatrix.EnhancePromptWithGemini(""));
        Assert.Same(GeminiRecommendationMatrix.QualityTokens, GeminiRecommendationMatrix.EnhancePromptWithGemini(null));

        // 아기 매트릭스 결합: 테마 → 모디파이어 → 강화 순서 누적.
        var baby = BabyStudioMatrix.AppendModifier(BabyStudioMatrix.Growth[0].Snippet, 1);
        var combo = GeminiRecommendationMatrix.EnhancePromptWithGemini(baby);
        Assert.StartsWith(BabyStudioMatrix.Growth[0].Snippet, combo);
        Assert.EndsWith(BabyStudioMatrix.Modifiers[1].Snippet + ", " + tokens, combo);

        // 할당: 픽 주입·재강화 10k회 0B, 강화 결합 1회는 결과 문자열 하나뿐.
        var cur = combo;
        for (var i = 0; i < 100; i++) { cur = GeminiRecommendationMatrix.EnhancePromptWithGemini(cur); _ = GeminiRecommendationMatrix.InjectPick(i & 3, cur); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) { cur = GeminiRecommendationMatrix.EnhancePromptWithGemini(cur); _ = GeminiRecommendationMatrix.InjectPick(i & 3, cur); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(combo, cur);

        const string head = "웃는 아기";
        _ = GeminiRecommendationMatrix.EnhancePromptWithGemini(head);
        before = GC.GetAllocatedBytesForCurrentThread();
        var joined = GeminiRecommendationMatrix.EnhancePromptWithGemini(head);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(head.Length + 2 + tokens.Length, joined.Length);
        Assert.True(bytes <= 2L * joined.Length + 40, $"강화 1회 할당 {bytes}B — 결과 문자열 외 할당 의심");
    }

    // ── v15.4.0 라이브 비디오 캔버스 & Wan 2.1 튜너 ──

    [Fact]
    public void LiveVideoCanvas_ParameterTuningAndSharpening_Check()
    {
        var c = new LiveVideoCanvasStudio();
        Assert.Equal(new WanTuning(30, 6.0f, 127, false, false), c.Snapshot());
        Assert.Null(c.EffectiveSeed);

        // 범위 클램프: Steps 15~50 · CFG 1.0~15.0(0.5 스냅, NaN→기본) · Motion 1~255 · Seed ≥ 0.
        c.Steps = 3; Assert.Equal(15, c.Steps);
        c.Steps = 99; Assert.Equal(50, c.Steps);
        c.CfgScale = 0.2f; Assert.Equal(1.0f, c.CfgScale);
        c.CfgScale = 40f; Assert.Equal(15.0f, c.CfgScale);
        c.CfgScale = 7.3f; Assert.Equal(7.5f, c.CfgScale);
        c.CfgScale = float.NaN; Assert.Equal(6.0f, c.CfgScale);
        c.MotionBucket = 0; Assert.Equal(1, c.MotionBucket);
        c.MotionBucket = 1000; Assert.Equal(255, c.MotionBucket);
        c.Seed = -5; Assert.Equal(0, c.Seed);

        // 시드 핀 + 얼굴 보정 + 60FPS 보간 → 요청 파이프라인 동기화.
        c.Steps = 40; c.CfgScale = 9f; c.MotionBucket = 180; c.Seed = 1234;
        c.SeedPinned = true; c.FaceSharpen = true; c.Interpolate60 = true;
        var req = new WanVideoRequest { Prompt = "아기", Seed = c.EffectiveSeed, Tuning = c.Snapshot() };
        Assert.Equal(1234, req.Seed);
        Assert.Equal(new WanTuning(40, 9f, 180, true, true), req.Tuning);
        Assert.Equal("40 steps · CFG 9.0 · Motion 180 · Seed 1234 · 👶보정 · 60FPS", c.Summary);
        Assert.Equal("9.0", c.CfgLabel);

        // 요약/라벨은 값 불변 시 동일 참조 재사용, 변경 시에만 재생성.
        var s = c.Summary;
        c.Steps = 40; c.FaceSharpen = true;
        Assert.Same(s, c.Summary);
        c.FaceSharpen = false;
        Assert.DoesNotContain("보정", c.Summary);

        // 할당: 튜너 조작 + 스냅숏 + 캐시 조회 10k회 0B(값 변경 없는 구간).
        for (var i = 0; i < 100; i++) { c.Steps = 40; c.MotionBucket = 180; _ = c.Snapshot(); _ = c.EffectiveSeed; _ = c.Summary; _ = c.CfgLabel; }
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { for (var i = 0; i < 10_000; i++) { c.Steps = 40; c.MotionBucket = 180; c.CfgScale = 9f; _ = c.Snapshot(); _ = c.EffectiveSeed; _ = c.Summary; _ = c.CfgLabel; } }));   // 다중 창 최소값(전체 실행 GC 압력 플래키 배제)

        // 리셋: 기본값 복귀 · 핀 해제 → 시드 미전송.
        c.Reset();
        Assert.Equal(new WanTuning(30, 6.0f, 127, false, false), c.Snapshot());
        Assert.Null(c.EffectiveSeed);
        Assert.Equal("30 steps · CFG 6.0 · Motion 127 · Seed 랜덤", c.Summary);
    }

    // ── v15.4.1 DashScope 튜닝 어댑터 · MMF 라이브 캔버스 ──

    [Fact]
    public void DashScopeTuningAdapter_MapsTuningToPromptTokens_Check()
    {
        const string p = "잠든 아기";
        var neutral = new WanTuning(30, 6.0f, 127, false, false);
        Assert.Same(p, DashScopeTuningAdapter.Apply(p, neutral));   // 기본 구간 → 원본 참조(0B)

        var full = new WanTuning(45, 10f, 40, true, true);
        var o = DashScopeTuningAdapter.Apply(p + " , ", full);
        Assert.Equal(string.Join(", ", p, DashScopeTuningAdapter.StepsHigh, DashScopeTuningAdapter.CfgHigh,
            DashScopeTuningAdapter.MotionStill, DashScopeTuningAdapter.FaceSharpen, DashScopeTuningAdapter.Interp60), o);
        Assert.Same(o, DashScopeTuningAdapter.Apply(o, full));       // 재적용 시 중복 0

        Assert.Equal(DashScopeTuningAdapter.StepsLow, DashScopeTuningAdapter.StepsToken(15));
        Assert.Null(DashScopeTuningAdapter.StepsToken(30));
        Assert.Equal(DashScopeTuningAdapter.CfgLow, DashScopeTuningAdapter.CfgToken(2f));
        Assert.Equal(DashScopeTuningAdapter.MotionSoft, DashScopeTuningAdapter.MotionToken(80));
        Assert.Equal(DashScopeTuningAdapter.MotionLively, DashScopeTuningAdapter.MotionToken(200));
        Assert.Equal(DashScopeTuningAdapter.MotionEnergetic, DashScopeTuningAdapter.MotionToken(255));
        Assert.Equal(DashScopeTuningAdapter.Interp60, DashScopeTuningAdapter.Apply("", new WanTuning(30, 6f, 127, false, true)));

        // 800자 상한: 원문은 자르지 않고 넘치는 토큰을 뒤에서부터 버린다.
        var longP = new string('가', 700);
        var capped = DashScopeTuningAdapter.Apply(longP, full);
        Assert.True(capped.Length <= DashScopeTuningAdapter.MaxPromptChars);
        Assert.StartsWith(longP, capped);
        Assert.Contains(DashScopeTuningAdapter.StepsHigh, capped);
        Assert.DoesNotContain(DashScopeTuningAdapter.Interp60, capped);
        var over = new string('가', 801);
        Assert.Same(over, DashScopeTuningAdapter.Apply(over, full));

        // 할당: 무변경 경로 10k회 0B · 결합 1회는 결과 문자열 하나.
        for (var i = 0; i < 100; i++) { _ = DashScopeTuningAdapter.Apply(p, neutral); _ = DashScopeTuningAdapter.Apply(o, full); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) { _ = DashScopeTuningAdapter.Apply(p, neutral); _ = DashScopeTuningAdapter.Apply(o, full); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        _ = DashScopeTuningAdapter.Apply(p, full);
        before = GC.GetAllocatedBytesForCurrentThread();
        var joined = DashScopeTuningAdapter.Apply(p, full);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes <= 2L * joined.Length + 40, $"결합 1회 할당 {bytes}B — 결과 문자열 외 할당 의심");
    }

    [Fact]
    public void GridAlignment_And_DashScopeTuningAdapter_Check()
    {
        // 1) 3칼럼 높이 동기화 CSS 계약(Workspace.razor 소스 잠금).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var razor = File.ReadAllText(Path.Combine(dir!.FullName, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("align-items: stretch;", razor);
        Assert.DoesNotContain(".ws-grid { display: grid; grid-template-columns: 1.15fr 1.15fr 300px; gap: 16px; margin-top: 16px; align-items: start; }", razor);
        Assert.Contains(".ws-grid > .ws-card, .ws-grid > .ws-rail { height: 100%; min-height: 0;", razor);
        Assert.Contains(".ws-rail > .ws-w:last-child { flex: 1 1 auto; }", razor);
        Assert.Contains("text-align: right; font-variant-numeric: tabular-nums;", razor);   // 튜너 수치 우측 정렬선
        Assert.Contains("id=\"ws-live\"", razor);                                          // MMF 라이브 캔버스 결선점
        Assert.Contains("Tuning = _canvas.Snapshot()", razor);                             // 튜너 → 요청 결선

        // 2) 튜너 스냅숏 → DashScope 토큰: UI 경계값이 그대로 클라우드 프롬프트에 반영.
        var c = new LiveVideoCanvasStudio { Steps = 50, CfgScale = 15f, MotionBucket = 1, FaceSharpen = true, Interpolate60 = true };
        var o = DashScopeTuningAdapter.Apply("웃는 아기", c.Snapshot());
        Assert.Equal(string.Join(", ", "웃는 아기", DashScopeTuningAdapter.StepsHigh, DashScopeTuningAdapter.CfgHigh,
            DashScopeTuningAdapter.MotionStill, DashScopeTuningAdapter.FaceSharpen, DashScopeTuningAdapter.Interp60), o);
        c.Reset();
        Assert.Same("웃는 아기", DashScopeTuningAdapter.Apply("웃는 아기", c.Snapshot()));
        c.Steps = 15; c.CfgScale = 1f; c.MotionBucket = 255;
        Assert.Equal(string.Join(", ", "웃는 아기", DashScopeTuningAdapter.StepsLow, DashScopeTuningAdapter.CfgLow, DashScopeTuningAdapter.MotionEnergetic),
            DashScopeTuningAdapter.Apply("웃는 아기", c.Snapshot()));
    }

    [Fact]
    public void Wan21_CameraAndStyleStudio_ZeroAlloc_Check()
    {
        var cams = WanCameraStyleStudio.Cameras;
        var stys = WanCameraStyleStudio.Styles;
        Assert.Equal(new[] { "Orbit", "FPV Drone", "Crane Up", "Dolly Zoom", "Smooth Pan", "Tilt Roll" }, cams.Select(c => c.Label));
        Assert.Equal(new[] { "8K Photoreal", "3D Render Art", "Watercolor", "Cyberpunk Neon", "16mm Vintage" }, stys.Select(c => c.Label));
        // 토큰끼리 부분 포함 금지(ActiveIndex·배타 교체 오검출 방지).
        var all = cams.Concat(stys).Select(c => c.Snippet).ToArray();
        for (var i = 0; i < all.Length; i++) for (var j = 0; j < all.Length; j++) if (i != j) Assert.DoesNotContain(all[i], all[j]);

        // 결합 · 그룹 배타 교체(중간/끝/처음 위치) · 재클릭 0B.
        const string p = "웃는 아기";
        var a = WanCameraStyleStudio.ApplyCamera(p + ", ", 0);
        Assert.Equal(p + ", " + cams[0].Snippet, a);
        Assert.Same(a, WanCameraStyleStudio.ApplyCamera(a, 0));
        var b = WanCameraStyleStudio.ApplyStyle(a, 3);
        Assert.Equal(p + ", " + cams[0].Snippet + ", " + stys[3].Snippet, b);
        var c = WanCameraStyleStudio.ApplyCamera(b, 1);                 // 중간 카메라 교체 → 스타일 보존 후 꼬리에 새 카메라
        Assert.Equal(p + ", " + stys[3].Snippet + ", " + cams[1].Snippet, c);
        Assert.Equal(1, WanCameraStyleStudio.ActiveIndex(c, cams));
        Assert.Equal(3, WanCameraStyleStudio.ActiveIndex(c, stys));
        var d = WanCameraStyleStudio.ApplyCamera(c, 5);                 // 끝 위치 교체
        Assert.Equal(p + ", " + stys[3].Snippet + ", " + cams[5].Snippet, d);
        Assert.Equal(stys[0].Snippet, WanCameraStyleStudio.ApplyStyle(stys[2].Snippet, 0));   // 단독 토큰 교체
        Assert.Equal("아기, " + stys[1].Snippet, WanCameraStyleStudio.ApplyStyle(stys[4].Snippet + ", 아기", 1));   // 처음 위치 제거 → 새 토큰은 꼬리
        Assert.Same(d, WanCameraStyleStudio.ApplyStyle(d, 99));
        Assert.Equal(-1, WanCameraStyleStudio.ActiveIndex(p, cams));
        Assert.Equal(cams[2].Snippet, WanCameraStyleStudio.ApplyCamera(null, 2));

        // 요청 결선: 프롬프트가 그대로 WanVideoRequest 로.
        Assert.Contains(cams[5].Snippet, new WanVideoRequest { Prompt = d }.Prompt);

        // 할당: 재클릭·활성 조회 10k회 0B, 교체 1회는 결과 문자열 하나.
        for (var i = 0; i < 100; i++) { _ = WanCameraStyleStudio.ApplyCamera(d, 5); _ = WanCameraStyleStudio.ApplyStyle(d, 3); _ = WanCameraStyleStudio.ActiveIndex(d, cams); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) { _ = WanCameraStyleStudio.ApplyCamera(d, 5); _ = WanCameraStyleStudio.ApplyStyle(d, 3); _ = WanCameraStyleStudio.ActiveIndex(d, cams); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        _ = WanCameraStyleStudio.ApplyCamera(d, 0);
        before = GC.GetAllocatedBytesForCurrentThread();
        var swapped = WanCameraStyleStudio.ApplyCamera(d, 0);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes <= 2L * swapped.Length + 40, $"교체 1회 할당 {bytes}B — 결과 문자열 외 할당 의심");
    }

    [Fact]
    public void LiveCanvas_MmfFrameReader_DeliversOnlyNewSequences_ZeroAlloc()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var pipe = new LiveStreamVirtualCamPipe("pfs_test_livecv_" + tag, "pfs_test_livecv_mx_" + tag);
        var reader = new LiveCanvasFrameReader(pipe);
        Assert.False(reader.TryPoll());                               // 프레임 없음

        var frame = new byte[64 * 36 * 4];
        for (var i = 0; i < frame.Length; i++) frame[i] = (byte)(i * 7);
        Assert.True(pipe.SubmitFrame(frame, 64, 36, VirtualCamPixelFormat.Rgba32));
        Assert.True(reader.TryPoll());
        Assert.Equal(64, reader.Width);
        Assert.Equal(36, reader.Height);
        Assert.Equal(frame.Length, reader.Frame.Length);               // interop 용 정확 길이
        Assert.True(frame.AsSpan().SequenceEqual(reader.Frame));
        var buf = reader.Frame;

        Assert.False(reader.TryPoll());                               // 같은 시퀀스 → 재전달 없음
        Assert.Equal(1, reader.Stale);

        frame[0] = 0xAB;
        Assert.True(pipe.SubmitFrame(frame, 64, 36, VirtualCamPixelFormat.Rgba32));
        Assert.True(reader.TryPoll());
        Assert.Same(buf, reader.Frame);                               // 같은 해상도 → 버퍼 재사용
        Assert.Equal(0xAB, reader.Frame[0]);
        Assert.Equal(2, reader.Delivered);

        // 무할당: 동일 시퀀스 폴링 10k회 0B(헤더 값 형식 · 스크래치 재사용).
        for (var i = 0; i < 100; i++) reader.TryPoll();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) reader.TryPoll();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        // 대역 주입: 비RGBA·상한 초과 프레임은 거부.
        var fake = new LiveCanvasFrameReader((Span<byte> d, out VirtualCamFrameHeader h, out int n) =>
        {
            h = new VirtualCamFrameHeader(7, 1920, 1080, VirtualCamPixelFormat.Rgba32, 0, 1920 * 1080 * 4);
            n = d.Length;
            return true;
        });
        Assert.False(fake.TryPoll());
        Assert.Equal(1, fake.Rejected);
        Assert.False(fake.TryPoll());                                 // 같은 시퀀스 재거부 없이 Stale
        Assert.Equal(1, fake.Stale);
    }
}
