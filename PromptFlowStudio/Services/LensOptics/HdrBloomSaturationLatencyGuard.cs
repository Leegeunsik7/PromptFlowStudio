using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.LensOptics;

/// <summary>
/// [PromptFlow Studio v17.43.0] 🛡 HDR 블룸 광량 폭주 &amp; 포스트 연산 레이턴시 가드(<see cref="Process"/>/<see cref="ToneMap"/> 무할당).
///   ▸ 블룸: 밝은 영역 추출(x − 임계) → 2×2 박스 다운샘플 피라미드(최대 5단 · 120×50 → 7×3) → 쌍선형 업샘플 누적 → 합성.
///   ▸ 포화 방어: 평면 HDR NaN/±Inf/음수 → 0(Vector&lt;T&gt; 일괄) · 블룸 버퍼 ACEScc 로그 소프트 니
///     a = (log₂x + 9.72)/17.52 · a &gt; 니(0.62) → 니 + Δ/(1 + Δ/(천장 0.80 − 니)) 로 천장 점근(Vector.GreaterThanAny 블록만 스칼라).
///   ▸ 레이턴시: 포스트(왜곡 + 블룸 + 톤매핑) 시간 &gt; 예산(4.0 ms) → 피라미드 단계 −1(쿨다운 10 프레임 · 0 = 블룸 우회) · EWMA &lt; 예산/2 가 60 프레임 지속 → +1.
///   ▸ 표시: ACES 필믹(Narkowicz) → 감마 2 → RGB8 · 화이트아웃 비율(3채널 ≥ 254) 측정.
/// </summary>
public sealed class HdrBloomSaturationLatencyGuard
{
    public const int MaxLevels = 5, MinLevels = 0;
    public const float DefaultBudgetMs = 4f, Threshold = 1f, Intensity = 0.08f, KneeCc = 0.62f, CeilingCc = 0.80f;
    private const int W = RealtimeAnamorphicLensOpticsEngine.W, H = RealtimeAnamorphicLensOpticsEngine.H;

    public static readonly float KneeLinear = FromAcesCc(KneeCc), CeilingLinear = FromAcesCc(CeilingCc);
    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private readonly float[][] _lv = new float[MaxLevels][];
    private readonly int[] _lw = new int[MaxLevels], _lh = new int[MaxLevels];
    private readonly int[] _cx = new int[W];
    private readonly float[] _cf = new float[W];
    private int _cool, _under;

    public HdrBloomSaturationLatencyGuard()
    {
        int w = W, h = H;
        for (var k = 0; k < MaxLevels; k++)
        {
            w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1);
            _lw[k] = w; _lh[k] = h; _lv[k] = new float[3 * w * h];
        }
        for (var x = 0; x < W; x++)
        {
            var sx = Math.Clamp((x + 0.5f) * 0.5f - 0.5f, 0f, _lw[0] - 1f);
            _cx[x] = Math.Min((int)sx, _lw[0] - 2); _cf[x] = sx - _cx[x];
        }
    }

    public bool Enabled { get; set; } = true;
    public float BudgetMs { get; set; } = DefaultBudgetMs;
    public int Levels { get; private set; } = MaxLevels;
    public double LastPostMs { get; private set; }
    public double EwmaPostMs { get; private set; }
    public double MaxPostMs { get; private set; }
    public long OverBudgetFrames { get; private set; }
    public long Downshifts { get; private set; }
    public long Upshifts { get; private set; }
    public long Repairs { get; private set; }
    public long Compressions { get; private set; }
    public float BloomPeak { get; private set; }
    public float WhiteoutRatio { get; private set; }
    public float MaxWhiteout { get; private set; }
    public double LastBloomUs { get; private set; }
    public int LevelWidth(int k) => _lw[k];
    public int LevelHeight(int k) => _lh[k];
    public ReadOnlySpan<float> Level(int k) => _lv[k];

    public void ResetMetrics()
    {
        OverBudgetFrames = Downshifts = Upshifts = Repairs = Compressions = 0;
        MaxPostMs = 0; MaxWhiteout = 0f;
    }

    public void ResetLevels() { Levels = MaxLevels; _cool = _under = 0; }

    public static float AcesCc(float x) => x < 3.0517578e-5f ? (MathF.Log2(1.5258789e-5f + 0.5f * MathF.Max(x, 0f)) + 9.72f) / 17.52f : (MathF.Log2(x) + 9.72f) / 17.52f;
    public static float FromAcesCc(float a) => MathF.Pow(2f, a * 17.52f - 9.72f);

    /// <summary>ACEScc 소프트 니 — 니 이하 항등 · 이상 천장 점근(단조 · 비유한 → 천장).</summary>
    public static float Compress(float x)
    {
        if (!float.IsFinite(x)) return float.IsNaN(x) ? 0f : x > 0f ? CeilingLinear : 0f;
        if (x <= KneeLinear) return x;
        var d = AcesCc(x) - KneeCc;
        return FromAcesCc(KneeCc + d / (1f + d / (CeilingCc - KneeCc)));
    }

    // ══════════════════════ 블룸(무할당) ══════════════════════

    public void Process(float[] r, float[] g, float[] b)
    {
        var t0 = Stopwatch.GetTimestamp();
        var on = Enabled;
        if (on) Repairs += Sanitize(r) + Sanitize(g) + Sanitize(b);
        var levels = on ? Levels : MaxLevels;
        if (levels == 0) { BloomPeak = 0f; LastBloomUs = (Stopwatch.GetTimestamp() - t0) * TickUs; return; } // 최후 단계: 블룸 우회(정화만)

        // 1) 밝은 영역 → 레벨 0(½)
        var l0 = _lv[0]; int w0 = _lw[0], h0 = _lh[0], n0 = w0 * h0;
        BrightDown(r, l0, 0, w0, h0);
        BrightDown(g, l0, n0, w0, h0);
        BrightDown(b, l0, 2 * n0, w0, h0);
        if (on) Compressions += CompressSpan(l0);
        // 2) 다운샘플 피라미드
        for (var k = 1; k < levels; k++) Down(_lv[k - 1], _lw[k - 1], _lh[k - 1], _lv[k], _lw[k], _lh[k]);
        // 3) 업샘플 누적
        for (var k = levels - 1; k >= 1; k--) Up(_lv[k], _lw[k], _lh[k], _lv[k - 1], _lw[k - 1], _lh[k - 1]);
        if (on) Compressions += CompressSpan(l0);
        // 4) 합성
        var peak = Composite(l0, 0, r, w0, h0, _cx, _cf);
        peak = Peak(peak, Composite(l0, n0, g, w0, h0, _cx, _cf));
        peak = Peak(peak, Composite(l0, 2 * n0, b, w0, h0, _cx, _cf));
        BloomPeak = peak;
        LastBloomUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    private static float Peak(float a, float b) => float.IsNaN(a) || float.IsNaN(b) ? float.NaN : MathF.Max(a, b);

    /// <summary>밝은 영역(x − 임계 · 미만 0 · NaN 전파 = 가드 OFF 관측) 2×2 평균 → 레벨 0 채널 평면.</summary>
    private static void BrightDown(float[] src, float[] dst, int off, int w0, int h0)
    {
        for (var y = 0; y < h0; y++)
        {
            int s = 2 * y * W, o = off + y * w0;
            for (var x = 0; x < w0; x++, s += 2, o++)
            {
                float a = src[s] - Threshold, b = src[s + 1] - Threshold, c = src[s + W] - Threshold, d = src[s + W + 1] - Threshold;
                if (a < 0f) a = 0f;
                if (b < 0f) b = 0f;
                if (c < 0f) c = 0f;
                if (d < 0f) d = 0f;
                dst[o] = 0.25f * (a + b + c + d);
            }
        }
    }

    /// <summary>레벨 0 쌍선형 ×2 업샘플 합성(가장자리 클램프) — 반환 = 채널 블룸 최대(NaN 전파).</summary>
    private static float Composite(float[] l0, int off, float[] dst, int w0, int h0, int[] cx, float[] cf)
    {
        var peak = 0f; var nan = false;
        for (var y = 0; y < H; y++)
        {
            var sy = (y + 0.5f) * 0.5f - 0.5f;
            if (sy < 0f) sy = 0f; else if (sy > h0 - 1f) sy = h0 - 1f;
            var iy = (int)sy; if (iy > h0 - 2) iy = h0 - 2;
            var fy = sy - iy;
            var row = off + iy * w0; var o = y * W;
            for (var x = 0; x < W; x++, o++)
            {
                var fx = cf[x];
                var j = row + cx[x];
                var t = l0[j] + (l0[j + 1] - l0[j]) * fx;
                var u = l0[j + w0] + (l0[j + w0 + 1] - l0[j + w0]) * fx;
                var v = t + (u - t) * fy;
                if (v > peak) peak = v; else if (float.IsNaN(v)) nan = true;
                dst[o] += Intensity * v;
            }
        }
        return nan ? float.NaN : peak;
    }

    private static void Down(float[] src, int sw, int sh, float[] dst, int dw, int dh)
    {
        int sn = sw * sh, dn = dw * dh;
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < dh; y++)
        {
            int y0 = Math.Min(2 * y, sh - 1), y1 = Math.Min(2 * y + 1, sh - 1);
            for (var x = 0; x < dw; x++)
            {
                int x0 = Math.Min(2 * x, sw - 1), x1 = Math.Min(2 * x + 1, sw - 1);
                dst[c * dn + y * dw + x] = 0.25f * (src[c * sn + y0 * sw + x0] + src[c * sn + y0 * sw + x1] + src[c * sn + y1 * sw + x0] + src[c * sn + y1 * sw + x1]);
            }
        }
    }

    private static void Up(float[] src, int sw, int sh, float[] dst, int dw, int dh)
    {
        int sn = sw * sh, dn = dw * dh;
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Clamp((y + 0.5f) * sh / dh - 0.5f, 0f, MathF.Max(sh - 1.001f, 0f));
            int iy = (int)sy; var fy = sy - iy; var iy1 = Math.Min(iy + 1, sh - 1);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Clamp((x + 0.5f) * sw / dw - 0.5f, 0f, MathF.Max(sw - 1.001f, 0f));
                int ix = (int)sx; var fx = sx - ix; var ix1 = Math.Min(ix + 1, sw - 1);
                var o = c * sn;
                var t = src[o + iy * sw + ix] + (src[o + iy * sw + ix1] - src[o + iy * sw + ix]) * fx;
                var u = src[o + iy1 * sw + ix] + (src[o + iy1 * sw + ix1] - src[o + iy1 * sw + ix]) * fx;
                dst[c * dn + y * dw + x] += t + (u - t) * fy;
            }
        }
    }

    /// <summary>NaN/±Inf/음수 → 0(Vector&lt;T&gt;). 반환 = 수리 수.</summary>
    public static int Sanitize(float[] a)
    {
        int i = 0, n = a.Length, bad = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var fm = new Vector<float>(float.MaxValue); var acc = Vector<int>.Zero;
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = new Vector<float>(a, i);
                var nf = ~Vector.Equals(v, v) | Vector.GreaterThan(Vector.Abs(v), fm) | Vector.LessThan(v, Vector<float>.Zero);
                if (nf == Vector<int>.Zero) continue;
                acc += nf;
                Vector.ConditionalSelect(nf, Vector<float>.Zero, v).CopyTo(a, i);
            }
            bad = -Vector.Sum(acc);
        }
        for (; i < n; i++) if (!(a[i] >= 0f) || a[i] > float.MaxValue) { a[i] = 0f; bad++; }
        return bad;
    }

    /// <summary>ACEScc 니 압축 — 니 초과 블록만 스칼라. 반환 = 압축 수.</summary>
    public static int CompressSpan(float[] a)
    {
        int i = 0, n = a.Length, cnt = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var knee = new Vector<float>(KneeLinear);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = new Vector<float>(a, i);
                if (!Vector.GreaterThanAny(v, knee) && Vector.EqualsAll(v, v)) continue;
                for (var k = i; k < i + Vector<float>.Count; k++)
                    if (!(a[k] <= KneeLinear)) { a[k] = Compress(a[k]); cnt++; }
            }
        }
        for (; i < n; i++) if (!(a[i] <= KneeLinear)) { a[i] = Compress(a[i]); cnt++; }
        return cnt;
    }

    // ══════════════════════ 레이턴시 예산 ══════════════════════

    public void Adapt(double postMs)
    {
        LastPostMs = postMs;
        EwmaPostMs = EwmaPostMs <= 0 ? postMs : 0.8 * EwmaPostMs + 0.2 * postMs;
        if (postMs > MaxPostMs) MaxPostMs = postMs;
        if (postMs > BudgetMs) OverBudgetFrames++;
        if (!Enabled) return;
        if (_cool > 0) _cool--;
        if (postMs > BudgetMs)
        {
            _under = 0;
            if (_cool == 0 && Levels > MinLevels) { Levels--; Downshifts++; _cool = 10; }
        }
        else if (EwmaPostMs < 0.5 * BudgetMs && Levels < MaxLevels)
        {
            if (++_under >= 60) { Levels++; Upshifts++; _under = 0; _cool = 10; }
        }
        else _under = 0;
    }

    // ══════════════════════ 표시 톤매핑 ══════════════════════

    /// <summary>ACES 필믹 → 감마 2 → RGB8(행 우선 · 상단부터). 화이트아웃 비율 갱신.</summary>
    public void ToneMap(float[] r, float[] g, float[] b, byte[] rgb)
    {
        Tm(r, rgb, 0); Tm(g, rgb, 1); Tm(b, rgb, 2);
        var white = 0;
        for (var i = 0; i < 3 * RealtimeAnamorphicLensOpticsEngine.Pixels; i += 3)
            if (rgb[i] >= 254 && rgb[i + 1] >= 254 && rgb[i + 2] >= 254) white++;
        WhiteoutRatio = white / (float)RealtimeAnamorphicLensOpticsEngine.Pixels;
        if (WhiteoutRatio > MaxWhiteout) MaxWhiteout = WhiteoutRatio;
    }

    /// <summary>ACES 필믹 → 감마 2 LUT(4096) — NaN/+Inf → 255(0xFFFFFFFF 화이트아웃 표면화 · 가드 OFF 관측).</summary>
    private static void Tm(float[] src, byte[] rgb, int c)
    {
        var lut = Gamma;
        for (int i = 0, o = c; i < RealtimeAnamorphicLensOpticsEngine.Pixels; i++, o += 3)
        {
            var x = src[i];
            int k;
            if (float.IsNaN(x) || x > 1e15f) k = GammaN - 1;
            else if (!(x > 0f)) k = 0;
            else
            {
                var t = x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f);
                k = t >= 1f ? GammaN - 1 : (int)(t * (GammaN - 1) + 0.5f);
            }
            rgb[o] = lut[k];
        }
    }

    private const int GammaN = 4096;
    private static readonly byte[] Gamma = BuildGamma();

    private static byte[] BuildGamma()
    {
        var g = new byte[GammaN];
        for (var i = 0; i < GammaN; i++) g[i] = (byte)(MathF.Sqrt(i / (GammaN - 1f)) * 255f + 0.5f);
        return g;
    }
}
