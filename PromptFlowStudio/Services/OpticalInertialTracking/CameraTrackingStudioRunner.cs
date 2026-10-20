using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
namespace PromptFlowStudio.Services.OpticalInertialTracking;

public enum CameraRigMove : byte { Dolly, Handheld, Crane, Static }

/// <summary>
/// [PromptFlow Studio v17.45.0] 📐 AI 카메라 트래킹 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 실측 카메라 궤적(돌리 · 핸드헬드 · 크레인 · 고정) → 광학 트래커 FreeD D1 1000 Hz 패킷 인코딩(잡음 · 손실 · 가림 · 튐 · 체크섬 손상)
/// → 무할당 디코딩/프레임 평균 → IMU(바이어스 · 잡음) 예측 + 자이로/광학 EKF 갱신 → 오클루전/패킷 유실 가드 →
/// 렌더 지연 3차 테일러 선제 예측 · 에르미트 표시 재표본화 Swim 평가 순으로 처리한다.
/// </summary>
public sealed class CameraTrackingStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300;
    public const float Dt = 1f / Hz;
    public const double OptPosSigma = 8e-4, OptAngSigmaDeg = 0.01, AccSigma = 0.05, GyroSigma = 0.002;
    private static readonly Vector3 AccBias = new(0.05f, -0.03f, 0.04f);
    private readonly object _gate = new();
    private readonly float[] _resS = new float[Hist], _errS = new float[Hist], _swRS = new float[Hist], _swPS = new float[Hist], _lossS = new float[Hist], _msS = new float[Hist];
    private readonly float[] _tx = new float[Hist], _tz = new float[Hist], _ox = new float[Hist], _oz = new float[Hist];
    private readonly byte[] _pkt = new byte[RealtimeOpticalInertialCameraTrackingEngine.PacketBytes];
    private ulong _rng = 0x2545F4914F6CDD1DUL;
    private double _spare, _time, _pktAcc;
    private bool _hasSpare, _seeded;
    private int _head, _series, _occludeLeft, _glitchLeft;
    private Vector3 _hold, _holdE;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public event Action? OnChange;
    public RealtimeOpticalInertialCameraTrackingEngine Engine { get; } = new();
    public RenderLatencyParallaxPredictionSynthesizer Predictor { get; } = new();
    public TrackerOcclusionPacketLossJitterGuard Guard { get; } = new();
    public CameraRigMove Move { get; private set; } = CameraRigMove.Dolly;
    public double LossPct { get; private set; } = 2.0;
    public double RenderLatencyMs { get; private set; } = 25.0;
    public double CorruptPct { get; set; } = 0.3;
    public Vector3 TruthPos { get; private set; }
    public Vector3 TruthEuler { get; private set; }
    public Vector3 OutPos => Guard.OutPos;
    public Vector3 OutEuler => Guard.OutEuler;
    public bool Measured { get; private set; }
    public bool Accepted { get; private set; }
    public bool Occluded => _occludeLeft > 0;
    public double TrackErrMm { get; private set; }
    public double MaxTrackErrMm { get; private set; }
    public double RawErrMm { get; private set; }
    public long PacketsSent { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float ResidualSeries(int i) => _resS[Ix(i)];
    public float ErrorSeries(int i) => _errS[Ix(i)];
    public float SwimRawSeries(int i) => _swRS[Ix(i)];
    public float SwimPredSeries(int i) => _swPS[Ix(i)];
    public float LossSeries(int i) => _lossS[Ix(i)];
    public float TickMsSeries(int i) => _msS[Ix(i)];
    public Vector2 TruthTrail(int i) { var k = Ix(i); return new(_tx[k], _tz[k]); }
    public Vector2 OutTrail(int i) { var k = Ix(i); return new(_ox[k], _oz[k]); }
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    public static string MoveName(CameraRigMove m) => m switch { CameraRigMove.Dolly => "돌리 트랙", CameraRigMove.Handheld => "핸드헬드", CameraRigMove.Crane => "크레인", _ => "고정" };

    // ══════════════════════ 제어 ══════════════════════
    public void SetMove(CameraRigMove m) { lock (_gate) Move = m; LastAction = "🎥 리그 " + MoveName(m); Raise(); }
    public void SetLoss(double pct)
    {
        lock (_gate) LossPct = Math.Clamp(pct, 0.0, 60.0);
        LastAction = "📉 패킷 손실 " + LossPct.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        Raise();
    }
    public void SetRenderLatency(double ms) { lock (_gate) RenderLatencyMs = Math.Clamp(ms, 0.0, 50.0); Raise(); }
    /// <summary>0 = 자동(측정 지연).</summary>
    public void SetPredictionOffset(double ms)
    {
        lock (_gate) Predictor.PredictionMs = ms <= 0 ? 0 : Math.Clamp(ms, 1.0, RenderLatencyParallaxPredictionSynthesizer.MaxMs);
        LastAction = Predictor.PredictionMs > 0 ? "⏩ 예측 오프셋 " + Predictor.PredictionMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "⏩ 예측 오프셋 자동";
        Raise();
    }
    public void SetPrediction(bool on) { lock (_gate) Predictor.Enabled = on; LastAction = on ? "⏩ 지연 선제 예측 ON" : "⏩ 예측 OFF"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 오클루전 가드 ON" : "🛡 가드 OFF(원시 FreeD)"; Raise(); }
    public void SetImu(bool on) { lock (_gate) Engine.FuseImu = on; LastAction = on ? "🧭 IMU 융합 ON" : "🧭 IMU 융합 OFF(광학 단독)"; Raise(); }
    public void Occlude(int frames = 30) { lock (_gate) _occludeLeft = Math.Max(_occludeLeft, frames); LastAction = "🙈 마커 가림 " + frames + " 프레임"; Raise(); }
    public void Glitch(int frames = 1) { lock (_gate) _glitchLeft = Math.Max(_glitchLeft, frames); LastAction = "⚡ 마커 스왑 튐 주입"; Raise(); }
    public void ResetMetrics()
    {
        lock (_gate) { Engine.ResetMetrics(); Predictor.ResetMetrics(); Guard.ResetMetrics(); MaxTickMs = 0; MaxTrackErrMm = 0; PacketsSent = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("CameraTracking.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _time += Dt;
            Truth(_time, out var tp, out var te);
            TruthPos = tp; TruthEuler = te;
            if (!_seeded) { Engine.Seed(tp, te); _seeded = true; }
            Truth(_time - 1e-2, out var pm, out var em); Truth(_time + 1e-2, out var pp, out var ep);
            var acc = (pp - 2f * tp + pm) * 1e4f + AccBias + G3(AccSigma);
            var gyro = (ep - em) * 50f + G3(GyroSigma);

            Emit(tp, te);
            Measured = Engine.TryTakeFrameMeasurement(Dt, out var mp, out var me);
            Engine.Predict(Dt, acc);
            Engine.UpdateGyro(gyro);
            Accepted = Measured && Engine.UpdateOptical(mp, me);
            if (Measured) { RawErrMm = (mp - tp).Length() * 1000.0; _hold = mp; _holdE = me; }
            if (Guard.Enabled) Guard.Process(Dt, Engine.Position, Engine.Euler, Accepted, Measured && !Accepted);
            else Guard.Process(Dt, _hold, _holdE, Measured, false);

            Predictor.Push(_time, Guard.OutPos, Guard.OutVel, Guard.OutEuler, Guard.Enabled ? Engine.AngularRate : Vector3.Zero);
            Predictor.ReportLatency(RenderLatencyMs + Gauss() * 0.8);
            Predictor.Issue(out _, out _);
            Predictor.Evaluate(_time, RenderLatencyMs * 1e-3, tp, te);

            TrackErrMm = (Guard.OutPos - tp).Length() * 1000.0;
            if (_tick > 30 && TrackErrMm > MaxTrackErrMm) MaxTrackErrMm = TrackErrMm;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _resS[_head] = (float)Engine.ResidualMm; _errS[_head] = (float)TrackErrMm; _swRS[_head] = (float)Predictor.SwimRawPx; _swPS[_head] = (float)Predictor.SwimPredPx;
            _lossS[_head] = (float)Engine.LossRate; _msS[_head] = ms;
            _tx[_head] = tp.X; _tz[_head] = tp.Z; _ox[_head] = Guard.OutPos.X; _oz[_head] = Guard.OutPos.Z;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    /// <summary>광학 트래커 FreeD 1000 Hz 송신 시뮬(가림 · 손실 · 튐 · 체크섬 손상) → 엔진 수신.</summary>
    private void Emit(Vector3 tp, Vector3 te)
    {
        _pktAcc += RealtimeOpticalInertialCameraTrackingEngine.NominalHz * (double)Dt;
        var n = (int)_pktAcc;
        _pktAcc -= n;
        var occluded = _occludeLeft > 0;
        if (occluded) _occludeLeft--;
        var glitch = _glitchLeft > 0;
        if (glitch) _glitchLeft--;
        if (occluded) return;
        const double r2d = 180.0 / Math.PI;
        var s = new FreeDSample { CameraId = 1, Zoom = 0x080000, Focus = 0x040000 };
        Span<byte> p = _pkt;
        for (var i = 0; i < n; i++)
        {
            PacketsSent++;
            if (Uniform() * 100.0 < LossPct) continue;
            s.X = tp.X + Gauss() * OptPosSigma + (glitch ? 0.35 : 0.0); s.Y = tp.Y + Gauss() * OptPosSigma; s.Z = tp.Z + Gauss() * OptPosSigma;
            s.PanDeg = RealtimeOpticalInertialCameraTrackingEngine.WrapDeg(te.X * r2d + Gauss() * OptAngSigmaDeg + (glitch ? 12.0 : 0.0));
            s.TiltDeg = te.Y * r2d + Gauss() * OptAngSigmaDeg; s.RollDeg = te.Z * r2d + Gauss() * OptAngSigmaDeg;
            RealtimeOpticalInertialCameraTrackingEngine.WriteFreeD(p, in s);
            if (Uniform() * 100.0 < CorruptPct) p[3 + (int)(Uniform() * 20.0)] ^= 0x5A;
            Engine.IngestPacket(p);
        }
    }

    /// <summary>실측 카메라 궤적 — 위치(m) · 오일러(팬/틸트/롤 rad).</summary>
    private void Truth(double t, out Vector3 pos, out Vector3 euler)
    {
        switch (Move)
        {
            case CameraRigMove.Dolly:
                pos = new Vector3((float)(1.5 * Math.Sin(0.5 * t)), 1.6f, (float)(2.0 + 0.6 * Math.Cos(0.35 * t)));
                euler = new Vector3((float)(0.5 * Math.Sin(0.5 * t)), (float)(-0.05 + 0.03 * Math.Sin(0.8 * t)), (float)(0.01 * Math.Sin(1.1 * t)));
                break;
            case CameraRigMove.Handheld:
                pos = new Vector3((float)(0.3 * Math.Sin(0.4 * t) + 0.01 * Math.Sin(7.3 * t) + 0.006 * Math.Sin(11.1 * t)), (float)(1.55 + 0.008 * Math.Sin(5.7 * t)), (float)(1.5 + 0.2 * Math.Sin(0.3 * t)));
                euler = new Vector3((float)(0.2 * Math.Sin(0.4 * t) + 0.01 * Math.Sin(6.1 * t)), (float)(0.01 * Math.Sin(4.3 * t)), (float)(0.02 * Math.Sin(3.7 * t)));
                break;
            case CameraRigMove.Crane:
                pos = new Vector3((float)(0.8 * Math.Sin(0.2 * t)), (float)(1.2 + 1.4 * (0.5 + 0.5 * Math.Sin(0.45 * t))), 2.5f);
                euler = new Vector3((float)(-0.2 + 0.3 * Math.Sin(0.2 * t)), (float)(-0.25 * (0.5 + 0.5 * Math.Sin(0.45 * t))), 0f);
                break;
            default:
                pos = new Vector3(0f, 1.7f, 2f); euler = Vector3.Zero;
                break;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.CameraTracking" };
            _loop.Start();
        }
        LastAction = "▶ 카메라 트래킹 60 Hz · " + MoveName(Move);
        Raise();
    }
    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }
    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("CameraTracking.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("CameraTracking.Runner.OnChange", ex); }
    }

    private Vector3 G3(double sigma) => new((float)(Gauss() * sigma), (float)(Gauss() * sigma), (float)(Gauss() * sigma));

    private double Gauss()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        double u, v, s;
        do { u = 2.0 * Uniform() - 1.0; v = 2.0 * Uniform() - 1.0; s = u * u + v * v; } while (s >= 1.0 || s == 0.0);
        var m = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spare = v * m; _hasSpare = true;
        return u * m;
    }

    private double Uniform()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 11) * (1.0 / 9007199254740992.0);
    }

    public void Dispose() => Stop();
}
