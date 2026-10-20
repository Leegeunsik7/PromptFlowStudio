using System.Numerics;
namespace PromptFlowStudio.Services.GenlockFrustum;

/// <summary>
/// [PromptFlow Studio v17.44.0] 🖥️ 인너/아우터 듀얼 프러스텀 스트림 동적 합성기(<see cref="Compose"/> 60 Hz 무할당).
/// LED 월 캔버스(24×4 패널 × 16 px = 384×64, θ/높이 언랩)를 두 스트림으로 채운다.
/// <b>인너</b>: 카메라 NDC 오버스캔 사각형(+ 스무스스텝 페더) 안쪽 픽셀마다 카메라 → 벽 픽셀 광선을 가상 세계(하늘 · 산맥 · 태양 ·
/// 지면 체커)에 시차 정합으로 투사(v17.43 렌즈 FoV/스퀴즈 일치 · 4K 목표 3840 텍셀), 엔진 모아레 블러 맵으로 4-이웃 프리블러.
/// <b>아우터</b>: 볼륨 중심 기준 64×32 등장방형 HDRI(저주파 하늘 + v17.41 무대 픽스처 방향 로브)를 3 슬롯 무할당 링버퍼에
/// 기록 · 게시하고 4×4 블록 단위 저해상도로 조명용 환경광을 공급한다.
/// 결합(SIMD): out = (outer + (inner − outer)·m) → 컬러 캘리브레이션 3×3 → 패널 시야각 게인.
/// </summary>
public sealed class InnerOuterFrustumDualStreamSynthesizer
{
    private const int Cols = RealtimeOffAxisFrustumProjectionEngine.Cols, Rows = RealtimeOffAxisFrustumProjectionEngine.Rows;
    public const int PxPerPanel = 16, W = Cols * PxPerPanel, H = Rows * PxPerPanel, Pixels = W * H, Block = 4, BlocksW = W / Block, BlocksH = H / Block;
    public const int HdriW = 64, HdriH = 32, HdriTexels = HdriW * HdriH, HdriSlots = 3, MaxLights = 32, InnerTargetW = 3840;
    public const float LobeExp = 24f, AmbientGain = 1.6f, SkyGain = 0.35f;
    public static readonly float LedPxPerCanvasPx = RealtimeOffAxisFrustumProjectionEngine.ChordM * 1000f / RealtimeOffAxisFrustumProjectionEngine.PitchMm / PxPerPanel;
    public static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.35f, 0.22f, -1f));
    /// <summary>LED 원색 → 카메라 센서 캘리브레이션(행 합 1 — 백색 보존).</summary>
    public static readonly float[] CalibratedMatrix = { 1.052f, -0.041f, -0.011f, -0.018f, 1.034f, -0.016f, 0.004f, -0.062f, 1.058f };
    private static readonly float[] Identity = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };

    private readonly float[] _sx = new float[W], _sz = new float[W], _theta = new float[W], _wy = new float[H];
    private readonly int[] _blockTexel = new int[BlocksW * BlocksH];
    private readonly float[] _hdx = new float[HdriTexels], _hdy = new float[HdriTexels], _hdz = new float[HdriTexels];
    private readonly float[] _ring = new float[HdriSlots * HdriTexels * 3];
    private readonly float[] _ir = new float[Pixels], _ig = new float[Pixels], _ib = new float[Pixels];
    private readonly float[] _tr = new float[Pixels], _tg = new float[Pixels], _tb = new float[Pixels];
    private readonly float[] _xr = new float[Pixels], _xg = new float[Pixels], _xb = new float[Pixels];
    private readonly float[] _m = new float[Pixels], _gp = new float[Pixels];
    private readonly float[] _or = new float[Pixels], _og = new float[Pixels], _ob = new float[Pixels];
    private readonly byte[] _rgb = new byte[Pixels * 3];
    private readonly Vector3[] _lp = new Vector3[MaxLights], _lc = new Vector3[MaxLights];
    private readonly float[] _li = new float[MaxLights];
    private int _lights, _published = -1;

    public InnerOuterFrustumDualStreamSynthesizer()
    {
        const float R = RealtimeOffAxisFrustumProjectionEngine.RadiusM;
        for (var x = 0; x < W; x++)
        {
            var th = -RealtimeOffAxisFrustumProjectionEngine.HalfArcRad + (x + 0.5f) / W * 2f * RealtimeOffAxisFrustumProjectionEngine.HalfArcRad;
            _theta[x] = th; _sx[x] = R * MathF.Sin(th); _sz[x] = -R * MathF.Cos(th);
        }
        for (var y = 0; y < H; y++) _wy[y] = RealtimeOffAxisFrustumProjectionEngine.WallHeightM * (1f - (y + 0.5f) / H);
        for (var v = 0; v < HdriH; v++)
        for (var u = 0; u < HdriW; u++)
        {
            var az = (u + 0.5f) / HdriW * MathF.Tau - MathF.PI;
            var el = MathF.PI * 0.5f - (v + 0.5f) / HdriH * MathF.PI;
            var i = v * HdriW + u;
            _hdx[i] = MathF.Sin(az) * MathF.Cos(el); _hdy[i] = MathF.Sin(el); _hdz[i] = -MathF.Cos(az) * MathF.Cos(el);
        }
        for (var by = 0; by < BlocksH; by++)
        for (var bx = 0; bx < BlocksW; bx++)
        {
            int cx = bx * Block + Block / 2, cy = by * Block + Block / 2;
            var el = MathF.Atan2(_wy[cy] - 0.5f * RealtimeOffAxisFrustumProjectionEngine.WallHeightM, R);
            var u = Math.Clamp((int)((_theta[cx] + MathF.PI) / MathF.Tau * HdriW), 0, HdriW - 1);
            var v = Math.Clamp((int)((MathF.PI * 0.5f - el) / MathF.PI * HdriH), 0, HdriH - 1);
            _blockTexel[by * BlocksW + bx] = v * HdriW + u;
        }
    }

    public bool UseSimd { get; set; } = true;
    public bool Calibrated { get; set; } = true;
    public bool MoireBlur { get; set; } = true;
    public float Overscan { get; set; } = 0.1f;
    public float Feather { get; set; } = 0.08f;
    public float InnerExposure { get; set; } = 1f;
    /// <summary>v17.55 인너 프러스텀 외부 장면(3DGS 등) — 커버리지만큼 절차적 월드 위에 합성.</summary>
    public IDirectionalSceneSampler? InnerScene { get; set; }
    public long InnerSceneHits { get; private set; }
    public float OuterExposure { get; set; } = 1f;
    public int LightCount => _lights;
    public int PublishedSlot => _published;
    public long OuterFrames { get; private set; }
    public long InnerFrames { get; private set; }
    public int InnerX0 { get; private set; }
    public int InnerX1 { get; private set; }
    public int InnerYp0 { get; private set; }
    public int InnerYp1 { get; private set; }
    public int InnerPixels { get; private set; }
    public float InnerCoverage { get; private set; }
    public float InnerLedPx { get; private set; }
    public float TexelsPerLedPx { get; private set; }
    public float OuterMeanLum { get; private set; }
    public float HdriPeak { get; private set; }
    public float BlurredPixels { get; private set; }
    public double LastInnerUs { get; private set; }
    public double LastOuterUs { get; private set; }
    public double LastComposeUs { get; private set; }

    public ReadOnlySpan<float> OutR => _or;
    public ReadOnlySpan<float> OutG => _og;
    public ReadOnlySpan<float> OutB => _ob;
    public ReadOnlySpan<float> Mask => _m;
    public ReadOnlySpan<float> InnerR => _ir;
    public ReadOnlySpan<float> OuterR => _xr;
    public ReadOnlySpan<byte> Display => _rgb;
    public float Matrix(int i) => (Calibrated ? CalibratedMatrix : Identity)[i];
    /// <summary>게시된 HDRI 슬롯의 텍셀(선형 RGB).</summary>
    public Vector3 Hdri(int texel)
    {
        if (_published < 0) return Vector3.Zero;
        var o = (_published * HdriTexels + texel) * 3;
        return new Vector3(_ring[o], _ring[o + 1], _ring[o + 2]);
    }
    public static int CanvasIndex(int col, int row, int px, int py) => (row * PxPerPanel + py) * W + col * PxPerPanel + px;

    // ══════════════════════ 광원(v17.41) ══════════════════════
    public void BeginLights() => _lights = 0;
    public bool AddLight(Vector3 position, Vector3 color, float level)
    {
        if (_lights >= MaxLights || !(level > 0f)) return false;
        _lp[_lights] = position; _lc[_lights] = color; _li[_lights] = level; _lights++;
        return true;
    }

    // ══════════════════════ 가상 세계(인너 · 아우터 공용) ══════════════════════
    public static Vector3 SampleWorld(Vector3 origin, Vector3 dir, bool detail)
    {
        if (dir.Y < -1e-3f)
        {
            var t = -origin.Y / dir.Y;
            var g = origin + dir * t;
            var fog = MathF.Exp(-t * 0.012f);
            var horizon = new Vector3(0.62f, 0.55f, 0.48f);
            if (!detail) return Vector3.Lerp(horizon, new Vector3(0.36f, 0.27f, 0.18f), fog);
            var chk = (((int)MathF.Floor(g.X * 0.5f) + (int)MathF.Floor(g.Z * 0.5f)) & 1) == 0;
            var dune = 0.5f + 0.5f * MathF.Sin(g.X * 1.7f + MathF.Sin(g.Z * 0.9f) * 2f);
            var sand = (chk ? new Vector3(0.46f, 0.34f, 0.22f) : new Vector3(0.32f, 0.23f, 0.15f)) * (0.8f + 0.25f * dune);
            return Vector3.Lerp(horizon, sand, fog);
        }
        var az = MathF.Atan2(dir.X, -dir.Z);
        var el = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        var ridge = 0.06f + 0.035f * MathF.Sin(3f * az) + 0.02f * MathF.Sin(7f * az + 1f) + (detail ? 0.008f * MathF.Sin(23f * az) : 0f);
        if (el < ridge)
        {
            var k = el / ridge;
            return new Vector3(0.16f, 0.17f, 0.24f) * (0.6f + 0.4f * k);
        }
        var h = MathF.Max(dir.Y, 0f);
        var sky = Vector3.Lerp(new Vector3(0.95f, 0.72f, 0.5f), new Vector3(0.18f, 0.32f, 0.62f), MathF.Sqrt(h));
        var s = MathF.Max(Vector3.Dot(dir, SunDir), 0f);
        var sun = detail ? MathF.Pow(s, 1800f) * 30f : 0f;
        return sky + new Vector3(1f, 0.85f, 0.6f) * (sun + 0.6f * MathF.Pow(s, 16f));
    }

    // ══════════════════════ 아우터: HDRI 링버퍼 ══════════════════════
    public void BuildOuter()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var slot = (_published + 1) % HdriSlots;
        var o = slot * HdriTexels * 3;
        var center = new Vector3(0f, 0.5f * RealtimeOffAxisFrustumProjectionEngine.WallHeightM, 0f);
        var peak = 0f;
        for (var i = 0; i < HdriTexels; i++)
        {
            var d = new Vector3(_hdx[i], _hdy[i], _hdz[i]);
            var c = SampleWorld(center, d, false) * SkyGain;
            for (var k = 0; k < _lights; k++)
            {
                var ld = _lp[k] - center;
                var len = ld.Length();
                if (len < 1e-3f) continue;
                var cs = Vector3.Dot(d, ld / len);
                if (cs <= 0f) continue;
                c += _lc[k] * (_li[k] * AmbientGain * MathF.Pow(cs, LobeExp));
            }
            c *= OuterExposure;
            _ring[o + i * 3] = c.X; _ring[o + i * 3 + 1] = c.Y; _ring[o + i * 3 + 2] = c.Z;
            var lum = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
            if (lum > peak) peak = lum;
        }
        HdriPeak = peak;
        Volatile.Write(ref _published, slot);
        OuterFrames++;
        LastOuterUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ══════════════════════ 결합 ══════════════════════
    public void Compose(RealtimeOffAxisFrustumProjectionEngine engine)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_published < 0) BuildOuter();
        ExpandOuter(engine);
        RenderInner(engine);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Blend();
        Tone();
        LastComposeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastInnerUs = (t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void ExpandOuter(RealtimeOffAxisFrustumProjectionEngine engine)
    {
        var o = Volatile.Read(ref _published) * HdriTexels * 3;
        var sum = 0f;
        for (var by = 0; by < BlocksH; by++)
        for (var bx = 0; bx < BlocksW; bx++)
        {
            var ti = o + _blockTexel[by * BlocksW + bx] * 3;
            float r = _ring[ti], g = _ring[ti + 1], b = _ring[ti + 2];
            sum += 0.2126f * r + 0.7152f * g + 0.0722f * b;
            for (var y = 0; y < Block; y++)
            {
                var row = (by * Block + y) * W + bx * Block;
                for (var x = 0; x < Block; x++) { _xr[row + x] = r; _xg[row + x] = g; _xb[row + x] = b; }
            }
        }
        OuterMeanLum = sum / (BlocksW * BlocksH);
        for (var p = 0; p < RealtimeOffAxisFrustumProjectionEngine.Panels; p++)
        {
            int col = p % Cols, row = p / Cols;
            var g = engine.Gain(p);
            for (var y = 0; y < PxPerPanel; y++) _gp.AsSpan(CanvasIndex(col, row, 0, y), PxPerPanel).Fill(g);
        }
    }

    private void RenderInner(RealtimeOffAxisFrustumProjectionEngine engine)
    {
        Array.Clear(_m);
        InnerPixels = 0; InnerCoverage = 0f; InnerLedPx = 0f; TexelsPerLedPx = 0f; BlurredPixels = 0f;
        if (!engine.InnerValid) { InnerX0 = InnerX1 = InnerYp0 = InnerYp1 = 0; return; }
        ref readonly var pose = ref engine.Pose;
        var span = 2f * RealtimeOffAxisFrustumProjectionEngine.HalfArcRad;
        var x0 = Math.Clamp((int)MathF.Floor((engine.InnerTheta0 + RealtimeOffAxisFrustumProjectionEngine.HalfArcRad) / span * W) - 1, 0, W - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling((engine.InnerTheta1 + RealtimeOffAxisFrustumProjectionEngine.HalfArcRad) / span * W) + 1, 0, W - 1);
        var y0 = Math.Clamp((int)MathF.Floor((1f - engine.InnerY1 / RealtimeOffAxisFrustumProjectionEngine.WallHeightM) * H) - 1, 0, H - 1);
        var y1 = Math.Clamp((int)MathF.Ceiling((1f - engine.InnerY0 / RealtimeOffAxisFrustumProjectionEngine.WallHeightM) * H) + 1, 0, H - 1);
        InnerX0 = x0; InnerX1 = x1; InnerYp0 = y0; InnerYp1 = y1;
        var tanH = MathF.Tan(Math.Clamp(pose.HFovDeg, 1f, 170f) * (MathF.PI / 360f));
        var tanV = MathF.Tan(Math.Clamp(pose.VFovDeg, 1f, 170f) * (MathF.PI / 360f));
        var lim = 1f + MathF.Max(Overscan, 0f);
        var fe = MathF.Max(Feather, 1e-4f);
        var e = pose.Position;
        var msum = 0f; var n = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var i = y * W + x;
            var v = new Vector3(_sx[x] - e.X, _wy[y] - e.Y, _sz[x] - e.Z);
            var z = Vector3.Dot(v, pose.Forward);
            if (!(z > 1e-3f)) continue;
            var nx = Vector3.Dot(v, pose.Right) / (z * tanH);
            var ny = Vector3.Dot(v, pose.Up) / (z * tanV);
            var ed = MathF.Max(MathF.Abs(nx), MathF.Abs(ny));
            if (ed >= lim + fe) continue;
            var m = 1f;
            if (ed > lim) { var u = 1f - (ed - lim) / fe; m = u * u * (3f - 2f * u); }
            var dir = Vector3.Normalize(v);
            var c = SampleWorld(e, dir, true);
            if (InnerScene is { } scene)
            {
                var cov = scene.Sample(e, dir, out var sc);
                if (cov > 0f) { c = sc + c * (1f - cov); InnerSceneHits++; }
            }
            c *= InnerExposure;
            _ir[i] = c.X; _ig[i] = c.Y; _ib[i] = c.Z; _m[i] = m;
            msum += m;
            if (m > 0.5f) n++;
        }
        if (MoireBlur)
        {
            for (var y = y0; y <= y1; y++)
            {
                var row = y * W;
                Array.Copy(_ir, row + x0, _tr, row + x0, x1 - x0 + 1);
                Array.Copy(_ig, row + x0, _tg, row + x0, x1 - x0 + 1);
                Array.Copy(_ib, row + x0, _tb, row + x0, x1 - x0 + 1);
            }
            var blurred = 0;
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var i = y * W + x;
                if (!(_m[i] > 0f)) continue;
                var k = 0.6f * engine.Blur(y / PxPerPanel * Cols + x / PxPerPanel) / RealtimeOffAxisFrustumProjectionEngine.MaxBlurPx;
                if (!(k > 1e-3f)) continue;
                int l = x > x0 ? i - 1 : i, r = x < x1 ? i + 1 : i, u = y > y0 ? i - W : i, d = y < y1 ? i + W : i;
                _ir[i] = _tr[i] * (1f - k) + k * 0.25f * (_tr[l] + _tr[r] + _tr[u] + _tr[d]);
                _ig[i] = _tg[i] * (1f - k) + k * 0.25f * (_tg[l] + _tg[r] + _tg[u] + _tg[d]);
                _ib[i] = _tb[i] * (1f - k) + k * 0.25f * (_tb[l] + _tb[r] + _tb[u] + _tb[d]);
                blurred++;
            }
            BlurredPixels = blurred;
        }
        InnerPixels = n;
        InnerCoverage = msum / Pixels;
        InnerLedPx = (x1 - x0 + 1) * LedPxPerCanvasPx;
        TexelsPerLedPx = InnerTargetW / MathF.Max(InnerLedPx, 1f);
        InnerFrames++;
    }

    private void Blend()
    {
        var mt = Calibrated ? CalibratedMatrix : Identity;
        float m00 = mt[0], m01 = mt[1], m02 = mt[2], m10 = mt[3], m11 = mt[4], m12 = mt[5], m20 = mt[6], m21 = mt[7], m22 = mt[8];
        var i = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var w = Vector<float>.Count;
            Vector<float> a00 = new(m00), a01 = new(m01), a02 = new(m02), a10 = new(m10), a11 = new(m11), a12 = new(m12), a20 = new(m20), a21 = new(m21), a22 = new(m22);
            for (; i <= Pixels - w; i += w)
            {
                var m = new Vector<float>(_m, i); var g = new Vector<float>(_gp, i);
                var xr = new Vector<float>(_xr, i); var xg = new Vector<float>(_xg, i); var xb = new Vector<float>(_xb, i);
                var r = xr + (new Vector<float>(_ir, i) - xr) * m;
                var gg = xg + (new Vector<float>(_ig, i) - xg) * m;
                var b = xb + (new Vector<float>(_ib, i) - xb) * m;
                ((a00 * r + a01 * gg + a02 * b) * g).CopyTo(_or, i);
                ((a10 * r + a11 * gg + a12 * b) * g).CopyTo(_og, i);
                ((a20 * r + a21 * gg + a22 * b) * g).CopyTo(_ob, i);
            }
        }
        for (; i < Pixels; i++)
        {
            float m = _m[i], g = _gp[i];
            var r = _xr[i] + (_ir[i] - _xr[i]) * m;
            var gg = _xg[i] + (_ig[i] - _xg[i]) * m;
            var b = _xb[i] + (_ib[i] - _xb[i]) * m;
            _or[i] = (m00 * r + m01 * gg + m02 * b) * g;
            _og[i] = (m10 * r + m11 * gg + m12 * b) * g;
            _ob[i] = (m20 * r + m21 * gg + m22 * b) * g;
        }
    }

    private void Tone()
    {
        for (var i = 0; i < Pixels; i++)
        {
            _rgb[i * 3] = To8(_or[i]); _rgb[i * 3 + 1] = To8(_og[i]); _rgb[i * 3 + 2] = To8(_ob[i]);
        }
    }

    private static byte To8(float v)
    {
        if (!(v > 0f)) return 0;
        var t = MathF.Sqrt(v / (1f + v));
        return (byte)(t >= 1f ? 255 : (int)(t * 255f + 0.5f));
    }
}
