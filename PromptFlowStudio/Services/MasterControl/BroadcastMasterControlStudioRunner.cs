namespace PromptFlowStudio.Services.MasterControl;

/// <summary>
/// [PromptFlow Studio v17.05.0] 📺 방송 주조정실(MCR) 러너(60 Hz 전용 스레드 · 시뮬레이션 시각 결정적).
/// 경기 → 카메라 리그 → 13 소스 렌더(결손/블랙 모사) → ISO 링 기록 · 이벤트 자동 마크 → 자율 디렉터(클립 완성 시 RPL 와이프 인 · 종료 시 라이브 믹스 복귀) →
/// 리플레이 120 fps 합성 → 세이프 가드 입력 감시 → 스위처 합성 → ST 2110 -20/-30/-40 패킷화 → 메인/서브 경로(손실 · 단절 · 지연차) →
/// 2022-7 병합 · 2 프레임 플레이아웃 → PTP 서보.
/// </summary>
public sealed class BroadcastMasterControlStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const double FrameUs = 1e6 / Hz;

    private readonly object _gate = new();
    private readonly float[] _ptp = new float[SeriesLen], _skew = new float[SeriesLen], _lossA = new float[SeriesLen], _lossB = new float[SeriesLen], _ms = new float[SeriesLen], _conceal = new float[SeriesLen];
    private readonly long[] _lostUntil = new long[RealtimeBroadcastVisionSwitcherEngine.Inputs], _blackUntil = new long[RealtimeBroadcastVisionSwitcherEngine.Inputs];
    private readonly byte[] _bmp;
    private int _seriesHead, _seriesCount, _liveBeforeReplay = -1;
    private long _prevDropA, _prevDropB, _prevConceal, _burstUntil = -1, _firstFrame = -1;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private int _faultNote;
    private string? _lastAction;

    public BroadcastMasterControlStudioRunner(IServiceProvider sp)
        : this((RealtimeBroadcastVisionSwitcherEngine?)sp.GetService(typeof(RealtimeBroadcastVisionSwitcherEngine)) ?? new RealtimeBroadcastVisionSwitcherEngine(),
               (AutonomousLiveInstantReplaySynthesizer?)sp.GetService(typeof(AutonomousLiveInstantReplaySynthesizer)) ?? new AutonomousLiveInstantReplaySynthesizer(),
               (OnAirBroadcastSignalFailSafeGuard?)sp.GetService(typeof(OnAirBroadcastSignalFailSafeGuard)) ?? new OnAirBroadcastSignalFailSafeGuard())
    {
    }

    public BroadcastMasterControlStudioRunner(RealtimeBroadcastVisionSwitcherEngine switcher, AutonomousLiveInstantReplaySynthesizer replay, OnAirBroadcastSignalFailSafeGuard guard)
    {
        Switcher = switcher; Replay = replay; Guard = guard;
        Scene = new BroadcastArenaScene();
        Sender = new St2110Sender(switcher.Width, switcher.Height);
        var mp = St2110.MaxPacket(switcher.Width);
        PathA = new NetworkPath(mp, 2048, 0xA5A5A5A55A5A5A5BUL) { BaseDelayUs = 450, JitterUs = 200, LossRate = 0.002f };
        PathB = new NetworkPath(mp, 2048, 0x3C6EF372FE94F82BUL) { BaseDelayUs = 2400, JitterUs = 300, LossRate = 0.002f };
        _bmp = new byte[switcher.BmpBytes];
        LastAction = "📋 대기 · 16 입력 · " + switcher.Width + "×" + switcher.Height + " 프록시 · ST 2110 이중 경로";
    }

    public event Action? OnChange;

    public RealtimeBroadcastVisionSwitcherEngine Switcher { get; }
    public AutonomousLiveInstantReplaySynthesizer Replay { get; }
    public OnAirBroadcastSignalFailSafeGuard Guard { get; }
    public BroadcastArenaScene Scene { get; }
    public St2110Sender Sender { get; }
    public NetworkPath PathA { get; }
    public NetworkPath PathB { get; }
    public bool AutoReplay { get; set; } = true;
    public TransitionKind ReplayTransition { get; set; } = TransitionKind.WipeDiamond;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction
    {
        get
        {
            if (Volatile.Read(ref _faultNote) != 0 && Interlocked.Exchange(ref _faultNote, 0) != 0)
                _lastAction = "🛡 세이프 가드 비상 컷 · " + RealtimeBroadcastVisionSwitcherEngine.Names[Math.Max(0, Guard.LastFaultInput)] + (Guard.LastFault == SignalFault.Loss ? " 신호 결손" : " 블랙") +
                              " → " + RealtimeBroadcastVisionSwitcherEngine.Names[Math.Max(0, Guard.LastBackup)] + " (프레임 " + Guard.LastFaultFrame + ")";
            return _lastAction;
        }
        private set => _lastAction = value;
    }
    public long NowUs => (long)(Switcher.Frame * FrameUs);
    public bool BurstActive => Switcher.Frame < _burstUntil;
    public bool IsLost(int i) => Switcher.Frame < _lostUntil[i];
    public bool IsBlack(int i) => Switcher.Frame < _blackUntil[i];
    public int SeriesCount => _seriesCount;
    public float PtpSeries(int i) => _ptp[Idx(i)];
    public float SkewSeries(int i) => _skew[Idx(i)];
    public float LossASeries(int i) => _lossA[Idx(i)];
    public float LossBSeries(int i) => _lossB[Idx(i)];
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    public float ConcealSeries(int i) => _conceal[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    // ── 운영자 조작(UI 스레드 · 게이트 보호) ─────────────────────

    public void Preview(int i) { lock (_gate) Switcher.SetPreview(i); LastAction = "🟩 PVW ← " + RealtimeBroadcastVisionSwitcherEngine.Names[i]; Raise(); }
    public void Program(int i) { lock (_gate) Switcher.SetProgram(i); LastAction = "🟥 PGM 핫 컷 ← " + RealtimeBroadcastVisionSwitcherEngine.Names[i]; Raise(); }
    public void Cut() { lock (_gate) Switcher.Cut(); LastAction = "✂ CUT → PGM " + RealtimeBroadcastVisionSwitcherEngine.Names[Switcher.Pgm]; Raise(); }
    public void Auto() { lock (_gate) Switcher.Auto(); LastAction = "▶ AUTO " + Switcher.Transition + " · " + Switcher.RateFrames + " 프레임"; Raise(); }
    public void SetTBar(float p) { lock (_gate) Switcher.SetTBar(p); }
    public void SetTransition(TransitionKind k) { lock (_gate) Switcher.Transition = k; LastAction = "🎚 트랜지션 = " + k; Raise(); }
    public void SetRate(int frames) { lock (_gate) Switcher.RateFrames = Math.Clamp(frames, 1, 240); }
    public void Ftb() { lock (_gate) Switcher.ToggleFtb(); LastAction = Switcher.FadeToBlack ? "⬛ FTB 페이드 투 블랙" : "⬜ FTB 해제"; Raise(); }

    public void MarkHighlight()
    {
        lock (_gate) Replay.Mark(ArenaEventKind.Manual, Switcher.Frame, 1f);
        LastAction = "⭐ 수동 하이라이트 마크 · 프리롤 " + Replay.PreRollFrames + " / 포스트롤 " + Replay.PostRollFrames + " 프레임";
        Raise();
    }

    public void RollReplay()
    {
        bool ok;
        lock (_gate)
        {
            ok = Replay.TakeNext();
            if (ok) { _liveBeforeReplay = Switcher.Pgm == RealtimeBroadcastVisionSwitcherEngine.ReplayInput ? 0 : Switcher.Pgm; Switcher.SetPreview(RealtimeBroadcastVisionSwitcherEngine.ReplayInput); Switcher.Transition = ReplayTransition; Switcher.Auto(); }
        }
        LastAction = ok ? "🎬 리플레이 수동 송출" : "🎬 송출 대기 클립 없음";
        Raise();
    }

    public void LoseSource(int i, float seconds = 3f) { lock (_gate) _lostUntil[i] = Switcher.Frame + (long)(seconds * Hz); LastAction = "📵 " + RealtimeBroadcastVisionSwitcherEngine.Names[i] + " 신호 결손 모사 " + seconds + "초"; Raise(); }
    public void BlackSource(int i, float seconds = 3f) { lock (_gate) _blackUntil[i] = Switcher.Frame + (long)(seconds * Hz); LastAction = "⬛ " + RealtimeBroadcastVisionSwitcherEngine.Names[i] + " 블랙 모사 " + seconds + "초"; Raise(); }
    public void OutageA(float seconds = 3f) { lock (_gate) PathA.Outage(NowUs, (long)(seconds * 1e6)); LastAction = "🔌 메인 경로(A) 단절 " + seconds + "초 — 2022-7 서브 경로 무결절 유지"; Raise(); }
    public void OutageB(float seconds = 3f) { lock (_gate) PathB.Outage(NowUs, (long)(seconds * 1e6)); LastAction = "🔌 서브 경로(B) 단절 " + seconds + "초"; Raise(); }

    /// <summary>양 경로 동시 손실 버스트(0.25 초 · 30%) → 라인 은닉 시연.</summary>
    public void DualBurst()
    {
        lock (_gate) _burstUntil = Switcher.Frame + Hz / 4;
        LastAction = "⚡ 양 경로 동시 손실 버스트 — 결손 라인 직전 송출 유지(블랙 없음)";
        Raise();
    }

    public void ToggleGm(int g)
    {
        lock (_gate)
        {
            Guard.Ptp.SetGmAlive(g, !Guard.Ptp.GmAlive(g));
            if (Guard.Ptp.GmAlive(g)) Guard.Ptp.Reevaluate();
        }
        LastAction = Guard.Ptp.GmAlive(g) ? "🛰 " + PtpSlaveServo.GmNames[g] + " 복구 · BMCA 재평가" : "🛰 " + PtpSlaveServo.GmNames[g] + " 장애 — Announce 타임아웃 후 BMCA/홀드오버";
        Raise();
    }

    public void SetLoss(int path, float rate) { lock (_gate) (path == 0 ? PathA : PathB).LossRate = Math.Clamp(rate, 0f, 0.5f); }
    public void SetSkewMs(double ms) { lock (_gate) PathB.BaseDelayUs = Math.Clamp(ms, 0, 60) * 1000; }
    public void SetJitterUs(double us) { lock (_gate) { PathA.JitterUs = PathB.JitterUs = Math.Clamp(us, 0, 5000); } }

    /// <summary>UI 모니터 BMP(0 = PGM · 1 = PVW · 2 = 멀티뷰어 · 3 = ON-AIR 수신 · 4 = 리플레이) — 게이트 내 복사.</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            ReadOnlySpan<byte> src = which switch
            {
                0 => Switcher.ProgramBus,
                1 => Switcher.PreviewBus,
                2 => Switcher.Multiviewer,
                3 => Guard.AirRgba(),
                _ => Switcher.Input(RealtimeBroadcastVisionSwitcherEngine.ReplayInput),
            };
            Switcher.WriteBmp(src, _bmp);
            return Convert.ToBase64String(_bmp);
        }
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            var sw = Switcher;
            var f = sw.Frame + 1;
            for (var i = 0; i < RealtimeBroadcastVisionSwitcherEngine.ReplayInput; i++) { sw.InputLost[i] = f < _lostUntil[i]; sw.InputBlack[i] = f < _blackUntil[i]; }
            Scene.Step(dt);
            sw.UpdateCameras(Scene, dt);
            sw.RenderSources(Scene);
            var frame = sw.Frame;
            if (_firstFrame < 0) _firstFrame = frame;
            Replay.Record(sw, frame);
            Replay.Observe(Scene);
            if (AutoReplay) Direct();
            Replay.Render(sw.Input(RealtimeBroadcastVisionSwitcherEngine.ReplayInput));
            if (Guard.Monitor(sw) != SignalFault.None) _faultNote = 1;
            sw.Composite(Scene.Excitement);
            var now = (long)(frame * FrameUs);
            var burst = frame < _burstUntil;
            float la = PathA.LossRate, lb = PathB.LossRate;
            if (burst) { PathA.LossRate = MathF.Max(la, 0.3f); PathB.LossRate = MathF.Max(lb, 0.3f); }
            Sender.SendFrame(sw.ProgramBus, sw.ProgramAudio, frame, now, FrameUs, PathA, PathB);
            if (burst) { PathA.LossRate = la; PathB.LossRate = lb; }
            var deadline = (long)((frame + 1) * FrameUs);
            PathA.Deliver(deadline, Guard, 0);
            PathB.Deliver(deadline, Guard, 1);
            if (frame - Guard.PlayoutFrames >= _firstFrame) Guard.Playout(frame - Guard.PlayoutFrames);
            Guard.Ptp.Step(FrameUs);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _ptp[_seriesHead] = (float)Guard.Ptp.OffsetNs;
            _skew[_seriesHead] = (float)(Guard.Video.SkewUs / 1000.0);
            _lossA[_seriesHead] = PathA.Dropped - _prevDropA; _prevDropA = PathA.Dropped;
            _lossB[_seriesHead] = PathB.Dropped - _prevDropB; _prevDropB = PathB.Dropped;
            _conceal[_seriesHead] = Guard.ConcealedLines - _prevConceal; _prevConceal = Guard.ConcealedLines;
            _ms[_seriesHead] = ms;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>자율 디렉터: 완성 클립 → RPL 프리뷰 + 리플레이 트랜지션 · 클립 종료 → 직전 라이브 소스로 믹스 복귀.</summary>
    private void Direct()
    {
        var sw = Switcher;
        const int rpl = RealtimeBroadcastVisionSwitcherEngine.ReplayInput;
        if (sw.InTransition) return;
        if (!Replay.IsOnAir && Replay.QueueCount > 0 && sw.Pgm != rpl && Replay.TakeNext())
        {
            _liveBeforeReplay = sw.Pgm;
            sw.SetPreview(rpl); sw.Transition = ReplayTransition; sw.Auto();
            return;
        }
        if (sw.Pgm == rpl && !Replay.IsOnAir && _liveBeforeReplay >= 0)
        {
            sw.SetPreview(_liveBeforeReplay); sw.Transition = TransitionKind.Mix; sw.Auto();
            _liveBeforeReplay = -1;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.MasterControl" };
            _loop.Start();
            LastAction = "▶ 주조정실 가동 · " + Hz + " Hz · ST 2110 -20/-30/-40 · 2022-7 이중 경로";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 주조정실 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("MasterControl.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MasterControl.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
