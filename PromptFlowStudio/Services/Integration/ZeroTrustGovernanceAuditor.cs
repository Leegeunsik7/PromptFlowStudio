using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

public enum GovAuditPillar { TenantPolicy, C2pa, Worm }

public enum GovAuditSeverity { Pass, Warn, Fail }

/// <summary>테넌트 거버넌스 정책 — C2PA/WORM 요구·보존 기한·허용 리전.</summary>
public sealed record GovTenantPolicy(string TenantId, bool RequireC2pa, bool RequireWorm, int RetentionDays, IReadOnlyList<string> AllowedRegions);

public sealed record GovAuditFinding(GovAuditPillar Pillar, GovAuditSeverity Severity, string Subject, string Detail);

/// <summary>단일 스캔 결과 — 집계(Pass/Warn/Fail)·원장 헤드까지 리포트 정규화에 바인딩.</summary>
public sealed record GovAuditScan(
    long Sequence, DateTimeOffset At, int Tenants, int LedgerEntries, int Manifests,
    int Pass, int Warn, int Fail, string LedgerHead, IReadOnlyList<GovAuditFinding> Findings, double ElapsedMs)
{
    public bool Compliant => Fail == 0;
}

/// <summary>B2B 규정 준수 감사 리포트 — SHA-256 지문 + HMAC 서명.</summary>
public sealed record GovComplianceReport(string ReportId, DateTimeOffset GeneratedAt, GovAuditScan Scan, string Canonical, string Fingerprint, string Signature, string Markdown);

/// <summary>
/// v9.5.3 제로트러스트 거버넌스 오디터 — 테넌트 정책(기본 거부)·C2PA 매니페스트 서명/콘텐츠 바인딩·WORM 해시체인 원장을
/// 상시(타이머) 스캔해 위변조와 규정 위반을 검출하고, 집계 바인딩 서명 리포트를 자동 생성한다.
/// 해시/서명 검증 경로는 stackalloc·정적 HashData 로 무할당. 순수 BCL.
/// </summary>
public sealed class ZeroTrustGovernanceAuditor : IDisposable
{
    public const int ScanIntervalMs = 5000;
    private const int MaxField = 96;
    private const int MaxFindings = 256;

    private sealed class LedgerEntry
    {
        public long Seq;
        public DateTimeOffset At;
        public string TenantId = "";
        public string AssetId = "";
        public string Region = "";
        public byte[] ContentHash = new byte[32];
        public byte[] PrevHash = new byte[32];
        public byte[] Hash = new byte[32];
        public byte[] Mac = new byte[32];
    }

    private sealed class AssetManifest
    {
        public string AssetId = "";
        public string TenantId = "";
        public byte[] ClaimHash = new byte[32];
        public byte[] Signature = new byte[32];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, GovTenantPolicy> _policies = new(StringComparer.Ordinal);
    private readonly List<LedgerEntry> _ledger = new();
    private readonly Dictionary<string, AssetManifest> _manifests = new(StringComparer.Ordinal);
    private readonly byte[] _wormKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _c2paKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _reportKey = RandomNumberGenerator.GetBytes(32);
    private System.Threading.Timer? _timer;
    private int _scanning;
    private long _scanSeq, _failTotal, _reports;
    private GovAuditScan? _lastScan;
    private GovComplianceReport? _lastReport;
    private bool _disposed;

    public event Action? OnChange;

    public bool Armed => _timer is not null;
    public string? LastAction { get; private set; }
    public long ScanCount => Interlocked.Read(ref _scanSeq);
    public long FailTotal => Interlocked.Read(ref _failTotal);
    public long ReportCount => Interlocked.Read(ref _reports);
    public GovAuditScan? LastScan { get { lock (_gate) return _lastScan; } }
    public GovComplianceReport? LastReport { get { lock (_gate) return _lastReport; } }
    public int LedgerCount { get { lock (_gate) return _ledger.Count; } }
    public int ManifestCount { get { lock (_gate) return _manifests.Count; } }
    public int TenantCount { get { lock (_gate) return _policies.Count; } }

    public void SetPolicy(GovTenantPolicy policy)
    {
        lock (_gate) _policies[policy.TenantId] = policy;
        OnChange?.Invoke();
    }

    /// <summary>자산 등록 — WORM 원장 append(해시체인+HMAC) + 선택적 C2PA 매니페스트 서명.</summary>
    public void RecordAsset(string tenantId, string assetId, ReadOnlySpan<byte> content, string region, bool withManifest = true, DateTimeOffset? at = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = new LedgerEntry
        {
            At = at ?? DateTimeOffset.UtcNow,
            TenantId = Clip(tenantId),
            AssetId = Clip(assetId),
            Region = Clip(region),
        };
        SHA256.HashData(content, entry.ContentHash);
        lock (_gate)
        {
            entry.Seq = _ledger.Count + 1;
            if (_ledger.Count > 0) _ledger[^1].Hash.CopyTo(entry.PrevHash, 0);
            ComputeEntryHash(entry, entry.Hash);
            HMACSHA256.HashData(_wormKey, entry.Hash, entry.Mac);
            _ledger.Add(entry);
            if (withManifest)
            {
                var m = new AssetManifest { AssetId = entry.AssetId, TenantId = entry.TenantId };
                entry.ContentHash.CopyTo(m.ClaimHash, 0);
                SignManifest(m, m.Signature);
                _manifests[m.AssetId] = m;
            }
            LastAction = $"자산 {entry.AssetId} 기록 · {entry.TenantId}@{entry.Region}";
        }
        OnChange?.Invoke();
    }

    /// <summary>데모 테넌트 4종 + 자산 8건(보존 기한 초과 1건 포함).</summary>
    public void SeedDemo()
    {
        lock (_gate)
        {
            _policies.Clear();
            _ledger.Clear();
            _manifests.Clear();
            _lastScan = null;
            _lastReport = null;
            _policies["studio-live"] = new GovTenantPolicy("studio-live", true, true, 365, new[] { "kr", "jp", "us" });
            _policies["agency-pro"] = new GovTenantPolicy("agency-pro", true, true, 180, new[] { "kr", "us", "eu" });
            _policies["team-std"] = new GovTenantPolicy("team-std", false, true, 90, new[] { "kr" });
            _policies["batch-free"] = new GovTenantPolicy("batch-free", false, false, 30, new[] { "kr", "us" });
        }
        var now = DateTimeOffset.UtcNow;
        (string Tenant, string Region, int AgeDays)[] seed =
        {
            ("studio-live", "kr", 2), ("studio-live", "jp", 10), ("agency-pro", "eu", 5), ("agency-pro", "us", 40),
            ("team-std", "kr", 12), ("team-std", "kr", 60), ("batch-free", "us", 3), ("batch-free", "kr", 45),
        };
        for (var i = 0; i < seed.Length; i++)
            RecordAsset(seed[i].Tenant, $"asset-{i + 1:00}", Encoding.UTF8.GetBytes($"render-output:{i}:{seed[i].Tenant}"),
                seed[i].Region, seed[i].Tenant != "batch-free", now.AddDays(-seed[i].AgeDays));
        LastAction = "데모 테넌트 4 · 자산 8 시드";
        OnChange?.Invoke();
    }

    /// <summary>데모 위조: WORM 원장 중간 엔트리 타임스탬프 소급 변조.</summary>
    public bool TamperLedger()
    {
        bool done;
        lock (_gate)
        {
            done = _ledger.Count > 1;
            if (done) _ledger[_ledger.Count / 2].At = _ledger[_ledger.Count / 2].At.AddMinutes(-5);
            LastAction = done ? "원장 소급 변조 주입" : "원장 부족";
        }
        OnChange?.Invoke();
        return done;
    }

    /// <summary>데모 위조: C2PA 매니페스트 클레임 해시 비트 반전(재서명 불가).</summary>
    public bool TamperManifest()
    {
        bool done = false;
        lock (_gate)
        {
            foreach (var m in _manifests.Values) { m.ClaimHash[0] ^= 0x01; done = true; break; }
            LastAction = done ? "C2PA 클레임 변조 주입" : "매니페스트 없음";
        }
        OnChange?.Invoke();
        return done;
    }

    /// <summary>데모 위반: 허용 외 리전 + 미서명 자산 기록.</summary>
    public void ViolatePolicy() =>
        RecordAsset("team-std", $"asset-x{Random.Shared.Next(100, 999)}", Encoding.UTF8.GetBytes("offshore"), "cn-north", false);

    /// <summary>상시 감시 타이머 무장/해제.</summary>
    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            if (armed && _timer is null && !_disposed)
                _timer = new System.Threading.Timer(_ => { ScanNow(); }, null, ScanIntervalMs, ScanIntervalMs);
            else if (!armed && _timer is not null) { _timer.Dispose(); _timer = null; }
            LastAction = armed ? $"상시 감시 무장 · {ScanIntervalMs / 1000}초 주기" : "상시 감시 해제";
        }
        OnChange?.Invoke();
    }

    /// <summary>3기둥 스캔 — 중첩 실행은 건너뛰고 직전 결과 반환.</summary>
    public GovAuditScan? ScanNow()
    {
        if (_disposed || Interlocked.Exchange(ref _scanning, 1) == 1) return LastScan;
        try
        {
            GovAuditScan scan;
            lock (_gate) scan = ScanLocked();
            Interlocked.Add(ref _failTotal, scan.Fail);
            LastAction = scan.Compliant
                ? $"스캔 #{scan.Sequence} 준수 · Pass {scan.Pass} · Warn {scan.Warn}"
                : $"스캔 #{scan.Sequence} 위반 {scan.Fail}건 · {scan.Findings[0].Detail}";
            OnChange?.Invoke();
            return scan;
        }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    private GovAuditScan ScanLocked()
    {
        var sw = Stopwatch.StartNew();
        var findings = new List<GovAuditFinding>();
        int pass = 0, warn = 0, fail = 0;
        var now = DateTimeOffset.UtcNow;
        Span<byte> hash = stackalloc byte[32];
        Span<byte> mac = stackalloc byte[32];
        Span<byte> prev = stackalloc byte[32];
        prev.Clear();

        void Add(GovAuditPillar p, GovAuditSeverity s, string subject, string detail, ref int w, ref int f)
        {
            if (s == GovAuditSeverity.Warn) w++; else f++;
            if (findings.Count < MaxFindings) findings.Add(new GovAuditFinding(p, s, subject, detail));
        }

        foreach (var e in _ledger)
        {
            // WORM: 해시 재계산 · HMAC · 체인 연결
            ComputeEntryHash(e, hash);
            HMACSHA256.HashData(_wormKey, hash, mac);
            if (!CryptographicOperations.FixedTimeEquals(hash, e.Hash))
                Add(GovAuditPillar.Worm, GovAuditSeverity.Fail, e.AssetId, $"#{e.Seq} 엔트리 내용 변조(해시 불일치)", ref warn, ref fail);
            else if (!CryptographicOperations.FixedTimeEquals(mac, e.Mac))
                Add(GovAuditPillar.Worm, GovAuditSeverity.Fail, e.AssetId, $"#{e.Seq} WORM 서명 불일치", ref warn, ref fail);
            else if (!CryptographicOperations.FixedTimeEquals(prev, e.PrevHash))
                Add(GovAuditPillar.Worm, GovAuditSeverity.Fail, e.AssetId, $"#{e.Seq} 체인 단절", ref warn, ref fail);
            else pass++;
            e.Hash.CopyTo(prev);

            // 정책: 기본 거부(미등록 테넌트) · 리전 · 보존 기한 · C2PA 요구
            if (!_policies.TryGetValue(e.TenantId, out var policy))
            {
                Add(GovAuditPillar.TenantPolicy, GovAuditSeverity.Fail, e.AssetId, $"미등록 테넌트 {e.TenantId} — 기본 거부", ref warn, ref fail);
                continue;
            }
            var regionOk = false;
            foreach (var r in policy.AllowedRegions)
                if (string.Equals(r, e.Region, StringComparison.OrdinalIgnoreCase)) { regionOk = true; break; }
            if (!regionOk)
                Add(GovAuditPillar.TenantPolicy, GovAuditSeverity.Fail, e.AssetId, $"{e.TenantId} 허용 외 리전 {e.Region}", ref warn, ref fail);
            else if ((now - e.At).TotalDays > policy.RetentionDays)
                Add(GovAuditPillar.TenantPolicy, GovAuditSeverity.Warn, e.AssetId, $"{e.TenantId} 보존 {policy.RetentionDays}일 초과 — 파기 심사", ref warn, ref fail);
            else pass++;

            if (policy.RequireC2pa && !_manifests.ContainsKey(e.AssetId))
                Add(GovAuditPillar.C2pa, GovAuditSeverity.Fail, e.AssetId, $"{e.TenantId} C2PA 필수 — 매니페스트 부재", ref warn, ref fail);
            else if (!policy.RequireC2pa && !_manifests.ContainsKey(e.AssetId))
                Add(GovAuditPillar.C2pa, GovAuditSeverity.Warn, e.AssetId, "미서명 자산(정책상 허용)", ref warn, ref fail);
        }

        foreach (var m in _manifests.Values)
        {
            // C2PA: 매니페스트 서명 + 원장 콘텐츠 해시 바인딩
            SignManifest(m, mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, m.Signature))
            {
                Add(GovAuditPillar.C2pa, GovAuditSeverity.Fail, m.AssetId, "C2PA 매니페스트 서명 위조", ref warn, ref fail);
                continue;
            }
            LedgerEntry? bound = null;
            foreach (var e in _ledger)
                if (string.Equals(e.AssetId, m.AssetId, StringComparison.Ordinal)) { bound = e; break; }
            if (bound is null)
                Add(GovAuditPillar.C2pa, GovAuditSeverity.Fail, m.AssetId, "원장 미기록 매니페스트(고아)", ref warn, ref fail);
            else if (!CryptographicOperations.FixedTimeEquals(bound.ContentHash, m.ClaimHash))
                Add(GovAuditPillar.C2pa, GovAuditSeverity.Fail, m.AssetId, "C2PA 클레임 ↔ WORM 콘텐츠 해시 불일치", ref warn, ref fail);
            else pass++;
        }

        var head = _ledger.Count > 0 ? Convert.ToHexString(_ledger[^1].Hash) : "EMPTY";
        var scan = new GovAuditScan(Interlocked.Increment(ref _scanSeq), now, _policies.Count, _ledger.Count, _manifests.Count,
            pass, warn, fail, head, findings, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
        _lastScan = scan;
        return scan;
    }

    /// <summary>신규 스캔 → 서명 규정 준수 리포트(Markdown 포함) 생성.</summary>
    public GovComplianceReport GenerateReport()
    {
        var scan = ScanNow() ?? LastScan;
        if (scan is null) lock (_gate) scan = ScanLocked();
        var canonical = ReportCanonical(scan);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var signature = Convert.ToHexString(HMACSHA256.HashData(_reportKey, Encoding.UTF8.GetBytes(canonical)));
        var report = new GovComplianceReport($"ZTGA-{scan.Sequence:000000}", DateTimeOffset.UtcNow, scan, canonical, fingerprint, signature, BuildMarkdown(scan, fingerprint));
        lock (_gate) _lastReport = report;
        Interlocked.Increment(ref _reports);
        LastAction = $"리포트 {report.ReportId} · {(scan.Compliant ? "준수" : "위반")} · 지문 {fingerprint[..12]}";
        OnChange?.Invoke();
        return report;
    }

    /// <summary>리포트 검증 — 스캔 집계로 정규화 재구성 후 지문·서명 상수시간 대조.</summary>
    public bool VerifyReport(GovComplianceReport report)
    {
        var canonical = ReportCanonical(report.Scan);
        if (!string.Equals(canonical, report.Canonical, StringComparison.Ordinal)) return false;
        var fp = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var sig = HMACSHA256.HashData(_reportKey, Encoding.UTF8.GetBytes(canonical));
        try
        {
            return CryptographicOperations.FixedTimeEquals(fp, Convert.FromHexString(report.Fingerprint))
                && CryptographicOperations.FixedTimeEquals(sig, Convert.FromHexString(report.Signature));
        }
        catch (FormatException) { return false; }
    }

    private static string ReportCanonical(GovAuditScan s)
    {
        var sb = new StringBuilder(256 + s.Findings.Count * 64);
        sb.Append("ZTGA|v1|seq=").Append(s.Sequence)
          .Append("|at=").Append(s.At.UtcTicks)
          .Append("|t=").Append(s.Tenants)
          .Append("|l=").Append(s.LedgerEntries)
          .Append("|m=").Append(s.Manifests)
          .Append("|p=").Append(s.Pass)
          .Append("|w=").Append(s.Warn)
          .Append("|f=").Append(s.Fail)
          .Append("|n=").Append(s.Findings.Count)
          .Append("|head=").Append(s.LedgerHead).Append('|');
        foreach (var f in s.Findings)
            sb.Append((int)f.Pillar).Append(':').Append((int)f.Severity).Append(':').Append(f.Subject).Append(':').Append(f.Detail).Append(';');
        return sb.ToString();
    }

    private static string BuildMarkdown(GovAuditScan s, string fingerprint)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# Zero-Trust Governance Audit #").Append(s.Sequence.ToString(inv)).Append('\n');
        sb.Append("- 판정: ").Append(s.Compliant ? "준수" : "위반").Append('\n');
        sb.Append("- 시각(UTC): ").Append(s.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\n');
        sb.Append("- 테넌트 ").Append(s.Tenants).Append(" · 원장 ").Append(s.LedgerEntries).Append(" · 매니페스트 ").Append(s.Manifests).Append('\n');
        sb.Append("- 지문(SHA-256): ").Append(fingerprint).Append("\n\n");
        sb.Append("| 기둥 | Pass/Warn/Fail |\n|---|---|\n");
        sb.Append("| 합계 | ").Append(s.Pass).Append(" / ").Append(s.Warn).Append(" / ").Append(s.Fail).Append(" |\n\n");
        foreach (var f in s.Findings)
            sb.Append("- [").Append(f.Severity).Append("] ").Append(f.Pillar).Append(" · ").Append(f.Subject).Append(" — ").Append(f.Detail).Append('\n');
        return sb.ToString();
    }

    private void SignManifest(AssetManifest m, Span<byte> dest)
    {
        Span<byte> buf = stackalloc byte[2 * (MaxField * 3 + 2) + 32];
        var w = WriteField(m.AssetId, buf);
        w += WriteField(m.TenantId, buf[w..]);
        m.ClaimHash.CopyTo(buf[w..]);
        w += 32;
        HMACSHA256.HashData(_c2paKey, buf[..w], dest);
    }

    private static void ComputeEntryHash(LedgerEntry e, Span<byte> dest)
    {
        Span<byte> buf = stackalloc byte[16 + 3 * (MaxField * 3 + 2) + 64];
        BinaryPrimitives.WriteInt64BigEndian(buf, e.Seq);
        BinaryPrimitives.WriteInt64BigEndian(buf[8..], e.At.UtcTicks);
        var w = 16;
        w += WriteField(e.TenantId, buf[w..]);
        w += WriteField(e.AssetId, buf[w..]);
        w += WriteField(e.Region, buf[w..]);
        e.ContentHash.CopyTo(buf[w..]);
        w += 32;
        e.PrevHash.CopyTo(buf[w..]);
        w += 32;
        SHA256.HashData(buf[..w], dest);
    }

    private static int WriteField(string s, Span<byte> dest)
    {
        var n = Encoding.UTF8.GetBytes(s.AsSpan(), dest[2..]);
        BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)n);
        return n + 2;
    }

    private static string Clip(string? s) => string.IsNullOrEmpty(s) ? "-" : s.Length > MaxField ? s[..MaxField] : s;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
