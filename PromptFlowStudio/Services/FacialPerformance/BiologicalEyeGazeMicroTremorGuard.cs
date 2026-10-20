using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.FacialPerformance;

/// <summary>
/// [PromptFlow Studio v17.12.0] 👁️ 생체역학적 시선 안구운동 &amp; 미세 떨림 가드(<see cref="Process"/> 무할당 · 결정적 xorshift).
///   ▸ 시선 측정: 엔진 정규 좌표 홍채 5점 평균 → 안구 구면(반경 r = 0.005 / sin 25°) 요/피치 → 적응 평활(고정 잡음 &lt; 2° 강평활 · 홱보기 즉시 통과).
///   ▸ 주 시퀀스(main sequence) 속도 제한: v_max(A) = 500°/s·(1 − e^(−A/15°)) + 20°/s(추종) — 순간 이동 시선 차단.
///   ▸ 이향(vergence) 제한: 좌우 요 차 ≤ 8° · 피치 차 ≤ 1.5°(양안 결합).
///   ▸ Dead-Eye 감지: 1.5 s EMA 평활 시선 표준편차 &lt; 0.35° 그리고 측정 깜빡임 공백 &gt; 5 s(히스테리시스) → 생체 주입 페이드 인.
///   ▸ 주입: 미세 홱보기(포아송 1.4 /s · 0.15–0.8° · 주 시퀀스 이동 · 0.4 s 복귀 드리프트) · 안구 드리프트(랜덤 워크 · ±0.6° 한계) ·
///     미세 떨림(OMT 83 Hz · 0.008°) · 무의식 깜빡임(간격 2–6 s · 닫힘 75 ms / 유지 25 ms / 열림 150 ms 비대칭) · 하방 시선 눈꺼풀 추종.
/// </summary>
public sealed class BiologicalEyeGazeMicroTremorGuard
{
    public const float SaccadePeak = 500f, SaccadeA0 = 15f, PursuitDegS = 20f, VergenceMax = 8f, PitchCoupling = 1.5f;
    public const float DeadStdDeg = 0.35f, FixNoiseDeg = 2f, DeadBlinkGapS = 5f, CloseS = 0.075f, HoldS = 0.025f, OpenS = 0.15f;
    public const int BlinkRing = 32;

    private readonly float[] _outYaw = new float[2], _outPitch = new float[2], _measYaw = new float[2], _measPitch = new float[2], _fYaw = new float[2], _fPitch = new float[2];
    private readonly float[] _blinkTimes = new float[BlinkRing], _msTimes = new float[BlinkRing];
    private uint _rng = 0x51ED27u;
    private float _time, _meanY, _meanP, _varY = 1f, _varP = 1f, _lastMeasBlink, _nextBlink = 2.5f, _blinkStart = -10f, _nextMs = 0.5f;
    private float _msYaw, _msPitch, _driftYaw, _driftPitch, _inject;
    private bool _measBlinkHigh;
    private int _blinkHead, _msHead;

    public bool Enabled { get; set; } = true;
    /// <summary>Dead-Eye 감지와 무관하게 주입 강제.</summary>
    public bool ForceInject { get; set; }
    public bool SaccadeLimit { get; set; } = true;
    public float BlinkIntervalS { get; set; } = 4f;
    public float MicrosaccadeRate { get; set; } = 1.4f;
    public float TremorDeg { get; set; } = 0.008f;

    public bool DeadEyes { get; private set; }
    public float InjectionLevel => _inject;
    public float GazeStdDeg => MathF.Sqrt(0.5f * (_varY + _varP));
    public float SinceBlinkS => _time - _lastMeasBlink;
    public float BlinkL { get; private set; }
    public float BlinkR { get; private set; }
    public float InjectedBlink { get; private set; }
    public long InjectedBlinks { get; private set; }
    public long InjectedMicrosaccades { get; private set; }
    public float LastMicrosaccadeDeg { get; private set; }
    public float PeakVelocityDegS { get; private set; }
    public float VergenceDeg => _outYaw[0] - _outYaw[1];
    public float Yaw(int eye) => _outYaw[eye];
    public float Pitch(int eye) => _outPitch[eye];
    public float MeasuredYaw(int eye) => _measYaw[eye];
    public float MeasuredPitch(int eye) => _measPitch[eye];
    public float MicroYaw => _msYaw + _driftYaw;
    public float MicroPitch => _msPitch + _driftPitch;
    public double LastGuardUs { get; private set; }
    public float Time => _time;

    /// <summary>최근 60 s 깜빡임 빈도(회/분 · 측정 + 주입).</summary>
    public float BlinkRatePerMin => RateIn(_blinkTimes, 60f) * (60f / MathF.Min(60f, MathF.Max(1f, _time)));
    /// <summary>최근 10 s 미세 홱보기 빈도(회/초).</summary>
    public float MicrosaccadePerS => RateIn(_msTimes, 10f) / MathF.Min(10f, MathF.Max(1f, _time));

    private float RateIn(float[] ring, float window)
    {
        var n = 0;
        for (var i = 0; i < BlinkRing; i++) if (ring[i] > 0 && _time - ring[i] <= window) n++;
        return n;
    }

    public void Reset()
    {
        Array.Clear(_outYaw); Array.Clear(_outPitch); Array.Clear(_fYaw); Array.Clear(_fPitch); Array.Clear(_blinkTimes); Array.Clear(_msTimes);
        _time = 0; _meanY = _meanP = 0; _varY = _varP = 1f; _lastMeasBlink = 0; _nextBlink = 2.5f; _blinkStart = -10f; _nextMs = 0.5f;
        _msYaw = _msPitch = _driftYaw = _driftPitch = _inject = 0; DeadEyes = false; InjectedBlinks = InjectedMicrosaccades = 0; PeakVelocityDegS = 0;
    }

    private float U()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    public static float MainSequenceDegS(float amplitudeDeg) => SaccadePeak * (1f - MathF.Exp(-MathF.Abs(amplitudeDeg) / SaccadeA0)) + PursuitDegS;

    /// <summary>깜빡임 프로파일(닫힘 빠름 · 열림 느림).</summary>
    public static float BlinkProfile(float t)
    {
        if (t < 0) return 0f;
        if (t < CloseS) { var u = t / CloseS; return u * u * (3f - 2f * u); }
        if (t < CloseS + HoldS) return 1f;
        if (t < CloseS + HoldS + OpenS) { var u = 1f - (t - CloseS - HoldS) / OpenS; return u * u * (3f - 2f * u); }
        return 0f;
    }

    /// <summary>측정 시선(L/R) · 측정 깜빡임 → 생체 타당 시선/깜빡임(무할당).</summary>
    public void Process(float yawL, float pitchL, float yawR, float pitchR, float blinkL, float blinkR, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _time += dt;
        _measYaw[0] = yawL; _measPitch[0] = pitchL; _measYaw[1] = yawR; _measPitch[1] = pitchR;
        // 측정 잡음 적응 평활: 고정 잡음(&lt; 2°) 강평활 · 홱보기(큰 변화) 즉시 통과
        for (var e = 0; e < 2; e++)
        {
            float dy = _measYaw[e] - _fYaw[e], dp = _measPitch[e] - _fPitch[e];
            var al = Math.Clamp((MathF.Sqrt(dy * dy + dp * dp) - FixNoiseDeg) / (2f * FixNoiseDeg), 0.08f, 1f);
            _fYaw[e] += dy * al; _fPitch[e] += dp * al;
        }
        yawL = _fYaw[0]; pitchL = _fPitch[0]; yawR = _fYaw[1]; pitchR = _fPitch[1];
        if (!Enabled)
        {
            for (var e = 0; e < 2; e++) { _outYaw[e] = _fYaw[e] = _measYaw[e]; _outPitch[e] = _fPitch[e] = _measPitch[e]; }
            BlinkL = blinkL; BlinkR = blinkR; InjectedBlink = 0; _inject = 0; DeadEyes = false;
            LastGuardUs = 0;
            return;
        }

        // Dead-Eye 감지
        float my = 0.5f * (yawL + yawR), mp = 0.5f * (pitchL + pitchR);
        var a = 1f - MathF.Exp(-dt / 1.5f);
        _meanY += (my - _meanY) * a; _meanP += (mp - _meanP) * a;
        _varY += ((my - _meanY) * (my - _meanY) - _varY) * a; _varP += ((mp - _meanP) * (mp - _meanP) - _varP) * a;
        var mb = MathF.Max(blinkL, blinkR);
        if (mb > 0.5f && !_measBlinkHigh) { _lastMeasBlink = _time; Record(_blinkTimes, ref _blinkHead); }
        _measBlinkHigh = mb > 0.5f;
        var still = GazeStdDeg < DeadStdDeg;
        if (!DeadEyes && still && SinceBlinkS > DeadBlinkGapS) DeadEyes = true;
        else if (DeadEyes && (GazeStdDeg > DeadStdDeg * 3f || SinceBlinkS < 0.5f)) DeadEyes = false;
        var want = DeadEyes || ForceInject ? 1f : 0f;
        _inject += (want - _inject) * (1f - MathF.Exp(-dt / 0.3f));

        // 미세 홱보기 · 드리프트 · 떨림
        if (_time >= _nextMs)
        {
            _nextMs = _time - MathF.Log(MathF.Max(1e-4f, U())) / MathF.Max(0.05f, MicrosaccadeRate);
            if (_inject > 0.5f)
            {
                var ang = MathF.Tau * U(); var amp = 0.15f + 0.65f * U();
                _msYaw += amp * MathF.Cos(ang); _msPitch += amp * MathF.Sin(ang);
                var m = MathF.Sqrt(_msYaw * _msYaw + _msPitch * _msPitch);
                if (m > 0.9f) { _msYaw *= 0.9f / m; _msPitch *= 0.9f / m; }
                LastMicrosaccadeDeg = amp;
                InjectedMicrosaccades++;
                Record(_msTimes, ref _msHead);
            }
        }
        var back = 1f - MathF.Exp(-dt / 0.4f);
        _msYaw -= _msYaw * back; _msPitch -= _msPitch * back;
        var sd = 0.1f * MathF.Sqrt(dt) * 1.7320508f;
        _driftYaw = Math.Clamp(_driftYaw + sd * (2f * U() - 1f) - _driftYaw * dt * 0.5f, -0.6f, 0.6f);
        _driftPitch = Math.Clamp(_driftPitch + sd * (2f * U() - 1f) - _driftPitch * dt * 0.5f, -0.6f, 0.6f);
        var tremor = TremorDeg * MathF.Sin(MathF.Tau * 83f * _time);
        float iy = _inject * (_msYaw + _driftYaw + tremor), ip = _inject * (_msPitch + _driftPitch + tremor * 0.7f);

        // 목표 → 이향 제한 → 주 시퀀스 이동
        float ty0 = yawL + iy, ty1 = yawR + iy, tp0 = pitchL + ip, tp1 = pitchR + ip;
        var verg = ty0 - ty1;
        if (MathF.Abs(verg) > VergenceMax) { var c = 0.5f * (ty0 + ty1); var h = 0.5f * MathF.Sign(verg) * VergenceMax; ty0 = c + h; ty1 = c - h; }
        var pd = tp0 - tp1;
        if (MathF.Abs(pd) > PitchCoupling) { var c = 0.5f * (tp0 + tp1); var h = 0.5f * MathF.Sign(pd) * PitchCoupling; tp0 = c + h; tp1 = c - h; }
        var peak = 0f;
        for (var e = 0; e < 2; e++)
        {
            float dy = (e == 0 ? ty0 : ty1) - _outYaw[e], dp = (e == 0 ? tp0 : tp1) - _outPitch[e];
            var amp = MathF.Sqrt(dy * dy + dp * dp);
            var step = amp;
            if (SaccadeLimit) step = MathF.Min(amp, MainSequenceDegS(amp) * dt);
            if (amp > 1e-6f) { _outYaw[e] += dy * (step / amp); _outPitch[e] += dp * (step / amp); }
            peak = MathF.Max(peak, step / dt);
        }
        PeakVelocityDegS = MathF.Max(PeakVelocityDegS * 0.98f, peak);

        // 무의식 깜빡임 · 하방 시선 눈꺼풀
        if (_time >= _nextBlink)
        {
            var mean = MathF.Max(1f, BlinkIntervalS);
            _nextBlink = _time + mean * (0.5f + U());
            if (_inject > 0.5f && _time - _blinkStart > 0.5f && mb < 0.3f)
            {
                _blinkStart = _time;
                InjectedBlinks++;
                Record(_blinkTimes, ref _blinkHead);
            }
        }
        InjectedBlink = _inject * BlinkProfile(_time - _blinkStart);
        float lidL = 0.25f * Math.Clamp(-_outPitch[0] / NeuralFacialLandmarkBlendshapeEngine.MaxGazeDeg, 0f, 1f), lidR = 0.25f * Math.Clamp(-_outPitch[1] / NeuralFacialLandmarkBlendshapeEngine.MaxGazeDeg, 0f, 1f);
        BlinkL = Math.Clamp(MathF.Max(blinkL, InjectedBlink) + lidL * (1f - MathF.Max(blinkL, InjectedBlink)), 0f, 1f);
        BlinkR = Math.Clamp(MathF.Max(blinkR, InjectedBlink) + lidR * (1f - MathF.Max(blinkR, InjectedBlink)), 0f, 1f);
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Record(float[] ring, ref int head)
    {
        ring[head] = _time;
        head = (head + 1) % BlinkRing;
    }

    /// <summary>가드 출력 → 52 배열(eyeLook 8 · eyeBlink 2).</summary>
    public void WriteEyes(Span<float> w52)
    {
        NeuralFacialLandmarkBlendshapeEngine.GazeToWeights(1, _outYaw[0], _outPitch[0], w52);
        NeuralFacialLandmarkBlendshapeEngine.GazeToWeights(-1, _outYaw[1], _outPitch[1], w52);
        w52[(int)ArkitBlendshape.EyeBlinkLeft] = BlinkL;
        w52[(int)ArkitBlendshape.EyeBlinkRight] = BlinkR;
    }
}
