using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.MotionMatching;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SkinnedSplatAvatar;

/// <summary>
/// [PromptFlow Studio v17.57.0] 🧬 4D 가우시안 아바타 스튜디오 러너(60 Hz · 320×192 · v17.20 <c>GaussianAvatar</c> 와 별도).
/// 프레임 = 모션 소스(내장 FK 격투/점프/팔 들기/전완 비틀림 · v17.33 모션 매칭 18 → 24 매핑 · v17.37 래그돌 24) → 본 DQ →
/// DQS 스키닝(+ 체적 보존 가드) → 셀프 섀도우 → v17.55 래스터라이저(변조기 = 셀프 섀도우). 연산 구간 무할당.
/// 글리치 주입(래그돌 폭발 모사): 1.5 s 마다 8 프레임간 임의 분절 신장 ×3.5 / 압축 ×0.3 · 관절 NaN.
/// </summary>
public sealed class DualQuaternionGaussianAvatarStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, DefaultPoints = 30_000, EnvSplats = 60_000;
    public const float Dt = 1f / Hz, Aspect = (float)RealtimeGaussianSplatRasterizerEngine.W / RealtimeGaussianSplatRasterizerEngine.H;
    public const int PreviewBytes = 54 + RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 3;
    public static readonly int[] PointPresets = { 10_000, 30_000, 60_000 };

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _skS = new float[Hist], _clS = new float[Hist];
    private readonly Vector3[] _joints = new Vector3[GaussianAvatarRig.Joints], _biped = new Vector3[BipedSkeleton.Count];
    private readonly NeuralMotionMatchingStudioRunner? _mm;
    private readonly NeuralRagdollStudioRunner? _rag;
    private GaussianSplatScene? _scene;
    private RealtimeDualQuaternionGaussianSkinningEngine? _skin;
    private GaussianAvatarSelfShadowingSynthesizer? _shadow;
    private RealtimeGaussianSplatRasterizerEngine? _raster;
    private int _points = DefaultPoints, _bound, _sHead, _sN;
    private float _t, _camOrbit = 0.5f;
    private uint _rng = 0x5EEDu;
    private SplatCamera _cam;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public DualQuaternionGaussianAvatarStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(NeuralMotionMatchingStudioRunner)) as NeuralMotionMatchingStudioRunner,
        sp.GetService(typeof(NeuralRagdollStudioRunner)) as NeuralRagdollStudioRunner) { }

    public DualQuaternionGaussianAvatarStudioRunner(NeuralMotionMatchingStudioRunner? motion = null, NeuralRagdollStudioRunner? ragdoll = null)
    { _mm = motion; _rag = ragdoll; }

    public event Action? OnChange;
    public AvatarFkAnimator Animator { get; } = new();
    public VolumetricDeterminantClampingGuard Guard { get; } = new();
    public SplatFrustumCullingVramBudgetGuard CullGuard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new() { Degree = 0 };
    public RealtimeDualQuaternionGaussianSkinningEngine? Skinning => _skin;
    public GaussianAvatarSelfShadowingSynthesizer? SelfShadow => _shadow;
    public RealtimeGaussianSplatRasterizerEngine? Raster => _raster;
    public GaussianSplatScene? Scene => _scene;
    public AvatarMotion Motion { get; private set; } = AvatarMotion.Fight;
    public int Points => _bound;
    public int PointTarget => _points;
    public bool Glitch { get; private set; }
    public bool UseDqs { get; private set; } = true;
    public bool ShowBones { get; set; } = true;
    public float ShadowOpacity => _shadow?.Opacity ?? 0.75f;
    public bool MotionAvailable => _mm is not null;
    public bool RagdollAvailable => _rag is not null;
    public ReadOnlySpan<Vector3> Joints => _joints;

    /// <summary>v17.63.1 — 최신 관절(24) 스냅숏을 게이트 안에서 복사(타 러너 섀도우 결선용 · 무할당). 첫 틱 전이면 false.</summary>
    public bool CopyJoints(Span<Vector3> dst)
    {
        if (dst.Length < _joints.Length) return false;
        lock (_gate)
        {
            if (Interlocked.Read(ref _tick) == 0) return false;
            _joints.CopyTo(dst);
            return true;
        }
    }
    public ref readonly SplatCamera Camera => ref _cam;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float SkinMsSeries(int i) => _skS[Idx(i)];
    public float ClampSeries(int i) => _clS[Idx(i)];

    public static string MotionName(AvatarMotion m) => m switch
    {
        AvatarMotion.Fight => "🥊 격투 콤보", AvatarMotion.Jump => "🦘 점프", AvatarMotion.ArmsRaise => "🙆 팔 들기", AvatarMotion.TwistStress => "🌀 전완 비틀림 ±170°",
        AvatarMotion.MotionMatching => "🏃 v17.33 모션 매칭", _ => "🤸 v17.37 래그돌",
    };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureRig()
    {
        if (_scene is not null) return;
        _scene = new GaussianSplatScene(EnvSplats + _points);
        _scene.Generate(EnvSplats, 0xD0A1u, centerpiece: false);
        var start = _scene.Reserve(_points);
        var weights = new BoneWeights4[_points]; var normals = new Vector3[_points];
        _bound = GaussianAvatarRig.Build(_scene, start, _points, weights, normals);
        _skin?.Dispose(); _shadow?.Dispose(); _raster?.Dispose();
        _skin = new RealtimeDualQuaternionGaussianSkinningEngine(_points) { UseDqs = UseDqs };
        _skin.Bind(_scene, start, _bound, weights);
        var op = _shadow?.Opacity ?? 0.75f;
        _shadow = new GaussianAvatarSelfShadowingSynthesizer(_points, normals) { Opacity = op };
        _raster = new RealtimeGaussianSplatRasterizerEngine { Modulator = _shadow };
    }

    public void SetPoints(int n)
    {
        lock (_gate) { _points = Math.Clamp(n, 2_000, 120_000); _scene = null; EnsureRig(); Guard.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "🧬 바인드 " + _bound.ToString("N0", CultureInfo.InvariantCulture) + " 가우시안(23 분절 캡슐 표면)";
        Raise();
    }

    public void SetMotion(AvatarMotion m)
    {
        if (m == AvatarMotion.MotionMatching && _mm is null || m == AvatarMotion.Ragdoll && _rag is null) return;
        lock (_gate) { Motion = m; _t = 0f; }
        LastAction = "🎬 모션 → " + MotionName(m);
        Raise();
    }

    public void SetDqs(bool on) { lock (_gate) { UseDqs = on; if (_skin is not null) _skin.UseDqs = on; } LastAction = on ? "🧬 듀얼 쿼터니언 스키닝(DQS)" : "📐 선형 블렌드 스키닝(LBS · 캔디 래퍼 비교)"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 체적 보존 가드 ON(det 0.5~2.5×)" : "⛔ 체적 가드 OFF(폭발 비교)"; Raise(); }
    public void SetGlitch(bool on) { lock (_gate) Glitch = on; LastAction = on ? "💥 글리치 주입 ON(신장 ×3.5 · 압축 ×0.3 · NaN)" : "💥 글리치 OFF"; Raise(); }
    public void SetShadowOpacity(float v) { lock (_gate) { EnsureRig(); _shadow!.Opacity = Math.Clamp(v, 0f, 1f); } Raise(); }
    public void SetSelfShadow(bool on) { lock (_gate) { EnsureRig(); _shadow!.Enabled = on; } Raise(); }
    public void SetLightAzimuth(float rad) { lock (_gate) { EnsureRig(); _shadow!.LightDir = Vector3.Normalize(new Vector3(0.45f * MathF.Sin(rad), 0.85f, 0.45f * MathF.Cos(rad))); } Raise(); }

    // ══════════════════════ 프레임 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureRig();
            if (Motion == AvatarMotion.MotionMatching && _mm is not null) { _mm.CopyPose(_biped); RagdollSkeleton.MapBiped(_biped, _joints); }
            else if (Motion == AvatarMotion.Ragdoll && _rag is not null) _rag.CopyPose(_joints);
            using var zg = ZeroGcRegionScope.Enter("DqAvatar.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        _t += Dt;
        var skin = _skin!;
        if (Motion is AvatarMotion.MotionMatching or AvatarMotion.Ragdoll) GaussianAvatarRig.SolveFromPositions(_joints, skin);
        else { Animator.Evaluate(Motion, _t, skin); Animator.Positions.CopyTo(_joints); }
        if (Glitch && _t % 1.5f < 8 * Dt) InjectGlitch(skin);
        skin.Skin(Guard);
        _shadow!.Compute(skin, _scene!, _joints);
        _camOrbit += 0.15f * Dt;
        var c = _joints[(int)RagdollJoint.Chest] * 0.5f + _joints[(int)RagdollJoint.Pelvis] * 0.5f;
        _cam = SplatCamera.LookAt(c + new Vector3(2.8f * MathF.Sin(_camOrbit), 0.25f, 2.8f * MathF.Cos(_camOrbit)), c, 45f, Aspect, 0.1f, 120f);
        _raster!.Render(_scene!, in _cam, CullGuard, Sh);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        _msS[_sHead] = (float)ms; _skS[_sHead] = (float)(skin.LastSkinUs / 1000.0); _clS[_sHead] = Guard.FrameClamps;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    /// <summary>래그돌 폭발 모사 — 임의 분절 2 개 신장/압축 · 1 개 비유한 회전.</summary>
    private void InjectGlitch(RealtimeDualQuaternionGaussianSkinningEngine skin)
    {
        var rest = RagdollSkeleton.Rest;
        for (var k = 0; k < 3; k++)
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            var b = 1 + (int)(_rng % (GaussianAvatarRig.Bones - 1));
            var pa = RagdollSkeleton.Parent[b];
            var rot = skin.BoneRotation(b);
            var pivot = _joints[pa];
            if (k == 2) skin.SetBone(b, new Quaternion(float.NaN, 0f, 0f, 1f), rest[pa], pivot);
            else skin.SetBone(b, rot, rest[pa], pivot, k == 0 ? 3.5f : 0.3f, rest[b] - rest[pa]);
        }
    }

    /// <summary>관절 j 화면 좌표(프리뷰 320×192 · 뒤면 false).</summary>
    public bool ProjectJoint(int j, out float x, out float y)
    {
        var d = _joints[j] - _cam.Position;
        var z = Vector3.Dot(d, _cam.Forward);
        x = y = 0f;
        if (z < 0.05f) return false;
        x = RealtimeGaussianSplatRasterizerEngine.W * 0.5f * (1f + Vector3.Dot(d, _cam.Right) / (z * _cam.TanHalfH));
        y = RealtimeGaussianSplatRasterizerEngine.H * 0.5f * (1f - Vector3.Dot(d, _cam.Up) / (z * _cam.TanHalfV));
        return true;
    }

    public void WritePreviewBmp(byte[] dst) { lock (_gate) _raster?.WriteBmp(dst, false); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.DqAvatar" };
            _loop.Start();
        }
        LastAction = "▶ 4D 가우시안 아바타 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("DqAvatar.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("DqAvatar.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) { _skin?.Dispose(); _shadow?.Dispose(); _raster?.Dispose(); }
    }
}
