using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.AnatomicalTissue;

/// <summary>
/// [PromptFlow Studio v17.14.0] 💪 AI 해부학 근육 &amp; 연조직 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// v16.75 합성 연기자(v17.11 SMPL 24 관절) + 8 초 시나리오(0–2 s 오른팔 이두 컬 145° · 2–4 s 스쿼트 점프/착지 · 4–6 s 복부 크런치 · 6–8 s 양팔 거상 + 골반 급회전) →
/// 발 접지 보정 FK → 근육 팽창/근막 슬라이딩 → 연조직 관성 지글 → 체적/자가 교차 가드 → 압축 주름 · 법선 맵.
/// </summary>
public sealed class AnatomicalTissueStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, StressHist = 120, J = MocapSkeleton.Body;
    public const float Dt = 1f / Hz, Cycle = 8f, JumpHeight = 0.22f;

    private readonly object _gate = new();
    private readonly Vector3[] _kp = new Vector3[MocapSkeleton.Keypoints], _pos = new Vector3[J], _offsets = new Vector3[J];
    private readonly float[] _face = new float[FaceShapes.Count];
    private readonly Quaternion[] _local = new Quaternion[J], _global = new Quaternion[J];
    private readonly float[] _rootHist = new float[3];
    private readonly float[] _stressHist = new float[StressHist * 16];
    private readonly float[] _msS = new float[SeriesLen], _jigS = new float[SeriesLen], _volBS = new float[SeriesLen], _volAS = new float[SeriesLen], _actS = new float[SeriesLen], _landS = new float[SeriesLen];
    private readonly byte[] _bmp = new byte[54 + SoftTissueInertialDynamicsWrinkleSynthesizer.MapW * SoftTissueInertialDynamicsWrinkleSynthesizer.MapH * 3 + 4096];
    private readonly float _restAnkleY;
    private int _seriesHead, _seriesCount, _stressHead, _stressCount, _rootCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public AnatomicalTissueStudioRunner(IServiceProvider sp)
        : this((NeuralAnatomicalMuscleFasciaEngine?)sp.GetService(typeof(NeuralAnatomicalMuscleFasciaEngine)) ?? new NeuralAnatomicalMuscleFasciaEngine())
    {
    }

    public AnatomicalTissueStudioRunner(NeuralAnatomicalMuscleFasciaEngine engine)
    {
        Engine = engine;
        Tissue = new SoftTissueInertialDynamicsWrinkleSynthesizer(engine);
        Guard = new TissueVolumePreservationSelfIntersectionGuard(engine);
        _restAnkleY = engine.RestJoint(MocapSkeleton.LAnkle).Y;
        LastAction = "📋 대기 · 피부 " + NeuralAnatomicalMuscleFasciaEngine.V + " 정점 · 근육 " + NeuralAnatomicalMuscleFasciaEngine.MuscleCount + " · 사면체 " + TissueVolumePreservationSelfIntersectionGuard.TetCount + " · " + engine.Path;
    }

    public event Action? OnChange;

    public NeuralAnatomicalMuscleFasciaEngine Engine { get; }
    public SoftTissueInertialDynamicsWrinkleSynthesizer Tissue { get; }
    public TissueVolumePreservationSelfIntersectionGuard Guard { get; }
    public bool Motion { get; set; } = true;
    public int MapTube { get; set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public string Phase { get; private set; } = "대기";
    public float LandingAccel { get; private set; }
    public float RootHeight { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float JiggleSeries(int i) => _jigS[Idx(i)];
    public float VolumeBeforeSeries(int i) => _volBS[Idx(i)];
    public float VolumeAfterSeries(int i) => _volAS[Idx(i)];
    public float BicepsSeries(int i) => _actS[Idx(i)];
    public float LandingSeries(int i) => _landS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // ── 제어 평면 ───────────────────────────────────────────

    public void SetFrequency(float hz) { lock (_gate) Tissue.FrequencyHz = Math.Clamp(hz, 0.8f, 10f); LastAction = "〰 연조직 탄성 " + F1(Tissue.FrequencyHz) + " Hz"; Raise(); }
    public void SetDamping(float z) { lock (_gate) Tissue.Damping = Math.Clamp(z, 0.03f, 1.2f); LastAction = "🧽 감쇄비 ζ " + F2(Tissue.Damping); Raise(); }
    public void SetJiggleGain(float g) { lock (_gate) Tissue.JiggleGain = Math.Clamp(g, 0f, 3f); LastAction = "🫧 지글 게인 × " + F2(Tissue.JiggleGain); Raise(); }
    public void SetWrinkleAmplitude(float mm) { lock (_gate) Tissue.WrinkleAmplitude = Math.Clamp(mm, 0f, 6f) * 0.001f; LastAction = "〽 주름 진폭 " + F1(Tissue.WrinkleAmplitude * 1000f) + " mm"; Raise(); }
    public void SetMuscleGain(float g) { lock (_gate) Engine.MuscleGain = Math.Clamp(g, 0f, 2.5f); LastAction = "💪 근육 팽창 게인 × " + F2(Engine.MuscleGain); Raise(); }
    public void SetMinRatio(float a) { lock (_gate) Guard.MinRatio = Math.Clamp(a, 0.3f, 1f); LastAction = "🧊 체적 하한 α " + F2(Guard.MinRatio); Raise(); }
    public void SetVolumeGuard(bool on) { lock (_gate) Guard.VolumeGuard = on; LastAction = on ? "🧊 체적 비압축 가드 ON" : "🧊 체적 가드 OFF — LBS 붕괴 노출"; Raise(); }
    public void SetIntersectionGuard(bool on) { lock (_gate) Guard.IntersectionGuard = on; LastAction = on ? "🔒 자가 교차 락 ON" : "🔓 자가 교차 락 OFF"; Raise(); }
    public void SetJiggle(bool on) { lock (_gate) Tissue.JiggleEnabled = on; LastAction = on ? "🫧 관성 지글 ON" : "🫧 관성 지글 OFF"; Raise(); }
    public void SetWrinkles(bool on) { lock (_gate) Tissue.WrinkleEnabled = on; LastAction = on ? "〽 동적 주름 ON" : "〽 동적 주름 OFF"; Raise(); }
    public void SetSkinning(TissueSkinning m) { lock (_gate) Engine.Skinning = m; LastAction = m == TissueSkinning.Lbs ? "🦴 기저 스키닝 LBS(관절 붕괴 재현)" : "🦴 기저 스키닝 관절 중심 회전 블렌드(DQS 등가)"; Raise(); }
    public void SetFascia(bool on) { lock (_gate) Engine.FasciaSliding = on; LastAction = on ? "🧵 근막 슬라이딩 ON" : "🧵 근막 슬라이딩 OFF"; Raise(); }

    // ── 시나리오 ────────────────────────────────────────────

    private static float Bump(float t, float a, float b) => t <= a || t >= b ? 0f : MathF.Pow(MathF.Sin(MathF.PI * (t - a) / (b - a)), 2f);
    private static float Ramp(float t, float a, float b) { var u = Math.Clamp((t - a) / (b - a), 0f, 1f); return u * u * (3f - 2f * u); }
    private const float D = MathF.PI / 180f;

    private void Pose()
    {
        var t = _time;
        SyntheticPerformer.Pose(t, 1f, _kp, _face, _local, _global, _offsets);
        var c = t % Cycle;
        var root = _kp[0];
        float squat = 0f, air = 0f;
        // ① 이두 컬
        var curl = Bump(c, 0f, 2f);
        if (curl > 0) _local[MocapSkeleton.RElbow] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (35f + 110f * curl) * D);
        // ② 스쿼트 점프
        if (c is >= 2f and < 4f)
        {
            if (c < 2.6f) squat = Ramp(c, 2f, 2.6f);
            else if (c < 2.75f) squat = 1f - Ramp(c, 2.6f, 2.75f);
            else if (c < 3.15f) { var u = (c - 2.75f) / 0.4f; air = 4f * JumpHeight * u * (1f - u); }
            else if (c < 3.3f) squat = 0.75f * Ramp(c, 3.15f, 3.3f);
            else squat = 0.75f * (1f - Ramp(c, 3.3f, 4f));
            for (var s = 0; s < 2; s++)
            {
                int hip = s == 0 ? MocapSkeleton.LHip : MocapSkeleton.RHip, kn = s == 0 ? MocapSkeleton.LKnee : MocapSkeleton.RKnee, an = s == 0 ? MocapSkeleton.LAnkle : MocapSkeleton.RAnkle;
                _local[hip] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -75f * squat * D);
                _local[kn] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 115f * squat * D);
                _local[an] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (-30f * squat + (air > 0 ? -25f : 0f)) * D);
            }
            _local[MocapSkeleton.Spine1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 25f * squat * D);
        }
        // ③ 크런치
        var crunch = Bump(c, 4f, 6f);
        if (crunch > 0)
        {
            _local[MocapSkeleton.Spine1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 22f * crunch * D);
            _local[MocapSkeleton.Spine2] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 24f * crunch * D);
            _local[MocapSkeleton.Spine3] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 15f * crunch * D);
        }
        // ④ 양팔 거상 + 골반 급회전
        var raise = Bump(c, 6f, 8f);
        if (raise > 0)
        {
            _local[MocapSkeleton.LShoulder] = Quaternion.Slerp(_local[MocapSkeleton.LShoulder], Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 75f * D), raise);
            _local[MocapSkeleton.RShoulder] = Quaternion.Slerp(_local[MocapSkeleton.RShoulder], Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -75f * D), raise);
            _local[MocapSkeleton.Pelvis] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.55f * MathF.Sin(MathF.Tau * 1.6f * (c - 6f)) * raise) * _local[MocapSkeleton.Pelvis];
        }
        Phase = c < 2f ? "이두 컬" : c < 4f ? (air > 0 ? "점프 체공" : c < 2.75f ? "스쿼트" : "착지 흡수") : c < 6f ? "복부 크런치" : "양팔 거상 · 급회전";
        MocapSkeleton.ForwardKinematics(_offsets, _local, root, _pos, _global);
        var dy = _restAnkleY - MathF.Min(_pos[MocapSkeleton.LAnkle].Y, _pos[MocapSkeleton.RAnkle].Y) + air;
        root.Y += dy;
        MocapSkeleton.ForwardKinematics(_offsets, _local, root, _pos, _global);
        RootHeight = root.Y;
        _rootHist[0] = _rootHist[1]; _rootHist[1] = _rootHist[2]; _rootHist[2] = root.Y;
        if (_rootCount < 3) _rootCount++;
        LandingAccel = _rootCount >= 3 ? MathF.Max(0f, (_rootHist[2] - 2f * _rootHist[1] + _rootHist[0]) / (Dt * Dt)) : 0f;
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("AnatomicalTissue.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            Pose();
            Engine.Update(_pos, _global, LandingAccel, Dt);
            Tissue.ApplyInertia(Dt);
            Guard.Process();
            Tissue.Wrinkles(MapTube);
            for (var m = 0; m < NeuralAnatomicalMuscleFasciaEngine.MuscleCount; m++) _stressHist[_stressHead * 16 + m] = Engine.Stress(m);
            _stressHead = (_stressHead + 1) % StressHist;
            if (_stressCount < StressHist) _stressCount++;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _jigS[h] = Tissue.MaxJiggleMm; _volBS[h] = Guard.MinVolumeRatioBefore; _volAS[h] = Guard.MinVolumeRatioAfter; _actS[h] = Engine.Activation(1); _landS[h] = LandingAccel;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    // ── 모니터(BMP) ─────────────────────────────────────────

    private static byte B8(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private int Header(int w, int h)
    {
        var row = (w * 3 + 3) & ~3;
        var size = 54 + row * h;
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
        return size;
    }

    private static void Heat(float t, out float r, out float g, out float b)
    {
        t = Math.Clamp(t, 0f, 1f);
        r = Math.Clamp(1.5f - MathF.Abs(4f * t - 3f), 0f, 1f); g = Math.Clamp(1.5f - MathF.Abs(4f * t - 2f), 0f, 1f); b = Math.Clamp(1.5f - MathF.Abs(4f * t - 1f), 0f, 1f);
        if (t < 0.12f) { var k = t / 0.12f; r *= k; g *= k; b *= k; }
    }

    /// <summary>0 = 근육 응력 시간 히트맵(가로 120 틱 × 세로 13 근육) · 1 = 주름 법선 맵(64 × 96 · 탄젠트 공간 RGB).</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            var d = _bmp.AsSpan();
            if (which == 1)
            {
                const int w = SoftTissueInertialDynamicsWrinkleSynthesizer.MapW, h = SoftTissueInertialDynamicsWrinkleSynthesizer.MapH;
                var size = Header(w, h);
                var row = (w * 3 + 3) & ~3;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var o = 54 + y * row + x * 3;
                        d[o] = B8(0.5f + 0.5f * Tissue.MapNormal(x, y, 2)); d[o + 1] = B8(0.5f + 0.5f * Tissue.MapNormal(x, y, 1)); d[o + 2] = B8(0.5f + 0.5f * Tissue.MapNormal(x, y, 0));
                    }
                return Convert.ToBase64String(_bmp, 0, size);
            }
            else
            {
                const int w = StressHist;
                var h = NeuralAnatomicalMuscleFasciaEngine.MuscleCount;
                var size = Header(w, h);
                var row = (w * 3 + 3) & ~3;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var back = w - 1 - x;
                        var v = back < _stressCount ? _stressHist[((_stressHead - 1 - back + StressHist * 2) % StressHist) * 16 + y] : 0f;
                        Heat(v / 1.2f, out var r, out var g, out var b);
                        var o = 54 + y * row + x * 3;
                        d[o] = B8(b); d[o + 1] = B8(g); d[o + 2] = B8(r);
                    }
                return Convert.ToBase64String(_bmp, 0, size);
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.AnatomicalTissue" };
            _loop.Start();
            LastAction = "▶ 해부학 연조직 가동 · 60 Hz · 근육 팽창/근막 슬라이딩 → 관성 지글 → 체적/자가 교차 가드 → 압축 주름";
        }
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("AnatomicalTissue.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("AnatomicalTissue.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
