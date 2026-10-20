namespace PromptFlowStudio.Services.SpatialHaptics;

/// <summary>재질 미세 텍스처 프로파일: 공간 파장 2성분(mm) · 상대 진폭 · 거칠기 잡음 · 스틱-슬립 강도.</summary>
public readonly record struct VibroTexture(float Lambda1Mm, float Amp1, float Lambda2Mm, float Amp2, float Roughness, float StickSlip);

/// <summary>
/// [PromptFlow Studio v17.24.0] 〰 진동 촉각 미세 텍스처 &amp; 생체 맥동 주파수 합성기(<see cref="RenderMs"/> · <see cref="Analyze"/> 무할당).
///   ▸ 텍스처: v17.16 직물(실크 0.12 mm · 데님 능직 0.8 mm · 가죽 결 1.2 mm) 공간 파장 λ → 시간 주파수 f = v_slide / λ (위상 누산 · 진폭 ∝ Fn · 속도 램프).
///   ▸ 거칠기: 백색 잡음 1극 고역 · 스틱-슬립: 미끄럼 누적 변위 &gt; 임계 시 250 Hz 감쇠 클릭(가죽/피부 강) — v17.19 젖음 피막이 고역 거칠기 −50 % · 스틱-슬립 −90 % 억제.
///   ▸ 맥동: v17.17 PPG 수축기 상승(위상 0.1 교차) 시 피부 접촉 HIP 에 40 Hz 3주기 버스트(진폭 ∝ Fn).
///   ▸ 대역: 2차 버터워스 고역 20 Hz + 저역 1000 Hz(Fs 4 kHz) → tanh 소프트 클립 → 4096 샘플 링버퍼.
///   ▸ 분석: 상수-Q(Q 5) 대역 통과 필터뱅크 24 로그 빈(20–1000 Hz) 평균 제곱 → 프레임당 1열 · 64 열 스펙트로그램.
/// </summary>
public sealed class VibrotactileMicroTextureSynthesizer
{
    public const int Fs = 4000, PerMs = Fs / 1000, Ring = 4096, Bins = 24, Cols = 64, Hips = RealtimeKinestheticForceFeedbackEngine.Hips;
    public const float FMin = 20f, FMax = 1000f, BankQ = 5f, Gain = 0.18f, ClickHz = 250f, PulseHz = 40f;
    public static readonly VibroTexture[] Textures =
    {
        new(0.45f, 0.3f, 1.6f, 0.2f, 0.15f, 0.6f),    // 피부
        new(0.12f, 0.15f, 0.35f, 0.05f, 0.04f, 0.05f), // 실크
        new(0.3f, 0.4f, 0.9f, 0.2f, 0.25f, 0.2f),      // 면
        new(0.8f, 1f, 2.4f, 0.35f, 0.3f, 0.25f),       // 데님
        new(1.2f, 0.35f, 3.5f, 0.25f, 0.2f, 0.9f),     // 가죽
        new(0.6f, 0.3f, 4f, 0.15f, 0.2f, 0.3f),        // 소품(목재)
    };

    private readonly float[] _ring = new float[Ring];
    private readonly float[] _hz = new float[Bins], _mag = new float[Bins], _bb0 = new float[Bins], _ba1 = new float[Bins], _ba2 = new float[Bins];
    private readonly float[] _bx1 = new float[Bins], _bx2 = new float[Bins], _by1 = new float[Bins], _by2 = new float[Bins], _be = new float[Bins];
    private readonly float[] _gram = new float[Cols * Bins];
    private readonly double[] _ph1 = new double[Hips], _ph2 = new double[Hips];
    private readonly float[] _slip = new float[Hips], _clickEnv = new float[Hips], _clickPh = new float[Hips], _burstEnv = new float[Hips], _burstPh = new float[Hips];
    private readonly float[] _level = new float[Hips], _nzPrev = new float[Hips], _nzHp = new float[Hips];
    private readonly float[] _hp = new float[5], _lp = new float[5];
    private float _hx1, _hx2, _hy1, _hy2, _lx1, _lx2, _ly1, _ly2;
    private int _head, _col, _colCount, _acc;
    private float _s2, _pk;
    private uint _rng = 0x2545F491u;
    private float _lastPulse;

    public VibrotactileMicroTextureSynthesizer()
    {
        for (var b = 0; b < Bins; b++)
        {
            _hz[b] = FMin * MathF.Pow(FMax / FMin, b / (float)(Bins - 1));
            var w = 2f * MathF.PI * _hz[b] / Fs;
            var al = MathF.Sin(w) / (2f * BankQ);
            _bb0[b] = al / (1f + al); _ba1[b] = -2f * MathF.Cos(w) / (1f + al); _ba2[b] = (1f - al) / (1f + al);
        }
        Biquad(FMin, true, _hp);
        Biquad(FMax, false, _lp);
    }

    public bool Enabled { get; set; } = true;
    public bool PulseEnabled { get; set; } = true;
    public long Samples { get; private set; }
    public long Clicks { get; private set; }
    public long Bursts { get; private set; }
    public float Rms { get; private set; }
    public float Peak { get; private set; }
    public float PeakHz { get; private set; }
    public static float DominantHz(HapticMaterial m, float slide) => slide / (Textures[(int)m].Lambda1Mm * 1e-3f);
    public float Level(int hip) => _level[hip];
    public float BinHz(int b) => _hz[b];
    public float Magnitude(int b) => _mag[b];
    public int ColumnCount => _colCount;
    /// <summary>스펙트로그램(열 c: 0 = 최신 · dB).</summary>
    public float Gram(int c, int b) => _gram[((_col - 1 - c + 2 * Cols) % Cols) * Bins + b];
    public float Sample(int back) => _ring[(_head - 1 - back + 2 * Ring) % Ring];

    private static void Biquad(float f0, bool high, float[] c)
    {
        var w = 2f * MathF.PI * f0 / Fs;
        var cw = MathF.Cos(w);
        var al = MathF.Sin(w) / (2f * 0.70710678f);
        var a0 = 1f + al;
        float b0, b1;
        if (high) { b0 = (1f + cw) * 0.5f; b1 = -(1f + cw); }
        else { b0 = (1f - cw) * 0.5f; b1 = 1f - cw; }
        c[0] = b0 / a0; c[1] = b1 / a0; c[2] = b0 / a0; c[3] = -2f * cw / a0; c[4] = (1f - al) / a0;
    }

    private float Noise()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (2f / 16777216f) - 1f;
    }

    /// <summary>1 ms(4 샘플) 합성: 접촉 HIP별 텍스처 · 스틱-슬립 · 맥동 버스트 합산 → 대역 필터 → 링버퍼.</summary>
    public void RenderMs(ReadOnlySpan<HapticContact> contacts, float wetness, float pulse)
    {
        var wet = Math.Clamp(wetness, 0f, 1f);
        var onset = PulseEnabled && _lastPulse < 0.5f && pulse >= 0.5f && pulse > _lastPulse;
        _lastPulse = pulse;
        const float dt = 1f / Fs;
        var hpA = 1f / (1f + 2f * MathF.PI * 120f * dt);
        var clickDecay = MathF.Exp(-dt / 0.006f);
        var burstDecay = MathF.Exp(-dt / 0.03f);
        for (var n = 0; n < PerMs; n++)
        {
            var acc = 0f;
            for (var i = 0; i < contacts.Length && i < Hips; i++)
            {
                ref readonly var c = ref contacts[i];
                if (!c.InContact || !Enabled) { _level[i] *= 0.995f; _clickEnv[i] *= clickDecay; _burstEnv[i] *= burstDecay; continue; }
                var tx = Textures[(int)c.Material];
                var v = c.SlideSpeed;
                var fn = c.NormalForce;
                var ramp = MathF.Min(1f, v / 0.02f);
                var amp = Gain * fn * ramp;
                _ph1[i] += v * dt / (tx.Lambda1Mm * 1e-3f);
                _ph2[i] += v * dt / (tx.Lambda2Mm * 1e-3f);
                if (_ph1[i] > 1e4) { _ph1[i] %= 1.0; _ph2[i] %= 1.0; }
                var nz = Noise();
                _nzHp[i] = hpA * (_nzHp[i] + nz - _nzPrev[i]); _nzPrev[i] = nz;
                var tex = tx.Amp1 * (float)Math.Sin(2.0 * Math.PI * _ph1[i]) + (1f - 0.5f * wet) * (tx.Amp2 * (float)Math.Sin(2.0 * Math.PI * _ph2[i]) + tx.Roughness * _nzHp[i]);
                var y = amp * tex;
                // 스틱-슬립
                if (!c.Sticking && v > 1e-4f)
                {
                    _slip[i] += v * dt;
                    if (_slip[i] > 0.0004f)
                    {
                        _slip[i] = 0f;
                        var e = tx.StickSlip * (1f - 0.9f * wet) * Gain * fn;
                        if (e > _clickEnv[i]) _clickEnv[i] = e;
                        _clickPh[i] = 0f;
                        Clicks++;
                    }
                }
                _clickPh[i] += ClickHz * dt;
                y += _clickEnv[i] * MathF.Sin(2f * MathF.PI * _clickPh[i]);
                _clickEnv[i] *= clickDecay;
                // 맥동 버스트(피부)
                if (onset && n == 0 && c.Material == HapticMaterial.Skin && fn > 0.2f) { _burstEnv[i] = 0.35f * Gain * fn; _burstPh[i] = 0f; Bursts++; }
                _burstPh[i] += PulseHz * dt;
                if (_burstPh[i] > 3f) _burstEnv[i] = 0f;
                y += _burstEnv[i] * MathF.Sin(2f * MathF.PI * _burstPh[i]);
                _burstEnv[i] *= burstDecay;
                var ay = MathF.Abs(y);
                _level[i] += 0.002f * (ay - _level[i]);
                acc += y;
            }
            // 대역 필터(20 Hz HP → 1 kHz LP)
            var h = _hp[0] * acc + _hp[1] * _hx1 + _hp[2] * _hx2 - _hp[3] * _hy1 - _hp[4] * _hy2;
            _hx2 = _hx1; _hx1 = acc; _hy2 = _hy1; _hy1 = h;
            var l = _lp[0] * h + _lp[1] * _lx1 + _lp[2] * _lx2 - _lp[3] * _ly1 - _lp[4] * _ly2;
            _lx2 = _lx1; _lx1 = h; _ly2 = _ly1; _ly1 = l;
            var o = MathF.Tanh(l);
            _ring[_head] = o;
            _s2 += o * o; var ao = MathF.Abs(o); if (ao > _pk) _pk = ao;
            for (var b = 0; b < Bins; b++)
            {
                var y = _bb0[b] * (o - _bx2[b]) - _ba1[b] * _by1[b] - _ba2[b] * _by2[b];
                _bx2[b] = _bx1[b]; _bx1[b] = o; _by2[b] = _by1[b]; _by1[b] = y;
                _be[b] += y * y;
            }
            _acc++;
            _head = (_head + 1) % Ring;
            Samples++;
        }
    }

    /// <summary>직전 분석 이후 누적 대역 에너지 → 스펙트로그램 1열 · RMS · 피크(무할당).</summary>
    public void Analyze()
    {
        var n = _acc > 0 ? _acc : 1;
        Rms = MathF.Sqrt(_s2 / n); Peak = _pk;
        var best = -1f; var bestHz = 0f;
        var row = _col * Bins;
        for (var b = 0; b < Bins; b++)
        {
            var m = MathF.Sqrt(2f * _be[b] / n);
            _mag[b] = m;
            if (m > best) { best = m; bestHz = _hz[b]; }
            _gram[row + b] = 20f * MathF.Log10(MathF.Max(m, 1e-5f));
            _be[b] = 0f;
        }
        PeakHz = best > 1e-4f ? bestHz : 0f;
        _s2 = 0f; _pk = 0f; _acc = 0;
        _col = (_col + 1) % Cols;
        if (_colCount < Cols) _colCount++;
    }
}
