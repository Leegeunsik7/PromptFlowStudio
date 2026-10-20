using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Edge;

/// <summary>
/// [PromptFlow Studio v16.64.0] 🌐 에지 스웜 &amp; 글로벌 메시 스튜디오 러너 — 데모 글로벌 메시(서울 스튜디오 → 에지 PoP → 뉴욕 시청자) + 스웜 노드 매핑.
/// 틱: 하트비트(침묵 노드 제외) → 워크로드 투입(렌더 타일/추론/인코드) → 배정 → 시계 진행(완료 · 타임아웃 이탈 → 메시 우회) → 미디어 프레임 메시 전송(ABR).
/// 30Hz PeriodicTimer · <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class EdgeSwarmStudioRunner : IDisposable
{
    public const int TickHz = 30, TilesPerFrame = 8;
    // 능력: cpu · gpu · npu · mem · battery · thermal · link · trust
    private static readonly float[] DesktopCap = { 0.9f, 0.95f, 0.3f, 0.9f, 1f, 0.9f, 0.9f, 1f };
    private static readonly float[] MobileCap = { 0.5f, 0.4f, 0.9f, 0.4f, 0.8f, 0.7f, 0.6f, 0.9f };
    private static readonly float[] CloudCap = { 1f, 1f, 0.2f, 1f, 1f, 1f, 0.95f, 0.8f };
    private static readonly float[] EdgeCap = { 0.7f, 0.6f, 0.5f, 0.7f, 1f, 0.85f, 0.85f, 0.85f };

    private readonly object _gate = new();
    private readonly AiEdgeSwarmNodeOrchestrator _swarm;
    private readonly RealtimeGlobalMediaMeshRouter _mesh;
    private readonly EdgeAutonomousAiAgentEngine _agent;
    private readonly byte[] _hb = new byte[AiEdgeSwarmNodeOrchestrator.MaxNodes * SwarmHeartbeat.Size];
    private readonly bool[] _silenced = new bool[AiEdgeSwarmNodeOrchestrator.MaxNodes];
    private readonly float[] _latHist = new float[120];
    private CancellationTokenSource? _cts;
    private int _flow = -1, _source, _viewer, _histHead;
    private long _frameSeq, _deliveredFrames;
    private MeshDelivery _last;

    public EdgeSwarmStudioRunner(IServiceProvider sp)
        : this((AiEdgeSwarmNodeOrchestrator?)sp.GetService(typeof(AiEdgeSwarmNodeOrchestrator)) ?? new AiEdgeSwarmNodeOrchestrator(),
               sp.GetService(typeof(RealtimeGlobalMediaMeshRouter)) as RealtimeGlobalMediaMeshRouter,
               (EdgeAutonomousAiAgentEngine?)sp.GetService(typeof(EdgeAutonomousAiAgentEngine)) ?? new EdgeAutonomousAiAgentEngine()) { }

    public EdgeSwarmStudioRunner(AiEdgeSwarmNodeOrchestrator swarm, RealtimeGlobalMediaMeshRouter? mesh, EdgeAutonomousAiAgentEngine agent)
    {
        _swarm = swarm; _agent = agent;
        if (mesh is null || mesh.NodeCount == 0) { mesh = RealtimeGlobalMediaMeshRouter.BuildDemo(out _source, out _viewer); }
        else { _source = 0; _viewer = mesh.NodeCount - 1; }
        _mesh = mesh;
        if (_swarm.NodeCount == 0) SeedSwarm();
        _swarm.AttachMesh(_mesh, _source);
        _flow = _mesh.OpenFlow(_source, _viewer, 6000);
    }

    public event Action? OnChange;

    public AiEdgeSwarmNodeOrchestrator Swarm => _swarm;
    public RealtimeGlobalMediaMeshRouter Mesh => _mesh;
    public EdgeAutonomousAiAgentEngine Agent => _agent;
    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public long DeliveredFrames => Interlocked.Read(ref _deliveredFrames);
    public int Flow => _flow;
    public int SourceNode => _source;
    public int ViewerNode => _viewer;
    public MeshDelivery LastDelivery => _last;
    public int Fps { get; set; } = TickHz;
    public string? LastAction { get; private set; }

    /// <summary>메시 노드 → 스웜 노드(데모 능력 프로파일).</summary>
    private void SeedSwarm()
    {
        for (var m = 0; m < _mesh.NodeCount; m++)
        {
            var role = _mesh.RoleOf(m);
            if (role == MeshRole.Viewer) continue;
            var cap = role switch
            {
                MeshRole.Desktop => DesktopCap,
                MeshRole.Mobile => MobileCap,
                MeshRole.CloudEdge => CloudCap,
                _ => EdgeCap,
            };
            _swarm.Join(_mesh.NameOf(m), role, cap, m);
        }
    }

    /// <summary>스웜 노드 침묵(하트비트 중단 → 타임아웃 이탈 · 메시 우회) / 복귀.</summary>
    public void Silence(int swarmNode, bool silent)
    {
        lock (_gate) if ((uint)swarmNode < (uint)_silenced.Length) _silenced[swarmNode] = silent;
        LastAction = silent ? "🔇 노드 침묵 주입 · #" + swarmNode : "🔊 노드 복귀 · #" + swarmNode;
        Raise();
    }

    public bool IsSilenced(int n) => (uint)n < (uint)_silenced.Length && _silenced[n];

    /// <summary>1틱(무할당).</summary>
    public MeshDelivery TickOnce(double dt)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("EdgeSwarm.Runner.Tick");
            var ms = dt * 1000;
            Heartbeats();
            for (var i = 0; i < TilesPerFrame; i++) _swarm.Submit(SwarmTaskKind.RenderTile, 0.25f);
            _swarm.Submit(SwarmTaskKind.Encode, 0.5f);
            if ((_frameSeq & 7) == 0) _swarm.Submit(SwarmTaskKind.Inference);
            if ((_frameSeq & 15) == 0) _swarm.Submit(SwarmTaskKind.Analysis);
            _swarm.Dispatch();
            _swarm.Advance(ms);
            var kbps = _mesh.BitrateOf(_flow);
            var bytes = (int)(kbps * 1000L / 8 / Math.Max(1, Fps));
            var d = _mesh.SendFrame(_flow, bytes, dt);
            _last = d;
            if (d.Delivered) { Interlocked.Increment(ref _deliveredFrames); _latHist[_histHead] = (float)d.LatencyMs; _histHead = (_histHead + 1) % _latHist.Length; }
            Interlocked.Increment(ref _frameSeq);
            return d;
        }
    }

    private void Heartbeats()
    {
        _swarm.SimulateHeartbeats(_hb, _silenced);
    }

    public int CopyLatencyHistory(Span<float> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _latHist.Length);
            for (var i = 0; i < n; i++) dst[i] = _latHist[(_histHead + _latHist.Length - n + i) % _latHist.Length];
            return n;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 스웜 · 메시 라이브";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(0.2, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                try { TickOnce(dt); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("EdgeSwarm.Tick", ex); }
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("EdgeSwarm.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
