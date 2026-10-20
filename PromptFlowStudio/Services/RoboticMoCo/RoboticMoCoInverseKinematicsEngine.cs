using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Cinematography;

namespace PromptFlowStudio.Services.RoboticMoCo;

/// <summary>IK 결과: 정상 · 작업 영역 밖(경계로 클램프) · 관절 한계(이전 자세 유지) · 손목 특이점(J4 유지).</summary>
public enum IkStatus : byte { Ok, Clamped, LimitViolation, WristSingular }

/// <summary>로봇 동작 모드: 추종 · 정지 궤적 실행 중 · 브레이크 체결.</summary>
public enum RobotMode : byte { Tracking, Stopping, Braked }

/// <summary>[PromptFlow Studio v17.01.0] MoCo SIMD 커널(Vector&lt;float&gt; · 스칼라 꼬리 · 스칼라 경로와 비트 동일).</summary>
public static class MocoSimd
{
    /// <summary>
    /// 저크 제한 3단 캐스케이드(위치 → 속도 → 가속 → 저크, 단계별 포화): v_ref = clamp(v_ff + clamp(k·e, ±√(0.7·amax·|e|)), ±vmax)(목표 속도 피드포워드 + 제동 가능 속도) ·
    /// a_ref = clamp(a_ff + 3k·(v_ref − v), ±amax) · j = clamp(8k·(a_ref − a), ±jmax) → a += j·dt · v = clamp(v + a·dt) · q += v·dt — 포화 구간 포함 오버슈트 없음.
    /// </summary>
    public static void JerkStep(Span<float> q, Span<float> v, Span<float> a, Span<float> jerk, ReadOnlySpan<float> target, ReadOnlySpan<float> vff, ReadOnlySpan<float> aff,
                                ReadOnlySpan<float> vmax, ReadOnlySpan<float> amax, ReadOnlySpan<float> jmax, float k, float dt)
    {
        var n = q.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            Vector<float> vk = new(k), vk3 = new(3f * k), vk8 = new(8f * k), vdt = new(dt), v07 = new(0.7f);
            var vq = MemoryMarshal.Cast<float, Vector<float>>(q[..m]); var vv = MemoryMarshal.Cast<float, Vector<float>>(v[..m]);
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vj = MemoryMarshal.Cast<float, Vector<float>>(jerk[..m]);
            var vt = MemoryMarshal.Cast<float, Vector<float>>(target[..m]); var vf = MemoryMarshal.Cast<float, Vector<float>>(vff[..m]); var af = MemoryMarshal.Cast<float, Vector<float>>(aff[..m]);
            var lv = MemoryMarshal.Cast<float, Vector<float>>(vmax[..m]); var la = MemoryMarshal.Cast<float, Vector<float>>(amax[..m]); var lj = MemoryMarshal.Cast<float, Vector<float>>(jmax[..m]);
            for (var c = 0; c < vq.Length; c++)
            {
                var e = vt[c] - vq[c];
                var vb = Vector.Min(Vector.SquareRoot(v07 * la[c] * Vector.Abs(e)), lv[c]);
                var vr = Vector.Min(Vector.Max(vf[c] + Vector.Min(Vector.Max(vk * e, -vb), vb), -lv[c]), lv[c]);
                var ar = Vector.Min(Vector.Max(af[c] + vk3 * (vr - vv[c]), -la[c]), la[c]);
                var j = Vector.Min(Vector.Max(vk8 * (ar - va[c]), -lj[c]), lj[c]);
                var na = Vector.Min(Vector.Max(va[c] + j * vdt, -la[c]), la[c]);
                var nv = Vector.Min(Vector.Max(vv[c] + na * vdt, -lv[c]), lv[c]);
                vq[c] = vq[c] + nv * vdt; vv[c] = nv; va[c] = na; vj[c] = j;
            }
            i = m;
        }
        for (; i < n; i++) JerkLane(q, v, a, jerk, target, vff, aff, vmax, amax, jmax, k, dt, i);
    }

    public static void JerkStepScalar(Span<float> q, Span<float> v, Span<float> a, Span<float> jerk, ReadOnlySpan<float> target, ReadOnlySpan<float> vff, ReadOnlySpan<float> aff,
                                      ReadOnlySpan<float> vmax, ReadOnlySpan<float> amax, ReadOnlySpan<float> jmax, float k, float dt)
    {
        for (var i = 0; i < q.Length; i++) JerkLane(q, v, a, jerk, target, vff, aff, vmax, amax, jmax, k, dt, i);
    }

    private static void JerkLane(Span<float> q, Span<float> v, Span<float> a, Span<float> jerk, ReadOnlySpan<float> target, ReadOnlySpan<float> vff, ReadOnlySpan<float> aff,
                                 ReadOnlySpan<float> vmax, ReadOnlySpan<float> amax, ReadOnlySpan<float> jmax, float k, float dt, int i)
    {
        var e = target[i] - q[i];
        var vb = MathF.Min(MathF.Sqrt(0.7f * amax[i] * MathF.Abs(e)), vmax[i]);
        var vr = MathF.Min(MathF.Max(vff[i] + MathF.Min(MathF.Max(k * e, -vb), vb), -vmax[i]), vmax[i]);
        var ar = MathF.Min(MathF.Max(aff[i] + 3f * k * (vr - v[i]), -amax[i]), amax[i]);
        var j = MathF.Min(MathF.Max(8f * k * (ar - a[i]), -jmax[i]), jmax[i]);
        var na = MathF.Min(MathF.Max(a[i] + j * dt, -amax[i]), amax[i]);
        var nv = MathF.Min(MathF.Max(v[i] + na * dt, -vmax[i]), vmax[i]);
        q[i] = q[i] + nv * dt; v[i] = nv; a[i] = na; jerk[i] = j;
    }

    /// <summary>전진 차분 dst[i] = (src[i+1] − src[i])·inv (dst 길이 = src − 1).</summary>
    public static void Differentiate(Span<float> dst, ReadOnlySpan<float> src, float inv)
    {
        var n = dst.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            Vector<float> vi = new(inv);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            for (var c = 0; c < d.Length; c++)
            {
                var a = new Vector<float>(src.Slice(c * Vector<float>.Count, Vector<float>.Count));
                var b = new Vector<float>(src.Slice(c * Vector<float>.Count + 1, Vector<float>.Count));
                d[c] = (b - a) * vi;
            }
            i = m;
        }
        for (; i < n; i++) dst[i] = (src[i + 1] - src[i]) * inv;
    }

    public static void DifferentiateScalar(Span<float> dst, ReadOnlySpan<float> src, float inv)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = (src[i + 1] - src[i]) * inv;
    }

    /// <summary>|x| 최댓값.</summary>
    public static float MaxAbs(ReadOnlySpan<float> x)
    {
        var n = x.Length; var i = 0; var r = 0f;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            var acc = Vector<float>.Zero;
            var v = MemoryMarshal.Cast<float, Vector<float>>(x[..m]);
            for (var c = 0; c < v.Length; c++) acc = Vector.Max(acc, Vector.Abs(v[c]));
            for (var k = 0; k < Vector<float>.Count; k++) r = MathF.Max(r, acc[k]);
            i = m;
        }
        for (; i < n; i++) r = MathF.Max(r, MathF.Abs(x[i]));
        return r;
    }
}

/// <summary>
/// [PromptFlow Studio v17.01.0] 🤖 실시간 6축 로보틱 모션 컨트롤(MoCo) 역운동학 엔진(무할당 · 결정적).
///   ▸ 기구: 산업용 6축 구면 손목(KR-급 · Y-up) — J1 요(베이스) · J2/J3 피치(상완 0.68 m · 전완 0.67 m · 숄더 오프셋 0.26 m · 높이 0.675 m) · J4 롤 · J5 피치 · J6 롤(플랜지 0.115 m + 카메라 노달 오프셋).
///   ▸ 해석 IK: v16.85 CameraPose(카메라 +Z 전방 · 언더슬렁 마운트) → 플랜지 자세 → 손목 중심 → J1(전/후 2해) · J3(팔꿈치 상/하 2해 · 코사인 법칙) · J2(복소 회전) · J4/J5/J6(X-Z-X 오일러 · 플립 2해) = 8해 →
///     관절 한계 통과 해 중 이전 자세 가중 최근접 선택(J4/J6 2π 언랩 연속). 작업 영역 밖 = 팔 신장 경계로 클램프 · 손목 특이점(|sin J5| ≈ 0) = J4 유지.
///   ▸ 모션 프로파일: 관절 6축 SIMD 저크 제한 캐스케이드(속도/가속/저크 축별 한계 · 속도 오버라이드) · 정지 궤적(동기 감속 · 안전 가드 구동) → 브레이크.
///   ▸ 부하: FK 링크 질량 중심 → J2/J3/J5 중력 토크 + 관성 토크 → 축별 부하율 · 패스 사전 검증(최대 512 샘플 IK → SIMD 차분 속도/가속/저크 피크).
/// </summary>
public sealed class RoboticMoCoInverseKinematicsEngine
{
    public const int Joints = 6, Points = 7, MaxPass = 512;
    public const float D1 = 0.675f, A1 = 0.26f, A2 = 0.68f, D4 = 0.67f, D6 = 0.115f, Gravity = 9.81f;
    public const float MassUpper = 25f, MassFore = 18f, MassPayload = 12f;
    public static readonly int Pad = (Joints + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
    public static readonly string[] JointNames = { "J1 베이스", "J2 숄더", "J3 엘보", "J4 손목 롤", "J5 손목 피치", "J6 플랜지" };
    private const float Deg = MathF.PI / 180f;
    public static readonly float[] LimitLo = { -170f * Deg, -90f * Deg, -120f * Deg, -185f * Deg, -125f * Deg, -350f * Deg };
    public static readonly float[] LimitHi = { 170f * Deg, 135f * Deg, 150f * Deg, 185f * Deg, 125f * Deg, 350f * Deg };
    public static readonly float[] TorqueMax = { 600f, 900f, 450f, 140f, 140f, 70f };
    private static readonly float[] Inertia = { 30f, 40f, 20f, 3f, 3f, 1f };
    private static readonly float[] Weight = { 2f, 2f, 1.5f, 1f, 1f, 0.6f };
    /// <summary>
    /// 언더슬렁 카메라 마운트(플랜지 로컬 → 카메라 로컬): 카메라 전방 = 플랜지 +Y · 카메라 위 = 플랜지 −X · 카메라 오른쪽 = 플랜지 −Z.
    /// 수평 촬영 시 J5 ≈ −90°(손목 특이점 J5 = 0에서 멀리) — 카메라는 손목 아래 플랜지 축 방향으로 매달림.
    /// </summary>
    public static readonly Quaternion FlangeToCamera = Quaternion.Conjugate(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(0, 0, -1, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1))));
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly float[] _q = new float[Pad], _v = new float[Pad], _a = new float[Pad], _j = new float[Pad], _target = new float[Pad], _prevTarget = new float[Pad], _vff = new float[Pad], _aff = new float[Pad];
    private readonly float[] _vmax = new float[Pad], _amax = new float[Pad], _jmax = new float[Pad], _vmaxEff = new float[Pad], _amaxEff = new float[Pad];
    private readonly float[] _torque = new float[Joints], _seed = new float[Joints], _tmp = new float[Joints];
    private readonly Vector3[] _pts = new Vector3[Points];
    private Vector3 _goal;
    private bool _ffValid;
    private readonly float[] _passQ = new float[Joints * MaxPass], _passD1 = new float[MaxPass], _passD2 = new float[MaxPass], _passD3 = new float[MaxPass];
    private readonly float[] _peakV = new float[Joints], _peakA = new float[Joints], _peakJ = new float[Joints];

    public RoboticMoCoInverseKinematicsEngine()
    {
        float[] vd = { 156f, 156f, 156f, 330f, 330f, 615f }, ad = { 3f, 3f, 3f, 6f, 6f, 10f }, jd = { 20f, 20f, 20f, 40f, 40f, 60f };
        for (var i = 0; i < Pad; i++)
        {
            var k = Math.Min(i, Joints - 1);
            _vmax[i] = vd[k] * Deg; _amax[i] = ad[k]; _jmax[i] = jd[k];
            if (i >= Joints) { _vmax[i] = _amax[i] = _jmax[i] = 0f; }
        }
        Forward(_q, Base, ToolLength, _pts, out _);
        Tcp = _pts[Points - 1];
    }

    // ── 설정 ──
    public Vector3 Base { get; set; }
    public float ToolLength { get; set; } = 0.2f;
    public float Gain { get; set; } = 3f;
    public float SpeedOverride { get; set; } = 1f;

    // ── 상태/계측 ──
    public RobotMode Mode { get; private set; }
    public IkStatus LastStatus { get; private set; }
    public long Solves { get; private set; }
    public long ClampedSolves { get; private set; }
    public long LimitRejects { get; private set; }
    public long Singular { get; private set; }
    public double LastSolveUs { get; private set; }
    public double LastStepUs { get; private set; }
    public float TcpSpeed { get; private set; }
    public float TrackingErrorMm { get; private set; }
    public Vector3 Tcp { get; private set; }
    public float Angle(int j) => _q[j];
    public float Velocity(int j) => _v[j];
    public float Acceleration(int j) => _a[j];
    public float Jerk(int j) => _j[j];
    public float TargetAngle(int j) => _target[j];
    public float VelocityLimit(int j) => _vmax[j];
    public float AccelLimit(int j) => _amax[j];
    public float JerkLimit(int j) => _jmax[j];
    public float Torque(int j) => _torque[j];
    public float Load(int j) => _torque[j] / TorqueMax[j];
    public Vector3 Point(int i) => _pts[i];
    public ReadOnlySpan<float> AnglesSpan => _q.AsSpan(0, Joints);
    public ReadOnlySpan<float> VelocitySpan => _v.AsSpan(0, Joints);

    // ── 패스 사전 검증 ──
    public int PassSamples { get; private set; }
    public int PassUnreachable { get; private set; }
    public int PassLimitHits { get; private set; }
    public float PassPeakVelocity(int j) => _peakV[j];
    public float PassPeakAccel(int j) => _peakA[j];
    public float PassPeakJerk(int j) => _peakJ[j];
    public bool PassFeasible
    {
        get
        {
            if (PassUnreachable > 0 || PassLimitHits > 0) return false;
            for (var j = 0; j < Joints; j++) if (_peakV[j] > _vmax[j] || _peakA[j] > _amax[j]) return false;
            return true;
        }
    }

    public static float Wrap(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau; else if (a <= -MathF.PI) a += MathF.Tau;
        return a;
    }

    /// <summary>카메라 위치 → 목표(Up = +Y) 바라보는 CameraPose(Forward = Orientation·Z).</summary>
    public static CameraPose LookAt(Vector3 eye, Vector3 at, float rollDeg = 0f)
    {
        var z = Vector3.Normalize(at - eye);
        var x = Vector3.Cross(Vector3.UnitY, z);
        x = x.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(x);
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        if (rollDeg != 0f) q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rollDeg * Deg));
        return new CameraPose { Position = eye, Orientation = q, FocalMm = 35f, FocusDistance = Vector3.Distance(eye, at), FStop = 2.8f };
    }

    /// <summary>순기구학: 점 7개(베이스 · 숄더 상단 · 숄더 · 엘보 · 손목 중심 · 플랜지 · 카메라) + 카메라 자세.</summary>
    public static void Forward(ReadOnlySpan<float> q, Vector3 basePos, float tool, Span<Vector3> pts, out Quaternion camera)
    {
        float j1 = q[0], j2 = q[1], j3 = q[2];
        var rhat = new Vector3(MathF.Cos(j1), 0f, -MathF.Sin(j1));
        var top = basePos + new Vector3(0f, D1, 0f);
        var s = top + rhat * A1;
        var e = s + (rhat * MathF.Sin(j2) + Vector3.UnitY * MathF.Cos(j2)) * A2;
        var phi = j2 + j3;
        var w = e + (rhat * MathF.Cos(phi) - Vector3.UnitY * MathF.Sin(phi)) * D4;
        var q06 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, j1) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -phi)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, q[3]) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, q[4]) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, q[5]);
        var xf = Vector3.Transform(Vector3.UnitX, q06);
        var f = w + xf * D6;
        pts[0] = basePos; pts[1] = top; pts[2] = s; pts[3] = e; pts[4] = w; pts[5] = f; pts[6] = f + xf * tool;
        camera = Quaternion.Normalize(q06 * Quaternion.Conjugate(FlangeToCamera));
    }

    /// <summary>해석 IK(8해 → 한계 통과 최근접). 실패 시 result = seed.</summary>
    public IkStatus Solve(in CameraPose target, ReadOnlySpan<float> seed, Span<float> result)
    {
        var r06 = Quaternion.Normalize(target.Orientation * FlangeToCamera);
        var xf = Vector3.Transform(Vector3.UnitX, r06);
        var w = target.Position - Base - xf * (D6 + ToolLength);
        var j1a = w.X * w.X + w.Z * w.Z < 1e-8f ? seed[0] : MathF.Atan2(-w.Z, w.X);
        Span<float> c = stackalloc float[Joints];
        var bestCost = float.PositiveInfinity; var clamped = false; var singular = false; var any = false;
        for (var sh = 0; sh < 2; sh++)
        {
            var j1 = sh == 0 ? j1a : Wrap(j1a + MathF.PI);
            float cr = MathF.Cos(j1), sr = MathF.Sin(j1);
            var r = w.X * cr - w.Z * sr - A1; var h = w.Y - D1;
            var s3 = (A2 * A2 + D4 * D4 - (r * r + h * h)) / (2f * A2 * D4);
            var cl = false;
            if (s3 < -1f) { s3 = -1f; cl = true; } else if (s3 > 1f) { s3 = 1f; cl = true; }
            var as3 = MathF.Asin(s3);
            for (var el = 0; el < 2; el++)
            {
                var j3 = el == 0 ? as3 : Wrap(MathF.PI - as3);
                if (cl && el == 1) break;
                var aa = A2 - D4 * MathF.Sin(j3); var bb = D4 * MathF.Cos(j3);
                var j2 = Wrap(MathF.Atan2(r, h) - MathF.Atan2(bb, aa));
                var q03 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, j1) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -(j2 + j3));
                var q36 = Quaternion.Normalize(Quaternion.Conjugate(q03) * r06);
                var ex = Vector3.Transform(Vector3.UnitX, q36);
                var ey = Vector3.Transform(Vector3.UnitY, q36);
                var ez = Vector3.Transform(Vector3.UnitZ, q36);
                var c5 = Math.Clamp(ex.X, -1f, 1f);
                var s5 = MathF.Sqrt(MathF.Max(0f, 1f - c5 * c5));
                var flips = s5 < 1e-4f ? 1 : 2;
                for (var fl = 0; fl < flips; fl++)
                {
                    c[0] = j1; c[1] = j2; c[2] = j3;
                    if (s5 < 1e-4f)
                    {
                        c[4] = MathF.Acos(c5); c[3] = seed[3];
                        c[5] = MathF.Atan2(ey.Z, ey.Y) - c[3];
                    }
                    else
                    {
                        var sg = fl == 0 ? 1f : -1f;
                        c[4] = sg * MathF.Acos(c5);
                        c[3] = MathF.Atan2(sg * ex.Z, sg * ex.Y);
                        c[5] = MathF.Atan2(sg * ez.X, -sg * ey.X);
                    }
                    c[3] += MathF.Tau * MathF.Round((seed[3] - c[3]) / MathF.Tau);
                    c[5] += MathF.Tau * MathF.Round((seed[5] - c[5]) / MathF.Tau);
                    c[0] += MathF.Tau * MathF.Round((seed[0] - c[0]) / MathF.Tau);
                    var ok = true; var cost = 0f;
                    for (var k = 0; k < Joints; k++)
                    {
                        if (c[k] < LimitLo[k] || c[k] > LimitHi[k]) { ok = false; break; }
                        var d = c[k] - seed[k]; cost += Weight[k] * d * d;
                    }
                    if (!ok || cost >= bestCost) continue;
                    bestCost = cost; any = true; clamped = cl; singular = s5 < 1e-4f;
                    c.CopyTo(result);
                }
            }
        }
        if (!any) { seed[..Joints].CopyTo(result); return IkStatus.LimitViolation; }
        return clamped ? IkStatus.Clamped : singular ? IkStatus.WristSingular : IkStatus.Ok;
    }

    /// <summary>v16.85 카메라 6DoF 목표 → IK(연속 시드 = 현재 목표) → 목표 관절(추종 중일 때만).</summary>
    public IkStatus SetTarget(in CameraPose pose)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            _target.AsSpan(0, Joints).CopyTo(_seed);
            var st = Solve(pose, _seed, _tmp);
            if (Mode == RobotMode.Tracking)
                for (var k = 0; k < Joints; k++)
                {
                    // 목표 점프(틱당 최대 속도 2배 초과) = 계단 입력 → 피드포워드 생략
                    if (MathF.Abs(_tmp[k] - _target[k]) > _vmax[k] * 0.02f) { _prevTarget[k] = _tmp[k]; _ffValid = false; }
                    _target[k] = _tmp[k];
                }
            Solves++;
            if (st == IkStatus.Clamped) ClampedSolves++; else if (st == IkStatus.LimitViolation) LimitRejects++; else if (st == IkStatus.WristSingular) Singular++;
            LastStatus = st;
            LastSolveUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            _goal = pose.Position;
            return st;
        }
    }

    /// <summary>관절 목표 직접 지정(티칭 · 홈).</summary>
    public void SetJointTarget(ReadOnlySpan<float> q)
    {
        lock (_gate) for (var k = 0; k < Joints; k++) _target[k] = _prevTarget[k] = Math.Clamp(q[k], LimitLo[k], LimitHi[k]);
    }

    /// <summary>1틱 저크 제한 추종(무할당 · 오버라이드 = 속도/가속 축척).</summary>
    public void Step(float dt)
    {
        lock (_gate)
        {
            if (Mode != RobotMode.Tracking) return;
            var t0 = Stopwatch.GetTimestamp();
            var ov = Math.Clamp(SpeedOverride, 0f, 1f);
            var inv = 1f / dt;
            for (var i = 0; i < Pad; i++)
            {
                _vmaxEff[i] = _vmax[i] * ov; _amaxEff[i] = _amax[i] * MathF.Max(ov, 0.05f);
                var vf = Math.Clamp((_target[i] - _prevTarget[i]) * inv, -_vmaxEff[i], _vmaxEff[i]);
                _aff[i] = _ffValid ? Math.Clamp((vf - _vff[i]) * inv, -_amaxEff[i], _amaxEff[i]) : 0f;
                _vff[i] = vf;
                _prevTarget[i] = _target[i];
            }
            _ffValid = true;
            MocoSimd.JerkStep(_q, _v, _a, _j, _target, _vff, _aff, _vmaxEff, _amaxEff, _jmax, Gain, dt);
            Kinematics(dt);
            LastStepUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
        }
    }

    /// <summary>정지 궤적 1틱: 축별 감속(rad/s²)으로 속도 → 0(경로 동기) · 전 축 정지 = 브레이크.</summary>
    public void ExecuteStop(ReadOnlySpan<float> decel, float dt)
    {
        lock (_gate)
        {
            if (Mode == RobotMode.Braked) return;
            Mode = RobotMode.Stopping;
            var moving = false;
            for (var k = 0; k < Joints; k++)
            {
                var v = _v[k]; var dv = decel[k] * dt;
                var nv = MathF.Abs(v) <= dv ? 0f : v - MathF.Sign(v) * dv;
                _q[k] += 0.5f * (v + nv) * dt;
                _a[k] = (nv - v) / dt; _j[k] = 0f; _v[k] = nv;
                if (nv != 0f) moving = true;
            }
            if (!moving) { Mode = RobotMode.Braked; for (var k = 0; k < Joints; k++) _a[k] = 0f; }
            Kinematics(dt);
        }
    }

    /// <summary>자세 즉시 지정(셋업 · 정지 상태 전용).</summary>
    public void Teleport(ReadOnlySpan<float> q)
    {
        lock (_gate)
        {
            for (var k = 0; k < Joints; k++) { _q[k] = _target[k] = _prevTarget[k] = Math.Clamp(q[k], LimitLo[k], LimitHi[k]); _v[k] = _a[k] = _j[k] = _vff[k] = _aff[k] = 0f; }
            _ffValid = false;
            Forward(_q, Base, ToolLength, _pts, out _);
            Tcp = _goal = _pts[Points - 1];
            Mode = RobotMode.Tracking;
        }
    }

    /// <summary>브레이크 해제 → 추종 재개(목표 = 현재 자세 · 점프 없음).</summary>
    public void Release()
    {
        lock (_gate)
        {
            for (var k = 0; k < Joints; k++) { _target[k] = _prevTarget[k] = _q[k]; _v[k] = 0f; _a[k] = 0f; _j[k] = 0f; _vff[k] = 0f; _aff[k] = 0f; }
            _ffValid = false;
            Mode = RobotMode.Tracking;
        }
    }

    /// <summary>축별 감속 한계에서의 정지 시간(동기 정지 = 최대값).</summary>
    public float StopTime(ReadOnlySpan<float> decel)
    {
        var t = 0f;
        for (var k = 0; k < Joints; k++) t = MathF.Max(t, MathF.Abs(_v[k]) / decel[k]);
        return t;
    }

    private void Kinematics(float dt)
    {
        var prev = Tcp;
        Forward(_q, Base, ToolLength, _pts, out _);
        Tcp = _pts[Points - 1];
        TcpSpeed = Vector3.Distance(prev, Tcp) / MathF.Max(dt, 1e-5f);
        TrackingErrorMm = Vector3.Distance(_goal, Tcp) * 1000f;
        // 중력 토크(수평 모멘트 암) + 관성
        var rhat = new Vector3(MathF.Cos(_q[0]), 0f, -MathF.Sin(_q[0]));
        Vector3 s = _pts[2], e = _pts[3], w = _pts[4], t = _pts[6];
        var g2 = Gravity * (MassUpper * Vector3.Dot((s + e) * 0.5f - s, rhat) + MassFore * Vector3.Dot((e + w) * 0.5f - s, rhat) + MassPayload * Vector3.Dot(t - s, rhat));
        var g3 = Gravity * (MassFore * Vector3.Dot((e + w) * 0.5f - e, rhat) + MassPayload * Vector3.Dot(t - e, rhat));
        var tw = t - w;
        var g5 = Gravity * MassPayload * MathF.Sqrt(tw.X * tw.X + tw.Z * tw.Z);
        for (var k = 0; k < Joints; k++) _torque[k] = Inertia[k] * MathF.Abs(_a[k]);
        _torque[1] += MathF.Abs(g2); _torque[2] += MathF.Abs(g3); _torque[4] += g5;
    }

    /// <summary>패스 사전 검증(무할당): 샘플별 IK(연속 시드) → 관절 SoA → SIMD 차분 속도/가속/저크 피크 · 도달 불가/한계.</summary>
    public void AnalyzePass(ReadOnlySpan<CameraPose> poses, float dt)
    {
        lock (_gate)
        {
            var n = Math.Min(poses.Length, MaxPass);
            PassSamples = n; PassUnreachable = 0; PassLimitHits = 0;
            Array.Clear(_peakV); Array.Clear(_peakA); Array.Clear(_peakJ);
            if (n == 0) return;
            _q.AsSpan(0, Joints).CopyTo(_seed);
            for (var s = 0; s < n; s++)
            {
                var st = Solve(poses[s], _seed, _tmp);
                if (st == IkStatus.Clamped) PassUnreachable++; else if (st == IkStatus.LimitViolation) PassLimitHits++;
                for (var k = 0; k < Joints; k++) { _passQ[k * MaxPass + s] = _tmp[k]; _seed[k] = _tmp[k]; }
            }
            var inv = 1f / dt;
            for (var k = 0; k < Joints; k++)
            {
                var src = _passQ.AsSpan(k * MaxPass, n);
                if (n < 2) continue;
                MocoSimd.Differentiate(_passD1.AsSpan(0, n - 1), src, inv);
                _peakV[k] = MocoSimd.MaxAbs(_passD1.AsSpan(0, n - 1));
                if (n < 3) continue;
                MocoSimd.Differentiate(_passD2.AsSpan(0, n - 2), _passD1.AsSpan(0, n - 1), inv);
                _peakA[k] = MocoSimd.MaxAbs(_passD2.AsSpan(0, n - 2));
                if (n < 4) continue;
                MocoSimd.Differentiate(_passD3.AsSpan(0, n - 3), _passD2.AsSpan(0, n - 2), inv);
                _peakJ[k] = MocoSimd.MaxAbs(_passD3.AsSpan(0, n - 3));
            }
        }
    }

    public float PassAngle(int joint, int sample) => _passQ[joint * MaxPass + sample];
}
