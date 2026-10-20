using System.Buffers.Binary;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Twin;

public enum SensorKind : byte { Lidar = 0, Lux, Range, Imu }

/// <summary>광학 환경(태양 방향 · 직사 조도 · 앰비언트 lux · 램프 위치 · 램프 1m 조도).</summary>
public struct SimLight
{
    public Vector3 SunDir;
    public float SunLux, AmbientLux, LampLux1m;
    public Vector3 LampPos;
}

/// <summary>센서 메트릭 1건(캡처 시각 기준). Lidar: Value = 최근접 m · Aux = 평균 m · Lux: lux · Range: m · Imu: |비력| m/s².</summary>
public readonly record struct SensorSample(int Sensor, SensorKind Kind, long TimeMicros, float Value, float Aux, uint Seq);

/// <summary>루프 백엔드 결선점(32B "SNS1" 메트릭 패킷).</summary>
public interface ISensorMetricBackend
{
    void Publish(ReadOnlySpan<byte> packet);
}

/// <summary>
/// [PromptFlow Studio v16.73.0] 📡 자율 센서 피드백 &amp; 가상 시뮬레이션 루프.
///   ▸ 가상 센서(32): 2D 라이다(64빔 레이캐스트 · 부착체 제외) · 조도계(태양 직사 그림자 + 램프 역제곱·코사인·가림 + 앰비언트) · 거리계(대상 객체) · IMU(유한차분 가속 + 중력 = 비력).
///   ▸ 센서 현실성: 결정적 가우시안 잡음(Box–Muller) · 드롭아웃 · 센서별 지연선(캡처 → 지연 후 게시) — 링 버퍼 무할당.
///   ▸ 메트릭 스트림: 1024 링 · 32B 와이어 패킷(CRC32) → <see cref="ISensorMetricBackend"/>.
///   ▸ 피드백 루프(8): PID(측정값 미분 · 포화 시 적분 정지 안티와인드업 · 출력 제한) · IAE · 안착 판정(허용 오차 1초 유지). 출력은 러너가 액추에이터(램프 · 트윈 명령)에 적용.
/// </summary>
public sealed class AutonomousSensorFeedbackLoopSimulator
{
    public const int MaxSensors = 32, Beams = 64, PendingDepth = 32, StreamCapacity = 1024, MaxLoops = 8, MetricPacketSize = 32;

    private readonly object _gate = new();
    // 센서
    private readonly byte[] _kind = new byte[MaxSensors];
    private readonly string[] _name = new string[MaxSensors];
    private readonly int[] _attach = new int[MaxSensors], _target = new int[MaxSensors];
    private readonly Vector3[] _offset = new Vector3[MaxSensors], _prevPos = new Vector3[MaxSensors], _prevVel = new Vector3[MaxSensors];
    private readonly float[] _sigma = new float[MaxSensors], _dropout = new float[MaxSensors], _range = new float[MaxSensors];
    private readonly long[] _latency = new long[MaxSensors], _published = new long[MaxSensors], _dropped = new long[MaxSensors];
    private readonly byte[] _imuState = new byte[MaxSensors];
    private readonly float[] _last = new float[MaxSensors], _lastAux = new float[MaxSensors], _truth = new float[MaxSensors];
    private readonly long[] _lastT = new long[MaxSensors];
    private readonly bool[] _has = new bool[MaxSensors];
    private readonly float[] _beams = new float[MaxSensors * Beams];
    private readonly long[] _pendT = new long[MaxSensors * PendingDepth];
    private readonly float[] _pendV = new float[MaxSensors * PendingDepth], _pendA = new float[MaxSensors * PendingDepth];
    private readonly int[] _pendHead = new int[MaxSensors], _pendCount = new int[MaxSensors];
    private int _n;
    // 스트림
    private readonly SensorSample[] _stream = new SensorSample[StreamCapacity];
    private long _streamSeq, _overflow;
    // 루프
    private readonly string[] _loopName = new string[MaxLoops];
    private readonly int[] _loopSensor = new int[MaxLoops];
    private readonly float[] _sp = new float[MaxLoops], _kp = new float[MaxLoops], _ki = new float[MaxLoops], _kd = new float[MaxLoops];
    private readonly float[] _outMin = new float[MaxLoops], _outMax = new float[MaxLoops], _tol = new float[MaxLoops];
    private readonly float[] _integral = new float[MaxLoops], _prevMeas = new float[MaxLoops], _out = new float[MaxLoops], _errNow = new float[MaxLoops];
    private readonly float[] _iae = new float[MaxLoops], _settled = new float[MaxLoops];
    private readonly bool[] _loopOn = new bool[MaxLoops], _loopInit = new bool[MaxLoops];
    private int _loops;
    private ulong _rng = 0x1673_5E55_0A11UL;
    private long _steps;

    public ISensorMetricBackend? Backend { get; set; }

    public int SensorCount => _n;
    public int LoopCount => _loops;
    public long Steps => Interlocked.Read(ref _steps);
    public long StreamSeq => Interlocked.Read(ref _streamSeq);
    public long Overflow => Interlocked.Read(ref _overflow);

    public string SensorName(int i) => _name[i];
    public SensorKind KindOf(int i) => (SensorKind)_kind[i];
    public bool HasValue(int i) => _has[i];
    public float LastValue(int i) => _last[i];
    public float LastAux(int i) => _lastAux[i];
    public long LastTime(int i) => _lastT[i];
    /// <summary>최근 캡처의 무잡음 참값(검증 · UI 비교용).</summary>
    public float TruthValue(int i) => _truth[i];
    public long Published(int i) => _published[i];
    public long Dropped(int i) => _dropped[i];
    public float LatencyMs(int i) => _latency[i] / 1000f;
    public ReadOnlySpan<float> BeamsOf(int i) => _beams.AsSpan(i * Beams, Beams);
    public int AttachedTo(int i) => _attach[i];

    public string LoopName(int l) => _loopName[l];
    public int LoopSensor(int l) => _loopSensor[l];
    public float Output(int l) => _out[l];
    public float Error(int l) => _errNow[l];
    public float Iae(int l) => _iae[l];
    public bool Settled(int l) => _settled[l] >= 1f;
    public float Setpoint(int l) => _sp[l];
    public void SetSetpoint(int l, float v) { lock (_gate) _sp[l] = v; }
    public bool LoopEnabled(int l) => _loopOn[l];

    public void SetLoopEnabled(int l, bool on)
    {
        lock (_gate) { _loopOn[l] = on; if (!on) { _integral[l] = 0; _loopInit[l] = false; _settled[l] = 0; } }
    }

    // ══════════════════════ 구성 ══════════════════════

    /// <summary>센서 추가. attach ≥ 0 이면 장면 객체 + offset, 아니면 offset = 월드 위치. target = Range 대상 객체.</summary>
    public int AddSensor(string name, SensorKind kind, int attach, Vector3 offset, float noiseSigma = 0, float latencyMs = 0, float dropout = 0, int target = -1, float range = 30f)
    {
        lock (_gate)
        {
            if (_n >= MaxSensors) return -1;
            var i = _n++;
            _name[i] = name; _kind[i] = (byte)kind; _attach[i] = attach; _offset[i] = offset; _sigma[i] = MathF.Max(0, noiseSigma);
            _latency[i] = (long)(MathF.Max(0, latencyMs) * 1000); _dropout[i] = Math.Clamp(dropout, 0f, 1f); _target[i] = target; _range[i] = MathF.Max(0.1f, range);
            _pendHead[i] = 0; _pendCount[i] = 0; _has[i] = false; _imuState[i] = 0;
            return i;
        }
    }

    /// <summary>PID 루프 추가(err = setpoint − 측정 · 역작용 공정은 음의 이득).</summary>
    public int AddLoop(string name, int sensor, float setpoint, float kp, float ki, float kd, float outMin, float outMax, float tolerance)
    {
        lock (_gate)
        {
            if (_loops >= MaxLoops) return -1;
            var l = _loops++;
            _loopName[l] = name; _loopSensor[l] = sensor; _sp[l] = setpoint; _kp[l] = kp; _ki[l] = ki; _kd[l] = kd;
            _outMin[l] = outMin; _outMax[l] = outMax; _tol[l] = MathF.Abs(tolerance); _loopOn[l] = true;
            _integral[l] = 0; _out[l] = 0; _iae[l] = 0; _settled[l] = 0; _loopInit[l] = false;
            return l;
        }
    }

    // ══════════════════════ 스텝(무할당) ══════════════════════

    /// <summary>센서 측정 → 지연선 → 게시(스트림 · 백엔드) → PID 갱신. 반환 = 게시 수.</summary>
    public int Step(SyntheticScene scene, in SimLight light, float dt, long nowMicros)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Sensor.Step");
            for (var i = 0; i < _n; i++)
            {
                var pos = _attach[i] >= 0 && _attach[i] < scene.Count ? scene.Position(_attach[i]) + _offset[i] : _offset[i];
                float v, aux = 0;
                switch ((SensorKind)_kind[i])
                {
                    case SensorKind.Lidar: v = Lidar(scene, i, pos, out aux); break;
                    case SensorKind.Lux: v = Lux(scene, light, pos, _attach[i]); break;
                    case SensorKind.Range: v = _target[i] >= 0 && _target[i] < scene.Count ? Vector3.Distance(pos, scene.Position(_target[i])) : float.NaN; break;
                    default: v = Imu(i, pos, dt); break;
                }
                _truth[i] = v;
                if (float.IsNaN(v)) continue;
                if (_dropout[i] > 0 && Next01() < _dropout[i]) { _dropped[i]++; continue; }
                if (_sigma[i] > 0) v += _sigma[i] * Gauss();
                if ((SensorKind)_kind[i] is SensorKind.Lux or SensorKind.Range or SensorKind.Lidar) v = MathF.Max(0, v);
                var b = i * PendingDepth;
                if (_pendCount[i] == PendingDepth) { _pendHead[i] = (_pendHead[i] + 1) % PendingDepth; _pendCount[i]--; _overflow++; }
                var slot = b + (_pendHead[i] + _pendCount[i]) % PendingDepth;
                _pendT[slot] = nowMicros; _pendV[slot] = v; _pendA[slot] = aux;
                _pendCount[i]++;
            }
            var published = 0;
            Span<byte> pkt = stackalloc byte[MetricPacketSize];
            for (var i = 0; i < _n; i++)
            {
                var b = i * PendingDepth;
                while (_pendCount[i] > 0)
                {
                    var slot = b + _pendHead[i];
                    if (_pendT[slot] + _latency[i] > nowMicros) break;
                    var s = new SensorSample(i, (SensorKind)_kind[i], _pendT[slot], _pendV[slot], _pendA[slot], (uint)_streamSeq);
                    _stream[_streamSeq % StreamCapacity] = s;
                    _streamSeq++;
                    _last[i] = s.Value; _lastAux[i] = s.Aux; _lastT[i] = s.TimeMicros; _has[i] = true; _published[i]++;
                    _pendHead[i] = (_pendHead[i] + 1) % PendingDepth; _pendCount[i]--;
                    published++;
                    if (Backend is { } be)
                    {
                        EncodeMetric(s, pkt);
                        try { be.Publish(pkt); }
                        catch (Exception ex) { PromptFlowStudio.Program.Log("Sensor.Backend", ex); }
                    }
                }
            }
            for (var l = 0; l < _loops; l++) UpdateLoop(l, dt);
            _steps++;
            return published;
        }
    }

    private float Lidar(SyntheticScene scene, int i, Vector3 pos, out float mean)
    {
        var maxR = _range[i];
        var min = maxR; var sum = 0f;
        var beams = _beams.AsSpan(i * Beams, Beams);
        for (var k = 0; k < Beams; k++)
        {
            var a = k * (MathF.Tau / Beams);
            var d = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var r = scene.Raycast(pos, d, maxR, out var t, out _, out _, _attach[i]) ? t : maxR;
            beams[k] = r;
            sum += r;
            if (r < min) min = r;
        }
        mean = sum / Beams;
        return min;
    }

    private static float Lux(SyntheticScene scene, in SimLight light, Vector3 pos, int skip)
    {
        var lux = light.AmbientLux;
        var sun = light.SunDir;
        if (light.SunLux > 0 && sun.Y > 0 && !scene.Occluded(pos, Vector3.Normalize(sun), 1000f, skip)) lux += light.SunLux * Vector3.Normalize(sun).Y;
        if (light.LampLux1m > 0)
        {
            var toL = light.LampPos - pos;
            var d = toL.Length();
            if (d > 1e-3f)
            {
                var dir = toL / d;
                if (dir.Y > 0 && !scene.Occluded(pos, dir, d, skip)) lux += light.LampLux1m / MathF.Max(d * d, 0.01f) * dir.Y;
            }
        }
        return lux;
    }

    private float Imu(int i, Vector3 pos, float dt)
    {
        if (dt <= 0) return float.NaN;
        var st = _imuState[i];
        var vel = (pos - _prevPos[i]) / dt;
        _prevPos[i] = pos;
        if (st == 0) { _imuState[i] = 1; return float.NaN; }
        var acc = (vel - _prevVel[i]) / dt;
        _prevVel[i] = vel;
        if (st == 1) { _imuState[i] = 2; return float.NaN; }
        return (acc + new Vector3(0, 9.81f, 0)).Length();
    }

    private void UpdateLoop(int l, float dt)
    {
        var s = _loopSensor[l];
        if (!_loopOn[l] || s < 0 || s >= _n || !_has[s] || dt <= 0) return;
        var meas = _last[s];
        var err = _sp[l] - meas;
        var dMeas = _loopInit[l] ? (meas - _prevMeas[l]) / dt : 0f;
        _prevMeas[l] = meas; _loopInit[l] = true;
        var cand = _integral[l] + err * dt;
        var u = _kp[l] * err + _ki[l] * cand - _kd[l] * dMeas;
        if (u > _outMax[l]) { u = _outMax[l]; if (_ki[l] * err < 0) _integral[l] = cand; }
        else if (u < _outMin[l]) { u = _outMin[l]; if (_ki[l] * err > 0) _integral[l] = cand; }
        else _integral[l] = cand;
        _out[l] = u;
        _errNow[l] = err;
        _iae[l] += MathF.Abs(err) * dt;
        _settled[l] = MathF.Abs(err) <= _tol[l] ? _settled[l] + dt : 0;
    }

    private float Next01()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; _rng = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    private float Gauss()
    {
        var u1 = MathF.Max(Next01(), 1e-7f);
        var u2 = Next01();
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2);
    }

    // ══════════════════════ 스트림 · 와이어 ══════════════════════

    /// <summary>최근 게시 샘플(최신 우선) 복사. 반환 = 복사 수.</summary>
    public int CopyRecent(Span<SensorSample> dst)
    {
        lock (_gate)
        {
            var n = (int)Math.Min(Math.Min(_streamSeq, StreamCapacity), dst.Length);
            for (var k = 0; k < n; k++) dst[k] = _stream[(_streamSeq - 1 - k) % StreamCapacity];
            return n;
        }
    }

    public static void EncodeMetric(in SensorSample s, Span<byte> d)
    {
        if (d.Length < MetricPacketSize) throw new ArgumentException("32바이트 이상 필요", nameof(d));
        "SNS1"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], (ushort)s.Sensor);
        d[6] = (byte)s.Kind; d[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], s.TimeMicros);
        BinaryPrimitives.WriteSingleLittleEndian(d[16..], s.Value);
        BinaryPrimitives.WriteSingleLittleEndian(d[20..], s.Aux);
        BinaryPrimitives.WriteUInt32LittleEndian(d[24..], s.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(d[28..], OpenUsdSpatialAssetCompositorEngine.Crc32(d[..28]));
    }

    public static bool TryDecodeMetric(ReadOnlySpan<byte> d, out SensorSample s)
    {
        s = default;
        if (d.Length < MetricPacketSize || !d.StartsWith("SNS1"u8) || d[6] > (byte)SensorKind.Imu) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d[28..]) != OpenUsdSpatialAssetCompositorEngine.Crc32(d[..28])) return false;
        s = new SensorSample(BinaryPrimitives.ReadUInt16LittleEndian(d[4..]), (SensorKind)d[6], BinaryPrimitives.ReadInt64LittleEndian(d[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(d[16..]), BinaryPrimitives.ReadSingleLittleEndian(d[20..]), BinaryPrimitives.ReadUInt32LittleEndian(d[24..]));
        return true;
    }
}
