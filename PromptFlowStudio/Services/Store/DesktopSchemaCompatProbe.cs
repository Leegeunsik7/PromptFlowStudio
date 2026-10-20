using System;
using System.Text.Json;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Shared.Store;

namespace PromptFlowStudio.Services.Store;

/// <summary>
/// [PromptFlow Studio v4.3.0] 데스크톱 스키마 호환 프로브 — 실제 <see cref="StoryboardProject"/> 왕복으로
///   v1.0~v4.2 project.json 역호환을 정밀 검증(공용 RCL <see cref="ISchemaCompatProbe"/> 구현).
/// </summary>
public sealed class DesktopSchemaCompatProbe : ISchemaCompatProbe
{
    public SchemaCompatResult Check(string projectJson)
    {
        try
        {
            var project = JsonSerializer.Deserialize(projectJson, PromptFlowJsonContext.Default.StoryboardProject);
            if (project is null) return new SchemaCompatResult(false, 0, "역직렬화 결과 null");
            _ = JsonSerializer.Serialize(project, PromptFlowJsonContext.Default.StoryboardProject);   // 재직렬화까지 왕복 성공 확인(구버전 필드 부재 → 기본값 로드)
            var props = typeof(StoryboardProject).GetProperties().Length;
            var ok = !string.IsNullOrEmpty(project.Id) && project.Scenes is not null;
            return new SchemaCompatResult(ok, props, ok ? $"StoryboardProject 왕복 성공(필드 {props} · 누락은 기본값 로드)" : "코어 필드 손상");
        }
        catch (Exception ex) { return new SchemaCompatResult(false, 0, "역직렬화 실패: " + ex.Message); }
    }
}
