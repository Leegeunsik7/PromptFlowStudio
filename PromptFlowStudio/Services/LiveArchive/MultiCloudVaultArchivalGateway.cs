using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.LiveArchive;

public enum VaultProvider : byte { AwsS3, CloudflareR2, MinIO }

/// <summary>v17.87.0 S3 호환 버킷 엔드포인트 — 시크릿은 메모리 한정(직렬화 · 로그 금지) · 환경변수 로드 지원.</summary>
public sealed class VaultEndpoint
{
    public const string EnvProvider = "PFS_VAULT_PROVIDER", EnvUrl = "PFS_VAULT_ENDPOINT", EnvRegion = "PFS_VAULT_REGION", EnvBucket = "PFS_VAULT_BUCKET",
        EnvAccess = "PFS_VAULT_ACCESS_KEY", EnvSecret = "PFS_VAULT_SECRET_KEY";

    public VaultProvider Provider { get; init; }
    /// <summary>서비스 루트(예: https://s3.ap-northeast-2.amazonaws.com · https://&lt;acct&gt;.r2.cloudflarestorage.com · http://127.0.0.1:9000).</summary>
    public string ServiceUrl { get; init; } = "";
    public string Region { get; init; } = "us-east-1";
    public string Bucket { get; init; } = "";
    public string Prefix { get; init; } = "live/";
    public string AccessKey { get; init; } = "";
    public string SecretKey { get; init; } = "";
    /// <summary>경로형(/bucket/key) — R2 · MinIO 기본, AWS 는 가상 호스트형(bucket.host/key).</summary>
    public bool PathStyle { get; init; }

    public static VaultEndpoint Preset(VaultProvider p, string accountOrUrl, string bucket, string region, string accessKey = "", string secretKey = "") => p switch
    {
        VaultProvider.CloudflareR2 => new VaultEndpoint
        {
            Provider = p, Bucket = bucket, Region = "auto", PathStyle = true, AccessKey = accessKey, SecretKey = secretKey,
            ServiceUrl = accountOrUrl.Contains("://", StringComparison.Ordinal) ? accountOrUrl : "https://" + accountOrUrl + ".r2.cloudflarestorage.com",
        },
        VaultProvider.MinIO => new VaultEndpoint
        {
            Provider = p, Bucket = bucket, Region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region, PathStyle = true, AccessKey = accessKey, SecretKey = secretKey,
            ServiceUrl = string.IsNullOrWhiteSpace(accountOrUrl) ? "http://127.0.0.1:9000" : accountOrUrl,
        },
        _ => new VaultEndpoint
        {
            Provider = p, Bucket = bucket, Region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region, PathStyle = false, AccessKey = accessKey, SecretKey = secretKey,
            ServiceUrl = string.IsNullOrWhiteSpace(accountOrUrl) || !accountOrUrl.Contains("://", StringComparison.Ordinal) ? "https://s3." + (string.IsNullOrWhiteSpace(region) ? "us-east-1" : region) + ".amazonaws.com" : accountOrUrl,
        },
    };

    /// <summary>환경변수 → 엔드포인트(필수 키 누락 시 null).</summary>
    public static VaultEndpoint? FromEnvironment()
    {
        var bucket = Environment.GetEnvironmentVariable(EnvBucket); var ak = Environment.GetEnvironmentVariable(EnvAccess); var sk = Environment.GetEnvironmentVariable(EnvSecret);
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(sk)) return null;
        var p = (Environment.GetEnvironmentVariable(EnvProvider) ?? "").Trim().ToLowerInvariant() switch { "r2" or "cloudflare" => VaultProvider.CloudflareR2, "minio" => VaultProvider.MinIO, _ => VaultProvider.AwsS3 };
        return Preset(p, Environment.GetEnvironmentVariable(EnvUrl) ?? "", bucket.Trim(), Environment.GetEnvironmentVariable(EnvRegion) ?? "", ak.Trim(), sk.Trim());
    }

    public bool Validate(out string error)
    {
        error = "";
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) error = "엔드포인트 URL 형식 오류";
        else if (Bucket.Length is < 3 or > 63 || !IsDnsBucket(Bucket)) error = "버킷 이름은 3~63 자 소문자/숫자/-/.";
        else if (string.IsNullOrWhiteSpace(Region)) error = "리전 누락";
        else if (AccessKey.Length < 3 || SecretKey.Length < 8) error = "액세스 키/시크릿 누락";
        else if (u.Scheme == Uri.UriSchemeHttp && Provider != VaultProvider.MinIO) error = "AWS/R2 는 HTTPS 필수";
        return error.Length == 0;
    }

    private static bool IsDnsBucket(string b)
    {
        foreach (var ch in b) if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')) return false;
        return b[0] is not ('-' or '.') && b[^1] is not ('-' or '.');
    }

    public static string ProviderName(VaultProvider p) => p switch { VaultProvider.CloudflareR2 => "Cloudflare R2", VaultProvider.MinIO => "MinIO", _ => "AWS S3" };

    /// <summary>시크릿 마스킹 표기.</summary>
    public override string ToString() => ProviderName(Provider) + " " + Bucket + " @" + Region + " (" + (AccessKey.Length > 4 ? AccessKey.Substring(0, 4) + "****" : "****") + ")";
}

public readonly record struct SigV4Header(string Name, string Value);

/// <summary>
/// v17.87.0 AWS Signature V4(HMAC-SHA256) 순수 BCL 서명기 — 정규 요청/서명 문자열을 인스턴스 버퍼에 조립 →
/// <see cref="SHA256.HashData(ReadOnlySpan{byte}, Span{byte})"/> · <see cref="HMACSHA256.HashData(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte})"/> 원샷 →
/// Authorization 값을 호출자 Span 에 기록(할당 0). 헤더는 소문자 이름 · 사전순 정렬 전제.
/// </summary>
public sealed class SigV4Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256", EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", UnsignedPayload = "UNSIGNED-PAYLOAD";

    private readonly char[] _c = new char[8192];
    private readonly byte[] _b = new byte[24 * 1024];
    private readonly object _gate = new();

    public long Signatures { get; private set; }

    /// <summary>서명 → <paramref name="authorization"/> 길이 반환(서명 hex 64 자는 <paramref name="signatureHex"/>).</summary>
    public int Sign(ReadOnlySpan<char> method, ReadOnlySpan<char> canonicalUri, ReadOnlySpan<char> canonicalQuery, ReadOnlySpan<SigV4Header> headers,
        ReadOnlySpan<char> payloadHash, ReadOnlySpan<char> amzDate, ReadOnlySpan<char> region, ReadOnlySpan<char> service,
        ReadOnlySpan<char> accessKey, ReadOnlySpan<char> secretKey, Span<char> authorization, Span<char> signatureHex)
    {
        lock (_gate)
        {
            Span<byte> hash = stackalloc byte[32];
            Span<byte> k1 = stackalloc byte[32];
            Span<byte> k2 = stackalloc byte[32];
            Span<byte> tmp = stackalloc byte[160];
            var c = _c.AsSpan(); var n = 0;

            // 1) 정규 요청
            Put(c, ref n, method); Put(c, ref n, '\n');
            Put(c, ref n, canonicalUri.IsEmpty ? "/" : canonicalUri); Put(c, ref n, '\n');
            Put(c, ref n, canonicalQuery); Put(c, ref n, '\n');
            for (var i = 0; i < headers.Length; i++) { Put(c, ref n, headers[i].Name); Put(c, ref n, ':'); Put(c, ref n, headers[i].Value.AsSpan().Trim()); Put(c, ref n, '\n'); }
            Put(c, ref n, '\n');
            var shStart = n; SignedHeaders(c, ref n, headers); var shLen = n - shStart;
            Put(c, ref n, '\n'); Put(c, ref n, payloadHash);
            var len = Encoding.UTF8.GetBytes(c.Slice(0, n), _b);
            SHA256.HashData(_b.AsSpan(0, len), hash);

            // 2) 서명 문자열(정규 요청 뒤에 이어서 조립 — SignedHeaders 원문 보존)
            var s0 = n;
            Put(c, ref n, Algorithm); Put(c, ref n, '\n'); Put(c, ref n, amzDate); Put(c, ref n, '\n');
            var scope0 = n;
            Put(c, ref n, amzDate.Slice(0, 8)); Put(c, ref n, '/'); Put(c, ref n, region); Put(c, ref n, '/'); Put(c, ref n, service); Put(c, ref n, "/aws4_request");
            var scopeLen = n - scope0;
            Put(c, ref n, '\n');
            Hex(hash, c.Slice(n, 64)); n += 64;
            len = Encoding.UTF8.GetBytes(c.Slice(s0, n - s0), _b);

            // 3) 서명 키 체인 kSecret → kDate → kRegion → kService → kSigning
            "AWS4".AsSpan().CopyTo(c.Slice(n)); secretKey.CopyTo(c.Slice(n + 4));
            var kl = Encoding.UTF8.GetBytes(c.Slice(n, 4 + secretKey.Length), tmp);
            HMACSHA256.HashData(tmp.Slice(0, kl), Ascii(amzDate.Slice(0, 8), tmp.Slice(kl)), k1);
            c.Slice(n, 4 + secretKey.Length).Clear(); tmp.Clear();
            HMACSHA256.HashData(k1, Ascii(region, tmp), k2);
            HMACSHA256.HashData(k2, Ascii(service, tmp), k1);
            HMACSHA256.HashData(k1, Ascii("aws4_request", tmp), k2);
            HMACSHA256.HashData(k2, _b.AsSpan(0, len), hash);
            k1.Clear(); k2.Clear();
            Hex(hash, signatureHex);

            // 4) Authorization
            var a = 0;
            Put(authorization, ref a, Algorithm); Put(authorization, ref a, " Credential="); Put(authorization, ref a, accessKey); Put(authorization, ref a, '/');
            Put(authorization, ref a, c.Slice(scope0, scopeLen)); Put(authorization, ref a, ", SignedHeaders="); Put(authorization, ref a, c.Slice(shStart, shLen));
            Put(authorization, ref a, ", Signature="); Put(authorization, ref a, signatureHex.Slice(0, 64));
            Signatures++;
            return a;
        }
    }

    private static void SignedHeaders(Span<char> c, ref int n, ReadOnlySpan<SigV4Header> headers)
    {
        for (var i = 0; i < headers.Length; i++) { if (i > 0) Put(c, ref n, ';'); Put(c, ref n, headers[i].Name); }
    }

    private static ReadOnlySpan<byte> Ascii(ReadOnlySpan<char> s, Span<byte> dst)
    {
        for (var i = 0; i < s.Length; i++) dst[i] = (byte)s[i];
        return dst.Slice(0, s.Length);
    }

    private static void Put(Span<char> d, ref int n, ReadOnlySpan<char> s) { s.CopyTo(d.Slice(n)); n += s.Length; }

    private static void Put(Span<char> d, ref int n, char ch) => d[n++] = ch;

    public static void Hex(ReadOnlySpan<byte> b, Span<char> dst)
    {
        const string h = "0123456789abcdef";
        for (var i = 0; i < b.Length; i++) { dst[2 * i] = h[b[i] >> 4]; dst[2 * i + 1] = h[b[i] & 0xF]; }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32]; Span<char> x = stackalloc char[64];
        SHA256.HashData(data, h); Hex(h, x);
        return new string(x);
    }

    /// <summary>RFC 3986 URI 인코딩(비예약 문자 외 %XX 대문자 · <paramref name="keepSlash"/> 시 '/' 유지) — 반환 = 기록 길이.</summary>
    public static int UriEncode(ReadOnlySpan<char> s, Span<char> dst, bool keepSlash)
    {
        const string h = "0123456789ABCDEF";
        Span<byte> u = stackalloc byte[4];
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~' || (keepSlash && ch == '/')) { dst[n++] = ch; continue; }
            var len = char.IsHighSurrogate(ch) && i + 1 < s.Length ? Encoding.UTF8.GetBytes(s.Slice(i++, 2), u) : Encoding.UTF8.GetBytes(s.Slice(i, 1), u);
            for (var k = 0; k < len; k++) { dst[n++] = '%'; dst[n++] = h[u[k] >> 4]; dst[n++] = h[u[k] & 0xF]; }
        }
        return n;
    }

    public static string UriEncode(string s, bool keepSlash)
    {
        var buf = s.Length <= 256 ? stackalloc char[s.Length * 12] : new char[s.Length * 12];
        return new string(buf.Slice(0, UriEncode(s, buf, keepSlash)));
    }
}

/// <summary>
/// v17.87.0 업로드 대역폭 셰이핑 밸브(토큰 버킷 · 부채 허용) — 상한 Mbps 초과분은 부채 시간만큼 대기 →
/// 송출(RTMPS) 업링크 정체 방지. <see cref="Reserve"/> 는 가상 시계 결정론(무할당).
/// </summary>
public sealed class BandwidthThrottleValve
{
    public const double DefaultMbps = 20;
    private readonly object _gate = new();
    private double _tokens;
    private long _lastUs = -1;
    private double _mbps = DefaultMbps;

    /// <summary>상한(Mbps · 0 = 무제한).</summary>
    public double Mbps { get { lock (_gate) return _mbps; } set { lock (_gate) { _mbps = Math.Max(0, value); _tokens = Math.Min(_tokens, BurstBytes); } } }
    public double BytesPerSec => Mbps * 125_000;
    /// <summary>순간 버스트 허용(50 ms 분 · 최소 64 KiB).</summary>
    public double BurstBytes => Math.Max(64 * 1024, _mbps * 125_000 / 20);
    public long ThrottledWaits { get; private set; }
    public long TotalWaitUs { get; private set; }
    public long PassedBytes { get; private set; }

    /// <summary>bytes 송신 예약 → 필요한 대기 µs(0 = 즉시).</summary>
    public long Reserve(int bytes, long nowUs)
    {
        lock (_gate)
        {
            PassedBytes += bytes;
            if (_mbps <= 0) return 0;
            var rate = _mbps * 0.125;   // bytes/µs
            if (_lastUs < 0) { _lastUs = nowUs; _tokens = BurstBytes; }
            _tokens = Math.Min(BurstBytes, _tokens + (nowUs - _lastUs) * rate);
            _lastUs = nowUs;
            _tokens -= bytes;
            if (_tokens >= 0) return 0;
            var wait = (long)Math.Ceiling(-_tokens / rate);
            ThrottledWaits++; TotalWaitUs += wait;
            return wait;
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var wait = Reserve(bytes, Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
        if (wait > 0) await Task.Delay(TimeSpan.FromTicks(wait * 10), ct).ConfigureAwait(false);
    }

    public void Reset() { lock (_gate) { _tokens = 0; _lastUs = -1; ThrottledWaits = 0; TotalWaitUs = 0; PassedBytes = 0; } }
}

public struct VaultUploadResult
{
    public bool Ok;
    public int Parts, Attempts;
    public long Bytes;
    public double Ms;
    public string? ETag, Error, UploadId;
}

/// <summary>
/// [PromptFlow Studio v17.87.0] ☁️ S3/R2/MinIO 멀티클라우드 볼트 게이트웨이 — 외부 SDK 없이 HttpClient + <see cref="SigV4Signer"/>.
///   ▸ 멀티파트: Initiate(POST ?uploads · x-amz-tagging/스토리지 클래스) → UploadPart(PUT 8 MB · 파트별 SHA-256 페이로드 서명 · ETag) → Complete(POST XML).
///     실패 시 Abort(DELETE) 로 고아 파트 과금 차단.
///   ▸ 요청별 타임아웃 · 5xx/408/429/네트워크 오류 지수 백오프 재시도(최대 3 회) · 4xx 즉시 실패 · 응답 XML 즉시 검증.
///   ▸ 파트 버퍼 1 회 확보 재사용 · 64 KiB 청크마다 <see cref="BandwidthThrottleValve"/> 통과(기본 20 Mbps 상한).
///   ▸ 리허설: 내장 <see cref="SimulatedS3VaultHandler"/>(SigV4 서명/페이로드 해시 실검증 인메모리 버킷).
/// </summary>
public sealed class MultiCloudVaultArchivalGateway : IDisposable
{
    public const int DefaultPartBytes = 8 * 1024 * 1024, MinPartBytes = 64 * 1024, ChunkBytes = 64 * 1024, MaxRetries = 3;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient? _fixed;
    private readonly HttpClient _simClient;
    private HttpClient? _realClient;
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private readonly char[] _auth = new char[1024];
    private readonly char[] _sig = new char[64];
    private byte[] _part;
    private long _curBytes, _curTotal;

    public MultiCloudVaultArchivalGateway(HttpMessageHandler? transport = null, int partBytes = DefaultPartBytes)
    {
        if (transport is not null) _fixed = new HttpClient(transport, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _simClient = new HttpClient(Simulator, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _part = new byte[Math.Max(MinPartBytes, partBytes)];
    }

    public SigV4Signer Signer { get; } = new();
    public BandwidthThrottleValve Valve { get; } = new();
    public SimulatedS3VaultHandler Simulator { get; } = new();
    public VaultEndpoint? Endpoint { get; private set; }
    /// <summary>리허설 모드 — 내장 시뮬레이터 버킷으로 송신(외부 전송기 주입 시 무시).</summary>
    public bool UseSimulator { get; set; } = true;
    public Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;
    public int PartBytes => _part.Length;
    public long Objects { get; private set; }
    public long PartsUploaded { get; private set; }
    public long BytesUploaded { get; private set; }
    public long Retries { get; private set; }
    public long Failures { get; private set; }
    public long Aborts { get; private set; }
    public double LastMs { get; private set; }
    public double LastMbps { get; private set; }
    public string? LastError { get; private set; }
    public bool? Connected { get; private set; }
    public long CurrentBytes => Volatile.Read(ref _curBytes);
    public long CurrentTotal => Volatile.Read(ref _curTotal);
    public float CurrentProgress { get { var t = CurrentTotal; return t > 0 ? Math.Clamp(CurrentBytes / (float)t, 0f, 1f) : 0f; } }

    /// <summary>파트 크기 변경(S3 는 마지막 제외 ≥ 5 MiB · 리허설/테스트는 64 KiB 까지 허용).</summary>
    public void SetPartBytes(int bytes)
    {
        bytes = Math.Max(MinPartBytes, bytes);
        if (!_uploadGate.Wait(0)) return;
        try { if (_part.Length != bytes) _part = new byte[bytes]; }
        finally { _uploadGate.Release(); }
    }

    public bool Configure(VaultEndpoint ep, out string error)
    {
        if (!ep.Validate(out error)) return false;
        Endpoint = ep; Connected = null;
        if (UseSimulator) Simulator.Provision(ep);
        return true;
    }

    private HttpClient Client => _fixed ?? (UseSimulator ? _simClient : (_realClient ??= new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan }));

    // ══════════════════════ 공개 작업 ══════════════════════

    /// <summary>연결 테스트 — HEAD 버킷(서명 · 권한 · 네트워크 검증).</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var ep = Endpoint;
        if (ep is null) { LastError = "엔드포인트 미설정"; return false; }
        try
        {
            using var r = await SendAsync(ep, HttpMethod.Head, null, "", null, SigV4Signer.EmptySha256, null, null, ct).ConfigureAwait(false);
            Connected = r.IsSuccessStatusCode;
            LastError = Connected == true ? null : "HEAD 버킷 " + ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture);
            return Connected == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            Connected = false; LastError = ex.Message;
            return false;
        }
    }

    /// <summary>스트림 → 멀티파트 업로드(직렬 1 객체 · 파트 버퍼 재사용).</summary>
    public async Task<VaultUploadResult> UploadAsync(Stream source, string objectKey, StorageTier tier, CancellationToken ct = default)
    {
        var res = new VaultUploadResult();
        var ep = Endpoint;
        if (ep is null) { res.Error = LastError = "엔드포인트 미설정"; Failures++; return res; }
        await _uploadGate.WaitAsync(ct).ConfigureAwait(false);
        var t0 = Stopwatch.GetTimestamp();
        string? uploadId = null;
        try
        {
            Volatile.Write(ref _curBytes, 0); Volatile.Write(ref _curTotal, source.CanSeek ? source.Length : 0);
            var cls = StorageCostLifecycleTieringGuard.StorageClass(ep.Provider, tier);
            using (var init = await SendAsync(ep, HttpMethod.Post, objectKey, "uploads=", null, SigV4Signer.EmptySha256,
                new SigV4Header("x-amz-tagging", StorageCostLifecycleTieringGuard.TaggingHeader(tier)), cls is null ? null : new SigV4Header("x-amz-storage-class", cls), ct).ConfigureAwait(false))
            {
                var body = await init.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!init.IsSuccessStatusCode) return Fail(ref res, "Initiate " + ((int)init.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + XmlValue(body, "Code"));
                uploadId = XmlValue(body, "UploadId");
                if (string.IsNullOrEmpty(uploadId)) return Fail(ref res, "Initiate 응답 스키마 오류(UploadId 없음)");
            }
            res.UploadId = uploadId;
            var etags = new List<string>(8);
            var encId = SigV4Signer.UriEncode(uploadId, false);
            while (true)
            {
                var n = await source.ReadAtLeastAsync(_part, _part.Length, false, ct).ConfigureAwait(false);
                if (n == 0 && etags.Count > 0) break;
                var hash = SigV4Signer.Sha256Hex(_part.AsSpan(0, n));
                var q = "partNumber=" + (etags.Count + 1).ToString(CultureInfo.InvariantCulture) + "&uploadId=" + encId;
                var len = n;
                using var put = await SendAsync(ep, HttpMethod.Put, objectKey, q, () => new ThrottledPartContent(_part, len, Valve, this), hash, null, null, ct).ConfigureAwait(false);
                if (!put.IsSuccessStatusCode) { Volatile.Write(ref _curBytes, res.Bytes); return await AbortFail(ep, objectKey, encId, res, "UploadPart " + ((int)put.StatusCode).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false); }
                var etag = put.Headers.ETag?.Tag ?? (put.Headers.TryGetValues("ETag", out var ev) ? string.Join("", ev) : null);
                if (string.IsNullOrEmpty(etag)) return await AbortFail(ep, objectKey, encId, res, "UploadPart 응답 스키마 오류(ETag 없음)", ct).ConfigureAwait(false);
                etags.Add(etag);
                res.Bytes += n; res.Parts++; PartsUploaded++; BytesUploaded += n;
                Volatile.Write(ref _curBytes, res.Bytes);
                if (n < _part.Length) break;
            }
            var xml = new StringBuilder(64 + etags.Count * 96).Append("<CompleteMultipartUpload>");
            for (var i = 0; i < etags.Count; i++) xml.Append("<Part><PartNumber>").Append(i + 1).Append("</PartNumber><ETag>").Append(etags[i]).Append("</ETag></Part>");
            var payload = Encoding.UTF8.GetBytes(xml.Append("</CompleteMultipartUpload>").ToString());
            using (var done = await SendAsync(ep, HttpMethod.Post, objectKey, "uploadId=" + encId, () => Xml(payload), SigV4Signer.Sha256Hex(payload), null, null, ct).ConfigureAwait(false))
            {
                var body = await done.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!done.IsSuccessStatusCode || body.Contains("<Error>", StringComparison.Ordinal)) return await AbortFail(ep, objectKey, encId, res, "Complete " + ((int)done.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + XmlValue(body, "Code"), ct).ConfigureAwait(false);
                res.ETag = XmlValue(body, "ETag");
                if (string.IsNullOrEmpty(res.ETag)) return Fail(ref res, "Complete 응답 스키마 오류(ETag 없음)");
            }
            res.Ok = true; Objects++; LastError = null; Connected = true;
            return res;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (uploadId is not null) await TryAbort(ep, objectKey, SigV4Signer.UriEncode(uploadId, false)).ConfigureAwait(false);
            return Fail(ref res, ex is OperationCanceledException ? "취소/타임아웃" : ex.Message);
        }
        finally
        {
            res.Ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            LastMs = res.Ms;
            if (res.Ok && res.Ms > 0) LastMbps = res.Bytes * 8 / 1000.0 / res.Ms;
            Volatile.Write(ref _curTotal, 0);
            _uploadGate.Release();
        }
    }

    /// <summary>PutObjectTagging — 수명주기 티어 태그 갱신.</summary>
    public async Task<bool> PutTaggingAsync(string objectKey, StorageTier tier, CancellationToken ct = default)
    {
        var ep = Endpoint;
        if (ep is null) return false;
        var payload = Encoding.UTF8.GetBytes(StorageCostLifecycleTieringGuard.TaggingXml(tier));
        try
        {
            using var r = await SendAsync(ep, HttpMethod.Put, objectKey, "tagging=", () => Xml(payload), SigV4Signer.Sha256Hex(payload), null, null, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) LastError = "Tagging " + ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { LastError = ex.Message; return false; }
    }

    /// <summary>PutBucketLifecycleConfiguration(Content-MD5 필수).</summary>
    public async Task<bool> ApplyLifecycleAsync(string xml, CancellationToken ct = default)
    {
        var ep = Endpoint;
        if (ep is null) return false;
        var payload = Encoding.UTF8.GetBytes(xml);
        try
        {
            using var r = await SendAsync(ep, HttpMethod.Put, null, "lifecycle=", () => { var c = Xml(payload); c.Headers.ContentMD5 = MD5.HashData(payload); return c; }, SigV4Signer.Sha256Hex(payload), null, null, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) LastError = "Lifecycle " + ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { LastError = ex.Message; return false; }
    }

    // ══════════════════════ 서명 송신 · 재시도 ══════════════════════

    private async Task<HttpResponseMessage> SendAsync(VaultEndpoint ep, HttpMethod method, string? key, string query, Func<HttpContent>? content, string payloadHash,
        SigV4Header? extra1, SigV4Header? extra2, CancellationToken ct)
    {
        var baseUri = new Uri(ep.ServiceUrl);
        var encKey = key is null ? null : SigV4Signer.UriEncode(key, true);
        string host, path;
        if (ep.PathStyle) { host = baseUri.Authority; path = "/" + ep.Bucket + (encKey is null ? "" : "/" + encKey); }
        else { host = ep.Bucket + "." + baseUri.Authority; path = "/" + (encKey ?? ""); }
        var url = baseUri.Scheme + "://" + host + path + (query.Length > 0 ? "?" + query : "");

        for (var attempt = 0; ; attempt++)
        {
            var amzDate = Clock().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var hs = new SigV4Header[3 + (extra1 is null ? 0 : 1) + (extra2 is null ? 0 : 1)];
            var k = 0;
            hs[k++] = new SigV4Header("host", host);
            hs[k++] = new SigV4Header("x-amz-content-sha256", payloadHash);
            hs[k++] = new SigV4Header("x-amz-date", amzDate);
            if (extra2 is { } e2) hs[k++] = e2;   // x-amz-storage-class < x-amz-tagging
            if (extra1 is { } e1) hs[k++] = e1;
            Array.Sort(hs, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var req = new HttpRequestMessage(method, url);
            int alen;
            lock (_auth) alen = Signer.Sign(method.Method, path, query, hs, payloadHash, amzDate, ep.Region, "s3", ep.AccessKey, ep.SecretKey, _auth, _sig);
            req.Headers.TryAddWithoutValidation("Authorization", new string(_auth, 0, alen));
            for (var i = 1; i < hs.Length; i++) req.Headers.TryAddWithoutValidation(hs[i].Name, hs[i].Value);
            req.Content = content?.Invoke();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            HttpResponseMessage? resp = null;
            try
            {
                resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code < 500 && code != 408 && code != 429 || attempt >= MaxRetries) return resp;
                resp.Dispose(); resp = null;
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                LastError = ex.Message;
            }
            finally { req.Dispose(); }
            Retries++;
            await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), ct).ConfigureAwait(false);
        }
    }

    private static ByteArrayContent Xml(byte[] payload)
    {
        var c = new ByteArrayContent(payload);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return c;
    }

    private VaultUploadResult Fail(ref VaultUploadResult res, string error)
    {
        res.Ok = false; res.Error = LastError = error; Failures++;
        return res;
    }

    private async Task<VaultUploadResult> AbortFail(VaultEndpoint ep, string key, string encId, VaultUploadResult res, string error, CancellationToken ct)
    {
        _ = ct;
        await TryAbort(ep, key, encId).ConfigureAwait(false);
        return Fail(ref res, error);
    }

    private async Task TryAbort(VaultEndpoint ep, string key, string encId)
    {
        try
        {
            using var r = await SendAsync(ep, HttpMethod.Delete, key, "uploadId=" + encId, null, SigV4Signer.EmptySha256, null, null, CancellationToken.None).ConfigureAwait(false);
            Aborts++;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { PromptFlowStudio.Program.Log("LiveArchive.Abort", ex); }
    }

    /// <summary>응답 XML 단일 요소 값(스팬 검색 · 따옴표 엔티티 복원).</summary>
    public static string? XmlValue(string xml, string tag)
    {
        var open = "<" + tag + ">";
        var i = xml.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return null;
        i += open.Length;
        var j = xml.IndexOf("</" + tag + ">", i, StringComparison.Ordinal);
        return j < 0 ? null : xml.Substring(i, j - i).Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&#34;", "\"", StringComparison.Ordinal);
    }

    internal void OnChunkSent(int n) => Interlocked.Add(ref _curBytes, n);

    public void ResetStats()
    {
        Objects = PartsUploaded = BytesUploaded = Retries = Failures = Aborts = 0;
        LastMs = LastMbps = 0; LastError = null; Connected = null;
        Valve.Reset();
    }

    public void Dispose()
    {
        _fixed?.Dispose(); _simClient.Dispose(); _realClient?.Dispose();
        _uploadGate.Dispose();
        Simulator.Dispose();
    }

    /// <summary>파트 본문 — 64 KiB 청크마다 대역폭 밸브 통과 후 전송(버퍼 재사용 · 진행률 보고).</summary>
    private sealed class ThrottledPartContent : HttpContent
    {
        private readonly byte[] _buf; private readonly int _len; private readonly BandwidthThrottleValve _valve; private readonly MultiCloudVaultArchivalGateway _owner;

        public ThrottledPartContent(byte[] buf, int len, BandwidthThrottleValve valve, MultiCloudVaultArchivalGateway owner)
        {
            _buf = buf; _len = len; _valve = valve; _owner = owner;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var off = 0; off < _len; off += ChunkBytes)
            {
                var n = Math.Min(ChunkBytes, _len - off);
                await _valve.WaitAsync(n, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(_buf.AsMemory(off, n), cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length) { length = _len; return true; }
    }
}

/// <summary>v17.87.0 시뮬레이터 버킷 오브젝트(본문은 완료 시 해시/길이만 보존).</summary>
public sealed class SimulatedObject
{
    public string Key = "";
    public long Length;
    public int Parts;
    public ulong Fnv;
    public string Sha256 = "";
    public string? StorageClass;
    public string Tags = "";
    public string ETag = "";
}

/// <summary>
/// v17.87.0 인메모리 S3 호환 버킷 핸들러(리허설 · 테스트) — Authorization SigV4 재계산 대조(불일치 403 SignatureDoesNotMatch) ·
/// x-amz-content-sha256 실본문 대조 · 멀티파트 Initiate/UploadPart/Complete/Abort · 태깅 · 수명주기(Content-MD5) · HEAD 버킷.
/// </summary>
public sealed class SimulatedS3VaultHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly SigV4Signer _verify = new();
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimulatedObject> _objects = new(StringComparer.Ordinal);
    private readonly char[] _auth = new char[1024], _sig = new char[64];
    private VaultEndpoint? _ep;
    private int _seq;

    public int FailNextParts { get; set; }
    public long Requests { get; private set; }
    public long SignatureRejects { get; private set; }
    public long HashRejects { get; private set; }
    public long Aborted { get; private set; }
    public string? Lifecycle { get; private set; }
    public int ObjectCount { get { lock (_gate) return _objects.Count; } }
    public int PendingUploads { get { lock (_gate) return _uploads.Count; } }

    /// <summary>버킷/자격 등록(시뮬레이터가 신뢰하는 단일 키 쌍).</summary>
    public void Provision(VaultEndpoint ep) { lock (_gate) _ep = ep; }

    public bool TryGetObject(string key, out SimulatedObject obj) { lock (_gate) return _objects.TryGetValue(key, out obj!); }

    public void Clear() { lock (_gate) { _uploads.Clear(); _objects.Clear(); Lifecycle = null; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) return Handle(request, body);
    }

    private HttpResponseMessage Handle(HttpRequestMessage req, byte[] body)
    {
        Requests++;
        var ep = _ep;
        var uri = req.RequestUri!;
        if (ep is null) return Error(HttpStatusCode.NotFound, "NoSuchBucket");
        var path = uri.AbsolutePath; var query = uri.Query.Length > 0 ? uri.Query.Substring(1) : "";
        var host = uri.Authority;

        // ── SigV4 검증 ──
        var auth = req.Headers.TryGetValues("Authorization", out var av) ? string.Join("", av) : "";
        var date = Header(req, "x-amz-date"); var sha = Header(req, "x-amz-content-sha256");
        var shI = auth.IndexOf("SignedHeaders=", StringComparison.Ordinal); var sgI = auth.IndexOf("Signature=", StringComparison.Ordinal);
        if (date is null || sha is null || shI < 0 || sgI < 0 || !auth.Contains("Credential=" + ep.AccessKey + "/", StringComparison.Ordinal)) { SignatureRejects++; return Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId"); }
        var names = auth.Substring(shI + 14, auth.IndexOf(',', shI) - shI - 14).Split(';');
        var hs = new SigV4Header[names.Length];
        for (var i = 0; i < names.Length; i++) hs[i] = new SigV4Header(names[i], names[i] == "host" ? host : Header(req, names[i]) ?? "");
        _verify.Sign(req.Method.Method, path, query, hs, sha, date, ep.Region, "s3", ep.AccessKey, ep.SecretKey, _auth, _sig);
        if (!CryptographicOperations.FixedTimeEquals(MemoryMarshalBytes(auth.AsSpan(sgI + 10)), MemoryMarshalBytes(_sig))) { SignatureRejects++; return Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch"); }
        if (sha != SigV4Signer.UnsignedPayload && !string.Equals(sha, SigV4Signer.Sha256Hex(body), StringComparison.Ordinal)) { HashRejects++; return Error(HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch"); }

        // ── 버킷/키 분해 ──
        string bucket, key;
        if (host.StartsWith(ep.Bucket + ".", StringComparison.Ordinal)) { bucket = ep.Bucket; key = Uri.UnescapeDataString(path.TrimStart('/')); }
        else
        {
            var p = path.TrimStart('/'); var s = p.IndexOf('/');
            bucket = s < 0 ? p : p.Substring(0, s); key = s < 0 ? "" : Uri.UnescapeDataString(p.Substring(s + 1));
        }
        if (!string.Equals(bucket, ep.Bucket, StringComparison.Ordinal)) return Error(HttpStatusCode.NotFound, "NoSuchBucket");
        var m = req.Method;

        if (m == HttpMethod.Head && key.Length == 0) return new HttpResponseMessage(HttpStatusCode.OK);
        if (m == HttpMethod.Put && key.Length == 0 && query == "lifecycle=")
        {
            var md5 = req.Content?.Headers.ContentMD5;
            if (md5 is null || !md5.AsSpan().SequenceEqual(MD5.HashData(body))) return Error(HttpStatusCode.BadRequest, "InvalidDigest");
            Lifecycle = Encoding.UTF8.GetString(body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        if (key.Length == 0) return Error(HttpStatusCode.BadRequest, "InvalidRequest");

        if (m == HttpMethod.Post && query == "uploads=")
        {
            var id = "pfs-" + (++_seq).ToString("D6", CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N").Substring(0, 12);
            _uploads[id] = new Upload { Key = key, StorageClass = Header(req, "x-amz-storage-class"), Tags = Header(req, "x-amz-tagging") ?? "" };
            return Xml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><InitiateMultipartUploadResult><Bucket>" + bucket + "</Bucket><Key>" + key + "</Key><UploadId>" + id + "</UploadId></InitiateMultipartUploadResult>");
        }
        var uploadId = QueryValue(query, "uploadId");
        if (m == HttpMethod.Put && uploadId is not null && int.TryParse(QueryValue(query, "partNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pn))
        {
            if (FailNextParts > 0) { FailNextParts--; return Error(HttpStatusCode.ServiceUnavailable, "SlowDown"); }
            if (!_uploads.TryGetValue(uploadId, out var up)) return Error(HttpStatusCode.NotFound, "NoSuchUpload");
            var etag = "\"" + Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant() + "\"";
            up.Parts[pn] = (body, etag);
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.ETag = new EntityTagHeaderValue(etag);
            return r;
        }
        if (m == HttpMethod.Post && uploadId is not null)
        {
            if (!_uploads.TryGetValue(uploadId, out var up)) return Error(HttpStatusCode.NotFound, "NoSuchUpload");
            var xml = Encoding.UTF8.GetString(body);
            var obj = new SimulatedObject { Key = up.Key, StorageClass = up.StorageClass, Tags = up.Tags };
            ulong h = 14695981039346656037UL; var n = 1; var at = 0;
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                var i = xml.IndexOf("<ETag>", at, StringComparison.Ordinal);
                if (i < 0) break;
                var j = xml.IndexOf("</ETag>", i, StringComparison.Ordinal);
                var tag = xml.Substring(i + 6, j - i - 6);
                if (!up.Parts.TryGetValue(n, out var part) || part.ETag != tag) return Error(HttpStatusCode.BadRequest, "InvalidPart");
                foreach (var b in part.Data) h = (h ^ b) * 1099511628211UL;
                sha256.AppendData(part.Data);
                obj.Length += part.Data.Length; obj.Parts++; n++; at = j;
            }
            if (obj.Parts == 0 || obj.Parts != up.Parts.Count) return Error(HttpStatusCode.BadRequest, "InvalidPartOrder");
            obj.Fnv = h; obj.Sha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            obj.ETag = "\"" + obj.Sha256.Substring(0, 32) + "-" + obj.Parts.ToString(CultureInfo.InvariantCulture) + "\"";
            _objects[up.Key] = obj; _uploads.Remove(uploadId);
            return Xml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CompleteMultipartUploadResult><Bucket>" + bucket + "</Bucket><Key>" + up.Key + "</Key><ETag>" + obj.ETag.Replace("\"", "&quot;", StringComparison.Ordinal) + "</ETag></CompleteMultipartUploadResult>");
        }
        if (m == HttpMethod.Delete && uploadId is not null) { if (_uploads.Remove(uploadId)) Aborted++; return new HttpResponseMessage(HttpStatusCode.NoContent); }
        if (m == HttpMethod.Put && query == "tagging=")
        {
            if (!_objects.TryGetValue(key, out var obj)) return Error(HttpStatusCode.NotFound, "NoSuchKey");
            var xml = Encoding.UTF8.GetString(body);
            var tier = xml.Contains("<Value>deep-archive</Value>", StringComparison.Ordinal) ? "deep-archive" : xml.Contains("<Value>glacier</Value>", StringComparison.Ordinal) ? "glacier" : xml.Contains("<Value>infrequent-access</Value>", StringComparison.Ordinal) ? "infrequent-access" : "hot";
            obj.Tags = StorageCostLifecycleTieringGuard.ArchiveTagKey + "=live&" + StorageCostLifecycleTieringGuard.TagKey + "=" + tier;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        return Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    private static ReadOnlySpan<byte> MemoryMarshalBytes(ReadOnlySpan<char> s) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Length >= 64 ? s.Slice(0, 64) : s);

    private static string? Header(HttpRequestMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content is not null && r.Content.Headers.TryGetValues(name, out var cv) ? string.Join(",", cv) : null;

    private static string? QueryValue(string q, string name)
    {
        foreach (var kv in q.Split('&'))
        {
            var e = kv.IndexOf('=');
            if (e > 0 && string.Equals(kv.Substring(0, e), name, StringComparison.Ordinal)) return Uri.UnescapeDataString(kv.Substring(e + 1));
        }
        return null;
    }

    private static HttpResponseMessage Xml(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Error(HttpStatusCode code, string err) =>
        new(code) { Content = new StringContent("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>" + err + "</Code></Error>", Encoding.UTF8, "application/xml") };

    private sealed class Upload
    {
        public string Key = "";
        public string? StorageClass;
        public string Tags = "";
        public readonly Dictionary<int, (byte[] Data, string ETag)> Parts = new();
    }
}
