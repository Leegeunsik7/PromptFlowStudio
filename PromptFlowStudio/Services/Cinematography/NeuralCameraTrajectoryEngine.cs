using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Cinematography;

public enum CameraMove : byte { Follow, Orbit, Dolly, Crane, Handheld }
public enum ShotScale : byte { Wide, Full, Medium, MediumCloseUp, CloseUp }
public enum FocusTarget : byte { Subject, Secondary, Manual }

/// <summary>시네마 공용 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
public static class CineSimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>dst += a·s.</summary>
    public static void MulAdd(Span<float> dst, ReadOnlySpan<float> a, float s)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]);
            var vs = new Vector<float>(s);
            for (var k = 0; k < d.Length; k++) d[k] = d[k] + va[k] * vs;
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] + a[i] * s;
    }

    public static void MulAddScalar(Span<float> dst, ReadOnlySpan<float> a, float s)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] + a[i] * s;
    }

    /// <summary>밝기 추출: dst = max(src − t, 0)·g.</summary>
    public static void Threshold(Span<float> dst, ReadOnlySpan<float> src, float t, float g)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var vs = MemoryMarshal.Cast<float, Vector<float>>(src[..m]);
            Vector<float> vt = new(t), vg = new(g);
            for (var k = 0; k < d.Length; k++) d[k] = Vector.Max(vs[k] - vt, Vector<float>.Zero) * vg;
        }
        for (var i = m; i < n; i++) dst[i] = MathF.Max(src[i] - t, 0f) * g;
    }

    public static void ThresholdScalar(Span<float> dst, ReadOnlySpan<float> src, float t, float g)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = MathF.Max(src[i] - t, 0f) * g;
    }
}

/// <summary>6DoF 카메라 포즈 + 렌즈 상태.</summary>
public struct CameraPose
{
    public Vector3 Position;
    public Quaternion Orientation;
    public float FocalMm, FocusDistance, FStop, RollDeg;

    public readonly Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Orientation);
    public readonly Vector3 Up => Vector3.Transform(Vector3.UnitY, Orientation);
    public readonly Vector3 Right => Vector3.Transform(Vector3.UnitX, Orientation);
}

/// <summary>
/// [PromptFlow Studio v16.85.0] 🎥 실시간 신경망 카메라 궤적 &amp; 스무스 패싱 엔진(무할당 · 결정적).
///   ▸ 무브 5종(팔로우 · 오빗 · 돌리 · 크레인 · 핸드헬드) → 피사체 등속 예측(4.8 s · 48 샘플) 기반 희망 경로.
///   ▸ 경로 최적화(CHOMP 계열): 근접 + 2차 차분 평활(가속 최소) 이차 에너지를 가우스-자이델로 풀고, 장애물 구 · 지형 여유고(v16.72 IHeightField) · 가시선(피사체 가림) 제약을 투영 — 시작점/속도 연속.
///   ▸ 추종: 계획 경로 → 임계 감쇠 스프링(ω = 6) · 쿼터니언 감쇠 slerp · 더치 롤 · 리드 타임 조준 · 구도 목표(화면 좌표) 역조준.
///   ▸ 렌즈: 샷 스케일 → 자동 줌(피사체 프레임 점유율) · 박막 렌즈 DoF(과초점 · 근/원 한계 · CoC 0.025 mm) · 랙 포커스(임계 감쇠).
/// </summary>
public sealed class NeuralCameraTrajectoryEngine
{
    public const int Horizon = 48, Sweeps = 12, MaxObstacles = 32, History = 240;
    public const float PlanDt = 0.1f, SensorWidthMm = 21.95f, SensorHeightMm = 18.6f, CocLimitMm = 0.025f, Clearance = 0.6f, ObstacleMargin = 0.5f, ImageAspect = 2.4f;

    private readonly object _gate = new();
    private readonly float[] _ox = new float[MaxObstacles], _oy = new float[MaxObstacles], _oz = new float[MaxObstacles], _or = new float[MaxObstacles];
    private readonly Vector3[] _plan = new Vector3[Horizon], _desired = new Vector3[Horizon], _subjectPath = new Vector3[Horizon], _raw = new Vector3[Horizon];
    private readonly Vector3[] _camHist = new Vector3[History], _subHist = new Vector3[History];
    private int _nObs, _histHead, _histCount;
    private IHeightField? _terrain;
    private Vector3 _vel, _acc, _heading = Vector3.UnitZ, _secondary;
    private float _t, _focusVel, _prevGoal, _logFocal, _jerkSum;
    private long _jerkN;
    private CameraPose _pose, _shaken;

    public NeuralCameraTrajectoryEngine()
    {
        _pose = new CameraPose { Position = new Vector3(0f, 1.6f, -5f), Orientation = Quaternion.Identity, FocalMm = 50f, FocusDistance = 5f, FStop = 2.8f };
        _logFocal = MathF.Log(50f);
        SubjectHeight = 1.75f;
        Reset(Vector3.Zero, Vector3.Zero);
    }

    // ── 리그 파라미터 ──
    public CameraMove Move { get; set; } = CameraMove.Follow;
    public ShotScale Scale { get; set; } = ShotScale.Medium;
    public FocusTarget Focus { get; set; } = FocusTarget.Subject;
    public float Distance { get; set; } = 4.5f;
    public float Height { get; set; } = 1.6f;
    public float AzimuthOffsetDeg { get; set; } = 25f;
    public float OrbitRateDeg { get; set; } = 18f;
    public float DutchDeg { get; set; }
    public float LeadTime { get; set; } = 0.35f;
    public float FStop { get; set; } = 2.8f;
    public float ManualFocus { get; set; } = 5f;
    public float RackSpeed { get; set; } = 4f;
    public float ShakeAmount { get; set; } = 0.6f;
    public bool AutoZoom { get; set; } = true;
    public float ManualFocal { get; set; } = 50f;
    public float SmoothWeight { get; set; } = 6f;
    /// <summary>구도 목표: 피사체 눈의 화면 좌표(0..1 · 좌상 원점). 기본 = 중앙.</summary>
    public Vector2 CompositionTarget { get; set; } = new(0.5f, 0.4f);

    // ── 상태/계측 ──
    public float SubjectHeight { get; set; }
    public Vector3 Subject { get; private set; }
    public Vector3 SubjectVelocity { get; private set; }
    public Vector3 Heading => _heading;
    public Vector3 Secondary => _secondary;
    public CameraPose Pose { get { lock (_gate) return _pose; } }
    public Vector3 Velocity => _vel;
    public float RawCost { get; private set; }
    public float OptimizedCost { get; private set; }
    public float MinClearance { get; private set; }
    public int OcclusionFixes { get; private set; }
    public float RmsJerk => _jerkN > 0 ? MathF.Sqrt(_jerkSum / _jerkN) : 0f;
    public float SubjectDistance { get; private set; }
    public double LastStepMs { get; private set; }
    public int ObstacleCount => _nObs;
    public ReadOnlySpan<Vector3> Plan => _plan;
    public ReadOnlySpan<Vector3> DesiredPath => _desired;
    public ReadOnlySpan<Vector3> SubjectPath => _subjectPath;
    public int HistoryCount => _histCount;
    public Vector3 CameraHistory(int k) => _camHist[(_histHead - 1 - k + 2 * History) % History];
    public Vector3 SubjectHistory(int k) => _subHist[(_histHead - 1 - k + 2 * History) % History];
    public Vector4 Obstacle(int i) => new(_ox[i], _oy[i], _oz[i], _or[i]);

    public static string MoveName(CameraMove m) => m switch
    {
        CameraMove.Orbit => "오빗", CameraMove.Dolly => "돌리", CameraMove.Crane => "크레인", CameraMove.Handheld => "핸드헬드", _ => "팔로우",
    };

    public static string ScaleName(ShotScale s) => s switch
    {
        ShotScale.Wide => "와이드", ShotScale.Full => "풀", ShotScale.MediumCloseUp => "미디엄 클로즈업", ShotScale.CloseUp => "클로즈업", _ => "미디엄",
    };

    /// <summary>샷 스케일 → 피사체 신장/프레임 높이 목표 비.</summary>
    public static float FrameCoverage(ShotScale s) => s switch
    {
        ShotScale.Wide => 0.28f, ShotScale.Full => 0.8f, ShotScale.Medium => 1.7f, ShotScale.MediumCloseUp => 2.8f, _ => 4.6f,
    };

    public void BindTerrain(IHeightField? terrain) { lock (_gate) _terrain = terrain; }

    public void ClearObstacles() { lock (_gate) _nObs = 0; }

    public int AddObstacle(Vector3 c, float r)
    {
        lock (_gate)
        {
            if (_nObs >= MaxObstacles) return -1;
            _ox[_nObs] = c.X; _oy[_nObs] = c.Y; _oz[_nObs] = c.Z; _or[_nObs] = MathF.Max(0.05f, r);
            return _nObs++;
        }
    }

    public void Reset(Vector3 subject, Vector3 subjectVel)
    {
        lock (_gate)
        {
            Subject = subject; SubjectVelocity = subjectVel; _t = 0f;
            UpdateHeading(subjectVel);
            BuildDesired();
            var p0 = Constrain(_desired[0], Subject + Vector3.UnitY * 1.3f);
            for (var k = 0; k < Horizon; k++) _plan[k] = _desired[k];
            _plan[0] = p0;
            _pose.Position = p0; _vel = Vector3.Zero; _acc = Vector3.Zero;
            _pose.Orientation = LookRotation(Vector3.Normalize(AimPoint(p0) - p0), DutchDeg);
            _pose.FocusDistance = MathF.Max(0.3f, Vector3.Distance(p0, Subject + Vector3.UnitY * 1.5f));
            _histHead = 0; _histCount = 0; _jerkSum = 0f; _jerkN = 0; OcclusionFixes = 0;
            _shaken = _pose; _prevGoal = 0f; _focusVel = 0f;
        }
    }

    /// <summary>쿼터니언 LookAt(+Z 전방 · +Y 상방) + 롤.</summary>
    public static Quaternion LookRotation(Vector3 f, float rollDeg)
    {
        var r = Vector3.Cross(Vector3.UnitY, f);
        if (r.LengthSquared() < 1e-8f) r = Vector3.UnitX;
        r = Vector3.Normalize(r);
        var u = Vector3.Cross(f, r);
        var m = new Matrix4x4(r.X, r.Y, r.Z, 0f, u.X, u.Y, u.Z, 0f, f.X, f.Y, f.Z, 0f, 0f, 0f, 0f, 1f);
        var q = Quaternion.CreateFromRotationMatrix(m);
        return rollDeg == 0f ? q : Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rollDeg * MathF.PI / 180f));
    }

    private void UpdateHeading(Vector3 v)
    {
        var h = new Vector3(v.X, 0f, v.Z);
        if (h.LengthSquared() > 0.01f) _heading = Vector3.Normalize(h);
    }

    private float Ground(float x, float z) => _terrain?.HeightAt(x, z) ?? 0f;

    /// <summary>피사체 예측(등속 · 지면 추종) → 무브별 희망 카메라 위치.</summary>
    private void BuildDesired()
    {
        var side = new Vector3(_heading.Z, 0f, -_heading.X);
        var off = AzimuthOffsetDeg * MathF.PI / 180f;
        var back = -(_heading * MathF.Cos(off) + side * MathF.Sin(off));
        for (var k = 0; k < Horizon; k++)
        {
            var tau = k * PlanDt;
            var s = Subject + SubjectVelocity * tau;
            s.Y = Ground(s.X, s.Z);
            _subjectPath[k] = s;
            var up = Vector3.UnitY * Height;
            Vector3 d;
            switch (Move)
            {
                case CameraMove.Orbit:
                    {
                        var a = (OrbitRateDeg * (_t + tau) + AzimuthOffsetDeg) * MathF.PI / 180f;
                        d = s + new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * Distance + up;
                        break;
                    }
                case CameraMove.Dolly: d = s + side * Distance + up; break;
                case CameraMove.Crane: d = s + back * (Distance * 0.8f) + up + Vector3.UnitY * (1.5f + 1.5f * MathF.Sin(0.35f * (_t + tau))); break;
                default: d = s + back * Distance + up; break;
            }
            _desired[k] = d;
        }
    }

    /// <summary>제약 투영: 장애물 구 · 지형 여유고 · 피사체 가시선(구-선분 최근접).</summary>
    private Vector3 Constrain(Vector3 p, Vector3 look)
    {
        for (var o = 0; o < _nObs; o++)
        {
            var c = new Vector3(_ox[o], _oy[o], _oz[o]); var r = _or[o] + ObstacleMargin;
            var d = p - c; var l = d.Length();
            if (l < r) p = c + (l > 1e-4f ? d / l : Vector3.UnitY) * r;
            // 가시선: 선분 p→look 위 최근접점이 장애물 안이면 수직 방향으로 밀어냄
            var seg = look - p; var sl2 = seg.LengthSquared();
            if (sl2 < 1e-6f) continue;
            var u = Math.Clamp(Vector3.Dot(c - p, seg) / sl2, 0f, 1f);
            if (u <= 0.02f || u >= 0.98f) continue;
            var q = p + seg * u; var dq = q - c; var lq = dq.Length();
            var rr = _or[o] + 0.15f;
            if (lq < rr)
            {
                // 밀어냄 방향: 하향 금지(지면 클램프로 소실) · 카메라가 이미 있는 옆쪽으로 수평 편향
                var side = Vector3.Cross(seg, Vector3.UnitY);
                side = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : Vector3.UnitX;
                if (Vector3.Dot(p - c, side) < 0f) side = -side;
                var n = lq > 1e-4f ? dq / lq : side;
                n.Y = MathF.Abs(n.Y);
                n = Vector3.Normalize(n + side * 0.5f);
                p += n * ((rr - lq) / MathF.Max(1f - u, 0.05f));
            }
        }
        var g = Ground(p.X, p.Z) + Clearance;
        if (p.Y < g) p.Y = g;
        return p;
    }

    private float Cost(ReadOnlySpan<Vector3> path)
    {
        float c = 0f;
        for (var k = 0; k < Horizon; k++)
        {
            c += Vector3.DistanceSquared(path[k], _desired[k]);
            if (k >= 1 && k + 1 < Horizon) c += SmoothWeight * (path[k - 1] - 2f * path[k] + path[k + 1]).LengthSquared();
            for (var o = 0; o < _nObs; o++)
            {
                var pen = _or[o] + ObstacleMargin - Vector3.Distance(path[k], new Vector3(_ox[o], _oy[o], _oz[o]));
                if (pen > 0f) c += 100f * pen * pen;
            }
            var gp = Ground(path[k].X, path[k].Z) + Clearance - path[k].Y;
            if (gp > 0f) c += 100f * gp * gp;
        }
        return c;
    }

    /// <summary>경로 최적화: 이차 에너지(근접 + 2차 차분) 가우스-자이델 + 제약 투영 · p₀ 고정 · 고스트 p₋₁ = p₀ − v·Δt(속도 연속).</summary>
    private void Optimize(float dt)
    {
        for (var k = 0; k < Horizon; k++) _raw[k] = _desired[k];
        _raw[0] = _pose.Position;
        RawCost = Cost(_raw);
        // 웜 스타트: 이전 계획을 dt 만큼 진행(샘플 간 보간)한 값과 희망 경로 평균
        var adv = Math.Clamp(dt / PlanDt, 0f, 1f);
        for (var k = 1; k < Horizon; k++) _plan[k] = 0.5f * (Vector3.Lerp(_plan[k], _plan[Math.Min(k + 1, Horizon - 1)], adv) + _desired[k]);
        _plan[0] = _pose.Position;
        var ghost = _pose.Position - _vel * PlanDt;
        var ws = SmoothWeight;
        for (var it = 0; it < Sweeps; it++)
        {
            for (var k = 1; k < Horizon; k++)
            {
                var num = _desired[k]; var den = 1f;
                var km1 = _plan[k - 1]; var km2 = k >= 2 ? _plan[k - 2] : ghost;
                num += ws * (2f * km1 - km2); den += ws;
                if (k + 1 < Horizon) { num += 2f * ws * (km1 + _plan[k + 1]); den += 4f * ws; }
                if (k + 2 < Horizon) { num += ws * (2f * _plan[k + 1] - _plan[k + 2]); den += ws; }
                _plan[k] = Constrain(num / den, _subjectPath[k] + Vector3.UnitY * 1.3f);
            }
        }
        OptimizedCost = Cost(_plan);
        var mc = float.MaxValue;
        for (var k = 0; k < Horizon; k++)
            for (var o = 0; o < _nObs; o++)
                mc = MathF.Min(mc, Vector3.Distance(_plan[k], new Vector3(_ox[o], _oy[o], _oz[o])) - _or[o]);
        MinClearance = _nObs > 0 ? mc : 99f;
    }

    private Vector3 AimPoint(Vector3 cam)
    {
        var aim = Subject + SubjectVelocity * LeadTime + Vector3.UnitY * (SubjectHeight * 0.92f);
        // 구도 역조준: 눈이 화면 CompositionTarget 에 오도록 조준점을 카메라 기저로 이동
        var f = Vector3.Normalize(aim - cam);
        var r = Vector3.Cross(Vector3.UnitY, f); r = r.LengthSquared() > 1e-8f ? Vector3.Normalize(r) : Vector3.UnitX;
        var u = Vector3.Cross(f, r);
        var dist = Vector3.Distance(aim, cam);
        var tv = SensorHeightMm * 0.5f / _pose.FocalMm; var th = tv * ImageAspect;
        var ct = CompositionTarget;
        return aim - r * ((ct.X - 0.5f) * 2f * th * dist) + u * ((ct.Y - 0.5f) * 2f * tv * dist);
    }

    /// <summary>1틱(무할당): 피사체 갱신 → 희망 경로 → 최적화 → 스프링 추종 → 조준/롤 → 줌 → 랙 포커스.</summary>
    public void Step(float dt, Vector3 subject, Vector3 subjectVel, Vector3 secondary)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _t += dt;
            Subject = subject; SubjectVelocity = subjectVel; _secondary = secondary;
            UpdateHeading(subjectVel);
            BuildDesired();
            Optimize(dt);

            // 계획 경로 dt 지점 → 임계 감쇠 스프링(ω = 6)
            var target = _plan[1];
            const float w = 6f;
            var a = w * w * (target - _pose.Position) - 2f * w * _vel;
            var jerk = (a - _acc) / MathF.Max(dt, 1e-4f);
            _jerkSum += jerk.LengthSquared(); _jerkN++;
            _acc = a;
            _vel += a * dt;
            var pos = Constrain(_pose.Position + _vel * dt, Subject + Vector3.UnitY * 1.3f);
            if (Vector3.DistanceSquared(pos, _pose.Position + _vel * dt) > 1e-8f) OcclusionFixes++;
            _pose.Position = pos;

            var shake = Vector3.Zero; var shakeRoll = 0f;
            if (Move == CameraMove.Handheld)
            {
                var s = ShakeAmount;
                shake = new Vector3(MathF.Sin(1.7f * _t) + 0.5f * MathF.Sin(4.3f * _t + 1f), MathF.Sin(2.3f * _t + 2f) + 0.4f * MathF.Sin(5.9f * _t), MathF.Sin(1.1f * _t + 0.5f)) * (0.015f * s);
                shakeRoll = 1.2f * s * MathF.Sin(0.9f * _t + 0.3f);
            }
            var eye = _pose.Position + shake;
            var fwd = Vector3.Normalize(AimPoint(eye) - eye);
            var qt = LookRotation(fwd, DutchDeg + shakeRoll);
            if (Quaternion.Dot(qt, _pose.Orientation) < 0f) qt = -qt;
            _pose.Orientation = Quaternion.Normalize(Quaternion.Slerp(_pose.Orientation, qt, 1f - MathF.Exp(-dt * 10f)));
            _pose.RollDeg = DutchDeg + shakeRoll;

            // 자동 줌: 초점거리 = 점유율·센서 높이·거리 / 피사체 신장(로그 완화)
            var subjDist = Vector3.Distance(eye, Subject + Vector3.UnitY * (SubjectHeight * 0.5f));
            SubjectDistance = subjDist;
            var fTarget = AutoZoom ? FrameCoverage(Scale) * SensorHeightMm * subjDist / SubjectHeight : ManualFocal;
            fTarget = Math.Clamp(fTarget, 18f, 300f);
            _logFocal += (MathF.Log(fTarget) - _logFocal) * (1f - MathF.Exp(-dt * 3f));
            _pose.FocalMm = MathF.Exp(_logFocal);
            _pose.FStop = Math.Clamp(FStop, 1.2f, 22f);

            // 랙 포커스: 대상 z 깊이로 임계 감쇠
            var f2 = _pose.Forward;
            var focusPt = Focus switch
            {
                FocusTarget.Secondary => _secondary,
                _ => Subject + Vector3.UnitY * (SubjectHeight * 0.92f),
            };
            var goal = Focus == FocusTarget.Manual ? ManualFocus : MathF.Max(0.3f, Vector3.Dot(focusPt - eye, f2));
            var wf = RackSpeed;
            // 목표 변화율 피드포워드(이동 표적 램프 지연 제거)
            var goalVel = _prevGoal > 0f ? Math.Clamp((goal - _prevGoal) / MathF.Max(dt, 1e-4f), -20f, 20f) : 0f;
            _prevGoal = goal;
            var af = wf * wf * (goal - _pose.FocusDistance) + 2f * wf * (goalVel - _focusVel);
            _focusVel += af * dt;
            _pose.FocusDistance = MathF.Max(0.3f, _pose.FocusDistance + _focusVel * dt);
            var final = _pose; final.Position = eye;

            _camHist[_histHead] = eye; _subHist[_histHead] = Subject;
            _histHead = (_histHead + 1) % History; if (_histCount < History) _histCount++;
            _shaken = final;
            LastStepMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>최종 렌더 포즈(핸드헬드 흔들림 포함).</summary>
    public CameraPose RenderPose { get { lock (_gate) return _shaken; } }

    // ══════════════════════ 박막 렌즈 광학 ══════════════════════

    public static float VerticalFovDeg(float focalMm) => 2f * MathF.Atan(SensorHeightMm * 0.5f / focalMm) * 180f / MathF.PI;
    public static float HorizontalFovDeg(float focalMm) => 2f * MathF.Atan(MathF.Tan(VerticalFovDeg(focalMm) * MathF.PI / 360f) * ImageAspect) * 180f / MathF.PI;

    /// <summary>과초점 거리(m): H = f²/(N·c) + f.</summary>
    public static float Hyperfocal(float focalMm, float fStop) => (focalMm * focalMm / (fStop * CocLimitMm) + focalMm) / 1000f;

    /// <summary>센서 착란원 직경(mm): A·f·|d − s| / (d·(s − f)) · A = f/N.</summary>
    public static float CocMm(float depthM, float focusM, float focalMm, float fStop)
    {
        var f = focalMm / 1000f; var s = MathF.Max(focusM, f * 1.01f); var d = MathF.Max(depthM, f * 1.01f);
        return (f / fStop) * f * MathF.Abs(d - s) / (d * (s - f)) * 1000f;
    }

    /// <summary>피사계 심도 근/원 한계(m · 원 한계 무한 = +∞).</summary>
    public static (float Near, float Far) DepthOfField(float focusM, float focalMm, float fStop)
    {
        var h = Hyperfocal(focalMm, fStop); var f = focalMm / 1000f;
        var near = focusM * (h - f) / (h + focusM - 2f * f);
        var far = focusM < h ? focusM * (h - f) / (h - focusM) : float.PositiveInfinity;
        return (near, far);
    }
}
