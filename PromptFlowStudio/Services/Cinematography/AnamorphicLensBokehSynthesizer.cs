using System.Numerics;
using PromptFlowStudio.Services.PathTracing;

namespace PromptFlowStudio.Services.Cinematography;

/// <summary>
/// [PromptFlow Studio v16.85.0] 🔭 광학 아나모픽 렌즈 보케 &amp; 광학 수차 합성기(무할당 프레임 · 해상도 변경 시만 할당).
///   ▸ CoC: 깊이 → 박막 렌즈 착란원(mm → 픽셀 반경 · 센서 높이 기준) — 궤적 엔진 초점/조리개/초점거리 결선.
///   ▸ 변형 보케: Vogel 적응 8/16/32탭 산란-집적(gather) · 아나모픽 압축비(가로 반경 = 세로/스퀴즈 → 세로 타원) · 캣아이(외곽 기계적 비네팅 = 오프셋 원 교집합) · 하이라이트 가중.
///   ▸ 스트릭 플레어: 휘도 임계 추출(SIMD) → 행별 양방향 IIR(감쇠 d^|x|) 수평 스트릭 · 청색 틴트 SIMD 합성.
///   ▸ 광학 수차: Brown-Conrady 배럴/머스태시(k₁, k₂) · 가로 색수차(R/B 반경 스케일 ±ca) · cos⁴ 근사 비네팅 → 쌍선형 재표본 · sRGB LUT 출력.
///   ▸ 입력 결선: v16.74 경로 추적기(선형 HDR Color/Depth · Reinhard) · v16.58 3DGS 라스터(sRGB 바이트 + 기대 깊이).
/// </summary>
public sealed class AnamorphicLensBokehSynthesizer
{
    public const int Taps = 32, LutSize = 4096;
    public const float MaxCocPx = 14f;

    // 적응 탭 세트: [0..8) = 8탭 · [8..24) = 16탭 · [24..56) = 32탭(각 세트 독립 Vogel 나선 = 원판 균등)
    private const int SetOffset16 = 8, SetOffset32 = 24, TapTotal = 56;
    private static readonly float[] TapX = new float[TapTotal], TapY = new float[TapTotal], TapR = new float[TapTotal];
    private static readonly float[] SrgbToLinear = new float[256];
    private static readonly byte[] LinearToSrgb = new byte[LutSize];

    private readonly object _gate = new();
    private int _w, _h;
    private float[] _r = Array.Empty<float>(), _g = Array.Empty<float>(), _b = Array.Empty<float>(), _depth = Array.Empty<float>(), _coc = Array.Empty<float>();
    private float[] _kr = Array.Empty<float>(), _kg = Array.Empty<float>(), _kb = Array.Empty<float>(), _lum = Array.Empty<float>(), _bright = Array.Empty<float>(), _streak = Array.Empty<float>();
    private float[] _or = Array.Empty<float>(), _og = Array.Empty<float>(), _ob = Array.Empty<float>();
    private byte[] _out = Array.Empty<byte>();

    static AnamorphicLensBokehSynthesizer()
    {
        void Fill(int off, int n)
        {
            for (var k = 0; k < n; k++)
            {
                var r = k == 0 ? 0f : MathF.Sqrt((k + 0.5f) / n); var a = k * 2.39996323f;
                TapX[off + k] = r * MathF.Cos(a); TapY[off + k] = r * MathF.Sin(a); TapR[off + k] = r;
            }
        }
        Fill(0, 8); Fill(SetOffset16, 16); Fill(SetOffset32, Taps);
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            SrgbToLinear[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        for (var i = 0; i < LutSize; i++)
        {
            var l = i / (float)(LutSize - 1);
            var s = l <= 0.0031308f ? 12.92f * l : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
            LinearToSrgb[i] = (byte)Math.Clamp((int)(s * 255f + 0.5f), 0, 255);
        }
    }

    // ── 렌즈 파라미터 ──
    public float Squeeze { get; set; } = 2f;
    public float FocalMm { get; set; } = 50f;
    public float FocusDistance { get; set; } = 5f;
    public float FStop { get; set; } = 2.8f;
    public float BokehScale { get; set; } = 1f;
    public float CatEye { get; set; } = 0.35f;
    public float HighlightBoost { get; set; } = 3f;
    public float StreakIntensity { get; set; } = 0.6f;
    public float StreakThreshold { get; set; } = 0.8f;
    public float StreakDecay { get; set; } = 0.95f;
    public Vector3 StreakTint { get; set; } = new(0.35f, 0.55f, 1f);
    public float DistortionK1 { get; set; } = 0.06f;
    public float DistortionK2 { get; set; } = -0.015f;
    public float ChromaticAberration { get; set; } = 0.004f;
    public float Vignette { get; set; } = 0.35f;
    public bool ToneMap { get; set; }

    // ── 계측 ──
    public int Width => _w;
    public int Height => _h;
    public long Frames { get; private set; }
    public int BokehPixels { get; private set; }
    public float MaxCoc { get; private set; }
    public float StreakEnergy { get; private set; }
    public float CornerShiftPx { get; private set; }
    public float CornerCaPx { get; private set; }
    public double LastMs { get; private set; }
    public ReadOnlySpan<byte> Output => _out;
    public Vector3 OutputLinear(int i) => new(_or[i], _og[i], _ob[i]);
    public float Coc(int i) => _coc[i];

    public static float Decode(byte srgb) => SrgbToLinear[srgb];
    public static byte Encode(float linear) => LinearToSrgb[(int)(Math.Clamp(linear, 0f, 1f) * (LutSize - 1))];

    /// <summary>궤적 엔진 렌즈 상태 결선(초점거리 · 초점 · 조리개).</summary>
    public void SetLens(in CameraPose pose) { FocalMm = pose.FocalMm; FocusDistance = pose.FocusDistance; FStop = pose.FStop; }

    /// <summary>해상도 설정(제어 평면 · 변경 시에만 할당).</summary>
    public void Resize(int w, int h)
    {
        lock (_gate)
        {
            w = Math.Clamp(w, 4, 4096); h = Math.Clamp(h, 4, 4096);
            if (w == _w && h == _h) return;
            _w = w; _h = h; var n = w * h;
            _r = new float[n]; _g = new float[n]; _b = new float[n]; _depth = new float[n]; _coc = new float[n];
            _kr = new float[n]; _kg = new float[n]; _kb = new float[n]; _lum = new float[n]; _bright = new float[n]; _streak = new float[n];
            _or = new float[n]; _og = new float[n]; _ob = new float[n]; _out = new byte[n * 3];
        }
    }

    /// <summary>v16.58 3DGS 프레임(sRGB 바이트 · 기대 깊이 · 0 이하 = 무한) 합성.</summary>
    public void ProcessSplat(ReadOnlySpan<byte> rgb, ReadOnlySpan<float> depth, int w, int h)
    {
        Resize(w, h);
        lock (_gate)
        {
            var n = w * h;
            for (var i = 0; i < n; i++)
            {
                _r[i] = SrgbToLinear[rgb[i * 3]]; _g[i] = SrgbToLinear[rgb[i * 3 + 1]]; _b[i] = SrgbToLinear[rgb[i * 3 + 2]];
                var d = depth.Length > i ? depth[i] : 0f;
                _depth[i] = d > 0f ? d : 1e4f;
            }
            Run(false);
        }
    }

    /// <summary>v16.74 경로 추적기 프레임(선형 HDR Color · 광선 거리 Depth · 0 = 하늘) 합성 — Reinhard 톤 매핑.</summary>
    public void ProcessPathTracer(VolumetricPathTracingLightTransportEngine pt)
    {
        int w = pt.Width, h = pt.Height;
        Resize(w, h);
        lock (_gate)
        {
            var c = pt.Color; var d = pt.Depth; var n = w * h;
            if (c.Length < n * 3 || d.Length < n) return;
            for (var i = 0; i < n; i++)
            {
                _r[i] = c[i * 3]; _g[i] = c[i * 3 + 1]; _b[i] = c[i * 3 + 2];
                _depth[i] = d[i] > 0f ? d[i] : 1e4f;
            }
            Run(true);
        }
    }

    /// <summary>선형 RGB(교차 배열) + 깊이 직접 입력.</summary>
    public void ProcessLinear(ReadOnlySpan<float> rgb, ReadOnlySpan<float> depth, int w, int h, bool toneMap)
    {
        Resize(w, h);
        lock (_gate)
        {
            var n = w * h;
            for (var i = 0; i < n; i++) { _r[i] = rgb[i * 3]; _g[i] = rgb[i * 3 + 1]; _b[i] = rgb[i * 3 + 2]; _depth[i] = depth[i] > 0f ? depth[i] : 1e4f; }
            Run(toneMap);
        }
    }

    private void Run(bool hdr)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var tm = hdr || ToneMap;
        int w = _w, h = _h, n = w * h;
        if (tm)
            for (var i = 0; i < n; i++) { _r[i] /= 1f + _r[i]; _g[i] /= 1f + _g[i]; _b[i] /= 1f + _b[i]; }
        ComputeCoc(n);
        Gather(w, h);
        Streak(w, h);
        Resample(w, h);
        Frames++;
        LastMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void ComputeCoc(int n)
    {
        var px = 0.5f * _h / NeuralCameraTrajectoryEngine.SensorHeightMm * BokehScale;
        float mx = 0f; var bp = 0;
        for (var i = 0; i < n; i++)
        {
            var c = MathF.Min(NeuralCameraTrajectoryEngine.CocMm(_depth[i], FocusDistance, FocalMm, FStop) * px, MaxCocPx);
            _coc[i] = c; mx = MathF.Max(mx, c); if (c > 1f) bp++;
            _lum[i] = 0.2126f * _r[i] + 0.7152f * _g[i] + 0.0722f * _b[i];
        }
        MaxCoc = mx; BokehPixels = bp;
    }

    /// <summary>타원 보케 산란-집적: 표본 자기 CoC 가 중심까지 닿을 때만 기여(전경 누출 억제) · 캣아이 마스크 · 하이라이트 가중.</summary>
    private void Gather(int w, int h)
    {
        float cx = w * 0.5f, cy = h * 0.5f, inv = 1f / cx;
        var sq = MathF.Max(1f, Squeeze);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x; var r = _coc[i];
                if (r < 0.5f) { _kr[i] = _r[i]; _kg[i] = _g[i]; _kb[i] = _b[i]; continue; }
                float ex = (x + 0.5f - cx) * inv * CatEye, ey = (y + 0.5f - cy) * inv * CatEye;
                float sr = 0f, sg = 0f, sb = 0f, sw = 0f;
                // CoC 반경별 적응 탭 수(&lt; 3 px = 8 · &lt; 7 px = 16 · 그 외 32)
                int k0 = r < 3f ? 0 : r < 7f ? SetOffset16 : SetOffset32, k1 = r < 3f ? 8 : r < 7f ? SetOffset32 : TapTotal;
                for (var k = k0; k < k1; k++)
                {
                    float tx = TapX[k], ty = TapY[k];
                    float mx = tx - ex, my = ty - ey;
                    if (k > k0 && mx * mx + my * my > 1f) continue;
                    var sx = Math.Clamp((int)(x + tx * r / sq), 0, w - 1); var sy = Math.Clamp((int)(y + ty * r), 0, h - 1);
                    var j = sy * w + sx;
                    var wt = Math.Clamp(_coc[j] - TapR[k] * r + 1f, 0f, 1f);
                    if (k == k0) wt = 1f;
                    wt *= 1f + HighlightBoost * MathF.Max(_lum[j] - 0.7f, 0f);
                    sr += _r[j] * wt; sg += _g[j] * wt; sb += _b[j] * wt; sw += wt;
                }
                var iw = 1f / sw;
                _kr[i] = sr * iw; _kg[i] = sg * iw; _kb[i] = sb * iw;
            }
    }

    /// <summary>수평 스트릭: 임계 추출(SIMD) → 행별 양방향 IIR → 틴트 합성(SIMD).</summary>
    private void Streak(int w, int h)
    {
        var n = w * h;
        for (var i = 0; i < n; i++) _lum[i] = 0.2126f * _kr[i] + 0.7152f * _kg[i] + 0.0722f * _kb[i];
        CineSimd.Threshold(_bright, _lum, StreakThreshold, 1f);
        var d = Math.Clamp(StreakDecay, 0f, 0.995f);
        var norm = (1f - d) / (1f + d) * 6f * StreakIntensity;
        double e = 0;
        for (var y = 0; y < h; y++)
        {
            var o = y * w; var acc = 0f;
            for (var x = 0; x < w; x++) { acc = _bright[o + x] + d * acc; _streak[o + x] = acc; }
            acc = 0f;
            for (var x = w - 1; x >= 0; x--)
            {
                acc = _bright[o + x] + d * acc;
                var s = (_streak[o + x] + acc - _bright[o + x]) * norm;
                _streak[o + x] = s; e += s;
            }
        }
        StreakEnergy = (float)(e / n);
        var t = StreakTint;
        CineSimd.MulAdd(_kr.AsSpan(0, n), _streak.AsSpan(0, n), t.X);
        CineSimd.MulAdd(_kg.AsSpan(0, n), _streak.AsSpan(0, n), t.Y);
        CineSimd.MulAdd(_kb.AsSpan(0, n), _streak.AsSpan(0, n), t.Z);
    }

    private static float Bilinear(float[] src, int w, int h, float x, float y)
    {
        x = Math.Clamp(x - 0.5f, 0f, w - 1.001f); y = Math.Clamp(y - 0.5f, 0f, h - 1.001f);
        int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0;
        var i = y0 * w + x0;
        var a = src[i] + (src[i + 1] - src[i]) * fx;
        var b = src[i + w] + (src[i + w + 1] - src[i + w]) * fx;
        return a + (b - a) * fy;
    }

    /// <summary>Brown-Conrady 왜곡 + 가로 색수차 + 비네팅 → 쌍선형 재표본 · sRGB 출력.</summary>
    private void Resample(int w, int h)
    {
        float cx = w * 0.5f, cy = h * 0.5f, inv = 1f / cx;
        float k1 = DistortionK1, k2 = DistortionK2, ca = ChromaticAberration, v = Vignette * 1.5f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                float xn = (x + 0.5f - cx) * inv, yn = (y + 0.5f - cy) * inv;
                var r2 = xn * xn + yn * yn;
                var dist = 1f + k1 * r2 + k2 * r2 * r2;
                float dx = xn * dist * cx, dy = yn * dist * cx;
                float sR = 1f + ca * r2 * 4f, sB = 1f - ca * r2 * 4f;
                var vr = 1f + v * v * r2; var vig = 1f / (vr * vr);
                var R = Bilinear(_kr, w, h, cx + dx * sR, cy + dy * sR) * vig;
                var G = Bilinear(_kg, w, h, cx + dx, cy + dy) * vig;
                var B = Bilinear(_kb, w, h, cx + dx * sB, cy + dy * sB) * vig;
                _or[i] = R; _og[i] = G; _ob[i] = B;
                _out[i * 3] = Encode(R); _out[i * 3 + 1] = Encode(G); _out[i * 3 + 2] = Encode(B);
            }
        var rc2 = 1f + (float)(h * h) / (w * w);
        CornerShiftPx = MathF.Abs(k1 * rc2 + k2 * rc2 * rc2) * MathF.Sqrt(rc2) * cx;
        CornerCaPx = 2f * ca * rc2 * 4f * MathF.Sqrt(rc2) * cx;
    }
}
