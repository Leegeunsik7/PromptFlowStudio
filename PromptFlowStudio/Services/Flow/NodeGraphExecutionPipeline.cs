using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Flow;

/// <summary>v16.21.0 비주얼 노드 종류.</summary>
public enum VisualNodeKind : byte { Prompt = 0, ControlNet = 1, WanGenerator = 2, Upscaler = 3, PostProcess = 4, Output = 5 }

/// <summary>v16.21.0 포트 데이터 타입 — Text 는 Control 입력으로 암묵 승격 가능.</summary>
public enum VisualPortType : byte { None = 0, Text = 1, Control = 2, Video = 3 }

/// <summary>v16.21.0 노드 실행 상태.</summary>
public enum VisualNodeState : byte { Idle = 0, Queued = 1, Running = 2, Completed = 3, Faulted = 4, Skipped = 5 }

/// <summary>v16.21.0 노드 간 전달값 — 타입 · 텍스트(프롬프트) · 경로(영상/마스크) · 힌트(ControlNet 모드) · 참조 이미지 · 강도.</summary>
public readonly record struct NodeValue(VisualPortType Type, string? Text, string? Path, string? Hint = null, string? ReferenceImage = null, double Strength = 0)
{
    public static readonly NodeValue Empty = default;
    public bool IsEmpty => Type == VisualPortType.None;
}

/// <summary>v16.21.0 노드 종류 정의 — 아이콘 · 라벨 · 입력/출력 타입.</summary>
public readonly record struct VisualNodeSpec(VisualNodeKind Kind, string Icon, string Label, VisualPortType Input, VisualPortType Output);

/// <summary>
/// v16.21.0 캔버스 노드(살아있는 엔티티) — 위치 · 파라미터 · 실행 상태. 실행 중 상태는 파이프라인이 제자리 갱신.
///   Text: 프롬프트 / ControlNet 모드(canny·depth·pose·reference·inpaint) / 후처리 프리셋. AssetPath: 참조 이미지. MaskPath: 인페인팅 마스크.
/// </summary>
public sealed class VisualNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public VisualNodeKind Kind { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public string? AssetPath { get; set; }
    public string? MaskPath { get; set; }
    public double Strength { get; set; } = 0.6;
    public int Scale { get; set; } = 2;

    public VisualNodeState State { get; set; }
    public string? Status { get; set; }
    public string? ResultPath { get; set; }
    public double ElapsedMs { get; set; }
}

/// <summary>v16.21.0 연결(출력 → 입력 · 노드당 입력 1개).</summary>
public readonly record struct VisualEdge(string From, string To);

/// <summary>v16.21.0 컴파일 결과 — 위상 순서 · 레벨(병렬 그룹) · 상위 인덱스(−1 없음). 정수 배열(실행 루프 무할당 근거).</summary>
public sealed class CompiledGraphPlan
{
    public required int[] Order { get; init; }
    public required int[] LevelStarts { get; init; }   // Order 상 레벨 i 의 시작 위치(길이 = 레벨 수 + 1)
    public required int[] Upstream { get; init; }       // 노드 인덱스 → 상위 노드 인덱스
    public required int MaxWidth { get; init; }
    public int Levels => LevelStarts.Length - 1;
}

/// <summary>v16.21.0 그래프 검증 결과.</summary>
public readonly record struct GraphValidation(bool Ok, string Message, CompiledGraphPlan? Plan);

/// <summary>v16.21.0 실행 백엔드.</summary>
public enum GraphRunBackend { Local, RemoteGpu }

/// <summary>v16.21.0 실행 결과.</summary>
public sealed record GraphRunResult(bool Ok, GraphRunBackend Backend, int Executed, int Faulted, int Skipped, IReadOnlyList<string> Outputs, double ElapsedMs, string Message);

/// <summary>v16.21.0 실행 옵션 — 레벨 내 최대 병렬 · 출력 폴더 · 원격 GPU 오프로딩 시도.</summary>
public sealed record GraphRunOptions(int MaxParallel = 2, string? OutputRoot = null, bool OffloadToRemote = false);

/// <summary>v16.21.0 노드 실행 문맥.</summary>
public sealed record NodeRunContext(string OutputRoot, IProgress<double>? Progress);

/// <summary>v16.21.0 노드 실행기 계약(종류별 1개).</summary>
public interface INodeExecutor
{
    ValueTask<NodeValue> ExecuteAsync(VisualNode node, NodeValue input, NodeRunContext ctx, CancellationToken ct);
}

/// <summary>v16.21.0 델리게이트 실행기(테스트·커스텀 결선).</summary>
public sealed class DelegateNodeExecutor(Func<VisualNode, NodeValue, NodeRunContext, CancellationToken, ValueTask<NodeValue>> run) : INodeExecutor
{
    public ValueTask<NodeValue> ExecuteAsync(VisualNode node, NodeValue input, NodeRunContext ctx, CancellationToken ct) => run(node, input, ctx, ct);
}

/// <summary>
/// [PromptFlowStudio] v16.21.0 🧩 비주얼 노드 그래프 — 노드/연결 소유 · 연결 규칙(타입·입력 1개·순환 차단) · 정수 인덱스 Kahn 컴파일(레벨 = 병렬 그룹) · JSON 직렬화.
///   ▸ v5.5 <see cref="NodeGraphEngine"/>(문자열 키·LINQ·순차)와 별개인 타입 지정 파이프라인 — 기존 /flow 캔버스는 무변경.
///   ▸ 구조 변경은 lock 보호 + <see cref="OnChanged"/>(예외 격리). 노드 상한 64.
/// </summary>
public sealed class VisualNodeGraph
{
    public const int MaxNodes = 64;

    public static readonly VisualNodeSpec[] Specs =
    {
        new(VisualNodeKind.Prompt, "✍️", "프롬프트", VisualPortType.None, VisualPortType.Text),
        new(VisualNodeKind.ControlNet, "🧭", "ControlNet", VisualPortType.Control, VisualPortType.Control),
        new(VisualNodeKind.WanGenerator, "🎬", "Wan 생성기", VisualPortType.Control, VisualPortType.Video),
        new(VisualNodeKind.Upscaler, "🔍", "업스케일러", VisualPortType.Video, VisualPortType.Video),
        new(VisualNodeKind.PostProcess, "🎨", "후처리", VisualPortType.Video, VisualPortType.Video),
        new(VisualNodeKind.Output, "📦", "출력", VisualPortType.Video, VisualPortType.None),
    };

    public static VisualNodeSpec SpecOf(VisualNodeKind k) => Specs[(int)k];

    /// <summary>타입 호환 — 동일 또는 Text→Control 승격.</summary>
    public static bool Compatible(VisualPortType from, VisualPortType to)
        => from != VisualPortType.None && to != VisualPortType.None && (from == to || (from == VisualPortType.Text && to == VisualPortType.Control));

    private readonly object _gate = new();
    private readonly List<VisualNode> _nodes = new(16);
    private readonly List<VisualEdge> _edges = new(16);

    public event Action? OnChanged;

    public IReadOnlyList<VisualNode> Nodes { get { lock (_gate) return _nodes.ToArray(); } }
    public IReadOnlyList<VisualEdge> Edges { get { lock (_gate) return _edges.ToArray(); } }
    public int NodeCount { get { lock (_gate) return _nodes.Count; } }
    public int EdgeCount { get { lock (_gate) return _edges.Count; } }

    /// <summary>상태별 노드 수(무할당 · HUD 용).</summary>
    public int CountState(VisualNodeState s)
    {
        lock (_gate)
        {
            var c = 0;
            foreach (var n in _nodes) if (n.State == s) c++;
            return c;
        }
    }

    public VisualNode? Find(string? id)
    {
        if (id is null) return null;
        lock (_gate) return IndexOf(id) is var i and >= 0 ? _nodes[i] : null;
    }

    public VisualNode? Add(VisualNodeKind kind, double x, double y, string text = "")
    {
        VisualNode node;
        lock (_gate)
        {
            if (_nodes.Count >= MaxNodes) return null;
            node = new VisualNode { Kind = kind, X = x, Y = y, Text = text };
            _nodes.Add(node);
        }
        Raise();
        return node;
    }

    /// <summary>역직렬화·동기화용 — 동일 Id 존재 시 거부.</summary>
    public bool AddExisting(VisualNode node)
    {
        lock (_gate)
        {
            if (_nodes.Count >= MaxNodes || IndexOf(node.Id) >= 0) return false;
            _nodes.Add(node);
        }
        Raise();
        return true;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var i = IndexOf(id);
            if (i < 0) return false;
            _nodes.RemoveAt(i);
            for (var e = _edges.Count - 1; e >= 0; e--)
                if (_edges[e].From == id || _edges[e].To == id) _edges.RemoveAt(e);
        }
        Raise();
        return true;
    }

    public void Clear()
    {
        lock (_gate) { _nodes.Clear(); _edges.Clear(); }
        Raise();
    }

    public void Move(string id, double x, double y)
    {
        var n = Find(id);
        if (n is null) return;
        n.X = x; n.Y = y;
        Raise();
    }

    /// <summary>연결 가능 판정 — 자기 연결 · 타입 · 순환.</summary>
    public (bool Ok, string Reason) CanConnect(string from, string to)
    {
        lock (_gate) return CanConnectLocked(from, to);
    }

    private (bool Ok, string Reason) CanConnectLocked(string from, string to)
    {
        if (from == to) return (false, "같은 노드에는 연결할 수 없습니다.");
        var fi = IndexOf(from);
        var ti = IndexOf(to);
        if (fi < 0 || ti < 0) return (false, "노드를 찾을 수 없습니다.");
        var fo = SpecOf(_nodes[fi].Kind).Output;
        var tin = SpecOf(_nodes[ti].Kind).Input;
        if (!Compatible(fo, tin)) return (false, "타입 불일치(" + fo.ToString() + "→" + tin.ToString() + ")");
        if (Reaches(to, from)) return (false, "순환이 생겨 연결할 수 없습니다.");
        return (true, "");
    }

    /// <summary>연결(대상 입력의 기존 연결은 대체). 실패 시 사유.</summary>
    public (bool Ok, string Reason) Connect(string from, string to)
    {
        lock (_gate)
        {
            var r = CanConnectLocked(from, to);
            if (!r.Ok) return r;
            for (var e = _edges.Count - 1; e >= 0; e--) if (_edges[e].To == to) _edges.RemoveAt(e);
            _edges.Add(new VisualEdge(from, to));
        }
        Raise();
        return (true, "");
    }

    public bool Disconnect(string from, string to)
    {
        bool removed;
        lock (_gate) removed = _edges.Remove(new VisualEdge(from, to));
        if (removed) Raise();
        return removed;
    }

    /// <summary>a 에서 b 로 도달 가능?(엣지 수 ≤ 64 → 스택 배열 DFS).</summary>
    private bool Reaches(string a, string b)
    {
        Span<int> stack = stackalloc int[MaxNodes + 1];
        Span<bool> seen = stackalloc bool[MaxNodes];
        var start = IndexOf(a);
        var target = IndexOf(b);
        if (start < 0 || target < 0) return false;
        var sp = 0;
        stack[sp++] = start;
        while (sp > 0)
        {
            var cur = stack[--sp];
            if (cur == target) return true;
            if (seen[cur]) continue;
            seen[cur] = true;
            var id = _nodes[cur].Id;
            foreach (var e in _edges)
                if (e.From == id && sp < stack.Length) { var ni = IndexOf(e.To); if (ni >= 0 && !seen[ni]) stack[sp++] = ni; }
        }
        return false;
    }

    /// <summary>
    /// 검증 + 컴파일 — 입력 누락(Prompt 외) · 순환 · 빈 그래프 · 출력 없는 말단 경고 없이 실행 가능 여부 판정. 레벨은 최장 경로 깊이.
    /// </summary>
    public GraphValidation Compile()
    {
        lock (_gate)
        {
            var n = _nodes.Count;
            if (n == 0) return new(false, "노드가 없습니다.", null);
            var upstream = new int[n];
            Array.Fill(upstream, -1);
            var indeg = new int[n];
            foreach (var e in _edges)
            {
                var f = IndexOf(e.From);
                var t = IndexOf(e.To);
                if (f < 0 || t < 0) continue;
                upstream[t] = f;
                indeg[t]++;
            }
            for (var i = 0; i < n; i++)
            {
                var spec = SpecOf(_nodes[i].Kind);
                if (spec.Input != VisualPortType.None && upstream[i] < 0)
                    return new(false, spec.Label + " 노드(" + _nodes[i].Id + ")에 입력이 연결되지 않았습니다.", null);
            }
            // 레벨 = 상위 레벨 + 1 (입력 1개 → 트리/숲 · Kahn 순회)
            var level = new int[n];
            var queue = new int[n];
            int head = 0, tail = 0;
            for (var i = 0; i < n; i++) if (indeg[i] == 0) queue[tail++] = i;
            var maxLevel = 0;
            while (head < tail)
            {
                var cur = queue[head++];
                for (var i = 0; i < n; i++)
                {
                    if (upstream[i] != cur) continue;
                    level[i] = level[cur] + 1;
                    if (level[i] > maxLevel) maxLevel = level[i];
                    if (--indeg[i] == 0) queue[tail++] = i;
                }
            }
            if (tail != n) return new(false, "순환 의존성 감지 — 실행할 수 없습니다.", null);
            // 레벨 버킷 정렬(안정 · 같은 레벨은 X 순)
            var counts = new int[maxLevel + 2];
            for (var i = 0; i < n; i++) counts[level[i] + 1]++;
            for (var l = 1; l < counts.Length; l++) counts[l] += counts[l - 1];
            var order = new int[n];
            var fill = (int[])counts.Clone();
            for (var i = 0; i < n; i++) order[fill[level[i]]++] = i;
            var maxWidth = 0;
            for (var l = 0; l <= maxLevel; l++)
            {
                var s = counts[l];
                var e = counts[l + 1];
                if (e - s > maxWidth) maxWidth = e - s;
                for (var a = s + 1; a < e; a++)
                {
                    var key = order[a];
                    var b = a - 1;
                    while (b >= s && _nodes[order[b]].X > _nodes[key].X) { order[b + 1] = order[b]; b--; }
                    order[b + 1] = key;
                }
            }
            var plan = new CompiledGraphPlan { Order = order, LevelStarts = counts, Upstream = upstream, MaxWidth = maxWidth };
            return new(true, n.ToString(CultureInfo.InvariantCulture) + "개 노드 · " + (maxLevel + 1).ToString(CultureInfo.InvariantCulture) + "단계 · 최대 병렬 " + maxWidth.ToString(CultureInfo.InvariantCulture), plan);
        }
    }

    /// <summary>실행 스냅숏(컴파일 시점 노드 배열 — 인덱스 = Plan 인덱스).</summary>
    public VisualNode[] SnapshotNodes() { lock (_gate) return _nodes.ToArray(); }

    public void ResetStates()
    {
        lock (_gate)
            foreach (var nd in _nodes) { nd.State = VisualNodeState.Idle; nd.Status = null; nd.ResultPath = null; nd.ElapsedMs = 0; }
        Raise();
    }

    /// <summary>기본 템플릿 — Prompt ➔ ControlNet ➔ Wan ➔ {Upscaler ➔ Output, PostProcess ➔ Output}(병렬 분기).</summary>
    public void LoadDefaultTemplate(string prompt = "a cute baby playing with colorful blocks, soft daylight")
    {
        Clear();
        var p = Add(VisualNodeKind.Prompt, 40, 160, prompt)!;
        var c = Add(VisualNodeKind.ControlNet, 280, 160, "reference")!;
        var w = Add(VisualNodeKind.WanGenerator, 520, 160)!;
        var u = Add(VisualNodeKind.Upscaler, 760, 60)!;
        var pp = Add(VisualNodeKind.PostProcess, 760, 280, "cinematic")!;
        var o1 = Add(VisualNodeKind.Output, 1000, 60)!;
        var o2 = Add(VisualNodeKind.Output, 1000, 280)!;
        Connect(p.Id, c.Id); Connect(c.Id, w.Id); Connect(w.Id, u.Id); Connect(w.Id, pp.Id); Connect(u.Id, o1.Id); Connect(pp.Id, o2.Id);
    }

    // ── 직렬화(동기화 · 원격 오프로딩 · 저장) ──

    public sealed record NodeDto(string Id, VisualNodeKind Kind, double X, double Y, string Text, string? AssetPath, string? MaskPath, double Strength, int Scale);
    public sealed record GraphDto(int Schema, int NodeCount, int EdgeCount, IReadOnlyList<NodeDto> Nodes, IReadOnlyList<VisualEdge> Edges);

    public const int SchemaVersion = 1;

    public GraphDto ToDto()
    {
        lock (_gate)
        {
            var nodes = new List<NodeDto>(_nodes.Count);
            foreach (var n in _nodes) nodes.Add(new NodeDto(n.Id, n.Kind, n.X, n.Y, n.Text, n.AssetPath, n.MaskPath, n.Strength, n.Scale));
            return new GraphDto(SchemaVersion, _nodes.Count, _edges.Count, nodes, _edges.ToArray());
        }
    }

    public string ToJson() => JsonSerializer.Serialize(ToDto());

    /// <summary>DTO 적용(스키마·상한·Id·집계 필드 검증 · 실패 시 무변경).</summary>
    public (bool Ok, string Message) LoadDto(GraphDto? dto)
    {
        if (dto is null || dto.Schema != SchemaVersion) return (false, "스키마 불일치");
        if (dto.Nodes is null || dto.Edges is null || dto.Nodes.Count > MaxNodes) return (false, "노드 수 초과/누락");
        if (dto.NodeCount != dto.Nodes.Count || dto.EdgeCount != dto.Edges.Count) return (false, "집계 필드 불일치");
        foreach (var nd in dto.Nodes)
            if (!IsSafeId(nd.Id) || !Enum.IsDefined(nd.Kind)) return (false, "노드 Id/종류 오류");
        lock (_gate)
        {
            _nodes.Clear();
            _edges.Clear();
            foreach (var nd in dto.Nodes)
                _nodes.Add(new VisualNode
                {
                    Id = nd.Id, Kind = nd.Kind, X = nd.X, Y = nd.Y, Text = nd.Text ?? "", AssetPath = nd.AssetPath, MaskPath = nd.MaskPath,
                    Strength = Math.Clamp(nd.Strength, 0, 1), Scale = Math.Clamp(nd.Scale, 1, 4),
                });
            foreach (var e in dto.Edges)
                if (CanConnectLocked(e.From, e.To).Ok) { for (var k = _edges.Count - 1; k >= 0; k--) if (_edges[k].To == e.To) _edges.RemoveAt(k); _edges.Add(e); }
        }
        Raise();
        return (true, "불러옴");
    }

    public (bool Ok, string Message) LoadJson(string json)
    {
        try { return LoadDto(JsonSerializer.Deserialize<GraphDto>(json)); }
        catch (JsonException) { return (false, "JSON 파싱 실패"); }
    }

    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 32) return false;
        foreach (var c in id) if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        return true;
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < _nodes.Count; i++) if (_nodes[i].Id == id) return i;
        return -1;
    }

    internal void Raise()
    {
        try { OnChanged?.Invoke(); } catch (Exception ex) { Program.Log("VisualGraph.OnChanged", ex); }
    }
}

/// <summary>
/// [PromptFlowStudio] v16.21.0 ⚙️ 노드 그래프 실행 파이프라인 — 컴파일된 정수 계획을 레벨 단위로 실행(레벨 내 병렬 · SemaphoreSlim 상한).
///   ▸ 실행 루프: 노드 값/상태는 실행 1회당 배열 1개씩 선할당 · 레벨 Task 배열 재사용 · LINQ/클로저 캡처 0(정적 실행 헬퍼).
///   ▸ 상위 실패/건너뜀 → 하위 Skipped([부분 구제] — 다른 분기는 계속). 취소 시 진행 노드 Idle 복귀.
///   ▸ 원격 GPU 오프로딩(<see cref="IRemoteGraphRunner"/>) 시도 → 실패 시 즉시 로컬 실행 폴백.
///   ▸ 기본 실행기: 프롬프트 · ControlNet(구조 가이드 토큰 + 참조 이미지 i2v · 마스크 메타) · Wan(IWanApiService 생성 → 로컬 다운로드)
///     · 업스케일(IVideoUpscaleService · 엔진 없으면 통과) · 후처리(ffmpeg 프리셋 · 없으면 통과) · 출력(수집).
/// </summary>
public sealed class NodeGraphExecutionPipeline
{
    private readonly INodeExecutor?[] _executors = new INodeExecutor?[6];
    private readonly IRemoteGraphRunner? _remote;
    private int _busy;

    public event Action? OnChanged;

    public bool IsRunning => Volatile.Read(ref _busy) != 0;
    public GraphRunResult? LastResult { get; private set; }
    public int TotalRuns { get; private set; }
    public int TotalNodesExecuted { get; private set; }
    public bool RemoteAvailable => _remote is { IsConfigured: true };

    /// <summary>DI 생성자 — 기본 실행기 결선.</summary>
    public NodeGraphExecutionPipeline(IWanApiService wan, ILocalMediaService media, IVideoUpscaleService upscale, IVideoMergeService merge,
        IRemoteGraphRunner? remote = null)
        : this(DefaultExecutors(wan, media, upscale, () => merge.ResolvedFfmpegPath), remote) { }

    /// <summary>테스트/커스텀 생성자 — 종류별 실행기 주입.</summary>
    public NodeGraphExecutionPipeline(IReadOnlyDictionary<VisualNodeKind, INodeExecutor> executors, IRemoteGraphRunner? remote = null)
    {
        foreach (var kv in executors) _executors[(int)kv.Key] = kv.Value;
        _remote = remote;
    }

    /// <summary>그래프 실행(예외 0).</summary>
    public async Task<GraphRunResult> ExecuteAsync(VisualNodeGraph graph, GraphRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var opt = options ?? new GraphRunOptions();
        if (Interlocked.Exchange(ref _busy, 1) != 0) return new(false, GraphRunBackend.Local, 0, 0, 0, Array.Empty<string>(), 0, "이미 실행 중입니다.");
        var sw = Stopwatch.StartNew();
        Raise();
        try
        {
            var v = graph.Compile();
            if (!v.Ok || v.Plan is null) return Finish(new(false, GraphRunBackend.Local, 0, 0, 0, Array.Empty<string>(), 0, v.Message));
            graph.ResetStates();
            var root = opt.OutputRoot ?? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "NodeGraph", DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);

            var remoteNote = "";
            if (opt.OffloadToRemote && _remote is { IsConfigured: true })
            {
                var rr = await _remote.RunAsync(graph, root, ct).ConfigureAwait(false);
                if (rr.Ok) return Finish(rr with { ElapsedMs = sw.Elapsed.TotalMilliseconds });
                if (ct.IsCancellationRequested) return Finish(new(false, GraphRunBackend.RemoteGpu, 0, 0, 0, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, "취소됨"));
                remoteNote = "원격 GPU 실패(" + rr.Message + ") → 로컬 실행 · ";
                graph.ResetStates();
            }

            var nodes = graph.SnapshotNodes();
            var plan = v.Plan;
            var values = new NodeValue[nodes.Length];
            var ctx = new NodeRunContext(root, null);
            var tasks = new Task[Math.Max(1, plan.MaxWidth)];
            using var gate = new SemaphoreSlim(Math.Clamp(opt.MaxParallel, 1, 8));
            foreach (var i in plan.Order) { nodes[i].State = VisualNodeState.Queued; }
            graph.Raise();

            for (var l = 0; l < plan.Levels; l++)
            {
                ct.ThrowIfCancellationRequested();
                var s = plan.LevelStarts[l];
                var e = plan.LevelStarts[l + 1];
                var t = 0;
                for (var k = s; k < e; k++) tasks[t++] = RunNodeAsync(this, graph, nodes, values, plan, plan.Order[k], ctx, gate, ct);
                for (var k = 0; k < t; k++) await tasks[k].ConfigureAwait(false);
                Array.Clear(tasks, 0, t);
            }

            int ok = 0, faulted = 0, skipped = 0;
            var outputs = new List<string>(2);
            for (var i = 0; i < nodes.Length; i++)
            {
                switch (nodes[i].State)
                {
                    case VisualNodeState.Completed: ok++; break;
                    case VisualNodeState.Faulted: faulted++; break;
                    case VisualNodeState.Skipped: skipped++; break;
                }
                if (nodes[i].Kind == VisualNodeKind.Output && nodes[i].State == VisualNodeState.Completed && nodes[i].ResultPath is { } p && !outputs.Contains(p)) outputs.Add(p);
            }
            var msg = remoteNote + ok.ToString(CultureInfo.InvariantCulture) + "개 완료"
                + (faulted > 0 ? " · " + faulted.ToString(CultureInfo.InvariantCulture) + "개 실패" : "")
                + (skipped > 0 ? " · " + skipped.ToString(CultureInfo.InvariantCulture) + "개 건너뜀" : "")
                + " · 출력 " + outputs.Count.ToString(CultureInfo.InvariantCulture) + "개";
            return Finish(new(faulted == 0 && skipped == 0, GraphRunBackend.Local, ok, faulted, skipped, outputs, sw.Elapsed.TotalMilliseconds, msg));
        }
        catch (OperationCanceledException)
        {
            foreach (var n in graph.SnapshotNodes()) if (n.State is VisualNodeState.Running or VisualNodeState.Queued) n.State = VisualNodeState.Idle;
            graph.Raise();
            return Finish(new(false, GraphRunBackend.Local, 0, 0, 0, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, "취소됨"));
        }
        catch (Exception ex)
        {
            Program.Log("NodeGraphPipeline.Execute", ex);
            return Finish(new(false, GraphRunBackend.Local, 0, 0, 0, Array.Empty<string>(), sw.Elapsed.TotalMilliseconds, "실행 실패: " + ex.Message));
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private static async Task RunNodeAsync(NodeGraphExecutionPipeline self, VisualNodeGraph graph, VisualNode[] nodes, NodeValue[] values,
        CompiledGraphPlan plan, int index, NodeRunContext ctx, SemaphoreSlim gate, CancellationToken ct)
    {
        var node = nodes[index];
        var up = plan.Upstream[index];
        if (up >= 0 && nodes[up].State != VisualNodeState.Completed)
        {
            node.State = VisualNodeState.Skipped;
            node.Status = "상위 노드 미완료로 건너뜀";
            graph.Raise();
            return;
        }
        var input = up >= 0 ? values[up] : NodeValue.Empty;
        var exec = self._executors[(int)node.Kind];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var start = Stopwatch.GetTimestamp();
        try
        {
            node.State = VisualNodeState.Running;
            node.Status = "실행 중…";
            graph.Raise();
            var result = exec is null ? input : await exec.ExecuteAsync(node, input, ctx, ct).ConfigureAwait(false);
            if (result.IsEmpty && node.Kind != VisualNodeKind.Output) throw new InvalidOperationException("빈 출력");
            values[index] = result;
            node.ResultPath = result.Path ?? node.ResultPath;
            node.State = VisualNodeState.Completed;
            if (node.Status is null or "실행 중…") node.Status = "완료";
        }
        catch (OperationCanceledException) { node.State = VisualNodeState.Idle; node.Status = "취소됨"; throw; }
        catch (Exception ex)
        {
            node.State = VisualNodeState.Faulted;
            node.Status = ex.Message;
            Program.Log("NodeGraphPipeline.Node", ex);
        }
        finally
        {
            node.ElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            gate.Release();
            graph.Raise();
        }
    }

    private GraphRunResult Finish(GraphRunResult r)
    {
        LastResult = r;
        TotalRuns++;
        TotalNodesExecuted += r.Executed;
        return r;
    }

    private void Raise()
    {
        try { OnChanged?.Invoke(); } catch (Exception ex) { Program.Log("NodeGraphPipeline.OnChanged", ex); }
    }

    // ── 기본 실행기 ─────────────────────────────────────────────────────────

    /// <summary>ControlNet 모드 → Wan 구조 가이드 토큰(네이티브 ControlNet 가중치를 받지 않는 백엔드용 프롬프트 변환).</summary>
    public static string ControlTokens(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "canny" => "preserve the exact outlines and edges of the reference composition",
        "depth" => "keep the same depth layout and camera distance as the reference",
        "pose" => "keep the same body pose and limb positions as the reference",
        "inpaint" => "only change the masked region, keep everything else identical",
        _ => "match the reference image composition, colors and subject identity",
    };

    /// <summary>후처리 프리셋 → ffmpeg -vf(순수).</summary>
    public static string PostFilter(string? preset) => (preset ?? "").Trim().ToLowerInvariant() switch
    {
        "sharpen" => "unsharp=5:5:0.8:3:3:0.4",
        "warm" => "colorbalance=rs=0.06:gs=0.02:bs=-0.05,eq=saturation=1.08",
        "bright" => "eq=brightness=0.04:contrast=1.04:saturation=1.05",
        "none" => "null",
        _ => "eq=contrast=1.06:saturation=1.1,vignette=PI/5",   // cinematic
    };

    /// <summary>ControlNet 출력 값(순수) — 입력 텍스트 + 모드 토큰 · 참조/마스크 경로 · 강도.</summary>
    public static NodeValue BuildControl(VisualNode node, NodeValue input)
    {
        var text = (input.Text ?? "").Trim();
        if (text.Length == 0) throw new InvalidOperationException("입력 프롬프트가 비었습니다.");
        var mode = string.IsNullOrWhiteSpace(node.Text) ? "reference" : node.Text.Trim().ToLowerInvariant();
        var hasRef = !string.IsNullOrWhiteSpace(node.AssetPath) && File.Exists(node.AssetPath);
        var hasMask = !string.IsNullOrWhiteSpace(node.MaskPath) && File.Exists(node.MaskPath);
        if (mode == "inpaint" && !hasMask) mode = "reference";
        var guided = hasRef || hasMask ? text + ", " + ControlTokens(mode) : text;
        return new NodeValue(VisualPortType.Control, guided, hasMask ? node.MaskPath : null, mode, hasRef ? node.AssetPath : null, Math.Clamp(node.Strength, 0, 1));
    }

    public static Dictionary<VisualNodeKind, INodeExecutor> DefaultExecutors(IWanApiService wan, ILocalMediaService media, IVideoUpscaleService upscale, Func<string?> ffmpeg)
        => new()
        {
            [VisualNodeKind.Prompt] = new DelegateNodeExecutor(static (n, _, _, _) =>
            {
                var t = n.Text.Trim();
                if (t.Length == 0) throw new InvalidOperationException("프롬프트가 비었습니다.");
                return ValueTask.FromResult(new NodeValue(VisualPortType.Text, t, null));
            }),
            [VisualNodeKind.ControlNet] = new DelegateNodeExecutor(static (n, input, _, _) => ValueTask.FromResult(BuildControl(n, input))),
            [VisualNodeKind.WanGenerator] = new WanNodeExecutor(wan, media),
            [VisualNodeKind.Upscaler] = new UpscaleNodeExecutor(upscale),
            [VisualNodeKind.PostProcess] = new PostProcessNodeExecutor(ffmpeg),
            [VisualNodeKind.Output] = new DelegateNodeExecutor(static (n, input, _, _) =>
            {
                if (string.IsNullOrWhiteSpace(input.Path)) throw new InvalidOperationException("출력할 영상이 없습니다.");
                n.Status = "📦 " + Path.GetFileName(input.Path);
                return ValueTask.FromResult(input);
            }),
        };

    private sealed class WanNodeExecutor(IWanApiService wan, ILocalMediaService media) : INodeExecutor
    {
        public async ValueTask<NodeValue> ExecuteAsync(VisualNode node, NodeValue input, NodeRunContext ctx, CancellationToken ct)
        {
            if (!wan.Enabled) throw new InvalidOperationException("Wan API 키가 구성되지 않았습니다.");
            var req = new WanVideoRequest
            {
                Prompt = input.Text ?? "",
                ImagePath = input.ReferenceImage,
                NegativePrompt = string.IsNullOrWhiteSpace(node.Text) ? null : node.Text,
            };
            node.Status = "🎬 Wan 생성 중…";
            var gen = await wan.GenerateAsync(req, null, ct).ConfigureAwait(false);
            if (!gen.Ok || gen.VideoUrl is null) throw new InvalidOperationException(gen.Message);
            node.Status = "⬇️ 다운로드 중…";
            var dl = await media.DownloadAsync(gen.TaskId ?? node.Id, gen.VideoUrl, null, ct).ConfigureAwait(false);
            if (!dl.Ok || dl.LocalPath is null) throw new InvalidOperationException("영상 다운로드 실패");
            node.Status = "완료";
            return new NodeValue(VisualPortType.Video, input.Text, dl.LocalPath, input.Hint, input.ReferenceImage, input.Strength);
        }
    }

    private sealed class UpscaleNodeExecutor(IVideoUpscaleService upscale) : INodeExecutor
    {
        public async ValueTask<NodeValue> ExecuteAsync(VisualNode node, NodeValue input, NodeRunContext ctx, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(input.Path)) throw new InvalidOperationException("입력 영상이 없습니다.");
            if (!upscale.AnyAvailable) { node.Status = "업스케일 엔진 없음 → 원본 통과"; return input; }
            var engine = upscale.IsAvailable(UpscaleEngine.RealEsrgan) ? UpscaleEngine.RealEsrgan : UpscaleEngine.RealCugan;
            var output = Path.Combine(ctx.OutputRoot, node.Id + "_x" + node.Scale.ToString(CultureInfo.InvariantCulture) + ".mp4");
            var r = await upscale.UpscaleAsync(input.Path, output, Math.Clamp(node.Scale, 2, 4), engine, null, null, ct).ConfigureAwait(false);
            if (!r.Ok || r.OutputPath is null) throw new InvalidOperationException(r.Message);
            node.Status = "완료";
            return input with { Path = r.OutputPath };
        }
    }

    private sealed class PostProcessNodeExecutor(Func<string?> ffmpeg) : INodeExecutor
    {
        public async ValueTask<NodeValue> ExecuteAsync(VisualNode node, NodeValue input, NodeRunContext ctx, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(input.Path)) throw new InvalidOperationException("입력 영상이 없습니다.");
            var exe = ffmpeg();
            if (exe is null) { node.Status = "ffmpeg 없음 → 원본 통과"; return input; }
            var output = Path.Combine(ctx.OutputRoot, node.Id + "_post.mp4");
            var r = await ProcessRunner.RunAsync(exe, new List<string>
            {
                "-y", "-hide_banner", "-nostdin", "-i", input.Path, "-vf", PostFilter(node.Text) + ",format=yuv420p",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-c:a", "copy", "-movflags", "+faststart", output,
            }, ct: ct).ConfigureAwait(false);
            if (!r.Ok || !File.Exists(output)) throw new InvalidOperationException("후처리 실패");
            node.Status = "완료";
            return input with { Path = output };
        }
    }
}

/// <summary>v16.21.0 원격 그래프 실행기 계약(클라우드 GPU 워커 릴레이).</summary>
public interface IRemoteGraphRunner
{
    bool IsConfigured { get; }
    Task<GraphRunResult> RunAsync(VisualNodeGraph graph, string outputRoot, CancellationToken ct);
}
