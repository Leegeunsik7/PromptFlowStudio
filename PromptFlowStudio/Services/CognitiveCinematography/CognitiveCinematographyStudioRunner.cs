using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.FacialPerformance;
using PromptFlowStudio.Services.HoloTelepresence;

namespace PromptFlowStudio.Services.CognitiveCinematography;

/// <summary>
/// [PromptFlow Studio v17.21.0] 🎥 AI 시네마토그래피 &amp; 카메라 디렉터 스튜디오 러너(60 Hz 전용 스레드 · 결정적).
/// 매 틱 v17.00 AGI 디렉터(감정 · 큐) → 블로킹 동선 · 섀도 맵 → 액터별 v17.12 시선 가드(상대 안구 응시) →
/// 인지 프레이밍(삼분할 · 렌즈 · 스프링) → 가림 가드 · 포커스 풀링 → 레이 마칭 갓레이 · 육각 보케 합성 순으로 처리한다.
/// </summary>
public sealed class CognitiveCinematographyStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240;
    public const float Dt = 1f / Hz;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _marchS = new float[SeriesLen], _bokehS = new float[SeriesLen], _frameS = new float[SeriesLen], _focusS = new float[SeriesLen], _focalS = new float[SeriesLen];
    private readonly byte[] _bmp = new byte[54 + VolumetricRayMarchingCinematicDoFSynthesizer.W * VolumetricRayMarchingCinematicDoFSynthesizer.H * 3];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public CognitiveCinematographyStudioRunner(IServiceProvider sp) : this() { }

    public CognitiveCinematographyStudioRunner()
    {
        Director = new UniversalAgiCinematicDirectorSynthesizer();
        Gaze = new[] { new BiologicalEyeGazeMicroTremorGuard(), new BiologicalEyeGazeMicroTremorGuard() };
        Set = new CinematicBlockingSet();
        Framing = new CognitiveCameraFramingBlockingEngine();
        Synth = new VolumetricRayMarchingCinematicDoFSynthesizer();
        Guard = new AutonomousFocusPullingOcclusionGuard();
        LastAction = "📋 대기 · 액터 " + CinematicBlockingSet.Actors + " · 세트 장애물 " + (Set.PrimCount - Set.ObstacleStart) + " · 디렉터 비트 " + Director.BeatCount + " · " + VolumetricRayMarchingCinematicDoFSynthesizer.W + "×" + VolumetricRayMarchingCinematicDoFSynthesizer.H;
    }

    public event Action? OnChange;

    public UniversalAgiCinematicDirectorSynthesizer Director { get; }
    public BiologicalEyeGazeMicroTremorGuard[] Gaze { get; }
    public CinematicBlockingSet Set { get; }
    public CognitiveCameraFramingBlockingEngine Framing { get; }
    public VolumetricRayMarchingCinematicDoFSynthesizer Synth { get; }
    public AutonomousFocusPullingOcclusionGuard Guard { get; }
    public bool Render { get; set; } = true;
    public bool ThirdsGrid { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float MarchUsSeries(int i) => _marchS[Idx(i)];
    public float BokehUsSeries(int i) => _bokehS[Idx(i)];
    public float FramingErrorSeries(int i) => _frameS[Idx(i)];
    public float FocusErrorSeries(int i) => _focusS[Idx(i)];
    public float FocalSeries(int i) => _focalS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public void SetAutoFocal(bool on) { lock (_gate) Framing.AutoFocal = on; LastAction = on ? "🧠 감정 강도 → 렌즈 자율(24–135 mm)" : "🔧 렌즈 수동 " + F1(Framing.ManualFocalMm) + " mm"; Raise(); }
    public void SetFocal(float mm) { lock (_gate) { Framing.AutoFocal = false; Framing.ManualFocalMm = Math.Clamp(mm, CognitiveCameraFramingBlockingEngine.MinFocal, CognitiveCameraFramingBlockingEngine.MaxFocal); } LastAction = "🔭 초점 거리 " + F1(Framing.ManualFocalMm) + " mm(수동)"; Raise(); }
    public void SetAutoFStop(bool on) { lock (_gate) Guard.AutoFStop = on; LastAction = on ? "🧠 디렉터 큐 조리개 자율" : "🔧 조리개 수동 f/" + F1(Guard.ManualFStop); Raise(); }
    public void SetFStop(float n) { lock (_gate) { Guard.AutoFStop = false; Guard.ManualFStop = Math.Clamp(n, 1.4f, 16f); } LastAction = "⭕ 조리개 f/" + F1(Guard.ManualFStop) + "(수동)"; Raise(); }
    public void SetAutoFocus(bool on) { lock (_gate) Guard.AutoFocus = on; LastAction = on ? "🎯 자율 포커스 풀링(안구 추적) ON" : "🎯 포커스 고정 " + F1(Guard.FocusDistance) + " m"; Raise(); }
    public void SetBokeh(BokehPreset p) { lock (_gate) Synth.SetPreset(p); LastAction = "🔶 보케 " + VolumetricRayMarchingCinematicDoFSynthesizer.PresetName(p); Raise(); }
    public void SetOcclusion(OcclusionMode m) { lock (_gate) Guard.Mode = m; LastAction = m switch { OcclusionMode.Fade => "👻 가림 → 장애물 투명화", OcclusionMode.Slide => "↔ 가림 → 카메라 미끄러짐", OcclusionMode.Auto => "🛡 가림 → 미끄러짐 우선 · 실패 시 투명화", _ => "⛔ 가림 가드 OFF" }; Raise(); }
    public void SetSubject(int s) { lock (_gate) { Framing.AutoSubject = s < 0; if (s >= 0) Framing.Subject = Math.Clamp(s, 0, CinematicBlockingSet.Actors - 1); } LastAction = s < 0 ? "🧠 피사체 자율(디렉터 비트)" : "👤 피사체 고정 · 액터 " + (s + 1); Raise(); }
    public void SetVolumetric(bool on) { lock (_gate) Synth.Volumetric = on; LastAction = on ? "🌫 볼류메트릭 레이 마칭 ON" : "🌫 볼류메트릭 OFF"; Raise(); }
    public void SetDof(bool on) { lock (_gate) Synth.DepthOfFieldOn = on; LastAction = on ? "🔶 육각 보케 피사계 심도 ON" : "🔶 피사계 심도 OFF(팬 포커스)"; Raise(); }
    public void SetFog(float d) { lock (_gate) Synth.FogDensity = Math.Clamp(d, 0f, 0.6f); LastAction = "🌫 안개 밀도 σ " + Synth.FogDensity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); Raise(); }
    public void SetView(CineView v) { lock (_gate) Synth.View = v; LastAction = v switch { CineView.Scatter => "☀ 산란광 버퍼", CineView.Depth => "🗺 Z 깊이", CineView.Coc => "🔥 CoC 히트맵", _ => "🎞 최종 합성" }; Raise(); }
    public void SetBlocking(bool on) { lock (_gate) Set.Blocking = on; LastAction = on ? "🚶 블로킹 동선 ON" : "🚶 동선 정지"; Raise(); }
    public void SetThirds(bool on) { ThirdsGrid = on; LastAction = on ? "▦ 삼분할 그리드 ON" : "▦ 그리드 OFF"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) { var p = on ? CineSimdPath.V256 : CineSimdPath.Scalar; Framing.Path = p; Synth.Path = p; } LastAction = on ? "⚡ Vector256 스프링 · CoC" : "🐢 스칼라 경로(비트 동일 비교)"; Raise(); }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("CognitiveCinematography.Step");
            var freq = System.Diagnostics.Stopwatch.Frequency;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _time += Dt;
            Director.Tick(Dt);
            Set.Advance(Dt);
            // 액터별 v17.12 시선: 상대 안구 응시(머리 기준 요 ±35°) → 생체 가드(홱보기 · 미세 떨림)
            for (var i = 0; i < CinematicBlockingSet.Actors; i++)
            {
                var d = Set.Eye(1 - i) - Set.Eye(i);
                var yaw = (MathF.Atan2(d.X, d.Z) - Set.ActorFacing(i)) * (180f / MathF.PI);
                yaw = (yaw + 540f) % 360f - 180f;
                yaw = Math.Clamp(yaw, -35f, 35f);
                var pitch = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * (180f / MathF.PI);
                var g = Gaze[i];
                g.Process(yaw, pitch, yaw, pitch, 0f, 0f, Dt);
                Set.SetEye(i, 0.5f * (g.Yaw(0) + g.Yaw(1)), 0.5f * (g.Pitch(0) + g.Pitch(1)));
            }
            if (Framing.AutoSubject) Framing.Subject = Director.CurrentBeat & 1;
            var cue = Director.Cue;
            Framing.Update(Set, Dt, Director.Arousal, Director.Tension, cue.DutchDeg, Guard.SlideYawDeg);
            Guard.Process(Set, Framing, Dt, cue.FStop > 0f ? cue.FStop : 2.8f);
            if (Render) Synth.Render(Set, Framing, Guard.FocusDistance, Guard.FStop, Dt);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq);
            var h = _seriesHead;
            _msS[h] = ms; _marchS[h] = (float)Synth.LastMarchUs; _bokehS[h] = (float)Synth.LastBokehUs; _frameS[h] = Framing.FramingError; _focusS[h] = Guard.FocusErrorMm; _focalS[h] = Framing.FocalMm;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>뷰포트 BMP(<see cref="VolumetricRayMarchingCinematicDoFSynthesizer.View"/>).</summary>
    public string RenderBase64()
    {
        lock (_gate)
        {
            Synth.WriteBmp(_bmp);
            return Convert.ToBase64String(_bmp);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.CognitiveCinematography" };
            _loop.Start();
            LastAction = "▶ AI 시네마토그래피 가동 · 60 Hz · 디렉터 → 블로킹 → 삼분할 프레이밍 → 가림 가드/포커스 풀링 → 레이 마칭 → 육각 보케";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("CognitiveCinematography.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("CognitiveCinematography.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
