using System.Numerics;
using PromptFlowStudio.Services.CognitiveCinematography;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.NeuralVoice;

namespace PromptFlowStudio.Services.StereoXr;

/// <summary>
/// [PromptFlow Studio v17.22.0] 🥽 AI 공간 컴퓨팅 &amp; WebXR 스튜디오 러너(60 Hz 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 틱 HMD 진값 헤드 포즈(사케이드 시선 · 급회전 버스트) → 데이터 채널 업링크 → 서버가 최신 포즈로 v17.21 블로킹 세트를
/// 양안 포비티드 렌더 → 타일 델타 부호화 · RTP 패킷화 + v17.18 음성 바이노럴 오디오 → 다운링크(지터 · 스파이크) →
/// HMD 복호 → IMU 예측 ATW 가드 → 표시 순으로 처리한다.
/// </summary>
public sealed class SpatialComputingXrStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, TickSamples = WebRtcUltraLowLatencyXrSynthesizer.AudioFs / Hz;
    public const float Dt = 1f / Hz, DtMs = 1000f / Hz, ServerPhaseMs = 2.5f;
    public const string Line = "공간 컴퓨팅 스튜디오에 오신 것을 환영합니다. 고개를 돌려 보세요.";

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _rawS = new float[SeriesLen], _effS = new float[SeriesLen], _resS = new float[SeriesLen], _uncS = new float[SeriesLen], _rateS = new float[SeriesLen], _mbpsS = new float[SeriesLen];
    private readonly float[] _mono = new float[TickSamples];
    private readonly byte[] _bmp = new byte[StereoscopicFoveatedRenderingEngine.BmpMax];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private double _simMs;
    private uint _poseSeq;
    private int _viewLen;

    public SpatialComputingXrStudioRunner(IServiceProvider sp) : this() { }

    public SpatialComputingXrStudioRunner()
    {
        Set = new CinematicBlockingSet();
        Renderer = new StereoscopicFoveatedRenderingEngine();
        Link = new WebRtcUltraLowLatencyXrSynthesizer();
        Guard = new MotionToPhotonAtwReprojectionGuard();
        Tract = new BiomechanicalVocalTractResonanceSynthesizer();
        Voice = new RealtimeNeuralVoiceSynthesisEngine(Tract);
        Voice.SetText(Line);
        LastAction = "📋 대기 · 양안 " + StereoscopicFoveatedRenderingEngine.EW + "×" + StereoscopicFoveatedRenderingEngine.EH + " ×2 · VRS 타일 " + StereoscopicFoveatedRenderingEngine.Tile + "² · RTP MTU " + WebRtcUltraLowLatencyXrSynthesizer.Mtu + " B · M2P 예산 " + (int)MotionToPhotonAtwReprojectionGuard.BudgetMs + " ms";
    }

    public event Action? OnChange;

    public CinematicBlockingSet Set { get; }
    public StereoscopicFoveatedRenderingEngine Renderer { get; }
    public WebRtcUltraLowLatencyXrSynthesizer Link { get; }
    public MotionToPhotonAtwReprojectionGuard Guard { get; }
    public BiomechanicalVocalTractResonanceSynthesizer Tract { get; }
    public RealtimeNeuralVoiceSynthesisEngine Voice { get; }
    public StereoView View { get; private set; } = StereoView.SideBySide;
    public bool Audio { get; set; } = true;
    public bool HeadMotion { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double SimMs => _simMs;
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int GazeActor { get; private set; }
    public Vector2 Gaze { get; private set; } = new(0.5f, 0.5f);
    public Quaternion TruthOrientation { get; private set; } = Quaternion.Identity;
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float RawM2pSeries(int i) => _rawS[Idx(i)];
    public float EffectiveM2pSeries(int i) => _effS[Idx(i)];
    public float ResidualSeries(int i) => _resS[Idx(i)];
    public float UncorrectedSeries(int i) => _uncS[Idx(i)];
    public float ShadingSeries(int i) => _rateS[Idx(i)];
    public float BitrateSeries(int i) => _mbpsS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public void SetIpd(float mm) { lock (_gate) Renderer.IpdMm = Math.Clamp(mm, StereoscopicFoveatedRenderingEngine.MinIpd, StereoscopicFoveatedRenderingEngine.MaxIpd); LastAction = "👀 IPD " + F1(Renderer.IpdMm) + " mm"; Raise(); }
    public void SetConvergence(float m) { lock (_gate) Renderer.ConvergenceM = Math.Clamp(m, 0.5f, 20f); LastAction = "🎯 수렴 거리 " + F1(Renderer.ConvergenceM) + " m(시차 0 평면)"; Raise(); }
    public void SetVrs(bool on) { lock (_gate) Renderer.VrsEnabled = on; LastAction = on ? "🔍 포비티드 VRS ON(1×1 · 2×2 · 4×4)" : "🔍 VRS OFF(전 타일 1×1)"; Raise(); }
    public void SetFovea(float deg) { lock (_gate) { Renderer.FoveaDeg = Math.Clamp(deg, 4f, 40f); Renderer.MidDeg = MathF.Max(Renderer.MidDeg, Renderer.FoveaDeg + 6f); } LastAction = "◎ 중심와 반경 " + F1(Renderer.FoveaDeg) + "°"; Raise(); }
    public void SetView(StereoView v) { View = v; LastAction = v switch { StereoView.Anaglyph => "🟥🟦 적청 애너글리프", StereoView.RateMap => "🗺 VRS 레이트 맵", _ => "◫ 양안 분할(SBS)" }; Raise(); }
    public void SetAtw(AtwMode m) { lock (_gate) Guard.Mode = m; LastAction = m switch { AtwMode.Always => "⏱ ATW 항상", AtwMode.Auto => "🛡 ATW 자동(스파이크 · 반복 · 회전 편차)", _ => "⛔ ATW OFF(원지연 그대로)" }; Raise(); }
    public void SetPrediction(bool on) { lock (_gate) Guard.Prediction = on; LastAction = on ? "📈 IMU 각속도 예측 ON" : "📈 IMU 예측 OFF(최신 샘플)"; Raise(); }
    public void SetSpikes(bool on) { lock (_gate) Link.Spikes = on; LastAction = on ? "📶 Wi-Fi 지연 스파이크 주입 ON" : "📶 스파이크 OFF"; Raise(); }
    public void SetJitter(float ms) { lock (_gate) Link.JitterMs = Math.Clamp(ms, 0f, 30f); LastAction = "〰 지터 " + F1(Link.JitterMs) + " ms"; Raise(); }
    public void SetLoss(float pct) { lock (_gate) Link.LossRate = Math.Clamp(pct, 0f, 10f) / 100f; LastAction = "📉 패킷 손실 " + F1(Link.LossRate * 100f) + "%"; Raise(); }
    public void SetHeadMotion(bool on) { HeadMotion = on; LastAction = on ? "🙆 헤드 모션(급회전 버스트) ON" : "🙆 헤드 고정"; Raise(); }

    // ── 시뮬레이션 HMD ───────────────────────────────────────

    /// <summary>진값 헤드 포즈: 느린 흔들림 + 3 초마다 0.3 초 급회전(±29° · 최대 ≈ 143°/s).</summary>
    public static void HeadTruth(double tMs, bool motion, out Vector3 pos, out Quaternion q)
    {
        var t = (float)(tMs / 1000.0);
        if (!motion) t = 0f;
        var k = (int)MathF.Floor(t / 3f);
        var f = Math.Clamp((t - 3f * k) / 0.3f, 0f, 1f);
        var s = f * f * (3f - 2f * f);
        var turn = (k & 1) == 0 ? 0.5f * s : 0.5f * (1f - s);
        var yaw = MathF.PI + 0.25f - turn + 0.12f * MathF.Sin(0.4f * t);
        q = StereoscopicFoveatedRenderingEngine.HeadRotation(yaw, -0.12f + 0.05f * MathF.Sin(0.7f * t), 0.02f * MathF.Sin(0.5f * t));
        pos = new Vector3(0.6f + 0.05f * MathF.Sin(0.3f * t), 1.6f + 0.01f * MathF.Sin(1.1f * t), 4f);
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당): HMD 업링크 → 서버 렌더/부호화 → 다운링크 → HMD 복호 · ATW · 표시(다음 vsync).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("StereoXr.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = _simMs;
            // 1) HMD: 진값 포즈 · 시선 추적(1.6 초마다 액터 머리로 사케이드)
            HeadTruth(now, HeadMotion, out var hp, out var hq);
            GazeActor = (int)(now / 1600.0) & 1;
            StereoscopicFoveatedRenderingEngine.ComputeEyes(hp, hq, Renderer.IpdMm, Renderer.FovHDeg, Renderer.ConvergenceM, out var le, out _);
            if (le.Project(Set.HeadCenter(GazeActor), out var gx, out var gy, out _)) Gaze = new Vector2(Math.Clamp(gx, 0f, 1f), Math.Clamp(gy, 0f, 1f));
            var trig = 0.5f + 0.5f * MathF.Sin((float)(now * 0.002));
            Link.SendPose(new XrClientPose(++_poseSeq, (long)(now * 1000.0), hp, hq, (ushort)(GazeActor + 1), trig, new Vector2(MathF.Sin((float)(now * 0.001)), 0f), Gaze), now);
            // 2) 서버: 최신 포즈로 양안 포비티드 렌더 → 부호화 · 패킷화 + 바이노럴 오디오
            var ts = now + ServerPhaseMs;
            Link.PumpServer(ts);
            if (Link.HasPose)
            {
                var p = Link.LatestPose;
                Renderer.SetPose(p.Position, p.Orientation);
                Renderer.SetGaze(p.Gaze, p.Gaze);
                Renderer.Render(Set);
                Link.SendFrame(Renderer.Pixels, Renderer.Rates, p.Position, p.Orientation, p.TimeUs / 1000.0, ts);
                if (Audio)
                {
                    for (var i = 0; i < TickSamples; i += RealtimeNeuralVoiceSynthesisEngine.Chunk) Voice.RenderChunk(_mono.AsSpan(i, RealtimeNeuralVoiceSynthesisEngine.Chunk));
                    Link.SendAudio(_mono, Set.HeadCenter(0), p.Position, p.Orientation, ts);
                }
            }
            // 3) HMD: 다음 vsync 까지 도착 패킷 복호 → IMU 예측 ATW → 표시
            var vsync = now + DtMs;
            var fresh = Link.PumpClient(vsync) > 0;
            HeadTruth(vsync, HeadMotion, out _, out var truth);
            TruthOrientation = truth;
            HeadTruth(vsync - Guard.AtwLeadMs - 1.0, HeadMotion, out _, out var q0);
            HeadTruth(vsync - Guard.AtwLeadMs, HeadMotion, out _, out var q1);
            Guard.FeedImu(vsync - Guard.AtwLeadMs - 1.0, q0);
            Guard.FeedImu(vsync - Guard.AtwLeadMs, q1);
            if (Link.ClientHasFrame)
                Guard.Process(Link.ClientFrame, Link.ClientRenderPosition, Link.ClientRenderOrientation, Link.ClientPoseTimeMs, fresh, vsync, Renderer.IpdMm, Renderer.FovHDeg, Renderer.ConvergenceM, truth);
            Set.Advance(Dt);
            _simMs = vsync;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _rawS[h] = Guard.RawM2pMs; _effS[h] = Guard.EffectiveM2pMs; _resS[h] = Guard.ResidualDeg; _uncS[h] = Guard.UncorrectedDeg; _rateS[h] = Renderer.ShadingRatio; _mbpsS[h] = Link.BitrateMbps;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>표시(ATW 후) 프레임 BMP — 첫 프레임 도착 전엔 서버 렌더.</summary>
    public string RenderBase64()
    {
        lock (_gate)
        {
            var src = Link.ClientHasFrame ? Guard.Output : Renderer.Pixels;
            _viewLen = StereoscopicFoveatedRenderingEngine.WriteBmp(src, Renderer.Rates, View, _bmp);
            return Convert.ToBase64String(_bmp, 0, _viewLen);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.StereoXr" };
            _loop.Start();
            LastAction = "▶ 공간 컴퓨팅 스트리밍 가동 · 60 Hz · 포즈 업링크 → 양안 포비티드 렌더 → RTP/L16 → 복호 → ATW";
        }
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("StereoXr.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("StereoXr.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
