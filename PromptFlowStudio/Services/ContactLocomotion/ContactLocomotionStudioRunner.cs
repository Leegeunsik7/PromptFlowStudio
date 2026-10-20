using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.ContactLocomotion;

/// <summary>
/// [PromptFlow Studio v17.35.0] 🪜 AI 접촉 로코모션 &amp; 계단 보행 스튜디오 러너.
/// 전용 v17.33 모션 매칭 러너(계단 지형 · 수동 입력)에 <see cref="ILocomotionStage"/> 로 결선: Begin → 발 배치 계획 · 스윙 합성 → 접지 락 → 재접지 · 발끝 롤링 · CoM 보정 입력
/// → IK → 무릎 가드 → 각운동량 · LIPM/DCM CoM 실현 → 동역학 측정 → 지표 · UI 스냅숏(앱 싱글턴 v17.33 러너 무간섭).
/// </summary>
public sealed class ContactLocomotionStudioRunner : ILocomotionStage, IDisposable
{
    public const int Trail = StairTraversalParkourMotionSynthesizer.Trail, Samples = ProceduralFootstepPlacementEngine.Samples;

    private readonly object _snap = new();
    private readonly Footstep[] _plan = new Footstep[ProceduralFootstepPlacementEngine.Steps];
    private readonly Vector3[] _trail = new Vector3[Trail], _pose = new Vector3[BipedSkeleton.Count];
    private readonly byte[] _trailSide = new byte[Trail];
    private readonly float[] _prof = new float[Samples], _edges = new float[ProceduralFootstepPlacementEngine.MaxEdges];
    private readonly Vector2[] _hull = new Vector2[9];
    private int _planN, _trailN, _edgeN, _hullN;
    private Vector3 _root, _scanOrigin;
    private float _heading, _speed = 1.2f, _headingDeg = 90f;
    private Vector2 _zmp, _zmpRef, _com;

    public ContactLocomotionStudioRunner(IServiceProvider sp) : this() { }

    public ContactLocomotionStudioRunner()
    {
        Motion = new NeuralMotionMatchingStudioRunner();
        Motion.SetTerrain(TerrainKind.Stairs);
        Motion.SetInput(_headingDeg, _speed);
        Motion.Stage = this;
        Motion.OnChange += Forward;
        LastAction = "📋 대기 · 계단 15 cm × 32 cm · 1.2 m/s";
    }

    public event Action? OnChange;

    public NeuralMotionMatchingStudioRunner Motion { get; }
    public ProceduralFootstepPlacementEngine Planner { get; } = new();
    public StairTraversalParkourMotionSynthesizer Synth { get; } = new();
    public ZmpDynamicStabilityAngularMomentumGuard Stability { get; } = new();
    public bool IsRunning => Motion.IsRunning;
    public long Tick => Motion.Tick;
    public double LastTickMs => Motion.LastTickMs;
    public double MaxTickMs => Motion.MaxTickMs;
    public float Speed => _speed;
    public float HeadingDeg => _headingDeg;
    public string? LastAction { get; private set; }

    // ══════════════════════ 단계 훅(모션 러너 잠금 내 · 무할당) ══════════════════════

    public void BeforeLock(NeuralMotionMatchingStudioRunner r)
    {
        Synth.Capture = Stability.Enabled ? Stability.CaptureOffset : Vector2.Zero;
        Synth.BeforeLock(r, Planner);
    }

    public void BeforeSolve(NeuralMotionMatchingStudioRunner r)
    {
        Synth.BeforeSolve(r, Planner);
        Stability.BeforeSolve(r.Ik);
    }

    public void AfterPost(NeuralMotionMatchingStudioRunner r) => Stability.AfterPost(r);

    public void AfterDynamics(NeuralMotionMatchingStudioRunner r)
    {
        Stability.Observe(r.Ik, r.TerrainField, r.Engine.Position, r.Engine.Heading, r.Engine.Contact);
        Synth.Observe(r);
        lock (_snap)
        {
            _planN = Planner.PlanCount;
            Planner.Planned.CopyTo(_plan);
            _trailN = Synth.TrailCount;
            for (var i = 0; i < _trailN; i++) _trail[i] = Synth.TrailPoint(i, out _trailSide[i]);
            r.Ik.Pose.CopyTo(_pose);
            _hullN = r.Ik.HullCount;
            for (var i = 0; i < _hullN; i++) _hull[i] = r.Ik.HullPoint(i);
            _root = r.Engine.Position; _heading = r.Engine.Heading; _scanOrigin = Planner.ScanOrigin;
            _zmp = new Vector2(r.Ik.Zmp.X, r.Ik.Zmp.Z); _zmpRef = Stability.Zmp; _com = new Vector2(r.Ik.CenterOfMass.X, r.Ik.CenterOfMass.Z);
            if (r.Tick % 6 == 0)
            {
                for (var i = 0; i < Samples; i++) _prof[i] = Planner.ProfileAt(i);
                _edgeN = Planner.EdgeCount;
                for (var i = 0; i < _edgeN; i++) _edges[i] = Planner.EdgeAlong(i);
            }
        }
    }

    // ══════════════════════ UI 스냅숏 ══════════════════════

    public int CopyPlan(Span<Footstep> dst) { lock (_snap) { _plan.AsSpan(0, _planN).CopyTo(dst); return _planN; } }
    public int CopyTrail(Span<Vector3> dst, Span<byte> side) { lock (_snap) { _trail.AsSpan(0, _trailN).CopyTo(dst); _trailSide.AsSpan(0, _trailN).CopyTo(side); return _trailN; } }
    public void CopyProfile(Span<float> dst) { lock (_snap) _prof.AsSpan().CopyTo(dst); }
    public int CopyEdges(Span<float> dst) { lock (_snap) { _edges.AsSpan(0, _edgeN).CopyTo(dst); return _edgeN; } }
    public int CopyHull(Span<Vector2> dst) { lock (_snap) { _hull.AsSpan(0, _hullN).CopyTo(dst); return _hullN; } }
    public void CopyPose(Span<Vector3> dst) { lock (_snap) _pose.AsSpan().CopyTo(dst); }
    public void Frame(out Vector3 root, out float heading, out Vector3 scanOrigin, out Vector2 zmp, out Vector2 zmpRef, out Vector2 com)
    {
        lock (_snap) { root = _root; heading = _heading; scanOrigin = _scanOrigin; zmp = _zmp; zmpRef = _zmpRef; com = _com; }
    }

    // ══════════════════════ 제어 ══════════════════════

    public void SetTerrain(TerrainKind k) { Motion.SetTerrain(k); Synth.Reset(); Stability.Reset(); LastAction = "🗺 지형 → " + NeuralMotionMatchingStudioRunner.TerrainName(k); Raise(); }

    public void SetWalk(float headingDeg, float speed)
    {
        _headingDeg = headingDeg; _speed = speed;
        Motion.SetInput(headingDeg, speed);
        LastAction = "🎮 방위 " + headingDeg.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "° · " + speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
        Raise();
    }

    public void SetStability(bool on) { Stability.Enabled = on; LastAction = on ? "⚖ ZMP 동적 안정 가드 ON(접지 창 · LIPM/DCM · CoM 실현)" : "⚖ ZMP 동적 안정 가드 OFF(v17.34 계단 가드만)"; Raise(); }
    public void SetAngular(bool on) { Stability.AngularMomentumEnabled = on; LastAction = on ? "🌀 각운동량 보존 ON(팔 요 상쇄 · 체간 피치)" : "🌀 각운동량 보존 OFF"; Raise(); }
    public void SetPlanner(bool on) { Synth.PlannerEnabled = on; LastAction = on ? "👣 발 배치 계획 ON(디딤판 모서리 회피)" : "👣 발 배치 계획 OFF(DB 착지 그대로)"; Raise(); }
    public void SetSpeedAdapt(bool on) { Synth.SpeedAdapt = on; LastAction = on ? "🐢 계단 속도 정합 ON(보폭 = 디딤판 깊이 · 한 칸/한 걸음)" : "🐢 계단 속도 정합 OFF(입력 속도 · 두 칸 보폭 가능)"; Raise(); }
    public void SetSynth(bool on) { Synth.Enabled = on; LastAction = on ? "🪜 계단 합성 ON(클리어런스 · 재접지 · 발끝 롤링)" : "🪜 계단 합성 OFF"; Raise(); }

    public void ResetMetrics()
    {
        Motion.ResetMetrics(); Planner.ResetMetrics(); Synth.ResetMetrics(); Stability.ResetMetrics();
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    public void Step() => Motion.Step();

    public void Start() { Motion.Start(); LastAction = "▶ 접촉 로코모션 60 Hz · " + NeuralMotionMatchingStudioRunner.TerrainName(Motion.Terrain); Raise(); }

    public void Stop() { Motion.Stop(); LastAction = "⏸ 정지"; Raise(); }

    private void Forward() => Raise();

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ContactLocomotion.Runner.OnChange", ex); }
    }

    public void Dispose() { Motion.OnChange -= Forward; Motion.Dispose(); }
}
