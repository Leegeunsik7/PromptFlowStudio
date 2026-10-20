using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PromptFlowStudio.Services.Mam;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Newsroom;

public enum FeedKind : byte { Rss, Api, Social, Webhook, Wire }

public enum NewsGrade : byte { Normal, Urgent, Breaking, Exclusive }

public enum FactVerdict : byte { Verified, Likely, Unverified, Disputed, False }

[Flags]
public enum FactFlag : ushort { None = 0, SingleSource = 1, SocialOnly = 2, Sensational = 4, NumberConflict = 8, LowReputation = 16, LlmFlagged = 32, Stale = 64, Contradiction = 128 }

public sealed record FeedSource(string Name, string Url, FeedKind Kind, float Reputation, int IntervalSec = 60);

public sealed class NewsItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Source { get; init; } = "";
    public FeedKind Kind { get; init; }
    public float Reputation { get; init; } = 0.5f;
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Url { get; init; }
    public string Category { get; init; } = "종합";
    public string? Location { get; init; }
    public DateTimeOffset Published { get; init; } = DateTimeOffset.UtcNow;
    public ulong SimHash { get; set; }
    public int Cluster { get; set; } = -1;
}

/// <summary>스토리 클러스터(교차 검증 단위) — 대표 기사 · 구성 기사 · 점수/판정/등급.</summary>
public sealed class NewsStory
{
    public int Id { get; init; }
    public List<NewsItem> Items { get; } = new();
    public NewsItem Lead => Items[0];
    public int Score { get; set; }
    public int AlgoScore { get; set; }
    public int? LlmScore { get; set; }
    public string? LlmReason { get; set; }
    public FactVerdict Verdict { get; set; } = FactVerdict.Unverified;
    public FactFlag Flags { get; set; }
    public NewsGrade Grade { get; set; }
    public int IndependentSources { get; set; }
    public List<double> Figures { get; } = new();
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>점수 산출 입력(값형).</summary>
public readonly record struct FactFeature(int SourceId, float Reputation, FeedKind Kind, float Sensational, double AgeMinutes, bool Negation);

/// <summary>
/// [PromptFlow Studio v16.44.0] 📡 실시간 속보 피드 수집기 &amp; AI 팩트체크 엔진.
///   ▸ 수집: RSS 2.0/Atom(XDocument) · JSON API(items/articles 배열) · 소셜/와이어 · 웹훅(루프백 POST /webhook) — 소스별 폴링 주기 · 타임아웃 · 지수 백오프 · 스키마 검증.
///   ▸ 클러스터링: 제목+요약 SimHash(64bit) 해밍 ≤ 12 → 동일 스토리(무할당 스캔) · URL/제목 정확 중복 차단(alreadyRegistered).
///   ▸ 교차 검증 점수(무할당 코어): 독립 출처 수(평판 가중) · 와이어/공신력 가산 · 단일/소셜 전용 감점 · 선정성 어휘 · 수치 주장 충돌(±20%) · 부정/반박 보도 · 신선도 → 0~100.
///   ▸ Gemini(설정 시): JSON {score,verdict,reason} 스키마 검증 → 70:30 블렌드(데드라인 4초 · 실패 시 BCL 단독). 판정 Verified/Likely/Unverified/Disputed/False · 등급 Normal/Urgent/Breaking/Exclusive.
/// </summary>
public sealed class RealtimeNewsFeedFactCheckEngine : IDisposable
{
    public const double ClusterOverlap = 0.5;
    public const int ClusterHamming = 12, MaxItems = 5000, MaxFigures = 16, DefaultWebhookPort = 17865;
    public static readonly string[] SensationalWords = { "충격", "경악", "발칵", "소름", "대박", "결국", "shocking", "unbelievable", "you won't believe", "exposed", "!!!" };
    public static readonly string[] UrgentWords = { "속보", "긴급", "breaking", "urgent", "just in", "flash" };
    public static readonly string[] ExclusiveWords = { "단독", "exclusive", "특종" };
    public static readonly string[] NegationWords = { "사실무근", "사실 아님", "가짜", "허위", "부인", "오보", "debunked", "false claim", "denied", "hoax", "fake" };

    private readonly object _gate = new();
    private readonly List<FeedSource> _sources = new();
    private readonly List<NewsItem> _items = new();
    private readonly List<NewsStory> _stories = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private ulong[] _storyHash = new ulong[256];
    private readonly List<HashSet<int>> _storyGrams = new();
    private readonly Dictionary<string, (int Fails, long Until)> _backoff = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, Task<string?>>? _llm;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private long _fetched, _duplicates, _webhooks, _llmCalls;

    public RealtimeNewsFeedFactCheckEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(GeminiClient)) is GeminiClient g ? async (p, ct) => { if (!g.Enabled) return null; var r = await g.GenerateAsync(p, null, 0.2, ct).ConfigureAwait(false); return r.Ok ? r.Text : null; } : null, null) { }

    public RealtimeNewsFeedFactCheckEngine(Func<string, CancellationToken, Task<string?>>? llm, HttpMessageHandler? handler)
    {
        _llm = llm;
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public event Action? OnChange;
    public event Action<NewsStory>? OnStory;

    public IReadOnlyList<FeedSource> Sources { get { lock (_gate) return _sources.ToArray(); } }
    public IReadOnlyList<NewsStory> Stories { get { lock (_gate) { var l = new List<NewsStory>(_stories); l.Sort((a, b) => b.Updated.CompareTo(a.Updated)); return l; } } }
    public int ItemCount { get { lock (_gate) return _items.Count; } }
    public long Fetched => Interlocked.Read(ref _fetched);
    public long Duplicates => Interlocked.Read(ref _duplicates);
    public long Webhooks => Interlocked.Read(ref _webhooks);
    public long LlmCalls => Interlocked.Read(ref _llmCalls);
    public bool IsRunning => _cts is not null;
    public bool LlmAvailable => _llm is not null;
    public bool UseLlm { get; set; } = true;
    public int WebhookPort { get; private set; }
    public int LlmDeadlineMs { get; set; } = 4000;
    public string? LastAction { get; private set; }

    public void AddSource(FeedSource s) { lock (_gate) { _sources.RemoveAll(x => x.Name == s.Name); _sources.Add(s); } Raise(); }

    // ══════════════════════ 파싱 ══════════════════════

    /// <summary>RSS 2.0 / Atom → 기사(태그/엔티티 제거 · 관용적).</summary>
    public static List<NewsItem> ParseFeed(string xml, FeedSource src)
    {
        var list = new List<NewsItem>();
        var doc = XDocument.Parse(xml);
        foreach (var e in doc.Descendants())
        {
            var ln = e.Name.LocalName;
            if (ln is not ("item" or "entry")) continue;
            string? Val(string n) { foreach (var c in e.Elements()) if (c.Name.LocalName == n) return c.Value; return null; }
            var title = Clean(Val("title"));
            if (title.Length == 0) continue;
            var link = Val("link");
            if (string.IsNullOrWhiteSpace(link)) foreach (var c in e.Elements()) if (c.Name.LocalName == "link") { link = (string?)c.Attribute("href"); break; }
            var date = Val("pubDate") ?? Val("published") ?? Val("updated");
            list.Add(new NewsItem
            {
                Source = src.Name, Kind = src.Kind, Reputation = src.Reputation, Title = title, Summary = Clean(Val("description") ?? Val("summary") ?? Val("content")),
                Url = link?.Trim(), Category = Clean(Val("category")) is { Length: > 0 } cat ? cat : "종합", Published = ParseDate(date),
            });
        }
        return list;
    }

    /// <summary>JSON API(items/articles/data 배열 · title 필수) → 기사. 스키마 위반 시 FormatException.</summary>
    public static List<NewsItem> ParseJson(string json, FeedSource src)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement arr = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("items", out var a) || root.TryGetProperty("articles", out a) || root.TryGetProperty("data", out a)) arr = a;
            else arr = default;
        }
        if (arr.ValueKind != JsonValueKind.Array) throw new FormatException("뉴스 API 스키마 불일치(items/articles 배열 없음)");
        var list = new List<NewsItem>();
        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object || !it.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
            string? S(string n) => it.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            list.Add(new NewsItem
            {
                Source = S("source") ?? src.Name, Kind = src.Kind, Reputation = src.Reputation, Title = Clean(t.GetString()), Summary = Clean(S("summary") ?? S("description") ?? S("text")),
                Url = S("url"), Category = S("category") ?? "종합", Location = S("location"), Published = ParseDate(S("published") ?? S("publishedAt")),
            });
        }
        return list;
    }

    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var inTag = false;
        foreach (var ch in s) { if (ch == '<') inTag = true; else if (ch == '>') inTag = false; else if (!inTag) sb.Append(ch); }
        return string.Join(' ', WebUtility.HtmlDecode(sb.ToString()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static DateTimeOffset ParseDate(string? s)
        => s is not null && DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.UtcNow;

    // ══════════════════════ 수집 · 클러스터 ══════════════════════

    /// <summary>기사 수집 → 중복 차단 → 클러스터 배정 → 재채점. 반환 = 신규 수.</summary>
    public int Ingest(IEnumerable<NewsItem> items)
    {
        var touched = new HashSet<NewsStory>();
        var added = 0;
        lock (_gate)
        {
            foreach (var it in items)
            {
                var key = (it.Url is { Length: > 0 } u ? u : it.Source + "|" + it.Title).ToLowerInvariant();
                if (!_seen.Add(key)) { _duplicates++; continue; }
                it.SimHash = EnterpriseMamSmartIndexingEngine.SimHash(it.Title + " " + it.Summary);
                var grams = TitleGrams(it.Title);
                var c = NearestStory(it.SimHash, _storyHash.AsSpan(0, _stories.Count), ClusterHamming);
                if (c < 0)
                {
                    var bestOv = 0.0;
                    for (var k = 0; k < _stories.Count; k++)
                    {
                        var ov = Overlap(grams, _storyGrams[k]);
                        if (ov >= ClusterOverlap && ov > bestOv) { bestOv = ov; c = k; }
                    }
                }
                NewsStory st;
                if (c < 0)
                {
                    st = new NewsStory { Id = _stories.Count };
                    if (_storyHash.Length == _stories.Count) Array.Resize(ref _storyHash, _stories.Count * 2);
                    _storyHash[_stories.Count] = it.SimHash;
                    _stories.Add(st);
                    _storyGrams.Add(grams);
                }
                else st = _stories[c];
                it.Cluster = st.Id;
                st.Items.Add(it);
                st.Items.Sort((a, b) => b.Reputation.CompareTo(a.Reputation));
                st.Updated = DateTimeOffset.UtcNow;
                _items.Add(it);
                if (_items.Count > MaxItems) _items.RemoveRange(0, 500);
                touched.Add(st);
                added++;
            }
            foreach (var st in touched) Rescore(st);
        }
        Interlocked.Add(ref _fetched, added);
        foreach (var st in touched) { try { OnStory?.Invoke(st); } catch (Exception ex) { Program.Log("News.OnStory", ex); } }
        if (added > 0) { LastAction = "📡 수집 " + added.ToString(CultureInfo.InvariantCulture) + "건 · 스토리 " + _stories.Count.ToString(CultureInfo.InvariantCulture); Raise(); }
        return added;
    }

    /// <summary>제목 문자 바이그램(공백·기호 제거 · 대소문자 무시) 집합.</summary>
    public static HashSet<int> TitleGrams(string title)
    {
        var set = new HashSet<int>();
        char prev = ' ';
        foreach (var raw in title)
        {
            if (!char.IsLetterOrDigit(raw)) continue;
            var ch = char.ToLowerInvariant(raw);
            if (prev != ' ') set.Add(prev << 16 | ch);
            prev = ch;
        }
        return set;
    }

    /// <summary>중첩 계수 |A∩B| / min(|A|,|B|).</summary>
    public static double Overlap(HashSet<int> a, HashSet<int> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
        var hit = 0;
        foreach (var g in small) if (big.Contains(g)) hit++;
        return hit / (double)small.Count;
    }

    /// <summary>최근접 스토리(해밍 ≤ max) — 무할당.</summary>
    public static int NearestStory(ulong h, ReadOnlySpan<ulong> stories, int max)
    {
        var best = -1; var bd = max + 1;
        for (var i = 0; i < stories.Length; i++)
        {
            var d = BitOperations.PopCount(h ^ stories[i]);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // ══════════════════════ 팩트체크(무할당 코어) ══════════════════════

    public static float Sensationalism(ReadOnlySpan<char> text)
    {
        var hits = 0;
        foreach (var w in SensationalWords) if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) hits++;
        var excl = 0;
        foreach (var ch in text) if (ch == '!') excl++;
        return Math.Min(1f, hits * 0.35f + (excl >= 2 ? 0.2f : 0));
    }

    public static bool ContainsAny(ReadOnlySpan<char> text, string[] words)
    {
        foreach (var w in words) if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>수치 주장 추출(천 단위 구분 · 소수 · 한국어 단위 만/억 반영) → dst. 반환 = 개수. 무할당.</summary>
    public static int ExtractFigures(ReadOnlySpan<char> text, Span<double> dst)
    {
        var n = 0;
        var i = 0;
        while (i < text.Length && n < dst.Length)
        {
            if (!char.IsAsciiDigit(text[i])) { i++; continue; }
            double v = 0; double frac = 0; var div = 1.0; var dot = false;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == ',' || (text[i] == '.' && !dot && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))))
            {
                var ch = text[i++];
                if (ch == ',') continue;
                if (ch == '.') { dot = true; continue; }
                if (dot) { div *= 10; frac += (ch - '0') / div; } else v = v * 10 + (ch - '0');
            }
            v += frac;
            while (i < text.Length && text[i] == ' ') i++;
            if (i < text.Length) { if (text[i] == '만') v *= 1e4; else if (text[i] == '억') v *= 1e8; }
            if (v >= 2 && !(v >= 1900 && v <= 2100 && !dot)) dst[n++] = v;   // 연도·1 제외
        }
        return n;
    }

    /// <summary>
    /// 교차 검증 점수 0~100(무할당): 독립 출처(평판 가중 · 출처 ID 중복 제거) · 와이어 · 단일/소셜 · 선정성 · 반박 · 신선도 · 수치 충돌.
    /// figures = 기사별 수치(행 = 기사 · 행당 MaxFigures · counts = 행별 개수).
    /// </summary>
    public static int CrossVerify(ReadOnlySpan<FactFeature> f, ReadOnlySpan<double> figures, ReadOnlySpan<int> counts, out FactFlag flags, out int independent)
    {
        flags = FactFlag.None;
        independent = 0;
        if (f.IsEmpty) { flags = FactFlag.SingleSource; return 0; }
        Span<int> seen = stackalloc int[Math.Min(64, f.Length)];
        double repSum = 0; var social = 0; var wire = false; float sens = 0; var neg = 0; double age = double.MaxValue; float maxRep = 0;
        for (var i = 0; i < f.Length; i++)
        {
            var dup = false;
            for (var k = 0; k < independent; k++) if (seen[k] == f[i].SourceId) { dup = true; break; }
            if (!dup && independent < seen.Length) { seen[independent++] = f[i].SourceId; repSum += f[i].Reputation; }
            if (f[i].Kind == FeedKind.Social) social++;
            if (f[i].Kind == FeedKind.Wire && f[i].Reputation >= 0.8f) wire = true;
            sens = MathF.Max(sens, f[i].Sensational);
            if (f[i].Negation) neg++;
            age = Math.Min(age, f[i].AgeMinutes);
            maxRep = MathF.Max(maxRep, f[i].Reputation);
        }
        var score = 25.0 + Math.Min(45, repSum * 18) + (wire ? 12 : 0) + maxRep * 10;
        if (independent <= 1) { flags |= FactFlag.SingleSource; score -= 12; }
        if (social == f.Length) { flags |= FactFlag.SocialOnly; score -= 20; }
        if (maxRep < 0.4f) { flags |= FactFlag.LowReputation; score -= 10; }
        if (sens >= 0.3f) { flags |= FactFlag.Sensational; score -= 25 * sens; }
        if (neg > 0)
        {
            flags |= FactFlag.Contradiction;
            score -= neg * 2 >= f.Length ? 45 : 20;
        }
        if (age > 24 * 60) { flags |= FactFlag.Stale; score -= 5; }
        // 수치 충돌: 같은 순번 수치의 상대 편차 > 20%
        var rows = counts.Length;
        for (var a = 0; a < rows; a++)
            for (var b = a + 1; b < rows; b++)
            {
                var m = Math.Min(counts[a], counts[b]);
                for (var k = 0; k < m; k++)
                {
                    double x = figures[a * MaxFigures + k], y = figures[b * MaxFigures + k];
                    if (Math.Abs(x - y) > Math.Max(x, y) * 0.2) { flags |= FactFlag.NumberConflict; goto conflictDone; }
                }
            }
        conflictDone:
        if ((flags & FactFlag.NumberConflict) != 0) score -= 15;
        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }

    public static FactVerdict VerdictOf(int score, FactFlag flags)
        => (flags & FactFlag.Contradiction) != 0 && score < 40 ? FactVerdict.False
         : score >= 80 ? FactVerdict.Verified : score >= 60 ? FactVerdict.Likely
         : (flags & (FactFlag.NumberConflict | FactFlag.Contradiction)) != 0 ? FactVerdict.Disputed : score >= 25 ? FactVerdict.Unverified : FactVerdict.False;

    private void Rescore(NewsStory st)
    {
        var n = Math.Min(st.Items.Count, 64);
        Span<FactFeature> f = stackalloc FactFeature[n];
        Span<double> figs = stackalloc double[n * MaxFigures];
        Span<int> cnt = stackalloc int[n];
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < n; i++)
        {
            var it = st.Items[i];
            var text = it.Title + " " + it.Summary;
            f[i] = new FactFeature(SourceId(it.Source), it.Reputation, it.Kind, Sensationalism(text), (now - it.Published).TotalMinutes, ContainsAny(text, NegationWords));
            cnt[i] = ExtractFigures(text, figs.Slice(i * MaxFigures, MaxFigures));
        }
        st.AlgoScore = CrossVerify(f, figs, cnt, out var flags, out var indep);
        st.IndependentSources = indep;
        if (st.LlmScore is { } l && st.LlmReason is { } r && r.Length > 0 && (flags & FactFlag.LlmFlagged) == 0 && l < 40) flags |= FactFlag.LlmFlagged;
        st.Flags = flags;
        st.Score = st.LlmScore is { } ls ? (int)Math.Round(st.AlgoScore * 0.7 + ls * 0.3) : st.AlgoScore;
        st.Verdict = VerdictOf(st.Score, flags);
        st.Figures.Clear();
        for (var k = 0; k < cnt[0]; k++) st.Figures.Add(figs[k]);
        var lead = st.Lead.Title;
        st.Grade = ContainsAny(lead, ExclusiveWords) && st.Lead.Reputation >= 0.75f ? NewsGrade.Exclusive
                 : indep >= 3 && ContainsAny(lead, UrgentWords) || indep >= 4 && st.Score >= 70 ? NewsGrade.Breaking
                 : ContainsAny(lead, UrgentWords) ? NewsGrade.Urgent : NewsGrade.Normal;
    }

    private readonly Dictionary<string, int> _sourceIds = new(StringComparer.OrdinalIgnoreCase);
    private int SourceId(string s) { if (!_sourceIds.TryGetValue(s, out var id)) { id = _sourceIds.Count; _sourceIds[s] = id; } return id; }

    /// <summary>Gemini 2차 검증(스키마 검증 · 데드라인 · 실패 시 BCL 유지). 반환 = 적용 여부.</summary>
    public async Task<bool> LlmVerifyAsync(NewsStory st, CancellationToken ct = default)
    {
        if (_llm is null || !UseLlm) return false;
        var sb = new StringBuilder("You are a broadcast fact-check desk. Assess credibility of this breaking story from multiple reports. Reply ONLY JSON {\"score\":0-100,\"verdict\":\"verified|likely|unverified|disputed|false\",\"reason\":\"<=120 chars Korean\"}.\nReports:\n");
        foreach (var it in st.Items.Take(8)) sb.Append("- [").Append(it.Source).Append(" rep=").Append(it.Reputation.ToString("0.0", CultureInfo.InvariantCulture)).Append("] ").Append(it.Title).Append(" — ").Append(it.Summary.Length > 300 ? it.Summary[..300] : it.Summary).Append('\n');
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(LlmDeadlineMs);
        try
        {
            Interlocked.Increment(ref _llmCalls);
            var text = await _llm(sb.ToString(), d.Token).ConfigureAwait(false);
            if (!TryParseVerdict(text, out var score, out var reason)) return false;
            lock (_gate) { st.LlmScore = score; st.LlmReason = reason; Rescore(st); }
            LastAction = "🤖 Gemini 팩트체크 · " + st.Lead.Title + " · " + st.Score.ToString(CultureInfo.InvariantCulture) + "%";
            Raise();
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { Program.Log("News.Llm", ex); return false; }
    }

    public static bool TryParseVerdict(string? text, out int score, out string reason)
    {
        score = 0; reason = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = GeminiClient.StripFence(text);
        var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return false;
        try
        {
            using var doc = JsonDocument.Parse(s[a..(b + 1)]);
            var r = doc.RootElement;
            if (!r.TryGetProperty("score", out var sc) || sc.ValueKind != JsonValueKind.Number || !sc.TryGetInt32(out score) || score is < 0 or > 100) return false;
            if (!r.TryGetProperty("verdict", out var v) || v.ValueKind != JsonValueKind.String) return false;
            reason = r.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString()! : "";
            if (reason.Length > 200) reason = reason[..200];
            return true;
        }
        catch (JsonException) { return false; }
    }

    // ══════════════════════ 폴링 · 웹훅 ══════════════════════

    /// <summary>소스 1회 수집(백오프 존중 · 지수 백오프 갱신). 반환 = 신규 수(-1 = 백오프 중/실패).</summary>
    public async Task<int> PollAsync(FeedSource src, CancellationToken ct = default)
    {
        lock (_gate) if (_backoff.TryGetValue(src.Name, out var b) && Environment.TickCount64 < b.Until) return -1;
        try
        {
            var body = await _http.GetStringAsync(src.Url, ct).ConfigureAwait(false);
            var t = body.TrimStart();
            var items = t.StartsWith('<') ? ParseFeed(body, src) : ParseJson(body, src);
            lock (_gate) _backoff.Remove(src.Name);
            return Ingest(items);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or JsonException or System.Xml.XmlException && !ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                var fails = _backoff.TryGetValue(src.Name, out var b) ? b.Fails + 1 : 1;
                _backoff[src.Name] = (fails, Environment.TickCount64 + Math.Min(300_000, 1000L << Math.Min(fails, 8)));
            }
            LastAction = "⚠ 피드 실패(백오프) · " + src.Name + " · " + ex.GetType().Name;
            Raise();
            return -1;
        }
    }

    public bool InBackoff(string source) { lock (_gate) return _backoff.TryGetValue(source, out var b) && Environment.TickCount64 < b.Until; }

    /// <summary>폴링 루프 + 웹훅 수신 시작(webhookPort 0 = 임의 · -1 = 미사용).</summary>
    public void Start(int webhookPort = DefaultWebhookPort)
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        if (webhookPort >= 0)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, webhookPort);
                l.Start();
                _listener = l;
                WebhookPort = ((IPEndPoint)l.LocalEndpoint).Port;
                _ = AcceptLoopAsync(l, cts.Token);
            }
            catch (SocketException ex) { Program.Log("News.Webhook", ex); }
        }
        _ = PollLoopAsync(cts.Token);
        LastAction = "📡 뉴스 피드 수집 ON" + (WebhookPort > 0 ? " · 웹훅 127.0.0.1:" + WebhookPort.ToString(CultureInfo.InvariantCulture) : "");
        Raise();
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        LastAction = "⏹ 뉴스 피드 수집 OFF";
        Raise();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var next = new Dictionary<string, long>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            foreach (var s in Sources)
            {
                if (s.Kind == FeedKind.Webhook || string.IsNullOrWhiteSpace(s.Url)) continue;
                var now = Environment.TickCount64;
                if (next.TryGetValue(s.Name, out var due) && now < due) continue;
                next[s.Name] = now + Math.Max(10, s.IntervalSec) * 1000L;
                try { await PollAsync(s, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Program.Log("News.Poll", ex); }
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            _ = HandleWebhookAsync(c, ct);
        }
    }

    private async Task HandleWebhookAsync(TcpClient c, CancellationToken ct)
    {
        using (c)
        {
            var s = c.GetStream();
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(5000);
                var buf = new byte[256 * 1024];
                var n = 0; var headerEnd = -1; var contentLength = 0;
                while (n < buf.Length)
                {
                    var r = await s.ReadAsync(buf.AsMemory(n), d.Token).ConfigureAwait(false);
                    if (r == 0) break;
                    n += r;
                    if (headerEnd < 0)
                    {
                        headerEnd = buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8);
                        if (headerEnd >= 0)
                        {
                            var head = Encoding.ASCII.GetString(buf, 0, headerEnd);
                            foreach (var line in head.Split("\r\n"))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.AsSpan(15).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                            if (!head.StartsWith("POST /webhook", StringComparison.Ordinal)) { await Reply(s, 404, "not found", d.Token).ConfigureAwait(false); return; }
                        }
                    }
                    if (headerEnd >= 0 && n - headerEnd - 4 >= contentLength) break;
                }
                if (headerEnd < 0 || contentLength <= 0 || contentLength > buf.Length - headerEnd - 4) { await Reply(s, 400, "bad request", d.Token).ConfigureAwait(false); return; }
                var json = Encoding.UTF8.GetString(buf, headerEnd + 4, contentLength);
                var added = IngestJson(json, new FeedSource("Webhook", "", FeedKind.Webhook, 0.6f));
                Interlocked.Increment(ref _webhooks);
                await Reply(s, 200, "{\"accepted\":" + added.ToString(CultureInfo.InvariantCulture) + "}", d.Token).ConfigureAwait(false);
            }
            catch (FormatException) { await Reply(s, 422, "schema", ct).ConfigureAwait(false); }
            catch (JsonException) { await Reply(s, 422, "json", ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        static async Task Reply(NetworkStream s, int code, string body, CancellationToken ct)
        {
            var b = Encoding.UTF8.GetBytes(body);
            var head = "HTTP/1.1 " + code.ToString(CultureInfo.InvariantCulture) + (code == 200 ? " OK" : " Error") + "\r\nContent-Type: application/json\r\nContent-Length: " + b.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
            try { await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false); await s.WriteAsync(b, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public int IngestJson(string json, FeedSource src) => Ingest(ParseJson(json, src));

    // ══════════════════════ 데모 ══════════════════════

    /// <summary>데모 피드: 다출처 교차 보도(검증) · 단독 · 소셜 루머(+반박) · 수치 충돌 스토리.</summary>
    public int SeedDemo()
    {
        var now = DateTimeOffset.UtcNow;
        NewsItem N(string src, FeedKind k, float rep, string t, string s, string cat = "사회", string? loc = null, int ago = 3)
            => new() { Source = src, Kind = k, Reputation = rep, Title = t, Summary = s, Category = cat, Location = loc, Published = now.AddMinutes(-ago), Url = "https://" + src.ToLowerInvariant().Replace(' ', '-') + ".example/" + Guid.NewGuid().ToString("N")[..6] };
        var items = new[]
        {
            N("연합뉴스", FeedKind.Wire, 0.95f, "[속보] 부산 앞바다 규모 4.8 지진 발생", "기상청은 오늘 오후 부산 남동쪽 해역에서 규모 4.8 지진이 발생했다고 밝혔다. 현재까지 인명 피해는 보고되지 않았다.", "재난", "부산"),
            N("KBS", FeedKind.Api, 0.9f, "속보 부산 앞바다 규모 4.8 지진 발생 피해 집계 중", "부산 남동쪽 해역 규모 4.8 지진 발생, 기상청 발표. 인명 피해 보고 없음.", "재난", "부산", 2),
            N("Reuters", FeedKind.Wire, 0.95f, "Breaking: 부산 앞바다 규모 4.8 지진 발생 South Korea", "규모 4.8 지진 부산 해역 발생 기상청 확인 인명 피해 보고 없음", "재난", "부산", 2),
            N("MBC", FeedKind.Rss, 0.85f, "부산 앞바다 규모 4.8 지진 발생 시민 대피", "부산 남동쪽 해역 규모 4.8 지진 발생 기상청 발표 인명 피해 보고 없음", "재난", "부산", 1),
            N("데일리 테크", FeedKind.Rss, 0.8f, "[단독] 국내 AI 반도체 스타트업 3000억 투자 유치", "업계에 따르면 국내 AI 반도체 스타트업이 시리즈 C 라운드에서 3000억 원 투자를 유치했다.", "경제", "서울", 10),
            N("익명 커뮤니티", FeedKind.Social, 0.2f, "충격!! 유명 배우 A씨 결국 은퇴 선언 발칵", "소름 돋는 결국 은퇴 소식!! 측근 제보", "연예", null, 20),
            N("SNS 계정", FeedKind.Social, 0.15f, "유명 배우 A씨 결국 은퇴 선언 충격 대박", "결국 은퇴!! 팬들 경악", "연예", null, 18),
            N("스포츠뉴스", FeedKind.Api, 0.8f, "유명 배우 A씨 은퇴 선언은 사실무근 소속사 부인", "소속사는 은퇴 선언 보도가 사실무근이라며 강하게 부인했다. 허위 정보 법적 대응.", "연예", null, 5),
            N("경제일보", FeedKind.Rss, 0.7f, "수도권 아파트 거래량 12000건 기록 전월 대비 급증", "수도권 아파트 거래량이 12000건으로 집계됐다.", "경제", "서울", 30),
            N("부동산포털", FeedKind.Api, 0.6f, "수도권 아파트 거래량 7000건 기록 전월 대비 급증", "수도권 아파트 거래량이 7000건으로 집계됐다는 분석.", "경제", "서울", 25),
        };
        var n = Ingest(items);
        LastAction = "🧪 데모 피드 " + n.ToString(CultureInfo.InvariantCulture) + "건";
        Raise();
        return n;
    }

    public static string VerdictLabel(FactVerdict v) => v switch
    {
        FactVerdict.Verified => "✅ 사실 확인", FactVerdict.Likely => "🟢 신뢰 가능", FactVerdict.Unverified => "🟡 미확인", FactVerdict.Disputed => "🟠 논란/충돌", _ => "⛔ 허위 의심",
    };

    public static string GradeLabel(NewsGrade g) => g switch { NewsGrade.Breaking => "BREAKING", NewsGrade.Urgent => "URGENT", NewsGrade.Exclusive => "EXCLUSIVE", _ => "NEWS" };

    public static string FlagText(FactFlag f)
    {
        var l = new List<string>();
        if ((f & FactFlag.SingleSource) != 0) l.Add("단일 출처");
        if ((f & FactFlag.SocialOnly) != 0) l.Add("소셜 전용");
        if ((f & FactFlag.Sensational) != 0) l.Add("선정적 표현");
        if ((f & FactFlag.NumberConflict) != 0) l.Add("수치 충돌");
        if ((f & FactFlag.LowReputation) != 0) l.Add("저평판 출처");
        if ((f & FactFlag.LlmFlagged) != 0) l.Add("AI 의심");
        if ((f & FactFlag.Stale) != 0) l.Add("오래된 보도");
        if ((f & FactFlag.Contradiction) != 0) l.Add("반박 보도");
        return l.Count == 0 ? "—" : string.Join(" · ", l);
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("News.OnChange", ex); } }

    public void Dispose() { Stop(); _http.Dispose(); }
}
