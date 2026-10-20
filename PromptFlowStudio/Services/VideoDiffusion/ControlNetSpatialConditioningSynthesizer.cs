using System.Numerics;
using PromptFlowStudio.Services.Cinematography;
using PromptFlowStudio.Services.Mocap;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.VideoDiffusion;

/// <summary>지형 높이 캐시(제어 평면 1회 샘플 → 쌍선형 조회 · 무할당 · IHeightField 호환).</summary>
public sealed class CachedHeightField : IHeightField
{
    public const int N = 128;
    private readonly float[] _h = new float[N * N];
    private float _x0, _z0, _cell = 1f;

    public float Extent => _cell * (N - 1);
    public Vector2 Origin => new(_x0, _z0);

    /// <summary>원본 높이장(예: v16.72 절차 월드)을 중심 ± extent/2 범위로 N² 샘플.</summary>
    public void Bake(IHeightField src, Vector2 center, float extent)
    {
        _cell = extent / (N - 1); _x0 = center.X - extent * 0.5f; _z0 = center.Y - extent * 0.5f;
        for (var j = 0; j < N; j++)
            for (var i = 0; i < N; i++) _h[j * N + i] = src.HeightAt(_x0 + i * _cell, _z0 + j * _cell);
    }

    /// <summary>해석적 언덕(기본 지형 · 외부 월드 미결선 시).</summary>
    public void BakeHills(float extent, float amplitude = 2.2f)
    {
        _cell = extent / (N - 1); _x0 = -extent * 0.5f; _z0 = -extent * 0.5f;
        for (var j = 0; j < N; j++)
            for (var i = 0; i < N; i++)
            {
                float x = _x0 + i * _cell, z = _z0 + j * _cell;
                var r = MathF.Sqrt(x * x + z * z);
                _h[j * N + i] = amplitude * (0.6f * MathF.Sin(0.045f * x + 0.3f) * MathF.Cos(0.038f * z) + 0.4f * MathF.Sin(0.11f * x - 0.07f * z + 1.1f)) * MathF.Min(1f, r / 14f);
            }
    }

    public float HeightAt(float x, float z)
    {
        var fx = Math.Clamp((x - _x0) / _cell, 0f, N - 1.001f); var fz = Math.Clamp((z - _z0) / _cell, 0f, N - 1.001f);
        int i = (int)fx, j = (int)fz; float u = fx - i, v = fz - j;
        var k = j * N + i;
        var a = _h[k] + (_h[k + 1] - _h[k]) * u; var b = _h[k + N] + (_h[k + N + 1] - _h[k + N]) * u;
        return a + (b - a) * v;
    }

    public float AbsorptionAt(float x, float z) => 0.5f;
}

/// <summary>
/// [PromptFlow Studio v16.87.0] 🦴 제너레이티브 ControlNet 공간 깊이 &amp; 포즈 컨디셔닝 합성기(라텐트 해상도 64×36 · 무할당).
///   ▸ 깊이: v16.85 카메라 포즈(초점거리 → 수직 FOV · 16:9) 광선 × v16.72 지형 높이장(캐시 128² · 지수 간격 48 스텝 + 이분 4회) → 로그 정규 깊이(하늘 = 1) · 깊이 경계(소벨).
///   ▸ 포즈: v16.75 모캡 스켈레톤 24관절 FK(보행 위상 → 엉덩이/무릎/어깨/팔꿈치 회전) → 뼈 캡슐(원근 반경) 소프트 마스크 + 피사체 깊이 덮어쓰기 · 2D 관절.
///   ▸ 카메라: 이전/현재 포즈로 원거리 점 투영 차 → 전역 카메라 플로(px) · 컨디셔닝 가중치(깊이/포즈/카메라 · 적용 구간 start..end).
/// </summary>
public sealed class ControlNetSpatialConditioningSynthesizer
{
    public const int LW = 64, LH = 36, Cells = LW * LH, MarchSteps = 48;
    public const float MaxDistance = 220f, Aspect = 16f / 9f;
    private static readonly int[] Bones = BuildBones();

    private readonly object _gate = new();
    public readonly float[] Depth = new float[Cells], Pose = new float[Cells], Edge = new float[Cells];
    private readonly Vector3[] _joints = new Vector3[MocapSkeleton.Body];
    private readonly Quaternion[] _local = new Quaternion[MocapSkeleton.Body], _global = new Quaternion[MocapSkeleton.Body];
    private readonly Vector3[] _joint2D = new Vector3[MocapSkeleton.Body];
    private IHeightField? _terrain;
    private CameraPose _prevCam;
    private bool _hasPrev;

    public ControlNetSpatialConditioningSynthesizer() { for (var i = 0; i < _local.Length; i++) _local[i] = Quaternion.Identity; }

    // ── 컨디셔닝 가중치 ──
    public float DepthWeight { get; set; } = 0.9f;
    public float PoseWeight { get; set; } = 1f;
    public float CameraWeight { get; set; } = 1f;
    public float ControlStart { get; set; }
    public float ControlEnd { get; set; } = 0.7f;

    // ── 계측 ──
    public CameraPose Camera { get; private set; }
    public Vector2 CameraFlowPx { get; private set; }
    public float SkyFraction { get; private set; }
    public float PoseCoverage { get; private set; }
    public float SubjectDepth { get; private set; }
    public double LastBuildMs { get; private set; }
    public long Builds { get; private set; }
    public ReadOnlySpan<Vector3> Joints => _joints;
    /// <summary>관절 화면 좌표(x, y ∈ 0..1 · z = 카메라 깊이 · z ≤ 0 = 뒤).</summary>
    public Vector3 Joint2D(int j) => _joint2D[j];
    public static int BoneCount => Bones.Length / 2;
    public static (int A, int B) Bone(int k) => (Bones[2 * k], Bones[2 * k + 1]);

    private static int[] BuildBones()
    {
        var n = 0; Span<int> tmp = stackalloc int[MocapSkeleton.Body * 2];
        for (var j = 1; j < MocapSkeleton.Body; j++) { tmp[n++] = MocapSkeleton.Parent[j]; tmp[n++] = j; }
        return tmp[..n].ToArray();
    }

    public void BindTerrain(IHeightField? terrain) { lock (_gate) _terrain = terrain; }

    /// <summary>보행 위상(0..1 주기) → 국소 회전(엉덩이/무릎/어깨/팔꿈치 · X축 스윙).</summary>
    public void SetWalkPose(float phase, float amplitude = 1f)
    {
        var a = MathF.Tau * phase;
        static Quaternion X(float r) => Quaternion.CreateFromAxisAngle(Vector3.UnitX, r);
        for (var i = 0; i < _local.Length; i++) _local[i] = Quaternion.Identity;
        _local[MocapSkeleton.LHip] = X(-0.45f * amplitude * MathF.Sin(a));
        _local[MocapSkeleton.RHip] = X(0.45f * amplitude * MathF.Sin(a));
        _local[MocapSkeleton.LKnee] = X(0.6f * amplitude * MathF.Max(0f, MathF.Sin(a + 1.2f)));
        _local[MocapSkeleton.RKnee] = X(0.6f * amplitude * MathF.Max(0f, MathF.Sin(a + 1.2f + MathF.PI)));
        _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -1.2f) * X(0.35f * amplitude * MathF.Sin(a));
        _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f) * X(-0.35f * amplitude * MathF.Sin(a));
        _local[MocapSkeleton.LElbow] = X(-0.3f * amplitude);
        _local[MocapSkeleton.RElbow] = X(-0.3f * amplitude);
    }

    /// <summary>카메라 기준 방향 → 화면(0..1) + z.</summary>
    public static Vector3 Project(in CameraPose cam, Vector3 p)
    {
        var d = p - cam.Position;
        var z = Vector3.Dot(d, cam.Forward);
        if (z < 0.05f) return new Vector3(-1f, -1f, z);
        var tv = NeuralCameraTrajectoryEngine.SensorHeightMm * 0.5f / cam.FocalMm; var th = tv * Aspect;
        return new Vector3(0.5f + Vector3.Dot(d, cam.Right) / (z * th) * 0.5f, 0.5f - Vector3.Dot(d, cam.Up) / (z * tv) * 0.5f, z);
    }

    public static Vector3 Ray(in CameraPose cam, float u, float v)
    {
        var tv = NeuralCameraTrajectoryEngine.SensorHeightMm * 0.5f / cam.FocalMm; var th = tv * Aspect;
        return Vector3.Normalize(cam.Forward + cam.Right * ((u - 0.5f) * 2f * th) + cam.Up * ((0.5f - v) * 2f * tv));
    }

    public static float NormalizeDepth(float t) => Math.Clamp(MathF.Log(1f + t) / MathF.Log(1f + MaxDistance), 0f, 1f);

    /// <summary>컨디션 맵 생성(무할당): 깊이 레이마치 → 포즈 FK/캡슐 → 경계 · 카메라 플로.</summary>
    public void Build(in CameraPose cam, Vector3 subjectRoot, float subjectYaw)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Camera = cam;
            // ── 깊이 ──
            var sky = 0;
            var k = MathF.Pow(MaxDistance / 0.5f, 1f / MarchSteps);
            for (var y = 0; y < LH; y++)
                for (var x = 0; x < LW; x++)
                {
                    var dir = Ray(cam, (x + 0.5f) / LW, (y + 0.5f) / LH);
                    var hit = MaxDistance; float prevT = 0f, t = 0.5f;
                    if (_terrain is not null)
                        for (var s = 0; s < MarchSteps; s++, prevT = t, t *= k)
                        {
                            var p = cam.Position + dir * t;
                            if (p.Y > _terrain.HeightAt(p.X, p.Z)) continue;
                            float lo = prevT, hi = t;
                            for (var b = 0; b < 4; b++)
                            {
                                var m = 0.5f * (lo + hi); var q = cam.Position + dir * m;
                                if (q.Y > _terrain.HeightAt(q.X, q.Z)) lo = m; else hi = m;
                            }
                            hit = 0.5f * (lo + hi);
                            break;
                        }
                    else if (dir.Y < -1e-4f) hit = MathF.Min(MaxDistance, -cam.Position.Y / dir.Y);
                    var d = hit >= MaxDistance ? 1f : NormalizeDepth(hit);
                    if (d >= 1f) sky++;
                    Depth[y * LW + x] = d;
                }
            SkyFraction = sky / (float)Cells;

            // ── 포즈 ──
            MocapSkeleton.ForwardKinematics(MocapSkeleton.RestOffset, _local, Vector3.Zero, _joints, _global);
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, subjectYaw);
            for (var j = 0; j < _joints.Length; j++)
            {
                _joints[j] = subjectRoot + Vector3.Transform(_joints[j] - new Vector3(0f, 0f, 0f), rot);
                _joint2D[j] = Project(cam, _joints[j]);
            }
            Array.Clear(Pose);
            var pz = Project(cam, _joints[MocapSkeleton.Spine2]).Z;
            SubjectDepth = pz;
            for (var bi = 0; bi < BoneCount; bi++)
            {
                var (ja, jb) = Bone(bi);
                Vector3 a = _joint2D[ja], b = _joint2D[jb];
                if (a.Z <= 0.05f || b.Z <= 0.05f) continue;
                var radius = (jb == MocapSkeleton.Head ? 0.11f : ja == MocapSkeleton.Spine1 || ja == MocapSkeleton.Spine2 || ja == MocapSkeleton.Spine3 || ja == MocapSkeleton.Pelvis ? 0.13f : 0.06f);
                var tvv = NeuralCameraTrajectoryEngine.SensorHeightMm * 0.5f / cam.FocalMm;
                var rpx = radius / (0.5f * (a.Z + b.Z) * tvv * 2f) * LH;
                float ax = a.X * LW, ay = a.Y * LH, bx = b.X * LW, by = b.Y * LH;
                int x0 = Math.Max(0, (int)(MathF.Min(ax, bx) - rpx * 2f)), x1 = Math.Min(LW - 1, (int)(MathF.Max(ax, bx) + rpx * 2f));
                int y0 = Math.Max(0, (int)(MathF.Min(ay, by) - rpx * 2f)), y1 = Math.Min(LH - 1, (int)(MathF.Max(ay, by) + rpx * 2f));
                float ex = bx - ax, ey = by - ay, el2 = MathF.Max(ex * ex + ey * ey, 1e-6f);
                var bz = 0.5f * (a.Z + b.Z);
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f - ax, py = y + 0.5f - ay;
                        var u = Math.Clamp((px * ex + py * ey) / el2, 0f, 1f);
                        float dx = px - u * ex, dy = py - u * ey;
                        var w = MathF.Exp(-(dx * dx + dy * dy) / MathF.Max(rpx * rpx, 0.25f));
                        var i = y * LW + x;
                        if (w > Pose[i]) Pose[i] = w;
                        if (w > 0.5f) { var dn = NormalizeDepth(bz); if (dn < Depth[i]) Depth[i] = dn; }
                    }
            }
            var cov = 0f; for (var i = 0; i < Cells; i++) cov += Pose[i];
            PoseCoverage = cov / Cells;

            // ── 깊이 경계(소벨) ──
            for (var y = 0; y < LH; y++)
                for (var x = 0; x < LW; x++)
                {
                    int xm = Math.Max(x - 1, 0), xp = Math.Min(x + 1, LW - 1), ym = Math.Max(y - 1, 0), yp = Math.Min(y + 1, LH - 1);
                    var gx = Depth[y * LW + xp] - Depth[y * LW + xm]; var gy = Depth[yp * LW + x] - Depth[ym * LW + x];
                    Edge[y * LW + x] = MathF.Min(1f, 4f * MathF.Sqrt(gx * gx + gy * gy));
                }

            // ── 카메라 플로: 원거리 기준점(현 시선 100 m) 이전/현재 투영 차 · 이미지 px(256×144 기준) ──
            if (_hasPrev)
            {
                var anchor = cam.Position + cam.Forward * 100f;
                var p1 = Project(cam, anchor); var p0 = Project(_prevCam, anchor);
                CameraFlowPx = p0.Z > 0f ? new Vector2((p1.X - p0.X) * LW * 4f, (p1.Y - p0.Y) * LH * 4f) * CameraWeight : Vector2.Zero;
            }
            _prevCam = cam; _hasPrev = true;
            Builds++;
            LastBuildMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }
}
