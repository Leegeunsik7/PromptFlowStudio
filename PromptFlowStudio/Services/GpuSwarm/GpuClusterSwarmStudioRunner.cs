namespace PromptFlowStudio.Services.GpuSwarm;

/// <summary>
/// [PromptFlow Studio v16.96.0] ⚡ AI 분산 GPU 클러스터 &amp; 렌더 스웜 스튜디오 러너.
/// 노드 4종: 로컬 CPU SIMD(레인 = 코어/2) · LAN Vulkan · 클라우드 DX12 · 클라우드 Metal — 원격 노드는 인프로세스 시뮬레이션(실 연산 + RTT/대역폭/지터 지연).
/// 전용 렌더 스레드(≤ 30 fps)에서 분산 프레임 렌더·조립 · 워크로드(패스트레이싱/디퓨전) · 연결/무통보 단절 토글 · 프레임 배치 계획.
/// </summary>
public sealed class GpuClusterSwarmStudioRunner : IDisposable
{
    public const int BatchFrames = 24, MinFrameMs = 33;

    private readonly object _gate = new();
    private readonly int[] _batch = new int[BatchFrames];
    private readonly double[] _batchLoad = new double[DistributedHeterogeneousGpuTopologyEngine.MaxNodes];
    private readonly PathTraceSwarmWorkload _pt;
    private readonly DiffusionSwarmWorkload _diff;
    private Thread? _loop;
    private volatile bool _running;
    private bool _nodesStarted;
    private long _frame;

    public GpuClusterSwarmStudioRunner(IServiceProvider sp)
        : this((DistributedHeterogeneousGpuTopologyEngine?)sp.GetService(typeof(DistributedHeterogeneousGpuTopologyEngine)) ?? new DistributedHeterogeneousGpuTopologyEngine(),
               sp.GetService(typeof(DynamicCloudRenderSwarmLoadBalancer)) as DynamicCloudRenderSwarmLoadBalancer,
               sp.GetService(typeof(LowLatencyFrameAssemblyFallbackGuard)) as LowLatencyFrameAssemblyFallbackGuard) { }

    public GpuClusterSwarmStudioRunner(DistributedHeterogeneousGpuTopologyEngine topo, DynamicCloudRenderSwarmLoadBalancer? lb, LowLatencyFrameAssemblyFallbackGuard? guard, int width = 160, int height = 90)
    {
        Topology = topo;
        Balancer = lb ?? new DynamicCloudRenderSwarmLoadBalancer(topo);
        Guard = guard ?? new LowLatencyFrameAssemblyFallbackGuard(topo, Balancer);
        _pt = PathTraceSwarmWorkload.CreateDemo(width, height);
        _diff = new DiffusionSwarmWorkload(width, height);
        LocalLanes = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        LocalNode = topo.AddNode("로컬 CPU SIMD", GpuBackend.CpuSimd, NodeLink.Local, Math.Min(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 8L << 30), LocalLanes, 0.3f);
        LanNode = topo.AddNode("LAN 워크스테이션", GpuBackend.Vulkan, NodeLink.Lan, 8L << 30, 2, 0.3f, 1100f, 1.5f);
        CloudA = topo.AddNode("클라우드 A100", GpuBackend.Dx12, NodeLink.Cloud, 40L << 30, 3, 0.3f, 120f, 18f);
        CloudB = topo.AddNode("클라우드 Mac Studio", GpuBackend.Metal, NodeLink.Cloud, 24L << 30, 2, 0.3f, 80f, 28f);
        Guard.SetWorkload(_pt);
    }

    public event Action? OnChange;

    public DistributedHeterogeneousGpuTopologyEngine Topology { get; }
    public DynamicCloudRenderSwarmLoadBalancer Balancer { get; }
    public LowLatencyFrameAssemblyFallbackGuard Guard { get; }
    public int LocalNode { get; }
    public int LanNode { get; }
    public int CloudA { get; }
    public int CloudB { get; }
    public int LocalLanes { get; }
    public bool IsRunning => _running;
    public long Frame => Interlocked.Read(ref _frame);
    public string? LastAction { get; private set; }
    public double Fps { get; private set; }
    public int WorkloadIndex { get; private set; }
    public double BatchMakespanMs { get; private set; }
    public int BatchFrame(int i) => _batch[i];

    /// <summary>노드 워커 1회 기동(로컬 레인 + 원격 시뮬레이션 링크).</summary>
    public void EnsureNodes()
    {
        lock (_gate)
        {
            if (_nodesStarted) return;
            _nodesStarted = true;
            Topology.CalibrateCpu();
            Guard.StartNode(LocalNode, LocalLanes, null);
            Guard.StartNode(LanNode, 2, new SimulatedLink { RttMs = 1.5, BandwidthMBs = 1100, JitterMs = 0.3 });
            Guard.StartNode(CloudA, 3, new SimulatedLink { RttMs = 18, BandwidthMBs = 120, JitterMs = 4 });
            Guard.StartNode(CloudB, 2, new SimulatedLink { RttMs = 28, BandwidthMBs = 80, JitterMs = 6 });
        }
    }

    public void SetWorkload(int idx)
    {
        lock (_gate)
        {
            WorkloadIndex = idx == 1 ? 1 : 0;
            Guard.SetWorkload(WorkloadIndex == 1 ? _diff : _pt);
            Balancer.ResetCostMap();
            LastAction = "🎞 워크로드 → " + Guard.Workload!.Name;
        }
        Raise();
    }

    public void SetConnected(int node, bool on)
    {
        Guard.SetConnected(node, on);
        LastAction = (on ? "🔌 연결 " : "✂ 단절 ") + Topology.Name(node);
        Raise();
    }

    public void SetSilent(int node, bool on)
    {
        var l = Guard.LinkOf(node);
        if (l is null) return;
        l.Silent = on;
        LastAction = (on ? "📵 무통보 단절 " : "📶 링크 복구 ") + Topology.Name(node);
        Raise();
    }

    /// <summary>1프레임 분산 렌더(동기).</summary>
    public SwarmFrameResult RenderOnce()
    {
        EnsureNodes();
        var f = Interlocked.Increment(ref _frame);
        var r = Guard.RenderFrame(f);
        if (r.Fallbacks > 0) LastAction = "↩ 페일백 " + r.Fallbacks + " 타일 → 로컬 (" + r.FallbackMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms)";
        return r;
    }

    /// <summary>프레임 배치 LPT 계획(현재 프레임 비용 기준).</summary>
    public void PlanBatch()
    {
        var w = Guard.Width; var h = Guard.Height;
        BatchMakespanMs = Balancer.PlanFrames(BatchFrames, Balancer.RegionCost(0, 0, w, h), w * h * 3 * sizeof(float), _batch, _batchLoad);
        LastAction = "🗂 " + BatchFrames + "프레임 배치 계획 · 예측 " + BatchMakespanMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        Raise();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            EnsureNodes();
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.GPU.Swarm" };
            _loop.Start();
            LastAction = "▶ 렌더 스웜 가동 · 노드 " + Topology.Count;
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(6000);
        LastAction = "⏸ 렌더 스웜 정지";
        Raise();
    }

    private void Loop()
    {
        while (_running)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { RenderOnce(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("GpuSwarm.Loop", ex); }
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (ms < MinFrameMs) Thread.Sleep((int)(MinFrameMs - ms));
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Fps = Fps <= 0 ? 1000 / total : Fps * 0.8 + 0.2 * (1000 / total);
            Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("GpuSwarm.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Guard.Dispose();
    }
}
