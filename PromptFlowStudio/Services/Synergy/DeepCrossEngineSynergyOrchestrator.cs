using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Icvfx;
using PromptFlowStudio.Services.MediaPipeline;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.Neuro;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Synergy;

/// <summary>시너지 버스 노드(엔진).</summary>
public enum SynergyNode : byte { Bci, Icvfx, Splat3d, World4d, Script }

/// <summary>버스 채널 — 앞쪽은 엔진 샘플 소스, <see cref="ToWorldTimeScale"/> 이후는 싱크.</summary>
public enum SynergySignal : byte
{
    BciEngagement, BciArousal, BciValence, BciFear, BciSynchrony,
    IcvfxEnergy, IcvfxValence, IcvfxKelvin, IcvfxSunAzimuth, IcvfxSunElevation, IcvfxLightPreset,
    WorldTimeScale, GsTrackErrorMm, ScriptTension, ScriptValence,
    ToWorldTimeScale, ToBciIntent, ToIcvfxFlash, ToGsKelvin, ToGsIntensity, ToGsAzimuth, ToGsElevation, ToWorldLight, ToBciSensitivity,
}

/// <summary>라우트 — dst 목표 = Σ(gain·src) + Σbias → 시정수 tau(s) 지수 평활.</summary>
public readonly record struct SynergyRoute(SynergySignal Src, SynergySignal Dst, float Gain, float Bias = 0f, float TauSec = 0f, bool Enabled = true);

/// <summary>라우트 추가 결과.</summary>
public enum RouteAddResult : byte { Added, Full, SelfLoop, UnstableLoop, DstNotSink }

/// <summary>
/// [PromptFlow Studio v16.62.0] 🔗 딥 크로스 엔진 시너지 오케스트레이터 — BCI(v16.56) · ICVFX(v16.59) · 3DGS(v16.58) · 4D 월드(v16.54) · 대본 디렉터(v16.62).
///   ▸ 단일 핫패스 3단: 샘플(엔진 상태 무할당 읽기) → 전파(스냅샷 기반 Jacobi · 라우트 합산 · 지수 평활) → 적용(저비용 싱크 즉시).
///   ▸ 즉시 싱크: 월드 시간 배속 · BCI 디렉터 의도(히스테리시스) · ICVFX 번개 플래시(상승 에지 · 재무장). 고비용 싱크(3DGS 광원 SH 재투영 · 월드 조명 프리셋 · 변조 감도)는
///     <see cref="Commit"/>(≤4Hz · 데드밴드)로 분리 — 핫패스 0B 유지.
///   ▸ 라우트 추가 시 버스 내 폐루프 이득 곱 ≥ 1 거부(발산 차단) · 수동 주입 홀드 · 단계별 μs 지연 + 라우트별 흐름 활성도(UI 시각화).
/// </summary>
public sealed class DeepCrossEngineSynergyOrchestrator : IDisposable
{
    public const int SignalCount = (int)SynergySignal.ToBciSensitivity + 1, MaxRoutes = 48, TickHz = 30, CommitEvery = 8;
    public const float FlashArm = 0.75f, FlashRearm = 0.55f, IntentBand = 0.25f;

    private static readonly SynergyNode[] Owner =
    {
        SynergyNode.Bci, SynergyNode.Bci, SynergyNode.Bci, SynergyNode.Bci, SynergyNode.Bci,
        SynergyNode.Icvfx, SynergyNode.Icvfx, SynergyNode.Icvfx, SynergyNode.Icvfx, SynergyNode.Icvfx, SynergyNode.Icvfx,
        SynergyNode.World4d, SynergyNode.Splat3d, SynergyNode.Script, SynergyNode.Script,
        SynergyNode.World4d, SynergyNode.Bci, SynergyNode.Icvfx, SynergyNode.Splat3d, SynergyNode.Splat3d, SynergyNode.Splat3d, SynergyNode.Splat3d, SynergyNode.World4d, SynergyNode.Bci,
    };
    private static readonly float[] Lo = { 0, 0, 0, 0, 0, 0, 0, 1500, -360, -10, 0, 0.05f, 0, 0, 0, 0.25f, -1, 0, 1800, 0.2f, -360, -10, 0, 0 };
    private static readonly float[] Hi = { 1, 1, 1, 1, 1, 1, 1, 12000, 360, 90, 4, 4, 1000, 1, 1, 2.5f, 1, 1, 12000, 6, 360, 90, 4, 1 };
    private static readonly float[] Init = { 0.5f, 0.5f, 0.5f, 0.2f, 0, 0.5f, 0.5f, 5600, 45, 40, 0, 1, 0, 0.3f, 0.5f, 1, 0, 0, 5600, 2.5f, 45, 40, 0, 0.6f };

    private readonly object _gate = new();
    private readonly CollectiveEmotionHeatmapAggregator? _agg;
    private readonly BioFeedbackGenerativeMediaModifier? _mod;
    private readonly AgenticAutonomousVirtualProductionDirector? _icvfx;
    private readonly AiGenerative4dWorldModelEngine? _world;
    private readonly WorldModelStudioRunner? _worldRunner;
    private readonly NeuralCameraPoseRelightingEngine? _relight;
    private readonly GaussianSplattingStudioRunner? _gsRunner;
    private readonly EnhancedScriptToScreenDirector? _script;
    private readonly SynergyRoute[] _routes = new SynergyRoute[MaxRoutes];
    private readonly float[] _flow = new float[MaxRoutes], _activity = new float[MaxRoutes];
    private readonly long[] _pulses = new long[MaxRoutes];
    private readonly float[] _val = new float[SignalCount], _snap = new float[SignalCount], _target = new float[SignalCount], _tau = new float[SignalCount];
    private readonly float[] _inject = new float[SignalCount], _injectHold = new float[SignalCount];
    private readonly bool[] _driven = new bool[SignalCount];
    private readonly bool[] _visit = new bool[SignalCount];
    private int _routeCount;
    private CancellationTokenSource? _cts;
    private long _ticks, _commits, _flashes, _flashVetoes, _intentChanges, _timeScaleWrites, _lightCommits, _presetCommits;
    private double _sampleUs, _propUs, _applyUs, _tickUs, _maxTickUs;
    private bool _flashArmed = true;
    private float _lastTimeScale = -1f, _lastSensitivity = -1f;
    private LightRig _lastRig;
    private int _lastPreset = -1;
    private bool _rigInit;

    public DeepCrossEngineSynergyOrchestrator(IServiceProvider sp)
        : this(sp.GetService(typeof(CollectiveEmotionHeatmapAggregator)) as CollectiveEmotionHeatmapAggregator,
               sp.GetService(typeof(BioFeedbackGenerativeMediaModifier)) as BioFeedbackGenerativeMediaModifier,
               sp.GetService(typeof(AgenticAutonomousVirtualProductionDirector)) as AgenticAutonomousVirtualProductionDirector,
               sp.GetService(typeof(AiGenerative4dWorldModelEngine)) as AiGenerative4dWorldModelEngine,
               sp.GetService(typeof(NeuralCameraPoseRelightingEngine)) as NeuralCameraPoseRelightingEngine,
               sp.GetService(typeof(EnhancedScriptToScreenDirector)) as EnhancedScriptToScreenDirector,
               sp.GetService(typeof(WorldModelStudioRunner)) as WorldModelStudioRunner,
               sp.GetService(typeof(GaussianSplattingStudioRunner)) as GaussianSplattingStudioRunner) { }

    public DeepCrossEngineSynergyOrchestrator(CollectiveEmotionHeatmapAggregator? agg, BioFeedbackGenerativeMediaModifier? mod, AgenticAutonomousVirtualProductionDirector? icvfx,
        AiGenerative4dWorldModelEngine? world, NeuralCameraPoseRelightingEngine? relight, EnhancedScriptToScreenDirector? script,
        WorldModelStudioRunner? worldRunner = null, GaussianSplattingStudioRunner? gsRunner = null)
    {
        _agg = agg; _mod = mod; _icvfx = icvfx; _world = world; _relight = relight; _script = script; _worldRunner = worldRunner; _gsRunner = gsRunner;
        Init.AsSpan().CopyTo(_val);
        Init.AsSpan().CopyTo(_target);
        LoadDefaultRoutes();
    }

    public event Action? OnChange;

    public bool IsRunning => _cts is not null;
    public long Ticks => Interlocked.Read(ref _ticks);
    public long Commits => Interlocked.Read(ref _commits);
    public long Flashes => Interlocked.Read(ref _flashes);
    public long FlashVetoes => Interlocked.Read(ref _flashVetoes);
    public long IntentChanges => Interlocked.Read(ref _intentChanges);
    public long TimeScaleWrites => Interlocked.Read(ref _timeScaleWrites);
    public long LightCommits => Interlocked.Read(ref _lightCommits);
    public long PresetCommits => Interlocked.Read(ref _presetCommits);
    public double SampleMicros => Volatile.Read(ref _sampleUs);
    public double PropagateMicros => Volatile.Read(ref _propUs);
    public double ApplyMicros => Volatile.Read(ref _applyUs);
    /// <summary>틱 지연 EWMA(μs).</summary>
    public double TickMicros => Volatile.Read(ref _tickUs);
    public double MaxTickMicros => Volatile.Read(ref _maxTickUs);
    public int RouteCount => _routeCount;
    public DirectorIntent CurrentIntent { get; private set; } = DirectorIntent.Balanced;
    public string? LastAction { get; private set; }
    public bool Attached(SynergyNode n) => n switch
    {
        SynergyNode.Bci => _agg is not null || _mod is not null,
        SynergyNode.Icvfx => _icvfx is not null,
        SynergyNode.Splat3d => _relight is not null || _gsRunner is not null,
        SynergyNode.World4d => _world is not null || _worldRunner is not null,
        _ => _script is not null,
    };

    public float this[SynergySignal s] => Volatile.Read(ref _val[(int)s]);
    public static SynergyNode OwnerOf(SynergySignal s) => Owner[(int)s];
    public static bool IsSink(SynergySignal s) => s >= SynergySignal.ToWorldTimeScale;
    /// <summary>채널 범위 정규화(0..1 · 미터 표시).</summary>
    public static float Normalize(SynergySignal s, float v) => Math.Clamp((v - Lo[(int)s]) / (Hi[(int)s] - Lo[(int)s]), 0f, 1f);
    public SynergyRoute RouteAt(int i) => (uint)i < (uint)_routeCount ? _routes[i] : default;
    /// <summary>라우트 최근 기여값(gain·src).</summary>
    public float FlowOf(int i) => (uint)i < (uint)_routeCount ? Volatile.Read(ref _flow[i]) : 0f;
    /// <summary>라우트 활성도(|Δ기여| EWMA · 0..1 정규화) — 흐름 애니메이션.</summary>
    public float ActivityOf(int i) => (uint)i < (uint)_routeCount ? Volatile.Read(ref _activity[i]) : 0f;
    public long PulsesOf(int i) => (uint)i < (uint)_routeCount ? Interlocked.Read(ref _pulses[i]) : 0;

    public static string Label(SynergySignal s) => s switch
    {
        SynergySignal.BciEngagement => "BCI 몰입", SynergySignal.BciArousal => "BCI 흥분", SynergySignal.BciValence => "BCI 쾌적", SynergySignal.BciFear => "BCI 공포", SynergySignal.BciSynchrony => "BCI 동기화",
        SynergySignal.IcvfxEnergy => "ICVFX 에너지", SynergySignal.IcvfxValence => "ICVFX 정서가", SynergySignal.IcvfxKelvin => "ICVFX 색온도", SynergySignal.IcvfxSunAzimuth => "ICVFX 태양 방위",
        SynergySignal.IcvfxSunElevation => "ICVFX 태양 고도", SynergySignal.IcvfxLightPreset => "ICVFX 시간대", SynergySignal.WorldTimeScale => "4D 배속", SynergySignal.GsTrackErrorMm => "3DGS 추적오차",
        SynergySignal.ScriptTension => "대본 긴장", SynergySignal.ScriptValence => "대본 정서",
        SynergySignal.ToWorldTimeScale => "→4D 배속", SynergySignal.ToBciIntent => "→BCI 의도", SynergySignal.ToIcvfxFlash => "→ICVFX 플래시", SynergySignal.ToGsKelvin => "→3DGS 색온도",
        SynergySignal.ToGsIntensity => "→3DGS 광량", SynergySignal.ToGsAzimuth => "→3DGS 방위", SynergySignal.ToGsElevation => "→3DGS 고도", SynergySignal.ToWorldLight => "→4D 조명", _ => "→BCI 감도",
    };

    public static string NodeLabel(SynergyNode n) => n switch
    {
        SynergyNode.Bci => "🧠 BCI", SynergyNode.Icvfx => "🎬 ICVFX", SynergyNode.Splat3d => "🧊 3DGS", SynergyNode.World4d => "🌐 4D 월드", _ => "📜 대본 디렉터",
    };

    // ══════════════════════ 라우팅 ══════════════════════

    /// <summary>기본 시너지 그래프 — 관객 감정 ↔ 연출 ↔ 물리/광원 교차 피드백.</summary>
    public void LoadDefaultRoutes()
    {
        lock (_gate)
        {
            _routeCount = 0;
            Array.Clear(_flow); Array.Clear(_activity); Array.Clear(_pulses);
            AddCore(new SynergyRoute(SynergySignal.BciArousal, SynergySignal.ToWorldTimeScale, 0.6f, 0.7f, 0.6f));          // 흥분 ↑ → 물리 배속 ↑
            AddCore(new SynergyRoute(SynergySignal.ScriptTension, SynergySignal.ToWorldTimeScale, -0.3f, 0.15f, 0.6f));     // 긴장 구간 슬로 모션
            AddCore(new SynergyRoute(SynergySignal.BciFear, SynergySignal.ToIcvfxFlash, 1f, 0f, 0.1f));                     // 공포 피크 → 번개 플래시
            AddCore(new SynergyRoute(SynergySignal.IcvfxKelvin, SynergySignal.ToGsKelvin, 1f, 0f, 1.5f));                   // LED 월 색온도 → 3DGS 리라이트
            AddCore(new SynergyRoute(SynergySignal.IcvfxSunAzimuth, SynergySignal.ToGsAzimuth, 1f, 0f, 1.5f));
            AddCore(new SynergyRoute(SynergySignal.IcvfxSunElevation, SynergySignal.ToGsElevation, 1f, 0f, 1.5f));
            AddCore(new SynergyRoute(SynergySignal.BciEngagement, SynergySignal.ToGsIntensity, 1.5f, 1.6f, 1f));            // 몰입 ↑ → 키 라이트 ↑
            AddCore(new SynergyRoute(SynergySignal.IcvfxEnergy, SynergySignal.ToBciIntent, 1f, -0.5f, 0.8f));               // 비트 에너지 → 스릴 의도
            AddCore(new SynergyRoute(SynergySignal.ScriptTension, SynergySignal.ToBciIntent, 0.8f, -0.25f, 0.8f));
            AddCore(new SynergyRoute(SynergySignal.BciArousal, SynergySignal.ToBciIntent, -0.8f, 0.4f, 0.8f));              // 과흥분 → 진정(음성 피드백)
            AddCore(new SynergyRoute(SynergySignal.IcvfxLightPreset, SynergySignal.ToWorldLight, 1f));                      // 시간대 → 4D 조명 프리셋
            AddCore(new SynergyRoute(SynergySignal.GsTrackErrorMm, SynergySignal.ToBciSensitivity, -0.1f, 0.8f, 1f));      // 추적 오차 ↑ → 셰이크 변조 감도 ↓
        }
        LastAction = "🔗 기본 시너지 라우트 " + _routeCount + "개";
        Raise();
    }

    public RouteAddResult AddRoute(in SynergyRoute r)
    {
        RouteAddResult res;
        lock (_gate) res = AddCore(r);
        LastAction = res == RouteAddResult.Added ? "➕ " + Label(r.Src) + " → " + Label(r.Dst) : "⛔ 라우트 거부(" + res + ")";
        Raise();
        return res;
    }

    private RouteAddResult AddCore(in SynergyRoute r)
    {
        if (_routeCount >= MaxRoutes) return RouteAddResult.Full;
        if (r.Src == r.Dst) return RouteAddResult.SelfLoop;
        if (!IsSink(r.Dst)) return RouteAddResult.DstNotSink;
        Array.Clear(_visit);
        if (MaxLoopGain(r.Dst, r.Src, 1f) * MathF.Abs(r.Gain) >= 1f) return RouteAddResult.UnstableLoop;
        _routes[_routeCount++] = r;
        return RouteAddResult.Added;
    }

    /// <summary>from → to 경로 중 |gain| 곱 최댓값(DFS · 방문 표시) — 0 = 경로 없음.</summary>
    private float MaxLoopGain(SynergySignal from, SynergySignal to, float acc)
    {
        if (from == to) return acc;
        _visit[(int)from] = true;
        var best = 0f;
        for (var i = 0; i < _routeCount; i++)
        {
            var e = _routes[i];
            if (e.Src != from || !e.Enabled || _visit[(int)e.Dst]) continue;
            best = MathF.Max(best, MaxLoopGain(e.Dst, to, acc * MathF.Abs(e.Gain)));
        }
        _visit[(int)from] = false;
        return best;
    }

    public void SetRouteEnabled(int i, bool on)
    {
        lock (_gate) { if ((uint)i < (uint)_routeCount) _routes[i] = _routes[i] with { Enabled = on }; }
        Raise();
    }

    /// <summary>수동 주입(UI 슬라이더 · 테스트) — holdSec 동안 엔진 샘플 대신 사용.</summary>
    public void Inject(SynergySignal s, float value, float holdSec = 1f)
    {
        lock (_gate) { _inject[(int)s] = value; _injectHold[(int)s] = MathF.Max(0.001f, holdSec); }
    }

    public void ClearInjections() { lock (_gate) Array.Clear(_injectHold); }

    // ══════════════════════ 핫패스 ══════════════════════

    /// <summary>1틱(무할당) — 샘플 → 전파 → 즉시 싱크 적용.</summary>
    public void TickOnce(double dtSec)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Synergy.Tick");
            var dt = (float)Math.Clamp(dtSec, 0.0, 0.5);
            var t0 = Stopwatch.GetTimestamp();
            Sample(dt);
            var t1 = Stopwatch.GetTimestamp();
            Propagate(dt);
            var t2 = Stopwatch.GetTimestamp();
            ApplyImmediate();
            var t3 = Stopwatch.GetTimestamp();
            var f = 1e6 / Stopwatch.Frequency;
            Ewma(ref _sampleUs, (t1 - t0) * f);
            Ewma(ref _propUs, (t2 - t1) * f);
            Ewma(ref _applyUs, (t3 - t2) * f);
            var total = (t3 - t0) * f;
            Ewma(ref _tickUs, total);
            if (total > _maxTickUs) Volatile.Write(ref _maxTickUs, total);
            Interlocked.Increment(ref _ticks);
        }
    }

    private static void Ewma(ref double slot, double v) => Volatile.Write(ref slot, slot <= 0 ? v : slot * 0.9 + v * 0.1);

    private void Put(SynergySignal s, float v) => _val[(int)s] = Math.Clamp(v, Lo[(int)s], Hi[(int)s]);

    private void Sample(float dt)
    {
        if (_agg is not null && _agg.ActiveViewers > 0)
        {
            Put(SynergySignal.BciEngagement, _agg.MeanEngagement); Put(SynergySignal.BciArousal, _agg.MeanArousal);
            Put(SynergySignal.BciValence, _agg.MeanValence); Put(SynergySignal.BciFear, _agg.MeanFear); Put(SynergySignal.BciSynchrony, _agg.Synchrony);
        }
        if (_icvfx is not null)
        {
            if (_icvfx.Beat is { } b)
            {
                Put(SynergySignal.IcvfxEnergy, b.Energy); Put(SynergySignal.IcvfxValence, b.Valence);
                Put(SynergySignal.IcvfxLightPreset, (float)PresetFor(b.Time, b.Rain, b.Exterior));
            }
            Put(SynergySignal.IcvfxKelvin, _icvfx.Kelvin); Put(SynergySignal.IcvfxSunAzimuth, _icvfx.SunAzimuth); Put(SynergySignal.IcvfxSunElevation, _icvfx.SunElevation);
        }
        if (_world is not null) Put(SynergySignal.WorldTimeScale, _world.TimeScale);
        if (_gsRunner is not null) Put(SynergySignal.GsTrackErrorMm, (float)_gsRunner.TrackErrorMm);
        if (_script is not null && _script.ShotCount > 0)
        {
            _script.Advance(dt);
            Put(SynergySignal.ScriptTension, _script.CurrentTension); Put(SynergySignal.ScriptValence, _script.CurrentValence);
        }
        for (var s = 0; s < (int)SynergySignal.ToWorldTimeScale; s++)
        {
            if (_injectHold[s] <= 0f) continue;
            _val[s] = Math.Clamp(_inject[s], Lo[s], Hi[s]);
            _injectHold[s] -= dt;
        }
    }

    /// <summary>ICVFX 비트 시간대/날씨 → 4D 조명 프리셋.</summary>
    public static LightPreset PresetFor(TimeOfDay t, bool rain, bool exterior) =>
        rain ? LightPreset.Overcast : !exterior || t == TimeOfDay.Indoor ? LightPreset.Studio
        : t switch { TimeOfDay.Night => LightPreset.Night, TimeOfDay.GoldenHour or TimeOfDay.Dawn => LightPreset.Sunset, TimeOfDay.Day => LightPreset.Noon, _ => LightPreset.Studio };

    private void Propagate(float dt)
    {
        _val.AsSpan().CopyTo(_snap);
        const int first = (int)SynergySignal.ToWorldTimeScale;
        for (var s = first; s < SignalCount; s++) { _target[s] = 0f; _tau[s] = 0f; _driven[s] = false; }
        for (var i = 0; i < _routeCount; i++)
        {
            var r = _routes[i];
            if (!r.Enabled) { _flow[i] = 0f; _activity[i] *= 0.9f; continue; }
            var c = r.Gain * _snap[(int)r.Src];
            var d = (int)r.Dst;
            _target[d] += c + r.Bias;
            _tau[d] = MathF.Max(_tau[d], r.TauSec);
            _driven[d] = true;
            var span = MathF.Max(1e-3f, (Hi[d] - Lo[d]) * 0.05f);
            var delta = MathF.Min(1f, MathF.Abs(c - _flow[i]) / span);
            _activity[i] = _activity[i] * 0.85f + delta * 0.15f + 0.002f;
            if (_activity[i] > 1f) _activity[i] = 1f;
            if (delta > 0.05f) _pulses[i]++;
            _flow[i] = c;
        }
        for (var s = first; s < SignalCount; s++)
        {
            if (!_driven[s]) continue;
            var goal = Math.Clamp(_target[s], Lo[s], Hi[s]);
            var k = _tau[s] <= 0f ? 1f : 1f - MathF.Exp(-dt / _tau[s]);
            _val[s] += (goal - _val[s]) * k;
        }
    }

    private void ApplyImmediate()
    {
        if (_driven[(int)SynergySignal.ToWorldTimeScale] && _world is not null)
        {
            var ts = _val[(int)SynergySignal.ToWorldTimeScale];
            if (MathF.Abs(ts - _lastTimeScale) > 0.01f)
            {
                _world.Apply(new WorldOp(WorldOpKind.TimeScale, -1, ts, 0, 0));
                _lastTimeScale = ts;
                Interlocked.Increment(ref _timeScaleWrites);
            }
        }
        if (_driven[(int)SynergySignal.ToBciIntent])
        {
            var v = _val[(int)SynergySignal.ToBciIntent];
            var next = CurrentIntent switch
            {
                DirectorIntent.Thrill => v < IntentBand * 0.5f ? (v < -IntentBand ? DirectorIntent.Calm : DirectorIntent.Balanced) : DirectorIntent.Thrill,
                DirectorIntent.Calm => v > -IntentBand * 0.5f ? (v > IntentBand ? DirectorIntent.Thrill : DirectorIntent.Balanced) : DirectorIntent.Calm,
                _ => v > IntentBand ? DirectorIntent.Thrill : v < -IntentBand ? DirectorIntent.Calm : DirectorIntent.Balanced,
            };
            if (next != CurrentIntent)
            {
                CurrentIntent = next;
                Interlocked.Increment(ref _intentChanges);
                if (_mod is not null) _mod.Intent = next;
            }
        }
        if (_driven[(int)SynergySignal.ToIcvfxFlash])
        {
            var f = _val[(int)SynergySignal.ToIcvfxFlash];
            if (_flashArmed && f >= FlashArm)
            {
                _flashArmed = false;
                if (_icvfx is null || _icvfx.RequestFlash(Math.Min(1f, f))) Interlocked.Increment(ref _flashes);
                else Interlocked.Increment(ref _flashVetoes);
            }
            else if (!_flashArmed && f <= FlashRearm) _flashArmed = true;
        }
    }

    /// <summary>고비용 싱크 반영(≤4Hz · 데드밴드) — 3DGS 광원(SH 재투영) · 4D 조명 프리셋 · BCI 변조 감도. 핫패스 외부.</summary>
    public int Commit()
    {
        LightRig rig; int preset; float sens;
        bool rigDriven, presetDriven, sensDriven;
        lock (_gate)
        {
            rigDriven = _driven[(int)SynergySignal.ToGsKelvin] || _driven[(int)SynergySignal.ToGsIntensity] || _driven[(int)SynergySignal.ToGsAzimuth] || _driven[(int)SynergySignal.ToGsElevation];
            presetDriven = _driven[(int)SynergySignal.ToWorldLight];
            sensDriven = _driven[(int)SynergySignal.ToBciSensitivity];
            rig = new LightRig(_val[(int)SynergySignal.ToGsAzimuth], _val[(int)SynergySignal.ToGsElevation], _val[(int)SynergySignal.ToGsIntensity], _val[(int)SynergySignal.ToGsKelvin]);
            preset = (int)MathF.Round(_val[(int)SynergySignal.ToWorldLight]);
            sens = _val[(int)SynergySignal.ToBciSensitivity];
        }
        var applied = 0;
        if (rigDriven && (!_rigInit || MathF.Abs(rig.Kelvin - _lastRig.Kelvin) > 150f || MathF.Abs(rig.Intensity - _lastRig.Intensity) > 0.1f
            || MathF.Abs(rig.AzimuthDeg - _lastRig.AzimuthDeg) > 3f || MathF.Abs(rig.ElevationDeg - _lastRig.ElevationDeg) > 3f))
        {
            if (_gsRunner is not null) _gsRunner.SetLight(rig); else _relight?.SetLight(rig);
            _lastRig = rig; _rigInit = true; applied++;
            Interlocked.Increment(ref _lightCommits);
        }
        if (presetDriven && preset != _lastPreset)
        {
            var p = (LightPreset)Math.Clamp(preset, 0, 4);
            if (_worldRunner is not null) _worldRunner.SetLighting(p); else _world?.SetLighting(p);
            _lastPreset = preset; applied++;
            Interlocked.Increment(ref _presetCommits);
        }
        if (sensDriven && _mod is not null && MathF.Abs(sens - _lastSensitivity) > 0.05f)
        {
            _mod.Sensitivity = sens;
            _lastSensitivity = sens; applied++;
        }
        Interlocked.Increment(ref _commits);
        return applied;
    }

    public void ResetTelemetry()
    {
        lock (_gate)
        {
            _ticks = _commits = _flashes = _flashVetoes = _intentChanges = _timeScaleWrites = _lightCommits = _presetCommits = 0;
            _sampleUs = _propUs = _applyUs = _tickUs = _maxTickUs = 0;
            Array.Clear(_pulses);
        }
        Raise();
    }

    // ══════════════════════ 루프 ══════════════════════

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 시너지 버스 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 시너지 버스 정지";
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
                var dt = Math.Min(0.25, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                try
                {
                    TickOnce(dt);
                    if (Ticks % CommitEvery == 0) { Commit(); Raise(); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Synergy.Tick", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Synergy.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
