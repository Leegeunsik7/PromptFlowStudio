using System.Buffers.Binary;
using System.Numerics;
namespace PromptFlowStudio.Services.OpticalInertialTracking;

/// <summary>FreeD D1 포즈 샘플(각도 °, 위치 m, 줌/포커스 원시 24-bit).</summary>
public struct FreeDSample
{
    public byte CameraId;
    public double PanDeg, TiltDeg, RollDeg, X, Y, Z;
    public int Zoom, Focus;
}

/// <summary>
/// [PromptFlow Studio v17.45.0] 📐 실시간 FreeD UDP 디코더 &amp; 광학-관성 센서 융합 엔진(<see cref="IngestPacket"/> · <see cref="Predict"/> · 갱신 무할당).
/// <b>FreeD D1</b>(29 바이트: 0xD1 · ID · Pan/Tilt/Roll 24-bit 부호(°×32768) · X/Y/Z 24-bit 부호(mm×64) · Zoom/Focus 24-bit · 스페어 2 · 체크섬
/// (0x40 − Σ) mod 256)을 스팬에서 직접 디코딩하고 프레임 단위로 평균해 광학 측정으로 만든다. <b>EKF</b>: 12 차원 상태
/// [위치 3 · 속도 3 · 오일러(팬/틸트/롤) 3 · 각속도 3] — IMU 가속도를 제어 입력으로 예측(P ← F P Fᵀ + Q, F = I + dt·S 희소 행 연산),
/// IMU 자이로를 각속도 측정, FreeD 광학 포즈를 위치/자세 측정으로 순차 스칼라 갱신(P ← P − K Pⱼ 랭크-1, Vector&lt;double&gt; 행 커널 · 스칼라 비트 동일)한다.
/// 정규화 혁신 제곱(NIS) χ² 게이트로 튐 패킷을 기각하고(8 프레임 연속 기각 시 측정 재획득) 잔차(mm)를 집계한다.
/// </summary>
public sealed class RealtimeOpticalInertialCameraTrackingEngine
{
    public const int N = 12, PacketBytes = 29, NominalHz = 1000, ReacquireFrames = 8;
    public const byte D1 = 0xD1;
    public const double AngScale = 32768.0, PosScale = 64_000.0, NisGate = 25.0;
    public const double OptPosVar = 1e-6, OptAngVar = 1.2e-7, GyroVar = 4e-6, AccVar = 0.04, AngAccVar = 0.4, EwmaA = 0.1;
    private readonly double[] _x = new double[N], _p = new double[N * N], _k = new double[N], _row = new double[N];
    private int _frameCount, _rejectStreak;
    private double _sx, _sy, _sz, _sp, _st, _sr;

    public RealtimeOpticalInertialCameraTrackingEngine() => Reset();

    public bool UseSimd { get; set; } = true;
    public bool FuseImu { get; set; } = true;
    public FreeDSample LastSample;
    public long PacketsOk { get; private set; }
    public long ChecksumErrors { get; private set; }
    public long Malformed { get; private set; }
    public long Updates { get; private set; }
    public long Rejected { get; private set; }
    public long Reacquisitions { get; private set; }
    public double PacketRateHz { get; private set; }
    public double LossRate { get; private set; }
    public double ResidualMm { get; private set; }
    public double ResidualDeg { get; private set; }
    public double Nis { get; private set; }
    public double LastPredictUs { get; private set; }
    public ReadOnlySpan<double> State => _x;
    public ReadOnlySpan<double> Covariance => _p;
    public Vector3 Position => new((float)_x[0], (float)_x[1], (float)_x[2]);
    public Vector3 Velocity => new((float)_x[3], (float)_x[4], (float)_x[5]);
    /// <summary>(팬, 틸트, 롤) rad.</summary>
    public Vector3 Euler => new((float)_x[6], (float)_x[7], (float)_x[8]);
    public Vector3 AngularRate => new((float)_x[9], (float)_x[10], (float)_x[11]);
    public double PosSigmaMm => Math.Sqrt(Math.Max(_p[0] + _p[N + 1] + _p[2 * N + 2], 0.0)) * 1000.0;

    public void Reset()
    {
        Array.Clear(_x);
        Array.Clear(_p);
        for (var i = 0; i < N; i++) _p[i * N + i] = i < 3 ? 1.0 : i < 6 ? 0.25 : i < 9 ? 0.1 : 0.1;
        _frameCount = 0;
    }

    public void ResetMetrics() { PacketsOk = 0; ChecksumErrors = 0; Malformed = 0; Updates = 0; Rejected = 0; Reacquisitions = 0; }

    public void Seed(Vector3 pos, Vector3 euler)
    {
        Reset();
        _x[0] = pos.X; _x[1] = pos.Y; _x[2] = pos.Z; _x[6] = euler.X; _x[7] = euler.Y; _x[8] = euler.Z;
        for (var i = 0; i < 3; i++) { _p[i * N + i] = 1e-4; _p[(6 + i) * N + 6 + i] = 1e-4; }
    }

    // ══════════════════════ FreeD D1(무할당) ══════════════════════
    public static byte Checksum(ReadOnlySpan<byte> p)
    {
        var s = 0x40;
        for (var i = 0; i < PacketBytes - 1; i++) s -= p[i];
        return (byte)s;
    }

    private static int ReadS24(ReadOnlySpan<byte> p, int o)
    {
        var v = (p[o] << 16) | (p[o + 1] << 8) | p[o + 2];
        return (v << 8) >> 8;
    }

    private static void WriteS24(Span<byte> p, int o, long v)
    {
        var c = (int)Math.Clamp(v, -8_388_608L, 8_388_607L);
        p[o] = (byte)(c >> 16); p[o + 1] = (byte)(c >> 8); p[o + 2] = (byte)c;
    }

    public static bool TryParseFreeD(ReadOnlySpan<byte> p, out FreeDSample s)
    {
        s = default;
        if (p.Length < PacketBytes || p[0] != D1 || Checksum(p) != p[PacketBytes - 1]) return false;
        s.CameraId = p[1];
        s.PanDeg = ReadS24(p, 2) / AngScale; s.TiltDeg = ReadS24(p, 5) / AngScale; s.RollDeg = ReadS24(p, 8) / AngScale;
        s.X = ReadS24(p, 11) / PosScale; s.Y = ReadS24(p, 14) / PosScale; s.Z = ReadS24(p, 17) / PosScale;
        s.Zoom = (p[20] << 16) | (p[21] << 8) | p[22];
        s.Focus = (p[23] << 16) | (p[24] << 8) | p[25];
        return true;
    }

    public static void WriteFreeD(Span<byte> p, in FreeDSample s)
    {
        p[0] = D1; p[1] = s.CameraId;
        WriteS24(p, 2, (long)Math.Round(s.PanDeg * AngScale)); WriteS24(p, 5, (long)Math.Round(s.TiltDeg * AngScale)); WriteS24(p, 8, (long)Math.Round(s.RollDeg * AngScale));
        WriteS24(p, 11, (long)Math.Round(s.X * PosScale)); WriteS24(p, 14, (long)Math.Round(s.Y * PosScale)); WriteS24(p, 17, (long)Math.Round(s.Z * PosScale));
        p[20] = (byte)(s.Zoom >> 16); p[21] = (byte)(s.Zoom >> 8); p[22] = (byte)s.Zoom;
        p[23] = (byte)(s.Focus >> 16); p[24] = (byte)(s.Focus >> 8); p[25] = (byte)s.Focus;
        BinaryPrimitives.WriteUInt16BigEndian(p[26..], 0);
        p[PacketBytes - 1] = Checksum(p);
    }

    /// <summary>UDP 데이터그램 1개 디코딩 → 프레임 평균 누적(체크섬/길이 오류 집계).</summary>
    public bool IngestPacket(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < PacketBytes || datagram[0] != D1) { Malformed++; return false; }
        if (!TryParseFreeD(datagram, out var s)) { ChecksumErrors++; return false; }
        LastSample = s;
        PacketsOk++;
        _sx += s.X; _sy += s.Y; _sz += s.Z;
        // 팬 랩(±180°) 평균 — 첫 샘플 기준 언랩
        var pan = s.PanDeg;
        if (_frameCount > 0) { var ref0 = _sp / _frameCount; pan = ref0 + WrapDeg(pan - ref0); }
        _sp += pan; _st += s.TiltDeg; _sr += s.RollDeg;
        _frameCount++;
        return true;
    }

    /// <summary>프레임 구간(dt) 수신 집계 — 패킷 레이트 · 손실률 EWMA. 수신 0 이면 false.</summary>
    public bool TryTakeFrameMeasurement(double dt, out Vector3 pos, out Vector3 eulerRad)
    {
        var n = _frameCount;
        var rate = dt > 0 ? n / dt : 0.0;
        PacketRateHz += EwmaA * (rate - PacketRateHz);
        var loss = Math.Clamp(1.0 - rate / NominalHz, 0.0, 1.0);
        LossRate += EwmaA * (loss - LossRate);
        if (n == 0) { pos = default; eulerRad = default; return false; }
        var inv = 1.0 / n;
        pos = new Vector3((float)(_sx * inv), (float)(_sy * inv), (float)(_sz * inv));
        const double d2r = Math.PI / 180.0;
        eulerRad = new Vector3((float)(WrapDeg(_sp * inv) * d2r), (float)(_st * inv * d2r), (float)(_sr * inv * d2r));
        _sx = _sy = _sz = _sp = _st = _sr = 0; _frameCount = 0;
        return true;
    }

    public static double WrapDeg(double d) => d - 360.0 * Math.Round(d / 360.0);
    public static double WrapRad(double r) => r - 2.0 * Math.PI * Math.Round(r / (2.0 * Math.PI));

    // ══════════════════════ EKF(무할당) ══════════════════════
    /// <summary>예측: IMU 가속도(월드 · 중력 보상) 제어 입력 · F = I + dt·S(p←v, θ←ω).</summary>
    public void Predict(double dt, Vector3 accel)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var a = FuseImu ? accel : Vector3.Zero;
        double ax = a.X, ay = a.Y, az = a.Z;
        _x[0] += _x[3] * dt + 0.5 * ax * dt * dt; _x[1] += _x[4] * dt + 0.5 * ay * dt * dt; _x[2] += _x[5] * dt + 0.5 * az * dt * dt;
        _x[3] += ax * dt; _x[4] += ay * dt; _x[5] += az * dt;
        _x[6] = WrapRad(_x[6] + _x[9] * dt); _x[7] += _x[10] * dt; _x[8] += _x[11] * dt;
        // F P : 행 i(0..2, 6..8) += dt · 행 i+3
        var ps = _p.AsSpan();
        for (var i = 0; i < 3; i++) { Axpy(ps.Slice(i * N, N), ps.Slice((i + 3) * N, N), dt); Axpy(ps.Slice((6 + i) * N, N), ps.Slice((9 + i) * N, N), dt); }
        // (F P) Fᵀ : 열 j(0..2, 6..8) += dt · 열 j+3
        for (var r = 0; r < N; r++)
        {
            var o = r * N;
            for (var j = 0; j < 3; j++) { _p[o + j] += dt * _p[o + j + 3]; _p[o + 6 + j] += dt * _p[o + 9 + j]; }
        }
        // Q: 백색 가속/각가속 이산화(위치 dt⁴/4 · 교차 dt³/2 · 속도 dt²)
        var qa = (FuseImu ? AccVar : 4.0 * AccVar) * dt * dt;
        for (var i = 0; i < 3; i++)
        {
            AddQ(i, i + 3, qa, dt);
            AddQ(6 + i, 9 + i, AngAccVar * dt * dt, dt);
        }
        LastPredictUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void AddQ(int p, int v, double q, double dt)
    {
        _p[p * N + p] += 0.25 * q * dt * dt; _p[p * N + v] += 0.5 * q * dt; _p[v * N + p] += 0.5 * q * dt; _p[v * N + v] += q;
    }

    public void UpdateGyro(Vector3 gyroRad)
    {
        if (!FuseImu) return;
        Scalar(9, gyroRad.X, GyroVar, false); Scalar(10, gyroRad.Y, GyroVar, false); Scalar(11, gyroRad.Z, GyroVar, false);
    }

    /// <summary>광학(FreeD) 포즈 측정 — NIS(6 성분 합) χ² 게이트 통과 시 순차 갱신. 기각 시 false.</summary>
    public bool UpdateOptical(Vector3 pos, Vector3 eulerRad)
    {
        double nis = 0;
        for (var j = 0; j < 6; j++)
        {
            var s = j < 3 ? j : j + 3;
            var inn = Innov(s, j < 3 ? Comp(pos, j) : Comp(eulerRad, j - 3));
            nis += inn * inn / (_p[s * N + s] + (j < 3 ? OptPosVar : OptAngVar));
        }
        Nis = nis;
        double ex = pos.X - _x[0], ey = pos.Y - _x[1], ez = pos.Z - _x[2];
        ResidualMm = Math.Sqrt(ex * ex + ey * ey + ez * ez) * 1000.0;
        double ep = WrapRad(eulerRad.X - _x[6]), et = eulerRad.Y - _x[7], er = eulerRad.Z - _x[8];
        ResidualDeg = Math.Sqrt(ep * ep + et * et + er * er) * (180.0 / Math.PI);
        if (nis > NisGate * 6.0)
        {
            Rejected++;
            if (++_rejectStreak < ReacquireFrames) return false;
            // 연속 기각 = 실제 리그 이동(리셋/텔레포트) — 측정으로 재획득
            Seed(pos, eulerRad);
            Reacquisitions++;
            _rejectStreak = 0;
            return true;
        }
        _rejectStreak = 0;
        for (var j = 0; j < 3; j++) Scalar(j, Comp(pos, j), OptPosVar, false);
        Scalar(6, eulerRad.X, OptAngVar, true); Scalar(7, eulerRad.Y, OptAngVar, false); Scalar(8, eulerRad.Z, OptAngVar, false);
        _x[6] = WrapRad(_x[6]);
        Updates++;
        return true;
    }

    private static double Comp(Vector3 v, int j) => j == 0 ? v.X : j == 1 ? v.Y : v.Z;
    private double Innov(int s, double z) => s == 6 ? WrapRad(z - _x[s]) : z - _x[s];

    /// <summary>H = eⱼ 스칼라 칼만 갱신: K = P[:, j]/(Pⱼⱼ + r) · x += K·ν · P −= K Pⱼ.</summary>
    private void Scalar(int j, double z, double r, bool wrap)
    {
        var sInv = 1.0 / (_p[j * N + j] + r);
        var nu = wrap ? WrapRad(z - _x[j]) : z - _x[j];
        for (var i = 0; i < N; i++) { _k[i] = _p[i * N + j] * sInv; _row[i] = _p[j * N + i]; }
        var ps = _p.AsSpan();
        ReadOnlySpan<double> row = _row;
        for (var i = 0; i < N; i++)
        {
            _x[i] += _k[i] * nu;
            Axpy(ps.Slice(i * N, N), row, -_k[i]);
        }
        // 대칭 유지
        for (var i = 0; i < N; i++)
            for (var c = i + 1; c < N; c++) { var m = 0.5 * (_p[i * N + c] + _p[c * N + i]); _p[i * N + c] = m; _p[c * N + i] = m; }
    }

    /// <summary>dst += a · src (Vector&lt;double&gt; — 곱 후 합, FMA 미사용으로 스칼라와 비트 동일).</summary>
    private void Axpy(Span<double> dst, ReadOnlySpan<double> src, double a)
    {
        var i = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var w = Vector<double>.Count;
            var va = new Vector<double>(a);
            for (; i <= dst.Length - w; i += w)
                (new Vector<double>(dst.Slice(i, w)) + va * new Vector<double>(src.Slice(i, w))).CopyTo(dst.Slice(i, w));
        }
        for (; i < dst.Length; i++) dst[i] += a * src[i];
    }
}
