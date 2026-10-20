using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.NeuralRagdoll;

/// <summary>
/// [PromptFlow Studio v17.37.0] 🤸 AI 신경망 래그돌 &amp; 충격 복원 스튜디오 러너(60 Hz 전용 스레드).
/// 전용 v17.33 모션 매칭 러너(평지 · 무할당 조향 원형 보행)를 매 프레임 직접 구동 → 18 → 24 관절 매핑 → 회복 합성기(상태 머신 · 기립 키포즈 · 지지 반발력)
/// → 하이브리드 래그돌 엔진(전환 가중 · 모터 PD · XPBD) + 특이점/에너지 가드 → UI 스냅숏(잠금 복사) 순으로 처리한다.
/// 충격 인젝터(부위 · N·s · 방위) · 자동 충격 데모 · 강성/가드 토글.
/// </summary>
public sealed class NeuralRagdollStudioRunner : IDisposable
{
    public const int Hz = 60, N = RagdollSkeleton.Count, Series = 240, Supports = FullBodyImpactFallAndRecoverySynthesizer.SupportCount;
    public const float Dt = 1f / Hz, TurnDegPerSec = 20f, AutoPeriod = 6f;

    private readonly object _gate = new();
    private readonly Vector3[] _kin = new Vector3[N], _out = new Vector3[N], _tgt = new Vector3[N], _snapOut = new Vector3[N], _snapTgt = new Vector3[N];
    private readonly float[] _snapW = new float[N], _snapF = new float[Supports];
    private readonly bool[] _snapG = new bool[N];
    private readonly float[] _keS = new float[Series], _wS = new float[Series], _omS = new float[Series], _msS = new float[Series];
    private int _head, _count, _auto;
    private float _heading, _speed, _walk = 1.2f, _autoT;
    private Vector3 _snapCom;
    private Vector2 _snapZmp;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public NeuralRagdollStudioRunner(IServiceProvider sp) : this() { }

    public NeuralRagdollStudioRunner()
    {
        Motion = new NeuralMotionMatchingStudioRunner();
        Motion.Steer(0f, 0f);
        Engine.Guard = Guard;
        Snapshot();
        LastAction = "📋 대기 · 24 관절 캡슐 래그돌 · 보행 " + _walk.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
    }

    public event Action? OnChange;

    public NeuralMotionMatchingStudioRunner Motion { get; }
    public RealtimeHybridNeuralRagdollEngine Engine { get; } = new();
    public FullBodyImpactFallAndRecoverySynthesizer Recovery { get; } = new();
    public JointSingularityEnergyDivergenceGuard Guard { get; } = new();
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public bool AutoImpulse { get; private set; }
    public float WalkSpeed => _walk;
    public float Heading => _heading;
    public int SeriesCount => _count;
    private int Idx(int i) => (_head - 1 - i + Series * 2) % Series;
    public float KineticSeries(int i) => _keS[Idx(i)];
    public float WeightSeries(int i) => _wS[Idx(i)];
    public float OmegaSeries(int i) => _omS[Idx(i)];
    public float TickMsSeries(int i) => _msS[Idx(i)];

    // ══════════════════════ UI 스냅숏 ══════════════════════

    public void CopyPose(Span<Vector3> dst) { lock (_gate) _snapOut.AsSpan().CopyTo(dst); }
    public void CopyTarget(Span<Vector3> dst) { lock (_gate) _snapTgt.AsSpan().CopyTo(dst); }
    public void CopyWeights(Span<float> dst, Span<bool> grounded) { lock (_gate) { _snapW.AsSpan().CopyTo(dst); _snapG.AsSpan().CopyTo(grounded); } }
    public void CopySupport(Span<float> dst) { lock (_gate) _snapF.AsSpan().CopyTo(dst); }
    public Vector3 ComPoint { get { lock (_gate) return _snapCom; } }
    public Vector2 ZmpPoint { get { lock (_gate) return _snapZmp; } }

    // ══════════════════════ 제어 ══════════════════════

    /// <summary>외력 충격(부위 · 크기 N·s · 방위 ° = 캐릭터 정면 기준 0 = 뒤에서 앞으로 밀기 · 고도 °).</summary>
    public float InjectImpulse(RagdollJoint part, float magnitudeNs, float azimuthDeg, float elevationDeg = 10f)
    {
        float e;
        lock (_gate)
        {
            var yaw = Motion.Engine.Heading + azimuthDeg / 57.29578f;
            var el = elevationDeg / 57.29578f;
            var dir = BipedSkeleton.Forward(yaw) * MathF.Cos(el) + Vector3.UnitY * MathF.Sin(el);
            e = Engine.ApplyImpulse((int)part, dir * Math.Clamp(magnitudeNs, 0f, 2000f));
            Recovery.OnImpulse(Engine, e);
        }
        LastAction = "💥 충격 " + RagdollSkeleton.Name(part) + " · " + magnitudeNs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " N·s · 방위 " + azimuthDeg.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "° · 에너지 " + e.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " J";
        Raise();
        return e;
    }

    public void SetStiffness(float s) { lock (_gate) Engine.Stiffness = Math.Clamp(s, 0.2f, 2f); LastAction = "💪 전신 모터 강성 × " + Engine.Stiffness.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 특이점 · 각속도 · 해밀토니안 가드 ON" : "⛔ 가드 OFF(관절 꼬임 · 각속도 폭주 비교)"; Raise(); }
    public void SetAutoImpulse(bool on) { lock (_gate) { AutoImpulse = on; _autoT = 0f; } LastAction = on ? "🤖 자동 충격 데모 ON(6 s 주기 · 타격/밀침 순환)" : "🤖 자동 충격 OFF"; Raise(); }
    public void SetWalk(float speed) { lock (_gate) _walk = Math.Clamp(speed, 0f, 2.5f); LastAction = "🚶 보행 " + _walk.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s"; Raise(); }

    public void ResetMetrics()
    {
        lock (_gate) { Engine.ResetMetrics(); Recovery.ResetMetrics(); Guard.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    public void ResetCharacter()
    {
        lock (_gate)
        {
            _heading = 0f; _speed = 0f;
            Motion.Teleport(Vector3.Zero, 0f);
            Recovery.Reset();
            Engine.Floor = 0f;
            Span<Vector3> b = stackalloc Vector3[BipedSkeleton.Count];
            BipedSkeleton.Rest(b);
            RagdollSkeleton.MapBiped(b, _kin);
            Engine.Reset(_kin);
        }
        LastAction = "⟲ 캐릭터 원점 복귀";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("NeuralRagdoll.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            if (Recovery.ConsumeTeleport(out var origin, out var yaw)) { Motion.Teleport(origin, yaw); _heading = yaw; _speed = 0f; }
            var loco = Recovery.State == RecoveryState.Locomotion;
            if (loco) { _heading = BipedSkeleton.WrapPi(_heading + TurnDegPerSec / 57.29578f * Dt * (_walk > 0.05f ? 1f : 0f)); _speed = MathF.Min(_walk, _speed + 1.5f * Dt); }
            else _speed = 0f;
            Motion.Steer(_heading, _speed);
            Motion.Step();
            RagdollSkeleton.MapBiped(Motion.Ik.Pose, _kin);
            if (AutoImpulse && loco)
            {
                _autoT += Dt;
                if (_autoT >= AutoPeriod)
                {
                    _autoT = 0f; _auto++;
                    var k = _auto % 3;
                    var part = k == 0 ? RagdollJoint.Head : RagdollJoint.Chest;
                    var mag = k == 0 ? 18f : 160f;
                    var az = k == 1 ? 0f : k == 2 ? 180f : 90f;
                    var yw = Motion.Engine.Heading + az / 57.29578f;
                    var e = Engine.ApplyImpulse((int)part, (BipedSkeleton.Forward(yw) * 0.985f + Vector3.UnitY * 0.17f) * mag);
                    Recovery.OnImpulse(Engine, e);
                }
            }
            Recovery.Update(Engine, _kin, Dt);
            Engine.SetTarget(Recovery.Target, Dt);
            Engine.Step(Dt);
            Snapshot();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _head;
            _keS[h] = Engine.KineticEnergy; _wS[h] = Engine.MeanWeight; _omS[h] = Guard.PeakOmega; _msS[h] = ms;
            _head = (h + 1) % Series;
            if (_count < Series) _count++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private void Snapshot()
    {
        for (var j = 0; j < N; j++) { _snapOut[j] = Engine.Output(j); _snapTgt[j] = Engine.Target(j); _snapW[j] = Engine.Weight(j); _snapG[j] = Engine.Grounded(j); }
        for (var i = 0; i < Supports; i++) _snapF[i] = Recovery.SupportForce(i);
        _snapCom = Engine.CenterOfMass; _snapZmp = Recovery.Zmp;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.NeuralRagdoll" };
            _loop.Start();
        }
        LastAction = "▶ 신경망 래그돌 60 Hz · 서브스텝 4 × 반복 4";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("NeuralRagdoll.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("NeuralRagdoll.Runner.OnChange", ex); }
    }

    public void Dispose() { Stop(); Motion.Dispose(); }
}
