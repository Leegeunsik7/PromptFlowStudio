using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.DexterousHand;

/// <summary>
/// [PromptFlow Studio v17.13.0] 🖐️ AI 덱스터러스 핸드 &amp; 소품 접촉 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 합성 양손(관절 각 참값): 오른손 = 소품 접근 → 파지(표면 너머까지 굽힘 = 관통 의도) → 들어 휘두르기 → 내려놓기 → 해제 / 왼손 = 손가락 제스처 +
/// 주입 이벤트(검지 DIP 과신전 · 약지 PIP 측방 탈구 · 중지 교차) → 멀티뷰 투영 + 검출 노이즈/튐 · 손바닥 자가 가림 · 강제 가림(오른손 중지/약지 원위 1뷰) →
/// 42 키포인트 삼각측량(뼈 길이 복원) → 탈구/ROM/지간 충돌 가드 → 소품 접촉 매니폴드 · 관통 락 · 파지 부착.
/// </summary>
public sealed class DexterousHandStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, J = HandTopology.Keypoints, P = HandTopology.Points, CamW = 1280, CamH = 720;
    public const float Dt = 1f / Hz, GraspCycle = 8f, GestureCycle = 6f;
    public static readonly Vector3 GripWrist = new(0.16f, 1.06f, 0.08f), LeftWrist = new(-0.17f, 1.12f, 0.1f), Center = new(0f, 1.12f, 0.1f);
    public static readonly Vector3 GripOffset = new(0.085f, -0.03f, 0f);

    private readonly object _gate = new();
    private readonly Vector3[] _gt = new Vector3[J], _obs = new Vector3[J], _tmp = new Vector3[P];
    private readonly float[] _flex = new float[15], _abd = new float[5];
    private readonly PinholeCamera[] _cams = new PinholeCamera[DexterousFingerPoseTriangulationEngine.MaxViews];
    private readonly float[] _msS = new float[SeriesLen], _rawS = new float[SeriesLen], _finS = new float[SeriesLen], _penS = new float[SeriesLen], _resS = new float[SeriesLen], _forceS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private uint _rng = 0x1713u;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public DexterousHandStudioRunner(IServiceProvider sp)
        : this((DexterousFingerPoseTriangulationEngine?)sp.GetService(typeof(DexterousFingerPoseTriangulationEngine)) ?? new DexterousFingerPoseTriangulationEngine(),
               (InterDigitalSelfCollisionDislocationGuard?)sp.GetService(typeof(InterDigitalSelfCollisionDislocationGuard)) ?? new InterDigitalSelfCollisionDislocationGuard(),
               (VirtualPropContactGraspingSynthesizer?)sp.GetService(typeof(VirtualPropContactGraspingSynthesizer)) ?? new VirtualPropContactGraspingSynthesizer())
    {
    }

    public DexterousHandStudioRunner(DexterousFingerPoseTriangulationEngine engine, InterDigitalSelfCollisionDislocationGuard guard, VirtualPropContactGraspingSynthesizer prop)
    {
        Engine = engine; Guard = guard; Prop = prop;
        BuildCameras(6);
        PlaceProp();
        LastAction = "📋 대기 · 카메라 " + CameraCount + "대 · 42 키포인트 · 소품 " + Prop.PropName + " (" + Prop.PrimPath + ")";
    }

    public event Action? OnChange;

    public DexterousFingerPoseTriangulationEngine Engine { get; }
    public InterDigitalSelfCollisionDislocationGuard Guard { get; }
    public VirtualPropContactGraspingSynthesizer Prop { get; }
    public bool Motion { get; set; } = true;
    public int CameraCount { get; private set; }
    public float NoisePx { get; set; } = 1.2f;
    public float SpikeRate { get; set; } = 0.005f;
    public bool InjectHyperextension { get; set; } = true;
    public bool InjectDislocation { get; set; } = true;
    public bool InjectCrossing { get; set; } = true;
    public bool InjectOcclusion { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public string Phase { get; private set; } = "대기";
    public bool OcclusionActive { get; private set; }
    public bool HyperActive { get; private set; }
    public bool DislocActive { get; private set; }
    public bool CrossActive { get; private set; }
    public float MpjpeRawMm { get; private set; }
    public float MpjpeFinalMm { get; private set; }
    public Vector3 Truth(int k) => _gt[k];
    public Vector3 Final(int k) => Prop.Out(k);
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float MpjpeRawSeries(int i) => _rawS[Idx(i)];
    public float MpjpeFinalSeries(int i) => _finS[Idx(i)];
    public float RawPenSeries(int i) => _penS[Idx(i)];
    public float ResidualPenSeries(int i) => _resS[Idx(i)];
    public float ForceSeries(int i) => _forceS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // ── 제어 평면 ───────────────────────────────────────────

    private void BuildCameras(int n)
    {
        n = Math.Clamp(n, 2, DexterousFingerPoseTriangulationEngine.MaxViews);
        for (var v = 0; v < n; v++)
        {
            var a = MathF.Tau * v / n + 0.3f;
            var pos = Center + new Vector3(0.95f * MathF.Sin(a), v % 2 == 0 ? -0.3f : 0.55f, 0.95f * MathF.Cos(a));
            _cams[v] = PinholeCamera.LookAt(pos, Center, 48f, CamW, CamH);
        }
        CameraCount = n;
        Engine.Configure(_cams.AsSpan(0, n));
        Guard.Reset();
    }

    private void PlaceProp()
    {
        Array.Clear(_flex); Array.Clear(_abd);
        HandTopology.Pose(1, GripWrist, Quaternion.Identity, _flex, _abd, 1f, _tmp);
        Span<Vector3> both = stackalloc Vector3[J];
        _tmp.AsSpan().CopyTo(both.Slice(P));
        VirtualPropContactGraspingSynthesizer.GripPose(both, 1, GripOffset, out var pos, out var rot);
        Prop.RestPosition = pos; Prop.RestRotation = rot;
        Prop.ResetProp();
    }

    public void SetCameraCount(int n)
    {
        lock (_gate) BuildCameras(n);
        LastAction = "📷 카메라 " + CameraCount + "대 링 재배치(반경 0.95 m)";
        Raise();
    }

    public void ToggleCamera(int v)
    {
        lock (_gate) Engine.SetCameraEnabled(v, !Engine.CameraEnabled(v));
        LastAction = "📷 CAM " + (v + 1) + (Engine.CameraEnabled(v) ? " 활성" : " 비활성");
        Raise();
    }

    public void SetProp(PropKind k)
    {
        lock (_gate) { Prop.SetProp(k); PlaceProp(); }
        LastAction = "🗡 소품 → " + Prop.PropName + " · " + Prop.PrimPath + " · SDF 원시 " + Prop.ShapeCount;
        Raise();
    }

    public void SetBoneRecovery(bool on) { lock (_gate) Engine.BoneRecovery = on; LastAction = on ? "🦴 가림 뼈 길이 복원 ON" : "🦴 가림 복원 OFF"; Raise(); }
    public void SetRomGuard(bool on) { lock (_gate) Guard.RomGuard = on; LastAction = on ? "📐 ROM 클램프 ON" : "📐 ROM 클램프 OFF"; Raise(); }
    public void SetDislocationGuard(bool on) { lock (_gate) Guard.DislocationGuard = on; LastAction = on ? "🩹 탈구 복귀 ON" : "🩹 탈구 복귀 OFF"; Raise(); }
    public void SetCollisionGuard(bool on) { lock (_gate) Guard.CollisionGuard = on; LastAction = on ? "🤞 지간 충돌 분리 ON" : "🤞 지간 충돌 분리 OFF"; Raise(); }
    public void SetContactLock(bool on) { lock (_gate) Prop.ContactLock = on; LastAction = on ? "🔒 접촉 스티키 락 ON" : "🔓 접촉 스티키 락 OFF(투영만)"; Raise(); }
    public void ResetProp() { lock (_gate) Prop.ResetProp(); LastAction = "↺ 소품 거치대 복귀"; Raise(); }

    // ── 합성 양손 ───────────────────────────────────────────

    private float U()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private float Gauss() => (U() + U() + U() + U() - 2f) * 1.7320508f;

    private static float Ramp(float t, float a, float b) { var u = Math.Clamp((t - a) / (b - a), 0f, 1f); return u * u * (3f - 2f * u); }

    private void SetGrip(float c)
    {
        for (var f = 0; f < 5; f++)
        {
            if (f == 0) { _flex[0] = 25f * c; _flex[1] = 35f * c; _flex[2] = 30f * c; _abd[0] = -10f * c; continue; }
            _flex[f * 3] = 58f * c; _flex[f * 3 + 1] = 80f * c; _flex[f * 3 + 2] = 48f * c; _abd[f] = 0;
        }
    }

    private void BuildTruth()
    {
        var t = _time;
        // 오른손 파지 사이클
        var g = t % GraspCycle;
        Vector3 wrist; Quaternion rot;
        float close;
        if (g < 1.2f) { var a = Ramp(g, 0f, 1.2f); wrist = GripWrist + new Vector3(0, 0.16f * (1 - a), -0.05f * (1 - a)); close = 0f; Phase = "접근"; }
        else if (g < 1.9f) { wrist = GripWrist; close = Ramp(g, 1.2f, 1.9f) * 1.12f; Phase = "파지"; }
        else if (g < 5.0f) { var a = Ramp(g, 1.9f, 2.6f) * (1f - Ramp(g, 4.3f, 5.0f)); wrist = GripWrist + new Vector3(0.02f * MathF.Sin((g - 1.9f) * 2f), 0.22f * a, 0.04f * a * MathF.Sin((g - 1.9f) * 3f)); close = 1.12f; Phase = "들어 휘두르기"; }
        else if (g < 5.8f) { wrist = GripWrist; close = 1.12f; Phase = "내려놓기"; }
        else if (g < 6.4f) { wrist = GripWrist; close = 1.12f * (1f - Ramp(g, 5.8f, 6.4f)); Phase = "해제"; }
        else { var a = Ramp(g, 6.4f, 8f); wrist = GripWrist + new Vector3(0, 0.16f * a, -0.05f * a); close = 0f; Phase = "후퇴"; }
        var swing = g is > 1.9f and < 5f ? 0.6f * MathF.Sin((g - 1.9f) * 2.2f) * Ramp(g, 1.9f, 2.6f) * (1f - Ramp(g, 4.3f, 5f)) : 0f;
        rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, swing) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.25f * swing);
        // 파지 손목 회전 기준: 소품 오프셋이 손 좌표계라 손목 피벗 회전
        Array.Clear(_flex); Array.Clear(_abd);
        if (close > 0) SetGrip(close);
        HandTopology.Pose(1, wrist, rot, _flex, _abd, 1f, _tmp);
        _tmp.AsSpan().CopyTo(_gt.AsSpan(P));

        // 왼손 제스처
        var e = t % GestureCycle;
        Array.Clear(_flex); Array.Clear(_abd);
        for (var f = 0; f < 5; f++)
        {
            var w = 0.5f + 0.5f * MathF.Sin(t * 1.6f - f * 0.7f);
            if (f == 0) { _flex[0] = 15f * w; _flex[1] = 30f * w; _flex[2] = 35f * w; continue; }
            _flex[f * 3] = 50f * w; _flex[f * 3 + 1] = 70f * w; _flex[f * 3 + 2] = 40f * w;
            _abd[f] = (f - 2.5f) * 5f * (1f - w);
        }
        var lrot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f * MathF.Sin(t * 0.5f)) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f);
        var lw = LeftWrist + new Vector3(0.02f * MathF.Sin(t * 0.7f), 0.02f * MathF.Sin(t * 0.9f), 0);
        HandTopology.Pose(0, lw, lrot, _flex, _abd, 1f, _tmp);
        _tmp.AsSpan().CopyTo(_gt.AsSpan(0, P));
        _gt.AsSpan().CopyTo(_obs);

        // 주입: 관측만 왜곡(참값 = 해부학적 유효 포즈)
        HyperActive = InjectHyperextension && e is > 1f and < 2f;
        DislocActive = InjectDislocation && e is > 3f and < 3.6f;
        CrossActive = InjectCrossing && e is > 4.4f and < 5.4f;
        if (HyperActive || CrossActive)
        {
            if (HyperActive) _flex[1 * 3 + 2] = -65f;
            if (CrossActive) { _abd[2] = -38f; _flex[2 * 3] = _flex[1 * 3]; _flex[2 * 3 + 1] = _flex[1 * 3 + 1]; _flex[2 * 3 + 2] = _flex[1 * 3 + 2]; }
            HandTopology.Pose(0, lw, lrot, _flex, _abd, 1f, _tmp);
            _tmp.AsSpan().CopyTo(_obs.AsSpan(0, P));
        }
        if (DislocActive)
        {
            HandTopology.Frame(_obs, 0, out _, out _, out var z);
            _obs[15] -= z * 0.02f; _obs[16] -= z * 0.03f;
        }
    }

    private void Detect()
    {
        var g = _time % 7f;
        OcclusionActive = InjectOcclusion && g is > 3f and < 4f;
        HandTopology.Frame(_gt, 0, out _, out var y0, out _);
        HandTopology.Frame(_gt, 1, out _, out var y1, out _);
        for (var v = 0; v < CameraCount; v++)
        {
            ref readonly var cam = ref _cams[v];
            for (var k = 0; k < J; k++)
            {
                var h = k / P;
                var wrist = _gt[h * P];
                var y = h == 0 ? y0 : y1;
                if (!cam.Project(_obs[k], out var uv) || !cam.InFrame(uv)) { Engine.SetObservation(v, k, 0, 0, 0); continue; }
                var conf = 0.9f - 0.05f * U();
                // 손바닥 자가 가림: 카메라가 손등을 보고 관절이 손바닥 쪽 깊숙이
                if (Vector3.Dot(cam.Position - wrist, y) > 0 && Vector3.Dot(_obs[k] - wrist, y) < -0.03f) conf = 0.05f;
                var p = k % P;
                if (OcclusionActive && h == 1 && v != 0 && (p is 11 or 12 or 15 or 16)) conf = 0.05f;
                uv.X += NoisePx * Gauss(); uv.Y += NoisePx * Gauss();
                if (U() < SpikeRate) { uv.X += 40f + 60f * U(); uv.Y -= 40f + 60f * U(); conf = 0.8f; }
                Engine.SetObservation(v, k, uv.X, uv.Y, conf);
            }
        }
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("DexterousHand.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            BuildTruth();
            Detect();
            Engine.Solve();
            Guard.Process(Engine.Joints, Engine.ValidMask);
            Prop.Process(Guard.Output, Engine.ValidMask, Dt);
            float er = 0, ef = 0; int nr = 0, nf = 0;
            for (var k = 0; k < J; k++)
            {
                if (!Engine.Valid(k)) continue;
                er += Vector3.Distance(Engine.Joint(k), _gt[k]); nr++;
                ef += Vector3.Distance(Prop.Out(k), _gt[k]); nf++;
            }
            MpjpeRawMm = nr > 0 ? er * 1000f / nr : 0f;
            MpjpeFinalMm = nf > 0 ? ef * 1000f / nf : 0f;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _rawS[h] = MpjpeRawMm; _finS[h] = MpjpeFinalMm; _penS[h] = Prop.RawPenetrationMm; _resS[h] = Prop.ResidualPenetrationMm; _forceS[h] = Prop.TotalForceN;
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
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.DexterousHand" };
            _loop.Start();
            LastAction = "▶ 핸드 캡처 가동 · 60 Hz · " + CameraCount + "뷰 → 42 키포인트 SIMD DLT → 탈구/ROM/지간 가드 → " + Prop.PropName + " 접촉 락 · 파지";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 핸드 캡처 정지 · 파지 " + Prop.Grasps + " · 해제 " + Prop.Releases + " · 잔여 관통 " + F1(Prop.ResidualPenetrationMm) + " mm";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("DexterousHand.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("DexterousHand.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
