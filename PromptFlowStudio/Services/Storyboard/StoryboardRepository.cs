using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 스토리보드 프로젝트 영속화 — <c>%LocalAppData%/PromptFlowStudio/Projects/{ProjectId}.json</c> 에
///   <see cref="StoryboardProject"/> 전체(컷 배치·프롬프트·상태)를 <see cref="System.Text.Json"/> 으로 직렬화/역직렬화.
///   저장은 .tmp → 교체로 원자적. 열기는 파일 경로 또는 스트림(파일 선택기) 양쪽 지원. 실패는 예외 없이 null/false.
/// </summary>
public sealed class StoryboardRepository
{
    /// <summary>프로젝트 저장 폴더(화면 표시용).</summary>
    public string ProjectsDir { get; } = Path.Combine(LicenseService.DataAreaRoot(), "Projects");

    /// <summary>프로젝트를 {ProjectId}.json 으로 저장하고 저장 경로를 반환한다. 실패 시 null.</summary>
    public string? Save(StoryboardProject project)
    {
        try
        {
            Directory.CreateDirectory(ProjectsDir);
            var path = Path.Combine(ProjectsDir, project.Id + ".json");
            var json = JsonSerializer.Serialize(project, PromptFlowJsonContext.Default.StoryboardProject);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
            return path;
        }
        catch (Exception ex)
        {
            Program.Log("StoryboardRepository.Save", ex);
            return null;
        }
    }

    /// <summary>JSON 문자열을 프로젝트로 역직렬화(파일 선택기 스트림용). 실패 시 null.</summary>
    public StoryboardProject? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.StoryboardProject); }
        catch (Exception ex) { Program.Log("StoryboardRepository.Deserialize", ex); return null; }
    }

    /// <summary>경로에서 프로젝트를 불러온다. 없거나 손상 시 null.</summary>
    public StoryboardProject? Load(string path)
    {
        try { return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null; }
        catch (Exception ex) { Program.Log("StoryboardRepository.Load", ex); return null; }
    }
}
