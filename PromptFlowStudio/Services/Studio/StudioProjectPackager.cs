using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Studio;

/// <summary>패키징 엔트리 — 원본 경로·ZIP 내 경로·분류(명세서 카테고리).</summary>
public sealed record PackageEntry(string SourcePath, string EntryPath, string Category);

/// <summary>패키징 결과 — 성공 여부·ZIP 경로·엔트리 수·총 바이트·메시지.</summary>
public sealed record PackageResult(bool Ok, string? ZipPath, int EntryCount, long TotalBytes, string Message);

/// <summary>
/// [PromptFlowStudio] 방송 마스터 프로젝트 ZIP 백업 패키저(v2.7.0) — 순수 BCL <see cref="ZipArchive"/>(외부 NuGet 0).
///   ▸ 프로젝트 결과물(Master MP4 · Platform MP4s · Global Dubbing · Lighting · PDF Spec · CAD STL · project.json)을
///     구조화된 폴더(master/·platforms/·global/·lighting/·spec/·cad/·project.json)로 <b>무손실 인라인 압축</b> 패키징한다.
///   ▸ 이미 압축된 미디어(mp4/pdf/stl)는 <c>Store</c>, 텍스트(json/html/md/srt)는 <c>Optimal</c> 로 지능 압축(무손실 동일).
///   ▸ <see cref="DeliveryManifestService"/>로 <b>납품 명세서(HTML+MD·SHA256 체크섬)</b>를 생성해 동봉. 진행률 방송 · 실패는 Graceful.
/// </summary>
public sealed class StudioProjectPackager
{
    private readonly DeliveryManifestService _manifest;
    public StudioProjectPackager(DeliveryManifestService manifest) { _manifest = manifest; }

    /// <summary>기본 ZIP 경로(프로젝트 디렉터리 내).</summary>
    public static string DefaultZipPath(string projectDir, StoryboardProject project)
        => Path.Combine(projectDir, $"PromptFlow_{project.Id}_master.zip");

    /// <summary>
    /// 프로젝트 디렉터리의 모든 결과물 + project.json + 명세서를 하나의 마스터 ZIP 으로 패키징한다.
    /// </summary>
    public async Task<PackageResult> PackageAsync(StoryboardProject project, string projectDir, string? outZipPath = null,
        IProgress<(string Stage, double Pct)>? progress = null, CancellationToken ct = default)
    {
        if (project is null) return new(false, null, 0, 0, "프로젝트가 없습니다.");
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return new(false, null, 0, 0, "프로젝트 디렉터리를 찾을 수 없습니다.");

        var zipPath = string.IsNullOrWhiteSpace(outZipPath) ? DefaultZipPath(projectDir, project) : outZipPath!;
        string? projJson = null;
        try
        {
            var entries = PlanEntries(project, projectDir);

            // project.json 을 임시 파일로 기록 후 엔트리에 포함(명세서 체크섬 대상).
            projJson = Path.Combine(Path.GetTempPath(), $"pfs_proj_{Guid.NewGuid():N}.json");
            File.WriteAllText(projJson, JsonSerializer.Serialize(project, PromptFlowJsonContext.Default.StoryboardProject), new UTF8Encoding(false));
            entries.Add(new PackageEntry(projJson, "project.json", "project"));

            if (entries.Count == 0) return new(false, null, 0, 0, "패키징할 결과물이 없습니다(먼저 병합/생성).");

            // 명세서(HTML/MD) 생성.
            var model = _manifest.BuildModel(project, entries.Select(e => (e.Category, Path.GetFileName(e.EntryPath), e.EntryPath, e.SourcePath)));
            var html = DeliveryManifestService.RenderHtml(model);
            var md = DeliveryManifestService.RenderMarkdown(model);

            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(zipPath)) { try { File.Delete(zipPath); } catch { } }

            long total = 0; var done = 0; var count = entries.Count + 2;   // +manifest html/md
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(e.SourcePath)) { done++; continue; }
                    progress?.Report(($"압축 중… {Path.GetFileName(e.EntryPath)}", done / (double)count));
                    var entry = zip.CreateEntry(e.EntryPath, LevelFor(e.EntryPath));
                    using (var es = entry.Open())
                    using (var src = new FileStream(e.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await src.CopyToAsync(es, 81920, ct).ConfigureAwait(false);
                        total += src.Length;
                    }
                    done++;
                }

                WriteText(zip, "manifest/delivery_manifest.html", html);
                WriteText(zip, "manifest/delivery_manifest.md", md);
            }

            progress?.Report(("완료", 1.0));
            var real = entries.Count(e => File.Exists(e.SourcePath)) + 2;
            return new(true, zipPath, real, total, $"마스터 ZIP 패키징 완료 · {real}개 엔트리 · {Human(total)}");
        }
        catch (OperationCanceledException) { try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { } return new(false, null, 0, 0, "취소됨"); }
        catch (Exception ex) { Program.Log("StudioPackager.Package", ex); return new(false, null, 0, 0, "패키징 실패: " + ex.Message); }
        finally { try { if (projJson is not null && File.Exists(projJson)) File.Delete(projJson); } catch { } }
    }

    // ══════════════════════ 엔트리 계획(순수 · 테스트 대상) ══════════════════════

    /// <summary>프로젝트 디렉터리를 스캔해 ZIP 엔트리 목록을 만든다(존재하는 결과물만 · 구조화 경로).</summary>
    public static List<PackageEntry> PlanEntries(StoryboardProject project, string projectDir)
    {
        var list = new List<PackageEntry>();

        // 마스터 영상.
        if (!string.IsNullOrWhiteSpace(project.FinalVideoPath) && File.Exists(project.FinalVideoPath))
            list.Add(new PackageEntry(project.FinalVideoPath, "master/" + Path.GetFileName(project.FinalVideoPath), "Master"));

        // 하위 산출물 트리(플랫폼/글로벌/조명).
        AddTree(list, projectDir, "platforms", "platforms", "Platform");
        AddTree(list, projectDir, "global", "global", "Dubbing");
        AddTree(list, projectDir, "lighting", "lighting", "Lighting");

        // PDF 스펙 시트.
        var pdf = Path.Combine(projectDir, "spec_sheet.pdf");
        if (File.Exists(pdf)) list.Add(new PackageEntry(pdf, "spec/spec_sheet.pdf", "Spec"));

        // 자막(있으면).
        foreach (var srt in SafeEnum(projectDir, "*.srt"))
            list.Add(new PackageEntry(srt, "subtitles/" + Path.GetFileName(srt), "Subtitle"));

        // CAD STL(프로젝트 디렉터리 밖일 수 있음).
        if (!string.IsNullOrWhiteSpace(project.CadModelPath) && File.Exists(project.CadModelPath))
            list.Add(new PackageEntry(project.CadModelPath, "cad/" + Path.GetFileName(project.CadModelPath), "CAD"));

        // 중복 엔트리 경로 제거(같은 파일 이중 포함 방지).
        return list.GroupBy(e => e.EntryPath).Select(g => g.First()).ToList();
    }

    private static void AddTree(List<PackageEntry> list, string projectDir, string sub, string zipRoot, string category)
    {
        var root = Path.Combine(projectDir, sub);
        if (!Directory.Exists(root)) return;
        foreach (var f in SafeEnumRec(root))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            list.Add(new PackageEntry(f, $"{zipRoot}/{rel}", category));
        }
    }

    private static IEnumerable<string> SafeEnum(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly) : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }
    private static IEnumerable<string> SafeEnumRec(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
        catch { return Enumerable.Empty<string>(); }
    }

    // ── 헬퍼 ──
    private static CompressionLevel LevelFor(string entryPath)
    {
        var ext = Path.GetExtension(entryPath).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".pdf" or ".stl" or ".png" or ".jpg" or ".jpeg" or ".wav" or ".mp3" or ".aac" or ".m4a"
            ? CompressionLevel.NoCompression    // 이미 압축된 미디어 → Store(무손실·CPU 절감)
            : CompressionLevel.Optimal;          // 텍스트(json/html/md/srt) → 지능 압축
    }

    private static void WriteText(ZipArchive zip, string entryPath, string content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string Human(long b) => b >= 1_000_000 ? $"{b / 1_000_000.0:0.0} MB" : b >= 1000 ? $"{b / 1000.0:0.0} KB" : $"{b} B";
}
