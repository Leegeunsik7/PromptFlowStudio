using System.Numerics;
using PromptFlowStudio.Services.GaussianFaceHand;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.SplatRelighting;

namespace PromptFlowStudio.Services.BroadcastMcr;

/// <summary>v17.66.0 다중 배우 접지 그림자 체인 — 배우별 v17.56 섀도우 엔진을 순차 변조(워커 병렬 · 무할당).</summary>
public sealed class SplatShadowChain : ISplatRadianceModulator
{
    private readonly RealtimeSplatShadowCastingEngine[] _e;
    public SplatShadowChain(RealtimeSplatShadowCastingEngine[] engines) => _e = engines;
    public int Active { get; set; }
    public void BeginFrame(GaussianSplatScene scene, in SplatCamera cam, int workers) { for (var i = 0; i < Active; i++) _e[i].BeginFrame(scene, cam, workers); }
    public Vector3 Modulate(int worker, int splat, Vector3 position, Vector3 color) { for (var i = 0; i < Active; i++) color = _e[i].Modulate(worker, splat, position, color); return color; }
    public void EndFrame() { for (var i = 0; i < Active; i++) _e[i].EndFrame(); }
}

/// <summary>
/// [PromptFlow Studio v17.66.0] 실시간 3DGS 환경 &amp; 멀티 아바타 통합 씬 컴포지터(공용 SoA 버퍼 1 개 · 단일 깊이 래딕스 큐 · 무할당).
///   ▸ 버퍼 배치: [v17.55 세트 N] [토크쇼 데스크] [아바타 k: v17.57 DQS 몸체 · v17.58 안면] × 2~3 [PPL 소품] — 정적 영역은 1 회 기록, 동적 영역만 매 틱 덮어씀.
///   ▸ 틱(<see cref="Update"/>): 아바타별 FK(루트 · 몸 yaw · 목 yaw/pitch) → DQS 스키닝 → 안면 블렌드 · 커밋 → 소품 강체 기록 → 배우별 v17.56 섀도우 뎁스 맵.
///   ▸ 렌더(<see cref="Render"/>): v17.55 SIMD 프러스텀 조기 컬링 → 16 비트 카메라 깊이 키 → 2 패스 LSD 래딕스 정렬(세트 · 아바타 · 소품 혼합 단일 큐) → 타일 전방-후방 블렌딩.
///     메쉬 z-버퍼 없이 정렬 순 알파 합성만으로 상호 차폐 · 접지 그림자 체인 변조 → 톤 매핑 RGB24 버스 프레임.
/// </summary>
public sealed class UnifiedGaussianSceneCompositorEngine : IDisposable
{
    public const int W = RealtimeGaussianSplatRasterizerEngine.W, H = RealtimeGaussianSplatRasterizerEngine.H, FrameBytes = W * H * 3;
    public const int DefaultEnv = 300_000, DefaultBody = 30_000, DefaultFace = 6_000, DeskSplats = 6_000, PropSplats = 1_200, MaxAvatars = 3, ViewerSplats = 12_000, ProductSplats = 50_000;
    public const float DeskTop = 0.74f, DeskRadius = 0.46f, PropRadius = 0.055f, PropHeight = 0.17f;
    public static readonly Vector3 DeskCenter = new(0f, DeskTop, 1.0f);
    public static readonly Vector3 PropColor = new(0.86f, 0.08f, 0.10f), PropBand = new(0.95f, 0.95f, 0.92f);

    private readonly RealtimeDualQuaternionGaussianSkinningEngine[] _body = new RealtimeDualQuaternionGaussianSkinningEngine[MaxAvatars];
    private readonly RealtimeGaussianFacialBlendshapeEngine[] _face = new RealtimeGaussianFacialBlendshapeEngine[MaxAvatars];
    private readonly AvatarFkAnimator[] _fk = new AvatarFkAnimator[MaxAvatars];
    private readonly RealtimeSplatShadowCastingEngine[] _shadow = new RealtimeSplatShadowCastingEngine[MaxAvatars];
    private readonly int[] _bodyStart = new int[MaxAvatars], _faceStart = new int[MaxAvatars];
    private readonly Vector3[] _root = new Vector3[MaxAvatars];
    private readonly float[] _yaw = new float[MaxAvatars], _headYaw = new float[MaxAvatars], _headPitch = new float[MaxAvatars];
    private readonly Quaternion[] _armLocal = new Quaternion[MaxAvatars * 4];
    private readonly bool[] _armSet = new bool[MaxAvatars];
    private readonly Vector3[] _propLocal, _propA, _propB, _propN;
    private readonly Vector3[] _propRgb;
    private readonly Vector3 _fx, _fy, _fz, _faceC;
    private readonly int _avatars, _propStart, _envEnd, _deskEnd;
    private Vector3 _propPos;
    private bool _propVisible = true;

    public UnifiedGaussianSceneCompositorEngine(int envSplats = DefaultEnv, int bodySplats = DefaultBody, int faceSplats = DefaultFace, int avatars = MaxAvatars, int workers = -1)
    {
        _avatars = Math.Clamp(avatars, 1, MaxAvatars);
        faceSplats = Math.Max(faceSplats, RealtimeGaussianFacialBlendshapeEngine.Interior + 2000);
        Scene = new GaussianSplatScene(envSplats + DeskSplats + _avatars * (bodySplats + faceSplats) + PropSplats + ViewerSplats + ProductSplats);
        Scene.Generate(envSplats, 0x1766u, centerpiece: false);
        _envEnd = Scene.Count;
        BuildDesk();
        _deskEnd = Scene.Count;
        var rest = RagdollSkeleton.Rest;
        var side = Vector3.Normalize(rest[(int)RagdollJoint.RHip] - rest[(int)RagdollJoint.LHip]);
        _fy = Vector3.Normalize(rest[(int)RagdollJoint.HeadTop] - rest[(int)RagdollJoint.Head]);
        _fz = Vector3.Normalize(Vector3.Cross(side, _fy));
        _fx = Vector3.Cross(_fy, _fz);
        _faceC = (rest[(int)RagdollJoint.Head] + rest[(int)RagdollJoint.HeadTop]) * 0.5f;
        ShadowGuard = new AdaptiveDepthBiasShadowGuard();
        var weights = new BoneWeights4[bodySplats]; var normals = new Vector3[bodySplats];
        for (var a = 0; a < _avatars; a++)
        {
            var bs = Scene.Reserve(bodySplats);
            var nb = GaussianAvatarRig.Build(Scene, bs, bodySplats, weights, normals, 0xA17A5u + (uint)a * 977u);
            for (var k = 0; k < nb; k++) if (weights[k].B0 == (int)RagdollJoint.HeadTop && Vector3.Dot(normals[k], _fz) > -0.25f) Scene.Opacity[bs + k] = 0f;
            Tint(bs, nb, a);
            _body[a] = new RealtimeDualQuaternionGaussianSkinningEngine(nb, workers < 0 ? 2 : Math.Min(workers, 2));
            _body[a].Bind(Scene, bs, nb, weights);
            _bodyStart[a] = bs;
            _face[a] = new RealtimeGaussianFacialBlendshapeEngine(faceSplats, 0xFACE5u + (uint)a);
            _faceStart[a] = Scene.Reserve(_face[a].Count);
            _face[a].WriteStatic(Scene, _faceStart[a]);
            _fk[a] = new AvatarFkAnimator();
            _shadow[a] = new RealtimeSplatShadowCastingEngine(ShadowGuard) { Opacity = 0.7f, BleedStrength = 0.08f };
        }
        AvatarEnd = Scene.Count;
        _propStart = Scene.Reserve(PropSplats);
        _propLocal = new Vector3[PropSplats]; _propA = new Vector3[PropSplats]; _propB = new Vector3[PropSplats]; _propN = new Vector3[PropSplats]; _propRgb = new Vector3[PropSplats];
        BuildProp();
        ViewerStart = Scene.Reserve(ViewerSplats);
        ProductStart = Scene.Reserve(ProductSplats);
        _propPos = DeskCenter + new Vector3(0.22f, 0f, 0.08f);
        Shadows = new SplatShadowChain(_shadow) { Active = _avatars };
        // 통합 씬(세트 + 아바타 3 + 소품 ≈ 40만)은 v17.55 기본 상한(7)보다 많은 워커로 블렌딩 · 투영을 분할(코어 − 1 · 최대 15)
        Raster = new RealtimeGaussianSplatRasterizerEngine(visibleCap: Math.Max(1 << 18, Scene.Capacity), workers: workers < 0 ? Math.Clamp(Environment.ProcessorCount - 1, 0, 15) : workers) { Modulator = Shadows };
        for (var a = 0; a < _avatars; a++) SetAvatarPose(a, new Vector3((a - (_avatars - 1) * 0.5f) * 1.25f, 0f, 0f), 0f, 0f, 0f);
    }

    public GaussianSplatScene Scene { get; }
    public RealtimeGaussianSplatRasterizerEngine Raster { get; }
    public SplatFrustumCullingVramBudgetGuard CullGuard { get; } = new();
    public SphericalHarmonicsLightingSynthesizer Sh { get; } = new() { Degree = 0 };
    public AdaptiveDepthBiasShadowGuard ShadowGuard { get; }
    public SplatShadowChain Shadows { get; }
    public bool ShadowsEnabled { get; set; } = true;
    public int Avatars => _avatars;
    public int EnvEnd => _envEnd;
    public int DeskEnd => _deskEnd;
    public int AvatarEnd { get; }
    public int PropStart => _propStart;
    /// <summary>v17.67 시청자 인터랙티브 물리 프롭 슬롯(불투명도 0 = 조기 컬링).</summary>
    public int ViewerStart { get; }
    /// <summary>v17.68 핸드헬드 3DGS 상품 슬롯(최대 5만 · 불투명도 0 = 조기 컬링).</summary>
    public int ProductStart { get; }
    public int Merged => Scene.Count;
    public int BodyStart(int a) => _bodyStart[a];
    public int FaceStart(int a) => _faceStart[a];
    public Vector3 PropPosition => _propPos;
    public bool PropVisible => _propVisible;
    public RealtimeGaussianFacialBlendshapeEngine Face(int a) => _face[a];
    public ReadOnlySpan<Vector3> Joints(int a) => _fk[a].Positions;
    /// <summary>관절 전역 회전(FK) — v17.68 손목 · 손바닥 상품 앵커.</summary>
    public Quaternion JointRotation(int a, int joint) => _fk[a].Global(joint);
    /// <summary>몸통 기준 정면 · 위 · 오른쪽(휴식 자세 · 몸 yaw 미적용).</summary>
    public Vector3 BodyForward => _fz;
    public Vector3 BodyUp => _fy;
    public Vector3 BodyRight => _fx;
    public ReadOnlySpan<Vector4> Spheres(int a) => _shadow[a].ActorSpheres;
    public Vector3 HeadPosition(int a) => _fk[a].Positions[(int)RagdollJoint.Head];
    public Vector3 Root(int a) => _root[a];
    public double LastUpdateUs { get; private set; }
    public double LastRenderMs { get; private set; }
    public double MaxRenderMs { get; private set; }
    public long Renders { get; private set; }

    /// <summary>원본 인덱스 → 영역(0 세트 · 1 데스크 · 2 아바타 · 3 PPL 소품 · 4 시청자 프롭 · 5 핸드헬드 상품).</summary>
    public int RegionOf(int i) => i < _envEnd ? 0 : i < _deskEnd ? 1 : i < AvatarEnd ? 2 : i < ViewerStart ? 3 : i < ProductStart ? 4 : 5;

    // ══════════════════════ 정적 영역 ══════════════════════
    private void BuildDesk()
    {
        var rng = 0xDE5Cu;
        for (var k = 0; k < DeskSplats; k++)
        {
            var top = k < DeskSplats * 2 / 3;
            float r, a;
            Vector3 p, n;
            if (top) { r = DeskRadius * MathF.Sqrt(U(ref rng)); a = MathF.Tau * U(ref rng); p = DeskCenter + new Vector3(r * MathF.Cos(a), 0f, r * MathF.Sin(a)); n = Vector3.UnitY; }
            else { a = MathF.Tau * U(ref rng); var y = DeskTop * U(ref rng); var rr = 0.09f + 0.05f * (y / DeskTop); p = new Vector3(DeskCenter.X + rr * MathF.Cos(a), y, DeskCenter.Z + rr * MathF.Sin(a)); n = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)); }
            var t = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var b = Vector3.Cross(n, t);
            var shade = 0.30f + 0.08f * U(ref rng);
            Scene.Add(p, t, b, n, new Vector3(0.022f, 0.022f, 0.004f), 0.92f, new Vector3(shade * 1.1f, shade * 0.85f, shade * 0.62f), top ? (byte)0 : (byte)0);
        }
    }

    private void Tint(int start, int n, int a)
    {
        // 아바타별 의상 색(호스트 남색 · 게스트 1 버건디 · 게스트 2 올리브) — 셔츠 계열 스플랫만
        var shirt = GaussianAvatarRig.Shirt;
        var c = a == 0 ? shirt : a == 1 ? new Vector3(0.55f, 0.16f, 0.22f) : new Vector3(0.36f, 0.42f, 0.18f);
        for (var i = start; i < start + n; i++)
        {
            var d = new Vector3(Scene.Dr[i], Scene.Dg[i], Scene.Db[i]) - shirt;
            if (d.LengthSquared() < 0.02f) { Scene.Dr[i] = c.X; Scene.Dg[i] = c.Y; Scene.Db[i] = c.Z; }
        }
    }

    private void BuildProp()
    {
        // 스폰서 캔(원통 · 상단 흰 띠 · 캡) — 로컬 좌표(바닥 중심 원점)
        var rng = 0xB2A9Du;
        for (var k = 0; k < PropSplats; k++)
        {
            Vector3 p, n;
            var cap = k < PropSplats / 8;
            var a = MathF.Tau * U(ref rng);
            if (cap) { var r = PropRadius * MathF.Sqrt(U(ref rng)); p = new Vector3(r * MathF.Cos(a), PropHeight, r * MathF.Sin(a)); n = Vector3.UnitY; }
            else { var y = PropHeight * U(ref rng); p = new Vector3(PropRadius * MathF.Cos(a), y, PropRadius * MathF.Sin(a)); n = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)); }
            var t = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
            _propLocal[k] = p; _propA[k] = t; _propB[k] = Vector3.Cross(n, t); _propN[k] = n;
            _propRgb[k] = cap ? new Vector3(0.75f, 0.75f, 0.78f) : p.Y > PropHeight * 0.62f && p.Y < PropHeight * 0.78f ? PropBand : PropColor;
        }
    }

    private static float U(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }

    // ══════════════════════ 입력(무할당) ══════════════════════
    /// <summary>아바타 포즈 — 발 루트 · 몸 yaw · 목 yaw/pitch(몸 기준 라디안 · pitch + = 위).</summary>
    public void SetAvatarPose(int a, Vector3 root, float bodyYaw, float headYaw, float headPitch)
    {
        _root[a] = root; _yaw[a] = bodyYaw;
        _headYaw[a] = Math.Clamp(headYaw, -1.2f, 1.2f); _headPitch[a] = Math.Clamp(headPitch, -0.6f, 0.6f);
    }

    public void SetFaceWeights(int a, ReadOnlySpan<float> w52) => _face[a].SetWeights(w52);

    /// <summary>v17.68 팔 로컬 회전(L 어깨 · L 팔꿈치 · R 어깨 · R 팔꿈치) — 상품 들기 · 박수 · 엄지척. 해제는 <see cref="ClearArmPose"/>.</summary>
    public void SetArmPose(int a, Quaternion lShoulder, Quaternion lElbow, Quaternion rShoulder, Quaternion rElbow)
    {
        var o = a * 4;
        _armLocal[o] = lShoulder; _armLocal[o + 1] = lElbow; _armLocal[o + 2] = rShoulder; _armLocal[o + 3] = rElbow;
        _armSet[a] = true;
    }

    public void ClearArmPose(int a) => _armSet[a] = false;

    public void SetProp(Vector3 basePosition, bool visible) { _propPos = basePosition; _propVisible = visible; }

    public void SetKeyLight(Vector3 position) { for (var a = 0; a < _avatars; a++) _shadow[a].LightPosition = position; }

    // ══════════════════════ 틱(무할당) ══════════════════════
    public void Update()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var sc = Scene;
        for (var a = 0; a < _avatars; a++)
        {
            var fk = _fk[a]; var body = _body[a];
            fk.ClearLocal();
            fk.SetLocal((int)RagdollJoint.Neck, Quaternion.CreateFromAxisAngle(Vector3.UnitY, _headYaw[a] * 0.6f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -_headPitch[a] * 0.5f));
            fk.SetLocal((int)RagdollJoint.Head, Quaternion.CreateFromAxisAngle(Vector3.UnitY, _headYaw[a] * 0.4f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -_headPitch[a] * 0.5f));
            if (_armSet[a])
            {
                var ao = a * 4;
                fk.SetLocal((int)RagdollJoint.LShoulder, _armLocal[ao]); fk.SetLocal((int)RagdollJoint.LElbow, _armLocal[ao + 1]);
                fk.SetLocal((int)RagdollJoint.RShoulder, _armLocal[ao + 2]); fk.SetLocal((int)RagdollJoint.RElbow, _armLocal[ao + 3]);
            }
            fk.Root = _root[a]; fk.Yaw = _yaw[a];
            fk.Solve(0f, body);
            body.Skin(BodyGuard);
            var face = _face[a];
            face.Blend();
            var rh = body.BoneRotation((int)RagdollJoint.HeadTop);
            var fm = new Matrix4x4(_fx.X, _fx.Y, _fx.Z, 0f, _fy.X, _fy.Y, _fy.Z, 0f, _fz.X, _fz.Y, _fz.Z, 0f, 0f, 0f, 0f, 1f) * Matrix4x4.CreateFromQuaternion(rh);
            var joints = fk.Positions;
            var ft = Vector3.Transform(_faceC - RagdollSkeleton.Rest[(int)RagdollJoint.Head], rh) + joints[(int)RagdollJoint.Head];
            face.Commit(sc, _faceStart[a], in fm, ft);
            var pel = joints[(int)RagdollJoint.Pelvis];
            var pose = new SplatActorPose { Root = new Vector3(pel.X, 0f, pel.Z), Rotation = Quaternion.Identity, Height = 1.75f };
            _shadow[a].SetActorSkeleton(joints, in pose);
            _shadow[a].Enabled = ShadowsEnabled;
            if (ShadowsEnabled) _shadow[a].BuildDepthMap();
        }
        var o = _propPos;
        var op = _propVisible ? 0.96f : 0f;
        for (var k = 0; k < PropSplats; k++)
            sc.Write(_propStart + k, o + _propLocal[k], _propA[k], _propB[k], _propN[k], new Vector3(0.012f, 0.012f, 0.003f), op, _propRgb[k], 7);
        LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public VolumetricDeterminantClampingGuard BodyGuard { get; } = new();

    /// <summary>카메라 1 대 통합 렌더 → 톤 매핑 RGB24(상 → 하 · W×H×3) 버스 프레임(무할당).</summary>
    public void Render(in SplatCamera cam, Span<byte> rgb)
    {
        Raster.Render(Scene, in cam, CullGuard, Sh);
        var R = Raster.ColorR; var G = Raster.ColorG; var B = Raster.ColorB; var T = Raster.Transmittance;
        var bg = RealtimeGaussianSplatRasterizerEngine.Background;
        for (int p = 0, o = 0; p < W * H; p++, o += 3)
        {
            rgb[o] = Tone(R[p] + T[p] * bg.X); rgb[o + 1] = Tone(G[p] + T[p] * bg.Y); rgb[o + 2] = Tone(B[p] + T[p] * bg.Z);
        }
        var ms = Raster.LastFrameMs;
        LastRenderMs = ms; if (ms > MaxRenderMs && Renders > 3) MaxRenderMs = ms;
        Renders++;
    }

    public static byte Tone(float v) { v = MathF.Max(v, 0f); return (byte)Math.Clamp((int)(MathF.Sqrt(v / (1f + v * 0.1f)) * 255f + 0.5f), 0, 255); }

    public void Dispose()
    {
        Raster.Dispose();
        for (var a = 0; a < _avatars; a++) _body[a].Dispose();
    }
}
