using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Enterprise;

/// <summary>v16.27.0 편집 이력 1건(c2pa.actions).</summary>
public sealed record ProvenanceAction(string Action, string Description, DateTimeOffset When);

/// <summary>v16.27.0 서명 요청 메타.</summary>
public sealed record ProvenanceRequest(string Title, string Tenant, string Author, IReadOnlyList<ProvenanceAction> Actions, string PromptDigest, int SceneCount);

public sealed record ProvenanceResult(bool Ok, string? VideoPath, string? ManifestPath, string? WatermarkId, string Message);

public sealed record ProvenanceVerification(bool SignatureValid, bool HashValid, bool? WatermarkMatch, double WatermarkScore, string? Signer, string Message)
{
    public bool Valid => SignatureValid && HashValid && WatermarkMatch != false;
}

/// <summary>
/// [PromptFlowStudio] v16.27.0 🔏 C2PA 저작권 검증 &amp; 암호화 워터마크 엔진 — AI 생성 출처 · 편집 이력 · IP 핑거프린트를 서명 매니페스트 + 비가시 워터마크로 영상에 결속.
///   ▸ 비가시 워터마크: 64비트 ID(매니페스트 claim SHA-256 앞 8바이트)를 Y 평면 8×8 블록 DC 에 ±강도 확산 스펙트럼으로 삽입
///     (블록→비트 매핑·부호는 비밀키 시드 PN · <see cref="EmbedWatermark"/> 무할당 Span). 검출은 블록 평균의 4-이웃 고역 잔차 상관(<see cref="DetectWatermark"/>)
///     — 재인코딩(crf≤23)에 견딤, 크롭/스케일/회전 동기화는 미지원.
///   ▸ 스트림 인젝션: ffmpeg yuv420p 디코드 파이프 → 프레임별 삽입(ArrayPool 1회 대여) → x264 인코드 파이프(오디오 copy) + mp4 comment 메타에 매니페스트 ID.
///   ▸ 매니페스트: C2PA 어서션 구조(c2pa.actions[trainedAlgorithmicMedia] · stds.schema-org.CreativeWork · pfs.ip_fingerprint · c2pa.hash.data)를
///     JSON claim 으로 만들어 ES256(ECDSA P-256 · SHA-256) 서명 → 사이드카 <c>*.c2pa.json</c>(claim 문자열 원문 + 서명 + 인증서 DER).
///     ⚠ JUMBF/COSE 임베드 및 C2PA 신뢰 목록 인증서가 아니므로 c2patool 공식 검증 대상은 아님(자체 검증 <see cref="Verify"/>).
///   ▸ 서명 키: 자체 서명 X.509(1년) — PKCS#8 개인키를 <see cref="SecureSecretVault"/> 'C2PA_SIGNER_SEAL' 키로 AES-GCM 봉인해 LocalAppData 보관(평문 저장 0).
/// </summary>
public sealed class C2paContentProvenanceEngine : IDisposable
{
    public const int Block = 8;
    public const int PayloadBits = 64;
    public const int DefaultStrength = 3;
    public const double DetectThreshold = 4.0;
    public const string DigitalSourceType = "http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia";
    public static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(20);

    private readonly Func<string?> _ffmpeg;
    private readonly Func<byte[]> _sealKey;
    private readonly Func<byte[]> _wmKey;
    private readonly string _keyDir;
    private readonly object _gate = new();
    private X509Certificate2? _cert;
    private ECDsa? _key;
    private int _busy;
    private long _signed, _verified, _failed;

    public C2paContentProvenanceEngine(IVideoMergeService merge, SecureSecretVault vault)
        : this(() => merge.ResolvedFfmpegPath, () => vault.ResolveOrCreate("C2PA_SIGNER_SEAL"), () => vault.ResolveOrCreate("C2PA_WATERMARK"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Keys")) { }

    public C2paContentProvenanceEngine(Func<string?> ffmpeg, Func<byte[]> sealKey, Func<byte[]> watermarkKey, string keyDir)
    {
        _ffmpeg = ffmpeg;
        _sealKey = sealKey;
        _wmKey = watermarkKey;
        _keyDir = keyDir;
    }

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public long SignedCount => Interlocked.Read(ref _signed);
    public long VerifiedCount => Interlocked.Read(ref _verified);
    public long FailedCount => Interlocked.Read(ref _failed);
    public string? LastAction { get; private set; }
    public double Progress { get; private set; }
    public X509Certificate2? Certificate { get { lock (_gate) return _cert; } }
    private string KeyPath => Path.Combine(_keyDir, "c2pa_signer.sealed");

    // ══════════════════════ 워터마크(무할당 핫패스) ══════════════════════

    /// <summary>비밀키 → PN 시드(64비트).</summary>
    public static ulong SeedFrom(ReadOnlySpan<byte> key)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(key, h);
        return BitConverter.ToUInt64(h);
    }

    /// <summary>블록 i 의 (비트 인덱스, 부호) — 비트는 시드 오프셋 순환(균등 분배), 부호는 splitmix64(seed ^ i) PN.</summary>
    public static int BlockBit(ulong seed, int block, out int sign)
    {
        var z = seed ^ ((ulong)block * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        sign = (z & 1) == 0 ? 1 : -1;
        return (int)(((ulong)block + (seed >> 58)) % PayloadBits);   // 비트당 블록 수 균등(검출 신뢰도 안정) · 부호만 PN
    }

    /// <summary>Y 평면(w×h, stride=w)에 64비트 페이로드 삽입 — 블록 DC 에 ±strength(PN 부호 × 비트 극성) · 0~255 포화.</summary>
    public static void EmbedWatermark(Span<byte> y, int w, int h, ulong payload, ulong seed, int strength = DefaultStrength)
    {
        var bx = w / Block; var by = h / Block;
        if (bx <= 0 || by <= 0 || y.Length < w * h) return;
        for (var j = 0; j < by; j++)
        {
            for (var i = 0; i < bx; i++)
            {
                var bit = BlockBit(seed, j * bx + i, out var sign);
                var delta = strength * sign * (((payload >> bit) & 1) == 1 ? 1 : -1);
                for (var r = 0; r < Block; r++)
                {
                    var row = y.Slice((j * Block + r) * w + i * Block, Block);
                    for (var c = 0; c < Block; c++) row[c] = (byte)Math.Clamp(row[c] + delta, 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// 블록 평균 4-이웃 고역 잔차를 PN 부호와 상관해 비트별 누적(<paramref name="acc"/> 길이 64 · 다프레임 누적 가능 · <paramref name="blockMeans"/>는 bx*by 이상 작업 버퍼).
    /// </summary>
    public static void AccumulateWatermark(ReadOnlySpan<byte> y, int w, int h, ulong seed, Span<double> acc, Span<float> blockMeans)
    {
        var bx = w / Block; var by = h / Block;
        if (bx < 3 || by < 3 || y.Length < w * h || acc.Length < PayloadBits || blockMeans.Length < bx * by) return;
        for (var j = 0; j < by; j++)
            for (var i = 0; i < bx; i++)
            {
                var s = 0;
                for (var r = 0; r < Block; r++)
                {
                    var row = y.Slice((j * Block + r) * w + i * Block, Block);
                    for (var c = 0; c < Block; c++) s += row[c];
                }
                blockMeans[j * bx + i] = s / (float)(Block * Block);
            }
        for (var j = 1; j < by - 1; j++)
            for (var i = 1; i < bx - 1; i++)
            {
                var k = j * bx + i;
                var resid = blockMeans[k] - 0.25f * (blockMeans[k - 1] + blockMeans[k + 1] + blockMeans[k - bx] + blockMeans[k + bx]);
                var bit = BlockBit(seed, k, out var sign);
                acc[bit] += sign * resid;
            }
    }

    /// <summary>
    /// 누적값 → 페이로드 + 신뢰도(|acc| 평균 / |acc| 표준편차). 워터마크가 있으면 |acc| 가 0 에서 멀리 몰려 값이 커지고(≫4),
    /// 무워터마크(가우시안 잡음)는 ≈1.3 — <see cref="DetectThreshold"/> 이상이면 검출.
    /// </summary>
    public static ulong DecodeAccumulator(ReadOnlySpan<double> acc, out double score)
    {
        ulong v = 0; double sumAbs = 0, sumSq = 0;
        for (var b = 0; b < PayloadBits; b++)
        {
            if (acc[b] > 0) v |= 1UL << b;
            sumAbs += Math.Abs(acc[b]);
            sumSq += acc[b] * acc[b];
        }
        var mean = sumAbs / PayloadBits;
        var var_ = Math.Max(1e-9, sumSq / PayloadBits - mean * mean);
        score = mean / Math.Sqrt(var_);
        return v;
    }

    /// <summary>단일 프레임 검출(편의 · 작업 버퍼 ArrayPool).</summary>
    public static ulong DetectWatermark(ReadOnlySpan<byte> y, int w, int h, ulong seed, out double score)
    {
        Span<double> acc = stackalloc double[PayloadBits];
        var n = (w / Block) * (h / Block);
        var buf = ArrayPool<float>.Shared.Rent(Math.Max(1, n));
        try
        {
            AccumulateWatermark(y, w, h, seed, acc, buf.AsSpan(0, Math.Max(1, n)));
            return DecodeAccumulator(acc, out score);
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
    }

    /// <summary>해밍 거리(검출 ID 대 매니페스트 ID 일치 판정용).</summary>
    public static int Hamming(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    // ══════════════════════ 매니페스트 / 서명 ══════════════════════

    /// <summary>C2PA 어서션 구조 claim JSON(서명 대상 원문 · 집계 필드 포함).</summary>
    public static string BuildClaim(ProvenanceRequest req, string instanceId, string watermarkIdHex, string fileSha256Hex, DateTimeOffset at)
    {
        using var ms = new MemoryStream(1024);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("claim_generator", "PromptFlow Studio/16.27.0");
            w.WriteString("format", "video/mp4");
            w.WriteString("instance_id", "xmp:iid:" + instanceId);
            w.WriteString("dc:title", req.Title);
            w.WriteString("signed_at", at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteNumber("assertion_count", 4);
            w.WriteStartArray("assertions");

            w.WriteStartObject(); w.WriteString("label", "c2pa.actions"); w.WriteStartObject("data"); w.WriteStartArray("actions");
            w.WriteStartObject(); w.WriteString("action", "c2pa.created"); w.WriteString("digitalSourceType", DigitalSourceType); w.WriteString("softwareAgent", "PromptFlow Studio"); w.WriteEndObject();
            foreach (var a in req.Actions)
            {
                w.WriteStartObject(); w.WriteString("action", a.Action.StartsWith("c2pa.", StringComparison.Ordinal) ? a.Action : "c2pa.edited");
                w.WriteString("when", a.When.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                w.WriteStartObject("parameters"); w.WriteString("description", a.Description); w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();

            w.WriteStartObject(); w.WriteString("label", "stds.schema-org.CreativeWork"); w.WriteStartObject("data");
            w.WriteString("@context", "https://schema.org"); w.WriteString("@type", "CreativeWork");
            w.WriteStartArray("author"); w.WriteStartObject(); w.WriteString("@type", "Organization"); w.WriteString("name", req.Tenant); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("@type", "Person"); w.WriteString("name", req.Author); w.WriteEndObject(); w.WriteEndArray();
            w.WriteEndObject(); w.WriteEndObject();

            w.WriteStartObject(); w.WriteString("label", "pfs.ip_fingerprint"); w.WriteStartObject("data");
            w.WriteString("watermark_id", watermarkIdHex); w.WriteString("watermark_alg", "pfs-blockdc-ss64/v1");
            w.WriteString("prompt_sha256", req.PromptDigest); w.WriteNumber("scene_count", req.SceneCount); w.WriteNumber("action_count", req.Actions.Count + 1);
            w.WriteEndObject(); w.WriteEndObject();

            w.WriteStartObject(); w.WriteString("label", "c2pa.hash.data"); w.WriteStartObject("data");
            w.WriteString("alg", "sha256"); w.WriteString("hash", fileSha256Hex);
            w.WriteEndObject(); w.WriteEndObject();

            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>claim 원문 → 워터마크 ID(SHA-256 앞 8바이트 · 해시 필드 이전 단계에서 사용하는 사전 claim 기준).</summary>
    public static ulong WatermarkIdOf(string instanceId, string promptDigest)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "|" + promptDigest), h);
        return BitConverter.ToUInt64(h);
    }

    /// <summary>사이드카 JSON(claim 문자열 원문 · ES256 서명 · 인증서 DER).</summary>
    public static string BuildSidecar(string claim, byte[] signature, byte[] certDer)
    {
        using var ms = new MemoryStream(claim.Length + 2048);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("spec", "c2pa-sidecar/pfs-1");
            w.WriteString("claim", claim);
            w.WriteStartObject("signature");
            w.WriteString("alg", "ES256");
            w.WriteString("value", Convert.ToBase64String(signature));
            w.WriteString("x5c", Convert.ToBase64String(certDer));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// 사이드카 검증(정적 · 파일 해시 주입) — 스키마 · ES256 서명(인증서 공개키) · c2pa.hash.data 대 실제 해시(FixedTimeEquals).
    /// 반환: (서명 유효, 해시 유효, 워터마크 ID hex, 서명자 CN, 사유).
    /// </summary>
    public static (bool Sig, bool Hash, string? WatermarkId, string? Signer, string Message) VerifySidecar(string? sidecarJson, ReadOnlySpan<byte> actualSha256)
    {
        if (string.IsNullOrWhiteSpace(sidecarJson) || sidecarJson.Length > 1_000_000) return (false, false, null, null, "매니페스트가 비었습니다.");
        try
        {
            using var doc = JsonDocument.Parse(sidecarJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("claim", out var ce) || ce.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("signature", out var se) || se.ValueKind != JsonValueKind.Object
                || !se.TryGetProperty("alg", out var alg) || alg.GetString() != "ES256"
                || !se.TryGetProperty("value", out var sv) || sv.ValueKind != JsonValueKind.String
                || !se.TryGetProperty("x5c", out var xc) || xc.ValueKind != JsonValueKind.String)
                return (false, false, null, null, "매니페스트 스키마 불일치");
            var claim = ce.GetString()!;
            var sig = Convert.FromBase64String(sv.GetString()!);
            using var cert = new X509Certificate2(Convert.FromBase64String(xc.GetString()!));
            using var pub = cert.GetECDsaPublicKey();
            if (pub is null) return (false, false, null, null, "인증서에 ECDSA 공개키가 없습니다.");
            var sigOk = pub.VerifyData(Encoding.UTF8.GetBytes(claim), sig, HashAlgorithmName.SHA256);
            string? wm = null, hashHex = null;
            using (var cd = JsonDocument.Parse(claim))
            {
                if (cd.RootElement.TryGetProperty("assertions", out var asr) && asr.ValueKind == JsonValueKind.Array)
                    foreach (var a in asr.EnumerateArray())
                    {
                        if (!a.TryGetProperty("label", out var lb) || !a.TryGetProperty("data", out var data)) continue;
                        var label = lb.GetString();
                        if (label == "c2pa.hash.data" && data.TryGetProperty("hash", out var hh)) hashHex = hh.GetString();
                        else if (label == "pfs.ip_fingerprint" && data.TryGetProperty("watermark_id", out var ww)) wm = ww.GetString();
                    }
            }
            var hashOk = hashHex is not null && CloudRenderFarmDispatcherService.HashMatches(actualSha256, hashHex);
            var signer = cert.GetNameInfo(X509NameType.SimpleName, false);
            var msg = !sigOk ? "⛔ 서명 불일치(매니페스트 변조)" : !hashOk ? "⛔ 영상 해시 불일치(파일 변조)" : "✅ 서명·해시 일치";
            return (sigOk, hashOk, wm, signer, msg);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            return (false, false, null, null, "매니페스트 파싱/서명 오류: " + ex.Message);
        }
    }

    // ══════════════════════ 서명 키 / 인증서 ══════════════════════

    /// <summary>서명 인증서 확보 — 봉인 파일 로드(복호 실패/만료 시 재발급) 또는 신규 발급(<paramref name="forceNew"/>).</summary>
    public X509Certificate2 EnsureCertificate(string subjectCn, bool forceNew = false)
    {
        lock (_gate)
        {
            if (!forceNew && _cert is not null && _cert.NotAfter > DateTime.Now.AddDays(1)) return _cert;
            if (!forceNew && TryLoadSealed(out var loaded, out var key) && loaded.NotAfter > DateTime.Now.AddDays(1))
            {
                Swap(loaded, key);
                return loaded;
            }
            var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var cn = TeamCollaborationSessionBridge.SanitizeText(subjectCn, 60).Replace(",", " ", StringComparison.Ordinal).Replace("=", " ", StringComparison.Ordinal);
            var req = new CertificateRequest("CN=" + (cn.Length == 0 ? "PromptFlow Signer" : cn) + ", O=PromptFlow Studio C2PA", ec, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.36") }, false));   // id-kp-documentSigning
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            using var withKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            var pub = new X509Certificate2(withKey.RawData);
            SaveSealed(pub.RawData, ec);
            Swap(pub, ec);
            LastAction = "🪪 C2PA 서명 인증서 발급 · " + pub.GetNameInfo(X509NameType.SimpleName, false);
            Raise();
            return pub;
        }
    }

    private void Swap(X509Certificate2 cert, ECDsa key)
    {
        var oldC = _cert; var oldK = _key;
        _cert = cert; _key = key;
        if (!ReferenceEquals(oldC, cert)) oldC?.Dispose();
        if (!ReferenceEquals(oldK, key)) oldK?.Dispose();
    }

    private void SaveSealed(byte[] certDer, ECDsa key)
    {
        try
        {
            Directory.CreateDirectory(_keyDir);
            var p8 = key.ExportPkcs8PrivateKey();
            try
            {
                var plain = new byte[4 + certDer.Length + p8.Length];
                BitConverter.TryWriteBytes(plain.AsSpan(0, 4), certDer.Length);
                certDer.CopyTo(plain, 4); p8.CopyTo(plain, 4 + certDer.Length);
                var sealedBytes = Seal(_sealKey(), plain);
                CryptographicOperations.ZeroMemory(plain);
                File.WriteAllBytes(KeyPath, sealedBytes);
            }
            finally { CryptographicOperations.ZeroMemory(p8); }
        }
        catch (Exception ex) { Program.Log("C2pa.SaveKey", ex); }
    }

    private bool TryLoadSealed(out X509Certificate2 cert, out ECDsa key)
    {
        cert = null!; key = null!;
        try
        {
            if (!File.Exists(KeyPath)) return false;
            var plain = Unseal(_sealKey(), File.ReadAllBytes(KeyPath));
            if (plain is null || plain.Length < 8) return false;
            try
            {
                var cl = BitConverter.ToInt32(plain, 0);
                if (cl <= 0 || cl > plain.Length - 4) return false;
                cert = new X509Certificate2(plain.AsSpan(4, cl));
                key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(plain.AsSpan(4 + cl), out _);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) { Program.Log("C2pa.LoadKey", ex); return false; }
    }

    /// <summary>AES-256-GCM 봉인(nonce12 | tag16 | cipher).</summary>
    public static byte[] Seal(byte[] secret, byte[] plain)
    {
        Span<byte> k = stackalloc byte[32];
        SHA256.HashData(secret, k);
        var outBuf = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(outBuf.AsSpan(0, 12));
        using var gcm = new AesGcmCompat(k, 16);
        gcm.Encrypt(outBuf.AsSpan(0, 12), plain, outBuf.AsSpan(28), outBuf.AsSpan(12, 16));
        CryptographicOperations.ZeroMemory(k);
        return outBuf;
    }

    public static byte[]? Unseal(byte[] secret, byte[] data)
    {
        if (data.Length < 28) return null;
        Span<byte> k = stackalloc byte[32];
        SHA256.HashData(secret, k);
        var plain = new byte[data.Length - 28];
        try
        {
            using var gcm = new AesGcmCompat(k, 16);
            gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(k); }
    }

    /// <summary>claim 서명(ES256) — 인증서 없으면 발급.</summary>
    public byte[] SignClaim(string claim, string signerCn)
    {
        EnsureCertificate(signerCn);
        lock (_gate) return _key!.SignData(Encoding.UTF8.GetBytes(claim), HashAlgorithmName.SHA256);
    }

    // ══════════════════════ 스트림 인젝션 파이프라인 ══════════════════════

    /// <summary>스토리보드 → 서명 요청(편집 이력 = 컷별 생성 · 최종 병합 · 팀 승인).</summary>
    public static ProvenanceRequest RequestFromProject(StoryboardProject p, string tenant, string author, bool teamApproved)
    {
        var actions = new List<ProvenanceAction>();
        var sb = new StringBuilder();
        var i = 0;
        foreach (var s in p.Scenes)
        {
            i++;
            sb.Append(s.Prompt).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.LocalPath)) actions.Add(new("c2pa.edited", "AI 컷 " + i.ToString(CultureInfo.InvariantCulture) + " 생성(" + s.Mode + ")", DateTimeOffset.Now));
            if (actions.Count >= 64) break;
        }
        actions.Add(new("c2pa.edited", "컷 병합 · 후처리", DateTimeOffset.Now));
        if (teamApproved) actions.Add(new("c2pa.published", "팀 리뷰 전 컷 승인", DateTimeOffset.Now));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return new ProvenanceRequest(p.Title, tenant, author, actions, digest, p.Scenes.Count);
    }

    /// <summary>
    /// 워터마크 인젝션 + 서명 — ffmpeg 디코드(yuv420p) → 프레임별 <see cref="EmbedWatermark"/> → x264 인코드(오디오 copy) → 해시 → 서명 사이드카.
    /// ffmpeg 부재/실패는 Graceful(원본 보존 · 예외 0).
    /// </summary>
    public async Task<ProvenanceResult> ProtectAsync(string input, ProvenanceRequest req, string? output = null, CancellationToken ct = default)
    {
        if (!File.Exists(input)) return new(false, null, null, null, "원본 영상이 없습니다.");
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return new(false, null, null, null, "ffmpeg 실행 파일을 찾을 수 없습니다.");
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, null, null, "다른 서명 작업이 진행 중입니다.");
        output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_c2pa.mp4");
        Progress = 0;
        LastAction = "🔏 워터마크 인젝션 중";
        Raise();
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, input, ct).ConfigureAwait(false);
            if (probe is null) return Fail("영상 정보를 읽지 못했습니다.");
            var (w, h, dur) = probe.Value;
            w &= ~1; h &= ~1;
            var instance = Guid.NewGuid().ToString("D");
            var wmId = WatermarkIdOf(instance, req.PromptDigest);
            var wmHex = wmId.ToString("x16", CultureInfo.InvariantCulture);
            var seed = SeedFrom(_wmKey());
            var fps = await ProbeFpsAsync(ffmpeg, input, ct).ConfigureAwait(false);

            var dec = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", input, "-vf", "scale=" + w.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "yuv420p", "pipe:1" })
                dec.ArgumentList.Add(a);
            var enc = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-hide_banner", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture),
                         "-r", fps.ToString("0.###", CultureInfo.InvariantCulture), "-i", "pipe:0", "-i", input, "-map", "0:v", "-map", "1:a?",
                         "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "copy", "-shortest",
                         "-metadata", "comment=c2pa:xmp:iid:" + instance, "-metadata", "copyright=" + TeamCollaborationSessionBridge.SanitizeText(req.Tenant, 64), "-movflags", "+faststart", output })
                enc.ArgumentList.Add(a);

            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(EncodeTimeout);
            using var pd = new Process { StartInfo = dec };
            using var pe = new Process { StartInfo = enc };
            if (!pd.StartCompat()) return Fail("디코더 시작 실패");
            if (!pe.StartCompat()) { try { pd.KillCompat(true); } catch { } return Fail("인코더 시작 실패"); }
            var decErr = pd.StandardError.ReadToEndAsync(d.Token);
            var encErr = pe.StandardError.ReadToEndAsync(d.Token);
            _ = pe.StandardOutput.ReadToEndAsync(d.Token);
            var frameBytes = w * h * 3 / 2;
            var buf = ArrayPool<byte>.Shared.Rent(frameBytes);
            var frames = 0;
            var expected = Math.Max(1, dur * fps);
            try
            {
                var src = pd.StandardOutput.BaseStream;
                var dst = pe.StandardInput.BaseStream;
                while (true)
                {
                    var off = 0;
                    while (off < frameBytes)
                    {
                        var r = await src.ReadAsync(buf.AsMemory(off, frameBytes - off), d.Token).ConfigureAwait(false);
                        if (r <= 0) break;
                        off += r;
                    }
                    if (off < frameBytes) break;
                    EmbedWatermark(buf.AsSpan(0, w * h), w, h, wmId, seed);
                    await dst.WriteAsync(buf.AsMemory(0, frameBytes), d.Token).ConfigureAwait(false);
                    if ((++frames & 15) == 0) { Progress = Math.Min(0.95, frames / expected); Raise(); }
                }
                await dst.FlushAsync(d.Token).ConfigureAwait(false);
                pe.StandardInput.Close();
                await pd.WaitForExitAsync(d.Token).ConfigureAwait(false);
                await pe.WaitForExitAsync(d.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!pd.HasExited) pd.KillCompat(true); } catch { }
                try { if (!pe.HasExited) pe.KillCompat(true); } catch { }
                throw;
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            if (frames == 0 || pe.ExitCode != 0)
            {
                var err = pe.ExitCode != 0 ? await encErr.ConfigureAwait(false) : await decErr.ConfigureAwait(false);
                Program.Log("C2pa.Encode", new InvalidOperationException(err.Trim()));
                return Fail("인코딩 실패(원본 보존)");
            }

            byte[] fileHash;
            await using (var fs = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                fileHash = await SHA256.HashDataAsync(fs, d.Token).ConfigureAwait(false);
            var claim = BuildClaim(req, instance, wmHex, Convert.ToHexString(fileHash).ToLowerInvariant(), DateTimeOffset.Now);
            var sig = SignClaim(claim, req.Tenant + " Signer");
            byte[] der;
            lock (_gate) der = _cert!.RawData;
            var manifestPath = output + ".c2pa.json";
            await File.WriteAllTextAsync(manifestPath, BuildSidecar(claim, sig, der), new UTF8Encoding(false), d.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _signed);
            Progress = 1;
            LastAction = "🔏 C2PA 서명 완료 · WM " + wmHex;
            return new(true, output, manifestPath, wmHex, "서명 완료 · 프레임 " + frames.ToString(CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail("취소됨"); }
        catch (Exception ex) { Program.Log("C2pa.Protect", ex); return Fail("서명 실패: " + ex.Message); }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Raise();
        }

        ProvenanceResult Fail(string m)
        {
            Interlocked.Increment(ref _failed);
            LastAction = "⚠ " + m;
            return new(false, null, null, null, m);
        }
    }

    /// <summary>
    /// 영상 + 사이드카 검증 — 서명/해시(정적) + (ffmpeg 가능 시) 앞 30프레임 워터마크 누적 검출 → 매니페스트 ID 해밍 거리 ≤ 4 일치.
    /// </summary>
    public async Task<ProvenanceVerification> Verify(string video, string? manifestPath = null, CancellationToken ct = default)
    {
        manifestPath ??= video + ".c2pa.json";
        if (!File.Exists(video) || !File.Exists(manifestPath)) return new(false, false, null, 0, null, "영상 또는 매니페스트(.c2pa.json)가 없습니다.");
        byte[] hash;
        await using (var fs = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
        var (sig, hashOk, wm, signer, msg) = VerifySidecar(json, hash);
        bool? match = null; double score = 0;
        var ffmpeg = _ffmpeg();
        if (ffmpeg is not null && wm is not null && ulong.TryParse(wm, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expect))
        {
            var detected = await DetectFromVideoAsync(ffmpeg, video, SeedFrom(_wmKey()), 30, ct).ConfigureAwait(false);
            if (detected is { } dt)
            {
                score = dt.Score;
                match = dt.Score >= DetectThreshold && Hamming(dt.Id, expect) <= 4;
                if (match == false) msg += " · ⚠ 워터마크 불일치/미검출";
                else msg += " · 🔍 워터마크 일치";
            }
        }
        var v = new ProvenanceVerification(sig, hashOk, match, score, signer, msg);
        Interlocked.Increment(ref v.Valid ? ref _verified : ref _failed);
        LastAction = (v.Valid ? "✅ 검증 통과" : "⛔ 검증 실패") + " · " + Path.GetFileName(video);
        Raise();
        return v;
    }

    private static async Task<(ulong Id, double Score)?> DetectFromVideoAsync(string ffmpeg, string video, ulong seed, int maxFrames, CancellationToken ct)
    {
        var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, video, ct).ConfigureAwait(false);
        if (probe is null) return null;
        var w = probe.Value.W & ~1; var h = probe.Value.H & ~1;
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", video, "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture),
                     "-vf", "scale=" + w.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        if (!p.StartCompat()) return null;
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(60));
        _ = p.StandardError.ReadToEndAsync(d.Token);
        var frame = ArrayPool<byte>.Shared.Rent(w * h);
        var means = ArrayPool<float>.Shared.Rent(Math.Max(1, (w / Block) * (h / Block)));
        var acc = new double[PayloadBits];
        var n = 0;
        try
        {
            var s = p.StandardOutput.BaseStream;
            while (n < maxFrames)
            {
                var off = 0;
                while (off < w * h)
                {
                    var r = await s.ReadAsync(frame.AsMemory(off, w * h - off), d.Token).ConfigureAwait(false);
                    if (r <= 0) break;
                    off += r;
                }
                if (off < w * h) break;
                AccumulateWatermark(frame.AsSpan(0, w * h), w, h, seed, acc, means);
                n++;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            try { if (!p.HasExited) p.KillCompat(true); } catch { }
            ArrayPool<byte>.Shared.Return(frame);
            ArrayPool<float>.Shared.Return(means);
        }
        if (n == 0) return null;
        var id = DecodeAccumulator(acc, out var score);
        return (id, score);
    }

    private static async Task<double> ProbeFpsAsync(string ffmpeg, string input, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input);
            using var p = new Process { StartInfo = psi };
            if (!p.StartCompat()) return 30;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(15));
            var err = await p.StandardError.ReadToEndAsync(d.Token).ConfigureAwait(false);
            var f = CloudRenderFarmDispatcherService.ParseFps(err);
            return f is > 1 and < 241 ? f : 30;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("C2pa.Fps", ex); return 30; }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("C2pa.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cert?.Dispose(); _cert = null;
            _key?.Dispose(); _key = null;
        }
        OnChange = null;
    }
}
