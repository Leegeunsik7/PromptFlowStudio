using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.GaussianFaceHand;

public enum FaceExpression : byte { Neutral, Joy, Surprise, Anger, Sadness, Speech }

/// <summary>
/// [PromptFlow Studio v17.58.0] 😊 4D 가우시안 페이셜 &amp; 핑거 스튜디오 러너(60 Hz · 320×192).
/// 프레임 = v17.57 DQS 전신(가드 자세 · 머리 끄덕임) → 안면 52 블렌드쉐이프(수동 슬라이더 + 표정 프리셋 + 음소 링버퍼 + 자동 깜빡임)
/// → 안면 가드(찢어짐 · 내부 SDF) → 머리 강체 커밋 → 손가락 FK/DQS/접촉 → 손가락 관통 가드 → v17.55 래스터라이저. 연산 구간 무할당.
/// 글리치(과장 발화): JawOpen 1.9 · MouthStretch 1.6 · LowerDown 1.8 · RollLower 1.9 · 응시 1.8 + 손가락 교차 프리셋.
/// </summary>
public sealed class GaussianFacialHandStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, BodyPoints = 20_000, EnvSplats = 20_000, FacePoints = RealtimeGaussianFacialBlendshapeEngine.DefaultCount, HandPoints = DexterousHandGaussianSkinningSynthesizer.DefaultCount;
    public const float Dt = 1f / Hz, Aspect = (float)RealtimeGaussianSplatRasterizerEngine.W / RealtimeGaussianSplatRasterizerEngine.H;
    public const int PreviewBytes = 54 + RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 3;
    public static readonly ArkitBlendshape[] SliderShapes =
    {
        ArkitBlendshape.EyeBlinkLeft, ArkitBlendshape.EyeBlinkRight, ArkitBlendshape.JawOpen, ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight,
        ArkitBlendshape.BrowInnerUp, ArkitBlendshape.MouthPucker, ArkitBlendshape.CheekPuff, ArkitBlendshape.EyeLookOutLeft, ArkitBlendshape.TongueOut,
    };

    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist], _tearS = new float[Hist], _penS = new float[Hist];
    private readonly float[] _manual = new float[RealtimeGaussianFacialBlendshapeEngine.K], _w = new float[RealtimeGaussianFacialBlendshapeEngine.K];
    private readonly MultilingualDubbingLipSyncStudioRunner? _dub;
    private readonly Vector3 _fx, _fy, _fz, _faceC;
    private GaussianSplatScene? _scene;
    private RealtimeDualQuaternionGaussianSkinningEngine? _body;
    private RealtimeGaussianFacialBlendshapeEngine? _face;
    private DexterousHandGaussianSkinningSynthesizer? _hands;
    private FacialSplatTearFingerIntersectionGuard? _guard;
    private RealtimeGaussianSplatRasterizerEngine? _raster;
    private int _faceStart, _sHead, _sN;
    private float _t, _blinkT = 1.2f;
    private double _ms;
    private SplatCamera _cam;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public GaussianFacialHandStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(MultilingualDubbingLipSyncStudioRunner)) as MultilingualDubbingLipSyncStudioRunner) { }

    public GaussianFacialHandStudioRunner(MultilingualDubbingLipSyncStudioRunner? dubbing = null)
    {
        _dub = dubbing;
        var rest = RagdollSkeleton.Rest;
        var side = Vector3.Normalize(rest[(int)RagdollJoint.RHip] - rest[(int)RagdollJoint.LHip]);
        _fy = Vector3.Normalize(rest[(int)RagdollJoint.HeadTop] - rest[(int)RagdollJoint.Head]);
        _fz = Vector3.Normalize(Vector3.Cross(side, _fy));
        _fx = Vector3.Cross(_fy, _fz);
        _faceC = (rest[(int)RagdollJoint.Head] + rest[(int)RagdollJoint.HeadTop]) * 0.5f;
    }

    public event Action? OnChange;
    public AvatarFkAnimator Animator { get; } = new();
    public VolumetricDeterminantClampingGuard BodyGuard { get; } = new();
    public FacialPhonemeRingTimeline Timeline { get; } = new();
    public SplatFrustumCullingVramBudgetGuard CullGuard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new() { Degree = 0 };
    public RealtimeGaussianFacialBlendshapeEngine? Face => _face;
    public DexterousHandGaussianSkinningSynthesizer? Hands => _hands;
    public FacialSplatTearFingerIntersectionGuard? Guard => _guard;
    public RealtimeGaussianSplatRasterizerEngine? Raster => _raster;
    public GaussianSplatScene? Scene => _scene;
    public int FaceStart => _faceStart;
    public FaceExpression Expression { get; private set; } = FaceExpression.Speech;
    public HandPreset HandPose => _hands?.Preset ?? HandPreset.Open;
    public bool Glitch { get; private set; }
    public bool AutoBlink { get; set; } = true;
    public bool UseDubbing { get; private set; }
    public bool DubbingAvailable => _dub is not null;
    public int Points => (_face?.Count ?? 0) + (_hands?.Count ?? 0);
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Weight(ArkitBlendshape k) => _w[(int)k];
    public float Manual(ArkitBlendshape k) => _manual[(int)k];
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float TearSeries(int i) => _tearS[Idx(i)];
    public float PenetrationSeries(int i) => _penS[Idx(i)];

    public static string ExpressionName(FaceExpression e) => e switch
    {
        FaceExpression.Neutral => "😐 무표정", FaceExpression.Joy => "😄 기쁨", FaceExpression.Surprise => "😮 놀람",
        FaceExpression.Anger => "😠 분노", FaceExpression.Sadness => "😢 슬픔", _ => "🗣 발화(음소)",
    };

    public static string SliderName(ArkitBlendshape k) => k switch
    {
        ArkitBlendshape.EyeBlinkLeft => "눈 깜빡임 L", ArkitBlendshape.EyeBlinkRight => "눈 깜빡임 R", ArkitBlendshape.JawOpen => "개구도",
        ArkitBlendshape.MouthSmileLeft => "미소 L", ArkitBlendshape.MouthSmileRight => "미소 R", ArkitBlendshape.BrowInnerUp => "눈썹 안쪽 올림",
        ArkitBlendshape.MouthPucker => "입 오므림", ArkitBlendshape.CheekPuff => "볼 부풀림", ArkitBlendshape.EyeLookOutLeft => "시선 바깥 L",
        ArkitBlendshape.TongueOut => "혀 내밀기", _ => NeuralFacialMuscleSimulator.Name(k),
    };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureRig()
    {
        if (_scene is not null) return;
        _scene = new GaussianSplatScene(EnvSplats + BodyPoints + FacePoints + HandPoints);
        _scene.Generate(EnvSplats, 0xFA58u, centerpiece: false);
        var bs = _scene.Reserve(BodyPoints);
        var weights = new BoneWeights4[BodyPoints]; var normals = new Vector3[BodyPoints];
        var nb = GaussianAvatarRig.Build(_scene, bs, BodyPoints, weights, normals);
        // 머리 캡슐 전면 = 가우시안 안면이 대체(불투명도 0)
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
        _guard = new FacialSplatTearFingerIntersectionGuard(_face);
        _raster = new RealtimeGaussianSplatRasterizerEngine();
    }

    public void Prepare() { lock (_gate) EnsureRig(); }

    public void SetExpression(FaceExpression e) { lock (_gate) { Expression = e; Timeline.Reset(); } LastAction = "🎭 표정 → " + ExpressionName(e); Raise(); }

    public void SetManual(ArkitBlendshape k, float w) { lock (_gate) _manual[(int)k] = Math.Clamp(w, 0f, 1f); Raise(); }

    public void ClearManual() { lock (_gate) Array.Clear(_manual); LastAction = "↺ 수동 슬라이더 초기화"; Raise(); }

    public void SetHandPreset(HandPreset p) { lock (_gate) { EnsureRig(); _hands!.SetPreset(p); } LastAction = "🖐 손가락 → " + DexterousHandGaussianSkinningSynthesizer.PresetName(p); Raise(); }

    public void SetGlitch(bool on) { lock (_gate) { EnsureRig(); Glitch = on; if (on) _hands!.SetPreset(HandPreset.Cross); } LastAction = on ? "💥 과장 발화 · 손가락 교차 주입 ON" : "💥 주입 OFF"; Raise(); }

    public void SetFaceGuard(bool on) { lock (_gate) { EnsureRig(); _guard!.FaceEnabled = on; } LastAction = on ? "🛡 안면 찢어짐 · 내부 SDF 가드 ON" : "⛔ 안면 가드 OFF(비교)"; Raise(); }

    public void SetFingerGuard(bool on) { lock (_gate) { EnsureRig(); _guard!.FingerEnabled = on; } LastAction = on ? "🛡 손가락 관통 밀어내기 ON" : "⛔ 손가락 가드 OFF(비교)"; Raise(); }

    public void SetContact(bool on) { lock (_gate) { EnsureRig(); _hands!.ContactEnabled = on; } LastAction = on ? "🤏 자가 접촉 압축면 ON" : "🤏 접촉 합성 OFF"; Raise(); }

    public void SetDubbing(bool on)
    {
        if (on && _dub is null) return;
        lock (_gate) { UseDubbing = on; Timeline.Reset(); Timeline.Procedural = !on; }
        LastAction = on ? "🎙 v17.32 더빙 음소 스트림 연동" : "🗣 내장 절차 음소 문장";
        Raise();
    }

    // ══════════════════════ 프레임 ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureRig();
            using var zg = ZeroGcRegionScope.Enter("FaceHand.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        _t += Dt; _ms += Dt * 1000.0;
        var body = _body!; var face = _face!; var guard = _guard!; var sc = _scene!;
        // ① 전신(가드 자세 · 머리 끄덕임)
        Animator.ClearLocal();
        var sway = MathF.Sin(_t * 0.9f);
        Animator.SetLocal((int)RagdollJoint.RShoulder, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.8f - 0.08f * sway) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.32f));
        Animator.SetLocal((int)RagdollJoint.LShoulder, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.8f + 0.08f * sway) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.32f));
        Animator.SetLocal((int)RagdollJoint.RElbow, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -1.95f));
        Animator.SetLocal((int)RagdollJoint.LElbow, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -1.95f));
        Animator.SetLocal((int)RagdollJoint.Neck, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.18f * MathF.Sin(_t * 0.5f)) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.06f * MathF.Sin(_t * 0.8f)));
        Animator.Solve(0f, body);
        body.Skin(BodyGuard);
        var joints = Animator.Positions;
        // ② 안면 52 계수
        _manual.AsSpan().CopyTo(_w);
        ApplyExpression(Expression, _w);
        if (Expression == FaceExpression.Speech)
        {
            if (UseDubbing && _dub is not null && _dub.IsRunning) Timeline.Push(_dub.WavePhoneme(0), 1000f / Hz, _ms);
            Timeline.Evaluate(_ms, _w);
        }
        if (AutoBlink)
        {
            _blinkT -= Dt;
            if (_blinkT < -0.15f) _blinkT = 2.6f + 1.4f * MathF.Abs(MathF.Sin(_t * 7.3f));
            var b = _blinkT < 0f ? MathF.Sin(MathF.PI * -_blinkT / 0.15f) : 0f;
            ref var bl = ref _w[(int)ArkitBlendshape.EyeBlinkLeft]; if (b > bl) bl = b;
            ref var br = ref _w[(int)ArkitBlendshape.EyeBlinkRight]; if (b > br) br = b;
        }
        if (Glitch) InjectGlitch(_w);
        face.SetWeights(_w);
        face.Blend();
        guard.ApplyFace(face);
        var rh = body.BoneRotation((int)RagdollJoint.HeadTop);
        var fm = new Matrix4x4(_fx.X, _fx.Y, _fx.Z, 0f, _fy.X, _fy.Y, _fy.Z, 0f, _fz.X, _fz.Y, _fz.Z, 0f, 0f, 0f, 0f, 1f) * Matrix4x4.CreateFromQuaternion(rh);
        var head = joints[(int)RagdollJoint.Head];
        var ft = Vector3.Transform(_faceC - RagdollSkeleton.Rest[(int)RagdollJoint.Head], rh) + head;
        face.Commit(sc, _faceStart, in fm, ft);
        // ③ 손가락
        _hands!.Step(body, joints, Dt);
        guard.ApplyHands(_hands, sc);
        // ④ 카메라 · 렌더
        var fwd = Vector3.Transform(_fz, Animator.Global((int)RagdollJoint.Chest));
        var hc = (joints[(int)RagdollJoint.LHand] + joints[(int)RagdollJoint.RHand]) * 0.5f;
        var target = ft * 0.6f + hc * 0.4f;
        var orbit = 0.35f * MathF.Sin(_t * 0.25f);
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        _cam = SplatCamera.LookAt(target + fwd * (0.95f * MathF.Cos(orbit)) + right * (0.95f * MathF.Sin(orbit)) + new Vector3(0f, 0.05f, 0f), target, 40f, Aspect, 0.05f, 120f);
        _raster!.Render(sc, in _cam, CullGuard, Sh);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        _msS[_sHead] = (float)ms; _tearS[_sHead] = guard.TearMeter; _penS[_sHead] = guard.FrameFingerDepthMm;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    /// <summary>표정 프리셋(최대값 합성 · 무할당).</summary>
    public static void ApplyExpression(FaceExpression e, Span<float> w)
    {
        static void M(Span<float> d, ArkitBlendshape k, float v) { ref var x = ref d[(int)k]; if (v > x) x = v; }
        switch (e)
        {
            case FaceExpression.Joy:
                M(w, ArkitBlendshape.MouthSmileLeft, 0.85f); M(w, ArkitBlendshape.MouthSmileRight, 0.85f); M(w, ArkitBlendshape.CheekSquintLeft, 0.6f); M(w, ArkitBlendshape.CheekSquintRight, 0.6f);
                M(w, ArkitBlendshape.EyeSquintLeft, 0.3f); M(w, ArkitBlendshape.EyeSquintRight, 0.3f); M(w, ArkitBlendshape.JawOpen, 0.15f); break;
            case FaceExpression.Surprise:
                M(w, ArkitBlendshape.BrowInnerUp, 0.9f); M(w, ArkitBlendshape.BrowOuterUpLeft, 0.8f); M(w, ArkitBlendshape.BrowOuterUpRight, 0.8f);
                M(w, ArkitBlendshape.EyeWideLeft, 0.8f); M(w, ArkitBlendshape.EyeWideRight, 0.8f); M(w, ArkitBlendshape.JawOpen, 0.6f); M(w, ArkitBlendshape.MouthFunnel, 0.3f); break;
            case FaceExpression.Anger:
                M(w, ArkitBlendshape.BrowDownLeft, 0.9f); M(w, ArkitBlendshape.BrowDownRight, 0.9f); M(w, ArkitBlendshape.NoseSneerLeft, 0.5f); M(w, ArkitBlendshape.NoseSneerRight, 0.5f);
                M(w, ArkitBlendshape.MouthPressLeft, 0.5f); M(w, ArkitBlendshape.MouthPressRight, 0.5f); M(w, ArkitBlendshape.EyeSquintLeft, 0.4f); M(w, ArkitBlendshape.EyeSquintRight, 0.4f); break;
            case FaceExpression.Sadness:
                M(w, ArkitBlendshape.BrowInnerUp, 0.7f); M(w, ArkitBlendshape.MouthFrownLeft, 0.7f); M(w, ArkitBlendshape.MouthFrownRight, 0.7f);
                M(w, ArkitBlendshape.MouthShrugLower, 0.4f); M(w, ArkitBlendshape.EyeBlinkLeft, 0.2f); M(w, ArkitBlendshape.EyeBlinkRight, 0.2f); break;
        }
    }

    /// <summary>과장 발화 — 선형 블렌드쉐이프 외삽(찢어짐 · 내부 돌출 유발).</summary>
    public static void InjectGlitch(Span<float> w)
    {
        w[(int)ArkitBlendshape.JawOpen] = 1.9f;
        w[(int)ArkitBlendshape.MouthStretchLeft] = 1.6f; w[(int)ArkitBlendshape.MouthStretchRight] = 1.6f;
        w[(int)ArkitBlendshape.MouthLowerDownLeft] = 1.8f; w[(int)ArkitBlendshape.MouthLowerDownRight] = 1.8f;
        w[(int)ArkitBlendshape.MouthRollLower] = 1.9f;
        w[(int)ArkitBlendshape.EyeLookOutLeft] = 1.8f; w[(int)ArkitBlendshape.EyeLookInRight] = 1.8f;
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
        var g = _guard;
        return g is null ? "대기" : "찢어짐 " + g.TearHoles.ToString(CultureInfo.InvariantCulture) + " · 돌출 " + g.Protrusions.ToString(CultureInfo.InvariantCulture) + " · 관통 " + g.FingerPenetrations.ToString(CultureInfo.InvariantCulture) + " · 잔여 " + g.Defects.ToString(CultureInfo.InvariantCulture);
    }

    public void WritePreviewBmp(byte[] dst) { lock (_gate) _raster?.WriteBmp(dst, false); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.FaceHand" };
            _loop.Start();
        }
        LastAction = "▶ 가우시안 페이셜 & 핑거 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("FaceHand.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("FaceHand.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) { _body?.Dispose(); _hands?.Dispose(); _raster?.Dispose(); }
    }
}
