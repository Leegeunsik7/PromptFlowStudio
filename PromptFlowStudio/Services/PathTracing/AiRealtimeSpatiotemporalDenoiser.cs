using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.PathTracing;

/// <summary>신경망 업스케일러 결선점(디노이즈된 선형 RGB → 출력 해상도). 무할당 구현 필요.</summary>
public interface INeuralUpscaler
{
    void Upscale(ReadOnlySpan<float> src, int width, int height, Span<float> dst, int outWidth, int outHeight);
}

/// <summary>
/// [PromptFlow Studio v16.74.0] 🧹 실시간 AI 시공간 디노이저 &amp; 업스케일러(SVGF 계열 · 무할당).
///   ▸ 알베도 복조 → 시간 누적: 모션 벡터 재투영(쌍선형 4탭) · 탭별 깊이/법선 일관성 검사(가림 해제 = 이력 리셋) · 3×3 이웃 분산 클리핑(조명 변화 잔상 제거) ·
///     이력 길이 기반 지수 이동 평균(α ≥ <see cref="AlphaMin"/>) · 휘도 모멘트 → 분산(이력 짧으면 공간 분산).
///   ▸ 공간: 에지 정지 À-trous 웨이블릿(B3 5×5 · 스텝 1,2,4,8,16) — 깊이(상대) · 법선(cos¹²⁸) · 분산 정규화 휘도 가중 · 분산 전파. 강도 = 반복 수/휘도 허용폭.
///   ▸ 재변조 → 언샤프 → Catmull-Rom 업스케일(×1~4, 렌더 960×540 → 3840×2160) 또는 <see cref="INeuralUpscaler"/> → ACES 톤매핑(SIMD) RGBA8.
/// </summary>
public sealed class AiRealtimeSpatiotemporalDenoiser
{
    public const int MaxHistory = 32;
    private static readonly float[] Kernel = { 1f / 16, 1f / 4, 3f / 8, 1f / 4, 1f / 16 };

    private readonly object _gate = new();
    private int _w, _h, _scale = 1, _ow, _oh;
    private float[] _illum = Array.Empty<float>(), _hist = Array.Empty<float>(), _histNext = Array.Empty<float>();
    private float[] _len = Array.Empty<float>(), _lenNext = Array.Empty<float>(), _m1 = Array.Empty<float>(), _m1Next = Array.Empty<float>(), _m2 = Array.Empty<float>(), _m2Next = Array.Empty<float>();
    private float[] _prevDepth = Array.Empty<float>(), _prevNormal = Array.Empty<float>(), _var = Array.Empty<float>(), _varB = Array.Empty<float>();
    private float[] _a = Array.Empty<float>(), _b = Array.Empty<float>(), _out = Array.Empty<float>(), _up = Array.Empty<float>(), _tone = Array.Empty<float>();
    private byte[] _rgba = Array.Empty<byte>();
    private bool _hasHistory;
    private long _frames, _ticks;
    private float _disocclusion, _meanLen;

    public AiRealtimeSpatiotemporalDenoiser() => Configure(160, 90, 2);

    public float Strength { get; set; } = 0.7f;
    public float AlphaMin { get; set; } = 0.1f;
    public float ClampGamma { get; set; } = 1.5f;
    public bool ClampHistory { get; set; } = true;
    public bool UseMotionVectors { get; set; } = true;
    public bool Temporal { get; set; } = true;
    public float Sharpen { get; set; } = 0.15f;
    /// <summary>파이어플라이 억제 배수(0 = 끔).</summary>
    public float FireflyClamp { get; set; } = 4f;
    public float Exposure { get; set; } = 1f;
    public INeuralUpscaler? Upscaler { get; set; }

    public int Width => _w;
    public int Height => _h;
    public int Scale => _scale;
    public int OutputWidth => _ow;
    public int OutputHeight => _oh;
    public long Frames => Interlocked.Read(ref _frames);
    public double LastMs => _ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    /// <summary>직전 프레임 이력 거부(가림 해제) 픽셀 비율.</summary>
    public float DisocclusionRatio => _disocclusion;
    public float MeanHistoryLength => _meanLen;
    public int Iterations => Strength <= 0 ? 0 : 1 + (int)MathF.Round(Math.Clamp(Strength, 0f, 1f) * 4);
    /// <summary>렌더 해상도 디노이즈 결과(선형 RGB).</summary>
    public ReadOnlySpan<float> Output => _out;
    /// <summary>출력 해상도(선형 RGB).</summary>
    public ReadOnlySpan<float> Upscaled => _up;
    /// <summary>출력 해상도 톤매핑 RGBA8.</summary>
    public ReadOnlySpan<byte> Rgba => _rgba;

    /// <summary>해상도/배율 구성(버퍼 재할당 · 이력 리셋).</summary>
    public void Configure(int width, int height, int scale)
    {
        lock (_gate)
        {
            _w = Math.Max(4, width); _h = Math.Max(4, height); _scale = Math.Clamp(scale, 1, 4);
            _ow = _w * _scale; _oh = _h * _scale;
            var n = _w * _h;
            _illum = new float[n * 3]; _hist = new float[n * 3]; _histNext = new float[n * 3];
            _len = new float[n]; _lenNext = new float[n]; _m1 = new float[n]; _m1Next = new float[n]; _m2 = new float[n]; _m2Next = new float[n];
            _prevDepth = new float[n]; _prevNormal = new float[n * 3]; _var = new float[n]; _varB = new float[n];
            _a = new float[n * 3]; _b = new float[n * 3]; _out = new float[n * 3];
            _up = new float[_ow * _oh * 3]; _tone = new float[_ow * _oh * 3]; _rgba = new byte[_ow * _oh * 4];
            _hasHistory = false;
        }
    }

    public void Reset() { lock (_gate) _hasHistory = false; }

    private static float Lum(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    /// <summary>1프레임 디노이즈(입력 = 렌더 해상도 AOV · mv = 이전 프레임 픽셀 − 현재 픽셀). 무할당.</summary>
    public void Denoise(ReadOnlySpan<float> color, ReadOnlySpan<float> albedo, ReadOnlySpan<float> normal, ReadOnlySpan<float> depth, ReadOnlySpan<float> mvx, ReadOnlySpan<float> mvy)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Denoise.Frame");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int w = _w, h = _h, n = w * h;
            if (color.Length < n * 3 || albedo.Length < n * 3 || normal.Length < n * 3 || depth.Length < n || mvx.Length < n || mvy.Length < n)
                throw new ArgumentException("AOV 크기가 구성 해상도와 다릅니다.");
            // 1) 복조 + 파이어플라이 억제(휘도 > k·이웃 평균 → 스케일 다운 · 저 SPP 편향-분산 절충)
            for (var i = 0; i < n * 3; i++) _b[i] = color[i] / MathF.Max(albedo[i], 1e-3f);
            var ffk = FireflyClamp;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var p = y * w + x;
                    float r = _b[p * 3], g = _b[p * 3 + 1], b = _b[p * 3 + 2];
                    if (ffk > 0)
                    {
                        float s = 0; var c = 0;
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if ((dx | dy) == 0 || (uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                                var q = (yy * w + xx) * 3;
                                s += Lum(_b[q], _b[q + 1], _b[q + 2]); c++;
                            }
                        var lim = ffk * s / c + 0.05f;
                        var l = Lum(r, g, b);
                        if (l > lim) { var sc = lim / l; r *= sc; g *= sc; b *= sc; }
                    }
                    _illum[p * 3] = r; _illum[p * 3 + 1] = g; _illum[p * 3 + 2] = b;
                }
            // 2) 시간 누적
            var rejected = 0; var lenSum = 0f;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var p = y * w + x;
                    float cr = _illum[p * 3], cg = _illum[p * 3 + 1], cb = _illum[p * 3 + 2];
                    var cl = Lum(cr, cg, cb);
                    // 3×3 이웃 통계
                    float sr = 0, sg = 0, sb = 0, qr = 0, qg = 0, qb = 0, sl = 0, ql = 0; var cnt = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                            var q = (yy * w + xx) * 3;
                            float r = _illum[q], g = _illum[q + 1], b = _illum[q + 2];
                            sr += r; sg += g; sb += b; qr += r * r; qg += g * g; qb += b * b;
                            var l = Lum(r, g, b); sl += l; ql += l * l;
                            cnt++;
                        }
                    float ic = 1f / cnt;
                    float mr = sr * ic, mg = sg * ic, mb = sb * ic;
                    var spatialVar = MathF.Max(0, ql * ic - sl * ic * (sl * ic));
                    float hr = 0, hg = 0, hb = 0, hm1 = 0, hm2 = 0, hl = 0;
                    var valid = false;
                    if (_hasHistory && Temporal)
                    {
                        var px = x + (UseMotionVectors ? mvx[p] : 0);
                        var py = y + (UseMotionVectors ? mvy[p] : 0);
                        int ix = (int)MathF.Floor(px), iy = (int)MathF.Floor(py);
                        float fx = px - ix, fy = py - iy, wsum = 0;
                        for (var k = 0; k < 4; k++)
                        {
                            int tx = ix + (k & 1), ty = iy + (k >> 1);
                            if ((uint)tx >= (uint)w || (uint)ty >= (uint)h) continue;
                            var tp = ty * w + tx;
                            if (!Consistent(depth[p], _prevDepth[tp], normal, p, tp)) continue;
                            var wt = ((k & 1) != 0 ? fx : 1 - fx) * ((k >> 1) != 0 ? fy : 1 - fy);
                            if (wt <= 0) continue;
                            hr += _hist[tp * 3] * wt; hg += _hist[tp * 3 + 1] * wt; hb += _hist[tp * 3 + 2] * wt;
                            hm1 += _m1[tp] * wt; hm2 += _m2[tp] * wt; hl += _len[tp] * wt;
                            wsum += wt;
                        }
                        if (wsum > 0.01f)
                        {
                            var iw = 1f / wsum;
                            hr *= iw; hg *= iw; hb *= iw; hm1 *= iw; hm2 *= iw; hl *= iw;
                            valid = true;
                        }
                    }
                    float len, outR, outG, outB, m1, m2;
                    if (valid)
                    {
                        if (ClampHistory)
                        {
                            var gma = ClampGamma;
                            float dr = gma * MathF.Sqrt(MathF.Max(0, qr * ic - mr * mr)), dg = gma * MathF.Sqrt(MathF.Max(0, qg * ic - mg * mg)), db = gma * MathF.Sqrt(MathF.Max(0, qb * ic - mb * mb));
                            hr = Math.Clamp(hr, mr - dr, mr + dr); hg = Math.Clamp(hg, mg - dg, mg + dg); hb = Math.Clamp(hb, mb - db, mb + db);
                        }
                        len = MathF.Min(hl + 1, MaxHistory);
                        var a = MathF.Max(1f / len, AlphaMin);
                        outR = hr + (cr - hr) * a; outG = hg + (cg - hg) * a; outB = hb + (cb - hb) * a;
                        m1 = hm1 + (cl - hm1) * a; m2 = hm2 + (cl * cl - hm2) * a;
                    }
                    else
                    {
                        if (_hasHistory && Temporal) rejected++;
                        len = 1; outR = cr; outG = cg; outB = cb; m1 = cl; m2 = cl * cl;
                    }
                    _histNext[p * 3] = outR; _histNext[p * 3 + 1] = outG; _histNext[p * 3 + 2] = outB;
                    _lenNext[p] = len; _m1Next[p] = m1; _m2Next[p] = m2;
                    _var[p] = len >= 4 ? MathF.Max(0, m2 - m1 * m1) : spatialVar;
                    lenSum += len;
                }
            (_hist, _histNext) = (_histNext, _hist);
            (_len, _lenNext) = (_lenNext, _len);
            (_m1, _m1Next) = (_m1Next, _m1);
            (_m2, _m2Next) = (_m2Next, _m2);
            depth[..n].CopyTo(_prevDepth);
            normal[..(n * 3)].CopyTo(_prevNormal);
            _hasHistory = true;
            _disocclusion = (float)rejected / n;
            _meanLen = lenSum / n;
            // 3) À-trous
            _hist.AsSpan(0, n * 3).CopyTo(_a);
            var src = _a; var dst = _b;
            var iters = Iterations;
            var sigmaL = 3f + 2f * Math.Clamp(Strength, 0f, 1f);
            for (var it = 0; it < iters; it++)
            {
                Atrous(src, dst, 1 << it, sigmaL, depth, normal);
                (src, dst) = (dst, src);
                (_var, _varB) = (_varB, _var);
            }
            // 4) 재변조 + 언샤프
            for (var i = 0; i < n * 3; i++) dst[i] = src[i] * MathF.Max(albedo[i], 1e-3f);
            if (Sharpen > 0)
            {
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        for (var c = 0; c < 3; c++)
                        {
                            var p = (y * w + x) * 3 + c;
                            float s = 0; var k = 0;
                            for (var dy = -1; dy <= 1; dy++)
                                for (var dx = -1; dx <= 1; dx++)
                                {
                                    int xx = x + dx, yy = y + dy;
                                    if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                                    s += dst[(yy * w + xx) * 3 + c]; k++;
                                }
                            _out[p] = MathF.Max(0, dst[p] + Sharpen * (dst[p] - s / k));
                        }
            }
            else dst.AsSpan(0, n * 3).CopyTo(_out);
            // 5) 업스케일 · 톤매핑
            if (Upscaler is { } up) up.Upscale(_out, w, h, _up, _ow, _oh);
            else CatmullRom(_out, w, h, _up, _ow, _oh);
            ToneMap(_up, _rgba, Exposure, _tone);
            _frames++;
            _ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }

    private bool Consistent(float curDepth, float prevDepth, ReadOnlySpan<float> normal, int p, int tp)
    {
        var curSky = curDepth <= 0; var prevSky = prevDepth <= 0;
        if (curSky || prevSky) return curSky && prevSky;
        if (MathF.Abs(curDepth - prevDepth) > 0.1f * curDepth + 0.05f) return false;
        var d = normal[p * 3] * _prevNormal[tp * 3] + normal[p * 3 + 1] * _prevNormal[tp * 3 + 1] + normal[p * 3 + 2] * _prevNormal[tp * 3 + 2];
        return d >= 0.8f;
    }

    private void Atrous(float[] src, float[] dst, int step, float sigmaL, ReadOnlySpan<float> depth, ReadOnlySpan<float> normal)
    {
        int w = _w, h = _h;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                var lp = Lum(src[p * 3], src[p * 3 + 1], src[p * 3 + 2]);
                // 분산 3×3 가우시안 사전 평활
                float vs = 0, vw = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                        var k = (dx == 0 ? 2f : 1f) * (dy == 0 ? 2f : 1f);
                        vs += _var[yy * w + xx] * k; vw += k;
                    }
                var lDen = sigmaL * MathF.Sqrt(MathF.Max(0, vs / vw)) + 1e-4f;
                var zp = depth[p];
                // 화면 공간 깊이 기울기(중앙 차분) — 경사면은 픽셀당 깊이 변화가 커도 같은 표면
                float gx = 0, gy = 0;
                if (zp > 0)
                {
                    float zl = x > 0 ? depth[p - 1] : zp, zr = x < w - 1 ? depth[p + 1] : zp, zu = y > 0 ? depth[p - w] : zp, zd = y < h - 1 ? depth[p + w] : zp;
                    if (zl > 0 && zr > 0) gx = MathF.Abs(zr - zl) * 0.5f;
                    if (zu > 0 && zd > 0) gy = MathF.Abs(zd - zu) * 0.5f;
                }
                float nx = normal[p * 3], ny = normal[p * 3 + 1], nz = normal[p * 3 + 2];
                float sr = 0, sg = 0, sb = 0, sw = 0, sv = 0;
                for (var j = -2; j <= 2; j++)
                    for (var i = -2; i <= 2; i++)
                    {
                        int xx = x + i * step, yy = y + j * step;
                        if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
                        var q = yy * w + xx;
                        var kw = Kernel[i + 2] * Kernel[j + 2];
                        var zq = depth[q];
                        float wz;
                        if (zp <= 0 || zq <= 0) wz = zp <= 0 && zq <= 0 ? 1f : 0f;
                        else wz = MathF.Exp(-MathF.Abs(zp - zq) / ((gx * MathF.Abs(i) + gy * MathF.Abs(j)) * step + 0.005f * zp + 1e-3f));
                        if (wz <= 1e-4f) continue;
                        var nd = MathF.Max(0, nx * normal[q * 3] + ny * normal[q * 3 + 1] + nz * normal[q * 3 + 2]);
                        var wn = MathF.Pow(nd, 128);
                        var lq = Lum(src[q * 3], src[q * 3 + 1], src[q * 3 + 2]);
                        var wl = MathF.Exp(-MathF.Abs(lp - lq) / lDen);
                        var wt = kw * wz * wn * wl;
                        sr += src[q * 3] * wt; sg += src[q * 3 + 1] * wt; sb += src[q * 3 + 2] * wt;
                        sv += _var[q] * wt * wt;
                        sw += wt;
                    }
                if (sw <= 1e-8f) { dst[p * 3] = src[p * 3]; dst[p * 3 + 1] = src[p * 3 + 1]; dst[p * 3 + 2] = src[p * 3 + 2]; _varB[p] = _var[p]; continue; }
                var inv = 1f / sw;
                dst[p * 3] = sr * inv; dst[p * 3 + 1] = sg * inv; dst[p * 3 + 2] = sb * inv;
                _varB[p] = sv * inv * inv;
            }
    }

    // ══════════════════════ 업스케일 · 톤매핑 · 지표(정적 · 무할당) ══════════════════════

    private static float Cr(float t, float a, float b, float c, float d) =>
        0.5f * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t * t + (-a + 3 * b - 3 * c + d) * t * t * t);

    /// <summary>Catmull-Rom 바이큐빅 업스케일(경계 클램프 · 음수 링잉 0 클램프).</summary>
    public static void CatmullRom(ReadOnlySpan<float> src, int w, int h, Span<float> dst, int ow, int oh)
    {
        var sx = (float)w / ow; var sy = (float)h / oh;
        Span<float> col = stackalloc float[4];
        for (var oy = 0; oy < oh; oy++)
        {
            var fy = (oy + 0.5f) * sy - 0.5f;
            var iy = (int)MathF.Floor(fy); var ty = fy - iy;
            for (var ox = 0; ox < ow; ox++)
            {
                var fx = (ox + 0.5f) * sx - 0.5f;
                var ix = (int)MathF.Floor(fx); var tx = fx - ix;
                for (var c = 0; c < 3; c++)
                {
                    for (var j = 0; j < 4; j++)
                    {
                        var yy = Math.Clamp(iy - 1 + j, 0, h - 1) * w;
                        col[j] = Cr(tx, src[(yy + Math.Clamp(ix - 1, 0, w - 1)) * 3 + c], src[(yy + Math.Clamp(ix, 0, w - 1)) * 3 + c],
                            src[(yy + Math.Clamp(ix + 1, 0, w - 1)) * 3 + c], src[(yy + Math.Clamp(ix + 2, 0, w - 1)) * 3 + c]);
                    }
                    dst[(oy * ow + ox) * 3 + c] = MathF.Max(0, Cr(ty, col[0], col[1], col[2], col[3]));
                }
            }
        }
    }

    /// <summary>ACES(Narkowicz) 톤매핑 + 감마 2.0 근사 — Vector&lt;float&gt; SIMD 본체. scratch 길이 ≥ rgb.</summary>
    public static void ToneMap(ReadOnlySpan<float> rgb, Span<byte> rgba, float exposure, Span<float> scratch)
    {
        var n = rgb.Length;
        rgb.CopyTo(scratch);
        var s = scratch[..n];
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var v = MemoryMarshal.Cast<float, Vector<float>>(s);
            Vector<float> e = new(exposure), a = new(2.51f), b = new(0.03f), c = new(2.43f), d = new(0.59f), f = new(0.14f), one = Vector<float>.One;
            for (var k = 0; k < v.Length; k++)
            {
                var x = Vector.Max(v[k] * e, Vector<float>.Zero);
                var y = x * (a * x + b) / (x * (c * x + d) + f);
                v[k] = Vector.SquareRoot(Vector.Min(y, one));
            }
            i = v.Length * Vector<float>.Count;
        }
        for (; i < n; i++)
        {
            var x = MathF.Max(0, s[i] * exposure);
            s[i] = MathF.Sqrt(MathF.Min(1, x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f)));
        }
        var px = n / 3;
        for (var p = 0; p < px; p++)
        {
            rgba[p * 4] = (byte)(s[p * 3] * 255 + 0.5f);
            rgba[p * 4 + 1] = (byte)(s[p * 3 + 1] * 255 + 0.5f);
            rgba[p * 4 + 2] = (byte)(s[p * 3 + 2] * 255 + 0.5f);
            rgba[p * 4 + 3] = 255;
        }
    }

    /// <summary>PSNR(dB · 값은 [0, peak] 클램프).</summary>
    public static double Psnr(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float peak = 1f)
    {
        var n = Math.Min(a.Length, b.Length);
        double mse = 0;
        for (var i = 0; i < n; i++)
        {
            var d = Math.Clamp(a[i], 0f, peak) - Math.Clamp(b[i], 0f, peak);
            mse += d * d;
        }
        mse /= Math.Max(1, n);
        return mse <= 1e-12 ? 120 : 10 * Math.Log10(peak * peak / mse);
    }
}
