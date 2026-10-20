using System.Numerics;
using PromptFlowStudio.Services.CognitiveCinematography;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.NeuralVoice;
using PromptFlowStudio.Services.StereoXr;

namespace PromptFlowStudio.Services.SpatialAcoustics;

/// <summary>
/// [PromptFlow Studio v17.23.0] 🎧 AI 6DoF 공간 음향 &amp; 광선 추적 스튜디오 러너(60 Hz 전용 스레드 · 결정적 시뮬레이션 시계).
/// 매 틱 v17.22 HMD 진값 헤드 포즈 → 음원 2개(v17.18 액터 음성 · 궤도 비행 드론 환경음)의 직접음/이미지 소스 경로 ·
/// 세트 차폐/회절 → 확률적 광선 추적(RT60 · 후기 레벨) → 6DoF HRTF 바이노럴 렌더(도플러 · 위상 가드) 순으로 처리한다.
/// </summary>
public sealed class SpatialAcousticsStudioRunner : IDisposable
{
    public const int Hz = 60, Block = Hrtf6DoFBinauralSpatialAudioSynthesizer.Block, Slots = RealtimeAcousticRayTracingEngine.PathSlots, SeriesLen = 240;
    public const float Dt = 1f / Hz, DtMs = 1000f / Hz;
    public const string Line = "이 공간의 울림을 들어 보세요. 기둥 뒤로 걸어가면 소리가 달라집니다.";

    private readonly object _gate = new();
    private readonly AcousticPath[] _paths = new AcousticPath[Hrtf6DoFBinauralSpatialAudioSynthesizer.Sources * Slots];
    private readonly float[] _mono0 = new float[Block], _mono1 = new float[Block];
    private readonly float[] _msS = new float[SeriesLen], _dopS = new float[SeriesLen], _occS = new float[SeriesLen], _itdS = new float[SeriesLen], _rmsS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private double _simMs;
    private double _ph1, _ph2, _ph3;
    private uint _noise = 0x9E3779B9u;
    private float _noiseLp;

    public SpatialAcousticsStudioRunner(IServiceProvider sp) : this() { }

    public SpatialAcousticsStudioRunner()
    {
        Set = new CinematicBlockingSet();
        Tracer = new RealtimeAcousticRayTracingEngine();
        Synth = new Hrtf6DoFBinauralSpatialAudioSynthesizer();
        Tract = new BiomechanicalVocalTractResonanceSynthesizer();
        Voice = new RealtimeNeuralVoiceSynthesisEngine(Tract);
        Voice.SetText(Line);
        LastAction = "📋 대기 · BVH 삼각형 " + Tracer.Bvh.TriangleCount + " / 노드 " + Tracer.Bvh.NodeCount + " · 경로 슬롯 " + Slots + " × 2 · HRTF " + HrtfFilterSet.AzCount + "×" + HrtfFilterSet.ElCount + " · " + HrtfFilterSet.Taps + "탭";
    }

    public event Action? OnChange;

    public CinematicBlockingSet Set { get; }
    public RealtimeAcousticRayTracingEngine Tracer { get; }
    public Hrtf6DoFBinauralSpatialAudioSynthesizer Synth { get; }
    public DopplerDiffractionOcclusionPhaseGuard Guard => Synth.Guard;
    public BiomechanicalVocalTractResonanceSynthesizer Tract { get; }
    public RealtimeNeuralVoiceSynthesisEngine Voice { get; }
    public bool HeadMotion { get; set; } = true;
    public bool Drone { get; set; } = true;
    public bool VoiceOn { get; set; } = true;
    public bool RayTracing { get; set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double SimMs => _simMs;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public Vector3 HeadPosition { get; private set; }
    public Quaternion HeadOrientation { get; private set; } = Quaternion.Identity;
    public Vector3 Source0 { get; private set; }
    public Vector3 Source1 { get; private set; }
    public OcclusionResult Occlusion0 { get; private set; }
    public OcclusionResult Occlusion1 { get; private set; }
    public AcousticPath Path(int i) => _paths[i];
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float DopplerSeries(int i) => _dopS[Idx(i)];
    public float OcclusionSeries(int i) => _occS[Idx(i)];
    public float ItdSeries(int i) => _itdS[Idx(i)];
    public float LevelSeries(int i) => _rmsS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public void SetMaterial(AcousticSurface s, AcousticMaterial m) { lock (_gate) Tracer.SetMaterial(s, m); LastAction = "🧱 " + AcousticMaterials.SurfaceName(s) + " → " + AcousticMaterials.Name(m) + " · Eyring RT60(500 Hz) " + F1(Tracer.EyringRt60(3) * 1000f) + " ms"; Raise(); }
    public void SetRays(int n) { lock (_gate) Tracer.RaysPerFrame = n; LastAction = "🔦 프레임당 광선 " + Tracer.RaysPerFrame + "개 × 최대 " + RealtimeAcousticRayTracingEngine.MaxBounces + " 반사"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 위상 가드 ON(교차 페이드 · 윈도우 스무딩)" : "⛔ 위상 가드 OFF(블록 경계 즉시 점프 — 팝핑 비교)"; Raise(); }
    public void SetHrtf(bool on) { lock (_gate) Synth.HrtfEnabled = on; LastAction = on ? "🎧 HRTF 바이노럴(ITD · ILD · 귓바퀴)" : "🔈 정전력 패닝(비교 · 공간 단서 없음)"; Raise(); }
    public void SetLate(bool on) { lock (_gate) Synth.LateEnabled = on; LastAction = on ? "🌊 후기 잔향 FDN ON" : "🌊 후기 잔향 OFF(초기 반사만)"; Raise(); }
    public void SetDrone(bool on) { Drone = on; LastAction = on ? "🛸 궤도 드론 환경음 ON(도플러)" : "🛸 드론 OFF"; Raise(); }
    public void SetVoice(bool on) { VoiceOn = on; LastAction = on ? "🗣 액터 음성 ON" : "🗣 액터 음성 OFF"; Raise(); }
    public void SetHeadMotion(bool on) { HeadMotion = on; LastAction = on ? "🙆 HMD 헤드 모션 ON" : "🙆 헤드 고정"; Raise(); }
    public void SetBlocking(bool on) { lock (_gate) Set.Blocking = on; LastAction = on ? "🚶 액터 블로킹 동선 ON" : "🚶 액터 정지"; Raise(); }
    public void SetRayTracing(bool on) { RayTracing = on; LastAction = on ? "🔦 확률적 광선 추적 ON" : "🔦 광선 추적 정지(RT60 고정)"; Raise(); }
    public void SetSimd(bool on) { lock (_gate) { Tracer.UseSimd = on; Synth.UseSimd = on; } LastAction = on ? "⚡ SIMD 커널(BVH 슬랩 · 흡음 · 64탭 컨볼루션)" : "🐢 스칼라 쌍둥이(비트 동일)"; Raise(); }

    /// <summary>궤도 비행 드론(타원 · 약 9–10 m/s · 청자 근접 통과 → 도플러).</summary>
    public static Vector3 DronePosition(double tMs)
    {
        var t = (float)(tMs / 1000.0);
        return new Vector3(4f * MathF.Sin(2.2f * t), 2.6f + 0.3f * MathF.Sin(0.7f * t), 0.5f + 4.6f * MathF.Cos(2.2f * t));
    }

    private void DroneAudio(Span<float> dst)
    {
        const double w1 = 2.0 * Math.PI * 118.0 / Hrtf6DoFBinauralSpatialAudioSynthesizer.Fs, w2 = 2.0 * Math.PI * 236.5 / Hrtf6DoFBinauralSpatialAudioSynthesizer.Fs, w3 = 2.0 * Math.PI * 472.0 / Hrtf6DoFBinauralSpatialAudioSynthesizer.Fs;
        for (var i = 0; i < dst.Length; i++)
        {
            var x = _noise; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _noise = x;
            var nz = (x >> 8) * (2f / 16777216f) - 1f;
            _noiseLp += 0.25f * (nz - _noiseLp);
            dst[i] = 0.11f * (float)Math.Sin(_ph1) + 0.07f * (float)Math.Sin(_ph2) + 0.04f * (float)Math.Sin(_ph3) + 0.05f * _noiseLp;
            _ph1 += w1; _ph2 += w2; _ph3 += w3;
        }
        if (_ph1 > 1e6) { _ph1 %= 2.0 * Math.PI; _ph2 %= 2.0 * Math.PI; _ph3 %= 2.0 * Math.PI; }
    }

    /// <summary>1틱(무할당): 헤드 포즈 → 경로 · 차폐/회절 → 광선 추적 → 음원 합성 → 바이노럴 렌더.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("SpatialAcoustics.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = _simMs;
            SpatialComputingXrStudioRunner.HeadTruth(now, HeadMotion, out var hp, out var hq);
            HeadPosition = hp; HeadOrientation = hq;
            var src0 = Set.HeadCenter(0) + 0.1f * CinematicBlockingSet.Forward(Set.ActorFacing(0));
            var src1 = DronePosition(now);
            Source0 = src0; Source1 = src1;
            var paths = _paths.AsSpan();
            Tracer.ComputePaths(src0, hp, paths.Slice(0, Slots));
            DopplerDiffractionOcclusionPhaseGuard.EvaluateDirect(Set, src0, hp, 0, out var o0);
            DopplerDiffractionOcclusionPhaseGuard.Apply(ref _paths[0], o0);
            Occlusion0 = o0;
            if (Drone)
            {
                Tracer.ComputePaths(src1, hp, paths.Slice(Slots, Slots));
                DopplerDiffractionOcclusionPhaseGuard.EvaluateDirect(Set, src1, hp, -1, out var o1);
                DopplerDiffractionOcclusionPhaseGuard.Apply(ref _paths[Slots], o1);
                Occlusion1 = o1;
            }
            else
            {
                for (var i = Slots; i < 2 * Slots; i++) _paths[i].Valid = false;
                Occlusion1 = default;
            }
            if (RayTracing) Tracer.TraceRays(src0, hp);
            if (VoiceOn) for (var i = 0; i < Block; i += RealtimeNeuralVoiceSynthesisEngine.Chunk) Voice.RenderChunk(_mono0.AsSpan(i, RealtimeNeuralVoiceSynthesisEngine.Chunk));
            else Array.Clear(_mono0);
            if (Drone) DroneAudio(_mono1); else Array.Clear(_mono1);
            Synth.Render(_mono0, _mono1, hp, hq, _paths, Tracer.Rt60Low, Tracer.Rt60High, Tracer.LateLevel);
            Set.Advance(Dt);
            _simMs = now + DtMs;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _dopS[h] = Guard.Doppler(Slots); _occS[h] = o0.Occluded ? o0.AttHighDb : 0f; _itdS[h] = Synth.ItdUs;
            _rmsS[h] = 20f * MathF.Log10(MathF.Max(0.5f * (Synth.RmsL + Synth.RmsR), 1e-5f));
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.SpatialAcoustics" };
            _loop.Start();
            LastAction = "▶ 공간 음향 가동 · 60 Hz · HMD 포즈 → 이미지 소스 37×2 · 차폐/회절 → 광선 추적 RT60 → HRTF 컨볼루션 · 도플러 가드";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SpatialAcoustics.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SpatialAcoustics.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
