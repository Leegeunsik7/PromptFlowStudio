using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

/// <summary>[v9.4.3] 테넌트 SLA 등급(높을수록 선점 우선).</summary>
public enum TenantGovTier
{
    /// <summary>베스트 에포트(선점 대상 1순위).</summary>
    BestEffort = 0,
    /// <summary>표준.</summary>
    Standard = 1,
    /// <summary>프리미엄.</summary>
    Premium = 2,
    /// <summary>미션 크리티컬(선점 불가).</summary>
    Critical = 3,
}

/// <summary>[v9.4.3] 자원 벡터(NPU/GPU 는 % 단위 슬라이스, RAM 은 MB · 불변).</summary>
public readonly record struct TenantGovResources(int NpuPct, int GpuPct, int RamMb)
{
    public static TenantGovResources Zero => default;

    public static TenantGovResources operator +(TenantGovResources a, TenantGovResources b)
        => new(a.NpuPct + b.NpuPct, a.GpuPct + b.GpuPct, a.RamMb + b.RamMb);

    public static TenantGovResources operator -(TenantGovResources a, TenantGovResources b)
        => new(a.NpuPct - b.NpuPct, a.GpuPct - b.GpuPct, a.RamMb - b.RamMb);

    /// <summary>모든 축이 상대 이하인지.</summary>
    public bool FitsWithin(TenantGovResources cap)
        => NpuPct <= cap.NpuPct && GpuPct <= cap.GpuPct && RamMb <= cap.RamMb;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"NPU {NpuPct}% · GPU {GpuPct}% · RAM {RamMb}MB");
}

/// <summary>[v9.4.3] 할당 판정 결과.</summary>
public enum TenantGovVerdict
{
    /// <summary>승인.</summary>
    Granted = 0,
    /// <summary>하위 등급 선점 후 승인.</summary>
    GrantedWithPreemption = 1,
    /// <summary>테넌트 Hard-cap 초과 거부.</summary>
    DeniedHardCap = 2,
    /// <summary>전역 오버로드 사전 차단(워터마크 초과 · 선점 불가).</summary>
    DeniedOverload = 3,
    /// <summary>미등록 테넌트.</summary>
    DeniedUnknownTenant = 4,
}

/// <summary>[v9.4.3] 할당 임대(Dispose 시 반환 · 선점 시 <see cref="Revoked"/> 토큰 취소).</summary>
public sealed class TenantGovLease : IDisposable
{
    private readonly MultiTenantResourceGovernor _owner;
    private readonly CancellationTokenSource _revoke = new();
    private int _released;

    internal TenantGovLease(MultiTenantResourceGovernor owner, long id, string tenantId, TenantGovTier tier, TenantGovResources amount)
    {
        _owner = owner;
        Id = id;
        TenantId = tenantId;
        Tier = tier;
        Amount = amount;
        GrantedAt = DateTimeOffset.Now;
    }

    public long Id { get; }
    public string TenantId { get; }
    public TenantGovTier Tier { get; }
    public TenantGovResources Amount { get; }
    public DateTimeOffset GrantedAt { get; }

    /// <summary>선점/회수 시 취소되는 토큰(작업 소유자는 이 토큰으로 협조적 중단 · 완료분 부분 구제).</summary>
    public CancellationToken Revoked => _revoke.Token;

    /// <summary>반환 완료 여부.</summary>
    public bool IsReleased => Volatile.Read(ref _released) == 1;

    internal bool TryMarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

    internal void Revoke()
    {
        try { _revoke.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }

    public void Dispose()
    {
        _owner.Release(this);
        _revoke.Dispose();
    }
}

/// <summary>[v9.4.3] 할당 결과(불변).</summary>
public sealed record TenantGovDecision(
    TenantGovVerdict Verdict,
    string TenantId,
    TenantGovResources Requested,
    TenantGovLease? Lease,
    IReadOnlyList<long> PreemptedLeaseIds,
    string Reason);

/// <summary>[v9.4.3] 테넌트 스냅샷.</summary>
public sealed record TenantGovView(
    string TenantId,
    TenantGovTier Tier,
    TenantGovResources HardCap,
    TenantGovResources InUse,
    int Leases,
    long Granted,
    long Denied,
    long Preempted);

/// <summary>
/// [PromptFlow Studio v9.4.3] 멀티테넌트 리소스 거버너(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ SLA 격리: 테넌트별 NPU/GPU/RAM Hard-cap — 사용량 + 요청이 캡을 넘으면 전역 여유와 무관하게 즉시 거부.
///   ▸ 오버로드 사전 차단: 전역 용량의 워터마크(기본 90%)를 초과하게 될 요청은 실제 포화 이전에 판정한다.
///   ▸ 우선순위 선점: 워터마크 초과 시 요청자보다 <b>엄격히 낮은</b> 등급 임대를 (등급↑·최신순) 회수해 필요량만큼 확보 — Critical 은 선점 불가.
///     선점된 임대는 <see cref="TenantGovLease.Revoked"/> 가 취소되어 소유자가 부분 구제 후 협조적으로 중단한다(제5헌장).
///   ▸ 원자성: 판정·선점·확정이 단일 락 안에서 수행되어 동시 요청 경합으로 캡을 넘지 않는다.
/// </summary>
public sealed class MultiTenantResourceGovernor
{
    private const double DefaultWatermark = 0.90d;

    private readonly object _gate = new();
    private readonly Dictionary<string, TenantState> _tenants = new(StringComparer.Ordinal);
    private readonly Dictionary<long, TenantGovLease> _leases = new();
    private TenantGovResources _inUse;
    private long _leaseSeq;
    private long _granted;
    private long _denied;
    private long _preemptions;
    private long _overloadBlocks;

    public MultiTenantResourceGovernor()
        : this(new TenantGovResources(100, 100, Math.Max(1024, (int)Math.Min(int.MaxValue, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)))))
    {
    }

    public MultiTenantResourceGovernor(TenantGovResources capacity, double watermark = DefaultWatermark)
    {
        Capacity = capacity;
        Watermark = double.IsNaN(watermark) ? DefaultWatermark : Math.Clamp(watermark, 0.5d, 1d);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>선점 발생 알림(회수된 임대).</summary>
    public event Action<TenantGovLease>? OnPreempted;

    /// <summary>전역 물리 용량.</summary>
    public TenantGovResources Capacity { get; }

    /// <summary>오버로드 사전 차단 워터마크(0.5~1).</summary>
    public double Watermark { get; }

    /// <summary>워터마크 적용 유효 용량.</summary>
    public TenantGovResources Effective
        => new((int)(Capacity.NpuPct * Watermark), (int)(Capacity.GpuPct * Watermark), (int)(Capacity.RamMb * Watermark));

    /// <summary>전역 사용량.</summary>
    public TenantGovResources InUse { get { lock (_gate) return _inUse; } }

    /// <summary>최대 축 점유율(0~1 · 물리 용량 기준).</summary>
    public double PeakUtilization
    {
        get
        {
            var u = InUse;
            return Math.Max(Ratio(u.NpuPct, Capacity.NpuPct), Math.Max(Ratio(u.GpuPct, Capacity.GpuPct), Ratio(u.RamMb, Capacity.RamMb)));
        }
    }

    public long GrantedCount => Interlocked.Read(ref _granted);
    public long DeniedCount => Interlocked.Read(ref _denied);
    public long PreemptionCount => Interlocked.Read(ref _preemptions);
    public long OverloadBlockCount => Interlocked.Read(ref _overloadBlocks);

    /// <summary>활성 임대 수.</summary>
    public int ActiveLeaseCount { get { lock (_gate) return _leases.Count; } }

    /// <summary>마지막 판정(없으면 null).</summary>
    public TenantGovDecision? LastDecision { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>테넌트 스냅샷(등급 내림차순).</summary>
    public IReadOnlyList<TenantGovView> Tenants
    {
        get
        {
            lock (_gate)
            {
                return _tenants.Values
                    .OrderByDescending(t => t.Tier).ThenBy(t => t.Id, StringComparer.Ordinal)
                    .Select(t => new TenantGovView(t.Id, t.Tier, t.HardCap, t.InUse, t.Leases,
                        t.Granted, t.Denied, t.Preempted))
                    .ToArray();
            }
        }
    }

    /// <summary>테넌트 등록/갱신(캡 축소 시 기존 임대는 유지 · 신규 요청부터 적용).</summary>
    public void RegisterTenant(string tenantId, TenantGovTier tier, TenantGovResources hardCap)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        lock (_gate)
        {
            if (_tenants.TryGetValue(tenantId, out var t))
            {
                t.Tier = tier;
                t.HardCap = hardCap;
            }
            else _tenants[tenantId] = new TenantState(tenantId, tier, hardCap);
        }
        Touch($"테넌트 {tenantId} · {tier} · {hardCap}");
    }

    /// <summary>데모 테넌트 4종(Critical/Premium/Standard/BestEffort) 등록 — 멱등.</summary>
    public void SeedDemoTenants()
    {
        var ram = Capacity.RamMb;
        RegisterTenant("studio-live", TenantGovTier.Critical, new TenantGovResources(40, 50, ram / 3));
        RegisterTenant("agency-pro", TenantGovTier.Premium, new TenantGovResources(40, 40, ram / 3));
        RegisterTenant("team-std", TenantGovTier.Standard, new TenantGovResources(30, 30, ram / 4));
        RegisterTenant("batch-free", TenantGovTier.BestEffort, new TenantGovResources(60, 60, ram / 2));
    }

    /// <summary>
    /// 자원 할당을 요청한다. Hard-cap → 워터마크 → 하위 등급 선점 순으로 판정하며, 판정·선점·확정은 원자적이다.
    /// </summary>
    public TenantGovDecision TryAcquire(string tenantId, TenantGovResources amount)
    {
        var req = new TenantGovResources(Math.Max(0, amount.NpuPct), Math.Max(0, amount.GpuPct), Math.Max(0, amount.RamMb));
        List<TenantGovLease>? victims = null;
        TenantGovDecision decision;

        lock (_gate)
        {
            if (!_tenants.TryGetValue(tenantId ?? string.Empty, out var t))
            {
                decision = Deny(null, tenantId ?? string.Empty, req, TenantGovVerdict.DeniedUnknownTenant, "미등록 테넌트");
            }
            else if (!(t.InUse + req).FitsWithin(t.HardCap))
            {
                decision = Deny(t, t.Id, req, TenantGovVerdict.DeniedHardCap, "Hard-cap 초과 · 사용 " + t.InUse + " / 캡 " + t.HardCap);
            }
            else
            {
                var eff = Effective;
                var projected = _inUse + req;
                if (!projected.FitsWithin(eff))
                {
                    // 필요량 = 초과분(축별) — 요청자보다 엄격히 낮은 등급만 회수 후보.
                    var need = Deficit(projected, eff);
                    var candidates = _leases.Values
                        .Where(l => l.Tier < t.Tier)
                        .OrderBy(l => l.Tier).ThenByDescending(l => l.Id)
                        .ToList();

                    var reclaimed = TenantGovResources.Zero;
                    victims = new List<TenantGovLease>();
                    foreach (var l in candidates)
                    {
                        if (Covers(reclaimed, need)) break;
                        if (!Helps(l.Amount, need, reclaimed)) continue;
                        victims.Add(l);
                        reclaimed += l.Amount;
                    }

                    if (!Covers(reclaimed, need))
                    {
                        victims = null;
                        Interlocked.Increment(ref _overloadBlocks);
                        decision = Deny(t, t.Id, req, TenantGovVerdict.DeniedOverload,
                            "오버로드 사전 차단 · 예상 " + projected + " > 워터마크 " + eff);
                        goto Done;
                    }

                    foreach (var v in victims) ReleaseLocked(v, preempted: true);
                }

                var lease = new TenantGovLease(this, ++_leaseSeq, t.Id, t.Tier, req);
                _leases[lease.Id] = lease;
                _inUse += req;
                t.InUse += req;
                t.Leases++;
                t.Granted++;
                Interlocked.Increment(ref _granted);

                var preemptedIds = victims is { Count: > 0 } ? victims.Select(v => v.Id).ToArray() : Array.Empty<long>();
                decision = new TenantGovDecision(
                    preemptedIds.Length > 0 ? TenantGovVerdict.GrantedWithPreemption : TenantGovVerdict.Granted,
                    t.Id, req, lease, preemptedIds,
                    preemptedIds.Length > 0 ? "하위 등급 " + preemptedIds.Length.ToString(CultureInfo.InvariantCulture) + "건 선점 후 승인" : "승인");
            }
        Done:
            LastDecision = decision;
        }

        // 선점 통지·토큰 취소는 락 밖에서(구독자 재진입 교착 방지).
        if (victims is { Count: > 0 })
        {
            foreach (var v in victims)
            {
                v.Revoke();
                try { OnPreempted?.Invoke(v); } catch { /* 구독자 예외 격리 */ }
            }
        }

        Touch(decision.TenantId + " · " + VerdictLabel(decision.Verdict) + " · " + req);
        return decision;
    }

    /// <summary>임대 반환(멱등).</summary>
    public void Release(TenantGovLease? lease)
    {
        if (lease is null) return;
        bool released;
        lock (_gate) released = ReleaseLocked(lease, preempted: false);
        if (released) Touch("반환 #" + lease.Id.ToString(CultureInfo.InvariantCulture) + " " + lease.TenantId);
    }

    /// <summary>전 임대 회수(세션 리셋 · 통계 보존).</summary>
    public int ReleaseAll()
    {
        TenantGovLease[] all;
        lock (_gate)
        {
            all = _leases.Values.ToArray();
            foreach (var l in all) ReleaseLocked(l, preempted: false);
        }
        foreach (var l in all) l.Revoke();
        Touch("전 임대 회수 " + all.Length.ToString(CultureInfo.InvariantCulture) + "건");
        return all.Length;
    }

    public static string VerdictLabel(TenantGovVerdict v) => v switch
    {
        TenantGovVerdict.Granted => "승인",
        TenantGovVerdict.GrantedWithPreemption => "선점 승인",
        TenantGovVerdict.DeniedHardCap => "캡 거부",
        TenantGovVerdict.DeniedOverload => "과부하 차단",
        _ => "미등록",
    };

    // ── 내부 ──

    private bool ReleaseLocked(TenantGovLease lease, bool preempted)
    {
        if (!lease.TryMarkReleased() || !_leases.Remove(lease.Id)) return false;
        _inUse -= lease.Amount;
        if (_tenants.TryGetValue(lease.TenantId, out var t))
        {
            t.InUse -= lease.Amount;
            t.Leases = Math.Max(0, t.Leases - 1);
            if (preempted) t.Preempted++;
        }
        if (preempted) Interlocked.Increment(ref _preemptions);
        return true;
    }

    private TenantGovDecision Deny(TenantState? t, string tenantId, TenantGovResources req, TenantGovVerdict verdict, string reason)
    {
        if (t is not null) t.Denied++;
        Interlocked.Increment(ref _denied);
        return new TenantGovDecision(verdict, tenantId, req, null, Array.Empty<long>(), reason);
    }

    private static TenantGovResources Deficit(TenantGovResources projected, TenantGovResources eff)
        => new(Math.Max(0, projected.NpuPct - eff.NpuPct), Math.Max(0, projected.GpuPct - eff.GpuPct), Math.Max(0, projected.RamMb - eff.RamMb));

    private static bool Covers(TenantGovResources got, TenantGovResources need)
        => got.NpuPct >= need.NpuPct && got.GpuPct >= need.GpuPct && got.RamMb >= need.RamMb;

    // 아직 부족한 축에 기여하는 임대만 선점(불필요한 회수 최소화).
    private static bool Helps(TenantGovResources amount, TenantGovResources need, TenantGovResources got)
        => (got.NpuPct < need.NpuPct && amount.NpuPct > 0)
        || (got.GpuPct < need.GpuPct && amount.GpuPct > 0)
        || (got.RamMb < need.RamMb && amount.RamMb > 0);

    private static double Ratio(int used, int cap) => cap <= 0 ? 0d : Math.Clamp(used / (double)cap, 0d, 1d);

    private void Touch(string action)
    {
        LastAction = action;
        OnChange?.Invoke();
    }

    private sealed class TenantState
    {
        public TenantState(string id, TenantGovTier tier, TenantGovResources hardCap)
        {
            Id = id;
            Tier = tier;
            HardCap = hardCap;
        }

        public string Id { get; }
        public TenantGovTier Tier { get; set; }
        public TenantGovResources HardCap { get; set; }
        public TenantGovResources InUse { get; set; }
        public int Leases { get; set; }
        public long Granted { get; set; }
        public long Denied { get; set; }
        public long Preempted { get; set; }
    }
}
