using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Telemetry;
using PromptFlowStudio.Services.Wan;
using Timer = System.Threading.Timer;

namespace PromptFlowStudio.Services.Autonomous;

/// <summary>v16.28.0 트렌드 신호 1건.</summary>
public sealed record TrendSignal(string Keyword, string Source, double Score, string Format);

public enum SlotState : byte { Planned, Producing, AwaitingApproval, Publishing, Published, Skipped, Failed }

/// <summary>v16.28.0 24시간 스케줄 슬롯.</summary>
public sealed class ScheduleSlot
{
    public string Id { get; init; } = "s_" + Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset At { get; init; }
    public string Theme { get; init; } = "";
    public string Keyword { get; init; } = "";
    public string Format { get; init; } = "story";
    public SocialPublishTarget Platform { get; init; }
    public SlotState State { get; set; }
    public string? Title { get; set; }
    public string? Prompt { get; set; }
    public string? VideoPath { get; set; }
    public string? PostId { get; set; }
    public string? Message { get; set; }
}

/// <summary>v16.28.0 캠페인 KPI 목표.</summary>
public sealed record CampaignKpi(long TargetViewsPerDay = 10_000, double TargetCtr = 0.06, int VideosPerDay = 6, double MaxDailyUsd = 0);

/// <summary>
/// [PromptFlowStudio] v16.28.0 🤖 자율 캠페인 기획 &amp; 스케줄링 디렉터 — 트렌드 수집 → KPI 기반 24시간 스케줄 → 스토리보드 자동 생성 → 생산 → (승인) → 게시 → 학습.
///   ▸ 트렌드: YouTube Data API mostPopular(지역 KR · <c>PFS_YOUTUBE_API_KEY</c>) 제목/태그 + Google Trends 일간 RSS(공개 피드) → 키워드/해시태그 빈도·순위 점수
///     + 숏폼 포맷 분류(POV/ASMR/튜토리얼/챌린지…). 모두 실패 시 1초 내 오프라인 시드(학습 테마 + 기본 키워드) 폴백. 스크레이핑 대신 공식 API/RSS 만 사용.
///   ▸ 스케줄(<see cref="BuildSchedule"/>): 피크 시간대(07·12·18·20·22시 …) 우선 슬롯 · 테마는 <see cref="CampaignRoiOptimizationEngine"/> 비중 × 트렌드 점수 최대 잉여 배분 · 플랫폼 순환.
///   ▸ 스토리보드: Gemini Strict JSON(제목 + 3~5컷 · 12초 데드라인 · 스키마 검증) → 실패 시 템플릿 폴백. 프롬프트에 피드백 엔진 최적 카메라/조명 팔 + 자가 정제 큐 반영.
///   ▸ 자율 루프: 30초 동기 틱(예외 격리) → in-flight 1슬롯 백프레셔 → 킬 스위치 · 일일 생산 상한 · v16.18.0 예산 차단 · 잡 매니저 유휴 확인 → 생산.
///     게시는 <see cref="AutoPublish"/> 꺼짐(기본)이면 승인 대기, 켜짐이어도 <see cref="PublishPublic"/> 꺼짐이면 비공개 업로드(QA 검수 포함). 매 6슬롯마다 학습 사이클(통계 갱신 → 자가 정제 → ROI 재배분).
/// </summary>
public sealed class AutonomousCampaignDirectorService : IDisposable
{
    public const int MaxTrends = 40;
    public const int MaxSlots = 96;
    public const int MinScenes = 3, MaxScenes = 5;
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AiDeadline = TimeSpan.FromSeconds(12);
    public static readonly int[] PeakHours = { 7, 12, 18, 20, 22, 9, 15, 21, 10, 13, 17, 19 };

    private static readonly string[] SeedKeywords = { "아기 일상", "반려동물", "홈카페", "캠핑", "자취 요리", "운동 루틴", "여행 브이로그", "정리 정돈" };
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "official", "video", "shorts", "short", "mv", "ep", "full", "live", "new", "vs", "feat",
        "공식", "영상", "뮤직비디오", "티저", "예고", "모음", "오늘", "그리고", "이번", "진짜", "ㅋㅋ", "ㅋㅋㅋ",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Func<string, CancellationToken, Task<GeminiResult>>? _generate;
    private readonly Func<bool> _aiEnabled;
    private readonly Func<string?> _ytKey;
    private readonly Func<StoryboardProject, CancellationToken, Task<string?>>? _produce;
    private readonly Func<SocialPublishRequest, CancellationToken, Task<SocialPublishResult>>? _publish;
    private readonly Func<CancellationToken, Task<List<TrendSignal>>>? _trendOverride;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ProductionCostTrackerService? _cost;
    private readonly StoryboardRepository? _repo;
    private readonly object _gate = new();
    private readonly List<TrendSignal> _trends = new();
    private readonly List<ScheduleSlot> _slots = new();
    private Timer? _timer;
    private CancellationTokenSource? _runCts;
    private int _inFlight, _producedToday, _publishedTotal, _failedTotal, _sinceLearn;
    private DateOnly _day;
    private bool _disposed;

    public PerformanceFeedbackLoopEngine Feedback { get; }
    public CampaignRoiOptimizationEngine Roi { get; }

    public AutonomousCampaignDirectorService(PerformanceFeedbackLoopEngine feedback, CampaignRoiOptimizationEngine roi,
        GeminiClient? gemini = null, StoryboardJobManager? jobs = null, DirectSocialPublisherBridge? publisher = null,
        ProductionCostTrackerService? cost = null, StoryboardRepository? repo = null)
        : this(feedback, roi,
               gemini is null ? null : (p, ct) => gemini.GenerateAsync(p, null, 0.7, ct), () => gemini?.Enabled == true,
               () => Environment.GetEnvironmentVariable("PFS_YOUTUBE_API_KEY"),
               jobs is null ? null : (p, ct) => ProduceWithJobsAsync(jobs, p, ct),
               publisher is null ? null : (r, ct) => publisher.PublishAsync(r, null, ct),
               null, null, cost, repo) { }

    public AutonomousCampaignDirectorService(PerformanceFeedbackLoopEngine feedback, CampaignRoiOptimizationEngine roi,
        Func<string, CancellationToken, Task<GeminiResult>>? generate, Func<bool> aiEnabled, Func<string?> ytKey,
        Func<StoryboardProject, CancellationToken, Task<string?>>? produce, Func<SocialPublishRequest, CancellationToken, Task<SocialPublishResult>>? publish,
        Func<CancellationToken, Task<List<TrendSignal>>>? trendOverride, Func<DateTimeOffset>? clock, ProductionCostTrackerService? cost, StoryboardRepository? repo)
    {
        Feedback = feedback;
        Roi = roi;
        _generate = generate;
        _aiEnabled = aiEnabled;
        _ytKey = ytKey;
        _produce = produce;
        _publish = publish;
        _trendOverride = trendOverride;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _cost = cost;
        _repo = repo;
        _day = DateOnly.FromDateTime(_clock().DateTime);
    }

    public event Action? OnChange;

    public CampaignKpi Kpi { get; set; } = new();
    public bool IsAutonomous => Volatile.Read(ref _timer) is not null;
    /// <summary>생산 완료 후 사람 승인 없이 게시(기본 꺼짐).</summary>
    public bool AutoPublish { get; set; }
    /// <summary>공개 게시(기본 꺼짐 → 비공개 업로드).</summary>
    public bool PublishPublic { get; set; }
    public bool IsBusy => Volatile.Read(ref _inFlight) != 0;
    public bool CanProduce => _produce is not null;
    public bool CanPublish => _publish is not null;
    public bool AiAvailable => _generate is not null && _aiEnabled();
    public bool YouTubeConfigured => !string.IsNullOrWhiteSpace(_ytKey());
    public int ProducedToday => Volatile.Read(ref _producedToday);
    public int PublishedTotal => Volatile.Read(ref _publishedTotal);
    public int FailedTotal => Volatile.Read(ref _failedTotal);
    public string? LastAction { get; private set; }
    public DateTimeOffset? LastTrendAt { get; private set; }
    public IReadOnlyList<TrendSignal> Trends { get { lock (_gate) return _trends.ToArray(); } }
    public IReadOnlyList<ScheduleSlot> Slots { get { lock (_gate) return _slots.ToArray(); } }

    public ScheduleSlot? NextSlot
    {
        get
        {
            lock (_gate) foreach (var s in _slots) if (s.State == SlotState.Planned) return s;
            return null;
        }
    }

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    /// <summary>제목/태그 → 키워드 후보(한글/영문 2자+ · 불용어 제외 · #해시태그는 가중 2).</summary>
    public static void CountKeywords(string? text, Dictionary<string, double> counts, double weight = 1)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            var hash = span[i] == '#';
            if (hash) i++;
            var start = i;
            while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] == '_')) i++;
            var len = i - start;
            if (len >= 2 && len <= 20 && !IsAllDigits(span.Slice(start, len)))
            {
                var w = span.Slice(start, len).ToString().ToLowerInvariant();
                if (!Stop.Contains(w))
                {
                    counts.TryGetValue(w, out var c);
                    counts[w] = c + weight * (hash ? 2 : 1);
                }
            }
            if (i == start) i++;
        }

        static bool IsAllDigits(ReadOnlySpan<char> s) { foreach (var ch in s) if (!char.IsDigit(ch)) return false; return true; }
    }

    /// <summary>텍스트 → 숏폼 포맷 라벨(피드백 엔진 포맷 사전 · 기본 story).</summary>
    public static string DetectFormat(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "story";
        var i = PerformanceFeedbackLoopEngine.DetectFeature(text, PerformanceFeedbackLoopEngine.FormatLexicon);
        return i >= 0 ? PerformanceFeedbackLoopEngine.FormatLexicon[i][0] : "story";
    }

    /// <summary>YouTube videos.list(mostPopular) 응답 → 트렌드(키워드 빈도 × 순위 가중 · 상위 n). 스키마 불일치 시 빈 목록.</summary>
    public static List<TrendSignal> ParseYouTubePopular(string? json, int top = 20)
    {
        var res = new List<TrendSignal>();
        if (string.IsNullOrWhiteSpace(json)) return res;
        var counts = new Dictionary<string, double>(256, StringComparer.Ordinal);
        var formats = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return res;
            var rank = 0;
            var total = Math.Max(1, items.GetArrayLength());
            foreach (var it in items.EnumerateArray())
            {
                rank++;
                if (!it.TryGetProperty("snippet", out var sn) || sn.ValueKind != JsonValueKind.Object) continue;
                var title = sn.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var w = 1.0 + (total - rank + 1) / (double)total;
                var before = new HashSet<string>(counts.Keys, StringComparer.Ordinal);
                CountKeywords(title, counts, w);
                if (sn.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    foreach (var tg in tags.EnumerateArray()) if (tg.ValueKind == JsonValueKind.String) CountKeywords(tg.GetString(), counts, w * 0.5);
                var fmt = DetectFormat(title);
                foreach (var k in counts.Keys) if (!before.Contains(k)) formats.TryAdd(k, fmt);
            }
        }
        catch (JsonException) { return res; }
        return TopOf(counts, formats, "youtube", top);
    }

    /// <summary>Google Trends 일간 RSS → 트렌드(순위 가중 · 검색량 approx_traffic 보정). DTD 금지 XmlReader.</summary>
    public static List<TrendSignal> ParseTrendsRss(string? xml, int top = 20)
    {
        var res = new List<TrendSignal>();
        if (string.IsNullOrWhiteSpace(xml)) return res;
        try
        {
            using var sr = new System.IO.StringReader(xml);
            using var r = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = 2_000_000 });
            var rank = 0;
            string? title = null; double traffic = 0; var inItem = false;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "item") { inItem = true; title = null; traffic = 0; }
                else if (inItem && r.NodeType == XmlNodeType.Element && r.LocalName == "title") title = r.ReadElementContentAsString().Trim();
                else if (inItem && r.NodeType == XmlNodeType.Element && r.LocalName == "approx_traffic") traffic = ParseTraffic(r.ReadElementContentAsString());
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "item")
                {
                    inItem = false;
                    if (string.IsNullOrWhiteSpace(title) || title.Length > 40) continue;
                    rank++;
                    var score = 1.0 / rank * 10 + Math.Log10(Math.Max(1, traffic));
                    res.Add(new TrendSignal(title, "google-trends", score, DetectFormat(title)));
                    if (res.Count >= top) break;
                }
            }
        }
        catch (XmlException) { return new(); }
        return res;

        static double ParseTraffic(string s)
        {
            s = s.Replace(",", "", StringComparison.Ordinal).Replace("+", "", StringComparison.Ordinal).Trim();
            double mul = 1;
            if (s.EndsWith('K') || s.EndsWith('k')) { mul = 1e3; s = s[..^1]; }
            else if (s.EndsWith('M') || s.EndsWith('m')) { mul = 1e6; s = s[..^1]; }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * mul : 0;
        }
    }

    private static List<TrendSignal> TopOf(Dictionary<string, double> counts, Dictionary<string, string> formats, string source, int top)
    {
        var list = new List<KeyValuePair<string, double>>(counts);
        list.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        var res = new List<TrendSignal>(Math.Min(top, list.Count));
        foreach (var kv in list)
        {
            if (res.Count >= top) break;
            res.Add(new TrendSignal(kv.Key, source, kv.Value, formats.TryGetValue(kv.Key, out var f) ? f : "story"));
        }
        return res;
    }

    /// <summary>
    /// 24시간 스케줄 — 피크 시간 순으로 <paramref name="videos"/>개 슬롯(현재 이후 · 시각순 정렬) · 테마 = 비중 × 트렌드 점수 최대 잉여 배분 · 플랫폼 순환.
    /// <paramref name="themeWeights"/> 비면 트렌드 점수만 사용.
    /// </summary>
    public static List<ScheduleSlot> BuildSchedule(DateTimeOffset now, int videos, IReadOnlyList<TrendSignal> trends, Func<string, double> themeWeight, IReadOnlyList<SocialPublishTarget> platforms)
    {
        var slots = new List<ScheduleSlot>();
        videos = Math.Clamp(videos, 1, Math.Min(MaxSlots, 24));
        if (trends.Count == 0 || platforms.Count == 0) return slots;
        var n = Math.Min(trends.Count, 12);
        Span<double> w = stackalloc double[n];
        Span<int> counts = stackalloc int[n];
        for (var i = 0; i < n; i++)
        {
            var tw = themeWeight(trends[i].Keyword);
            w[i] = Math.Max(1e-6, trends[i].Score) * (tw > 0 ? 0.5 + tw * 4 : 1);
        }
        CampaignRoiOptimizationEngine.Apportion(w, videos, counts);
        var hours = new List<DateTimeOffset>(videos);
        var baseDay = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        for (var k = 0; hours.Count < videos && k < 48; k++)
        {
            var h = PeakHours[k % PeakHours.Length];
            var at = baseDay.AddDays(k / PeakHours.Length).AddHours(h);
            if (at <= now) at = at.AddDays(1);
            if (!hours.Contains(at)) hours.Add(at);
        }
        for (var h = 0; hours.Count < videos && h < 24; h++)
        {
            var at = baseDay.AddHours(h);
            if (at <= now) at = at.AddDays(1);
            if (!hours.Contains(at)) hours.Add(at);
        }
        hours.Sort();
        var ti = 0; var used = 0;
        for (var s = 0; s < videos && s < hours.Count; s++)
        {
            while (ti < n && used >= counts[ti]) { ti++; used = 0; }
            if (ti >= n) break;
            var t = trends[ti];
            slots.Add(new ScheduleSlot { At = hours[s], Theme = t.Keyword, Keyword = t.Keyword, Format = t.Format, Platform = platforms[s % platforms.Count] });
            used++;
        }
        return slots;
    }

    /// <summary>Gemini 스토리보드 응답 검증 — <c>{"title":"...","scenes":[{"prompt":"..."}]}</c> · 3~5컷 · 프롬프트 15~600자 · URL 금지.</summary>
    public static bool TryParseStoryboard(string? text, out string title, out List<string> prompts)
    {
        title = ""; prompts = new();
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("scenes", out var sc) || sc.ValueKind != JsonValueKind.Array) return false;
            title = (t.GetString() ?? "").Trim();
            if (title.Length is 0 or > 80) return false;
            foreach (var s in sc.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("prompt", out var p) || p.ValueKind != JsonValueKind.String) return false;
                var ps = (p.GetString() ?? "").Trim();
                if (ps.Length is < 15 or > 600 || ps.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;
                prompts.Add(ps);
            }
            return prompts.Count is >= MinScenes and <= MaxScenes;
        }
        catch (JsonException) { prompts = new(); return false; }
    }

    /// <summary>템플릿 스토리보드(오프라인 폴백) — 포맷별 3컷 아크 + 카메라/조명 팔.</summary>
    public static (string Title, List<string> Prompts) TemplateStoryboard(string keyword, string format, string camera, string lighting)
    {
        var k = keyword.Trim();
        var (a, b, c) = format switch
        {
            "pov" => ("first-person POV discovering " + k, "POV hands interacting with " + k + ", reaction moment", "POV satisfying payoff of " + k),
            "asmr" => ("extreme close-up textures of " + k + ", quiet ambience", "slow tactile details of " + k, "calm final reveal of " + k),
            "tutorial" => ("problem setup about " + k + ", bold on-screen hook", "step-by-step demonstration of " + k, "finished result of " + k + ", satisfying reveal"),
            "challenge" => ("energetic start of the " + k + " challenge", "peak attempt of the " + k + " challenge", "triumphant ending of the " + k + " challenge"),
            "before-after" => ("messy before state of " + k, "quick transformation process of " + k, "clean after state of " + k + ", dramatic comparison"),
            _ => ("intriguing opening scene about " + k, "emotional rising moment of " + k, "heartwarming conclusion of " + k),
        };
        var tail = ", " + camera + ", " + lighting + ", vertical 9:16, cinematic, high detail";
        return (k + " · " + format, new List<string> { "eye-catching opening motion in first second, " + a + tail, b + tail, c + tail });
    }

    // ══════════════════════ 트렌드 수집 ══════════════════════

    /// <summary>트렌드 갱신 — YouTube + Google Trends 병합(정규화 점수 합) → 실패 시 오프라인 시드. 반환: 개수.</summary>
    public async Task<int> RefreshTrendsAsync(CancellationToken ct = default)
    {
        List<TrendSignal> merged;
        if (_trendOverride is not null) merged = await _trendOverride(ct).ConfigureAwait(false);
        else
        {
            var yt = await FetchYouTubeAsync(ct).ConfigureAwait(false);
            var gt = await FetchTrendsRssAsync(ct).ConfigureAwait(false);
            merged = Merge(yt, gt);
        }
        if (merged.Count == 0) merged = OfflineSeed();
        lock (_gate)
        {
            _trends.Clear();
            for (var i = 0; i < merged.Count && i < MaxTrends; i++) _trends.Add(merged[i]);
        }
        LastTrendAt = _clock();
        LastAction = "📡 트렌드 " + merged.Count.ToString(CultureInfo.InvariantCulture) + "건 수집 · 1위 " + merged[0].Keyword;
        Raise();
        return merged.Count;
    }

    /// <summary>두 소스 병합 — 소스별 최대값 정규화 후 키워드 합산(대소문자 무시).</summary>
    public static List<TrendSignal> Merge(List<TrendSignal> a, List<TrendSignal> b)
    {
        var map = new Dictionary<string, TrendSignal>(StringComparer.OrdinalIgnoreCase);
        Add(a); Add(b);
        var list = new List<TrendSignal>(map.Values);
        list.Sort(static (x, y) => y.Score.CompareTo(x.Score));
        return list;

        void Add(List<TrendSignal> src)
        {
            double max = 0;
            foreach (var t in src) max = Math.Max(max, t.Score);
            if (max <= 0) return;
            foreach (var t in src)
            {
                var s = t.Score / max;
                map[t.Keyword] = map.TryGetValue(t.Keyword, out var cur) ? cur with { Score = cur.Score + s, Source = cur.Source + "+" + t.Source } : t with { Score = s };
            }
        }
    }

    private List<TrendSignal> OfflineSeed()
    {
        var res = new List<TrendSignal>();
        foreach (var a in Roi.Plan) res.Add(new TrendSignal(a.Theme, "learned", 1 + a.Weight * 2, "story"));
        for (var i = 0; i < SeedKeywords.Length; i++) res.Add(new TrendSignal(SeedKeywords[i], "offline", 1.0 - i * 0.05, i % 2 == 0 ? "vlog" : "story"));
        return res;
    }

    private async Task<List<TrendSignal>> FetchYouTubeAsync(CancellationToken ct)
    {
        var key = _ytKey();
        if (string.IsNullOrWhiteSpace(key)) return new();
        var url = "https://www.googleapis.com/youtube/v3/videos?part=snippet&chart=mostPopular&regionCode=KR&maxResults=50&key=" + Uri.EscapeDataString(key);
        return ParseYouTubePopular(await GetWithRetryAsync(url, ct).ConfigureAwait(false));
    }

    private async Task<List<TrendSignal>> FetchTrendsRssAsync(CancellationToken ct)
        => ParseTrendsRss(await GetWithRetryAsync("https://trends.google.com/trending/rss?geo=KR", ct).ConfigureAwait(false));

    private static async Task<string?> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromSeconds(8));
                using var resp = await Http.GetAsync(url, d.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode is 429 or >= 500) { await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false); continue; }
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
                return body.Length > 2_000_000 ? null : body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Program.Log("Director.Fetch", ex); }
        }
        return null;
    }

    // ══════════════════════ 스케줄 / 스토리보드 ══════════════════════

    /// <summary>스케줄 재수립 — 미착수 슬롯만 교체(진행/완료 이력 보존).</summary>
    public List<ScheduleSlot> PlanSchedule()
    {
        var trends = Trends;
        var platforms = new List<SocialPublishTarget> { SocialPublishTarget.YouTubeShorts, SocialPublishTarget.TikTok, SocialPublishTarget.InstagramReels };
        var plan = BuildSchedule(_clock(), Kpi.VideosPerDay, trends, Roi.WeightOf, platforms);
        lock (_gate)
        {
            _slots.RemoveAll(s => s.State == SlotState.Planned);
            _slots.AddRange(plan);
            _slots.Sort(static (a, b) => a.At.CompareTo(b.At));
            while (_slots.Count > MaxSlots) _slots.RemoveAt(0);
        }
        LastAction = "🗓 24시간 스케줄 " + plan.Count.ToString(CultureInfo.InvariantCulture) + "슬롯 수립";
        Raise();
        return plan;
    }

    /// <summary>슬롯 → 스토리보드(Gemini → 템플릿 폴백 · 자가 정제 프롬프트 우선 1컷 반영).</summary>
    public async Task<StoryboardProject> BuildStoryboardAsync(ScheduleSlot slot, CancellationToken ct)
    {
        var camIdx = Feedback.BestArm(PromptAxis.Camera); if (camIdx < 0) camIdx = Math.Max(0, Feedback.NextArm(PromptAxis.Camera));
        var litIdx = Feedback.BestArm(PromptAxis.Lighting); if (litIdx < 0) litIdx = Math.Max(0, Feedback.NextArm(PromptAxis.Lighting));
        var cam = PerformanceFeedbackLoopEngine.CameraLexicon[camIdx][0];
        var lit = PerformanceFeedbackLoopEngine.LightingLexicon[litIdx][0];
        string title; List<string> prompts;
        var ok = false;
        title = ""; prompts = new();
        if (AiAvailable)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(AiDeadline);
                var instr = new StringBuilder(512)
                    .Append("너는 숏폼 바이럴 캠페인 디렉터다. 트렌드 키워드 '").Append(slot.Keyword).Append("' 로 ").Append(slot.Format)
                    .Append(" 포맷의 9:16 AI 영상 스토리보드를 만들어라. 3~5컷, 각 컷은 영어 Wan 비디오 프롬프트(카메라 무빙 '").Append(cam)
                    .Append("' 과 조명 '").Append(lit).Append("' 반영, 첫 컷은 1초 내 시선 훅). 목표 CTR ")
                    .Append((Kpi.TargetCtr * 100).ToString("0.#", CultureInfo.InvariantCulture)).Append("%. 실존 인물/상표/URL 금지.\n")
                    .Append("JSON 하나만 출력: {\"title\":\"한국어 제목(40자 이내)\",\"scenes\":[{\"prompt\":\"...\"}]}");
                var r = await _generate!(instr.ToString(), d.Token).ConfigureAwait(false);
                ok = r.Ok && TryParseStoryboard(r.Text, out title, out prompts);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ok = false; }
            catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Director.Storyboard", ex); ok = false; }
        }
        if (!ok) (title, prompts) = TemplateStoryboard(slot.Keyword, slot.Format, cam, lit);
        if (Feedback.DequeueTuned(slot.Theme) is { } tuned) prompts[0] = tuned;
        var p = new StoryboardProject { Title = title };
        foreach (var pr in prompts) p.Scenes.Add(new StoryboardScene { Prompt = pr, Size = "720*1280", Mode = SceneMode.TextToVideo });
        slot.Title = title;
        slot.Prompt = prompts[0];
        return p;
    }

    private static async Task<string?> ProduceWithJobsAsync(StoryboardJobManager jobs, StoryboardProject p, CancellationToken ct)
    {
        if (jobs.IsRunning || jobs.IsMerging || !jobs.Enabled) return null;
        using var reg = ct.Register(jobs.Cancel);
        await jobs.StartAsync(p).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(p.FinalVideoPath) ? null : p.FinalVideoPath;
    }

    // ══════════════════════ 자율 루프 ══════════════════════

    /// <summary>자율 가동 시작(트렌드 → 스케줄 → 30초 틱).</summary>
    public async Task<bool> StartAutonomousAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return false;
            _runCts = new CancellationTokenSource();
        }
        try
        {
            await RefreshTrendsAsync(ct).ConfigureAwait(false);
            Roi.Rebalance();
            PlanSchedule();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Director.Start", ex); }
        lock (_gate)
        {
            if (_disposed || _runCts is null) return false;
            _timer = new Timer(static s => ((AutonomousCampaignDirectorService)s!).OnTick(), this, TimeSpan.FromSeconds(2), TickInterval);
        }
        LastAction = "🤖 자율 가동 ON";
        Raise();
        return true;
    }

    /// <summary>킬 스위치 — 틱 중지 + 진행 중 생산/게시 취소.</summary>
    public void StopAutonomous()
    {
        Timer? t; CancellationTokenSource? c;
        lock (_gate) { t = _timer; _timer = null; c = _runCts; _runCts = null; }
        t?.Dispose();
        try { c?.Cancel(); } catch (ObjectDisposedException) { }
        c?.Dispose();
        LastAction = "⏹ 자율 가동 OFF";
        Raise();
    }

    private void OnTick()
    {
        try
        {
            if (Interlocked.Exchange(ref _inFlight, 1) != 0) return;
            CancellationToken ct;
            lock (_gate)
            {
                if (_runCts is null) { Volatile.Write(ref _inFlight, 0); return; }
                ct = _runCts.Token;
            }
            _ = RunTickGuardedAsync(ct);
        }
        catch (Exception ex) { Volatile.Write(ref _inFlight, 0); Program.Log("Director.Tick", ex); }
    }

    private async Task RunTickGuardedAsync(CancellationToken ct)
    {
        try { await RunDueAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("Director.Run", ex); }
        finally { Volatile.Write(ref _inFlight, 0); Raise(); }
    }

    /// <summary>
    /// 틱 1회 — 날짜 경계 리셋 · 스케줄 소진 시 재수립 · 도래 슬롯 1개 처리(가드 → 생산 → 승인/게시) · 학습 사이클.
    /// 반환: 처리한 슬롯(없으면 null).
    /// </summary>
    public async Task<ScheduleSlot?> RunDueAsync(CancellationToken ct)
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now.DateTime);
        if (today != _day) { _day = today; Interlocked.Exchange(ref _producedToday, 0); }
        if (NextSlot is null) { await RefreshTrendsAsync(ct).ConfigureAwait(false); PlanSchedule(); }
        ScheduleSlot? slot = null;
        lock (_gate) foreach (var s in _slots) if (s.State == SlotState.Planned && s.At <= now) { slot = s; break; }
        if (slot is null) return null;
        return await ProcessSlotAsync(slot, ct).ConfigureAwait(false);
    }

    /// <summary>예정 슬롯 즉시 실행(수동 · 자율 틱과 in-flight 1슬롯 공유 · 실행 중이면 null).</summary>
    public async Task<ScheduleSlot?> RunSlotNowAsync(string slotId, CancellationToken ct = default)
    {
        ScheduleSlot? slot = null;
        lock (_gate) foreach (var s in _slots) if (s.Id == slotId && s.State == SlotState.Planned) { slot = s; break; }
        if (slot is null || Interlocked.Exchange(ref _inFlight, 1) != 0) return null;
        try { return await ProcessSlotAsync(slot, ct).ConfigureAwait(false); }
        finally { Volatile.Write(ref _inFlight, 0); Raise(); }
    }

    private async Task<ScheduleSlot> ProcessSlotAsync(ScheduleSlot slot, CancellationToken ct)
    {
        var guard = GuardReason();
        if (guard is not null) { Mark(slot, SlotState.Skipped, guard); return slot; }
        Mark(slot, SlotState.Producing, "스토리보드 생성");
        var project = await BuildStoryboardAsync(slot, ct).ConfigureAwait(false);
        try { _repo?.Save(project); } catch (Exception ex) { Program.Log("Director.Save", ex); }
        string? video;
        using (var scope = _cost?.BeginScope(project.Title, slot.Theme, PlatformLabel(slot.Platform)))
        {
            video = await _produce!(project, ct).ConfigureAwait(false);
            if (scope is not null) scope.Succeeded = video is not null;
        }
        if (video is null) { Interlocked.Increment(ref _failedTotal); Mark(slot, SlotState.Failed, "생산 실패/잡 매니저 사용 중"); return slot; }
        Interlocked.Increment(ref _producedToday);
        slot.VideoPath = video;
        if (AutoPublish && CanPublish) await PublishAsync(slot, ct).ConfigureAwait(false);
        else Mark(slot, SlotState.AwaitingApproval, CanPublish ? "승인 대기" : "게시 브릿지 없음 — 수동 게시");
        if (++_sinceLearn >= 6) { _sinceLearn = 0; await LearnAsync(ct).ConfigureAwait(false); }
        return slot;
    }

    /// <summary>가드 — 생산 불가/일일 상한/예산 차단/일일 USD 상한 사유(통과 null).</summary>
    public string? GuardReason()
    {
        if (!CanProduce) return "생산 파이프라인 없음(데스크톱 전용)";
        if (ProducedToday >= Math.Max(1, Kpi.VideosPerDay)) return "일일 생산 상한 도달";
        if (_cost?.CheckVideoAllowed() is { } block) return block;
        if (Kpi.MaxDailyUsd > 0 && _cost is not null && _cost.TodayUsd >= Kpi.MaxDailyUsd) return "캠페인 일일 USD 상한 도달";
        return null;
    }

    /// <summary>승인 대기 슬롯 게시(사람 승인).</summary>
    public async Task<bool> ApproveAsync(string slotId, CancellationToken ct = default)
    {
        ScheduleSlot? s = null;
        lock (_gate) foreach (var x in _slots) if (x.Id == slotId && x.State == SlotState.AwaitingApproval) { s = x; break; }
        if (s is null || !CanPublish) return false;
        return await PublishAsync(s, ct).ConfigureAwait(false);
    }

    public void Reject(string slotId)
    {
        lock (_gate) foreach (var x in _slots) if (x.Id == slotId && x.State == SlotState.AwaitingApproval) { x.State = SlotState.Skipped; x.Message = "거절됨"; break; }
        Raise();
    }

    private async Task<bool> PublishAsync(ScheduleSlot s, CancellationToken ct)
    {
        Mark(s, SlotState.Publishing, PublishPublic ? "공개 게시 중" : "비공개 업로드 중");
        var tags = new List<string> { s.Keyword.Replace(" ", "", StringComparison.Ordinal), s.Format, "shorts" };
        var req = new SocialPublishRequest(s.Platform, s.VideoPath!, s.Title ?? s.Keyword, (s.Title ?? s.Keyword) + "\n#" + tags[0], tags, null, PublishPublic);
        SocialPublishResult r;
        try { r = await _publish!(req, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("Director.Publish", ex); r = new SocialPublishResult(s.Platform, false, SocialPublishState.Failed, null, null, ex.Message); }
        if (!r.Ok) { Interlocked.Increment(ref _failedTotal); Mark(s, SlotState.Failed, "게시 실패: " + r.Message); return false; }
        s.PostId = r.PostId;
        Interlocked.Increment(ref _publishedTotal);
        if (r.PostId is { Length: > 0 } id)
            Feedback.Ingest(new VideoPerformance(id, s.Theme, s.Prompt ?? "", PlatformLabel(s.Platform), 0, 0, 0, 0, 0, 0, _clock(), s.Id));
        Mark(s, SlotState.Published, r.Url ?? "게시 완료");
        return true;
    }

    /// <summary>학습 사이클 — YouTube 통계 갱신 → 저성과 자가 정제(최대 3) → ROI 재배분 → 스케줄 재수립.</summary>
    public async Task LearnAsync(CancellationToken ct = default)
    {
        var ids = new List<string>();
        foreach (var r in Feedback.Records) if (r.Platform == "YouTube" && ids.Count < 50) ids.Add(r.VideoId);
        try { await Feedback.RefreshYouTubeStatsAsync(ids, ct).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Director.Learn", ex); }
        await Feedback.TuneLowPerformersAsync(3, ct).ConfigureAwait(false);
        Roi.Rebalance();
        PlanSchedule();
        LastAction = "🧠 학습 사이클 · 세대 G" + Feedback.Generation.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>1-Click 자율 캠페인 — KPI 적용 후 자율 가동(이미 가동 중이면 스케줄만 재수립).</summary>
    public async Task<bool> LaunchCampaignAsync(CampaignKpi kpi, CancellationToken ct = default)
    {
        Kpi = kpi with { VideosPerDay = Math.Clamp(kpi.VideosPerDay, 1, 24) };
        if (IsAutonomous) { PlanSchedule(); return true; }
        return await StartAutonomousAsync(ct).ConfigureAwait(false);
    }

    public static string PlatformLabel(SocialPublishTarget t) => t switch { SocialPublishTarget.YouTubeShorts => "YouTube", SocialPublishTarget.TikTok => "TikTok", _ => "Instagram" };

    private void Mark(ScheduleSlot s, SlotState st, string msg)
    {
        lock (_gate) { s.State = st; s.Message = msg; }
        LastAction = st switch
        {
            SlotState.Producing => "🎬 생산 · ", SlotState.AwaitingApproval => "⏳ 승인 대기 · ", SlotState.Published => "🚀 게시 · ",
            SlotState.Failed => "⚠ 실패 · ", SlotState.Skipped => "⏭ 건너뜀 · ", _ => "",
        } + (s.Title ?? s.Keyword);
        Raise();
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Director.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopAutonomous();
        OnChange = null;
    }
}
