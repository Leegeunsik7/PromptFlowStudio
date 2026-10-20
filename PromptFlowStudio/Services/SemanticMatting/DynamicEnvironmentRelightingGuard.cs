using System.Numerics;

namespace PromptFlowStudio.Services.SemanticMatting;

public enum HdriPreset : byte { Studio, Sunset, Neon, Night }

/// <summary>
/// [PromptFlow Studio v17.08.0] 가상 세트 HDRI(등장방형 64×32 · 선형 RGB) + 조명 리그(키 라이트 방위/고도/세기/색온도).
/// 좌표계 = 카메라 프레임(x 오른쪽 · y 위 · z 카메라 전방). 변경 시에만 재구성 → SH9 조도 계수 · 지배 광원 방향/색 추출.
/// </summary>
public sealed class HdriEnvironment
{
    public const int W = 64, H = 32;
    private readonly float[] _r = new float[W * H], _g = new float[W * H], _b = new float[W * H];
    private readonly Vector3[] _sh = new Vector3[9];

    public HdriEnvironment() => Rebuild();

    public HdriPreset Preset { get; private set; } = HdriPreset.Studio;
    public float KeyAzimuth { get; private set; } = -35f;
    public float KeyElevation { get; private set; } = 35f;
    public float KeyIntensity { get; private set; } = 2.2f;
    public float KeyKelvin { get; private set; } = 5600f;
    public long Version { get; private set; }
    public Vector3 DominantDir { get; private set; }
    public Vector3 DominantColor { get; private set; }
    public double LastBuildUs { get; private set; }
    public float Texel(int c, int i) => c == 0 ? _r[i] : c == 1 ? _g[i] : _b[i];
    public Vector3 Sh(int i) => _sh[i];

    public void SetPreset(HdriPreset p) { Preset = p; Rebuild(); }

    public void SetKey(float azimuthDeg, float elevationDeg, float intensity, float kelvin)
    {
        KeyAzimuth = Math.Clamp(azimuthDeg, -180f, 180f); KeyElevation = Math.Clamp(elevationDeg, -10f, 89f);
        KeyIntensity = Math.Clamp(intensity, 0f, 12f); KeyKelvin = Math.Clamp(kelvin, 1800f, 12000f);
        Rebuild();
    }

    /// <summary>방위 0° = 카메라 정면 뒤편(+z · 피사체 뒤) · 90° = 오른쪽 · 고도 = 위.</summary>
    public static Vector3 Dir(float azDeg, float elDeg)
    {
        float a = azDeg * MathF.PI / 180f, e = elDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), MathF.Cos(a) * MathF.Cos(e));
    }

    public static Vector3 TexelDir(int x, int y) => Dir((x + 0.5f) / W * 360f - 180f, 90f - (y + 0.5f) / H * 180f);

    /// <summary>색온도 → 선형 RGB(근사 · G = 1 정규화).</summary>
    public static Vector3 Kelvin(float k)
    {
        var t = k / 100f;
        float r = t <= 66 ? 1f : Math.Clamp(1.2929f * MathF.Pow(t - 60f, -0.1332f), 0f, 1f);
        float g = t <= 66 ? Math.Clamp(0.3901f * MathF.Log(t) - 0.6318f, 0f, 1f) : Math.Clamp(1.1299f * MathF.Pow(t - 60f, -0.0755f), 0f, 1f);
        float b = t >= 66 ? 1f : t <= 19 ? 0f : Math.Clamp(0.5432f * MathF.Log(t - 10f) - 1.1962f, 0f, 1f);
        return new Vector3(r, g, b) / MathF.Max(0.05f, g);
    }

    private Vector3 Sky(Vector3 d) => Preset switch
    {
        HdriPreset.Sunset => Vector3.Lerp(new Vector3(0.55f, 0.32f, 0.22f), new Vector3(0.18f, 0.24f, 0.45f), Math.Clamp(d.Y * 1.6f, 0f, 1f)) * (d.Y < -0.05f ? 0.35f : 1f),
        HdriPreset.Neon => (d.X < 0 ? new Vector3(0.75f, 0.12f, 0.6f) : new Vector3(0.1f, 0.55f, 0.75f)) * (0.35f + 0.65f * MathF.Abs(d.X)) * (d.Y < -0.05f ? 0.3f : 1f),
        HdriPreset.Night => new Vector3(0.05f, 0.06f, 0.11f) * (d.Y < 0 ? 0.5f : 1f),
        _ => new Vector3(0.62f, 0.62f, 0.64f) * (0.65f + 0.35f * Math.Clamp(d.Y + 0.4f, 0f, 1f)),
    };

    private void Rebuild()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var kd = Dir(KeyAzimuth, KeyElevation);
        var kc = Kelvin(KeyKelvin) * KeyIntensity;
        Vector3 s0 = default, s1 = default, s2 = default, s3 = default, s4 = default, s5 = default, s6 = default, s7 = default, s8 = default;
        float best = -1; Vector3 bd = kd, bc = default;
        for (var y = 0; y < H; y++)
        {
            var el = 90f - (y + 0.5f) / H * 180f;
            var dw = MathF.Cos(el * MathF.PI / 180f) * (2f * MathF.PI / W) * (MathF.PI / H);   // 입체각
            for (var x = 0; x < W; x++)
            {
                var d = TexelDir(x, y);
                var c = Sky(d);
                var lobe = MathF.Max(0f, Vector3.Dot(d, kd));
                lobe = MathF.Pow(lobe, 48f) * 18f;
                c += kc * lobe;
                var i = y * W + x;
                _r[i] = c.X; _g[i] = c.Y; _b[i] = c.Z;
                var lum = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
                if (lum > best) { best = lum; bd = d; bc = c; }
                var cw = c * dw;
                s0 += cw * 0.282095f;
                s1 += cw * (0.488603f * d.Y); s2 += cw * (0.488603f * d.Z); s3 += cw * (0.488603f * d.X);
                s4 += cw * (1.092548f * d.X * d.Y); s5 += cw * (1.092548f * d.Y * d.Z);
                s6 += cw * (0.315392f * (3f * d.Z * d.Z - 1f)); s7 += cw * (1.092548f * d.X * d.Z);
                s8 += cw * (0.546274f * (d.X * d.X - d.Y * d.Y));
            }
        }
        _sh[0] = s0; _sh[1] = s1; _sh[2] = s2; _sh[3] = s3; _sh[4] = s4; _sh[5] = s5; _sh[6] = s6; _sh[7] = s7; _sh[8] = s8;
        DominantDir = bd; DominantColor = bc / MathF.Max(1f, best) * MathF.Min(best, 4f);
        Version++;
        LastBuildUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>SH9 램버트 조도(Ramamoorthi-Hanrahan) / π.</summary>
    public static Vector3 Irradiance(ReadOnlySpan<Vector3> sh, Vector3 n)
    {
        const float c1 = 0.429043f, c2 = 0.511664f, c3 = 0.743125f, c4 = 0.886227f, c5 = 0.247708f;
        var e = c1 * sh[8] * (n.X * n.X - n.Y * n.Y) + c3 * sh[6] * (n.Z * n.Z) + c4 * sh[0] - c5 * sh[6]
              + 2f * c1 * (sh[4] * (n.X * n.Y) + sh[7] * (n.X * n.Z) + sh[5] * (n.Y * n.Z))
              + 2f * c2 * (sh[3] * n.X + sh[1] * n.Y + sh[2] * n.Z);
        return Vector3.Max(Vector3.Zero, e / MathF.PI);
    }

    public Vector3 Irradiance(Vector3 n) => Irradiance(_sh, n);
}

/// <summary>
/// [PromptFlow Studio v17.08.0] 💡 동적 환경 조명 리라이팅 &amp; 라이트 랩 가드(무할당).
/// 매트 기반 의사 법선(블러 α 기울기) → 반사율 = F / 스튜디오 조도 → 가상 HDRI SH9 조도 · 지배 광원 Blinn-Phong 스페큘러 ·
/// 프레넬 림(뒤로 기운 엣지 법선의 조도) 역산 부여. 가드: 이득 [MinGain, MaxGain] 클램프 · NaN/무한 차단 · 합법 범위 소프트 니 ·
/// SH 계수 프레임당 슬루(HDRI 교체 시 팝 방지) · 라이트 랩은 가시도 0 &lt; v 인 경계 대역에서만 · 가중 상한 MaxWrap.
/// </summary>
public sealed class DynamicEnvironmentRelightingGuard
{
    private readonly int _w, _h, _n;
    private readonly float[] _blur, _rr, _rg, _rb, _nx, _ny, _nz, _vb;
    private readonly double[] _ii, _ir, _ig, _ib;
    private readonly Vector3[] _sh = new Vector3[9];
    private long _envVersion = -1;
    private bool _primed;

    public DynamicEnvironmentRelightingGuard(int width = RealtimeSemanticAlphaMattingEngine.DefaultWidth, int height = RealtimeSemanticAlphaMattingEngine.DefaultHeight)
    {
        _w = width; _h = height; _n = width * height;
        _blur = new float[_n]; _rr = new float[_n]; _rg = new float[_n]; _rb = new float[_n]; _nx = new float[_n]; _ny = new float[_n]; _nz = new float[_n]; _vb = new float[_n];
        var ni = (width + 1) * (height + 1);
        _ii = new double[ni]; _ir = new double[ni]; _ig = new double[ni]; _ib = new double[ni];
    }

    public HdriEnvironment Env { get; } = new();
    public bool Relight { get; set; } = true;
    public bool Wrap { get; set; } = true;
    /// <summary>원본 스튜디오 조도(플레이트 촬영 조명 · 반사율 역산 기준).</summary>
    public float StudioIrradiance { get; set; } = 1f;
    public float Exposure { get; set; } = 1f;
    public float RimStrength { get; set; } = 0.8f;
    public float Specular { get; set; } = 0.25f;
    public float Shininess { get; set; } = 24f;
    public float WrapAmount { get; set; } = 0.45f;
    public int WrapRadius { get; set; } = 4;
    public float MinGain { get; set; } = 0.15f;
    public float MaxGain { get; set; } = 4f;
    public float MaxWrap { get; set; } = 0.6f;
    /// <summary>프레임당 SH 슬루율(1 = 즉시).</summary>
    public float Slew { get; set; } = 0.2f;

    public ReadOnlySpan<float> R => _rr;
    public ReadOnlySpan<float> G => _rg;
    public ReadOnlySpan<float> B => _rb;
    public ReadOnlySpan<float> NormalX => _nx;
    public ReadOnlySpan<float> NormalY => _ny;
    public float MeanGain { get; private set; }
    public int GainClamps { get; private set; }
    public int LevelClamps { get; private set; }
    public int NanBlocked { get; private set; }
    public int RimPixels { get; private set; }
    public int WrapPixels { get; private set; }
    public int WrapViolations { get; private set; }
    public bool Slewing { get; private set; }
    public long SlewFrames { get; private set; }
    public long Frames { get; private set; }
    public double RelightUs { get; private set; }
    public double WrapUs { get; private set; }

    /// <summary>리라이팅: 전경(직선 RGB) + α → 재조명 전경(_rr/_rg/_rb).</summary>
    public void Apply(ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb, ReadOnlySpan<float> alpha)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SlewSh();
        Normals(alpha);
        int gc = 0, lc = 0, nb = 0, rim = 0; double gsum = 0; var gn = 0;
        var inv = 1f / MathF.Max(1e-3f, StudioIrradiance);
        var ld = Env.DominantDir; var lcol = Env.DominantColor;
        var h = Vector3.Normalize(ld + new Vector3(0, 0, -1));
        for (var i = 0; i < _n; i++)
        {
            if (alpha[i] <= 0f) { _rr[i] = _rg[i] = _rb[i] = 0f; continue; }
            var f = new Vector3(fr[i], fg[i], fb[i]);
            if (!Relight) { _rr[i] = f.X; _rg[i] = f.Y; _rb[i] = f.Z; continue; }
            var n = new Vector3(_nx[i], _ny[i], _nz[i]);
            var e = HdriEnvironment.Irradiance(_sh, n) * (inv * Exposure);
            var gl = 0.2126f * e.X + 0.7152f * e.Y + 0.0722f * e.Z;
            if (gl > MaxGain) { e *= MaxGain / gl; gc++; }
            else if (gl < MinGain) { e = gl > 1e-6f ? e * (MinGain / gl) : new Vector3(MinGain); gc++; }
            gsum += MathF.Min(gl, MaxGain); gn++;
            var c = f * e;
            var ndh = MathF.Max(0f, Vector3.Dot(n, h));
            if (ndh > 0f && Specular > 0f) c += lcol * (Specular * MathF.Pow(ndh, Shininess));
            var fres = 1f + n.Z;                                  // n.Z = −1 정면 → 0 · 엣지 → 1
            fres = fres * fres * fres;
            if (fres > 0.05f && RimStrength > 0f)
            {
                var back = HdriEnvironment.Irradiance(_sh, Vector3.Normalize(new Vector3(n.X, n.Y, 0.6f)));
                c += back * (RimStrength * fres * Exposure);
                rim++;
            }
            if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z)) { c = f; nb++; }
            if (c.X > 1f || c.Y > 1f || c.Z > 1f) { lc++; c = Knee(c); }
            _rr[i] = c.X; _rg[i] = c.Y; _rb[i] = c.Z;
        }
        GainClamps = gc; LevelClamps = lc; NanBlocked = nb; RimPixels = rim;
        MeanGain = gn == 0 ? 0f : (float)(gsum / gn);
        Frames++;
        RelightUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>합법 범위 소프트 니(0.8 이상 압축 · 1 미만 보장).</summary>
    private static Vector3 Knee(Vector3 c) => new(K(c.X), K(c.Y), K(c.Z));
    private static float K(float v) => v <= 0.8f ? MathF.Max(0f, v) : 0.8f + 0.2f * (1f - 1f / (1f + (v - 0.8f) * 5f));

    private void SlewSh()
    {
        if (Env.Version != _envVersion) { _envVersion = Env.Version; if (!_primed) { for (var k = 0; k < 9; k++) _sh[k] = Env.Sh(k); _primed = true; } }
        var moving = false;
        for (var k = 0; k < 9; k++)
        {
            var tgt = Env.Sh(k);
            var d = tgt - _sh[k];
            if (d.LengthSquared() > 1e-8f) { _sh[k] += d * Slew; moving = true; }
            else _sh[k] = tgt;
        }
        Slewing = moving;
        if (moving) SlewFrames++;
    }

    /// <summary>즉시 SH 동기(슬루 생략 · 테스트/리셋).</summary>
    public void SnapEnvironment() { for (var k = 0; k < 9; k++) _sh[k] = Env.Sh(k); _envVersion = Env.Version; _primed = true; Slewing = false; }

    /// <summary>매트 의사 법선: α 박스 블러(r = 2) 중앙 차분 → n = normalize(−∂α/∂u · s, ∂α/∂v · s, −1).</summary>
    private void Normals(ReadOnlySpan<float> alpha)
    {
        BoxBlur(alpha, 2, _blur);
        int w = _w, h = _h;
        const float s = 2.2f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                float gx = (_blur[y * w + Math.Min(w - 1, x + 1)] - _blur[y * w + Math.Max(0, x - 1)]) * 0.5f;
                float gy = (_blur[Math.Min(h - 1, y + 1) * w + x] - _blur[Math.Max(0, y - 1) * w + x]) * 0.5f;
                var n = Vector3.Normalize(new Vector3(-gx * s * 4f, gy * s * 4f, -1f));
                _nx[i] = n.X; _ny[i] = n.Y; _nz[i] = n.Z;
            }
    }

    private void BoxBlur(ReadOnlySpan<float> src, int r, Span<float> dst)
    {
        int w = _w, h = _h, s = w + 1;
        for (var y = 0; y < h; y++)
        {
            double row = 0;
            for (var x = 0; x < w; x++) { row += src[y * w + x]; _ii[(y + 1) * s + x + 1] = _ii[y * s + x + 1] + row; }
        }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(w, x + r + 1), y1 = Math.Min(h, y + r + 1);
                dst[y * w + x] = (float)((_ii[y1 * s + x1] - _ii[y0 * s + x1] - _ii[y1 * s + x0] + _ii[y0 * s + x0]) / ((x1 - x0) * (y1 - y0)));
            }
    }

    /// <summary>
    /// 라이트 랩(제자리): 가시도 v 경계 대역에서 블러된 가상 배경을 스며들게 함 — w = WrapAmount · v · (1 − blur(v)) · 2, w ≤ MaxWrap.
    /// v = 0(순수 배경) 픽셀은 절대 변경하지 않음(위반 = 0 보증 · 집계).
    /// </summary>
    public void LightWrap(Span<float> or, Span<float> og, Span<float> ob, ReadOnlySpan<float> vis, ReadOnlySpan<float> sr, ReadOnlySpan<float> sg, ReadOnlySpan<float> sb)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int wp = 0, viol = 0;
        if (Wrap && WrapAmount > 0f)
        {
            BoxBlur(vis, WrapRadius, _vb);
            int w = _w, h = _h, s = w + 1, r = WrapRadius;
            for (var y = 0; y < h; y++)
            {
                double ar = 0, ag = 0, ab = 0;
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    ar += sr[i]; ag += sg[i]; ab += sb[i];
                    int o = (y + 1) * s + x + 1, u = y * s + x + 1;
                    _ir[o] = _ir[u] + ar; _ig[o] = _ig[u] + ag; _ib[o] = _ib[u] + ab;
                }
            }
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    var v = vis[i];
                    if (v <= 0f) continue;
                    var wt = MathF.Min(MaxWrap, WrapAmount * v * (1f - _vb[i]) * 2f);
                    if (wt <= 1e-3f) continue;
                    int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(w, x + r + 1), y1 = Math.Min(h, y + r + 1);
                    var area = 1.0 / ((x1 - x0) * (y1 - y0));
                    var br = (float)((_ir[y1 * s + x1] - _ir[y0 * s + x1] - _ir[y1 * s + x0] + _ir[y0 * s + x0]) * area);
                    var bg = (float)((_ig[y1 * s + x1] - _ig[y0 * s + x1] - _ig[y1 * s + x0] + _ig[y0 * s + x0]) * area);
                    var bb = (float)((_ib[y1 * s + x1] - _ib[y0 * s + x1] - _ib[y1 * s + x0] + _ib[y0 * s + x0]) * area);
                    // 스크린 블렌드형 랩: 가산 성분만(어둡게 하지 않음) · 합법 범위 유지.
                    or[i] = MathF.Min(1f, or[i] + wt * MathF.Max(0f, br - or[i] * br));
                    og[i] = MathF.Min(1f, og[i] + wt * MathF.Max(0f, bg - og[i] * bg));
                    ob[i] = MathF.Min(1f, ob[i] + wt * MathF.Max(0f, bb - ob[i] * bb));
                    wp++;
                }
        }
        for (var i = 0; i < _n; i++) if (vis[i] <= 0f && (or[i] != sr[i] || og[i] != sg[i] || ob[i] != sb[i])) viol++;
        WrapPixels = wp; WrapViolations = viol;
        WrapUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
