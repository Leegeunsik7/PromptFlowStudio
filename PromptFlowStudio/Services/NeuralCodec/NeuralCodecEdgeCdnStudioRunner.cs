using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.NeuralCodec;

/// <summary>
/// [PromptFlow Studio v17.02.0] 📡 신경망 코덱 &amp; 글로벌 에지 CDN 스튜디오 러너(30 Hz 전용 스레드 · 시뮬레이션 시각 결정적).
/// CDN 틱(라우팅 · ABR) → 감시 지역 경로(손실/RTT/지터)를 FEC 가드에 · ABR 비트레이트를 코덱 레이트 제어에 →
/// 합성 소스(또는 v16.58 3DGS 스튜디오 씬 스냅샷) 인코드 → 패킷화 · FEC · 채널 → 복원 디코드/은닉 → 시청자 화면 PSNR/SSIM.
/// </summary>
public sealed class NeuralCodecEdgeCdnStudioRunner : IDisposable
{
    public const int Hz = 30, SeriesLen = 180, Splats = 2000, PreviewW = 64, PreviewH = 36;

    private readonly object _gate = new();
    private readonly GaussianSplattingStudioRunner? _gs;
    private readonly GaussianScene _gsScene = new();
    private readonly float[] _src = new float[NeuralVolumetricVideoCodecEngine.Pixels];
    private readonly VolumetricSplatFrame _splat = new(NeuralVolumetricVideoCodecEngine.MaxSplats);
    private readonly float[] _psnr = new float[SeriesLen], _mbps = new float[SeriesLen], _loss = new float[SeriesLen], _rtt = new float[SeriesLen];
    private readonly byte[] _outcome = new byte[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private FrameOutcome _prevOutcome = FrameOutcome.None;
    private bool _outcomeNote;
    private double _concealPsnrSum;
    private long _concealPsnrN;

    public NeuralCodecEdgeCdnStudioRunner(IServiceProvider sp)
        : this((NeuralVolumetricVideoCodecEngine?)sp.GetService(typeof(NeuralVolumetricVideoCodecEngine)) ?? new NeuralVolumetricVideoCodecEngine(),
               (GlobalEdgeCdnRoutingSynthesizer?)sp.GetService(typeof(GlobalEdgeCdnRoutingSynthesizer)) ?? new GlobalEdgeCdnRoutingSynthesizer(),
               (PacketLossConcealmentFecGuard?)sp.GetService(typeof(PacketLossConcealmentFecGuard)) ?? new PacketLossConcealmentFecGuard(),
               sp.GetService(typeof(GaussianSplattingStudioRunner)) as GaussianSplattingStudioRunner)
    {
    }

    public NeuralCodecEdgeCdnStudioRunner(NeuralVolumetricVideoCodecEngine codec, GlobalEdgeCdnRoutingSynthesizer cdn, PacketLossConcealmentFecGuard guard,
                                          GaussianSplattingStudioRunner? splatStudio = null)
    {
        Codec = codec; Cdn = cdn; Guard = guard; _gs = splatStudio;
        Codec.FrameRate = Hz;
        LastAction = "📋 대기 · 256×144 프록시(16K 환산) · 스플랫 " + Splats;
    }

    public event Action? OnChange;

    public NeuralVolumetricVideoCodecEngine Codec { get; }
    public GlobalEdgeCdnRoutingSynthesizer Cdn { get; }
    public PacketLossConcealmentFecGuard Guard { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double Now => _tick / (double)Hz;
    public double LastTickMs { get; private set; }
    public bool UseSplatStudio { get; set; }
    public bool SplatStudioBound => _gs is not null;
    public bool SplatFromStudio { get; private set; }
    public bool RateControl { get; set; } = true;
    public float ViewerPsnr { get; private set; }
    public float ViewerSsim { get; private set; }
    public float ConcealPsnr => _concealPsnrN == 0 ? 0f : (float)(_concealPsnrSum / _concealPsnrN);
    public string? LastAction { get; private set; }
    public ReadOnlySpan<float> Source => _src;
    public int SeriesCount => _seriesCount;
    public float PsnrSeries(int i) => _psnr[Idx(i)];
    public float MbpsSeries(int i) => _mbps[Idx(i)];
    public float LossSeries(int i) => _loss[Idx(i)];
    public float RttSeries(int i) => _rtt[Idx(i)];
    public FrameOutcome OutcomeSeries(int i) => (FrameOutcome)_outcome[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    public void Storm() { lock (_gate) Guard.Storm(Now, 4.0); LastAction = "🌩 버스트 손실 폭풍 4 s"; Raise(); }
    public void ForceKey() { lock (_gate) Codec.ForceKeyframe(); LastAction = "🔑 키프레임 강제(IDR)"; Raise(); }
    public void FailPrimary()
    {
        lock (_gate)
        {
            var p = Cdn.PathPrimary;
            if (p > 0 && p < GlobalEdgeCdnRoutingSynthesizer.PeerBase) Cdn.FailNode(p);
            else Cdn.FailNode(1 + Cdn.ViewerRegion % 8);
        }
        LastAction = Cdn.LastEvent;
        Raise();
    }
    public void ToggleNode(int j) { lock (_gate) Cdn.ToggleNode(j); LastAction = Cdn.LastEvent; Raise(); }
    public void FlashCrowd() { lock (_gate) Cdn.FlashCrowd(Cdn.ViewerRegion); LastAction = Cdn.LastEvent; Raise(); }
    public void BandwidthDrop() { lock (_gate) Cdn.BandwidthDrop(Cdn.ViewerRegion); LastAction = Cdn.LastEvent; Raise(); }

    /// <summary>1틱(무할당 · 33 ms).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            var now = (_tick + 1) / (double)Hz;
            // ── CDN → 경로 · 비트레이트 ──
            Cdn.Step(dt, now);
            Guard.SetPath(Cdn.PathLoss, Cdn.PathRttMs, Cdn.PathJitterMs);
            if (Guard.TakePli(now)) Codec.ForceKeyframe();
            Codec.TargetBitsPerFrame = RateControl ? (float)(Cdn.ViewerRateMbps * 1e6 / Hz / NeuralVolumetricVideoCodecEngine.Upscale16K) : 0f;
            // ── 소스 ──
            NeuralVolumetricVideoCodecEngine.SynthesizeFrame(_src, now);
            SplatFromStudio = false;
            if (UseSplatStudio && _gs is { } g && g.Scene.Count > 0)
            {
                g.SnapshotScene(_gsScene);
                CopyScene(_gsScene, _splat);
                SplatFromStudio = true;
            }
            else NeuralVolumetricVideoCodecEngine.SynthesizeSplats(_splat, Splats, now);
            // ── 인코드 → 전송 → 복원/은닉 ──
            Codec.Encode(_src, _splat);
            var o = Guard.Deliver(Codec.AccessUnit, Codec, now);
            ViewerPsnr = NeuralVolumetricVideoCodecEngine.Psnr(_src, Codec.Decoded);
            ViewerSsim = NeuralVolumetricVideoCodecEngine.Ssim(_src, Codec.Decoded);
            if (o == FrameOutcome.Concealed) { _concealPsnrSum += ViewerPsnr; _concealPsnrN++; }
            if (o != _prevOutcome && (o == FrameOutcome.Concealed || o == FrameOutcome.Frozen || _prevOutcome == FrameOutcome.Drift)) _outcomeNote = true;
            _prevOutcome = o;
            // ── 계측 ──
            _psnr[_seriesHead] = ViewerPsnr; _mbps[_seriesHead] = (float)Codec.Mbps16K;
            _loss[_seriesHead] = Guard.LastLost; _rtt[_seriesHead] = Cdn.PathRttMs; _outcome[_seriesHead] = (byte)o;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>v16.58 3DGS 씬 → SoA 10 속성(회전/고차 SH 제외 · 최대 MaxSplats).</summary>
    private static void CopyScene(GaussianScene s, VolumetricSplatFrame f)
    {
        var n = Math.Min(s.Count, f.Capacity);
        f.Count = n;
        for (var i = 0; i < n; i++)
        {
            f.Plane[0][i] = s.Pos[i * 3]; f.Plane[1][i] = s.Pos[i * 3 + 1]; f.Plane[2][i] = s.Pos[i * 3 + 2];
            f.Plane[3][i] = s.Scale[i * 3]; f.Plane[4][i] = s.Scale[i * 3 + 1]; f.Plane[5][i] = s.Scale[i * 3 + 2];
            f.Plane[6][i] = s.Opacity[i];
            var c = s.BaseColor(i);
            f.Plane[7][i] = c.X; f.Plane[8][i] = c.Y; f.Plane[9][i] = c.Z;
        }
    }

    /// <summary>디코드 화면 다운샘플(미리보기 · 박스 평균) — 소비자 버퍼에 기록.</summary>
    public void Preview(Span<float> dst, bool source)
    {
        lock (_gate)
        {
            ReadOnlySpan<float> f = source ? _src : Codec.Decoded;
            const int sx = NeuralVolumetricVideoCodecEngine.Width / PreviewW, sy = NeuralVolumetricVideoCodecEngine.Height / PreviewH;
            for (var py = 0; py < PreviewH; py++)
                for (var px = 0; px < PreviewW; px++)
                {
                    var s = 0f;
                    for (var y = 0; y < sy; y++)
                        for (var x = 0; x < sx; x++) s += f[(py * sy + y) * NeuralVolumetricVideoCodecEngine.Width + px * sx + x];
                    dst[py * PreviewW + px] = s / (sx * sy);
                }
        }
    }

    /// <summary>최근 동작(손실/은닉 전이는 틱에서 플래그만 → 조회 시 지연 포맷).</summary>
    public string? Action
    {
        get
        {
            if (_outcomeNote)
            {
                _outcomeNote = false;
                LastAction = "📶 " + PacketLossConcealmentFecGuard.OutcomeNames[(int)Guard.LastOutcome] + " · 손실 " + Guard.LastLost + " 패킷 · PLI " + Guard.PliSent;
            }
            return LastAction;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.NeuralCodec" };
            _loop.Start();
            LastAction = "▶ 스트리밍 가동 · " + Hz + " fps · " + GlobalEdgeCdnRoutingSynthesizer.RegionNames[Cdn.ViewerRegion] + " 시청자";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 스트리밍 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("NeuralCodec.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            if (n % 3 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("NeuralCodec.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
