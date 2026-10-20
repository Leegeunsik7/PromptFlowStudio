using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Microclimate;

namespace PromptFlowStudio.Services.MotionMatching;

/// <summary>
/// [PromptFlow Studio v17.33.0] 🏃 AI 뉴럴 모션 매칭 &amp; 생체역학 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 자동 조종(보행 · 선회 · 질주 · 급반전 · 정지 · 제자리 회전 프로그램) 또는 수동 입력 → 궤적 컨트롤러(노면 μ 적응) → 모션 매칭 → 가드 뼈 길이/ROM
/// → 지형 정렬 → 접지 락 → 골반/ZMP/FABRIK → 무릎 과신전·각속도 가드 → 동역학(ZMP · 접지력) 순으로 처리한다.
/// </summary>
/// <summary>v17.35 접촉 로코모션 단계 훅(러너 잠금 내 · 무할당 구현 계약).</summary>
public interface ILocomotionStage
{
    /// <summary>지형 정렬(Begin) 후 · 접지 락 전 — 스윙 발 목표 수정.</summary>
    void BeforeLock(NeuralMotionMatchingStudioRunner r);
    /// <summary>접지 락 후 · IK 해 전 — 접지 발 재정렬 · CoM 보정 입력.</summary>
    void BeforeSolve(NeuralMotionMatchingStudioRunner r);
    /// <summary>무릎 가드 후 · 동역학 측정 전 — 최종 자세 CoM 제어.</summary>
    void AfterPost(NeuralMotionMatchingStudioRunner r);
    /// <summary>동역학 측정 후 — 지표.</summary>
    void AfterDynamics(NeuralMotionMatchingStudioRunner r);
}

public sealed class NeuralMotionMatchingStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, Count = BipedSkeleton.Count, ArcPoints = 12, Grid = 15;
    public const float Dt = 1f / Hz, GridStep = 0.5f;

    private readonly record struct Segment(float Seconds, float Speed, float TurnDegPerSec, float JumpDeg, string Label);

    private static readonly Segment[] Script =
    {
        new(3f, 1.4f, 0f, 0f, "🚶 보행 1.4 m/s"),
        new(2.5f, 1.4f, 40f, 0f, "↪ 보행 우선회"),
        new(3f, 4.0f, 0f, 0f, "🏃 질주 4.0 m/s"),
        new(2f, 3.0f, 0f, 180f, "↩ 급반전 180°"),
        new(2.5f, 1.0f, -60f, 0f, "↩ 저속 좌선회"),
        new(2f, 0f, 0f, 0f, "🧍 정지"),
        new(1.5f, 0f, 0f, 90f, "🔄 제자리 회전 90°"),
        new(3f, 2.2f, 20f, 0f, "🚶 속보 2.2 m/s"),
    };

    private readonly object _gate = new();
    private readonly ITerrainHeightField[] _terrains = new ITerrainHeightField[5];
    private readonly Vector3[] _local = new Vector3[Count], _snap = new Vector3[Count], _arc = new Vector3[ArcPoints], _match = new Vector3[3];
    private readonly Vector2[] _hullSnap = new Vector2[9];
    private readonly float[] _grid = new float[Grid * Grid];
    private readonly float[] _costS = new float[SeriesLen], _usS = new float[SeriesLen], _zmpS = new float[SeriesLen], _skateS = new float[SeriesLen], _flS = new float[SeriesLen], _frS = new float[SeriesLen], _msS = new float[SeriesLen];
    private readonly AtmosphericWeatherStudioRunner? _weather;
    private int _seriesHead, _seriesCount, _seg, _hullN;
    private float _segT, _dirYaw, _speed, _manualYaw, _manualSpeed;
    private Vector3 _zmpSnap, _comSnap, _posSnap;
    private float _headingSnap;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public NeuralMotionMatchingStudioRunner(IServiceProvider sp) : this() => _weather = sp.GetService(typeof(AtmosphericWeatherStudioRunner)) as AtmosphericWeatherStudioRunner;

    public NeuralMotionMatchingStudioRunner()
    {
        Engine = new RealtimeNeuralMotionMatchingEngine();
        Ik = new TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer();
        Guard = new FootSkatingSuppressionJointHyperExtensionGuard();
        for (var k = 0; k < 4; k++) _terrains[k] = new ProceduralTerrainField((TerrainKind)k);
        Profile = SurfaceProfile.Of(GroundSurface.Dry);
        LastAction = "📋 대기 · DB " + Engine.Database.Frames.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " 프레임 / 클립 " + Engine.Database.ClipCount + " · 구축 " + Engine.Database.BuildMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        Snapshot();
    }

    public event Action? OnChange;

    public RealtimeNeuralMotionMatchingEngine Engine { get; }
    public TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer Ik { get; }
    public FootSkatingSuppressionJointHyperExtensionGuard Guard { get; }
    /// <summary>v17.34 계단 ZMP 균형 가드(계단 적응 IK · 동역학 필터 · 시각 디버깅 오버레이).</summary>
    public PromptFlowStudio.Services.VisualQa.ZmpStairBalanceVisualDebuggingGuard StairBalance { get; } = new();
    /// <summary>v17.35 접촉 로코모션 단계(발 배치 · 계단 합성 · ZMP 동적 안정) — null = v17.34 파이프라인.</summary>
    public ILocomotionStage? Stage { get; set; }
    /// <summary>v17.35 단계가 지정하는 속도 상한(m/s · 계단 = 보폭 ↔ 디딤판 깊이 정합 · 기본 무제한).</summary>
    public float SpeedLimit { get; set; } = float.PositiveInfinity;
    public TerrainKind Terrain { get; private set; } = TerrainKind.Flat;
    public SurfaceProfile Profile { get; private set; }
    public bool Autopilot { get; private set; } = true;
    public string SegmentLabel => Autopilot ? Script[_seg].Label : "🎮 수동 입력";
    public float DesiredSpeed => _speed;
    public float SpeedCap => 1.5f + 6f * Profile.Friction;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float CostSeries(int i) => _costS[Idx(i)];
    public float SearchUsSeries(int i) => _usS[Idx(i)];
    public float ZmpSeries(int i) => _zmpS[Idx(i)];
    public float SkateSeries(int i) => _skateS[Idx(i)];
    public float ForceLSeries(int i) => _flS[Idx(i)];
    public float ForceRSeries(int i) => _frS[Idx(i)];
    public float TickMsSeries(int i) => _msS[Idx(i)];

    public static string TerrainName(TerrainKind k) => k switch
    {
        TerrainKind.Ramp => "⛰ 경사로 ±14°", TerrainKind.Stairs => "🪜 계단 15 cm", TerrainKind.Bumps => "〰 굴곡 지형", TerrainKind.NeuralWorld => "🌍 v17.27 신경망 월드", _ => "▭ 평지",
    };

    public ITerrainHeightField TerrainField => _terrains[(int)Terrain];

    // ══════════════════════ UI 스냅숏(잠금 복사) ══════════════════════

    public void CopyPose(Span<Vector3> dst) { lock (_gate) _snap.AsSpan().CopyTo(dst); }
    public void CopyArc(Span<Vector3> dst) { lock (_gate) _arc.AsSpan().CopyTo(dst); }
    public void CopyMatch(Span<Vector3> dst) { lock (_gate) _match.AsSpan().CopyTo(dst); }
    public int CopyHull(Span<Vector2> dst) { lock (_gate) { _hullSnap.AsSpan(0, _hullN).CopyTo(dst); return _hullN; } }
    public void CopyGrid(Span<float> dst) { lock (_gate) _grid.AsSpan().CopyTo(dst); }
    public Vector3 ZmpPoint { get { lock (_gate) return _zmpSnap; } }
    public Vector3 ComPoint { get { lock (_gate) return _comSnap; } }
    public Vector3 RootPoint { get { lock (_gate) return _posSnap; } }
    public float RootHeading { get { lock (_gate) return _headingSnap; } }

    // ══════════════════════ 제어 ══════════════════════

    public void SetTerrain(TerrainKind k)
    {
        lock (_gate) { _terrains[(int)k] ??= new NeuralWorldTerrainField(); Terrain = k; Ik.Reset(); Guard.Reset(); }
        LastAction = "🗺 지형 → " + TerrainName(k);
        Raise();
    }

    public void SetSurface(GroundSurface k, float snowCm = 15f)
    {
        lock (_gate) Profile = SurfaceProfile.Of(k, snowCm);
        LastAction = "🧊 노면 → " + SurfaceProfile.Name(k) + " · 침하 " + (Profile.SinkM * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cm · μ " + Profile.Friction.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>v17.28 미기후 러너의 평균 적설/습윤도 → 노면 물성.</summary>
    public bool SyncWeather()
    {
        if (_weather is null) { LastAction = "☁ v17.28 기상 러너 미등록"; Raise(); return false; }
        var p = SurfaceProfile.FromWeather(_weather.Micro.MeanSnowCm, _weather.Micro.MeanWetness);
        lock (_gate) Profile = p;
        LastAction = "☁ v17.28 기상 연동 → " + SurfaceProfile.Name(p.Kind) + " · 적설 " + _weather.Micro.MeanSnowCm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cm";
        Raise();
        return true;
    }

    public void SetAutopilot(bool on)
    {
        lock (_gate) { Autopilot = on; if (on) { _seg = 0; _segT = 0f; } else { _manualYaw = _dirYaw; _manualSpeed = 0f; } }
        LastAction = on ? "🤖 자동 조종 ON(보행 · 선회 · 질주 · 급반전 · 정지 · 제자리 회전)" : "🎮 수동 입력";
        Raise();
    }

    /// <summary>수동 입력(월드 방위 ° · 속도 m/s) — 자동 조종 해제.</summary>
    public void SetInput(float headingDeg, float speed)
    {
        lock (_gate) { Autopilot = false; _manualYaw = headingDeg * MathF.PI / 180f; _manualSpeed = Math.Clamp(speed, 0f, 5f); }
        LastAction = "🎮 입력 → 방위 " + headingDeg.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "° · " + speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
        Raise();
    }

    public void Reverse()
    {
        lock (_gate) { if (Autopilot) _dirYaw += MathF.PI; else _manualYaw += MathF.PI; }
        LastAction = "↩ 급반전 주입(180°)";
        Raise();
    }

    /// <summary>v17.37 래그돌 러너 무할당 조향(문자열 · 이벤트 없음 · 자동 조종 해제).</summary>
    public void Steer(float headingRad, float speed) { lock (_gate) { Autopilot = false; _manualYaw = headingRad; _manualSpeed = Math.Clamp(speed, 0f, 5f); } }

    /// <summary>v17.37 기립 완료 루트 순간 이동(DB 유휴 프레임 · IK/가드 초기화 · 정지).</summary>
    public void Teleport(Vector3 position, float headingRad)
    {
        lock (_gate) { Engine.Reset(position, headingRad); Ik.Reset(); Guard.Reset(); _dirYaw = _manualYaw = headingRad; _manualSpeed = 0f; Autopilot = false; }
    }

    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 가드 ON(뼈 길이 · 접지 락 · 무릎 4~150° · 20 rad/s)" : "⛔ 가드 OFF(풋스케이팅 · 과신전 비교)"; Raise(); }
    public void SetIk(bool on) { lock (_gate) Ik.Enabled = on; LastAction = on ? "🦵 지형 IK ON(FABRIK · 골반 · 척추)" : "🦵 IK OFF(평지 애니 그대로 → 관통 비교)"; Raise(); }
    public void SetStairBalance(bool on) { lock (_gate) { StairBalance.Enabled = on; StairBalance.Apply(Ik); } LastAction = on ? "🦶 계단 ZMP 가드 ON(등가 경사로 · 골반 감쇠 · 동역학 필터)" : "🦶 계단 ZMP 가드 OFF(v17.33 동작)"; Raise(); }
    public void SetBalance(bool on) { lock (_gate) Ik.BalanceEnabled = on; LastAction = on ? "⚖ ZMP 균형 ON" : "⚖ ZMP 균형 OFF"; Raise(); }
    public void SetInertialization(bool on) { lock (_gate) Engine.Inertialize = on; LastAction = on ? "〰 관성 블렌딩 ON(반감기 0.1 s)" : "⚡ 관성 블렌딩 OFF(즉시 전환 팝 비교)"; Raise(); }

    public void ResetMetrics()
    {
        lock (_gate) { Engine.ResetMetrics(); Ik.ResetMetrics(); Guard.ResetMetrics(); StairBalance.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    public void ResetCharacter()
    {
        lock (_gate) { Engine.Reset(Vector3.Zero, 0f); Ik.Reset(); Guard.Reset(); _dirYaw = 0f; _seg = 0; _segT = 0f; }
        LastAction = "⟲ 캐릭터 원점 복귀";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("MotionMatching.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            if (Autopilot)
            {
                var sg = Script[_seg];
                if (_segT == 0f) _dirYaw += sg.JumpDeg * MathF.PI / 180f;
                _dirYaw += sg.TurnDegPerSec * MathF.PI / 180f * Dt;
                _speed = sg.Speed;
                _segT += Dt;
                if (_segT >= sg.Seconds) { _segT = 0f; _seg = (_seg + 1) % Script.Length; }
            }
            else { _dirYaw = _manualYaw; _speed = _manualSpeed; }
            _dirYaw = BipedSkeleton.WrapPi(_dirYaw);

            // 저마찰 노면: 속도 상한 · 가감속 완화(신중 보행)
            var speed = MathF.Min(MathF.Min(_speed, SpeedCap), SpeedLimit);
            Engine.Controller.HalfLife = Math.Clamp(0.27f * MathF.Sqrt(0.8f / Profile.Friction), 0.27f, 0.5f);
            Engine.Controller.SetDesired(BipedSkeleton.Forward(_dirYaw) * speed, _dirYaw);
            StairBalance.Apply(Ik);
            Engine.Step();

            Engine.Pose.CopyTo(_local);
            Guard.Sanitize(_local);
            var terrain = TerrainField;
            Ik.Begin(_local, Engine.Position, Engine.Heading, terrain, Profile, Dt, Engine.Contact);
            var stage = Stage;
            stage?.BeforeLock(this);
            Guard.LockFeet(Ik.AnkleTargets, Ik.ToeTargets, Ik.GroundY, Ik.AnimWorld, Engine.Contact, Dt);
            stage?.BeforeSolve(this);
            Ik.Solve(terrain, Profile, Engine.Contact, Guard.Enabled ? FootSkatingSuppressionJointHyperExtensionGuard.MaxReach : BipedSkeleton.Thigh + BipedSkeleton.Shin, Dt);
            Guard.Post(Ik.Pose, Ik.Poles, Engine.Contact, Dt);
            stage?.AfterPost(this);
            Ik.UpdateDynamics(terrain, Profile, Engine.Contact, Dt);
            StairBalance.Observe(Ik, terrain, Engine.Position, Engine.Heading);
            stage?.AfterDynamics(this);

            Snapshot();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _costS[h] = Engine.LastCost; _usS[h] = (float)Engine.LastSearchUs; _zmpS[h] = Ik.ZmpOffset; _skateS[h] = Guard.RecentSkatingCmPerSec;
            _flS[h] = Ik.ForceL; _frS[h] = Ik.ForceR; _msS[h] = ms;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private void Snapshot()
    {
        Ik.Pose.CopyTo(_snap);
        if (Tick == 0) { BipedSkeleton.Rest(_snap); }
        Engine.PredictArc(_arc);
        Engine.MatchedFuture(_match);
        var terrain = TerrainField;
        for (var i = 0; i < ArcPoints; i++) _arc[i].Y = terrain.Height(_arc[i].X, _arc[i].Z);
        for (var i = 0; i < 3; i++) _match[i].Y = terrain.Height(_match[i].X, _match[i].Z);
        _hullN = Ik.HullCount;
        for (var i = 0; i < _hullN; i++) _hullSnap[i] = Ik.HullPoint(i);
        _zmpSnap = Ik.Zmp; _comSnap = Ik.CenterOfMass; _posSnap = Engine.Position; _headingSnap = Engine.Heading;
        if (Tick % 6 == 0)
        {
            var cx = MathF.Round(Engine.Position.X / GridStep) * GridStep; var cz = MathF.Round(Engine.Position.Z / GridStep) * GridStep;
            for (var j = 0; j < Grid; j++)
                for (var i = 0; i < Grid; i++)
                    _grid[j * Grid + i] = terrain.Height(cx + (i - Grid / 2) * GridStep, cz + (j - Grid / 2) * GridStep);
        }
    }

    /// <summary>지형 격자 원점(UI — 스냅숏 격자 중심).</summary>
    public Vector2 GridCenter
    {
        get
        {
            lock (_gate) return new Vector2(MathF.Round(_posSnap.X / GridStep) * GridStep, MathF.Round(_posSnap.Z / GridStep) * GridStep);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.MotionMatching" };
            _loop.Start();
            LastAction = "▶ 모션 매칭 60 Hz · DB " + Engine.Database.Frames.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " 프레임 · " + TerrainName(Terrain);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("MotionMatching.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MotionMatching.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
