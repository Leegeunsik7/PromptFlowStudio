using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.MarkerlessMocap;

/// <summary>
/// [PromptFlow Studio v17.11.0] 🏃 AI 무마커 모션 캡처 &amp; 리타게팅 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 합성 연기자(v16.75 참값) → 이벤트 주입(팔 몸통 관통 · 무릎 과신전) → V 카메라 투영 + 2D 검출 모사(가우스 노이즈 · 튐 · 가림 좌우 스왑 · 손목 소실) →
/// 에피폴라 게이트 + SIMD DLT 삼각측량 → 튐/떨림/ROM/관통 가드 → 대상 리그 리타게팅 + 발 고정 FABRIK.
/// </summary>
public sealed class MarkerlessMocapStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, J = MocapSkeleton.Body, CamW = 1280, CamH = 720;
    public const float Dt = 1f / Hz;

    private readonly object _gate = new();
    private readonly Vector3[] _kp = new Vector3[MocapSkeleton.Keypoints], _gt = new Vector3[J], _obs = new Vector3[J], _offsets = new Vector3[J];
    private readonly float[] _face = new float[FaceShapes.Count];
    private readonly Quaternion[] _lq = new Quaternion[J], _gq = new Quaternion[J];
    private readonly PinholeCamera[] _cams = new PinholeCamera[MarkerlessMultiViewPoseTriangulationEngine.MaxViews];
    private readonly float[] _msS = new float[SeriesLen], _repS = new float[SeriesLen], _rawS = new float[SeriesLen], _grdS = new float[SeriesLen],
        _slideRawS = new float[SeriesLen], _slideS = new float[SeriesLen], _jitInS = new float[SeriesLen], _jitOutS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private uint _rng = 0x9E3779B9u;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public MarkerlessMocapStudioRunner(IServiceProvider sp)
        : this((MarkerlessMultiViewPoseTriangulationEngine?)sp.GetService(typeof(MarkerlessMultiViewPoseTriangulationEngine)) ?? new MarkerlessMultiViewPoseTriangulationEngine(),
               (KinematicJointLimitBonePenetrationGuard?)sp.GetService(typeof(KinematicJointLimitBonePenetrationGuard)) ?? new KinematicJointLimitBonePenetrationGuard(),
               (BiomechanicalSkeletonRetargetingSynthesizer?)sp.GetService(typeof(BiomechanicalSkeletonRetargetingSynthesizer)) ?? new BiomechanicalSkeletonRetargetingSynthesizer())
    {
    }

    public MarkerlessMocapStudioRunner(MarkerlessMultiViewPoseTriangulationEngine engine, KinematicJointLimitBonePenetrationGuard guard, BiomechanicalSkeletonRetargetingSynthesizer retarget)
    {
        Engine = engine; Guard = guard; Retarget = retarget;
        BuildCameras(4);
        LastAction = "📋 대기 · 카메라 " + CameraCount + "대 링 · " + CamW + "×" + CamH + " · 대상 " + Retarget.Rig.Name;
    }

    public event Action? OnChange;

    public MarkerlessMultiViewPoseTriangulationEngine Engine { get; }
    public KinematicJointLimitBonePenetrationGuard Guard { get; }
    public BiomechanicalSkeletonRetargetingSynthesizer Retarget { get; }
    public bool Motion { get; set; } = true;
    public int CameraCount { get; private set; }
    /// <summary>2D 검출 노이즈 σ(px).</summary>
    public float NoisePx { get; set; } = 1.5f;
    /// <summary>관측당 오검출 튐 확률.</summary>
    public float SpikeRate { get; set; } = 0.01f;
    public bool InjectOcclusion { get; set; } = true;
    public bool InjectPenetration { get; set; } = true;
    public bool InjectRom { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public float MpjpeRawMm { get; private set; }
    public float MpjpeGuardMm { get; private set; }
    public bool OcclusionActive { get; private set; }
    public float PenetrationEvent { get; private set; }
    public float RomEvent { get; private set; }
    public Vector3 Truth(int j) => _gt[j];
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float ReprojSeries(int i) => _repS[Idx(i)];
    public float MpjpeRawSeries(int i) => _rawS[Idx(i)];
    public float MpjpeGuardSeries(int i) => _grdS[Idx(i)];
    public float SlideRawSeries(int i) => _slideRawS[Idx(i)];
    public float SlideSeries(int i) => _slideS[Idx(i)];
    public float JitterInSeries(int i) => _jitInS[Idx(i)];
    public float JitterOutSeries(int i) => _jitOutS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // ── 카메라 리그(제어 평면) ───────────────────────────────

    private void BuildCameras(int n)
    {
        n = Math.Clamp(n, 2, MarkerlessMultiViewPoseTriangulationEngine.MaxViews);
        for (var v = 0; v < n; v++)
        {
            var a = MathF.Tau * v / n + 0.35f;
            var pos = new Vector3(3.4f * MathF.Sin(a), v % 2 == 0 ? 1.4f : 2.3f, 3.4f * MathF.Cos(a));
            _cams[v] = PinholeCamera.LookAt(pos, new Vector3(0, 0.95f, 0), 52f, CamW, CamH);
        }
        CameraCount = n;
        Engine.Configure(_cams.AsSpan(0, n));
        Guard.Reset();
        Retarget.ResetContacts();
    }

    public void SetCameraCount(int n)
    {
        lock (_gate) BuildCameras(n);
        LastAction = "📷 카메라 " + CameraCount + "대 링 재배치(반경 3.4 m · 높이 1.4/2.3 m)";
        Raise();
    }

    public void ToggleCamera(int v)
    {
        lock (_gate) Engine.SetCameraEnabled(v, !Engine.CameraEnabled(v));
        LastAction = "📷 CAM " + (v + 1) + (Engine.CameraEnabled(v) ? " 활성" : " 비활성(피드 차단)");
        Raise();
    }

    public void SetEpipolarGate(bool on) { lock (_gate) Engine.EpipolarGate = on; LastAction = on ? "📐 에피폴라 게이트 ON" : "📐 에피폴라 게이트 OFF — 모든 관측 DLT 투입"; Raise(); }
    public void SetSpikeGate(bool on) { lock (_gate) Guard.SpikeGate = on; LastAction = on ? "⚡ 튐/가림 게이트 ON" : "⚡ 튐/가림 게이트 OFF"; Raise(); }
    public void SetRomGuard(bool on) { lock (_gate) Guard.RomGuard = on; LastAction = on ? "🦴 ROM 가동 범위 락 ON" : "🦴 ROM 락 OFF"; Raise(); }
    public void SetPenetrationGuard(bool on) { lock (_gate) Guard.PenetrationGuard = on; LastAction = on ? "🛡 자가 관통 차단 ON" : "🛡 자가 관통 차단 OFF"; Raise(); }
    public void SetFootLock(bool on) { lock (_gate) Retarget.FootLock = on; LastAction = on ? "🦶 발 고정 FABRIK ON" : "🦶 발 고정 OFF — 발 밀림 노출"; Raise(); }
    public void SetGroundClamp(bool on) { lock (_gate) Retarget.GroundClamp = on; LastAction = on ? "⬛ 지면 클램프 ON" : "⬛ 지면 클램프 OFF"; Raise(); }

    public void SetSmoothing(float s)
    {
        lock (_gate) Guard.Smoothing = Math.Clamp(s, 0f, 1f);
        LastAction = "🎚 떨림 억제 " + F2(Guard.Smoothing) + " · One Euro fmin " + F2(Guard.MinCutoffHz) + " Hz";
        Raise();
    }

    public void SetPreset(MocapRigPreset p)
    {
        lock (_gate) Retarget.SetPreset(p);
        LastAction = "🧍 대상 리그 → " + Retarget.Rig.Name + " · 골반 " + F2(Retarget.Rig.PelvisHeight) + " m";
        Raise();
    }

    // ── 검출 모사 ───────────────────────────────────────────

    private float Uniform()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private float Gauss() => (Uniform() + Uniform() + Uniform() + Uniform() - 2f) * 1.7320508f;

    private static float Smooth(float t, float period, float on, float len)
    {
        var p = t % period - on;
        if (p < 0 || p > len) return 0f;
        var e = MathF.Min(p, len - p) / 0.4f;
        return e >= 1f ? 1f : e * e * (3f - 2f * e);
    }

    /// <summary>참값 + 주입 이벤트 → 관측 3D(무할당).</summary>
    private void BuildObserved()
    {
        SyntheticPerformer.Pose(_time, 1f, _kp, _face, _lq, _gq, _offsets);
        for (var j = 0; j < J; j++) _gt[j] = _obs[j] = _kp[j];
        PenetrationEvent = InjectPenetration ? Smooth(_time, 7f, 2f, 2.2f) : 0f;
        if (PenetrationEvent > 0)
        {
            var w = PenetrationEvent;
            var c = Vector3.Lerp(_gt[MocapSkeleton.Spine2], _gt[MocapSkeleton.Spine3], 0.5f) + new Vector3(-0.02f, 0, 0.02f);
            var dw = (c - _gt[MocapSkeleton.LWrist]) * w;
            _obs[MocapSkeleton.LWrist] += dw; _obs[MocapSkeleton.LHand] += dw;
            _obs[MocapSkeleton.LElbow] += (Vector3.Lerp(_gt[MocapSkeleton.LShoulder], c, 0.55f) + new Vector3(0, 0, 0.05f) - _gt[MocapSkeleton.LElbow]) * w;
        }
        RomEvent = InjectRom ? Smooth(_time, 5f, 0.5f, 1.6f) : 0f;
        if (RomEvent > 0) _obs[MocapSkeleton.RKnee] += new Vector3(0, 0, -0.09f * RomEvent);
    }

    private void Detect()
    {
        OcclusionActive = InjectOcclusion && _time % 4f > 2.4f;
        var n = CameraCount;
        for (var v = 0; v < n; v++)
        {
            ref readonly var cam = ref _cams[v];
            for (var j = 0; j < J; j++)
            {
                var src = j;
                if (OcclusionActive && v == 0)
                {
                    // 가림 → 좌우 다리 라벨 스왑(고신뢰 오검출)
                    if (j is MocapSkeleton.LKnee or MocapSkeleton.LAnkle or MocapSkeleton.LFoot) src = j + 1;
                    else if (j is MocapSkeleton.RKnee or MocapSkeleton.RAnkle or MocapSkeleton.RFoot) src = j - 1;
                }
                if (!cam.Project(_obs[src], out var uv) || !cam.InFrame(uv)) { Engine.SetObservation(v, j, 0, 0, 0); continue; }
                var conf = 0.92f - 0.05f * Uniform();
                if (OcclusionActive && v == 1 % n && j is MocapSkeleton.LWrist or MocapSkeleton.LHand or MocapSkeleton.RWrist or MocapSkeleton.RHand) conf = 0.05f;
                uv.X += NoisePx * Gauss(); uv.Y += NoisePx * Gauss();
                if (Uniform() < SpikeRate)
                {
                    var a = MathF.Tau * Uniform();
                    var r = 60f + 90f * Uniform();
                    uv.X += r * MathF.Cos(a); uv.Y += r * MathF.Sin(a);
                    conf = 0.85f;
                }
                Engine.SetObservation(v, j, uv.X, uv.Y, conf);
            }
        }
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("MarkerlessMocap.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            BuildObserved();
            Detect();
            Engine.Solve();
            Guard.Process(Engine.Joints, Engine.ValidMask, Dt);
            Retarget.Retarget(Guard.Output, Guard.Scale, Dt);
            float er = 0, eg = 0; var nr = 0;
            for (var j = 0; j < J; j++)
            {
                if (Engine.Valid(j)) { er += Vector3.Distance(Engine.Joint(j), _gt[j]); nr++; }
                eg += Vector3.Distance(Guard.Out(j), _gt[j]);
            }
            MpjpeRawMm = nr > 0 ? er * 1000f / nr : 0f;
            MpjpeGuardMm = eg * 1000f / J;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _repS[h] = Engine.MeanReprojPx; _rawS[h] = MpjpeRawMm; _grdS[h] = MpjpeGuardMm;
            _slideRawS[h] = Retarget.FootSlideRawMmS; _slideS[h] = Retarget.FootSlideMmS; _jitInS[h] = Guard.JitterInMm; _jitOutS[h] = Guard.JitterOutMm;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.MarkerlessMocap" };
            _loop.Start();
            LastAction = "▶ 캡처 가동 · 60 Hz · " + CameraCount + "뷰 2D 검출 → 에피폴라 게이트 + SIMD DLT → 튐/떨림/ROM/관통 가드 → " + Retarget.Rig.Name + " 리타게팅 + 발 고정";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 캡처 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("MarkerlessMocap.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MarkerlessMocap.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
