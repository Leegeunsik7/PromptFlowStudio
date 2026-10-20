using System.Numerics;
namespace PromptFlowStudio.Services.SetExtension;

/// <summary>
/// [PromptFlow Studio v17.46.0] 🏛️ 하이브리드 LED-크로마 경계 블렌딩 &amp; 디스필 합성기(<see cref="BuildPlate"/> · <see cref="Compose"/> 60 Hz 무할당).
/// <b>플레이트</b>: 카메라가 실제로 찍는 화면 — LED 영역은 가상 월드 × LED 휘도 게인 · CCT 틴트(카메라 대비 편차), 천장/바닥/측면은 크로마 그린,
/// 배우 실루엣(소프트 에지 · 그린 반사 스필). <b>합성</b>: 크로마 영역은 그린 키(G − max(R,B)) 알파 + 디스필(AiChromaKeyingAndRelightingEngine 과 동일
/// G → max(R,B) 리미터, 강도 0~1) 후 세트 확장 레이어로 대체한다. LED 경계 밴드(≤ 8 px)에서 LED/확장 채널 합을 8 슬롯 링버퍼로 누적해
/// 채널별 게인(휘도 · CCT 매칭)을 추정·적용하고, LED 내부 거리장 기반 smoothstep 에지 페더(0~100 px)로 확장 레이어를 섞되
/// 확장 레이어와 크게 다른 픽셀(배우 등 전경)은 차이 홀드아웃으로 보존한다. 이음새 단차 = 경계 픽셀 쌍 휘도 차분의 기준(일관 렌더) 대비 초과량.
/// </summary>
public sealed class HybridLedChromaBoundaryBlendSynthesizer
{
    public const int W = RealtimeVirtualSetExtensionEngine.W, H = RealtimeVirtualSetExtensionEngine.H, Px = W * H, Ring = 8;
    public const float BandPx = 8f, KeyLo = 0.08f, KeyHi = 0.25f, HoldoutDev = 0.35f, MaxFeather = 100f;
    public static readonly Vector3 Green = new(0.1f, 0.62f, 0.2f);
    public static readonly Vector3 Skin = new(0.75f, 0.58f, 0.5f), Shirt = new(0.35f, 0.33f, 0.36f);
    private readonly float[] _pr = new float[Px], _pg = new float[Px], _pb = new float[Px], _or = new float[Px], _og = new float[Px], _ob = new float[Px];
    private readonly float[] _wr = new float[Px], _wg = new float[Px], _wb = new float[Px], _actor = new float[Px], _alpha = new float[Px], _wt = new float[Px];
    private readonly double[] _ring = new double[Ring * 6];
    private int _slot, _filled;
    private Vector3 _gain = Vector3.One;

    public bool ColorMatch { get; set; } = true;
    /// <summary>에지 페더 폭(px, 0 = 하드 컷).</summary>
    public float FeatherPx { get; set; } = 12f;
    public float DespillStrength { get; set; } = 0.3f;
    public float SpillLevel { get; set; } = 0.16f;
    public bool ActorEnabled { get; set; } = true;
    /// <summary>LED 가 카메라에 찍히는 휘도 게인 · CCT 틴트(카메라 화이트 대비).</summary>
    public float LedLuma { get; set; } = 0.82f;
    public Vector3 LedTint { get; set; } = new(1.05f, 1f, 0.88f);
    public Vector3 LedTransform => LedTint * LedLuma;
    public Vector3 Gain => _gain;
    public ReadOnlySpan<float> OutR => _or;
    public ReadOnlySpan<float> OutG => _og;
    public ReadOnlySpan<float> OutB => _ob;
    public ReadOnlySpan<float> PlateG => _pg;
    public ReadOnlySpan<float> Alpha => _alpha;
    public ReadOnlySpan<float> BlendWeight => _wt;
    public ReadOnlySpan<float> ActorMask => _actor;
    public double SeamStep { get; private set; }
    public double SeamMax { get; private set; }
    public int SeamPairs { get; private set; }
    public double Spill { get; private set; }
    public double LumaDiffPct { get; private set; }
    public double LedCct { get; private set; }
    public double ExtCct { get; private set; }
    public double MatchedCct { get; private set; }
    public int BlendedPixels { get; private set; }
    public int HeldOutPixels { get; private set; }
    public int KeyedPixels { get; private set; }
    public double LastComposeUs { get; private set; }
    public long Frames { get; private set; }

    public void ResetRing() { Array.Clear(_ring); _slot = 0; _filled = 0; _gain = Vector3.One; }

    // ══════════════════════ 플레이트(무할당) ══════════════════════
    /// <summary>카메라 원본: LED = 월드 × LED 변환, 크로마 = 그린 · 배우(소프트 에지 · 스필).</summary>
    public void BuildPlate(RealtimeVirtualSetExtensionEngine e, float time)
    {
        var tr = e.TrueR; var tg = e.TrueG; var tb = e.TrueB; var reg = e.Regions;
        var lt = LedTransform;
        var light = 0.95f + 0.05f * MathF.Sin(0.7f * time);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                Vector3 bg;
                var led = reg[i] == (byte)SetRegion.Led;
                bg = led ? new Vector3(tr[i], tg[i], tb[i]) * lt : Green * light;
                var body = false; var edge = 0f;
                var a = ActorEnabled ? ActorAlpha(x + 0.5f, y + 0.5f, out body, out edge) : 0f;
                _actor[i] = a;
                if (a <= 0f) { _pr[i] = bg.X; _pg[i] = bg.Y; _pb[i] = bg.Z; continue; }
                var ac = body ? Shirt : Skin;
                // 그린 반사 스필: 크로마 배경일 때 윤곽(5 px) 집중 + 전신 바운스
                if (!led) ac.Y += SpillLevel * (0.3f + 0.7f * MathF.Max(0f, 1f - edge / 5f));
                var c = ac * a + bg * (1f - a);
                _pr[i] = c.X; _pg[i] = c.Y; _pb[i] = c.Z;
            }
    }

    /// <summary>배우 실루엣(몸통 타원 + 머리 원) 커버리지 · 몸통 여부 · 윤곽까지 거리(px).</summary>
    public static float ActorAlpha(float x, float y, out bool body, out float edge)
    {
        float bx = (x - 0.5f * W) / (0.085f * W), by = (y - 0.83f * H) / (0.3f * H);
        var bd = (MathF.Sqrt(bx * bx + by * by) - 1f) * MathF.Min(0.085f * W, 0.3f * H);
        float hx = x - 0.5f * W, hy = y - 0.43f * H;
        var hd = MathF.Sqrt(hx * hx + hy * hy) - 0.075f * H;
        var d = MathF.Min(bd, hd);
        body = bd <= hd;
        edge = -d;
        return Math.Clamp(0.5f - d / 1.5f, 0f, 1f);
    }

    // ══════════════════════ 합성(무할당) ══════════════════════
    /// <summary>확장 레이어 래치 재투영(latch) → 키/디스필 → 게인 매칭(링버퍼) → 경계 페더 → 이음새/스필 계측.</summary>
    public void Compose(RealtimeVirtualSetExtensionEngine e, in SetCameraPose extPose, bool latch, float featherScale = 1f)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Warp(e, in extPose, latch);
        var reg = e.Regions; var dist = e.Dist;
        // ── 경계 밴드 채널 합 → 링 슬롯 ──
        double lr = 0, lg = 0, lb = 0, xr = 0, xg = 0, xb = 0;
        for (var i = 0; i < Px; i++)
        {
            if (reg[i] != (byte)SetRegion.Led || dist[i] > BandPx || Holdout(i)) continue;
            lr += _pr[i]; lg += _pg[i]; lb += _pb[i]; xr += _wr[i]; xg += _wg[i]; xb += _wb[i];
        }
        var o = _slot * 6;
        _ring[o] = lr; _ring[o + 1] = lg; _ring[o + 2] = lb; _ring[o + 3] = xr; _ring[o + 4] = xg; _ring[o + 5] = xb;
        _slot = (_slot + 1) % Ring;
        if (_filled < Ring) _filled++;
        double sl0 = 0, sl1 = 0, sl2 = 0, sx0 = 0, sx1 = 0, sx2 = 0;
        for (var k = 0; k < Ring; k++) { var q = k * 6; sl0 += _ring[q]; sl1 += _ring[q + 1]; sl2 += _ring[q + 2]; sx0 += _ring[q + 3]; sx1 += _ring[q + 4]; sx2 += _ring[q + 5]; }
        if (sx0 > 1e-6 && sx1 > 1e-6 && sx2 > 1e-6)
        {
            LedCct = Cct(sl0, sl1, sl2); ExtCct = Cct(sx0, sx1, sx2);
            var ll = 0.2126 * sl0 + 0.7152 * sl1 + 0.0722 * sl2; var lx = 0.2126 * sx0 + 0.7152 * sx1 + 0.0722 * sx2;
            LumaDiffPct = 100.0 * (ll - lx) / lx;
            _gain = ColorMatch ? new Vector3((float)(sl0 / sx0), (float)(sl1 / sx1), (float)(sl2 / sx2)) : Vector3.One;
            MatchedCct = Cct(sx0 * _gain.X, sx1 * _gain.Y, sx2 * _gain.Z);
        }
        var g = _gain;
        var f = Math.Clamp(FeatherPx * featherScale, 0f, MaxFeather);
        var ds = Math.Clamp(DespillStrength, 0f, 1f);
        int blended = 0, held = 0, keyed = 0;
        for (var i = 0; i < Px; i++)
        {
            float er = _wr[i] * g.X, eg = _wg[i] * g.Y, eb = _wb[i] * g.Z;
            float r = _pr[i], gg = _pg[i], b = _pb[i];
            if (reg[i] == (byte)SetRegion.Led)
            {
                _alpha[i] = 1f;
                var w = 0f;
                if (f > 0f && dist[i] - 0.5f < f)
                {
                    if (Holdout(i)) held++;
                    else w = 1f - SmoothStep(0f, f, dist[i] - 0.5f);
                }
                _wt[i] = w;
                if (w > 0f) blended++;
                _or[i] = r + (er - r) * w; _og[i] = gg + (eg - gg) * w; _ob[i] = b + (eb - b) * w;
            }
            else
            {
                var m = MathF.Max(r, b);
                var a = 1f - SmoothStep(KeyLo, KeyHi, gg - m);
                if (gg > m) gg = m + (gg - m) * (1f - ds);
                _alpha[i] = a; _wt[i] = 1f - a;
                if (a < 1f) keyed++;
                _or[i] = r * a + er * (1f - a); _og[i] = gg * a + eg * (1f - a); _ob[i] = b * a + eb * (1f - a);
            }
        }
        BlendedPixels = blended; HeldOutPixels = held; KeyedPixels = keyed;
        Measure(e);
        Frames++;
        LastComposeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>현재 게인 기준 확장 레이어와 채널 상대 편차 &gt; 35 % → 전경(배우) 홀드아웃.</summary>
    private bool Holdout(int i)
    {
        var g = _gain;
        float er = _wr[i] * g.X, eg = _wg[i] * g.Y, eb = _wb[i] * g.Z;
        var dev = MathF.Max(MathF.Abs(_pr[i] - er) / MathF.Max(er, 0.02f), MathF.Max(MathF.Abs(_pg[i] - eg) / MathF.Max(eg, 0.02f), MathF.Abs(_pb[i] - eb) / MathF.Max(eb, 0.02f)));
        return dev > HoldoutDev;
    }

    /// <summary>에지 록 레이트 래치 — 실제 카메라 광선을 확장 렌더 포즈 카메라 공간으로 회전 재투영 후 쌍선형 표본화(경계 클램프).</summary>
    private void Warp(RealtimeVirtualSetExtensionEngine e, in SetCameraPose extPose, bool latch)
    {
        ReadOnlySpan<float> sr = e.ExtR, sg = e.ExtG, sb = e.ExtB;
        ref readonly var tp = ref e.Pose;
        if (!latch || (extPose.Pan == tp.Pan && extPose.Tilt == tp.Tilt && extPose.Roll == tp.Roll)) { sr.CopyTo(_wr); sg.CopyTo(_wg); sb.CopyTo(_wb); return; }
        extPose.Basis(out var f, out var rt, out var u);
        var fp = RealtimeVirtualSetExtensionEngine.FocalPx;
        ReadOnlySpan<float> dx = e.DirX, dy = e.DirY, dz = e.DirZ, dist = e.Dist;
        var need = MathF.Max(BandPx, MaxFeather * 2f) + 1f;
        for (var i = 0; i < Px; i++)
        {
            if (dist[i] > need) { _wr[i] = sr[i]; _wg[i] = sg[i]; _wb[i] = sb[i]; continue; }
            var d = new Vector3(dx[i], dy[i], dz[i]);
            var cz = Vector3.Dot(d, f);
            float px, py;
            if (cz > 1e-3f)
            {
                px = 0.5f * W + Vector3.Dot(d, rt) / cz * fp - 0.5f;
                py = 0.5f * H - Vector3.Dot(d, u) / cz * fp - 0.5f;
            }
            else { px = i % W; py = i / W; }
            px = Math.Clamp(px, 0f, W - 1f); py = Math.Clamp(py, 0f, H - 1f);
            int x0 = (int)px, y0 = (int)py;
            int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
            float ax = px - x0, ay = py - y0;
            int a = y0 * W + x0, b = y0 * W + x1, c = y1 * W + x0, q = y1 * W + x1;
            _wr[i] = Bil(sr[a], sr[b], sr[c], sr[q], ax, ay);
            _wg[i] = Bil(sg[a], sg[b], sg[c], sg[q], ax, ay);
            _wb[i] = Bil(sb[a], sb[b], sb[c], sb[q], ax, ay);
        }
    }

    private static float Bil(float a, float b, float c, float d, float ax, float ay) => (a + (b - a) * ax) + ((c + (d - c) * ax) - (a + (b - a) * ax)) * ay;

    /// <summary>이음새 단차: LED↔비 LED 인접 쌍의 출력 휘도 차분 − 기준(월드 × LED 변환 일관 렌더) 차분 · 배우 스필 잔량.</summary>
    private void Measure(RealtimeVirtualSetExtensionEngine e)
    {
        var reg = e.Regions; var tr = e.TrueR; var tg = e.TrueG; var tb = e.TrueB;
        var lt = LedTransform;
        double sum = 0, mx = 0, sp = 0;
        int pairs = 0, spn = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                if (_actor[i] > 0.5f && reg[i] != (byte)SetRegion.Led) { sp += MathF.Max(0f, _og[i] - MathF.Max(_or[i], _ob[i])); spn++; }
                if (_actor[i] > 0f) continue;
                for (var k = 0; k < 2; k++)
                {
                    var j = k == 0 ? (x + 1 < W ? i + 1 : -1) : (y + 1 < H ? i + W : -1);
                    if (j < 0 || _actor[j] > 0f || (reg[i] == (byte)SetRegion.Led) == (reg[j] == (byte)SetRegion.Led)) continue;
                    var dOut = Luma(_or[i], _og[i], _ob[i]) - Luma(_or[j], _og[j], _ob[j]);
                    var dRef = Luma(tr[i] * lt.X, tg[i] * lt.Y, tb[i] * lt.Z) - Luma(tr[j] * lt.X, tg[j] * lt.Y, tb[j] * lt.Z);
                    var s = Math.Abs(dOut - dRef);
                    sum += s;
                    if (s > mx) mx = s;
                    pairs++;
                }
            }
        SeamPairs = pairs;
        SeamStep = pairs > 0 ? sum / pairs : 0;
        SeamMax = mx;
        Spill = spn > 0 ? sp / spn : 0;
    }

    public static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>선형 sRGB → XYZ → xy → McCamy CCT(K).</summary>
    public static double Cct(double r, double g, double b)
    {
        double X = 0.4124 * r + 0.3576 * g + 0.1805 * b, Y = 0.2126 * r + 0.7152 * g + 0.0722 * b, Z = 0.0193 * r + 0.1192 * g + 0.9505 * b;
        var s = X + Y + Z;
        if (s <= 1e-12) return 0;
        double x = X / s, y = Y / s;
        var n = (x - 0.3320) / (0.1858 - y);
        return 449.0 * n * n * n + 3525.0 * n * n + 6823.3 * n + 5520.33;
    }

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
