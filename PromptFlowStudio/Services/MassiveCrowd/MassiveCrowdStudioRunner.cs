namespace PromptFlowStudio.Services.MassiveCrowd;

/// <summary>
/// [PromptFlow Studio v17.04.0] 👥 대규모 군중 시뮬레이션 &amp; 음향 스튜디오 러너(60 Hz 전용 스레드).
/// 카메라(스탠드 궤도 또는 수동 방위) → LOD 가드 배정 → 군중 엔진 1 프레임 → 스타디움 음향 1 블록(800 샘플 · 카메라 = 청취자) →
/// 실측 프레임 시간을 가드 예산 피드백에 보고(16.6 ms).
/// </summary>
public sealed class MassiveCrowdStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float CameraRadius = 150f;

    private readonly object _gate = new();
    private readonly float[] _spl = new float[SeriesLen], _ms = new float[SeriesLen], _arousal = new float[SeriesLen], _fear = new float[SeriesLen], _near = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _orbit = MathF.PI;
    private ulong _rng = 0xDA942042E4DD58B5UL;

    public MassiveCrowdStudioRunner(IServiceProvider sp)
        : this((MassiveAgenticCrowdSimulationEngine?)sp.GetService(typeof(MassiveAgenticCrowdSimulationEngine)) ?? new MassiveAgenticCrowdSimulationEngine(),
               (SwarmStadiumAcousticsSynthesizer?)sp.GetService(typeof(SwarmStadiumAcousticsSynthesizer)) ?? new SwarmStadiumAcousticsSynthesizer(),
               (BehavioralLodSpatialCullingGuard?)sp.GetService(typeof(BehavioralLodSpatialCullingGuard)) ?? new BehavioralLodSpatialCullingGuard())
    {
    }

    public MassiveCrowdStudioRunner(MassiveAgenticCrowdSimulationEngine crowd, SwarmStadiumAcousticsSynthesizer acoustics, BehavioralLodSpatialCullingGuard lod)
    {
        Crowd = crowd; Acoustics = acoustics; Lod = lod;
        PlaceCamera();
        LastAction = "📋 대기 · 군중 " + crowd.Count + "명";
    }

    public event Action? OnChange;

    public MassiveAgenticCrowdSimulationEngine Crowd { get; }
    public SwarmStadiumAcousticsSynthesizer Acoustics { get; }
    public BehavioralLodSpatialCullingGuard Lod { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public bool Orbit { get; set; } = true;
    public float OrbitDegPerSec { get; set; } = 4f;
    public float CameraYawDeg { get => _orbit * 180f / MathF.PI; set => _orbit = value * MathF.PI / 180f; }
    public float Excitement { get => Crowd.BaseExcitement; set => Crowd.BaseExcitement = Math.Clamp(value, 0f, 1f); }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    public float SplSeries(int i) => _spl[Idx(i)];
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    public float ArousalSeries(int i) => _arousal[Idx(i)];
    public float FearSeries(int i) => _fear[Idx(i)];
    public float NearSeries(int i) => _near[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    /// <summary>카메라 = 스탠드 바깥 궤도에서 필드 중심 조준(청취자 겸용).</summary>
    private void PlaceCamera()
    {
        var x = MassiveAgenticCrowdSimulationEngine.Cx + CameraRadius * MathF.Cos(_orbit);
        var z = MassiveAgenticCrowdSimulationEngine.Cz + CameraRadius * 0.7f * MathF.Sin(_orbit);
        Lod.SetCamera(x, z, MathF.Atan2(MassiveAgenticCrowdSimulationEngine.Cz - z, MassiveAgenticCrowdSimulationEngine.Cx - x), Lod.FovDeg);
    }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    public void GoalHome() { lock (_gate) Crowd.Goal(0); LastAction = "⚽ 홈 팀 골 · 환호/박수 · 원정 야유"; Raise(); }
    public void GoalAway() { lock (_gate) Crowd.Goal(1); LastAction = "⚽ 원정 팀 골 · 원정 환호 · 홈 야유"; Raise(); }
    public void Cheer() { lock (_gate) Crowd.Cheer(); LastAction = "🙌 전원 환호"; Raise(); }
    public void Wave() { lock (_gate) Crowd.StartWave(); LastAction = "🌊 스타디움 파도타기 시작"; Raise(); }
    public void ToggleChant() { Acoustics.ChantEnabled = !Acoustics.ChantEnabled; LastAction = Acoustics.ChantEnabled ? "📢 응원 챈트 시작" : "📢 챈트 중지"; Raise(); }

    /// <summary>카메라 시야 쪽 스탠드에서 피격/위협 → 공포 전파 · 출구 대피.</summary>
    public void Hit()
    {
        lock (_gate)
        {
            var a = _orbit + MathF.PI + (Next01() - 0.5f) * 0.6f;
            var s = 0.5f * (MassiveAgenticCrowdSimulationEngine.StandInner + MassiveAgenticCrowdSimulationEngine.StandOuter);
            Crowd.Panic(MassiveAgenticCrowdSimulationEngine.Cx + MassiveAgenticCrowdSimulationEngine.FieldRx * s * MathF.Cos(a),
                        MassiveAgenticCrowdSimulationEngine.Cz + MassiveAgenticCrowdSimulationEngine.FieldRz * s * MathF.Sin(a));
        }
        LastAction = "💥 스탠드 피격 · 공포 전파 · 출구 대피";
        Raise();
    }

    public void SetPopulation(int n)
    {
        lock (_gate) Crowd.Populate(n);
        LastAction = "👥 군중 재배치 · " + Crowd.Count + "명";
        Raise();
    }

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            if (Orbit) _orbit += OrbitDegPerSec * MathF.PI / 180f * dt;
            PlaceCamera();
            Lod.Assign(Crowd);
            Crowd.Step(dt);
            Acoustics.Render(Crowd, Lod.CameraX, Lod.CameraZ, Lod.CameraYaw);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Lod.Report(ms);
            _spl[_seriesHead] = Acoustics.TotalSplDb; _ms[_seriesHead] = ms; _arousal[_seriesHead] = Crowd.MeanArousal; _fear[_seriesHead] = Crowd.MeanFear; _near[_seriesHead] = Lod.NearCount;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
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
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Crowd" };
            _loop.Start();
            LastAction = "▶ 군중 시뮬레이션 가동 · " + Hz + " Hz · " + Crowd.Count + "명";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 군중 시뮬레이션 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Crowd.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Crowd.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
