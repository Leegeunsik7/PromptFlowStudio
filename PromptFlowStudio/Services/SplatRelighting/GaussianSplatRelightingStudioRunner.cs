using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.ColorScience;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.StageLighting;
namespace PromptFlowStudio.Services.SplatRelighting;

/// <summary>
/// [PromptFlow Studio v17.56.0] 🌓 3DGS 섀도우 &amp; 뉴럴 릴라이팅 스튜디오 러너(60 Hz · 320×192).
/// 프레임 = 배우 6DoF 포즈(원 궤도 보행) → 배우 스플랫 1 500(15 구 표면 · 키 + 환경 앰비언트/림/바운스 셰이딩) 갱신 →
/// 키 라이트 뎁스 맵 → v17.55 래스터라이저(변조기 = 접지 그림자 PCSS + 배우 바운스) → 그림자 레이턴시 가드 → 환경광 역추출.
/// 연산 구간 무할당 · 결선(디지털 휴먼 피부 셰이더 6 프레임 주기 · DIT 키 CCT 1 s / 150 K 히스테리시스 · v17.41 키 픽스처 방향)은 구간 밖.
/// 장면 · 엔진은 첫 프레임에 지연 생성(기본 20 만 스플랫 · 중앙 광택 구체 없음 + 반경 3 m 무대 바닥 3 만).
/// </summary>
public sealed class GaussianSplatRelightingStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, DefaultSplats = 200_000, StageFloorSplats = 30_000, PerSphere = 100, ActorSplats = RealtimeSplatShadowCastingEngine.Spheres * PerSphere;
    public const float Dt = 1f / Hz, Aspect = (float)RealtimeGaussianSplatRasterizerEngine.W / RealtimeGaussianSplatRasterizerEngine.H;
    public const int PreviewBytes = 54 + RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 3, DepthPreviewBytes = 54 + 128 * 128 * 3;
    private static readonly Vector3[] Fib = BuildFib();
    private static readonly Vector3[] Albedo =
    {
        new(0.80f, 0.58f, 0.48f), new(0.78f, 0.56f, 0.46f), new(0.22f, 0.30f, 0.48f), new(0.22f, 0.30f, 0.48f), new(0.16f, 0.17f, 0.22f),
        new(0.22f, 0.30f, 0.48f), new(0.22f, 0.30f, 0.48f), new(0.78f, 0.56f, 0.46f), new(0.78f, 0.56f, 0.46f),
        new(0.16f, 0.17f, 0.22f), new(0.16f, 0.17f, 0.22f), new(0.16f, 0.17f, 0.22f), new(0.16f, 0.17f, 0.22f), new(0.1f, 0.08f, 0.07f), new(0.1f, 0.08f, 0.07f),
    };

    private readonly object _gate = new();
    private readonly float[] _shS = new float[Hist], _msS = new float[Hist], _lumS = new float[Hist];
    private readonly DigitalHumanPerformanceStudioRunner? _human;
    private readonly LiveDitColorStudioRunner? _dit;
    private readonly IntelligentStageLightingStudioRunner? _lights;
    private readonly DualQuaternionGaussianAvatarStudioRunner? _avatar;
    private readonly Vector3[] _skel = new Vector3[RagdollSkeleton.Count];
    private GaussianSplatScene? _scene;
    private RealtimeGaussianSplatRasterizerEngine? _engine;
    private int _actorStart = -1, _sHead, _sN;
    private float _orbit, _walk, _camOrbit = 0.6f, _lastDitCct;
    private double _lastDitT = double.NegativeInfinity, _t;
    private SplatActorPose _pose;
    private SplatCamera _cam;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public GaussianSplatRelightingStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(DigitalHumanPerformanceStudioRunner)) as DigitalHumanPerformanceStudioRunner,
        sp.GetService(typeof(LiveDitColorStudioRunner)) as LiveDitColorStudioRunner,
        sp.GetService(typeof(IntelligentStageLightingStudioRunner)) as IntelligentStageLightingStudioRunner,
        sp.GetService(typeof(DualQuaternionGaussianAvatarStudioRunner)) as DualQuaternionGaussianAvatarStudioRunner) { }

    public GaussianSplatRelightingStudioRunner(DigitalHumanPerformanceStudioRunner? human = null, LiveDitColorStudioRunner? dit = null, IntelligentStageLightingStudioRunner? lights = null,
        DualQuaternionGaussianAvatarStudioRunner? avatar = null)
    {
        _human = human; _dit = dit; _lights = lights; _avatar = avatar;
        LinkAvatar = avatar is not null;
        Shadow = new RealtimeSplatShadowCastingEngine(ShadowGuard);
        _pose = new SplatActorPose { Root = new Vector3(1f, 0f, 0f), Rotation = Quaternion.Identity, Height = 1.8f };
        Shadow.SetActor(_pose);
    }

    private static Vector3[] BuildFib()
    {
        var a = new Vector3[PerSphere]; var ga = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var j = 0; j < PerSphere; j++) { var z = 1f - (2f * j + 1f) / PerSphere; var r = MathF.Sqrt(1f - z * z); a[j] = new Vector3(r * MathF.Cos(ga * j), z, r * MathF.Sin(ga * j)); }
        return a;
    }

    public event Action? OnChange;
    public SplatFrustumCullingVramBudgetGuard CullGuard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new();
    public AdaptiveDepthBiasShadowGuard ShadowGuard { get; } = new();
    public RealtimeSplatShadowCastingEngine Shadow { get; }
    public SplatEnvironmentMutualRelightingSynthesizer Relight { get; } = new();
    public RealtimeGaussianSplatRasterizerEngine? Engine => _engine;
    public GaussianSplatScene? Scene => _scene;
    public int ActorStart => _actorStart;
    public int SplatCount => _scene?.Count ?? 0;
    public int VisibleSplats => _engine?.Visible ?? 0;
    public ref readonly SplatActorPose Pose => ref _pose;
    public ref readonly SplatCamera Camera => ref _cam;
    public Vector3 KeyColor { get; private set; } = new(1.1f, 1.02f, 0.92f);
    public float ActorSpeed { get; private set; } = 0.35f;
    public bool LinkHuman { get; private set; }
    public bool LinkDit { get; private set; }
    public bool LinkLights { get; private set; }
    public bool HumanAvailable => _human is not null;
    /// <summary>v17.63.1 — v17.57 DQS 아바타 24 관절로 15 구 섀도우 계층 구동(아바타 미가동 · 관절 비유한 시 간이 보행 프록시 폴백).</summary>
    public bool LinkAvatar { get; private set; }
    public bool AvatarAvailable => _avatar is not null;
    public long SkeletonFrames { get; private set; }
    public bool DitAvailable => _dit is not null;
    public bool LightsAvailable => _lights is not null;
    public long HumanPushes { get; private set; }
    public long DitPushes { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float ShadowMsSeries(int i) => _shS[Idx(i)];
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float LumSeries(int i) => _lumS[Idx(i)];

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureScene()
    {
        if (_scene is not null) return;
        _scene = new GaussianSplatScene(DefaultSplats + StageFloorSplats + ActorSplats);
        _scene.Generate(DefaultSplats, 0x51A7u, centerpiece: false);
        // 배우 무대 바닥(반경 3 m · 약 1 000/m² 원목) — 접지 그림자 · 바운스 수용면
        var rng = 0x2F6Bu;
        for (var k = 0; k < StageFloorSplats; k++)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; var u1 = (rng >> 8) * (1f / 16777216f);
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; var u2 = (rng >> 8) * (1f / 16777216f);
            var r = 3f * MathF.Sqrt(u1); var a = MathF.Tau * u2;
            var p = new Vector3(r * MathF.Cos(a), 0.002f, r * MathF.Sin(a));
            var grain = 0.9f + 0.2f * MathF.Sin(p.X * 23f + MathF.Sin(p.Z * 3f) * 2f);
            _scene.Add(p, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.035f, 0.004f, 0.035f), 0.95f, new Vector3(0.55f, 0.42f, 0.30f) * grain, RealtimeSplatShadowCastingEngine.GroundKind);
        }
        _actorStart = _scene.Reserve(ActorSplats);
        _engine = new RealtimeGaussianSplatRasterizerEngine { Modulator = Shadow };
    }

    public void SetOpacity(float v) { lock (_gate) Shadow.Opacity = Math.Clamp(v, 0f, 1f); Raise(); }
    public void SetSoftness(float v) { lock (_gate) Shadow.Softness = Math.Clamp(v, 0f, 3f); Raise(); }
    public void SetReflection(float v) { lock (_gate) { Relight.Gain = Math.Clamp(v, 0f, 2f); Shadow.BleedStrength = 0.18f * Relight.Gain; } Raise(); }
    public void SetPcfRadius(int r) { lock (_gate) ShadowGuard.UserRadius = r; LastAction = "🔲 PCF 반경 " + ShadowGuard.UserRadius.ToString(CultureInfo.InvariantCulture) + " 텍셀"; Raise(); }
    public void SetBiasScale(float v) { lock (_gate) ShadowGuard.BiasScale = Math.Clamp(v, 0f, 40f); Raise(); }
    public void SetGuard(bool on) { lock (_gate) { ShadowGuard.Enabled = on; ShadowGuard.Reset(); } LastAction = on ? "🛡 슬로프 바이어스 상한 · 3.5 ms 레이턴시 가드 ON" : "⛔ 섀도우 가드 OFF(아크네/피터 패닝 · 지연 비교)"; Raise(); }
    public void SetShadows(bool on) { lock (_gate) Shadow.Enabled = on; Raise(); }
    public void SetActorSpeed(float v) { lock (_gate) ActorSpeed = Math.Clamp(v, 0f, 1.5f); Raise(); }
    public void SetLinkHuman(bool on) { lock (_gate) LinkHuman = on && _human is not null; LastAction = LinkHuman ? "🧑 디지털 휴먼 피부 셰이더 환경광 주입" : "🧑 셰이더 결선 해제"; Raise(); }
    public void SetLinkDit(bool on) { lock (_gate) { LinkDit = on && _dit is not null; _lastDitT = double.NegativeInfinity; } LastAction = LinkDit ? "🎨 DIT 키 CCT = 환경 색온도" : "🎨 DIT 결선 해제"; Raise(); }
    public void SetLinkLights(bool on) { lock (_gate) LinkLights = on && _lights is not null; LastAction = LinkLights ? "💡 v17.41 키 픽스처 방향 추종" : "💡 기본 키 라이트"; Raise(); }

    /// <summary>테스트 · 외부 포즈 주입(6DoF) — 다음 프레임부터 반영.</summary>
    public void SetLinkAvatar(bool on) { lock (_gate) LinkAvatar = on && _avatar is not null; LastAction = LinkAvatar ? "🦴 DQS 아바타 골격 → 그림자 계층 결선" : "🦴 골격 결선 해제(15 구 프록시)"; Raise(); }
    public void SetPose(in SplatActorPose pose) { lock (_gate) { _pose = pose; ActorSpeed = 0f; } }

    // ══════════════════════ 프레임 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureScene();
            {
                using var zg = ZeroGcRegionScope.Enter("SplatRelight.Step");
                Compute();
            }
            Dispatch();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        _t += Dt;
        if (ActorSpeed > 0f)
        {
            _orbit += ActorSpeed * Dt; _walk += ActorSpeed * Dt * 7f;
            _pose.Root = new Vector3(1.1f * MathF.Cos(_orbit), 0f, 1.1f * MathF.Sin(_orbit));
            _pose.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -_orbit);
            _pose.WalkPhase = _walk;
        }
        _camOrbit += 0.12f * Dt;
        if (!(LinkAvatar && _avatar is not null && _avatar.CopyJoints(_skel) && Shadow.SetActorSkeleton(_skel, _pose))) Shadow.SetActor(_pose);
        else SkeletonFrames++;
        var center = Shadow.ActorCenter + new Vector3(0f, 0.25f, 0f);
        _cam = SplatCamera.LookAt(center + new Vector3(3.6f * MathF.Sin(_camOrbit), 0.35f, 3.6f * MathF.Cos(_camOrbit)), center, 45f, Aspect, 0.1f, 120f);
        if (LinkLights && _lights is not null) KeyFromStage(_lights, center);
        else Shadow.LightPosition = center + new Vector3(2.4f, 3.6f, 1.8f);
        WriteActorSplats();
        Shadow.BuildDepthMap();
        _engine!.Render(_scene!, in _cam, CullGuard, Sh);
        Relight.Step(_scene!, Sh, center, _cam.Position);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        _shS[_sHead] = Shadow.LastShadowMs; _msS[_sHead] = (float)ms; _lumS[_sHead] = Relight.Luminance;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    /// <summary>v17.41 키 픽스처(역할 Key) 조준 방향 → 배우 기준 5.5 m 거리 키 라이트 · 색.</summary>
    private void KeyFromStage(IntelligentStageLightingStudioRunner li, Vector3 center)
    {
        for (var i = 0; i < RealtimeIntelligentStageLightingEngine.Fixtures; i++)
        {
            ref readonly var f = ref li.Engine.Fixture(i);
            if (f.Role != LightRole.Key) continue;
            var dir = f.Position - f.Aim;
            if (dir.LengthSquared() < 1e-4f) continue;
            dir = Vector3.Normalize(dir);
            if (dir.Y < 0.25f) dir = Vector3.Normalize(new Vector3(dir.X, 0.25f, dir.Z));
            Shadow.LightPosition = center + dir * 5.5f;
            if (f.Color.LengthSquared() > 1e-4f) KeyColor = f.Color * 1.1f;
            return;
        }
        Shadow.LightPosition = center + new Vector3(2.4f, 3.6f, 1.8f);
    }

    /// <summary>배우 스플랫 갱신(무할당) — 구 표면 · 법선 평면 원반 · 키(그림자 없음) + 환경 앰비언트/림/바운스.</summary>
    private void WriteActorSplats()
    {
        var s = _scene!; var sph = Shadow.ActorSpheres;
        var amb = Relight.Ambient; var rim = Relight.Rim; var rimDir = Relight.RimDir; var bounce = Relight.Bounce;
        var key = KeyColor;
        for (var k = 0; k < RealtimeSplatShadowCastingEngine.Spheres; k++)
        {
            var c = new Vector3(sph[k].X, sph[k].Y, sph[k].Z); var r = sph[k].W;
            var L = Vector3.Normalize(Shadow.LightPosition - c);
            var alb = Albedo[k];
            for (var j = 0; j < PerSphere; j++)
            {
                var n = Fib[j];
                var t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, n)); var b = Vector3.Cross(n, t);
                var e = amb + key * MathF.Max(0f, Vector3.Dot(n, L)) + rim * MathF.Max(0f, Vector3.Dot(n, rimDir)) + bounce * MathF.Max(0f, -n.Y);
                var col = new Vector3(alb.X * e.X, alb.Y * e.Y, alb.Z * e.Z);
                s.Write(_actorStart + k * PerSphere + j, c + n * r, t, b, n, new Vector3(r * 0.32f, r * 0.32f, r * 0.05f), 0.97f, col, RealtimeSplatShadowCastingEngine.ActorKind);
            }
        }
    }

    /// <summary>결선(구간 밖 · 외부 러너 잠금) — 환경 → 배우 셰이더 · DIT.</summary>
    private void Dispatch()
    {
        try
        {
            if (LinkHuman && _human is not null && _tick % 6 == 0) { _human.ApplyEnvironmentLight(Relight.Ambient, Relight.Rim, Relight.RimDir, Relight.Bounce); HumanPushes++; }
            if (LinkDit && _dit is not null && _t - _lastDitT >= 1.0 && MathF.Abs(Relight.CctK - _lastDitCct) >= 150f)
            {
                _lastDitT = _t; _lastDitCct = Relight.CctK;
                _dit.SetKeyCct(Relight.CctK); DitPushes++;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { PromptFlowStudio.Program.Log("SplatRelight.Dispatch", ex); }
    }

    public void WritePreviewBmp(byte[] dst) { lock (_gate) _engine?.WriteBmp(dst, false); }
    public void WriteDepthBmp(byte[] dst) { lock (_gate) Shadow.WriteDepthBmp(dst); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SplatRelight" };
            _loop.Start();
        }
        LastAction = "▶ 3DGS 섀도우 & 릴라이팅 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SplatRelight.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SplatRelight.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _engine?.Dispose();
    }
}
