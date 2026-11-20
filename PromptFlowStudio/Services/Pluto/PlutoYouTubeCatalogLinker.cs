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

    private static readonly Regex EpisodeRx = new(@"(\d{1,3})\s*/\s*(\d{1,3})\s*편|\b(?:ep|part|episode)\.?\s*(\d{1,3})|제?\s*(\d{1,3})\s*편",
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
            if (!m.Success) return 0;
            for (var g = 1; g <= 4; g++)
            {
                if (g == 2 || !m.Groups[g].Success) continue;
                return int.TryParse(m.Groups[g].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
            }
            return 0;
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
        PlutoRenderedClipRecord? best = null;
        double bestScore = 0, second = 0;
        foreach (var c in clips)
        {
            if (c.YouTubeVideoId is not null) continue;   // 이미 다른 영상에 연결됨
            if (excludedClipIds is not null && excludedClipIds.Contains(c.VideoId)) continue;
            if (!InPublishWindow(c.RenderedAtUtc, published)) continue;
            var sim = TitleSimilarity(row.Title, c.Title);
            if (sim < MinFuzzyScore) continue;
            var days = Math.Max(0.0, (published - c.RenderedAtUtc).TotalDays);
            var score = sim + 0.05 * (1.0 - Math.Min(1.0, days / PublishWindowDays));
            if (score > bestScore) { second = bestScore; bestScore = score; best = c; }
            else if (score > second) second = score;
        }
        if (best is null) return (null, 0, "유사 제목/게시 창 후보 없음");
        if (second > 0 && bestScore - second < AmbiguityMargin) return (null, bestScore, "모호(후보 2개 이상)");
        return (best, bestScore, null);
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
        var clips = await _catalog.GetRecentClipsAsync(CandidateScanCount, ct).ConfigureAwait(false);
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
