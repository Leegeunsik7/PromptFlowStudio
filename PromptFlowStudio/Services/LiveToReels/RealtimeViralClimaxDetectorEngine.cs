namespace PromptFlowStudio.Services.LiveToReels;

public enum ClimaxCause : byte { Chat, Physics, Commerce }

/// <summary>v17.69.0 클라이맥스 클립 요청(감지 스레드 → 디스패처 · 값 형식).</summary>
public struct ClimaxClipRequest
{
    public int Id;
    public long OnsetMs, PeakMs, TriggerMs, EndMs, StartMs;
    public float Peak, Chat, Physics, Commerce;
    public ClimaxCause Cause;
    public readonly double Seconds => (EndMs - StartMs) / 1000.0;
}

/// <summary>v17.69.0 클라이맥스 → v17.62 비트스트림 클리퍼 추출 디스패치(무할당 · 예외 격리는 호출자).</summary>
public interface IClimaxClipDispatcher
{
    bool Dispatch(in ClimaxClipRequest request);
}

/// <summary>
/// [PromptFlow Studio v17.69.0] 실시간 복합 바이럴 클라이맥스 감지 엔진(무할당 · 60 Hz).
///   ▸ 입력(임의 스레드 · Interlocked): 채팅 유입(v17.62) · 물리 피격/헤드샷(v17.67) · 구매 수량/품절(v17.68) → 틱 빈(1/60 s) 60 칸 링 = 1 s 슬라이딩 윈도우 합.
///   ▸ 성분 점수: 채팅 = (rate / 기저선 − 1) / (SpikeRatio − 1) · 피격 = (hit + 2·head) / HitSat · 구매 = units / BuySat + 품절 임펄스(3 s 감쇠).
///     바이럴 지수 = 100 · (1 − Π(1 − wᵢ·sᵢ)) — 단일 성분 최대 75 · 복합 스파이크만 임계(85) 도달 · 어택/릴리스 EMA 평활.
///   ▸ FSM: Idle → Armed(≥ Threshold) → 피크 추적 → Release(&lt; ReleaseLevel 또는 꼬리 MaxTailMs) → 구간 = [onset − Lead, now] 15~30 s 클램프 → 디스패치 → Cooldown.
/// </summary>
public sealed class RealtimeViralClimaxDetectorEngine
{
    public const int Hz = 60, Bins = Hz, Hist = 240, HistEvery = Hz / 4, EventCap = 16;
    public const float WChat = 0.75f, WPhysics = 0.6f, WCommerce = 0.6f, SpikeRatio = 4f, HitSat = 6f, BuySat = 8f, ChatFloor = 2f;
    public const long LeadMs = 3_000, MaxTailMs = 6_000, MinClipMs = 15_000, MaxClipMs = 30_000;

    private readonly int[] _chatBin = new int[Bins], _hitBin = new int[Bins], _buyBin = new int[Bins];
    private readonly float[] _idxS = new float[Hist];
    private readonly ClimaxClipRequest[] _ev = new ClimaxClipRequest[EventCap];
    private int _pChat, _pHit, _pHead, _pBuy, _pSoldOut;
    private int _bin, _chatSum, _hitSum, _buySum, _sHead, _sN, _evHead, _evN, _nextId, _histTick;
    private float _baseline = ChatFloor, _soldOutPulse;
    private bool _armed, _aboveOnset, _supLatch;
    private long _onsetMs = -1, _triggerMs, _peakMs, _cooldownUntil;
    private ClimaxClipRequest _cur;

    public IClimaxClipDispatcher? Dispatcher { get; set; }
    public float Threshold { get; set; } = 85f;
    public float OnsetLevel { get; set; } = 45f;
    public float ReleaseLevel { get; set; } = 60f;
    public long CooldownMs { get; set; } = 20_000;
    public float Index { get; private set; }
    public float RawIndex { get; private set; }
    public float ChatScore { get; private set; }
    public float PhysicsScore { get; private set; }
    public float CommerceScore { get; private set; }
    public float ChatPerSec => _chatSum;
    public float HitsPerSec => _hitSum;
    public float UnitsPerSec => _buySum;
    public float Baseline => _baseline;
    public float PeakIndex { get; private set; }
    public bool Armed => _armed;
    public bool CoolingDown(long nowMs) => nowMs < _cooldownUntil;
    public long Ticks { get; private set; }
    public long Triggers { get; private set; }
    public long Dispatched { get; private set; }
    public long Rejected { get; private set; }
    public long Suppressed { get; private set; }
    public double LastStepUs { get; private set; }
    public double MaxStepUs { get; private set; }
    public int SeriesCount => _sN;
    public float IndexSeries(int i) => _idxS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public int EventCount => _evN;
    public ref readonly ClimaxClipRequest Event(int i) => ref _ev[(_evHead - 1 - i + 2 * EventCap) % EventCap];

    public static string CauseName(ClimaxCause c) => c switch { ClimaxCause.Physics => "물리 피격", ClimaxCause.Commerce => "구매 폭주", _ => "채팅 폭발" };

    // ══════════════════════ 입력(임의 스레드 · 무할당) ══════════════════════
    public void AddChat(int n = 1) { if (n > 0) Interlocked.Add(ref _pChat, n); }
    public void AddHit(int n = 1, int head = 0) { if (n > 0) Interlocked.Add(ref _pHit, n); if (head > 0) Interlocked.Add(ref _pHead, head); }
    public void AddPurchase(int units) { if (units > 0) Interlocked.Add(ref _pBuy, units); }
    public void AddSoldOut() => Interlocked.Increment(ref _pSoldOut);

    public void Reset()
    {
        Array.Clear(_chatBin); Array.Clear(_hitBin); Array.Clear(_buyBin); Array.Clear(_idxS);
        _pChat = _pHit = _pHead = _pBuy = _pSoldOut = 0;
        _bin = _chatSum = _hitSum = _buySum = _sHead = _sN = _evN = _histTick = 0;
        _baseline = ChatFloor; _soldOutPulse = 0; _armed = _aboveOnset = false; _onsetMs = -1; _cooldownUntil = 0;
        Index = RawIndex = ChatScore = PhysicsScore = CommerceScore = PeakIndex = 0;
        Ticks = Triggers = Dispatched = Rejected = Suppressed = 0; MaxStepUs = 0;
    }

    // ══════════════════════ 60 Hz 틱(무할당) ══════════════════════
    public void Step(long nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var chat = Interlocked.Exchange(ref _pChat, 0);
        var hit = Interlocked.Exchange(ref _pHit, 0) + 2 * Interlocked.Exchange(ref _pHead, 0);
        var buy = Interlocked.Exchange(ref _pBuy, 0);
        if (Interlocked.Exchange(ref _pSoldOut, 0) > 0) _soldOutPulse = 1f;
        // 1 s 슬라이딩 윈도우(축출 빈 차감 → 신규 빈 가산)
        _chatSum += chat - _chatBin[_bin]; _hitSum += hit - _hitBin[_bin]; _buySum += buy - _buyBin[_bin];
        _chatBin[_bin] = chat; _hitBin[_bin] = hit; _buyBin[_bin] = buy;
        _bin = (_bin + 1) % Bins;
        Ticks++;

        var baseRate = MathF.Max(_baseline, ChatFloor);
        ChatScore = Math.Clamp((_chatSum / baseRate - 1f) / (SpikeRatio - 1f), 0f, 1f);
        PhysicsScore = Math.Clamp(_hitSum / HitSat, 0f, 1f);
        CommerceScore = Math.Clamp(_buySum / BuySat + _soldOutPulse, 0f, 1f);
        _soldOutPulse = MathF.Max(0f, _soldOutPulse - 1f / (3f * Hz));
        RawIndex = 100f * (1f - (1f - WChat * ChatScore) * (1f - WPhysics * PhysicsScore) * (1f - WCommerce * CommerceScore));
        Index += (RawIndex - Index) * (RawIndex > Index ? 0.25f : 0.04f);
        // 기저선(평상시 채팅 속도) — 스파이크 중에는 동결(τ ≈ 20 s)
        if (!_armed && Index < OnsetLevel && Ticks > Hz) _baseline += (_chatSum - _baseline) / (20f * Hz);

        if (Index >= OnsetLevel) { if (!_aboveOnset) { _aboveOnset = true; if (!_armed) _onsetMs = nowMs; } }
        else { _aboveOnset = false; _supLatch = false; }

        if (!_armed)
        {
            if (Index >= Threshold)
            {
                if (nowMs < _cooldownUntil) { if (!_supLatch) { _supLatch = true; Suppressed++; } }
                else
                {
                    _armed = true; Triggers++; _triggerMs = _peakMs = nowMs; PeakIndex = Index;
                    if (_onsetMs < 0 || _onsetMs > nowMs) _onsetMs = nowMs;
                    Capture(nowMs);
                }
            }
        }
        else
        {
            if (Index > PeakIndex) { PeakIndex = Index; _peakMs = nowMs; Capture(nowMs); }
            if (Index < ReleaseLevel || nowMs - _triggerMs >= MaxTailMs) Fire(nowMs);
        }

        if (++_histTick >= HistEvery) { _histTick = 0; _idxS[_sHead] = Index; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++; }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastStepUs = us; if (us > MaxStepUs && Ticks > 3) MaxStepUs = us;
    }

    private void Capture(long nowMs)
    {
        _cur.Peak = PeakIndex; _cur.Chat = ChatScore; _cur.Physics = PhysicsScore; _cur.Commerce = CommerceScore; _cur.PeakMs = nowMs;
        _cur.Cause = WChat * ChatScore >= WPhysics * PhysicsScore && WChat * ChatScore >= WCommerce * CommerceScore ? ClimaxCause.Chat
            : WPhysics * PhysicsScore >= WCommerce * CommerceScore ? ClimaxCause.Physics : ClimaxCause.Commerce;
    }

    /// <summary>클라이맥스 종료 — 최적 15~30 s 구간 산출(빌드업 Lead 포함 · 피크 보존) → 디스패치.</summary>
    private void Fire(long nowMs)
    {
        _armed = false;
        _cooldownUntil = nowMs + CooldownMs;
        long start = _onsetMs - LeadMs, end = nowMs;
        if (end - start < MinClipMs) start = end - MinClipMs;
        if (end - start > MaxClipMs) start = end - MaxClipMs;
        _cur.Id = ++_nextId; _cur.OnsetMs = _onsetMs; _cur.TriggerMs = _triggerMs; _cur.EndMs = end; _cur.StartMs = start;
        _ev[_evHead] = _cur; _evHead = (_evHead + 1) % EventCap; if (_evN < EventCap) _evN++;
        var ok = false;
        try { ok = Dispatcher?.Dispatch(in _cur) ?? false; }
        catch (Exception ex) { PromptFlowStudio.Program.Log("ViralClimax.Dispatch", ex); }
        if (ok) Dispatched++; else Rejected++;
        _onsetMs = -1;
    }
}
