using System.Numerics;
namespace PromptFlowStudio.Services.OpticalInertialTracking;

public enum TrackGuardState : byte { Tracking, DeadReckoning, Recovering, Lost }

/// <summary>
/// [PromptFlow Studio v17.45.0] 📐 트래커 마커 오클루전 &amp; UDP 패킷 유실 방어 가드(<see cref="Process"/> 무할당).
/// 광학 측정이 끊기면(마커 가림 · 패킷 드롭) EKF 의 IMU 관성 예측을 그대로 출력하는 데드레커닝으로 전환해 최대 45 프레임(0.75 s)
/// 포즈를 이어 가고, 초과 시 Lost 로 속도를 지수 감쇠해 정지시킨다. 출력 포즈는 가속도 한계(위치 25 m/s² · 회전 40 rad/s²)를
/// 3차 에르미트 소프트 클램프(무릎 L/2 이하 항등, 이상 1 − (1 − s)³ 포화 — C¹ 연속)로 제한해 튐 패킷/보정 점프의 프러스텀 급변을 차단하고,
/// 신호 복구 직후 12 프레임 동안 점프 오프셋을 지수 스무딩(τ 50 ms) × 에르미트 smoothstep 램프로 0 까지 감쇠해 1 프레임 팝핑을 방지한다.
/// </summary>
public sealed class TrackerOcclusionPacketLossJitterGuard
{
    public const int MaxDeadReckonFrames = 45, RecoverFrames = 12;
    public const double MaxAccel = 25.0, MaxAngAccel = 40.0, TauSec = 0.05, LostDecay = 0.85;
    private Vector3 _out, _vel, _eOut, _eVel, _off, _eOff;
    private bool _init;
    private int _recoverLeft;

    public bool Enabled { get; set; } = true;
    public TrackGuardState State { get; private set; } = TrackGuardState.Tracking;
    public Vector3 OutPos => _out;
    public Vector3 OutEuler => _eOut;
    public Vector3 OutVel => _vel;
    public int DeadReckonStreak { get; private set; }
    public int MaxDeadReckonStreak { get; private set; }
    public long DeadReckonFrames { get; private set; }
    public long Recoveries { get; private set; }
    public long LostEvents { get; private set; }
    public long ClampEvents { get; private set; }
    public long Glitches { get; private set; }
    public double LastStepMm { get; private set; }
    public double MaxStepMm { get; private set; }
    public double MaxRecoveryStepMm { get; private set; }
    public double LastBlend { get; private set; } = 1.0;

    public void Reset() { _init = false; State = TrackGuardState.Tracking; DeadReckonStreak = 0; _recoverLeft = 0; ResetMetrics(); }

    public void ResetMetrics()
    {
        MaxDeadReckonStreak = 0; DeadReckonFrames = 0; Recoveries = 0; LostEvents = 0; ClampEvents = 0; Glitches = 0; MaxStepMm = 0; MaxRecoveryStepMm = 0;
    }

    /// <summary>프레임 처리: target = EKF 포즈(측정 없으면 관성 예측), measured = 이번 프레임 광학 갱신 수락, glitch = 게이트 기각.</summary>
    public void Process(double dt, Vector3 target, Vector3 targetEuler, bool measured, bool glitch)
    {
        if (glitch) Glitches++;
        if (!_init || dt <= 0)
        {
            _out = target; _eOut = targetEuler; _vel = Vector3.Zero; _eVel = Vector3.Zero; _init = true; LastStepMm = 0;
            return;
        }
        var prev = _out;
        if (measured)
        {
            if (State is TrackGuardState.DeadReckoning or TrackGuardState.Lost)
            {
                Recoveries++;
                _recoverLeft = RecoverFrames;
            }
            DeadReckonStreak = 0;
            State = _recoverLeft > 0 ? TrackGuardState.Recovering : TrackGuardState.Tracking;
        }
        else
        {
            DeadReckonStreak++;
            DeadReckonFrames++;
            if (DeadReckonStreak > MaxDeadReckonStreak) MaxDeadReckonStreak = DeadReckonStreak;
            if (DeadReckonStreak > MaxDeadReckonFrames)
            {
                if (State != TrackGuardState.Lost) LostEvents++;
                State = TrackGuardState.Lost;
            }
            else State = TrackGuardState.DeadReckoning;
        }

        if (!Enabled)
        {
            _vel = (target - _out) / (float)dt; _out = target; _eOut = targetEuler;
            Step(prev, false);
            return;
        }

        if (State == TrackGuardState.Lost)
        {
            // 관성 신뢰 상실 — 속도 지수 감쇠 정지
            _vel *= (float)LostDecay; _eVel *= (float)LostDecay;
            _out += _vel * (float)dt; _eOut += _eVel * (float)dt;
            Step(prev, false);
            return;
        }

        var te = targetEuler;
        te.X = _eOut.X + (float)RealtimeOpticalInertialCameraTrackingEngine.WrapRad(te.X - _eOut.X);
        if (_recoverLeft == RecoverFrames) { _off = _out - target; _eOff = _eOut - te; }
        if (_recoverLeft > 0)
        {
            // 복구 점프 오프셋 = 지수 감쇠 × (1 − 에르미트 smoothstep) → RecoverFrames 에서 정확히 0
            var k = RecoverFrames - _recoverLeft + 1;
            var s = (double)k / RecoverFrames;
            var w = Math.Exp(-k * dt / TauSec) * (1.0 - s * s * (3.0 - 2.0 * s));
            LastBlend = 1.0 - w;
            var o = _out; var eo = _eOut;
            _out = target + _off * (float)w; _eOut = te + _eOff * (float)w;
            _vel = (_out - o) / (float)dt; _eVel = (_eOut - eo) / (float)dt;
            if (--_recoverLeft == 0 && measured) State = TrackGuardState.Tracking;
        }
        else
        {
            LastBlend = 1.0;
            Follow(ref _out, ref _vel, target, MaxAccel, dt);
            Follow(ref _eOut, ref _eVel, te, MaxAngAccel, dt);
        }
        _eOut.X = (float)RealtimeOpticalInertialCameraTrackingEngine.WrapRad(_eOut.X);
        Step(prev, LastBlend < 1.0);
    }

    private void Step(Vector3 prev, bool recovering)
    {
        LastStepMm = (_out - prev).Length() * 1000.0;
        if (LastStepMm > MaxStepMm) MaxStepMm = LastStepMm;
        if (recovering && LastStepMm > MaxRecoveryStepMm) MaxRecoveryStepMm = LastStepMm;
    }

    /// <summary>가속도 한계 추종: Δv = (target − x)/dt − v 를 에르미트 소프트 클램프 후 적분.</summary>
    private void Follow(ref Vector3 x, ref Vector3 v, Vector3 target, double maxAcc, double dt)
    {
        var dv = (target - x) / (float)dt - v;
        var m = dv.Length();
        var lim = maxAcc * dt;
        var c = HermiteSoftClamp(m, lim);
        if (c < m - 1e-9) { ClampEvents++; dv *= (float)(c / m); }
        v += dv;
        x += v * (float)dt;
    }

    /// <summary>|x| ≤ L/2 항등, 이후 [L/2, 2L] 입력을 1 − (1 − s)³ 로 [L/2, L] 에 포화(무릎 기울기 1 · 끝 기울기 0).</summary>
    public static double HermiteSoftClamp(double x, double limit)
    {
        var k = 0.5 * limit;
        if (x <= k) return x;
        var s = Math.Min((x - k) / (3.0 * k), 1.0);
        var u = 1.0 - s;
        return k + k * (1.0 - u * u * u);
    }
}
