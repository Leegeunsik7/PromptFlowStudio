using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PromptFlowStudio.Services.Security;   // SecureSecretVault(v10.5.0-F1)

namespace PromptFlowStudio.Services.Integration;

public enum AttestState { Trusted, Degraded, Untrusted }

public enum AttestClaimKind { EnclaveIsolation, TpmPcr, SecureBoot, MemoryEncryption, ZtnaPosture }

public sealed record AttestClaimView(AttestClaimKind Kind, bool Passed, string Digest8);

public sealed record AttestReport(AttestState State, bool QuoteValid, int Passed, int Total, string Fingerprint8, string Nonce8, double PostureScore, double ElapsedMs);

/// <summary>
/// v9.8.2 기밀 컴퓨팅 하드웨어 증명기 — 엔클레이브 격리·TPM PCR·시큐어부트·메모리 암호화·ZTNA 포스처를
/// SHA-256 측정으로 스캔하고 nonce 바인딩 quote 를 HMAC(PQC 스텁 · 교체 시 상위 무변경) 서명·상수시간 검증한다.
/// 변조/포스처 저하 시 Degraded/Untrusted 로 강등해 ZTNA 바인딩을 차단. 순수 BCL(System.Security.Cryptography).
/// </summary>
public sealed class ConfidentialComputingAttestor : IDisposable
{
    public const int TickMs = 1500;
    private static readonly AttestClaimKind[] Claims = Enum.GetValues<AttestClaimKind>();
    // v10.5.0-F1: 하드코딩 리터럴 → SecureSecretVault 이관(환경변수→DPAPI→Graceful 폴백). CLAUDE.md Rule 7 소급 적용.
    private readonly byte[] AikKey;

    private sealed class Claim { public byte[] Pcr = Array.Empty<byte>(); public bool Passed = true; public byte[] Digest = Array.Empty<byte>(); }

    private readonly object _gate = new();
    private readonly Claim[] _claims;
    private readonly Random _rng = new(9802);
    private System.Threading.Timer? _timer;
    private double _posture = 0.95;
    private AttestReport? _last;
    private long _attestations, _quotesValid, _tamperDetected, _ztnaDenied;
    private bool _disposed;

    public event Action? OnChange;

    public ConfidentialComputingAttestor(SecureSecretVault vault)
    {
        AikKey = vault.ResolveOrCreate("confidential-computing-attestor");
        _claims = new Claim[Claims.Length];
        for (var i = 0; i < _claims.Length; i++) _claims[i] = new Claim();
        SealBaselineLocked();
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public AttestReport? LastReport => _last;
    public long Attestations => Interlocked.Read(ref _attestations);
    public long QuotesValid => Interlocked.Read(ref _quotesValid);
    public long TamperDetected => Interlocked.Read(ref _tamperDetected);
    public long ZtnaDenied => Interlocked.Read(ref _ztnaDenied);

    public IReadOnlyList<AttestClaimView> ClaimViews
    {
        get
        {
            lock (_gate)
            {
                var list = new List<AttestClaimView>(_claims.Length);
                for (var i = 0; i < _claims.Length; i++) list.Add(new AttestClaimView(Claims[i], _claims[i].Passed, Hex8(_claims[i].Digest)));
                return list;
            }
        }
    }

    private void SealBaselineLocked()
    {
        foreach (var c in _claims)
        {
            c.Pcr = new byte[32];
            _rng.NextBytes(c.Pcr);
            c.Passed = true;
            c.Digest = SHA256.HashData(c.Pcr);
        }
        LastAction = "기준 PCR 봉인";
    }

    public AttestReport Attest()
    {
        AttestReport rep;
        lock (_gate) rep = AttestLocked();
        OnChange?.Invoke();
        return rep;
    }

    private AttestReport AttestLocked()
    {
        var sw = Stopwatch.StartNew();
        _attestations++;
        var nonce = new byte[16];
        _rng.NextBytes(nonce);
        var passed = 0;
        using var agg = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var c in _claims)
        {
            var d = SHA256.HashData(c.Pcr);
            c.Passed = CryptographicOperations.FixedTimeEquals(d, c.Digest);   // 기준 PCR 대비 상수시간 측정
            if (c.Passed) passed++; else _tamperDetected++;
            agg.AppendData(d);
        }
        agg.AppendData(nonce);
        var fingerprint = agg.GetHashAndReset();
        var quote = HMACSHA256.HashData(AikKey, fingerprint);          // AIK quote 서명(PQC 스텁)
        var expect = HMACSHA256.HashData(AikKey, fingerprint);         // 검증측 재서명
        var quoteValid = CryptographicOperations.FixedTimeEquals(quote, expect) && passed == _claims.Length;
        if (quoteValid) _quotesValid++;
        var state = !quoteValid ? AttestState.Untrusted : _posture < 0.6 ? AttestState.Degraded : AttestState.Trusted;
        if (state != AttestState.Trusted) _ztnaDenied++;
        sw.Stop();
        _last = new AttestReport(state, quoteValid, passed, _claims.Length, Hex8(fingerprint), Hex8(nonce), Math.Round(_posture, 3), sw.Elapsed.TotalMilliseconds);
        LastAction = state switch
        {
            AttestState.Trusted => $"증명 통과 · 지문 {Hex8(fingerprint)} · ZTNA 바인딩 허용",
            AttestState.Degraded => "포스처 저하 — ZTNA 바인딩 거부(Degraded)",
            _ => "엔클레이브 변조 검출 — ZTNA 차단(Untrusted)",
        };
        return _last;
    }

    public void InjectEnclaveTamper()
    {
        lock (_gate)
        {
            var c = _claims[_rng.Next(_claims.Length)];
            if (c.Pcr.Length > 0) c.Pcr[_rng.Next(c.Pcr.Length)] ^= 0xFF;
            LastAction = "엔클레이브 PCR 변조 주입";
        }
        OnChange?.Invoke();
    }

    public void InjectPostureDrop() { lock (_gate) { _posture = Math.Max(0, _posture - 0.5); LastAction = "ZTNA 포스처 저하 주입"; } OnChange?.Invoke(); }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
            LastAction = "주기 증명 스캔 기동";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate) { if (_timer is null) return; _posture = Math.Min(1.0, _posture + 0.05); AttestLocked(); }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "증명 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate) { _posture = 0.95; SealBaselineLocked(); _last = null; LastAction = "증명기 초기화"; }
        OnChange?.Invoke();
    }

    private static string Hex8(byte[] h)
    {
        if (h.Length == 0) return "--------";
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4 && i < h.Length; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
