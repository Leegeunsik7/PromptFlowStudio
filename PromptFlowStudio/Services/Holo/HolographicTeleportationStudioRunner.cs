using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.Neuro;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Holo;

/// <summary>
/// [PromptFlow Studio v16.77.0] 📡 AI 홀로그래픽 텔레포테이션 &amp; 3D 텔레프레젠스 스튜디오 러너(30 Hz · <see cref="TickOnce"/> 무할당).
/// 송신: 모캡 FK 연기(말하기 · 손 인사) → 뼈 캡슐 스플랫 아바타 + USD 무대 프림(3DGS 바인딩) + 관절 + 외피 메시 → 공간 압축 → MTU 분할 → 손실 링크 →
/// 수신: 재조립/디코드(PLI 키프레임 요청) → 시선 교정 · BCI 표정 · 바이노럴 음성(립싱크 지연 정렬) → 앵커 기준 공유 월드 P2P 동기화.
/// </summary>
public sealed class HolographicTeleportationStudioRunner : IDisposable
{
    public const int Hz = 30, SampleRate = 48000, AudioBlock = SampleRate / Hz, PerBone = 28, HeadSplats = 60;
    public const float LatencyBudgetMs = 50f;

    public const string StageUsda = """
#usda 1.0
(
    defaultPrim = "Stage"
    upAxis = "Y"
)

def Xform "Stage"
{
    def Cube "Podium"
    {
        double size = 0.6
        double3 xformOp:translate = (0.9, 0.3, 0.3)
    }
    def Sphere "Lamp"
    {
        double radius = 0.25
        double3 xformOp:translate = (-0.9, 1.9, 0)
    }
    def Cube "FloorMark"
    {
        double size = 0.2
        double3 xformOp:translate = (0, 0.02, 0)
    }
}
""";

    private readonly object _gate = new();
    private readonly RealtimeVolumetricHologramStreamerEngine _stream;
    private readonly AiTelepresenceAvatarSynthesizer _avatar;
    private readonly SpatialAnchorHolographicWorldRouter _router;
    private readonly NeuroBioSignalEmotionParserEngine? _neuro;
    private readonly OpenUsdSpatialAssetCompositorEngine _usd = new();
    private readonly UsdLayer _stage = new("stage.usda");
    private readonly GaussianScene _sender = new(1024), _received = new(1024);
    private readonly Quaternion[] _local = new Quaternion[MocapSkeleton.Body], _global = new Quaternion[MocapSkeleton.Body];
    private readonly Vector3[] _joints = new Vector3[MocapSkeleton.Body];
    private readonly Vector3[] _mesh = new Vector3[MocapSkeleton.Body * 6];
    private readonly int[] _splatBone;
    private readonly float[] _splatT;
    private readonly Vector3[] _splatOff;
    private readonly byte[] _dgram = new byte[RealtimeVolumetricHologramStreamerEngine.Mtu];
    private readonly float[] _outL = new float[AudioBlock], _outR = new float[AudioBlock];
    private readonly int _propCount, _entity;
    private CancellationTokenSource? _cts;
    private long _frame;
    private ulong _rng = 0xD00D_1677UL;
    private float _peak, _rms, _viewerAngle = 35f;
    private bool _linkFault;

    public HolographicTeleportationStudioRunner(IServiceProvider sp)
        : this((RealtimeVolumetricHologramStreamerEngine?)sp.GetService(typeof(RealtimeVolumetricHologramStreamerEngine)) ?? new RealtimeVolumetricHologramStreamerEngine(),
               (AiTelepresenceAvatarSynthesizer?)sp.GetService(typeof(AiTelepresenceAvatarSynthesizer)) ?? new AiTelepresenceAvatarSynthesizer(),
               (SpatialAnchorHolographicWorldRouter?)sp.GetService(typeof(SpatialAnchorHolographicWorldRouter)) ?? new SpatialAnchorHolographicWorldRouter(),
               (NeuroBioSignalEmotionParserEngine?)sp.GetService(typeof(NeuroBioSignalEmotionParserEngine))) { }

    public HolographicTeleportationStudioRunner(RealtimeVolumetricHologramStreamerEngine stream, AiTelepresenceAvatarSynthesizer avatar,
        SpatialAnchorHolographicWorldRouter router, NeuroBioSignalEmotionParserEngine? neuro = null)
    {
        _stream = stream; _avatar = avatar; _router = router; _neuro = neuro;
        UsdaReader.Parse(Encoding.UTF8.GetBytes(StageUsda), _stage);
        _usd.SetLayer(0, _stage);
        _usd.Compose();
        _usd.Evaluate(0);
        _propCount = _usd.BindToGaussians(_sender);

        // 뼈 캡슐 샘플(부모→자식 선분 · 반경 오프셋 = 레스트 프레임) + 머리 구
        var bones = MocapSkeleton.Body - 1;
        var total = bones * PerBone + HeadSplats;
        _splatBone = new int[total]; _splatT = new float[total]; _splatOff = new Vector3[total];
        var k = 0;
        for (var j = 1; j < MocapSkeleton.Body; j++)
        {
            var axis = Vector3.Normalize(MocapSkeleton.RestOffset[j]);
            var perp = MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(axis, perp)); var v = Vector3.Cross(axis, u);
            var radius = j is MocapSkeleton.Spine1 or MocapSkeleton.Spine2 or MocapSkeleton.Spine3 ? 0.13f : j is MocapSkeleton.LHip or MocapSkeleton.RHip or MocapSkeleton.LKnee or MocapSkeleton.RKnee ? 0.07f : 0.045f;
            for (var s = 0; s < PerBone; s++, k++)
            {
                var ang = s * 2.39996f; // 황금각
                _splatBone[k] = j; _splatT[k] = (s + 0.5f) / PerBone;
                _splatOff[k] = (u * MathF.Cos(ang) + v * MathF.Sin(ang)) * radius;
            }
        }
        for (var s = 0; s < HeadSplats; s++, k++)
        {
            // 피보나치 구(반경 11 cm)
            var y = 1f - 2f * (s + 0.5f) / HeadSplats; var r = MathF.Sqrt(1f - y * y); var ang = s * 2.39996f;
            _splatBone[k] = MocapSkeleton.Head; _splatT[k] = -1f;
            _splatOff[k] = new Vector3(r * MathF.Cos(ang), y, r * MathF.Sin(ang)) * 0.11f + new Vector3(0, 0.1f, 0);
        }
        for (var i = 0; i < total; i++)
        {
            var b = _splatBone[i];
            var col = b == MocapSkeleton.Head ? new Vector3(0.95f, 0.78f, 0.66f) : b <= MocapSkeleton.RFoot && b != MocapSkeleton.Spine1 && b != MocapSkeleton.Spine2 && b != MocapSkeleton.Spine3 ? new Vector3(0.18f, 0.22f, 0.4f) : new Vector3(0.2f, 0.75f, 0.85f);
            _sender.Add(Vector3.Zero, Quaternion.Identity, new Vector3(0.03f), 0.85f, col);
        }
        for (var j = 0; j < MocapSkeleton.Body; j++) _local[j] = Quaternion.Identity;

        // 공유 월드: 피어 0 = 호스트(월드 원점) · 1 = 원격 출연자 · 2/3 = 시청자 — 앵커 3개(무대 모서리)
        _router.Reset();
        _router.AddPeer("호스트·서울"); _router.AddPeer("출연자·LA"); _router.AddPeer("시청자·런던"); _router.AddPeer("시청자·도쿄");
        _router.SetLink(0, 1, true, 62f, 0.01f); _router.SetLink(0, 2, true, 120f, 0.01f); _router.SetLink(0, 3, true, 16f, 0f);
        _router.SetLink(1, 2, true, 70f, 0.01f); _router.SetLink(1, 3, true, 54f, 0.01f); _router.SetLink(2, 3, true, 110f, 0.01f);
        var a0 = _router.AddAnchor(new Vector3(0, 0, 0), Quaternion.Identity);
        _router.AddAnchor(new Vector3(3, 0, 0), Quaternion.Identity);
        _router.AddAnchor(new Vector3(0, 0, 3), Quaternion.Identity);
        for (var p = 1; p < _router.PeerCount; p++)
        {
            // 피어별 실측 로컬 좌표계(요 · 이동) → 앵커 관측(±2 mm 잡음)
            var yaw = 0.6f * p; var t = new Vector3(1.5f * p, 0, -0.8f * p);
            for (var a = 0; a < _router.AnchorCount; a++)
            {
                var w = _router.AnchorPosition(a) - t;
                float c = MathF.Cos(-yaw), s = MathF.Sin(-yaw);
                var l = new Vector3(w.X * c + w.Z * s, w.Y, -w.X * s + w.Z * c) + new Vector3(Noise() * 0.002f, 0, Noise() * 0.002f);
                _router.ObserveAnchor(p, a, l);
            }
            _router.SolvePeer(p);
        }
        _entity = _router.AddEntity(1, a0, new Vector3(1.5f, 0, 1.5f));
        TickOnce();
    }

    public event Action? OnChange;

    public RealtimeVolumetricHologramStreamerEngine Streamer => _stream;
    public AiTelepresenceAvatarSynthesizer Avatar => _avatar;
    public SpatialAnchorHolographicWorldRouter Router => _router;
    public GaussianScene Received => _received;
    public int PropSplats => _propCount;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public ReadOnlySpan<Vector3> Joints => _joints;
    public string? LastAction { get; private set; }

    /// <summary>패킷 손실률(0..0.3).</summary>
    public float LossRate { get; set; } = 0.01f;
    public float NetworkLatencyMs { get; set; } = 18f;
    public float JitterBufferMs { get; set; } = 8f;
    public float BandwidthMbps { get; set; } = 200f;
    public float LatencyMs { get; private set; }
    public float Kbps { get; private set; }
    public float PeakOut => _peak;
    public float RmsOut => _rms;
    public bool WithinBudget => LatencyMs < LatencyBudgetMs;
    public Vector3 RemoteCamera { get; } = new(0, 1.6f, 1.8f);
    public float ViewerAngleDeg { get => _viewerAngle; set => _viewerAngle = Math.Clamp(value, -80f, 80f); }
    public Vector3 ViewerEye { get { var r = _viewerAngle * (MathF.PI / 180f); return new Vector3(2.2f * MathF.Sin(r), 1.65f, 2.2f * MathF.Cos(r)); } }
    public bool LinkFault => _linkFault;

    public void SetQuantBits(int bits)
    {
        _stream.QuantBits = Math.Clamp(bits, RealtimeVolumetricHologramStreamerEngine.MinBits, RealtimeVolumetricHologramStreamerEngine.MaxBits);
        LastAction = "🧊 공간 양자화 " + _stream.QuantBits.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bit/축";
        Raise();
    }

    public void SetEyeContact(bool on)
    {
        _avatar.EyeContact = on;
        LastAction = on ? "👁 시선 교정 ON" : "👁 시선 교정 OFF";
        Raise();
    }

    /// <summary>출연자 ↔ 런던 시청자 직결 링크 장애 토글(릴레이 우회 검증).</summary>
    public void ToggleLinkFault()
    {
        lock (_gate)
        {
            _linkFault = !_linkFault;
            _router.SetLink(1, 2, !_linkFault, 70f, 0.01f);
        }
        LastAction = _linkFault ? "⚡ LA↔런던 직결 단절 → 릴레이 우회" : "🔗 LA↔런던 직결 복구";
        Raise();
    }

    /// <summary>1프레임(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Holo.Tick");
            var t = _frame / (float)Hz;
            Animate(t);
            MocapSkeleton.ForwardKinematics(MocapSkeleton.RestOffset, _local, new Vector3(0.03f * MathF.Sin(t * 0.7f), 0.95f + 0.01f * MathF.Sin(t * 2.1f), 0), _joints, _global);
            BuildSplatsAndMesh();

            // 송신 → 손실 링크 → 수신
            var now = (long)(_frame * (1_000_000.0 / Hz));
            var bytes = _stream.EncodeFrame(_sender, _joints, _mesh, now);
            var frags = _stream.LastFragments;
            for (var i = 0; i < frags; i++)
            {
                var len = _stream.WriteFragment(i, _dgram);
                if (NextFloat() < LossRate) continue;
                _stream.ReceiveDatagram(_dgram.AsSpan(0, len));
            }
            if (_stream.ReceiverWantsKeyframe) _stream.RequestKeyframe(); // PLI
            _stream.CopyReceivedSplats(_received);
            var serialMs = bytes * 8f / (BandwidthMbps * 1000f);
            LatencyMs = _stream.EncodeMs + serialMs + NetworkLatencyMs + JitterBufferMs + _stream.DecodeMs;
            Kbps = bytes * 8f * Hz / 1000f;

            // 아바타: 시선 · 표정 · 바이노럴(시청자 = 홀로그램 로컬 좌표)
            var joints = _stream.ReceivedJoints == MocapSkeleton.Body ? _stream.ReceivedJointPositions : _joints;
            var eye = ViewerEye;
            _avatar.UpdateGaze(joints, RemoteCamera, eye, 1f / Hz);
            var emo = _neuro is not null && _neuro.SubjectCount > 0 ? _neuro.StateAt(0) : SyntheticEmotion(t);
            Array.Clear(_outL); Array.Clear(_outR);
            _avatar.ProcessBlock(emo, ReadOnlySpan<float>.Empty, AudioBlock, eye, LatencyMs, NetworkLatencyMs + 5f, _outL, _outR);
            float pk = 0; double ss = 0;
            for (var k = 0; k < AudioBlock; k++) { var m = MathF.Max(MathF.Abs(_outL[k]), MathF.Abs(_outR[k])); pk = MathF.Max(pk, m); ss += _outL[k] * _outL[k]; }
            _peak = pk; _rms = (float)Math.Sqrt(ss / AudioBlock);

            // 공유 월드: 출연자(피어 1) 소유 홀로그램 = 골반 이동 반영
            _router.UpdateOwned(_entity, new Vector3(_joints[0].X, 0, _joints[0].Z), _avatar.HeadCorrection);
            _router.Tick(1000f / Hz);
            _frame++;
        }
    }

    private void Animate(float t)
    {
        // 말하는 상체 · 고개 끄덕임 · 왼팔 손 인사
        _local[MocapSkeleton.Spine2] = Quaternion.CreateFromYawPitchRoll(0.08f * MathF.Sin(t * 0.9f), 0.03f * MathF.Sin(t * 1.3f), 0);
        _local[MocapSkeleton.Neck] = Quaternion.CreateFromYawPitchRoll(0.15f * MathF.Sin(t * 0.5f), 0.08f * MathF.Sin(t * 2.4f), 0);
        _local[MocapSkeleton.LShoulder] = Quaternion.CreateFromYawPitchRoll(0, 0, 1.1f + 0.1f * MathF.Sin(t));
        _local[MocapSkeleton.LElbow] = Quaternion.CreateFromYawPitchRoll(0, 0, 0.9f + 0.45f * MathF.Sin(t * 5f));
        _local[MocapSkeleton.RShoulder] = Quaternion.CreateFromYawPitchRoll(0, 0, -1.25f);
        _local[MocapSkeleton.RElbow] = Quaternion.CreateFromYawPitchRoll(-0.6f, 0, 0);
    }

    private void BuildSplatsAndMesh()
    {
        var pos = _sender.Pos;
        var n = _splatBone.Length;
        for (var i = 0; i < n; i++)
        {
            var j = _splatBone[i];
            Vector3 p;
            if (_splatT[i] < 0) p = _joints[j] + Vector3.Transform(_splatOff[i], _global[j]);
            else
            {
                var par = MocapSkeleton.Parent[j];
                p = Vector3.Lerp(_joints[par], _joints[j], _splatT[i]) + Vector3.Transform(_splatOff[i], _global[par]);
            }
            var o = (_propCount + i) * 3;
            pos[o] = p.X; pos[o + 1] = p.Y; pos[o + 2] = p.Z;
        }
        _sender.Version++;
        // 외피 메시: 관절별 6정점(±X · ±Y · ±Z 7 cm)
        for (var j = 0; j < MocapSkeleton.Body; j++)
        {
            var c = _joints[j]; var b = j * 6; var g = _global[j];
            _mesh[b] = c + Vector3.Transform(new Vector3(0.07f, 0, 0), g); _mesh[b + 1] = c + Vector3.Transform(new Vector3(-0.07f, 0, 0), g);
            _mesh[b + 2] = c + Vector3.Transform(new Vector3(0, 0.07f, 0), g); _mesh[b + 3] = c + Vector3.Transform(new Vector3(0, -0.07f, 0), g);
            _mesh[b + 4] = c + Vector3.Transform(new Vector3(0, 0, 0.07f), g); _mesh[b + 5] = c + Vector3.Transform(new Vector3(0, 0, -0.07f), g);
        }
    }

    private static EmotionState SyntheticEmotion(float t)
    {
        var v = 0.55f + 0.3f * MathF.Sin(t * 0.21f); var a = 0.5f + 0.25f * MathF.Sin(t * 0.33f + 1f);
        return EmotionState.Neutral with { Valence = v, Arousal = a, Engagement = 0.7f, Fear = 0.15f, Confidence = 0.5f };
    }

    private float NextFloat()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }

    private float Noise() => NextFloat() * 2f - 1f;

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 홀로그래픽 텔레포테이션 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 홀로그래픽 텔레포테이션 정지";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Holo.Tick", ex); }
                if ((Frame & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Holo.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
