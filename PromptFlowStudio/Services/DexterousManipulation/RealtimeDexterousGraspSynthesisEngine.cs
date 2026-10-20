using System.Numerics;
using PromptFlowStudio.Services.DexterousHand;

namespace PromptFlowStudio.Services.DexterousManipulation;

public enum DexPropKind : byte { Sphere = 0, Cylinder, Handle, Box, Crate }

/// <summary>소품 로컬 파지 사이트: 중심 · 접근 방향(물체 → 손바닥 · 바깥) · 손가락 방향 힌트.</summary>
public readonly record struct GraspSite(Vector3 Center, Vector3 Approach, Vector3 FingerDir);

/// <summary>
/// 조작 소품(강체 · v17.13 해석 SDF 원시 <see cref="PropShape"/> 합성 · 질량 · 쿨롱 μ · 파지 사이트) — 구 · 원통 · 손잡이(망치) · 박스 · 협동 운반 상자(손잡이 바 2).
/// </summary>
public sealed class DexProp
{
    public const int MaxShapes = 3, MaxSites = 4;
    private readonly PropShape[] _shapes = new PropShape[MaxShapes];
    private readonly GraspSite[] _sites = new GraspSite[MaxSites];

    public DexProp(DexPropKind kind = DexPropKind.Cylinder) => Set(kind);

    public DexPropKind Kind { get; private set; }
    /// <summary>형상 변경 버전(파지 캐시 무효화).</summary>
    public int Version { get; private set; }
    public string Name { get; private set; } = "";
    public int ShapeCount { get; private set; }
    public int SiteCount { get; private set; }
    public float Mass { get; private set; }
    public float Friction { get; private set; }
    /// <summary>렌치 토크 정규화 길이(m).</summary>
    public float CharLength { get; private set; }
    public float ThumbOppositionDeg { get; private set; }
    public float FingerSpreadDeg { get; private set; }
    public Vector3 CenterOfMassLocal { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public PropShape Shape(int i) => _shapes[i];
    public GraspSite Site(int i) => _sites[i];

    public static string KindName(DexPropKind k) => k switch
    {
        DexPropKind.Sphere => "⚪ 공(구)", DexPropKind.Handle => "🔨 망치 손잡이", DexPropKind.Box => "📦 소형 박스", DexPropKind.Crate => "🧳 협동 운반 상자", _ => "🥫 원통(병)",
    };

    public void Set(DexPropKind k)
    {
        Kind = k; Name = KindName(k); Version++;
        Array.Clear(_shapes); Array.Clear(_sites);
        CenterOfMassLocal = Vector3.Zero;
        Vector3 ax = -Vector3.UnitX, bx = Vector3.UnitX, up = Vector3.UnitY;
        switch (k)
        {
            case DexPropKind.Sphere:
                _shapes[0] = new PropShape(SdfPrimitive.Sphere, Vector3.Zero, Vector3.Zero, 0.045f);
                ShapeCount = 1; Mass = 0.45f; Friction = 0.7f; CharLength = 0.045f; ThumbOppositionDeg = 55f; FingerSpreadDeg = 9f;
                _sites[0] = new GraspSite(Vector3.Zero, ax, up); _sites[1] = new GraspSite(Vector3.Zero, bx, up); SiteCount = 2;
                break;
            case DexPropKind.Handle:
                _shapes[0] = new PropShape(SdfPrimitive.Capsule, new Vector3(0, 0, -0.13f), new Vector3(0, 0, 0.07f), 0.016f);
                _shapes[1] = new PropShape(SdfPrimitive.RoundBox, new Vector3(0, 0, 0.12f), new Vector3(0.055f, 0.018f, 0.02f), 0.003f);
                ShapeCount = 2; Mass = 0.7f; Friction = 0.55f; CharLength = 0.13f; ThumbOppositionDeg = 75f; FingerSpreadDeg = 0f;
                CenterOfMassLocal = new Vector3(0, 0, 0.06f);
                _sites[0] = new GraspSite(new Vector3(0, 0, -0.075f), ax, up); _sites[1] = new GraspSite(new Vector3(0, 0, 0.015f), bx, up); SiteCount = 2;
                break;
            case DexPropKind.Box:
                _shapes[0] = new PropShape(SdfPrimitive.RoundBox, Vector3.Zero, new Vector3(0.026f, 0.03f, 0.08f), 0.004f);
                ShapeCount = 1; Mass = 0.4f; Friction = 0.5f; CharLength = 0.08f; ThumbOppositionDeg = 60f; FingerSpreadDeg = 0f;
                _sites[0] = new GraspSite(new Vector3(0, 0, -0.042f), ax, up); _sites[1] = new GraspSite(new Vector3(0, 0, 0.042f), bx, up); SiteCount = 2;
                break;
            case DexPropKind.Crate:
                _shapes[0] = new PropShape(SdfPrimitive.RoundBox, Vector3.Zero, new Vector3(0.3f, 0.14f, 0.2f), 0.01f);
                _shapes[1] = new PropShape(SdfPrimitive.Capsule, new Vector3(-0.5f, 0.06f, -0.17f), new Vector3(-0.5f, 0.06f, 0.17f), 0.014f);
                _shapes[2] = new PropShape(SdfPrimitive.Capsule, new Vector3(0.5f, 0.06f, -0.17f), new Vector3(0.5f, 0.06f, 0.17f), 0.014f);
                ShapeCount = 3; Mass = 12f; Friction = 0.6f; CharLength = 0.5f; ThumbOppositionDeg = 20f; FingerSpreadDeg = 0f;
                CenterOfMassLocal = new Vector3(0.06f, 0f, 0f);
                _sites[0] = new GraspSite(new Vector3(-0.5f, 0.06f, -0.09f), up, bx); _sites[1] = new GraspSite(new Vector3(0.5f, 0.06f, -0.09f), up, ax);
                _sites[2] = new GraspSite(new Vector3(-0.5f, 0.06f, 0.09f), up, bx); _sites[3] = new GraspSite(new Vector3(0.5f, 0.06f, 0.09f), up, ax);
                SiteCount = 4;
                break;
            default:
                _shapes[0] = new PropShape(SdfPrimitive.Capsule, new Vector3(0, 0, -0.1f), new Vector3(0, 0, 0.1f), 0.032f);
                ShapeCount = 1; Mass = 0.6f; Friction = 0.6f; CharLength = 0.1f; ThumbOppositionDeg = 70f; FingerSpreadDeg = 0f;
                _sites[0] = new GraspSite(new Vector3(0, 0, -0.055f), ax, up); _sites[1] = new GraspSite(new Vector3(0, 0, 0.055f), bx, up); SiteCount = 2;
                break;
        }
    }

    public float Sdf(Vector3 local)
    {
        var d = float.MaxValue;
        for (var i = 0; i < ShapeCount; i++) d = MathF.Min(d, VirtualPropContactGraspingSynthesizer.ShapeDistance(_shapes[i], local));
        return d;
    }

    /// <summary>로컬 SDF 바깥 법선(중앙 차분).</summary>
    public Vector3 Normal(Vector3 local)
    {
        const float e = 1e-4f;
        var n = new Vector3(Sdf(local + new Vector3(e, 0, 0)) - Sdf(local - new Vector3(e, 0, 0)),
                            Sdf(local + new Vector3(0, e, 0)) - Sdf(local - new Vector3(0, e, 0)),
                            Sdf(local + new Vector3(0, 0, e)) - Sdf(local - new Vector3(0, 0, e)));
        var l = n.Length();
        return l > 1e-12f ? n / l : Vector3.UnitY;
    }

    public Vector3 ToLocal(Vector3 w) => Vector3.Transform(w - Position, Quaternion.Conjugate(Rotation));
    public Vector3 ToWorld(Vector3 l) => Position + Vector3.Transform(l, Rotation);
    public Vector3 DirToWorld(Vector3 l) => Vector3.Transform(l, Rotation);
    public Vector3 DirToLocal(Vector3 w) => Vector3.Transform(w, Quaternion.Conjugate(Rotation));
    public Vector3 CenterOfMass => ToWorld(CenterOfMassLocal);
}

/// <summary>
/// [PromptFlow Studio v17.36.0] 🤲 실시간 3D 접촉 다양체 덱스그랩 파지 합성 엔진(<see cref="Synthesize"/> · <see cref="Finalize"/> 무할당).
///   ▸ 손 모델: 5 손가락 × 4 자유도 = 20 관절(검지~소지 MCP 굴곡 · MCP 외전 · PIP · DIP / 엄지 CMC 굴곡 · CMC 대립 · MCP · IP) · v17.13 21점 토폴로지(<see cref="HandTopology"/>) 순서 출력 ·
///     지골 캡슐 반경 8.5/9.5 mm · 손바닥 라운드 박스 · 좌/우 측방 거울.
///   ▸ 파지 템플릿(형태별): 사이트 표면점 = 접근선 SDF 이분 탐색 → 손바닥 하면 정렬(손등 = 접근 · 손가락 = 힌트 직교화) · 구 = 구형(손가락 부채 + 대립 55°) · 원통/손잡이 = 원통 감싸기(70/75°) ·
///     박스 = 면 감싸기(60°) · 상자 = 갈고리(손잡이 바 · 20°).
///   ▸ 접촉 닫힘: 손가락별 근위 → 원위 순차(MCP → PIP → DIP) · 관절 범위 이분 14회로 첫 접촉각(캡슐 3 샘플 × SDF − 반경 = 0) · 원위 지골은 근위 접촉 후 계속 감싸기.
///   ▸ 접촉 다양체: 지골/손바닥 여유 ≤ 1.5 mm → 표면 투영점 · 바깥 법선(SDF 기울기) · 최대 16.
///   ▸ 힘 폐쇄: 쿨롱 원뿔 8 모서리 선형화 → 6D 렌치 [f ; (p − c) × f / L] ·
///     Ferrari–Canny ε(44 고정 방향 지지 함수 최소 = 상한 근사) &gt; 0 ∧ 원점 ↔ 렌치 볼록 껍질 거리(Wolfe 최소 노름점 · 유한 종료) &lt; 0.03 → 힘 폐쇄.
///   ▸ 출력 합성: 각 = 닫힘 블렌드 × 접촉각 + 파지력 압착(가드) − 관통 후퇴(가드) · 엄지는 대립각 포함 → 21점 월드 · 손가락별 관통 측정.
/// </summary>
public sealed class RealtimeDexterousGraspSynthesisEngine
{
    public const int MaxSlots = 4, Dof = 20, Pts = HandTopology.Points, MaxContacts = 16, Cone = 8, Dirs = 44, MnpIter = 64, Bisect = 14;
    public const float HullTol = 0.03f, FingerRadius = 0.0085f, ThumbRadius = 0.0095f, ContactTol = 0.0015f, PalmX = 0.045f, PalmY = -0.004f, PalmHalfX = 0.045f, PalmHalfY = 0.009f, PalmHalfZ = 0.04f;
    private const float Deg = MathF.PI / 180f;

    private static readonly Vector3[] Base = { new(0.022f, -0.003f, 0.028f), new(0.085f, 0, 0.022f), new(0.09f, 0, 0.006f), new(0.085f, 0, -0.01f), new(0.075f, 0, -0.025f) };
    private static readonly float[] Seg = { 0.042f, 0.032f, 0.026f, 0.044f, 0.026f, 0.020f, 0.047f, 0.029f, 0.021f, 0.044f, 0.027f, 0.020f, 0.035f, 0.021f, 0.018f };
    private static readonly float[] RestFan = { 0f, 6f, 0f, -6f, -12f };
    private static readonly float[] Lo = { -10f, 0f, 0f, -10f, 0f, -15f, 0f, 0f };    // [엄지 4 | 손가락 4]
    private static readonly float[] Hi = { 45f, 85f, 60f, 80f, 90f, 15f, 105f, 80f };
    private static readonly float[] SqueezeW = { 0.4f, 0f, 0.35f, 0.25f }, ThumbW = { 0.3f, 0.3f, 0.2f, 0.2f };
    private static readonly int[] Stage = { 0, 2, 3 };
    private static readonly float[] Dir6 = BuildDirs();

    private readonly float[] _q = new float[MaxSlots * Dof], _blend = new float[MaxSlots], _squeeze = new float[MaxSlots * 5], _retreat = new float[MaxSlots * 5];
    private readonly Vector3[] _wristL = new Vector3[MaxSlots], _slip = new Vector3[MaxSlots], _pts = new Vector3[MaxSlots * Pts];
    private readonly Quaternion[] _rotL = new Quaternion[MaxSlots];
    private readonly float[] _side = { -1f, -1f, 1f, 1f };
    private readonly Vector3[] _cp = new Vector3[MaxSlots * MaxContacts], _cn = new Vector3[MaxSlots * MaxContacts];
    private readonly byte[] _cf = new byte[MaxSlots * MaxContacts], _cs = new byte[MaxSlots * MaxContacts];
    private readonly int[] _nc = new int[MaxSlots], _site = new int[MaxSlots];
    private readonly bool[] _fc = new bool[MaxSlots], _active = new bool[MaxSlots];
    private readonly float[] _eps = new float[MaxSlots], _hull = new float[MaxSlots], _pen = new float[MaxSlots], _penF = new float[MaxSlots * 5];
    private readonly float[] _w = new float[MaxContacts * Cone * 6];
    private readonly int[] _cacheVer = { -1, -1, -1, -1 };
    private readonly Vector3[] _cacheSlip = new Vector3[MaxSlots];

    public bool Active(int slot) => _active[slot];
    public void SetActive(int slot, bool on) => _active[slot] = on;
    public bool IsRightHand(int slot) => _side[slot] < 0f;
    public int SiteOf(int slot) => _site[slot];
    public int ContactCount(int slot) => _nc[slot];
    public Vector3 ContactPoint(int slot, int i) => _cp[slot * MaxContacts + i];
    public Vector3 ContactNormal(int slot, int i) => _cn[slot * MaxContacts + i];
    /// <summary>접촉 손가락(0 엄지 ~ 4 소지 · 5 손바닥).</summary>
    public int ContactFinger(int slot, int i) => _cf[slot * MaxContacts + i];
    public bool ForceClosure(int slot) => _fc[slot];
    public float Epsilon(int slot) => _eps[slot];
    public float HullDistance(int slot) => _hull[slot];
    public float CloseBlend(int slot) => _blend[slot];
    public void SetCloseBlend(int slot, float w) => _blend[slot] = Math.Clamp(w, 0f, 1f);
    public void SetSqueeze(int slot, int finger, float deg) => _squeeze[slot * 5 + finger] = MathF.Max(0f, deg);
    public void SetRetreat(int slot, int finger, float deg) => _retreat[slot * 5 + finger] = MathF.Max(0f, deg);
    public float Squeeze(int slot, int finger) => _squeeze[slot * 5 + finger];
    public float Retreat(int slot, int finger) => _retreat[slot * 5 + finger];
    /// <summary>미끄럼 변위(소품 로컬 · 손이 소품 대비 이동).</summary>
    public void SetSlipOffset(int slot, Vector3 local) => _slip[slot] = local;
    public float ContactAngle(int slot, int finger, int joint) => _q[slot * Dof + finger * 4 + joint];
    public float FinalAngle(int slot, int finger, int joint) => Angle(slot, finger, joint);
    public Vector3 Point(int slot, int k) => _pts[slot * Pts + k];
    public float PenetrationMm(int slot) => _pen[slot] * 1000f;
    public float FingerPenetrationMm(int slot, int finger) => _penF[slot * 5 + finger] * 1000f;
    public double LastSynthUs { get; private set; }
    public double MaxSynthUs { get; private set; }
    public long Syntheses { get; private set; }
    public long CacheHits { get; private set; }
    public void Invalidate(int slot) => _cacheVer[slot] = -1;
    public static float JointLo(int finger, int j) => Lo[(finger == 0 ? 0 : 4) + j];
    public static float JointHi(int finger, int j) => Hi[(finger == 0 ? 0 : 4) + j];

    public void ResetMetrics() { MaxSynthUs = 0; Syntheses = CacheHits = 0; }

    private static float[] BuildDirs()
    {
        var d = new float[Dirs * 6];
        for (var i = 0; i < 12; i++) d[i * 6 + i / 2] = (i & 1) == 0 ? 1f : -1f;
        uint s = 0x9E3779B9u;
        for (var i = 12; i < Dirs; i++)
        {
            var l = 0f;
            for (var k = 0; k < 6; k++) { s = s * 1664525u + 1013904223u; var v = (s >> 8) / 16777216f * 2f - 1f; d[i * 6 + k] = v; l += v * v; }
            l = MathF.Sqrt(MathF.Max(l, 1e-9f));
            for (var k = 0; k < 6; k++) d[i * 6 + k] /= l;
        }
        return d;
    }

    // ══════════════════════ 손 기구학(손 로컬: x 손가락 · −y 손바닥 법선 · z 측방 × 좌우 부호) ══════════════════════

    private float Angle(int slot, int f, int j)
    {
        var o = slot * Dof + f * 4 + j;
        var b = _blend[slot];
        var rest = j == 1 && f == 0 ? 25f : 0f;
        var a = rest + (_q[o] - rest) * b;
        a += (_squeeze[slot * 5 + f] - _retreat[slot * 5 + f]) * (f == 0 ? ThumbW[j] : SqueezeW[j]);
        return Math.Clamp(a, Lo[(f == 0 ? 0 : 4) + j], Hi[(f == 0 ? 0 : 4) + j] + 25f);
    }

    /// <summary>손가락 f 4점(기저 · 3 관절 끝) 손 로컬 — 각(°) q0 · q1 · q2 · q3.</summary>
    private static void FingerFk(int f, float s, float q0, float q1, float q2, float q3, Span<Vector3> p)
    {
        var b = Base[f];
        p[0] = new Vector3(b.X, b.Y, b.Z * s);
        Vector3 e1, e2;
        if (f == 0)
        {
            var dt = new Vector3(0.55f, 0f, 0.835f * s);
            var g = q1 * Deg * s;
            float c = MathF.Cos(g), sn = MathF.Sin(g);
            e1 = new Vector3(dt.X, -dt.Z * sn, dt.Z * c);
            e2 = new Vector3(0f, -c, -sn);
            e1 = Vector3.Normalize(e1);
        }
        else
        {
            var a = (RestFan[f] + q1) * Deg;
            e1 = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a) * s);
            e2 = -Vector3.UnitY;
        }
        var phi = q0 * Deg;
        p[1] = p[0] + (MathF.Cos(phi) * e1 + MathF.Sin(phi) * e2) * Seg[f * 3];
        phi += q2 * Deg;
        p[2] = p[1] + (MathF.Cos(phi) * e1 + MathF.Sin(phi) * e2) * Seg[f * 3 + 1];
        phi += q3 * Deg;
        p[3] = p[2] + (MathF.Cos(phi) * e1 + MathF.Sin(phi) * e2) * Seg[f * 3 + 2];
    }

    private Vector3 HandToProp(int slot, Vector3 h) => _wristL[slot] + _slip[slot] + Vector3.Transform(h, _rotL[slot]);

    /// <summary>지골 seg(0~2) 최소 여유(SDF − 반경) · 최소점(소품 로컬).</summary>
    private float SegClear(int slot, DexProp prop, ReadOnlySpan<Vector3> p, int seg, float r, out Vector3 at)
    {
        var a = HandToProp(slot, p[seg]); var b = HandToProp(slot, p[seg + 1]);
        var best = float.MaxValue; at = b;
        for (var k = 1; k <= 3; k++)
        {
            var x = a + (b - a) * (k / 3f);
            var d = prop.Sdf(x) - r;
            if (d < best) { best = d; at = x; }
        }
        return best;
    }

    private float ChainClear(int slot, DexProp prop, int f, float s, Span<float> q, int fromSeg, Span<Vector3> p)
    {
        FingerFk(f, s, q[0], q[1], q[2], q[3], p);
        var r = f == 0 ? ThumbRadius : FingerRadius;
        var m = float.MaxValue;
        for (var sg = fromSeg; sg < 3; sg++) m = MathF.Min(m, SegClear(slot, prop, p, sg, r, out _));
        return m;
    }

    // ══════════════════════ 합성 ══════════════════════

    /// <summary>파지 템플릿 → 손목 자세(소품 로컬) · 순차 접촉 닫힘 · 접촉 다양체 · 힘 폐쇄(무할당).</summary>
    public void Synthesize(int slot, DexProp prop, int site, bool rightHand)
    {
        // 소품 로컬 파지는 소품 강체 운동에 불변 → 형상 · 사이트 · 손 · 미끄럼 오프셋 동일 시 재사용(닫힘 블렌드만 갱신)
        if (_cacheVer[slot] == prop.Version && _site[slot] == site && (_side[slot] < 0f) == rightHand && _cacheSlip[slot] == _slip[slot]) { _blend[slot] = 1f; CacheHits++; return; }
        _cacheVer[slot] = prop.Version; _cacheSlip[slot] = _slip[slot];
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _site[slot] = site; _side[slot] = rightHand ? -1f : 1f;
        var s = _side[slot];
        var g = prop.Site(site);
        // 표면점: 접근선 SDF 이분
        float lo = 0f, hi = 0.6f;
        for (var i = 0; i < 24; i++) { var m = 0.5f * (lo + hi); if (prop.Sdf(g.Center + g.Approach * m) < 0f) lo = m; else hi = m; }
        var surf = g.Center + g.Approach * hi;
        var y = Vector3.Normalize(g.Approach);
        var x = g.FingerDir - y * Vector3.Dot(g.FingerDir, y);
        x = x.LengthSquared() > 1e-8f ? Vector3.Normalize(x) : Vector3.Normalize(Vector3.Cross(y, Vector3.UnitX));
        var z = Vector3.Cross(x, y);
        var rot = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        _rotL[slot] = rot;
        _wristL[slot] = surf - Vector3.Transform(new Vector3(PalmX, PalmY - PalmHalfY - 0.0005f, 0f), rot);

        Span<Vector3> p = stackalloc Vector3[4];
        Span<float> q = stackalloc float[4];
        var o = slot * Dof;
        for (var f = 0; f < 5; f++)
        {
            q[0] = f == 0 ? 0f : 0f;
            q[1] = f == 0 ? prop.ThumbOppositionDeg : prop.FingerSpreadDeg * (f - 2.5f) / 1.5f;
            q[2] = 0f; q[3] = 0f;
            var bo = f == 0 ? 0 : 4;
            // 초기 관통 해소: 엄지 = 대립각 이분 축소(0 까지) → CMC 신전 · 손가락 = MCP 신전 하한
            if (ChainClear(slot, prop, f, s, q, 0, p) <= 0f)
            {
                if (f == 0)
                {
                    float ol = 0f, oh = q[1];
                    q[1] = 0f;
                    if (ChainClear(slot, prop, f, s, q, 0, p) > 0f)
                    {
                        for (var it = 0; it < Bisect; it++) { q[1] = 0.5f * (ol + oh); if (ChainClear(slot, prop, f, s, q, 0, p) > 0f) ol = q[1]; else oh = q[1]; }
                        q[1] = ol;
                    }
                }
                if (ChainClear(slot, prop, f, s, q, 0, p) <= 0f) q[0] = Lo[bo];
            }
            foreach (var j in Stage)
            {
                var fromSeg = j == 0 ? 0 : j - 1;
                var a0 = q[j];
                if (ChainClear(slot, prop, f, s, q, fromSeg, p) <= 0f) continue;   // 이미 접촉
                q[j] = Hi[bo + j];
                if (ChainClear(slot, prop, f, s, q, fromSeg, p) > 0f) continue;   // 범위 내 미접촉 = 최대 굴곡
                float l = a0, h = Hi[bo + j];
                for (var it = 0; it < Bisect; it++)
                {
                    var m = 0.5f * (l + h);
                    q[j] = m;
                    if (ChainClear(slot, prop, f, s, q, fromSeg, p) > 0f) l = m; else h = m;
                }
                q[j] = l;
            }
            for (var j = 0; j < 4; j++) _q[o + f * 4 + j] = q[j];
        }
        _blend[slot] = 1f;
        BuildContacts(slot, prop);
        ForceClosureTest(slot, prop);
        Syntheses++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastSynthUs = us; if (us > MaxSynthUs) MaxSynthUs = us;
    }

    private void BuildContacts(int slot, DexProp prop)
    {
        var s = _side[slot];
        Span<Vector3> p = stackalloc Vector3[4];
        var n = 0; var b = slot * MaxContacts;
        for (var f = 0; f < 5 && n < MaxContacts; f++)
        {
            var o = slot * Dof + f * 4;
            FingerFk(f, s, _q[o], _q[o + 1], _q[o + 2], _q[o + 3], p);
            var r = f == 0 ? ThumbRadius : FingerRadius;
            for (var sg = 0; sg < 3 && n < MaxContacts; sg++)
            {
                if (SegClear(slot, prop, p, sg, r, out var at) > ContactTol) continue;
                var nl = prop.Normal(at);
                var sp = at - nl * prop.Sdf(at);
                _cp[b + n] = prop.ToWorld(sp); _cn[b + n] = prop.DirToWorld(nl); _cf[b + n] = (byte)f; _cs[b + n] = (byte)sg; n++;
            }
        }
        // 손바닥 하면 5 샘플(중심 · 내측 모서리) — 최소 여유점 1개
        var pb = float.MaxValue; var pa = Vector3.Zero;
        for (var k = 0; k < 5; k++)
        {
            var dx = k == 0 ? 0f : (k & 1) == 0 ? 0.7f : -0.7f; var dz = k == 0 ? 0f : k < 3 ? 0.7f : -0.7f;
            var h = new Vector3(PalmX + dx * PalmHalfX, PalmY - PalmHalfY, dz * PalmHalfZ * s);
            var l = HandToProp(slot, h);
            var d = prop.Sdf(l);
            if (d < pb) { pb = d; pa = l; }
        }
        if (pb <= ContactTol && n < MaxContacts)
        {
            var nl = prop.Normal(pa);
            _cp[b + n] = prop.ToWorld(pa - nl * prop.Sdf(pa)); _cn[b + n] = prop.DirToWorld(nl); _cf[b + n] = 5; _cs[b + n] = 0; n++;
        }
        _nc[slot] = n;
    }

    /// <summary>원뿔 8 모서리 6D 렌치 → ε(44 방향 지지 최소) · Frank–Wolfe 볼록 껍질 거리.</summary>
    private void ForceClosureTest(int slot, DexProp prop)
    {
        var n = _nc[slot]; var b = slot * MaxContacts;
        var com = prop.CenterOfMass; var mu = prop.Friction; var invL = 1f / prop.CharLength;
        var m = 0;
        for (var i = 0; i < n; i++)
        {
            var ni = -_cn[b + i];
            var t1 = Vector3.Cross(ni, MathF.Abs(ni.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX); t1 = Vector3.Normalize(t1);
            var t2 = Vector3.Cross(ni, t1);
            var r = _cp[b + i] - com;
            for (var k = 0; k < Cone; k++)
            {
                var a = k * (MathF.Tau / Cone);
                var e = Vector3.Normalize(ni + mu * (MathF.Cos(a) * t1 + MathF.Sin(a) * t2));
                var tq = Vector3.Cross(r, e) * invL;
                var o = m * 6;
                _w[o] = e.X; _w[o + 1] = e.Y; _w[o + 2] = e.Z; _w[o + 3] = tq.X; _w[o + 4] = tq.Y; _w[o + 5] = tq.Z;
                m++;
            }
        }
        if (m == 0) { _eps[slot] = -1f; _hull[slot] = 1f; _fc[slot] = false; return; }
        var eps = float.MaxValue;
        for (var d = 0; d < Dirs; d++)
        {
            var best = float.MinValue;
            for (var i = 0; i < m; i++)
            {
                var o = i * 6; var v = 0f;
                for (var k = 0; k < 6; k++) v += Dir6[d * 6 + k] * _w[o + k];
                if (v > best) best = v;
            }
            if (best < eps) eps = best;
        }
        // Wolfe 최소 노름점(유한 종료 · 코랄 ≤ 7): 원점 ↔ 렌치 볼록 껍질 거리
        Span<float> xv = stackalloc float[6];
        _hull[slot] = MinNormPoint(m, xv);
        _eps[slot] = eps;
        _fc[slot] = eps > 0f && _hull[slot] < HullTol;
    }

    private float Dot6(int i, int j) { var v = 0f; for (var k = 0; k < 6; k++) v += _w[i * 6 + k] * _w[j * 6 + k]; return v; }

    /// <summary>Wolfe(1976) 최소 노름점 — 렌치 m 개 볼록 껍질 · 코랄 아핀 최소화(소형 선형계 가우스 소거) · 무할당.</summary>
    private float MinNormPoint(int m, Span<float> x)
    {
        Span<int> set = stackalloc int[8];
        Span<float> lam = stackalloc float[8], alpha = stackalloc float[8], a = stackalloc float[81];
        var best = 0; var bn = float.MaxValue;
        for (var i = 0; i < m; i++) { var nn = Dot6(i, i); if (nn < bn) { bn = nn; best = i; } }
        var n = 1; set[0] = best; lam[0] = 1f;
        var scale = 0f;
        for (var i = 0; i < m; i++) scale = MathF.Max(scale, Dot6(i, i));
        for (var it = 0; it < MnpIter; it++)
        {
            x.Clear();
            for (var i = 0; i < n; i++) for (var k = 0; k < 6; k++) x[k] += lam[i] * _w[set[i] * 6 + k];
            var xx = 0f; for (var k = 0; k < 6; k++) xx += x[k] * x[k];
            // 주 루프: 최소 지지점
            var j = -1; var jv = float.MaxValue;
            for (var i = 0; i < m; i++) { var v = 0f; for (var k = 0; k < 6; k++) v += _w[i * 6 + k] * x[k]; if (v < jv) { jv = v; j = i; } }
            if (xx - jv <= 1e-7f * scale || n >= 7) break;
            var dup = false; for (var i = 0; i < n; i++) if (set[i] == j) dup = true;
            if (dup) break;
            set[n] = j; lam[n] = 0f; n++;
            // 부 루프
            for (var inner = 0; inner < 16; inner++)
            {
                // [G 1; 1ᵀ 0][α; μ] = [0; 1]
                var d = n + 1;
                for (var r = 0; r < d; r++) for (var c = 0; c <= d; c++) a[r * 9 + c] = 0f;
                for (var r = 0; r < n; r++)
                {
                    for (var c = 0; c < n; c++) a[r * 9 + c] = Dot6(set[r], set[c]);
                    a[r * 9 + n] = 1f; a[n * 9 + r] = 1f;
                }
                a[n * 9 + d] = 1f;
                if (!Solve(a, d, alpha)) { n--; break; }
                var allPos = true;
                for (var i = 0; i < n; i++) if (alpha[i] <= 1e-9f) { allPos = false; break; }
                if (allPos) { for (var i = 0; i < n; i++) lam[i] = alpha[i]; break; }
                var theta = 1f;
                for (var i = 0; i < n; i++) if (alpha[i] <= 1e-9f) { var den = lam[i] - alpha[i]; if (den > 1e-12f) theta = MathF.Min(theta, lam[i] / den); }
                var w = 0;
                for (var i = 0; i < n; i++)
                {
                    var l = theta * alpha[i] + (1f - theta) * lam[i];
                    if (l > 1e-8f) { set[w] = set[i]; lam[w] = l; w++; }
                }
                n = w;
                if (n <= 1) { lam[0] = 1f; n = Math.Max(n, 1); break; }
            }
        }
        x.Clear();
        for (var i = 0; i < n; i++) for (var k = 0; k < 6; k++) x[k] += lam[i] * _w[set[i] * 6 + k];
        var dd = 0f; for (var k = 0; k < 6; k++) dd += x[k] * x[k];
        return MathF.Sqrt(dd);
    }

    /// <summary>d×d 확대 행렬(행 간격 9) 부분 피벗 가우스 소거 → sol(무할당).</summary>
    private static bool Solve(Span<float> a, int d, Span<float> sol)
    {
        for (var c = 0; c < d; c++)
        {
            var piv = c; var pv = MathF.Abs(a[c * 9 + c]);
            for (var r = c + 1; r < d; r++) { var v = MathF.Abs(a[r * 9 + c]); if (v > pv) { pv = v; piv = r; } }
            if (pv < 1e-10f) return false;
            if (piv != c) for (var k = 0; k <= d; k++) { var t = a[c * 9 + k]; a[c * 9 + k] = a[piv * 9 + k]; a[piv * 9 + k] = t; }
            for (var r = 0; r < d; r++)
            {
                if (r == c) continue;
                var f = a[r * 9 + c] / a[c * 9 + c];
                if (f == 0f) continue;
                for (var k = c; k <= d; k++) a[r * 9 + k] -= f * a[c * 9 + k];
            }
        }
        for (var r = 0; r < d; r++) sol[r] = a[r * 9 + d] / a[r * 9 + r];
        return true;
    }

    // ══════════════════════ 출력 ══════════════════════

    /// <summary>손목 월드 자세(템플릿 · 미끄럼 포함).</summary>
    public void WristWorld(int slot, DexProp prop, out Vector3 pos, out Quaternion rot)
    {
        pos = prop.ToWorld(_wristL[slot] + _slip[slot]);
        rot = Quaternion.Normalize(prop.Rotation * _rotL[slot]);
    }

    /// <summary>손가락 f 현재 합성 각(블렌드 · 압착 · 후퇴) 기준 관통 깊이(m · ≥ 0).</summary>
    public float FingerPenetration(int slot, DexProp prop, int f)
    {
        Span<Vector3> p = stackalloc Vector3[4];
        FingerFk(f, _side[slot], Angle(slot, f, 0), Angle(slot, f, 1), Angle(slot, f, 2), Angle(slot, f, 3), p);
        var r = f == 0 ? ThumbRadius : FingerRadius;
        var m = float.MaxValue;
        for (var sg = 0; sg < 3; sg++) m = MathF.Min(m, SegClear(slot, prop, p, sg, r, out _));
        return MathF.Max(0f, -m);
    }

    /// <summary>손가락 끝(소품 로컬) — 터널링 검사.</summary>
    public Vector3 TipLocal(int slot, int f)
    {
        Span<Vector3> p = stackalloc Vector3[4];
        FingerFk(f, _side[slot], Angle(slot, f, 0), Angle(slot, f, 1), Angle(slot, f, 2), Angle(slot, f, 3), p);
        return HandToProp(slot, p[3]);
    }

    /// <summary>최종 21점 월드 · 손가락별 관통(무할당).</summary>
    public void Finalize(int slot, DexProp prop)
    {
        Span<Vector3> p = stackalloc Vector3[4];
        var o = slot * Pts;
        _pts[o] = prop.ToWorld(HandToProp(slot, Vector3.Zero));
        var pen = 0f;
        for (var f = 0; f < 5; f++)
        {
            FingerFk(f, _side[slot], Angle(slot, f, 0), Angle(slot, f, 1), Angle(slot, f, 2), Angle(slot, f, 3), p);
            for (var k = 0; k < 4; k++) _pts[o + HandTopology.Base(f) + k] = prop.ToWorld(HandToProp(slot, p[k]));
            var r = f == 0 ? ThumbRadius : FingerRadius;
            var m = float.MaxValue;
            for (var sg = 0; sg < 3; sg++) m = MathF.Min(m, SegClear(slot, prop, p, sg, r, out _));
            _penF[slot * 5 + f] = MathF.Max(0f, -m);
            pen = MathF.Max(pen, _penF[slot * 5 + f]);
        }
        _pen[slot] = pen;
    }
}
