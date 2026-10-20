using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Metaverse;

/// <summary>
/// [PromptFlow Studio v16.61.0] 🥽 공간 XR 메타버스 스튜디오 러너 — v16.54 4D 월드(분기 서사) 진행 → 3DGS 스테이지 미러 → 디바이스(시선/핀치) 브릿지 →
/// 6DoF 오빗 헤드 포즈 → 스테레오 렌더 → 관객 상태/이벤트 동기화 → 홀로그램 합성 → XRS1 씬 브로드캐스트(루프백 미러 검증) · 30Hz · <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class SpatialXrStudioRunner : IDisposable
{
    public const int TickHz = 30, WorldPreviewW = 64, WorldPreviewH = 36;
    public const string DemoPrompt = "노란 고무 공이 받침대 위에서 2초 후 떨어진다. 빨간 상자가 공 오른쪽에 있다. 카메라가 공을 따라간다. 정오 햇빛";

    private readonly object _gate = new();
    private readonly SpatialXrMetaverseBroadcastingEngine _xr;
    private readonly SpatialComputingInteractionBridge _bridge;
    private readonly NeuralHologramMultiuserStateSync _sync;
    private readonly AiGenerative4dWorldModelEngine _world = new();
    private readonly InteractiveVideoBranchingTranspiler _branch = new();
    private readonly NeuralSceneGraphPromptOptimizer _opt = new();
    private readonly GaussianSplattingStudioRunner? _gsplat;
    private readonly SpatialSceneMirror _mirror = new();
    private readonly byte[] _worldRgb = new byte[WorldPreviewW * WorldPreviewH * 3];
    private readonly byte[] _held = new byte[NeuralHologramMultiuserStateSync.StatePacketSize];
    private readonly uint[] _audSeq = new uint[NeuralHologramMultiuserStateSync.MaxUsers];
    private readonly XrInteractionEvent[] _evts = new XrInteractionEvent[16];
    private byte[] _back = Array.Empty<byte>(), _front = Array.Empty<byte>();
    private CancellationTokenSource? _cts;
    private BranchProgram? _program;
    private long _frameSeq, _evCursor, _nowUs;
    private double _t, _tickMs;
    private uint _rng = 0x6C8E9CF5u;
    private bool _hasHeld;
    private int _audience = 6, _heldUser;
    private Vector3 _pan;

    public SpatialXrStudioRunner(IServiceProvider sp)
        : this((SpatialXrMetaverseBroadcastingEngine?)sp.GetService(typeof(SpatialXrMetaverseBroadcastingEngine)) ?? new SpatialXrMetaverseBroadcastingEngine(),
               (SpatialComputingInteractionBridge?)sp.GetService(typeof(SpatialComputingInteractionBridge)) ?? new SpatialComputingInteractionBridge(),
               (NeuralHologramMultiuserStateSync?)sp.GetService(typeof(NeuralHologramMultiuserStateSync)) ?? new NeuralHologramMultiuserStateSync(),
               sp.GetService(typeof(GaussianSplattingStudioRunner)) as GaussianSplattingStudioRunner) { }

    public SpatialXrStudioRunner(SpatialXrMetaverseBroadcastingEngine xr, SpatialComputingInteractionBridge bridge, NeuralHologramMultiuserStateSync sync, GaussianSplattingStudioRunner? gsplat = null)
    {
        _xr = xr; _bridge = bridge; _sync = sync; _gsplat = gsplat;
        _bridge.AttachBranch(_branch);
        if (_bridge.TargetCount == 0) _bridge.BuildDefaultPanel();
        EnsureFrame();
        GenerateWorld(DemoPrompt);
    }

    public event Action? OnChange;

    public SpatialXrMetaverseBroadcastingEngine Xr => _xr;
    public SpatialComputingInteractionBridge Bridge => _bridge;
    public NeuralHologramMultiuserStateSync Sync => _sync;
    public AiGenerative4dWorldModelEngine World => _world;
    public InteractiveVideoBranchingTranspiler Branch => _branch;
    public SpatialSceneMirror Mirror => _mirror;
    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public double TimeSec => _t;
    public double LastTickMs => Volatile.Read(ref _tickMs);
    public bool SimulateDevices { get; set; } = true;
    public bool SimulateAudience { get; set; } = true;
    public float PacketLoss { get; set; } = 0.08f;
    public int Audience { get => _audience; set => _audience = Math.Clamp(value, 0, 24); }
    public Vector3 Pan { get => _pan; set => _pan = Vector3.Clamp(value, new Vector3(-3f, -1f, -3f), new Vector3(3f, 2f, 3f)); }
    public bool CanImport3dgs => _gsplat is not null && _gsplat.Scene.Count > 0;
    public string? CurrentCaption { get; private set; }
    public string? LastAction { get; private set; }
    public int FrameWidth => _xr.EyeWidth * 2;
    public int FrameHeight => _xr.EyeHeight;

    private void EnsureFrame()
    {
        var n = _xr.EyeWidth * 2 * _xr.EyeHeight * 3;
        if (_back.Length == n) return;
        _back = new byte[n]; _front = new byte[n];
    }

    /// <summary>프롬프트 → 4D 월드 + 기본 분기 스토리 로드.</summary>
    public int GenerateWorld(string prompt)
    {
        int objs;
        lock (_gate)
        {
            var (graph, _) = _opt.AnalyzeAndOptimize(prompt);
            _world.Fps = TickHz;
            _opt.Compile(graph, _world);
            var story = _branch.Transpile(InteractiveVideoBranchingTranspiler.BuildDefaultStory(_world), _world);
            _program = story.Program;
            if (_program is not null) { _branch.ResetStats(); _branch.Load(_program, _world); }
            _xr.SyncWorld(_world);
            _xr.RequestKeyFrame();
            objs = _world.ObjectCount;
        }
        LastAction = "🌐 4D 월드 → 6DoF 스테이지 · 객체 " + objs.ToString(CultureInfo.InvariantCulture);
        Raise();
        return objs;
    }

    /// <summary>v16.58 3DGS 스튜디오 장면을 공간 스테이지로 임포트.</summary>
    public int Import3dgs()
    {
        if (_gsplat is null || _gsplat.Scene.Count == 0) return 0;
        int n;
        lock (_gate) { n = _xr.ImportStage(_gsplat.Scene, 30000, 1.6f, new Vector3(0f, 0.2f, 0f)); _xr.SyncWorld(_world); }
        LastAction = _xr.LastAction;
        Raise();
        return n;
    }

    public void ResetStage()
    {
        lock (_gate) { _xr.BuildDefaultStage(); _xr.SyncWorld(_world); }
        LastAction = "🏟 기본 스테이지";
        Raise();
    }

    /// <summary>6DoF 트랙패드 — 오빗(deg) + 팬(m) + 줌 배율.</summary>
    public void Move(float dYawDeg, float dPitchDeg, Vector3 dPan, float zoom = 1f)
    {
        _bridge.Orbit(dYawDeg, dPitchDeg, zoom);
        Pan += dPan;
    }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f; }

    /// <summary>오빗 상태 → 헤드 포즈(피벗 응시).</summary>
    public XrPose OrbitPose()
    {
        var pivot = new Vector3(0f, 1.0f, 0f) + _pan;
        var pose = XrPose.FromYawPitch(Vector3.Zero, _bridge.CameraYawDeg, -_bridge.CameraPitchDeg);
        return new XrPose(pivot - pose.Forward * _bridge.CameraDistance, pose.Orientation);
    }

    /// <summary>1틱(무할당 · 스토리 엔딩 재시작 시 상태 문자열 1회 제외).</summary>
    public void TickOnce(double dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        Span<byte> pkt = stackalloc byte[64];
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Xr.Metaverse.Runner.Tick");
            _t += dt;
            _nowUs = (long)(_t * 1e6);
            if (_program is not null && !_branch.Tick(_worldRgb, WorldPreviewW, WorldPreviewH)) _branch.Load(_program, _world);
            var p = _branch.Program; var seg = _branch.CurrentSegment;
            CurrentCaption = p is not null && seg >= 0 ? p.Segments[seg].Caption : null;
            _sync.Subtitle = CurrentCaption;
            _xr.SyncWorld(_world);
            if (_world.ObjectCount > 0) _sync.SetObjectAnchor(0, _xr.EntityPosition(0));

            var head = OrbitPose();
            _xr.UpdateHead(head, _nowUs);
            if (SimulateDevices) SimulateDevicesTick(head);
            if (SimulateAudience) SimulateAudienceTick(pkt);

            var n = _bridge.ReadEvents(ref _evCursor, _evts);
            for (var i = 0; i < n; i++)
            {
                ref readonly var e = ref _evts[i];
                if (e.Kind == XrEventKind.Select && e.Target >= 0) _sync.EmitBurst(_bridge.Target(e.Target).Center, new Vector3(0.3f, 1f, 1f), 20);
                else if (e.Kind == XrEventKind.OverlayToggled) _sync.Layers = (HoloLayer)(_bridge.OverlayMask & (int)HoloLayer.All);
            }

            _xr.RenderStereo(out var camL, out var camR);
            _sync.StepVfx((float)dt);
            _sync.Composite(_xr.LeftBuffer, _xr.RightBuffer, _xr.LeftDepth, _xr.RightDepth, camL, camR, _nowUs);
            DrawTargets(_xr.LeftBuffer, _xr.LeftDepth, camL);
            DrawTargets(_xr.RightBuffer, _xr.RightDepth, camR);
            PackSideBySide();
            _mirror.Apply(_xr.Broadcast(_nowUs));
            if (_mirror.NeedsKey) _xr.RequestKeyFrame();
            Interlocked.Increment(ref _frameSeq);
        }
        Volatile.Write(ref _tickMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
    }

    private void DrawTargets(Span<byte> rgb, ReadOnlySpan<float> dep, in SplatCamera cam)
    {
        var hover = _bridge.HoverTarget;
        for (var i = 0; i < _bridge.TargetCount; i++)
        {
            var t = _bridge.Target(i);
            var col = t.Kind switch { HoloTargetKind.OverlayToggle => ((_bridge.OverlayMask >> t.Arg) & 1) != 0 ? new Vector3(0.3f, 1f, 0.6f) : new Vector3(0.4f, 0.4f, 0.45f), HoloTargetKind.CameraAngle => new Vector3(1f, 0.75f, 0.2f), _ => new Vector3(0.9f, 0.4f, 1f) };
            NeuralHologramMultiuserStateSync.DrawDisc(rgb, dep, cam, t.Center, t.Radius, i == hover ? Vector3.One : col, i == hover ? 0.85f : 0.5f);
        }
    }

    private void PackSideBySide()
    {
        int w = _xr.EyeWidth, h = _xr.EyeHeight, row = w * 3;
        var l = _xr.LeftRgb; var r = _xr.RightRgb;
        for (var y = 0; y < h; y++)
        {
            l.Slice(y * row, row).CopyTo(_back.AsSpan(y * row * 2, row));
            r.Slice(y * row, row).CopyTo(_back.AsSpan(y * row * 2 + row, row));
        }
        (_back, _front) = (_front, _back);
    }

    /// <summary>Vision Pro/Quest 시뮬: 타깃 순회 응시(3s 주기) → 0.45s 시점 핀치(Look-and-Pinch) · 매 4주기 빈 공간 드래그 오빗.</summary>
    private void SimulateDevicesTick(in XrPose head)
    {
        var cycle = (int)(_t / 3.0);
        var phase = _t - cycle * 3.0;
        var tc = _bridge.TargetCount;
        var drag = cycle % 4 == 3;
        var target = tc > 0 && !drag ? cycle % tc : -1;
        Vector3 dir;
        if (target >= 0) dir = _bridge.Target(target).Center - head.Position;
        else dir = head.Forward + head.Up * 0.6f;
        dir = Vector3.Normalize(dir) + new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * 0.004f;
        _bridge.ProcessGaze(new XrGazeSample(head.Position, dir, 0.95f, _nowUs));
        var palm = head.Position + head.Forward * 0.45f - head.Up * 0.25f + head.Right * 0.15f;
        if (drag && phase is > 0.4 and < 1.4) palm += head.Right * (float)((phase - 0.4) * 0.12);
        var pinch = drag ? phase is > 0.4 and < 1.4 : phase is > 0.45 and < 0.6;
        var gap = pinch ? 0.008f : 0.06f;
        _bridge.ProcessHand(new XrHandSample(XrHand.Right, palm + new Vector3(gap, 0f, 0f), palm, palm, 0.98f, _nowUs));
    }

    /// <summary>관객 시뮬: 원주 보행 · 시선 = 무대 중심 · 손실/역순(재정렬) 주입 · 환호/잡기 이벤트.</summary>
    private void SimulateAudienceTick(Span<byte> pkt)
    {
        if (_hasHeld && _heldUser >= _audience) _hasHeld = false;
        for (var u = 0; u < _audience; u++)
        {
            var id = (ushort)(10 + u);
            var a = (float)(_t * (0.15 + 0.03 * u)) + u * (MathF.PI * 2f / Math.Max(1, _audience));
            var pos = new Vector3(MathF.Sin(a) * 2.6f, 0f, MathF.Cos(a) * 2.6f);
            var gaze = Vector3.Normalize(new Vector3(0f, 1.0f, 0f) - (pos + new Vector3(0f, 1.55f, 0f)));
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(gaze.X, gaze.Z));
            NeuralHologramMultiuserStateSync.WriteState(pkt, id, ++_audSeq[u], _nowUs, pos, rot, gaze);
            var roll = Rand();
            if (roll < PacketLoss) continue;
            var st = pkt[..NeuralHologramMultiuserStateSync.StatePacketSize];
            if (roll < PacketLoss + 0.04f && !_hasHeld) { st.CopyTo(_held); _hasHeld = true; _heldUser = u; continue; }
            _sync.Receive(st, _nowUs);
            if (_hasHeld && _heldUser == u) { _sync.Receive(_held, _nowUs); _hasHeld = false; }   // 같은 사용자 이전 seq 지연 도착 → 역순 폐기
        }
        if (_audience == 0) return;
        var f = _frameSeq;
        if (f % 60 == 17)
        {
            var who = (ushort)(10 + (int)(Rand() * _audience) % _audience);
            var n = NeuralHologramMultiuserStateSync.WriteEvent(pkt, new SharedEvent(SharedEventKind.Cheer, who, 0, _sync.Lamport + 1));
            _sync.Receive(pkt[..n], _nowUs);
        }
        if (f % 120 == 40 || f % 120 == 100)
        {
            var who = (ushort)(10 + (int)(f / 120) % _audience);
            var kind = f % 120 == 40 ? SharedEventKind.Grab : SharedEventKind.Release;
            var n = NeuralHologramMultiuserStateSync.WriteEvent(pkt, new SharedEvent(kind, who, 0, _sync.Lamport + 1));
            _sync.Receive(pkt[..n], _nowUs);
        }
    }

    /// <summary>SBS 스테레오 프레임 복사(dst ≥ FrameWidth×FrameHeight×3).</summary>
    public void CopyFrame(Span<byte> dst) { lock (_gate) _front.AsSpan().CopyTo(dst); }

    public void CopyWorldPreview(Span<byte> dst) { lock (_gate) _worldRgb.AsSpan().CopyTo(dst); }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 공간 스트리밍";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(0.2, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                TickOnce(dt);
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Xr.Metaverse.Loop", ex); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Xr.Metaverse.Runner.OnChange", ex); }
    }

    public void Dispose() { Stop(); _xr.Dispose(); }
}
