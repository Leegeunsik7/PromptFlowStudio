using System.Numerics;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.Garment;

public enum GarmentLayer : byte { Base = 0, Mid, Outer }

/// <summary>직물 제약 종류: 경사(축) · 위사(둘레) · 전단(대각) · 경사 굽힘 · 위사 굽힘.</summary>
public enum FabricEdge : byte { Warp = 0, Weft, Shear, BendWarp, BendWeft }

/// <summary>의상 패널: v17.14 피부 튜브의 링 범위(+ 몸통 아래 자유 스커트 링) · 고정 링.</summary>
public readonly record struct GarmentPanel(GarmentLayer Layer, TissueTube Tube, int Ring0, int Ring1, int SkirtRings, int PinRing, string Name);

/// <summary>의상 충돌/고정 결선점(가드 구현 · 인터페이스 호출 무할당).</summary>
public interface IGarmentCollider
{
    void Pin(MultiLayerGarmentPbdDynamicsEngine e, float alpha);
    void Collide(MultiLayerGarmentPbdDynamicsEngine e, int iteration, int substep);
}

/// <summary>
/// [PromptFlow Studio v17.16.0] 👗 실시간 다층 의상 PBD 시뮬레이션 &amp; 직물 역학 엔진(<see cref="Step"/> 무할당).
///   ▸ 구조: 베이스(이너 · 몸통 + 반소매) · 미드(셔츠 몸통/긴소매 + 팬츠 허리/양다리) · 아우터(코트 몸통 + 자유 스커트 7 링 + 소매) =
///     v17.14 피부 튜브 링 × 둘레 16 격자 패널 9 개 · 레이어 간격 오프셋으로 착장(목/소매산/허리 링 피부 고정).
///   ▸ 제약: 경사/위사 신장 · 전단(양 대각) · 경사/위사 굽힘(한 칸 건너) — 엣지별 강성(직물 비등방) · 입자별 감쇠.
///   ▸ 솔버: 엣지 탐욕 색칠(정점 비공유 배치) 가우스-자이델 · 배치 = SIMD 레인(V256 → V128 → 스칼라 · <see cref="IHairLane{TV}"/> 커널 공유 · 비트 동일) ·
///     서브스텝 2 × 반복 6 · 반복마다 가드 충돌(피부 · 레이어 간 · 교차 튜브 · 다리).
/// </summary>
public sealed class MultiLayerGarmentPbdDynamicsEngine
{
    public const int Segs = NeuralAnatomicalMuscleFasciaEngine.Segs, Rings = NeuralAnatomicalMuscleFasciaEngine.Rings, MaxSkirt = 8, Layers = 3;
    public static readonly GarmentPanel[] Panels =
    {
        new(GarmentLayer.Base, TissueTube.Torso, 6, 23, 0, 23, "이너 몸통"),
        new(GarmentLayer.Base, TissueTube.ArmL, 0, 7, 0, 0, "이너 소매(좌)"),
        new(GarmentLayer.Base, TissueTube.ArmR, 0, 7, 0, 0, "이너 소매(우)"),
        new(GarmentLayer.Mid, TissueTube.Torso, 9, 23, 0, 23, "셔츠 몸통"),
        new(GarmentLayer.Mid, TissueTube.ArmL, 0, 15, 0, 0, "셔츠 소매(좌)"),
        new(GarmentLayer.Mid, TissueTube.ArmR, 0, 15, 0, 0, "셔츠 소매(우)"),
        new(GarmentLayer.Mid, TissueTube.Torso, 0, 8, 0, 8, "팬츠 허리"),
        new(GarmentLayer.Mid, TissueTube.LegL, 0, 21, 0, 0, "팬츠 다리(좌)"),
        new(GarmentLayer.Mid, TissueTube.LegR, 0, 21, 0, 0, "팬츠 다리(우)"),
        new(GarmentLayer.Outer, TissueTube.Torso, 0, 23, 7, 23, "코트 몸통 + 스커트"),
        new(GarmentLayer.Outer, TissueTube.ArmL, 0, 18, 0, 0, "코트 소매(좌)"),
        new(GarmentLayer.Outer, TissueTube.ArmR, 0, 18, 0, 0, "코트 소매(우)"),
    };

    public readonly int P;                       // 실입자 수(더미 1 개 별도 = 인덱스 P)
    public readonly float[] X, Y, Z, PX, PY, PZ, W, Keep;
    public readonly byte[] LayerOf, TubeOf, SegOf, PanelOf;
    public readonly short[] RingOf;              // 스커트 = 음수(−1 … −7)
    public readonly int[] SkinVertex;            // 고정 입자 피부 정점(−1 = 자유)
    public readonly float[] Clearance;           // 피부 대비 반경 여유(레이어 누적)
    public readonly int[] EA, EB;
    public readonly float[] ERest, EK;
    public readonly byte[] EClass;
    public readonly int[] BatchStart;            // 색 배치 경계(패딩 8 배수)
    private readonly int[] _map;                 // [layer][tube][ring + MaxSkirt][seg] → 입자
    private readonly float[] _ax, _ay, _az, _bx, _by, _bz, _wa, _wb, _rs, _k, _cax, _cay, _caz, _cbx, _cby, _cbz;
    private readonly int _maxBatch;

    public MultiLayerGarmentPbdDynamicsEngine(NeuralAnatomicalMuscleFasciaEngine skin)
    {
        Skin = skin;
        var n = 0;
        foreach (var pn in Panels) n += (pn.Ring1 - pn.Ring0 + 1 + pn.SkirtRings) * Segs;
        P = n;
        X = new float[P + 1]; Y = new float[P + 1]; Z = new float[P + 1]; PX = new float[P + 1]; PY = new float[P + 1]; PZ = new float[P + 1]; W = new float[P + 1]; Keep = new float[P + 1];
        LayerOf = new byte[P + 1]; TubeOf = new byte[P + 1]; SegOf = new byte[P + 1]; PanelOf = new byte[P + 1]; RingOf = new short[P + 1]; SkinVertex = new int[P + 1]; Clearance = new float[P + 1];
        _map = new int[Layers * NeuralAnatomicalMuscleFasciaEngine.Tubes * (Rings + MaxSkirt) * Segs];
        Array.Fill(_map, -1);
        for (var l = 0; l < Layers; l++) LayerClearance[l] = 0.004f + 0.009f * l + (l == 2 ? 0.012f : 0f);

        // 입자
        var idx = 0;
        for (var pi = 0; pi < Panels.Length; pi++)
        {
            var pn = Panels[pi];
            for (var r = pn.Ring0 - pn.SkirtRings; r <= pn.Ring1; r++)
                for (var s = 0; s < Segs; s++)
                {
                    LayerOf[idx] = (byte)pn.Layer; TubeOf[idx] = (byte)pn.Tube; SegOf[idx] = (byte)s; PanelOf[idx] = (byte)pi; RingOf[idx] = (short)r;
                    SkinVertex[idx] = r >= 0 ? NeuralAnatomicalMuscleFasciaEngine.Vertex((int)pn.Tube, r, s) : -1;
                    W[idx] = r == pn.PinRing ? 0f : 1f;
                    Keep[idx] = 0.985f;
                    _map[MapIndex((int)pn.Layer, (int)pn.Tube, r, s)] = idx;
                    idx++;
                }
        }
        W[P] = 0f; Keep[P] = 1f;
        RestPlace();

        // 엣지
        var ea = new List<int>(P * 8); var eb = new List<int>(P * 8); var ec = new List<byte>(P * 8);
        void Add(int a, int b, FabricEdge c) { if (a < 0 || b < 0 || (W[a] == 0f && W[b] == 0f)) return; ea.Add(a); eb.Add(b); ec.Add((byte)c); }
        for (var pi = 0; pi < Panels.Length; pi++)
        {
            var pn = Panels[pi];
            int lo = pn.Ring0 - pn.SkirtRings, hi = pn.Ring1;
            for (var r = lo; r <= hi; r++)
                for (var s = 0; s < Segs; s++)
                {
                    int a = Map((int)pn.Layer, (int)pn.Tube, r, s), sn = (s + 1) % Segs, s2 = (s + 2) % Segs;
                    Add(a, Map((int)pn.Layer, (int)pn.Tube, r, sn), FabricEdge.Weft);
                    Add(a, Map((int)pn.Layer, (int)pn.Tube, r, s2), FabricEdge.BendWeft);
                    if (r + 1 <= hi)
                    {
                        Add(a, Map((int)pn.Layer, (int)pn.Tube, r + 1, s), FabricEdge.Warp);
                        Add(a, Map((int)pn.Layer, (int)pn.Tube, r + 1, sn), FabricEdge.Shear);
                        Add(Map((int)pn.Layer, (int)pn.Tube, r, sn), Map((int)pn.Layer, (int)pn.Tube, r + 1, s), FabricEdge.Shear);
                    }
                    if (r + 2 <= hi) Add(a, Map((int)pn.Layer, (int)pn.Tube, r + 2, s), FabricEdge.BendWarp);
                }
        }
        // 탐욕 색칠(정점 비공유)
        var m = ea.Count;
        var color = new int[m];
        var used = new List<HashSet<int>>();
        for (var e = 0; e < m; e++)
        {
            var c = 0;
            while (c < used.Count && (used[c].Contains(ea[e]) || used[c].Contains(eb[e]))) c++;
            if (c == used.Count) used.Add(new HashSet<int>());
            used[c].Add(ea[e]); used[c].Add(eb[e]);
            color[e] = c;
        }
        var colors = used.Count;
        var counts = new int[colors];
        foreach (var c in color) counts[c]++;
        BatchStart = new int[colors + 1];
        for (var c = 0; c < colors; c++) BatchStart[c + 1] = BatchStart[c] + (counts[c] + 7) / 8 * 8;
        var total = BatchStart[colors];
        EA = new int[total]; EB = new int[total]; ERest = new float[total]; EK = new float[total]; EClass = new byte[total];
        Array.Fill(EA, P); Array.Fill(EB, P);
        var fill = new int[colors];
        for (var e = 0; e < m; e++)
        {
            var c = color[e];
            var k = BatchStart[c] + fill[c]++;
            EA[k] = ea[e]; EB[k] = eb[e]; EClass[k] = ec[e];
            ERest[k] = Vector3.Distance(Pos(ea[e]), Pos(eb[e]));
            EK[k] = 1f;
        }
        Edges = m; Colors = colors;
        _maxBatch = 0;
        for (var c = 0; c < colors; c++) _maxBatch = Math.Max(_maxBatch, BatchStart[c + 1] - BatchStart[c]);
        _ax = new float[_maxBatch]; _ay = new float[_maxBatch]; _az = new float[_maxBatch]; _bx = new float[_maxBatch]; _by = new float[_maxBatch]; _bz = new float[_maxBatch];
        _wa = new float[_maxBatch]; _wb = new float[_maxBatch]; _rs = new float[_maxBatch]; _k = new float[_maxBatch];
        _cax = new float[_maxBatch]; _cay = new float[_maxBatch]; _caz = new float[_maxBatch]; _cbx = new float[_maxBatch]; _cby = new float[_maxBatch]; _cbz = new float[_maxBatch];
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V256 : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V128 : HairSimdPath.Scalar;
    }

    public NeuralAnatomicalMuscleFasciaEngine Skin { get; }
    public HairSimdPath Path { get; set; }
    public int Edges { get; }
    public int Colors { get; }
    public int PaddedEdges => BatchStart[Colors];
    public float[] LayerClearance { get; } = new float[Layers];
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
    public int Iterations { get; set; } = 6;
    public int Substeps { get; set; } = 2;
    public float LastSubstepDt { get; private set; } = 1f / 120f;
    public double LastSolveUs { get; private set; }
    public float MaxStretchError { get; private set; }
    public float MeanStretchError { get; private set; }
    public long Steps { get; private set; }
    public Vector3 Pos(int i) => new(X[i], Y[i], Z[i]);

    public static int MapIndex(int layer, int tube, int ring, int seg) => ((layer * NeuralAnatomicalMuscleFasciaEngine.Tubes + tube) * (Rings + MaxSkirt) + ring + MaxSkirt) * Segs + seg;

    /// <summary>(레이어, 튜브, 링, 둘레) → 입자(없으면 −1).</summary>
    public int Map(int layer, int tube, int ring, int seg)
    {
        if (ring < -MaxSkirt || ring >= Rings) return -1;
        return _map[MapIndex(layer, tube, ring, ((seg % Segs) + Segs) % Segs)];
    }

    /// <summary>레스트 착장 배치(피부 레스트 + 법선 × 레이어 여유 · 스커트 = 골반 링 아래 A 라인).</summary>
    public void RestPlace()
    {
        var sk = Skin;
        for (var i = 0; i < P; i++)
        {
            var l = LayerOf[i];
            Clearance[i] = LayerClearance[l];
            Vector3 p;
            if (RingOf[i] >= 0)
            {
                var v = SkinVertex[i];
                p = sk.RestVertex(v) + Vector3.Normalize(sk.RestVertex(v) - sk.RestCenter(v / Segs)) * LayerClearance[l];
            }
            else p = SkirtRest(TubeOf[i], -RingOf[i], SegOf[i], LayerClearance[l]);
            X[i] = PX[i] = p.X; Y[i] = PY[i] = p.Y; Z[i] = PZ[i] = p.Z;
        }
        X[P] = PX[P] = Y[P] = PY[P] = Z[P] = PZ[P] = 0f;
    }

    private Vector3 SkirtRest(int tube, int k, int seg, float clearance)
    {
        var sk = Skin;
        int r0 = NeuralAnatomicalMuscleFasciaEngine.RingOf(tube, 0), r1 = NeuralAnatomicalMuscleFasciaEngine.RingOf(tube, 1);
        var c0 = sk.RestCenter(r0); var t = Vector3.Normalize(sk.RestCenter(r1) - c0);
        var spacing = Vector3.Distance(sk.RestCenter(r1), c0) * 1.1f;
        var v = sk.RestVertex(NeuralAnatomicalMuscleFasciaEngine.Vertex(tube, 0, seg));
        var rad = v - c0; rad -= t * Vector3.Dot(rad, t);
        var rl = rad.Length();
        var dir = rad / rl;
        return c0 - t * (spacing * k) + dir * (rl + clearance + 0.014f * k);
    }

    /// <summary>현재 배치 기준 레스트 길이 재계산(착장 여유 변경 후 · 제어 평면).</summary>
    public void RecomputeRest()
    {
        for (var k = 0; k < PaddedEdges; k++) if (EA[k] != P) ERest[k] = Vector3.Distance(Pos(EA[k]), Pos(EB[k]));
    }

    /// <summary>강성 일괄 설정(레이어별 · 엣지 종류별).</summary>
    public void SetStiffness(GarmentLayer layer, float warp, float weft, float shear, float bendWarp, float bendWeft)
    {
        for (var k = 0; k < PaddedEdges; k++)
        {
            if (EA[k] == P || LayerOf[EA[k]] != (byte)layer) continue;
            EK[k] = (FabricEdge)EClass[k] switch { FabricEdge.Warp => warp, FabricEdge.Weft => weft, FabricEdge.Shear => shear, FabricEdge.BendWarp => bendWarp, _ => bendWeft };
        }
    }

    public void SetDamping(GarmentLayer layer, float damping)
    {
        for (var i = 0; i < P; i++) if (LayerOf[i] == (byte)layer) Keep[i] = 1f - damping;
    }

    // ── SIMD 커널 ───────────────────────────────────────────

    private static void EdgeK<TV, L>(MultiLayerGarmentPbdDynamicsEngine e, int n) where TV : struct where L : struct, IHairLane<TV>
    {
        TV eps = L.S(1e-9f);
        for (var i = 0; i < n; i += L.Width)
        {
            TV ax = L.Load(e._ax, i), ay = L.Load(e._ay, i), az = L.Load(e._az, i);
            TV dx = L.Sub(L.Load(e._bx, i), ax), dy = L.Sub(L.Load(e._by, i), ay), dz = L.Sub(L.Load(e._bz, i), az);
            TV wa = L.Load(e._wa, i), wb = L.Load(e._wb, i);
            var d = L.Sqrt(L.Add(L.Add(L.Mul(dx, dx), L.Mul(dy, dy)), L.Mul(dz, dz)));
            var s = L.Div(L.Mul(L.Sub(d, L.Load(e._rs, i)), L.Load(e._k, i)), L.Mul(L.Max(d, eps), L.Max(L.Add(wa, wb), eps)));
            TV sx = L.Mul(dx, s), sy = L.Mul(dy, s), sz = L.Mul(dz, s);
            L.Store(e._cax, i, L.Mul(sx, wa)); L.Store(e._cay, i, L.Mul(sy, wa)); L.Store(e._caz, i, L.Mul(sz, wa));
            L.Store(e._cbx, i, L.Mul(sx, wb)); L.Store(e._cby, i, L.Mul(sy, wb)); L.Store(e._cbz, i, L.Mul(sz, wb));
        }
    }

    private static void IntegrateK<TV, L>(float[] x, float[] p, float[] keep, float[] w, int n, float g, float h2) where TV : struct where L : struct, IHairLane<TV>
    {
        TV gg = L.S(g * h2);
        for (var i = 0; i < n; i += L.Width)
        {
            var xi = L.Load(x, i);
            var v = L.Mul(L.Sub(xi, L.Load(p, i)), L.Load(keep, i));
            L.Store(p, i, xi);
            L.Store(x, i, L.Add(L.Add(xi, v), L.Mul(gg, L.Load(w, i))));   // 고정 입자(역질량 0) = 중력 0
        }
    }

    private HairSimdPath Eff => Path switch
    {
        HairSimdPath.V256 when Vector256.IsHardwareAccelerated => HairSimdPath.V256,
        HairSimdPath.V256 or HairSimdPath.V128 when Vector128.IsHardwareAccelerated => HairSimdPath.V128,
        _ => HairSimdPath.Scalar,
    };

    public HairSimdPath EffectivePath => Eff;

    private void Integrate(float h)
    {
        var n = P / 8 * 8;
        var h2 = h * h;
        switch (Eff)
        {
            case HairSimdPath.V256: IntegrateK<Vector256<float>, V256Lane>(X, PX, Keep, W, n, Gravity.X, h2); IntegrateK<Vector256<float>, V256Lane>(Y, PY, Keep, W, n, Gravity.Y, h2); IntegrateK<Vector256<float>, V256Lane>(Z, PZ, Keep, W, n, Gravity.Z, h2); break;
            case HairSimdPath.V128: IntegrateK<Vector128<float>, V128Lane>(X, PX, Keep, W, n, Gravity.X, h2); IntegrateK<Vector128<float>, V128Lane>(Y, PY, Keep, W, n, Gravity.Y, h2); IntegrateK<Vector128<float>, V128Lane>(Z, PZ, Keep, W, n, Gravity.Z, h2); break;
            default: IntegrateK<float, ScalarLane>(X, PX, Keep, W, n, Gravity.X, h2); IntegrateK<float, ScalarLane>(Y, PY, Keep, W, n, Gravity.Y, h2); IntegrateK<float, ScalarLane>(Z, PZ, Keep, W, n, Gravity.Z, h2); break;
        }
        var gx = Gravity.X * h2; var gy = Gravity.Y * h2; var gz = Gravity.Z * h2;
        for (var i = n; i < P; i++)
        {
            float vx = (X[i] - PX[i]) * Keep[i], vy = (Y[i] - PY[i]) * Keep[i], vz = (Z[i] - PZ[i]) * Keep[i];
            PX[i] = X[i]; PY[i] = Y[i]; PZ[i] = Z[i];
            X[i] = X[i] + vx + gx * W[i]; Y[i] = Y[i] + vy + gy * W[i]; Z[i] = Z[i] + vz + gz * W[i];
        }
        X[P] = Y[P] = Z[P] = 0f;
    }

    /// <summary>색 배치 순차 GS: 수집(스칼라) → SIMD 보정 → 분배(정점 비공유 · 스칼라).</summary>
    private void Constraints()
    {
        var path = Eff;
        for (var c = 0; c < Colors; c++)
        {
            int b0 = BatchStart[c], n = BatchStart[c + 1] - b0;
            for (var i = 0; i < n; i++)
            {
                int a = EA[b0 + i], b = EB[b0 + i];
                _ax[i] = X[a]; _ay[i] = Y[a]; _az[i] = Z[a]; _bx[i] = X[b]; _by[i] = Y[b]; _bz[i] = Z[b];
                _wa[i] = W[a]; _wb[i] = W[b]; _rs[i] = ERest[b0 + i]; _k[i] = EK[b0 + i];
            }
            switch (path)
            {
                case HairSimdPath.V256: EdgeK<Vector256<float>, V256Lane>(this, n); break;
                case HairSimdPath.V128: EdgeK<Vector128<float>, V128Lane>(this, n); break;
                default: EdgeK<float, ScalarLane>(this, n); break;
            }
            for (var i = 0; i < n; i++)
            {
                int a = EA[b0 + i], b = EB[b0 + i];
                X[a] += _cax[i]; Y[a] += _cay[i]; Z[a] += _caz[i];
                X[b] -= _cbx[i]; Y[b] -= _cby[i]; Z[b] -= _cbz[i];
            }
        }
        X[P] = Y[P] = Z[P] = 0f;
    }

    /// <summary>충돌 없는 1 서브스텝(경로 비교 테스트용).</summary>
    public void SolveOnce(float h)
    {
        Integrate(h);
        for (var it = 0; it < Iterations; it++) Constraints();
    }

    /// <summary>서브스텝(고정 보간 → 적분 → 색 배치 GS + 충돌) → 신장 지표(무할당).</summary>
    public void Step(float dt, IGarmentCollider? collider)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ns = Math.Max(1, Substeps);
        var h = dt / ns;
        LastSubstepDt = h;
        for (var s = 0; s < ns; s++)
        {
            collider?.Pin(this, (s + 1f) / ns);
            Integrate(h);
            for (var it = 0; it < Iterations; it++)
            {
                Constraints();
                collider?.Collide(this, it, s);
            }
        }
        float mx = 0; double sum = 0; var cnt = 0;
        for (var k = 0; k < PaddedEdges; k++)
        {
            if (EA[k] == P || EClass[k] > (byte)FabricEdge.Weft) continue;
            var r = MathF.Abs(Vector3.Distance(Pos(EA[k]), Pos(EB[k])) / ERest[k] - 1f);
            if (r > mx) mx = r;
            sum += r; cnt++;
        }
        MaxStretchError = mx; MeanStretchError = cnt > 0 ? (float)(sum / cnt) : 0f;
        Steps++;
        LastSolveUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void Snapshot(float[] dst)
    {
        var n = P + 1;
        X.CopyTo(dst, 0); Y.CopyTo(dst, n); Z.CopyTo(dst, 2 * n); PX.CopyTo(dst, 3 * n); PY.CopyTo(dst, 4 * n); PZ.CopyTo(dst, 5 * n);
    }

    public void Restore(float[] src)
    {
        var n = P + 1;
        Array.Copy(src, 0, X, 0, n); Array.Copy(src, n, Y, 0, n); Array.Copy(src, 2 * n, Z, 0, n);
        Array.Copy(src, 3 * n, PX, 0, n); Array.Copy(src, 4 * n, PY, 0, n); Array.Copy(src, 5 * n, PZ, 0, n);
    }
}
