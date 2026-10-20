using PromptFlowStudio.Services.GraphBridge;
namespace PromptFlowStudio.Services.OmniChannel;

public enum OmniPlatform : byte { Instagram, YouTube, TikTok }

/// <summary>
/// [PromptFlow Studio v17.51.0] 🌐 플랫폼 쿼터 버짓 &amp; 답글 품질/페이싱 세이프티 가드(<see cref="TrySpendYouTube"/> · <see cref="TryTikTokInit"/> · <see cref="Similarity"/> · <see cref="CheckReply"/> 무할당).
/// <b>YouTube</b>: Data API v3 일일 10,000 유닛(태평양 자정 리셋) — videos.insert 1,600 · commentThreads/comments.insert 50 · commentThreads.list 1.
/// 업로드 1 건 유닛을 예약해 두고 잔여가 부족하면 지연(다음 리셋 시각까지). <b>TikTok</b>: Content Posting API 초기화 분당 6 회(사용자 토큰) · 일일 발행 상한.
/// <b>답글</b>: 최근 32 건과 정규화 레벤슈타인 유사도 ≤ 0.40 일 때만 허용(반복 문구 방지) · 답글 간 최소 간격 45~180 초(균일 난수 페이싱) ·
/// 영상당 시간당 12 건 상한 · 스팸/공격 댓글에는 답하지 않음. 이는 플랫폼 스팸 정책 준수를 위한 속도/품질 제한이며 자동화 사실을 숨기거나
/// 봇 탐지를 우회하는 기능은 두지 않는다(자동 전송 기본 OFF · AI 답글 서명 옵션).
/// 토큰은 v17.48 <see cref="GraphSecretSlot"/>(AES-GCM) 로 플랫폼별 보관.
/// </summary>
public sealed class OmniPlatformQuotaSpamSafetyGuard : IDisposable
{
    public const int YtDailyUnits = 10_000, YtInsertCost = 1600, YtCommentCost = 50, YtListCost = 1;
    public const int TikTokInitPerMinute = 6, ReplyHistory = 32, ReplyCap = 280, RepliesPerHour = 12;
    public const double MaxSimilarity = 0.40, PaceMinSec = 45, PaceMaxSec = 180;
    private readonly char[] _hist = new char[ReplyHistory * ReplyCap];
    private readonly int[] _histLen = new int[ReplyHistory];
    private readonly double[] _initAt = new double[TikTokInitPerMinute], _replyAt = new double[RepliesPerHour];
    private int _histHead, _histN, _initHead, _replyHead;
    private ulong _rng = 0xD1B54A32D192ED03UL;
    private long _ytDay = -1;

    public GraphSecretSlot YouTubeToken { get; } = new();
    public GraphSecretSlot TikTokToken { get; } = new();
    public int YtUnitsUsed { get; private set; }
    public int YtUnitsReserved { get; private set; }
    public int YtRemaining => YtDailyUnits - YtUnitsUsed - YtUnitsReserved;
    public double YtQuotaPct => 100.0 * Math.Max(YtRemaining, 0) / YtDailyUnits;
    public int TikTokDailyCap { get; set; } = 15;
    public int TikTokPostsToday { get; private set; }
    public double NextReplyAt { get; private set; }
    public double LastPaceSec { get; private set; }
    public double LastSimilarity { get; private set; }
    public long YtDeferred { get; private set; }
    public long TikTokDeferred { get; private set; }
    public long RepliesBlockedSimilar { get; private set; }
    public long RepliesBlockedRate { get; private set; }
    public long RepliesAllowed { get; private set; }

    /// <summary>태평양 시간 일자(PDT 가정 UTC−7 · 리셋 경계 계산용).</summary>
    public static long PacificDay(double unixSec) => (long)Math.Floor((unixSec - 7 * 3600) / 86400);
    public static double NextPacificMidnight(double unixSec) => (PacificDay(unixSec) + 1) * 86400.0 + 7 * 3600;

    private void Roll(double now)
    {
        var d = PacificDay(now);
        if (d != _ytDay) { _ytDay = d; YtUnitsUsed = 0; TikTokPostsToday = 0; }
    }

    // ══════════════════════ YouTube 유닛 ══════════════════════
    /// <summary>업로드 시작 시 1,600 + 고정 댓글 50 유닛 예약 — 부족하면 false(다음 리셋까지 지연).</summary>
    public bool TryReserveYouTubeUpload(double now)
    {
        Roll(now);
        if (YtRemaining < YtInsertCost + YtCommentCost) { YtDeferred++; return false; }
        YtUnitsReserved += YtInsertCost + YtCommentCost;
        return true;
    }

    public void CommitYouTubeUpload(bool ok)
    {
        YtUnitsReserved = Math.Max(0, YtUnitsReserved - YtInsertCost - YtCommentCost);
        if (ok) YtUnitsUsed += YtInsertCost; // 실패 업로드는 세션 생성 전 단계로 간주(유닛 미차감 · 보수적 추정은 UI 잔여로 노출)
    }

    /// <summary>개별 호출 유닛 차감(무할당) — 잔여 부족 시 false.</summary>
    public bool TrySpendYouTube(int units, double now)
    {
        Roll(now);
        if (YtDailyUnits - YtUnitsUsed < units) { YtDeferred++; return false; }
        YtUnitsUsed += units;
        return true;
    }

    // ══════════════════════ TikTok ══════════════════════
    public bool TryTikTokInit(double now)
    {
        Roll(now);
        var n = 0;
        for (var i = 0; i < TikTokInitPerMinute; i++) if (_initAt[i] > 0 && now - _initAt[i] < 60) n++;
        if (n >= TikTokInitPerMinute || TikTokPostsToday >= TikTokDailyCap) { TikTokDeferred++; return false; }
        _initAt[_initHead] = now; _initHead = (_initHead + 1) % TikTokInitPerMinute;
        TikTokPostsToday++;
        return true;
    }

    public double TikTokWaitSec(double now)
    {
        var oldest = double.MaxValue; var n = 0;
        for (var i = 0; i < TikTokInitPerMinute; i++) if (_initAt[i] > 0 && now - _initAt[i] < 60) { n++; oldest = Math.Min(oldest, _initAt[i]); }
        return n < TikTokInitPerMinute ? 0 : oldest + 60 - now;
    }

    // ══════════════════════ 답글 다양성 · 페이싱 ══════════════════════
    /// <summary>정규화 레벤슈타인 유사도 1 − d / max(|a|,|b|)(대소문자 무시 · 2 행 stackalloc · 무할당).</summary>
    public static double Similarity(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length > ReplyCap) a = a[..ReplyCap];
        if (b.Length > ReplyCap) b = b[..ReplyCap];
        var m = Math.Max(a.Length, b.Length);
        if (m == 0) return 1;
        Span<int> prev = stackalloc int[ReplyCap + 1];
        Span<int> cur = stackalloc int[ReplyCap + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var ca = char.ToLowerInvariant(a[i - 1]);
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            cur[..(b.Length + 1)].CopyTo(prev);
        }
        return 1.0 - (double)prev[b.Length] / m;
    }

    /// <summary>후보 답글의 최근 이력 대비 최대 유사도.</summary>
    public double MaxHistorySimilarity(ReadOnlySpan<char> reply)
    {
        var best = 0.0;
        for (var i = 0; i < _histN; i++) best = Math.Max(best, Similarity(reply, _hist.AsSpan(i * ReplyCap, _histLen[i])));
        return best;
    }

    /// <summary>답글 허용 판정(무할당) — 0 허용 · 1 유사 · 2 페이싱/시간당 상한.</summary>
    public int CheckReply(ReadOnlySpan<char> reply, double now)
    {
        LastSimilarity = MaxHistorySimilarity(reply);
        if (LastSimilarity > MaxSimilarity) { RepliesBlockedSimilar++; return 1; }
        var hour = 0;
        for (var i = 0; i < RepliesPerHour; i++) if (_replyAt[i] > 0 && now - _replyAt[i] < 3600) hour++;
        if (now < NextReplyAt || hour >= RepliesPerHour) { RepliesBlockedRate++; return 2; }
        return 0;
    }

    /// <summary>전송 확정 — 이력 적재 · 다음 허용 시각(45~180 초 균일 페이싱).</summary>
    public void CommitReply(ReadOnlySpan<char> reply, double now)
    {
        var n = Math.Min(reply.Length, ReplyCap);
        reply[..n].CopyTo(_hist.AsSpan(_histHead * ReplyCap));
        _histLen[_histHead] = n;
        _histHead = (_histHead + 1) % ReplyHistory;
        if (_histN < ReplyHistory) _histN++;
        _replyAt[_replyHead] = now; _replyHead = (_replyHead + 1) % RepliesPerHour;
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        LastPaceSec = PaceMinSec + (PaceMaxSec - PaceMinSec) * ((_rng >> 11) * (1.0 / 9007199254740992.0));
        NextReplyAt = now + LastPaceSec;
        RepliesAllowed++;
    }

    public void Dispose() { YouTubeToken.Dispose(); TikTokToken.Dispose(); }
}
