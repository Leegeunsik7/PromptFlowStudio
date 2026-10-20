using System.Security.Cryptography;

namespace PromptFlowStudio.Services.Sovereign;

[Flags]
public enum TenantRole : byte { None = 0, Viewer = 1, Editor = 2, Owner = 4, Auditor = 8, Operator = 16 }

public enum AssetAction : byte { Read, Write, Delete, Render, Export, Share, Seal }

public enum Classification : byte { Public, Internal, Confidential, Secret }

public enum DenyReason : byte
{
    None, UnknownSubject, UnknownAsset, SubjectQuarantined, CrossTenant, ShareExpired, ShareActionNotGranted, RoleNotPermitted,
    ClearanceTooLow, MfaRequired, ResidencyViolation, ExplicitDeny, QuotaGpu, QuotaRam, QuotaNetwork,
}

/// <summary>권한 판정(값 형식 · 사유 enum — 문자열은 UI 지연).</summary>
public readonly record struct AccessDecision(bool Allowed, DenyReason Reason, int Subject, int Asset, AssetAction Action, bool CrossTenant);

/// <summary>요청 컨텍스트(ABAC 속성).</summary>
public readonly record struct AccessContext(Jurisdiction ClientRegion, bool MfaVerified, long NowMs);

/// <summary>
/// [PromptFlow Studio v16.53.0] 🏢 멀티 테넌트 스튜디오 격리 &amp; 정책 거버넌스.
///   ▸ RBAC(역할 비트 × 동작 권한 행렬) + ABAC(보안 등급 ≥ 분류 · Secret = MFA 필수 · 거주 관할 밖 Export 금지 · 명시 거부 우선).
///   ▸ 테넌트 경계 Zero-Trust: 타 테넌트 자산은 기본 거부 — 기한부·동작 한정 공유 승인만 예외(그래도 ABAC 재평가) · 침범 시도 테넌트×테넌트 매트릭스 · 누적 위반 주체 격리.
///   ▸ 리소스 쿼터: 테넌트별 GPU 슬롯 · RAM(MB) · 네트워크(Mbps) CAS 예약/반납(초과 판매 0) · 사용률.
///   ▸ 감사: 모든 판정 링 버퍼 + HMAC-SHA256 체인 헤드(변조 증거) — Authorize/Reserve 무할당.
/// </summary>
public sealed class MultiTenantStudioGovernanceEngine
{
    public const int MaxTenants = 16, MaxSubjects = 256, MaxAssets = 1024, MaxShares = 256, AuditCap = 512;
    public const int QuarantineAfter = 5;

    private static readonly byte[] RolePermissions = BuildRolePermissions();

    private readonly string[] _tenantNames = new string[MaxTenants];
    private readonly Jurisdiction[] _tenantJur = new Jurisdiction[MaxTenants];
    private readonly int[] _gpuLimit = new int[MaxTenants], _ramLimit = new int[MaxTenants], _netLimit = new int[MaxTenants];
    private readonly int[] _gpuUsed = new int[MaxTenants], _ramUsed = new int[MaxTenants], _netUsed = new int[MaxTenants];
    private readonly int[] _subTenant = new int[MaxSubjects], _subViolations = new int[MaxSubjects];
    private readonly TenantRole[] _subRoles = new TenantRole[MaxSubjects];
    private readonly Classification[] _subClearance = new Classification[MaxSubjects];
    private readonly bool[] _subQuarantined = new bool[MaxSubjects];
    private readonly string[] _subNames = new string[MaxSubjects];
    private readonly int[] _assetTenant = new int[MaxAssets];
    private readonly Classification[] _assetClass = new Classification[MaxAssets];
    private readonly Jurisdiction[] _assetResidency = new Jurisdiction[MaxAssets];
    private readonly string[] _assetNames = new string[MaxAssets];
    private readonly int[] _shareAsset = new int[MaxShares], _shareTenant = new int[MaxShares];
    private readonly byte[] _shareActions = new byte[MaxShares];
    private readonly long[] _shareExpiry = new long[MaxShares];
    private readonly long[] _deny = new long[MaxSubjects * 8];
    private readonly long[] _breach = new long[MaxTenants * MaxTenants];
    private readonly AccessDecision[] _audit = new AccessDecision[AuditCap];
    private readonly byte[] _auditHead = new byte[32];
    private readonly byte[] _auditKey;
    private readonly object _gate = new();
    private int _tenants, _subjects, _assets, _shares, _auditPos, _auditCount, _denyCount;
    private long _allowed, _denied, _crossBlocked, _quotaDenied, _decisions;

    public MultiTenantStudioGovernanceEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(Security.SecureSecretVault)) is Security.SecureSecretVault v ? v.ResolveOrCreate("tenant_audit_hmac") : SHA256.HashData("pfs-tenant-audit-dev"u8)) { }

    public MultiTenantStudioGovernanceEngine(byte[] auditKey) => _auditKey = auditKey;

    public event Action? OnChange;

    public int TenantCount => _tenants;
    public int SubjectCount => _subjects;
    public int AssetCount => _assets;
    public string TenantName(int t) => _tenantNames[t];
    public Jurisdiction TenantJurisdiction(int t) => _tenantJur[t];
    public string SubjectName(int s) => _subNames[s];
    public int SubjectTenant(int s) => _subTenant[s];
    public TenantRole SubjectRoles(int s) => _subRoles[s];
    public bool IsQuarantined(int s) => _subQuarantined[s];
    public string AssetName(int a) => _assetNames[a];
    public int AssetTenant(int a) => _assetTenant[a];
    public Classification AssetClassification(int a) => _assetClass[a];
    public Jurisdiction AssetResidency(int a) => _assetResidency[a];
    public long Allowed => Interlocked.Read(ref _allowed);
    public long Denied => Interlocked.Read(ref _denied);
    public long CrossTenantBlocked => Interlocked.Read(ref _crossBlocked);
    public long QuotaDenied => Interlocked.Read(ref _quotaDenied);
    public long Decisions => Interlocked.Read(ref _decisions);
    public long Breaches(int from, int to) => Interlocked.Read(ref _breach[from * MaxTenants + to]);
    public (int Gpu, int GpuLimit, int Ram, int RamLimit, int Net, int NetLimit) Usage(int t) => (Volatile.Read(ref _gpuUsed[t]), _gpuLimit[t], Volatile.Read(ref _ramUsed[t]), _ramLimit[t], Volatile.Read(ref _netUsed[t]), _netLimit[t]);
    public string AuditHeadHex { get { lock (_gate) return Convert.ToHexString(_auditHead).ToLowerInvariant(); } }
    public string? LastAction { get; private set; }

    // ══════════════════════ 등록 ══════════════════════

    public int AddTenant(string name, Jurisdiction j, int gpuSlots, int ramMb, int netMbps)
    {
        lock (_gate)
        {
            if (_tenants >= MaxTenants) return -1;
            var t = _tenants++;
            _tenantNames[t] = name; _tenantJur[t] = j; _gpuLimit[t] = gpuSlots; _ramLimit[t] = ramMb; _netLimit[t] = netMbps;
            return t;
        }
    }

    public int AddSubject(string name, int tenant, TenantRole roles, Classification clearance)
    {
        lock (_gate)
        {
            if (_subjects >= MaxSubjects || (uint)tenant >= (uint)_tenants) return -1;
            var s = _subjects++;
            _subNames[s] = name; _subTenant[s] = tenant; _subRoles[s] = roles; _subClearance[s] = clearance;
            return s;
        }
    }

    public int AddAsset(string name, int tenant, Classification c, Jurisdiction residency)
    {
        lock (_gate)
        {
            if (_assets >= MaxAssets || (uint)tenant >= (uint)_tenants) return -1;
            var a = _assets++;
            _assetNames[a] = name; _assetTenant[a] = tenant; _assetClass[a] = c; _assetResidency[a] = residency;
            return a;
        }
    }

    /// <summary>기한부·동작 한정 교차 테넌트 공유(자산 소유 테넌트 Owner만 승인 가능).</summary>
    public bool GrantShare(int grantor, int asset, int targetTenant, AssetAction[] actions, long expiresAtMs)
    {
        lock (_gate)
        {
            if ((uint)grantor >= (uint)_subjects || (uint)asset >= (uint)_assets || (uint)targetTenant >= (uint)_tenants) return false;
            if (_subTenant[grantor] != _assetTenant[asset] || (_subRoles[grantor] & TenantRole.Owner) == 0 || _shares >= MaxShares) return false;
            byte mask = 0;
            foreach (var a in actions) if (a is not (AssetAction.Share or AssetAction.Delete)) mask |= (byte)(1 << (int)a);   // 재공유·삭제는 공유 불가
            var i = _shares++;
            _shareAsset[i] = asset; _shareTenant[i] = targetTenant; _shareActions[i] = mask; _shareExpiry[i] = expiresAtMs;
        }
        LastAction = "🤝 교차 테넌트 공유 승인 · " + _assetNames[asset] + " → " + _tenantNames[targetTenant];
        Raise();
        return true;
    }

    public void RevokeShares(int asset) { lock (_gate) for (var i = 0; i < _shares; i++) if (_shareAsset[i] == asset) _shareExpiry[i] = 0; Raise(); }

    /// <summary>명시 거부(주체 × 동작) — 모든 허용보다 우선.</summary>
    public void ExplicitDeny(int subject, AssetAction action) { lock (_gate) { if (_denyCount < _deny.Length) _deny[_denyCount++] = (long)subject << 8 | (byte)action; } }

    public void ReleaseQuarantine(int subject) { lock (_gate) { _subQuarantined[subject] = false; _subViolations[subject] = 0; } Raise(); }

    // ══════════════════════ 판정 ══════════════════════

    private static byte[] BuildRolePermissions()
    {
        var p = new byte[32];
        static byte M(params AssetAction[] a) { byte m = 0; foreach (var x in a) m |= (byte)(1 << (int)x); return m; }
        p[(int)TenantRole.Viewer] = M(AssetAction.Read);
        p[(int)TenantRole.Editor] = M(AssetAction.Read, AssetAction.Write, AssetAction.Render, AssetAction.Seal);
        p[(int)TenantRole.Owner] = M(AssetAction.Read, AssetAction.Write, AssetAction.Delete, AssetAction.Render, AssetAction.Export, AssetAction.Share, AssetAction.Seal);
        p[(int)TenantRole.Auditor] = M(AssetAction.Read);
        p[(int)TenantRole.Operator] = M(AssetAction.Render, AssetAction.Seal);
        return p;
    }

    private static bool RolePermits(TenantRole roles, AssetAction a)
    {
        var bit = (byte)(1 << (int)a);
        for (var r = 1; r <= 16; r <<= 1)
            if (((int)roles & r) != 0 && (RolePermissions[r] & bit) != 0) return true;
        return false;
    }

    /// <summary>권한 판정(명시 거부 → 격리 → 테넌트 경계/공유 → RBAC → ABAC). 감사 기록. 무할당.</summary>
    public AccessDecision Authorize(int subject, AssetAction action, int asset, in AccessContext ctx)
    {
        DenyReason reason;
        var cross = false;
        lock (_gate)
        {
            reason = Evaluate(subject, action, asset, ctx, ref cross);
            var d = new AccessDecision(reason == DenyReason.None, reason, subject, asset, action, cross);
            Audit(d);
            if (reason == DenyReason.None) Interlocked.Increment(ref _allowed);
            else
            {
                Interlocked.Increment(ref _denied);
                if (cross && (uint)subject < (uint)_subjects && (uint)asset < (uint)_assets)
                {
                    Interlocked.Increment(ref _crossBlocked);
                    Interlocked.Increment(ref _breach[_subTenant[subject] * MaxTenants + _assetTenant[asset]]);
                    if (++_subViolations[subject] >= QuarantineAfter) _subQuarantined[subject] = true;
                }
            }
            return d;
        }
    }

    private DenyReason Evaluate(int s, AssetAction action, int a, in AccessContext ctx, ref bool cross)
    {
        if ((uint)s >= (uint)_subjects) return DenyReason.UnknownSubject;
        if ((uint)a >= (uint)_assets) return DenyReason.UnknownAsset;
        var key = (long)s << 8 | (byte)action;
        for (var i = 0; i < _denyCount; i++) if (_deny[i] == key) return DenyReason.ExplicitDeny;
        if (_subQuarantined[s]) return DenyReason.SubjectQuarantined;
        var st = _subTenant[s]; var at = _assetTenant[a];
        var bit = (byte)(1 << (int)action);
        if (st != at)
        {
            cross = true;
            var granted = false; var expired = false; var wrongAction = false;
            for (var i = 0; i < _shares; i++)
            {
                if (_shareAsset[i] != a || _shareTenant[i] != st) continue;
                if (ctx.NowMs >= _shareExpiry[i]) { expired = true; continue; }
                if ((_shareActions[i] & bit) == 0) { wrongAction = true; continue; }
                granted = true; break;
            }
            if (!granted) return expired ? DenyReason.ShareExpired : wrongAction ? DenyReason.ShareActionNotGranted : DenyReason.CrossTenant;
            var foreign = _subRoles[s] & ~TenantRole.Owner;                                                  // 타 테넌트 자산에서 Owner 권한은 Editor 로 강등
            if ((_subRoles[s] & TenantRole.Owner) != 0) foreign |= TenantRole.Editor;
            if (!RolePermits(foreign, action)) return DenyReason.RoleNotPermitted;
        }
        else if (!RolePermits(_subRoles[s], action)) return DenyReason.RoleNotPermitted;
        if (_subClearance[s] < _assetClass[a]) return DenyReason.ClearanceTooLow;
        if (_assetClass[a] == Classification.Secret && !ctx.MfaVerified) return DenyReason.MfaRequired;
        if (action == AssetAction.Export && ctx.ClientRegion != _assetResidency[a] && _assetClass[a] >= Classification.Confidential) return DenyReason.ResidencyViolation;
        return DenyReason.None;
    }

    private void Audit(AccessDecision d)
    {
        _audit[_auditPos] = d; _auditPos = (_auditPos + 1) % AuditCap; if (_auditCount < AuditCap) _auditCount++;
        Interlocked.Increment(ref _decisions);
        Span<byte> buf = stackalloc byte[32 + 12];
        _auditHead.CopyTo(buf);
        buf[32] = (byte)(d.Allowed ? 1 : 0); buf[33] = (byte)d.Reason; buf[34] = (byte)d.Action; buf[35] = (byte)(d.CrossTenant ? 1 : 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf[36..], d.Subject);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf[40..], d.Asset);
        HMACSHA256.HashData(_auditKey, buf, _auditHead);
    }

    /// <summary>감사 링 재생으로 체인 헤드 재계산(링이 전체 이력을 보유할 때만 유효).</summary>
    public bool VerifyAuditChain(ReadOnlySpan<AccessDecision> history, string expectedHeadHex)
    {
        Span<byte> head = stackalloc byte[32];
        head.Clear();
        Span<byte> buf = stackalloc byte[44];
        foreach (var d in history)
        {
            head.CopyTo(buf);
            buf[32] = (byte)(d.Allowed ? 1 : 0); buf[33] = (byte)d.Reason; buf[34] = (byte)d.Action; buf[35] = (byte)(d.CrossTenant ? 1 : 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf[36..], d.Subject);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf[40..], d.Asset);
            HMACSHA256.HashData(_auditKey, buf, head);
        }
        return string.Equals(Convert.ToHexString(head), expectedHeadHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>감사 링 사본(오래된 순).</summary>
    public int CopyAudit(Span<AccessDecision> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _auditCount);
            var start = (_auditPos - n + AuditCap) % AuditCap;
            for (var i = 0; i < n; i++) dst[i] = _audit[(start + i) % AuditCap];
            return n;
        }
    }

    // ══════════════════════ 쿼터 ══════════════════════

    /// <summary>리소스 예약(GPU · RAM · NET 원자적 — 하나라도 초과 시 전부 롤백). 무할당.</summary>
    public DenyReason TryReserve(int tenant, int gpu, int ramMb, int netMbps)
    {
        if ((uint)tenant >= (uint)_tenants) return DenyReason.UnknownSubject;
        if (!Cas(ref _gpuUsed[tenant], gpu, _gpuLimit[tenant])) { Interlocked.Increment(ref _quotaDenied); return DenyReason.QuotaGpu; }
        if (!Cas(ref _ramUsed[tenant], ramMb, _ramLimit[tenant])) { Interlocked.Add(ref _gpuUsed[tenant], -gpu); Interlocked.Increment(ref _quotaDenied); return DenyReason.QuotaRam; }
        if (!Cas(ref _netUsed[tenant], netMbps, _netLimit[tenant])) { Interlocked.Add(ref _gpuUsed[tenant], -gpu); Interlocked.Add(ref _ramUsed[tenant], -ramMb); Interlocked.Increment(ref _quotaDenied); return DenyReason.QuotaNetwork; }
        return DenyReason.None;
    }

    public void Release(int tenant, int gpu, int ramMb, int netMbps)
    {
        Interlocked.Add(ref _gpuUsed[tenant], -gpu); Interlocked.Add(ref _ramUsed[tenant], -ramMb); Interlocked.Add(ref _netUsed[tenant], -netMbps);
    }

    private static bool Cas(ref int used, int add, int limit)
    {
        while (true)
        {
            var cur = Volatile.Read(ref used);
            if (cur + add > limit) return false;
            if (Interlocked.CompareExchange(ref used, cur + add, cur) == cur) return true;
        }
    }

    public static string ReasonLabel(DenyReason r) => r switch
    {
        DenyReason.None => "허용", DenyReason.UnknownSubject => "미등록 주체", DenyReason.UnknownAsset => "미등록 자산", DenyReason.SubjectQuarantined => "주체 격리",
        DenyReason.CrossTenant => "테넌트 경계 침범", DenyReason.ShareExpired => "공유 만료", DenyReason.ShareActionNotGranted => "공유 범위 외 동작", DenyReason.RoleNotPermitted => "역할 권한 없음",
        DenyReason.ClearanceTooLow => "보안 등급 부족", DenyReason.MfaRequired => "MFA 필요", DenyReason.ResidencyViolation => "데이터 거주 위반", DenyReason.ExplicitDeny => "명시 거부",
        DenyReason.QuotaGpu => "GPU 쿼터 초과", DenyReason.QuotaRam => "RAM 쿼터 초과", _ => "네트워크 쿼터 초과",
    };

    public static string ActionLabel(AssetAction a) => a switch
    {
        AssetAction.Read => "읽기", AssetAction.Write => "쓰기", AssetAction.Delete => "삭제", AssetAction.Render => "렌더", AssetAction.Export => "반출", AssetAction.Share => "공유", _ => "봉인",
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Governance.OnChange", ex); } }
}
