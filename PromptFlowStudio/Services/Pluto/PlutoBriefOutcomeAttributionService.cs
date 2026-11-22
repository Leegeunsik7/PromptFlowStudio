using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎯 Step 27 — [기획 즉시 적용] 출처 무장(arm). 적용 시점의 기획안 스냅샷 · 기대 합성 지표 · 적용 시각을 보관했다가
/// 다음 렌더 클립에 출처를 찍는다(TTL · 충실도 · 편수 예산으로 오귀속 차단).
/// </summary>
public sealed record PlutoAppliedBriefArm(string BriefId, string Kind, string Format, int TotalEpisodes, string Theme, string HookStyle, string ColorLookId,
    string BgmMood, string TitleFraming, double ExpectedComposite, DateTimeOffset AppliedAtUtc)
{
    /// <summary>이미 출처를 찍은 클립 VideoId(같은 영상 재렌더 → 재사용 허용 · 예산 = 편수).</summary>
    public IReadOnlyList<string> StampedVideoIds { get; init; } = Array.Empty<string>();

    /// <summary>출처 예산(단편 1 · 시리즈 = 총 편수).</summary>
    public int Budget => Math.Max(1, TotalEpisodes);

    /// <summary>기획안 → 무장(ID 없음 → null).</summary>
    public static PlutoAppliedBriefArm? From(PlutoTopicBrief? brief, DateTimeOffset appliedAtUtc)
    {
        if (brief is null || string.IsNullOrWhiteSpace(brief.BriefId)) return null;
        var expected = double.IsFinite(brief.ExpectedComposite) && brief.ExpectedComposite > 0 ? brief.ExpectedComposite : PlutoBriefOutcomeAttributionService.PriorComposite;
        return new PlutoAppliedBriefArm(brief.BriefId, brief.Kind ?? string.Empty, brief.Format ?? string.Empty, brief.TotalEpisodes, brief.Theme ?? string.Empty,
            brief.HookStyle ?? string.Empty, brief.ColorLookId ?? string.Empty, brief.BgmMood ?? string.Empty, brief.TitleFraming ?? string.Empty,
            Math.Clamp(expected, 0.0, 100.0), appliedAtUtc);
    }
}

/// <summary>🎯 출처 찍기 판정.</summary>
public enum PlutoBriefStampDecision
{
    /// <summary>출처 기록.</summary>
    Stamped,
    /// <summary>무장된 기획 없음.</summary>
    NoArm,
    /// <summary>TTL 경과 → 무장 해제.</summary>
    Expired,
    /// <summary>크리에이터가 룩/BGM 을 기획과 다르게 바꿈 → 이 클립은 기획 책임 아님(무장 유지).</summary>
    Diverged,
    /// <summary>편수 예산 소진 → 무장 해제.</summary>
    BudgetExhausted,
}

/// <summary>🎯 클립 1편 채점 결과.</summary>
public sealed record PlutoBriefVerdict(string ClipVideoId, string BriefId, double ExpectedComposite, double RealizedComposite, double SurpriseDelta, bool IsHit, bool IsSevere)
{
    /// <summary>오답 노트 사후 분석 기록됨.</summary>
    public bool PostMortemRecorded { get; init; }
    /// <summary>사후 분석 조합 키(<c>pair:</c> · <c>domain_hook:</c>).</summary>
    public IReadOnlyList<string> PostMortemKeys { get; init; } = Array.Empty<string>();
    /// <summary>사후 분석 실패 차원(<see cref="PlutoMistakeNotebookService"/> 차원 상수).</summary>
    public string? FailureDimension { get; init; }
}

/// <summary>🎯 채점 스윕 결과.</summary>
public sealed record PlutoBriefSweepResult(int EvaluatedCount, int HitCount, int PostMortemCount, IReadOnlyList<PlutoBriefVerdict> Verdicts)
{
    public static readonly PlutoBriefSweepResult Empty = new(0, 0, 0, Array.Empty<PlutoBriefVerdict>());
}

/// <summary>
/// 📐 Step 27 — 기획안 적중률(카탈로그 클립 채점 결과에서 매번 파생 · 별도 카운터 없음 → 재가져오기 · 멀티 PC 병합에도 이중 집계 없음).
/// 집계 단위 = 기획 결정 1건(기획안 ID + 적용 시각) — 시리즈 3편은 평균 SurpriseDelta 로 1건.
/// </summary>
public sealed record PlutoBriefHitRateSummary(int EvaluatedBriefs, int Hits, int SevereMisses, int ExploitEvaluated, int ExploitHits, int ExploreEvaluated, int ExploreHits,
    double MeanSurpriseDelta, int EvaluatedClips, int PendingClips)
{
    public static readonly PlutoBriefHitRateSummary Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>실측 채점된 기획 결정이 1건 이상.</summary>
    public bool HasEvidence => EvaluatedBriefs > 0;

    /// <summary>적중률(%) = hits / evaluatedBriefs.</summary>
    public double HitRatePct => EvaluatedBriefs > 0 ? Hits * 100.0 / EvaluatedBriefs : 0.0;

    /// <summary>보정 활성(평가 ≥ <see cref="PlutoBriefOutcomeAttributionService.MinCalibrationBriefs"/>).</summary>
    public bool IsCalibrating => EvaluatedBriefs >= PlutoBriefOutcomeAttributionService.MinCalibrationBriefs;

    /// <summary>⚖️ 활용 기획 점수 배수 = 0.8 + 0.4 · Beta(1,1) 축소 적중률(보정 전 · 버킷 표본 0 → 1.0).</summary>
    public double ExploitFactor => Factor(ExploitHits, ExploitEvaluated);

    /// <summary>🎰🧬 탐색 · 경험적 베이즈 기획 점수 배수.</summary>
    public double ExploreFactor => Factor(ExploreHits, ExploreEvaluated);

    private double Factor(int hits, int n) => !IsCalibrating ? 1.0 : 0.8 + 0.4 * ((hits + 1.0) / (n + 2.0));

    /// <summary>클립 채점 결과 → 기획 결정 단위 적중률(순수 · 결정론).</summary>
    public static PlutoBriefHitRateSummary Compute(IReadOnlyList<PlutoRenderedClipRecord>? clips)
    {
        if (clips is null || clips.Count == 0) return Empty;
        var groups = new Dictionary<string, (double Sum, int N, double Min, string Kind)>(StringComparer.Ordinal);
        var seenClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int evaluatedClips = 0, pending = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (c is null || !c.HasAppliedBrief || !seenClips.Add(c.VideoId)) continue;
            if (!c.AppliedBriefEvaluated || c.AppliedBriefSurpriseDelta is not { } d || !double.IsFinite(d)) { pending++; continue; }
            evaluatedClips++;
            var key = PlutoBriefOutcomeAttributionService.DecisionKeyOf(c);
            groups[key] = groups.TryGetValue(key, out var g) ? (g.Sum + d, g.N + 1, Math.Min(g.Min, d), g.Kind) : (d, 1, d, c.AppliedBriefKind ?? string.Empty);
        }
        if (groups.Count == 0) return Empty with { PendingClips = pending };
        int hits = 0, severe = 0, xe = 0, xh = 0, re = 0, rh = 0;
        double sum = 0;
        foreach (var g in groups.Values)
        {
            var mean = g.Sum / g.N;
            var hit = mean >= 0.0;
            sum += mean;
            if (hit) hits++;
            if (g.Min <= PlutoBriefOutcomeAttributionService.SevereDeltaPoints) severe++;
            if (g.Kind == PlutoTopicBriefKinds.ParetoExploit) { xe++; if (hit) xh++; }
            else if (g.Kind is PlutoTopicBriefKinds.UpsideExplore or PlutoTopicBriefKinds.ColdStartPrior) { re++; if (hit) rh++; }
        }
        return new PlutoBriefHitRateSummary(groups.Count, hits, severe, xe, xh, re, rh, sum / groups.Count, evaluatedClips, pending);
    }

    /// <summary>기획안 적중률 요약 1줄(결정론 한국어 · 평가 없음 → 대기 안내).</summary>
    public string ToLabel()
    {
        if (!HasEvidence)
            return PendingClips > 0 ? "적용 기획 " + PendingClips.ToString(Inv) + "편 실측 대기(조회 " + PlutoBriefOutcomeAttributionService.MinViews.ToString(Inv) + "회부터 채점)" : "적용 기획 채점 없음";
        var s = "평가 " + EvaluatedBriefs.ToString(Inv) + "건 · 적중 " + Hits.ToString(Inv) + "건(" + HitRatePct.ToString("0", Inv) + "%) · 평균 기대 대비 "
            + MeanSurpriseDelta.ToString("+0.0;-0.0;0.0", Inv) + "p";
        if (SevereMisses > 0) s += " · 🩻 사후 분석 " + SevereMisses.ToString(Inv) + "건";
        if (ExploitEvaluated > 0) s += " · 활용 " + ExploitHits.ToString(Inv) + "/" + ExploitEvaluated.ToString(Inv);
        if (ExploreEvaluated > 0) s += " · 탐색 " + ExploreHits.ToString(Inv) + "/" + ExploreEvaluated.ToString(Inv);
        if (IsCalibrating) s += " · 보정 활용 ×" + ExploitFactor.ToString("0.00", Inv) + " / 탐색 ×" + ExploreFactor.ToString("0.00", Inv);
        else s += " · 보정은 " + PlutoBriefOutcomeAttributionService.MinCalibrationBriefs.ToString(Inv) + "건부터";
        if (PendingClips > 0) s += " · 대기 " + PendingClips.ToString(Inv) + "편";
        return s;
    }
}

/// <summary>🎯 Step 27 — 적용 기획안 실측 채점 · 자동 사후 분석 · 적중률 피드백.</summary>
public interface IPlutoBriefOutcomeAttributionService
{
    /// <summary>카탈로그 변경 구독 시작(멱등) — 워터마크 갱신 · 수동 페어링 · 멀티 PC 재적재 어느 경로로 실측이 들어와도 채점.</summary>
    void Start();

    /// <summary>채점 대상(출처 有 · 미채점 · 조회 ≥ <see cref="PlutoBriefOutcomeAttributionService.MinViews"/>) 전수 채점(클립당 정확히 1회).</summary>
    Task<PlutoBriefSweepResult> SweepAsync(CancellationToken ct = default);

    /// <summary>카탈로그 파생 적중률.</summary>
    Task<PlutoBriefHitRateSummary> GetHitRateAsync(CancellationToken ct = default);

    /// <summary>마지막 스윕(없음 → null).</summary>
    PlutoBriefSweepResult? LastSweep { get; }

    /// <summary>채점 발생 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🎯 <see cref="IPlutoBriefOutcomeAttributionService"/> 구현(순수 BCL · 결정론 채점).
///   ▸ 합성 지표 = 0.4·3초 유지 + 0.4·완주 + 0.2·min(100, CTR×10) — SurpriseDelta = 실측 − 기획 시점 기대.
///   ▸ 정확히 1회 — 카탈로그 게이트 안 CAS(<see cref="IPlutoRenderCatalogService.TryRecordBriefOutcomeAsync"/>) 성공 시에만 사후 분석.
///   ▸ 🩻 SurpriseDelta ≤ <see cref="SevereDeltaPoints"/> → 오답 노트에 그 조합(<c>pair:</c>훅+룩 · <c>domain_hook:</c>)만 차단 벌점(단일 훅 전역 차단 아님) · 같은 기획 결정은 1회.
///   ▸ 📐 적중률은 카탈로그 채점 결과에서 파생(별도 카운터 없음) → 기획기가 활용/탐색 점수 배수로 사용.
/// </summary>
public sealed class PlutoBriefOutcomeAttributionService : IPlutoBriefOutcomeAttributionService, IDisposable
{
    /// <summary>채점 최소 조회수(미성숙 스냅샷 노이즈 차단).</summary>
    public const long MinViews = 50;
    /// <summary>심각 미달 — 사후 분석 임계(포인트).</summary>
    public const double SevereDeltaPoints = -12.0;
    /// <summary>사후 분석 벌점(≥ <see cref="PlutoMistakeNotebookService.BlockThreshold"/> → 해당 조합 즉시 차단).</summary>
    public const double PostMortemPenalty = 2.0;
    /// <summary>실측 근거 없는 채널의 기대 합성 사전값.</summary>
    public const double PriorComposite = 40.0;
    /// <summary>훅별 기대치 → 채널 기준선으로의 경험적 베이즈 수축 강도(가상 표본 수).</summary>
    public const double ShrinkStrength = 2.0;
    /// <summary>적중률 보정 시작 평가 건수.</summary>
    public const int MinCalibrationBriefs = 3;
    /// <summary>무장 TTL — 적용 후 이 시간이 지나면 렌더해도 기획 출처를 찍지 않음.</summary>
    public static readonly TimeSpan ArmTtl = TimeSpan.FromHours(48);
    /// <summary>카탈로그 변경 → 스윕 디바운스.</summary>
    public const int SweepDebounceMs = 400;
    public const string PostMortemReasonPrefix = "brief-postmortem: ";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPlutoRenderCatalogService _catalog;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private PlutoBriefSweepResult? _last;
    private int _started;
    private int _sweepScheduled;
    private int _disposed;

    public PlutoBriefOutcomeAttributionService(IPlutoRenderCatalogService catalog, IPlutoMistakeNotebookService? notebook)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _notebook = notebook;
    }

    public event Action? Changed;

    public PlutoBriefSweepResult? LastSweep => Volatile.Read(ref _last);

    // ── 순수 함수 ──

    /// <summary>합성 지표(0~100) — 0.4·3초 + 0.4·완주 + 0.2·min(100, CTR×10).</summary>
    public static double Composite(double hookPct, double completionPct, double ctrPct)
    {
        static double P(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 100.0) : 0.0;
        return 0.4 * P(hookPct) + 0.4 * P(completionPct) + 0.2 * Math.Min(100.0, P(ctrPct) * 10.0);
    }

    /// <summary>기획 결정 키 = 기획안 ID + 적용 시각(시리즈 여러 편 → 1건).</summary>
    public static string DecisionKeyOf(PlutoRenderedClipRecord c)
        => (c.AppliedBriefId ?? string.Empty) + "@" + (c.AppliedBriefAppliedAtUtc?.UtcTicks.ToString(Inv) ?? "-");

    private static bool TryMeasured(PlutoRenderedClipRecord? c, out double composite, out double weight)
    {
        composite = weight = 0;
        if (c?.YouTubeWatermark is not { } wm || wm.Views <= 0) return false;
        if (!Valid(wm.HookPct) || !Valid(wm.CompletionPct) || !Valid(wm.CtrPct)) return false;
        composite = Composite(wm.HookPct, wm.CompletionPct, wm.CtrPct);
        weight = Math.Min(1.0, (double)wm.Views / PlutoYouTubePublishPackService.FullWeightViews);
        return true;
    }

    private static bool Valid(double v) => double.IsFinite(v) && v >= 0 && v <= 100;

    /// <summary>
    /// 📏 기획 시점 기대 합성 — 채널 기준선(조회수 가중 · 1,000회 상한) → 훅별 실측 평균을 기준선으로 경험적 베이즈 수축(k = <see cref="ShrinkStrength"/>) → 종류 보정.
    /// 실측 없음 → <see cref="PriorComposite"/>(evidenced = false).
    /// </summary>
    public static double EstimateExpectedComposite(string? kind, string? hookStyle, IReadOnlyList<PlutoRenderedClipRecord>? clips, out bool evidenced, out int hookSamples)
    {
        evidenced = false;
        hookSamples = 0;
        double wSum = 0, wComp = 0, hW = 0, hComp = 0;
        var hookKey = PlutoRenderCatalogService.NormalizeKey(hookStyle);
        if (clips is not null)
            for (var i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (!TryMeasured(c, out var comp, out var w)) continue;
                wSum += w;
                wComp += w * comp;
                if (hookKey.Length > 0 && (string.Equals(PlutoRenderCatalogService.NormalizeKey(c!.AppliedBriefHookStyle), hookKey, StringComparison.Ordinal)
                    || string.Equals(PlutoRenderCatalogService.NormalizeKey(c.HookStyle), hookKey, StringComparison.Ordinal)))
                {
                    hW += w;
                    hComp += w * comp;
                    hookSamples++;
                }
            }
        var baseline = PriorComposite;
        if (wSum > 0) { baseline = wComp / wSum; evidenced = true; }
        var expected = hW > 0 ? (hComp + ShrinkStrength * baseline) / (hW + ShrinkStrength) : baseline;
        expected += kind switch
        {
            PlutoTopicBriefKinds.ParetoExploit => 1.5,
            PlutoTopicBriefKinds.UpsideExplore => -3.0,
            PlutoTopicBriefKinds.SeriesReboot => -2.0,
            _ => 0.0,
        };
        return Math.Round(Math.Clamp(expected, 0.0, 100.0), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 🎯 렌더 클립에 기획 출처 찍기(순수). TTL 경과 → 해제 · 룩(알려진 프리셋)/BGM 이 기획과 다르면 출처 없음(무장 유지) ·
    /// 편수 예산 소진 → 해제. 같은 VideoId 재렌더는 예산을 소비하지 않음.
    /// </summary>
    public static PlutoRenderedClipRecord StampProvenance(PlutoRenderedClipRecord record, PlutoAppliedBriefArm? arm, DateTimeOffset nowUtc, int episodeNumber,
        out PlutoAppliedBriefArm? nextArm, out PlutoBriefStampDecision decision)
    {
        ArgumentNullException.ThrowIfNull(record);
        nextArm = arm;
        if (arm is null) { decision = PlutoBriefStampDecision.NoArm; return record; }
        if (nowUtc - arm.AppliedAtUtc > ArmTtl || nowUtc < arm.AppliedAtUtc - TimeSpan.FromMinutes(5)) { nextArm = null; decision = PlutoBriefStampDecision.Expired; return record; }
        if (Diverges(record, arm)) { decision = PlutoBriefStampDecision.Diverged; return record; }
        var already = false;
        foreach (var v in arm.StampedVideoIds) if (string.Equals(v, record.VideoId, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
        if (!already && arm.StampedVideoIds.Count >= arm.Budget) { nextArm = null; decision = PlutoBriefStampDecision.BudgetExhausted; return record; }
        if (!already)
        {
            var ids = new List<string>(arm.StampedVideoIds.Count + 1);
            ids.AddRange(arm.StampedVideoIds);
            ids.Add(record.VideoId);
            nextArm = arm with { StampedVideoIds = ids };
        }
        decision = PlutoBriefStampDecision.Stamped;
        return record with
        {
            AppliedBriefId = arm.BriefId, AppliedBriefFormat = arm.Format, AppliedBriefExpectedRetention = arm.ExpectedComposite, AppliedBriefTitleFraming = arm.TitleFraming,
            AppliedBriefEvaluated = false, AppliedBriefSurpriseDelta = null, AppliedBriefKind = arm.Kind, AppliedBriefHookStyle = arm.HookStyle,
            AppliedBriefColorLookId = arm.ColorLookId, AppliedBriefBgmMood = arm.BgmMood, AppliedBriefTheme = arm.Theme, AppliedBriefAppliedAtUtc = arm.AppliedAtUtc,
        };
    }

    /// <summary>충실도 위반 — 클립 룩이 알려진 프리셋인데 기획 룩과 다름 · BGM 무드가 둘 다 있는데 다름.</summary>
    public static bool Diverges(PlutoRenderedClipRecord record, PlutoAppliedBriefArm arm)
    {
        if (arm.ColorLookId.Length > 0 && !string.IsNullOrWhiteSpace(record.ColorLookId) && PlutoColorGradingCompiler.TryGetPreset(record.ColorLookId) is not null
            && !string.Equals(record.ColorLookId.Trim(), arm.ColorLookId.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return arm.BgmMood.Length > 0 && !string.IsNullOrWhiteSpace(record.BgmMood)
            && !string.Equals(PlutoRenderCatalogService.NormalizeKey(record.BgmMood), PlutoRenderCatalogService.NormalizeKey(arm.BgmMood), StringComparison.Ordinal);
    }

    /// <summary>채점(순수) — 출처 有 · 미채점 · 조회 ≥ <see cref="MinViews"/> · 지표 유효일 때만(그 밖 → null).</summary>
    public static PlutoBriefVerdict? Grade(PlutoRenderedClipRecord? clip)
    {
        if (clip is null || !clip.HasAppliedBrief || clip.AppliedBriefEvaluated || clip.AppliedBriefExpectedRetention is not { } expected || !double.IsFinite(expected)) return null;
        if (clip.YouTubeWatermark is not { } wm || wm.Views < MinViews) return null;
        if (!TryMeasured(clip, out var realized, out _)) return null;
        var delta = Math.Round(realized - expected, 2, MidpointRounding.AwayFromZero);
        return new PlutoBriefVerdict(clip.VideoId, clip.AppliedBriefId!, expected, Math.Round(realized, 2, MidpointRounding.AwayFromZero), delta, delta >= 0.0, delta <= SevereDeltaPoints);
    }

    /// <summary>🩻 사후 분석 조합 키 — <c>pair:</c>훅+룩 · <c>domain_hook:</c>테마 도메인+훅(단일 특성 키는 넣지 않음 → 훅 자체를 전역 차단하지 않음).</summary>
    public static IReadOnlyList<string> PostMortemKeysOf(PlutoRenderedClipRecord clip)
    {
        var keys = new List<string>(2);
        var hook = string.IsNullOrWhiteSpace(clip.AppliedBriefHookStyle) ? clip.HookStyle : clip.AppliedBriefHookStyle;
        var look = string.IsNullOrWhiteSpace(clip.AppliedBriefColorLookId) ? clip.ColorLookId : clip.AppliedBriefColorLookId;
        if (PlutoPairAttribution.PairKey(hook, look) is { } pair) keys.Add(pair);
        if (!string.IsNullOrWhiteSpace(clip.AppliedBriefTheme) && PlutoPairAttribution.DomainHookKey(PromptDomainClassifier.Classify(clip.AppliedBriefTheme).ToString(), hook) is { } dh)
            keys.Add(dh);
        return keys;
    }

    // ── 서비스 ──

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
        _catalog.Changed += OnCatalogChanged;
        OnCatalogChanged();   // 기동 직후 1회(다른 PC 병합본 · 이전 세션 미채점분)
    }

    private void OnCatalogChanged()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _sweepScheduled, 1) != 0) return;   // 코얼레싱
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SweepDebounceMs, _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _sweepScheduled, 0);
                await SweepAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { Volatile.Write(ref _sweepScheduled, 0); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Volatile.Write(ref _sweepScheduled, 0); Program.Log("Pluto.BriefOutcome.AutoSweep", ex); }   // 콜백 예외 격리
        });
    }

    public async Task<PlutoBriefSweepResult> SweepAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return PlutoBriefSweepResult.Empty;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        List<PlutoBriefVerdict>? verdicts = null;
        int hits = 0, postMortems = 0;
        try
        {
            var clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false);
            // 이미 사후 분석된 기획 결정(같은 결정의 다른 편이 심각 미달로 채점됨) → 중복 벌점 금지
            var postMortemDecisions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in clips)
                if (c is not null && c.AppliedBriefEvaluated && c.AppliedBriefSurpriseDelta is { } d && d <= SevereDeltaPoints) postMortemDecisions.Add(DecisionKeyOf(c));
            foreach (var c in clips)
            {
                ct.ThrowIfCancellationRequested();
                if (Grade(c) is not { } verdict) continue;
                if (!await _catalog.TryRecordBriefOutcomeAsync(c.VideoId, verdict.SurpriseDelta, ct).ConfigureAwait(false)) continue;   // 🔒 CAS 실패 → 다른 스윕/PC 가 채점
                if (verdict.IsHit) hits++;
                if (verdict.IsSevere && postMortemDecisions.Add(DecisionKeyOf(c)))
                {
                    var keys = PostMortemKeysOf(c);
                    var wm = c.YouTubeWatermark!;
                    var (_, dimension) = PlutoClosedLoopReinforcementService.SteepestNormalizedDeficit(wm.HookPct, wm.CompletionPct, wm.CtrPct);
                    var recorded = false;
                    if (_notebook is not null && keys.Count > 0)
                    {
                        try
                        {
                            await _notebook.RecordDiagnosedMistakeAsync(keys, PostMortemPenalty, dimension,
                                PostMortemReasonPrefix + verdict.BriefId + " Δ" + verdict.SurpriseDelta.ToString("0.0", Inv) + "p", ct).ConfigureAwait(false);
                            recorded = true;
                            postMortems++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Program.Log("Pluto.BriefOutcome.PostMortem", ex); }   // [부분 구제] 채점은 유지
                    }
                    verdict = verdict with { PostMortemRecorded = recorded, PostMortemKeys = keys, FailureDimension = dimension };
                }
                (verdicts ??= new List<PlutoBriefVerdict>(4)).Add(verdict);
            }
        }
        finally { _gate.Release(); }
        if (verdicts is null) return PlutoBriefSweepResult.Empty;
        var result = new PlutoBriefSweepResult(verdicts.Count, hits, postMortems, verdicts);
        Volatile.Write(ref _last, result);
        try { Changed?.Invoke(); }
        catch (Exception ex) { Program.Log("Pluto.BriefOutcome.Changed", ex); }
        return result;
    }

    public async Task<PlutoBriefHitRateSummary> GetHitRateAsync(CancellationToken ct = default)
        => PlutoBriefHitRateSummary.Compute(await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Volatile.Read(ref _started) != 0) _catalog.Changed -= OnCatalogChanged;
        Changed = null;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _gate.Dispose();
    }
}
