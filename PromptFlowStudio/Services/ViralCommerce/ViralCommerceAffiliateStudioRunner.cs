using System.Diagnostics;
using System.Globalization;

namespace PromptFlowStudio.Services.ViralCommerce;

/// <summary>
/// [PromptFlow Studio v17.97.0] 🛒 바이럴 커머스 &amp; 제휴 어트리뷰션 러너(HUD <c>VcaRun</c>).
///   ▸ 체인: 대본 → <see cref="VideoContextAffiliateProductMatcherEngine"/> 상위 3 상품 → <see cref="CompliantShortlinkLegalNoticeInjector"/> 공정위/FTC 고지 + 숏링크 패키지 →
///     게시 시 <see cref="RealtimeAffiliateAttributionSafetyGuard"/> 영상당 3 링크 등록 + v17.95 골든아워 고정 댓글 템플릿 주입 → 10 s 틱 모의 클릭/전환 스트림 어트리뷰션.
///   ▸ 시뮬레이션 가상 시계 · 결정적 xorshift 트래픽.
/// </summary>
public sealed class ViralCommerceAffiliateStudioRunner : IDisposable
{
    public const int StepSec = 10, LogSize = 32;
    public const long SimEpoch = 1_800_000_000;
    public const string DefaultSubId = "pfs_ch01", DefaultCampaign = "v1797_shorts";

    private static readonly string[] HumanAgents =
    {
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 YouTube/19.40",
        "Mozilla/5.0 (Linux; Android 15; SM-S931N) AppleWebKit/537.36 Chrome/130.0 Mobile Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36",
    };
    public const string CrawlerAgent = "Mozilla/5.0 (compatible; AffiliateScraperBot/2.1; +http://crawler.example)";

    /// <summary>대본 프리셋 — 이름 · 대본 · 고정 댓글 본문(v17.95) · 설명란 본문(v17.94).</summary>
    public static readonly string[] PresetNames = { "📱 아이폰16 야간 촬영 꿀팁", "🎃 10분 할로윈 좀비 분장" };
    private static readonly string[] PresetScripts =
    {
        "아이폰16으로 야간 촬영할 때 흔들림 없이 찍는 법! 짐벌로 안정화하고 링라이트 조명 하나만 더하면 밤 영상 퀄리티가 확 달라집니다. 맥세이프 케이스까지 끼우면 준비 끝.",
        "10분 완성 할로윈 좀비 분장! 특수분장 키트 하나로 상처를 표현하고 글리터 섀도우로 포인트 메이크업까지. 삼각대에 폰 세우고 따라 해 보세요.",
    };
    private static readonly string[] PresetPinned = { "여러분은 야간 촬영 때 어떤 장비부터 챙기시나요? 👇 댓글로 투표해 주세요!", "다음 분장 콘셉트 투표! 좀비 vs 뱀파이어 👇 댓글로 알려 주세요!" };
    private static readonly string[] PresetDesc = { "아이폰16 야간 촬영 설정 3가지와 흔들림 없는 장비 세팅을 소개합니다.", "다이소 · 특수분장 키트로 10분 만에 끝내는 좀비 할로윈 메이크업 튜토리얼." };

    private readonly IServiceProvider? _sp;
    private readonly string[] _log = new string[LogSize];
    private readonly char[] _ipBuf = new char[24];
    private int _logHead, _logCount, _video;
    private uint _rng = 0x9E3779B9u;
    private string _script = PresetScripts[0], _subId = DefaultSubId, _campaign = DefaultCampaign;

    public ViralCommerceAffiliateStudioRunner(IServiceProvider sp) : this() => _sp = sp;

    public ViralCommerceAffiliateStudioRunner() => ApplyPreset(0);

    public event Action? OnChange;
    public VideoContextAffiliateProductMatcherEngine Matcher { get; } = new();
    public CompliantShortlinkLegalNoticeInjector Injector { get; } = new();
    public RealtimeAffiliateAttributionSafetyGuard Guard { get; } = new();
    public double SimNow { get; private set; } = SimEpoch;
    public int Preset { get; private set; }
    public int Video => _video;
    public bool Published { get; private set; }
    public long Publications { get; private set; }
    public long Copies { get; private set; }
    public long ComplianceViolations { get; private set; }
    public long PinnedSynced { get; private set; }
    public double LastMatchMicros { get; private set; }
    public double MaxMatchMicros { get; private set; }
    public int LogCount => _logCount;
    public string? LastAction { get; private set; }
    public string Script => _script;
    public string SubId => _subId;
    public string Campaign => _campaign;
    public long ElapsedSec => (long)(SimNow - SimEpoch);

    /// <summary>현재 패키지 공정위/FTC 최상단 고지 무결성(고정 댓글 · 설명란 동시).</summary>
    public bool NoticeOk
    {
        get
        {
            var c = Injector.UsesNetwork(AffiliateNetwork.CoupangPartners);
            var a = Injector.UsesNetwork(AffiliateNetwork.AmazonAssociates);
            return CompliantShortlinkLegalNoticeInjector.HasTopNotice(Injector.PinnedComment, c, a) && CompliantShortlinkLegalNoticeInjector.HasTopNotice(Injector.Description, c, a);
        }
    }

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    /// <summary>게시 — 현재 영상 링크 재등록(상한 3) · 고정 댓글 템플릿 v17.95 동기화. 반환 = 등록 링크 수.</summary>
    public int Publish()
    {
        Recompose();
        if (Injector.LinkCount == 0)
        {
            LastAction = "⚠️ 매칭 상품 없음 — 연관도 ≥ " + Id(VideoContextAffiliateProductMatcherEngine.MinRelevance) + " 상품이 나오도록 대본을 보강하세요";
            Push(LastAction);
            Raise();
            return 0;
        }
        if (!NoticeOk)
        {
            ComplianceViolations++;
            LastAction = "🚫 공정위 고지 누락 — 게시 차단";
            Push(LastAction);
            Raise();
            return 0;
        }
        Guard.ClearLinks(_video);
        var n = 0;
        for (var i = 0; i < Injector.LinkCount; i++)
            if (Guard.TryRegisterLink(_video, Injector.LinkProduct(i)) >= 0) n++;
        Published = true;
        Publications++;
        SyncPinned();
        Push("🚀 영상 #" + Id(_video + 1) + " 게시 · 링크 " + Id(n) + " · " + CompliantShortlinkLegalNoticeInjector.Domain + Injector.ShortId(0).ToString());
        LastAction = "🚀 게시 완료 · 제휴 링크 " + Id(n) + "/" + Id(RealtimeAffiliateAttributionSafetyGuard.MaxLinksPerVideo) + " · 고정 댓글 · 설명란 최상단 고지 주입";
        Raise();
        return n;
    }

    /// <summary>4 번째 링크 추가 시도 — 영상당 3 상한 차단 확인.</summary>
    public bool ForceExtraLink()
    {
        if (!Published) Publish();
        var extra = -1;
        for (var p = 0; p < VideoContextAffiliateProductMatcherEngine.CatalogSize && extra < 0; p++)
        {
            var used = false;
            for (var i = 0; i < Guard.LinkCount(_video); i++) if (Guard.LinkProduct(_video, i) == p) used = true;
            if (!used) extra = p;
        }
        var ok = Guard.TryRegisterLink(_video, extra) >= 0;
        LastAction = ok ? "⚠️ 링크 추가 " + Id(Guard.LinkCount(_video)) + "개" : "🛑 링크 도배 차단 — 영상당 최대 " + Id(RealtimeAffiliateAttributionSafetyGuard.MaxLinksPerVideo) + "개 상한 · 거부 " + Id(Guard.LinkCapRejected) + "회";
        Push(LastAction);
        Raise();
        return ok;
    }

    /// <summary>모의 트래픽 전진(10 s 틱) — 사람 클릭 · 전환 귀속. 반환 = 이번 구간 유효 클릭.</summary>
    public long Advance(int seconds)
    {
        if (!Published) Publish();
        var steps = Math.Max(1, seconds / StepSec);
        var v0 = Guard.ValidClicks;
        var c0 = Guard.Conversions;
        for (var k = 0; k < steps; k++) Step();
        var dv = Guard.ValidClicks - v0;
        var dc = Guard.Conversions - c0;
        if (dc > 0) Push("💰 전환 +" + Id(dc) + " · 누적 수수료 ₩" + Guard.CommissionKrw.ToString("N0", CultureInfo.InvariantCulture));
        LastAction = "⏩ +" + Id(steps * StepSec) + "s · 유효 클릭 +" + Id(dv) + " · 전환 +" + Id(dc) + " · CVR " + Guard.ConversionRatePct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        Raise();
        return dv;
    }

    /// <summary>봇 공습 주입 — 동일 IP 0.2 s 간격 6 연타 + 크롤러 UA 3 회. 반환 = 차단 수.</summary>
    public long InjectBotAttack()
    {
        if (!Published) Publish();
        var b0 = Guard.BlockedTotal;
        var ip = "198.51.100.66".AsSpan();
        for (var i = 0; i < 6; i++) Guard.OnClick(_video, 0, ip, HumanAgents[0], SimNow + i * 0.2);
        for (var i = 0; i < 3; i++) Guard.OnClick(_video, i % Math.Max(1, Guard.LinkCount(_video)), "203.0.113.9".AsSpan(), CrawlerAgent, SimNow + 2 + i);
        SimNow += StepSec;
        var blocked = Guard.BlockedTotal - b0;
        LastAction = "🤖 봇 공습 차단 " + Id(blocked) + "건 · 다중 클릭 " + Id(Guard.BlockedBurst) + " · 봇 UA " + Id(Guard.BlockedBot) + " · 차단 IP " + Id(Guard.BannedIps(SimNow));
        Push(LastAction);
        Raise();
        return blocked;
    }

    /// <summary>1 클릭 복사 텍스트(고정 댓글 · 설명란 패키지) — 복사 횟수 기록.</summary>
    public string CopyPackage(bool description)
    {
        var text = description ? Injector.Description : Injector.PinnedComment;
        Copies++;
        LastAction = "📋 " + (description ? "설명란" : "고정 댓글") + " 패키지 복사 · " + Id(text.Length) + "자";
        Raise();
        return text.ToString();
    }

    /// <summary>다음 영상 슬롯으로 전환(링크 상한은 영상별 독립).</summary>
    public void NextVideo()
    {
        _video = (_video + 1) % RealtimeAffiliateAttributionSafetyGuard.VideoCap;
        Guard.ClearLinks(_video);
        Published = false;
        LastAction = "🎬 영상 #" + Id(_video + 1) + " 준비 — 게시 전";
        Raise();
    }

    public void SetPreset(int preset)
    {
        ApplyPreset(Math.Clamp(preset, 0, PresetNames.Length - 1));
        LastAction = "📝 대본 프리셋: " + PresetNames[Preset] + " · 추천 " + Id(Matcher.TopCount);
        Raise();
    }

    public void SetScript(string? text)
    {
        _script = string.IsNullOrWhiteSpace(text) ? PresetScripts[Preset] : text.Length > 1000 ? text[..1000] : text;
        Recompose();
        LastAction = "✏️ 대본 " + Id(_script.Length) + "자 · 엔티티 " + Id(Matcher.EntityHits) + " · 추천 " + Id(Matcher.TopCount);
        Raise();
    }

    public void SetSubId(string? text)
    {
        _subId = string.IsNullOrWhiteSpace(text) ? DefaultSubId : text.Length > 40 ? text[..40] : text.Trim();
        Recompose();
        LastAction = "🏷 subId 갱신 · 숏링크 재발급";
        Raise();
    }

    public void SetCampaign(string? text)
    {
        _campaign = string.IsNullOrWhiteSpace(text) ? DefaultCampaign : text.Length > 40 ? text[..40] : text.Trim();
        Recompose();
        LastAction = "🏷 캠페인 태그 갱신 · 숏링크 재발급";
        Raise();
    }

    public void Reset()
    {
        Matcher.Reset(); Injector.Reset(); Guard.Reset();
        SimNow = SimEpoch;
        _video = 0;
        Published = false;
        Publications = Copies = ComplianceViolations = PinnedSynced = 0;
        LastMatchMicros = MaxMatchMicros = 0;
        _rng = 0x9E3779B9u;
        _subId = DefaultSubId;
        _campaign = DefaultCampaign;
        ApplyPreset(Preset);
        Array.Clear(_log);
        _logHead = _logCount = 0;
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 내부 ══════════════════════

    private void ApplyPreset(int p)
    {
        Preset = p;
        _script = PresetScripts[p];
        Recompose();
    }

    private void Recompose()
    {
        var t0 = Stopwatch.GetTimestamp();
        Matcher.SetScript(_script.AsSpan());
        Matcher.Match();
        Injector.Compose(Matcher, _subId.AsSpan(), _campaign.AsSpan(), PresetPinned[Preset].AsSpan(), PresetDesc[Preset].AsSpan());
        LastMatchMicros = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
        if (LastMatchMicros > MaxMatchMicros) MaxMatchMicros = LastMatchMicros;
    }

    private void Step()
    {
        SimNow += StepSec;
        var links = Guard.LinkCount(_video);
        if (links == 0) return;
        var clicks = 2 + (int)(Next() % 5);
        for (var c = 0; c < clicks; c++)
        {
            var slot = (int)(Next() % (uint)links);
            var ip = FormatIp(Next());
            var at = SimNow + c * 0.7;
            if (Guard.OnClick(_video, slot, ip, HumanAgents[Next() % (uint)HumanAgents.Length], at) != ClickVerdict.Valid) continue;
            var p = Guard.LinkProduct(_video, slot);
            if (Next() % 1000 < (uint)VideoContextAffiliateProductMatcherEngine.ConvPermille[p] * 4)   // 숏폼 구매 의도 트래픽 가중(×4)
                Guard.OnConversion(_video, slot, VideoContextAffiliateProductMatcherEngine.CommissionPerSale(p), at);
        }
    }

    /// <summary>재사용 버퍼에 "10.x.y.z" 사설 IP 기록.</summary>
    private ReadOnlySpan<char> FormatIp(uint r)
    {
        var b = _ipBuf.AsSpan();
        var n = 0;
        b[n++] = '1'; b[n++] = '0';
        for (var k = 0; k < 3; k++)
        {
            b[n++] = '.';
            ((r >> (k * 8)) & 0xFF).TryFormat(b[n..], out var w, default, CultureInfo.InvariantCulture);
            n += w;
        }
        return b[..n];
    }

    private void SyncPinned()
    {
        try
        {
            if (_sp?.GetService(typeof(GoldenHourEngagement.GoldenHourEngagementStudioRunner)) is GoldenHourEngagement.GoldenHourEngagementStudioRunner ghe)
            {
                ghe.SetTemplate(Injector.PinnedComment.ToString());
                PinnedSynced++;
            }
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("ViralCommerce.SyncPinned", ex); }
    }

    private uint Next()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }

    private void Push(string line)
    {
        lock (_log)
        {
            _log[_logHead] = "[+" + Id(ElapsedSec) + "s] " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
    }

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);

    public string StatusLine() =>
        "추천 " + Id(Matcher.TopCount) + " · 링크 " + Id(Guard.LinkCount(_video)) + "/3 · 유효 클릭 " + Id(Guard.ValidClicks) + " · 전환 " + Id(Guard.Conversions) +
        " · 차단 " + Id(Guard.BlockedTotal) + " · 수수료 ₩" + Guard.CommissionKrw.ToString("N0", CultureInfo.InvariantCulture) + " · 최대 매칭 " + MaxMatchMicros.ToString("0.0", CultureInfo.InvariantCulture) + "µs";

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("ViralCommerce.OnChange", ex); }
    }

    public void Dispose() => OnChange = null;
}
