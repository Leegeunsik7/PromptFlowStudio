using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PromptFlowStudio.Services.Integration;

public enum FedVerdict
{
    Granted, Malformed, UntrustedIssuer, BadClassicalSignature, BadPqcSignature,
    Expired, Replayed, AudienceMismatch, MtlsBindingMismatch, PeerAttestationFailed, ZtnaDenied,
}

/// <summary>워크로드 신원(SPIFFE 유사) — 홈 클러스터가 하이브리드 서명한 어테스테이션 + 키 지문(mTLS 식별자).</summary>
public sealed record FedWorkloadIdentity(string SpiffeId, string ClusterId, string Fingerprint, long ExpiresAt, string Attestation);

public sealed record FedClusterView(string Id, string KeyFingerprint, int Generation, IReadOnlyList<string> TrustedPeers);

public sealed record FedExchangeResult(
    bool Granted, FedVerdict Verdict, string Subject, string FromCluster, string ToCluster,
    IReadOnlyList<string> Scopes, string? IssuedToken, double ElapsedMs, string Reason);

/// <summary>
/// v9.6.2 양자 내성 신원 연합 — 클러스터별 하이브리드 서명(ECDSA P-384 + ML-DSA-65 결선점 스텁 HMAC-SHA512)으로
/// B2B 교차 클러스터 토큰 교환(RFC 8693 유사)을 수행한다. 신뢰 번들·양 서명·만료·재생(jti)·수신자·
/// mTLS 바인딩(cnf 지문 = 제시 피어 지문, RFC 8705)·피어 어테스테이션 상호 검증 후 ZTNA 정책(포스처·스코프 축소)으로 재발급. 순수 BCL.
/// </summary>
public sealed class QuantumIdentityFederation : IDisposable
{
    public const int TokenTtlSec = 300;

    private sealed class Cluster : IDisposable
    {
        public Cluster(string id)
        {
            Id = id;
            Rotate();
        }

        public string Id { get; }
        public ECDsa Signer { get; private set; } = null!;
        public byte[] PublicKey { get; private set; } = Array.Empty<byte>();
        public byte[] PqcKey { get; private set; } = Array.Empty<byte>();
        public int Generation { get; private set; }

        public void Rotate()
        {
            Signer?.Dispose();
            Signer = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            PublicKey = Signer.ExportSubjectPublicKeyInfo();
            PqcKey = RandomNumberGenerator.GetBytes(64);
            Generation++;
        }

        public void Dispose() => Signer.Dispose();
    }

    /// <summary>연합 링크 — 수신 클러스터가 발급 클러스터에 허용하는 스코프·최소 포스처.</summary>
    private sealed record Link(string From, string To, string[] AllowedScopes, int MinPosture);

    private readonly object _gate = new();
    private readonly Dictionary<string, Cluster> _clusters = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), Link> _links = new();
    private readonly ConcurrentDictionary<string, long> _seenJti = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (FedWorkloadIdentity Identity, ECDsa Key)> _workloads = new(StringComparer.Ordinal);
    private long _granted, _denied, _issued;
    private FedExchangeResult? _last;
    private bool _disposed;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public long GrantedCount => Interlocked.Read(ref _granted);
    public long DeniedCount => Interlocked.Read(ref _denied);
    public long IssuedCount => Interlocked.Read(ref _issued);
    public int ReplayCacheSize => _seenJti.Count;
    public FedExchangeResult? LastExchange { get { lock (_gate) return _last; } }

    public IReadOnlyList<FedClusterView> Clusters
    {
        get
        {
            lock (_gate)
            {
                var list = new List<FedClusterView>(_clusters.Count);
                foreach (var c in _clusters.Values)
                {
                    var peers = new List<string>();
                    foreach (var l in _links.Values) if (l.To == c.Id) peers.Add(l.From);
                    list.Add(new FedClusterView(c.Id, Convert.ToHexString(SHA256.HashData(c.PublicKey))[..16], c.Generation, peers));
                }
                return list;
            }
        }
    }

    public IReadOnlyList<FedWorkloadIdentity> Workloads
    {
        get { lock (_gate) { var l = new List<FedWorkloadIdentity>(); foreach (var w in _workloads.Values) l.Add(w.Identity); return l; } }
    }

    /// <summary>데모 연합: seoul-prod ⇄ tokyo-edge, frankfurt-b2b → seoul-prod (tokyo ↛ frankfurt 미신뢰).</summary>
    public void SeedFederation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            DisposeAllLocked();
            foreach (var id in new[] { "seoul-prod", "tokyo-edge", "frankfurt-b2b" }) _clusters[id] = new Cluster(id);
            AddLinkLocked("seoul-prod", "tokyo-edge", new[] { "render:read", "render:submit", "gallery:read" }, 60);
            AddLinkLocked("tokyo-edge", "seoul-prod", new[] { "render:read", "telemetry:read" }, 60);
            AddLinkLocked("frankfurt-b2b", "seoul-prod", new[] { "gallery:read" }, 80);
            IssueWorkloadLocked("seoul-prod", "ns/studio/sa/director");
            IssueWorkloadLocked("tokyo-edge", "ns/edge/sa/sts");
            IssueWorkloadLocked("seoul-prod", "ns/studio/sa/sts");
            IssueWorkloadLocked("frankfurt-b2b", "ns/partner/sa/agency");
            IssueWorkloadLocked("tokyo-edge", "ns/edge/sa/rogue");
            _seenJti.Clear();
            LastAction = "연합 3클러스터 · 링크 3 · 워크로드 5";
        }
        OnChange?.Invoke();
    }

    /// <summary>발급 클러스터가 서브젝트 워크로드에 하이브리드 서명 토큰 발급(cnf = 워크로드 키 지문).</summary>
    public string IssueSubjectToken(string spiffeId, string audience, IEnumerable<string> scopes, int posture, int ttlSec = TokenTtlSec)
    {
        lock (_gate)
        {
            if (!_workloads.TryGetValue(spiffeId, out var wl)) throw new InvalidOperationException("미등록 워크로드: " + spiffeId);
            var cluster = _clusters[wl.Identity.ClusterId];
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = WritePayload(cluster.Id, spiffeId, audience, now, now + ttlSec, Guid.NewGuid().ToString("N"),
                string.Join(' ', scopes), wl.Identity.Fingerprint, posture, null);
            Interlocked.Increment(ref _issued);
            return SignToken(cluster, payload);
        }
    }

    /// <summary>
    /// 토큰 교환 — 수신 클러스터 STS 가 피어(mTLS 제시 워크로드)와 서브젝트 토큰을 검증 후 다운스코프 토큰 재발급.
    /// </summary>
    public FedExchangeResult Exchange(string subjectToken, string presentedPeerSpiffe, string targetCluster, IEnumerable<string> requestedScopes)
    {
        var sw = Stopwatch.StartNew();
        FedExchangeResult result;
        lock (_gate) result = ExchangeLocked(subjectToken, presentedPeerSpiffe, targetCluster, requestedScopes, sw);
        if (result.Granted) Interlocked.Increment(ref _granted); else Interlocked.Increment(ref _denied);
        lock (_gate) _last = result;
        LastAction = result.Reason;
        OnChange?.Invoke();
        return result;
    }

    private FedExchangeResult ExchangeLocked(string token, string peerSpiffe, string target, IEnumerable<string> requested, Stopwatch sw)
    {
        FedExchangeResult Deny(FedVerdict v, string sub, string from, string reason) =>
            new(false, v, sub, from, target, Array.Empty<string>(), null, Math.Round(sw.Elapsed.TotalMilliseconds, 3), $"{VerdictLabel(v)} — {reason}");

        var parts = token.Split('.');
        if (parts.Length != 4) return Deny(FedVerdict.Malformed, "-", "-", "4분절 하이브리드 토큰 아님");
        byte[] payloadBytes, ecSig, pqcSig;
        try
        {
            payloadBytes = B64UrlDecode(parts[1]);
            ecSig = B64UrlDecode(parts[2]);
            pqcSig = B64UrlDecode(parts[3]);
        }
        catch (FormatException) { return Deny(FedVerdict.Malformed, "-", "-", "base64url 손상"); }

        string iss, sub, aud, jti, cnf;
        long exp;
        int posture;
        string[] scopes;
        try
        {
            using var doc = JsonDocument.Parse(payloadBytes);
            var r = doc.RootElement;
            iss = r.GetProperty("iss").GetString() ?? "";
            sub = r.GetProperty("sub").GetString() ?? "";
            aud = r.GetProperty("aud").GetString() ?? "";
            jti = r.GetProperty("jti").GetString() ?? "";
            cnf = r.GetProperty("cnf").GetString() ?? "";
            exp = r.GetProperty("exp").GetInt64();
            posture = r.GetProperty("posture").GetInt32();
            scopes = (r.GetProperty("scope").GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Deny(FedVerdict.Malformed, "-", "-", "클레임 파싱 실패");
        }

        // 1) 신뢰 번들: 수신 클러스터가 발급 클러스터를 연합 신뢰하는가
        if (!_clusters.TryGetValue(iss, out var issuer) || !_links.TryGetValue((iss, target), out var link) || !_clusters.TryGetValue(target, out var receiver))
            return Deny(FedVerdict.UntrustedIssuer, sub, iss, $"{target} 신뢰 번들에 {iss} 없음");

        // 2) 하이브리드 서명: 고전(ECDSA P-384) + PQC(ML-DSA 스텁) 둘 다 통과해야 함
        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(issuer.PublicKey, out _);
            if (!verifier.VerifyData(signingInput, ecSig, HashAlgorithmName.SHA384))
                return Deny(FedVerdict.BadClassicalSignature, sub, iss, "ECDSA P-384 서명 불일치");
        }
        if (pqcSig.Length != 64 || !CryptographicOperations.FixedTimeEquals(HMACSHA512.HashData(issuer.PqcKey, signingInput), pqcSig))
            return Deny(FedVerdict.BadPqcSignature, sub, iss, "ML-DSA-65 서명 불일치");

        // 3) 시간·수신자·재생
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (exp <= now) return Deny(FedVerdict.Expired, sub, iss, "토큰 만료");
        if (!string.Equals(aud, target, StringComparison.Ordinal)) return Deny(FedVerdict.AudienceMismatch, sub, iss, $"aud={aud}");
        PruneReplayCache(now);
        if (!_seenJti.TryAdd(jti, exp)) return Deny(FedVerdict.Replayed, sub, iss, "jti 재사용");

        // 4) 상호 mTLS: 제시 피어 어테스테이션 검증 + cnf 바인딩
        if (!_workloads.TryGetValue(peerSpiffe, out var peer) || !VerifyAttestationLocked(peer.Identity))
            return Deny(FedVerdict.PeerAttestationFailed, sub, iss, "피어 어테스테이션 실패");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cnf), Encoding.ASCII.GetBytes(peer.Identity.Fingerprint)))
            return Deny(FedVerdict.MtlsBindingMismatch, sub, iss, "cnf 지문 ≠ 제시 인증서(탈취 토큰)");
        var sts = FindStsLocked(target);
        if (sts is null || !VerifyAttestationLocked(sts))
            return Deny(FedVerdict.PeerAttestationFailed, sub, iss, $"{target} STS 역검증 실패");

        // 5) ZTNA: 포스처 하한 + 스코프 교집합 축소
        if (posture < link.MinPosture) return Deny(FedVerdict.ZtnaDenied, sub, iss, $"포스처 {posture} < {link.MinPosture}");
        var granted = new List<string>();
        foreach (var s in requested)
            if (Array.IndexOf(scopes, s) >= 0 && Array.IndexOf(link.AllowedScopes, s) >= 0 && !granted.Contains(s)) granted.Add(s);
        if (granted.Count == 0) return Deny(FedVerdict.ZtnaDenied, sub, iss, "허용 스코프 교집합 공집합");

        var issued = SignToken(receiver, WritePayload(receiver.Id, sub, receiver.Id, now, Math.Min(exp, now + TokenTtlSec),
            Guid.NewGuid().ToString("N"), string.Join(' ', granted), cnf, posture, iss + ":" + sts.SpiffeId));
        Interlocked.Increment(ref _issued);
        return new FedExchangeResult(true, FedVerdict.Granted, sub, iss, target, granted, issued,
            Math.Round(sw.Elapsed.TotalMilliseconds, 3), $"교환 승인 · {iss}→{target} · 스코프 {granted.Count}/{scopes.Length}");
    }

    // ── 데모 시나리오 ──
    public FedExchangeResult DemoValid() =>
        Exchange(IssueSubjectToken(Spiffe("seoul-prod", "ns/studio/sa/director"), "tokyo-edge", new[] { "render:submit", "render:read", "admin:all" }, 92),
            Spiffe("seoul-prod", "ns/studio/sa/director"), "tokyo-edge", new[] { "render:submit", "admin:all" });

    public FedExchangeResult DemoReplay()
    {
        var director = Spiffe("seoul-prod", "ns/studio/sa/director");
        var token = IssueSubjectToken(director, "tokyo-edge", new[] { "render:read" }, 90);
        Exchange(token, director, "tokyo-edge", new[] { "render:read" });
        return Exchange(token, director, "tokyo-edge", new[] { "render:read" });
    }

    public FedExchangeResult DemoStolen() =>
        Exchange(IssueSubjectToken(Spiffe("seoul-prod", "ns/studio/sa/director"), "tokyo-edge", new[] { "render:read" }, 90),
            Spiffe("tokyo-edge", "ns/edge/sa/rogue"), "tokyo-edge", new[] { "render:read" });

    public FedExchangeResult DemoForgedPqc()
    {
        var director = Spiffe("seoul-prod", "ns/studio/sa/director");
        var parts = IssueSubjectToken(director, "tokyo-edge", new[] { "render:read" }, 90).Split('.');
        var pqc = B64UrlDecode(parts[3]);
        pqc[0] ^= 0x80;
        return Exchange($"{parts[0]}.{parts[1]}.{parts[2]}.{B64UrlEncode(pqc)}", director, "tokyo-edge", new[] { "render:read" });
    }

    public FedExchangeResult DemoUntrusted() =>
        Exchange(IssueSubjectToken(Spiffe("frankfurt-b2b", "ns/partner/sa/agency"), "tokyo-edge", new[] { "gallery:read" }, 95),
            Spiffe("frankfurt-b2b", "ns/partner/sa/agency"), "tokyo-edge", new[] { "gallery:read" });

    public FedExchangeResult DemoLowPosture() =>
        Exchange(IssueSubjectToken(Spiffe("frankfurt-b2b", "ns/partner/sa/agency"), "seoul-prod", new[] { "gallery:read" }, 55),
            Spiffe("frankfurt-b2b", "ns/partner/sa/agency"), "seoul-prod", new[] { "gallery:read" });

    /// <summary>클러스터 키 회전 — 구 세대 서명 토큰·어테스테이션 즉시 무효.</summary>
    public void RotateKeys(string clusterId)
    {
        lock (_gate)
        {
            if (!_clusters.TryGetValue(clusterId, out var c)) return;
            c.Rotate();
            LastAction = $"{clusterId} 키 회전 · gen {c.Generation} (구 서명 무효)";
        }
        OnChange?.Invoke();
    }

    public static string VerdictLabel(FedVerdict v) => v switch
    {
        FedVerdict.Granted => "승인",
        FedVerdict.Malformed => "형식 오류",
        FedVerdict.UntrustedIssuer => "미신뢰 발급자",
        FedVerdict.BadClassicalSignature => "ECDSA 위조",
        FedVerdict.BadPqcSignature => "PQC 위조",
        FedVerdict.Expired => "만료",
        FedVerdict.Replayed => "재생 차단",
        FedVerdict.AudienceMismatch => "수신자 불일치",
        FedVerdict.MtlsBindingMismatch => "mTLS 바인딩 불일치",
        FedVerdict.PeerAttestationFailed => "피어 증명 실패",
        _ => "ZTNA 거부",
    };

    private static string Spiffe(string cluster, string path) => $"spiffe://{cluster}/{path}";

    private void AddLinkLocked(string from, string to, string[] scopes, int minPosture) => _links[(from, to)] = new Link(from, to, scopes, minPosture);

    private void IssueWorkloadLocked(string clusterId, string path)
    {
        var cluster = _clusters[clusterId];
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var spiffe = Spiffe(clusterId, path);
        var expires = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes($"ATT|{spiffe}|{clusterId}|{fingerprint}|{expires}");
        var att = B64UrlEncode(cluster.Signer.SignData(body, HashAlgorithmName.SHA384)) + "." + B64UrlEncode(HMACSHA512.HashData(cluster.PqcKey, body));
        _workloads[spiffe] = (new FedWorkloadIdentity(spiffe, clusterId, fingerprint, expires, att), key);
    }

    private bool VerifyAttestationLocked(FedWorkloadIdentity id)
    {
        if (!_clusters.TryGetValue(id.ClusterId, out var cluster) || id.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        var parts = id.Attestation.Split('.');
        if (parts.Length != 2) return false;
        var body = Encoding.UTF8.GetBytes($"ATT|{id.SpiffeId}|{id.ClusterId}|{id.Fingerprint}|{id.ExpiresAt}");
        try
        {
            using var v = ECDsa.Create();
            v.ImportSubjectPublicKeyInfo(cluster.PublicKey, out _);
            return v.VerifyData(body, B64UrlDecode(parts[0]), HashAlgorithmName.SHA384)
                && CryptographicOperations.FixedTimeEquals(HMACSHA512.HashData(cluster.PqcKey, body), B64UrlDecode(parts[1]));
        }
        catch (FormatException) { return false; }
    }

    private FedWorkloadIdentity? FindStsLocked(string clusterId)
    {
        foreach (var w in _workloads.Values)
            if (w.Identity.ClusterId == clusterId && w.Identity.SpiffeId.EndsWith("/sa/sts", StringComparison.Ordinal)) return w.Identity;
        return null;
    }

    private void PruneReplayCache(long now)
    {
        if (_seenJti.Count < 256) return;
        foreach (var kv in _seenJti)
            if (kv.Value <= now) _seenJti.TryRemove(kv.Key, out _);
    }

    private static byte[] WritePayload(string iss, string sub, string aud, long iat, long exp, string jti, string scope, string cnf, int posture, string? act)
    {
        using var ms = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("iss", iss);
            w.WriteString("sub", sub);
            w.WriteString("aud", aud);
            w.WriteNumber("iat", iat);
            w.WriteNumber("exp", exp);
            w.WriteString("jti", jti);
            w.WriteString("scope", scope);
            w.WriteString("cnf", cnf);
            w.WriteNumber("posture", posture);
            if (act is not null) w.WriteString("act", act);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static string SignToken(Cluster cluster, byte[] payload)
    {
        var header = B64UrlEncode(Encoding.ASCII.GetBytes($"{{\"alg\":\"ES384+ML-DSA-65\",\"kid\":\"{cluster.Id}#{cluster.Generation}\"}}"));
        var body = B64UrlEncode(payload);
        var input = Encoding.ASCII.GetBytes(header + "." + body);
        return $"{header}.{body}.{B64UrlEncode(cluster.Signer.SignData(input, HashAlgorithmName.SHA384))}.{B64UrlEncode(HMACSHA512.HashData(cluster.PqcKey, input))}";
    }

    private static string B64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] B64UrlDecode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    private void DisposeAllLocked()
    {
        foreach (var c in _clusters.Values) c.Dispose();
        foreach (var w in _workloads.Values) w.Key.Dispose();
        _clusters.Clear();
        _links.Clear();
        _workloads.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) DisposeAllLocked();
    }
}
