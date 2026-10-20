using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

/// <summary>테넌트 RBAC 권한 등급 — 열람/편집/관리(오름차순 권한).</summary>
public enum TenantRole { Viewer = 0, Editor = 1, Admin = 2 }

/// <summary>정책 검증 대상 작업 — 열람·렌더·내보내기·테넌트 관리.</summary>
public enum PolicyAction { Read, Render, Export, Manage }

/// <summary>
/// [PromptFlow Studio v8.3.1] B2B 테넌트 거버넌스 정책(불변) — RBAC 등급·데이터 주권 리전·리소스 임계치.
///   ▸ <see cref="SovereigntyRegion"/> = 이 테넌트의 데이터가 처리되어야 하는 규제 리전(예: "kr", "eu"). 빈 값이면 리전 무제약.
///   ▸ <see cref="MaxConcurrentRenders"/>·<see cref="MaxDailyRenderUnits"/> = 리소스 가드레일(동시 렌더·일일 유닛 상한).
/// </summary>
public sealed record TenantPolicy(
    string TenantId, TenantRole Role, string SovereigntyRegion,
    int MaxConcurrentRenders, long MaxDailyRenderUnits, bool Enabled = true);

/// <summary>
/// 정책 판정 결과(불변) — <see cref="ApiGatewayEngine"/> 와 동일한 <see cref="GatewayStatus"/> 코드로 매핑.
///   ▸ RBAC/주권/비활성 위반 → 403 Forbidden · 리소스 임계치 초과 → 429 TooManyRequests · 통과 → 200.
/// </summary>
public sealed record PolicyDecision(bool Allowed, GatewayStatus Status, string Reason, string TenantId, string? ViolationCategory = null)
{
    public int Code => (int)Status;
}

/// <summary>
/// [PromptFlow Studio v8.3.1] 엔터프라이즈 정책 거버넌스 &amp; RBAC 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 테넌트 권한(Admin/Editor/Viewer) · 데이터 주권 리전 · 동시/일일 리소스 임계치를 <see cref="Evaluate"/> 로 검증.
///   ▸ 위반 시 <see cref="OnPolicyViolation"/> 로 403(RBAC/주권) 또는 429(임계치) 차단 이벤트를 방송 —
///     <see cref="ApiGatewayEngine"/>·HUD 가 구독해 요청을 차단하고, 선택 결선된 <see cref="C2PaWatermarkEngine"/> 로 위반 프로비넌스를 봉인.
///   ▸ 일일 유닛 사용량은 UTC 일 인덱스로 롤오버(자정 자동 리셋) · 동시 요청은 <see cref="ConcurrentDictionary{TKey,TValue}"/>·Interlocked 로 락 경합 최소화.
///   ⚠ <see cref="OnChange"/>/<see cref="OnPolicyViolation"/> 은 임의 요청 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class TenantPolicyEngine
{
    private readonly ConcurrentDictionary<string, TenantPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Usage> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly C2PaWatermarkEngine? _c2pa;   // 선택 결선 — 위반 프로비넌스 서명

    private long _evaluated, _allowed, _denied403, _denied429;

    public TenantPolicyEngine(C2PaWatermarkEngine? c2pa = null) => _c2pa = c2pa;

    /// <summary>정책/카운터 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    /// <summary>정책 위반 차단 이벤트(게이트웨이 403/429 연동 · HUD 보안 카드 즉시 표출).</summary>
    public event Action<PolicyDecision>? OnPolicyViolation;
    private void Notify() => OnChange?.Invoke();

    // ── 카운터(텔레메트리/HUD) ──
    public long Evaluated => Interlocked.Read(ref _evaluated);
    public long AllowedCount => Interlocked.Read(ref _allowed);
    public long Denied403Count => Interlocked.Read(ref _denied403);
    public long Denied429Count => Interlocked.Read(ref _denied429);
    public int TenantCount => _policies.Count;
    public string? LastViolation { get; private set; }
    public string? LastViolationJumbf { get; private set; }
    public IReadOnlyList<TenantPolicy> Policies => _policies.Values.OrderBy(p => p.TenantId).ToList();

    // ── 구성 ───────────────────────────────────────────────────────────────

    /// <summary>테넌트 정책 등록/갱신 — 리전은 소문자 정규화, 임계치는 하한 클램프.</summary>
    public TenantPolicy RegisterPolicy(string tenantId, TenantRole role, string? sovereigntyRegion,
        int maxConcurrentRenders, long maxDailyRenderUnits, bool enabled = true)
    {
        var id = (tenantId ?? "").Trim();
        var policy = new TenantPolicy(
            id, role, (sovereigntyRegion ?? "").Trim().ToLowerInvariant(),
            Math.Max(1, maxConcurrentRenders), Math.Max(1, maxDailyRenderUnits), enabled);
        _policies[id] = policy;
        Notify();
        return policy;
    }

    /// <summary>테넌트 활성/비활성 전환(비활성 테넌트는 모든 작업 403).</summary>
    public void SetEnabled(string tenantId, bool enabled)
    {
        if (_policies.TryGetValue(tenantId, out var p)) { _policies[tenantId] = p with { Enabled = enabled }; Notify(); }
    }

    /// <summary>테넌트 정책 제거(사용량 카운터 동반 정리).</summary>
    public void RemovePolicy(string tenantId)
    {
        if (_policies.TryRemove(tenantId, out _)) { _usage.TryRemove(tenantId, out _); Notify(); }
    }

    /// <summary>테넌트 일일 사용량 가용 비율(0~1 · HUD 게이지) — 정책 없으면 1.</summary>
    public double QuotaFillRatio(string tenantId)
    {
        if (!_policies.TryGetValue(tenantId, out var p)) return 1;
        var used = CurrentDailyUnits(tenantId);
        return p.MaxDailyRenderUnits <= 0 ? 0 : Math.Clamp(1.0 - (double)used / p.MaxDailyRenderUnits, 0, 1);
    }

    // ── 정책 검증 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 정책 인가 — 존재·활성·RBAC·데이터 주권·리소스 임계치를 순차 검증. 통과분만 일일 유닛을 커밋한다.
    ///   ▸ <paramref name="requestRegion"/> 가 지정되고 테넌트 주권 리전과 다르면 403(데이터 주권 위반).
    ///   ▸ <paramref name="requestedUnits"/> 커밋 시 일일 상한 초과면 429(사용량 미반영).
    /// </summary>
    public PolicyDecision Evaluate(string tenantId, PolicyAction action, string? requestRegion = null, long requestedUnits = 0)
    {
        Interlocked.Increment(ref _evaluated);
        var id = (tenantId ?? "").Trim();

        if (!_policies.TryGetValue(id, out var p))
            return Deny(GatewayStatus.Forbidden, "미등록 테넌트", id, "unknown-tenant");
        if (!p.Enabled)
            return Deny(GatewayStatus.Forbidden, "비활성 테넌트", id, "tenant-disabled");
        if (!IsRoleAuthorized(p.Role, action))
            return Deny(GatewayStatus.Forbidden, $"권한 부족 — {p.Role} 등급은 {action} 불가", id, "rbac-forbidden");

        var region = (requestRegion ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(p.SovereigntyRegion) && !string.IsNullOrEmpty(region)
            && !string.Equals(region, p.SovereigntyRegion, StringComparison.Ordinal))
            return Deny(GatewayStatus.Forbidden,
                $"데이터 주권 위반 — '{region}' 처리 요청, 허용 리전 '{p.SovereigntyRegion}'", id, "sovereignty-violation");

        // 리소스 임계치 — 일일 유닛 상한(초과 시 429, 사용량 커밋 안 함).
        if (requestedUnits > 0)
        {
            var projected = ProjectedDailyUnits(id, requestedUnits);
            if (projected > p.MaxDailyRenderUnits)
            {
                Interlocked.Increment(ref _denied429);
                var d = new PolicyDecision(false, GatewayStatus.TooManyRequests,
                    $"일일 리소스 임계치 초과({p.MaxDailyRenderUnits} 유닛) — 잠시 후 재시도", id, "quota-exceeded");
                RaiseViolation(d);
                return d;
            }
            CommitDailyUnits(id, requestedUnits);   // 통과분만 사용량 반영
        }

        Interlocked.Increment(ref _allowed); Notify();
        return new PolicyDecision(true, GatewayStatus.Ok, "정책 통과", id);
    }

    /// <summary>RBAC 매트릭스 — Viewer=열람 / Editor=열람·렌더·내보내기 / Admin=전체.</summary>
    private static bool IsRoleAuthorized(TenantRole role, PolicyAction action) => action switch
    {
        PolicyAction.Read => true,                                   // 전 등급 열람 허용
        PolicyAction.Render or PolicyAction.Export => role >= TenantRole.Editor,
        PolicyAction.Manage => role >= TenantRole.Admin,
        _ => false,
    };

    private PolicyDecision Deny(GatewayStatus status, string reason, string tenantId, string category)
    {
        if (status == GatewayStatus.Forbidden) Interlocked.Increment(ref _denied403);
        else if (status == GatewayStatus.TooManyRequests) Interlocked.Increment(ref _denied429);
        var d = new PolicyDecision(false, status, reason, tenantId, category);
        RaiseViolation(d);
        return d;
    }

    /// <summary>위반 방송 + (선택) C2PA 프로비넌스 서명. 구독자/서명 예외는 격리(Graceful).</summary>
    private void RaiseViolation(PolicyDecision d)
    {
        LastViolation = $"[{d.Code}] {d.TenantId} · {d.ViolationCategory} · {d.Reason}";
        try
        {
            if (_c2pa is not null)
            {
                var claim = new C2paClaim(d.TenantId,
                    $"policy-violation:{d.ViolationCategory}:{d.Code}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                LastViolationJumbf = _c2pa.SignManifest(claim).JumbfBase64;   // 위반 감사증적 봉인
            }
        }
        catch (Exception ex) { Program.Log("TenantPolicy.C2paSign", ex); }
        try { OnPolicyViolation?.Invoke(d); } catch (Exception ex) { Program.Log("TenantPolicy.Violation", ex); }
        Notify();
    }

    // ── 일일 사용량(UTC 일 인덱스 롤오버) ──
    private static long TodayIndex => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400;

    private long CurrentDailyUnits(string tenantId)
    {
        if (!_usage.TryGetValue(tenantId, out var u)) return 0;
        lock (u.Gate) return u.DayIndex == TodayIndex ? u.Units : 0;
    }

    private long ProjectedDailyUnits(string tenantId, long add)
    {
        var u = _usage.GetOrAdd(tenantId, _ => new Usage());
        lock (u.Gate)
        {
            var day = TodayIndex;
            var cur = u.DayIndex == day ? u.Units : 0;
            return cur + Math.Max(0, add);
        }
    }

    private void CommitDailyUnits(string tenantId, long add)
    {
        var u = _usage.GetOrAdd(tenantId, _ => new Usage());
        lock (u.Gate)
        {
            var day = TodayIndex;
            if (u.DayIndex != day) { u.DayIndex = day; u.Units = 0; }   // 자정 롤오버 리셋
            u.Units += Math.Max(0, add);
        }
    }

    /// <summary>테넌트 일일 사용량(가변 · 인스턴스별 lock 으로 보호).</summary>
    private sealed class Usage
    {
        public readonly object Gate = new();
        public long DayIndex;
        public long Units;
    }
}
