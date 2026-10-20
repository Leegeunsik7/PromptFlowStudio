using System.Numerics;
using PromptFlowStudio.Services.DexterousManipulation;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Garment;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SpatialAcoustics;

namespace PromptFlowStudio.Services.ProceduralFoley;

/// <summary>
/// [PromptFlow Studio v17.38.0] 🔊 AI 절차적 물리 폴리 스튜디오 러너(60 Hz 전용 스레드 · 프레임 = 48 kHz 블록 800).
/// 매 프레임 전용 v17.37 래그돌 러너(v17.33 보행 · 발 착지 · 전신 강타 · 지면 스크레이프 · 관절 각가속도) + 전용 v17.36 양손 조작 러너(파지 접촉 · 낙하 · 쿨롱 슬립)
/// → 접촉/슬립 이벤트 추출(서브 프레임 시각 = 샘플 오프셋) → 모달 폴리 엔진 · 마찰/스침 합성기 → 마스터 스테레오 + 공간 버스 2 → 소프트 리미터/LUFS 가드
/// → v17.23 광선 추적 경로 + 6DoF 바이노럴 합성기(가드 후 버스 직접 공급) 순으로 처리한다. 앱 v17.16 의상 러너가 구동 중이면 스트레인 변화율을 그대로 사용.
/// </summary>
public sealed class ProceduralFoleyStudioRunner : IDisposable
{
    public const int Hz = 60, Block = RealtimeModalContactFoleyEngine.Block, Fs = RealtimeModalContactFoleyEngine.Fs, Scope = 2048, Bands = 48, N = RagdollSkeleton.Count;
    public const float Dt = 1f / Hz, G = 9.81f;
    public static readonly Vector3 Listener = new(0f, 1.6f, 3.2f);

    private readonly object _gate = new();
    private readonly float[] _b0 = new float[Block], _b1 = new float[Block], _l = new float[Block], _r = new float[Block], _f0 = new float[Block], _f1 = new float[Block];
    private readonly float[] _scopeL = new float[Scope], _scopeSnap = new float[Scope], _band = new float[Bands], _bandSnap = new float[Bands];
    private readonly float[] _msS = new float[240];
    private readonly AcousticPath[] _paths = new AcousticPath[Hrtf6DoFBinauralSpatialAudioSynthesizer.Sources * RealtimeAcousticRayTracingEngine.PathSlots];
    private readonly bool[] _gPrev = new bool[N];
    private readonly Vector3[] _pPrev = new Vector3[N], _vPrev = new Vector3[N], _omPrev = new Vector3[4];
    private readonly float[] _blendPrev = new float[DexterousManipulationStudioRunner.Slots], _slipPrev = new float[DexterousManipulationStudioRunner.Slots];
    private readonly GarmentDynamicsStudioRunner? _garment;
    private uint _rng = 0x1234567u;
    private byte _contactPrev;
    private long _dropsPrev, _tick;
    private float _flexPrev, _strainPrev;
    private bool _primed;
    private int _scopeHead, _msHead;
    private Thread? _loop;
    private volatile bool _running;

    public ProceduralFoleyStudioRunner(IServiceProvider sp) : this() => _garment = sp.GetService(typeof(GarmentDynamicsStudioRunner)) as GarmentDynamicsStudioRunner;

    public ProceduralFoleyStudioRunner()
    {
        Ragdoll = new NeuralRagdollStudioRunner();
        Dex = new DexterousManipulationStudioRunner();
        Tracer = new RealtimeAcousticRayTracingEngine();
        Spatial = new Hrtf6DoFBinauralSpatialAudioSynthesizer();
        LastAction = "📋 대기 · 모달 보이스 " + RealtimeModalContactFoleyEngine.MaxVoices + " × " + RealtimeModalContactFoleyEngine.Modes + " 모드 · 48 kHz · 블록 " + Block;
    }

    public event Action? OnChange;

    public NeuralRagdollStudioRunner Ragdoll { get; }
    public DexterousManipulationStudioRunner Dex { get; }
    public RealtimeAcousticRayTracingEngine Tracer { get; }
    public Hrtf6DoFBinauralSpatialAudioSynthesizer Spatial { get; }
    public RealtimeModalContactFoleyEngine Modal { get; } = new();
    public ContinuousFrictionClothRustleSynthesizer Friction { get; } = new();
    public AcousticEnergyPeakSoftLimiterGuard Limiter { get; } = new();
    public FoleyMaterial Floor { get; private set; } = FoleyMaterial.Wood;
    public bool SpatialFeed { get; set; } = true;
    public bool AutoDemo { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public long Footsteps { get; private set; }
    public long BodyThuds { get; private set; }
    public long GraspTaps { get; private set; }
    public long PropDrops { get; private set; }
    public long Bursts { get; private set; }
    public float ClothStrainRate { get; private set; }
    public float ClothAngularAccel { get; private set; }
    public bool GarmentLinked { get; private set; }
    public float TickMsSeries(int i) => _msS[(_msHead - 1 - i + 480) % 240];
    public float BandLevelDb(int b) { lock (_gate) return _bandSnap[b]; }
    public static float BandHz(int b) => 60f * MathF.Pow(16000f / 60f, b / (Bands - 1f));
    public void CopyScope(Span<float> dst) { lock (_gate) _scopeSnap.AsSpan(0, Math.Min(dst.Length, Scope)).CopyTo(dst); }
    public static FoleyMaterial PropMaterial(DexPropKind k) => k switch { DexPropKind.Sphere => FoleyMaterial.Glass, DexPropKind.Handle => FoleyMaterial.Metal, _ => FoleyMaterial.Wood };

    // ══════════════════════ 제어 ══════════════════════

    public void SetFloor(FoleyMaterial m) { lock (_gate) Floor = m; LastAction = "🧱 바닥 재질 → " + FoleyMaterials.Name(m); Raise(); }
    public void SetFabric(FabricKind f) { lock (_gate) Friction.Fabric = f; LastAction = "🧣 직물 → " + (f == FabricKind.Silk ? "실크" : f == FabricKind.Leather ? "가죽" : "면"); Raise(); }
    public void SetLimiter(bool on) { lock (_gate) Limiter.Enabled = on; LastAction = on ? "🛡 소프트 리미터 ON(4× 트루 피크 · 전방 예측 · 2차 소프트 클립 · LUFS)" : "⛔ 리미터 OFF(하드 클램프 비교)"; Raise(); }
    public void SetAutoDemo(bool on) { lock (_gate) { AutoDemo = on; Ragdoll.SetAutoImpulse(on); } LastAction = on ? "🤖 자동 데모 ON(보행 · 충격 전도 · 인계)" : "🤖 자동 데모 OFF"; Raise(); }

    /// <summary>수동 타격(재질 매트릭스 · 충격체 · 속도) — 버스 1.</summary>
    public void Strike(FoleyMaterial surface, FoleyMaterial impactor, float speed, float massKg = 0.3f, float radiusM = 0.02f)
    {
        lock (_gate) Modal.Strike(new FoleyContact(surface, impactor, massKg, radiusM, speed, Listener + new Vector3(0.6f, -0.6f, -1.2f), 1), 0);
        LastAction = "🔨 " + FoleyMaterials.Name(impactor) + " → " + FoleyMaterials.Name(surface) + " · " + speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s · F " + Modal.LastForceN.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " N · τ " + Modal.LastTauMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        Raise();
    }

    /// <summary>군중(v17.29) 동시 다중 충돌 스트레스 — count 개 접촉을 같은 블록에 무작위 재질/시각으로.</summary>
    public void CrowdBurst(int count, float speed = 4f)
    {
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                var s = (FoleyMaterial)(Next() % 5); var m = (FoleyMaterial)(Next() % 5);
                Modal.Strike(new FoleyContact(s, m, 0.5f + (Next() % 100) * 0.03f, 0.02f + (Next() % 50) * 0.001f, speed * (0.6f + (Next() % 80) * 0.01f), Listener, (byte)(i & 1)), (int)(Next() % 120));
            }
            Bursts++;
        }
        LastAction = "👥 군중 동시 충돌 " + count + "건 · 활성 보이스 " + Modal.ActiveVoices;
        Raise();
    }

    public void ResetMetrics()
    {
        lock (_gate) { Modal.ResetMetrics(); Friction.ResetMetrics(); Limiter.ResetMetrics(); Footsteps = BodyThuds = GraspTaps = PropDrops = Bursts = 0; MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    private uint Next() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return x; }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("ProceduralFoley.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            Ragdoll.Step();
            Dex.Step();
            Events();
            Array.Clear(_b0); Array.Clear(_b1);
            Modal.Render(_b0, _b1);
            Friction.Render(_b0, _b1);
            for (var i = 0; i < Block; i++)
            {
                float a = _b0[i], b = _b1[i];
                _l[i] = a + 0.6f * b; _r[i] = 0.6f * a + b; _f0[i] = a; _f1[i] = b;
            }
            Limiter.Process(_l, _r, _f0, _f1);
            if (SpatialFeed)
            {
                var src0 = BodySource(); var src1 = Listener + new Vector3(0.6f, -0.6f, -1.2f);
                Tracer.ComputePaths(src0, Listener, _paths.AsSpan(0, RealtimeAcousticRayTracingEngine.PathSlots));
                Tracer.ComputePaths(src1, Listener, _paths.AsSpan(RealtimeAcousticRayTracingEngine.PathSlots, RealtimeAcousticRayTracingEngine.PathSlots));
                Spatial.Render(_f0, _f1, Listener, Quaternion.Identity, _paths, 0.6f, 0.45f, 0.25f);
            }
            for (var i = 0; i < Block; i++) { _scopeL[_scopeHead] = _l[i]; _scopeHead = (_scopeHead + 1) % Scope; }
            if (_tick % 6 == 0) Bands48();
            for (var i = 0; i < Scope; i++) _scopeSnap[i] = _scopeL[(_scopeHead + i) % Scope];
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _msS[_msHead] = ms; _msHead = (_msHead + 1) % 240;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private Vector3 BodySource()
    {
        var p = Ragdoll.Engine.Output(0);
        return new Vector3(Math.Clamp(p.X * 0.6f, -4f, 4f), 0.4f, Math.Clamp(p.Z * 0.6f - 1f, -4f, 5f));
    }

    /// <summary>물리 단계 → 접촉/슬립/스침 이벤트(서브 프레임 시각 → 샘플 오프셋).</summary>
    private void Events()
    {
        var e = Ragdoll.Engine;
        var floor = Floor;
        var src = BodySource();
        // ── 발 착지(v17.33/v17.35 접지 비트 상승 에지 · 보행 중) ──
        var ct = Ragdoll.Motion.Engine.Contact;
        if (_primed && Ragdoll.Recovery.State == RecoveryState.Locomotion)
        {
            for (var s = 0; s < 2; s++)
            {
                if ((ct >> s & 1) == 0 || (_contactPrev >> s & 1) != 0) continue;
                var j = s == 0 ? 18 : 22;
                var p = e.Output(j);
                var v = MathF.Max(0.35f, (_pPrev[j].Y - p.Y) / Dt + 0.25f);
                var frac = Math.Clamp((_pPrev[j].Y - 0.08f) / MathF.Max(_pPrev[j].Y - p.Y, 1e-4f), 0f, 1f);
                Modal.Strike(new FoleyContact(floor, FoleyMaterial.Flesh, 1.2f, 0.045f, v, src, 0), (int)(frac * (Block - 1)));
                Footsteps++;
            }
        }
        _contactPrev = ct;
        // ── 의상 스침(이전 프레임 위치 갱신 전): v17.16 스트레인 변화율(구동 중) 또는 관절 굴곡 변화율 대리 + v17.37 전완/하퇴 각가속도 ──
        var acc = 0f;
        for (var k = 0; k < 4; k++)
        {
            int a = k switch { 0 => 8, 1 => 13, 2 => 17, _ => 21 }, b = a + 1;
            var d = e.Output(b) - e.Output(a);
            var dp = _primed ? d - (_pPrev[b] - _pPrev[a]) : Vector3.Zero;
            var r2 = MathF.Max(d.LengthSquared(), 1e-6f);
            var om = Vector3.Cross(d, dp / Dt) / r2;
            if (_primed) acc = MathF.Max(acc, (om - _omPrev[k]).Length() / Dt);
            _omPrev[k] = om;
        }
        var flex = Flex(e.Output(16), e.Output(17), e.Output(18)) + Flex(e.Output(20), e.Output(21), e.Output(22)) + Flex(e.Output(7), e.Output(8), e.Output(9)) + Flex(e.Output(12), e.Output(13), e.Output(14));
        float strainRate;
        var gm = _garment;
        if (gm != null && gm.IsRunning)
        {
            var s = gm.Engine.MeanStretchError;
            strainRate = _primed ? (s - _strainPrev) / Dt : 0f; _strainPrev = s; GarmentLinked = true;
        }
        else { strainRate = _primed ? (flex - _flexPrev) / Dt * 0.004f : 0f; GarmentLinked = false; }
        _flexPrev = flex;
        ClothStrainRate = strainRate; ClothAngularAccel = acc;
        Friction.SetCloth(strainRate, acc);
        // ── 전신 강타(v17.37 래그돌 관절 지면 접촉 상승 에지 · 법선 속도) ──
        var scrapeV = 0f; var scrapeN = 0f;
        for (var j = 0; j < N; j++)
        {
            var g = e.Grounded(j);
            var p = e.Output(j);
            var vel = _primed ? (p - _pPrev[j]) / Dt : Vector3.Zero;
            if (_primed && g && !_gPrev[j] && e.Dynamic(j))
            {
                var vn = -_vPrev[j].Y;
                if (vn > 0.4f)
                {
                    var r = RagdollSkeleton.ParticleRadius[j];
                    var frac = Math.Clamp((_pPrev[j].Y - r) / MathF.Max(_pPrev[j].Y - p.Y, 1e-4f), 0f, 1f);
                    Modal.Strike(new FoleyContact(floor, FoleyMaterial.Flesh, RagdollSkeleton.Mass[j], r, vn, src, 0), (int)(frac * (Block - 1)));
                    BodyThuds++;
                }
            }
            if (g && e.Dynamic(j))
            {
                var h = new Vector2(vel.X, vel.Z).Length();
                scrapeV = MathF.Max(scrapeV, h);
                scrapeN += RagdollSkeleton.Mass[j] * G;
            }
            _gPrev[j] = g; _vPrev[j] = vel; _pPrev[j] = p;
        }
        Friction.SetSlip(2, scrapeV > 0.05f ? scrapeV : 0f, MathF.Min(scrapeN, 700f), floor, 0);
        // ── 양손 조작(v17.36): 파지 접촉 탭 · 쿨롱 슬립 · 낙하 ──
        var pm = PropMaterial(Dex.Prop.Kind);
        for (var s = 0; s < DexterousManipulationStudioRunner.Slots; s++)
        {
            var bl = Dex.Engine.Active(s) ? Dex.Engine.CloseBlend(s) : 0f;
            if (_primed && bl > 0.98f && _blendPrev[s] <= 0.98f)
            {
                Modal.Strike(new FoleyContact(pm, FoleyMaterial.Flesh, 0.05f, 0.008f, 0.35f, src, 1), (int)(Next() % 200));
                GraspTaps++;
            }
            _blendPrev[s] = bl;
            var sm = Dex.Guard.SlipMm(s);
            var sv = _primed ? MathF.Abs(sm - _slipPrev[s]) * 0.001f / Dt : 0f;
            _slipPrev[s] = sm;
            if (s < 2 || sv > 0f)
            {
                var nf = 0f; for (var f = 0; f < 5; f++) nf += Dex.Guard.FingerForce(s, f);
                var ch = s < 2 ? s : 3;
                if (s < 2 || sv > Friction.SlipSpeed(3)) Friction.SetSlip(ch, Dex.Guard.Slipping(s) || sv > 0.002f ? sv : 0f, nf, pm, 1);
            }
        }
        var drops = Dex.Handover.Drops;
        if (_primed && drops > _dropsPrev)
        {
            Modal.Strike(new FoleyContact(floor, pm, Dex.Prop.Mass, MathF.Max(0.01f, Dex.Prop.CharLength * 0.5f), MathF.Sqrt(2f * G * 1f), src, 1), (int)(Next() % 400));
            PropDrops++;
        }
        _dropsPrev = drops;
        _primed = true;
    }

    private static float Flex(Vector3 a, Vector3 b, Vector3 c)
    {
        var u = b - a; var v = c - b;
        var d = u.Length() * v.Length();
        return d > 1e-6f ? MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / d, -1f, 1f)) : 0f;
    }

    /// <summary>스펙트럼 뷰어(마스터 L 최근 2048 샘플 · 로그 48 대역 Goertzel).</summary>
    private void Bands48()
    {
        for (var b = 0; b < Bands; b++)
        {
            var m = FoleyAudioAnalysis.Magnitude(_scopeSnap, BandHz(b), Fs);
            _band[b] = 20f * MathF.Log10(MathF.Max(m, 1e-6f));
        }
        _band.AsSpan().CopyTo(_bandSnap);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.ProceduralFoley" };
            _loop.Start();
        }
        LastAction = "▶ 절차적 폴리 60 Hz · 48 kHz · " + FoleyMaterials.Name(Floor) + " 바닥";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("ProceduralFoley.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ProceduralFoley.Runner.OnChange", ex); }
    }

    public void Dispose() { Stop(); Ragdoll.Dispose(); Dex.Dispose(); }
}
