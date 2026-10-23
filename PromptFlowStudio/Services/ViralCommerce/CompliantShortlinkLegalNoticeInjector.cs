using System.Text;

namespace PromptFlowStudio.Services.ViralCommerce;

/// <summary>
/// [PromptFlow Studio v17.97.0] ⚖️ 준법형 브랜디드 숏링크 &amp; 공정위 표시광고 인젝터.
///   ▸ 원본 URL + <c>subId</c> · <c>utm_campaign</c>(RFC 3986 비예약 문자 외 UTF-8 퍼센트 인코딩)을 고정 버퍼에 결합 →
///     FNV-1a 64 해시 mod 62⁸ 를 8 자리 Base62 로 인코딩한 브랜디드 숏링크 <c>pfs.link/{id}</c>.
///   ▸ 공정거래위원회 「추천·보증 등에 관한 표시·광고 심사지침」 경제적 이해관계 고지(쿠팡 파트너스 필수 문구) ·
///     미국 FTC 16 CFR Part 255 아마존 어소시에이트 고지를 네트워크별로 조립해 v17.95 골든아워 고정 댓글 · v17.94 플랫폼 설명란 본문 <b>최상단</b>에 주입.
///   ▸ 링크 수 <see cref="RealtimeAffiliateAttributionSafetyGuard.MaxLinksPerVideo"/>(3) 상한 · 조립 경로 힙 할당 0.
/// </summary>
public sealed class CompliantShortlinkLegalNoticeInjector
{
    public const int UrlCap = 512, NoticeCap = 512, PinnedCap = 2048, DescCap = 5000, IdLen = 8;
    public const string Domain = "pfs.link/";
    public const string AdLabel = "📢 [광고] ";
    public const string CoupangNotice = "이 포스팅은 쿠팡 파트너스 활동의 일환으로, 이에 따른 일정액의 수수료를 제공받습니다.";
    public const string AmazonNotice = "As an Amazon Associate I earn from qualifying purchases. (아마존 어소시에이트로서 적격 구매 시 수수료를 받습니다.)";
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const ulong Base62Pow8 = 218_340_105_584_896UL;   // 62⁸

    private readonly char[] _url = new char[UrlCap];
    private readonly char[] _notice = new char[NoticeCap];
    private readonly char[] _pinned = new char[PinnedCap];
    private readonly char[] _desc = new char[DescCap];
    private readonly char[] _shortIds = new char[RealtimeAffiliateAttributionSafetyGuard.MaxLinksPerVideo * IdLen];
    private readonly int[] _linkProduct = new int[RealtimeAffiliateAttributionSafetyGuard.MaxLinksPerVideo];
    private int _urlLen, _noticeLen, _pinnedLen, _descLen, _linkCount;

    public ReadOnlySpan<char> TrackedUrl => _url.AsSpan(0, _urlLen);
    public ReadOnlySpan<char> Notice => _notice.AsSpan(0, _noticeLen);
    public ReadOnlySpan<char> PinnedComment => _pinned.AsSpan(0, _pinnedLen);
    public ReadOnlySpan<char> Description => _desc.AsSpan(0, _descLen);
    public int LinkCount => _linkCount;
    public int LinkProduct(int i) => _linkProduct[i];
    public ReadOnlySpan<char> ShortId(int i) => _shortIds.AsSpan(i * IdLen, IdLen);
    public long Composed { get; private set; }
    public long CappedLinks { get; private set; }

    /// <summary>원본 URL + subId + 캠페인 태그 결합(기존 쿼리 존재 시 '&amp;'). 반환 = 길이 · 버퍼 초과 시 −1. 무할당.</summary>
    public int BuildTrackedUrl(ReadOnlySpan<char> url, ReadOnlySpan<char> subId, ReadOnlySpan<char> campaign)
    {
        var d = _url.AsSpan();
        var n = Put(d, 0, url);
        if (n < 0) return Fail();
        n = Put(d, n, url.IndexOf('?') >= 0 ? "&subId=" : "?subId=");
        if (n >= 0) n = PutEncoded(d, n, subId);
        if (n >= 0) n = Put(d, n, "&utm_campaign=");
        if (n >= 0) n = PutEncoded(d, n, campaign);
        return n < 0 ? Fail() : _urlLen = n;
    }

    /// <summary>추적 URL → 8 자리 Base62 ID(결정적). 무할당.</summary>
    public static void EncodeShortId(ReadOnlySpan<char> trackedUrl, Span<char> dst8)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in trackedUrl) { h ^= ch; h *= 1099511628211UL; }
        var v = h % Base62Pow8;
        for (var i = IdLen - 1; i >= 0; i--) { dst8[i] = Base62[(int)(v % 62)]; v /= 62; }
    }

    /// <summary>Base62 8 자리 디코드(역검증용) · 형식 위반 시 −1.</summary>
    public static long DecodeShortId(ReadOnlySpan<char> id)
    {
        if (id.Length != IdLen) return -1;
        long v = 0;
        foreach (var ch in id)
        {
            var k = Base62.IndexOf(ch);
            if (k < 0) return -1;
            v = v * 62 + k;
        }
        return v;
    }

    /// <summary>
    /// 상위 매칭 상품(최대 3)으로 고지문 · 숏링크 · 고정 댓글 · 설명란 패키지 조립.
    /// <paramref name="pinnedBody"/> = v17.95 골든아워 고정 댓글 본문 · <paramref name="descBody"/> = v17.94 설명란 본문.
    /// 반환 = 링크 수. 핫패스 — 무할당.
    /// </summary>
    public int Compose(VideoContextAffiliateProductMatcherEngine matcher, ReadOnlySpan<char> subId, ReadOnlySpan<char> campaign, ReadOnlySpan<char> pinnedBody, ReadOnlySpan<char> descBody)
    {
        _linkCount = 0;
        bool coupang = false, amazon = false;
        for (var i = 0; i < matcher.TopCount; i++)
        {
            if (_linkCount == RealtimeAffiliateAttributionSafetyGuard.MaxLinksPerVideo) { CappedLinks++; break; }
            var p = matcher.Top(i);
            if (BuildTrackedUrl(VideoContextAffiliateProductMatcherEngine.Urls[p], subId, campaign) < 0) continue;
            EncodeShortId(TrackedUrl, _shortIds.AsSpan(_linkCount * IdLen, IdLen));
            _linkProduct[_linkCount++] = p;
            if (VideoContextAffiliateProductMatcherEngine.Networks[p] == AffiliateNetwork.AmazonAssociates) amazon = true; else coupang = true;
        }

        // 고지문: 링크가 없으면 비움(광고 아님) · 네트워크별 필수 문구 병기
        var nb = _notice.AsSpan();
        var nn = 0;
        if (_linkCount > 0)
        {
            nn = Put(nb, nn, AdLabel);
            if (coupang) nn = Put(nb, nn, CoupangNotice);
            if (coupang && amazon) nn = Put(nb, nn, " ");
            if (amazon) nn = Put(nb, nn, AmazonNotice);
        }
        _noticeLen = Math.Max(0, nn);

        _pinnedLen = Package(_pinned, "📌 ", pinnedBody);
        _descLen = Package(_desc, "", descBody);
        Composed++;
        return _linkCount;
    }

    /// <summary>본문 최상단 고지 여부 — 첫 줄이 광고 표기 + 네트워크별 필수 문구를 모두 포함해야 통과. 무할당.</summary>
    public static bool HasTopNotice(ReadOnlySpan<char> text, bool coupang, bool amazon)
    {
        if (!coupang && !amazon) return true;
        var nl = text.IndexOf('\n');
        var first = nl < 0 ? text : text[..nl];
        if (first.IndexOf("[광고]", StringComparison.Ordinal) < 0) return false;
        if (coupang && first.IndexOf(CoupangNotice, StringComparison.Ordinal) < 0) return false;
        return !amazon || first.IndexOf(AmazonNotice, StringComparison.Ordinal) >= 0;
    }

    public bool UsesNetwork(AffiliateNetwork n)
    {
        for (var i = 0; i < _linkCount; i++) if (VideoContextAffiliateProductMatcherEngine.Networks[_linkProduct[i]] == n) return true;
        return false;
    }

    public void Reset()
    {
        _urlLen = _noticeLen = _pinnedLen = _descLen = _linkCount = 0;
        Composed = CappedLinks = 0;
    }

    // ── "{고지}\n🛒 {상품} 👉 pfs.link/{id}\n…\n\n{본문}" ──
    private int Package(char[] buf, ReadOnlySpan<char> bodyPrefix, ReadOnlySpan<char> body)
    {
        var d = buf.AsSpan();
        var n = 0;
        if (_noticeLen > 0)
        {
            n = Put(d, n, Notice);
            n = Put(d, n, "\n");
        }
        for (var i = 0; i < _linkCount && n >= 0; i++)
        {
            n = Put(d, n, "🛒 ");
            if (n >= 0) n = Put(d, n, VideoContextAffiliateProductMatcherEngine.Names[_linkProduct[i]]);
            if (n >= 0) n = Put(d, n, " 👉 ");
            if (n >= 0) n = Put(d, n, Domain);
            if (n >= 0) n = Put(d, n, ShortId(i));
            if (n >= 0) n = Put(d, n, "\n");
        }
        if (n < 0) return 0;
        var b = body.Trim();
        if (b.IsEmpty) return n;
        var m = Put(d, n, _linkCount > 0 ? "\n" : "");
        if (m >= 0) m = Put(d, m, bodyPrefix);
        if (m < 0) return n;
        var room = d.Length - m;
        if (b.Length > room)
        {
            var k = room - 1;
            if (k > 0 && char.IsHighSurrogate(b[k - 1])) k--;
            if (k <= 0) return m;
            b[..k].CopyTo(d[m..]);
            d[m + k] = '…';
            return m + k + 1;
        }
        b.CopyTo(d[m..]);
        return m + b.Length;
    }

    private int Fail() { _urlLen = 0; return -1; }

    /// <summary>복사 · 반환 = 새 길이 · 공간 부족 = −1.</summary>
    private static int Put(Span<char> dst, int n, ReadOnlySpan<char> s)
    {
        if (n < 0 || n + s.Length > dst.Length) return -1;
        s.CopyTo(dst[n..]);
        return n + s.Length;
    }

    /// <summary>RFC 3986 비예약 문자(A–Z a–z 0–9 - . _ ~) 외 UTF-8 퍼센트 인코딩.</summary>
    private static int PutEncoded(Span<char> dst, int n, ReadOnlySpan<char> s)
    {
        Span<byte> u8 = stackalloc byte[4];
        var i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '.' || ch == '_' || ch == '~')
            {
                if (n >= dst.Length) return -1;
                dst[n++] = ch;
                i++;
                continue;
            }
            if (Rune.DecodeFromUtf16(s[i..], out var r, out var used) != System.Buffers.OperationStatus.Done) r = Rune.ReplacementChar;
            var w = r.EncodeToUtf8(u8);
            if (n + w * 3 > dst.Length) return -1;
            for (var k = 0; k < w; k++)
            {
                dst[n++] = '%';
                dst[n++] = "0123456789ABCDEF"[u8[k] >> 4];
                dst[n++] = "0123456789ABCDEF"[u8[k] & 0xF];
            }
            i += Math.Max(1, used);
        }
        return n;
    }
}
