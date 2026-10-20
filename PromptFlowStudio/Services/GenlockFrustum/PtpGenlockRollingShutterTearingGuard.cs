namespace PromptFlowStudio.Services.GenlockFrustum;

/// <summary>
/// [PromptFlow Studio v17.44.0] 🖥️ PTP(IEEE 1588) 젠록 위상 동기화 &amp; 롤링셔터 밴딩 방어 가드(<see cref="AdvanceFrame"/> 무할당).
/// 카메라 셔터 펄스(PTP 그랜드마스터 기준 싱크 제너레이터)를 진실 시계로, LED 프로세서 V-Sync 의 위상차를 매 프레임
/// PTP 타임스탬프(측정 잡음 포함)로 추적한다. <b>젠록</b>: PI 루프(Kp 0.4 · Ki 0.08)가 주기 트림(ns)으로 드리프트/프레임레이트
/// 불일치(24 vs 23.976)를 흡수하고, |오차| ≤ 0.5 µs 24 프레임 연속 시 잠금. <b>가드</b>: |오차| &gt; 1.0 µs 감지 즉시 위상 스텝 +
/// 프레임 버퍼 재플립(보정 프레임)으로 스왑을 셔터 닫힘 구간 중앙에 되돌리고, LED PWM 주파수를 노출 시간의 정수 주기로 정렬해
/// 셔터 각도 불일치 주사선 밴딩을 제거한다. 스왑이 노출+리드아웃 구간에 들어가면 티어링, 위상 랩은 프레임 드롭으로 집계.
/// </summary>
public sealed class PtpGenlockRollingShutterTearingGuard
{
    public const double LockNs = 500.0, CorrectNs = 1000.0, FreqWindowNs = 200_000.0, Kp = 0.4, Ki = 0.08, EwmaA = 0.05;
    public const int LockFramesNeeded = 24, BandRows = 64, MaxFramesPerAdvance = 8;
    private readonly float[] _band = new float[BandRows], _prevBand = new float[BandRows];
    private ulong _rng = 0x9E3779B97F4A7C15UL;
    private double _spare, _phase, _trim, _acc, _pendingSpike, _ms2;
    private bool _hasSpare, _hasPrevBand;
    private int _streak;

    public PtpGenlockRollingShutterTearingGuard(double initialPhaseNs = 3_000_000.0) => _phase = initialPhaseNs;

    public bool GenlockEnabled { get; set; } = true;
    public bool GuardEnabled { get; set; } = true;
    public double CameraFps { get; set; } = 24.0;
    public double DisplayFps { get; set; } = 24.0;
    public double DriftPpm { get; set; } = 12.0;
    public double JitterNs { get; set; } = 120.0;
    public double MeasNoiseNs { get; set; } = 30.0;
    public double ShutterAngle { get; set; } = 180.0;
    public double ReadoutMs { get; set; } = 10.0;
    public double PwmHz { get; set; } = 3840.0;
    public double PwmDuty { get; set; } = 0.5;

    public long Frames { get; private set; }
    public bool Locked { get; private set; }
    public long LockedAtFrame { get; private set; } = -1;
    public double PhaseErrNs { get; private set; }
    public double ResidualNs => _phase;
    public double JitterRmsNs => Math.Sqrt(_ms2);
    public double TrimNs => _trim;
    public double MaxLockedErrNs { get; private set; }
    public double MaxResidualLockedNs { get; private set; }
    public long Corrections { get; private set; }
    public long Tears { get; private set; }
    public long Drops { get; private set; }
    public long Spikes { get; private set; }
    public long LockLosses { get; private set; }
    public bool LastCorrected { get; private set; }
    public bool LastTear { get; private set; }
    public double BandContrast { get; private set; }
    public double Flicker { get; private set; }
    public double EffectivePwmHz { get; private set; }
    public double PwmCycles { get; private set; }
    public double PeriodNs => 1e9 / CameraFps;
    public double ExposureNs => ShutterAngle / 360.0 * PeriodNs;
    public double BusyNs => ExposureNs + ReadoutMs * 1e6;
    /// <summary>스왑 허용 반폭 — 셔터 닫힘 구간(주기 − 노출 − 리드아웃)의 절반(최소 2 µs).</summary>
    public double SafeHalfNs => Math.Max(0.5 * (PeriodNs - BusyNs), 2.0 * CorrectNs);
    public double TargetNs => Math.Min(BusyNs, PeriodNs) + 0.5 * Math.Max(PeriodNs - BusyNs, 0.0);
    /// <summary>스왑 위치(셔터 열림 기준 0~360°).</summary>
    public double SwapPhaseDeg
    {
        get
        {
            var p = (TargetNs + _phase) / PeriodNs;
            p -= Math.Floor(p);
            return p * 360.0;
        }
    }
    public float Band(int row) => _band[row];

    public void InjectSpike(double ns) { _pendingSpike += ns; Spikes++; }

    public void ResetMetrics()
    {
        Corrections = 0; Tears = 0; Drops = 0; Spikes = 0; LockLosses = 0; MaxLockedErrNs = 0; MaxResidualLockedNs = 0;
    }

    /// <summary>실시간 dt(s) 만큼 카메라 프레임을 진행(호출당 최대 8 프레임).</summary>
    public int Advance(double dtSeconds)
    {
        _acc += dtSeconds;
        var p = 1.0 / CameraFps;
        var n = 0;
        while (_acc >= p && n < MaxFramesPerAdvance) { AdvanceFrame(); _acc -= p; n++; }
        if (_acc > p) _acc = 0;
        return n;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void AdvanceFrame()
    {
        var pc = PeriodNs;
        var pd = 1e9 / DisplayFps * (1.0 + DriftPpm * 1e-6);
        _phase += pd - (GenlockEnabled ? _trim : 0.0) - pc + Gauss() * JitterNs + _pendingSpike;
        _pendingSpike = 0;
        if (_phase > 0.5 * pc) { _phase -= pc; Drops++; }
        else if (_phase < -0.5 * pc) { _phase += pc; Drops++; }
        var meas = _phase + Gauss() * MeasNoiseNs;
        PhaseErrNs = meas;
        LastCorrected = false;
        if (GenlockEnabled)
        {
            if (GuardEnabled && Math.Abs(meas) > CorrectNs)
            {
                _phase -= meas;
                Corrections++;
                LastCorrected = true;
            }
            else _phase -= Kp * meas;
            if (Math.Abs(meas) < FreqWindowNs) _trim += Ki * meas;
        }
        _ms2 += EwmaA * (meas * meas - _ms2);
        var a = Math.Abs(meas);
        if (a <= LockNs) _streak++; else _streak = 0;
        if (!Locked && GenlockEnabled && _streak >= LockFramesNeeded) { Locked = true; LockedAtFrame = Frames; }
        else if (Locked && (!GenlockEnabled || a > CorrectNs)) { Locked = false; LockLosses++; }
        if (Locked)
        {
            if (a > MaxLockedErrNs) MaxLockedErrNs = a;
            if (Math.Abs(_phase) > MaxResidualLockedNs) MaxResidualLockedNs = Math.Abs(_phase);
        }
        LastTear = Math.Abs(_phase) > SafeHalfNs;
        if (LastTear) Tears++;
        Banding();
        Frames++;
    }

    /// <summary>롤링셔터 행별 PWM 적분 — 대비(max−min)/평균 · 프레임 간 변화(플리커).</summary>
    private void Banding()
    {
        var texp = ExposureNs * 1e-9;
        var f = Math.Max(PwmHz, 1.0);
        if (GuardEnabled) f = Math.Max(1.0, Math.Round(texp * f)) / texp;
        EffectivePwmHz = f;
        PwmCycles = texp * f;
        var tp = 1.0 / f;
        var d = Math.Clamp(PwmDuty, 0.01, 1.0);
        var psi = (TargetNs + _phase) * 1e-9;
        psi -= Math.Floor(psi / tp) * tp;
        var tr = ReadoutMs * 1e-3;
        double mn = double.MaxValue, mx = 0, sum = 0;
        for (var k = 0; k < BandRows; k++)
        {
            var a0 = psi + k * tr / BandRows;
            var v = OnTime(a0 + texp, tp, d) - OnTime(a0, tp, d);
            _band[k] = (float)v;
            sum += v;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        var mean = sum / BandRows;
        BandContrast = mean > 0 ? (mx - mn) / mean : 0;
        if (_hasPrevBand && mean > 0)
        {
            var fl = 0.0;
            for (var k = 0; k < BandRows; k++) fl += Math.Abs(_band[k] - _prevBand[k]);
            Flicker = fl / BandRows / mean;
        }
        Array.Copy(_band, _prevBand, BandRows);
        _hasPrevBand = true;
    }

    /// <summary>듀티 d · 주기 tp 사각파의 [0, x] 누적 점등 시간.</summary>
    public static double OnTime(double x, double tp, double d)
    {
        var n = Math.Floor(x / tp);
        return n * d * tp + Math.Min(x - n * tp, d * tp);
    }

    private double Gauss()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        double u, v, s;
        do { u = 2.0 * Uniform() - 1.0; v = 2.0 * Uniform() - 1.0; s = u * u + v * v; } while (s >= 1.0 || s == 0.0);
        var m = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spare = v * m; _hasSpare = true;
        return u * m;
    }

    private double Uniform()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 11) * (1.0 / 9007199254740992.0);
    }
}
