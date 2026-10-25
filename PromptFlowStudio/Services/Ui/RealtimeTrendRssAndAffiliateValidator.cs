using PromptFlowStudio.Services.ViralCommerce;

namespace PromptFlowStudio.Services.Ui;

/// <summary>[v18.0.6] RSS 급상승 토픽 1 건 — 제목은 피드 버퍼 오프셋(복사 0) · 트래픽 · 가속도 지수(0~999).</summary>
public readonly struct TrendSpike
{
    public readonly int TitleStart, TitleLength, Traffic, PrevTraffic, Acceleration;

    public TrendSpike(int titleStart, int titleLength, int traffic, int prevTraffic, int acceleration)
    {
        TitleStart = titleStart; TitleLength = titleLength; Traffic = traffic; PrevTraffic = prevTraffic; Acceleration = acceleration;
    }

    public bool IsEmpty => TitleLength == 0;
    public ReadOnlySpan<char> Title(ReadOnlySpan<char> feed) => feed.Slice(TitleStart, TitleLength);
}

/// <summary>[v18.0.6] 공정위 추천·보증 심사지침 린트 결과.</summary>
public enum AffiliateNoticeLint : byte
{
    /// <summary>필수 문구 원문 그대로 · 본문 첫머리.</summary>
    Ok,
    /// <summary>필수 문구도 · 핵심어도 없음.</summary>
    Missing,
    /// <summary>핵심어(쿠팡 파트너스 · 수수료 등)는 있으나 원문과 다름(축약 · 변형).</summary>
    Altered,
    /// <summary>원문은 있으나 첫머리(<see cref="RealtimeTrendRssAndAffiliateValidator.TopWindowChars"/>) 밖에 묻힘.</summary>
    Buried,
}

/// <summary>
/// [PromptFlow Studio v18.0.6] 📡 실시간 트렌드 RSS 무할당 파서 &amp; 공정위 표시광고 정적 린터.
///   ▸ <see cref="Parse"/>: RSS 텍스트를 <see cref="ReadOnlySpan{T}"/> 로 1 회 스캔 → &lt;item&gt; 별 &lt;title&gt;(CDATA 허용) ·
///     &lt;ht:approx_traffic&gt;("20,000+") · &lt;ht:prev_traffic&gt;(선택)를 <see cref="TrendSpike"/> 로 추출(0 B).
///     가속도 = 직전 대비 증가율 %(prev 있음) → 없으면 피드 최대 트래픽 대비 ‰/10.
///   ▸ <see cref="Lint"/>: 쿠팡/아마존 필수 고지 원문을 공백 정규화 비교 · 첫머리 위치 검사(0 B).
///   ▸ <see cref="MockFeed"/>: 오프라인 · 단위 테스트용 결정론적 피드.
/// </summary>
public static class RealtimeTrendRssAndAffiliateValidator
{
    public const int MaxSpikes = 16, MaxAcceleration = 999;
    /// <summary>고지 문구가 시작돼야 하는 첫머리 범위(문자) — "더보기" 접힘 전 노출 구간.</summary>
    public const int TopWindowChars = 120;

    /// <summary>결정론적 Mock 피드(Google Trends RSS 형식 · prev_traffic 확장).</summary>
    public const string MockFeed =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\" xmlns:ht=\"https://trends.google.com/trends/trendingsearches/daily\"><channel>" +
        "<title>Daily Search Trends</title>" +
        "<item><title>에어프라이어 레시피</title><ht:approx_traffic>50,000+</ht:approx_traffic><ht:prev_traffic>20,000+</ht:prev_traffic></item>" +
        "<item><title><![CDATA[무선 이어폰 할인]]></title><ht:approx_traffic>20,000+</ht:approx_traffic><ht:prev_traffic>18,000+</ht:prev_traffic></item>" +
        "<item><title>캠핑 의자</title><ht:approx_traffic>100,000+</ht:approx_traffic><ht:prev_traffic>25,000+</ht:prev_traffic></item>" +
        "<item><title>로봇청소기 비교</title><ht:approx_traffic>10,000+</ht:approx_traffic></item>" +
        "</channel></rss>";

    private const string Item = "<item>", ItemEnd = "</item>", TitleTag = "<title>", TitleEnd = "</title>";
    private const string TrafficTag = "<ht:approx_traffic>", TrafficEnd = "</ht:approx_traffic>";
    private const string PrevTag = "<ht:prev_traffic>", PrevEnd = "</ht:prev_traffic>";
    private const string CdataOpen = "<![CDATA[", CdataClose = "]]>";

    /// <summary>RSS → 급상승 토픽(최대 dst.Length) · 반환 = 개수. 형식 위반 item 은 건너뜀. 무할당.</summary>
    public static int Parse(ReadOnlySpan<char> feed, Span<TrendSpike> dst)
    {
        var n = 0;
        var pos = 0;
        var maxTraffic = 0;
        while (n < dst.Length)
        {
            var s = feed[pos..].IndexOf(Item, StringComparison.Ordinal);
            if (s < 0) break;
            s += pos + Item.Length;
            var e = feed[s..].IndexOf(ItemEnd, StringComparison.Ordinal);
            if (e < 0) break;
            var body = feed.Slice(s, e);
            pos = s + e + ItemEnd.Length;

            if (!TryElement(body, TitleTag, TitleEnd, out var ts, out var tl)) continue;
            var title = body.Slice(ts, tl);
            if (title.StartsWith(CdataOpen, StringComparison.Ordinal) && title.EndsWith(CdataClose, StringComparison.Ordinal))
            {
                ts += CdataOpen.Length;
                tl -= CdataOpen.Length + CdataClose.Length;
            }
            var trimmed = body.Slice(ts, tl);
            var lead = trimmed.Length - trimmed.TrimStart().Length;
            tl = trimmed.Trim().Length;
            ts += lead;
            if (tl == 0) continue;

            var traffic = TryElement(body, TrafficTag, TrafficEnd, out var a, out var al) ? ParseTraffic(body.Slice(a, al)) : 0;
            var prev = TryElement(body, PrevTag, PrevEnd, out var b, out var bl) ? ParseTraffic(body.Slice(b, bl)) : 0;
            if (traffic > maxTraffic) maxTraffic = traffic;
            dst[n++] = new TrendSpike(s + ts, tl, traffic, prev, prev > 0 ? GrowthPercent(traffic, prev) : -1);
        }
        // prev 없는 토픽 — 피드 최대 대비 상대 지수로 보정(2 패스 · 같은 버퍼).
        for (var i = 0; i < n; i++)
        {
            ref readonly var t = ref dst[i];
            if (t.Acceleration >= 0) continue;
            var acc = maxTraffic > 0 ? (int)((long)t.Traffic * 100 / maxTraffic) : 0;
            dst[i] = new TrendSpike(t.TitleStart, t.TitleLength, t.Traffic, 0, acc);
        }
        return n;
    }

    /// <summary>가속도 최대 토픽 인덱스(동점 = 트래픽 큰 쪽) · 없으면 −1.</summary>
    public static int TopIndex(ReadOnlySpan<TrendSpike> spikes)
    {
        var best = -1;
        for (var i = 0; i < spikes.Length; i++)
        {
            if (best < 0 || spikes[i].Acceleration > spikes[best].Acceleration
                || (spikes[i].Acceleration == spikes[best].Acceleration && spikes[i].Traffic > spikes[best].Traffic)) best = i;
        }
        return best;
    }

    /// <summary>"50,000+" · "1.2M+" 형식 → 정수(천 단위 구분 · K/M 접미 허용). 무할당.</summary>
    public static int ParseTraffic(ReadOnlySpan<char> s)
    {
        long v = 0;
        var frac = 0;
        var inFrac = false;
        foreach (var c in s.Trim())
        {
            if (c is >= '0' and <= '9')
            {
                v = v * 10 + (c - '0');
                if (inFrac) frac++;
                if (v > int.MaxValue) return int.MaxValue;
            }
            else if (c == '.') inFrac = true;
            else if (c is 'K' or 'k') { v *= 1_000; break; }
            else if (c is 'M' or 'm') { v *= 1_000_000; break; }
            else if (c != ',') break;
        }
        for (; frac > 0; frac--) v /= 10;
        return (int)Math.Min(v, int.MaxValue);
    }

    /// <summary>필수 고지 린트 — 원문(공백 정규화 일치) 위치 · 핵심어 존재로 판정. 무할당.</summary>
    public static AffiliateNoticeLint Lint(ReadOnlySpan<char> text, AffiliateNetwork network)
    {
        var notice = network == AffiliateNetwork.AmazonAssociates ? CompliantShortlinkLegalNoticeInjector.AmazonNotice : CompliantShortlinkLegalNoticeInjector.CoupangNotice;
        var at = IndexOfNormalized(text, notice);
        if (at >= 0) return at <= TopWindowChars ? AffiliateNoticeLint.Ok : AffiliateNoticeLint.Buried;
        return network == AffiliateNetwork.AmazonAssociates
            ? text.Contains("Amazon Associate", StringComparison.OrdinalIgnoreCase) || text.Contains("아마존", StringComparison.Ordinal) ? AffiliateNoticeLint.Altered : AffiliateNoticeLint.Missing
            : text.Contains("쿠팡", StringComparison.Ordinal) || text.Contains("파트너스", StringComparison.Ordinal) || text.Contains("수수료", StringComparison.Ordinal) ? AffiliateNoticeLint.Altered : AffiliateNoticeLint.Missing;
    }

    public static string LintText(AffiliateNoticeLint r) => r switch
    {
        AffiliateNoticeLint.Ok => "✔ 공정위 고지 통과",
        AffiliateNoticeLint.Altered => "⚠ 고지 문구 변형",
        AffiliateNoticeLint.Buried => "⚠ 고지 위치(첫머리 밖)",
        _ => "⛔ 고지 누락",
    };

    /// <summary>공백 연속 · 줄바꿈 차이를 무시한 부분 문자열 검색 — 일치 시작 인덱스(원문 기준) · 없으면 −1.</summary>
    private static int IndexOfNormalized(ReadOnlySpan<char> text, ReadOnlySpan<char> needle)
    {
        needle = needle.Trim();
        if (needle.IsEmpty) return 0;
        var first = needle[0];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != first) continue;
            int t = i, k = 0;
            while (k < needle.Length && t < text.Length)
            {
                var ws1 = char.IsWhiteSpace(needle[k]);
                var ws2 = char.IsWhiteSpace(text[t]);
                if (ws1 && ws2)
                {
                    while (k < needle.Length && char.IsWhiteSpace(needle[k])) k++;
                    while (t < text.Length && char.IsWhiteSpace(text[t])) t++;
                    continue;
                }
                if (ws1 || ws2 || needle[k] != text[t]) break;
                k++; t++;
            }
            if (k == needle.Length) return i;
        }
        return -1;
    }

    private static bool TryElement(ReadOnlySpan<char> body, string open, string close, out int start, out int length)
    {
        start = length = 0;
        var s = body.IndexOf(open, StringComparison.Ordinal);
        if (s < 0) return false;
        s += open.Length;
        var e = body[s..].IndexOf(close, StringComparison.Ordinal);
        if (e < 0) return false;
        start = s; length = e;
        return true;
    }

    private static int GrowthPercent(int traffic, int prev) => Math.Clamp((int)(((long)traffic - prev) * 100 / prev), 0, MaxAcceleration);
}
