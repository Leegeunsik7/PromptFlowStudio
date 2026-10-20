using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Flow;

// ════════════════════════════════════════════════════════════════════════════
//  [PromptFlow Studio v5.5 · Task1] DAG 기반 비동기 노드 그래프 엔진 — 순수 C# BCL(외부 SDK 0).
//   ▸ Kahn 위상정렬로 노드 의존성 순서를 산출하고, 상위 노드 Output → 하위 노드 Input 으로 데이터 자동 전파(Propagate).
//   ▸ 노드별 비동기 Executor 를 순서대로 평가, 실행 상태(Idle/Running/Completed/Faulted)를 OnChanged 로 실시간 방송(UI 스피너/테두리).
//   ▸ 순환 의존성 감지(위상정렬 실패) · 상위 실패 시 하위 건너뜀 · 취소 토큰 이식(v4.4.4 하드닝 계승).
//   ⚠ 데이터 페이로드는 object? (제네릭) — 코어 엔진은 Wan/도메인에 비의존 → 데스크톱 net8.0-windows 컴파일 정합.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>포트 방향.</summary>
public enum FlowPortDirection { Input, Output }

/// <summary>노드 실행 상태 — 헤더 테두리 색/스피너 매핑.</summary>
public enum NodeRunState { Idle, Running, Completed, Faulted }

/// <summary>노드 포트 — 식별자/이름/방향/데이터타입 + 전파된 값(Value).</summary>
public sealed class FlowPort
{
    public string Id { get; }
    public string Name { get; }
    public FlowPortDirection Direction { get; }
    /// <summary>데이터 타입 태그(예: prompt/motion/video/any). "any" 는 모든 타입과 호환.</summary>
    public string DataType { get; }
    /// <summary>전파된 데이터(실행 시 채워짐).</summary>
    public object? Value { get; set; }

    public FlowPort(string id, string name, FlowPortDirection dir, string dataType)
    {
        Id = id; Name = name; Direction = dir; DataType = string.IsNullOrWhiteSpace(dataType) ? "any" : dataType;
    }
}

/// <summary>비주얼 노드 — 종류/제목/좌표/포트/실행상태 + 비동기 Executor(입력 dict → 출력 dict).</summary>
public sealed class FlowNode
{
    public string Id { get; }
    public string Kind { get; }
    public string Title { get; set; }
    public string Emoji { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>노드 파라미터(프롬프트 텍스트/모션 프리셋 등 · 종류별 해석).</summary>
    public string Param { get; set; } = "";

    public List<FlowPort> Inputs { get; } = new();
    public List<FlowPort> Outputs { get; } = new();

    public NodeRunState State { get; set; } = NodeRunState.Idle;
    public string? StatusMessage { get; set; }

    /// <summary>실행기 — 입력(포트명→값)을 받아 출력(포트명→값)을 반환. null 이면 통과(빈 출력).</summary>
    public Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>>? Executor { get; set; }

    public FlowNode(string id, string kind, string title, string emoji)
    {
        Id = id; Kind = kind; Title = title; Emoji = emoji;
    }

    public FlowPort AddInput(string name, string dataType) { var p = new FlowPort($"{Id}:in:{name}", name, FlowPortDirection.Input, dataType); Inputs.Add(p); return p; }
    public FlowPort AddOutput(string name, string dataType) { var p = new FlowPort($"{Id}:out:{name}", name, FlowPortDirection.Output, dataType); Outputs.Add(p); return p; }
}

/// <summary>포트 간 연결 — 출력(From) → 입력(To).</summary>
public sealed record FlowConnection(string Id, string FromNodeId, string FromPortId, string ToNodeId, string ToPortId);

/// <summary>그래프 실행 결과.</summary>
public sealed record GraphExecutionResult(bool Ok, int Executed, int Faulted, string Message)
{
    public static GraphExecutionResult Cyclic => new(false, 0, 0, "순환 의존성 감지 — DAG 가 아니므로 실행할 수 없습니다.");
}

/// <summary>
/// DAG 노드 그래프 엔진 — 노드/연결 소유 + 위상정렬 + 비동기 실행(데이터 전파). 캔버스(렌더)와 파이프라인(실행)이 공유.
/// </summary>
public sealed class NodeGraphEngine
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyOut = new Dictionary<string, object?>();

    private readonly Dictionary<string, FlowNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<FlowConnection> _connections = new();

    /// <summary>현재 노드(삽입 순서 무관 · 렌더용).</summary>
    public IReadOnlyCollection<FlowNode> Nodes => _nodes.Values;
    /// <summary>현재 연결.</summary>
    public IReadOnlyList<FlowConnection> Connections => _connections;
    /// <summary>실행 중 여부(중복 실행 방지).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>구조/상태 변경 방송(캔버스가 StateHasChanged 로 리렌더).</summary>
    public event Action? OnChanged;
    private void Notify() => OnChanged?.Invoke();

    // ── 구성 ──
    public FlowNode AddNode(FlowNode node) { if (node is null) return node!; _nodes[node.Id] = node; Notify(); return node; }

    public void RemoveNode(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        if (_nodes.Remove(nodeId)) { _connections.RemoveAll(c => c.FromNodeId == nodeId || c.ToNodeId == nodeId); Notify(); }
    }

    /// <summary>모든 노드/연결 제거(그래프 불러오기 전 초기화).</summary>
    public void Clear()
    {
        _nodes.Clear();
        _connections.Clear();
        Notify();
    }

    public FlowNode? Get(string? nodeId) => nodeId is not null && _nodes.TryGetValue(nodeId, out var n) ? n : null;

    public FlowPort? FindPort(string? nodeId, string? portId)
    {
        var node = Get(nodeId);
        if (node is null || portId is null) return null;
        return node.Inputs.FirstOrDefault(p => p.Id == portId) ?? node.Outputs.FirstOrDefault(p => p.Id == portId);
    }

    /// <summary>연결 가능성 검사 — 방향(출력→입력)·타입 호환·자기연결/순환 방지.</summary>
    public (bool Ok, string Reason) CanConnect(string fromNodeId, string fromPortId, string toNodeId, string toPortId)
    {
        if (fromNodeId == toNodeId) return (false, "같은 노드에는 연결할 수 없습니다.");
        var from = FindPort(fromNodeId, fromPortId);
        var to = FindPort(toNodeId, toPortId);
        if (from is null || to is null) return (false, "포트를 찾을 수 없습니다.");
        if (from.Direction != FlowPortDirection.Output) return (false, "출력 포트에서 시작해야 합니다.");
        if (to.Direction != FlowPortDirection.Input) return (false, "입력 포트로 연결해야 합니다.");
        if (!TypeCompatible(from.DataType, to.DataType)) return (false, $"타입 불일치({from.DataType}→{to.DataType}).");
        if (WouldCycle(fromNodeId, toNodeId)) return (false, "순환이 생겨 연결할 수 없습니다.");
        return (true, "");
    }

    private static bool TypeCompatible(string a, string b) => a == b || a == "any" || b == "any";

    /// <summary>연결 추가(입력 포트당 1개 유지 — 기존 유입 대체). 유효하지 않으면 null.</summary>
    public FlowConnection? Connect(string fromNodeId, string fromPortId, string toNodeId, string toPortId)
    {
        var (ok, _) = CanConnect(fromNodeId, fromPortId, toNodeId, toPortId);
        if (!ok) return null;
        _connections.RemoveAll(c => c.ToNodeId == toNodeId && c.ToPortId == toPortId);   // 입력당 단일 유입
        var conn = new FlowConnection(Guid.NewGuid().ToString("N")[..10], fromNodeId, fromPortId, toNodeId, toPortId);
        _connections.Add(conn);
        Notify();
        return conn;
    }

    public void Disconnect(string? connectionId)
    {
        if (connectionId is null) return;
        if (_connections.RemoveAll(c => c.Id == connectionId) > 0) Notify();
    }

    /// <summary>from→to 엣지를 더하면 순환이 생기는지 — to 에서 출발해 from 에 도달 가능하면 순환.</summary>
    private bool WouldCycle(string fromNodeId, string toNodeId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(toNodeId);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == fromNodeId) return true;
            if (!visited.Add(cur)) continue;
            foreach (var c in _connections.Where(c => c.FromNodeId == cur)) stack.Push(c.ToNodeId);
        }
        return false;
    }

    // ── 위상정렬(Kahn) ──
    /// <summary>Kahn 알고리즘 위상정렬 — 좌→우(X) 순 안정화. cyclic=true 면 순환(부분 순서 반환).</summary>
    public IReadOnlyList<FlowNode> TopologicalOrder(out bool cyclic)
    {
        var indeg = _nodes.Values.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        foreach (var c in _connections)
            if (indeg.ContainsKey(c.ToNodeId) && _nodes.ContainsKey(c.FromNodeId)) indeg[c.ToNodeId]++;

        // 진입차수 0 노드를 X(좌→우)·Y 순으로 시드(실행 순서 가독성).
        var ready = new List<FlowNode>(_nodes.Values.Where(n => indeg[n.Id] == 0).OrderBy(n => n.X).ThenBy(n => n.Y));
        var order = new List<FlowNode>(_nodes.Count);

        while (ready.Count > 0)
        {
            var n = ready[0]; ready.RemoveAt(0);
            order.Add(n);
            var nexts = _connections.Where(c => c.FromNodeId == n.Id)
                                    .Select(c => c.ToNodeId).Distinct()
                                    .Where(id => _nodes.ContainsKey(id));
            foreach (var toId in nexts)
            {
                if (--indeg[toId] == 0 && _nodes.TryGetValue(toId, out var tn))
                {
                    // 정렬 위치 유지(X 순) 삽입
                    var idx = ready.FindIndex(r => r.X > tn.X);
                    if (idx < 0) ready.Add(tn); else ready.Insert(idx, tn);
                }
            }
        }

        cyclic = order.Count != _nodes.Count;
        return order;
    }

    /// <summary>모든 노드 상태/포트 값 초기화(재실행 전).</summary>
    public void ResetStates()
    {
        foreach (var n in _nodes.Values)
        {
            n.State = NodeRunState.Idle; n.StatusMessage = null;
            foreach (var p in n.Inputs) p.Value = null;
            foreach (var p in n.Outputs) p.Value = null;
        }
        Notify();
    }

    // ── 실행(위상순 · 데이터 전파 · 비동기) ──
    /// <summary>
    /// 그래프를 위상 순서로 실행한다. 각 노드 직전에 상위 Output → 이 노드 Input 으로 값을 전파하고,
    ///   Executor 를 호출해 Output 값을 채운다. 상위 실패 노드의 하위는 자동 건너뜀(Faulted).
    /// </summary>
    public async Task<GraphExecutionResult> ExecuteAsync(CancellationToken ct = default)
    {
        if (IsRunning) return new(false, 0, 0, "이미 실행 중입니다.");
        IsRunning = true;
        try
        {
            ResetStates();
            var order = TopologicalOrder(out var cyclic);
            if (cyclic) return GraphExecutionResult.Cyclic;

            var faulted = new HashSet<string>(StringComparer.Ordinal);
            int executed = 0, faults = 0;

            foreach (var node in order)
            {
                if (ct.IsCancellationRequested) return new(false, executed, faults, "실행이 취소되었습니다.");

                // 상위 실패 → 이 노드 건너뜀
                var upstream = _connections.Where(c => c.ToNodeId == node.Id).Select(c => c.FromNodeId);
                if (upstream.Any(u => faulted.Contains(u)))
                {
                    node.State = NodeRunState.Faulted; node.StatusMessage = "상위 노드 실패로 건너뜀";
                    faulted.Add(node.Id); faults++; Notify(); continue;
                }

                // 데이터 전파: 각 입력 포트 ← 연결된 상위 출력 포트 Value
                var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var inPort in node.Inputs)
                {
                    var conn = _connections.FirstOrDefault(c => c.ToNodeId == node.Id && c.ToPortId == inPort.Id);
                    var val = conn is null ? null : FindPort(conn.FromNodeId, conn.FromPortId)?.Value;
                    inPort.Value = val;
                    inputs[inPort.Name] = val;
                }

                node.State = NodeRunState.Running; node.StatusMessage = null; Notify();
                try
                {
                    var outputs = node.Executor is null ? EmptyOut : await node.Executor(inputs, ct).ConfigureAwait(false);
                    foreach (var outPort in node.Outputs)
                        outPort.Value = outputs is not null && outputs.TryGetValue(outPort.Name, out var v) ? v : null;
                    node.State = NodeRunState.Completed; executed++; Notify();
                }
                catch (OperationCanceledException)
                {
                    node.State = NodeRunState.Idle; Notify();
                    return new(false, executed, faults, "실행이 취소되었습니다.");
                }
                catch (Exception ex)
                {
                    node.State = NodeRunState.Faulted; node.StatusMessage = ex.Message;
                    faulted.Add(node.Id); faults++; Notify();
                }
            }

            return new(faults == 0, executed, faults,
                faults == 0 ? $"{executed}개 노드 실행 완료" : $"{executed}개 완료 · {faults}개 실패");
        }
        finally { IsRunning = false; Notify(); }
    }
}
