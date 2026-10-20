using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.Sovereign.Pqc;

namespace PromptFlowStudio.Services.Sovereign;

/// <summary>금고 키 세트(공개키 · 래핑된 개인키 · 키 ID).</summary>
public sealed class VaultKeySet
{
    internal VaultKeySet(string id, byte[] kemEk, byte[] kemDkWrapped, byte[] ecdhPub, byte[] ecdhPrivWrapped, byte[] dsaPk, byte[] dsaSkWrapped, byte[] ecdsaPub, byte[] ecdsaPrivWrapped, DateTimeOffset created)
    { Id = id; KemEk = kemEk; KemDkWrapped = kemDkWrapped; EcdhPublic = ecdhPub; EcdhPrivateWrapped = ecdhPrivWrapped; DsaPk = dsaPk; DsaSkWrapped = dsaSkWrapped; EcdsaPublic = ecdsaPub; EcdsaPrivateWrapped = ecdsaPrivWrapped; CreatedAt = created; }

    public string Id { get; }
    public byte[] KemEk { get; }
    public byte[] EcdhPublic { get; }
    public byte[] DsaPk { get; }
    public byte[] EcdsaPublic { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool Retired { get; internal set; }
    internal byte[] KemDkWrapped { get; }
    internal byte[] EcdhPrivateWrapped { get; }
    internal byte[] DsaSkWrapped { get; }
    internal byte[] EcdsaPrivateWrapped { get; }
}

/// <summary>봉인 메타데이터(서명 대상 · 평문 지문 포함).</summary>
public sealed record SealedAssetInfo(string KeyId, string AssetId, string Owner, string Kind, long PlainBytes, string PlainSha3, DateTimeOffset SealedAt);

/// <summary>하이브리드 서명 원장 항목(집계 필드 포함 canonical).</summary>
public sealed record PqLedgerEntry(long Seq, string KeyId, string EntrySha3, string PrevHash, long TotalEntries, byte[] DsaSignature, byte[] EcdsaSignature, string Hash);

/// <summary>
/// [PromptFlow Studio v16.53.0] 🔐 양자 내성(PQC) 미디어 금고 &amp; 서명 엔진.
///   ▸ 하이브리드 KEM: ML-KEM-768(FIPS 203) + ECDH P-384 → SHAKE256 결합기(도메인 분리 · ct/ephemeral 바인딩) → AES-256-GCM — 한쪽이 깨져도 기밀성 유지.
///   ▸ 하이브리드 서명: ML-DSA-65(FIPS 204) + ECDSA P-384 — 둘 다 검증 통과해야 유효. 봉인 엔벨로프 · 워터마크 토큰 · 프랜차이즈 원장(HMAC 대신 PQ 서명 체인).
///   ▸ 개인키 at-rest 래핑: AES-256-GCM(KEK = SecureSecretVault/DPAPI) — 사용 시에만 언랩 후 즉시 Zeroize. 키 회전(구 키 = 열기/검증 전용).
///   ▸ 순수 C# ML-KEM/ML-DSA 는 NIST ACVP 공식 벡터로 검증(테스트 내장 서브셋). .NET 8 BCL 에 PQC API 없음.
/// </summary>
public sealed class PostQuantumMediaVaultEngine
{
    public const string KekName = "pqc_vault_kek";
    public static readonly byte[] Magic = "PFQV"u8.ToArray();
    public const byte FormatVersion = 1;
    private static readonly byte[] CombinerLabel = "PFS-HYBRID-KEM/ML-KEM-768+P-384/v1"u8.ToArray();

    private readonly Func<byte[]> _kek;
    private readonly object _gate = new();
    private readonly List<VaultKeySet> _keys = new();
    private readonly List<PqLedgerEntry> _ledger = new();
    private long _sealed, _opened, _signed, _verified, _tamper, _kemExchanges, _sealedBytes;

    public PostQuantumMediaVaultEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(Security.SecureSecretVault)) is Security.SecureSecretVault v ? () => v.ResolveOrCreate(KekName) : () => SHA256.HashData("pfs-pqc-kek-dev"u8)) { }

    public PostQuantumMediaVaultEngine(Func<byte[]> kek) => _kek = kek;

    public event Action? OnChange;

    public long Sealed => Interlocked.Read(ref _sealed);
    public long Opened => Interlocked.Read(ref _opened);
    public long Signatures => Interlocked.Read(ref _signed);
    public long Verifications => Interlocked.Read(ref _verified);
    public long TamperDetected => Interlocked.Read(ref _tamper);
    public long KemExchanges => Interlocked.Read(ref _kemExchanges);
    public long SealedBytes => Interlocked.Read(ref _sealedBytes);
    public IReadOnlyList<VaultKeySet> Keys { get { lock (_gate) return _keys.ToArray(); } }
    public VaultKeySet? Current { get { lock (_gate) { for (var i = _keys.Count - 1; i >= 0; i--) if (!_keys[i].Retired) return _keys[i]; return null; } } }
    public IReadOnlyList<PqLedgerEntry> Ledger { get { lock (_gate) return _ledger.ToArray(); } }
    public string? LastAction { get; private set; }

    // ══════════════════════ 키 관리 ══════════════════════

    /// <summary>새 하이브리드 키 세트 생성(기존 현재 키는 은퇴 — 열기/검증 전용 보존).</summary>
    public VaultKeySet RotateKeys(DateTimeOffset now)
    {
        var (ek, dk) = MlKem768.GenerateKeyPair();
        var (pk, sk) = MlDsa65.GenerateKeyPair();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var ecdhPriv = ecdh.ExportPkcs8PrivateKey(); var ecdsaPriv = ecdsa.ExportPkcs8PrivateKey();
        Span<byte> idHash = stackalloc byte[32];
        var idk = KeccakState.Sha3_256(); idk.Absorb(ek); idk.Absorb(pk); idk.Absorb(ecdh.ExportSubjectPublicKeyInfo()); idk.Absorb(ecdsa.ExportSubjectPublicKeyInfo()); idk.Squeeze(idHash);
        var id = "pqv-" + Convert.ToHexString(idHash[..8]).ToLowerInvariant();
        var set = new VaultKeySet(id, ek, Wrap(dk, id, "kem"), ecdh.ExportSubjectPublicKeyInfo(), Wrap(ecdhPriv, id, "ecdh"), pk, Wrap(sk, id, "dsa"), ecdsa.ExportSubjectPublicKeyInfo(), Wrap(ecdsaPriv, id, "ecdsa"), now);
        CryptographicOperations.ZeroMemory(dk); CryptographicOperations.ZeroMemory(sk); CryptographicOperations.ZeroMemory(ecdhPriv); CryptographicOperations.ZeroMemory(ecdsaPriv);
        lock (_gate) { foreach (var k in _keys) k.Retired = true; _keys.Add(set); }
        LastAction = "🔑 PQC 키 회전 · " + id;
        Raise();
        return set;
    }

    private VaultKeySet EnsureKeys() => Current ?? RotateKeys(DateTimeOffset.UtcNow);

    private VaultKeySet? FindKey(string id) { lock (_gate) { foreach (var k in _keys) if (k.Id == id) return k; } return null; }

    private byte[] Wrap(byte[] secret, string keyId, string purpose)
    {
        var kek = _kek();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var outp = new byte[12 + 16 + secret.Length];
        nonce.CopyTo(outp, 0);
        using (var g = new AesGcm(kek, 16)) g.Encrypt(nonce, secret, outp.AsSpan(28), outp.AsSpan(12, 16), Encoding.UTF8.GetBytes(keyId + "/" + purpose));
        return outp;
    }

    private byte[] Unwrap(byte[] wrapped, string keyId, string purpose)
    {
        var plain = new byte[wrapped.Length - 28];
        using var g = new AesGcm(_kek(), 16);
        g.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(keyId + "/" + purpose));
        return plain;
    }

    // ══════════════════════ 하이브리드 서명 ══════════════════════

    /// <summary>하이브리드 서명(ML-DSA-65 + ECDSA P-384 · 동일 메시지 · 도메인 ctx).</summary>
    public (byte[] Dsa, byte[] Ecdsa) HybridSign(VaultKeySet keys, ReadOnlySpan<byte> message, ReadOnlySpan<byte> context)
    {
        var sk = Unwrap(keys.DsaSkWrapped, keys.Id, "dsa");
        var ep = Unwrap(keys.EcdsaPrivateWrapped, keys.Id, "ecdsa");
        try
        {
            var dsa = MlDsa65.Sign(sk, message, context);
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(ep, out _);
            var ecSig = ec.SignData(Concat(context, message), HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence);
            Interlocked.Increment(ref _signed);
            return (dsa, ecSig);
        }
        finally { CryptographicOperations.ZeroMemory(sk); CryptographicOperations.ZeroMemory(ep); }
    }

    public bool HybridVerify(VaultKeySet keys, ReadOnlySpan<byte> message, ReadOnlySpan<byte> context, ReadOnlySpan<byte> dsaSig, ReadOnlySpan<byte> ecdsaSig)
    {
        Interlocked.Increment(ref _verified);
        var pqOk = MlDsa65.Verify(keys.DsaPk, message, dsaSig, context);
        bool ecOk;
        using (var ec = ECDsa.Create())
        {
            ec.ImportSubjectPublicKeyInfo(keys.EcdsaPublic, out _);
            try { ecOk = ec.VerifyData(Concat(context, message), ecdsaSig, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence); }
            catch (CryptographicException) { ecOk = false; }
        }
        return pqOk & ecOk;
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r); b.CopyTo(r.AsSpan(a.Length)); return r; }

    // ══════════════════════ 봉인 엔벨로프 ══════════════════════

    /// <summary>
    /// 봉인: [PFQV|ver|keyIdLen|keyId|kemCt 1088|ephLen u16|ephSPKI|nonce 12|tag 16|metaLen u32|meta|ctLen u32|ct] + [dsaSig 3309|ecdsaLen u16|ecdsaSig].
    /// 서명 대상 = 서명부 이전 전체(헤더 · 메타 · 암호문) — AAD = 헤더+메타.
    /// </summary>
    public byte[] Seal(string assetId, ReadOnlySpan<byte> plaintext, string owner, string kind, DateTimeOffset now)
    {
        var keys = EnsureKeys();
        var (ssKem, kemCt) = MlKem768.Encapsulate(keys.KemEk);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        using var recip = ECDiffieHellman.Create();
        recip.ImportSubjectPublicKeyInfo(keys.EcdhPublic, out _);
        var ssEc = eph.DeriveKeyFromHash(recip.PublicKey, HashAlgorithmName.SHA384);
        var ephPub = eph.ExportSubjectPublicKeyInfo();
        Span<byte> plainSha = stackalloc byte[32];
        KeccakState.Sha3_256(plaintext, plainSha);
        var info = new SealedAssetInfo(keys.Id, assetId, owner, kind, plaintext.Length, Convert.ToHexString(plainSha).ToLowerInvariant(), now);
        var meta = Encoding.UTF8.GetBytes(MetaCanonical(info));
        var keyId = Encoding.ASCII.GetBytes(keys.Id);
        var nonce = RandomNumberGenerator.GetBytes(12);
        using var ms = new MemoryStream(plaintext.Length + 6000);
        ms.Write(Magic); ms.WriteByte(FormatVersion); ms.WriteByte((byte)keyId.Length); ms.Write(keyId);
        ms.Write(kemCt); WriteU16(ms, ephPub.Length); ms.Write(ephPub); ms.Write(nonce);
        var tagPos = ms.Position; ms.Write(stackalloc byte[16]);
        WriteU32(ms, meta.Length); ms.Write(meta);
        var aad = ms.ToArray();
        var ct = new byte[plaintext.Length]; var tag = new byte[16];
        var key = Combine(ssKem, ssEc, kemCt, ephPub);
        try { using var g = new AesGcm(key, 16); g.Encrypt(nonce, plaintext, ct, tag, AadWithoutTag(aad, (int)tagPos)); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(ssKem); CryptographicOperations.ZeroMemory(ssEc); }
        ms.Position = tagPos; ms.Write(tag); ms.Position = ms.Length;
        WriteU32(ms, ct.Length); ms.Write(ct);
        var body = ms.ToArray();
        var (dsa, ecSig) = HybridSign(keys, body, "pfs-vault-envelope"u8);
        ms.Write(dsa); WriteU16(ms, ecSig.Length); ms.Write(ecSig);
        Interlocked.Increment(ref _sealed); Interlocked.Increment(ref _kemExchanges); Interlocked.Add(ref _sealedBytes, plaintext.Length);
        LastAction = "🔒 양자 내성 봉인 · " + assetId + " (" + plaintext.Length.ToString(CultureInfo.InvariantCulture) + "B)";
        Raise();
        return ms.ToArray();
    }

    /// <summary>열기: 형식 → 하이브리드 서명 → ML-KEM 디캡 + ECDH → AES-GCM → 평문 SHA3 대조. 실패 시 CryptographicException.</summary>
    public (byte[] Plaintext, SealedAssetInfo Info) Open(ReadOnlySpan<byte> env)
    {
        try
        {
            if (env.Length < 4 + 2 + MlKem768.CiphertextSize || !env[..4].SequenceEqual(Magic) || env[4] != FormatVersion) throw Bad("형식 오류");
            var p = 5;
            var idLen = env[p++];
            var keyId = Encoding.ASCII.GetString(env.Slice(p, idLen)); p += idLen;
            var keys = FindKey(keyId) ?? throw Bad("알 수 없는 키 " + keyId);
            var kemCt = env.Slice(p, MlKem768.CiphertextSize); p += MlKem768.CiphertextSize;
            var ephLen = BinaryPrimitives.ReadUInt16BigEndian(env[p..]); p += 2;
            var ephPub = env.Slice(p, ephLen); p += ephLen;
            var nonce = env.Slice(p, 12); p += 12;
            var tagPos = p; var tag = env.Slice(p, 16); p += 16;
            var metaLen = (int)BinaryPrimitives.ReadUInt32BigEndian(env[p..]); p += 4;
            if (metaLen < 0 || metaLen > 4096) throw Bad("메타 길이 오류");
            var meta = env.Slice(p, metaLen); p += metaLen;
            var aadEnd = p;
            var ctLen = (int)BinaryPrimitives.ReadUInt32BigEndian(env[p..]); p += 4;
            if (ctLen < 0 || p + ctLen + MlDsa65.SignatureSize + 2 > env.Length) throw Bad("길이 오류");
            var ct = env.Slice(p, ctLen); p += ctLen;
            var body = env[..p];
            var dsa = env.Slice(p, MlDsa65.SignatureSize); p += MlDsa65.SignatureSize;
            var ecLen = BinaryPrimitives.ReadUInt16BigEndian(env[p..]); p += 2;
            if (p + ecLen != env.Length) throw Bad("서명 길이 오류");
            var ecSig = env.Slice(p, ecLen);
            if (!HybridVerify(keys, body, "pfs-vault-envelope"u8, dsa, ecSig)) throw Bad("하이브리드 서명 불일치");
            var dk = Unwrap(keys.KemDkWrapped, keys.Id, "kem");
            var ep = Unwrap(keys.EcdhPrivateWrapped, keys.Id, "ecdh");
            byte[] ssKem, ssEc, key;
            try
            {
                ssKem = MlKem768.Decapsulate(dk, kemCt);
                using var me = ECDiffieHellman.Create(); me.ImportPkcs8PrivateKey(ep, out _);
                using var other = ECDiffieHellman.Create(); other.ImportSubjectPublicKeyInfo(ephPub, out _);
                ssEc = me.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA384);
                key = Combine(ssKem, ssEc, kemCt, ephPub);
            }
            finally { CryptographicOperations.ZeroMemory(dk); CryptographicOperations.ZeroMemory(ep); }
            var plain = new byte[ctLen];
            try
            {
                using var g = new AesGcm(key, 16);
                g.Decrypt(nonce, ct, tag, plain, AadWithoutTag(env[..aadEnd].ToArray(), tagPos));
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(ssKem); CryptographicOperations.ZeroMemory(ssEc); }
            var info = ParseMeta(Encoding.UTF8.GetString(meta), keyId);
            Span<byte> h = stackalloc byte[32];
            KeccakState.Sha3_256(plain, h);
            if (!string.Equals(Convert.ToHexString(h), info.PlainSha3, StringComparison.OrdinalIgnoreCase) || info.PlainBytes != plain.Length) throw Bad("평문 지문 불일치");
            Interlocked.Increment(ref _opened);
            LastAction = "🔓 금고 열기 · " + info.AssetId;
            Raise();
            return (plain, info);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentOutOfRangeException or FormatException or IndexOutOfRangeException or AuthenticationTagMismatchException)
        {
            Interlocked.Increment(ref _tamper);
            LastAction = "⛔ 봉인 검증 실패 · " + ex.Message;
            Raise();
            throw ex as CryptographicException ?? new CryptographicException("봉인 검증 실패: " + ex.Message, ex);
        }
    }

    private static CryptographicException Bad(string m) => new("PQC 금고: " + m);

    /// <summary>하이브리드 결합기: SHAKE256(label ‖ len·ssKem ‖ len·ssEcdh ‖ kemCt ‖ ephPub, 32).</summary>
    public static byte[] Combine(ReadOnlySpan<byte> ssKem, ReadOnlySpan<byte> ssEc, ReadOnlySpan<byte> kemCt, ReadOnlySpan<byte> ephPub)
    {
        var x = KeccakState.Shake256();
        x.Absorb(CombinerLabel);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)ssKem.Length); x.Absorb(len); x.Absorb(ssKem);
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)ssEc.Length); x.Absorb(len); x.Absorb(ssEc);
        x.Absorb(kemCt); x.Absorb(ephPub);
        var key = new byte[32];
        x.Squeeze(key);
        return key;
    }

    private static byte[] AadWithoutTag(byte[] header, int tagPos) { var a = (byte[])header.Clone(); Array.Clear(a, tagPos, 16); return a; }

    private static string MetaCanonical(SealedAssetInfo i) => string.Join('\n', "v1", i.KeyId, i.AssetId.Replace("\n", " ", StringComparison.Ordinal), i.Owner.Replace("\n", " ", StringComparison.Ordinal), i.Kind,
        i.PlainBytes.ToString(CultureInfo.InvariantCulture), i.PlainSha3, i.SealedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    private static SealedAssetInfo ParseMeta(string s, string keyId)
    {
        var p = s.Split('\n');
        if (p.Length != 8 || p[0] != "v1" || p[1] != keyId) throw new FormatException("메타 스키마 오류");
        return new SealedAssetInfo(p[1], p[2], p[3], p[4], long.Parse(p[5], CultureInfo.InvariantCulture), p[6], DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(p[7], CultureInfo.InvariantCulture)));
    }

    private static void WriteU16(Stream s, int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); s.Write(b); }
    private static void WriteU32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); s.Write(b); }

    // ══════════════════════ 워터마크 토큰 ══════════════════════

    /// <summary>워터마크 토큰: base64url(payload) "." base64url(mldsa) "." base64url(ecdsa) — payload = keyId|assetId|owner|unixMs.</summary>
    public string SignWatermark(string assetId, string owner, DateTimeOffset at)
    {
        var keys = EnsureKeys();
        var payload = Encoding.UTF8.GetBytes(string.Join('|', keys.Id, assetId, owner, at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        var (d, e) = HybridSign(keys, payload, "pfs-watermark"u8);
        LastAction = "💧 PQ 워터마크 서명 · " + assetId;
        Raise();
        return B64(payload) + "." + B64(d) + "." + B64(e);
    }

    public (bool Ok, string? AssetId, string? Owner) VerifyWatermark(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return (false, null, null);
        try
        {
            var payload = UnB64(parts[0]);
            var f = Encoding.UTF8.GetString(payload).Split('|');
            if (f.Length != 4 || FindKey(f[0]) is not { } keys) return (false, null, null);
            var ok = HybridVerify(keys, payload, "pfs-watermark"u8, UnB64(parts[1]), UnB64(parts[2]));
            if (!ok) Interlocked.Increment(ref _tamper);
            return ok ? (true, f[1], f[2]) : (false, null, null);
        }
        catch (FormatException) { Interlocked.Increment(ref _tamper); return (false, null, null); }
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }

    // ══════════════════════ 프랜차이즈 원장(PQ 서명 체인) ══════════════════════

    public static string LedgerCanonical(long seq, string keyId, string entrySha3, string prev, long total)
        => "pfs-pq-ledger/v1|" + seq.ToString(CultureInfo.InvariantCulture) + "|" + keyId + "|" + entrySha3 + "|" + prev + "|" + total.ToString(CultureInfo.InvariantCulture);

    /// <summary>원장 항목 추가(엔트리 SHA3 · 이전 해시 · 누적 수 → 하이브리드 서명).</summary>
    public PqLedgerEntry AppendLedger(ReadOnlySpan<byte> entry)
    {
        var keys = EnsureKeys();
        Span<byte> h = stackalloc byte[32];
        KeccakState.Sha3_256(entry, h);
        var entrySha = Convert.ToHexString(h).ToLowerInvariant();
        lock (_gate)
        {
            var seq = _ledger.Count + 1L;
            var prev = _ledger.Count > 0 ? _ledger[^1].Hash : "genesis";
            var canon = Encoding.UTF8.GetBytes(LedgerCanonical(seq, keys.Id, entrySha, prev, seq));
            var (d, e) = HybridSign(keys, canon, "pfs-ledger"u8);
            KeccakState.Sha3_256(Concat(canon, d), h);
            var item = new PqLedgerEntry(seq, keys.Id, entrySha, prev, seq, d, e, Convert.ToHexString(h).ToLowerInvariant());
            _ledger.Add(item);
            LastAction = "📜 PQ 원장 #" + seq.ToString(CultureInfo.InvariantCulture);
            Raise();
            return item;
        }
    }

    /// <summary>원장 검증(체인 · 순번 · 집계 · 하이브리드 서명). 반환 = 첫 위반 인덱스(-1 정상).</summary>
    public int VerifyLedger(IReadOnlyList<PqLedgerEntry> ledger)
    {
        var prev = "genesis";
        Span<byte> h = stackalloc byte[32];
        for (var i = 0; i < ledger.Count; i++)
        {
            var e = ledger[i];
            if (e.Seq != i + 1 || e.TotalEntries != i + 1 || e.PrevHash != prev || FindKey(e.KeyId) is not { } keys) return i;
            var canon = Encoding.UTF8.GetBytes(LedgerCanonical(e.Seq, e.KeyId, e.EntrySha3, e.PrevHash, e.TotalEntries));
            if (!HybridVerify(keys, canon, "pfs-ledger"u8, e.DsaSignature, e.EcdsaSignature)) return i;
            KeccakState.Sha3_256(Concat(canon, e.DsaSignature), h);
            if (!string.Equals(Convert.ToHexString(h), e.Hash, StringComparison.OrdinalIgnoreCase)) return i;
            prev = e.Hash;
        }
        return -1;
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("PqVault.OnChange", ex); } }
}
