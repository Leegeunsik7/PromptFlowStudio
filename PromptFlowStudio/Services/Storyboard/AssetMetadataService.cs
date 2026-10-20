using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>완성 영상 자산의 정밀 메타데이터 — 원본 프로젝트에서 추출(프롬프트/폴리 타임코드/템포/프리셋).</summary>
public sealed record AssetMetadata(
    bool ProjectFound, string? ProjectPath, string Title, string? PresetKey,
    double Speed, string Resolution, int CutCount,
    IReadOnlyList<string> Prompts, IReadOnlyList<string> Foley, string Message);

/// <summary>
/// [PromptFlowStudio] 갤러리 자산 메타 인스펙터 — 순수 BCL(외부 NuGet 0).
///   ▸ 완성 영상 경로(<c>{MediaRoot}/{projId}/final.mp4</c>)의 부모 폴더명(=projId)으로 원본 프로젝트(<c>Projects/{projId}.json</c>)를 찾아
///     Wan 2.1 프롬프트·Gen-Foley 타임코드·템포·해상도·프리셋을 정밀 추출.
///   ▸ <see cref="FindProjectPath"/> 로 복원 대상 프로젝트 경로를 반환(원클릭 스토리보드 재송출용). 없으면 Graceful.
/// </summary>
public sealed class AssetMetadataService(StoryboardRepository repo)
{
    private const int MaxPromptLen = 90;

    /// <summary>영상 경로에서 원본 프로젝트 JSON 경로를 찾는다(없으면 null).</summary>
    public string? FindProjectPath(string? videoPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return null;
            var dir = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var projId = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(projId)) return null;
            var p = Path.Combine(repo.ProjectsDir, projId + ".json");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>영상 자산의 메타데이터를 조회한다(원본 프로젝트 로드). 실패는 Graceful.</summary>
    public AssetMetadata Inspect(string? videoPath)
    {
        var title = string.IsNullOrWhiteSpace(videoPath) ? "자산" : Path.GetFileNameWithoutExtension(videoPath);
        var path = FindProjectPath(videoPath);
        if (path is null)
            return new(false, null, title, null, 1.0, "", 0, Array.Empty<string>(), Array.Empty<string>(),
                "원본 프로젝트(.json)를 찾지 못했습니다. 스토리보드에서 💾 저장한 영상만 복원할 수 있어요.");

        var proj = repo.Load(path);
        if (proj is null)
            return new(false, path, title, null, 1.0, "", 0, Array.Empty<string>(), Array.Empty<string>(), "프로젝트를 열지 못했습니다(손상 가능).");

        var prompts = proj.Scenes.Select((s, i) => (i, p: (s.Prompt ?? "").Trim()))
            .Where(x => x.p.Length > 0)
            .Select(x => $"컷 {x.i + 1} · {Truncate(x.p)}")
            .Take(20).ToList();

        var foley = proj.Scenes.SelectMany((s, i) => (s.PresetFoley ?? new())
                .Select(f => $"컷 {i + 1} · {f.SfxType} @ {f.TimestampMs}ms (pan {f.Pan:0.##})"))
            .Take(30).ToList();

        return new(true, path, string.IsNullOrWhiteSpace(proj.Title) ? title : proj.Title,
            string.IsNullOrWhiteSpace(proj.PresetKey) ? null : proj.PresetKey,
            proj.PlaybackSpeed, proj.ExportResolution, proj.Scenes.Count, prompts, foley, "OK");
    }

    private static string Truncate(string s) => s.Length <= MaxPromptLen ? s : s[..MaxPromptLen] + "…";
}

/// <summary>
/// [PromptFlowStudio] 프로젝트 핸드오프 — 갤러리 → 스토리보드 원클릭 복원용 1회성 전달(싱글턴).
///   ▸ PromptDraftService 패턴: 설정 후 스토리보드가 최초 로드 시 <see cref="Take"/>(소비-1회)로 경로를 받아 연다.
/// </summary>
public sealed class ProjectHandoffService
{
    private string? _pending;

    /// <summary>복원할 프로젝트 JSON 경로를 예약한다.</summary>
    public void Set(string path) => _pending = path;

    /// <summary>예약된 경로를 1회 소비해 반환(없으면 null).</summary>
    public string? Take() { var p = _pending; _pending = null; return p; }
}
