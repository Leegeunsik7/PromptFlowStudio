using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.SpatialSlam;

/// <summary>루프 폐쇄 기록.</summary>
public readonly record struct LoopClosure(long Frame, long KeyframeFrame, float CorrectionM, float CorrectionDeg, float Rms, float Inliers, float ErrorBeforeM, float ErrorAfterM);

/// <summary>
/// [PromptFlow Studio v16.98.0] 🗺️ 실시간 공간 6DoF SLAM 트래킹 엔진.
/// · 전처리: 깊이 → 정점 맵 Vector&lt;float&gt; 역투영(열 계수 사전 계산) · ±2 픽셀 중심 차분 외적 법선 · 곡률 특징점(법선 변화) 추출.
/// · 추적: 등속 예측 → 기준 키프레임(실패 시 직전 프레임) 투영 데이터 연관 점-평면 ICP(6×6 정규방정식 · 야코비 고유분해 퇴화 축은 예측 유지) · 거리/법선 게이트.
/// · 키프레임(이동 0.3 m / 15°): 깊이 보관 + 8×6 깊이 기술자(영평균 정규화 · SIMD 내적).
/// · 루프 폐쇄: 최근 LoopMinGap 이전 키프레임 기술자 최근접 → 키프레임 정점 맵에 항등 초기값 ICP(드리프트 크기 무관) → RMS/인라이어 검증 →
///   보정 Δ를 키프레임 이후 궤적에 선형 분배(slerp) · 콜백으로 TSDF 재융합.
/// · "신경망"은 명칭 계승 — 학습 모델 없이 결정적 기하 알고리즘. 무할당(루프 검증 포함 사전 할당 scratch).
/// </summary>
public sealed class NeuralSpatialSlamTrackingEngine
{
    public const int MaxKeyframes = 48, TrajCap = 4096, DescW = 8, DescH = 6, NormalRadius = 2;
    private static readonly int DescPad = (DescW * DescH + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    private sealed class Maps
    {
        public readonly float[] Vx, Vy, Vz, Nx, Ny, Nz;
        public Maps(int n) { Vx = new float[n]; Vy = new float[n]; Vz = new float[n]; Nx = new float[n]; Ny = new float[n]; Nz = new float[n]; }
    }

    private readonly object _gate = new();
    private readonly float[] _ku, _row;
    private Maps _cur, _prev;
    private readonly Maps _ref, _refMaps;
    private int _refTraj;
    private long _refFrame = -1;
    private readonly float[][] _kfDepth = new float[MaxKeyframes][];
    private readonly float[][] _kfDesc = new float[MaxKeyframes][];
    private readonly long[] _kfFrame = new long[MaxKeyframes];
    private readonly int[] _kfTraj = new int[MaxKeyframes];
    private readonly float[] _desc;
    private readonly SlamPose[] _traj = new SlamPose[TrajCap], _gt = new SlamPose[TrajCap];
    private readonly bool[] _hasGt = new bool[TrajCap];
    private readonly long[] _trajFrame = new long[TrajCap];
    private readonly LoopClosure[] _loops = new LoopClosure[64];
    private int _trajCount, _kfCount, _loopCount, _features;
    private SlamPose _pose = SlamPose.Identity, _prevPose = SlamPose.Identity, _vel = SlamPose.Identity;
    private SlamPose _raw = SlamPose.Identity, _rawPrev = SlamPose.Identity, _rawRef = SlamPose.Identity, _drift = SlamPose.Identity;
    private bool _init;
    private long _fallbacks, _frames, _lost, _lastLoopFrame = long.MinValue / 2;

    public NeuralSpatialSlamTrackingEngine(int width = 160, int height = 120, float fx = 120, float fy = 120, float cx = 80, float cy = 60)
    {
        Width = width; Height = height; Fx = fx; Fy = fy; Cx = cx; Cy = cy;
        var n = width * height;
        _cur = new Maps(n); _prev = new Maps(n); _ref = new Maps(n); _refMaps = new Maps(n);
        var pad = (width + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        _ku = new float[pad]; _row = new float[pad];
        for (var u = 0; u < width; u++) _ku[u] = (u + 0.5f - cx) / fx;
        for (var k = 0; k < MaxKeyframes; k++) { _kfDepth[k] = new float[n]; _kfDesc[k] = new float[DescPad]; }
        _desc = new float[DescPad];
    }

    /// <summary>루프 폐쇄 후(키프레임 프레임 → 보정 궤적) — TSDF 재융합 등.</summary>
    public event Action<LoopClosure>? LoopClosed;

    public int Width { get; }
    public int Height { get; }
    public float Fx { get; }
    public float Fy { get; }
    public float Cx { get; }
    public float Cy { get; }
    public int Stride { get; set; } = 2;
    public int MaxIterations { get; set; } = 20;
    public float MaxCorrDist { get; set; } = 0.12f;
    public float MinNormalDot { get; set; } = 0.8f;
    public double Damping { get; set; } = 1e-6;
    /// <summary>퇴화 판정 고유값 비(λ/λmax).</summary>
    public double DegeneracyRatio { get; set; } = 5e-3;
    public float VelocityAlpha { get; set; } = 0.3f;
    public float KeyframeDistM { get; set; } = 0.3f;
    public float KeyframeAngleDeg { get; set; } = 15f;
    public int LoopMinGap { get; set; } = 120;
    public float LoopDescThreshold { get; set; } = 0.12f;
    public float LoopMaxRms { get; set; } = 0.03f;
    public float LoopMinInliers { get; set; } = 0.5f;
    public bool LoopClosureEnabled { get; set; } = true;
    /// <summary>시뮬레이션 드리프트 주입(IMU/오도메트리 바이어스 대역 · 프레임당 월드 요 ° · 월드 x mm).</summary>
    public float InjectDriftDeg { get; set; }
    public float InjectDriftMm { get; set; }

    public SlamPose Pose { get { lock (_gate) return _pose; } }
    public long Frames => Interlocked.Read(ref _frames);
    public long Lost => Interlocked.Read(ref _lost);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public int Features => _features;
    public int KeyframeCount => _kfCount;
    public int LoopCount => _loopCount;
    public int TrajectoryCount => _trajCount;
    public int LastIterations { get; private set; }
    public int LastDegenerateAxes { get; private set; }
    public float LastRms { get; private set; }
    public float LastInlierRatio { get; private set; }
    public double LastTrackUs { get; private set; }
    public double LastLoopUs { get; private set; }
    public float LastDescDistance { get; private set; } = float.NaN;

    public SlamPose TrajectoryPose(int i) => _traj[i];
    public bool TryGroundTruth(int i, out SlamPose p) { p = _gt[i]; return _hasGt[i]; }
    public long TrajectoryFrame(int i) => _trajFrame[i];
    public long KeyframeFrame(int k) => _kfFrame[k];
    public SlamPose KeyframePose(int k) => _traj[_kfTraj[k]];
    public ReadOnlySpan<float> KeyframeDepth(int k) => _kfDepth[k];
    public LoopClosure Loop(int i) => _loops[i % _loops.Length];

    public void Reset(SlamPose initial)
    {
        lock (_gate)
        {
            _pose = _prevPose = _raw = _rawPrev = _rawRef = initial; _vel = SlamPose.Identity; _drift = SlamPose.Identity;
            _init = false; _trajCount = 0; _kfCount = 0; _refFrame = -1; _refTraj = 0; _loopCount = 0; _frames = 0; _lost = 0; _lastLoopFrame = long.MinValue / 2;
            Array.Clear(_hasGt);
        }
    }

    /// <summary>현재 추정 오차(m) — 지상 진실 없으면 NaN.</summary>
    public float CurrentErrorM { get { lock (_gate) return _trajCount > 0 && _hasGt[_trajCount - 1] ? (_traj[_trajCount - 1].T - _gt[_trajCount - 1].T).Length() : float.NaN; } }

    /// <summary>ATE RMSE(m · 지상 진실 보유 프레임).</summary>
    public float AteRmse
    {
        get
        {
            lock (_gate)
            {
                double s = 0; var n = 0;
                for (var i = 0; i < _trajCount; i++) if (_hasGt[i]) { s += (_traj[i].T - _gt[i].T).LengthSquared(); n++; }
                return n == 0 ? float.NaN : (float)Math.Sqrt(s / n);
            }
        }
    }

    // ── 전처리 ──
    private void BuildMaps(ReadOnlySpan<float> depth, Maps m)
    {
        int w = Width, h = Height;
        var vk = MemoryMarshal.Cast<float, Vector<float>>(_ku.AsSpan());
        var vr = MemoryMarshal.Cast<float, Vector<float>>(_row.AsSpan());
        for (var v = 0; v < h; v++)
        {
            var kv = -(v + 0.5f - Cy) / Fy;
            var o = v * w;
            depth.Slice(o, w).CopyTo(_row);
            var vkv = new Vector<float>(kv);
            for (var k = 0; k < vk.Length; k++)
            {
                var d = vr[k];
                var x = vk[k] * d; var y = vkv * d; var z = -d;
                var lim = Math.Min(Vector<float>.Count, w - k * Vector<float>.Count);
                for (var l = 0; l < lim; l++)
                {
                    var i = o + k * Vector<float>.Count + l;
                    m.Vx[i] = x[l]; m.Vy[i] = y[l]; m.Vz[i] = z[l];
                }
            }
        }
        var feats = 0;
        for (var v = 0; v < h; v++)
            for (var u = 0; u < w; u++)
            {
                var i = v * w + u;
                const int r = NormalRadius;
                if (u < r || v < r || u >= w - r || v >= h - r || m.Vz[i] >= 0 || m.Vz[i - r] >= 0 || m.Vz[i + r] >= 0 || m.Vz[i - r * w] >= 0 || m.Vz[i + r * w] >= 0) { m.Nx[i] = m.Ny[i] = m.Nz[i] = 0; continue; }
                var p = new Vector3(m.Vx[i], m.Vy[i], m.Vz[i]);
                // 중심 차분(±r 픽셀) — 깊이 잡음에 강건한 법선
                var a = new Vector3(m.Vx[i + r] - m.Vx[i - r], m.Vy[i + r] - m.Vy[i - r], m.Vz[i + r] - m.Vz[i - r]);
                var b = new Vector3(m.Vx[i + r * w] - m.Vx[i - r * w], m.Vy[i + r * w] - m.Vy[i - r * w], m.Vz[i + r * w] - m.Vz[i - r * w]);
                var depthJump = 0.08f * r * -p.Z;
                if (a.LengthSquared() > depthJump * depthJump || b.LengthSquared() > depthJump * depthJump) { m.Nx[i] = m.Ny[i] = m.Nz[i] = 0; continue; }
                var n = Vector3.Cross(b, a);
                var len = n.Length();
                if (len < 1e-9f) { m.Nx[i] = m.Ny[i] = m.Nz[i] = 0; continue; }
                n /= len;
                if (Vector3.Dot(n, p) > 0) n = -n;
                m.Nx[i] = n.X; m.Ny[i] = n.Y; m.Nz[i] = n.Z;
            }
        // 곡률 특징점: 이웃 법선 내적 < 0.9(모서리/가구 경계)
        for (var v = 1; v < h - 2; v += 2)
            for (var u = 1; u < w - 2; u += 2)
            {
                var i = v * w + u;
                if (m.Nz[i] == 0 && m.Nx[i] == 0 && m.Ny[i] == 0) continue;
                var j = i + 2;
                if (m.Nx[j] == 0 && m.Ny[j] == 0 && m.Nz[j] == 0) continue;
                if (m.Nx[i] * m.Nx[j] + m.Ny[i] * m.Ny[j] + m.Nz[i] * m.Nz[j] < 0.9f) feats++;
            }
        _features = feats;
    }

    private void Descriptor(ReadOnlySpan<float> depth, Span<float> desc)
    {
        desc.Clear();
        int cw = Width / DescW, ch = Height / DescH;
        for (var gy = 0; gy < DescH; gy++)
            for (var gx = 0; gx < DescW; gx++)
            {
                float s = 0; var c = 0;
                for (var y = gy * ch; y < (gy + 1) * ch; y++)
                    for (var x = gx * cw; x < (gx + 1) * cw; x++) { var d = depth[y * Width + x]; if (d > 0) { s += d; c++; } }
                desc[gy * DescW + gx] = c > 0 ? s / c : 0;
            }
        var n = DescW * DescH;
        float mean = 0; for (var i = 0; i < n; i++) mean += desc[i]; mean /= n;
        float norm = 0; for (var i = 0; i < n; i++) { desc[i] -= mean; norm += desc[i] * desc[i]; }
        norm = MathF.Sqrt(Math.Max(norm, 1e-12f));
        for (var i = 0; i < n; i++) desc[i] /= norm;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a); var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (var i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        return Vector.Dot(acc, Vector<float>.One);
    }

    // ── ICP ──
    /// <summary>
    /// 점-평면 ICP: 현재 맵(카메라) → 참조 맵(refPose 카메라) 투영 연관. pose = 현재 카메라→월드(초기값 · 결과).
    /// </summary>
    private int Icp(Maps cur, Maps rf, SlamPose refPose, ref SlamPose pose, int iters, out float rms, out float inlierRatio)
    {
        Span<double> a = stackalloc double[36];
        Span<double> b = stackalloc double[6];
        Span<double> x = stackalloc double[6];
        int w = Width, h = Height, st = Math.Max(1, Stride);
        var refInv = refPose.Inverse();
        rms = float.MaxValue; inlierRatio = 0;
        var it = 0;
        for (; it < iters; it++)
        {
            a.Clear(); b.Clear();
            double se = 0; int inl = 0, tot = 0;
            var toRef = refInv * pose;
            for (var v = 0; v < h; v += st)
                for (var u = 0; u < w; u += st)
                {
                    var i = v * w + u;
                    if (cur.Vz[i] >= 0 || (cur.Nx[i] == 0 && cur.Ny[i] == 0 && cur.Nz[i] == 0)) continue;
                    tot++;
                    var pc = new Vector3(cur.Vx[i], cur.Vy[i], cur.Vz[i]);
                    var pr = toRef.Apply(pc);
                    if (pr.Z >= -0.05f) continue;
                    var ru = (int)(Fx * pr.X / -pr.Z + Cx); var rv = (int)(-Fy * pr.Y / -pr.Z + Cy);
                    if ((uint)ru >= (uint)w || (uint)rv >= (uint)h) continue;
                    var j = rv * w + ru;
                    if (rf.Vz[j] >= 0 || (rf.Nx[j] == 0 && rf.Ny[j] == 0 && rf.Nz[j] == 0)) continue;
                    var p = pose.Apply(pc);
                    var q = refPose.Apply(new Vector3(rf.Vx[j], rf.Vy[j], rf.Vz[j]));
                    var n = refPose.Rotate(new Vector3(rf.Nx[j], rf.Ny[j], rf.Nz[j]));
                    var diff = p - q;
                    if (diff.LengthSquared() > MaxCorrDist * MaxCorrDist) continue;
                    if (Vector3.Dot(n, pose.Rotate(new Vector3(cur.Nx[i], cur.Ny[i], cur.Nz[i]))) < MinNormalDot) continue;
                    var r = Vector3.Dot(n, diff);
                    var c = Vector3.Cross(p, n);
                    double j0 = c.X, j1 = c.Y, j2 = c.Z, j3 = n.X, j4 = n.Y, j5 = n.Z;
                    Accumulate(a, b, j0, j1, j2, j3, j4, j5, r);
                    se += r * r; inl++;
                }
            if (inl < 30) { rms = float.MaxValue; inlierRatio = tot == 0 ? 0 : (float)inl / tot; return it; }
            rms = (float)Math.Sqrt(se / inl);
            inlierRatio = (float)inl / Math.Max(1, tot);
            if (!SolveDegeneracyAware(a, b, x)) return it;
            var om = new Vector3((float)-x[0], (float)-x[1], (float)-x[2]);
            var tau = new Vector3((float)-x[3], (float)-x[4], (float)-x[5]);
            var ang = om.Length();
            var dq = ang > 1e-12f ? Quaternion.CreateFromAxisAngle(om / ang, ang) : Quaternion.Identity;
            var delta = new SlamPose(dq, tau);
            pose = delta * pose;
            if (ang < 1e-5f && tau.Length() < 1e-5f) { it++; break; }
        }
        return it;
    }

    private static void Accumulate(Span<double> a, Span<double> b, double j0, double j1, double j2, double j3, double j4, double j5, double r)
    {
        Span<double> j = stackalloc double[6] { j0, j1, j2, j3, j4, j5 };
        for (var p = 0; p < 6; p++)
        {
            b[p] += j[p] * r;
            for (var q = p; q < 6; q++) a[p * 6 + q] += j[p] * j[q];
        }
    }

    /// <summary>
    /// 퇴화 인지 해(solution remapping): A(상삼각) 야코비 고유분해 → λ &lt; DegeneracyRatio·λmax 축은 갱신 0(등속 예측 유지) · 나머지 축 = (vᵀb/λ) v.
    /// 평면만 보이는 시야(벽+바닥)에서 미구속 병진/요가 잡음으로 미끄러지는 것을 차단.
    /// </summary>
    private bool SolveDegeneracyAware(Span<double> a, Span<double> b, Span<double> x)
    {
        for (var p = 0; p < 6; p++) for (var q = 0; q < p; q++) a[p * 6 + q] = a[q * 6 + p];
        Span<double> v = stackalloc double[36];
        v.Clear();
        for (var i = 0; i < 6; i++) v[i * 6 + i] = 1;
        for (var sweep = 0; sweep < 12; sweep++)
        {
            double off = 0;
            for (var p = 0; p < 6; p++) for (var q = p + 1; q < 6; q++) off += a[p * 6 + q] * a[p * 6 + q];
            if (off < 1e-24) break;
            for (var p = 0; p < 6; p++)
                for (var q = p + 1; q < 6; q++)
                {
                    var apq = a[p * 6 + q];
                    if (Math.Abs(apq) < 1e-30) continue;
                    var th = (a[q * 6 + q] - a[p * 6 + p]) / (2 * apq);
                    var t = Math.Sign(th) / (Math.Abs(th) + Math.Sqrt(th * th + 1));
                    if (th == 0) t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1); var sn = t * c;
                    for (var k = 0; k < 6; k++)
                    {
                        var akp = a[k * 6 + p]; var akq = a[k * 6 + q];
                        a[k * 6 + p] = c * akp - sn * akq; a[k * 6 + q] = sn * akp + c * akq;
                    }
                    for (var k = 0; k < 6; k++)
                    {
                        var apk = a[p * 6 + k]; var aqk = a[q * 6 + k];
                        a[p * 6 + k] = c * apk - sn * aqk; a[q * 6 + k] = sn * apk + c * aqk;
                    }
                    for (var k = 0; k < 6; k++)
                    {
                        var vkp = v[k * 6 + p]; var vkq = v[k * 6 + q];
                        v[k * 6 + p] = c * vkp - sn * vkq; v[k * 6 + q] = sn * vkp + c * vkq;
                    }
                }
        }
        double lmax = 0;
        for (var i = 0; i < 6; i++) lmax = Math.Max(lmax, a[i * 6 + i]);
        if (lmax <= 1e-18) return false;
        x.Clear();
        var degenerate = 0;
        for (var i = 0; i < 6; i++)
        {
            var li = a[i * 6 + i];
            if (li < DegeneracyRatio * lmax) { degenerate++; continue; }
            double proj = 0;
            for (var k = 0; k < 6; k++) proj += v[k * 6 + i] * b[k];
            proj /= li + Damping * lmax;
            for (var k = 0; k < 6; k++) x[k] += proj * v[k * 6 + i];
        }
        LastDegenerateAxes = degenerate;
        return true;
    }

    /// <summary>1프레임 추적(무할당) → 추적 성공 여부. depth = z-깊이(m · 0 = 무효).</summary>
    public bool Track(ReadOnlySpan<float> depth, long frame) => Track(depth, frame, default, false);

    /// <summary>지상 진실 동반 추적(시뮬레이션 평가 · 루프 폐쇄 전후 오차 기록).</summary>
    public bool Track(ReadOnlySpan<float> depth, long frame, SlamPose groundTruth) => Track(depth, frame, groundTruth, true);

    private bool Track(ReadOnlySpan<float> depth, long frame, SlamPose gt, bool hasGt)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            BuildMaps(depth, _cur);
            Interlocked.Increment(ref _frames);
            bool ok;
            if (!_init)
            {
                _init = true; ok = true;
                LastIterations = 0; LastRms = 0; LastInlierRatio = 1;
            }
            else
            {
                // 내부(원시) 오도메트리 체인: 등속 예측 → 기준 키프레임 ICP(실패 시 직전 프레임)
                var pred = _rawPrev * _vel;
                var est = pred;
                LastIterations = Icp(_cur, _refMaps, _rawRef, ref est, MaxIterations, out var rms, out var inl);
                if (!(rms < 0.05f && inl > 0.3f))
                {
                    est = pred;
                    LastIterations = Icp(_cur, _prev, _rawPrev, ref est, MaxIterations, out rms, out inl);
                    Interlocked.Increment(ref _fallbacks);
                }
                LastRms = rms; LastInlierRatio = inl;
                ok = rms < 0.05f && inl > 0.3f;
                if (!ok) { Interlocked.Increment(ref _lost); est = pred; }
                var rel = _rawPrev.Inverse() * est;
                var va = Math.Clamp(VelocityAlpha, 0.05f, 1f);
                _vel = new SlamPose(Quaternion.Normalize(Quaternion.Slerp(_vel.Q, rel.Q, va)), Vector3.Lerp(_vel.T, rel.T, va)); // 등속 모델 평활(EMA)
                _raw = est;
                // 시뮬레이션 드리프트: 출력 자세에만 프레임당 누적(내부 추적 체인과 분리 → 퇴화 구간 예측 오염 없음)
                if (InjectDriftDeg != 0 || InjectDriftMm != 0)
                    _drift = new SlamPose(Quaternion.CreateFromAxisAngle(Vector3.UnitY, InjectDriftDeg * MathF.PI / 180f), new Vector3(InjectDriftMm * 0.001f, 0, 0)) * _drift;
            }
            _rawPrev = _raw;
            _pose = _drift * _raw;
            _prevPose = _pose;
            (_cur, _prev) = (_prev, _cur);
            // 궤적
            if (_trajCount < TrajCap) { _traj[_trajCount] = _pose; _trajFrame[_trajCount] = frame; _gt[_trajCount] = gt; _hasGt[_trajCount] = hasGt; _trajCount++; }
            Descriptor(depth, _desc);
            if (LoopClosureEnabled) TryLoopClosure(frame);
            MaybeKeyframe(depth, frame);
            if (_trajCount == 1 || _refFrame < 0) SetReference(frame);
            LastTrackUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            return ok;
        }
    }

    /// <summary>추적 기준 = 현재 프레임(교체 후 _prev) 맵 복사.</summary>
    private void SetReference(long frame)
    {
        _prev.Vx.AsSpan().CopyTo(_refMaps.Vx); _prev.Vy.AsSpan().CopyTo(_refMaps.Vy); _prev.Vz.AsSpan().CopyTo(_refMaps.Vz);
        _prev.Nx.AsSpan().CopyTo(_refMaps.Nx); _prev.Ny.AsSpan().CopyTo(_refMaps.Ny); _prev.Nz.AsSpan().CopyTo(_refMaps.Nz);
        _refTraj = _trajCount - 1; _refFrame = frame; _rawRef = _raw;
    }

    private void MaybeKeyframe(ReadOnlySpan<float> depth, long frame)
    {
        if (_trajCount == 0) return;
        if (_refFrame >= 0)
        {
            var last = _traj[_refTraj];
            if ((last.T - _pose.T).Length() < KeyframeDistM && SlamPose.AngleBetweenDeg(last, _pose) < KeyframeAngleDeg) return;
        }
        SetReference(frame);
        if (_kfCount >= MaxKeyframes) return;
        var k = _kfCount++;
        depth.CopyTo(_kfDepth[k]);
        _desc.AsSpan().CopyTo(_kfDesc[k]);
        _kfFrame[k] = frame; _kfTraj[k] = _trajCount - 1;
    }

    private void TryLoopClosure(long frame)
    {
        if (frame - _lastLoopFrame < LoopMinGap / 2 || _trajCount == 0) return;
        var best = -1; var bestD = float.MaxValue;
        for (var k = 0; k < _kfCount; k++)
        {
            if (frame - _kfFrame[k] < LoopMinGap) continue;
            var d = 1f - Dot(_desc, _kfDesc[k]);
            if (d < bestD) { bestD = d; best = k; }
        }
        LastDescDistance = best >= 0 ? bestD : float.NaN;
        if (best < 0 || bestD > LoopDescThreshold) return;
        var t0 = Stopwatch.GetTimestamp();
        BuildMaps(_kfDepth[best], _ref);
        // 현재 프레임 맵 = 방금 교체된 _prev
        var kfPose = _traj[_kfTraj[best]];
        var est = kfPose; // 항등 상대 초기값
        Icp(_prev, _ref, kfPose, ref est, MaxIterations * 2, out var rms, out var inl);
        LastLoopUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        if (rms > LoopMaxRms || inl < LoopMinInliers) return;
        var last = _trajCount - 1;
        var before = _hasGt[last] ? (_traj[last].T - _gt[last].T).Length() : float.NaN;
        var delta = est * _traj[last].Inverse();
        var k0 = _kfTraj[best];
        var span = Math.Max(1, last - k0);
        for (var i = k0 + 1; i <= last; i++) _traj[i] = delta.Fraction((float)(i - k0) / span) * _traj[i];
        _pose = _traj[last];
        _drift = delta * _drift; // 출력 = drift ∘ raw 재기준(내부 체인 불변)
        _prevPose = _pose;
        var after = _hasGt[last] ? (_traj[last].T - _gt[last].T).Length() : float.NaN;
        var lc = new LoopClosure(frame, _kfFrame[best], delta.T.Length(), delta.AngleDeg(), rms, inl, before, after);
        _loops[_loopCount % _loops.Length] = lc;
        _loopCount++;
        _lastLoopFrame = frame;
        try { LoopClosed?.Invoke(lc); } catch (Exception ex) { PromptFlowStudio.Program.Log("Slam.LoopClosed", ex); }
    }

    /// <summary>현재 프레임 정점(카메라) — 뷰어/특징 시각화(교체 후 _prev = 현재).</summary>
    public int CopyCurrentPointsWorld(Span<float> x, Span<float> y, Span<float> z, int stride)
    {
        lock (_gate)
        {
            var n = 0;
            for (var i = 0; i < Width * Height && n < x.Length; i += Math.Max(1, stride))
            {
                if (_prev.Vz[i] >= 0) continue;
                var p = _pose.Apply(new Vector3(_prev.Vx[i], _prev.Vy[i], _prev.Vz[i]));
                x[n] = p.X; y[n] = p.Y; z[n] = p.Z; n++;
            }
            return n;
        }
    }
}
