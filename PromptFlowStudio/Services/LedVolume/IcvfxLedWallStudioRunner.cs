using System.Numerics;
using PromptFlowStudio.Services.ArGraphics;

namespace PromptFlowStudio.Services.LedVolume;

/// <summary>
/// [PromptFlow Studio v17.09.0] 🎬 인카메라 VFX LED 월 &amp; 젠록 스튜디오 러너(23.976 fps · 24 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 카메라 크레인(달리/팬/틸트/줌) 실측 자세 → FreeD D1(양자화) → 오프액시스 엔진(예측 · 왜곡 역보정 프러스텀 · Kooima · 마스크) →
/// LED 파이프라인 지연(표시 상태 = L 프레임 전 계산) → 월 콘텐츠(내부/외부) → 실측 카메라 피드 · 1:1 모아레 루페 → PTP 젠록 가드 → 시차 오차(UHD px).
/// </summary>
public sealed class IcvfxLedWallStudioRunner : IDisposable
{
    public const int Hz = 24, SeriesLen = 240, Ring = 64;

    private readonly object _gate = new();
    private readonly float[] _par = new float[SeriesLen], _moire = new float[SeriesLen], _jit = new float[SeriesLen], _cov = new float[SeriesLen], _ms = new float[SeriesLen];
    private readonly FreeDPose[] _truth = new FreeDPose[Ring];
    private readonly FrustumState[] _states = new FrustumState[Ring];
    private readonly byte[] _pkt = new byte[FreeD.PacketBytes];
    private readonly byte[] _bmp = new byte[54 + LedVolumeTopology.MaxPanels * LedVolumeTopology.ColsPerPanel * LedVolumeTopology.Rows * 3];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick, _frame;
    private float _time;
    private FrustumState _shown;
    private ulong _rng = 0xBF58_476D_1CE4_E5B9UL;

    public IcvfxLedWallStudioRunner(IServiceProvider sp)
        : this((InnerFrustumOffAxisProjectionEngine?)sp.GetService(typeof(InnerFrustumOffAxisProjectionEngine)) ?? new InnerFrustumOffAxisProjectionEngine(),
               (LedWallMoireSuppressionSynthesizer?)sp.GetService(typeof(LedWallMoireSuppressionSynthesizer)) ?? new LedWallMoireSuppressionSynthesizer(),
               (PtpGenlockPhaseSyncTearingGuard?)sp.GetService(typeof(PtpGenlockPhaseSyncTearingGuard)) ?? new PtpGenlockPhaseSyncTearingGuard())
    {
    }

    public IcvfxLedWallStudioRunner(InnerFrustumOffAxisProjectionEngine engine, LedWallMoireSuppressionSynthesizer synth, PtpGenlockPhaseSyncTearingGuard genlock)
    {
        Engine = engine; Synth = synth; Genlock = genlock;
        Truth = new LensCamera(InnerFrustumOffAxisProjectionEngine.FeedW, InnerFrustumOffAxisProjectionEngine.FeedH);
        Synth.BuildEnvironment();
        LastAction = "📋 대기 · LED 볼륨 " + Topology.Panels + "패널 · " + Topology.LedColumns + "×" + Topology.LedRows + " LED · 피치 " + Topology.PitchMm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " mm";
    }

    public event Action? OnChange;

    public InnerFrustumOffAxisProjectionEngine Engine { get; }
    public LedWallMoireSuppressionSynthesizer Synth { get; }
    public PtpGenlockPhaseSyncTearingGuard Genlock { get; }
    public LedVolumeTopology Topology => Engine.Topology;
    /// <summary>실측 카메라(피드 렌더 · 시차 기준).</summary>
    public LensCamera Truth { get; }
    public ref readonly FrustumState Shown => ref _shown;
    public bool Motion { get; set; } = true;
    /// <summary>FreeD 엔코더 각도 지터(°).</summary>
    public float JitterDeg { get; set; } = 0.001f;
    public float MotionSpeed { get; set; } = 1f;
    public bool AutoZoom { get; set; } = true;
    public float ZoomFov { get; set; } = 40f;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public long Frame => _frame;
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    /// <summary>가상 랜드마크 시차 오차(UHD px · RMS/최대).</summary>
    public float ParallaxPx { get; private set; }
    public float ParallaxMaxPx { get; private set; }
    public int ParallaxPoints { get; private set; }
    public float CameraSpeed { get; private set; }
    public int SeriesCount => _seriesCount;
    public float ParallaxSeries(int i) => _par[Idx(i)];
    public float MoireSeries(int i) => _moire[Idx(i)];
    public float JitterSeries(int i) => _jit[Idx(i)];
    public float CoverageSeries(int i) => _cov[Idx(i)];
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    private float Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 40) / 16777216f * 2f - 1f; }

    // ── 운영자 조작 ───────────────────────────────────────

    public void SetTopology(float radius, float arc, float height, float panelW, float pitch)
    {
        lock (_gate) Topology.Set(radius, arc, height, panelW, pitch);
        LastAction = "🧱 토폴로지 · R " + F1(Topology.RadiusM) + " m · " + F1(Topology.ArcDeg) + "° · " + Topology.Panels + "패널 · 피치 " + F1(Topology.PitchMm) + " mm";
        Raise();
    }

    public void InjectRenderSpike()
    {
        lock (_gate) Genlock.InjectRenderLateNs = PtpGenlockPhaseSyncTearingGuard.FramePeriodNs * 0.25;
        LastAction = "🐢 렌더 지연 스파이크 주입(+10 ms)";
        Raise();
    }

    public void ToggleGrandmaster()
    {
        lock (_gate) Genlock.GrandmasterOnline = !Genlock.GrandmasterOnline;
        LastAction = Genlock.GrandmasterOnline ? "🛰 PTP 그랜드마스터 복구 — 재획득" : "⚠ PTP 그랜드마스터 소실 — 홀드오버";
        Raise();
    }

    public void SetGenlock(bool on) { lock (_gate) Genlock.GenlockEnabled = on; LastAction = on ? "🔒 젠록 ON (SMPTE 2059)" : "🔓 젠록 OFF — 프리런 24.000 Hz"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) { Genlock.GuardEnabled = on; Genlock.ResetCounters(); } LastAction = on ? "🛡 티어링 가드 ON" : "⬜ 티어링 가드 OFF"; Raise(); }

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            var frame = ++_frame;
            if (Motion) _time += dt * MotionSpeed;
            var t = _time;
            var fov = AutoZoom ? 38f + 8f * MathF.Sin(0.13f * t) : ZoomFov;
            var truth = new FreeDPose
            {
                CameraId = 1, Pan = 10f * MathF.Sin(0.27f * t), Tilt = 3f + 2.5f * MathF.Sin(0.4f * t), Roll = 0.3f * MathF.Sin(0.31f * t),
                X = 1.4f * MathF.Sin(0.35f * t), Y = -1.2f + 0.8f * MathF.Sin(0.21f * t), Z = 1.6f + 0.15f * MathF.Sin(0.5f * t),
                Zoom = InnerFrustumOffAxisProjectionEngine.ZoomForFov(fov), Focus = (int)(Synth.FocusM * 1000f),
            };
            var prev = _truth[(frame - 1) % Ring];
            CameraSpeed = frame > 1 ? MathF.Sqrt((truth.X - prev.X) * (truth.X - prev.X) + (truth.Y - prev.Y) * (truth.Y - prev.Y) + (truth.Z - prev.Z) * (truth.Z - prev.Z)) * Hz : 0f;
            _truth[frame % Ring] = truth;
            var enc = truth;
            enc.Pan += Noise() * JitterDeg; enc.Tilt += Noise() * JitterDeg;
            FreeD.Encode(enc, _pkt);
            Engine.Ingest(_pkt, frame);
            Engine.Update();
            _states[frame % Ring] = Engine.State;
            var lf = frame - Engine.LatencyFrames;
            _shown = lf >= 1 ? _states[lf % Ring] : Engine.State;
            Engine.Mask(_shown);
            Synth.RenderWall(Topology, _shown, Engine.Weights);
            var tf = InnerFrustumOffAxisProjectionEngine.FovForZoom(truth.Zoom);
            Truth.Set(truth.Pan, truth.Tilt, truth.Roll, new Vector3(truth.X, truth.Z, truth.Y), tf, InnerFrustumOffAxisProjectionEngine.K1ForFov(tf), InnerFrustumOffAxisProjectionEngine.K2ForFov(tf));
            Synth.RenderFeed(Topology, Truth, _shown);
            Synth.RenderLoupe(Topology, Truth);
            Genlock.Step();
            Parallax();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _par[_seriesHead] = ParallaxPx; _moire[_seriesHead] = Synth.MoireIndex; _jit[_seriesHead] = (float)Genlock.JitterRmsNs; _cov[_seriesHead] = Synth.Coverage; _ms[_seriesHead] = ms;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>시차: 랜드마크 P 를 표시 시점 e 에서 월에 투영한 점 W' 를 실측 카메라가 보는 위치 vs P 의 실제 위치.</summary>
    private void Parallax()
    {
        double se = 0; float mx = 0; var n = 0;
        var scale = (float)LedWallMoireSuppressionSynthesizer.SensorW / Truth.Width;
        for (var i = 0; i < VirtualWorld.Landmarks; i++)
        {
            var p = VirtualWorld.Landmark(i);
            if (!Truth.Project(p, out var u, out var v) || u < 0 || v < 0 || u > Truth.Width - 1 || v > Truth.Height - 1) continue;
            var d = p - _shown.Eye;
            if (!Topology.Intersect(_shown.Eye, d, out _, out _, out _, out var tw)) continue;
            if (!Truth.Project(_shown.Eye + d * tw, out var u2, out var v2)) continue;
            float ex = (u2 - u) * scale, ey = (v2 - v) * scale, e2 = ex * ex + ey * ey;
            se += e2; mx = MathF.Max(mx, e2); n++;
        }
        ParallaxPoints = n;
        ParallaxPx = n == 0 ? 0f : (float)Math.Sqrt(se / n);
        ParallaxMaxPx = MathF.Sqrt(mx);
    }

    // ── 모니터 ──────────────────────────────────────────────

    private static byte B8(float v) => (byte)Math.Clamp((int)(MathF.Sqrt(Math.Clamp(v, 0f, 1f)) * 255f + 0.5f), 0, 255);

    private int Header(int w, int h)
    {
        var size = 54 + w * h * 3;
        var d = _bmp.AsSpan();
        d.Slice(0, 54).Clear();
        d[0] = (byte)'B'; d[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -h);
        d[26] = 1; d[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), size - 54);
        return size;
    }

    /// <summary>UI 모니터 BMP(0 피드 + 프러스텀 오버레이 · 1 피드 원본 · 2 LED 볼륨 펼침 맵 + 내부 프러스텀 · 3 1:1 모아레 루페 · 4 외부 환경 맵). w 폭은 모드별 상이.</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            int w, h;
            switch (which)
            {
                case 2: w = Topology.MapW; h = LedVolumeTopology.Rows; break;
                case 3: w = LedWallMoireSuppressionSynthesizer.CropW; h = LedWallMoireSuppressionSynthesizer.CropH; break;
                case 4: w = LedWallMoireSuppressionSynthesizer.EnvW; h = LedWallMoireSuppressionSynthesizer.EnvH; break;
                default: w = Truth.Width; h = Truth.Height; break;
            }
            var size = Header(w, h);
            var d = _bmp.AsSpan();
            var wts = Engine.Weights;
            for (int p = 0, o = 54; p < w * h; p++, o += 3)
            {
                float r, g, b;
                switch (which)
                {
                    case 2:
                        {
                            r = Synth.MapR[p]; g = Synth.MapG[p]; b = Synth.MapB[p];
                            var wv = wts[p];
                            if (wv <= 0f) { r *= 0.55f; g *= 0.55f; b = b * 0.55f + 0.08f; }
                            else if (wv < 1f) { r = r * 0.6f + 0.35f; g = g * 0.6f + 0.3f; b *= 0.6f; }
                            if ((p % w) % LedVolumeTopology.ColsPerPanel == 0) { r *= 0.7f; g *= 0.7f; b *= 0.7f; }
                            break;
                        }
                    case 3: r = Synth.LoupeR[p]; g = Synth.LoupeG[p]; b = Synth.LoupeB[p]; break;
                    case 4: r = Synth.EnvTexel(0, p); g = Synth.EnvTexel(1, p); b = Synth.EnvTexel(2, p); break;
                    default:
                        {
                            r = Synth.FeedR[p]; g = Synth.FeedG[p]; b = Synth.FeedB[p];
                            if (which == 0)
                            {
                                switch (Synth.FeedZone[p])
                                {
                                    case 1: r = r * 0.6f + 0.25f; g *= 0.6f; b = b * 0.6f + 0.25f; break;   // 외부 프러스텀 = 마젠타
                                    case 2: r = r * 0.6f + 0.3f; g = g * 0.6f + 0.3f; b *= 0.6f; break;    // 페더 = 황색
                                }
                            }
                            break;
                        }
                }
                d[o] = B8(b); d[o + 1] = B8(g); d[o + 2] = B8(r);
            }
            return Convert.ToBase64String(_bmp, 0, size);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.IcvfxLedWall" };
            _loop.Start();
            LastAction = "▶ ICVFX 가동 · 23.976p · FreeD → 오프액시스 내부 프러스텀 → 외부 환경 맵 → OLPF 모아레 억제 → PTP 젠록 가드";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ ICVFX 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("IcvfxLedWall.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 3 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("IcvfxLedWall.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
