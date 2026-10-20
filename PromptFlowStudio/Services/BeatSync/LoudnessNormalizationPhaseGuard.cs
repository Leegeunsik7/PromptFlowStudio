using PromptFlowStudio.Services.ProceduralFoley;
namespace PromptFlowStudio.Services.BeatSync;

/// <summary>
/// ITU-R BS.1770-4 라우드니스 미터(48 kHz · 무할당) — K 가중(셸프 + RLB 고역 바이쿼드) · 100 ms 서브블록 · 400 ms 블록 75% 중첩 ·
/// 모멘터리(400 ms) · 숏텀(3 s) · 통합(−70 LUFS 절대 · −10 LU 상대 게이트 · 1 시간 블록 링).
/// </summary>
public sealed class Bs1770Meter
{
    public const int Fs = 48000, Sub = 4800, MaxBlocks = 36000;
    private const double B0 = 1.53512485958697, B1 = -2.69169618940638, B2 = 1.19839281085285, A1 = -1.69065929318241, A2 = 0.73248077421585;
    private const double C1 = -1.99004745483398, C2 = 0.99007225036621;
    private readonly double[] _z = new double[16];
    private readonly double[] _sub = new double[30];
    private readonly float[] _blocks = new float[MaxBlocks];
    private int _subHead, _subN, _blockN, _blockHead, _inSub;
    private double _acc;

    public float MomentaryLufs { get; private set; } = -70f;
    public float ShortTermLufs { get; private set; } = -70f;
    public float IntegratedLufs { get; private set; } = -70f;
    public double GatedSeconds { get; private set; }

    public void Reset() { Array.Clear(_z); Array.Clear(_sub); _subHead = _subN = _blockN = _blockHead = _inSub = 0; _acc = 0; MomentaryLufs = ShortTermLufs = IntegratedLufs = -70f; GatedSeconds = 0; }

    private static double K(double x, double[] z, int o)
    {
        var y1 = B0 * x + z[o]; z[o] = B1 * x - A1 * y1 + z[o + 1]; z[o + 1] = B2 * x - A2 * y1;
        var y2 = y1 + z[o + 2]; z[o + 2] = -2 * y1 - C1 * y2 + z[o + 3]; z[o + 3] = y1 - C2 * y2;
        return y2;
    }

    public static float ToLufs(double meanSquare) => meanSquare > 1e-12 ? (float)(-0.691 + 10 * Math.Log10(meanSquare)) : -70f;

    public void Process(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        for (var i = 0; i < l.Length; i++)
        {
            var a = K(l[i], _z, 0); var b = K(r[i], _z, 4);
            _acc += a * a + b * b;
            if (++_inSub < Sub) continue;
            _sub[_subHead] = _acc / Sub; _subHead = (_subHead + 1) % _sub.Length; if (_subN < _sub.Length) _subN++;
            _acc = 0; _inSub = 0;
            if (_subN >= 4)
            {
                double m = 0; for (var k = 1; k <= 4; k++) m += _sub[(_subHead - k + _sub.Length) % _sub.Length];
                m /= 4;
                MomentaryLufs = ToLufs(m);
                _blocks[_blockHead] = (float)m; _blockHead = (_blockHead + 1) % MaxBlocks; if (_blockN < MaxBlocks) _blockN++;
                Integrate();
            }
            if (_subN >= 30) { double s = 0; for (var k = 0; k < 30; k++) s += _sub[k]; ShortTermLufs = ToLufs(s / 30); }
        }
    }

    private void Integrate()
    {
        const double absPow = 1.1724e-7; // −70 LUFS → 10^((−70+0.691)/10)
        double s = 0; var n = 0;
        for (var i = 0; i < _blockN; i++) if (_blocks[i] > absPow) { s += _blocks[i]; n++; }
        if (n == 0) { IntegratedLufs = -70f; GatedSeconds = 0; return; }
        var rel = s / n * 0.1; // −10 LU
        s = 0; n = 0;
        for (var i = 0; i < _blockN; i++) if (_blocks[i] > absPow && _blocks[i] > rel) { s += _blocks[i]; n++; }
        IntegratedLufs = n > 0 ? ToLufs(s / n) : -70f;
        GatedSeconds = n * 0.1;
    }
}

/// <summary>
/// [PromptFlow Studio v17.53.0] 📏 소셜 미디어 −14 LUFS 라우드니스 &amp; 위상/마스킹 방어 가드(<see cref="ProcessBgm"/> · <see cref="ProcessMaster"/> 무할당 · 블록 800).
/// <b>마스킹</b>: 보컬 · BGM 을 300 Hz~3 kHz 대역통과(1 kHz · Q 0.55)로 측정해 보컬 발화 중 BGM 중음역이 보컬 −6 dB 보다 크면 그 초과량(최대 9 dB)만큼
/// BGM 에 1 kHz · Q 0.7 피킹 노치(다이내믹 EQ · 블록당 1 dB 이내 변화)를 건다. <b>위상</b>: 마스터 L/R 상관 &lt; −0.2 면 사이드 성분을 상관 회복까지 축소(모노 호환).
/// <b>라우드니스</b>: 입력(정규화 전) BS.1770 통합 라우드니스(초기 3 s 는 숏텀)로 목표 −14 LUFS 메이크업을 산정해 초당 3 dB 이내로 추종 · ±20 dB 제한 →
/// v17.38 4× 트루 피크 전방 예측 리미터(천장 −1 dBTP · 오버슈트 0) 로 마감. 출력 미터는 별도 BS.1770 인스턴스로 검증한다.
/// </summary>
public sealed class LoudnessNormalizationPhaseGuard
{
    public const int Block = AcousticEnergyPeakSoftLimiterGuard.Block;
    public const float TargetLufs = -14f, Tolerance = 0.5f, CeilingDbtp = -1f;
    /// <summary>v17.38 리미터는 링크 검출(max |L|,|R|) 기준이라 채널별 샘플 간 피크가 미세 초과할 수 있어 −0.2 dB 여유를 둔다.</summary>
    public const float IspMargin = 0.97724f;
    private const int Hist = AcousticEnergyPeakSoftLimiterGuard.Hist;
    private readonly float[] _tpL = new float[Block + Hist], _tpR = new float[Block + Hist];
    private readonly AcousticEnergyPeakSoftLimiterGuard _lim = new() { LoudnessGuard = false };
    private readonly float[] _f0 = new float[Block], _f1 = new float[Block];
    private readonly double[] _bpV = new double[2], _bpB = new double[2], _nz = new double[4];
    private double _bb0, _bb1, _bb2, _ba1, _ba2;
    private float _notchDb, _makeupDb, _trimDb, _side = 1f;
    private float _voiceBandDb = -120f, _bgmBandDb = -120f;

    public LoudnessNormalizationPhaseGuard()
    {
        // 대역통과(RBJ · 0 dB 피크 · f0 1 kHz · Q 0.55)
        var w = 2 * Math.PI * 1000 / 48000; var alpha = Math.Sin(w) / (2 * 0.55); var a0 = 1 + alpha;
        _bb0 = alpha / a0; _bb1 = 0; _bb2 = -alpha / a0; _ba1 = -2 * Math.Cos(w) / a0; _ba2 = (1 - alpha) / a0;
    }

    public Bs1770Meter Input { get; } = new();
    public Bs1770Meter Output { get; } = new();
    public AcousticEnergyPeakSoftLimiterGuard Limiter => _lim;
    public bool Normalize { get; set; } = true;
    public bool MaskingGuard { get; set; } = true;
    public bool PhaseGuard { get; set; } = true;
    public float MakeupDb => _makeupDb + _trimDb;
    /// <summary>출력 숏텀 폐루프 보정(리미터가 깎은 라우드니스 회복 · ±0.5 LU 공차 유지).</summary>
    public float TrimDb => _trimDb;
    public float NotchDb => _notchDb;
    public float VoiceBandDb => _voiceBandDb;
    public float BgmBandDb => _bgmBandDb;
    public float Correlation { get; private set; } = 1f;
    public float SideScale => _side;
    public long NotchBlocks { get; private set; }
    public long PhaseFixBlocks { get; private set; }
    /// <summary>채널별 4× 트루 피크(출력 · dBTP).</summary>
    public float TruePeakDbtp { get; private set; } = -120f;
    public float MaxTruePeakDbtp { get; private set; } = -120f;
    /// <summary>여유 적용 후에도 천장을 넘어 블록 비상 감쇄가 걸린 횟수.</summary>
    public long OverCeilingBlocks { get; private set; }
    public double LastProcessUs { get; private set; }

    public void Reset()
    {
        Input.Reset(); Output.Reset(); _lim.ResetMetrics(); _makeupDb = _trimDb = 0; _notchDb = 0; _side = 1; NotchBlocks = PhaseFixBlocks = OverCeilingBlocks = 0; TruePeakDbtp = MaxTruePeakDbtp = -120f;
        Array.Clear(_tpL); Array.Clear(_tpR);
        Array.Clear(_bpV); Array.Clear(_bpB); Array.Clear(_nz);
    }

    private double Band(ReadOnlySpan<float> x, double[] z)
    {
        double s = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var y = _bb0 * x[i] + z[0]; z[0] = _bb1 * x[i] - _ba1 * y + z[1]; z[1] = _bb2 * x[i] - _ba2 * y;
            s += y * y;
        }
        return s / Math.Max(x.Length, 1);
    }

    /// <summary>BGM 중음역 마스킹 노치(더킹 전 BGM · 보컬 스템 · 무할당).</summary>
    public void ProcessBgm(ReadOnlySpan<float> voice, Span<float> bgmL, Span<float> bgmR, bool voiceActive)
    {
        var pv = Band(voice, _bpV);
        double pb = 0;
        for (var i = 0; i < bgmL.Length; i++) _f0[i] = 0.5f * (bgmL[i] + bgmR[i]);
        pb = Band(_f0.AsSpan(0, bgmL.Length), _bpB);
        _voiceBandDb = 10f * MathF.Log10((float)Math.Max(pv, 1e-12)); _bgmBandDb = 10f * MathF.Log10((float)Math.Max(pb, 1e-12));
        var want = MaskingGuard && voiceActive ? Math.Clamp(_bgmBandDb - (_voiceBandDb - 6f), 0f, 9f) : 0f;
        _notchDb += Math.Clamp(want - _notchDb, -1f, 1f);
        if (_notchDb < 0.05f) return;
        NotchBlocks++;
        // RBJ 피킹(1 kHz · Q 0.7 · −notch dB)
        var A = Math.Pow(10, -_notchDb / 40); var w = 2 * Math.PI * 1000 / 48000; var alpha = Math.Sin(w) / (2 * 0.7); var cw = Math.Cos(w);
        var a0 = 1 + alpha / A;
        double b0 = (1 + alpha * A) / a0, b1 = -2 * cw / a0, b2 = (1 - alpha * A) / a0, a1 = -2 * cw / a0, a2 = (1 - alpha / A) / a0;
        Peak(bgmL, b0, b1, b2, a1, a2, 0); Peak(bgmR, b0, b1, b2, a1, a2, 2);
    }

    private void Peak(Span<float> x, double b0, double b1, double b2, double a1, double a2, int o)
    {
        for (var i = 0; i < x.Length; i++)
        {
            double v = x[i];
            var y = b0 * v + _nz[o]; _nz[o] = b1 * v - a1 * y + _nz[o + 1]; _nz[o + 1] = b2 * v - a2 * y;
            x[i] = (float)y;
        }
    }

    /// <summary>마스터 처리(제자리 · 길이 = 800) — 위상 → 측정 → 메이크업 → −1 dBTP 리미터 → 출력 측정.</summary>
    public void ProcessMaster(Span<float> l, Span<float> r)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (l.Length != Block || r.Length != Block) throw new ArgumentException("block 800");
        double lr = 0, ll = 0, rr = 0;
        for (var i = 0; i < Block; i++) { lr += l[i] * r[i]; ll += l[i] * l[i]; rr += r[i] * r[i]; }
        Correlation = ll > 1e-12 && rr > 1e-12 ? (float)(lr / Math.Sqrt(ll * rr)) : 1f;
        var wantSide = PhaseGuard && Correlation < -0.2f ? MathF.Max(_side - 0.2f, 0.2f) : MathF.Min(_side + 0.02f, 1f);
        if (wantSide < 1f && Correlation < -0.2f) PhaseFixBlocks++;
        _side = wantSide;
        if (_side < 0.999f)
            for (var i = 0; i < Block; i++) { var m = 0.5f * (l[i] + r[i]); var s = 0.5f * (l[i] - r[i]) * _side; l[i] = m + s; r[i] = m - s; }
        Input.Process(l, r);
        if (Normalize)
        {
            var meas = Input.GatedSeconds >= 3 ? Input.IntegratedLufs : Input.ShortTermLufs > -69f ? Input.ShortTermLufs : Input.MomentaryLufs;
            if (meas > -69f)
            {
                var want = Math.Clamp(TargetLufs - meas, -20f, 20f);
                _makeupDb += Math.Clamp(want - _makeupDb, -3f * Block / 48000f, 3f * Block / 48000f);
            }
            if (Output.ShortTermLufs > -69f)
                _trimDb = Math.Clamp(_trimDb + Math.Clamp((TargetLufs - Output.ShortTermLufs) * 0.05f, -0.5f * Block / 48000f, 0.5f * Block / 48000f), -3f, 6f);
        }
        else _makeupDb = _trimDb = 0;
        _lim.InputGain = MathF.Pow(10f, (_makeupDb + _trimDb) / 20f);
        _lim.Process(l, r, _f0, _f1);
        SafetyTruePeak(l, r);
        Output.Process(l, r);
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>ISP 여유 → 채널별 4× 트루 피크 검증 → 천장 초과 시 블록 비상 감쇄(무할당).</summary>
    private void SafetyTruePeak(Span<float> l, Span<float> r)
    {
        _tpL.AsSpan(Block, Hist).CopyTo(_tpL); _tpR.AsSpan(Block, Hist).CopyTo(_tpR);
        for (var i = 0; i < Block; i++) { l[i] *= IspMargin; r[i] *= IspMargin; _tpL[Hist + i] = l[i]; _tpR[Hist + i] = r[i]; }
        var tp = MathF.Max(FoleyAudioAnalysis.TruePeak(_tpL), FoleyAudioAnalysis.TruePeak(_tpR));
        if (tp > AcousticEnergyPeakSoftLimiterGuard.Ceiling)
        {
            OverCeilingBlocks++;
            var g = AcousticEnergyPeakSoftLimiterGuard.Ceiling / tp * 0.999f;
            for (var i = 0; i < Block; i++) { l[i] *= g; r[i] *= g; _tpL[Hist + i] = l[i]; _tpR[Hist + i] = r[i]; }
            tp *= g;
        }
        TruePeakDbtp = 20f * MathF.Log10(MathF.Max(tp, 1e-6f));
        if (TruePeakDbtp > MaxTruePeakDbtp) MaxTruePeakDbtp = TruePeakDbtp;
    }
}
