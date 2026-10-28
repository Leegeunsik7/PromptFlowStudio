using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

[Trait("Category", "FastUnit")]
public class HeterogeneousTaskSchedulerTests
{
    [Fact]
    public void DetectDevices_DualGpuModeFlag_MatchesActualDeviceCount()
    {
        var scheduler = new HeterogeneousTaskScheduler();
        Assert.Equal(scheduler.Devices.Count >= 2, scheduler.DualGpuMode);
    }

    [Fact]
    public async Task RunOnSecondaryAsync_SingleGpuFallback_RoutesToPrimaryInstead()
    {
        var scheduler = new HeterogeneousTaskScheduler();
        await scheduler.RunOnSecondaryAsync(_ => Task.CompletedTask, "xunit-secondary");

        if (scheduler.DualGpuMode)
            Assert.Equal(1, scheduler.SecondaryTasks);
        else
            Assert.Equal(1, scheduler.PrimaryTasks);   // 단일GPU/미탐지 환경에서도 Graceful Fallback 이 실제로 라우팅됨
    }

    [Fact]
    public async Task RunOnPrimaryAsync_AlwaysRoutesToPrimary_RegardlessOfGpuCount()
    {
        var scheduler = new HeterogeneousTaskScheduler();
        await scheduler.RunOnPrimaryAsync(_ => Task.CompletedTask, "xunit-primary");
        Assert.Equal(1, scheduler.PrimaryTasks);
        Assert.Equal(0, scheduler.SecondaryTasks);
    }

    [Fact]
    public void SimulateMigration_IncrementsCounter_AndReturnsNonNegativeElapsed()
    {
        var scheduler = new HeterogeneousTaskScheduler();
        var data = new byte[4096];
        new Random(5).NextBytes(data);
        var ms = scheduler.SimulateMigration(data);
        Assert.True(ms >= 0);
        Assert.Equal(1, scheduler.Migrations);
    }
}

[Trait("Category", "FastUnit")]
public class LoRAHotSwapManagerTests
{
    // 결정적 동기화: 스왑 구간을 TaskCompletionSource 게이트로 붙잡아 실시간 지연(60ms)과의 경쟁을 원천 제거.
    //   "게이트가 닫혀 있는 동안 완료될 수 없음"을 IsCompleted 로 단언 - 벽시계 측정이 전혀 없어 CPU 부하와 무관.
    internal static (LoRAHotSwapManager Manager, TaskCompletionSource Gate) GatedManager()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (new LoRAHotSwapManager(ct => gate.Task.WaitAsync(ct)), gate);
    }

    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);   // 교착 시 무한대기 방지용 안전망(타이밍 단언 아님)

    [Fact]
    public async Task MergeAsync_EntersPausedForSwap_AndWaitIfPausedAsyncBlocksUntilResume()
    {
        var (manager, gate) = GatedManager();

        var mergeTask = manager.MergeAsync("xunit-lora", 0.5);
        Assert.Equal(SamplingLoopState.PausedForSwap, manager.LoopState);   // SwapAsync 는 첫 await 전에 동기적으로 Pause 게시

        var waitTask = manager.WaitIfPausedAsync();
        Assert.False(waitTask.IsCompleted, "게이트가 닫혀 있는 동안 Pause 게이트가 통과되면 회귀");
        Assert.False(mergeTask.IsCompleted);

        gate.SetResult();
        var result = await mergeTask.WaitAsync(SafetyNet);
        await waitTask.WaitAsync(SafetyNet);

        Assert.True(result.Ok);
        Assert.Equal(SamplingLoopState.Running, manager.LoopState);
    }

    [Fact]
    public async Task MergeAsync_CanceledMidSwap_ResumesLoopImmediatelyWithoutMerging()
    {
        var (manager, _) = GatedManager();   // 게이트를 끝까지 열지 않음 - 취소만이 유일한 탈출 경로
        using var cts = new CancellationTokenSource();

        var mergeTask = manager.MergeAsync("xunit-lora-cancel", 0.5, cts.Token);
        Assert.Equal(SamplingLoopState.PausedForSwap, manager.LoopState);

        cts.Cancel();
        var result = await mergeTask.WaitAsync(SafetyNet);

        Assert.False(result.Ok);
        Assert.Equal(SamplingLoopState.Running, manager.LoopState);
        Assert.Empty(manager.ActiveLoRAs);
    }

    [Fact]
    public async Task MergeThenUnmerge_TracksActiveLoRAsAndCountersCorrectly()
    {
        var manager = new LoRAHotSwapManager();
        await manager.MergeAsync("a", 0.5);
        await manager.MergeAsync("b", 0.9);
        Assert.Equal(2, manager.ActiveLoRAs.Count);

        await manager.UnmergeAsync("a");
        Assert.Single(manager.ActiveLoRAs);
        Assert.Equal("b", manager.ActiveLoRAs[0].Id);
        Assert.Equal(2, manager.Merges);
        Assert.Equal(1, manager.Unmerges);
    }
}


[Trait("Category", "FastUnit")]
public class MultimodalAudioSyncHudBindingTests
{
    // v11.0.0-F1: GlobalTelemetryHudView 의 gth-v110f1 카드가 바로 이 두 값(Report/LastApplied)을 그대로 바인딩한다 —
    //   HUD 가 보여줄 실제 데이터 계약(진폭/주파수/비트→모션 주입)이 무너지지 않는지 회귀 검증.
    [Fact]
    public void HudBinding_LoudFrameAfterQuiet_ReportAndMotionVectorBothReflectRealInjection()
    {
        var motion = new WanDynamicMotionController();
        var scheduler = new HeterogeneousTaskScheduler();
        var engine = new MultimodalAudioSyncEngine(motion, scheduler);

        for (var i = 0; i < 5; i++) engine.AnalyzeFrame(GenerateSineWavePcm16(440, 16000, 512, 0.05), 16000);
        var loud = engine.AnalyzeFrame(GenerateSineWavePcm16(1200, 16000, 512, 0.9), 16000);

        Assert.True(loud.BeatDetected, "조용한 프레임 다음의 급격한 진폭 증가는 비트로 감지되어야 한다.");
        var report = engine.Report;
        Assert.True(report.FramesAnalyzed >= 6);
        Assert.True(report.BeatsDetected >= 1);
        Assert.True(report.LastFrequencyHz > 0);
        Assert.NotNull(motion.LastApplied);
        Assert.True(motion.LastApplied!.ZoomMagnitude > 0);
    }

    private static byte[] GenerateSineWavePcm16(double freqHz, int sampleRateHz, int sampleCount, double amplitude)
    {
        var bytes = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * freqHz * i / sampleRateHz) * amplitude * short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }
}

[Trait("Category", "FastUnit")]
public class JsonSourceGenerationRegressionTests
{
    // v11.0.0-Refinement REFINE-1: 24개 파일을 이관한 PromptFlowWireJsonContext(camelCase) 가 실제로
    //   리플렉션 없이 왕복 직렬화되는지 대표 타입(CloudProvisionRequest)으로 회귀 검증.
    [Fact]
    public void CloudProvisionRequest_SourceGenRoundTrip_PreservesAllFieldsAndCamelCaseWireFormat()
    {
        var req = new CloudProvisionRequest(4, "ap-northeast-2", "xunit-render");

        var json = System.Text.Json.JsonSerializer.Serialize(req, PromptFlowWireJsonContext.Default.CloudProvisionRequest);
        Assert.Contains("\"slots\"", json);   // camelCase 와이어 규약 유지 확인(대소문자 그대로)

        var back = System.Text.Json.JsonSerializer.Deserialize(json, PromptFlowWireJsonContext.Default.CloudProvisionRequest);
        Assert.NotNull(back);
        Assert.Equal(req.Slots, back!.Slots);
        Assert.Equal(req.Region, back.Region);
        Assert.Equal(req.Purpose, back.Purpose);
    }
}
[Trait("Category", "FastUnit")]
public class WanPostProcessingEngineTests
{
    private static WanPostProcessingEngine Build(out LivePipelineAutoTuner tuner, out AutonomousNeuralTilingUpscaler upscaler)
    {
        var calibrator = new AutoTunerHardwareCalibrator();
        tuner = new LivePipelineAutoTuner(calibrator);
        upscaler = new AutonomousNeuralTilingUpscaler();
        var scheduler = new HeterogeneousTaskScheduler();
        var motion = new WanDynamicMotionController();
        var lora = new LoRAHotSwapManager();
        return new WanPostProcessingEngine(upscaler, tuner, scheduler, motion, lora);
    }

    [Fact]
    public void LivePipelineAutoTuner_Int8Downgrade_ForcesUpscale8KTo4K_AndFp16RecoveryRestores8K()
    {
        var engine = Build(out var tuner, out var upscaler);
        upscaler.SetTarget(UpscaleTarget.Uhd8K);

        for (var i = 0; i < 8; i++) tuner.ObserveFrame(200);   // 목표(120)×1.15=138 초과 지속 8틱 → Fp16→Fp8→Int8
        Assert.Equal(WanQuantMode.Int8, tuner.CurrentQuant);
        Assert.Equal(UpscaleTarget.Uhd4K, engine.CurrentFactor);

        for (var i = 0; i < 20; i++) tuner.ObserveFrame(30);   // 목표×0.55=66 미만 지속 → Int8→Fp8→Fp16(EWMA 감쇠 여유있게 20틱)
        Assert.Equal(WanQuantMode.Fp16, tuner.CurrentQuant);
        Assert.Equal(UpscaleTarget.Uhd8K, engine.CurrentFactor);
    }

    [Fact]
    public async Task SubmitFrameWithMotionAsync_AppliesObservableByteTransform_NotDeadComputation()
    {
        var motion = new WanDynamicMotionController();
        var calibrator = new AutoTunerHardwareCalibrator();
        var tuner = new LivePipelineAutoTuner(calibrator);
        var upscaler = new AutonomousNeuralTilingUpscaler();
        var scheduler = new HeterogeneousTaskScheduler();
        var lora = new LoRAHotSwapManager();
        var engine = new WanPostProcessingEngine(upscaler, tuner, scheduler, motion, lora);

        var original = new byte[256];
        new Random(2).NextBytes(original);
        var frame = (byte[])original.Clone();
        motion.SetPan(PanDirection.Right, 0.8);
        motion.SetZoom(ZoomDirection.In, 0.5);
        motion.SetRoll(15);

        await engine.SubmitFrameWithMotionAsync(frame);

        Assert.NotEqual(original, frame);   // 모션 벡터가 프레임 바이트를 실제로 변형(계산 후 버려지는 죽은 값 금지)
        Assert.NotNull(motion.LastApplied);
        Assert.Equal(PanDirection.Right, motion.LastApplied!.Pan);
    }

    [Fact]
    public async Task SubmitFrameWithMotionAsync_WaitsForInFlightHotSwap_BeforeProcessing()
    {
        var motion = new WanDynamicMotionController();
        var calibrator = new AutoTunerHardwareCalibrator();
        var tuner = new LivePipelineAutoTuner(calibrator);
        var upscaler = new AutonomousNeuralTilingUpscaler();
        var scheduler = new HeterogeneousTaskScheduler();
        var (lora, gate) = LoRAHotSwapManagerTests.GatedManager();
        var engine = new WanPostProcessingEngine(upscaler, tuner, scheduler, motion, lora);

        var mergeTask = lora.MergeAsync("xunit-lora-2", 0.5);
        var frame = new byte[256];
        new Random(3).NextBytes(frame);
        var submitTask = engine.SubmitFrameWithMotionAsync(frame);

        Assert.False(submitTask.IsCompleted, "진행 중 핫스왑을 무시하고 프레임을 처리하면 Pause 게이트 회귀");
        Assert.Equal(0, engine.FramesIn);
        Assert.Null(motion.LastApplied);   // 모션 주입도 Pause 해제 전엔 일어나지 않아야 함

        gate.SetResult();
        await mergeTask.WaitAsync(TimeSpan.FromSeconds(10));
        await submitTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, engine.FramesIn);
        Assert.NotNull(motion.LastApplied);
    }
}

[Trait("Category", "Integration")]
public class EdgeP2PTaskRelayTests
{
    private static Task<byte[]> LocalEcho(byte[] payload, CancellationToken ct) => Task.FromResult(payload);

    [Fact]
    public async Task P2PPeerDiscoveryAndFallback_ExecutesCleanly()
    {
        // overloadThreshold: 0 -> 첫 호출부터 "오버로드"로 취급(결정적 - 실 동시성 경쟁 없이 오프로드 경로를 확실히 태움).
        // v12.2.0-Phase4: 포트 0(OS 임시 할당) - 고정 포트는 동시 실행 중인 다른 테스트 프로세스/직전 실행의 TIME_WAIT 와 충돌할 수 있었다.
        using var nodeA = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0, overloadThreshold: 0);
        using var nodeB = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0, overloadThreshold: 0);
        nodeA.Start();
        nodeB.Start();
        try
        {
            // 1) 디스커버리 - 브로드캐스트 대신 명시적 유니캐스트(테스트 결정성 - OS/네트워크 브로드캐스트 루프백 의존 배제).
            await nodeA.PingAsync(new IPEndPoint(IPAddress.Loopback, nodeB.BoundDiscoveryPort));
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (nodeA.Peers.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(nodeA.Peers.Count > 0, "UDP 핑/퐁 디스커버리로 피어를 찾지 못함");
            Assert.Equal(1, nodeA.PingsSent);

            // 2) 오프로드 성공 경로 - 피어(nodeB)가 실제로 TCP 로 받아 결정적 변형 후 되돌려줌.
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var (relay1, result1) = await nodeA.RunOrOffloadAsync((byte[])payload.Clone(), LocalEcho);
            Assert.True(relay1.Ok);
            Assert.True(relay1.UsedPeer, "피어가 탐지됐고 정상 응답했으니 오프로드 경로를 타야 한다");
            Assert.Equal(1, nodeA.PeerOffloads);
            for (var i = 0; i < payload.Length; i++)
                Assert.Equal((byte)(payload[i] ^ 0x5A ^ (byte)i), result1[i]);   // 결정적 스텁 변형 검증

            // 3) Fail-Closed 폴백 경로 - 존재하지 않는 피어(nodeB 중지 후)로는 실패 -> 예외 없이 로컬로 대체.
            nodeB.Stop();
            var (relay2, result2) = await nodeA.RunOrOffloadAsync((byte[])payload.Clone(), LocalEcho);
            Assert.True(relay2.Ok);
            Assert.False(relay2.UsedPeer, "피어가 응답 불가하면 로컬 폴백으로 떨어져야 한다");
            Assert.Equal(payload, result2);
            Assert.True(nodeA.Fallbacks >= 1);
        }
        finally { nodeA.Stop(); nodeB.Stop(); }
    }

    [Fact]
    public async Task RunOrOffloadAsync_NoPeersEverDiscovered_AlwaysUsesLocalWithoutException()
    {
        using var node = new EdgeP2PTaskRelay(discoveryPort: 47996, tcpPort: 47997, overloadThreshold: 0);
        var (relay, result) = await node.RunOrOffloadAsync(new byte[] { 9, 9 }, LocalEcho);

        Assert.True(relay.Ok);
        Assert.False(relay.UsedPeer);
        Assert.Equal(new byte[] { 9, 9 }, result);
        Assert.Equal(0, node.PeerOffloads);
    }

    [Fact]
    public void ResetCounters_ClearsAllCountersAndPeers()
    {
        using var node = new EdgeP2PTaskRelay(discoveryPort: 47998, tcpPort: 47999);
        node.ResetCounters();
        Assert.Empty(node.Peers);
        Assert.Equal(0, node.LocalRuns);
        Assert.Equal(0, node.PeerOffloads);
        Assert.Equal(0, node.Fallbacks);
    }
}
[Trait("Category", "Integration")]
public class InteractiveReactionEngineTests
{
    private static (WanDynamicMotionController motion, LoRAHotSwapManager lora, InteractiveReactionEngine engine) Build()
    {
        var motion = new WanDynamicMotionController();
        var lora = new LoRAHotSwapManager();
        return (motion, lora, new InteractiveReactionEngine(motion, lora));
    }

    [Fact]
    public async Task InteractiveReactionEngine_TriggerDispatchesMotionAndSwap_Successfully()
    {
        var (motion, lora, engine) = Build();

        Assert.True(engine.EnqueueTrigger(EventTriggerType.Donation, "CustomEvent"));
        var result = await engine.DispatchNextAsync();

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(EventTriggerType.Donation, result.Type);
        Assert.True(result.MotionInjected);
        Assert.True(result.LoraSwapped);
        Assert.NotNull(motion.LastApplied);
        Assert.Equal(PanDirection.Up, motion.LastApplied!.Pan);   // ProfileFor(Donation) 의 결정적 프로필
        Assert.Contains(lora.ActiveLoRAs, w => w.Id == "reaction-donation");
        Assert.Equal(1, engine.Dispatched);
    }

    [Fact]
    public async Task EnqueueTrigger_ConcurrentFloodBeyondCapacity_DropsExcessSafelyAndDispatchesRestWithoutCorruption()
    {
        var (_, _, engine) = Build();
        const int floodCount = 200;   // 큐 상한(64) 을 훨씬 초과 - 동시 폭주 시뮬레이션.

        var enqueueTasks = Enumerable.Range(0, floodCount)
            .Select(i => Task.Run(() => engine.EnqueueTrigger(EventTriggerType.ChatCommand, $"flood-{i}")))
            .ToArray();
        var accepted = await Task.WhenAll(enqueueTasks);

        Assert.Equal(floodCount, engine.EventsReceived);
        Assert.True(engine.QueueOverflows > 0, "폭주 상황에서 오버플로우가 최소 1건 이상 발생해야 한다");
        Assert.Equal(accepted.Count(a => a), floodCount - (int)engine.QueueOverflows);

        // 수락된 만큼 전부 예외 없이 디스패치 가능해야 한다(스레드 세이프티 - 카운터/큐 상태 붕괴 없음).
        var dispatchedCount = 0;
        while (await engine.DispatchNextAsync() is not null) dispatchedCount++;
        Assert.Equal(accepted.Count(a => a), dispatchedCount);
        Assert.Equal(0, engine.QueueDepth);
    }

    [Fact]
    public async Task ConnectAsync_RealObsWebSocket5HandshakeAgainstStubServer_ReceivesAndMapsSceneChangeEvent()
    {
        // 실 OBS 가 없는 환경에서 프로토콜(Hello->Identify->Identified->Event) 자체의 정확성을 검증하기 위한
        // 자체 스텁 서버(HttpListener 웹소켓 업그레이드 - EmbeddedHttpServer.cs 와 동일한 BCL 기법).
        // v17.63.1: 고정 범위 난수 포트(47700+) → 빈 포트 선점 기동(실패 시 재선정) · 이벤트 수신은 폴링 대신 OnChange 신호 대기.
        var listener = ServiceLifecycleTests.StartLoopbackListener(out var port);
        using var cts = new CancellationTokenSource(ServiceLifecycleTests.LoadSafetyNet);
        var serverTask = RunStubObsServerAsync(listener, cts.Token);

        var (_, _, engine) = Build();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnChange += () => { if (engine.EventsReceived > 0) received.TrySetResult(); };
        await engine.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), password: null, cts.Token);
        if (engine.EventsReceived == 0) await received.Task.WaitAsync(cts.Token);

        Assert.Equal(ObsConnectionState.Identified, engine.ConnectionState);
        Assert.Equal(1, engine.EventsReceived);
        Assert.Contains(engine.RecentEvents, e => e.Type == EventTriggerType.SceneChange);

        cts.Cancel();
        try { await serverTask; } catch { /* 취소로 인한 종료 무시 */ }
    }

    private static async Task RunStubObsServerAsync(HttpListener listener, CancellationToken ct)
    {
        try
        {
            var getContextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, ct));
            if (completed != getContextTask) return;
            var ctx = await getContextTask;
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }

            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws = wsCtx.WebSocket;

            await SendJsonAsync(ws, new JsonObject { ["op"] = 0, ["d"] = new JsonObject { ["rpcVersion"] = 1 } }, ct);
            await ReceiveJsonDiscardAsync(ws, ct);   // Identify - 스텁이라 내용 검증은 생략
            await SendJsonAsync(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } }, ct);

            await SendJsonAsync(ws, new JsonObject
            {
                ["op"] = 5,
                ["d"] = new JsonObject
                {
                    ["eventType"] = "CurrentProgramSceneChanged",
                    ["eventIntent"] = 4,
                    ["eventData"] = new JsonObject { ["sceneName"] = "Scene A" },
                },
            }, ct);

            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { /* 정상 종료 */ }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
        catch (HttpListenerException) { /* 리스너 중지에 따른 예외 무시 */ }
        finally { listener.Stop(); listener.Close(); }
    }

    internal static async Task SendJsonAsync(WebSocket ws, JsonNode node, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    internal static async Task ReceiveJsonDiscardAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do { result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false); } while (!result.EndOfMessage);
    }
}

/// <summary>v12.1.0: MainForm/HUD 에서 쓰는 수명주기 경로(재시작·재연결·해제)의 회귀 검증 - 실시간 지연 비교 없이 신호/상태로만 판정.</summary>
[Trait("Category", "Integration")]
public class ServiceLifecycleTests
{
    // v17.63.1: 고부하 병렬 일괄 실행(463 건) 안전망 — 교착 탐지 전용(타이밍 단언 아님). 10 s 는 CPU 포화 시 정상 핸드셰이크도 초과했다.
    internal static readonly TimeSpan LoadSafetyNet = TimeSpan.FromSeconds(45);

    /// <summary>
    /// v17.63.1 플레이키 근본 해결 — 이전엔 테스트마다 47600~47989 고정 범위 난수 포트로 스텁 리스너를 비동기 메서드 안에서 기동해
    /// 병렬 실행 중 포트 충돌(HttpListenerException)이 서버 태스크에 묻히고 클라이언트는 거부/타임아웃으로 실패했다.
    /// 이제 OS 할당 빈 포트로 테스트 본문에서 동기 기동하고, 선정~바인드 경합 창에서 실패하면 새 포트로 재시도한다.
    /// </summary>
    internal static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); return l; }
            catch (HttpListenerException) when (attempt < 32) { l.Close(); }
        }
    }

    // 매 실행 빈 UDP/TCP 포트 선정(고정 포트 충돌 회피). 선정 직후 해제하므로 극히 짧은 경합 창만 남는다.
    internal static (int Udp, int Tcp) FreePorts()
    {
        using var u = new System.Net.Sockets.UdpClient(0);
        var l = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
        l.Start();
        var tcp = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return (((IPEndPoint)u.Client.LocalEndPoint!).Port, tcp);
    }

    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);

    [Fact]
    public void EdgeP2PTaskRelay_StopThenStart_RebindsSamePortsAndRunsAgain()
    {
        var (udpPort, tcpPort) = FreePorts();   // 같은 포트 재바인드를 검증하려면 구성값이 고정이어야 함 - 대신 매 실행 빈 포트로 선정
        using var node = new EdgeP2PTaskRelay(discoveryPort: udpPort, tcpPort: tcpPort);
        node.Start();
        Assert.True(node.Running);
        node.Stop();
        Assert.False(node.Running);

        node.Start();   // HUD 토글 재시작 - 이전 세대 소켓이 해제돼 있어야 같은 포트 재바인드 성공
        Assert.True(node.Running);
        node.Stop();
    }

    [Fact]
    public void EdgeP2PTaskRelay_StartWithTcpPortOccupied_ThrowsThenRecoversAfterPortFreed()
    {
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
        blocker.Start();
        var blockedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
        using var node = new EdgeP2PTaskRelay(discoveryPort: FreePorts().Udp, tcpPort: blockedPort);
        try
        {
            Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => node.Start());
            Assert.False(node.Running);   // 롤백 - 이전엔 true 로 남아 이후 Start 가 영구 no-op
        }
        finally { blocker.Stop(); }

        node.Start();
        Assert.True(node.Running);
        node.Stop();
    }

    [Fact]
    public async Task InteractiveReactionEngine_ReconnectThenDisconnect_CyclesStateAndOpensFreshHandshake()
    {
        var listener = StartLoopbackListener(out var port);
        using var cts = new CancellationTokenSource(LoadSafetyNet);
        var handshakes = 0;
        var serverTask = RunMultiConnectionStubAsync(listener, () => Interlocked.Increment(ref handshakes), cts.Token);

        using var engine = new InteractiveReactionEngine(new WanDynamicMotionController(), new LoRAHotSwapManager());
        var uri = new Uri($"ws://127.0.0.1:{port}/");

        await engine.ConnectAsync(uri, password: null, cts.Token);
        Assert.Equal(ObsConnectionState.Identified, engine.ConnectionState);
        Assert.Equal(uri, engine.LastServerUri);

        await engine.ReconnectAsync(cts.Token);
        Assert.Equal(ObsConnectionState.Identified, engine.ConnectionState);
        Assert.Equal(2, Volatile.Read(ref handshakes));   // 새 소켓으로 핸드셰이크를 처음부터 다시 수행

        await engine.DisconnectAsync();
        Assert.Equal(ObsConnectionState.Disconnected, engine.ConnectionState);

        cts.Cancel();
        try { await serverTask; } catch { /* 취소 종료 무시 */ }
    }

    [Fact]
    public async Task InteractiveReactionEngine_ReconnectWithoutPriorConnect_ThrowsInvalidOperation()
    {
        using var engine = new InteractiveReactionEngine(new WanDynamicMotionController(), new LoRAHotSwapManager());
        Assert.Null(engine.LastServerUri);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ReconnectAsync());
        Assert.Equal(ObsConnectionState.Disconnected, engine.ConnectionState);
    }

    // 다중 접속 스텁 - 접속마다 Hello→Identify→Identified 를 수행하고 연결을 유지(클라이언트가 끊을 때까지).
    private static async Task RunMultiConnectionStubAsync(HttpListener listener, Action onIdentified, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var getContextTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, ct));
                if (completed != getContextTask) return;
                var ctx = await getContextTask;
                if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                        await InteractiveReactionEngineTests.SendJsonAsync(ws, new JsonObject { ["op"] = 0, ["d"] = new JsonObject { ["rpcVersion"] = 1 } }, ct);
                        await InteractiveReactionEngineTests.ReceiveJsonDiscardAsync(ws, ct);
                        onIdentified();   // Identified 송신 전에 집계 - 클라이언트 ConnectAsync 반환 시점엔 이미 반영돼 있음
                        await InteractiveReactionEngineTests.SendJsonAsync(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } }, ct);
                        var buf = new byte[1024];
                        while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(buf, ct);
                    }
                    catch { /* 클라이언트 Abort/취소 - 정상 */ }
                });
            }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
        catch (HttpListenerException) { /* 리스너 중지 */ }
        finally { listener.Stop(); listener.Close(); }
    }
}

/// <summary>v12.2.0 Phase 1 감사: 핸들/소켓 수명주기 엣지케이스 회귀 잠금 - 벽시계 비교 없이 신호/상태로만 판정.</summary>
[Trait("Category", "Integration")]
public class Phase1AuditHardeningTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);

    private static (string map, string mutex) UniqueNames()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        return ("Local\\pfs_test_audit_map_" + tag, "Local\\pfs_test_audit_mtx_" + tag);
    }

    // 소유 스레드가 Mutex 를 쥔 채 종료 → 커널이 Abandoned 로 표시. 반쪽 쓰기를 흉내내려고 헤더 Magic 은 유효하게 두고 페이로드만 훼손.
    private static void AbandonMutexAfterTornWrite(string mapName, string mutexName)
    {
        var t = new Thread(() =>
        {
            var m = new Mutex(false, mutexName);
            m.WaitOne();
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(mapName);
            using var acc = mmf.CreateViewAccessor();
            acc.Write(LiveStreamVirtualCamPipe.HeaderBytes, (byte)0xEE);   // 페이로드 첫 바이트 훼손(쓰기 도중 크래시)
            // ReleaseMutex 없이 스레드 종료 = Abandoned (핸들은 GC 에 맡김 - 닫아도 소유권은 해제되지 않음)
        });
        t.Start();
        t.Join();
    }

    [Fact]
    public void AbandonedMutex_Recovery_Successfully()
    {
        var (map, mutex) = UniqueNames();
        // 취득 한도 10초: Join 이후 OS 스레드 종료(=Abandoned 표시)가 늦어져도 50ms 만료로 오판하지 않고 Abandoned 신호로 대기가 풀린다.
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex, SafetyNet);
        var pixels = new byte[16 * 16 * 4];
        new Random(3).NextBytes(pixels);
        Assert.True(pipe.SubmitFrame(pixels, 16, 16, VirtualCamPixelFormat.Rgba32));

        // 1) 리더 경로: 이전엔 AbandonedMutexException 이 그대로 전파됐다 → 이제 예외 없이 거부 + 헤더 무효화.
        AbandonMutexAfterTornWrite(map, mutex);
        var dest = new byte[pixels.Length];
        var ok = pipe.TryReadLatestFrame(dest, out var info, out _);
        Assert.False(ok);   // 찢어진 프레임을 유효 프레임으로 넘기지 않음
        Assert.Null(info);
        Assert.Equal(1, pipe.AbandonedRecoveries);

        // 무효화 후엔 소유권이 정상 반환돼 다음 읽기도 막히지 않고 "프레임 없음"으로 응답.
        Assert.False(pipe.TryReadLatestFrame(dest, out _, out _));
        Assert.Equal(1, pipe.AbandonedRecoveries);

        // 2) 라이터 경로: Abandoned 상태에서도 소유권 재취득 후 전체 덮어쓰기로 일관성 복구.
        AbandonMutexAfterTornWrite(map, mutex);
        Assert.True(pipe.SubmitFrame(pixels, 16, 16, VirtualCamPixelFormat.Rgba32));
        Assert.Equal(2, pipe.AbandonedRecoveries);

        Assert.True(pipe.TryReadLatestFrame(dest, out info, out var n));
        Assert.Equal(pixels.Length, n);
        Assert.Equal(pixels, dest);
    }

    [Fact]
    public void LiveStreamVirtualCamPipe_SubmitAndReadAfterDispose_DropGracefullyWithoutException()
    {
        var (map, mutex) = UniqueNames();
        var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        pipe.Dispose();
        pipe.Dispose();   // 이중 Dispose 무해
        Assert.False(pipe.SubmitFrame(new byte[16], 2, 2, VirtualCamPixelFormat.Rgba32));
        Assert.False(pipe.TryReadLatestFrame(new byte[16], out _, out _));
    }

    [Fact]
    public void OperationsCanaryLogRetentionCleaner_RunAfterDispose_ReturnsResultWithoutTouchingReleasedView()
    {
        var cleaner = new PromptFlowStudio.Services.Operations.OperationsCanaryLogRetentionCleaner("Local\\pfs_test_audit_ret_" + Guid.NewGuid().ToString("N")[..8]);
        cleaner.Dispose();
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfs_audit_missing_" + Guid.NewGuid().ToString("N"));
        var r = cleaner.RunCleanup(logsRootOverride: missing);
        Assert.Equal(0, r.FilesScanned);
    }

    [Fact]
    public async Task WebSocket_UngracefulDisconnect_CleansUpResources_WithoutLeak()
    {
        var listener = ServiceLifecycleTests.StartLoopbackListener(out var port);
        using var cts = new CancellationTokenSource(ServiceLifecycleTests.LoadSafetyNet);
        var abortServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = RunAbortingStubAsync(listener, abortServer.Task, cts.Token);

        var engine = new InteractiveReactionEngine(new WanDynamicMotionController(), new LoRAHotSwapManager());
        await engine.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), password: null, cts.Token);
        Assert.Equal(ObsConnectionState.Identified, engine.ConnectionState);
        Assert.True(engine.HasLiveConnectionResources);
        var loops = engine.ActiveLoopTasks;
        Assert.NotNull(loops);

        abortServer.SetResult();   // 서버 측 RST(Close 프레임 없이 끊음) - OBS 크래시 흉내

        // 이전엔 수신 루프만 끝나고 디스패치 루프(10ms 폴링)·소켓·CTS 가 영구 잔류 → 이 대기가 SafetyNet 까지 풀리지 않았다.
        await Task.WhenAll(loops!.Value.Receive, loops.Value.Dispatch).WaitAsync(ServiceLifecycleTests.LoadSafetyNet);

        Assert.Equal(ObsConnectionState.Disconnected, engine.ConnectionState);
        Assert.False(engine.HasLiveConnectionResources);
        Assert.Equal(1, engine.ConnectionsReleased);

        engine.Dispose();   // 이미 해제된 세대를 다시 해제하지 않음(이중 해제 0)
        Assert.Equal(1, engine.ConnectionsReleased);

        cts.Cancel();
        try { await serverTask; } catch { /* 취소 종료 무시 */ }
    }

    [Fact]
    public async Task WebSocket_HandshakeFailure_ReleasesSocketImmediately()
    {
        // 리스너 없는 포트 - ConnectAsync 실패 시 Failed 상태로 소켓/CTS 가 남던 누수 확인.
        using var engine = new InteractiveReactionEngine(new WanDynamicMotionController(), new LoRAHotSwapManager());
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ConnectAsync(new Uri("ws://127.0.0.1:47599/"), null, CancellationToken.None));
        Assert.Equal(ObsConnectionState.Failed, engine.ConnectionState);
        Assert.False(engine.HasLiveConnectionResources);
        Assert.Equal(1, engine.ConnectionsReleased);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 20)]
    [InlineData(640, 1280)]
    [InlineData(1280, 2000)]
    [InlineData(2000, 2000)]
    public void EdgeP2P_NextBackoffMs_GrowsExponentiallyAndCaps(int current, int expected)
        => Assert.Equal(expected, EdgeP2PTaskRelay.NextBackoffMs(current));

    [Theory]
    [InlineData(System.Net.Sockets.SocketError.ConnectionReset, true)]
    [InlineData(System.Net.Sockets.SocketError.NetworkUnreachable, true)]
    [InlineData(System.Net.Sockets.SocketError.HostUnreachable, true)]
    [InlineData(System.Net.Sockets.SocketError.OperationAborted, false)]
    [InlineData(System.Net.Sockets.SocketError.Interrupted, false)]
    [InlineData(System.Net.Sockets.SocketError.NotSocket, false)]
    public void EdgeP2P_IsTransientSocketError_ClassifiesRecoverableVsShutdown(System.Net.Sockets.SocketError error, bool transient)
        => Assert.Equal(transient, EdgeP2PTaskRelay.IsTransientSocketError(error));

    [Fact]
    public async Task EdgeP2P_PingToClosedPort_DiscoveryLoopSurvivesAndStillAnswersPeers()
    {
        // 닫힌 UDP 포트로 유니캐스트 → ICMP Port Unreachable. Windows 에선 이전 구현의 다음 ReceiveFrom 이 WSAECONNRESET 으로
        // 실패해 탐지 루프가 break(영구 사망)했다. 이제 SIO_UDP_CONNRESET 차단 + 백오프 복구로 루프가 살아 있어야 한다.
        using var nodeA = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        using var nodeB = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        nodeA.Start();
        nodeB.Start();

        await nodeA.PingAsync(new IPEndPoint(IPAddress.Loopback, 48009));   // 리스너 없는 포트
        await nodeA.PingAsync(new IPEndPoint(IPAddress.Loopback, 48009));

        var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnB() { if (nodeB.PongsReceived > 0) pong.TrySetResult(); }
        nodeB.OnChange += OnB;
        try
        {
            await nodeB.PingAsync(new IPEndPoint(IPAddress.Loopback, nodeA.BoundDiscoveryPort));
            await pong.Task.WaitAsync(SafetyNet);   // A 가 살아서 Pong 을 돌려줘야만 완료
        }
        finally { nodeB.OnChange -= OnB; }

        Assert.True(nodeA.Running);
        Assert.Contains(nodeA.Peers, p => p.NodeId == nodeB.NodeId);
    }

    // 핸드셰이크 완료 후 신호를 받으면 Close 프레임 없이 Abort(TCP RST 계열 비정상 종료).
    private static async Task RunAbortingStubAsync(HttpListener listener, Task abortSignal, CancellationToken ct)
    {
        try
        {
            var ctx = await listener.GetContextAsync().WaitAsync(ct);
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            await InteractiveReactionEngineTests.SendJsonAsync(ws, new JsonObject { ["op"] = 0, ["d"] = new JsonObject { ["rpcVersion"] = 1 } }, ct);
            await InteractiveReactionEngineTests.ReceiveJsonDiscardAsync(ws, ct);
            await InteractiveReactionEngineTests.SendJsonAsync(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } }, ct);
            await abortSignal.WaitAsync(ct);
            ws.Abort();
            ctx.Response.Abort();
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
        catch (HttpListenerException) { /* 리스너 중지 */ }
        finally { listener.Stop(); listener.Close(); }
    }
}

/// <summary>v12.2.0 Phase 2: Zero-Alloc 핫패스·SIMD 비트 동일성·무락 리더 torn-read 부재 회귀 잠금(벽시계 단언 없음).</summary>
[Trait("Category", "Integration")]
public class Phase2ZeroAllocOptimizationTests
{
    private static WanPostProcessingEngine BuildEngine()
        => new(new AutonomousNeuralTilingUpscaler(), new LivePipelineAutoTuner(new AutoTunerHardwareCalibrator()),
               new HeterogeneousTaskScheduler(), new WanDynamicMotionController(), new LoRAHotSwapManager());

    private static byte[] SinePcm16(double freqHz, int sampleRate, int samples, double amp)
    {
        var buf = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * freqHz * i / sampleRate) * amp * short.MaxValue);
            buf[i * 2] = (byte)(s & 0xFF);
            buf[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return buf;
    }

    [Fact]
    public void FastFFT_And_FrameTransform_DoesNotCauseGCPressure()
    {
        using var engine = BuildEngine();
        var audio = new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler());
        var frame = new byte[64 * 1024];
        new Random(5).NextBytes(frame);
        var dest = new byte[64 * 1024];
        var pcm = SinePcm16(440, 48000, 1024, 0.5);   // 일정 진폭 → 비트 미발생(모션 주입 경로 제외, 순수 FFT 핫패스만 측정)
        var vector = new WanMotionVector(1, PanDirection.Right, 0.4, ZoomDirection.In, 0.3, 2, DateTimeOffset.UnixEpoch);

        void Iteration()
        {
            WanPostProcessingEngine.ApplyMotionInPlace(frame, vector);
            engine.SubmitFrame(frame);
            engine.CopyLastInterpolatedFrameTo(dest);
            audio.AnalyzeFrame(pcm, 48000);
        }

        for (var i = 0; i < 64; i++) Iteration();   // 워밍업: JIT 티어링·ArrayPool 스레드 캐시 채움

        var allocated = AllocationProbe.MinAllocated(() => { for (var i = 0; i < 600; i++) Iteration(); });   // 60FPS × 10초 분량

        Assert.Equal(0, allocated);   // 이전: 프레임당 Stopwatch·지문 string·LastAction string·결과 record·UpscaleReport·FFT 공유버퍼 경합
        Assert.True(engine.FramesIn >= 64 + 600);
        Assert.NotNull(engine.LastInterpolatedDigest8);   // 지연 생성 경로가 조회 시 정상 동작
        Assert.StartsWith($"프레임 #{engine.FramesIn} ", engine.LastAction);
        Assert.StartsWith($"프레임 #{audio.FramesAnalyzed} ", audio.LastAction);
    }

    [Fact]
    public void InterpolateInto_Simd_MatchesScalarReferenceBitExact_ForAllTailLengths()
    {
        var rng = new Random(17);
        for (var len = 0; len <= 70; len++)
        {
            var a = new byte[len]; var b = new byte[len]; rng.NextBytes(a); rng.NextBytes(b);
            if (len > 0) { a[0] = 255; b[0] = 255; }   // 오버플로 경계(255+255)
            var got = new byte[len];
            WanPostProcessingEngine.InterpolateInto(a, b, got);
            for (var i = 0; i < len; i++) Assert.Equal((byte)((a[i] + b[i]) >> 1), got[i]);
        }
    }

    [Fact]
    public void ApplyMotionInPlace_Simd_MatchesScalarReferenceBitExact()
    {
        var vector = new WanMotionVector(3, PanDirection.Up, 0.9, ZoomDirection.In, 0.7, -12.5, DateTimeOffset.UnixEpoch);
        var key = (byte)(1 + (int)(vector.PanMagnitude * 50) + (int)(vector.ZoomMagnitude * 30) + (int)Math.Abs(vector.RollDegrees));
        foreach (var len in new[] { 0, 1, 15, 16, 255, 256, 257, 1000, 65536 })
        {
            var data = new byte[len];
            new Random(len).NextBytes(data);
            var expected = new byte[len];
            for (var i = 0; i < len; i++) expected[i] = (byte)(data[i] ^ key ^ (byte)i);   // v12.1.0 이전 스칼라 원식
            WanPostProcessingEngine.ApplyMotionInPlace(data, vector);
            Assert.Equal(expected, data);
        }
    }

    [Fact]
    public async Task CopyLastInterpolatedFrameTo_LockFreeReaderDuringConcurrentWrites_NeverObservesTornFrame()
    {
        // 균일값 프레임만 제출 → 보간 결과도 항상 균일. 리더가 두 게시 사이에 섞인(torn) 버퍼를 복사하면 균일성이 깨진다.
        using var engine = BuildEngine();
        const int Frames = 1500;
        var stop = 0;
        var reads = 0;
        var torn = 0;
        var reader = Task.Run(() =>
        {
            var dest = new byte[64 * 1024];
            while (Volatile.Read(ref stop) == 0)
            {
                var n = engine.CopyLastInterpolatedFrameTo(dest);
                if (n == 0) continue;
                reads++;
                var first = dest[0];
                for (var i = 1; i < n; i++) if (dest[i] != first) { torn++; break; }
            }
        });

        var frame = new byte[64 * 1024];
        for (var k = 0; k < Frames; k++)
        {
            Array.Fill(frame, (byte)(k * 37));
            engine.SubmitFrame(frame);
        }
        Volatile.Write(ref stop, 1);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, torn);
        Assert.Equal(Frames, engine.FramesIn);
    }

    [Fact]
    public void AnalyzeFrame_ConcurrentCallers_EachGetsOwnCorrectDominantFrequency()
    {
        // 이전 구현은 인스턴스 공유 _re/_im 를 락 없이 써서 병렬 호출 시 서로의 FFT 입력을 덮어썼다.
        var audio = new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler());
        var low = SinePcm16(440, 48000, 1024, 0.5);
        var high = SinePcm16(6000, 48000, 1024, 0.5);
        const double BinHz = 48000.0 / 1024;
        var wrong = 0;
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, i =>
        {
            var useHigh = (i & 1) == 1;
            var f = audio.AnalyzeFrame(useHigh ? high : low, 48000);
            if (Math.Abs(f.DominantFrequencyHz - (useHigh ? 6000 : 440)) > BinHz) Interlocked.Increment(ref wrong);
        });
        Assert.Equal(0, wrong);
        Assert.Equal(400, audio.FramesAnalyzed);
    }

    [Fact]
    public void Upscaler_HotPathThenLastReport_MaterializesLazilyWithSameSemantics()
    {
        var up = new AutonomousNeuralTilingUpscaler();
        Assert.Null(up.LastReport);
        up.UpscaleHotPath();
        var rep = up.LastReport;
        Assert.NotNull(rep);
        Assert.Same(rep, up.LastReport);   // 캐시 - 반복 조회 시 재할당 없음
        Assert.Equal(up.Target, rep!.Target);
        Assert.Equal(1, up.Runs);
        var viaPublic = up.Upscale();   // 공개 API 는 여전히 리포트 반환
        Assert.Equal(2, up.Runs);
        Assert.Same(viaPublic, up.LastReport);
        Assert.Contains("타일 업스케일", up.LastAction);
    }
}

/// <summary>v12.2.0-Phase4: 리액션 오버레이 → 가상카메라 송출 종단간 결속(Passthrough/합성/폴백/0-Alloc).</summary>
[Collection("ProcessGcMode")]   // v16.9.0: 스레드 할당 0B 단언 — 병렬 GC 중 ArrayPool 트림 오염 차단
[Trait("Category", "Integration")]
public class Phase4VirtualCamOverlayE2ETests
{
    private const int W = 128, H = 128;

    private static (string map, string mutex) UniqueNames()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        return ("Local\\pfs_test_p4_map_" + tag, "Local\\pfs_test_p4_mtx_" + tag);
    }

    private static StreamerReactionEngine BuildFx()
        => new(new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler()), new ReactionOverlayPipeline(), () => 0);

    private sealed class ThrowingOverlay : IVirtualCamOverlay
    {
        public bool HasVisibleContent => true;
        public int RenderOverlayInto(Span<byte> rgba, int width, int height) => throw new InvalidOperationException("overlay fault");
    }

    [Fact]
    public void AttachedButEmptyOverlay_PassesThroughOriginalBytesUntouched()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var reader = new LiveStreamVirtualCamPipe(map, mutex);
        using var fx = BuildFx();
        pipe.AttachOverlay(fx);
        Assert.False(fx.HasVisibleContent);

        var frame = new byte[W * H * 4];
        new Random(9).NextBytes(frame);
        Assert.True(pipe.SubmitFrame(frame, W, H, VirtualCamPixelFormat.Rgba32));

        var back = new byte[frame.Length];
        Assert.True(reader.TryReadLatestFrame(back, out _, out _));
        Assert.Equal(frame, back);
        Assert.Equal(0, pipe.OverlayFramesComposited);
    }

    [Fact]
    public void ActiveOverlay_CompositesIntoBroadcastFrame_WithoutMutatingCallerBuffer()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var reader = new LiveStreamVirtualCamPipe(map, mutex);
        using var fx = BuildFx();
        pipe.AttachOverlay(fx);
        fx.TriggerManual(ReactionAffect.Hype, 0.9);
        fx.Advance(ReactionOverlayPipeline.StepMs);
        Assert.True(fx.HasVisibleContent);

        var black = new byte[W * H * 4];
        Assert.True(pipe.SubmitFrame(black, W, H, VirtualCamPixelFormat.Rgba32));

        Assert.All(black, b => Assert.Equal(0, b));   // 호출측 버퍼 불변(합성은 풀 스크래치에서)
        var back = new byte[black.Length];
        Assert.True(reader.TryReadLatestFrame(back, out var info, out var n));
        Assert.Equal(black.Length, n);
        Assert.Contains(back, b => b != 0);           // 송출 프레임엔 파티클이 실제로 합성됨
        Assert.Equal(1, pipe.OverlayFramesComposited);
        Assert.Equal(W, info!.Width);
    }

    [Fact]
    public void ActiveOverlay_SteadyStateSubmit_DoesNotAllocate()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var fx = BuildFx();
        pipe.AttachOverlay(fx);
        var frame = new byte[W * H * 4];

        void Iteration(int i)
        {
            if (i % 20 == 0) fx.TriggerManual(ReactionAffect.Hype, 0.8);
            fx.Advance(ReactionOverlayPipeline.StepMs);
            pipe.SubmitFrame(frame, W, H, VirtualCamPixelFormat.Rgba32);
        }

        for (var i = 0; i < 60; i++) Iteration(i);   // 워밍업(JIT·ArrayPool 스레드 캐시)
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { for (var i = 0; i < 300; i++) Iteration(i); }));
        Assert.True(pipe.OverlayFramesComposited > 0);
    }

    [Fact]
    public void Nv12Frame_BypassesRgbaOverlayEvenWhenActive()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var fx = BuildFx();
        pipe.AttachOverlay(fx);
        fx.TriggerManual(ReactionAffect.Hype, 0.9);
        fx.Advance(ReactionOverlayPipeline.StepMs);
        Assert.True(pipe.SubmitFrame(new byte[W * H * 3 / 2], W, H, VirtualCamPixelFormat.Nv12));
        Assert.Equal(0, pipe.OverlayFramesComposited);
    }

    [Fact]
    public void FaultyOverlay_FallsBackToOriginalFrame_AndBroadcastStillSucceeds()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var reader = new LiveStreamVirtualCamPipe(map, mutex);
        pipe.AttachOverlay(new ThrowingOverlay());
        var frame = new byte[W * H * 4];
        new Random(4).NextBytes(frame);

        Assert.True(pipe.SubmitFrame(frame, W, H, VirtualCamPixelFormat.Rgba32));
        var back = new byte[frame.Length];
        Assert.True(reader.TryReadLatestFrame(back, out _, out _));
        Assert.Equal(frame, back);

        pipe.AttachOverlay(null);
        Assert.False(pipe.OverlayAttached);
    }
}

/// <summary>v15.1.0: Wan Ultra-Efficiency 파이프라인 — 거버너 동적 스케일링·SIMD 위상 보간 비트 정합성·락-프리 스테이징·0B 연쇄 처리.</summary>
[Collection("ProcessGcMode")]   // v16.9.0: 스레드 할당 0B 단언 — 병렬 GC 중 ArrayPool 트림 오염 차단
[Trait("Category", "Integration")]
public class WanUltraEfficiencyPipelineTests
{
    private const int W = 128, H = 128;

    private static (string map, string mutex) UniqueNames()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        return ("Local\\pfs_test_v151_map_" + tag, "Local\\pfs_test_v151_mtx_" + tag);
    }

    private static void Feed(WanQualitySpeedGovernor g, double ms, int count)
    {
        for (var i = 0; i < count; i++) g.RecordLatency(ms);
    }

    [Fact]
    public void Governor_SustainedOverBudget_DegradesOneLevelPerWindow_AndClampsAtMax()
    {
        var g = new WanQualitySpeedGovernor(100);
        Assert.Equal(50, g.CurrentSteps);
        Assert.Equal(100, g.ResolutionScalePct);

        var lastSteps = g.CurrentSteps;
        for (var window = 1; window <= WanQualitySpeedGovernor.MaxLevel + 3; window++)
        {
            Feed(g, 150, WanQualitySpeedGovernor.EvaluateEvery);   // 예산 초과·스파이크(200) 미만
            Assert.Equal(Math.Min(window, WanQualitySpeedGovernor.MaxLevel), g.Level);
            Assert.True(g.CurrentSteps <= lastSteps);              // 스텝 역행(증가) 없음
            lastSteps = g.CurrentSteps;
        }
        Assert.Equal(20, g.CurrentSteps);
        Assert.Equal(60, g.ResolutionScalePct);
        Assert.Equal(WanQualitySpeedGovernor.MaxLevel, g.Degrades);
        Assert.Equal(0, g.Preemptions);
        Assert.Contains("지연 예산 초과", g.LastAction);
    }

    [Fact]
    public void Governor_GpuStallSpike_PreemptsImmediately_WithoutWaitingForWindow()
    {
        var g = new WanQualitySpeedGovernor(100);
        g.RecordLatency(250);
        Assert.Equal(1, g.Level);
        Assert.Equal(42, g.CurrentSteps);
        Assert.Equal(1, g.Preemptions);
        Assert.Contains("선제 완화", g.LastAction);
    }

    [Fact]
    public void Governor_Headroom_UpgradesOnlyAfterConsecutiveCalmWindows()
    {
        var g = new WanQualitySpeedGovernor(100);
        g.RecordLatency(250); g.RecordLatency(250);
        Assert.Equal(2, g.Level);

        Feed(g, 10, WanQualitySpeedGovernor.EvaluateEvery - 2);   // 창 1(스파이크 2건 포함) — 여유 1회
        Assert.Equal(2, g.Level);
        Feed(g, 10, WanQualitySpeedGovernor.EvaluateEvery);       // 창 2 — 여유 2회
        Assert.Equal(2, g.Level);
        Feed(g, 10, WanQualitySpeedGovernor.EvaluateEvery);       // 창 3 — 3회 연속 → 1단 상향
        Assert.Equal(1, g.Level);
        Assert.Equal(1, g.Upgrades);
        Assert.Contains("여유 회복", g.LastAction);
    }

    [Fact]
    public void Governor_BackpressureDropRate_DegradesEvenWhenLatencyIsFast()
    {
        var g = new WanQualitySpeedGovernor(100);
        for (var i = 0; i < 100; i++) g.RecordFrame(i % 10 == 0);   // 드롭률 10%
        Feed(g, 30, WanQualitySpeedGovernor.EvaluateEvery);
        Assert.Equal(1, g.Level);
        Assert.InRange(g.Snapshot().DropRate, 0.099, 0.101);
        Assert.Contains("드롭률", g.LastAction);
    }

    [Fact]
    public void Governor_ConcurrentSamplers_NeverSkipOrOvershootLevels()
    {
        var g = new WanQualitySpeedGovernor(100);
        var changes = 0;
        g.OnChange += () => Interlocked.Increment(ref changes);
        var threads = new Thread[8];
        for (var t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() => { for (var i = 0; i < 5000; i++) { g.RecordLatency(150); g.RecordFrame(false); } });
            threads[t].Start();
        }
        foreach (var th in threads) Assert.True(th.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(WanQualitySpeedGovernor.MaxLevel, g.Level);
        Assert.Equal(WanQualitySpeedGovernor.MaxLevel, g.Degrades);   // CAS ±1 — 경계 초과 전이 0
        Assert.Equal(0, g.Upgrades);
        Assert.Equal(WanQualitySpeedGovernor.MaxLevel, Volatile.Read(ref changes));
        Assert.Equal(40000, g.Snapshot().Samples);
    }

    [Fact]
    public void Governor_ScaleResolution_KeepsAspectAndSixteenAlignment_AndBindsIntoWanRequest()
    {
        var g = new WanQualitySpeedGovernor(100);
        Assert.Equal((1080, 1920), g.ScaleResolution(1080, 1920));

        var plain = WanRenderClient.BuildRequest(new WanPromptBuild("sunset", 30, Array.Empty<string>(), null), null);
        var plainJson = System.Text.Json.JsonSerializer.Serialize(plain, PromptFlowWireJsonContext.Default.WanRenderRequest);
        Assert.DoesNotContain("inferenceSteps", plainJson);   // 거버너 미지정 = 기존 페이로드 그대로

        Feed(g, 150, WanQualitySpeedGovernor.EvaluateEvery * WanQualitySpeedGovernor.MaxLevel);
        var (w, h) = g.ScaleResolution(1080, 1920);
        Assert.Equal((640, 1152), (w, h));
        Assert.Equal(0, w % 16); Assert.Equal(0, h % 16);

        var governed = WanRenderClient.ApplyGovernor(plain, g);
        Assert.Equal(20, governed.InferenceSteps);
        Assert.Equal(640, governed.Width);
        var json = System.Text.Json.JsonSerializer.Serialize(governed, PromptFlowWireJsonContext.Default.WanRenderRequest);
        Assert.Contains("\"inferenceSteps\":20", json);
    }

    [Fact]
    public void BlendWeightedInto_Simd_MatchesScalarReferenceBitExact_ForAllTailsAndWeights()
    {
        var rng = new Random(151);
        int[] weights = { -5, 0, 1, 64, 127, 128, 200, 255, 256, 999 };
        for (var len = 0; len <= 100; len++)
        {
            var a = new byte[len]; var b = new byte[len]; rng.NextBytes(a); rng.NextBytes(b);
            if (len > 1) { a[0] = 255; b[0] = 255; a[1] = 0; b[1] = 255; }   // 오버플로/극단 경계
            foreach (var w in weights)
            {
                var t = Math.Clamp(w, 0, 256);
                var got = new byte[len];
                WanPostProcessingEngine.BlendWeightedInto(a, b, got, w);
                for (var i = 0; i < len; i++) Assert.Equal((byte)((a[i] * (256 - t) + b[i] * t + 128) >> 8), got[i]);

                var inPlace = (byte[])a.Clone();   // 별칭(dest == prev) 제자리 스무딩
                WanPostProcessingEngine.SmoothTemporalInPlace(inPlace, b, w);
                Assert.Equal(got, inPlace);
            }
        }
        var x = new byte[64]; var y = new byte[64]; rng.NextBytes(x); rng.NextBytes(y);
        var d = new byte[64];
        WanPostProcessingEngine.BlendWeightedInto(x, y, d, 0); Assert.Equal(x, d);     // 위상 0 = 직전 키프레임
        WanPostProcessingEngine.BlendWeightedInto(x, y, d, 256); Assert.Equal(y, d);   // 위상 256 = 최신 키프레임
    }

    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(500, 1000, 128)]
    [InlineData(1000, 1000, 256)]
    [InlineData(5000, 1000, 256)]
    [InlineData(-10, 1000, 0)]
    [InlineData(10, 0, 256)]
    public void ComputePhase256_ClampsToKeyframeInterval(long elapsed, long interval, int expected)
        => Assert.Equal(expected, WanPostProcessingEngine.ComputePhase256(elapsed, interval));

    [Fact]
    public void DoubleBuffer_ProducerNeverWaits_WhileConsumerHoldsFrontSlot()
    {
        var buf = new WanFrameDoubleBuffer(256);
        var frame = new byte[256];
        Assert.True(buf.TryPublish(frame, 8, 8, VirtualCamPixelFormat.Rgba32, out _));
        Assert.True(buf.TryBeginConsume(out var held, out var info));   // 소비자가 front 를 쥔 채로
        Assert.Equal(1, info.Sequence);

        for (var i = 0; i < 100; i++) Assert.True(buf.TryPublish(frame, 8, 8, VirtualCamPixelFormat.Rgba32, out _));   // 생산자 대기·실패 0
        Assert.Equal(256, held.Length);
        Assert.False(buf.TryBeginConsume(out _, out _));   // 단일 소비자 계약: 2번째 동시 소비는 즉시 거부
        buf.EndConsume();

        Assert.True(buf.TryBeginConsume(out _, out var latest));
        Assert.Equal(101, latest.Sequence);   // 항상 최신 게시본
        buf.EndConsume();
        Assert.Equal(99, buf.Overwritten);
        Assert.Equal(0, buf.StepRegressions);
    }

    [Fact]
    public void DoubleBuffer_ConcurrentSpsc_NoTornFrames_NoSequenceRegression()
    {
        const int Frames = 20000, Bytes = 4096;
        var buf = new WanFrameDoubleBuffer(Bytes);
        var producerDone = 0;
        var publishFailures = 0;
        var torn = 0;
        var nonMonotonic = 0;
        var producer = new Thread(() =>
        {
            var f = new byte[Bytes];
            for (var s = 1; s <= Frames; s++)
            {
                Array.Fill(f, (byte)s);
                BitConverter.TryWriteBytes(f.AsSpan(0, 8), (long)s);
                if (!buf.TryPublish(f, 32, 32, VirtualCamPixelFormat.Rgba32, out _)) Interlocked.Increment(ref publishFailures);
            }
            Volatile.Write(ref producerDone, 1);
        });
        var consumer = new Thread(() =>
        {
            long last = 0;
            while (Volatile.Read(ref producerDone) == 0 || buf.HasPending)
            {
                if (!buf.TryBeginConsume(out var fr, out var info)) { Thread.SpinWait(4); continue; }
                try
                {
                    var stamped = BitConverter.ToInt64(fr[..8]);
                    if (stamped != info.Sequence || fr[Bytes - 1] != (byte)stamped || fr[Bytes / 2] != (byte)stamped) Interlocked.Increment(ref torn);
                    if (stamped <= last) Interlocked.Increment(ref nonMonotonic);
                    last = stamped;
                }
                finally { buf.EndConsume(); }
            }
        });
        consumer.Start(); producer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(20)));
        Assert.True(consumer.Join(TimeSpan.FromSeconds(20)));

        Assert.Equal(0, publishFailures);
        Assert.Equal(0, torn);
        Assert.Equal(0, nonMonotonic);
        Assert.Equal(0, buf.StepRegressions);
        Assert.Equal(Frames, buf.Published);
        Assert.Equal(Frames, buf.Consumed + buf.Overwritten);   // 게시 1건 = 소비 또는 덮어씀(백프레셔) 정확히 1건
    }

    [Fact]
    public void PacedPump_InterpolatesBetweenKeyframesByPhase_ThenHoldsLatest()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var reader = new LiveStreamVirtualCamPipe(map, mutex);
        var a = new byte[W * H * 4];
        var b = new byte[W * H * 4]; Array.Fill(b, (byte)200);
        var back = new byte[a.Length];
        const long T0 = 1_000_000, P = 1_000;

        Assert.True(pipe.StageFrame(a, W, H, VirtualCamPixelFormat.Rgba32));
        Assert.True(pipe.PumpPacedTick(T0));                     // 첫 키프레임 그대로
        Assert.False(pipe.PumpPacedTick(T0 + 10));               // 새 키프레임 없음 → 홀드(재송출 0)

        Assert.True(pipe.StageFrame(b, W, H, VirtualCamPixelFormat.Rgba32));
        Assert.True(pipe.PumpPacedTick(T0 + P));                 // 간격 P 확정, 위상 0 = 직전 키프레임
        Assert.True(reader.TryReadLatestFrame(back, out _, out _));
        Assert.All(back, x => Assert.Equal(0, x));

        Assert.True(pipe.PumpPacedTick(T0 + P + P / 2));         // 위상 128 → (0·128 + 200·128 + 128) >> 8 = 100
        Assert.True(reader.TryReadLatestFrame(back, out _, out _));
        Assert.All(back, x => Assert.Equal(100, x));

        Assert.True(pipe.PumpPacedTick(T0 + 2 * P));             // 위상 256 = 최신 키프레임
        Assert.True(reader.TryReadLatestFrame(back, out _, out _));
        Assert.All(back, x => Assert.Equal(200, x));
        Assert.False(pipe.PumpPacedTick(T0 + 3 * P));            // 최종 위상 송출 후 홀드
        Assert.Equal(2, pipe.InterpolatedEmits);
        Assert.Equal(0, pipe.Staging.StepRegressions);
    }

    [Fact]
    public async Task StartPump_PassThrough_DeliversStagedFrameOffProducerThread()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        using var reader = new LiveStreamVirtualCamPipe(map, mutex);
        var g = new WanQualitySpeedGovernor(100);
        pipe.AttachGovernor(g);
        Assert.True(pipe.StartPump());
        Assert.False(pipe.StartPump());   // 중복 기동 차단

        var frame = new byte[W * H * 4]; Array.Fill(frame, (byte)77);
        Assert.True(pipe.StageFrame(frame, W, H, VirtualCamPixelFormat.Rgba32));

        var back = new byte[frame.Length];
        var delivered = await Task.Run(() => SpinWait.SpinUntil(() => reader.TryReadLatestFrame(back, out _, out _), TimeSpan.FromSeconds(5)));
        Assert.True(delivered);
        Assert.Equal(frame, back);
        pipe.StopPump();
        Assert.False(pipe.PumpRunning);
        Assert.Equal(1, pipe.FramesSubmitted);
    }

    [Fact]
    public void ThousandFrameChain_StageGovernBlendPump_DoesNotAllocate()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        var g = new WanQualitySpeedGovernor(100);
        pipe.AttachGovernor(g);
        var frame = new byte[W * H * 4];
        new Random(15).NextBytes(frame);
        var smooth = new byte[frame.Length];
        long ts = 1_000_000;
        var i = 0;

        void Iteration()
        {
            frame[i & 0xFFF] ^= 0x5A;
            g.RecordLatency(i % 97 == 0 ? 400 : 40 + (i & 31));   // 간헐 스파이크 → 하향/상향 전이 포함
            pipe.StageFrame(frame, W, H, VirtualCamPixelFormat.Rgba32);
            pipe.PumpPacedTick(ts);
            pipe.PumpPacedTick(ts + 500);                           // 키프레임 사이 위상 보간 송출
            WanPostProcessingEngine.SmoothTemporalInPlace(smooth, frame, 192);
            ts += 1000; i++;
        }

        for (var k = 0; k < 64; k++) Iteration();   // 워밍업(JIT 티어링·슬롯 확보)
        var allocated = AllocationProbe.MinAllocated(() => { for (var k = 0; k < 1000; k++) Iteration(); });

        Assert.Equal(0, allocated);
        Assert.True(pipe.InterpolatedEmits >= 1000);
        Assert.True(g.Preemptions > 0);
        Assert.Equal(0, pipe.Staging.StepRegressions);
    }
}
