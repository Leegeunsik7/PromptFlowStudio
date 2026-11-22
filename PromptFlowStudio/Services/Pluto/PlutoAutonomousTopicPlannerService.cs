using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎯 Step 26 — 다음 제작 기획안 종류(학습 키 · UI data 속성).</summary>
public static class PlutoTopicBriefKinds
{
    /// <summary>📺 미완 시리즈 다음 편 이어가기(최우선).</summary>
    public const string SeriesContinuation = "series-continuation";
    /// <summary>🆕 지친/방치된 시리즈 → 새 시즌으로 재시작(같은 구조로 끝까지 밀지 않음).</summary>
    public const string SeriesReboot = "series-reboot";
    /// <summary>⚖️ 검증된 파레토 승자 활용.</summary>
    public const string ParetoExploit = "pareto-exploit";
    /// <summary>🎰 덜 검증된 고상한(UCB) 조합 탐색.</summary>
    public const string UpsideExplore = "upside-explore";
    /// <summary>🧬 콜드스타트 — 계층적 경험적 베이즈 사전.</summary>
    public const string ColdStartPrior = "cold-start-prior";
}

/// <summary>🎯 Step 26 — 다음 제작 기획안 1건(테마 · 형식 · 4축 패턴 · 제목 프레이밍 · 근거).</summary>
public sealed record PlutoTopicBrief(int Rank, string BriefId, string Kind, string Theme, string Angle, string Format, int TotalEpisodes, int NextEpisodeNumber,
    string HookStyle, string ColorLookId, string BgmMood, IReadOnlyList<string> StyleTags, string TitleFraming, bool TitleFramingEvidenced, double Score, string RationaleKorean)
{
    /// <summary>시리즈 기획(2편 이상).</summary>
    public bool IsSeries => TotalEpisodes >= 2;
    /// <summary>콜드스타트 사전 기반(실측 근거 부족).</summary>
    public bool IsColdStart { get; init; }
    /// <summary>이어가기 · 재시작 대상 시리즈 키(그 밖 → 빈 문자열).</summary>
    public string SeriesKey { get; init; } = string.Empty;
}

/// <summary>🎯 Step 26 — 기획 합성 입력(순수 · 결정론 · 테스트 가능).</summary>
public sealed record PlutoTopicPlannerInputs
{
    public IReadOnlyList<PlutoRankedOption> Hooks { get; init; } = Array.Empty<PlutoRankedOption>();
    public IReadOnlyList<PlutoRankedOption> Looks { get; init; } = Array.Empty<PlutoRankedOption>();
    public IReadOnlyList<PlutoRankedOption> BgmMoods { get; init; } = Array.Empty<PlutoRankedOption>();
    public IReadOnlyList<PlutoRankedOption> StyleTags { get; init; } = Array.Empty<PlutoRankedOption>();
    public IReadOnlyList<PlutoTitleFramingStat> FramingRanking { get; init; } = Array.Empty<PlutoTitleFramingStat>();
    public IReadOnlyList<PlutoSeriesArcReport> SeriesArcs { get; init; } = Array.Empty<PlutoSeriesArcReport>();
    public IReadOnlySet<string>? Suppressed { get; init; }
    public IReadOnlyList<PlutoRenderedClipRecord> RecentClips { get; init; } = Array.Empty<PlutoRenderedClipRecord>();
    public string? SeedTheme { get; init; }
    public DateTimeOffset NowUtc { get; init; }
    public int MaxBriefs { get; init; } = PlutoAutonomousTopicPlannerService.DefaultMaxBriefs;
}

/// <summary>📺 Step 26 — 두뇌 리포트 '실측 유튜브 성과 요약'(연결 영상 · 조회수 가중 CTR/유지율 · 선두 제목 프레이밍).</summary>
public sealed record PlutoRealizedYouTubeSummary(int LinkedVideoCount, int MeasuredVideoCount, long TotalViews, double WeightedCtrPct, double WeightedHookPct,
    double WeightedCompletionPct, string LeadingFraming, int LeadingFramingSamples, double LeadingFramingCtrPct)
{
    public static readonly PlutoRealizedYouTubeSummary Empty = new(0, 0, 0, 0, 0, 0, string.Empty, 0, 0);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>조회수 가중 실측 지표 있음.</summary>
    public bool HasRealizedData => MeasuredVideoCount > 0;
    /// <summary>귀속 표본 ≥ <see cref="PlutoYouTubePublishPackService.MinAttributedSamples"/> 인 선두 프레이밍 있음.</summary>
    public bool HasFramingLeader => LeadingFraming.Length > 0;

    /// <summary>
    /// 순수 집계 — YouTube ID 연결 클립(중복 ID 1회) · 워터마크 조회수 &gt; 0 · 지표 유한 범위만.
    /// 가중치 = min(1, 조회수 / <see cref="PlutoYouTubePublishPackService.FullWeightViews"/>) → 바이럴 1편이 평균을 독점하지 못함.
    /// 선두 프레이밍 = <see cref="PlutoYouTubePublishPackService.ComputeFramingStats"/> 의 축소 CTR 최대(합성 프레이밍 귀속 표본 합 ≥ 2일 때만).
    /// </summary>
    public static PlutoRealizedYouTubeSummary Compute(IReadOnlyList<PlutoRenderedClipRecord>? clips)
    {
        if (clips is null || clips.Count == 0) return Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int linked = 0, measured = 0;
        long views = 0;
        double wSum = 0, wCtr = 0, wHook = 0, wComp = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (c is null || string.IsNullOrWhiteSpace(c.YouTubeVideoId) || !seen.Add(c.YouTubeVideoId)) continue;
            linked++;
            if (c.YouTubeWatermark is not { } wm || wm.Views <= 0) continue;
            if (!Valid(wm.CtrPct) || !Valid(wm.HookPct) || !Valid(wm.CompletionPct)) continue;
            measured++;
            views += wm.Views;
            var w = Math.Min(1.0, (double)wm.Views / PlutoYouTubePublishPackService.FullWeightViews);
            wSum += w;
            wCtr += w * wm.CtrPct;
            wHook += w * wm.HookPct;
            wComp += w * wm.CompletionPct;
        }
        string lead = string.Empty;
        int leadN = 0;
        double leadCtr = 0;
        var stats = PlutoYouTubePublishPackService.ComputeFramingStats(clips);
        var total = 0;
        foreach (var s in stats) if (PlutoTitleFramings.IsComposable(s.Framing)) total += s.Samples;
        if (total >= PlutoYouTubePublishPackService.MinAttributedSamples)
        {
            foreach (var s in stats)
            {
                if (!PlutoTitleFramings.IsComposable(s.Framing) || s.Samples <= 0) continue;
                if (lead.Length == 0 || s.ShrunkCtrPct > leadCtr + PlutoAdaptivePatternRanker.Epsilon) { lead = s.Framing; leadN = s.Samples; leadCtr = s.ShrunkCtrPct; }
            }
        }
        return wSum <= 0
            ? new PlutoRealizedYouTubeSummary(linked, 0, 0, 0, 0, 0, lead, leadN, leadCtr)
            : new PlutoRealizedYouTubeSummary(linked, measured, views, wCtr / wSum, wHook / wSum, wComp / wSum, lead, leadN, leadCtr);
    }

    private static bool Valid(double v) => double.IsFinite(v) && v >= 0 && v <= 100;

    /// <summary>1줄(결정론 한국어) — 실측 없음 → 수집 안내 · 프레이밍 근거 부족 → 명시(수치 날조 금지).</summary>
    public string ToLabel()
    {
        if (LinkedVideoCount == 0) return "연결 영상 없음 · YouTube Studio 내보내기를 가져오면 실측 CTR · 유지율을 요약해요";
        if (!HasRealizedData) return "연결 " + LinkedVideoCount.ToString(Inv) + "편 · 실측 지표 수집 대기";
        var s = "연결 " + LinkedVideoCount.ToString(Inv) + "편(실측 " + MeasuredVideoCount.ToString(Inv) + ") · 조회 " + TotalViews.ToString(Inv)
            + " · CTR " + WeightedCtrPct.ToString("0.0", Inv) + "% · 3초 " + WeightedHookPct.ToString("0", Inv) + "% · 완주 " + WeightedCompletionPct.ToString("0", Inv) + "%(조회수 가중)";
        return HasFramingLeader
            ? s + " · 선두 제목 " + LeadingFraming + "(CTR " + LeadingFramingCtrPct.ToString("0.0", Inv) + "% · n=" + LeadingFramingSamples.ToString(Inv) + ")"
            : s + " · 선두 제목 근거 부족(귀속 " + PlutoYouTubePublishPackService.MinAttributedSamples.ToString(Inv) + "건부터)";
    }
}

/// <summary>🎯 Step 26 — 자율 렌더 전 주제 · 시리즈 기획기.</summary>
public interface IPlutoAutonomousTopicPlannerService
{
    /// <summary>랭커 · 실측 프레이밍 · 시리즈 아크 · 오답 노트 · 렌더 이력 → 순위 기획안 최대 <paramref name="maxBriefs"/>건(소스 실패 → [부분 구제] · 취소만 전파).</summary>
    Task<IReadOnlyList<PlutoTopicBrief>> ProposeAsync(string? seedTheme = null, int maxBriefs = PlutoAutonomousTopicPlannerService.DefaultMaxBriefs, CancellationToken ct = default);
}

/// <summary>
/// 🎯 <see cref="IPlutoAutonomousTopicPlannerService"/> 구현(순수 BCL · 결정론 · 수치 날조 없는 근거).
///   ▸ 📺 미완 시리즈 이어가기 최우선(앵커 룩/BGM · 이탈 시 궁금증 훅) — 단, 🧯 지친 아크(이탈 단계 3 ∧ 측정 2편+) · 방치 아크(<see cref="StaleSeriesDays"/>일+)는 새 시즌 재시작으로 전환.
///   ▸ ⚖️ 활용 = 파레토/사후 점수 1위 · 🎰 탐색 = UCB 상한 1위(다른 훅 · 2위 제목 프레이밍으로 A/B 학습) · 🧬 시행 &lt; <see cref="ColdStartTrials"/> → 경험적 베이즈 사전 표기.
///   ▸ 🛡️ 오답 노트 차단 · 감점 상한 · 클릭베이트 급등 후보는 전 축에서 제외.
///   ▸ 🔁 주제 피로 가드 — 최근 렌더 <see cref="FatigueWindow"/>건의 훅 · 룩 · BGM · 테마 반복을 기하 감쇠(½ⁱ)로 감점(이어가기 테마는 면제).
///   ▸ 🧩 포트폴리오 다양성 — 기획안끼리 훅이 겹치지 않고 (룩 · BGM) 중 하나 이상 달라야 함(복제 3건 방지).
///   ▸ 🔬 근거 정직성 — 표본 임계 미달 신호는 수치를 인용하지 않고 '사전'으로 표기.
/// </summary>
public sealed class PlutoAutonomousTopicPlannerService : IPlutoAutonomousTopicPlannerService
{
    public const int DefaultMaxBriefs = 3;
    public const int MaxBriefsCap = 5;
    /// <summary>피로 가드 대상 최근 렌더 수.</summary>
    public const int FatigueWindow = 6;
    /// <summary>훅 반복 1회(가장 최근) 감점 — 누적 ½ⁱ 가중 · 상한 <see cref="MaxFatiguePenalty"/>.</summary>
    public const double HookFatigueWeight = 0.4;
    public const double LookFatigueWeight = 0.15;
    public const double ThemeFatigueWeight = 0.5;
    public const double MaxFatiguePenalty = 0.6;
    /// <summary>테마 쿨다운 — 최근 n건에 쓴 테마는 새 기획 테마로 재사용하지 않음(대안 없을 때만 각도 전환 표기).</summary>
    public const int ThemeCooldown = 2;
    /// <summary>훅 누적 시행이 이 값 미만이면 콜드스타트(경험적 베이즈 사전).</summary>
    public const double ColdStartTrials = 3.0;
    /// <summary>이 일수 이상 갱신 없는 미완 시리즈는 이어가기 대신 새 시즌 재시작 권고.</summary>
    public const double StaleSeriesDays = 45.0;
    public const int MaxStyleTags = 3;
    public const int MaxSeriesArcs = 8;
    public const int TrilogyEpisodes = 3;
    public const string FormatSingle = "단편";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPlutoAdaptivePatternRanker? _ranker;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoRenderCatalogService? _catalog;

    /// <summary>📺 시리즈 아크 메모리(미연결 → 이어가기 없음).</summary>
    public IPlutoSeriesArcMemoryService? SeriesArc { get; init; }
    /// <summary>🅰️🅱️ 실측 제목 프레이밍 순위(미연결 → 카탈로그에서 직접 집계).</summary>
    public IPlutoYouTubePublishPackService? PublishPack { get; init; }
    /// <summary>테스트용 시계(기본 = UTC 현재).</summary>
    public Func<DateTimeOffset>? UtcNowProvider { get; init; }

    public PlutoAutonomousTopicPlannerService(IPlutoAdaptivePatternRanker? ranker, IPlutoMistakeNotebookService? notebook, IPlutoRenderCatalogService? catalog)
    {
        _ranker = ranker;
        _notebook = notebook;
        _catalog = catalog;
    }

    public static string FormatFor(int totalEpisodes) => totalEpisodes >= 2 ? "시리즈 " + totalEpisodes.ToString(Inv) + "부작" : FormatSingle;

    public async Task<IReadOnlyList<PlutoTopicBrief>> ProposeAsync(string? seedTheme = null, int maxBriefs = DefaultMaxBriefs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<PlutoRankedOption> hooks = Array.Empty<PlutoRankedOption>(), looks = hooks, bgms = hooks, tags = hooks;
        IReadOnlyList<PlutoRenderedClipRecord> clips = Array.Empty<PlutoRenderedClipRecord>();
        IReadOnlyList<PlutoTitleFramingStat> framing = Array.Empty<PlutoTitleFramingStat>();
        IReadOnlyList<PlutoSeriesArcReport> arcs = Array.Empty<PlutoSeriesArcReport>();
        IReadOnlySet<string>? suppressed = null;

        if (_notebook is not null)
        {
            try { await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false); suppressed = _notebook.GetSuppressedTokens(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.TopicPlanner.Notebook", ex); }   // [부분 구제]
        }
        if (_ranker is not null)
        {
            try
            {
                await _ranker.EnsureLoadedAsync(ct).ConfigureAwait(false);
                var domain = string.IsNullOrWhiteSpace(seedTheme) ? null : PromptDomainClassifier.Classify(seedTheme).ToString();
                hooks = RankSafe(PlutoPatternKind.Hook, domain);
                looks = RankSafe(PlutoPatternKind.Look, domain);
                bgms = RankSafe(PlutoPatternKind.BgmMood, domain);
                tags = RankSafe(PlutoPatternKind.StyleTag, domain);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.TopicPlanner.Ranker", ex); }
        }
        if (_catalog is not null)
        {
            try { clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.TopicPlanner.Catalog", ex); }
        }
        try
        {
            if (PublishPack is not null)
            {
                if (clips.Count > 0) PublishPack.RefreshFramingEvidence(clips);
                framing = PublishPack.GetFramingRanking();
            }
            else framing = OrderFraming(PlutoYouTubePublishPackService.ComputeFramingStats(clips));
        }
        catch (Exception ex) { Program.Log("Pluto.TopicPlanner.Framing", ex); }
        if (SeriesArc is not null)
        {
            try
            {
                await SeriesArc.EnsureLoadedAsync(ct).ConfigureAwait(false);
                await SeriesArc.SyncFromCatalogAsync(ct).ConfigureAwait(false);
                arcs = SeriesArc.GetSeriesArcs(MaxSeriesArcs);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("Pluto.TopicPlanner.SeriesArc", ex); }
        }

        ct.ThrowIfCancellationRequested();
        return Synthesize(new PlutoTopicPlannerInputs
        {
            Hooks = hooks, Looks = looks, BgmMoods = bgms, StyleTags = tags, FramingRanking = framing, SeriesArcs = arcs,
            Suppressed = suppressed, RecentClips = clips, SeedTheme = seedTheme, NowUtc = UtcNowProvider?.Invoke() ?? DateTimeOffset.UtcNow, MaxBriefs = maxBriefs,
        });

        IReadOnlyList<PlutoRankedOption> RankSafe(PlutoPatternKind kind, string? domain)
        {
            try { return (domain is null ? _ranker!.RankOptions(kind) : _ranker!.RankForContext(kind, domain, null)) ?? Array.Empty<PlutoRankedOption>(); }
            catch (Exception ex) { Program.Log("Pluto.TopicPlanner.Rank." + kind.ToString(), ex); return Array.Empty<PlutoRankedOption>(); }
        }
    }

    /// <summary>프레이밍 통계 → 합성 프레이밍 순서(귀속 표본 ≥ 2 → 축소 CTR 내림차순 · 그 밖 기본 순서).</summary>
    public static IReadOnlyList<PlutoTitleFramingStat> OrderFraming(IReadOnlyList<PlutoTitleFramingStat>? stats)
    {
        var list = new List<PlutoTitleFramingStat>(PlutoTitleFramings.DefaultOrder.Count);
        var total = 0;
        foreach (var d in PlutoTitleFramings.DefaultOrder)
        {
            PlutoTitleFramingStat? found = null;
            if (stats is not null) foreach (var s in stats) if (s is not null && s.Framing == d) { found = s; break; }
            found ??= new PlutoTitleFramingStat(d, 0, 0, 0, 0);
            total += found.Samples;
            list.Add(found);
        }
        if (total >= PlutoYouTubePublishPackService.MinAttributedSamples)
            list.Sort(static (a, b) =>
            {
                var c = b.ShrunkCtrPct.CompareTo(a.ShrunkCtrPct);
                return c != 0 ? c : DefaultIndex(a.Framing).CompareTo(DefaultIndex(b.Framing));
            });
        return list;
    }

    private static int DefaultIndex(string f)
    {
        for (var i = 0; i < PlutoTitleFramings.DefaultOrder.Count; i++) if (PlutoTitleFramings.DefaultOrder[i] == f) return i;
        return int.MaxValue;
    }

    /// <summary>🛡️ 오답 노트 차단 · 감점 상한 · 클릭베이트 급등 → 기획 후보 제외.</summary>
    public static bool IsBlocked(PlutoRankedOption? o, IReadOnlySet<string>? suppressed)
    {
        if (o is null || string.IsNullOrWhiteSpace(o.DisplayLabel)) return true;
        if (suppressed is { Count: > 0 } && (suppressed.Contains(o.DisplayLabel) || suppressed.Contains(o.Key))) return true;
        return o.MistakePenaltyFactor >= PlutoAdaptivePatternRanker.MaxPenaltyFactor - PlutoAdaptivePatternRanker.Epsilon || o.IsClickbaitSpike;
    }

    private static bool IsBlockedLabel(string? label, IReadOnlySet<string>? suppressed)
        => string.IsNullOrWhiteSpace(label) || (suppressed is { Count: > 0 } && suppressed.Contains(label));

    /// <summary>🔁 최근 렌더(최신 → 과거) 중 값 일치 피로도 = Σ ½ⁱ(i = 최신부터 0) · 창 <see cref="FatigueWindow"/>건.</summary>
    public static double FatigueOf(IReadOnlyList<PlutoRenderedClipRecord> recentNewestFirst, Func<PlutoRenderedClipRecord, string?> selector, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || recentNewestFirst.Count == 0) return 0.0;
        var key = PlutoRenderCatalogService.NormalizeKey(value);
        if (key.Length == 0) return 0.0;
        var f = 0.0;
        var weight = 1.0;
        var n = Math.Min(FatigueWindow, recentNewestFirst.Count);
        for (var i = 0; i < n; i++, weight *= 0.5)
            if (string.Equals(PlutoRenderCatalogService.NormalizeKey(selector(recentNewestFirst[i])), key, StringComparison.Ordinal)) f += weight;
        return f;
    }

    /// <summary>테마 키(“n/N편” 표기 제거 → 정규화).</summary>
    public static string ThemeKeyOf(string? titleOrTheme) => PlutoRenderCatalogService.NormalizeKey(PlutoSeriesArcMemoryService.SeriesThemeOf(titleOrTheme));

    /// <summary>🎯 순수 합성(결정론) — 이어가기/재시작 · 활용 · 탐색 · 보충 후보 → 다양성 가드 → 점수순 최대 N건.</summary>
    public static IReadOnlyList<PlutoTopicBrief> Synthesize(PlutoTopicPlannerInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var max = Math.Clamp(inputs.MaxBriefs, 0, MaxBriefsCap);
        if (max == 0) return Array.Empty<PlutoTopicBrief>();
        var suppressed = inputs.Suppressed;
        var recent = NewestFirst(inputs.RecentClips);
        var hooks = Allowed(inputs.Hooks, suppressed);
        var looks = Allowed(inputs.Looks, suppressed);
        var bgms = Allowed(inputs.BgmMoods, suppressed);
        var tags = Allowed(inputs.StyleTags, suppressed);
        var blockedCount = CountBlocked(inputs.Hooks, suppressed) + CountBlocked(inputs.Looks, suppressed) + CountBlocked(inputs.BgmMoods, suppressed) + CountBlocked(inputs.StyleTags, suppressed);
        var hookTrials = 0.0;
        foreach (var h in inputs.Hooks) if (h is not null && double.IsFinite(h.ArmTrials) && h.ArmTrials > 0) hookTrials += h.ArmTrials;
        var coldStart = hookTrials < ColdStartTrials;
        var framing = inputs.FramingRanking is { Count: > 0 } fr ? fr : OrderFraming(null);
        var framingTotal = 0;
        foreach (var s in framing) if (PlutoTitleFramings.IsComposable(s.Framing)) framingTotal += s.Samples;
        var framingEvidenced = framingTotal >= PlutoYouTubePublishPackService.MinAttributedSamples;
        var topTags = new List<string>(MaxStyleTags);
        for (var i = 0; i < tags.Count && topTags.Count < MaxStyleTags; i++) topTags.Add(tags[i].DisplayLabel);

        var candidates = new List<PlutoTopicBrief>(8);
        var continuationThemes = new HashSet<string>(StringComparer.Ordinal);

        // ── 📺 시리즈 이어가기 / 🧯 재시작 ──
        foreach (var arc in inputs.SeriesArcs)
        {
            if (arc is null || arc.TotalEpisodes < 2) continue;
            var done = 0;
            foreach (var e in arc.Episodes) if (e is not null && e.EpisodeNumber > done) done = e.EpisodeNumber;
            if (done <= 0 || done >= arc.TotalEpisodes) continue;   // 미시작 · 완결 → 대상 아님
            var theme = SeriesDisplayTheme(arc);
            var ageDays = inputs.NowUtc > arc.UpdatedAtUtc ? (inputs.NowUtc - arc.UpdatedAtUtc).TotalDays : 0.0;
            var fatigued = arc.HasDropOff && arc.EscalationLevel >= PlutoSeriesArcMemoryService.MaxEscalationLevel && arc.MeasuredCount >= 2;
            var stale = ageDays >= StaleSeriesDays;
            if (fatigued || stale)
            {
                PickHook(hooks, recent, exclude: null, arcHook: LastHookOf(arc), out var freshHook);
                var reason = fatigued
                    ? "🧯 '" + theme + "' 편 간 유지율 붕괴(이탈 단계 " + arc.EscalationLevel.ToString(Inv) + " · 측정 " + arc.MeasuredCount.ToString(Inv) + "편) — 같은 구조로 " + (done + 1).ToString(Inv) + "편을 밀지 말고 새 시즌 재시작"
                    : "🧯 '" + theme + "' " + ageDays.ToString("0", Inv) + "일 방치 — 시청자 맥락이 끊겨 " + (done + 1).ToString(Inv) + "편 대신 새 시즌으로 재시작";
                var look = PickByFatigue(looks, recent, static c => c.ColorLookId, null);
                var bgm = PickByFatigue(bgms, recent, static c => c.BgmMood, null);
                candidates.Add(Make(PlutoTopicBriefKinds.SeriesReboot, theme, "새 시즌 · 새 오프닝 훅", TrilogyEpisodes, 1, freshHook?.DisplayLabel ?? "", look?.DisplayLabel ?? "", bgm?.DisplayLabel ?? "",
                    topTags, framing, 0, framingEvidenced, 2.0 - Math.Min(0.3, ageDays / 1000.0), coldStart, blockedCount, reason, freshHook, arc.SeriesKey));
                continuationThemes.Add(ThemeKeyOf(theme));
                continue;
            }
            // 이어가기: 앵커 룩/BGM(차단 → 대체) · 이탈 → 궁금증 훅(차단 → 활용 1위)
            var anchorLook = !IsBlockedLabel(arc.AnchorColorLookId, suppressed) ? arc.AnchorColorLookId : looks.Count > 0 ? looks[0].DisplayLabel : "";
            var anchorBgm = !IsBlockedLabel(arc.AnchorBgmMood, suppressed) ? arc.AnchorBgmMood : bgms.Count > 0 ? bgms[0].DisplayLabel : "";
            string hookLine;
            PlutoRankedOption? hookOpt = null;
            if (arc.HasDropOff && arc.EscalationLevel > 0)
            {
                var gaps = PlutoSeriesArcMemoryService.CuriosityGapHooks;
                hookLine = gaps[Math.Clamp(arc.EscalationLevel - 1, 0, gaps.Count - 1)];
                if (IsBlockedLabel(hookLine, suppressed)) { PickHook(hooks, recent, null, null, out hookOpt); hookLine = hookOpt?.DisplayLabel ?? ""; }
            }
            else
            {
                PickHook(hooks, recent, null, null, out hookOpt);
                hookLine = hookOpt?.DisplayLabel ?? "";
            }
            var sb = new StringBuilder(128);
            sb.Append("📺 '").Append(theme).Append("' ").Append(done.ToString(Inv)).Append('/').Append(arc.TotalEpisodes.ToString(Inv)).Append("편까지 제작 · 미완 → ").Append((done + 1).ToString(Inv)).Append("편 우선");
            if (arc.MeasuredCount >= 2) sb.Append(" · 편 간 유지 변화 ").Append(arc.EpisodeRetentionDelta.ToString("+0.0;-0.0;0.0", Inv)).Append('p');
            if (arc.HasDropOff) sb.Append(" · 이탈 단계 ").Append(arc.EscalationLevel.ToString(Inv)).Append(" → 궁금증 훅");
            if (arc.HasAnchor) sb.Append(" · 앵커 룩/BGM 유지");
            candidates.Add(Make(PlutoTopicBriefKinds.SeriesContinuation, theme, (done + 1).ToString(Inv) + "편 이어가기", arc.TotalEpisodes, done + 1, hookLine, anchorLook, anchorBgm,
                topTags, framing, 0, framingEvidenced, 3.0 - Math.Min(0.5, ageDays / 100.0), coldStart, blockedCount, sb.ToString(), hookOpt, arc.SeriesKey));
            continuationThemes.Add(ThemeKeyOf(theme));
        }

        // ── ⚖️ 활용(또는 🧬 콜드스타트 사전) ──
        var exploitHook = PickHook(hooks, recent, null, null, out var exploitOpt);
        var hasContinuation = false;
        foreach (var c in candidates) if (c.Kind == PlutoTopicBriefKinds.SeriesContinuation) { hasContinuation = true; break; }
        if (exploitOpt is not null)
        {
            var proven = !coldStart && (exploitOpt.Posterior.IsProvenWinner || exploitOpt.IsParetoFrontier);
            var total = proven && !hasContinuation ? TrilogyEpisodes : 1;
            var look = PickByFatigue(looks, recent, static c => c.ColorLookId, null);
            var bgm = PickByFatigue(bgms, recent, static c => c.BgmMood, null);
            var kind = coldStart ? PlutoTopicBriefKinds.ColdStartPrior : PlutoTopicBriefKinds.ParetoExploit;
            var fatigueNote = hooks.Count > 0 && !string.Equals(hooks[0].DisplayLabel, exploitOpt.DisplayLabel, StringComparison.Ordinal)
                ? "🔁 최근 반복 훅 '" + hooks[0].DisplayLabel + "' 피로 회피" : null;
            candidates.Add(Make(kind, "", proven ? "검증된 패턴을 시리즈로 확장" : "현재 1위 패턴 집중", total, total >= 2 ? 1 : 0, exploitOpt.DisplayLabel, look?.DisplayLabel ?? "", bgm?.DisplayLabel ?? "",
                topTags, framing, 0, framingEvidenced, exploitHook, coldStart, blockedCount, fatigueNote, exploitOpt, ""));
        }

        // ── 🎰 탐색(다른 훅 · UCB 상한 · 2위 프레이밍) ──
        PlutoRankedOption? exploreOpt = null;
        var exploreScore = double.NegativeInfinity;
        foreach (var h in hooks)
        {
            if (exploitOpt is not null && string.Equals(h.DisplayLabel, exploitOpt.DisplayLabel, StringComparison.Ordinal)) continue;
            var fat = Math.Min(MaxFatiguePenalty, HookFatigueWeight * FatigueOf(recent, static c => c.HookStyle, h.DisplayLabel));
            var s = (h.UcbScore + (h.IsParetoFrontier ? 0.05 : 0.0)) * (1.0 - fat);
            if (s > exploreScore + PlutoAdaptivePatternRanker.Epsilon) { exploreScore = s; exploreOpt = h; }
        }
        if (exploreOpt is not null)
        {
            var look = PickByFatigue(looks, recent, static c => c.ColorLookId, null, skip: 1);
            var bgm = PickByFatigue(bgms, recent, static c => c.BgmMood, null, skip: 1);
            candidates.Add(Make(PlutoTopicBriefKinds.UpsideExplore, "", "덜 검증된 고상한 조합 실험", 1, 0, exploreOpt.DisplayLabel, look?.DisplayLabel ?? "", bgm?.DisplayLabel ?? "",
                topTags, framing, framing.Count > 1 ? 1 : 0, framingEvidenced, 0.8 * exploreScore, coldStart, blockedCount, null, exploreOpt, ""));
        }

        // ── 보충(남은 훅 순서대로) ──
        foreach (var h in hooks)
        {
            if (candidates.Count >= max + 3) break;
            var used = false;
            foreach (var c in candidates) if (string.Equals(c.HookStyle, h.DisplayLabel, StringComparison.Ordinal)) { used = true; break; }
            if (used) continue;
            var fat = Math.Min(MaxFatiguePenalty, HookFatigueWeight * FatigueOf(recent, static c => c.HookStyle, h.DisplayLabel));
            var look = PickByFatigue(looks, recent, static c => c.ColorLookId, null, skip: 2);
            var bgm = PickByFatigue(bgms, recent, static c => c.BgmMood, null, skip: 2);
            candidates.Add(Make(coldStart ? PlutoTopicBriefKinds.ColdStartPrior : PlutoTopicBriefKinds.ParetoExploit, "", "대안 패턴", 1, 0, h.DisplayLabel, look?.DisplayLabel ?? "", bgm?.DisplayLabel ?? "",
                topTags, framing, 0, framingEvidenced, 0.7 * h.FinalScore * (1.0 - fat), coldStart, blockedCount, null, h, ""));
        }

        candidates.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.CompareOrdinal(a.BriefId, b.BriefId);
        });

        // ── 🧩 포트폴리오 다양성 가드 → 🔁 테마 배정(피로 · 쿨다운) → 순위 ──
        var selected = new List<PlutoTopicBrief>(max);
        foreach (var c in candidates)
        {
            if (selected.Count >= max) break;
            var diversified = Diversify(c, selected, looks, bgms);
            if (diversified is null) continue;
            selected.Add(diversified);
        }
        var themes = ThemePool(inputs.SeedTheme, recent, continuationThemes);
        var usedThemes = new HashSet<string>(continuationThemes, StringComparer.Ordinal);
        var lastThemeKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(ThemeCooldown, recent.Count); i++) lastThemeKeys.Add(ThemeKeyOf(recent[i].Title));
        var result = new List<PlutoTopicBrief>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var b = selected[i];
            if (b.Theme.Length == 0)
            {
                string? theme = null;
                foreach (var t in themes) { var k = ThemeKeyOf(t); if (k.Length > 0 && !usedThemes.Contains(k)) { theme = t; usedThemes.Add(k); break; } }
                var rationale = b.RationaleKorean;
                if (theme is null)
                {
                    var seed = string.IsNullOrWhiteSpace(inputs.SeedTheme) ? "" : inputs.SeedTheme.Trim();
                    theme = seed.Length > 0 ? seed + " — " + b.HookStyle + " 각도" : "새 주제 — " + (b.HookStyle.Length > 0 ? b.HookStyle : "자유") + " 각도";
                    if (seed.Length > 0 && lastThemeKeys.Contains(ThemeKeyOf(seed))) rationale += " · 🔁 최근 반복 테마 → 각도 전환";
                }
                b = b with { Theme = theme, RationaleKorean = rationale };
            }
            result.Add(b with { Rank = i + 1 });
        }
        return result;
    }

    /// <summary>🧩 이미 선택된 (비시리즈) 기획안과 훅이 겹치면 탈락 · (룩, BGM)이 모두 같으면 미사용 대안 룩 → BGM 순으로 교체(불가 → 탈락).</summary>
    public static PlutoTopicBrief? Diversify(PlutoTopicBrief candidate, IReadOnlyList<PlutoTopicBrief> selected, IReadOnlyList<PlutoRankedOption> looks, IReadOnlyList<PlutoRankedOption> bgms)
    {
        var b = candidate;
        foreach (var s in selected)
            if (b.HookStyle.Length > 0 && string.Equals(s.HookStyle, b.HookStyle, StringComparison.Ordinal)
                && b.SeriesKey.Length == 0 && s.SeriesKey.Length == 0) return null;   // 시리즈 기획은 테마가 달라 훅 공유 허용
        if (!Clashes(b, selected)) return b;
        foreach (var l in looks)
        {
            var alt = b with { ColorLookId = l.DisplayLabel };
            if (!Clashes(alt, selected)) return alt;
        }
        foreach (var m in bgms)
        {
            var alt = b with { BgmMood = m.DisplayLabel };
            if (!Clashes(alt, selected)) return alt;
        }
        return null;
    }

    private static bool Clashes(PlutoTopicBrief b, IReadOnlyList<PlutoTopicBrief> selected)
    {
        foreach (var s in selected)
            if (string.Equals(s.ColorLookId, b.ColorLookId, StringComparison.Ordinal) && string.Equals(s.BgmMood, b.BgmMood, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>🎯 1클릭 적용 프롬프트(테마 + 훅 · 룩 · BGM · 스타일 · 시리즈 편수) — 결정론.</summary>
    public static string BuildReadyPrompt(PlutoTopicBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var sb = new StringBuilder(160);
        sb.Append(brief.Theme.Trim());
        if (brief.HookStyle.Length > 0) sb.Append(", opening hook: ").Append(brief.HookStyle);
        if (brief.ColorLookId.Length > 0) sb.Append(", color look: ").Append(brief.ColorLookId);
        if (brief.BgmMood.Length > 0) sb.Append(", bgm mood: ").Append(brief.BgmMood);
        if (brief.StyleTags.Count > 0) sb.Append(", style: ").Append(string.Join(" ", brief.StyleTags));
        if (brief.IsSeries) sb.Append(", series ").Append(Math.Max(1, brief.NextEpisodeNumber).ToString(Inv)).Append('/').Append(brief.TotalEpisodes.ToString(Inv));
        return sb.ToString();
    }

    // ── 내부 ──

    private static List<PlutoRenderedClipRecord> NewestFirst(IReadOnlyList<PlutoRenderedClipRecord>? clips)
    {
        var list = new List<PlutoRenderedClipRecord>(clips?.Count ?? 0);
        if (clips is not null) foreach (var c in clips) if (c is not null) list.Add(c);
        list.Sort(static (a, b) =>
        {
            var c = b.RenderedAtUtc.CompareTo(a.RenderedAtUtc);
            return c != 0 ? c : string.CompareOrdinal(a.VideoId, b.VideoId);
        });
        return list;
    }

    private static List<PlutoRankedOption> Allowed(IReadOnlyList<PlutoRankedOption>? options, IReadOnlySet<string>? suppressed)
    {
        var list = new List<PlutoRankedOption>(options?.Count ?? 0);
        if (options is null) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in options) if (!IsBlocked(o, suppressed) && seen.Add(o.DisplayLabel)) list.Add(o);
        return list;
    }

    private static int CountBlocked(IReadOnlyList<PlutoRankedOption>? options, IReadOnlySet<string>? suppressed)
    {
        var n = 0;
        if (options is not null) foreach (var o in options) if (o is not null && !string.IsNullOrWhiteSpace(o.DisplayLabel) && IsBlocked(o, suppressed)) n++;
        return n;
    }

    /// <summary>피로 감점 반영 활용 1위 훅 — 반환 = 감점 후 점수(후보 없음 → 0).</summary>
    private static double PickHook(List<PlutoRankedOption> hooks, List<PlutoRenderedClipRecord> recent, string? exclude, string? arcHook, out PlutoRankedOption? best)
    {
        best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var h in hooks)
        {
            if (exclude is not null && string.Equals(h.DisplayLabel, exclude, StringComparison.Ordinal)) continue;
            var fat = Math.Min(MaxFatiguePenalty, HookFatigueWeight * FatigueOf(recent, static c => c.HookStyle, h.DisplayLabel));
            if (arcHook is not null && string.Equals(h.DisplayLabel, arcHook, StringComparison.Ordinal)) fat = MaxFatiguePenalty;   // 재시작은 지친 훅 회피
            var s = h.FinalScore * (1.0 - fat);
            if (s > bestScore + PlutoAdaptivePatternRanker.Epsilon) { bestScore = s; best = h; }
        }
        return best is null ? 0.0 : bestScore;
    }

    private static PlutoRankedOption? PickByFatigue(List<PlutoRankedOption> options, List<PlutoRenderedClipRecord> recent, Func<PlutoRenderedClipRecord, string?> selector, string? exclude, int skip = 0)
    {
        if (options.Count == 0) return null;
        var scored = new List<KeyValuePair<double, PlutoRankedOption>>(options.Count);
        foreach (var o in options)
        {
            if (exclude is not null && string.Equals(o.DisplayLabel, exclude, StringComparison.Ordinal)) continue;
            var fat = Math.Min(MaxFatiguePenalty, LookFatigueWeight * FatigueOf(recent, selector, o.DisplayLabel));
            scored.Add(new KeyValuePair<double, PlutoRankedOption>(o.FinalScore * (1.0 - fat), o));
        }
        if (scored.Count == 0) return null;
        scored.Sort(static (a, b) =>
        {
            var c = b.Key.CompareTo(a.Key);
            return c != 0 ? c : string.CompareOrdinal(a.Value.Key, b.Value.Key);
        });
        return scored[Math.Min(skip, scored.Count - 1)].Value;
    }

    private static string? LastHookOf(PlutoSeriesArcReport arc)
    {
        PlutoSeriesEpisodeStat? last = null;
        foreach (var e in arc.Episodes) if (e is not null && (last is null || e.EpisodeNumber > last.EpisodeNumber)) last = e;
        return string.IsNullOrWhiteSpace(last?.HookStyle) ? null : last!.HookStyle;
    }

    private static string SeriesDisplayTheme(PlutoSeriesArcReport arc)
    {
        var t = PlutoSeriesArcMemoryService.SeriesThemeOf(arc.SeriesTitle);
        if (t.Length > 0) return t;
        var k = arc.SeriesKey;
        var hash = k.IndexOf('#');
        return hash > 0 ? k[..hash] : k.Length > 0 ? k : "시리즈";
    }

    /// <summary>🔁 새 기획 테마 후보 — 시드 테마(쿨다운 밖) → 실측 성과 상위 과거 테마(쿨다운 · 이어가기 테마 제외).</summary>
    private static List<string> ThemePool(string? seedTheme, List<PlutoRenderedClipRecord> recent, HashSet<string> continuationThemes)
    {
        var cooldown = new HashSet<string>(continuationThemes, StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(ThemeCooldown, recent.Count); i++) { var k = ThemeKeyOf(recent[i].Title); if (k.Length > 0) cooldown.Add(k); }
        var pool = new List<string>(8);
        var seen = new HashSet<string>(cooldown, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(seedTheme))
        {
            var s = seedTheme.Trim();
            if (s.Length > 60) s = s[..60];
            if (seen.Add(ThemeKeyOf(s))) pool.Add(s);
        }
        var past = new List<KeyValuePair<double, string>>(recent.Count);
        var pastSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in recent)
        {
            var theme = PlutoSeriesArcMemoryService.SeriesThemeOf(c.Title);
            var k = ThemeKeyOf(theme);
            if (k.Length == 0 || seen.Contains(k) || !pastSeen.Add(k)) continue;
            var score = c.YouTubeWatermark is { } wm && double.IsFinite(wm.CtrPct) && double.IsFinite(wm.CompletionPct) ? wm.CtrPct + wm.CompletionPct / 10.0 : 0.0;
            past.Add(new KeyValuePair<double, string>(score, theme));
        }
        past.Sort(static (a, b) =>
        {
            var c = b.Key.CompareTo(a.Key);
            return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
        });
        foreach (var p in past) pool.Add(p.Value);
        return pool;
    }

    private static PlutoTopicBrief Make(string kind, string theme, string angle, int total, int next, string hook, string look, string bgm, IReadOnlyList<string> tags,
        IReadOnlyList<PlutoTitleFramingStat> framing, int framingIndex, bool framingEvidenced, double score, bool coldStart, int blockedCount, string? leadReason,
        PlutoRankedOption? hookOpt, string seriesKey)
    {
        var f = framing.Count > 0 ? framing[Math.Clamp(framingIndex, 0, framing.Count - 1)] : new PlutoTitleFramingStat(PlutoTitleFramings.OutcomeProof, 0, 0, 0, 0);
        var rationale = BuildRationale(kind, leadReason, hookOpt, f, framingEvidenced, framingIndex, coldStart, blockedCount);
        var id = BriefIdOf(kind, theme, hook, look, bgm, seriesKey);
        return new PlutoTopicBrief(0, id, kind, theme, angle, FormatFor(total), total, next, hook, look, bgm, tags, f.Framing, framingEvidenced && f.Samples > 0, score, rationale)
        {
            IsColdStart = coldStart,
            SeriesKey = seriesKey,
        };
    }

    /// <summary>🔬 근거 정직성 — 표본 임계 충족 신호만 수치 인용 · 미달 → '사전' 표기.</summary>
    public static string BuildRationale(string kind, string? leadReason, PlutoRankedOption? hook, PlutoTitleFramingStat framing, bool framingEvidenced, int framingIndex,
        bool coldStart, int blockedCount)
    {
        var sb = new StringBuilder(192);
        if (!string.IsNullOrEmpty(leadReason)) sb.Append(leadReason);
        if (hook is not null && kind is not PlutoTopicBriefKinds.SeriesContinuation and not PlutoTopicBriefKinds.SeriesReboot)
        {
            if (sb.Length > 0) sb.Append(" · ");
            if (coldStart) sb.Append("🧬 콜드스타트 — 훅 '").Append(hook.DisplayLabel).Append("' 계층적 경험적 베이즈 사전(전역 사후 수축 · 실측 시행 부족)");
            else if (hook.Posterior.IsProvenWinner) sb.Append("🎯 훅 '").Append(hook.DisplayLabel).Append("' 검증된 승자(하한 ×").Append(hook.LowerConfidenceBound.ToString("0.00", Inv)).Append(" · 시행 ").Append(hook.ArmTrials.ToString("0.#", Inv)).Append(')');
            else if (hook.IsParetoFrontier) sb.Append("⚖️ 훅 '").Append(hook.DisplayLabel).Append("' 파레토 프런티어(3초·완주·CTR 표본 ").Append(hook.ObjectiveSamples.ToString("0.#", Inv)).Append(')');
            else if (kind == PlutoTopicBriefKinds.UpsideExplore) sb.Append("🎰 훅 '").Append(hook.DisplayLabel).Append("' 탐색 상한 ×").Append(hook.UcbScore.ToString("0.00", Inv)).Append("(시행 ").Append(hook.ArmTrials.ToString("0.#", Inv)).Append(" · 미검증)");
            else if (hook.ArmTrials > 0) sb.Append("훅 '").Append(hook.DisplayLabel).Append("' 학습 ×").Append(hook.LearnedWeightMultiplier.ToString("0.00", Inv)).Append("(시행 ").Append(hook.ArmTrials.ToString("0.#", Inv)).Append(" · 미검증)");
            else sb.Append("훅 '").Append(hook.DisplayLabel).Append("' 사전 순위");
        }
        if (sb.Length > 0) sb.Append(" · ");
        if (framingEvidenced && framing.Samples > 0)
            sb.Append("🅰️ 제목 ").Append(framing.Framing).Append(framingIndex > 0 ? " 2위 A/B 검증" : " 실측 CTR ").Append(framingIndex > 0 ? "(" : "").Append(framing.ShrunkCtrPct.ToString("0.0", Inv)).Append("% · n=").Append(framing.Samples.ToString(Inv)).Append(framingIndex > 0 ? ")" : "");
        else sb.Append("🅰️ 제목 ").Append(framing.Framing).Append(" 사전 순서(실측 귀속 ").Append(PlutoYouTubePublishPackService.MinAttributedSamples.ToString(Inv)).Append("건 미만)");
        if (blockedCount > 0) sb.Append(" · 🛡️ 오답 가드 ").Append(blockedCount.ToString(Inv)).Append("개 제외");
        return sb.ToString();
    }

    private static string BriefIdOf(string kind, string theme, string hook, string look, string bgm, string seriesKey)
    {
        var h = 2166136261u;
        foreach (var s in new[] { kind, theme, hook, look, bgm, seriesKey })
        {
            foreach (var ch in s) { h ^= ch; h *= 16777619u; }
            h ^= '|'; h *= 16777619u;
        }
        return "tp-" + h.ToString("x8", Inv);
    }
}
