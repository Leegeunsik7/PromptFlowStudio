using System.Numerics;
using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Services.NeuralCrowd;

/// <summary>ORCA 반평면(점 P · 방향 D — 허용 속도는 D 의 왼쪽).</summary>
public struct OrcaLine
{
    public Vector2 P, D;
}

/// <summary>[v17.29.0] 군중 SIMD 커널 — 적분은 원소별 곱/합만 사용해 스칼라 쌍둥이와 비트 동일.</summary>
public static class CrowdKernels
{
    public static void IntegrateScalar(Span<float> px, Span<float> py, Span<float> vx, Span<float> vy, ReadOnlySpan<float> nvx, ReadOnlySpan<float> nvy, float dt, int n)
    {
        for (var i = 0; i < n; i++)
        {
            float x = nvx[i], y = nvy[i];
            px[i] = px[i] + x * dt; py[i] = py[i] + y * dt;
            vx[i] = x; vy[i] = y;
        }
    }

    public static void IntegrateSimd(Span<float> px, Span<float> py, Span<float> vx, Span<float> vy, ReadOnlySpan<float> nvx, ReadOnlySpan<float> nvy, float dt, int n)
    {
        var w = Vector<float>.Count;
        var vdt = new Vector<float>(dt);
        var i = 0;
        for (; i <= n - w; i += w)
        {
            var x = new Vector<float>(nvx.Slice(i, w));
            var y = new Vector<float>(nvy.Slice(i, w));
            (new Vector<float>(px.Slice(i, w)) + x * vdt).CopyTo(px.Slice(i, w));
            (new Vector<float>(py.Slice(i, w)) + y * vdt).CopyTo(py.Slice(i, w));
            x.CopyTo(vx.Slice(i, w)); y.CopyTo(vy.Slice(i, w));
        }
        IntegrateScalar(px[i..], py[i..], vx[i..], vy[i..], nvx[i..], nvy[i..], dt, n - i);
    }

    /// <summary>Σ|v| (표시용 리덕션 — 시뮬레이션 분기에 쓰지 않음).</summary>
    public static float SpeedSumSimd(ReadOnlySpan<float> vx, ReadOnlySpan<float> vy, int n)
    {
        var w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        var i = 0;
        for (; i <= n - w; i += w)
        {
            var x = new Vector<float>(vx.Slice(i, w)); var y = new Vector<float>(vy.Slice(i, w));
            acc += Vector.SquareRoot(x * x + y * y);
        }
        var s = Vector.Dot(acc, Vector<float>.One);
        for (; i < n; i++) s += MathF.Sqrt(vx[i] * vx[i] + vy[i] * vy[i]);
        return s;
    }
}

/// <summary>
/// [PromptFlow Studio v17.29.0] 👥 실시간 ORCA 흐름장 군중 내비게이션 엔진.
/// 64×64 m 광장(1 m 셀 · v17.27 지형 경사 비용 · v17.26 물리 장애물/잔해 셀)에서 목표 A/B · 비상구 3 개의 다익스트라 흐름장을
/// 고정 힙으로 구축하고, 에이전트 간 속도 장애물을 ORCA 반평면 + 2D 선형 계획법(LP2 → 실패 시 LP3 최소 침투)으로 풀어 상호 회피한다.
/// 모든 버퍼는 생성 시 확보(최대 4096) — 프레임 경로는 스택 스팬만 사용한다.
/// </summary>
public sealed class RealtimeOrcaCrowdNavigationEngine
{
    public const int G = 64, Cells = G * G, MaxAgents = 4096, Fields = 3, FieldExit = 2, MaxNeighbors = 10, MaxStatic = 8, MaxLines = MaxNeighbors + MaxStatic + 2;
    public const float NeighborDist = 2.5f, TimeHorizon = 1.5f, TimeHorizonObst = 0.6f, ArriveRadius = 3.5f, TerrainScale = 6f, Eps = 1e-5f;
    public const int DoorX0 = 29, DoorX1 = 34, WallY = 32;

    private readonly AgentDensityDivergenceAntiPenetrationGuard _guard;
    private readonly byte[] _baseSolid = new byte[Cells], _solid = new byte[Cells], _exit = new byte[Cells];
    private readonly float[] _terrain = new float[Cells], _cost = new float[Cells];
    private readonly float[] _dist = new float[Fields * Cells], _dirX = new float[Fields * Cells], _dirY = new float[Fields * Cells];
    private readonly float[] _heapKey = new float[Cells * 8];
    private readonly int[] _heapVal = new int[Cells * 8];
    private readonly int[] _pois = new int[Cells * 4];
    private bool _fieldsDirty = true;

    public RealtimeOrcaCrowdNavigationEngine(AgentDensityDivergenceAntiPenetrationGuard guard) : this(guard, null) { }

    public RealtimeOrcaCrowdNavigationEngine(AgentDensityDivergenceAntiPenetrationGuard guard, WorldExpansion.GenerativeNeuralWorldExpansionEngine? world)
    {
        _guard = guard;
        // 지형(v17.27): 셀 중심 높이 → 경사 비용(1 + 2·|∇h| · 최대 3)
        for (var c = 0; c < Cells; c++)
            _terrain[c] = world is null ? 0f : world.Height((c % G - G / 2) * 0.25f, (c / G - G / 2) * 0.25f) * TerrainScale;
        for (var y = 0; y < G; y++)
            for (var x = 0; x < G; x++)
            {
                var c = y * G + x;
                var gx = (_terrain[y * G + Math.Min(x + 1, G - 1)] - _terrain[y * G + Math.Max(x - 1, 0)]) * 0.5f;
                var gy = (_terrain[Math.Min(y + 1, G - 1) * G + x] - _terrain[Math.Max(y - 1, 0) * G + x]) * 0.5f;
                _cost[c] = MathF.Min(3f, 1f + 2f * MathF.Sqrt(gx * gx + gy * gy));
            }
        BuildLayout();
        GoalA = new Vector2(32f, 54f); GoalB = new Vector2(32f, 10f);
        AttractPoint = new Vector2(48f, 46f);
        SetAgentCount(1000);
    }

    public AgentDensityDivergenceAntiPenetrationGuard Guard => _guard;
    public bool UseSimd { get; set; } = true;
    public int Count { get; private set; }
    public int ActiveCount { get; private set; }
    public long Frame { get; private set; }
    public float SimTime { get; private set; }
    public Vector2 GoalA { get; private set; }
    public Vector2 GoalB { get; private set; }
    /// <summary>군중 주목 지점(v17.12 메인 액터 위치).</summary>
    public Vector2 AttractPoint { get; set; }
    public int OrcaSolves { get; private set; }
    public int LpFallbacks { get; private set; }
    public long Arrivals { get; private set; }
    public int Evacuated { get; private set; }
    public int Nan { get; private set; }
    public float MeanSpeed { get; private set; }
    public int FieldBuilds { get; private set; }
    public double LastFieldMs { get; private set; }
    public int RubbleCells { get; private set; }

    // SoA 상태
    public float[] PX { get; } = new float[MaxAgents];
    public float[] PY { get; } = new float[MaxAgents];
    public float[] VX { get; } = new float[MaxAgents];
    public float[] VY { get; } = new float[MaxAgents];
    public float[] NVX { get; } = new float[MaxAgents];
    public float[] NVY { get; } = new float[MaxAgents];
    public float[] Radius { get; } = new float[MaxAgents];
    public float[] Speed0 { get; } = new float[MaxAgents];
    public byte[] Group { get; } = new byte[MaxAgents];
    public byte[] Active { get; } = new byte[MaxAgents];
    /// <summary>감정 합성기 출력: 속도 배율 · 비상구 가중 · 주목 지점 가중.</summary>
    public float[] SpeedScale { get; } = new float[MaxAgents];
    public float[] ExitWeight { get; } = new float[MaxAgents];
    public float[] AttractWeight { get; } = new float[MaxAgents];

    // 공간 해시(1 m 셀 계수 정렬)
    public int[] CellCount { get; } = new int[Cells];
    public int[] CellStart { get; } = new int[Cells + 1];
    public int[] CellItems { get; } = new int[MaxAgents];
    public int[] AgentCell { get; } = new int[MaxAgents];

    public bool Solid(int c) => _solid[c] != 0;
    public bool IsExit(int c) => _exit[c] != 0;
    public float Terrain(int c) => _terrain[c];
    public float Cost(int c) => _cost[c];
    public float FieldDistance(int f, int c) => _dist[f * Cells + c];
    public Vector2 FieldDir(int f, int c) => new(_dirX[f * Cells + c], _dirY[f * Cells + c]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CellOf(int i)
    {
        float x = PX[i], y = PY[i];
        if (!float.IsFinite(x) || !float.IsFinite(y)) return AgentCell[i];
        return Math.Clamp((int)y, 0, G - 1) * G + Math.Clamp((int)x, 0, G - 1);
    }

    private void BuildLayout()
    {
        Array.Clear(_baseSolid); Array.Clear(_exit);
        for (var k = 0; k < G; k++) { _baseSolid[k] = 1; _baseSolid[(G - 1) * G + k] = 1; _baseSolid[k * G] = 1; _baseSolid[k * G + G - 1] = 1; }
        // 비상구 4 개(상·하 테두리 4 m 개구)
        for (var x = 4; x < 8; x++) { OpenExit(x, 0); OpenExit(x, G - 1); OpenExit(G - 1 - x, 0); OpenExit(G - 1 - x, G - 1); }
        // 중앙 분리벽(2 m 두께) + 4 m 출입문 병목
        for (var x = 1; x < G - 1; x++)
            if (x < DoorX0 || x > DoorX1) { _baseSolid[WallY * G + x] = 1; _baseSolid[(WallY + 1) * G + x] = 1; }
        // 기둥(v17.26 물리 장애물 프록시)
        Block(14, 14, 3); Block(46, 16, 3); Block(16, 46, 3); Block(44, 52, 2); Block(24, 24, 2); Block(40, 40, 2);
        Array.Copy(_baseSolid, _solid, Cells);
        RubbleCells = 0;
        _fieldsDirty = true;
    }

    private void OpenExit(int x, int y) { _baseSolid[y * G + x] = 0; _exit[y * G + x] = 1; }

    private void Block(int x0, int y0, int s)
    {
        for (var y = y0; y < y0 + s; y++) for (var x = x0; x < x0 + s; x++) _baseSolid[y * G + x] = 1;
    }

    /// <summary>군중 압착(서지) 스트레스: 중심 방향으로 위치를 factor 배 수축(겹침 유발 · 가드 시험용).</summary>
    public void Compress(Vector2 center, float factor, float radius)
    {
        for (var i = 0; i < Count; i++)
        {
            if (Active[i] == 0) continue;
            var p = new Vector2(PX[i], PY[i]);
            var d = p - center;
            if (d.LengthSquared() > radius * radius) continue;
            var q = center + d * factor;
            var c = Math.Clamp((int)q.Y, 0, G - 1) * G + Math.Clamp((int)q.X, 0, G - 1);
            if (_solid[c] != 0) continue;
            PX[i] = q.X; PY[i] = q.Y;
        }
    }

    public void SetGoal(int which, Vector2 p)
    {
        p = Vector2.Clamp(p, new Vector2(2f), new Vector2(G - 2f));
        if (which == 0) GoalA = p; else GoalB = p;
        _fieldsDirty = true;
    }

    /// <summary>v17.26 파괴 잔해: 원형 영역을 고체로 막고 흐름장 재구축(출구 · 목표 셀 제외).</summary>
    public void AddRubble(Vector2 c, float r)
    {
        int x0 = Math.Max(1, (int)(c.X - r)), x1 = Math.Min(G - 2, (int)(c.X + r)), y0 = Math.Max(1, (int)(c.Y - r)), y1 = Math.Min(G - 2, (int)(c.Y + r));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var k = y * G + x;
                if (_solid[k] != 0 || _exit[k] != 0) continue;
                if (Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f), c) > r * r) continue;
                if (Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f), GoalA) < 4f || Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f), GoalB) < 4f) continue;
                _solid[k] = 1; RubbleCells++;
            }
        _fieldsDirty = true;
    }

    public void ClearRubble() { Array.Copy(_baseSolid, _solid, Cells); RubbleCells = 0; _fieldsDirty = true; }

    /// <summary>포아송 디스크(최소 간격 0.75 m · 결정적 다트 투척) 배치: 짝수 그룹은 남쪽 → 목표 A, 홀수는 북쪽 → 목표 B.</summary>
    public void SetAgentCount(int n)
    {
        n = Math.Clamp(n, 0, MaxAgents);
        Array.Fill(_pois, -1);
        const float minD = 0.75f;
        uint s = 0x9E3779B9u;
        var placed = 0;
        for (var attempt = 0; placed < n && attempt < n * 60; attempt++)
        {
            var grp = (byte)(placed & 1);
            s = s * 1664525u + 1013904223u; var fx = (s >> 8) * (1f / 16777216f);
            s = s * 1664525u + 1013904223u; var fy = (s >> 8) * (1f / 16777216f);
            var x = 1.5f + fx * (G - 3f);
            var y = grp == 0 ? 1.5f + fy * (WallY - 3f) : WallY + 3.5f + fy * (G - WallY - 5f);
            int cx = (int)x, cy = (int)y, c = cy * G + cx;
            if (_solid[c] != 0) continue;
            var ok = true;
            for (var oy = -1; oy <= 1 && ok; oy++)
                for (var ox = -1; ox <= 1 && ok; ox++)
                {
                    int nx = cx + ox, ny = cy + oy;
                    if ((uint)nx >= G || (uint)ny >= G) continue;
                    var b = (ny * G + nx) * 4;
                    for (var k = 0; k < 4; k++)
                    {
                        var j = _pois[b + k];
                        if (j < 0) break;
                        var dx = PX[j] - x; var dy = PY[j] - y;
                        if (dx * dx + dy * dy < minD * minD) { ok = false; break; }
                    }
                }
            if (!ok) continue;
            var slot = -1;
            for (var k = 0; k < 4; k++) if (_pois[c * 4 + k] < 0) { slot = k; break; }
            if (slot < 0) continue;
            _pois[c * 4 + slot] = placed;
            var i = placed++;
            PX[i] = x; PY[i] = y; VX[i] = VY[i] = NVX[i] = NVY[i] = 0f;
            s = s * 1664525u + 1013904223u;
            Radius[i] = 0.24f + 0.06f * ((s >> 8) * (1f / 16777216f));
            s = s * 1664525u + 1013904223u;
            Speed0[i] = 1.15f + 0.3f * ((s >> 8) * (1f / 16777216f));
            Group[i] = grp; Active[i] = 1;
            SpeedScale[i] = 1f; ExitWeight[i] = 0f; AttractWeight[i] = 0f;
            AgentCell[i] = c;
        }
        for (var i = placed; i < MaxAgents; i++) { Active[i] = 0; PX[i] = PY[i] = VX[i] = VY[i] = NVX[i] = NVY[i] = 0f; }
        Count = placed; ActiveCount = placed;
        Arrivals = 0; Evacuated = 0; Nan = 0; Frame = 0; SimTime = 0f;
        _guard.OnReset(this);
        RebuildHash();
    }

    public void InjectFault(int i)
    {
        if ((uint)i >= (uint)Count) return;
        PX[i] = float.NaN; PY[i] = float.NaN; VX[i] = float.NaN; VY[i] = float.NaN;
    }

    // ── 흐름장(다익스트라 · 8 이웃 · 벽 인접 패널티) ──
    public void RebuildFieldsIfDirty()
    {
        if (!_fieldsDirty) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildField(0, GoalA, false);
        BuildField(1, GoalB, false);
        BuildField(FieldExit, default, true);
        _fieldsDirty = false;
        FieldBuilds++;
        LastFieldMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void BuildField(int f, Vector2 goal, bool exits)
    {
        var dist = _dist.AsSpan(f * Cells, Cells);
        dist.Fill(float.PositiveInfinity);
        var hn = 0;
        // 우측 통행 차선: 북행(목표가 벽 북쪽) 흐름은 출입문 동쪽 절반, 남행은 서쪽 절반을 선호(반대편 +3 비용)
        var north = goal.Y > WallY + 1;
        var mid = (DoorX0 + DoorX1 + 1) * 0.5f;
        if (exits)
        {
            for (var c = 0; c < Cells; c++) if (_exit[c] != 0) { dist[c] = 0f; Push(ref hn, 0f, c); }
        }
        else
        {
            // 목표 원판(반경 2 m) 다중 소스 — 점 목표 주변 정체 방지
            for (var oy = -2; oy <= 2; oy++)
                for (var ox = -2; ox <= 2; ox++)
                {
                    int gx = Math.Clamp((int)goal.X + ox, 0, G - 1), gy = Math.Clamp((int)goal.Y + oy, 0, G - 1);
                    var c = gy * G + gx;
                    if (_solid[c] != 0 || ox * ox + oy * oy > 5) continue;
                    dist[c] = 0f; Push(ref hn, 0f, c);
                }
        }
        while (hn > 0)
        {
            Pop(ref hn, out var d, out var c);
            if (d > dist[c]) continue;
            int cx = c % G, cy = c / G;
            for (var k = 0; k < 8; k++)
            {
                int ox = k switch { 0 => 1, 1 => -1, 2 => 0, 3 => 0, 4 => 1, 5 => 1, 6 => -1, _ => -1 };
                int oy = k switch { 0 => 0, 1 => 0, 2 => 1, 3 => -1, 4 => 1, 5 => -1, 6 => 1, _ => -1 };
                int nx = cx + ox, ny = cy + oy;
                if ((uint)nx >= G || (uint)ny >= G) continue;
                var n = ny * G + nx;
                if (_solid[n] != 0) continue;
                if (k >= 4 && (_solid[cy * G + nx] != 0 || _solid[ny * G + cx] != 0)) continue;   // 모서리 절단 금지
                var step = (k >= 4 ? 1.41421356f : 1f) * 0.5f * (_cost[c] + _cost[n]) + (NearWall(nx, ny) ? 0.6f : 0f);
                if (!exits && ny >= WallY - 8 && ny <= WallY + 9 && nx >= DoorX0 - 8 && nx <= DoorX1 + 8 && (north ? nx + 0.5f < mid : nx + 0.5f > mid)) step += 3f;
                var nd = d + step;
                if (nd < dist[n]) { dist[n] = nd; Push(ref hn, nd, n); }
            }
        }
        // 방향: 최소 거리 이웃(동일 모서리 규칙)
        for (var c = 0; c < Cells; c++)
        {
            float bx = 0f, by = 0f;
            if (_solid[c] == 0 && dist[c] > 0f && float.IsFinite(dist[c]))
            {
                int cx = c % G, cy = c / G;
                var best = dist[c];
                for (var oy = -1; oy <= 1; oy++)
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = cx + ox, ny = cy + oy;
                        if ((uint)nx >= G || (uint)ny >= G) continue;
                        var n = ny * G + nx;
                        if (_solid[n] != 0) continue;
                        if (ox != 0 && oy != 0 && (_solid[cy * G + nx] != 0 || _solid[ny * G + cx] != 0)) continue;
                        if (dist[n] < best) { best = dist[n]; bx = ox; by = oy; }
                    }
                var l = MathF.Sqrt(bx * bx + by * by);
                if (l > 0f) { bx /= l; by /= l; }
            }
            _dirX[f * Cells + c] = bx; _dirY[f * Cells + c] = by;
        }
    }

    private bool NearWall(int x, int y)
    {
        for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                int nx = x + ox, ny = y + oy;
                if ((uint)nx < G && (uint)ny < G && _solid[ny * G + nx] != 0) return true;
            }
        return false;
    }

    private void Push(ref int n, float key, int val)
    {
        if (n >= _heapKey.Length) return;
        var i = n++;
        while (i > 0)
        {
            var p = (i - 1) >> 1;
            if (_heapKey[p] <= key) break;
            _heapKey[i] = _heapKey[p]; _heapVal[i] = _heapVal[p]; i = p;
        }
        _heapKey[i] = key; _heapVal[i] = val;
    }

    private void Pop(ref int n, out float key, out int val)
    {
        key = _heapKey[0]; val = _heapVal[0];
        var lk = _heapKey[--n]; var lv = _heapVal[n];
        var i = 0;
        while (true)
        {
            var l = 2 * i + 1;
            if (l >= n) break;
            var r = l + 1;
            var m = r < n && _heapKey[r] < _heapKey[l] ? r : l;
            if (_heapKey[m] >= lk) break;
            _heapKey[i] = _heapKey[m]; _heapVal[i] = _heapVal[m]; i = m;
        }
        _heapKey[i] = lk; _heapVal[i] = lv;
    }

    /// <summary>흐름장 방향 쌍선형 샘플(통과 셀만 가중 · 정규화).</summary>
    public Vector2 SampleDir(int f, float x, float y)
    {
        float gx = x - 0.5f, gy = y - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(gx), 0, G - 2), y0 = Math.Clamp((int)MathF.Floor(gy), 0, G - 2);
        float tx = Math.Clamp(gx - x0, 0f, 1f), ty = Math.Clamp(gy - y0, 0f, 1f);
        var o = f * Cells;
        float sx = 0f, sy = 0f;
        for (var k = 0; k < 4; k++)
        {
            int cx = x0 + (k & 1), cy = y0 + (k >> 1);
            var c = cy * G + cx;
            if (_solid[c] != 0) continue;
            var w = ((k & 1) != 0 ? tx : 1f - tx) * ((k >> 1) != 0 ? ty : 1f - ty);
            sx += w * _dirX[o + c]; sy += w * _dirY[o + c];
        }
        var l = MathF.Sqrt(sx * sx + sy * sy);
        if (l < 1e-4f)
        {
            var c = Math.Clamp((int)y, 0, G - 1) * G + Math.Clamp((int)x, 0, G - 1);
            return new Vector2(_dirX[o + c], _dirY[o + c]);
        }
        return new Vector2(sx / l, sy / l);
    }

    /// <summary>공간 해시 계수 정렬(활성 에이전트만).</summary>
    public void RebuildHash()
    {
        Array.Clear(CellCount);
        var n = Count;
        for (var i = 0; i < n; i++)
        {
            if (Active[i] == 0) continue;
            var c = CellOf(i);
            AgentCell[i] = c;
            CellCount[c]++;
        }
        var acc = 0;
        for (var c = 0; c < Cells; c++) { CellStart[c] = acc; acc += CellCount[c]; }
        CellStart[Cells] = acc;
        Array.Clear(CellCount);
        for (var i = 0; i < n; i++)
        {
            if (Active[i] == 0) continue;
            var c = AgentCell[i];
            CellItems[CellStart[c] + CellCount[c]++] = i;
        }
    }

    /// <summary>1 프레임(무할당): 흐름장 → 해시 → 가드 사전(밀도 · LoD) → 선호 속도 → ORCA LP → 적분 → 도착/대피 → 가드 사후(침투 투영).</summary>
    public void Step(float dt)
    {
        RebuildFieldsIfDirty();
        RebuildHash();
        _guard.PreStep(this);
        var n = Count;
        var solves = 0; var fallbacks = 0;
        Span<OrcaLine> lines = stackalloc OrcaLine[MaxLines];
        Span<OrcaLine> proj = stackalloc OrcaLine[MaxLines];
        Span<int> nb = stackalloc int[MaxNeighbors];
        Span<float> nd = stackalloc float[MaxNeighbors];
        for (var i = 0; i < n; i++)
        {
            if (Active[i] == 0) { NVX[i] = 0f; NVY[i] = 0f; continue; }
            var pref = PreferredVelocity(i, out var maxSpeed);
            if (!_guard.ShouldSolve(i, Frame))
            {
                // LoD 코스팅: ORCA 생략 — 선호 속도로 완화
                NVX[i] = VX[i] + (pref.X - VX[i]) * 0.25f;
                NVY[i] = VY[i] + (pref.Y - VY[i]) * 0.25f;
                continue;
            }
            solves++;
            var v = SolveOrca(i, pref, maxSpeed, dt, lines, proj, nb, nd, ref fallbacks);
            NVX[i] = v.X; NVY[i] = v.Y;
        }
        OrcaSolves = solves; LpFallbacks = fallbacks;

        if (UseSimd) CrowdKernels.IntegrateSimd(PX, PY, VX, VY, NVX, NVY, dt, n);
        else CrowdKernels.IntegrateScalar(PX, PY, VX, VY, NVX, NVY, dt, n);

        // 경계 · 도착(그룹 반전) · 대피(비상구 셀)
        for (var i = 0; i < n; i++)
        {
            if (Active[i] == 0) continue;
            PX[i] = Math.Clamp(PX[i], 0.3f, G - 0.3f);
            PY[i] = Math.Clamp(PY[i], 0.3f, G - 0.3f);
            if (!float.IsFinite(PX[i])) continue;
            var goal = Group[i] == 0 ? GoalA : GoalB;
            float gx = goal.X - PX[i], gy = goal.Y - PY[i];
            if (gx * gx + gy * gy < ArriveRadius * ArriveRadius && ExitWeight[i] < 0.5f) { Group[i] ^= 1; Arrivals++; }
            if (ExitWeight[i] >= 0.5f)
            {
                var c = Math.Clamp((int)PY[i], 0, G - 1) * G + Math.Clamp((int)PX[i], 0, G - 1);
                if (_exit[c] != 0) { Active[i] = 0; VX[i] = VY[i] = NVX[i] = NVY[i] = 0f; Evacuated++; }
            }
        }

        _guard.PostStep(this, dt);

        var act = 0; var nan = 0;
        for (var i = 0; i < n; i++)
        {
            if (Active[i] == 0) continue;
            act++;
            if (!float.IsFinite(PX[i]) || !float.IsFinite(PY[i]) || !float.IsFinite(VX[i]) || !float.IsFinite(VY[i])) nan++;
        }
        ActiveCount = act; Nan = nan;
        MeanSpeed = act == 0 ? 0f : CrowdKernels.SpeedSumSimd(VX, VY, n) / act;
        Frame++; SimTime += dt;
    }

    private Vector2 PreferredVelocity(int i, out float maxSpeed)
    {
        float x = PX[i], y = PY[i];
        var goal = Group[i] == 0 ? GoalA : GoalB;
        var tg = goal - new Vector2(x, y);
        var dg = tg.Length();
        var dir = dg < 2f && dg > 1e-4f ? tg / dg : SampleDir(Group[i], x, y);
        var ew = Math.Clamp(ExitWeight[i], 0f, 1f);
        var aw = Math.Clamp(AttractWeight[i], 0f, 1f) * (1f - ew);
        if (ew > 0f) dir = dir * (1f - ew) + SampleDir(FieldExit, x, y) * ew;
        if (aw > 0f)
        {
            var ta = AttractPoint - new Vector2(x, y);
            var da = ta.Length();
            aw *= Math.Clamp((da - 4f) / 3f, 0f, 1f);   // 액터 주변 4 m 링에서 정지
            if (da > 1e-4f) dir = dir * (1f - aw) + ta / da * aw;
        }
        // 우측 통행 관습(밀도 > 0.8 인/m²): 양방향 흐름의 차선 형성 유도
        if (_guard.Density[AgentCell[i]] > 0.8f) dir += new Vector2(dir.Y, -dir.X) * 0.3f;
        var l = dir.Length();
        // 대칭 파괴 미세 섭동(결정적 해시)
        var h = (uint)(i * 73856093) ^ (uint)(Frame * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
        var jit = ((h & 1023) / 1023f - 0.5f) * 0.04f;
        var speed = Speed0[i] * Math.Clamp(SpeedScale[i], 0f, 3f) * _guard.SpeedFactor(AgentCell[i]);
        maxSpeed = MathF.Min(4f, MathF.Max(0.2f, speed * 1.25f));
        if (l < 1e-4f) return Vector2.Zero;
        dir /= l;
        float cs = MathF.Cos(jit), sn = MathF.Sin(jit);
        return new Vector2(dir.X * cs - dir.Y * sn, dir.X * sn + dir.Y * cs) * speed;
    }

    private Vector2 SolveOrca(int i, Vector2 pref, float maxSpeed, float dt, Span<OrcaLine> lines, Span<OrcaLine> proj, Span<int> nb, Span<float> nd, ref int fallbacks)
    {
        var p = new Vector2(PX[i], PY[i]);
        var v = new Vector2(VX[i], VY[i]);
        var r = Radius[i];
        var count = 0;
        var invTh = 1f / TimeHorizon;
        var invObst = 1f / TimeHorizonObst;
        var invDt = 1f / dt;

        // 1) 정적 장애물: 인접 고체 셀 AABB 최근접점(책임 1)
        int cx = Math.Clamp((int)p.X, 0, G - 1), cy = Math.Clamp((int)p.Y, 0, G - 1);
        var stat = 0;
        for (var oy = -1; oy <= 1 && stat < MaxStatic; oy++)
            for (var ox = -1; ox <= 1 && stat < MaxStatic; ox++)
            {
                int sx = cx + ox, sy = cy + oy;
                if ((uint)sx >= G || (uint)sy >= G || _solid[sy * G + sx] == 0) continue;
                var q = new Vector2(Math.Clamp(p.X, sx, sx + 1f), Math.Clamp(p.Y, sy, sy + 1f));
                var rel = q - p;
                if (rel.LengthSquared() > (r + 0.9f) * (r + 0.9f)) continue;
                lines[count++] = MakeLine(rel, v, r, invObst, invDt, 1f, v);
                stat++;
            }

        // 2) 이웃 에이전트 k-최근접(삽입 정렬)
        var k = 0;
        var nd2 = NeighborDist * NeighborDist;
        for (var oy = -2; oy <= 2; oy++)
        {
            var yy = cy + oy;
            if ((uint)yy >= G) continue;
            for (var ox = -2; ox <= 2; ox++)
            {
                var xx = cx + ox;
                if ((uint)xx >= G) continue;
                var c = yy * G + xx;
                for (var s = CellStart[c]; s < CellStart[c + 1]; s++)
                {
                    var j = CellItems[s];
                    if (j == i) continue;
                    float dx = PX[j] - p.X, dy = PY[j] - p.Y;
                    var d2 = dx * dx + dy * dy;
                    if (!(d2 < nd2)) continue;
                    if (k == MaxNeighbors && d2 >= nd[k - 1]) continue;
                    var at = k < MaxNeighbors ? k++ : MaxNeighbors - 1;
                    while (at > 0 && nd[at - 1] > d2) { nd[at] = nd[at - 1]; nb[at] = nb[at - 1]; at--; }
                    nd[at] = d2; nb[at] = j;
                }
            }
        }
        for (var a = 0; a < k; a++)
        {
            var j = nb[a];
            var rel = new Vector2(PX[j], PY[j]) - p;
            lines[count++] = MakeLine(rel, v - new Vector2(VX[j], VY[j]), r + Radius[j], invTh, invDt, 0.5f, v);
        }

        var result = Vector2.Zero;
        var fail = LinearProgram2(lines[..count], maxSpeed, pref, false, ref result);
        if (fail < count) { fallbacks++; LinearProgram3(lines[..count], proj, fail, maxSpeed, ref result); }
        return result;
    }

    /// <summary>ORCA 반평면 구성(rel = 상대 위치 · relVel = vi − vj · resp = 책임 0.5 상호 / 1 정적).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OrcaLine MakeLine(Vector2 rel, Vector2 relVel, float combR, float invTh, float invDt, float resp, Vector2 v)
    {
        var distSq = rel.LengthSquared();
        var combSq = combR * combR;
        Vector2 u, dir;
        if (distSq > combSq)
        {
            var w = relVel - invTh * rel;
            var wLenSq = w.LengthSquared();
            var dot1 = Vector2.Dot(w, rel);
            if (dot1 < 0f && dot1 * dot1 > combSq * wLenSq)
            {
                var wLen = MathF.Sqrt(wLenSq);
                var unitW = w / wLen;
                dir = new Vector2(unitW.Y, -unitW.X);
                u = (combR * invTh - wLen) * unitW;
            }
            else
            {
                var leg = MathF.Sqrt(distSq - combSq);
                dir = Det(rel, w) > 0f
                    ? new Vector2(rel.X * leg - rel.Y * combR, rel.X * combR + rel.Y * leg) / distSq
                    : -new Vector2(rel.X * leg + rel.Y * combR, -rel.X * combR + rel.Y * leg) / distSq;
                u = Vector2.Dot(relVel, dir) * dir - relVel;
            }
        }
        else
        {
            var w = relVel - invDt * rel;
            var wLen = w.Length();
            var unitW = wLen > Eps ? w / wLen : new Vector2(1f, 0f);
            dir = new Vector2(unitW.Y, -unitW.X);
            u = (combR * invDt - wLen) * unitW;
        }
        return new OrcaLine { P = v + resp * u, D = dir };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Det(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool LinearProgram1(ReadOnlySpan<OrcaLine> lines, int no, float radius, Vector2 opt, bool dirOpt, ref Vector2 result)
    {
        var ln = lines[no];
        var dot = Vector2.Dot(ln.P, ln.D);
        var disc = dot * dot + radius * radius - ln.P.LengthSquared();
        if (disc < 0f) return false;
        var sq = MathF.Sqrt(disc);
        float tl = -dot - sq, tr = -dot + sq;
        for (var i = 0; i < no; i++)
        {
            var den = Det(ln.D, lines[i].D);
            var num = Det(lines[i].D, ln.P - lines[i].P);
            if (MathF.Abs(den) <= Eps)
            {
                if (num < 0f) return false;
                continue;
            }
            var t = num / den;
            if (den >= 0f) tr = MathF.Min(tr, t); else tl = MathF.Max(tl, t);
            if (tl > tr) return false;
        }
        if (dirOpt) result = Vector2.Dot(opt, ln.D) > 0f ? ln.P + tr * ln.D : ln.P + tl * ln.D;
        else result = ln.P + Math.Clamp(Vector2.Dot(ln.D, opt - ln.P), tl, tr) * ln.D;
        return true;
    }

    private static int LinearProgram2(ReadOnlySpan<OrcaLine> lines, float radius, Vector2 opt, bool dirOpt, ref Vector2 result)
    {
        if (dirOpt) result = opt * radius;
        else if (opt.LengthSquared() > radius * radius) result = Vector2.Normalize(opt) * radius;
        else result = opt;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Det(lines[i].D, lines[i].P - result) > 0f)
            {
                var temp = result;
                if (!LinearProgram1(lines, i, radius, opt, dirOpt, ref result)) { result = temp; return i; }
            }
        }
        return lines.Length;
    }

    /// <summary>실행 불가능(과밀) 시 최대 침투 최소화 — 투영 반평면으로 LP2 재귀.</summary>
    private static void LinearProgram3(ReadOnlySpan<OrcaLine> lines, Span<OrcaLine> proj, int begin, float radius, ref Vector2 result)
    {
        var distance = 0f;
        for (var i = begin; i < lines.Length; i++)
        {
            if (!(Det(lines[i].D, lines[i].P - result) > distance)) continue;
            var pc = 0;
            for (var j = 0; j < i; j++)
            {
                var det = Det(lines[i].D, lines[j].D);
                Vector2 pt;
                if (MathF.Abs(det) <= Eps)
                {
                    if (Vector2.Dot(lines[i].D, lines[j].D) > 0f) continue;
                    pt = 0.5f * (lines[i].P + lines[j].P);
                }
                else pt = lines[i].P + Det(lines[j].D, lines[i].P - lines[j].P) / det * lines[i].D;
                var dd = lines[j].D - lines[i].D;
                var dl = dd.Length();
                proj[pc++] = new OrcaLine { P = pt, D = dl > Eps ? dd / dl : lines[i].D };
            }
            var temp = result;
            if (LinearProgram2(proj[..pc], radius, new Vector2(-lines[i].D.Y, lines[i].D.X), true, ref result) < pc) result = temp;
            distance = Det(lines[i].D, lines[i].P - result);
        }
    }
}
