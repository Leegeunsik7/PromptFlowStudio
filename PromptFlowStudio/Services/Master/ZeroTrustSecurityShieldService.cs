using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Master;

public enum ZtRole : byte { Viewer = 0, Operator = 1, Admin = 2 }

/// <summary>v16.30.0 인증된 세션(디바이스 바인딩 · 짧은 수명 · 인증 시각 = 스텝업 판단).</summary>
public sealed record ZtSession(string Token, string Subject, string Tenant, ZtRole Role, string Method, DateTimeOffset AuthenticatedAt, DateTimeOffset ExpiresAt, string DeviceHash, IReadOnlyList<string> Groups);

public sealed record ZtDecision(bool Allowed, string Reason, bool StepUpRequired = false);

/// <summary>v16.30.0 불변 감사 항목 — HMAC 체인(prev → mac).</summary>
public sealed record ZtAuditEntry(long Seq, DateTimeOffset At, string Actor, string Action, string Resource, bool Allowed, string Detail, string Prev, string Mac);

public sealed record ZtAuditVerify(bool Ok, long BrokenAt, int Count, string Message);

/// <summary>v16.30.0 OIDC 디스커버리 메타데이터.</summary>
public sealed record OidcMetadata(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);

/// <summary>v16.30.0 OIDC 인가 요청(PKCE · state · nonce).</summary>
public sealed record OidcAuthRequest(string Url, string State, string Nonce, string CodeVerifier, string RedirectUri);

/// <summary>v16.30.0 SSO 신원 클레임(검증 결과).</summary>
public sealed record SsoIdentity(bool Valid, string Subject, string Tenant, ZtRole Role, IReadOnlyList<string> Groups, string Method, string Reason);

/// <summary>v16.30.0 비밀 대여 — 고정(pinned) 버퍼 · Dispose 시 0 덮어쓰기.</summary>
public sealed class SecretLease : IDisposable
{
    private char[]? _buf;
    private readonly int _len;

    internal SecretLease(char[] buf, int len) { _buf = buf; _len = len; }

    public ReadOnlySpan<char> Value => _buf is null ? ReadOnlySpan<char>.Empty : _buf.AsSpan(0, _len);
    public int Length => _len;

    public void Dispose()
    {
        var b = Interlocked.Exchange(ref _buf, null);
        if (b is not null) Array.Clear(b);
    }
}

/// <summary>
/// 메모리 하드닝 키 — 고정 배열에 키 ⊕ 랜덤 패드로 분할 보관(평문 키가 힙에 상주하지 않음) · 사용 시 스택 버퍼에 복원 후 즉시 0.
/// </summary>
internal sealed class MaskedKey : IDisposable
{
    private readonly byte[] _masked = GC.AllocateArray<byte>(32, pinned: true);
    private readonly byte[] _pad = GC.AllocateArray<byte>(32, pinned: true);

    public MaskedKey(ReadOnlySpan<byte> key)
    {
        RandomNumberGenerator.Fill(_pad);
        for (var i = 0; i < 32; i++) _masked[i] = (byte)(key[i] ^ _pad[i]);
    }

    public void Unmask(Span<byte> dst)
    {
        for (var i = 0; i < 32; i++) dst[i] = (byte)(_masked[i] ^ _pad[i]);
    }

    public void Dispose() { Array.Clear(_masked); Array.Clear(_pad); }
}

/// <summary>
/// [PromptFlowStudio] v16.30.0 🛡 제로 트러스트 암호화 보안 &amp; 엔터프라이즈 SSO 쉴드.
///   ▸ 키 계층: KEK(256비트) → 비밀/자산은 AES-256-GCM(AAD = 이름/프레임 바인딩). KEK 보관은 Windows DPAPI-NG(NCryptProtectSecret · "LOCAL=user")
///     → 실패 시 DPAPI(CurrentUser) → 비-Windows 는 <see cref="SecureSecretVault"/> 파생 키 래핑. 메모리에서는 <see cref="MaskedKey"/>(고정 배열 키⊕패드) 로만 상주.
///   ▸ 비밀: <see cref="SetSecret"/>/<see cref="Lease"/>(고정 char[] · Dispose 시 0) · 목록은 이름/메타만 · KEK 회전 시 전체 재암호화.
///   ▸ 자산: <see cref="EncryptFileAsync"/> 1MB 프레임 AES-GCM 스트리밍(ArrayPool · nonce=기본⊕카운터 · AAD=프레임번호|최종플래그 → 절단/재배열 탐지).
///   ▸ 제로 트러스트 인가(<see cref="Authorize"/>): 기본 거부 · 세션 유효/만료 · 디바이스 바인딩 · 역할-행위 매트릭스 · 민감 행위 스텝업(인증 10분 이내) · 전 판정 감사.
///   ▸ SSO: OIDC/OAuth2 인가 코드 + PKCE(S256) · state/nonce · 디스커버리/JWKS(RS256 · kid) · iss/aud/exp/nbf/iat(±2분) 검증 · 루프백 리디렉트 수신.
///     SAML2: XML-DSig(SignedXml · Windows) 서명 검증 + 참조 ID = Assertion ID(XSW 차단) · Issuer/Audience/NotBefore/NotOnOrAfter · DTD 금지.
///   ▸ 불변 감사 트레일: HMAC-SHA256 체인(감사 키 = KEK 와 독립 보호 · 회전 무관) · 추가 전용 JSONL · <see cref="VerifyAudit"/> 변조 위치 탐지 · v3.0 <see cref="EnterpriseSecurityAudit"/> 미러링.
/// </summary>
public sealed class ZeroTrustSecurityShieldService : IDisposable
{
    public const int FrameSize = 1 << 20;
    public const int MaxSecrets = 256;
    public const int MaxAuditInMemory = 5000;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly byte[] FileMagic = "PFSZT1\0\0"u8.ToArray();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _dir;
    private readonly Func<byte[]> _fallbackSeed;
    private readonly EnterpriseSecurityAudit? _legacyAudit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _preferDpapiNg;
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Blob, DateTimeOffset Updated)> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZtSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<ZtAuditEntry> _audit = new();
    private MaskedKey? _kek;
    private MaskedKey? _auditKey;
    private string _lastMac = new('0', 64);
    private long _seq, _allowed, _denied;
    private bool _disposed;

    public ZeroTrustSecurityShieldService(SecureSecretVault vault, EnterpriseSecurityAudit? legacyAudit = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "ZeroTrust"),
               () => vault.ResolveOrCreate("ZT_FALLBACK_WRAP"), legacyAudit, null, true) { }

    public ZeroTrustSecurityShieldService(string dir, Func<byte[]> fallbackSeed, EnterpriseSecurityAudit? legacyAudit, Func<DateTimeOffset>? clock, bool preferDpapiNg)
    {
        _dir = dir;
        _fallbackSeed = fallbackSeed;
        _legacyAudit = legacyAudit;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _preferDpapiNg = preferDpapiNg;
    }

    public event Action? OnChange;
    public event Action<ZtAuditEntry>? OnSecurityAlert;

    /// <summary>SSO 강제 — 켜면 로컬 세션 발급 불가(기본 꺼짐: 로컬 Owner 세션 허용 · 감사됨).</summary>
    public bool EnforceSso { get; set; }
    public string KeyProtection { get; private set; } = "미초기화";
    public bool IsUnlocked => Volatile.Read(ref _kek) is not null;
    public long AllowedCount => Interlocked.Read(ref _allowed);
    public long DeniedCount => Interlocked.Read(ref _denied);
    public int SecretCount { get { lock (_gate) return _secrets.Count; } }
    public int SessionCount { get { lock (_gate) return _sessions.Count; } }
    public string? LastAction { get; private set; }
    public OidcMetadata? Oidc { get; private set; }
    public string? OidcClientId { get; set; }
    public IReadOnlyList<ZtAuditEntry> RecentAudit(int n = 50)
    {
        lock (_gate)
        {
            var start = Math.Max(0, _audit.Count - n);
            var list = _audit.GetRange(start, _audit.Count - start);
            list.Reverse();
            return list;
        }
    }

    private string KekPath => Path.Combine(_dir, "kek.protected");
    private string VaultPath => Path.Combine(_dir, "secrets.vault");
    private string AuditPath => Path.Combine(_dir, "audit.jsonl");
    private string AuditKeyPath => Path.Combine(_dir, "audit.key.protected");

    // ══════════════════════ 키 계층 ══════════════════════

    /// <summary>KEK 로드/생성(멱등) → 비밀 볼트 · 감사 체인 로드.</summary>
    public bool Unlock()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_kek is not null) return true;
            Span<byte> kek = stackalloc byte[32];
            try
            {
                Directory.CreateDirectory(_dir);
                if (File.Exists(KekPath))
                {
                    var raw = Unprotect(File.ReadAllBytes(KekPath), out var how);
                    if (raw is null || raw.Length != 32) { KeyProtection = "KEK 복호 실패"; return false; }
                    raw.CopyTo(kek); CryptographicOperations.ZeroMemory(raw);
                    KeyProtection = how;
                }
                else
                {
                    RandomNumberGenerator.Fill(kek);
                    var blob = Protect(kek.ToArray(), out var how);
                    File.WriteAllBytes(KekPath, blob);
                    KeyProtection = how;
                }
                _kek = new MaskedKey(kek);
                // 감사 키는 KEK 회전과 독립(회전 후에도 과거 체인 검증 가능) — 별도 랜덤 키를 같은 방식으로 보호 보관.
                byte[]? ak = null;
                if (File.Exists(AuditKeyPath)) ak = Unprotect(File.ReadAllBytes(AuditKeyPath), out _);
                if (ak is null || ak.Length != 32)
                {
                    ak = RandomNumberGenerator.GetBytes(32);
                    File.WriteAllBytes(AuditKeyPath, Protect(ak, out _));
                }
                _auditKey = new MaskedKey(ak);
                CryptographicOperations.ZeroMemory(ak);
            }
            catch (Exception ex) { Program.Log("ZeroTrust.Unlock", ex); KeyProtection = "초기화 실패: " + ex.Message; return false; }
            finally { CryptographicOperations.ZeroMemory(kek); }
            LoadVaultLocked();
            LoadAuditLocked();
        }
        Audit("system", "unlock", "kek", true, KeyProtection);
        return true;
    }

    /// <summary>KEK 보호 — DPAPI-NG → DPAPI → 폴백 래핑(헤더 1바이트로 방식 표기).</summary>
    private byte[] Protect(byte[] plain, out string how)
    {
#if WINDOWS
        if (_preferDpapiNg && DpapiNg.TryProtect(plain, out var ng)) { how = "DPAPI-NG (LOCAL=user)"; return Prefix(1, ng); }
        try { how = "DPAPI (CurrentUser)"; return Prefix(2, ProtectedData.Protect(plain, "pfs-zt-kek"u8.ToArray(), DataProtectionScope.CurrentUser)); }
        catch (CryptographicException ex) { Program.Log("ZeroTrust.Dpapi", ex); }
#endif
        how = "AES-GCM 래핑(볼트 파생 키)";
        return Prefix(3, AesGcmSeal(DeriveWrap(), plain, "kek"u8));
    }

    private byte[]? Unprotect(byte[] blob, out string how)
    {
        how = "?";
        if (blob.Length < 2) return null;
        var body = blob.AsSpan(1).ToArray();
        switch (blob[0])
        {
#if WINDOWS
            case 1: how = "DPAPI-NG (LOCAL=user)"; return DpapiNg.TryUnprotect(body, out var p) ? p : null;
            case 2:
                how = "DPAPI (CurrentUser)";
                try { return ProtectedData.Unprotect(body, "pfs-zt-kek"u8.ToArray(), DataProtectionScope.CurrentUser); } catch (CryptographicException) { return null; }
#endif
            case 3: how = "AES-GCM 래핑(볼트 파생 키)"; return AesGcmOpen(DeriveWrap(), body, "kek"u8);
            default: return null;
        }
    }

    private byte[] DeriveWrap() => SHA256.HashData(_fallbackSeed());

    private static byte[] Prefix(byte tag, byte[] body)
    {
        var r = new byte[body.Length + 1];
        r[0] = tag;
        body.CopyTo(r, 1);
        return r;
    }

    /// <summary>AES-256-GCM 봉인 — nonce12 | tag16 | cipher.</summary>
    public static byte[] AesGcmSeal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad)
    {
        var o = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(o.AsSpan(0, 12));
        using var g = new AesGcm(key, 16);
        g.Encrypt(o.AsSpan(0, 12), plain, o.AsSpan(28), o.AsSpan(12, 16), aad);
        return o;
    }

    public static byte[]? AesGcmOpen(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> aad)
    {
        if (blob.Length < 28) return null;
        var p = new byte[blob.Length - 28];
        try
        {
            using var g = new AesGcm(key, 16);
            g.Decrypt(blob[..12], blob[28..], blob.Slice(12, 16), p, aad);
            return p;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(p); return null; }
    }

    /// <summary>KEK 회전 — 새 KEK 생성 → 전 비밀 재암호화 → 감사 체인은 유지(감사 키는 새 KEK 파생 · 회전 항목으로 경계 기록).</summary>
    public bool RotateKek(string? sessionToken)
    {
        if (!Authorize(sessionToken, "kek", "rotate").Allowed) return false;
        lock (_gate)
        {
            if (_kek is null) return false;
            var plain = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Span<byte> old = stackalloc byte[32];
            _kek.Unmask(old);
            try
            {
                foreach (var kv in _secrets)
                {
                    var p = AesGcmOpen(old, kv.Value.Blob, Encoding.UTF8.GetBytes(kv.Key));
                    if (p is null) return false;
                    plain[kv.Key] = p;
                }
            }
            finally { CryptographicOperations.ZeroMemory(old); }
            Span<byte> fresh = stackalloc byte[32];
            RandomNumberGenerator.Fill(fresh);
            try
            {
                File.WriteAllBytes(KekPath, Protect(fresh.ToArray(), out var how));
                KeyProtection = how;
                foreach (var kv in plain)
                {
                    _secrets[kv.Key] = (AesGcmSeal(fresh, kv.Value, Encoding.UTF8.GetBytes(kv.Key)), _clock());
                    CryptographicOperations.ZeroMemory(kv.Value);
                }
                _kek.Dispose();
                _kek = new MaskedKey(fresh);
                SaveVaultLocked();
            }
            finally { CryptographicOperations.ZeroMemory(fresh); }
        }
        Audit(SubjectOf(sessionToken), "rotate", "kek", true, "KEK 회전 · 비밀 " + SecretCount.ToString(CultureInfo.InvariantCulture) + "개 재암호화");
        return true;
    }

    // ══════════════════════ 비밀 ══════════════════════

    public static bool IsSafeName(string? n)
    {
        if (string.IsNullOrEmpty(n) || n.Length > 64) return false;
        foreach (var ch in n) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')) return false;
        return true;
    }

    /// <summary>비밀 저장(인가 secret:write · 값은 UTF-8 → AES-GCM · 평문 즉시 0).</summary>
    public bool SetSecret(string? sessionToken, string name, ReadOnlySpan<char> value)
    {
        if (!IsSafeName(name) || value.Length is 0 or > 8192) return false;
        if (!Authorize(sessionToken, "secret:" + name, "write").Allowed) return false;
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            var n = Encoding.UTF8.GetBytes(value, bytes);
            lock (_gate)
            {
                if (_kek is null || (!_secrets.ContainsKey(name) && _secrets.Count >= MaxSecrets)) return false;
                Span<byte> k = stackalloc byte[32];
                _kek.Unmask(k);
                try { _secrets[name] = (AesGcmSeal(k, bytes.AsSpan(0, n), Encoding.UTF8.GetBytes(name)), _clock()); }
                finally { CryptographicOperations.ZeroMemory(k); }
                SaveVaultLocked();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
        LastAction = "🔐 비밀 저장 · " + name;
        Raise();
        return true;
    }

    /// <summary>비밀 대여(인가 secret:read · 스텝업) — 고정 char[] 에 복호 · 반드시 using.</summary>
    public SecretLease? Lease(string? sessionToken, string name)
    {
        if (!Authorize(sessionToken, "secret:" + name, "read").Allowed) return null;
        byte[]? p = null;
        lock (_gate)
        {
            if (_kek is null || !_secrets.TryGetValue(name, out var s)) return null;
            Span<byte> k = stackalloc byte[32];
            _kek.Unmask(k);
            try { p = AesGcmOpen(k, s.Blob, Encoding.UTF8.GetBytes(name)); }
            finally { CryptographicOperations.ZeroMemory(k); }
        }
        if (p is null) { Audit(SubjectOf(sessionToken), "read", "secret:" + name, false, "복호 실패(변조 의심)", alert: true); return null; }
        try
        {
            var chars = GC.AllocateArray<char>(Encoding.UTF8.GetCharCount(p), pinned: true);
            var len = Encoding.UTF8.GetChars(p, chars);
            return new SecretLease(chars, len);
        }
        finally { CryptographicOperations.ZeroMemory(p); }
    }

    public bool DeleteSecret(string? sessionToken, string name)
    {
        if (!Authorize(sessionToken, "secret:" + name, "delete").Allowed) return false;
        lock (_gate) { if (!_secrets.Remove(name)) return false; SaveVaultLocked(); }
        Raise();
        return true;
    }

    public List<(string Name, DateTimeOffset Updated, int Bytes)> SecretInventory()
    {
        var list = new List<(string, DateTimeOffset, int)>();
        lock (_gate) foreach (var kv in _secrets) list.Add((kv.Key, kv.Value.Updated, kv.Value.Blob.Length - 28));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    private void SaveVaultLocked()
    {
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var kv in _secrets)
                {
                    w.WriteStartObject(kv.Key);
                    w.WriteString("b", Convert.ToBase64String(kv.Value.Blob));
                    w.WriteString("u", kv.Value.Updated.ToString("O", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            var tmp = VaultPath + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, VaultPath, true);
        }
        catch (Exception ex) { Program.Log("ZeroTrust.SaveVault", ex); }
    }

    private void LoadVaultLocked()
    {
        try
        {
            if (!File.Exists(VaultPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(VaultPath));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!IsSafeName(p.Name) || p.Value.ValueKind != JsonValueKind.Object) continue;
                if (!p.Value.TryGetProperty("b", out var b) || b.ValueKind != JsonValueKind.String) continue;
                var u = p.Value.TryGetProperty("u", out var ue) && DateTimeOffset.TryParse(ue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : _clock();
                _secrets[p.Name] = (Convert.FromBase64String(b.GetString()!), u);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException) { Program.Log("ZeroTrust.LoadVault", ex); }
    }

    // ══════════════════════ 자산 스트리밍 암호화 ══════════════════════

    /// <summary>파일 → 암호 파일(헤더: 매직 · 기본 nonce · 프레임 크기). 프레임별 AES-GCM(AAD = 인덱스|최종).</summary>
    public async Task<bool> EncryptFileAsync(string? sessionToken, string input, string output, CancellationToken ct = default)
    {
        if (!Authorize(sessionToken, "asset:" + Path.GetFileName(input), "encrypt").Allowed) return false;
        var key = ArrayPool<byte>.Shared.Rent(32);
        var plain = ArrayPool<byte>.Shared.Rent(FrameSize);
        var cipher = ArrayPool<byte>.Shared.Rent(FrameSize);
        try
        {
            lock (_gate) { if (_kek is null) return false; _kek.Unmask(key.AsSpan(0, 32)); }
            var baseNonce = new byte[12];
            RandomNumberGenerator.Fill(baseNonce);
            await using var src = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var dst = new FileStream(output + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await dst.WriteAsync(FileMagic, ct).ConfigureAwait(false);
            await dst.WriteAsync(baseNonce, ct).ConfigureAwait(false);
            var tag = new byte[16];
            var nonce = new byte[12];
            var aad = new byte[9];
            long idx = 0;
            var total = src.Length;
            long read = 0;
            using var g = new AesGcm(key.AsSpan(0, 32), 16);
            do
            {
                var n = await ReadFullAsync(src, plain, FrameSize, ct).ConfigureAwait(false);
                read += n;
                var final = read >= total;
                FrameNonce(baseNonce, idx, nonce);
                FrameAad(idx, final, aad);
                g.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), tag, aad);
                var len = new byte[4];
                BitConverter.TryWriteBytes(len, n);
                await dst.WriteAsync(len, ct).ConfigureAwait(false);
                await dst.WriteAsync(tag, ct).ConfigureAwait(false);
                await dst.WriteAsync(cipher.AsMemory(0, n), ct).ConfigureAwait(false);
                idx++;
                if (final) break;
            } while (true);
            await dst.FlushAsync(ct).ConfigureAwait(false);
            dst.Close();
            File.Move(output + ".tmp", output, true);
            Audit(SubjectOf(sessionToken), "encrypt", "asset:" + Path.GetFileName(input), true, idx.ToString(CultureInfo.InvariantCulture) + " 프레임");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { Program.Log("ZeroTrust.Encrypt", ex); TryDelete(output + ".tmp"); return false; }
        finally
        {
            ArrayPool<byte>.Shared.Return(key, clearArray: true);
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher, clearArray: true);
        }
    }

    /// <summary>암호 파일 → 평문(프레임 인증 실패 · 절단 · 재배열 시 false · 부분 출력 삭제).</summary>
    public async Task<bool> DecryptFileAsync(string? sessionToken, string input, string output, CancellationToken ct = default)
    {
        if (!Authorize(sessionToken, "asset:" + Path.GetFileName(input), "decrypt").Allowed) return false;
        var key = ArrayPool<byte>.Shared.Rent(32);
        var plain = ArrayPool<byte>.Shared.Rent(FrameSize);
        var cipher = ArrayPool<byte>.Shared.Rent(FrameSize);
        var ok = false;
        try
        {
            lock (_gate) { if (_kek is null) return false; _kek.Unmask(key.AsSpan(0, 32)); }
            await using var src = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var head = new byte[FileMagic.Length + 12];
            if (await ReadFullAsync(src, head, head.Length, ct).ConfigureAwait(false) != head.Length || !head.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic)) return false;
            var baseNonce = head.AsSpan(FileMagic.Length, 12).ToArray();
            await using var dst = new FileStream(output + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            using var g = new AesGcm(key.AsSpan(0, 32), 16);
            var hdr = new byte[20];
            var nonce = new byte[12];
            var aad = new byte[9];
            long idx = 0;
            while (true)
            {
                var h = await ReadFullAsync(src, hdr, 20, ct).ConfigureAwait(false);
                if (h == 0) return false;   // 최종 프레임 없이 끝남 = 절단
                if (h != 20) return false;
                var n = BitConverter.ToInt32(hdr, 0);
                if (n < 0 || n > FrameSize || await ReadFullAsync(src, cipher, n, ct).ConfigureAwait(false) != n) return false;
                var final = src.Position >= src.Length;
                FrameNonce(baseNonce, idx, nonce);
                FrameAad(idx, final, aad);
                g.Decrypt(nonce, cipher.AsSpan(0, n), hdr.AsSpan(4, 16), plain.AsSpan(0, n), aad);
                await dst.WriteAsync(plain.AsMemory(0, n), ct).ConfigureAwait(false);
                idx++;
                if (final) break;
            }
            await dst.FlushAsync(ct).ConfigureAwait(false);
            ok = true;
        }
        catch (CryptographicException) { ok = false; }
        catch (IOException ex) { Program.Log("ZeroTrust.Decrypt", ex); ok = false; }
        finally
        {
            ArrayPool<byte>.Shared.Return(key, clearArray: true);
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher, clearArray: true);
        }
        if (ok) File.Move(output + ".tmp", output, true);
        else TryDelete(output + ".tmp");
        Audit(SubjectOf(sessionToken), "decrypt", "asset:" + Path.GetFileName(input), ok, ok ? "무결성 확인" : "인증 실패/절단/변조", alert: !ok);
        return ok;
    }

    private static void FrameNonce(ReadOnlySpan<byte> baseNonce, long idx, Span<byte> nonce)
    {
        baseNonce.CopyTo(nonce);
        for (var i = 0; i < 8; i++) nonce[4 + i] ^= (byte)(idx >> (8 * i));
    }

    private static void FrameAad(long idx, bool final, Span<byte> aad)
    {
        BitConverter.TryWriteBytes(aad, idx);
        aad[8] = final ? (byte)1 : (byte)0;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        var off = 0;
        while (off < count)
        {
            var r = await s.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (r <= 0) break;
            off += r;
        }
        return off;
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ══════════════════════ 세션 / 인가 ══════════════════════

    /// <summary>현 디바이스 지문(머신명 · 사용자 · OS) SHA-256 앞 16자.</summary>
    public static string DeviceFingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName + "|" + Environment.OSVersion.VersionString)))[..16];

    /// <summary>검증된 SSO 신원 → 세션 발급(디바이스 바인딩).</summary>
    public ZtSession? IssueSession(SsoIdentity id, string? deviceHash = null)
    {
        if (!id.Valid || !IsUnlocked) { Audit(id.Subject.Length > 0 ? id.Subject : "anonymous", "login", "session", false, id.Reason, alert: true); return null; }
        var now = _clock();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var s = new ZtSession(token, id.Subject, id.Tenant, id.Role, id.Method, now, now + SessionLifetime, deviceHash ?? DeviceFingerprint(), id.Groups);
        lock (_gate)
        {
            var now2 = now;
            var stale = new List<string>();
            foreach (var kv in _sessions) if (kv.Value.ExpiresAt <= now2) stale.Add(kv.Key);
            foreach (var k in stale) _sessions.Remove(k);
            _sessions[token] = s;
        }
        Audit(s.Subject, "login", "session", true, s.Method + " · " + s.Role);
        Raise();
        return s;
    }

    /// <summary>로컬 Owner 세션(SSO 미강제 시만 · Windows 로그온 사용자).</summary>
    public ZtSession? IssueLocalSession()
    {
        if (EnforceSso) { Audit(Environment.UserName, "login", "session", false, "SSO 강제 정책 — 로컬 세션 거부", alert: true); return null; }
        return IssueSession(new SsoIdentity(true, Environment.UserName, "local", ZtRole.Admin, Array.Empty<string>(), "local-windows", "로컬"));
    }

    public void Revoke(string? token)
    {
        if (token is null) return;
        ZtSession? s;
        lock (_gate) { _sessions.Remove(token, out s); }
        if (s is not null) Audit(s.Subject, "logout", "session", true, "세션 폐기");
        Raise();
    }

    public ZtSession? SessionOf(string? token)
    {
        if (token is null) return null;
        lock (_gate) return _sessions.TryGetValue(token, out var s) ? s : null;
    }

    /// <summary>역할-행위 매트릭스(기본 거부).</summary>
    public static bool RoleAllows(ZtRole role, string resource, string action)
    {
        var res = resource.AsSpan();
        var colon = res.IndexOf(':');
        var kind = colon < 0 ? res : res[..colon];
        if (kind.SequenceEqual("kek") || (kind.SequenceEqual("audit") && action != "read")) return role == ZtRole.Admin;
        if (kind.SequenceEqual("secret")) return action == "read" ? role >= ZtRole.Operator : role == ZtRole.Admin;
        if (kind.SequenceEqual("asset") || kind.SequenceEqual("pipeline") || kind.SequenceEqual("sync")) return action == "read" ? true : role >= ZtRole.Operator;
        if (kind.SequenceEqual("audit")) return role >= ZtRole.Operator;
        return false;
    }

    /// <summary>민감 행위(스텝업 필요) — 비밀 읽기/쓰기/삭제 · KEK.</summary>
    public static bool IsSensitive(string resource, string action) => resource.StartsWith("secret:", StringComparison.Ordinal) || resource == "kek";

    /// <summary>제로 트러스트 인가 — 모든 판정 감사.</summary>
    public ZtDecision Authorize(string? token, string resource, string action, string? deviceHash = null)
    {
        ZtDecision d;
        var s = SessionOf(token);
        var now = _clock();
        if (!IsUnlocked) d = new(false, "쉴드 잠김(KEK 미로드)");
        else if (s is null) d = new(false, "세션 없음/폐기됨");
        else if (now >= s.ExpiresAt) d = new(false, "세션 만료");
        else if (!string.Equals(s.DeviceHash, deviceHash ?? DeviceFingerprint(), StringComparison.Ordinal)) d = new(false, "디바이스 바인딩 불일치");
        else if (!RoleAllows(s.Role, resource, action)) d = new(false, s.Role + " 역할로 " + resource + ":" + action + " 불가");
        else if (IsSensitive(resource, action) && now - s.AuthenticatedAt > StepUpWindow) d = new(false, "스텝업 재인증 필요", true);
        else d = new(true, "허용");
        Interlocked.Increment(ref d.Allowed ? ref _allowed : ref _denied);
        Audit(s?.Subject ?? "anonymous", action, resource, d.Allowed, d.Reason, alert: !d.Allowed && s is not null && d.Reason.Contains("디바이스", StringComparison.Ordinal));
        return d;
    }

    private string SubjectOf(string? token) => SessionOf(token)?.Subject ?? "anonymous";

    // ══════════════════════ OIDC / OAuth2 ══════════════════════

    /// <summary>PKCE code_verifier(43~128) · S256 challenge.</summary>
    public static (string Verifier, string Challenge) CreatePkce()
    {
        var v = B64Url(RandomNumberGenerator.GetBytes(32));
        return (v, B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(v))));
    }

    public static string B64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(t); } catch (FormatException) { return null; }
    }

    /// <summary>디스커버리 문서 파싱(https 엔드포인트 필수).</summary>
    public static OidcMetadata? ParseDiscovery(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var iss = S("issuer"); var a = S("authorization_endpoint"); var t = S("token_endpoint"); var j = S("jwks_uri");
            if (!IsHttps(iss) || !IsHttps(a) || !IsHttps(t) || !IsHttps(j)) return null;
            return new OidcMetadata(iss!, a!, t!, j!);
        }
        catch (JsonException) { return null; }
    }

    private static bool IsHttps(string? u) => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme == Uri.UriSchemeHttps;

    public async Task<bool> ConfigureOidcAsync(string authority, string clientId, CancellationToken ct = default)
    {
        if (!IsHttps(authority)) return false;
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(10));
            var body = await Http.GetStringAsync(authority.TrimEnd('/') + "/.well-known/openid-configuration", d.Token).ConfigureAwait(false);
            var m = ParseDiscovery(body);
            if (m is null) return false;
            Oidc = m; OidcClientId = clientId;
            await RefreshJwksAsync(ct).ConfigureAwait(false);
            LastAction = "🔑 OIDC 구성 · " + m.Issuer;
            Raise();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Program.Log("ZeroTrust.Oidc", ex); return false; }
    }

    private readonly Dictionary<string, RSAParameters> _jwks = new(StringComparer.Ordinal);

    /// <summary>JWKS(RSA) 파싱 → kid 별 공개키(n/e base64url).</summary>
    public static Dictionary<string, RSAParameters> ParseJwks(string? json)
    {
        var res = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return res;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return res;
            foreach (var k in keys.EnumerateArray())
            {
                if (!k.TryGetProperty("kty", out var kty) || kty.GetString() != "RSA") continue;
                var kid = k.TryGetProperty("kid", out var ki) && ki.ValueKind == JsonValueKind.String ? ki.GetString()! : "";
                var n = k.TryGetProperty("n", out var ne) ? FromB64Url(ne.GetString() ?? "") : null;
                var e = k.TryGetProperty("e", out var ee) ? FromB64Url(ee.GetString() ?? "") : null;
                if (n is null || e is null || n.Length < 256) continue;   // 2048비트 미만 거부
                res[kid] = new RSAParameters { Modulus = n, Exponent = e };
            }
        }
        catch (JsonException) { }
        return res;
    }

    public async Task RefreshJwksAsync(CancellationToken ct = default)
    {
        if (Oidc is null) return;
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(10));
        var keys = ParseJwks(await Http.GetStringAsync(Oidc.JwksUri, d.Token).ConfigureAwait(false));
        lock (_gate) { _jwks.Clear(); foreach (var kv in keys) _jwks[kv.Key] = kv.Value; }
    }

    public void SetJwks(Dictionary<string, RSAParameters> keys, OidcMetadata meta, string clientId)
    {
        lock (_gate) { _jwks.Clear(); foreach (var kv in keys) _jwks[kv.Key] = kv.Value; }
        Oidc = meta; OidcClientId = clientId;
    }

    /// <summary>인가 URL(코드 플로 + PKCE S256 · state · nonce).</summary>
    public OidcAuthRequest? BuildAuthorizationRequest(string redirectUri, string scope = "openid profile email")
    {
        if (Oidc is null || string.IsNullOrWhiteSpace(OidcClientId)) return null;
        var (verifier, challenge) = CreatePkce();
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var nonce = B64Url(RandomNumberGenerator.GetBytes(16));
        var url = Oidc.AuthorizationEndpoint + (Oidc.AuthorizationEndpoint.Contains('?') ? "&" : "?")
            + "response_type=code&client_id=" + Uri.EscapeDataString(OidcClientId) + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&scope=" + Uri.EscapeDataString(scope) + "&state=" + state + "&nonce=" + nonce + "&code_challenge=" + challenge + "&code_challenge_method=S256";
        return new OidcAuthRequest(url, state, nonce, verifier, redirectUri);
    }

    /// <summary>
    /// ID 토큰(RS256) 검증 — 헤더 alg/kid · 서명(JWKS) · iss · aud · exp/nbf/iat(±2분) · nonce. 역할: roles/groups 클레임 → Admin/Operator/Viewer.
    /// </summary>
    public SsoIdentity ValidateIdToken(string? jwt, string? expectedNonce)
    {
        SsoIdentity Fail(string why) => new(false, "", "", ZtRole.Viewer, Array.Empty<string>(), "oidc", why);
        if (Oidc is null || string.IsNullOrWhiteSpace(OidcClientId)) return Fail("OIDC 미구성");
        if (string.IsNullOrEmpty(jwt) || jwt.Length > 16384) return Fail("토큰 없음");
        var parts = jwt.Split('.');
        if (parts.Length != 3) return Fail("JWS 형식 오류");
        var hb = FromB64Url(parts[0]); var pb = FromB64Url(parts[1]); var sig = FromB64Url(parts[2]);
        if (hb is null || pb is null || sig is null) return Fail("base64url 오류");
        try
        {
            using var h = JsonDocument.Parse(hb);
            var alg = h.RootElement.TryGetProperty("alg", out var a) ? a.GetString() : null;
            if (alg != "RS256") return Fail("허용되지 않은 alg(" + (alg ?? "none") + ")");
            var kid = h.RootElement.TryGetProperty("kid", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "";
            RSAParameters key;
            lock (_gate) if (!_jwks.TryGetValue(kid, out key)) return Fail("알 수 없는 kid");
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(key);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return Fail("서명 불일치");
            }
            using var p = JsonDocument.Parse(pb);
            var r = p.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            long L(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : 0;
            if (S("iss") != Oidc.Issuer) return Fail("iss 불일치");
            var audOk = r.TryGetProperty("aud", out var aud) && (aud.ValueKind == JsonValueKind.String ? aud.GetString() == OidcClientId
                : aud.ValueKind == JsonValueKind.Array && ContainsString(aud, OidcClientId!));
            if (!audOk) return Fail("aud 불일치");
            var now = _clock().ToUnixTimeSeconds();
            var skew = (long)ClockSkew.TotalSeconds;
            var exp = L("exp");
            if (exp == 0 || now > exp + skew) return Fail("토큰 만료");
            if (L("nbf") is var nbf && nbf > 0 && now + skew < nbf) return Fail("아직 유효하지 않음(nbf)");
            if (L("iat") is var iat && iat > now + skew) return Fail("iat 미래");
            if (expectedNonce is not null && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(S("nonce") ?? ""), Encoding.UTF8.GetBytes(expectedNonce))) return Fail("nonce 불일치(재전송 의심)");
            var sub = S("sub");
            if (string.IsNullOrEmpty(sub)) return Fail("sub 없음");
            var groups = new List<string>();
            foreach (var claim in new[] { "roles", "groups" })
                if (r.TryGetProperty(claim, out var g) && g.ValueKind == JsonValueKind.Array)
                    foreach (var x in g.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && groups.Count < 64) groups.Add(x.GetString()!);
            return new SsoIdentity(true, S("email") ?? sub, S("tid") ?? S("tenant") ?? "default", RoleFromGroups(groups), groups, "oidc", "OIDC 검증 통과");
        }
        catch (JsonException) { return Fail("JSON 오류"); }
        catch (CryptographicException) { return Fail("키 오류"); }
    }

    private static bool ContainsString(JsonElement arr, string v)
    {
        foreach (var x in arr.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && x.GetString() == v) return true;
        return false;
    }

    /// <summary>그룹/역할 → ZtRole(대소문자 무시 · admin/owner → Admin · editor/operator/producer → Operator).</summary>
    public static ZtRole RoleFromGroups(IReadOnlyList<string> groups)
    {
        var role = ZtRole.Viewer;
        foreach (var g in groups)
        {
            if (g.Contains("admin", StringComparison.OrdinalIgnoreCase) || g.Contains("owner", StringComparison.OrdinalIgnoreCase)) return ZtRole.Admin;
            if (g.Contains("editor", StringComparison.OrdinalIgnoreCase) || g.Contains("operator", StringComparison.OrdinalIgnoreCase) || g.Contains("producer", StringComparison.OrdinalIgnoreCase)) role = ZtRole.Operator;
        }
        return role;
    }

    /// <summary>
    /// 브라우저 로그인(데스크톱) — 루프백 리디렉트(127.0.0.1 임의 포트) 수신 → state 대조 → 토큰 교환(PKCE) → ID 토큰 검증 → 세션.
    /// </summary>
    public async Task<ZtSession?> LoginInteractiveAsync(CancellationToken ct = default)
    {
        var port = FreePort();
        var redirect = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/callback/";
        var req = BuildAuthorizationRequest(redirect);
        if (req is null) return null;
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        try { Process.Start(new ProcessStartInfo(req.Url) { UseShellExecute = true }); }
        catch (Exception ex) { Program.Log("ZeroTrust.Browser", ex); return null; }
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromMinutes(5));
        HttpListenerContext ctx;
        using (d.Token.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } }))
        {
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return null; }
        }
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
        var page = "<html><body style='font-family:sans-serif'>PromptFlow Studio 로그인이 처리되었습니다. 창을 닫아도 됩니다.</body></html>"u8.ToArray();
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);
        ctx.Response.Close();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(q["state"] ?? ""), Encoding.UTF8.GetBytes(req.State))) { Audit("anonymous", "login", "oidc", false, "state 불일치(CSRF 의심)", alert: true); return null; }
        var code = q["code"];
        if (string.IsNullOrEmpty(code)) return null;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = OidcClientId!, ["code_verifier"] = req.CodeVerifier,
        });
        using var resp = await Http.PostAsync(Oidc!.TokenEndpoint, form, d.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { Audit("anonymous", "login", "oidc", false, "토큰 교환 HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)); return null; }
        string? idToken;
        try { using var doc = JsonDocument.Parse(body); idToken = doc.RootElement.TryGetProperty("id_token", out var t) ? t.GetString() : null; }
        catch (JsonException) { return null; }
        return IssueSession(ValidateIdToken(idToken, req.Nonce));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ══════════════════════ SAML2 ══════════════════════

    /// <summary>
    /// SAML2 Response/Assertion 검증 — DTD 금지 파싱 · XML-DSig(IdP 인증서) · 서명 참조 = Assertion ID(XSW 차단) · Issuer · Audience · 유효 기간(±2분).
    /// </summary>
    public SsoIdentity ValidateSaml(string? xml, System.Security.Cryptography.X509Certificates.X509Certificate2 idpCert, string expectedIssuer, string expectedAudience)
    {
        SsoIdentity Fail(string why) => new(false, "", "", ZtRole.Viewer, Array.Empty<string>(), "saml2", why);
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 256 * 1024) return Fail("어설션 없음");
#if WINDOWS
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (var sr = new StringReader(xml))
            using (var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(xr);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
            ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
            var assertions = doc.SelectNodes("//saml:Assertion", ns);
            if (assertions is null || assertions.Count != 1) return Fail("Assertion 은 정확히 1개여야 합니다(XSW 차단)");
            var assertion = (XmlElement)assertions[0]!;
            var aid = assertion.GetAttribute("ID");
            var sigEl = assertion.SelectSingleNode("ds:Signature", ns) as XmlElement;
            if (sigEl is null || string.IsNullOrEmpty(aid)) return Fail("Assertion 서명 없음");
            var signed = new System.Security.Cryptography.Xml.SignedXml(assertion);
            signed.LoadXml(sigEl);
            if (signed.SignedInfo is null || signed.SignedInfo.References.Count != 1 || ((System.Security.Cryptography.Xml.Reference)signed.SignedInfo.References[0]!).Uri != "#" + aid)
                return Fail("서명 참조가 Assertion ID 와 다릅니다(XSW 의심)");
            if (!signed.CheckSignature(idpCert, true)) return Fail("XML 서명 불일치");
            var issuer = assertion.SelectSingleNode("saml:Issuer", ns)?.InnerText?.Trim();
            if (issuer != expectedIssuer) return Fail("Issuer 불일치");
            var cond = assertion.SelectSingleNode("saml:Conditions", ns) as XmlElement;
            var now = _clock();
            if (cond is null) return Fail("Conditions 없음");
            if (DateTimeOffset.TryParse(cond.GetAttribute("NotBefore"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var nb) && now + ClockSkew < nb) return Fail("NotBefore 이전");
            if (!DateTimeOffset.TryParse(cond.GetAttribute("NotOnOrAfter"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var na) || now - ClockSkew >= na) return Fail("만료(NotOnOrAfter)");
            var audOk = false;
            foreach (XmlNode a in cond.SelectNodes("saml:AudienceRestriction/saml:Audience", ns)!) if (a.InnerText.Trim() == expectedAudience) audOk = true;
            if (!audOk) return Fail("Audience 불일치");
            var nameId = assertion.SelectSingleNode("saml:Subject/saml:NameID", ns)?.InnerText?.Trim();
            if (string.IsNullOrEmpty(nameId)) return Fail("NameID 없음");
            var groups = new List<string>();
            string tenant = "default";
            foreach (XmlElement attr in assertion.SelectNodes("saml:AttributeStatement/saml:Attribute", ns)!)
            {
                var name = attr.GetAttribute("Name");
                foreach (XmlNode v in attr.SelectNodes("saml:AttributeValue", ns)!)
                {
                    var val = v.InnerText.Trim();
                    if (name.EndsWith("role", StringComparison.OrdinalIgnoreCase) || name.EndsWith("groups", StringComparison.OrdinalIgnoreCase)) { if (groups.Count < 64) groups.Add(val); }
                    else if (name.EndsWith("tenant", StringComparison.OrdinalIgnoreCase)) tenant = val;
                }
            }
            return new SsoIdentity(true, nameId, tenant, RoleFromGroups(groups), groups, "saml2", "SAML2 서명·조건 검증 통과");
        }
        catch (XmlException ex) { return Fail("XML 오류: " + ex.Message); }
        catch (CryptographicException ex) { return Fail("서명 처리 오류: " + ex.Message); }
#else
        return Fail("SAML2 XML-DSig 검증은 Windows 데스크톱 전용");
#endif
    }

    // ══════════════════════ 불변 감사 ══════════════════════

    /// <summary>감사 MAC — HMAC-SHA256(감사 키, seq|ts|actor|action|resource|allowed|detail|prev).</summary>
    private static string ComputeMac(ReadOnlySpan<byte> key, long seq, DateTimeOffset at, string actor, string action, string resource, bool allowed, string detail, string prev)
    {
        var s = seq.ToString(CultureInfo.InvariantCulture) + "|" + at.ToString("O", CultureInfo.InvariantCulture) + "|" + actor + "|" + action + "|" + resource + "|" + (allowed ? "1" : "0") + "|" + detail + "|" + prev;
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(s)));
    }

    private void Audit(string actor, string action, string resource, bool allowed, string detail, bool alert = false)
    {
        ZtAuditEntry e;
        lock (_gate)
        {
            if (_auditKey is null) return;
            Span<byte> k = stackalloc byte[32];
            _auditKey.Unmask(k);
            try
            {
                var seq = ++_seq;
                var at = _clock();
                var mac = ComputeMac(k, seq, at, actor, action, resource, allowed, detail, _lastMac);
                e = new ZtAuditEntry(seq, at, actor, action, resource, allowed, detail, _lastMac, mac);
                _lastMac = mac;
            }
            finally { CryptographicOperations.ZeroMemory(k); }
            _audit.Add(e);
            if (_audit.Count > MaxAuditInMemory) _audit.RemoveRange(0, _audit.Count - MaxAuditInMemory);
            try { File.AppendAllText(AuditPath, SerializeAudit(e) + "\n"); } catch (IOException ex) { Program.Log("ZeroTrust.Audit", ex); }
        }
        try { _legacyAudit?.Log("ZeroTrust", actor, action + (allowed ? " ✓ " : " ✗ ") + resource, detail); } catch (Exception ex) { Program.Log("ZeroTrust.LegacyAudit", ex); }
        if (alert)
        {
            var h = OnSecurityAlert;
            if (h is not null)
                foreach (var d in h.GetInvocationList())
                {
                    try { ((Action<ZtAuditEntry>)d)(e); } catch (Exception ex) { Program.Log("ZeroTrust.Alert", ex); }
                }
        }
        LastAction = (allowed ? "✅ " : "⛔ ") + action + " · " + resource;
        Raise();
    }

    private static string SerializeAudit(ZtAuditEntry e)
    {
        using var ms = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", e.Seq); w.WriteString("at", e.At.ToString("O", CultureInfo.InvariantCulture)); w.WriteString("actor", e.Actor);
            w.WriteString("action", e.Action); w.WriteString("res", e.Resource); w.WriteBoolean("ok", e.Allowed); w.WriteString("detail", e.Detail);
            w.WriteString("prev", e.Prev); w.WriteString("mac", e.Mac);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static ZtAuditEntry? ParseAudit(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement;
            return new ZtAuditEntry(r.GetProperty("seq").GetInt64(), DateTimeOffset.Parse(r.GetProperty("at").GetString()!, CultureInfo.InvariantCulture),
                r.GetProperty("actor").GetString()!, r.GetProperty("action").GetString()!, r.GetProperty("res").GetString()!, r.GetProperty("ok").GetBoolean(),
                r.GetProperty("detail").GetString()!, r.GetProperty("prev").GetString()!, r.GetProperty("mac").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    private void LoadAuditLocked()
    {
        try
        {
            if (!File.Exists(AuditPath)) return;
            foreach (var line in File.ReadLines(AuditPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var e = ParseAudit(line);
                if (e is null) continue;
                _audit.Add(e);
                if (_audit.Count > MaxAuditInMemory) _audit.RemoveAt(0);
                _seq = e.Seq; _lastMac = e.Mac;
            }
        }
        catch (IOException ex) { Program.Log("ZeroTrust.LoadAudit", ex); }
    }

    /// <summary>디스크 감사 트레일 전체 검증 — seq 연속 · prev 연결 · MAC 재계산(감사 키). 첫 불일치 위치 반환.</summary>
    public ZtAuditVerify VerifyAudit()
    {
        lock (_gate)
        {
            if (_auditKey is null) return new(false, 0, 0, "쉴드 잠김");
            if (!File.Exists(AuditPath)) return new(true, 0, 0, "감사 기록 없음");
            Span<byte> k = stackalloc byte[32];
            _auditKey.Unmask(k);
            try
            {
                var prev = new string('0', 64);
                long expect = 0; var n = 0;
                foreach (var line in File.ReadLines(AuditPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var e = ParseAudit(line);
                    expect++;
                    if (e is null) return new(false, expect, n, "⛔ 손상된 항목 #" + expect.ToString(CultureInfo.InvariantCulture));
                    if (e.Seq != expect || e.Prev != prev) return new(false, e.Seq, n, "⛔ 체인 단절 #" + e.Seq.ToString(CultureInfo.InvariantCulture) + "(삭제/삽입/재배열)");
                    var mac = ComputeMac(k, e.Seq, e.At, e.Actor, e.Action, e.Resource, e.Allowed, e.Detail, e.Prev);
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(e.Mac))) return new(false, e.Seq, n, "⛔ MAC 불일치 #" + e.Seq.ToString(CultureInfo.InvariantCulture) + "(변조)");
                    prev = e.Mac; n++;
                }
                return new(true, 0, n, "✅ 감사 트레일 무결성 확인 · " + n.ToString(CultureInfo.InvariantCulture) + "건");
            }
            catch (IOException ex) { return new(false, 0, 0, "읽기 실패: " + ex.Message); }
            finally { CryptographicOperations.ZeroMemory(k); }
        }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("ZeroTrust.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _kek?.Dispose(); _kek = null;
            _auditKey?.Dispose(); _auditKey = null;
            _sessions.Clear();
            foreach (var kv in _secrets) Array.Clear(kv.Value.Blob);
            _secrets.Clear();
        }
        OnChange = null;
        OnSecurityAlert = null;
    }

#if WINDOWS
    /// <summary>Windows DPAPI-NG(ncrypt.dll) — 보호 디스크립터 "LOCAL=user".</summary>
    private static class DpapiNg
    {
        private const uint NCRYPT_SILENT_FLAG = 0x40;

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptCreateProtectionDescriptor(string pwszDescriptorString, uint dwFlags, out IntPtr phDescriptor);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptCloseProtectionDescriptor(IntPtr hDescriptor);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptProtectSecret(IntPtr hDescriptor, uint dwFlags, byte[] pbData, uint cbData, IntPtr pMemPara, IntPtr hWnd, out IntPtr ppbProtectedBlob, out uint pcbProtectedBlob);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptUnprotectSecret(IntPtr phDescriptor, uint dwFlags, byte[] pbProtectedBlob, uint cbProtectedBlob, IntPtr pMemPara, IntPtr hWnd, out IntPtr ppbData, out uint pcbData);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static bool TryProtect(byte[] plain, out byte[] blob)
        {
            blob = Array.Empty<byte>();
            try
            {
                if (NCryptCreateProtectionDescriptor("LOCAL=user", 0, out var desc) != 0) return false;
                try
                {
                    if (NCryptProtectSecret(desc, NCRYPT_SILENT_FLAG, plain, (uint)plain.Length, IntPtr.Zero, IntPtr.Zero, out var p, out var n) != 0) return false;
                    try { blob = new byte[n]; Marshal.Copy(p, blob, 0, (int)n); return true; }
                    finally { LocalFree(p); }
                }
                finally { NCryptCloseProtectionDescriptor(desc); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }

        public static bool TryUnprotect(byte[] blob, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            try
            {
                if (NCryptUnprotectSecret(IntPtr.Zero, NCRYPT_SILENT_FLAG, blob, (uint)blob.Length, IntPtr.Zero, IntPtr.Zero, out var p, out var n) != 0) return false;
                try
                {
                    plain = new byte[n];
                    Marshal.Copy(p, plain, 0, (int)n);
                    Marshal.Copy(new byte[n], 0, p, (int)n);   // 네이티브 평문 버퍼 0 덮어쓰기
                    return true;
                }
                finally { LocalFree(p); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
#endif
}
