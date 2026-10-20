namespace PromptFlowStudio.Services.ProceduralFoley;

/// <summary>무할당 오디오 분석(Goertzel · THD · 스펙트럼 중심 · 4× 트루 피크).</summary>
public static class FoleyAudioAnalysis
{
    public const int Os = 4, PhaseTaps = 12;
    /// <summary>4× 폴리페이즈 보간 FIR(카이저 창 sinc · 위상당 12 탭 · BS.1770 트루 피크 근사).</summary>
    public static readonly float[] Polyphase = BuildPolyphase();

    private static float[] BuildPolyphase()
    {
        var h = new float[Os * PhaseTaps];
        const int n = Os * PhaseTaps;
        const double beta = 7.0;
        static double I0(double x) { double s = 1, t = 1; for (var k = 1; k < 25; k++) { t *= x * x / (4.0 * k * k); s += t; } return s; }
        for (var p = 0; p < Os; p++)
        {
            double sum = 0;
            for (var k = 0; k < PhaseTaps; k++)
            {
                var i = k * Os + p;
                var t = (i - (n - 1) / 2.0) / Os;
                var sinc = Math.Abs(t) < 1e-9 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                var r = 2.0 * i / (n - 1) - 1.0;
                var w = I0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / I0(beta);
                h[p * PhaseTaps + k] = (float)(sinc * w);
                sum += sinc * w;
            }
            for (var k = 0; k < PhaseTaps; k++) h[p * PhaseTaps + k] = (float)(h[p * PhaseTaps + k] / sum);
        }
        return h;
    }

    /// <summary>Goertzel 크기(Hann 창 · 진폭 정규화).</summary>
    public static float Magnitude(ReadOnlySpan<float> x, float hz, float fs)
    {
        var w = 2.0 * Math.PI * hz / fs;
        var c = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0, wsum = 0;
        var n = x.Length;
        for (var i = 0; i < n; i++)
        {
            var win = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            wsum += win;
            var s = x[i] * win + c * s1 - s2; s2 = s1; s1 = s;
        }
        var re = s1 - s2 * Math.Cos(w); var im = s2 * Math.Sin(w);
        return (float)(2.0 * Math.Sqrt(re * re + im * im) / wsum);
    }

    /// <summary>전고조파 왜곡률 THD = √Σ(2..H) A_h² / A₁.</summary>
    public static float Thd(ReadOnlySpan<float> x, float f0, float fs, int harmonics = 10)
    {
        var a1 = Magnitude(x, f0, fs);
        double s = 0;
        for (var h = 2; h <= harmonics && h * f0 < 0.5f * fs; h++) { var a = Magnitude(x, h * f0, fs); s += a * a; }
        return a1 > 1e-9f ? (float)(Math.Sqrt(s) / a1) : 0f;
    }

    /// <summary>스펙트럼 중심(Hz) — 60 Hz–16 kHz 로그 48 대역 Goertzel 파워 가중.</summary>
    public static float Centroid(ReadOnlySpan<float> x, float fs)
    {
        double num = 0, den = 0;
        for (var b = 0; b < 48; b++)
        {
            var hz = 60f * MathF.Pow(16000f / 60f, b / 47f);
            var m = Magnitude(x, hz, fs);
            var p = (double)m * m * hz;   // 로그 대역 폭 ∝ f
            num += p * hz; den += p;
        }
        return den > 0 ? (float)(num / den) : 0f;
    }

    /// <summary>4× 트루 피크(선형) — 블록 경계 이전 11 샘플 히스토리 포함 입력.</summary>
    public static float TruePeak(ReadOnlySpan<float> x)
    {
        var pk = 0f;
        for (var i = PhaseTaps - 1; i < x.Length; i++)
        {
            var a = MathF.Abs(x[i]); if (a > pk) pk = a;
            for (var p = 0; p < Os; p++)
            {
                var acc = 0f;
                for (var k = 0; k < PhaseTaps; k++) acc += Polyphase[p * PhaseTaps + k] * x[i - k];
                a = MathF.Abs(acc); if (a > pk) pk = a;
            }
        }
        return pk;
    }
}

/// <summary>
/// [PromptFlow Studio v17.38.0] 🛡 음향 피크 중첩 포화 &amp; 왜곡 방어 소프트 리미터 가드(<see cref="Process"/> 무할당 · 스테레오 링크 + 공간 공급 버스 2).
///   ▸ 트루 피크 검출: 4× 폴리페이즈(카이저 sinc 48 탭) 샘플 간 피크 → 필요 이득 g = min(1, 천장 −1 dBTP / 피크).
///   ▸ 전방 예측(지연 74 샘플 = 1.54 ms): 지연선 + 창 최소(75 · 고정 링) → 릴리스(지수 80 ms) → 길이 64 이동 평균(박스) —
///     램프가 피크 도달 전에 완료되어 피크 샘플 이득 ≤ 필요 이득(오버슈트 0) · 정현 정상 상태에서 이득 일정 → THD &lt; 1 %.
///   ▸ 안전 소프트 클리퍼(2차 다항식): |x| &gt; 무릎 T(0.92) 구간 y = T + u − u²/(4(1 − T)) (u = |x| − T · |x| = 2 − T 에서 1 접선) → 0 dBFS 초과 불가.
///   ▸ LUFS(ITU-R BS.1770): K 가중(셸프 + RLB 고역) 두 바이쿼드 · 모멘터리 400 ms · 숏텀 3 s · 통합(100 ms 홉 400 ms 블록 · −70 LUFS 절대 / −10 LU 상대 게이트 · 0.1 LU 히스토그램 무할당) ·
///     라우드니스 가드: 숏텀 &gt; 상한(−12 LUFS)이면 메이크업 이득을 초당 −6 dB 이내로 하향 · 이하이면 0 dB 로 복귀.
///   ▸ 가드 OFF: 하드 클램프(비교 · 클립 계수 · THD).
/// </summary>
public sealed class AcousticEnergyPeakSoftLimiterGuard
{
    public const int Fs = RealtimeModalContactFoleyEngine.Fs, Block = RealtimeModalContactFoleyEngine.Block, Look = 64, Hist = FoleyAudioAnalysis.PhaseTaps - 1, Delay = Look + Hist - 1, Window = Delay + 1;
    public const int Bins = 800, StBlocks = 180, MomBlocks = 24, HopBlocks = 6;
    public const float Ceiling = 0.8913f, Knee = 0.92f, ReleaseMs = 80f, MaxShortTermLufs = -12f;

    private readonly float[] _dl = new float[Delay * 4], _det = new float[Block + Hist];
    private readonly float[] _req = new float[Block], _box = new float[Look];
    private readonly float[] _histMin = new float[Window];
    private readonly float[] _blkMs = new float[StBlocks];
    private readonly long[] _binCount = new long[Bins];
    private readonly double[] _binPow = new double[Bins];
    private readonly float[] _grS = new float[240], _tpS = new float[240], _luS = new float[240];
    private int _dlHead, _boxHead, _blkHead, _blkCount, _hop, _sHead, _sCount;
    private float _boxSum = Look, _held = 1f, _makeup = 1f;
    private long _sampleClock;
    private double _k1x1, _k1x2, _k1y1, _k1y2, _k2x1, _k2x2, _k2y1, _k2y2, _k1x1r, _k1x2r, _k1y1r, _k1y2r, _k2x1r, _k2x2r, _k2y1r, _k2y2r;

    public AcousticEnergyPeakSoftLimiterGuard()
    {
        for (var i = 0; i < Look; i++) _box[i] = 1f;
        for (var i = 0; i < Window; i++) _histMin[i] = 1f;
    }

    public bool Enabled { get; set; } = true;
    public bool LoudnessGuard { get; set; } = true;
    public float InputGain { get; set; } = 1f;
    public long OversIn { get; private set; }
    public long HardClipsOut { get; private set; }
    public long SoftClipSamples { get; private set; }
    public float TruePeakInDbtp { get; private set; } = -120f;
    public float TruePeakOutDbtp { get; private set; } = -120f;
    public float MaxTruePeakOutDbtp { get; private set; } = -120f;
    public float MaxTruePeakInDbtp { get; private set; } = -120f;
    public float GainReductionDb { get; private set; }
    public float MaxGainReductionDb { get; private set; }
    public float MakeupDb => 20f * MathF.Log10(_makeup);
    public float MomentaryLufs { get; private set; } = -70f;
    public float ShortTermLufs { get; private set; } = -70f;
    public float IntegratedLufs { get; private set; } = -70f;
    public double LastProcessUs { get; private set; }
    public int SeriesCount => _sCount;
    private int Idx(int i) => (_sHead - 1 - i + 480) % 240;
    public float GainReductionSeries(int i) => _grS[Idx(i)];
    public float TruePeakSeries(int i) => _tpS[Idx(i)];
    public float LoudnessSeries(int i) => _luS[Idx(i)];

    public void ResetMetrics()
    {
        OversIn = HardClipsOut = SoftClipSamples = 0; MaxGainReductionDb = 0f; MaxTruePeakOutDbtp = MaxTruePeakInDbtp = -120f;
        Array.Clear(_binCount); Array.Clear(_binPow); IntegratedLufs = -70f;
    }

    /// <summary>2차 다항식 소프트 클리프(|y| ≤ 1).</summary>
    public static float SoftClip(float x)
    {
        var a = MathF.Abs(x);
        if (a <= Knee) return x;
        var u = a - Knee;
        var y = u >= 2f * (1f - Knee) ? 1f : Knee + u - u * u / (4f * (1f - Knee));
        return MathF.CopySign(y, x);
    }

    // ══════════════════════ 처리(무할당) ══════════════════════

    /// <summary>
    /// 블록 처리(제자리) — l/r = 마스터 스테레오 · f0/f1 = 공간 공급 버스(같은 링크 이득 · 같은 지연). 길이 = 800.
    /// </summary>
    public void Process(Span<float> l, Span<float> r, Span<float> f0, Span<float> f1)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var pre = InputGain * _makeup;
        // 검출 신호(링크 = max |L|,|R|) · 트루 피크 히스토리
        _det.AsSpan(Block, Hist).CopyTo(_det.AsSpan(0, Hist));
        var tpIn = 0f;
        for (var i = 0; i < Block; i++)
        {
            l[i] *= pre; r[i] *= pre; f0[i] *= pre; f1[i] *= pre;
            var al = MathF.Abs(l[i]); var ar = MathF.Abs(r[i]);
            if (al > 1f) OversIn++;
            if (ar > 1f) OversIn++;
            _det[Hist + i] = al >= ar ? l[i] : r[i];
        }
        // 샘플별 트루 피크(4× 보간) → 필요 이득
        for (var i = 0; i < Block; i++)
        {
            var j = Hist + i;
            var pk = MathF.Abs(_det[j]);
            for (var p = 0; p < FoleyAudioAnalysis.Os; p++)
            {
                var acc = 0f;
                for (var k = 0; k < FoleyAudioAnalysis.PhaseTaps; k++) acc += FoleyAudioAnalysis.Polyphase[p * FoleyAudioAnalysis.PhaseTaps + k] * _det[j - k];
                var a = MathF.Abs(acc); if (a > pk) pk = a;
            }
            if (pk > tpIn) tpIn = pk;
            _req[i] = pk > Ceiling ? Ceiling / pk : 1f;
        }
        TruePeakInDbtp = 20f * MathF.Log10(MathF.Max(tpIn, 1e-6f));
        if (TruePeakInDbtp > MaxTruePeakInDbtp) MaxTruePeakInDbtp = TruePeakInDbtp;

        var rel = 1f - MathF.Exp(-1000f / (ReleaseMs * Fs));
        var minG = 1f; var tpOut = 0f;
        for (var i = 0; i < Block; i++)
        {
            // 창 최소(현재 + 과거 Window−1 샘플 필요 이득 · 트루 피크 FIR 지연 11 포함) — 고정 링 순회
            _histMin[(int)(_sampleClock % Window)] = _req[i];
            var wmin = 1f;
            for (var k = 0; k < Window; k++) if (_histMin[k] < wmin) wmin = _histMin[k];
            // 릴리스(유지 신호) → 박스 평균
            _held = wmin < _held ? wmin : _held + (wmin - _held) * rel;
            _boxSum += _held - _box[_boxHead]; _box[_boxHead] = _held; _boxHead = (_boxHead + 1) % Look;
            var g = Enabled ? MathF.Min(1f, _boxSum / Look) : 1f;
            if (g < minG) minG = g;
            // 지연선(Delay = 박스 64 + FIR 지연 10 → 피크 샘플 박스 평균 전 구간이 그 피크를 창에 포함 = 오버슈트 0) — 4 채널
            var di = _dlHead * 4;
            float dl = _dl[di], dr = _dl[di + 1], d0 = _dl[di + 2], d1 = _dl[di + 3];
            _dl[di] = l[i]; _dl[di + 1] = r[i]; _dl[di + 2] = f0[i]; _dl[di + 3] = f1[i];
            _dlHead = (_dlHead + 1) % Delay;
            float yl, yr;
            if (Enabled)
            {
                yl = dl * g; yr = dr * g;
                if (MathF.Abs(yl) > Knee || MathF.Abs(yr) > Knee) SoftClipSamples++;
                yl = SoftClip(yl); yr = SoftClip(yr);
                f0[i] = SoftClip(d0 * g); f1[i] = SoftClip(d1 * g);
            }
            else
            {
                yl = dl; yr = dr; f0[i] = d0; f1[i] = d1;
            }
            if (yl > 1f || yl < -1f) HardClipsOut++;
            if (yr > 1f || yr < -1f) HardClipsOut++;
            yl = Math.Clamp(yl, -1f, 1f); yr = Math.Clamp(yr, -1f, 1f);
            l[i] = yl; r[i] = yr;
            _sampleClock++;
        }
        // 출력 트루 피크(검출 버퍼 재사용)
        var hl = Hist;
        for (var i = 0; i < Block; i++) _det[hl + i] = MathF.Abs(l[i]) >= MathF.Abs(r[i]) ? l[i] : r[i];
        tpOut = FoleyAudioAnalysis.TruePeak(_det.AsSpan(0, Block + Hist));
        TruePeakOutDbtp = 20f * MathF.Log10(MathF.Max(tpOut, 1e-6f));
        if (TruePeakOutDbtp > MaxTruePeakOutDbtp) MaxTruePeakOutDbtp = TruePeakOutDbtp;
        GainReductionDb = -20f * MathF.Log10(MathF.Max(minG, 1e-6f));
        if (GainReductionDb > MaxGainReductionDb) MaxGainReductionDb = GainReductionDb;
        Loudness(l, r);
        _grS[_sHead] = GainReductionDb; _tpS[_sHead] = TruePeakOutDbtp; _luS[_sHead] = ShortTermLufs;
        _sHead = (_sHead + 1) % 240; if (_sCount < 240) _sCount++;
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>K 가중 라우드니스(BS.1770 · 48 kHz 계수) · 게이트 통합 · 라우드니스 가드.</summary>
    private void Loudness(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        const double b0 = 1.53512485958697, b1 = -2.69169618940638, b2 = 1.19839281085285, a1 = -1.69065929318241, a2 = 0.73248077421585;
        const double c1 = -1.99004745483398, c2 = 0.99007225036621;
        double ms = 0;
        for (var i = 0; i < Block; i++)
        {
            double x = l[i];
            var y = b0 * x + b1 * _k1x1 + b2 * _k1x2 - a1 * _k1y1 - a2 * _k1y2; _k1x2 = _k1x1; _k1x1 = x; _k1y2 = _k1y1; _k1y1 = y;
            var z = y - 2 * _k2x1 + _k2x2 - c1 * _k2y1 - c2 * _k2y2; _k2x2 = _k2x1; _k2x1 = y; _k2y2 = _k2y1; _k2y1 = z;
            ms += z * z;
            x = r[i];
            y = b0 * x + b1 * _k1x1r + b2 * _k1x2r - a1 * _k1y1r - a2 * _k1y2r; _k1x2r = _k1x1r; _k1x1r = x; _k1y2r = _k1y1r; _k1y1r = y;
            z = y - 2 * _k2x1r + _k2x2r - c1 * _k2y1r - c2 * _k2y2r; _k2x2r = _k2x1r; _k2x1r = y; _k2y2r = _k2y1r; _k2y1r = z;
            ms += z * z;
        }
        _blkMs[_blkHead] = (float)(ms / Block);
        _blkHead = (_blkHead + 1) % StBlocks; if (_blkCount < StBlocks) _blkCount++;
        MomentaryLufs = Lufs(Mean(MomBlocks));
        ShortTermLufs = Lufs(Mean(StBlocks));
        if (++_hop >= HopBlocks && _blkCount >= MomBlocks)
        {
            _hop = 0;
            var p = Mean(MomBlocks);
            var lu = Lufs(p);
            if (lu > -70f)
            {
                var bin = Math.Clamp((int)((lu + 70f) * 10f), 0, Bins - 1);
                _binCount[bin]++; _binPow[bin] += p;
                IntegratedLufs = Integrated();
            }
        }
        if (LoudnessGuard && Enabled)
        {
            var step = MathF.Pow(10f, -6f / 20f * Block / Fs);       // −6 dB/s
            var up = MathF.Pow(10f, 3f / 20f * Block / Fs);          // +3 dB/s 복귀
            _makeup = ShortTermLufs > MaxShortTermLufs ? MathF.Max(0.0625f, _makeup * step) : MathF.Min(1f, _makeup * up);
        }
        else _makeup = 1f;
    }

    private double Mean(int blocks)
    {
        var n = Math.Min(blocks, _blkCount);
        if (n == 0) return 0;
        double s = 0;
        for (var i = 1; i <= n; i++) s += _blkMs[(_blkHead - i + StBlocks) % StBlocks];
        return s / n;
    }

    private static float Lufs(double p) => p > 1e-12 ? (float)(-0.691 + 10.0 * Math.Log10(p)) : -70f;

    private float Integrated()
    {
        long n = 0; double s = 0;
        for (var b = 0; b < Bins; b++) { n += _binCount[b]; s += _binPow[b]; }
        if (n == 0) return -70f;
        var rel = Lufs(s / n) - 10f;
        var rb = Math.Clamp((int)((rel + 70f) * 10f), 0, Bins - 1);
        n = 0; s = 0;
        for (var b = rb; b < Bins; b++) { n += _binCount[b]; s += _binPow[b]; }
        return n > 0 ? Lufs(s / n) : -70f;
    }
}
