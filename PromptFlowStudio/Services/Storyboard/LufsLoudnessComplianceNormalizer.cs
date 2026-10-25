using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [v18.2.4] 라우드니스 실측 · 보정 결과 — 현재 통합 LUFS · 목표 LUFS · 적용 게인 dB.
///   미계측(<see cref="Measured"/> = false · 무음/PCM 아님)이면 게인 0 dB(계수 1) — default 도 동일.
/// </summary>
public readonly struct LufsLoudnessReport
{
    public readonly bool Measured, PeakLimited;
    public readonly double IntegratedLufs, TargetLufs, TruePeakDbtp, GainDb;
    public readonly int GatedBlocks;

    public LufsLoudnessReport(bool measured, double integratedLufs, double targetLufs, double truePeakDbtp, double gainDb, bool peakLimited, int gatedBlocks)
    {
        Measured = measured; IntegratedLufs = integratedLufs; TargetLufs = targetLufs; TruePeakDbtp = truePeakDbtp;
        GainDb = gainDb; PeakLimited = peakLimited; GatedBlocks = gatedBlocks;
    }

    /// <summary>선형 게인 계수(미계측 = 1).</summary>
    public float GainFactor => Measured ? (float)Math.Pow(10.0, GainDb / 20.0) : 1f;
    /// <summary>게인 적용 후 통합 라우드니스 · True Peak.</summary>
    public double OutputLufs => IntegratedLufs + GainDb;
    public double OutputTruePeakDbtp => TruePeakDbtp + GainDb;
    /// <summary>적용 후 목표 ± 허용 오차 · True Peak 상한 이내.</summary>
    public bool IsCompliant => Measured
        && Math.Abs(OutputLufs - TargetLufs) <= LufsLoudnessComplianceNormalizer.ToleranceLu
        && OutputTruePeakDbtp <= LufsLoudnessComplianceNormalizer.TruePeakCeilingDbtp + 1e-6;
}

/// <summary>
/// [PromptFlow Studio v18.2.4] 📏 소셜 숏폼 ITU-R BS.1770-4 라우드니스 실측 노멀라이저 — 유튜브 쇼츠 · 인스타 릴스 −14 LUFS(±1 LU) · True Peak −1 dBTP.
///   ▸ K-가중치: 고역 셸빙(+4 dB · 1.68 kHz) + RLB 고역 통과(38 Hz) 2 단 바이쿼드 — 임의 샘플레이트 쌍선형 계수.
///   ▸ 통합 LUFS: 400 ms 블록 · 75 % 중첩(100 ms 홉) · 절대 게이트 −70 LUFS → 상대 게이트 −10 LU(2 패스 정확 게이팅 · 블록 버퍼 0).
///   ▸ True Peak: 4× 오버샘플(48 탭 윈도우드 sinc 다상 보간) 채널 최대 |x|.
///   ▸ 게인 = min(목표 − 통합, 상한 − True Peak) → 클리핑 방지 우선. PCM 16-bit LE · float 인터리브 · 계측 경로 힙 할당 0 B.
/// </summary>
public static class LufsLoudnessComplianceNormalizer
{
    public const double ShortsTargetLufs = -14.0;
    public const double TruePeakCeilingDbtp = -1.0;
    public const double ToleranceLu = 1.0;
    public const double AbsoluteGateLufs = -70.0;
    public const double RelativeGateLu = -10.0;
    /// <summary>최대 부스트(잡음 과증폭 방지).</summary>
    public const double MaxBoostDb = 30.0;
    public const int MaxChannels = 8;

    private const int Taps = 12, Phases = 4;
    /// <summary>다상 보간 계수 [위상 1~3][탭 −5~+6] — 정적 1 회 산출.</summary>
    private static readonly float[] Interp = BuildInterpolator();

    /// <summary>PCM 16-bit LE 인터리브 실측 → 목표 게인(0 B).</summary>
    public static LufsLoudnessReport Measure(ReadOnlySpan<byte> pcm16, int sampleRate, int channels,
                                             double targetLufs = ShortsTargetLufs, double ceilingDbtp = TruePeakCeilingDbtp) =>
        MeasureCore(pcm16, false, sampleRate, channels, targetLufs, ceilingDbtp);

    /// <summary>float(−1 ~ 1) 인터리브 실측 → 목표 게인(0 B).</summary>
    public static LufsLoudnessReport Measure(ReadOnlySpan<float> samples, int sampleRate, int channels,
                                             double targetLufs = ShortsTargetLufs, double ceilingDbtp = TruePeakCeilingDbtp) =>
        MeasureCore(MemoryMarshal.AsBytes(samples), true, sampleRate, channels, targetLufs, ceilingDbtp);

    /// <summary>통합 LUFS 만(무음 · 부적합 입력 = −∞).</summary>
    public static double IntegratedLufs(ReadOnlySpan<float> samples, int sampleRate, int channels) =>
        Integrated(MemoryMarshal.AsBytes(samples), true, sampleRate, channels, out _);

    /// <summary>True Peak dBTP 만(무음 = −∞).</summary>
    public static double TruePeakDbtp(ReadOnlySpan<float> samples, int channels) =>
        ToDb(TruePeak(MemoryMarshal.AsBytes(samples), true, channels));

    /// <summary>게인 계수 제자리 적용(0 B).</summary>
    public static void Apply(Span<float> samples, float gainFactor)
    {
        for (var i = 0; i < samples.Length; i++) samples[i] *= gainFactor;
    }

    private static LufsLoudnessReport MeasureCore(ReadOnlySpan<byte> data, bool isFloat, int rate, int ch, double target, double ceiling)
    {
        var lufs = Integrated(data, isFloat, rate, ch, out var blocks);
        if (double.IsNegativeInfinity(lufs)) return default;
        var tp = ToDb(TruePeak(data, isFloat, ch));
        var gain = Math.Min(target - lufs, MaxBoostDb);
        var limited = false;
        if (tp + gain > ceiling) { gain = ceiling - tp; limited = true; }
        return new LufsLoudnessReport(true, lufs, target, tp, gain, limited, blocks);
    }

    private static double Integrated(ReadOnlySpan<byte> data, bool isFloat, int rate, int ch, out int blocks)
    {
        blocks = 0;
        if (rate < 8000 || ch <= 0 || ch > MaxChannels) return double.NegativeInfinity;
        var frames = data.Length / (ch * (isFloat ? 4 : 2));
        if (frames == 0) return double.NegativeInfinity;
        Span<double> coef = stackalloc double[10];
        Coefficients(rate, coef);
        var absGate = LoudnessToEnergy(AbsoluteGateLufs);
        var sum = Scan(data, isFloat, ch, frames, rate, coef, absGate, out var n);
        if (n == 0) return double.NegativeInfinity;
        var relGate = Math.Max(absGate, sum / n * Math.Pow(10.0, RelativeGateLu / 10.0));
        sum = Scan(data, isFloat, ch, frames, rate, coef, relGate, out n);
        blocks = n;
        return n == 0 ? double.NegativeInfinity : EnergyToLoudness(sum / n);
    }

    /// <summary>K-가중 400 ms 블록(100 ms 홉) 중 에너지 &gt; 게이트인 블록 합 · 개수 — 400 ms 미만 신호는 전체 1 블록.</summary>
    private static double Scan(ReadOnlySpan<byte> data, bool isFloat, int ch, int frames, int rate, ReadOnlySpan<double> c, double gate, out int count)
    {
        Span<double> z = stackalloc double[MaxChannels * 4];
        z.Clear();
        Span<double> hops = stackalloc double[4];
        hops.Clear();
        var hop = rate / 10;
        var block = hop * 4;
        double total = 0, acc = 0, sum = 0;
        int filled = 0, inHop = 0, k = 0;
        count = 0;
        for (var f = 0; f < frames; f++)
        {
            var e = 0.0;
            for (var j = 0; j < ch; j++)
            {
                var x = (double)Sample(data, isFloat, f * ch + j);
                var s = z.Slice(j * 4, 4);
                // 1 단 셸빙 · 2 단 RLB(Direct Form II Transposed)
                var y1 = c[0] * x + s[0];
                s[0] = c[1] * x - c[3] * y1 + s[1];
                s[1] = c[2] * x - c[4] * y1;
                var y2 = c[5] * y1 + s[2];
                s[2] = c[6] * y1 - c[8] * y2 + s[3];
                s[3] = c[7] * y1 - c[9] * y2;
                e += ChannelWeight(j, ch) * y2 * y2;
            }
            acc += e;
            total += e;
            if (++inHop < hop) continue;
            hops[k] = acc;
            k = (k + 1) & 3;
            acc = 0;
            inHop = 0;
            if (++filled < 4) continue;
            var be = (hops[0] + hops[1] + hops[2] + hops[3]) / block;
            if (be > gate) { sum += be; count++; }
        }
        if (filled < 4)
        {
            var be = total / frames;
            if (be > gate) { sum += be; count++; }
        }
        return sum;
    }

    /// <summary>채널 가중치 — L/R/C = 1 · LFE(5.1 의 4 번째) = 0 · 서라운드 = 1.41.</summary>
    private static double ChannelWeight(int j, int ch) => ch < 5 ? 1.0 : j == 3 ? 0.0 : j >= 4 ? 1.41 : 1.0;

    private static float Sample(ReadOnlySpan<byte> data, bool isFloat, int i) => isFloat
        ? BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4))
        : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) * (1f / 32768f);

    /// <summary>4× 오버샘플 True Peak(선형 최대 |x|).</summary>
    private static float TruePeak(ReadOnlySpan<byte> data, bool isFloat, int ch)
    {
        if (ch <= 0 || ch > MaxChannels) return 0f;
        var frames = data.Length / (ch * (isFloat ? 4 : 2));
        var interp = Interp.AsSpan();
        var peak = 0f;
        for (var j = 0; j < ch; j++)
            for (var n = 0; n < frames; n++)
            {
                var a = MathF.Abs(Sample(data, isFloat, n * ch + j));
                if (a > peak) peak = a;
                for (var p = 1; p < Phases; p++)
                {
                    var h = interp.Slice((p - 1) * Taps, Taps);
                    var v = 0f;
                    for (var t = 0; t < Taps; t++)
                    {
                        var m = n + t - (Taps / 2 - 1);
                        if ((uint)m < (uint)frames) v += h[t] * Sample(data, isFloat, m * ch + j);
                    }
                    v = MathF.Abs(v);
                    if (v > peak) peak = v;
                }
            }
        return peak;
    }

    /// <summary>위상 p/4 지점 보간 — 탭 t ↔ 원본 n + t − 5 · Hann 창 sinc(반폭 6) · 위상별 DC 이득 1 정규화.</summary>
    private static float[] BuildInterpolator()
    {
        var h = new float[(Phases - 1) * Taps];
        for (var p = 1; p < Phases; p++)
        {
            var frac = p / (double)Phases;
            double norm = 0;
            for (var t = 0; t < Taps; t++)
            {
                var x = t - (Taps / 2 - 1) - frac;
                var sinc = Math.Sin(Math.PI * x) / (Math.PI * x);
                var w = 0.5 + 0.5 * Math.Cos(Math.PI * x / (Taps / 2));
                h[(p - 1) * Taps + t] = (float)(sinc * w);
                norm += sinc * w;
            }
            for (var t = 0; t < Taps; t++) h[(p - 1) * Taps + t] = (float)(h[(p - 1) * Taps + t] / norm);
        }
        return h;
    }

    /// <summary>BS.1770 K-가중 2 단 바이쿼드 계수 [b0 b1 b2 a1 a2] × 2 — 임의 레이트 쌍선형 변환(48 kHz 에서 규격 계수와 일치).</summary>
    private static void Coefficients(int rate, Span<double> c)
    {
        var f0 = 1681.974450955533;
        var g = 3.999843853973347;
        var q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / rate);
        var vh = Math.Pow(10.0, g / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1.0 + k / q + k * k;
        c[0] = (vh + vb * k / q + k * k) / a0;
        c[1] = 2.0 * (k * k - vh) / a0;
        c[2] = (vh - vb * k / q + k * k) / a0;
        c[3] = 2.0 * (k * k - 1.0) / a0;
        c[4] = (1.0 - k / q + k * k) / a0;
        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / rate);
        a0 = 1.0 + k / q + k * k;
        c[5] = 1.0; c[6] = -2.0; c[7] = 1.0;
        c[8] = 2.0 * (k * k - 1.0) / a0;
        c[9] = (1.0 - k / q + k * k) / a0;
    }

    private static double LoudnessToEnergy(double lufs) => Math.Pow(10.0, (lufs + 0.691) / 10.0);
    private static double EnergyToLoudness(double e) => -0.691 + 10.0 * Math.Log10(e);
    private static double ToDb(float linear) => linear <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(linear);
}
