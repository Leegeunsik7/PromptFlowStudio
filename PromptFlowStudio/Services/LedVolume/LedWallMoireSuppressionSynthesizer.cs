using System.Numerics;
using PromptFlowStudio.Services.ArGraphics;

namespace PromptFlowStudio.Services.LedVolume;

/// <summary>
/// [PromptFlow Studio v17.09.0] 🌈 LED 월 광학 모아레 억제 &amp; 외부 프러스텀 합성기(무할당).
/// · 월 콘텐츠: 내부 프러스텀 = 렌더 시점에서 가상 환경 광선 셰이딩(오프액시스 래스터와 등가) · 외부 = 스테이지 중앙 기준 저해상 등장방형 환경 조명 맵(32×16 · 쌍선형) · 페더 블렌드.
/// · 카메라 피드(160×90): 실측 카메라(왜곡 ST 맵) 광선 → 월 현 교차 → 콘텐츠 쌍선형.
/// · 1:1 모아레 루페(UHD 센서 중앙 128×72 · 4×4 서브샘플 박스 적분): LED 발광부(필팩터) 격자 ⊗ 광학 저역(OLPF + 피사계 심도 착란원)을
///   격자 푸리에 급수 감쇠 exp(−2π²σ²k²)로 해석적 적용(표 64 위상 × 32 σ) → 샘플링 전 블러(실제 광학 경로와 동일 순서).
/// · 자동 OLPF: 결상 피치(px) 대비 1차 고조파 잔류 ≤ AttTarget 가 되도록 σ = √(−ln a / 2π²)·피치 − 착란원 σ(직교 합) · 상한 MaxOlpfPx.
/// </summary>
public sealed class LedWallMoireSuppressionSynthesizer
{
    public const int EnvW = 32, EnvH = 16, CropW = 128, CropH = 72, Sub = 4, Phases = 64, SigmaLevels = 32, Harmonics = 8;
    public const int SensorW = 3840, SensorH = 2160;
    public const float SensorWidthMm = 36f, MaxSigmaCell = 1f;
    public static readonly Vector3 StageCenter = new(0f, 1.5f, 0f);

    private readonly float[] _mr = new float[LedVolumeTopology.MaxMap], _mg = new float[LedVolumeTopology.MaxMap], _mb = new float[LedVolumeTopology.MaxMap];
    private readonly float[] _er = new float[EnvW * EnvH], _eg = new float[EnvW * EnvH], _eb = new float[EnvW * EnvH];
    private readonly float[] _fr = new float[InnerFrustumOffAxisProjectionEngine.FeedW * InnerFrustumOffAxisProjectionEngine.FeedH], _fg, _fb;
    private readonly byte[] _fz = new byte[InnerFrustumOffAxisProjectionEngine.FeedW * InnerFrustumOffAxisProjectionEngine.FeedH];
    private readonly float[] _cr = new float[CropW * CropH], _cg = new float[CropW * CropH], _cb = new float[CropW * CropH], _ci = new float[CropW * CropH];
    private readonly float[] _table = new float[SigmaLevels * Phases];
    private float _tableFill = -1;
    private bool _envBuilt;

    public LedWallMoireSuppressionSynthesizer()
    {
        _fg = new float[_fr.Length]; _fb = new float[_fr.Length];
    }

    /// <summary>LED 발광부 면적 비율(한 축 · 0.2–1).</summary>
    public float FillFactor { get; set; } = 0.4f;
    public bool AutoOlpf { get; set; } = true;
    /// <summary>수동 OLPF σ(UHD px).</summary>
    public float ManualOlpfPx { get; set; }
    public float MaxOlpfPx { get; set; } = 2.5f;
    /// <summary>1차 고조파 잔류 목표.</summary>
    public float AttTarget { get; set; } = 0.05f;
    public float FocusM { get; set; } = 6f;
    public float TStop { get; set; } = 8f;
    public float OuterGain { get; set; } = 0.85f;
    public bool ShowOuter { get; set; } = true;
    public float ImagedPitchPx { get; private set; }
    public float CocPx { get; private set; }
    public float CocSigmaPx { get; private set; }
    public float OlpfSigmaPx { get; private set; }
    public float TotalSigmaPx { get; private set; }
    public float FocalMm { get; private set; }
    public float WallDistanceM { get; private set; }
    /// <summary>모아레 지수 = RMS(격자 결상 / 무격자 기준 − 1)(루페).</summary>
    public float MoireIndex { get; private set; }
    /// <summary>OLPF 미적용(착란원만) 시 모아레 지수(동일 프레임 · 비교 기준).</summary>
    public float MoireIndexRaw { get; private set; }
    public float MtfNyquist { get; private set; }
    public bool GridResolved { get; private set; }
    public float Coverage { get; private set; }
    public int FeedWallPixels { get; private set; }
    public long Frames { get; private set; }
    public double WallUs { get; private set; }
    public double FeedUs { get; private set; }
    public double LoupeUs { get; private set; }
    public double EnvUs { get; private set; }
    public ReadOnlySpan<float> MapR => _mr;
    public ReadOnlySpan<float> MapG => _mg;
    public ReadOnlySpan<float> MapB => _mb;
    public ReadOnlySpan<float> FeedR => _fr;
    public ReadOnlySpan<float> FeedG => _fg;
    public ReadOnlySpan<float> FeedB => _fb;
    /// <summary>피드 픽셀 영역(0 = 월 밖 · 1 = 외부 프러스텀 · 2 = 페더 · 3 = 내부).</summary>
    public ReadOnlySpan<byte> FeedZone => _fz;
    public ReadOnlySpan<float> LoupeR => _cr;
    public ReadOnlySpan<float> LoupeG => _cg;
    public ReadOnlySpan<float> LoupeB => _cb;
    public float EnvTexel(int c, int i) => c == 0 ? _er[i] : c == 1 ? _eg[i] : _eb[i];

    /// <summary>외부 프러스텀 저해상 환경 맵(스테이지 중앙 · 정적 환경 → 최초 1회 · 이후 Invalidate 시).</summary>
    public void BuildEnvironment()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var y = 0; y < EnvH; y++)
            for (var x = 0; x < EnvW; x++)
            {
                float az = ((x + 0.5f) / EnvW * 2f - 1f) * MathF.PI, el = (0.5f - (y + 0.5f) / EnvH) * MathF.PI;
                var d = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
                var c = VirtualWorld.Shade(StageCenter, d);
                var i = y * EnvW + x;
                _er[i] = c.X; _eg[i] = c.Y; _eb[i] = c.Z;
            }
        _envBuilt = true;
        EnvUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private Vector3 Env(Vector3 d)
    {
        var az = MathF.Atan2(d.X, d.Z);
        var el = MathF.Asin(Math.Clamp(d.Y / MathF.Max(1e-6f, d.Length()), -1f, 1f));
        float u = (az / MathF.PI + 1f) * 0.5f * EnvW - 0.5f, v = (0.5f - el / MathF.PI) * EnvH - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = Math.Clamp((int)MathF.Floor(v), 0, EnvH - 1);
        float fx = u - x0, fy = Math.Clamp(v - y0, 0f, 1f);
        int y1 = Math.Min(EnvH - 1, y0 + 1), xa = (x0 % EnvW + EnvW) % EnvW, xb = (xa + 1) % EnvW;
        int i00 = y0 * EnvW + xa, i01 = y0 * EnvW + xb, i10 = y1 * EnvW + xa, i11 = y1 * EnvW + xb;
        float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
        return new Vector3(_er[i00] * w00 + _er[i01] * w01 + _er[i10] * w10 + _er[i11] * w11,
                           _eg[i00] * w00 + _eg[i01] * w01 + _eg[i10] * w10 + _eg[i11] * w11,
                           _eb[i00] * w00 + _eb[i01] * w01 + _eb[i10] * w10 + _eb[i11] * w11);
    }

    /// <summary>월 콘텐츠 합성: 내부(렌더 시점 광선) ⊕ 외부(환경 맵) · 가중 = 엔진 내부 마스크.</summary>
    public void RenderWall(LedVolumeTopology topo, in FrustumState s, ReadOnlySpan<float> weights)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_envBuilt) BuildEnvironment();
        var n = topo.MapCount;
        for (var k = 0; k < n; k++)
        {
            var p = new Vector3(topo.Px[k], topo.Py[k], topo.Pz[k]);
            var w = weights[k];
            Vector3 c = default;
            if (w < 1f) c = ShowOuter ? Env(p - StageCenter) * OuterGain : new Vector3(0.02f);
            if (w > 0f) c = Vector3.Lerp(c, VirtualWorld.Shade(s.Eye, p - s.Eye), w);
            _mr[k] = c.X; _mg[k] = c.Y; _mb[k] = c.Z;
        }
        WallUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private Vector3 Map(LedVolumeTopology topo, int panel, float f, float h)
    {
        int mw = topo.MapW;
        float u = (panel + f) * LedVolumeTopology.ColsPerPanel - 0.5f, v = (1f - h / topo.HeightM) * LedVolumeTopology.Rows - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(u), 0, mw - 1), y0 = Math.Clamp((int)MathF.Floor(v), 0, LedVolumeTopology.Rows - 1);
        int x1 = Math.Min(mw - 1, x0 + 1), y1 = Math.Min(LedVolumeTopology.Rows - 1, y0 + 1);
        float fx = Math.Clamp(u - x0, 0f, 1f), fy = Math.Clamp(v - y0, 0f, 1f);
        int i00 = y0 * mw + x0, i01 = y0 * mw + x1, i10 = y1 * mw + x0, i11 = y1 * mw + x1;
        float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
        return new Vector3(_mr[i00] * w00 + _mr[i01] * w01 + _mr[i10] * w10 + _mr[i11] * w11,
                           _mg[i00] * w00 + _mg[i01] * w01 + _mg[i10] * w10 + _mg[i11] * w11,
                           _mb[i00] * w00 + _mb[i01] * w01 + _mb[i10] * w10 + _mb[i11] * w11);
    }

    /// <summary>실측 카메라 피드(왜곡 포함) · 영역 분류 · 커버리지(월 픽셀 중 표시 상태 기준 내부 비율).</summary>
    public void RenderFeed(LedVolumeTopology topo, LensCamera cam, in FrustumState shown)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int w = cam.Width, h = cam.Height, wall = 0, inner = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                cam.GridRay(x, y, out var gx, out var gy);
                var d = cam.Forward + cam.Right * gx + cam.Up * gy;
                if (!topo.Intersect(cam.Position, d, out var panel, out var f, out var hh, out var t))
                {   // 월 밖: 스테이지 바닥/천장(무광 회색).
                    var g = d.Y < 0 ? 0.12f : 0.04f;
                    _fr[i] = _fg[i] = _fb[i] = g; _fz[i] = 0;
                    continue;
                }
                var c = Map(topo, panel, f, hh);
                _fr[i] = c.X; _fg[i] = c.Y; _fb[i] = c.Z;
                var p = cam.Position + d * t;
                wall++;
                if (InnerFrustumOffAxisProjectionEngine.Inside(shown, p)) { inner++; _fz[i] = 3; }
                else
                {
                    var feathered = new FrustumState { Eye = shown.Eye, F = shown.F, R = shown.R, U = shown.U, TanX = shown.TanX + shown.Feather, TanY = shown.TanY + shown.Feather };
                    _fz[i] = InnerFrustumOffAxisProjectionEngine.Inside(feathered, p) ? (byte)2 : (byte)1;
                }
            }
        FeedWallPixels = wall;
        Coverage = wall == 0 ? 0f : (float)inner / wall;
        FeedUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>필팩터 변경 시 블러 격자 표 재구성: m(φ, σc) = fill + Σk 2 sin(πk·fill)/(πk) · cos(2πkφ) · exp(−2π²σc²k²) → /fill 정규화(평균 1).</summary>
    private void BuildTable()
    {
        var fill = Math.Clamp(FillFactor, 0.2f, 1f);
        for (var s = 0; s < SigmaLevels; s++)
        {
            var sc = s * MaxSigmaCell / (SigmaLevels - 1);
            for (var p = 0; p < Phases; p++)
            {
                var ph = (p + 0.5f) / Phases;
                double m = fill;
                for (var k = 1; k <= Harmonics; k++)
                    m += 2.0 * Math.Sin(Math.PI * k * fill) / (Math.PI * k) * Math.Cos(2 * Math.PI * k * (ph - 0.5)) * Math.Exp(-2 * Math.PI * Math.PI * sc * sc * k * k);
                _table[s * Phases + p] = (float)Math.Max(0, m / fill);
            }
        }
        _tableFill = FillFactor;
    }

    private float Grid(float phase, float sigmaCell)
    {
        var sl = Math.Clamp(sigmaCell / MaxSigmaCell * (SigmaLevels - 1), 0f, SigmaLevels - 1.001f);
        var s0 = (int)sl; var fs = sl - s0;
        var p = phase - MathF.Floor(phase);
        var pi = Math.Min(Phases - 1, (int)(p * Phases));
        return _table[s0 * Phases + pi] * (1f - fs) + _table[(s0 + 1) * Phases + pi] * fs;
    }

    /// <summary>
    /// 1:1 모아레 루페(UHD 센서 중앙 · 왜곡 무시 근축). 광학 σ = √(착란원σ² + OLPFσ²) → 격자 블러(샘플링 전) → 4×4 박스 적분.
    /// 같은 패스에서 착란원만(OLPF 0) 격자도 적분해 원 모아레 지수 산출.
    /// </summary>
    public void RenderLoupe(LedVolumeTopology topo, LensCamera cam)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_tableFill != FillFactor) BuildTable();
        var fx = SensorW * 0.5f / MathF.Tan(cam.HFovDeg * MathF.PI / 360f);
        FocalMm = SensorWidthMm * 0.5f / MathF.Tan(cam.HFovDeg * MathF.PI / 360f);
        var pitch = topo.PitchMm * 1e-3f;
        WallDistanceM = topo.Intersect(cam.Position, cam.Forward, out _, out _, out _, out var tw) ? tw : 10f;
        ImagedPitchPx = pitch * fx / WallDistanceM;
        // 착란원(박막 렌즈): c = f² · |S2 − S1| / (N · S2 · (S1 − f)) [mm] → px.
        float f = FocalMm, s1 = MathF.Max(FocusM * 1000f, f + 1f), s2 = WallDistanceM * 1000f;
        CocPx = f * f * MathF.Abs(s2 - s1) / (TStop * s2 * (s1 - f)) * (SensorW / SensorWidthMm);
        CocSigmaPx = CocPx / 2.8f;
        var target = MathF.Sqrt(-MathF.Log(Math.Clamp(AttTarget, 1e-4f, 0.9f)) / (2f * MathF.PI * MathF.PI)) * ImagedPitchPx;
        OlpfSigmaPx = AutoOlpf ? MathF.Min(MaxOlpfPx, MathF.Sqrt(MathF.Max(0f, target * target - CocSigmaPx * CocSigmaPx))) : ManualOlpfPx;
        TotalSigmaPx = MathF.Sqrt(CocSigmaPx * CocSigmaPx + OlpfSigmaPx * OlpfSigmaPx);
        MtfNyquist = MathF.Exp(-2f * MathF.PI * MathF.PI * OlpfSigmaPx * OlpfSigmaPx * 0.25f);
        GridResolved = ImagedPitchPx > 4f && TotalSigmaPx < 0.4f * ImagedPitchPx;
        float scTot = TotalSigmaPx / MathF.Max(1e-3f, ImagedPitchPx), scRaw = CocSigmaPx / MathF.Max(1e-3f, ImagedPitchPx);
        double se = 0, seRaw = 0; var n = 0;
        const float inv = 1f / (Sub * Sub);
        for (var y = 0; y < CropH; y++)
            for (var x = 0; x < CropW; x++)
            {
                Vector3 acc = default, accI = default; float accRaw = 0, lumI = 0;
                for (var sy = 0; sy < Sub; sy++)
                    for (var sx = 0; sx < Sub; sx++)
                    {
                        float u = (x - CropW * 0.5f) + (sx + 0.5f) / Sub, v = (y - CropH * 0.5f) + (sy + 0.5f) / Sub;
                        var d = cam.Forward + cam.Right * (u / fx) - cam.Up * (v / fx);
                        if (!topo.Intersect(cam.Position, d, out var panel, out var ff, out var hh, out _)) continue;
                        var c = Map(topo, panel, ff, hh);
                        float lu = (panel + ff) * topo.Chord / pitch, lv = hh / pitch;
                        var m = Grid(lu, scTot) * Grid(lv, scTot);
                        var mr = Grid(lu, scRaw) * Grid(lv, scRaw);
                        acc += c * m; accI += c;
                        var l = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
                        accRaw += l * mr; lumI += l;
                    }
                var i = y * CropW + x;
                acc *= inv; accI *= inv;
                _cr[i] = acc.X; _cg[i] = acc.Y; _cb[i] = acc.Z;
                var li = 0.2126f * accI.X + 0.7152f * accI.Y + 0.0722f * accI.Z;
                _ci[i] = li;
                if (li > 1e-3f)
                {
                    var lm = 0.2126f * acc.X + 0.7152f * acc.Y + 0.0722f * acc.Z;
                    var r = lm / li - 1f; se += r * r;
                    var rr = accRaw * inv / li - 1f; seRaw += rr * rr;
                    n++;
                }
            }
        MoireIndex = n == 0 ? 0f : (float)Math.Sqrt(se / n);
        MoireIndexRaw = n == 0 ? 0f : (float)Math.Sqrt(seRaw / n);
        Frames++;
        LoupeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
