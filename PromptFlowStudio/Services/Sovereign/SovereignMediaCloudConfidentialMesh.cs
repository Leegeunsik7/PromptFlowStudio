using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Sovereign;

public enum Jurisdiction : byte { KR, EU, US, JP, SG, IN }

public enum TeeProvider : byte { IntelSgx, IntelTdx, AmdSevSnp, AwsNitro, ArmCca }

public enum EnclaveState : byte { Booting, Attested, Untrusted, Revoked }

public enum PlacementVerdict : byte { Placed, NoEnclaveInJurisdiction, AttestationFailed, CapacityExhausted }

/// <summary>Enclave 증명 견적(Quote): 측정값 · Enclave 채널 공개키 · 논스 · 관할 · 공급자 · 시각 + 플랫폼 서명.</summary>
public sealed record AttestationQuote(int Enclave, byte[] Measurement, byte[] EnclavePublicKey, byte[] Nonce, Jurisdiction Jurisdiction, TeeProvider Provider, long TimestampMs, byte[] PlatformSignature);

/// <summary>
/// [PromptFlow Studio v16.53.0] 🏰 소버린 멀티 클라우드 기밀 연산 메시.
///   ▸ Enclave 레지스트리(관할 · TEE 공급자 · 측정값 SHA-384 · 용량) · 원격 증명: 검증자 논스 → 플랫폼 키(ECDSA P-256) 서명 견적 → 신뢰 플랫폼 키 핀 · 측정값 허용 목록 · 논스 1회성 · 신선도 · 관할 허용 검사.
///   ▸ 데이터 주권 배치: 데이터 거주 관할(KR/EU/US/JP/SG/IN)과 일치하는 증명된 Enclave 만 — 위반 시 배치 거부.
///   ▸ 기밀 채널: ECDH P-256 → HKDF-SHA384 방향별 AES-256-GCM 키 · 세션 솔트 + 64비트 카운터 논스(재사용 0) · 요청/응답 키 분리.
///   ▸ Enclave 내부 처리: 복호화는 Enclave 전용 고정(POH) 버퍼에서만 → 봉인 모델(측정값 바인딩 봉인 키) 커널 적용 → 재암호화 → Zeroize. ProcessFrame 무할당.
///   ⚠ 하드웨어 TEE(SGX/TDX/SEV-SNP/Nitro) 실 결선 아님 — 소프트웨어 Enclave 시뮬레이션 + 증명 프로토콜/주권 정책/암호 채널은 실구현(실 Quote 검증기로 교체 가능한 결선점).
/// </summary>
public sealed class SovereignMediaCloudConfidentialMesh : IDisposable
{
    public const int MaxEnclaves = 32, MaxFrame = 1 << 20, NonceSize = 12, TagSize = 16, MaxSessions = 64;
    public const double QuoteFreshMs = 60000;

    private sealed class Enclave
    {
        public string Name = "";
        public Jurisdiction Jurisdiction;
        public TeeProvider Provider;
        public byte[] Measurement = Array.Empty<byte>();
        public ECDsa PlatformKey = null!;
        public ECDiffieHellman ChannelKey = null!;
        public EnclaveState State;
        public int Capacity, Active;
        public long Frames;
        public byte[] Private = Array.Empty<byte>();
        public byte[] Lut = new byte[256];
        public bool ModelLoaded;
    }

    private sealed class Session
    {
        public int Enclave;
        public AesGcmCompat Up = null!, Down = null!;
        public byte[] Salt = new byte[4];
        public ulong SendCtr, RecvCtr, EnclaveSendCtr;
        public bool Open;
    }

    private readonly Enclave?[] _enclaves = new Enclave?[MaxEnclaves];
    private readonly Session?[] _sessions = new Session?[MaxSessions];
    private readonly HashSet<string> _allowedMeasurements = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<byte[]> _trustedPlatformKeys = new();
    private readonly HashSet<string> _usedNonces = new(StringComparer.Ordinal);
    private readonly byte[] _platformRoot = RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private int _count;
    private long _attestOk, _attestFail, _frames, _placed, _sovereigntyBlocks, _bytes;

    public SovereignMediaCloudConfidentialMesh() : this(true) { }

    public SovereignMediaCloudConfidentialMesh(bool seedDemo)
    {
        if (!seedDemo) return;
        AllowMeasurement(Measure("pfs-render-enclave", "16.53.0"));
        AddEnclave("seoul-sgx-1", Jurisdiction.KR, TeeProvider.IntelSgx, "pfs-render-enclave", "16.53.0", 8);
        AddEnclave("seoul-tdx-2", Jurisdiction.KR, TeeProvider.IntelTdx, "pfs-render-enclave", "16.53.0", 8);
        AddEnclave("frankfurt-snp-1", Jurisdiction.EU, TeeProvider.AmdSevSnp, "pfs-render-enclave", "16.53.0", 8);
        AddEnclave("virginia-nitro-1", Jurisdiction.US, TeeProvider.AwsNitro, "pfs-render-enclave", "16.53.0", 8);
        AddEnclave("tokyo-snp-1", Jurisdiction.JP, TeeProvider.AmdSevSnp, "pfs-render-enclave", "16.53.0", 4);
        AddEnclave("singapore-cca-1", Jurisdiction.SG, TeeProvider.ArmCca, "pfs-render-enclave", "16.53.0", 4);
    }

    public event Action? OnChange;

    public int EnclaveCount => _count;
    public string EnclaveName(int i) => _enclaves[i]!.Name;
    public Jurisdiction EnclaveJurisdiction(int i) => _enclaves[i]!.Jurisdiction;
    public TeeProvider EnclaveProvider(int i) => _enclaves[i]!.Provider;
    public EnclaveState State(int i) => _enclaves[i]!.State;
    public int ActiveSessions(int i) => _enclaves[i]!.Active;
    public int Capacity(int i) => _enclaves[i]!.Capacity;
    public long EnclaveFrames(int i) => Interlocked.Read(ref _enclaves[i]!.Frames);
    public bool ModelLoaded(int i) => _enclaves[i]!.ModelLoaded;
    public string MeasurementHex(int i) => Convert.ToHexString(_enclaves[i]!.Measurement).ToLowerInvariant();
    public long AttestationsOk => Interlocked.Read(ref _attestOk);
    public long AttestationsFailed => Interlocked.Read(ref _attestFail);
    public long FramesProcessed => Interlocked.Read(ref _frames);
    public long Placements => Interlocked.Read(ref _placed);
    public long SovereigntyBlocks => Interlocked.Read(ref _sovereigntyBlocks);
    public long BytesProcessed => Interlocked.Read(ref _bytes);
    public int OpenSessions { get { var n = 0; foreach (var s in _sessions) if (s is { Open: true }) n++; return n; } }
    public string? LastAction { get; private set; }

    // ══════════════════════ 레지스트리 · 측정 ══════════════════════

    /// <summary>코드 정체성 측정값 = SHA-384(이미지 ‖ 버전).</summary>
    public static byte[] Measure(string image, string version) => SHA384.HashData(Encoding.UTF8.GetBytes("pfs-enclave-measure|" + image + "|" + version));

    public void AllowMeasurement(byte[] m) { lock (_gate) _allowedMeasurements.Add(Convert.ToHexString(m)); }

    public int AddEnclave(string name, Jurisdiction j, TeeProvider p, string image, string version, int capacity)
    {
        lock (_gate)
        {
            if (_count >= MaxEnclaves) return -1;
            var platform = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _trustedPlatformKeys.Add(platform.ExportSubjectPublicKeyInfo());
            _enclaves[_count] = new Enclave
            {
                Name = name, Jurisdiction = j, Provider = p, Measurement = Measure(image, version), PlatformKey = platform,
                ChannelKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), Capacity = capacity,
                Private = GC.AllocateArray<byte>(MaxFrame, pinned: true), State = EnclaveState.Booting,
            };
            return _count++;
        }
    }

    /// <summary>Enclave 코드 교체(측정값 변경 → 재증명 필요 · 기존 세션 폐기).</summary>
    public void UpdateEnclaveCode(int i, string image, string version)
    {
        lock (_gate)
        {
            var e = _enclaves[i]!;
            e.Measurement = Measure(image, version); e.State = EnclaveState.Booting; e.ModelLoaded = false; Array.Clear(e.Lut);
            RevokeSessionsLocked(i);
        }
        LastAction = "♻ Enclave 코드 갱신 · " + _enclaves[i]!.Name + " → 재증명 필요";
        Raise();
    }

    // ══════════════════════ 원격 증명 ══════════════════════

    public static byte[] QuoteCanonical(byte[] measurement, byte[] enclavePub, byte[] nonce, Jurisdiction j, TeeProvider p, long ts)
    {
        var buf = new byte[measurement.Length + enclavePub.Length + nonce.Length + 2 + 8 + 16];
        var o = 0;
        "PFS-QUOTE/v1"u8.CopyTo(buf); o += 12;
        buf[o++] = (byte)j; buf[o++] = (byte)p;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(o), ts); o += 8;
        measurement.CopyTo(buf, o); o += measurement.Length;
        nonce.CopyTo(buf, o); o += nonce.Length;
        enclavePub.CopyTo(buf, o);
        return buf;
    }

    /// <summary>Enclave 측 견적 생성(시뮬레이션 TEE: 플랫폼 키로 서명).</summary>
    public AttestationQuote GenerateQuote(int i, byte[] nonce, long nowMs)
    {
        var e = _enclaves[i]!;
        var pub = e.ChannelKey.ExportSubjectPublicKeyInfo();
        var sig = e.PlatformKey.SignData(QuoteCanonical(e.Measurement, pub, nonce, e.Jurisdiction, e.Provider, nowMs), HashAlgorithmName.SHA256);
        return new AttestationQuote(i, (byte[])e.Measurement.Clone(), pub, nonce, e.Jurisdiction, e.Provider, nowMs, sig);
    }

    /// <summary>검증자: 서명(신뢰 플랫폼 키) · 측정값 허용 · 논스 일치/1회성 · 신선도 · 관할. 반환 = 통과 여부 + 사유.</summary>
    public (bool Ok, string Reason) VerifyQuote(AttestationQuote q, byte[] expectedNonce, long nowMs, Jurisdiction? required = null)
    {
        string? why = null;
        if (!CryptographicOperations.FixedTimeEquals(q.Nonce, expectedNonce)) why = "논스 불일치";
        else if (Math.Abs(nowMs - q.TimestampMs) > QuoteFreshMs) why = "견적 만료";
        else if (required is { } r && q.Jurisdiction != r) why = "관할 불일치";
        else
        {
            bool trusted = false, sigOk = false;
            lock (_gate)
            {
                foreach (var k in _trustedPlatformKeys)
                {
                    using var v = ECDsa.Create(); v.ImportSubjectPublicKeyInfo(k, out _);
                    if (v.VerifyData(QuoteCanonical(q.Measurement, q.EnclavePublicKey, q.Nonce, q.Jurisdiction, q.Provider, q.TimestampMs), q.PlatformSignature, HashAlgorithmName.SHA256)) { trusted = true; sigOk = true; break; }
                }
                if (sigOk && !_allowedMeasurements.Contains(Convert.ToHexString(q.Measurement))) why = "측정값 미허용(코드 변조/미승인 버전)";
                else if (sigOk && !_usedNonces.Add(Convert.ToHexString(q.Nonce))) why = "논스 재사용(재생)";
            }
            if (!trusted) why = "플랫폼 서명 불신";
        }
        if (why is null) { Interlocked.Increment(ref _attestOk); return (true, "OK"); }
        Interlocked.Increment(ref _attestFail);
        LastAction = "⛔ 증명 실패 · " + why;
        return (false, why);
    }

    /// <summary>Enclave 증명 수행(검증자 논스 → 견적 → 검증 → 상태 갱신).</summary>
    public bool Attest(int i, long nowMs)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var q = GenerateQuote(i, nonce, nowMs);
        var (ok, why) = VerifyQuote(q, nonce, nowMs);
        lock (_gate) _enclaves[i]!.State = ok ? EnclaveState.Attested : EnclaveState.Untrusted;
        LastAction = ok ? "✅ 원격 증명 · " + _enclaves[i]!.Name : "⛔ 증명 실패 · " + _enclaves[i]!.Name + " · " + why;
        Raise();
        return ok;
    }

    public int AttestAll(long nowMs) { var n = 0; for (var i = 0; i < _count; i++) if (Attest(i, nowMs)) n++; return n; }

    // ══════════════════════ 봉인 모델 ══════════════════════

    /// <summary>측정값 바인딩 봉인 키 = HMAC-SHA256(플랫폼 루트, 측정값) — 동일 코드 Enclave 만 언봉인.</summary>
    private byte[] SealingKey(byte[] measurement) => HMACSHA256.HashData(_platformRoot, measurement);

    /// <summary>모델(256B LUT 가중치)을 대상 측정값에 봉인.</summary>
    public byte[] SealModel(ReadOnlySpan<byte> weights256, byte[] measurement)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var outp = new byte[NonceSize + TagSize + weights256.Length];
        nonce.CopyTo(outp, 0);
        using var g = new AesGcmCompat(SealingKey(measurement), TagSize);
        g.Encrypt(nonce, weights256, outp.AsSpan(NonceSize + TagSize), outp.AsSpan(NonceSize, TagSize), "pfs-model"u8);
        return outp;
    }

    /// <summary>Enclave 내부 모델 로드(증명 + 현재 측정값 봉인 키로만 복호화).</summary>
    public bool LoadModel(int i, byte[] sealedModel)
    {
        var e = _enclaves[i]!;
        if (e.State != EnclaveState.Attested || sealedModel.Length != NonceSize + TagSize + 256) return false;
        try
        {
            using var g = new AesGcmCompat(SealingKey(e.Measurement), TagSize);
            g.Decrypt(sealedModel.AsSpan(0, NonceSize), sealedModel.AsSpan(NonceSize + TagSize), sealedModel.AsSpan(NonceSize, TagSize), e.Lut, "pfs-model"u8);
            e.ModelLoaded = true;
            return true;
        }
        catch (CryptographicException) { LastAction = "⛔ 모델 언봉인 거부(측정값 불일치) · " + e.Name; Raise(); return false; }
    }

    // ══════════════════════ 배치 · 세션 ══════════════════════

    /// <summary>데이터 주권 배치 + 채널 수립: 거주 관할의 증명된 Enclave(최저 부하)에 세션 생성. 반환 = (판정, 세션 ID, 클라이언트 키 재료).</summary>
    public (PlacementVerdict Verdict, int Session, int Enclave) Place(Jurisdiction residency, long nowMs)
    {
        int best = -1, inJ = 0;
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var e = _enclaves[i]!;
                if (e.Jurisdiction != residency) continue;
                inJ++;
                if (e.State != EnclaveState.Attested || !e.ModelLoaded || e.Active >= e.Capacity) continue;
                if (best < 0 || e.Active < _enclaves[best]!.Active) best = i;
            }
        }
        if (inJ == 0) { Interlocked.Increment(ref _sovereigntyBlocks); LastAction = "🛑 주권 차단 · " + residency + " 관할 Enclave 없음(역외 배치 금지)"; Raise(); return (PlacementVerdict.NoEnclaveInJurisdiction, -1, -1); }
        if (best < 0)
        {
            var anyAttested = false;
            lock (_gate) for (var i = 0; i < _count; i++) if (_enclaves[i]!.Jurisdiction == residency && _enclaves[i]!.State == EnclaveState.Attested) anyAttested = true;
            return (anyAttested ? PlacementVerdict.CapacityExhausted : PlacementVerdict.AttestationFailed, -1, -1);
        }
        // 클라이언트 측: 증명 시 받은 Enclave 공개키로 ECDH(여기서는 검증된 견적 공개키 사용)
        var nonce = RandomNumberGenerator.GetBytes(32);
        var q = GenerateQuote(best, nonce, nowMs);
        var (ok, _) = VerifyQuote(q, nonce, nowMs, residency);
        if (!ok) return (PlacementVerdict.AttestationFailed, -1, -1);
        using var client = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var enclavePub = ECDiffieHellman.Create(); enclavePub.ImportSubjectPublicKeyInfo(q.EnclavePublicKey, out _);
        var shared = client.DeriveKeyFromHash(enclavePub.PublicKey, HashAlgorithmName.SHA384);
        // Enclave 측 동일 비밀 도출 검증(채널 키 일치)
        var shared2 = _enclaves[best]!.ChannelKey.DeriveKeyFromHash(client.PublicKey, HashAlgorithmName.SHA384);
        if (!CryptographicOperations.FixedTimeEquals(shared, shared2)) return (PlacementVerdict.AttestationFailed, -1, -1);
        Span<byte> keys = stackalloc byte[64];
        HKDF.DeriveKey(HashAlgorithmName.SHA384, shared, keys, nonce, "pfs-confidential-channel/v1"u8);
        var s = new Session { Enclave = best, Up = new AesGcmCompat(keys[..32], TagSize), Down = new AesGcmCompat(keys[32..], TagSize), Open = true };
        RandomNumberGenerator.Fill(s.Salt);
        CryptographicOperations.ZeroMemory(keys); CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(shared2);
        int id;
        lock (_gate)
        {
            id = -1;
            for (var k = 0; k < MaxSessions; k++) if (_sessions[k] is not { Open: true }) { _sessions[k]?.Up.Dispose(); _sessions[k]?.Down.Dispose(); _sessions[k] = s; id = k; break; }
            if (id < 0) { s.Up.Dispose(); s.Down.Dispose(); return (PlacementVerdict.CapacityExhausted, -1, -1); }
            _enclaves[best]!.Active++;
        }
        Interlocked.Increment(ref _placed);
        LastAction = "🏰 기밀 배치 · " + residency + " → " + _enclaves[best]!.Name;
        Raise();
        return (PlacementVerdict.Placed, id, best);
    }

    public void CloseSession(int id)
    {
        lock (_gate)
        {
            if (_sessions[id] is not { Open: true } s) return;
            s.Open = false; _enclaves[s.Enclave]!.Active--;
            s.Up.Dispose(); s.Down.Dispose(); _sessions[id] = null;
        }
        Raise();
    }

    private void RevokeSessionsLocked(int enclave)
    {
        for (var k = 0; k < MaxSessions; k++)
            if (_sessions[k] is { Open: true } s && s.Enclave == enclave) { s.Open = false; s.Up.Dispose(); s.Down.Dispose(); _sessions[k] = null; }
        _enclaves[enclave]!.Active = 0;
    }

    private static void MakeNonce(Span<byte> nonce, byte[] salt, ulong ctr, byte dir)
    {
        salt.AsSpan(0, 3).CopyTo(nonce); nonce[3] = dir;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], ctr);
    }

    /// <summary>클라이언트 → Enclave 프레임 암호화(dst = ct ‖ tag ‖ ctr8). 반환 = 길이. 무할당.</summary>
    public int ClientEncrypt(int session, ReadOnlySpan<byte> frame, Span<byte> dst)
    {
        var s = _sessions[session] ?? throw new InvalidOperationException("세션 없음");
        if (!s.Open) throw new InvalidOperationException("세션 폐기됨");
        var ctr = ++s.SendCtr;
        Span<byte> nonce = stackalloc byte[NonceSize];
        MakeNonce(nonce, s.Salt, ctr, 0x01);
        s.Up.Encrypt(nonce, frame, dst[..frame.Length], dst.Slice(frame.Length, TagSize));
        BinaryPrimitives.WriteUInt64BigEndian(dst.Slice(frame.Length + TagSize, 8), ctr);
        return frame.Length + TagSize + 8;
    }

    /// <summary>
    /// Enclave 처리: 요청 복호화(Enclave 전용 고정 버퍼) → 카운터 재생 검사 → 봉인 모델 LUT 적용 → 응답 암호화 → Zeroize. 반환 = 응답 길이(-1 = 인증 실패/재생). 무할당.
    /// </summary>
    public int ProcessFrame(int session, ReadOnlySpan<byte> request, Span<byte> response)
    {
        var s = _sessions[session];
        if (s is not { Open: true }) return -1;
        var e = _enclaves[s.Enclave]!;
        if (e.State != EnclaveState.Attested || !e.ModelLoaded) return -1;
        var len = request.Length - TagSize - 8;
        if (len <= 0 || len > MaxFrame) return -1;
        var ctr = BinaryPrimitives.ReadUInt64BigEndian(request[(len + TagSize)..]);
        if (ctr <= s.RecvCtr) return -1;                                                                    // 재생/역순 거부
        Span<byte> nonce = stackalloc byte[NonceSize];
        MakeNonce(nonce, s.Salt, ctr, 0x01);
        var plain = e.Private.AsSpan(0, len);
        try { s.Up.Decrypt(nonce, request[..len], request.Slice(len, TagSize), plain); }
        catch (CryptographicException) { plain.Clear(); return -1; }
        s.RecvCtr = ctr;
        var lut = e.Lut;
        for (var k = 0; k < plain.Length; k++) plain[k] = lut[plain[k]];
        var outCtr = ++s.EnclaveSendCtr;
        MakeNonce(nonce, s.Salt, outCtr, 0x02);
        s.Down.Encrypt(nonce, plain, response[..len], response.Slice(len, TagSize));
        BinaryPrimitives.WriteUInt64BigEndian(response.Slice(len + TagSize, 8), outCtr);
        plain.Clear();
        Interlocked.Increment(ref e.Frames); Interlocked.Increment(ref _frames); Interlocked.Add(ref _bytes, len);
        return len + TagSize + 8;
    }

    /// <summary>클라이언트 응답 복호화. 반환 = 평문 길이(-1 실패). 무할당.</summary>
    public int ClientDecrypt(int session, ReadOnlySpan<byte> response, Span<byte> frame)
    {
        var s = _sessions[session];
        if (s is not { Open: true }) return -1;
        var len = response.Length - TagSize - 8;
        if (len <= 0) return -1;
        var ctr = BinaryPrimitives.ReadUInt64BigEndian(response[(len + TagSize)..]);
        Span<byte> nonce = stackalloc byte[NonceSize];
        MakeNonce(nonce, s.Salt, ctr, 0x02);
        try { s.Down.Decrypt(nonce, response[..len], response.Slice(len, TagSize), frame[..len]); }
        catch (CryptographicException) { return -1; }
        return len;
    }

    public static string ProviderLabel(TeeProvider p) => p switch
    {
        TeeProvider.IntelSgx => "Intel SGX", TeeProvider.IntelTdx => "Intel TDX", TeeProvider.AmdSevSnp => "AMD SEV-SNP", TeeProvider.AwsNitro => "AWS Nitro", _ => "Arm CCA",
    };

    public static string StateLabel(EnclaveState s) => s switch { EnclaveState.Attested => "증명됨", EnclaveState.Untrusted => "불신", EnclaveState.Revoked => "폐기", _ => "부팅" };

    public static string VerdictLabel(PlacementVerdict v) => v switch
    {
        PlacementVerdict.Placed => "배치", PlacementVerdict.NoEnclaveInJurisdiction => "주권 차단", PlacementVerdict.AttestationFailed => "증명 실패", _ => "용량 소진",
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ConfidentialMesh.OnChange", ex); } }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _sessions) { s?.Up.Dispose(); s?.Down.Dispose(); }
            for (var i = 0; i < _count; i++) { _enclaves[i]!.PlatformKey.Dispose(); _enclaves[i]!.ChannelKey.Dispose(); Array.Clear(_enclaves[i]!.Private); }
        }
        CryptographicOperations.ZeroMemory(_platformRoot);
    }
}
