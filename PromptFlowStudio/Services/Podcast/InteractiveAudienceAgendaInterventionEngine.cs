using PromptFlowStudio.Services.Newsroom;

namespace PromptFlowStudio.Services.Podcast;

public enum AudienceKind : byte { Question, Challenge, Opinion, Spam }

public enum InterventionStatus : byte { Pending, Adopted, Rejected, Expired }

/// <summary>시청자 개입 항목(정규화 바이그램 · 공감 수 · 후원 · 관련도 · 지목 패널 · 상태).</summary>
public sealed class AudienceIntervention
{
    public int Id { get; init; }
    public string Viewer { get; init; } = "";
    public string Text { get; init; } = "";
    public AudienceKind Kind { get; init; }
    public double AtMs { get; init; }
    internal HashSet<int> Grams { get; init; } = new();
    public int Votes { get; internal set; } = 1;
    public decimal Donation { get; internal set; }
    public double Relevance { get; internal set; }
    public double Score { get; internal set; }
    public int Target { get; internal set; } = -1;
    public InterventionStatus Status { get; internal set; }
    public double DecidedMs { get; internal set; }
}

/// <summary>
/// [PromptFlow Studio v16.49.0] 🙋 실시간 시청자 개입 &amp; 동적 아젠다 브레이크인 인젝터.
///   ▸ 수신 분류(질문 / 핵심 지적 / 의견 / 스팸 — 링크 · 반복 문자 · 금칙어 · 과단문) · 바이그램 유사 질문 병합(공감 +1 · 이미 채택된 질문 재채택 차단) · 의견은 유사 질문 공감으로 흡수 · 대기 상한 · 만료.
///   ▸ 채택 점수 = 주제/현재 쟁점 관련도 · 공감 · 후원 · 최신성 · 지적 가중 → 임계 이상 · 최소 간격 · 오케스트레이터 대기 개입 0일 때 MC가 자연스럽게 브레이크인.
///   ▸ 지목 라우팅: 패널 전문 키워드 관련도 + 지적은 직전 발언자(반론 기회) + 발화 공정성 · 하이라이트 마킹 · 수동 채택/거절.
///   ▸ Tick: 채택 외 무할당(점수 재계산은 상한 목록 순회).
/// </summary>
public sealed class InteractiveAudienceAgendaInterventionEngine : IDisposable
{
    public const int QueueCap = 24, DecidedCap = 60;
    public const double MergeOverlap = 0.7;

    private static readonly string[] Banned = { "시발", "씨발", "병신", "개새", "꺼져", "fuck", "shit" };
    private static readonly string[] ChallengeCues = { "틀렸", "아닌데", "아니잖", "반박", "근거", "말이 안", "동의 못", "동의할 수 없", "모순", "과장", "wrong", "disagree" };
    private static readonly string[] QuestionCues = { "까요", "나요", "인가요", "어떻게", "왜", "뭐", "무엇", "어떤", "궁금", "how", "why", "what" };

    private readonly MultiAgentTalkshowConversationOrchestrator _talk;
    private readonly AutonomousPodcastVideoMultiCamEngine? _cam;
    private readonly object _gate = new();
    private readonly List<AudienceIntervention> _pending = new(QueueCap + 1);
    private readonly List<AudienceIntervention> _decided = new(DecidedCap + 1);
    private readonly HashSet<int>?[] _agentGrams = new HashSet<int>?[MultiAgentTalkshowConversationOrchestrator.MaxAgents];
    private readonly TalkAgent?[] _gramsOwner = new TalkAgent?[MultiAgentTalkshowConversationOrchestrator.MaxAgents];
    private HashSet<int> _topicGrams = new(), _agendaGrams = new();
    private string? _topicSrc, _agendaSrc;
    private int _nextId;
    private double _lastAdopt = double.NegativeInfinity;
    private long _received, _spam, _merged, _adopted, _expired;

    public InteractiveAudienceAgendaInterventionEngine(IServiceProvider sp)
        : this((MultiAgentTalkshowConversationOrchestrator)sp.GetService(typeof(MultiAgentTalkshowConversationOrchestrator))!,
               sp.GetService(typeof(AutonomousPodcastVideoMultiCamEngine)) as AutonomousPodcastVideoMultiCamEngine) { }

    public InteractiveAudienceAgendaInterventionEngine(MultiAgentTalkshowConversationOrchestrator talk, AutonomousPodcastVideoMultiCamEngine? cam = null)
    {
        _talk = talk; _cam = cam;
    }

    public event Action? OnChange;

    public bool AutoAdopt { get; set; } = true;
    public double AdoptThreshold { get; set; } = 0.3;
    public double MinGapMs { get; set; } = 20000;
    public double ExpireMs { get; set; } = 120000;
    public long Received => Interlocked.Read(ref _received);
    public long SpamBlocked => Interlocked.Read(ref _spam);
    public long Merged => Interlocked.Read(ref _merged);
    public long Adopted => Interlocked.Read(ref _adopted);
    public long Expired => Interlocked.Read(ref _expired);
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public IReadOnlyList<AudienceIntervention> Pending { get { lock (_gate) return _pending.ToArray(); } }
    public IReadOnlyList<AudienceIntervention> Decided { get { lock (_gate) return _decided.ToArray(); } }
    public string? LastAction { get; private set; }

    // ══════════════════════ 분류 ══════════════════════

    public static AudienceKind Classify(string text)
    {
        var t = text.Trim();
        var letters = 0;
        foreach (var c in t) if (char.IsLetterOrDigit(c)) letters++;
        if (letters < 4) return AudienceKind.Spam;
        var lower = t.ToLowerInvariant();
        if (lower.Contains("http", StringComparison.Ordinal) || lower.Contains("www.", StringComparison.Ordinal) || lower.Contains(".com", StringComparison.Ordinal)) return AudienceKind.Spam;
        var run = 1;
        for (var i = 1; i < t.Length; i++) { run = t[i] == t[i - 1] ? run + 1 : 1; if (run >= 6) return AudienceKind.Spam; }
        if (ContainsAny(lower, Banned)) return AudienceKind.Spam;
        if (ContainsAny(lower, ChallengeCues)) return AudienceKind.Challenge;
        if (t.Contains('?') || ContainsAny(lower, QuestionCues)) return AudienceKind.Question;
        return AudienceKind.Opinion;
    }

    private static bool ContainsAny(string s, string[] keys) { foreach (var k in keys) if (s.Contains(k, StringComparison.Ordinal)) return true; return false; }

    /// <summary>시청자 메시지 수신 → 스팸 차단 · 유사 병합(공감) · 대기열(상한 초과 시 최저 점수 드롭). 반환 = 분류.</summary>
    public AudienceKind Ingest(string viewer, string text, double nowMs, decimal donation = 0)
    {
        Interlocked.Increment(ref _received);
        var kind = Classify(text);
        if (kind == AudienceKind.Spam) { Interlocked.Increment(ref _spam); LastAction = "🚫 스팸 차단 · " + viewer; Raise(); return kind; }
        var grams = RealtimeNewsFeedFactCheckEngine.TitleGrams(text);
        lock (_gate)
        {
            foreach (var d in _decided)
                if (d.Status == InterventionStatus.Adopted && RealtimeNewsFeedFactCheckEngine.Overlap(grams, d.Grams) >= MergeOverlap)
                {
                    d.Votes++;
                    Interlocked.Increment(ref _merged);
                    LastAction = "♻ 이미 다룬 질문 · " + d.Text;
                    Raise();
                    return kind;
                }
            foreach (var p in _pending)
                if (RealtimeNewsFeedFactCheckEngine.Overlap(grams, p.Grams) >= MergeOverlap)
                {
                    p.Votes++; p.Donation += donation;
                    Interlocked.Increment(ref _merged);
                    LastAction = "👍 공감 +1 · " + p.Text;
                    Raise();
                    return kind;
                }
            if (kind == AudienceKind.Opinion) { LastAction = "💬 의견 · " + viewer; Raise(); return kind; }
            var item = new AudienceIntervention { Id = ++_nextId, Viewer = viewer.Trim().Length > 0 ? viewer.Trim() : "익명", Text = text.Trim(), Kind = kind, AtMs = nowMs, Grams = grams, Donation = donation };
            _pending.Add(item);
            if (_pending.Count > QueueCap)
            {
                var worst = 0;
                for (var i = 1; i < _pending.Count; i++) if (ScoreOf(_pending[i], nowMs) < ScoreOf(_pending[worst], nowMs)) worst = i;
                Decide(_pending[worst], InterventionStatus.Expired, nowMs);
                _pending.RemoveAt(worst);
            }
        }
        LastAction = (kind == AudienceKind.Challenge ? "❗ 핵심 지적 · " : "❓ 질문 · ") + viewer;
        Raise();
        return kind;
    }

    // ══════════════════════ 점수 · 라우팅 ══════════════════════

    private void RefreshGrams()
    {
        var topic = _talk.Topic;
        if (!ReferenceEquals(topic, _topicSrc)) { _topicSrc = topic; _topicGrams = RealtimeNewsFeedFactCheckEngine.TitleGrams(topic ?? ""); }
        var agenda = _talk.CurrentAgenda;
        if (!ReferenceEquals(agenda, _agendaSrc)) { _agendaSrc = agenda; _agendaGrams = RealtimeNewsFeedFactCheckEngine.TitleGrams(agenda ?? ""); }
        for (var i = 1; i < _talk.AgentCount; i++)
        {
            var a = _talk.Agent(i);
            if (ReferenceEquals(_gramsOwner[i], a) && _agentGrams[i] is not null) continue;
            _gramsOwner[i] = a;
            _agentGrams[i] = RealtimeNewsFeedFactCheckEngine.TitleGrams(string.Join(' ', a.Keywords) + " " + a.Title);
        }
    }

    /// <summary>관련도 = max(주제, 현재 쟁점, 패널 전문 키워드) 바이그램 중첩.</summary>
    public double RelevanceOf(AudienceIntervention it)
    {
        var r = Math.Max(RealtimeNewsFeedFactCheckEngine.Overlap(it.Grams, _topicGrams), RealtimeNewsFeedFactCheckEngine.Overlap(it.Grams, _agendaGrams));
        for (var i = 1; i < _talk.AgentCount; i++)
            if (_agentGrams[i] is { } g) r = Math.Max(r, 0.8 * KeywordHit(it.Grams, g));
        return r;
    }

    private static double KeywordHit(HashSet<int> q, HashSet<int> kw)
    {
        if (q.Count == 0 || kw.Count == 0) return 0;
        var hit = 0;
        foreach (var g in q) if (kw.Contains(g)) hit++;
        return Math.Min(1, hit / 3.0);
    }

    /// <summary>채택 점수: 관련도 0.45 · 공감 0.25 · 후원 0.2 · 최신성 0.1 · 지적 +0.05.</summary>
    public double ScoreOf(AudienceIntervention it, double nowMs)
    {
        var rel = RelevanceOf(it);
        it.Relevance = rel;
        var votes = Math.Min(1, Math.Log2(1 + it.Votes) / 3);
        var don = Math.Min(1, (double)it.Donation / 10000);
        var fresh = Math.Clamp(1 - (nowMs - it.AtMs) / ExpireMs, 0, 1);
        var s = 0.45 * rel + 0.25 * votes + 0.2 * don + 0.1 * fresh + (it.Kind == AudienceKind.Challenge ? 0.05 : 0);
        it.Score = s;
        return s;
    }

    /// <summary>지목 패널: 전문 키워드 관련도 + 지적이면 직전 발언자 +0.5 + 공정성(최소 발화 +0.1).</summary>
    public int RouteTarget(AudienceIntervention it)
    {
        RefreshGrams();
        var n = _talk.AgentCount;
        var last = _talk.LastPanelSpeaker;
        int best = 1, least = 1; var bestScore = double.NegativeInfinity;
        for (var i = 1; i < n; i++) if (_talk.Agent(i).SpokenSeconds < _talk.Agent(least).SpokenSeconds) least = i;
        for (var i = 1; i < n; i++)
        {
            var s = _agentGrams[i] is { } g ? KeywordHit(it.Grams, g) : 0;
            if (it.Kind == AudienceKind.Challenge && i == last) s += 0.5;
            if (_talk.Agent(i).Persona == PanelPersona.Expert && it.Kind == AudienceKind.Question) s += 0.05;
            if (i == least) s += 0.1;
            if (s > bestScore) { bestScore = s; best = i; }
        }
        return best;
    }

    // ══════════════════════ 채택 ══════════════════════

    /// <summary>주기 틱: 만료 정리 → 조건 충족 시 최고 점수 자동 채택. 반환 = 채택 여부.</summary>
    public bool Tick(double nowMs)
    {
        AudienceIntervention? best = null;
        lock (_gate)
        {
            if (_pending.Count == 0) return false;
            RefreshGrams();
            var bestScore = double.NegativeInfinity;
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var it = _pending[i];
                if (nowMs - it.AtMs > ExpireMs) { Decide(it, InterventionStatus.Expired, nowMs); _pending.RemoveAt(i); Interlocked.Increment(ref _expired); continue; }
                var s = ScoreOf(it, nowMs);
                if (s > bestScore) { bestScore = s; best = it; }
            }
            if (!AutoAdopt || best is null || bestScore < AdoptThreshold || !_talk.IsLive || _talk.PendingInterventions > 0 || nowMs - _lastAdopt < MinGapMs || _talk.AgendaIndex < 0) return false;
        }
        return Adopt(best.Id, nowMs);
    }

    /// <summary>채택: 지목 라우팅 → 오케스트레이터 주입 · 하이라이트 마킹. 수동 채택은 간격/임계 무시.</summary>
    public bool Adopt(int id, double nowMs)
    {
        AudienceIntervention? it;
        lock (_gate)
        {
            it = Find(id);
            if (it is null) return false;
            it.Target = RouteTarget(it);
            if (!_talk.InjectIntervention(it.Viewer, it.Text, it.Target, nowMs)) return false;
            _pending.Remove(it);
            Decide(it, InterventionStatus.Adopted, nowMs);
            _lastAdopt = nowMs;
        }
        Interlocked.Increment(ref _adopted);
        _cam?.MarkHighlight(HighlightReason.Intervention, it.Target, nowMs, 1000, 15000);
        LastAction = "✅ 채택 · " + it.Viewer + " → " + _talk.Agent(it.Target).Name;
        Raise();
        return true;
    }

    public bool Reject(int id, double nowMs)
    {
        lock (_gate)
        {
            var it = Find(id);
            if (it is null) return false;
            _pending.Remove(it);
            Decide(it, InterventionStatus.Rejected, nowMs);
        }
        LastAction = "✖ 거절";
        Raise();
        return true;
    }

    public void Clear() { lock (_gate) { _pending.Clear(); _decided.Clear(); } _lastAdopt = double.NegativeInfinity; Raise(); }

    private AudienceIntervention? Find(int id) { foreach (var p in _pending) if (p.Id == id) return p; return null; }

    private void Decide(AudienceIntervention it, InterventionStatus st, double nowMs)
    {
        it.Status = st; it.DecidedMs = nowMs;
        _decided.Add(it);
        if (_decided.Count > DecidedCap) _decided.RemoveAt(0);
    }

    public static string KindLabel(AudienceKind k) => k switch { AudienceKind.Question => "❓ 질문", AudienceKind.Challenge => "❗ 지적", AudienceKind.Opinion => "💬 의견", _ => "🚫 스팸" };

    public static string StatusLabel(InterventionStatus s) => s switch { InterventionStatus.Adopted => "채택", InterventionStatus.Rejected => "거절", InterventionStatus.Expired => "만료", _ => "대기" };

    public static readonly (string Viewer, string Text)[] DemoChat =
    {
        ("코딩하는고양이", "AI가 만든 그림의 저작권은 누구한테 있나요?"),
        ("현직디자이너", "데이터 과학자님 말씀은 근거가 부족한 것 같아요. 실제 일자리 통계는 반대 아닌가요?"),
        ("주부유튜버", "AI 시대에 크리에이터는 어떤 기술을 배워야 하나요?"),
        ("광고봇", "지금 바로 http://spam.example 클릭!!!!!!"),
        ("새벽감성", "저는 그냥 재밌게 보고 있어요"),
        ("익명의팬", "AI 그림 저작권은 누구에게 있는 건가요?"),
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Audience.OnChange", ex); } }

    public void Dispose() => OnChange = null;
}
