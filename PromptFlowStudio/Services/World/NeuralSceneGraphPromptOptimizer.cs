using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.World;

public enum SceneEntityKind : byte { Prop = 0, Support, Character, Vehicle }
public enum SceneRelation : byte { On = 0, Under, LeftOf, RightOf, NextTo, InFrontOf, Behind }
public enum SceneAction : byte { Fall = 0, Bounce, Roll, Throw, Collide, Push, Rest, Drift }
public enum SceneIssueSeverity : byte { Info = 0, Warning, Error }

/// <summary>4D 장면 그래프 엔티티(인스턴스 1개).</summary>
public sealed class SceneEntity
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string English { get; set; } = "";
    public SceneEntityKind Kind { get; set; }
    public WorldShape Shape { get; set; }
    public float Hx { get; set; }
    public float Hy { get; set; }
    public float Hz { get; set; }
    public float Mass { get; set; }
    public float Rest { get; set; }
    public float Fric { get; set; }
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public string? ColorKo { get; set; }
    public string? ColorEn { get; set; }
    public string DefaultColorEn { get; set; } = "";
    public string? MaterialEn { get; set; }
    public string? SizeEn { get; set; }
    public int Group { get; set; }
}

public readonly record struct SceneEdge(int From, SceneRelation Relation, int To);

public sealed record SceneEvent(int Entity, SceneAction Action, float Start, bool ExplicitTime, int Other);

public sealed record SceneIssue(SceneIssueSeverity Severity, string Code, string Message, bool AutoFixed);

/// <summary>프롬프트에서 파싱된 4D 장면 그래프(공간 관계 · 시간 이벤트 · 카메라 · 조명 · 환경).</summary>
public sealed class SceneGraph
{
    public string Prompt { get; init; } = "";
    public List<SceneEntity> Entities { get; } = new();
    public List<SceneEdge> Edges { get; } = new();
    public List<SceneEvent> Events { get; } = new();
    public List<SceneIssue> Issues { get; } = new();
    public List<string> Styles { get; } = new();
    public CameraMode Camera { get; set; }
    public bool CameraExplicit { get; set; }
    public int CameraTarget { get; set; } = -1;
    public bool CameraSlow { get; set; }
    public bool Handheld { get; set; }
    public float Fov { get; set; } = 45f;
    public bool FovExplicit { get; set; }
    public LightPreset Light { get; set; }
    public bool LightExplicit { get; set; }
    public float Gravity { get; set; } = -9.81f;
    public string GravityEn { get; set; } = "realistic Earth gravity (9.81 m/s²)";
    public bool GravityExplicit { get; set; }
    public float Reflectivity { get; set; } = 0.2f;
    public float FrictionScale { get; set; } = 1f;
    public float Damping { get; set; } = 0.02f;
    public float Duration { get; set; } = 5f;
    public float TimeScale { get; set; } = 1f;
    public string RelationLabel(SceneEdge e) => Entities[e.From].Label + " " + RelLabel(e.Relation) + " " + Entities[e.To].Label;
    public static string RelLabel(SceneRelation r) => r switch
    {
        SceneRelation.On => "⊤위", SceneRelation.Under => "⊥아래", SceneRelation.LeftOf => "◀왼쪽", SceneRelation.RightOf => "▶오른쪽",
        SceneRelation.NextTo => "↔옆", SceneRelation.InFrontOf => "▼앞", _ => "▲뒤",
    };
    public static string ActionLabel(SceneAction a) => a switch
    {
        SceneAction.Fall => "낙하", SceneAction.Bounce => "바운스", SceneAction.Roll => "구름", SceneAction.Throw => "투척", SceneAction.Collide => "충돌",
        SceneAction.Push => "밀기", SceneAction.Rest => "정지", _ => "부유 낙하",
    };
}

/// <summary>프롬프트 품질(0~1 · 휴리스틱 구체성 지표).</summary>
public sealed record PromptQuality(float Subject, float Relations, float Temporal, float Camera, float Lighting, float Physics, float Consistency, float Overall);

public sealed record PromptCandidate(string Variant, string Text, float Acceptance, float[] Features);

public sealed record OptimizedPrompt(string Refined, string Negative, string Variant, float Acceptance, PromptQuality Before, PromptQuality After, IReadOnlyList<PromptCandidate> Candidates);

/// <summary>컴파일 결과(그래프 엔티티 → 월드 객체 인덱스).</summary>
public sealed record CompiledWorld(int[] Map, CameraSpec Camera, float Duration, int Scheduled);

/// <summary>
/// [PromptFlow Studio v16.54.0] ▶ 4D 비주얼 장면 그래프 &amp; 신경망 프롬프트 옵티마이저.
///   ▸ 파서(한/영): 객체(색·재질·크기·개수 · 상호참조) · 공간 관계(위/아래/좌/우/옆/앞/뒤) · 시간 이벤트(낙하/바운스/구름/투척/충돌/밀기/정지 + N초 후)
///     · 카메라(오비트/달리/트래킹/부감/크레인/로우앵글 · 렌즈 mm/광각/망원 · 핸드헬드) · 조명 · 중력/환경 · 스타일.
///   ▸ 검증·자동 보정: 지지 순환 · 좌우 모순 · 다중 지지 · 불안정 적재 · 무중력 낙하 · 정지/운동 충돌 · 단독 충돌 · 조명/카메라/주체 누락.
///   ▸ 정제: 정규화된 영문 비디오 프롬프트(주체→관계→시간 이벤트→물리 제약→카메라→조명→스타일) + 네거티브. 3변형 후보를 단층 퍼셉트론
///     (로지스틱 · 온라인 SGD · 사용자 피드백 학습)으로 채택 확률 순위화. 품질 지표는 결정론적 휴리스틱(외부 LLM 미사용).
///   ▸ 컴파일: 관계 기반 레이아웃 솔버 → <see cref="AiGenerative4dWorldModelEngine"/> 강체/스케줄/카메라/조명/중력 결선.
/// </summary>
public sealed class NeuralSceneGraphPromptOptimizer
{
    private sealed record Noun(string[] Keys, string Ko, string En, SceneEntityKind Kind, WorldShape Shape, float Hx, float Hy, float Hz, float Mass, float Rest, float Fric, string ColorEn, float R, float G, float B);
    private sealed record Adj(string[] Keys, string Ko, string En, float R, float G, float B);

    private static readonly Noun[] Nouns =
    {
        new(new[] { "농구공", "basketball" }, "농구공", "basketball", SceneEntityKind.Prop, WorldShape.Sphere, 0.13f, 0, 0, 0.6f, 0.8f, 0.6f, "orange", 0.92f, 0.45f, 0.12f),
        new(new[] { "축구공", "soccer", "football" }, "축구공", "soccer ball", SceneEntityKind.Prop, WorldShape.Sphere, 0.12f, 0, 0, 0.43f, 0.7f, 0.5f, "white", 0.92f, 0.92f, 0.92f),
        new(new[] { "볼링공", "bowling" }, "볼링공", "bowling ball", SceneEntityKind.Prop, WorldShape.Sphere, 0.12f, 0, 0, 6.5f, 0.15f, 0.3f, "black", 0.1f, 0.1f, 0.14f),
        new(new[] { "공", "ball" }, "공", "ball", SceneEntityKind.Prop, WorldShape.Sphere, 0.15f, 0, 0, 0.5f, 0.7f, 0.5f, "red", 0.85f, 0.12f, 0.12f),
        new(new[] { "구슬", "marble" }, "구슬", "marble", SceneEntityKind.Prop, WorldShape.Sphere, 0.1f, 0, 0, 0.1f, 0.6f, 0.3f, "blue", 0.2f, 0.4f, 0.95f),
        new(new[] { "사과", "apple" }, "사과", "apple", SceneEntityKind.Prop, WorldShape.Sphere, 0.1f, 0, 0, 0.2f, 0.3f, 0.6f, "red", 0.8f, 0.1f, 0.1f),
        new(new[] { "돌", "바위", "rock", "stone" }, "돌", "stone", SceneEntityKind.Prop, WorldShape.Sphere, 0.18f, 0, 0, 4f, 0.1f, 0.8f, "gray", 0.5f, 0.5f, 0.52f),
        new(new[] { "상자", "박스", "큐브", "box", "crate", "cube" }, "상자", "box", SceneEntityKind.Prop, WorldShape.Box, 0.25f, 0.25f, 0.25f, 3f, 0.15f, 0.6f, "brown", 0.6f, 0.42f, 0.25f),
        new(new[] { "컵", "cup", "mug" }, "컵", "cup", SceneEntityKind.Prop, WorldShape.Box, 0.08f, 0.1f, 0.08f, 0.3f, 0.2f, 0.5f, "white", 0.9f, 0.9f, 0.88f),
        new(new[] { "테이블", "탁자", "책상", "table", "desk" }, "테이블", "table", SceneEntityKind.Support, WorldShape.Box, 0.8f, 0.38f, 0.5f, 0, 0.2f, 0.6f, "wooden", 0.55f, 0.38f, 0.22f),
        new(new[] { "선반", "받침대", "단상", "shelf", "platform", "pedestal" }, "받침대", "pedestal", SceneEntityKind.Support, WorldShape.Box, 0.45f, 0.55f, 0.45f, 0, 0.2f, 0.6f, "gray", 0.62f, 0.62f, 0.66f),
        new(new[] { "사람", "남자", "여자", "소년", "소녀", "아이", "person", "man", "woman", "boy", "girl", "child" }, "사람", "person", SceneEntityKind.Character, WorldShape.Box, 0.22f, 0.85f, 0.15f, 60f, 0.05f, 0.8f, "blue-clothed", 0.3f, 0.45f, 0.7f),
        new(new[] { "로봇", "robot" }, "로봇", "robot", SceneEntityKind.Character, WorldShape.Box, 0.3f, 0.7f, 0.25f, 80f, 0.1f, 0.6f, "silver", 0.75f, 0.77f, 0.8f),
        new(new[] { "자동차", "차", "car", "truck" }, "자동차", "car", SceneEntityKind.Vehicle, WorldShape.Box, 0.9f, 0.6f, 1.9f, 1200f, 0.1f, 0.7f, "red", 0.75f, 0.1f, 0.1f),
    };

    private static readonly Adj[] Colors =
    {
        new(new[] { "빨간", "빨강", "붉은", "red" }, "빨간", "red", 0.85f, 0.12f, 0.12f),
        new(new[] { "파란", "파랑", "푸른", "blue" }, "파란", "blue", 0.15f, 0.35f, 0.9f),
        new(new[] { "초록", "녹색", "green" }, "초록", "green", 0.15f, 0.7f, 0.25f),
        new(new[] { "노란", "노랑", "yellow" }, "노란", "yellow", 0.95f, 0.8f, 0.15f),
        new(new[] { "하얀", "흰", "white" }, "흰", "white", 0.92f, 0.92f, 0.92f),
        new(new[] { "검은", "까만", "black" }, "검은", "black", 0.08f, 0.08f, 0.09f),
        new(new[] { "주황", "orange" }, "주황", "orange", 0.95f, 0.5f, 0.1f),
        new(new[] { "보라", "purple" }, "보라", "purple", 0.55f, 0.25f, 0.8f),
        new(new[] { "분홍", "pink" }, "분홍", "pink", 0.95f, 0.5f, 0.7f),
        new(new[] { "금색", "황금", "gold", "golden" }, "금색", "gold", 0.9f, 0.72f, 0.2f),
        new(new[] { "은색", "silver" }, "은색", "silver", 0.75f, 0.77f, 0.8f),
        new(new[] { "회색", "gray", "grey" }, "회색", "gray", 0.5f, 0.5f, 0.52f),
    };

    private static readonly string[] Particles = { "", "이", "가", "은", "는", "을", "를", "의", "에", "에서", "도", "와", "과", "로", "으로", "들", "들이", "들은", "들을", "만", "처럼", "까지", "부터" };
    private static readonly string[] ClauseBreakers = { " 그리고 ", " 그 후 ", " 그 다음 ", " 다음에 ", " 이후 ", " then ", " and then ", " after that ", " afterwards " };
    private static readonly string[] SequenceMarkers = { "그 후", "그 다음", "다음에", "이후", "then", "after that", "afterwards" };

    private readonly float[] _w = { -0.5f, 2.5f, 2.0f, -2.2f, 0.25f, -0.2f, 0.2f, 0.3f };
    private readonly object _gate = new();
    private long _analyses, _optimizations, _issues, _fixes, _feedback, _accepted;

    public event Action? OnChange;

    public long Analyses => Interlocked.Read(ref _analyses);
    public long Optimizations => Interlocked.Read(ref _optimizations);
    public long IssuesFound => Interlocked.Read(ref _issues);
    public long AutoFixes => Interlocked.Read(ref _fixes);
    public long Feedbacks => Interlocked.Read(ref _feedback);
    public long Accepted => Interlocked.Read(ref _accepted);
    public string? LastAction { get; private set; }
    public SceneGraph? LastGraph { get; private set; }
    public OptimizedPrompt? LastPrompt { get; private set; }

    // ══════════════════════ 파싱 ══════════════════════

    /// <summary>프롬프트 → 원시 4D 장면 그래프(보정 전).</summary>
    public SceneGraph Parse(string prompt)
    {
        var g = new SceneGraph { Prompt = prompt ?? "" };
        var text = " " + g.Prompt.Replace('\n', '.').Replace('\r', ' ').ToLowerInvariant() + " ";
        foreach (var cb in ClauseBreakers) text = text.Replace(cb, " . " + cb.Trim() + " ", StringComparison.Ordinal);
        var clauses = text.Split(new[] { '.', ',', ';', '!', '?', '·' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = new List<List<int>>();
        var groupNoun = new List<Noun>();
        var groupColor = new List<string?>();
        var discourse = -1;
        var lastStart = -1f;
        foreach (var clause in clauses)
        {
            var tok = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) continue;
            ParseGlobal(g, clause, tok);
            var isCamera = IsCameraClause(clause);
            // ① 언급(mention) 수집
            var mTok = new List<int>(); var mGrp = new List<int>();
            for (var k = 0; k < tok.Length; k++)
            {
                var noun = MatchNoun(tok[k], out var plural);
                if (noun is null) continue;
                Adj? color = null; string? material = null; string? size = null; var another = false;
                for (var q = k - 1; q >= Math.Max(0, k - 3); q--)
                {
                    if (MatchNoun(tok[q], out _) is not null) break;
                    color ??= MatchAdj(tok[q]);
                    material ??= Material(tok[q]);
                    size ??= Size(tok[q]);
                    if (tok[q] is "또" or "다른" or "another") another = true;
                }
                var count = CountAround(tok, k);
                if (count == 0 && plural) count = 2;
                var colorKey = color?.En;
                var gi = -1;
                if (count == 0 && !another)
                    for (var x = groups.Count - 1; x >= 0; x--)
                        if (ReferenceEquals(groupNoun[x], noun) && (colorKey is null || groupColor[x] == colorKey)) { gi = x; break; }
                if (gi < 0)
                {
                    gi = groups.Count;
                    var members = new List<int>();
                    var n = Math.Clamp(count == 0 ? 1 : count, 1, 8);
                    for (var c = 0; c < n; c++) members.Add(AddEntity(g, noun, color, material, size, gi));
                    groups.Add(members); groupNoun.Add(noun); groupColor.Add(colorKey);
                }
                mTok.Add(k); mGrp.Add(gi);
            }
            if (isCamera) { ParseCamera(g, clause, tok, mGrp, groups); continue; }
            // ② 시간
            var start = -1f;
            for (var k = 0; k < tok.Length; k++)
            {
                var sec = Seconds(tok, k, out var unitAt);
                if (sec < 0) continue;
                var next = unitAt + 1 < tok.Length ? tok[unitAt + 1] : "";
                var self = tok[unitAt];
                var prevTok = k > 0 ? tok[k - 1] : "";
                if (self.Contains("동안", StringComparison.Ordinal) || next.StartsWith("동안", StringComparison.Ordinal) || prevTok == "for"
                    || next.StartsWith("영상", StringComparison.Ordinal) || next.StartsWith("짜리", StringComparison.Ordinal) || self.Contains("짜리", StringComparison.Ordinal)
                    || next is "video" or "clip" or "shot" || self.EndsWith("-second", StringComparison.Ordinal))
                { g.Duration = Math.Clamp(sec, 2f, 20f); continue; }
                start = sec;
            }
            var sequenced = false;
            foreach (var sm in SequenceMarkers) if (clause.StartsWith(sm, StringComparison.Ordinal)) sequenced = true;
            // ③ 관계
            for (var k = 0; k < tok.Length; k++)
            {
                var rel = MatchRelation(tok, k, out var korean);
                if (rel is null) continue;
                if (k > 0 && Seconds(tok, k - 1, out _) >= 0) continue;   // "2초 뒤에" = 시간
                int lm = -1, tr = -1;
                if (korean)
                {
                    for (var m = mTok.Count - 1; m >= 0; m--) if (mTok[m] < k && k - mTok[m] <= 2) { lm = mGrp[m]; break; }
                    for (var m = 0; m < mGrp.Count; m++) if (mGrp[m] != lm) { tr = mGrp[m]; break; }
                }
                else
                {
                    for (var m = mTok.Count - 1; m >= 0; m--) if (mTok[m] < k) { tr = mGrp[m]; break; }
                    for (var m = 0; m < mTok.Count; m++) if (mTok[m] > k && mTok[m] - k <= 4) { lm = mGrp[m]; break; }
                }
                if (lm < 0) continue;
                if (tr < 0 && discourse >= 0 && discourse != lm) tr = discourse;
                if (tr < 0 || tr == lm) continue;
                foreach (var a in groups[tr]) g.Edges.Add(new SceneEdge(a, rel.Value, groups[lm][0]));
            }
            // ④ 행동
            var actions = new List<SceneAction>();
            foreach (var t in tok) { var act = MatchAction(t); if (act is not null && !actions.Contains(act.Value)) actions.Add(act.Value); }
            if (tok.Length > 0 && (clause.Contains("회전", StringComparison.Ordinal) || clause.Contains("spin", StringComparison.Ordinal)))
                g.Issues.Add(new SceneIssue(SceneIssueSeverity.Info, "SG-SPIN", "강체 자전(스핀)은 미지원 — 병진 운동으로 근사", false));
            var subject = -1; var other = -1; var agent = -1;
            for (var m = 0; m < mGrp.Count; m++)
            {
                var e = g.Entities[groups[mGrp[m]][0]];
                if (e.Kind == SceneEntityKind.Character && agent < 0) agent = mGrp[m];
            }
            var landmarks = new HashSet<int>();
            foreach (var ed in g.Edges) landmarks.Add(ed.To);
            for (var m = 0; m < mGrp.Count; m++)
            {
                var gi = mGrp[m];
                if (gi == agent && actions.Exists(a => a is SceneAction.Throw or SceneAction.Push)) continue;
                if (landmarks.Contains(groups[gi][0]) && g.Entities[groups[gi][0]].Kind == SceneEntityKind.Support) continue;
                if (subject < 0) subject = gi; else if (other < 0 && gi != subject) other = gi;
            }
            if (subject < 0 && mGrp.Count > 0 && agent < 0) subject = mGrp[0];
            if (subject < 0 && discourse >= 0) subject = discourse;
            if (other < 0 && agent >= 0 && agent != subject) other = agent;
            if (actions.Count > 0 && subject >= 0)
            {
                var explicitTime = start >= 0;
                var st = explicitTime ? start : sequenced && lastStart >= 0 ? lastStart + 1.5f : 0f;
                foreach (var a in actions)
                    foreach (var ent in groups[subject])
                        g.Events.Add(new SceneEvent(ent, a, st, explicitTime, other >= 0 ? groups[other][0] : -1));
                lastStart = st;
            }
            if (subject >= 0) discourse = subject;
        }
        Interlocked.Increment(ref _analyses);
        return g;
    }

    private static int AddEntity(SceneGraph g, Noun n, Adj? color, string? material, string? size, int group)
    {
        var scale = size == "large" ? 1.6f : size == "small" ? 0.6f : 1f;
        var idx = 1;
        foreach (var e in g.Entities) if (e.English == n.En) idx++;
        var e2 = new SceneEntity
        {
            Id = n.En.Replace(" ", "", StringComparison.Ordinal) + idx.ToString(CultureInfo.InvariantCulture), Label = (color is null ? "" : color.Ko + " ") + n.Ko + (idx > 1 ? "#" + idx.ToString(CultureInfo.InvariantCulture) : ""),
            English = n.En, Kind = n.Kind, Shape = n.Shape, Hx = n.Hx * scale, Hy = (n.Hy > 0 ? n.Hy : n.Hx) * scale, Hz = (n.Hz > 0 ? n.Hz : n.Hx) * scale,
            Mass = n.Mass * scale * scale * scale, Rest = n.Rest, Fric = n.Fric, R = color?.R ?? n.R, G = color?.G ?? n.G, B = color?.B ?? n.B,
            ColorKo = color?.Ko, ColorEn = color?.En, DefaultColorEn = n.ColorEn, MaterialEn = material, SizeEn = size, Group = group,
        };
        if (material == "rubber") e2.Rest = 0.85f; else if (material == "metal") { e2.Rest = 0.4f; e2.Fric = 0.35f; } else if (material == "glass") { e2.Rest = 0.5f; e2.Fric = 0.2f; } else if (material == "wooden") { e2.Rest = 0.3f; e2.Fric = 0.6f; }
        g.Entities.Add(e2);
        return g.Entities.Count - 1;
    }

    private static Noun? MatchNoun(string t, out bool plural)
    {
        plural = false;
        Noun? best = null; var bestLen = 0;
        foreach (var n in Nouns)
            foreach (var k in n.Keys)
            {
                if (k.Length <= bestLen) continue;
                bool hit;
                if (IsAscii(k))
                {
                    hit = t == k || t == k + "s" || t == k + "es" || (k.Length >= 4 && t.StartsWith(k, StringComparison.Ordinal) && t.Length <= k.Length + 3);
                    if (hit && t.Length > k.Length && t[^1] == 's') plural = true;
                }
                else if (k.Length == 1) hit = t.StartsWith(k, StringComparison.Ordinal) && IsParticle(t[1..]);
                else hit = t.StartsWith(k, StringComparison.Ordinal) && (IsParticle(t[k.Length..]) || t.Length <= k.Length + 3);
                if (hit) { best = n; bestLen = k.Length; }
            }
        if (best is not null && t.Contains("들", StringComparison.Ordinal) && !IsAscii(t)) plural = true;
        return best;
    }

    private static bool IsAscii(string s) { foreach (var c in s) if (c > 127) return false; return true; }

    private static bool IsParticle(string rest) { foreach (var p in Particles) if (rest == p) return true; return false; }

    private static Adj? MatchAdj(string t)
    {
        foreach (var a in Colors) foreach (var k in a.Keys) if (IsAscii(k) ? t == k : t.StartsWith(k, StringComparison.Ordinal)) return a;
        return null;
    }

    private static string? Material(string t) =>
        t.StartsWith("나무", StringComparison.Ordinal) || t.StartsWith("원목", StringComparison.Ordinal) || t is "wooden" or "wood" ? "wooden"
        : t.StartsWith("금속", StringComparison.Ordinal) || t.StartsWith("쇠", StringComparison.Ordinal) || t.StartsWith("강철", StringComparison.Ordinal) || t is "metal" or "metallic" or "steel" ? "metal"
        : t.StartsWith("유리", StringComparison.Ordinal) || t == "glass" ? "glass"
        : t.StartsWith("고무", StringComparison.Ordinal) || t == "rubber" ? "rubber" : null;

    private static string? Size(string t) =>
        t.StartsWith("큰", StringComparison.Ordinal) || t.StartsWith("커다란", StringComparison.Ordinal) || t.StartsWith("거대한", StringComparison.Ordinal) || t is "big" or "large" or "huge" ? "large"
        : t.StartsWith("작은", StringComparison.Ordinal) || t.StartsWith("조그만", StringComparison.Ordinal) || t is "small" or "tiny" or "little" ? "small" : null;

    /// <summary>명사 주변 개수: 뒤("공 두 개" · "공 2개") 또는 앞("두 개의 빨간 공" · "two red balls").</summary>
    private static int CountAround(string[] tok, int k)
    {
        if (k + 1 < tok.Length)
        {
            var c = Count(tok[k + 1]);
            if (c > 0 && (tok[k + 1].Contains('개') || (k + 2 < tok.Length && tok[k + 2].StartsWith('개')))) return c;
        }
        var q = k - 1;
        while (q >= 0 && q >= k - 3 && (MatchAdj(tok[q]) is not null || Material(tok[q]) is not null || Size(tok[q]) is not null)) q--;
        if (q < 0) return 0;
        if (tok[q] is "개의" or "개" && q > 0) q--;
        return Count(tok[q]);
    }

    private static int Count(string t)
    {
        var s = t;
        var gi = t.IndexOf('개');
        if (gi >= 0 && IsParticle(t[(gi + 1)..])) s = t[..gi];
        if (s.Length == 0) return 0;
        if (s.Length > 0 && s.Length <= 2 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) return Math.Min(n, 8);
        return s switch
        {
            "하나" or "한" or "one" or "a" => t == "a" ? 0 : 1,
            "두" or "둘" or "two" or "pair" => 2, "세" or "셋" or "three" => 3, "네" or "넷" or "four" => 4, "다섯" or "five" => 5, "여섯" or "six" => 6,
            _ => 0,
        };
    }

    /// <summary>tok[k] 에서 시작하는 시간(초) 표현. unitAt = 단위 토큰 위치.</summary>
    private static float Seconds(string[] tok, int k, out int unitAt)
    {
        unitAt = k;
        var t = tok[k];
        var i = 0;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
        if (i == 0 || !float.TryParse(t.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return -1;
        var suf = t[i..];
        if (suf.StartsWith("초", StringComparison.Ordinal) || suf is "s" or "sec" or "secs" or "-second" or "-sec") return v;
        if (suf.Length == 0 && k + 1 < tok.Length && (tok[k + 1].StartsWith("초", StringComparison.Ordinal) || tok[k + 1] is "s" or "sec" or "secs" or "second" or "seconds")) { unitAt = k + 1; return v; }
        return -1;
    }

    private static SceneRelation? MatchRelation(string[] tok, int k, out bool korean)
    {
        var t = tok[k];
        korean = !IsAscii(t);
        if (korean)
        {
            if (t.StartsWith("위", StringComparison.Ordinal) && IsParticle(t[1..])) return SceneRelation.On;
            if (t is "안에" or "안에서" or "속에" or "속에서") return SceneRelation.On;
            if ((t.StartsWith("아래", StringComparison.Ordinal) && IsParticle(t[2..])) || (t.StartsWith("밑", StringComparison.Ordinal) && IsParticle(t[1..]))) return SceneRelation.Under;
            if (t.StartsWith("왼쪽", StringComparison.Ordinal) || t.StartsWith("좌측", StringComparison.Ordinal)) return SceneRelation.LeftOf;
            if (t.StartsWith("오른쪽", StringComparison.Ordinal) || t.StartsWith("우측", StringComparison.Ordinal)) return SceneRelation.RightOf;
            if (t.StartsWith("옆", StringComparison.Ordinal) && IsParticle(t[1..])) return SceneRelation.NextTo;
            if (t.StartsWith("앞", StringComparison.Ordinal) && IsParticle(t[1..])) return SceneRelation.InFrontOf;
            if (t.StartsWith("뒤", StringComparison.Ordinal) && IsParticle(t[1..])) return SceneRelation.Behind;
            return null;
        }
        var nx = k + 1 < tok.Length ? tok[k + 1] : "";
        return t switch
        {
            "on" or "onto" or "atop" or "above" => SceneRelation.On,
            "under" or "below" or "beneath" or "underneath" => SceneRelation.Under,
            "left" when nx == "of" => SceneRelation.LeftOf,
            "right" when nx == "of" => SceneRelation.RightOf,
            "beside" => SceneRelation.NextTo,
            "next" when nx == "to" => SceneRelation.NextTo,
            "front" when nx == "of" => SceneRelation.InFrontOf,
            "behind" => SceneRelation.Behind,
            _ => null,
        };
    }

    private static SceneAction? MatchAction(string t)
    {
        static bool H(string t, string s) => t.Contains(s, StringComparison.Ordinal);
        if (H(t, "떨어") || H(t, "낙하") || H(t, "추락") || t.StartsWith("fall", StringComparison.Ordinal) || t.StartsWith("drop", StringComparison.Ordinal) || t == "fell") return SceneAction.Fall;
        if (H(t, "튀") || H(t, "튄") || H(t, "튕") || H(t, "바운") || t.StartsWith("bounc", StringComparison.Ordinal)) return SceneAction.Bounce;
        if (H(t, "굴러") || H(t, "구르") || H(t, "굴린") || H(t, "굴리") || H(t, "구른") || H(t, "굴러") || t.StartsWith("roll", StringComparison.Ordinal)) return SceneAction.Roll;
        if (H(t, "던지") || H(t, "던져") || H(t, "던진") || t.StartsWith("throw", StringComparison.Ordinal) || t.StartsWith("toss", StringComparison.Ordinal) || t is "thrown" or "threw") return SceneAction.Throw;
        if (H(t, "충돌") || H(t, "부딪") || t.StartsWith("collid", StringComparison.Ordinal) || t.StartsWith("crash", StringComparison.Ordinal) || t is "hit" or "hits" || t.StartsWith("smash", StringComparison.Ordinal)) return SceneAction.Collide;
        if (H(t, "밀어") || H(t, "민다") || H(t, "밀린") || H(t, "밀고") || H(t, "밀치") || t.StartsWith("push", StringComparison.Ordinal)) return SceneAction.Push;
        if (H(t, "멈") || H(t, "정지") || H(t, "가만히") || t is "still" or "stays" or "rests" or "resting" or "stops") return SceneAction.Rest;
        return null;
    }

    private static readonly string[] CameraWords = { "카메라", "camera", "샷", "shot", "앵글", "angle", "촬영", "시점", "렌즈", "lens", "오비트", "orbit", "달리", "dolly", "줌", "zoom", "트래킹", "tracking", "follow", "부감", "top-down", "overhead", "bird", "크레인", "crane", "핸드헬드", "handheld" };

    private static bool IsCameraClause(string c) { foreach (var w in CameraWords) if (c.Contains(w, StringComparison.Ordinal)) return true; return false; }

    private static void ParseCamera(SceneGraph g, string c, string[] tok, List<int> mGrp, List<List<int>> groups)
    {
        static bool H(string c, string s) => c.Contains(s, StringComparison.Ordinal);
        CameraMode? m = null;
        if (H(c, "오비트") || H(c, "orbit") || H(c, "회전") || H(c, "돌며") || H(c, "돌아") || H(c, "360") || H(c, "주위를") || H(c, "around")) m = CameraMode.Orbit;
        else if (H(c, "따라") || H(c, "추적") || H(c, "트래킹") || H(c, "tracking") || H(c, "follow")) m = CameraMode.Track;
        else if (H(c, "부감") || H(c, "내려다") || H(c, "top-down") || H(c, "overhead") || H(c, "bird") || H(c, "탑뷰")) m = CameraMode.Overhead;
        else if (H(c, "로우앵글") || H(c, "low-angle") || H(c, "low angle") || H(c, "올려다")) m = CameraMode.LowAngle;
        else if (H(c, "크레인") || H(c, "crane") || H(c, "상승")) m = CameraMode.Crane;
        else if (H(c, "달리") || H(c, "dolly") || H(c, "줌") || H(c, "zoom") || H(c, "다가") || H(c, "push in") || H(c, "push-in")) m = CameraMode.Dolly;
        else if (H(c, "고정") || H(c, "static") || H(c, "locked") || H(c, "정적")) m = CameraMode.Static;
        if (m is not null)
        {
            if (g.CameraExplicit && g.Camera != m.Value)
                g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-CAM-CONFLICT", "카메라 모드 중복(" + g.Camera + " vs " + m.Value + ") — 첫 지정 유지", true));
            else { g.Camera = m.Value; g.CameraExplicit = true; }
        }
        if (H(c, "천천히") || H(c, "slow")) g.CameraSlow = true;
        if (H(c, "핸드헬드") || H(c, "handheld") || H(c, "흔들")) { g.Handheld = true; g.CameraExplicit = true; }
        foreach (var t in tok)
        {
            var mm = t.IndexOf("mm", StringComparison.Ordinal);
            if (mm > 0 && float.TryParse(t.AsSpan(0, mm), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is >= 8 and <= 600)
            { g.Fov = 2f * MathF.Atan(12f / f) * (180f / MathF.PI); g.FovExplicit = true; }
        }
        if (H(c, "광각") || H(c, "wide")) { g.Fov = 70f; g.FovExplicit = true; }
        if (H(c, "망원") || H(c, "telephoto")) { g.Fov = 25f; g.FovExplicit = true; }
        if (g.Camera == CameraMode.Track && mGrp.Count > 0) g.CameraTarget = groups[mGrp[0]][0];
    }

    private static void ParseGlobal(SceneGraph g, string c, string[] tok)
    {
        static bool H(string c, string s) => c.Contains(s, StringComparison.Ordinal);
        foreach (var t in tok)
        {
            if (t.StartsWith("달", StringComparison.Ordinal) && IsParticle(t[1..]) && t.Length > 1 || t is "moon" or "lunar") { g.Gravity = -1.62f; g.GravityEn = "low lunar gravity (1.62 m/s²)"; g.GravityExplicit = true; }
            if (t.StartsWith("밤", StringComparison.Ordinal) && IsParticle(t[1..])) { g.Light = LightPreset.Night; g.LightExplicit = true; }
        }
        if (H(c, "무중력") || H(c, "zero gravity") || H(c, "zero-g") || H(c, "weightless")) { g.Gravity = 0f; g.GravityEn = "zero gravity, objects float freely"; g.GravityExplicit = true; }
        if (H(c, "화성") || H(c, "mars")) { g.Gravity = -3.71f; g.GravityEn = "Martian gravity (3.71 m/s²)"; g.GravityExplicit = true; }
        if (H(c, "목성") || H(c, "jupiter")) { g.Gravity = -24.79f; g.GravityEn = "crushing Jovian gravity (24.8 m/s²)"; g.GravityExplicit = true; }
        if (H(c, "지구 중력") || H(c, "earth gravity") || H(c, "현실적인 중력")) { g.Gravity = -9.81f; g.GravityExplicit = true; }
        if (H(c, "물속") || H(c, "수중") || H(c, "underwater")) { g.Damping = 1.5f; g.GravityExplicit = true; g.GravityEn += ", heavy underwater drag"; g.Styles.Add("underwater caustics"); }
        if (H(c, "거울") || H(c, "mirror") || H(c, "반사") || H(c, "reflective") || H(c, "젖은") || H(c, "wet") || H(c, "물웅덩이")) g.Reflectivity = 0.55f;
        if (H(c, "대리석")) g.Reflectivity = Math.Max(g.Reflectivity, 0.35f);
        if (H(c, "얼음") || H(c, " ice")) { g.Reflectivity = Math.Max(g.Reflectivity, 0.45f); g.FrictionScale = 0.1f; }
        if (H(c, "노을") || H(c, "석양") || H(c, "일몰") || H(c, "sunset") || H(c, "golden hour") || H(c, "황혼")) { g.Light = LightPreset.Sunset; g.LightExplicit = true; }
        else if (H(c, "정오") || H(c, "한낮") || H(c, "햇빛") || H(c, "sunny") || H(c, "noon") || H(c, "daylight") || H(c, "midday")) { g.Light = LightPreset.Noon; g.LightExplicit = true; }
        else if (H(c, "야간") || H(c, "달빛") || H(c, "night") || H(c, "moonlight")) { g.Light = LightPreset.Night; g.LightExplicit = true; }
        else if (H(c, "흐린") || H(c, "overcast") || H(c, "cloudy")) { g.Light = LightPreset.Overcast; g.LightExplicit = true; }
        else if (H(c, "스튜디오") || H(c, "studio") || H(c, "소프트박스")) { g.Light = LightPreset.Studio; g.LightExplicit = true; }
        if (H(c, "시네마틱") || H(c, "cinematic")) AddStyle(g, "cinematic");
        if (H(c, "실사") || H(c, "사실적") || H(c, "photoreal") || H(c, "realistic")) AddStyle(g, "photorealistic");
        if (H(c, "애니") || H(c, "anime")) AddStyle(g, "anime style");
        if (H(c, "픽사") || H(c, "pixar") || H(c, "3d 애니")) AddStyle(g, "stylized 3D animation");
        if (H(c, "미니어처") || H(c, "miniature") || H(c, "틸트시프트")) AddStyle(g, "tilt-shift miniature");
        if (H(c, "다큐") || H(c, "documentary")) AddStyle(g, "documentary");
        if (H(c, "필름") || H(c, "film grain")) AddStyle(g, "35mm film grain");
        if (H(c, "4k")) AddStyle(g, "4K");
        if (H(c, "8k")) AddStyle(g, "8K");
        if (H(c, "슬로모션") || H(c, "slow motion") || H(c, "slow-motion") || H(c, "슬로우")) { g.TimeScale = 0.5f; AddStyle(g, "slow motion"); }
    }

    private static void AddStyle(SceneGraph g, string s) { if (!g.Styles.Contains(s)) g.Styles.Add(s); }

    // ══════════════════════ 검증 · 자동 보정 ══════════════════════

    /// <summary>모순/불안정/누락 진단 + 자동 보정(그래프 변경).</summary>
    public void Repair(SceneGraph g)
    {
        var fixes = 0;
        if (g.Entities.Count == 0)
        {
            AddEntity(g, Nouns[3], null, null, null, 0);
            g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-NO-ENTITY", "인식된 피사체 없음 — 기본 구체 피사체 추가", true)); fixes++;
        }
        if (g.Entities.Count > AiGenerative4dWorldModelEngine.MaxObjects)
        {
            g.Entities.RemoveRange(AiGenerative4dWorldModelEngine.MaxObjects, g.Entities.Count - AiGenerative4dWorldModelEngine.MaxObjects);
            g.Edges.RemoveAll(e => e.From >= g.Entities.Count || e.To >= g.Entities.Count);
            g.Events.RemoveAll(e => e.Entity >= g.Entities.Count);
            g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-TOO-MANY", "객체 상한 초과 — 절단", true)); fixes++;
        }
        // 지지(On) 다중 · 순환
        var kept = new List<SceneEdge>();
        foreach (var e in g.Edges)
        {
            if (e.From == e.To) continue;
            if (kept.Contains(e)) continue;
            if (e.Relation == SceneRelation.On)
            {
                if (kept.Exists(k => k.Relation == SceneRelation.On && k.From == e.From))
                { g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-MULTI-SUPPORT", g.Entities[e.From].Label + " 다중 지지 — 첫 지지만 유지", true)); fixes++; continue; }
                if (Reaches(kept, e.To, e.From))
                { g.Issues.Add(new SceneIssue(SceneIssueSeverity.Error, "SG-CYCLE", "지지 순환: " + g.RelationLabel(e) + " — 후행 관계 제거", true)); fixes++; continue; }
            }
            var opp = Opposite(e.Relation);
            if (opp is not null && (kept.Exists(k => k.From == e.From && k.To == e.To && k.Relation == opp.Value) || kept.Exists(k => k.From == e.To && k.To == e.From && k.Relation == e.Relation)))
            { g.Issues.Add(new SceneIssue(SceneIssueSeverity.Error, "SG-SPATIAL-CONFLICT", "공간 모순: " + g.RelationLabel(e) + " — 후행 관계 제거", true)); fixes++; continue; }
            kept.Add(e);
        }
        g.Edges.Clear(); g.Edges.AddRange(kept);
        foreach (var e in g.Edges)
        {
            if (e.Relation != SceneRelation.On) continue;
            var a = g.Entities[e.From]; var b = g.Entities[e.To];
            if (b.Mass > 0 && a.Mass > b.Mass * 3f) g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-UNSTABLE", "불안정 적재: 무거운 " + a.Label + " 이(가) 가벼운 " + b.Label + " 위", false));
            else if (b.Shape == WorldShape.Sphere) g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-UNSTABLE", "구체 위 적재: " + a.Label + " → 굴러떨어질 수 있음", false));
        }
        // 이벤트
        for (var i = 0; i < g.Events.Count; i++)
        {
            var ev = g.Events[i];
            if (g.Gravity == 0f && ev.Action == SceneAction.Fall)
            { g.Events[i] = ev with { Action = SceneAction.Drift }; g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-ZEROG-FALL", "무중력에서 낙하 모순 — 느린 부유 하강으로 보정", true)); fixes++; }
            else if (ev.Action == SceneAction.Collide && ev.Other < 0)
            {
                var partner = -1;
                for (var k = 0; k < g.Entities.Count; k++) if (k != ev.Entity && g.Entities[k].Kind != SceneEntityKind.Support) { partner = k; break; }
                g.Events[i] = partner >= 0 ? ev with { Other = partner } : ev with { Action = SceneAction.Roll };
                g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-COLLIDE-ALONE", "충돌 상대 미지정 — " + (partner >= 0 ? g.Entities[partner].Label + " 배정" : "구름으로 대체"), true)); fixes++;
            }
        }
        var restDrop = g.Events.FindAll(r => r.Action == SceneAction.Rest && g.Events.Exists(m => m.Entity == r.Entity && m.Action != SceneAction.Rest && MathF.Abs(m.Start - r.Start) < 0.01f));
        foreach (var r in restDrop) { g.Events.Remove(r); g.Issues.Add(new SceneIssue(SceneIssueSeverity.Warning, "SG-REST-MOTION", g.Entities[r.Entity].Label + " 정지/운동 동시 지정 — 정지 제거", true)); fixes++; }
        if (!g.LightExplicit) { g.Issues.Add(new SceneIssue(SceneIssueSeverity.Info, "SG-NO-LIGHT", "조명 미지정 — 스튜디오 키라이트 기본값", true)); fixes++; }
        if (!g.CameraExplicit) { g.Issues.Add(new SceneIssue(SceneIssueSeverity.Info, "SG-NO-CAMERA", "카메라 미지정 — 고정 와이드 기본값", true)); fixes++; }
        if (g.Events.Count == 0) g.Issues.Add(new SceneIssue(SceneIssueSeverity.Info, "SG-NO-MOTION", "시간축 이벤트 없음 — 정적 장면(카메라 운동만)", false));
        if (g.Camera == CameraMode.Track && g.CameraTarget < 0)
            for (var k = 0; k < g.Entities.Count; k++) if (g.Entities[k].Kind != SceneEntityKind.Support) { g.CameraTarget = k; break; }
        Interlocked.Add(ref _issues, g.Issues.Count);
        Interlocked.Add(ref _fixes, fixes);
    }

    private static bool Reaches(List<SceneEdge> on, int from, int to)
    {
        var cur = from;
        for (var guard = 0; guard < 128; guard++)
        {
            if (cur == to) return true;
            var nx = -1;
            foreach (var e in on) if (e.Relation == SceneRelation.On && e.From == cur) { nx = e.To; break; }
            if (nx < 0) return false;
            cur = nx;
        }
        return true;
    }

    private static SceneRelation? Opposite(SceneRelation r) => r switch
    {
        SceneRelation.LeftOf => SceneRelation.RightOf, SceneRelation.RightOf => SceneRelation.LeftOf,
        SceneRelation.InFrontOf => SceneRelation.Behind, SceneRelation.Behind => SceneRelation.InFrontOf,
        SceneRelation.On => SceneRelation.Under, SceneRelation.Under => SceneRelation.On, _ => null,
    };

    // ══════════════════════ 품질 · 정제 · 퍼셉트론 ══════════════════════

    public static PromptQuality Measure(SceneGraph g, bool refined)
    {
        var props = 0; float subj = 0;
        foreach (var e in g.Entities) { props++; subj += (refined || e.ColorEn is not null ? 0.4f : 0f) + (e.MaterialEn is not null || e.SizeEn is not null ? 0.2f : 0f) + 0.4f; }
        subj = props == 0 ? 0 : Math.Min(1f, subj / props + (refined ? 0.1f : 0f));
        var rel = g.Entities.Count <= 1 ? 1f : Math.Min(1f, (float)g.Edges.Count / (g.Entities.Count - 1));
        if (refined) rel = Math.Min(1f, rel + 0.5f * (1f - rel));
        float temp;
        if (g.Events.Count == 0) temp = refined ? 0.5f : 0f;
        else { temp = 0; foreach (var ev in g.Events) temp += refined || ev.ExplicitTime ? 1f : 0.7f; temp /= g.Events.Count; }
        var cam = refined ? 1f : g.CameraExplicit ? (g.FovExplicit ? 1f : 0.8f) : 0f;
        var light = refined || g.LightExplicit ? 1f : 0f;
        var mat = false; foreach (var e in g.Entities) mat |= e.MaterialEn is not null;
        var phys = (g.Events.Count > 0 ? 0.4f : 0f) + (g.GravityExplicit ? 0.3f : 0f) + (mat ? 0.3f : 0f);
        if (refined) phys = Math.Min(1f, phys + 0.5f);
        int err = 0, warn = 0;
        foreach (var i in g.Issues)
        {
            if (refined && i.AutoFixed) continue;
            if (i.Severity == SceneIssueSeverity.Error) err++; else if (i.Severity == SceneIssueSeverity.Warning) warn++;
        }
        var cons = 1f - Math.Min(1f, 0.35f * err + 0.12f * warn);
        var overall = 0.15f * subj + 0.15f * rel + 0.15f * temp + 0.15f * cam + 0.1f * light + 0.15f * phys + 0.15f * cons;
        return new PromptQuality(subj, rel, temp, cam, light, phys, cons, overall);
    }

    /// <summary>원시 그래프 품질 측정 → 보정 → 정제 3변형 → 퍼셉트론 순위.</summary>
    public (SceneGraph Graph, OptimizedPrompt Prompt) AnalyzeAndOptimize(string prompt)
    {
        var g = Parse(prompt);
        var before = Measure(g, false);
        Repair(g);
        var after = Measure(g, true);
        var neg = Negative(g);
        var cands = new List<PromptCandidate>();
        foreach (var v in new[] { "detailed", "concise", "physics-first" })
        {
            var text = Compose(g, v);
            var f = Features(after.Overall, text, v, g);
            cands.Add(new PromptCandidate(v, text, Predict(f), f));
        }
        cands.Sort((a, b) => b.Acceptance.CompareTo(a.Acceptance));
        var best = cands[0];
        var op = new OptimizedPrompt(best.Text, neg, best.Variant, best.Acceptance, before, after, cands);
        LastGraph = g; LastPrompt = op;
        Interlocked.Increment(ref _optimizations);
        LastAction = "🧠 장면 그래프 · 객체 " + g.Entities.Count.ToString(CultureInfo.InvariantCulture) + " · 관계 " + g.Edges.Count.ToString(CultureInfo.InvariantCulture)
            + " · 품질 " + Pct(before.Overall) + "→" + Pct(after.Overall) + " · " + best.Variant;
        Raise();
        return (g, op);
    }

    private static string Pct(float v) => ((int)MathF.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    private static float[] Features(float quality, string text, string variant, SceneGraph g)
    {
        var len = text.Length / 600f;
        return new[] { 1f, quality, len, len * len, variant == "physics-first" ? 1f : 0f, variant == "concise" ? 1f : 0f, g.Styles.Count > 0 ? 1f : 0f, g.Edges.Count > 0 && variant != "concise" ? 1f : 0f };
    }

    /// <summary>채택 확률(로지스틱 단층 퍼셉트론 · 무할당).</summary>
    public float Predict(ReadOnlySpan<float> f)
    {
        float z = 0;
        lock (_gate) for (var i = 0; i < _w.Length && i < f.Length; i++) z += _w[i] * f[i];
        return 1f / (1f + MathF.Exp(-z));
    }

    /// <summary>사용자 피드백 온라인 학습(SGD · 로그 손실).</summary>
    public void Feedback(PromptCandidate c, bool accepted)
    {
        var p = Predict(c.Features);
        var y = accepted ? 1f : 0f;
        lock (_gate) for (var i = 0; i < _w.Length && i < c.Features.Length; i++) _w[i] += 0.3f * (y - p) * c.Features[i];
        Interlocked.Increment(ref _feedback);
        if (accepted) Interlocked.Increment(ref _accepted);
        LastAction = (accepted ? "👍 채택 학습 · " : "👎 거부 학습 · ") + c.Variant + " · p " + p.ToString("0.00", CultureInfo.InvariantCulture) + "→" + Predict(c.Features).ToString("0.00", CultureInfo.InvariantCulture);
        Raise();
    }

    public float[] Weights() { lock (_gate) return (float[])_w.Clone(); }

    private static string Describe(SceneGraph g, SceneEntity e, int count)
    {
        var sb = new StringBuilder();
        sb.Append(count > 1 ? NumberWord(count) + " " : "a ");
        if (e.SizeEn is not null) sb.Append(e.SizeEn).Append(' ');
        sb.Append(e.ColorEn ?? e.DefaultColorEn).Append(' ');
        if (e.MaterialEn is not null) sb.Append(e.MaterialEn).Append(' ');
        sb.Append(count > 1 ? Plural(e.English) : e.English);
        return sb.ToString();
    }

    private static string NumberWord(int n) => n switch { 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", _ => n.ToString(CultureInfo.InvariantCulture) };

    private static string Plural(string w) => w switch { "box" => "boxes", "person" => "people", "glass" => "glasses", _ => w + "s" };

    private static string The(SceneGraph g, int i) { var e = g.Entities[i]; return "the " + (e.ColorEn ?? e.DefaultColorEn) + " " + e.English; }

    private static string RelEn(SceneRelation r) => r switch
    {
        SceneRelation.On => "rests on", SceneRelation.Under => "sits under", SceneRelation.LeftOf => "is to the left of", SceneRelation.RightOf => "is to the right of",
        SceneRelation.NextTo => "is next to", SceneRelation.InFrontOf => "is in front of", _ => "is behind",
    };

    private static string ActEn(SceneGraph g, SceneEvent ev) => ev.Action switch
    {
        SceneAction.Fall => "falls under gravity and lands",
        SceneAction.Bounce => "bounces with progressively smaller rebounds",
        SceneAction.Roll => "rolls across the floor and slows by friction",
        SceneAction.Throw => "is thrown in a smooth ballistic arc" + (ev.Other >= 0 ? " by " + The(g, ev.Other) : ""),
        SceneAction.Collide => "collides with " + (ev.Other >= 0 ? The(g, ev.Other) : "another object") + ", momentum transfers realistically",
        SceneAction.Push => "is pushed and slides" + (ev.Other >= 0 ? " by " + The(g, ev.Other) : ""),
        SceneAction.Rest => "stays still",
        _ => "drifts slowly downward",
    };

    private static string CamEn(SceneGraph g)
    {
        var s = g.Camera switch
        {
            CameraMode.Orbit => (g.CameraSlow ? "slow 60°" : "smooth 120°") + " orbit around the scene",
            CameraMode.Dolly => "slow dolly-in toward the subject",
            CameraMode.Track => "smooth tracking shot following " + (g.CameraTarget >= 0 ? The(g, g.CameraTarget) : "the subject"),
            CameraMode.Overhead => "top-down overhead view",
            CameraMode.Crane => "rising crane shot",
            CameraMode.LowAngle => "low-angle static shot",
            _ => "locked-off static wide shot",
        };
        var mm = 12f / MathF.Tan(g.Fov * (MathF.PI / 360f));
        s += ", " + ((int)MathF.Round(mm)).ToString(CultureInfo.InvariantCulture) + "mm lens";
        if (g.Handheld) s += ", subtle handheld motion";
        return s;
    }

    private static string LightEn(LightPreset l) => l switch
    {
        LightPreset.Sunset => "warm golden-hour sunset light from the side with long soft shadows",
        LightPreset.Noon => "bright midday sunlight with crisp shadows",
        LightPreset.Night => "cool moonlit night lighting",
        LightPreset.Overcast => "soft overcast diffuse light",
        _ => "soft studio key light with gentle shadows",
    };

    private static string Compose(SceneGraph g, string variant)
    {
        var subjects = new List<string>();
        var seen = new HashSet<int>();
        foreach (var e in g.Entities)
        {
            if (!seen.Add(e.Group)) continue;
            var n = 0; foreach (var x in g.Entities) if (x.Group == e.Group) n++;
            subjects.Add(Describe(g, e, n));
        }
        var rels = new List<string>();
        var relSeen = new HashSet<string>();
        foreach (var ed in g.Edges)
        {
            var s = The(g, ed.From) + " " + RelEn(ed.Relation) + " " + The(g, ed.To);
            if (relSeen.Add(s)) rels.Add(s);
        }
        if (rels.Count == 0 && g.Entities.Count > 1) rels.Add("objects are placed side by side on the floor");
        var evs = new List<string>();
        var evSeen = new HashSet<string>();
        foreach (var ev in g.Events)
        {
            var s = "at " + ev.Start.ToString("0.0", CultureInfo.InvariantCulture) + "s " + The(g, ev.Entity) + " " + ActEn(g, ev);
            if (evSeen.Add(s)) evs.Add(s);
        }
        if (evs.Count == 0) evs.Add("subtle ambient motion");
        var refl = g.Reflectivity > 0.3f ? ", accurate floor reflections" : "";
        var physics = g.GravityEn + ", consistent cast shadows" + refl + ", rigid objects keep shape, size and count across every frame, no interpenetration, momentum-preserving collisions";
        var style = g.Styles.Count > 0 ? string.Join(", ", g.Styles) : "photorealistic, cinematic";
        var dur = g.Duration.ToString("0.#", CultureInfo.InvariantCulture) + "s continuous shot, temporally coherent";
        var subj = string.Join(", ", subjects);
        return variant switch
        {
            "concise" => Cap(subj) + ". " + Cap(string.Join("; ", evs)) + ". " + Cap(CamEn(g)) + ". " + Cap(LightEn(g.Light)) + ".",
            "physics-first" => Cap(physics) + ". " + Cap(subj) + ". " + Cap(string.Join("; ", rels)) + ". " + Cap(string.Join("; ", evs)) + ". Camera: " + CamEn(g) + ". Lighting: " + LightEn(g.Light) + ". " + Cap(style) + ".",
            _ => Cap(style) + ". " + Cap(subj) + ". " + Cap(string.Join("; ", rels)) + ". " + Cap(string.Join("; ", evs)) + ". Physics: " + physics + ". Camera: " + CamEn(g) + ". Lighting: " + LightEn(g.Light) + ". " + Cap(dur) + ".",
        };
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Negative(SceneGraph g)
    {
        var n = "flickering, morphing objects, object duplication, objects appearing or disappearing, warped geometry, inconsistent shadows, jitter, interpenetrating objects, low quality";
        if (g.Reflectivity > 0.3f) n += ", inconsistent reflections";
        if (g.Gravity != 0f) n += ", floating objects, objects defying gravity";
        if (g.Camera == CameraMode.Static && !g.Handheld) n += ", camera shake";
        if (g.Entities.Exists(e => e.Kind == SceneEntityKind.Character)) n += ", extra limbs, distorted hands";
        return n;
    }

    // ══════════════════════ 월드 컴파일 ══════════════════════

    /// <summary>장면 그래프 → 4D 월드(레이아웃 솔버 · 행동 → 초기 속도/스케줄 · 카메라 · 조명 · 중력).</summary>
    public CompiledWorld Compile(SceneGraph g, AiGenerative4dWorldModelEngine w)
    {
        var n = Math.Min(g.Entities.Count, AiGenerative4dWorldModelEngine.MaxObjects);
        float[] x = new float[n], y = new float[n], z = new float[n], vx = new float[n], vy = new float[n], vz = new float[n];
        var hold = new float[n];
        var settled = new bool[n];
        // ① 기본 배치(좌→우 일렬)
        float total = 0; for (var i = 0; i < n; i++) total += 2 * g.Entities[i].Hx + 0.4f;
        var cur = -total * 0.5f;
        for (var i = 0; i < n; i++)
        {
            var e = g.Entities[i];
            x[i] = cur + e.Hx; cur += 2 * e.Hx + 0.4f; y[i] = e.Hy; z[i] = 0; hold[i] = -1;
            settled[i] = true;
        }
        foreach (var ed in g.Edges) if (ed.From < n) settled[ed.From] = false;
        // ② 관계 해석(모든 랜드마크가 확정된 엔티티부터 · 수평 관계 → 지지 순 · 순환은 기본 배치 유지)
        var onIdx = new int[n];
        for (var pass = 0; pass < n + 1; pass++)
        {
            var progress = false;
            for (var t = 0; t < n; t++)
            {
                if (settled[t]) continue;
                var ready = true;
                foreach (var ed in g.Edges) if (ed.From == t && (ed.To >= n || !settled[ed.To])) ready = false;
                if (!ready) continue;
                foreach (var ed in g.Edges) if (ed.From == t && ed.Relation != SceneRelation.On) Place(g, ed, x, y, z, onIdx);
                foreach (var ed in g.Edges) if (ed.From == t && ed.Relation == SceneRelation.On) Place(g, ed, x, y, z, onIdx);
                settled[t] = true;
                progress = true;
            }
            if (!progress) break;
        }
        // ③ 겹침 해소(지지 관계 제외)
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                if (i == j || Supported(g, i, j) || Supported(g, j, i)) continue;
                var a = g.Entities[i]; var b = g.Entities[j];
                if (MathF.Abs(x[i] - x[j]) < a.Hx + b.Hx + 0.02f && MathF.Abs(y[i] - y[j]) < a.Hy + b.Hy && MathF.Abs(z[i] - z[j]) < a.Hz + b.Hz + 0.02f && j > i)
                    z[j] += a.Hz + b.Hz + 0.3f;
            }
        // ④ 행동 → 초기 상태/스케줄
        var ops = new List<(float T, WorldOp Op)>();
        foreach (var ev in g.Events)
        {
            var i = ev.Entity; if (i >= n) continue;
            var e = g.Entities[i];
            if (e.Mass <= 0) continue;
            float dvx = 0, dvy = 0, dvz = 0;
            switch (ev.Action)
            {
                case SceneAction.Fall: y[i] += 1.2f; break;
                case SceneAction.Bounce: if (!g.Events.Exists(o => o.Entity == i && o.Action is SceneAction.Fall or SceneAction.Throw)) y[i] += 0.9f; break;
                case SceneAction.Roll: dvx = 1.6f; break;
                case SceneAction.Throw: x[i] -= 0.6f; y[i] += 0.8f; dvx = 2.6f; dvy = 3.6f; break;
                case SceneAction.Push: dvx = 1.4f; break;
                case SceneAction.Drift: y[i] += 1.2f; dvy = -0.4f; break;
                case SceneAction.Collide:
                    if (ev.Other >= 0 && ev.Other < n && g.Entities[ev.Other].Mass > 0)
                    {
                        var o = ev.Other;
                        var mid = (x[i] + x[o]) * 0.5f;
                        z[o] = z[i]; y[o] = Math.Max(y[o], g.Entities[o].Hy);
                        x[i] = mid - 1.5f; x[o] = mid + 1.5f; dvx = 2.2f;
                        if (ev.Start > 0.01f) { hold[o] = ev.Start; ops.Add((ev.Start, new WorldOp(WorldOpKind.SetVelocity, o, -2.2f, 0, 0))); }
                        else vx[o] = -2.2f;
                    }
                    else dvx = 1.6f;
                    break;
            }
            if (ev.Start > 0.01f)
            {
                hold[i] = hold[i] < 0 ? ev.Start : Math.Min(hold[i], ev.Start);
                if (dvx != 0 || dvy != 0 || dvz != 0) ops.Add((ev.Start, new WorldOp(WorldOpKind.SetVelocity, i, dvx, dvy, dvz)));
            }
            else { vx[i] += dvx; vy[i] += dvy; vz[i] += dvz; }
            if (ev.Action == SceneAction.Bounce) g.Entities[i].Rest = Math.Max(g.Entities[i].Rest, 0.85f);
        }
        // ⑤ 월드 결선
        w.Reset();
        w.SetLighting(g.Light);
        var map = new int[n];
        for (var i = 0; i < n; i++)
        {
            var e = g.Entities[i];
            map[i] = w.Spawn(e.Id, e.Label, e.Shape, x[i], y[i], z[i], e.Hx, e.Hy, e.Hz, e.Mass, e.Rest, e.Fric * g.FrictionScale, e.R, e.G, e.B);
        }
        w.Apply(new WorldOp(WorldOpKind.Gravity, -1, 0, g.Gravity, 0));
        w.Apply(new WorldOp(WorldOpKind.Reflectivity, -1, g.Reflectivity, 0, 0));
        w.Apply(new WorldOp(WorldOpKind.TimeScale, -1, g.TimeScale, 0, 0));
        if (g.Damping > 0.05f) w.Apply(new WorldOp(WorldOpKind.Friction, -1, 0.9f, 0, 0));
        for (var i = 0; i < n; i++)
        {
            if (map[i] < 0) continue;
            if (vx[i] != 0 || vy[i] != 0 || vz[i] != 0) w.Apply(new WorldOp(WorldOpKind.SetVelocity, map[i], vx[i], vy[i], vz[i]));
            if (hold[i] > 0) { w.Apply(new WorldOp(WorldOpKind.Hold, map[i], 0, 0, 0)); w.Schedule(hold[i], new WorldOp(WorldOpKind.Release, map[i], 0, 0, 0)); }
        }
        var sched = 0;
        foreach (var (t, op) in ops) if (w.Schedule(t, op with { Obj = map[op.Obj] })) sched++;
        var target = g.CameraTarget >= 0 && g.CameraTarget < n ? map[g.CameraTarget] : -1;
        var cam = w.MakeCamera(g.Camera, target, g.CameraSlow, g.Fov, g.Duration);
        if (g.Handheld) cam.Shake = 1f;
        w.SetCamera(cam, 0);
        w.Touch("🌐 4D 월드 컴파일 · 객체 " + n.ToString(CultureInfo.InvariantCulture) + " · 예약 " + (sched + CountHolds(hold)).ToString(CultureInfo.InvariantCulture));
        return new CompiledWorld(map, cam, g.Duration, sched);
    }

    private static int CountHolds(float[] hold) { var c = 0; foreach (var h in hold) if (h > 0) c++; return c; }

    private static bool Supported(SceneGraph g, int a, int b) { foreach (var e in g.Edges) if (e.From == a && e.To == b && e.Relation == SceneRelation.On) return true; return false; }

    private static void Place(SceneGraph g, SceneEdge ed, float[] x, float[] y, float[] z, int[] onIdx)
    {
        var a = g.Entities[ed.From]; var b = g.Entities[ed.To];
        int t = ed.From, l = ed.To;
        switch (ed.Relation)
        {
            case SceneRelation.On:
                var cnt = 0; foreach (var e in g.Edges) if (e.Relation == SceneRelation.On && e.To == l) cnt++;
                var k = onIdx[l]++;
                var span = 2 * a.Hx + 0.08f;
                var off = (k - (cnt - 1) * 0.5f) * span;
                off = Math.Clamp(off, -Math.Max(0, b.Hx - a.Hx * 0.5f), Math.Max(0, b.Hx - a.Hx * 0.5f));
                x[t] = x[l] + off; z[t] = z[l]; y[t] = y[l] + b.Hy + a.Hy + 0.002f;
                break;
            case SceneRelation.Under: x[t] = x[l]; z[t] = z[l]; y[t] = a.Hy; break;
            case SceneRelation.LeftOf: x[t] = x[l] - (b.Hx + a.Hx + 0.35f); z[t] = z[l]; break;
            case SceneRelation.RightOf:
            case SceneRelation.NextTo: x[t] = x[l] + (b.Hx + a.Hx + 0.35f); z[t] = z[l]; break;
            case SceneRelation.InFrontOf: z[t] = z[l] + (b.Hz + a.Hz + 0.35f); x[t] = x[l]; break;
            case SceneRelation.Behind: z[t] = z[l] - (b.Hz + a.Hz + 0.35f); x[t] = x[l]; break;
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SceneGraph.OnChange", ex); }
    }
}
