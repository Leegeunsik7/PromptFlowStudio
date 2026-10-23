using PromptFlowStudio.Services.MatrixSyndication;

namespace PromptFlowStudio.Services.PostPublishSplitTest;

/// <summary>승자 마스터 에셋 — MP4 · 자막 · 메타데이터를 참조로만 들고 다님(복사 0).</summary>
public readonly struct WinnerMasterAsset
{
    public WinnerMasterAsset(int contentId, SplitVariant variant, ReadOnlyMemory<byte> mp4, ReadOnlyMemory<char> subtitle, ReadOnlyMemory<char> metadata)
    {
        ContentId = contentId; Variant = variant; Mp4 = mp4; Subtitle = subtitle; Metadata = metadata;
    }

    public int ContentId { get; }
    public SplitVariant Variant { get; }
    public ReadOnlyMemory<byte> Mp4 { get; }
    public ReadOnlyMemory<char> Subtitle { get; }
    public ReadOnlyMemory<char> Metadata { get; }
}

public enum PromoteResult : byte { Promoted, Duplicate, InvalidAsset, DailyCap, NoChannel }

/// <summary>승격 기록 1 건(링 슬롯 재사용).</summary>
public struct PromotionRecord
{
    public WinnerMasterAsset Asset;
    public long TUnix, ReadyUnix;
    public int JitterSec, Routed;
}

/// <summary>
/// [PromptFlow Studio v17.93.0] 🚀 승자 클립 서브 채널 증폭 프로모터 가드.
///   ▸ 확정 승자의 마스터 에셋(MP4 · 자막 · 메타)을 v17.81 <see cref="CompliantMultiAccountMatrixRouterEngine"/> 배포 큐로 승격 —
///     라우터가 채널별 쿼터 · 골든타임 정렬 · 45 분+ 교차 채널 시차를 이미 강제하므로 여기서는 콘텐츠 단위 지터(0~<see cref="MaxJitterMin"/> 분)만 더함.
///   ▸ 같은 콘텐츠 재승격 차단(<c>alreadyRegistered</c>) · 24 h 승격 상한 · 빈 에셋 거절.
///   ▸ 에셋은 <see cref="ReadOnlyMemory{T}"/> 참조 전달 + 고정 링(32) 기록 → 승격 경로 무할당.
/// </summary>
public sealed class ViralWinnerSubChannelPromoterGuard
{
    public const int HistoryCap = 32, DefaultDailyCap = 6, MaxJitterMin = 30;
    private const long DaySec = 86_400;

    private readonly PromotionRecord[] _hist = new PromotionRecord[HistoryCap];
    private int _head, _count;
    private uint _rng = 0x9E3779B9u;

    public ViralWinnerSubChannelPromoterGuard(CompliantMultiAccountMatrixRouterEngine router) => Router = router ?? throw new ArgumentNullException(nameof(router));

    public CompliantMultiAccountMatrixRouterEngine Router { get; }
    public int DailyCap { get; set; } = DefaultDailyCap;
    public long Promotions { get; private set; }
    public long Duplicates { get; private set; }
    public long CapBlocks { get; private set; }
    public long RoutedPosts { get; private set; }
    public int HistoryCount => _count;
    public PromoteResult LastResult { get; private set; }

    /// <summary>승격 기록(0 = 최신).</summary>
    public ref readonly PromotionRecord History(int i) => ref _hist[(_head - 1 - i + HistoryCap * 2) % HistoryCap];

    public static string ResultName(PromoteResult r) => r switch
    {
        PromoteResult.Promoted => "승격",
        PromoteResult.Duplicate => "중복 차단",
        PromoteResult.InvalidAsset => "에셋 누락",
        PromoteResult.DailyCap => "24h 상한",
        _ => "채널 없음",
    };

    public bool AlreadyPromoted(int contentId)
    {
        for (var i = 0; i < _count; i++) if (_hist[i].Asset.ContentId == contentId) return true;
        return false;
    }

    private int PromotionsSince(long since)
    {
        var n = 0;
        for (var i = 0; i < _count; i++) if (_hist[i].TUnix >= since) n++;
        return n;
    }

    /// <summary>xorshift32 결정적 지터(초) — 0 ~ MaxJitterMin 분.</summary>
    private int NextJitterSec()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return (int)(x % (uint)(MaxJitterMin * 60 + 1));
    }

    /// <summary>승자 에셋 → 매트릭스 큐 승격(무할당). 반환 = 결과 코드 · <paramref name="routed"/> = 예약된 서브 채널 포스트 수.</summary>
    public PromoteResult Promote(in WinnerMasterAsset asset, long nowUnix, out int routed)
    {
        routed = 0;
        PromoteResult r;
        if (asset.ContentId <= 0 || asset.Mp4.IsEmpty || asset.Metadata.IsEmpty) r = PromoteResult.InvalidAsset;
        else if (AlreadyPromoted(asset.ContentId)) { Duplicates++; r = PromoteResult.Duplicate; }
        else if (Router.ChannelCount == 0) r = PromoteResult.NoChannel;
        else if (PromotionsSince(nowUnix - DaySec) >= DailyCap) { CapBlocks++; r = PromoteResult.DailyCap; }
        else
        {
            var jitter = NextJitterSec();
            var ready = nowUnix + jitter;
            routed = Router.Route(asset.ContentId, ready, nowUnix);
            ref var h = ref _hist[_head];
            h.Asset = asset; h.TUnix = nowUnix; h.ReadyUnix = ready; h.JitterSec = jitter; h.Routed = routed;
            _head = (_head + 1) % HistoryCap;
            if (_count < HistoryCap) _count++;
            Promotions++;
            RoutedPosts += routed;
            r = PromoteResult.Promoted;
        }
        LastResult = r;
        return r;
    }

    public void ClearHistory() { Array.Clear(_hist); _head = _count = 0; }
}
