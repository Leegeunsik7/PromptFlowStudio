using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📋 Step 24 — 업로드 패키지 생성 요청(활성 렌더 기준).</summary>
/// <param name="Theme">시리즈 테마 또는 영상 주제.</param>
/// <param name="CatalogTitle">렌더 카탈로그에 기록된 제목(게시 후 자동 연결 기준 · null → <paramref name="Theme"/>).</param>
/// <param name="EpisodeNumber">시리즈 편 번호(비시리즈 → 0).</param>
/// <param name="TotalEpisodes">시리즈 총 편수(비시리즈 → 0).</param>
/// <param name="HookStyle">렌더 훅 스타일 · 첫 자막.</param>
/// <param name="StyleTags">렌더 스타일 태그.</param>
public sealed record PlutoPublishPackRequest(string Theme, string? CatalogTitle, int EpisodeNumber, int TotalEpisodes, string? HookStyle, IReadOnlyList<string>? StyleTags)
{
    /// <summary>Shorts(<c>#shorts</c> 포함 · 기본) / 일반 영상.</summary>
    public bool IsShorts { get; init; } = true;
    public bool IsSeries => EpisodeNumber >= 1 && TotalEpisodes >= 2 && EpisodeNumber <= TotalEpisodes;
}

/// <summary>📋 A/B 제목 후보 1개 — 카탈로그 제목과의 연결 유사도(게시 후 자동 연결 예측) 포함.</summary>
public sealed record PlutoPublishTitleCandidate(string Variant, string Title, double LinkabilityScore)
{
    /// <summary>게시 후 YouTube Studio 내보내기가 Tier 2로 자동 연결될 만큼 유사.</summary>
    public bool AutoLinkable => LinkabilityScore >= PlutoYouTubeCatalogLinker.MinFuzzyScore;
}

/// <summary>📋 Step 24 — 붙여넣기용 YouTube 업로드 메타데이터 패키지.</summary>
public sealed record PlutoYouTubePublishPack(PlutoPublishTitleCandidate TitleA, PlutoPublishTitleCandidate TitleB, string Description, string PinnedComment,
    IReadOnlyList<string> Hashtags, IReadOnlyList<string> Tags, IReadOnlyList<string> BlockedRemoved, string HookLine, bool IsKorean)
{
    /// <summary>클립보드 1회 복사용 전체 텍스트.</summary>
    public string ToClipboardText()
    {
        var sb = new StringBuilder(512);
        sb.Append("[제목 A] ").Append(TitleA.Title).Append('\n');
        sb.Append("[제목 B] ").Append(TitleB.Title).Append("\n\n");
        sb.Append("[설명]\n").Append(Description).Append("\n\n");
        sb.Append("[고정 댓글]\n").Append(PinnedComment).Append("\n\n");
        sb.Append("[태그] ").Append(string.Join(", ", Tags));
        return sb.ToString();
    }

    /// <summary>UI 1줄 요약.</summary>
    public string SummaryKorean => "📋 A: " + TitleA.Title + " · B: " + TitleB.Title + " · " + string.Join(" ", Hashtags)
        + (BlockedRemoved.Count > 0 ? " · 🛡️ 오답 노트 제외 " + BlockedRemoved.Count.ToString(CultureInfo.InvariantCulture) + "개" : "");
}

/// <summary>📋 Step 24 — 게시 전 YouTube 업로드 메타데이터(A/B 제목 · 훅 정렬 설명 · 고정 댓글 CTA · 파레토 해시태그) 생성기.</summary>
public interface IPlutoYouTubePublishPackService
{
    /// <summary>메모리 기준 동기 생성(결정론).</summary>
    PlutoYouTubePublishPack Generate(PlutoPublishPackRequest request);

    /// <summary>랭커 · 오답 노트 · 시리즈 아크 선적재 후 생성(적재 실패 → [부분 구제] 메모리 기준 · 취소만 전파).</summary>
    Task<PlutoYouTubePublishPack> GenerateAsync(PlutoPublishPackRequest request, CancellationToken ct = default);
}

/// <summary>
/// 📋 <see cref="IPlutoYouTubePublishPackService"/> 순수 BCL 구현.
///   ▸ 제목 A(정보형) · B(호기심형) — 둘 다 <c>n/N편</c> 토큰 보존(링커 편 가드 · 시리즈 아크 키) · 100자 상한(토큰은 절대 자르지 않음).
///   ▸ 🔗 연결성 자가 검사: 후보 제목이 카탈로그 제목과의 <see cref="PlutoYouTubeCatalogLinker.TitleSimilarity"/> 하한 미만이면
///     카탈로그 제목 중심 안전 형태로 되돌려 게시 후 내보내기가 미매칭 대기열로 빠지지 않게 한다.
///   ▸ 훅 · 태그는 파레토/학습 랭킹 순 · 오답 노트 차단 토큰은 제외(제외 목록 보고). 해시태그 ≤ <see cref="MaxHashtags"/>개(YouTube 15개 초과 시 전부 무시됨).
/// </summary>
public sealed class PlutoYouTubePublishPackService : IPlutoYouTubePublishPackService
{
    public const int MaxTitleLength = 100;
    public const int MaxHashtags = 8;
    public const int MaxTagsTotalChars = 450;   // YouTube 태그 필드 500자 여유
    public const int MaxHookPhraseLength = 40;

    private readonly IPlutoAdaptivePatternRanker? _ranker;
    private readonly IPlutoSeriesArcMemoryService? _arc;
    private readonly IPlutoMistakeNotebookService? _notebook;

    public PlutoYouTubePublishPackService(IPlutoAdaptivePatternRanker? ranker = null, IPlutoSeriesArcMemoryService? arc = null, IPlutoMistakeNotebookService? notebook = null)
    {
        _ranker = ranker;
        _arc = arc;
        _notebook = notebook;
    }

    public async Task<PlutoYouTubePublishPack> GenerateAsync(PlutoPublishPackRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (_notebook is not null) await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_ranker is not null) await _ranker.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_arc is not null) await _arc.EnsureLoadedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] preload ([부분 구제]): " + ex.Message); }
        return Generate(request);
    }

    public PlutoYouTubePublishPack Generate(PlutoPublishPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var theme = Clean(request.Theme);
        var catalogTitle = Clean(request.CatalogTitle);
        if (theme.Length == 0) theme = catalogTitle.Length > 0 ? PlutoSeriesArcMemoryService.SeriesThemeOf(catalogTitle) : "Pluto";
        if (catalogTitle.Length == 0) catalogTitle = theme;
        var korean = ContainsHangul(theme) || ContainsHangul(catalogTitle);
        var suppressed = SafeSuppressed();
        var blocked = new List<string>();

        // ── 훅: 요청 훅 → (차단 시) 파레토/학습 1위 훅 → 기본 문구 ──
        var hook = Clean(request.HookStyle);
        if (hook.Length > 0 && IsBlocked(hook, suppressed)) { blocked.Add(hook); hook = string.Empty; }
        if (hook.Length == 0 || IsGenericHookKey(hook)) hook = TopRankedHook(suppressed) ?? string.Empty;
        var directive = SafeDirective(theme, request);
        if (directive.EscalateHook && directive.RecommendedHookLine.Length > 0 && !IsBlocked(directive.RecommendedHookLine, suppressed))
            hook = directive.RecommendedHookLine;   // 📺 편 간 이탈 → 아크 메모리 강화 훅 우선
        var hookPhrase = HookPhrase(hook, korean);

        // ── 제목 A/B ──
        var token = request.IsSeries ? EpisodeToken(request.EpisodeNumber, request.TotalEpisodes) : string.Empty;
        var core = StripEpisodeToken(catalogTitle);
        if (core.Length == 0) core = StripEpisodeToken(theme);
        var titleA = ComposeTitle(core, token, hookPhrase, curiosity: false, korean);
        var titleB = ComposeTitle(core, token, hookPhrase, curiosity: true, korean);
        var a = EnsureLinkable("A", titleA, catalogTitle, core, token);
        var b = EnsureLinkable("B", titleB, catalogTitle, core, token);
        if (string.Equals(a.Title, b.Title, StringComparison.Ordinal))   // A/B가 같아지면 실험 불가 → B를 질문형 접미로 차별화
            b = EnsureLinkable("B", FitTitle(core + (korean ? " — 결말 예상?" : " — guess the ending?"), token), catalogTitle, core, token);

        // ── 해시태그 · 태그 ──
        var tagCandidates = new List<string>();
        if (request.StyleTags is { } st) foreach (var t in st) tagCandidates.Add(t);
        foreach (var lt in ParetoTagLeaders()) tagCandidates.Add(lt);
        foreach (var t in PlutoYouTubeCatalogLinker.Tokenize(theme)) if (t.Length >= 2) tagCandidates.Add(t);
        var ranked = RankTagsSafe(tagCandidates);
        var hashtags = new List<string>(MaxHashtags);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.IsShorts) { hashtags.Add("#shorts"); seen.Add("shorts"); }
        var seriesTag = request.IsSeries ? HashtagBody(StripEpisodeToken(theme)) : string.Empty;
        if (seriesTag.Length > 0 && seen.Add(seriesTag)) hashtags.Add("#" + seriesTag);
        foreach (var raw in ranked)
        {
            if (hashtags.Count >= MaxHashtags) break;
            if (IsBlocked(raw, suppressed)) { if (!ContainsIgnoreCase(blocked, raw)) blocked.Add(raw); continue; }
            var body = HashtagBody(raw);
            if (body.Length < 2 || !seen.Add(body)) continue;
            hashtags.Add("#" + body);
        }
        var tags = new List<string>();
        var total = 0;
        foreach (var h in hashtags)
        {
            var t = h.TrimStart('#');
            if (total + t.Length + 1 > MaxTagsTotalChars) break;
            tags.Add(t);
            total += t.Length + 1;
        }
        if (core.Length > 0 && total + core.Length + 1 <= MaxTagsTotalChars && !ContainsIgnoreCase(tags, core)) tags.Insert(0, core);

        var description = BuildDescription(request, a.Title, hookPhrase, directive, hashtags, korean);
        var pinned = BuildPinnedComment(request, korean);
        return new PlutoYouTubePublishPack(a, b, description, pinned, hashtags, tags, blocked, hookPhrase, korean);
    }

    // ── 순수 함수 ──

    /// <summary><c>n/N편</c> 토큰(링커 정규식 · 아크 메모리 파서 공통 형식).</summary>
    public static string EpisodeToken(int n, int total) => n.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + "편";

    public static bool ContainsHangul(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s) if (ch is >= '가' and <= '힣') return true;
        return false;
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsControl(ch) || ch is '<' or '>') continue;   // YouTube 제목/설명 금지 문자
            if (char.IsWhiteSpace(ch)) { if (!space) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>편 표기(<c>2/3편</c> · <c>[2/3편]</c>) 제거 후 정리.</summary>
    public static string StripEpisodeToken(string? title)
    {
        var s = Clean(title);
        if (s.Length == 0) return s;
        var theme = PlutoSeriesArcMemoryService.SeriesThemeOf(s);
        if (!string.Equals(theme, s, StringComparison.Ordinal)) return theme;
        // 테마 추출이 실패한 경우에도 남은 "n/N편"을 직접 제거
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var j = i;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            if (j > i && j < s.Length && s[j] == '/')
            {
                var k = j + 1;
                while (k < s.Length && char.IsAsciiDigit(s[k])) k++;
                if (k > j + 1 && k < s.Length && s[k] == '편') { i = k; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Trim().Trim('[', ']', '(', ')', '·', '-', '—').Trim();
    }

    private static bool IsGenericHookKey(string hook)
        => hook.Equals("montage-hook", StringComparison.OrdinalIgnoreCase) || hook.Equals("훅", StringComparison.Ordinal) || hook.Equals("hook", StringComparison.OrdinalIgnoreCase);

    /// <summary>훅 키/자막 → 제목 문구(최대 <see cref="MaxHookPhraseLength"/>자 · 키 형태는 로캘 문구로 매핑).</summary>
    public static string HookPhrase(string? hook, bool korean)
    {
        var h = Clean(hook);
        if (h.Length == 0 || IsGenericHookKey(h)) return korean ? "끝까지 보면 반전" : "Wait for the ending";
        if (h.IndexOf(' ') < 0 && (h.IndexOf('-') > 0 || h.IndexOf('_') > 0))
        {
            var k = h.ToLowerInvariant();
            if (k.Contains("question", StringComparison.Ordinal)) return korean ? "이거 알고 있었나요?" : "Did you know this?";
            if (k.Contains("countdown", StringComparison.Ordinal)) return korean ? "3초 뒤 놀라운 장면" : "In 3 seconds…";
            if (k.Contains("reveal", StringComparison.Ordinal) || k.Contains("twist", StringComparison.Ordinal)) return korean ? "마지막 장면 주목" : "Watch the last scene";
            if (k.Contains("pov", StringComparison.Ordinal)) return "POV";
            h = h.Replace('-', ' ').Replace('_', ' ');
        }
        return h.Length > MaxHookPhraseLength ? h[..MaxHookPhraseLength].TrimEnd() + "…" : h;
    }

    private static string ComposeTitle(string core, string token, string hookPhrase, bool curiosity, bool korean)
    {
        // A(정보형): "{core} {n/N편} · {hook}"  ·  B(호기심형): "{hook} | {core} {n/N편}"
        var main = token.Length > 0 ? core + " " + token : core;
        var full = curiosity ? hookPhrase + " | " + main : main + " · " + hookPhrase;
        if (full.Length <= MaxTitleLength) return full;
        return FitTitle(core, token);   // 길면 훅 문구를 버리고 연결 핵심(제목 + 편 토큰)만 유지
    }

    /// <summary>제목 + 편 토큰을 100자에 맞춤 — 토큰은 절대 자르지 않고 제목을 줄인다.</summary>
    public static string FitTitle(string core, string token)
    {
        var suffix = token.Length > 0 ? " " + token : string.Empty;
        var room = MaxTitleLength - suffix.Length;
        var c = core.Length > room ? core[..Math.Max(0, room - 1)].TrimEnd() + "…" : core;
        return (c + suffix).Trim();
    }

    private static PlutoPublishTitleCandidate EnsureLinkable(string variant, string title, string catalogTitle, string core, string token)
    {
        var score = PlutoYouTubeCatalogLinker.TitleSimilarity(title, catalogTitle);
        if (score >= PlutoYouTubeCatalogLinker.MinFuzzyScore) return new PlutoPublishTitleCandidate(variant, title, score);
        // 🔗 연결성 미달 → 카탈로그 제목 중심 안전 형태(훅 문구가 Dice 점수를 희석해 자동 연결이 끊기는 것을 방지)
        var safe = FitTitle(core, token);
        var safeScore = PlutoYouTubeCatalogLinker.TitleSimilarity(safe, catalogTitle);
        return safeScore > score ? new PlutoPublishTitleCandidate(variant, safe, safeScore) : new PlutoPublishTitleCandidate(variant, title, score);
    }

    /// <summary>해시태그 본문 — 문자/숫자/밑줄만(공백 · 기호 제거) · 최대 30자.</summary>
    public static string HashtagBody(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        var colon = s.LastIndexOf(':');
        if (colon >= 0 && colon < s.Length - 1) s = s[(colon + 1)..];   // "style:xxx" 학습 키 접두 제거
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.IsUpper(ch) ? char.ToLowerInvariant(ch) : ch);
            if (sb.Length >= 30) break;
        }
        return sb.ToString();
    }

    private static bool ContainsIgnoreCase(List<string> list, string v)
    {
        foreach (var x in list) if (string.Equals(x, v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>오답 노트 차단 판정 — 원문 또는 해시태그 정규형이 차단 토큰과 일치.</summary>
    private static bool IsBlocked(string token, IReadOnlySet<string> suppressed)
    {
        if (suppressed.Count == 0) return false;
        if (suppressed.Contains(token.Trim())) return true;
        var body = HashtagBody(token);
        if (body.Length == 0) return false;
        foreach (var s in suppressed) if (string.Equals(HashtagBody(s), body, StringComparison.Ordinal)) return true;
        return false;
    }

    private IReadOnlySet<string> SafeSuppressed()
    {
        try
        {
            if (_notebook?.GetSuppressedTokens() is { } set)
            {
                var copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in set) if (!string.IsNullOrWhiteSpace(s)) copy.Add(s.Trim());
                return copy;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] notebook: " + ex.Message); }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private string? TopRankedHook(IReadOnlySet<string> suppressed)
    {
        try
        {
            if (_ranker is null) return null;
            foreach (var m in _ranker.GetParetoModalityStatus())
                if (m.Kind == PlutoPatternKind.Hook && m.Leader is { } l && !IsBlocked(l.Label, suppressed)) return l.Label;
            foreach (var o in _ranker.RankOptions(PlutoPatternKind.Hook))
                if (!o.IsPenalized && !IsBlocked(o.DisplayLabel, suppressed)) return o.DisplayLabel;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] hook rank: " + ex.Message); }
        return null;
    }

    private List<string> ParetoTagLeaders()
    {
        var list = new List<string>(1);
        try
        {
            if (_ranker is null) return list;
            foreach (var m in _ranker.GetParetoModalityStatus())
                if (m.Kind == PlutoPatternKind.StyleTag && m.Leader is { } l && !l.Label.Contains(' ', StringComparison.Ordinal)) list.Add(l.Label);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] pareto: " + ex.Message); }
        return list;
    }

    private IReadOnlyList<string> RankTagsSafe(List<string> candidates)
    {
        var dedup = new List<string>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            var t = Clean(c);
            if (t.Length > 0 && seen.Add(HashtagBody(t))) dedup.Add(t);
        }
        try
        {
            if (_ranker is not null)
            {
                var ranked = _ranker.RankTags(dedup, dedup.Count);
                if (ranked.Count > 0)
                {
                    // 랭커는 학습 라벨로 돌려줄 수 있음 → 순위만 취하고 누락 후보는 원래 순서로 뒤에 붙임
                    var result = new List<string>(dedup.Count);
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in ranked) if (used.Add(HashtagBody(r))) result.Add(r);
                    foreach (var d in dedup) if (used.Add(HashtagBody(d))) result.Add(d);
                    return result;
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] tag rank: " + ex.Message); }
        return dedup;
    }

    private PlutoSeriesContinuityDirective SafeDirective(string theme, PlutoPublishPackRequest request)
    {
        if (_arc is null || !request.IsSeries) return PlutoSeriesContinuityDirective.None;
        try { return _arc.GetContinuityDirective(theme, request.TotalEpisodes, request.EpisodeNumber) ?? PlutoSeriesContinuityDirective.None; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] arc: " + ex.Message); return PlutoSeriesContinuityDirective.None; }
    }

    private static string BuildDescription(PlutoPublishPackRequest r, string title, string hookPhrase, PlutoSeriesContinuityDirective d, List<string> hashtags, bool korean)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(400);
        sb.Append(hookPhrase).Append('\n');   // 첫 줄 = 훅(검색 · 피드 미리보기 노출)
        sb.Append(title).Append("\n\n");
        if (r.IsSeries)
        {
            if (korean)
            {
                sb.Append("📺 시리즈 ").Append(r.EpisodeNumber.ToString(inv)).Append('/').Append(r.TotalEpisodes.ToString(inv)).Append("편");
                if (r.EpisodeNumber > 1) sb.Append(" — ").Append((r.EpisodeNumber - 1).ToString(inv)).Append("편에서 이어집니다");
                sb.Append('\n');
                if (r.EpisodeNumber < r.TotalEpisodes)
                    sb.Append("▶ 다음 ").Append((r.EpisodeNumber + 1).ToString(inv)).Append("편: ").Append(d.CliffhangerOutroLine.Length > 0 ? d.CliffhangerOutroLine : "이야기는 계속됩니다").Append('\n');
                else sb.Append("🏁 시리즈 완결편\n");
            }
            else
            {
                sb.Append("📺 Series part ").Append(r.EpisodeNumber.ToString(inv)).Append(" of ").Append(r.TotalEpisodes.ToString(inv)).Append('\n');
                if (r.EpisodeNumber < r.TotalEpisodes)
                    sb.Append("▶ Next: part ").Append((r.EpisodeNumber + 1).ToString(inv)).Append(d.CliffhangerOutroLine.Length > 0 ? " — " + d.CliffhangerOutroLine : "").Append('\n');
                else sb.Append("🏁 Series finale\n");
            }
            sb.Append('\n');
        }
        sb.Append(korean ? "👍 좋아요 · 🔔 구독으로 다음 영상을 받아보세요" : "👍 Like & 🔔 subscribe for the next one").Append("\n\n");
        sb.Append(string.Join(" ", hashtags));
        return sb.ToString();
    }

    private static string BuildPinnedComment(PlutoPublishPackRequest r, bool korean)
    {
        var inv = CultureInfo.InvariantCulture;
        if (r.IsSeries && r.EpisodeNumber < r.TotalEpisodes)
            return korean
                ? "📌 " + (r.EpisodeNumber + 1).ToString(inv) + "편이 곧 올라옵니다 — 구독 + 🔔 알림 켜고 놓치지 마세요! 이번 편 몇 점인가요? 👇"
                : "📌 Part " + (r.EpisodeNumber + 1).ToString(inv) + " drops soon — subscribe + 🔔 so you don't miss it! Rate this part 👇";
        if (r.IsSeries)
            return korean ? "📌 시리즈 완결! 다음 시리즈 주제를 댓글로 추천해 주세요 👇" : "📌 Series complete! Suggest the next series in the comments 👇";
        return korean ? "📌 끝까지 본 분들, 가장 좋았던 장면을 댓글로 알려주세요 👇" : "📌 Made it to the end? Tell me your favorite moment 👇";
    }
}
