using System.Numerics;

namespace PromptFlowStudio.Services.Deflicker;

/// <summary>
/// [PromptFlow Studio v17.07.0] 평면(float 단일 채널) 연산 — 2× 축소([1 3 3 1]/8 분리형 · 반화소 정렬) · 쌍선형 확대 · 박스 합 · 쌍선형 표본/워프.
/// </summary>
public static class FlowPlane
{
    /// <summary>2× 축소: dst(x) = Σ k[i]·src(2x − 1 + i), k = [1 3 3 1]/8(가장자리 복제). tmp ≥ (w/2)·h.</summary>
    public static void Down(ReadOnlySpan<float> src, int w, int h, Span<float> tmp, Span<float> dst)
    {
        int w2 = w / 2, h2 = h / 2;
        for (var y = 0; y < h; y++)
        {
            var r = y * w;
            for (var x = 0; x < w2; x++)
            {
                int a = Math.Max(0, 2 * x - 1), d = Math.Min(w - 1, 2 * x + 2);
                tmp[y * w2 + x] = (src[r + a] + 3f * src[r + 2 * x] + 3f * src[r + 2 * x + 1] + src[r + d]) * 0.125f;
            }
        }
        for (var y = 0; y < h2; y++)
        {
            int a = Math.Max(0, 2 * y - 1) * w2, b = 2 * y * w2, c = (2 * y + 1) * w2, d = Math.Min(h - 1, 2 * y + 2) * w2;
            for (var x = 0; x < w2; x++) dst[y * w2 + x] = (tmp[a + x] + 3f * tmp[b + x] + 3f * tmp[c + x] + tmp[d + x]) * 0.125f;
        }
    }

    /// <summary>2× 쌍선형 확대(반화소 정렬): dst(x, y) = src((x + 0.5)/2 − 0.5, …).</summary>
    public static void Up(ReadOnlySpan<float> src, int w2, int h2, Span<float> dst)
    {
        int w = w2 * 2, h = h2 * 2;
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Clamp((y + 0.5f) * 0.5f - 0.5f, 0f, h2 - 1f);
            int y0 = Math.Min((int)sy, h2 - 2 < 0 ? 0 : h2 - 2), y1 = Math.Min(y0 + 1, h2 - 1); var fy = sy - y0;
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Clamp((x + 0.5f) * 0.5f - 0.5f, 0f, w2 - 1f);
                int x0 = Math.Min((int)sx, w2 - 2 < 0 ? 0 : w2 - 2), x1 = Math.Min(x0 + 1, w2 - 1); var fx = sx - x0;
                var top = src[y0 * w2 + x0] * (1 - fx) + src[y0 * w2 + x1] * fx;
                var bot = src[y1 * w2 + x0] * (1 - fx) + src[y1 * w2 + x1] * fx;
                dst[y * w + x] = top * (1 - fy) + bot * fy;
            }
        }
    }

    /// <summary>[1 2 1]/4 분리형 평활(제자리) — 반해상도로 앨리어싱된 고주파 질감 잔여 억제.</summary>
    public static void Smooth121(Span<float> s, int w, int h, Span<float> tmp)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var r = y * w;
                tmp[r + x] = (s[r + Math.Max(0, x - 1)] + 2f * s[r + x] + s[r + Math.Min(w - 1, x + 1)]) * 0.25f;
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                s[y * w + x] = (tmp[Math.Max(0, y - 1) * w + x] + 2f * tmp[y * w + x] + tmp[Math.Min(h - 1, y + 1) * w + x]) * 0.25f;
    }

    /// <summary>창 반경 r 박스 합(가장자리에서 잘림 · 분리형 누적).</summary>
    public static void BoxSum(ReadOnlySpan<float> src, int w, int h, int r, Span<float> tmp, Span<float> dst)
    {
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            float acc = 0;
            for (var x = 0; x <= Math.Min(r, w - 1); x++) acc += src[row + x];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = acc;
                var add = x + r + 1; var sub = x - r;
                if (add < w) acc += src[row + add];
                if (sub >= 0) acc -= src[row + sub];
            }
        }
        for (var x = 0; x < w; x++)
        {
            float acc = 0;
            for (var y = 0; y <= Math.Min(r, h - 1); y++) acc += tmp[y * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = acc;
                var add = y + r + 1; var sub = y - r;
                if (add < h) acc += tmp[add * w + x];
                if (sub >= 0) acc -= tmp[sub * w + x];
            }
        }
    }

    public static float Sample(ReadOnlySpan<float> s, int w, int h, float x, float y)
    {
        x = Math.Clamp(x, 0f, w - 1.001f); y = Math.Clamp(y, 0f, h - 1.001f);
        int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0;
        var o = y0 * w + x0;
        return (s[o] * (1 - fx) + s[o + 1] * fx) * (1 - fy) + (s[o + w] * (1 - fx) + s[o + w + 1] * fx) * fy;
    }

    /// <summary>Catmull-Rom 쌍삼차 표본(서브픽셀 정합용 — 쌍선형 보간 편향 제거).</summary>
    public static float SampleCubic(ReadOnlySpan<float> s, int w, int h, float x, float y)
    {
        x = Math.Clamp(x, 0f, w - 1.001f); y = Math.Clamp(y, 0f, h - 1.001f);
        int ix = (int)x, iy = (int)y; float fx = x - ix, fy = y - iy;
        Span<float> wx = stackalloc float[4];
        Weights(fx, wx);
        float acc = 0;
        for (var j = -1; j <= 2; j++)
        {
            var row = Math.Clamp(iy + j, 0, h - 1) * w;
            var r = s[row + Math.Clamp(ix - 1, 0, w - 1)] * wx[0] + s[row + ix] * wx[1] + s[row + Math.Min(ix + 1, w - 1)] * wx[2] + s[row + Math.Min(ix + 2, w - 1)] * wx[3];
            float t = fy, wy = j switch { -1 => ((-0.5f * t + 1f) * t - 0.5f) * t, 0 => (1.5f * t - 2.5f) * t * t + 1f, 1 => ((-1.5f * t + 2f) * t + 0.5f) * t, _ => (0.5f * t - 0.5f) * t * t };
            acc += r * wy;
        }
        return acc;
    }

    private static void Weights(float t, Span<float> w)
    {
        w[0] = ((-0.5f * t + 1f) * t - 0.5f) * t;
        w[1] = (1.5f * t - 2.5f) * t * t + 1f;
        w[2] = ((-1.5f * t + 2f) * t + 0.5f) * t;
        w[3] = (0.5f * t - 0.5f) * t * t;
    }

    /// <summary>쌍삼차 역방향 워프(고주파 보존 — 지표/롤백용).</summary>
    public static void WarpCubic(ReadOnlySpan<float> src, int w, int h, ReadOnlySpan<float> fx, ReadOnlySpan<float> fy, Span<float> dst)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                dst[i] = SampleCubic(src, w, h, x + fx[i], y + fy[i]);
            }
    }

    /// <summary>역방향 워프: dst(x) = src(x + f(x)).</summary>
    public static void Warp(ReadOnlySpan<float> src, int w, int h, ReadOnlySpan<float> fx, ReadOnlySpan<float> fy, Span<float> dst)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                dst[i] = Sample(src, w, h, x + fx[i], y + fy[i]);
            }
    }
}

/// <summary>[PromptFlow Studio v17.07.0] 피닝 SIMD 커널(스칼라 쌍둥이와 비트 동일 · FMA 축약 없음).</summary>
public static class PinSimd
{
    /// <summary>d = c + (s·w)·(p − c) — 픽셀별 신뢰도 w로 이전 텍스처 좌표 고정.</summary>
    public static void Pin(ReadOnlySpan<float> c, ReadOnlySpan<float> p, ReadOnlySpan<float> w, float s, Span<float> d)
    {
        int i = 0, n = d.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vs = new Vector<float>(s);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vc = new Vector<float>(c.Slice(i));
                var k = vs * new Vector<float>(w.Slice(i));
                (vc + k * (new Vector<float>(p.Slice(i)) - vc)).CopyTo(d.Slice(i));
            }
        }
        for (; i < n; i++) { var k = s * w[i]; d[i] = c[i] + k * (p[i] - c[i]); }
    }

    public static void PinScalar(ReadOnlySpan<float> c, ReadOnlySpan<float> p, ReadOnlySpan<float> w, float s, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++) { var k = s * w[i]; d[i] = c[i] + k * (p[i] - c[i]); }
    }

    /// <summary>d = l / max(b, eps) — 조명 정규화 디테일.</summary>
    public static void Normalize(ReadOnlySpan<float> l, ReadOnlySpan<float> b, float eps, Span<float> d)
    {
        int i = 0, n = d.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var ve = new Vector<float>(eps);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(l.Slice(i)) / Vector.Max(new Vector<float>(b.Slice(i)), ve)).CopyTo(d.Slice(i));
        }
        for (; i < n; i++) d[i] = l[i] / MathF.Max(b[i], eps);
    }

    public static void NormalizeScalar(ReadOnlySpan<float> l, ReadOnlySpan<float> b, float eps, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++) d[i] = l[i] / MathF.Max(b[i], eps);
    }

    /// <summary>d = a + q·max(b, eps) — 디테일 재조명 합성.</summary>
    public static void Compose(ReadOnlySpan<float> a, ReadOnlySpan<float> q, ReadOnlySpan<float> b, float eps, Span<float> d)
    {
        int i = 0, n = d.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var ve = new Vector<float>(eps);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(a.Slice(i)) + new Vector<float>(q.Slice(i)) * Vector.Max(new Vector<float>(b.Slice(i)), ve)).CopyTo(d.Slice(i));
        }
        for (; i < n; i++) d[i] = a[i] + q[i] * MathF.Max(b[i], eps);
    }
}

/// <summary>
/// [PromptFlow Studio v17.07.0] 텍스처 좌표 트랙 — 픽셀별 앵커(기준 프레임) 텍스처 좌표를 흐름으로 누적 합성(coord_t(x) = coord_{t−1}(x + f)),
/// 트랙 신뢰도 = 경로상 최소 흐름 신뢰도(화면 밖 유입 · 앵커 밖 좌표 = 0). 매 프레임 재샘플링 누적 블러 없이 앵커 텍스처를 1회만 표본.
/// </summary>
public sealed class PinTrack
{
    private readonly float[] _cx, _cy, _acc, _nx, _ny, _na;

    public PinTrack(int w, int h)
    {
        W = w; H = h;
        var n = w * h;
        _cx = new float[n]; _cy = new float[n]; _acc = new float[n]; _nx = new float[n]; _ny = new float[n]; _na = new float[n];
        Reset();
    }

    public int W { get; }
    public int H { get; }
    public float MeanTrust { get; private set; }
    public ReadOnlySpan<float> Trust => _acc;
    public ReadOnlySpan<float> CoordX => _cx;
    public ReadOnlySpan<float> CoordY => _cy;

    public void Reset()
    {
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var i = y * W + x; _cx[i] = x; _cy[i] = y; _acc[i] = 1f; }
        MeanTrust = 1f;
    }

    public void Advance(ReadOnlySpan<float> fx, ReadOnlySpan<float> fy, ReadOnlySpan<float> conf)
    {
        double sum = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                float sx = x + fx[i], sy = y + fy[i];
                if (sx < 0 || sy < 0 || sx > W - 1 || sy > H - 1) { _nx[i] = x; _ny[i] = y; _na[i] = 0; continue; }
                var cx = FlowPlane.Sample(_cx, W, H, sx, sy); var cy = FlowPlane.Sample(_cy, W, H, sx, sy);
                var a = MathF.Min(FlowPlane.Sample(_acc, W, H, sx, sy), MathF.Min(1f, 2f * conf[i]));   // 신뢰 0.5 이상 = 추적 유지(경로 최소)
                if (cx < 0 || cy < 0 || cx > W - 1 || cy > H - 1) a = 0;
                _nx[i] = cx; _ny[i] = cy; _na[i] = a; sum += a;
            }
        _nx.AsSpan().CopyTo(_cx); _ny.AsSpan().CopyTo(_cy); _na.AsSpan().CopyTo(_acc);
        MeanTrust = (float)(sum / _acc.Length);
    }

    /// <summary>불일치(가림 해제 · 다른 텍스처 유입) 픽셀 트랙 무효화 — mask는 scale배 해상도(scale × scale 블록 중 하나라도 불일치면 무효).</summary>
    public void Invalidate(ReadOnlySpan<byte> mask, int maskW, int scale)
    {
        double sum = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                for (var dy = 0; dy < scale && _acc[i] > 0; dy++)
                    for (var dx = 0; dx < scale; dx++)
                        if (mask[(y * scale + dy) * maskW + x * scale + dx] != 0) { _acc[i] = 0; break; }
                sum += _acc[i];
            }
        MeanTrust = (float)(sum / _acc.Length);
    }

    /// <summary>앵커 평면을 트랙 좌표로 1회 표본(쌍삼차).</summary>
    public void Fetch(ReadOnlySpan<float> anchor, Span<float> dst)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = FlowPlane.SampleCubic(anchor, W, H, _cx[i], _cy[i]);
    }
}

/// <summary>
/// [PromptFlow Studio v17.07.0] ✨ 서브픽셀 광학 흐름 &amp; 텍스처 피닝 엔진.
/// 반해상도 저역(블러) 휘도에서 3 단계 피라미드 조밀 루카스-카나데(창 7×7 · 단계당 6 반복 · 쌍선형 서브픽셀 표본) → 고유값 신뢰도 기반 정규화 합성곱으로
/// 무텍스처 영역 보간 → 광도 잔차 신뢰도 → 전해상도 흐름(×2) 확대. 고주파 텍스처 자체의 재생성 떨림은 흐름에 섞이지 않도록 저역에서만 추적하고,
/// 앵커 프레임 텍스처 좌표를 흐름으로 누적 추적(PinTrack)해 1회 표본한 텍스처로 SIMD 피닝(d = c + s·w·(p − c)) — 좌표 고정 · 재샘플링 블러 없음. 전 경로 무할당.
/// </summary>
public sealed class SubPixelOpticalFlowTexturePinningEngine
{
    public const int Levels = 3, Window = 3, Iterations = 12, FillRadius = 3;
    public const float MinEigen = 8e-4f, MaxLevelUpdate = 1.5f;

    private readonly int[] _w = new int[Levels], _h = new int[Levels];
    private readonly float[][] _cur = new float[Levels][], _prev = new float[Levels][], _ix = new float[Levels][], _iy = new float[Levels][];
    private readonly float[][] _hxx = new float[Levels][], _hxy = new float[Levels][], _hyy = new float[Levels][], _fx = new float[Levels][], _fy = new float[Levels][], _rel = new float[Levels][];
    private readonly float[] _e, _a, _b, _tmp, _bx, _by, _f0x, _f0y;
    private readonly float[] _conf, _fullX, _fullY, _fullConf;
    private readonly PinTrack _trackFull, _trackHalf;

    public SubPixelOpticalFlowTexturePinningEngine(int halfWidth = 80, int halfHeight = 48)
    {
        if (halfWidth % 4 != 0 || halfHeight % 4 != 0) throw new ArgumentException("half size % 4 == 0");
        for (var l = 0; l < Levels; l++)
        {
            int w = halfWidth >> l, h = halfHeight >> l, n = w * h;
            _w[l] = w; _h[l] = h;
            _cur[l] = new float[n]; _prev[l] = new float[n]; _ix[l] = new float[n]; _iy[l] = new float[n];
            _hxx[l] = new float[n]; _hxy[l] = new float[n]; _hyy[l] = new float[n]; _fx[l] = new float[n]; _fy[l] = new float[n]; _rel[l] = new float[n];
        }
        var m = halfWidth * halfHeight;
        _e = new float[m]; _a = new float[m]; _b = new float[m]; _tmp = new float[m]; _bx = new float[m]; _by = new float[m]; _f0x = new float[m]; _f0y = new float[m];
        _conf = new float[m];
        _fullX = new float[m * 4]; _fullY = new float[m * 4]; _fullConf = new float[m * 4];
        HalfWidth = halfWidth; HalfHeight = halfHeight;
        _trackFull = new PinTrack(halfWidth * 2, halfHeight * 2); _trackHalf = new PinTrack(halfWidth, halfHeight);
    }

    public int HalfWidth { get; }
    public int HalfHeight { get; }
    public int Width => HalfWidth * 2;
    public int Height => HalfHeight * 2;
    public float ResidualLow { get; set; } = 0.015f;
    public float ResidualHigh { get; set; } = 0.06f;
    public long Estimations { get; private set; }
    public double LastFlowUs { get; private set; }
    public float MeanConfidence { get; private set; }
    public float MeanMotion { get; private set; }
    public float ReliableShare { get; private set; }
    /// <summary>직전 대비 전역 조명 이득(흐름 전처리).</summary>
    public float LightGain { get; private set; } = 1f;
    /// <summary>반해상도 흐름(px): cur(x) ≈ prev(x + f).</summary>
    public ReadOnlySpan<float> FlowX => _fx[0];
    public ReadOnlySpan<float> FlowY => _fy[0];
    public ReadOnlySpan<float> Confidence => _conf;
    /// <summary>전해상도 흐름(px) · 신뢰도.</summary>
    public ReadOnlySpan<float> FullFlowX => _fullX;
    public ReadOnlySpan<float> FullFlowY => _fullY;
    public ReadOnlySpan<float> FullConfidence => _fullConf;

    /// <summary>반해상도 저역 휘도 2장 → 조밀 흐름 · 신뢰도(무할당).</summary>
    public void Estimate(ReadOnlySpan<float> cur, ReadOnlySpan<float> prev)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 전역 조명 이득 보정(밝기 항상성 가정 복원 — 저기울기 영역 흐름 편향 방지).
        double mc = 0, mp = 0;
        for (var i = 0; i < cur.Length; i++) { mc += cur[i]; mp += prev[i]; }
        var gain = mc > 1e-6 ? (float)(mp / mc) : 1f;
        LightGain = gain;
        for (var i = 0; i < cur.Length; i++) _cur[0][i] = cur[i] * gain;
        prev.CopyTo(_prev[0]);
        for (var l = 1; l < Levels; l++)
        {
            FlowPlane.Down(_cur[l - 1], _w[l - 1], _h[l - 1], _tmp, _cur[l]);
            FlowPlane.Down(_prev[l - 1], _w[l - 1], _h[l - 1], _tmp, _prev[l]);
        }
        for (var l = Levels - 1; l >= 0; l--)
        {
            int w = _w[l], h = _h[l], n = w * h;
            if (l == Levels - 1) { Array.Clear(_fx[l]); Array.Clear(_fy[l]); }
            else
            {
                FlowPlane.Up(_fx[l + 1], _w[l + 1], _h[l + 1], _fx[l]); FlowPlane.Up(_fy[l + 1], _w[l + 1], _h[l + 1], _fy[l]);
                for (var i = 0; i < n; i++) { _fx[l][i] *= 2f; _fy[l][i] *= 2f; }
            }
            Level(l);
        }
        // 광도 잔차 신뢰도(반해상도) → 전해상도 흐름/신뢰도.
        int w0 = _w[0], h0 = _h[0];
        double cs = 0, ms = 0;
        for (var y = 0; y < h0; y++)
            for (var x = 0; x < w0; x++)
            {
                var i = y * w0 + x;
                var r = MathF.Abs(FlowPlane.Sample(_prev[0], w0, h0, x + _fx[0][i], y + _fy[0][i]) - _cur[0][i]);
                var c = Math.Clamp(1f - (r - ResidualLow) / (ResidualHigh - ResidualLow), 0f, 1f);
                _conf[i] = c; cs += c; ms += MathF.Sqrt(_fx[0][i] * _fx[0][i] + _fy[0][i] * _fy[0][i]);
            }
        // 흐름 매끄러움 신뢰: 5×5 평균과의 편차가 큰 픽셀(평탄 영역 발산 · 띠 모양 오흐름)은 광도 잔차가 작아도 불신.
        var nn = w0 * h0;
        FlowPlane.BoxSum(_fx[0], w0, h0, 2, _tmp, _bx); FlowPlane.BoxSum(_fy[0], w0, h0, 2, _tmp, _by);
        Array.Fill(_a, 1f, 0, nn);
        FlowPlane.BoxSum(_a.AsSpan(0, nn), w0, h0, 2, _tmp, _b);
        cs = 0;
        for (var i = 0; i < nn; i++)
        {
            float dx = _fx[0][i] - _bx[i] / _b[i], dy = _fy[0][i] - _by[i] / _b[i];
            var dev = MathF.Sqrt(dx * dx + dy * dy);
            _conf[i] *= Math.Clamp(1f - (dev - 0.4f) / 0.8f, 0f, 1f);
            cs += _conf[i];
        }
        MeanConfidence = (float)(cs / (w0 * h0)); MeanMotion = (float)(ms / (w0 * h0) * 2);
        FlowPlane.Up(_fx[0], w0, h0, _fullX); FlowPlane.Up(_fy[0], w0, h0, _fullY); FlowPlane.Up(_conf, w0, h0, _fullConf);
        for (var i = 0; i < _fullX.Length; i++) { _fullX[i] *= 2f; _fullY[i] *= 2f; }
        Estimations++;
        LastFlowUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>한 단계: 현재 영상 기울기 헤시안(창 합) 고정 · 반복마다 잔차 기울기 창 합 → Δf = −H⁻¹b(±1 px 제한) → 신뢰 낮은 픽셀 정규화 합성곱 보간.</summary>
    private void Level(int l)
    {
        int w = _w[l], h = _h[l], n = w * h;
        float[] c = _cur[l], p = _prev[l], ix = _ix[l], iy = _iy[l], fx = _fx[l], fy = _fy[l], rel = _rel[l];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                ix[i] = (c[y * w + Math.Min(w - 1, x + 1)] - c[y * w + Math.Max(0, x - 1)]) * 0.5f;
                iy[i] = (c[Math.Min(h - 1, y + 1) * w + x] - c[Math.Max(0, y - 1) * w + x]) * 0.5f;
            }
        var e = _e.AsSpan(0, n); var a = _a.AsSpan(0, n); var tmp = _tmp.AsSpan(0, n);
        for (var i = 0; i < n; i++) a[i] = ix[i] * ix[i];
        FlowPlane.BoxSum(a, w, h, Window, tmp, _hxx[l]);
        for (var i = 0; i < n; i++) a[i] = ix[i] * iy[i];
        FlowPlane.BoxSum(a, w, h, Window, tmp, _hxy[l]);
        for (var i = 0; i < n; i++) a[i] = iy[i] * iy[i];
        FlowPlane.BoxSum(a, w, h, Window, tmp, _hyy[l]);
        float[] hxx = _hxx[l], hxy = _hxy[l], hyy = _hyy[l];
        var rc = 0;
        for (var i = 0; i < n; i++)
        {
            float tr = hxx[i] + hyy[i], dt = hxx[i] * hyy[i] - hxy[i] * hxy[i];
            var lmin = 0.5f * (tr - MathF.Sqrt(MathF.Max(0f, tr * tr - 4f * dt)));
            rel[i] = lmin > MinEigen ? MathF.Min(lmin, 0.05f) : 0f;
            if (rel[i] > 0) rc++;
        }
        if (l == 0) ReliableShare = rc / (float)n;
        var bxs = _bx.AsSpan(0, n); var bys = _by.AsSpan(0, n);
        fx.AsSpan(0, n).CopyTo(_f0x); fy.AsSpan(0, n).CopyTo(_f0y);
        for (var it = 0; it < Iterations; it++)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    e[i] = FlowPlane.SampleCubic(p, w, h, x + fx[i], y + fy[i]) - c[i];
                }
            for (var i = 0; i < n; i++) a[i] = ix[i] * e[i];
            FlowPlane.BoxSum(a, w, h, Window, tmp, bxs);
            for (var i = 0; i < n; i++) a[i] = iy[i] * e[i];
            FlowPlane.BoxSum(a, w, h, Window, tmp, bys);
            for (var i = 0; i < n; i++)
            {
                if (rel[i] <= 0) continue;
                var det = hxx[i] * hyy[i] - hxy[i] * hxy[i];
                var dx = -(hyy[i] * bxs[i] - hxy[i] * bys[i]) / det;
                var dy = -(hxx[i] * bys[i] - hxy[i] * bxs[i]) / det;
                // 단계당 누적 갱신 ±MaxLevelUpdate(평탄/개구 영역 발산 방지 — 큰 이동은 거친 단계가 담당).
                fx[i] = Math.Clamp(fx[i] + Math.Clamp(dx, -1f, 1f), _f0x[i] - MaxLevelUpdate, _f0x[i] + MaxLevelUpdate);
                fy[i] = Math.Clamp(fy[i] + Math.Clamp(dy, -1f, 1f), _f0y[i] - MaxLevelUpdate, _f0y[i] + MaxLevelUpdate);
            }
            Regularize(w, h, n, fx, fy, rel, a, tmp, bxs, bys, e);
        }
        // 정규화 합성곱: 신뢰 픽셀 흐름으로 무텍스처/개구 문제 픽셀 채움.
        for (var i = 0; i < n; i++) a[i] = fx[i] * rel[i];
        FlowPlane.BoxSum(a, w, h, FillRadius, tmp, bxs);
        for (var i = 0; i < n; i++) a[i] = fy[i] * rel[i];
        FlowPlane.BoxSum(a, w, h, FillRadius, tmp, bys);
        FlowPlane.BoxSum(rel, w, h, FillRadius, tmp, e);
        for (var i = 0; i < n; i++)
        {
            if (rel[i] > 0 || e[i] <= 0) continue;
            fx[i] = bxs[i] / e[i]; fy[i] = bys[i] / e[i];
        }
    }

    /// <summary>
    /// 반복 간 흐름 정칙화(3×3 신뢰도 가중 평균): 픽셀별 창 합 근사(이웃 흐름 공유)에서 감쇠하지 않는 고주파(체커) 흐름 모드를 제거.
    /// </summary>
    private static void Regularize(int w, int h, int n, float[] fx, float[] fy, float[] rel, Span<float> a, Span<float> tmp, Span<float> sx, Span<float> sy, Span<float> sw)
    {
        for (var i = 0; i < n; i++) a[i] = fx[i] * rel[i];
        FlowPlane.BoxSum(a, w, h, 1, tmp, sx);
        for (var i = 0; i < n; i++) a[i] = fy[i] * rel[i];
        FlowPlane.BoxSum(a, w, h, 1, tmp, sy);
        FlowPlane.BoxSum(rel, w, h, 1, tmp, sw);
        for (var i = 0; i < n; i++)
        {
            if (sw[i] <= 0) continue;
            fx[i] = sx[i] / sw[i]; fy[i] = sy[i] / sw[i];
        }
    }

    public PinTrack TrackFull => _trackFull;
    public PinTrack TrackHalf => _trackHalf;
    public long Reanchors { get; private set; }
    /// <summary>트랙 신뢰 평균이 이 값 미만이면 재고정(새 앵커).</summary>
    public float ReanchorTrust { get; set; } = 0.8f;
    /// <summary>앵커 최대 수명(프레임) — 흐름 오차 누적(좌표 표류) 상한.</summary>
    public int MaxAge { get; set; } = 40;
    public int Age { get; private set; }
    public bool NeedsAnchor => _trackFull.MeanTrust < ReanchorTrust || Age >= MaxAge;

    /// <summary>흐름 추정 후 텍스처 좌표 트랙 전진.</summary>
    public void AdvanceTracks()
    {
        _trackFull.Advance(_fullX, _fullY, _fullConf);
        _trackHalf.Advance(_fx[0], _fy[0], _conf);
        Age++;
    }

    /// <summary>현재 출력을 새 앵커로(좌표 항등 · 신뢰 1).</summary>
    public void Reanchor() { _trackFull.Reset(); _trackHalf.Reset(); Reanchors++; Age = 0; }

    /// <summary>텍스처 피닝: 앵커 텍스처를 누적 좌표로 끌어와(pinned) 가중(트랙 신뢰 × 비구조) 고정 → dst.</summary>
    public void PinPlane(ReadOnlySpan<float> cur, ReadOnlySpan<float> anchor, bool full, float strength, ReadOnlySpan<float> weight, Span<float> pinned, Span<float> dst)
    {
        (full ? _trackFull : _trackHalf).Fetch(anchor, pinned);
        PinSimd.Pin(cur, pinned, weight, strength, dst);
    }
}
