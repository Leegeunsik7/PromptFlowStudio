using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Services.ColorScience;

/// <summary>[PromptFlow Studio v17.10.0] ASC CDL(Slope · Offset · Power · Saturation).</summary>
public struct AscCdl
{
    public float SlopeR, SlopeG, SlopeB, OffsetR, OffsetG, OffsetB, PowerR, PowerG, PowerB, Saturation;

    public static AscCdl Identity => new() { SlopeR = 1, SlopeG = 1, SlopeB = 1, PowerR = 1, PowerG = 1, PowerB = 1, Saturation = 1 };

    /// <summary>ASC 기준 1 화소(음수 → 0 클램프 후 파워 · Rec.709 루마 채도).</summary>
    public readonly void ApplyReference(ref float r, ref float g, ref float b)
    {
        r = MathF.Pow(MathF.Max(0f, r * SlopeR + OffsetR), PowerR);
        g = MathF.Pow(MathF.Max(0f, g * SlopeG + OffsetG), PowerG);
        b = MathF.Pow(MathF.Max(0f, b * SlopeB + OffsetB), PowerB);
        var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        r = l + Saturation * (r - l); g = l + Saturation * (g - l); b = l + Saturation * (b - l);
    }
}

/// <summary>3-Way 컬러 휠 1개(퍽 = 벡터스코프 Cb/Cr 평면 단위 원 · 마스터).</summary>
public struct ColorWheel
{
    public float X, Y, Master;
}

public enum FalseColorZone : byte { None, Crush, Under, MidGray, Skin, NearClip, Clip }

/// <summary>
/// [PromptFlow Studio v17.10.0] 🎛 온셋 라이브 DIT CDL 그레이딩 &amp; 시네마 스코프 합성기(무할당 핫패스).
/// · 3-Way(Lift/Gamma/Gain) → ASC CDL: Offset = Lift · Slope = Gain · Power = 1/Gamma(퍽 → Y'CbCr 역변환 RGB 편차). ACEScct 코드값에 적용(ACES CDL 관례).
/// · 파워는 채널별 4097 점 1D LUT(0–2 · x &lt; 1/64 와 범위 밖은 MathF.Pow) — 서브 ms.
/// · 스코프(표시 코드값 Rec.709): RGB 퍼레이드 96열 × 128 IRE 빈 · 벡터스코프 128² Cb/Cr · False Color 7구간 · 4프레임 순환 잔상 링.
/// </summary>
public sealed class OnSetLiveDitGradingColorDecisionSynthesizer
{
    public const int PowLut = 4097, WaveCols = 96, WaveBins = 128, VecBins = 128, Persist = 4;
    public const float PowMax = 2f, PowMin = 1f / 64f;

    private readonly float[] _powR = new float[PowLut], _powG = new float[PowLut], _powB = new float[PowLut];
    private readonly int[] _wave = new int[Persist * 3 * WaveCols * WaveBins];
    private readonly int[] _vec = new int[Persist * VecBins * VecBins];
    private readonly int[] _waveSum = new int[3 * WaveCols * WaveBins];
    private readonly int[] _vecSum = new int[VecBins * VecBins];
    private readonly ColorWheel[] _wheels = new ColorWheel[3];
    private byte[] _zones = Array.Empty<byte>();
    private int _ring;
    private AscCdl _cdl = AscCdl.Identity;

    public OnSetLiveDitGradingColorDecisionSynthesizer()
    {
        _wheels[0].Master = 0f; _wheels[1].Master = 1f; _wheels[2].Master = 1f;
        Rebuild();
    }

    public ref readonly AscCdl Cdl => ref _cdl;
    public ref readonly ColorWheel Wheel(int i) => ref _wheels[i];
    public bool Bypass { get; set; }
    public bool UsePowLut { get; set; } = true;
    public float WheelStrength { get; set; } = 0.25f;
    public double LastCdlUs { get; private set; }
    public double LastScopeUs { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public float AvgIre { get; private set; }
    public float ClipPct { get; private set; }
    public float CrushPct { get; private set; }
    public float PeakIre { get; private set; }
    public int ZoneCount(FalseColorZone z) => z switch { FalseColorZone.Crush => _zc0, FalseColorZone.Under => _zc1, FalseColorZone.MidGray => _zc2, FalseColorZone.Skin => _zc3, FalseColorZone.NearClip => _zc4, FalseColorZone.Clip => _zc5, _ => 0 };
    private int _zc0, _zc1, _zc2, _zc3, _zc4, _zc5;

    public int WaveSum(int ch, int col, int bin) => _waveSum[(ch * WaveCols + col) * WaveBins + bin];
    public int VecSum(int cb, int cr) => _vecSum[cr * VecBins + cb];
    public byte Zone(int p) => _zones[p];

    // ── 그레이드 ────────────────────────────────────────────

    public void SetWheel(int i, float x, float y, float master)
    {
        var len = MathF.Sqrt(x * x + y * y);
        if (len > 1f) { x /= len; y /= len; }
        _wheels[i].X = x; _wheels[i].Y = y; _wheels[i].Master = master;
        Rebuild();
    }

    public void SetSaturation(float s) { _cdl.Saturation = Math.Clamp(s, 0f, 2f); }

    public void SetCdl(in AscCdl cdl) { _cdl = cdl; BuildPow(); }

    public void Reset()
    {
        _wheels[0] = new ColorWheel { Master = 0f }; _wheels[1] = new ColorWheel { Master = 1f }; _wheels[2] = new ColorWheel { Master = 1f };
        _cdl.Saturation = 1f;
        Rebuild();
    }

    /// <summary>퍽(Cb = X · Cr = Y) → RGB 편차(Y' = 0 · BT.709 역행렬).</summary>
    public static void WheelRgb(float x, float y, out float r, out float g, out float b)
    {
        float cb = x * 0.5f, cr = y * 0.5f;
        r = 1.5748f * cr; g = -0.1873f * cb - 0.4681f * cr; b = 1.8556f * cb;
    }

    private void Rebuild()
    {
        var k = WheelStrength;
        WheelRgb(_wheels[0].X, _wheels[0].Y, out var lr, out var lg, out var lb);
        WheelRgb(_wheels[1].X, _wheels[1].Y, out var mr, out var mg, out var mb);
        WheelRgb(_wheels[2].X, _wheels[2].Y, out var hr, out var hg, out var hb);
        var sat = _cdl.Saturation <= 0f ? 1f : _cdl.Saturation;
        _cdl = new AscCdl
        {
            OffsetR = _wheels[0].Master + 0.4f * k * lr, OffsetG = _wheels[0].Master + 0.4f * k * lg, OffsetB = _wheels[0].Master + 0.4f * k * lb,
            PowerR = 1f / MathF.Max(0.2f, _wheels[1].Master * (1f + k * mr)), PowerG = 1f / MathF.Max(0.2f, _wheels[1].Master * (1f + k * mg)), PowerB = 1f / MathF.Max(0.2f, _wheels[1].Master * (1f + k * mb)),
            SlopeR = MathF.Max(0f, _wheels[2].Master * (1f + k * hr)), SlopeG = MathF.Max(0f, _wheels[2].Master * (1f + k * hg)), SlopeB = MathF.Max(0f, _wheels[2].Master * (1f + k * hb)),
            Saturation = sat,
        };
        BuildPow();
    }

    private void BuildPow()
    {
        for (var i = 0; i < PowLut; i++)
        {
            var x = i * (PowMax / (PowLut - 1));
            _powR[i] = MathF.Pow(x, _cdl.PowerR); _powG[i] = MathF.Pow(x, _cdl.PowerG); _powB[i] = MathF.Pow(x, _cdl.PowerB);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Pow(float x, float p, float[] lut, bool useLut)
    {
        if (p == 1f) return x;
        if (!useLut || x < PowMin || x >= PowMax) return MathF.Pow(x, p);
        var f = x * ((PowLut - 1) / PowMax);
        var i = (int)f;
        return lut[i] + (f - i) * (lut[i + 1] - lut[i]);
    }

    /// <summary>ACEScct 평면 버퍼 제자리 CDL(무할당 · 서브 ms).</summary>
    public void ApplyCdl(Span<float> r, Span<float> g, Span<float> b)
    {
        if (Bypass) { LastCdlUs = 0; return; }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var c = _cdl;
        var lut = UsePowLut;
        float[] pr = _powR, pg = _powG, pb = _powB;
        for (var i = 0; i < r.Length; i++)
        {
            var x = Pow(MathF.Max(0f, r[i] * c.SlopeR + c.OffsetR), c.PowerR, pr, lut);
            var y = Pow(MathF.Max(0f, g[i] * c.SlopeG + c.OffsetG), c.PowerG, pg, lut);
            var z = Pow(MathF.Max(0f, b[i] * c.SlopeB + c.OffsetB), c.PowerB, pb, lut);
            if (c.Saturation != 1f)
            {
                var l = 0.2126f * x + 0.7152f * y + 0.0722f * z;
                x = l + c.Saturation * (x - l); y = l + c.Saturation * (y - l); z = l + c.Saturation * (z - l);
            }
            r[i] = x; g[i] = y; b[i] = z;
        }
        LastCdlUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── 스코프 ──────────────────────────────────────────────

    public static FalseColorZone Classify(float ire) =>
        ire < 2.5f ? FalseColorZone.Crush : ire < 10f ? FalseColorZone.Under : ire >= 38f && ire <= 46f ? FalseColorZone.MidGray :
        ire >= 52f && ire <= 62f ? FalseColorZone.Skin : ire >= 99f ? FalseColorZone.Clip : ire >= 93f ? FalseColorZone.NearClip : FalseColorZone.None;

    /// <summary>표시 코드값(0–1) 평면 → 퍼레이드 · 벡터스코프 · False Color · 통계(4프레임 잔상 링 · 무할당).</summary>
    public void Analyze(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, int width, int height)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = width * height;
        if (_zones.Length != n) _zones = new byte[n];   // 해상도 변경 시에만
        Width = width; Height = height;
        _ring = (_ring + 1) % Persist;
        var wave = _wave.AsSpan(_ring * 3 * WaveCols * WaveBins, 3 * WaveCols * WaveBins);
        var vec = _vec.AsSpan(_ring * VecBins * VecBins, VecBins * VecBins);
        wave.Clear(); vec.Clear();
        int z0 = 0, z1 = 0, z2 = 0, z3 = 0, z4 = 0, z5 = 0;
        double sum = 0; var peak = 0f;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                float rr = Math.Clamp(r[p], 0f, 1f), gg = Math.Clamp(g[p], 0f, 1f), bb = Math.Clamp(b[p], 0f, 1f);
                var col = x * WaveCols / width;
                wave[col * WaveBins + (int)(rr * (WaveBins - 1) + 0.5f)]++;
                wave[(WaveCols + col) * WaveBins + (int)(gg * (WaveBins - 1) + 0.5f)]++;
                wave[(2 * WaveCols + col) * WaveBins + (int)(bb * (WaveBins - 1) + 0.5f)]++;
                var l = 0.2126f * rr + 0.7152f * gg + 0.0722f * bb;
                float cb = (bb - l) / 1.8556f, cr = (rr - l) / 1.5748f;
                var ib = Math.Clamp((int)((cb + 0.5f) * (VecBins - 0.001f)), 0, VecBins - 1);
                var ic = Math.Clamp((int)((cr + 0.5f) * (VecBins - 0.001f)), 0, VecBins - 1);
                vec[ic * VecBins + ib]++;
                var ire = l * 100f;
                sum += ire; peak = MathF.Max(peak, ire);
                var zn = Classify(ire);
                _zones[p] = (byte)zn;
                switch (zn)
                {
                    case FalseColorZone.Crush: z0++; break;
                    case FalseColorZone.Under: z1++; break;
                    case FalseColorZone.MidGray: z2++; break;
                    case FalseColorZone.Skin: z3++; break;
                    case FalseColorZone.NearClip: z4++; break;
                    case FalseColorZone.Clip: z5++; break;
                }
            }
        _zc0 = z0; _zc1 = z1; _zc2 = z2; _zc3 = z3; _zc4 = z4; _zc5 = z5;
        AvgIre = n == 0 ? 0f : (float)(sum / n); PeakIre = peak;
        ClipPct = n == 0 ? 0f : 100f * z5 / n; CrushPct = n == 0 ? 0f : 100f * z0 / n;
        Accumulate();
        LastScopeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Accumulate()
    {
        var ws = _waveSum.AsSpan(); var vs = _vecSum.AsSpan();
        ws.Clear(); vs.Clear();
        for (var k = 0; k < Persist; k++)
        {
            var w = _wave.AsSpan(k * ws.Length, ws.Length);
            for (var i = 0; i < ws.Length; i++) ws[i] += w[i];
            var v = _vec.AsSpan(k * vs.Length, vs.Length);
            for (var i = 0; i < vs.Length; i++) vs[i] += v[i];
        }
    }

    /// <summary>현재 링 슬롯(가장 최근 프레임) 퍼레이드 합계 = 화소 수 × 3(검증용).</summary>
    public long LatestWaveTotal()
    {
        long s = 0;
        var w = _wave.AsSpan(_ring * 3 * WaveCols * WaveBins, 3 * WaveCols * WaveBins);
        for (var i = 0; i < w.Length; i++) s += w[i];
        return s;
    }
}
