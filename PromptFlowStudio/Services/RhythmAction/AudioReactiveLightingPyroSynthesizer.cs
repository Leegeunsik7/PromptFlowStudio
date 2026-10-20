using PromptFlowStudio.Services.BeatSync;
namespace PromptFlowStudio.Services.RhythmAction;

public enum ReactiveKind : byte { Pulse, Flare, Pyro }

/// <summary>반응 이벤트(값 형식) — 시각 · 종류 · 레벨(0..1) · 비트 동기 여부.</summary>
public struct ReactiveEvent
{
    public double TimeSec;
    public ReactiveKind Kind;
    public float Level;
    public bool OnBeat;
}

/// <summary>프레임 매개변수(셰이더 유니폼/DMX 공급) — 조도 부스트 · 림 펄스 · 스트릭 플레어 · 이번 블록 점화/플레어 트리거.</summary>
public struct ReactiveFrame
{
    public float DmxBoost, RimPulse, FlareStreak, BassDb, FloorDb;
    public bool PyroIgnite, FlareTrigger;
}

/// <summary>
/// [PromptFlow Studio v17.54.0] 🔊 저음역 펄스 반응형 조명 · 파이로 다이내믹 합성기(<see cref="Process"/> 무할당 · 블록 800 = 60 fps).
/// <b>검출</b>: 60~120 Hz 대역통과(RBJ · f0 = √(60·120) ≈ 84.9 Hz · Q √2 · 2 단 캐스케이드) 블록 에너지 vs 1.5 s 바닥 EWMA — 바닥 × 2 초과 · 직전 블록 대비 15 % 상승 ·
/// 대역 에너지 ≥ 전대역 5 %(중고역 트랜지언트 누설 차단) · 최소 간격 150 ms 면 베이스 히트. 히트 시각은 200 샘플 서브블록 최대(4 ms 해상도), 비트 그리드(최근/다음 비트 ±60 ms)와 대조해 비트 동기 여부를 판정한다.
/// <b>출력</b>: 레벨 = (바닥 대비 dB − 3) / 9 → 조도 부스트(v17.41 DMX 워시 · 릴리스 τ 120 ms) · 림 펄스(비트 동기 ×1 · 비동기 ×0.5 · τ 180 ms) ·
/// 스트릭 플레어(v17.43 · 동기 히트 ≥ 0.25 · τ 250 ms) · 파이로 점화(v17.42 · 드롭 구간 다운비트 동기 히트 또는 드롭 서지 · 쿨다운 1 s).
/// </summary>
public sealed class AudioReactiveLightingPyroSynthesizer
{
    public const int Fs = 48000, Ring = 64, SubBlocks = 4;
    public const float LowHz = 60f, HighHz = 120f, HitRatio = 2f, RiseRatio = 1.15f, MinHitGapS = 0.15f, BandShareMin = 0.05f, BeatWindowS = 0.06f, PyroCooldownS = 1f, FlareThreshold = 0.25f;
    private readonly double _b0, _b2, _a1, _a2;
    private readonly double[] _z = new double[4];
    private readonly ReactiveEvent[] _ev = new ReactiveEvent[Ring];
    private int _evHead, _evN;
    private double _floor, _prevE, _lastHit = double.NegativeInfinity, _lastPyro = double.NegativeInfinity;
    private ReactiveFrame _frame;

    public AudioReactiveLightingPyroSynthesizer()
    {
        var f0 = Math.Sqrt(LowHz * HighHz); var q = f0 / (HighHz - LowHz);
        var w = 2 * Math.PI * f0 / Fs; var alpha = Math.Sin(w) / (2 * q); var a0 = 1 + alpha;
        _b0 = alpha / a0; _b2 = -alpha / a0; _a1 = -2 * Math.Cos(w) / a0; _a2 = (1 - alpha) / a0;
        _frame.BassDb = _frame.FloorDb = -120f;
    }

    /// <summary>조명 감도(0..2).</summary>
    public float LightSensitivity { get; set; } = 1f;
    /// <summary>VFX(플레어 · 파이로) 감도(0..2).</summary>
    public float VfxSensitivity { get; set; } = 1f;
    public ref readonly ReactiveFrame Current => ref _frame;
    public long Hits { get; private set; }
    public long OnBeatHits { get; private set; }
    public long Flares { get; private set; }
    public long PyroIgnitions { get; private set; }
    public float LastHitLevel { get; private set; }
    public double LastProcessUs { get; private set; }
    public int EventCount => _evN;
    /// <summary>i = 0 이 최신.</summary>
    public ref readonly ReactiveEvent Event(int i) => ref _ev[(_evHead - 1 - i + 2 * Ring) % Ring];

    public void Reset()
    {
        Array.Clear(_z); _evHead = _evN = 0; _floor = _prevE = 0; _lastHit = _lastPyro = double.NegativeInfinity;
        _frame = default; _frame.BassDb = _frame.FloorDb = -120f;
        Hits = OnBeatHits = Flares = PyroIgnitions = 0; LastHitLevel = 0;
    }

    /// <summary>대역통과 2 단(무할당 · 상태 보존) — 단일 샘플.</summary>
    private double Band(double x)
    {
        var y = _b0 * x + _z[0]; _z[0] = -_a1 * y + _z[1]; _z[1] = _b2 * x - _a2 * y;
        var y2 = _b0 * y + _z[2]; _z[2] = -_a1 * y2 + _z[3]; _z[3] = _b2 * y - _a2 * y2;
        return y2;
    }

    /// <summary>
    /// 블록 처리(무할당) — mono = BGM 모노 · startSec = 블록 첫 샘플 시각 · tracker = 비트 그리드(null 이면 비동기 반응만) ·
    /// section = 시퀀서 구간 · dropSurge = 이번 프레임 드롭 다운비트 확정.
    /// </summary>
    public void Process(ReadOnlySpan<float> mono, double startSec, RealtimeBeatGridTrackerEngine? tracker, MvSection section, bool dropSurge)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = mono.Length;
        if (n == 0) return;
        var dt = n / (double)Fs;
        var subLen = Math.Max(1, n / SubBlocks);
        double e = 0, full = 0, s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        for (var i = 0; i < n; i++)
        {
            var y = Band(mono[i]); var p = y * y;
            e += p; full += (double)mono[i] * mono[i];
            switch (Math.Min(i / subLen, SubBlocks - 1)) { case 0: s0 += p; break; case 1: s1 += p; break; case 2: s2 += p; break; default: s3 += p; break; }
        }
        e /= n; full /= n;
        var best = 0; var bs = s0;
        if (s1 > bs) { bs = s1; best = 1; }
        if (s2 > bs) { bs = s2; best = 2; }
        if (s3 > bs) best = 3;

        _frame.DmxBoost *= (float)Math.Exp(-dt / 0.12); _frame.RimPulse *= (float)Math.Exp(-dt / 0.18); _frame.FlareStreak *= (float)Math.Exp(-dt / 0.25);
        _frame.PyroIgnite = false; _frame.FlareTrigger = false;
        var hitT = startSec + (best + 0.5) * subLen / (double)Fs;
        var hit = _floor > 0 && e > 1e-9 && e > _floor * HitRatio && e > _prevE * RiseRatio && e >= BandShareMin * full && hitT - _lastHit >= MinHitGapS;
        var vfx = Math.Clamp(VfxSensitivity, 0f, 2f); var light = Math.Clamp(LightSensitivity, 0f, 2f);
        if (hit)
        {
            _lastHit = hitT; Hits++;
            var lv = (float)Math.Clamp((10 * Math.Log10(e / _floor) - 3) / 9, 0, 1);
            LastHitLevel = lv;
            var onBeat = false; var downbeat = false;
            if (tracker is { Locked: true, EventCount: > 0 })
            {
                ref readonly var last = ref tracker.Event(0);
                if (Math.Abs(hitT - last.TimeSec) <= BeatWindowS) { onBeat = true; downbeat = last.Downbeat; }
                else if (!double.IsNaN(tracker.NextBeatSec) && Math.Abs(hitT - tracker.NextBeatSec) <= BeatWindowS) { onBeat = true; downbeat = (last.Index + 1) % 4 == tracker.DownbeatSlot; }
            }
            if (onBeat) OnBeatHits++;
            _frame.DmxBoost = MathF.Min(1f, MathF.Max(_frame.DmxBoost, lv * light));
            _frame.RimPulse = MathF.Min(1f, MathF.Max(_frame.RimPulse, lv * light * (onBeat ? 1f : 0.5f)));
            Push(hitT, ReactiveKind.Pulse, lv, onBeat);
            if (onBeat && lv * vfx >= FlareThreshold)
            {
                _frame.FlareStreak = MathF.Min(1f, MathF.Max(_frame.FlareStreak, lv * vfx)); _frame.FlareTrigger = true; Flares++;
                Push(hitT, ReactiveKind.Flare, lv * vfx, true);
            }
            if (onBeat && downbeat && section == MvSection.Drop && vfx > 0.05f && hitT - _lastPyro >= PyroCooldownS) Ignite(hitT, MathF.Min(1f, MathF.Max(lv, 0.5f) * vfx));
        }
        if (dropSurge && vfx > 0.05f && startSec - _lastPyro >= PyroCooldownS) Ignite(startSec, MathF.Min(1f, vfx));
        // 바닥 갱신(히트 판정 후) · 상승 기준
        _floor = _floor <= 0 ? e : _floor + (e - _floor) * (1 - Math.Exp(-dt / 1.5));
        _prevE = e;
        _frame.BassDb = (float)(10 * Math.Log10(e + 1e-12)); _frame.FloorDb = (float)(10 * Math.Log10(_floor + 1e-12));
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Ignite(double t, float level)
    {
        _lastPyro = t; PyroIgnitions++; _frame.PyroIgnite = true;
        Push(t, ReactiveKind.Pyro, level, true);
    }

    private void Push(double t, ReactiveKind k, float lv, bool onBeat)
    {
        ref var ev = ref _ev[_evHead];
        ev.TimeSec = t; ev.Kind = k; ev.Level = lv; ev.OnBeat = onBeat;
        _evHead = (_evHead + 1) % Ring; if (_evN < Ring) _evN++;
    }

    /// <summary>프레임버퍼/셰이더 유니폼 패킹(무할당) — [조도 · 림 · 플레어 · 점화 · 트리거 · 베이스 dB].</summary>
    public int WriteUniforms(Span<float> dst)
    {
        if (dst.Length < 6) return 0;
        dst[0] = _frame.DmxBoost; dst[1] = _frame.RimPulse; dst[2] = _frame.FlareStreak;
        dst[3] = _frame.PyroIgnite ? 1f : 0f; dst[4] = _frame.FlareTrigger ? 1f : 0f; dst[5] = _frame.BassDb;
        return 6;
    }
}
