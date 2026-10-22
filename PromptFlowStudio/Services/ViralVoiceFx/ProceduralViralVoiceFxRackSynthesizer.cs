namespace PromptFlowStudio.Services.ViralVoiceFx;

[Flags]
public enum ViralVoiceFx : byte { None = 0, PhoneEq = 1, CyberVocoder = 2, ReverbPunch = 4, DeepBass = 8, All = 15 }

/// <summary>RBJ 바이쿼드(TDF-II · 값 형식 상태).</summary>
public struct VoiceBiquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static VoiceBiquad Lowpass(float fs, float f, float q) => Make(fs, f, q, 0);
    public static VoiceBiquad Highpass(float fs, float f, float q) => Make(fs, f, q, 1);

    /// <summary>로우 셸프(S = 1) — <paramref name="gainDb"/> 저역 증폭.</summary>
    public static VoiceBiquad LowShelf(float fs, float f, float gainDb)
    {
        var a = MathF.Pow(10f, gainDb / 40f);
        var w = 2f * MathF.PI * f / fs; var cs = MathF.Cos(w); var al = MathF.Sin(w) / 2f * MathF.Sqrt(2f);
        var sq = 2f * MathF.Sqrt(a) * al;
        var a0 = (a + 1) + (a - 1) * cs + sq;
        return new VoiceBiquad
        {
            _b0 = a * ((a + 1) - (a - 1) * cs + sq) / a0, _b1 = 2 * a * ((a - 1) - (a + 1) * cs) / a0, _b2 = a * ((a + 1) - (a - 1) * cs - sq) / a0,
            _a1 = -2 * ((a - 1) + (a + 1) * cs) / a0, _a2 = ((a + 1) + (a - 1) * cs - sq) / a0,
        };
    }

    private static VoiceBiquad Make(float fs, float f, float q, int kind)
    {
        var w = 2f * MathF.PI * f / fs; var cs = MathF.Cos(w); var al = MathF.Sin(w) / (2f * q); var a0 = 1f + al;
        var b1 = kind == 0 ? 1f - cs : -(1f + cs);
        var b0 = kind == 0 ? (1f - cs) / 2f : (1f + cs) / 2f;
        return new VoiceBiquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b0 / a0, _a1 = -2f * cs / a0, _a2 = (1f - al) / a0 };
    }

    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Clear() => _z1 = _z2 = 0f;
}

/// <summary>
/// [PromptFlow Studio v17.80.0] 🎛️ 절차적 바이럴 보이스 FX 랙(<see cref="Process(Span{float})"/> 제자리 · 무할당 · 외부 오디오 라이브러리 0).
///   1. <b>전화기 EQ</b>: 300 Hz 하이패스 ×2 + 3.4 kHz 로우패스 ×2(4 차 대역통과) → tanh 경미 새츄레이션(드라이브 1.8) + 메이크업.
///   2. <b>사이버 보코더</b>: 110 Hz 사인 캐리어 링 모듈레이션(측파대 기계음) + 3 ms 금속성 콤(피드백 0.55) · 드라이 15 % 명료도 유지.
///   3. <b>리버브 펀치</b>: Schroeder 병렬 콤 4 + 직렬 올패스 2 — 콤 이득 g = 10^(−3·d / (T60·Fs)) 로 T60 = 1.2 s 테일.
///      <see cref="Punch"/>(감정 클라이맥스) 시 180 ms 센드 게이트 개방 → 이후 입력 차단 · 테일만 자연 감쇠.
///   4. <b>딥 베이스</b>: 220 Hz 저역 추출 → 상승 영교차 플립플롭(옥타브 분주 f/2) × 포락 추종 → 140 Hz 로우패스 ×2 서브하모닉 + 120 Hz +6 dB 셸프.
/// 처리 순서: 보코더 → 딥 베이스 → 전화기 EQ → 리버브.
/// </summary>
public sealed class ProceduralViralVoiceFxRackSynthesizer
{
    public const int Fs = 48_000, CombCount = 4, AllpassCount = 2, VocoderComb = 144, PunchSendSamples = Fs * 180 / 1000;
    public const float ReverbT60 = 1.2f, CarrierHz = 110f, PhoneLowHz = 300f, PhoneHighHz = 3400f, PhoneDrive = 1.8f;
    private static readonly int[] CombDelays = { 1693, 1759, 1621, 1547 };
    private static readonly int[] AllpassDelays = { 605, 479 };

    private readonly float[][] _comb = new float[CombCount][];
    private readonly float[][] _ap = new float[AllpassCount][];
    private readonly int[] _combIdx = new int[CombCount], _apIdx = new int[AllpassCount];
    private readonly float[] _combG = new float[CombCount];
    private readonly float[] _vComb = new float[VocoderComb];
    private VoiceBiquad _hp1, _hp2, _lp1, _lp2, _bassLp, _subLp1, _subLp2, _shelf;
    private readonly float _phoneNorm;
    private int _vIdx, _sendLeft;
    private double _phase;
    private float _send, _env, _prevLo, _flip = 1f;

    public ProceduralViralVoiceFxRackSynthesizer()
    {
        for (var c = 0; c < CombCount; c++)
        {
            _comb[c] = new float[CombDelays[c]];
            _combG[c] = MathF.Pow(10f, -3f * CombDelays[c] / (ReverbT60 * Fs));
        }
        for (var a = 0; a < AllpassCount; a++) _ap[a] = new float[AllpassDelays[a]];
        _phoneNorm = 1f / MathF.Tanh(PhoneDrive);
        ResetFilters();
    }

    public ViralVoiceFx Active { get; set; }
    public float ReverbMix { get; set; } = 0.45f;
    public float BassMix { get; set; } = 0.8f;
    public float VocoderDry { get; set; } = 0.15f;
    public long Punches { get; private set; }
    public bool PunchOpen => _sendLeft > 0;
    /// <summary>직전 블록 리버브 웻 피크(테일 미터).</summary>
    public float TailLevel { get; private set; }
    /// <summary>직전 블록 서브하모닉 피크.</summary>
    public float SubLevel { get; private set; }
    public int ActiveCount => ((int)Active & 1) + ((int)Active >> 1 & 1) + ((int)Active >> 2 & 1) + ((int)Active >> 3 & 1);

    public static string FxName(ViralVoiceFx fx) => fx switch
    {
        ViralVoiceFx.PhoneEq => "전화기 EQ", ViralVoiceFx.CyberVocoder => "사이버 보코더", ViralVoiceFx.ReverbPunch => "리버브 펀치", ViralVoiceFx.DeepBass => "딥 베이스", _ => "없음",
    };

    public static string FxIcon(ViralVoiceFx fx) => fx switch
    {
        ViralVoiceFx.PhoneEq => "📞", ViralVoiceFx.CyberVocoder => "🤖", ViralVoiceFx.ReverbPunch => "💥", ViralVoiceFx.DeepBass => "🔊", _ => "·",
    };

    public void Toggle(ViralVoiceFx fx) => Active ^= fx;

    /// <summary>감정 클라이맥스 — 리버브 센드 180 ms 개방(1.2 s 테일).</summary>
    public void Punch() { _sendLeft = PunchSendSamples; Punches++; }

    public void Reset()
    {
        for (var c = 0; c < CombCount; c++) { Array.Clear(_comb[c]); _combIdx[c] = 0; }
        for (var a = 0; a < AllpassCount; a++) { Array.Clear(_ap[a]); _apIdx[a] = 0; }
        Array.Clear(_vComb); _vIdx = 0; _sendLeft = 0; _phase = 0; _send = _env = _prevLo = 0f; _flip = 1f;
        TailLevel = SubLevel = 0f; Punches = 0;
        ResetFilters();
    }

    private void ResetFilters()
    {
        _hp1 = _hp2 = VoiceBiquad.Highpass(Fs, PhoneLowHz, 0.7071f);
        _lp1 = _lp2 = VoiceBiquad.Lowpass(Fs, PhoneHighHz, 0.7071f);
        _bassLp = VoiceBiquad.Lowpass(Fs, 220f, 0.7071f);
        _subLp1 = _subLp2 = VoiceBiquad.Lowpass(Fs, 140f, 0.7071f);
        _shelf = VoiceBiquad.LowShelf(Fs, 120f, 6f);
    }

    /// <summary>제자리 블록 처리(활성 FX 만 · 무할당).</summary>
    public void Process(Span<float> buf)
    {
        var fx = Active;
        float tailPk = 0f, subPk = 0f;
        var step = 2.0 * Math.PI * CarrierHz / Fs;
        for (var i = 0; i < buf.Length; i++)
        {
            var x = buf[i];
            if ((fx & ViralVoiceFx.CyberVocoder) != 0)
            {
                var ring = x * (float)Math.Sin(_phase) * 1.6f;
                _phase += step; if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                var c = ring + 0.55f * _vComb[_vIdx];
                _vComb[_vIdx] = c; if (++_vIdx == VocoderComb) _vIdx = 0;
                x = x * VocoderDry + c * 0.7f;
            }
            if ((fx & ViralVoiceFx.DeepBass) != 0)
            {
                var lo = _bassLp.Process(x);
                var a = MathF.Abs(lo);
                _env += (a - _env) * (a > _env ? 0.01f : 0.0007f);
                if (_prevLo < 0f && lo >= 0f && _env > 1e-3f) _flip = -_flip;
                _prevLo = lo;
                var sub = _subLp2.Process(_subLp1.Process(_flip * _env * 1.4f));
                if (MathF.Abs(sub) > subPk) subPk = MathF.Abs(sub);
                x = _shelf.Process(x) + sub * BassMix;
            }
            if ((fx & ViralVoiceFx.PhoneEq) != 0)
            {
                var bp = _lp2.Process(_lp1.Process(_hp2.Process(_hp1.Process(x))));
                x = MathF.Tanh(bp * PhoneDrive) * _phoneNorm * 1.25f;
            }
            if ((fx & ViralVoiceFx.ReverbPunch) != 0)
            {
                var target = _sendLeft > 0 ? 1f : 0f;
                if (_sendLeft > 0) _sendLeft--;
                _send += (target - _send) * 0.004f;
                var input = x * _send * 0.25f;
                var sum = 0f;
                for (var c = 0; c < CombCount; c++)
                {
                    var line = _comb[c]; var k = _combIdx[c];
                    var y = line[k];
                    line[k] = input + y * _combG[c];
                    if (++k == line.Length) k = 0;
                    _combIdx[c] = k;
                    sum += y;
                }
                for (var a = 0; a < AllpassCount; a++)
                {
                    var line = _ap[a]; var k = _apIdx[a];
                    var d = line[k];
                    var v = sum + d * 0.5f;
                    line[k] = v;
                    sum = d - v * 0.5f;
                    if (++k == line.Length) k = 0;
                    _apIdx[a] = k;
                }
                var wet = sum * ReverbMix;
                if (MathF.Abs(wet) > tailPk) tailPk = MathF.Abs(wet);
                x += wet;
            }
            buf[i] = x;
        }
        TailLevel = tailPk;
        SubLevel = subPk;
    }

    /// <summary>Goertzel 단일 주파수 진폭(사인 진폭 단위) — 분석/검증 보조.</summary>
    public static double Goertzel(ReadOnlySpan<float> x, double freq, int fs = Fs)
    {
        var w = 2 * Math.PI * freq / fs; var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < x.Length; i++) { var s0 = x[i] + coeff * s1 - s2; s2 = s1; s1 = s0; }
        var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / Math.Max(1, x.Length);
    }
}
