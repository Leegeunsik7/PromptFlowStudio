using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] 지능형 API 속도 제한 방어 &amp; 지연 보정 가드(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ Wan 무료 API 의 <b>Rate Limit(HTTP 429 Too Many Requests)</b> 및 쓰로틀/서버 지연을 감지해 <b>지수 백오프</b>(4→8→16→32…초)로 자동 대기·재시도하게 한다.
///   ▸ 대기 동안 <see cref="RemainingSeconds"/> 를 1초 단위로 감소시키며 <see cref="OnChange"/> 를 방송 → UI 는 <c>[⚠️ API 쿨다운 진행 중 (N초)]</c> 상태 카드로 투명하게 표시.
///   ▸ 병렬 생성 안전: 한 작업이 쿨다운을 걸면 다른 작업은 <see cref="WaitIfCoolingAsync"/> 로 함께 물러선다(공유 상태).
///   ▸ Graceful: 감지 실패/취소는 조용히 통과 — 정상 흐름을 막지 않는다. 성공하면 <see cref="ReportSuccess"/> 로 백오프 단계를 리셋.
///   ⚠ <see cref="OnChange"/> 는 백그라운드(폴링) 스레드에서 올 수 있다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링할 것.
/// </summary>
public sealed class QuotaRateGuardService
{
    private const int BaseDelaySeconds = 4;      // 1차 백오프
    private const int MaxDelaySeconds = 120;      // 상한(무한 대기 방지)
    private const int MaxHitShift = 5;            // 지수 상한(4 * 2^5 = 128 → 120 클램프)

    private readonly object _lock = new();

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;

    /// <summary>쿨다운 진행 중 여부(상태 카드 표시 조건).</summary>
    public bool IsCoolingDown { get; private set; }
    /// <summary>이번 쿨다운의 총 대기 시간(초) — 진행 바 분모.</summary>
    public int CooldownTotalSeconds { get; private set; }
    /// <summary>남은 대기 시간(초) — 1초마다 감소.</summary>
    public int RemainingSeconds { get; private set; }
    /// <summary>연속 감지 횟수(지수 백오프 지수) — 성공 시 0으로 리셋.</summary>
    public int ConsecutiveHits { get; private set; }
    /// <summary>누적 감지 횟수(세션 통계 · 표시용).</summary>
    public long TotalHits { get; private set; }
    /// <summary>마지막 감지 사유(한국어 · 카드 부제).</summary>
    public string? Reason { get; private set; }
    /// <summary>마지막 감지 시각.</summary>
    public DateTimeOffset? LastHitAt { get; private set; }

    /// <summary>최대 자동 재시도 횟수(호출부 루프 상한).</summary>
    public int MaxRetryAttempts => 5;

    /// <summary>쿨다운 진행률(0.0~1.0) — 상태 카드 진행 바.</summary>
    public double Progress => CooldownTotalSeconds <= 0
        ? 0
        : Math.Clamp(1.0 - RemainingSeconds / (double)CooldownTotalSeconds, 0, 1);

    /// <summary>
    /// 응답이 속도 제한/쓰로틀 계열인지 판정(HTTP 상태·오류 코드·메시지 종합 · 대소문자 무시).
    /// </summary>
    public static bool LooksRateLimited(int? httpStatus, string? code, string? message)
    {
        if (httpStatus == 429) return true;
        var hay = ((code ?? "") + " " + (message ?? "")).ToLowerInvariant();
        if (hay.Length == 0) return false;
        return hay.Contains("429")
            || hay.Contains("throttl")           // Throttling.*
            || hay.Contains("rate limit")
            || hay.Contains("ratelimit")
            || hay.Contains("too many request")
            || hay.Contains("requestlimit")
            || hay.Contains("limitexceeded")
            || hay.Contains("quota")
            || hay.Contains("flow control")
            || hay.Contains("serviceunavailable")
            || hay.Contains("503");
    }

    /// <summary>다음 백오프 대기 시간(초)을 계산한다(연속 감지 지수 증가). <paramref name="retryAfterSeconds"/>(서버 힌트)가 더 길면 그 값 사용. 상태는 바꾸지 않음(순수 계산).</summary>
    public int PlanBackoffSeconds(int? retryAfterSeconds)
    {
        var hits = Math.Min(ConsecutiveHits, MaxHitShift);         // 다음 대기 계산용(0-기반 지수)
        var exp = BaseDelaySeconds * (1 << hits);                  // 4,8,16,32,64,128
        var chosen = retryAfterSeconds is int r && r > 0 ? Math.Max(r, BaseDelaySeconds) : exp;
        return Math.Clamp(chosen, BaseDelaySeconds, MaxDelaySeconds);
    }

    /// <summary>
    /// 속도 제한 감지 시 호출 — 지수 백오프만큼 쿨다운(1초 단위 틱 방송)한 뒤 반환한다. 취소되면 즉시 종료.
    ///   호출부는 반환 후 재시도한다. 연속 호출마다 대기 시간이 지수적으로 늘어난다.
    /// </summary>
    public async Task CoolDownAsync(int? retryAfterSeconds, string reason, CancellationToken ct = default)
    {
        int seconds;
        lock (_lock)
        {
            seconds = PlanBackoffSeconds(retryAfterSeconds);
            ConsecutiveHits = Math.Min(ConsecutiveHits + 1, MaxHitShift + 1);
            TotalHits++;
            LastHitAt = DateTimeOffset.Now;
            Reason = string.IsNullOrWhiteSpace(reason) ? "API 속도 제한 감지" : reason;
            IsCoolingDown = true;
            CooldownTotalSeconds = seconds;
            RemainingSeconds = seconds;
        }
        OnChange?.Invoke();

        try
        {
            while (true)
            {
                lock (_lock) { if (RemainingSeconds <= 0) break; }
                await Task.Delay(1000, ct).ConfigureAwait(false);
                lock (_lock) { if (RemainingSeconds > 0) RemainingSeconds--; }
                OnChange?.Invoke();
            }
        }
        catch (OperationCanceledException) { /* 취소 — 아래에서 상태 정리 */ }
        finally
        {
            lock (_lock) { IsCoolingDown = false; RemainingSeconds = 0; }
            OnChange?.Invoke();
        }
    }

    /// <summary>이미 다른 작업이 쿨다운을 걸어 두었다면 끝날 때까지 대기(병렬 생성 시 동반 백오프). 쿨다운이 없으면 즉시 반환.</summary>
    public async Task WaitIfCoolingAsync(CancellationToken ct = default)
    {
        while (true)
        {
            lock (_lock) { if (!IsCoolingDown) return; }
            try { await Task.Delay(400, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>정상 응답 성공 시 호출 — 백오프 단계/쿨다운을 리셋(연속 감지 카운터 0). 변화 있을 때만 방송.</summary>
    public void ReportSuccess()
    {
        var changed = false;
        lock (_lock)
        {
            if (ConsecutiveHits != 0 || IsCoolingDown || RemainingSeconds != 0 || Reason is not null)
            {
                ConsecutiveHits = 0;
                IsCoolingDown = false;
                RemainingSeconds = 0;
                CooldownTotalSeconds = 0;
                Reason = null;
                changed = true;
            }
        }
        if (changed) OnChange?.Invoke();
    }
}
