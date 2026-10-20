using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.SpatialSlam;

public enum PlaneKind : byte { Other, Floor, Ceiling, Wall, Table }
public enum AnchorKind : byte { Prop, DigitalHuman }
public enum AnchorFix : byte { None, Levitation, FloorPenetration, WallPenetration, CeilingPenetration, SurfacePenetration, NoSupport }

/// <summary>감지 평면(n·p + d = 0 · n = 자유 공간 방향) — 접선 축 U/V 범위 = 인라이어 외접 사각형.</summary>
public struct SpatialPlane
{
    public int Id, Inliers, Seen;
    public PlaneKind Kind;
    public Vector3 Normal, Centroid, U, V;
    public float D, UMin, UMax, VMin, VMax;
    public readonly float Area => (UMax - UMin) * (VMax - VMin);
    public readonly float Height => Centroid.Y;
    public readonly bool Stable => Seen >= 3;
    public readonly float Distance(Vector3 p) => Vector3.Dot(Normal, p) + D;
    public readonly bool Contains(Vector3 p, float margin)
    {
        var r = p - Centroid;
        var u = Vector3.Dot(r, U); var v = Vector3.Dot(r, V);
        return u >= UMin - margin && u <= UMax + margin && v >= VMin - margin && v <= VMax + margin;
    }
}

/// <summary>가상 소품/디지털 휴먼 앵커(Position = 바닥 중심 · 반지름 · 높이 원기둥).</summary>
public struct SpatialAnchor
{
    public string Name;
    public AnchorKind Kind;
    public Vector3 Position, Velocity;
    public float Radius, Height;
    public bool NeedsSupport, Grounded;
    public int SupportPlane;
    public AnchorFix LastFix;
}

public readonly record struct AnchorEvent(long Seq, int Anchor, AnchorFix Fix, float Amount, int Plane);

/// <summary>
/// [PromptFlow Studio v16.98.0] 🗺️ 공간 평면 분할 &amp; 물리 충돌 앵커 가드.
/// · RANSAC(법선 1점 가설 · 결정적 xorshift): 가용 점 SoA Vector&lt;float&gt; 일괄 인라이어(거리 + 법선 일치) 계수 → 최다 가설 → 인라이어 평균 법선/중심 정제 → 제거 반복(최대 12면).
/// · 분류: 수평 상향 최저 = 바닥 · 바닥 +0.55~1.2 m 상향 = 테이블 · 하향 = 천장 · 수직 ≥ 1 m² = 벽 · 이전 감지와 종류/오프셋/법선 매칭 → ID 유지 · 3회 = 안정.
/// · 앵커 가드(무할당): 지지면(바닥/테이블 · 범위 내 최고) 관통 → 올림 · 공중 부양 → 스냅(또는 중력 낙하) · 벽/천장 침범 → 법선 밀어냄 ·
///   TSDF 표면(가구 등 평면 외) 침투 → 그래디언트 밀어냄 · 보정 이벤트 링.
/// </summary>
public sealed class SpatialPlaneDetectionAnchorGuard
{
    public const int MaxPlanes = 16, MaxAnchors = 32, MaxPoints = 32_768, EventCap = 256;
    public static readonly string[] KindNames = { "기타", "바닥", "천장", "벽", "테이블" };
    public static readonly string[] FixNames = { "정상", "공중 부양 보정", "바닥 관통 보정", "벽 침범 보정", "천장 침범 보정", "표면 침투 보정", "지지면 없음" };
    private static readonly int Pad = (MaxPoints + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    private readonly object _gate = new();
    private readonly float[] _x = new float[Pad], _y = new float[Pad], _z = new float[Pad], _nx = new float[Pad], _ny = new float[Pad], _nz = new float[Pad], _avail = new float[Pad];
    private readonly sbyte[] _label = new sbyte[MaxPoints];
    private readonly SpatialPlane[] _planes = new SpatialPlane[MaxPlanes], _prev = new SpatialPlane[MaxPlanes];
    private readonly SpatialAnchor[] _anchors = new SpatialAnchor[MaxAnchors];
    private readonly AnchorEvent[] _events = new AnchorEvent[EventCap];
    private int _n, _planeCount, _prevCount, _anchorCount, _nextId = 1;
    private long _eventSeq, _detections;
    private ulong _rng = 0x1698_5EEDUL;

    public float DistanceThreshold { get; set; } = 0.04f;
    public float NormalThreshold { get; set; } = 0.9f;
    public int Iterations { get; set; } = 160;
    public int MinInliers { get; set; } = 60;
    public float MinArea { get; set; } = 0.12f;
    public float Tolerance { get; set; } = 0.02f;
    public bool Gravity { get; set; }
    public LidarPointCloudMeshReconstructionSynthesizer? Surface { get; set; }

    public int PointCount => _n;
    public int PlaneCount => _planeCount;
    public int AnchorCount => _anchorCount;
    public long Detections => _detections;
    public long EventSeq => Interlocked.Read(ref _eventSeq);
    public double LastDetectUs { get; private set; }
    public ref readonly SpatialPlane Plane(int i) => ref _planes[i];
    public ref SpatialAnchor Anchor(int i) => ref _anchors[i];
    public int Label(int point) => _label[point];
    public Vector3 Point(int i) => new(_x[i], _y[i], _z[i]);

    public int FindKind(PlaneKind k) { for (var i = 0; i < _planeCount; i++) if (_planes[i].Kind == k) return i; return -1; }
    public int CountKind(PlaneKind k) { var c = 0; for (var i = 0; i < _planeCount; i++) if (_planes[i].Kind == k) c++; return c; }

    public int AddAnchor(string name, AnchorKind kind, Vector3 pos, float radius, float height, bool needsSupport = true)
    {
        lock (_gate)
        {
            if (_anchorCount >= MaxAnchors) return -1;
            _anchors[_anchorCount] = new SpatialAnchor { Name = name, Kind = kind, Position = pos, Radius = radius, Height = height, NeedsSupport = needsSupport, SupportPlane = -1 };
            return _anchorCount++;
        }
    }

    public void ClearAnchors() { lock (_gate) _anchorCount = 0; }

    // ── RANSAC ──
    /// <summary>점(+법선) SoA 입력 → 평면 분할(무할당) → 평면 수.</summary>
    public int Detect(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, ReadOnlySpan<float> nx, ReadOnlySpan<float> ny, ReadOnlySpan<float> nz, int stride = 1)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var n = 0;
            for (var i = 0; i < x.Length && n < MaxPoints; i += Math.Max(1, stride))
            {
                _x[n] = x[i]; _y[n] = y[i]; _z[n] = z[i]; _nx[n] = nx[i]; _ny[n] = ny[i]; _nz[n] = nz[i]; _avail[n] = 1f; _label[n] = -1; n++;
            }
            _n = n;
            for (var i = n; i < Pad; i++) _avail[i] = 0f;
            Array.Copy(_planes, _prev, _planeCount); _prevCount = _planeCount;
            _planeCount = 0;
            var remaining = n;
            while (_planeCount < MaxPlanes && remaining >= MinInliers)
            {
                var bestCount = 0; Vector3 bn = default; float bd = 0;
                for (var it = 0; it < Iterations; it++)
                {
                    var s = Sample(n);
                    if (s < 0) break;
                    var hn = new Vector3(_nx[s], _ny[s], _nz[s]);
                    var hd = -Vector3.Dot(hn, new Vector3(_x[s], _y[s], _z[s]));
                    var c = CountInliers(hn, hd);
                    if (c > bestCount) { bestCount = c; bn = hn; bd = hd; }
                }
                if (bestCount < MinInliers) break;
                // 정제: 인라이어 평균 법선/중심 → 재계수 · 라벨
                var sumN = Vector3.Zero; var sumP = Vector3.Zero; var cnt = 0;
                for (var i = 0; i < n; i++)
                {
                    if (_avail[i] == 0f || !IsInlier(i, bn, bd)) continue;
                    sumN += new Vector3(_nx[i], _ny[i], _nz[i]); sumP += new Vector3(_x[i], _y[i], _z[i]); cnt++;
                }
                var rn = Vector3.Normalize(sumN); var cen = sumP / cnt; var rd = -Vector3.Dot(rn, cen);
                var pl = new SpatialPlane { Normal = rn, D = rd, Centroid = cen };
                Basis(rn, out pl.U, out pl.V);
                pl.UMin = pl.VMin = float.MaxValue; pl.UMax = pl.VMax = float.MinValue;
                // 동일 평면 재검출(잔여 점) → 기존 평면에 병합
                var merge = -1;
                for (var q = 0; q < _planeCount; q++)
                    if (Vector3.Dot(_planes[q].Normal, rn) > 0.98f && MathF.Abs(_planes[q].D - rd) < DistanceThreshold * 2) { merge = q; break; }
                var idx = (sbyte)(merge >= 0 ? merge : _planeCount); var used = 0;
                if (merge >= 0) { pl = _planes[merge]; rn = pl.Normal; rd = pl.D; cen = pl.Centroid; }
                for (var i = 0; i < n; i++)
                {
                    // 소비는 2배 거리 허용(경계 잡음 잔여점이 중복 평면을 만들지 않게) · 범위는 엄격 인라이어만
                    if (_avail[i] == 0f) continue;
                    var di = MathF.Abs(rn.X * _x[i] + rn.Y * _y[i] + rn.Z * _z[i] + rd);
                    if (di >= DistanceThreshold * 2 || rn.X * _nx[i] + rn.Y * _ny[i] + rn.Z * _nz[i] <= NormalThreshold * 0.9f) continue;
                    _avail[i] = 0f; used++;
                    if (di >= DistanceThreshold) { _label[i] = idx; continue; }
                    var r = new Vector3(_x[i], _y[i], _z[i]) - cen;
                    var u = Vector3.Dot(r, pl.U); var v = Vector3.Dot(r, pl.V);
                    pl.UMin = Math.Min(pl.UMin, u); pl.UMax = Math.Max(pl.UMax, u); pl.VMin = Math.Min(pl.VMin, v); pl.VMax = Math.Max(pl.VMax, v);
                    _label[i] = idx;
                }
                if (used == 0)
                {
                    // 병합 대상이 가설 인라이어를 못 덮음 → 가설 인라이어 소비(무한 반복 방지)
                    for (var i = 0; i < n; i++) if (_avail[i] != 0f && IsInlier(i, bn, bd)) { _avail[i] = 0f; _label[i] = -2; used++; }
                    remaining -= Math.Max(1, used);
                    continue;
                }
                remaining -= used;
                if (merge >= 0) { pl.Inliers += used; _planes[merge] = pl; continue; }
                pl.Inliers = used;
                if (used < MinInliers || pl.Area < MinArea)
                {
                    for (var i = 0; i < n; i++) if (_label[i] == idx) _label[i] = -2; // 소비(기각 평면)
                    continue;
                }
                _planes[_planeCount++] = pl;
            }
            Classify();
            Track();
            _detections++;
            LastDetectUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            return _planeCount;
        }
    }

    private bool IsInlier(int i, Vector3 n, float d) =>
        MathF.Abs(n.X * _x[i] + n.Y * _y[i] + n.Z * _z[i] + d) < DistanceThreshold && n.X * _nx[i] + n.Y * _ny[i] + n.Z * _nz[i] > NormalThreshold;

    private int CountInliers(Vector3 n, float d)
    {
        var vx = MemoryMarshal.Cast<float, Vector<float>>(_x.AsSpan()); var vy = MemoryMarshal.Cast<float, Vector<float>>(_y.AsSpan()); var vz = MemoryMarshal.Cast<float, Vector<float>>(_z.AsSpan());
        var wx = MemoryMarshal.Cast<float, Vector<float>>(_nx.AsSpan()); var wy = MemoryMarshal.Cast<float, Vector<float>>(_ny.AsSpan()); var wz = MemoryMarshal.Cast<float, Vector<float>>(_nz.AsSpan());
        var va = MemoryMarshal.Cast<float, Vector<float>>(_avail.AsSpan());
        Vector<float> a = new(n.X), b = new(n.Y), c = new(n.Z), dd = new(d), th = new(DistanceThreshold), nt = new(NormalThreshold);
        var acc = Vector<float>.Zero;
        var blocks = (_n + Vector<float>.Count - 1) / Vector<float>.Count;
        for (var k = 0; k < blocks; k++)
        {
            var dist = Vector.Abs(a * vx[k] + b * vy[k] + c * vz[k] + dd);
            var dot = a * wx[k] + b * wy[k] + c * wz[k];
            var m = Vector.BitwiseAnd(Vector.LessThan(dist, th), Vector.GreaterThan(dot, nt));
            acc += Vector.ConditionalSelect(m, va[k], Vector<float>.Zero);
        }
        return (int)Vector.Dot(acc, Vector<float>.One);
    }

    private int Sample(int n)
    {
        for (var t = 0; t < 64; t++)
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
            var i = (int)(_rng % (ulong)n);
            if (_avail[i] != 0f) return i;
        }
        return -1;
    }

    private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var a = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(a, n));
        v = Vector3.Cross(n, u);
    }

    private void Classify()
    {
        var floorH = float.MaxValue;
        for (var i = 0; i < _planeCount; i++) if (_planes[i].Normal.Y > 0.9f) floorH = Math.Min(floorH, _planes[i].Height);
        for (var i = 0; i < _planeCount; i++)
        {
            ref var p = ref _planes[i];
            var ny = p.Normal.Y;
            p.Kind = ny > 0.9f
                ? (p.Height - floorH < 0.15f ? PlaneKind.Floor : p.Height - floorH is >= 0.55f and <= 1.2f ? PlaneKind.Table : PlaneKind.Other)
                : ny < -0.9f ? PlaneKind.Ceiling
                : MathF.Abs(ny) < 0.25f && p.Area >= 1.0f ? PlaneKind.Wall : PlaneKind.Other;
        }
    }

    private void Track()
    {
        for (var i = 0; i < _planeCount; i++)
        {
            ref var p = ref _planes[i];
            p.Id = 0; p.Seen = 1;
            for (var j = 0; j < _prevCount; j++)
            {
                ref readonly var q = ref _prev[j];
                if (q.Kind != p.Kind || Vector3.Dot(q.Normal, p.Normal) < 0.95f || MathF.Abs(q.D - p.D) > 0.1f) continue;
                var taken = false;
                for (var k = 0; k < i; k++) if (_planes[k].Id == q.Id) { taken = true; break; }
                if (taken) continue;
                p.Id = q.Id; p.Seen = q.Seen + 1;
                break;
            }
            if (p.Id == 0) p.Id = _nextId++;
        }
    }

    // ── 앵커 가드 ──
    /// <summary>1틱(무할당) → 보정 수.</summary>
    public int Guard(float dt)
    {
        lock (_gate)
        {
            var fixes = 0;
            for (var a = 0; a < _anchorCount; a++)
            {
                ref var an = ref _anchors[a];
                an.LastFix = AnchorFix.None;
                // 지지면: 범위 내 상향 수평면 중 앵커 바닥 + 허용치 이하 최고
                var sup = -1; var supH = float.MinValue;
                for (var i = 0; i < _planeCount; i++)
                {
                    ref readonly var p = ref _planes[i];
                    if (p.Kind is not (PlaneKind.Floor or PlaneKind.Table or PlaneKind.Other) || p.Normal.Y < 0.9f) continue;
                    if (!p.Contains(an.Position, -an.Radius * 0.25f)) continue;
                    var h = -(p.D + p.Normal.X * an.Position.X + p.Normal.Z * an.Position.Z) / p.Normal.Y;
                    if (h <= an.Position.Y + Math.Max(Tolerance, an.Height * 0.5f) && h > supH) { supH = h; sup = i; }
                }
                an.SupportPlane = sup;
                if (sup >= 0)
                {
                    var gap = an.Position.Y - supH;
                    if (gap < -Tolerance) { Fix(ref an, a, AnchorFix.FloorPenetration, -gap, sup); an.Position.Y = supH; an.Velocity = Vector3.Zero; fixes++; }
                    else if (an.NeedsSupport && gap > Tolerance)
                    {
                        if (Gravity)
                        {
                            an.Velocity.Y -= 9.81f * dt;
                            an.Position.Y = Math.Max(supH, an.Position.Y + an.Velocity.Y * dt);
                            if (an.Position.Y <= supH) { an.Velocity = Vector3.Zero; Fix(ref an, a, AnchorFix.Levitation, gap, sup); fixes++; }
                        }
                        else { Fix(ref an, a, AnchorFix.Levitation, gap, sup); an.Position.Y = supH; fixes++; }
                    }
                    an.Grounded = MathF.Abs(an.Position.Y - supH) <= Tolerance;
                }
                else if (an.NeedsSupport && _planeCount > 0) { an.Grounded = false; if (an.LastFix == AnchorFix.None) an.LastFix = AnchorFix.NoSupport; }
                // 벽/천장
                var center = an.Position + new Vector3(0, an.Height * 0.5f, 0);
                for (var i = 0; i < _planeCount; i++)
                {
                    ref readonly var p = ref _planes[i];
                    if (p.Kind == PlaneKind.Wall)
                    {
                        if (!p.Contains(center, an.Radius)) continue;
                        var s = p.Distance(center);
                        if (s < an.Radius - Tolerance && s > -1f)
                        {
                            var push = p.Normal * (an.Radius - s);
                            push.Y = 0;
                            an.Position += push; center += push;
                            Fix(ref an, a, AnchorFix.WallPenetration, an.Radius - s, i); fixes++;
                        }
                    }
                    else if (p.Kind == PlaneKind.Ceiling)
                    {
                        var top = an.Position.Y + an.Height;
                        var ch = p.Height;
                        if (top > ch + Tolerance && p.Contains(center, 0)) { Fix(ref an, a, AnchorFix.CeilingPenetration, top - ch, i); an.Position.Y = ch - an.Height; fixes++; }
                    }
                }
                // TSDF 표면(평면 외 가구) 침투
                if (Surface is { } surf)
                {
                    center = an.Position + new Vector3(0, an.Height * 0.5f, 0);
                    var sd = surf.SampleSdf(center);
                    if (sd < an.Radius * 0.5f && sd < surf.Truncation * 0.99f)
                    {
                        var g = surf.Gradient(center);
                        if (g != Vector3.Zero)
                        {
                            g.Y = Math.Max(0, g.Y);
                            an.Position += g * (an.Radius * 0.5f - sd);
                            Fix(ref an, a, AnchorFix.SurfacePenetration, an.Radius * 0.5f - sd, -1); fixes++;
                        }
                    }
                }
            }
            return fixes;
        }
    }

    private void Fix(ref SpatialAnchor an, int a, AnchorFix fix, float amount, int plane)
    {
        an.LastFix = fix;
        var seq = _eventSeq + 1;
        _events[seq % EventCap] = new AnchorEvent(seq, a, fix, amount, plane);
        Interlocked.Exchange(ref _eventSeq, seq);
    }

    public AnchorEvent Event(int iFromNewest)
    {
        var seq = EventSeq - iFromNewest;
        if (seq <= 0 || seq <= EventSeq - EventCap) return default;
        var e = _events[seq % EventCap];
        return e.Seq == seq ? e : default;
    }

    public int EventCount => (int)Math.Min(EventSeq, EventCap);
}
