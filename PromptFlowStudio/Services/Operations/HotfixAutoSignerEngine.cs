using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Operations;

/// <summary>배포용 핫픽스 매니페스트 — hotfix-manifest.json 으로 그대로 직렬화되는 스키마.</summary>
public sealed record HotfixManifest(
    string ArtifactId, string Version, string FileName, long Bytes, string Algorithm,
    string DigestHex, string SignatureHex, string Digest8, string HotfixStage, bool IntegrityOk, bool CompatOk,
    DateTimeOffset SignedAtUtc, string Note);

/// <summary>1건의 서명+내보내기 결과 — Ok=false 라도 사유(Note) 확인 가능(Graceful).</summary>
public sealed record HotfixSignResult(bool Ok, string? ManifestPath, string? SigPath, string ArtifactId, string Digest8, string Algorithm, string Note);

/// <summary>
/// [PromptFlow Studio v10.5.0-F5] 핫픽스 자동 서명기 — <see cref="LtsHotfixPatcher"/>(스테이징/호환성 매트릭스
///   라이프사이클)와 <see cref="ReleaseIntegrityValidator"/>(SHA3-256·런타임 미가용 시 SHA-256 폴백 + HMAC 서명,
///   SecureSecretVault 키 재사용)를 그대로 재사용해(중복 암호구현 금지) 지정 패치 파일(zip/dll)을 스테이징+서명하고,
///   배포용 <c>{id}_{version}.hotfix-manifest.json</c> + <c>.sig</c>(서명 16진 텍스트) 파일을
///   <c>%LocalAppData%/PromptFlowStudio/HotfixReleases/</c> 에 출력한다. 순수 BCL(System.IO/Text.Json).
/// </summary>
public sealed class HotfixAutoSignerEngine
{
    private readonly LtsHotfixPatcher _patcher;
    private readonly ReleaseIntegrityValidator _validator;
    private readonly object _gate = new();

    private long _signed, _failures;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public HotfixSignResult? LastResult { get; private set; }
    public string OutputRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "HotfixReleases");

    public long Signed => Interlocked.Read(ref _signed);
    public long Failures => Interlocked.Read(ref _failures);

    public HotfixAutoSignerEngine(LtsHotfixPatcher patcher, ReleaseIntegrityValidator validator)
    {
        _patcher = patcher;
        _validator = validator;
    }

    /// <summary>패치 파일 1건을 스테이징(라이프사이클)+서명(SHA3/HMAC)하고 매니페스트/.sig 를 출력.
    ///   실패해도 예외를 던지지 않고 Ok=false 로 사유를 반환(Graceful).</summary>
    public HotfixSignResult SignAndExport(string patchFilePath, string version, string? artifactId = null)
    {
        var id = string.IsNullOrWhiteSpace(artifactId) ? Path.GetFileNameWithoutExtension(patchFilePath ?? "") : artifactId!;
        if (string.IsNullOrWhiteSpace(id)) id = "hotfix-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            if (string.IsNullOrWhiteSpace(patchFilePath) || !File.Exists(patchFilePath))
                return Fail(id, "패치 파일을 찾을 수 없습니다: " + patchFilePath);

            var payload = File.ReadAllBytes(patchFilePath);

            // 1) 핫픽스 라이프사이클 스테이징 + 무결성/호환성 판정(LtsHotfixPatcher 자체 정책 재사용).
            _patcher.Stage(id, version, payload);
            _patcher.VerifyIntegrity(id);
            var staged = _patcher.Packages.FirstOrDefault(p => p.Id == id);

            // 2) 배포용 최종 서명(SHA3-256·미가용 시 SHA-256 폴백 + HMAC) — ReleaseIntegrityValidator 재사용.
            var rec = _validator.SignFile(id, patchFilePath);
            var seal = _validator.GetSeal(id);
            if (seal is null) return Fail(id, "서명은 완료됐으나 봉인 조회 실패(내부 불일치)");

            Directory.CreateDirectory(OutputRoot);
            var baseName = $"{id}_{version}";
            var manifestPath = Path.Combine(OutputRoot, baseName + ".hotfix-manifest.json");
            var sigPath = Path.Combine(OutputRoot, baseName + ".sig");

            var result = WriteManifestAndSigLocked(id, version, Path.GetFileName(patchFilePath), rec, seal, manifestPath, sigPath,
                staged?.Stage.ToString() ?? "Unknown", staged?.IntegrityOk ?? false, staged?.CompatOk ?? false);
            lock (_gate) { LastResult = result; LastAction = $"{id}@{version} 서명 완료 · {result.Algorithm} · {seal.Digest8}"; }
            Interlocked.Increment(ref _signed);
            OnChange?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            Program.Log("HotfixAutoSigner.SignAndExport", ex);
            return Fail(id, "예외: " + ex.Message);
        }
    }

    /// <summary>v10.7.0-LTS: 최종 배포 zip 전용 서명 — <see cref="SignAndExport"/> 와 동일한 핵심 봉인 로직
    ///   (<see cref="ReleaseIntegrityValidator"/> 재사용)이지만 출력 파일명/위치를 배포 관례
    ///   (<c>release-manifest.json</c> · <c>{version}.sig</c>, zip 과 동일 폴더)에 맞춘다. 핫픽스 라이프사이클
    ///   (<see cref="LtsHotfixPatcher"/> 스테이징)은 적용 대상이 아니므로 생략 — 최종 릴리스 zip 은 "핫픽스 패치"가
    ///   아니라 배포 산출물 그 자체이기 때문이다.</summary>
    public HotfixSignResult SignReleaseArtifact(string zipFilePath, string version, string outputDir)
    {
        var id = $"release-{version}";
        try
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
                return Fail(id, "릴리스 아티팩트 파일을 찾을 수 없습니다: " + zipFilePath);

            var rec = _validator.SignFile(id, zipFilePath);
            var seal = _validator.GetSeal(id);
            if (seal is null) return Fail(id, "서명은 완료됐으나 봉인 조회 실패(내부 불일치)");

            Directory.CreateDirectory(outputDir);
            var manifestPath = Path.Combine(outputDir, "release-manifest.json");
            var sigPath = Path.Combine(outputDir, $"{version}.sig");

            var result = WriteManifestAndSigLocked(id, version, Path.GetFileName(zipFilePath), rec, seal, manifestPath, sigPath,
                "Released", true, true);
            lock (_gate) { LastResult = result; LastAction = $"{id} 릴리스 서명 완료 · {result.Algorithm} · {seal.Digest8}"; }
            Interlocked.Increment(ref _signed);
            OnChange?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            Program.Log("HotfixAutoSigner.SignReleaseArtifact", ex);
            return Fail(id, "예외: " + ex.Message);
        }
    }

    // SignAndExport/SignReleaseArtifact 공용 — 매니페스트 직렬화+.sig 기록(파일 I/O 만, 카운터/이벤트는 호출부 책임).
    private static HotfixSignResult WriteManifestAndSigLocked(string id, string version, string sourceFileName,
        ArtifactValidationRecord rec, ExternalArtifactSeal seal, string manifestPath, string sigPath,
        string hotfixStage, bool integrityOk, bool compatOk)
    {
        var algorithm = QuantumResistantStateSealer.Sha3Supported ? "SHA3-256" : "SHA-256(Fallback)";
        var manifest = new HotfixManifest(id, version, sourceFileName, rec.Bytes, algorithm,
            Convert.ToHexString(seal.Digest), Convert.ToHexString(seal.Signature), seal.Digest8,
            hotfixStage, integrityOk, compatOk, rec.At, rec.Note);

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, PromptFlowJsonContext.Default.HotfixManifest));
        File.WriteAllText(sigPath, Convert.ToHexString(seal.Signature));
        return new HotfixSignResult(true, manifestPath, sigPath, id, seal.Digest8, algorithm, "서명+매니페스트/.sig 출력 완료");
    }

    private HotfixSignResult Fail(string id, string note)
    {
        var r = new HotfixSignResult(false, null, null, id, "--------", "-", note);
        lock (_gate) { LastResult = r; LastAction = note; }
        Interlocked.Increment(ref _failures);
        OnChange?.Invoke();
        return r;
    }

    public void ResetAll()
    {
        lock (_gate) { LastResult = null; LastAction = "초기화"; }
        Interlocked.Exchange(ref _signed, 0);
        Interlocked.Exchange(ref _failures, 0);
        OnChange?.Invoke();
    }
}
