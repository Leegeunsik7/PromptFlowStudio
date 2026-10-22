namespace PromptFlowStudio.Services.ViralVoiceFx;

/// <summary>
/// [PromptFlow Studio v17.80.0] 🛡️ 제로-클리핑 소프트 니 고속 리미터 가드(<see cref="Process(ReadOnlySpan{float}, Span{float})"/> 무할당 · 제자리 가능).
/// <b>트루 피크 검출</b>: 12 탭 Hann 창 sinc 로 샘플 사이 ¼·½·¾ 위치를 4× 보간해 인터샘플 피크까지 포함한 구간 레벨 산출(인접 두 구간 최대).
/// <b>소프트 니</b>: 임계 −1 dBFS · 니 폭 4 dB(−3 dBFS 부터) 2 차 곡선 · 무한비 — 정적 출력 상한 = 임계.
/// <b>1 ms 룩어헤드</b>: 요구 이득을 48 샘플 최소 필터(홀드) → 릴리스 150 ms 상승 제한 → 48 샘플 박스 평균, 오디오를 47(+보간 6) 샘플 지연 →
///   평균 창의 모든 값이 해당 샘플 요구 이득 이하임이 구성적으로 보장되어 어택 오버슈트 0 · 이득 곡선 연속(THD ≤ 0.1 %).
/// 최종 안전망: |y| &gt; 상한이면 하드 클램프(<see cref="HardClamps"/> — 정상 동작 시 0).
/// </summary>
public sealed class ZeroClippingFastLimiterGuard
{
    public const int Fs = 48_000, Lookahead = 48, TpTaps = 12, TpCenter = 6, Delay = Lookahead - 1 + TpCenter, DelayRing = 64, HistRing = 16;
    public const float ThresholdDb = -1f, KneeDb = 4f, ReleaseMs = 150f;
    public static readonly float Ceiling = MathF.Pow(10f, ThresholdDb / 20f);
    public static readonly float KneeStart = MathF.Pow(10f, (ThresholdDb - KneeDb / 2f) / 20f);
    private static readonly float[] TpCoef = BuildTrueKernel();
    /// <summary>부동소수 반올림 여유(−0.0009 dB) — 구성적 보장이 ulp 단위로 상한을 넘지 않도록.</summary>
    private const float SafetyTrim = 0.9999f;
    private static readonly float ReleaseCoef = 1f - MathF.Exp(-1f / (ReleaseMs * 0.001f * Fs));

    private readonly float[] _hist = new float[HistRing], _dl = new float[DelayRing], _req = new float[Lookahead], _box = new float[Lookahead];
    private long _n;
    private int _ri, _bi;
    private float _gr = 1f, _ipPrev, _g = 1f;
    private double _boxSum = Lookahead;

    public ZeroClippingFastLimiterGuard() => Reset();

    public float CurrentGain => _g;
    public float GainReductionDb => _g >= 1f ? 0f : -20f * MathF.Log10(MathF.Max(_g, 1e-6f));
    public float MaxGainReductionDb { get; private set; }
    public float BlockGainReductionDb { get; private set; }
    public float PeakIn { get; private set; }
    public float PeakOut { get; private set; }
    public long LimitedSamples { get; private set; }
    public long HardClamps { get; private set; }
    public long ClippedSamples { get; private set; }
    public long Samples { get; private set; }

    public void Reset()
    {
        Array.Clear(_hist); Array.Clear(_dl);
        Array.Fill(_req, 1f); Array.Fill(_box, 1f);
        _boxSum = Lookahead; _gr = _g = 1f; _ipPrev = 0f; _n = 0; _ri = _bi = 0;
        ResetMeters();
    }

    public void ResetMeters()
    {
        MaxGainReductionDb = BlockGainReductionDb = PeakIn = PeakOut = 0f;
        LimitedSamples = HardClamps = ClippedSamples = Samples = 0;
    }

    /// <summary>소프트 니 정적 이득(선형 레벨 → 선형 이득).</summary>
    public static float KneeGain(float level)
    {
        if (level <= KneeStart) return 1f;
        var lx = 20f * MathF.Log10(level);
        float ly;
        if (lx <= ThresholdDb + KneeDb / 2f) { var d = lx - ThresholdDb + KneeDb / 2f; ly = lx - d * d / (2f * KneeDb); }
        else ly = ThresholdDb;
        return MathF.Pow(10f, (ly - lx) / 20f) * SafetyTrim;
    }

    /// <summary>float 블록 처리(제자리 가능 · 지연 <see cref="Delay"/> 샘플).</summary>
    public void Process(ReadOnlySpan<float> src, Span<float> dst)
    {
        var len = Math.Min(src.Length, dst.Length);
        var minG = 1f;
        for (var i = 0; i < len; i++)
        {
            var y = Step(src[i]);
            if (_g < minG) minG = _g;
            dst[i] = y;
        }
        BlockGainReductionDb = minG >= 1f ? 0f : -20f * MathF.Log10(MathF.Max(minG, 1e-6f));
        if (BlockGainReductionDb > MaxGainReductionDb) MaxGainReductionDb = BlockGainReductionDb;
    }

    /// <summary>float → 48 kHz 16-bit PCM(반올림 · 상한 −1 dBFS 이므로 포화 0).</summary>
    public void Process(ReadOnlySpan<float> src, Span<short> dst)
    {
        var len = Math.Min(src.Length, dst.Length);
        var minG = 1f;
        for (var i = 0; i < len; i++)
        {
            var v = MathF.Round(Step(src[i]) * 32768f);
            if (_g < minG) minG = _g;
            if (v > 32767f) { v = 32767f; ClippedSamples++; } else if (v < -32768f) { v = -32768f; ClippedSamples++; }
            dst[i] = (short)v;
        }
        BlockGainReductionDb = minG >= 1f ? 0f : -20f * MathF.Log10(MathF.Max(minG, 1e-6f));
        if (BlockGainReductionDb > MaxGainReductionDb) MaxGainReductionDb = BlockGainReductionDb;
    }

    private float Step(float x)
    {
        var ax = MathF.Abs(x);
        if (ax > PeakIn) PeakIn = ax;
        _hist[_n & (HistRing - 1)] = x;
        _dl[_n & (DelayRing - 1)] = x;

        // 구간 [m, m+1] 트루 피크(m = n − 6)
        var ip = 0f;
        var b = _n - (TpTaps - 1);
        for (var ph = 0; ph < 3; ph++)
        {
            var v = 0f; var o = ph * TpTaps;
            for (var t = 0; t < TpTaps; t++) v += _hist[(b + t) & (HistRing - 1)] * TpCoef[o + t];
            var av = MathF.Abs(v);
            if (av > ip) ip = av;
        }
        var level = MathF.Max(MathF.Abs(_hist[(_n - TpCenter) & (HistRing - 1)]), MathF.Max(ip, _ipPrev));
        _ipPrev = ip;

        _req[_ri] = KneeGain(level);
        if (++_ri == Lookahead) _ri = 0;
        var mn = 1f;
        for (var k = 0; k < Lookahead; k++) if (_req[k] < mn) mn = _req[k];
        _gr = MathF.Min(mn, _gr + (1f - _gr) * ReleaseCoef);
        _boxSum += _gr - _box[_bi];
        _box[_bi] = _gr;
        if (++_bi == Lookahead) { _bi = 0; _boxSum = 0; for (var k = 0; k < Lookahead; k++) _boxSum += _box[k]; }
        _g = (float)(_boxSum / Lookahead);

        var y = _dl[(_n - Delay) & (DelayRing - 1)] * _g;
        _n++;
        Samples++;
        if (_g < 0.9999f) LimitedSamples++;
        var ay = MathF.Abs(y);
        if (ay > Ceiling) { y = y > 0 ? Ceiling : -Ceiling; ay = Ceiling; HardClamps++; }
        if (ay > PeakOut) PeakOut = ay;
        return y;
    }

    /// <summary>12 탭 Hann 창 sinc — 위상 ¼/½/¾ × 탭 j = −5..6(표본 m+j).</summary>
    private static float[] BuildTrueKernel()
    {
        var c = new float[3 * TpTaps];
        for (var ph = 0; ph < 3; ph++)
        {
            var frac = (ph + 1) * 0.25;
            double sum = 0;
            for (var t = 0; t < TpTaps; t++)
            {
                var j = t - (TpCenter - 1);
                var x = frac - j;
                var sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var w = 0.5 + 0.5 * Math.Cos(Math.PI * x / 6.5);
                c[ph * TpTaps + t] = (float)(sinc * w);
                sum += sinc * w;
            }
            for (var t = 0; t < TpTaps; t++) c[ph * TpTaps + t] = (float)(c[ph * TpTaps + t] / sum);
        }
        return c;
    }
}
