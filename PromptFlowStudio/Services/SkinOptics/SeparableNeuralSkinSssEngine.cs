using System.Numerics;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.SkinOptics;

public enum SkinRegion : byte { None = 0, Face, Nose, Lips, Ear, Finger }

/// <summary>
/// [PromptFlow Studio v17.17.0] 🧬 실시간 분리형 피부 SSS 확산 엔진(<see cref="Render"/> 무할당).
///   ▸ 텍스처 공간(UV 128² · 1 텍셀 = <see cref="TexelMm"/>) 절차 피부: 얼굴(타원체 + 코 + 입술) · 양 귀(얇은 판) · 손가락 끝 2(원통) — 위치 · 법선 · 두께 · 마스크.
///   ▸ 확산 프로파일 = d'Eon–Luebke 6-가우시안 합(분산 0.0064 … 7.41 mm² · RGB 가중) · 투광 반경 r → 분산 × r².
///   ▸ 분리형 컨볼루션(Jimenez 2015): 채널별 1D 커널(17 탭 · 이산 정규화 Σ = 1) 수평 → 수직 2 패스 · 마스크 정규화(Σk·E·m / Σk·m) 로 경계 어두워짐 제거 ·
///     8 텍셀 영패딩 행/열 → 연속 로드 · 텍셀 = SIMD 레인(<see cref="IHairLane{TV}"/> V256 → V128 → 스칼라 · 탭 오름차순 누적 → 비트 동일).
///   ▸ 투광(Transmittance): T_c(s) = Σᵢ wᵢc·exp(−s² / (vᵢ r²)) · 후면 조사 E_back = 광원 × max(0, 0.3 + (−N)·L) → 얇은 귀/코끝/손가락 끝 적색 투과.
///   ▸ 최종: 알베도(<see cref="MicroVascularHemodynamicsFlushingSynthesizer"/> 혈류/멜라닌) × (SSS 확산 + 투광) + 켈레멘/베크만 스펙큘러.
/// </summary>
public sealed class SeparableNeuralSkinSssEngine
{
    public const int W = 128, H = 128, R = 8, Taps = 2 * R + 1, Wp = W + 2 * R, Hp = H + 2 * R, N = W * H;
    public static readonly float[] ProfileVar = { 0.0064f, 0.0484f, 0.187f, 0.567f, 1.99f, 7.41f };
    public static readonly Vector3[] ProfileW =
    {
        new(0.233f, 0.455f, 0.649f), new(0.100f, 0.336f, 0.344f), new(0.118f, 0.198f, 0f),
        new(0.113f, 0.007f, 0.007f), new(0.358f, 0.004f, 0f), new(0.078f, 0f, 0f),
    };

    // 기하(N)
    public readonly float[] PX = new float[N], PY = new float[N], PZ = new float[N], NX = new float[N], NY = new float[N], NZ = new float[N], Thickness = new float[N], Mask = new float[N];
    public readonly SkinRegion[] RegionOf = new SkinRegion[N];
    // 알베도(혈류 합성기가 기록 · N)
    public readonly float[] AlbedoR = new float[N], AlbedoG = new float[N], AlbedoB = new float[N];
    // 조도 · 결과(N)
    public readonly float[] IrrR = new float[N], IrrG = new float[N], IrrB = new float[N];
    public readonly float[] SssR = new float[N], SssG = new float[N], SssB = new float[N], TrR = new float[N], TrG = new float[N], TrB = new float[N];
    public readonly float[] OutR = new float[N], OutG = new float[N], OutB = new float[N];
    // 패딩 버퍼(Hp × Wp)
    private readonly float[] _pr = new float[Hp * Wp], _pg = new float[Hp * Wp], _pb = new float[Hp * Wp], _pm = new float[Hp * Wp];
    private readonly float[] _hr = new float[Hp * Wp], _hg = new float[Hp * Wp], _hb = new float[Hp * Wp], _hmr = new float[Hp * Wp], _hmg = new float[Hp * Wp], _hmb = new float[Hp * Wp];
    private readonly float[] _vr = new float[Hp * Wp], _vg = new float[Hp * Wp], _vb = new float[Hp * Wp], _vmr = new float[Hp * Wp], _vmg = new float[Hp * Wp], _vmb = new float[Hp * Wp];
    private readonly float[] _k = new float[3 * Taps];
    private float _kRadius = -1f, _kTexel = -1f;

    public SeparableNeuralSkinSssEngine()
    {
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V256 : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V128 : HairSimdPath.Scalar;
        BuildGeometry();
        for (var i = 0; i < N; i++) { AlbedoR[i] = 0.8f; AlbedoG[i] = 0.55f; AlbedoB[i] = 0.45f; }
        BuildKernel();
    }

    public HairSimdPath Path { get; set; }
    /// <summary>투광 반경 배율(1 = 성인 피부).</summary>
    public float TranslucencyRadius { get; set; } = 1f;
    /// <summary>텍셀당 mm(UV 해상도 · 얼굴 폭 180 mm / 128).</summary>
    public float TexelMm { get; set; } = 180f / W;
    public float TransmitGain { get; set; } = 3f;
    public float SpecularGain { get; set; } = 0.35f;
    public float Roughness { get; set; } = 0.35f;
    public bool SssEnabled { get; set; } = true;
    public bool TransmitEnabled { get; set; } = true;
    public Vector3 LightDir { get; set; } = Vector3.Normalize(new Vector3(0.4f, 0.3f, 0.85f));
    public Vector3 LightColor { get; set; } = new(1.6f, 1.55f, 1.45f);
    public Vector3 ViewDir { get; set; } = Vector3.UnitZ;
    public float Ambient { get; set; } = 0.06f;
    public double LastConvUs { get; private set; }
    public double LastRenderUs { get; private set; }
    public float Kernel(int channel, int tap) => _k[channel * Taps + tap];
    public static int Index(int x, int y) => y * W + x;

    // ══════════════════════ 절차 기하 ══════════════════════

    private void BuildGeometry()
    {
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = Index(x, y);
                float mx = ((x + 0.5f) / W - 0.5f) * 180f, my = (0.5f - (y + 0.5f) / H) * 230f;
                float z, th; Vector3 n; SkinRegion reg;
                var face = (mx / 75f) * (mx / 75f) + (my / 105f) * (my / 105f);
                var earE = ((MathF.Abs(mx) - 80f) / 9f) * ((MathF.Abs(mx) - 80f) / 9f) + ((my - 5f) / 28f) * ((my - 5f) / 28f);
                float f1 = Finger(mx, my, -80f), f2 = Finger(mx, my, -63f);
                if (face < 1f)
                {
                    var q = MathF.Max(0f, 1f - (mx / 80f) * (mx / 80f) - (my / 112f) * (my / 112f));
                    var nose = 18f * MathF.Exp(-mx * mx / (2f * 9f * 9f) - (my + 12f) * (my + 12f) / (2f * 16f * 16f));
                    z = 95f * MathF.Sqrt(q) + nose;
                    // 법선: 높이장 기울기
                    var dzdx = -95f * (mx / (80f * 80f)) / MathF.Max(0.05f, MathF.Sqrt(q)) - nose * mx / (9f * 9f);
                    var dzdy = -95f * (my / (112f * 112f)) / MathF.Max(0.05f, MathF.Sqrt(q)) - nose * (my + 12f) / (16f * 16f);
                    n = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
                    var lips = MathF.Abs(mx) < 22f && MathF.Abs(my + 45f) < 6f;
                    th = 28f - 20f * MathF.Exp(-mx * mx / (2f * 8f * 8f) - (my + 14f) * (my + 14f) / (2f * 9f * 9f));
                    reg = nose > 9f ? SkinRegion.Nose : lips ? SkinRegion.Lips : SkinRegion.Face;
                    if (lips) th = 10f;
                }
                else if (earE < 1f)
                {
                    z = 12f;
                    n = Vector3.Normalize(new Vector3(MathF.Sign(mx) * 0.75f, 0f, 0.65f));
                    th = 2.2f + 2.5f * earE;
                    reg = SkinRegion.Ear;
                }
                else if (f1 > 0f || f2 > 0f)
                {
                    var fx = f1 > 0f ? mx + 80f : mx + 63f;
                    var ch = MathF.Max(f1, f2);
                    z = 30f + ch * 0.5f;
                    n = Vector3.Normalize(new Vector3(fx / 8f, 0f, MathF.Sqrt(MathF.Max(0.05f, 1f - fx * fx / 64f))));
                    th = ch;
                    reg = SkinRegion.Finger;
                }
                else { z = 0f; n = Vector3.UnitZ; th = 0f; reg = SkinRegion.None; }
                PX[i] = mx; PY[i] = my; PZ[i] = z; NX[i] = n.X; NY[i] = n.Y; NZ[i] = n.Z; Thickness[i] = th;
                RegionOf[i] = reg; Mask[i] = reg == SkinRegion.None ? 0f : 1f;
            }
    }

    /// <summary>손가락 끝 원통(반경 8 mm · 세로 · 둥근 끝) 현 두께(mm · 밖이면 0).</summary>
    private static float Finger(float mx, float my, float cx)
    {
        var dx = mx - cx;
        if (MathF.Abs(dx) >= 8f || my < -115f || my > -78f) return 0f;
        var top = -86f;
        if (my > top) { var dy = my - top; var r2 = 64f - dy * dy; if (dx * dx >= r2) return 0f; return 2f * MathF.Sqrt(r2 - dx * dx); }
        return 2f * MathF.Sqrt(64f - dx * dx);
    }

    // ══════════════════════ 커널 ══════════════════════

    /// <summary>채널별 1D 커널(분산 × r² · 텍셀 위치 샘플 · Σ = 1).</summary>
    public void BuildKernel()
    {
        _kRadius = TranslucencyRadius; _kTexel = TexelMm;
        var r2 = MathF.Max(1e-4f, TranslucencyRadius * TranslucencyRadius);
        for (var c = 0; c < 3; c++)
        {
            var sum = 0f;
            for (var t = 0; t < Taps; t++)
            {
                var x = (t - R) * TexelMm;
                var v = 0f;
                for (var g = 0; g < ProfileVar.Length; g++)
                {
                    var w = c == 0 ? ProfileW[g].X : c == 1 ? ProfileW[g].Y : ProfileW[g].Z;
                    if (w <= 0f) continue;
                    // 텍셀 폭 적분 근사: 분산 + 텍셀²/12(박스 필터)
                    var var = ProfileVar[g] * r2 + TexelMm * TexelMm / 12f;
                    v += w * MathF.Exp(-x * x / (2f * var)) / MathF.Sqrt(var);
                }
                _k[c * Taps + t] = v;
                sum += v;
            }
            for (var t = 0; t < Taps; t++) _k[c * Taps + t] /= sum;
        }
    }

    /// <summary>채널 커널 2차 모멘트(mm²) — 확산 폭 지표.</summary>
    public float KernelVarianceMm2(int c)
    {
        var s = 0f;
        for (var t = 0; t < Taps; t++) { var x = (t - R) * TexelMm; s += _k[c * Taps + t] * x * x; }
        return s;
    }

    // ══════════════════════ SIMD 컨볼루션 ══════════════════════

    private static void ConvK<TV, L>(float[] src, float[] dst, float[] k, int ko, int stride) where TV : struct where L : struct, IHairLane<TV>
    {
        for (var y = R; y < R + H; y++)
        {
            var row = y * Wp;
            for (var x = R; x < R + W; x += L.Width)
            {
                var i = row + x;
                var acc = L.S(0f);
                for (var t = 0; t < Taps; t++) acc = L.Add(acc, L.Mul(L.S(k[ko + t]), L.Load(src, i + (t - R) * stride)));
                L.Store(dst, i, acc);
            }
        }
    }

    private void Conv(float[] src, float[] dst, int channel, int stride)
    {
        switch (Path)
        {
            case HairSimdPath.V256 when Vector256.IsHardwareAccelerated: ConvK<Vector256<float>, V256Lane>(src, dst, _k, channel * Taps, stride); break;
            case HairSimdPath.V256 or HairSimdPath.V128 when Vector128.IsHardwareAccelerated: ConvK<Vector128<float>, V128Lane>(src, dst, _k, channel * Taps, stride); break;
            default: ConvK<float, ScalarLane>(src, dst, _k, channel * Taps, stride); break;
        }
    }

    /// <summary>입력(IrrR/G/B · Mask) → 마스크 정규화 분리형 확산 → SssR/G/B(무할당).</summary>
    public void Convolve()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_kRadius != TranslucencyRadius || _kTexel != TexelMm) BuildKernel();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                int i = Index(x, y), p = (y + R) * Wp + x + R;
                var m = Mask[i];
                _pr[p] = IrrR[i] * m; _pg[p] = IrrG[i] * m; _pb[p] = IrrB[i] * m; _pm[p] = m;
            }
        Conv(_pr, _hr, 0, 1); Conv(_pg, _hg, 1, 1); Conv(_pb, _hb, 2, 1);
        Conv(_pm, _hmr, 0, 1); Conv(_pm, _hmg, 1, 1); Conv(_pm, _hmb, 2, 1);
        Conv(_hr, _vr, 0, Wp); Conv(_hg, _vg, 1, Wp); Conv(_hb, _vb, 2, Wp);
        Conv(_hmr, _vmr, 0, Wp); Conv(_hmg, _vmg, 1, Wp); Conv(_hmb, _vmb, 2, Wp);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                int i = Index(x, y), p = (y + R) * Wp + x + R;
                if (Mask[i] <= 0f) { SssR[i] = SssG[i] = SssB[i] = 0f; continue; }
                SssR[i] = _vr[p] / MathF.Max(_vmr[p], 1e-6f); SssG[i] = _vg[p] / MathF.Max(_vmg[p], 1e-6f); SssB[i] = _vb[p] / MathF.Max(_vmb[p], 1e-6f);
            }
        LastConvUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>투광 프로파일 T(s) 채널 값(s = 두께 mm).</summary>
    public Vector3 Transmittance(float s)
    {
        var r2 = MathF.Max(1e-4f, TranslucencyRadius * TranslucencyRadius);
        var t = Vector3.Zero;
        for (var g = 0; g < ProfileVar.Length; g++) t += ProfileW[g] * MathF.Exp(-s * s / (ProfileVar[g] * r2));
        return t;
    }

    // ══════════════════════ 렌더 ══════════════════════

    /// <summary>조도 → 확산 → 투광 → 알베도 합성 + 스펙큘러(무할당).</summary>
    public void Render()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var l = Vector3.Normalize(LightDir); var lc = LightColor; var v = Vector3.Normalize(ViewDir);
        var hv = Vector3.Normalize(l + v);
        for (var i = 0; i < N; i++)
        {
            if (Mask[i] <= 0f) { IrrR[i] = IrrG[i] = IrrB[i] = 0f; continue; }
            var n = new Vector3(NX[i], NY[i], NZ[i]);
            var e = MathF.Max(0f, Vector3.Dot(n, l)) + Ambient;
            IrrR[i] = lc.X * e; IrrG[i] = lc.Y * e; IrrB[i] = lc.Z * e;
        }
        if (SssEnabled) Convolve();
        else { IrrR.AsSpan().CopyTo(SssR); IrrG.AsSpan().CopyTo(SssG); IrrB.AsSpan().CopyTo(SssB); }
        var m2 = Roughness * Roughness;
        for (var i = 0; i < N; i++)
        {
            if (Mask[i] <= 0f) { OutR[i] = 0.04f; OutG[i] = 0.05f; OutB[i] = 0.08f; TrR[i] = TrG[i] = TrB[i] = 0f; continue; }
            var n = new Vector3(NX[i], NY[i], NZ[i]);
            Vector3 tr = Vector3.Zero;
            if (TransmitEnabled)
            {
                var back = MathF.Max(0f, 0.3f + Vector3.Dot(-n, l));
                tr = Transmittance(Thickness[i]) * (back * TransmitGain) * lc;
            }
            TrR[i] = tr.X; TrG[i] = tr.Y; TrB[i] = tr.Z;
            // 베크만 스펙큘러(켈레멘 근사 · 피부 F0 0.028)
            var nh = MathF.Max(1e-4f, Vector3.Dot(n, hv));
            var nl = MathF.Max(0f, Vector3.Dot(n, l));
            var t2 = (1f - nh * nh) / (nh * nh);
            var d = MathF.Exp(-t2 / m2) / (MathF.PI * m2 * nh * nh * nh * nh);
            var f = 0.028f + 0.972f * MathF.Pow(1f - MathF.Max(0f, Vector3.Dot(hv, v)), 5f);
            var spec = SpecularGain * d * f * nl * 0.25f;
            OutR[i] = AlbedoR[i] * (SssR[i] + tr.X) + spec * lc.X;
            OutG[i] = AlbedoG[i] * (SssG[i] + tr.Y) + spec * lc.Y;
            OutB[i] = AlbedoB[i] * (SssB[i] + tr.Z) + spec * lc.Z;
        }
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>영역 평균 투광(RGB).</summary>
    public Vector3 RegionTransmit(SkinRegion r)
    {
        var s = Vector3.Zero; var n = 0;
        for (var i = 0; i < N; i++) if (RegionOf[i] == r) { s += new Vector3(TrR[i], TrG[i], TrB[i]); n++; }
        return n > 0 ? s / n : Vector3.Zero;
    }
}
