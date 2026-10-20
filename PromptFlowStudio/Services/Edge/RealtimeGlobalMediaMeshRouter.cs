using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PromptFlowStudio.Services.Edge;

/// <summary>메시 노드 역할.</summary>
public enum MeshRole : byte { Desktop, Mobile, Edge, CloudEdge, Viewer }

/// <summary>미디어 메시 패킷 헤더(28B · 리틀엔디언) — 플로우 · 프레임 · 조각 · 홉/TTL · 송신 시각(μs).</summary>
public readonly record struct MeshPacketHeader(ushort Flow, uint Frame, ushort Fragment, ushort Fragments, byte Hops, byte Ttl, ushort PayloadBytes, long SentMicros, ushort Origin, ushort Dest)
{
    public const uint Magic = 0x3148534D; // "MSH1"
    public const int Size = 28;

    public void Write(Span<byte> d)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], Flow);
        BinaryPrimitives.WriteUInt32LittleEndian(d[6..], Frame);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], Fragment);
        BinaryPrimitives.WriteUInt16LittleEndian(d[12..], Fragments);
        d[14] = Hops; d[15] = Ttl;
        BinaryPrimitives.WriteUInt16LittleEndian(d[16..], PayloadBytes);
        BinaryPrimitives.WriteInt64LittleEndian(d[18..], SentMicros);
        BinaryPrimitives.WriteUInt16LittleEndian(d[26..], (ushort)((Origin & 0xFF) | (Dest << 8)));
    }

    public static bool TryRead(ReadOnlySpan<byte> s, out MeshPacketHeader h)
    {
        h = default;
        if (s.Length < Size || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) return false;
        var od = BinaryPrimitives.ReadUInt16LittleEndian(s[26..]);
        h = new MeshPacketHeader(BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[6..]), BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[12..]), s[14], s[15], BinaryPrimitives.ReadUInt16LittleEndian(s[16..]), BinaryPrimitives.ReadInt64LittleEndian(s[18..]), (ushort)(od & 0xFF), (ushort)(od >> 8));
        return h.Fragment < h.Fragments && h.PayloadBytes <= s.Length - Size;
    }
}

/// <summary>프레임 전달 결과(서브프레임 컷스루 vs 저장 후 전달 비교 포함).</summary>
public readonly record struct MeshDelivery(int Flow, uint Frame, bool Delivered, int Fragments, int Lost, int Rerouted, double LatencyMs, double StoreForwardMs, int Hops, int BitrateKbps);

/// <summary>플로우 상태 스냅샷.</summary>
public readonly record struct MeshFlowStatus(int Flow, int Source, int Dest, int BitrateKbps, int Hops, double PathLatencyMs, double PathBandwidthMbps, long Frames, long Lost, long Reroutes, bool HasBackup, int Rung);

/// <summary>
/// [PromptFlow Studio v16.64.0] 🌐 실시간 글로벌 미디어 메시 패킷 라우터.
///   ▸ 링크 상태: 노드(≤64) · 단방향 링크(지연 μs · 용량 kbps · 손실률) · 링크별 대역폭 추정(전달 처리량 EWMA + 큐잉 지연) — 정수 인덱스 · 스크래치 배열(무할당).
///   ▸ 경로 트랜스파일: 플로우마다 Dijkstra(지연 + 직렬화 + 혼잡 비용) 1차 경로 + 노드 분리 백업 경로를 사전 컴파일 → 다음홉 포워딩 테이블.
///     노드 이탈/링크 단절 시 백업으로 즉시 전환(재계산 대기 0 · 무단절) 후 백그라운드 재트랜스파일.
///   ▸ 서브프레임 포워딩: 프레임을 MTU 조각으로 나눠 홉마다 컷스루(조각 단위 전달) — 지연 = Σ전파 + 조각 직렬화×(홉-1) + 프레임 직렬화(병목). 저장 후 전달 대비 이득 보고.
///   ▸ ABR: 경로 병목 추정 대역폭 × 0.8 → 비트레이트 래더(스텝다운 즉시 · 스텝업 히스테리시스 3프레임) · 손실 시 승법 감소.
///   ▸ 실전송: <see cref="MeshUdpTransport"/>(MSH1 헤더 + 페이로드 · SIO_UDP_CONNRESET off) — 루프백/LAN 피어.
/// </summary>
public sealed class RealtimeGlobalMediaMeshRouter
{
    public const int MaxNodes = 64, MaxFlows = 32, Mtu = 1200, PayloadMtu = Mtu - MeshPacketHeader.Size;
    public static readonly int[] Ladder = { 400, 800, 1500, 3000, 6000, 12000, 25000 };

    private readonly object _gate = new();
    private readonly string?[] _name = new string?[MaxNodes];
    private readonly MeshRole[] _role = new MeshRole[MaxNodes];
    private readonly bool[] _alive = new bool[MaxNodes];
    private readonly float[] _x = new float[MaxNodes], _y = new float[MaxNodes];
    // 인접 행렬(단방향 · -1 = 링크 없음)
    private readonly int[] _latUs = new int[MaxNodes * MaxNodes];
    private readonly int[] _capKbps = new int[MaxNodes * MaxNodes];
    private readonly float[] _loss = new float[MaxNodes * MaxNodes];
    private readonly float[] _estKbps = new float[MaxNodes * MaxNodes];
    private readonly float[] _loadKbps = new float[MaxNodes * MaxNodes];
    private readonly float[] _rttUs = new float[MaxNodes * MaxNodes];
    // Dijkstra 스크래치
    private readonly double[] _dist = new double[MaxNodes];
    private readonly int[] _prev = new int[MaxNodes];
    private readonly bool[] _done = new bool[MaxNodes], _ban = new bool[MaxNodes];
    // 플로우
    private readonly int[] _fSrc = new int[MaxFlows], _fDst = new int[MaxFlows], _fKbps = new int[MaxFlows], _fRung = new int[MaxFlows], _fUp = new int[MaxFlows];
    private readonly int[] _fPath = new int[MaxFlows * MaxNodes], _fPathLen = new int[MaxFlows];
    private readonly int[] _fBackup = new int[MaxFlows * MaxNodes], _fBackupLen = new int[MaxFlows];
    private readonly long[] _fFrames = new long[MaxFlows], _fLost = new long[MaxFlows], _fReroutes = new long[MaxFlows];
    private readonly bool[] _fActive = new bool[MaxFlows], _fDirty = new bool[MaxFlows];
    private readonly uint[] _fFrameNo = new uint[MaxFlows];
    private readonly float[] _fLatEwma = new float[MaxFlows];
    private uint _rng = 0xC0FFEEu;
    private int _nodeCount;
    private long _transpiles, _seamless, _packets, _bytes, _drops;

    public event Action? OnChange;

    public int NodeCount => _nodeCount;
    public long Transpiles => Interlocked.Read(ref _transpiles);
    public long SeamlessSwitches => Interlocked.Read(ref _seamless);
    public long PacketsForwarded => Interlocked.Read(ref _packets);
    public long BytesForwarded => Interlocked.Read(ref _bytes);
    public long PacketDrops => Interlocked.Read(ref _drops);
    public string? LastAction { get; private set; }
    /// <summary>손실 시뮬 사용(false = 결정적 무손실).</summary>
    public bool SimulateLoss { get; set; } = true;

    public string NameOf(int n) => (uint)n < MaxNodes ? _name[n] ?? "" : "";
    public MeshRole RoleOf(int n) => (uint)n < MaxNodes ? _role[n] : MeshRole.Edge;
    public bool IsAlive(int n) => (uint)n < MaxNodes && _alive[n];
    public (float X, float Y) PositionOf(int n) => (uint)n < MaxNodes ? (_x[n], _y[n]) : (0, 0);
    public bool HasLink(int a, int b) => (uint)a < MaxNodes && (uint)b < MaxNodes && _latUs[a * MaxNodes + b] >= 0 && _alive[a] && _alive[b];
    public double LinkLatencyMs(int a, int b) => HasLink(a, b) ? _latUs[a * MaxNodes + b] / 1000.0 : double.NaN;
    /// <summary>측정 RTT(μs EWMA · 큐잉 포함) — 히트맵.</summary>
    public double MeasuredRttMs(int a, int b) => HasLink(a, b) ? _rttUs[a * MaxNodes + b] / 1000.0 : double.NaN;
    public float LinkUtilization(int a, int b) => HasLink(a, b) ? Math.Clamp(_loadKbps[a * MaxNodes + b] / Math.Max(1, _capKbps[a * MaxNodes + b]), 0f, 2f) : 0f;
    public float EstimatedKbps(int a, int b) => HasLink(a, b) ? _estKbps[a * MaxNodes + b] : 0f;

    public RealtimeGlobalMediaMeshRouter()
    {
        Array.Fill(_latUs, -1);
    }

    public int AddNode(string name, MeshRole role, float x, float y)
    {
        lock (_gate)
        {
            var n = -1;
            for (var i = 0; i < MaxNodes; i++) if (_name[i] is null) { n = i; break; }
            if (n < 0) throw new InvalidOperationException("메시 노드 상한 " + MaxNodes);
            _name[n] = name; _role[n] = role; _alive[n] = true; _x[n] = x; _y[n] = y;
            if (n >= _nodeCount) _nodeCount = n + 1;
            return n;
        }
    }

    /// <summary>양방향 링크(지연 ms · 용량 Mbps · 손실률).</summary>
    public void Link(int a, int b, double latencyMs, double mbps, float loss = 0f)
    {
        lock (_gate)
        {
            foreach (var (u, v) in new[] { (a, b), (b, a) })
            {
                var k = u * MaxNodes + v;
                _latUs[k] = (int)Math.Max(1, latencyMs * 1000);
                _capKbps[k] = (int)Math.Max(1, mbps * 1000);
                _loss[k] = Math.Clamp(loss, 0f, 0.5f);
                _estKbps[k] = _capKbps[k];
                _rttUs[k] = _latUs[k] * 2;
                _loadKbps[k] = 0;
            }
            MarkAllDirty();
        }
    }

    public void Unlink(int a, int b)
    {
        lock (_gate) { _latUs[a * MaxNodes + b] = -1; _latUs[b * MaxNodes + a] = -1; SwitchAffected(); }
        Raise();
    }

    /// <summary>링크 용량 변경(혼잡/회선 품질 변화 시뮬).</summary>
    public void SetCapacity(int a, int b, double mbps)
    {
        lock (_gate)
        {
            _capKbps[a * MaxNodes + b] = (int)Math.Max(1, mbps * 1000);
            _capKbps[b * MaxNodes + a] = (int)Math.Max(1, mbps * 1000);
        }
    }

    /// <summary>노드 이탈 — 영향 플로우는 백업 경로로 즉시 전환(무단절) 후 재트랜스파일 예약.</summary>
    public void RemoveNode(int n)
    {
        if ((uint)n >= MaxNodes) return;
        lock (_gate)
        {
            _alive[n] = false;
            SwitchAffected();
        }
        LastAction = "⛔ 노드 이탈 · " + NameOf(n) + " → 백업 경로 전환";
        Raise();
    }

    public void ReviveNode(int n)
    {
        if ((uint)n >= MaxNodes || _name[n] is null) return;
        lock (_gate) { _alive[n] = true; MarkAllDirty(); }
        Raise();
    }

    private void MarkAllDirty() { for (var f = 0; f < MaxFlows; f++) if (_fActive[f]) _fDirty[f] = true; }

    private bool PathValid(ReadOnlySpan<int> p)
    {
        if (p.Length < 2) return false;
        for (var i = 0; i < p.Length; i++) if (!_alive[p[i]]) return false;
        for (var i = 0; i + 1 < p.Length; i++) if (_latUs[p[i] * MaxNodes + p[i + 1]] < 0) return false;
        return true;
    }

    private void SwitchAffected()
    {
        for (var f = 0; f < MaxFlows; f++)
        {
            if (!_fActive[f]) continue;
            var path = _fPath.AsSpan(f * MaxNodes, _fPathLen[f]);
            if (PathValid(path)) continue;
            var bk = _fBackup.AsSpan(f * MaxNodes, _fBackupLen[f]);
            if (PathValid(bk))
            {
                bk.CopyTo(_fPath.AsSpan(f * MaxNodes));
                _fPathLen[f] = bk.Length;
                _fBackupLen[f] = 0;
                _fReroutes[f]++;
                Interlocked.Increment(ref _seamless);
            }
            else _fPathLen[f] = 0;
            _fDirty[f] = true;
        }
    }

    public int OpenFlow(int src, int dst, int startKbps = 3000)
    {
        lock (_gate)
        {
            for (var f = 0; f < MaxFlows; f++)
            {
                if (_fActive[f]) continue;
                _fActive[f] = true; _fSrc[f] = src; _fDst[f] = dst;
                var r = 0; while (r + 1 < Ladder.Length && Ladder[r + 1] <= startKbps) r++;
                _fRung[f] = r; _fKbps[f] = Ladder[r]; _fUp[f] = 0;
                _fFrames[f] = _fLost[f] = _fReroutes[f] = 0; _fFrameNo[f] = 0; _fLatEwma[f] = 0;
                _fPathLen[f] = _fBackupLen[f] = 0;
                Transpile(f);
                return f;
            }
            throw new InvalidOperationException("플로우 상한 " + MaxFlows);
        }
    }

    public void CloseFlow(int f) { lock (_gate) if ((uint)f < MaxFlows) _fActive[f] = false; }

    /// <summary>링크 비용(μs): 전파 + MTU 직렬화 + 혼잡(이용률 &gt; 0.7 에서 급증).</summary>
    private double Cost(int k)
    {
        var est = Math.Max(1f, _estKbps[k]);
        var ser = Mtu * 8.0 * 1000.0 / est;
        var u = _loadKbps[k] / est;
        var cong = u > 0.7f ? (u - 0.7f) * 20000.0 : 0;
        return _latUs[k] + ser + cong + _loss[k] * 50000.0;
    }

    /// <summary>Dijkstra(O(V²) · 무할당) — 경로를 dst 에 기록, 길이 반환(0 = 없음).</summary>
    private int ShortestPath(int s, int t, Span<int> dst)
    {
        var n = _nodeCount;
        for (var i = 0; i < n; i++) { _dist[i] = double.MaxValue; _prev[i] = -1; _done[i] = false; }
        if (!_alive[s] || !_alive[t] || _ban[s] || _ban[t]) return 0;
        _dist[s] = 0;
        for (var it = 0; it < n; it++)
        {
            var u = -1; var best = double.MaxValue;
            for (var i = 0; i < n; i++) if (!_done[i] && _dist[i] < best) { best = _dist[i]; u = i; }
            if (u < 0 || u == t) break;
            _done[u] = true;
            for (var v = 0; v < n; v++)
            {
                var k = u * MaxNodes + v;
                if (_latUs[k] < 0 || !_alive[v] || _ban[v] || _done[v]) continue;
                var d = best + Cost(k);
                if (d < _dist[v]) { _dist[v] = d; _prev[v] = u; }
            }
        }
        if (_dist[t] == double.MaxValue) return 0;
        var len = 0;
        for (var v = t; v >= 0; v = _prev[v]) len++;
        var i2 = len - 1;
        for (var v = t; v >= 0; v = _prev[v]) dst[i2--] = v;
        return len;
    }

    /// <summary>플로우 경로 트랜스파일 — 1차 + 노드 분리 백업(중간 노드 금지 후 재탐색).</summary>
    private void Transpile(int f)
    {
        var p = _fPath.AsSpan(f * MaxNodes, MaxNodes);
        Array.Clear(_ban);
        var len = ShortestPath(_fSrc[f], _fDst[f], p);
        _fPathLen[f] = len;
        for (var i = 1; i < len - 1; i++) _ban[p[i]] = true;
        var b = _fBackup.AsSpan(f * MaxNodes, MaxNodes);
        var bl = len > 0 ? ShortestPath(_fSrc[f], _fDst[f], b) : 0;
        // 직결 경로(중간 노드 없음)의 백업: 직결 링크 금지 후 탐색
        if (len == 2)
        {
            var k1 = _fSrc[f] * MaxNodes + _fDst[f];
            var save = _latUs[k1];
            _latUs[k1] = -1;
            Array.Clear(_ban);
            bl = ShortestPath(_fSrc[f], _fDst[f], b);
            _latUs[k1] = save;
        }
        _fBackupLen[f] = bl;
        Array.Clear(_ban);
        _fDirty[f] = false;
        Interlocked.Increment(ref _transpiles);
    }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f; }

    /// <summary>
    /// 1프레임 전송(무할당 · 시뮬 시계) — 경로 유효성 확인/백업 전환 → 조각화 → 홉별 컷스루 → 손실(재전송 1회) → 링크 부하/대역폭 추정 갱신 → ABR.
    /// </summary>
    public MeshDelivery SendFrame(int flow, int frameBytes, double dtSec)
    {
        lock (_gate)
        {
            if ((uint)flow >= MaxFlows || !_fActive[flow]) return default;
            var rerouted = 0;
            if (!PathValid(_fPath.AsSpan(flow * MaxNodes, _fPathLen[flow]))) { SwitchAffected(); rerouted = 1; }
            if (_fPathLen[flow] < 2) Transpile(flow);
            var len = _fPathLen[flow];
            var fn = ++_fFrameNo[flow];
            if (len < 2) { _fLost[flow]++; Interlocked.Increment(ref _drops); return new MeshDelivery(flow, fn, false, 0, 0, rerouted, double.NaN, double.NaN, 0, _fKbps[flow]); }
            var path = _fPath.AsSpan(flow * MaxNodes, len);
            frameBytes = Math.Max(1, frameBytes);
            var frags = (frameBytes + PayloadMtu - 1) / PayloadMtu;
            var wire = frameBytes + frags * MeshPacketHeader.Size;
            // 링크별: 전파 + 큐잉(이용률) · 병목 직렬화
            double prop = 0, maxFrameSer = 0, sumFragSer = 0, sf = 0; var lost = 0;
            for (var h = 0; h + 1 < len; h++)
            {
                var k = path[h] * MaxNodes + path[h + 1];
                var cap = Math.Max(1, _capKbps[k]);
                var frameSer = wire * 8.0 * 1000.0 / cap;       // μs
                var fragSer = Mtu * 8.0 * 1000.0 / cap;
                var util = _loadKbps[k] / cap;
                var queue = util >= 0.95f ? frameSer * 4 : util / (1 - util) * fragSer;
                prop += _latUs[k] + queue;
                maxFrameSer = Math.Max(maxFrameSer, frameSer);
                sumFragSer += fragSer;
                sf += _latUs[k] + queue + frameSer;
                // 손실(조각 단위 · 1회 재전송은 RTT 패널티)
                if (SimulateLoss && _loss[k] > 0)
                    for (var i = 0; i < frags; i++) if (Rand() < _loss[k]) { lost++; prop += _latUs[k] * 2; }
                // 부하 EWMA · 대역폭 추정(오프로드 kbps = 비트레이트) · RTT
                var offered = (float)(wire * 8.0 / Math.Max(1e-3, dtSec) / 1000.0);
                _loadKbps[k] = _loadKbps[k] * 0.7f + offered * 0.3f;
                var avail = Math.Max(cap * 0.05f, cap - (_loadKbps[k] - offered * 0.3f));
                var sample = Math.Min(cap, avail);   // 비대칭 추정: 하락 빠르게(혼잡 즉응) · 상승 느리게(진동 억제)
                _estKbps[k] = sample < _estKbps[k] ? _estKbps[k] * 0.5f + sample * 0.5f : _estKbps[k] * 0.9f + sample * 0.1f;
                _rttUs[k] = _rttUs[k] * 0.8f + (float)((_latUs[k] + queue) * 2) * 0.2f;
                Interlocked.Add(ref _packets, frags);
                Interlocked.Add(ref _bytes, wire);
            }
            // 컷스루: 첫 조각이 홉마다 조각 직렬화만큼 지연 + 병목에서 프레임 전체 직렬화
            var cut = prop + (sumFragSer - Mtu * 8.0 * 1000.0 / Math.Max(1, _capKbps[path[len - 2] * MaxNodes + path[len - 1]])) + maxFrameSer;
            var latMs = cut / 1000.0;
            _fFrames[flow]++;
            if (lost > 0) _fLost[flow] += lost;
            _fLatEwma[flow] = _fLatEwma[flow] <= 0 ? (float)latMs : _fLatEwma[flow] * 0.9f + (float)latMs * 0.1f;
            Abr(flow, path, lost, frags);
            // 혼잡이 생기면 다음 프레임 전 재트랜스파일 고려
            if (_fFrames[flow] % 30 == 0) _fDirty[flow] = true;
            if (_fDirty[flow]) Transpile(flow);
            return new MeshDelivery(flow, fn, true, frags, lost, rerouted, latMs, sf / 1000.0, len - 1, _fKbps[flow]);
        }
    }

    private void Abr(int f, ReadOnlySpan<int> path, int lost, int frags)
    {
        float bottleneck = float.MaxValue;
        for (var h = 0; h + 1 < path.Length; h++) bottleneck = MathF.Min(bottleneck, _estKbps[path[h] * MaxNodes + path[h + 1]]);
        var budget = bottleneck * 0.8f;
        var r = _fRung[f];
        if (lost > 0 && lost * 20 > frags) { r = Math.Max(0, r - 1); _fUp[f] = 0; }
        else if (Ladder[r] > budget)
        {
            while (r > 0 && Ladder[r] > budget) r--;
            _fUp[f] = 0;
        }
        else if (r + 1 < Ladder.Length && Ladder[r + 1] <= budget * 0.85f)
        {
            if (++_fUp[f] >= 3) { r++; _fUp[f] = 0; }
        }
        else _fUp[f] = 0;
        _fRung[f] = r; _fKbps[f] = Ladder[r];
    }

    public int BitrateOf(int f) => (uint)f < MaxFlows ? _fKbps[f] : 0;

    /// <summary>현재 경로 복사(노드 인덱스) — 길이.</summary>
    public int CopyPath(int f, Span<int> dst, bool backup = false)
    {
        lock (_gate)
        {
            if ((uint)f >= MaxFlows || !_fActive[f]) return 0;
            var len = backup ? _fBackupLen[f] : _fPathLen[f];
            (backup ? _fBackup : _fPath).AsSpan(f * MaxNodes, len).CopyTo(dst);
            return len;
        }
    }

    public MeshFlowStatus[] Flows()
    {
        lock (_gate)
        {
            var n = 0;
            for (var f = 0; f < MaxFlows; f++) if (_fActive[f]) n++;
            var arr = new MeshFlowStatus[n];
            var j = 0;
            for (var f = 0; f < MaxFlows; f++)
            {
                if (!_fActive[f]) continue;
                double lat = 0; float bw = float.MaxValue;
                var len = _fPathLen[f];
                for (var h = 0; h + 1 < len; h++) { var k = _fPath[f * MaxNodes + h] * MaxNodes + _fPath[f * MaxNodes + h + 1]; lat += _latUs[k]; bw = MathF.Min(bw, _estKbps[k]); }
                arr[j++] = new MeshFlowStatus(f, _fSrc[f], _fDst[f], _fKbps[f], Math.Max(0, len - 1), lat / 1000.0, len >= 2 ? bw / 1000.0 : 0, _fFrames[f], _fLost[f], _fReroutes[f], _fBackupLen[f] >= 2, _fRung[f]);
            }
            return arr;
        }
    }

    public float LatencyEwmaMs(int f) => (uint)f < MaxFlows ? _fLatEwma[f] : 0f;

    /// <summary>데모 글로벌 토폴로지(서울 데스크톱 · 모바일 · 에지 PoP · 클라우드 에지 · 시청자).</summary>
    public static RealtimeGlobalMediaMeshRouter BuildDemo(out int source, out int viewer)
    {
        var r = new RealtimeGlobalMediaMeshRouter();
        var seoul = r.AddNode("서울-스튜디오", MeshRole.Desktop, 0.62f, 0.38f);
        var phone = r.AddNode("모바일-현장", MeshRole.Mobile, 0.68f, 0.30f);
        var tokyo = r.AddNode("에지-도쿄", MeshRole.Edge, 0.74f, 0.42f);
        var sg = r.AddNode("에지-싱가포르", MeshRole.Edge, 0.60f, 0.70f);
        var fra = r.AddNode("클라우드에지-프랑크푸르트", MeshRole.CloudEdge, 0.30f, 0.30f);
        var lax = r.AddNode("에지-LA", MeshRole.Edge, 0.10f, 0.45f);
        var nyc = r.AddNode("시청자-뉴욕", MeshRole.Viewer, 0.20f, 0.36f);
        r.Link(seoul, phone, 4, 80, 0.002f);
        r.Link(seoul, tokyo, 16, 400);
        r.Link(seoul, sg, 38, 300);
        r.Link(tokyo, lax, 52, 500);
        r.Link(sg, fra, 82, 400);
        r.Link(lax, nyc, 31, 600);
        r.Link(fra, nyc, 40, 500);
        r.Link(tokyo, fra, 110, 200);
        r.Link(phone, tokyo, 20, 40, 0.01f);
        source = seoul; viewer = nyc;
        return r;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mesh.OnChange", ex); }
    }
}

/// <summary>
/// MSH1 UDP 전송(루프백/LAN 피어) — 송신: 헤더+페이로드 조각화 · 수신: 프레임 재조립(조각 비트맵) → 콜백. 수신 루프는 전용 스레드 · 무할당(고정 버퍼).
/// </summary>
public sealed class MeshUdpTransport : IDisposable
{
    public const int MaxFrameBytes = 1 << 20;
    private readonly Socket _sock;
    private readonly byte[] _tx = new byte[RealtimeGlobalMediaMeshRouter.Mtu], _rx = new byte[2048];
    private readonly byte[] _frame = new byte[MaxFrameBytes];
    private readonly ulong[] _got = new ulong[(MaxFrameBytes / RealtimeGlobalMediaMeshRouter.PayloadMtu + 64) / 64];
    private readonly Thread _thread;
    private volatile bool _stop;
    private uint _cur = uint.MaxValue;
    private int _have, _need, _len;
    private long _rxFrames, _rxPackets, _txPackets, _bad, _latUsSum;

    public MeshUdpTransport(int port = 0)
    {
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            try { _sock.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (SocketException) { } catch (PlatformNotSupportedException) { }
        }
        _sock.ReceiveBufferSize = 4 << 20;
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, port));
        Port = ((IPEndPoint)_sock.LocalEndPoint!).Port;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Mesh.Udp" };
        _thread.Start();
    }

    public int Port { get; }
    public long FramesReceived => Interlocked.Read(ref _rxFrames);
    public long PacketsReceived => Interlocked.Read(ref _rxPackets);
    public long PacketsSent => Interlocked.Read(ref _txPackets);
    public long BadPackets => Interlocked.Read(ref _bad);
    public double MeanLatencyMs => FramesReceived > 0 ? Interlocked.Read(ref _latUsSum) / 1000.0 / FramesReceived : 0;
    /// <summary>재조립 완료 프레임(플로우 · 프레임 번호 · 데이터) — 수신 스레드에서 호출.</summary>
    public Action<int, uint, ReadOnlyMemory<byte>>? OnFrame { get; set; }

    private static long NowUs() => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

    /// <summary>프레임 송신(조각화 · 무할당).</summary>
    public int Send(IPEndPoint to, ushort flow, uint frame, ReadOnlySpan<byte> data, ushort origin = 0, ushort dest = 0)
    {
        var frags = (data.Length + RealtimeGlobalMediaMeshRouter.PayloadMtu - 1) / RealtimeGlobalMediaMeshRouter.PayloadMtu;
        var now = NowUs();
        for (var i = 0; i < frags; i++)
        {
            var off = i * RealtimeGlobalMediaMeshRouter.PayloadMtu;
            var n = Math.Min(RealtimeGlobalMediaMeshRouter.PayloadMtu, data.Length - off);
            new MeshPacketHeader(flow, frame, (ushort)i, (ushort)frags, 0, 16, (ushort)n, now, origin, dest).Write(_tx);
            data.Slice(off, n).CopyTo(_tx.AsSpan(MeshPacketHeader.Size));
            _sock.SendTo(_tx.AsSpan(0, MeshPacketHeader.Size + n), SocketFlags.None, to);
            Interlocked.Increment(ref _txPackets);
        }
        return frags;
    }

    private void Loop()
    {
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            int n;
            try { n = _sock.ReceiveFrom(_rx, ref any); }
            catch (SocketException) { if (_stop) return; Thread.Sleep(1); continue; }
            catch (ObjectDisposedException) { return; }
            if (!MeshPacketHeader.TryRead(_rx.AsSpan(0, n), out var h)) { Interlocked.Increment(ref _bad); continue; }
            Interlocked.Increment(ref _rxPackets);
            if (h.Frame != _cur)
            {
                _cur = h.Frame; _have = 0; _need = h.Fragments; _len = 0;
                Array.Clear(_got, 0, (h.Fragments + 63) / 64 + 1);
            }
            var off = h.Fragment * RealtimeGlobalMediaMeshRouter.PayloadMtu;
            if (off + h.PayloadBytes > MaxFrameBytes) { Interlocked.Increment(ref _bad); continue; }
            ref var word = ref _got[h.Fragment >> 6];
            var bit = 1UL << (h.Fragment & 63);
            if ((word & bit) != 0) continue;
            word |= bit;
            _rx.AsSpan(MeshPacketHeader.Size, h.PayloadBytes).CopyTo(_frame.AsSpan(off));
            _len = Math.Max(_len, off + h.PayloadBytes);
            if (++_have == _need)
            {
                Interlocked.Increment(ref _rxFrames);
                Interlocked.Add(ref _latUsSum, NowUs() - h.SentMicros);
                try { OnFrame?.Invoke(h.Flow, h.Frame, _frame.AsMemory(0, _len)); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { PromptFlowStudio.Program.Log("Mesh.Udp.OnFrame", ex); }
                _cur = uint.MaxValue;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _sock.Dispose(); } catch (ObjectDisposedException) { }
    }
}
