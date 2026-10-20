using System.Numerics;

namespace PromptFlowStudio.Services.CognitiveCinematography;

/// <summary>세트 프리미티브 종류: 구 · 캡슐 · 축 정렬 박스.</summary>
public enum SetPrimKind : byte { Sphere, Capsule, Box }

/// <summary>프리미티브 소속: 액터 머리 · 액터 몸통 · 기둥 · 상자(v17.08 세트 장애물).</summary>
public enum SetPrimRole : byte { ActorHead, ActorBody, Pillar, Crate }

/// <summary>
/// [PromptFlow Studio v17.21.0] 🎬 시네마틱 블로킹 세트(무할당 · 결정적).
///   ▸ 액터 2명(몸통 캡슐 + 머리 구 · 안구 위치 · 몸 방향) — 블로킹 마크 사이를 리사주 동선으로 이동(기둥 뒤 통과 = 가림 이벤트).
///   ▸ v17.08 가상 세트 장애물: 기둥 캡슐 3 · 상자 AABB 2 · 바닥 y = 0 · 뒷벽 z = −5 · 좌벽 x = −5 창(블라인드 쿠키)으로 햇빛 유입.
///   ▸ 해석적 광선 교차(구/캡슐/박스/평면) · SDF(장애물 가림 판정) · 창 평면 섀도 맵(64²) → 표면 그림자와 볼류메트릭 갓레이가 같은 가시성 사용.
/// </summary>
public sealed class CinematicBlockingSet
{
    public const int Actors = 2, MaxPrims = 12, ShadowRes = 64;
    public const float EyeHeight = 1.62f, HeadRadius = 0.12f, BodyRadius = 0.19f, WallZ = -5f, WindowX = -5f;
    public const float WinY0 = 1.2f, WinY1 = 3.6f, WinZ0 = -3.2f, WinZ1 = 1.8f, Slats = 5f, SlatOpen = 0.62f;

    private readonly SetPrimKind[] _kind = new SetPrimKind[MaxPrims];
    private readonly SetPrimRole[] _role = new SetPrimRole[MaxPrims];
    private readonly int[] _owner = new int[MaxPrims];
    private readonly Vector3[] _a = new Vector3[MaxPrims], _b = new Vector3[MaxPrims], _col = new Vector3[MaxPrims];
    private readonly float[] _r = new float[MaxPrims], _alpha = new float[MaxPrims];
    private readonly float[] _shadow = new float[ShadowRes * ShadowRes];
    private readonly Vector3[] _actorPos = new Vector3[Actors];
    private readonly float[] _actorFacing = new float[Actors], _eyeYaw = new float[Actors], _eyePitch = new float[Actors];
    private int _n, _obstacles;
    private float _time;

    public CinematicBlockingSet()
    {
        Light = Vector3.Normalize(new Vector3(0.78f, -0.52f, 0.34f));
        // 액터 0(따뜻) · 1(차가움): 머리 · 몸통
        for (var i = 0; i < Actors; i++)
        {
            Add(SetPrimKind.Capsule, SetPrimRole.ActorBody, i, default, default, BodyRadius, i == 0 ? new Vector3(0.78f, 0.36f, 0.24f) : new Vector3(0.22f, 0.4f, 0.74f));
            Add(SetPrimKind.Sphere, SetPrimRole.ActorHead, i, default, default, HeadRadius, new Vector3(0.86f, 0.66f, 0.54f));
        }
        _obstacles = _n;
        Add(SetPrimKind.Capsule, SetPrimRole.Pillar, -1, new Vector3(-0.2f, -1f, -0.9f), new Vector3(-0.2f, 6f, -0.9f), 0.32f, new Vector3(0.55f, 0.55f, 0.58f));
        Add(SetPrimKind.Capsule, SetPrimRole.Pillar, -1, new Vector3(2.4f, -1f, -2.6f), new Vector3(2.4f, 6f, -2.6f), 0.32f, new Vector3(0.55f, 0.55f, 0.58f));
        Add(SetPrimKind.Capsule, SetPrimRole.Pillar, -1, new Vector3(-2.6f, -1f, 1.6f), new Vector3(-2.6f, 6f, 1.6f), 0.32f, new Vector3(0.55f, 0.55f, 0.58f));
        Add(SetPrimKind.Box, SetPrimRole.Crate, -1, new Vector3(1.1f, 0f, 1.2f), new Vector3(1.9f, 0.9f, 1.9f), 0f, new Vector3(0.52f, 0.36f, 0.2f));
        Add(SetPrimKind.Box, SetPrimRole.Crate, -1, new Vector3(-3.4f, 0f, -3.2f), new Vector3(-2.4f, 1.2f, -2.4f), 0f, new Vector3(0.46f, 0.32f, 0.18f));
        Advance(0f);
    }

    /// <summary>햇빛 진행 방향(창 → 실내).</summary>
    public Vector3 Light { get; }
    public Vector3 SunColor { get; set; } = new(1f, 0.86f, 0.64f);
    public Vector3 Ambient { get; set; } = new(0.07f, 0.08f, 0.11f);
    public bool Blocking { get; set; } = true;
    public int PrimCount => _n;
    public int ObstacleStart => _obstacles;
    public SetPrimKind Kind(int k) => _kind[k];
    public SetPrimRole Role(int k) => _role[k];
    public int Owner(int k) => _owner[k];
    public float Alpha(int k) => _alpha[k];
    public float Time => _time;
    public Vector3 ActorPosition(int i) => _actorPos[i];
    public float ActorFacing(int i) => _actorFacing[i];
    public Vector3 Eye(int i) => _actorPos[i] + new Vector3(0f, EyeHeight, 0f) + 0.09f * Forward(_actorFacing[i]);
    public Vector3 HeadCenter(int i) => _actorPos[i] + new Vector3(0f, EyeHeight - 0.02f, 0f);
    public float EyeYaw(int i) => _eyeYaw[i];

    /// <summary>시선 방향 = 몸 방향 + v17.12 안구 요/피치(도).</summary>
    public Vector3 Gaze(int i)
    {
        var yaw = _actorFacing[i] + _eyeYaw[i] * (MathF.PI / 180f);
        var p = _eyePitch[i] * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(yaw) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(yaw) * MathF.Cos(p));
    }

    public static Vector3 Forward(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

    private void Add(SetPrimKind k, SetPrimRole role, int owner, Vector3 a, Vector3 b, float r, Vector3 col)
    {
        _kind[_n] = k; _role[_n] = role; _owner[_n] = owner; _a[_n] = a; _b[_n] = b; _r[_n] = r; _col[_n] = col; _alpha[_n] = 1f;
        _n++;
    }

    public void SetAlpha(int k, float a) => _alpha[k] = Math.Clamp(a, 0f, 1f);

    /// <summary>안구 요/피치(도, 머리 기준) — v17.12 가드 출력.</summary>
    public void SetEye(int i, float yawDeg, float pitchDeg) { _eyeYaw[i] = yawDeg; _eyePitch[i] = pitchDeg; }

    /// <summary>블로킹 마크 동선(리사주) 전진 · 프리미티브 갱신 · 섀도 맵 재구축(무할당).</summary>
    public void Advance(float dt)
    {
        if (Blocking) _time += dt;
        var t = _time;
        // 액터 0: 기둥(−0.2, −0.9) 앞뒤를 가로지르는 8자 동선 · 액터 1: 우측 마크 사이 왕복
        _actorPos[0] = new Vector3(-0.4f + 1.9f * MathF.Sin(t * 0.21f), 0f, -0.2f + 1.5f * MathF.Sin(t * 0.42f + 0.6f));
        _actorPos[1] = new Vector3(1.5f + 0.7f * MathF.Sin(t * 0.17f + 2f), 0f, -0.6f + 0.9f * MathF.Cos(t * 0.13f));
        for (var i = 0; i < Actors; i++)
        {
            // 서로 마주보는 블로킹(상대 방향) + 느린 몸 흔들림
            var d = _actorPos[1 - i] - _actorPos[i];
            _actorFacing[i] = MathF.Atan2(d.X, d.Z) + 0.35f * MathF.Sin(t * 0.3f + i * 2.1f);
        }
        for (var k = 0; k < _obstacles; k++)
        {
            var i = _owner[k];
            if (_role[k] == SetPrimRole.ActorHead) { _a[k] = HeadCenter(i); }
            else { _a[k] = _actorPos[i] + new Vector3(0f, 0.2f, 0f); _b[k] = _actorPos[i] + new Vector3(0f, 1.36f, 0f); }
        }
        BuildShadowMap();
    }

    // ── 해석적 교차 ─────────────────────────────────────────

    /// <summary>프리미티브 k 광선 교차 거리(없으면 +∞).</summary>
    public float Intersect(int k, Vector3 ro, Vector3 rd)
    {
        switch (_kind[k])
        {
            case SetPrimKind.Sphere:
            {
                var oc = ro - _a[k];
                var b = Vector3.Dot(oc, rd);
                var c = Vector3.Dot(oc, oc) - _r[k] * _r[k];
                var h = b * b - c;
                if (h < 0f) return float.PositiveInfinity;
                var t = -b - MathF.Sqrt(h);
                return t > 1e-4f ? t : float.PositiveInfinity;
            }
            case SetPrimKind.Capsule:
            {
                // iq 캡슐 교차
                Vector3 pa = _a[k], ba = _b[k] - pa, oa = ro - pa;
                float r = _r[k], baba = Vector3.Dot(ba, ba), bard = Vector3.Dot(ba, rd), baoa = Vector3.Dot(ba, oa), rdoa = Vector3.Dot(rd, oa), oaoa = Vector3.Dot(oa, oa);
                float a = baba - bard * bard, b = baba * rdoa - baoa * bard, c = baba * oaoa - baoa * baoa - r * r * baba, h = b * b - a * c;
                if (h < 0f) return float.PositiveInfinity;
                var t = (-b - MathF.Sqrt(h)) / a;
                var y = baoa + t * bard;
                if (y > 0f && y < baba) return t > 1e-4f ? t : float.PositiveInfinity;
                var oc = y <= 0f ? oa : ro - _b[k];
                b = Vector3.Dot(rd, oc); c = Vector3.Dot(oc, oc) - r * r; h = b * b - c;
                if (h < 0f) return float.PositiveInfinity;
                t = -b - MathF.Sqrt(h);
                return t > 1e-4f ? t : float.PositiveInfinity;
            }
            default:
            {
                var inv = new Vector3(1f / rd.X, 1f / rd.Y, 1f / rd.Z);
                var t0 = (_a[k] - ro) * inv;
                var t1 = (_b[k] - ro) * inv;
                var lo = Vector3.Min(t0, t1); var hi = Vector3.Max(t0, t1);
                var tn = MathF.Max(MathF.Max(lo.X, lo.Y), lo.Z);
                var tf = MathF.Min(MathF.Min(hi.X, hi.Y), hi.Z);
                return tf >= MathF.Max(tn, 1e-4f) && tn > 1e-4f ? tn : float.PositiveInfinity;
            }
        }
    }

    /// <summary>프리미티브 k 법선.</summary>
    public Vector3 Normal(int k, Vector3 p)
    {
        switch (_kind[k])
        {
            case SetPrimKind.Sphere: return Vector3.Normalize(p - _a[k]);
            case SetPrimKind.Capsule:
            {
                var ba = _b[k] - _a[k];
                var h = Math.Clamp(Vector3.Dot(p - _a[k], ba) / Vector3.Dot(ba, ba), 0f, 1f);
                return Vector3.Normalize(p - (_a[k] + ba * h));
            }
            default:
            {
                var c = (_a[k] + _b[k]) * 0.5f; var e = (_b[k] - _a[k]) * 0.5f;
                var q = (p - c) / e;
                var aq = Vector3.Abs(q);
                return aq.X > aq.Y && aq.X > aq.Z ? new Vector3(MathF.Sign(q.X), 0, 0) : aq.Y > aq.Z ? new Vector3(0, MathF.Sign(q.Y), 0) : new Vector3(0, 0, MathF.Sign(q.Z));
            }
        }
    }

    public Vector3 Color(int k) => _col[k];

    /// <summary>프리미티브 k 부호 거리.</summary>
    public float Sdf(int k, Vector3 p)
    {
        switch (_kind[k])
        {
            case SetPrimKind.Sphere: return Vector3.Distance(p, _a[k]) - _r[k];
            case SetPrimKind.Capsule:
            {
                var ba = _b[k] - _a[k]; var pa = p - _a[k];
                var h = Math.Clamp(Vector3.Dot(pa, ba) / Vector3.Dot(ba, ba), 0f, 1f);
                return (pa - ba * h).Length() - _r[k];
            }
            default:
            {
                var c = (_a[k] + _b[k]) * 0.5f; var e = (_b[k] - _a[k]) * 0.5f;
                var q = Vector3.Abs(p - c) - e;
                return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
            }
        }
    }

    /// <summary>환경 장애물(v17.08) SDF 최소값과 해당 인덱스.</summary>
    public float ObstacleSdf(Vector3 p, out int which)
    {
        var best = float.PositiveInfinity; which = -1;
        for (var k = _obstacles; k < _n; k++) { var d = Sdf(k, p); if (d < best) { best = d; which = k; } }
        return best;
    }

    /// <summary>가장 가까운 불투명 교차(알파 ≥ minAlpha) · 반투명 최근접 교차를 함께 반환. 바닥(−2)/뒷벽(−3) 포함.</summary>
    public float Trace(Vector3 ro, Vector3 rd, out int hit, out float ghostT, out int ghost)
    {
        var best = float.PositiveInfinity; hit = -1; ghostT = float.PositiveInfinity; ghost = -1;
        for (var k = 0; k < _n; k++)
        {
            var t = Intersect(k, ro, rd);
            if (t == float.PositiveInfinity) continue;
            if (_alpha[k] >= 0.999f) { if (t < best) { best = t; hit = k; } }
            else if (t < ghostT) { ghostT = t; ghost = k; }
        }
        if (rd.Y < -1e-5f) { var t = -ro.Y / rd.Y; if (t > 1e-4f && t < best) { best = t; hit = -2; } }
        if (rd.Z < -1e-5f) { var t = (WallZ - ro.Z) / rd.Z; if (t > 1e-4f && t < best) { best = t; hit = -3; } }
        if (ghostT >= best) { ghostT = float.PositiveInfinity; ghost = -1; }
        return best;
    }

    // ── 햇빛 가시성(창 쿠키 + 섀도 맵) ────────────────────────

    /// <summary>창 평면(x = −5)까지 역추적 → (y, z) · 창에서의 진행 거리.</summary>
    private bool WindowCoord(Vector3 p, out float wy, out float wz, out float dist)
    {
        dist = (p.X - WindowX) / Light.X;
        wy = p.Y - Light.Y * dist; wz = p.Z - Light.Z * dist;
        return dist > 0f;
    }

    /// <summary>블라인드 슬랫 쿠키(0/1 · 경계 부드러움).</summary>
    public static float Cookie(float wy, float wz)
    {
        if (wy < WinY0 || wy > WinY1 || wz < WinZ0 || wz > WinZ1) return 0f;
        var s = (wy - WinY0) / (WinY1 - WinY0) * Slats;
        var f = s - MathF.Floor(s);
        return Math.Clamp((SlatOpen - f) * 12f, 0f, 1f);
    }

    /// <summary>점 p 의 햇빛 가시도(쿠키 × 섀도 맵).</summary>
    public float LightVisibility(Vector3 p)
    {
        if (!WindowCoord(p, out var wy, out var wz, out var dist)) return 0f;
        var c = Cookie(wy, wz);
        if (c <= 0f) return 0f;
        var u = (int)((wz - WinZ0) / (WinZ1 - WinZ0) * ShadowRes);
        var v = (int)((wy - WinY0) / (WinY1 - WinY0) * ShadowRes);
        if ((uint)u >= ShadowRes || (uint)v >= ShadowRes) return c;
        return dist > _shadow[v * ShadowRes + u] + 0.02f ? 0f : c;
    }

    private void BuildShadowMap()
    {
        for (var v = 0; v < ShadowRes; v++)
        for (var u = 0; u < ShadowRes; u++)
        {
            var ro = new Vector3(WindowX, WinY0 + (v + 0.5f) / ShadowRes * (WinY1 - WinY0), WinZ0 + (u + 0.5f) / ShadowRes * (WinZ1 - WinZ0));
            var best = float.PositiveInfinity;
            for (var k = 0; k < _n; k++) { var t = Intersect(k, ro, Light); if (t < best) best = t; }
            _shadow[v * ShadowRes + u] = best;
        }
    }
}
