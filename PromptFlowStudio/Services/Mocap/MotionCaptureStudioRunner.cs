using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Mocap;

/// <summary>
/// 결정적 합성 연기자(검증·데모용 참값): 제자리 스텝 · 골반 흔들림 · 손 흔들기 · 고개 돌림 · 손가락 굽힘 · 표정(입·미소·눈썹·깜빡임) → 76 키포인트.
/// </summary>
public static class SyntheticPerformer
{
    private const float D = MathF.PI / 180f;

    public static void Pose(float t, float scale, Span<Vector3> kp, Span<float> faceGt, Span<Quaternion> local, Span<Quaternion> global, Span<Vector3> offsets)
    {
        for (var j = 0; j < MocapSkeleton.Body; j++) { local[j] = Quaternion.Identity; offsets[j] = MocapSkeleton.RestOffset[j] * scale; }
        var ph = t * 1.4f * MathF.Tau;
        var liftL = MathF.Max(0, MathF.Sin(ph)); var liftR = MathF.Max(0, -MathF.Sin(ph));
        local[MocapSkeleton.Pelvis] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 15f * D * MathF.Sin(t * 0.4f));
        local[MocapSkeleton.LHip] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -40f * D * liftL);
        local[MocapSkeleton.RHip] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -40f * D * liftR);
        local[MocapSkeleton.LKnee] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 70f * D * liftL);
        local[MocapSkeleton.RKnee] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 70f * D * liftR);
        local[MocapSkeleton.LAnkle] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -25f * D * liftL);
        local[MocapSkeleton.RAnkle] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -25f * D * liftR);
        local[MocapSkeleton.Spine2] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 4f * D * MathF.Sin(ph));
        var wave = 0.5f + 0.5f * MathF.Sin(t * 0.9f);
        local[MocapSkeleton.LShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (-65f + 120f * wave) * D);
        local[MocapSkeleton.LElbow] = Quaternion.CreateFromAxisAngle(-Vector3.UnitY, (30f + 60f * wave * (0.5f + 0.5f * MathF.Sin(t * 6f))) * D);
        local[MocapSkeleton.RShoulder] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 70f * D) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, 25f * D * MathF.Sin(ph));
        local[MocapSkeleton.RElbow] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 35f * D);
        local[MocapSkeleton.Neck] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20f * D * MathF.Sin(t * 0.6f));
        var root = new Vector3(0.05f * scale * MathF.Sin(ph), 0.95f * scale - 0.015f * scale * MathF.Abs(MathF.Cos(ph)), 0);
        Span<Vector3> pos = kp[..MocapSkeleton.Body];
        MocapSkeleton.ForwardKinematics(offsets, local, root, pos, global);
        Span<float> curl = stackalloc float[5];
        for (var f = 0; f < 5; f++) curl[f] = 0.5f + 0.5f * MathF.Sin(t * 1.3f + f * 0.4f);
        MocapSkeleton.HandPositions(true, pos[MocapSkeleton.LWrist], global[MocapSkeleton.LWrist], curl, scale, kp.Slice(MocapSkeleton.LeftHandBase, MocapSkeleton.HandPoints));
        for (var f = 0; f < 5; f++) curl[f] = 0.3f;
        MocapSkeleton.HandPositions(false, pos[MocapSkeleton.RWrist], global[MocapSkeleton.RWrist], curl, scale, kp.Slice(MocapSkeleton.RightHandBase, MocapSkeleton.HandPoints));
        faceGt[..FaceShapes.Count].Clear();
        var jaw = 0.6f * (0.5f + 0.5f * MathF.Sin(t * 5f));
        var smile = 0.5f + 0.5f * MathF.Sin(t * 0.7f);
        var brow = MathF.Max(0, MathF.Sin(t * 0.5f));
        var bt = t % 3.3f; var blink = bt < 0.15f ? MathF.Sin(bt / 0.15f * MathF.PI) : 0f;
        faceGt[FaceShapes.JawOpen] = jaw; faceGt[FaceShapes.MouthSmile] = smile; faceGt[FaceShapes.BrowInnerUp] = brow;
        faceGt[FaceShapes.EyeBlinkL] = blink; faceGt[FaceShapes.EyeBlinkR] = blink;
        var head = pos[MocapSkeleton.Head]; var hq = global[MocapSkeleton.Head];
        for (var i = 0; i < MocapSkeleton.Face; i++)
        {
            var o = MocapSkeleton.FaceRest[i];
            if (i is 0 or 1) o.Y += 0.015f * brow;
            if (i is 2 or 3) o.Y -= 0.012f * blink;
            if (i is 5 or 6) o.Y += 0.015f * smile;
            if (i is 8 or 9) o.Y -= 0.03f * jaw;
            kp[MocapSkeleton.FaceBase + i] = head + Vector3.Transform(o * scale, hq);
        }
    }
}

/// <summary>캡슐 표면 점군 휴머노이드 메시(오토 리깅 입력 · 검증용).</summary>
public static class HumanoidMesh
{
    public static Vector3[] Build(CharacterRig skel, float headScale = 1f, int ring = 10, float spacing = 0.03f)
    {
        var s = skel.Offset[MocapSkeleton.Spine2].Length() / 0.13f;   // 몸통 기준 스케일(다리·팔·머리 배율 무관)
        var pts = new List<Vector3>(4096);
        var r = skel.Rest;
        void Capsule(Vector3 a, Vector3 b, float rad)
        {
            var ab = b - a; var len = ab.Length();
            var dir = len > 1e-6f ? ab / len : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(dir, u);
            var n = Math.Max(1, (int)(len / (spacing * s)));
            for (var k = 0; k <= n; k++)
            {
                var c = a + ab * ((float)k / n);
                for (var m = 0; m < ring; m++) { var ang = m * MathF.Tau / ring; pts.Add(c + (u * MathF.Cos(ang) + v * MathF.Sin(ang)) * rad); }
            }
            foreach (var e in new[] { a, b })
                for (var m = 0; m < ring; m++) { var ang = m * MathF.Tau / ring; pts.Add(e + (u * MathF.Cos(ang) + v * MathF.Sin(ang)) * rad * 0.7f + (e == a ? -dir : dir) * rad * 0.7f); }
        }
        Capsule(r[MocapSkeleton.Pelvis], r[MocapSkeleton.Spine1], 0.13f * s);
        Capsule(r[MocapSkeleton.Spine1], r[MocapSkeleton.Spine3], 0.13f * s);
        Capsule(r[MocapSkeleton.Spine3], r[MocapSkeleton.Neck], 0.12f * s);
        Capsule(r[MocapSkeleton.Neck], r[MocapSkeleton.Head], 0.05f * s);
        for (var side = 0; side < 2; side++)
        {
            int hip = side == 0 ? 1 : 2, knee = side == 0 ? 4 : 5, ankle = side == 0 ? 7 : 8, toe = side == 0 ? 10 : 11, col = side == 0 ? 13 : 14, sh = side == 0 ? 16 : 17, el = side == 0 ? 18 : 19, wr = side == 0 ? 20 : 21, hd = side == 0 ? 22 : 23;
            Capsule(r[MocapSkeleton.Pelvis], r[hip], 0.1f * s);
            Capsule(r[hip], r[knee], 0.075f * s);
            Capsule(r[knee], r[ankle], 0.055f * s);
            Capsule(r[ankle], r[toe] + new Vector3(0, 0.035f * s, 0), 0.035f * s);
            Capsule(r[col], r[sh], 0.05f * s);
            Capsule(r[sh], r[el], 0.05f * s);
            Capsule(r[el], r[wr], 0.04f * s);
            Capsule(r[wr], r[hd] + Vector3.Normalize(r[hd] - r[wr]) * 0.03f * s, 0.035f * s);
        }
        var head = r[MocapSkeleton.Head]; var hr = 0.11f * s * headScale;
        for (var i = 0; i < 200; i++)
        {
            var z = 1 - 2 * (i + 0.5f) / 200f; var rr = MathF.Sqrt(1 - z * z); var phi = i * 2.399963f;
            pts.Add(head + new Vector3(rr * MathF.Cos(phi), z, rr * MathF.Sin(phi)) * hr);
        }
        return pts.ToArray();
    }
}

/// <summary>
/// [PromptFlow Studio v16.75.0] 💃 AI 신경망 마커리스 모션 캡처 스튜디오 러너(30Hz · <see cref="TickOnce"/> 무할당).
/// 합성 연기자 → 4대 가상 카메라 투영(픽셀 잡음 · 드롭아웃 · 프레임당 이상치 1개) → 다중 시점 3D 추정 → 이종 체형 캐릭터(오토 리깅 · 대두 단신) 리타겟(+제스처) →
/// 물리 가드(발 고정 · 지면 · 자기 충돌 · 관성) → 캡처+생성 표정 → LBS 스키닝. 실 웹캠은 <see cref="I2dKeypointDetector"/> 결선 시 <see cref="NeuralMarkerlessPoseEstimationEngine.SubmitFrame"/>.
/// </summary>
public sealed class MotionCaptureStudioRunner : IDisposable
{
    public const int Hz = 30, Cameras = 4;
    private const int K = MocapSkeleton.Keypoints, B = MocapSkeleton.Body;

    private readonly object _gate = new();
    private readonly NeuralMarkerlessPoseEstimationEngine _pose;
    private readonly AutoRiggingAndRetargetingSynthesizer _rig;
    private readonly GenerativeCharacterPerformanceGuard _guard;
    private readonly Vector3[] _gt = new Vector3[K], _det = new Vector3[Cameras * K], _src = new Vector3[B], _charPre = new Vector3[B], _charPost = new Vector3[B], _offsets = new Vector3[B];
    private readonly Quaternion[] _gtLocal = new Quaternion[B], _gtGlobal = new Quaternion[B], _add = new Quaternion[B];
    private readonly float[] _faceGt = new float[FaceShapes.Count], _faceCap = new float[FaceShapes.Count], _face = new float[FaceShapes.Count];
    private Vector3[] _skinned = Array.Empty<Vector3>();
    private CancellationTokenSource? _cts;
    private long _frame;
    private ulong _rng = 0x0C0FFEE1675UL;
    private float _mpjpe, _handErr, _faceErr;

    public MotionCaptureStudioRunner(IServiceProvider sp)
        : this((NeuralMarkerlessPoseEstimationEngine?)sp.GetService(typeof(NeuralMarkerlessPoseEstimationEngine)) ?? new NeuralMarkerlessPoseEstimationEngine(),
               (AutoRiggingAndRetargetingSynthesizer?)sp.GetService(typeof(AutoRiggingAndRetargetingSynthesizer)) ?? new AutoRiggingAndRetargetingSynthesizer(),
               (GenerativeCharacterPerformanceGuard?)sp.GetService(typeof(GenerativeCharacterPerformanceGuard)) ?? new GenerativeCharacterPerformanceGuard()) { }

    public MotionCaptureStudioRunner(NeuralMarkerlessPoseEstimationEngine pose, AutoRiggingAndRetargetingSynthesizer rig, GenerativeCharacterPerformanceGuard guard)
    {
        _pose = pose; _rig = rig; _guard = guard;
        for (var c = 0; c < Cameras; c++)
        {
            var a = (30f + 90f * c) * MathF.PI / 180f;
            _pose.AddCamera(PinholeCamera.LookAt(new Vector3(3.2f * MathF.Sin(a), 1.5f, 3.2f * MathF.Cos(a)), new Vector3(0, 1f, 0), 55f, 640, 480));
        }
        BuildCharacter(0.75f, 0.8f, 1f, 1.6f, "대두 단신 캐릭터");
        _guard.SetDialogue("안녕하세요! 오늘 정말 기뻐요, 함께 춤춰요!", 0);
    }

    public event Action? OnChange;

    public NeuralMarkerlessPoseEstimationEngine Pose => _pose;
    public AutoRiggingAndRetargetingSynthesizer Rig => _rig;
    public GenerativeCharacterPerformanceGuard Guard => _guard;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public long NowMicros => Frame * 1_000_000 / Hz;
    public float NoisePx { get; set; } = 1.2f;
    public float Dropout { get; set; } = 0.05f;
    public bool InjectOutliers { get; set; } = true;
    /// <summary>추정 전신 관절 평균 위치 오차(mm · 참값 대비).</summary>
    public float MpjpeMm => _mpjpe * 1000f;
    public float HandErrorMm => _handErr * 1000f;
    /// <summary>캡처 표정(입·미소·눈썹) 평균 절대 오차(EMA).</summary>
    public float FaceError => _faceErr;
    public ReadOnlySpan<Vector3> GroundTruth => _gt;
    public ReadOnlySpan<Vector3> CharacterRaw => _charPre;
    public ReadOnlySpan<Vector3> Character => _charPost;
    public ReadOnlySpan<float> FaceWeights => _face;
    public ReadOnlySpan<float> FaceGroundTruth => _faceGt;
    public ReadOnlySpan<Vector3> Skinned => _skinned;
    public string? LastAction { get; private set; }

    public ReadOnlySpan<Vector3> Detections(int camera) => _det.AsSpan(camera * K, K);

    /// <summary>캐릭터 메시 생성 → 오토 리깅 → 리타겟 대상 · 가드 스케일.</summary>
    public void BuildCharacter(float scale, float legScale, float armScale, float headScale, string name)
    {
        lock (_gate)
        {
            var skel = AutoRiggingAndRetargetingSynthesizer.ReferenceRig(scale, legScale, armScale, headScale);
            var mesh = HumanoidMesh.Build(skel, headScale);
            var rig = _rig.AutoRig(mesh, name);
            _rig.Target = rig;
            _guard.SetCharacterHeight(rig.Height);
            _guard.Ground = rig.Ground;
            _guard.ResetPhysics();
            _skinned = new Vector3[rig.Vertices.Length];
        }
        LastAction = _rig.LastAction;
        Raise();
    }

    private float Rand()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; _rng = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    private float Gauss() => MathF.Sqrt(-2f * MathF.Log(MathF.Max(Rand(), 1e-7f))) * MathF.Cos(MathF.Tau * Rand());

    /// <summary>1프레임(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Mocap.Runner.Tick");
            var f = _frame;
            var now = f * 1_000_000 / Hz;
            var t = now / 1e6f;
            SyntheticPerformer.Pose(t, 1f, _gt, _faceGt, _gtLocal, _gtGlobal, _offsets);
            // 가상 카메라 검출
            var outCam = (int)(f % Cameras); var outKp = (int)(f * 7 % K);
            for (var c = 0; c < Cameras; c++)
            {
                var cam = _pose.Camera(c);
                var o = c * K;
                for (var k = 0; k < K; k++)
                {
                    if (!cam.Project(_gt[k], out var uv) || !cam.InFrame(uv) || Rand() < Dropout) { _det[o + k] = default; continue; }
                    uv += new Vector2(Gauss(), Gauss()) * NoisePx;
                    if (InjectOutliers && c == outCam && k == outKp) uv.X += 35f;
                    _det[o + k] = new Vector3(uv, k < B ? 0.9f : 0.8f);
                }
                _pose.SetDetections(c, _det.AsSpan(o, K));
            }
            _pose.Estimate(now);
            // 오차 지표
            float e = 0, eh = 0;
            for (var j = 0; j < B; j++) e += Vector3.Distance(_pose.Position(j), _gt[j]);
            for (var k = B; k < MocapSkeleton.FaceBase; k++) eh += Vector3.Distance(_pose.Position(k), _gt[k]);
            _mpjpe = e / B; _handErr = eh / MocapSkeleton.Hands;
            // 리타겟(+제스처) → 가드
            for (var j = 0; j < B; j++) _src[j] = _pose.Position(j);
            var srcPelvis = _pose.BoneLength(MocapSkeleton.LKnee) + _pose.BoneLength(MocapSkeleton.LAnkle) + 0.12f * (_pose.BoneLength(MocapSkeleton.LKnee) / 0.42f);
            _guard.Gesture(now, _add);
            _rig.Retarget(_src, srcPelvis, _add, _charPre);
            _charPre.AsSpan().CopyTo(_charPost);
            _guard.Apply(_charPost, 1f / Hz);
            // 표정
            _pose.FaceBlendshapes(_faceCap);
            float fc = 0;
            for (var i = 0; i < MocapSkeleton.Face; i++) fc += _pose.Confidence(MocapSkeleton.FaceBase + i);
            _guard.SetCapturedFace(_faceCap, fc / MocapSkeleton.Face);
            _guard.EvaluateFace(now, _face);
            float fe = 0;
            fe += MathF.Abs(_faceCap[FaceShapes.JawOpen] - _faceGt[FaceShapes.JawOpen]) + MathF.Abs(_faceCap[FaceShapes.MouthSmile] - _faceGt[FaceShapes.MouthSmile]) + MathF.Abs(_faceCap[FaceShapes.BrowInnerUp] - _faceGt[FaceShapes.BrowInnerUp]);
            _faceErr += (fe / 3 - _faceErr) * (f == 0 ? 1f : 0.1f);   // EMA(전신 카메라 거리에서 1px ≈ 8mm — 단일 프레임 표정 잡음 큼)
            // 스키닝
            var rig = _rig.Target;
            if (_skinned.Length == rig.Vertices.Length && rig.BoneIndex.Length == rig.Vertices.Length * 4)
            {
                // 가드 후 위치 + 리타겟 전역 회전
                AutoRiggingAndRetargetingSynthesizer.Skin(rig, _charPost, _rig.Global, _skinned);
            }
            _frame = f + 1;
        }
    }

    public void SetDialogue(string text) { _guard.SetDialogue(text, NowMicros); LastAction = _guard.LastAction; Raise(); }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 캡처 루프 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 캡처 루프 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Mocap.Tick", ex); }
                if ((Frame & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mocap.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
