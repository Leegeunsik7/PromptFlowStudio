using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Wan;

/// <summary>실시간 트렌드 키워드 1개(제목 + 근사 트래픽 + 관련 뉴스 제목).</summary>
public sealed record TrendItem(string Keyword, string Traffic, string Snippet);

/// <summary>실시간 트렌드 수집 결과. Ok=false 면 수집 실패(수동 주제 입력 폴백).</summary>
public sealed record TrendResult(bool Ok, IReadOnlyList<TrendItem> Items, string Message)
{
    public static TrendResult Fail(string msg) => new(false, Array.Empty<TrendItem>(), msg);
}

/// <summary>실시간 트렌드 하이재킹(RSS 무인 수집) 계약.</summary>
public interface IAutoViralTrendService
{
    /// <summary>지원 지역(코드, 표시명).</summary>
    IReadOnlyList<(string Geo, string Label)> Regions { get; }

    /// <summary>Google Trends 일일 RSS 를 수집해 오늘의 핫 키워드를 추출한다(BCL HttpClient + XML). 네트워크 전용 — API 키 불필요.</summary>
    Task<TrendResult> FetchTrendsAsync(string geo = "KR", CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 실시간 트렌드 하이재킹 — 순수 BCL(HttpClient + System.Xml.Linq, 외부 NuGet 0).
///   ▸ Google Trends 일일 RSS(<c>trends.google.com/.../daily/rss?geo=</c>)를 무인 수집해 <b>오늘의 핫 키워드</b>·근사 트래픽·관련 뉴스 제목을 추출.
///   ▸ API 키가 필요 없다(공개 RSS) — 네트워크만 있으면 동작. 실패(오프라인/포맷 변경)는 Graceful(Ok=false → 수동 주제 입력).
///   ▸ 추출한 키워드를 <see cref="ISwarmAgencyService"/>(스웜 기획)에 넘겨 트렌디한 대본을 자동 생성하는 오토 파이프라인의 입구.
/// </summary>
public sealed class AutoViralTrendService(IConfiguration cfg) : IAutoViralTrendService
{
    // RSS 수집용 HttpClient(User-Agent 필수 — 일부 엔드포인트가 빈 UA 거부).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PromptFlowStudio/1.1 (+https://promptflow.local)");
        return c;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string Geo, string Label)> Regions { get; } = new[]
    {
        ("KR", "대한민국"), ("US", "미국"), ("JP", "일본"), ("GB", "영국"),
        ("IN", "인도"), ("BR", "브라질"), ("ID", "인도네시아"), ("DE", "독일"), ("FR", "프랑스"),
    };

    private string RssBase => string.IsNullOrWhiteSpace(cfg["Trends:RssBase"])
        ? "https://trends.google.com/trends/trendingsearches/daily/rss"
        : cfg["Trends:RssBase"]!.Trim();

    /// <inheritdoc/>
    public async Task<TrendResult> FetchTrendsAsync(string geo = "KR", CancellationToken ct = default)
    {
        var g = string.IsNullOrWhiteSpace(geo) ? "KR" : geo.Trim().ToUpperInvariant();
        try
        {
            var url = $"{RssBase}?geo={Uri.EscapeDataString(g)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return TrendResult.Fail($"트렌드 수집 실패(HTTP {(int)resp.StatusCode}).");

            var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var items = ParseRss(xml);
            return items.Count > 0 ? new(true, items, $"{items.Count}개 트렌드 수집") : TrendResult.Fail("트렌드 항목을 찾지 못했습니다(포맷 변경 가능).");
        }
        catch (OperationCanceledException) { return TrendResult.Fail("수집이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("AutoViralTrend.Fetch", ex); return TrendResult.Fail("네트워크 오류: " + ex.Message); }
    }

    /// <summary>RSS 2.0 XML 을 파싱해 트렌드 항목 추출(순수 BCL XDocument · ht: 네임스페이스 근사 트래픽/뉴스 포함).</summary>
    private static List<TrendItem> ParseRss(string xml)
    {
        var list = new List<TrendItem>();
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ht = "https://trends.google.com/trending/rss";
            foreach (var item in doc.Descendants("item"))
            {
                var title = (item.Element("title")?.Value ?? "").Trim();
                if (title.Length == 0) continue;
                var traffic = (item.Element(ht + "approx_traffic")?.Value ?? "").Trim();
                // 관련 뉴스 첫 제목(스니펫).
                var news = item.Elements(ht + "news_item").FirstOrDefault();
                var snippet = (news?.Element(ht + "news_item_title")?.Value ?? "").Trim();
                list.Add(new TrendItem(title, traffic, snippet));
                if (list.Count >= 20) break;
            }
        }
        catch (Exception ex) { Program.Log("AutoViralTrend.ParseRss", ex); }
        return list;
    }
}
