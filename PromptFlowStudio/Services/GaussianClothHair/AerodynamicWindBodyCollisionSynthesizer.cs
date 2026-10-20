using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
namespace PromptFlowStudio.Services.GaussianClothHair;

/// <summary>
/// [PromptFlow Studio v17.59.0] 🌬 공기역학 바람 &amp; 신체 캡슐 충돌 반응 합성기(<see cref="ApplyForces"/> · <see cref="Collide"/> 무할당).
/// 바람 = 기본 벡터(풍속 · 풍향 — v17.28 지균풍/난류 연동 가능) × 저주파 돌풍 + 위치·시간 정현 합 난류(캐릭터 가속도 후류 가산).
/// 상대 유속 v = 바람 − 입자 속도(이동 · 도약 · 회전 자동 반영) → 의상: 법선 항력 ½ρC_d|vₙ|vₙ/σ + 양력(유동 수직 · |v|² sin2α) · 헤어: 축 수직 성분 항력.
/// 충돌 = v17.57 래그돌 24 관절 → 23 본 캡슐(<see cref="Collides"/> 대상표)(<see cref="RagdollSkeleton.BoneRadius"/> + 두께) 거리 필드 투영 · 마지막 반복에 캡슐 표면 속도 기준 쿨롱 마찰 · 법선 반발 계수 적용.
/// </summary>
public sealed class AerodynamicWindBodyCollisionSynthesizer
{
    public const int Capsules = RagdollSkeleton.Count - 1;
    public const float AirDensity = 1.2f, MaxAccel = 25f, HairDrag = 1.1f, HairFriction = 0.3f, HairRestitution = 0.05f;
    private readonly Vector3[] _a0 = new Vector3[Capsules], _b0 = new Vector3[Capsules], _a1 = new Vector3[Capsules], _b1 = new Vector3[Capsules], _bc = new Vector3[Capsules];
    private readonly float[] _r = new float[Capsules], _br = new float[Capsules];
    private Vector3 _pelvis, _vel;
    private bool _hasBody;
    private int _contacts;

    public float WindSpeed { get; set; } = 3f;
    public float WindDirDeg { get; set; } = 210f;
    public float Turbulence { get; set; } = 0.35f;
    public bool AeroEnabled { get; set; } = true;
    public bool CollisionEnabled { get; set; } = true;
    public float Time { get; private set; }
    public Vector3 BodyVelocity => _vel;
    public Vector3 BodyAccel { get; private set; }
    public float EffectiveTurbulence { get; private set; }
    public float PeakApparentWind { get; private set; }
    public float FrameMaxAccel { get; private set; }
    public int FrameContacts { get; private set; }
    public long Contacts { get; private set; }
    public Vector3 CapsuleA(int c) => _a1[c];
    public Vector3 CapsuleB(int c) => _b1[c];
    public Vector3 PrevCapsuleA(int c) => _a0[c];
    public Vector3 PrevCapsuleB(int c) => _b0[c];
    public float Radius(int c) => _r[c];
    public float BoundRadius(int c) => _br[c];
    public Vector3 BoundCenter(int c) => _bc[c];
    public static int Bone(int c) => c + 1;
    /// <summary>입자-캡슐 충돌 대상 — 스커트 = 몸통 · 골반 · 다리(캡슐 0 · 1 · 15..22 · 팔/목/머리는 스커트 바깥 레이어 · 휴지 전완이 골반 캡슐과 겹쳐 허리 고정점과 상충) · 헤어 = 전 캡슐.</summary>
    public static bool Collides(int particle, int capsule) => particle >= RealtimeGaussianClothHairPhysicsEngine.ClothParticles || capsule is <= 1 or >= 15;

    public Vector3 BaseWind
    {
        get
        {
            var a = WindDirDeg * (MathF.PI / 180f);
            return new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * WindSpeed;
        }
    }

    /// <summary>v17.28 기상 엔진 지균풍 · 풍향 · 난류 연동.</summary>
    public void LinkWeather(float windMs, float dirDeg, float turbulence)
    {
        WindSpeed = Math.Clamp(float.IsFinite(windMs) ? windMs : 0f, 0f, 25f);
        WindDirDeg = float.IsFinite(dirDeg) ? dirDeg : 0f;
        Turbulence = Math.Clamp(float.IsFinite(turbulence) ? turbulence : 0f, 0f, 1f);
    }

    /// <summary>캡슐 프록시 갱신(이전 → 현재 시프트) · 골반 속도/가속도 유한차분(지수 평활).</summary>
    public void UpdateBody(AvatarFkAnimator anim, float dt)
    {
        var p = anim.Positions;
        for (var c = 0; c < Capsules; c++)
        {
            var b = Bone(c);
            Vector3 a = p[RagdollSkeleton.Parent[b]], e = p[b];
            if (_hasBody) { _a0[c] = _a1[c]; _b0[c] = _b1[c]; } else { _a0[c] = a; _b0[c] = e; }
            _a1[c] = a; _b1[c] = e; _r[c] = RagdollSkeleton.BoneRadius[b];
            _bc[c] = (a + e) * 0.5f; _br[c] = Vector3.Distance(a, e) * 0.5f + _r[c];
        }
        var pel = p[(int)RagdollJoint.Pelvis];
        if (_hasBody && dt > 0f)
        {
            var v = (pel - _pelvis) / dt;
            var acc = (v - _vel) / dt;
            var k = 1f - MathF.Exp(-dt * 12f);
            BodyAccel += (acc - BodyAccel) * k;
            _vel = v;
        }
        _pelvis = pel; _hasBody = true;
        Time += dt;
        EffectiveTurbulence = Math.Clamp(Turbulence + 0.015f * BodyAccel.Length(), 0f, 1.5f);
    }

    public void ResetBody() { _hasBody = false; _vel = Vector3.Zero; BodyAccel = Vector3.Zero; }

    /// <summary>위치 · 시간 바람 표본(돌풍 × 기본 + 정현 합 난류).</summary>
    public Vector3 WindAt(Vector3 p)
    {
        var t = Time; var tb = EffectiveTurbulence;
        var b = BaseWind;
        var gust = 1f + 0.45f * tb * MathF.Sin(0.7f * t) * MathF.Sin(1.9f * t + 0.4f);
        var tx = MathF.Sin(4.3f * p.Y + 2.1f * t) + 0.5f * MathF.Sin(6.7f * p.Z - 3.7f * t + 0.5f);
        var ty = MathF.Sin(4.7f * p.Z + 1.9f * t + 1.3f) + 0.5f * MathF.Sin(7.1f * p.X + 2.3f * t);
        var tz = MathF.Sin(4.1f * p.X - 2.4f * t + 2.1f) + 0.5f * MathF.Sin(6.9f * p.Y + 3.2f * t);
        var amp = tb * (0.6f * WindSpeed + 0.8f);
        return b * gust + new Vector3(tx, 0.5f * ty, tz) * amp;
    }

    /// <summary>상대 유속 공기역학 가속도 → 엔진 외력 버퍼 가산.</summary>
    public void ApplyForces(RealtimeGaussianClothHairPhysicsEngine e, float dt)
    {
        if (!AeroEnabled) { FrameMaxAccel = 0f; return; }
        var inv = 1f / dt;
        var m = e.Material;
        float kd = 0.5f * AirDensity * m.Drag / m.Density, kl = 0.5f * AirDensity * m.Lift / m.Density;
        float peakW = 0f, peakA = 0f;
        const int U = RealtimeGaussianClothHairPhysicsEngine.SkirtU, V = RealtimeGaussianClothHairPhysicsEngine.SkirtV;
        for (var v = 1; v < V; v++)
            for (var u = 0; u < U; u++)
            {
                var i = RealtimeGaussianClothHairPhysicsEngine.Cloth(u, v);
                var x = e.Position(i);
                var vr = WindAt(x) - (x - e.Previous(i)) * inv;
                var n = e.ClothNormal(u, v);
                var v2 = vr.LengthSquared();
                if (v2 < 1e-8f) continue;
                var vn = Vector3.Dot(vr, n);
                var a = n * (kd * MathF.Abs(vn) * vn);
                var vl = MathF.Sqrt(v2); var vh = vr / vl; var c = vn / vl;
                a += (n - vh * c) * (kl * v2 * 2f * c);
                peakW = MathF.Max(peakW, vl);
                peakA = MathF.Max(peakA, Add(e, i, a));
            }
        const int N = RealtimeGaussianClothHairPhysicsEngine.Nodes;
        for (var s = 0; s < RealtimeGaussianClothHairPhysicsEngine.Strands; s++)
            for (var j = 2; j < N; j++)
            {
                var i = RealtimeGaussianClothHairPhysicsEngine.Hair(s, j);
                var x = e.Position(i);
                var tg = (j < N - 1 ? e.Position(i + 1) : x) - e.Position(i - 1);
                var tl = tg.Length(); var T = tl > 1e-9f ? tg / tl : Vector3.UnitY;
                var vr = WindAt(x) - (x - e.Previous(i)) * inv;
                var vp = vr - T * Vector3.Dot(vr, T);
                peakW = MathF.Max(peakW, vr.Length());
                peakA = MathF.Max(peakA, Add(e, i, vp * (HairDrag * vp.Length())));
            }
        if (peakW > PeakApparentWind) PeakApparentWind = peakW;
        FrameMaxAccel = peakA;
    }

    private static float Add(RealtimeGaussianClothHairPhysicsEngine e, int i, Vector3 a)
    {
        var l = a.Length();
        if (!(l < 1e9f)) return 0f;
        if (l > MaxAccel) { a *= MaxAccel / l; l = MaxAccel; }
        e.AX[i] += a.X; e.AY[i] += a.Y; e.AZ[i] += a.Z;
        return l;
    }

    /// <summary>캡슐 거리 필드 투영(+ 마지막 반복: 마찰 · 반발 속도 응답).</summary>
    public void Collide(RealtimeGaussianClothHairPhysicsEngine e, bool velocityPass)
    {
        if (!CollisionEnabled) return;
        if (velocityPass) _contacts = 0;
        var m = e.Material;
        for (var i = 0; i < RealtimeGaussianClothHairPhysicsEngine.Particles; i++)
        {
            if (e.W[i] <= 0f) continue;
            var x = e.Position(i);
            var hair = e.IsHair(i);
            var th = hair ? RealtimeGaussianClothHairPhysicsEngine.HairThickness : m.Thickness;
            for (var c = 0; c < Capsules; c++)
            {
                var R = _r[c] + th; var br = _br[c] + th;
                if (Vector3.DistanceSquared(x, _bc[c]) > br * br || !Collides(i, c)) continue;
                var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(_a1[c], _b1[c], x, out var t);
                var dv = x - q; var d2 = dv.LengthSquared();
                if (d2 >= R * R) continue;
                var d = MathF.Sqrt(d2);
                var n = d > 1e-7f ? dv / d : Outward(x, q);
                x = q + n * R;
                e.SetPosition(i, x);
                if (!velocityPass) continue;
                _contacts++;
                var dc = (_a1[c] - _a0[c]) * (1f - t) + (_b1[c] - _b0[c]) * t;
                var vr = x - e.Previous(i) - dc;
                var vn = Vector3.Dot(vr, n);
                var vt = vr - n * vn;
                var mu = hair ? HairFriction : m.Friction; var rest = hair ? HairRestitution : m.Restitution;
                var vtl = vt.Length(); var pen = R - d;
                vt *= vtl > 1e-9f ? MathF.Max(0f, 1f - mu * pen / vtl) : 0f;
                if (vn < 0f) vn = -rest * vn;
                e.SetPrevious(i, x - dc - vt - n * vn);
            }
        }
        if (velocityPass) { FrameContacts = _contacts; Contacts += _contacts; }
    }

    private Vector3 Outward(Vector3 x, Vector3 q)
    {
        var o = q - _pelvis; o.Y = 0f;
        var l = o.Length();
        return l > 1e-6f ? o / l : Vector3.UnitZ;
    }

    /// <summary>현재 캡슐 부호 거리(표면 기준) 최소값 · 해당 캡슐.</summary>
    public float SignedDistance(Vector3 x, out int capsule)
    {
        var best = float.MaxValue; capsule = -1;
        for (var c = 0; c < Capsules; c++)
        {
            var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(_a1[c], _b1[c], x, out _);
            var d = Vector3.Distance(x, q) - _r[c];
            if (d < best) { best = d; capsule = c; }
        }
        return best;
    }
}
