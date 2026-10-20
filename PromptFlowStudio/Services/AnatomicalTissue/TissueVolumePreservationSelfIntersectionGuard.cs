using System.Numerics;

namespace PromptFlowStudio.Services.AnatomicalTissue;

/// <summary>
/// [PromptFlow Studio v17.14.0] 🧊 사면체 체적 비압축성 &amp; 메쉬 자가 관통 방지 가드(<see cref="Process"/> 무할당).
///   ▸ 사면체화: 튜브 링 i · i+1 와 축 중심(뼈 고정 · 질량 무한)으로 만든 프리즘(a b c · a′ b′ c′)마다 3 사면체 → 5 튜브 × 23 × 16 × 3 = 5,520 사면체 · 레스트 부호 체적 V₀.
///   ▸ 비압축 제약(단방향 · 단조): 정점 체적비 ρᵥ = 인접 사면체 V/V₀ 최소 &lt; α(기본 0.75) → 자기 링 중심 기준 방사 단면 팽창 √(α/ρ)(≤ 1.5배 · 이동 ≤ 12 mm) ·
///     역전은 레스트 반경 복원 · 인접 최소비 개선 시에만 채택(새 역전 불가) · 4 회. 근육 팽창(체적 증가)은 허용 · 관절 축 압축 손실은 벌지로 보상 · 접촉 락 정점(주름면)은 제외하고 별도 집계.
///   ▸ 자가 교차 락: 팔꿈치/무릎 안쪽 = 관절 이등분 평면(법선 = 근위 방향 − 원위 방향) 클리핑(상완 ↔ 하완 · 허벅지 ↔ 정강이 피부가 주름면에서 맞닿고 교차 금지) ·
///     팔 ↔ 몸통 = 링 타원 음함수(현재 링 중심 · 전후/측방 반경 · 인접 링 보간) 외부 투영(+0.5 mm) · 락 플래그 · 클리핑 후 체적 3 회 재투영 → 재클리핑.
/// </summary>
public sealed class TissueVolumePreservationSelfIntersectionGuard
{
    private const int V = NeuralAnatomicalMuscleFasciaEngine.V, Rings = NeuralAnatomicalMuscleFasciaEngine.Rings, Segs = NeuralAnatomicalMuscleFasciaEngine.Segs, Tubes = NeuralAnatomicalMuscleFasciaEngine.Tubes;
    public const int TetCount = Tubes * (Rings - 1) * Segs * 3;
    public const float MaxStep = 0.012f, Eps = 0.0005f;

    private readonly int[] _tet = new int[TetCount * 4];
    private readonly float[] _v0 = new float[TetCount], _ratio = new float[TetCount];
    private readonly int[] _jointRing = new int[Tubes];
    private readonly Vector3[] _rc = new Vector3[Tubes * Rings], _n0 = new Vector3[Tubes * Rings], _n1 = new Vector3[Tubes * Rings];
    private readonly float[] _rf = new float[Tubes * Rings], _rs = new float[Tubes * Rings];
    private readonly bool[] _locked = new bool[V];
    private readonly float[] _tubeV0 = new float[Tubes], _tubeV = new float[Tubes];
    private readonly int[] _active = new int[TetCount];
    private readonly int[] _incStart = new int[V + 1], _inc = new int[TetCount * 4];
    private int _activeCount, _contact;
    private float _minFree;

    public TissueVolumePreservationSelfIntersectionGuard(NeuralAnatomicalMuscleFasciaEngine engine)
    {
        Engine = engine;
        var n = 0;
        for (var t = 0; t < Tubes; t++)
            for (var i = 0; i < Rings - 1; i++)
                for (var j = 0; j < Segs; j++)
                {
                    int a = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, i, j), b = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, i, (j + 1) % Segs);
                    int a1 = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, i + 1, j), b1 = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, i + 1, (j + 1) % Segs);
                    int c = V + t * Rings + i, c1 = V + t * Rings + i + 1;
                    Set(n++, a, b, c, a1); Set(n++, b, c, a1, b1); Set(n++, c, a1, b1, c1);
                }
        for (var k = 0; k < TetCount; k++)
        {
            _v0[k] = Volume(k, true);
            _tubeV0[k / (TetCount / Tubes)] += MathF.Abs(_v0[k]);
        }
        // 내부 관절 링(팔꿈치 · 무릎)
        for (var t = 1; t < Tubes; t++)
        {
            var ch = NeuralAnatomicalMuscleFasciaEngine.Chains[t];
            var ja = Vector3.Distance(engine.RestJoint(ch[0]), engine.RestJoint(ch[1]));
            var best = 0; var bd = float.MaxValue;
            for (var i = 0; i < Rings; i++) { var d = MathF.Abs(engine.RingArc(t * Rings + i) - ja); if (d < bd) { bd = d; best = i; } }
            _jointRing[t] = best;
        }
        for (var k = 0; k < TetCount * 4; k++) if (_tet[k] < V) _incStart[_tet[k] + 1]++;
        for (var v = 0; v < V; v++) _incStart[v + 1] += _incStart[v];
        var fill = new int[V];
        for (var k = 0; k < TetCount; k++)
            for (var c = 0; c < 4; c++)
            {
                var vi = _tet[k * 4 + c];
                if (vi < V) _inc[_incStart[vi] + fill[vi]++] = k;
            }
    }

    private void Set(int k, int a, int b, int c, int d) { _tet[k * 4] = a; _tet[k * 4 + 1] = b; _tet[k * 4 + 2] = c; _tet[k * 4 + 3] = d; }

    public NeuralAnatomicalMuscleFasciaEngine Engine { get; }
    public bool VolumeGuard { get; set; } = true;
    public bool IntersectionGuard { get; set; } = true;
    /// <summary>국소 체적 하한 비 α.</summary>
    public float MinRatio { get; set; } = 0.75f;
    public int Iterations { get; set; } = 4;
    public float MinVolumeRatioBefore { get; private set; } = 1f;
    public float MinVolumeRatioAfter { get; private set; } = 1f;
    public int CollapsedBefore { get; private set; }
    public int CollapsedAfter { get; private set; }
    public int InvertedBefore { get; private set; }
    public int InvertedAfter { get; private set; }
    public int IntersectionsBefore { get; private set; }
    public int IntersectionsAfter { get; private set; }
    public float MaxIntersectionMm { get; private set; }
    public float ResidualIntersectionMm { get; private set; }
    public int LockedVertices { get; private set; }
    /// <summary>접촉 락 정점에 걸린 압착 사면체(주름면 접촉 · 붕괴 집계 제외).</summary>
    public int ContactCompressed { get; private set; }
    /// <summary>비접촉 사면체 최소 체적비(가드 후).</summary>
    public float MinFreeRatioAfter { get; private set; } = 1f;
    public double LastVolumeUs { get; private set; }
    public double LastIntersectionUs { get; private set; }
    public float TubeVolumeRatio(int t) => _tubeV0[t] > 0 ? _tubeV[t] / _tubeV0[t] : 1f;
    public float TetRatio(int k) => _ratio[k];
    public bool Locked(int v) => _locked[v];
    public int JointRing(int tube) => _jointRing[tube];

    private Vector3 Pos(int i, bool rest)
    {
        var e = Engine;
        if (rest) return i < V ? e.RestVertex(i) : e.RestCenter(i - V);
        return i < V ? new Vector3(e.Px[i], e.Py[i], e.Pz[i]) : e.Center[i - V];
    }

    private float Volume(int k, bool rest)
    {
        Vector3 x0 = Pos(_tet[k * 4], rest), x1 = Pos(_tet[k * 4 + 1], rest), x2 = Pos(_tet[k * 4 + 2], rest), x3 = Pos(_tet[k * 4 + 3], rest);
        return Vector3.Dot(x1 - x0, Vector3.Cross(x2 - x0, x3 - x0)) / 6f;
    }

    private void Move(int i, Vector3 d)
    {
        if (i >= V) return;
        var e = Engine;
        e.Px[i] += d.X; e.Py[i] += d.Y; e.Pz[i] += d.Z;
    }

    private void Measure(out float minR, out int collapsed, out int inverted)
    {
        minR = float.MaxValue; collapsed = 0; inverted = 0; _activeCount = 0; _contact = 0; _minFree = float.MaxValue;
        Array.Clear(_tubeV);
        for (var k = 0; k < TetCount; k++)
        {
            var r = Volume(k, false) / _v0[k];
            _ratio[k] = r;
            if (!(TetLocked(k)) && r < _minFree) _minFree = r;
            _tubeV[k / (TetCount / Tubes)] += MathF.Abs(_v0[k]) * r;
            if (r < minR) minR = r;
            if (r < MinRatio - 1e-3f)
            {
                if (TetLocked(k)) { _contact++; continue; }
                collapsed++;
            }
            if (r < MinRatio + 0.05f) _active[_activeCount++] = k;
            if (r <= 0f && !TetLocked(k)) inverted++;
        }
    }

    /// <summary>체적 보존 → 자가 교차 락(엔진 정점 직접 수정 · 무할당).</summary>
    public void Process()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Array.Clear(_locked);
        Measure(out var minB, out var colB, out var invB);
        MinVolumeRatioBefore = minB; CollapsedBefore = colB; InvertedBefore = invB;
        if (VolumeGuard && colB > 0) SolveVolume(Iterations);
        LastVolumeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;

        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        RingFrames();
        float depth0 = 0f;
        var before = Intersections(false, ref depth0);
        IntersectionsBefore = before; MaxIntersectionMm = depth0 * 1000f;
        if (IntersectionGuard && before > 0)
        {
            var d = 0f;
            Intersections(true, ref d);
            if (VolumeGuard) SolveVolume(2);
            RingFrames();
            d = 0f;
            Intersections(true, ref d);
        }
        var dr = 0f;
        RingFrames();
        IntersectionsAfter = Intersections(false, ref dr);
        ResidualIntersectionMm = dr * 1000f;
        var lk = 0;
        for (var v = 0; v < V; v++) if (_locked[v]) lk++;
        LockedVertices = lk;
        LastIntersectionUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency;

        Measure(out var minA, out var colA, out var invA);
        MinVolumeRatioAfter = minA; CollapsedAfter = colA; InvertedAfter = invA; ContactCompressed = _contact; MinFreeRatioAfter = _minFree;
    }

    /// <summary>정점 인접 사면체 최소 체적비(현재 위치).</summary>
    private float VertexMinRatio(int v)
    {
        var m = float.MaxValue;
        for (var a = _incStart[v]; a < _incStart[v + 1]; a++)
        {
            var k = _inc[a];
            var r = Volume(k, false) / _v0[k];
            if (r < m) m = r;
        }
        return m;
    }

    /// <summary>
    /// 정점 단위 가우스-자이델(단조): ρᵥ = 인접 사면체 최소 체적비 &lt; α → 자기 링 중심 기준 방사 단면 팽창 √(α/ρ)(≤ 1.5배) 또는
    /// 역전/반전 시 레스트 반경 방향 복원을 시도하고, 인접 최소비가 개선될 때만 채택(불개선 시 절반 보폭 1 회 재시도) — 새 역전 생성 불가.
    /// 축 압축으로 잃은 조직 체적을 단면 팽창(벌지)으로 보상 · 접촉 락 정점 제외.
    /// </summary>
    private void SolveVolume(int iterations)
    {
        var e = Engine;
        for (var it = 0; it < iterations; it++)
        {
            var moved = 0;
            for (var k = 0; k < TetCount; k++) _ratio[k] = Volume(k, false) / _v0[k];
            for (var v = 0; v < V; v++)
            {
                if (_locked[v]) continue;
                var cached = float.MaxValue;
                for (var a = _incStart[v]; a < _incStart[v + 1]; a++) if (_ratio[_inc[a]] < cached) cached = _ratio[_inc[a]];
                if (cached >= MinRatio) continue;
                var rho = VertexMinRatio(v);
                if (rho >= MinRatio) continue;
                var ring = v / Segs;
                var c = e.Center[ring];
                var x = new Vector3(e.Px[v], e.Py[v], e.Pz[v]);
                var t = e.Tangent(v);
                var q = x - c;
                var axial = t * Vector3.Dot(q, t);
                var rad = q - axial;
                Vector3 nr;
                if (rho <= 0.05f || rad.LengthSquared() < 1e-10f || Vector3.Dot(rad, e.Normal(v)) <= 0f) nr = e.Normal(v) * e.RestRadius(v);
                else nr = rad * MathF.Min(1.5f, MathF.Sqrt(MinRatio / rho));
                var d = Clamp(c + axial + nr - x);
                for (var tries = 0; tries < 2; tries++, d *= 0.5f)
                {
                    e.Px[v] = x.X + d.X; e.Py[v] = x.Y + d.Y; e.Pz[v] = x.Z + d.Z;
                    if (VertexMinRatio(v) > rho + 1e-4f) { moved++; break; }
                    e.Px[v] = x.X; e.Py[v] = x.Y; e.Pz[v] = x.Z;
                }
            }
            if (moved == 0) break;
        }
    }

    private bool IsLocked(int i) => i < V && _locked[i];
    private bool TetLocked(int k) => IsLocked(_tet[k * 4]) || IsLocked(_tet[k * 4 + 1]) || IsLocked(_tet[k * 4 + 2]) || IsLocked(_tet[k * 4 + 3]);

    private static Vector3 Clamp(Vector3 d)
    {
        var l = d.Length();
        return l > MaxStep ? d * (MaxStep / l) : d;
    }

    // ── 링 타원 음함수 ──────────────────────────────────────

    private void RingFrames()
    {
        var e = Engine;
        for (var r = 0; r < Tubes * Rings; r++)
        {
            var c = e.Center[r];
            int v0 = r * Segs, v4 = r * Segs + Segs / 4;
            var a = new Vector3(e.Px[v0], e.Py[v0], e.Pz[v0]) - c;
            var b = new Vector3(e.Px[v4], e.Py[v4], e.Pz[v4]) - c;
            float la = a.Length(), lb = b.Length();
            _rc[r] = c; _rf[r] = la; _rs[r] = lb;
            _n0[r] = la > 1e-6f ? a / la : Vector3.UnitZ;
            _n1[r] = lb > 1e-6f ? b / lb : Vector3.UnitX;
        }
    }

    /// <summary>점 x 의 튜브 링 구간 [r0, r1] 음함수 값(&lt; 1 = 내부) · 표면 투영점.</summary>
    private bool Inside(int tube, int r0, int r1, Vector3 x, out Vector3 surface, out float depth)
    {
        surface = x; depth = 0f;
        var best = -1; var bt = 0f; var bd = float.MaxValue;
        for (var i = r0; i < r1; i++)
        {
            int a = tube * Rings + i, b = a + 1;
            var ab = _rc[b] - _rc[a];
            var t = Math.Clamp(Vector3.Dot(x - _rc[a], ab) / MathF.Max(1e-10f, ab.LengthSquared()), 0f, 1f);
            var d = Vector3.DistanceSquared(x, _rc[a] + ab * t);
            if (d < bd) { bd = d; best = i; bt = t; }
        }
        if (best < 0 || (best == r0 && bt <= 0f) || (best == r1 - 1 && bt >= 1f)) return false;
        int ia = tube * Rings + best, ib = ia + 1;
        var c = Vector3.Lerp(_rc[ia], _rc[ib], bt);
        var n0 = Vector3.Normalize(Vector3.Lerp(_n0[ia], _n0[ib], bt)); var n1 = Vector3.Normalize(Vector3.Lerp(_n1[ia], _n1[ib], bt));
        float rf = _rf[ia] + (_rf[ib] - _rf[ia]) * bt, rs = _rs[ia] + (_rs[ib] - _rs[ia]) * bt;
        var q = x - c;
        float q0 = Vector3.Dot(q, n0), q1 = Vector3.Dot(q, n1);
        var f = MathF.Sqrt((q0 / rf) * (q0 / rf) + (q1 / rs) * (q1 / rs));
        if (f >= 1f) return false;
        var axial = q - q0 * n0 - q1 * n1;
        if (f < 1e-4f) { q0 = rf; q1 = 0f; f = 1f; }
        var k = 1f / f;
        var bnd = c + axial + (q0 * n0 + q1 * n1) * k;
        var dir = bnd - x;
        var dl = dir.Length();
        depth = dl;
        surface = dl > 1e-9f ? bnd + dir / dl * Eps : bnd;
        return true;
    }

    private int Test(int tube, int s0, int s1, int otherTube, int r0, int r1, bool resolve, ref float maxDepth)
    {
        var e = Engine;
        var n = 0;
        for (var i = s0; i <= s1; i++)
            for (var j = 0; j < Segs; j++)
            {
                var v = NeuralAnatomicalMuscleFasciaEngine.Vertex(tube, i, j);
                var x = new Vector3(e.Px[v], e.Py[v], e.Pz[v]);
                if (!Inside(otherTube, r0, r1, x, out var sfc, out var d)) continue;
                n++;
                if (d > maxDepth) maxDepth = d;
                if (!resolve) continue;
                e.Px[v] = sfc.X; e.Py[v] = sfc.Y; e.Pz[v] = sfc.Z;
                _locked[v] = true;
            }
        return n;
    }

    /// <summary>관절 주름 이등분 평면 클리핑: 근위 링은 평면 근위 쪽 · 원위 링은 원위 쪽(+ε)에 유지.</summary>
    private int Crease(int tube, bool resolve, ref float maxDepth)
    {
        var e = Engine;
        var ch = NeuralAnatomicalMuscleFasciaEngine.Chains[tube];
        Vector3 S = e.Joint(ch[0]), E = e.Joint(ch[1]), W = e.Joint(ch[2]);
        var m = Vector3.Normalize(S - E) - Vector3.Normalize(W - E);
        var ml = m.Length();
        if (ml < 1e-6f) return 0;
        m /= ml;
        var k = _jointRing[tube];
        var n = 0;
        for (var i = 0; i < Rings; i++)
        {
            if (i > k - 3 && i < k + 3) continue;
            var side = i < k ? 1f : -1f;   // 근위 = +m 쪽
            for (var j = 0; j < Segs; j++)
            {
                var v = NeuralAnatomicalMuscleFasciaEngine.Vertex(tube, i, j);
                var x = new Vector3(e.Px[v], e.Py[v], e.Pz[v]);
                var dd = side * Vector3.Dot(x - E, m);
                if (dd >= 0f) continue;
                n++;
                if (-dd > maxDepth) maxDepth = -dd;
                if (!resolve) continue;
                x += side * m * (-dd + Eps);
                e.Px[v] = x.X; e.Py[v] = x.Y; e.Pz[v] = x.Z;
                _locked[v] = true;
            }
        }
        return n;
    }

    private int Intersections(bool resolve, ref float maxDepth)
    {
        var n = 0;
        for (var t = 1; t < Tubes; t++)
        {
            n += Crease(t, resolve, ref maxDepth);   // 팔꿈치 · 무릎 안쪽 피부 상호 관통
            if (t <= 2) n += Test(t, 5, Rings - 1, 0, 2, Rings - 2, resolve, ref maxDepth);   // 팔 → 몸통
        }
        return n;
    }
}
