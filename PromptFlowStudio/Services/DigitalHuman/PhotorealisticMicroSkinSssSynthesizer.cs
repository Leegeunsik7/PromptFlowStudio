using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.PathTracing;

namespace PromptFlowStudio.Services.DigitalHuman;

public enum SkinLook : byte { Natural, Fair, Deep, Oily, Aged }

/// <summary>
/// [PromptFlow Studio v16.82.0] 포토리얼 미세 피부 SSS · 모공 합성기(<see cref="Shade"/> 무할당).
/// ① 2-발색단 알베도(멜라닌 · 헤모글로빈 흡수 → RGB · 입술 혈류 가중) → ② 미세 기하: 결정적 모공 노이즈 + 표정 구동 주름 맵
/// (이마 = 눈썹 올림 · 눈가 = 눈 찡그림/볼 올림 · 팔자 = 미소/코 찡그림 · 미간 = 눈썹 내림) → 캐비티 · 미세 법선 →
/// ③ 조도(키 · 필 · 림 3광원) → ④ 텍스처 공간 확산(d'Eon 2007): 사전/사후 √알베도 분할 + Burley 정규화 확산 근사 분리형 2패스
/// (채널별 평균 자유 행정 R &gt; G &gt; B · 소스 정규화 산란 = 에너지 정확 보존) → ⑤ 이중 로브 GGX 정반사(F0 0.028 · 유분 · 모공 캐비티 차폐) →
/// ⑥ 톤매핑 → 정점 색 → v16.58 3DGS 스플랫(<see cref="WriteSplats"/>) · v16.74 경로 추적 재질(<see cref="ToPbrMaterial"/>).
/// </summary>
public sealed class PhotorealisticMicroSkinSssSynthesizer
{
    public const int V = NeuralFacialMuscleSimulator.VertexCount, W = NeuralFacialMuscleSimulator.GridW, H = NeuralFacialMuscleSimulator.GridH;
    public const int KernelRadius = 6, Taps = KernelRadius * 2 + 1;
    public const float SkinIor = 1.4f;
    /// <summary>채널별 평균 자유 행정 비(R:G:B · Christensen–Burley 피부 실측 3.67 : 1.37 : 0.68 mm 정규화).</summary>
    public static readonly Vector3 MfpRatio = new(1f, 0.373f, 0.185f);

    // 피부 룩 파라미터(0..1 · 산란 mm)
    public float Melanin = 0.3f, Hemoglobin = 0.45f, ScatterMm = 5f, PoreDensity = 0.6f, PoreDepth = 0.5f, WrinkleDepth = 0.6f, Oiliness = 0.45f, Roughness = 0.42f, Translucency = 0.5f;
    public Vector3 KeyDir = Vector3.Normalize(new Vector3(-0.55f, 0.45f, 0.7f)), FillDir = Vector3.Normalize(new Vector3(0.7f, 0.1f, 0.6f)), RimDir = Vector3.Normalize(new Vector3(0.4f, 0.35f, -0.85f));
    public Vector3 KeyColor = new(1.15f, 1.05f, 0.95f), FillColor = new(0.28f, 0.32f, 0.4f), RimColor = new(0.9f, 0.85f, 0.8f), Ambient = new(0.06f, 0.065f, 0.075f);
    public float Exposure = 1.1f;

    public readonly float[] ColorR = new float[V], ColorG = new float[V], ColorB = new float[V];
    public readonly float[] Cavity = new float[V], Wrinkle = new float[V], Pore = new float[V];
    private readonly float[] _wrinkleMask = new float[4 * V], _wrinklePattern = new float[4 * V], _lip = new float[V];
    private readonly float[] _ar = new float[V], _ag = new float[V], _ab = new float[V];
    private readonly float[] _er = new float[V], _eg = new float[V], _eb = new float[V], _tmp = new float[V], _out = new float[V];
    private readonly float[] _kR = new float[Taps], _kG = new float[Taps], _kB = new float[Taps];
    private readonly float[] _nRowR = new float[W], _nRowG = new float[W], _nRowB = new float[W], _nColR = new float[H], _nColG = new float[H], _nColB = new float[H];
    private float _hx = 0.004f, _hy = 0.004f, _kernelScatter = -1f;

    public PhotorealisticMicroSkinSssSynthesizer() => SetLook(SkinLook.Natural);

    public SkinLook Look { get; private set; }
    public double LastShadeMs { get; private set; }
    public float EnergyError { get; private set; }
    public float WrinkleActivity { get; private set; }
    public Vector3 MeanAlbedo { get; private set; }
    public long Shades { get; private set; }
    public ReadOnlySpan<float> KernelR => _kR;
    public ReadOnlySpan<float> KernelG => _kG;
    public ReadOnlySpan<float> KernelB => _kB;
    public float GridSpacingMm => 500f * (_hx + _hy);

    public static string LookName(SkinLook l) => l switch
    {
        SkinLook.Fair => "밝은 피부", SkinLook.Deep => "깊은 톤", SkinLook.Oily => "유분 광택", SkinLook.Aged => "노화 주름", _ => "내추럴",
    };

    public void SetLook(SkinLook l)
    {
        Look = l;
        (Melanin, Hemoglobin, ScatterMm, PoreDensity, PoreDepth, WrinkleDepth, Oiliness, Roughness, Translucency) = l switch
        {
            SkinLook.Fair => (0.12f, 0.5f, 6f, 0.5f, 0.35f, 0.45f, 0.35f, 0.4f, 0.65f),
            SkinLook.Deep => (0.85f, 0.4f, 4f, 0.6f, 0.45f, 0.5f, 0.5f, 0.38f, 0.35f),
            SkinLook.Oily => (0.3f, 0.5f, 5f, 0.85f, 0.7f, 0.5f, 0.9f, 0.28f, 0.5f),
            SkinLook.Aged => (0.35f, 0.38f, 4.5f, 0.75f, 0.65f, 1f, 0.25f, 0.55f, 0.4f),
            _ => (0.3f, 0.45f, 5f, 0.6f, 0.5f, 0.6f, 0.45f, 0.42f, 0.5f),
        };
    }

    // ───────────────────────── 물성 ─────────────────────────

    /// <summary>2-발색단 확산 알베도(멜라닌 = 단파장 강흡수 · 헤모글로빈 = G/B 흡수 → 붉은 기).</summary>
    public static Vector3 SkinAlbedo(float melanin, float hemoglobin)
    {
        var m = Math.Clamp(melanin, 0f, 1f) * 3.2f; var h = Math.Clamp(hemoglobin, 0f, 1f) * 1.1f;
        var mu = m * new Vector3(0.55f, 0.95f, 1.55f) + h * new Vector3(0.12f, 0.95f, 0.75f);
        return 0.86f * new Vector3(MathF.Exp(-mu.X), MathF.Exp(-mu.Y), MathF.Exp(-mu.Z)) + new Vector3(0.02f);
    }

    /// <summary>Burley 정규화 확산 반경 프로파일(R(r) = (e^{−r/d} + e^{−r/3d}) / 8πdr · 적분 = 1).</summary>
    public static float BurleyProfile(float r, float d)
    {
        r = MathF.Max(r, 1e-6f);
        return (MathF.Exp(-r / d) + MathF.Exp(-r / (3f * d))) / (8f * MathF.PI * d * r);
    }

    /// <summary>v16.74 경로 추적 재질(MaterialX BSDF · 랜덤워크 SSS) 결선.</summary>
    public PbrMaterial ToPbrMaterial()
    {
        var a = SkinAlbedo(Melanin, Hemoglobin);
        var m = PbrMaterial.Default;
        m.BaseColor = a; m.Metallic = 0f; m.Roughness = Math.Clamp(Roughness, 0.05f, 1f); m.Specular = 0.5f + 0.5f * Oiliness; m.Ior = SkinIor;
        m.Subsurface = Math.Clamp(0.55f + 0.4f * Translucency, 0f, 1f); m.SubsurfaceRadius = ScatterMm * 1e-3f;
        m.SubsurfaceColor = Vector3.Clamp(a * 0.5f + MfpRatio * 0.5f, Vector3.Zero, Vector3.One);
        return m;
    }

    // ───────────────────────── 정적 맵 ─────────────────────────

    private static float Hash(int x, int y)
    {
        var h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
    }

    /// <summary>메시 바인딩: 격자 간격 · 모공 노이즈 · 주름 마스크/패턴 · 입술 마스크(1회).</summary>
    public void Bind(NeuralFacialMuscleSimulator face)
    {
        double sx = 0, sy = 0; var nx = 0; var ny = 0;
        for (var iy = 0; iy < H; iy++)
            for (var ix = 0; ix < W; ix++)
            {
                var v = NeuralFacialMuscleSimulator.Index(ix, iy);
                if (face.NeutralPosition(v).Z < 0.02f) continue;
                if (ix + 1 < W) { sx += Vector3.Distance(face.NeutralPosition(v), face.NeutralPosition(v + 1)); nx++; }
                if (iy + 1 < H) { sy += Vector3.Distance(face.NeutralPosition(v), face.NeutralPosition(v + W)); ny++; }
            }
        _hx = nx > 0 ? (float)(sx / nx) : 0.004f; _hy = ny > 0 ? (float)(sy / ny) : 0.004f;
        _kernelScatter = -1f;
        for (var v = 0; v < V; v++)
        {
            var p = face.NeutralPosition(v); var ax = MathF.Abs(p.X);
            var ix = v % W; var iy = v / W;
            // 2옥타브 결정적 모공(0..1)
            Pore[v] = 0.65f * Hash(ix, iy) + 0.35f * Hash(ix * 3 + 17, iy * 3 + 5);
            var front = Math.Clamp(p.Z / 0.05f, 0f, 1f);
            // 0 이마(가로 주름) · 1 눈가(방사) · 2 팔자 · 3 미간(세로)
            _wrinkleMask[v] = front * Smooth01((p.Y - 0.05f) / 0.012f) * Smooth01((0.085f - p.Y) / 0.012f) * Smooth01((0.05f - ax) / 0.015f);
            _wrinklePattern[v] = 0.5f + 0.5f * MathF.Sin(p.Y * 2f * MathF.PI / 0.012f);
            var ex = ax - 0.052f; var ey = p.Y - 0.022f;
            _wrinkleMask[V + v] = front * MathF.Exp(-(ex * ex + ey * ey) / (0.009f * 0.009f));
            _wrinklePattern[V + v] = 0.5f + 0.5f * MathF.Sin(MathF.Atan2(ey, ex) * 7f);
            // 팔자: 콧방울(±0.018, −0.01) → 입꼬리(±0.031, −0.046) 선분 거리
            var t = Math.Clamp(((ax - 0.018f) * 0.013f + (p.Y + 0.01f) * -0.036f) / (0.013f * 0.013f + 0.036f * 0.036f), 0f, 1f);
            var dx = ax - (0.018f + 0.013f * t); var dy = p.Y - (-0.01f - 0.036f * t);
            var dl = MathF.Sqrt(dx * dx + dy * dy);
            _wrinkleMask[2 * V + v] = front * MathF.Exp(-dl * dl / (0.004f * 0.004f));
            _wrinklePattern[2 * V + v] = 1f;
            _wrinkleMask[3 * V + v] = front * Smooth01((0.012f - ax) / 0.006f) * Smooth01((p.Y - 0.03f) / 0.006f) * Smooth01((0.055f - p.Y) / 0.006f);
            _wrinklePattern[3 * V + v] = 0.5f + 0.5f * MathF.Cos(p.X * 2f * MathF.PI / 0.008f);
            _lip[v] = front * (MathF.Exp(-(p.X * p.X / (0.022f * 0.022f) + (p.Y + 0.036f) * (p.Y + 0.036f) / (0.0045f * 0.0045f)))
                             + MathF.Exp(-(p.X * p.X / (0.021f * 0.021f) + (p.Y + 0.051f) * (p.Y + 0.051f) / (0.0055f * 0.0055f))));
        }
    }

    private static float Smooth01(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    private void BuildKernels()
    {
        if (_kernelScatter == ScatterMm) return;
        _kernelScatter = ScatterMm;
        var h = 0.5f * (_hx + _hy);
        Fill(_kR, ScatterMm * 1e-3f * MfpRatio.X, h); Fill(_kG, ScatterMm * 1e-3f * MfpRatio.Y, h); Fill(_kB, ScatterMm * 1e-3f * MfpRatio.Z, h);
        Norm(_kR, _nRowR); Norm(_kG, _nRowG); Norm(_kB, _nRowB);
        Norm(_kR, _nColR); Norm(_kG, _nColG); Norm(_kB, _nColB);

        static void Fill(float[] k, float d, float h)
        {
            // 분리형 근사: 탭 = 셀 중심 반경의 Burley 프로파일 × 2πr(링 면적) · r0 = h/4(특이점 회피)
            for (var t = -KernelRadius; t <= KernelRadius; t++)
            {
                var r = MathF.Max(MathF.Abs(t) * h, 0.25f * h);
                k[t + KernelRadius] = BurleyProfile(r, d) * r;
            }
            var s = 0f; foreach (var x in k) s += x;
            for (var t = 0; t < k.Length; t++) k[t] /= s;
        }

        // 소스 정규화: 위치 i 에서 유효 범위 탭 합의 역수(경계 에너지 보존)
        static void Norm(float[] k, float[] n)
        {
            for (var i = 0; i < n.Length; i++)
            {
                var s = 0f;
                for (var t = -KernelRadius; t <= KernelRadius; t++) { var j = i + t; if (j >= 0 && j < n.Length) s += k[t + KernelRadius]; }
                n[i] = s > 0f ? 1f / s : 0f;
            }
        }
    }

    // ───────────────────────── 셰이딩 ─────────────────────────

    /// <summary>1프레임 셰이딩(무할당): 알베도 → 미세 기하 → 조도 → 확산(SSS) → 정반사 → 톤매핑.</summary>
    public void Shade(NeuralFacialMuscleSimulator face, Vector3 eye)
    {
        var t0 = Stopwatch.GetTimestamp();
        BuildKernels();
        var baseA = SkinAlbedo(Melanin, Hemoglobin); var lipA = SkinAlbedo(Melanin * 0.8f, MathF.Min(1f, Hemoglobin + 0.45f));
        // 주름 구동(블렌드셰이프 웨이트)
        var wF = 0.5f * (face.Weight(ArkitBlendshape.BrowInnerUp) + 0.5f * (face.Weight(ArkitBlendshape.BrowOuterUpLeft) + face.Weight(ArkitBlendshape.BrowOuterUpRight)));
        var wE = 0.5f * (face.Weight(ArkitBlendshape.EyeSquintLeft) + face.Weight(ArkitBlendshape.EyeSquintRight) + face.Weight(ArkitBlendshape.CheekSquintLeft) + face.Weight(ArkitBlendshape.CheekSquintRight)) * 0.5f;
        var wN = 0.5f * (face.Weight(ArkitBlendshape.MouthSmileLeft) + face.Weight(ArkitBlendshape.MouthSmileRight)) + 0.5f * (face.Weight(ArkitBlendshape.NoseSneerLeft) + face.Weight(ArkitBlendshape.NoseSneerRight));
        var wG = 0.5f * (face.Weight(ArkitBlendshape.BrowDownLeft) + face.Weight(ArkitBlendshape.BrowDownRight));
        float act = 0f, sa = 0f; Vector3 meanA = default; var cnt = 0;
        var poreAmp = PoreDepth * (0.4f + 0.6f * PoreDensity);
        for (var v = 0; v < V; v++)
        {
            var wr = WrinkleDepth * (0.15f + 0.85f * MathF.Min(1f, wF)) * _wrinkleMask[v] * _wrinklePattern[v]
                   + WrinkleDepth * _wrinkleMask[V + v] * _wrinklePattern[V + v] * MathF.Min(1f, wE)
                   + WrinkleDepth * _wrinkleMask[2 * V + v] * (0.2f + 0.8f * MathF.Min(1f, wN))
                   + WrinkleDepth * _wrinkleMask[3 * V + v] * _wrinklePattern[3 * V + v] * MathF.Min(1f, wG);
            wr = MathF.Min(wr, 1f);
            Wrinkle[v] = wr; act += wr;
            var pore = Pore[v] > 1f - PoreDensity * 0.6f ? (Pore[v] - (1f - PoreDensity * 0.6f)) / MathF.Max(PoreDensity * 0.6f, 1e-3f) : 0f;
            Cavity[v] = Math.Clamp(1f - 0.35f * poreAmp * pore - 0.45f * wr, 0.2f, 1f);
            var lip = MathF.Min(1f, _lip[v]);
            var a = baseA + (lipA - baseA) * lip;
            _ar[v] = a.X; _ag[v] = a.Y; _ab[v] = a.Z;
            // 조도(기하 법선) × √알베도(사전 산란)
            var n = face.Normal(v);
            var e = Ambient + KeyColor * MathF.Max(0f, Vector3.Dot(n, KeyDir)) + FillColor * MathF.Max(0f, Vector3.Dot(n, FillDir))
                  + RimColor * MathF.Max(0f, Vector3.Dot(n, RimDir));
            // 박막 투과(림 역광 · 채널별 감쇠: 붉은 빛이 더 투과)
            var back = Translucency * MathF.Max(0f, -Vector3.Dot(n, RimDir)) * 0.25f;
            e += back * RimColor * new Vector3(MathF.Exp(-0.5f / MfpRatio.X), MathF.Exp(-0.5f / MfpRatio.Y), MathF.Exp(-0.5f / MfpRatio.Z));
            _er[v] = e.X * MathF.Sqrt(a.X); _eg[v] = e.Y * MathF.Sqrt(a.Y); _eb[v] = e.Z * MathF.Sqrt(a.Z);
            if (face.NeutralPosition(v).Z > 0.02f) { meanA += a; cnt++; }
        }
        WrinkleActivity = act / V; MeanAlbedo = cnt > 0 ? meanA / cnt : baseA;
        // 확산(채널별 분리형 2패스 · 에너지 검증 = R 채널)
        var inSum = 0f; for (var v = 0; v < V; v++) inSum += _er[v];
        Diffuse(_er, _kR, _nRowR, _nColR); Diffuse(_eg, _kG, _nRowG, _nColG); Diffuse(_eb, _kB, _nRowB, _nColB);
        for (var v = 0; v < V; v++) sa += _er[v];
        EnergyError = inSum > 0f ? MathF.Abs(sa - inSum) / inSum : 0f;
        // 정반사 + 합성
        var f0 = (SkinIor - 1f) / (SkinIor + 1f); f0 *= f0;
        float r1 = Math.Clamp(Roughness * 0.8f, 0.08f, 1f), r2 = Math.Clamp(Roughness * 1.4f, 0.08f, 1f);
        var specGain = 0.35f + 0.9f * Oiliness;
        for (var v = 0; v < V; v++)
        {
            var p = face.Position(v); var n = MicroNormal(face, v);
            var wo = Vector3.Normalize(eye - p);
            var spec = Vector3.Zero;
            spec += KeyColor * Spec(n, wo, KeyDir, r1, r2, f0);
            spec += RimColor * Spec(n, wo, RimDir, r1, r2, f0);
            spec += FillColor * Spec(n, wo, FillDir, r1, r2, f0);
            var c = Cavity[v];
            var d = new Vector3(_er[v] * MathF.Sqrt(_ar[v]), _eg[v] * MathF.Sqrt(_ag[v]), _eb[v] * MathF.Sqrt(_ab[v])) * (0.55f + 0.45f * c);
            var col = (d + spec * specGain * c * c) * Exposure;
            ColorR[v] = Tone(col.X); ColorG[v] = Tone(col.Y); ColorB[v] = Tone(col.Z);
        }
        Shades++;
        LastShadeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>미세 법선: 모공(격자 해시 기울기) + 주름(패턴 기울기) 섭동.</summary>
    private Vector3 MicroNormal(NeuralFacialMuscleSimulator face, int v)
    {
        var n = face.Normal(v);
        int ix = v % W, iy = v / W;
        var gx = Pore[NeuralFacialMuscleSimulator.Index(Math.Min(ix + 1, W - 1), iy)] - Pore[NeuralFacialMuscleSimulator.Index(Math.Max(ix - 1, 0), iy)];
        var gy = Pore[NeuralFacialMuscleSimulator.Index(ix, Math.Min(iy + 1, H - 1))] - Pore[NeuralFacialMuscleSimulator.Index(ix, Math.Max(iy - 1, 0))];
        var s = 0.12f * PoreDepth * PoreDensity;
        var wy = Wrinkle[NeuralFacialMuscleSimulator.Index(ix, Math.Min(iy + 1, H - 1))] - Wrinkle[NeuralFacialMuscleSimulator.Index(ix, Math.Max(iy - 1, 0))];
        var p = n + new Vector3(-s * gx, -s * gy - 0.4f * wy, 0f);
        return Vector3.Normalize(p);
    }

    private static float Ggx(float nh, float a)
    {
        var a2 = a * a * a * a; var d = nh * nh * (a2 - 1f) + 1f;
        return a2 / (MathF.PI * d * d);
    }

    /// <summary>이중 로브 GGX(0.85 / 0.15) · Schlick 프레넬 · 단순 가시성(Kelemen 근사).</summary>
    private static float Spec(Vector3 n, Vector3 wo, Vector3 wi, float r1, float r2, float f0)
    {
        var nl = Vector3.Dot(n, wi); var nv = Vector3.Dot(n, wo);
        if (nl <= 0f || nv <= 0f) return 0f;
        var hv = Vector3.Normalize(wi + wo);
        var nh = MathF.Max(0f, Vector3.Dot(n, hv)); var vh = MathF.Max(0f, Vector3.Dot(wo, hv));
        var f = f0 + (1f - f0) * MathF.Pow(1f - vh, 5f);
        var d = 0.85f * Ggx(nh, r1) + 0.15f * Ggx(nh, r2);
        return MathF.Min(d * f * nl / MathF.Max(4f * vh * vh, 1e-3f), 8f);
    }

    private static float Tone(float x)
    {
        x = MathF.Max(0f, x);
        var t = x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f); // ACES 근사(Narkowicz)
        return MathF.Pow(Math.Clamp(t, 0f, 1f), 1f / 2.2f);
    }

    /// <summary>분리형 확산: 행 → 열(소스 정규화 산란 · Σ출력 = Σ입력).</summary>
    private void Diffuse(float[] e, float[] k, float[] nRow, float[] nCol)
    {
        var o = _tmp; Array.Clear(o);
        for (var iy = 0; iy < H; iy++)
        {
            var row = iy * W;
            for (var i = 0; i < W; i++)
            {
                var src = e[row + i] * nRow[i];
                if (src == 0f) continue;
                int t0 = Math.Max(-KernelRadius, -i), t1 = Math.Min(KernelRadius, W - 1 - i);
                for (var t = t0; t <= t1; t++) o[row + i + t] += src * k[t + KernelRadius];
            }
        }
        var r = _out; Array.Clear(r);
        for (var ix = 0; ix < W; ix++)
            for (var i = 0; i < H; i++)
            {
                var src = o[i * W + ix] * nCol[i];
                if (src == 0f) continue;
                int t0 = Math.Max(-KernelRadius, -i), t1 = Math.Min(KernelRadius, H - 1 - i);
                for (var t = t0; t <= t1; t++) r[(i + t) * W + ix] += src * k[t + KernelRadius];
            }
        r.AsSpan().CopyTo(e);
    }

    /// <summary>테스트/진단: 임의 조도 장(채널 지정)에 확산 1회 적용(무할당 · 입력 덮어씀).</summary>
    public void DiffuseChannel(float[] e, int channel)
    {
        BuildKernels();
        switch (channel)
        {
            case 0: Diffuse(e, _kR, _nRowR, _nColR); break;
            case 1: Diffuse(e, _kG, _nRowG, _nColG); break;
            default: Diffuse(e, _kB, _nRowB, _nColB); break;
        }
    }

    /// <summary>커널 표준편차(탭 단위 · 진단: R &gt; G &gt; B).</summary>
    public float KernelSigma(int channel)
    {
        BuildKernels();
        var k = channel == 0 ? _kR : channel == 1 ? _kG : _kB;
        var s = 0f;
        for (var t = -KernelRadius; t <= KernelRadius; t++) s += k[t + KernelRadius] * t * t;
        return MathF.Sqrt(s);
    }

    /// <summary>3DGS 스플랫 기록(정점당 법선 정렬 원반 가우시안 · 색 = 셰이딩 결과).</summary>
    public void WriteSplats(NeuralFacialMuscleSimulator face, GaussianScene dst)
    {
        var sx = 0.65f * _hx; var sy = 0.65f * _hy;
        for (var v = 0; v < V; v++)
        {
            var n = face.Normal(v);
            var q = FromTo(Vector3.UnitZ, n);
            dst.Add(face.Position(v), q, new Vector3(sx, sy, 0.0006f), 0.95f, new Vector3(ColorR[v], ColorG[v], ColorB[v]));
        }
    }

    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d < -0.9999f) return new Quaternion(0f, 1f, 0f, 0f);
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }
}
