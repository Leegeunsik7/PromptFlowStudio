using System.Numerics;

namespace PromptFlowStudio.Services.CinematicDirector;

public enum DirectorCam : byte { Master, CamA, CamB, Action }
public enum ShotSize : byte { Wide, Medium, MediumClose, CloseUp, ExtremeCloseUp }

/// <summary>배우 입력(프레임) — 머리 중심 · 눈 · 시선 · 발 위치 · 키.</summary>
public struct DirectorActor
{
    public Vector3 Foot, Head, Eye, Gaze;
    public float Height;
}

/// <summary>가상 카메라 상태(위치 · 시선 · FoV · 초점/DoF · 구도 지표).</summary>
public struct CameraRig
{
    public Vector3 Position, Forward, Up, Right;
    public float VFovDeg, FocalMm, FocusM, FStop, NearM, FarM;
    public ShotSize Size;
    public byte Subject;
    /// <summary>주 피사체 눈의 화면 좌표(NDC −1..1 · +y 위) · 헤드룸 오차 · 리드룸 오차.</summary>
    public Vector2 EyeNdc;
    public float HeadroomError, LeadroomError, Score;
    public bool Occluded;
}

/// <summary>
/// [PromptFlow Studio v17.39.0] 🎥 실시간 3D 샷 구도 &amp; 가상 멀티 카메라 배치 엔진(<see cref="Update"/> 무할당 · 60 fps).
///   ▸ 카메라 4: 마스터(투샷 와이드 · 축 수직 · 두 배우 + 여유 15 % 를 FoV 안에) · A(OTS — 배우 1 어깨 너머 배우 0) · B(OTS — 배우 0 어깨 너머 배우 1) ·
///     액션(트래킹 — 지정 배우 궤도 추적 · 미디엄 ↔ ECU).
///   ▸ 구도 문법: 눈 = 상단 삼분선(NDC y = +1/3 → 헤드룸) · 리드룸 = 시선의 화면 방향 반대편 삼분선(x = ∓1/3)에 눈 배치 ·
///     샷 크기별 피사체 세로 점유(와이드 = 전신 70 % · 미디엄 = 허리 위 · MCU = 가슴 위 · CU = 머리 · ECU = 눈 밴드) → 수직 FoV ·
///     목표 화면 좌표를 만드는 yaw/pitch 해석해(시선축 회전 · 오프셋 각 = atan(ndc · tan(FoV/2))).
///   ▸ 후보 평가(SIMD): 카메라당 궤도 후보 32(방위 · 고도) SoA → Vector&lt;float&gt; 로 축 측 · 거리 · 시선 정면성(눈이 보이는가) · 기둥/상대 배우 수직 캡슐 2D 차폐(선분-점 거리) 점수 → 최적 후보.
///   ▸ 피사계 심도: 초점 = 피사체 눈 거리 · 35 mm 풀프레임 CoC 0.03 mm · 하이퍼포컬 H = f²/(N c) + f · 근/원 한계(얇은 렌즈) · 샷 크기별 조리개(와이드 f/5.6 → ECU f/1.8).
///   ▸ 궤적 보간: 위치/주시점/FoV 임계 감쇠 스프링(반감기 0.25 s · 액션 0.12 s) — 컷 시에는 <see cref="Snap"/> 로 즉시 정착.
/// </summary>
public sealed class RealtimeCinematicShotFramingEngine
{
    public const int Cams = 4, Candidates = 32, MaxOccluders = 8;
    public const float Third = 1f / 3f, SensorH = 24f, Coc = 0.03f, Aspect = 16f / 9f;

    private readonly CameraRig[] _rig = new CameraRig[Cams], _goal = new CameraRig[Cams];
    private readonly Vector3[] _vel = new Vector3[Cams], _tgt = new Vector3[Cams], _tgtVel = new Vector3[Cams];
    private readonly float[] _fovVel = new float[Cams];
    private readonly float[] _cx = new float[Candidates], _cy = new float[Candidates], _cz = new float[Candidates], _score = new float[Candidates], _scoreScalar = new float[Candidates];
    private readonly float[] _ox = new float[MaxOccluders], _oz = new float[MaxOccluders], _or = new float[MaxOccluders];
    private readonly bool[] _snap = new bool[Cams];
    private int _occN;

    public RealtimeCinematicShotFramingEngine()
    {
        for (var c = 0; c < Cams; c++) { _snap[c] = true; _rig[c].Up = Vector3.UnitY; _rig[c].VFovDeg = 40f; }
    }

    public DirectorActor A, B;
    /// <summary>축 측 부호(+1/−1) — 가드가 확정한 180° 선의 카메라 측(마스터 기준).</summary>
    public float LineSide { get; set; } = 1f;
    public byte ActionSubject { get; set; } = 1;
    public ShotSize ActionSize { get; set; } = ShotSize.MediumClose;
    /// <summary>액션 카메라 궤도 방위(축 기준 rad · 자유 트래킹 — 가드가 선 넘김을 차단).</summary>
    public float ActionOrbit { get; set; } = 0.6f;
    /// <summary>강제 샷 크기 배율(점프컷 회피 — 가드 요청).</summary>
    public float ActionTighten { get; set; } = 1f;
    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    public float LastScoreDiff { get; private set; }
    public double LastUpdateUs { get; private set; }
    public double MaxUpdateUs { get; private set; }

    public ref readonly CameraRig Rig(int c) => ref _rig[c];
    public ref readonly CameraRig Goal(int c) => ref _goal[c];
    public Vector3 Target(int c) => _tgt[c];
    public int OccluderCount => _occN;
    public Vector3 Occluder(int i, out float r) { r = _or[i]; return new Vector3(_ox[i], 0f, _oz[i]); }

    public static string Name(DirectorCam c) => c switch { DirectorCam.Master => "마스터", DirectorCam.CamA => "Cam-A(OTS L)", DirectorCam.CamB => "Cam-B(OTS R)", _ => "Action" };
    public static string SizeName(ShotSize s) => s switch { ShotSize.Wide => "풀샷", ShotSize.Medium => "미디엄", ShotSize.MediumClose => "MCU", ShotSize.CloseUp => "CU", _ => "ECU" };

    public void ClearOccluders() => _occN = 0;
    public void AddOccluder(Vector3 p, float r) { if (_occN < MaxOccluders) { _ox[_occN] = p.X; _oz[_occN] = p.Z; _or[_occN] = r; _occN++; } }

    /// <summary>다음 Update 에서 카메라 c 를 목표로 즉시 정착(컷 직전 대기 카메라).</summary>
    public void Snap(int c) => _snap[c] = true;

    /// <summary>외부(가드) 위치 교정 — 축 반사 등.</summary>
    public void Override(int c, Vector3 position)
    {
        _rig[c].Position = position; _vel[c] = Vector3.Zero;
        Orient(ref _rig[c], _tgt[c]);
        Lens(ref _rig[c], _rig[c].Subject == 0 ? A : B);
        Measure(ref _rig[c], _rig[c].Subject == 0 ? A : B);
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Update(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var axis = Axis2(out var mid, out var dist);
        var nrm = new Vector3(-axis.Z, 0f, axis.X) * LineSide;   // 카메라 측 법선
        // 마스터: 축 수직 · 두 배우 + 여유를 수평/수직 FoV 안에
        {
            ref var g = ref _goal[0];
            var span = dist + 1.2f;
            var vfov = 34f;
            var hfov = 2f * MathF.Atan(MathF.Tan(vfov * 0.5f * MathF.PI / 180f) * Aspect);
            var d = MathF.Max(3f, 0.5f * span * 1.15f / MathF.Tan(0.5f * hfov));
            var look = mid + new Vector3(0f, 1.1f, 0f);
            g.Position = look + nrm * d + new Vector3(0f, 0.25f, 0f);
            g.VFovDeg = vfov; g.Size = ShotSize.Wide; g.Subject = 0;
            _goalTarget0 = look;
        }
        Ots(1, B, A, axis, nrm, -1f);   // A: 배우 1 어깨 너머 → 배우 0
        Ots(2, A, B, axis, nrm, +1f);   // B: 배우 0 어깨 너머 → 배우 1
        ActionCam(axis, nrm);
        for (var c = 0; c < Cams; c++) Smooth(c, dt);
        LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (LastUpdateUs > MaxUpdateUs) MaxUpdateUs = LastUpdateUs;
    }

    private Vector3 _goalTarget0;
    private readonly Vector3[] _goalTarget = new Vector3[Cams];

    private Vector3 Axis2(out Vector3 mid, out float dist)
    {
        var d = B.Foot - A.Foot; d.Y = 0f;
        dist = d.Length();
        mid = 0.5f * (A.Foot + B.Foot); mid.Y = 0f;
        return dist > 1e-4f ? d / dist : Vector3.UnitX;
    }

    /// <summary>OTS — 전경 배우(fg) 어깨 너머 피사체(subj) · 카메라 측 유지.</summary>
    private void Ots(int c, in DirectorActor fg, in DirectorActor subj, Vector3 axis, Vector3 nrm, float sideOfAxis)
    {
        ref var g = ref _goal[c];
        g.Size = ShotSize.MediumClose; g.Subject = (byte)(c == 1 ? 0 : 1);
        // 전경 배우 뒤(피사체 반대) 0.9 m · 카메라 측으로 0.55 m · 어깨 높이
        var toSubj = subj.Foot - fg.Foot; toSubj.Y = 0f;
        var dir = toSubj.LengthSquared() > 1e-6f ? Vector3.Normalize(toSubj) : axis * -sideOfAxis;
        var basePos = fg.Foot - dir * 0.9f + nrm * 0.55f;
        basePos.Y = fg.Head.Y - 0.12f;
        Search(c, basePos, subj, in fg, nrm, 0.35f, 0.5f);
        Frame(ref g, in subj, ShotSize.MediumClose, out var look);
        _goalTarget[c] = look;
    }

    private void ActionCam(Vector3 axis, Vector3 nrm)
    {
        ref var g = ref _goal[3];
        var subj = ActionSubject == 0 ? A : B;
        var other = ActionSubject == 0 ? B : A;
        var size = ActionSize;
        if (ActionTighten > 1.01f && size < ShotSize.ExtremeCloseUp) size = (ShotSize)Math.Min((int)size + 1, (int)ShotSize.ExtremeCloseUp);
        g.Size = size; g.Subject = ActionSubject;
        var dist = size switch { ShotSize.Wide => 4.2f, ShotSize.Medium => 2.8f, ShotSize.MediumClose => 2.2f, ShotSize.CloseUp => 1.7f, _ => 1.3f };
        // 궤도: 축 방향 기준 ActionOrbit(rad) — 자유(가드가 측 위반 교정)
        float ca = MathF.Cos(ActionOrbit), sa = MathF.Sin(ActionOrbit);
        var nrm0 = new Vector3(-axis.Z, 0f, axis.X);
        var od = axis * ca + nrm0 * sa;
        var basePos = subj.Foot + od * dist; basePos.Y = subj.Eye.Y - 0.05f;
        Search(3, basePos, subj, in other, Vector3.Zero, 0.25f, 0.3f);
        Frame(ref g, in subj, size, out var look);
        _goalTarget[3] = look;
    }

    /// <summary>
    /// 후보 32(기준 위치 주변 방위 ±spread · 고도 4 단) 점수(SIMD SoA) → 최적 위치 = goal.Position.
    /// 점수 = 기준 거리 페널티 + 차폐(기둥 · 비-OTS 상대 배우) + 눈 정면성(피사체 시선과 카메라 방향 내적) + 축 측(nrm ≠ 0 일 때 반대 측 큰 페널티).
    /// </summary>
    private void Search(int c, Vector3 basePos, in DirectorActor subj, in DirectorActor other, Vector3 nrm, float radial, float spreadRad)
    {
        var eye = subj.Eye;
        var rel = basePos - eye; var rh = new Vector2(rel.X, rel.Z);
        var r0 = MathF.Max(rh.Length(), 0.5f);
        var a0 = MathF.Atan2(rel.X, rel.Z);
        for (var k = 0; k < Candidates; k++)
        {
            var az = a0 + spreadRad * ((k % 8) / 3.5f - 1f);
            var rr = r0 * (1f + radial * ((k / 8) / 3f - 0.5f));
            _cx[k] = eye.X + MathF.Sin(az) * rr; _cz[k] = eye.Z + MathF.Cos(az) * rr; _cy[k] = basePos.Y;
        }
        var gz = new Vector2(subj.Gaze.X, subj.Gaze.Z);
        var gl = gz.Length(); if (gl > 1e-5f) gz /= gl;
        var mid = 0.5f * (A.Foot + B.Foot);
        var otherAsOcc = c == 3;
        if (UseSimd) ScoreSimd(basePos, eye, gz, other.Foot, otherAsOcc, nrm, mid);
        ScoreScalar(basePos, eye, gz, other.Foot, otherAsOcc, nrm, mid, UseSimd ? _scoreScalar : _score);
        var diff = 0f;
        if (UseSimd) for (var k = 0; k < Candidates; k++) diff = MathF.Max(diff, MathF.Abs(_score[k] - _scoreScalar[k]));
        LastScoreDiff = diff;
        var best = 0;
        for (var k = 1; k < Candidates; k++) if (_score[k] < _score[best]) best = k;
        ref var g = ref _goal[c];
        g.Position = new Vector3(_cx[best], _cy[best], _cz[best]);
        g.Score = _score[best];
        g.Occluded = OccludedAt(g.Position, eye, otherAsOcc ? other.Foot : new Vector3(1e6f, 0f, 1e6f));
    }

    private void ScoreSimd(Vector3 basePos, Vector3 eye, Vector2 gz, Vector3 otherFoot, bool otherOcc, Vector3 nrm, Vector3 mid)
    {
        var vc = Vector<float>.Count;
        var bx = new Vector<float>(basePos.X); var bz = new Vector<float>(basePos.Z);
        var ex = new Vector<float>(eye.X); var ez = new Vector<float>(eye.Z);
        var gx = new Vector<float>(gz.X); var gzz = new Vector<float>(gz.Y);
        var nx = new Vector<float>(nrm.X); var nz = new Vector<float>(nrm.Z);
        var mx = new Vector<float>(mid.X); var mz = new Vector<float>(mid.Z);
        var zero = Vector<float>.Zero; var one = Vector<float>.One;
        for (var i = 0; i < Candidates; i += vc)
        {
            var x = new Vector<float>(_cx, i); var z = new Vector<float>(_cz, i);
            var dx = x - bx; var dz = z - bz;
            var s = (dx * dx + dz * dz) * new Vector<float>(2f);
            // 정면성: 카메라 → 눈 방향과 시선의 내적이 음수(마주봄)일수록 좋음 → 페널티 = (1 + dot)·0.6
            var vx = ex - x; var vz = ez - z;
            var vl = Vector.SquareRoot(vx * vx + vz * vz + new Vector<float>(1e-8f));
            var dot = (vx * gx + vz * gzz) / vl;
            s += (one + dot) * new Vector<float>(0.6f);
            // 축 측
            var side = (x - mx) * nx + (z - mz) * nz;
            s += Vector.ConditionalSelect(Vector.LessThan(side, zero), new Vector<float>(50f), zero);
            // 차폐: 선분(카메라 → 눈) 대 수직 캡슐 2D 거리
            for (var o = 0; o <= _occN; o++)
            {
                float px, pz, pr;
                if (o == _occN) { if (!otherOcc) break; px = otherFoot.X; pz = otherFoot.Z; pr = 0.25f; }
                else { px = _ox[o]; pz = _oz[o]; pr = _or[o]; }
                var qx = new Vector<float>(px) - x; var qz = new Vector<float>(pz) - z;
                var ll = vx * vx + vz * vz + new Vector<float>(1e-8f);
                var t = Vector.Min(Vector.Max((qx * vx + qz * vz) / ll, zero), one);
                var cx = qx - vx * t; var cz = qz - vz * t;
                var d = Vector.SquareRoot(cx * cx + cz * cz);
                var pen = Vector.Max(new Vector<float>(pr + 0.05f) - d, zero);
                s += pen * new Vector<float>(40f);
            }
            s.CopyTo(_score, i);
        }
    }

    private void ScoreScalar(Vector3 basePos, Vector3 eye, Vector2 gz, Vector3 otherFoot, bool otherOcc, Vector3 nrm, Vector3 mid, float[] dst)
    {
        for (var k = 0; k < Candidates; k++)
        {
            float x = _cx[k], z = _cz[k];
            float dx = x - basePos.X, dz = z - basePos.Z;
            var s = (dx * dx + dz * dz) * 2f;
            float vx = eye.X - x, vz = eye.Z - z;
            var vl = MathF.Sqrt(vx * vx + vz * vz + 1e-8f);
            s += (1f + (vx * gz.X + vz * gz.Y) / vl) * 0.6f;
            if ((x - mid.X) * nrm.X + (z - mid.Z) * nrm.Z < 0f) s += 50f;
            for (var o = 0; o <= _occN; o++)
            {
                float px, pz, pr;
                if (o == _occN) { if (!otherOcc) break; px = otherFoot.X; pz = otherFoot.Z; pr = 0.25f; }
                else { px = _ox[o]; pz = _oz[o]; pr = _or[o]; }
                float qx = px - x, qz = pz - z;
                var ll = vx * vx + vz * vz + 1e-8f;
                var t = Math.Clamp((qx * vx + qz * vz) / ll, 0f, 1f);
                float cx = qx - vx * t, cz = qz - vz * t;
                s += MathF.Max(pr + 0.05f - MathF.Sqrt(cx * cx + cz * cz), 0f) * 40f;
            }
            dst[k] = s;
        }
    }

    private bool OccludedAt(Vector3 cam, Vector3 eye, Vector3 other)
    {
        for (var o = 0; o <= _occN; o++)
        {
            float px, pz, pr;
            if (o == _occN) { if (other.X > 1e5f) break; px = other.X; pz = other.Z; pr = 0.25f; }
            else { px = _ox[o]; pz = _oz[o]; pr = _or[o]; }
            var v = new Vector2(eye.X - cam.X, eye.Z - cam.Z); var q = new Vector2(px - cam.X, pz - cam.Z);
            var t = Math.Clamp(Vector2.Dot(q, v) / MathF.Max(v.LengthSquared(), 1e-8f), 0f, 1f);
            if ((q - v * t).Length() < pr) return true;
        }
        return false;
    }

    /// <summary>
    /// 구도: 샷 크기 → 수직 FoV(피사체 점유) · 눈을 (∓1/3, +1/3) 에 두는 주시점(yaw/pitch 오프셋) · 렌즈/DoF.
    /// </summary>
    private void Frame(ref CameraRig g, in DirectorActor s, ShotSize size, out Vector3 look)
    {
        var cam = g.Position;
        var toEye = s.Eye - cam;
        var d = MathF.Max(toEye.Length(), 0.3f);
        // 세로 점유: 화면 높이(눈 위 1/3 · 아래 2/3)에 들어갈 피사체 세로 범위
        var cover = size switch
        {
            ShotSize.Wide => s.Height * 1.45f,
            ShotSize.Medium => (s.Eye.Y - s.Foot.Y) * 0.75f,
            ShotSize.MediumClose => 0.78f,
            ShotSize.CloseUp => 0.42f,
            _ => 0.16f,
        };
        var vfov = 2f * MathF.Atan(0.5f * cover / d) * 180f / MathF.PI;
        g.VFovDeg = Math.Clamp(vfov, 4f, 70f);
        var th = MathF.Tan(0.5f * g.VFovDeg * MathF.PI / 180f);
        // 리드룸: 시선의 화면 수평 방향(카메라 우측 축 성분) 반대 삼분선
        var fwd0 = Vector3.Normalize(new Vector3(toEye.X, 0f, toEye.Z));
        var right0 = Vector3.Normalize(Vector3.Cross(fwd0, Vector3.UnitY));
        var gs = Vector3.Dot(s.Gaze, right0);
        var tx = gs > 0.05f ? -Third : gs < -0.05f ? Third : 0f;
        var ty = Third;
        // 카메라 축을 눈 방향에서 화면 오프셋만큼 회전(yaw = atan(tx·tan·aspect) · pitch = atan(ty·tan))
        var yawOff = MathF.Atan(tx * th * Aspect);
        var pitchOff = MathF.Atan(ty * th);
        var dirYaw = MathF.Atan2(toEye.X, toEye.Z) + yawOff;   // +yaw = +X = 화면 좌(Right = Forward × Up) → 눈이 화면 우측으로
        var hDist = MathF.Sqrt(toEye.X * toEye.X + toEye.Z * toEye.Z);
        var dirPitch = MathF.Atan2(toEye.Y, hDist) - pitchOff;
        var f = new Vector3(MathF.Sin(dirYaw) * MathF.Cos(dirPitch), MathF.Sin(dirPitch), MathF.Cos(dirYaw) * MathF.Cos(dirPitch));
        look = cam + f * d;
        Orient(ref g, look);
        Lens(ref g, in s);
        Measure(ref g, in s);
    }

    private static void Orient(ref CameraRig g, Vector3 look)
    {
        var f = look - g.Position;
        var l = f.Length();
        g.Forward = l > 1e-6f ? f / l : Vector3.UnitZ;
        var r = Vector3.Cross(g.Forward, Vector3.UnitY);
        g.Right = r.LengthSquared() > 1e-8f ? Vector3.Normalize(r) : Vector3.UnitX;
        g.Up = Vector3.Cross(g.Right, g.Forward);
    }

    /// <summary>렌즈: 초점 거리(수직 FoV · 24 mm 센서) · 초점 = 눈 거리 · 조리개 · 하이퍼포컬 근/원.</summary>
    private static void Lens(ref CameraRig g, in DirectorActor s)
    {
        g.FocalMm = 0.5f * SensorH / MathF.Tan(0.5f * g.VFovDeg * MathF.PI / 180f);
        g.FocusM = MathF.Max(0.3f, Vector3.Dot(s.Eye - g.Position, g.Forward));
        g.FStop = g.Size switch { ShotSize.Wide => 5.6f, ShotSize.Medium => 4f, ShotSize.MediumClose => 2.8f, ShotSize.CloseUp => 2f, _ => 1.8f };
        var f = g.FocalMm / 1000f; var c = Coc / 1000f;
        var h = f * f / (g.FStop * c) + f;
        var u = g.FocusM;
        g.NearM = h * u / (h + (u - f));
        g.FarM = u < h - f ? h * u / (h - (u - f)) : float.PositiveInfinity;
    }

    private static void Measure(ref CameraRig g, in DirectorActor s)
    {
        g.EyeNdc = Project(in g, s.Eye, out _);
        g.HeadroomError = MathF.Abs(g.EyeNdc.Y - Third);
        var right = Vector3.Dot(s.Gaze, g.Right);
        var want = right > 0.05f ? -Third : right < -0.05f ? Third : 0f;
        g.LeadroomError = MathF.Abs(g.EyeNdc.X - want);
    }

    /// <summary>원근 투영(NDC x ∈ −1..1 수평 · y 위 +) · depth = 시선축 거리.</summary>
    public static Vector2 Project(in CameraRig g, Vector3 p, out float depth)
    {
        var d = p - g.Position;
        depth = Vector3.Dot(d, g.Forward);
        var th = MathF.Tan(0.5f * g.VFovDeg * MathF.PI / 180f);
        var z = MathF.Max(depth, 1e-3f);
        return new Vector2(Vector3.Dot(d, g.Right) / (z * th * Aspect), Vector3.Dot(d, g.Up) / (z * th));
    }

    private void Smooth(int c, float dt)
    {
        ref var g = ref _goal[c];
        var target = c == 0 ? _goalTarget0 : _goalTarget[c];
        ref var r = ref _rig[c];
        if (_snap[c])
        {
            r = g; _vel[c] = Vector3.Zero; _tgt[c] = target; _tgtVel[c] = Vector3.Zero; _fovVel[c] = 0f; _snap[c] = false;
        }
        else
        {
            var hl = c == 3 ? 0.12f : 0.25f;
            var w = 2f * 0.6931472f / hl;   // 임계 감쇠 ω(반감기)
            r.Position = Spring(r.Position, ref _vel[c], g.Position, w, dt);
            _tgt[c] = Spring(_tgt[c], ref _tgtVel[c], target, w, dt);
            var fv = _fovVel[c];
            r.VFovDeg = SpringF(r.VFovDeg, ref fv, g.VFovDeg, w, dt); _fovVel[c] = fv;
            r.Size = g.Size; r.Subject = g.Subject; r.Score = g.Score; r.Occluded = g.Occluded;
        }
        if (c == 0) { Orient(ref r, _tgt[c]); Lens(ref r, in A); r.EyeNdc = Project(in r, A.Eye, out _); r.HeadroomError = 0f; r.LeadroomError = 0f; return; }
        Orient(ref r, _tgt[c]);
        var s = r.Subject == 0 ? A : B;
        Lens(ref r, in s);
        Measure(ref r, in s);
    }

    private static Vector3 Spring(Vector3 x, ref Vector3 v, Vector3 goal, float w, float dt)
    {
        var e = x - goal;
        var j = v + w * e;
        var ex = MathF.Exp(-w * dt);
        var nx = goal + (e + j * dt) * ex;
        v = (v - j * w * dt) * ex;
        return nx;
    }

    private static float SpringF(float x, ref float v, float goal, float w, float dt)
    {
        var e = x - goal;
        var j = v + w * e;
        var ex = MathF.Exp(-w * dt);
        v = (v - j * w * dt) * ex;
        return goal + (e + j * dt) * ex;
    }
}
