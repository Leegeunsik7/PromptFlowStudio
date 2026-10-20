using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.PathTracing;

namespace PromptFlowStudio.Services.HairCloth;

public enum HairColorPreset : byte { Black, Brown, Blonde, Red, Silver }

public enum HairShadingModel : byte { Marschner, KajiyaKay }

/// <summary>
/// [PromptFlow Studio v16.83.0] Marschner/Kajiya-Kay 이방성 헤어 · 패브릭 셰이더 결합 가드(<see cref="ShadeHair"/> · <see cref="ShadeCloth"/> 무할당).
/// 헤어: 멜라닌(유/페오) → 흡수 σa(d'Eon 2011) · Marschner 3 로브(R 백색 · TT/TRT 착색 · 경도 가우시안 α/β 이동 · 방위 근사 = Karis 2016) |
/// Kajiya-Kay(Scheuermann 이중 이동 하이라이트) · 다중 산란 확산 근사.
/// 자기 그림자 = 딥 오패시티 맵(64² × 4 층 · 키 광원 공간 · 가닥 단면적 밀도 → Beer–Lambert 투과).
/// 의상: 랩 확산 · 시스(Sheen) · 경사 방향 이방성 광택 · 주름 차폐 · 헤어 그림자.
/// 가드: 방향 알베도 결정적 구적(96×48) → 에너지 배율(≤ 0.98) · NaN/파이어플라이 클램프 · 피부 SSS 그림자 번짐(채널별 지수) 오버레이 ·
/// v16.74 경로 추적 PbrMaterial · v16.58 3DGS 스플랫(가닥 = 접선 정렬 장축 가우시안).
/// </summary>
public sealed class NeuralHairClothShaderIntegrationGuard
{
    public const int ShadowRes = 64, ShadowLayers = 4, AlbedoTheta = 96, AlbedoPhi = 48;
    private static readonly float[] LayerEdge = { 0f, 0.004f, 0.012f, 0.03f };

    // 헤어 물성
    private float _eu = 2.2f, _pheo = 0.4f, _alphaR = -6f * MathF.PI / 180f, _betaR = 7f * MathF.PI / 180f, _eta = 1.55f, _diffuse = 0.25f;
    private HairShadingModel _model = HairShadingModel.Marschner;
    private bool _dirty = true;
    private Vector3 _sigma;

    public Vector3 KeyDir = Vector3.Normalize(new Vector3(-0.5f, 0.7f, 0.5f)), FillDir = Vector3.Normalize(new Vector3(0.7f, 0.15f, 0.6f)), RimDir = Vector3.Normalize(new Vector3(0.3f, 0.4f, -0.85f));
    public Vector3 KeyColor = new(2.6f, 2.45f, 2.25f), FillColor = new(0.5f, 0.55f, 0.65f), RimColor = new(1.8f, 1.75f, 1.7f), Ambient = new(0.05f, 0.055f, 0.065f);
    public float Exposure = 1f, ShadowDensity = 0.2f, StrandDiameterMm = 0.08f, RealHairCount = 100000f, FireflyLimit = 8f;

    public readonly float[] HairR, HairG, HairB, HairShadow;
    public readonly float[] ClothR = new float[GenerativeGarmentClothCollisionSynthesizer.MaxVerts], ClothG = new float[GenerativeGarmentClothCollisionSynthesizer.MaxVerts], ClothB = new float[GenerativeGarmentClothCollisionSynthesizer.MaxVerts];
    public readonly float[] SkinR = new float[NeuralFacialMuscleSimulator.VertexCount], SkinG = new float[NeuralFacialMuscleSimulator.VertexCount], SkinB = new float[NeuralFacialMuscleSimulator.VertexCount];
    private readonly float[] _dmin = new float[ShadowRes * ShadowRes], _op = new float[ShadowRes * ShadowRes * ShadowLayers];
    private Vector3 _bu, _bv, _bl;
    private float _u0, _v0, _cell = 1f;
    private bool _hasShadow;

    public NeuralHairClothShaderIntegrationGuard(int hairCapacity = NeuralStrandHairDynamicsEngine.DefaultCapacity)
    {
        var n = Math.Max(1, hairCapacity) * NeuralStrandHairDynamicsEngine.NodesPerStrand;
        HairR = new float[n]; HairG = new float[n]; HairB = new float[n]; HairShadow = new float[n];
        SetHairColor(HairColorPreset.Brown);
    }

    public HairColorPreset HairColor { get; private set; }
    public HairShadingModel Model { get => _model; set { _model = value; _dirty = true; } }
    public float Eumelanin { get => _eu; set { _eu = Math.Clamp(value, 0f, 12f); _dirty = true; } }
    public float Pheomelanin { get => _pheo; set { _pheo = Math.Clamp(value, 0f, 6f); _dirty = true; } }
    public float ShiftDeg { get => _alphaR * 180f / MathF.PI; set { _alphaR = Math.Clamp(value, -12f, 0f) * MathF.PI / 180f; _dirty = true; } }
    public float WidthDeg { get => _betaR * 180f / MathF.PI; set { _betaR = Math.Clamp(value, 2f, 20f) * MathF.PI / 180f; _dirty = true; } }
    public float Ior { get => _eta; set { _eta = Math.Clamp(value, 1.3f, 1.8f); _dirty = true; } }
    public Vector3 Absorption => _sigma;

    public float EnergyScale { get; private set; } = 1f;
    public float RawAlbedo { get; private set; }
    public float GuardedAlbedo { get; private set; }
    public long NanGuards { get; private set; }
    public long FireflyClamps { get; private set; }
    public double LastHairMs { get; private set; }
    public double LastShadowMs { get; private set; }
    public double LastClothMs { get; private set; }
    public int ShadedNodes { get; private set; }
    public float MeanHairShadow { get; private set; }
    public string? LastAction { get; private set; }

    public static string ColorName(HairColorPreset c) => c switch
    {
        HairColorPreset.Black => "흑발", HairColorPreset.Blonde => "금발", HairColorPreset.Red => "적발", HairColorPreset.Silver => "은발", _ => "갈색",
    };

    public void SetHairColor(HairColorPreset c)
    {
        HairColor = c;
        (_eu, _pheo) = c switch
        {
            HairColorPreset.Black => (8f, 0.5f), HairColorPreset.Blonde => (0.3f, 0.25f), HairColorPreset.Red => (0.35f, 2.4f),
            HairColorPreset.Silver => (0.04f, 0.02f), _ => (2.2f, 0.4f),
        };
        _dirty = true;
        LastAction = "🎨 헤어 컬러 " + ColorName(c);
    }

    /// <summary>d'Eon 2011 멜라닌 → 흡수 계수(지름 단위).</summary>
    public static Vector3 AbsorptionFromMelanin(float eu, float pheo) =>
        eu * new Vector3(0.419f, 0.697f, 1.37f) + pheo * new Vector3(0.187f, 0.4f, 1.05f);

    // ───────────────────────── BCSDF ─────────────────────────

    private static float Gauss(float x, float b) => MathF.Exp(-x * x / (2f * b * b)) / (b * 2.5066283f);

    private static float Fresnel(float eta, float c)
    {
        var f0 = (1f - eta) / (1f + eta); f0 *= f0;
        var m = 1f - Math.Clamp(c, 0f, 1f);
        return f0 + (1f - f0) * m * m * m * m * m;
    }

    private static Vector3 Exp(Vector3 v) => new(MathF.Exp(v.X), MathF.Exp(v.Y), MathF.Exp(v.Z));

    /// <summary>Marschner 경도 R 로브 M_R(θh) — 진단(최대 = α_R).</summary>
    public float LongitudinalR(float thetaH) => Gauss(thetaH - _alphaR, _betaR);

    /// <summary>Marschner 3 로브 분해(t = 모근→모끝 접선 · wi/wo = 단위 방향). 에너지 배율 미적용.</summary>
    public void EvalMarschner(Vector3 t, Vector3 wi, Vector3 wo, out Vector3 r, out Vector3 tt, out Vector3 trt)
    {
        EnsureGuard();
        EvalMarschnerCore(t, wi, wo, out r, out tt, out trt);
    }

    private void EvalMarschnerCore(Vector3 t, Vector3 wi, Vector3 wo, out Vector3 r, out Vector3 tt, out Vector3 trt)
    {
        float sinI = Math.Clamp(Vector3.Dot(t, wi), -1f, 1f), sinO = Math.Clamp(Vector3.Dot(t, wo), -1f, 1f);
        float thI = MathF.Asin(sinI), thO = MathF.Asin(sinO);
        float thH = 0.5f * (thI + thO), thD = 0.5f * (thO - thI), cosD = MathF.Max(MathF.Cos(thD), 1e-3f);
        var li = wi - sinI * t; var lo = wo - sinO * t;
        var den = MathF.Sqrt(li.LengthSquared() * lo.LengthSquared());
        var cosPhi = den > 1e-8f ? Math.Clamp(Vector3.Dot(li, lo) / den, -1f, 1f) : 1f;
        var cosHalf = MathF.Sqrt(Math.Clamp(0.5f + 0.5f * cosPhi, 0f, 1f));
        float aR = _alphaR, aTT = -0.5f * _alphaR, aTRT = -1.5f * _alphaR, bR = _betaR, bTT = 0.5f * _betaR, bTRT = 2f * _betaR;
        // R
        var fR = Fresnel(_eta, MathF.Sqrt(Math.Clamp(0.5f + 0.5f * Vector3.Dot(wi, wo), 0f, 1f)));
        r = new Vector3(Gauss(thH - aR, bR) * 0.25f * cosHalf * fR);
        // TT
        var etaP = 1.19f / cosD + 0.36f * cosD; var a = 1f / etaP;
        var h = Math.Clamp(cosHalf * (1f + a * (0.6f - 0.8f * cosPhi)), -1f, 1f);
        var path = MathF.Sqrt(MathF.Max(0f, 1f - h * h * a * a)) / cosD;
        var T = Exp(-_sigma * path);
        var fT = Fresnel(_eta, cosD * MathF.Sqrt(MathF.Max(0f, 1f - h * h)));
        var dTT = MathF.Exp(-3.65f * cosPhi - 3.98f);
        tt = Gauss(thH - aTT, bTT) * (1f - fT) * (1f - fT) * dTT * T;
        // TRT
        var T2 = Exp(-_sigma * (1.6f / cosD));
        var fTRT = Fresnel(_eta, cosD * 0.5f);
        var dTRT = MathF.Exp(17f * cosPhi - 16.78f);
        trt = Gauss(thH - aTRT, bTRT) * (1f - fTRT) * (1f - fTRT) * fTRT * dTRT * T2;
    }

    /// <summary>Kajiya-Kay + Scheuermann 이중 이동 하이라이트(에너지 배율 미적용 · 확산 포함).</summary>
    public Vector3 EvalKajiyaKay(Vector3 t, Vector3 wi, Vector3 wo)
    {
        EnsureGuard();
        return EvalKajiyaKayCore(t, wi, wo);
    }

    private Vector3 EvalKajiyaKayCore(Vector3 t, Vector3 wi, Vector3 wo)
    {
        var nv = wo - Vector3.Dot(wo, t) * t; var nl = nv.Length();
        var n = nl > 1e-6f ? nv / nl : Vector3.UnitZ;
        var col = Exp(-_sigma * 0.8f);
        var t1 = Vector3.Normalize(t + 0.08f * n); var t2 = Vector3.Normalize(t - 0.12f * n);
        var s1 = KkSpec(t1, wi, wo, 90f); var s2 = KkSpec(t2, wi, wo, 22f);
        var ti = Vector3.Dot(t, wi); var sinTL = MathF.Sqrt(MathF.Max(0f, 1f - ti * ti));
        return new Vector3(0.09f * s1) + 0.06f * s2 * col + _diffuse * col * sinTL / MathF.PI;
    }

    /// <summary>KK 정반사: (sinθl·sinθv − cosθl·cosθv)^p — 최대 = 반사 원뿔(t·v = −t·l).</summary>
    public static float KkSpec(Vector3 t, Vector3 wi, Vector3 wo, float p)
    {
        float tl = Vector3.Dot(t, wi), tv = Vector3.Dot(t, wo);
        var c = MathF.Sqrt(MathF.Max(0f, 1f - tl * tl)) * MathF.Sqrt(MathF.Max(0f, 1f - tv * tv)) - tl * tv;
        return c > 0f ? MathF.Pow(c, p) : 0f;
    }

    /// <summary>전체 BCSDF(현재 모델 · 확산 포함 · 에너지 배율 적용).</summary>
    public Vector3 Eval(Vector3 t, Vector3 wi, Vector3 wo)
    {
        EnsureGuard();
        return EvalRaw(t, wi, wo) * EnergyScale;
    }

    private Vector3 EvalRaw(Vector3 t, Vector3 wi, Vector3 wo)
    {
        if (_model == HairShadingModel.KajiyaKay) return EvalKajiyaKayCore(t, wi, wo);
        EvalMarschnerCore(t, wi, wo, out var r, out var tt, out var trt);
        return r + tt + trt + _diffuse * Exp(-_sigma) / MathF.PI;
    }

    /// <summary>방향 알베도 ∫ S · cosθi dω(결정적 구적 · t = +Y · wo 경도 θo).</summary>
    public Vector3 DirectionalAlbedo(float thetaODeg, bool guarded)
    {
        EnsureGuard();
        return AlbedoCore(thetaODeg * MathF.PI / 180f) * (guarded ? EnergyScale : 1f);
    }

    private Vector3 AlbedoCore(float thO)
    {
        var t = Vector3.UnitY; var n = Vector3.UnitZ; var b = Vector3.UnitX;
        var wo = MathF.Sin(thO) * t + MathF.Cos(thO) * n;
        var acc = Vector3.Zero;
        float dth = MathF.PI / AlbedoTheta, dph = 2f * MathF.PI / AlbedoPhi;
        for (var i = 0; i < AlbedoTheta; i++)
        {
            var th = -0.5f * MathF.PI + (i + 0.5f) * dth; var ct = MathF.Cos(th); var st = MathF.Sin(th);
            for (var j = 0; j < AlbedoPhi; j++)
            {
                var ph = (j + 0.5f) * dph;
                var wi = st * t + ct * (MathF.Cos(ph) * n + MathF.Sin(ph) * b);
                acc += EvalRaw(t, wi, wo) * (ct * ct * dth * dph);
            }
        }
        return acc;
    }

    /// <summary>가드: 물성 변경 시 흡수 갱신 + 최대 방향 알베도 → 에너지 배율(≤ 0.98).</summary>
    private void EnsureGuard()
    {
        if (!_dirty) return;
        _dirty = false;
        _sigma = AbsorptionFromMelanin(_eu, _pheo);
        var max = 0f;
        for (var k = -2; k <= 2; k++)
        {
            var a = AlbedoCore(k * 30f * MathF.PI / 180f);
            max = MathF.Max(max, MathF.Max(a.X, MathF.Max(a.Y, a.Z)));
        }
        RawAlbedo = max;
        EnergyScale = max > 0.98f ? 0.98f / max : 1f;
        GuardedAlbedo = max * EnergyScale;
    }

    // ───────────────────────── 딥 오패시티 맵 ─────────────────────────

    /// <summary>키 광원 공간 딥 오패시티 맵 구축(활성 전 노드 · 2 패스).</summary>
    public void BuildShadow(NeuralStrandHairDynamicsEngine hair)
    {
        var t0 = Stopwatch.GetTimestamp();
        _bl = Vector3.Normalize(KeyDir);
        _bu = Vector3.Normalize(Vector3.Cross(_bl, MathF.Abs(_bl.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX)); _bv = Vector3.Cross(_bu, _bl);
        var n = hair.NodeCount;
        float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
        var X = hair.X; var Y = hair.Y; var Z = hair.Z;
        for (var i = 0; i < n; i++)
        {
            float u = X[i] * _bu.X + Y[i] * _bu.Y + Z[i] * _bu.Z, v = X[i] * _bv.X + Y[i] * _bv.Y + Z[i] * _bv.Z;
            if (u < u0) u0 = u; if (u > u1) u1 = u; if (v < v0) v0 = v; if (v > v1) v1 = v;
        }
        var ext = MathF.Max(u1 - u0, v1 - v0) * 1.04f + 1e-3f;
        _cell = ext / ShadowRes; _u0 = 0.5f * (u0 + u1) - 0.5f * ext; _v0 = 0.5f * (v0 + v1) - 0.5f * ext;
        Array.Fill(_dmin, float.MaxValue); Array.Clear(_op);
        for (var i = 0; i < n; i++)
        {
            var c = Cell(X[i], Y[i], Z[i], out var d);
            if (d < _dmin[c]) _dmin[c] = d;
        }
        // 가닥 단면적(지름 × 세그먼트) × 실제 모발 수 보정 / 셀 면적
        var seg = n > 0 ? hair.SegmentLength(0) : 0.01f;
        var kappa = ShadowDensity * StrandDiameterMm * 1e-3f * seg * (RealHairCount / MathF.Max(hair.ActiveStrands, 1)) / (_cell * _cell);
        // 밀도 누적은 가닥 2:1 표본(κ × 2 보정)
        const int N = NeuralStrandHairDynamicsEngine.NodesPerStrand;
        kappa *= 2f;
        for (var i = 0; i < n; i++)
        {
            if (((i / N) & 1) != 0) { i += N - 1; continue; }
            var c = Cell(X[i], Y[i], Z[i], out var d);
            var dd = d - _dmin[c]; var l = ShadowLayers - 1;
            while (l > 0 && dd < LayerEdge[l]) l--;
            _op[c * ShadowLayers + l] += kappa;
        }
        _hasShadow = n > 0;
        LastShadowMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private int Cell(float x, float y, float z, out float depth)
    {
        float u = x * _bu.X + y * _bu.Y + z * _bu.Z, v = x * _bv.X + y * _bv.Y + z * _bv.Z;
        depth = -(x * _bl.X + y * _bl.Y + z * _bl.Z);
        var cu = Math.Clamp((int)((u - _u0) / _cell), 0, ShadowRes - 1); var cv = Math.Clamp((int)((v - _v0) / _cell), 0, ShadowRes - 1);
        return cv * ShadowRes + cu;
    }

    /// <summary>키 광원 투과율(0..1) — 임의 월드 점(헤어 · 의상 · 피부).</summary>
    public float ShadowAt(Vector3 p)
    {
        if (!_hasShadow) return 1f;
        float u = Vector3.Dot(p, _bu), v = Vector3.Dot(p, _bv);
        if (u < _u0 || v < _v0 || u >= _u0 + _cell * ShadowRes || v >= _v0 + _cell * ShadowRes) return 1f;
        var c = Cell(p.X, p.Y, p.Z, out var d);
        var dm = _dmin[c];
        if (dm == float.MaxValue) return 1f;
        var dd = d - dm;
        if (dd <= 0f) return 1f;
        var op = 0f; var o = c * ShadowLayers;
        for (var l = 0; l < ShadowLayers; l++)
        {
            var lo = LayerEdge[l]; var hi = l + 1 < ShadowLayers ? LayerEdge[l + 1] : lo + 0.05f;
            if (dd >= hi) { op += _op[o + l]; continue; }
            if (dd > lo) op += _op[o + l] * (dd - lo) / (hi - lo);
            break;
        }
        return MathF.Exp(-op);
    }

    // ───────────────────────── 셰이딩 ─────────────────────────

    private static float Tone(float x)
    {
        x = MathF.Max(0f, x);
        var t = x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f);
        return MathF.Pow(Math.Clamp(t, 0f, 1f), 1f / 2.2f);
    }

    private Vector3 Guard(Vector3 c)
    {
        if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z)) { NanGuards++; return Vector3.Zero; }
        var m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (m > FireflyLimit) { FireflyClamps++; c *= FireflyLimit / m; }
        return c;
    }

    /// <summary>헤어 셰이딩(가닥 stride 간격 표시 가닥만 · 무할당).</summary>
    public void ShadeHair(NeuralStrandHairDynamicsEngine hair, Vector3 eye, int stride)
    {
        var t0 = Stopwatch.GetTimestamp();
        EnsureGuard();
        stride = Math.Max(1, stride);
        const int N = NeuralStrandHairDynamicsEngine.NodesPerStrand;
        var amb = Ambient * 2f * Exp(-_sigma * 0.5f);
        var shaded = 0; var shSum = 0f;
        for (var s = 0; s < hair.ActiveStrands; s += stride)
        {
            var b = s * N;
            for (var k = 0; k < N; k++)
            {
                var i = b + k;
                var p = hair.Position(i);
                var tv = k < N - 1 ? hair.Position(i + 1) - p : p - hair.Position(i - 1);
                var tl = tv.Length(); var t = tl > 1e-9f ? tv / tl : -Vector3.UnitY;
                var wo = Vector3.Normalize(eye - p);
                var sh = ShadowAt(p);
                var c = amb;
                c += Guard(Light(t, KeyDir, wo) * KeyColor) * sh;
                c += Guard(Light(t, FillDir, wo) * FillColor);
                c += Guard(Light(t, RimDir, wo) * RimColor);
                c *= Exposure;
                HairR[i] = Tone(c.X); HairG[i] = Tone(c.Y); HairB[i] = Tone(c.Z); HairShadow[i] = sh;
                shSum += sh; shaded++;
            }
        }
        ShadedNodes = shaded; MeanHairShadow = shaded > 0 ? shSum / shaded : 1f;
        LastHairMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private Vector3 Light(Vector3 t, Vector3 wi, Vector3 wo)
    {
        var ti = Vector3.Dot(t, wi);
        return EvalRaw(t, wi, wo) * (EnergyScale * MathF.Sqrt(MathF.Max(0f, 1f - ti * ti)));
    }

    /// <summary>의상 셰이딩: 랩 확산 · 시스 · 경사 이방성 광택 · 주름 차폐 · 헤어 그림자(양면).</summary>
    public void ShadeCloth(GenerativeGarmentClothCollisionSynthesizer cloth, Vector3 eye)
    {
        var t0 = Stopwatch.GetTimestamp();
        var m = cloth.Material;
        const int C = GenerativeGarmentClothCollisionSynthesizer.Cols, R = GenerativeGarmentClothCollisionSynthesizer.Rows;
        for (var i = 0; i < R; i++)
            for (var j = 0; j < C; j++)
            {
                var v = GenerativeGarmentClothCollisionSynthesizer.Index(i, j);
                var p = cloth.Position(v); var n = cloth.Normal(v);
                var wo = Vector3.Normalize(eye - p);
                if (Vector3.Dot(n, wo) < 0f) n = -n;
                var warp = cloth.Position(GenerativeGarmentClothCollisionSynthesizer.Index(Math.Min(i + 1, R - 1), j)) - cloth.Position(GenerativeGarmentClothCollisionSynthesizer.Index(Math.Max(i - 1, 0), j));
                var wl = warp.Length(); var t = wl > 1e-9f ? warp / wl : -Vector3.UnitY;
                var ao = 1f - 0.55f * cloth.Wrinkle[v];
                var c = Ambient * 2f * m.Color * ao;
                c += Guard(Fabric(m, n, t, KeyDir, wo, ao) * KeyColor) * ShadowAt(p);
                c += Guard(Fabric(m, n, t, FillDir, wo, ao) * FillColor);
                c += Guard(Fabric(m, n, t, RimDir, wo, ao) * RimColor);
                c *= Exposure;
                ClothR[v] = Tone(c.X); ClothG[v] = Tone(c.Y); ClothB[v] = Tone(c.Z);
            }
        LastClothMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private static Vector3 Fabric(in FabricMaterial m, Vector3 n, Vector3 t, Vector3 wi, Vector3 wo, float ao)
    {
        var nl = Vector3.Dot(n, wi);
        var wrap = MathF.Max(0f, (nl + 0.35f) / 1.35f);
        var diff = m.Color * (wrap / MathF.PI) * ao;
        var nv = MathF.Max(Vector3.Dot(n, wo), 1e-3f);
        var sheen = m.Sheen * MathF.Pow(1f - nv, 4f) * MathF.Max(0f, nl + 0.2f) * 0.6f;
        var hv = Vector3.Normalize(wi + wo);
        var shin = 2f / MathF.Max(m.Roughness * m.Roughness, 0.02f);
        var iso = MathF.Pow(MathF.Max(0f, Vector3.Dot(n, hv)), shin) * (shin + 2f) / (8f * MathF.PI) * MathF.Max(nl, 0f);
        var aniso = KkSpec(t, wi, wo, 6f + 40f * (1f - m.Roughness)) * MathF.Max(nl, 0f) * 0.25f;
        var spec = 0.04f * ((1f - m.Anisotropy) * iso + m.Anisotropy * aniso * 6f) * ao;
        return diff + new Vector3(sheen + spec);
    }

    /// <summary>피부 오버레이: SSS 셰이딩 색 × 헤어 그림자(채널별 지수 0.6/0.85/1.0 = 적색 산란 번짐 근사).</summary>
    public void OverlaySkin(NeuralFacialMuscleSimulator face, PhotorealisticMicroSkinSssSynthesizer skin, Matrix4x4 headPose)
    {
        for (var v = 0; v < NeuralFacialMuscleSimulator.VertexCount; v++)
        {
            var s = ShadowAt(Vector3.Transform(face.Position(v), headPose));
            var a = 0.35f + 0.65f * s;
            SkinR[v] = skin.ColorR[v] * MathF.Pow(a, 0.6f); SkinG[v] = skin.ColorG[v] * MathF.Pow(a, 0.85f); SkinB[v] = skin.ColorB[v] * a;
        }
    }

    // ───────────────────────── 경로 추적 결선 ─────────────────────────

    public PbrMaterial ToHairPbr()
    {
        EnsureGuard();
        var m = PbrMaterial.Default;
        m.BaseColor = Vector3.Clamp(Exp(-_sigma * 0.8f), Vector3.Zero, Vector3.One);
        m.Roughness = Math.Clamp(MathF.Sqrt(_betaR), 0.05f, 1f); m.Specular = 1f; m.Ior = _eta; m.Metallic = 0f;
        return m;
    }

    public static PbrMaterial ToFabricPbr(in FabricMaterial f)
    {
        var m = PbrMaterial.Default;
        m.BaseColor = f.Color; m.Roughness = f.Roughness; m.Specular = 0.5f; m.Ior = 1.5f; m.Metallic = 0f;
        m.Subsurface = 0.1f * f.Sheen; m.SubsurfaceColor = f.Color; m.SubsurfaceRadius = 0.002f;
        return m;
    }
}
