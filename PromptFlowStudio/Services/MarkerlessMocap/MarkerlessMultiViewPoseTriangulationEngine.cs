using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.MarkerlessMocap;

public enum TriangulationSimdPath : byte { Scalar = 0, V128, V256 }

/// <summary>
/// [PromptFlow Studio v17.11.0] 🏃 멀티뷰 무마커 3D 관절 삼각측량 엔진(<see cref="Solve"/> 무할당).
///   ▸ 입력: 동기화된 V(2–8)대 핀홀 카메라(<see cref="PinholeCamera"/>) × 24관절(SMPL 순서) 2D 키포인트 + 신뢰도.
///   ▸ 에피폴라 게이트(과반 뷰 일치 필요) + 재투영 합의 게이트(최악 관측 반복 제외 · 잔여 ≥ 3뷰): 뷰 쌍 (i,k) 마다 관측 광선 dᵢ 와 기선 Cₖ−Cᵢ 가 이루는 평면 m = (Cₖ−Cᵢ)×dᵢ → 뷰 k 의 에피폴라 직선 l = (m·Rₖ/fx, m·Dₖ/fy, m·Fₖ − l₁cx − l₂cy) ·
///     대칭 점-직선 거리(px). 다른 어떤 뷰와도 일치하지 않는 관측(가림 스왑 · 오검출 튐)은 가중치 0.
///   ▸ DLT(비동차 · 광선 정규화): 관측당 두 평면 a = x̂·F − R, b = ŷ·F − D (x̂ = (u−cx)/fx) → Σw(aaᵀ + bbᵀ)X = Σw(a(a·C) + b(b·C)) ·
///     3×3 대칭 정규방정식 Cramer 해. 관절을 SIMD 레인으로 배치(SoA · 패딩 32) — V256 → V128 → 스칼라(비트 동일).
///   ▸ 출력: 관절 3D · 재투영 오차(px) · 인라이어 뷰 수 · 에피폴라 잔차.
/// </summary>
public sealed class MarkerlessMultiViewPoseTriangulationEngine
{
    public const int MaxViews = 8, J = MocapSkeleton.Body, Pad = 32;

    private readonly PinholeCamera[] _cams = new PinholeCamera[MaxViews];
    private readonly bool[] _camOn = new bool[MaxViews];
    // SoA 관측(뷰 × Pad): 정규화 좌표 x̂ · ŷ · 가중치
    private readonly float[] _u = new float[MaxViews * Pad], _v = new float[MaxViews * Pad], _c = new float[MaxViews * Pad];
    private readonly float[] _nx = new float[MaxViews * Pad], _ny = new float[MaxViews * Pad], _w = new float[MaxViews * Pad];
    private readonly float[] _x = new float[Pad], _y = new float[Pad], _z = new float[Pad];
    private readonly float[] _epi = new float[MaxViews * Pad], _reproj = new float[Pad];
    private readonly byte[] _inl = new byte[Pad];
    private readonly bool[] _valid = new bool[Pad];
    private readonly Vector3[] _joint = new Vector3[J];
    private readonly int[] _camInliers = new int[MaxViews], _camObs = new int[MaxViews];
    private readonly float[] _camReproj = new float[MaxViews];

    public MarkerlessMultiViewPoseTriangulationEngine()
    {
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? TriangulationSimdPath.V256
             : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? TriangulationSimdPath.V128 : TriangulationSimdPath.Scalar;
    }

    public int Views { get; private set; }
    public TriangulationSimdPath Path { get; set; }
    public bool EpipolarGate { get; set; } = true;
    /// <summary>에피폴라 일치 임계(px).</summary>
    public float GateThresholdPx { get; set; } = 12f;
    /// <summary>신뢰도 하한(미만 = 미관측).</summary>
    public float MinConfidence { get; set; } = 0.15f;
    public double LastSolveUs { get; private set; }
    public double LastEpipolarUs { get; private set; }
    public float MeanReprojPx { get; private set; }
    public float MeanEpipolarPx { get; private set; }
    public int GatedObservations { get; private set; }
    public int ValidJoints { get; private set; }
    public long Solves { get; private set; }

    public ref readonly PinholeCamera Camera(int v) => ref _cams[v];
    public bool CameraEnabled(int v) => _camOn[v];
    public void SetCameraEnabled(int v, bool on) { if ((uint)v < MaxViews) _camOn[v] = on; }
    public Vector3 Joint(int j) => _joint[j];
    public ReadOnlySpan<Vector3> Joints => _joint;
    public bool Valid(int j) => _valid[j];
    public ReadOnlySpan<bool> ValidMask => _valid.AsSpan(0, J);
    /// <summary>게이트 후 DLT 가중치(0 = 미관측/게이트 제외).</summary>
    public float Weight(int v, int j) => _w[v * Pad + j];
    public float ReprojPx(int j) => _reproj[j];
    public int InlierViews(int j) => _inl[j];
    public float EpipolarPx(int v, int j) => _epi[v * Pad + j];
    public int CameraInliers(int v) => _camInliers[v];
    public int CameraObservations(int v) => _camObs[v];
    public float CameraReprojPx(int v) => _camReproj[v];
    public float ObservedU(int v, int j) => _u[v * Pad + j];
    public float ObservedV(int v, int j) => _v[v * Pad + j];
    public float ObservedConfidence(int v, int j) => _c[v * Pad + j];

    /// <summary>카메라 리그 구성(제어 평면). 관측 초기화.</summary>
    public void Configure(ReadOnlySpan<PinholeCamera> cams)
    {
        Views = Math.Clamp(cams.Length, 0, MaxViews);
        for (var v = 0; v < MaxViews; v++) { _camOn[v] = v < Views; if (v < Views) _cams[v] = cams[v]; }
        ClearObservations();
    }

    public void ClearObservations()
    {
        Array.Clear(_u); Array.Clear(_v); Array.Clear(_c);
    }

    public void SetObservation(int view, int joint, float u, float v, float confidence)
    {
        var i = view * Pad + joint;
        _u[i] = u; _v[i] = v; _c[i] = confidence;
    }

    // ── 에피폴라 기하 ───────────────────────────────────────

    /// <summary>뷰 i 픽셀 (ui,vi) 의 에피폴라 직선(뷰 k 픽셀 좌표 · 정규화 l₁²+l₂² = 1).</summary>
    public static void EpipolarLine(in PinholeCamera ci, in PinholeCamera ck, float ui, float vi, out float l1, out float l2, out float l3)
    {
        var d = ci.Forward + ci.Right * ((ui - ci.Cx) / ci.Fx) + ci.Down * ((vi - ci.Cy) / ci.Fy);
        var m = Vector3.Cross(ck.Position - ci.Position, d);
        l1 = Vector3.Dot(m, ck.Right) / ck.Fx; l2 = Vector3.Dot(m, ck.Down) / ck.Fy;
        l3 = Vector3.Dot(m, ck.Forward) - l1 * ck.Cx - l2 * ck.Cy;
        var n = MathF.Sqrt(l1 * l1 + l2 * l2);
        if (n < 1e-12f) { l1 = l2 = l3 = 0; return; }
        l1 /= n; l2 /= n; l3 /= n;
    }

    /// <summary>대칭 에피폴라 거리(px) = ½(d(xₖ, l(xᵢ)) + d(xᵢ, l(xₖ))).</summary>
    public static float SymmetricEpipolarPx(in PinholeCamera ci, in PinholeCamera ck, float ui, float vi, float uk, float vk)
    {
        EpipolarLine(ci, ck, ui, vi, out var a1, out var a2, out var a3);
        EpipolarLine(ck, ci, uk, vk, out var b1, out var b2, out var b3);
        return 0.5f * (MathF.Abs(a1 * uk + a2 * vk + a3) + MathF.Abs(b1 * ui + b2 * vi + b3));
    }

    /// <summary>관측 → 정규화 좌표 · 에피폴라 게이트 가중치(무할당).</summary>
    private void Gate()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var V = Views;
        var gated = 0;
        double epiSum = 0; var epiN = 0;
        Array.Clear(_w); Array.Clear(_nx); Array.Clear(_ny); Array.Clear(_epi);
        for (var v = 0; v < V; v++)
        {
            ref readonly var c = ref _cams[v];
            for (var j = 0; j < J; j++)
            {
                var i = v * Pad + j;
                _nx[i] = (_u[i] - c.Cx) / c.Fx; _ny[i] = (_v[i] - c.Cy) / c.Fy;
                _w[i] = _camOn[v] && _c[i] >= MinConfidence ? _c[i] : 0f;
            }
        }
        Span<float> best = stackalloc float[MaxViews];
        Span<byte> cnt = stackalloc byte[MaxViews];
        for (var j = 0; j < J; j++)
        {
            var obs = 0;
            for (var v = 0; v < V; v++) { best[v] = float.MaxValue; if (_w[v * Pad + j] > 0) obs++; }
            if (obs < 2) continue;
            cnt.Clear();
            for (var a = 0; a < V; a++)
            {
                var ia = a * Pad + j;
                if (_w[ia] <= 0) continue;
                for (var b = a + 1; b < V; b++)
                {
                    var ib = b * Pad + j;
                    if (_w[ib] <= 0) continue;
                    var d = SymmetricEpipolarPx(_cams[a], _cams[b], _u[ia], _v[ia], _u[ib], _v[ib]);
                    epiSum += d; epiN++;
                    if (d < best[a]) best[a] = d;
                    if (d < best[b]) best[b] = d;
                    if (d < GateThresholdPx) { cnt[a]++; cnt[b]++; }
                }
            }
            for (var v = 0; v < V; v++)
            {
                var i = v * Pad + j;
                if (_w[i] <= 0) continue;
                _epi[i] = best[v];
                if (EpipolarGate && obs >= 3 && cnt[v] < Math.Max(1, (obs - 1) / 2)) { _w[i] = 0; gated++; }
            }
        }
        GatedObservations = gated;
        MeanEpipolarPx = epiN > 0 ? (float)(epiSum / epiN) : 0f;
        LastEpipolarUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── DLT(관절 = SIMD 레인) ────────────────────────────────

    /// <summary>게이트 → DLT → 재투영. 무할당.</summary>
    public void Solve()
    {
        Gate();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var round = 0; round < MaxViews - 2; round++)
        {
            switch (Path)
            {
                case TriangulationSimdPath.V256 when Vector256.IsHardwareAccelerated: SolveV256(); break;
                case TriangulationSimdPath.V128 when Vector128.IsHardwareAccelerated: SolveV128(); break;
                default: SolveScalar(); break;
            }
            if (!EpipolarGate || !RejectWorst()) break;
        }
        LastSolveUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        Finish();
        Solves++;
    }

    /// <summary>스칼라 기준 경로(레인별 SIMD 와 동일 연산 순서).</summary>
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
            _valid[j] = det > 1e-6f;
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
            for (var k = 0; k < Vector128<float>.Count; k++) _valid[j + k] = det.GetElement(k) > 1e-6f;
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
            for (var k = 0; k < Vector256<float>.Count; k++) _valid[j + k] = det.GetElement(k) > 1e-6f;
        }
    }

    /// <summary>합의 게이트: 관절별 최대 재투영 오차 관측(&gt; 임계 · 잔여 ≥ 3뷰)을 제외. 변경 여부 반환.</summary>
    private bool RejectWorst()
    {
        var changed = false;
        for (var j = 0; j < J; j++)
        {
            if (!(_valid[j] && float.IsFinite(_x[j]) && float.IsFinite(_y[j]) && float.IsFinite(_z[j]))) continue;
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

    /// <summary>레인 원시 해(비트 동일 테스트용).</summary>
    public void RawSolution(Span<float> xyz)
    {
        for (var j = 0; j < J; j++) { xyz[j * 3] = _x[j]; xyz[j * 3 + 1] = _y[j]; xyz[j * 3 + 2] = _z[j]; }
    }

    /// <summary>게이트만 수행(경로 비교 테스트용 · 무할당).</summary>
    public void PrepareOnly() => Gate();

    /// <summary>관절 물질화 · 재투영 오차 · 카메라별 통계.</summary>
    private void Finish()
    {
        Array.Clear(_camInliers); Array.Clear(_camObs); Array.Clear(_camReproj);
        double sum = 0; var n = 0; var valid = 0;
        for (var j = 0; j < J; j++)
        {
            _inl[j] = 0; _reproj[j] = 0;
            if (!_valid[j] || !float.IsFinite(_x[j]) || !float.IsFinite(_y[j]) || !float.IsFinite(_z[j])) { _valid[j] = false; continue; }
            var p = new Vector3(_x[j], _y[j], _z[j]);
            _joint[j] = p;
            valid++;
            float es = 0; var en = 0;
            for (var v = 0; v < Views; v++)
            {
                var i = v * Pad + j;
                if (_c[i] >= MinConfidence && _camOn[v]) _camObs[v]++;
                if (_w[i] <= 0) continue;
                if (!_cams[v].Project(p, out var uv)) continue;
                var e = MathF.Sqrt((uv.X - _u[i]) * (uv.X - _u[i]) + (uv.Y - _v[i]) * (uv.Y - _v[i]));
                es += e; en++;
                _camInliers[v]++; _camReproj[v] += e;
            }
            _inl[j] = (byte)en;
            if (en < 2) { _valid[j] = false; valid--; continue; }
            _reproj[j] = es / en;
            sum += _reproj[j]; n++;
        }
        for (var v = 0; v < Views; v++) if (_camInliers[v] > 0) _camReproj[v] /= _camInliers[v];
        MeanReprojPx = n > 0 ? (float)(sum / n) : 0f;
        ValidJoints = valid;
    }
}
