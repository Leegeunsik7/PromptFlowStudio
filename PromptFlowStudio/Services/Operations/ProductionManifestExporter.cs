using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using PromptFlowStudio.Services.Security;   // ArtifactValidationRecord(서명 상태 반영)

namespace PromptFlowStudio.Services.Operations;

/// <summary>시크릿/환경변수 규격 1건 — 이름/범위(Runtime 또는 CI/CD)/용도.</summary>
public sealed record SecretSpecEntry(string Name, string Scope, string Purpose);

/// <summary>배포 청사진의 아티팩트 1건 — 패키징+서명 정보 결합.</summary>
public sealed record DeploymentArtifactEntry(string Platform, string ArchivePath, long Bytes, string Digest8, bool Signed);

/// <summary>최종 배포 청사진 — 버전/아티팩트/시크릿 규격/LTS 정책 한 번에.</summary>
public sealed record DeploymentBlueprint(string ProductVersion, DateTimeOffset ExportedAt, IReadOnlyList<DeploymentArtifactEntry> Artifacts, IReadOnlyList<SecretSpecEntry> Secrets, string LtsPolicy);

public sealed record ManifestExportResult(bool Ok, string? JsonPath, string? YamlPath, string Message);

/// <summary>
/// v10.3.3 프로덕션 매니페스트 내보내기 — <see cref="ProductionArtifactPackager"/> 산출물 +
/// <see cref="ReleaseIntegrityValidator"/> 서명 이력을 결합해 배포 청사진(<see cref="DeploymentBlueprint"/>)을
/// 구성하고 JSON(<see cref="System.Text.Json"/>)과 YAML(외부 라이브러리 없이 이 레코드 구조 전용 최소 이미터)
/// 양쪽으로 <c>%LocalAppData%\PromptFlowStudio\Manifests</c> 에 저장한다. 환경변수/시크릿 규격은
/// <c>RELEASE_NOTES.md</c> 표를 코드 정본화한 <see cref="KnownSecrets"/> 로 단일 소스화(문서-코드 불일치 방지).
/// 순수 BCL(파일 I/O + JSON) · 하드웨어 비의존 · 데스크톱/모바일 공용.
/// </summary>
public sealed class ProductionManifestExporter
{
    public static string ExportRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Manifests");

    /// <summary>LTS 지원 정책 요약(RELEASE_NOTES.md "LTS 지원 정책" 절과 동일 정본).</summary>
    public const string LtsPolicySummary = "호환성 창 9.5~10.99(major.minor, Desktop/Mobile/Shared 동일) · 원자적 롤백 보장(사전 스냅샷) · 전 감사/정책 판정 SHA-256 해시체인 봉인";

    /// <summary>환경변수/시크릿 규격 정본 — RELEASE_NOTES.md 표를 코드로 단일화(실제 코드 참조/CI 시크릿만 등재 · 지어내지 않음).</summary>
    public static readonly IReadOnlyList<SecretSpecEntry> KnownSecrets = new[]
    {
        new SecretSpecEntry("PATH", "Runtime", "외부 CLI(ffmpeg 등) 실행 파일 탐색 — Services/Deploy, Services/Engines"),
        new SecretSpecEntry("GOOGLE_SA_JSON", "Runtime/CI", "Google Play 서비스 계정 자격증명(배포 검수) — tools/PromptFlowCiGate"),
        new SecretSpecEntry("ANDROID_KEYSTORE_B64", "CI/CD", "Android AAB 서명 키스토어(Base64) — store-deploy.yml"),
        new SecretSpecEntry("ANDROID_KEY_ALIAS", "CI/CD", "Android 서명 키 별칭 — store-deploy.yml"),
        new SecretSpecEntry("ANDROID_STORE_PASS", "CI/CD", "Android 키스토어 암호 — store-deploy.yml"),
        new SecretSpecEntry("ANDROID_KEY_PASS", "CI/CD", "Android 서명 키 암호 — store-deploy.yml"),
        new SecretSpecEntry("IOS_CERT_P12_B64", "CI/CD", "iOS 배포 인증서(P12·Base64) — store-deploy.yml"),
        new SecretSpecEntry("IOS_PROFILE_B64", "CI/CD", "iOS 프로비저닝 프로파일(Base64) — store-deploy.yml"),
        new SecretSpecEntry("IOS_SIGNING_IDENTITY", "CI/CD", "iOS 코드사인 아이덴티티 — store-deploy.yml"),
        new SecretSpecEntry("ASC_KEY_ID", "CI/CD", "App Store Connect API Key ID — store-deploy.yml"),
        new SecretSpecEntry("ASC_ISSUER_ID", "CI/CD", "App Store Connect Issuer ID — store-deploy.yml"),
        new SecretSpecEntry("ASC_PRIVATE_KEY", "CI/CD", "App Store Connect API 개인키 — store-deploy.yml"),
        new SecretSpecEntry("CODE_SIGN_PFX_BASE64", "CI/CD", "Windows EXE 코드사인 PFX(Base64 · release.yml 내 현재 주석 처리/미적용)"),
        new SecretSpecEntry("CODE_SIGN_PFX_PASSWORD", "CI/CD", "Windows EXE 코드사인 PFX 암호(현재 미적용)"),
    };



    private long _exports, _saveFailures;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public DeploymentBlueprint? LastBlueprint { get; private set; }
    public string? LastJsonPath { get; private set; }
    public string? LastYamlPath { get; private set; }
    public long Exports => Interlocked.Read(ref _exports);
    public long SaveFailures => Interlocked.Read(ref _saveFailures);

    /// <summary>패키징 산출물 + (선택)서명 이력을 결합해 배포 청사진을 구성.</summary>
    public DeploymentBlueprint BuildBlueprint(string productVersion, IEnumerable<PackagedArtifact> artifacts, IReadOnlyList<ArtifactValidationRecord>? signatures = null)
    {
        // ⚠ History 는 아티팩트당 여러 건(서명→검증→재검증…)이 누적되는 감사 로그라 ArtifactId 가 중복될 수 있음 —
        //   단순 ToDictionary 는 중복 키에서 예외(ArgumentException) 발생 → 최신 1건(History 가 최신순이므로 GroupBy.First())만 채택.
        var sigById = signatures?.GroupBy(s => s.ArtifactId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, ArtifactValidationRecord>(StringComparer.Ordinal);
        var entries = artifacts.Select(a =>
        {
            sigById.TryGetValue(a.DisplayName, out var sig);
            var signed = sig is { State: ArtifactValidationState.Signed or ArtifactValidationState.Verified };
            return new DeploymentArtifactEntry(a.DisplayName, a.ArchivePath, a.Bytes, sig?.Digest8 ?? "--------", signed);
        }).ToList();
        return new DeploymentBlueprint(productVersion, DateTimeOffset.UtcNow, entries, KnownSecrets, LtsPolicySummary);
    }

    public static string ToJson(DeploymentBlueprint bp) => JsonSerializer.Serialize(bp, PromptFlowJsonContext.Default.DeploymentBlueprint);

    /// <summary>이 레코드 구조 전용 최소 YAML 이미터(외부 라이브러리 0) — 스칼라 문자열은 안전 인용.</summary>
    public static string ToYaml(DeploymentBlueprint bp)
    {
        var sb = new StringBuilder();
        sb.Append("product_version: ").AppendLine(Q(bp.ProductVersion));
        sb.Append("exported_at: ").AppendLine(Q(bp.ExportedAt.ToString("O", CultureInfo.InvariantCulture)));
        sb.Append("lts_policy: ").AppendLine(Q(bp.LtsPolicy));
        sb.AppendLine("artifacts:");
        if (bp.Artifacts.Count == 0) sb.AppendLine("  []");
        foreach (var a in bp.Artifacts)
        {
            sb.Append("  - platform: ").AppendLine(Q(a.Platform));
            sb.Append("    archive_path: ").AppendLine(Q(a.ArchivePath));
            sb.Append("    bytes: ").AppendLine(a.Bytes.ToString(CultureInfo.InvariantCulture));
            sb.Append("    digest8: ").AppendLine(Q(a.Digest8));
            sb.Append("    signed: ").AppendLine(a.Signed ? "true" : "false");
        }
        sb.AppendLine("secrets:");
        foreach (var s in bp.Secrets)
        {
            sb.Append("  - name: ").AppendLine(Q(s.Name));
            sb.Append("    scope: ").AppendLine(Q(s.Scope));
            sb.Append("    purpose: ").AppendLine(Q(s.Purpose));
        }
        return sb.ToString();
    }

    // YAML 이중따옴표 스칼라 인용(백슬래시/따옴표 이스케이프) — 구조 문자(:,#,-) 오파싱 방지.
    private static string Q(string? s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>배포 청사진을 JSON+YAML 동시 저장.</summary>
    public ManifestExportResult Export(DeploymentBlueprint blueprint)
    {
        try
        {
            Directory.CreateDirectory(ExportRoot);
            var stamp = blueprint.ExportedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var jsonPath = Path.Combine(ExportRoot, $"deployment-blueprint-{stamp}.json");
            var yamlPath = Path.Combine(ExportRoot, $"deployment-blueprint-{stamp}.yaml");
            File.WriteAllText(jsonPath, ToJson(blueprint), Encoding.UTF8);
            File.WriteAllText(yamlPath, ToYaml(blueprint), Encoding.UTF8);

            Interlocked.Increment(ref _exports);
            LastBlueprint = blueprint; LastJsonPath = jsonPath; LastYamlPath = yamlPath;
            LastAction = $"청사진 내보내기 완료 · {blueprint.Artifacts.Count}아티팩트 · {Path.GetFileName(jsonPath)}";
            OnChange?.Invoke();
            return new ManifestExportResult(true, jsonPath, yamlPath, "저장 완료");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _saveFailures);
            LastAction = "내보내기 실패: " + ex.Message;
            OnChange?.Invoke();
            return new ManifestExportResult(false, null, null, ex.Message);
        }
    }

    public void ResetAll()
    {
        LastBlueprint = null; LastJsonPath = null; LastYamlPath = null; _exports = 0; _saveFailures = 0;
        LastAction = "매니페스트 내보내기 초기화";
        OnChange?.Invoke();
    }
}
