using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Inference;

/// <summary>PQC(ML-DSA/Dilithium 근사) 키 쌍 — 공개키만 HUD/원장 표출, 개인키는 프로세스 메모리 내 서명 전용.</summary>
public sealed record PqcKeyPair(string PublicKeyHex, string Algorithm);

/// <summary>WORM 원장 엔트리(불변 · Append-Only) — 콘텐츠 해시 + PQC 서명 + SHA-256 해시체인.</summary>
public sealed record LedgerEntry(
    long Seq, long TimestampMs, string TenantId, string ContentHashHex, string PqcSignatureHex, string PrevLedgerHash, string LedgerHash);

/// <summary>원장 체인 검증 결과(불변) — FirstMismatchSeq=0 이면 무결.</summary>
public sealed record LedgerVerifyResult(bool Valid, long FirstMismatchSeq, string Reason);

/// <summary>
/// [PromptFlow Studio v8.7.2] 양자 내성(PQC) WORM 원장 서명기(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ <see cref="SignAndAppend"/>: 콘텐츠 SHA-256 하드 바인딩(C2PA 결합) → <see cref="SignPqcStub"/> 로 서명 → SHA-256 해시체인으로 WORM 원장에 Append-Only 봉인.
///   ▸ <see cref="VerifyChain"/>: 체인 전체를 재계산해 최초 변조 지점을 검출(WORM 무결성 감사) · <see cref="VerifyEntry"/> 는 단건 PQC 서명 재검증.
///   ⚠ <see cref="SignPqcStub"/>/<see cref="VerifyPqcStub"/> 는 <b>미결선 스텁</b>(HMAC-SHA512 확산 함수로 서명 결합을 근사) —
///     실 NIST FIPS 204 ML-DSA/Dilithium 네이티브 서명(.NET 9+ <c>System.Security.Cryptography.MLDsa</c>)으로 교체 시
///     byte[] in/out 시그니처만 유지하면 <see cref="LedgerEntry"/>/체인 상위 로직은 무변경 승격(교체 결선점).
/// </summary>
public sealed class QuantumResistantLedgerSigner
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly object _gate = new();
    private readonly List<LedgerEntry> _ledger = new();
    private readonly byte[] _privateKeyStub;
    private long _seq;

    public QuantumResistantLedgerSigner()
    {
        // 부트스트랩 PQC 키 쌍(프로세스 수명 · 스텁은 개인키 시드의 SHA-256 지문을 공개키로 표출).
        _privateKeyStub = RandomNumberGenerator.GetBytes(64);
        var pub = Convert.ToHexString(SHA256.HashData(_privateKeyStub)).ToLowerInvariant();
        Keys = new PqcKeyPair(pub, "ML-DSA-65(stub)");
    }

    public event Action? OnChange;
    /// <summary>서명/체인 검증 실패 보안 경고(HUD 즉시 표출).</summary>
    public event Action<string>? OnSecurityAlert;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public PqcKeyPair Keys { get; }
    public long SignedCount { get; private set; }
    public long VerifiedOk { get; private set; }
    public long TamperCount { get; private set; }
    public string? LastAlert { get; private set; }
    public int EntryCount { get { lock (_gate) return _ledger.Count; } }
    public string ChainHead { get { lock (_gate) return _ledger.Count == 0 ? Genesis : _ledger[^1].LedgerHash; } }
    public IReadOnlyList<LedgerEntry> Entries { get { lock (_gate) return _ledger.ToList(); } }

    /// <summary>콘텐츠를 PQC 서명 + WORM 해시체인으로 원장에 봉인한다(Append-Only).</summary>
    public LedgerEntry SignAndAppend(byte[] contentBytes, string tenantId = "ledger")
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(contentBytes ?? Array.Empty<byte>())).ToLowerInvariant();
        var signature = Convert.ToHexString(SignPqcStub(Encoding.UTF8.GetBytes(contentHash))).ToLowerInvariant();

        LedgerEntry entry;
        lock (_gate)
        {
            var seq = ++_seq;
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var prev = _ledger.Count == 0 ? Genesis : _ledger[^1].LedgerHash;
            var chainHash = ChainHash(seq, ts, tenantId ?? "", contentHash, signature, prev);
            entry = new LedgerEntry(seq, ts, tenantId ?? "", contentHash, signature, prev, chainHash);
            _ledger.Add(entry);
        }
        SignedCount++;
        Notify();
        return entry;
    }

    /// <summary>원장 항목의 PQC 서명을 재계산 대조한다(단건 재검증).</summary>
    public bool VerifyEntry(LedgerEntry entry)
    {
        if (entry is null) { RaiseAlert("원장 항목 없음"); return false; }
        var sigOk = VerifyPqcStub(Encoding.UTF8.GetBytes(entry.ContentHashHex), Convert.FromHexString(entry.PqcSignatureHex));
        if (!sigOk) { RaiseAlert($"PQC 서명 불일치 — seq={entry.Seq} 원장 위변조 의심"); return false; }
        VerifiedOk++; Notify();
        return true;
    }

    /// <summary>전체 체인 재계산 검증 — WORM 무결성 감사. 반환 FirstMismatchSeq=0 이면 무결.</summary>
    public LedgerVerifyResult VerifyChain()
    {
        lock (_gate)
        {
            var prev = Genesis;
            foreach (var e in _ledger)
            {
                var expected = ChainHash(e.Seq, e.TimestampMs, e.TenantId, e.ContentHashHex, e.PqcSignatureHex, prev);
                if (!FixedEquals(expected, e.LedgerHash) || !FixedEquals(prev, e.PrevLedgerHash))
                {
                    RaiseAlert($"WORM 체인 불일치 — seq={e.Seq}");
                    return new LedgerVerifyResult(false, e.Seq, "해시체인 불일치");
                }
                prev = e.LedgerHash;
            }
        }
        VerifiedOk++; Notify();
        return new LedgerVerifyResult(true, 0, "무결");
    }

    private void RaiseAlert(string message)
    {
        TamperCount++; LastAlert = message;
        try { OnSecurityAlert?.Invoke(message); } catch { /* 구독자 예외 격리 */ }
        Notify();
    }

    // ── PQC 서명 스텁(HMAC-SHA512 확산 · Dilithium 서명 자리) ──
    private byte[] SignPqcStub(byte[] message)
    {
        using var h = new HMACSHA512(_privateKeyStub);
        return h.ComputeHash(message);
    }

    private bool VerifyPqcStub(byte[] message, byte[] signature)
        => CryptographicOperations.FixedTimeEquals(SignPqcStub(message), signature);

    private static string ChainHash(long seq, long ts, string tenantId, string contentHash, string sig, string prev)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seq}{ts}{tenantId}{contentHash}{sig}{prev}"))).ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));
}
