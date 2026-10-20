using System.Numerics;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.Garment;
using PromptFlowStudio.Services.SkinOptics;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.FluidWetness;

/// <summary>
/// [PromptFlow Studio v17.19.0] 💧 실시간 표면 습윤 확산 &amp; 모세관 흡수 역학 엔진(<see cref="Update"/> 무할당).
///   ▸ 피부(v17.17 얼굴 UV 128² · 1 µm = 1 g/m²): 수막 두께 확산(보존형 5점 플럭스 · 마스크 경계 무유량 전도도) + 땀(분비샘 밀도 × 땀률) + 비/눈(노출 = 법선 · 바람 기울기) − 증발(습도 · 기온 · 풍속) ·
///     텍셀 = SIMD 레인(v17.15 레인 커널 · 비트 동일) · 피막 → 액적 핵생성/수확은 <see cref="MicroDropletSlidingAdhesionSynthesizer"/> 결선.
///   ▸ 의상(v17.16 3 레이어 입자 격자): 수분 g/m² · 포화도 S = W / (건조 밀도 × 흡수 용량)(면 3.0 = +300% · 데님 2.0 · 실크 1.5 · 가죽 0.3) ·
///     워시번 흡수(입사 × √(1 − S) · 잔여 유출) · 불포화 모관 확산(전도도 D₀ + D·S̄² · 대칭 → 레이어 내 질량 보존) · 레이어 간 접촉 전달 · 최외곽 증발 ·
///     이웃 4 수집 → 입자 = SIMD 레인 계산.
///   ▸ 광학: 젖음 암전 ρ_w = ρ(1 − rᵢ)/(1 − ρ·rᵢ)(내부 반사 rᵢ ≈ 0.6 · 레크너–도프 근사) · 거칠기 감소(수막 경면화) · 중량비 = (건조 + 수분)/건조.
/// </summary>
public sealed class MultiphaseSurfaceWetnessCapillaryEngine
{
    public const int W = SeparableNeuralSkinSssEngine.W, H = SeparableNeuralSkinSssEngine.H, Wp = W + 2, Hp = H + 2, N = W * H;
    public const float TexelMmX = 180f / W, TexelMmY = 230f / H, InternalReflect = 0.6f;
    public static readonly float[] DryDensity = { 60f, 150f, 400f, 1000f };   // 실크 · 면 · 데님 · 가죽 g/m²
    public static readonly float[] Capacity = { 1.5f, 3.0f, 2.0f, 0.3f };

    private readonly SeparableNeuralSkinSssEngine _skin;
    private readonly MultiLayerGarmentPbdDynamicsEngine _gar;
    private readonly AnisotropicFabricDrapingWrinkleSynthesizer _drape;
    // 피부(패딩 Hp × Wp)
    private readonly float[] _m = new float[Hp * Wp], _mn = new float[Hp * Wp], _cx = new float[Hp * Wp], _cy = new float[Hp * Wp], _src = new float[Hp * Wp];
    private readonly float[] _gland = new float[N], _expo = new float[N];
    // 의상(입자 · 패딩 8)
    private readonly int _pp;
    private readonly float[] _w, _wn, _s, _cap, _dry, _n0, _n1, _n2, _n3, _gsrc;
    private readonly int[] _nb, _inner, _outer;
    private readonly float[] _layerS = new float[MultiLayerGarmentPbdDynamicsEngine.Layers], _layerMass = new float[MultiLayerGarmentPbdDynamicsEngine.Layers], _layerMaxS = new float[MultiLayerGarmentPbdDynamicsEngine.Layers];

    public MultiphaseSurfaceWetnessCapillaryEngine(SeparableNeuralSkinSssEngine skin, MultiLayerGarmentPbdDynamicsEngine garment, AnisotropicFabricDrapingWrinkleSynthesizer draping)
    {
        _skin = skin; _gar = garment; _drape = draping;
        // 피부 전도도 · 분비샘 밀도(이마 · 코 · 윗입술 높음)
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = SeparableNeuralSkinSssEngine.Index(x, y);
                var p = (y + 1) * Wp + x + 1;
                var m = skin.Mask[i];
                _cx[p] = x + 1 < W ? m * skin.Mask[i + 1] : 0f;
                _cy[p] = y + 1 < H ? m * skin.Mask[i + W] : 0f;
                float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
                var g = 0.6f + 1.4f * MathF.Exp(-((u - 0.5f) * (u - 0.5f) / 0.03f + (v - 0.18f) * (v - 0.18f) / 0.006f)) + 0.6f * MathF.Exp(-((u - 0.5f) * (u - 0.5f) / 0.004f + (v - 0.6f) * (v - 0.6f) / 0.002f));
                _gland[i] = m * (skin.RegionOf[i] == SkinRegion.Finger ? 2f : skin.RegionOf[i] == SkinRegion.Ear ? 0.3f : g);
            }
        // 의상 입자
        var P = garment.P;
        _pp = (P + 7) / 8 * 8;
        _w = new float[_pp]; _wn = new float[_pp]; _s = new float[_pp]; _cap = new float[_pp]; _dry = new float[_pp];
        _n0 = new float[_pp]; _n1 = new float[_pp]; _n2 = new float[_pp]; _n3 = new float[_pp]; _gsrc = new float[_pp];
        _nb = new int[P * 4]; _inner = new int[P]; _outer = new int[P];
        for (var i = 0; i < P; i++)
        {
            int l = garment.LayerOf[i], t = garment.TubeOf[i], r = garment.RingOf[i], sg = garment.SegOf[i];
            _nb[i * 4] = garment.Map(l, t, r - 1, sg); _nb[i * 4 + 1] = garment.Map(l, t, r + 1, sg);
            _nb[i * 4 + 2] = garment.Map(l, t, r, sg - 1); _nb[i * 4 + 3] = garment.Map(l, t, r, sg + 1);
            _inner[i] = -1; _outer[i] = -1;
            for (var li = l - 1; li >= 0 && _inner[i] < 0; li--) _inner[i] = garment.Map(li, t, r, sg);
            for (var lo = l + 1; lo < MultiLayerGarmentPbdDynamicsEngine.Layers && _outer[i] < 0; lo++) _outer[i] = garment.Map(lo, t, r, sg);
        }
        Path = skin.Path;
        RefreshFabrics();
    }

    public HairSimdPath Path { get; set; }
    /// <summary>피막 확산 계수(mm²/s).</summary>
    public float FilmDiffusion { get; set; } = 1.2f;
    public float FabricWicking { get; set; } = 0.9f;
    public float LayerTransfer { get; set; } = 0.25f;
    public float EvaporationBase { get; set; } = 0.12f;
    public float FabricEvaporation { get; set; } = 0.35f;
    /// <summary>땀률(µm/s · 분비샘 밀도 1 기준).</summary>
    public float SweatRate { get; private set; }
    public float RainMmH { get; private set; }
    public float Humidity { get; private set; } = 0.5f;
    public float Temperature { get; private set; } = 20f;
    public Vector3 Wind { get; private set; }
    public float BodySweatGain { get; set; } = 1f;
    public double LastSkinUs { get; private set; }
    public double LastGarmentUs { get; private set; }
    public float SkinMeanUm { get; private set; }
    public float SkinMaxUm { get; private set; }
    public float SkinWetFraction { get; private set; }
    public float RunoffGrams { get; private set; }
    public float EvaporatedUm { get; private set; }
    public int GarmentCount => _gar.P;
    public float Film(int i) => _m[(i / W + 1) * Wp + i % W + 1];
    public float Saturation(int p) => _s[p];
    public float Water(int p) => _w[p];
    public float CapacityOf(int p) => _cap[p];
    public float LayerSaturation(int l) => _layerS[l];
    public float LayerMassRatio(int l) => _layerMass[l];
    public float LayerMaxSaturation(int l) => _layerMaxS[l];
    public float Gland(int i) => _gland[i];
    public float Exposure(int i) => _expo[i];

    /// <summary>레이어 직물 → 건조 밀도 · 흡수 용량(제어 평면 · 직물 교체 시).</summary>
    public void RefreshFabrics()
    {
        for (var i = 0; i < _gar.P; i++)
        {
            var f = (int)_drape.LayerPreset((GarmentLayer)_gar.LayerOf[i]);
            _dry[i] = DryDensity[f];
            _cap[i] = DryDensity[f] * Capacity[f];
            _w[i] = MathF.Min(_w[i], _cap[i]);
        }
    }

    public void SetEnvironment(float rainMmH, float humidity, float temperature, Vector3 wind, float sweatRate)
    {
        RainMmH = MathF.Max(0f, rainMmH); Humidity = Math.Clamp(humidity, 0f, 1f); Temperature = temperature; Wind = wind; SweatRate = MathF.Max(0f, sweatRate);
    }

    // ── 피막 결선(액적) ─────────────────────────────────────

    public void AddFilm(int x, int y, float um)
    {
        if ((uint)x >= W || (uint)y >= H) return;
        var i = SeparableNeuralSkinSssEngine.Index(x, y);
        if (_skin.Mask[i] <= 0f) return;
        _m[(y + 1) * Wp + x + 1] += um;
    }

    /// <summary>반경 r 텍셀 내 피막 수확(최대 fraction · 반환 = 수확 µm·텍셀 합).</summary>
    public float Harvest(int cx, int cy, int r, float fraction)
    {
        var got = 0f;
        for (var y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
            for (var x = Math.Max(0, cx - r); x <= Math.Min(W - 1, cx + r); x++)
            {
                var p = (y + 1) * Wp + x + 1;
                var take = _m[p] * fraction;
                _m[p] -= take; got += take;
            }
        return got;
    }

    // ── SIMD 커널 ───────────────────────────────────────────

    private static void FilmK<TV, L>(float[] m, float[] mn, float[] cx, float[] cy, float[] src, float k) where TV : struct where L : struct, IHairLane<TV>
    {
        TV kk = L.S(k), zero = L.S(0f);
        for (var y = 1; y <= H; y++)
            for (var x = 1; x <= W; x += L.Width)
            {
                var i = y * Wp + x;
                var c = L.Load(m, i);
                var fx = L.Sub(L.Mul(L.Load(cx, i), L.Sub(L.Load(m, i + 1), c)), L.Mul(L.Load(cx, i - 1), L.Sub(c, L.Load(m, i - 1))));
                var fy = L.Sub(L.Mul(L.Load(cy, i), L.Sub(L.Load(m, i + Wp), c)), L.Mul(L.Load(cy, i - Wp), L.Sub(c, L.Load(m, i - Wp))));
                L.Store(mn, i, L.Max(zero, L.Add(L.Add(c, L.Mul(kk, L.Add(fx, fy))), L.Load(src, i))));
            }
    }

    private static void WickK<TV, L>(float[] w, float[] s, float[] n0, float[] n1, float[] n2, float[] n3, float[] cap, float[] src, float[] wn, int count, float d0, float dw, float k) where TV : struct where L : struct, IHairLane<TV>
    {
        TV D0 = L.S(d0), DW = L.S(dw), kk = L.S(k), half = L.S(0.5f), zero = L.S(0f);
        for (var i = 0; i < count; i += L.Width)
        {
            var si = L.Load(s, i);
            var acc = L.S(0f);
            TV a, m;
            a = L.Load(n0, i); m = L.Mul(L.Add(si, a), half); acc = L.Add(acc, L.Mul(L.Add(D0, L.Mul(DW, L.Mul(m, m))), L.Sub(a, si)));
            a = L.Load(n1, i); m = L.Mul(L.Add(si, a), half); acc = L.Add(acc, L.Mul(L.Add(D0, L.Mul(DW, L.Mul(m, m))), L.Sub(a, si)));
            a = L.Load(n2, i); m = L.Mul(L.Add(si, a), half); acc = L.Add(acc, L.Mul(L.Add(D0, L.Mul(DW, L.Mul(m, m))), L.Sub(a, si)));
            a = L.Load(n3, i); m = L.Mul(L.Add(si, a), half); acc = L.Add(acc, L.Mul(L.Add(D0, L.Mul(DW, L.Mul(m, m))), L.Sub(a, si)));
            var nw = L.Add(L.Add(L.Load(w, i), L.Mul(kk, L.Mul(acc, L.Load(cap, i)))), L.Load(src, i));
            L.Store(wn, i, L.Max(zero, nw));
        }
    }

    private HairSimdPath Eff => Path switch
    {
        HairSimdPath.V256 when Vector256.IsHardwareAccelerated => HairSimdPath.V256,
        HairSimdPath.V256 or HairSimdPath.V128 when Vector128.IsHardwareAccelerated => HairSimdPath.V128,
        _ => HairSimdPath.Scalar,
    };

    /// <summary>피막 확산 1 스텝(원천 = _src · 테스트/경로 비교용 공개).</summary>
    public void FilmStep(float dt)
    {
        var k = FilmDiffusion * dt / (TexelMmX * TexelMmY);
        switch (Eff)
        {
            case HairSimdPath.V256: FilmK<Vector256<float>, V256Lane>(_m, _mn, _cx, _cy, _src, k); break;
            case HairSimdPath.V128: FilmK<Vector128<float>, V128Lane>(_m, _mn, _cx, _cy, _src, k); break;
            default: FilmK<float, ScalarLane>(_m, _mn, _cx, _cy, _src, k); break;
        }
        for (var y = 1; y <= H; y++) Array.Copy(_mn, y * Wp + 1, _m, y * Wp + 1, W);
    }

    /// <summary>의상 모관 확산/원천 1 스텝(_gsrc 사용).</summary>
    public void WickStep(float dt)
    {
        var P = _gar.P;
        for (var i = 0; i < P; i++)
        {
            _s[i] = _cap[i] > 0f ? _w[i] / _cap[i] : 0f;
        }
        for (var i = 0; i < P; i++)
        {
            int a = _nb[i * 4], b = _nb[i * 4 + 1], c = _nb[i * 4 + 2], d = _nb[i * 4 + 3];
            _n0[i] = a >= 0 ? _s[a] : _s[i]; _n1[i] = b >= 0 ? _s[b] : _s[i]; _n2[i] = c >= 0 ? _s[c] : _s[i]; _n3[i] = d >= 0 ? _s[d] : _s[i];
        }
        var k = FabricWicking * dt;
        switch (Eff)
        {
            case HairSimdPath.V256: WickK<Vector256<float>, V256Lane>(_w, _s, _n0, _n1, _n2, _n3, _cap, _gsrc, _wn, _pp, 0.02f, 1f, k); break;
            case HairSimdPath.V128: WickK<Vector128<float>, V128Lane>(_w, _s, _n0, _n1, _n2, _n3, _cap, _gsrc, _wn, _pp, 0.02f, 1f, k); break;
            default: WickK<float, ScalarLane>(_w, _s, _n0, _n1, _n2, _n3, _cap, _gsrc, _wn, _pp, 0.02f, 1f, k); break;
        }
        for (var i = 0; i < P; i++) _w[i] = MathF.Min(_wn[i], _cap[i]);
    }

    public float SkinTotal()
    {
        double s = 0;
        for (var y = 1; y <= H; y++) for (var x = 1; x <= W; x++) s += _m[y * Wp + x];
        return (float)s;
    }

    public float GarmentTotal(int layer)
    {
        double s = 0;
        for (var i = 0; i < _gar.P; i++) if (layer < 0 || _gar.LayerOf[i] == layer) s += _w[i];
        return (float)s;
    }

    public void ClearSources() { Array.Clear(_src); Array.Clear(_gsrc); }
    public void SetFilm(int x, int y, float um) => _m[(y + 1) * Wp + x + 1] = um;
    public void SetWater(int p, float g) => _w[p] = MathF.Min(g, _cap[p]);
    public void ResetAll() { Array.Clear(_m); Array.Clear(_w); RunoffGrams = 0f; EvaporatedUm = 0f; }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>원천/증발 → 피막 확산 → 의상 흡수/모관/레이어 전달 → 지표(무할당).</summary>
    public void Update(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var rainUm = RainMmH * 1000f / 3600f * (Temperature < 0f ? 0.6f : 1f);   // 1 mm = 1000 µm(눈 = 녹는 비율)
        var rainDir = Vector3.Normalize(new Vector3(Wind.X * 0.12f, -1f, Wind.Z * 0.12f));
        var windF = 1f + 0.3f * Wind.Length();
        var evapRate = EvaporationBase * (1f - Humidity) * windF * MathF.Max(0.2f, 1f + 0.03f * (Temperature - 20f));
        // 피부 원천
        var sk = _skin;
        double evapTot = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = SeparableNeuralSkinSssEngine.Index(x, y);
                var p = (y + 1) * Wp + x + 1;
                if (sk.Mask[i] <= 0f) { _src[p] = 0f; continue; }
                var n = new Vector3(sk.NX[i], sk.NY[i], sk.NZ[i]);
                var e = MathF.Max(0f, Vector3.Dot(n, -rainDir));
                _expo[i] = e;
                var film = _m[p];
                var ev = evapRate * Math.Clamp(film / 2f, 0f, 1f);
                ev = MathF.Min(ev * dt, film);
                evapTot += ev;
                _src[p] = (SweatRate * _gland[i] + rainUm * e) * dt - ev;
            }
        EvaporatedUm += (float)evapTot;
        FilmStep(dt);
        double sum = 0; float mx = 0; var wet = 0; var cnt = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = SeparableNeuralSkinSssEngine.Index(x, y);
                if (sk.Mask[i] <= 0f) continue;
                var f = _m[(y + 1) * Wp + x + 1];
                sum += f; cnt++; if (f > mx) mx = f; if (f > 5f) wet++;
            }
        SkinMeanUm = cnt > 0 ? (float)(sum / cnt) : 0f; SkinMaxUm = mx; SkinWetFraction = cnt > 0 ? wet / (float)cnt : 0f;
        LastSkinUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;

        // 의상 원천
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var g = _gar;
        var P = g.P;
        var fabEvap = FabricEvaporation * (1f - Humidity) * windF * MathF.Max(0.2f, 1f + 0.03f * (Temperature - 20f));
        for (var i = 0; i < P; i++)
        {
            var s = _cap[i] > 0f ? _w[i] / _cap[i] : 1f;
            var inflow = 0f;
            if (_outer[i] < 0)
            {
                // 최외곽: 비 노출(링 중심 방사 외향 · 풍향)
                int t = g.TubeOf[i], r = Math.Max(0, (int)g.RingOf[i]);
                var c = g.Skin.Center[t * NeuralAnatomicalMuscleFasciaEngine.Rings + r];
                var o = g.Pos(i) - c; o.Y *= 0.2f;
                var ol = o.Length();
                var e = 0.25f + 0.75f * MathF.Max(0f, ol > 1e-6f ? Vector3.Dot(o / ol, -rainDir) : 0f);
                inflow += rainUm * e * dt;
                inflow -= MathF.Min(fabEvap * s * dt, _w[i]);
            }
            if (_inner[i] < 0)
            {
                // 피부 접촉 레이어: 체간 땀(부위 계수)
                var reg = g.TubeOf[i] == 0 ? 1f : g.TubeOf[i] <= 2 ? 0.7f : 0.5f;
                inflow += SweatRate * BodySweatGain * reg * dt;
            }
            // 워시번 흡수 한계(양의 유입만) · 잔여 유출
            if (inflow > 0f)
            {
                var abs = inflow * MathF.Sqrt(MathF.Max(0f, 1f - s));
                RunoffGrams += (inflow - abs) * 1e-4f;
                inflow = abs;
            }
            // 레이어 간 전달(안 → 밖 · 포화도 차)
            if (_outer[i] >= 0)
            {
                var j = _outer[i];
                var sj = _cap[j] > 0f ? _w[j] / _cap[j] : 1f;
                var q = LayerTransfer * (s - sj) * MathF.Min(_cap[i], _cap[j]) * dt;
                q = q > 0f ? MathF.Min(q, _w[i]) : MathF.Max(q, -_w[j]);
                _w[i] -= q; _w[j] += q;
            }
            _gsrc[i] = inflow;
        }
        WickStep(dt);
        Array.Clear(_layerS); Array.Clear(_layerMass); Array.Clear(_layerMaxS);
        Span<int> lc = stackalloc int[MultiLayerGarmentPbdDynamicsEngine.Layers];
        lc.Clear();
        Span<double> dryS = stackalloc double[MultiLayerGarmentPbdDynamicsEngine.Layers];
        Span<double> wetS = stackalloc double[MultiLayerGarmentPbdDynamicsEngine.Layers];
        dryS.Clear(); wetS.Clear();
        for (var i = 0; i < P; i++)
        {
            var l = g.LayerOf[i];
            var s = _cap[i] > 0f ? _w[i] / _cap[i] : 0f;
            _s[i] = s;
            _layerS[l] += s; lc[l]++;
            if (s > _layerMaxS[l]) _layerMaxS[l] = s;
            dryS[l] += _dry[i]; wetS[l] += _dry[i] + _w[i];
        }
        for (var l = 0; l < MultiLayerGarmentPbdDynamicsEngine.Layers; l++)
        {
            if (lc[l] > 0) _layerS[l] /= lc[l];
            _layerMass[l] = dryS[l] > 0 ? (float)(wetS[l] / dryS[l]) : 1f;
        }
        LastGarmentUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── 광학 ────────────────────────────────────────────────

    /// <summary>젖음 암전 알베도(레크너–도프 근사 · 수막 내부 반사).</summary>
    public static float WetAlbedo(float dry, float wetness)
    {
        var w = dry * (1f - InternalReflect) / (1f - dry * InternalReflect);
        return dry + (w - dry) * Math.Clamp(wetness, 0f, 1f);
    }

    /// <summary>피부 텍셀 젖음 정도(0..1 · 20 µm 포화).</summary>
    public float SkinWetness(int i) => Math.Clamp(Film(i) / 20f, 0f, 1f);

    /// <summary>젖음 거칠기(수막 경면화).</summary>
    public static float WetRoughness(float dry, float wetness) => dry + (MathF.Min(dry, 0.12f) - dry) * Math.Clamp(wetness, 0f, 1f);

    /// <summary>의상 입자 솔리드 색 배율(직물 휘도 기준 암전).</summary>
    public void FillGarmentShade(float[] dst)
    {
        for (var i = 0; i < _gar.P && i < dst.Length; i++)
        {
            var c = _drape.LayerMaterial((GarmentLayer)_gar.LayerOf[i]).Color;
            var lum = MathF.Max(0.02f, 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z);
            dst[i] = WetAlbedo(lum, _s[i]) / lum;
        }
    }
}
