using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.SpatialXr;

public enum ReprojectionMode : byte { None, Atw, Ptw }

/// <summary>
/// 안구 레이캐스터(원격 렌더 서버 대역): 핸드 가드 소품 구 + 체커 지면 + 하늘 그라디언트 → RGB(0..1) + 거리(광선 길이 · 하늘 = 0). 무할당.
/// </summary>
public static class SpatialSceneRaycaster
{
    public static void Render(in XrEyeView eye, int w, int h, SpatialHandGestureHapticGuard props, Span<float> rgb, Span<float> dist)
    {
        var o = eye.Position;
        var n = props.PropCount;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var d = OpenXrWebXrSpatialTrackingEngine.PixelRay(eye, x + 0.5f, y + 0.5f, w, h);
                var best = float.MaxValue; var hit = -1;
                for (var i = 0; i < n; i++)
                {
                    var oc = o - props.PropCenter(i);
                    var r = props.PropRadius(i);
                    var b = Vector3.Dot(oc, d); var c = oc.LengthSquared() - r * r;
                    var disc = b * b - c;
                    if (disc < 0) continue;
                    var t = -b - MathF.Sqrt(disc);
                    if (t > 1e-3f && t < best) { best = t; hit = i; }
                }
                Vector3 col;
                var tg = d.Y < -1e-4f ? -o.Y / d.Y : float.MaxValue;
                if (hit >= 0 && best < tg)
                {
                    var p = o + d * best;
                    var nrm = Vector3.Normalize(p - props.PropCenter(hit));
                    var hue = (hit * 0.37f) % 1f;
                    var baseC = new Vector3(0.55f + 0.45f * MathF.Cos(6.283f * hue), 0.55f + 0.45f * MathF.Cos(6.283f * (hue + 0.33f)), 0.55f + 0.45f * MathF.Cos(6.283f * (hue + 0.67f)));
                    var lam = Math.Max(0.15f, Vector3.Dot(nrm, Vector3.Normalize(new Vector3(0.4f, 0.9f, 0.3f))));
                    col = baseC * lam;
                }
                else if (tg < 200f)
                {
                    best = tg; hit = int.MaxValue;
                    var p = o + d * tg;
                    var chk = ((int)MathF.Floor(p.X * 2f) + (int)MathF.Floor(p.Z * 2f) & 1) == 0;
                    var fade = MathF.Exp(-tg * 0.08f);
                    col = Vector3.Lerp(new Vector3(0.75f, 0.8f, 0.88f), chk ? new Vector3(0.82f) : new Vector3(0.25f, 0.28f, 0.32f), fade);
                }
                else { best = 0; col = Vector3.Lerp(new Vector3(0.75f, 0.8f, 0.88f), new Vector3(0.25f, 0.45f, 0.85f), Math.Clamp(d.Y, 0f, 1f)); }
                var k = y * w + x;
                rgb[k * 3] = col.X; rgb[k * 3 + 1] = col.Y; rgb[k * 3 + 2] = col.Z;
                dist[k] = hit >= 0 ? best : 0f;
            }
    }
}

/// <summary>
/// [PromptFlow Studio v16.97.0] 🥽 시선 기반 포비에이티드 압축 &amp; 모션-투-포톤 재투영 합성기.
/// · 포비에이션: 8×8 타일 이심률(타일 내 시선 최근접 방향 각도) → 4단계(1/2/4/8 배 블록 평균 · 8/6/5/4 비트 양자화) · 부호화 바이트 실측 · 밀도 맵.
///   예산(Mbps ÷ fps) 피드백으로 영역 배율(FoveaScale) 적응.
/// · 재투영: 렌더 포즈 프레임 → 표시 포즈. ATW = 회전 호모그래피(행 단위 Vector&lt;float&gt; 탄젠트 변환) · PTW = 거리 버퍼 기반 시차 보정 2회 고정점 반복 · 쌍선형 샘플 · 프레임 밖 = 가장자리 클램프(홀 집계) · 후방 = 검정.
/// </summary>
public sealed class FoveatedCompressionReprojectionSynthesizer
{
    public const int Tile = 8, Levels = 4;
    public static readonly int[] Factor = { 1, 2, 4, 8 };
    public static readonly int[] Bits = { 8, 6, 5, 4 };
    public static readonly string[] ModeNames = { "없음", "ATW(회전)", "PTW(회전+위치)" };
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private float[][] _src = new float[2][], _dist = new float[2][], _dec = new float[2][], _out = new float[2][];
    private byte[][] _level = new byte[2][];
    private float[] _tanX = Array.Empty<float>(), _sx = Array.Empty<float>(), _sy = Array.Empty<float>(), _sw = Array.Empty<float>();
    private readonly XrEyeView[] _renderEye = new XrEyeView[2];
    private readonly long[] _bytes = new long[2];
    private readonly int[] _holes = new int[2];
    private readonly int[] _levelCount = new int[Levels];

    public FoveatedCompressionReprojectionSynthesizer(int w = 112, int h = 112) => Configure(w, h);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int TilesX => Width / Tile;
    public int TilesY => Height / Tile;
    public float FoveaDeg { get; set; } = 5f;
    public float MidDeg { get; set; } = 15f;
    public float FarDeg { get; set; } = 30f;
    public float FoveaScale { get; set; } = 1f;
    public bool Adaptive { get; set; } = true;
    public double BudgetMbps { get; set; } = 6;
    public double StreamFps { get; set; } = 30;
    public bool Foveated { get; set; } = true;
    public ReprojectionMode Mode { get; set; } = ReprojectionMode.Ptw;
    public double LastEncodeUs { get; private set; }
    public double LastReprojectUs { get; private set; }
    public long FullBytes => (long)Width * Height * 3;
    public long EncodedBytes(int eye) => _bytes[eye];
    public double CompressionRatio => (_bytes[0] + _bytes[1]) == 0 ? 1 : 2.0 * FullBytes / (_bytes[0] + _bytes[1]);
    public double FrameMbps => (_bytes[0] + _bytes[1]) * 8 * StreamFps / 1e6;
    public int Holes(int eye) => _holes[eye];
    public int LevelCount(int l) => _levelCount[l];
    public byte Level(int eye, int tx, int ty) => _level[eye][ty * TilesX + tx];
    public ref readonly XrEyeView RenderEye(int eye) => ref _renderEye[eye];

    public Span<float> Source(int eye) => _src[eye];
    public Span<float> Distance(int eye) => _dist[eye];
    public ReadOnlySpan<float> Decoded(int eye) => _dec[eye];
    public ReadOnlySpan<float> Output(int eye) => _out[eye];

    /// <summary>해상도 구성(타일 배수 정렬 · 콜드).</summary>
    public void Configure(int w, int h)
    {
        w = Math.Max(Tile, w / Tile * Tile); h = Math.Max(Tile, h / Tile * Tile);
        Width = w; Height = h;
        for (var e = 0; e < 2; e++)
        {
            _src[e] = new float[w * h * 3]; _dist[e] = new float[w * h]; _dec[e] = new float[w * h * 3]; _out[e] = new float[w * h * 3];
            _level[e] = new byte[(w / Tile) * (h / Tile)];
        }
        var pad = (w + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        _tanX = new float[pad]; _sx = new float[pad]; _sy = new float[pad]; _sw = new float[pad];
    }

    /// <summary>렌더 시점 안구 뷰 기록(재투영 기준).</summary>
    public void SetRenderEye(int eye, in XrEyeView v) => _renderEye[eye] = v;

    /// <summary>
    /// 포비에이티드 부호화/복호(무할당) → 부호화 바이트. gaze = 안구 공간 시선 방향(−z 전방).
    /// </summary>
    public long Encode(int eye, Vector3 gaze)
    {
        var t0 = Stopwatch.GetTimestamp();
        ref readonly var rv = ref _renderEye[eye];
        var g = Vector3.Normalize(gaze);
        if (g.Z > -0.05f) g = new Vector3(0, 0, -1);
        float gtx = g.X / -g.Z, gty = g.Y / -g.Z;
        var s = Math.Clamp(FoveaScale, 0.2f, 6f);
        float c0 = MathF.Cos(FoveaDeg * s * MathF.PI / 180f), c1 = MathF.Cos(MidDeg * s * MathF.PI / 180f), c2 = MathF.Cos(FarDeg * s * MathF.PI / 180f);
        var src = _src[eye]; var dec = _dec[eye]; var lv = _level[eye];
        int w = Width, tx = TilesX, ty = TilesY;
        if (eye == 0) Array.Clear(_levelCount);
        long bits = 0;
        for (var j = 0; j < ty; j++)
            for (var i = 0; i < tx; i++)
            {
                // 타일 내 시선 최근접 점(보수적: 타일 일부라도 중심와면 L0)
                float tx0 = rv.TanL + (rv.TanR - rv.TanL) * i * Tile / w, tx1 = rv.TanL + (rv.TanR - rv.TanL) * (i + 1) * Tile / w;
                float ty0 = rv.TanU - (rv.TanU - rv.TanD) * (j + 1) * Tile / Height, ty1 = rv.TanU - (rv.TanU - rv.TanD) * j * Tile / Height;
                var dir = Vector3.Normalize(new Vector3(Math.Clamp(gtx, tx0, tx1), Math.Clamp(gty, ty0, ty1), -1f));
                var cos = Vector3.Dot(dir, g);
                var l = !Foveated || cos >= c0 ? 0 : cos >= c1 ? 1 : cos >= c2 ? 2 : 3;
                lv[j * tx + i] = (byte)l;
                _levelCount[l]++;
                var f = Factor[l]; var q = (1 << Bits[l]) - 1; var iq = 1f / q;
                for (var by = 0; by < Tile; by += f)
                    for (var bx = 0; bx < Tile; bx += f)
                    {
                        float r = 0, gg = 0, b = 0;
                        for (var yy = 0; yy < f; yy++)
                        {
                            var row = ((j * Tile + by + yy) * w + i * Tile + bx) * 3;
                            for (var xx = 0; xx < f; xx++) { r += src[row + xx * 3]; gg += src[row + xx * 3 + 1]; b += src[row + xx * 3 + 2]; }
                        }
                        var inv = 1f / (f * f);
                        r = MathF.Round(Math.Clamp(r * inv, 0f, 1f) * q) * iq;
                        gg = MathF.Round(Math.Clamp(gg * inv, 0f, 1f) * q) * iq;
                        b = MathF.Round(Math.Clamp(b * inv, 0f, 1f) * q) * iq;
                        for (var yy = 0; yy < f; yy++)
                        {
                            var row = ((j * Tile + by + yy) * w + i * Tile + bx) * 3;
                            for (var xx = 0; xx < f; xx++) { dec[row + xx * 3] = r; dec[row + xx * 3 + 1] = gg; dec[row + xx * 3 + 2] = b; }
                        }
                        bits += 3L * Bits[l];
                    }
            }
        _bytes[eye] = (bits + 7) / 8;
        LastEncodeUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
        return _bytes[eye];
    }

    /// <summary>예산 피드백: 양안 바이트 vs Mbps÷fps → FoveaScale 조정.</summary>
    public void AdaptToBudget()
    {
        if (!Adaptive || !Foveated) return;
        var budget = BudgetMbps * 1e6 / 8 / Math.Max(1, StreamFps);
        var total = _bytes[0] + _bytes[1];
        if (total > budget) FoveaScale = Math.Max(0.2f, FoveaScale * 0.9f);
        else if (total < budget * 0.8) FoveaScale = Math.Min(6f, FoveaScale * 1.05f);
    }

    /// <summary>
    /// 재투영(무할당): 복호 프레임(렌더 안구) → 표시 안구. ATW 행 SIMD 호모그래피 · PTW 거리 기반 시차 2회 반복.
    /// </summary>
    public void Reproject(int eye, in XrEyeView display, ReprojectionMode mode)
    {
        var t0 = Stopwatch.GetTimestamp();
        var src = _dec[eye]; var dst = _out[eye]; var dist = _dist[eye];
        if (mode == ReprojectionMode.None) { src.AsSpan().CopyTo(dst); _holes[eye] = 0; LastReprojectUs = (Stopwatch.GetTimestamp() - t0) * TickUs; return; }
        ref readonly var rv = ref _renderEye[eye];
        int w = Width, h = Height;
        // 표시 안구 방향 → 렌더 안구 공간: d_r = d_d · R(conj(qr)·qd)
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(rv.Orientation) * display.Orientation);
        var tr = Vector3.Transform(display.Position - rv.Position, Quaternion.Conjugate(rv.Orientation));
        var ptw = mode == ReprojectionMode.Ptw && tr.LengthSquared() > 1e-10f;
        for (var x = 0; x < w; x++) _tanX[x] = display.TanL + (display.TanR - display.TanL) * (x + 0.5f) / w;
        var vtx = MemoryMarshal.Cast<float, Vector<float>>(_tanX.AsSpan());
        var vsx = MemoryMarshal.Cast<float, Vector<float>>(_sx.AsSpan()); var vsy = MemoryMarshal.Cast<float, Vector<float>>(_sy.AsSpan()); var vsw = MemoryMarshal.Cast<float, Vector<float>>(_sw.AsSpan());
        float rl = rv.TanL, rr = rv.TanR, ru = rv.TanU, rd = rv.TanD;
        var holes = 0;
        for (var y = 0; y < h; y++)
        {
            var ty = display.TanU - (display.TanU - display.TanD) * (y + 0.5f) / h;
            // d = (tx, ty, −1) · m → (dx, dy, dz) 행 SIMD
            Vector<float> m11 = new(m.M11), m12 = new(m.M12), m13 = new(m.M13);
            Vector<float> bx = new(ty * m.M21 - m.M31), by = new(ty * m.M22 - m.M32), bz = new(ty * m.M23 - m.M33);
            for (var k = 0; k < vtx.Length; k++)
            {
                var t = vtx[k];
                vsx[k] = t * m11 + bx; vsy[k] = t * m12 + by; vsw[k] = t * m13 + bz;
            }
            var orow = y * w * 3;
            for (var x = 0; x < w; x++)
            {
                float dx = _sx[x], dy = _sy[x], dz = _sw[x];
                if (dz >= -1e-6f) { Black(dst, orow + x * 3); holes++; continue; }
                var ux = dx / -dz; var uy = dy / -dz;
                if (ptw)
                {
                    var dn = Vector3.Normalize(new Vector3(dx, dy, dz));
                    for (var it = 0; it < 2; it++)
                    {
                        var spx = (ux - rl) / (rr - rl) * w - 0.5f; var spy = (ru - uy) / (ru - rd) * h - 0.5f;
                        var di = Nearest(dist, spx, spy, w, h);
                        if (di <= 0) break; // 하늘(무한) = 회전만
                        var p = tr + dn * di;
                        if (p.Z >= -1e-4f) break;
                        ux = p.X / -p.Z; uy = p.Y / -p.Z;
                    }
                }
                var sx = (ux - rl) / (rr - rl) * w - 0.5f; var sy = (ru - uy) / (ru - rd) * h - 0.5f;
                if (sx < -0.5f || sy < -0.5f || sx > w - 0.5f || sy > h - 0.5f) holes++; // 프레임 밖 = 가장자리 클램프 채움(홀 집계)
                Bilinear(src, sx, sy, w, h, dst, orow + x * 3);
            }
        }
        _holes[eye] = holes;
        LastReprojectUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    private static void Black(float[] d, int o) { d[o] = 0; d[o + 1] = 0; d[o + 2] = 0; }

    private static float Nearest(float[] dist, float x, float y, int w, int h)
    {
        var ix = Math.Clamp((int)(x + 0.5f), 0, w - 1); var iy = Math.Clamp((int)(y + 0.5f), 0, h - 1);
        return dist[iy * w + ix];
    }

    private static void Bilinear(float[] s, float x, float y, int w, int h, float[] d, int o)
    {
        x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = x - x0, fy = y - y0;
        for (var c = 0; c < 3; c++)
        {
            var a = s[(y0 * w + x0) * 3 + c] + (s[(y0 * w + x1) * 3 + c] - s[(y0 * w + x0) * 3 + c]) * fx;
            var b = s[(y1 * w + x0) * 3 + c] + (s[(y1 * w + x1) * 3 + c] - s[(y1 * w + x0) * 3 + c]) * fx;
            d[o + c] = a + (b - a) * fy;
        }
    }

    /// <summary>PSNR(dB · 0..1 신호) — region: 0 = 전체 · 1 = 중심와(L0) 타일 · 2 = 주변(L≥2) 타일.</summary>
    public double Psnr(int eye, ReadOnlySpan<float> a, ReadOnlySpan<float> b, int region = 0)
    {
        double se = 0; long n = 0;
        int w = Width, tx = TilesX;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < w; x++)
            {
                if (region != 0)
                {
                    var l = _level[eye][(y / Tile) * tx + x / Tile];
                    if (region == 1 ? l != 0 : l < 2) continue;
                }
                var k = (y * w + x) * 3;
                for (var c = 0; c < 3; c++) { var d = Math.Clamp(a[k + c], 0f, 1f) - Math.Clamp(b[k + c], 0f, 1f); se += d * d; }
                n += 3;
            }
        if (n == 0) return double.PositiveInfinity;
        var mse = se / n;
        return mse <= 1e-12 ? 99.0 : 10 * Math.Log10(1.0 / mse);
    }
}
