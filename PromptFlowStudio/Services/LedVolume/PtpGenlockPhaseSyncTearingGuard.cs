namespace PromptFlowStudio.Services.LedVolume;

public enum PtpState : byte { Unlocked, Acquiring, Locked, Holdover }
public enum GenlockDevice : byte { RenderNode = 0, LedProcessor = 1, Camera = 2 }

/// <summary>[PromptFlow Studio v17.09.0] PTP 슬레이브 클록(실 오프셋 · 드리프트 · PI 서보 · 행운 패킷 필터).</summary>
public struct PtpSlave
{
    public double TrueOffsetNs, DriftPpb, AdjPpb, Integral, MeasNs, DelayNs, MinDelayNs;
    public int Good, Accepted, Rejected, Steps;
    public PtpState State;
}

/// <summary>
/// [PromptFlow Studio v17.09.0] ⏱ PTP 젠록 위상 동기 &amp; 셔터 티어링 가드(무할당 · ns 이산 시뮬레이션).
/// · IEEE 1588 E2E: Sync/Follow_Up(t1,t2) · Delay_Req/Resp(t3,t4) → 오프셋 = ((t2−t1) − (t4−t3))/2 · 경로 지연 = 합/2.
///   PDV 스파이크는 최소 지연 창(16) + 200 ns 행운 패킷 필터로 거부 · 1 ms 초과 = 스텝 · PI 서보(Kp 0.3 · Ki 0.02 · ±500 ppm).
///   |오프셋| &lt; 1 µs 연속 32 = Locked · GM 소실 = Holdover(주파수 유지).
/// · SMPTE ST 2059-1/2: 프레임 정렬점 = PTP 에포크 기준 k · (1001/24000 s). 각 장치 이벤트 = 로컬 시계의 정렬점 + 위상.
/// · 카메라 롤링 셔터: 행 r 노출 = [시작 + r · 리드아웃/행, + 노출]. LED 래치(렌더 스왑 + 처리 지연)가 어느 행 노출 창 안이면 = 티어링.
/// · 가드: 래치 목표 = 리드아웃+노출 이후 블랭킹 갭 중앙 · 렌더 지연으로 안전 창을 놓치면 스왑 보류(이전 프레임 반복 = 티어링 0) ·
///   노출이 LED PWM 주기의 정수배가 아니면 셔터 각을 최근접 정수배로 스냅(블랙 밴드 제거) · 노출+리드아웃 &gt; 프레임이면 셔터 축소.
/// </summary>
public sealed class PtpGenlockPhaseSyncTearingGuard
{
    public const int Devices = 3, Window = 16, JitterWindow = 64;
    public const double FramePeriodNs = 1e9 * 1001.0 / 24000.0;

    private readonly PtpSlave[] _dev = new PtpSlave[Devices];
    private readonly double[] _delayWin = new double[Devices * Window];
    private readonly double[] _phaseWin = new double[JitterWindow];
    private int _phaseHead, _phaseCount, _lastWindow = -1;
    private long _frame;
    private double _freeClock;
    private ulong _rng = 0x2545_F491_4F6C_DD1DUL;

    public PtpGenlockPhaseSyncTearingGuard()
    {
        _dev[0].TrueOffsetNs = 3.2e6; _dev[0].DriftPpb = 25_000;
        _dev[1].TrueOffsetNs = -1.7e6; _dev[1].DriftPpb = 12_000;
        _dev[2].TrueOffsetNs = 4.4e6; _dev[2].DriftPpb = -18_000;
        for (var i = 0; i < _delayWin.Length; i++) _delayWin[i] = double.MaxValue;
        for (var d = 0; d < Devices; d++) _dev[d].MinDelayNs = double.MaxValue;
    }

    public bool GenlockEnabled { get; set; } = true;
    public bool GuardEnabled { get; set; } = true;
    public bool GrandmasterOnline { get; set; } = true;
    public int SyncPerFrame { get; set; } = 4;
    public double BasePathDelayNs { get; set; } = 2_400;
    public double PathJitterNs { get; set; } = 60;
    public double PdvSpikeProb { get; set; } = 0.03;
    /// <summary>하드웨어 스왑 동기 지터(ns · 젠록) · 프리런 OS 스케줄 지터(ns).</summary>
    public double HwSwapJitterNs { get; set; } = 250;
    public double FreeRunJitterNs { get; set; } = 600_000;
    public double FreeRunHz { get; set; } = 24.0;
    public double LedProcessingNs { get; set; } = 480_000;
    public double ReadoutNs { get; set; } = 12_000_000;
    public double CameraPhaseNs { get; set; }
    public double SafetyMarginNs { get; set; } = 600_000;
    public float ShutterAngle { get; set; } = 172.8f;
    public float LedRefreshHz { get; set; } = 7680f;
    public bool AutoShutter { get; set; } = true;
    /// <summary>다음 프레임 렌더 지연 주입(ns · 1회).</summary>
    public double InjectRenderLateNs { get; set; }

    public long Frame => _frame;
    public ref readonly PtpSlave Device(int d) => ref _dev[d];
    public PtpState State { get; private set; } = PtpState.Unlocked;
    public float EffectiveShutter { get; private set; }
    public double ExposureNs { get; private set; }
    public double SwapPhaseNs { get; private set; }
    public double TargetLatchNs { get; private set; }
    public double SafeGapNs { get; private set; }
    /// <summary>마지막 래치 위치(카메라 프레임 시작 기준 ns).</summary>
    public double LatchPhaseNs { get; private set; }
    public double PhaseErrorNs { get; private set; }
    public double JitterRmsNs { get; private set; }
    public double MaxDeviceOffsetNs { get; private set; }
    public float BandDepthPct { get; private set; }
    public bool LastTear { get; private set; }
    public long Tears { get; private set; }
    public long Drops { get; private set; }
    public long Repeats { get; private set; }
    public long HeldSwaps { get; private set; }
    public long LateFrames { get; private set; }
    public long Steps { get; private set; }
    public long Rejected { get; private set; }
    public double LastFrameUs { get; private set; }

    private double U() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 11) * (1.0 / 9007199254740992.0); }
    private double Gauss() => (U() + U() + U() + U() - 2.0) * 1.7320508;   // σ ≈ 1

    /// <summary>1 카메라 프레임 진행(무할당).</summary>
    public void Step()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var k = ++_frame;
        var dt = FramePeriodNs / SyncPerFrame;
        for (var s = 0; s < SyncPerFrame; s++)
            for (var d = 0; d < Devices; d++) Sync(d, dt);
        UpdateState();
        Shutter();
        Schedule(k);
        LastFrameUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Sync(int d, double dtNs)
    {
        ref var c = ref _dev[d];
        c.TrueOffsetNs += (c.DriftPpb + c.AdjPpb) * dtNs * 1e-9;
        if (!GrandmasterOnline || !GenlockEnabled) { c.State = GrandmasterOnline ? PtpState.Unlocked : c.State == PtpState.Locked || c.State == PtpState.Holdover ? PtpState.Holdover : PtpState.Unlocked; return; }
        double dms = BasePathDelayNs + (U() * 2 - 1) * PathJitterNs, dsm = BasePathDelayNs + (U() * 2 - 1) * PathJitterNs;
        if (U() < PdvSpikeProb) dms += U() * 25_000;
        if (U() < PdvSpikeProb) dsm += U() * 25_000;
        double t1 = 0, t2 = t1 + dms + c.TrueOffsetNs, t3 = t2 + 50_000, t4 = t3 - c.TrueOffsetNs + dsm;
        var meas = ((t2 - t1) - (t4 - t3)) * 0.5;
        var delay = ((t2 - t1) + (t4 - t3)) * 0.5;
        var w = d * Window;
        for (var i = Window - 1; i > 0; i--) _delayWin[w + i] = _delayWin[w + i - 1];
        _delayWin[w] = delay;
        var min = double.MaxValue;
        for (var i = 0; i < Window; i++) if (_delayWin[w + i] < min) min = _delayWin[w + i];
        c.MinDelayNs = min; c.DelayNs = delay;
        if (Math.Abs(meas) > 1e6) { c.TrueOffsetNs -= meas; c.Integral = 0; c.Steps++; Steps++; c.Good = 0; c.State = PtpState.Acquiring; c.MeasNs = meas; c.Accepted++; return; }
        if (delay > min + 200) { c.Rejected++; Rejected++; return; }
        c.Accepted++;
        c.MeasNs = meas;
        c.Integral += meas;
        // θ(n+1) = θ + drift·Δ − Kp·meas − Ki·Σmeas  ⇔  adj = −(Kp·meas + Ki·Σ)/Δ
        c.AdjPpb = Math.Clamp(-(0.3 * meas + 0.02 * c.Integral) / (dtNs * 1e-9), -500_000, 500_000);
        c.Good = Math.Abs(meas) < 1000 ? c.Good + 1 : 0;
        c.State = c.Good >= 32 ? PtpState.Locked : PtpState.Acquiring;
    }

    private void UpdateState()
    {
        var all = PtpState.Locked; var hold = false; double mx = 0;
        for (var d = 0; d < Devices; d++)
        {
            var st = _dev[d].State;
            if (st == PtpState.Holdover) hold = true;
            else if (st < all) all = st;
            mx = Math.Max(mx, Math.Abs(_dev[d].TrueOffsetNs));
        }
        State = hold ? PtpState.Holdover : all;
        MaxDeviceOffsetNs = mx;
    }

    /// <summary>노출 · 블랙 밴드 · 가드 셔터 스냅 · 안전 갭.</summary>
    private void Shutter()
    {
        var ang = Math.Clamp(ShutterAngle, 1f, 358f);
        var e = ang / 360.0 * FramePeriodNs;
        var tp = 1e9 / Math.Max(60.0, LedRefreshHz);
        if (GuardEnabled && AutoShutter)
        {
            var maxE = FramePeriodNs - ReadoutNs - 2 * SafetyMarginNs - 1_000_000;
            if (e > maxE) e = maxE;
            var n = Math.Max(1, Math.Round(e / tp));
            while (n * tp > maxE && n > 1) n--;
            e = n * tp;
        }
        ExposureNs = e;
        EffectiveShutter = (float)(e / FramePeriodNs * 360.0);
        var cycles = e / tp;
        var frac = Math.Abs(cycles - Math.Round(cycles));
        BandDepthPct = (float)(100.0 * frac / Math.Max(1.0, Math.Floor(cycles)));
        if (!GenlockEnabled) BandDepthPct += (float)(100.0 / Math.Max(1.0, cycles) * 0.5);   // PWM 위상 미동기 → 롤링 밴드
        SafeGapNs = FramePeriodNs - ReadoutNs - e;
        TargetLatchNs = ReadoutNs + e + SafeGapNs * 0.5;
        SwapPhaseNs = GuardEnabled ? CameraPhaseNs + TargetLatchNs - LedProcessingNs : 0;
    }

    private void Schedule(long k)
    {
        ref readonly var rn = ref _dev[(int)GenlockDevice.RenderNode];
        var late = InjectRenderLateNs; InjectRenderLateNs = 0;
        if (late > 0) LateFrames++;
        double swap;
        if (GenlockEnabled) swap = k * FramePeriodNs + SwapPhaseNs - rn.TrueOffsetNs + Gauss() * HwSwapJitterNs + late;
        else { _freeClock += 1e9 / FreeRunHz; swap = _freeClock + SwapPhaseNs + Gauss() * FreeRunJitterNs + late; }
        Locate(swap + LedProcessingNs, out var win, out _, out var phase);
        var tear = phase < ReadoutNs + ExposureNs;
        if (GuardEnabled && tear)
        {
            // 안전 창 이탈 → 스왑 보류: 늦은 프레임은 폐기하고 직전 프레임 유지(반복 1 · 티어링 0).
            HeldSwaps++; Repeats++;
            LastTear = false;
            _lastWindow = Math.Max(_lastWindow, win);
            return;
        }
        LastTear = tear;
        if (tear) Tears++;
        if (_lastWindow >= 0)
        {
            if (win <= _lastWindow) Drops++;
            else if (win > _lastWindow + 1) Repeats += win - _lastWindow - 1;
        }
        _lastWindow = Math.Max(_lastWindow, win);
        LatchPhaseNs = phase;
        PhaseErrorNs = phase - TargetLatchNs;
        _phaseWin[_phaseHead] = PhaseErrorNs; _phaseHead = (_phaseHead + 1) % JitterWindow;
        if (_phaseCount < JitterWindow) _phaseCount++;
        double m = 0, s2 = 0;
        for (var i = 0; i < _phaseCount; i++) m += _phaseWin[i];
        m /= _phaseCount;
        for (var i = 0; i < _phaseCount; i++) s2 += (_phaseWin[i] - m) * (_phaseWin[i] - m);
        JitterRmsNs = Math.Sqrt(s2 / _phaseCount);
    }

    /// <summary>GM 시각 → 카메라 프레임 창 index · 시작 · 창 내 위상.</summary>
    private void Locate(double gm, out int win, out double camStart, out double phase)
    {
        var camErr = GenlockEnabled ? _dev[(int)GenlockDevice.Camera].TrueOffsetNs : 0;
        var rel = gm - CameraPhaseNs + camErr;
        win = (int)Math.Floor(rel / FramePeriodNs);
        camStart = win * FramePeriodNs + CameraPhaseNs - camErr;
        phase = gm - camStart;
    }

    /// <summary>카메라 노출 행 r 의 시작(프레임 기준 ns).</summary>
    public double RowStart(float rowFrac) => ReadoutNs * rowFrac;

    public void ResetCounters() { Tears = Drops = Repeats = HeldSwaps = LateFrames = 0; _lastWindow = -1; _phaseCount = 0; _phaseHead = 0; }
}
