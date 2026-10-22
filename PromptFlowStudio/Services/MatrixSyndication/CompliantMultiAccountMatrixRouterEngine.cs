using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.SmartPublishing;

namespace PromptFlowStudio.Services.MatrixSyndication;

/// <summary>v17.81.0 소유 채널 계층 — 메인 브랜드 · 하이라이트 · 숏클립 전용.</summary>
public enum ChannelTier : byte { Primary, Sub, Clip }

/// <summary>v17.81.0 변형본 화면비 — 9:16 세로 릴스/쇼츠 · 1:1 인스타그램 피드(스마트 블러 패딩).</summary>
public enum VariantAspect : byte { Vertical916, Square11 }

/// <summary>v17.81.0 채널 로고 워터마크 형태.</summary>
public enum BrandLogo : byte { Ring, Hexagon, Diamond }

public enum MatrixPostState : byte { Scheduled, Held, Published, Failed, Cancelled }

/// <summary>v17.81.0 채널 맞춤 편집 변형 명세(사람이 육안으로 구별 가능한 편집 축만 — 화면비 · 범퍼 · 로고 · 강조색 · 자막 언어).</summary>
public struct ChannelVariantSpec
{
    public VariantAspect Aspect;
    public BrandLogo Logo;
    public DubLanguage SubtitleLang;
    public float IntroSec, OutroSec;
    public uint Accent;

    /// <summary>두 변형 사이 육안 구별 축 수(0~5).</summary>
    public static int Differences(in ChannelVariantSpec a, in ChannelVariantSpec b)
    {
        var d = 0;
        if (a.Aspect != b.Aspect) d++;
        if (a.Logo != b.Logo) d++;
        if (a.SubtitleLang != b.SubtitleLang) d++;
        if (MathF.Abs(a.IntroSec - b.IntroSec) >= 0.2f || MathF.Abs(a.OutroSec - b.OutroSec) >= 0.2f) d++;
        if (a.Accent != b.Accent) d++;
        return d;
    }
}

/// <summary>v17.81.0 등록 채널(정규 OAuth2 연결 계정 · 채널별 독립 쿼터 가드).</summary>
public sealed class MatrixChannel
{
    internal MatrixChannel(int index, string name, string handle, ChannelTier tier, PublishPlatforms platform, in ChannelVariantSpec variant)
    {
        Index = index; Name = name; Handle = handle; Tier = tier; Platform = platform; Variant = variant;
    }

    public int Index { get; }
    public string Name { get; }
    public string Handle { get; }
    public ChannelTier Tier { get; }
    public PublishPlatforms Platform { get; }
    public ChannelVariantSpec Variant;
    public MultiChannelQuotaDistributionGuard Guard { get; } = new();
    public bool Paused { get; internal set; }
    public long Published { get; internal set; }
    public long Scheduled { get; internal set; }
}

/// <summary>v17.81.0 채널 큐 항목(콘텐츠 × 채널 1:1 변형본).</summary>
public struct MatrixPost
{
    public int ContentId, Channel;
    public long ReadyUnix, SlotUnix, PublishedUnix;
    public GoldenRegion Region;
    public MatrixPostState State;
}

/// <summary>
/// [PromptFlow Studio v17.81.0] 🌐 준법형 다계정 매트릭스 분산 라우터.
///   ▸ 정규 OAuth2 로 연결된 소유 채널만 등록(미검증 거부) — Primary(메인 브랜드) · Sub(하이라이트) · Clip(숏클립) 계층.
///   ▸ 채널마다 고유 편집 변형본(<see cref="ChannelVariantSpec"/>)을 1:1 로 매핑 — 기존 채널과 육안 구별 축이 <see cref="MinVariantDiff"/> 미만이면 등록 거부(무분별 중복 게시 차단).
///     동일 콘텐츠 × 동일 채널 재라우팅은 중복으로 거부.
///   ▸ 시차 분산: 같은 콘텐츠의 채널 간 슬롯을 45~120 분(<see cref="StaggerMinutes"/>) 이상 벌리고 v17.76 골든타임 창(<see cref="GlobalGoldenHourPublishingScheduler.NextGolden"/>) ·
///     채널별 토큰 버킷 쿼터 가드(<see cref="MultiChannelQuotaDistributionGuard.EarliestFit"/>)를 모두 만족하는 최초 시각에 예약.
///   ▸ 일시정지된 채널의 항목은 Held 로 동결 — 다른 계정으로 옮기는 경로 자체가 없다(우회 재라우팅 0).
///   ▸ 큐 · 예약 계산 = 고정 배열(무할당).
/// </summary>
public sealed class CompliantMultiAccountMatrixRouterEngine
{
    public const int MaxChannels = 12, Cap = 256, MinStaggerMin = 45, MaxStaggerMin = 120, MinVariantDiff = 2;
    public const long HorizonSec = GlobalGoldenHourPublishingScheduler.HorizonSec;

    private readonly MatrixChannel?[] _ch = new MatrixChannel?[MaxChannels];
    private readonly MatrixPost[] _q = new MatrixPost[Cap];
    private readonly int[] _order = new int[MaxChannels];
    private int _chN, _n, _stagger = 60;

    public int ChannelCount => _chN;
    public MatrixChannel Channel(int i) => _ch[i]!;
    public int Count => _n;
    public ref readonly MatrixPost At(int i) => ref _q[i];
    public GoldenRegions Regions { get; set; } = GoldenRegions.All;
    public bool AlignGolden { get; set; } = true;
    /// <summary>같은 콘텐츠의 채널 간 최소 시차(분 · 45~120 클램프).</summary>
    public int StaggerMinutes { get => _stagger; set => _stagger = Math.Clamp(value, MinStaggerMin, MaxStaggerMin); }
    public long Routed { get; private set; }
    public long DuplicateRejects { get; private set; }
    public long UnverifiedRejects { get; private set; }
    public long VariantCollisions { get; private set; }
    public long Unschedulable { get; private set; }
    public long Overflow { get; private set; }
    public long HeldByPause { get; private set; }
    public long Published { get; private set; }
    public long QuotaDeferrals { get; private set; }

    public static string TierName(ChannelTier t) => t switch { ChannelTier.Primary => "메인 브랜드", ChannelTier.Sub => "하이라이트", _ => "숏클립" };
    public static string TierIcon(ChannelTier t) => t switch { ChannelTier.Primary => "👑", ChannelTier.Sub => "✨", _ => "✂️" };
    public static string AspectName(VariantAspect a) => a == VariantAspect.Vertical916 ? "9:16" : "1:1";
    public static string StateName(MatrixPostState s) => s switch
    {
        MatrixPostState.Scheduled => "예약", MatrixPostState.Held => "동결", MatrixPostState.Published => "발행", MatrixPostState.Failed => "실패", _ => "취소",
    };

    // ══════════════════════ 채널 등록(비핫패스) ══════════════════════
    /// <summary>정규 OAuth2 소유 채널 등록 — 반환 = 인덱스 · 미검증/용량 초과/변형 충돌 = −1.</summary>
    public int Register(string name, string handle, ChannelTier tier, PublishPlatforms platform, bool oauthVerified, in ChannelVariantSpec variant)
    {
        if (!oauthVerified || platform is not (PublishPlatforms.YouTube or PublishPlatforms.Instagram)) { UnverifiedRejects++; return -1; }
        if (_chN == MaxChannels) return -1;
        for (var i = 0; i < _chN; i++)
            if (ChannelVariantSpec.Differences(in _ch[i]!.Variant, in variant) < MinVariantDiff) { VariantCollisions++; return -1; }
        var c = new MatrixChannel(_chN, name, handle, tier, platform, in variant);
        _ch[_chN++] = c;
        return c.Index;
    }

    // ══════════════════════ 분산 예약(무할당) ══════════════════════
    /// <summary>콘텐츠 1 건 → 각 채널 변형본 1:1 예약(계층 순 · 시차 분산). 반환 = 새로 만든 항목 수.</summary>
    public int Route(int contentId, long readyUnix, long nowUnix)
    {
        var m = 0;
        for (var tier = 0; tier <= (int)ChannelTier.Clip; tier++)
            for (var i = 0; i < _chN; i++) if ((int)_ch[i]!.Tier == tier) _order[m++] = i;
        var made = 0;
        long prev = long.MinValue;
        for (var k = 0; k < m; k++)
        {
            var c = _ch[_order[k]]!;
            if (Find(contentId, c.Index) >= 0) { DuplicateRejects++; continue; }
            if (_n == Cap) { Overflow++; break; }
            var t = Math.Max(readyUnix, nowUnix);
            if (prev != long.MinValue) t = Math.Max(t, prev + _stagger * 60L);
            var slot = Fit(c, contentId, t, nowUnix + HorizonSec, out var region);
            if (slot == long.MaxValue) { Unschedulable++; continue; }
            c.Guard.Reserve(c.Platform, slot);
            ref var p = ref _q[_n++];
            p.ContentId = contentId; p.Channel = c.Index; p.ReadyUnix = readyUnix; p.SlotUnix = slot; p.PublishedUnix = 0; p.Region = region;
            p.State = c.Paused ? MatrixPostState.Held : MatrixPostState.Scheduled;
            if (c.Paused) HeldByPause++;
            c.Scheduled++;
            Routed++; made++;
            prev = slot;
        }
        return made;
    }

    private long Fit(MatrixChannel c, int contentId, long t, long horizon, out GoldenRegion region)
    {
        region = GoldenRegion.Asia;
        var gap = _stagger * 60L;
        for (var guard = 0; guard < 256 && t <= horizon; guard++)
        {
            if (AlignGolden)
            {
                var g = GlobalGoldenHourPublishingScheduler.NextGolden(t, Regions, out region, out _);
                if (g == long.MaxValue) return long.MaxValue;
                t = g;
            }
            var fit = c.Guard.EarliestFit(c.Platform, t, horizon);
            if (fit == long.MaxValue) return long.MaxValue;
            if (fit != t) { QuotaDeferrals++; t = fit; continue; }
            var moved = false;
            for (var i = 0; i < _n; i++)
            {
                ref readonly var q = ref _q[i];
                if (q.ContentId == contentId && q.State != MatrixPostState.Cancelled && Math.Abs(q.SlotUnix - t) < gap) { t = q.SlotUnix + gap; moved = true; }
            }
            if (!moved) return t;
        }
        return long.MaxValue;
    }

    public int Find(int contentId, int channel)
    {
        for (var i = 0; i < _n; i++) if (_q[i].ContentId == contentId && _q[i].Channel == channel && _q[i].State != MatrixPostState.Cancelled) return i;
        return -1;
    }

    /// <summary>같은 콘텐츠의 활성 슬롯 간 최소 간격(초 · 1 개 이하 = long.MaxValue).</summary>
    public long MinCrossChannelGap(int contentId)
    {
        var best = long.MaxValue;
        for (var i = 0; i < _n; i++)
        {
            if (_q[i].ContentId != contentId || _q[i].State == MatrixPostState.Cancelled) continue;
            for (var j = i + 1; j < _n; j++)
                if (_q[j].ContentId == contentId && _q[j].State != MatrixPostState.Cancelled) best = Math.Min(best, Math.Abs(_q[j].SlotUnix - _q[i].SlotUnix));
        }
        return best;
    }

    // ══════════════════════ 실행(무할당) ══════════════════════
    /// <summary>now 기준 발행 기한이 된 Scheduled 항목 1 개 — 동결 채널 건너뜀 · 채널 쿼터 실소비 통과 시에만 반환.</summary>
    public bool TakeDue(long now, out int index)
    {
        for (var i = 0; i < _n; i++)
        {
            ref var p = ref _q[i];
            if (p.State != MatrixPostState.Scheduled || p.SlotUnix > now) continue;
            var c = _ch[p.Channel]!;
            if (c.Paused) { p.State = MatrixPostState.Held; HeldByPause++; continue; }
            if (!c.Guard.TryConsume(c.Platform, now)) continue;
            index = i;
            return true;
        }
        index = -1;
        return false;
    }

    public void MarkPublished(int index, long now, bool ok)
    {
        ref var p = ref _q[index];
        p.State = ok ? MatrixPostState.Published : MatrixPostState.Failed;
        p.PublishedUnix = now;
        if (!ok) return;
        _ch[p.Channel]!.Published++;
        Published++;
    }

    // ══════════════════════ 동결 · 해제(건전성 가드 전용) ══════════════════════
    /// <summary>채널 큐 동결 — 해당 채널 Scheduled → Held(타 채널 항목은 불변).</summary>
    internal int Freeze(int channel)
    {
        var c = _ch[channel]!;
        c.Paused = true;
        var held = 0;
        for (var i = 0; i < _n; i++)
            if (_q[i].Channel == channel && _q[i].State == MatrixPostState.Scheduled) { _q[i].State = MatrixPostState.Held; held++; }
        HeldByPause += held;
        return held;
    }

    /// <summary>관리자 승인 — Held → Scheduled 복귀(같은 채널 안에서만).</summary>
    internal int Thaw(int channel)
    {
        _ch[channel]!.Paused = false;
        var n = 0;
        for (var i = 0; i < _n; i++)
            if (_q[i].Channel == channel && _q[i].State == MatrixPostState.Held) { _q[i].State = MatrixPostState.Scheduled; n++; }
        return n;
    }

    /// <summary>관리자 취소 — 해당 채널 Held/Scheduled 전부 Cancelled · 예약표 재구성 · 동결 해제.</summary>
    internal int CancelChannel(int channel)
    {
        var c = _ch[channel]!;
        var n = 0;
        c.Guard.ClearReservations();
        for (var i = 0; i < _n; i++)
        {
            if (_q[i].Channel != channel) continue;
            if (_q[i].State is MatrixPostState.Held or MatrixPostState.Scheduled) { _q[i].State = MatrixPostState.Cancelled; n++; }
            else if (_q[i].State == MatrixPostState.Published) c.Guard.Reserve(c.Platform, _q[i].SlotUnix);
        }
        c.Paused = false;
        return n;
    }

    public int CountState(int channel, MatrixPostState s)
    {
        var n = 0;
        for (var i = 0; i < _n; i++) if ((channel < 0 || _q[i].Channel == channel) && _q[i].State == s) n++;
        return n;
    }

    /// <summary>큐 초기화(채널 유지 · 예약표 비움).</summary>
    public void ClearQueue()
    {
        _n = 0;
        for (var i = 0; i < _chN; i++) { _ch[i]!.Guard.ClearReservations(); _ch[i]!.Scheduled = 0; }
    }
}
