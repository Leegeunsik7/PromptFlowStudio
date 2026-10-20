using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.GaussianFaceHand;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.MotionMatching;
using PromptFlowStudio.Services.NarrativeTimeline;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.AvatarActing;

/// <summary>
/// [PromptFlow Studio v17.60.0] 🎬 아바타 자율 연기 디렉터 스튜디오 러너(60 Hz · 320×192).
/// 프레임 = 대사 링(내장 문장 · v17.40 각본 대사) → 텍스트-투-제스처 트랜스포머(v17.32 더빙 F0/RMS 운율) → v17.33 모션 매칭 무대 보행(하체) →
/// 연기 IK 합성(FABRIK 상지 · 상/하체 레이어) → 해부학 관절 한계 가드 → v17.57 FK/DQS 전신 → v17.58 안면 52 블렌드쉐이프(감정 오프셋 + 음소) · 손가락 모양 → v17.55 래스터라이저. 연산 구간 무할당.
/// </summary>
public sealed class AvatarActingDirectorStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, BodyPoints = 20_000, EnvSplats = 20_000, FacePoints = RealtimeGaussianFacialBlendshapeEngine.DefaultCount, HandPoints = DexterousHandGaussianSkinningSynthesizer.DefaultCount;
    public const float Dt = 1f / Hz, StageHalf = 1.4f, Aspect = (float)RealtimeGaussianSplatRasterizerEngine.W / RealtimeGaussianSplatRasterizerEngine.H;
    public const int PreviewBytes = 54 + RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 3;
    public static readonly string[] DefaultLines =
    {
        "여러분, 오늘 우리 모두 함께 미래를 이야기해 봅시다!",
        "저는 처음에 이 아이디어가 정말 실패할 거라고 생각했습니다.",
        "하지만 당신이 바로 그 답을 찾아냈어요.",
        "그 제안은 정중히 거절하겠습니다. 아니요, 절대 안 됩니다.",
        "음... 글쎄요, 아마 다른 방법이 있을지도 모르겠네요?",
        "이것은 세상을 바꿀 가장 놀라운 결과입니다!",
    };

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _ikS = new float[Hist], _clampS = new float[Hist];
    private readonly float[] _w = new float[RealtimeGaussianFacialBlendshapeEngine.K];
    private readonly MultilingualDubbingLipSyncStudioRunner? _dub;
    private readonly NarrativeTimelineStudioRunner? _narr;
    private readonly Vector3 _fx, _fy, _fz, _faceC;
    private RealtimeNeuralMotionMatchingEngine? _mm;
    private GaussianSplatScene? _scene;
    private RealtimeDualQuaternionGaussianSkinningEngine? _body;
    private RealtimeGaussianFacialBlendshapeEngine? _face;
    private DexterousHandGaussianSkinningSynthesizer? _hands;
    private FacialSplatTearFingerIntersectionGuard? _fhGuard;
    private RealtimeGaussianSplatRasterizerEngine? _raster;
    private int _faceStart, _sHead, _sN, _line;
    private float _t, _dir = 1f, _heartFrom, _heartUntil = float.NegativeInfinity;
    private double _ms;
    private SplatCamera _cam;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public AvatarActingDirectorStudioRunner(IServiceProvider sp)
        : this(sp.GetService(typeof(MultilingualDubbingLipSyncStudioRunner)) as MultilingualDubbingLipSyncStudioRunner, sp.GetService(typeof(NarrativeTimelineStudioRunner)) as NarrativeTimelineStudioRunner) { }

    public AvatarActingDirectorStudioRunner(MultilingualDubbingLipSyncStudioRunner? dubbing = null, NarrativeTimelineStudioRunner? narrative = null)
    {
        _dub = dubbing; _narr = narrative;
        var rest = RagdollSkeleton.Rest;
        var side = Vector3.Normalize(rest[(int)RagdollJoint.RHip] - rest[(int)RagdollJoint.LHip]);
        _fy = Vector3.Normalize(rest[(int)RagdollJoint.HeadTop] - rest[(int)RagdollJoint.Head]);
        _fz = Vector3.Normalize(Vector3.Cross(side, _fy));
        _fx = Vector3.Cross(_fy, _fz);
        _faceC = (rest[(int)RagdollJoint.Head] + rest[(int)RagdollJoint.HeadTop]) * 0.5f;
    }

    public event Action? OnChange;
    public RealtimeLlmTextToGestureTransformer Gesture { get; } = new();
    public AutonomousAvatarActingIkSynthesizer Ik { get; } = new();
    public BiomechanicalJointLimitSafetyGuard Guard { get; } = new();
    public AvatarFkAnimator Animator { get; } = new();
    public VolumetricDeterminantClampingGuard BodyGuard { get; } = new();
    public FacialPhonemeRingTimeline Timeline { get; } = new();
    public SplatFrustumCullingVramBudgetGuard CullGuard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new() { Degree = 0 };
    public RealtimeNeuralMotionMatchingEngine? Locomotion => _mm;
    public RealtimeGaussianFacialBlendshapeEngine? Face => _face;
    public DexterousHandGaussianSkinningSynthesizer? Hands => _hands;
    public FacialSplatTearFingerIntersectionGuard? FaceHandGuard => _fhGuard;
    public RealtimeGaussianSplatRasterizerEngine? Raster => _raster;
    public GaussianSplatScene? Scene => _scene;
    public ActingPersona Persona => Gesture.Persona;
    public bool Walk { get; private set; } = true;
    public bool AutoScript { get; set; } = true;
    public bool UseDubbing { get; private set; }
    public bool UseScript { get; private set; }
    public bool DubbingAvailable => _dub is not null;
    public bool ScriptAvailable => _narr is not null;
    public float WalkSpeed { get; private set; }
    public float HandErrorL { get; private set; }
    public float HandErrorR { get; private set; }
    public int Points => BodyPoints + FacePoints + HandPoints;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float IkUsSeries(int i) => _ikS[Idx(i)];
    public float ClampSeries(int i) => _clampS[Idx(i)];

    public static string PersonaName(ActingPersona p) => PersonaSpec.Of(p).Name;

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureRig()
    {
        if (_scene is not null) return;
        _scene = new GaussianSplatScene(EnvSplats + BodyPoints + FacePoints + HandPoints);
        _scene.Generate(EnvSplats, 0xAC60u, centerpiece: false);
        var bs = _scene.Reserve(BodyPoints);
        var weights = new BoneWeights4[BodyPoints]; var normals = new Vector3[BodyPoints];
        var nb = GaussianAvatarRig.Build(_scene, bs, BodyPoints, weights, normals);
        for (var k = 0; k < nb; k++) if (weights[k].B0 == (int)RagdollJoint.HeadTop && Vector3.Dot(normals[k], _fz) > -0.25f) _scene.Opacity[bs + k] = 0f;
        _body?.Dispose(); _hands?.Dispose(); _raster?.Dispose();
        _body = new RealtimeDualQuaternionGaussianSkinningEngine(BodyPoints);
        _body.Bind(_scene, bs, nb, weights);
        _face = new RealtimeGaussianFacialBlendshapeEngine(FacePoints);
        _faceStart = _scene.Reserve(_face.Count);
        _face.WriteStatic(_scene, _faceStart);
        _hands = new DexterousHandGaussianSkinningSynthesizer(HandPoints);
        var hs = _scene.Reserve(HandPoints);
        _hands.Bind(_scene, hs, HandPoints);
        _fhGuard = new FacialSplatTearFingerIntersectionGuard(_face);
        _raster = new RealtimeGaussianSplatRasterizerEngine();
        _mm = new RealtimeNeuralMotionMatchingEngine();
        _mm.Reset(Vector3.Zero, 0f);
        Gesture.Reset(); _line = 0;
        WalkSpeed = Gesture.Spec.WalkSpeed;
    }

    public void Prepare() { lock (_gate) EnsureRig(); }

    public void SetPersona(ActingPersona p) { lock (_gate) { Gesture.Persona = p; WalkSpeed = Gesture.Spec.WalkSpeed; } LastAction = "🎭 성향 → " + PersonaName(p); Raise(); }

    public void SetWalk(bool on) { lock (_gate) Walk = on; LastAction = on ? "🚶 v17.33 무대 보행 레이어 ON" : "🧍 제자리(상체 연기만)"; Raise(); }

    public void SetWalkSpeed(float ms) { lock (_gate) WalkSpeed = Math.Clamp(ms, 0f, 2.5f); LastAction = "🚶 보행 속도 → " + WalkSpeed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s"; Raise(); }

    public void SetHallucination(bool on) { lock (_gate) Gesture.Hallucinate = on; LastAction = on ? "💥 LLM 환각 주입 ON(고개 180° · 손목 180° · 팔꿈치 과신전 · 척추 비틀림)" : "💥 환각 주입 OFF"; Raise(); }

    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 해부학 관절 한계 가드 ON" : "⛔ 관절 가드 OFF(비교)"; Raise(); }

    public void SetSlew(bool on) { lock (_gate) Guard.SlewEnabled = on; LastAction = on ? "🛡 각속도 14 rad/s 슬루 ON" : "⚡ 슬루 OFF"; Raise(); }

    public void SetIk(bool on) { lock (_gate) Ik.IkEnabled = on; LastAction = on ? "🦾 FABRIK 상지 IK ON" : "🦾 IK OFF(이동 레이어 팔)"; Raise(); }

    public void SetActingWeight(float w) { lock (_gate) Ik.ActingWeight = Math.Clamp(w, 0f, 1f); LastAction = "🎚 연기 레이어 가중치 → " + Ik.ActingWeight.ToString("0.00", CultureInfo.InvariantCulture); Raise(); }

    /// <summary>사용자 대사 즉시 적재.</summary>
    public void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate) { EnsureRig(); Gesture.Enqueue(text.AsSpan(0, Math.Min(text.Length, 400))); }
        LastAction = "🗣 대사 적재 · " + text.Trim();
        Raise();
    }

    /// <summary>v17.61 라이브 리액션 — 잠금 하 무할당 적재(선점 시 예약 대사 폐기).</summary>
    public void SayNow(ReadOnlySpan<char> line, bool interrupt)
    {
        if (line.IsEmpty) return;
        lock (_gate)
        {
            EnsureRig();
            if (interrupt) Gesture.Interrupt();
            Gesture.Enqueue(line.Length > 400 ? line[..400] : line);
        }
    }

    // ══════════════════════ v17.63.1 키네마틱 하트 포즈 ══════════════════════
    /// <summary>머리 위 하트(가슴 좌표계 손 목표 · 좌우 손 7 cm 간격 · 손가락 굴곡) — 0.2 s 램프 인/아웃으로 연기 레이어 위에 블렌드.</summary>
    public static readonly Vector3 HeartLeft = new(-0.07f, 0.56f, 0.14f), HeartRight = new(0.07f, 0.56f, 0.14f);
    public const float HeartSec = 1.6f, HeartRamp = 0.2f;
    public long HeartPoses { get; private set; }
    public bool HeartActive => _t < _heartUntil;

    /// <summary>v17.61 하트 리액션 → 전용 키네마틱 포즈 예약(진행 중이면 연장 · 무할당).</summary>
    public void TriggerHeartPose(float seconds = HeartSec)
    {
        lock (_gate)
        {
            if (_t >= _heartUntil) _heartFrom = _t;
            _heartUntil = _t + Math.Clamp(seconds, 0.5f, 5f);
            HeartPoses++;
        }
    }

    /// <summary>제스처 프레임에 하트 포즈 오버레이(손 목표 · 가중치 · 손 모양 · 고개) — 순수 함수.</summary>
    public static void ApplyHeartPose(ref GestureFrame f, float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        if (k <= 0f) return;
        f.LeftTarget = Vector3.Lerp(f.LeftWeight > 1e-3f ? f.LeftTarget : HeartLeft, HeartLeft, k);
        f.RightTarget = Vector3.Lerp(f.RightWeight > 1e-3f ? f.RightTarget : HeartRight, HeartRight, k);
        f.LeftWeight += (1f - f.LeftWeight) * k; f.RightWeight += (1f - f.RightWeight) * k;
        f.HeadPitch += (0.08f - f.HeadPitch) * k; f.SpineTwist *= 1f - k; f.WristTwistL *= 1f - k; f.WristTwistR *= 1f - k;
        if (k >= 0.5f) { f.LeftShape = HandShape.Relaxed; f.RightShape = HandShape.Relaxed; }
    }

    public void SetDubbing(bool on)
    {
        if (on && _dub is null) return;
        lock (_gate) UseDubbing = on;
        LastAction = on ? "🎙 v17.32 더빙 F0/RMS 운율 연동" : "🗣 계획 운율(내장)";
        Raise();
    }

    public void SetScript(bool on)
    {
        if (on && _narr is null) return;
        lock (_gate)
        {
            EnsureRig();
            UseScript = on; Gesture.Reset(); _line = 0;
            if (on) Gesture.EnqueueScript(_narr!.Sequencer);
        }
        LastAction = on ? "📜 v17.40 각본 대사 " + Gesture.Lines + " 줄 적재" : "📜 내장 문장";
        Raise();
    }

    // ══════════════════════ 프레임 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureRig();
            using var zg = ZeroGcRegionScope.Enter("Acting.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        _t += Dt; _ms += Dt * 1000.0;
        var body = _body!; var face = _face!; var sc = _scene!; var mm = _mm!;
        // ① 대사 · 운율 · 트랜스포머
        if (AutoScript && Gesture.Clock >= Gesture.ScheduleEndS - 0.05)
        {
            if (!UseScript || _narr is null || Gesture.EnqueueScript(_narr.Sequencer) == 0) Gesture.Enqueue(DefaultLines[_line++ % DefaultLines.Length]);
        }
        if (UseDubbing && _dub is not null && _dub.IsRunning && _dub.Engine.IsSpeaking(0)) Gesture.PushProsody(_dub.Engine.CloneF0(0), _dub.Engine.Output(0).LastRms);
        else Gesture.PushProsody(0f, 0f);
        Gesture.Tick(Dt);
        var gf = Gesture.Current;   // v17.63.1 값 복사(무할당) — 하트 포즈 오버레이 대상
        if (_t < _heartUntil) ApplyHeartPose(ref gf, MathF.Min(_t - _heartFrom, _heartUntil - _t) / HeartRamp);
        // ② v17.33 하체 이동(무대 왕복 보행)
        var speed = Walk ? WalkSpeed : 0f;
        var px = mm.Position.X;
        if (px > StageHalf) _dir = -1f; else if (px < -StageHalf) _dir = 1f;
        var yaw = speed > 0.01f ? _dir * MathF.PI * 0.5f : 0f;
        mm.Controller.SetDesired(BipedSkeleton.Forward(yaw) * speed, yaw);
        mm.Step();
        Ik.SetLocomotion(mm.Pose, mm.Position, mm.Heading);
        // ③ 연기 IK · 관절 가드 · FK
        Ik.Synthesize(in gf);
        Guard.Apply(Ik.LocalMutable, Dt);
        var loc = Ik.Local;
        for (var j = 0; j < RagdollSkeleton.Count; j++) Animator.SetLocal(j, loc[j]);
        Animator.Root = Ik.AnimatorRoot; Animator.Yaw = Ik.Yaw;
        Animator.Solve(0f, body);
        body.Skin(BodyGuard);
        var joints = Animator.Positions;
        HandErrorL = gf.LeftWeight * Ik.ActingWeight >= 0.95f ? Vector3.Distance(joints[(int)RagdollJoint.LHand], Ik.Target(0)) : 0f;
        HandErrorR = gf.RightWeight * Ik.ActingWeight >= 0.95f ? Vector3.Distance(joints[(int)RagdollJoint.RHand], Ik.Target(1)) : 0f;
        // ④ 안면(감정 오프셋 + 음소) · 손가락 모양
        Gesture.FaceOffsets.CopyTo(_w);
        if (gf.Speaking) Timeline.Evaluate(_ms, _w);
        face.SetWeights(_w);
        face.Blend();
        _fhGuard!.ApplyFace(face);
        var rh = body.BoneRotation((int)RagdollJoint.HeadTop);
        var fm = new Matrix4x4(_fx.X, _fx.Y, _fx.Z, 0f, _fy.X, _fy.Y, _fy.Z, 0f, _fz.X, _fz.Y, _fz.Z, 0f, 0f, 0f, 0f, 1f) * Matrix4x4.CreateFromQuaternion(rh);
        var head = joints[(int)RagdollJoint.Head];
        var ft = Vector3.Transform(_faceC - RagdollSkeleton.Rest[(int)RagdollJoint.Head], rh) + head;
        face.Commit(sc, _faceStart, in fm, ft);
        ApplyShape(_hands!, 0, gf.LeftShape); ApplyShape(_hands!, 1, gf.RightShape);
        _hands!.Step(body, joints, Dt);
        _fhGuard.ApplyHands(_hands, sc);
        // ⑤ 카메라 · 렌더
        var chest = joints[(int)RagdollJoint.Chest];
        var orbit = 0.3f * MathF.Sin(_t * 0.2f);
        var target = chest + new Vector3(0f, -0.1f, 0f);
        _cam = SplatCamera.LookAt(target + new Vector3(2.6f * MathF.Sin(orbit), 0.15f, 2.6f * MathF.Cos(orbit)), target, 42f, Aspect, 0.05f, 120f);
        _raster!.Render(sc, in _cam, CullGuard, Sh);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        _msS[_sHead] = (float)ms; _ikS[_sHead] = (float)Ik.LastIkUs; _clampS[_sHead] = Guard.FrameClamps;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    /// <summary>손 모양 → v17.58 손가락 굴곡(MCP · PIP · 외전) 직접 지정.</summary>
    public static void ApplyShape(DexterousHandGaussianSkinningSynthesizer h, int hand, HandShape s)
    {
        for (var f = 0; f < DexterousHandGaussianSkinningSynthesizer.Fingers; f++)
        {
            var thumb = f == 0;
            (float m, float p, float a) = s switch
            {
                HandShape.Open => thumb ? (0.05f, 0.05f, 0f) : (0.05f, 0.05f, f == 1 ? 0.12f : f == 4 ? 0.14f : 0.05f),
                HandShape.Palm => (0f, 0f, 0f),
                HandShape.Point => f == 1 ? (0.02f, 0.02f, 0f) : thumb ? (0.6f, 0.8f, 0f) : (1.45f, 1.6f, 0f),
                HandShape.Fist => thumb ? (0.6f, 0.8f, 0f) : (1.45f, 1.6f, 0f),
                _ => thumb ? (0.2f, 0.2f, 0f) : (0.25f + 0.05f * f, 0.35f + 0.05f * f, 0f),
            };
            h.SetFinger(hand, f, m, p, a);
        }
    }

    /// <summary>월드 점 → 프리뷰(320×192) 좌표.</summary>
    public bool Project(Vector3 p, out float x, out float y)
    {
        var d = p - _cam.Position;
        var z = Vector3.Dot(d, _cam.Forward);
        x = y = 0f;
        if (z < 0.05f) return false;
        x = RealtimeGaussianSplatRasterizerEngine.W * 0.5f * (1f + Vector3.Dot(d, _cam.Right) / (z * _cam.TanHalfH));
        y = RealtimeGaussianSplatRasterizerEngine.H * 0.5f * (1f - Vector3.Dot(d, _cam.Up) / (z * _cam.TanHalfV));
        return true;
    }

    public string Summary()
    {
        var g = Guard;
        return RealtimeLlmTextToGestureTransformer.ClassName(Gesture.Current.Class) + " · 클램프 " + g.Clamps.ToString(CultureInfo.InvariantCulture) + " · 최대 이탈 " + g.PeakExcessDeg.ToString("0.0", CultureInfo.InvariantCulture) + "° · 잔여 " + g.ResidualDeg.ToString("0.000", CultureInfo.InvariantCulture) + "° · 환각 " + Gesture.Hallucinations.ToString(CultureInfo.InvariantCulture);
    }

    public void WritePreviewBmp(byte[] dst) { lock (_gate) _raster?.WriteBmp(dst, false); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Acting" };
            _loop.Start();
        }
        LastAction = "▶ 아바타 자율 연기 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Acting.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Acting.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) { _body?.Dispose(); _hands?.Dispose(); _raster?.Dispose(); }
    }
}
