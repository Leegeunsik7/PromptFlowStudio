using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace PromptFlowStudio.Services.GpuSwarm;

/// <summary>프레임 조립 결과(값 타입).</summary>
public readonly record struct SwarmFrameResult(long Frame, double FrameMs, int Tiles, int Fallbacks, int Stale, long ZeroCopyBytes, long CopiedBytes, double PredictedMs, double FallbackMs, bool TimedOut);

/// <summary>
/// 이중 프레임 버퍼(RGB float) — 익명 MemoryMappedFile 뷰 포인터 1회 획득(제로카피 직접 기록) · 실패 시 POH 고정 배열 폴백.
/// </summary>
public sealed unsafe class MappedFrameBuffer : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly float[]? _pinned;
    private byte* _ptr;

    public MappedFrameBuffer(int floatsPerFrame)
    {
        FloatsPerFrame = floatsPerFrame;
        var bytes = (long)floatsPerFrame * sizeof(float) * 2;
        try
        {
            _mmf = MemoryMappedFile.CreateNew(null, bytes);
            _view = _mmf.CreateViewAccessor(0, bytes);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _ptr = p + _view.PointerOffset;
            IsMapped = true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException or NotSupportedException)
        {
            _view?.Dispose(); _mmf?.Dispose(); _view = null; _mmf = null;
            _pinned = GC.AllocateArray<float>(floatsPerFrame * 2, pinned: true);
            PromptFlowStudio.Program.Log("GpuSwarm.Mmf", ex);
        }
    }

    public int FloatsPerFrame { get; }
    public bool IsMapped { get; }

    public Span<float> Frame(int i) => _pinned is not null
        ? _pinned.AsSpan(i * FloatsPerFrame, FloatsPerFrame)
        : new Span<float>(_ptr + (long)i * FloatsPerFrame * sizeof(float), FloatsPerFrame);

    public void Dispose()
    {
        if (_view is not null && _ptr != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _ptr = null; }
        _view?.Dispose(); _mmf?.Dispose();
    }
}

/// <summary>원격 노드 링크 시뮬레이션(인프로세스 워커 — 실 연산 후 RTT + 바이트/대역폭 + 지터만큼 전송 지연).</summary>
public sealed class SimulatedLink
{
    public double RttMs { get; set; }
    public double BandwidthMBs { get; set; } = 100;
    public double JitterMs { get; set; }
    /// <summary>무통보 단절(킵얼라이브 중단 · 결과 소실) — 하트비트 타임아웃으로만 검출.</summary>
    public bool Silent { get; set; }
}

/// <summary>
/// [PromptFlow Studio v16.96.0] ⚡ 초저지연 프레임 조립 &amp; 하드웨어 페일백 가드.
/// · 타일 상태(epoch&lt;&lt;2 | 단계 Assigned/Writing/Done) 전역 단조 epoch CAS — 탈취/만료 타일의 늦은 결과는 커밋 불가(드롭 집계) → 찢김 없음.
/// · 로컬 백엔드: 매핑 프레임 버퍼에 타일 직접 렌더(제로카피) · 조립 스레드도 로컬 큐를 함께 소화(work-helping).
/// · 원격: 수신 슬랩(노드별 1회 할당)에 도착 → CAS 커밋 후 행 단위 블릿 1회(네트워크 수신 버퍼 → 프레임) · VRAM 예약/해제.
/// · 페일백: 노드 비건강(명시 단절 = 즉시 · 무통보 = 하트비트 타임아웃) 또는 타일 데드라인(예측×배수+여유) 초과 → 로컬 노드로 epoch 재배정(µs) · 검출→재배정 지연 실측.
/// </summary>
public sealed class LowLatencyFrameAssemblyFallbackGuard : IDisposable
{
    private const int MaxTiles = DynamicCloudRenderSwarmLoadBalancer.MaxTiles, MaxNodes = DistributedHeterogeneousGpuTopologyEngine.MaxNodes;
    private const long Assigned = 0, Writing = 1, Done = 2;
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    private sealed class NodeQueue
    {
        public readonly long[] Items = new long[MaxTiles * 4];
        public int Head, Count;
    }

    private readonly DistributedHeterogeneousGpuTopologyEngine _topo;
    private readonly DynamicCloudRenderSwarmLoadBalancer _lb;
    private readonly NodeQueue[] _q = new NodeQueue[MaxNodes];
    private readonly SimulatedLink?[] _links = new SimulatedLink?[MaxNodes];
    private readonly long[] _disconnectTs = new long[MaxNodes];
    private readonly List<Thread> _threads = new();
    private readonly int[] _tx = new int[MaxTiles], _ty = new int[MaxTiles], _tw = new int[MaxTiles], _th = new int[MaxTiles], _owner = new int[MaxTiles], _reject = new int[MaxTiles];
    private readonly long[] _state = new long[MaxTiles], _deadline = new long[MaxTiles];
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly object _frameGate = new();
    private MappedFrameBuffer? _fb;
    private float[] _mainScratch = Array.Empty<float>();
    private ISwarmWorkload? _work;
    private volatile bool _stop;
    private long _epochGen, _frame, _zeroCopy, _copied;
    private int _count, _done, _front, _fallbacks, _stale, _width, _height;
    private long _totalFallbacks, _totalStale, _frames;
    private Thread? _keepalive;

    public LowLatencyFrameAssemblyFallbackGuard(DistributedHeterogeneousGpuTopologyEngine topo, DynamicCloudRenderSwarmLoadBalancer lb)
    {
        _topo = topo; _lb = lb;
        for (var i = 0; i < MaxNodes; i++) _q[i] = new NodeQueue();
    }

    public DistributedHeterogeneousGpuTopologyEngine Topology => _topo;
    public DynamicCloudRenderSwarmLoadBalancer Balancer => _lb;
    public double DeadlineFactor { get; set; } = 3;
    public double DeadlineSlackMs { get; set; } = 25;
    public double FrameTimeoutMs { get; set; } = 5000;
    public bool MainThreadHelps { get; set; } = true;
    public bool IsMapped => _fb?.IsMapped ?? false;
    public int Width => _width;
    public int Height => _height;
    public ISwarmWorkload? Workload => _work;
    public long Frames => Interlocked.Read(ref _frames);
    public long TotalFallbacks => Interlocked.Read(ref _totalFallbacks);
    public long TotalStale => Interlocked.Read(ref _totalStale);
    public SwarmFrameResult Last { get; private set; }
    public double MaxFallbackMs { get; private set; }
    public SimulatedLink? LinkOf(int n) => _links[n];
    public int TileCount => _count;
    public int TileOwner(int i) => Volatile.Read(ref _owner[i]);
    public (int X, int Y, int W, int H) TileRect(int i) => (_tx[i], _ty[i], _tw[i], _th[i]);

    /// <summary>표시용 전면 프레임(마지막 완성 프레임).</summary>
    public ReadOnlySpan<float> Front => _fb is null ? ReadOnlySpan<float>.Empty : _fb.Frame(Volatile.Read(ref _front));

    /// <summary>워크로드 교체(프레임 경계 · 해상도 변경 시 매핑 버퍼 재생성).</summary>
    public void SetWorkload(ISwarmWorkload w)
    {
        lock (_frameGate)
        {
            if (_fb is null || w.Width != _width || w.Height != _height)
            {
                _fb?.Dispose();
                _fb = new MappedFrameBuffer(w.Width * w.Height * 3);
                _width = w.Width; _height = w.Height;
            }
            _mainScratch = new float[Math.Max(1, w.ScratchFloats)];
            _work = w;
        }
    }

    /// <summary>노드 워커 시작(threads = 레인 수 · link = 원격 시뮬레이션, null = 로컬).</summary>
    public void StartNode(int node, int threads, SimulatedLink? link)
    {
        _links[node] = link;
        for (var k = 0; k < threads; k++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = "PF.GPU.Node" + node + "." + k };
            lock (_threads) _threads.Add(t);
            t.Start(node);
        }
        if (_keepalive is null)
        {
            _keepalive = new Thread(KeepaliveLoop) { IsBackground = true, Name = "PF.GPU.Keepalive" };
            _keepalive.Start();
        }
    }

    /// <summary>원격 연결 토글(명시 단절 = 즉시 비건강 → 다음 스캔에서 페일백).</summary>
    public void SetConnected(int node, bool on)
    {
        if (!on) Volatile.Write(ref _disconnectTs[node], Stopwatch.GetTimestamp());
        _topo.SetConnected(node, on);
    }

    private void KeepaliveLoop()
    {
        while (!_stop)
        {
            for (var n = 0; n < _topo.Count; n++)
            {
                var l = _links[n];
                if (l is null) { _topo.Heartbeat(n, 0); continue; }
                if (_topo.Connected(n) && !l.Silent) _topo.Heartbeat(n, l.RttMs);
            }
            Thread.Sleep(10);
        }
    }

    // ── 큐(Monitor · 무할당) ──
    private void Enqueue(int node, int tile, long epoch)
    {
        var q = _q[node];
        lock (q)
        {
            if (q.Count < q.Items.Length)
            {
                q.Items[(q.Head + q.Count) % q.Items.Length] = ((long)tile << 40) | epoch;
                q.Count++;
            }
            Monitor.Pulse(q);
        }
    }

    private bool TryDequeue(int node, int waitMs, out int tile, out long epoch)
    {
        var q = _q[node];
        lock (q)
        {
            while (q.Count == 0)
            {
                if (waitMs == 0 || _stop || !Monitor.Wait(q, waitMs)) { tile = 0; epoch = 0; return false; }
            }
            var v = q.Items[q.Head];
            q.Head = (q.Head + 1) % q.Items.Length; q.Count--;
            tile = (int)(v >> 40); epoch = v & ((1L << 40) - 1);
            return true;
        }
    }

    private void ClearQueues()
    {
        for (var n = 0; n < MaxNodes; n++) { var q = _q[n]; lock (q) { q.Head = 0; q.Count = 0; } }
    }

    // ── 워커 ──
    private void WorkerLoop(object? o)
    {
        var node = (int)o!;
        float[] scratch = Array.Empty<float>(), recv = Array.Empty<float>();
        while (!_stop)
        {
            try
            {
                if (!TryDequeue(node, 50, out var tile, out var epoch)) continue;
                var w = _work;
                if (w is null) continue;
                if (scratch.Length < w.ScratchFloats) scratch = new float[w.ScratchFloats]; // 워크로드 교체 시 1회
                var link = _links[node];
                if (link is null) { RunLocal(node, tile, epoch, w, scratch); continue; }
                if (recv.Length < w.Width * w.Height * 3) recv = new float[w.Width * w.Height * 3];
                RunRemote(node, tile, epoch, w, scratch, recv, link);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { PromptFlowStudio.Program.Log("GpuSwarm.Worker", ex); }
        }
    }

    private static long Pack(long epoch, long phase) => (epoch << 2) | phase;

    /// <summary>로컬 타일: CAS Assigned→Writing → 매핑 프레임에 직접 렌더(제로카피) → Done.</summary>
    private bool RunLocal(int node, int i, long epoch, ISwarmWorkload w, Span<float> scratch)
    {
        if (Interlocked.CompareExchange(ref _state[i], Pack(epoch, Writing), Pack(epoch, Assigned)) != Pack(epoch, Assigned)) return false;
        var bytes = _tw[i] * _th[i] * 3 * sizeof(float);
        var reserved = _topo.ReserveVram(node, bytes);
        var t0 = Stopwatch.GetTimestamp();
        var frame = _fb!.Frame(1 - Volatile.Read(ref _front));
        w.RenderTile(Interlocked.Read(ref _frame), _tx[i], _ty[i], _tw[i], _th[i], frame[((_ty[i] * _width + _tx[i]) * 3)..], _width * 3, scratch);
        var ct = Stopwatch.GetTimestamp() - t0;
        if (reserved) _topo.ReleaseVram(node, bytes);
        _topo.ReportTile(node, _tw[i] * _th[i], ct, 0, 0);
        _lb.Observe(_tx[i], _ty[i], _tw[i], _th[i], node, ct);
        Interlocked.Add(ref _zeroCopy, bytes);
        Complete(i, epoch);
        return true;
    }

    /// <summary>원격 타일: 수신 슬랩 렌더 → 전송 지연 → 단절/탈취면 드롭 → CAS 커밋 → 행 블릿 → Done.</summary>
    private void RunRemote(int node, int i, long epoch, ISwarmWorkload w, Span<float> scratch, Span<float> recv, SimulatedLink link)
    {
        if (Volatile.Read(ref _state[i]) != Pack(epoch, Assigned)) { Interlocked.Increment(ref _stale); return; }
        if (!_topo.Connected(node) || link.Silent) return; // 블랙홀(결과 미도착)
        int x = _tx[i], y = _ty[i], tw = _tw[i], th = _th[i];
        var bytes = tw * th * 3 * sizeof(float);
        if (!_topo.ReserveVram(node, bytes)) { Volatile.Write(ref _reject[i], 1); return; }
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            w.RenderTile(Interlocked.Read(ref _frame), x, y, tw, th, recv, tw * 3, scratch);
            var ct = Stopwatch.GetTimestamp() - t0;
            var delayMs = link.RttMs + bytes / (Math.Max(1e-3, link.BandwidthMBs) * 1024 * 1024 / 1000.0) + (link.JitterMs > 0 ? link.JitterMs * Random.Shared.NextDouble() : 0);
            var x0 = Stopwatch.GetTimestamp();
            Delay(delayMs);
            var xt = Stopwatch.GetTimestamp() - x0;
            if (!_topo.Connected(node) || link.Silent) return; // 전송 중 단절 = 소실
            if (Interlocked.CompareExchange(ref _state[i], Pack(epoch, Writing), Pack(epoch, Assigned)) != Pack(epoch, Assigned)) { Interlocked.Increment(ref _stale); return; }
            var frame = _fb!.Frame(1 - Volatile.Read(ref _front));
            for (var r = 0; r < th; r++) recv.Slice(r * tw * 3, tw * 3).CopyTo(frame.Slice(((y + r) * _width + x) * 3, tw * 3));
            Interlocked.Add(ref _copied, bytes);
            _topo.ReportTile(node, tw * th, ct, bytes, xt);
            _lb.Observe(x, y, tw, th, node, ct);
            Complete(i, epoch);
        }
        finally { _topo.ReleaseVram(node, bytes); }
    }

    private void Delay(double ms)
    {
        if (ms <= 0) return;
        var end = Stopwatch.GetTimestamp() + (long)(ms / TickMs);
        if (ms > 3) Thread.Sleep((int)(ms - 2));
        var sw = new SpinWait();
        while (Stopwatch.GetTimestamp() < end && !_stop) sw.SpinOnce(-1);
    }

    private void Complete(int i, long epoch)
    {
        Volatile.Write(ref _state[i], Pack(epoch, Done));
        Interlocked.Increment(ref _done);
        _signal.Set();
    }

    /// <summary>탈취: (epoch, Assigned) → 새 epoch로 로컬 재배정.</summary>
    private bool Steal(int i, int localNode)
    {
        var cur = Volatile.Read(ref _state[i]);
        if ((cur & 3) != Assigned) return false;
        var ne = Interlocked.Increment(ref _epochGen);
        if (Interlocked.CompareExchange(ref _state[i], Pack(ne, Assigned), cur) != cur) return false;
        Volatile.Write(ref _owner[i], localNode);
        Volatile.Write(ref _reject[i], 0);
        Enqueue(localNode, i, ne);
        return true;
    }

    /// <summary>
    /// 1프레임 분산 렌더·조립(동기): 계획 → 배정 → 대기 루프(로컬 보조 · 비건강/데드라인 탈취) → 비용 맵 정규화 → 전면 교체.
    /// </summary>
    public SwarmFrameResult RenderFrame(long frameIndex)
    {
        lock (_frameGate)
        {
            var w = _work ?? throw new InvalidOperationException("워크로드 미설정");
            var t0 = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _frame, frameIndex);
            _topo.Probe();
            var n = _lb.Plan(_width, _height);
            ClearQueues();
            _done = 0; _fallbacks = 0; _stale = 0; _zeroCopy = 0; _copied = 0;
            _count = n;
            for (var i = 0; i < n; i++)
            {
                ref readonly var t = ref _lb.Tile(i);
                _tx[i] = t.X; _ty[i] = t.Y; _tw[i] = t.W; _th[i] = t.H; _owner[i] = t.Node; _reject[i] = 0;
                _deadline[i] = t0 + (long)((t.PredMs * DeadlineFactor + DeadlineSlackMs) / TickMs);
                var e = Interlocked.Increment(ref _epochGen);
                Volatile.Write(ref _state[i], Pack(e, Assigned));
            }
            for (var i = 0; i < n; i++) Enqueue(_owner[i], i, Volatile.Read(ref _state[i]) >> 2);
            var local = _topo.LocalFallbackNode;
            double fallbackMs = 0;
            var timedOut = false;
            var lastProbe = t0;
            while (Volatile.Read(ref _done) < n)
            {
                if (MainThreadHelps && TryDequeue(local, 0, out var lt, out var le)) { RunLocal(local, lt, le, w, _mainScratch); continue; }
                var now = Stopwatch.GetTimestamp();
                if ((now - lastProbe) * TickMs >= 2) { _topo.Probe(); lastProbe = now; }
                for (var i = 0; i < n; i++)
                {
                    var o = Volatile.Read(ref _owner[i]);
                    if (o == local || (Volatile.Read(ref _state[i]) & 3) != Assigned) continue;
                    var unhealthy = !_topo.Healthy(o);
                    if (!unhealthy && now < _deadline[i] && Volatile.Read(ref _reject[i]) == 0) continue;
                    if (!Steal(i, local)) continue;
                    _fallbacks++;
                    // 검출→재배정 지연: 명시 단절 = 단절 시각부터 · 무통보 = 마지막 하트비트부터(타임아웃 포함) · 데드라인/VRAM 거부 = 배정 시각부터
                    var dts = Volatile.Read(ref _disconnectTs[o]);
                    var since = !unhealthy ? (now - t0) * TickMs : dts != 0 ? (now - dts) * TickMs : _topo.HeartbeatAgeMs(o);
                    if (since > fallbackMs && since < 10_000) fallbackMs = since;
                }
                if ((now - t0) * TickMs > FrameTimeoutMs)
                {
                    timedOut = true;
                    for (var i = 0; i < n; i++) if (Steal(i, local)) _fallbacks++;
                    while (TryDequeue(local, 0, out var ft, out var fe)) RunLocal(local, ft, fe, w, _mainScratch);
                    if ((now - t0) * TickMs > FrameTimeoutMs * 2) break;
                }
                _signal.Wait(1);
                _signal.Reset();
            }
            _lb.NormalizeCostMap();
            Volatile.Write(ref _front, 1 - _front);
            var ms = (Stopwatch.GetTimestamp() - t0) * TickMs;
            Interlocked.Add(ref _totalFallbacks, _fallbacks);
            Interlocked.Add(ref _totalStale, Volatile.Read(ref _stale));
            Interlocked.Increment(ref _frames);
            if (fallbackMs > MaxFallbackMs) MaxFallbackMs = fallbackMs;
            for (var k = 0; k < MaxNodes; k++) if (_topo.Healthy(k) || k >= _topo.Count) Volatile.Write(ref _disconnectTs[k], 0);
            var r = new SwarmFrameResult(frameIndex, ms, n, _fallbacks, Volatile.Read(ref _stale), Interlocked.Read(ref _zeroCopy), Interlocked.Read(ref _copied), _lb.PredictedMakespanMs, fallbackMs, timedOut);
            Last = r;
            return r;
        }
    }

    public void Dispose()
    {
        _stop = true;
        for (var n = 0; n < MaxNodes; n++) { var q = _q[n]; lock (q) Monitor.PulseAll(q); }
        Thread[] ts;
        lock (_threads) { ts = _threads.ToArray(); _threads.Clear(); }
        foreach (var t in ts) t.Join(2000);
        _keepalive?.Join(500);
        lock (_frameGate) { _fb?.Dispose(); _fb = null; }
        _signal.Dispose();
    }
}
