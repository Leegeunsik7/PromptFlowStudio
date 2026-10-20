using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.RoboticMoCo;

/// <summary>안전 상태: 정상 · 감속(경고 대역) · 보호 정지(Cat.1 · 자동 재개) · 비상 정지(Cat.0 · 수동 리셋).</summary>
public enum SafetyState : byte { Clear, Warning, ProtectiveStop, EmergencyStop }

/// <summary>정지 원인.</summary>
public enum StopCause : byte { None, Proximity, Contact, KeepOut, PlaneViolation, Watchdog, RelayDiscrepancy, Manual }

/// <summary>추적 인원 역할.</summary>
public enum PersonRole : byte { Actor, Staff }

/// <summary>
/// [PromptFlow Studio v17.01.0] 🛑 하드웨어-인-더-루프(HIL) 안전 구역 &amp; 비상 정지 가드(무할당 · 결정적 시각 입력).
///   ▸ 로봇 링크 캡슐 6(FK 점 7 · 링크 반경) × 인원 수직 캡슐 16(배우/스태프 · 위치/속도) → 선분-선분 최근접 분리 거리.
///   ▸ 속도·분리 감시(ISO/TS 15066 SSM): S_p = v_h(T_r + T_s) + v_r·T_r + B + C + Z_d — v_h = max(접근 속도, 최소 인간 속도) · T_s = 동기 정지 시간 + 브레이크 · B = 로봇 정지 거리.
///     분리 &lt; S_p + 경고 대역 = 속도 오버라이드 비례 감속 · &lt; S_p = 보호 정지(Cat.1 · 1 s 이격 유지 시 자동 재개) · 접촉 = 비상 정지(Cat.0).
///   ▸ 정적 구역: v16.98 SLAM 평면(바닥/벽/천장 · 안정 평면만) 여유 미달 · 침범 불가 박스(배우 마크/크루 구역) 진입 = 비상 정지.
///   ▸ 정지 궤적: 축별 속도/감속 한계 → 공통 정지 시간 T = max|v_i|/d_i → 축별 감속 |v_i|/T(경로 동기 · 동시 정지) · Cat.0 = 브레이크 감속(×1.5).
///   ▸ 하드웨어: 이중 채널 안전 릴레이(A/B 불일치 20 ms = 고장) · 하트비트 워치독(50 ms) · 검출 → 릴레이 차단 지연 µs 실측 · 래치 + 조건부 리셋.
/// </summary>
public sealed class HardwareInTheLoopSafetyEStopGuard
{
    public const int MaxPersons = 16, MaxPlanes = 16, MaxKeepOut = 8, Links = RoboticMoCoInverseKinematicsEngine.Points - 1;
    public const float ReactionS = 0.01f, BrakeS = 0.05f, IntrusionC = 0.1f, UncertaintyZ = 0.05f, WarnBand = 0.5f, PlaneMargin = 0.05f;
    public const float WatchdogS = 0.05f, DiscrepancyS = 0.02f, AutoResumeS = 1f;
    public static readonly float[] LinkRadius = { 0.22f, 0.16f, 0.13f, 0.11f, 0.09f, 0.1f };
    public static readonly string[] StateNames = { "정상", "감속", "보호 정지", "비상 정지" };
    public static readonly string[] CauseNames = { "—", "근접(SSM)", "접촉", "침범 불가 구역", "SLAM 평면 여유", "하트비트 워치독", "릴레이 불일치", "수동 E-Stop" };
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;
    private static readonly float[] EStopDecel = { 7.5f, 7.5f, 7.5f, 15f, 15f, 25f };

    private readonly object _gate = new();
    private readonly Vector3[] _pp = new Vector3[MaxPersons], _pv = new Vector3[MaxPersons];
    private readonly float[] _pr = new float[MaxPersons], _ph = new float[MaxPersons], _pdist = new float[MaxPersons], _psp = new float[MaxPersons];
    private readonly PersonRole[] _prole = new PersonRole[MaxPersons];
    private readonly bool[] _pact = new bool[MaxPersons];
    private readonly Vector3[] _plN = new Vector3[MaxPlanes];
    private readonly float[] _plD = new float[MaxPlanes];
    private readonly PlaneKind[] _plK = new PlaneKind[MaxPlanes];
    private readonly Vector3[] _koMin = new Vector3[MaxKeepOut], _koMax = new Vector3[MaxKeepOut];
    private readonly float[] _decel = new float[RoboticMoCoInverseKinematicsEngine.Joints];
    private readonly Vector3[] _pts = new Vector3[RoboticMoCoInverseKinematicsEngine.Points];
    private int _planes, _keepOut;
    private double _lastHeartbeat = double.NaN, _clearSince = double.NaN, _discrepancySince = double.NaN;
    private bool _relayA, _relayB, _faultB;

    // ── 설정 ──
    public float MinHumanSpeed { get; set; } = 0.3f;

    // ── 상태/계측 ──
    public SafetyState State { get; private set; }
    public StopCause Cause { get; private set; }
    public float Override { get; private set; } = 1f;
    public bool StopActive => State is SafetyState.ProtectiveStop or SafetyState.EmergencyStop;
    public bool RelayA => _relayA;
    public bool RelayB => _relayB;
    public bool HardwareEStop => _relayA || _relayB;
    public bool RelayFault { get; private set; }
    public float MinSeparation { get; private set; } = float.PositiveInfinity;
    public float ProtectiveDistance { get; private set; }
    public float StopTimeS { get; private set; }
    public float StopDistance { get; private set; }
    public int NearestPerson { get; private set; } = -1;
    public long Evaluations { get; private set; }
    public long Warnings { get; private set; }
    public long ProtectiveStops { get; private set; }
    public long EmergencyStops { get; private set; }
    public long AutoResumes { get; private set; }
    public long ResetsDenied { get; private set; }
    public double LastEvalUs { get; private set; }
    public double LastTriggerUs { get; private set; }
    public double MaxTriggerUs { get; private set; }
    public ReadOnlySpan<float> Decel => _decel;
    public int PlaneCount => _planes;
    public int KeepOutCount => _keepOut;
    public bool PersonActive(int i) => _pact[i];
    public Vector3 PersonPosition(int i) => _pp[i];
    public float PersonRadius(int i) => _pr[i];
    public PersonRole Role(int i) => _prole[i];
    public float PersonSeparation(int i) => _pdist[i];
    public float PersonProtective(int i) => _psp[i];
    public Vector3 PlaneNormal(int i) => _plN[i];
    public float PlaneD(int i) => _plD[i];
    public PlaneKind PlaneKindOf(int i) => _plK[i];
    public Vector3 KeepOutMin(int i) => _koMin[i];
    public Vector3 KeepOutMax(int i) => _koMax[i];

    public void SetPerson(int i, Vector3 floorPos, Vector3 velocity, float radius, float height, PersonRole role)
    {
        _pp[i] = floorPos; _pv[i] = velocity; _pr[i] = radius; _ph[i] = height; _prole[i] = role; _pact[i] = true;
    }

    public void RemovePerson(int i) { _pact[i] = false; _pdist[i] = float.PositiveInfinity; }

    public int AddPlane(Vector3 normal, float d, PlaneKind kind)
    {
        lock (_gate)
        {
            if (_planes >= MaxPlanes) return -1;
            _plN[_planes] = Vector3.Normalize(normal); _plD[_planes] = d; _plK[_planes] = kind;
            return _planes++;
        }
    }

    public void ClearPlanes() { lock (_gate) _planes = 0; }

    /// <summary>v16.98 SLAM 안정 평면(바닥/벽/천장) 가져오기 → 가져온 수.</summary>
    public int ImportSlamPlanes(SpatialPlaneDetectionAnchorGuard slam)
    {
        lock (_gate)
        {
            _planes = 0;
            for (var i = 0; i < slam.PlaneCount && _planes < MaxPlanes; i++)
            {
                ref readonly var p = ref slam.Plane(i);
                if (!p.Stable || p.Kind is not (PlaneKind.Floor or PlaneKind.Wall or PlaneKind.Ceiling)) continue;
                _plN[_planes] = p.Normal; _plD[_planes] = p.D; _plK[_planes] = p.Kind; _planes++;
            }
            return _planes;
        }
    }

    public int AddKeepOut(Vector3 min, Vector3 max)
    {
        lock (_gate)
        {
            if (_keepOut >= MaxKeepOut) return -1;
            _koMin[_keepOut] = Vector3.Min(min, max); _koMax[_keepOut] = Vector3.Max(min, max);
            return _keepOut++;
        }
    }

    /// <summary>모션 컨트롤러 하트비트(HIL 루프 생존 신호).</summary>
    public void Heartbeat(double now) => _lastHeartbeat = now;

    /// <summary>릴레이 B 채널 고착(미차단) 고장 주입.</summary>
    public void InjectRelayFault(bool stuck) => _faultB = stuck;

    public void ManualStop(RoboticMoCoInverseKinematicsEngine robot)
    {
        lock (_gate) Trip(SafetyState.EmergencyStop, StopCause.Manual, robot, Stopwatch.GetTimestamp());
    }

    /// <summary>선분 p0-p1 ↔ q0-q1 최근접 거리.</summary>
    public static float SegmentDistance(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-12f && e <= 1e-12f) return r.Length();
        if (a <= 1e-12f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-12f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                var b = Vector3.Dot(d1, d2); var den = a * e - b * b;
                s = den > 1e-12f ? Math.Clamp((b * f - c * e) / den, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        return (p0 + d1 * s - (q0 + d2 * t)).Length();
    }

    /// <summary>1회 평가(무할당): 워치독 · 릴레이 · 정적 구역 · SSM → 상태 전이 · 정지 궤적 · 릴레이.</summary>
    public SafetyState Evaluate(RoboticMoCoInverseKinematicsEngine robot, double now)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            Evaluations++;
            for (var i = 0; i < _pts.Length; i++) _pts[i] = robot.Point(i);
            // ── 하드웨어 무결성 ──
            var hbLost = !double.IsNaN(_lastHeartbeat) && now - _lastHeartbeat > WatchdogS;
            var cmdB = _faultB ? false : _relayB;
            if (_relayA != cmdB) { if (double.IsNaN(_discrepancySince)) _discrepancySince = now; }
            else _discrepancySince = double.NaN;
            if (!double.IsNaN(_discrepancySince) && now - _discrepancySince >= DiscrepancyS) RelayFault = true;
            // ── 정적 구역 ──
            var planeHit = PlaneViolated();
            var koHit = KeepOutViolated();
            // ── SSM ──
            var ts = robot.StopTime(EStopDecel) + BrakeS;
            var vr = robot.TcpSpeed;
            StopTimeS = ts; StopDistance = 0.5f * vr * ts;
            var minSep = float.PositiveInfinity; var worst = float.PositiveInfinity; var nearest = -1; var contact = false; var sp = 0f;
            for (var p = 0; p < MaxPersons; p++)
            {
                if (!_pact[p]) continue;
                Vector3 a0 = _pp[p], a1 = _pp[p] + new Vector3(0f, _ph[p], 0f);
                var dist = float.PositiveInfinity;
                for (var l = 0; l < Links; l++)
                {
                    var d = SegmentDistance(_pts[l], _pts[l + 1], a0, a1) - LinkRadius[l] - _pr[p];
                    if (d < dist) dist = d;
                }
                var toRobot = _pts[3] - _pp[p]; toRobot.Y = 0f;
                var approach = toRobot.LengthSquared() > 1e-8f ? MathF.Max(0f, Vector3.Dot(_pv[p], Vector3.Normalize(toRobot))) : _pv[p].Length();
                var vh = MathF.Max(approach, MinHumanSpeed);
                var spp = vh * (ReactionS + ts) + vr * ReactionS + StopDistance + IntrusionC + UncertaintyZ;
                _pdist[p] = dist; _psp[p] = spp;
                if (dist <= 0f) contact = true;
                if (dist - spp < worst) { worst = dist - spp; sp = spp; nearest = p; }
                if (dist < minSep) minSep = dist;
            }
            MinSeparation = minSep; ProtectiveDistance = sp; NearestPerson = nearest;
            // ── 판정 ──
            if (hbLost) Trip(SafetyState.EmergencyStop, StopCause.Watchdog, robot, t0);
            else if (RelayFault) Trip(SafetyState.EmergencyStop, StopCause.RelayDiscrepancy, robot, t0);
            else if (contact) Trip(SafetyState.EmergencyStop, StopCause.Contact, robot, t0);
            else if (planeHit) Trip(SafetyState.EmergencyStop, StopCause.PlaneViolation, robot, t0);
            else if (koHit) Trip(SafetyState.EmergencyStop, StopCause.KeepOut, robot, t0);
            else if (worst < 0f) Trip(SafetyState.ProtectiveStop, StopCause.Proximity, robot, t0);
            else if (State == SafetyState.ProtectiveStop)
            {
                // 이격 회복 1 s 유지 + 정지 완료 → 자동 재개
                if (double.IsNaN(_clearSince)) _clearSince = now;
                if (now - _clearSince >= AutoResumeS && robot.Mode == RobotMode.Braked)
                {
                    State = SafetyState.Clear; Cause = StopCause.None; _relayA = _relayB = false; _clearSince = double.NaN;
                    robot.Release(); AutoResumes++;
                }
            }
            if (State is SafetyState.Clear or SafetyState.Warning)
            {
                if (worst < WarnBand)
                {
                    if (State == SafetyState.Clear) Warnings++;
                    State = SafetyState.Warning;
                    Override = Math.Clamp(worst / WarnBand, 0.1f, 1f);
                }
                else { State = SafetyState.Clear; Override = 1f; }
            }
            else Override = 0f;
            // Cat.1: 정지 완료 후 STO(릴레이 차단)
            if (State == SafetyState.ProtectiveStop && robot.Mode == RobotMode.Braked) _relayA = _relayB = true;
            if (_faultB) _relayB = false;
            LastEvalUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            return State;
        }
    }

    private void Trip(SafetyState s, StopCause cause, RoboticMoCoInverseKinematicsEngine robot, long t0)
    {
        _clearSince = double.NaN;
        if (State == SafetyState.EmergencyStop || (State == s && s == SafetyState.ProtectiveStop)) return;
        var cat0 = s == SafetyState.EmergencyStop;
        // 경로 동기 정지: 공통 T → 축별 감속
        var mul = cat0 ? 1.5f : 1f;
        var t = 0f;
        for (var k = 0; k < _decel.Length; k++) t = MathF.Max(t, MathF.Abs(robot.Velocity(k)) / (EStopDecel[k] * mul));
        for (var k = 0; k < _decel.Length; k++) _decel[k] = t > 1e-6f ? MathF.Max(MathF.Abs(robot.Velocity(k)) / t, 1e-3f) : EStopDecel[k] * mul;
        State = s; Cause = cause; Override = 0f;
        if (cat0) { _relayA = true; _relayB = !_faultB; EmergencyStops++; }
        else ProtectiveStops++;
        var us = (Stopwatch.GetTimestamp() - t0) * TickUs;
        LastTriggerUs = us;
        if (us > MaxTriggerUs) MaxTriggerUs = us;
    }

    /// <summary>비상 정지 리셋: 정지 완료 · 위반 해소(재평가 기준) · 릴레이 정상 · 하트비트 정상일 때만 → 추종 재개.</summary>
    public bool Reset(RoboticMoCoInverseKinematicsEngine robot, double now)
    {
        lock (_gate)
        {
            var hbOk = double.IsNaN(_lastHeartbeat) || now - _lastHeartbeat <= WatchdogS;
            var personOk = true;
            for (var p = 0; p < MaxPersons; p++) if (_pact[p] && _pdist[p] < _psp[p]) { personOk = false; break; }
            if (State != SafetyState.EmergencyStop || robot.Mode != RobotMode.Braked || _faultB || !hbOk || !personOk || PlaneViolated() || KeepOutViolated())
            {
                if (State == SafetyState.EmergencyStop) ResetsDenied++;
                return false;
            }
            State = SafetyState.Clear; Cause = StopCause.None; _relayA = _relayB = false; RelayFault = false; _discrepancySince = double.NaN; Override = 1f;
            robot.Release();
            return true;
        }
    }

    /// <summary>SLAM 평면 여유: 숄더 이후 점(링크 반경 절반 포함 · 카메라 끝 = 여유만).</summary>
    private bool PlaneViolated()
    {
        for (var k = 0; k < _planes; k++)
            for (var i = 2; i < _pts.Length; i++)
                if (Vector3.Dot(_plN[k], _pts[i]) + _plD[k] < PlaneMargin + (i == _pts.Length - 1 ? 0f : LinkRadius[i - 1] * 0.5f)) return true;
        return false;
    }

    /// <summary>침범 불가 박스: 엘보 · 손목 · 플랜지 · 카메라.</summary>
    private bool KeepOutViolated()
    {
        for (var k = 0; k < _keepOut; k++)
            for (var i = 3; i < _pts.Length; i++)
            {
                var p = _pts[i];
                if (p.X >= _koMin[k].X && p.X <= _koMax[k].X && p.Y >= _koMin[k].Y && p.Y <= _koMax[k].Y && p.Z >= _koMin[k].Z && p.Z <= _koMax[k].Z) return true;
            }
        return false;
    }
}
