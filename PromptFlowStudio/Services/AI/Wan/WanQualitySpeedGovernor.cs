using System;
using System.Globalization;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>거버너 관제 스냅샷(값 형식 — HUD 조회 시 힙 할당 0).</summary>
public readonly record struct WanGovernorSnapshot(int Level, int Steps, int ResolutionPct, double EwmaLatencyMs, double DropRate,
    long Samples, long Degrades, long Upgrades, long Preemptions);

/// <summary>
/// [PromptFlow Studio v15.1.0 · WAN-OPT-1] Wan 품질·속도 자율 거버너 — 추론 지연(EWMA)과 백프레셔 드롭률을 원자적으로
///   관측해 생성 스텝(50→20)·내부 해상도(100→60%)를 5단 래더로 가변 조율한다.
///   ▸ 핫패스(<see cref="RecordLatency"/>·<see cref="RecordFrame"/>)는 Interlocked/CAS 만 사용 — 락·할당·문자열 0.
///   ▸ 선제 완화: 단일 샘플이 예산×<see cref="SpikeFactor"/> 이상(GPU 스톨 징후)이면 평가 창을 기다리지 않고 즉시 1단 하향.
///   ▸ 히스테리시스: 하향은 창 1회 판정, 상향은 여유(EWMA ≤ 예산×<see cref="HeadroomRatio"/>·드롭 0) 창이
///     <see cref="CalmWindowsToUpgrade"/> 회 연속일 때만 — 진동(flapping) 방지.
///   ▸ 레벨 전이는 CAS ±1 단위라 동시 평가에서도 단계 건너뛰기·역행이 없다. 평가 자체는 CAS 게이트로 단일 실행.
/// </summary>
public sealed class WanQualitySpeedGovernor
{
    // 레벨 0 = 최고 품질. 해상도는 스텝을 먼저 깎고 나서야 내린다(체감 품질 손실이 더 큼).
    private static readonly int[] StepLadder = { 50, 42, 34, 27, 20 };
    private static readonly int[] ResolutionLadderPct = { 100, 100, 85, 75, 60 };
    public static int MaxLevel => StepLadder.Length - 1;
    public const int EvaluateEvery = 16;            // 평가 창(지연 샘플 수)
    public const double DropRateDegrade = 0.05;     // 창 드롭률 5% 이상 → 하향
    public const double HeadroomRatio = 0.6;
    public const int CalmWindowsToUpgrade = 3;
    public const double SpikeFactor = 2.0;
    public const double DefaultLatencyBudgetMs = 1000.0;
    private const float EwmaAlpha = 0.2f;

    private enum ShiftReason : byte { None, Latency, DropRate, Spike, Headroom }

    private readonly object _gate = new();   // 레벨 전이(드묾) 시 LastAction 원시값 기록 전용 — 핫패스 미사용
    private int _level;
    private int _ewmaBits;                   // float 비트(0 = 샘플 없음)
    private int _dropRateBits;
    private long _budgetBits = BitConverter.DoubleToInt64Bits(DefaultLatencyBudgetMs);
    private int _windowSamples, _windowFrames, _windowDrops;
    private int _calmWindows;
    private int _evaluating;
    private long _samples, _degrades, _upgrades, _preemptions;
    private int _actFrom, _actTo;
    private ShiftReason _actReason;
    private float _actEwma;
    private bool _actionDirty;
    private string? _lastAction;

    public event Action? OnChange;

    public WanQualitySpeedGovernor() { }
    public WanQualitySpeedGovernor(double latencyBudgetMs) => LatencyBudgetMs = latencyBudgetMs;

    /// <summary>추론 1건 지연 예산(ms). 0 이하/NaN 은 무시.</summary>
    public double LatencyBudgetMs
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _budgetBits));
        set { if (value > 0 && !double.IsNaN(value)) Interlocked.Exchange(ref _budgetBits, BitConverter.DoubleToInt64Bits(value)); }
    }

    public int Level => Volatile.Read(ref _level);
    public int CurrentSteps => StepLadder[Volatile.Read(ref _level)];
    public int ResolutionScalePct => ResolutionLadderPct[Volatile.Read(ref _level)];
    public double EwmaLatencyMs => BitConverter.Int32BitsToSingle(Volatile.Read(ref _ewmaBits));
    public long Degrades => Interlocked.Read(ref _degrades);
    public long Upgrades => Interlocked.Read(ref _upgrades);
    public long Preemptions => Interlocked.Read(ref _preemptions);

    public WanGovernorSnapshot Snapshot()
    {
        var lv = Volatile.Read(ref _level);
        return new WanGovernorSnapshot(lv, StepLadder[lv], ResolutionLadderPct[lv], EwmaLatencyMs,
            BitConverter.Int32BitsToSingle(Volatile.Read(ref _dropRateBits)), Interlocked.Read(ref _samples), Degrades, Upgrades, Preemptions);
    }

    public string? LastAction
    {
        get
        {
            lock (_gate)
            {
                if (_actionDirty)
                {
                    var why = _actReason switch
                    {
                        ShiftReason.Spike => "GPU 스톨 스파이크 선제 완화",
                        ShiftReason.Latency => "지연 예산 초과",
                        ShiftReason.DropRate => "백프레셔 드롭률 초과",
                        ShiftReason.Headroom => "여유 회복",
                        _ => "수동",
                    };
                    _lastAction = string.Create(CultureInfo.InvariantCulture,
                        $"L{_actFrom}→L{_actTo} · {StepLadder[_actTo]} steps · 해상도 {ResolutionLadderPct[_actTo]}% · {why} (EWMA {_actEwma:0}ms)");
                    _actionDirty = false;
                }
                return _lastAction;
            }
        }
    }

    /// <summary>현재 해상도 배율을 적용한 (폭,높이) — 종횡비 유지, 16 배수 내림(Wan latent 정렬), 최소 16.</summary>
    public (int Width, int Height) ScaleResolution(int width, int height)
    {
        var pct = ResolutionScalePct;
        if (pct >= 100 || width <= 0 || height <= 0) return (width, height);
        return (Math.Max(16, width * pct / 100 / 16 * 16), Math.Max(16, height * pct / 100 / 16 * 16));
    }

    /// <summary>추론 1건 지연 기록(무할당). 스파이크면 즉시 선제 하향, 창이 차면 평가.</summary>
    public void RecordLatency(double latencyMs)
    {
        if (!(latencyMs >= 0) || double.IsInfinity(latencyMs)) return;
        var sample = (float)latencyMs;
        float ewma;
        while (true)
        {
            var oldBits = Volatile.Read(ref _ewmaBits);
            var old = BitConverter.Int32BitsToSingle(oldBits);
            ewma = oldBits == 0 ? sample : old + EwmaAlpha * (sample - old);
            if (Interlocked.CompareExchange(ref _ewmaBits, BitConverter.SingleToInt32Bits(ewma), oldBits) == oldBits) break;
        }
        Interlocked.Increment(ref _samples);

        if (latencyMs >= LatencyBudgetMs * SpikeFactor && TryShift(+1, ShiftReason.Spike, ewma))
        {
            Interlocked.Increment(ref _preemptions);
            Volatile.Write(ref _calmWindows, 0);
        }
        if (Interlocked.Increment(ref _windowSamples) >= EvaluateEvery) Evaluate();
    }

    /// <summary>송출 프레임 1건 결과 기록(무할당) — dropped = 백프레셔로 소비 전 덮어씀/경합 드롭.</summary>
    public void RecordFrame(bool dropped)
    {
        Interlocked.Increment(ref _windowFrames);
        if (dropped) Interlocked.Increment(ref _windowDrops);
    }

    private void Evaluate()
    {
        if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0) return;
        try
        {
            Interlocked.Exchange(ref _windowSamples, 0);
            var frames = Interlocked.Exchange(ref _windowFrames, 0);
            var drops = Interlocked.Exchange(ref _windowDrops, 0);
            var dropRate = frames > 0 ? (float)drops / frames : 0f;
            Volatile.Write(ref _dropRateBits, BitConverter.SingleToInt32Bits(dropRate));
            var ewma = (float)EwmaLatencyMs;
            var budget = LatencyBudgetMs;

            if (dropRate >= DropRateDegrade || ewma > budget)
            {
                _calmWindows = 0;
                TryShift(+1, dropRate >= DropRateDegrade ? ShiftReason.DropRate : ShiftReason.Latency, ewma);
            }
            else if (ewma <= budget * HeadroomRatio && drops == 0)
            {
                if (++_calmWindows >= CalmWindowsToUpgrade)
                {
                    _calmWindows = 0;
                    TryShift(-1, ShiftReason.Headroom, ewma);
                }
            }
            else _calmWindows = 0;
        }
        finally { Volatile.Write(ref _evaluating, 0); }
    }

    // CAS ±1 전이 — 경계(0/MaxLevel)면 false. 성공 시에만 원시값 기록 + OnChange(구독자 예외 격리).
    private bool TryShift(int delta, ShiftReason reason, float ewma)
    {
        int from, to;
        do
        {
            from = Volatile.Read(ref _level);
            to = from + delta;
            if (to < 0 || to > MaxLevel) return false;
        } while (Interlocked.CompareExchange(ref _level, to, from) != from);

        if (delta > 0) Interlocked.Increment(ref _degrades); else Interlocked.Increment(ref _upgrades);
        lock (_gate) { _actFrom = from; _actTo = to; _actReason = reason; _actEwma = ewma; _actionDirty = true; }
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("WanGovernor.OnChange", ex); }
        return true;
    }

    public void Reset()
    {
        Volatile.Write(ref _level, 0);
        Volatile.Write(ref _ewmaBits, 0);
        Volatile.Write(ref _dropRateBits, 0);
        Interlocked.Exchange(ref _windowSamples, 0);
        Interlocked.Exchange(ref _windowFrames, 0);
        Interlocked.Exchange(ref _windowDrops, 0);
        Volatile.Write(ref _calmWindows, 0);
        Interlocked.Exchange(ref _samples, 0);
        Interlocked.Exchange(ref _degrades, 0);
        Interlocked.Exchange(ref _upgrades, 0);
        Interlocked.Exchange(ref _preemptions, 0);
        lock (_gate) { _actionDirty = false; _lastAction = "거버너 초기화"; }
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("WanGovernor.OnChange", ex); }
    }
}
