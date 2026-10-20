using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PromptFlowStudio.Services.Security;

public enum ArtifactValidationState { Signed, Verified, TamperDetected }

/// <summary>아티팩트 1건의 서명/검증 이력.</summary>
public sealed record ArtifactValidationRecord(string ArtifactId, ArtifactValidationState State, string Digest8, long Bytes, DateTimeOffset At, string Note);

public sealed record IntegrityCheckReport(bool Ok, int Checked, int Tampered, double ElapsedMs);

/// <summary>
/// v10.3.2 릴리스 무결성 최종 검증기 — <see cref="QuantumResistantStateSealer"/> 를 재사용해(중복 암호구현 금지)
/// 최종 패키징 아티팩트(zip 등)를 <b>SHA3-256(런타임 미가용 시 SHA-256 Graceful 폴백)</b> 다이제스트 + HMAC 서명으로
/// 봉인하고, 배포 직전 전체 아티팩트를 재해시해 상수시간 대조로 위변조를 검출한다. 서명 이력은 인메모리 원장으로
/// 보존(HUD 조회용). 순수 BCL(파일 I/O + 암호화) · 하드웨어 비의존 · 데스크톱/모바일 공용.
/// </summary>
public sealed class ReleaseIntegrityValidator
{
    private readonly QuantumResistantStateSealer _sealer;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ExternalArtifactSeal Seal, long Bytes)> _ledger = new(StringComparer.Ordinal);
    private readonly List<ArtifactValidationRecord> _history = new();
    private long _signed, _verified, _tamperDetected;

    public event Action? OnChange;

    public ReleaseIntegrityValidator(QuantumResistantStateSealer sealer) => _sealer = sealer;

    public string? LastAction { get; private set; }
    public bool Sha3Active => QuantumResistantStateSealer.Sha3Supported;
    public long Signed => Interlocked.Read(ref _signed);
    public long Verified => Interlocked.Read(ref _verified);
    public long TamperDetected => Interlocked.Read(ref _tamperDetected);
    public int LedgerCount { get { lock (_gate) return _ledger.Count; } }

    public IReadOnlyList<ArtifactValidationRecord> History { get { lock (_gate) return new List<ArtifactValidationRecord>(_history); } }

    /// <summary>원장에 등록된 아티팩트의 전체 봉인(다이제스트/서명 전체 바이트) 조회 — v10.5.0-F5 매니페스트/.sig
    ///   내보내기 등 외부 연동 전용(HUD 표시용 <see cref="ArtifactValidationRecord.Digest8"/> 축약값과 별개).</summary>
    public ExternalArtifactSeal? GetSeal(string artifactId)
    {
        lock (_gate) return _ledger.TryGetValue(artifactId, out var e) ? e.Seal : null;
    }

    /// <summary>파일 1건을 최종 서명(SHA3/HMAC) — 발급 시점 봉인을 원장에 보존.</summary>
    public ArtifactValidationRecord SignFile(string artifactId, string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var seal = _sealer.SealExternalArtifactSha3(bytes);
        ArtifactValidationRecord rec;
        lock (_gate)
        {
            _ledger[artifactId] = (seal, bytes.LongLength);
            rec = new ArtifactValidationRecord(artifactId, ArtifactValidationState.Signed, seal.Digest8, bytes.LongLength, DateTimeOffset.UtcNow,
                $"{(Sha3Active ? "SHA3-256" : "SHA-256(폴백)")} · HMAC 서명 완료");
            _history.Insert(0, rec);
            _signed++;
            LastAction = $"{artifactId} 서명 완료 · {rec.Digest8}";
        }
        OnChange?.Invoke();
        return rec;
    }

    /// <summary>원장에 서명된 파일 1건을 재해시해 발급 시점 봉인과 상수시간 대조.</summary>
    public ArtifactValidationRecord VerifyFile(string artifactId, string filePath)
    {
        ArtifactValidationRecord rec;
        lock (_gate)
        {
            if (!_ledger.TryGetValue(artifactId, out var entry))
            {
                rec = new ArtifactValidationRecord(artifactId, ArtifactValidationState.TamperDetected, "--------", 0, DateTimeOffset.UtcNow, "원장에 서명 이력 없음(미서명 아티팩트)");
                _history.Insert(0, rec);
                OnChange?.Invoke();
                return rec;
            }
            var bytes = File.ReadAllBytes(filePath);
            var ok = _sealer.VerifyExternalArtifactSha3(bytes, entry.Seal);
            _verified++;
            if (!ok) _tamperDetected++;
            rec = new ArtifactValidationRecord(artifactId, ok ? ArtifactValidationState.Verified : ArtifactValidationState.TamperDetected,
                entry.Seal.Digest8, bytes.LongLength, DateTimeOffset.UtcNow, ok ? "무결성 확인" : "다이제스트/서명 불일치 — 변조 의심");
            _history.Insert(0, rec);
            LastAction = $"{artifactId} 검증 {(ok ? "통과" : "실패")}";
        }
        OnChange?.Invoke();
        return rec;
    }

    /// <summary>원장에 등록된 전 아티팩트를 일괄 재검증(배포 직전 최종 게이트).</summary>
    public IntegrityCheckReport VerifyAll(Func<string, string?> resolvePath)
    {
        var sw = Stopwatch.StartNew();
        int checkedCount = 0, tampered = 0;
        lock (_gate)
        {
            foreach (var id in new List<string>(_ledger.Keys))
            {
                var path = resolvePath(id);
                if (path is null || !File.Exists(path)) { tampered++; continue; }
                var rec = VerifyFileLockedFreeInternal(id, path);
                checkedCount++;
                if (rec.State == ArtifactValidationState.TamperDetected) tampered++;
            }
        }
        sw.Stop();
        var ok = tampered == 0;
        lock (_gate) LastAction = ok ? $"전체 검증 통과 · {checkedCount}건" : $"변조/누락 {tampered}건 검출 · 배포 차단 권고";
        OnChange?.Invoke();
        return new IntegrityCheckReport(ok, checkedCount, tampered, sw.Elapsed.TotalMilliseconds);
    }

    // VerifyFile 은 자체적으로 lock 을 잡으므로 VerifyAll(이미 lock 보유) 내부 재진입 방지를 위한 락-프리 경로.
    private ArtifactValidationRecord VerifyFileLockedFreeInternal(string artifactId, string filePath)
    {
        var entry = _ledger[artifactId];
        var bytes = File.ReadAllBytes(filePath);
        var ok = _sealer.VerifyExternalArtifactSha3(bytes, entry.Seal);
        _verified++;
        if (!ok) _tamperDetected++;
        var rec = new ArtifactValidationRecord(artifactId, ok ? ArtifactValidationState.Verified : ArtifactValidationState.TamperDetected,
            entry.Seal.Digest8, bytes.LongLength, DateTimeOffset.UtcNow, ok ? "무결성 확인" : "다이제스트/서명 불일치 — 변조 의심");
        _history.Insert(0, rec);
        return rec;
    }

    public void ResetAll()
    {
        lock (_gate) { _ledger.Clear(); _history.Clear(); _signed = 0; _verified = 0; _tamperDetected = 0; LastAction = "무결성 검증기 초기화"; }
        OnChange?.Invoke();
    }
}
