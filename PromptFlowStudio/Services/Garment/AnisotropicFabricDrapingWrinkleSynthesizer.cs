using System.Numerics;

namespace PromptFlowStudio.Services.Garment;

public enum FabricPreset : byte { Silk = 0, Cotton, Denim, Leather }

/// <summary>직물 물성: 경사/위사 신장 · 전단 · 경사/위사 굽힘(PBD 강성 0..1) · 감쇠 · 마찰 · 두께 · 주름 진폭/파장 · 주름 기억(크리스).</summary>
public readonly record struct FabricMaterial(string Name, float Warp, float Weft, float Shear, float BendWarp, float BendWeft, float Damping, float Friction,
    float Thickness, float WrinkleAmp, float WrinkleLambda, float CreaseMemory, Vector3 Color);

/// <summary>
/// [PromptFlow Studio v17.16.0] 🧵 비등방성 직물 드레이핑 &amp; 절차적 미세 주름 합성기(<see cref="Update"/> 무할당).
///   ▸ 재질 4종(실크 · 면 · 데님 · 가죽): 경사 ≠ 위사 강성(데님 경사 우세) · 굽힘 강성(실크 0.004 → 가죽 0.2) · 감쇠 · 마찰 · 두께 → 엔진 엣지 강성/감쇠 · 가드 두께/마찰/레이어 여유 반영.
///   ▸ 변형률: 입자별 경사/위사 인접 엣지 평균 εw/εf(+ 인장 · − 압축) · 장력 = max(εw, εf, 0) · 압축 = max(−εw, −εf, 0).
///   ▸ 링버퍼(16 프레임 · 입자별) 압축 이력 → 주름 기억 = 기억 계수 × 이력 평균 · 주름 강도 = max(현재 압축, 기억) ·
///     미세 주름 높이 h = A·min(1, 6·강도)·½(1 + cos(2π·u/λ + φ)) (u = 압축 방향 레스트 호 좌표) · 미세 법선 n′ = normalize(n − ∂h/∂u·t_u).
///   ▸ 기하 폴드 계수: 격자 라플라시안(4 이웃 평균 대비 법선 변위) &gt; 3 mm 인 입자.
/// </summary>
public sealed class AnisotropicFabricDrapingWrinkleSynthesizer
{
    public const int Hist = 16;
    public static readonly FabricMaterial[] Presets =
    {
        new("실크", 0.92f, 0.86f, 0.25f, 0.004f, 0.004f, 0.008f, 0.15f, 0.0006f, 0.0006f, 0.006f, 0.1f, new Vector3(0.82f, 0.36f, 0.52f)),
        new("면", 0.96f, 0.9f, 0.5f, 0.02f, 0.015f, 0.015f, 0.35f, 0.001f, 0.001f, 0.01f, 0.35f, new Vector3(0.88f, 0.86f, 0.8f)),
        new("데님", 1f, 0.8f, 0.7f, 0.08f, 0.045f, 0.025f, 0.5f, 0.0018f, 0.0016f, 0.016f, 0.6f, new Vector3(0.2f, 0.32f, 0.55f)),
        new("가죽", 1f, 1f, 0.9f, 0.22f, 0.2f, 0.04f, 0.6f, 0.0025f, 0.002f, 0.024f, 0.85f, new Vector3(0.35f, 0.22f, 0.14f)),
    };

    private readonly MultiLayerGarmentPbdDynamicsEngine _e;
    private readonly MultiLayerGarmentContactSelfIntersectionGuard _g;
    private readonly float[] _sw, _sf, _cw, _cf;
    private readonly byte[] _nw, _nf;
    private readonly float[] _hist;
    private readonly FabricPreset[] _layer = { FabricPreset.Cotton, FabricPreset.Cotton, FabricPreset.Denim };
    private int _head, _count;

    public AnisotropicFabricDrapingWrinkleSynthesizer(MultiLayerGarmentPbdDynamicsEngine engine, MultiLayerGarmentContactSelfIntersectionGuard guard)
    {
        _e = engine; _g = guard;
        var p = engine.P;
        _sw = new float[p]; _sf = new float[p]; _cw = new float[p]; _cf = new float[p]; _nw = new byte[p]; _nf = new byte[p];
        Tension = new float[p]; Compression = new float[p]; Wrinkle = new float[p]; Memory = new float[p];
        NormalX = new float[p]; NormalY = new float[p]; NormalZ = new float[p]; MicroX = new float[p]; MicroY = new float[p]; MicroZ = new float[p];
        _hist = new float[p * Hist];
        for (var l = 0; l < MultiLayerGarmentPbdDynamicsEngine.Layers; l++) Apply((GarmentLayer)l);
        Update();
    }

    public readonly float[] Tension, Compression, Wrinkle, Memory, NormalX, NormalY, NormalZ, MicroX, MicroY, MicroZ;
    public float WrinkleGain { get; set; } = 1f;
    public FabricPreset LayerPreset(GarmentLayer l) => _layer[(int)l];
    public FabricMaterial LayerMaterial(GarmentLayer l) => Presets[(int)_layer[(int)l]];
    public float[] LayerMaxTension { get; } = new float[MultiLayerGarmentPbdDynamicsEngine.Layers];
    public float[] LayerMeanTension { get; } = new float[MultiLayerGarmentPbdDynamicsEngine.Layers];
    public int[] LayerFolds { get; } = new int[MultiLayerGarmentPbdDynamicsEngine.Layers];
    public int[] LayerWrinkled { get; } = new int[MultiLayerGarmentPbdDynamicsEngine.Layers];
    public int Folds { get; private set; }
    public double LastUpdateUs { get; private set; }

    /// <summary>레이어 직물 교체 → 엔진 강성/감쇠 · 가드 두께/마찰 · 레이어 여유 재계산.</summary>
    public void SetLayer(GarmentLayer layer, FabricPreset preset)
    {
        _layer[(int)layer] = preset;
        Apply(layer);
    }

    private void Apply(GarmentLayer layer)
    {
        var m = Presets[(int)_layer[(int)layer]];
        _e.SetStiffness(layer, m.Warp, m.Weft, m.Shear, m.BendWarp, m.BendWeft);
        _e.SetDamping(layer, m.Damping);
        _g.LayerThickness[(int)layer] = m.Thickness;
        _g.LayerFriction[(int)layer] = m.Friction;
        // 레이어 여유: 피부 3 mm + 안쪽 레이어 두께/간격 누적(아우터 +12 mm 여유)
        var c = 0.003f;
        for (var l = 0; l < MultiLayerGarmentPbdDynamicsEngine.Layers; l++)
        {
            var t = _g.LayerThickness[l];
            c += t * 0.5f;
            _e.LayerClearance[l] = c + (l == 2 ? 0.012f : 0f);
            c += t * 0.5f + MultiLayerGarmentContactSelfIntersectionGuard.LayerGap + 0.004f;
        }
    }

    /// <summary>변형률 · 장력 · 주름 기억 링버퍼 · 미세 법선 · 폴드(무할당).</summary>
    public void Update()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var e = _e;
        int P = e.P;
        Array.Clear(_sw); Array.Clear(_sf); Array.Clear(_nw); Array.Clear(_nf);
        for (var k = 0; k < e.PaddedEdges; k++)
        {
            var c = (FabricEdge)e.EClass[k];
            if (e.EA[k] == P || c > FabricEdge.Weft) continue;
            int a = e.EA[k], b = e.EB[k];
            var st = Vector3.Distance(e.Pos(a), e.Pos(b)) / e.ERest[k] - 1f;
            if (c == FabricEdge.Warp) { _sw[a] += st; _sw[b] += st; _nw[a]++; _nw[b]++; }
            else { _sf[a] += st; _sf[b] += st; _nf[a]++; _nf[b]++; }
        }
        Array.Clear(LayerMaxTension); Array.Clear(LayerMeanTension); Array.Clear(LayerFolds); Array.Clear(LayerWrinkled);
        Span<int> cnt = stackalloc int[MultiLayerGarmentPbdDynamicsEngine.Layers];
        cnt.Clear();
        var folds = 0;
        for (var i = 0; i < P; i++)
        {
            float w = _nw[i] > 0 ? _sw[i] / _nw[i] : 0f, f = _nf[i] > 0 ? _sf[i] / _nf[i] : 0f;
            var l = e.LayerOf[i];
            Tension[i] = MathF.Max(0f, MathF.Max(w, f));
            _cw[i] = MathF.Max(0f, -w); _cf[i] = MathF.Max(0f, -f);
            var comp = MathF.Max(_cw[i], _cf[i]);
            Compression[i] = comp;
            _hist[i * Hist + _head] = comp;
            var m = LayerMaterial((GarmentLayer)l);
            double hs = 0;
            var n = Math.Min(Hist, _count + 1);
            for (var h = 0; h < n; h++) hs += _hist[i * Hist + h];
            Memory[i] = m.CreaseMemory * (float)(hs / n);
            var inten = MathF.Max(comp, Memory[i]);
            // 법선 · 접선(격자 이웃)
            int ring = e.RingOf[i], seg = e.SegOf[i], tube = e.TubeOf[i];
            int up = e.Map(l, tube, ring + 1, seg), dn = e.Map(l, tube, ring - 1, seg), lf = e.Map(l, tube, ring, seg - 1), rt = e.Map(l, tube, ring, seg + 1);
            var pi = e.Pos(i);
            var tw = (up >= 0 ? e.Pos(up) : pi) - (dn >= 0 ? e.Pos(dn) : pi);
            var tf = (rt >= 0 ? e.Pos(rt) : pi) - (lf >= 0 ? e.Pos(lf) : pi);
            var nrm = Vector3.Cross(tf, tw);
            var nl = nrm.Length();
            nrm = nl > 1e-10f ? nrm / nl : Vector3.UnitZ;
            NormalX[i] = nrm.X; NormalY[i] = nrm.Y; NormalZ[i] = nrm.Z;
            // 미세 주름: 압축 우세 방향의 레스트 호 좌표
            var alongWarp = _cw[i] >= _cf[i];
            var u = alongWarp ? (ring + 8) * 0.03f : seg * MathF.Tau * 0.1f / 16f;
            var tu = alongWarp ? tw : tf;
            var tl = tu.Length();
            tu = tl > 1e-9f ? tu / tl : Vector3.UnitY;
            var amp = m.WrinkleAmp * WrinkleGain * MathF.Min(1f, inten * 6f);
            var ph = MathF.Tau * u / m.WrinkleLambda + 0.7f * MathF.Sin(1.7f * (alongWarp ? seg : ring));
            Wrinkle[i] = amp * 0.5f * (1f + MathF.Cos(ph));
            var dh = -amp * 0.5f * MathF.Sin(ph) * MathF.Tau / m.WrinkleLambda;
            var mic = Vector3.Normalize(nrm - tu * dh);
            MicroX[i] = mic.X; MicroY[i] = mic.Y; MicroZ[i] = mic.Z;
            if (amp > m.WrinkleAmp * 0.25f) LayerWrinkled[l]++;
            // 폴드: 라플라시안 법선 성분
            if (up >= 0 && dn >= 0 && lf >= 0 && rt >= 0)
            {
                var lap = 0.25f * (e.Pos(up) + e.Pos(dn) + e.Pos(lf) + e.Pos(rt)) - pi;
                if (MathF.Abs(Vector3.Dot(lap, nrm)) > 0.003f) { LayerFolds[l]++; folds++; }
            }
            if (Tension[i] > LayerMaxTension[l]) LayerMaxTension[l] = Tension[i];
            LayerMeanTension[l] += Tension[i]; cnt[l]++;
        }
        for (var l = 0; l < MultiLayerGarmentPbdDynamicsEngine.Layers; l++) if (cnt[l] > 0) LayerMeanTension[l] /= cnt[l];
        Folds = folds;
        _head = (_head + 1) % Hist;
        if (_count < Hist) _count++;
        LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void ResetMemory() { Array.Clear(_hist); _count = 0; _head = 0; }
}
