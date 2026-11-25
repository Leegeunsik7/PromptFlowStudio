using System.Buffers.Text;

namespace PromptFlowStudio.Services.Autopilot;

public enum PublishTarget : byte { YouTube, Instagram, TikTok }
public enum TargetHealth : byte { Healthy, Backoff, RateLimited, QuotaExhausted, AuthError }

/// <summary>v17.73.0 로컬 재시도 큐 항목(값 형식).</summary>
public struct QuotaRetryItem
{
    public int ReelId, Attempt;
    public PublishTarget Target;
    public long NextAtMs, EnqueuedMs;
    public bool Used;
}

/// <summary>
/// [PromptFlow Studio v17.73.0] 🛡 소셜 플랫폼 스마트 쿼터 &amp; 지수 백오프 세이프티 가드(무할당 · 방송 스레드 비차단 — 순수 상태 기계).
///   ▸ 쿼터 회계: YouTube Data API 10 000 유닛/일(videos.insert 1 600 · 태평양 자정 리셋) · Instagram 게시 50/24 h · 호출 200/h(BUC) · TikTok 게시 15/일.
///   ▸ 응답 분류(<see cref="Observe"/>): 403 + quotaExceeded/dailyLimitExceeded/rateLimitExceeded → 쿼터 소진(리셋까지 격리) · 429 / Graph 코드 4·17·32·613 → 레이트 리밋 ·
///     5xx/0(네트워크) → 일시 백오프 · 401/190 → 인증 오류(재인증 대기) · 2xx → 정상 · 연속 실패 리셋.
///   ▸ 백오프: min(base·2ⁿ, cap) × 지터 U(0.5, 1.0)(decorrelated full jitter 변형) ∨ Retry-After 중 큰 값.
///   ▸ 라우팅(<see cref="Route"/>): 요청 플랫폼 중 즉시 가능 = 송출 마스크 · 불가 = 로컬 재시도 큐(256 · 플랫폼별 1 슬롯/릴) 격리 + 정상 플랫폼으로 우회(대체 마스크).
///   ▸ <see cref="PumpDue"/>: 시각 도래 재시도 항목을 호출자 스팬에 반환(무할당).
/// </summary>
public sealed class SmartPlatformQuotaResilienceGuard
{
    public const int Targets = 3, QueueCap = 256, YouTubeDailyUnits = 10_000, YouTubeUploadCost = 1_600, InstagramDailyPosts = 50, InstagramHourlyCalls = 200, TikTokDailyPosts = 15, MaxAttempts = 8;
    public const long BaseBackoffMs = 2_000, MaxBackoffMs = 15 * 60_000, DayMs = 86_400_000;

    private readonly TargetHealth[] _health = new TargetHealth[Targets];
    private readonly long[] _blockedUntil = new long[Targets], _resetAt = new long[Targets], _used = new long[Targets], _calls = new long[Targets], _callsResetAt = new long[Targets];
    private readonly int[] _fails = new int[Targets];
    private readonly long[] _ok = new long[Targets], _quotaHits = new long[Targets], _rateHits = new long[Targets], _transient = new long[Targets], _rerouted = new long[Targets];
    private readonly QuotaRetryItem[] _q = new QuotaRetryItem[QueueCap];
    // 제어 틱(Route/PumpDue/Requeue) · 업로드/재시도 태스크(Observe/Enqueue) 동시 변경 직렬화 — Monitor(재진입 · 무경합 시 힙 할당 0)
    private readonly object _sync = new();
    private uint _rng = 0x7A11u;
    private int _qN;
    private long _overflows, _retried, _gaveUp;

    public Func<long> Clock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long Overflows => Interlocked.Read(ref _overflows);
    public long Retried => Interlocked.Read(ref _retried);
    public long GaveUp => Interlocked.Read(ref _gaveUp);
    public int QueueDepth => Volatile.Read(ref _qN);
    public TargetHealth Health(PublishTarget t) { lock (_sync) return _health[(int)t]; }
    public long BlockedUntil(PublishTarget t) { lock (_sync) return _blockedUntil[(int)t]; }
    public int Failures(PublishTarget t) { lock (_sync) return _fails[(int)t]; }
    public long Successes(PublishTarget t) { lock (_sync) return _ok[(int)t]; }
    public long QuotaHits(PublishTarget t) { lock (_sync) return _quotaHits[(int)t]; }
    public long RateHits(PublishTarget t) { lock (_sync) return _rateHits[(int)t]; }
    public long Rerouted(PublishTarget t) { lock (_sync) return _rerouted[(int)t]; }
    public long Used(PublishTarget t) { lock (_sync) return _used[(int)t]; }
    public static int Capacity(PublishTarget t) => t switch { PublishTarget.YouTube => YouTubeDailyUnits, PublishTarget.Instagram => InstagramDailyPosts, _ => TikTokDailyPosts };
    public static int CostPerUpload(PublishTarget t) => t == PublishTarget.YouTube ? YouTubeUploadCost : 1;
    public long Remaining(PublishTarget t) { lock (_sync) { Roll((int)t, Clock()); return Math.Max(0, Capacity(t) - _used[(int)t]); } }
    public float RemainingRatio(PublishTarget t) => Remaining(t) / (float)Capacity(t);
    /// <summary>큐 슬롯 스냅숏(값 복사 — 잠금 해제 후 공유 슬롯 참조 노출 금지).</summary>
    public QuotaRetryItem Queued(int i) { lock (_sync) return _q[i]; }

    public static string TargetName(PublishTarget t) => t switch { PublishTarget.YouTube => "YouTube Shorts", PublishTarget.Instagram => "Instagram Reels", _ => "TikTok" };
    public static string HealthName(TargetHealth h) => h switch
    {
        TargetHealth.Backoff => "일시 백오프", TargetHealth.RateLimited => "레이트 리밋", TargetHealth.QuotaExhausted => "쿼터 소진", TargetHealth.AuthError => "재인증 필요", _ => "정상",
    };

    /// <summary>YouTube 쿼터 리셋 = 다음 태평양 자정(PST 고정 −8 h 근사 · DST 무시 시 최대 1 h 보수적 지연).</summary>
    public static long NextYouTubeReset(long nowMs) { const long Pst = 8 * 3_600_000L; return ((nowMs - Pst) / DayMs + 1) * DayMs + Pst; }

    private void Roll(int t, long now)
    {
        if (_resetAt[t] == 0) _resetAt[t] = t == 0 ? NextYouTubeReset(now) : now + DayMs;
        if (now >= _resetAt[t])
        {
            _used[t] = 0; _resetAt[t] = t == 0 ? NextYouTubeReset(now) : now + DayMs;
            if (_health[t] == TargetHealth.QuotaExhausted) { _health[t] = TargetHealth.Healthy; _blockedUntil[t] = 0; }
        }
        if (now >= _callsResetAt[t]) { _calls[t] = 0; _callsResetAt[t] = now + 3_600_000; }
    }

    /// <summary>즉시 업로드 가능 여부(차단 · 쿼터 · IG 시간당 호출).</summary>
    public bool Available(PublishTarget target) { lock (_sync) return AvailableCore(target); }

    private bool AvailableCore(PublishTarget target)
    {
        var t = (int)target; var now = Clock();
        Roll(t, now);
        if (_health[t] == TargetHealth.AuthError) return false;
        if (now < _blockedUntil[t]) return false;
        if (_health[t] is TargetHealth.Backoff or TargetHealth.RateLimited) _health[t] = TargetHealth.Healthy;   // 백오프 만료 → 재시도 허용(성공 시 연속 실패 리셋)
        if (_used[t] + CostPerUpload(target) > Capacity(target)) { _health[t] = TargetHealth.QuotaExhausted; _blockedUntil[t] = _resetAt[t]; return false; }
        if (target == PublishTarget.Instagram && _calls[t] + 4 > InstagramHourlyCalls) { _health[t] = TargetHealth.RateLimited; _blockedUntil[t] = _callsResetAt[t]; return false; }
        return true;
    }

    // ══════════════════════ 응답 관측(무할당) ══════════════════════
    public TargetHealth Observe(PublishTarget target, int status, ReadOnlySpan<byte> body = default, int retryAfterSec = 0, int apiCalls = 1)
    {
        // 본문 분류(순수 함수)는 잠금 밖 — 상태 변경만 임계 구역
        var quota = status == 403 && (Has(body, "quotaExceeded"u8) || Has(body, "dailyLimitExceeded"u8));
        var code = GraphCode(body);
        var rate = status == 429 || (status == 403 && (Has(body, "rateLimitExceeded"u8) || Has(body, "userRateLimitExceeded"u8)))
            || (status is 400 or 403 && code is 4 or 17 or 32 or 613);
        lock (_sync) return ObserveCore(target, status, quota, rate, status is 401 || code == 190, retryAfterSec, apiCalls);
    }

    private TargetHealth ObserveCore(PublishTarget target, int status, bool quota, bool rate, bool auth, int retryAfterSec, int apiCalls)
    {
        var t = (int)target; var now = Clock();
        Roll(t, now);
        _calls[t] += Math.Max(0, apiCalls);
        if (status is >= 200 and < 300)
        {
            _used[t] += CostPerUpload(target); _ok[t]++; _fails[t] = 0;
            _health[t] = TargetHealth.Healthy; _blockedUntil[t] = 0;
            return TargetHealth.Healthy;
        }
        if (quota)
        {
            _quotaHits[t]++; _health[t] = TargetHealth.QuotaExhausted; _used[t] = Capacity(target);
            _blockedUntil[t] = Math.Max(_resetAt[t], now + 60_000);
            return _health[t];
        }
        if (auth) { _health[t] = TargetHealth.AuthError; _blockedUntil[t] = long.MaxValue; return _health[t]; }
        _fails[t]++;
        var delay = Backoff(_fails[t]);
        if (retryAfterSec > 0) delay = Math.Max(delay, retryAfterSec * 1000L);
        if (rate) { _rateHits[t]++; _health[t] = TargetHealth.RateLimited; }
        else { _transient[t]++; _health[t] = TargetHealth.Backoff; }
        _blockedUntil[t] = now + delay;
        return _health[t];
    }

    public void ClearAuthError(PublishTarget t) { lock (_sync) { if (_health[(int)t] == TargetHealth.AuthError) { _health[(int)t] = TargetHealth.Healthy; _blockedUntil[(int)t] = 0; } } }

    /// <summary>지수 백오프 + 지터(무할당) — min(base·2ⁿ⁻¹, cap) × U(0.5, 1.0).</summary>
    public long Backoff(int attempt)
    {
        lock (_sync)
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            var exp = Math.Min(MaxBackoffMs, BaseBackoffMs << Math.Clamp(attempt - 1, 0, 20));
            return (long)(exp * (0.5 + 0.5 * ((_rng & 0xFFFF) / 65535.0)));
        }
    }

    private static bool Has(ReadOnlySpan<byte> s, ReadOnlySpan<byte> v) => !s.IsEmpty && s.IndexOf(v) >= 0;

    /// <summary>Graph 오류 JSON "code": N 판독(무할당 · 없음 = 0).</summary>
    public static int GraphCode(ReadOnlySpan<byte> body)
    {
        var i = body.IndexOf("\"code\""u8);
        if (i < 0) return 0;
        var s = body[(i + 6)..]; var c = s.IndexOf((byte)':'); if (c < 0) return 0;
        s = s[(c + 1)..].TrimStart(" "u8);
        return Utf8Parser.TryParse(s, out int v, out _) ? v : 0;
    }

    // ══════════════════════ 라우팅 · 로컬 재시도 큐(무할당) ══════════════════════
    /// <summary>요청 마스크 → 즉시 송출 마스크(반환) · 불가 플랫폼 = 재시도 큐 격리 · <paramref name="rerouteMask"/> 의 정상 플랫폼으로 우회(우회 마스크 out).</summary>
    public int Route(int reelId, int requestMask, int rerouteMask, out int rerouted)
    {
        lock (_sync)
        {
            var now = Clock(); var go = 0; rerouted = 0; var blocked = 0;
            for (var t = 0; t < Targets; t++)
            {
                if ((requestMask & (1 << t)) == 0) continue;
                if (AvailableCore((PublishTarget)t)) go |= 1 << t;
                else { blocked++; EnqueueCore(reelId, (PublishTarget)t, now); }
            }
            if (blocked > 0)
                for (var t = 0; t < Targets; t++)
                {
                    if ((rerouteMask & (1 << t)) == 0 || (go & (1 << t)) != 0 || (requestMask & (1 << t)) != 0) continue;
                    if (AvailableCore((PublishTarget)t)) { go |= 1 << t; rerouted |= 1 << t; _rerouted[t]++; }
                }
            return go;
        }
    }

    /// <summary>가드 자체 시계(<see cref="Clock"/>)로 격리 — 호출자 시계 축(TickCount64 등) 혼용 방지.</summary>
    public bool Enqueue(int reelId, PublishTarget t) { lock (_sync) return EnqueueCore(reelId, t, Clock()); }

    private bool EnqueueCore(int reelId, PublishTarget t, long now)
    {
        var free = -1;
        for (var i = 0; i < QueueCap; i++)
        {
            ref var q = ref _q[i];
            if (q.Used && q.ReelId == reelId && q.Target == t) { q.NextAtMs = Math.Max(q.NextAtMs, _blockedUntil[(int)t]); return true; }
            if (!q.Used && free < 0) free = i;
        }
        if (free < 0) { Interlocked.Increment(ref _overflows); return false; }
        _q[free] = new QuotaRetryItem { Used = true, ReelId = reelId, Target = t, Attempt = 0, EnqueuedMs = now, NextAtMs = Math.Max(now, _blockedUntil[(int)t]) };
        Volatile.Write(ref _qN, _qN + 1);
        return true;
    }

    /// <summary>시각 도래 + 플랫폼 가용 항목 → dst(최대 길이) · 큐에서 제거 · 반환 수. 재실패 시 호출자가 <see cref="Requeue"/>.</summary>
    public int PumpDue(Span<QuotaRetryItem> dst)
    {
        lock (_sync)
        {
            var now = Clock(); var n = 0;
            for (var i = 0; i < QueueCap && n < dst.Length; i++)
            {
                ref var q = ref _q[i];
                if (!q.Used || now < q.NextAtMs || !AvailableCore(q.Target)) continue;
                dst[n++] = q; q.Used = false; Volatile.Write(ref _qN, _qN - 1); Interlocked.Increment(ref _retried);
            }
            return n;
        }
    }

    /// <summary>첫 도래 릴의 항목만 반환(다른 릴 = 큐 잔류 · 시도 불변 — 무관 릴 페널티 차단). 재실패 시 호출자가 <see cref="Requeue"/>.</summary>
    public int PumpDueReel(Span<QuotaRetryItem> dst)
    {
        lock (_sync)
        {
            var now = Clock(); var n = 0; var reel = 0; var picked = false;
            for (var i = 0; i < QueueCap && n < dst.Length; i++)
            {
                ref var q = ref _q[i];
                if (!q.Used || (picked && q.ReelId != reel) || now < q.NextAtMs || !AvailableCore(q.Target)) continue;
                if (!picked) { picked = true; reel = q.ReelId; }
                dst[n++] = q; q.Used = false; Volatile.Write(ref _qN, _qN - 1); Interlocked.Increment(ref _retried);
            }
            return n;
        }
    }

    private int Slot(int reelId, PublishTarget t)
    {
        for (var i = 0; i < QueueCap; i++) if (_q[i].Used && _q[i].ReelId == reelId && _q[i].Target == t) return i;
        return -1;
    }

    /// <summary>재시도 실패 → 시도 +1 · 백오프 후 재격리(최대 시도 초과 = 포기). 격리 · 시도 갱신은 단일 임계 구역(중간 PumpDue 끼어들기 차단). 시각 = 가드 <see cref="Clock"/> 축.</summary>
    public bool Requeue(in QuotaRetryItem item)
    {
        lock (_sync)
        {
            if (item.Attempt + 1 >= MaxAttempts) { Interlocked.Increment(ref _gaveUp); return false; }
            var now = Clock();
            if (!EnqueueCore(item.ReelId, item.Target, now)) return false;
            var i = Slot(item.ReelId, item.Target);
            if (i >= 0) { _q[i].Attempt = Math.Max(_q[i].Attempt, item.Attempt + 1); _q[i].NextAtMs = Math.Max(_blockedUntil[(int)item.Target], now + Backoff(item.Attempt + 1)); }
            return true;
        }
    }

    /// <summary>시도 미소비 복귀(취소 · 스탠바이 보류 · 임대 실패) — 시도 수 보존 · 백오프 페널티 없음 · 가드 시계 축.</summary>
    public bool Reinstate(in QuotaRetryItem item)
    {
        lock (_sync)
        {
            if (!EnqueueCore(item.ReelId, item.Target, Clock())) return false;
            var i = Slot(item.ReelId, item.Target);
            if (i >= 0) _q[i].Attempt = Math.Max(_q[i].Attempt, item.Attempt);
            return true;
        }
    }
}
