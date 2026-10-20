using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.SpatialSlam;

/// <summary>
/// [PromptFlow Studio v16.98.0] 🗺️ 라이다 포인트 클라우드 &amp; TSDF 복셀 메쉬 재구성 합성기.
/// · TSDF 볼륨(기본 64×32×56 · 10 cm · 절단 μ = 3 복셀) · 가중 이동 평균(가중 상한).
/// · 깊이 융합: 복셀 x-행 단위 카메라 좌표 Vector&lt;float&gt; 선형 증분 → 투영 → 깊이 샘플 → sdf = d − z.
/// · 라이다 융합: 센서 원점 → 점 광선 ±μ 반 복셀 간격 표본(공간 조각 · 점 수 무관 고정 비용/점).
/// · 메쉬: Naive Surface Nets(셀 부호 변화 → 12 모서리 교차 평균 정점 · 그래디언트 법선 · 모서리 공유 4셀 쿼드 → 삼각형 2) · 사전 할당 상한.
/// · 변환: v16.71 OpenUSD Points 프림(상한 8192점 · 표시 색) · v16.58 3DGS(정점 = 법선 정렬 납작 스플랫).
/// </summary>
public sealed class LidarPointCloudMeshReconstructionSynthesizer
{
    public const int MaxVertices = 60_000, MaxTriangles = 120_000;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly float[] _tsdf, _w;
    private readonly int[] _cellVertex;
    private readonly float[] _rx, _ry, _rz, _ix;
    private readonly float[] _vx = new float[MaxVertices], _vy = new float[MaxVertices], _vz = new float[MaxVertices];
    private readonly float[] _nx = new float[MaxVertices], _ny = new float[MaxVertices], _nz = new float[MaxVertices];
    private readonly int[] _tri = new int[MaxTriangles * 3];
    private int _vertCount, _triCount;
    private long _integrations, _points;

    public LidarPointCloudMeshReconstructionSynthesizer(int nx = 64, int ny = 32, int nz = 56, float voxel = 0.1f, Vector3? origin = null)
    {
        Nx = nx; Ny = ny; Nz = nz; Voxel = voxel;
        Origin = origin ?? new Vector3(-3.2f, -0.1f, -2.8f);
        _tsdf = new float[nx * ny * nz]; _w = new float[nx * ny * nz];
        _cellVertex = new int[(nx - 1) * (ny - 1) * (nz - 1)];
        var pad = (nx + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        _rx = new float[pad]; _ry = new float[pad]; _rz = new float[pad]; _ix = new float[pad];
        for (var i = 0; i < pad; i++) _ix[i] = i;
        Truncation = voxel * 3;
        Reset();
    }

    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    public float Voxel { get; }
    public Vector3 Origin { get; }
    public float Truncation { get; set; }
    public float MaxWeight { get; set; } = 64;
    public float MaxDepth { get; set; } = 6f;
    public long Integrations => Interlocked.Read(ref _integrations);
    public long PointsFused => Interlocked.Read(ref _points);
    public double LastIntegrateUs { get; private set; }
    public double LastExtractUs { get; private set; }
    public int VertexCount => _vertCount;
    public int TriangleCount => _triCount;
    public bool Truncated { get; private set; }

    public ReadOnlySpan<float> VX => _vx.AsSpan(0, _vertCount);
    public ReadOnlySpan<float> VY => _vy.AsSpan(0, _vertCount);
    public ReadOnlySpan<float> VZ => _vz.AsSpan(0, _vertCount);
    public ReadOnlySpan<float> NX => _nx.AsSpan(0, _vertCount);
    public ReadOnlySpan<float> NY => _ny.AsSpan(0, _vertCount);
    public ReadOnlySpan<float> NZ => _nz.AsSpan(0, _vertCount);
    public ReadOnlySpan<int> Triangles => _tri.AsSpan(0, _triCount * 3);
    public Vector3 Vertex(int i) => new(_vx[i], _vy[i], _vz[i]);
    public Vector3 Normal(int i) => new(_nx[i], _ny[i], _nz[i]);

    /// <summary>관측 복셀 수(가중 &gt; 0).</summary>
    public int ObservedVoxels { get { var c = 0; foreach (var w in _w) if (w > 0) c++; return c; } }

    public void Reset()
    {
        Array.Fill(_tsdf, 1f);
        Array.Clear(_w);
        _vertCount = 0; _triCount = 0;
    }

    private int Idx(int x, int y, int z) => (z * Ny + y) * Nx + x;
    public Vector3 VoxelCenter(int x, int y, int z) => Origin + new Vector3((x + 0.5f) * Voxel, (y + 0.5f) * Voxel, (z + 0.5f) * Voxel);

    private void Update(int i, float sdf, float weight)
    {
        var t = Math.Clamp(sdf / Truncation, -1f, 1f);
        var w0 = _w[i];
        var w1 = Math.Min(MaxWeight, w0 + weight);
        _tsdf[i] = (_tsdf[i] * w0 + t * weight) / (w0 + weight);
        _w[i] = w1;
    }

    /// <summary>깊이 프레임 융합(무할당 · 카메라→월드 pose).</summary>
    public void IntegrateDepth(ReadOnlySpan<float> depth, int w, int h, float fx, float fy, float cx, float cy, SlamPose pose)
    {
        var t0 = Stopwatch.GetTimestamp();
        var inv = pose.Inverse();
        var step = inv.Rotate(new Vector3(Voxel, 0, 0));
        var vix = MemoryMarshal.Cast<float, Vector<float>>(_ix.AsSpan());
        var vrx = MemoryMarshal.Cast<float, Vector<float>>(_rx.AsSpan()); var vry = MemoryMarshal.Cast<float, Vector<float>>(_ry.AsSpan()); var vrz = MemoryMarshal.Cast<float, Vector<float>>(_rz.AsSpan());
        Vector<float> sx = new(step.X), sy = new(step.Y), sz = new(step.Z);
        var mu = Truncation;
        for (var z = 0; z < Nz; z++)
            for (var y = 0; y < Ny; y++)
            {
                var c0 = inv.Apply(VoxelCenter(0, y, z));
                Vector<float> bx = new(c0.X), by = new(c0.Y), bz = new(c0.Z);
                for (var k = 0; k < vix.Length; k++) { vrx[k] = bx + vix[k] * sx; vry[k] = by + vix[k] * sy; vrz[k] = bz + vix[k] * sz; }
                var row = (z * Ny + y) * Nx;
                for (var x = 0; x < Nx; x++)
                {
                    var cz = -_rz[x];
                    if (cz <= 0.1f || cz > MaxDepth + mu) continue;
                    var u = (int)(fx * _rx[x] / cz + cx); var v = (int)(-fy * _ry[x] / cz + cy);
                    if ((uint)u >= (uint)w || (uint)v >= (uint)h) continue;
                    var d = depth[v * w + u];
                    if (d <= 0) continue;
                    var sdf = d - cz;
                    if (sdf < -mu) continue;
                    Update(row + x, sdf, 1f);
                }
            }
        Interlocked.Increment(ref _integrations);
        LastIntegrateUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    /// <summary>라이다 점 융합(센서 프레임 SoA → pose → 월드 · 광선 ±μ 표본 · 무할당).</summary>
    public void IntegratePoints(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, int count, SlamPose pose)
    {
        var t0 = Stopwatch.GetTimestamp();
        var o = pose.T;
        var mu = Truncation; var stepL = Voxel * 0.5f;
        var inv = 1f / Voxel;
        for (var i = 0; i < count; i++)
        {
            var p = pose.Apply(new Vector3(x[i], y[i], z[i]));
            var dir = p - o; var len = dir.Length();
            if (len < 0.1f) continue;
            dir /= len;
            for (var s = -mu; s <= mu; s += stepL)
            {
                var q = p - dir * s; // s > 0 = 센서 쪽(자유 공간)
                var g = (q - Origin) * inv;
                int gx = (int)g.X, gy = (int)g.Y, gz = (int)g.Z;
                if ((uint)gx >= (uint)Nx || (uint)gy >= (uint)Ny || (uint)gz >= (uint)Nz) continue;
                Update(Idx(gx, gy, gz), s, 0.5f);
            }
        }
        Interlocked.Add(ref _points, count);
        Interlocked.Increment(ref _integrations);
        LastIntegrateUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    /// <summary>삼선형 sdf(m · 미관측/범위 밖 = +μ).</summary>
    public float SampleSdf(Vector3 p)
    {
        var g = (p - Origin) / Voxel - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(g.X), y0 = (int)MathF.Floor(g.Y), z0 = (int)MathF.Floor(g.Z);
        if (x0 < 0 || y0 < 0 || z0 < 0 || x0 >= Nx - 1 || y0 >= Ny - 1 || z0 >= Nz - 1) return Truncation;
        float fx = g.X - x0, fy = g.Y - y0, fz = g.Z - z0;
        float acc = 0;
        for (var c = 0; c < 8; c++)
        {
            int dx = c & 1, dy = (c >> 1) & 1, dz = c >> 2;
            var i = Idx(x0 + dx, y0 + dy, z0 + dz);
            if (_w[i] <= 0) return Truncation;
            acc += _tsdf[i] * (dx == 1 ? fx : 1 - fx) * (dy == 1 ? fy : 1 - fy) * (dz == 1 ? fz : 1 - fz);
        }
        return acc * Truncation;
    }

    public Vector3 Gradient(Vector3 p)
    {
        var h = Voxel * 0.5f;
        var g = new Vector3(SampleSdf(p + new Vector3(h, 0, 0)) - SampleSdf(p - new Vector3(h, 0, 0)),
                            SampleSdf(p + new Vector3(0, h, 0)) - SampleSdf(p - new Vector3(0, h, 0)),
                            SampleSdf(p + new Vector3(0, 0, h)) - SampleSdf(p - new Vector3(0, 0, h)));
        var l = g.Length();
        return l > 1e-9f ? g / l : Vector3.Zero;
    }

    private static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
    private static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };

    /// <summary>Naive Surface Nets 추출(무할당 · 상한 초과 시 Truncated) → 정점 수.</summary>
    public int ExtractMesh()
    {
        var t0 = Stopwatch.GetTimestamp();
        Array.Fill(_cellVertex, -1);
        _vertCount = 0; _triCount = 0; Truncated = false;
        int cx = Nx - 1, cy = Ny - 1, cz = Nz - 1;
        Span<float> cv = stackalloc float[8];
        for (var z = 0; z < cz; z++)
            for (var y = 0; y < cy; y++)
                for (var x = 0; x < cx; x++)
                {
                    int mask = 0; var ok = true;
                    for (var c = 0; c < 8; c++)
                    {
                        var i = Idx(x + (c & 1), y + ((c >> 1) & 1), z + (c >> 2));
                        if (_w[i] <= 0) { ok = false; break; }
                        cv[c] = _tsdf[i];
                        if (cv[c] < 0) mask |= 1 << c;
                    }
                    if (!ok || mask == 0 || mask == 255) continue;
                    if (_vertCount >= MaxVertices) { Truncated = true; continue; }
                    var acc = Vector3.Zero; var ne = 0;
                    for (var e = 0; e < 12; e++)
                    {
                        int a = EdgeA[e], b = EdgeB[e];
                        if ((cv[a] < 0) == (cv[b] < 0)) continue;
                        var t = cv[a] / (cv[a] - cv[b]);
                        var pa = new Vector3(a & 1, (a >> 1) & 1, a >> 2); var pb = new Vector3(b & 1, (b >> 1) & 1, b >> 2);
                        acc += pa + (pb - pa) * t; ne++;
                    }
                    var local = acc / ne;
                    var wp = VoxelCenter(x, y, z) + local * Voxel;
                    var vi = _vertCount++;
                    _vx[vi] = wp.X; _vy[vi] = wp.Y; _vz[vi] = wp.Z;
                    // 그래디언트 법선(셀 모서리 차분 · 자유 공간 방향)
                    var gx = (cv[1] + cv[3] + cv[5] + cv[7]) - (cv[0] + cv[2] + cv[4] + cv[6]);
                    var gy = (cv[2] + cv[3] + cv[6] + cv[7]) - (cv[0] + cv[1] + cv[4] + cv[5]);
                    var gz = (cv[4] + cv[5] + cv[6] + cv[7]) - (cv[0] + cv[1] + cv[2] + cv[3]);
                    var n = new Vector3(gx, gy, gz); var nl = n.Length();
                    n = nl > 1e-9f ? n / nl : Vector3.UnitY;
                    _nx[vi] = n.X; _ny[vi] = n.Y; _nz[vi] = n.Z;
                    _cellVertex[(z * cy + y) * cx + x] = vi;
                }
        // 면: 복셀 모서리(x/y/z 방향) 부호 변화 → 공유 4셀 쿼드
        for (var z = 1; z < cz; z++)
            for (var y = 1; y < cy; y++)
                for (var x = 1; x < cx; x++)
                {
                    var i0 = Idx(x, y, z);
                    if (_w[i0] <= 0) continue;
                    var s0 = _tsdf[i0] < 0;
                    for (var axis = 0; axis < 3; axis++)
                    {
                        int ax = axis == 0 ? 1 : 0, ay = axis == 1 ? 1 : 0, az = axis == 2 ? 1 : 0;
                        if (x + ax >= Nx || y + ay >= Ny || z + az >= Nz) continue;
                        var i1 = Idx(x + ax, y + ay, z + az);
                        if (_w[i1] <= 0 || s0 == (_tsdf[i1] < 0)) continue;
                        // 모서리 주위 4셀: 축에 수직인 두 방향으로 (0,−1)
                        int ux = axis == 1 ? 1 : axis == 2 ? 1 : 0, uy = axis == 0 ? 1 : 0, uz = 0;
                        int vx = 0, vy = axis == 2 ? 1 : 0, vz = axis == 2 ? 0 : 1;
                        var c00 = Cell(x, y, z); var c10 = Cell(x - ux, y - uy, z - uz);
                        var c11 = Cell(x - ux - vx, y - uy - vy, z - uz - vz); var c01 = Cell(x - vx, y - vy, z - vz);
                        if (c00 < 0 || c10 < 0 || c11 < 0 || c01 < 0) continue;
                        if (_triCount + 2 > MaxTriangles) { Truncated = true; continue; }
                        if (s0) { Tri(c00, c10, c11); Tri(c00, c11, c01); }
                        else { Tri(c00, c11, c10); Tri(c00, c01, c11); }
                    }
                }
        LastExtractUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
        return _vertCount;
    }

    private int Cell(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= Nx - 1 || y >= Ny - 1 || z >= Nz - 1) return -1;
        return _cellVertex[(z * (Ny - 1) + y) * (Nx - 1) + x];
    }

    private void Tri(int a, int b, int c)
    {
        var o = _triCount * 3;
        _tri[o] = a; _tri[o + 1] = b; _tri[o + 2] = c;
        _triCount++;
    }

    /// <summary>표시 색(높이 그라디언트 · 법선 음영).</summary>
    public static Vector3 ShadeColor(Vector3 p, Vector3 n)
    {
        var h = Math.Clamp(p.Y / 3f, 0f, 1f);
        var baseC = Vector3.Lerp(new Vector3(0.25f, 0.55f, 0.85f), new Vector3(0.95f, 0.75f, 0.35f), h);
        return baseC * (0.45f + 0.55f * MathF.Abs(Vector3.Dot(n, Vector3.Normalize(new Vector3(0.3f, 0.9f, 0.4f)))));
    }

    /// <summary>OpenUSD 내보내기: /World/SlamMesh(Points · 균등 솎음 ≤ maxPoints) → 내보낸 점 수.</summary>
    public int ExportUsd(UsdLayer layer, int maxPoints = 8000)
    {
        var world = layer.AddPath("/World", UsdSpec.Def, UsdPrimType.Xform);
        var prim = layer.AddPath("/World/SlamMesh", UsdSpec.Def, UsdPrimType.Points);
        if (world < 0 || prim < 0 || _vertCount == 0) return 0;
        var cap = Math.Min(maxPoints, (UsdLayer.MaxPointFloats - layer.PtLen) / 3);
        var step = Math.Max(1, (_vertCount + cap - 1) / Math.Max(1, cap));
        var n = 0;
        layer.BeginPoints(prim);
        for (var i = 0; i < _vertCount && n < cap; i += step)
        {
            if (!layer.AppendPoint(prim, _vx[i], _vy[i], _vz[i])) break;
            n++;
        }
        layer.SetColor(prim, 0.7f, 0.75f, 0.8f);
        return n;
    }

    /// <summary>3DGS 변환: 정점 → 법선 정렬 납작 스플랫(두께 15%) → 추가 수.</summary>
    public int ExportGaussians(GaussianScene gs, int maxSplats = 20000)
    {
        var step = Math.Max(1, (_vertCount + maxSplats - 1) / Math.Max(1, maxSplats));
        var count = Math.Min(maxSplats, (_vertCount + step - 1) / step);
        gs.EnsureCapacity(gs.Count + count);
        var n = 0;
        for (var i = 0; i < _vertCount && n < maxSplats; i += step)
        {
            var p = Vertex(i); var nn = Normal(i);
            var q = FromTo(Vector3.UnitZ, nn);
            var s = Voxel * 0.6f;
            gs.Add(p, q, new Vector3(s, s, s * 0.15f), 0.85f, ShadeColor(p, nn));
            n++;
        }
        return n;
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.9999f) return Quaternion.Identity;
        if (d < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var ax = Vector3.Normalize(Vector3.Cross(a, b));
        return Quaternion.CreateFromAxisAngle(ax, MathF.Acos(d));
    }
}
