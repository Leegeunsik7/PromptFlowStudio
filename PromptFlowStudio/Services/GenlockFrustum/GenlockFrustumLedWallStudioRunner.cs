using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LensOptics;
using PromptFlowStudio.Services.StageLighting;
namespace PromptFlowStudio.Services.GenlockFrustum;

public enum TrackerMove : byte { Orbit, PushIn, Crane, Static }

/// <summary>
/// [PromptFlow Studio v17.44.0] 🖥️ AI ICVFX LED 월 관제 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 6DoF 트래커(오빗 · 푸시인 · 크레인 · 고정) → 렌즈 화각(v17.43 아나모픽 리그 → 수동/기본 35 mm) →
/// 패널별 오프액시스 투영 · 모아레/게인 맵 → v17.41 무대 픽스처 → HDRI 링버퍼(아우터) → 인너 시차 렌더 · 결합 →
/// PTP 젠록 위상 잠금 · 티어링/밴딩 가드 순으로 처리한다.
/// </summary>
public sealed class GenlockFrustumLedWallStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300, W = InnerOuterFrustumDualStreamSynthesizer.W, H = InnerOuterFrustumDualStreamSynthesizer.H, Px = InnerOuterFrustumDualStreamSynthesizer.Pixels;
    public const int BmpBytes = 54 + Px * 3;
    public const float Dt = 1f / Hz;
    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _phS = new float[Hist], _jitS = new float[Hist], _covS = new float[Hist];
    private readonly IntelligentStageLightingStudioRunner? _stage;
    private readonly CinematicLensOpticsStudioRunner? _lens;
    private TrackedCameraPose _pose;
    private LensPresetSpec? _lensSpec;
    private string _lensName = "";
    private int _head, _series;
    private float _time;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    public GenlockFrustumLedWallStudioRunner(IServiceProvider sp) : this()
    {
        _stage = sp.GetService(typeof(IntelligentStageLightingStudioRunner)) as IntelligentStageLightingStudioRunner;
        _lens = sp.GetService(typeof(CinematicLensOpticsStudioRunner)) as CinematicLensOpticsStudioRunner;
    }
    public GenlockFrustumLedWallStudioRunner() { }
    public event Action? OnChange;
    public RealtimeOffAxisFrustumProjectionEngine Projection { get; } = new();
    public InnerOuterFrustumDualStreamSynthesizer Synth { get; } = new();
    public PtpGenlockRollingShutterTearingGuard Genlock { get; } = new();
    public TrackerMove Move { get; private set; } = TrackerMove.Orbit;
    /// <summary>0 = 자동(v17.43 렌즈 리그 · 기본 35 mm).</summary>
    public float FocalOverride { get; private set; }
    public float FocusOverride { get; private set; }
    public string LensSource { get; private set; } = "기본 35 mm";
    public ref readonly TrackedCameraPose Pose => ref _pose;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public bool StageLinked => _stage is not null;
    public bool LensLinked => _lens is not null;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float TickMsSeries(int i) => _msS[Ix(i)];
    public float PhaseSeries(int i) => _phS[Ix(i)];
    public float JitterSeries(int i) => _jitS[Ix(i)];
    public float CoverageSeries(int i) => _covS[Ix(i)];
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    public ReadOnlySpan<byte> Display => Synth.Display;
    public static string MoveName(TrackerMove m) => m switch { TrackerMove.Orbit => "오빗 달리", TrackerMove.PushIn => "푸시 인", TrackerMove.Crane => "크레인", _ => "고정" };
    // ══════════════════════ 제어 ══════════════════════
    public void SetMove(TrackerMove m)
    {
        lock (_gate) Move = m;
        LastAction = "🎥 트래커 " + MoveName(m);
        Raise();
    }
    public void SetFocal(float mm)
    {
        lock (_gate) FocalOverride = mm <= 0f ? 0f : Math.Clamp(mm, 12f, 200f);
        LastAction = FocalOverride > 0f ? "🔍 초점 거리 " + FocalOverride.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " mm" : "🔍 초점 거리 자동";
        Raise();
    }
    public void SetFocus(float m)
    {
        lock (_gate) FocusOverride = m <= 0f ? 0f : Math.Clamp(m, 0.5f, 50f);
        LastAction = FocusOverride > 0f ? "◎ 초점 " + FocusOverride.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m" : "◎ 초점 자동";
        Raise();
    }
    public void SetGenlock(bool on) { lock (_gate) Genlock.GenlockEnabled = on; LastAction = on ? "🔒 PTP 젠록 ON" : "🔓 젠록 OFF(프리런)"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Genlock.GuardEnabled = on; LastAction = on ? "🛡 티어링 가드 ON" : "🛡 티어링 가드 OFF"; Raise(); }
    public void SetCalibration(bool on) { lock (_gate) Synth.Calibrated = on; LastAction = on ? "🎨 컬러 캘리브레이션 ON" : "🎨 캘리브레이션 OFF"; Raise(); }
    public void SetMoireBlur(bool on) { lock (_gate) Synth.MoireBlur = on; LastAction = on ? "〰 모아레 블러 ON" : "〰 모아레 블러 OFF"; Raise(); }
    public void SetShutter(double deg) { lock (_gate) Genlock.ShutterAngle = Math.Clamp(deg, 45.0, 358.0); Raise(); }
    /// <summary>디스플레이 23.976 ↔ 카메라 24 불일치 시나리오 토글.</summary>
    public void SetRateMismatch(bool on)
    {
        lock (_gate) Genlock.DisplayFps = on ? 24000.0 / 1001.0 : Genlock.CameraFps;
        LastAction = on ? "⚠ 23.976 ↔ 24 불일치" : "✓ 프레임레이트 일치";
        Raise();
    }
    public void Spike(double ns = 8000.0)
    {
        lock (_gate) Genlock.InjectSpike(ns);
        LastAction = "⚡ PTP 경로 스파이크 " + (ns / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " µs";
        Raise();
    }
    public void ResetMetrics()
    {
        lock (_gate) { Genlock.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }
    /// <summary>UI 뷰어 — 24-bit BMP(하단부터 BGR) 를 호출자 버퍼에 기록.</summary>
    public void WriteBmp(Span<byte> dst)
    {
        dst[..54].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[2..], BmpBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[10..], 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[18..], W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[22..], H);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[26..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[28..], 24);
        lock (_gate)
        {
            var src = Synth.Display;
            for (var y = 0; y < H; y++)
            {
                var s = (H - 1 - y) * W * 3;
                var d = 54 + y * W * 3;
                for (var x = 0; x < W; x++)
                {
                    dst[d + x * 3] = src[s + x * 3 + 2]; dst[d + x * 3 + 1] = src[s + x * 3 + 1]; dst[d + x * 3 + 2] = src[s + x * 3];
                }
            }
        }
    }
    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("GenlockFrustum.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _time += Dt;
            Track();
            Projection.Compute(in _pose, Synth.Overscan, Synth.Feather);
            Gather();
            Synth.BuildOuter();
            Synth.Compose(Projection);
            Genlock.Advance(Dt);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_head] = ms; _phS[_head] = (float)Genlock.PhaseErrNs; _jitS[_head] = (float)Genlock.JitterRmsNs; _covS[_head] = Synth.InnerCoverage;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }
    /// <summary>6DoF 트래커 포즈(위치 + 요/피치/롤) · 렌즈 화각.</summary>
    private void Track()
    {
        var t = _time;
        Vector3 pos; float yaw, pitch, roll;
        switch (Move)
        {
            case TrackerMove.Orbit:
                var a = 0.45f * MathF.Sin(0.25f * t);
                pos = new Vector3(2.2f * MathF.Sin(a), 1.65f, 1.6f + 0.8f * MathF.Cos(a));
                yaw = 0.6f * a; pitch = -0.03f; roll = 0.015f * MathF.Sin(0.7f * t);
                break;
            case TrackerMove.PushIn:
                pos = new Vector3(0.3f, 1.6f, 3.5f - 2.5f * (0.5f + 0.5f * MathF.Sin(0.2f * t)));
                yaw = 0.05f * MathF.Sin(0.3f * t); pitch = 0f; roll = 0f;
                break;
            case TrackerMove.Crane:
                pos = new Vector3(-0.8f + 0.6f * MathF.Sin(0.15f * t), 1.2f + 1.6f * (0.5f + 0.5f * MathF.Sin(0.3f * t)), 2f);
                yaw = -0.15f + 0.2f * MathF.Sin(0.15f * t); pitch = -0.12f * (0.5f + 0.5f * MathF.Sin(0.3f * t)); roll = 0f;
                break;
            default:
                pos = new Vector3(0f, 1.7f, 2f); yaw = 0f; pitch = 0f; roll = 0f;
                break;
        }
        var cp = MathF.Cos(pitch);
        var fwd = new Vector3(MathF.Sin(yaw) * cp, MathF.Sin(pitch), -MathF.Cos(yaw) * cp);
        var r0 = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var u0 = Vector3.Cross(r0, fwd);
        _pose.Position = pos; _pose.Forward = fwd;
        _pose.Right = r0 * MathF.Cos(roll) + u0 * MathF.Sin(roll);
        _pose.Up = u0 * MathF.Cos(roll) - r0 * MathF.Sin(roll);

        float focal = 35f, tstop = 2.8f, focus = 3f, squeeze = 1f;
        var lensLive = _lens is not null && _lens.Lens.VFovDeg > 0f;
        if (lensLive)
        {
            var l = _lens!.Lens;
            squeeze = l.Squeeze; tstop = l.TStop; focus = l.FocusM;
            if (FocalOverride <= 0f)
            {
                _pose.VFovDeg = l.EffectiveVFovDeg;
                _pose.HFovDeg = 2f * MathF.Atan(MathF.Tan(l.HFovDeg * (MathF.PI / 360f)) * l.BreathScale) * (180f / MathF.PI);
                _pose.FocalMm = l.FocalMm;
                if (!ReferenceEquals(_lensSpec, l.Spec)) { _lensSpec = l.Spec; _lensName = "v17.43 " + l.Spec.Name; }
                LensSource = _lensName;
            }
        }
        if (!lensLive || FocalOverride > 0f)
        {
            if (FocalOverride > 0f) focal = FocalOverride;
            _pose.VFovDeg = 2f * MathF.Atan(RealtimeAnamorphicLensOpticsEngine.SensorH / (2f * focal)) * (180f / MathF.PI);
            _pose.HFovDeg = 2f * MathF.Atan(squeeze * RealtimeAnamorphicLensOpticsEngine.SensorW / (2f * focal)) * (180f / MathF.PI);
            _pose.FocalMm = focal;
            LensSource = FocalOverride > 0f ? (lensLive ? "수동 + v17.43 스퀴즈" : "수동") : "기본 35 mm";
        }
        _pose.TStop = tstop;
        _pose.FocusM = FocusOverride > 0f ? FocusOverride : focus;
    }
    private void Gather()
    {
        Synth.BeginLights();
        if (_stage is null) return;
        var e = _stage.Engine;
        for (var i = 0; i < RealtimeIntelligentStageLightingEngine.Fixtures; i++)
        {
            ref readonly var fx = ref e.Fixture(i);
            if (fx.Type == FixtureType.Hazer) continue;
            Synth.AddLight(fx.Position, fx.Color, e.OutLevel(i));
        }
    }
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.GenlockFrustum" };
            _loop.Start();
        }
        LastAction = "▶ ICVFX LED 월 60 Hz · " + MoveName(Move);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("GenlockFrustum.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("GenlockFrustum.Runner.OnChange", ex); }
    }
    public void Dispose() => Stop();
}
