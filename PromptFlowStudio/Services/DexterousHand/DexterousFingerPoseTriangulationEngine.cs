using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.DexterousHand;

public enum HandSimdPath : byte { Scalar = 0, V128, V256 }

/// <summary>
/// 양손 42 키포인트 토폴로지(MediaPipe 21 순서 × 좌/우): 0 손목 · 엄지 1–4(CMC · MCP · IP · TIP) · 검지 5–8 · 중지 9–12 · 약지 13–16 · 소지 17–20(MCP · PIP · DIP · TIP).
/// 손 좌표계: x = 손목 → 중지 MCP · 측방 z = 소지 MCP → 검지 MCP 직교화 · 손등 법선 y = s·(z × x)(s = +1 왼손 · −1 오른손).
/// </summary>
public static class HandTopology
{
    public const int Points = MocapSkeleton.HandPoints, Hands = 2, Keypoints = Points * Hands, Fingers = 5;
    public static readonly int[] Parent = { -1, 0, 1, 2, 3, 0, 5, 6, 7, 0, 9, 10, 11, 0, 13, 14, 15, 0, 17, 18, 19 };
    public static readonly string[] FingerNames = { "엄지", "검지", "중지", "약지", "소지" };
    public static readonly string[] JointNames = { "CMC/MCP", "PIP/MCP", "DIP/IP", "TIP" };

    public static int Base(int finger) => 1 + finger * 4;
    public static int FingerOf(int p) => p == 0 ? -1 : (p - 1) / 4;
    public static int Index(int hand, int p) => hand * Points + p;
    public static float Sign(int hand) => hand == 0 ? 1f : -1f;

    /// <summary>손 좌표계(무할당 · 성공 여부).</summary>
    public static bool Frame(ReadOnlySpan<Vector3> p, int hand, out Vector3 x, out Vector3 y, out Vector3 z) => FrameAt(p, hand * Points, Sign(hand), out x, out y, out z);

    /// <summary>오프셋 o 의 21점 손 좌표계(sign = +1 왼손 · −1 오른손).</summary>
    public static bool FrameAt(ReadOnlySpan<Vector3> p, int o, float sign, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        x = p[o + 9] - p[o];
        var lat = p[o + 5] - p[o + 17];
        var xl = x.Length();
        if (xl < 1e-6f) { x = Vector3.UnitX; y = Vector3.UnitY; z = Vector3.UnitZ; return false; }
        x /= xl;
        z = lat - x * Vector3.Dot(lat, x);
        var zl = z.Length();
        if (zl < 1e-6f) { y = Vector3.UnitY; z = Vector3.UnitZ; return false; }
        z /= zl;
        y = sign * Vector3.Cross(z, x);
        return true;
    }

    private const float Deg = MathF.PI / 180f;
    private static readonly float[] RestAngles = BuildRest();   // [hand][finger] × (MCP 굴곡 · 외전 · PIP 굴곡 · 측방 · DIP 굴곡 · 측방)

    public static Vector3 Dir(Vector3 v, Vector3 fallback)
    {
        var l = v.Length();
        return l > 1e-9f ? v / l : fallback;
    }

    public static Vector3 Orth(Vector3 v, Vector3 u, Vector3 fallback)
    {
        var o = v - u * Vector3.Dot(v, u);
        var l = o.Length();
        return l > 1e-6f ? o / l : fallback;
    }

    /// <summary>손가락 f 각 분해(°, 절대 · 전파 프레임): MCP 굴곡/외전 · PIP 굴곡/측방 · DIP 굴곡/측방. 굴곡 + = 손바닥 쪽.</summary>
    public static void Decompose(ReadOnlySpan<Vector3> p, int o, int hand, int f, out float fl0, out float ab, out float fl1, out float la1, out float fl2, out float la2)
    {
        FrameAt(p, o, Sign(hand), out var hx, out var hy, out _);
        var b = o + Base(f);
        var u = Dir(p[b] - p[o], hx);
        var y = Orth(hy, u, hy);
        var a = Vector3.Cross(y, u);
        var v = Dir(p[b + 1] - p[b], u);
        fl0 = MathF.Atan2(-Vector3.Dot(v, y), Vector3.Dot(v, u)) / Deg;
        ab = MathF.Asin(Math.Clamp(Vector3.Dot(v, a), -1f, 1f)) / Deg;
        u = v; a = Orth(a, u, a); y = Vector3.Cross(u, a);
        v = Dir(p[b + 2] - p[b + 1], u);
        fl1 = MathF.Atan2(-Vector3.Dot(v, y), Vector3.Dot(v, u)) / Deg;
        la1 = MathF.Asin(Math.Clamp(Vector3.Dot(v, a), -1f, 1f)) / Deg;
        u = v; a = Orth(a, u, a); y = Vector3.Cross(u, a);
        v = Dir(p[b + 3] - p[b + 2], u);
        fl2 = MathF.Atan2(-Vector3.Dot(v, y), Vector3.Dot(v, u)) / Deg;
        la2 = MathF.Asin(Math.Clamp(Vector3.Dot(v, a), -1f, 1f)) / Deg;
    }

    /// <summary>프레임 (u 진행 · y 손등 · a 측방) 기준 굴곡/측방(°) 방향.</summary>
    public static Vector3 Compose(Vector3 u, Vector3 y, Vector3 a, float flexDeg, float latDeg)
    {
        float fr = flexDeg * Deg, lr = latDeg * Deg;
        return MathF.Cos(lr) * (MathF.Cos(fr) * u - MathF.Sin(fr) * y) + MathF.Sin(lr) * a;
    }

    private static float[] BuildRest()
    {
        var r = new float[Hands * Fingers * 6];
        Span<Vector3> kp = stackalloc Vector3[Points];
        Span<float> curl = stackalloc float[5];
        for (var h = 0; h < Hands; h++)
        {
            MocapSkeleton.HandPositions(h == 0, Vector3.Zero, Quaternion.Identity, curl, 1f, kp);
            for (var f = 0; f < Fingers; f++)
            {
                Decompose(kp, 0, h, f, out var a0, out var a1, out var a2, out var a3, out var a4, out var a5);
                var i = (h * Fingers + f) * 6;
                r[i] = a0; r[i + 1] = a1; r[i + 2] = a2; r[i + 3] = a3; r[i + 4] = a4; r[i + 5] = a5;
            }
        }
        return r;
    }

    /// <summary>레스트 각(°): k = 0 MCP 굴곡 · 1 외전 · 2 PIP 굴곡 · 3 PIP 측방 · 4 DIP 굴곡 · 5 DIP 측방.</summary>
    public static float RestAngle(int hand, int finger, int k) => RestAngles[(hand * Fingers + finger) * 6 + k];

    /// <summary>
    /// 관절 각 기반 손 포즈(무할당): 레스트 대비 굴곡 Δ(손가락 × MCP/PIP/DIP · 15) · 외전 Δ(5) · 뼈 길이 = v16.75 손 레스트 × scale → 손목 위치/회전 변환.
    /// </summary>
    public static void Pose(int hand, Vector3 wrist, Quaternion rot, ReadOnlySpan<float> flexDelta, ReadOnlySpan<float> abdDelta, float scale, Span<Vector3> dst)
    {
        Span<Vector3> rest = stackalloc Vector3[Points];
        Span<float> curl = stackalloc float[5];
        MocapSkeleton.HandPositions(hand == 0, Vector3.Zero, Quaternion.Identity, curl, scale, rest);
        FrameAt(rest, 0, Sign(hand), out var hx, out var hy, out _);
        dst[0] = Vector3.Zero;
        for (var f = 0; f < Fingers; f++)
        {
            var b = Base(f);
            dst[b] = rest[b];
            var u = Dir(rest[b] - rest[0], hx);
            var y = Orth(hy, u, hy);
            var a = Vector3.Cross(y, u);
            var ri = (hand * Fingers + f) * 6;
            for (var j = 0; j < 3; j++)
            {
                var len = Vector3.Distance(rest[b + j + 1], rest[b + j]);
                var flex = RestAngles[ri + j * 2] + flexDelta[f * 3 + j];
                var lat = j == 0 ? RestAngles[ri + 1] + abdDelta[f] : RestAngles[ri + j * 2 + 1];
                var v = Compose(u, y, a, flex, lat);
                dst[b + j + 1] = dst[b + j] + v * len;
                u = v; a = Orth(a, u, a); y = Vector3.Cross(u, a);
            }
        }
        for (var p = 0; p < Points; p++) dst[p] = wrist + Vector3.Transform(dst[p], rot);
    }
}

/// <summary>
/// [PromptFlow Studio v17.13.0] 🖐️ 실시간 멀티뷰 42 키포인트 덱스터러스 핸드 삼각측량 엔진(<see cref="Solve"/> 무할당).
///   ▸ DLT(비동차 · 광선 정규화 · 관측 신뢰도 가중): 키포인트 = SIMD 레인(SoA · 패딩 48) · V256 → V128 → 스칼라 비트 동일 · 3×3 정규방정식 Cramer 해.
///   ▸ 재투영 합의 게이트: 키포인트별 최악 관측(&gt; 임계 · 잔여 ≥ 3뷰) 반복 제외 — 손가락 라벨 스왑 · 오검출 제거.
///   ▸ 자가 가림(Self-Occlusion) 복원: 인라이어 뷰 &lt; 2 이거나 실측 뼈 길이가 보정 길이 대비 ±30% 이탈 → 중수골 = 손 좌표계 직전 방향 · 마디 = 전파 프레임 관절 각(DIP ≈ 0.67·PIP 결합 · 직전 유효 각) × 보정 뼈 길이(손목 → 손끝).
///   ▸ 뼈 길이 보정: 양 끝 유효 프레임 EMA(α 0.05) · 첫 완전 관측 프레임에서 초기화.
/// </summary>
public sealed class DexterousFingerPoseTriangulationEngine
{
    public const int MaxViews = 8, J = HandTopology.Keypoints, Pad = 48, P = HandTopology.Points;

    private readonly PinholeCamera[] _cams = new PinholeCamera[MaxViews];
    private readonly bool[] _camOn = new bool[MaxViews];
    private readonly float[] _u = new float[MaxViews * Pad], _v = new float[MaxViews * Pad], _c = new float[MaxViews * Pad];
    private readonly float[] _nx = new float[MaxViews * Pad], _ny = new float[MaxViews * Pad], _w = new float[MaxViews * Pad];
    private readonly float[] _x = new float[Pad], _y = new float[Pad], _z = new float[Pad], _reproj = new float[Pad];
    private readonly bool[] _solved = new bool[Pad], _valid = new bool[J], _recovered = new bool[J];
    private readonly byte[] _inl = new byte[Pad];
    private readonly Vector3[] _joint = new Vector3[J], _localDir = new Vector3[J];
    private readonly float[] _len = new float[J];
    private readonly bool[] _calibrated = new bool[HandTopology.Hands];
    private readonly float[] _lastAng = new float[HandTopology.Hands * HandTopology.Fingers * 4];
    private readonly bool[] _hasAng = new bool[HandTopology.Hands * HandTopology.Fingers];
    private readonly int[] _camInliers = new int[MaxViews];

    public DexterousFingerPoseTriangulationEngine()
    {
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HandSimdPath.V256
             : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HandSimdPath.V128 : HandSimdPath.Scalar;
    }

    public int Views { get; private set; }
    public HandSimdPath Path { get; set; }
    public float GateThresholdPx { get; set; } = 10f;
    public float MinConfidence { get; set; } = 0.2f;
    public bool BoneRecovery { get; set; } = true;
    public double LastSolveUs { get; private set; }
    public double LastDltUs { get; private set; }
    public float MeanReprojPx { get; private set; }
    public int GatedObservations { get; private set; }
    public int TriangulatedJoints { get; private set; }
    public int RecoveredJoints { get; private set; }
    public long Solves { get; private set; }

    public ref readonly PinholeCamera Camera(int v) => ref _cams[v];
    public bool CameraEnabled(int v) => _camOn[v];
    public void SetCameraEnabled(int v, bool on) { if ((uint)v < MaxViews) _camOn[v] = on; }
    public ReadOnlySpan<Vector3> Joints => _joint;
    public ReadOnlySpan<bool> ValidMask => _valid;
    public Vector3 Joint(int k) => _joint[k];
    public bool Valid(int k) => _valid[k];
    public bool Recovered(int k) => _recovered[k];
    public int InlierViews(int k) => _inl[k];
    public float ReprojPx(int k) => _reproj[k];
    public float BoneLength(int k) => _len[k];
    public bool Calibrated(int hand) => _calibrated[hand];
    public int CameraInliers(int v) => _camInliers[v];
    public float ObservedU(int v, int k) => _u[v * Pad + k];
    public float ObservedV(int v, int k) => _v[v * Pad + k];
    public float ObservedConfidence(int v, int k) => _c[v * Pad + k];
    public float Weight(int v, int k) => _w[v * Pad + k];

    public void Configure(ReadOnlySpan<PinholeCamera> cams)
    {
        Views = Math.Clamp(cams.Length, 0, MaxViews);
        for (var v = 0; v < MaxViews; v++) { _camOn[v] = v < Views; if (v < Views) _cams[v] = cams[v]; }
        Array.Clear(_u); Array.Clear(_v); Array.Clear(_c);
        ResetCalibration();
    }

    public void ResetCalibration() { Array.Clear(_calibrated); Array.Clear(_len); Array.Clear(_localDir); Array.Clear(_valid); Array.Clear(_hasAng); }

    public void SetObservation(int view, int k, float u, float v, float confidence)
    {
        var i = view * Pad + k;
        _u[i] = u; _v[i] = v; _c[i] = confidence;
    }

    /// <summary>관측 가중 · 정규화 좌표(제어 · 테스트용 공개).</summary>
    public void Prepare()
    {
        Array.Clear(_w); Array.Clear(_nx); Array.Clear(_ny);
        for (var v = 0; v < Views; v++)
        {
            ref readonly var c = ref _cams[v];
            for (var k = 0; k < J; k++)
            {
                var i = v * Pad + k;
                _nx[i] = (_u[i] - c.Cx) / c.Fx; _ny[i] = (_v[i] - c.Cy) / c.Fy;
                _w[i] = _camOn[v] && _c[i] >= MinConfidence ? _c[i] : 0f;
            }
        }
    }

    // ── DLT(키포인트 = 레인) ─────────────────────────────────

    public void SolveScalar()
    {
        for (var j = 0; j < Pad; j++)
        {
            float m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0, r0 = 0, r1 = 0, r2 = 0;
            for (var v = 0; v < Views; v++)
            {
                ref readonly var c = ref _cams[v];
                var i = v * Pad + j;
                float w = _w[i], nx = _nx[i], ny = _ny[i];
                float ax = nx * c.Forward.X - c.Right.X, ay = nx * c.Forward.Y - c.Right.Y, az = nx * c.Forward.Z - c.Right.Z;
                float bx = ny * c.Forward.X - c.Down.X, by = ny * c.Forward.Y - c.Down.Y, bz = ny * c.Forward.Z - c.Down.Z;
                float ac = ax * c.Position.X + ay * c.Position.Y + az * c.Position.Z, bc = bx * c.Position.X + by * c.Position.Y + bz * c.Position.Z;
                float wax = w * ax, way = w * ay, waz = w * az, wbx = w * bx, wby = w * by, wbz = w * bz;
                m00 += wax * ax + wbx * bx; m01 += wax * ay + wbx * by; m02 += wax * az + wbx * bz;
                m11 += way * ay + wby * by; m12 += way * az + wby * bz; m22 += waz * az + wbz * bz;
                r0 += wax * ac + wbx * bc; r1 += way * ac + wby * bc; r2 += waz * ac + wbz * bc;
            }
            float c00 = m11 * m22 - m12 * m12, c01 = m02 * m12 - m01 * m22, c02 = m01 * m12 - m02 * m11;
            var det = m00 * c00 + m01 * c01 + m02 * c02;
            float c11 = m00 * m22 - m02 * m02, c12 = m01 * m02 - m00 * m12, c22 = m00 * m11 - m01 * m01;
            _x[j] = (c00 * r0 + c01 * r1 + c02 * r2) / det;
            _y[j] = (c01 * r0 + c11 * r1 + c12 * r2) / det;
            _z[j] = (c02 * r0 + c12 * r1 + c22 * r2) / det;
            _solved[j] = det > 1e-6f;
        }
    }

    public void SolveV128()
    {
        ref var wr = ref MemoryMarshal.GetArrayDataReference(_w);
        ref var xr = ref MemoryMarshal.GetArrayDataReference(_nx);
        ref var yr = ref MemoryMarshal.GetArrayDataReference(_ny);
        for (var j = 0; j < Pad; j += Vector128<float>.Count)
        {
            Vector128<float> m00 = default, m01 = default, m02 = default, m11 = default, m12 = default, m22 = default, r0 = default, r1 = default, r2 = default;
            for (var v = 0; v < Views; v++)
            {
                ref readonly var c = ref _cams[v];
                var i = (nuint)(v * Pad + j);
                var w = Vector128.LoadUnsafe(ref wr, i); var nx = Vector128.LoadUnsafe(ref xr, i); var ny = Vector128.LoadUnsafe(ref yr, i);
                Vector128<float> fx = Vector128.Create(c.Forward.X), fy = Vector128.Create(c.Forward.Y), fz = Vector128.Create(c.Forward.Z);
                Vector128<float> px = Vector128.Create(c.Position.X), py = Vector128.Create(c.Position.Y), pz = Vector128.Create(c.Position.Z);
                Vector128<float> ax = nx * fx - Vector128.Create(c.Right.X), ay = nx * fy - Vector128.Create(c.Right.Y), az = nx * fz - Vector128.Create(c.Right.Z);
                Vector128<float> bx = ny * fx - Vector128.Create(c.Down.X), by = ny * fy - Vector128.Create(c.Down.Y), bz = ny * fz - Vector128.Create(c.Down.Z);
                Vector128<float> ac = ax * px + ay * py + az * pz, bc = bx * px + by * py + bz * pz;
                Vector128<float> wax = w * ax, way = w * ay, waz = w * az, wbx = w * bx, wby = w * by, wbz = w * bz;
                m00 += wax * ax + wbx * bx; m01 += wax * ay + wbx * by; m02 += wax * az + wbx * bz;
                m11 += way * ay + wby * by; m12 += way * az + wby * bz; m22 += waz * az + wbz * bz;
                r0 += wax * ac + wbx * bc; r1 += way * ac + wby * bc; r2 += waz * ac + wbz * bc;
            }
            Vector128<float> c00 = m11 * m22 - m12 * m12, c01 = m02 * m12 - m01 * m22, c02 = m01 * m12 - m02 * m11;
            var det = m00 * c00 + m01 * c01 + m02 * c02;
            Vector128<float> c11 = m00 * m22 - m02 * m02, c12 = m01 * m02 - m00 * m12, c22 = m00 * m11 - m01 * m01;
            ((c00 * r0 + c01 * r1 + c02 * r2) / det).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_x), (nuint)j);
            ((c01 * r0 + c11 * r1 + c12 * r2) / det).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_y), (nuint)j);
            ((c02 * r0 + c12 * r1 + c22 * r2) / det).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_z), (nuint)j);
            for (var k = 0; k < Vector128<float>.Count; k++) _solved[j + k] = det.GetElement(k) > 1e-6f;
        }
    }

    public void SolveV256()
    {
        ref var wr = ref MemoryMarshal.GetArrayDataReference(_w);
        ref var xr = ref MemoryMarshal.GetArrayDataReference(_nx);
        ref var yr = ref MemoryMarshal.GetArrayDataReference(_ny);
        for (var j = 0; j < Pad; j += Vector256<float>.Count)
        {
            Vector256<float> m00 = default, m01 = default, m02 = default, m11 = default, m12 = default, m22 = default, r0 = default, r1 = default, r2 = default;
            for (var v = 0; v < Views; v++)
            {
                ref readonly var c = ref _cams[v];
                var i = (nuint)(v * Pad + j);
                var w = Vector256.LoadUnsafe(ref wr, i); var nx = Vector256.LoadUnsafe(ref xr, i); var ny = Vector256.LoadUnsafe(ref yr, i);
                Vector256<float> fx = Vector256.Create(c.Forward.X), fy = Vector256.Create(c.Forward.Y), fz = Vector256.Create(c.Forward.Z);
                Vector256<float> px = Vector256.Create(c.Position.X), py = Vector256.Create(c.Position.Y), pz = Vector256.Create(c.Position.Z);
                Vector256<float> ax = nx * fx - Vector256.Create(c.Right.X), ay = nx * fy - Vector256.Create(c.Right.Y), az = nx * fz - Vector256.Create(c.Right.Z);
                Vector256<float> bx = ny * fx - Vector256.Create(c.Down.X), by = ny * fy - Vector256.Create(c.Down.Y), bz = ny * fz - Vector256.Create(c.Down.Z);
                Vector256<float> ac = ax * px + ay * py + az * pz, bc = bx * px + by * py + bz * pz;
                Vector256<float> wax = w * ax, way = w * ay, waz = w * az, wbx = w * bx, wby = w * by, wbz = w * bz;
                m00 += wax * ax + wbx * bx; m01 += wax * ay + wbx * by; m02 += wax * az + wbx * bz;
                m11 += way * ay + wby * by; m12 += way * az + wby * bz; m22 += waz * az + wbz * bz;
                r0 += wax * ac + wbx * bc; r1 += way * ac + wby * bc; r2 += waz * ac + wbz * bc;
            }
            Vector256<float> c00 = m11 * m22 - m12 * m12, c01 = m02 * m12 - m01 * m22, c02 = m01 * m12 - m02 * m11;
            var det = m00 * c00 + m01 * c01 + m02 * c02;
            Vector256<float> c11 = m00 * m22 - m02 * m02, c12 = m01 * m02 - m00 * m12, c22 = m00 * m11 - m01 * m01;
            ((c00 * r0 + c01 * r1 + c02 * r2) / det).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_x), (nuint)j);
            ((c01 * r0 + c11 * r1 + c12 * r2) / det).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_y), (nuint)j);
            ((c02 * r0 + c12 * r1 + c22 * r2) / det).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_z), (nuint)j);
            for (var k = 0; k < Vector256<float>.Count; k++) _solved[j + k] = det.GetElement(k) > 1e-6f;
        }
    }

    public void RawSolution(Span<float> xyz)
    {
        for (var j = 0; j < J; j++) { xyz[j * 3] = _x[j]; xyz[j * 3 + 1] = _y[j]; xyz[j * 3 + 2] = _z[j]; }
    }

    private void Dlt()
    {
        switch (Path)
        {
            case HandSimdPath.V256 when Vector256.IsHardwareAccelerated: SolveV256(); break;
            case HandSimdPath.V128 when Vector128.IsHardwareAccelerated: SolveV128(); break;
            default: SolveScalar(); break;
        }
    }

    private bool Finite(int j) => _solved[j] && float.IsFinite(_x[j]) && float.IsFinite(_y[j]) && float.IsFinite(_z[j]);

    private bool RejectWorst()
    {
        var changed = false;
        for (var j = 0; j < J; j++)
        {
            if (!Finite(j)) continue;
            var p = new Vector3(_x[j], _y[j], _z[j]);
            int n = 0, worst = -1;
            var we = GateThresholdPx;
            for (var v = 0; v < Views; v++)
            {
                var i = v * Pad + j;
                if (_w[i] <= 0) continue;
                n++;
                if (!_cams[v].Project(p, out var uv)) { we = float.MaxValue; worst = v; continue; }
                var e = MathF.Sqrt((uv.X - _u[i]) * (uv.X - _u[i]) + (uv.Y - _v[i]) * (uv.Y - _v[i]));
                if (e > we) { we = e; worst = v; }
            }
            if (n < 3 || worst < 0) continue;
            _w[worst * Pad + j] = 0;
            GatedObservations++;
            changed = true;
        }
        return changed;
    }

    /// <summary>준비 → DLT + 합의 게이트 → 재투영 → 뼈 길이 보정/가림 복원. 무할당.</summary>
    public void Solve()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Prepare();
        GatedObservations = 0;
        var td = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var round = 0; round < MaxViews - 2; round++)
        {
            Dlt();
            if (!RejectWorst()) break;
        }
        LastDltUs = (System.Diagnostics.Stopwatch.GetTimestamp() - td) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        Array.Clear(_camInliers);
        double sum = 0; var n = 0; var tri = 0;
        for (var j = 0; j < J; j++)
        {
            _inl[j] = 0; _reproj[j] = 0; _valid[j] = false; _recovered[j] = false;
            if (!Finite(j)) continue;
            var p = new Vector3(_x[j], _y[j], _z[j]);
            float es = 0; var en = 0;
            for (var v = 0; v < Views; v++)
            {
                var i = v * Pad + j;
                if (_w[i] <= 0 || !_cams[v].Project(p, out var uv)) continue;
                es += MathF.Sqrt((uv.X - _u[i]) * (uv.X - _u[i]) + (uv.Y - _v[i]) * (uv.Y - _v[i])); en++;
                _camInliers[v]++;
            }
            _inl[j] = (byte)en;
            if (en < 2) continue;
            _reproj[j] = es / en; sum += _reproj[j]; n++;
            _valid[j] = true; tri++;
            _joint[j] = p;
        }
        MeanReprojPx = n > 0 ? (float)(sum / n) : 0f;
        TriangulatedJoints = tri;
        var rec = 0;
        for (var h = 0; h < HandTopology.Hands; h++) rec += Recover(h);
        RecoveredJoints = rec;
        Solves++;
        LastSolveUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// 뼈 길이 보정 · 가림/이상 길이 복원(손목 → 손끝): 중수골 끝(MCP · 엄지 CMC) = 손 좌표계 직전 방향 ·
    /// 손가락 마디 = 부모 마디 전파 프레임 기준 관절 각(DIP ≈ 0.67 × PIP 굴곡 결합 · 그 외 직전 유효 각) × 보정 길이.
    /// </summary>
    private int Recover(int h)
    {
        var o = h * P;
        if (!_calibrated[h])
        {
            for (var p = 0; p < P; p++) if (!_valid[o + p]) return 0;
            for (var p = 1; p < P; p++) _len[o + p] = Vector3.Distance(_joint[o + p], _joint[o + HandTopology.Parent[p]]);
            _calibrated[h] = true;
        }
        if (!_valid[o] || !_valid[o + 5] || !_valid[o + 9] || !_valid[o + 17]) return 0;
        HandTopology.Frame(_joint, h, out var fx, out var fy, out var fz);
        var rec = 0;
        for (var f = 0; f < HandTopology.Fingers; f++)
        {
            var b = o + HandTopology.Base(f);
            // 길이 이탈 관측은 무효화
            for (var j = 0; j < 4; j++)
            {
                var k = b + j; var pk = j == 0 ? o : k - 1;
                if (_valid[k] && _valid[pk] && MathF.Abs(Vector3.Distance(_joint[k], _joint[pk]) - _len[k]) > 0.3f * _len[k]) _valid[k] = false;
            }
            // 중수골 끝
            if (_valid[b])
            {
                var d = _joint[b] - _joint[o];
                var dl = d.Length();
                _len[b] += 0.05f * (dl - _len[b]);
                d /= dl;
                _localDir[b] = new Vector3(Vector3.Dot(d, fx), Vector3.Dot(d, fy), Vector3.Dot(d, fz));
            }
            else
            {
                if (!BoneRecovery || _localDir[b] == Vector3.Zero) continue;
                var ld = _localDir[b];
                _joint[b] = _joint[o] + (fx * ld.X + fy * ld.Y + fz * ld.Z) * _len[b];
                _valid[b] = _recovered[b] = true; rec++;
            }
            var all = _valid[b + 1] && _valid[b + 2] && _valid[b + 3];
            var ai = (h * HandTopology.Fingers + f) * 4;
            if (all && !_recovered[b])
            {
                HandTopology.Decompose(_joint, o, h, f, out var a0, out var ab, out var a1, out _, out var a2, out _);
                _lastAng[ai] = a0; _lastAng[ai + 1] = ab; _lastAng[ai + 2] = a1; _lastAng[ai + 3] = a2;
                _hasAng[h * HandTopology.Fingers + f] = true;
                for (var j = 1; j < 4; j++) _len[b + j] += 0.05f * (Vector3.Distance(_joint[b + j], _joint[b + j - 1]) - _len[b + j]);
                continue;
            }
            if (all || !BoneRecovery || !_hasAng[h * HandTopology.Fingers + f]) continue;
            var u = HandTopology.Dir(_joint[b] - _joint[o], fx);
            var y = HandTopology.Orth(fy, u, fy);
            var a = Vector3.Cross(y, u);
            var pipRel = float.NaN;
            for (var j = 0; j < 3; j++)
            {
                int k = b + j + 1;
                Vector3 v;
                if (_valid[k])
                {
                    v = HandTopology.Dir(_joint[k] - _joint[k - 1], u);
                    if (j == 1) pipRel = MathF.Atan2(-Vector3.Dot(v, y), Vector3.Dot(v, u)) * 180f / MathF.PI - HandTopology.RestAngle(h, f, 2);
                }
                else
                {
                    float flex, lat;
                    if (j == 0) { flex = _lastAng[ai]; lat = _lastAng[ai + 1]; }
                    else if (j == 1) { flex = _lastAng[ai + 2]; lat = HandTopology.RestAngle(h, f, 3); pipRel = flex - HandTopology.RestAngle(h, f, 2); }
                    else
                    {
                        flex = float.IsNaN(pipRel) ? _lastAng[ai + 3] : HandTopology.RestAngle(h, f, 4) + 0.67f * pipRel;
                        lat = HandTopology.RestAngle(h, f, 5);
                    }
                    v = HandTopology.Compose(u, y, a, flex, lat);
                    _joint[k] = _joint[k - 1] + v * _len[k];
                    _valid[k] = _recovered[k] = true; rec++;
                }
                u = v; a = HandTopology.Orth(a, u, a); y = Vector3.Cross(u, a);
            }
        }
        return rec;
    }
}
