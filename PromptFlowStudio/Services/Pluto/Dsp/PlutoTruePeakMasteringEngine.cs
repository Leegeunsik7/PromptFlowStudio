using System;
using System.Buffers;
using System.Numerics;

namespace PromptFlowStudio.Services.Pluto.Dsp;

/// <summary>🎛️ 마스터링 리포트 — 입력/출력 라우드니스(LUFS) · 트루피크(dBTP) · 적용 프리게인 · 최대 리덕션 · 반복 패스 수.</summary>
public readonly record struct PlutoMasteringReport(
    double InputLufs, double OutputLufs, double InputTruePeakDbtp, double OutputTruePeakDbtp,
    double PreGainDb, double MaxGainReductionDb, int Passes, bool LoudnessOnTarget);

/// <summary>
/// 🎛️ 방송 규격 트루피크 마스터링 — 순수 C# · 외부 라이브러리 0 · 작업 버퍼 <see cref="ArrayPool{T}"/>.
///   ▸ ITU-R BS.1770-4 K-가중(2단 바이쿼드: 하이셸프 + RLB 하이패스) · 400ms 블록(75% 중첩) · 절대 −70 LKFS / 상대 −10 LU 게이팅 적분 라우드니스.
///   ▸ 4배 폴리페이즈 FIR(48탭 대칭 Kaiser-sinc = 4 서브페이즈 × 12탭) · <see cref="Vector{T}"/> SIMD 내적 → 샘플 간 피크(ISP) 검출.
///   ▸ 5ms(240샘플 @48k) 원형 링버퍼 룩어헤드 최소값 + 박스카 램프(오버슈트 0 어택) · 이중 릴리즈(40ms 트랜지언트 / 250ms 프로그램) · 소프트니 브릭월.
///   ▸ −14 LUFS(±0.5) 프리게인 반복 수렴 → 리미팅 → PCM16 양자화 후 트루피크 재검증 트림(≤ −1.0 dBTP 보장).
/// </summary>
public static class PlutoTruePeakMasteringEngine
{
    public const double TargetLufs = -14.0, LufsTolerance = 0.5;
    public const double CeilingDbtp = -1.0;
    /// <summary>−1.0 dBFS 선형값.</summary>
    public const float CeilingLinear = 0.89125094f;
    public const int OversampleFactor = 4, TapsPerPhase = 12, FirTaps = OversampleFactor * TapsPerPhase;
    public const double LookaheadMs = 5.0, ReleaseFastMs = 40.0, ReleaseSlowMs = 250.0;
    /// <summary>소프트니 폭(dB) · 내부 임계 여유(dB) — 게인 보간 · 16비트 양자화 오차 흡수.</summary>
    public const double KneeDb = 1.0, InternalMarginDb = 0.3;
    public const double MaxPreGainDb = 40.0, MinPreGainDb = -40.0;
    private const int MaxPasses = 12, MaxGuardPasses = 4;
    private const double AbsoluteGateLufs = -70.0, RelativeGateLu = -10.0;

    // 폴리페이즈 탭 — 페이즈당 12탭을 16칸(4·8·16 레인 정렬)으로 0 패딩.
    private const int PaddedTaps = 16, PadFront = 5;
    private static readonly float[][] s_phases = BuildPolyphase();

    /// <summary>ITU-R BS.1770-4 K-가중 48kHz 계수(1단 하이셸프 · 2단 RLB 하이패스).</summary>
    public static readonly (double B0, double B1, double B2, double A1, double A2) Stage1At48k =
        (1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
    public static readonly (double B0, double B1, double B2, double A1, double A2) Stage2At48k =
        (1.0, -2.0, 1.0, -1.99004745483398, 0.99007225035529);

    // ══════════════════════ 🎛️ 공개 API ══════════════════════

    /// <summary>WAV 마스터링(−14 LUFS · ≤ −1 dBTP · PCM16) — 실패 시 원본 바이트 사본(무손실 폴백).</summary>
    public static byte[] MasterWavBuffer(ReadOnlySpan<byte> wavBytes)
        => TryMasterWavBuffer(wavBytes, out var mastered, out _) ? mastered : wavBytes.ToArray();

    /// <summary>WAV 마스터링 — 파싱 실패/무음/예외 → false.</summary>
    public static bool TryMasterWavBuffer(ReadOnlySpan<byte> wavBytes, out byte[] mastered, out PlutoMasteringReport report)
    {
        mastered = Array.Empty<byte>();
        report = default;
        if (!PlutoPcmWav.TryReadHeader(wavBytes, out var info)) return false;
        var n = info.Frames;
        var stereo = info.Channels >= 2;
        var pool = ArrayPool<float>.Shared;
        var l = pool.Rent(n);
        var r = stereo ? pool.Rent(n) : null;
        try
        {
            PlutoPcmWav.DecodeChannel(wavBytes, in info, 0, l);
            if (r is not null) PlutoPcmWav.DecodeChannel(wavBytes, in info, 1, r);
            var rs = r is null ? Span<float>.Empty : r.AsSpan(0, n);
            if (!MasterInPlace(l.AsSpan(0, n), rs, info.SampleRate, out report)) return false;
            mastered = PlutoPcmWav.EncodePcm16(l.AsSpan(0, n), rs, stereo ? 2 : 1, info.SampleRate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or OutOfMemoryException)
        {
            Program.Log("Pluto.Dsp.Mastering", ex);
            return false;
        }
        finally
        {
            pool.Return(l);
            if (r is not null) pool.Return(r);
        }
    }

    /// <summary>
    /// 🎛️ 인플레이스 마스터링(<paramref name="right"/> 비어 있으면 모노) — 결과는 PCM16 격자로 양자화된 float.
    /// 무음(−∞ LUFS) → false(미변경).
    /// </summary>
    public static bool MasterInPlace(Span<float> left, Span<float> right, int sampleRate, out PlutoMasteringReport report)
    {
        report = default;
        var n = left.Length;
        var stereo = !right.IsEmpty;
        if (n == 0 || sampleRate < 8000 || (stereo && right.Length != n)) return false;
        var inLufs = MeasureIntegratedLoudness(left, right, sampleRate);
        if (!double.IsFinite(inLufs)) return false;

        var look = Math.Max(1, (int)Math.Round(LookaheadMs * 0.001 * sampleRate));
        var fp = ArrayPool<float>.Shared;
        float[] env = fp.Rent(n), scratch = fp.Rent(n), req = fp.Rent(n), m1 = fp.Rent(n + look), gain = fp.Rent(n), pad = fp.Rent(n + PadFront + PaddedTaps);
        float[] outL = fp.Rent(n), outR = stereo ? fp.Rent(n) : outL;
        try
        {
            // 1) 단위 게인 트루피크 엔벨로프(채널 최대 · 인접 구간 최대) — 프리게인에 선형 비례하므로 1회만 계산.
            ComputeIntervalPeaks(left, scratch.AsSpan(0, n), pad);
            if (stereo)
            {
                ComputeIntervalPeaks(right, req.AsSpan(0, n), pad);
                for (var i = 0; i < n; i++) if (req[i] > scratch[i]) scratch[i] = req[i];
            }
            var inTp = 0f;
            for (var i = 0; i < n; i++)
            {
                var p = scratch[i];
                if (p > inTp) inTp = p;
                env[i] = i > 0 && scratch[i - 1] > p ? scratch[i - 1] : p;
            }

            // 2) 프리게인 반복 수렴(할선법) — 리미팅 후 라우드니스가 목표 ±0.05 LU 안에 들 때까지.
            var gDb = Math.Clamp(TargetLufs - inLufs, MinPreGainDb, MaxPreGainDb);
            double outLufs = double.NegativeInfinity, prevG = double.NaN, prevL = double.NaN, minGain = 1;
            var passes = 0;
            while (passes < MaxPasses)
            {
                passes++;
                var pre = (float)Math.Pow(10, gDb / 20.0);
                minGain = ComputeLimiterGain(env.AsSpan(0, n), pre, sampleRate, look, req, m1, gain);
                for (var i = 0; i < n; i++)
                {
                    var g = pre * gain[i];
                    outL[i] = left[i] * g;
                    if (stereo) outR[i] = right[i] * g;
                }
                outLufs = MeasureIntegratedLoudness(outL.AsSpan(0, n), stereo ? outR.AsSpan(0, n) : Span<float>.Empty, sampleRate);
                var err = TargetLufs - outLufs;
                if (!double.IsFinite(err) || Math.Abs(err) <= 0.05) break;
                var slope = 1.0;
                if (double.IsFinite(prevG) && Math.Abs(gDb - prevG) > 1e-6) slope = Math.Clamp((outLufs - prevL) / (gDb - prevG), 0.15, 1.0);
                var next = Math.Clamp(gDb + err / slope, MinPreGainDb, MaxPreGainDb);
                if (Math.Abs(next - gDb) < 1e-4) break;   // 프리게인 한계 도달
                prevG = gDb; prevL = outLufs; gDb = next;
            }
            outL.AsSpan(0, n).CopyTo(left);
            if (stereo) outR.AsSpan(0, n).CopyTo(right);

            // 3) PCM16 양자화 후 트루피크 재검증 — 초과 시 정적 트림(보장 루프).
            var outTp = 0.0;
            for (var guard = 0; guard < MaxGuardPasses; guard++)
            {
                Quantize(left);
                if (stereo) Quantize(right);
                outTp = Math.Max(MeasureTruePeak(left, scratch, pad), stereo ? MeasureTruePeak(right, scratch, pad) : 0);
                if (outTp <= CeilingLinear * 0.9994) break;   // ≈ −1.005 dBTP
                var trim = (float)(CeilingLinear * 0.998 / outTp);
                Scale(left, trim);
                if (stereo) Scale(right, trim);
            }
            outLufs = MeasureIntegratedLoudness(left, right, sampleRate);
            report = new PlutoMasteringReport(inLufs, outLufs, ToDb(inTp), ToDb(outTp), gDb, ToDb(minGain),
                passes, Math.Abs(outLufs - TargetLufs) <= LufsTolerance);
            return true;
        }
        finally
        {
            fp.Return(env); fp.Return(scratch); fp.Return(req); fp.Return(m1); fp.Return(gain); fp.Return(pad);
            fp.Return(outL);
            if (stereo) fp.Return(outR);
        }
    }

    /// <summary>📏 ITU-R BS.1770-4 적분 라우드니스(LUFS) — <paramref name="right"/> 비어 있으면 모노(G=1). 무음 → −∞.</summary>
    public static double MeasureIntegratedLoudness(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int sampleRate)
    {
        var n = left.Length;
        if (n == 0 || sampleRate < 8000) return double.NegativeInfinity;
        if (!right.IsEmpty && right.Length < n) n = right.Length;
        var step = sampleRate / 10;   // 100ms 서브블록
        var subs = Math.Max(1, n / step);
        var dp = ArrayPool<double>.Shared;
        var e = dp.Rent(subs);
        try
        {
            Array.Clear(e, 0, subs);
            KWeightedEnergy(left.Slice(0, n), sampleRate, step, e.AsSpan(0, subs));
            if (!right.IsEmpty) KWeightedEnergy(right.Slice(0, n), sampleRate, step, e.AsSpan(0, subs));

            var zAbs = Math.Pow(10, (AbsoluteGateLufs + 0.691) / 10.0);
            if (subs < 4)
            {
                // 400ms 미만 → 전 구간 단일 블록.
                double sum = 0;
                for (var i = 0; i < subs; i++) sum += e[i];
                var z = sum / Math.Max(1, Math.Min(n, subs * step));
                return z > zAbs ? -0.691 + 10 * Math.Log10(z) : double.NegativeInfinity;
            }
            var blocks = subs - 3;
            var norm = 1.0 / (4.0 * step);
            double acc = 0;
            var cnt = 0;
            for (var j = 0; j < blocks; j++)
            {
                var z = (e[j] + e[j + 1] + e[j + 2] + e[j + 3]) * norm;
                if (z > zAbs) { acc += z; cnt++; }
            }
            if (cnt == 0) return double.NegativeInfinity;
            var zRel = acc / cnt * Math.Pow(10, RelativeGateLu / 10.0);
            acc = 0; cnt = 0;
            for (var j = 0; j < blocks; j++)
            {
                var z = (e[j] + e[j + 1] + e[j + 2] + e[j + 3]) * norm;
                if (z > zAbs && z > zRel) { acc += z; cnt++; }
            }
            return cnt == 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(acc / cnt);
        }
        finally { dp.Return(e); }
    }

    /// <summary>📈 4배 오버샘플 트루피크(선형 · 원 샘플 포함).</summary>
    public static double MeasureTruePeak(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        var fp = ArrayPool<float>.Shared;
        float[] peaks = fp.Rent(samples.Length), pad = fp.Rent(samples.Length + PadFront + PaddedTaps);
        try { return MeasureTruePeak(samples, peaks, pad); }
        finally { fp.Return(peaks); fp.Return(pad); }
    }

    /// <summary>📈 트루피크(dBTP) — 스테레오는 채널 최대.</summary>
    public static double MeasureTruePeakDbtp(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        => ToDb(Math.Max(MeasureTruePeak(left), right.IsEmpty ? 0 : MeasureTruePeak(right)));

    /// <summary>
    /// 📈 구간 피크 — <c>peaks[k] = max(|x[k]|, |x[k+1]|, |y₄ₖ|, |y₄ₖ₊₁|, |y₄ₖ₊₂|, |y₄ₖ₊₃|)</c>(yₚ = 페이즈 p 보간 · 오프셋 (p+½)/4).
    /// 12탭 내적은 <see cref="Vector{T}"/> 레인(16칸 0 패딩)으로 가속. <paramref name="pad"/> 길이 ≥ n + 21.
    /// </summary>
    public static void ComputeIntervalPeaks(ReadOnlySpan<float> x, Span<float> peaks, float[] pad)
    {
        var n = x.Length;
        Array.Clear(pad, 0, PadFront);
        x.CopyTo(pad.AsSpan(PadFront));
        Array.Clear(pad, PadFront + n, PaddedTaps);
        float[] t0 = s_phases[0], t1 = s_phases[1], t2 = s_phases[2], t3 = s_phases[3];
        var vc = Vector<float>.Count;
        var simd = Vector.IsHardwareAccelerated && vc <= PaddedTaps && PaddedTaps % vc == 0;
        for (var k = 0; k < n; k++)
        {
            float y0, y1, y2, y3;
            if (simd)
            {
                Vector<float> a0 = Vector<float>.Zero, a1 = Vector<float>.Zero, a2 = Vector<float>.Zero, a3 = Vector<float>.Zero;
                for (var j = 0; j < PaddedTaps; j += vc)
                {
                    var xv = new Vector<float>(pad, k + j);
                    a0 += xv * new Vector<float>(t0, j);
                    a1 += xv * new Vector<float>(t1, j);
                    a2 += xv * new Vector<float>(t2, j);
                    a3 += xv * new Vector<float>(t3, j);
                }
                y0 = Vector.Dot(a0, Vector<float>.One); y1 = Vector.Dot(a1, Vector<float>.One);
                y2 = Vector.Dot(a2, Vector<float>.One); y3 = Vector.Dot(a3, Vector<float>.One);
            }
            else
            {
                y0 = y1 = y2 = y3 = 0f;
                for (var j = 0; j < TapsPerPhase; j++)
                {
                    var v = pad[k + j];
                    y0 += v * t0[j]; y1 += v * t1[j]; y2 += v * t2[j]; y3 += v * t3[j];
                }
            }
            var m = Math.Max(Math.Abs(pad[k + PadFront]), Math.Abs(pad[k + PadFront + 1]));
            m = Math.Max(m, Math.Max(Math.Max(Math.Abs(y0), Math.Abs(y1)), Math.Max(Math.Abs(y2), Math.Abs(y3))));
            peaks[k] = m;
        }
    }

    /// <summary>K-가중 계수 — 48kHz 는 규격 상수, 그 외는 아날로그 프로토타입 쌍선형 변환(libebur128 동일 식).</summary>
    public static void GetKWeightingCoefficients(int sampleRate,
        out (double B0, double B1, double B2, double A1, double A2) stage1, out (double B0, double B1, double B2, double A1, double A2) stage2)
    {
        if (sampleRate == 48000) { stage1 = Stage1At48k; stage2 = Stage2At48k; return; }
        double fs = sampleRate;
        var f0 = 1681.974450955533; var g = 3.999843853973347; var q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / fs);
        var vh = Math.Pow(10.0, g / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1.0 + k / q + k * k;
        stage1 = ((vh + vb * k / q + k * k) / a0, 2.0 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
                  2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0);
        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        var d = 1.0 + k / q + k * k;
        stage2 = (1.0, -2.0, 1.0, 2.0 * (k * k - 1.0) / d, (1.0 - k / q + k * k) / d);
    }

    public static double ToDb(double linear) => linear > 0 ? 20.0 * Math.Log10(linear) : double.NegativeInfinity;

    // ══════════════════════ 🔧 내부 ══════════════════════

    private static double MeasureTruePeak(ReadOnlySpan<float> x, float[] peaks, float[] pad)
    {
        var n = x.Length;
        ComputeIntervalPeaks(x, peaks.AsSpan(0, n), pad);
        var m = 0f;
        for (var i = 0; i < n; i++) if (peaks[i] > m) m = peaks[i];
        return m;
    }

    /// <summary>
    /// 리미터 게인 커브 — (a) 소프트니 정적 게인 요구치 → (b) 원형 링버퍼 단조 덱 룩어헤드 최소(창 L+1) →
    /// (c) 길이 L+1 박스카 평균(요구치 이하 보장 · 오버슈트 0 램프) → (d) 즉시 어택 + 활동도 기반 이중 릴리즈. 반환 = 최소 게인.
    /// </summary>
    private static double ComputeLimiterGain(ReadOnlySpan<float> peakEnv, float preGain, int rate, int look, float[] req, float[] m1, float[] gain)
    {
        var n = peakEnv.Length;
        var tDb = CeilingDbtp - InternalMarginDb;
        var kneeStart = (float)Math.Pow(10, (tDb - KneeDb / 2) / 20.0);
        for (var i = 0; i < n; i++)
        {
            var lvl = peakEnv[i] * preGain;
            if (lvl <= kneeStart) { req[i] = 1f; continue; }
            var lDb = 20.0 * Math.Log10(lvl);
            var over = lDb - tDb + KneeDb / 2;
            var gDb = over >= KneeDb ? tDb - lDb : -(over * over) / (2 * KneeDb);
            req[i] = (float)Math.Pow(10, gDb / 20.0);
        }

        // (b) 룩어헤드 최소 — 가상 패딩 reqPad[j] = j∈[L, L+n) ? req[j−L] : 1.
        var w = look + 1;
        var cap = 1;
        while (cap < w + 1) cap <<= 1;
        var mask = cap - 1;
        var ip = ArrayPool<int>.Shared;
        var ring = ip.Rent(cap);
        try
        {
            int head = 0, tail = 0;
            var total = n + 2 * look;
            for (var i = 0; i < total; i++)
            {
                var v = PadReq(req, i, look, n);
                while (tail > head && PadReq(req, ring[(tail - 1) & mask], look, n) >= v) tail--;
                ring[tail & mask] = i;
                tail++;
                while (ring[head & mask] < i - look) head++;
                if (i >= look) m1[i - look] = PadReq(req, ring[head & mask], look, n);
            }
        }
        finally { ip.Return(ring); }

        // (c) 박스카 램프 s[i] = mean(m1[i..i+L]) · (d) 이중 릴리즈.
        double sum = 0;
        for (var j = 0; j < w; j++) sum += m1[j];
        double fast = 1 - Math.Exp(-1.0 / (ReleaseFastMs * 0.001 * rate));
        double slow = 1 - Math.Exp(-1.0 / (ReleaseSlowMs * 0.001 * rate));
        double actDecay = Math.Exp(-1.0 / (ReleaseSlowMs * 0.001 * rate));
        double env = 1, activity = 0, minGain = 1;
        var invW = 1.0 / w;
        for (var i = 0; i < n; i++)
        {
            var s = sum * invW;
            if (i + w < n + look) sum += m1[i + w] - m1[i];
            if (s < env) env = s;
            else
            {
                var mix = Math.Clamp(activity * 2.0, 0.0, 1.0);   // 지속 리덕션(프로그램) → 느린 릴리즈
                env += (s - env) * (fast + (slow - fast) * mix);
                if (env > s) env = s;
            }
            activity = activity * actDecay + (s < 0.9999 ? 1 - actDecay : 0);
            gain[i] = (float)env;
            if (env < minGain) minGain = env;
        }
        return minGain;
    }

    private static float PadReq(float[] req, int j, int look, int n) => j >= look && j < look + n ? req[j - look] : 1f;

    private static void KWeightedEnergy(ReadOnlySpan<float> x, int rate, int step, Span<double> e)
    {
        GetKWeightingCoefficients(rate, out var s1, out var s2);
        double z1a = 0, z2a = 0, z1b = 0, z2b = 0;
        var subs = e.Length;
        for (var i = 0; i < x.Length; i++)
        {
            double v = x[i];
            var y1 = s1.B0 * v + z1a;
            z1a = s1.B1 * v - s1.A1 * y1 + z2a;
            z2a = s1.B2 * v - s1.A2 * y1;
            var y2 = s2.B0 * y1 + z1b;
            z1b = s2.B1 * y1 - s2.A1 * y2 + z2b;
            z2b = s2.B2 * y1 - s2.A2 * y2;
            var b = i / step;
            if (b < subs) e[b] += y2 * y2;
        }
    }

    private static void Quantize(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++) x[i] = PlutoPcmWav.QuantizePcm16(x[i]);
    }

    private static void Scale(Span<float> x, float g)
    {
        for (var i = 0; i < x.Length; i++) x[i] *= g;
    }

    /// <summary>48탭 대칭 Kaiser(β=5) 윈도우 sinc — 페이즈 p 탭 j 의 지연 <c>j − 5 − (p+½)/4</c> · 페이즈별 DC 이득 1 정규화.</summary>
    private static float[][] BuildPolyphase()
    {
        const double beta = 5.0, halfSpan = 6.0;
        var i0b = BesselI0(beta);
        var phases = new float[OversampleFactor][];
        for (var p = 0; p < OversampleFactor; p++)
        {
            var mu = (p + 0.5) / OversampleFactor;
            var taps = new double[TapsPerPhase];
            double sum = 0;
            for (var j = 0; j < TapsPerPhase; j++)
            {
                var x = j - 5 - mu;
                var sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var r = x / halfSpan;
                var win = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
                taps[j] = sinc * win;
                sum += taps[j];
            }
            var arr = new float[PaddedTaps];
            for (var j = 0; j < TapsPerPhase; j++) arr[j] = (float)(taps[j] / sum);
            phases[p] = arr;
        }
        return phases;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (var k = 1; k < 50; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }
}
