using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.FluidWetness;
using PromptFlowStudio.Services.SemanticMatting;
using PromptFlowStudio.Services.SkinOptics;

namespace PromptFlowStudio.Services.GaussianAvatar;

/// <summary>
/// [PromptFlow Studio v17.20.0] 🌈 차수-3 구면 조화 함수(SH) 신경망 리라이팅 합성기.
/// 가우시안마다 16 계수 × RGB(계수 우선 SoA · 48 × N)를 절차적으로 굽고(표면별 알베도 DC + 법선 방향 반사 로브 + 결정적 잡음),
/// 매 틱 시점 방향을 스키닝 회전의 역으로 정준 공간에 되돌려 SH 를 평가(Vector256 · 비트 동일)한다.
/// 확산 DC 는 v17.08 HDRI SH9 조도(8 프레임 무할당 링버퍼 평활)로 재조명하고, 시점 의존 대역은 반사광 게인 · 젖음 광택으로 증폭,
/// 피부 가우시안에는 v17.17 SSS 투광 프로파일 T(두께) 역광 투과를, 전 표면에 v17.19 젖음 알베도 암전을 합성한다.
/// </summary>
public sealed class SphericalHarmonicsNeuralRelightingSynthesizer
{
    public const int Coeffs = 16, Ring = 8;
    public const float C0 = 0.28209479177387814f, C1 = 0.4886025119029199f;
    public const float C20 = 1.0925484305920792f, C21 = -1.0925484305920792f, C22 = 0.31539156525252005f, C23 = -1.0925484305920792f, C24 = 0.5462742152960396f;
    public const float C30 = -0.5900435899266435f, C31 = 2.890611442640554f, C32 = -0.4570457994644658f, C33 = 0.3731763325901154f, C34 = -0.4570457994644658f, C35 = 1.445305721320277f, C36 = -0.5900435899266435f;

    public readonly int N;
    /// <summary>SH 계수 — 인덱스 (k·3 + c)·N + i.</summary>
    public readonly float[] Sh;
    public readonly float[] R, G, B;                    // 재조명 선형 RGB
    public readonly float[] VR, VG, VB;                 // 시점 의존 대역(k ≥ 1) 평가값
    public readonly float[] Wetness, TrR, TrG, TrB;     // 젖음 0..1 · SSS 투광(피부 외 0)
    private readonly int[] _texel;                      // 근육 가우시안 → 피부 UV 텍셀(−1 = 없음)
    private readonly float[] _dx, _dy, _dz;
    private readonly Vector3[] _ringSh = new Vector3[Ring * 9], _ringDir = new Vector3[Ring], _ringCol = new Vector3[Ring];
    private readonly Vector3[] _sh9 = new Vector3[9];
    private int _ringHead, _ringCount;
    private long _envVersion = -1;
    private readonly DeformableGaussianSkinningBindingEngine _e;

    public SphericalHarmonicsNeuralRelightingSynthesizer(DeformableGaussianSkinningBindingEngine engine, SeparableNeuralSkinSssEngine skin, uint seed = 0x2545F491u) : this(engine, skin, null, seed) { }

    public SphericalHarmonicsNeuralRelightingSynthesizer(DeformableGaussianSkinningBindingEngine engine, SeparableNeuralSkinSssEngine skin, byte[]? garmentLayerOf, uint seed = 0x2545F491u)
    {
        _e = engine;
        N = engine.N;
        Sh = new float[Coeffs * 3 * N];
        R = new float[N]; G = new float[N]; B = new float[N]; VR = new float[N]; VG = new float[N]; VB = new float[N];
        Wetness = new float[N]; TrR = new float[N]; TrG = new float[N]; TrB = new float[N];
        _texel = new int[N]; _dx = new float[N]; _dy = new float[N]; _dz = new float[N];
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? GaussianSimdPath.V256 : GaussianSimdPath.Scalar;
        Bake(skin, garmentLayerOf, seed);
    }

    public GaussianSimdPath Path { get; set; }
    public float SpecularGain { get; set; } = 1f;
    public float DiffuseGain { get; set; } = 1f;
    public float SssGain { get; set; } = 1f;
    public bool SssEnabled { get; set; } = true;
    public bool WetEnabled { get; set; } = true;
    public bool Relight { get; set; } = true;
    public double LastSampleUs { get; private set; }
    public double LastShUs { get; private set; }
    public double LastShadeUs { get; private set; }
    public float MeanWetness { get; private set; }
    public float MeanSpecular { get; private set; }
    public float MeanTransmit { get; private set; }
    public float MeanLuma { get; private set; }
    public int RingCount => _ringCount;
    public Vector3 LightDir { get; private set; } = Vector3.Normalize(new Vector3(0.4f, 0.5f, 0.75f));
    public Vector3 LightColor { get; private set; } = Vector3.One;
    public float Coefficient(int i, int k, int c) => Sh[(k * 3 + c) * N + i];

    private static uint Next(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
    private static float Sn(ref uint s) => (Next(ref s) >> 8) * (2f / 16777216f) - 1f;

    // ══════════════════════ 절차적 베이크 ══════════════════════

    private void Bake(SeparableNeuralSkinSssEngine skin, byte[]? layerOf, uint seed)
    {
        var s = seed == 0 ? 1u : seed;
        const int segs = NeuralAnatomicalMuscleFasciaEngine.Segs, rings = NeuralAnatomicalMuscleFasciaEngine.Rings;
        for (var i = 0; i < N; i++)
        {
            float ar, ag, ab, gloss;
            _texel[i] = -1;
            switch (_e.SurfaceOf[i])
            {
                case GaussianSurface.Muscle:
                {
                    var v = _e.HostPoint[i];
                    int t = v / NeuralAnatomicalMuscleFasciaEngine.PerTube, r = v / segs % rings, sg = v % segs;
                    var tx = (sg * SeparableNeuralSkinSssEngine.W / segs + t * 13) % SeparableNeuralSkinSssEngine.W;
                    var ty = r * SeparableNeuralSkinSssEngine.H / rings;
                    var tex = SeparableNeuralSkinSssEngine.Index(tx, ty);
                    _texel[i] = tex;
                    if (skin.Mask[tex] > 0f) { ar = skin.AlbedoR[tex]; ag = skin.AlbedoG[tex]; ab = skin.AlbedoB[tex]; }
                    else { ar = 0.78f; ag = 0.57f; ab = 0.47f; }
                    var j = 0.04f * Sn(ref s);
                    ar += j; ag += j * 0.8f; ab += j * 0.7f;
                    gloss = 0.08f;
                    var thick = (t == 0 ? 9f : 5f) * (0.7f + 0.6f * MathF.Abs(MathF.Sin(MathF.PI * r / rings)));   // 몸통 두꺼움 · 관절 끝 얇음(mm)
                    var tr = skin.Transmittance(thick);
                    TrR[i] = tr.X; TrG[i] = tr.Y; TrB[i] = tr.Z;
                    break;
                }
                case GaussianSurface.Garment:
                {
                    var j = 0.05f * Sn(ref s);
                    var hp = _e.HostPoint[i];
                    var h = layerOf is not null && hp < layerOf.Length ? layerOf[hp] : hp % 3;   // 0 이너 · 1 셔츠 · 2 코트
                    if (h == 0) { ar = 0.82f + j; ag = 0.8f + j; ab = 0.76f + j; } else if (h == 1) { ar = 0.22f + j; ag = 0.3f + j; ab = 0.52f + j; } else { ar = 0.36f + j; ag = 0.27f + j; ab = 0.19f + j; }
                    gloss = 0.03f;
                    break;
                }
                default:
                {
                    var j = 0.02f * Sn(ref s);
                    ar = 0.13f + j; ag = 0.085f + j; ab = 0.055f + j;
                    gloss = 0.16f;
                    break;
                }
            }
            Set(i, 0, (ar - 0.5f) / C0, (ag - 0.5f) / C0, (ab - 0.5f) / C0);
            // 시점 방향 d(카메라 → 가우시안) 가 국소 −z(법선 정면)일 때 양의 반사 로브
            for (var k = 1; k < Coeffs; k++)
            {
                var lobe = k == 2 ? -gloss : k == 6 ? 0.6f * gloss : k == 12 ? -0.3f * gloss : 0f;
                var n = 0.15f * gloss;
                Set(i, k, lobe + n * Sn(ref s), lobe + n * Sn(ref s), lobe + n * Sn(ref s));
            }
        }
    }

    private void Set(int i, int k, float r, float g, float b)
    {
        Sh[(k * 3) * N + i] = r; Sh[(k * 3 + 1) * N + i] = g; Sh[(k * 3 + 2) * N + i] = b;
    }

    // ══════════════════════ 환경 · 표면 상태 ══════════════════════

    /// <summary>HDRI SH9 · 지배 광원을 링버퍼에 적재(무할당) → 최근 <see cref="Ring"/> 프레임 평균으로 프리셋 전환 깜빡임 억제.</summary>
    public void PushEnvironment(HdriEnvironment env)
    {
        var h = _ringHead;
        for (var k = 0; k < 9; k++) _ringSh[h * 9 + k] = env.Sh(k);
        _ringDir[h] = env.DominantDir; _ringCol[h] = env.DominantColor;
        _ringHead = (h + 1) % Ring;
        if (_ringCount < Ring) _ringCount++;
        _envVersion = env.Version;
        for (var k = 0; k < 9; k++)
        {
            var acc = Vector3.Zero;
            for (var r = 0; r < _ringCount; r++) acc += _ringSh[r * 9 + k];
            _sh9[k] = acc / _ringCount;
        }
        Vector3 d = Vector3.Zero, c = Vector3.Zero;
        for (var r = 0; r < _ringCount; r++) { d += _ringDir[r]; c += _ringCol[r]; }
        var dl = d.Length();
        LightDir = dl > 1e-6f ? d / dl : Vector3.UnitZ;
        LightColor = c / _ringCount;
    }

    public long EnvironmentVersion => _envVersion;

    /// <summary>v17.19 젖음 상태 샘플링(피부 텍셀 피막 · 직물 포화도 · 모발 평균 수분).</summary>
    public void SampleSurface(MultiphaseSurfaceWetnessCapillaryEngine wet, SeparableNeuralSkinSssEngine skin, float hairWet)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var skinMean = Math.Clamp(wet.SkinMeanUm / 20f, 0f, 1f);
        var gp = wet.GarmentCount;
        double sum = 0;
        for (var i = 0; i < N; i++)
        {
            float w;
            switch (_e.SurfaceOf[i])
            {
                case GaussianSurface.Muscle: { var tex = _texel[i]; w = tex >= 0 && skin.Mask[tex] > 0f ? wet.SkinWetness(tex) : skinMean; break; }
                case GaussianSurface.Garment: { var p = _e.HostPoint[i]; w = p < gp ? wet.Saturation(p) : 0f; break; }
                default: w = hairWet; break;
            }
            w = Math.Clamp(w, 0f, 1f);
            Wetness[i] = w; sum += w;
        }
        MeanWetness = (float)(sum / N);
        LastSampleUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ══════════════════════ 평가 · 합성 ══════════════════════

    /// <summary>시점 방향 정준화 → SH 차수 3 평가(VR/VG/VB · DC 제외) → 재조명 합성(R/G/B). 무할당.</summary>
    public void Update(Vector3 cam)
    {
        var f = System.Diagnostics.Stopwatch.Frequency / 1e6;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var e = _e;
        for (var i = 0; i < N; i++)
        {
            float vx = e.X[i] - cam.X, vy = e.Y[i] - cam.Y, vz = e.Z[i] - cam.Z;
            var inv = 1f / MathF.Sqrt(MathF.Max(1e-12f, vx * vx + vy * vy + vz * vz));
            vx *= inv; vy *= inv; vz *= inv;
            // 스키닝 회전 r 의 역(r*)으로 회전: v' = v + 2u×(u×v + w v) · u = −r.xyz
            float ux = -e.Sx[i], uy = -e.Sy[i], uz = -e.Sz[i], w = e.Sw[i];
            var cx = uy * vz - uz * vy + w * vx;
            var cy = uz * vx - ux * vz + w * vy;
            var cz = ux * vy - uy * vx + w * vz;
            _dx[i] = vx + 2f * (uy * cz - uz * cy);
            _dy[i] = vy + 2f * (uz * cx - ux * cz);
            _dz[i] = vz + 2f * (ux * cy - uy * cx);
        }
        if (Path == GaussianSimdPath.V256 && BitConverter.IsLittleEndian) EvalV256(); else EvalScalar(0);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Shade(cam);
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastShUs = (t1 - t0) / f; LastShadeUs = (t2 - t1) / f;
    }

    private void Shade(Vector3 cam)
    {
        var e = _e;
        var sh9 = _sh9.AsSpan();
        var useEnv = Relight && _ringCount > 0;
        var ld = LightDir; var lc = LightColor;
        var lum = MathF.Max(0.05f, 0.2126f * lc.X + 0.7152f * lc.Y + 0.0722f * lc.Z);
        float sR = lc.X / lum, sG = lc.Y / lum, sB = lc.Z / lum;
        int dcR = 0, dcG = N, dcB = 2 * N;
        double specSum = 0, trSum = 0, lumaSum = 0;
        for (var i = 0; i < N; i++)
        {
            float qx = e.Qx[i], qy = e.Qy[i], qz = e.Qz[i], qw = e.Qw[i];
            var n = new Vector3(2f * (qx * qz + qw * qy), 2f * (qy * qz - qw * qx), 1f - 2f * (qx * qx + qy * qy));
            var toCam = new Vector3(cam.X - e.X[i], cam.Y - e.Y[i], cam.Z - e.Z[i]);
            if (Vector3.Dot(n, toCam) < 0f) n = -n;
            var wet = WetEnabled ? Wetness[i] : 0f;
            var ar = MultiphaseSurfaceWetnessCapillaryEngine.WetAlbedo(Math.Clamp(C0 * Sh[dcR + i] + 0.5f, 0f, 1f), wet);
            var ag = MultiphaseSurfaceWetnessCapillaryEngine.WetAlbedo(Math.Clamp(C0 * Sh[dcG + i] + 0.5f, 0f, 1f), wet);
            var ab = MultiphaseSurfaceWetnessCapillaryEngine.WetAlbedo(Math.Clamp(C0 * Sh[dcB + i] + 0.5f, 0f, 1f), wet);
            Vector3 irr;
            if (useEnv) irr = HdriEnvironment.Irradiance(sh9, n) * DiffuseGain;
            else irr = new Vector3(DiffuseGain * (0.25f + 0.75f * MathF.Max(0f, Vector3.Dot(n, ld))));
            var gain = SpecularGain * (1f + 1.5f * wet);
            float spR = MathF.Max(0f, VR[i]) * gain * sR, spG = MathF.Max(0f, VG[i]) * gain * sG, spB = MathF.Max(0f, VB[i]) * gain * sB;
            float tR = 0f, tG = 0f, tB = 0f;
            if (SssEnabled && TrR[i] > 0f)
            {
                var back = MathF.Max(0f, -Vector3.Dot(n, ld)) * SssGain;
                tR = TrR[i] * back * lc.X * ar; tG = TrG[i] * back * lc.Y * ag; tB = TrB[i] * back * lc.Z * ab;
                trSum += tR + tG + tB;
            }
            var r = ar * irr.X + spR + tR; var g = ag * irr.Y + spG + tG; var b = ab * irr.Z + spB + tB;
            R[i] = r; G[i] = g; B[i] = b;
            specSum += spR + spG + spB;
            lumaSum += 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
        MeanSpecular = (float)(specSum / (3.0 * N)); MeanTransmit = (float)(trSum / (3.0 * N)); MeanLuma = (float)(lumaSum / N);
    }

    /// <summary>스칼라 SH 평가(k = 1..15 · 3DGS 부호 규약).</summary>
    private void EvalScalar(int from)
    {
        var sh = Sh; var n = N;
        for (var i = from; i < n; i++)
        {
            float x = _dx[i], y = _dy[i], z = _dz[i];
            float xx = x * x, yy = y * y, zz = z * z, xy = x * y, yz = y * z, xz = x * z;
            var b1 = -C1 * y; var b2 = C1 * z; var b3 = -C1 * x;
            var b4 = C20 * xy; var b5 = C21 * yz; var b6 = C22 * (2f * zz - xx - yy); var b7 = C23 * xz; var b8 = C24 * (xx - yy);
            var b9 = C30 * y * (3f * xx - yy); var b10 = C31 * xy * z; var b11 = C32 * y * (4f * zz - xx - yy); var b12 = C33 * z * (2f * zz - 3f * xx - 3f * yy);
            var b13 = C34 * x * (4f * zz - xx - yy); var b14 = C35 * z * (xx - yy); var b15 = C36 * x * (xx - 3f * yy);
            for (var c = 0; c < 3; c++)
            {
                var o = c * n + i;
                var s = b1 * sh[3 * n + o] + b2 * sh[6 * n + o] + b3 * sh[9 * n + o]
                      + b4 * sh[12 * n + o] + b5 * sh[15 * n + o] + b6 * sh[18 * n + o] + b7 * sh[21 * n + o] + b8 * sh[24 * n + o]
                      + b9 * sh[27 * n + o] + b10 * sh[30 * n + o] + b11 * sh[33 * n + o] + b12 * sh[36 * n + o] + b13 * sh[39 * n + o] + b14 * sh[42 * n + o] + b15 * sh[45 * n + o];
                if (c == 0) VR[i] = s; else if (c == 1) VG[i] = s; else VB[i] = s;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> L(float[] a, int i) => Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);

    /// <summary>Vector256 SH 평가 — <see cref="EvalScalar"/> 와 연산 순서 동일(비트 동일).</summary>
    private void EvalV256()
    {
        var sh = Sh; var n = N;
        var two = Vector256.Create(2f); var three = Vector256.Create(3f); var four = Vector256.Create(4f);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Vector256<float> x = L(_dx, i), y = L(_dy, i), z = L(_dz, i);
            Vector256<float> xx = x * x, yy = y * y, zz = z * z, xy = x * y, yz = y * z, xz = x * z;
            var b1 = Vector256.Create(-C1) * y; var b2 = Vector256.Create(C1) * z; var b3 = Vector256.Create(-C1) * x;
            var b4 = Vector256.Create(C20) * xy; var b5 = Vector256.Create(C21) * yz; var b6 = Vector256.Create(C22) * (two * zz - xx - yy); var b7 = Vector256.Create(C23) * xz; var b8 = Vector256.Create(C24) * (xx - yy);
            var b9 = Vector256.Create(C30) * y * (three * xx - yy); var b10 = Vector256.Create(C31) * xy * z; var b11 = Vector256.Create(C32) * y * (four * zz - xx - yy); var b12 = Vector256.Create(C33) * z * (two * zz - three * xx - three * yy);
            var b13 = Vector256.Create(C34) * x * (four * zz - xx - yy); var b14 = Vector256.Create(C35) * z * (xx - yy); var b15 = Vector256.Create(C36) * x * (xx - three * yy);
            for (var c = 0; c < 3; c++)
            {
                var o = c * n + i;
                var s = b1 * L(sh, 3 * n + o) + b2 * L(sh, 6 * n + o) + b3 * L(sh, 9 * n + o)
                      + b4 * L(sh, 12 * n + o) + b5 * L(sh, 15 * n + o) + b6 * L(sh, 18 * n + o) + b7 * L(sh, 21 * n + o) + b8 * L(sh, 24 * n + o)
                      + b9 * L(sh, 27 * n + o) + b10 * L(sh, 30 * n + o) + b11 * L(sh, 33 * n + o) + b12 * L(sh, 36 * n + o) + b13 * L(sh, 39 * n + o) + b14 * L(sh, 42 * n + o) + b15 * L(sh, 45 * n + o);
                var dst = c == 0 ? VR : c == 1 ? VG : VB;
                s.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(dst), (nuint)i);
            }
        }
        if (i < n) EvalScalar(i);
    }

    /// <summary>단일 방향 SH 평가(검증용 · DC 포함 + 0.5).</summary>
    public Vector3 Evaluate(int i, Vector3 dir)
    {
        float x = dir.X, y = dir.Y, z = dir.Z;
        float xx = x * x, yy = y * y, zz = z * z, xy = x * y, yz = y * z, xz = x * z;
        Span<float> b = stackalloc float[Coeffs];
        b[0] = C0; b[1] = -C1 * y; b[2] = C1 * z; b[3] = -C1 * x;
        b[4] = C20 * xy; b[5] = C21 * yz; b[6] = C22 * (2f * zz - xx - yy); b[7] = C23 * xz; b[8] = C24 * (xx - yy);
        b[9] = C30 * y * (3f * xx - yy); b[10] = C31 * xy * z; b[11] = C32 * y * (4f * zz - xx - yy); b[12] = C33 * z * (2f * zz - 3f * xx - 3f * yy);
        b[13] = C34 * x * (4f * zz - xx - yy); b[14] = C35 * z * (xx - yy); b[15] = C36 * x * (xx - 3f * yy);
        var r = new Vector3(0.5f);
        for (var k = 0; k < Coeffs; k++) r += b[k] * new Vector3(Coefficient(i, k, 0), Coefficient(i, k, 1), Coefficient(i, k, 2));
        return r;
    }
}
