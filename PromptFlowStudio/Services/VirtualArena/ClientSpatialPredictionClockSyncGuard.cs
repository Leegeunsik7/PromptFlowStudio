using System.Diagnostics;

namespace PromptFlowStudio.Services.VirtualArena;

/// <summary>
/// [PromptFlow Studio v17.03.0] ⏱ 클라이언트 시공간 모션 예측 &amp; 지터 클록 동기화 가드(무할당 · 시뮬레이션 시각 결정적).
/// 클록: NTP 4-타임스탬프 핑(4 Hz) → 최근 16 샘플(4 s) 중 최소 RTT 선택(비대칭 지터 제거) + 주파수 투영 → PI 루프(위상 슬루 · 주파수 = 드리프트 추정) ·
/// 50 ms 초과 오차는 스텝 재동기 · 슬루 속도 제한으로 단조 증가 보장.
/// 모션: 20 Hz 스냅샷(서버 시각 · 위치 · 속도) → 정렬 지터 버퍼 → 렌더 시각 = 추정 서버 시각 − 적응 보간 지연(스냅샷 간격 + 3σ 지터) →
/// 큐빅 에르미트 보간 · 버퍼 고갈 시 데드 레커닝 외삽(≤ 250 ms) · 경로 불연속은 오프셋 흡수 후 지수 수렴(τ 100 ms) · 3 m 초과만 순간이동 스냅.
/// 비교 기준선 = 최신 수신 스냅샷 즉시 표시(나이브).
/// </summary>
public sealed class ClientSpatialPredictionClockSyncGuard
{
    public const int Tracks = 16, Buffer = 32, TruthHist = 64, MaxFlight = 1024, ClockWindow = 16;
    public const float SnapshotHz = 20f, PingHz = 4f, MaxExtrapolateSec = 0.25f, SnapDistance = 3f, ConvergeTau = 0.1f, MaxSpeed = 6f, Box = 40f;
    public const double StepThreshold = 0.05;

    private struct Pkt
    {
        public double Arrive, Sent, T0;
        public int Track;
        public float X, Z, Vx, Vz;
    }

    private readonly Pkt[] _flight = new Pkt[MaxFlight];
    private int _flightN;
    // ── 진실(서버) 운동 ──
    private readonly float[] _tx = new float[Tracks], _tz = new float[Tracks], _th = new float[Tracks], _ts = new float[Tracks], _tw = new float[Tracks];
    private readonly float[] _hx = new float[Tracks * TruthHist], _hz = new float[Tracks * TruthHist];
    private readonly double[] _ht = new double[TruthHist];
    private int _hHead, _hCount;
    // ── 클라이언트 버퍼 ──
    private readonly double[] _bt = new double[Tracks * Buffer];
    private readonly float[] _bx = new float[Tracks * Buffer], _bz = new float[Tracks * Buffer], _bvx = new float[Tracks * Buffer], _bvz = new float[Tracks * Buffer];
    private readonly int[] _bn = new int[Tracks];
    // ── 렌더 ──
    private readonly float[] _rawX = new float[Tracks], _rawZ = new float[Tracks], _rawVx = new float[Tracks], _rawVz = new float[Tracks];
    private readonly float[] _cx = new float[Tracks], _cz = new float[Tracks], _visX = new float[Tracks], _visZ = new float[Tracks];
    private readonly float[] _nx = new float[Tracks], _nz = new float[Tracks], _nPrevX = new float[Tracks], _nPrevZ = new float[Tracks];
    private readonly bool[] _init = new bool[Tracks];
    // ── 클록 ──
    private readonly double[] _cwOff = new double[ClockWindow], _cwRtt = new double[ClockWindow], _cwAt = new double[ClockWindow];
    private int _cwN, _cwHead;
    private double _drift = 200e-6, _rawOffset = 1.337, _corr, _freq, _slew, _lastEst = double.MinValue, _prevTransit = double.NaN;
    private bool _synced;
    private double _snapAcc, _pingAcc, _interp = 0.1;
    private ulong _rng = 0x853C49E6748FEA9BUL;
    private double _errAbsSum, _naiveErrSum;
    private long _errN;

    public ClientSpatialPredictionClockSyncGuard()
    {
        for (var i = 0; i < Tracks; i++)
        {
            _tx[i] = 5f + Next01() * (Box - 10f); _tz[i] = 5f + Next01() * (Box - 10f);
            _th[i] = Next01() * MathF.Tau; _ts[i] = 1.5f + 3f * Next01();
        }
    }

    public double Now { get; private set; }
    public long Ticks { get; private set; }
    public double TrueDriftPpm { get => _drift * 1e6; set => _drift = value * 1e-6; }
    public double DriftEstimatePpm => -_freq * 1e6;
    public double ClockErrorMs => (EstimatedServerTime - Now) * 1000.0;
    public double EstimatedServerTime => Raw(Now) + _corr;
    public bool Synced => _synced;
    public long ClockSteps { get; private set; }
    public long PingsSent { get; private set; }
    public long PingsAnswered { get; private set; }
    public long MonotonicViolations { get; private set; }
    public double RttEstimateMs { get; private set; }
    public double OffsetSampleMs { get; private set; }
    public float JitterMs { get; private set; }
    public double InterpDelayMs => _interp * 1000.0;
    public long SnapshotsSent { get; private set; }
    public long SnapshotsReceived { get; private set; }
    public long SnapshotsLost { get; private set; }
    public long LateDrops { get; private set; }
    public long Interpolated { get; private set; }
    public long Extrapolated { get; private set; }
    public long Held { get; private set; }
    public long Corrections { get; private set; }
    public long Snaps { get; private set; }
    public long GuardPops { get; private set; }
    public long NaivePops { get; private set; }
    public float MeanErrorM => _errN == 0 ? 0f : (float)(_errAbsSum / _errN);
    public float NaiveErrorM => _errN == 0 ? 0f : (float)(_naiveErrSum / _errN);
    public float ExtrapolationRatio => Interpolated + Extrapolated + Held == 0 ? 0f : (Extrapolated + Held) / (float)(Interpolated + Extrapolated + Held);
    public double LastStepUs { get; private set; }

    public float TruthX(int t) => _tx[t];
    public float TruthZ(int t) => _tz[t];
    public float VisualX(int t) => _visX[t];
    public float VisualZ(int t) => _visZ[t];
    public float NaiveX(int t) => _nx[t];
    public float NaiveZ(int t) => _nz[t];
    public float CorrectionM(int t) => MathF.Sqrt(_cx[t] * _cx[t] + _cz[t] * _cz[t]);
    public int Buffered(int t) => _bn[t];

    /// <summary>클라이언트 원시 클록(드리프트 + 오프셋).</summary>
    private double Raw(double t) => t + _drift * t + _rawOffset;

    /// <summary>클라이언트 클록 점프(절전 복귀 · NTP 스텝 모사).</summary>
    public void ClockJump(double seconds) => _rawOffset += seconds;

    public void ResetStats()
    {
        _errAbsSum = _naiveErrSum = 0; _errN = 0;
        GuardPops = NaivePops = Snaps = Corrections = 0;
        Interpolated = Extrapolated = Held = 0;
    }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    private double OneWay(float rttMs, float jitterMs)
    {
        var u1 = Math.Max(Next01(), 1e-7f); var u2 = Next01();
        var g = Math.Abs(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(Math.Tau * u2));
        return (rttMs * 0.5 + jitterMs * g) / 1000.0;
    }

    private void Enqueue(in Pkt p)
    {
        if (_flightN < MaxFlight) _flight[_flightN++] = p;
    }

    /// <summary>1틱(무할당).</summary>
    public void Step(float dt, double now, float rttMs, float jitterMs, float loss)
    {
        var t0 = Stopwatch.GetTimestamp();
        Now = now;
        Ticks++;
        // ── 진실 운동(급회전 포함) ──
        for (var i = 0; i < Tracks; i++)
        {
            _tw[i] = Math.Clamp(_tw[i] + (Next01() - 0.5f) * 2f * dt, -1.2f, 1.2f);
            if (Next01() < dt * 0.25f) _th[i] += (Next01() < 0.5f ? -1f : 1f) * (1f + 1.5f * Next01());
            if (Next01() < dt * 0.2f) _ts[i] = 1f + 4.5f * Next01();
            _th[i] += _tw[i] * dt;
            _tx[i] += MathF.Cos(_th[i]) * _ts[i] * dt; _tz[i] += MathF.Sin(_th[i]) * _ts[i] * dt;
            if (_tx[i] < 1f || _tx[i] > Box - 1f) { _th[i] = MathF.PI - _th[i]; _tx[i] = Math.Clamp(_tx[i], 1f, Box - 1f); }
            if (_tz[i] < 1f || _tz[i] > Box - 1f) { _th[i] = -_th[i]; _tz[i] = Math.Clamp(_tz[i], 1f, Box - 1f); }
            _hx[i * TruthHist + _hHead] = _tx[i]; _hz[i * TruthHist + _hHead] = _tz[i];
        }
        _ht[_hHead] = now;
        _hHead = (_hHead + 1) % TruthHist;
        if (_hCount < TruthHist) _hCount++;
        // ── 서버 송신 ──
        _snapAcc += dt;
        if (_snapAcc >= 1.0 / SnapshotHz)
        {
            _snapAcc -= 1.0 / SnapshotHz;
            for (var i = 0; i < Tracks; i++)
            {
                SnapshotsSent++;
                if (Next01() < loss) { SnapshotsLost++; continue; }
                Enqueue(new Pkt { Arrive = now + OneWay(rttMs, jitterMs), Sent = now, Track = i, X = _tx[i], Z = _tz[i], Vx = MathF.Cos(_th[i]) * _ts[i], Vz = MathF.Sin(_th[i]) * _ts[i] });
            }
        }
        _pingAcc += dt;
        if (_pingAcc >= 1.0 / PingHz)
        {
            _pingAcc -= 1.0 / PingHz;
            PingsSent++;
            if (Next01() >= loss && Next01() >= loss)
            {
                var d1 = OneWay(rttMs, jitterMs); var d2 = OneWay(rttMs, jitterMs);
                Enqueue(new Pkt { Arrive = now + d1 + d2, Sent = now + d1, T0 = Raw(now), Track = -1 });
            }
        }
        // ── 수신 ──
        for (var k = 0; k < _flightN;)
        {
            if (_flight[k].Arrive > now) { k++; continue; }
            var p = _flight[k];
            _flight[k] = _flight[--_flightN];
            if (p.Track < 0) OnPong(p.T0, p.Sent, Raw(p.Arrive));   // 소켓 수신 타임스탬프(틱 양자화 아님)
            else OnSnapshot(p);
        }
        // ── 클록 슬루 ──
        var maxSlew = 0.05 * dt;   // 5% 속도 한도 → 단조 증가
        var s = Math.Clamp(_slew, -maxSlew, maxSlew);
        _slew -= s;
        _corr += s + _freq * dt;
        var est = EstimatedServerTime;
        if (est < _lastEst) MonotonicViolations++;
        _lastEst = est;
        // ── 렌더 ──
        var target = 1.0 / SnapshotHz + 3.0 * JitterMs / 1000.0 + 0.01;
        target = Math.Clamp(target, 0.05, 0.35);
        _interp += Math.Clamp(target - _interp, -0.05 * dt, 0.2 * dt);
        if (_synced) Render(dt, est - _interp);
        LastStepUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
    }

    /// <summary>NTP: off = ((t1 − t0) + (t2 − t3)) / 2 · rtt = (t3 − t0) − (t2 − t1) (t1 = t2 서버 즉시 응답).</summary>
    private void OnPong(double t0, double ts, double t3)
    {
        PingsAnswered++;
        var off = ((ts - t0) + (ts - t3)) * 0.5;
        var rtt = t3 - t0;
        _cwOff[_cwHead] = off; _cwRtt[_cwHead] = rtt; _cwAt[_cwHead] = t3;
        _cwHead = (_cwHead + 1) % ClockWindow;
        if (_cwN < ClockWindow) _cwN++;
        var best = 0;
        for (var i = 1; i < _cwN; i++) if (_cwRtt[i] < _cwRtt[best]) best = i;
        var proj = _cwOff[best] + _freq * (t3 - _cwAt[best]);
        RttEstimateMs = _cwRtt[best] * 1000.0;
        OffsetSampleMs = off * 1000.0;
        var corrNow = _corr + _slew;
        var e = proj - corrNow;
        if (!_synced || Math.Abs(e) > StepThreshold)
        {
            _corr = proj; _slew = 0;
            if (_synced) { _cwN = 1; _cwOff[0] = off; _cwRtt[0] = rtt; _cwAt[0] = t3; _cwHead = 1; }
            _synced = true; ClockSteps++;
            _lastEst = double.MinValue;
            return;
        }
        _slew += 0.15 * e;
        _freq += 0.008 * e;
    }

    private void OnSnapshot(in Pkt p)
    {
        SnapshotsReceived++;
        // RFC 3550 도착 지터(추정 서버 시각 기준)
        var transit = (_synced ? Raw(p.Arrive) + _corr : p.Arrive) - p.Sent;
        if (!double.IsNaN(_prevTransit)) JitterMs += ((float)(Math.Abs(transit - _prevTransit) * 1000.0) - JitterMs) / 16f;
        _prevTransit = transit;
        // 나이브: 최신 시각 스냅샷 즉시 표시
        var t = p.Track; var o = t * Buffer; var n = _bn[t];
        if (n > 0 && p.Sent <= _bt[o + n - 1] - 1.0) { LateDrops++; return; }
        if (n == 0 || p.Sent > _bt[o + n - 1]) { _nx[t] = p.X; _nz[t] = p.Z; }
        if (n == Buffer)
        {
            Array.Copy(_bt, o + 1, _bt, o, Buffer - 1); Array.Copy(_bx, o + 1, _bx, o, Buffer - 1); Array.Copy(_bz, o + 1, _bz, o, Buffer - 1);
            Array.Copy(_bvx, o + 1, _bvx, o, Buffer - 1); Array.Copy(_bvz, o + 1, _bvz, o, Buffer - 1);
            n--;
        }
        var i = n;
        while (i > 0 && _bt[o + i - 1] > p.Sent)
        {
            _bt[o + i] = _bt[o + i - 1]; _bx[o + i] = _bx[o + i - 1]; _bz[o + i] = _bz[o + i - 1]; _bvx[o + i] = _bvx[o + i - 1]; _bvz[o + i] = _bvz[o + i - 1];
            i--;
        }
        if (i > 0 && _bt[o + i - 1] == p.Sent) { LateDrops++; for (var j = i; j < n; j++) { _bt[o + j] = _bt[o + j + 1]; _bx[o + j] = _bx[o + j + 1]; _bz[o + j] = _bz[o + j + 1]; _bvx[o + j] = _bvx[o + j + 1]; _bvz[o + j] = _bvz[o + j + 1]; } return; }
        _bt[o + i] = p.Sent; _bx[o + i] = p.X; _bz[o + i] = p.Z; _bvx[o + i] = p.Vx; _bvz[o + i] = p.Vz;
        _bn[t] = n + 1;
    }

    private void Render(float dt, double rt)
    {
        var decay = MathF.Exp(-dt / ConvergeTau);
        var popStep = MaxSpeed * dt * 1.6f + 0.02f;
        for (var t = 0; t < Tracks; t++)
        {
            var o = t * Buffer; var n = _bn[t];
            if (n == 0) continue;
            float x, z, vx, vz;
            if (rt <= _bt[o])
            {
                x = _bx[o]; z = _bz[o]; vx = vz = 0f; Held++;
            }
            else if (rt >= _bt[o + n - 1])
            {
                var k = o + n - 1;
                var dx = (float)(rt - _bt[k]);
                if (dx <= MaxExtrapolateSec) { x = _bx[k] + _bvx[k] * dx; z = _bz[k] + _bvz[k] * dx; vx = _bvx[k]; vz = _bvz[k]; Extrapolated++; }
                else { x = _bx[k] + _bvx[k] * MaxExtrapolateSec; z = _bz[k] + _bvz[k] * MaxExtrapolateSec; vx = vz = 0f; Held++; }
            }
            else
            {
                var i = o;
                while (i < o + n - 2 && _bt[i + 1] <= rt) i++;
                var h = (float)(_bt[i + 1] - _bt[i]);
                var u = (float)((rt - _bt[i]) / h);
                float u2 = u * u, u3 = u2 * u;
                float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
                x = h00 * _bx[i] + h10 * h * _bvx[i] + h01 * _bx[i + 1] + h11 * h * _bvx[i + 1];
                z = h00 * _bz[i] + h10 * h * _bvz[i] + h01 * _bz[i + 1] + h11 * h * _bvz[i + 1];
                float d00 = 6 * u2 - 6 * u, d10 = 3 * u2 - 4 * u + 1, d01 = -6 * u2 + 6 * u, d11 = 3 * u2 - 2 * u;
                vx = (d00 * _bx[i] + d01 * _bx[i + 1]) / h + d10 * _bvx[i] + d11 * _bvx[i + 1];
                vz = (d00 * _bz[i] + d01 * _bz[i + 1]) / h + d10 * _bvz[i] + d11 * _bvz[i + 1];
                Interpolated++;
            }
            if (!_init[t])
            {
                _init[t] = true;
                _rawX[t] = x; _rawZ[t] = z; _visX[t] = x; _visZ[t] = z; _nPrevX[t] = _nx[t]; _nPrevZ[t] = _nz[t];
            }
            // 경로 불연속 → 보정 오프셋 흡수(시각 연속) · 대거리만 스냅
            float jx = x - (_rawX[t] + _rawVx[t] * dt), jz = z - (_rawZ[t] + _rawVz[t] * dt);
            var jl = MathF.Sqrt(jx * jx + jz * jz);
            if (jl > SnapDistance) { _cx[t] = 0f; _cz[t] = 0f; Snaps++; }
            else if (jl > 0.02f) { _cx[t] -= jx; _cz[t] -= jz; Corrections++; }
            _cx[t] *= decay; _cz[t] *= decay;
            _rawX[t] = x; _rawZ[t] = z; _rawVx[t] = vx; _rawVz[t] = vz;
            float px = _visX[t], pz = _visZ[t];
            _visX[t] = x + _cx[t]; _visZ[t] = z + _cz[t];
            float sx = _visX[t] - px, sz = _visZ[t] - pz;
            if (sx * sx + sz * sz > popStep * popStep) GuardPops++;
            float nsx = _nx[t] - _nPrevX[t], nsz = _nz[t] - _nPrevZ[t];
            if (nsx * nsx + nsz * nsz > popStep * popStep) NaivePops++;
            _nPrevX[t] = _nx[t]; _nPrevZ[t] = _nz[t];
            // 오차: 가드 = 렌더 시각 진실 · 나이브 = 현재 진실
            if (TruthAt(t, rt, out var gx, out var gz))
            {
                float ex = _visX[t] - gx, ez = _visZ[t] - gz, nx = _nx[t] - _tx[t], nz = _nz[t] - _tz[t];
                _errAbsSum += MathF.Sqrt(ex * ex + ez * ez);
                _naiveErrSum += MathF.Sqrt(nx * nx + nz * nz);
                _errN++;
            }
        }
    }

    private bool TruthAt(int t, double time, out float x, out float z)
    {
        x = z = 0f;
        for (var k = 1; k < _hCount; k++)
        {
            var i1 = (_hHead - k + TruthHist) % TruthHist;
            var i0 = (_hHead - k - 1 + TruthHist) % TruthHist;
            if (_ht[i0] <= time && time <= _ht[i1])
            {
                var u = (float)((time - _ht[i0]) / Math.Max(1e-9, _ht[i1] - _ht[i0]));
                x = _hx[t * TruthHist + i0] + (_hx[t * TruthHist + i1] - _hx[t * TruthHist + i0]) * u;
                z = _hz[t * TruthHist + i0] + (_hz[t * TruthHist + i1] - _hz[t * TruthHist + i0]) * u;
                return true;
            }
        }
        return false;
    }
}
