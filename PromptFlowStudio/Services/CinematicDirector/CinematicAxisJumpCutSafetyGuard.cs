using System.Numerics;

namespace PromptFlowStudio.Services.CinematicDirector;

public enum CutVerdict : byte { Approved, AxisViolation, JumpCut, TooSoon, RateLimited }

/// <summary>
/// [PromptFlow Studio v17.39.0] 🛡 180° 가상선 침범 &amp; 점프컷 방어 세이프티 가드(무할당).
///   ▸ 액션 축: 배우 0 → 1 수평 벡터를 각도 언랩(연속 추적 · 배우가 서로 돌아가도 선 측 부호 일관) · 확립 측 = 첫 프레임 마스터 쪽.
///     카메라 측 부호 s = sign(cross(축, 카메라 − 중점)) — 매 프레임 4 카메라 검사 → 반대 측이면 축 직선에 대해 위치 반사(원천 클램핑) +
///     선 위 근처(|거리| &lt; 0.25 m)는 확립 측으로 0.25 m 밀어냄(축 위 = 방향성 모호) · 자유 트래킹(액션) 궤도는 합법 반평면(축과 ≥ 15°)으로 반사.
///   ▸ 컷 승인(<see cref="Review"/>): ① 대상 카메라 측 ≠ 확립 측 → 거부(AxisViolation) ② 같은 피사체 기준 시선 방향 각 &lt; 30° 이고 초점 거리 비 &lt; 1.5 → 점프컷 거부
///     ③ 직전 컷 후 최소 간격(일반 0.5 s · 충격 인서트 0.25 s) 미만 → 거부(TooSoon · 시각 피로) ④ 10 s 창 컷 수 상한(페이싱) 초과 → 거부.
///     OTS 역방향 쌍(A ↔ B)은 피사체가 바뀌므로 ② 대신 화면 시선 방향 반대(마주봄) 검사.
///   ▸ 온에어 감시: 프로그램 카메라의 측 위반 · 점프컷 · 간격을 사후 계수(가드 OFF 비교 · ON = 0 목표).
/// </summary>
public sealed class CinematicAxisJumpCutSafetyGuard
{
    public const int Cams = RealtimeCinematicShotFramingEngine.Cams, CutRing = 64;
    public const float MinCutAngleDeg = 30f, MinFocalRatio = 1.5f, MinGap = 0.5f, MinGapImpact = 0.25f, NearLine = 0.25f;

    private readonly double[] _cutTimes = new double[CutRing];
    private int _cutHead, _cutN;
    private float _axisAngle, _prevRaw;
    private bool _init;
    private double _lastCut = double.NegativeInfinity;

    public bool Enabled { get; set; } = true;
    public float LineSide { get; private set; } = 1f;
    public float AxisAngleDeg => _axisAngle * 57.29578f;
    public Vector3 AxisDir { get; private set; } = Vector3.UnitX;
    public Vector3 AxisMid { get; private set; }
    public int MaxCutsPer10s { get; set; } = 8;
    public long SideViolationsDetected { get; private set; }
    public long Reflections { get; private set; }
    public long OrbitClamps { get; private set; }
    public long AxisRejects { get; private set; }
    public long JumpRejects { get; private set; }
    public long FatigueRejects { get; private set; }
    public long RateRejects { get; private set; }
    public long OnAirAxisViolations { get; private set; }
    public long OnAirJumpCuts { get; private set; }
    public long OnAirTooSoon { get; private set; }
    public float MinApprovedAngleDeg { get; private set; } = 180f;
    public float LastCutAngleDeg { get; private set; }
    public float LastFocalRatio { get; private set; }
    public float MinGapSeen { get; private set; } = float.PositiveInfinity;
    public CutVerdict LastVerdict { get; private set; }

    public void ResetMetrics()
    {
        SideViolationsDetected = Reflections = OrbitClamps = AxisRejects = JumpRejects = FatigueRejects = RateRejects = OnAirAxisViolations = OnAirJumpCuts = OnAirTooSoon = 0;
        MinApprovedAngleDeg = 180f; MinGapSeen = float.PositiveInfinity;
    }

    public static string VerdictName(CutVerdict v) => v switch
    {
        CutVerdict.Approved => "승인", CutVerdict.AxisViolation => "180° 선 위반 거부", CutVerdict.JumpCut => "점프컷 거부(< 30° · 초점비 < 1.5)", CutVerdict.TooSoon => "간격 부족 거부", _ => "페이싱 상한 거부",
    };

    /// <summary>축 추적(언랩) · 확립 측 · 카메라 측 클램핑(엔진 위치 교정).</summary>
    public void Track(RealtimeCinematicShotFramingEngine eng)
    {
        var d = eng.B.Foot - eng.A.Foot; d.Y = 0f;
        var mid = 0.5f * (eng.A.Foot + eng.B.Foot); mid.Y = 0f;
        AxisMid = mid;
        if (d.LengthSquared() > 1e-6f)
        {
            var raw = MathF.Atan2(d.Z, d.X);
            if (!_init) { _axisAngle = raw; _prevRaw = raw; }
            else
            {
                var dd = raw - _prevRaw;
                if (dd > MathF.PI) dd -= MathF.Tau; else if (dd < -MathF.PI) dd += MathF.Tau;
                _axisAngle += dd; _prevRaw = raw;
            }
            AxisDir = new Vector3(MathF.Cos(_axisAngle), 0f, MathF.Sin(_axisAngle));
        }
        if (!_init)
        {
            // 확립: 마스터 카메라(목표) 측
            var s0 = Side(eng.Goal(0).Position);
            LineSide = s0 >= 0f ? 1f : -1f;
            _init = true;
        }
        eng.LineSide = LineSide;
        // 액션 궤도(축 기준 방위) 측 = sin(궤도) — 반대 측이면 궤도 반사(합법 반평면 클램프 · 선 위 15° 이내도 밀어냄)
        var so = MathF.Sin(eng.ActionOrbit) * LineSide;
        if (so < 0.26f)
        {
            if (so < 0f) SideViolationsDetected++;
            if (Enabled)
            {
                var o = eng.ActionOrbit;
                if (so < 0f) o = -o;
                if (MathF.Sin(o) * LineSide < 0.26f) o = MathF.Atan2(0.26f * LineSide, MathF.Cos(o) >= 0f ? 0.966f : -0.966f);
                eng.ActionOrbit = o;
                OrbitClamps++;
            }
        }
        for (var c = 0; c < Cams; c++)
        {
            var p = eng.Rig(c).Position;
            var s = Side(p) * LineSide;
            if (s >= NearLine) continue;
            if (s < 0f) SideViolationsDetected++;
            if (!Enabled) continue;
            // 축 직선 반사(측 위반) 또는 선 위 근처 밀어냄
            var n = Normal();
            var sd = Side(p);
            var fixedP = s < 0f ? p - 2f * sd * n : p + n * (LineSide * NearLine - sd);
            if (s < 0f && MathF.Abs(sd) < NearLine) fixedP = p + n * (LineSide * NearLine - sd);
            eng.Override(c, fixedP);
            Reflections++;
        }
    }

    /// <summary>축 법선(+측 방향) — cross(축, 위) 기준.</summary>
    private Vector3 Normal() => new(-AxisDir.Z, 0f, AxisDir.X);

    /// <summary>부호 거리(+ = 법선 측).</summary>
    public float Side(Vector3 p)
    {
        var r = p - AxisMid; r.Y = 0f;
        var n = Normal();
        return r.X * n.X + r.Z * n.Z;
    }

    /// <summary>컷 심사(무할당) — from → to · now(s) · impact(인서트).</summary>
    public CutVerdict Review(RealtimeCinematicShotFramingEngine eng, int from, int to, double now, bool impact)
    {
        ref readonly var a = ref eng.Rig(from);
        ref readonly var b = ref eng.Goal(to);
        Metrics(eng, from, to, out var ang, out var ratio);
        LastCutAngleDeg = ang; LastFocalRatio = ratio;
        if (!Enabled) return LastVerdict = CutVerdict.Approved;
        if (Side(b.Position) * LineSide < 0f) { AxisRejects++; return LastVerdict = CutVerdict.AxisViolation; }
        var gap = now - _lastCut;
        if (gap < (impact ? MinGapImpact : MinGap)) { FatigueRejects++; return LastVerdict = CutVerdict.TooSoon; }
        if (!impact && CutsSince(now - 10.0) >= MaxCutsPer10s) { RateRejects++; return LastVerdict = CutVerdict.RateLimited; }
        if (IsJump(ang, ratio, a.Subject, b.Subject)) { JumpRejects++; return LastVerdict = CutVerdict.JumpCut; }
        return LastVerdict = CutVerdict.Approved;
    }

    /// <summary>점프컷: 같은 피사체 · 시선 방향 각 &lt; 30° · 초점 거리 비 &lt; 1.5.</summary>
    public static bool IsJump(float angleDeg, float focalRatio, byte subjA, byte subjB) => subjA == subjB && angleDeg < MinCutAngleDeg && focalRatio < MinFocalRatio;

    /// <summary>두 카메라의 피사체 기준 시선 각 · 초점 거리 비(≥ 1).</summary>
    public static void Metrics(RealtimeCinematicShotFramingEngine eng, int from, int to, out float angleDeg, out float focalRatio)
    {
        ref readonly var a = ref eng.Rig(from);
        ref readonly var b = ref eng.Goal(to);
        var subj = b.Subject == 0 ? eng.A.Eye : eng.B.Eye;
        var va = subj - a.Position; var vb = subj - b.Position;
        va.Y = 0f; vb.Y = 0f;
        var la = va.Length(); var lb = vb.Length();
        angleDeg = la > 1e-5f && lb > 1e-5f ? MathF.Acos(Math.Clamp(Vector3.Dot(va, vb) / (la * lb), -1f, 1f)) * 57.29578f : 180f;
        var fa = MathF.Max(a.FocalMm, 1f); var fb = MathF.Max(b.FocalMm, 1f);
        focalRatio = MathF.Max(fa, fb) / MathF.Min(fa, fb);
    }

    /// <summary>컷 확정 기록 + 온에어 감시(가드 OFF 에도).</summary>
    public void Commit(RealtimeCinematicShotFramingEngine eng, int from, int to, double now, bool impact)
    {
        Metrics(eng, from, to, out var ang, out var ratio);
        ref readonly var a = ref eng.Rig(from); ref readonly var b = ref eng.Rig(to);
        if (IsJump(ang, ratio, a.Subject, b.Subject)) OnAirJumpCuts++;
        var gap = (float)(now - _lastCut);
        if (gap < MinGapSeen) MinGapSeen = gap;
        if (gap < (impact ? MinGapImpact : MinGap)) OnAirTooSoon++;
        if (ang < MinApprovedAngleDeg && a.Subject == b.Subject) MinApprovedAngleDeg = ang;
        _lastCut = now;
        _cutTimes[_cutHead] = now; _cutHead = (_cutHead + 1) % CutRing; if (_cutN < CutRing) _cutN++;
    }

    /// <summary>프레임 온에어 측 검사(프로그램 카메라).</summary>
    public void Observe(RealtimeCinematicShotFramingEngine eng, int program)
    {
        if (Side(eng.Rig(program).Position) * LineSide < 0f) OnAirAxisViolations++;
    }

    public int CutsSince(double t)
    {
        var n = 0;
        for (var i = 0; i < _cutN; i++) if (_cutTimes[i] >= t) n++;
        return n;
    }
}
