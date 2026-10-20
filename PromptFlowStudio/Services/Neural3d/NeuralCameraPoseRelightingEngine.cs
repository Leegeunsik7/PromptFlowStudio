using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace PromptFlowStudio.Services.Neural3d;

/// <summary>추적 결과 — 반복 수 · RMS 재투영 오차(px) · 인라이어 · 수렴 여부.</summary>
public readonly record struct PoseTrackResult(bool Converged, int Iterations, double RmsPx, int Inliers, int Points, double Micros);

/// <summary>광원 휠 — 방위/고도(°) · 강도 · 색온도(K) · 환경광 · 그림자 강도.</summary>
public readonly record struct LightRig(float AzimuthDeg = 45f, float ElevationDeg = 40f, float Intensity = 2.5f, float Kelvin = 5600f, float Ambient = 0.5f, float ShadowStrength = 0.85f);

/// <summary>
/// [PromptFlow Studio v16.58.0] 🎯 신경망 카메라 포즈 추적 &amp; 동적 리라이팅 합성기(Pure BCL · 무할당 추적/리라이트).
///   ▸ 포즈: 3DGS 랜드마크(고불투명 가우시안 중심) ↔ 2D 관측 → 배정밀 Levenberg–Marquardt PnP(6DoF · 해석적 야코비안 · Huber 가중 · 3σ 이상치 배제) →
///     등속 예측 초기값 · 서브밀리미터 수렴(잡음 0 기준 위치 오차 &lt; 0.1mm) · 프레임간 속도 추정.
///   ▸ 환경광: HDR 등장방형 환경맵(Radiance .hdr RGBE · RLE 로더 / 절차 하늘+태양) → SH 9계수 투영 → 코사인 컨볼루션 조도(Ramamoorthi–Hanrahan).
///   ▸ 리라이트: 가우시안 최소 스케일 축 = 법선(카메라 방향 정렬) · 알베도(SH DC) × (SH 조도 · 환경광 + 태양 Lambert × 그림자) + Blinn 하이라이트 →
///     출력 장면 DC 갱신(SH1 시점 의존 성분 비례 유지).
///   ▸ 그림자: 불투명도 점유 격자(48³) 광원 방향 광학 깊이 마칭 → 투과율.
/// </summary>
public sealed class NeuralCameraPoseRelightingEngine
{
    public const int MaxLandmarks = 512, OccRes = 48, EnvW = 64, EnvH = 32;

    private readonly double[] _w = new double[MaxLandmarks];
    private readonly float[] _occ = new float[OccRes * OccRes * OccRes];
    private readonly float[] _env = new float[EnvW * EnvH * 3];
    private readonly float[] _sh9 = new float[27];
    private Vector3 _occMin, _occCell;
    private long _occVersion = -1;
    private GaussianScene? _occScene;
    // 추적 상태(배정밀)
    private readonly double[] _R = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };   // 행 = right, up, forward(월드)
    private readonly double[] _C = new double[3];
    private readonly double[] _vel = new double[6];
    private bool _hasPose;
    private long _lastMicros;
    private long _tracks, _relights;
    private double _lastRms, _trackMicros, _relightMs;
    private LightRig _rig = new(AzimuthDeg: 45f);
    private Vector3 _sunDir, _sunColor;

    public NeuralCameraPoseRelightingEngine() => SetLight(new LightRig(AzimuthDeg: 45f));

    public event Action? OnChange;

    public long Tracks => Interlocked.Read(ref _tracks);
    public long Relights => Interlocked.Read(ref _relights);
    public double LastRmsPx => Volatile.Read(ref _lastRms);
    public double LastTrackMicros => Volatile.Read(ref _trackMicros);
    public double LastRelightMs => Volatile.Read(ref _relightMs);
    public LightRig Light => _rig;
    public Vector3 SunDirection => _sunDir;
    public ReadOnlySpan<float> EnvSh9 => _sh9;
    public ReadOnlySpan<float> Environment => _env;
    public bool HasPose => _hasPose;
    public string? LastAction { get; private set; }

    public Vector3 Position => new((float)_C[0], (float)_C[1], (float)_C[2]);
    public (double X, double Y, double Z) PositionPrecise => (_C[0], _C[1], _C[2]);

    // ══════════════════════ 포즈 추적 ══════════════════════

    public void ResetPose(in SplatCamera cam)
    {
        _R[0] = cam.Right.X; _R[1] = cam.Right.Y; _R[2] = cam.Right.Z;
        _R[3] = cam.Up.X; _R[4] = cam.Up.Y; _R[5] = cam.Up.Z;
        _R[6] = cam.Forward.X; _R[7] = cam.Forward.Y; _R[8] = cam.Forward.Z;
        _C[0] = cam.Position.X; _C[1] = cam.Position.Y; _C[2] = cam.Position.Z;
        Array.Clear(_vel);
        _hasPose = true;
        _lastMicros = 0;
    }

    /// <summary>추적 포즈를 카메라(내부 파라미터 유지)로 변환.</summary>
    public SplatCamera CurrentCamera(in SplatCamera intrinsics) => intrinsics.WithPose(Position,
        new Vector3((float)_R[0], (float)_R[1], (float)_R[2]), new Vector3((float)_R[3], (float)_R[4], (float)_R[5]), new Vector3((float)_R[6], (float)_R[7], (float)_R[8]));

    /// <summary>랜드마크 선택(고불투명 · 공간 분산 — 격자 간격 샘플). 반환 = 개수.</summary>
    public static int SelectLandmarks(GaussianScene s, Span<Vector3> dst, float minOpacity = 0.6f)
    {
        var n = 0;
        if (s.Count == 0) return 0;
        var stride = Math.Max(1, s.Count / Math.Max(1, dst.Length * 2));
        for (var i = 0; i < s.Count && n < dst.Length; i += stride)
            if (s.Opacity[i] >= minOpacity) dst[n++] = s.Position(i);
        return n;
    }

    /// <summary>점 투영(배정밀) — 카메라 행렬 R/C.</summary>
    private static void ProjectD(ReadOnlySpan<double> R, ReadOnlySpan<double> C, Vector3 p, double fx, double fy, double cx, double cy, out double u, out double v, out double x, out double y, out double z)
    {
        double dx = p.X - C[0], dy = p.Y - C[1], dz = p.Z - C[2];
        x = R[0] * dx + R[1] * dy + R[2] * dz;
        y = R[3] * dx + R[4] * dy + R[5] * dz;
        z = R[6] * dx + R[7] * dy + R[8] * dz;
        u = fx * x / z + cx;
        v = cy - fy * y / z;
    }

    /// <summary>
    /// 1프레임 추적(무할당) — landmarks(월드) ↔ observed(px) · 내부 파라미터는 intrinsics. tMicros 로 등속 예측.
    /// </summary>
    public PoseTrackResult Track(ReadOnlySpan<Vector3> landmarks, ReadOnlySpan<Vector2> observed, in SplatCamera intrinsics, long tMicros, int maxIterations = 30)
    {
        var t0 = Stopwatch.GetTimestamp();
        var n = Math.Min(Math.Min(landmarks.Length, observed.Length), MaxLandmarks);
        if (n < 4) return new PoseTrackResult(false, 0, double.NaN, 0, n, 0);
        if (!_hasPose) ResetPose(intrinsics);
        // 등속 예측
        var dtPred = _lastMicros > 0 ? Math.Clamp((tMicros - _lastMicros) / 1e6, 0, 0.5) : 0;
        Span<double> prevC = stackalloc double[3];
        Span<double> prevR = stackalloc double[9];
        _C.AsSpan().CopyTo(prevC); _R.AsSpan().CopyTo(prevR);
        if (dtPred > 0) Apply(_vel[0] * dtPred, _vel[1] * dtPred, _vel[2] * dtPred, _vel[3] * dtPred, _vel[4] * dtPred, _vel[5] * dtPred);

        double fx = intrinsics.Fx, fy = intrinsics.Fy, cx = intrinsics.Cx, cy = intrinsics.Cy;
        var lambda = 1e-3;
        var rms = double.MaxValue;
        var it = 0;
        var converged = false;
        Span<double> H = stackalloc double[36];
        Span<double> g = stackalloc double[6];
        Span<double> delta = stackalloc double[6];
        Span<double> saveR = stackalloc double[9];
        Span<double> saveC = stackalloc double[3];
        var inliers = n;
        for (; it < maxIterations; it++)
        {
            var cost = Linearize(landmarks, observed, n, fx, fy, cx, cy, H, g, out inliers);
            rms = Math.Sqrt(cost / Math.Max(1, inliers));
            for (var k = 0; k < 6; k++) H[k * 6 + k] *= 1 + lambda;
            for (var k = 0; k < 6; k++) H[k * 6 + k] += 1e-12;
            if (!Solve6(H, g, delta)) break;
            _R.AsSpan().CopyTo(saveR); _C.AsSpan().CopyTo(saveC);
            Apply(delta[0], delta[1], delta[2], delta[3], delta[4], delta[5]);
            var newCost = Cost(landmarks, observed, n, fx, fy, cx, cy);
            if (newCost <= cost)
            {
                lambda = Math.Max(1e-9, lambda * 0.3);
                var step = Math.Sqrt(delta[0] * delta[0] + delta[1] * delta[1] + delta[2] * delta[2]) + Math.Sqrt(delta[3] * delta[3] + delta[4] * delta[4] + delta[5] * delta[5]);
                rms = Math.Sqrt(newCost / Math.Max(1, inliers));
                if (step < 1e-10) { converged = true; it++; break; }
            }
            else
            {
                saveR.CopyTo(_R); saveC.CopyTo(_C);
                lambda *= 10;
                if (lambda > 1e8) { converged = rms < 0.5; break; }
            }
        }
        if (!converged && rms < 0.05) converged = true;
        // 속도(월드 위치 · 소각 회전) 추정
        if (_lastMicros > 0 && tMicros > _lastMicros)
        {
            var dt = (tMicros - _lastMicros) / 1e6;
            for (var k = 0; k < 3; k++) _vel[3 + k] = 0.6 * _vel[3 + k] + 0.4 * (_C[k] - prevC[k]) / dt;
            // 회전 속도: ΔR = R_prev^T·R_new 의 소각 근사(월드축)
            double wx = 0, wy = 0, wz = 0;
            for (var a = 0; a < 3; a++)
            {
                // 각 기저 벡터 변화의 외적 평균
                double ox = prevR[a * 3], oy = prevR[a * 3 + 1], oz = prevR[a * 3 + 2];
                double nx = _R[a * 3], ny = _R[a * 3 + 1], nz = _R[a * 3 + 2];
                wx += oy * nz - oz * ny; wy += oz * nx - ox * nz; wz += ox * ny - oy * nx;
            }
            _vel[0] = 0.6 * _vel[0] + 0.4 * (wx / 2) / dt; _vel[1] = 0.6 * _vel[1] + 0.4 * (wy / 2) / dt; _vel[2] = 0.6 * _vel[2] + 0.4 * (wz / 2) / dt;
        }
        _lastMicros = tMicros;
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        Volatile.Write(ref _lastRms, rms);
        Volatile.Write(ref _trackMicros, us);
        if ((Interlocked.Increment(ref _tracks) & 31) == 1) Raise();
        return new PoseTrackResult(converged, it, rms, inliers, n, us);
    }

    /// <summary>회전(월드축 소각 ω) + 이동 적용 — 기저 재직교화.</summary>
    private void Apply(double wx, double wy, double wz, double tx, double ty, double tz)
    {
        var th = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        if (th > 1e-15)
        {
            double kx = wx / th, ky = wy / th, kz = wz / th, c = Math.Cos(th), s = Math.Sin(th), t = 1 - c;
            // 월드 회전 행렬 Q (로드리게스) → 기저 벡터 b' = Q b
            double q00 = c + kx * kx * t, q01 = kx * ky * t - kz * s, q02 = kx * kz * t + ky * s;
            double q10 = ky * kx * t + kz * s, q11 = c + ky * ky * t, q12 = ky * kz * t - kx * s;
            double q20 = kz * kx * t - ky * s, q21 = kz * ky * t + kx * s, q22 = c + kz * kz * t;
            for (var a = 0; a < 3; a++)
            {
                double x = _R[a * 3], y = _R[a * 3 + 1], z = _R[a * 3 + 2];
                _R[a * 3] = q00 * x + q01 * y + q02 * z; _R[a * 3 + 1] = q10 * x + q11 * y + q12 * z; _R[a * 3 + 2] = q20 * x + q21 * y + q22 * z;
            }
            Orthonormalize();
        }
        _C[0] += tx; _C[1] += ty; _C[2] += tz;
    }

    private void Orthonormalize()
    {
        // forward 정규화 → right = up × forward → up = forward × right
        static void Norm(Span<double> v, int o) { var l = Math.Sqrt(v[o] * v[o] + v[o + 1] * v[o + 1] + v[o + 2] * v[o + 2]); v[o] /= l; v[o + 1] /= l; v[o + 2] /= l; }
        var r = _R.AsSpan();
        Norm(r, 6);
        double fx = r[6], fy = r[7], fz = r[8], ux = r[3], uy = r[4], uz = r[5];
        r[0] = uy * fz - uz * fy; r[1] = uz * fx - ux * fz; r[2] = ux * fy - uy * fx;
        Norm(r, 0);
        double rx = r[0], ry = r[1], rz = r[2];
        r[3] = fy * rz - fz * ry; r[4] = fz * rx - fx * rz; r[5] = fx * ry - fy * rx;
    }

    private double Cost(ReadOnlySpan<Vector3> L, ReadOnlySpan<Vector2> O, int n, double fx, double fy, double cx, double cy)
    {
        double c = 0;
        for (var i = 0; i < n; i++)
        {
            if (_w[i] <= 0) continue;
            ProjectD(_R, _C, L[i], fx, fy, cx, cy, out var u, out var v, out _, out _, out var z);
            if (z <= 1e-6) { c += 1e6; continue; }
            double du = u - O[i].X, dv = v - O[i].Y;
            c += _w[i] * (du * du + dv * dv);
        }
        return c;
    }

    /// <summary>가중 정규방정식 HΔ = −Jᵀr. 파라미터 = (ω 월드 회전, δC). 반환 = 가중 비용.</summary>
    private double Linearize(ReadOnlySpan<Vector3> L, ReadOnlySpan<Vector2> O, int n, double fx, double fy, double cx, double cy, Span<double> H, Span<double> g, out int inliers)
    {
        H.Clear(); g.Clear();
        // 1차: 잔차 · 중앙값 기반 Huber 임계
        Span<double> r2 = stackalloc double[MaxLandmarks];
        for (var i = 0; i < n; i++)
        {
            ProjectD(_R, _C, L[i], fx, fy, cx, cy, out var u, out var v, out _, out _, out var z);
            r2[i] = z <= 1e-6 ? 1e12 : (u - O[i].X) * (u - O[i].X) + (v - O[i].Y) * (v - O[i].Y);
        }
        var sorted = r2[..n];
        Span<double> tmp = stackalloc double[MaxLandmarks];
        sorted.CopyTo(tmp);
        tmp[..n].Sort();
        var med = Math.Sqrt(tmp[n / 2]);
        var huber = Math.Max(1.0, 1.5 * med);
        var outlier = Math.Max(3.0, 6.0 * med);
        double cost = 0;
        inliers = 0;
        Span<double> ju = stackalloc double[6];
        Span<double> jv = stackalloc double[6];
        for (var i = 0; i < n; i++)
        {
            var e = Math.Sqrt(r2[i]);
            if (e > outlier) { _w[i] = 0; continue; }
            _w[i] = e <= huber ? 1 : huber / e;
            inliers++;
            ProjectD(_R, _C, L[i], fx, fy, cx, cy, out var u, out var v, out var x, out var y, out var z);
            double ru = u - O[i].X, rv = v - O[i].Y;
            cost += _w[i] * (ru * ru + rv * rv);
            // ∂(u,v)/∂(x,y,z)
            double iz = 1 / z, iz2 = iz * iz;
            double du_dx = fx * iz, du_dz = -fx * x * iz2, dv_dy = -fy * iz, dv_dz = fy * y * iz2;
            // 카메라 좌표 c = R(p − C). 월드 회전 ω: R' = R·Qᵀ → ∂c/∂ω = R·[d]×  (d = p − C) ; ∂c/∂C = −R
            double dx = L[i].X - _C[0], dy = L[i].Y - _C[1], dz = L[i].Z - _C[2];
            ju.Clear(); jv.Clear();
            for (var k = 0; k < 3; k++)
            {
                // e_k × d  (기저 회전 시 b' = b + ω×b → c_a = b_a·d 의 미분 = (ω×b_a)·d = ω·(b_a×d))
                double bxdx, bxdy, bxdz;
                double rx = _R[k * 3], ry = _R[k * 3 + 1], rz = _R[k * 3 + 2];
                bxdx = ry * dz - rz * dy; bxdy = rz * dx - rx * dz; bxdz = rx * dy - ry * dx;
                // dc_k/dω = b_k × d ; dc_k/dC = −b_k
                if (k == 0)
                {
                    ju[0] += du_dx * bxdx; ju[1] += du_dx * bxdy; ju[2] += du_dx * bxdz;
                    ju[3] += -du_dx * rx; ju[4] += -du_dx * ry; ju[5] += -du_dx * rz;
                }
                else if (k == 1)
                {
                    jv[0] += dv_dy * bxdx; jv[1] += dv_dy * bxdy; jv[2] += dv_dy * bxdz;
                    jv[3] += -dv_dy * rx; jv[4] += -dv_dy * ry; jv[5] += -dv_dy * rz;
                }
                else
                {
                    ju[0] += du_dz * bxdx; ju[1] += du_dz * bxdy; ju[2] += du_dz * bxdz;
                    ju[3] += -du_dz * rx; ju[4] += -du_dz * ry; ju[5] += -du_dz * rz;
                    jv[0] += dv_dz * bxdx; jv[1] += dv_dz * bxdy; jv[2] += dv_dz * bxdz;
                    jv[3] += -dv_dz * rx; jv[4] += -dv_dz * ry; jv[5] += -dv_dz * rz;
                }
            }
            var w = _w[i];
            for (var a = 0; a < 6; a++)
            {
                g[a] -= w * (ju[a] * ru + jv[a] * rv);
                for (var b = 0; b < 6; b++) H[a * 6 + b] += w * (ju[a] * ju[b] + jv[a] * jv[b]);
            }
        }
        return cost;
    }

    private static bool Solve6(Span<double> H, ReadOnlySpan<double> g, Span<double> x)
    {
        Span<double> m = stackalloc double[42];
        for (var r = 0; r < 6; r++) { for (var c = 0; c < 6; c++) m[r * 7 + c] = H[r * 6 + c]; m[r * 7 + 6] = g[r]; }
        for (var col = 0; col < 6; col++)
        {
            var piv = col;
            for (var r = col + 1; r < 6; r++) if (Math.Abs(m[r * 7 + col]) > Math.Abs(m[piv * 7 + col])) piv = r;
            if (Math.Abs(m[piv * 7 + col]) < 1e-30) return false;
            if (piv != col) for (var c = 0; c < 7; c++) (m[col * 7 + c], m[piv * 7 + c]) = (m[piv * 7 + c], m[col * 7 + c]);
            for (var r = 0; r < 6; r++)
            {
                if (r == col) continue;
                var f = m[r * 7 + col] / m[col * 7 + col];
                if (f == 0) continue;
                for (var c = col; c < 7; c++) m[r * 7 + c] -= f * m[col * 7 + c];
            }
        }
        for (var r = 0; r < 6; r++) x[r] = m[r * 7 + 6] / m[r * 7 + r];
        return true;
    }

    // ══════════════════════ 환경광 · SH9 ══════════════════════

    /// <summary>색온도(K) → 선형 RGB(정규화 · Tanner Helland 근사).</summary>
    public static Vector3 KelvinToRgb(float k)
    {
        var t = Math.Clamp(k, 1500f, 15000f) / 100f;
        float r, g, b;
        if (t <= 66) { r = 255; g = 99.4708f * MathF.Log(t) - 161.1196f; b = t <= 19 ? 0 : 138.5177f * MathF.Log(t - 10) - 305.0448f; }
        else { r = 329.6987f * MathF.Pow(t - 60, -0.1332f); g = 288.1222f * MathF.Pow(t - 60, -0.0755f); b = 255; }
        var c = new Vector3(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255)) / 255f;
        return c / MathF.Max(c.X, MathF.Max(c.Y, c.Z));
    }

    private static Vector3 DirOf(float azDeg, float elDeg)
    {
        float az = azDeg * MathF.PI / 180f, el = elDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el));
    }

    /// <summary>광원 휠 적용 → 절차 HDR 하늘(천정 그라데이션 + 지면 반사 + 태양 원반) 생성 · SH9 투영.</summary>
    public void SetLight(in LightRig rig)
    {
        _rig = rig;
        _sunDir = DirOf(rig.AzimuthDeg, Math.Clamp(rig.ElevationDeg, -10f, 90f));
        _sunColor = KelvinToRgb(rig.Kelvin) * rig.Intensity;
        var sky = Vector3.Lerp(new Vector3(0.35f, 0.5f, 0.85f), KelvinToRgb(rig.Kelvin * 1.6f), 0.3f) * rig.Ambient;
        var ground = new Vector3(0.25f, 0.22f, 0.2f) * rig.Ambient;
        for (var y = 0; y < EnvH; y++)
            for (var x = 0; x < EnvW; x++)
            {
                var d = EnvDir(x, y);
                var c = d.Y >= 0 ? Vector3.Lerp(sky * 0.6f, sky, d.Y) : ground;
                var sd = Vector3.Dot(d, _sunDir);
                if (sd > 0.995f) c += _sunColor * 40f;          // 태양 원반(≈5.7°)
                else if (sd > 0.9f) c += _sunColor * 0.3f * (sd - 0.9f) / 0.095f;
                var o = (y * EnvW + x) * 3;
                _env[o] = c.X; _env[o + 1] = c.Y; _env[o + 2] = c.Z;
            }
        ProjectSh9(_env, EnvW, EnvH, _sh9);
        LastAction = "💡 광원 " + rig.AzimuthDeg.ToString("0", CultureInfo.InvariantCulture) + "°/" + rig.ElevationDeg.ToString("0", CultureInfo.InvariantCulture) + "° · "
            + rig.Kelvin.ToString("0", CultureInfo.InvariantCulture) + "K";
        Raise();
    }

    private static Vector3 EnvDir(float x, float y, int w = EnvW, int h = EnvH)
    {
        var phi = (x + 0.5f) / w * MathF.Tau - MathF.PI;
        var theta = (y + 0.5f) / h * MathF.PI;
        return new Vector3(MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta), -MathF.Sin(theta) * MathF.Cos(phi));
    }

    /// <summary>등장방형 HDR → SH9(RGB · 입체각 가중).</summary>
    public static void ProjectSh9(ReadOnlySpan<float> env, int w, int h, Span<float> sh)
    {
        sh[..27].Clear();
        Span<float> b = stackalloc float[9];
        for (var y = 0; y < h; y++)
        {
            var theta = (y + 0.5f) / h * MathF.PI;
            var dw = MathF.Sin(theta) * (MathF.PI / h) * (MathF.Tau / w);
            for (var x = 0; x < w; x++)
            {
                var d = EnvDir(x, y, w, h);
                ShBasis9(d, b);
                var o = (y * w + x) * 3;
                for (var k = 0; k < 9; k++)
                {
                    sh[k * 3] += env[o] * b[k] * dw; sh[k * 3 + 1] += env[o + 1] * b[k] * dw; sh[k * 3 + 2] += env[o + 2] * b[k] * dw;
                }
            }
        }
    }

    private static void ShBasis9(Vector3 d, Span<float> b)
    {
        b[0] = 0.282095f;
        b[1] = 0.488603f * d.Y; b[2] = 0.488603f * d.Z; b[3] = 0.488603f * d.X;
        b[4] = 1.092548f * d.X * d.Y; b[5] = 1.092548f * d.Y * d.Z; b[6] = 0.315392f * (3 * d.Z * d.Z - 1);
        b[7] = 1.092548f * d.X * d.Z; b[8] = 0.546274f * (d.X * d.X - d.Y * d.Y);
    }

    /// <summary>법선 조도 E(n)/π (Lambert 반사 계수 1 기준 · 코사인 컨볼루션).</summary>
    public Vector3 Irradiance(Vector3 n)
    {
        Span<float> b = stackalloc float[9];
        ShBasis9(n, b);
        const float a0 = MathF.PI, a1 = 2f * MathF.PI / 3f, a2 = MathF.PI / 4f;
        Vector3 e = Vector3.Zero;
        for (var k = 0; k < 9; k++)
        {
            var a = k == 0 ? a0 : k < 4 ? a1 : a2;
            e += new Vector3(_sh9[k * 3], _sh9[k * 3 + 1], _sh9[k * 3 + 2]) * (a * b[k]);
        }
        return Vector3.Max(e / MathF.PI, Vector3.Zero);
    }

    /// <summary>Radiance .hdr(RGBE · 신규 RLE / 평문) 로더 → 내부 64×32 환경맵 다운샘플 · SH9 재투영. 반환 = 성공.</summary>
    public bool LoadHdr(string path)
    {
        var data = File.ReadAllBytes(path);
        var p = 0;
        int w = 0, h = 0;
        var sb = new StringBuilder();
        while (p < data.Length)
        {
            var b = data[p++];
            if (b != '\n') { sb.Append((char)b); continue; }
            var line = sb.ToString(); sb.Clear();
            if (line.StartsWith("-Y ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) break;
                return false;
            }
        }
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return false;
        var acc = new double[EnvW * EnvH * 3];
        var cnt = new int[EnvW * EnvH];
        var scan = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            if (p + 4 > data.Length) return false;
            if (w >= 8 && w < 32768 && data[p] == 2 && data[p + 1] == 2 && (data[p + 2] << 8 | data[p + 3]) == w)
            {
                p += 4;
                for (var c = 0; c < 4; c++)
                {
                    var x = 0;
                    while (x < w)
                    {
                        if (p >= data.Length) return false;
                        int count = data[p++];
                        if (count > 128) { count -= 128; var v = data[p++]; for (var k = 0; k < count && x < w; k++) scan[(x++) * 4 + c] = v; }
                        else for (var k = 0; k < count && x < w; k++) scan[(x++) * 4 + c] = data[p++];
                    }
                }
            }
            else
            {
                if (p + w * 4 > data.Length) return false;
                Array.Copy(data, p, scan, 0, w * 4); p += w * 4;
            }
            var ty = y * EnvH / h;
            for (var x = 0; x < w; x++)
            {
                var e = scan[x * 4 + 3];
                if (e == 0) continue;
                var f = MathF.ScaleB(1f, e - 136);
                var ti = ty * EnvW + x * EnvW / w;
                acc[ti * 3] += scan[x * 4] * f; acc[ti * 3 + 1] += scan[x * 4 + 1] * f; acc[ti * 3 + 2] += scan[x * 4 + 2] * f;
                cnt[ti]++;
            }
        }
        for (var i = 0; i < cnt.Length; i++)
            for (var c = 0; c < 3; c++) _env[i * 3 + c] = cnt[i] > 0 ? (float)(acc[i * 3 + c] / cnt[i]) : 0f;
        ProjectSh9(_env, EnvW, EnvH, _sh9);
        // 최대 휘도 방향을 태양으로
        var best = 0; var bl = -1f;
        for (var i = 0; i < EnvW * EnvH; i++) { var l = _env[i * 3] + _env[i * 3 + 1] + _env[i * 3 + 2]; if (l > bl) { bl = l; best = i; } }
        _sunDir = EnvDir(best % EnvW, best / EnvW);
        _sunColor = new Vector3(_env[best * 3], _env[best * 3 + 1], _env[best * 3 + 2]) / MathF.Max(1f, bl / 3f) * _rig.Intensity;
        LastAction = "🌅 HDR 환경맵 " + w.ToString(CultureInfo.InvariantCulture) + "×" + h.ToString(CultureInfo.InvariantCulture);
        Raise();
        return true;
    }

    // ══════════════════════ 그림자 · 리라이트 ══════════════════════

    private void BuildOccupancy(GaussianScene s)
    {
        Array.Clear(_occ);
        var (mn, mx) = s.Bounds();
        var pad = (mx - mn) * 0.05f + new Vector3(0.02f);
        _occMin = mn - pad;
        _occCell = (mx - mn + pad * 2f) / OccRes;
        for (var i = 0; i < s.Count; i++)
        {
            var c = (s.Position(i) - _occMin) / _occCell;
            int x = (int)c.X, y = (int)c.Y, z = (int)c.Z;
            if ((uint)x >= OccRes || (uint)y >= OccRes || (uint)z >= OccRes) continue;
            var sc = s.ScaleOf(i);
            var area = MathF.Max(sc.X * sc.Y, MathF.Max(sc.Y * sc.Z, sc.X * sc.Z)) * 4f;
            var cellArea = MathF.Max(_occCell.X * _occCell.Z, 1e-8f);
            _occ[(z * OccRes + y) * OccRes + x] += s.Opacity[i] * MathF.Min(1f, area / cellArea);
        }
        _occScene = s; _occVersion = s.Version;
    }

    /// <summary>광원 방향 투과율(0..1) — 점유 격자 마칭.</summary>
    public float ShadowTransmittance(Vector3 p, Vector3 toLight)
    {
        var step = MathF.Min(_occCell.X, MathF.Min(_occCell.Y, _occCell.Z));
        var pos = p + toLight * step * 1.5f;
        var od = 0f;
        for (var k = 0; k < OccRes * 2; k++)
        {
            var c = (pos - _occMin) / _occCell;
            int x = (int)c.X, y = (int)c.Y, z = (int)c.Z;
            if ((uint)x >= OccRes || (uint)y >= OccRes || (uint)z >= OccRes) break;
            od += _occ[(z * OccRes + y) * OccRes + x];
            if (od > 6f) break;
            pos += toLight * step;
        }
        return MathF.Exp(-od);
    }

    /// <summary>
    /// 리라이트(무할당 · 점유 격자는 장면 버전 변경 시 재구성) — src 알베도 → dst 색(DC + 비례 SH1). viewer = 법선 정렬 기준(카메라 위치).
    /// 반환 = 그림자 영향 가우시안 수.
    /// </summary>
    public int Relight(GaussianScene src, GaussianScene dst, Vector3 viewer, float gloss = 0.25f)
    {
        var t0 = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(_occScene, src) || _occVersion != src.Version) BuildOccupancy(src);
        if (!ReferenceEquals(src, dst)) src.CopyTo(dst);
        var shadowed = 0;
        var L = _sunDir;
        var aboveHorizon = L.Y > -0.05f;
        var strength = _rig.ShadowStrength;
        for (var i = 0; i < src.Count; i++)
        {
            var p = src.Position(i);
            var sc = src.ScaleOf(i);
            var q = src.Rotation(i);
            var axis = sc.X <= sc.Y && sc.X <= sc.Z ? Vector3.UnitX : sc.Y <= sc.Z ? Vector3.UnitY : Vector3.UnitZ;
            var n = Vector3.Transform(axis, q);
            var toView = Vector3.Normalize(viewer - p);
            if (Vector3.Dot(n, toView) < 0) n = -n;
            var albedo = src.BaseColor(i);
            var ambient = Irradiance(n);
            var ndl = aboveHorizon ? MathF.Max(0f, Vector3.Dot(n, L)) : 0f;
            var vis = 1f;
            if (ndl > 0)
            {
                var tr = ShadowTransmittance(p, L);
                vis = 1f - strength * (1f - tr);
                if (tr < 0.6f) shadowed++;
            }
            var h = Vector3.Normalize(L + toView);
            var spec = gloss * MathF.Pow(MathF.Max(0f, Vector3.Dot(n, h)), 32f) * vis * (ndl > 0 ? 1f : 0f);
            var lit = albedo * (ambient + _sunColor * (ndl * vis) * 0.35f) + _sunColor * spec * 0.1f;
            lit = Vector3.Clamp(lit, Vector3.Zero, new Vector3(1.5f));
            var o = i * GaussianScene.ShStride;
            for (var c = 0; c < 3; c++)
            {
                var a = c == 0 ? albedo.X : c == 1 ? albedo.Y : albedo.Z;
                var l = c == 0 ? lit.X : c == 1 ? lit.Y : lit.Z;
                var ratio = a > 1e-3f ? l / a : 1f;
                dst.Sh[o + c] = GaussianScene.ToDc(l);
                dst.Sh[o + 3 + c] = src.Sh[o + 3 + c] * ratio;
                dst.Sh[o + 6 + c] = src.Sh[o + 6 + c] * ratio;
                dst.Sh[o + 9 + c] = src.Sh[o + 9 + c] * ratio;
            }
        }
        dst.Version++;
        Volatile.Write(ref _relightMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        if ((Interlocked.Increment(ref _relights) & 7) == 1) Raise();
        return shadowed;
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Gs.PoseRelight.OnChange", ex); }
    }
}
