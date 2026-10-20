using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.OpenUsd;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.DigitalTwinWorld;

/// <summary>노드 권한: 실물 우선(센서 → 트윈) · 가상 우선(트윈 → 액추에이터 명령) · 양방향(최신 기록 우선).</summary>
public enum TwinAuthority : byte { Physical, Virtual, Bidirectional }

/// <summary>
/// [PromptFlow Studio v16.99.0] 🌐 실시간 디지털 트윈 공간 양방향 동기화 엔진.
/// · 노드 SoA(최대 256): 실물 센서 포즈(센서 프레임 · 위치 + 회전) ↔ 트윈 포즈(USD 월드) · 스칼라 상태(온도 등).
/// · 실물 → 가상: 캘리브레이션 6DoF(센서 → 월드) 3×3 회전 + 이동을 Vector&lt;float&gt; 일괄 변환 → 트윈 갱신(가상 우선 노드 제외) → USD 프림 translate 기록.
/// · 가상 → 실물: 트윈 편집(USD/물리) → 역변환 일괄 → 액추에이터 명령 큐(실물 우선 노드 제외) · 양방향 = 타임스탬프 최신 우선.
/// · 캘리브레이션: 대응점 Horn 사원수(4×4 대칭 야코비) 추정 · 동기화 오차(mm) = |트윈 − 캘리브레이션 적용 실물| · 왕복 지연 µs 실측 · 무할당.
/// · SLAM 결선: v16.98 SLAM 자세를 센서 노드 입력으로 받음.
/// </summary>
public sealed class RealtimeDigitalTwinSpatialSyncEngine
{
    public const int MaxNodes = 256;
    private static readonly int Pad = (MaxNodes + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly string[] _name = new string[MaxNodes];
    private readonly int[] _prim = new int[MaxNodes];
    private readonly TwinAuthority[] _auth = new TwinAuthority[MaxNodes];
    // 실물(센서 프레임) · 트윈(월드) · 변환 결과(월드) · 액추에이터 명령(센서 프레임)
    private readonly float[] _px = new float[Pad], _py = new float[Pad], _pz = new float[Pad];
    private readonly float[] _tx = new float[Pad], _ty = new float[Pad], _tz = new float[Pad];
    private readonly float[] _wx = new float[Pad], _wy = new float[Pad], _wz = new float[Pad];
    private readonly float[] _cx = new float[Pad], _cy = new float[Pad], _cz = new float[Pad];
    private readonly float[] _err = new float[Pad];
    private readonly Quaternion[] _pq = new Quaternion[MaxNodes], _tq = new Quaternion[MaxNodes];
    private readonly float[] _scalarP = new float[MaxNodes], _scalarT = new float[MaxNodes];
    private readonly long[] _physTs = new long[MaxNodes], _twinTs = new long[MaxNodes];
    private readonly bool[] _twinDirty = new bool[MaxNodes], _cmdPending = new bool[MaxNodes];
    private int _count;
    private SlamPose _calib = SlamPose.Identity;
    private long _toVirtual, _toPhysical, _syncs;

    public int Count => Volatile.Read(ref _count);
    public SlamPose Calibration { get { lock (_gate) return _calib; } set { lock (_gate) _calib = value; } }
    public double LastSyncUs { get; private set; }
    public double MaxSyncUs { get; private set; }
    public float MeanErrorMm { get; private set; }
    public float MaxErrorMm { get; private set; }
    public long ToVirtual => Interlocked.Read(ref _toVirtual);
    public long ToPhysical => Interlocked.Read(ref _toPhysical);
    public long Syncs => Interlocked.Read(ref _syncs);
    public float CalibrationRmsMm { get; private set; } = float.NaN;
    public UsdLayer? Layer { get; set; }

    public string Name(int i) => _name[i];
    public TwinAuthority Authority(int i) => _auth[i];
    public void SetAuthority(int i, TwinAuthority a) => _auth[i] = a;
    public int Prim(int i) => _prim[i];
    public Vector3 Physical(int i) => new(_px[i], _py[i], _pz[i]);
    public Vector3 Twin(int i) => new(_tx[i], _ty[i], _tz[i]);
    public Quaternion TwinRotation(int i) => _tq[i];
    public Vector3 PhysicalInWorld(int i) => new(_wx[i], _wy[i], _wz[i]);
    public Vector3 Command(int i) => new(_cx[i], _cy[i], _cz[i]);
    public bool CommandPending(int i) => _cmdPending[i];
    public float ErrorMm(int i) => _err[i];
    public float ScalarPhysical(int i) => _scalarP[i];
    public float ScalarTwin(int i) => _scalarT[i];

    /// <summary>노드 등록(동일 이름 = 기존) · prim = USD 프림 인덱스(-1 = 없음).</summary>
    public int AddNode(string name, TwinAuthority auth, int prim = -1)
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++) if (_name[i] == name) return i;
            if (_count >= MaxNodes) return -1;
            var n = _count;
            _name[n] = name; _auth[n] = auth; _prim[n] = prim; _pq[n] = _tq[n] = Quaternion.Identity;
            Volatile.Write(ref _count, n + 1);
            return n;
        }
    }

    // ── 입력(핫패스 · 무할당) ──
    /// <summary>실물 센서 표본(센서 프레임).</summary>
    public void SubmitPhysical(int i, Vector3 p, Quaternion q, float scalar, long tsUs)
    {
        lock (_gate)
        {
            _px[i] = p.X; _py[i] = p.Y; _pz[i] = p.Z; _pq[i] = q; _scalarP[i] = scalar; _physTs[i] = tsUs;
        }
    }

    /// <summary>SLAM 추적 자세(v16.98) → 센서 노드.</summary>
    public void SubmitSlam(int i, SlamPose pose, long tsUs) => SubmitPhysical(i, pose.T, pose.Q, _scalarP[i], tsUs);

    /// <summary>가상 편집(USD/물리 · 월드).</summary>
    public void EditTwin(int i, Vector3 p, Quaternion q, long tsUs)
    {
        lock (_gate)
        {
            _tx[i] = p.X; _ty[i] = p.Y; _tz[i] = p.Z; _tq[i] = q; _twinTs[i] = tsUs; _twinDirty[i] = true;
        }
    }

    public void EditTwinScalar(int i, float v, long tsUs) { lock (_gate) { _scalarT[i] = v; _twinTs[i] = tsUs; _twinDirty[i] = true; } }

    /// <summary>
    /// 1회 양방향 동기화(무할당): 실물 → 월드 SIMD 변환 → 권한 판정 → 트윈 갱신 / 액추에이터 명령(역변환 SIMD) → 오차 → USD 기록.
    /// </summary>
    public void Sync()
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var n = _count;
            Transform(_calib, _px, _py, _pz, _wx, _wy, _wz);
            for (var i = 0; i < n; i++)
            {
                var a = _auth[i];
                var physNewer = _physTs[i] >= _twinTs[i];
                var toVirtual = a == TwinAuthority.Physical || (a == TwinAuthority.Bidirectional && physNewer);
                if (toVirtual && _physTs[i] != 0)
                {
                    _tx[i] = _wx[i]; _ty[i] = _wy[i]; _tz[i] = _wz[i];
                    _tq[i] = Quaternion.Normalize(_calib.Q * _pq[i]);
                    _scalarT[i] = _scalarP[i];
                    _twinDirty[i] = false;
                    _toVirtual++;
                    if (Layer is { } l && _prim[i] >= 0) l.SetTranslate(_prim[i], _tx[i], _ty[i], _tz[i]);
                }
                else if (_twinDirty[i] && a != TwinAuthority.Physical)
                {
                    _cmdPending[i] = true;
                    _twinDirty[i] = false;
                    _toPhysical++;
                }
            }
            // 가상 → 실물 역변환(일괄)
            Transform(_calib.Inverse(), _tx, _ty, _tz, _cx, _cy, _cz);
            // 오차(SIMD)
            var vt = MemoryMarshal.Cast<float, Vector<float>>(_tx.AsSpan()); var vu = MemoryMarshal.Cast<float, Vector<float>>(_ty.AsSpan()); var vv = MemoryMarshal.Cast<float, Vector<float>>(_tz.AsSpan());
            var wx = MemoryMarshal.Cast<float, Vector<float>>(_wx.AsSpan()); var wy = MemoryMarshal.Cast<float, Vector<float>>(_wy.AsSpan()); var wz = MemoryMarshal.Cast<float, Vector<float>>(_wz.AsSpan());
            var ve = MemoryMarshal.Cast<float, Vector<float>>(_err.AsSpan());
            var k = new Vector<float>(1000f);
            for (var b = 0; b < vt.Length; b++)
            {
                var dx = vt[b] - wx[b]; var dy = vu[b] - wy[b]; var dz = vv[b] - wz[b];
                ve[b] = Vector.SquareRoot(dx * dx + dy * dy + dz * dz) * k;
            }
            float sum = 0, mx = 0; var m = 0;
            for (var i = 0; i < n; i++) { if (_physTs[i] == 0) continue; sum += _err[i]; mx = Math.Max(mx, _err[i]); m++; }
            MeanErrorMm = m == 0 ? 0 : sum / m; MaxErrorMm = mx;
            _syncs++;
            LastSyncUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            if (LastSyncUs > MaxSyncUs) MaxSyncUs = LastSyncUs;
        }
    }

    /// <summary>액추에이터 명령 소비(센서 프레임 목표) → true = 대기 명령 있었음.</summary>
    public bool TryTakeCommand(int i, out Vector3 target)
    {
        lock (_gate)
        {
            target = new Vector3(_cx[i], _cy[i], _cz[i]);
            if (!_cmdPending[i]) return false;
            _cmdPending[i] = false;
            return true;
        }
    }

    /// <summary>강체 변환 SoA 일괄(Vector&lt;float&gt;).</summary>
    public static void Transform(SlamPose t, ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, Span<float> ox, Span<float> oy, Span<float> oz)
    {
        var m = Matrix4x4.CreateFromQuaternion(t.Q);
        var vx = MemoryMarshal.Cast<float, Vector<float>>(x); var vy = MemoryMarshal.Cast<float, Vector<float>>(y); var vz = MemoryMarshal.Cast<float, Vector<float>>(z);
        var rx = MemoryMarshal.Cast<float, Vector<float>>(ox); var ry = MemoryMarshal.Cast<float, Vector<float>>(oy); var rz = MemoryMarshal.Cast<float, Vector<float>>(oz);
        Vector<float> m11 = new(m.M11), m12 = new(m.M12), m13 = new(m.M13), m21 = new(m.M21), m22 = new(m.M22), m23 = new(m.M23), m31 = new(m.M31), m32 = new(m.M32), m33 = new(m.M33);
        Vector<float> tx = new(t.T.X), ty = new(t.T.Y), tz = new(t.T.Z);
        for (var k = 0; k < vx.Length; k++)
        {
            var a = vx[k]; var b = vy[k]; var c = vz[k];
            rx[k] = a * m11 + b * m21 + c * m31 + tx;
            ry[k] = a * m12 + b * m22 + c * m32 + ty;
            rz[k] = a * m13 + b * m23 + c * m33 + tz;
        }
        for (var i = vx.Length * Vector<float>.Count; i < x.Length; i++)
        {
            var p = t.Apply(new Vector3(x[i], y[i], z[i]));
            ox[i] = p.X; oy[i] = p.Y; oz[i] = p.Z;
        }
    }

    /// <summary>
    /// Horn 사원수 캘리브레이션: 센서 점 s_i ↔ 월드 점 w_i → w = R s + t 최소제곱(무할당 · 야코비 4×4) → RMS mm.
    /// </summary>
    public float Calibrate(ReadOnlySpan<Vector3> sensor, ReadOnlySpan<Vector3> world)
    {
        var n = Math.Min(sensor.Length, world.Length);
        if (n < 3) return float.NaN;
        Vector3 cs = default, cw = default;
        for (var i = 0; i < n; i++) { cs += sensor[i]; cw += world[i]; }
        cs /= n; cw /= n;
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        for (var i = 0; i < n; i++)
        {
            var a = sensor[i] - cs; var b = world[i] - cw;
            sxx += a.X * b.X; sxy += a.X * b.Y; sxz += a.X * b.Z;
            syx += a.Y * b.X; syy += a.Y * b.Y; syz += a.Y * b.Z;
            szx += a.Z * b.X; szy += a.Z * b.Y; szz += a.Z * b.Z;
        }
        Span<double> nm = stackalloc double[16]
        {
            sxx + syy + szz, syz - szy, szx - sxz, sxy - syx,
            syz - szy, sxx - syy - szz, sxy + syx, szx + sxz,
            szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy,
            sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz,
        };
        Span<double> v = stackalloc double[16];
        Jacobi4(nm, v);
        var best = 0;
        for (var i = 1; i < 4; i++) if (nm[i * 4 + i] > nm[best * 4 + best]) best = i;
        var q = Quaternion.Normalize(new Quaternion((float)v[1 * 4 + best], (float)v[2 * 4 + best], (float)v[3 * 4 + best], (float)v[0 * 4 + best]));
        var t = cw - Vector3.Transform(cs, q);
        var cal = new SlamPose(q, t);
        double se = 0;
        for (var i = 0; i < n; i++) se += (cal.Apply(sensor[i]) - world[i]).LengthSquared();
        lock (_gate) _calib = cal;
        CalibrationRmsMm = (float)(Math.Sqrt(se / n) * 1000);
        return CalibrationRmsMm;
    }

    private static void Jacobi4(Span<double> a, Span<double> v)
    {
        v.Clear();
        for (var i = 0; i < 4; i++) v[i * 4 + i] = 1;
        for (var sweep = 0; sweep < 30; sweep++)
        {
            double off = 0;
            for (var p = 0; p < 4; p++) for (var q = p + 1; q < 4; q++) off += a[p * 4 + q] * a[p * 4 + q];
            if (off < 1e-22) break;
            for (var p = 0; p < 4; p++)
                for (var q = p + 1; q < 4; q++)
                {
                    var apq = a[p * 4 + q];
                    if (Math.Abs(apq) < 1e-30) continue;
                    var th = (a[q * 4 + q] - a[p * 4 + p]) / (2 * apq);
                    var t = th == 0 ? 1 : Math.Sign(th) / (Math.Abs(th) + Math.Sqrt(th * th + 1));
                    var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                    for (var k = 0; k < 4; k++) { var akp = a[k * 4 + p]; var akq = a[k * 4 + q]; a[k * 4 + p] = c * akp - s * akq; a[k * 4 + q] = s * akp + c * akq; }
                    for (var k = 0; k < 4; k++) { var apk = a[p * 4 + k]; var aqk = a[q * 4 + k]; a[p * 4 + k] = c * apk - s * aqk; a[q * 4 + k] = s * apk + c * aqk; }
                    for (var k = 0; k < 4; k++) { var vkp = v[k * 4 + p]; var vkq = v[k * 4 + q]; v[k * 4 + p] = c * vkp - s * vkq; v[k * 4 + q] = s * vkp + c * vkq; }
                }
        }
    }
}
