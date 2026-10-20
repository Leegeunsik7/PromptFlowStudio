using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.OpticalInertialTracking;
namespace PromptFlowStudio.Services.SetExtension;

public enum SetCameraMove : byte { PanSweep, CraneUp, WhipPan, Static }

/// <summary>
/// [PromptFlow Studio v17.46.0] 🏛️ AI 세트 확장 &amp; 크로마 블렌딩 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 카메라 포즈(v17.45 트래킹 러너 가동 시 융합 포즈 → 내장 무브 4종) → 가드 포즈 링/에지 록 발행 →
/// 볼륨 클리핑 · 분류 · 거리장 · 실제 월드 → 확장 레이어(지연 포즈) → 카메라 플레이트 → 래치 시프트 합성 · 게인 매칭 · 페더 · 디스필 →
/// 스필 P 제어 순으로 처리한다.
/// </summary>
public sealed class VirtualSetExtensionStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300, W = RealtimeVirtualSetExtensionEngine.W, H = RealtimeVirtualSetExtensionEngine.H, Px = W * H;
    public const int BmpBytes = 54 + Px * 3;
    public const float Dt = 1f / Hz;
    private readonly object _gate = new();
    private readonly float[] _ovS = new float[Hist], _seamS = new float[Hist], _tearS = new float[Hist], _spillS = new float[Hist], _msS = new float[Hist];
    private readonly CameraTrackingStudioRunner? _track;
    private SetCameraPose _pose;
    private int _head, _series;
    private double _time;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public VirtualSetExtensionStudioRunner(IServiceProvider sp) : this() => _track = sp.GetService(typeof(CameraTrackingStudioRunner)) as CameraTrackingStudioRunner;
    public VirtualSetExtensionStudioRunner() { }
    public event Action? OnChange;
    public RealtimeVirtualSetExtensionEngine Engine { get; } = new();
    public HybridLedChromaBoundaryBlendSynthesizer Synth { get; } = new();
    public BoundarySeamTearChromaBleedGuard Guard { get; } = new();
    public SetCameraMove Move { get; private set; } = SetCameraMove.PanSweep;
    public bool ShowMask { get; private set; }
    public string PoseSource { get; private set; } = "내장 무브";
    public ref readonly SetCameraPose Pose => ref _pose;
    public bool TrackingLinked => _track is not null;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float OverrunSeries(int i) => _ovS[Ix(i)];
    public float SeamSeries(int i) => _seamS[Ix(i)];
    public float TearSeries(int i) => _tearS[Ix(i)];
    public float SpillSeries(int i) => _spillS[Ix(i)];
    public float TickMsSeries(int i) => _msS[Ix(i)];
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    public static string MoveName(SetCameraMove m) => m switch { SetCameraMove.PanSweep => "팬 스윕", SetCameraMove.CraneUp => "크레인 업", SetCameraMove.WhipPan => "휩 팬", _ => "고정" };

    // ══════════════════════ 제어 ══════════════════════
    public void SetMove(SetCameraMove m) { lock (_gate) Move = m; LastAction = "🎥 카메라 " + MoveName(m); Raise(); }
    public void SetFeather(float px)
    {
        lock (_gate) Synth.FeatherPx = Math.Clamp(px, 0f, HybridLedChromaBoundaryBlendSynthesizer.MaxFeather);
        Raise();
    }
    public void SetDespill(float s) { lock (_gate) Synth.DespillStrength = Math.Clamp(s, 0f, 1f); Raise(); }
    public void SetColorMatch(bool on) { lock (_gate) Synth.ColorMatch = on; LastAction = on ? "🎨 LED↔확장 컬러 매칭 ON" : "🎨 컬러 매칭 OFF"; Raise(); }
    public void SetEdgeLock(bool on) { lock (_gate) Guard.EdgeLock = on; LastAction = on ? "🔗 에지 록 ON" : "🔗 에지 록 OFF(지연 포즈)"; Raise(); }
    public void SetAutoDespill(bool on) { lock (_gate) Guard.AutoDespill = on; LastAction = on ? "🟩 자동 디스필 ON" : "🟩 자동 디스필 OFF"; Raise(); }
    public void SetMask(bool on) { lock (_gate) ShowMask = on; LastAction = on ? "◩ 블렌드 마스크 오버레이" : "◩ 합성 화면"; Raise(); }
    public void SetStyle(int i)
    {
        lock (_gate) Engine.StyleIndex = i;
        LastAction = "🌄 v17.27 월드 " + Engine.StyleName;
        Raise();
    }
    public void SetLatency(int frames) { lock (_gate) Guard.ExtLatencyFrames = Math.Clamp(frames, 0, 6); Raise(); }
    public void ResetMetrics()
    {
        lock (_gate) { Guard.ResetMetrics(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    /// <summary>UI 뷰어 — 24-bit BMP(하단부터 BGR) · 마스크 모드: 적 = LED · 녹 = 크로마 키 · 청 = 확장 블렌드 가중치.</summary>
    public void WriteBmp(Span<byte> dst)
    {
        dst[..54].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[2..], BmpBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[10..], 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[18..], W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[22..], H);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[26..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[28..], 24);
        lock (_gate)
        {
            var r = Synth.OutR; var g = Synth.OutG; var b = Synth.OutB; var reg = Engine.Regions; var wt = Synth.BlendWeight; var al = Synth.Alpha;
            for (var y = 0; y < H; y++)
            {
                var d = 54 + y * W * 3;
                var s = (H - 1 - y) * W;
                for (var x = 0; x < W; x++)
                {
                    var i = s + x;
                    float cr, cg, cb;
                    if (ShowMask)
                    {
                        var led = reg[i] == (byte)SetRegion.Led;
                        cr = led ? 0.55f : 0.08f; cg = led ? 0.1f : 0.45f * al[i]; cb = wt[i];
                    }
                    else { cr = r[i]; cg = g[i]; cb = b[i]; }
                    dst[d + x * 3] = To8(cb); dst[d + x * 3 + 1] = To8(cg); dst[d + x * 3 + 2] = To8(cr);
                }
            }
        }
    }

    private static byte To8(float v) => (byte)(Math.Clamp(MathF.Sqrt(Math.Max(v, 0f)), 0f, 1f) * 255f + 0.5f);

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("SetExtension.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _time += Dt;
            Track();
            Guard.Push(_time, in _pose);
            Guard.Resolve(in _pose, out var extPose);
            Engine.Compute(in _pose);
            Engine.RenderExtension(in extPose);
            Synth.BuildPlate(Engine, (float)_time);
            Synth.Compose(Engine, in extPose, Guard.EdgeLock, Guard.FeatherScale);
            Synth.DespillStrength = Guard.UpdateSpill(Synth.Spill, Synth.DespillStrength);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _ovS[_head] = Engine.OverrunPct; _seamS[_head] = (float)Synth.SeamStep; _tearS[_head] = Guard.RawTearPx; _spillS[_head] = (float)Synth.Spill; _msS[_head] = ms;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    private void Track()
    {
        if (_track is not null && _track.IsRunning)
        {
            var p = _track.OutPos; var e = _track.OutEuler;
            _pose.Position = p; _pose.Pan = e.X; _pose.Tilt = e.Y; _pose.Roll = e.Z;
            PoseSource = "v17.45 트래킹";
            return;
        }
        PoseSource = "내장 무브";
        var t = (float)_time;
        switch (Move)
        {
            case SetCameraMove.PanSweep:
                _pose.Position = new Vector3(0.6f * MathF.Sin(0.2f * t), 1.6f, 2f);
                _pose.Pan = 1.1f * MathF.Sin(0.35f * t); _pose.Tilt = 0.08f + 0.12f * MathF.Sin(0.5f * t); _pose.Roll = 0f;
                break;
            case SetCameraMove.CraneUp:
                var k = 0.5f + 0.5f * MathF.Sin(0.3f * t);
                _pose.Position = new Vector3(0f, 1.2f + 3.2f * k, 2.5f);
                _pose.Pan = 0.3f * MathF.Sin(0.15f * t); _pose.Tilt = -0.2f + 0.75f * k; _pose.Roll = 0f;
                break;
            case SetCameraMove.WhipPan:
                _pose.Position = new Vector3(0f, 1.7f, 1.5f);
                _pose.Pan = 1.2f * MathF.Sin(MathF.PI * t / 1.0f); _pose.Tilt = 0.12f + 0.06f * MathF.Sin(2.3f * t); _pose.Roll = 0.01f * MathF.Sin(3f * t);
                break;
            default:
                _pose.Position = new Vector3(0f, 1.6f, 2f); _pose.Pan = 0f; _pose.Tilt = 0.1f; _pose.Roll = 0f;
                break;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SetExtension" };
            _loop.Start();
        }
        LastAction = "▶ 세트 확장 60 Hz · " + MoveName(Move);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SetExtension.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SetExtension.Runner.OnChange", ex); }
    }
    public void Dispose() => Stop();
}
