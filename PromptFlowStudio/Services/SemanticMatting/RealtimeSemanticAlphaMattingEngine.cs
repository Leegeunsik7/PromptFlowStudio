using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.SemanticMatting;

/// <summary>[PromptFlow Studio v17.08.0] 시맨틱 클래스(세그멘테이션 네트워크 출력 · 결선점 = <see cref="RealtimeSemanticAlphaMattingEngine.Process"/>의 prior/cls 입력).</summary>
public enum MatteClass : byte { Background = 0, Person = 1, Hair = 2, Transparent = 3 }

/// <summary>트라이맵 값(0 = 확정 배경 · 64 = 반투명 · 128 = 미지 · 255 = 확정 전경).</summary>
public static class Trimap
{
    public const byte Bg = 0, Translucent = 64, Unknown = 128, Fg = 255;
}

/// <summary>
/// [PromptFlow Studio v17.08.0] 매팅 SIMD 커널(스칼라 쌍둥이와 비트 동일 · FMA 축약 없음).
/// 합성 방정식 I = αF + (1−α)B 의 α 사영: a = clamp(⟨I−B, F−B⟩ / (|F−B|² + ε)) · 신뢰도 c = |F−B|² / (|F−B|² + τ) · α = prior + c(a − prior).
/// </summary>
public static class MatteSimd
{
    public const float Eps = 1e-4f;

    public static void Solve(ReadOnlySpan<float> ir, ReadOnlySpan<float> ig, ReadOnlySpan<float> ib,
                             ReadOnlySpan<float> br, ReadOnlySpan<float> bg, ReadOnlySpan<float> bb,
                             ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb,
                             ReadOnlySpan<float> prior, float tau, Span<float> dst)
    {
        var n = dst.Length;
        var i = 0;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && n >= Vector<float>.Count)
        {
            var w = Vector<float>.Count;
            var eps = new Vector<float>(Eps); var vt = new Vector<float>(tau); var one = Vector<float>.One;
            ref var rir = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(ir));
            ref var rig = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(ig));
            ref var rib = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(ib));
            ref var rbr = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(br));
            ref var rbg = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(bg));
            ref var rbb = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(bb));
            ref var rfr = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(fr));
            ref var rfg = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(fg));
            ref var rfb = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(fb));
            ref var rp = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(prior));
            ref var rd = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(dst));
            for (; i <= n - w; i += w)
            {
                var o = (nint)(i * 4);
                var vbr = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rbr, o));
                var vbg = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rbg, o));
                var vbb = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rbb, o));
                var xr = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rir, o)) - vbr;
                var xg = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rig, o)) - vbg;
                var xb = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rib, o)) - vbb;
                var dr = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rfr, o)) - vbr;
                var dg = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rfg, o)) - vbg;
                var db = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rfb, o)) - vbb;
                var num = xr * dr + xg * dg + xb * db;
                var den = dr * dr + dg * dg + db * db;
                var a = Vector.Min(one, Vector.Max(Vector<float>.Zero, num / (den + eps)));
                var c = den / (den + vt);
                var p = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rp, o));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rd, o), p + c * (a - p));
            }
        }
        for (; i < n; i++) dst[i] = One(ir[i], ig[i], ib[i], br[i], bg[i], bb[i], fr[i], fg[i], fb[i], prior[i], tau);
    }

    public static void SolveScalar(ReadOnlySpan<float> ir, ReadOnlySpan<float> ig, ReadOnlySpan<float> ib,
                                   ReadOnlySpan<float> br, ReadOnlySpan<float> bg, ReadOnlySpan<float> bb,
                                   ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb,
                                   ReadOnlySpan<float> prior, float tau, Span<float> dst)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = One(ir[i], ig[i], ib[i], br[i], bg[i], bb[i], fr[i], fg[i], fb[i], prior[i], tau);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float One(float ir, float ig, float ib, float br, float bg, float bb, float fr, float fg, float fb, float p, float tau)
    {
        float xr = ir - br, xg = ig - bg, xb = ib - bb, dr = fr - br, dg = fg - bg, db = fb - bb;
        var num = xr * dr + xg * dg + xb * db;
        var den = dr * dr + dg * dg + db * db;
        var a = MathF.Min(1f, MathF.Max(0f, num / (den + Eps)));
        var c = den / (den + tau);
        return p + c * (a - p);
    }

    /// <summary>전경색 언믹스: Fc = B + (I − B)/α (α ≥ 0.05) → 국소 전경 추정 F와 신뢰 가중 블렌드 · [0,1] 클램프.</summary>
    public static void Unmix(ReadOnlySpan<float> i, ReadOnlySpan<float> b, ReadOnlySpan<float> f, ReadOnlySpan<float> alpha, Span<float> dst)
    {
        var n = dst.Length;
        var k = 0;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && n >= Vector<float>.Count)
        {
            var w = Vector<float>.Count;
            var lo = new Vector<float>(0.05f); var two = new Vector<float>(2f); var one = Vector<float>.One;
            ref var ri = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(i));
            ref var rb = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(b));
            ref var rf = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(f));
            ref var ra = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(alpha));
            ref var rd = ref Unsafe.As<float, byte>(ref MemoryMarshal.GetReference(dst));
            for (; k <= n - w; k += w)
            {
                var o = (nint)(k * 4);
                var vb = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rb, o));
                var vf = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref rf, o));
                var va = Vector.Max(lo, Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref ra, o)));
                var fc = Vector.Min(one, Vector.Max(Vector<float>.Zero, vb + (Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.Add(ref ri, o)) - vb) / va));
                var t = Vector.Min(one, (va - lo) * two);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rd, o), vf + t * (fc - vf));
            }
        }
        for (; k < n; k++) dst[k] = UnmixOne(i[k], b[k], f[k], alpha[k]);
    }

    public static void UnmixScalar(ReadOnlySpan<float> i, ReadOnlySpan<float> b, ReadOnlySpan<float> f, ReadOnlySpan<float> alpha, Span<float> dst)
    {
        for (var k = 0; k < dst.Length; k++) dst[k] = UnmixOne(i[k], b[k], f[k], alpha[k]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float UnmixOne(float i, float b, float f, float alpha)
    {
        var a = MathF.Max(0.05f, alpha);
        var fc = MathF.Min(1f, MathF.Max(0f, b + (i - b) / a));
        var t = MathF.Min(1f, (a - 0.05f) * 2f);
        return f + t * (fc - f);
    }
}

/// <summary>
/// [PromptFlow Studio v17.08.0] 🎭 실시간 마커리스 시맨틱 알파 매팅 엔진(그린스크린 없음 · 무할당).
/// 시맨틱 prior(세그멘테이션 확률 + 클래스) → 적분 영상 침식으로 트라이맵(머리카락 클래스 = 넓은 미지 대역 · 투명 클래스 = 반투명 영역) →
/// 클린 플레이트 B(확정 배경에서 적응 갱신) · 확정 전경 정규화 박스 전파로 국소 전경색 F(머리카락은 머리카락 클래스 전경 우선) →
/// SIMD α 사영(신뢰도 가중 prior 폴백) · 반투명 영역은 영역 회귀 I = c + k·B 로 α = 1−k · 틴트 F = c/α 추정 → SIMD 전경색 언믹스.
/// </summary>
public sealed class RealtimeSemanticAlphaMattingEngine
{
    public const int DefaultWidth = 160, DefaultHeight = 96;

    private readonly int _w, _h, _n;
    private readonly float[] _br, _bg, _bb, _fr, _fg, _fb, _a, _prevA, _pir, _pig, _pib, _sr, _sg, _sb, _tmp;
    private readonly byte[] _tri;
    private readonly int[] _ifg, _ibg;
    private readonly double[] _iw, _ir, _ig, _ib, _hw, _hr, _hg, _hb;
    private bool _hasPrev;

    public RealtimeSemanticAlphaMattingEngine(int width = DefaultWidth, int height = DefaultHeight)
    {
        _w = width; _h = height; _n = width * height;
        _br = new float[_n]; _bg = new float[_n]; _bb = new float[_n];
        _fr = new float[_n]; _fg = new float[_n]; _fb = new float[_n];
        _sr = new float[_n]; _sg = new float[_n]; _sb = new float[_n];
        _a = new float[_n]; _prevA = new float[_n]; _pir = new float[_n]; _pig = new float[_n]; _pib = new float[_n]; _tmp = new float[_n];
        _tri = new byte[_n];
        var ni = (width + 1) * (height + 1);
        _ifg = new int[ni]; _ibg = new int[ni];
        _iw = new double[ni]; _ir = new double[ni]; _ig = new double[ni]; _ib = new double[ni];
        _hw = new double[ni]; _hr = new double[ni]; _hg = new double[ni]; _hb = new double[ni];
    }

    public int Width => _w;
    public int Height => _h;
    /// <summary>일반 경계 미지 대역 반경(px).</summary>
    public int Band { get; set; } = 3;
    /// <summary>머리카락 클래스 미지 대역 반경(px).</summary>
    public int HairBand { get; set; } = 6;
    /// <summary>국소 전경색 전파 반경(px).</summary>
    public int ColorRadius { get; set; } = 5;
    /// <summary>공유 표본 방향 탐색 최대 거리(px).</summary>
    public int MaxSearch { get; set; } = 14;
    /// <summary>α 사영 신뢰도 τ(|F−B|² 가 τ보다 작으면 prior 폴백).</summary>
    public float Tau { get; set; } = 0.004f;
    /// <summary>미지 영역 시간 평활(정지 픽셀만).</summary>
    public float Temporal { get; set; } = 0.35f;
    /// <summary>확정 배경에서의 클린 플레이트 적응률.</summary>
    public float PlateAdapt { get; set; } = 0.06f;

    public ReadOnlySpan<float> Alpha => _a;
    public ReadOnlySpan<float> ForeR => _fr;
    public ReadOnlySpan<float> ForeG => _fg;
    public ReadOnlySpan<float> ForeB => _fb;
    public ReadOnlySpan<float> PlateR => _br;
    public ReadOnlySpan<float> PlateG => _bg;
    public ReadOnlySpan<float> PlateB => _bb;
    public ReadOnlySpan<byte> Tri => _tri;
    public bool PlateCaptured { get; private set; }
    public long Frames { get; private set; }
    public int FgPixels { get; private set; }
    public int UnknownPixels { get; private set; }
    public int TranslucentPixels { get; private set; }
    public int LowConfidencePixels { get; private set; }
    /// <summary>반투명 영역 회귀 추정 α · 틴트(유효 = TranslucentValid).</summary>
    public float GlassAlpha { get; private set; }
    public float GlassR { get; private set; }
    public float GlassG { get; private set; }
    public float GlassB { get; private set; }
    public bool TranslucentValid { get; private set; }
    public double LastUs { get; private set; }

    /// <summary>클린 플레이트 취득(피사체 없는 로크오프 프레임).</summary>
    public void CapturePlate(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b)
    {
        r.CopyTo(_br); g.CopyTo(_bg); b.CopyTo(_bb);
        PlateCaptured = true;
        _hasPrev = false;
    }

    /// <summary>1프레임 매팅(무할당). prior = 전경 확률[0,1] · cls = 시맨틱 클래스.</summary>
    public void Process(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> prior, ReadOnlySpan<byte> cls)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!PlateCaptured) CapturePlate(r, g, b);
        BuildTrimap(prior, cls);
        TrackExposure(r, g, b);
        PropagateColor(r, g, b, cls);
        MatteSimd.Solve(r, g, b, _br, _bg, _bb, _sr, _sg, _sb, prior, Tau, _a);
        SolveTranslucent(r, g, b, cls);
        Finish(r, g, b, prior);
        MatteSimd.Unmix(r, _br, _sr, _a, _tmp); Select(_fr, r, _tmp, Trimap.Translucent, GlassR);
        MatteSimd.Unmix(g, _bg, _sg, _a, _tmp); Select(_fg, g, _tmp, Trimap.Translucent, GlassG);
        MatteSimd.Unmix(b, _bb, _sb, _a, _tmp); Select(_fb, b, _tmp, Trimap.Translucent, GlassB);
        AdaptPlate(r, g, b);
        r.CopyTo(_pir); g.CopyTo(_pig); b.CopyTo(_pib); _a.AsSpan().CopyTo(_prevA);
        _hasPrev = true;
        Frames++;
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static int Box(int[] ii, int stride, int x0, int y0, int x1, int y1) => ii[y1 * stride + x1] - ii[y0 * stride + x1] - ii[y1 * stride + x0] + ii[y0 * stride + x0];
    private static double Box(double[] ii, int stride, int x0, int y0, int x1, int y1) => ii[y1 * stride + x1] - ii[y0 * stride + x1] - ii[y1 * stride + x0] + ii[y0 * stride + x0];

    /// <summary>트라이맵: 전경/배경 마스크 적분 영상 → 반경 r 정사각 침식(클래스별 r).</summary>
    private void BuildTrimap(ReadOnlySpan<float> prior, ReadOnlySpan<byte> cls)
    {
        int w = _w, h = _h, s = w + 1;
        for (var y = 0; y < h; y++)
        {
            int rf = 0, rb = 0;
            for (var x = 0; x < w; x++)
            {
                var fgp = prior[y * w + x] >= 0.5f;
                if (fgp) rf++; else rb++;
                _ifg[(y + 1) * s + x + 1] = _ifg[y * s + x + 1] + rf;
                _ibg[(y + 1) * s + x + 1] = _ibg[y * s + x + 1] + rb;
            }
        }
        int nf = 0, nu = 0, nt = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var c = (MatteClass)cls[i];
                // 머리카락 가닥은 세그멘테이션 바깥으로 뻗음 → 배경 쪽 침식만 HairBand(전경 쪽은 Band).
                int r = Band, rb = c == MatteClass.Hair ? HairBand : Band;
                int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(w, x + r + 1), y1 = Math.Min(h, y + r + 1);
                int bx0 = Math.Max(0, x - rb), by0 = Math.Max(0, y - rb), bx1 = Math.Min(w, x + rb + 1), by1 = Math.Min(h, y + rb + 1);
                byte t;
                if (c == MatteClass.Transparent && prior[i] >= 0.5f) t = Trimap.Translucent;
                else if (Box(_ifg, s, x0, y0, x1, y1) == (x1 - x0) * (y1 - y0)) t = Trimap.Fg;
                else if (Box(_ibg, s, bx0, by0, bx1, by1) == (bx1 - bx0) * (by1 - by0)) t = Trimap.Bg;
                else t = Trimap.Unknown;
                _tri[i] = t;
                if (t == Trimap.Fg) nf++; else if (t == Trimap.Unknown) nu++; else if (t == Trimap.Translucent) nt++;
            }
        FgPixels = nf; UnknownPixels = nu; TranslucentPixels = nt;
    }

    /// <summary>국소 전경색: 확정 전경 정규화 박스 평균(머리카락 클래스 픽셀은 머리카락 전경 우선 · 없으면 확장 반경 → 전역 평균).</summary>
    private void PropagateColor(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<byte> cls)
    {
        int w = _w, h = _h, s = w + 1;
        double gw = 0, gr = 0, gg = 0, gb = 0;
        for (var y = 0; y < h; y++)
        {
            double aw = 0, ar = 0, ag = 0, ab = 0, hw = 0, hr = 0, hg = 0, hb = 0;
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (_tri[i] == Trimap.Fg)
                {
                    aw += 1; ar += r[i]; ag += g[i]; ab += b[i];
                    if (cls[i] == (byte)MatteClass.Hair) { hw += 1; hr += r[i]; hg += g[i]; hb += b[i]; }
                }
                int o = (y + 1) * s + x + 1, u = y * s + x + 1;
                _iw[o] = _iw[u] + aw; _ir[o] = _ir[u] + ar; _ig[o] = _ig[u] + ag; _ib[o] = _ib[u] + ab;
                _hw[o] = _hw[u] + hw; _hr[o] = _hr[u] + hr; _hg[o] = _hg[u] + hg; _hb[o] = _hb[u] + hb;
            }
            gw += aw; gr += ar; gg += ag; gb += ab;
        }
        float mr = gw > 0 ? (float)(gr / gw) : 0.5f, mg = gw > 0 ? (float)(gg / gw) : 0.5f, mb = gw > 0 ? (float)(gb / gw) : 0.5f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (_tri[i] != Trimap.Unknown) { _sr[i] = r[i]; _sg[i] = g[i]; _sb[i] = b[i]; continue; }
                var done = false;
                for (var pass = 0; pass < 2 && !done; pass++)
                {
                    var rad = pass == 0 ? ColorRadius : ColorRadius * 3;
                    int x0 = Math.Max(0, x - rad), y0 = Math.Max(0, y - rad), x1 = Math.Min(w, x + rad + 1), y1 = Math.Min(h, y + rad + 1);
                    if (cls[i] == (byte)MatteClass.Hair)
                    {
                        var hwc = Box(_hw, s, x0, y0, x1, y1);
                        if (hwc >= 1)
                        {
                            _sr[i] = (float)(Box(_hr, s, x0, y0, x1, y1) / hwc); _sg[i] = (float)(Box(_hg, s, x0, y0, x1, y1) / hwc); _sb[i] = (float)(Box(_hb, s, x0, y0, x1, y1) / hwc);
                            done = true; continue;
                        }
                    }
                    var wc = Box(_iw, s, x0, y0, x1, y1);
                    if (wc >= 1)
                    {
                        _sr[i] = (float)(Box(_ir, s, x0, y0, x1, y1) / wc); _sg[i] = (float)(Box(_ig, s, x0, y0, x1, y1) / wc); _sb[i] = (float)(Box(_ib, s, x0, y0, x1, y1) / wc);
                        done = true;
                    }
                }
                if (!done) { _sr[i] = mr; _sg[i] = mg; _sb[i] = mb; }
                SampleBest(r, g, b, x, y, i);
            }
    }

    private static readonly sbyte[] DirX = { 1, -1, 0, 0, 1, 1, -1, -1, 2, 2, -2, -2, 1, -1, 1, -1 };
    private static readonly sbyte[] DirY = { 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 2, 2, -2, -2 };

    /// <summary>
    /// 공유 표본 선택: 16방향으로 최초 확정 전경 픽셀을 후보로 수집 → 후보별 α 사영 후 합성 잔차 |I − (αF + (1−α)B)|² 최소 후보 채택
    /// (다색 피사체 · 얼굴/머리카락 경계에서 평균 전경색 오염 방지). 후보 없으면 박스 전파값 유지.
    /// </summary>
    private void SampleBest(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, int x, int y, int i)
    {
        float ir = r[i], ig = g[i], ib = b[i], br = _br[i], bg = _bg[i], bb = _bb[i];
        float best = Residual(ir, ig, ib, br, bg, bb, _sr[i], _sg[i], _sb[i]) + 2e-4f, cr = _sr[i], cg = _sg[i], cb = _sb[i];
        for (var d = 0; d < DirX.Length; d++)
        {
            int dx = DirX[d], dy = DirY[d];
            for (var k = 1; k <= MaxSearch; k++)
            {
                int sx = x + dx * k, sy = y + dy * k;
                if ((uint)sx >= (uint)_w || (uint)sy >= (uint)_h) break;
                var j = sy * _w + sx;
                if (_tri[j] == Trimap.Bg) break;
                if (_tri[j] != Trimap.Fg) continue;
                var e = Residual(ir, ig, ib, br, bg, bb, r[j], g[j], b[j]) + 1e-5f * k;
                if (e < best) { best = e; cr = r[j]; cg = g[j]; cb = b[j]; }
                break;
            }
        }
        _sr[i] = cr; _sg[i] = cg; _sb[i] = cb;
    }

    private static float Residual(float ir, float ig, float ib, float br, float bg, float bb, float fr, float fg, float fb)
    {
        float xr = ir - br, xg = ig - bg, xb = ib - bb, dr = fr - br, dg = fg - bg, db = fb - bb;
        var a = Math.Clamp((xr * dr + xg * dg + xb * db) / (dr * dr + dg * dg + db * db + MatteSimd.Eps), 0f, 1f);
        float er = xr - a * dr, eg = xg - a * dg, eb = xb - a * db;
        return er * er + eg * eg + eb * eb;
    }

    /// <summary>반투명 영역 회귀: 채널 공유 기울기 k = Σcov(I,B)/Σvar(B) · c = Ī − kB̄ → α = 1 − k · F = c/α. 픽셀 α는 추정 틴트로 사영.</summary>
    private void SolveTranslucent(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<byte> cls)
    {
        double n = 0, sir = 0, sig = 0, sib = 0, sbr = 0, sbg = 0, sbb = 0;
        for (var i = 0; i < _n; i++)
        {
            if (_tri[i] != Trimap.Translucent) continue;
            n++; sir += r[i]; sig += g[i]; sib += b[i]; sbr += _br[i]; sbg += _bg[i]; sbb += _bb[i];
        }
        TranslucentValid = false;
        if (n < 8) return;
        double mir = sir / n, mig = sig / n, mib = sib / n, mbr = sbr / n, mbg = sbg / n, mbb = sbb / n, cov = 0, vbg = 0;
        for (var i = 0; i < _n; i++)
        {
            if (_tri[i] != Trimap.Translucent) continue;
            double dr = _br[i] - mbr, dg = _bg[i] - mbg, db = _bb[i] - mbb;
            cov += (r[i] - mir) * dr + (g[i] - mig) * dg + (b[i] - mib) * db;
            vbg += dr * dr + dg * dg + db * db;
        }
        if (vbg < 1e-4 * n) return;   // 뒤 배경이 평탄 — 식별 불가
        var k = Math.Clamp(cov / vbg, 0.0, 0.98);
        var al = 1.0 - k;
        GlassAlpha = (float)al;
        GlassR = (float)Math.Clamp((mir - k * mbr) / al, 0, 1); GlassG = (float)Math.Clamp((mig - k * mbg) / al, 0, 1); GlassB = (float)Math.Clamp((mib - k * mbb) / al, 0, 1);
        TranslucentValid = true;
        for (var i = 0; i < _n; i++)
        {
            if (_tri[i] != Trimap.Translucent) continue;
            float xr = r[i] - _br[i], xg = g[i] - _bg[i], xb = b[i] - _bb[i], dr = GlassR - _br[i], dg = GlassG - _bg[i], db = GlassB - _bb[i];
            var den = dr * dr + dg * dg + db * db;
            _a[i] = den < Tau ? GlassAlpha : Math.Clamp((xr * dr + xg * dg + xb * db) / (den + MatteSimd.Eps), 0f, 1f);
        }
    }

    /// <summary>트라이맵 확정 · 미지 영역 저신뢰 집계 · 정지 픽셀 시간 평활.</summary>
    private void Finish(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> prior)
    {
        var low = 0;
        for (var i = 0; i < _n; i++)
        {
            var t = _tri[i];
            if (t == Trimap.Fg) { _a[i] = 1f; continue; }
            if (t == Trimap.Bg) { _a[i] = 0f; continue; }
            if (t == Trimap.Translucent && !TranslucentValid) _a[i] = prior[i] * 0.5f;
            if (t == Trimap.Unknown)
            {
                float dr = _sr[i] - _br[i], dg = _sg[i] - _bg[i], db = _sb[i] - _bb[i];
                if (dr * dr + dg * dg + db * db < Tau) low++;
            }
            if (_hasPrev && Temporal > 0)
            {
                float mr = r[i] - _pir[i], mg = g[i] - _pig[i], mb = b[i] - _pib[i];
                if (mr * mr + mg * mg + mb * mb < 4e-4f) _a[i] += Temporal * (_prevA[i] - _a[i]);
            }
        }
        LowConfidencePixels = low;
    }

    private void Select(float[] dst, ReadOnlySpan<float> src, ReadOnlySpan<float> unmixed, byte translucent, float tint)
    {
        for (var i = 0; i < _n; i++)
        {
            var t = _tri[i];
            dst[i] = t == Trimap.Fg ? src[i] : t == Trimap.Bg ? 0f : t == translucent ? tint : unmixed[i];
        }
    }

    /// <summary>전역 노출 추적: 확정 배경 최소제곱 이득 g = ΣI·B / ΣB·B → 플레이트 전체(가려진 픽셀 포함) 스케일.</summary>
    private void TrackExposure(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b)
    {
        double ib = 0, bb = 0;
        for (var i = 0; i < _n; i++)
        {
            if (_tri[i] != Trimap.Bg) continue;
            ib += r[i] * _br[i] + g[i] * _bg[i] + b[i] * _bb[i];
            bb += _br[i] * _br[i] + _bg[i] * _bg[i] + _bb[i] * _bb[i];
        }
        if (bb < 1.0) return;
        var k = (float)(ib / bb);
        ExposureGain = k;
        if (MathF.Abs(k - 1f) < 0.003f || k < 0.25f || k > 4f) return;
        for (var i = 0; i < _n; i++) { _br[i] *= k; _bg[i] *= k; _bb[i] *= k; }
        ExposureCorrections++;
    }

    public float ExposureGain { get; private set; } = 1f;
    public long ExposureCorrections { get; private set; }

    private void AdaptPlate(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b)
    {
        var k = PlateAdapt;
        for (var i = 0; i < _n; i++)
        {
            if (_tri[i] != Trimap.Bg) continue;
            _br[i] += k * (r[i] - _br[i]); _bg[i] += k * (g[i] - _bg[i]); _bb[i] += k * (b[i] - _bb[i]);
        }
    }

    public void ResetPlate() { PlateCaptured = false; _hasPrev = false; }
}
