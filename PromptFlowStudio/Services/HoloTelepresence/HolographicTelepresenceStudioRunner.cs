using System.Numerics;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.Cinematography;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.FilmScore;

namespace PromptFlowStudio.Services.HoloTelepresence;

/// <summary>
/// [PromptFlow Studio v17.00.0] 🔮 AI 홀로그래픽 텔레프레전스 &amp; AGI 디렉터 스튜디오 러너(60 Hz 전용 스레드).
/// 원격 퍼포머(절차적 볼류메트릭 휴먼 2400 스플랫 · 6파트 관절 애니메이션 · 춤 진폭 = 디렉터 각성) → 6DoF 자세 → 라이트필드 엔진(인코딩 → 전송 → 복원 → 45 시점).
/// 관객 시뮬레이션(숨은 취향 + 환호/이탈/야유 펄스) → AGI 디렉터(분기 · 큐 → v16.85/84/86/82 결선).
/// 시청자 손(홀로 오브 접촉 왕복 · 고속 스와이프) → 햅틱 가드 · 머리 vs 디렉터 카메라 인공 회전 → 인지 안정 가드.
/// </summary>
public sealed class HolographicTelepresenceStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 180, Splats = 2400, Parts = 6;
    public static readonly string[] PartNames = { "머리", "몸통", "왼팔", "오른팔", "왼다리", "오른다리" };
    private static readonly Vector3[] Pivots = { new(0f, 1.55f, 0f), new(0f, 1.0f, 0f), new(-0.22f, 1.45f, 0f), new(0.22f, 1.45f, 0f), new(-0.1f, 0.9f, 0f), new(0.1f, 0.9f, 0f) };
    private static readonly uint[] PartColors = { 0xFFF5C9A8u, 0xFF6D5BD0u, 0xFF8E7CF0u, 0xFF8E7CF0u, 0xFF2A3A6Eu, 0xFF2A3A6Eu };

    private readonly object _gate = new();
    private readonly Vector3[] _rest = new Vector3[Splats];
    private readonly byte[] _part = new byte[Splats];
    private readonly Quaternion[] _partRot = new Quaternion[Parts];
    private readonly float[] _mbps = new float[SeriesLen], _sick = new float[SeriesLen], _vig = new float[SeriesLen], _ev = new float[SeriesLen], _ea = new float[SeriesLen], _et = new float[SeriesLen];
    private int _seriesHead, _seriesCount, _orb, _block;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private ulong _rng = 0x1700_4F10u;
    private float _time, _cheer, _leave, _boo, _spin, _swipe = -1f;

    public HolographicTelepresenceStudioRunner(IServiceProvider sp)
        : this((HolographicLightFieldTelepresenceEngine?)sp.GetService(typeof(HolographicLightFieldTelepresenceEngine)) ?? new HolographicLightFieldTelepresenceEngine(),
               (UniversalAgiCinematicDirectorSynthesizer?)sp.GetService(typeof(UniversalAgiCinematicDirectorSynthesizer)) ?? new UniversalAgiCinematicDirectorSynthesizer(),
               (SpatialHapticCognitiveInteractionGuard?)sp.GetService(typeof(SpatialHapticCognitiveInteractionGuard)) ?? new SpatialHapticCognitiveInteractionGuard())
    {
        Director.Bind(sp.GetService(typeof(NeuralCameraTrajectoryEngine)) as NeuralCameraTrajectoryEngine,
                      sp.GetService(typeof(AtmosphericScatteringLightingEngine)) as AtmosphericScatteringLightingEngine,
                      sp.GetService(typeof(NeuralCinematographicFilmScoreGenerator)) as NeuralCinematographicFilmScoreGenerator,
                      sp.GetService(typeof(NeuralFacialMuscleSimulator)) as NeuralFacialMuscleSimulator);
    }

    public HolographicTelepresenceStudioRunner(HolographicLightFieldTelepresenceEngine holo, UniversalAgiCinematicDirectorSynthesizer director, SpatialHapticCognitiveInteractionGuard guard)
    {
        Holo = holo; Director = director; Guard = guard;
        Build();
    }

    public event Action? OnChange;

    public HolographicLightFieldTelepresenceEngine Holo { get; }
    public UniversalAgiCinematicDirectorSynthesizer Director { get; }
    public SpatialHapticCognitiveInteractionGuard Guard { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public float LatencyMs { get; set; } = 14f;
    private string? _action;
    private bool _branchNote;
    /// <summary>최근 동작(분기 문구는 틱에서 플래그만 기록 → 조회 시 지연 포맷).</summary>
    public string? LastAction
    {
        get
        {
            if (_branchNote)
            {
                _branchNote = false;
                _action = "🎬 분기 → " + Director.BeatName(Director.CurrentBeat) + " (" + UniversalAgiCinematicDirectorSynthesizer.ReasonName(Director.LogEntry(0).Reason) + ")";
            }
            return _action;
        }
        private set { _action = value; _branchNote = false; }
    }
    public int SeriesCount => _seriesCount;
    public int OrbObject => _orb;
    public int BlockObject => _block;
    public float MbpsSeries(int i) => _mbps[Idx(i)];
    public float SicknessSeries(int i) => _sick[Idx(i)];
    public float VignetteSeries(int i) => _vig[Idx(i)];
    public Vector3 EmotionSeries(int i) { var k = Idx(i); return new Vector3(_ev[k], _ea[k], _et[k]); }
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    private float Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 40) / (float)(1 << 24); }

    private void Build()
    {
        // 절차적 볼류메트릭 휴먼(파트별 타원체/원기둥 표면 샘플)
        for (var i = 0; i < Splats; i++)
        {
            var part = i < 300 ? 0 : i < 1200 ? 1 : i < 1500 ? 2 : i < 1800 ? 3 : i < 2100 ? 4 : 5;
            _part[i] = (byte)part;
            float u = Rand() * MathF.Tau, w = Rand();
            Vector3 p = part switch
            {
                0 => Pivots[0] + new Vector3(0.11f * MathF.Cos(u) * MathF.Sin(w * MathF.PI), 0.13f * MathF.Cos(w * MathF.PI) + 0.1f, 0.11f * MathF.Sin(u) * MathF.Sin(w * MathF.PI)),
                1 => new Vector3(0.18f * MathF.Cos(u), 0.95f + 0.55f * w, 0.11f * MathF.Sin(u)),
                2 => Pivots[2] + new Vector3(-0.05f + 0.045f * MathF.Cos(u), -0.6f * w, 0.045f * MathF.Sin(u)),
                3 => Pivots[3] + new Vector3(0.05f + 0.045f * MathF.Cos(u), -0.6f * w, 0.045f * MathF.Sin(u)),
                4 => Pivots[4] + new Vector3(0.06f * MathF.Cos(u), -0.88f * w, 0.06f * MathF.Sin(u)),
                _ => Pivots[5] + new Vector3(0.06f * MathF.Cos(u), -0.88f * w, 0.06f * MathF.Sin(u)),
            };
            _rest[i] = p;
            var shade = 0.75f + 0.25f * MathF.Cos(u);
            var c = PartColors[part];
            uint r = (uint)(((c >> 16) & 0xFF) * shade), g = (uint)(((c >> 8) & 0xFF) * shade), b = (uint)((c & 0xFF) * shade);
            Holo.SetSplat(i, p, 0xFF00_0000u | (r << 16) | (g << 8) | b);
        }
        Holo.SetSplatCount(Splats);
        for (var k = 0; k < Parts; k++) _partRot[k] = Quaternion.Identity;
        _orb = Guard.AddObject(new Vector3(0.25f, 0.75f, 0.35f), 0.08f, 600f, 4f, 0.7f, 0.0015f);
        _block = Guard.AddObject(new Vector3(-0.3f, 0.55f, 0.3f), 0.06f, 900f, 6f, 0.2f, 0.004f);
        Holo.SetPose(Quaternion.Identity, new Vector3(0f, -0.45f, 0f));
        Holo.ProcessFrame();
    }

    // ── 관객/연출 펄스 ──
    public void Cheer() { _cheer = 2.5f; LastAction = "📣 관객 환호"; }
    public void Leave() { _leave = 5f; LastAction = "😴 관객 몰입 이탈"; }
    public void Boo() { _boo = 3f; LastAction = "💔 관객 반감"; }
    public void Spin() { _spin = 2f; LastAction = "🌀 급회전 유도(180 °/s) → 인지 가드"; }
    public void Swipe() { _swipe = 0f; LastAction = "✋ 고속 스와이프 → 관통 차단"; }
    public void ForceKey() { Holo.ForceKey = true; LastAction = "🔑 키 프레임 강제"; }

    public bool ForceBranch(int k)
    {
        var ok = Director.ForceBranch(k);
        if (ok) LastAction = "🎬 수동 분기 → " + Director.BeatName(Director.CurrentBeat);
        return ok;
    }

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            _time += dt;
            // ── 관객 → 디렉터 ──
            var d = Director;
            var taste = 0.5f + 0.35f * MathF.Sin(_time * 0.07f);
            var eng = 1f - 1.2f * MathF.Abs(d.Arousal - taste) + 0.1f * (Rand() - 0.5f) + (_cheer > 0 ? 0.3f : 0f) - (_leave > 0 ? 0.6f : 0f);
            var sen = 0.5f * d.Valence + 0.3f * MathF.Sin(_time * 0.05f) + (_cheer > 0 ? 0.4f : 0f) - (_boo > 0 ? 1.4f : 0f);
            var exc = 0.7f * d.Arousal + 0.1f * Rand() + (_cheer > 0 ? 0.6f : 0f) - (_leave > 0 ? 0.3f : 0f);
            d.SubmitAudience(eng, sen, exc);
            _cheer -= dt; _leave -= dt; _boo -= dt;
            var beat = d.CurrentBeat;
            d.Tick(dt);
            if (d.CurrentBeat != beat) _branchNote = true;
            // ── 퍼포머 애니메이션(춤 진폭 = 각성) ──
            var amp = 0.2f + 0.9f * d.Arousal;
            var ph = _time * (1.5f + 3f * d.Arousal);
            _partRot[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.15f * amp * MathF.Sin(ph * 0.5f));
            _partRot[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f * amp * MathF.Sin(ph * 0.5f));
            _partRot[2] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.3f - 1.1f * amp * (0.5f + 0.5f * MathF.Sin(ph)));
            _partRot[3] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f + 1.1f * amp * (0.5f + 0.5f * MathF.Sin(ph + MathF.PI)));
            _partRot[4] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.35f * amp * MathF.Sin(ph));
            _partRot[5] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.35f * amp * MathF.Sin(ph));
            for (var i = 0; i < Splats; i++)
            {
                var k = _part[i];
                Holo.SetSplatPosition(i, Pivots[k] + Vector3.Transform(_rest[i] - Pivots[k], _partRot[k]));
            }
            var yaw = 0.6f * MathF.Sin(_time * 0.3f);
            Holo.SetPose(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), new Vector3(0.15f * MathF.Sin(_time * 0.2f), -0.45f, 0.1f * MathF.Cos(_time * 0.25f)));
            Holo.ProcessFrame();
            // ── 햅틱: 손 0 = 오브 접촉 왕복 · 손 1 = 대기/고속 스와이프 ──
            var orbC = new Vector3(0.25f + 0.05f * MathF.Sin(_time * 0.8f), 0.75f, 0.35f);
            Guard.MoveObject(_orb, orbC, new Vector3(0.04f * MathF.Cos(_time * 0.8f), 0f, 0f));
            var hand0 = orbC + new Vector3(0.03f * MathF.Sin(_time * 3f), 0.02f, 0.08f + 0.03f * MathF.Sin(_time * 1.7f));
            Guard.StepHand(0, hand0, dt);
            var bc = Guard.ObjectCenter(_block);
            Vector3 hand1;
            if (_swipe >= 0f)
            {
                hand1 = bc + new Vector3(_swipe < 0.5f ? -0.25f : 0.25f, 0f, 0f);
                _swipe += 0.5f;
                if (_swipe > 4f) _swipe = -1f;
            }
            else hand1 = bc + new Vector3(-0.25f, 0.1f, 0f);
            Guard.StepHand(1, hand1, dt);
            // ── 인지 안정: 머리(전정) vs 디렉터 가상 카메라(시각) ──
            var head = new Vector3(0.05f * MathF.Sin(_time * 1.3f), 0.2f * MathF.Sin(_time * 0.9f), 0f);
            var c = d.Cue;
            var visualYaw = c.Move switch
            {
                CameraMove.Orbit => c.OrbitRateDeg * MathF.PI / 180f,
                CameraMove.Handheld => 0.6f * (Rand() - 0.5f),
                CameraMove.Crane => 0.15f,
                _ => 0.05f,
            };
            if (_spin > 0f) { visualYaw += MathF.PI; _spin -= dt; }
            var visual = head + new Vector3(0f, visualYaw, 0f);
            var visAcc = c.Move == CameraMove.Handheld ? new Vector3(2f * (Rand() - 0.5f), 2f * (Rand() - 0.5f), 0f) : Vector3.Zero;
            Guard.StepCognitive(head, Vector3.Zero, visual, visAcc, LatencyMs, (float)LastTickMs, dt);
            // ── 계측 ──
            _mbps[_seriesHead] = Holo.Mbps; _sick[_seriesHead] = Guard.Sickness; _vig[_seriesHead] = Guard.Vignette;
            _ev[_seriesHead] = d.Valence; _ea[_seriesHead] = d.Arousal; _et[_seriesHead] = d.Tension;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.HoloTele" };
            _loop.Start();
            LastAction = "▶ 홀로그래픽 텔레프레전스 가동 · " + Hz + " Hz";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 텔레프레전스 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("HoloTele.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("HoloTele.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
