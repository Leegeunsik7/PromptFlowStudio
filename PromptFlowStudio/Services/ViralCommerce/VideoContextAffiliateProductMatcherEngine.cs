namespace PromptFlowStudio.Services.ViralCommerce;

public enum AffiliateNetwork : byte { CoupangPartners, AmazonAssociates }

/// <summary>
/// [PromptFlow Studio v17.97.0] 🧠 무할당 영상 맥락 &amp; 제휴 상품 스마트 매처.
///   ▸ 숏폼 대본을 정규형(공백 제거 · 소문자) 고정 char 버퍼로 1 회 복사 → 인메모리 제휴 카탈로그(쿠팡 파트너스 · 아마존 어소시에이트 모의 품목)의
///     제품명 · 브랜드 · 카테고리 · 별칭 엔티티를 <see cref="ReadOnlySpan{T}"/> 부분 일치로 추출.
///   ▸ 연관도 0~100 = 제품명 <see cref="NameScore"/> + 브랜드 <see cref="BrandScore"/> + 카테고리 <see cref="CategoryScore"/> + 별칭 <see cref="AliasScore"/>×(최대 <see cref="AliasMaxHits"/>).
///   ▸ 연관도 ≥ <see cref="MinRelevance"/> 후보를 기대 수익(연관도 × EPC) 내림차순 상위 <see cref="TopN"/> 선별 — 매칭 경로 힙 할당 0.
/// </summary>
public sealed class VideoContextAffiliateProductMatcherEngine
{
    public const int ScriptCap = 2048, TermCap = 48, TopN = 3, MinRelevance = 30;
    public const int NameScore = 50, BrandScore = 20, CategoryScore = 20, AliasScore = 15, AliasMaxHits = 3;

    // ── 인메모리 제휴 카탈로그(모의) ──
    public static readonly string[] Names = { "아이폰16 프로 맥세이프 케이스", "스마트폰 짐벌 OM7", "LED 휴대용 링라이트", "무선 핀마이크 Mic Mini", "원터치 캠핑 텐트", "할로윈 특수분장 키트", "글리터 아이섀도우 팔레트", "에어프라이어 5.5L", "갤럭시 S25 투명 케이스", "ND 필터 렌즈 클립", "블루투스 삼각대 셀카봉", "보조배터리 20000mAh" };
    public static readonly string[] Brands = { "애플", "DJI", "울란지", "RODE", "코베아", "스노우볼", "클리오", "코소리", "삼성", "프리웰", "울란지", "앤커" };
    public static readonly string[] Categories = { "폰케이스", "짐벌", "조명", "마이크", "텐트", "분장", "메이크업", "주방가전", "폰케이스", "렌즈", "삼각대", "충전" };
    private static readonly string[] Aliases = { "아이폰16,맥세이프,케이스", "짐벌,흔들림,안정화,야간 촬영", "조명,링라이트,야간 촬영,라이트", "마이크,핀마이크,녹음,음질", "캠핑,텐트,차박", "할로윈,분장,좀비,특수분장", "글리터,섀도우,메이크업,팔레트", "에어프라이어,요리,레시피", "갤럭시,S25,케이스", "렌즈,필터,카메라", "삼각대,셀카봉,타임랩스", "배터리,충전,보조배터리" };
    public static readonly AffiliateNetwork[] Networks = { AffiliateNetwork.CoupangPartners, AffiliateNetwork.CoupangPartners, AffiliateNetwork.CoupangPartners, AffiliateNetwork.AmazonAssociates, AffiliateNetwork.CoupangPartners, AffiliateNetwork.CoupangPartners, AffiliateNetwork.CoupangPartners, AffiliateNetwork.AmazonAssociates, AffiliateNetwork.CoupangPartners, AffiliateNetwork.AmazonAssociates, AffiliateNetwork.CoupangPartners, AffiliateNetwork.AmazonAssociates };
    public static readonly int[] PriceKrw = { 29000, 149000, 25000, 89000, 129000, 19000, 32000, 119000, 15000, 39000, 22000, 45000 };
    /// <summary>수수료율(bp · 1/100 %).</summary>
    public static readonly int[] CommissionBp = { 300, 300, 300, 400, 300, 300, 300, 400, 300, 400, 300, 400 };
    /// <summary>클릭 → 구매 전환율(‰).</summary>
    public static readonly int[] ConvPermille = { 45, 18, 40, 22, 12, 55, 38, 15, 50, 25, 42, 30 };
    public static readonly string[] Urls =
    {
        "https://www.coupang.com/vp/products/7810001", "https://www.coupang.com/vp/products/7810002", "https://www.coupang.com/vp/products/7810003",
        "https://www.amazon.com/dp/B0PFS00004", "https://www.coupang.com/vp/products/7810005", "https://www.coupang.com/vp/products/7810006",
        "https://www.coupang.com/vp/products/7810007", "https://www.amazon.com/dp/B0PFS00008", "https://www.coupang.com/vp/products/7810009",
        "https://www.amazon.com/dp/B0PFS00010", "https://www.coupang.com/vp/products/7810011", "https://www.amazon.com/dp/B0PFS00012",
    };

    public static int CatalogSize => Names.Length;

    private readonly char[] _script = new char[ScriptCap];
    private readonly int[] _relevance = new int[Names.Length];
    private readonly byte[] _hitMask = new byte[Names.Length];   // 1 = 제품명 · 2 = 브랜드 · 4 = 카테고리
    private readonly int[] _aliasHits = new int[Names.Length];
    private readonly int[] _top = new int[TopN];
    private int _scriptLen, _topCount, _entityHits;

    public int TopCount => _topCount;
    public int Top(int i) => _top[i];
    public int Relevance(int product) => _relevance[product];
    public bool NameHit(int product) => (_hitMask[product] & 1) != 0;
    public bool BrandHit(int product) => (_hitMask[product] & 2) != 0;
    public bool CategoryHit(int product) => (_hitMask[product] & 4) != 0;
    public int AliasHits(int product) => _aliasHits[product];
    /// <summary>마지막 매칭에서 추출된 엔티티(제품명 · 브랜드 · 카테고리 · 별칭) 총 적중 수.</summary>
    public int EntityHits => _entityHits;
    public int ScriptLength => _scriptLen;
    public long Matches { get; private set; }
    public long Truncated { get; private set; }

    public static string NetworkName(AffiliateNetwork n) => n == AffiliateNetwork.AmazonAssociates ? "아마존 어소시에이트" : "쿠팡 파트너스";

    /// <summary>클릭당 기대 수수료(원) = 가격 × 수수료율 × 전환율.</summary>
    public static double Epc(int product) => PriceKrw[product] * (CommissionBp[product] / 10_000.0) * (ConvPermille[product] / 1000.0);

    /// <summary>전환 1 건 수수료(원).</summary>
    public static int CommissionPerSale(int product) => PriceKrw[product] * CommissionBp[product] / 10_000;

    /// <summary>대본 정규형 적재(공백 제거 · 소문자) — 초과분 절단. 무할당.</summary>
    public void SetScript(ReadOnlySpan<char> script)
    {
        var n = Normalize(script, _script);
        if (n == _script.Length && script.Length > n) Truncated++;
        _scriptLen = n;
    }

    /// <summary>전 카탈로그 연관도 산출 → 상위 <see cref="TopN"/> 선별. 반환 = 선별 수. 핫패스 — 무할당.</summary>
    public int Match()
    {
        ReadOnlySpan<char> s = _script.AsSpan(0, _scriptLen);
        _entityHits = 0;
        for (var p = 0; p < Names.Length; p++)
        {
            byte mask = 0;
            var score = 0;
            if (Contains(s, Names[p])) { mask |= 1; score += NameScore; _entityHits++; }
            if (Contains(s, Brands[p])) { mask |= 2; score += BrandScore; _entityHits++; }
            if (Contains(s, Categories[p])) { mask |= 4; score += CategoryScore; _entityHits++; }
            var hits = 0;
            ReadOnlySpan<char> list = Aliases[p];
            while (!list.IsEmpty)
            {
                var c = list.IndexOf(',');
                var seg = c < 0 ? list : list[..c];
                if (Contains(s, seg)) hits++;
                list = c < 0 ? default : list[(c + 1)..];
            }
            _entityHits += hits;
            score += AliasScore * Math.Min(hits, AliasMaxHits);
            _hitMask[p] = mask;
            _aliasHits[p] = hits;
            _relevance[p] = Math.Min(100, score);
        }

        // 기대 수익(연관도 × EPC) 내림차순 삽입 선별 · 동점은 카탈로그 순
        _topCount = 0;
        for (var p = 0; p < Names.Length; p++)
        {
            if (_relevance[p] < MinRelevance) continue;
            var key = Expected(p);
            var at = _topCount;
            while (at > 0 && Expected(_top[at - 1]) < key) at--;
            if (at >= TopN) continue;
            var end = Math.Min(_topCount, TopN - 1);
            for (var k = end; k > at; k--) _top[k] = _top[k - 1];
            _top[at] = p;
            if (_topCount < TopN) _topCount++;
        }
        Matches++;
        return _topCount;
    }

    /// <summary>단일 용어 연관 적중 판정(테스트 · UI 하이라이트용). 무할당.</summary>
    public bool ContainsTerm(ReadOnlySpan<char> term) => Contains(_script.AsSpan(0, _scriptLen), term);

    public double Expected(int product) => _relevance[product] * Epc(product);

    public void Reset()
    {
        _scriptLen = _topCount = _entityHits = 0;
        Array.Clear(_relevance); Array.Clear(_hitMask); Array.Clear(_aliasHits);
        Matches = Truncated = 0;
    }

    private static bool Contains(ReadOnlySpan<char> normScript, ReadOnlySpan<char> term)
    {
        Span<char> t = stackalloc char[TermCap];
        var n = Normalize(term, t);
        return n > 0 && normScript.IndexOf(t[..n], StringComparison.Ordinal) >= 0;
    }

    /// <summary>공백 제거 · 소문자 정규형 복사. 반환 = 기록 길이.</summary>
    private static int Normalize(ReadOnlySpan<char> src, Span<char> dst)
    {
        var n = 0;
        foreach (var ch in src)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (n == dst.Length) break;
            dst[n++] = char.ToLowerInvariant(ch);
        }
        return n;
    }
}
