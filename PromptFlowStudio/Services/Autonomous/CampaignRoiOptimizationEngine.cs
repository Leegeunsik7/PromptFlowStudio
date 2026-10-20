using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using PromptFlowStudio.Services.Enterprise;
using PromptFlowStudio.Services.Interactive;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.Services.Autonomous;

/// <summary>v16.28.0 테마 단위 효율(비용 × 성과 결합).</summary>
public sealed record ThemeEfficiency(string Theme, int Videos, double Usd, long Views, long Conversions, double Revenue, double MeanReward)
{
    /// <summary>1,000뷰당 생산 비용(뷰 0 이면 +∞ 대신 0 · <see cref="HasViews"/> 확인).</summary>
    public double Ecpm => Views > 0 ? Usd / Views * 1000 : 0;
    /// <summary>전환 1건당 비용.</summary>
    public double Ecpa => Conversions > 0 ? Usd / Conversions : 0;
    public double Roas => Usd > 0 ? Revenue / Usd : 0;
    public bool HasViews => Views > 0;
}

/// <summary>v16.28.0 재배분 결과 — 테마별 비중 · 일 예산 · 렌더 팜 슬롯.</summary>
public sealed record ThemeAllocation(string Theme, double Weight, double DailyUsd, int RenderSlots, double ExpectedEcpm, string Reason);

/// <summary>v16.28.0 교차 플랫폼 터치포인트(기여도 분석 입력).</summary>
public readonly record struct Touchpoint(string Platform, DateTimeOffset At);

/// <summary>
/// [PromptFlowStudio] v16.28.0 💹 교차 플랫폼 성과 기여도 &amp; ROI 자동 최적화기 — eCPM/eCPA 최소화 예산 비중 · 렌더 팜 자원 재배분.
///   ▸ 원천: v16.18.0 <see cref="ProductionCostTrackerService"/> 생산 원장(테마별 USD) + <see cref="PerformanceFeedbackLoopEngine"/> 성과(조회/보상)
///     + v16.26.0 <see cref="InteractiveConversionAnalytics"/> 전환/매출(총합을 조회 비중으로 테마 배분).
///   ▸ 효율 점수: 베이지안 수축 비용효율 = (뷰 + k·전체평균뷰/$·$) / ($ + k$) → 1000뷰당 비용 역수 · 보상 가중(0.7 + 0.6·보상) · ROAS 보너스.
///   ▸ 배분(<see cref="Allocate"/> · 무할당 Span): softmax(온도 τ) → 탐색 하한(최소 비중) → 직전 대비 변화 상한(관성) → 정규화.
///   ▸ 정수 배분(<see cref="Apportion"/>): 렌더 팜 슬롯 최대 잉여(Largest Remainder · 무할당).
///   ▸ 기여도(<see cref="AttributeTimeDecay"/>): 전환 1건을 터치포인트에 시간 감쇠(반감기) 가중으로 분할(플랫폼별 합산).
/// </summary>
public sealed class CampaignRoiOptimizationEngine
{
    public const int MaxThemes = 32;
    public const double DefaultMinShare = 0.05;
    public const double DefaultMaxShift = 0.20;
    public const double DefaultTemperature = 0.35;

    private readonly ProductionCostTrackerService? _cost;
    private readonly PerformanceFeedbackLoopEngine _feedback;
    private readonly InteractiveConversionAnalytics? _interactive;
    private readonly CloudRenderFarmDispatcherService? _farm;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _lastWeights = new(StringComparer.Ordinal);
    private List<ThemeAllocation> _plan = new();
    private int _rebalances;

    public CampaignRoiOptimizationEngine(PerformanceFeedbackLoopEngine feedback, ProductionCostTrackerService? cost = null,
        InteractiveConversionAnalytics? interactive = null, CloudRenderFarmDispatcherService? farm = null)
    {
        _feedback = feedback;
        _cost = cost;
        _interactive = interactive;
        _farm = farm;
    }

    public event Action? OnChange;

    public double MinShare { get; set; } = DefaultMinShare;
    public double MaxShift { get; set; } = DefaultMaxShift;
    public double Temperature { get; set; } = DefaultTemperature;
    /// <summary>예산 상한이 없을 때의 일 예산 기본값(USD).</summary>
    public double FallbackDailyUsd { get; set; } = 20;
    public int Rebalances => Volatile.Read(ref _rebalances);
    public string? LastAction { get; private set; }
    public IReadOnlyList<ThemeAllocation> Plan { get { lock (_gate) return _plan.ToArray(); } }

    /// <summary>테마 비중(없으면 0) — 디렉터 스케줄 배분용.</summary>
    public double WeightOf(string theme)
    {
        lock (_gate) return _lastWeights.TryGetValue(theme, out var w) ? w : 0;
    }

    // ══════════════════════ 순수 헬퍼(무할당 · 테스트 대상) ══════════════════════

    /// <summary>효율 점수(높을수록 좋음) — 수축된 1000뷰당 비용 역수 × 보상 가중 × ROAS 보너스.</summary>
    public static double EfficiencyScore(double usd, long views, double meanReward, double roas, double priorViewsPerUsd, double priorUsd = 1.0)
    {
        var shrunkViewsPerUsd = (views + priorViewsPerUsd * priorUsd) / (Math.Max(0, usd) + priorUsd);
        var rewardW = 0.7 + 0.6 * Math.Clamp(meanReward, 0, 1.5);
        var roasW = 1 + Math.Min(1, Math.Max(0, roas) * 0.1);
        return shrunkViewsPerUsd * rewardW * roasW;
    }

    /// <summary>
    /// 점수 → 비중(무할당) — log 점수 softmax(τ) → 하한 보장 → 직전 비중 대비 ±maxShift 클램프(직전 &lt;0 이면 미적용) → 합 1 정규화.
    /// </summary>
    public static void Allocate(ReadOnlySpan<double> scores, ReadOnlySpan<double> previous, double temperature, double minShare, double maxShift, Span<double> weights)
    {
        var n = scores.Length;
        if (n == 0) return;
        if (n == 1) { weights[0] = 1; return; }
        temperature = Math.Max(0.05, temperature);
        minShare = Math.Clamp(minShare, 0, 1.0 / n);
        var max = double.MinValue;
        for (var i = 0; i < n; i++) max = Math.Max(max, Math.Log(Math.Max(1e-9, scores[i])));
        double sum = 0;
        for (var i = 0; i < n; i++) { weights[i] = Math.Exp((Math.Log(Math.Max(1e-9, scores[i])) - max) / temperature); sum += weights[i]; }
        for (var i = 0; i < n; i++) weights[i] = weights[i] / sum;
        Floor(weights, minShare);
        if (previous.Length == n && maxShift > 0)
        {
            // 관성 박스 [prev-shift, prev+shift] ∩ [floor, 1] 안에서 합 1 투영(잔여를 여유 있는 항목에 비례 분배 · 반복 수렴).
            for (var it = 0; it < 16; it++)
            {
                double s = 0;
                for (var i = 0; i < n; i++)
                {
                    Bounds(previous[i], minShare, maxShift, out var lo, out var hi);
                    weights[i] = Math.Clamp(weights[i], lo, hi);
                    s += weights[i];
                }
                var resid = 1 - s;
                if (Math.Abs(resid) < 1e-12) break;
                double slack = 0;
                for (var i = 0; i < n; i++)
                {
                    Bounds(previous[i], minShare, maxShift, out var lo, out var hi);
                    slack += resid > 0 ? hi - weights[i] : weights[i] - lo;
                }
                if (slack <= 1e-12) { Normalize(weights); break; }   // 박스 제약이 불가능하면 정규화 우선
                for (var i = 0; i < n; i++)
                {
                    Bounds(previous[i], minShare, maxShift, out var lo, out var hi);
                    var room = resid > 0 ? hi - weights[i] : weights[i] - lo;
                    weights[i] += resid * room / slack;
                }
            }
        }

        static void Bounds(double prev, double floor, double shift, out double lo, out double hi)
        {
            if (prev < 0) { lo = floor; hi = 1; return; }
            lo = Math.Max(floor, prev - shift);
            hi = Math.Min(1, prev + shift);
            if (hi < lo) hi = lo;
        }

        static void Floor(Span<double> w, double floor)
        {
            if (floor <= 0) return;
            double deficit = 0, surplus = 0;
            for (var i = 0; i < w.Length; i++) { if (w[i] < floor) deficit += floor - w[i]; else surplus += w[i] - floor; }
            if (deficit <= 0 || surplus <= 0) return;
            for (var i = 0; i < w.Length; i++) w[i] = w[i] < floor ? floor : w[i] - (w[i] - floor) / surplus * deficit;
        }

        static void Normalize(Span<double> w)
        {
            double s = 0;
            for (var i = 0; i < w.Length; i++) s += w[i];
            if (s <= 0) { for (var i = 0; i < w.Length; i++) w[i] = 1.0 / w.Length; return; }
            for (var i = 0; i < w.Length; i++) w[i] /= s;
        }
    }

    /// <summary>최대 잉여 정수 배분(무할당) — 합이 정확히 <paramref name="total"/>.</summary>
    public static void Apportion(ReadOnlySpan<double> weights, int total, Span<int> result)
    {
        var n = weights.Length;
        if (n == 0) return;
        total = Math.Max(0, total);
        double sum = 0;
        for (var i = 0; i < n; i++) sum += Math.Max(0, weights[i]);
        var given = 0;
        for (var i = 0; i < n; i++)
        {
            result[i] = sum > 0 ? (int)Math.Floor(Math.Max(0, weights[i]) / sum * total) : 0;
            given += result[i];
        }
        while (given < total)
        {
            var best = 0; var bestRem = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                var exact = sum > 0 ? Math.Max(0, weights[i]) / sum * total : (double)total / n;
                var rem = exact - result[i];
                if (rem > bestRem) { bestRem = rem; best = i; }
            }
            result[best]++;
            given++;
        }
    }

    /// <summary>
    /// 시간 감쇠 기여도 — 전환 시각 기준 각 터치 가중 2^(-Δt/반감기) 정규화 → 플랫폼별 합(전환 1건 = 1.0).
    /// </summary>
    public static Dictionary<string, double> AttributeTimeDecay(IReadOnlyList<Touchpoint> touches, DateTimeOffset conversionAt, TimeSpan halfLife)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (touches.Count == 0) return res;
        var hl = Math.Max(1, halfLife.TotalSeconds);
        double sum = 0;
        Span<double> w = touches.Count <= 64 ? stackalloc double[touches.Count] : new double[touches.Count];
        for (var i = 0; i < touches.Count; i++)
        {
            var dt = (conversionAt - touches[i].At).TotalSeconds;
            w[i] = dt < 0 ? 0 : Math.Pow(2, -dt / hl);
            sum += w[i];
        }
        if (sum <= 0) return res;
        for (var i = 0; i < touches.Count; i++)
        {
            if (w[i] <= 0) continue;
            res.TryGetValue(touches[i].Platform, out var v);
            res[touches[i].Platform] = v + w[i] / sum;
        }
        return res;
    }

    // ══════════════════════ 집계 ══════════════════════

    /// <summary>테마별 효율 — 비용(원장 테마 버킷 · 없으면 성과 기록 수 × 평균 단가) + 성과(조회/보상) + 전환(조회 비중 배분).</summary>
    public List<ThemeEfficiency> Efficiencies()
    {
        var recs = _feedback.Records;
        var byTheme = new Dictionary<string, (int N, long Views, double RewardSum)>(StringComparer.Ordinal);
        long totalViews = 0;
        var t = _feedback.Targets;
        foreach (var r in recs)
        {
            byTheme.TryGetValue(r.Theme, out var a);
            byTheme[r.Theme] = (a.N + 1, a.Views + r.Views, a.RewardSum + PerformanceFeedbackLoopEngine.Reward(r.Ctr, r.EarlyDropRate, r.LikeRate, t));
            totalViews += r.Views;
        }
        var costByTheme = new Dictionary<string, double>(StringComparer.Ordinal);
        double costSum = 0; var costN = 0;
        if (_cost is not null)
            foreach (var b in _cost.Buckets(true))
            {
                costByTheme[b.Key] = b.Usd;
                costSum += b.Usd; costN += Math.Max(1, b.Count);
            }
        var avgUnit = costN > 0 ? costSum / costN : 1.0;
        long conv = 0; double rev = 0;
        if (_interactive is not null)
        {
            foreach (var h in _interactive.Hotspots()) conv += h.Conversions;
            rev = _interactive.Totals().Revenue;
        }
        var list = new List<ThemeEfficiency>(byTheme.Count);
        foreach (var kv in byTheme)
        {
            if (list.Count >= MaxThemes) break;
            var usd = costByTheme.TryGetValue(kv.Key, out var c) && c > 0 ? c : kv.Value.N * avgUnit;
            var share = totalViews > 0 ? (double)kv.Value.Views / totalViews : 0;
            list.Add(new ThemeEfficiency(kv.Key, kv.Value.N, usd, kv.Value.Views, (long)Math.Round(conv * share), rev * share, kv.Value.RewardSum / Math.Max(1, kv.Value.N)));
        }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Theme, b.Theme));
        return list;
    }

    /// <summary>
    /// 재배분 실행 — 효율 점수 → 비중 → 일 예산(예산 상한 · 없으면 <see cref="FallbackDailyUsd"/>) · 렌더 슬롯(온라인 노드 슬롯 합 + 로컬 1).
    /// </summary>
    public List<ThemeAllocation> Rebalance()
    {
        var eff = Efficiencies();
        var n = eff.Count;
        if (n == 0)
        {
            lock (_gate) { _plan = new(); }
            LastAction = "성과 데이터 없음 — 균등 탐색";
            Raise();
            return new();
        }
        double usdSum = 0; long viewSum = 0;
        foreach (var e in eff) { usdSum += e.Usd; viewSum += e.Views; }
        var prior = usdSum > 0 ? viewSum / usdSum : 1000;
        Span<double> scores = stackalloc double[n];
        Span<double> prev = stackalloc double[n];
        Span<double> w = stackalloc double[n];
        Span<int> slots = stackalloc int[n];
        lock (_gate)
            for (var i = 0; i < n; i++)
            {
                scores[i] = EfficiencyScore(eff[i].Usd, eff[i].Views, eff[i].MeanReward, eff[i].Roas, prior);
                prev[i] = _lastWeights.TryGetValue(eff[i].Theme, out var p) ? p : -1;
            }
        Allocate(scores, prev, Temperature, MinShare, MaxShift, w);
        var daily = _cost?.Budget.DailyCapUsd is > 0 and var cap ? cap : FallbackDailyUsd;
        var farmSlots = 1;
        if (_farm is not null) foreach (var node in _farm.Nodes) if (node.State is RenderNodeState.Online or RenderNodeState.Busy) farmSlots += node.Slots;
        Apportion(w, farmSlots, slots);
        var plan = new List<ThemeAllocation>(n);
        for (var i = 0; i < n; i++)
        {
            var e = eff[i];
            var reason = !e.HasViews ? "데이터 부족 — 탐색 하한" : "eCPM $" + e.Ecpm.ToString("0.00", CultureInfo.InvariantCulture) + " · 보상 " + e.MeanReward.ToString("0.00", CultureInfo.InvariantCulture)
                + (e.Conversions > 0 ? " · eCPA $" + e.Ecpa.ToString("0.00", CultureInfo.InvariantCulture) : "");
            plan.Add(new ThemeAllocation(e.Theme, w[i], daily * w[i], slots[i], e.Ecpm, reason));
        }
        plan.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
        lock (_gate)
        {
            _plan = plan;
            _lastWeights.Clear();
            foreach (var a in plan) _lastWeights[a.Theme] = a.Weight;
        }
        Interlocked.Increment(ref _rebalances);
        LastAction = "💹 재배분 #" + Rebalances.ToString(CultureInfo.InvariantCulture) + " · 1위 " + plan[0].Theme + " " + (plan[0].Weight * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        Raise();
        return plan;
    }

    /// <summary>전체 eCPM(가중) · ROAS — HUD/게이지용.</summary>
    public (double Ecpm, double Roas, double Usd, long Views) Totals()
    {
        double usd = 0, rev = 0; long views = 0;
        foreach (var e in Efficiencies()) { usd += e.Usd; views += e.Views; rev += e.Revenue; }
        return (views > 0 ? usd / views * 1000 : 0, usd > 0 ? rev / usd : 0, usd, views);
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Roi.OnChange", ex); }
        }
    }
}
