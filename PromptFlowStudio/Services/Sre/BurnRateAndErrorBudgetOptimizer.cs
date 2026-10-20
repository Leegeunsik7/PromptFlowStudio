namespace PromptFlowStudio.Services.Sre;

public enum BurnSeverity : byte { Ok, Precursor, SlowBurn, FastBurn }

/// <summary>오류 예산 결정 — 심각도 · 저우선 트래픽 수용률 · 예비 용량 비율 · 배포 동결 · 창별 번 레이트 · 잔여 예산.</summary>
public readonly record struct BudgetDecision(BurnSeverity Severity, float AdmissionRatio, float ReserveRatio, bool FreezeDeploys,
    double BurnShort, double BurnMid, double BurnLong, double BudgetRemaining);

/// <summary>
/// [PromptFlow Studio v16.67.0] 🔥 실시간 번 레이트 &amp; 오류 예산 자율 최적화기.
/// 1초 버킷 3600칸 링(정상/오류 요청) + 창별(30s · 300s · 3600s) 증분 합계 — 버킷 회전 시 이탈 버킷만 차감(O(창 수) · 무할당).
/// 번 레이트 = 오류율 / (1 − SLO). 다중 창 판정(Google SRE): 고속(300s·30s ≥ 14.4) · 저속(3600s·300s ≥ 6) · 전조(30s ≥ 1 — 예산 소비가 적립보다 빠름).
/// 결정: 심각도별 저우선 수용률/예비 용량(전조 단계에서 선제 증설 = 사전 차단) · 잔여 예산 &lt; 25% → 배포 동결.
/// </summary>
public sealed class BurnRateAndErrorBudgetOptimizer
{
    public const int Buckets = 3600, ShortWindow = 30, MidWindow = 300, LongWindow = 3600;
    public const double FastBurn = 14.4, SlowBurn = 6, PrecursorBurn = 1;

    private static readonly int[] Windows = { ShortWindow, MidWindow, LongWindow };

    private readonly object _gate = new();
    private readonly double[] _good = new double[Buckets], _bad = new double[Buckets];
    private readonly double[] _sumGood = new double[3], _sumBad = new double[3];
    private int _head;
    private double _clockMs, _bucketStartMs, _slo = 0.99999;
    private BudgetDecision _decision = new(BurnSeverity.Ok, 1f, 0f, false, 0, 0, 0, 1);
    private long _precursors, _slowAlerts, _fastAlerts, _rotations;

    public event Action? OnChange;

    public double Slo
    {
        get { lock (_gate) return _slo; }
        set { lock (_gate) _slo = Math.Clamp(value, 0.9, 0.999999); }
    }

    public BudgetDecision Current { get { lock (_gate) return _decision; } }
    public double ClockMs { get { lock (_gate) return _clockMs; } }
    public long Precursors { get { lock (_gate) return _precursors; } }
    public long SlowAlerts { get { lock (_gate) return _slowAlerts; } }
    public long FastAlerts { get { lock (_gate) return _fastAlerts; } }
    public long Rotations { get { lock (_gate) return _rotations; } }
    public string? LastAction { get; private set; }

    /// <summary>30일 기준 허용 다운타임(초) — 99.999% ≈ 25.9s.</summary>
    public double AllowedDowntimeSecondsPer30d { get { lock (_gate) return (1 - _slo) * 30 * 86400; } }

    /// <summary>요청 결과 기록(현재 버킷 · 무할당).</summary>
    public void Record(double good, double bad)
    {
        if (good < 0 || bad < 0 || double.IsNaN(good) || double.IsNaN(bad)) return;
        lock (_gate)
        {
            _good[_head] += good; _bad[_head] += bad;
            for (var w = 0; w < 3; w++) { _sumGood[w] += good; _sumBad[w] += bad; }
        }
    }

    /// <summary>시계 진행 → 버킷 회전 → 재평가(무할당).</summary>
    public BudgetDecision Tick(double dtMs)
    {
        BurnSeverity before, after;
        lock (_gate)
        {
            _clockMs += Math.Max(0, dtMs);
            if (_clockMs - _bucketStartMs > Buckets * 1000.0) ResetLocked();
            while (_clockMs - _bucketStartMs >= 1000) { Rotate(); _bucketStartMs += 1000; }
            before = _decision.Severity;
            _decision = Evaluate();
            after = _decision.Severity;
            if (after != before && after > before)
            {
                if (after == BurnSeverity.Precursor) _precursors++;
                else if (after == BurnSeverity.SlowBurn) _slowAlerts++;
                else _fastAlerts++;
            }
        }
        if (after != before)
        {
            LastAction = after switch
            {
                BurnSeverity.FastBurn => "🚨 고속 번 — 저우선 셰이핑 40% · 예비 +35%",
                BurnSeverity.SlowBurn => "⚠ 저속 번 — 셰이핑 70% · 예비 +20%",
                BurnSeverity.Precursor => "🔎 전조 감지 — 선제 예비 +10%",
                _ => "✅ 번 레이트 정상",
            };
            Raise();
        }
        return _decision;
    }

    private void Rotate()
    {
        var next = (_head + 1) % Buckets;
        for (var w = 0; w < 3; w++)
        {
            var leave = (next - Windows[w] + Buckets) % Buckets;
            _sumGood[w] -= _good[leave]; _sumBad[w] -= _bad[leave];
            if (_sumGood[w] < 0) _sumGood[w] = 0;
            if (_sumBad[w] < 0) _sumBad[w] = 0;
        }
        _good[next] = 0; _bad[next] = 0;
        _head = next;
        _rotations++;
    }

    private void ResetLocked()
    {
        Array.Clear(_good); Array.Clear(_bad); Array.Clear(_sumGood); Array.Clear(_sumBad);
        _bucketStartMs = Math.Floor(_clockMs / 1000) * 1000;
    }

    private double BurnLocked(int w)
    {
        var total = _sumGood[w] + _sumBad[w];
        return total > 0 ? _sumBad[w] / total / (1 - _slo) : 0;
    }

    /// <summary>창 인덱스(0=30s · 1=300s · 2=3600s) 번 레이트.</summary>
    public double BurnRate(int window) { lock (_gate) return (uint)window < 3u ? BurnLocked(window) : 0; }

    public (double Good, double Bad) WindowTotals(int window) { lock (_gate) return (uint)window < 3u ? (_sumGood[window], _sumBad[window]) : (0, 0); }

    private BudgetDecision Evaluate()
    {
        double s = BurnLocked(0), m = BurnLocked(1), l = BurnLocked(2);
        var sev = m >= FastBurn && s >= FastBurn ? BurnSeverity.FastBurn
            : l >= SlowBurn && m >= SlowBurn ? BurnSeverity.SlowBurn
            : s >= PrecursorBurn ? BurnSeverity.Precursor
            : BurnSeverity.Ok;
        var (adm, res) = sev switch
        {
            BurnSeverity.FastBurn => (0.4f, 0.35f),
            BurnSeverity.SlowBurn => (0.7f, 0.2f),
            BurnSeverity.Precursor => (0.95f, 0.1f),
            _ => (1f, 0f),
        };
        var remaining = Math.Clamp(1 - l, 0, 1);
        return new BudgetDecision(sev, adm, res, remaining < 0.25, s, m, l, remaining);
    }

    public static string SeverityLabel(BurnSeverity s) => s switch
    {
        BurnSeverity.FastBurn => "고속 번",
        BurnSeverity.SlowBurn => "저속 번",
        BurnSeverity.Precursor => "전조",
        _ => "정상",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sre.Budget.OnChange", ex); }
    }
}
