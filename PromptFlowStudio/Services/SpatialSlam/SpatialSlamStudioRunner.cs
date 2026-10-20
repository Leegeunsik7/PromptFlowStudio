using System.Numerics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.SpatialSlam;

/// <summary>
/// [PromptFlow Studio v16.98.0] 🗺️ AI 신경망 공간 SLAM &amp; 3D 환경 재구성 스튜디오 러너.
/// 합성 실내(SlamSyntheticRoom)를 루프 궤적으로 촬영: 깊이 렌더(잡음) → SLAM 추적 → TSDF 융합(깊이 또는 라이다 스윕 · 추정 자세) →
/// 10프레임마다 메쉬 추출 + RANSAC 평면 → 매 프레임 앵커 가드. 루프 폐쇄 시 키프레임 깊이로 TSDF 재융합(보정 궤적).
/// </summary>
public sealed class SpatialSlamStudioRunner : IDisposable
{
    public const int W = 160, H = 120, FramesPerLoop = 240, MeshEvery = 10, LidarBeams = 16, LidarAz = 180;
    public const float F = 120f, Cx = 80f, Cy = 60f;
    public static readonly Vector3 PathCenter = new(-0.6f, 1.4f, 0f);
    public const float PathRadius = 1.0f;

    private readonly object _gate = new();
    private readonly float[] _depth = new float[W * H];
    private readonly float[] _lx = new float[LidarBeams * LidarAz], _ly = new float[LidarBeams * LidarAz], _lz = new float[LidarBeams * LidarAz];
    private readonly float[] _cloudX = new float[W * H], _cloudY = new float[W * H], _cloudZ = new float[W * H];
    private Thread? _loop;
    private volatile bool _running;
    private long _frame;
    private int _cloudN, _refuses;

    public SpatialSlamStudioRunner(IServiceProvider sp)
        : this((NeuralSpatialSlamTrackingEngine?)sp.GetService(typeof(NeuralSpatialSlamTrackingEngine)) ?? new NeuralSpatialSlamTrackingEngine(W, H, F, F, Cx, Cy),
               (LidarPointCloudMeshReconstructionSynthesizer?)sp.GetService(typeof(LidarPointCloudMeshReconstructionSynthesizer)) ?? new LidarPointCloudMeshReconstructionSynthesizer(),
               (SpatialPlaneDetectionAnchorGuard?)sp.GetService(typeof(SpatialPlaneDetectionAnchorGuard)) ?? new SpatialPlaneDetectionAnchorGuard()) { }

    public SpatialSlamStudioRunner(NeuralSpatialSlamTrackingEngine slam, LidarPointCloudMeshReconstructionSynthesizer mesh, SpatialPlaneDetectionAnchorGuard guard)
    {
        Slam = slam; Mesh = mesh; Guard = guard;
        Guard.Surface = mesh;
        Slam.LoopClosed += OnLoopClosed;
        Slam.InjectDriftDeg = 0.04f; Slam.InjectDriftMm = 1.5f;
        Restart();
    }

    public event Action? OnChange;

    public NeuralSpatialSlamTrackingEngine Slam { get; }
    public LidarPointCloudMeshReconstructionSynthesizer Mesh { get; }
    public SpatialPlaneDetectionAnchorGuard Guard { get; }
    public bool IsRunning => _running;
    public bool LidarMode { get; set; }
    public float DepthNoise { get; set; } = 0.0015f;
    public int Hz { get; set; } = 15;
    public long Frame => Interlocked.Read(ref _frame);
    public double LastTickMs { get; private set; }
    public int Refuses => _refuses;
    public string? LastAction { get; private set; }
    public string? LastExport { get; private set; }
    public int CloudCount => _cloudN;
    public Vector3 CloudPoint(int i) => new(_cloudX[i], _cloudY[i], _cloudZ[i]);
    public SlamPose GroundTruth(long frame) => SlamSyntheticRoom.Trajectory((double)frame / FramesPerLoop, PathCenter, PathRadius);

    /// <summary>초기화(궤적 · TSDF · 앵커 데모 배치).</summary>
    public void Restart()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _frame, 0);
            Slam.Reset(GroundTruth(0));
            Mesh.Reset();
            Guard.ClearAnchors();
            Guard.AddAnchor("의자 소품(부양)", AnchorKind.Prop, new Vector3(-1.6f, 1.1f, 0.6f), 0.25f, 0.9f);
            Guard.AddAnchor("꽃병(테이블 위 부양)", AnchorKind.Prop, new Vector3(1.2f, 1.25f, -0.2f), 0.08f, 0.3f);
            Guard.AddAnchor("디지털 휴먼(벽 침범)", AnchorKind.DigitalHuman, new Vector3(2.85f, 0f, 0.2f), 0.3f, 1.75f);
            Guard.AddAnchor("조명 소품(바닥 관통)", AnchorKind.Prop, new Vector3(0.2f, -0.3f, 1.6f), 0.15f, 1.2f);
            _refuses = 0;
            LastAction = "↺ 재시작";
        }
        Raise();
    }

    /// <summary>1 센서 프레임(무할당 · 메쉬/평면 주기 제외).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var f = Interlocked.Increment(ref _frame) - 1;
            var gt = GroundTruth(f);
            SlamSyntheticRoom.RenderDepth(gt, W, H, F, F, Cx, Cy, Mesh.MaxDepth, DepthNoise, (ulong)f * 2654435761UL + 7, _depth);
            Slam.Track(_depth, f, gt);
            var pose = Slam.Pose;
            if (LidarMode)
            {
                var n = SlamSyntheticRoom.LidarSweep(gt, LidarBeams, LidarAz, Mesh.MaxDepth, DepthNoise * 2, (ulong)f + 11, _lx, _ly, _lz);
                Mesh.IntegratePoints(_lx, _ly, _lz, n, pose);
            }
            else Mesh.IntegrateDepth(_depth, W, H, F, F, Cx, Cy, pose);
            _cloudN = Slam.CopyCurrentPointsWorld(_cloudX, _cloudY, _cloudZ, 3);
            if (f % MeshEvery == MeshEvery - 1) Remesh();
            Guard.Guard(1f / Math.Max(1, Hz));
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>메쉬 추출 + RANSAC 평면(콜드 주기).</summary>
    public void Remesh()
    {
        Mesh.ExtractMesh();
        var stride = Math.Max(1, (Mesh.VertexCount + SpatialPlaneDetectionAnchorGuard.MaxPoints - 1) / SpatialPlaneDetectionAnchorGuard.MaxPoints);
        Guard.Detect(Mesh.VX, Mesh.VY, Mesh.VZ, Mesh.NX, Mesh.NY, Mesh.NZ, stride);
    }

    private void OnLoopClosed(LoopClosure lc)
    {
        // 키프레임 깊이 + 보정 자세로 TSDF 재융합(콜드 · 추적 스레드 동기)
        Mesh.Reset();
        for (var k = 0; k < Slam.KeyframeCount; k++) Mesh.IntegrateDepth(Slam.KeyframeDepth(k), W, H, F, F, Cx, Cy, Slam.KeyframePose(k));
        _refuses++;
        LastAction = "🔁 루프 폐쇄 #" + Slam.LoopCount + " · 보정 " + (lc.CorrectionM * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cm / "
            + lc.CorrectionDeg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "° · TSDF 재융합(키프레임 " + Slam.KeyframeCount + ")";
    }

    /// <summary>OpenUSD(usda) + 3DGS 변환 내보내기 → 경로.</summary>
    public string Export(string? dir = null)
    {
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "slam");
        Directory.CreateDirectory(dir);
        int pts, splats;
        var layer = new UsdLayer("slam.usda");
        var gs = new GaussianScene(1024);
        lock (_gate)
        {
            pts = Mesh.ExportUsd(layer);
            for (var i = 0; i < Guard.PlaneCount; i++)
            {
                ref readonly var p = ref Guard.Plane(i);
                var prim = layer.AddPath("/World/Plane_" + p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), UsdSpec.Def, UsdPrimType.Cube);
                if (prim < 0) continue;
                layer.SetTranslate(prim, p.Centroid.X, p.Centroid.Y, p.Centroid.Z);
                layer.SetSize(prim, 1f);
                var ext = p.U * (p.UMax - p.UMin) + p.V * (p.VMax - p.VMin) + p.Normal * 0.02f;
                layer.SetScale(prim, Math.Max(0.02f, MathF.Abs(ext.X)), Math.Max(0.02f, MathF.Abs(ext.Y)), Math.Max(0.02f, MathF.Abs(ext.Z)));
                layer.SetColor(prim, p.Kind == PlaneKind.Floor ? 0.4f : 0.8f, 0.6f, p.Kind == PlaneKind.Wall ? 0.9f : 0.5f);
            }
            splats = Mesh.ExportGaussians(gs);
        }
        var buf = new byte[1 << 20];
        var n = OpenUsdSpatialAssetCompositorEngine.WriteUsda(layer, buf);
        var path = Path.Combine(dir, "slam-scene.usda");
        if (n > 0)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, buf.AsSpan(0, n).ToArray());
            File.Move(tmp, path, overwrite: true);
        }
        LastExport = "OpenUSD 점 " + pts + " · 평면 프림 " + Guard.PlaneCount + " · 3DGS 스플랫 " + splats + (n > 0 ? " → " + path : " (usda 버퍼 초과)");
        Raise();
        return path;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SLAM" };
            _loop.Start();
            LastAction = "▶ SLAM 가동 · " + (LidarMode ? "라이다" : "RGB-D") + " 융합";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ SLAM 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Slam.Step", ex); }
            n++;
            var wait = n * 1000.0 / Math.Max(1, Hz) - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Slam.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Slam.LoopClosed -= OnLoopClosed;
    }
}
