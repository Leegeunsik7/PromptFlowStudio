using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.World;

public sealed record BranchDiagnostic(int Line, SceneIssueSeverity Severity, string Code, string Message);

/// <summary>컴파일된 분기 세그먼트(연산/선택지 범위 · 카메라 지시).</summary>
public readonly record struct BranchSegment(string Id, string Caption, int Frames, int OpStart, int OpCount, int ChoiceStart, int ChoiceCount, int Next,
    bool HasCamera, CameraMode Camera, int CameraTarget, int CameraBlend, bool Cut);

public readonly record struct BranchChoice(string Label, int Target, int From, int To, bool IsDefault);

/// <summary>트랜스파일된 인터랙티브 분기 프로그램(평탄 배열 · 런타임 무할당 순회).</summary>
public sealed class BranchProgram
{
    public BranchProgram(BranchSegment[] segments, WorldOp[] ops, BranchChoice[] choices, int start)
    { Segments = segments; Ops = ops; Choices = choices; Start = start; }
    public BranchSegment[] Segments { get; }
    public WorldOp[] Ops { get; }
    public BranchChoice[] Choices { get; }
    public int Start { get; }
    public int IndexOf(string id) { for (var i = 0; i < Segments.Length; i++) if (string.Equals(Segments[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
}

public sealed record TranspileResult(BranchProgram? Program, IReadOnlyList<BranchDiagnostic> Diagnostics)
{
    public bool Ok => Program is not null;
    public int Errors { get { var n = 0; foreach (var d in Diagnostics) if (d.Severity == SceneIssueSeverity.Error) n++; return n; } }
}

/// <summary>
/// [PromptFlow Studio v16.54.0] ▶ 실시간 인터랙티브 비디오 분기 &amp; 디시전 트랜스파일러.
///   ▸ 트랜스파일러: 분기 스크립트 DSL(segment/choice/next/impulse/velocity/gravity/hold/release/hide/show/timescale/restitution/camera/cut/start)
///     → 평탄 연산·선택지 배열. 진단: 미정의 대상 · 미지 객체 · 결정 창 범위 · 다중 기본값 · 도달 불가 · 선택 없는 무한 루프 (라인 번호).
///   ▸ 플레이어(무할당 틱): 시청자 선택(락프리 원자 교환) → 다음 프레임에 즉시 분기 — 4D 상태 매트릭스 이관(스냅샷) · 월드 연속(리셋 없음)
///     · 대상 세그먼트 연산 적용 · 카메라 스무스 블렌드(명시 cut 제외) · 결정 창 밖 선택 거부 · 타임아웃 기본 분기.
///   ▸ 심리스 판정: 전환 프레임 휘도 변화 vs 이동 평균(아티팩트 검출) · 전환 지연(프레임/µs) 계측.
///   ▸ 되감기(15프레임 간격 16 스냅샷 링) · 섀도 월드 선택지 프리뷰(본 월드 불변).
/// </summary>
public sealed class InteractiveVideoBranchingTranspiler
{
    private const int HistorySlots = 16, HistoryEvery = 15, DefaultBlend = 12;
    private readonly object _gate = new();
    private readonly float[] _handoff = new float[AiGenerative4dWorldModelEngine.StateFloats];
    private readonly float[][] _hist = new float[HistorySlots][];
    private readonly int[] _histSeg = new int[HistorySlots], _histFrame = new int[HistorySlots];
    private int _histHead, _histCount;
    private readonly AiGenerative4dWorldModelEngine _shadow = new();
    private BranchProgram? _p;
    private AiGenerative4dWorldModelEngine? _w;
    private int _seg = -1, _segFrame, _pending = -1;
    private long _tick, _pendingTick;
    private byte[] _prev = Array.Empty<byte>();
    private bool _hasPrev;
    private float _avgDelta = -1f;
    private long _switches, _seamless, _artifacts, _cuts, _rejected, _timeouts, _transpiles, _ticks;
    private float _maxSwitchDelta;
    private int _lastLatency;
    private double _lastSwitchUs;

    public InteractiveVideoBranchingTranspiler()
    {
        for (var i = 0; i < HistorySlots; i++) _hist[i] = new float[AiGenerative4dWorldModelEngine.StateFloats];
    }

    public event Action? OnChange;

    public BranchProgram? Program => _p;
    public int CurrentSegment => _seg;
    public int SegmentFrame => _segFrame;
    public bool Ended { get; private set; }
    public bool Playing => _p is not null && _seg >= 0 && !Ended;
    public long Switches => Interlocked.Read(ref _switches);
    public long SeamlessSwitches => Interlocked.Read(ref _seamless);
    public long ArtifactSwitches => Interlocked.Read(ref _artifacts);
    public long HardCuts => Interlocked.Read(ref _cuts);
    public long RejectedChoices => Interlocked.Read(ref _rejected);
    public long TimeoutDefaults => Interlocked.Read(ref _timeouts);
    public long Transpiles => Interlocked.Read(ref _transpiles);
    public long Ticks => Interlocked.Read(ref _ticks);
    public float MaxSwitchDelta => _maxSwitchDelta;
    public float AverageDelta => Math.Max(0f, _avgDelta);
    public int LastSwitchLatencyFrames => _lastLatency;
    public double LastSwitchMicros => _lastSwitchUs;
    public int HistoryCount => _histCount;
    public ReadOnlySpan<float> HandoffMatrix => _handoff;
    public string? LastAction
    {
        get { var p = _p; var a = _actionSeg; return a >= 0 && p is not null && a < p.Segments.Length ? "🔀 분기 → " + p.Segments[a].Caption : _lastAction; }
        private set { _lastAction = value; _actionSeg = -1; }
    }
    private string? _lastAction;
    private int _actionSeg = -1;

    // ══════════════════════ 트랜스파일 ══════════════════════

    /// <summary>분기 DSL → 프로그램(객체 이름은 월드 기준 해석).</summary>
    public TranspileResult Transpile(string source, AiGenerative4dWorldModelEngine world)
    {
        var diags = new List<BranchDiagnostic>();
        var segIds = new List<string>(); var captions = new List<string>(); var frames = new List<int>(); var segLine = new List<int>();
        var segOps = new List<List<WorldOp>>(); var segChoices = new List<List<(string Label, string Target, int From, int To, bool Def, int Line)>>();
        var segNext = new List<(string? Target, int Line)>(); var segCam = new List<(bool Has, CameraMode Mode, int Target, int Blend, bool Cut)>();
        string? startId = null; var startLine = 0;
        var lines = (source ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var cur = -1;
        for (var ln = 0; ln < lines.Length; ln++)
        {
            var lineNo = ln + 1;
            var raw = lines[ln];
            var hash = raw.IndexOf('#');
            if (hash >= 0 && !InQuote(raw, hash)) raw = raw[..hash];
            var t = Tokenize(raw);
            if (t.Count == 0) continue;
            var cmd = t[0].ToLowerInvariant();
            if (cmd == "start") { if (t.Count < 2) Err(diags, lineNo, "BR-SYNTAX", "start <세그먼트>"); else { startId = t[1]; startLine = lineNo; } continue; }
            if (cmd == "segment")
            {
                if (t.Count < 3 || !int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fr) || fr < 1 || fr > 36000)
                { Err(diags, lineNo, "BR-SYNTAX", "segment <id> <프레임 1~36000> [\"캡션\"]"); cur = -1; continue; }
                if (segIds.Exists(s => string.Equals(s, t[1], StringComparison.OrdinalIgnoreCase))) { Err(diags, lineNo, "BR-DUP-SEGMENT", "세그먼트 중복: " + t[1]); cur = -1; continue; }
                segIds.Add(t[1]); captions.Add(t.Count > 3 ? t[3] : t[1]); frames.Add(fr); segLine.Add(lineNo);
                segOps.Add(new List<WorldOp>()); segChoices.Add(new()); segNext.Add((null, 0)); segCam.Add((false, CameraMode.Static, -1, DefaultBlend, false));
                cur = segIds.Count - 1;
                continue;
            }
            if (cmd == "end") { cur = -1; continue; }
            if (cur < 0) { Err(diags, lineNo, "BR-ORPHAN", "세그먼트 밖 명령: " + cmd); continue; }
            switch (cmd)
            {
                case "choice":
                {
                    // choice "라벨" -> target [@ a..b] [default]
                    var arrow = t.IndexOf("->");
                    if (t.Count < 4 || arrow != 2) { Err(diags, lineNo, "BR-SYNTAX", "choice \"라벨\" -> <세그먼트> [@ 시작..끝] [default]"); break; }
                    int from = 0, to = frames[cur] - 1; var def = false;
                    for (var k = 4; k < t.Count; k++)
                    {
                        if (t[k] == "@" && k + 1 < t.Count)
                        {
                            var r = t[k + 1].Split("..");
                            if (r.Length != 2 || !int.TryParse(r[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out from) || !int.TryParse(r[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out to))
                            { Err(diags, lineNo, "BR-SYNTAX", "결정 창 형식: @ 시작..끝"); from = 0; to = frames[cur] - 1; }
                            k++;
                        }
                        else if (string.Equals(t[k], "default", StringComparison.OrdinalIgnoreCase)) def = true;
                        else Err(diags, lineNo, "BR-SYNTAX", "알 수 없는 choice 인자: " + t[k]);
                    }
                    if (from < 0 || to >= frames[cur] || from > to) Err(diags, lineNo, "BR-WINDOW", "결정 창 " + from.ToString(CultureInfo.InvariantCulture) + ".." + to.ToString(CultureInfo.InvariantCulture) + " 이 세그먼트(0.." + (frames[cur] - 1).ToString(CultureInfo.InvariantCulture) + ") 밖");
                    segChoices[cur].Add((t[1], t[3], from, to, def, lineNo));
                    break;
                }
                case "next":
                    if (t.Count < 2) Err(diags, lineNo, "BR-SYNTAX", "next <세그먼트>"); else segNext[cur] = (t[1], lineNo);
                    break;
                case "impulse":
                case "velocity":
                case "teleport":
                {
                    if (t.Count < 5) { Err(diags, lineNo, "BR-SYNTAX", cmd + " <객체> x y z"); break; }
                    var o = Obj(world, t[1]);
                    if (o < 0) { Err(diags, lineNo, "BR-UNKNOWN-OBJECT", "미지 객체: " + t[1]); break; }
                    if (!Vec(t, 2, out var x, out var y, out var z)) { Err(diags, lineNo, "BR-SYNTAX", "수치 형식 오류"); break; }
                    var kind = cmd == "impulse" ? WorldOpKind.Impulse : cmd == "velocity" ? WorldOpKind.SetVelocity : WorldOpKind.Teleport;
                    if (world.IsStatic(o) && kind != WorldOpKind.Teleport) diags.Add(new BranchDiagnostic(lineNo, SceneIssueSeverity.Warning, "BR-STATIC", "정적 객체에 운동 연산(무시됨): " + t[1]));
                    segOps[cur].Add(new WorldOp(kind, o, x, y, z));
                    break;
                }
                case "gravity":
                {
                    if (t.Count < 4 || !Vec(t, 1, out var x, out var y, out var z)) { Err(diags, lineNo, "BR-SYNTAX", "gravity x y z"); break; }
                    segOps[cur].Add(new WorldOp(WorldOpKind.Gravity, -1, x, y, z));
                    break;
                }
                case "hold":
                case "release":
                case "hide":
                case "show":
                {
                    if (t.Count < 2) { Err(diags, lineNo, "BR-SYNTAX", cmd + " <객체>"); break; }
                    var o = Obj(world, t[1]);
                    if (o < 0) { Err(diags, lineNo, "BR-UNKNOWN-OBJECT", "미지 객체: " + t[1]); break; }
                    var kind = cmd switch { "hold" => WorldOpKind.Hold, "release" => WorldOpKind.Release, "hide" => WorldOpKind.Hide, _ => WorldOpKind.Show };
                    if (kind is WorldOpKind.Hide or WorldOpKind.Show) diags.Add(new BranchDiagnostic(lineNo, SceneIssueSeverity.Info, "BR-PERMANENCE", "의도된 객체 " + (kind == WorldOpKind.Hide ? "소멸" : "출현") + " — 영속성 예외"));
                    segOps[cur].Add(new WorldOp(kind, o, 0, 0, 0));
                    break;
                }
                case "timescale":
                case "restitution":
                case "friction":
                case "reflect":
                {
                    if (t.Count < 2 || !float.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { Err(diags, lineNo, "BR-SYNTAX", cmd + " <값>"); break; }
                    var kind = cmd switch { "timescale" => WorldOpKind.TimeScale, "restitution" => WorldOpKind.Restitution, "friction" => WorldOpKind.Friction, _ => WorldOpKind.Reflectivity };
                    var o = -1;
                    if (t.Count > 2 && kind is WorldOpKind.Restitution or WorldOpKind.Friction) { o = Obj(world, t[2]); if (o < 0) { Err(diags, lineNo, "BR-UNKNOWN-OBJECT", "미지 객체: " + t[2]); break; } }
                    segOps[cur].Add(new WorldOp(kind, o, v, 0, 0));
                    break;
                }
                case "camera":
                {
                    // camera <static|orbit|dolly|track|overhead|crane|low> [객체] [블렌드]
                    if (t.Count < 2 || !TryMode(t[1], out var mode)) { Err(diags, lineNo, "BR-SYNTAX", "camera <static|orbit|dolly|track|overhead|crane|low> [객체] [블렌드 프레임]"); break; }
                    var target = -1; var blend = segCam[cur].Blend; var k = 2;
                    if (mode == CameraMode.Track)
                    {
                        if (t.Count < 3 || (target = Obj(world, t[2])) < 0) { Err(diags, lineNo, "BR-UNKNOWN-OBJECT", "track 대상 객체 필요"); break; }
                        k = 3;
                    }
                    if (k < t.Count && (!int.TryParse(t[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out blend) || blend < 0 || blend > 240)) { Err(diags, lineNo, "BR-SYNTAX", "블렌드 프레임 0~240"); break; }
                    segCam[cur] = (true, mode, target, blend, segCam[cur].Cut);
                    break;
                }
                case "cut":
                    segCam[cur] = (segCam[cur].Has, segCam[cur].Mode, segCam[cur].Target, 0, true);
                    break;
                default:
                    Err(diags, lineNo, "BR-UNKNOWN-COMMAND", "알 수 없는 명령: " + cmd);
                    break;
            }
        }
        if (segIds.Count == 0) Err(diags, 0, "BR-EMPTY", "세그먼트 없음");
        // 대상 해석
        int Find(string id) { for (var i = 0; i < segIds.Count; i++) if (string.Equals(segIds[i], id, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        var start = 0;
        if (startId is not null && (start = Find(startId)) < 0) { Err(diags, startLine, "BR-UNKNOWN-TARGET", "start 대상 없음: " + startId); start = 0; }
        var segs = new List<BranchSegment>(); var ops = new List<WorldOp>(); var chs = new List<BranchChoice>();
        for (var s = 0; s < segIds.Count; s++)
        {
            var opStart = ops.Count; ops.AddRange(segOps[s]);
            var chStart = chs.Count; var defaults = 0;
            foreach (var c in segChoices[s])
            {
                var target = Find(c.Target);
                if (target < 0) Err(diags, c.Line, "BR-UNKNOWN-TARGET", "선택지 대상 없음: " + c.Target);
                if (c.Def) defaults++;
                chs.Add(new BranchChoice(c.Label, target, c.From, c.To, c.Def));
            }
            if (defaults > 1) Err(diags, segLine[s], "BR-MULTI-DEFAULT", segIds[s] + " 기본 선택지 " + defaults.ToString(CultureInfo.InvariantCulture) + "개");
            if (defaults == 0 && segChoices[s].Count > 0)
            {
                chs[chStart] = chs[chStart] with { IsDefault = true };
                diags.Add(new BranchDiagnostic(segLine[s], SceneIssueSeverity.Info, "BR-IMPLICIT-DEFAULT", segIds[s] + " 기본 선택지 미지정 — 첫 선택지가 타임아웃 기본값"));
            }
            var next = -1;
            if (segNext[s].Target is { } nt && (next = Find(nt)) < 0) Err(diags, segNext[s].Line, "BR-UNKNOWN-TARGET", "next 대상 없음: " + nt);
            var cam = segCam[s];
            segs.Add(new BranchSegment(segIds[s], captions[s], frames[s], opStart, ops.Count - opStart, chStart, chs.Count - chStart, next, cam.Has, cam.Mode, cam.Target, cam.Blend, cam.Cut));
        }
        // 도달성 · 선택 없는 루프
        if (segs.Count > 0)
        {
            var seen = new bool[segs.Count]; var stack = new Stack<int>(); stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                for (var c = 0; c < segs[s].ChoiceCount; c++) { var tg = chs[segs[s].ChoiceStart + c].Target; if (tg >= 0 && !seen[tg]) { seen[tg] = true; stack.Push(tg); } }
                if (segs[s].Next >= 0 && !seen[segs[s].Next]) { seen[segs[s].Next] = true; stack.Push(segs[s].Next); }
            }
            for (var s = 0; s < segs.Count; s++) if (!seen[s]) diags.Add(new BranchDiagnostic(segLine[s], SceneIssueSeverity.Warning, "BR-UNREACHABLE", "도달 불가 세그먼트: " + segIds[s]));
            for (var s = 0; s < segs.Count; s++)
            {
                var cur2 = s; var hop = 0;
                while (hop++ <= segs.Count && segs[cur2].ChoiceCount == 0 && segs[cur2].Next >= 0) { cur2 = segs[cur2].Next; if (cur2 == s) { diags.Add(new BranchDiagnostic(segLine[s], SceneIssueSeverity.Warning, "BR-LOOP", "선택지 없는 무한 루프: " + segIds[s])); break; } }
            }
            var endings = 0;
            foreach (var sg in segs) if (sg.ChoiceCount == 0 && sg.Next < 0) endings++;
            if (endings == 0) diags.Add(new BranchDiagnostic(0, SceneIssueSeverity.Info, "BR-NO-ENDING", "엔딩 세그먼트 없음(순환 스토리)"));
        }
        Interlocked.Increment(ref _transpiles);
        var ok = !diags.Exists(d => d.Severity == SceneIssueSeverity.Error);
        var prog = ok ? new BranchProgram(segs.ToArray(), ops.ToArray(), chs.ToArray(), start) : null;
        LastAction = ok ? "🔀 트랜스파일 · 세그먼트 " + segs.Count.ToString(CultureInfo.InvariantCulture) + " · 연산 " + ops.Count.ToString(CultureInfo.InvariantCulture) + " · 선택지 " + chs.Count.ToString(CultureInfo.InvariantCulture)
                        : "⛔ 트랜스파일 오류 " + diags.FindAll(d => d.Severity == SceneIssueSeverity.Error).Count.ToString(CultureInfo.InvariantCulture) + "건";
        Raise();
        return new TranspileResult(prog, diags);
    }

    private static void Err(List<BranchDiagnostic> d, int line, string code, string msg) => d.Add(new BranchDiagnostic(line, SceneIssueSeverity.Error, code, msg));

    private static bool InQuote(string s, int idx) { var q = false; for (var i = 0; i < idx; i++) if (s[i] == '"') q = !q; return q; }

    private static List<string> Tokenize(string s)
    {
        var r = new List<string>(); var sb = new StringBuilder(); var q = false;
        foreach (var c in s)
        {
            if (c == '"') { if (q) { r.Add(sb.ToString()); sb.Clear(); } q = !q; continue; }
            if (!q && char.IsWhiteSpace(c)) { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) r.Add(sb.ToString());
        return r;
    }

    private static int Obj(AiGenerative4dWorldModelEngine w, string name)
    {
        if (name.Length > 1 && name[0] == '#' && int.TryParse(name.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i >= 0 && i < w.ObjectCount ? i : -1;
        return w.IndexOf(name);
    }

    private static bool Vec(List<string> t, int k, out float x, out float y, out float z)
    {
        y = z = 0;
        return float.TryParse(t[k], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(t[k + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) && float.TryParse(t[k + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
    }

    private static bool TryMode(string s, out CameraMode m)
    {
        m = s.ToLowerInvariant() switch { "static" => CameraMode.Static, "orbit" => CameraMode.Orbit, "dolly" => CameraMode.Dolly, "track" => CameraMode.Track, "overhead" => CameraMode.Overhead, "crane" => CameraMode.Crane, "low" => CameraMode.LowAngle, _ => (CameraMode)255 };
        return (byte)m != 255;
    }

    /// <summary>월드 객체로부터 기본 인터랙티브 스토리 DSL 생성(첫 동적 객체 기준).</summary>
    public static string BuildDefaultStory(AiGenerative4dWorldModelEngine w)
    {
        var d0 = -1;
        for (var i = 0; i < w.ObjectCount; i++) if (!w.IsStatic(i)) { d0 = i; break; }
        var n = d0 >= 0 ? w.Name(d0) : null;
        var sb = new StringBuilder();
        sb.Append("# PromptFlow 4D 인터랙티브 분기 스크립트\n");
        sb.Append("start intro\n");
        sb.Append("segment intro 150 \"생성된 4D 장면\"\n");
        if (n is not null) sb.Append("  choice \"💥 ").Append(n).Append(" 밀어내기\" -> push @ 20..140\n");
        sb.Append("  choice \"🌙 달 중력 전환\" -> moon @ 20..140\n");
        sb.Append("  choice \"🎥 오비트 카메라\" -> orbit @ 20..140 default\n");
        sb.Append("segment push 120 \"충격 분기\"\n");
        if (n is not null) sb.Append("  impulse ").Append(n).Append(" 2.5 3 0\n  camera track ").Append(n).Append(" 18\n");
        sb.Append("  next finale\n");
        sb.Append("segment moon 150 \"저중력 분기\"\n  gravity 0 -1.62 0\n");
        if (n is not null) sb.Append("  impulse ").Append(n).Append(" 0 3.5 0\n");
        sb.Append("  camera dolly 18\n  next finale\n");
        sb.Append("segment orbit 120 \"카메라 분기\"\n  camera orbit 18\n  next finale\n");
        sb.Append("segment finale 90 \"피날레\"\n  gravity 0 -9.81 0\n  camera static 24\n");
        return sb.ToString();
    }

    // ══════════════════════ 플레이어 ══════════════════════

    /// <summary>프로그램 로드 → 시작 세그먼트 진입(월드 현재 상태 유지).</summary>
    public void Load(BranchProgram program, AiGenerative4dWorldModelEngine world)
    {
        lock (_gate)
        {
            _p = program; _w = world;
            _histHead = 0; _histCount = 0; _hasPrev = false; _avgDelta = -1f; _tick = 0; Ended = false;
            Interlocked.Exchange(ref _pending, -1);
            Enter(program.Start, false);
        }
        LastAction = "▶ 분기 스토리 로드 · " + program.Segments[program.Start].Caption;
        Raise();
    }

    /// <summary>시청자 선택(현재 세그먼트 기준 선택지 번호 · 락프리) — 다음 틱에 적용.</summary>
    public void Choose(int choice)
    {
        Interlocked.Exchange(ref _pendingTick, Interlocked.Read(ref _tick));
        Interlocked.Exchange(ref _pending, choice);
    }

    /// <summary>결정 창 열림 여부(현재 프레임).</summary>
    public bool IsWindowOpen(int choice)
    {
        var p = _p; var s = _seg;
        if (p is null || s < 0 || Ended) return false;
        var sg = p.Segments[s];
        if ((uint)choice >= (uint)sg.ChoiceCount) return false;
        var c = p.Choices[sg.ChoiceStart + choice];
        return _segFrame >= c.From && _segFrame <= c.To;
    }

    /// <summary>1프레임 진행 + 렌더(무할당). 반환 false = 엔딩 도달.</summary>
    public bool Tick(Span<byte> rgb, int w, int h)
    {
        lock (_gate)
        {
            var p = _p; var world = _w;
            if (p is null || world is null || _seg < 0 || Ended) return false;
            var switched = false; var cut = false;
            var pend = Interlocked.Exchange(ref _pending, -1);
            if (pend >= 0)
            {
                var sg = p.Segments[_seg];
                if ((uint)pend < (uint)sg.ChoiceCount && _segFrame >= p.Choices[sg.ChoiceStart + pend].From && _segFrame <= p.Choices[sg.ChoiceStart + pend].To)
                {
                    _lastLatency = (int)(_tick - Interlocked.Read(ref _pendingTick)) + 1;
                    cut = Switch(p.Choices[sg.ChoiceStart + pend].Target);
                    switched = true;
                }
                else Interlocked.Increment(ref _rejected);
            }
            world.StepFrame();
            _segFrame++; _tick++;
            Interlocked.Increment(ref _ticks);
            if (_segFrame % HistoryEvery == 0) PushHistory(world);
            var cs = p.Segments[_seg];
            if (_segFrame >= cs.Frames)
            {
                if (cs.Next >= 0) { cut |= Switch(cs.Next); switched = true; _lastLatency = 0; }
                else if (cs.ChoiceCount > 0)
                {
                    var def = cs.ChoiceStart;
                    for (var c = 0; c < cs.ChoiceCount; c++) if (p.Choices[cs.ChoiceStart + c].IsDefault) { def = cs.ChoiceStart + c; break; }
                    Interlocked.Increment(ref _timeouts);
                    cut |= Switch(p.Choices[def].Target); switched = true; _lastLatency = 0;
                }
                else Ended = true;
            }
            if (rgb.Length >= w * h * 3 && w > 0 && h > 0)
            {
                world.Render(rgb, w, h);
                var n = w * h * 3;
                if (_prev.Length != n) { _prev = new byte[n]; _hasPrev = false; }   // 해상도 변경 시 1회 재할당
                if (_hasPrev)
                {
                    var d = AiGenerative4dWorldModelEngine.MeanAbsLumaDiff(_prev, rgb[..n]);
                    if (switched)
                    {
                        if (cut) Interlocked.Increment(ref _cuts);
                        else
                        {
                            if (d > _maxSwitchDelta) _maxSwitchDelta = d;
                            var avg = _avgDelta < 0 ? d : _avgDelta;
                            if (d <= Math.Max(Math.Max(avg * 3f, avg + 10f), 6f)) Interlocked.Increment(ref _seamless); else Interlocked.Increment(ref _artifacts);
                        }
                    }
                    else _avgDelta = _avgDelta < 0 ? d : _avgDelta * 0.9f + d * 0.1f;
                }
                rgb[..n].CopyTo(_prev);
                _hasPrev = true;
            }
            else if (switched && !cut) Interlocked.Increment(ref _seamless);
            if ((_tick & 7) == 0 || switched || Ended) Raise();
            return !Ended;
        }
    }

    /// <summary>분기 전환: 4D 상태 매트릭스 이관 → 대상 연산 → 카메라 블렌드. 반환 = 하드 컷 여부.</summary>
    private bool Switch(int target)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var cut = Enter(target, true);
        _lastSwitchUs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
        Interlocked.Increment(ref _switches);
        return cut;
    }

    private bool Enter(int target, bool handoff)
    {
        var p = _p!; var w = _w!;
        if (handoff) w.Capture(_handoff);
        _seg = target; _segFrame = 0;
        var sg = p.Segments[target];
        for (var k = 0; k < sg.OpCount; k++) w.Apply(in p.Ops[sg.OpStart + k]);
        if (sg.HasCamera)
        {
            var spec = w.MakeCamera(sg.Camera, sg.CameraTarget, false, w.Camera.Fov, sg.Frames / (float)w.Fps);
            w.SetCamera(spec, sg.Cut ? 0 : sg.CameraBlend);
        }
        else if (sg.Cut) w.CutCamera();
        _actionSeg = target;   // 문자열 생성은 읽기 시점(틱 무할당)
        return sg.Cut;
    }

    private void PushHistory(AiGenerative4dWorldModelEngine w)
    {
        w.Capture(_hist[_histHead]);
        _histSeg[_histHead] = _seg; _histFrame[_histHead] = _segFrame;
        _histHead = (_histHead + 1) % HistorySlots;
        if (_histCount < HistorySlots) _histCount++;
    }

    /// <summary>스냅샷 단위 되감기(1 = 최근 스냅샷). 성공 시 세그먼트/프레임/월드 상태 복원.</summary>
    public bool Rewind(int steps)
    {
        lock (_gate)
        {
            var w = _w;
            if (w is null || steps < 1 || steps > _histCount) return false;
            var idx = ((_histHead - steps) % HistorySlots + HistorySlots) % HistorySlots;
            w.Restore(_hist[idx]);
            _seg = _histSeg[idx]; _segFrame = _histFrame[idx]; Ended = false; _hasPrev = false;
            _histHead = (idx + 1) % HistorySlots; _histCount -= steps - 1;
            Interlocked.Exchange(ref _pending, -1);
            LastAction = "⏪ 되감기 · " + _p!.Segments[_seg].Caption + " f" + _segFrame.ToString(CultureInfo.InvariantCulture);
        }
        Raise();
        return true;
    }

    /// <summary>섀도 월드에서 선택지 결과를 frames 만큼 미리 시뮬해 썸네일 렌더(본 월드 불변 · 첫 호출 후 무할당).</summary>
    public bool PreviewChoice(int choice, int frames, Span<byte> rgb, int w, int h)
    {
        lock (_gate)
        {
            var p = _p; var world = _w;
            if (p is null || world is null || _seg < 0) return false;
            var sg = p.Segments[_seg];
            if ((uint)choice >= (uint)sg.ChoiceCount) return false;
            var target = p.Choices[sg.ChoiceStart + choice].Target;
            if (target < 0) return false;
            _shadow.CopyFrom(world);
            var ts = p.Segments[target];
            for (var k = 0; k < ts.OpCount; k++) _shadow.Apply(in p.Ops[ts.OpStart + k]);
            if (ts.HasCamera) { var spec = _shadow.MakeCamera(ts.Camera, ts.CameraTarget, false, _shadow.Camera.Fov, ts.Frames / (float)_shadow.Fps); _shadow.SetCamera(spec, ts.Cut ? 0 : ts.CameraBlend); }
            for (var f = 0; f < frames; f++) _shadow.StepFrame();
            _shadow.Render(rgb, w, h);
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate) { Ended = true; }
        LastAction = "⏹ 분기 재생 정지";
        Raise();
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _switches, 0); Interlocked.Exchange(ref _seamless, 0); Interlocked.Exchange(ref _artifacts, 0); Interlocked.Exchange(ref _cuts, 0);
        Interlocked.Exchange(ref _rejected, 0); Interlocked.Exchange(ref _timeouts, 0);
        _maxSwitchDelta = 0; _lastLatency = 0; _lastSwitchUs = 0;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Branching.OnChange", ex); }
    }
}
