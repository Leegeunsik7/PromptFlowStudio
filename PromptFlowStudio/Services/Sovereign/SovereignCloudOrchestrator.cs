using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Sovereign;

/// <summary>1-Click 봉인 결과(자산별).</summary>
public sealed record SovereignSealItem(int Asset, string Name, bool Ok, string Stage, string Detail, int Envelope, string? Watermark);

/// <summary>
/// [PromptFlow Studio v16.53.0] ▶ 소버린 클라우드 오케스트레이터 &amp; 1-Click 양자 보안 봉인.
///   ▸ 데모 부트스트랩: 테넌트 4(KR/EU/US/JP · 쿼터) · 주체(역할/등급) · 자산(분류/거주) · Enclave 원격 증명 + 측정값 바인딩 모델 봉인/로드.
///   ▸ 1-Click(테넌트): 자산별 정책 판정(Seal) → 쿼터 예약 → 거주 관할 Enclave 기밀 렌더(암호 채널 왕복) → PQC 하이브리드 봉인 → 왕복 열기 검증 → PQ 워터마크 → PQ 원장 기록 → 쿼터 반납 — 단계별 부분 실패 격리.
/// </summary>
public sealed class SovereignCloudOrchestrator
{
    private readonly SovereignMediaCloudConfidentialMesh _mesh;
    private readonly PostQuantumMediaVaultEngine _vault;
    private readonly MultiTenantStudioGovernanceEngine _gov;
    private readonly List<SovereignSealItem> _last = new();
    private readonly object _gate = new();
    private bool _bootstrapped;

    public SovereignCloudOrchestrator(IServiceProvider sp)
        : this((SovereignMediaCloudConfidentialMesh)sp.GetService(typeof(SovereignMediaCloudConfidentialMesh))!, (PostQuantumMediaVaultEngine)sp.GetService(typeof(PostQuantumMediaVaultEngine))!,
               (MultiTenantStudioGovernanceEngine)sp.GetService(typeof(MultiTenantStudioGovernanceEngine))!) { }

    public SovereignCloudOrchestrator(SovereignMediaCloudConfidentialMesh mesh, PostQuantumMediaVaultEngine vault, MultiTenantStudioGovernanceEngine gov)
    { _mesh = mesh; _vault = vault; _gov = gov; }

    public bool Bootstrapped => _bootstrapped;
    public IReadOnlyList<SovereignSealItem> LastRun { get { lock (_gate) return _last.ToArray(); } }
    public string? LastSummary { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>데모 테넌트·주체·자산 + Enclave 증명/모델 로드(멱등).</summary>
    public void Bootstrap(long nowMs)
    {
        lock (_gate)
        {
            if (_bootstrapped) return;
            if (_gov.TenantCount == 0)
            {
                var kr = _gov.AddTenant("한빛 스튜디오(KR)", Jurisdiction.KR, 8, 65536, 2000);
                var eu = _gov.AddTenant("Aurora Media GmbH(EU)", Jurisdiction.EU, 6, 49152, 1500);
                var us = _gov.AddTenant("Pacific Pictures(US)", Jurisdiction.US, 6, 49152, 1500);
                var jp = _gov.AddTenant("さくらアニメ(JP)", Jurisdiction.JP, 4, 32768, 1000);
                _gov.AddSubject("kim.owner", kr, TenantRole.Owner, Classification.Secret);
                _gov.AddSubject("lee.editor", kr, TenantRole.Editor, Classification.Confidential);
                _gov.AddSubject("anna.owner", eu, TenantRole.Owner, Classification.Secret);
                _gov.AddSubject("john.viewer", us, TenantRole.Viewer, Classification.Internal);
                _gov.AddSubject("sato.operator", jp, TenantRole.Operator | TenantRole.Editor, Classification.Confidential);
                _gov.AddSubject("audit.bot", kr, TenantRole.Auditor, Classification.Secret);
                _gov.AddAsset("시즌2 마스터 영상", kr, Classification.Secret, Jurisdiction.KR);
                _gov.AddAsset("캐릭터 디지털 트윈", kr, Classification.Confidential, Jurisdiction.KR);
                _gov.AddAsset("Brand Campaign Cut", eu, Classification.Confidential, Jurisdiction.EU);
                _gov.AddAsset("Trailer Draft", us, Classification.Internal, Jurisdiction.US);
                _gov.AddAsset("新作アニメ原画", jp, Classification.Confidential, Jurisdiction.JP);
            }
            _mesh.AttestAll(nowMs);
            var lut = new byte[256];
            for (var i = 0; i < 256; i++) lut[i] = (byte)Math.Min(255, (int)(Math.Pow(i / 255.0, 0.8) * 255 + 0.5));   // 감마 보정 "모델"
            for (var i = 0; i < _mesh.EnclaveCount; i++)
                if (_mesh.State(i) == EnclaveState.Attested) _mesh.LoadModel(i, _mesh.SealModel(lut, SovereignMediaCloudConfidentialMesh.Measure("pfs-render-enclave", "16.53.0")));
            if (_vault.Current is null) _vault.RotateKeys(DateTimeOffset.UtcNow);
            _bootstrapped = true;
        }
    }

    /// <summary>테넌트 첫 Owner/Editor 주체.</summary>
    public int ActorOf(int tenant)
    {
        for (var s = 0; s < _gov.SubjectCount; s++) if (_gov.SubjectTenant(s) == tenant && (_gov.SubjectRoles(s) & TenantRole.Owner) != 0) return s;
        for (var s = 0; s < _gov.SubjectCount; s++) if (_gov.SubjectTenant(s) == tenant && (_gov.SubjectRoles(s) & (TenantRole.Editor | TenantRole.Operator)) != 0) return s;
        return -1;
    }

    /// <summary>1-Click 양자 보안 봉인(테넌트 전 자산).</summary>
    public IReadOnlyList<SovereignSealItem> OneClickQuantumSeal(int tenant, int subject, bool mfa, long nowMs, Func<int, byte[]>? payload = null)
    {
        Busy = true;
        var items = new List<SovereignSealItem>();
        try
        {
            Bootstrap(nowMs);
            var ctx = new AccessContext(_gov.TenantJurisdiction(tenant), mfa, nowMs);
            var frame = new byte[4096]; var req = new byte[4096 + 64]; var resp = new byte[4096 + 64]; var back = new byte[4096];
            for (var a = 0; a < _gov.AssetCount; a++)
            {
                if (_gov.AssetTenant(a) != tenant) continue;
                var name = _gov.AssetName(a);
                var d = _gov.Authorize(subject, AssetAction.Seal, a, ctx);
                if (!d.Allowed) { items.Add(new SovereignSealItem(a, name, false, "정책", MultiTenantStudioGovernanceEngine.ReasonLabel(d.Reason), 0, null)); continue; }
                var q = _gov.TryReserve(tenant, 1, 4096, 100);
                if (q != DenyReason.None) { items.Add(new SovereignSealItem(a, name, false, "쿼터", MultiTenantStudioGovernanceEngine.ReasonLabel(q), 0, null)); continue; }
                var session = -1;
                try
                {
                    var (verdict, s, enclave) = _mesh.Place(_gov.AssetResidency(a), nowMs);
                    if (verdict != PlacementVerdict.Placed) { items.Add(new SovereignSealItem(a, name, false, "기밀 배치", SovereignMediaCloudConfidentialMesh.VerdictLabel(verdict), 0, null)); continue; }
                    session = s;
                    var data = payload?.Invoke(a) ?? DemoPayload(a, name);
                    var rendered = new MemoryStream(data.Length);
                    for (var off = 0; off < data.Length; off += frame.Length)
                    {
                        var n = Math.Min(frame.Length, data.Length - off);
                        var rl = _mesh.ClientEncrypt(s, data.AsSpan(off, n), req);
                        var pl = _mesh.ProcessFrame(s, req.AsSpan(0, rl), resp);
                        var bl = pl < 0 ? -1 : _mesh.ClientDecrypt(s, resp.AsSpan(0, pl), back);
                        if (bl != n) throw new InvalidOperationException("기밀 렌더 채널 오류");
                        rendered.Write(back, 0, n);
                    }
                    var owner = _gov.TenantName(tenant);
                    var env = _vault.Seal("asset-" + a.ToString(CultureInfo.InvariantCulture), rendered.ToArray(), owner, _gov.AssetClassification(a).ToString(), DateTimeOffset.UtcNow);
                    var (plain, _) = _vault.Open(env);
                    if (plain.Length != data.Length) throw new InvalidOperationException("봉인 왕복 불일치");
                    var wm = _vault.SignWatermark("asset-" + a.ToString(CultureInfo.InvariantCulture), owner, DateTimeOffset.UtcNow);
                    _vault.AppendLedger(Encoding.UTF8.GetBytes("seal|" + a.ToString(CultureInfo.InvariantCulture) + "|" + env.Length.ToString(CultureInfo.InvariantCulture) + "|" + _mesh.EnclaveName(enclave)));
                    items.Add(new SovereignSealItem(a, name, true, "완료", _mesh.EnclaveName(enclave) + " · " + _gov.AssetResidency(a), env.Length, wm));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
                {
                    items.Add(new SovereignSealItem(a, name, false, "봉인", ex.Message, 0, null));
                }
                finally
                {
                    if (session >= 0) _mesh.CloseSession(session);
                    _gov.Release(tenant, 1, 4096, 100);
                }
            }
            var ok = items.Count(i => i.Ok);
            LastSummary = "🏰 1-Click 양자 봉인 · " + _gov.TenantName(tenant) + " · " + ok.ToString(CultureInfo.InvariantCulture) + "/" + items.Count.ToString(CultureInfo.InvariantCulture) + " 성공";
            lock (_gate) { _last.Clear(); _last.AddRange(items); }
            return items;
        }
        finally { Busy = false; }
    }

    private static byte[] DemoPayload(int asset, string name)
    {
        var b = new byte[16 * 1024];
        var x = (uint)(asset * 2654435761u + 12345);
        for (var i = 0; i < b.Length; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; b[i] = (byte)x; }
        Encoding.UTF8.GetBytes(name).AsSpan().CopyTo(b);
        return b;
    }
}
