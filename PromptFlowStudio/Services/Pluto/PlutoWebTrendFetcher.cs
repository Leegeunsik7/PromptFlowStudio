using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌐 실시간 급상승 키워드 1건 — 정제된 검색어 + 대략 트래픽(<c>ht:approx_traffic</c> · 미상 0).</summary>
public sealed record PlutoLiveTrendKeyword(string Keyword, long ApproxTraffic);

/// <summary>🌐 무료 공개 RSS 기반 실시간 트렌드 수집기 — 실패 시 빈 목록(예외 미전파 · 외부 취소만 전파).</summary>
public interface IPlutoWebTrendFetcher
{
    /// <summary>한국 급상승 키워드 상위 <see cref="PlutoWebTrendFetcher.MaxKeywords"/>건 — 네트워크/DNS/타임아웃/XML 오류 → 빈 목록.</summary>
    Task<IReadOnlyList<PlutoLiveTrendKeyword>> FetchLiveTrendingKeywordsAsync(CancellationToken ct = default);
}

/// <summary>
/// 🌐 Google Trends 일간 급상승 RSS(<c>geo=KR</c>) 수집 — API 키 불필요 · <see cref="RequestTimeout"/> 상한 · 응답 크기 상한.
/// <c>&lt;item&gt;&lt;title&gt;</c> + <c>&lt;ht:approx_traffic&gt;</c> 를 DTD 금지 <see cref="XmlReader"/> → <see cref="XDocument"/> 로 안전 파싱,
/// 정제·중복 제거 후 트래픽 내림차순 상위 5~8건. 실패 → 빈 목록으로 <see cref="PlutoAutonomousTrendHarvester"/> 가 LLM/카탈로그 폴백.
/// </summary>
public sealed class PlutoWebTrendFetcher : IPlutoWebTrendFetcher, IDisposable
{
    public const string FeedUrl = "https://trends.google.co.kr/trending/rss?geo=KR";
    public const int MinKeywords = 5;
    public const int MaxKeywords = 8;
    public const int MaxKeywordLength = 40;
    public const long MaxResponseBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _url;
    private int _disposed;

    public PlutoWebTrendFetcher() : this(null, null) { }

    /// <param name="http">주입 클라이언트(테스트용 · 수명 비소유) · null → 내부 생성(<see cref="RequestTimeout"/>).</param>
    /// <param name="url">피드 URL · null → <see cref="FeedUrl"/>.</param>
    public PlutoWebTrendFetcher(HttpClient? http, string? url = null)
    {
        if (http is null)
        {
            _http = new HttpClient { Timeout = RequestTimeout };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PromptFlowStudio/1.0");
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
        _url = string.IsNullOrWhiteSpace(url) ? FeedUrl : url;
    }

    /// <summary>마지막 수집 성공 시각(UTC) — 없음 → null.</summary>
    public DateTime? LastSuccessUtc { get; private set; }

    public async Task<IReadOnlyList<PlutoLiveTrendKeyword>> FetchLiveTrendingKeywordsAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Array.Empty<PlutoLiveTrendKeyword>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await _http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<PlutoLiveTrendKeyword>();
            if (resp.Content.Headers.ContentLength is { } len && len > MaxResponseBytes) return Array.Empty<PlutoLiveTrendKeyword>();
            var xml = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (xml.Length > MaxResponseBytes) return Array.Empty<PlutoLiveTrendKeyword>();
            var result = Parse(xml);
            if (result.Count > 0) LastSuccessUtc = DateTime.UtcNow;
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Array.Empty<PlutoLiveTrendKeyword>(); }   // 4초 타임아웃
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.WebTrend] fetch failed: " + ex.Message);
            return Array.Empty<PlutoLiveTrendKeyword>();
        }
    }

    /// <summary>
    /// RSS XML → 키워드(트래픽 내림차순 · 동률 원 순서 · 대소문자 무시 중복 제거 · 상위 <paramref name="max"/>건).
    /// 파싱 오류/빈 입력 → 빈 목록. 네임스페이스 접두어 변경에 견고(<c>LocalName</c> 매칭).
    /// </summary>
    public static IReadOnlyList<PlutoLiveTrendKeyword> Parse(string? xml, int max = MaxKeywords)
    {
        if (string.IsNullOrWhiteSpace(xml) || max <= 0) return Array.Empty<PlutoLiveTrendKeyword>();
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxResponseBytes,
                IgnoreComments = true,
            };
            using var sr = new StringReader(xml);
            using var reader = XmlReader.Create(sr, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.WebTrend] parse failed: " + ex.Message);
            return Array.Empty<PlutoLiveTrendKeyword>();
        }

        var list = new List<PlutoLiveTrendKeyword>(16);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in doc.Descendants())
        {
            if (item.Name.LocalName != "item") continue;
            string? title = null;
            long traffic = 0;
            foreach (var child in item.Elements())
            {
                var n = child.Name.LocalName;
                if (title is null && n == "title") title = child.Value;
                else if (n == "approx_traffic") traffic = ParseTraffic(child.Value);
            }
            var kw = CleanKeyword(title);
            if (kw is null || !seen.Add(kw)) continue;
            list.Add(new PlutoLiveTrendKeyword(kw, traffic));
        }

        // 안정 정렬(트래픽 내림차순 · 동률 피드 순서)
        var idx = new int[list.Count];
        for (var i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => { var c = list[b].ApproxTraffic.CompareTo(list[a].ApproxTraffic); return c != 0 ? c : a.CompareTo(b); });
        var take = Math.Min(Math.Min(max, MaxKeywords), idx.Length);
        var result = new PlutoLiveTrendKeyword[take];
        for (var i = 0; i < take; i++) result[i] = list[idx[i]];
        return result;
    }

    /// <summary>"20,000+" · "2K+" · "1.5M+" · "500+" → 정수(미상/오류 → 0).</summary>
    public static long ParseTraffic(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        long whole = 0, frac = 0, fracDiv = 1;
        var inFrac = false;
        var mult = 1L;
        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9')
            {
                if (inFrac) { if (fracDiv < 1000) { frac = frac * 10 + (ch - '0'); fracDiv *= 10; } }
                else if (whole < 1_000_000_000_000L) whole = whole * 10 + (ch - '0');
            }
            else if (ch == '.') inFrac = true;
            else if (ch is 'K' or 'k' or '천') { mult = 1_000; break; }
            else if (ch is 'M' or 'm') { mult = 1_000_000; break; }
            else if (ch == '만') { mult = 10_000; break; }
        }
        return whole * mult + frac * mult / fracDiv;
    }

    /// <summary>제어문자 제거 · 공백 정규화 · 길이 상한 — 빈 문자열/URL 형태 → null.</summary>
    public static string? CleanKeyword(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (ch is '<' or '>' or '"' or '\\') continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        if (sb.Length == 0) return null;
        var s = sb.Length > MaxKeywordLength ? sb.ToString(0, MaxKeywordLength).TrimEnd() : sb.ToString();
        if (s.Contains("://", StringComparison.Ordinal)) return null;
        return s;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsHttp) _http.Dispose();
    }
}
