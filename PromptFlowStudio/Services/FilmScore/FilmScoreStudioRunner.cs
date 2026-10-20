using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.GenerativeVfx;
using PromptFlowStudio.Services.HairCloth;
using PromptFlowStudio.Services.OpenUsd;
using PromptFlowStudio.Services.SpatialAudio;

namespace PromptFlowStudio.Services.FilmScore;

/// <summary>
/// [PromptFlow Studio v16.86.0] 🎼 AI 제너레이티브 영화 음악 &amp; 사운드 디자인 스튜디오 러너(60 Hz = 오디오 블록 800 · <see cref="TickOnce"/> 무할당).
/// 서사 아크(평온 → 긴장 → 액션 → 해소 · 48 s 순환) 또는 수동 큐 → BGM 편곡 · 컷 스팅어 · 대사 가이드 →
/// 캐릭터 보행(발 접지 선형 보간 시각 = 서브틱 정확) · 재질 구간/v16.72 바이옴 · 의상 마찰 · 액션 폭발 · v16.81 VFX 충돌 수집 → 믹싱/마스터링 가드.
/// </summary>
public sealed class FilmScoreStudioRunner : IDisposable
{
    public const int Hz = 60;
    public const float ArcSeconds = 48f;
    private static readonly SurfaceKind[] Path = { SurfaceKind.Grass, SurfaceKind.Gravel, SurfaceKind.Wood, SurfaceKind.Water, SurfaceKind.Stone, SurfaceKind.Snow, SurfaceKind.Sand, SurfaceKind.Mud };

    private readonly object _gate = new();
    private readonly NeuralCinematographicFilmScoreGenerator _score;
    private readonly AutonomousNeuralFoleySfxSynthesizer _foley;
    private readonly HarmonicMixingAndAudioMasteringGuard _guard;
    private readonly GenerativeWorldProceduralEngine? _world;
    private readonly InteractiveVfxCollisionSplashGuard? _vfx;
    private FootContactDetector _lFoot = new(), _rFoot = new();
    private float _lPrevH, _rPrevH;
    private CancellationTokenSource? _cts;
    private long _frame;
    private double _time;
    private float _gait, _distance, _nextCut = 4f, _nextBoom = 1f, _v = 0.5f, _a = 0.2f, _t = 0.1f;
    private ulong _rng = 0x1686_F0EEu;

    public FilmScoreStudioRunner(IServiceProvider sp)
        : this((NeuralCinematographicFilmScoreGenerator?)sp.GetService(typeof(NeuralCinematographicFilmScoreGenerator)) ?? new NeuralCinematographicFilmScoreGenerator(),
               (AutonomousNeuralFoleySfxSynthesizer?)sp.GetService(typeof(AutonomousNeuralFoleySfxSynthesizer)) ?? new AutonomousNeuralFoleySfxSynthesizer(),
               (HarmonicMixingAndAudioMasteringGuard?)sp.GetService(typeof(HarmonicMixingAndAudioMasteringGuard)) ?? new HarmonicMixingAndAudioMasteringGuard(new NeuralBinauralHrtfSpatializer()),
               (GenerativeWorldProceduralEngine?)sp.GetService(typeof(GenerativeWorldProceduralEngine)),
               (InteractiveVfxCollisionSplashGuard?)sp.GetService(typeof(InteractiveVfxCollisionSplashGuard))) { }

    public FilmScoreStudioRunner(NeuralCinematographicFilmScoreGenerator score, AutonomousNeuralFoleySfxSynthesizer foley, HarmonicMixingAndAudioMasteringGuard guard,
        GenerativeWorldProceduralEngine? world = null, InteractiveVfxCollisionSplashGuard? vfx = null)
    {
        _score = score; _foley = foley; _guard = guard; _world = world; _vfx = vfx;
        NewShot();
    }

    public event Action? OnChange;

    public NeuralCinematographicFilmScoreGenerator Score => _score;
    public AutonomousNeuralFoleySfxSynthesizer Foley => _foley;
    public HarmonicMixingAndAudioMasteringGuard Guard => _guard;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public double SceneTime => _time;
    public bool AutoNarrative { get; set; } = true;
    public bool UseWorldBiome { get; set; }
    public bool Running { get; set; } = true;
    public float WalkSpeed { get; private set; } = 1.4f;
    public Vector3 Character { get; private set; }
    public SurfaceKind CurrentSurface { get; private set; }
    public long Footsteps { get; private set; }
    public long Explosions { get; private set; }
    public long Cuts { get; private set; }
    public long VfxIngested { get; private set; }
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int Segment => (int)(_time % ArcSeconds / 12f);

    public static string SegmentName(int s) => s switch { 1 => "긴장", 2 => "액션", 3 => "해소", _ => "평온" };

    private float Next01()
    {
        _rng ^= _rng >> 12; _rng ^= _rng << 25; _rng ^= _rng >> 27;
        return ((_rng * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    public void NewShot()
    {
        lock (_gate)
        {
            _time = 0; _frame = 0; _distance = 0f; _gait = 0f; _nextCut = 4f; _nextBoom = 1f;
            _lFoot = new FootContactDetector(); _rFoot = new FootContactDetector();
            _guard.ResetMeters();
        }
        LastAction = "🎬 새 샷 — 서사 아크 " + (AutoNarrative ? "자동" : "수동");
        Raise();
    }

    /// <summary>수동 감정 큐(서사 자동 해제).</summary>
    public void SetCue(float valence, float arousal, float tension)
    {
        AutoNarrative = false;
        _v = Math.Clamp(valence, -1f, 1f); _a = Math.Clamp(arousal, 0f, 1f); _t = Math.Clamp(tension, 0f, 1f);
        _score.SetCue(_v, _a, _t);
        Raise();
    }

    public void Cut(bool hard) { _score.Cut(hard); Cuts++; LastAction = hard ? "✂ 하드 컷 → 즉시 스팅어" : "✂ 컷 → 다음 박 스팅어"; Raise(); }

    public void Explode(float distance)
    {
        lock (_gate) TriggerBoom(distance);
        LastAction = "💥 폭발 " + distance.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";
        Raise();
    }

    private void TriggerBoom(float distance)
    {
        var a = Next01() * MathF.Tau;
        var p = Character + new Vector3(MathF.Sin(a) * distance, 0f, MathF.Cos(a) * distance);
        _foley.Trigger(new FoleyEvent(_time, FoleyKind.Explosion, SurfaceKind.Stone, 0.9f, p));
        Explosions++;
    }

    /// <summary>프롬프트(한/영) → 감정 큐 · 재질 · 원단 · 규격.</summary>
    public int TriggerPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { LastAction = "⚠ 프롬프트 없음"; Raise(); return 0; }
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        var n = 0;
        if (Has(prompt, "슬픔", "sad") || Has(prompt, "비극", "tragic")) { SetCue(-0.65f, 0.2f, 0.3f); n++; }
        else if (Has(prompt, "긴장", "suspense") || Has(prompt, "공포", "horror")) { SetCue(-0.3f, 0.45f, 0.85f); n++; }
        else if (Has(prompt, "액션", "action") || Has(prompt, "추격", "chase")) { SetCue(-0.4f, 0.9f, 0.7f); n++; }
        else if (Has(prompt, "승리", "triumph") || Has(prompt, "희망", "hope")) { SetCue(0.75f, 0.65f, 0.2f); n++; }
        else if (Has(prompt, "평화", "calm") || Has(prompt, "잔잔", "peaceful")) { SetCue(0.4f, 0.12f, 0.05f); n++; }
        if (Has(prompt, "가죽", "leather")) { _foley.Fabric = FabricKind.Leather; n++; }
        else if (Has(prompt, "실크", "silk")) { _foley.Fabric = FabricKind.Silk; n++; }
        else if (Has(prompt, "데님", "denim")) { _foley.Fabric = FabricKind.Denim; n++; }
        if (Has(prompt, "ATSC", "A/85")) { _guard.Standard = LoudnessStandard.AtscA85; n++; }
        else if (Has(prompt, "EBU", "R128")) { _guard.Standard = LoudnessStandard.EbuR128; n++; }
        if (Has(prompt, "폭발", "explosion")) { lock (_gate) TriggerBoom(30f); n++; }
        LastAction = n > 0 ? "✨ 프롬프트 → 반영 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건" : "⚠ 인식된 키워드 없음";
        Raise();
        return n;
    }

    /// <summary>1틱 = 오디오 1블록(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("FilmScore.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            var blockStart = _time;
            _time += dt;
            Narrative(dt);
            Walk(blockStart, dt);
            if (_vfx is not null) VfxIngested += _foley.IngestVfx(_vfx);
            _score.RenderBlock();
            _foley.RenderBlock(blockStart);
            _guard.Process(_score, _foley);
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void Narrative(float dt)
    {
        var seg = Segment; var local = (float)(_time % 12.0);
        if (AutoNarrative)
        {
            float tv, ta, tt;
            switch (seg)
            {
                case 1: tv = -0.3f; ta = 0.45f; tt = 0.75f; break;
                case 2: tv = -0.5f; ta = 0.9f; tt = 0.8f; break;
                case 3: tv = 0.7f; ta = 0.35f; tt = 0.1f; break;
                default: tv = 0.5f; ta = 0.18f; tt = 0.1f; break;
            }
            var k = 1f - MathF.Exp(-dt / 2f);
            _v += (tv - _v) * k; _a += (ta - _a) * k; _t += (tt - _t) * k;
            _score.SetCue(_v, _a, _t);
            _guard.Dialogue.Active = (seg == 0 && local is > 3f and < 9f) || (seg == 3 && local is > 4f and < 10f);
            if ((float)_time >= _nextCut)
            {
                var hard = _a > 0.75f;
                _score.Cut(hard); Cuts++;
                _nextCut = (float)_time + 6f - 3.5f * _a;
            }
            if (seg == 2 && (float)_time >= _nextBoom) { TriggerBoom(20f + 60f * Next01()); _nextBoom = (float)_time + 2f + 1.5f * Next01(); }
            else if (seg != 2) _nextBoom = (float)_time + 0.5f;
        }
        WalkSpeed = 1.2f + 2.6f * Math.Clamp((_a - 0.3f) / 0.6f, 0f, 1f);
    }

    /// <summary>보행: 보폭 = 0.75 m(걷기) ~ 1.4 m(달리기) · 발 높이 = 진동 반파 · 접지 시각 = 임계 교차 선형 보간.</summary>
    private void Walk(double blockStart, float dt)
    {
        if (!Running) { _foley.SetRustle(0f, _foley.Fabric); return; }
        var stride = 0.75f + 0.65f * Math.Clamp((WalkSpeed - 1.2f) / 2.6f, 0f, 1f);
        _distance += WalkSpeed * dt;
        _gait += WalkSpeed / (2f * stride) * dt;
        Character = new Vector3(_distance, 0f, 0f);
        var seg = (int)(_distance / 6f) % Path.Length;
        CurrentSurface = UseWorldBiome && _world is not null ? AutonomousNeuralFoleySfxSynthesizer.FromBiome(_world.BiomeAt(Character.X, Character.Z)) : Path[seg];
        _foley.Wetness = CurrentSurface is SurfaceKind.Water or SurfaceKind.Mud ? 1f : 0f;
        var lift = 0.08f + 0.06f * Math.Clamp((WalkSpeed - 1.2f) / 2.6f, 0f, 1f);
        var lh = MathF.Max(0f, MathF.Sin(MathF.Tau * _gait)) * lift;
        var rh = MathF.Max(0f, MathF.Sin(MathF.Tau * _gait + MathF.PI)) * lift;
        Step(ref _lFoot, lh, ref _lPrevH, blockStart, dt, -0.12f);
        Step(ref _rFoot, rh, ref _rPrevH, blockStart, dt, 0.12f);
        _foley.SetRustle(WalkSpeed * 0.8f, _foley.Fabric);
        _foley.RustlePosition = Character + Vector3.UnitY;
        _foley.Listener = Character + new Vector3(-3f, 1.6f, -1.5f);
        _guard.ListenerYawDeg = 63f;
    }

    private void Step(ref FootContactDetector det, float h, ref float prevH, double blockStart, float dt, float side)
    {
        if (det.Update(h, dt, out var speed))
        {
            var thr = det.Threshold;
            var frac = prevH > h ? Math.Clamp((prevH - thr) / (prevH - h), 0f, 1f) : 1f;
            var e = Math.Clamp(0.35f + speed * 0.5f, 0f, 1f);
            _foley.Trigger(new FoleyEvent(blockStart + frac * dt, FoleyKind.Footstep, CurrentSurface, e, Character + new Vector3(0f, 0f, side)));
            Footsteps++;
        }
        prevH = h;
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 스코어 · 사운드 디자인 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("FilmScore.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("FilmScore.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
