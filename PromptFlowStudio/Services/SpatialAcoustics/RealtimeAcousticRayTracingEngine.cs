using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.SpatialAcoustics;

/// <summary>[PromptFlow Studio v17.23.0] 음향 표면 재질.</summary>
public enum AcousticMaterial : byte { Concrete, Wood, Fabric, Glass, AcousticPanel, Plaster }

/// <summary>[PromptFlow Studio v17.23.0] 재질 교체 단위 표면 그룹 — 삼각형은 그룹 인덱스만 보유하므로 재질 교체에 BVH 재구축이 없다.</summary>
public enum AcousticSurface : byte { Floor, Ceiling, BackWall, WindowWall, SideWall, FrontWall, Pillars, Crates }

/// <summary>[PromptFlow Studio v17.23.0] 옥타브 8대역(63 Hz–8 kHz) 흡음률 α · 산란 계수 · 공기 흡수(문헌 대표값 근사).</summary>
public static class AcousticMaterials
{
    public const int Bands = 8, Count = 6, Surfaces = 8;

    public static readonly float[] CenterHz = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };

    private static readonly float[] AlphaTable =
    {
        0.01f, 0.01f, 0.01f, 0.02f, 0.02f, 0.02f, 0.03f, 0.04f,    // 콘크리트(도장)
        0.30f, 0.28f, 0.22f, 0.17f, 0.09f, 0.10f, 0.11f, 0.12f,    // 나무 패널
        0.05f, 0.07f, 0.31f, 0.49f, 0.75f, 0.70f, 0.60f, 0.55f,    // 두꺼운 커튼
        0.40f, 0.35f, 0.25f, 0.18f, 0.12f, 0.07f, 0.04f, 0.03f,    // 창 유리
        0.15f, 0.25f, 0.70f, 0.95f, 0.98f, 0.95f, 0.90f, 0.85f,    // 50 mm 흡음 패널
        0.02f, 0.013f, 0.015f, 0.02f, 0.03f, 0.04f, 0.05f, 0.06f,  // 석고
    };

    private static readonly float[] ScatterTable = { 0.10f, 0.15f, 0.40f, 0.05f, 0.30f, 0.10f };
    private static readonly float[] ReflectTable = BuildReflect();

    /// <summary>공기 흡수 에너지 감쇠 계수 m(1/m · 20 °C · 50 %RH 근사).</summary>
    public static readonly float[] AirAbsorption = { 0f, 0.0001f, 0.0003f, 0.0006f, 0.0011f, 0.0024f, 0.0076f, 0.025f };

    /// <summary>대역별 1(감쇠 없음) — 반사율만 곱할 때 공기 항 자리표시.</summary>
    public static readonly float[] Ones = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

    private static float[] BuildReflect()
    {
        var r = new float[AlphaTable.Length];
        for (var i = 0; i < r.Length; i++) r[i] = 1f - AlphaTable[i];
        return r;
    }

    public static float Alpha(AcousticMaterial m, int band) => AlphaTable[(int)m * Bands + band];
    public static ReadOnlySpan<float> Reflectance(AcousticMaterial m) => ReflectTable.AsSpan((int)m * Bands, Bands);
    public static float Scattering(AcousticMaterial m) => ScatterTable[(int)m];

    public static string Name(AcousticMaterial m) => m switch
    {
        AcousticMaterial.Concrete => "콘크리트",
        AcousticMaterial.Wood => "나무",
        AcousticMaterial.Fabric => "천(커튼)",
        AcousticMaterial.Glass => "유리",
        AcousticMaterial.AcousticPanel => "흡음 패널",
        _ => "석고",
    };

    public static string SurfaceName(AcousticSurface s) => s switch
    {
        AcousticSurface.Floor => "바닥",
        AcousticSurface.Ceiling => "천장",
        AcousticSurface.BackWall => "뒷벽",
        AcousticSurface.WindowWall => "창 벽",
        AcousticSurface.SideWall => "측벽",
        AcousticSurface.FrontWall => "전면 벽",
        AcousticSurface.Pillars => "기둥",
        _ => "상자",
    };
}

/// <summary>
/// [PromptFlow Studio v17.23.0] 음향 SIMD 커널(스칼라 쌍둥이와 비트 동일 · FMA 축약 없음).
///   ▸ 대역 감쇠: 8대역 = Vector256 1개(→ Vector128 2개 → 스칼라).
///   ▸ 64탭 내적: 레인 j 가 (k = j, j+8, …) 곱을 순서대로 누적하는 8개 부분합 → 고정 순서 축약 — 모든 경로가 같은 덧셈 순서.
///   ▸ 슬랩(광선-AABB): x/y/z/패드 4레인, min/max 는 (a &lt; b ? a : b) 의미(minps/maxps)와 동일하게 스칼라 정의.
/// </summary>
public static class AcousticKernels
{
    public const int Taps = 64;

    public static void AttenuateScalar(Span<float> e, ReadOnlySpan<float> refl, ReadOnlySpan<float> air)
    {
        Check(e, refl, air);
        for (var i = 0; i < AcousticMaterials.Bands; i++) e[i] = e[i] * refl[i] * air[i];
    }

    public static void AttenuateSimd(Span<float> e, ReadOnlySpan<float> refl, ReadOnlySpan<float> air)
    {
        Check(e, refl, air);
        ref var re = ref MemoryMarshal.GetReference(e);
        ref var rr = ref MemoryMarshal.GetReference(refl);
        ref var ra = ref MemoryMarshal.GetReference(air);
        if (Vector256.IsHardwareAccelerated)
        {
            (Vector256.LoadUnsafe(ref re) * Vector256.LoadUnsafe(ref rr) * Vector256.LoadUnsafe(ref ra)).StoreUnsafe(ref re);
            return;
        }
        if (Vector128.IsHardwareAccelerated)
        {
            (Vector128.LoadUnsafe(ref re) * Vector128.LoadUnsafe(ref rr) * Vector128.LoadUnsafe(ref ra)).StoreUnsafe(ref re);
            (Vector128.LoadUnsafe(ref re, 4) * Vector128.LoadUnsafe(ref rr, 4) * Vector128.LoadUnsafe(ref ra, 4)).StoreUnsafe(ref re, 4);
            return;
        }
        for (var i = 0; i < AcousticMaterials.Bands; i++) e[i] = e[i] * refl[i] * air[i];
    }

    private static void Check(Span<float> e, ReadOnlySpan<float> refl, ReadOnlySpan<float> air)
    {
        if (e.Length < AcousticMaterials.Bands || refl.Length < AcousticMaterials.Bands || air.Length < AcousticMaterials.Bands)
            throw new ArgumentException("대역 버퍼는 8 이상이어야 합니다.");
    }

    public static float Dot64Scalar(ReadOnlySpan<float> x, ReadOnlySpan<float> h)
    {
        if (x.Length < Taps || h.Length < Taps) throw new ArgumentException("64탭 이상이어야 합니다.");
        float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f, a4 = 0f, a5 = 0f, a6 = 0f, a7 = 0f;
        for (var k = 0; k < Taps; k += 8)
        {
            a0 += x[k] * h[k]; a1 += x[k + 1] * h[k + 1]; a2 += x[k + 2] * h[k + 2]; a3 += x[k + 3] * h[k + 3];
            a4 += x[k + 4] * h[k + 4]; a5 += x[k + 5] * h[k + 5]; a6 += x[k + 6] * h[k + 6]; a7 += x[k + 7] * h[k + 7];
        }
        return Reduce(a0, a1, a2, a3, a4, a5, a6, a7);
    }

    public static float Dot64Simd(ReadOnlySpan<float> x, ReadOnlySpan<float> h)
    {
        if (x.Length < Taps || h.Length < Taps) throw new ArgumentException("64탭 이상이어야 합니다.");
        ref var rx = ref MemoryMarshal.GetReference(x);
        ref var rh = ref MemoryMarshal.GetReference(h);
        if (Vector256.IsHardwareAccelerated)
        {
            var acc = Vector256<float>.Zero;
            for (nuint k = 0; k < Taps; k += 8) acc += Vector256.LoadUnsafe(ref rx, k) * Vector256.LoadUnsafe(ref rh, k);
            return Reduce(acc.GetElement(0), acc.GetElement(1), acc.GetElement(2), acc.GetElement(3), acc.GetElement(4), acc.GetElement(5), acc.GetElement(6), acc.GetElement(7));
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var lo = Vector128<float>.Zero; var hi = Vector128<float>.Zero;
            for (nuint k = 0; k < Taps; k += 8)
            {
                lo += Vector128.LoadUnsafe(ref rx, k) * Vector128.LoadUnsafe(ref rh, k);
                hi += Vector128.LoadUnsafe(ref rx, k + 4) * Vector128.LoadUnsafe(ref rh, k + 4);
            }
            return Reduce(lo.GetElement(0), lo.GetElement(1), lo.GetElement(2), lo.GetElement(3), hi.GetElement(0), hi.GetElement(1), hi.GetElement(2), hi.GetElement(3));
        }
        return Dot64Scalar(x, h);
    }

    private static float Reduce(float a0, float a1, float a2, float a3, float a4, float a5, float a6, float a7) => ((a0 + a1) + (a2 + a3)) + ((a4 + a5) + (a6 + a7));

    private static float Mn(float a, float b) => a < b ? a : b;
    private static float Mx(float a, float b) => a > b ? a : b;

    /// <summary>광선-AABB 슬랩(SIMD). box = (min x,y,z,−1e30) · (max x,y,z,+1e30), o = (o, 0), inv = (1/d, 1).</summary>
    public static bool SlabSimd(Vector128<float> mn, Vector128<float> mx, Vector128<float> o, Vector128<float> inv, float tMax, out float tNear)
    {
        var t0 = (mn - o) * inv;
        var t1 = (mx - o) * inv;
        var lo = Vector128.Min(t0, t1);
        var hi = Vector128.Max(t0, t1);
        tNear = Mx(Mx(lo.GetElement(0), lo.GetElement(1)), Mx(lo.GetElement(2), lo.GetElement(3)));
        var tFar = Mn(Mn(hi.GetElement(0), hi.GetElement(1)), Mn(hi.GetElement(2), hi.GetElement(3)));
        return Mx(tNear, 0f) <= Mn(tFar, tMax);
    }

    /// <summary>광선-AABB 슬랩(스칼라 쌍둥이 · 같은 8 float 박스 레이아웃).</summary>
    public static bool SlabScalar(ReadOnlySpan<float> box, Vector3 o, Vector3 inv, float tMax, out float tNear)
    {
        float x0 = (box[0] - o.X) * inv.X, x1 = (box[4] - o.X) * inv.X;
        float y0 = (box[1] - o.Y) * inv.Y, y1 = (box[5] - o.Y) * inv.Y;
        float z0 = (box[2] - o.Z) * inv.Z, z1 = (box[6] - o.Z) * inv.Z;
        float w0 = (box[3] - 0f) * 1f, w1 = (box[7] - 0f) * 1f;
        tNear = Mx(Mx(Mn(x0, x1), Mn(y0, y1)), Mx(Mn(z0, z1), Mn(w0, w1)));
        var tFar = Mn(Mn(Mx(x0, x1), Mx(y0, y1)), Mn(Mx(z0, z1), Mx(w0, w1)));
        return Mx(tNear, 0f) <= Mn(tFar, tMax);
    }

    /// <summary>광선 방향 역수(0 성분은 ±1e-12 로 치환 — inf·0 = NaN 경로 제거).</summary>
    public static Vector3 SafeInverse(Vector3 d)
    {
        static float S(float v) => MathF.Abs(v) < 1e-12f ? (v < 0f ? -1e-12f : 1e-12f) : v;
        return new Vector3(1f / S(d.X), 1f / S(d.Y), 1f / S(d.Z));
    }
}

/// <summary>
/// [PromptFlow Studio v17.23.0] 정적 음향 메쉬 BVH — 평탄화 노드(8 float 박스) · 중앙값 분할 · 리프 4삼각형 · 고정 스택(stackalloc 64) 탐색.
/// 구축은 1회(할당 허용), <see cref="Raycast"/> 는 무할당 · SIMD 슬랩(<see cref="UseSimd"/> = false 면 스칼라 쌍둥이 — 결과 비트 동일).
/// </summary>
public sealed class AcousticSceneBvh
{
    public const int MaxTris = 128, MaxNodes = 2 * MaxTris, LeafTris = 4, StackDepth = 64;

    private readonly Vector3[] _v0 = new Vector3[MaxTris], _e1 = new Vector3[MaxTris], _e2 = new Vector3[MaxTris], _n = new Vector3[MaxTris];
    private readonly AcousticSurface[] _surf = new AcousticSurface[MaxTris];
    private readonly int[] _order = new int[MaxTris];
    private readonly float[] _box = new float[MaxNodes * 8];
    private readonly int[] _left = new int[MaxNodes], _right = new int[MaxNodes], _first = new int[MaxNodes], _count = new int[MaxNodes];
    private int _tris, _nodes;
    private long _casts, _visits;

    public int TriangleCount => _tris;
    public int NodeCount => _nodes;
    public long Casts => _casts;
    public long NodeVisits => _visits;
    public bool UseSimd { get; set; } = Vector128.IsHardwareAccelerated;

    public Vector3 Normal(int tri) => _n[tri];
    public AcousticSurface Surface(int tri) => _surf[tri];
    public float Area(int tri) => 0.5f * Vector3.Cross(_e1[tri], _e2[tri]).Length();

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, AcousticSurface s)
    {
        if (_tris >= MaxTris) throw new InvalidOperationException("음향 메쉬 삼각형 상한 초과");
        _v0[_tris] = a; _e1[_tris] = b - a; _e2[_tris] = c - a;
        _n[_tris] = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        _surf[_tris] = s;
        _tris++;
    }

    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, AcousticSurface s) { AddTriangle(a, b, c, s); AddTriangle(a, c, d, s); }

    /// <summary>축 정렬 상자(옆면 4 + 선택적 윗면 — 바닥에 놓인 물체는 밑면 생략).</summary>
    public void AddBox(Vector3 mn, Vector3 mx, AcousticSurface s, bool top)
    {
        Vector3 a = new(mn.X, mn.Y, mn.Z), b = new(mx.X, mn.Y, mn.Z), c = new(mx.X, mn.Y, mx.Z), d = new(mn.X, mn.Y, mx.Z);
        Vector3 e = new(mn.X, mx.Y, mn.Z), f = new(mx.X, mx.Y, mn.Z), g = new(mx.X, mx.Y, mx.Z), h = new(mn.X, mx.Y, mx.Z);
        AddQuad(a, b, f, e, s); AddQuad(b, c, g, f, s); AddQuad(c, d, h, g, s); AddQuad(d, a, e, h, s);
        if (top) AddQuad(e, f, g, h, s);
    }

    /// <summary>BVH 구축(최장 축 중심점 정렬 → 반분할 · 1회).</summary>
    public void Build()
    {
        for (var i = 0; i < _tris; i++) _order[i] = i;
        _nodes = 0;
        if (_tris > 0) BuildNode(0, _tris);
    }

    private Vector3 Centroid(int t) => _v0[t] + (_e1[t] + _e2[t]) / 3f;

    private int BuildNode(int first, int count)
    {
        var node = _nodes++;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        var cmn = mn; var cmx = mx;
        for (var i = first; i < first + count; i++)
        {
            var t = _order[i];
            var a = _v0[t]; var b = a + _e1[t]; var c = a + _e2[t];
            mn = Vector3.Min(mn, Vector3.Min(a, Vector3.Min(b, c)));
            mx = Vector3.Max(mx, Vector3.Max(a, Vector3.Max(b, c)));
            var ct = Centroid(t);
            cmn = Vector3.Min(cmn, ct); cmx = Vector3.Max(cmx, ct);
        }
        var o = node * 8;
        _box[o] = mn.X; _box[o + 1] = mn.Y; _box[o + 2] = mn.Z; _box[o + 3] = -1e30f;
        _box[o + 4] = mx.X; _box[o + 5] = mx.Y; _box[o + 6] = mx.Z; _box[o + 7] = 1e30f;
        if (count <= LeafTris)
        {
            _first[node] = first; _count[node] = count; _left[node] = -1; _right[node] = -1;
            return node;
        }
        var ext = cmx - cmn;
        var axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
        // 삽입 정렬(구축 1회 · 범위 ≤ 128)
        for (var i = first + 1; i < first + count; i++)
        {
            var key = _order[i]; var kv = Axis(Centroid(key), axis); var j = i - 1;
            while (j >= first && Axis(Centroid(_order[j]), axis) > kv) { _order[j + 1] = _order[j]; j--; }
            _order[j + 1] = key;
        }
        var half = count / 2;
        _count[node] = 0;
        _left[node] = BuildNode(first, half);
        _right[node] = BuildNode(first + half, count - half);
        return node;
    }

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>삼각형 교차(Möller–Trumbore · 양면). 없으면 +∞.</summary>
    public float IntersectTriangle(int t, Vector3 o, Vector3 d)
    {
        var e1 = _e1[t]; var e2 = _e2[t];
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return float.PositiveInfinity;
        var inv = 1f / det;
        var s = o - _v0[t];
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return float.PositiveInfinity;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return float.PositiveInfinity;
        var th = Vector3.Dot(e2, q) * inv;
        return th > 1e-4f ? th : float.PositiveInfinity;
    }

    /// <summary>최근접 교차(무할당). d 는 단위 벡터.</summary>
    public bool Raycast(Vector3 o, Vector3 d, float tMax, out float tHit, out int triHit)
    {
        _casts++;
        tHit = tMax; triHit = -1;
        if (_nodes == 0) return false;
        var inv = AcousticKernels.SafeInverse(d);
        var vo = Vector128.Create(o.X, o.Y, o.Z, 0f);
        var vi = Vector128.Create(inv.X, inv.Y, inv.Z, 1f);
        var simd = UseSimd;
        ref var box = ref MemoryMarshal.GetArrayDataReference(_box);
        Span<int> stack = stackalloc int[StackDepth];
        var sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            var node = stack[--sp];
            _visits++;
            var b = node * 8;
            var hit = simd
                ? AcousticKernels.SlabSimd(Vector128.LoadUnsafe(ref box, (nuint)b), Vector128.LoadUnsafe(ref box, (nuint)(b + 4)), vo, vi, tHit, out _)
                : AcousticKernels.SlabScalar(_box.AsSpan(b, 8), o, inv, tHit, out _);
            if (!hit) continue;
            var c = _count[node];
            if (c > 0)
            {
                var end = _first[node] + c;
                for (var i = _first[node]; i < end; i++)
                {
                    var t = _order[i];
                    var th = IntersectTriangle(t, o, d);
                    if (th < tHit) { tHit = th; triHit = t; }
                }
            }
            else if (sp + 2 <= StackDepth)
            {
                stack[sp++] = _right[node];
                stack[sp++] = _left[node];
            }
        }
        return triHit >= 0;
    }

    /// <summary>선분 a→b 차폐 여부(양 끝 1 mm 제외).</summary>
    public bool Blocked(Vector3 a, Vector3 b)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 3e-3f) return false;
        d /= len;
        return Raycast(a + d * 1e-3f, d, len - 2e-3f, out _, out _);
    }
}

/// <summary>[PromptFlow Studio v17.23.0] 음향 경로 1개(직접음 · 1차 · 2차 이미지 소스) — 길이 · 청자 직전 지점 · 저/고 대역 압력 이득.</summary>
public struct AcousticPath
{
    public bool Valid;
    public bool Diffracted;
    public byte Order;
    public float LengthM;
    public float GainLow;
    public float GainHigh;
    /// <summary>청자 직전 지점(직접음 = 음원 · 반사 = 마지막 반사점 · 회절 = 모서리).</summary>
    public Vector3 Via;
}

/// <summary>
/// [PromptFlow Studio v17.23.0] 🔊 실시간 음향 광선 추적 &amp; 재질 흡음 연산 엔진(무할당 · 결정적).
///   ▸ 정적 세트 메쉬(방 6면 · v17.21 블로킹 세트 기둥 3 · 상자 2 = 56 삼각형)를 <see cref="AcousticSceneBvh"/> 로 1회 구축.
///   ▸ 초기 반사음: 방 6면 이미지 소스 1차(6) · 2차(30) 후보를 BVH 광선으로 구간별 가시성 검증 → 경로 슬롯 고정(37) — 슬롯이 연속이라 도플러 램프가 성립.
///   ▸ 후기 잔향: 매 프레임 피보나치 구(무작위 회전) 광선 N개 × 최대 128 반사 — 재질 산란 확률로 경면/람베르트 반사,
///     8대역 반사율 × 공기 흡수를 SIMD 커널로 곱하고, 전 광선 잔존 에너지 감쇠 곡선(5 ms 빈) · 수음 구 에코그램(4 ms 빈)을 EMA 누적.
///   ▸ RT60: 대역별 감쇠 곡선 −5 → −25 dB 선형 회귀(T20 × 3) · 비교용 Eyring 해석해.
/// </summary>
public sealed class RealtimeAcousticRayTracingEngine
{
    public const int Bands = AcousticMaterials.Bands, Planes = 6, PathSlots = 1 + Planes + Planes * (Planes - 1);
    public const int MaxRaysPerFrame = 512, MaxBounces = 128, DecayBins = 320, EchoBins = 200, VizRays = 12, VizPoints = 8, LateStartBin = 20;
    public const float DecayBinMs = 5f, EchoBinMs = 4f, SoundSpeed = 343f, ReceiverRadius = 0.6f, EmaAlpha = 1f / 32f, MinLength = 0.3f;
    public const float X0 = -5f, X1 = 5f, Z0 = -5f, Z1 = 6f, H = 4.2f, PillarHalf = 0.32f;

    /// <summary>v17.21 블로킹 세트 기둥(x, z) · 상자 — 시각 세트와 동일 배치.</summary>
    public static readonly Vector2[] Pillars = { new(-0.2f, -0.9f), new(2.4f, -2.6f), new(-2.6f, 1.6f) };
    public static readonly Vector3[] CrateMin = { new(1.1f, 0f, 1.2f), new(-3.4f, 0f, -3.2f) };
    public static readonly Vector3[] CrateMax = { new(1.9f, 0.9f, 1.9f), new(-2.4f, 1.2f, -2.4f) };

    private readonly AcousticMaterial[] _surfMat =
    {
        AcousticMaterial.Wood, AcousticMaterial.AcousticPanel, AcousticMaterial.Concrete, AcousticMaterial.Glass,
        AcousticMaterial.Plaster, AcousticMaterial.Fabric, AcousticMaterial.Concrete, AcousticMaterial.Wood,
    };
    private readonly float[] _surfArea = new float[AcousticMaterials.Surfaces];
    private readonly Vector3[] _pn = new Vector3[Planes];
    private readonly float[] _pd = new float[Planes];
    private readonly AcousticSurface[] _ps = new AcousticSurface[Planes];
    private readonly float[] _decay = new float[DecayBins * Bands], _decayFrame = new float[DecayBins * Bands];
    private readonly float[] _echo = new float[EchoBins], _echoFrame = new float[EchoBins];
    private readonly float[] _rt60 = new float[Bands];
    private readonly Vector3[] _viz = new Vector3[VizRays * VizPoints];
    private readonly int[] _vizN = new int[VizRays];
    private uint _rng = 0x2545F491u;
    private bool _primed;
    private int _rays = 64;
    private bool _simd = Vector128.IsHardwareAccelerated;

    public RealtimeAcousticRayTracingEngine()
    {
        var b = Bvh;
        b.AddQuad(new(X0, 0f, Z0), new(X1, 0f, Z0), new(X1, 0f, Z1), new(X0, 0f, Z1), AcousticSurface.Floor);
        b.AddQuad(new(X0, H, Z0), new(X0, H, Z1), new(X1, H, Z1), new(X1, H, Z0), AcousticSurface.Ceiling);
        b.AddQuad(new(X0, 0f, Z0), new(X0, H, Z0), new(X1, H, Z0), new(X1, 0f, Z0), AcousticSurface.BackWall);
        b.AddQuad(new(X0, 0f, Z0), new(X0, 0f, Z1), new(X0, H, Z1), new(X0, H, Z0), AcousticSurface.WindowWall);
        b.AddQuad(new(X1, 0f, Z0), new(X1, H, Z0), new(X1, H, Z1), new(X1, 0f, Z1), AcousticSurface.SideWall);
        b.AddQuad(new(X0, 0f, Z1), new(X1, 0f, Z1), new(X1, H, Z1), new(X0, H, Z1), AcousticSurface.FrontWall);
        for (var i = 0; i < Pillars.Length; i++)
            b.AddBox(new Vector3(Pillars[i].X - PillarHalf, 0f, Pillars[i].Y - PillarHalf), new Vector3(Pillars[i].X + PillarHalf, H, Pillars[i].Y + PillarHalf), AcousticSurface.Pillars, false);
        for (var i = 0; i < CrateMin.Length; i++) b.AddBox(CrateMin[i], CrateMax[i], AcousticSurface.Crates, true);
        b.Build();
        for (var t = 0; t < b.TriangleCount; t++) _surfArea[(int)b.Surface(t)] += b.Area(t);

        SetPlane(0, Vector3.UnitY, 0f, AcousticSurface.Floor);
        SetPlane(1, -Vector3.UnitY, -H, AcousticSurface.Ceiling);
        SetPlane(2, Vector3.UnitZ, Z0, AcousticSurface.BackWall);
        SetPlane(3, Vector3.UnitX, X0, AcousticSurface.WindowWall);
        SetPlane(4, -Vector3.UnitX, -X1, AcousticSurface.SideWall);
        SetPlane(5, -Vector3.UnitZ, -Z1, AcousticSurface.FrontWall);
        for (var k = 0; k < Bands; k++) _rt60[k] = EyringRt60(k);
    }

    private void SetPlane(int i, Vector3 n, float d, AcousticSurface s) { _pn[i] = n; _pd[i] = d; _ps[i] = s; }

    public AcousticSceneBvh Bvh { get; } = new();
    public int RaysPerFrame { get => _rays; set => _rays = Math.Clamp(value, 8, MaxRaysPerFrame); }
    public bool UseSimd { get => _simd; set { _simd = value; Bvh.UseSimd = value; } }
    public float Volume => (X1 - X0) * (Z1 - Z0) * H;
    public float SurfaceArea(AcousticSurface s) => _surfArea[(int)s];
    public AcousticMaterial Material(AcousticSurface s) => _surfMat[(int)s];
    public float Rt60(int band) => _rt60[band];
    public float Rt60Low => 0.5f * (_rt60[2] + _rt60[3]);
    public float Rt60High => 0.5f * (_rt60[4] + _rt60[5]);
    public float LateLevel { get; private set; }
    public float DecayDb(int bin, int band) => 10f * MathF.Log10(MathF.Max(_decay[bin * Bands + band], 1e-12f));
    public float EchoDb(int bin) => 10f * MathF.Log10(MathF.Max(_echo[bin], 1e-12f));
    public int VizCount(int ray) => _vizN[ray];
    public Vector3 VizPoint(int ray, int k) => _viz[ray * VizPoints + k];
    public long RaysTraced { get; private set; }
    public long Bounces { get; private set; }
    public long ReceiverHits { get; private set; }
    public long Escaped { get; private set; }
    public long Frames { get; private set; }
    public double LastTraceUs { get; private set; }
    public double LastPathUs { get; private set; }
    public int ValidPaths { get; private set; }

    /// <summary>표면 그룹 재질 교체(BVH 재구축 없음) — 통계 재수렴을 위해 EMA 초기화.</summary>
    public void SetMaterial(AcousticSurface s, AcousticMaterial m)
    {
        _surfMat[(int)s] = m;
        ResetStatistics();
    }

    public void ResetStatistics()
    {
        _primed = false;
        Array.Clear(_decay); Array.Clear(_echo);
        for (var k = 0; k < Bands; k++) _rt60[k] = EyringRt60(k);
        LateLevel = 0f;
    }

    /// <summary>Eyring 잔향 시간(s) — 0.161 V / (−S ln(1 − ᾱ) + 4 m V).</summary>
    public float EyringRt60(int band)
    {
        float s = 0f, sa = 0f;
        for (var i = 0; i < AcousticMaterials.Surfaces; i++) { s += _surfArea[i]; sa += _surfArea[i] * AcousticMaterials.Alpha(_surfMat[i], band); }
        var abar = Math.Clamp(sa / s, 1e-4f, 0.9999f);
        var v = Volume;
        return 0.161f * v / (-s * MathF.Log(1f - abar) + 4f * AcousticMaterials.AirAbsorption[band] * v);
    }

    private float S(int p, Vector3 x) => Vector3.Dot(_pn[p], x) - _pd[p];
    private static bool InRoom(Vector3 r) => r.X >= X0 - 1e-3f && r.X <= X1 + 1e-3f && r.Y >= -1e-3f && r.Y <= H + 1e-3f && r.Z >= Z0 - 1e-3f && r.Z <= Z1 + 1e-3f;

    /// <summary>
    /// 직접음 + 이미지 소스 1차 · 2차 경로(37 슬롯 고정) 계산 — 무할당. 구간 가시성은 BVH 광선(기둥 · 상자 차폐)으로 검증.
    /// 슬롯: 0 직접음 · 1–6 평면 p · 7– (p, q≠p).
    /// </summary>
    public int ComputePaths(Vector3 src, Vector3 lis, Span<AcousticPath> dst)
    {
        if (dst.Length < PathSlots) throw new ArgumentException("경로 슬롯 부족", nameof(dst));
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<float> e = stackalloc float[Bands];
        var len = MathF.Max(Vector3.Distance(src, lis), MinLength);
        e.Fill(1f); Air(e, len);
        Fill(ref dst[0], 0, len, src, e);
        var valid = 1;
        for (var p = 0; p < Planes; p++)
        {
            ref var t = ref dst[1 + p];
            t.Valid = false; t.Order = 1; t.Diffracted = false;
            var sl = S(p, lis); var ss = S(p, src);
            if (sl <= 0f || ss <= 0f) continue;
            var img = src - 2f * ss * _pn[p];
            var r = lis + (img - lis) * (sl / (sl + ss));
            if (!InRoom(r) || Bvh.Blocked(src, r) || Bvh.Blocked(r, lis)) continue;
            len = MathF.Max(Vector3.Distance(img, lis), MinLength);
            e.Fill(1f); Reflect(e, p); Air(e, len);
            Fill(ref t, 1, len, r, e);
            valid++;
        }
        var slot = 1 + Planes;
        for (var p = 0; p < Planes; p++)
        {
            for (var q = 0; q < Planes; q++)
            {
                if (q == p) continue;
                ref var t = ref dst[slot++];
                t.Valid = false; t.Order = 2; t.Diffracted = false;
                var ss = S(p, src);
                if (ss <= 0f) continue;
                var img1 = src - 2f * ss * _pn[p];
                var s1 = S(q, img1);
                var sl = S(q, lis);
                if (s1 <= 0f || sl <= 0f) continue;
                var img2 = img1 - 2f * s1 * _pn[q];
                var r2 = lis + (img2 - lis) * (sl / (sl + s1));
                var a = S(p, r2);
                if (a <= 0f) continue;
                var r1 = r2 + (img1 - r2) * (a / (a + ss));
                if (!InRoom(r1) || !InRoom(r2) || Bvh.Blocked(src, r1) || Bvh.Blocked(r1, r2) || Bvh.Blocked(r2, lis)) continue;
                len = MathF.Max(Vector3.Distance(img2, lis), MinLength);
                e.Fill(1f); Reflect(e, p); Reflect(e, q); Air(e, len);
                Fill(ref t, 2, len, r2, e);
                valid++;
            }
        }
        ValidPaths = valid;
        LastPathUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return valid;
    }

    private void Reflect(Span<float> e, int plane)
    {
        var refl = AcousticMaterials.Reflectance(_surfMat[(int)_ps[plane]]);
        if (_simd) AcousticKernels.AttenuateSimd(e, refl, AcousticMaterials.Ones);
        else AcousticKernels.AttenuateScalar(e, refl, AcousticMaterials.Ones);
    }

    private static void Air(Span<float> e, float m)
    {
        for (var k = 0; k < Bands; k++) e[k] *= MathF.Exp(-AcousticMaterials.AirAbsorption[k] * m);
    }

    private static void Fill(ref AcousticPath t, byte order, float len, Vector3 via, ReadOnlySpan<float> e)
    {
        var inv = 1f / len;
        t.Valid = true; t.Order = order; t.Diffracted = false; t.LengthM = len; t.Via = via;
        t.GainLow = inv * MathF.Sqrt(0.25f * (e[0] + e[1] + e[2] + e[3]));
        t.GainHigh = inv * MathF.Sqrt(0.25f * (e[4] + e[5] + e[6] + e[7]));
    }

    private float Rand()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private static Vector3 Fibonacci(int i, int n)
    {
        var z = 1f - 2f * (i + 0.5f) / n;
        var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        var phi = i * 2.3999632f;
        return new Vector3(r * MathF.Cos(phi), z, r * MathF.Sin(phi));
    }

    private Vector3 CosineHemisphere(Vector3 n)
    {
        var phi = 2f * MathF.PI * Rand();
        var r2 = Rand();
        var sr = MathF.Sqrt(r2);
        var t = MathF.Abs(n.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(t, n));
        var v = Vector3.Cross(n, u);
        return Vector3.Normalize(u * (sr * MathF.Cos(phi)) + v * (sr * MathF.Sin(phi)) + n * MathF.Sqrt(MathF.Max(0f, 1f - r2)));
    }

    /// <summary>
    /// 확률적 광선 추적 1프레임(무할당): 피보나치 구 × 프레임 무작위 회전 → 반사(산란 확률 경면/확산) → 잔존 에너지 감쇠 곡선 ·
    /// 수음 구 에코그램 누적 → EMA 혼합 → 대역별 RT60 · 후기 잔향 레벨 갱신.
    /// </summary>
    public void TraceRays(Vector3 src, Vector3 lis)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = _rays;
        Array.Clear(_decayFrame); Array.Clear(_echoFrame);
        Span<float> e = stackalloc float[Bands];
        Span<float> air = stackalloc float[Bands];
        // Shoemake 무작위 회전(프레임마다 다른 방향 집합 → 프레임 누적 수렴)
        float u1 = Rand(), u2 = Rand(), u3 = Rand();
        var rot = new Quaternion(MathF.Sqrt(1f - u1) * MathF.Sin(2f * MathF.PI * u2), MathF.Sqrt(1f - u1) * MathF.Cos(2f * MathF.PI * u2), MathF.Sqrt(u1) * MathF.Sin(2f * MathF.PI * u3), MathF.Sqrt(u1) * MathF.Cos(2f * MathF.PI * u3));
        var inv = 1f / n;
        const float r2Recv = ReceiverRadius * ReceiverRadius;
        const float invArea = 1f / (MathF.PI * ReceiverRadius * ReceiverRadius);
        long bounces = 0, hits = 0, escaped = 0;
        for (var i = 0; i < n; i++)
        {
            var dir = Vector3.Transform(Fibonacci(i, n), rot);
            var pos = src;
            var pathM = 0f;
            e.Fill(inv);
            var viz = i < VizRays;
            if (viz) { _viz[i * VizPoints] = src; _vizN[i] = 1; }
            for (var b = 0; b < MaxBounces; b++)
            {
                if (!Bvh.Raycast(pos, dir, 200f, out var t, out var tri)) { escaped++; break; }
                if (b > 0)
                {
                    var toL = lis - pos;
                    var sp = Vector3.Dot(toL, dir);
                    if (sp > 0f && sp < t && toL.LengthSquared() - sp * sp < r2Recv)
                    {
                        var bin = (int)((pathM + sp) / SoundSpeed * 1000f / EchoBinMs);
                        if (bin < EchoBins) _echoFrame[bin] += Mean(e) * invArea;
                        hits++;
                    }
                }
                AccumulateDecay(pathM, pathM + t, e);
                pathM += t;
                pos += dir * t;
                if (viz && _vizN[i] < VizPoints) _viz[i * VizPoints + _vizN[i]++] = pos;
                for (var k = 0; k < Bands; k++) air[k] = MathF.Exp(-AcousticMaterials.AirAbsorption[k] * t);
                var mat = _surfMat[(int)Bvh.Surface(tri)];
                if (_simd) AcousticKernels.AttenuateSimd(e, AcousticMaterials.Reflectance(mat), air);
                else AcousticKernels.AttenuateScalar(e, AcousticMaterials.Reflectance(mat), air);
                bounces++;
                if (Max(e) < inv * 1e-4f) break;   // 전 대역 −40 dB
                var nrm = Bvh.Normal(tri);
                if (Vector3.Dot(nrm, dir) > 0f) nrm = -nrm;
                dir = Rand() < AcousticMaterials.Scattering(mat) ? CosineHemisphere(nrm) : Vector3.Reflect(dir, nrm);
                pos += nrm * 1e-3f;
            }
        }
        // EMA 혼합(첫 프레임 = 그대로)
        var a = _primed ? EmaAlpha : 1f;
        for (var i = 0; i < _decay.Length; i++) _decay[i] += (_decayFrame[i] - _decay[i]) * a;
        for (var i = 0; i < EchoBins; i++) _echo[i] += (_echoFrame[i] - _echo[i]) * a;
        _primed = true;
        EstimateRt60();
        var late = 0f;
        for (var i = LateStartBin; i < EchoBins; i++) late += _echo[i];
        LateLevel = MathF.Sqrt(late * 4f * MathF.PI);   // 1 m 직접음 대비 후기 잔향 압력비
        RaysTraced += n; Bounces += bounces; ReceiverHits += hits; Escaped += escaped; Frames++;
        LastTraceUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static float Mean(ReadOnlySpan<float> e)
    {
        var s = 0f;
        for (var k = 0; k < Bands; k++) s += e[k];
        return s * (1f / Bands);
    }

    private static float Max(ReadOnlySpan<float> e)
    {
        var m = e[0];
        for (var k = 1; k < Bands; k++) if (e[k] > m) m = e[k];
        return m;
    }

    /// <summary>구간 [m0, m1)(미터) 동안 광선 에너지를 시간 빈 체류 비율만큼 누적 — 빈 값 = 그 시간 동안의 평균 잔존 에너지.</summary>
    private void AccumulateDecay(float m0, float m1, ReadOnlySpan<float> e)
    {
        const float scale = 1000f / (SoundSpeed * DecayBinMs);
        var t0 = m0 * scale; var t1 = m1 * scale;
        var bEnd = Math.Min((int)t1, DecayBins - 1);
        for (var b = (int)t0; b <= bEnd; b++)
        {
            var w = MathF.Min(t1, b + 1) - MathF.Max(t0, b);
            if (w <= 0f) continue;
            var o = b * Bands;
            for (var k = 0; k < Bands; k++) _decayFrame[o + k] += e[k] * w;
        }
    }

    /// <summary>대역별 감쇠 곡선 −5 → −25 dB 구간 선형 회귀(T20 × 3) — 미도달 시 마지막 유효 빈까지(≥ 10 dB 범위).</summary>
    private void EstimateRt60()
    {
        for (var k = 0; k < Bands; k++)
        {
            var e0 = _decay[k];
            if (e0 <= 0f) continue;
            int i5 = -1, iEnd = -1;
            for (var i = 0; i < DecayBins; i++)
            {
                var v = _decay[i * Bands + k];
                if (v <= 0f) break;
                var db = 10f * MathF.Log10(v / e0);
                if (i5 < 0 && db <= -5f) i5 = i;
                iEnd = i;
                if (db <= -25f) break;
            }
            if (i5 < 0 || iEnd - i5 < 2) continue;
            var dbEnd = 10f * MathF.Log10(_decay[iEnd * Bands + k] / e0);
            if (dbEnd > -15f) continue;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            var cnt = 0;
            for (var i = i5; i <= iEnd; i++)
            {
                double x = (i + 0.5) * DecayBinMs, y = 10.0 * Math.Log10(_decay[i * Bands + k] / e0);
                sx += x; sy += y; sxx += x * x; sxy += x * y; cnt++;
            }
            var den = cnt * sxx - sx * sx;
            if (den <= 0) continue;
            var slope = (cnt * sxy - sx * sy) / den;   // dB/ms
            if (slope < -1e-6) _rt60[k] = (float)(-60.0 / slope / 1000.0);
        }
    }
}
