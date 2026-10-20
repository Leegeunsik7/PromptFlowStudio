using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.GpuSwarm;

/// <summary>분배 타일(값 타입) — Cost = 상대 비용 가중 픽셀 · PredMs = 배정 노드 예측 완료 시각.</summary>
public struct SwarmTile
{
    public int X, Y, W, H, Node;
    public float Cost, PredMs;
    public readonly int Pixels => W * H;
    public readonly int Bytes => W * H * 3 * sizeof(float);
}

/// <summary>
/// [PromptFlow Studio v16.96.0] ⚡ 동적 타일/프레임 클라우드 렌더 스웜 로드밸런서 합성기.
/// · 비용 맵(16×9 셀 · 상대 µs/px EMA · 평균 1 정규화 SIMD): 완료 타일 실측 µs/px ÷ 노드 평균 → 셀 갱신 → 비싼 영역(구/안개/광원)일수록 잘게 분할.
/// · 적응 타일: 목표 타일 수 = 건강 노드 레인 합 × TilesPerLane · 명시적 스택 kd 분할(긴 축 · 8 px 정렬 · 최소 MinTile) — 비용 &gt; 총비용/목표일 때만 분할.
/// · LPT 스케줄: 비용 내림차순(Span 정렬 · 무할당) → 노드별 예측 완료 = (부하+비용)/(Mpx·레인) + RTT + 바이트/대역폭 최소 노드 배정 · VRAM 잔량 승인.
/// · 프레임 모드: 다프레임 배치(디퓨전 시퀀스 등)를 같은 비용 모델로 노드에 LPT 배정.
/// </summary>
public sealed class DynamicCloudRenderSwarmLoadBalancer
{
    public const int MaxTiles = 512, CostW = 16, CostH = 9, Align = 8;
    private static readonly int CostPad = (CostW * CostH + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    private readonly DistributedHeterogeneousGpuTopologyEngine _topo;
    private readonly SwarmTile[] _tiles = new SwarmTile[MaxTiles];
    private readonly float[] _keys = new float[MaxTiles];
    private readonly int[] _order = new int[MaxTiles];
    private readonly int[] _stack = new int[MaxTiles * 8];
    private readonly float[] _cost = new float[CostPad];
    private readonly double[] _nodeLoad = new double[DistributedHeterogeneousGpuTopologyEngine.MaxNodes];
    private readonly long[] _nodeBytes = new long[DistributedHeterogeneousGpuTopologyEngine.MaxNodes];
    private readonly int[] _nodeTiles = new int[DistributedHeterogeneousGpuTopologyEngine.MaxNodes];
    private int _count, _w, _h;
    private long _plans, _vramDenied;

    public DynamicCloudRenderSwarmLoadBalancer(DistributedHeterogeneousGpuTopologyEngine topo)
    {
        _topo = topo;
        ResetCostMap();
    }

    public DistributedHeterogeneousGpuTopologyEngine Topology => _topo;
    public int TilesPerLane { get; set; } = 3;
    public int MinTile { get; set; } = 16;
    public float CostAlpha { get; set; } = 0.25f;
    public bool Adaptive { get; set; } = true;
    public int Count => _count;
    public long Plans => Interlocked.Read(ref _plans);
    public long VramDenied => Interlocked.Read(ref _vramDenied);
    public double PredictedMakespanMs { get; private set; }
    public double TotalCost { get; private set; }
    public ref readonly SwarmTile Tile(int i) => ref _tiles[i];
    public int NodeTiles(int n) => _nodeTiles[n];
    public double NodePredMs(int n) => _nodeLoad[n];
    public float CellCost(int cx, int cy) => _cost[cy * CostW + cx];

    public void ResetCostMap()
    {
        for (var i = 0; i < CostPad; i++) _cost[i] = i < CostW * CostH ? 1f : 0f;
    }

    /// <summary>영역 상대 비용(픽셀 × 셀 비용 겹침 적분).</summary>
    public float RegionCost(int x, int y, int w, int h)
    {
        if (_w <= 0 || _h <= 0) return w * h;
        float sum = 0;
        int cx0 = x * CostW / _w, cx1 = Math.Min(CostW - 1, (x + w - 1) * CostW / _w);
        int cy0 = y * CostH / _h, cy1 = Math.Min(CostH - 1, (y + h - 1) * CostH / _h);
        for (var cy = cy0; cy <= cy1; cy++)
        {
            int ya = Math.Max(y, cy * _h / CostH), yb = Math.Min(y + h, (cy + 1) * _h / CostH);
            if (yb <= ya) continue;
            for (var cx = cx0; cx <= cx1; cx++)
            {
                int xa = Math.Max(x, cx * _w / CostW), xb = Math.Min(x + w, (cx + 1) * _w / CostW);
                if (xb > xa) sum += (xb - xa) * (yb - ya) * _cost[cy * CostW + cx];
            }
        }
        return sum;
    }

    /// <summary>프레임 계획(무할당) → 타일 수. 건강 노드 없음 = 로컬 페일백 노드 단일 배정.</summary>
    public int Plan(int width, int height)
    {
        _w = width; _h = height;
        var topo = _topo;
        var nodes = topo.Count;
        var lanes = 0;
        for (var n = 0; n < nodes; n++) { _nodeLoad[n] = 0; _nodeBytes[n] = 0; _nodeTiles[n] = 0; if (topo.Healthy(n)) lanes += topo.Lanes(n); }
        TotalCost = RegionCost(0, 0, width, height);
        var desired = Math.Clamp(Math.Max(1, lanes) * TilesPerLane, 1, MaxTiles);
        var target = TotalCost / desired;
        // kd 분할(명시적 스택: x,y,w,h)
        _count = 0;
        var sp = 0;
        Push(ref sp, 0, 0, width, height);
        while (sp > 0)
        {
            sp -= 4;
            int x = _stack[sp], y = _stack[sp + 1], w = _stack[sp + 2], h = _stack[sp + 3];
            var c = Adaptive ? RegionCost(x, y, w, h) : (float)(w * (double)h * TotalCost / ((double)width * height));
            var splitW = w >= h;
            var len = splitW ? w : h;
            var canSplit = len >= MinTile * 2 && _count + sp / 4 + 2 < MaxTiles && sp + 8 <= _stack.Length;
            if (c > target && canSplit)
            {
                var half = Math.Max(MinTile, len / 2 / Align * Align);
                if (splitW) { Push(ref sp, x, y, half, h); Push(ref sp, x + half, y, w - half, h); }
                else { Push(ref sp, x, y, w, half); Push(ref sp, x, y + half, w, h - half); }
                continue;
            }
            ref var t = ref _tiles[_count++];
            t.X = x; t.Y = y; t.W = w; t.H = h; t.Cost = c; t.Node = -1; t.PredMs = 0;
        }
        // LPT: 비용 내림차순
        for (var i = 0; i < _count; i++) { _keys[i] = -_tiles[i].Cost; _order[i] = i; }
        _keys.AsSpan(0, _count).Sort(_order.AsSpan(0, _count));
        var fallback = topo.LocalFallbackNode;
        double makespan = 0;
        for (var k = 0; k < _count; k++)
        {
            ref var t = ref _tiles[_order[k]];
            var best = -1; var bestMs = double.MaxValue;
            for (var n = 0; n < nodes; n++)
            {
                if (!topo.Healthy(n)) continue;
                if (topo.VramFree(n) - _nodeBytes[n] < t.Bytes) { Interlocked.Increment(ref _vramDenied); continue; }
                var ms = Predict(n, _nodeLoad[n], t.Cost, t.Bytes);
                if (ms < bestMs) { bestMs = ms; best = n; }
            }
            if (best < 0) { best = fallback; bestMs = Predict(best, _nodeLoad[best], t.Cost, t.Bytes); }
            t.Node = best; t.PredMs = (float)bestMs;
            _nodeLoad[best] += ComputeMs(best, t.Cost);
            _nodeBytes[best] += t.Bytes;
            _nodeTiles[best]++;
            if (bestMs > makespan) makespan = bestMs;
        }
        PredictedMakespanMs = makespan;
        Interlocked.Increment(ref _plans);
        return _count;
    }

    private void Push(ref int sp, int x, int y, int w, int h)
    {
        _stack[sp] = x; _stack[sp + 1] = y; _stack[sp + 2] = w; _stack[sp + 3] = h; sp += 4;
    }

    /// <summary>노드 순수 연산 ms(비용 픽셀 ÷ (Mpx·1000 px/ms × 레인)).</summary>
    public double ComputeMs(int n, float cost) => cost / (Math.Max(1e-3, _topo.Mpx(n)) * 1000.0 * _topo.Lanes(n));

    /// <summary>예측 완료 ms = (기존 부하 + 연산) + 원격 오버헤드(RTT + 전송).</summary>
    public double Predict(int n, double load, float cost, int bytes)
    {
        var ms = load + ComputeMs(n, cost);
        if (!_topo.IsLocal(n))
        {
            var bw = Math.Max(1e-3, _topo.BandwidthMBs(n));
            ms += _topo.RttMs(n) + bytes / (bw * 1024 * 1024 / 1000.0);
        }
        return ms;
    }

    /// <summary>
    /// 완료 타일 피드백(무할당): 실측 µs/px ÷ 노드 평균 µs/px(1/Mpx) = 상대 비용 → 겹치는 셀 EMA → 평균 1 재정규화(SIMD).
    /// </summary>
    public void Observe(int x, int y, int w, int h, int node, long computeTicks)
    {
        if (_w <= 0 || w * h <= 0) return;
        var usPerPx = computeTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / (w * h);
        var nodeUsPerPx = 1.0 / Math.Max(1e-3, _topo.Mpx(node));
        var rel = (float)Math.Clamp(usPerPx / nodeUsPerPx, 0.05, 20);
        var a = Math.Clamp(CostAlpha, 0.01f, 1f);
        int cx0 = x * CostW / _w, cx1 = Math.Min(CostW - 1, (x + w - 1) * CostW / _w);
        int cy0 = y * CostH / _h, cy1 = Math.Min(CostH - 1, (y + h - 1) * CostH / _h);
        lock (_cost)
        {
            for (var cy = cy0; cy <= cy1; cy++)
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    ref var c = ref _cost[cy * CostW + cx];
                    c += (rel - c) * a;
                }
        }
    }

    /// <summary>비용 맵 평균 1 재정규화(프레임 경계 · SIMD).</summary>
    public void NormalizeCostMap()
    {
        lock (_cost)
        {
            var mean = DistributedHeterogeneousGpuTopologyEngine.SimdSum(_cost) / (CostW * CostH);
            if (mean <= 0) { ResetCostMap(); return; }
            var v = MemoryMarshal.Cast<float, Vector<float>>(_cost.AsSpan());
            var inv = new Vector<float>(1f / mean);
            for (var k = 0; k < v.Length; k++) v[k] *= inv;
        }
    }

    /// <summary>
    /// 프레임 모드 LPT 배정(동일 비용 프레임 · 원격은 프레임 전체 전송 오버헤드 포함) → 예측 makespan ms. assignment[i] = 노드.
    /// </summary>
    public double PlanFrames(int frames, float frameCost, int frameBytes, Span<int> assignment, Span<double> nodeLoad)
    {
        var nodes = _topo.Count;
        nodeLoad[..nodes].Clear();
        var fallback = _topo.LocalFallbackNode;
        double makespan = 0;
        for (var f = 0; f < frames; f++)
        {
            var best = -1; var bestMs = double.MaxValue;
            for (var n = 0; n < nodes; n++)
            {
                if (!_topo.Healthy(n) || _topo.VramTotal(n) < frameBytes) continue;
                var ms = Predict(n, nodeLoad[n], frameCost, frameBytes);
                if (ms < bestMs) { bestMs = ms; best = n; }
            }
            if (best < 0) { best = fallback; bestMs = Predict(best, nodeLoad[best], frameCost, frameBytes); }
            assignment[f] = best;
            nodeLoad[best] += ComputeMs(best, frameCost);
            if (bestMs > makespan) makespan = bestMs;
        }
        return makespan;
    }
}
