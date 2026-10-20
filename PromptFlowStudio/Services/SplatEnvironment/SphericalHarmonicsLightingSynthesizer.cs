using System.Numerics;
using PromptFlowStudio.Services.GenlockFrustum;
namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>
/// [PromptFlow Studio v17.55.0] 🌈 구면 조화(SH) 뷰 디펜던트 라이팅 &amp; LED 뷰포트 합성기(<see cref="Color"/> · <see cref="Sample"/> 무할당).
/// <b>평가</b>: 차수 3(16 기저 · 3DGS 실수 SH 규약) — color = 0.5 + C0·DC + Σₖ Yₖ(dir)·qₖ·scale/127 (dir = 카메라 → 스플랫). 차수 0~3 절단 · 정반사 게인으로
/// 카메라 이동에 따른 하이라이트 · 광택 변화를 재현한다(<see cref="ProjectLobe"/> = 피보나치 256 방향 수치 적분 투영).
/// <b>LED 합성</b>: 렌더 결과(사전 곱 RGB + 커버리지)를 이중 버퍼로 게시하고, v17.44 인너 프러스텀 · v17.46 세트 확장이 방향 광선을 게시 카메라에
/// 투영해 쌍선형 샘플링한다(<see cref="IDirectionalSceneSampler"/>).
/// </summary>
public sealed class SphericalHarmonicsLightingSynthesizer : IDirectionalSceneSampler
{
    public const int Coeffs = 16, W = RealtimeGaussianSplatRasterizerEngine.W, H = RealtimeGaussianSplatRasterizerEngine.H, LobeSamples = 256;
    public const float C0 = 0.28209479177387814f, C1 = 0.4886025119029199f;
    private const float C20 = 1.0925484305920792f, C21 = -1.0925484305920792f, C22 = 0.31539156525252005f, C23 = -1.0925484305920792f, C24 = 0.5462742152960396f;
    private const float C30 = -0.5900435899266435f, C31 = 2.890611442640554f, C32 = -0.4570457994644658f, C33 = 0.3731763325901154f, C34 = -0.4570457994644658f, C35 = 1.445305721320277f, C36 = -0.5900435899266435f;
    private static readonly float[] LobeBasis = BuildLobeBasis(out LobeDirs);
    private static readonly Vector3[] LobeDirs;

    private readonly float[] _y = new float[Coeffs];
    private readonly float[][] _pub = { new float[W * H * 4], new float[W * H * 4] };
    private readonly SplatCamera[] _pubCam = new SplatCamera[2];
    private int _front = -1;

    /// <summary>평가 차수(0 = 확산만 · 3 = 전체).</summary>
    public int Degree { get; set; } = 3;
    public float SpecularGain { get; set; } = 1f;
    public long Evaluations { get; private set; }
    public void AddEvaluations(long n) => Evaluations += n;
    public long Published { get; private set; }
    public long SampleCalls { get; private set; }
    public long SampleHits { get; private set; }
    public double LastPublishUs { get; private set; }

    private static float[] BuildLobeBasis(out Vector3[] dirs)
    {
        dirs = new Vector3[LobeSamples];
        var b = new float[LobeSamples * Coeffs];
        var ga = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var j = 0; j < LobeSamples; j++)
        {
            var z = 1f - (2f * j + 1f) / LobeSamples; var r = MathF.Sqrt(1f - z * z);
            dirs[j] = new Vector3(r * MathF.Cos(ga * j), z, r * MathF.Sin(ga * j));
            Basis(dirs[j], b.AsSpan(j * Coeffs, Coeffs));
        }
        return b;
    }

    /// <summary>실수 SH 기저 16(차수 0~3 · 3DGS 순서) — d 는 단위 벡터.</summary>
    public static void Basis(Vector3 d, Span<float> y)
    {
        float x = d.X, yy0 = d.Y, z = d.Z;
        float xx = x * x, yy = yy0 * yy0, zz = z * z, xy = x * yy0, yz = yy0 * z, xz = x * z;
        y[0] = C0;
        y[1] = -C1 * yy0; y[2] = C1 * z; y[3] = -C1 * x;
        y[4] = C20 * xy; y[5] = C21 * yz; y[6] = C22 * (2f * zz - xx - yy); y[7] = C23 * xz; y[8] = C24 * (xx - yy);
        y[9] = C30 * yy0 * (3f * xx - yy); y[10] = C31 * xy * z; y[11] = C32 * yy0 * (4f * zz - xx - yy);
        y[12] = C33 * z * (2f * zz - 3f * xx - 3f * yy); y[13] = C34 * x * (4f * zz - xx - yy); y[14] = C35 * z * (xx - yy); y[15] = C36 * x * (xx - 3f * yy);
    }

    /// <summary>로브 ks·max(0, d·axis)^p 를 차수 3 SH 로 투영(비핫패스 · 256 방향 적분).</summary>
    public static void ProjectLobe(Vector3 axis, float power, float ks, Span<float> coeff16)
    {
        coeff16[..Coeffs].Clear();
        if (ks <= 0f) return;
        axis = Vector3.Normalize(axis);
        const float w = 4f * MathF.PI / LobeSamples;
        for (var j = 0; j < LobeSamples; j++)
        {
            var c = Vector3.Dot(LobeDirs[j], axis);
            if (c <= 0f) continue;
            var f = ks * MathF.Pow(c, power) * w;
            var o = j * Coeffs;
            for (var k = 0; k < Coeffs; k++) coeff16[k] += f * LobeBasis[o + k];
        }
    }

    /// <summary>스플랫 i 의 시점 의존 색(무할당 · 단일 스레드 스크래치) — dir = 카메라 → 스플랫(정규화 불필요).</summary>
    public Vector3 Color(GaussianSplatScene s, int i, Vector3 dir) { Evaluations++; return Color(s, i, dir, _y); }

    /// <summary>병렬 워커용(스크래치 16 float 를 호출자가 소유 · 평가 수는 <see cref="AddEvaluations"/> 로 합산).</summary>
    public Vector3 Color(GaussianSplatScene s, int i, Vector3 dir, Span<float> basis16)
    {
        var r = 0.5f + C0 * s.Dr[i]; var g = 0.5f + C0 * s.Dg[i]; var b = 0.5f + C0 * s.Db[i];
        var sc = s.ShScale[i];
        if (Degree > 0 && sc > 0f)
        {
            var len = dir.Length();
            if (len > 1e-8f)
            {
                Basis(dir / len, basis16);
                var n = Degree >= 3 ? 16 : Degree == 2 ? 9 : 4;
                var o = i * GaussianSplatScene.ShRest;
                float ar = 0, ag = 0, ab = 0;
                var sh = s.Sh;
                for (var k = 1; k < n; k++)
                {
                    var yk = basis16[k]; var q = o + (k - 1) * 3;
                    ar += yk * sh[q]; ag += yk * sh[q + 1]; ab += yk * sh[q + 2];
                }
                var m = sc * SpecularGain / 127f;
                r += ar * m; g += ag * m; b += ab * m;
            }
        }
        return new Vector3(MathF.Max(r, 0f), MathF.Max(g, 0f), MathF.Max(b, 0f));
    }

    /// <summary>렌더 결과 게시(무할당 · 후면 버퍼 기록 후 교체).</summary>
    public void Publish(RealtimeGaussianSplatRasterizerEngine e, in SplatCamera cam)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var back = _front == 0 ? 1 : 0;
        var dst = _pub[back];
        ReadOnlySpan<float> r = e.ColorR, g = e.ColorG, b = e.ColorB, t = e.Transmittance;
        for (var p = 0; p < W * H; p++) { var o = p * 4; dst[o] = r[p]; dst[o + 1] = g[p]; dst[o + 2] = b[p]; dst[o + 3] = 1f - t[p]; }
        _pubCam[back] = cam;
        Volatile.Write(ref _front, back);
        Published++;
        LastPublishUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary><see cref="IDirectionalSceneSampler"/> — 게시 카메라 투영 · 쌍선형(무할당).</summary>
    public float Sample(Vector3 eye, Vector3 dir, out Vector3 rgb)
    {
        rgb = Vector3.Zero;
        SampleCalls++;
        var f = Volatile.Read(ref _front);
        if (f < 0) return 0f;
        ref readonly var cam = ref _pubCam[f];
        var z = Vector3.Dot(dir, cam.Forward);
        if (!(z > 1e-5f)) return 0f;
        var fy = H * 0.5f / cam.TanHalfV; var fx = W * 0.5f / cam.TanHalfH;
        var u = W * 0.5f + fx * Vector3.Dot(dir, cam.Right) / z - 0.5f;
        var v = H * 0.5f - fy * Vector3.Dot(dir, cam.Up) / z - 0.5f;
        if (!(u >= 0f && v >= 0f && u <= W - 1 && v <= H - 1)) return 0f;
        int x0 = Math.Min((int)u, W - 2), y0 = Math.Min((int)v, H - 2);
        float ax = u - x0, ay = v - y0;
        var buf = _pub[f];
        float cr = 0, cg = 0, cb = 0, cov = 0;
        for (var k = 0; k < 4; k++)
        {
            int dx = k & 1, dy = k >> 1;
            var wgt = (dx == 0 ? 1f - ax : ax) * (dy == 0 ? 1f - ay : ay);
            var o = ((y0 + dy) * W + x0 + dx) * 4;
            cr += buf[o] * wgt; cg += buf[o + 1] * wgt; cb += buf[o + 2] * wgt; cov += buf[o + 3] * wgt;
        }
        rgb = new Vector3(cr, cg, cb);
        if (cov > 0f) SampleHits++;
        return Math.Clamp(cov, 0f, 1f);
    }
}
