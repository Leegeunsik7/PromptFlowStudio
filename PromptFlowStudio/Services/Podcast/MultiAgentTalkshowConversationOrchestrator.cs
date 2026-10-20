using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Podcast;

public enum PanelPersona : byte { Moderator, Expert, Critic, Humorist, Optimist, Influencer }

public enum TalkStance : byte { Neutral, Pro, Con }

public enum TalkFormat : byte { Debate, Interview, Roundtable }

public enum TalkLineKind : byte { Opening, Introduce, Question, Redirect, Argument, Answer, Rebuttal, Support, Joke, Intervention, AudienceAnswer, Summary, Closing }

/// <summary>토크쇼 출연자(페르소나 · 입장 · 전문 키워드 · 음색 · 발화 통계).</summary>
public sealed class TalkAgent
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public PanelPersona Persona { get; set; }
    public TalkStance Stance { get; set; }
    public string[] Keywords { get; set; } = Array.Empty<string>();
    public uint Color { get; set; } = 0x64748B;
    public float PitchHz { get; set; } = 140;
    internal long Turns;
    internal double SpokenMs, LastSpokeMs = double.NegativeInfinity;
    public long TurnsTaken => Interlocked.Read(ref Turns);
    public double SpokenSeconds => SpokenMs / 1000;
}

/// <summary>토크쇼 발화(시각 · 길이 · 종류 · 화자 · 지목 대상 · 대사 · 표정 · 출처).</summary>
public sealed record TalkLine(double TimeMs, double DurationMs, TalkLineKind Kind, int Speaker, int Addressee, string Text, FacialEmotion Emotion, string Source);

/// <summary>
/// [PromptFlow Studio v16.49.0] 🎙️ 멀티 에이전트 토크쇼 &amp; 토론 대화 오케스트레이터.
///   ▸ MC 1 + 패널 2~4(지식인 · 비판가 · 유머 · 낙관론자 · 인플루언서) · 포맷(찬반 토론 / 인플루언서 인터뷰 / 라운드테이블) · 아젠다 순차 진행.
///   ▸ 발화권(Turn-taking): 연속 발화 금지 · 지목 대상 우선 · 반대 입장 반박 욕구 · 동일 입장 보충 · 발화 시간 공정성 · 페르소나 가중(비판가/유머 완급) · MC 개입 주기(인터뷰 = 매 답변).
///   ▸ 시청자 개입 주입(MC 소개 → 지목 패널 답변) · Gemini 페르소나 재작성(JSON · 데드라인 · 실패 시 BCL 템플릿).
///   ▸ Tick: 발화 중/대기 시 무할당(점수 계산은 고정 배열).
/// </summary>
public sealed class MultiAgentTalkshowConversationOrchestrator
{
    public const int MaxAgents = 5, MinPanel = 2, MaxPanel = 4, HistoryCap = 300, InjectCap = 6;
    public const double CharsPerSecond = 7.0, MinLineMs = 1500, GapMs = 350;

    private readonly Func<string, CancellationToken, Task<string?>>? _llm;
    private readonly object _gate = new();
    private readonly TalkAgent[] _agents = new TalkAgent[MaxAgents];
    private readonly List<TalkLine> _history = new(HistoryCap + 1);
    private readonly Queue<TalkLine> _injected = new(InjectCap + 1);
    private int _count, _agendaIdx = -1, _exchanges, _panelSinceMc, _lastPanel = -1, _addressee = -1, _phase, _seq;
    private double _speakingUntil, _lineEnd;
    private TalkLine? _current;
    private bool _live;
    private long _lines, _llmLines, _interventions;

    public MultiAgentTalkshowConversationOrchestrator(IServiceProvider sp)
        : this(sp.GetService(typeof(GeminiClient)) is GeminiClient g ? async (p, ct) => { if (!g.Enabled) return null; var r = await g.GenerateAsync(p, null, 0.8, ct).ConfigureAwait(false); return r.Ok ? r.Text : null; } : null) { }

    public MultiAgentTalkshowConversationOrchestrator(Func<string, CancellationToken, Task<string?>>? llm = null)
    {
        _llm = llm;
        Configure(DemoLineup());
    }

    public event Action? OnChange;
    public event Action<TalkLine>? OnLine;
    public event Action? OnEnded;

    public string Topic { get; set; } = DemoTopic;
    public List<string> Agenda { get; } = new(DemoAgenda);
    public TalkFormat Format { get; set; } = TalkFormat.Debate;
    public int ExchangesPerAgenda { get; set; } = 6;
    public int McEvery { get; set; } = 3;
    public bool UseLlm { get; set; } = true;
    public int LlmDeadlineMs { get; set; } = 5000;
    public bool LlmAvailable => _llm is not null;
    public bool IsLive => _live;
    public int AgentCount => _count;
    public int PanelCount => _count - 1;
    public TalkAgent Agent(int i) => _agents[i];
    public int AgendaIndex => _agendaIdx;
    public string? CurrentAgenda { get { var i = _agendaIdx; var a = Agenda; return (uint)i < (uint)a.Count ? a[i] : null; } }
    public int PendingInterventions { get { lock (_gate) return _injected.Count; } }
    public int LastPanelSpeaker => _lastPanel;
    public long LinesSpoken => Interlocked.Read(ref _lines);
    public long LlmLines => Interlocked.Read(ref _llmLines);
    public long Interventions => Interlocked.Read(ref _interventions);
    public TalkLine? Current => _current;
    public IReadOnlyList<TalkLine> History { get { lock (_gate) return _history.ToArray(); } }
    public Task? PendingDelivery { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>발화 중 화자(라인 길이 내) — 없으면 -1. 무할당.</summary>
    public int SpeakerAt(double nowMs) { var c = _current; return c is not null && nowMs < _lineEnd ? c.Speaker : -1; }

    /// <summary>현재 라인 진행률 0~1.</summary>
    public double LineProgress(double nowMs) { var c = _current; return c is null ? 1 : Math.Clamp((nowMs - c.TimeMs) / c.DurationMs, 0, 1); }

    // ══════════════════════ 구성 ══════════════════════

    /// <summary>라인업 구성: [0] = MC(Moderator) · 패널 2~4명.</summary>
    public void Configure(IReadOnlyList<TalkAgent> agents)
    {
        if (_live) throw new InvalidOperationException("방송 중에는 라인업을 변경할 수 없습니다.");
        if (agents.Count < MinPanel + 1 || agents.Count > MaxPanel + 1) throw new ArgumentException("패널은 2~4명이어야 합니다(MC 별도).", nameof(agents));
        if (agents[0].Persona != PanelPersona.Moderator) throw new ArgumentException("첫 번째 출연자는 MC(Moderator)여야 합니다.", nameof(agents));
        for (var i = 1; i < agents.Count; i++)
            if (agents[i].Persona == PanelPersona.Moderator) throw new ArgumentException("MC는 1명만 지정할 수 있습니다.", nameof(agents));
        lock (_gate)
        {
            Array.Clear(_agents);
            for (var i = 0; i < agents.Count; i++) _agents[i] = agents[i];
            _count = agents.Count;
        }
        Raise();
    }

    // ══════════════════════ 방송 제어 ══════════════════════

    public void Start(double nowMs)
    {
        if (Agenda.Count == 0) throw new InvalidOperationException("토론 아젠다가 비어 있습니다.");
        if (string.IsNullOrWhiteSpace(Topic)) throw new InvalidOperationException("토론 주제를 입력하세요.");
        lock (_gate)
        {
            _history.Clear(); _injected.Clear();
            for (var i = 0; i < _count; i++) { var a = _agents[i]; a.Turns = 0; a.SpokenMs = 0; a.LastSpokeMs = double.NegativeInfinity; }
            _agendaIdx = -1; _exchanges = 0; _panelSinceMc = 0; _lastPanel = -1; _addressee = -1; _phase = 0; _seq = 0;
            _speakingUntil = nowMs; _lineEnd = nowMs; _current = null;
            _live = true;
        }
        LastAction = "🔴 토크쇼 시작 · " + Topic;
        Raise();
    }

    public void Stop(double nowMs)
    {
        if (!_live) return;
        Say(nowMs, TalkLineKind.Closing, 0, -1, ClosingText(), FacialEmotion.HappySmile);
        EndShow();
    }

    private void EndShow()
    {
        lock (_gate) _live = false;
        LastAction = "⏹ 토크쇼 종료";
        Raise();
        try { OnEnded?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Talkshow.OnEnded", ex); }
    }

    /// <summary>시청자 개입 주입: MC 소개 → 지목 패널 답변(다음 발화 경계에서 우선 송출). 반환 = 수락 여부.</summary>
    public bool InjectIntervention(string viewer, string question, int target, double nowMs)
    {
        if (!_live || (uint)(target - 1) >= (uint)(_count - 1)) return false;
        var t = _agents[target];
        lock (_gate)
        {
            if (_injected.Count + 2 > InjectCap) return false;
            _injected.Enqueue(new TalkLine(nowMs, 0, TalkLineKind.Intervention, 0, target,
                "잠시 시청자 질문 하나 받겠습니다. " + viewer + "님의 질문, “" + question.Trim() + "” — " + t.Name + "님, 어떻게 보세요?", FacialEmotion.Surprised, "Audience"));
            _injected.Enqueue(new TalkLine(nowMs, 0, TalkLineKind.AudienceAnswer, target, 0, AudienceAnswerText(t, viewer, question), FacialEmotion.Curious, "BCL"));
        }
        Interlocked.Increment(ref _interventions);
        LastAction = "🙋 시청자 개입 채택 → " + t.Name;
        Raise();
        return true;
    }

    /// <summary>주기 틱: 주입 개입 → MC 진행 → 패널 발화권 순. 반환 = 발화 여부. 발화 외 무할당.</summary>
    public bool Tick(double nowMs)
    {
        if (!_live || nowMs < _speakingUntil) return false;
        TalkLine? inj = null;
        lock (_gate) if (_injected.Count > 0) inj = _injected.Dequeue();
        if (inj is not null)
        {
            if (inj.Kind == TalkLineKind.AudienceAnswer) { _lastPanel = inj.Speaker; _panelSinceMc = 0; }
            Say(nowMs, inj.Kind, inj.Speaker, inj.Addressee, inj.Text, inj.Emotion);
            return true;
        }
        switch (_phase)
        {
            case 0: _phase = 1; Say(nowMs, TalkLineKind.Opening, 0, -1, OpeningText(), FacialEmotion.HappySmile); return true;
            case 1: _phase = 2; Say(nowMs, TalkLineKind.Introduce, 0, -1, IntroduceText(), FacialEmotion.HappySmile); return true;
            case 3: Say(nowMs, TalkLineKind.Closing, 0, -1, ClosingText(), FacialEmotion.HappySmile); EndShow(); return true;
        }
        var mcEvery = Format == TalkFormat.Interview ? 1 : Math.Max(1, McEvery);
        if (_agendaIdx < 0 || _panelSinceMc >= mcEvery || _addressee < 0 && _exchanges >= ExchangesPerAgenda) { ModeratorTurn(nowMs); return true; }
        PanelTurn(nowMs);
        return true;
    }

    private void ModeratorTurn(double nowMs)
    {
        _panelSinceMc = 0;
        if (_agendaIdx < 0 || _exchanges >= ExchangesPerAgenda)
        {
            var next = _agendaIdx + 1;
            if (next >= Agenda.Count) { _phase = 3; _addressee = -1; Say(nowMs, TalkLineKind.Summary, 0, -1, SummaryText(), FacialEmotion.HappySmile); return; }
            _agendaIdx = next; _exchanges = 0;
            var first = FirstSpeakerForAgenda(next);
            _addressee = first;
            var name = _agents[first].Name;
            var text = next == 0
                ? "첫 번째 쟁점은 「" + Agenda[0] + "」입니다. " + name + "님, 먼저 의견 주시죠."
                : "좋습니다. 다음 쟁점으로 넘어가죠. 「" + Agenda[next] + "」 — " + name + "님부터 들어볼게요.";
            Say(nowMs, next == 0 ? TalkLineKind.Question : TalkLineKind.Redirect, 0, first, text, FacialEmotion.Curious);
            return;
        }
        var target = Format == TalkFormat.Interview && _seq % 3 != 0 ? Guest() : LeastSpokenPanel(_lastPanel);
        _addressee = target;
        Say(nowMs, TalkLineKind.Question, 0, target, FollowUpText(_agents[target], _seq), FacialEmotion.Curious);
    }

    private void PanelTurn(double nowMs)
    {
        var i = NextSpeaker(nowMs);
        var a = _agents[i];
        var last = _lastPanel;
        TalkLineKind kind; FacialEmotion emo; int to;
        if (i == _addressee) { kind = TalkLineKind.Answer; emo = FacialEmotion.Neutral; to = 0; _addressee = -1; }
        else if (a.Persona == PanelPersona.Humorist && _panelSinceMc >= 1 && (_seq & 1) == 0) { kind = TalkLineKind.Joke; emo = FacialEmotion.Giggle; to = last; }
        else if (last > 0 && Opposed(a.Stance, _agents[last].Stance)) { kind = TalkLineKind.Rebuttal; emo = FacialEmotion.Pout; to = last; }
        else if (last > 0 && a.Stance != TalkStance.Neutral && a.Stance == _agents[last].Stance) { kind = TalkLineKind.Support; emo = FacialEmotion.HappySmile; to = last; }
        else if (last > 0 && a.Persona == PanelPersona.Critic) { kind = TalkLineKind.Rebuttal; emo = FacialEmotion.Pout; to = last; }
        else { kind = TalkLineKind.Argument; emo = FacialEmotion.Neutral; to = -1; }
        _lastPanel = i; _panelSinceMc++; _exchanges++;
        Say(nowMs, kind, i, to, PanelText(kind, a, to > 0 ? _agents[to].Name : null, _seq), emo);
    }

    public static bool Opposed(TalkStance a, TalkStance b) => a == TalkStance.Pro && b == TalkStance.Con || a == TalkStance.Con && b == TalkStance.Pro;

    /// <summary>발화권 점수(연속 금지 · 지목 · 반박 욕구 · 공정성 · 페르소나) 최대 패널. 무할당.</summary>
    public int NextSpeaker(double nowMs)
    {
        var last = _lastPanel;
        double total = 0; var n = _count - 1;
        for (var i = 1; i < _count; i++) total += _agents[i].SpokenMs;
        var avg = n > 0 ? total / n : 0;
        var best = -1; var bestScore = double.NegativeInfinity;
        for (var i = 1; i < _count; i++)
        {
            var s = SpeakerScore(i, last, avg, nowMs);
            if (s > bestScore) { bestScore = s; best = i; }
        }
        return best;
    }

    public double SpeakerScore(int i, int last, double avgSpokenMs, double nowMs)
    {
        if (i == _addressee) return 10;
        if (i == last) return double.NegativeInfinity;
        var a = _agents[i];
        var idle = double.IsNegativeInfinity(a.LastSpokeMs) ? 1 : Math.Min(1, (nowMs - a.LastSpokeMs) / 15000);
        var s = idle;
        if (last > 0)
        {
            var ls = _agents[last].Stance;
            if (Opposed(a.Stance, ls)) s += 0.6;
            else if (a.Stance != TalkStance.Neutral && a.Stance == ls) s += 0.15;
            if (a.Persona == PanelPersona.Critic && ls != TalkStance.Con) s += 0.2;
        }
        if (a.Persona == PanelPersona.Humorist && _panelSinceMc >= 2) s += 0.35;
        if (a.Persona == PanelPersona.Expert) s += 0.1;
        if (Format == TalkFormat.Interview && i == Guest()) s += 0.5;
        if (avgSpokenMs > 0) s -= Math.Clamp(a.SpokenMs / avgSpokenMs - 1, -1, 1) * 0.5;
        return s;
    }

    private int LeastSpokenPanel(int exclude)
    {
        var best = -1; var min = double.MaxValue;
        for (var i = 1; i < _count; i++)
            if (i != exclude && _agents[i].SpokenMs < min) { min = _agents[i].SpokenMs; best = i; }
        return best < 0 ? 1 : best;
    }

    private int FirstSpeakerForAgenda(int agenda)
    {
        if (Format == TalkFormat.Interview) return Guest();
        if (Format == TalkFormat.Debate)
        {
            var want = (agenda & 1) == 0 ? TalkStance.Pro : TalkStance.Con;
            for (var i = 1; i < _count; i++) if (_agents[i].Stance == want) return i;
        }
        return 1 + agenda % (_count - 1);
    }

    /// <summary>인터뷰 게스트(인플루언서 우선, 없으면 첫 패널).</summary>
    public int Guest()
    {
        for (var i = 1; i < _count; i++) if (_agents[i].Persona == PanelPersona.Influencer) return i;
        return 1;
    }

    // ══════════════════════ 대사 템플릿(BCL) ══════════════════════

    private string OpeningText()
        => "안녕하세요, " + _agents[0].Name + "의 AI 토크쇼입니다. " + (Format switch { TalkFormat.Interview => "오늘은 특별한 게스트와 함께 「", TalkFormat.Roundtable => "오늘 라운드테이블 주제는 「", _ => "오늘의 찬반 토론 주제는 「" }) + Topic + "」입니다.";

    private string IntroduceText()
    {
        var sb = new StringBuilder("오늘 함께할 패널을 소개합니다. ");
        for (var i = 1; i < _count; i++)
        {
            var a = _agents[i];
            if (i > 1) sb.Append(", ");
            if (a.Title.Length > 0) sb.Append(a.Title).Append(' ');
            sb.Append(a.Name).Append("님");
            if (Format == TalkFormat.Debate && a.Stance != TalkStance.Neutral) sb.Append(a.Stance == TalkStance.Pro ? "(찬성)" : "(반대)");
        }
        return sb.Append(". 반갑습니다!").ToString();
    }

    private string SummaryText()
    {
        var pro = new StringBuilder(); var con = new StringBuilder();
        for (var i = 1; i < _count; i++)
        {
            var a = _agents[i];
            var sb = a.Stance == TalkStance.Pro ? pro : a.Stance == TalkStance.Con ? con : null;
            if (sb is null) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(a.Name);
        }
        var text = "오늘 「" + Topic + "」에 대해 " + Agenda.Count.ToString(CultureInfo.InvariantCulture) + "개 쟁점을 나눴습니다. ";
        if (pro.Length > 0 && con.Length > 0) text += "찬성 측 " + pro + "님과 반대 측 " + con + "님 모두 날카로운 논거를 보여주셨습니다.";
        else text += "패널 여러분의 다양한 시각 덕분에 풍성한 대화였습니다.";
        var iv = Interlocked.Read(ref _interventions);
        if (iv > 0) text += " 시청자 질문 " + iv.ToString(CultureInfo.InvariantCulture) + "건도 함께 다뤘습니다.";
        return text;
    }

    private string ClosingText() => "시청해 주셔서 감사합니다. 지금까지 " + _agents[0].Name + "이었습니다. 다음 회차에서 뵙겠습니다!";

    private string FollowUpText(TalkAgent t, int seq) => (seq % 4) switch
    {
        0 => t.Name + "님은 지금 논의를 어떻게 보세요?",
        1 => t.Name + "님, 조금 다른 시각에서 말씀해 주시겠어요?",
        2 => t.Name + "님, 「" + (CurrentAgenda ?? Topic) + "」에 대해 구체적인 근거나 사례가 있을까요?",
        _ => "흥미로운 대목이네요. " + t.Name + "님 생각은 어떠세요?",
    };

    private static string StancePrefix(TalkStance s, string agenda) => s switch
    {
        TalkStance.Pro => "저는 분명히 긍정적으로 봅니다. ",
        TalkStance.Con => "저는 「" + agenda + "」 문제에 신중해야 한다고 봅니다. ",
        _ => "",
    };

    public string PanelText(TalkLineKind kind, TalkAgent a, string? to, int seq)
    {
        var agenda = CurrentAgenda ?? Topic;
        return kind switch
        {
            TalkLineKind.Rebuttal => (to ?? "앞선 분") + "님 말씀엔 동의하기 어렵습니다. " + a.Persona switch
            {
                PanelPersona.Expert => "연구 결과는 조금 다르게 말합니다. 표본과 맥락을 같이 봐야 하죠.",
                PanelPersona.Humorist => "제 월급 통장만큼 낙관적인 말씀이네요. 현실은 좀 더 냉정합니다.",
                PanelPersona.Optimist => "우려는 이해하지만 그 안의 기회를 놓치고 계신 것 같아요.",
                PanelPersona.Influencer => "현장에서 제가 체감하는 반응은 정반대예요.",
                _ => "그 주장은 반례와 부작용을 충분히 고려하지 않았어요.",
            },
            TalkLineKind.Support => (to ?? "앞선 분") + "님 말씀에 덧붙이자면, " + (a.Persona == PanelPersona.Expert ? "실제 지표로도 같은 흐름이 확인됩니다." : "제 경험도 정확히 그렇습니다."),
            TalkLineKind.Joke => (seq % 3) switch
            {
                0 => "잠깐만요, 이러다 오늘 결론은 AI가 내리는 거 아니에요? (웃음)",
                1 => (to ?? "다들") + "님 말씀 들으니 저도 갑자기 진지해지네요. 이러면 제 캐릭터가 무너지는데요.",
                _ => "「" + agenda + "」 이야기만 나오면 다들 눈빛이 달라지시네요. 저만 점심 메뉴 생각했나요?",
            },
            TalkLineKind.Answer => StancePrefix(a.Stance, agenda) + PersonaView(a.Persona, agenda),
            _ => StancePrefix(a.Stance, agenda) + PersonaView(a.Persona, agenda),
        };
    }

    private static string PersonaView(PanelPersona p, string agenda) => p switch
    {
        PanelPersona.Expert => "「" + agenda + "」의 핵심은 측정 가능한 지표로 효과를 검증하는 데 있습니다. 데이터가 보여주는 흐름은 분명합니다.",
        PanelPersona.Critic => "「" + agenda + "」에는 아직 검증되지 않은 가정이 많습니다. 누가 책임지는지부터 따져야 합니다.",
        PanelPersona.Humorist => "솔직히 「" + agenda + "」, 어렵게 말하면 끝이 없는데요. 쉽게 말하면 결국 사람이 재밌어야 오래 갑니다.",
        PanelPersona.Optimist => "「" + agenda + "」는 위기이자 기회입니다. 새로운 시장과 역할이 분명히 생겨날 거예요.",
        PanelPersona.Influencer => "제 구독자분들 반응을 보면 「" + agenda + "」는 이미 일상이에요. 현장의 목소리를 들어봐야 합니다.",
        _ => "「" + agenda + "」에 대해 다양한 의견을 들어보겠습니다.",
    };

    private string AudienceAnswerText(TalkAgent t, string viewer, string question)
    {
        var view = t.Persona switch
        {
            PanelPersona.Expert => "근거부터 말씀드리면, 지금 데이터로는 단정하기보다 조건부로 보는 게 맞습니다.",
            PanelPersona.Critic => "날카로운 지적입니다. 바로 그 부분이 제가 오늘 계속 문제 삼은 지점이에요.",
            PanelPersona.Humorist => "제가 받을 줄 몰랐는데요(웃음). 진지하게 답하면, 결국 사람 중심으로 봐야 합니다.",
            PanelPersona.Optimist => "충분히 걱정하실 만해요. 그래도 저는 해법이 있다고 봅니다.",
            PanelPersona.Influencer => "저도 현장에서 정말 많이 받는 질문이에요. 직접 겪어본 입장에서 말씀드릴게요.",
            _ => "좋은 질문입니다.",
        };
        return viewer + "님, 좋은 질문 감사합니다. " + view;
    }

    // ══════════════════════ 발화 ══════════════════════

    public static double EstimateMs(string text) => Math.Max(MinLineMs, text.Length / CharsPerSecond * 1000);

    private void Say(double nowMs, TalkLineKind kind, int speaker, int to, string text, FacialEmotion emo)
    {
        var dur = EstimateMs(text);
        var line = new TalkLine(nowMs, dur, kind, speaker, to, text, emo, "BCL");
        var a = _agents[speaker];
        lock (_gate)
        {
            _history.Add(line);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
            _current = line;
            _lineEnd = nowMs + dur; _speakingUntil = nowMs + dur + GapMs;
            a.SpokenMs += dur; a.LastSpokeMs = nowMs;
            _seq++;
        }
        Interlocked.Increment(ref a.Turns);
        Interlocked.Increment(ref _lines);
        LastAction = "🎙 " + a.Name + " · " + text;
        try { OnLine?.Invoke(line); } catch (Exception ex) { PromptFlowStudio.Program.Log("Talkshow.OnLine", ex); }
        if (_llm is not null && UseLlm && speaker > 0 && kind is TalkLineKind.Argument or TalkLineKind.Answer or TalkLineKind.Rebuttal or TalkLineKind.Support or TalkLineKind.AudienceAnswer)
            PendingDelivery = RewriteAsync(line);
        Raise();
    }

    public static bool TryParseLineJson(string? text, out string line)
    {
        line = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = GeminiClient.StripFence(text);
        var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return false;
        try
        {
            using var doc = JsonDocument.Parse(s[a..(b + 1)]);
            if (!doc.RootElement.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return false;
            line = t.GetString()!.Trim();
            return line.Length is > 0 and <= 220;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Gemini 페르소나 재작성 → 기록 교체(실패 시 BCL 유지).</summary>
    private async Task RewriteAsync(TalkLine line)
    {
        try
        {
            var a = _agents[line.Speaker];
            var prev = "";
            lock (_gate) { var i = _history.LastIndexOf(line); if (i > 0) prev = _history[i - 1].Text; }
            using var d = new CancellationTokenSource(LlmDeadlineMs);
            var prompt = "You are " + a.Name + " (" + PersonaLabel(a.Persona) + ", stance " + StanceLabel(a.Stance) + ") on a Korean AI talk show. Topic: " + Topic + ". Current issue: " + (CurrentAgenda ?? Topic)
                + ". Previous line: " + prev + "\nRewrite your line below in natural spoken Korean, max 140 chars, keep your persona and stance, address the same person, no new statistics. Reply ONLY JSON {\"text\":\"...\"}.\nLine: " + line.Text;
            var text = await _llm!(prompt, d.Token).ConfigureAwait(false);
            if (!TryParseLineJson(text, out var t)) return;
            var final = line with { Text = t, Source = "Gemini" };
            lock (_gate)
            {
                var i = _history.LastIndexOf(line);
                if (i >= 0) _history[i] = final;
                if (ReferenceEquals(_current, line)) _current = final;
            }
            Interlocked.Increment(ref _llmLines);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException) { PromptFlowStudio.Program.Log("Talkshow.Rewrite", ex); }
        Raise();
    }

    public static string KindLabel(TalkLineKind k) => k switch
    {
        TalkLineKind.Opening => "오프닝", TalkLineKind.Introduce => "소개", TalkLineKind.Question => "질문", TalkLineKind.Redirect => "전환", TalkLineKind.Argument => "주장",
        TalkLineKind.Answer => "답변", TalkLineKind.Rebuttal => "반박", TalkLineKind.Support => "보충", TalkLineKind.Joke => "유머", TalkLineKind.Intervention => "시청자",
        TalkLineKind.AudienceAnswer => "응답", TalkLineKind.Summary => "정리", _ => "클로징",
    };

    public static string PersonaLabel(PanelPersona p) => p switch
    {
        PanelPersona.Moderator => "MC", PanelPersona.Expert => "지식인", PanelPersona.Critic => "비판가", PanelPersona.Humorist => "유머 패널", PanelPersona.Optimist => "낙관론자", _ => "인플루언서",
    };

    public static string StanceLabel(TalkStance s) => s switch { TalkStance.Pro => "찬성", TalkStance.Con => "반대", _ => "중립" };

    public const string DemoTopic = "AI가 인간 크리에이터를 대체할 것인가";

    public static readonly string[] DemoAgenda = { "창작의 독창성과 저작권", "일자리와 경제적 영향", "AI 시대 크리에이터의 생존 전략" };

    public static List<TalkAgent> DemoLineup() => new()
    {
        new TalkAgent { Name = "진행자 한나", Title = "MC", Persona = PanelPersona.Moderator, Stance = TalkStance.Neutral, Color = 0xA855F7, PitchHz = 210, Keywords = new[] { "진행", "토론" } },
        new TalkAgent { Name = "김도현", Title = "데이터 과학자", Persona = PanelPersona.Expert, Stance = TalkStance.Pro, Color = 0x0EA5E9, PitchHz = 125, Keywords = new[] { "AI", "데이터", "연구", "기술", "모델", "알고리즘", "생산성" } },
        new TalkAgent { Name = "박서연", Title = "문화 칼럼니스트", Persona = PanelPersona.Critic, Stance = TalkStance.Con, Color = 0xEF4444, PitchHz = 195, Keywords = new[] { "저작권", "윤리", "규제", "일자리", "법", "책임", "창작" } },
        new TalkAgent { Name = "이준", Title = "코미디언", Persona = PanelPersona.Humorist, Stance = TalkStance.Neutral, Color = 0xF59E0B, PitchHz = 150, Keywords = new[] { "웃음", "예능", "문화", "밈", "일상", "재미" } },
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Talkshow.OnChange", ex); } }
}
