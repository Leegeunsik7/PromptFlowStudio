using PromptFlowStudio.Services.CompetitorSpy;
using PromptFlowStudio.Services.ViralCommerce;

namespace PromptFlowStudio.Services.Ui;

/// <summary>① 스파이 — 역공학 훅 DNA(대항 대본 본문은 컨텍스트 버퍼 <see cref="ViralGrowthStrategyPipelineContext.Script"/>).</summary>
public struct OutlierHookDna
{
    public HookPattern Hook;
    public int ConfidencePermille;
    /// <summary>권장 초당 컷 빈도(milli · 1500 = 1.5 cut/s).</summary>
    public int CutsPerSecMilli;
    public int ScriptLength;
    public int Revision;
}

/// <summary>② 트렌드재킹 — 대본 연관 키워드 · 해시태그(#Shorts #릴스 #키워드).</summary>
public struct InjectedTrendMetadata
{
    public int Traffic, Acceleration;
    public int KeywordLength, HashtagsLength, HashtagCount;
    public int Revision;
}

/// <summary>③ 제휴 커머스 — subId 인코딩 숏링크 · 공정위 필수 문구 블록 · 린트 결과.</summary>
public struct AffiliateLinkPackage
{
    public AffiliateNetwork Network;
    public int ShortLinkLength, NoticeLength;
    public AffiliateNoticeLint Lint;
    public int Revision;
}

/// <summary>④ 골든아워 — 업로드 후 1 분 고정 댓글(고지 → 트렌드 질문 → 숏링크).</summary>
public struct GoldenPinnedComment
{
    public int Length, PinDelayMs;
    public AffiliateNoticeLint Lint;
    public int Revision;
}

/// <summary>
/// [PromptFlow Studio v18.0.6] 📈 바이럴 그로스 룸 4 탭 관통 전략 컨텍스트 — 스파이 → 트렌드 → 커머스 → 댓글을 단일 데이터 플로우로 결속.
///   ▸ 모든 텍스트는 생성자에서 1 회 할당한 고정 char 버퍼에 Span 복사로 조립 — 체인 갱신 루프 0 B.
///   ▸ 상류 단계가 바뀌면 하류 단계 Revision 이 낡아져 <see cref="IsComplete"/> 가 false(낡은 기획안 인계 차단).
///   ▸ <see cref="Fingerprint"/>: 대본 · 훅 · 해시태그 FNV-1a — 숏폼 팩토리 인계 일치성 검증용.
/// </summary>
public sealed class ViralGrowthStrategyPipelineContext
{
    public const int ScriptCap = 1024, KeywordCap = 64, TagsCap = 256, LinkCap = 64, NoticeCap = 256, PinnedCap = 1024, GuideCap = 128;
    public const int PinDelayMs = 60_000, DefaultCutsMilli = 1_500;
    public const string Campaign = "pfs_growth";

    /// <summary>훅 유형별 대항 오프너(평서 · 의문 · 반전 · 공포/경고 · 호기심).</summary>
    private static readonly string[] CounterOpeners =
    {
        "결론부터 말씀드리면,", "혹시 아직도 이렇게 하세요?", "다들 반대로 알고 있습니다.", "이거 모르면 손해 봅니다.", "끝까지 보면 이유가 나옵니다.",
    };

    private readonly ViralHookAudiovisualReverseEngineer _decompiler = new();
    private readonly char[] _script = new char[ScriptCap], _keyword = new char[KeywordCap], _tags = new char[TagsCap];
    private readonly char[] _link = new char[LinkCap], _notice = new char[NoticeCap], _pinned = new char[PinnedCap], _guide = new char[GuideCap];
    private readonly CompliantShortlinkLegalNoticeInjector _linker = new();
    private readonly TrendSpike[] _spikes = new TrendSpike[RealtimeTrendRssAndAffiliateValidator.MaxSpikes];
    private OutlierHookDna _hook;
    private InjectedTrendMetadata _trend;
    private AffiliateLinkPackage _aff;
    private GoldenPinnedComment _pin;
    private int _guideLen, _rev;

    public ref readonly OutlierHookDna Hook => ref _hook;
    public ref readonly InjectedTrendMetadata Trend => ref _trend;
    public ref readonly AffiliateLinkPackage Affiliate => ref _aff;
    public ref readonly GoldenPinnedComment Pinned => ref _pin;

    public ReadOnlySpan<char> Script => _script.AsSpan(0, _hook.ScriptLength);
    public ReadOnlySpan<char> HookGuide => _guide.AsSpan(0, _guideLen);
    public ReadOnlySpan<char> TrendKeyword => _keyword.AsSpan(0, _trend.KeywordLength);
    public ReadOnlySpan<char> Hashtags => _tags.AsSpan(0, _trend.HashtagsLength);
    public ReadOnlySpan<char> ShortLink => _link.AsSpan(0, _aff.ShortLinkLength);
    public ReadOnlySpan<char> NoticeBlock => _notice.AsSpan(0, _aff.NoticeLength);
    public ReadOnlySpan<char> PinnedComment => _pinned.AsSpan(0, _pin.Length);

    /// <summary>인계용 0-카피 슬라이스(버퍼 공유 · <see cref="Revision"/> 로 낡음 판정).</summary>
    public ReadOnlyMemory<char> ScriptMemory => _script.AsMemory(0, _hook.ScriptLength);
    public ReadOnlyMemory<char> HookGuideMemory => _guide.AsMemory(0, _guideLen);
    public ReadOnlyMemory<char> HashtagsMemory => _tags.AsMemory(0, _trend.HashtagsLength);

    /// <summary>단계 갱신마다 증가(전역).</summary>
    public int Revision => _rev;
    public long Chains { get; private set; }
    public int StageCount => (_hook.Revision > 0 ? 1 : 0) + (_trend.Revision > _hook.Revision ? 1 : 0)
                           + (_aff.Revision > _trend.Revision ? 1 : 0) + (_pin.Revision > _aff.Revision ? 1 : 0);
    /// <summary>4 단계가 상류 → 하류 순서로 모두 최신 · 공정위 린트 통과.</summary>
    public bool IsComplete => _hook.Revision > 0 && _trend.Revision > _hook.Revision && _aff.Revision > _trend.Revision && _pin.Revision > _aff.Revision
                              && _aff.Lint == AffiliateNoticeLint.Ok && _pin.Lint == AffiliateNoticeLint.Ok;

    /// <summary>① 경쟁 아웃라이어 훅 분류 → 권장 컷 빈도 · 대항 대본. 컷 정보 없으면 1.5 cut/s.</summary>
    public bool ApplyOutlier(ReadOnlySpan<char> competitorHook, int cutCount, int durationMs)
    {
        competitorHook = competitorHook.Trim();
        if (competitorHook.IsEmpty) return false;
        var pattern = _decompiler.ClassifyHook(competitorHook, out var conf);
        var cps = cutCount > 0 && durationMs > 0 ? (int)Math.Clamp((long)cutCount * 1_000_000 / durationMs, 300, 4_000) : DefaultCutsMilli;
        var d = _script.AsSpan();
        var n = Put(d, 0, CounterOpeners[(int)pattern]);
        n = Put(d, n, " ");
        n = Put(d, n, competitorHook);
        n = Put(d, n, " — 제가 직접 검증한 핵심만 30초 안에 정리합니다.");
        _hook.Hook = pattern;
        _hook.ConfidencePermille = (int)(conf * 1000);
        _hook.CutsPerSecMilli = cps;
        _hook.ScriptLength = n;
        _hook.Revision = ++_rev;

        var g = _guide.AsSpan();
        var k = Put(g, 0, ViralHookAudiovisualReverseEngineer.PatternIcons[(int)pattern]);
        k = Put(g, k, " ");
        k = Put(g, k, ViralHookAudiovisualReverseEngineer.PatternNames[(int)pattern]);
        k = Put(g, k, " 훅 · 초당 ");
        k = PutMilli(g, k, cps);
        k = Put(g, k, "컷 · 첫 3초 대항 오프너");
        _guideLen = k;
        return true;
    }

    /// <summary>② RSS 급상승 1 위 키워드 주입 — 대본 말미 연관 문장 + #Shorts #릴스 #키워드.</summary>
    public bool InjectTrend(ReadOnlySpan<char> rssFeed)
    {
        if (_hook.Revision == 0) return false;
        var n = RealtimeTrendRssAndAffiliateValidator.Parse(rssFeed, _spikes);
        var top = RealtimeTrendRssAndAffiliateValidator.TopIndex(_spikes.AsSpan(0, n));
        if (top < 0) return false;
        ref readonly var sp = ref _spikes[top];
        var kw = sp.Title(rssFeed);
        if (kw.Length > KeywordCap) kw = kw[..KeywordCap];
        kw.CopyTo(_keyword);

        // 대본 = ① 본문(오프너 이후) 유지 + 트렌드 연관 문장 1 개(재주입 시 덮어씀).
        var d = _script.AsSpan();
        var baseLen = ((ReadOnlySpan<char>)d[.._hook.ScriptLength]).IndexOf(TrendMarker, StringComparison.Ordinal);
        var s = baseLen < 0 ? _hook.ScriptLength : baseLen;
        s = Put(d, s, TrendMarker);
        s = Put(d, s, kw);
        s = Put(d, s, " 이야기도 같이 담았습니다.");
        _hook.ScriptLength = s;

        var t = _tags.AsSpan();
        var m = Put(t, 0, "#Shorts #릴스 #");
        m = PutTag(t, m, kw);
        _trend.Traffic = sp.Traffic;
        _trend.Acceleration = sp.Acceleration;
        _trend.KeywordLength = kw.Length;
        _trend.HashtagsLength = m;
        _trend.HashtagCount = 3;
        _trend.Revision = ++_rev;
        return true;
    }

    /// <summary>③ 제휴 상품 URL + subId → 추적 URL → Base62 숏링크 · 필수 고지 블록 · 린트.</summary>
    public bool BuildAffiliate(ReadOnlySpan<char> productUrl, ReadOnlySpan<char> subId, AffiliateNetwork network)
    {
        if (_trend.Revision <= _hook.Revision || productUrl.IsEmpty) return false;
        if (_linker.BuildTrackedUrl(productUrl, subId, Campaign) < 0) return false;
        var l = _link.AsSpan();
        var n = Put(l, 0, CompliantShortlinkLegalNoticeInjector.Domain);
        CompliantShortlinkLegalNoticeInjector.EncodeShortId(_linker.TrackedUrl, l.Slice(n, CompliantShortlinkLegalNoticeInjector.IdLen));
        n += CompliantShortlinkLegalNoticeInjector.IdLen;

        var b = _notice.AsSpan();
        var k = Put(b, 0, CompliantShortlinkLegalNoticeInjector.AdLabel);
        k = Put(b, k, network == AffiliateNetwork.AmazonAssociates ? CompliantShortlinkLegalNoticeInjector.AmazonNotice : CompliantShortlinkLegalNoticeInjector.CoupangNotice);
        _aff.Network = network;
        _aff.ShortLinkLength = n;
        _aff.NoticeLength = k;
        _aff.Lint = RealtimeTrendRssAndAffiliateValidator.Lint(NoticeBlock, network);
        _aff.Revision = ++_rev;
        return true;
    }

    /// <summary>④ 1 분 고정 댓글 = 고지 블록(첫머리) → 트렌드 질문 → 🛒 숏링크 · 린트.</summary>
    public bool ComposePinned()
    {
        if (_aff.Revision <= _trend.Revision) return false;
        var d = _pinned.AsSpan();
        var n = Put(d, 0, NoticeBlock);
        n = Put(d, n, "\n💬 여러분은 ");
        n = Put(d, n, TrendKeyword);
        n = Put(d, n, ", 어떻게 생각하세요? 댓글로 알려주세요 👇\n🛒 영상 속 제품: https://");
        n = Put(d, n, ShortLink);
        _pin.Length = n;
        _pin.PinDelayMs = PinDelayMs;
        _pin.Lint = RealtimeTrendRssAndAffiliateValidator.Lint(PinnedComment, _aff.Network);
        _pin.Revision = ++_rev;
        return true;
    }

    /// <summary>⚡ 4 단계 순차 합성(스파이 → 트렌드 → 커머스 → 댓글) — 성공 = <see cref="IsComplete"/>. 무할당.</summary>
    public bool RunChain(ReadOnlySpan<char> competitorHook, int cutCount, int durationMs, ReadOnlySpan<char> rssFeed,
                         ReadOnlySpan<char> productUrl, ReadOnlySpan<char> subId, AffiliateNetwork network)
    {
        var ok = ApplyOutlier(competitorHook, cutCount, durationMs) && InjectTrend(rssFeed) && BuildAffiliate(productUrl, subId, network) && ComposePinned();
        if (ok) Chains++;
        return ok && IsComplete;
    }

    /// <summary>대본 · 훅 가이드 · 해시태그 FNV-1a 64(인계 일치성).</summary>
    public ulong Fingerprint => Hash(Script, Hash(HookGuide, Hash(Hashtags, 14695981039346656037UL)));

    public static ulong Hash(ReadOnlySpan<char> s, ulong h)
    {
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    public static string StageName(int stage) => stage switch { 0 => "🕵 스파이", 1 => "📡 트렌드", 2 => "🛒 커머스", _ => "📌 댓글" };

    public void Reset()
    {
        _hook = default; _trend = default; _aff = default; _pin = default;
        _guideLen = 0;
        _rev++;
    }

    private const string TrendMarker = " 요즘 화제인 ";

    private static int Put(Span<char> d, int at, ReadOnlySpan<char> s)
    {
        var n = Math.Min(s.Length, d.Length - at);
        s[..n].CopyTo(d[at..]);
        return at + n;
    }

    /// <summary>해시태그 본문 — 공백 · 기호 제거(한글 · 영숫자 · '_' 만).</summary>
    private static int PutTag(Span<char> d, int at, ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (at >= d.Length) break;
            if (char.IsLetterOrDigit(c) || c == '_') d[at++] = c;
        }
        return at;
    }

    /// <summary>milli → "1.5"(소수 1 자리 · 무할당).</summary>
    private static int PutMilli(Span<char> d, int at, int milli)
    {
        if (at + 8 > d.Length) return at;
        var tenths = (milli + 50) / 100;
        (tenths / 10).TryFormat(d[at..], out var w, default, System.Globalization.CultureInfo.InvariantCulture);
        at += w;
        d[at++] = '.';
        d[at++] = (char)('0' + tenths % 10);
        return at;
    }
}
