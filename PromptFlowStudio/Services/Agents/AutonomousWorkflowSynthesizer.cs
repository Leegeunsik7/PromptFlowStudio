using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Agents;

/// <summary>[v9.4.1] 합성 노드 종류(실행 단계 · <c>NodeGraphEngine</c> 노드 타입과 1:1 매핑 가능한 의미 단위).</summary>
public enum SynthNodeKind
{
    Prompt = 0,
    Enhance = 1,
    Tts = 2,
    ImageToVideo = 3,
    TextToVideo = 4,
    LipSync = 5,
    Upscale = 6,
    Subtitle = 7,
    AudioMix = 8,
    Concat = 9,
    Watermark = 10,
    Export = 11,
}

/// <summary>[v9.4.1] 합성 노드(불변).</summary>
public sealed record SynthNode(string Id, SynthNodeKind Kind, string Label, int Cut);

/// <summary>[v9.4.1] 합성 간선(From → To · 불변).</summary>
public sealed record SynthEdge(string From, string To);

/// <summary>[v9.4.1] 의도 파싱 결과(추출 단서 · 컷 수 · 신뢰도).</summary>
public sealed record SynthIntent(
    string Source,
    int Cuts,
    IReadOnlyList<SynthNodeKind> Stages,
    IReadOnlyList<string> Cues,
    double Confidence);

/// <summary>
/// [v9.4.1] 합성 워크플로우(불변).
/// ⚠ <see cref="Canonical"/> 에는 노드/간선 목록뿐 아니라 <b>노드·간선·레이어·제거 간선 개수</b>까지 바인딩한다(v9 봉인 헌장).
/// </summary>
public sealed record SynthWorkflow(
    long Revision,
    SynthIntent Intent,
    IReadOnlyList<SynthNode> Nodes,
    IReadOnlyList<SynthEdge> Edges,
    IReadOnlyList<string> Order,
    IReadOnlyList<IReadOnlyList<string>> Layers,
    int RemovedEdges,
    int Rounds,
    bool Valid,
    IReadOnlyList<string> Issues,
    string Canonical,
    string Digest,
    double ElapsedMs);

/// <summary>
/// [PromptFlow Studio v9.4.1] 자율 워크플로우 합성 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 자연어 의도(한/영 키워드 · 컷 수 · 체인 단서)를 파싱해 단계 집합을 도출하고, 컷별 DAG 노드 그래프를 합성한다.
///   ▸ Identity Chaining: I2V 컷 n 은 컷 n-1 영상 출력을 앵커로 받는다(프로젝트 I2V 체인 규약).
///   ▸ Kahn 위상 정렬 → 잔여 노드가 있으면 순환으로 판정, 역방향(생성 순서 역행) 간선을 제거하며 최대 8라운드 수렴한다.
///   ▸ 검증: 고립/미도달 노드, 미등록 간선 끝점, 자기 루프, 중복 간선, Export 단일성 점검.
///   ▸ 멱등: 동일 의도 문자열 재합성은 동일 <see cref="SynthWorkflow.Digest"/> 를 낸다(Revision 제외 정규화).
///   ▸ 제5헌장: 빈/무의미 입력은 기본 파이프라인(Prompt→T2V→Export)으로 강등한다.
/// </summary>
public sealed class AutonomousWorkflowSynthesizer
{
    private const int MaxCuts = 8;
    private const int MaxRounds = 8;

    private static readonly (SynthNodeKind Kind, string[] Keys)[] Lexicon =
    {
        (SynthNodeKind.Enhance, new[] { "부스트", "boost", "enhance", "고급", "시네마틱", "cinematic" }),
        (SynthNodeKind.Tts, new[] { "tts", "나레이션", "narration", "음성", "보이스", "voice", "더빙" }),
        (SynthNodeKind.ImageToVideo, new[] { "i2v", "이미지", "image", "앵커", "anchor", "정체성", "identity" }),
        (SynthNodeKind.LipSync, new[] { "립싱크", "lipsync", "lip-sync", "lip sync", "입모양" }),
        (SynthNodeKind.Upscale, new[] { "업스케일", "upscale", "4k", "고해상도", "esrgan" }),
        (SynthNodeKind.Subtitle, new[] { "자막", "subtitle", "caption", "캡션" }),
        (SynthNodeKind.AudioMix, new[] { "bgm", "배경음", "음악", "music", "믹스", "mix", "sfx" }),
        (SynthNodeKind.Watermark, new[] { "워터마크", "watermark", "c2pa", "출처" }),
    };

    private readonly object _gate = new();
    private long _revision;
    private int _cyclesBroken;

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>마지막 합성 결과(없으면 null).</summary>
    public SynthWorkflow? LastWorkflow { get; private set; }

    /// <summary>누적 합성 횟수.</summary>
    public long SynthesisCount => Interlocked.Read(ref _revision);

    /// <summary>누적 순환 제거 간선 수.</summary>
    public int CyclesBroken => Volatile.Read(ref _cyclesBroken);

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>자연어 의도 → 단계 집합 파싱(BCL 키워드 휴리스틱).</summary>
    public static SynthIntent ParseIntent(string? text)
    {
        var src = (text ?? string.Empty).Trim();
        var lower = src.ToLowerInvariant();
        var cues = new List<string>();
        var stages = new List<SynthNodeKind> { SynthNodeKind.Prompt };

        foreach (var (kind, keys) in Lexicon)
        {
            var hit = keys.FirstOrDefault(k => lower.Contains(k, StringComparison.Ordinal));
            if (hit is null) continue;
            stages.Add(kind);
            cues.Add(hit);
        }

        // 립싱크는 음성 트랙을 전제한다 — TTS 자동 보강.
        if (stages.Contains(SynthNodeKind.LipSync) && !stages.Contains(SynthNodeKind.Tts))
        {
            stages.Add(SynthNodeKind.Tts);
            cues.Add("(lipsync→tts)");
        }

        var cuts = ParseCuts(lower);
        stages.Add(stages.Contains(SynthNodeKind.ImageToVideo) || cuts > 1 ? SynthNodeKind.ImageToVideo : SynthNodeKind.TextToVideo);
        if (cuts > 1) stages.Add(SynthNodeKind.Concat);
        stages.Add(SynthNodeKind.Export);

        var distinct = stages.Distinct().OrderBy(k => (int)k).ToArray();
        if (distinct.Contains(SynthNodeKind.ImageToVideo)) distinct = distinct.Where(k => k != SynthNodeKind.TextToVideo).ToArray();

        var confidence = src.Length == 0 ? 0d : Math.Clamp(0.35d + cues.Count * 0.12d + (cuts > 1 ? 0.1d : 0d), 0d, 1d);
        return new SynthIntent(src, cuts, distinct, cues, confidence);
    }

    /// <summary>의도 문자열로부터 DAG 워크플로우를 합성·검증한다.</summary>
    public SynthWorkflow Synthesize(string? intentText)
    {
        var sw = Stopwatch.StartNew();
        var intent = ParseIntent(intentText);
        var (nodes, edges) = BuildGraph(intent);
        var result = Converge(intent, nodes, edges, sw);
        Publish(result);
        return result;
    }

    /// <summary>
    /// 외부 편집 그래프(순환 포함 가능)를 수렴·검증한다. 역행 간선(노드 목록 순서 기준)을 우선 제거한다.
    /// </summary>
    public SynthWorkflow Validate(SynthIntent intent, IReadOnlyList<SynthNode> nodes, IReadOnlyList<SynthEdge> edges)
    {
        var sw = Stopwatch.StartNew();
        var result = Converge(intent ?? ParseIntent(null), nodes ?? Array.Empty<SynthNode>(), edges ?? Array.Empty<SynthEdge>(), sw);
        Publish(result);
        return result;
    }

    /// <summary>순환 주입 프로브 — 마지막 워크플로우에 Export→Prompt 역행 간선을 추가해 수렴을 실측한다.</summary>
    public SynthWorkflow? InjectCycleProbe()
    {
        var last = LastWorkflow;
        if (last is null || last.Nodes.Count < 2) return null;
        var edges = last.Edges.ToList();
        edges.Add(new SynthEdge(last.Nodes[^1].Id, last.Nodes[0].Id));
        return Validate(last.Intent, last.Nodes, edges);
    }

    /// <summary>워크플로우 정규화를 재계산해 지문을 상수시간 대조한다(노드/간선/집계 위조 검출).</summary>
    public static bool VerifyDigest(SynthWorkflow? wf)
    {
        if (wf is null) return false;
        var canonical = BuildCanonical(wf.Nodes, wf.Edges, wf.Layers, wf.RemovedEdges);
        var digest = Hash(canonical);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(digest), Encoding.UTF8.GetBytes(wf.Digest ?? string.Empty))
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(canonical), Encoding.UTF8.GetBytes(wf.Canonical ?? string.Empty));
    }

    // ── 그래프 합성 ──

    private static (List<SynthNode> Nodes, List<SynthEdge> Edges) BuildGraph(SynthIntent intent)
    {
        var nodes = new List<SynthNode>();
        var edges = new List<SynthEdge>();
        bool Has(SynthNodeKind k) => intent.Stages.Contains(k);

        SynthNode Add(SynthNodeKind kind, int cut, string label)
        {
            var node = new SynthNode(cut < 0 ? Id(kind) : Id(kind) + "-" + cut.ToString(CultureInfo.InvariantCulture), kind, label, cut);
            nodes.Add(node);
            return node;
        }

        var prompt = Add(SynthNodeKind.Prompt, -1, "프롬프트");
        var head = prompt;
        if (Has(SynthNodeKind.Enhance))
        {
            var enh = Add(SynthNodeKind.Enhance, -1, "AI 부스팅");
            edges.Add(new SynthEdge(head.Id, enh.Id));
            head = enh;
        }

        SynthNode? tts = null;
        if (Has(SynthNodeKind.Tts))
        {
            tts = Add(SynthNodeKind.Tts, -1, "TTS 나레이션");
            edges.Add(new SynthEdge(head.Id, tts.Id));
        }

        var videoKind = Has(SynthNodeKind.ImageToVideo) ? SynthNodeKind.ImageToVideo : SynthNodeKind.TextToVideo;
        var cutOuts = new List<SynthNode>();
        SynthNode? prevVideo = null;

        for (var c = 1; c <= intent.Cuts; c++)
        {
            var video = Add(videoKind, c, (videoKind == SynthNodeKind.ImageToVideo ? "I2V" : "T2V") + " 컷" + c.ToString(CultureInfo.InvariantCulture));
            edges.Add(new SynthEdge(head.Id, video.Id));
            if (prevVideo is not null && videoKind == SynthNodeKind.ImageToVideo)
                edges.Add(new SynthEdge(prevVideo.Id, video.Id));   // Identity Chaining 앵커
            prevVideo = video;

            var tail = video;
            if (Has(SynthNodeKind.LipSync) && tts is not null)
            {
                var lip = Add(SynthNodeKind.LipSync, c, "립싱크 컷" + c.ToString(CultureInfo.InvariantCulture));
                edges.Add(new SynthEdge(tail.Id, lip.Id));
                edges.Add(new SynthEdge(tts.Id, lip.Id));
                tail = lip;
            }
            if (Has(SynthNodeKind.Upscale))
            {
                var up = Add(SynthNodeKind.Upscale, c, "업스케일 컷" + c.ToString(CultureInfo.InvariantCulture));
                edges.Add(new SynthEdge(tail.Id, up.Id));
                tail = up;
            }
            cutOuts.Add(tail);
        }

        var merged = cutOuts[0];
        if (cutOuts.Count > 1)
        {
            merged = Add(SynthNodeKind.Concat, -1, "Concat 롱폼 병합");
            foreach (var o in cutOuts) edges.Add(new SynthEdge(o.Id, merged.Id));
        }

        var final = merged;
        if (Has(SynthNodeKind.AudioMix) || (tts is not null && !Has(SynthNodeKind.LipSync)))
        {
            var mix = Add(SynthNodeKind.AudioMix, -1, "오디오 믹스");
            edges.Add(new SynthEdge(final.Id, mix.Id));
            if (tts is not null && !Has(SynthNodeKind.LipSync)) edges.Add(new SynthEdge(tts.Id, mix.Id));
            final = mix;
        }
        if (Has(SynthNodeKind.Subtitle))
        {
            var sub = Add(SynthNodeKind.Subtitle, -1, "자막");
            edges.Add(new SynthEdge(final.Id, sub.Id));
            if (tts is not null) edges.Add(new SynthEdge(tts.Id, sub.Id));
            final = sub;
        }
        if (Has(SynthNodeKind.Watermark))
        {
            var wm = Add(SynthNodeKind.Watermark, -1, "C2PA 워터마크");
            edges.Add(new SynthEdge(final.Id, wm.Id));
            final = wm;
        }

        var export = Add(SynthNodeKind.Export, -1, "내보내기");
        edges.Add(new SynthEdge(final.Id, export.Id));
        return (nodes, edges);
    }

    // ── Kahn 수렴 & 검증 ──

    private SynthWorkflow Converge(SynthIntent intent, IReadOnlyList<SynthNode> nodes, IReadOnlyList<SynthEdge> input, Stopwatch sw)
    {
        var issues = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!index.TryAdd(nodes[i].Id, i)) issues.Add("중복 노드 " + nodes[i].Id);
        }

        // 정화: 미등록 끝점 · 자기 루프 · 중복 간선 제거.
        var seen = new HashSet<(string, string)>();
        var edges = new List<SynthEdge>();
        foreach (var e in input)
        {
            if (!index.ContainsKey(e.From) || !index.ContainsKey(e.To)) { issues.Add("미등록 끝점 " + e.From + "→" + e.To); continue; }
            if (e.From == e.To) { issues.Add("자기 루프 " + e.From); continue; }
            if (!seen.Add((e.From, e.To))) continue;
            edges.Add(e);
        }

        var removed = 0;
        var rounds = 0;
        List<string> order;
        List<IReadOnlyList<string>> layers;

        while (true)
        {
            rounds++;
            (order, layers) = Kahn(nodes, edges);
            if (order.Count == nodes.Count || rounds >= MaxRounds) break;

            // 잔여(순환) 노드 사이의 역행 간선을 제거 — 생성 순서를 신뢰 기준으로 삼는다.
            var resolved = new HashSet<string>(order, StringComparer.Ordinal);
            var back = edges
                .Where(e => !resolved.Contains(e.From) && !resolved.Contains(e.To) && index[e.From] >= index[e.To])
                .ToList();
            if (back.Count == 0)
            {
                // 역행 간선이 없는 순환(외부 편집) — 잔여 간선 중 도착 인덱스가 가장 작은 것 1개 제거.
                var victim = edges
                    .Where(e => !resolved.Contains(e.From) && !resolved.Contains(e.To))
                    .OrderBy(e => index[e.To]).FirstOrDefault();
                if (victim is null) break;
                back.Add(victim);
            }
            foreach (var b in back)
            {
                edges.Remove(b);
                issues.Add("순환 차단 " + b.From + "→" + b.To);
            }
            removed += back.Count;
        }

        if (order.Count != nodes.Count) issues.Add("수렴 실패(잔여 " + (nodes.Count - order.Count).ToString(CultureInfo.InvariantCulture) + ")");

        var exports = nodes.Count(n => n.Kind == SynthNodeKind.Export);
        if (exports != 1) issues.Add("Export 노드 " + exports.ToString(CultureInfo.InvariantCulture) + "개");

        // 도달성: 루트(진입차수 0)에서 모든 노드에 도달해야 하고, 모든 노드는 Export 로 흘러야 한다.
        var reach = Reach(nodes.Where(n => !edges.Any(e => e.To == n.Id)).Select(n => n.Id), edges, forward: true);
        var toExport = Reach(nodes.Where(n => n.Kind == SynthNodeKind.Export).Select(n => n.Id), edges, forward: false);
        foreach (var n in nodes)
        {
            if (!reach.Contains(n.Id)) issues.Add("미도달 " + n.Id);
            else if (exports == 1 && !toExport.Contains(n.Id)) issues.Add("고립(출력 미연결) " + n.Id);
        }

        var valid = order.Count == nodes.Count && exports == 1 && issues.All(i => !i.StartsWith("미", StringComparison.Ordinal) && !i.StartsWith("고립", StringComparison.Ordinal) && !i.StartsWith("중복", StringComparison.Ordinal));
        var canonical = BuildCanonical(nodes, edges, layers, removed);
        sw.Stop();

        if (removed > 0) Interlocked.Add(ref _cyclesBroken, removed);
        return new SynthWorkflow(Interlocked.Increment(ref _revision), intent, nodes.ToArray(), edges.ToArray(), order, layers,
            removed, rounds, valid, issues, canonical, Hash(canonical), sw.Elapsed.TotalMilliseconds);
    }

    private static (List<string> Order, List<IReadOnlyList<string>> Layers) Kahn(IReadOnlyList<SynthNode> nodes, List<SynthEdge> edges)
    {
        var indeg = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        var adj = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            adj[e.From].Add(e.To);
            indeg[e.To]++;
        }

        var order = new List<string>(nodes.Count);
        var layers = new List<IReadOnlyList<string>>();
        var frontier = nodes.Where(n => indeg[n.Id] == 0).Select(n => n.Id).ToList();
        while (frontier.Count > 0)
        {
            layers.Add(frontier.ToArray());
            order.AddRange(frontier);
            var next = new List<string>();
            foreach (var u in frontier)
                foreach (var v in adj[u])
                    if (--indeg[v] == 0) next.Add(v);
            frontier = next;
        }
        return (order, layers);
    }

    private static HashSet<string> Reach(IEnumerable<string> seeds, List<SynthEdge> edges, bool forward)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(seeds);
        while (stack.Count > 0)
        {
            var u = stack.Pop();
            if (!set.Add(u)) continue;
            foreach (var e in edges)
            {
                if (forward && e.From == u) stack.Push(e.To);
                else if (!forward && e.To == u) stack.Push(e.From);
            }
        }
        return set;
    }

    private static string BuildCanonical(IReadOnlyList<SynthNode> nodes, IReadOnlyList<SynthEdge> edges, IReadOnlyList<IReadOnlyList<string>> layers, int removed)
    {
        var sb = new StringBuilder(512);
        sb.Append("wf|v9.4.1|nodes=").Append(nodes.Count.ToString(CultureInfo.InvariantCulture))
          .Append("|edges=").Append(edges.Count.ToString(CultureInfo.InvariantCulture))
          .Append("|layers=").Append(layers.Count.ToString(CultureInfo.InvariantCulture))
          .Append("|removed=").Append(removed.ToString(CultureInfo.InvariantCulture));
        foreach (var n in nodes) sb.Append("|n:").Append(n.Id).Append(':').Append((int)n.Kind);
        foreach (var e in edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
            sb.Append("|e:").Append(e.From).Append('>').Append(e.To);
        return sb.ToString();
    }

    private void Publish(SynthWorkflow wf)
    {
        lock (_gate)
        {
            LastWorkflow = wf;
            LastAction = string.Create(CultureInfo.InvariantCulture,
                $"rev {wf.Revision} · {wf.Nodes.Count}노드 {wf.Edges.Count}간선 · {wf.Layers.Count}레이어 · 순환차단 {wf.RemovedEdges} · {(wf.Valid ? "유효" : "무효")} · {wf.ElapsedMs:0.###}ms");
        }
        OnChange?.Invoke();
    }

    private static int ParseCuts(string lower)
    {
        // "3컷", "4 cuts", "5개 장면", "3 scenes" 등 숫자 단서.
        for (var i = 0; i < lower.Length; i++)
        {
            if (!char.IsDigit(lower[i])) continue;
            var j = i;
            while (j < lower.Length && char.IsDigit(lower[j])) j++;
            var rest = lower.AsSpan(j).TrimStart();
            if (rest.StartsWith("컷") || rest.StartsWith("cut") || rest.StartsWith("개 장면") || rest.StartsWith("장면") || rest.StartsWith("scene") || rest.StartsWith("shot"))
            {
                if (int.TryParse(lower.AsSpan(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return Math.Clamp(n, 1, MaxCuts);
            }
            i = j;
        }
        return lower.Contains("롱폼", StringComparison.Ordinal) || lower.Contains("longform", StringComparison.Ordinal) ? 4 : 1;
    }

    private static string Id(SynthNodeKind kind) => kind switch
    {
        SynthNodeKind.ImageToVideo => "i2v",
        SynthNodeKind.TextToVideo => "t2v",
        SynthNodeKind.LipSync => "lipsync",
        SynthNodeKind.AudioMix => "mix",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
