using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Edge;

/// <summary>스웜 작업 종류 — 요구 벡터 프로파일.</summary>
public enum SwarmTaskKind : byte { RenderTile, Inference, Encode, Analysis }

/// <summary>
/// 스웜 하트비트 패킷(64B · blittable · SIMD 레인 정렬) — 노드 Id · 시퀀스 · 시각 · 8차원 능력 벡터(CPU/GPU/NPU/메모리/배터리/열여유/링크/신뢰) · 큐 깊이 · 역할.
/// 바이트 ↔ 구조체 변환은 MemoryMarshal 재해석(복사 0 · 리틀엔디언 전제).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct SwarmHeartbeat
{
    public const uint MagicValue = 0x31525753; // "SWR1"
    public const int Size = 64, Dims = 8;
    public uint Magic;
    public ushort NodeId;
    public byte Role, Flags;
    public uint Sequence;
    public int QueueDepth;
    public long TimestampMicros;
    public Vector256<float> Capability;   // 32B
    public uint Reserved0, Reserved1;
}

/// <summary>노드 상태 스냅샷(UI).</summary>
public readonly record struct SwarmNodeStatus(int Id, string Name, MeshRole Role, bool Online, float Cpu, float Gpu, float Npu, float Battery, float Thermal, float Link,
    int Queue, double BusyMs, long Completed, long Assigned, float Share, double LastSeenMs, int MeshNode);

/// <summary>배정 결과(작업 → 노드 · 예상 완료 ms).</summary>
public readonly record struct SwarmAssignment(int Task, SwarmTaskKind Kind, int Node, double EtaMs, float Score);

/// <summary>
/// [PromptFlow Studio v16.64.0] 🐝 실시간 AI 에지 스웜 노드 오케스트레이터.
///   ▸ 노드 테이블 SoA(능력 Vector256 행 · 큐/바쁨 시간) · SWR1 64B 하트비트 일괄 디코드(MemoryMarshal 재해석 · 무할당) · 하트비트 타임아웃 → 이탈 · 미완 작업 재큐.
///   ▸ 배정: 작업 요구 벡터 × 노드 능력 Vector256 내적(SIMD) = 처리율 → 예상 완료(바쁨 + 비용/처리율 + 메시 RTT) 최소 노드에 탐욕 배정(최소 완료 시간 · HEFT 계열).
///     배터리 &lt; 20% 모바일은 추론/렌더 제외 · 열여유 감쇠 · 신뢰도 가중 · 동률은 큐 깊이로.
///   ▸ 메시 결선: 노드 ↔ <see cref="RealtimeGlobalMediaMeshRouter"/> 노드 매핑 · 이탈 시 라우터에 통보(무단절 우회) · 원격 결과 반송 RTT 를 비용에 반영.
/// </summary>
public sealed class AiEdgeSwarmNodeOrchestrator
{
    public const int MaxNodes = 64, MaxTasks = 1024;
    public const double HeartbeatTimeoutMs = 1500;

    private static readonly float[][] Demand =
    {
        new[] { 0.35f, 1.0f, 0.10f, 0.15f, 0f, 0.20f, 0.20f, 0.10f }, // RenderTile: GPU 위주
        new[] { 0.20f, 0.40f, 1.0f, 0.20f, 0f, 0.20f, 0.05f, 0.10f }, // Inference: NPU 위주
        new[] { 0.30f, 0.80f, 0.10f, 0.10f, 0f, 0.15f, 0.40f, 0.10f }, // Encode: GPU + 링크
        new[] { 1.0f, 0.10f, 0.30f, 0.30f, 0f, 0.20f, 0.05f, 0.10f },  // Analysis: CPU 위주
    };
    private static readonly double[] BaseCostMs = { 24, 18, 10, 12 };

    private readonly object _gate = new();
    private readonly Vector256<float>[] _cap = new Vector256<float>[MaxNodes];
    private readonly string?[] _name = new string?[MaxNodes];
    private readonly MeshRole[] _role = new MeshRole[MaxNodes];
    private readonly bool[] _online = new bool[MaxNodes];
    private readonly int[] _meshNode = new int[MaxNodes];
    private readonly int[] _queue = new int[MaxNodes];
    private readonly double[] _busyMs = new double[MaxNodes];
    private readonly long[] _seenUs = new long[MaxNodes];
    private readonly uint[] _seq = new uint[MaxNodes];
    private readonly long[] _completed = new long[MaxNodes], _assigned = new long[MaxNodes];
    private readonly Vector256<float>[] _demand = new Vector256<float>[4];
    // 작업 큐(링) · 진행 중 작업
    private readonly SwarmTaskKind[] _taskKind = new SwarmTaskKind[MaxTasks];
    private readonly float[] _taskCost = new float[MaxTasks];
    private readonly int[] _taskNode = new int[MaxTasks];
    private readonly double[] _taskDue = new double[MaxTasks];
    private readonly int[] _pending = new int[MaxTasks];
    private int _pendingCount, _nodeCount, _nextTask;
    private long _dispatched, _requeued, _evicted, _heartbeats, _badPackets, _completedTotal;
    private double _clockMs, _schedUs;
    private RealtimeGlobalMediaMeshRouter? _mesh;
    private int _meshHome = -1;

    public AiEdgeSwarmNodeOrchestrator()
    {
        for (var k = 0; k < 4; k++) _demand[k] = Vector256.Create(Demand[k]);
        Array.Fill(_taskNode, -1);
        Array.Fill(_meshNode, -1);
    }

    public event Action? OnChange;
    /// <summary>노드 이탈 통보(이름).</summary>
    public event Action<int>? OnNodeEvicted;

    public int NodeCount => _nodeCount;
    public int PendingTasks => Volatile.Read(ref _pendingCount);
    public long Dispatched => Interlocked.Read(ref _dispatched);
    public long Requeued => Interlocked.Read(ref _requeued);
    public long Evicted => Interlocked.Read(ref _evicted);
    public long Heartbeats => Interlocked.Read(ref _heartbeats);
    public long BadPackets => Interlocked.Read(ref _badPackets);
    public long CompletedTotal => Interlocked.Read(ref _completedTotal);
    public double ClockMs => _clockMs;
    /// <summary>최근 배정 1회 소요(μs).</summary>
    public double ScheduleMicros => Volatile.Read(ref _schedUs);
    public string? LastAction { get; private set; }
    public RealtimeGlobalMediaMeshRouter? Mesh => _mesh;

    /// <summary>메시 결선 — home = 작업 발생(스튜디오) 메시 노드.</summary>
    public void AttachMesh(RealtimeGlobalMediaMeshRouter mesh, int homeMeshNode) { lock (_gate) { _mesh = mesh; _meshHome = homeMeshNode; } }

    public static string Label(SwarmTaskKind k) => k switch { SwarmTaskKind.RenderTile => "렌더 타일", SwarmTaskKind.Inference => "AI 추론", SwarmTaskKind.Encode => "인코드", _ => "분석" };

    /// <summary>노드 합류(능력: cpu,gpu,npu,mem,battery,thermal,link,trust 0..1).</summary>
    public int Join(string name, MeshRole role, ReadOnlySpan<float> capability, int meshNode = -1)
    {
        if (capability.Length < SwarmHeartbeat.Dims) throw new ArgumentException("능력 벡터 8차원");
        int id;
        lock (_gate)
        {
            id = -1;
            for (var i = 0; i < MaxNodes; i++) if (_name[i] is null) { id = i; break; }
            if (id < 0) throw new InvalidOperationException("스웜 노드 상한 " + MaxNodes);
            _name[id] = name; _role[id] = role; _online[id] = true; _meshNode[id] = meshNode;
            _cap[id] = Vector256.Create(capability[..8]);
            _seenUs[id] = (long)(_clockMs * 1000); _queue[id] = 0; _busyMs[id] = 0; _completed[id] = _assigned[id] = 0;
            if (id >= _nodeCount) _nodeCount = id + 1;
            LastAction = "➕ 스웜 합류 · " + name;
        }
        Raise();
        return id;
    }


    /// <summary>하트비트 인코딩(64B · 복사 0 재해석).</summary>
    public static void WriteHeartbeat(Span<byte> dst, ushort node, MeshRole role, uint seq, int queue, long tMicros, Vector256<float> cap)
    {
        ref var h = ref MemoryMarshal.AsRef<SwarmHeartbeat>(dst[..SwarmHeartbeat.Size]);
        h.Magic = SwarmHeartbeat.MagicValue; h.NodeId = node; h.Role = (byte)role; h.Flags = 0;
        h.Sequence = seq; h.QueueDepth = queue; h.TimestampMicros = tMicros; h.Capability = cap; h.Reserved0 = h.Reserved1 = 0;
    }

    /// <summary>하트비트 일괄 수신(N×64B · 무할당) — 역순/중복 시퀀스 폐기 · NaN/범위 밖 능력 거부. 적용 수 반환.</summary>
    public int IngestHeartbeats(ReadOnlySpan<byte> batch, long nowMicros)
    {
        if (!BitConverter.IsLittleEndian) return 0;
        var packets = MemoryMarshal.Cast<byte, SwarmHeartbeat>(batch[..(batch.Length / SwarmHeartbeat.Size * SwarmHeartbeat.Size)]);
        var applied = 0;
        lock (_gate)
        {
            for (var i = 0; i < packets.Length; i++)
            {
                ref readonly var h = ref packets[i];
                var c = h.Capability;
                var ok = h.Magic == SwarmHeartbeat.MagicValue && h.NodeId < MaxNodes && _name[h.NodeId] is not null
                    && Vector256.EqualsAll(c, c)   // NaN 거부
                    && Vector256.LessThanOrEqualAll(c, Vector256.Create(1f)) && Vector256.GreaterThanOrEqualAll(c, Vector256<float>.Zero);
                if (!ok) { Interlocked.Increment(ref _badPackets); continue; }
                var id = h.NodeId;
                if (_seq[id] != 0 && (int)(h.Sequence - _seq[id]) <= 0) continue;
                _seq[id] = h.Sequence;
                _cap[id] = c;
                _queue[id] = Math.Max(_queue[id], h.QueueDepth);
                _seenUs[id] = nowMicros;
                if (!_online[id]) { _online[id] = true; if (_mesh is not null && _meshNode[id] >= 0) _mesh.ReviveNode(_meshNode[id]); }
                applied++;
            }
        }
        Interlocked.Add(ref _heartbeats, applied);
        return applied;
    }

    /// <summary>작업 투입(비용 = 기준 대비 배율).</summary>
    public int Submit(SwarmTaskKind kind, float costScale = 1f)
    {
        lock (_gate)
        {
            if (_pendingCount >= MaxTasks) return -1;
            var id = -1;
            for (var k = 0; k < MaxTasks; k++)
            {
                var t = (_nextTask + k) % MaxTasks;
                if (_taskNode[t] == -1 && !IsPending(t)) { id = t; break; }
            }
            if (id < 0) return -1;
            _nextTask = (id + 1) % MaxTasks;
            _taskKind[id] = kind; _taskCost[id] = MathF.Max(0.05f, costScale);
            _taskNode[id] = -2; // 대기
            _pending[_pendingCount++] = id;
            return id;
        }
    }

    private bool IsPending(int t) => _taskNode[t] == -2;

    /// <summary>처리율(SIMD 내적 · 배터리/열 보정) — 0 = 불가.</summary>
    private float Throughput(int n, SwarmTaskKind k)
    {
        var cap = _cap[n];
        var thr = Vector256.Dot(cap, _demand[(int)k]);
        var battery = cap.GetElement(4);
        var thermal = cap.GetElement(5);
        var trust = cap.GetElement(7);
        if (_role[n] == MeshRole.Mobile && battery < 0.2f && k is SwarmTaskKind.RenderTile or SwarmTaskKind.Inference) return 0f;
        return thr * (0.4f + 0.6f * thermal) * (0.5f + 0.5f * trust);
    }

    private double MeshRttMs(int n)
    {
        if (_mesh is null || _meshHome < 0 || _meshNode[n] < 0 || _meshNode[n] == _meshHome) return 0;
        var m = _meshNode[n];
        if (_mesh.HasLink(_meshHome, m)) return _mesh.MeasuredRttMs(_meshHome, m);
        return 2 * 60; // 다중 홉 추정치
    }

    /// <summary>대기 작업 일괄 배정(무할당 · 탐욕 최소 완료 시간) — 배정 수. dst 지정 시 결과 기록.</summary>
    public int Dispatch(Span<SwarmAssignment> dst = default)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Swarm.Dispatch");
            var t0 = Stopwatch.GetTimestamp();
            var count = 0; var keep = 0;
            for (var p = 0; p < _pendingCount; p++)
            {
                var t = _pending[p];
                var kind = _taskKind[t];
                int best = -1; double bestEta = double.MaxValue; float bestScore = 0;
                for (var n = 0; n < _nodeCount; n++)
                {
                    if (!_online[n] || _name[n] is null) continue;
                    var thr = Throughput(n, kind);
                    if (thr <= 1e-3f) continue;
                    var exec = BaseCostMs[(int)kind] * _taskCost[t] / thr;
                    var eta = _busyMs[n] + exec + MeshRttMs(n);
                    if (eta < bestEta - 1e-9 || (Math.Abs(eta - bestEta) <= 1e-9 && best >= 0 && _queue[n] < _queue[best])) { bestEta = eta; best = n; bestScore = thr; }
                }
                if (best < 0) { _pending[keep++] = t; continue; }
                _busyMs[best] = bestEta;
                _queue[best]++;
                _assigned[best]++;
                _taskNode[t] = best;
                _taskDue[t] = _clockMs + bestEta;
                if (count < dst.Length) dst[count] = new SwarmAssignment(t, kind, best, bestEta, bestScore);
                count++;
            }
            _pendingCount = keep;
            Interlocked.Add(ref _dispatched, count);
            Volatile.Write(ref _schedUs, (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            return count;
        }
    }

    /// <summary>시뮬 시계 진행(무할당) — 바쁨 소진 · 완료 처리 · 하트비트 타임아웃 이탈/재큐. 완료 수 반환.</summary>
    public int Advance(double dtMs)
    {
        var evictedAny = false;
        int done;
        lock (_gate)
        {
            _clockMs += dtMs;
            var nowUs = (long)(_clockMs * 1000);
            for (var n = 0; n < _nodeCount; n++) _busyMs[n] = Math.Max(0, _busyMs[n] - dtMs);
            done = 0;
            for (var t = 0; t < MaxTasks; t++)
            {
                var n = _taskNode[t];
                if (n < 0 || _taskDue[t] > _clockMs) continue;
                _taskNode[t] = -1;
                _queue[n] = Math.Max(0, _queue[n] - 1);
                _completed[n]++;
                done++;
            }
            for (var n = 0; n < _nodeCount; n++)
            {
                if (!_online[n] || _name[n] is null || nowUs - _seenUs[n] <= HeartbeatTimeoutMs * 1000) continue;
                EvictCore(n);
                evictedAny = true;
            }
        }
        Interlocked.Add(ref _completedTotal, done);
        if (evictedAny) Raise();
        return done;
    }

    private void EvictCore(int n)
    {
        _online[n] = false;
        _busyMs[n] = 0; _queue[n] = 0;
        for (var t = 0; t < MaxTasks; t++)
        {
            if (_taskNode[t] != n) continue;
            _taskNode[t] = -2;
            if (_pendingCount < MaxTasks) _pending[_pendingCount++] = t;
            Interlocked.Increment(ref _requeued);
        }
        Interlocked.Increment(ref _evicted);
        if (_mesh is not null && _meshNode[n] >= 0) _mesh.RemoveNode(_meshNode[n]);
        LastAction = "⛔ 스웜 이탈 · " + _name[n] + " → 작업 재큐 · 메시 우회";
        try { OnNodeEvicted?.Invoke(n); } catch (Exception ex) { PromptFlowStudio.Program.Log("Swarm.OnEvicted", ex); }
    }

    /// <summary>즉시 강제 이탈(장애 주입).</summary>
    public void Evict(int n) { lock (_gate) if ((uint)n < MaxNodes && _online[n]) EvictCore(n); Raise(); }

    /// <summary>로컬 시뮬 하트비트(모든 노드 · 배터리 소모 · 열 변화 반영 · skip[n] 침묵) — 이탈 노드도 송신하면 복귀.</summary>
    public int SimulateHeartbeats(Span<byte> scratch, ReadOnlySpan<bool> skip = default)
    {
        int count = 0;
        long now;
        lock (_gate)
        {
            now = (long)(_clockMs * 1000);
            for (var n = 0; n < _nodeCount && (count + 1) * SwarmHeartbeat.Size <= scratch.Length; n++)
            {
                if (_name[n] is null || (n < skip.Length && skip[n])) continue;
                var c = _cap[n];
                var load = Math.Min(1f, _queue[n] / 8f);
                var battery = _role[n] == MeshRole.Mobile ? MathF.Max(0f, c.GetElement(4) - 0.0005f * (1 + load)) : c.GetElement(4);
                var thermal = Math.Clamp(c.GetElement(5) + (0.5f - load) * 0.01f, 0.2f, 1f);
                c = c.WithElement(4, battery).WithElement(5, thermal);
                WriteHeartbeat(scratch.Slice(count * SwarmHeartbeat.Size), (ushort)n, _role[n], _seq[n] + 1, _queue[n], now, c);
                count++;
            }
        }
        return IngestHeartbeats(scratch[..(count * SwarmHeartbeat.Size)], now);
    }

    public SwarmNodeStatus[] Snapshot()
    {
        lock (_gate)
        {
            var list = new List<SwarmNodeStatus>(_nodeCount);
            long total = 0;
            for (var n = 0; n < _nodeCount; n++) total += _assigned[n];
            for (var n = 0; n < _nodeCount; n++)
            {
                if (_name[n] is null) continue;
                var c = _cap[n];
                list.Add(new SwarmNodeStatus(n, _name[n]!, _role[n], _online[n], c.GetElement(0), c.GetElement(1), c.GetElement(2), c.GetElement(4), c.GetElement(5), c.GetElement(6),
                    _queue[n], _busyMs[n], _completed[n], _assigned[n], total > 0 ? _assigned[n] / (float)total : 0f, _clockMs - _seenUs[n] / 1000.0, _meshNode[n]));
            }
            return list.ToArray();
        }
    }

    public bool IsOnline(int n) => (uint)n < MaxNodes && _online[n];
    public long CompletedBy(int n) => (uint)n < MaxNodes ? _completed[n] : 0;
    public long AssignedTo(int n) => (uint)n < MaxNodes ? _assigned[n] : 0;
    public int MeshNodeOf(int n) => (uint)n < MaxNodes ? _meshNode[n] : -1;

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Swarm.OnChange", ex); }
    }
}
