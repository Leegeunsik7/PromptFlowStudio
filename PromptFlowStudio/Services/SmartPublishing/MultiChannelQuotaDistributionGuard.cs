using PromptFlowStudio.Services.Publisher;

namespace PromptFlowStudio.Services.SmartPublishing;

/// <summary>v17.76.0 발행 플랫폼 비트 마스크.</summary>
[Flags]
public enum PublishPlatforms : byte { None = 0, YouTube = 1, Instagram = 2, Both = 3 }

/// <summary>
/// [PromptFlow Studio v17.76.0] 🚦 다채널 쿼터 분산 &amp; 스로틀링 세이프티 가드(계획 · 실행 이중 밸브 · 무할당 고정 표).
///   ▸ YouTube Data API: 일일 10,000 유닛(태평양 표준시 자정 리셋 · UTC−8 고정) − 예비 1,500(v17.75 A/B 승격 videos.update/thumbnails.set · 분석 폴링) =
///     업로드 예산 8,500 · 1 건 = videos.insert 1,600 + thumbnails.set 50 → 하루 최대 5 건.
///   ▸ Instagram Graph: 콘텐츠 게시 24 h 롤링 50 건 상한 · 사용자 호출 200 회/h(토큰 버킷 연속 리필) · 1 건 ≈ 컨테이너 1 + rupload 4 + 상태 폴링 2 + 게시 1 = 8 호출.
///   ▸ 채널당 최소 발행 간격 45 분(양방향 · 알고리즘 노출 잠식 방지).
///   ▸ 계획(<see cref="EarliestFit"/> · <see cref="Reserve"/>): 예약표(플랫폼별 고정 256) 기준 간격/일일 예산/롤링 상한을 만족하는 최초 시각 — 스케줄러가 우선순위 순으로 호출(순차 지연).
///   ▸ 실행(<see cref="TryConsume"/>): 실제 소비 기록(당일 유닛 · 게시 이력 링 · 호출 버킷 · 마지막 발행) — 거부 시 <see cref="NextAllowed"/>.
/// </summary>
public sealed class MultiChannelQuotaDistributionGuard
{
    public const int Platforms = 2, ReserveCap = 256, HistoryCap = 64;
    public const int YtDailyUnits = 10_000, YtReserveUnits = 1_500, YtInsertUnits = 1_600, YtThumbUnits = 50;
    public const int IgPosts24h = 50, IgCallsPerHour = 200, IgCallsPerPost = 8;
    public const long Day = 86_400, PacificOffsetSec = -8 * 3600;

    private readonly long[][] _res = { new long[ReserveCap], new long[ReserveCap] };
    private readonly int[] _resN = new int[Platforms];
    private readonly long[] _igHist = new long[HistoryCap];
    private int _igHistHead, _igHistN;
    private long _ytDay = long.MinValue, _igTokensStamp;
    private int _ytUsedToday;
    private double _igTokens = IgCallsPerHour;
    private readonly long[] _lastPublish = { long.MinValue / 2, long.MinValue / 2 };

    public long MinGapSec { get; set; } = 45 * 60;
    public int YtUploadCost => YtInsertUnits + YtThumbUnits;
    public int YtUploadBudget => YtDailyUnits - YtReserveUnits;
    public int YtUploadsPerDay => YtUploadBudget / YtUploadCost;
    public int YtUsedToday => _ytUsedToday;
    public int IgPostsInWindow(long now) { var c = 0; for (var i = 0; i < _igHistN; i++) if (now - _igHist[i] < Day) c++; return c; }
    public double IgTokens => _igTokens;
    public int Reserved(PublishPlatforms p) => _resN[Index(p)];
    public long LastPublish(PublishPlatforms p) => _lastPublish[Index(p)];
    public long Consumed { get; private set; }
    public long Rejected { get; private set; }
    public long GapRejects { get; private set; }
    public long QuotaRejects { get; private set; }
    public long Overflow { get; private set; }

    public static int Index(PublishPlatforms p) => p == PublishPlatforms.Instagram ? 1 : 0;
    public static PublishPlatforms Of(SocialPlatform p) => p == SocialPlatform.YouTube ? PublishPlatforms.YouTube : PublishPlatforms.Instagram;
    /// <summary>YouTube 쿼터 일 번호(태평양 자정 경계).</summary>
    public static long YtDayOf(long unix) => (long)Math.Floor((unix + PacificOffsetSec) / (double)Day);
    public static long YtDayStart(long day) => day * Day - PacificOffsetSec;

    // ══════════════════════ 계획(예약표 · 무할당) ══════════════════════
    public void ClearReservations() { _resN[0] = 0; _resN[1] = 0; }

    /// <summary>t 이상에서 간격 · 일일 예산(YT) · 24 h 롤링(IG)을 만족하는 최초 시각(실 소비 이력 포함). 수평선 초과 = long.MaxValue.</summary>
    public long EarliestFit(PublishPlatforms p, long t, long horizonUnix)
    {
        var k = Index(p); var res = _res[k];
        for (var guard = 0; guard < 4 * ReserveCap + 64 && t <= horizonUnix; guard++)
        {
            var moved = false;
            // 간격: 예약 · 마지막 실 발행
            if (t - _lastPublish[k] < MinGapSec) { t = _lastPublish[k] + MinGapSec; moved = true; }
            for (var i = 0; i < _resN[k]; i++) if (Math.Abs(res[i] - t) < MinGapSec) { t = res[i] + MinGapSec; moved = true; }
            if (moved) continue;
            if (k == 0)
            {
                var day = YtDayOf(t); var used = day == _ytDay ? _ytUsedToday : 0;
                for (var i = 0; i < _resN[0]; i++) if (YtDayOf(res[i]) == day) used += YtUploadCost;
                if (used + YtUploadCost > YtUploadBudget) { t = YtDayStart(day + 1); continue; }
            }
            else
            {
                var c = 0; long oldest = long.MaxValue;
                for (var i = 0; i < _resN[1]; i++) if (res[i] <= t && t - res[i] < Day) { c++; if (res[i] < oldest) oldest = res[i]; }
                for (var i = 0; i < _igHistN; i++) if (_igHist[i] <= t && t - _igHist[i] < Day) { c++; if (_igHist[i] < oldest) oldest = _igHist[i]; }
                if (c + 1 > IgPosts24h) { t = oldest + Day; continue; }
            }
            return t;
        }
        return long.MaxValue;
    }

    public bool Reserve(PublishPlatforms p, long t)
    {
        var k = Index(p);
        if (_resN[k] == ReserveCap) { Overflow++; return false; }
        _res[k][_resN[k]++] = t;
        return true;
    }

    /// <summary>계획상 해당 YT 일의 예약 유닛(게이지).</summary>
    public int ProjectedYtUnits(long day)
    {
        var u = day == _ytDay ? _ytUsedToday : 0;
        for (var i = 0; i < _resN[0]; i++) if (YtDayOf(_res[0][i]) == day) u += YtUploadCost;
        return u;
    }

    /// <summary>계획상 [now, now + 24 h) IG 게시 수(실 이력 포함).</summary>
    public int ProjectedIgPosts(long now)
    {
        var c = IgPostsInWindow(now);
        for (var i = 0; i < _resN[1]; i++) if (_res[1][i] >= now && _res[1][i] - now < Day) c++;
        return c;
    }

    // ══════════════════════ 실행(토큰 버킷 · 무할당) ══════════════════════
    private void Refill(long now)
    {
        var day = YtDayOf(now);
        if (day != _ytDay) { _ytDay = day; _ytUsedToday = 0; }
        if (_igTokensStamp == 0) _igTokensStamp = now;
        if (now > _igTokensStamp) { _igTokens = Math.Min(IgCallsPerHour, _igTokens + (now - _igTokensStamp) * (IgCallsPerHour / 3600.0)); _igTokensStamp = now; }
    }

    /// <summary>발행 직전 실 소비 — 간격 · 일일 유닛 · 롤링 게시 · 호출 버킷 전부 통과해야 기록.</summary>
    public bool TryConsume(PublishPlatforms p, long now)
    {
        Refill(now);
        var k = Index(p);
        if (now - _lastPublish[k] < MinGapSec) { Rejected++; GapRejects++; return false; }
        if (k == 0)
        {
            if (_ytUsedToday + YtUploadCost > YtUploadBudget) { Rejected++; QuotaRejects++; return false; }
            _ytUsedToday += YtUploadCost;
        }
        else
        {
            if (IgPostsInWindow(now) + 1 > IgPosts24h || _igTokens < IgCallsPerPost) { Rejected++; QuotaRejects++; return false; }
            _igTokens -= IgCallsPerPost;
            _igHist[_igHistHead] = now; _igHistHead = (_igHistHead + 1) % HistoryCap; if (_igHistN < HistoryCap) _igHistN++;
        }
        _lastPublish[k] = now; Consumed++;
        return true;
    }

    /// <summary>A/B 승격 등 비업로드 유닛 소비(YT 예비분에서 차감 · 예비 초과 시 false).</summary>
    public bool ConsumeYtUnits(long now, int units)
    {
        Refill(now);
        if (_ytUsedToday + units > YtDailyUnits) { Rejected++; QuotaRejects++; return false; }
        _ytUsedToday += units;
        return true;
    }

    /// <summary>실 소비 기준 다음 허용 시각(간격 · 일일 · 롤링 · 버킷).</summary>
    public long NextAllowed(PublishPlatforms p, long now)
    {
        Refill(now);
        var k = Index(p);
        var t = Math.Max(now, _lastPublish[k] + MinGapSec);
        if (k == 0) { if (YtDayOf(t) == _ytDay && _ytUsedToday + YtUploadCost > YtUploadBudget) t = YtDayStart(_ytDay + 1); }
        else
        {
            if (IgPostsInWindow(t) + 1 > IgPosts24h) { long oldest = long.MaxValue; for (var i = 0; i < _igHistN; i++) if (t - _igHist[i] < Day && _igHist[i] < oldest) oldest = _igHist[i]; t = Math.Max(t, oldest + Day); }
            if (_igTokens < IgCallsPerPost) t = Math.Max(t, now + (long)Math.Ceiling((IgCallsPerPost - _igTokens) * 3600.0 / IgCallsPerHour));
        }
        return t;
    }

    /// <summary>하루 소진 예측(0~1) — YT = 예약 포함 유닛 / 10,000 · IG = 24 h 게시 / 50.</summary>
    public float YtGauge(long now) => Math.Clamp(ProjectedYtUnits(YtDayOf(now)) / (float)YtDailyUnits, 0f, 1f);
    public float IgGauge(long now) => Math.Clamp(ProjectedIgPosts(now) / (float)IgPosts24h, 0f, 1f);

    public void ResetRuntime()
    {
        _ytDay = long.MinValue; _ytUsedToday = 0; _igHistN = 0; _igHistHead = 0; _igTokens = IgCallsPerHour; _igTokensStamp = 0;
        _lastPublish[0] = _lastPublish[1] = long.MinValue / 2;
        Consumed = Rejected = GapRejects = QuotaRejects = 0;
    }
}
