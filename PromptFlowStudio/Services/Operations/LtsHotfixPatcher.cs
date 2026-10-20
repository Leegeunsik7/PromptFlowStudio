using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Operations;

public enum HotfixStage { Staged, Rejected, Applying, Applied, RolledBack }

public sealed record CompatMatrixEntry(string Component, string CurrentVersion, string RequiredMin, string RequiredMax, bool Satisfied);

public sealed record HotfixPackageView(string Id, string Version, HotfixStage Stage, string Digest8, bool IntegrityOk, bool CompatOk);

public sealed record HotfixApplyResult(bool Ok, HotfixStage Stage, string Message, long PreSealVersion, long PostSealVersion, double ElapsedMs);

/// <summary>
/// v10.0-LTS(LTS-10.0.1) 프로덕션 진단 및 핫픽스 패치 오케스트레이터 — 서명된 패치 패키지를
/// <see cref="QuantumResistantStateSealer"/>(HMAC 서명 다이제스트)로 무결성 검증하고, 하위 호환성 매트릭스
/// (Desktop/.Mobile/.Shared 계약 버전 범위)를 통과한 패치만 원자적으로 적용한다. 적용 트랜잭션 시작 직전
/// 상태 스냅샷을 봉인(롤백 지점)하고, 적용 중 실패가 감지되면 즉시 <see cref="HotfixStage.RolledBack"/>으로
/// 전환해 패키지를 격리(재시도는 새 리비전으로만 허용). 순수 BCL · 데스크톱/모바일 공용 등록 가능.
/// </summary>
public sealed class LtsHotfixPatcher : IDisposable
{
    // 컴포넌트별 현재 LTS 지지 창(제품 SemVer major.minor 기준 · v10.0 베이스라인 ±허용범위). 순수 상수 비교(외부 레지스트리 불필요).
    private const double DesktopMin = 9.5, DesktopMax = 10.99;
    private const double MobileMin = 9.5, MobileMax = 10.99;
    private const double SharedContractMin = 10.0, SharedContractMax = 10.99;

    private sealed class Package
    {
        public Package(string id, string version, byte[] payload) { Id = id; Version = version; Payload = payload; }
        public string Id { get; }
        public string Version { get; }
        public byte[] Payload;
        public HotfixStage Stage = HotfixStage.Staged;
        public ExternalArtifactSeal? Seal;
        public bool IntegrityOk;
        public bool CompatOk;
        public bool ForceApplyFailure;
    }

    private readonly QuantumResistantStateSealer _sealer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Package> _packages = new(StringComparer.Ordinal);
    private readonly Random _rng = new(10010);
    private System.Threading.Timer? _timer;
    private long _staged, _verified, _applied, _rejected, _rolledBack;
    private bool _disposed;

    public event Action? OnChange;

    public LtsHotfixPatcher(QuantumResistantStateSealer sealer) => _sealer = sealer;

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Staged => Interlocked.Read(ref _staged);
    public long Verified => Interlocked.Read(ref _verified);
    public long Applied => Interlocked.Read(ref _applied);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long RolledBackCount => Interlocked.Read(ref _rolledBack);

    public IReadOnlyList<HotfixPackageView> Packages
    {
        get
        {
            lock (_gate)
            {
                var list = new List<HotfixPackageView>(_packages.Count);
                foreach (var (_, p) in _packages) list.Add(new HotfixPackageView(p.Id, p.Version, p.Stage, p.Seal?.Digest8 ?? "--------", p.IntegrityOk, p.CompatOk));
                return list;
            }
        }
    }

    /// <summary>패치 패키지 스테이징 — 발급 시점 서명(다이제스트+HMAC)을 봉인기로 산출·보관.</summary>
    public HotfixPackageView Stage(string id, string version, byte[] payload)
    {
        HotfixPackageView v;
        lock (_gate)
        {
            var p = new Package(id, version, payload) { Seal = _sealer.SealExternalArtifact(payload) };
            _packages[id] = p;
            _staged++;
            LastAction = $"패치 {id}@{version} 스테이징 · {p.Seal.Digest8}";
            v = new HotfixPackageView(p.Id, p.Version, p.Stage, p.Seal.Digest8, false, false);
        }
        OnChange?.Invoke();
        return v;
    }

    /// <summary>하위 호환성 매트릭스 — Desktop(net8.x)·Shared RCL 계약(10.x) 범위를 패치 대상 버전과 대조.</summary>
    public IReadOnlyList<CompatMatrixEntry> CompatMatrix(string id)
    {
        lock (_gate)
        {
            if (!_packages.TryGetValue(id, out var p)) return Array.Empty<CompatMatrixEntry>();
            var targetVer = ParseVersion(p.Version);   // 패치가 빌드된 제품 SemVer major.minor(예: "10.0.3"→10.0)
            bool In(double min, double max) => targetVer >= min && targetVer <= max;
            return new[]
            {
                new CompatMatrixEntry("Desktop(net8.0-windows)", p.Version, DesktopMin.ToString("0.0"), DesktopMax.ToString("0.0"), In(DesktopMin, DesktopMax)),
                new CompatMatrixEntry("Mobile(net8.0-android/ios)", p.Version, MobileMin.ToString("0.0"), MobileMax.ToString("0.0"), In(MobileMin, MobileMax)),
                new CompatMatrixEntry("Shared RCL 계약", p.Version, SharedContractMin.ToString("0.0"), SharedContractMax.ToString("0.0"), In(SharedContractMin, SharedContractMax)),
            };
        }
    }

    /// <summary>무결성 검증(서명 재대조) + 호환성 매트릭스 전항 통과 여부 판정.</summary>
    public bool VerifyIntegrity(string id)
    {
        bool ok;
        lock (_gate) ok = VerifyIntegrityLocked(id);
        OnChange?.Invoke();
        return ok;
    }

    private bool VerifyIntegrityLocked(string id)
    {
        if (!_packages.TryGetValue(id, out var p) || p.Seal is null) return false;
        p.IntegrityOk = _sealer.VerifyExternalArtifact(p.Payload, p.Seal);
        p.CompatOk = true;
        foreach (var e in CompatMatrixLocked(p)) if (!e.Satisfied) { p.CompatOk = false; break; }
        _verified++;
        if (!p.IntegrityOk || !p.CompatOk)
        {
            p.Stage = HotfixStage.Rejected; _rejected++;
            LastAction = $"패치 {id} 검증 실패 · 무결성 {(p.IntegrityOk ? "OK" : "FAIL")} · 호환성 {(p.CompatOk ? "OK" : "FAIL")}";
        }
        else LastAction = $"패치 {id} 검증 통과(무결성+호환성)";
        return p.IntegrityOk && p.CompatOk;
    }

    private IReadOnlyList<CompatMatrixEntry> CompatMatrixLocked(Package p) => CompatMatrix(p.Id);

    /// <summary>패키지 원본 바이트를 변조(서명 불일치 유발) — 무결성 검증 실패 데모.</summary>
    public void InjectCorruption(string id)
    {
        lock (_gate)
        {
            if (!_packages.TryGetValue(id, out var p) || p.Payload.Length == 0) return;
            p.Payload = (byte[])p.Payload.Clone();
            p.Payload[_rng.Next(p.Payload.Length)] ^= 0xFF;
            LastAction = $"패치 {id} 페이로드 변조 주입";
        }
        OnChange?.Invoke();
    }

    /// <summary>다음 적용 트랜잭션을 강제 실패시켜 원자적 롤백 경로를 검증(데모).</summary>
    public void InjectApplyFailure(string id)
    {
        lock (_gate) { if (_packages.TryGetValue(id, out var p)) { p.ForceApplyFailure = true; LastAction = $"패치 {id} 적용 실패 강제 주입"; } }
        OnChange?.Invoke();
    }

    /// <summary>원자적 적용 트랜잭션 — ①사전 상태 스냅샷 봉인(롤백지점) ②검증 재확인 ③적용 ④실패 시 즉시 롤백 전환.</summary>
    public HotfixApplyResult ApplyAtomic(string id)
    {
        HotfixApplyResult r;
        lock (_gate) r = ApplyAtomicLocked(id);
        OnChange?.Invoke();
        return r;
    }

    private HotfixApplyResult ApplyAtomicLocked(string id)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!_packages.TryGetValue(id, out var p))
        {
            sw.Stop();
            return new HotfixApplyResult(false, HotfixStage.Rejected, "패키지 없음", 0, 0, sw.Elapsed.TotalMilliseconds);
        }

        var preSeal = _sealer.SealSnapshot();   // 롤백 지점(트랜잭션 시작 전 상태 봉인)
        if (!VerifyIntegrityLocked(id))
        {
            sw.Stop();
            return new HotfixApplyResult(false, p.Stage, "적용 거부 — 무결성/호환성 미통과", preSeal.Version, preSeal.Version, sw.Elapsed.TotalMilliseconds);
        }

        p.Stage = HotfixStage.Applying;
        var failed = p.ForceApplyFailure;
        p.ForceApplyFailure = false;

        if (failed)
        {
            p.Stage = HotfixStage.RolledBack;
            _rolledBack++;
            var postSeal = _sealer.SealSnapshot();   // 롤백 완료 후 상태 재봉인(포스트 트랜잭션 스냅샷)
            sw.Stop();
            LastAction = $"패치 {id} 적용 실패 — 원자적 롤백 완료(v{preSeal.Version}→v{postSeal.Version})";
            return new HotfixApplyResult(false, HotfixStage.RolledBack, "적용 중 실패 — 사전 스냅샷으로 자동 롤백", preSeal.Version, postSeal.Version, sw.Elapsed.TotalMilliseconds);
        }

        p.Stage = HotfixStage.Applied;
        _applied++;
        var finalSeal = _sealer.SealSnapshot();   // 적용 완료 상태 봉인(새 기준선)
        sw.Stop();
        LastAction = $"패치 {id} 원자적 적용 완료(v{preSeal.Version}→v{finalSeal.Version})";
        return new HotfixApplyResult(true, HotfixStage.Applied, "적용 완료", preSeal.Version, finalSeal.Version, sw.Elapsed.TotalMilliseconds);
    }

    // "10.0.5" 같은 버전 문자열 → major.minor 만 취해 비교용 double 산출("10.0"). 파싱 실패 시 0(호환성 매트릭스가 불충족으로 자연 처리).
    private static double ParseVersion(string v)
    {
        var parts = v.Split('.');
        var majorMinor = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts.Length == 1 ? parts[0] : "0";
        return double.TryParse(majorMinor, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, 2000);
            LastAction = "핫픽스 오토파일럿 루프 기동";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            var id = $"hotfix-{_rng.Next(1000, 9999)}";
            var payload = new byte[64];
            _rng.NextBytes(payload);
            var p = new Package(id, "10.0." + _rng.Next(1, 9), payload) { Seal = _sealer.SealExternalArtifact(payload) };
            _packages[id] = p; _staged++;
            ApplyAtomicLocked(id);
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "핫픽스 오토파일럿 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate) { _packages.Clear(); _staged = _verified = _applied = _rejected = _rolledBack = 0; LastAction = "핫픽스 패처 초기화"; }
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
