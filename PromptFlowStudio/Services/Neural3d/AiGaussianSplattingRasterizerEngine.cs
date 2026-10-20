using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Spatial;

namespace PromptFlowStudio.Services.Neural3d;

/// <summary>
/// 핀홀 카메라(월드 Y 상향 · 카메라 x 우 / y 상 / z 전방). 투영 u = fx·x/z + cx, v = cy − fy·y/z (영상 y 하향).
/// </summary>
public readonly struct SplatCamera
{
    public readonly Vector3 Position, Right, Up, Forward;
    public readonly float Fx, Fy, Cx, Cy;
    public readonly int Width, Height;

    public SplatCamera(Vector3 position, Vector3 right, Vector3 up, Vector3 forward, float fx, float fy, float cx, float cy, int width, int height)
    {
        Position = position; Right = right; Up = up; Forward = forward;
        Fx = fx; Fy = fy; Cx = cx; Cy = cy; Width = width; Height = height;
    }

    /// <summary>요/피치/롤(라디안) — 요 0·피치 0 = +Z 응시.</summary>
    public static SplatCamera FromYawPitch(Vector3 position, float yaw, float pitch, float roll, float hfovDeg, int width, int height)
    {
        var f = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var r = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        var u = Vector3.Cross(f, r);
        if (roll != 0f)
        {
            float c = MathF.Cos(roll), s = MathF.Sin(roll);
            var r2 = r * c + u * s;
            u = u * c - r * s;
            r = r2;
        }
        var fx = width * 0.5f / MathF.Tan(Math.Clamp(hfovDeg, 5f, 170f) * MathF.PI / 360f);
        return new SplatCamera(position, r, u, f, fx, fx, width * 0.5f, height * 0.5f, width, height);
    }

    public static SplatCamera LookAt(Vector3 position, Vector3 target, float hfovDeg, int width, int height, float roll = 0f)
    {
        var d = Vector3.Normalize(target - position);
        return FromYawPitch(position, MathF.Atan2(d.X, d.Z), MathF.Asin(Math.Clamp(d.Y, -1f, 1f)), roll, hfovDeg, width, height);
    }

    /// <summary>피벗 주위 오빗(각도 deg · 반경 m · 높이 m).</summary>
    public static SplatCamera Orbit(Vector3 pivot, float azimuthDeg, float elevationDeg, float radius, float hfovDeg, int width, int height)
    {
        float az = azimuthDeg * MathF.PI / 180f, el = elevationDeg * MathF.PI / 180f;
        var pos = pivot + new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el)) * radius;
        return LookAt(pos, pivot, hfovDeg, width, height);
    }

    public Vector3 ToCamera(Vector3 p) { var d = p - Position; return new Vector3(Vector3.Dot(d, Right), Vector3.Dot(d, Up), Vector3.Dot(d, Forward)); }
    public Vector3 ToWorld(Vector3 c) => Position + Right * c.X + Up * c.Y + Forward * c.Z;
    public Vector2 Project(Vector3 c) => new(Fx * c.X / c.Z + Cx, Cy - Fy * c.Y / c.Z);
    /// <summary>픽셀 + 카메라 z 깊이 → 월드.</summary>
    public Vector3 Unproject(float px, float py, float z) => ToWorld(new Vector3((px - Cx) * z / Fx, (Cy - py) * z / Fy, z));

    public SplatCamera WithPose(Vector3 position, Vector3 right, Vector3 up, Vector3 forward) => new(position, right, up, forward, Fx, Fy, Cx, Cy, Width, Height);
}

/// <summary>
/// 3D 가우시안 장면(SoA · 용량 선할당). 회전 = 단위 쿼터니언(w,x,y,z) · 스케일 = 선형 표준편차(m) · 불투명도 0..1 ·
/// 색 = 구면 조화(SH) 1차 4계수 × RGB(계수 k · 채널 c → Sh[i·12 + k·3 + c] · DC 는 색 0.5 오프셋 규약).
/// </summary>
public sealed class GaussianScene
{
    public const int ShStride = 12;
    public const float ShC0 = 0.28209479f, ShC1 = 0.48860251f;
    public float[] Pos = Array.Empty<float>(), Rot = Array.Empty<float>(), Scale = Array.Empty<float>(), Opacity = Array.Empty<float>(), Sh = Array.Empty<float>();
    public int Count;
    public long Version;

    public GaussianScene(int capacity = 0) => EnsureCapacity(capacity);

    public int Capacity => Opacity.Length;

    /// <summary>용량 확보(초과 시에만 재할당 · 기존 데이터 보존).</summary>
    public void EnsureCapacity(int n)
    {
        if (n <= Opacity.Length) return;
        var cap = Math.Max(n, Opacity.Length * 2);
        Array.Resize(ref Pos, cap * 3); Array.Resize(ref Rot, cap * 4); Array.Resize(ref Scale, cap * 3);
        Array.Resize(ref Opacity, cap); Array.Resize(ref Sh, cap * ShStride);
    }

    public void Clear() { Count = 0; Version++; }

    public static float ToDc(float color01) => (color01 - 0.5f) / ShC0;

    /// <summary>가우시안 추가(색 0..1 · SH 1차 계수 0). 반환 = 인덱스.</summary>
    public int Add(Vector3 p, Quaternion q, Vector3 scale, float opacity, Vector3 rgb)
    {
        EnsureCapacity(Count + 1);
        var i = Count++;
        Pos[i * 3] = p.X; Pos[i * 3 + 1] = p.Y; Pos[i * 3 + 2] = p.Z;
        q = Quaternion.Normalize(q);
        Rot[i * 4] = q.W; Rot[i * 4 + 1] = q.X; Rot[i * 4 + 2] = q.Y; Rot[i * 4 + 3] = q.Z;
        Scale[i * 3] = scale.X; Scale[i * 3 + 1] = scale.Y; Scale[i * 3 + 2] = scale.Z;
        Opacity[i] = Math.Clamp(opacity, 0f, 1f);
        var s = Sh.AsSpan(i * ShStride, ShStride);
        s.Clear();
        s[0] = ToDc(rgb.X); s[1] = ToDc(rgb.Y); s[2] = ToDc(rgb.Z);
        Version++;
        return i;
    }

    public Vector3 Position(int i) => new(Pos[i * 3], Pos[i * 3 + 1], Pos[i * 3 + 2]);
    public Quaternion Rotation(int i) => new(Rot[i * 4 + 1], Rot[i * 4 + 2], Rot[i * 4 + 3], Rot[i * 4]);
    public Vector3 ScaleOf(int i) => new(Scale[i * 3], Scale[i * 3 + 1], Scale[i * 3 + 2]);
    public Vector3 BaseColor(int i) => new(0.5f + ShC0 * Sh[i * ShStride], 0.5f + ShC0 * Sh[i * ShStride + 1], 0.5f + ShC0 * Sh[i * ShStride + 2]);

    /// <summary>시점 방향 SH 1차 색 평가(0..∞ 클램프 하한 0).</summary>
    public Vector3 EvalSh(int i, Vector3 dir)
    {
        var o = i * ShStride;
        float x = dir.X, y = dir.Y, z = dir.Z;
        float r = ShC0 * Sh[o] - ShC1 * y * Sh[o + 3] + ShC1 * z * Sh[o + 6] - ShC1 * x * Sh[o + 9] + 0.5f;
        float g = ShC0 * Sh[o + 1] - ShC1 * y * Sh[o + 4] + ShC1 * z * Sh[o + 7] - ShC1 * x * Sh[o + 10] + 0.5f;
        float b = ShC0 * Sh[o + 2] - ShC1 * y * Sh[o + 5] + ShC1 * z * Sh[o + 8] - ShC1 * x * Sh[o + 11] + 0.5f;
        return new Vector3(MathF.Max(0f, r), MathF.Max(0f, g), MathF.Max(0f, b));
    }

    public void CopyTo(GaussianScene dst)
    {
        dst.EnsureCapacity(Count);
        Pos.AsSpan(0, Count * 3).CopyTo(dst.Pos); Rot.AsSpan(0, Count * 4).CopyTo(dst.Rot); Scale.AsSpan(0, Count * 3).CopyTo(dst.Scale);
        Opacity.AsSpan(0, Count).CopyTo(dst.Opacity); Sh.AsSpan(0, Count * ShStride).CopyTo(dst.Sh);
        dst.Count = Count;
        dst.Version++;
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Count == 0) return (Vector3.Zero, Vector3.Zero);
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        for (var i = 0; i < Count; i++) { var p = Position(i); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }
}

/// <summary>렌더 옵션 — 클리핑(근/원 · 크롭 박스 · 최소 불투명도) · 밀도(표시 비율) · 스케일 배율 · 배경.</summary>
public readonly record struct SplatRenderOptions(float Near = 0.05f, float Far = 100f, float MinOpacity = 0.004f, float Density = 1f, float ScaleMultiplier = 1f,
    Vector3 Background = default, bool UseCrop = false, Vector3 CropMin = default, Vector3 CropMax = default)
{
    public static readonly SplatRenderOptions Default = new(Near: 0.05f);   // 주의: new() 는 기본 매개변수 미적용(전부 0)
}

/// <summary>
/// [PromptFlow Studio v16.58.0] 🧊 실시간 3D 가우시안 스플래팅 신경망 라스터라이저(Pure BCL · 무할당 프레임 · SIMD 타일).
///   ▸ 전처리: 시야/근원/크롭/불투명도 클리핑 · 밀도 샘플링 → 3D 공분산 RΣSᵀRᵀ → EWA 2D 투영(J·W·Σ·Wᵀ·Jᵀ + 0.3 저역 필터) → 코닉 · 3σ 반경 · SH 1차 시점 색.
///   ▸ 타일 비닝: 16×16 타일 · (타일 « 32 | 깊이 비트) 64-bit 키 → 인-플레이스 정렬(무할당) → 타일 범위 테이블.
///   ▸ 블렌딩: 타일 내 가우시안 전→후 알파 합성 · 가로 16픽셀 지수부 <see cref="Vector{T}"/> SIMD 평가 · 픽셀별 투과율 조기 종료 · 기대 깊이/알파 버퍼.
///   ▸ 복원: 다시점 RGB(+깊이, 없으면 v16.31 단안 깊이 추정) → 복셀 융합 → PCA 비등방 공분산(자코비 고유분해) → 다시점 일관성 불투명도 → SH 1차 최소제곱 적합.
///   ▸ 호환: Inria 3DGS 표준 binary PLY 입·출력(f_dc · f_rest 0..8 · logit 불투명도 · log 스케일 · 쿼터니언 · Y 축 반전).
/// </summary>
public sealed class AiGaussianSplattingRasterizerEngine
{
    public const int Tile = 16, TilePixels = Tile * Tile;
    private const float AlphaMin = 1f / 255f, TransmittanceStop = 1e-4f;

    private readonly object _gate = new();
    private float[] _sx = Array.Empty<float>(), _sy = Array.Empty<float>(), _ca = Array.Empty<float>(), _cb = Array.Empty<float>(), _cc = Array.Empty<float>();
    private float[] _cr = Array.Empty<float>(), _cg = Array.Empty<float>(), _cbl = Array.Empty<float>(), _depth = Array.Empty<float>(), _op = Array.Empty<float>();
    private int[] _rect = Array.Empty<int>();
    private ulong[] _keys = Array.Empty<ulong>();
    private int[] _vals = Array.Empty<int>();
    private int[] _ranges = Array.Empty<int>();
    private readonly float[] _pT = new float[TilePixels], _pR = new float[TilePixels], _pG = new float[TilePixels], _pB = new float[TilePixels], _pD = new float[TilePixels];
    private readonly float[] _laneX = new float[Tile];
    private long _frames;
    private int _visible, _instances, _culled, _capacityGrows;
    private double _renderMs;

    public AiGaussianSplattingRasterizerEngine() { for (var i = 0; i < Tile; i++) _laneX[i] = i + 0.5f; }

    public event Action? OnChange;

    public long Frames => Interlocked.Read(ref _frames);
    public int LastVisible => Volatile.Read(ref _visible);
    public int LastInstances => Volatile.Read(ref _instances);
    public int LastCulled => Volatile.Read(ref _culled);
    public int CapacityGrows => Volatile.Read(ref _capacityGrows);
    public double LastRenderMs => Volatile.Read(ref _renderMs);
    public string? LastAction { get; private set; }

    private void EnsureWork(int n, int tiles)
    {
        if (_sx.Length < n)
        {
            var cap = Math.Max(n, _sx.Length * 2);
            _sx = new float[cap]; _sy = new float[cap]; _ca = new float[cap]; _cb = new float[cap]; _cc = new float[cap];
            _cr = new float[cap]; _cg = new float[cap]; _cbl = new float[cap]; _depth = new float[cap]; _op = new float[cap];
            _rect = new int[cap * 4];
            Interlocked.Increment(ref _capacityGrows);
        }
        if (_ranges.Length < tiles * 2) { _ranges = new int[tiles * 2]; Interlocked.Increment(ref _capacityGrows); }
    }

    private void EnsureKeys(int n)
    {
        if (_keys.Length >= n) return;
        var cap = Math.Max(n, _keys.Length * 2);
        _keys = new ulong[cap]; _vals = new int[cap];
        Interlocked.Increment(ref _capacityGrows);
    }

    private static uint Hash(uint x) { x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16; return x; }

    /// <summary>
    /// 장면 렌더(무할당 · 작업 버퍼는 용량 증가 시에만 확장). rgb = W×H×3 · depth/alpha(선택) = W×H.
    /// 반환 = 가시 가우시안 수.
    /// </summary>
    public int Render(GaussianScene scene, in SplatCamera cam, in SplatRenderOptions opt, Span<byte> rgb, Span<float> depth = default, Span<float> alpha = default)
    {
        int w = cam.Width, h = cam.Height;
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) return 0;
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            int tx = (w + Tile - 1) / Tile, ty = (h + Tile - 1) / Tile, tiles = tx * ty;
            var n = scene.Count;
            EnsureWork(n, tiles);
            int visible = 0, culled = 0;
            long instances = 0;
            var densityCut = (uint)(Math.Clamp(opt.Density, 0f, 1f) * uint.MaxValue);
            var sm = MathF.Max(0.01f, opt.ScaleMultiplier);
            var limX = 1.3f * (w * 0.5f / cam.Fx);
            var limY = 1.3f * (h * 0.5f / cam.Fy);
            var vis = _rect;
            for (var i = 0; i < n; i++)
            {
                vis[i * 4] = -1;
                var op = scene.Opacity[i];
                if (op < opt.MinOpacity || (opt.Density < 1f && Hash((uint)i) > densityCut)) { culled++; continue; }
                var p = scene.Position(i);
                if (opt.UseCrop && (p.X < opt.CropMin.X || p.Y < opt.CropMin.Y || p.Z < opt.CropMin.Z || p.X > opt.CropMax.X || p.Y > opt.CropMax.Y || p.Z > opt.CropMax.Z)) { culled++; continue; }
                var c = cam.ToCamera(p);
                if (c.Z < opt.Near || c.Z > opt.Far) { culled++; continue; }

                // 3D 공분산 Σ = R S Sᵀ Rᵀ (카메라 좌표로 회전: W R)
                var q = scene.Rotation(i);
                float qw = q.W, qx = q.X, qy = q.Y, qz = q.Z;
                float r00 = 1 - 2 * (qy * qy + qz * qz), r01 = 2 * (qx * qy - qw * qz), r02 = 2 * (qx * qz + qw * qy);
                float r10 = 2 * (qx * qy + qw * qz), r11 = 1 - 2 * (qx * qx + qz * qz), r12 = 2 * (qy * qz - qw * qx);
                float r20 = 2 * (qx * qz - qw * qy), r21 = 2 * (qy * qz + qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);
                float s0 = scene.Scale[i * 3] * sm, s1 = scene.Scale[i * 3 + 1] * sm, s2 = scene.Scale[i * 3 + 2] * sm;
                // M = W · R · S (열 = 스케일된 축의 카메라 좌표)
                Vector3 a0 = new Vector3(r00, r10, r20) * s0, a1 = new Vector3(r01, r11, r21) * s1, a2 = new Vector3(r02, r12, r22) * s2;
                Vector3 m0 = new(Vector3.Dot(a0, cam.Right), Vector3.Dot(a0, cam.Up), Vector3.Dot(a0, cam.Forward));
                Vector3 m1 = new(Vector3.Dot(a1, cam.Right), Vector3.Dot(a1, cam.Up), Vector3.Dot(a1, cam.Forward));
                Vector3 m2 = new(Vector3.Dot(a2, cam.Right), Vector3.Dot(a2, cam.Up), Vector3.Dot(a2, cam.Forward));
                // 야코비안(시야 밖 클램프)
                var z = c.Z;
                var xz = Math.Clamp(c.X / z, -limX, limX) * z;
                var yz = Math.Clamp(c.Y / z, -limY, limY) * z;
                float j00 = cam.Fx / z, j02 = -cam.Fx * xz / (z * z), j11 = -cam.Fy / z, j12 = cam.Fy * yz / (z * z);
                // T = J · M  (2×3) → Σ2 = T Tᵀ
                float t00 = j00 * m0.X + j02 * m0.Z, t01 = j00 * m1.X + j02 * m1.Z, t02 = j00 * m2.X + j02 * m2.Z;
                float t10 = j11 * m0.Y + j12 * m0.Z, t11 = j11 * m1.Y + j12 * m1.Z, t12 = j11 * m2.Y + j12 * m2.Z;
                var ca = t00 * t00 + t01 * t01 + t02 * t02 + 0.3f;
                var cb = t00 * t10 + t01 * t11 + t02 * t12;
                var cc = t10 * t10 + t11 * t11 + t12 * t12 + 0.3f;
                var det = ca * cc - cb * cb;
                if (det <= 1e-12f) { culled++; continue; }
                var mid = 0.5f * (ca + cc);
                var lam = mid + MathF.Sqrt(MathF.Max(0.1f, mid * mid - det));
                var rad = MathF.Ceiling(3f * MathF.Sqrt(lam));
                var uv = cam.Project(c);
                var minX = (int)MathF.Floor((uv.X - rad) / Tile); var maxX = (int)MathF.Floor((uv.X + rad) / Tile);
                var minY = (int)MathF.Floor((uv.Y - rad) / Tile); var maxY = (int)MathF.Floor((uv.Y + rad) / Tile);
                minX = Math.Max(minX, 0); minY = Math.Max(minY, 0); maxX = Math.Min(maxX, tx - 1); maxY = Math.Min(maxY, ty - 1);
                if (minX > maxX || minY > maxY) { culled++; continue; }
                var inv = 1f / det;
                _sx[i] = uv.X; _sy[i] = uv.Y; _ca[i] = cc * inv; _cb[i] = -cb * inv; _cc[i] = ca * inv;
                var col = scene.EvalSh(i, Vector3.Normalize(p - cam.Position));
                _cr[i] = col.X; _cg[i] = col.Y; _cbl[i] = col.Z; _depth[i] = z; _op[i] = op;
                vis[i * 4] = minX; vis[i * 4 + 1] = minY; vis[i * 4 + 2] = maxX; vis[i * 4 + 3] = maxY;
                instances += (long)(maxX - minX + 1) * (maxY - minY + 1);
                visible++;
            }
            if (instances > int.MaxValue / 2) instances = int.MaxValue / 2;
            EnsureKeys((int)instances);
            var k = 0;
            for (var i = 0; i < n; i++)
            {
                if (vis[i * 4] < 0) continue;
                var db = (ulong)BitConverter.SingleToUInt32Bits(_depth[i]);
                for (var yy = vis[i * 4 + 1]; yy <= vis[i * 4 + 3]; yy++)
                    for (var xx = vis[i * 4]; xx <= vis[i * 4 + 2]; xx++)
                    {
                        _keys[k] = ((ulong)(uint)(yy * tx + xx) << 32) | db;
                        _vals[k++] = i;
                    }
            }
            _keys.AsSpan(0, k).Sort(_vals.AsSpan(0, k));
            var ranges = _ranges.AsSpan(0, tiles * 2);
            ranges.Clear();
            for (var e = 0; e < k; e++)
            {
                var t = (int)(_keys[e] >> 32);
                if (e == 0 || (int)(_keys[e - 1] >> 32) != t) ranges[t * 2] = e;
                ranges[t * 2 + 1] = e + 1;
            }
            var bg = opt.Background;
            for (var t = 0; t < tiles; t++) BlendTile(t, tx, w, h, ranges[t * 2], ranges[t * 2 + 1], bg, rgb, depth, alpha);
            Volatile.Write(ref _visible, visible);
            Volatile.Write(ref _instances, k);
            Volatile.Write(ref _culled, culled);
        }
        Volatile.Write(ref _renderMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        if ((Interlocked.Increment(ref _frames) & 15) == 0) Raise();
        return Volatile.Read(ref _visible);
    }

    private void BlendTile(int t, int tx, int w, int h, int start, int end, Vector3 bg, Span<byte> rgb, Span<float> depthOut, Span<float> alphaOut)
    {
        int ox = (t % tx) * Tile, oy = (t / tx) * Tile;
        int tw = Math.Min(Tile, w - ox), th = Math.Min(Tile, h - oy);
        var T = _pT.AsSpan(); var R = _pR.AsSpan(); var G = _pG.AsSpan(); var B = _pB.AsSpan(); var D = _pD.AsSpan();
        T.Fill(1f); R.Clear(); G.Clear(); B.Clear(); D.Clear();
        var done = 0;
        var vw = Vector<float>.Count;
        Span<float> power = stackalloc float[Tile];
        var lane = _laneX.AsSpan();
        for (var e = start; e < end && done < tw * th; e++)
        {
            var i = _vals[e];
            float gx = _sx[i] - ox, gy = _sy[i] - oy, A = _ca[i], Bc = _cb[i], C = _cc[i], op = _op[i];
            var vA = new Vector<float>(-0.5f * A);
            var vBx = new Vector<float>(-Bc);
            var vGx = new Vector<float>(gx);
            for (var py = 0; py < th; py++)
            {
                var dy = py + 0.5f - gy;
                var cRow = -0.5f * C * dy * dy;
                var vRow = new Vector<float>(cRow);
                var vBdy = vBx * new Vector<float>(dy);
                var px = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    for (; px + vw <= Tile; px += vw)
                    {
                        var dx = new Vector<float>(lane[px..]) - vGx;
                        (vA * dx * dx + vRow + vBdy * dx).CopyTo(power[px..]);
                    }
                }
                for (; px < Tile; px++)
                {
                    var dx = lane[px] - gx;
                    power[px] = -0.5f * A * dx * dx + cRow - Bc * dx * dy;
                }
                var row = py * Tile;
                for (px = 0; px < tw; px++)
                {
                    var pw = power[px];
                    if (pw > 0f || pw < -12f) continue;
                    var ti = row + px;
                    var tr = T[ti];
                    if (tr < TransmittanceStop) continue;
                    var a = MathF.Min(0.99f, op * MathF.Exp(pw));
                    if (a < AlphaMin) continue;
                    var wgt = a * tr;
                    R[ti] += _cr[i] * wgt; G[ti] += _cg[i] * wgt; B[ti] += _cbl[i] * wgt; D[ti] += _depth[i] * wgt;
                    var nt = tr * (1f - a);
                    T[ti] = nt;
                    if (nt < TransmittanceStop) done++;
                }
            }
        }
        for (var py = 0; py < th; py++)
        {
            for (var px = 0; px < tw; px++)
            {
                var ti = py * Tile + px;
                var pix = (oy + py) * w + ox + px;
                var tr = T[ti];
                var o = pix * 3;
                rgb[o] = (byte)Math.Clamp((int)((R[ti] + tr * bg.X) * 255f + 0.5f), 0, 255);
                rgb[o + 1] = (byte)Math.Clamp((int)((G[ti] + tr * bg.Y) * 255f + 0.5f), 0, 255);
                rgb[o + 2] = (byte)Math.Clamp((int)((B[ti] + tr * bg.Z) * 255f + 0.5f), 0, 255);
                var acc = 1f - tr;
                if (!depthOut.IsEmpty) depthOut[pix] = acc > 0.5f ? D[ti] / acc : 0f;
                if (!alphaOut.IsEmpty) alphaOut[pix] = acc;
            }
        }
    }

    // ══════════════════════ 다시점 복원 ══════════════════════

    /// <summary>다시점 입력 — RGB24 · 카메라 · 카메라 z 깊이(m · 0 = 무효 · null 이면 단안 추정).</summary>
    public sealed record SplatView(byte[] Rgb, SplatCamera Camera, float[]? Depth = null);

    public sealed record ReconstructionReport(int Views, int Points, int Voxels, int Gaussians, int Pruned, double Seconds, string Message);

    private sealed class VoxelAcc
    {
        public double Sx, Sy, Sz, Sxx, Sxy, Sxz, Syy, Syz, Szz;
        public int N;
        public double R, G, B;
    }

    /// <summary>
    /// 다시점 → 가우시안 복원. voxel = 융합 격자(m) · step = 픽셀 간격 · depthTolerance = 다시점 일관성 허용 오차(m).
    /// 단안 추정 깊이는 상대값이므로 nearMeters..farMeters 로 사상.
    /// </summary>
    public ReconstructionReport Reconstruct(IReadOnlyList<SplatView> views, GaussianScene dst, float voxel = 0.02f, int step = 1, float depthTolerance = 0.03f,
        float nearMeters = 0.5f, float farMeters = 6f, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        dst.Clear();
        if (views.Count == 0) return new(0, 0, 0, 0, 0, 0, "입력 시점 없음");
        var depths = new float[views.Count][];
        for (var v = 0; v < views.Count; v++)
        {
            var cam = views[v].Camera;
            if (views[v].Depth is { } d) { depths[v] = d; continue; }
            var rel = new float[cam.Width * cam.Height];
            var scratch = new float[rel.Length];
            Spatial3dGaussianSplattingEngine.EstimateDepth(views[v].Rgb, cam.Width, cam.Height, rel, scratch);
            for (var k = 0; k < rel.Length; k++) rel[k] = nearMeters + Math.Clamp(rel[k], 0f, 1f) * (farMeters - nearMeters);
            depths[v] = rel;
        }
        var map = new Dictionary<long, int>();
        var accs = new List<VoxelAcc>();
        var inv = 1f / voxel;
        long points = 0;
        step = Math.Max(1, step);
        for (var v = 0; v < views.Count; v++)
        {
            ct.ThrowIfCancellationRequested();
            var cam = views[v].Camera; var rgb = views[v].Rgb; var d = depths[v];
            for (var y = 0; y < cam.Height; y += step)
                for (var x = 0; x < cam.Width; x += step)
                {
                    var z = d[y * cam.Width + x];
                    if (z <= 0f) continue;
                    var p = cam.Unproject(x + 0.5f, y + 0.5f, z);
                    long kx = (long)MathF.Floor(p.X * inv) + 1_000_000, ky = (long)MathF.Floor(p.Y * inv) + 1_000_000, kz = (long)MathF.Floor(p.Z * inv) + 1_000_000;
                    var key = (kx << 42) | (ky << 21) | kz;
                    if (!map.TryGetValue(key, out var idx)) { idx = accs.Count; accs.Add(new VoxelAcc()); map[key] = idx; }
                    var a = accs[idx];
                    a.Sx += p.X; a.Sy += p.Y; a.Sz += p.Z;
                    a.Sxx += p.X * p.X; a.Sxy += p.X * p.Y; a.Sxz += p.X * p.Z; a.Syy += p.Y * p.Y; a.Syz += p.Y * p.Z; a.Szz += p.Z * p.Z;
                    var o = (y * cam.Width + x) * 3;
                    a.R += rgb[o] / 255.0; a.G += rgb[o + 1] / 255.0; a.B += rgb[o + 2] / 255.0;
                    a.N++;
                    points++;
                }
        }
        Span<double> cov = stackalloc double[9];
        Span<double> evec = stackalloc double[9];
        Span<double> eval = stackalloc double[3];
        Span<double> ata = stackalloc double[16];
        Span<double> atb = stackalloc double[12];
        Span<double> basis = stackalloc double[4];
        var pruned = 0;
        dst.EnsureCapacity(accs.Count);
        foreach (var a in accs)
        {
            if (a.N < 2) { pruned++; continue; }
            var mean = new Vector3((float)(a.Sx / a.N), (float)(a.Sy / a.N), (float)(a.Sz / a.N));
            // 다시점 일관성 + SH 적합
            int expected = 0, observed = 0;
            ata.Clear(); atb.Clear();
            for (var v = 0; v < views.Count; v++)
            {
                var cam = views[v].Camera;
                var c = cam.ToCamera(mean);
                if (c.Z <= 0.01f) continue;
                var uv = cam.Project(c);
                int px = (int)uv.X, py = (int)uv.Y;
                if (px < 0 || py < 0 || px >= cam.Width || py >= cam.Height) continue;
                var dz = depths[v][py * cam.Width + px];
                if (dz <= 0f || dz < c.Z - depthTolerance) continue;   // 가려짐
                expected++;
                if (MathF.Abs(dz - c.Z) > depthTolerance) continue;
                observed++;
                var dir = Vector3.Normalize(mean - cam.Position);
                basis[0] = GaussianScene.ShC0; basis[1] = -GaussianScene.ShC1 * dir.Y; basis[2] = GaussianScene.ShC1 * dir.Z; basis[3] = -GaussianScene.ShC1 * dir.X;
                var o = (py * cam.Width + px) * 3;
                for (var r = 0; r < 4; r++)
                {
                    for (var s = 0; s < 4; s++) ata[r * 4 + s] += basis[r] * basis[s];
                    for (var ch = 0; ch < 3; ch++) atb[r * 3 + ch] += basis[r] * (views[v].Rgb[o + ch] / 255.0 - 0.5);
                }
            }
            if (observed == 0 || expected == 0) { pruned++; continue; }
            var opacity = (float)Math.Clamp(0.97 * observed / expected, 0.05, 0.97);
            if (opacity < 0.25f) { pruned++; continue; }
            // 공분산 PCA
            double n = a.N;
            cov[0] = a.Sxx / n - mean.X * (double)mean.X; cov[1] = a.Sxy / n - mean.X * (double)mean.Y; cov[2] = a.Sxz / n - mean.X * (double)mean.Z;
            cov[4] = a.Syy / n - mean.Y * (double)mean.Y; cov[5] = a.Syz / n - mean.Y * (double)mean.Z; cov[8] = a.Szz / n - mean.Z * (double)mean.Z;
            cov[3] = cov[1]; cov[6] = cov[2]; cov[7] = cov[5];
            JacobiEigen(cov, eval, evec);
            var e0 = new Vector3((float)evec[0], (float)evec[3], (float)evec[6]);
            var e1 = new Vector3((float)evec[1], (float)evec[4], (float)evec[7]);
            var e2 = Vector3.Cross(e0, e1);
            var rot = Quaternion.CreateFromRotationMatrix(new Matrix4x4(e0.X, e0.Y, e0.Z, 0, e1.X, e1.Y, e1.Z, 0, e2.X, e2.Y, e2.Z, 0, 0, 0, 0, 1));
            float lo = voxel * 0.2f, hi = voxel * 0.9f;
            var scale = new Vector3(Math.Clamp((float)Math.Sqrt(Math.Max(0, eval[0])) * 1.2f, lo, hi), Math.Clamp((float)Math.Sqrt(Math.Max(0, eval[1])) * 1.2f, lo, hi),
                Math.Clamp((float)Math.Sqrt(Math.Max(0, eval[2])) * 1.2f, lo, hi));
            var gi = dst.Add(mean, rot, scale, opacity, new Vector3((float)(a.R / n), (float)(a.G / n), (float)(a.B / n)));
            // SH: 리지 정규화 최소제곱(1차 계수 강한 정규화 — 시점 수 부족 시 DC 로 수렴)
            for (var r = 1; r < 4; r++) ata[r * 4 + r] += 0.05 + 0.5 / observed;
            ata[0] += 1e-6;
            if (SolveSh(ata, atb, dst.Sh.AsSpan(gi * GaussianScene.ShStride, GaussianScene.ShStride))) { }
        }
        var rep = new ReconstructionReport(views.Count, (int)Math.Min(int.MaxValue, points), accs.Count, dst.Count, pruned, sw.Elapsed.TotalSeconds,
            "복원 " + dst.Count.ToString(CultureInfo.InvariantCulture) + " 가우시안 · " + views.Count.ToString(CultureInfo.InvariantCulture) + "시점");
        LastAction = "🧊 " + rep.Message;
        Raise();
        return rep;
    }

    /// <summary>4×4 정규방정식(3 RHS) 가우스 소거 → SH 계수(k·3 + c).</summary>
    private static bool SolveSh(Span<double> a, Span<double> b, Span<float> sh)
    {
        Span<double> m = stackalloc double[4 * 7];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++) m[r * 7 + c] = a[r * 4 + c];
            for (var ch = 0; ch < 3; ch++) m[r * 7 + 4 + ch] = b[r * 3 + ch];
        }
        for (var col = 0; col < 4; col++)
        {
            var piv = col;
            for (var r = col + 1; r < 4; r++) if (Math.Abs(m[r * 7 + col]) > Math.Abs(m[piv * 7 + col])) piv = r;
            if (Math.Abs(m[piv * 7 + col]) < 1e-12) return false;
            if (piv != col) for (var c = 0; c < 7; c++) (m[col * 7 + c], m[piv * 7 + c]) = (m[piv * 7 + c], m[col * 7 + c]);
            for (var r = 0; r < 4; r++)
            {
                if (r == col) continue;
                var f = m[r * 7 + col] / m[col * 7 + col];
                for (var c = col; c < 7; c++) m[r * 7 + c] -= f * m[col * 7 + c];
            }
        }
        for (var k = 0; k < 4; k++)
            for (var ch = 0; ch < 3; ch++)
                sh[k * 3 + ch] = (float)(m[k * 7 + 4 + ch] / m[k * 7 + k]);
        return true;
    }

    /// <summary>대칭 3×3 자코비 고유분해(내림차순 · 고유벡터 열).</summary>
    public static void JacobiEigen(ReadOnlySpan<double> s, Span<double> values, Span<double> vectors)
    {
        Span<double> a = stackalloc double[9];
        s.CopyTo(a);
        vectors.Clear(); vectors[0] = vectors[4] = vectors[8] = 1;
        for (var sweep = 0; sweep < 32; sweep++)
        {
            var off = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
            if (off < 1e-20) break;
            for (var p = 0; p < 2; p++)
                for (var q = p + 1; q < 3; q++)
                {
                    var apq = a[p * 3 + q];
                    if (Math.Abs(apq) < 1e-30) continue;
                    var theta = (a[q * 3 + q] - a[p * 3 + p]) / (2 * apq);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1); var sn = t * c;
                    for (var k = 0; k < 3; k++)
                    {
                        double akp = a[k * 3 + p], akq = a[k * 3 + q];
                        a[k * 3 + p] = c * akp - sn * akq; a[k * 3 + q] = sn * akp + c * akq;
                    }
                    for (var k = 0; k < 3; k++)
                    {
                        double apk = a[p * 3 + k], aqk = a[q * 3 + k];
                        a[p * 3 + k] = c * apk - sn * aqk; a[q * 3 + k] = sn * apk + c * aqk;
                    }
                    for (var k = 0; k < 3; k++)
                    {
                        double vkp = vectors[k * 3 + p], vkq = vectors[k * 3 + q];
                        vectors[k * 3 + p] = c * vkp - sn * vkq; vectors[k * 3 + q] = sn * vkp + c * vkq;
                    }
                }
        }
        values[0] = a[0]; values[1] = a[4]; values[2] = a[8];
        for (var i = 0; i < 2; i++)
            for (var j = i + 1; j < 3; j++)
                if (values[j] > values[i])
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    for (var k = 0; k < 3; k++) (vectors[k * 3 + i], vectors[k * 3 + j]) = (vectors[k * 3 + j], vectors[k * 3 + i]);
                }
    }

    // ══════════════════════ 표준 3DGS PLY ══════════════════════

    private static readonly string[] PlyProps =
    {
        "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2",
        "f_rest_0", "f_rest_1", "f_rest_2", "f_rest_3", "f_rest_4", "f_rest_5", "f_rest_6", "f_rest_7", "f_rest_8",
        "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3",
    };

    /// <summary>Inria 3DGS PLY 내보내기(SH 1차 · f_rest 채널 우선 배열 · Y 축 반전). 반환 = 가우시안 수.</summary>
    public static int ExportPly(GaussianScene s, string path)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var sb = new StringBuilder("ply\nformat binary_little_endian 1.0\nelement vertex ").Append(s.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var p in PlyProps) sb.Append("property float ").Append(p).Append('\n');
            sb.Append("end_header\n");
            fs.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            Span<byte> rec = stackalloc byte[PlyProps.Length * 4];
            Span<float> f = stackalloc float[PlyProps.Length];
            for (var i = 0; i < s.Count; i++)
            {
                f.Clear();
                f[0] = s.Pos[i * 3]; f[1] = -s.Pos[i * 3 + 1]; f[2] = s.Pos[i * 3 + 2];
                var o = i * GaussianScene.ShStride;
                for (var c = 0; c < 3; c++)
                {
                    f[6 + c] = s.Sh[o + c];
                    // f_rest 채널 우선(c·3 + k−1) · Y 반전: Y1 기저(−y) 계수 부호 반전
                    f[9 + c * 3] = -s.Sh[o + 3 + c]; f[9 + c * 3 + 1] = s.Sh[o + 6 + c]; f[9 + c * 3 + 2] = s.Sh[o + 9 + c];
                }
                var op = Math.Clamp(s.Opacity[i], 1e-4f, 1f - 1e-4f);
                f[18] = MathF.Log(op / (1f - op));
                for (var a = 0; a < 3; a++) f[19 + a] = MathF.Log(MathF.Max(s.Scale[i * 3 + a], 1e-7f));
                f[22] = s.Rot[i * 4]; f[23] = -s.Rot[i * 4 + 1]; f[24] = s.Rot[i * 4 + 2]; f[25] = -s.Rot[i * 4 + 3];
                for (var k = 0; k < f.Length; k++) BinaryPrimitives.WriteSingleLittleEndian(rec[(k * 4)..], f[k]);
                fs.Write(rec);
            }
        }
        File.Move(tmp, path, true);
        return s.Count;
    }

    /// <summary>3DGS PLY 가져오기(binary_little_endian float · 속성 순서 무관 · f_rest 고차 계수는 1차만 사용). 반환 = 가우시안 수(-1 = 형식 오류).</summary>
    public static int ImportPly(string path, GaussianScene dst)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var props = new List<string>();
        var count = -1;
        var binary = false;
        var line = new StringBuilder();
        while (true)
        {
            var b = fs.ReadByte();
            if (b < 0) return -1;
            if (b != '\n') { line.Append((char)b); continue; }
            var l = line.ToString().Trim();
            line.Clear();
            if (l.StartsWith("format", StringComparison.Ordinal)) binary = l.Contains("binary_little_endian", StringComparison.Ordinal);
            else if (l.StartsWith("element vertex", StringComparison.Ordinal)) count = int.Parse(l.AsSpan(15), NumberStyles.Integer, CultureInfo.InvariantCulture);
            else if (l.StartsWith("element", StringComparison.Ordinal) && count >= 0 && props.Count > 0) return -1;
            else if (l.StartsWith("property", StringComparison.Ordinal))
            {
                var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[1] != "float") return -1;
                props.Add(parts[2]);
            }
            else if (l == "end_header") break;
        }
        if (!binary || count < 0) return -1;
        if (props.Count > 256) return -1;
        var map = new int[PlyProps.Length];
        for (var k = 0; k < PlyProps.Length; k++) map[k] = props.IndexOf(PlyProps[k]);
        if (map[0] < 0 || map[1] < 0 || map[2] < 0 || map[6] < 0 || map[18] < 0) return -1;
        var restCount = 0;
        foreach (var p in props) if (p.StartsWith("f_rest_", StringComparison.Ordinal)) restCount++;
        var perChannel = restCount / 3;
        var restBase = props.IndexOf("f_rest_0");
        dst.Clear();
        dst.EnsureCapacity(count);
        var rec = new byte[props.Count * 4];
        var f = new float[props.Count];
        for (var i = 0; i < count; i++)
        {
            if (fs.Read(rec, 0, rec.Length) != rec.Length) return -1;
            for (var k = 0; k < props.Count; k++) f[k] = BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(k * 4));
            float V(int k, float dflt) => map[k] >= 0 ? f[map[k]] : dflt;
            var gi = dst.Count++;
            dst.Pos[gi * 3] = V(0, 0); dst.Pos[gi * 3 + 1] = -V(1, 0); dst.Pos[gi * 3 + 2] = V(2, 0);
            var q = Quaternion.Normalize(new Quaternion(-V(23, 0), V(24, 0), -V(25, 0), V(22, 1)));
            dst.Rot[gi * 4] = q.W; dst.Rot[gi * 4 + 1] = q.X; dst.Rot[gi * 4 + 2] = q.Y; dst.Rot[gi * 4 + 3] = q.Z;
            for (var a = 0; a < 3; a++) dst.Scale[gi * 3 + a] = MathF.Exp(V(19 + a, MathF.Log(0.01f)));
            dst.Opacity[gi] = 1f / (1f + MathF.Exp(-V(18, 0)));
            var o = gi * GaussianScene.ShStride;
            for (var c = 0; c < 3; c++)
            {
                dst.Sh[o + c] = V(6 + c, 0);
                if (restBase >= 0 && perChannel >= 3)
                {
                    dst.Sh[o + 3 + c] = -f[restBase + c * perChannel];
                    dst.Sh[o + 6 + c] = f[restBase + c * perChannel + 1];
                    dst.Sh[o + 9 + c] = f[restBase + c * perChannel + 2];
                }
                else { dst.Sh[o + 3 + c] = 0; dst.Sh[o + 6 + c] = 0; dst.Sh[o + 9 + c] = 0; }
            }
        }
        dst.Version++;
        return count;
    }

    /// <summary>PSNR(dB) — RGB24 두 영상.</summary>
    public static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        double se = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) { double d = a[i] - b[i]; se += d * d; }
        var mse = se / Math.Max(1, n);
        return mse <= 1e-10 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Gs.Rasterizer.OnChange", ex); }
    }
}
