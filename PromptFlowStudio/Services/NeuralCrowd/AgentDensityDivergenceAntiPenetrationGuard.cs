using System.Numerics;

namespace PromptFlowStudio.Services.NeuralCrowd;

/// <summary>
/// [PromptFlow Studio v17.29.0] 🛡 에이전트 과밀 밀도 발산 &amp; 메쉬 침투 방어 가드.
/// 사전 단계: NaN 격리 · 사전 투영 × 2(ORCA 가 깊은 겹침을 보고 invΔt 반발 속도를 내지 않도록) → 3×3 평활 밀도장(인/m²)
/// → 바이드만 기본 다이어그램 속도 상한 · 2D 카메라 절두체 계층 LoD(시야 밖 1/4 · 원거리 1/8 ORCA 주기).
/// 사후 단계: NaN 격리(최근 유효 상태 복원) · 속도/가속 클램프 → 비탄성(반발 계수 0) 위치 투영 Gauss-Seidel × 4
/// (과밀 셀은 포아송 디스크 최소 간격 여유 추가) · 벽 AABB 밀어내기 → 침투/튕김(|Δv| &gt; 2.5 m/s) 계측(ON/OFF 공통).
/// </summary>
public sealed class AgentDensityDivergenceAntiPenetrationGuard
{
    public const int G = RealtimeOrcaCrowdNavigationEngine.G, Cells = RealtimeOrcaCrowdNavigationEngine.Cells, MaxAgents = RealtimeOrcaCrowdNavigationEngine.MaxAgents;
    public const float DensityLimit = 4f, JamDensity = 5.4f, PoissonMargin = 0.03f, MaxSpeed = 4.5f, MaxDeltaV = 1.2f, BounceDeltaV = 2.5f, PenetrationTol = 0.01f;
    public const int Iterations = 4, PreIterations = 2;

    private readonly float[] _raw = new float[Cells];
    private readonly float[] _lastX = new float[MaxAgents], _lastY = new float[MaxAgents], _prevVx = new float[MaxAgents], _prevVy = new float[MaxAgents];

    public bool Enabled { get; set; } = true;
    public bool LodEnabled { get; set; } = true;
    public float[] Density { get; } = new float[Cells];
    public byte[] Lod { get; } = new byte[MaxAgents];

    // 2D 카메라 절두체(요 0° = +Y)
    public Vector2 CamPos { get; set; } = new(32f, 2f);
    public float CamYawDeg { get; set; }
    public float FovDeg { get; set; } = 70f;
    public float Far { get; set; } = 42f;

    public float PeakDensity { get; private set; }
    public int CellsOver { get; private set; }
    public int OverlapPairs { get; private set; }
    public float MaxPenetration { get; private set; }
    public float MeanPenetration { get; private set; }
    public int Bounces { get; private set; }
    public long BouncesTotal { get; private set; }
    public float PeakPenetrationSeen { get; private set; }
    public float MaxDeltaVSeen { get; private set; }
    public long AccelClamps { get; private set; }
    public long NanScrubbed { get; private set; }
    public long SpeedClamps { get; private set; }
    public long Corrections { get; private set; }
    public long WallPushes { get; private set; }
    public int Lod0 { get; private set; }
    public int Lod1 { get; private set; }
    public int Lod2 { get; private set; }
    public double LastPostUs { get; private set; }

    public void OnReset(RealtimeOrcaCrowdNavigationEngine e)
    {
        for (var i = 0; i < e.Count; i++) { _lastX[i] = e.PX[i]; _lastY[i] = e.PY[i]; _prevVx[i] = 0f; _prevVy[i] = 0f; }
        ResetMetrics();
    }

    public void ResetMetrics()
    {
        NanScrubbed = 0; SpeedClamps = 0; AccelClamps = 0; Corrections = 0; WallPushes = 0; BouncesTotal = 0; PeakPenetrationSeen = 0f; MaxDeltaVSeen = 0f;
    }

    /// <summary>바이드만 기본 다이어그램 v(ρ)/v₀(ρ ≤ 0.5 → 1 · 하한 0.12).</summary>
    public float SpeedFactor(int cell)
    {
        if (!Enabled) return 1f;
        var rho = Density[cell];
        if (rho <= 0.5f) return 1f;
        return MathF.Max(0.12f, 1f - MathF.Exp(-1.913f * (1f / rho - 1f / JamDensity)));
    }

    /// <summary>ORCA 해 갱신 여부(LoD 1 → 4 프레임 · LoD 2 → 8 프레임 주기 · 인덱스 위상 분산).</summary>
    public bool ShouldSolve(int i, long frame)
    {
        if (!Enabled || !LodEnabled) return true;
        return Lod[i] switch { 0 => true, 1 => ((i + frame) & 3) == 0, _ => ((i + frame) & 7) == 0 };
    }

    public void PreStep(RealtimeOrcaCrowdNavigationEngine e)
    {
        if (Enabled)
        {
            Scrub(e);
            e.RebuildHash();
            Project(e, PreIterations);
        }
        // 밀도: 셀 점유 → 3×3 상자 평활(인/m²)
        for (var c = 0; c < Cells; c++) _raw[c] = e.CellCount[c];
        float peak = 0f; var over = 0;
        for (var y = 0; y < G; y++)
            for (var x = 0; x < G; x++)
            {
                float s = 0f; var area = 0;
                for (var oy = -1; oy <= 1; oy++)
                {
                    var yy = y + oy;
                    if ((uint)yy >= G) continue;
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var xx = x + ox;
                        if ((uint)xx >= G) continue;
                        var k = yy * G + xx;
                        if (e.Solid(k)) continue;
                        s += _raw[k]; area++;
                    }
                }
                var d = area == 0 ? 0f : s / area;
                Density[y * G + x] = d;
                if (d > peak) peak = d;
                if (d > DensityLimit) over++;
            }
        PeakDensity = peak; CellsOver = over;

        // 절두체 LoD
        var yaw = CamYawDeg * (MathF.PI / 180f);
        var fwd = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        var cosHalf = MathF.Cos(FovDeg * 0.5f * (MathF.PI / 180f));
        int l0 = 0, l1 = 0, l2 = 0;
        for (var i = 0; i < e.Count; i++)
        {
            if (e.Active[i] == 0) { Lod[i] = 2; continue; }
            var rel = new Vector2(e.PX[i], e.PY[i]) - CamPos;
            var dist = rel.Length();
            var inside = dist <= Far && (dist < 1e-3f || Vector2.Dot(rel, fwd) >= cosHalf * dist);
            byte l = inside ? (byte)0 : dist <= Far * 1.5f ? (byte)1 : (byte)2;
            Lod[i] = l;
            if (l == 0) l0++; else if (l == 1) l1++; else l2++;
        }
        Lod0 = l0; Lod1 = l1; Lod2 = l2;
    }

    public void PostStep(RealtimeOrcaCrowdNavigationEngine e, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = e.Count;
        float[] px = e.PX, py = e.PY, vx = e.VX, vy = e.VY, rad = e.Radius;
        if (Enabled)
        {
            // 1) NaN 격리 · 속도/가속 클램프(프레임당 |Δv| ≤ 1.2 m/s)
            Scrub(e);
            for (var i = 0; i < n; i++)
            {
                if (e.Active[i] == 0) continue;
                float dvx = vx[i] - _prevVx[i], dvy = vy[i] - _prevVy[i];
                var dv2 = dvx * dvx + dvy * dvy;
                if (dv2 > MaxDeltaV * MaxDeltaV)
                {
                    var k = MaxDeltaV / MathF.Sqrt(dv2);
                    vx[i] = _prevVx[i] + dvx * k; vy[i] = _prevVy[i] + dvy * k;
                    px[i] = _lastX[i] + vx[i] * dt; py[i] = _lastY[i] + vy[i] * dt;
                    AccelClamps++;
                }
                var s2 = vx[i] * vx[i] + vy[i] * vy[i];
                if (s2 > MaxSpeed * MaxSpeed)
                {
                    var k = MaxSpeed / MathF.Sqrt(s2);
                    vx[i] *= k; vy[i] *= k;
                    px[i] = _lastX[i] + vx[i] * dt; py[i] = _lastY[i] + vy[i] * dt;
                    SpeedClamps++;
                }
            }
            // 2) 비탄성 투영(Gauss-Seidel) + 벽
            e.RebuildHash();
            Project(e, Iterations);
        }

        // 3) 계측(공통) — 최종 위치 해시
        e.RebuildHash();
        var pairs = 0; float maxPen = 0f, sumPen = 0f, maxDv = 0f; var bounces = 0;
        for (var i = 0; i < n; i++)
        {
            if (e.Active[i] == 0) continue;
            float x = px[i], y = py[i];
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
            var c = e.AgentCell[i];
            int cx = c % G, cy = c / G;
            for (var oy = -1; oy <= 1; oy++)
            {
                var yy = cy + oy;
                if ((uint)yy >= G) continue;
                for (var ox = -1; ox <= 1; ox++)
                {
                    var xx = cx + ox;
                    if ((uint)xx >= G) continue;
                    var cc = yy * G + xx;
                    for (var s = e.CellStart[cc]; s < e.CellStart[cc + 1]; s++)
                    {
                        var j = e.CellItems[s];
                        if (j <= i) continue;
                        float dx = px[j] - x, dy = py[j] - y;
                        var min = rad[i] + rad[j];
                        var d2 = dx * dx + dy * dy;
                        if (!(d2 < min * min)) continue;
                        var pen = min - MathF.Sqrt(d2);
                        if (pen <= PenetrationTol) continue;
                        pairs++; sumPen += pen;
                        if (pen > maxPen) maxPen = pen;
                    }
                }
            }
            // 튕김: 프레임 간 속도 급변 |Δv| > 2.5 m/s(가속 150 m/s² — 보행 불가능한 반발)
            float bx = vx[i], by = vy[i], ddx = bx - _prevVx[i], ddy = by - _prevVy[i];
            var dv = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (dv > BounceDeltaV) bounces++;
            if (dv > maxDv) maxDv = dv;
            _prevVx[i] = bx; _prevVy[i] = by;
            _lastX[i] = x; _lastY[i] = y;
        }
        OverlapPairs = pairs; MaxPenetration = maxPen; MeanPenetration = pairs == 0 ? 0f : sumPen / pairs;
        if (maxPen > PeakPenetrationSeen) PeakPenetrationSeen = maxPen;
        Bounces = bounces; BouncesTotal += bounces;
        if (maxDv > MaxDeltaVSeen) MaxDeltaVSeen = maxDv;
        LastPostUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Scrub(RealtimeOrcaCrowdNavigationEngine e)
    {
        for (var i = 0; i < e.Count; i++)
        {
            if (e.Active[i] == 0) continue;
            if (float.IsFinite(e.PX[i]) && float.IsFinite(e.PY[i]) && float.IsFinite(e.VX[i]) && float.IsFinite(e.VY[i])) continue;
            e.PX[i] = _lastX[i]; e.PY[i] = _lastY[i]; e.VX[i] = e.VY[i] = 0f; e.NVX[i] = e.NVY[i] = 0f;
            NanScrubbed++;
        }
    }

    private void Project(RealtimeOrcaCrowdNavigationEngine e, int iterations)
    {
        float[] px = e.PX, py = e.PY, vx = e.VX, vy = e.VY, rad = e.Radius;
        var n = e.Count;
        for (var it = 0; it < iterations; it++)
        {
            for (var i = 0; i < n; i++)
            {
                if (e.Active[i] == 0) continue;
                var c = e.AgentCell[i];
                int cx = c % G, cy = c / G;
                var margin = Density[c] > DensityLimit ? PoissonMargin : 0f;
                for (var oy = -1; oy <= 1; oy++)
                {
                    var yy = cy + oy;
                    if ((uint)yy >= G) continue;
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var xx = cx + ox;
                        if ((uint)xx >= G) continue;
                        var cc = yy * G + xx;
                        for (var s = e.CellStart[cc]; s < e.CellStart[cc + 1]; s++)
                        {
                            var j = e.CellItems[s];
                            if (j <= i) continue;
                            float dx = px[j] - px[i], dy = py[j] - py[i];
                            var min = rad[i] + rad[j] + margin;
                            var d2 = dx * dx + dy * dy;
                            if (!(d2 < min * min)) continue;
                            var d = MathF.Sqrt(d2);
                            float nx, ny;
                            if (d > 1e-5f) { nx = dx / d; ny = dy / d; }
                            else { var a = (i * 2654435761u % 6283u) * 1e-3f; nx = MathF.Cos(a); ny = MathF.Sin(a); }
                            var corr = (min - d) * 0.5f;
                            px[i] -= nx * corr; py[i] -= ny * corr; px[j] += nx * corr; py[j] += ny * corr;
                            // 반발 계수 0: 접근 법선 상대 속도 제거(튕김 억제)
                            var vn = (vx[j] - vx[i]) * nx + (vy[j] - vy[i]) * ny;
                            if (vn < 0f) { var h = vn * 0.5f; vx[i] += nx * h; vy[i] += ny * h; vx[j] -= nx * h; vy[j] -= ny * h; }
                            Corrections++;
                        }
                    }
                }
                PushOutOfWalls(e, i);
            }
        }
    }

    private void PushOutOfWalls(RealtimeOrcaCrowdNavigationEngine e, int i)
    {
        float x = e.PX[i], y = e.PY[i], r = e.Radius[i];
        int cx = Math.Clamp((int)x, 0, G - 1), cy = Math.Clamp((int)y, 0, G - 1);
        for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                int sx = cx + ox, sy = cy + oy;
                if ((uint)sx >= G || (uint)sy >= G || !e.Solid(sy * G + sx)) continue;
                float qx = Math.Clamp(x, sx, sx + 1f), qy = Math.Clamp(y, sy, sy + 1f);
                float dx = x - qx, dy = y - qy;
                var d2 = dx * dx + dy * dy;
                if (d2 >= r * r) continue;
                float nx, ny, d;
                if (d2 > 1e-10f) { d = MathF.Sqrt(d2); nx = dx / d; ny = dy / d; }
                else
                {
                    // 중심이 셀 내부: 최단 면으로 탈출
                    float l = x - sx, rr = sx + 1f - x, b = y - sy, t = sy + 1f - y;
                    var m = MathF.Min(MathF.Min(l, rr), MathF.Min(b, t));
                    if (m == l) { nx = -1f; ny = 0f; d = -l; } else if (m == rr) { nx = 1f; ny = 0f; d = -rr; } else if (m == b) { nx = 0f; ny = -1f; d = -b; } else { nx = 0f; ny = 1f; d = -t; }
                }
                var push = r - d;
                x += nx * push; y += ny * push;
                var vn = e.VX[i] * nx + e.VY[i] * ny;
                if (vn < 0f) { e.VX[i] -= nx * vn; e.VY[i] -= ny * vn; }
                WallPushes++;
            }
        e.PX[i] = x; e.PY[i] = y;
    }
}
