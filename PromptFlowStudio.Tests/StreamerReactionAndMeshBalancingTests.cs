using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>v12.2.0-Phase3: 리액션 오버레이(10ms 고정스텝·0-Alloc) 회귀 잠금 - 벽시계 단언 없이 스텝 수/상태로 판정.</summary>
[Trait("Category", "FastUnit")]
public class StreamerReactionOverlayTests
{
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
    public void ReactionOverlay_StepRenderCopy_HotPathDoesNotAllocate()
    {
        var audio = new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler());
        using var engine = new StreamerReactionEngine(audio, new ReactionOverlayPipeline(), () => 0);
        var rgba = new byte[128 * 128 * 4];
        var particles = new OverlayParticle[ReactionOverlayPipeline.DefaultCapacity];

        void Iteration(int i)
        {
            if (i % 20 == 0) engine.TriggerManual((ReactionAffect)(2 + (i / 20 & 1)), 0.7);   // 200ms 마다 Excited/Hype 교대 분출
            engine.Advance(ReactionOverlayPipeline.StepMs);
            engine.RenderOverlayInto(rgba, 128, 128);
            engine.CopyParticles(particles);
        }

        for (var i = 0; i < 100; i++) Iteration(i);   // 워밍업(JIT 티어링)

        var allocated = AllocationProbe.MinAllocated(() => { for (var i = 0; i < 1000; i++) Iteration(i); });   // 10ms × 1000 = 10초 분량

        Assert.Equal(0, allocated);
        Assert.True(engine.Ticks >= 1100);
        Assert.True(engine.ParticlesSpawned > 0);
    }

    [Fact]
    public void Pipeline_SameSeedSameImpulses_ProducesBitIdenticalParticles()
    {
        var a = new ReactionOverlayPipeline(64, seed: 42);
        var b = new ReactionOverlayPipeline(64, seed: 42);
        for (var i = 0; i < 50; i++)
        {
            var imp = i % 10 == 0 ? new ReactionImpulse(12, 0.6f, ReactionAffect.Hype) : default;
            a.Step(imp); b.Step(imp);
        }
        var pa = new OverlayParticle[64]; var pb = new OverlayParticle[64];
        var na = a.CopyParticles(pa); var nb = b.CopyParticles(pb);
        Assert.Equal(na, nb);
        Assert.True(na > 0);
        for (var i = 0; i < na; i++) Assert.Equal(pa[i], pb[i]);
    }

    [Fact]
    public void Pipeline_BurstBeyondCapacity_CapsActiveAndCountsDropsWithoutThrowing()
    {
        var p = new ReactionOverlayPipeline(capacity: 16);
        p.Step(new ReactionImpulse(100, 1f, ReactionAffect.Hype));
        Assert.Equal(16, p.ActiveParticles);
        Assert.Equal(16, p.Spawned);
        Assert.Equal(84, p.Dropped);
    }

    [Fact]
    public void Pipeline_AfterQuietPeriod_DecaysToCalmWithNoParticlesAndBounceSettled()
    {
        var p = new ReactionOverlayPipeline();
        p.Step(new ReactionImpulse(40, 0.9f, ReactionAffect.Hype));
        Assert.Equal(ReactionAffect.Hype, p.Affect);
        Assert.True(p.Snapshot.BounceScale > 1f || p.Snapshot.BounceScale < 1f || p.ActiveParticles > 0);

        for (var i = 0; i < 300; i++) p.Step(default);   // 3초 무입력(최대 수명 1.5초·뱃지 1.5초보다 김)

        var s = p.Snapshot;
        Assert.Equal(0, s.ActiveParticles);
        Assert.Equal(ReactionAffect.Calm, s.Affect);
        Assert.Equal(0f, s.BadgeAlpha);
        Assert.Equal(1f, s.BounceScale);
    }

    [Fact]
    public void Advance_FixedTenMsStep_CarriesRemainderAndCapsCatchUp()
    {
        var audio = new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler());
        using var engine = new StreamerReactionEngine(audio, new ReactionOverlayPipeline(), () => 0);
        Assert.Equal(3, engine.Advance(35));   // 30ms 소비, 5ms 이월
        Assert.Equal(1, engine.Advance(5));    // 이월 5 + 5 = 10
        Assert.Equal(0, engine.Advance(9.9));
        Assert.Equal((int)(StreamerReactionEngine.MaxCatchUpMs / StreamerReactionEngine.TickMs), engine.Advance(10_000));   // 장시간 정지 후 폭주 방지
        Assert.Equal(4 + 20, engine.Ticks);
    }

    [Fact]
    public void AudioBeat_FlowsThroughOnFrameAnalyzed_IntoExcitedOrHypeOverlay()
    {
        var audio = new MultimodalAudioSyncEngine(new WanDynamicMotionController(), new HeterogeneousTaskScheduler());
        using var engine = new StreamerReactionEngine(audio, new ReactionOverlayPipeline(), () => 0);
        var quiet = SinePcm16(440, 48000, 1024, 0.05);
        var loud = SinePcm16(440, 48000, 1024, 0.9);

        for (var i = 0; i < 10; i++) audio.AnalyzeFrame(quiet, 48000);
        engine.Advance(10);
        Assert.Equal(0, engine.Snapshot.ActiveParticles);   // 조용한 구간은 분출 없음

        var f = audio.AnalyzeFrame(loud, 48000);
        Assert.True(f.BeatDetected);
        engine.Advance(10);

        var s = engine.Snapshot;
        Assert.Equal(11, engine.AudioFramesObserved);
        Assert.True(engine.ImpulsesQueued >= 1);
        Assert.True(s.ActiveParticles > 0);
        Assert.True(s.Affect >= ReactionAffect.Excited);
        Assert.Equal(1f, s.BadgeAlpha, 1);
    }

    [Theory]
    [InlineData(0.01, 1.0, false, ReactionAffect.Calm)]
    [InlineData(0.10, 1.0, false, ReactionAffect.Engaged)]
    [InlineData(0.25, 1.5, false, ReactionAffect.Excited)]
    [InlineData(0.10, 3.0, true, ReactionAffect.Excited)]
    [InlineData(0.50, 3.0, true, ReactionAffect.Hype)]
    public void Classify_DeterministicHeuristicTable(double amp, double ratio, bool beat, ReactionAffect expected)
        => Assert.Equal(expected, StreamerReactionEngine.Classify(amp, ratio, beat));
}

/// <summary>v12.2.0-Phase3: P2P 메시 부하분산/원자 페일오버 - 시계 주입으로 무응답 판정까지 결정적 재현.</summary>
[Trait("Category", "FastUnit")]
public class P2pLoadBalancerTests
{
    private sealed class FakeClock
    {
        public long Now = 1_000_000;
        public void AdvanceMs(double ms) => Now += (long)(ms * Stopwatch.Frequency / 1000.0);
    }

    // realClock: 페일오버 소요(ms) 실측이 필요한 테스트용 - 무응답 한도를 크게 잡아 부하 시 샘플 간격으로 인한 오판 차단.
    private static (P2pLoadBalancer lb, FakeClock clk) BuildThreeNodeMesh(bool realClock = false)
    {
        var clk = new FakeClock();
        var lb = realClock ? new P2pLoadBalancer { StaleAfterMs = 60_000 } : new P2pLoadBalancer(() => clk.Now);
        lb.AddNode("A"); lb.AddNode("B"); lb.AddNode("C");
        Feed(lb);
        lb.Rebalance();
        return (lb, clk);
    }

    private static void Feed(P2pLoadBalancer lb, bool a = true, bool b = true, bool c = true)
    {
        for (var i = 0; i < 8; i++)
        {
            if (a) lb.RecordSample(0, 10, 900);
            if (b) lb.RecordSample(1, 30, 600);
            if (c) lb.RecordSample(2, 80, 300);
        }
    }

    [Fact]
    public void Rebalance_PicksLowestCostPrimaryAndBackup_WithInverseCostShare()
    {
        var (lb, _) = BuildThreeNodeMesh();
        var r = lb.Route;
        Assert.Equal(0, r.PrimaryIndex);
        Assert.Equal(1, r.BackupIndex);
        Assert.InRange(r.PrimarySharePct, 50, 100);

        var toPrimary = 0;
        const int Flows = 20_000;
        for (ulong k = 0; k < Flows; k++) if (lb.SelectRoute(k).NodeIndex == 0) toPrimary++;
        Assert.InRange(toPrimary * 100.0 / Flows, r.PrimarySharePct - 3, r.PrimarySharePct + 3);   // 해시 버킷 분배가 게시 비율을 따른다
        Assert.Equal(Flows, lb.RoutedRequests);
    }

    [Fact]
    public void SelectRoute_SameFlowKey_IsStickyWhileTableUnchanged()
    {
        var (lb, _) = BuildThreeNodeMesh();
        for (ulong k = 0; k < 500; k++)
        {
            var first = lb.SelectRoute(k).NodeIndex;
            for (var j = 0; j < 3; j++) Assert.Equal(first, lb.SelectRoute(k).NodeIndex);
        }
    }

    [Fact]
    public void SelectRoute_HotPathDoesNotAllocate()
    {
        var (lb, _) = BuildThreeNodeMesh();
        for (ulong k = 0; k < 1000; k++) lb.SelectRoute(k);
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { for (ulong k = 0; k < 100_000; k++) lb.SelectRoute(k); }));
    }

    [Fact]
    public void ReportFailure_OnPrimary_AtomicallyPromotesBackupWithinFiveMs_AndReelectsBackup()
    {
        var (lb, _) = BuildThreeNodeMesh(realClock: true);
        var gen0 = lb.Route.Generation;

        Assert.True(lb.ReportFailure(0));

        var r = lb.Route;
        Assert.Equal(1, r.PrimaryIndex);   // 이전 Backup 승격
        Assert.Equal(2, r.BackupIndex);    // 남은 노드에서 새 Backup 재선출
        Assert.True(r.Generation > gen0);
        Assert.Equal(1, lb.Failovers);
        Assert.InRange(lb.LastFailoverMs, 0, 5);   // 검출(통보) → 게시 소요 - 스펙 5ms 한도
        for (ulong k = 0; k < 2000; k++) Assert.NotEqual(0, lb.SelectRoute(k).NodeIndex);   // 이탈 노드로는 단 1건도 가지 않음
    }

    [Fact]
    public void ReportFailure_OnBackupOnly_DoesNotCountAsFailoverButReplacesBackup()
    {
        var (lb, _) = BuildThreeNodeMesh();
        Assert.False(lb.ReportFailure(1));
        Assert.Equal(0, lb.Route.PrimaryIndex);
        Assert.Equal(2, lb.Route.BackupIndex);
        Assert.Equal(0, lb.Failovers);
    }

    [Fact]
    public void SilentNodeDeparture_DetectedAfterStaleWindow_FailsOverOnNextProbeTick()
    {
        var (lb, clk) = BuildThreeNodeMesh();
        clk.AdvanceMs(20);
        Feed(lb, a: false);            // A 만 응답 중단
        lb.ProbeTick();
        Assert.Equal(0, lb.Route.PrimaryIndex);   // 20ms < 30ms 무응답 한도 - 아직 유지

        clk.AdvanceMs(15);             // 누적 35ms 무응답
        Feed(lb, a: false);
        lb.ProbeTick();
        Assert.Equal(1, lb.Route.PrimaryIndex);
        Assert.Equal(1, lb.Failovers);
        Assert.Equal(MeshNodeHealth.Down, lb.FingerprintAt(0).Health);

        Feed(lb);                      // A 복귀 → 다음 재구성에서 다시 최저 비용 Primary
        lb.ProbeTick();
        Assert.Equal(0, lb.Route.PrimaryIndex);
    }

    [Fact]
    public void ProbeDelegate_ConsecutiveLosses_MarkNodeDownAndReroute()
    {
        var clk = new FakeClock();
        using var lb = new P2pLoadBalancer(() => clk.Now);
        lb.AddNode("A"); lb.AddNode("B");
        var aAlive = true;
        (bool, double, double) Probe(int i) => i == 0 ? (aAlive, 5, 900) : (true, 40, 500);
        lb.SetProbe(Probe);   // 타이머 없이 ProbeTick 직접 구동(타이머 콜백과 동일 본체)
        lb.ProbeTick();
        Assert.Equal(0, lb.Route.PrimaryIndex);

        aAlive = false;
        for (var i = 0; i < 3; i++) lb.ProbeTick();   // 연속 3회 유실 → Down
        Assert.Equal(1, lb.Route.PrimaryIndex);
        Assert.Equal(-1, lb.Route.BackupIndex);
        Assert.Equal(100, lb.Route.PrimarySharePct);
    }

    [Fact]
    public async Task ConcurrentReadersDuringRepeatedFailovers_NeverSeeInvalidOrRegressedTable()
    {
        var (lb, _) = BuildThreeNodeMesh(realClock: true);
        var stop = 0;
        var invalid = 0;
        var regressed = 0;
        var readers = new Task[Math.Max(2, Environment.ProcessorCount - 1)];
        for (var t = 0; t < readers.Length; t++)
        {
            var seed = (ulong)t * 7919;
            readers[t] = Task.Run(() =>
            {
                uint lastGen = 0;
                ulong k = seed;
                while (Volatile.Read(ref stop) == 0)
                {
                    var c = lb.SelectRoute(k++);
                    if (c.NodeIndex < 0 || c.NodeIndex > 2) Interlocked.Increment(ref invalid);
                    if (c.Generation < lastGen) Interlocked.Increment(ref regressed);
                    lastGen = c.Generation;
                }
            });
        }

        for (var i = 0; i < 500; i++)
        {
            var victim = i % 3;
            lb.ReportFailure(victim);
            lb.RecordSample(victim, 10 + victim * 20, 900 - victim * 300);   // 즉시 복귀(강제 Down 해제)
            lb.Rebalance();
        }
        Volatile.Write(ref stop, 1);
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, invalid);     // 항상 2개 이상 건강 노드 → 경로 없음(-1)·범위 밖 인덱스 관측 0
        Assert.Equal(0, regressed);   // 패킹 워드 원자 게시 → 세대 역행(반쪽/구버전 테이블) 관측 0
        Assert.True(lb.Failovers > 0);
        Assert.InRange(lb.MaxFailoverMs, 0, 5);
    }
}

[Trait("Category", "FastUnit")]
public class MeshRelayNodeTests
{
    [Fact]
    public void RingBuffer_WrapsAtCapacity_AndP95ReflectsOnlyRecentWindow()
    {
        var n = new MeshRelayNode("X", ringCapacity: 8);
        for (var i = 1; i <= 20; i++) n.RecordSample(i, 100, timestampTicks: 10);
        var fp = n.Fingerprint(nowTicks: 10, staleAfterTicks: 1000);
        Assert.Equal(8, fp.Samples);
        Assert.Equal(20, fp.P95LatencyMs);   // 최근 8개(13..20)의 p95 = ceil(7.6)=8번째 = 20
        Assert.Equal(MeshNodeHealth.Healthy, fp.Health);
        Assert.True(fp.JitterMs > 0);
    }

    [Fact]
    public void Health_ThreeLossesOrMarkDown_IsDownUntilNextSuccessfulSample()
    {
        var n = new MeshRelayNode("X");
        n.RecordSample(10, 500, 0);
        n.RecordLoss();
        Assert.Equal(MeshNodeHealth.Degraded, n.Fingerprint(0, 100).Health);
        n.RecordLoss(); n.RecordLoss();
        var down = n.Fingerprint(0, 100);
        Assert.Equal(MeshNodeHealth.Down, down.Health);
        Assert.True(double.IsPositiveInfinity(down.Cost));

        n.RecordSample(10, 500, 0);
        Assert.Equal(MeshNodeHealth.Healthy, n.Fingerprint(0, 100).Health);
        n.MarkDown();
        Assert.Equal(MeshNodeHealth.Down, n.Fingerprint(0, 100).Health);
        n.RecordSample(10, 500, 0);
        Assert.Equal(MeshNodeHealth.Healthy, n.Fingerprint(0, 100).Health);
    }

    [Fact]
    public void RecordSample_RejectsInvalidSchemaValues()
    {
        var n = new MeshRelayNode("X");
        n.RecordSample(double.NaN, 100, 0);
        n.RecordSample(-1, 100, 0);
        n.RecordSample(10, -5, 0);
        Assert.Equal(0, n.Fingerprint(0, 100).Samples);
    }
}

/// <summary>v12.2.0-Phase4: EdgeP2PTaskRelay 실 UDP Ping/Pong → P2pLoadBalancer 결선 - 루프백 실소켓, 신호(TCS) 기반 동기화.</summary>
public class EdgeRelayProbeBinderTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(45);

    private static Task NextRtt(EdgeP2PTaskRelay relay, string fromNodeId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(PeerRttSample s) { if (s.NodeId == fromNodeId && tcs.TrySetResult()) relay.OnPeerRtt -= Handler; }
        relay.OnPeerRtt += Handler;
        return tcs.Task;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task RealUdpPingPong_RttSampleFlowsIntoBalancerAsNewNode()
    {
        using var a = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        using var b = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        a.Start(); b.Start();
        using var lb = new P2pLoadBalancer();
        using var binder = new EdgeRelayProbeBinder(lb, a);
        lb.StaleAfterMs = 60_000;   // RTT 기록 경로만 검증(병렬 부하 시 기록→Rebalance 사이 30 ms 벽시계 무응답 판정 개입 차단)

        var rtt = NextRtt(a, b.NodeId);   // binder 가 먼저 구독(생성자) → 이 신호 시점엔 이미 기록 완료
        await a.PingAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, b.BoundDiscoveryPort));
        await rtt.WaitAsync(SafetyNet);

        var idx = binder.IndexOf(b.NodeId);
        Assert.True(idx >= 0);
        var fp = lb.FingerprintAt(idx);
        Assert.Equal(1, fp.Samples);
        Assert.InRange(fp.EwmaLatencyMs, 0, 60_000);
        Assert.Equal(1, binder.RttSamples);
        Assert.Equal(idx, lb.Rebalance().PrimaryIndex);
        Assert.Contains(a.Peers, p => p.NodeId == b.NodeId && p.UdpPort == b.BoundDiscoveryPort);   // 유니캐스트 프로브 대상 포트 기록
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PeerDeparture_ConsecutiveUnansweredPings_TriggersAtomicFailoverToBackup()
    {
        using var a = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        var b = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        a.Start(); b.Start();
        using var lb = new P2pLoadBalancer();
        using var binder = new EdgeRelayProbeBinder(lb, a, pingIntervalMs: 50);
        lb.StaleAfterMs = 60_000;   // 이 테스트는 "손실 기반" 경로만 검증(부하 시 벽시계 무응답 판정 개입 차단)

        var first = NextRtt(a, b.NodeId);
        await a.PingAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, b.BoundDiscoveryPort));
        await first.WaitAsync(SafetyNet);
        var peerIdx = binder.IndexOf(b.NodeId);

        var backupIdx = lb.AddNode("synthetic-backup");
        // 부하 시 루프백 RTT 에 스레드 스케줄링 지연이 섞여 수백 ms 까지 부풀 수 있다 - 손실 판정 전 재구성이 먼저 백업으로 넘어가지 않도록 충분히 느린 백업.
        lb.RecordSample(backupIdx, 5000, EdgeRelayProbeBinder.UnmeasuredBandwidthMbps);
        Assert.Equal(peerIdx, lb.Rebalance().PrimaryIndex);   // 루프백 RTT(≪5000ms) 피어가 Primary

        // 정상 주기 1회: Ping 발신 → Pong 으로 미응답 표시 해제
        var second = NextRtt(a, b.NodeId);
        binder.Tick();
        await second.WaitAsync(SafetyNet);
        Assert.Equal(0, binder.LossesRecorded);

        b.Dispose();   // 피어 이탈(무통보)

        binder.Tick();   // 죽은 피어로 Ping - 미응답 표시만
        Assert.Equal(peerIdx, lb.Route.PrimaryIndex);
        binder.Tick();   // 손실 1
        binder.Tick();   // 손실 2
        Assert.Equal(peerIdx, lb.Route.PrimaryIndex);
        binder.Tick();   // 손실 3 → ReportFailure 원자 전환

        Assert.Equal(3, binder.LossesRecorded);
        Assert.Equal(1, binder.FailoversTriggered);
        Assert.Equal(backupIdx, lb.Route.PrimaryIndex);
        Assert.InRange(lb.LastFailoverMs, 0, 5);
        var fp = lb.FingerprintAt(peerIdx);
        Assert.Equal(MeshNodeHealth.Down, fp.Health);
        Assert.True(fp.LossPct > 0);
        for (ulong k = 0; k < 1000; k++) Assert.NotEqual(peerIdx, lb.SelectRoute(k).NodeIndex);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LegacyThreeFieldPing_IsStillAnsweredWithThreeFieldPong()
    {
        using var b = new EdgeP2PTaskRelay(discoveryPort: 0, tcpPort: 0);
        b.Start();
        using var client = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var ping = System.Text.Encoding.UTF8.GetBytes("PFSP2P-PING|legacy-node|1234");
        await client.SendAsync(ping, ping.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, b.BoundDiscoveryPort));

        using var cts = new CancellationTokenSource(SafetyNet);
        var resp = await client.ReceiveAsync(cts.Token);
        var parts = System.Text.Encoding.UTF8.GetString(resp.Buffer).Split('|');
        Assert.Equal(3, parts.Length);   // 구형 피어엔 구형 형식 그대로(타임스탬프 필드 미부가)
        Assert.Equal("PFSP2P-PONG", parts[0]);
        Assert.Equal(b.NodeId, parts[1]);
    }
}
