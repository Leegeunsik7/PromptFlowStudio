using System;
using System.Collections.Generic;
using System.Globalization;

namespace PromptFlowStudio.Services.Render;

/// <summary>⚡ 프로바이더 회로 상태.</summary>
public enum ProviderCircuitState
{
    /// <summary>정상 — 모든 호출 통과.</summary>
    Closed = 0,
    /// <summary>차단 — 냉각 시간 동안 호출 생략(즉시 실패).</summary>
    Open = 1,
    /// <summary>반개방 — 탐침 1건만 통과(성공 → Closed · 실패 → 냉각 2배로 Open).</summary>
    HalfOpen = 2,
}

/// <summary>⚡ 프로바이더별 회로 텔레메트리 스냅샷.</summary>
public sealed record ProviderCircuitSnapshot(string Provider, ProviderCircuitState State, int ConsecutiveFailures, int TripCount, int TotalFailures, int TotalSuccesses,
    int ShortCircuitedCalls, TimeSpan RetryAfter, string? LastError)
{
    /// <summary>⚡ 상태 1줄(결정론 한국어).</summary>
    public string ToLabel()
    {
        var inv = CultureInfo.InvariantCulture;
        var state = State switch
        {
            ProviderCircuitState.Open => "차단(약 " + Math.Ceiling(RetryAfter.TotalSeconds).ToString("0", inv) + "초 후 탐침)",
            ProviderCircuitState.HalfOpen => "반개방(탐침 중)",
            _ => "정상",
        };
        return Provider + " " + state + " · 연속 실패 " + ConsecutiveFailures.ToString(inv) + " · 차단 " + TripCount.ToString(inv) + "회 · 생략 " + ShortCircuitedCalls.ToString(inv) + "건";
    }
}

/// <summary>
/// ⚡ 영상 프로바이더 회로 차단기(순수 BCL · 스레드 안전 · 주입 시계로 결정론).
/// 컷 재시도(<see cref="CutRetryPolicy"/>)는 '한 컷' 안의 일시 장애만 흡수한다 — 프로바이더가 통째로 내려가면 3컷 × 3부작 = 9컷이 각각
/// 3회씩(백오프 5초) 호출해 27회·수 분을 낭비한다. 이 차단기는 '작업 단위' 일시 실패가 <see cref="FailureThreshold"/>회 연속되면 회로를 열어
/// 냉각 시간 동안 호출 자체를 생략하고, 냉각 후 탐침 1건으로 복구를 확인한다(실패 시 냉각 2배 · 상한 <see cref="MaxCooldown"/>).
///   ▸ 인증/검증 등 영구 오류는 프로바이더가 '살아 있다'는 증거 → 회로를 열지 않고 연속 실패를 초기화.
///   ▸ 취소는 판정 보류 — 반개방 탐침 슬롯만 반납.
///   ▸ 🖐️ 운영자 개입: <see cref="RequestProbe"/>(냉각을 건너뛰고 다음 호출을 탐침으로) · <see cref="Reset"/>(즉시 닫기) ·
///     <see cref="StateChanged"/>(상태 전이 통지 → UI 배지).
/// </summary>
public sealed class ProviderCircuitBreaker
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);
    public const int MaxErrorLength = 200;

    private sealed class Circuit
    {
        public ProviderCircuitState State;
        public int ConsecutiveFailures, TripCount, TotalFailures, TotalSuccesses, ShortCircuited, CooldownLevel;
        public DateTimeOffset OpenedAt;
        public TimeSpan Cooldown;
        public bool ProbeInFlight;
        public string? LastError;
    }

    private readonly Dictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>현재 시각(테스트 주입용).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>⚡ 회로 상태 전이(Closed ↔ Open ↔ HalfOpen) 통지 — 잠금 밖에서 발생 · 백그라운드 스레드 가능 · 핸들러 예외 격리.</summary>
    public event Action? StateChanged;

    private void RaiseStateChanged()
    {
        var h = StateChanged;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Render.CircuitBreaker] StateChanged handler: " + ex.Message); }
        }
    }

    /// <summary>
    /// 🖐️ 수동 탐침 요청 — Open 회로의 남은 냉각을 건너뛰어 다음 <see cref="TryAcquire"/> 1건이 반개방 탐침이 되게 한다
    /// (냉각 단계는 유지 → 탐침 실패 시 냉각 2배로 다시 차단). 반환 true = Open 이었고 탐침이 예약됨.
    /// </summary>
    public bool RequestProbe(string? provider)
    {
        lock (_sync)
        {
            if (!_circuits.TryGetValue(Key(provider), out var c) || c.State != ProviderCircuitState.Open) return false;
            c.Cooldown = TimeSpan.Zero;
        }
        RaiseStateChanged();
        return true;
    }

    /// <summary>
    /// 🖐️ 수동 해제 — 회로를 즉시 닫고 연속 실패 · 냉각 단계를 초기화(누적 통계는 보존). 프로바이더 복구를 운영자가 직접 확인했을 때 사용.
    /// 반환 true = 닫힌 상태가 아니었거나 연속 실패가 남아 있었음.
    /// </summary>
    public bool Reset(string? provider)
    {
        bool changed;
        lock (_sync)
        {
            if (!_circuits.TryGetValue(Key(provider), out var c)) return false;
            changed = c.State != ProviderCircuitState.Closed || c.ConsecutiveFailures > 0;
            Close(c);
        }
        if (changed) RaiseStateChanged();
        return changed;
    }

    /// <summary>⚡ 닫히지 않은(Open · HalfOpen) 회로 스냅샷(키 사전순) — UI 배지용.</summary>
    public IReadOnlyList<ProviderCircuitSnapshot> GetTrippedSnapshots()
    {
        var all = GetSnapshots();
        List<ProviderCircuitSnapshot>? tripped = null;
        for (var i = 0; i < all.Count; i++)
            if (all[i].State != ProviderCircuitState.Closed) (tripped ??= new List<ProviderCircuitSnapshot>()).Add(all[i]);
        return tripped is null ? Array.Empty<ProviderCircuitSnapshot>() : tripped;
    }

    /// <summary>
    /// 호출 허가 — Closed → true · Open + 냉각 경과 → 반개방 탐침 1건 true · 그 외 false(<paramref name="retryAfter"/> = 남은 냉각 · 탐침 중이면 0).
    /// true 를 받은 호출자는 반드시 <see cref="RecordSuccess"/> · <see cref="RecordTransientFailure"/> · <see cref="RecordReachable"/> ·
    /// <see cref="RecordAbandoned"/> 중 하나로 결과를 보고해야 한다.
    /// </summary>
    public bool TryAcquire(string? provider, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var key = Key(provider);
        lock (_sync)
        {
            if (!_circuits.TryGetValue(key, out var c) || c.State == ProviderCircuitState.Closed) return true;
            if (c.State == ProviderCircuitState.Open)
            {
                var elapsed = Clock() - c.OpenedAt;
                if (elapsed < c.Cooldown && elapsed >= TimeSpan.Zero)   // 시계 역행 → 영구 차단 방지(탐침 허용)
                {
                    retryAfter = c.Cooldown - elapsed;
                    c.ShortCircuited++;
                    return false;
                }
                c.State = ProviderCircuitState.HalfOpen;
                c.ProbeInFlight = true;
            }
            else
            {
                if (c.ProbeInFlight) { c.ShortCircuited++; return false; }
                c.ProbeInFlight = true;
                return true;
            }
        }
        RaiseStateChanged();   // Open → HalfOpen
        return true;
    }

    /// <summary>성공 → Closed · 연속 실패/냉각 단계 초기화.</summary>
    public void RecordSuccess(string? provider)
    {
        bool changed;
        lock (_sync)
        {
            var c = Get(Key(provider));
            c.TotalSuccesses++;
            changed = c.State != ProviderCircuitState.Closed;
            Close(c);
        }
        if (changed) RaiseStateChanged();
    }

    /// <summary>영구 오류(인증 · 검증) — 프로바이더는 응답 중 → 회로 닫음(성공 계수는 올리지 않음).</summary>
    public void RecordReachable(string? provider)
    {
        var changed = false;
        lock (_sync)
        {
            if (_circuits.TryGetValue(Key(provider), out var c))
            {
                changed = c.State != ProviderCircuitState.Closed;
                Close(c);
            }
        }
        if (changed) RaiseStateChanged();
    }

    /// <summary>판정 보류(취소) — 반개방 탐침 슬롯 반납(다음 호출이 다시 탐침).</summary>
    public void RecordAbandoned(string? provider)
    {
        lock (_sync)
        {
            if (_circuits.TryGetValue(Key(provider), out var c)) c.ProbeInFlight = false;
        }
    }

    /// <summary>작업 단위 일시 실패(재시도 소진 후) — 임계 도달 또는 탐침 실패 → Open. 반환 true = 이번 실패로 회로가 열림.</summary>
    public bool RecordTransientFailure(string? provider, string? error)
    {
        var tripped = RecordTransientFailureCore(provider, error);
        if (tripped) RaiseStateChanged();
        return tripped;
    }

    private bool RecordTransientFailureCore(string? provider, string? error)
    {
        lock (_sync)
        {
            var c = Get(Key(provider));
            c.TotalFailures++;
            c.ConsecutiveFailures++;
            c.LastError = string.IsNullOrWhiteSpace(error) ? null : error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
            var probeFailed = c.State == ProviderCircuitState.HalfOpen;
            c.ProbeInFlight = false;
            if (!probeFailed && (c.State == ProviderCircuitState.Open || c.ConsecutiveFailures < FailureThreshold)) return false;
            c.Cooldown = CooldownFor(c.CooldownLevel);
            if (c.CooldownLevel < 16) c.CooldownLevel++;
            c.State = ProviderCircuitState.Open;
            c.OpenedAt = Clock();
            c.TripCount++;
            return true;
        }
    }

    /// <summary>냉각 시간 = 30초 × 2^단계(상한 5분).</summary>
    public static TimeSpan CooldownFor(int level)
    {
        var seconds = BaseCooldown.TotalSeconds * Math.Pow(2.0, Math.Clamp(level, 0, 16));
        return seconds >= MaxCooldown.TotalSeconds ? MaxCooldown : TimeSpan.FromSeconds(seconds);
    }

    public ProviderCircuitSnapshot GetSnapshot(string? provider)
    {
        var key = Key(provider);
        lock (_sync) return _circuits.TryGetValue(key, out var c) ? Snap(key, c) : new ProviderCircuitSnapshot(key, ProviderCircuitState.Closed, 0, 0, 0, 0, 0, TimeSpan.Zero, null);
    }

    /// <summary>추적 중인 전 프로바이더 스냅샷(키 사전순).</summary>
    public IReadOnlyList<ProviderCircuitSnapshot> GetSnapshots()
    {
        var list = new List<ProviderCircuitSnapshot>();
        lock (_sync) foreach (var kv in _circuits) list.Add(Snap(kv.Key, kv.Value));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Provider, b.Provider));
        return list;
    }

    /// <summary>⚡ 차단 중 즉시 실패 메시지(한국어 · 일시 장애 재시도 패턴과 겹치지 않는 문구).</summary>
    public static string BuildOpenMessage(string? provider, TimeSpan retryAfter)
    {
        var wait = retryAfter > TimeSpan.Zero
            ? "약 " + Math.Ceiling(retryAfter.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "초 뒤 자동 탐침"
            : "복구 탐침 진행 중";
        return OpenMessagePrefix + Key(provider) + " 연속 장애로 호출을 잠시 멈췄습니다 — " + wait + "(완료된 컷은 체크포인트로 보존)";
    }

    public const string OpenMessagePrefix = "⚡ 회로 차단: ";

    /// <summary>회로 차단 즉시 실패 메시지 여부.</summary>
    public static bool IsOpenMessage(string? message) => message is not null && message.StartsWith(OpenMessagePrefix, StringComparison.Ordinal);

    private ProviderCircuitSnapshot Snap(string key, Circuit c)
    {
        var retry = TimeSpan.Zero;
        if (c.State == ProviderCircuitState.Open)
        {
            var left = c.Cooldown - (Clock() - c.OpenedAt);
            retry = left > TimeSpan.Zero ? (left > c.Cooldown ? c.Cooldown : left) : TimeSpan.Zero;
        }
        return new ProviderCircuitSnapshot(key, c.State, c.ConsecutiveFailures, c.TripCount, c.TotalFailures, c.TotalSuccesses, c.ShortCircuited, retry, c.LastError);
    }

    private static void Close(Circuit c)
    {
        c.State = ProviderCircuitState.Closed;
        c.ConsecutiveFailures = 0;
        c.CooldownLevel = 0;
        c.ProbeInFlight = false;
    }

    private Circuit Get(string key)
    {
        if (!_circuits.TryGetValue(key, out var c)) { c = new Circuit(); _circuits[key] = c; }
        return c;
    }

    private static string Key(string? provider) => string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim();
}
