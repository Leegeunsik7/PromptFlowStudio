using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Deploy;

/// <summary>배포 타겟 플랫폼 — 산출물 확장자 규칙의 근거.</summary>
public enum DeployPlatform { Windows, Android, iOS }

/// <summary>산출물 정량 검증 결과 — 경로·존재·바이트·확장자 적합·파일락·종합 통과·사유.</summary>
public sealed record ArtifactCheck(string Path, bool Exists, long Bytes, bool ExtensionOk, bool Locked, bool Ok, string Message);

/// <summary>
/// [PromptFlowStudio] v4.4.4 Task2 · 산출물 무결성 &amp; I/O 권한 검증기 — 순수 BCL 파일시스템 API(외부 NuGet 0).
///   ▸ <b>정량 검증</b>: 배포 산출물이 <b>존재</b>하고 <b>0바이트가 아니며</b>(<see cref="MinBytes"/> 이상) 타겟 플랫폼 <b>확장자</b>(.exe/.msi · .apk/.aab · .ipa)와
///     일치하는지 배포 완료 <b>전</b>에 검사(<see cref="Verify"/> / <see cref="VerifyDirectoryAsync"/>).
///   ▸ <b>파일 락 방어</b>: 기존 산출물을 덮어쓰지 못하는 <see cref="IOException"/>(다른 프로세스 사용 중)을 예산 안에서 프로브해 포착하고,
///     크래시 대신 Graceful 안내 메시지로 귀결(<see cref="EnsureOverwritableAsync"/>).
/// </summary>
public sealed class ArtifactIntegrityGuard
{
    /// <summary>이 크기 미만이면 0바이트/스텁 손상으로 간주(기본 1KB).</summary>
    public long MinBytes { get; init; } = 1024;

    /// <summary>플랫폼별 허용 산출물 확장자.</summary>
    public static IReadOnlyList<string> ExtensionsFor(DeployPlatform p) => p switch
    {
        DeployPlatform.Windows => new[] { ".exe", ".msi" },
        DeployPlatform.Android => new[] { ".apk", ".aab" },
        DeployPlatform.iOS     => new[] { ".ipa" },
        _ => Array.Empty<string>(),
    };

    /// <summary>경로 확장자가 타겟 플랫폼과 일치하는지(순수).</summary>
    public static bool ExtensionMatches(string? path, DeployPlatform p)
    {
        var ext = Path.GetExtension(path ?? "");
        return ExtensionsFor(p).Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    // ══════════════════════ 정량 검증(순수 · 테스트 대상) ══════════════════════

    /// <summary>산출물 1개를 정량 검증한다 — 존재 + 0바이트 아님(<see cref="MinBytes"/> 이상) + 확장자 일치.</summary>
    public ArtifactCheck Verify(string? path, DeployPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(path)) return new ArtifactCheck("", false, 0, false, false, false, "경로가 비어 있습니다.");

        long bytes = 0; var exists = false;
        try { var fi = new FileInfo(path); exists = fi.Exists; bytes = exists ? fi.Length : 0; }
        catch { /* 접근 실패 → 미존재 취급 */ }

        var extOk = ExtensionMatches(path, platform);
        var sized = bytes >= MinBytes;
        var ok = exists && sized && extOk;
        var msg =
            !exists ? "산출물이 존재하지 않습니다(누락)."
            : bytes == 0 ? "산출물이 0바이트입니다(생성 실패)."
            : !sized ? $"용량이 비정상적으로 작습니다({Human(bytes)} < {Human(MinBytes)} · 손상 의심)."
            : !extOk ? $"확장자가 타겟({platform})과 불일치합니다: {Path.GetExtension(path)} (허용 {string.Join("/", ExtensionsFor(platform))})"
            : $"검증 통과 · {Human(bytes)}";
        return new ArtifactCheck(path, exists, bytes, extOk, false, ok, msg);
    }

    /// <summary>배포 출력 폴더에서 타겟 확장자 산출물을 찾아 정량 검증 + 파일락 프로브(비차단).</summary>
    public async Task<IReadOnlyList<ArtifactCheck>> VerifyDirectoryAsync(string outputDir, DeployPlatform platform, CancellationToken ct = default)
    {
        var results = new List<ArtifactCheck>();
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
        {
            results.Add(new ArtifactCheck(outputDir ?? "", false, 0, false, false, false, "배포 출력 폴더가 없습니다: " + outputDir));
            return results;
        }

        var exts = ExtensionsFor(platform);
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(outputDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => exts.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex)
        {
            results.Add(new ArtifactCheck(outputDir, true, 0, false, false, false, "폴더 열거 실패: " + ex.Message));
            return results;
        }

        if (files.Count == 0)
        {
            results.Add(new ArtifactCheck(outputDir, true, 0, false, false, false,
                $"타겟({platform}) 산출물({string.Join("/", exts)})을 찾지 못했습니다."));
            return results;
        }

        foreach (var f in files)
        {
            var chk = Verify(f, platform);
            // 파일락 프로브 — 잠겨 있으면 후속 덮어쓰기 실패 가능성을 경고로 부착(검증 자체는 비차단).
            var (overwritable, lockMsg) = await EnsureOverwritableAsync(f, TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            chk = chk with { Locked = !overwritable, Message = overwritable ? chk.Message : chk.Message + " · ⚠ " + lockMsg };
            results.Add(chk);
        }
        return results;
    }

    // ══════════════════════ 파일 락 방어(IOException Graceful) ══════════════════════

    /// <summary>
    /// 기존 산출물을 덮어쓸 수 있는지 예산 안에서 프로브한다. 배타적 쓰기 오픈이 되면 가능, <see cref="IOException"/>(사용 중)이면
    /// 재시도 후 Graceful 안내로 귀결(무한 대기·크래시 없음). 파일이 없으면 신규 생성 가능으로 간주.
    /// </summary>
    public async Task<(bool Ok, string Message)> EnsureOverwritableAsync(string? path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, "경로가 비어 있습니다.");
        if (!File.Exists(path)) return (true, "신규 생성 가능(기존 파일 없음).");

        var budget = timeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(3);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        while (true)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);   // 배타 오픈 성공 = 덮어쓰기 가능
                return (true, "덮어쓰기 가능.");
            }
            catch (IOException)
            {
                if (cts.IsCancellationRequested)
                    return (false, ct.IsCancellationRequested ? "취소됨" : "다른 프로세스가 파일을 사용 중입니다 — 실행 중인 앱/탐색기 미리보기를 닫고 다시 시도하세요.");
                // 잠금 해제 대기 → 재시도.
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "쓰기 권한이 없습니다 — 폴더 권한 또는 관리자 권한을 확인하세요.");
            }
            catch (Exception ex)
            {
                return (false, "덮어쓰기 가능 여부 확인 실패: " + ex.Message);
            }

            try { await Task.Delay(120, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return (false, ct.IsCancellationRequested ? "취소됨" : "다른 프로세스가 파일을 사용 중입니다 — 닫고 다시 시도하세요.");
            }
        }
    }

    private static string Human(long bytes)
    {
        var ci = CultureInfo.InvariantCulture;
        return bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024.0)).ToString("0.#", ci) + " MB"
             : bytes >= 1024 ? (bytes / 1024.0).ToString("0.#", ci) + " KB"
             : bytes + " B";
    }
}
