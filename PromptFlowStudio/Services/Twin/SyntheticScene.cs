using System.Numerics;

namespace PromptFlowStudio.Services.Twin;

public enum SimShape : byte { Sphere = 0, Box = 1 }

/// <summary>
/// [PromptFlow Studio v16.73.0] 디지털 트윈 · 합성 데이터 · 센서 시뮬레이션 공용 장면(SoA · 축 정렬 박스/구 64개 + 무한 바닥 y=0).
/// 레이캐스트/가림 판정은 무할당 — 합성 렌더러(픽셀 광선) · 라이다 · 조도계 그림자 공용.
/// </summary>
public sealed class SyntheticScene
{
    public const int MaxObjects = 64;
    /// <summary>레이캐스트 결과 hit = 바닥.</summary>
    public const int FloorHit = -1;
    public static readonly string[] ClassNames = { "background", "person", "forklift", "robot", "drone", "pallet", "rack", "station" };

    public int Count;
    public bool Floor = true;
    public readonly byte[] Shape = new byte[MaxObjects], Class = new byte[MaxObjects];
    public readonly bool[] Hidden = new bool[MaxObjects];
    public readonly float[] Px = new float[MaxObjects], Py = new float[MaxObjects], Pz = new float[MaxObjects];
    public readonly float[] Hx = new float[MaxObjects], Hy = new float[MaxObjects], Hz = new float[MaxObjects];
    public readonly float[] R = new float[MaxObjects], G = new float[MaxObjects], B = new float[MaxObjects];

    /// <summary>객체 추가(구는 half.X = 반지름). 반환 = 인덱스(가득 차면 −1).</summary>
    public int Add(SimShape shape, byte cls, Vector3 p, Vector3 half, Vector3 rgb)
    {
        if (Count >= MaxObjects) return -1;
        var i = Count++;
        Shape[i] = (byte)shape; Class[i] = cls; Hidden[i] = false;
        Px[i] = p.X; Py[i] = p.Y; Pz[i] = p.Z;
        Hx[i] = MathF.Max(0.01f, half.X);
        Hy[i] = shape == SimShape.Sphere ? Hx[i] : MathF.Max(0.01f, half.Y);
        Hz[i] = shape == SimShape.Sphere ? Hx[i] : MathF.Max(0.01f, half.Z);
        R[i] = rgb.X; G[i] = rgb.Y; B[i] = rgb.Z;
        return i;
    }

    public void Clear() => Count = 0;

    public Vector3 Position(int i) => new(Px[i], Py[i], Pz[i]);
    public Vector3 Half(int i) => new(Hx[i], Hy[i], Hz[i]);
    public Vector3 Color(int i) => new(R[i], G[i], B[i]);

    public void SetPosition(int i, Vector3 p) { Px[i] = p.X; Py[i] = p.Y; Pz[i] = p.Z; }

    public void CopyFrom(SyntheticScene s)
    {
        var n = Count = s.Count;
        Floor = s.Floor;
        Array.Copy(s.Shape, Shape, n); Array.Copy(s.Class, Class, n); Array.Copy(s.Hidden, Hidden, n);
        Array.Copy(s.Px, Px, n); Array.Copy(s.Py, Py, n); Array.Copy(s.Pz, Pz, n);
        Array.Copy(s.Hx, Hx, n); Array.Copy(s.Hy, Hy, n); Array.Copy(s.Hz, Hz, n);
        Array.Copy(s.R, R, n); Array.Copy(s.G, G, n); Array.Copy(s.B, B, n);
    }

    /// <summary>최근접 교차(d 정규화 가정). hit = 객체 인덱스 · <see cref="FloorHit"/>. skip = 제외 객체(센서 부착체).</summary>
    public bool Raycast(Vector3 o, Vector3 d, float tMax, out float t, out int hit, out Vector3 n, int skip = -1)
    {
        t = tMax; hit = int.MinValue; n = default;
        for (var i = 0; i < Count; i++)
        {
            if (i == skip || Hidden[i]) continue;
            if (Intersect(i, o, d, t, out var ti, out var ni)) { t = ti; hit = i; n = ni; }
        }
        if (Floor && d.Y < -1e-6f)
        {
            var tf = -o.Y / d.Y;
            if (tf > 1e-4f && tf < t) { t = tf; hit = FloorHit; n = Vector3.UnitY; }
        }
        return hit != int.MinValue;
    }

    /// <summary>o→d 방향 maxT 이내 가림 여부(그림자 · 조도계 가시성).</summary>
    public bool Occluded(Vector3 o, Vector3 d, float maxT, int skip = -1)
    {
        for (var i = 0; i < Count; i++)
            if (i != skip && !Hidden[i] && Intersect(i, o, d, maxT, out _, out _)) return true;
        if (Floor && d.Y < -1e-6f)
        {
            var tf = -o.Y / d.Y;
            if (tf > 1e-4f && tf < maxT) return true;
        }
        return false;
    }

    /// <summary>단일 객체 교차(아모달 마스크 · 가림률 계산용).</summary>
    public bool IntersectOne(int i, Vector3 o, Vector3 d, float tMax, out float t) => Intersect(i, o, d, tMax, out t, out _);

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    private bool Intersect(int i, Vector3 o, Vector3 d, float tMax, out float t, out Vector3 n)
    {
        t = 0; n = default;
        var c = new Vector3(Px[i], Py[i], Pz[i]);
        if (Shape[i] == (byte)SimShape.Sphere)
        {
            var r = Hx[i];
            var oc = o - c;
            var b = Vector3.Dot(oc, d);
            var disc = b * b - (oc.LengthSquared() - r * r);
            if (disc < 0) return false;
            var s = MathF.Sqrt(disc);
            var tt = -b - s;
            if (tt < 1e-4f) tt = -b + s;
            if (tt < 1e-4f || tt >= tMax) return false;
            t = tt; n = (o + d * tt - c) / r;
            return true;
        }
        var h = new Vector3(Hx[i], Hy[i], Hz[i]);
        var mn = c - h; var mx = c + h;
        float t0 = float.NegativeInfinity, t1 = float.PositiveInfinity, sign = 0;
        var axis = 0;
        for (var a = 0; a < 3; a++)
        {
            float oa = Axis(o, a), da = Axis(d, a), lo = Axis(mn, a), hi = Axis(mx, a);
            if (MathF.Abs(da) < 1e-9f)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            var inv = 1f / da;
            float ta = (lo - oa) * inv, tb = (hi - oa) * inv, sg = -1f;
            if (ta > tb) { (ta, tb) = (tb, ta); sg = 1f; }
            if (ta > t0) { t0 = ta; axis = a; sign = sg; }
            if (tb < t1) t1 = tb;
            if (t0 > t1) return false;
        }
        if (t1 < 1e-4f) return false;
        var hitT = t0 >= 1e-4f ? t0 : t1;
        if (hitT >= tMax) return false;
        t = hitT;
        n = axis == 0 ? new Vector3(sign, 0, 0) : axis == 1 ? new Vector3(0, sign, 0) : new Vector3(0, 0, sign);
        return true;
    }
}
