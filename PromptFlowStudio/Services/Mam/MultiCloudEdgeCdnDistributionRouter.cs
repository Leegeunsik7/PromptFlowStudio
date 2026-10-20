using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Mam;

public enum CdnProvider : byte { CloudFront, CloudflareStream, Fastly }

public sealed record EdgePop(string Id, CdnProvider Provider, string City, string Country, double Lat, double Lon, double CapacityGbps, double CostPerGb, string Host);

/// <summary>지오 라우팅 결정(값형) — Pop = -1 이면 차단/불가.</summary>
public readonly record struct RouteDecision(int Pop, double Score, double EstRttMs, bool Blocked, ComplianceRegion? Region);

public sealed record PublishStep(CdnProvider Provider, bool Ok, bool DryRun, int Attempts, string Message);

public sealed record PublishResult(string AssetId, bool Ok, bool PartialRescue, IReadOnlyList<PublishStep> Steps, IReadOnlyList<ComplianceRegion> Regions, string PlaybackUrl);

/// <summary>
/// [PromptFlow Studio v16.42.0] 🌐 멀티클라우드 엣지 CDN 분배 &amp; DRM 라우터.
///   ▸ PoP 15개(CloudFront · Cloudflare Stream · Fastly) — 하버사인 거리 → 추정 RTT + 부하/오류/비용 페널티 · 국가 → 심의 지역 게이트(반려 지역 451 차단) · 무할당 라우팅.
///   ▸ 토큰 DRM: HMAC-SHA256(볼트 키) canonical "v1|필드수|경로길이|경로|만료|국가|kid" → 공급자별 쿼리 형식(CloudFront Expires/Signature/Key-Pair-Id · Cloudflare token=exp.sig · Fastly token=exp_hex) · FixedTimeEquals 검증.
///   ▸ 콘텐츠 키 HKDF 파생(자산별 AES-128 · KID) → HLS EXT-X-KEY(AES-128 · 키 URI 토큰) · DASH MPD ContentProtection(CENC · ClearKey/Widevine/PlayReady system ID).
///   ▸ 동적 매니페스트 오프로더: 루프백 HTTP(/play 마스터 재작성 · 변형 미디어 플레이리스트 · MPD · /keys · /edge 세그먼트 토큰 검증 · 트래픽 집계) — 마스터 재작성은 재사용 버퍼 무할당.
///   ▸ 1-Click 배포: 심의 승인 지역만 · 공급자 API(Cloudflare Stream copy · Fastly purge — 환경변수 자격 시 실호출 · 타임아웃/지수 백오프) · 자격 없음/CloudFront(SigV4 미구현)는 드라이런 [부분 구제].
/// </summary>
public sealed class MultiCloudEdgeCdnDistributionRouter : IDisposable
{
    public const int DefaultPort = 17864, SegmentBytes = 1_500_000, KeyTtlSec = 3600;
    public const string TokenKeyName = "cdn_token_hmac", DrmKeyName = "cdn_drm_master";
    public static readonly EdgePop[] DefaultPops =
    {
        new("cf-icn", CdnProvider.CloudFront, "서울", "KR", 37.57, 126.98, 400, 0.12, "d1pfs.cloudfront.net"),
        new("cf-nrt", CdnProvider.CloudFront, "도쿄", "JP", 35.68, 139.69, 500, 0.114, "d1pfs.cloudfront.net"),
        new("cf-iad", CdnProvider.CloudFront, "버지니아", "US", 38.95, -77.45, 900, 0.085, "d1pfs.cloudfront.net"),
        new("cf-gru", CdnProvider.CloudFront, "상파울루", "BR", -23.55, -46.63, 250, 0.11, "d1pfs.cloudfront.net"),
        new("cf-bom", CdnProvider.CloudFront, "뭄바이", "IN", 19.08, 72.88, 300, 0.109, "d1pfs.cloudfront.net"),
        new("cfs-fra", CdnProvider.CloudflareStream, "프랑크푸르트", "DE", 50.11, 8.68, 800, 0.05, "customer-pfs.cloudflarestream.com"),
        new("cfs-sin", CdnProvider.CloudflareStream, "싱가포르", "SG", 1.35, 103.82, 500, 0.05, "customer-pfs.cloudflarestream.com"),
        new("cfs-syd", CdnProvider.CloudflareStream, "시드니", "AU", -33.87, 151.21, 250, 0.05, "customer-pfs.cloudflarestream.com"),
        new("cfs-jnb", CdnProvider.CloudflareStream, "요하네스버그", "ZA", -26.2, 28.05, 150, 0.05, "customer-pfs.cloudflarestream.com"),
        new("cfs-dxb", CdnProvider.CloudflareStream, "두바이", "AE", 25.2, 55.27, 200, 0.05, "customer-pfs.cloudflarestream.com"),
        new("fy-lhr", CdnProvider.Fastly, "런던", "GB", 51.51, -0.13, 700, 0.08, "pfs.global.ssl.fastly.net"),
        new("fy-cdg", CdnProvider.Fastly, "파리", "FR", 48.86, 2.35, 500, 0.08, "pfs.global.ssl.fastly.net"),
        new("fy-sjc", CdnProvider.Fastly, "산호세", "US", 37.34, -121.89, 800, 0.08, "pfs.global.ssl.fastly.net"),
        new("fy-yyz", CdnProvider.Fastly, "토론토", "CA", 43.65, -79.38, 300, 0.08, "pfs.global.ssl.fastly.net"),
        new("fy-hkg", CdnProvider.Fastly, "홍콩", "HK", 22.32, 114.17, 400, 0.08, "pfs.global.ssl.fastly.net"),
    };

    private static readonly (double Lat, double Lon, string Cc, double Weight)[] Population =
    {
        (37.57, 126.98, "KR", 6), (35.18, 129.08, "KR", 2), (35.68, 139.69, "JP", 6), (34.69, 135.5, "JP", 3), (40.71, -74.0, "US", 7), (34.05, -118.24, "US", 5), (41.88, -87.63, "US", 3),
        (51.51, -0.13, "GB", 4), (48.86, 2.35, "FR", 3), (52.52, 13.4, "DE", 3), (40.42, -3.7, "ES", 2), (41.9, 12.5, "IT", 2), (-23.55, -46.63, "BR", 3), (19.43, -99.13, "MX", 2),
        (19.08, 72.88, "IN", 4), (1.35, 103.82, "SG", 2), (-33.87, 151.21, "AU", 2), (43.65, -79.38, "CA", 2), (25.2, 55.27, "AE", 1), (-26.2, 28.05, "ZA", 1), (22.32, 114.17, "HK", 2),
    };

    private readonly EdgePop[] _pops;
    private readonly double[] _rtt, _err, _load;
    private readonly long[] _requests, _bytes, _blockedByRegion = new long[AiContentModerationComplianceShield.Regions + 1];
    private readonly Func<byte[]> _tokenKeySrc, _drmKeySrc;
    private byte[]? _tokenKeyCache, _drmKeyCache;
    private byte[] TokenKey => _tokenKeyCache ??= _tokenKeySrc();
    private byte[] DrmKey => _drmKeyCache ??= _drmKeySrc();
    private readonly Func<string, string?> _cred;
    private readonly AiContentModerationComplianceShield? _shield;
    private readonly EnterpriseMamSmartIndexingEngine? _mam;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly ArrayBufferWriter<byte> _out = new(16 * 1024);
    private readonly Dictionary<string, (ComplianceRegion[] Rejected, int Approved)> _gates = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _tokensIssued, _tokensRejected, _manifests;
    private uint _rng = 0x13579BDFu;

    public MultiCloudEdgeCdnDistributionRouter(IServiceProvider sp)
        : this(sp.GetService(typeof(SecureSecretVault)) as SecureSecretVault, sp.GetService(typeof(AiContentModerationComplianceShield)) as AiContentModerationComplianceShield,
               sp.GetService(typeof(EnterpriseMamSmartIndexingEngine)) as EnterpriseMamSmartIndexingEngine) { }

    private MultiCloudEdgeCdnDistributionRouter(SecureSecretVault? vault, AiContentModerationComplianceShield? shield, EnterpriseMamSmartIndexingEngine? mam)
        : this(vault is null ? () => SHA256.HashData("pfs-cdn-dev"u8) : () => vault.ResolveOrCreate(TokenKeyName),
               vault is null ? () => SHA256.HashData("pfs-drm-dev"u8) : () => vault.ResolveOrCreate(DrmKeyName),
               vault is null ? _ => null : vault.TryGetExternalCredential, shield, mam, null) { }

    public MultiCloudEdgeCdnDistributionRouter(Func<byte[]> tokenKey, Func<byte[]> drmKey, Func<string, string?> credentials,
        AiContentModerationComplianceShield? shield, EnterpriseMamSmartIndexingEngine? mam, HttpMessageHandler? handler, EdgePop[]? pops = null)
    {
        _tokenKeySrc = tokenKey; _drmKeySrc = drmKey; _cred = credentials; _shield = shield; _mam = mam;
        _pops = pops ?? DefaultPops;
        _rtt = new double[_pops.Length]; _err = new double[_pops.Length]; _load = new double[_pops.Length];
        _requests = new long[_pops.Length]; _bytes = new long[_pops.Length];
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public event Action? OnChange;
    public IReadOnlyList<EdgePop> Pops => _pops;
    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }
    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
    public long TokensIssued => Interlocked.Read(ref _tokensIssued);
    public long TokensRejected => Interlocked.Read(ref _tokensRejected);
    public long Manifests => Interlocked.Read(ref _manifests);
    public int BackoffBaseMs { get; set; } = 400;
    public string? LastAction { get; private set; }
    public PublishResult? LastPublish { get; private set; }
    public long RequestsAt(int pop) => Interlocked.Read(ref _requests[pop]);
    public long BytesAt(int pop) => Interlocked.Read(ref _bytes[pop]);
    public long BlockedIn(ComplianceRegion? r) => Interlocked.Read(ref _blockedByRegion[r is { } x ? (int)x : AiContentModerationComplianceShield.Regions]);
    public double LoadAt(int pop) => Volatile.Read(ref _load[pop]);

    public void SetHealth(int pop, double rttMs, double errorRate) { Volatile.Write(ref _rtt[pop], rttMs); Volatile.Write(ref _err[pop], errorRate); }

    public void ResetTraffic()
    {
        Array.Clear(_requests); Array.Clear(_bytes); Array.Clear(_load); Array.Clear(_blockedByRegion);
        Raise();
    }

    // ══════════════════════ 지오 라우팅(무할당) ══════════════════════

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>ISO-3166 alpha-2 → 심의 지역(미지정 null).</summary>
    public static ComplianceRegion? RegionOf(ReadOnlySpan<char> cc)
    {
        if (cc.Length != 2) return null;
        char a = char.ToUpperInvariant(cc[0]), b = char.ToUpperInvariant(cc[1]);
        return (a, b) switch
        {
            ('U', 'S') => ComplianceRegion.US, ('K', 'R') => ComplianceRegion.KR, ('G', 'B') => ComplianceRegion.UK, ('J', 'P') => ComplianceRegion.JP,
            ('D', 'E') or ('F', 'R') or ('I', 'T') or ('E', 'S') or ('N', 'L') or ('B', 'E') or ('A', 'T') or ('S', 'E') or ('P', 'L') or ('I', 'E') or ('P', 'T')
                or ('F', 'I') or ('D', 'K') or ('C', 'Z') or ('G', 'R') or ('H', 'U') or ('R', 'O') => ComplianceRegion.EU,
            _ => null,
        };
    }

    /// <summary>자산 배포 게이트 등록(심의 리포트의 반려 지역).</summary>
    public void SetGate(ComplianceReport r)
    {
        var rejected = new List<ComplianceRegion>();
        foreach (var x in r.Regions) if (x.Status == ComplianceStatus.Rejected) rejected.Add(x.Region);
        lock (_gate) _gates[r.AssetId] = (rejected.ToArray(), r.ApprovedRegions.Count);
    }

    public bool IsAllowed(string? assetId, ComplianceRegion? region)
    {
        if (assetId is null) return true;
        lock (_gate)
        {
            if (!_gates.TryGetValue(assetId, out var g)) return true;
            if (region is { } rg) { foreach (var x in g.Rejected) if (x == rg) return false; return true; }
            return g.Approved > 0;
        }
    }

    /// <summary>클라이언트 → 최적 PoP(거리 RTT + 부하 + 오류 + 비용 가중 · 심의 게이트). 무할당.</summary>
    public RouteDecision Route(double lat, double lon, ReadOnlySpan<char> country, string? assetId = null)
    {
        var region = RegionOf(country);
        if (!IsAllowed(assetId, region))
        {
            Interlocked.Increment(ref _blockedByRegion[region is { } r ? (int)r : AiContentModerationComplianceShield.Regions]);
            return new RouteDecision(-1, double.MaxValue, 0, true, region);
        }
        var best = -1; var bestScore = double.MaxValue; var bestRtt = 0.0;
        for (var i = 0; i < _pops.Length; i++)
        {
            var err = Volatile.Read(ref _err[i]);
            if (err >= 0.5) continue;
            var km = HaversineKm(lat, lon, _pops[i].Lat, _pops[i].Lon);
            var rtt = 5 + km / 100.0 + Volatile.Read(ref _rtt[i]);
            var util = Volatile.Read(ref _load[i]) / Math.Max(1, _pops[i].CapacityGbps);
            var score = rtt * (1 + Math.Max(0, util - 0.7) * 4) + err * 400 + _pops[i].CostPerGb * 40;
            if (score < bestScore) { bestScore = score; best = i; bestRtt = rtt; }
        }
        return new RouteDecision(best, bestScore, bestRtt, best < 0, region);
    }

    /// <summary>시청자 시뮬레이터(인구 가중 도시 + 지터 → 라우팅 · 세그먼트 바이트 · 부하 갱신). 무할당. 반환 = 차단 수.</summary>
    public int SimulateViewers(int n, string? assetId = null)
    {
        double wsum = 0;
        foreach (var p in Population) wsum += p.Weight;
        var blocked = 0;
        for (var k = 0; k < n; k++)
        {
            var pick = NextRand() * wsum;
            var ci = 0;
            for (; ci < Population.Length - 1; ci++) { pick -= Population[ci].Weight; if (pick <= 0) break; }
            var c = Population[ci];
            var d = Route(c.Lat + (NextRand() - 0.5) * 2, c.Lon + (NextRand() - 0.5) * 2, c.Cc, assetId);
            if (d.Blocked || d.Pop < 0) { blocked++; continue; }
            Interlocked.Increment(ref _requests[d.Pop]);
            Interlocked.Add(ref _bytes[d.Pop], SegmentBytes);
        }
        for (var i = 0; i < _pops.Length; i++) Volatile.Write(ref _load[i], Interlocked.Read(ref _bytes[i]) * 8 / 6.0 / 1e9 / Math.Max(1, n / 1000.0));
        Raise();
        return blocked;
    }

    private double NextRand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFFFF) / 16777216.0; }

    // ══════════════════════ 토큰 DRM(무할당) ══════════════════════

    private const int MaxCanonical = 1024;

    private int Canonical(ReadOnlySpan<char> path, long exp, ReadOnlySpan<char> cc, ReadOnlySpan<char> kid, Span<byte> dst)
    {
        var p = 0;
        p += Put(dst[p..], "v1|5|");
        p += PutNum(dst[p..], path.Length); dst[p++] = (byte)'|';
        p += Put(dst[p..], path); dst[p++] = (byte)'|';
        p += PutNum(dst[p..], exp); dst[p++] = (byte)'|';
        p += Put(dst[p..], cc); dst[p++] = (byte)'|';
        p += Put(dst[p..], kid);
        return p;

        static int Put(Span<byte> d, ReadOnlySpan<char> s) => Encoding.UTF8.GetBytes(s, d);
        static int PutNum(Span<byte> d, long v) => v.TryFormat(d, out var w, default, CultureInfo.InvariantCulture) ? w : 0;
    }

    private static ReadOnlySpan<char> KidOf(CdnProvider p) => p switch { CdnProvider.CloudFront => "K2PFS01", CdnProvider.CloudflareStream => "cfs01", _ => "fy01" };

    /// <summary>서명 쿼리 문자열(공급자 형식) → dst. 반환 = 문자 수(0 = 버퍼 부족).</summary>
    public int SignQuery(ReadOnlySpan<char> path, long exp, ReadOnlySpan<char> cc, CdnProvider provider, Span<char> dst)
    {
        if (path.Length > 700 || dst.Length < 160) return 0;
        Span<byte> canon = stackalloc byte[MaxCanonical];
        Span<byte> mac = stackalloc byte[32];
        Span<byte> b64 = stackalloc byte[48];
        var kid = KidOf(provider);
        var n = Canonical(path, exp, cc, kid, canon);
        HMACSHA256.HashData(TokenKey, canon[..n], mac);
        var w = 0;
        switch (provider)
        {
            case CdnProvider.CloudFront:
                w += Copy("Expires=", dst[w..]); exp.TryFormat(dst[w..], out var e1, default, CultureInfo.InvariantCulture); w += e1;
                w += Copy("&Signature=", dst[w..]); w += B64Url(mac, b64, dst[w..]);
                w += Copy("&Key-Pair-Id=", dst[w..]); w += Copy(kid, dst[w..]);
                break;
            case CdnProvider.CloudflareStream:
                w += Copy("token=", dst[w..]); exp.TryFormat(dst[w..], out var e2, default, CultureInfo.InvariantCulture); w += e2;
                dst[w++] = '.'; w += B64Url(mac, b64, dst[w..]);
                break;
            default:
                w += Copy("token=", dst[w..]); exp.TryFormat(dst[w..], out var e3, default, CultureInfo.InvariantCulture); w += e3;
                dst[w++] = '_';
                for (var i = 0; i < 32; i++) { dst[w++] = Hex(mac[i] >> 4); dst[w++] = Hex(mac[i] & 15); }
                break;
        }
        w += Copy("&cc=", dst[w..]); w += Copy(cc, dst[w..]);
        Interlocked.Increment(ref _tokensIssued);
        return w;

        static char Hex(int v) => (char)(v < 10 ? '0' + v : 'a' + v - 10);
    }

    private static int Copy(ReadOnlySpan<char> s, Span<char> d) { s.CopyTo(d); return s.Length; }

    private static int B64Url(ReadOnlySpan<byte> mac, Span<byte> tmp, Span<char> dst)
    {
        Base64.EncodeToUtf8(mac, tmp, out _, out var w);
        var n = 0;
        for (var i = 0; i < w; i++)
        {
            var c = (char)tmp[i];
            if (c == '=') break;
            dst[n++] = c == '+' ? '-' : c == '/' ? '_' : c;
        }
        return n;
    }

    /// <summary>쿼리 토큰 검증(형식 자동 판별 · 만료 · 국가 바인딩 · FixedTimeEquals). 무할당.</summary>
    public bool ValidateQuery(ReadOnlySpan<char> path, ReadOnlySpan<char> query, ReadOnlySpan<char> requestCc, long nowUnix)
    {
        var cc = Param(query, "cc");
        if (cc.IsEmpty || !cc.Equals(requestCc, StringComparison.OrdinalIgnoreCase)) return Reject();
        long exp; CdnProvider provider; ReadOnlySpan<char> sig;
        var expires = Param(query, "Expires");
        if (!expires.IsEmpty)
        {
            provider = CdnProvider.CloudFront;
            if (!long.TryParse(expires, NumberStyles.Integer, CultureInfo.InvariantCulture, out exp)) return Reject();
            sig = Param(query, "Signature");
        }
        else
        {
            var tok = Param(query, "token");
            var dot = tok.IndexOf('.'); var us = tok.IndexOf('_');
            var sep = dot >= 0 ? dot : us;
            if (sep <= 0) return Reject();
            provider = dot >= 0 ? CdnProvider.CloudflareStream : CdnProvider.Fastly;
            if (!long.TryParse(tok[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out exp)) return Reject();
            sig = tok[(sep + 1)..];
        }
        if (exp < nowUnix || sig.IsEmpty) return Reject();
        Span<char> expect = stackalloc char[256];
        var n = SignQuery(path, exp, cc, provider, expect);
        Interlocked.Decrement(ref _tokensIssued);
        var ex = expect[..n];
        var expSig = provider switch
        {
            CdnProvider.CloudFront => Param(ex, "Signature"),
            _ => Param(ex, "token")[(Param(ex, "token").IndexOfAny('.', '_') + 1)..],
        };
        var ok = sig.Length == expSig.Length && CryptographicOperations.FixedTimeEquals(MemoryMarshalBytes(sig), MemoryMarshalBytes(expSig));
        return ok || Reject();
    }

    private static ReadOnlySpan<byte> MemoryMarshalBytes(ReadOnlySpan<char> s) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(s);

    private bool Reject() { Interlocked.Increment(ref _tokensRejected); return false; }

    public static ReadOnlySpan<char> Param(ReadOnlySpan<char> query, ReadOnlySpan<char> name)
    {
        var q = query;
        while (!q.IsEmpty)
        {
            var amp = q.IndexOf('&');
            var part = amp < 0 ? q : q[..amp];
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].SequenceEqual(name)) return part[(eq + 1)..];
            if (amp < 0) break;
            q = q[(amp + 1)..];
        }
        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>자산 콘텐츠 키(AES-128 · HKDF-SHA256) · KID(16B).</summary>
    public void DeriveContentKey(ReadOnlySpan<char> assetId, Span<byte> key16, Span<byte> kid16)
    {
        Span<byte> info = stackalloc byte[160];
        var n = Encoding.UTF8.GetBytes(assetId.Length > 120 ? assetId[..120] : assetId, info[4..]);
        "key|"u8.CopyTo(info);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, DrmKey, key16[..16], ReadOnlySpan<byte>.Empty, info[..(n + 4)]);
        "kid|"u8.CopyTo(info);
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(info[..(n + 4)], h);
        h[..16].CopyTo(kid16);
    }

    public string KidString(string assetId)
    {
        Span<byte> k = stackalloc byte[16]; Span<byte> kid = stackalloc byte[16];
        DeriveContentKey(assetId, k, kid);
        k.Clear();
        return new Guid(kid, true).ToString("D");
    }

    // ══════════════════════ 동적 매니페스트 ══════════════════════

    /// <summary>표준 래더 마스터(소스 없을 때).</summary>
    public static string DefaultMaster(IReadOnlyList<int>? heights = null)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        foreach (var h in heights ?? new[] { 1080, 720, 480 })
        {
            var bw = (h >= 1080 ? 5000 : h >= 720 ? 2800 : 1200) * 1000;
            sb.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(bw.ToString(CultureInfo.InvariantCulture)).Append(",RESOLUTION=").Append(((int)Math.Round(h * 16 / 9.0 / 2) * 2).ToString(CultureInfo.InvariantCulture))
              .Append('x').Append(h.ToString(CultureInfo.InvariantCulture)).Append('\n').Append('v').Append(h.ToString(CultureInfo.InvariantCulture)).Append("/index.m3u8\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 마스터 재작성: URI 라인/URI="…" 속성 → https://엣지/자산/경로?서명 — 재사용 버퍼(UTF-8) 기록 · 워밍업 후 무할당.
    /// 반환 스팬은 다음 호출 전까지 유효(락 내부 호출 권장).
    /// </summary>
    public ReadOnlySpan<byte> RewriteMaster(ReadOnlySpan<char> master, ReadOnlySpan<char> assetId, int pop, ReadOnlySpan<char> cc, long exp)
    {
        _out.ResetWrittenCount();
        var p = _pops[pop];
        Span<char> url = stackalloc char[1024];
        var rest = master;
        while (!rest.IsEmpty)
        {
            var nl = rest.IndexOf('\n');
            var line = (nl < 0 ? rest : rest[..nl]).TrimEnd('\r');
            rest = nl < 0 ? ReadOnlySpan<char>.Empty : rest[(nl + 1)..];
            if (line.IsEmpty) continue;
            if (line[0] != '#')
            {
                var n = BuildUrl(p, assetId, line, cc, exp, url);
                WriteChars(url[..n]);
            }
            else
            {
                var u = line.IndexOf("URI=\"");
                if (u < 0) WriteChars(line);
                else
                {
                    var start = u + 5;
                    var end = line[start..].IndexOf('"');
                    if (end < 0) { WriteChars(line); }
                    else
                    {
                        WriteChars(line[..start]);
                        var n = BuildUrl(p, assetId, line.Slice(start, end), cc, exp, url);
                        WriteChars(url[..n]);
                        WriteChars(line[(start + end)..]);
                    }
                }
            }
            WriteChars("\n");
        }
        Interlocked.Increment(ref _manifests);
        return _out.WrittenSpan;
    }

    private int BuildUrl(EdgePop p, ReadOnlySpan<char> assetId, ReadOnlySpan<char> rel, ReadOnlySpan<char> cc, long exp, Span<char> dst)
    {
        var w = 0;
        w += Copy("https://", dst); w += Copy(p.Host, dst[w..]); dst[w++] = '/';
        var pathStart = w;
        w += Copy(assetId, dst[w..]); dst[w++] = '/'; w += Copy(rel, dst[w..]);
        dst[w++] = '?';
        var path = dst[(pathStart - 1)..(w - 1)];
        Span<char> q = stackalloc char[256];
        var qn = SignQuery(path, exp, cc, p.Provider, q);
        w += Copy(q[..qn], dst[w..]);
        return w;
    }

    private void WriteChars(ReadOnlySpan<char> s)
    {
        var span = _out.GetSpan(s.Length * 3);
        var n = Encoding.UTF8.GetBytes(s, span);
        _out.Advance(n);
    }

    /// <summary>변형 미디어 플레이리스트(AES-128 EXT-X-KEY · 서명 세그먼트).</summary>
    public string MediaPlaylist(string assetId, string variant, int pop, string cc, long exp, double durationSec = 60, int segSec = 6)
    {
        var p = _pops[pop];
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:").Append(segSec.ToString(CultureInfo.InvariantCulture)).Append("\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        Span<char> q = stackalloc char[256];
        var keyPath = "/keys/" + assetId;
        var kn = SignQuery(keyPath, exp, cc, p.Provider, q);
        sb.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"").Append(BaseUrl).Append(keyPath).Append('?').Append(q[..kn]).Append("\",KEYFORMAT=\"identity\"\n");
        var segs = Math.Max(1, (int)Math.Ceiling(durationSec / segSec));
        for (var i = 0; i < segs; i++)
        {
            var path = "/edge/" + p.Id + "/" + assetId + "/" + variant + "/seg_" + i.ToString("0000", CultureInfo.InvariantCulture) + ".ts";
            var n = SignQuery(path, exp, cc, p.Provider, q);
            sb.Append("#EXTINF:").Append(Math.Min(segSec, durationSec - i * segSec).ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n").Append(BaseUrl).Append(path).Append('?').Append(q[..n]).Append('\n');
        }
        return sb.Append("#EXT-X-ENDLIST\n").ToString();
    }

    /// <summary>DASH MPD(CENC default_KID · ClearKey/Widevine/PlayReady ContentProtection · 엣지 BaseURL).</summary>
    public string DashManifest(string assetId, int pop, string cc, long exp, double durationSec = 60, IReadOnlyList<int>? heights = null)
    {
        var p = _pops[pop];
        var kid = KidString(assetId);
        Span<char> q = stackalloc char[256];
        var n = SignQuery("/" + assetId + "/dash/", exp, cc, p.Provider, q);
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" xmlns:cenc=\"urn:mpeg:cenc:2013\" type=\"static\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\" mediaPresentationDuration=\"PT")
            .Append(durationSec.ToString("0.###", CultureInfo.InvariantCulture)).Append("S\" minBufferTime=\"PT2S\">\n  <BaseURL>https://").Append(p.Host).Append('/').Append(assetId).Append("/dash/</BaseURL>\n  <Period>\n    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\">\n")
            .Append("      <ContentProtection schemeIdUri=\"urn:mpeg:dash:mp4protection:2011\" value=\"cenc\" cenc:default_KID=\"").Append(kid).Append("\"/>\n")
            .Append("      <ContentProtection schemeIdUri=\"urn:uuid:e2719d58-a985-b3c9-781a-b030af78d30e\" value=\"ClearKey1.0\"/>\n")
            .Append("      <ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\" value=\"Widevine\"/>\n")
            .Append("      <ContentProtection schemeIdUri=\"urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95\" value=\"PlayReady\"/>\n");
        foreach (var h in heights ?? new[] { 1080, 720, 480 })
        {
            var bw = (h >= 1080 ? 5000 : h >= 720 ? 2800 : 1200) * 1000;
            sb.Append("      <Representation id=\"v").Append(h.ToString(CultureInfo.InvariantCulture)).Append("\" bandwidth=\"").Append(bw.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"").Append(h.ToString(CultureInfo.InvariantCulture))
              .Append("\" codecs=\"avc1.640028\">\n        <SegmentTemplate media=\"v").Append(h.ToString(CultureInfo.InvariantCulture)).Append("/seg_$Number%04d$.m4s?").Append(Xml(q[..n])).Append("\" initialization=\"v").Append(h.ToString(CultureInfo.InvariantCulture))
              .Append("/init.mp4?").Append(Xml(q[..n])).Append("\" duration=\"6\" startNumber=\"0\"/>\n      </Representation>\n");
        }
        Interlocked.Increment(ref _manifests);
        return sb.Append("    </AdaptationSet>\n  </Period>\n</MPD>\n").ToString();

        static string Xml(ReadOnlySpan<char> s) => s.ToString().Replace("&", "&amp;");
    }

    // ══════════════════════ 오프로더(루프백 HTTP) ══════════════════════

    public void Start(int port = DefaultPort)
    {
        if (_listener is not null) return;
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = AcceptLoopAsync(l, cts.Token);
        LastAction = "🌐 매니페스트 오프로더 ON · " + BaseUrl;
        Raise();
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        LastAction = "⏹ 오프로더 OFF";
        Raise();
    }

    private async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            _ = HandleAsync(c, ct);
        }
    }

    private async Task HandleAsync(TcpClient c, CancellationToken ct)
    {
        using (c)
        {
            var s = c.GetStream();
            try
            {
                var buf = new byte[4096];
                var n = 0;
                using (var d = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    d.CancelAfter(TimeSpan.FromSeconds(5));
                    while (n < buf.Length)
                    {
                        var r = await s.ReadAsync(buf.AsMemory(n), d.Token).ConfigureAwait(false);
                        if (r == 0) break;
                        n += r;
                        if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
                    }
                }
                var head = Encoding.ASCII.GetString(buf, 0, n);
                var sp1 = head.IndexOf(' '); var sp2 = sp1 < 0 ? -1 : head.IndexOf(' ', sp1 + 1);
                if (!head.StartsWith("GET ", StringComparison.Ordinal) || sp2 < 0) { await WriteAsync(s, 405, "text/plain", "method"u8.ToArray(), null, ct).ConfigureAwait(false); return; }
                var target = Uri.UnescapeDataString(head[(sp1 + 1)..sp2]);
                var qi = target.IndexOf('?');
                var path = qi < 0 ? target : target[..qi];
                var query = qi < 0 ? "" : target[(qi + 1)..];
                var (code, type, body, edge) = Dispatch(path, query);
                await WriteAsync(s, code, type, body, edge, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
            catch (Exception ex) { Program.Log("Cdn.Handle", ex); }
        }
    }

    /// <summary>요청 디스패치(테스트 가능) → 상태 코드 · 타입 · 본문 · 엣지 헤더.</summary>
    public (int Code, string Type, byte[] Body, string? Edge) Dispatch(string path, string query)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cc = Param(query, "cc").ToString();
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length >= 3 && seg[0] == "play")
        {
            var asset = seg[1];
            double.TryParse(Param(query, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat);
            double.TryParse(Param(query, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon);
            var d = Route(lat, lon, cc, asset);
            if (d.Blocked || d.Pop < 0) return (451, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Unavailable For Legal Reasons — 심의 반려 지역"), null);
            var exp = now + KeyTtlSec;
            if (seg[2] == "master.m3u8")
            {
                byte[] body;
                lock (_gate) body = RewriteMaster(DefaultMaster(), asset, d.Pop, cc, exp).ToArray();
                return (200, "application/vnd.apple.mpegurl", body, _pops[d.Pop].Id);
            }
            if (seg[2] == "manifest.mpd") return (200, "application/dash+xml", Encoding.UTF8.GetBytes(DashManifest(asset, d.Pop, cc, exp)), _pops[d.Pop].Id);
            if (seg.Length >= 4 && seg[3] == "index.m3u8") return (200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(MediaPlaylist(asset, seg[2], d.Pop, cc, exp)), _pops[d.Pop].Id);
        }
        if (seg.Length == 2 && seg[0] == "keys")
        {
            if (!ValidateQuery(path, query, cc, now)) return (403, "text/plain", "forbidden"u8.ToArray(), null);
            var key = new byte[16]; Span<byte> kid = stackalloc byte[16];
            DeriveContentKey(seg[1], key, kid);
            return (200, "application/octet-stream", key, null);
        }
        if (seg.Length >= 4 && seg[0] == "edge")
        {
            if (!ValidateQuery(path, query, cc, now)) return (403, "text/plain", "forbidden"u8.ToArray(), null);
            var pop = Array.FindIndex(_pops, p => p.Id == seg[1]);
            if (pop < 0) return (404, "text/plain", "pop"u8.ToArray(), null);
            Interlocked.Increment(ref _requests[pop]);
            Interlocked.Add(ref _bytes[pop], SegmentBytes);
            return (200, "video/mp2t", new byte[188], _pops[pop].Id);
        }
        return (404, "text/plain", "not found"u8.ToArray(), null);
    }

    private static async Task WriteAsync(NetworkStream s, int code, string type, byte[] body, string? edge, CancellationToken ct)
    {
        var reason = code switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 451 => "Unavailable For Legal Reasons", _ => "Error" };
        var head = "HTTP/1.1 " + code.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture)
                 + (edge is null ? "" : "\r\nX-PFS-Edge: " + edge) + "\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
    }

    // ══════════════════════ 1-Click 글로벌 배포 ══════════════════════

    /// <summary>심의 → 게이트 → 공급자 배포(자격 있으면 실 API · 지수 백오프) → 재생 URL.</summary>
    public async Task<PublishResult> PublishAsync(MamAsset asset, string? sourceUrl = null, CancellationToken ct = default)
    {
        var report = _shield?.ReportFor(asset.Id) ?? _shield?.Check(asset);
        if (report is not null) SetGate(report);
        var regions = report?.ApprovedRegions ?? AiContentModerationComplianceShield.AllRegions;
        var steps = new List<PublishStep>();
        if (report is not null && report.ApprovedRegions.Count == 0)
        {
            var blocked = new PublishResult(asset.Id, false, false, steps, regions, "");
            LastPublish = blocked;
            LastAction = "⛔ 배포 차단 · 전 지역 심의 반려 · " + asset.Title;
            Raise();
            return blocked;
        }
        var src = sourceUrl ?? (IsRunning ? BaseUrl + "/play/" + asset.Id + "/master.m3u8" : "file://" + (asset.Path ?? asset.Id));

        // Cloudflare Stream — POST /accounts/{id}/stream/copy (Bearer)
        var cfAcct = _cred("CLOUDFLARE_ACCOUNT_ID"); var cfTok = _cred("CLOUDFLARE_API_TOKEN");
        if (cfAcct is null || cfTok is null) steps.Add(new PublishStep(CdnProvider.CloudflareStream, true, true, 0, "드라이런 · PFS_CLOUDFLARE_ACCOUNT_ID/API_TOKEN 미설정"));
        else steps.Add(await SendWithBackoffAsync(CdnProvider.CloudflareStream, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "https://api.cloudflare.com/client/v4/accounts/" + Uri.EscapeDataString(cfAcct) + "/stream/copy");
            m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfTok);
            m.Content = new StringContent("{\"url\":" + System.Text.Json.JsonSerializer.Serialize(src) + ",\"meta\":{\"name\":" + System.Text.Json.JsonSerializer.Serialize(asset.Title) + "}}", Encoding.UTF8, "application/json");
            return m;
        }, ct).ConfigureAwait(false));

        // Fastly — POST /service/{id}/purge/{surrogate-key} (Fastly-Key)
        var fySvc = _cred("FASTLY_SERVICE_ID"); var fyKey = _cred("FASTLY_API_KEY");
        if (fySvc is null || fyKey is null) steps.Add(new PublishStep(CdnProvider.Fastly, true, true, 0, "드라이런 · PFS_FASTLY_SERVICE_ID/API_KEY 미설정"));
        else steps.Add(await SendWithBackoffAsync(CdnProvider.Fastly, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "https://api.fastly.com/service/" + Uri.EscapeDataString(fySvc) + "/purge/" + Uri.EscapeDataString(asset.Id));
            m.Headers.TryAddWithoutValidation("Fastly-Key", fyKey);
            return m;
        }, ct).ConfigureAwait(false));

        steps.Add(new PublishStep(CdnProvider.CloudFront, true, true, 0, "드라이런 · CreateInvalidation SigV4 서명 미구현 — 오리진(S3) 업로드 후 수동 무효화"));

        var ok = steps.TrueForAll(x => x.Ok);
        var partial = steps.Exists(x => x.DryRun || !x.Ok);
        var res = new PublishResult(asset.Id, ok, partial, steps, regions, IsRunning ? BaseUrl + "/play/" + asset.Id + "/master.m3u8" : src);
        LastPublish = res;
        LastAction = (ok ? partial ? "🩹 [부분 구제] 배포 · " : "🚀 글로벌 배포 · " : "⚠ 배포 일부 실패 · ") + asset.Title + " · 지역 " + regions.Count.ToString(CultureInfo.InvariantCulture);
        Raise();
        return res;
    }

    private async Task<PublishStep> SendWithBackoffAsync(CdnProvider p, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        string msg = "";
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var req = make();
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (p == CdnProvider.CloudflareStream && !body.Contains("\"success\":true", StringComparison.Ordinal)) return new PublishStep(p, false, false, attempt, "스키마 검증 실패(success≠true)");
                    return new PublishStep(p, true, false, attempt, "HTTP " + code.ToString(CultureInfo.InvariantCulture));
                }
                msg = "HTTP " + code.ToString(CultureInfo.InvariantCulture);
                if (code is not (429 or >= 500)) return new PublishStep(p, false, false, attempt, msg);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { msg = ex.GetType().Name; }
            if (attempt < 4) await Task.Delay(BackoffBaseMs * (1 << (attempt - 1)), ct).ConfigureAwait(false);
        }
        return new PublishStep(p, false, false, 4, "재시도 소진 · " + msg);
    }

    public static string ProviderLabel(CdnProvider p) => p switch { CdnProvider.CloudFront => "AWS CloudFront", CdnProvider.CloudflareStream => "Cloudflare Stream", _ => "Fastly" };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Cdn.OnChange", ex); } }

    public void Dispose() { Stop(); _http.Dispose(); }
}
