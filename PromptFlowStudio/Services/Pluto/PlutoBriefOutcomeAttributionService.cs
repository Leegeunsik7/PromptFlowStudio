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
    /// <summary>🌱 Step 28 — 이번 스윕에서 유기적 복권된 조합 키.</summary>
    public IReadOnlyList<string> RehabilitatedKeys { get; init; } = Array.Empty<string>();

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

    /// <summary>
    /// 🔮 Step 28 — 렌더 전 셋업 예측(기획안 없이 고른 현재 Workspace 조합 포함) — 0~100 예상 합성 · 신뢰 등급 · 오답 노트 활성 벌점/차단 · 안전 룩 대안.
    /// 마지막 카탈로그 스냅샷(스윕 · <see cref="RefreshForecastCacheAsync"/>)으로 동기 평가.
    /// </summary>
    PlutoSetupForecast EvaluateSetupForecast(string? theme, string? hook, string? look, string? bgm, string? renderMode);

    /// <summary>🔮 예측용 카탈로그 스냅샷 갱신(기본 구현 = 없음).</summary>
    Task RefreshForecastCacheAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 🎯 Step 29 — YouTube Studio 내보내기 수집(자동 감시 · 수동) 직후 호출: 새로 성숙한 클립(조회 ≥ <see cref="PlutoBriefOutcomeAttributionService.MinViews"/>) 채점 ·
    /// 유기적 복권을 즉시 수행(멱등 — 클립당 채점 CAS · 복권 클립당 1회). 기본 구현 = <see cref="SweepAsync"/>.
    /// </summary>
    Task<PlutoBriefSweepResult> EvaluatePendingBriefOutcomesAndRehabilitationAsync(CancellationToken ct = default) => SweepAsync(ct);

    /// <summary>채점 발생 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>🔮 Step 28 — 예측 신뢰 등급(실측 표본 기반).</summary>
public static class PlutoForecastConfidence
{
    /// <summary>실측 근거 없음 → 사전값.</summary>
    public const string Prior = "Prior";
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    /// <summary>등급 → 한국어 라벨.</summary>
    public static string Korean(string? tier) => tier switch
    {
        High => "높음",
        Medium => "보통",
        Low => "낮음",
        _ => "사전값",
    };
}

/// <summary>
/// 🔮 Step 28 — 렌더 전 셋업 예측. <see cref="PredictedScore"/> = 0~100 예상 합성 지표(오답 노트 벌점 차감 반영) ·
/// <see cref="IsBlocked"/> = 활성 오답 가드(조합 키 · 단일 특성) · <see cref="SafeLookId"/> = 룩 교체로 해소 가능한 차단일 때만 대안 룩.
/// </summary>
public sealed record PlutoSetupForecast(double PredictedScore, double BaseScore, string ConfidenceTier, int EvidenceSamples, double ActivePenalty, bool IsBlocked,
    string? BlockedKey, bool LookSwapFixesBlock, string? SafeLookId, string? WarningKorean)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly PlutoSetupForecast Prior = new(PlutoBriefOutcomeAttributionService.PriorComposite, PlutoBriefOutcomeAttributionService.PriorComposite,
        PlutoForecastConfidence.Prior, 0, 0.0, false, null, false, null, null);

    /// <summary>배지 1줄(결정론 한국어).</summary>
    public string ToBadgeLabel()
    {
        var s = "🔮 예상 " + PredictedScore.ToString("0", Inv) + "점 · 신뢰 " + PlutoForecastConfidence.Korean(ConfidenceTier);
        if (EvidenceSamples > 0) s += "(" + EvidenceSamples.ToString(Inv) + "건)";
        if (IsBlocked) s += " · 🛡️ 차단 " + (BlockedKey ?? "") + " ⚖" + ActivePenalty.ToString("0.00", Inv);
        else if (ActivePenalty >= PlutoMistakeNotebookService.RecoveringFloor) s += " · ⏳ 회복 중 벌점 ⚖" + ActivePenalty.ToString("0.00", Inv);
        return s;
    }
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
    private IReadOnlyList<PlutoRenderedClipRecord>? _clipCache;

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
        => EstimateExpectedComposite(kind, hookStyle, clips, null, out evidenced, out hookSamples);

    /// <summary>📏 <paramref name="excludeVideoId"/> 클립을 표본에서 뺀 기대 합성(leave-one-out — 자기 성과가 자기 기대치를 끌어올려 놀람을 상쇄하지 않도록).</summary>
    public static double EstimateExpectedComposite(string? kind, string? hookStyle, IReadOnlyList<PlutoRenderedClipRecord>? clips, string? excludeVideoId, out bool evidenced, out int hookSamples)
    {
        evidenced = false;
        hookSamples = 0;
        double wSum = 0, wComp = 0, hW = 0, hComp = 0;
        var hookKey = PlutoRenderCatalogService.NormalizeKey(hookStyle);
        if (clips is not null)
            for (var i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (excludeVideoId is not null && c is not null && string.Equals(c.VideoId, excludeVideoId, StringComparison.OrdinalIgnoreCase)) continue;
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

    // ── 🌱 Step 28 — 유기적 복권 ──

    /// <summary>
    /// 🌱 복권 판정용 SurpriseDelta(순수) — 조회 ≥ <see cref="MinViews"/> · 지표 유효일 때만.
    /// 기획 클립 = 채점 결과(<paramref name="justGradedDelta"/> 우선) · 커스텀 클립(기획 없음) = 실측 − leave-one-out 훅 기대치.
    /// 기획기는 차단 조합을 제안하지 않으므로 커스텀 셋업 경로가 없으면 복권은 도달 불가.
    /// </summary>
    public static double? OrganicSurpriseOf(PlutoRenderedClipRecord? clip, IReadOnlyList<PlutoRenderedClipRecord>? clips, double? justGradedDelta = null)
    {
        if (clip?.YouTubeWatermark is not { } wm || wm.Views < MinViews || !TryMeasured(clip, out var realized, out _)) return null;
        if (clip.HasAppliedBrief)
        {
            var d = justGradedDelta ?? (clip.AppliedBriefEvaluated ? clip.AppliedBriefSurpriseDelta : null);
            return d is { } v && double.IsFinite(v) ? v : null;
        }
        var expected = EstimateExpectedComposite(null, clip.HookStyle, clips, clip.VideoId, out _, out _);
        return Math.Round(realized - expected, 2, MidpointRounding.AwayFromZero);
    }

    // ── 🔮 Step 28 — 렌더 전 셋업 예측 ──

    /// <summary>예측 감점 = min(<see cref="ForecastMaxPenaltyPoints"/>, 유효 벌점 × <see cref="ForecastPenaltyPointsPerUnit"/>).</summary>
    public const double ForecastPenaltyPointsPerUnit = 5.0, ForecastMaxPenaltyPoints = 15.0;
    /// <summary>룩 · BGM · 렌더 모드 리프트 가중(훅 기대치와 이중 계산 방지).</summary>
    public const double ForecastSideLiftWeight = 0.5;

    private static readonly string[] s_lookPresets =
    {
        PlutoColorGradingCompiler.GoldenHourWarmName, PlutoColorGradingCompiler.VintageFilm16mmName, PlutoColorGradingCompiler.NaturalPastelName,
    };

    /// <summary>
    /// 🔮 셋업 예측(순수 · 결정론). 기대 = 훅 기대치(채널 기준선 수축) + 0.5 × (룩 · BGM · 렌더 모드 수축 리프트) − 오답 노트 감점.
    /// 차단 = 조합 키(<c>pair:</c>훅+룩 · <c>domain_hook:</c>) 또는 단일 특성(룩 · 훅 · BGM)이 활성 가드. 룩 교체 대안은 룩이 원인인 차단일 때만.
    /// </summary>
    public static PlutoSetupForecast EvaluateSetupForecast(string? theme, string? hook, string? look, string? bgm, string? renderMode,
        IReadOnlyList<PlutoRenderedClipRecord>? clips, IPlutoMistakeNotebookService? notebook)
    {
        var expected = EstimateExpectedComposite(null, hook, clips, null, out var evidenced, out var hookSamples);
        var baseline = ChannelBaseline(clips);
        var lookPreset = PlutoColorGradingCompiler.TryGetPreset(look);
        var lookLabel = lookPreset?.PresetName ?? (string.IsNullOrWhiteSpace(look) ? null : look.Trim());
        var lookLift = Lift(clips, 1, lookLabel, baseline, out var lookSamples);
        var bgmKey = string.IsNullOrWhiteSpace(bgm) || bgm is "none" or "custom" ? null : bgm;
        var bgmLift = Lift(clips, 2, bgmKey, baseline, out var bgmSamples);
        var modeLift = Lift(clips, 3, renderMode, baseline, out _);
        var raw = expected + ForecastSideLiftWeight * (lookLift + bgmLift + modeLift);
        var samples = hookSamples + lookSamples + bgmSamples;
        var tier = !evidenced ? PlutoForecastConfidence.Prior : samples >= 8 ? PlutoForecastConfidence.High : samples >= 3 ? PlutoForecastConfidence.Medium : PlutoForecastConfidence.Low;

        // 🛡️ 오답 노트
        double penalty = 0;
        string? blockedKey = null, worstKey = null;
        var lookFixable = false;
        string? safeLook = null;
        if (notebook is not null)
        {
            try
            {
                var composite = notebook.GetSuppressedCompositeKeys();
                var single = notebook.GetSuppressedTokens();
                var pair = PlutoPairAttribution.PairKey(hook, lookLabel);
                var dh = string.IsNullOrWhiteSpace(theme) ? null : PlutoPairAttribution.DomainHookKey(PromptDomainClassifier.Classify(theme).ToString(), hook);
                Consider(notebook, pair, composite, true, ref penalty, ref worstKey, ref blockedKey, ref lookFixable);
                Consider(notebook, dh, composite, false, ref penalty, ref worstKey, ref blockedKey, ref lookFixable);
                if (blockedKey is null && lookPreset is not null && PlutoColorGradingCompiler.IsSuppressed(lookPreset, notebook)) { blockedKey = lookPreset.PresetName; lookFixable = true; penalty = Math.Max(penalty, notebook.GetEffectivePenalty(lookPreset.PresetName)); }
                if (blockedKey is null && !string.IsNullOrWhiteSpace(hook) && single.Contains(hook.Trim())) { blockedKey = hook.Trim(); penalty = Math.Max(penalty, notebook.GetEffectivePenalty(hook)); }
                if (blockedKey is null && bgmKey is not null && single.Contains(bgmKey.Trim())) { blockedKey = bgmKey.Trim(); penalty = Math.Max(penalty, notebook.GetEffectivePenalty(bgmKey)); }
                // 🔁 룩 교체 대안 — 룩이 원인인 차단(pair · 룩 단일 가드)만 · domain_hook / 훅 / BGM 차단은 룩을 바꿔도 그대로이므로 제안하지 않음
                if (blockedKey is not null && lookFixable && !HasNonLookBlock(notebook, dh, composite, hook, bgmKey, single))
                {
                    double bestLift = double.NegativeInfinity;
                    foreach (var cand in s_lookPresets)
                    {
                        if (lookPreset is not null && string.Equals(cand, lookPreset.PresetName, StringComparison.OrdinalIgnoreCase)) continue;
                        var candPreset = PlutoColorGradingCompiler.TryGetPreset(cand);
                        if (candPreset is null || PlutoColorGradingCompiler.IsSuppressed(candPreset, notebook)) continue;
                        if (PlutoPairAttribution.PairKey(hook, cand) is { } cp && (composite.Contains(cp) || notebook.GetEffectivePenalty(cp) >= PlutoMistakeNotebookService.ReleaseThreshold)) continue;
                        var l = Lift(clips, 1, cand, baseline, out _);
                        if (l > bestLift) { bestLift = l; safeLook = cand; }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Forecast] notebook: " + ex.Message); }   // [부분 구제] 벌점 없이 예측
        }
        var deduction = penalty >= PlutoMistakeNotebookService.RecoveringFloor ? Math.Min(ForecastMaxPenaltyPoints, penalty * ForecastPenaltyPointsPerUnit) : 0.0;
        var baseScore = Math.Round(Math.Clamp(raw, 0.0, 100.0), 1, MidpointRounding.AwayFromZero);
        var predicted = Math.Round(Math.Clamp(raw - deduction, 0.0, 100.0), 1, MidpointRounding.AwayFromZero);
        string? warning = null;
        if (blockedKey is not null)
            warning = "🛡️ 오답 노트 차단 조합: " + blockedKey + (safeLook is not null ? " — 룩을 " + safeLook + "(으)로 바꾸면 해소" : lookFixable ? " — 차단 없는 대안 룩이 없어요" : " — 룩 교체로는 해소되지 않아요(훅/테마 변경 필요)");
        else if (deduction > 0 && worstKey is not null)
            warning = "⏳ 회복 중인 오답 벌점: " + worstKey + " (−" + deduction.ToString("0.0", Inv) + "점)";
        return new PlutoSetupForecast(predicted, baseScore, tier, samples, Math.Round(penalty, 2, MidpointRounding.AwayFromZero), blockedKey is not null,
            blockedKey, lookFixable, safeLook, warning);
    }

    private static void Consider(IPlutoMistakeNotebookService nb, string? key, IReadOnlySet<string> composite, bool isPair, ref double penalty, ref string? worstKey, ref string? blockedKey, ref bool lookFixable)
    {
        if (key is null) return;
        var eff = nb.GetEffectivePenalty(key);
        if (eff > penalty) { penalty = eff; worstKey = key; }
        if (blockedKey is null && composite.Contains(key)) { blockedKey = key; lookFixable = isPair; }
    }

    private static bool HasNonLookBlock(IPlutoMistakeNotebookService nb, string? dh, IReadOnlySet<string> composite, string? hook, string? bgm, IReadOnlySet<string> single)
        => (dh is not null && composite.Contains(dh))
            || (!string.IsNullOrWhiteSpace(hook) && single.Contains(hook.Trim()))
            || (bgm is not null && single.Contains(bgm.Trim()));

    /// <summary>채널 기준선(조회수 가중 · 실측 없음 → 사전값).</summary>
    private static double ChannelBaseline(IReadOnlyList<PlutoRenderedClipRecord>? clips)
    {
        double wSum = 0, wComp = 0;
        if (clips is not null)
            for (var i = 0; i < clips.Count; i++)
                if (TryMeasured(clips[i], out var comp, out var w)) { wSum += w; wComp += w * comp; }
        return wSum > 0 ? wComp / wSum : PriorComposite;
    }

    /// <summary>차원(1 = 룩 · 2 = BGM · 3 = 렌더 모드) 일치 클립의 기준선 대비 수축 리프트 = Σw(c − base) / (Σw + k).</summary>
    private static double Lift(IReadOnlyList<PlutoRenderedClipRecord>? clips, int dim, string? value, double baseline, out int samples)
    {
        samples = 0;
        if (clips is null || string.IsNullOrWhiteSpace(value)) return 0.0;
        var key = dim == 1 ? (PlutoColorGradingCompiler.TryGetPreset(value)?.PresetName ?? value.Trim()) : PlutoRenderCatalogService.NormalizeKey(value);
        double w = 0, acc = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (!TryMeasured(c, out var comp, out var cw)) continue;
            var match = dim switch
            {
                1 => string.Equals(PlutoColorGradingCompiler.TryGetPreset(c!.ColorLookId)?.PresetName ?? c.ColorLookId?.Trim(), key, StringComparison.OrdinalIgnoreCase),
                2 => string.Equals(PlutoRenderCatalogService.NormalizeKey(c!.BgmMood), key, StringComparison.Ordinal),
                _ => string.Equals(PlutoRenderCatalogService.NormalizeKey(c!.RenderMode), key, StringComparison.Ordinal),
            };
            if (!match) continue;
            w += cw;
            acc += cw * (comp - baseline);
            samples++;
        }
        return w > 0 ? acc / (w + ShrinkStrength) : 0.0;
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

    /// <summary>🎯 Step 29 — 수집 직후 스윕(카탈로그 변경 디바운스를 기다리지 않음 · 예측 캐시도 함께 갱신).</summary>
    public Task<PlutoBriefSweepResult> EvaluatePendingBriefOutcomesAndRehabilitationAsync(CancellationToken ct = default) => SweepAsync(ct);

    public async Task<PlutoBriefSweepResult> SweepAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return PlutoBriefSweepResult.Empty;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        List<PlutoBriefVerdict>? verdicts = null;
        List<string>? rehabilitated = null;
        int hits = 0, postMortems = 0;
        try
        {
            var clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false);
            Volatile.Write(ref _clipCache, clips);   // 🔮 렌더 전 예측 스냅샷
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
                            await _notebook.RecordPostMortemMistakeAsync(keys, PostMortemPenalty, dimension,
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
            if (_notebook is not null) rehabilitated = await RehabilitateAsync(clips, verdicts, postMortemDecisions, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (verdicts is null && rehabilitated is null) return PlutoBriefSweepResult.Empty;
        var result = verdicts is null
            ? PlutoBriefSweepResult.Empty with { RehabilitatedKeys = rehabilitated! }
            : new PlutoBriefSweepResult(verdicts.Count, hits, postMortems, verdicts) { RehabilitatedKeys = (IReadOnlyList<string>?)rehabilitated ?? Array.Empty<string>() };
        Volatile.Write(ref _last, result);
        try { Changed?.Invoke(); }
        catch (Exception ex) { Program.Log("Pluto.BriefOutcome.Changed", ex); }
        return result;
    }

    /// <summary>
    /// 🌱 유기적 복권 패스(<see cref="_gate"/> 보유) — 조회 ≥ 50 · SurpriseDelta ≥ +8 클립의 조합 키 중 벌점이 남은 것만 노트에 전달
    /// (클립당 1회 · 벌점 이후 렌더 여부는 노트가 판정). 개별 실패 격리.
    /// </summary>
    private async Task<List<string>?> RehabilitateAsync(IReadOnlyList<PlutoRenderedClipRecord> clips, List<PlutoBriefVerdict>? verdicts, HashSet<string> postMortemDecisions, CancellationToken ct)
    {
        List<string>? result = null;
        for (var i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = clips[i];
            if (c is null) continue;
            if (c.HasAppliedBrief && postMortemDecisions.Contains(DecisionKeyOf(c))) continue;   // 🧮 사후 분석된 기획 결정의 형제 편은 그 판정을 뒤집지 못함
            double? graded = null;
            if (verdicts is not null)
                for (var j = 0; j < verdicts.Count; j++)
                    if (string.Equals(verdicts[j].ClipVideoId, c.VideoId, StringComparison.OrdinalIgnoreCase)) { graded = verdicts[j].SurpriseDelta; break; }
            if (OrganicSurpriseOf(c, clips, graded) is not { } delta || delta < PlutoMistakeNotebookService.RehabilitationDeltaPoints) continue;
            var keys = PostMortemKeysOf(c);
            var penalized = false;
            for (var k = 0; k < keys.Count && !penalized; k++) penalized = _notebook!.GetEffectivePenalty(keys[k]) >= PlutoMistakeNotebookService.RecoveringFloor;
            if (!penalized) continue;
            try
            {
                var r = await _notebook!.RecordOrganicRecoveryAsync(keys, c.VideoId, delta, c.RenderedAtUtc.UtcDateTime, ct).ConfigureAwait(false);
                if (r.Any) (result ??= new List<string>(2)).AddRange(r.AttenuatedKeys);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.BriefOutcome.Rehabilitate", ex); }   // [부분 구제] 다음 클립 계속
        }
        return result;
    }

    public PlutoSetupForecast EvaluateSetupForecast(string? theme, string? hook, string? look, string? bgm, string? renderMode)
    {
        try { return EvaluateSetupForecast(theme, hook, look, bgm, renderMode, Volatile.Read(ref _clipCache), _notebook); }
        catch (Exception ex) { Program.Log("Pluto.BriefOutcome.Forecast", ex); return PlutoSetupForecast.Prior; }   // [부분 구제]
    }

    public async Task RefreshForecastCacheAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _clipCache, await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false));
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
