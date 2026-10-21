namespace PromptFlowStudio.Services.SmartPublishing;

public enum GoldenRegion : byte { Asia, NorthAmerica, Europe }

[Flags]
public enum GoldenRegions : byte { None = 0, Asia = 1, NorthAmerica = 2, Europe = 4, All = 7 }

public enum ScheduledState : byte { Queued, Published, Failed, Unschedulable }

/// <summary>v17.76.0 예약 항목(값 형식 · 고정 표) — 페이로드 참조는 발행/취소 즉시 해제(누수 0).</summary>
public struct ScheduledPost
{
    public int ReelId, Seq;
    public byte Priority;
    public bool AbWinner;
    public PublishPlatforms Platforms;
    public GoldenRegions Regions;
    public long ReadyUnix, DesiredUnix, SlotUnix;
    public GoldenRegion SlotRegion;
    public ScheduledState State;
    public object? Payload;
    public readonly int EffectivePriority => Priority + (AbWinner ? 100 : 0);
    public readonly bool Deferred => SlotUnix > DesiredUnix;
}

/// <summary>v17.76.0 발행 이력(값 형식 · 참조 없음).</summary>
public struct PublishedRecord
{
    public int ReelId;
    public PublishPlatforms Platforms;
    public GoldenRegion Region;
    public long SlotUnix, PublishedUnix;
    public bool Ok;
}

/// <summary>
/// [PromptFlow Studio v17.76.0] 🌏 글로벌 골든타임 타겟 예약 발행 스케줄러(<see cref="Enqueue"/> · <see cref="Plan"/> · <see cref="TakeDue"/> 무할당 고정 표).
///   ▸ 권역 골든타임(고정 UTC 오프셋): 아시아 KST(UTC+9) 18~21 시 · 북미 EST(UTC−5) 12~14 시 · 유럽 GMT(UTC+0) 19~21 시 — 시청 트래픽 곡선 = 기저 0.2 + 창 중앙 정점 가우시안(σ = 창 폭 / 2).
///   ▸ 예약: 준비 시각 이후 대상 권역 중 가장 가까운 다음 골든타임(창 내부면 5 분 올림) = 희망 슬롯 → 우선순위 순 탐욕 배치(<see cref="Plan"/>):
///     희망 시각 오름차순 · 유효 우선순위(A/B 승자 +100) 내림차순 · 등록 순 → 플랫폼별 <see cref="MultiChannelQuotaDistributionGuard.EarliestFit"/>(45 분 간격 · 일일 예산) →
///     창을 벗어나면 다음 골든타임으로 이월(순차 지연) — 14 일 수평선 초과 = 배치 불가.
///   ▸ 표 상한 128 · 발행/취소 시 압축 + 페이로드 null(GC 회수 보장) · 이력 링 64(값 형식).
/// </summary>
public sealed class GlobalGoldenHourPublishingScheduler
{
    public const int Cap = 128, HistoryCap = 64, Regions = 3, SlotRoundSec = 300;
    public const long HorizonSec = 14 * 86_400;
    /// <summary>권역별 UTC 오프셋(시) · 현지 골든타임 시작/끝(시).</summary>
    public static readonly int[] UtcOffsetHours = { 9, -5, 0 };
    public static readonly int[] GoldenStartHour = { 18, 12, 19 };
    public static readonly int[] GoldenEndHour = { 21, 14, 21 };

    private readonly ScheduledPost[] _q = new ScheduledPost[Cap];
    private readonly int[] _order = new int[Cap];
    private readonly PublishedRecord[] _hist = new PublishedRecord[HistoryCap];
    private int _n, _seq, _histHead, _histN;

    public GlobalGoldenHourPublishingScheduler(MultiChannelQuotaDistributionGuard? guard = null) => Guard = guard ?? new MultiChannelQuotaDistributionGuard();

    public MultiChannelQuotaDistributionGuard Guard { get; }
    public int Count => _n;
    public ref readonly ScheduledPost At(int i) => ref _q[i];
    public int HistoryCount => _histN;
    public ref readonly PublishedRecord History(int i) => ref _hist[(_histHead - 1 - i + 2 * HistoryCap) % HistoryCap];
    public long Enqueued { get; private set; }
    public long Duplicates { get; private set; }
    public long Full { get; private set; }
    public long Plans { get; private set; }
    public long Deferrals { get; private set; }
    public long Published { get; private set; }
    public long Unschedulable { get; private set; }
    public double LastPlanUs { get; private set; }

    public static string RegionName(GoldenRegion r) => r switch { GoldenRegion.Asia => "아시아(KST)", GoldenRegion.NorthAmerica => "북미(EST)", _ => "유럽(GMT)" };
    public static string RegionFlag(GoldenRegion r) => r switch { GoldenRegion.Asia => "🇰🇷", GoldenRegion.NorthAmerica => "🇺🇸", _ => "🇬🇧" };

    // ══════════════════════ 권역 시간 수학(무할당) ══════════════════════
    /// <summary>권역 r 의 골든타임 창 [start, end) 중 t 이후(끝 &gt; t) 가장 이른 창.</summary>
    public static (long Start, long End) NextWindow(GoldenRegion r, long t)
    {
        var off = UtcOffsetHours[(int)r] * 3600L;
        var localDay = (long)Math.Floor((t + off) / 86_400.0);
        for (var d = localDay - 1; d <= localDay + 1; d++)
        {
            var s = d * 86_400 + GoldenStartHour[(int)r] * 3600L - off; var e = d * 86_400 + GoldenEndHour[(int)r] * 3600L - off;
            if (e > t) return (s, e);
        }
        var d2 = localDay + 2;
        return (d2 * 86_400 + GoldenStartHour[(int)r] * 3600L - off, d2 * 86_400 + GoldenEndHour[(int)r] * 3600L - off);
    }

    /// <summary>t 이상 대상 권역 중 가장 이른 발행 가능 시각(창 내부면 5 분 올림) · 해당 창.</summary>
    public static long NextGolden(long t, GoldenRegions mask, out GoldenRegion region, out long windowEnd)
    {
        long best = long.MaxValue; region = GoldenRegion.Asia; windowEnd = 0;
        for (var r = 0; r < Regions; r++)
        {
            if (((int)mask & (1 << r)) == 0) continue;
            var (s, e) = NextWindow((GoldenRegion)r, t);
            var c = t <= s ? s : RoundUp(t);
            if (c >= e) { (s, e) = NextWindow((GoldenRegion)r, e); c = s; }
            if (c < best) { best = c; region = (GoldenRegion)r; windowEnd = e; }
        }
        return best;
    }

    public static long RoundUp(long t) => (t + SlotRoundSec - 1) / SlotRoundSec * SlotRoundSec;

    /// <summary>t 가 대상 권역 골든타임 안이면 true(권역 · 창 끝).</summary>
    public static bool InGolden(long t, GoldenRegions mask, out GoldenRegion region, out long windowEnd)
    {
        region = GoldenRegion.Asia; windowEnd = 0;
        for (var r = 0; r < Regions; r++)
        {
            if (((int)mask & (1 << r)) == 0) continue;
            var (s, e) = NextWindow((GoldenRegion)r, t);
            if (t >= s && t < e) { region = (GoldenRegion)r; windowEnd = e; return true; }
        }
        return false;
    }

    /// <summary>권역 현지 시(0~24 실수) 시청 트래픽(0~1) — 기저 0.2 + 골든 창 중앙 가우시안.</summary>
    public static float Traffic(GoldenRegion r, double localHour)
    {
        double s = GoldenStartHour[(int)r], e = GoldenEndHour[(int)r], c = (s + e) * 0.5, sigma = (e - s) * 0.5;
        var d = localHour - c; if (d > 12) d -= 24; if (d < -12) d += 24;
        return (float)(0.2 + 0.8 * Math.Exp(-0.5 * d * d / (sigma * sigma)));
    }

    /// <summary>UTC 시각 t 의 권역 현지 시(0~24).</summary>
    public static double LocalHour(GoldenRegion r, long t)
    {
        var x = (t + UtcOffsetHours[(int)r] * 3600L) % 86_400; if (x < 0) x += 86_400;
        return x / 3600.0;
    }

    // ══════════════════════ 대기열 ══════════════════════
    /// <summary>예약 등록(같은 릴 중복 차단 · 표 가득 = false) → 즉시 재배치. 반환 = 표 인덱스(-1 = 거부).</summary>
    public int Enqueue(int reelId, long readyUnix, PublishPlatforms platforms, GoldenRegions regions = GoldenRegions.All, byte priority = 0, bool abWinner = false, object? payload = null, long nowUnix = 0)
    {
        for (var i = 0; i < _n; i++) if (_q[i].ReelId == reelId) { Duplicates++; return -1; }
        if (_n == Cap) { Full++; return -1; }
        if (platforms == PublishPlatforms.None || regions == GoldenRegions.None) return -1;
        _q[_n] = new ScheduledPost { ReelId = reelId, Seq = ++_seq, Priority = priority, AbWinner = abWinner, Platforms = platforms, Regions = regions, ReadyUnix = Math.Max(readyUnix, nowUnix), Payload = payload, State = ScheduledState.Queued };
        _n++; Enqueued++;
        Plan();
        return Find(reelId);
    }

    public int Find(int reelId) { for (var i = 0; i < _n; i++) if (_q[i].ReelId == reelId) return i; return -1; }

    /// <summary>A/B 승자 확정 등 우선순위 승격 → 재배치.</summary>
    public bool Promote(int reelId, bool abWinner = true, byte priority = 0)
    {
        var i = Find(reelId); if (i < 0) return false;
        _q[i].AbWinner = abWinner; if (priority > _q[i].Priority) _q[i].Priority = priority;
        Plan();
        return true;
    }

    public bool Cancel(int reelId)
    {
        var i = Find(reelId); if (i < 0) return false;
        RemoveAt(i); Plan();
        return true;
    }

    private void RemoveAt(int i)
    {
        _q[i].Payload = null;
        Array.Copy(_q, i + 1, _q, i, _n - i - 1);
        _n--;
        _q[_n] = default;   // 마지막 칸 참조 제거
    }

    /// <summary>전량 재배치(무할당) — 예약표 초기화 → 우선순위 순 탐욕 배치.</summary>
    public void Plan()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Guard.ClearReservations();
        var m = 0;
        for (var i = 0; i < _n; i++)
        {
            ref var p = ref _q[i];
            if (p.State != ScheduledState.Queued && p.State != ScheduledState.Unschedulable) continue;
            p.DesiredUnix = NextGolden(p.ReadyUnix, p.Regions, out p.SlotRegion, out _);
            _order[m++] = i;
        }
        // 삽입 정렬: 희망 ↑ · 유효 우선순위 ↓ · 등록 순 ↑
        for (var a = 1; a < m; a++)
        {
            var x = _order[a]; var b = a - 1;
            while (b >= 0 && Before(x, _order[b])) { _order[b + 1] = _order[b]; b--; }
            _order[b + 1] = x;
        }
        long deferred = 0;
        for (var k = 0; k < m; k++)
        {
            ref var p = ref _q[_order[k]];
            var horizon = p.ReadyUnix + HorizonSec;
            var t = p.DesiredUnix; var region = p.SlotRegion; var ok = false;
            for (var iter = 0; iter < 96 && t <= horizon; iter++)
            {
                if (!InGolden(t, p.Regions, out region, out _)) { t = NextGolden(t, p.Regions, out region, out _); continue; }
                var fit = t;
                if ((p.Platforms & PublishPlatforms.YouTube) != 0) fit = Math.Max(fit, Guard.EarliestFit(PublishPlatforms.YouTube, t, horizon));
                if ((p.Platforms & PublishPlatforms.Instagram) != 0) fit = Math.Max(fit, Guard.EarliestFit(PublishPlatforms.Instagram, fit, horizon));
                if (fit == long.MaxValue) break;
                if (fit != t) { t = fit; continue; }   // 다른 플랫폼 · 창 경계 재검증
                ok = true; break;
            }
            if (!ok)
            {
                if (p.State != ScheduledState.Unschedulable) Unschedulable++;
                p.State = ScheduledState.Unschedulable; p.SlotUnix = 0;
                continue;
            }
            p.State = ScheduledState.Queued; p.SlotUnix = t; p.SlotRegion = region;
            if ((p.Platforms & PublishPlatforms.YouTube) != 0) Guard.Reserve(PublishPlatforms.YouTube, t);
            if ((p.Platforms & PublishPlatforms.Instagram) != 0) Guard.Reserve(PublishPlatforms.Instagram, t);
            if (t > p.DesiredUnix) deferred++;
        }
        Deferrals = deferred; Plans++;
        LastPlanUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private bool Before(int x, int y)
    {
        ref readonly var a = ref _q[x]; ref readonly var b = ref _q[y];
        if (a.DesiredUnix != b.DesiredUnix) return a.DesiredUnix < b.DesiredUnix;
        if (a.EffectivePriority != b.EffectivePriority) return a.EffectivePriority > b.EffectivePriority;
        return a.Seq < b.Seq;
    }

    /// <summary>now 기준 슬롯 도래 항목 중 가장 이른 1 건 → 표에서 제거(값 복사 · 페이로드는 호출자에게 이전). 없으면 false.</summary>
    public bool TakeDue(long now, out ScheduledPost post)
    {
        var best = -1;
        for (var i = 0; i < _n; i++)
        {
            ref readonly var p = ref _q[i];
            if (p.State == ScheduledState.Queued && p.SlotUnix > 0 && p.SlotUnix <= now && (best < 0 || p.SlotUnix < _q[best].SlotUnix)) best = i;
        }
        if (best < 0) { post = default; return false; }
        post = _q[best];
        RemoveAt(best);
        return true;
    }

    /// <summary>발행 결과 기록(이력 링) · 실패 = 재등록하지 않음(호출자가 재큐 결정).</summary>
    public void Record(in ScheduledPost p, long publishedUnix, bool ok)
    {
        _hist[_histHead] = new PublishedRecord { ReelId = p.ReelId, Platforms = p.Platforms, Region = p.SlotRegion, SlotUnix = p.SlotUnix, PublishedUnix = publishedUnix, Ok = ok };
        _histHead = (_histHead + 1) % HistoryCap; if (_histN < HistoryCap) _histN++;
        if (ok) Published++;
    }

    /// <summary>예약 칸 집계(UI 캘린더 · KST 일/시 격자) — counts[day × 24 + hour] · fromUnix = KST 자정 기준 첫 날.</summary>
    public void FillCalendar(Span<int> counts, long fromUnix, int days)
    {
        counts.Clear();
        for (var i = 0; i < _n; i++)
        {
            ref readonly var p = ref _q[i];
            if (p.State != ScheduledState.Queued || p.SlotUnix < fromUnix) continue;
            var h = (p.SlotUnix - fromUnix) / 3600;
            if (h < days * 24L && h < counts.Length) counts[(int)h]++;
        }
    }

    /// <summary>테스트/진단: 표 슬롯의 페이로드 참조 수(누수 감시).</summary>
    public int PayloadRefs() { var c = 0; for (var i = 0; i < Cap; i++) if (_q[i].Payload is not null) c++; return c; }
}
