using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>지식 그래프 노드 스냅샷(불변 · HUD/조회 표출).</summary>
public sealed record GraphNodeView(
    string Id, string Label, string SourceFile, string SourceLocation,
    string Namespace, int Community, string CommunityName, int Degree, int InDegree, int OutDegree);

/// <summary>지식 그래프 간선 스냅샷(불변 · 근거 파일/라인 동반).</summary>
public sealed record GraphEdgeView(
    string SourceLabel, string TargetLabel, string Relation,
    string SourceFile, string SourceLocation, double Weight);

/// <summary>커뮤니티(모듈 경계) 집계 스냅샷 — 내부 결합도 대비 경계 간선으로 응집도 판정.</summary>
public sealed record GraphCommunityView(int Id, string Name, int NodeCount, int InternalEdges, int BoundaryEdges)
{
    /// <summary>응집도 = 내부 간선 / (내부 + 경계) — 1.0 에 가까울수록 독립 모듈.</summary>
    public double Cohesion => InternalEdges + BoundaryEdges <= 0 ? 0d : (double)InternalEdges / (InternalEdges + BoundaryEdges);
}

/// <summary>역추적 경로 한 홉(불변 · 깊이 · 관계 · 근거 위치).</summary>
public sealed record GraphTraceHop(int Depth, string FromLabel, string ToLabel, string Relation, string SourceFile, string SourceLocation);

/// <summary>
/// [PromptFlow Studio v8.9.1] Graphify 지식 그래프 쿼리 엔진(관찰 가능 싱글턴 · 순수 BCL <see cref="JsonDocument"/> · 외부 NuGet 0).
///   ▸ <see cref="LoadAsync"/>: <c>graphify-out/graph.json</c>(6,874 노드 · 12,824 간선 · 402 커뮤니티)을 1회 파싱 후
///     CSR(Compressed Sparse Row) 인접 배열(<c>_outOffset</c>/<c>_inOffset</c>)로 접어 넣는다 — 파싱 후 <see cref="JsonDocument"/> 는 즉시 Dispose 되어 상주 할당 최소화.
///   ▸ <see cref="QueryDependencies"/>/<see cref="QueryDependents"/>: God Node 정방향 의존 · 역방향 피의존을 O(deg) 조회(조회 자체는 무탐색 · 인덱스 직참조).
///   ▸ <see cref="TraceModuleDependencies"/>: BFS 역추적으로 모듈 변경 파급 경로(근거 파일:라인 동반)를 깊이 제한 산출.
///   ▸ <see cref="Communities"/>/<see cref="CommunityBoundary"/>: 커뮤니티 내부/경계 간선을 집계해 모듈 경계 응집도를 실시간 판정.
///   ⚠ graph.json 부재/손상 시 예외를 삼키고 <see cref="LastError"/> 만 세운 뒤 빈 결과를 반환한다(제5헌장 Graceful Degradation · 앱 동작 무영향).
/// </summary>
public sealed class GraphifyQueryEngine
{
    private const int MaxProbeDepth = 7;             // 산출물 탐색 상향 한계(bin/Release/net8.0-windows → 리포 루트)
    private const int DefaultTraceDepth = 3;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);

    private NodeRecord[] _nodes = Array.Empty<NodeRecord>();
    private EdgeRecord[] _edges = Array.Empty<EdgeRecord>();
    private int[] _outOffset = Array.Empty<int>();   // CSR 오프셋(노드 i 의 정방향 간선 = _outIdx[_outOffset[i].._outOffset[i+1]])
    private int[] _outIdx = Array.Empty<int>();
    private int[] _inOffset = Array.Empty<int>();
    private int[] _inIdx = Array.Empty<int>();
    private CommunityRecord[] _communities = Array.Empty<CommunityRecord>();

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public string? GraphPath { get; private set; }
    public int NodeCount => _nodes.Length;
    public int EdgeCount => _edges.Length;
    public int CommunityCount => _communities.Length;
    public int CodeNodeCount { get; private set; }
    public int SemanticEdgeCount { get; private set; }
    public double LoadMs { get; private set; }
    public long GraphBytes { get; private set; }
    public long QueryCount { get; private set; }
    public string? LastAction { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LoadedAt { get; private set; }

    /// <summary>최대 차수 노드 라벨(God Node 대표 · 미로드 시 null).</summary>
    public string? TopGodNode { get; private set; }

    // ── 로드 ──

    /// <summary>
    /// graph.json 을 파싱해 인메모리 인덱스를 구축한다. <paramref name="explicitPath"/> 미지정 시 실행 디렉터리부터 상위로 <c>graphify-out/graph.json</c> 을 탐색한다.
    /// 이미 로드된 상태에서 재호출하면 재파싱(핫 리로드)하며, 실패 시 직전 인덱스를 보존한다.
    /// </summary>
    public async Task<bool> LoadAsync(string? explicitPath = null, CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        IsLoading = true;
        LastError = null;
        LastAction = "graph.json 파싱 중…";
        Notify();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var path = explicitPath ?? ResolveGraphPath();
            if (path is null || !File.Exists(path))
            {
                LastError = "graph.json 을 찾지 못했습니다(graphify-out 미생성 · /graphify 실행 필요).";
                LastAction = "로드 실패 — 빈 인덱스 유지";
                return false;
            }

            var info = new FileInfo(path);
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            using var doc = await JsonDocument.ParseAsync(fs, default, ct).ConfigureAwait(false);
            BuildIndex(doc.RootElement);

            GraphPath = path;
            GraphBytes = info.Length;
            IsLoaded = true;
            LoadedAt = DateTimeOffset.Now;
            LoadMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            LastAction = $"로드 완료 — 노드 {NodeCount} · 간선 {EdgeCount} · 커뮤니티 {CommunityCount}";
            return true;
        }
        catch (OperationCanceledException) { LastAction = "로드 취소"; return false; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastAction = "로드 실패 — 직전 인덱스 보존";
            return false;
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
            Notify();
        }
    }

    /// <summary>실행 디렉터리 → 상위 방향으로 graphify-out/graph.json 을 탐색(최대 <see cref="MaxProbeDepth"/> 단계).</summary>
    private static string? ResolveGraphPath()
    {
        var seeds = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var seed in seeds)
        {
            var dir = seed;
            for (var i = 0; i < MaxProbeDepth && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "graphify-out", "graph.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        return null;
    }

    /// <summary>노드/간선 배열 → 사전 + CSR 인접 구조 + 커뮤니티 집계로 1회 접기.</summary>
    private void BuildIndex(JsonElement root)
    {
        var nodesEl = root.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array ? n : default;
        var linksEl = root.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array ? l : default;

        var nodes = new NodeRecord[nodesEl.ValueKind == JsonValueKind.Array ? nodesEl.GetArrayLength() : 0];
        var byId = new Dictionary<string, int>(nodes.Length, StringComparer.Ordinal);
        var codeNodes = 0;
        var idx = 0;
        if (nodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in nodesEl.EnumerateArray())
            {
                var id = Str(el, "id");
                if (id.Length == 0) continue;
                var ns = el.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object ? Str(meta, "namespace") : string.Empty;
                var fileType = Str(el, "file_type");
                if (fileType.Length == 0 || string.Equals(fileType, "code", StringComparison.Ordinal)) codeNodes++;
                nodes[idx] = new NodeRecord(
                    id, Str(el, "label"), Str(el, "norm_label"), Str(el, "source_file"), Str(el, "source_location"),
                    ns, Int(el, "community", -1), Str(el, "community_name"));
                byId[id] = idx;
                idx++;
            }
        }
        if (idx != nodes.Length) Array.Resize(ref nodes, idx);

        var rawLinks = linksEl.ValueKind == JsonValueKind.Array ? linksEl.GetArrayLength() : 0;
        var edges = new EdgeRecord[rawLinks];
        var semantic = 0;
        var e = 0;
        if (linksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in linksEl.EnumerateArray())
            {
                if (!byId.TryGetValue(Str(el, "source"), out var s) || !byId.TryGetValue(Str(el, "target"), out var t)) continue;
                var origin = Str(el, "_origin");
                if (!string.Equals(origin, "ast", StringComparison.Ordinal)) semantic++;
                edges[e++] = new EdgeRecord(s, t, Str(el, "relation"), Str(el, "source_file"), Str(el, "source_location"), Dbl(el, "weight", 1d));
            }
        }
        if (e != edges.Length) Array.Resize(ref edges, e);

        // CSR 구축 — 카운팅 정렬 2패스(정방향/역방향 각각 오프셋 누적 후 채움).
        var outOffset = new int[nodes.Length + 1];
        var inOffset = new int[nodes.Length + 1];
        for (var i = 0; i < edges.Length; i++) { outOffset[edges[i].Source + 1]++; inOffset[edges[i].Target + 1]++; }
        for (var i = 0; i < nodes.Length; i++) { outOffset[i + 1] += outOffset[i]; inOffset[i + 1] += inOffset[i]; }
        var outIdx = new int[edges.Length];
        var inIdx = new int[edges.Length];
        var outCursor = (int[])outOffset.Clone();
        var inCursor = (int[])inOffset.Clone();
        for (var i = 0; i < edges.Length; i++)
        {
            outIdx[outCursor[edges[i].Source]++] = i;
            inIdx[inCursor[edges[i].Target]++] = i;
        }

        // 커뮤니티 집계 — 내부/경계 간선 분리 계수.
        var agg = new Dictionary<int, (string Name, int Nodes, int Internal, int Boundary)>();
        foreach (var node in nodes)
        {
            agg.TryGetValue(node.Community, out var cur);
            var name = cur.Name is { Length: > 0 } ? cur.Name : node.CommunityName;
            agg[node.Community] = (name, cur.Nodes + 1, cur.Internal, cur.Boundary);
        }
        foreach (var edge in edges)
        {
            var cs = nodes[edge.Source].Community;
            var ctg = nodes[edge.Target].Community;
            if (cs == ctg)
            {
                if (agg.TryGetValue(cs, out var a)) agg[cs] = (a.Name, a.Nodes, a.Internal + 1, a.Boundary);
            }
            else
            {
                if (agg.TryGetValue(cs, out var a1)) agg[cs] = (a1.Name, a1.Nodes, a1.Internal, a1.Boundary + 1);
                if (agg.TryGetValue(ctg, out var a2)) agg[ctg] = (a2.Name, a2.Nodes, a2.Internal, a2.Boundary + 1);
            }
        }
        var communities = new CommunityRecord[agg.Count];
        var c = 0;
        foreach (var kv in agg) communities[c++] = new CommunityRecord(kv.Key, kv.Value.Name, kv.Value.Nodes, kv.Value.Internal, kv.Value.Boundary);
        Array.Sort(communities, static (x, y) => y.Nodes.CompareTo(x.Nodes));

        _nodes = nodes;
        _edges = edges;
        _outOffset = outOffset;
        _outIdx = outIdx;
        _inOffset = inOffset;
        _inIdx = inIdx;
        _communities = communities;
        _indexById.Clear();
        foreach (var kv in byId) _indexById[kv.Key] = kv.Value;
        CodeNodeCount = codeNodes;
        SemanticEdgeCount = semantic;

        var best = -1;
        var bestDeg = -1;
        for (var i = 0; i < nodes.Length; i++)
        {
            var deg = Degree(i);
            if (deg > bestDeg) { bestDeg = deg; best = i; }
        }
        TopGodNode = best >= 0 ? _nodes[best].Label : null;
    }

    // ── 조회 ──

    /// <summary>라벨/노드 ID/소스 파일 부분 일치 검색(대소문자 무시 · 차수 내림차순).</summary>
    public IReadOnlyList<GraphNodeView> FindNodes(string term, int limit = 12)
    {
        var result = new List<GraphNodeView>();
        if (!IsLoaded || string.IsNullOrWhiteSpace(term)) return result;
        QueryCount++;
        var needle = term.Trim();
        for (var i = 0; i < _nodes.Length; i++)
        {
            ref readonly var node = ref _nodes[i];
            if (node.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || node.NormLabel.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || node.SourceFile.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ToView(i));
            }
        }
        result.Sort(static (x, y) => y.Degree.CompareTo(x.Degree));
        if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
        return result;
    }

    /// <summary>차수 상위 God Node 목록(허브 · 변경 파급 위험 지점).</summary>
    public IReadOnlyList<GraphNodeView> GodNodes(int top = 8)
    {
        var result = new List<GraphNodeView>();
        if (!IsLoaded) return result;
        QueryCount++;
        for (var i = 0; i < _nodes.Length; i++) result.Add(ToView(i));
        result.Sort(static (x, y) => y.Degree.CompareTo(x.Degree));
        if (result.Count > top) result.RemoveRange(top, result.Count - top);
        return result;
    }

    /// <summary>해당 노드가 <b>참조하는</b> 대상(정방향 의존 · 근거 파일:라인 동반).</summary>
    public IReadOnlyList<GraphEdgeView> QueryDependencies(string labelOrId, int limit = 24) => Neighbors(labelOrId, forward: true, limit);

    /// <summary>해당 노드를 <b>참조하는</b> 주체(역방향 피의존 — 삭제/시그니처 변경 시 깨질 호출부).</summary>
    public IReadOnlyList<GraphEdgeView> QueryDependents(string labelOrId, int limit = 24) => Neighbors(labelOrId, forward: false, limit);

    private IReadOnlyList<GraphEdgeView> Neighbors(string labelOrId, bool forward, int limit)
    {
        var result = new List<GraphEdgeView>();
        if (!IsLoaded) return result;
        QueryCount++;
        var i = Resolve(labelOrId);
        if (i < 0) return result;
        var offset = forward ? _outOffset : _inOffset;
        var index = forward ? _outIdx : _inIdx;
        for (var k = offset[i]; k < offset[i + 1] && result.Count < limit; k++)
        {
            ref readonly var edge = ref _edges[index[k]];
            result.Add(new GraphEdgeView(_nodes[edge.Source].Label, _nodes[edge.Target].Label, edge.Relation, edge.SourceFile, edge.SourceLocation, edge.Weight));
        }
        return result;
    }

    /// <summary>
    /// 모듈 의존 역추적 — <paramref name="labelOrId"/> 를 기점으로 BFS 로 파급 경로를 <paramref name="depth"/> 홉까지 전개.
    /// <paramref name="reverse"/>=true(기본)면 "이 심볼이 바뀌면 누가 깨지는가"(피의존), false 면 "이 심볼이 무엇에 기대는가"(의존).
    /// </summary>
    public IReadOnlyList<GraphTraceHop> TraceModuleDependencies(string labelOrId, int depth = DefaultTraceDepth, bool reverse = true, int limit = 40)
    {
        var hops = new List<GraphTraceHop>();
        if (!IsLoaded) return hops;
        QueryCount++;
        var start = Resolve(labelOrId);
        if (start < 0) return hops;

        var visited = new HashSet<int> { start };
        var frontier = new Queue<(int Node, int Depth)>();
        frontier.Enqueue((start, 0));
        var offset = reverse ? _inOffset : _outOffset;
        var index = reverse ? _inIdx : _outIdx;
        while (frontier.Count > 0 && hops.Count < limit)
        {
            var (node, d) = frontier.Dequeue();
            if (d >= depth) continue;
            for (var k = offset[node]; k < offset[node + 1] && hops.Count < limit; k++)
            {
                ref readonly var edge = ref _edges[index[k]];
                var next = reverse ? edge.Source : edge.Target;
                if (!visited.Add(next)) continue;
                hops.Add(new GraphTraceHop(d + 1, _nodes[edge.Source].Label, _nodes[edge.Target].Label, edge.Relation, edge.SourceFile, edge.SourceLocation));
                frontier.Enqueue((next, d + 1));
            }
        }
        return hops;
    }

    /// <summary>커뮤니티(모듈 경계) 집계 — 노드 수 내림차순 상위 <paramref name="top"/>.</summary>
    public IReadOnlyList<GraphCommunityView> Communities(int top = 8)
    {
        var result = new List<GraphCommunityView>();
        if (!IsLoaded) return result;
        var n = Math.Min(top, _communities.Length);
        for (var i = 0; i < n; i++)
        {
            ref readonly var c = ref _communities[i];
            result.Add(new GraphCommunityView(c.Id, c.Name, c.Nodes, c.Internal, c.Boundary));
        }
        return result;
    }

    /// <summary>특정 노드가 속한 커뮤니티 경계 요약(미로드/미발견 시 null).</summary>
    public GraphCommunityView? CommunityBoundary(string labelOrId)
    {
        if (!IsLoaded) return null;
        QueryCount++;
        var i = Resolve(labelOrId);
        if (i < 0) return null;
        var cid = _nodes[i].Community;
        foreach (var c in _communities)
        {
            if (c.Id == cid) return new GraphCommunityView(c.Id, c.Name, c.Nodes, c.Internal, c.Boundary);
        }
        return null;
    }

    // ── 내부 유틸 ──

    private int Resolve(string labelOrId)
    {
        if (string.IsNullOrWhiteSpace(labelOrId)) return -1;
        var key = labelOrId.Trim();
        if (_indexById.TryGetValue(key, out var direct)) return direct;
        var best = -1;
        var bestDeg = -1;
        for (var i = 0; i < _nodes.Length; i++)
        {
            if (!string.Equals(_nodes[i].Label, key, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_nodes[i].NormLabel, key, StringComparison.OrdinalIgnoreCase)) continue;
            var deg = Degree(i);
            if (deg > bestDeg) { bestDeg = deg; best = i; }
        }
        return best;
    }

    private int Degree(int i) => _outOffset[i + 1] - _outOffset[i] + (_inOffset[i + 1] - _inOffset[i]);

    private GraphNodeView ToView(int i)
    {
        ref readonly var node = ref _nodes[i];
        var outDeg = _outOffset[i + 1] - _outOffset[i];
        var inDeg = _inOffset[i + 1] - _inOffset[i];
        return new GraphNodeView(node.Id, node.Label, node.SourceFile, node.SourceLocation, node.Namespace, node.Community, node.CommunityName, outDeg + inDeg, inDeg, outDeg);
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement el, string name, int fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static double Dbl(JsonElement el, string name, double fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

    private readonly record struct NodeRecord(
        string Id, string Label, string NormLabel, string SourceFile, string SourceLocation,
        string Namespace, int Community, string CommunityName);

    private readonly record struct EdgeRecord(int Source, int Target, string Relation, string SourceFile, string SourceLocation, double Weight);

    private readonly record struct CommunityRecord(int Id, string Name, int Nodes, int Internal, int Boundary);
}
