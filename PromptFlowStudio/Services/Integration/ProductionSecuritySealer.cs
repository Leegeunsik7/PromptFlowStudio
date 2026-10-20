using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Inference;

namespace PromptFlowStudio.Services.Integration;

/// <summary>종단간 보안 기둥(4주) — 콘텐츠 출처 · WORM 원장 · 기업 신원 · 제로 트러스트 메쉬.</summary>
public enum SealPillar { Provenance, Ledger, Identity, Mesh }

/// <summary>기둥 1개의 봉인 판정(Unknown = 미주입 프로파일 · 실패 아님).</summary>
public enum SealVerdict { Unknown, Pass, Warn, Fail }

/// <summary>기둥 1개의 검사 결과(불변 · HUD 표출).</summary>
public sealed record SecurityPillarResult(SealPillar Pillar, string Name, SealVerdict Verdict, string Metric, string? Issue);

/// <summary>프로덕션 보안 봉인 인증서(불변 · 4주 스냅샷을 SHA-256 으로 하드 바인딩 · 비밀키 구성 시 HMAC-SHA256 서명 동봉).</summary>
public sealed record ProductionSealCertificate(
    DateTimeOffset At, string Version, SealVerdict Verdict,
    int Pass, int Warn, int Fail, int Unknown,
    string FingerprintHex, string SignatureHex, double DurationMs,
    IReadOnlyList<SecurityPillarResult> Pillars)
{
    /// <summary>프로덕션 출하 가능 여부(Fail 0 · Pass 1개 이상).</summary>
    public bool IsSealed => Fail == 0 && Pass > 0;

    /// <summary>서명 동봉 여부(HMAC 비밀키 구성 시에만 true).</summary>
    public bool IsSigned => SignatureHex.Length > 0;

    /// <summary>폐쇄망 반출용 정규화 JSON(순수 <see cref="StringBuilder"/> · InvariantCulture · 외부 직렬화기 0).</summary>
    public string ToJson()
    {
        var sb = new StringBuilder(512);
        sb.Append("{\"at\":\"").Append(At.ToString("O", CultureInfo.InvariantCulture))
          .Append("\",\"version\":\"").Append(Version)
          .Append("\",\"verdict\":\"").Append(Verdict.ToString())
          .Append("\",\"sealed\":").Append(IsSealed ? "true" : "false")
          .Append(",\"pass\":").Append(Pass.ToString(CultureInfo.InvariantCulture))
          .Append(",\"warn\":").Append(Warn.ToString(CultureInfo.InvariantCulture))
          .Append(",\"fail\":").Append(Fail.ToString(CultureInfo.InvariantCulture))
          .Append(",\"unknown\":").Append(Unknown.ToString(CultureInfo.InvariantCulture))
          .Append(",\"durationMs\":").Append(DurationMs.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(",\"fingerprint\":\"").Append(FingerprintHex)
          .Append("\",\"signature\":\"").Append(SignatureHex)
          .Append("\",\"pillars\":[");
        for (var i = 0; i < Pillars.Count; i++)
        {
            var p = Pillars[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"pillar\":\"").Append(p.Pillar.ToString())
              .Append("\",\"name\":\"").Append(Escape(p.Name))
              .Append("\",\"verdict\":\"").Append(p.Verdict.ToString())
              .Append("\",\"metric\":\"").Append(Escape(p.Metric))
              .Append("\",\"issue\":");
            if (p.Issue is null) sb.Append("null"); else sb.Append('"').Append(Escape(p.Issue)).Append('"');
            sb.Append('}');
        }
        return sb.Append("]}").ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// [PromptFlow Studio v9.0.2] 프로덕션 보안 · 무결성 최종 봉인기(관찰 가능 싱글턴 · 순수 BCL <see cref="SHA256"/>/<see cref="HMACSHA256"/> · 외부 NuGet 0).
///   ▸ <see cref="SealAsync"/>: C2PA 출처(<see cref="C2PaWatermarkEngine"/>) · WORM 원장(<see cref="QuantumResistantLedgerSigner"/>) ·
///     SAML/OIDC 신원(<see cref="EnterpriseSsoEngine"/>) · ZTNA 메쉬(<see cref="ZeroTrustMeshProxy"/>) 4주를 <b>단일 헬스체크 인터페이스</b>로 통일 판정한다.
///   ▸ 각 기둥은 해당 서비스의 공개 지표만 읽는다 — 내부 상태를 소유/변조하지 않아 기존 실 로직은 무변경(제1헌장).
///   ▸ 판정 집합을 정규화 직렬화해 SHA-256 지문으로 하드 바인딩하고, <see cref="ConfigureSecret"/> 로 비밀키가 주입된 경우 HMAC-SHA256 서명을 동봉한다.
///   ▸ <see cref="VerifyCertificate"/> 는 인증서를 재정규화해 지문/서명을 <see cref="CryptographicOperations.FixedTimeEquals"/> 상수시간 대조한다(위변조 즉시 검출).
///   ▸ 미주입(미등록) 서비스는 <see cref="SealVerdict.Unknown"/> 으로 건너뛴다 — 데스크톱/모바일 프로파일 차이로 봉인이 실패하지 않는다(제5헌장).
///   ⚠ <see cref="OnChange"/> 는 봉인 스레드(백그라운드)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장). 프로브 예외는 삼켜 Fail 로 기록(앱 동작 무영향).
/// </summary>
public sealed class ProductionSecuritySealer
{
    private const string SealVersion = "v9.0.0";

    private readonly C2PaWatermarkEngine? _c2pa;
    private readonly QuantumResistantLedgerSigner? _ledger;
    private readonly EnterpriseSsoEngine? _sso;
    private readonly ZeroTrustMeshProxy? _mesh;
    private readonly SemaphoreSlim _sealGate = new(1, 1);
    private readonly object _gate = new();
    private readonly List<SecurityPillarResult> _pillars = new(4);

    private string? _secret;

    public ProductionSecuritySealer(
        C2PaWatermarkEngine? c2pa = null,
        QuantumResistantLedgerSigner? ledger = null,
        EnterpriseSsoEngine? sso = null,
        ZeroTrustMeshProxy? mesh = null)
    {
        _c2pa = c2pa;
        _ledger = ledger;
        _sso = sso;
        _mesh = mesh;
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;

    /// <summary>봉인 실패(Fail) 발생 시 1회 경보.</summary>
    public event Action<string>? OnSealAlert;

    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public bool IsSealing { get; private set; }
    public string? CurrentPillar { get; private set; }
    public long SealCount { get; private set; }
    public SealVerdict LastVerdict { get; private set; } = SealVerdict.Unknown;
    public string? LastFingerprint { get; private set; }
    public string? LastIssue { get; private set; }
    public DateTimeOffset? LastSealedAt { get; private set; }
    public ProductionSealCertificate? LastCertificate { get; private set; }

    /// <summary>직전 봉인의 기둥별 결과 스냅샷(복사본 · 열거 중 변조 안전).</summary>
    public IReadOnlyList<SecurityPillarResult> Pillars
    {
        get { lock (_gate) return _pillars.ToArray(); }
    }

    /// <summary>프로덕션 출하 가능 여부(직전 인증서 기준).</summary>
    public bool IsProductionSealed => LastCertificate?.IsSealed == true;

    /// <summary>인증서 HMAC 서명 비밀키 구성(미구성 시 지문만 발급).</summary>
    public void ConfigureSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret)) _secret = secret!.Trim();
    }

    /// <summary>4주 종단간 봉인 실행 → 단일 무결성 스냅샷 인증서 발급.</summary>
    public async Task<ProductionSealCertificate> SealAsync(CancellationToken ct = default)
    {
        await _sealGate.WaitAsync(ct).ConfigureAwait(false);
        var startTicks = DateTime.UtcNow.Ticks;
        try
        {
            IsSealing = true;
            LastIssue = null;
            Notify();

            var results = new List<SecurityPillarResult>(4)
            {
                Probe(SealPillar.Provenance, "C2PA 출처 서명", ProbeProvenance, ct),
                Probe(SealPillar.Ledger, "WORM 양자내성 원장", ProbeLedger, ct),
                Probe(SealPillar.Identity, "SAML/OIDC 기업 신원", ProbeIdentity, ct),
                Probe(SealPillar.Mesh, "ZTNA 메쉬 프록시", ProbeMesh, ct),
            };

            int pass = 0, warn = 0, fail = 0, unknown = 0;
            string? firstIssue = null;
            foreach (var r in results)
            {
                switch (r.Verdict)
                {
                    case SealVerdict.Pass: pass++; break;
                    case SealVerdict.Warn: warn++; break;
                    case SealVerdict.Fail: fail++; break;
                    default: unknown++; break;
                }
                if (r.Issue is not null && firstIssue is null && r.Verdict != SealVerdict.Pass)
                    firstIssue = $"{r.Name}: {r.Issue}";
            }

            var verdict = fail > 0 ? SealVerdict.Fail
                : warn > 0 ? SealVerdict.Warn
                : pass > 0 ? SealVerdict.Pass
                : SealVerdict.Unknown;

            var at = DateTimeOffset.UtcNow;
            var canonical = BuildCanonical(at, verdict, pass, warn, fail, unknown, results);
            var fingerprint = Sha256Hex(canonical);
            var signature = _secret is null ? string.Empty : HmacHex(canonical, _secret);
            var durationMs = (DateTime.UtcNow.Ticks - startTicks) / (double)TimeSpan.TicksPerMillisecond;

            var cert = new ProductionSealCertificate(
                at, SealVersion, verdict, pass, warn, fail, unknown,
                fingerprint, signature, durationMs, results);

            lock (_gate)
            {
                _pillars.Clear();
                _pillars.AddRange(results);
            }

            SealCount++;
            LastVerdict = verdict;
            LastFingerprint = fingerprint;
            LastIssue = firstIssue;
            LastSealedAt = at;
            LastCertificate = cert;
            CurrentPillar = null;

            if (fail > 0) OnSealAlert?.Invoke(firstIssue ?? "보안 봉인 실패");
            return cert;
        }
        finally
        {
            IsSealing = false;
            _sealGate.Release();
            Notify();
        }
    }

    /// <summary>인증서 재정규화 → 지문(및 서명) 상수시간 대조. 위변조 시 false.</summary>
    public bool VerifyCertificate(ProductionSealCertificate? cert)
    {
        if (cert is null) return false;
        var canonical = BuildCanonical(cert.At, cert.Verdict, cert.Pass, cert.Warn, cert.Fail, cert.Unknown, cert.Pillars);
        if (!FixedEquals(Sha256Hex(canonical), cert.FingerprintHex)) return false;
        if (cert.SignatureHex.Length == 0) return _secret is null;
        return _secret is not null && FixedEquals(HmacHex(canonical, _secret), cert.SignatureHex);
    }

    // ── 기둥별 프로브(예외는 Fail 로 흡수 · 미주입은 Unknown) ──

    private SecurityPillarResult Probe(SealPillar pillar, string name, Func<(SealVerdict Verdict, string Metric, string? Issue)?> probe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentPillar = name;
        Notify();
        try
        {
            var r = probe();
            if (r is null)
                return new SecurityPillarResult(pillar, name, SealVerdict.Unknown, "미주입", "프로파일 미포함 — 건너뜀");
            return new SecurityPillarResult(pillar, name, r.Value.Verdict, r.Value.Metric, r.Value.Issue);
        }
        catch (Exception ex)
        {
            return new SecurityPillarResult(pillar, name, SealVerdict.Fail, "프로브 예외", ex.Message);
        }
    }

    private (SealVerdict Verdict, string Metric, string? Issue)? ProbeProvenance()
    {
        if (_c2pa is null) return null;
        var metric = $"서명 {_c2pa.SignedCount.ToString(CultureInfo.InvariantCulture)} · 검증 {_c2pa.VerifiedOk.ToString(CultureInfo.InvariantCulture)} · 위변조 {_c2pa.TamperCount.ToString(CultureInfo.InvariantCulture)}";
        if (_c2pa.TamperCount > 0) return (SealVerdict.Fail, metric, _c2pa.LastAlert ?? "매니페스트 위변조 검출");
        if (_c2pa.SignedCount == 0) return (SealVerdict.Warn, metric, "서명 이력 없음 — 출처 미증명");
        return (SealVerdict.Pass, metric, null);
    }

    private (SealVerdict Verdict, string Metric, string? Issue)? ProbeLedger()
    {
        if (_ledger is null) return null;
        var head = _ledger.ChainHead;
        var headShort = head.Length >= 8 ? head.Substring(0, 8) : head;
        var metric = $"엔트리 {_ledger.EntryCount.ToString(CultureInfo.InvariantCulture)} · 헤드 {headShort} · {_ledger.Keys.Algorithm}";
        if (_ledger.TamperCount > 0) return (SealVerdict.Fail, metric, _ledger.LastAlert ?? "원장 위변조 검출");
        if (_ledger.EntryCount == 0) return (SealVerdict.Warn, metric, "원장 공백 — 체인 미개시");
        var chain = _ledger.VerifyChain();
        if (!chain.Valid) return (SealVerdict.Fail, metric, $"체인 단절 seq={chain.FirstMismatchSeq.ToString(CultureInfo.InvariantCulture)} · {chain.Reason}");
        return (SealVerdict.Pass, metric, null);
    }

    private (SealVerdict Verdict, string Metric, string? Issue)? ProbeIdentity()
    {
        if (_sso is null) return null;
        var metric = $"통과 {_sso.ValidatedOk.ToString(CultureInfo.InvariantCulture)} · 거부 {_sso.Rejected.ToString(CultureInfo.InvariantCulture)} · 세션 {_sso.ActiveSessions.ToString(CultureInfo.InvariantCulture)}";
        if (!_sso.RsaConfigured && !_sso.HmacConfigured)
            return (SealVerdict.Fail, metric, "서명 키 미구성 — 토큰 검증 불가");
        if (_sso.Rejected > 0) return (SealVerdict.Warn, metric, _sso.LastError ?? "거부 이력 존재");
        return (SealVerdict.Pass, metric, null);
    }

    private (SealVerdict Verdict, string Metric, string? Issue)? ProbeMesh()
    {
        if (_mesh is null) return null;
        var denied = _mesh.Denied401 + _mesh.DeniedOpen;
        var metric = $"허용 {_mesh.Allowed.ToString(CultureInfo.InvariantCulture)} · 차단 {denied.ToString(CultureInfo.InvariantCulture)} · 피어 {_mesh.PeerCount.ToString(CultureInfo.InvariantCulture)}";
        if (_mesh.PeerCount == 0) return (SealVerdict.Warn, metric, "피어 미등록 — 메쉬 미개통");
        if (_mesh.TripCount > 0) return (SealVerdict.Warn, metric, $"서킷 트립 {_mesh.TripCount.ToString(CultureInfo.InvariantCulture)}회");
        return (SealVerdict.Pass, metric, null);
    }

    // ── 정규화 · 해시 ──

    // 집계(Pass/Warn/Fail/Unknown)까지 정규화에 포함해야 "기둥은 그대로 두고 카운트만 조작"한 위조 인증서가 검출된다.
    private static string BuildCanonical(DateTimeOffset at, SealVerdict verdict,
        int pass, int warn, int fail, int unknown, IReadOnlyList<SecurityPillarResult> pillars)
    {
        var sb = new StringBuilder(256);
        sb.Append(SealVersion).Append('|')
          .Append(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
          .Append(verdict.ToString()).Append('|')
          .Append(pass.ToString(CultureInfo.InvariantCulture)).Append('/')
          .Append(warn.ToString(CultureInfo.InvariantCulture)).Append('/')
          .Append(fail.ToString(CultureInfo.InvariantCulture)).Append('/')
          .Append(unknown.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < pillars.Count; i++)
        {
            var p = pillars[i];
            sb.Append('\n').Append(p.Pillar.ToString()).Append('|').Append(p.Name).Append('|')
              .Append(p.Verdict.ToString()).Append('|').Append(p.Metric).Append('|').Append(p.Issue ?? "-");
        }
        return sb.ToString();
    }

    private static string Sha256Hex(string payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);
        return Convert.ToHexString(hash);
    }

    private static string HmacHex(string payload, string secret)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload), mac);
        return Convert.ToHexString(mac);
    }

    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }
}
