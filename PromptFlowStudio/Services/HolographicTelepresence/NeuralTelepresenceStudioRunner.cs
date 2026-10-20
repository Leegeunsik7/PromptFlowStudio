using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.HolographicTelepresence;

/// <summary>
/// [PromptFlow Studio v17.25.0] 🌐 AI 홀로그래픽 텔레프레전스 스튜디오 러너(60 Hz 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 프레임 롤백 가드(원격 3 피어 입력 수신 → 롤백/재시뮬레이션 → 에르미트 표시) → 크로스모달 합성기(시선 교차 보정 · 공간 오디오/AEC · 햅틱 이벤트)
/// → 로컬 4D 가우시안 아바타를 원격 참가자 HMD 위치별 동적 LoD 로 인코드/디코드(청크 데이터그램 손실 포함) 순으로 처리한다.
/// </summary>
public sealed class NeuralTelepresenceStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, Viewers = NeuralVolumetricGaussianStreamingEngine.MaxViewers, Participants = TelepresenceScenario.Participants;
    public const float Dt = 1f / Hz, DtMs = 1000f / Hz;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _rttS = new float[SeriesLen], _rbS = new float[SeriesLen], _mbpsS = new float[SeriesLen], _erleS = new float[SeriesLen], _warpS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private double _simMs;
    private uint _rng = 0xC0FFEEu;

    public NeuralTelepresenceStudioRunner(IServiceProvider sp) : this() { }

    public NeuralTelepresenceStudioRunner() : this(GaussianAvatar.DeformableGaussianSkinningBindingEngine.DefaultCount) { }

    public NeuralTelepresenceStudioRunner(int gaussians)
    {
        Stream = new NeuralVolumetricGaussianStreamingEngine(gaussians);
        Stream.SetPlacement(TelepresenceScenario.Seat(0), TelepresenceScenario.SeatYaw(0));
        Interaction = new MultiUserCrossModalInteractionSynthesizer();
        Rollback = new DeterministicRollbackStateResimulationGuard();
        for (var v = 0; v < Viewers; v++) Stream.SetViewerPosition(v, MultiUserCrossModalInteractionSynthesizer.HeadPosition(v + 1, 0));
        LastAction = "📋 대기 · 가우시안 " + Stream.Count + " · 옥트리 청크 " + Stream.ChunkCount + " · 원격 " + Viewers + " 피어 · 롤백 창 " + DeterministicRollbackStateResimulationGuard.MaxRollback + " 프레임";
    }

    public event Action? OnChange;

    public NeuralVolumetricGaussianStreamingEngine Stream { get; }
    public MultiUserCrossModalInteractionSynthesizer Interaction { get; }
    public DeterministicRollbackStateResimulationGuard Rollback { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double SimMs => _simMs;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public float TotalMbps { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float RttSeries(int i) => _rttS[Idx(i)];
    public float RollbackSeries(int i) => _rbS[Idx(i)];
    public float MbpsSeries(int i) => _mbpsS[Idx(i)];
    public float ErleSeries(int i) => _erleS[Idx(i)];
    public float WarpSeries(int i) => _warpS[Idx(i)];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public void SetBandwidth(int v, float mbps) { lock (_gate) Stream.SetBudgetMbps(v, mbps); LastAction = "📶 " + TelepresenceScenario.Names[v + 1] + " LoD 대역폭 → " + F0(Stream.BudgetMbps(v)) + " Mbps"; Raise(); }
    public void SetAllBandwidth(float mbps) { lock (_gate) for (var v = 0; v < Viewers; v++) Stream.SetBudgetMbps(v, mbps); LastAction = "📶 전체 피어 LoD 대역폭 → " + F0(Stream.BudgetMbps(0)) + " Mbps"; Raise(); }
    public void SetLink(int p, float rttMs, float jitterMs, float lossPct) { lock (_gate) Rollback.SetLink(p, rttMs, jitterMs, lossPct / 100f); LastAction = "🛰 " + TelepresenceScenario.Names[p] + " 링크 RTT " + F0(Rollback.RttMs(p)) + " ms · 지터 " + F0(Rollback.JitterMs(p)) + " ms · 손실 " + F1(Rollback.LossRate(p) * 100f) + " %"; Raise(); }
    public void SetRollback(bool on) { lock (_gate) Rollback.RollbackEnabled = on; LastAction = on ? "⏪ 결정적 롤백 ON(불일치 프레임 복원 → 전진 재시뮬레이션)" : "⛔ 롤백 OFF(예측 고정 · 디싱크 비교)"; Raise(); }
    public void SetSmoothing(bool on) { lock (_gate) Rollback.Smoothing = on; LastAction = on ? "〰 에르미트 워프 평활 ON(" + Rollback.SmoothFrames + " 프레임)" : "⚡ 평활 OFF(롤백 보정 즉시 반영)"; Raise(); }
    public void SetEyeContact(bool on) { lock (_gate) Interaction.EyeContactCorrection = on; LastAction = on ? "👁 상호 아이컨택 보정 ON(25° 원뿔 교차 재조준)" : "👁 보정 OFF(원시 시선 · 정합 오차 노출)"; Raise(); }
    public void SetAec(bool on) { lock (_gate) Interaction.AecEnabled = on; LastAction = on ? "🎙 AEC ON(NLMS 128 탭 · Geigel 이중 발화)" : "🔊 AEC OFF(스피커 반향 노출)"; Raise(); }
    public void SetHaptics(bool on) { lock (_gate) Interaction.HapticsEnabled = on; LastAction = on ? "🤝 원격 햅틱 합성 ON" : "🤝 햅틱 OFF"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) { Stream.UseSimd = on; Interaction.UseSimd = on; } LastAction = on ? "⚡ SIMD(양자화 · 최소/최대 · NLMS)" : "🐢 스칼라 경로"; Raise(); }
    public void ResetMetrics() { lock (_gate) { Rollback.ResetMetrics(); Interaction.ResetMetrics(); MaxTickMs = 0; } LastAction = "↺ 지표 초기화"; Raise(); }

    /// <summary>1 프레임(무할당): 롤백 가드 → 시선/오디오/햅틱 → 3 피어 LoD 계획 · 인코드 · 디코드.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("Telepresence.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = _simMs;
            Rollback.Advance(now);

            Span<Vector3> hands = stackalloc Vector3[Participants];
            Span<Vector2> bodies = stackalloc Vector2[TelepresenceScenario.Bodies];
            Span<float> oneWay = stackalloc float[Participants];
            for (var k = 0; k < TelepresenceScenario.Bodies; k++) bodies[k] = Rollback.Display(k);
            for (var p = 0; p < Participants; p++)
            {
                var d = Rollback.Display(TelepresenceScenario.Bodies + p);
                var u = Rollback.UsedInput(p);
                hands[p] = new Vector3(d.X, u.Press != 0 ? TelepresenceScenario.TableY + 0.03f : TelepresenceScenario.TableY + 0.15f, d.Y);
                oneWay[p] = p == 0 ? 0f : Rollback.OneWayMs(p);
            }
            Interaction.Step(now, hands, bodies, oneWay);

            Stream.Animate(now);
            var mbps = 0f;
            var rtt = 0f;
            for (var v = 0; v < Viewers; v++)
            {
                Stream.SetViewerPosition(v, Interaction.Gaze(v + 1).Head);
                Stream.Plan(v);
                Stream.Encode(v);
                Stream.Decode(v, Rollback.LossRate(v + 1), ref _rng);
                mbps += Stream.SentMbps(v);
                rtt += Rollback.MeasuredRttMs(v + 1);
            }
            TotalMbps = mbps;
            _simMs = now + DtMs;

            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _rttS[h] = rtt / Viewers; _rbS[h] = Rollback.LastResim; _mbpsS[h] = mbps; _erleS[h] = Interaction.ErleDb; _warpS[h] = Rollback.WarpMm;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Telepresence" };
            _loop.Start();
            LastAction = "▶ 텔레프레전스 가동 · 60 Hz · GGPO 롤백 → 아이컨택/AEC/햅틱 → 옥트리 LoD 체적 스트리밍 × " + Viewers;
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Telepresence.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Telepresence.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
