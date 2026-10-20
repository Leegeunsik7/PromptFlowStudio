using System.Numerics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.Garment;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.FluidWetness;

/// <summary>
/// [PromptFlow Studio v17.19.0] 🛡️ 젖은 모발 표면장력 &amp; 의상 유체역학적 피부 밀착 가드(무할당).
///   ▸ 모발 수분(가이드별 0..1): 비 노출(모근 법선 상향) + 두피 땀 − 건조 · 모세관 인력: 끝 쪽 절반 입자 ↔ 모근 최근접 4 이웃 동일 입자,
///     액체 다리 힘 a = k·w̄·(1 − d/R)(가이드 = 클럼프 대표 · R 120 mm) → 강제 클러스터링 · 자식 뭉침 강도 ↑ · 잔머리 ↓ · 굽힘 강성 × (1 − 0.5w)(젖은 모발 처짐).
///   ▸ 이마/목 흡착: 가이드 입자가 신체 SDF(v17.15 두피/목 캡슐) 3·w mm 이내 → 표면(+0.3 mm) 쪽 견인(스텝당 ≤ 2 mm) + 점착 마찰(직전 위치 동반 → 속도 소거).
///   ▸ 의상 점성 흡착: 포화도 S &gt; 0.2 입자가 근육 피부(v17.14) 여유 + 30·S mm 이내 → 법선 상대 속도 감쇠(직전 위치 이동 0.8·S · 스퀴즈 필름) + 여유 위치 인력(0.25·S) ·
///     입자 감쇠 = 직물 기본 + 0.08·S · 굽힘 강성 ÷ 중량비(무거운 젖은 천 드레이프) · 펄럭임 = 아우터 피부 기준 이격 변화량.
///   ▸ 안전: 피부 안쪽 견인 금지(목표 = 피부 + 여유 이상) · 견인 상한 · 관통 계측은 v17.16/v17.15 가드 유지.
/// </summary>
public sealed class WetFiberTensionGarmentAdherenceGuard
{
    public const int Nbr = 4;
    public const float BridgeRange = 0.12f, MaxPull = 0.002f;

    private readonly StrandHairPbdDynamicsEngine _hair;
    private readonly ScalpRootAnchorBodyPenetrationGuard _hairGuard;
    private readonly MultiLayerGarmentPbdDynamicsEngine _gar;
    private readonly MultiLayerGarmentContactSelfIntersectionGuard _garGuard;
    private readonly AnisotropicFabricDrapingWrinkleSynthesizer _drape;
    private readonly MultiphaseSurfaceWetnessCapillaryEngine _wet;
    private readonly float[] _hw;
    private readonly int[] _nbr;
    private readonly float[] _layerBendMass = { 1f, 1f, 1f };
    private readonly float[] _prevGap;
    private readonly bool[] _gapValid;
    private float _baseBend, _baseClump, _baseFrizz;

    public WetFiberTensionGarmentAdherenceGuard(StrandHairPbdDynamicsEngine hair, ScalpRootAnchorBodyPenetrationGuard hairGuard, MultiLayerGarmentPbdDynamicsEngine garment,
        MultiLayerGarmentContactSelfIntersectionGuard garmentGuard, AnisotropicFabricDrapingWrinkleSynthesizer draping, MultiphaseSurfaceWetnessCapillaryEngine wet)
    {
        _hair = hair; _hairGuard = hairGuard; _gar = garment; _garGuard = garmentGuard; _drape = draping; _wet = wet;
        _hw = new float[hair.G];
        _nbr = new int[hair.G * Nbr];
        _prevGap = new float[garment.P];
        _gapValid = new bool[garment.P];
        Span<float> bd = stackalloc float[Nbr];
        for (var g = 0; g < hair.G; g++)
        {
            bd.Fill(float.MaxValue);
            for (var j = 0; j < Nbr; j++) _nbr[g * Nbr + j] = g;
            for (var o = 0; o < hair.G; o++)
            {
                if (o == g) continue;
                var d = Vector3.DistanceSquared(hair.RootLocal(g), hair.RootLocal(o));
                for (var j = 0; j < Nbr; j++)
                {
                    if (d >= bd[j]) continue;
                    for (var m = Nbr - 1; m > j; m--) { bd[m] = bd[m - 1]; _nbr[g * Nbr + m] = _nbr[g * Nbr + m - 1]; }
                    bd[j] = d; _nbr[g * Nbr + j] = o;
                    break;
                }
            }
        }
        _baseBend = hair.BendStiffness; _baseClump = hair.ClumpStrength; _baseFrizz = hair.Frizz;
    }

    public bool HairCapillary { get; set; } = true;
    public bool HairAdhesion { get; set; } = true;
    public bool GarmentAdhesion { get; set; } = true;
    public float CapillaryGain { get; set; } = 40f;
    public float HairDryRate { get; set; } = 0.01f;
    /// <summary>수동 모발 습윤(음수 = 환경 연동).</summary>
    public float HairWetOverride { get; set; } = -1f;
    public float HairWetMean { get; private set; }
    public float HairWet(int g) => _hw[g];
    public int HairAdhered { get; private set; }
    public float MaxBridgeAccel { get; private set; }
    public int BridgePairs { get; private set; }
    /// <summary>모끝 이웃 평균 간격(mm · 클러스터링 지표 · 작을수록 뭉침).</summary>
    public float TipSpreadMm { get; private set; }
    public int GarmentAdhered { get; private set; }
    /// <summary>아우터 펄럭임: 피부 기준 이격 변화 틱당 평균(mm · 신체 강체 운동 제외).</summary>
    public float FlutterMm { get; private set; }
    /// <summary>아우터 평균 이격(여유 위치 초과분 · mm).</summary>
    public float OuterStandOffMm { get; private set; }
    /// <summary>점성 흡착 포착 거리(m · × 포화도).</summary>
    public float AdhesionRange { get; set; } = 0.03f;
    /// <summary>법선 상대 속도 감쇠율(× 포화도).</summary>
    public float ViscousDamping { get; set; } = 0.8f;
    public float LayerBendScale(int l) => 1f / _layerBendMass[l];
    public double LastHairUs { get; private set; }
    public double LastGarmentUs { get; private set; }
    /// <summary>직물 교체 후 굽힘 재적용 강제.</summary>
    public void InvalidateLayer(int l) => _layerBendMass[l] = 0.5f;
    public void ResetHair() => Array.Clear(_hw);

    /// <summary>모발 수분 갱신 + 모세관 인력(엔진 FX/FY/FZ 기록 · 헤어 스텝 전 호출).</summary>
    public void PreHair(float rainMmH, float sweatRate, float humidity, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var h = _hair; int G = h.G, N = StrandHairPbdDynamicsEngine.N;
        double sum = 0;
        for (var g = 0; g < G; g++)
        {
            var up = MathF.Max(0f, Vector3.Normalize(h.RootLocal(g) - StrandHairPbdDynamicsEngine.HeadCenterLocal).Y);
            var w = _hw[g] + (0.02f * rainMmH * (0.3f + 0.7f * up) + 0.4f * sweatRate) * dt - HairDryRate * (1f - humidity) * dt;
            w = HairWetOverride >= 0f ? HairWetOverride : Math.Clamp(w, 0f, 1f);
            _hw[g] = w; sum += w;
        }
        HairWetMean = (float)(sum / G);
        h.BendStiffness = _baseBend * (1f - 0.5f * HairWetMean);
        h.ClumpStrength = Math.Clamp(_baseClump + 0.5f * HairWetMean, 0f, 1f);
        h.Frizz = _baseFrizz * (1f - 0.8f * HairWetMean);
        h.UpdateChildProfiles();
        Array.Clear(h.FX); Array.Clear(h.FY); Array.Clear(h.FZ);
        var pairs = 0; var amax = 0f;
        if (HairCapillary)
            for (var g = 0; g < G; g++)
                for (var j = 0; j < Nbr; j++)
                {
                    var o = _nbr[g * Nbr + j];
                    if (o == g) continue;
                    var wb = 0.5f * (_hw[g] + _hw[o]);
                    if (wb < 0.05f) continue;
                    for (var i = N / 2; i < N; i++)
                    {
                        int ka = i * G + g, kb = i * G + o;
                        var dv = new Vector3(h.X[kb] - h.X[ka], h.Y[kb] - h.Y[ka], h.Z[kb] - h.Z[ka]);
                        var d = dv.Length();
                        if (d < 0.001f || d > BridgeRange) continue;
                        var a = CapillaryGain * wb * (1f - d / BridgeRange);
                        var f = dv / d * a;
                        h.FX[ka] += f.X; h.FY[ka] += f.Y; h.FZ[ka] += f.Z;
                        pairs++; if (a > amax) amax = a;
                    }
                }
        double spread = 0; var sc = 0;
        for (var g = 0; g < G; g++)
            for (var j = 0; j < Nbr; j++)
            {
                var o = _nbr[g * Nbr + j];
                if (o == g) continue;
                int ka = (N - 1) * G + g, kb = (N - 1) * G + o;
                spread += new Vector3(h.X[kb] - h.X[ka], h.Y[kb] - h.Y[ka], h.Z[kb] - h.Z[ka]).Length(); sc++;
            }
        TipSpreadMm = sc > 0 ? (float)(spread / sc) * 1000f : 0f;
        BridgePairs = pairs; MaxBridgeAccel = amax;
        LastHairUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>헤어 스텝 후: 젖은 가닥 이마/목 흡착(SDF 근접 견인 + 점착 마찰).</summary>
    public void PostHair()
    {
        var h = _hair; int G = h.G, N = StrandHairPbdDynamicsEngine.N;
        var n = 0;
        if (HairAdhesion)
            for (var i = 2; i < N; i++)
                for (var g = 0; g < G; g++)
                {
                    var w = _hw[g];
                    if (w < 0.1f) continue;
                    var k = i * G + g;
                    var x = new Vector3(h.X[k], h.Y[k], h.Z[k]);
                    var d = _hairGuard.Sdf(x, out var nrm, out _);
                    if (d > 0.003f * w || d < -0.001f) continue;
                    var target = x - nrm * (d - 0.0003f);
                    var mv = target - x; var ml = mv.Length();
                    if (ml > MaxPull) mv *= MaxPull / ml;
                    x += mv * (0.6f * w);
                    h.X[k] = x.X; h.Y[k] = x.Y; h.Z[k] = x.Z;
                    var s = 0.5f * w;
                    h.PX[k] += (x.X - h.PX[k]) * s; h.PY[k] += (x.Y - h.PY[k]) * s; h.PZ[k] += (x.Z - h.PZ[k]) * s;
                    n++;
                }
        HairAdhered = n;
    }

    /// <summary>의상 스텝 전: 감쇠 · 굽힘(중량비) 갱신.</summary>
    public void PreGarment()
    {
        var e = _gar;
        for (var i = 0; i < e.P; i++)
        {
            var m = _drape.LayerMaterial((GarmentLayer)e.LayerOf[i]);
            e.Keep[i] = 1f - (m.Damping + (GarmentAdhesion ? 0.08f * _wet.Saturation(i) : 0f));
        }
        for (var l = 0; l < MultiLayerGarmentPbdDynamicsEngine.Layers; l++)
        {
            var mr = GarmentAdhesion ? MathF.Max(1f, _wet.LayerMassRatio(l)) : 1f;
            if (MathF.Abs(mr - _layerBendMass[l]) / _layerBendMass[l] < 0.05f) continue;
            _layerBendMass[l] = mr;
            var m = _drape.LayerMaterial((GarmentLayer)l);
            e.SetStiffness((GarmentLayer)l, m.Warp, m.Weft, m.Shear, m.BendWarp / mr, m.BendWeft / mr);
        }
    }

    /// <summary>의상 스텝 후: 젖은 입자 점성 흡착(피부 여유 위치 견인 + 속도 감쇠) · 펄럭임 계측.</summary>
    public void PostGarment()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var e = _gar;
        var n = 0;
        double flut = 0, gapSum = 0; var dc = 0;
        for (var i = 0; i < e.P; i++)
        {
            if (e.W[i] == 0f || e.RingOf[i] < 0) continue;
            int l = e.LayerOf[i], t = e.TubeOf[i];
            var s = _wet.Saturation(i);
            var adhere = GarmentAdhesion && s >= 0.2f;
            if (l != 2 && !adhere) continue;
            var x = e.Pos(i);
            if (!_garGuard.QueryNear(t, 0, NeuralAnatomicalMuscleFasciaEngine.Rings - 1, x, out _, out var dir, out var radial, out var skinR)) continue;
            var want = skinR + e.LayerClearance[l] + _garGuard.LayerThickness[l] * 0.5f;
            var gap = radial - want;
            var dg = _gapValid[i] ? gap - _prevGap[i] : 0f;
            if (l == 2 && _gapValid[i]) { flut += MathF.Abs(dg); gapSum += MathF.Max(0f, gap); dc++; }
            _gapValid[i] = true; _prevGap[i] = gap;
            if (!adhere || gap > AdhesionRange * s) continue;
            // 점성 흡착: 법선 상대 속도 감쇠(스퀴즈 필름 · 직전 위치 이동 → 위치 제약 무충돌) + 여유 위치 인력(피부 안쪽 금지)
            var dv = dir * (dg * ViscousDamping * s);
            e.PX[i] += dv.X; e.PY[i] += dv.Y; e.PZ[i] += dv.Z;
            var mv = MathF.Min(MathF.Max(0f, gap) * 0.25f * s, MaxPull);
            x -= dir * mv;
            e.X[i] = x.X; e.Y[i] = x.Y; e.Z[i] = x.Z;
            n++;
        }
        GarmentAdhered = n;
        FlutterMm = dc > 0 ? (float)(flut / dc) * 1000f : 0f;
        OuterStandOffMm = dc > 0 ? (float)(gapSum / dc) * 1000f : 0f;
        LastGarmentUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
