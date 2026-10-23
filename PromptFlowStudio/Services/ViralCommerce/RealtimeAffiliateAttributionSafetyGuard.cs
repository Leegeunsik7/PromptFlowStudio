namespace PromptFlowStudio.Services.ViralCommerce;

public enum ClickVerdict : byte { Valid, BlockedBot, BlockedBurst, BlockedBanned, UnknownLink }

/// <summary>
/// [PromptFlow Studio v17.97.0] 🛡️ 실시간 전환 어트리뷰션 &amp; 클릭 어뷰징 방어 가드.
///   ▸ User-Agent 봇/크롤러 시그니처(대소문자 무시 Span 탐색) 즉시 차단.
///   ▸ 동일 IP(FNV-1a 64 해시 링 <see cref="IpCap"/>) <see cref="BurstWindowSec"/> 초 내 <see cref="BurstClicks"/> 회 이상 = 다중 클릭 봇 → <see cref="BanSec"/> 초 차단.
///   ▸ 영상(<see cref="VideoCap"/> 슬롯)별 링크 <see cref="MaxLinksPerVideo"/>(3) 상한 강제 — 링크 도배 알고리즘 제재 방어.
///   ▸ 영상별 유효 클릭 · 전환 · 추정 수수료 누계 + 분 단위 시계열 링(<see cref="SeriesCap"/>) — 판정 · 집계 경로 힙 할당 0.
/// </summary>
public sealed class RealtimeAffiliateAttributionSafetyGuard
{
    public const int MaxLinksPerVideo = 3, VideoCap = 16, IpCap = 256, SeriesCap = 60, BurstClicks = 3, BanSec = 600;
    public const double BurstWindowSec = 1.0;
    private static readonly string[] BotSignatures = { "bot", "crawler", "spider", "headless", "curl", "python-requests", "scrapy", "wget" };

    // ── 영상 · 링크 ──
    private readonly int[] _links = new int[VideoCap];
    private readonly int[] _linkProduct = new int[VideoCap * MaxLinksPerVideo];
    private readonly long[] _valid = new long[VideoCap];
    private readonly long[] _conv = new long[VideoCap];
    private readonly long[] _commission = new long[VideoCap];

    // ── IP 해시 링 ──
    private readonly ulong[] _ip = new ulong[IpCap];
    private readonly double[] _winStart = new double[IpCap];
    private readonly int[] _winCount = new int[IpCap];
    private readonly double[] _banUntil = new double[IpCap];
    private int _ipHead, _ipCount;

    // ── 분 시계열 링 ──
    private readonly long[] _sMinute = new long[SeriesCap];
    private readonly int[] _sValid = new int[SeriesCap];
    private readonly int[] _sBlocked = new int[SeriesCap];
    private readonly int[] _sConv = new int[SeriesCap];
    private readonly long[] _sCommission = new long[SeriesCap];

    public long ValidClicks { get; private set; }
    public long BlockedBot { get; private set; }
    public long BlockedBurst { get; private set; }
    public long BlockedBanned { get; private set; }
    public long UnknownLinks { get; private set; }
    public long Conversions { get; private set; }
    public long CommissionKrw { get; private set; }
    public long LinkCapRejected { get; private set; }
    public long BlockedTotal => BlockedBot + BlockedBurst + BlockedBanned;

    public int LinkCount(int video) => (uint)video < VideoCap ? _links[video] : 0;
    public int LinkProduct(int video, int slot) => _linkProduct[video * MaxLinksPerVideo + slot];
    public long VideoValidClicks(int video) => _valid[video];
    public long VideoConversions(int video) => _conv[video];
    public long VideoCommission(int video) => _commission[video];

    /// <summary>전환율(%) — 전체 유효 클릭 대비.</summary>
    public double ConversionRatePct => ValidClicks == 0 ? 0 : Conversions * 100.0 / ValidClicks;
    /// <summary>차단율(%) — 전체 클릭 대비.</summary>
    public double BlockRatePct { get { var all = ValidClicks + BlockedTotal; return all == 0 ? 0 : BlockedTotal * 100.0 / all; } }

    public static string VerdictName(ClickVerdict v) => v switch
    {
        ClickVerdict.Valid => "유효",
        ClickVerdict.BlockedBot => "봇 UA 차단",
        ClickVerdict.BlockedBurst => "다중 클릭 차단",
        ClickVerdict.BlockedBanned => "IP 차단 중",
        _ => "미등록 링크",
    };

    /// <summary>영상 링크 등록 — 상한(3) 초과 시 거부. 반환 = 슬롯 · 실패 −1.</summary>
    public int TryRegisterLink(int video, int product)
    {
        if ((uint)video >= VideoCap) return -1;
        if (_links[video] >= MaxLinksPerVideo) { LinkCapRejected++; return -1; }
        var slot = _links[video]++;
        _linkProduct[video * MaxLinksPerVideo + slot] = product;
        return slot;
    }

    /// <summary>영상 링크 전체 해제(재발행 전).</summary>
    public void ClearLinks(int video)
    {
        if ((uint)video < VideoCap) _links[video] = 0;
    }

    /// <summary>클릭 판정 · 집계. 무할당.</summary>
    public ClickVerdict OnClick(int video, int slot, ReadOnlySpan<char> ip, ReadOnlySpan<char> userAgent, double nowUnix)
    {
        if ((uint)video >= VideoCap || (uint)slot >= (uint)_links[video]) { UnknownLinks++; return ClickVerdict.UnknownLink; }
        var bucket = Bucket(nowUnix);
        if (IsBot(userAgent)) { BlockedBot++; _sBlocked[bucket]++; return ClickVerdict.BlockedBot; }

        var k = IpSlot(Hash(ip));
        if (_banUntil[k] > nowUnix) { BlockedBanned++; _sBlocked[bucket]++; return ClickVerdict.BlockedBanned; }
        if (nowUnix - _winStart[k] >= BurstWindowSec) { _winStart[k] = nowUnix; _winCount[k] = 0; }
        if (++_winCount[k] >= BurstClicks)
        {
            _banUntil[k] = nowUnix + BanSec;
            BlockedBurst++;
            _sBlocked[bucket]++;
            return ClickVerdict.BlockedBurst;
        }
        ValidClicks++;
        _valid[video]++;
        _sValid[bucket]++;
        return ClickVerdict.Valid;
    }

    /// <summary>구매 전환 귀속(링크 등록 확인). 반환 = 귀속 여부.</summary>
    public bool OnConversion(int video, int slot, int commissionKrw, double nowUnix)
    {
        if ((uint)video >= VideoCap || (uint)slot >= (uint)_links[video]) { UnknownLinks++; return false; }
        var bucket = Bucket(nowUnix);
        Conversions++;
        CommissionKrw += commissionKrw;
        _conv[video]++;
        _commission[video] += commissionKrw;
        _sConv[bucket]++;
        _sCommission[bucket] += commissionKrw;
        return true;
    }

    /// <summary>UA 봇/크롤러 시그니처 포함 여부(빈 UA 도 봇). 무할당.</summary>
    public static bool IsBot(ReadOnlySpan<char> userAgent)
    {
        if (userAgent.Trim().IsEmpty) return true;
        foreach (var sig in BotSignatures)
            if (userAgent.IndexOf(sig, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>현재 차단 중인 IP 수.</summary>
    public int BannedIps(double nowUnix)
    {
        var n = 0;
        for (var i = 0; i < _ipCount; i++) if (_banUntil[i] > nowUnix) n++;
        return n;
    }

    /// <summary>최근 <paramref name="ago"/> 분 전 시계열(0 = 이번 분) — 버킷이 비었으면 0.</summary>
    public int SeriesValid(double nowUnix, int ago) => SeriesIndex(nowUnix, ago) is var i and >= 0 ? _sValid[i] : 0;
    public int SeriesBlocked(double nowUnix, int ago) => SeriesIndex(nowUnix, ago) is var i and >= 0 ? _sBlocked[i] : 0;
    public int SeriesConversions(double nowUnix, int ago) => SeriesIndex(nowUnix, ago) is var i and >= 0 ? _sConv[i] : 0;
    public long SeriesCommission(double nowUnix, int ago) => SeriesIndex(nowUnix, ago) is var i and >= 0 ? _sCommission[i] : 0;

    public void Reset()
    {
        Array.Clear(_links); Array.Clear(_linkProduct); Array.Clear(_valid); Array.Clear(_conv); Array.Clear(_commission);
        Array.Clear(_ip); Array.Clear(_winStart); Array.Clear(_winCount); Array.Clear(_banUntil);
        Array.Clear(_sMinute); Array.Clear(_sValid); Array.Clear(_sBlocked); Array.Clear(_sConv); Array.Clear(_sCommission);
        _ipHead = _ipCount = 0;
        ValidClicks = BlockedBot = BlockedBurst = BlockedBanned = UnknownLinks = Conversions = CommissionKrw = LinkCapRejected = 0;
    }

    private int SeriesIndex(double nowUnix, int ago)
    {
        if ((uint)ago >= SeriesCap) return -1;
        var m = (long)Math.Floor(nowUnix / 60) - ago;
        var i = (int)(((m % SeriesCap) + SeriesCap) % SeriesCap);
        return _sMinute[i] == m ? i : -1;
    }

    /// <summary>분 버킷 확보 — 링 재사용 시 이전 분 값 리셋.</summary>
    private int Bucket(double nowUnix)
    {
        var m = (long)Math.Floor(nowUnix / 60);
        var i = (int)(((m % SeriesCap) + SeriesCap) % SeriesCap);
        if (_sMinute[i] != m)
        {
            _sMinute[i] = m;
            _sValid[i] = _sBlocked[i] = _sConv[i] = 0;
            _sCommission[i] = 0;
        }
        return i;
    }

    /// <summary>IP 해시 슬롯 탐색/할당 — 링 만석 시 가장 오래된 슬롯 교체.</summary>
    private int IpSlot(ulong h)
    {
        for (var i = 0; i < _ipCount; i++) if (_ip[i] == h) return i;
        var k = _ipHead;
        _ip[k] = h;
        _winStart[k] = double.MinValue;
        _winCount[k] = 0;
        _banUntil[k] = 0;
        _ipHead = (_ipHead + 1) % IpCap;
        if (_ipCount < IpCap) _ipCount++;
        return k;
    }

    private static ulong Hash(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }
}
