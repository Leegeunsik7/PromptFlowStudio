using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Deploy;

/// <summary>번들 체크섬 검증 결과(값 형식) — <see cref="FirstFailure"/> 는 첫 불일치 상대 경로 또는 사유.</summary>
public readonly record struct ChecksumVerifyResult(int Checked, int Failed, string? FirstFailure)
{
    public bool AllMatched => Failed == 0 && Checked > 0;
}

/// <summary>
/// [PromptFlowStudio] v18.3.0 배포 번들(win-x64) SHA-256 무결성 매니페스트 생성기.
///   ▸ 대상: <see cref="ExecutableName"/>(필수) · <see cref="SettingsName"/>(존재 시) · <see cref="WebRootName"/>/**/*(존재 시).
///   ▸ 형식: <c>{sha256 소문자 hex}  {상대경로('/' 구분)}</c> 줄 — <c>sha256sum -c</c> 호환, 상대경로 서수 정렬.
///   ▸ 해시는 <see cref="SHA256.HashData(Stream, Span{byte})"/> + stackalloc 다이제스트/hex 버퍼(파일당 문자열 할당 0).
///   ▸ 기록은 임시 파일 → <see cref="File.Move(string, string, bool)"/> 원자 교체. 검증 비교는 <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </summary>
public static class ReleaseBundleChecksumGenerator
{
    public const string ManifestFileName = "release-checksums.sha256";

    /// <summary>CLI 스위치(<c>PromptFlowStudio.exe --release-checksums {bundleDir}</c>).</summary>
    public const string Command = "--release-checksums";

    public const string ExecutableName = "PromptFlowStudio.exe";
    public const string SettingsName = "appsettings.json";
    public const string WebRootName = "wwwroot";

    private const int HashBytes = 32;   // SHA-256
    private const int HexLength = HashBytes * 2;
    private const string Separator = "  ";

    /// <summary>체크섬 대상 상대 경로('/' 구분 · 서수 정렬). 실행 파일 부재 시 <see cref="FileNotFoundException"/>.</summary>
    public static List<string> CollectTargets(string bundleDir)
    {
        var root = Path.GetFullPath(bundleDir);
        var exe = Path.Combine(root, ExecutableName);
        if (!File.Exists(exe)) throw new FileNotFoundException("배포 실행 파일 없음", exe);

        var list = new List<string> { ExecutableName };
        if (File.Exists(Path.Combine(root, SettingsName))) list.Add(SettingsName);
        var web = Path.Combine(root, WebRootName);
        if (Directory.Exists(web))
            foreach (var f in Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories))
                list.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary><paramref name="bundleDir"/> 루트에 <see cref="ManifestFileName"/> 을 원자적으로 기록하고 항목 수를 돌려준다.</summary>
    public static int Generate(string bundleDir)
    {
        var root = Path.GetFullPath(bundleDir);
        var targets = CollectTargets(root);
        var final = Path.Combine(root, ManifestFileName);
        var tmp = final + ".tmp";
        Span<byte> digest = stackalloc byte[HashBytes];
        Span<char> hex = stackalloc char[HexLength];
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    HashFile(Path.Combine(root, targets[i]), digest);
                    WriteHex(digest, hex);
                    w.Write(hex);
                    w.Write(Separator);
                    w.Write(targets[i]);
                    w.Write('\n');
                }
                w.Flush();
                fs.Flush(true);
            }
            File.Move(tmp, final, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* 정리 실패는 원 예외를 가리지 않는다 */ }
            throw;
        }
        return targets.Count;
    }

    /// <summary>매니페스트 대비 번들 전수 검증 — 해시 불일치·파일 누락·미등재 파일·경로 이탈 모두 실패로 집계. 예외 없음.</summary>
    public static ChecksumVerifyResult Verify(string bundleDir)
    {
        try
        {
            var root = Path.GetFullPath(bundleDir);
            var path = Path.Combine(root, ManifestFileName);
            if (!File.Exists(path)) return new(0, 1, ManifestFileName);

            var listed = new HashSet<string>(StringComparer.Ordinal);
            Span<byte> expected = stackalloc byte[HashBytes];
            Span<byte> actual = stackalloc byte[HashBytes];
            int checkedCount = 0, failed = 0;
            string? first = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                checkedCount++;
                if (line.Length <= HexLength + Separator.Length
                    || !line.AsSpan(HexLength, Separator.Length).SequenceEqual(Separator)
                    || !TryParseHex(line.AsSpan(0, HexLength), expected))
                {
                    failed++; first ??= "형식 위반: " + line; continue;
                }
                var rel = line.Substring(HexLength + Separator.Length);
                if (!IsContained(root, rel, out var full)) { failed++; first ??= "경로 이탈: " + rel; continue; }
                listed.Add(rel);
                if (!File.Exists(full)) { failed++; first ??= rel + " (누락)"; continue; }
                HashFile(full, actual);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual)) { failed++; first ??= rel; }
            }

            // 집계 필드: 현재 번들의 대상 집합이 매니페스트와 정확히 같아야 한다(사후 주입 파일 탐지).
            var current = CollectTargets(root);
            if (current.Count != listed.Count) { failed++; first ??= "항목 수 불일치 " + current.Count + "≠" + listed.Count; }
            else
                for (var i = 0; i < current.Count; i++)
                    if (!listed.Contains(current[i])) { failed++; first ??= current[i] + " (미등재)"; break; }
            return new(checkedCount, failed, first);
        }
        catch (Exception ex)
        {
            return new(0, 1, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>CLI 진입점 — 생성 후 즉시 재검증. 0 = 성공 · 1 = 실행 파일 부재 · 2 = 재검증 불일치 · 3 = I/O 예외.</summary>
    public static int RunCommand(string bundleDir, out string message)
    {
        try
        {
            var n = Generate(bundleDir);
            var v = Verify(bundleDir);
            if (!v.AllMatched) { message = "체크섬 재검증 실패: " + v.FirstFailure; return 2; }
            message = ManifestFileName + " 기록 완료: " + n + "개 항목 (재검증 일치 " + v.Checked + "/" + n + ")";
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            message = "배포 실행 파일 없음: " + ex.FileName;
            return 1;
        }
        catch (Exception ex)
        {
            message = "체크섬 기록 실패: " + ex.GetType().Name + " " + ex.Message;
            return 3;
        }
    }

    /// <summary>파일 SHA-256 을 <paramref name="digest"/>(32 B)에 기록(순차 스트림 · 다이제스트 할당 0).</summary>
    public static void HashFile(string path, Span<byte> digest)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        SHA256.HashData(fs, digest);
    }

    /// <summary>다이제스트 → 소문자 hex(무할당).</summary>
    public static void WriteHex(ReadOnlySpan<byte> bytes, Span<char> dest)
    {
        const string digits = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            dest[i * 2] = digits[bytes[i] >> 4];
            dest[i * 2 + 1] = digits[bytes[i] & 0xF];
        }
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, Span<byte> dest)
    {
        if (hex.Length != dest.Length * 2) return false;
        for (var i = 0; i < dest.Length; i++)
        {
            int hi = Nibble(hex[i * 2]), lo = Nibble(hex[i * 2 + 1]);
            if ((hi | lo) < 0) return false;
            dest[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int Nibble(char c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>매니페스트 상대 경로가 번들 루트 밖을 가리키지 않는지(절대 경로·<c>..</c> 이탈 차단).</summary>
    private static bool IsContained(string root, string rel, out string full)
    {
        full = "";
        if (Path.IsPathRooted(rel)) return false;
        full = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
