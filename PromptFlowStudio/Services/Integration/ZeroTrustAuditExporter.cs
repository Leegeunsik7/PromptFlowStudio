using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Integration;

/// <summary>
/// WORM 감사 항목(불변 · Append-Only) — 순번·시각·소스·범주·상세 + 해시 체인(이전 해시 → 현재 해시).
///   ▸ <see cref="Hash"/> = SHA-256(Seq · TimestampMs · Source · Category · Detail · <see cref="PrevHash"/>) → 한 항목이라도 변조되면 이후 체인 전부 불일치.
/// </summary>
public sealed record AuditEntry(
    long Seq, long TimestampMs, string Source, string Category, string Detail, string PrevHash, string Hash);

/// <summary>서명된 WORM 감사 보고서(불변) — 항목 전량 + 체인 말단 해시 + C2PA JUMBF 봉인.</summary>
public sealed record AuditReport(
    long GeneratedMs, int EntryCount, string ChainHead, IReadOnlyList<AuditEntry> Entries, string C2paJumbfBase64);

/// <summary>
/// [PromptFlow Studio v8.4.3] 제로 트러스트 WORM 감사 로그 내보내기(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="TenantPolicyEngine.OnPolicyViolation"/>·<see cref="C2PaWatermarkEngine.OnSecurityAlert"/> 를 구독해 위반/변조 이벤트를 <b>Append-Only</b> 로 적재.
///   ▸ 각 항목은 SHA-256 해시 체인으로 봉인(WORM 근사) → 사후 변조 시 <see cref="VerifyChain"/> 이 최초 불일치 지점을 검출.
///   ▸ <see cref="BuildReport"/> 는 전체 체인을 B2B 감사 보고서로 패키징하고 <see cref="C2PaWatermarkEngine.SignManifest"/> 로 최종 서명(프로비넌스 바인딩).
///   ⚠ <see cref="OnChange"/> 는 이벤트 발신(임의) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class ZeroTrustAuditExporter : IDisposable
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly TenantPolicyEngine? _policy;
    private readonly C2PaWatermarkEngine? _c2pa;
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = new();
    private long _seq;

    public ZeroTrustAuditExporter(TenantPolicyEngine? policy = null, C2PaWatermarkEngine? c2pa = null)
    {
        _policy = policy;
        _c2pa = c2pa;
        if (_policy is not null) _policy.OnPolicyViolation += OnViolation;       // 정책 위반 → 감사 적재
        if (_c2pa is not null) _c2pa.OnSecurityAlert += OnTamperAlert;           // C2PA 변조 경고 → 감사 적재
    }

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public int EntryCount { get { lock (_gate) return _entries.Count; } }
    public string ChainHead { get { lock (_gate) return _entries.Count == 0 ? Genesis : _entries[^1].Hash; } }
    public IReadOnlyList<AuditEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    // ── 이벤트 수신 → Append-Only 적재 ──
    private void OnViolation(PolicyDecision d)
        => Append("TenantPolicy", d.ViolationCategory ?? "policy", $"[{d.Code}] {d.TenantId} · {d.Reason}");

    private void OnTamperAlert(string message)
        => Append("C2PA", "tamper-alert", message);

    /// <summary>감사 항목 추가(해시 체인 연결). 외부에서 커스텀 이벤트도 봉인 가능.</summary>
    public AuditEntry Append(string source, string category, string detail)
    {
        AuditEntry entry;
        lock (_gate)
        {
            var seq = ++_seq;
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var prev = _entries.Count == 0 ? Genesis : _entries[^1].Hash;
            var hash = ChainHash(seq, ts, source ?? "", category ?? "", detail ?? "", prev);
            entry = new AuditEntry(seq, ts, source ?? "", category ?? "", detail ?? "", prev, hash);
            _entries.Add(entry);
        }
        Notify();
        return entry;
    }

    /// <summary>
    /// 체인 무결성 검증 — 순번·PrevHash 연속성과 각 항목 해시를 재계산해 대조. 반환=최초 불일치 순번(0=무결).
    /// </summary>
    public long VerifyChain()
    {
        lock (_gate)
        {
            var prev = Genesis;
            foreach (var e in _entries)
            {
                var expected = ChainHash(e.Seq, e.TimestampMs, e.Source, e.Category, e.Detail, prev);
                if (!FixedEquals(expected, e.Hash) || !FixedEquals(prev, e.PrevHash))
                    return e.Seq;   // 최초 변조 지점
                prev = e.Hash;
            }
        }
        return 0;
    }

    /// <summary>WORM 감사 보고서 패키징 — 체인 스냅샷 + C2PA 서명 봉인.</summary>
    public AuditReport BuildReport(string tenantId = "audit")
    {
        List<AuditEntry> snap;
        string head;
        lock (_gate) { snap = _entries.ToList(); head = snap.Count == 0 ? Genesis : snap[^1].Hash; }

        var jumbf = "";
        try
        {
            if (_c2pa is not null)
            {
                var claim = new C2paClaim(tenantId, $"audit-report:{snap.Count}:{head}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snap, PromptFlowJsonContext.Default.ZeroTrustAuditEntryList));
                jumbf = _c2pa.SignManifest(claim, body).JumbfBase64;   // 보고서 콘텐츠에 하드 바인딩 서명
            }
        }
        catch (Exception ex) { Program.Log("ZeroTrustAudit.Sign", ex); }

        return new AuditReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), snap.Count, head, snap, jumbf);
    }

    /// <summary>감사 보고서를 JSON 문자열로 내보내기(B2B 전달용).</summary>
    public string ExportJson(string tenantId = "audit") => JsonSerializer.Serialize(BuildReport(tenantId), PromptFlowJsonContext.Default.ZeroTrustAuditReport);

    // ── 해시 체인(BCL SHA-256) ──
    private static string ChainHash(long seq, long ts, string source, string category, string detail, string prev)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seq}{ts}{source}{category}{detail}{prev}"))).ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

    public void Dispose()
    {
        try { if (_policy is not null) _policy.OnPolicyViolation -= OnViolation; } catch { /* 무시 */ }
        try { if (_c2pa is not null) _c2pa.OnSecurityAlert -= OnTamperAlert; } catch { /* 무시 */ }
    }
}
