using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🔭 즉시 실행 가능한 일일 영상 기획안 — 프롬프트 · 훅 · 룩 · BGM 무드까지 한 번에 세팅.</summary>
public sealed record PlutoTrendOpportunityBrief(string BriefId, string Title, string TrendKeyword, string TargetPlatform, string ReadyPrompt,
    string RecommendedHook, string RecommendedColorLook, string RecommendedBgmMood, string RationaleBadge, int AvoidedMistakeCount)
{
    /// <summary>LLM 보강 채택 여부(false → 결정론 템플릿).</summary>
    public bool UsedLlm { get; init; }
    /// <summary>트렌드 출처(<c>live</c> · <c>memory</c> · <c>seasonal</c>).</summary>
    public string Source { get; init; } = PlutoTrendOpportunityEngine.SourceSeasonal;
    /// <summary>🎰 탐색 기획안 — 검증된 승자 대신 UCB1 최상위(덜 검증된) 훅/룩을 배정.</summary>
    public bool IsExploration { get; init; }
}

/// <summary>🔭 Pluto 4.3 자율 트렌드 기회 레이더 — 트렌드 × 학습 랭커 × 오답 노트 → 일일 기획안.</summary>
public interface IPlutoTrendOpportunityEngine
{
    /// <summary>
    /// 🔭 일일 기획안 <paramref name="count"/>건(기본 3 · 1~5 클램프) — 실시간/저장 트렌드(오프라인 → 한국 시즌·상시 주제) · 오답 노트 차단 키워드 제외 ·
    /// 랭커 1위 훅/룩/BGM 결합. 제목·프롬프트 보강은 Gemini → PlutoCore → 로컬 게이트웨이 → 결정론 템플릿. 호출자 취소만 전파.
    /// </summary>
    Task<IReadOnlyList<PlutoTrendOpportunityBrief>> GenerateDailyBriefsAsync(int count = 3, CancellationToken ct = default);
}

/// <summary>
/// 🔭 <see cref="IPlutoTrendOpportunityEngine"/> 구현(순수 BCL · 결정론 폴백).
///   ▸ 트렌드 우선순위: 하베스터 실시간 캐시 → 웹 수집기(<see cref="LiveFetchTimeout"/>) → 학습 메모리 활성 바이럴 태그 → 시즌/상시 폴백.
///   ▸ 차단: 오답 노트 차단 토큰과 양방향 부분 일치(대소문자 무시) 또는 유효 벌점 ≥ <see cref="PlutoMistakeNotebookService.BlockThreshold"/>.
///   ▸ LLM 응답은 스키마 즉시 검증 · 차단 토큰 포함/길이 초과 필드는 템플릿 유지.
///   ▸ 🎰 활용/탐색 균형: 2건 이상이면 마지막 1건은 UCB1 최상위 덜 검증된 훅/룩의 탐색 기획안(탐색 이력 없음 → 전부 활용) · 🔗 역시너지 훅×룩 조합 회피.
/// </summary>
public sealed class PlutoTrendOpportunityEngine : IPlutoTrendOpportunityEngine
{
    public const int DefaultCount = 3;
    public const int MaxCount = 5;
    public const int MaxTitleLength = 40;
    public const int MaxPromptLength = 400;
    public const double Temperature = 0.7;
    public static readonly TimeSpan LiveFetchTimeout = TimeSpan.FromSeconds(4);
    public const string SourceLive = "live", SourceMemory = "memory", SourceSeasonal = "seasonal";

    public const string LlmInstruction =
        "You are Pluto, an autonomous short-form video strategist for a Korean baby/family creator. For each numbered brief, write a catchy Korean title "
        + "(<= 24 Korean characters, no hashtags) and one English text-to-video prompt (<= 300 chars) for a 15-second vertical montage that uses the given trend keyword, hook, color look and BGM mood. "
        + "Respond with JSON only: {\"briefs\":[{\"i\":1,\"title\":\"...\",\"prompt\":\"...\"}, ...]} with exactly one entry per brief in order.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Platforms = { "YouTube Shorts", "Instagram Reels", "TikTok" };
    private static readonly string[] Evergreen = { "아기 첫걸음", "아기 웃음 모먼트", "육아 브이로그", "아기 목욕 시간", "주말 가족 나들이", "아기 먹방" };

    /// <summary>월(1~12) → 한국 시즌 주제(KST 기준).</summary>
    private static readonly string[][] Seasonal =
    {
        new[] { "새해 첫 일출", "아기 설빔 한복" },          // 1
        new[] { "설날 세배", "겨울 눈놀이" },                // 2
        new[] { "봄맞이 첫 나들이", "어린이집 첫 등원" },    // 3
        new[] { "벚꽃 피크닉", "봄꽃 나들이" },              // 4
        new[] { "어린이날 선물", "가정의 달 가족사진" },     // 5
        new[] { "초여름 물놀이", "아기 수박 먹방" },         // 6
        new[] { "여름 바다 여행", "아기 수영장" },           // 7
        new[] { "한여름 계곡 캠핑", "여름휴가 브이로그" },   // 8
        new[] { "추석 한복", "가을 하늘 산책" },             // 9
        new[] { "가을 단풍 나들이", "할로윈 코스튬" },       // 10
        new[] { "첫눈 기다림", "포근한 집콕 육아" },         // 11
        new[] { "크리스마스 트리", "연말 가족 파티" },       // 12
    };

    private readonly IPlutoAdaptivePatternRanker? _ranker;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoLearningService? _learning;
    private readonly PlutoAutonomousTrendHarvester? _harvester;
    private readonly IPlutoWebTrendFetcher? _web;
    private readonly Func<DateTime> _utcNow;
    private volatile bool _lastUsedLlm;

    /// <summary>LLM 전체 예산(체인 각 단계 타임아웃은 브리지 정책 유지).</summary>
    public TimeSpan? LlmBudget { get; init; }

    /// <summary>직전 생성이 LLM 보강을 1건 이상 채택했는지.</summary>
    public bool LastUsedLlm => _lastUsedLlm;

    public PlutoTrendOpportunityEngine(IPlutoAdaptivePatternRanker? ranker = null, IPlutoMistakeNotebookService? notebook = null, IPlutoLlmClient? llm = null,
        PlutoLearningService? learning = null, PlutoAutonomousTrendHarvester? harvester = null, IPlutoWebTrendFetcher? webFetcher = null, Func<DateTime>? utcNow = null)
    {
        _ranker = ranker;
        _notebook = notebook;
        _llm = llm;
        _learning = learning;
        _harvester = harvester;
        _web = webFetcher;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<PlutoTrendOpportunityBrief>> GenerateDailyBriefsAsync(int count = DefaultCount, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _lastUsedLlm = false;
        count = Math.Clamp(count, 1, MaxCount);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var suppressed = SafeSuppressed();
        var now = _utcNow();
        var raw = await CollectTrendsAsync(now, ct).ConfigureAwait(false);
        var picked = new List<(string Keyword, string Source)>(count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var avoidedKeywords = 0;
        for (var i = 0; i < raw.Count && picked.Count < count; i++)
        {
            var kw = raw[i].Keyword;
            if (!seen.Add(kw)) continue;
            if (IsBlocked(kw, suppressed)) { avoidedKeywords++; continue; }
            picked.Add(raw[i]);
        }

        var ranker = _ranker ?? new PlutoAdaptivePatternRanker(null, _notebook);
        PlutoAdaptiveRecommendation? rec = null;
        IReadOnlyList<PlutoRankedOption> looks = Array.Empty<PlutoRankedOption>(), bgms = Array.Empty<PlutoRankedOption>();
        try
        {
            rec = ranker.GetCurrentRecommendations();
            looks = ranker.RankOptions(PlutoPatternKind.Look);
            bgms = ranker.RankOptions(PlutoPatternKind.BgmMood);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] ranker: " + ex.Message); }   // [부분 구제] 기본 추천

        var look = PickOption(looks, rec?.TopColorLook, null, suppressed, out var lookSkipped) ?? PlutoColorGradingCompiler.NaturalPastelName;
        var bgm = PickOption(bgms, rec?.TopBgmMood, ExcludedBgmMoods, suppressed, out var bgmSkipped) ?? PromptFlowStudio.Services.Export.BabyBgmTrack.Lullaby.ToString();
        var negative = SafeNegativeConstraint();
        var date = now.AddHours(9).ToString("yyyyMMdd", Inv);   // KST 일자

        var surge = SafeSurge(ranker);   // 🌊 레짐 드리프트 → 첫 1건만 활용 · 나머지는 전부 탐색 슬롯
        var briefs = new PlutoTrendOpportunityBrief[picked.Count];
        for (var i = 0; i < briefs.Length; i++)
        {
            var (kw, source) = picked[i];
            var hook = ResolveHook(ranker, rec, kw, suppressed, out var hookSkipped, out var hookReinforced);
            var briefLook = look;
            var exploration = false;
            if (briefs.Length >= 2 && (i == briefs.Length - 1 || (surge && i >= 1)))   // 🎰 마지막 1건 = 탐색 슬롯(이력 없으면 활용 유지) · 🌊 서지 중엔 2번째부터 전부
            {
                var exHook = PickExplorationHook(ranker, kw, hook, suppressed);
                var exLook = PickExplorationLook(ranker, look, suppressed);
                if (exHook is not null) { hook = exHook; hookReinforced = false; exploration = true; }
                if (exLook is not null) { briefLook = exLook; exploration = true; }
            }
            var synergyAvoided = 0;
            if (IsAntiSynergy(ranker, hook, briefLook))   // 🔗 역시너지 조합 → 대체 룩
            {
                var alt = PickSynergySafeLook(ranker, looks, hook, briefLook, suppressed);
                if (alt is not null) { briefLook = alt; synergyAvoided = 1; }
            }
            var platform = Platforms[i % Platforms.Length];
            var title = BuildTemplateTitle(kw, i);
            var prompt = BuildTemplatePrompt(kw, hook, briefLook, bgm, platform, negative);
            var badge = BuildBadge(source, hookReinforced, rec, false, exploration);
            briefs[i] = new PlutoTrendOpportunityBrief("brief-" + date + "-" + Fnv(kw).ToString("x8", Inv), title, kw, platform, prompt, hook, briefLook, bgm, badge,
                avoidedKeywords + hookSkipped + lookSkipped + bgmSkipped + synergyAvoided) { Source = source, IsExploration = exploration };
        }

        if (briefs.Length > 0 && _llm is not null)
        {
            string? json = null;
            try { json = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmInstruction, BuildUserPrompt(briefs, negative), Temperature, ct, LlmBudget).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] llm fallback: " + ex.Message); json = null; }
            _lastUsedLlm = MergeLlm(briefs, json, suppressed, rec);
        }
        return briefs;
    }

    // ── 📡 트렌드 수집 ──

    private async Task<List<(string Keyword, string Source)>> CollectTrendsAsync(DateTime nowUtc, CancellationToken ct)
    {
        var list = new List<(string, string)>(24);
        IReadOnlyList<PlutoLiveTrendKeyword>? live = null;
        try { live = _harvester?.LastLiveKeywords; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] harvester: " + ex.Message); }
        if ((live is null || live.Count == 0) && _web is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LiveFetchTimeout);
            try { live = await _web.FetchLiveTrendingKeywordsAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] web: " + ex.Message); live = null; }   // 1초 내 BCL 폴백
        }
        if (live is not null)
            for (var i = 0; i < live.Count; i++)
                if (Clean(live[i]?.Keyword) is { } k) list.Add((k, SourceLive));
        try
        {
            var active = _learning?.GetActiveViralTrends(6);
            if (active is not null)
                for (var i = 0; i < active.Count; i++)
                    if (Clean(active[i].Tag.TrimStart('#')) is { } k) list.Add((k, SourceMemory));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] memory: " + ex.Message); }

        var month = nowUtc.AddHours(9).Month;   // KST
        foreach (var s in Seasonal[month - 1]) list.Add((s, SourceSeasonal));
        foreach (var s in Seasonal[month % 12]) list.Add((s, SourceSeasonal));   // 다음 달 선기획
        foreach (var s in Evergreen) list.Add((s, SourceSeasonal));
        return list;
    }

    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        return v.Length > 40 ? v[..40] : v;
    }

    // ── 🛡 오답 노트 ──

    private static readonly string[] ExcludedBgmMoods = { "custom", "none" };

    /// <summary>🛡 차단 판정 — 차단 토큰과 양방향 부분 일치(대소문자 무시) 또는 유효 벌점 ≥ 차단 임계.</summary>
    internal bool IsBlocked(string text, IReadOnlySet<string>? suppressed)
    {
        if (suppressed is { Count: > 0 })
            foreach (var t in suppressed)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (text.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            }
        try { return _notebook is not null && _notebook.GetEffectivePenalty(text) >= PlutoMistakeNotebookService.BlockThreshold; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] penalty: " + ex.Message); return false; }
    }

    /// <summary>랭킹 순 첫 비감점 · 비차단 후보(없음 → 1위 · 랭킹 없음 → null). 건너뛴 감점 후보 수 반환.</summary>
    private string? PickOption(IReadOnlyList<PlutoRankedOption> ranked, PlutoRankedOption? top, string[]? excluded, IReadOnlySet<string>? suppressed, out int skipped)
    {
        skipped = 0;
        if (top is { IsPenalized: false } t && !IsExcluded(t.DisplayLabel, excluded) && !IsBlocked(t.DisplayLabel, suppressed)) return t.DisplayLabel;
        for (var i = 0; i < ranked.Count; i++)
        {
            var o = ranked[i];
            if (IsExcluded(o.DisplayLabel, excluded)) continue;
            if (o.IsPenalized || IsBlocked(o.DisplayLabel, suppressed)) { skipped++; continue; }
            return o.DisplayLabel;
        }
        return top?.DisplayLabel ?? (ranked.Count > 0 ? ranked[0].DisplayLabel : null);
    }

    private static bool IsExcluded(string label, string[]? excluded)
    {
        if (excluded is null) return false;
        for (var i = 0; i < excluded.Length; i++) if (string.Equals(label, excluded[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>🧠 훅 — 강화된 전역 1위(비감점) 우선 → 키워드 테마 훅 랭킹 첫 비감점 → 전역 1위.</summary>
    private string ResolveHook(IPlutoAdaptivePatternRanker ranker, PlutoAdaptiveRecommendation? rec, string keyword, IReadOnlySet<string>? suppressed, out int skipped, out bool reinforced)
    {
        skipped = 0;
        reinforced = false;
        if (rec?.TopHook is { IsReinforced: true, IsPenalized: false } th && !IsBlocked(th.DisplayLabel, suppressed)) { reinforced = true; return th.DisplayLabel; }
        IReadOnlyList<PlutoRankedOption> themed;
        try { themed = ranker.RankOptions(PlutoPatternKind.Hook, PlutoMontageStoryGenerator.TemplateHookLines(keyword)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] hook: " + ex.Message); themed = Array.Empty<PlutoRankedOption>(); }
        var pick = PickOption(themed, null, null, suppressed, out skipped);
        if (pick is not null) return pick;
        return rec?.TopHook.DisplayLabel ?? PlutoMontageStoryGenerator.TemplateHookLines(keyword)[0];
    }

    // ── 🎰 탐색 · 🔗 조합 ──

    /// <summary>🌊 탐색 서지 활성 여부(실패 → false · [부분 구제] 기본 탐색 슬롯 1건).</summary>
    private static bool SafeSurge(IPlutoAdaptivePatternRanker ranker)
    {
        try { return ranker.GetRegimeDriftStatus() is { IsSurgeActive: true }; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] surge: " + ex.Message); return false; }
    }

    /// <summary>🎰 탐색 훅 — 키워드 테마 훅 + 활용 훅을 한 풀로 UCB1 랭킹 → 활용 훅이 아닌 첫 비감점 · 비차단 · 덜 검증된(보너스 &gt; 0) 후보(없음 → null).</summary>
    private string? PickExplorationHook(IPlutoAdaptivePatternRanker ranker, string keyword, string exploitHook, IReadOnlySet<string>? suppressed)
    {
        try
        {
            var themed = PlutoMontageStoryGenerator.TemplateHookLines(keyword);
            var pool = new List<string>(themed.Count + 1);
            for (var i = 0; i < themed.Count; i++) pool.Add(themed[i]);
            pool.Add(exploitHook);
            return FirstExplorationCandidate(ranker.RankForExploration(PlutoPatternKind.Hook, pool), exploitHook, suppressed);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] explore hook: " + ex.Message); return null; }
    }

    private string? PickExplorationLook(IPlutoAdaptivePatternRanker ranker, string exploitLook, IReadOnlySet<string>? suppressed)
    {
        try { return FirstExplorationCandidate(ranker.RankForExploration(PlutoPatternKind.Look), exploitLook, suppressed); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] explore look: " + ex.Message); return null; }
    }

    private string? FirstExplorationCandidate(IReadOnlyList<PlutoRankedOption> ranked, string exploit, IReadOnlySet<string>? suppressed)
    {
        for (var i = 0; i < ranked.Count; i++)
        {
            var o = ranked[i];
            if (string.Equals(o.DisplayLabel, exploit, StringComparison.Ordinal)) continue;
            if (o.ExplorationBonus <= PlutoAdaptivePatternRanker.Epsilon || o.ArmTrials >= PlutoAdaptivePatternRanker.UnderSampledTrials) continue;
            if (o.IsPenalized || IsBlocked(o.DisplayLabel, suppressed)) continue;
            return o.DisplayLabel;
        }
        return null;
    }

    private static bool IsAntiSynergy(IPlutoAdaptivePatternRanker ranker, string hook, string look)
    {
        try { return ranker.GetPairSynergy(hook, look) is { IsAntiSynergy: true }; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] synergy: " + ex.Message); return false; }
    }

    private string? PickSynergySafeLook(IPlutoAdaptivePatternRanker ranker, IReadOnlyList<PlutoRankedOption> looks, string hook, string current, IReadOnlySet<string>? suppressed)
    {
        for (var i = 0; i < looks.Count; i++)
        {
            var o = looks[i];
            if (string.Equals(o.DisplayLabel, current, StringComparison.Ordinal) || o.IsPenalized || IsBlocked(o.DisplayLabel, suppressed)) continue;
            if (!IsAntiSynergy(ranker, hook, o.DisplayLabel)) return o.DisplayLabel;
        }
        return null;
    }

    // ── 📝 템플릿 ──

    private static readonly string[] TitlePatterns = { "{0}, 15초로 담은 우리 이야기", "지금 뜨는 {0} 감성 몽타주", "{0} 모먼트 하이라이트" };

    internal static string BuildTemplateTitle(string keyword, int index)
    {
        var t = string.Format(Inv, TitlePatterns[index % TitlePatterns.Length], keyword);
        return t.Length > MaxTitleLength ? t[..MaxTitleLength] : t;
    }

    internal static string BuildTemplatePrompt(string keyword, string hook, string look, string bgm, string platform, string? negative)
    {
        var sb = new StringBuilder(256);
        sb.Append(keyword).Append(" themed 15-second vertical family montage for ").Append(platform)
          .Append(", warm natural light, cinematic handheld, opening hook: ").Append(hook)
          .Append(", color look: ").Append(look).Append(", bgm mood: ").Append(bgm);
        if (!string.IsNullOrWhiteSpace(negative)) sb.Append(". ").Append(negative.Trim());
        return sb.Length > MaxPromptLength ? sb.ToString(0, MaxPromptLength) : sb.ToString();
    }

    private static string BuildBadge(string source, bool hookReinforced, PlutoAdaptiveRecommendation? rec, bool llm, bool exploration = false)
    {
        var s = source switch { SourceLive => "🔥 실시간 급상승", SourceMemory => "🧠 트렌드 메모리", _ => "🌱 시즌·상시 기획" };
        if (exploration) s += " · 🎰 탐색 기획";
        else if (hookReinforced && rec is not null) s += " · 학습 훅 ×" + rec.TopHook.LearnedWeightMultiplier.ToString("0.00", Inv);
        else if (rec is { HasReinforcedHistory: true }) s += " · 🧠 학습 반영";
        return s + (llm ? " · AI" : " · 템플릿");
    }

    // ── 🧠 LLM 보강(스키마 즉시 검증) ──

    internal static string BuildUserPrompt(IReadOnlyList<PlutoTrendOpportunityBrief> briefs, string? negative)
    {
        var sb = new StringBuilder(512);
        for (var i = 0; i < briefs.Count; i++)
        {
            var b = briefs[i];
            sb.Append(i + 1).Append(". keyword=").Append(b.TrendKeyword).Append(" | platform=").Append(b.TargetPlatform).Append(" | hook=").Append(b.RecommendedHook)
              .Append(" | look=").Append(b.RecommendedColorLook).Append(" | bgm=").Append(b.RecommendedBgmMood).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(negative)) sb.Append(negative.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>LLM JSON(<c>{"briefs":[…]}</c> 또는 루트 배열) → 브리프별 제목/프롬프트 채택 · 무효/차단 → 템플릿 유지. 1건 이상 채택 시 true.</summary>
    internal bool MergeLlm(PlutoTrendOpportunityBrief[] briefs, string? json, IReadOnlySet<string>? suppressed, PlutoAdaptiveRecommendation? rec)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        var any = false;
        try
        {
            using var doc = JsonDocument.Parse(PlutoJsonSanitizer.Sanitize(json));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("briefs", out var arr)) root = arr;
            if (root.ValueKind != JsonValueKind.Array) return false;
            var pos = 0;
            foreach (var el in root.EnumerateArray())
            {
                var idx = pos++;
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (el.TryGetProperty("i", out var iv) && iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out var n)) idx = n - 1;
                if ((uint)idx >= (uint)briefs.Length) continue;
                var title = ValidField(el, "title", MaxTitleLength, suppressed);
                var prompt = ValidField(el, "prompt", MaxPromptLength, suppressed);
                if (title is null && prompt is null) continue;
                var b = briefs[idx];
                briefs[idx] = b with
                {
                    Title = title ?? b.Title,
                    ReadyPrompt = prompt is null ? b.ReadyPrompt : EnsureDirectives(prompt, b),
                    RationaleBadge = BuildBadge(b.Source, b.RationaleBadge.Contains("학습 훅", StringComparison.Ordinal), rec, true, b.IsExploration),
                    UsedLlm = true,
                };
                any = true;
            }
        }
        catch (JsonException ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] schema: " + ex.Message); }
        return any;
    }

    private string? ValidField(JsonElement el, string name, int maxLen, IReadOnlySet<string>? suppressed)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > maxLen || IsBlockedText(s, suppressed)) return null;
        return s;
    }

    /// <summary>LLM 문구 차단 — 차단 토큰 포함(텍스트 → 토큰 방향만).</summary>
    private static bool IsBlockedText(string text, IReadOnlySet<string>? suppressed)
    {
        if (suppressed is not { Count: > 0 }) return false;
        foreach (var t in suppressed)
            if (!string.IsNullOrWhiteSpace(t) && text.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>LLM 프롬프트에 훅/룩/BGM 지시가 빠졌으면 말미 보강(즉시 실행성 보장).</summary>
    private static string EnsureDirectives(string prompt, PlutoTrendOpportunityBrief b)
    {
        if (prompt.Contains("opening hook:", StringComparison.OrdinalIgnoreCase)) return prompt;
        var s = prompt.TrimEnd('.', ' ') + ", opening hook: " + b.RecommendedHook + ", color look: " + b.RecommendedColorLook + ", bgm mood: " + b.RecommendedBgmMood;
        return s.Length > MaxPromptLength + 120 ? s[..(MaxPromptLength + 120)] : s;
    }

    // ── 공통 ──

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        try { if (_ranker is not null) await _ranker.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] ranker load: " + ex.Message); }
        try { if (_notebook is not null) await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] notebook load: " + ex.Message); }
    }

    private IReadOnlySet<string>? SafeSuppressed()
    {
        try { return _notebook?.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] suppressed: " + ex.Message); return null; }
    }

    private string SafeNegativeConstraint()
    {
        try { return _notebook?.BuildNegativeConstraintPrompt() ?? ""; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Opportunity] constraint: " + ex.Message); return ""; }
    }

    private static uint Fnv(string s)
    {
        var h = 2166136261u;
        for (var i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
        return h;
    }
}
