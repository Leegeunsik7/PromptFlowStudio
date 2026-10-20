using System.Numerics;
using PromptFlowStudio.Services.MasterControl;
using PromptFlowStudio.Services.MassiveCrowd;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.ArGraphics;

/// <summary>
/// [PromptFlow Studio v17.06.0] 📐 방송 AR 그래픽 &amp; 텔레스트레이션 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 경기(v17.05 아레나) + 관중(v17.04 군중 · 4,000명 · 15 Hz · 골 → 감정 전파) → 카메라 오퍼레이터(공 추적 팬/틸트/줌) →
/// 실측 자세 + 마운트 오프셋/FOV 오차 → FreeD D1 직렬화(엔코더 양자화 · 지터) → 엔진 수신 · 비디오 지연 정합 → AR 그래픽(트래킹 카메라) ·
/// 카메라 피드(실측 카메라 · 비디오 지연) → Key/Fill 가드 합성 · ST 2110-40 ANC 패킹/루프백 → 정합 오차(px) 계측.
/// 지면 = 피치 LiDAR 스캔(경사 · 잡음) → v16.98 SLAM RANSAC 바닥 평면.
/// </summary>
public sealed class BroadcastArGraphicsStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, PoseRing = 64, ScanPoints = 6000, GridLandmarks = 11 * 9, Landmarks = GridLandmarks + 4;
    public static readonly Vector3 CameraPos = new(50f, 14f, -26f);

    private readonly object _gate = new();
    private readonly float[] _align = new float[SeriesLen], _ms = new float[SeriesLen], _cov = new float[SeriesLen], _zoom = new float[SeriesLen];
    private readonly FreeDPose[] _truth = new FreeDPose[PoseRing];
    private readonly byte[] _pkt = new byte[FreeD.PacketBytes];
    private readonly byte[] _bmp;
    private readonly Vector3[] _lm = new Vector3[Landmarks];
    private readonly Vector2[] _obs = new Vector2[Landmarks];
    private readonly bool[] _seen = new bool[Landmarks];
    private readonly float[] _sx = new float[ScanPoints], _sy = new float[ScanPoints], _sz = new float[ScanPoints], _snx = new float[ScanPoints], _sny = new float[ScanPoints], _snz = new float[ScanPoints];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick, _frame, _seenEvents;
    private float _pan, _tilt = -12f, _fov = 30f;
    private ulong _rng = 0xA076_1D64_78BD_642FUL;

    public BroadcastArGraphicsStudioRunner(IServiceProvider sp)
        : this((FreeDTrackingProtocolLensDistortionEngine?)sp.GetService(typeof(FreeDTrackingProtocolLensDistortionEngine)) ?? new FreeDTrackingProtocolLensDistortionEngine(),
               (OnAirArTelestrationSportsTelemetrySynthesizer?)sp.GetService(typeof(OnAirArTelestrationSportsTelemetrySynthesizer)) ?? new OnAirArTelestrationSportsTelemetrySynthesizer(),
               (AncillaryDataKeyFillGraphicsCompositingGuard?)sp.GetService(typeof(AncillaryDataKeyFillGraphicsCompositingGuard)) ?? new AncillaryDataKeyFillGraphicsCompositingGuard())
    {
    }

    public BroadcastArGraphicsStudioRunner(FreeDTrackingProtocolLensDistortionEngine tracking, OnAirArTelestrationSportsTelemetrySynthesizer ar, AncillaryDataKeyFillGraphicsCompositingGuard guard, int crowd = 4000)
    {
        Tracking = tracking; Ar = ar; Guard = guard;
        Scene = new BroadcastArenaScene(0x1706);
        Crowd = new MassiveAgenticCrowdSimulationEngine(crowd, Math.Max(16, crowd)) { AutoGoals = false };
        Planes = new SpatialPlaneDetectionAnchorGuard();
        Truth = new LensCamera(tracking.Camera.Width, tracking.Camera.Height);
        _bmp = new byte[54 + ar.Width * ar.Height * 3];
        Array.Fill(_truth, default);
        ScanGround();
        Guard.SetCaption(ar.Caption);
        LastAction = "📋 대기 · FreeD D1 · 렌즈 " + tracking.Lens.Length + "점 표 · 지면 평면 " + (FloorPlane >= 0 ? "검출" : "없음");
    }

    public event Action? OnChange;

    public FreeDTrackingProtocolLensDistortionEngine Tracking { get; }
    public OnAirArTelestrationSportsTelemetrySynthesizer Ar { get; }
    public AncillaryDataKeyFillGraphicsCompositingGuard Guard { get; }
    public BroadcastArenaScene Scene { get; }
    public MassiveAgenticCrowdSimulationEngine Crowd { get; }
    public SpatialPlaneDetectionAnchorGuard Planes { get; }
    /// <summary>실측 카메라(피드 렌더 · 비디오 지연 적용).</summary>
    public LensCamera Truth { get; }
    /// <summary>실측 마운트 오프셋/FOV 오차(트래킹이 모르는 값 — 캘리브레이션 대상).</summary>
    public float TruePanOffset { get; set; } = 0.6f;
    public float TrueTiltOffset { get; set; } = -0.35f;
    public float TrueFovScale { get; set; } = 1.025f;
    public int VideoDelayFrames { get; set; } = 3;
    public float JitterDeg { get; set; } = 0.002f;
    public float GroundTiltDeg { get; private set; } = 0.35f;
    public Vector3 TrueGroundN { get; private set; } = Vector3.UnitY;
    public float TrueGroundD { get; private set; }
    public int FloorPlane { get; private set; } = -1;
    public bool AutoZoom { get; set; } = true;
    public float ZoomFov { get; set; } = 30f;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public long Frame => _frame;
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float AlignErrorPx { get; private set; }
    public float AlignMaxPx { get; private set; }
    public int VisibleLandmarks { get; private set; }
    public int SeriesCount => _seriesCount;
    public float AlignSeries(int i) => _align[Idx(i)];
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    public float CoverageSeries(int i) => _cov[Idx(i)];
    public float FovSeries(int i) => _zoom[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public Vector3 Landmark(int i) => _lm[i];
    public bool LandmarkSeen(int i) => _seen[i];
    public Vector2 LandmarkObserved(int i) => _obs[i];

    private float Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 40) / 16777216f * 2f - 1f; }

    /// <summary>피치 LiDAR 스캔(경사 지면 + 잡음 + 광고판/골대 클러터) → SLAM RANSAC 바닥 평면.</summary>
    public void ScanGround()
    {
        lock (_gate)
        {
            var t = GroundTiltDeg * MathF.PI / 180f;
            TrueGroundN = Vector3.Normalize(new Vector3(-MathF.Sin(t), MathF.Cos(t), 0.004f));
            TrueGroundD = -Vector3.Dot(TrueGroundN, new Vector3(50f, 0.08f, 32f));
            for (var i = 0; i < ScanPoints; i++)
            {
                float x, z, y, nx, ny, nz;
                if (i % 10 == 9)
                {   // 광고판(수직 벽) 클러터
                    x = -3f; z = (Noise() * 0.5f + 0.5f) * 64f; y = 0.2f + (Noise() * 0.5f + 0.5f) * 0.8f; nx = 1; ny = 0; nz = 0;
                }
                else
                {
                    x = (Noise() * 0.5f + 0.5f) * 100f; z = (Noise() * 0.5f + 0.5f) * 64f;
                    y = -(TrueGroundN.X * x + TrueGroundN.Z * z + TrueGroundD) / TrueGroundN.Y + Noise() * 0.008f;
                    nx = TrueGroundN.X + Noise() * 0.02f; ny = TrueGroundN.Y; nz = TrueGroundN.Z + Noise() * 0.02f;
                }
                _sx[i] = x; _sy[i] = y; _sz[i] = z; _snx[i] = nx; _sny[i] = ny; _snz[i] = nz;
            }
            Planes.MinArea = 1f;
            Planes.Detect(_sx, _sy, _sz, _snx, _sny, _snz);
            FloorPlane = Planes.FindKind(PlaneKind.Floor);
            if (FloorPlane >= 0) { ref readonly var p = ref Planes.Plane(FloorPlane); Ar.SetGround(p.Normal, p.D); }
            else Ar.SetGround(Vector3.UnitY, 0f);
            for (var j = 0; j < 9; j++)
                for (var i = 0; i < 11; i++)
                    _lm[j * 11 + i] = Ar.G(i * 10f, j * 8f);
            _lm[GridLandmarks] = Ar.G(0, 28, 2.44f); _lm[GridLandmarks + 1] = Ar.G(0, 36, 2.44f);
            _lm[GridLandmarks + 2] = Ar.G(100, 28, 2.44f); _lm[GridLandmarks + 3] = Ar.G(100, 36, 2.44f);
        }
    }

    // ── 운영자 조작 ───────────────────────────────────────

    public void SetLayers(ArLayer l) { lock (_gate) Ar.Layers = l; LastAction = "🎨 레이어 = " + l; Raise(); }
    public void Toggle(ArLayer l) { lock (_gate) Ar.Layers ^= l; LastAction = ((Ar.Layers & l) != 0 ? "✅ " : "⬜ ") + l; Raise(); }

    public void PresetLowerThird() => SetLayers(ArLayer.LowerThird | ArLayer.ScoreBug | ArLayer.Rings);
    public void PresetTelestrator() => SetLayers(ArLayer.Telestrator | ArLayer.Rings | ArLayer.Distance | ArLayer.Arrow | ArLayer.Offside | ArLayer.Trail | ArLayer.Tags);
    public void PresetPlayerCard() => SetLayers(ArLayer.PlayerCard | ArLayer.Rings | ArLayer.Tags | ArLayer.ScoreBug | ArLayer.CrowdHud);

    public void Select(int p) { lock (_gate) Ar.Selected = p; LastAction = "🎯 선택 " + OnAirArTelestrationSportsTelemetrySynthesizer.Names[p]; Raise(); }
    public void SelectSecond(int p) { lock (_gate) Ar.Second = p; Raise(); }
    public void SetOutput(FillMode m) { lock (_gate) Ar.Output = m; LastAction = "🔀 그래픽 Fill 출력 = " + m + " · 가드 기대 = " + Guard.Expected; Raise(); }
    public void SetExpected(FillMode m) { lock (_gate) Guard.Expected = m; Raise(); }
    public void Take() { lock (_gate) Guard.Take(); LastAction = Guard.KeyerOn ? "▶ DSK AUTO ON" : "⏹ DSK AUTO OFF"; Raise(); }
    public void SetTrackingDelay(int f) { lock (_gate) Tracking.TrackingDelayFrames = Math.Clamp(f, 0, 12); }
    public void SetVideoDelay(int f) { lock (_gate) VideoDelayFrames = Math.Clamp(f, 0, 12); }

    public void SetCaption(string text)
    {
        lock (_gate) { Ar.SetCaption(text); Guard.SetCaption(Ar.Caption); }
        LastAction = "💬 자막 → 로어 서드 + CEA-608/708 ANC";
        Raise();
    }

    public void Stroke(float u, float v, bool newStroke)
    {
        bool ok;
        lock (_gate) ok = Ar.AddStroke(Tracking.Camera, u, v, newStroke);
        if (ok) { LastAction = "✏ 텔레스트레이터 지면 고정 점 " + Ar.StrokePoints; Raise(); }
    }

    public void ClearStrokes() { lock (_gate) Ar.ClearStrokes(); LastAction = "🧽 텔레스트레이터 지움"; Raise(); }

    public void Miscalibrate()
    {
        lock (_gate) { TruePanOffset = Noise() * 1.2f; TrueTiltOffset = Noise() * 0.8f; TrueFovScale = 1f + Noise() * 0.05f; }
        LastAction = "🔧 마운트/렌즈 오차 무작위 주입";
        Raise();
    }

    /// <summary>실측 피드에서 랜드마크(라인 교차 10 m 격자 + 골대 상단) 관측 → 엔진 캘리브레이션.</summary>
    public float Calibrate()
    {
        float rms;
        lock (_gate)
        {
            var f = _frame - VideoDelayFrames;
            if (f < 0) return float.NaN;
            var raw = Encoded(_truth[f % PoseRing]);
            Observe();
            rms = Tracking.Calibrate(raw, _lm, _obs, _seen);
        }
        LastAction = float.IsNaN(rms) ? "📏 캘리브레이션 실패 — 화면 내 랜드마크 부족" :
            "📏 캘리브레이션 · 랜드마크 " + Tracking.CalibrationPoints + " · RMS " + rms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " px";
        Raise();
        return rms;
    }

    /// <summary>실측 자세 → 엔코더 보고값(마운트 오프셋/FOV 오차 제거 전).</summary>
    private FreeDPose Encoded(in FreeDPose truth)
    {
        var e = truth;
        e.Pan -= TruePanOffset; e.Tilt -= TrueTiltOffset;
        return e;
    }

    private void Observe()
    {
        var n = 0;
        for (var i = 0; i < Landmarks; i++)
        {
            _seen[i] = Truth.Project(_lm[i], out var u, out var v) && u >= 2 && v >= 2 && u <= Truth.Width - 3 && v <= Truth.Height - 3;
            _obs[i] = new Vector2(u, v);
            if (_seen[i]) n++;
        }
        VisibleLandmarks = n;
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            var frame = ++_frame;
            Scene.Step(dt);
            for (; _seenEvents < Scene.EventCount; _seenEvents++)
                if (Scene.EventKind(_seenEvents) == ArenaEventKind.Goal) Crowd.Goal(Scene.EventX(_seenEvents) > BroadcastArenaScene.FieldW / 2 ? 0 : 1);
            if (frame % 4 == 0) Crowd.Step(4f / Hz);
            Ar.Track(Scene, dt);
            Operate(dt);
            var truth = new FreeDPose
            {
                CameraId = 1, Pan = _pan, Tilt = _tilt, Roll = 0.15f, X = CameraPos.X, Y = CameraPos.Z, Z = CameraPos.Y,
                Zoom = Tracking.ZoomForFov(_fov), Focus = 20000,
            };
            _truth[frame % PoseRing] = truth;
            var enc = Encoded(truth);
            enc.Pan += Noise() * JitterDeg; enc.Tilt += Noise() * JitterDeg;
            FreeD.Encode(enc, _pkt);
            Tracking.Ingest(_pkt, frame);
            Tracking.Apply(frame);
            // 실측 카메라 = 비디오 지연만큼 과거 자세 + 실제 오프셋/FOV.
            var vf = Math.Max(1, frame - VideoDelayFrames);
            ref readonly var tp = ref _truth[vf % PoseRing];
            Tracking.LensAt(tp.Zoom, tp.Focus, out var hfov, out var k1, out var k2);
            Truth.Set(tp.Pan, tp.Tilt, tp.Roll, new Vector3(tp.X, tp.Z, tp.Y), hfov * TrueFovScale, k1, k2);
            Ar.RenderFeed(Truth, Scene, TrueGroundN, TrueGroundD, Crowd);
            Ar.Render(Tracking.Camera, Scene, Crowd, dt);
            Guard.CheckTitleSafe(Ar);
            Guard.Composite(Ar.Feed, Ar.Fill, Ar.Key, Ar.Output);
            Guard.Pack(frame, Tracking.LastPacket);
            Guard.Receive(Guard.LastAncPacket, frame);
            Align();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _align[_seriesHead] = AlignErrorPx; _ms[_seriesHead] = ms; _cov[_seriesHead] = Ar.KeyCoverage; _zoom[_seriesHead] = Tracking.Camera.HFovDeg;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>카메라 오퍼레이터: 공(+ 진행 방향) 추적 팬/틸트 · 공 거리 기반 줌(약 36 m 화폭).</summary>
    private void Operate(float dt)
    {
        float bx = Scene.X(BroadcastArenaScene.Ball) + Scene.Vx(BroadcastArenaScene.Ball) * 0.4f, bz = Scene.Z(BroadcastArenaScene.Ball) + Scene.Vz(BroadcastArenaScene.Ball) * 0.4f;
        float dx = bx - CameraPos.X, dz = bz - CameraPos.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
        var pan = MathF.Atan2(dx, dz) * 180f / MathF.PI;
        var tilt = MathF.Atan2(Ar.GroundY(bx, bz) + 1f - CameraPos.Y, dist) * 180f / MathF.PI;
        var a = MathF.Min(1f, 2.5f * dt);
        _pan += (pan - _pan) * a; _tilt += (tilt - _tilt) * a;
        var fov = AutoZoom ? Math.Clamp(2f * MathF.Atan(18f / dist) * 180f / MathF.PI, 8f, 55f) : ZoomFov;
        _fov += (fov - _fov) * MathF.Min(1f, 1.2f * dt);
    }

    /// <summary>정합 오차: 화면 내 랜드마크의 실측 피드 위치 vs 트래킹 카메라 투영(px).</summary>
    private void Align()
    {
        Observe();
        double se = 0; float mx = 0; var n = 0;
        var cam = Tracking.Camera;
        for (var i = 0; i < Landmarks; i++)
        {
            if (!_seen[i] || !cam.Project(_lm[i], out var u, out var v)) continue;
            float ex = u - _obs[i].X, ey = v - _obs[i].Y, e2 = ex * ex + ey * ey;
            se += e2; mx = MathF.Max(mx, e2); n++;
        }
        AlignErrorPx = n == 0 ? 0 : (float)Math.Sqrt(se / n);
        AlignMaxPx = MathF.Sqrt(mx);
    }

    /// <summary>UI 모니터 BMP(0 = 피드 PGM · 1 = Fill · 2 = Key · 3 = 합성 출력 · 4 = 가림 매트).</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            int w = Ar.Width, h = Ar.Height, size = _bmp.Length;
            var d = _bmp.AsSpan();
            d.Slice(0, 54).Clear();
            d[0] = (byte)'B'; d[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), size);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -h);
            d[26] = 1; d[28] = 24;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), size - 54);
            ReadOnlySpan<byte> src = which switch { 0 => Ar.Feed, 1 => Guard.FillOut, 2 => Guard.KeyOut, _ => Guard.Output };
            var m = Ar.Matte;
            for (int p = 0, o = 54; p < w * h; p++, o += 3)
            {
                if (which == 4) { var v = m[p] != 0 ? (byte)255 : (byte)0; d[o] = d[o + 1] = d[o + 2] = v; continue; }
                d[o] = src[p * 4 + 2]; d[o + 1] = src[p * 4 + 1]; d[o + 2] = src[p * 4];
            }
            return Convert.ToBase64String(_bmp);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.ArGraphics" };
            _loop.Start();
            LastAction = "▶ AR 그래픽 가동 · " + Hz + " Hz · FreeD → 렌즈 왜곡 → 지면 고정 그래픽 → Key/Fill · ST 2110-40";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ AR 그래픽 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("ArGraphics.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ArGraphics.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
