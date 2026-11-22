using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📺 연결 단계.</summary>
public enum PlutoYouTubeLinkTier
{
    /// <summary>매칭 실패(보고만 함).</summary>
    Unmatched = 0,
    /// <summary>Tier 1 — 저장된 <c>YouTubeVideoId</c> 정확 일치.</summary>
    ExactBinding = 1,
    /// <summary>Tier 1b — 기존 로컬 클립 ID/정규화 제목 정확 일치.</summary>
    ExactLocal = 2,
    /// <summary>Tier 2 — 제목 유사도 + 시리즈 편 + 게시 창 결정론 매칭(연결 영속).</summary>
    Fuzzy = 3,
}

/// <summary>📺 YouTube 행 1개의 연결 결과.</summary>
public sealed record PlutoYouTubeLinkResult(PlutoRenderedClipRecord? Clip, PlutoYouTubeLinkTier Tier, double Score, string? Reason)
{
    public bool IsMatched => Clip is not null;
    /// <summary>이번 호출에서 카탈로그에 새로 영속한 연결.</summary>
    public bool NewlyBound { get; init; }
}

/// <summary>
/// 📺 YouTube Studio 행 ↔ Pluto 렌더 카탈로그 연결기.
///   ▸ Tier 1: 저장된 <c>YouTubeVideoId</c>(O(1) 재매칭) → 로컬 클립 ID/정규화 제목 정확 일치.
///   ▸ Tier 2: 미연결 클립만 대상. 정규화 제목 유사도(토큰 Dice + 문자 바이그램 Dice) ≥ <see cref="MinFuzzyScore"/>,
///     시리즈 편(<c>n/N편</c>)이 양쪽에 있으면 반드시 일치, 게시 시각이 렌더 후 <see cref="PublishWindowDays"/>일 안.
///     1·2위 점수 차가 <see cref="AmbiguityMargin"/> 미만이면 모호로 거부. 확정 연결은 카탈로그에 영속(1:1 원자 검사).
///   ▸ Tier 3: 그 밖에는 추측하지 않고 미매칭 사유를 보고.
/// </summary>
public sealed class PlutoYouTubeCatalogLinker
{
    public const double MinFuzzyScore = 0.6;
    public const double AmbiguityMargin = 0.08;
    public const int PublishWindowDays = 7;
    /// <summary>렌더 시각과 게시일(날짜 단위) 간 시계/시간대 오차 허용.</summary>
    public const double PublishSkewToleranceHours = 36;
    public const int CandidateScanCount = PlutoRenderCatalogService.Capacity;
    /// <summary>🌐 Step 31 — 내보내기 헤더 언어가 같은 로캘 형제에 주는 동점 해소 가점(모호 여유보다 큼 · 제목 유사도 0.12 이상 차이는 뒤집지 못함).</summary>
    public const double WeakLocaleBonus = 0.12;

    /// <summary>🌐 Step 31 — 로캘 변형이 2개 이상인(마스터 + 변형) 가족 루트 집합. 단일 로캘 클립(레거시)은 로캘 필터 대상이 아니다.</summary>
    public static HashSet<string> LocaleFamilies(IReadOnlyList<PlutoRenderedClipRecord> clips)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clips) if (c.ParentClipId is { } p) set.Add(p);
        return set;
    }

    /// <summary>
    /// 🌐 Step 31 — Tier 1 정확 제목 일치를 로캘 가족 안에서 재라우팅(순수 · 결정론). 같은 가족에 정규화 제목이 같은 형제가 여럿이면
    /// (예: 브랜드명 제목 en/es · <c>Ep.1/3</c> vs <c>Ep. 1/3</c> 정규형 동일) 제목 로캘(강) → 헤더 언어(약) 순으로 고르고, 못 고르면 null(모호 → 미매칭 대기열).
    /// 강한 제목 로캘이 일치 클립과 다르고 그 로캘 형제가 정확 일치 후보에 없으면 null(교차 로캘 오연결 차단).
    /// </summary>
    public static (PlutoRenderedClipRecord? Clip, string? Reason) RouteExactTitleToLocale(PlutoRenderedClipRecord matched, YouTubeAnalyticsRow row, IReadOnlyList<PlutoRenderedClipRecord> clips)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(clips);
        var root = matched.LocaleFamilyRoot;
        var norm = PlutoRenderCatalogService.NormalizeKey(row.Title);
        var exact = new List<PlutoRenderedClipRecord>(2);
        var familySize = 0;
        foreach (var c in clips)
        {
            if (!string.Equals(c.LocaleFamilyRoot, root, StringComparison.OrdinalIgnoreCase)) continue;
            familySize++;
            if (norm.Length > 0 && string.Equals(PlutoRenderCatalogService.NormalizeKey(c.Title), norm, StringComparison.Ordinal)) exact.Add(c);
        }
        if (familySize <= 1) return (matched, null);   // 단일 로캘 클립 — 기존 동작
        if (exact.Count == 0) exact.Add(matched);       // VideoId 일치 등 제목 외 경로
        var strong = PlutoMultiLanguageChannelService.InferTitleLocale(row.Title);
        var weak = PlutoMultiLanguageChannelService.LocaleOfExportLanguage(row.ExportLanguage);
        if (strong is not null)
        {
            foreach (var c in exact) if (string.Equals(c.TargetLocale, strong, StringComparison.OrdinalIgnoreCase)) return (c, null);
            return (null, "제목 로캘(" + strong + ")과 다른 로캘 변형만 일치");
        }
        if (exact.Count == 1) return (exact[0], null);   // 유일 정확 일치는 UI 언어보다 강함
        if (weak is not null)
            foreach (var c in exact) if (string.Equals(c.TargetLocale, weak, StringComparison.OrdinalIgnoreCase)) return (c, null);
        return (null, "로캘 변형 모호(같은 제목 " + exact.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개)");
    }

    // 🌐 Step 31 — 로캘 편 토큰: 1/3편 · 第1/3話 · 第2話 · Ep.1/3 · Ep. 1/3 · Episodio 2 · Parte 2 · Capítulo 2 (n/N 의 총 편수까지 소비 → 토큰 잔여물 없음)
    private static readonly Regex EpisodeRx = new(
        @"(?<n>\d{1,3})\s*/\s*\d{1,3}\s*(?:편|話|话)|第\s*(?<n>\d{1,3})\s*(?:/\s*\d{1,3}\s*)?(?:話|话|回|集)|\b(?:episodio|episode|cap[ií]tulo|parte|part|cap|ep)\.?\s*(?<n>\d{1,3})(?:\s*/\s*\d{1,3})?|제?\s*(?<n>\d{1,3})\s*편",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal) { "shorts", "short", "쇼츠", "숏츠", "mp4", "mov", "webm", "youtube", "유튜브" };

    private readonly IPlutoRenderCatalogService _catalog;

    public PlutoYouTubeCatalogLinker(IPlutoRenderCatalogService catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>시리즈 편 번호(<c>2/3편</c> → 2 · <c>EP 4</c> → 4 · <c>3편</c> → 3 · 없음 → 0).</summary>
    public static int ExtractEpisode(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return 0;
        try
        {
            var m = EpisodeRx.Match(title);
            if (!m.Success || !m.Groups["n"].Success) return 0;
            return int.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        catch (RegexMatchTimeoutException) { return 0; }
    }

    /// <summary>제목 토큰(소문자 · 문자/숫자 연속 · 해시태그 기호 제거 · 편 표기 · 불용어 제외).</summary>
    public static List<string> Tokenize(string? title)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(title)) return tokens;
        string s;
        try { s = EpisodeRx.Replace(title, " "); }
        catch (RegexMatchTimeoutException) { s = title; }
        var sb = new StringBuilder(16);
        for (var i = 0; i <= s.Length; i++)
        {
            var ch = i < s.Length ? s[i] : ' ';
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); continue; }
            if (sb.Length > 0)
            {
                var t = sb.ToString();
                sb.Clear();
                if (!StopTokens.Contains(t)) tokens.Add(t);
            }
        }
        return tokens;
    }

    private static double Dice(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var inter = 0;
        foreach (var x in a) if (b.Contains(x)) inter++;
        return 2.0 * inter / (a.Count + b.Count);
    }

    private static HashSet<string> Bigrams(List<string> tokens)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var joined = string.Concat(tokens);
        if (joined.Length == 1) set.Add(joined);
        for (var i = 0; i + 1 < joined.Length; i++) set.Add(joined.Substring(i, 2));
        return set;
    }

    /// <summary>제목 유사도 0~1 — 토큰 Dice와 문자 바이그램 Dice의 평균(한국어 조사 차이에 강건). 편 번호 충돌 → 0.</summary>
    public static double TitleSimilarity(string? youTubeTitle, string? catalogTitle)
    {
        var ea = ExtractEpisode(youTubeTitle);
        var eb = ExtractEpisode(catalogTitle);
        if (ea > 0 && eb > 0 && ea != eb) return 0.0;   // 🛡️ 같은 시리즈의 다른 편
        var ta = Tokenize(youTubeTitle);
        var tb = Tokenize(catalogTitle);
        if (ta.Count == 0 || tb.Count == 0) return 0.0;
        var tokenDice = Dice(new HashSet<string>(ta, StringComparer.Ordinal), new HashSet<string>(tb, StringComparer.Ordinal));
        var bigramDice = Dice(Bigrams(ta), Bigrams(tb));
        return 0.5 * tokenDice + 0.5 * bigramDice;
    }

    /// <summary>게시 시각이 렌더 후 창 안인지(렌더 전 <see cref="PublishSkewToleranceHours"/>시간까지 허용).</summary>
    public static bool InPublishWindow(DateTimeOffset renderedAtUtc, DateTimeOffset publishedAtUtc)
    {
        var hours = (publishedAtUtc - renderedAtUtc).TotalHours;
        return hours >= -PublishSkewToleranceHours && hours <= PublishWindowDays * 24.0 + PublishSkewToleranceHours;
    }

    /// <summary>
    /// Tier 2 후보 선정(순수 · 결정론). 미연결 클립 중 창·유사도 조건을 통과한 최고점 1개. 2위와 차이가 작으면 모호로 null.
    /// 근접도 보너스(최대 0.05)는 동점을 줄이는 데만 쓴다.
    /// </summary>
    public static (PlutoRenderedClipRecord? Clip, double Score, string? Reason) SelectFuzzyCandidate(YouTubeAnalyticsRow row, IReadOnlyList<PlutoRenderedClipRecord> clips, ISet<string>? excludedClipIds = null)
    {
        if (string.IsNullOrWhiteSpace(row.Title)) return (null, 0, "제목 없음");
        if (row.PublishedAtUtc is not { } published) return (null, 0, "게시일 없음");
        var families = LocaleFamilies(clips);
        var strong = PlutoMultiLanguageChannelService.InferTitleLocale(row.Title);
        var weak = strong is null ? PlutoMultiLanguageChannelService.LocaleOfExportLanguage(row.ExportLanguage) : null;
        PlutoRenderedClipRecord? best = null;
        double bestScore = 0, second = 0;
        foreach (var c in clips)
        {
            if (c.YouTubeVideoId is not null) continue;   // 이미 다른 영상에 연결됨
            if (excludedClipIds is not null && excludedClipIds.Contains(c.VideoId)) continue;
            if (!InPublishWindow(c.RenderedAtUtc, published)) continue;
            if (EpisodeConflict(row.Title, c)) continue;   // 🛡️ Step 24 — 편 번호가 카탈로그 RenderMode(시리즈 n/N편)에만 있는 경우
            var multi = families.Contains(c.LocaleFamilyRoot);
            if (multi && strong is not null && !string.Equals(c.TargetLocale, strong, StringComparison.OrdinalIgnoreCase)) continue;   // 🌐 다른 로캘 변형 차단
            var sim = TitleSimilarity(row.Title, c.Title);
            if (sim < MinFuzzyScore) continue;
            var days = Math.Max(0.0, (published - c.RenderedAtUtc).TotalDays);
            var score = sim + 0.05 * (1.0 - Math.Min(1.0, days / PublishWindowDays));
            // 🌐 약한 신호(Studio UI 언어)는 같은 가족 형제 간 동점만 가름 — 제목이 훨씬 잘 맞는 다른 로캘을 뒤집지 못함
            if (multi && weak is not null && string.Equals(c.TargetLocale, weak, StringComparison.OrdinalIgnoreCase)) score += WeakLocaleBonus;
            if (score > bestScore) { second = bestScore; bestScore = score; best = c; }
            else if (score > second) second = score;
        }
        if (best is null) return (null, 0, "유사 제목/게시 창 후보 없음");
        if (second > 0 && bestScore - second < AmbiguityMargin) return (null, bestScore, "모호(후보 2개 이상)");
        return (best, bestScore, null);
    }

    /// <summary>
    /// 🎬 Step 24 — 카탈로그 클립의 편 번호. 제목의 편 표기가 우선이고, 없으면 RenderMode(<c>시리즈 2/3편</c>)에서 읽는다(시리즈 아님 → 0).
    /// 시리즈 편의 카탈로그 제목은 편 제목만 담으므로, RenderMode를 보지 않으면 같은 시리즈의 다른 편이 편 가드를 통과한다.
    /// </summary>
    public static int CatalogEpisode(PlutoRenderedClipRecord clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var e = ExtractEpisode(clip.Title);
        if (e > 0) return e;
        return PlutoSeriesArcMemoryService.TryParseEpisodeMode(clip.RenderMode, out var n, out _) ? n : 0;
    }

    /// <summary>YouTube 제목의 편 번호와 카탈로그 편 번호가 둘 다 있고 서로 다르면 true.</summary>
    public static bool EpisodeConflict(string? youTubeTitle, PlutoRenderedClipRecord clip)
    {
        var a = ExtractEpisode(youTubeTitle);
        if (a <= 0) return false;
        var b = CatalogEpisode(clip);
        return b > 0 && a != b;
    }

    /// <summary>
    /// 📌 Step 24 — 미매칭 행 수동 페어링 후보(순수 · 결정론). 아직 YouTube ID가 없는 클립만, 편 충돌 제외.
    /// 점수 = 제목 유사도 + 게시 창 안이면 0.1 + 근접도 최대 0.05. 유사도가 0이어도 게시 전 렌더 클립은 최근순 후보로 남긴다
    /// (YouTube에서 제목을 완전히 바꾼 경우). 같은 점수는 최근 렌더 우선.
    /// </summary>
    public static IReadOnlyList<(PlutoRenderedClipRecord Clip, double Score)> RankPairSuggestions(YouTubeAnalyticsRow row, IReadOnlyList<PlutoRenderedClipRecord> clips, int maxCount = 3)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(clips);
        var list = new List<(PlutoRenderedClipRecord Clip, double Score, int Order)>();
        var families = LocaleFamilies(clips);
        var strong = PlutoMultiLanguageChannelService.InferTitleLocale(row.Title);
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (c.YouTubeVideoId is not null || EpisodeConflict(row.Title, c)) continue;
            if (strong is not null && families.Contains(c.LocaleFamilyRoot) && !string.Equals(c.TargetLocale, strong, StringComparison.OrdinalIgnoreCase)) continue;   // 🌐 교차 로캘 제안 제외
            var sim = TitleSimilarity(row.Title, c.Title);
            var score = sim;
            if (row.PublishedAtUtc is { } p)
            {
                if (InPublishWindow(c.RenderedAtUtc, p))
                    score += 0.1 + 0.05 * (1.0 - Math.Min(1.0, Math.Max(0.0, (p - c.RenderedAtUtc).TotalDays) / PublishWindowDays));
                else if ((p - c.RenderedAtUtc).TotalHours < -PublishSkewToleranceHours) continue;   // 게시보다 나중에 렌더된 클립은 원본일 수 없음
            }
            list.Add((c, score, i));
        }
        list.Sort(static (x, y) => { var d = y.Score.CompareTo(x.Score); return d != 0 ? d : x.Order.CompareTo(y.Order); });
        var n = Math.Min(Math.Max(0, maxCount), list.Count);
        var result = new (PlutoRenderedClipRecord Clip, double Score)[n];
        for (var i = 0; i < n; i++) result[i] = (list[i].Clip, list[i].Score);
        return result;
    }

    /// <summary>
    /// 📺 보고서 행 전체를 연결(행 순서 결정론). 같은 내보내기 안에서 한 클립은 한 행에만 연결된다.
    /// Tier 2 확정 시 <see cref="IPlutoRenderCatalogService.TryBindYouTubeVideoIdAsync"/>로 영속하고, 1:1 충돌이면 매칭을 취소한다.
    /// </summary>
    public async Task<IReadOnlyList<PlutoYouTubeLinkResult>> LinkAsync(IReadOnlyList<YouTubeAnalyticsRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var results = new PlutoYouTubeLinkResult[rows.Count];
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingFuzzy = new List<int>();
        IReadOnlyList<PlutoRenderedClipRecord>? snapshot = null;

        // 1차: 정확 매칭(Tier 1)을 먼저 모두 확정 → 퍼지 매칭이 정확 매칭 대상 클립을 가로채지 않음
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            PlutoRenderedClipRecord? clip = null;
            var tier = PlutoYouTubeLinkTier.Unmatched;
            if (row.YouTubeVideoId is { } yt)
            {
                clip = await _catalog.FindByYouTubeVideoIdAsync(yt, ct).ConfigureAwait(false);
                if (clip is not null) tier = PlutoYouTubeLinkTier.ExactBinding;
                else if ((clip = await _catalog.FindByVideoIdOrTitleAsync(yt, ct).ConfigureAwait(false)) is not null) tier = PlutoYouTubeLinkTier.ExactLocal;
            }
            if (clip is null && row.Title is { } t && (clip = await _catalog.FindByVideoIdOrTitleAsync(t, ct).ConfigureAwait(false)) is not null)
                tier = PlutoYouTubeLinkTier.ExactLocal;
            // 🌐 Step 31 — 정확 제목 일치는 첫 적중 클립이라 로캘 형제(같은 제목 en/es 등) 중 엉뚱한 채널 변형을 고를 수 있음 → 가족 안 재라우팅
            if (clip is not null && tier == PlutoYouTubeLinkTier.ExactLocal)
            {
                snapshot ??= await _catalog.GetRecentClipsAsync(CandidateScanCount, ct).ConfigureAwait(false);
                var (routed, why) = RouteExactTitleToLocale(clip, row, snapshot);
                if (routed is null)
                {
                    results[i] = new PlutoYouTubeLinkResult(null, PlutoYouTubeLinkTier.Unmatched, 0, why);
                    continue;
                }
                clip = routed;
            }
            // 정확 제목 일치라도 이미 다른 YouTube ID에 연결된 클립이면 거부(재업로드 · 동명 영상)
            if (clip is not null && tier == PlutoYouTubeLinkTier.ExactLocal && clip.YouTubeVideoId is { } bound && row.YouTubeVideoId is { } rowYt
                && !string.Equals(bound, rowYt, StringComparison.Ordinal))
            {
                results[i] = new PlutoYouTubeLinkResult(null, PlutoYouTubeLinkTier.Unmatched, 0, "제목 일치 클립이 다른 YouTube ID에 연결됨");
                continue;
            }
            if (clip is not null && claimed.Add(clip.VideoId))
            {
                var newly = false;
                if (tier == PlutoYouTubeLinkTier.ExactLocal && clip.YouTubeVideoId is null && row.YouTubeVideoId is { } y2)
                {
                    var bind = await _catalog.TryBindYouTubeVideoIdAsync(clip.VideoId, y2, ct).ConfigureAwait(false);
                    newly = bind == PlutoYouTubeBindResult.Bound;
                    if (newly) clip = clip with { YouTubeVideoId = y2 };
                }
                results[i] = new PlutoYouTubeLinkResult(clip, tier, 1.0, null) { NewlyBound = newly };
            }
            else if (clip is not null) results[i] = new PlutoYouTubeLinkResult(null, PlutoYouTubeLinkTier.Unmatched, 0, "같은 내보내기의 다른 행이 이미 연결");
            else pendingFuzzy.Add(i);
        }

        if (pendingFuzzy.Count == 0) return results;
        var clips = await _catalog.GetRecentClipsAsync(CandidateScanCount, ct).ConfigureAwait(false);   // 바인딩 반영 최신본(1차 스냅샷 재사용 금지)
        foreach (var i in pendingFuzzy)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            if (row.YouTubeVideoId is null)
            {
                results[i] = new PlutoYouTubeLinkResult(null, PlutoYouTubeLinkTier.Unmatched, 0, "YouTube ID 없음");   // 연결 영속 불가 → 추측 금지
                continue;
            }
            var (cand, score, reason) = SelectFuzzyCandidate(row, clips, claimed);
            if (cand is null) { results[i] = new PlutoYouTubeLinkResult(null, PlutoYouTubeLinkTier.Unmatched, score, reason); continue; }
            var bind = await _catalog.TryBindYouTubeVideoIdAsync(cand.VideoId, row.YouTubeVideoId, ct).ConfigureAwait(false);
            if (bind is PlutoYouTubeBindResult.Bound or PlutoYouTubeBindResult.AlreadyBound)
            {
                claimed.Add(cand.VideoId);
                results[i] = new PlutoYouTubeLinkResult(cand with { YouTubeVideoId = row.YouTubeVideoId }, PlutoYouTubeLinkTier.Fuzzy, score, null) { NewlyBound = bind == PlutoYouTubeBindResult.Bound };
            }
            else results[i] = new PlutoYouTubeLinkResult(null, PlutoYouTubeLinkTier.Unmatched, score, "연결 충돌(" + bind.ToString() + ")");
        }
        return results;
    }
}
