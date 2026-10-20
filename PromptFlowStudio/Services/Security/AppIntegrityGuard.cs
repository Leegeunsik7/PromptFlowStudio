using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Security;

public enum IntegrityVerdict : byte { Verified, Unsealed, KeyUnavailable, Tampered }

/// <summary>자체 무결성 검증 결과(값 형식) — <see cref="FirstFailure"/> 는 첫 불일치 파일명 또는 사유.</summary>
public readonly record struct IntegrityResult(IntegrityVerdict Verdict, int Checked, int Failed, string? FirstFailure)
{
    /// <summary>변조 감지 시에만 종료 대상(미봉인 개발 빌드·공개키 손상은 경고만).</summary>
    public bool ShouldShutdown => Verdict == IntegrityVerdict.Tampered;
}

/// <summary>
/// [PromptFlowStudio] v16.0.0 Titan RSA-2048 비대칭 무결성 쉴드(v15.17.0 HMAC 대칭키 방식 폐기).
///   ▸ CI/CD 가 <see cref="SignManifest"/>(개인키)로 핵심 어셈블리별 SHA-256 + 매니페스트 RSA 서명(PKCS#1 v1.5 · SHA-256)을 <see cref="ManifestFileName"/> 에 기록.
///   ▸ 클라이언트는 내장 <see cref="PublicKey"/> 로 <c>VerifyData</c> 만 수행 — 개인키가 없으므로 매니페스트 재생성(위조)이 불가능.
///   ▸ canonical 페이로드 = <c>PFI1 {count}</c> 헤더(집계 필드) + 정렬된 <c>{hex} {name}</c> 줄 — 항목 삭제·추가·순서 변조도 서명 불일치.
///   ▸ 파일 해시 비교는 <see cref="CryptographicOperations.FixedTimeEquals"/>. 매니페스트 부재(개발 빌드)는 종료하지 않는다.
/// </summary>
public static class AppIntegrityGuard
{
    public const string ManifestFileName = "pfi1.manifest";

    /// <summary>CI/CD 서명 모드 CLI 스위치(<c>PromptFlowStudio.exe --pfi1-sign {publishDir}</c>).</summary>
    public const string SignCommand = "--pfi1-sign";

    /// <summary>릴리스 검인 CLI 스위치(<c>PromptFlowStudio.exe --pfi1-verify {dir}</c>) — 내장 공개키로만 검증(개인키 불필요).</summary>
    public const string VerifyCommand = "--pfi1-verify";

    /// <summary>서명 모드 개인키 환경변수(base64 PKCS#8 또는 PEM) — CI/CD 프로세스에만 주입, 클라이언트에는 존재하지 않는다.</summary>
    public const string SigningKeyEnvVar = "PROMPTFLOW_SIGNING_KEY";

    /// <summary>RSA-2048 공개키(SubjectPublicKeyInfo · base64). 개인키는 저장소 밖 CI/CD 시크릿으로만 보관.</summary>
    public const string PublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAy1spZjrA9AiLro8smqqxIcDwaig5uabxLbTjTRIJ9RrOSxkJ3fqmv5PgJrPJryPl+BlIl7r0CigF7lgO5hu8agh9T5Z3Rsn6Jazfr0B8bAI7mu/Zr+Q2n1tpPssCTqwf3eLATiHKj2r3LsuJfkfvI/tuEWDbEp79baPg/1t10/YO8EcWnMs8S17OvBFx6EaIoHlBzL94BwrhWDUl/wUm4Sv2qnqbioukMfmSWA+spvnWsqmNCrd83ouAnxZ1/VpGvrUZdkD1l1vqs6NoHhperLOd3yXtYKzNZ/FZ5Trin4s6bqqBMvo2eiMM/Xv0bcb7O9mQ9Iu+vUfSlYSlRv5bxQIDAQAB";

    public const int MinKeyBits = 2048;
    private const string Header = "PFI1";
    private const string SigPrefix = "SIG ";
    private const int HashHexLength = SHA256.HashSizeInBytes * 2;

    /// <summary>기본 서명 대상(앱 본체 + 공용 RCL).</summary>
    public static readonly string[] CoreFiles = { "PromptFlowStudio.dll", "PromptFlowStudio.Shared.dll" };

    /// <summary>번들 동봉 시 함께 서명되는 선택 파일(존재할 때만).</summary>
    public static readonly string[] OptionalFiles = { "ffmpeg.exe" };

    /// <summary>설치 폴더(<see cref="AppContext.BaseDirectory"/>)를 내장 공개키로 검증. 예외 없음.</summary>
    public static IntegrityResult VerifyInstalled() => Verify(AppContext.BaseDirectory, PublicKey);

    /// <summary>
    /// <paramref name="baseDir"/> 의 매니페스트를 <paramref name="publicKeyBase64"/>(SPKI)로 검증.
    /// 매니페스트 없음 → Unsealed, 공개키 손상 → KeyUnavailable, 서명/형식/파일 해시 불일치·파일 누락 → Tampered.
    /// </summary>
    public static IntegrityResult Verify(string baseDir, string publicKeyBase64)
    {
        var manifestPath = Path.Combine(baseDir, ManifestFileName);
        string[] lines;
        try
        {
            if (!File.Exists(manifestPath)) return new(IntegrityVerdict.Unsealed, 0, 0, null);
            lines = File.ReadAllLines(manifestPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(IntegrityVerdict.Tampered, 0, 1, ManifestFileName);
        }

        // 형식: [0]=PFI1 {count} · [1..count]={hex} {name} · [count+1]=SIG {base64}
        if (lines.Length < 2 || !TryParseHeader(lines[0], out var count) || lines.Length != count + 2
            || !lines[count + 1].StartsWith(SigPrefix, StringComparison.Ordinal))
            return new(IntegrityVerdict.Tampered, 0, 1, ManifestFileName);

        // ① 매니페스트 RSA 서명 — canonical 본문(헤더+항목) 전체
        using (var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false })
        {
            try { rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _); }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return new(IntegrityVerdict.KeyUnavailable, 0, 0, "PublicKey");
            }
            if (rsa.KeySize < MinKeyBits) return new(IntegrityVerdict.KeyUnavailable, 0, 0, "PublicKey");

            bool sealedOk;
            try
            {
                var sig = Convert.FromBase64String(lines[count + 1].Substring(SigPrefix.Length));
                sealedOk = sig.Length == rsa.KeySize / 8
                    && rsa.VerifyData(BuildCanonical(lines, count), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException) { sealedOk = false; }
            if (!sealedOk) return new(IntegrityVerdict.Tampered, 0, 1, ManifestFileName);
        }

        // ② 파일별 SHA-256
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        int failed = 0;
        string? first = null;
        for (int i = 1; i <= count; i++)
        {
            var line = lines[i];
            if (line.Length <= HashHexLength + 1 || line[HashHexLength] != ' ' || !TryParseHex(line.AsSpan(0, HashHexLength), expected))
            {
                failed++; first ??= ManifestFileName;
                continue;
            }
            var name = line.Substring(HashHexLength + 1);
            if (!IsSafeRelativeName(name) || !TryComputeFileHash(Path.Combine(baseDir, name), actual)
                || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                failed++; first ??= name;
            }
        }
        return new(failed == 0 ? IntegrityVerdict.Verified : IntegrityVerdict.Tampered, count, failed, first);
    }

    /// <summary>
    /// [CI/CD 전용] <paramref name="privateKey"/>(base64 PKCS#8 또는 PEM, RSA ≥2048)로 <paramref name="baseDir"/>(기본 설치 폴더)의
    /// <paramref name="files"/>(기본 <see cref="CoreFiles"/>) 해시를 서명해 매니페스트를 원자적으로 기록하고,
    /// 개인키에서 유도한 공개키로 즉시 자기 검증한 결과를 반환. 누락 파일은 <see cref="FileNotFoundException"/>.
    /// </summary>
    public static IntegrityResult SignManifest(string privateKey, string? baseDir = null, IReadOnlyList<string>? files = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        files ??= CoreFiles;
        if (string.IsNullOrWhiteSpace(privateKey)) throw new ArgumentException("개인키가 비어 있습니다.", nameof(privateKey));

        var names = new List<string>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            if (!IsSafeRelativeName(files[i])) throw new ArgumentException("상대 파일명만 허용됩니다: " + files[i], nameof(files));
            names.Add(files[i]);
        }
        names.Sort(StringComparer.Ordinal);

        using var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false };
        ImportPrivateKey(rsa, privateKey);
        if (rsa.KeySize < MinKeyBits) throw new ArgumentException("RSA 키는 2048비트 이상이어야 합니다.", nameof(privateKey));

        var lines = new string[names.Count + 2];
        lines[0] = Header + " " + names.Count.ToString(CultureInfo.InvariantCulture);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        for (int i = 0; i < names.Count; i++)
        {
            var path = Path.Combine(baseDir, names[i]);
            if (!TryComputeFileHash(path, hash)) throw new FileNotFoundException("서명 대상 파일을 읽을 수 없습니다.", path);
            lines[i + 1] = Convert.ToHexString(hash) + " " + names[i];
        }
        var sig = rsa.SignData(BuildCanonical(lines, names.Count), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        lines[names.Count + 1] = SigPrefix + Convert.ToBase64String(sig);

        var target = Path.Combine(baseDir, ManifestFileName);
        var tmp = target + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, target, overwrite: true);

        return Verify(baseDir, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    /// <see cref="SignCommand"/> 진입점: 환경변수 <see cref="SigningKeyEnvVar"/> 개인키로 <paramref name="publishDir"/> 를 서명하고
    /// 내장 <see cref="PublicKey"/> 로 재검증. 종료 코드 0=성공 · 2=키 없음 · 4=내장 공개키 불일치 · 5=서명 실패.
    /// </summary>
    public static int RunSignCommand(string publishDir, out string message)
    {
        var key = Environment.GetEnvironmentVariable(SigningKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key)) { message = SigningKeyEnvVar + " 미설정"; return 2; }
        try
        {
            var self = SignManifest(key, publishDir, ResolveSignTargets(publishDir));
            if (self.Verdict != IntegrityVerdict.Verified) { message = "자기 검증 실패: " + self.FirstFailure; return 5; }
            var embedded = Verify(publishDir, PublicKey);
            if (embedded.Verdict != IntegrityVerdict.Verified) { message = "개인키가 내장 공개키와 쌍이 아닙니다(" + embedded.Verdict + ")"; return 4; }
            message = "서명 완료: " + embedded.Checked.ToString(CultureInfo.InvariantCulture) + "개 파일 → " + ManifestFileName;
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            message = "서명 실패: " + ex.Message;
            return 5;
        }
    }

    /// <summary>
    /// <see cref="VerifyCommand"/> 진입점(릴리스 검인 · scripts/verify_release.ps1): <paramref name="dir"/> 의 매니페스트를 <paramref name="publicKeyBase64"/>
    /// (기본 내장 <see cref="PublicKey"/>)로 검증. 종료 코드 0=Verified · 3=Tampered · 6=Unsealed · 7=KeyUnavailable. 예외 없음.
    /// </summary>
    public static int RunVerifyCommand(string dir, out string message, string? publicKeyBase64 = null)
    {
        var r = Verify(dir, publicKeyBase64 ?? PublicKey);
        message = r.Verdict.ToString() + " " + r.Checked.ToString(CultureInfo.InvariantCulture) + "/" + r.Failed.ToString(CultureInfo.InvariantCulture)
            + (r.FirstFailure is null ? "" : " " + r.FirstFailure);
        return r.Verdict switch
        {
            IntegrityVerdict.Verified => 0,
            IntegrityVerdict.Tampered => 3,
            IntegrityVerdict.Unsealed => 6,
            _ => 7,
        };
    }

    /// <summary>서명 대상 =<see cref="CoreFiles"/> + <paramref name="publishDir"/> 에 실제 동봉된 <see cref="OptionalFiles"/>(bundle_ffmpeg.ps1 산출 ffmpeg.exe 등).</summary>
    public static List<string> ResolveSignTargets(string publishDir)
    {
        var files = new List<string>(CoreFiles.Length + OptionalFiles.Length);
        files.AddRange(CoreFiles);
        for (int i = 0; i < OptionalFiles.Length; i++)
            if (File.Exists(Path.Combine(publishDir, OptionalFiles[i]))) files.Add(OptionalFiles[i]);
        return files;
    }

    /// <summary>파일 스트리밍 SHA-256(전체 로드 없음). 읽기 실패 시 false.</summary>
    public static bool TryComputeFileHash(string path, Span<byte> destination)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            return SHA256.HashData(fs, destination) == SHA256.HashSizeInBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void ImportPrivateKey(RSA rsa, string privateKey)
    {
        var text = privateKey.Trim();
        if (text.StartsWith("-----BEGIN", StringComparison.Ordinal)) { rsa.ImportFromPem(text); return; }
        var der = Convert.FromBase64String(text);
        try { rsa.ImportPkcs8PrivateKey(der, out _); }
        finally { CryptographicOperations.ZeroMemory(der); }
    }

    private static byte[] BuildCanonical(string[] lines, int count)
    {
        var sb = new StringBuilder(64 + count * (HashHexLength + 32));
        for (int i = 0; i <= count; i++) sb.Append(lines[i]).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static bool TryParseHeader(string line, out int count)
    {
        count = 0;
        return line.StartsWith(Header + " ", StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(Header.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out count)
            && count is > 0 and <= 4096;
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> dst)
    {
        if (hex.Length != dst.Length * 2) return false;
        for (int i = 0; i < dst.Length; i++)
            if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out dst[i])) return false;
        return true;
    }

    // 설치 폴더 밖 경로(절대·상위 탐색) 차단 — 매니페스트가 임의 파일을 가리키지 못하게.
    private static bool IsSafeRelativeName(string name)
        => name.Length > 0 && !Path.IsPathRooted(name) && name.IndexOf("..", StringComparison.Ordinal) < 0;
}
