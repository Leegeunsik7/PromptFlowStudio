using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Flow;

// ════════════════════════════════════════════════════════════════════════════
//  [PromptFlow Studio v5.5.3 · Task3] 플로우 그래프 JSON 직렬화 — 순수 System.Text.Json(외부 파서 0).
//   ▸ 노드/연결을 <b>DTO 레벨</b>로 평면 직렬화(참조 순환 방지 · Executor/포트객체 미직렬화).
//   ▸ 역직렬화 시 종류(Kind)+보존 Id 로 <see cref="FlowExecutionPipeline.CreateNode"/> 재구성 → Executor·포트Id 결정적 복원 → 연결 재수립.
//   ⚠ 데스크톱 net8.0-windows 컴파일 정합(순수 BCL).
// ════════════════════════════════════════════════════════════════════════════
public static class FlowSerializer
{
    /// <summary>스키마 버전(향후 마이그레이션 대비).</summary>
    public const int Version = 1;

    /// <summary>노드 DTO — Executor/포트 객체 없이 평면 데이터만(포트는 Kind 로 재구성).</summary>
    public sealed record NodeDto(string Id, string Kind, string Title, string Emoji, double X, double Y, string Param);

    /// <summary>연결 DTO — 노드/포트 Id 참조(문자열)만.</summary>
    public sealed record ConnDto(string FromNodeId, string FromPortId, string ToNodeId, string ToPortId);

    /// <summary>플로우 그래프 문서.</summary>
    public sealed record GraphDto(int Version, List<NodeDto> Nodes, List<ConnDto> Connections);

    /// <summary>엔진 → .pfGraph JSON 문자열.</summary>
    public static string Serialize(NodeGraphEngine engine)
    {
        if (engine is null) return "{}";
        var dto = new GraphDto(
            Version,
            engine.Nodes.Select(n => new NodeDto(n.Id, n.Kind, n.Title, n.Emoji, n.X, n.Y, n.Param ?? "")).ToList(),
            engine.Connections.Select(c => new ConnDto(c.FromNodeId, c.FromPortId, c.ToNodeId, c.ToPortId)).ToList());
        return JsonSerializer.Serialize(dto, PromptFlowWireJsonContext.Default.FlowGraphDto);
    }

    /// <summary>
    /// .pfGraph JSON → 대상 엔진 재구성(기존 그래프 Clear 후 로드). 실패는 false + 사유(그래프 무변경 보장은 성공 시).
    /// </summary>
    public static bool TryLoad(string? json, FlowExecutionPipeline pipeline, NodeGraphEngine target, StoryboardProject? project, out string message)
    {
        if (string.IsNullOrWhiteSpace(json)) { message = "빈 파일입니다."; return false; }
        if (pipeline is null || target is null) { message = "엔진/파이프라인이 없습니다."; return false; }

        GraphDto? dto;
        try { dto = JsonSerializer.Deserialize(json!, PromptFlowWireJsonContext.Default.FlowGraphDto); }
        catch (Exception ex) { message = "JSON 파싱 실패: " + ex.Message; return false; }
        if (dto?.Nodes is null) { message = "유효하지 않은 플로우 파일입니다."; return false; }

        target.Clear();
        foreach (var nd in dto.Nodes)
        {
            if (string.IsNullOrWhiteSpace(nd.Id) || string.IsNullOrWhiteSpace(nd.Kind)) continue;
            var n = pipeline.CreateNode(nd.Kind, nd.Id, project);   // 동일 Id → 포트 Id(={id}:in/out:name)도 동일 복원
            n.Title = string.IsNullOrWhiteSpace(nd.Title) ? n.Title : nd.Title;
            n.Emoji = string.IsNullOrWhiteSpace(nd.Emoji) ? n.Emoji : nd.Emoji;
            n.X = nd.X; n.Y = nd.Y; n.Param = nd.Param ?? "";
            target.AddNode(n);
        }

        var conns = 0;
        foreach (var cd in dto.Connections ?? new List<ConnDto>())
            if (target.Connect(cd.FromNodeId, cd.FromPortId, cd.ToNodeId, cd.ToPortId) is not null) conns++;

        message = $"{dto.Nodes.Count}개 노드 · {conns}개 연결 불러옴";
        return true;
    }
}
