using System.Numerics;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SkinnedSplatAvatar;

/// <summary>
/// [PromptFlow Studio v17.57.0] 🛡 타원체 체적 보존 &amp; 스플랫 폭발 방어 가드(<see cref="Apply"/> 무할당 · Vector&lt;float&gt; 판정).
/// 스키닝 직후 스플랫마다 ρ = det(Σ')/det(Σ₀)(바인드 체적 비) 를 SIMD 로 계산해 0.5 미만(찌그러짐) · 2.5 초과(폭발)면
/// Σ' ← Σ'·ρ^(−1/3) 로 바인드 체적에 강제 복원하고(형태 · 방향 보존), 비유한(NaN/∞) 공분산은 바인드 Σ₀ 로 · 비유한 평균은 직전 값으로 복구한다.
/// 클램프 후 재검사로 방송 위반(가드 ON 이면 0)을 계측하고, 컬링용 MaxSigma = √trace(Σ') 를 갱신한다. OFF 면 판정 · 계측만 한다.
/// </summary>
public sealed class VolumetricDeterminantClampingGuard
{
    public const float MinRatio = 0.5f, MaxRatio = 2.5f;
    public const int MaxWorkers = 16, StatN = 8;
    private readonly long[] _w = new long[MaxWorkers * StatN];
    private readonly float[] _wMax = new float[MaxWorkers], _wMin = new float[MaxWorkers];
    private int _workers;

    public bool Enabled { get; set; } = true;
    public long Frames { get; private set; }
    public long Squashed { get; private set; }
    public long Exploded { get; private set; }
    public long NaNFixes { get; private set; }
    public long Clamps => Squashed + Exploded + NaNFixes;
    public long FrameClamps { get; private set; }
    /// <summary>클램프 후에도 [0.5, 2.5] 밖으로 방송된 스플랫(누적 · 가드 ON 이면 0).</summary>
    public long OnAirViolations { get; private set; }
    public long FrameOnAirViolations { get; private set; }
    public float FrameMaxRatio { get; private set; } = 1f;
    public float FrameMinRatio { get; private set; } = 1f;
    public float PeakRatio { get; private set; } = 1f;

    public void ResetMetrics() { Frames = Squashed = Exploded = NaNFixes = OnAirViolations = FrameClamps = FrameOnAirViolations = 0; PeakRatio = 1f; }

    public void BeginFrame(int workers)
    {
        _workers = Math.Min(workers, MaxWorkers);
        Array.Clear(_w);
        for (var i = 0; i < MaxWorkers; i++) { _wMax[i] = 0f; _wMin[i] = float.MaxValue; }
    }

    public void EndFrame()
    {
        long sq = 0, ex = 0, nf = 0, on = 0; float mx = 0f, mn = float.MaxValue;
        for (var w = 0; w < MaxWorkers; w++)
        {
            var o = w * StatN; sq += _w[o]; ex += _w[o + 1]; nf += _w[o + 2]; on += _w[o + 3];
            if (_wMax[w] > mx) mx = _wMax[w]; if (_wMin[w] < mn) mn = _wMin[w];
        }
        Frames++;
        Squashed += sq; Exploded += ex; NaNFixes += nf; OnAirViolations += on;
        FrameClamps = sq + ex + nf; FrameOnAirViolations = on;
        FrameMaxRatio = mx; FrameMinRatio = mn == float.MaxValue ? 1f : mn;
        if (mx > PeakRatio) PeakRatio = mx;
    }

    /// <summary>스플랫 [s, e)(바인드 인덱스 · 장면 오프셋 start) 판정 · 클램프(워커 w · 무할당).</summary>
    public void Apply(int w, GaussianSplatScene sc, int start, int s, int e, float[] det0, float[] r00, float[] r01, float[] r02, float[] r11, float[] r12, float[] r22, float[] sig0)
    {
        var ws = Math.Min(w, MaxWorkers - 1);
        long sq = 0, ex = 0, nf = 0, on = 0; float mx = _wMax[ws], mn = _wMin[ws];
        var k = s;
        if (Vector.IsHardwareAccelerated)
        {
            var vc = Vector<float>.Count;
            Vector<float> lo = new(MinRatio), hi = new(MaxRatio);
            for (; k <= e - vc; k += vc)
            {
                var o = start + k;
                var a = new Vector<float>(sc.C00, o); var b = new Vector<float>(sc.C01, o); var c = new Vector<float>(sc.C02, o);
                var d = new Vector<float>(sc.C11, o); var f = new Vector<float>(sc.C12, o); var g = new Vector<float>(sc.C22, o);
                var det = a * (d * g - f * f) - b * (b * g - f * c) + c * (b * f - d * c);
                var ratio = det / new Vector<float>(det0, k);
                // NaN 은 모든 비교가 거짓 → 정상 마스크에서 빠져 스칼라 경로로 간다
                var ok = Vector.GreaterThanOrEqual(ratio, lo) & Vector.LessThanOrEqual(ratio, hi);
                // 평균 유한성(x − x = 0 은 NaN/∞ 에서 거짓)
                var px = new Vector<float>(sc.X, o); var py = new Vector<float>(sc.Y, o); var pz = new Vector<float>(sc.Z, o);
                ok &= Vector.Equals(px - px, Vector<float>.Zero) & Vector.Equals(py - py, Vector<float>.Zero) & Vector.Equals(pz - pz, Vector<float>.Zero);
                if (ok == new Vector<int>(-1))
                {
                    Vector.SquareRoot(a + d + g).CopyTo(sc.MaxSigma, o);
                    for (var j = 0; j < vc; j++) { var r = ratio[j]; if (r > mx) mx = r; if (r < mn) mn = r; }
                    continue;
                }
                for (var j = 0; j < vc; j++) Fix(sc, start, k + j, det0, r00, r01, r02, r11, r12, r22, sig0, ref sq, ref ex, ref nf, ref on, ref mx, ref mn);
            }
        }
        for (; k < e; k++) Fix(sc, start, k, det0, r00, r01, r02, r11, r12, r22, sig0, ref sq, ref ex, ref nf, ref on, ref mx, ref mn);
        var oo = ws * StatN;
        _w[oo] += sq; _w[oo + 1] += ex; _w[oo + 2] += nf; _w[oo + 3] += on;
        _wMax[ws] = mx; _wMin[ws] = mn;
    }

    private void Fix(GaussianSplatScene sc, int start, int k, float[] det0, float[] r00, float[] r01, float[] r02, float[] r11, float[] r12, float[] r22, float[] sig0,
        ref long sq, ref long ex, ref long nf, ref long on, ref float mx, ref float mn)
    {
        var i = start + k;
        float a = sc.C00[i], b = sc.C01[i], c = sc.C02[i], d = sc.C11[i], f = sc.C12[i], g = sc.C22[i];
        var det = RealtimeDualQuaternionGaussianSkinningEngine.Det(a, b, c, d, f, g);
        var ratio = det / det0[k];
        var finite = float.IsFinite(ratio) && float.IsFinite(a + b + c + d + f + g);
        if (!float.IsFinite(sc.X[i]) || !float.IsFinite(sc.Y[i]) || !float.IsFinite(sc.Z[i]))
        {
            if (Enabled) { sc.X[i] = float.IsFinite(sc.X[i]) ? sc.X[i] : 0f; sc.Y[i] = float.IsFinite(sc.Y[i]) ? sc.Y[i] : 0f; sc.Z[i] = float.IsFinite(sc.Z[i]) ? sc.Z[i] : 0f; sc.Opacity[i] = 0f; }
            if (finite) nf++;
        }
        if (!finite)
        {
            nf++;
            if (Enabled) { sc.C00[i] = r00[k]; sc.C01[i] = r01[k]; sc.C02[i] = r02[k]; sc.C11[i] = r11[k]; sc.C12[i] = r12[k]; sc.C22[i] = r22[k]; ratio = 1f; }
            else { on++; sc.MaxSigma[i] = sig0[k]; return; }
        }
        else if (ratio < MinRatio || ratio > MaxRatio)
        {
            if (ratio < MinRatio) sq++; else ex++;
            if (Enabled && ratio > 1e-20f)
            {
                var sc3 = MathF.Pow(ratio, -1f / 3f);
                sc.C00[i] = a * sc3; sc.C01[i] = b * sc3; sc.C02[i] = c * sc3; sc.C11[i] = d * sc3; sc.C12[i] = f * sc3; sc.C22[i] = g * sc3;
                ratio *= sc3 * sc3 * sc3;
            }
            else if (Enabled) { sc.C00[i] = r00[k]; sc.C01[i] = r01[k]; sc.C02[i] = r02[k]; sc.C11[i] = r11[k]; sc.C12[i] = r12[k]; sc.C22[i] = r22[k]; ratio = 1f; }
        }
        if (!(ratio >= MinRatio && ratio <= MaxRatio)) on++;
        if (ratio > mx) mx = ratio; if (ratio < mn) mn = ratio;
        var tr = sc.C00[i] + sc.C11[i] + sc.C22[i];
        sc.MaxSigma[i] = tr > 0f && float.IsFinite(tr) ? MathF.Sqrt(tr) : sig0[k];
    }
}
