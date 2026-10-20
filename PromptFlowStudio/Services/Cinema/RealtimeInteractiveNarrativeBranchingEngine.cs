using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Metaverse;
using PromptFlowStudio.Services.Neuro;
using PromptFlowStudio.Services.Nexus;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Cinema;

public enum BranchTrigger : byte { Timeout = 0, Audience, Vote, Override, Restart }

public enum AudienceEventKind : byte { Vote = 0, Gaze, Bci, Text, Voice }

/// <summary>관객 이벤트(값 형식 · Text 는 기존 문자열 참조만 보관 → 무할당 큐잉).</summary>
public struct AudienceEvent
{
    public AudienceEventKind Kind;
    public int Arg;
    public XrGazeSample Gaze;
    public EmotionState Bci;
    public string? Text;
}

public readonly record struct BranchTransition(int From, int To, int Edge, BranchTrigger Trigger, float AtSec, float Probability, float DecisionMicros, bool PrefetchHit);

public sealed class NarrativeNode
{
    public string Name = "", Caption = "";
    public int Act, Depth = -1, OutCount;
    public float DurationSec, LayoutX, LayoutY, Reach;
    public readonly float[] Mood = new float[NeuralChoiceTreeEmotionEvaluator.Dim];
    public readonly int[] Out = new int[NeuralChoiceTreeEmotionEvaluator.MaxChoices];
    public NexusShot[] Shots = Array.Empty<NexusShot>();
    public bool Ending;
    public long Visits;
}

public sealed class NarrativeEdge
{
    public int From, To, RequireFlag = -1, SetFlag = -1;
    public string Label = "";
    public string[] Keywords = Array.Empty<string>();
    public float Prior;
    public Vector3 Hotspot;
    public long Taken;
}

/// <summary>
/// [PromptFlow Studio v16.78.0] 🎬 실시간 인터랙티브 시네마 서사 분기 엔진.
/// 시나리오 그래프(합류 · 루프 · 플래그 조건 간선 · 엔딩 → 재시작) · v16.54 BranchProgram 임포트 · v16.70 오케스트레이터 3막 샷 플랜(노드별 샷 · 카메라/조명) 결선.
/// 관객 이벤트 링 큐(투표 · 시선 · BCI · 텍스트 · 음성) → 평가기 → 결정 창(노드 말미 N 초)에서 예측 노드 프리페치 · 의도/투표 정족수 조기 확정 ·
/// 타임아웃 시 최대 확률 간선 · 감독 수동 개입(즉시) · 감독 홀드(노드 루프) → 블렌딩 가드 트랜지션(진행 중 커밋 지연 직렬화). 3단 도달 확률 트리. 틱 무할당.
/// </summary>
public sealed class RealtimeInteractiveNarrativeBranchingEngine
{
    public const int MaxNodes = 64, MaxEdges = 256, MaxFlags = 16, EventCap = 256, HistoryCap = 64, Horizon = 3;
    public static readonly float[] DefaultMood = { 0.2f, 0.3f, 0.1f, 0.1f, 0.6f, 0.2f };

    private readonly object _gate = new();
    private readonly NarrativeNode[] _nodes = new NarrativeNode[MaxNodes];
    private readonly NarrativeEdge[] _edges = new NarrativeEdge[MaxEdges];
    private readonly bool[] _flags = new bool[MaxFlags];
    private readonly AudienceEvent[] _queue = new AudienceEvent[EventCap];
    private readonly BranchTransition[] _history = new BranchTransition[HistoryCap];
    private readonly float[] _massA = new float[MaxNodes], _massB = new float[MaxNodes];
    private readonly float[] _tmpLogit = new float[NeuralChoiceTreeEmotionEvaluator.MaxChoices], _tmpProb = new float[NeuralChoiceTreeEmotionEvaluator.MaxChoices];
    private readonly bool[] _tmpElig = new bool[NeuralChoiceTreeEmotionEvaluator.MaxChoices];
    private readonly NeuralChoiceTreeEmotionEvaluator _eval;
    private int _nodeCount, _edgeCount, _qHead, _qCount, _histCount, _start, _current = -1, _pendingOverride = -1, _predicted = -1;
    private float _playhead, _time;

    public RealtimeInteractiveNarrativeBranchingEngine(NeuralChoiceTreeEmotionEvaluator? evaluator = null, ZeroSeamlessTransitionBlendingGuard? guard = null)
    {
        _eval = evaluator ?? new NeuralChoiceTreeEmotionEvaluator();
        Guard = guard;
    }

    public NeuralChoiceTreeEmotionEvaluator Evaluator => _eval;
    public ZeroSeamlessTransitionBlendingGuard? Guard { get; set; }
    public int NodeCount => _nodeCount;
    public int EdgeCount => _edgeCount;
    public int StartNode => _start;
    public int CurrentNode => _current;
    public int PredictedNode => _predicted;
    public float Playhead => _playhead;
    public float TimeSec => _time;
    public NarrativeNode Node(int i) => _nodes[i];
    public NarrativeEdge Edge(int i) => _edges[i];
    public bool Flag(int f) => _flags[f];
    public bool AutoBranch { get; set; } = true;
    /// <summary>감독 홀드: 자동 분기 정지 · 노드 결정 창 루프 · 수동 개입 대기.</summary>
    public bool DirectorHold { get; set; }
    public float DecisionWindowSec { get; set; } = 4f;
    public float EarlyCommitShare { get; set; } = 0.65f;
    public int VoteQuorum { get; set; } = 3;
    public float VideoFadeMs { get; set; } = 800f;
    public float AudioFadeMs { get; set; } = 600f;
    public float OverrideFadeMs { get; set; } = 400f;
    public long Commits { get; private set; }
    public long DeferredCommits { get; private set; }
    public long PrefetchHits { get; private set; }
    public long PrefetchMisses { get; private set; }
    public long PrefetchSerial { get; private set; }
    public long TransitionSerial { get; private set; }
    public long EventsProcessed { get; private set; }
    public long EventsDropped { get; private set; }
    public float LastDecisionMicros { get; private set; }
    public float MaxDecisionMicros { get; private set; }
    public int PreviousNode { get; private set; } = -1;
    public bool InDecisionWindow { get; private set; }
    public string? LastAction { get; private set; }
    public int HistoryCount => _histCount;
    /// <summary>최근 → 과거 순 k 번째 트랜지션.</summary>
    public BranchTransition History(int k) => _history[(_histCount - 1 - k) % HistoryCap];

    // ══════════════════════ 그래프 구축(제어 평면) ══════════════════════

    public void Clear()
    {
        lock (_gate)
        {
            _nodeCount = _edgeCount = 0; _current = -1; _histCount = 0; _qCount = 0; _pendingOverride = -1; _predicted = -1; _time = 0;
            Array.Clear(_flags);
        }
    }

    public int AddNode(string name, string caption, int act, float durationSec, ReadOnlySpan<float> mood, bool ending = false)
    {
        lock (_gate)
        {
            if (_nodeCount >= MaxNodes) return -1;
            var n = _nodes[_nodeCount] ??= new NarrativeNode();
            n.Name = name; n.Caption = caption; n.Act = act; n.DurationSec = MathF.Max(2f, durationSec); n.Ending = ending;
            n.OutCount = 0; n.Visits = 0; n.Depth = -1; n.Reach = 0; n.Shots = Array.Empty<NexusShot>();
            (mood.Length >= NeuralChoiceTreeEmotionEvaluator.Dim ? mood : DefaultMood)[..NeuralChoiceTreeEmotionEvaluator.Dim].CopyTo(n.Mood);
            return _nodeCount++;
        }
    }

    public int AddEdge(int from, int to, string label, float prior, string[]? keywords = null, int requireFlag = -1, int setFlag = -1)
    {
        lock (_gate)
        {
            if (_edgeCount >= MaxEdges || (uint)from >= (uint)_nodeCount || (uint)to >= (uint)_nodeCount) return -1;
            var n = _nodes[from];
            if (n.OutCount >= n.Out.Length) return -1;
            var e = _edges[_edgeCount] ??= new NarrativeEdge();
            e.From = from; e.To = to; e.Label = label; e.Prior = prior; e.Keywords = keywords ?? Array.Empty<string>();
            e.RequireFlag = requireFlag < MaxFlags ? requireFlag : -1; e.SetFlag = setFlag < MaxFlags ? setFlag : -1; e.Taken = 0;
            n.Out[n.OutCount++] = _edgeCount;
            return _edgeCount++;
        }
    }

    /// <summary>BFS 깊이 레이아웃 + 선택지 핫스팟(시청자 전방 2 m · 좌우 0.8 m 간격) + v16.70 오케스트레이터 샷 플랜.</summary>
    public void Finalize(int start)
    {
        lock (_gate)
        {
            _start = Math.Clamp(start, 0, Math.Max(0, _nodeCount - 1));
            Span<int> q = stackalloc int[MaxNodes];
            Span<int> perDepth = stackalloc int[MaxNodes];
            perDepth.Clear();
            for (var i = 0; i < _nodeCount; i++) _nodes[i].Depth = -1;
            int h = 0, t = 0;
            if (_nodeCount > 0) { _nodes[_start].Depth = 0; q[t++] = _start; }
            while (h < t)
            {
                var n = _nodes[q[h++]];
                for (var k = 0; k < n.OutCount; k++)
                {
                    var to = _nodes[_edges[n.Out[k]].To];
                    if (to.Depth >= 0) continue;
                    to.Depth = n.Depth + 1; q[t++] = _edges[n.Out[k]].To;
                }
            }
            var maxD = 0;
            for (var i = 0; i < _nodeCount; i++) { if (_nodes[i].Depth < 0) _nodes[i].Depth = 0; maxD = Math.Max(maxD, _nodes[i].Depth); }
            for (var i = 0; i < _nodeCount; i++)
            {
                var n = _nodes[i];
                n.LayoutX = n.Depth;
                n.LayoutY = perDepth[n.Depth]++;
                for (var k = 0; k < n.OutCount; k++)
                    _edges[n.Out[k]].Hotspot = new Vector3((k - (n.OutCount - 1) * 0.5f) * 0.8f, 1.5f, -2f);
                var brief = AutonomousCreativeSingularityOrchestrator.Parse(n.Caption + " " + ((int)n.DurationSec).ToString(System.Globalization.CultureInfo.InvariantCulture) + "초");
                n.Shots = AutonomousCreativeSingularityOrchestrator.PlanShots(brief);
            }
            LastAction = "🎬 그래프 노드 " + _nodeCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · 간선 " + _edgeCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Restart();
    }

    /// <summary>기본 스토리 「심야 연구소」(합류 · 플래그 조건 · 3 엔딩 → 재시작).</summary>
    public void BuildDefaultStory()
    {
        Clear();
        ReadOnlySpan<float> calm = stackalloc float[] { 0.2f, 0.3f, 0.1f, 0.1f, 0.7f, 0.3f };
        ReadOnlySpan<float> eerie = stackalloc float[] { 0.05f, 0.7f, 0.4f, 0.1f, 0.8f, 0.2f };
        ReadOnlySpan<float> fear = stackalloc float[] { 0f, 0.8f, 0.9f, 0.2f, 0.2f, 0.1f };
        ReadOnlySpan<float> chase = stackalloc float[] { 0.1f, 1f, 0.6f, 0f, 0.1f, 0.2f };
        ReadOnlySpan<float> hope = stackalloc float[] { 0.8f, 0.3f, 0.1f, 0.2f, 0.3f, 0.5f };
        ReadOnlySpan<float> sad = stackalloc float[] { 0.1f, 0.2f, 0.1f, 1f, 0.1f, 0.4f };
        ReadOnlySpan<float> awe = stackalloc float[] { 0.5f, 0.2f, 0.1f, 0.1f, 0.6f, 1f };
        var n0 = AddNode("프롤로그", "심야 연구소 SF 미스터리 프롤로그", 1, 12f, calm);
        var n1 = AddNode("수상한 신호", "정체불명의 신호 서스펜스", 1, 10f, eerie);
        var n2 = AddNode("지하 실험실", "지하 실험실 조사 호러", 2, 10f, fear);
        var n3 = AddNode("탈출 추격", "복도 추격 액션", 2, 9f, chase);
        var n4 = AddNode("동료 구출", "갇힌 동료 구출 드라마", 2, 10f, hope);
        var n5 = AddNode("진실 폭로", "우주 신호의 진실 에픽", 3, 10f, awe, true);
        var n6 = AddNode("희생", "마지막 희생 멜랑콜리", 3, 10f, sad, true);
        var n7 = AddNode("생존", "새벽 생존 업리프팅", 3, 9f, hope, true);
        AddEdge(n0, n1, "📡 신호를 추적한다", 1f, new[] { "신호", "추적", "follow" });
        AddEdge(n1, n2, "🔦 지하로 내려간다", 0.6f, new[] { "지하", "조사", "내려" }, -1, 0);
        AddEdge(n1, n3, "🏃 도망친다", 0.4f, new[] { "도망", "run", "탈출" });
        AddEdge(n2, n4, "🤝 동료를 구한다", 0.5f, new[] { "구해", "구출", "동료" }, -1, 1);
        AddEdge(n2, n5, "🌌 신호의 근원을 연다", 0.5f, new[] { "열어", "근원", "진실" });
        AddEdge(n3, n4, "↩ 동료에게 돌아간다", 0.5f, new[] { "돌아", "동료", "구해" }, -1, 1);
        AddEdge(n3, n7, "🚪 비상구로 나간다", 0.5f, new[] { "비상구", "나가", "exit" });
        AddEdge(n4, n6, "🕯 동료를 위해 남는다", 0.4f, new[] { "남", "희생" });
        AddEdge(n4, n5, "🔬 함께 진실을 밝힌다", 0.4f, new[] { "진실", "함께" }, 0);
        AddEdge(n4, n7, "🌅 함께 탈출한다", 0.6f, new[] { "탈출", "함께", "나가" }, 1);
        Finalize(n0);
    }

    /// <summary>v16.54 인터랙티브 비디오 분기 프로그램 임포트(세그먼트 → 노드 · 선택지/다음 → 간선 · 캡션 어휘로 무드 추정).</summary>
    public void ImportBranchProgram(BranchProgram p, float fps = 30f)
    {
        Clear();
        Span<float> mood = stackalloc float[NeuralChoiceTreeEmotionEvaluator.Dim];
        for (var s = 0; s < p.Segments.Length; s++)
        {
            var seg = p.Segments[s];
            if (!NeuralChoiceTreeEmotionEvaluator.ScoreLexicon(seg.Caption, mood)) DefaultMood.CopyTo(mood);
            AddNode(seg.Id, seg.Caption, 1 + Math.Min(2, s * 3 / Math.Max(1, p.Segments.Length)), seg.Frames / fps, mood, seg.ChoiceCount == 0 && seg.Next < 0);
        }
        for (var s = 0; s < p.Segments.Length; s++)
        {
            var seg = p.Segments[s];
            for (var c = 0; c < seg.ChoiceCount; c++)
            {
                var ch = p.Choices[seg.ChoiceStart + c];
                AddEdge(s, ch.Target, ch.Label, ch.IsDefault ? 1f : 0.4f, new[] { ch.Label });
            }
            if (seg.ChoiceCount == 0 && seg.Next >= 0) AddEdge(s, seg.Next, "▶ 계속", 1f);
        }
        Finalize(p.Start);
    }

    // ══════════════════════ 재생 ══════════════════════

    public void Restart()
    {
        lock (_gate)
        {
            Array.Clear(_flags);
            PreviousNode = -1;
            Enter(_start);
            LastAction = "⏮ 처음부터";
        }
    }

    private void Enter(int node)
    {
        _current = node; _playhead = 0; _predicted = -1; _pendingOverride = -1; InDecisionWindow = false;
        var n = _nodes[node];
        n.Visits++;
        _eval.BeginChoices(n.OutCount);
        for (var k = 0; k < n.OutCount; k++)
        {
            var e = _edges[n.Out[k]];
            _eval.SetChoice(k, e.Hotspot, 0.35f, _nodes[e.To].Mood, e.Prior, e.Keywords, Eligible(e));
        }
        _eval.Update(_time, n.Mood); // 진입 즉시 사전 확률 · 도달 트리 갱신
        ComputeReach();
    }

    private bool Eligible(NarrativeEdge e) => e.RequireFlag < 0 || _flags[e.RequireFlag];

    // ══════════════════════ 관객 이벤트 ══════════════════════

    public bool Push(in AudienceEvent ev)
    {
        lock (_gate)
        {
            if (_qCount >= EventCap) { EventsDropped++; return false; }
            _queue[(_qHead + _qCount) % EventCap] = ev; _qCount++;
            return true;
        }
    }

    public bool PushVote(int choice) => Push(new AudienceEvent { Kind = AudienceEventKind.Vote, Arg = choice });
    public bool PushGaze(in XrGazeSample g) => Push(new AudienceEvent { Kind = AudienceEventKind.Gaze, Gaze = g });
    public bool PushBci(in EmotionState s) => Push(new AudienceEvent { Kind = AudienceEventKind.Bci, Bci = s });
    public bool PushText(string text, bool voice) => Push(new AudienceEvent { Kind = voice ? AudienceEventKind.Voice : AudienceEventKind.Text, Text = text });

    /// <summary>1-Click 감독 수동 개입: 현재 노드의 k 번째 선택지로 즉시 분기(다음 틱 · 조건 무시).</summary>
    public void Override(int choice)
    {
        lock (_gate) { if (_current >= 0 && (uint)choice < (uint)_nodes[_current].OutCount) _pendingOverride = choice; }
    }

    // ══════════════════════ 틱 ══════════════════════

    /// <summary>1틱(무할당): 이벤트 소화 → 평가 → 도달 확률 → 예측/프리페치 → 커밋 판정.</summary>
    public void Tick(float dt)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Cinema.Branch");
            if (_current < 0) return;
            var t0 = Stopwatch.GetTimestamp();
            _time += dt;
            while (_qCount > 0)
            {
                ref var ev = ref _queue[_qHead];
                switch (ev.Kind)
                {
                    case AudienceEventKind.Vote: _eval.PushVote(ev.Arg); break;
                    case AudienceEventKind.Gaze: _eval.PushGaze(ev.Gaze, _time); break;
                    case AudienceEventKind.Bci: _eval.PushBci(ev.Bci, _time); break;
                    default: if (ev.Text is not null) _eval.PushText(ev.Text, ev.Kind == AudienceEventKind.Voice, _time); break;
                }
                ev.Text = null;
                _qHead = (_qHead + 1) % EventCap; _qCount--;
                EventsProcessed++;
            }
            var node = _nodes[_current];
            _playhead += dt;
            _eval.Update(_time, node.Mood);
            ComputeReach();
            InDecisionWindow = _playhead >= node.DurationSec - DecisionWindowSec;

            var best = -1; var bp = -1f;
            for (var k = 0; k < node.OutCount; k++) if (_eval.IsEligible(k) && _eval.Probability(k) > bp) { bp = _eval.Probability(k); best = k; }
            if (InDecisionWindow && best >= 0 && _edges[node.Out[best]].To != _predicted)
            {
                _predicted = _edges[node.Out[best]].To; PrefetchSerial++;
            }

            if (_pendingOverride >= 0) { Commit(_pendingOverride, BranchTrigger.Override, t0); return; }
            if (node.OutCount == 0)
            {
                if (_playhead >= node.DurationSec && !DirectorHold) Commit(-1, BranchTrigger.Restart, t0);
                return;
            }
            if (!AutoBranch || DirectorHold)
            {
                if (_playhead >= node.DurationSec) _playhead = node.DurationSec - DecisionWindowSec * 0.5f; // 결정 창 루프
                return;
            }
            if (InDecisionWindow && best >= 0)
            {
                if (_eval.IntentShare(best) >= EarlyCommitShare && _eval.IntentSum >= 1.5f) { Commit(best, BranchTrigger.Audience, t0); return; }
                if (_eval.Votes(best) >= VoteQuorum && _eval.VoteShare(best) >= 0.6f) { Commit(best, BranchTrigger.Vote, t0); return; }
            }
            if (_playhead >= node.DurationSec && best >= 0) Commit(best, BranchTrigger.Timeout, t0);
        }
    }

    /// <summary>커밋(가드 진행 중이면 지연 · 반환 false).</summary>
    private bool Commit(int choice, BranchTrigger trig, long t0)
    {
        if (Guard is { Active: true }) { DeferredCommits++; return false; }
        var from = _current;
        int to, edge = -1; float p;
        if (choice < 0) { to = _start; p = 1f; Array.Clear(_flags); }
        else
        {
            edge = _nodes[from].Out[choice];
            var e = _edges[edge];
            to = e.To; p = _eval.Probability(choice);
            if (e.SetFlag >= 0) _flags[e.SetFlag] = true;
            e.Taken++;
        }
        var hit = _predicted == to;
        if (trig != BranchTrigger.Restart) { if (hit) PrefetchHits++; else PrefetchMisses++; }
        var us = (float)(Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0);
        LastDecisionMicros = us; MaxDecisionMicros = MathF.Max(MaxDecisionMicros, us);
        _history[_histCount % HistoryCap] = new BranchTransition(from, to, edge, trig, _time, p, us, hit);
        _histCount++;
        PreviousNode = from;
        Enter(to);
        var fade = trig == BranchTrigger.Override ? OverrideFadeMs : VideoFadeMs;
        Guard?.Begin(fade, trig == BranchTrigger.Override ? OverrideFadeMs : AudioFadeMs);
        Commits++; TransitionSerial++;
        LastAction = trig switch
        {
            BranchTrigger.Override => "🎛 감독 개입 분기",
            BranchTrigger.Audience => "🗣 관객 의도 조기 분기",
            BranchTrigger.Vote => "🗳 관객 투표 분기",
            BranchTrigger.Restart => "🔁 엔딩 → 재시작",
            _ => "⏱ 감정 확률 분기",
        };
        return true;
    }

    /// <summary>Horizon 단 도달 확률(1단 = 실시간 평가 확률 · 이후 = 감정 적합 + 사전 softmax).</summary>
    private void ComputeReach()
    {
        for (var i = 0; i < _nodeCount; i++) _nodes[i].Reach = 0;
        Array.Clear(_massA, 0, _nodeCount);
        var cur = _nodes[_current];
        for (var k = 0; k < cur.OutCount; k++) _massA[_edges[cur.Out[k]].To] += _eval.Probability(k);
        _nodes[_current].Reach = 1f;
        for (var d = 1; d <= Horizon; d++)
        {
            Array.Clear(_massB, 0, _nodeCount);
            for (var i = 0; i < _nodeCount; i++)
            {
                var m = _massA[i];
                if (m <= 1e-5f) continue;
                var n = _nodes[i];
                n.Reach = MathF.Min(1f, n.Reach + m);
                if (d == Horizon || n.OutCount == 0) continue;
                for (var k = 0; k < n.OutCount; k++)
                {
                    var e = _edges[n.Out[k]];
                    _tmpElig[k] = ReachableFlag(e);
                    _tmpLogit[k] = _eval.PriorLogit(_nodes[e.To].Mood, e.Prior);
                }
                NeuralChoiceTreeEmotionEvaluator.Softmax(_tmpLogit.AsSpan(0, n.OutCount), _tmpElig.AsSpan(0, n.OutCount), _tmpProb.AsSpan(0, n.OutCount));
                for (var k = 0; k < n.OutCount; k++) _massB[_edges[n.Out[k]].To] += m * _tmpProb[k];
            }
            Array.Copy(_massB, _massA, _nodeCount);
        }
    }

    /// <summary>플래그 조건 간선의 미래 충족 가능성(현재 미충족이어도 현 노드 선택지가 그 플래그를 세우면 도달 가능).</summary>
    private bool ReachableFlag(NarrativeEdge e)
    {
        if (e.RequireFlag < 0 || _flags[e.RequireFlag]) return true;
        var cur = _nodes[_current];
        for (var k = 0; k < cur.OutCount; k++) if (_edges[cur.Out[k]].SetFlag == e.RequireFlag) return true;
        return false;
    }

    /// <summary>현재 노드 내 샷 인덱스(오케스트레이터 샷 길이를 노드 길이로 정규화).</summary>
    public int CurrentShot
    {
        get
        {
            if (_current < 0) return -1;
            var n = _nodes[_current];
            if (n.Shots.Length == 0) return -1;
            float total = 0;
            for (var i = 0; i < n.Shots.Length; i++) total += n.Shots[i].DurationSec;
            var t = _playhead / n.DurationSec * total; float acc = 0;
            for (var i = 0; i < n.Shots.Length; i++) { acc += n.Shots[i].DurationSec; if (t < acc) return i; }
            return n.Shots.Length - 1;
        }
    }
}
