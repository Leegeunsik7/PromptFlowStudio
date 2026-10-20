using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.VTuber;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.EduTech;

public enum TutorStyle : byte { Friendly, Socratic, Concise, Storyteller }

public enum TutorMode : byte { Foundation, Standard, Advanced }

/// <summary>AI 튜터 페르소나 설정(이름 · 화법 · 비유 도메인 · 난이도 · 음성).</summary>
public sealed class TutorPersonaConfig
{
    public string Name { get; set; } = "하나 쌤";
    public TutorStyle Style { get; set; } = TutorStyle.Friendly;
    public string AnalogyDomain { get; set; } = "일상";
    public string Speech { get; set; } = "친절한 존댓말";
    public NarrationTone Voice { get; set; } = NarrationTone.Bright;
    public bool AutoRemediate { get; set; } = true;
    public bool UseLlm { get; set; } = true;
    public bool UseAvatar { get; set; } = true;
}

/// <summary>튜터 응답(본문 · 비유 · 확인 질문 · 표정 · RAG 근거 · 입모양 큐).</summary>
public sealed record TutorReply(string LearnerId, string Concept, TutorMode Mode, string Text, string Analogy, string? CheckQuestion, FacialEmotion Emotion,
    IReadOnlyList<RecalledMemory> Recalled, string Source, VisemeCue[] Visemes, double DurationSec, DateTimeOffset At);

/// <summary>
/// [PromptFlow Studio v16.46.0] 🧑‍🏫 맞춤형 AI 튜터 음성 &amp; 실시간 Q&amp;A 코파일럿.
///   ▸ RAG 기억(v16.38.0 VTuberPersonaMemoryEngine): 강좌 챕터 요약 · 퀴즈 해설을 튜터 세계관(Lore)으로 색인 · 학습자 오답/질문을 학습자별(Fact/Chat) 기억으로 축적 → 질의 시 상위 K 근거 회수.
///   ▸ 적응형 보충 강의: 오답 채점 이벤트 → BKT 숙달도별 모드(기초 = 쉬운 문장 + 비유 + 확인 질문 · 표준 · 심화 = 간결) · 비유 도메인(일상/요리/스포츠/게임) 은유 · 페르소나 화법.
///   ▸ Gemini 재작성(JSON 스키마 검증 · 데드라인 · 실패 시 BCL 템플릿 1초 내 폴백) · 한국어 조사 자동 선택.
///   ▸ 립싱크 아바타(v16.20.0 입모양 큐 · v16.38.0 라이브 브릿지): TTS(선택) → PrepareSpeechAsync → Speak · 무브릿지 시 대사 추정 큐 · 입 벌림 조회 무할당.
/// </summary>
public sealed class AdaptiveAiTutorCoPilot : IDisposable
{
    public const int HistoryCap = 100, RecallK = 4;

    private static readonly Dictionary<string, string[]> KnownAnalogies = new(StringComparer.Ordinal)
    {
        // 순서: 일상, 요리, 스포츠, 게임
        ["지도학습"] = new[] { "정답지가 있는 문제집으로 공부하는 것", "레시피와 완성 사진을 보며 요리를 배우는 것", "코치가 매 동작마다 맞다/틀리다 알려주는 훈련", "공략집을 보며 퀘스트 클리어 방법을 익히는 것" },
        ["과적합"] = new[] { "시험 족보만 통째로 외워서 새 문제는 못 푸는 학생", "한 손님 입맛에만 맞춘 요리라 다른 손님은 싫어하는 것", "연습 경기 상대 버릇만 외워서 실전에서 지는 팀", "한 맵 공략만 외워서 새 맵에선 헤매는 플레이어" },
        ["경사하강법"] = new[] { "안개 낀 산에서 발밑 경사만 느끼며 한 걸음씩 내려가는 등산객", "간을 보며 소금을 조금씩 줄여 가는 요리사", "슛 각도를 조금씩 고쳐 가며 골대를 찾는 연습", "조금씩 레벨업하며 보스 공략 최적 루트를 찾는 것" },
        ["신경망"] = new[] { "여러 단계 심사위원이 점수를 넘겨주며 최종 판정을 내리는 오디션", "재료 손질 → 조리 → 플레이팅 단계를 거치는 주방 라인", "수비 → 미드필드 → 공격으로 공을 연결하는 팀 전술", "스킬 트리를 단계별로 찍어 강해지는 캐릭터" },
        ["학습률"] = new[] { "계단을 몇 칸씩 오를지 정하는 보폭", "불 세기 조절 — 너무 세면 타고 약하면 안 익는 것", "드리블 보폭 — 너무 크면 공을 놓치는 것", "한 번에 투자하는 스탯 포인트 양" },
    };

    private static readonly string[] Domains = { "일상", "요리", "스포츠", "게임" };

    private readonly Func<string, CancellationToken, Task<string?>>? _llm;
    private readonly VTuberPersonaMemoryEngine? _memory;
    private readonly InteractiveEduVideoQuizEngine? _quiz;
    private readonly LiveVTuberBroadcastingBridge? _bridge;
    private readonly AiVoiceoverTtsService? _tts;
    private readonly List<TutorReply> _history = new();
    private readonly object _gate = new();
    private string? _personaId, _courseId;
    private long _remediations, _questions, _llmReplies, _spoken;

    public AdaptiveAiTutorCoPilot(IServiceProvider sp)
        : this(sp.GetService(typeof(GeminiClient)) is GeminiClient g ? async (p, ct) => { if (!g.Enabled) return null; var r = await g.GenerateAsync(p, null, 0.5, ct).ConfigureAwait(false); return r.Ok ? r.Text : null; } : null,
               sp.GetService(typeof(VTuberPersonaMemoryEngine)) as VTuberPersonaMemoryEngine, sp.GetService(typeof(InteractiveEduVideoQuizEngine)) as InteractiveEduVideoQuizEngine,
               sp.GetService(typeof(LiveVTuberBroadcastingBridge)) as LiveVTuberBroadcastingBridge, sp.GetService(typeof(AiVoiceoverTtsService)) as AiVoiceoverTtsService) { }

    public AdaptiveAiTutorCoPilot(Func<string, CancellationToken, Task<string?>>? llm, VTuberPersonaMemoryEngine? memory, InteractiveEduVideoQuizEngine? quiz,
        LiveVTuberBroadcastingBridge? bridge = null, AiVoiceoverTtsService? tts = null)
    {
        _llm = llm; _memory = memory; _quiz = quiz; _bridge = bridge; _tts = tts;
        if (_quiz is not null) _quiz.OnGraded += OnGraded;
    }

    public event Action? OnChange;
    public event Action<TutorReply>? OnReply;

    public TutorPersonaConfig Persona { get; } = new();
    public int LlmDeadlineMs { get; set; } = 6000;
    public bool LlmAvailable => _llm is not null;
    public bool RagAvailable => _memory is not null;
    public string? PersonaId => _personaId;
    public long Remediations => Interlocked.Read(ref _remediations);
    public long Questions => Interlocked.Read(ref _questions);
    public long LlmReplies => Interlocked.Read(ref _llmReplies);
    public long Spoken => Interlocked.Read(ref _spoken);
    public TutorReply? Last { get { lock (_gate) return _history.Count > 0 ? _history[^1] : null; } }
    public IReadOnlyList<TutorReply> History { get { lock (_gate) return _history.ToArray(); } }
    public Task? PendingRemediation { get; private set; }
    public string? LastAction { get; private set; }

    // ══════════════════════ RAG 색인 ══════════════════════

    /// <summary>강좌 지식(챕터 요약 · 퀴즈 해설) → 튜터 페르소나 세계관 색인(강좌 변경 시 재생성). 반환 = 페르소나 ID(RAG 없음 → null).</summary>
    public string? IndexCourse(EduCourse course)
    {
        if (_memory is null) return null;
        if (_personaId is not null && _courseId == course.Id && _memory.Find(_personaId) is not null) return _personaId;
        if (_personaId is not null) _memory.RemovePersona(_personaId);
        var lore = new List<string>();
        foreach (var c in course.Chapters) if (c.Summary.Length > 0) lore.Add("[" + c.Concept + "] " + c.Summary);
        foreach (var q in course.Quizzes) if (q.Explanation.Length > 0) lore.Add("[" + q.Concept + "] " + q.Prompt + " → " + q.Explanation);
        var p = _memory.CreatePersona(Persona.Name, "AI 튜터 · " + course.Title + " · " + course.Description, lore, null, Persona.Voice, DanceStyle.KPop, Persona.Speech);
        _personaId = p.Id; _courseId = course.Id;
        LastAction = "📚 튜터 RAG 색인 · 지식 " + lore.Count.ToString(CultureInfo.InvariantCulture) + "건";
        Raise();
        return _personaId;
    }

    private IReadOnlyList<RecalledMemory> Recall(string query, string learnerId)
    {
        if (_memory is null || _personaId is null) return Array.Empty<RecalledMemory>();
        return _memory.Recall(_personaId, query, learnerId, RecallK);
    }

    // ══════════════════════ BCL 교수법 ══════════════════════

    public static TutorMode ModeOf(float mastery) => mastery < 0.4f ? TutorMode.Foundation : mastery < 0.75f ? TutorMode.Standard : TutorMode.Advanced;

    /// <summary>한글 받침 여부로 조사 선택(은/는 · 이/가 · 을/를 · 과/와). 비한글은 뒤 조사.</summary>
    public static string Josa(string word, string withBatchim, string without)
    {
        if (word.Length == 0) return without;
        var c = word[^1];
        if (c < 0xAC00 || c > 0xD7A3) return without;
        return (c - 0xAC00) % 28 != 0 ? withBatchim : without;
    }

    public static string Analogy(string concept, string domain)
    {
        var di = Array.IndexOf(Domains, domain);
        if (di < 0) di = 0;
        if (KnownAnalogies.TryGetValue(concept, out var arr)) return arr[di];
        foreach (var kv in KnownAnalogies) if (concept.Contains(kv.Key, StringComparison.Ordinal)) return kv.Value[di];
        return di switch
        {
            1 => "레시피를 한 단계씩 따라 하며 맛을 맞춰 가는 과정",
            2 => "기본기 훈련을 반복해 실전 플레이로 연결하는 과정",
            3 => "튜토리얼에서 익힌 조작을 보스전에 응용하는 과정",
            _ => "처음 가는 길을 지도로 확인하며 한 걸음씩 걷는 과정",
        };
    }

    private string Opener(TutorMode mode) => Persona.Style switch
    {
        TutorStyle.Socratic => "함께 생각해 볼까요?",
        TutorStyle.Concise => "핵심만 정리할게요.",
        TutorStyle.Storyteller => "짧은 이야기로 풀어 볼게요.",
        _ => mode == TutorMode.Foundation ? "괜찮아요, 처음엔 누구나 헷갈려요!" : "좋은 시도였어요!",
    };

    /// <summary>오답 보충 강의(BCL) — 숙달도 모드 · 비유 · RAG 근거 · 확인 질문.</summary>
    public TutorReply BuildRemediation(string learnerId, QuizItem q, GradeResult g, float mastery, IReadOnlyList<RecalledMemory> recalled)
    {
        var concept = q.Concept.Length > 0 ? q.Concept : "이 개념";
        var mode = ModeOf(mastery);
        var analogy = Analogy(concept, Persona.AnalogyDomain);
        var basis = FirstLore(recalled) ?? q.Explanation;
        var sb = new StringBuilder();
        sb.Append(Opener(mode)).Append(' ');
        if (mode == TutorMode.Advanced) sb.Append(concept).Append(Josa(concept, "은", "는")).Append(' ').Append(basis);
        else
        {
            sb.Append(concept).Append(Josa(concept, "을", "를")).Append(' ').Append(Persona.AnalogyDomain).Append("에 비유하면 '").Append(analogy).Append("'과 비슷해요. ");
            sb.Append(basis);
            if (g.Final) sb.Append(" 정답은 '").Append(InteractiveEduVideoQuizEngine.AnswerText(q)).Append("'였어요.");
        }
        string? check = mode == TutorMode.Advanced ? null
            : Persona.Style == TutorStyle.Socratic ? "그렇다면 " + concept + Josa(concept, "이", "가") + " 없다면 어떤 일이 생길까요?"
            : "제 비유를 떠올리며 " + concept + Josa(concept, "을", "를") + " 한 문장으로 설명해 볼래요?";
        if (check is not null) sb.Append(' ').Append(check);
        var emo = mode == TutorMode.Foundation ? FacialEmotion.HappySmile : FacialEmotion.Curious;
        return Finish(learnerId, concept, mode, sb.ToString(), analogy, check, emo, recalled, "BCL");
    }

    /// <summary>자유 질문 답변(BCL) — 개념 탐지 · RAG 근거 · 비유.</summary>
    public TutorReply BuildAnswer(string learnerId, string question, IReadOnlyList<RecalledMemory> recalled, EduCourse? course)
    {
        var concept = DetectConcept(question, course, recalled) ?? "질문하신 내용";
        var mastery = _quiz?.MasteryOf(concept) ?? InteractiveEduVideoQuizEngine.BktPrior;
        var mode = ModeOf(mastery);
        var analogy = Analogy(concept, Persona.AnalogyDomain);
        var basis = FirstLore(recalled);
        var sb = new StringBuilder();
        sb.Append("좋은 질문이에요! ");
        if (basis is not null) sb.Append(basis).Append(' ');
        else sb.Append(concept).Append("에 대해서는 강의 자료에서 직접적인 근거를 찾지 못했어요. 관련 챕터를 다시 확인해 볼까요? ");
        if (mode != TutorMode.Advanced) sb.Append(Persona.AnalogyDomain).Append("에 비유하자면 '").Append(analogy).Append("'이라고 생각하면 쉬워요.");
        return Finish(learnerId, concept, mode, sb.ToString().TrimEnd(), analogy, mode == TutorMode.Foundation ? "이해가 됐는지 짧은 퀴즈로 확인해 볼까요?" : null, FacialEmotion.Curious, recalled, "BCL");
    }

    public static string? DetectConcept(string text, EduCourse? course, IReadOnlyList<RecalledMemory>? recalled)
    {
        if (course is not null)
        {
            foreach (var c in course.Chapters) if (c.Concept.Length > 0 && text.Contains(c.Concept, StringComparison.OrdinalIgnoreCase)) return c.Concept;
            foreach (var q in course.Quizzes) if (q.Concept.Length > 0 && text.Contains(q.Concept, StringComparison.OrdinalIgnoreCase)) return q.Concept;
        }
        foreach (var k in KnownAnalogies.Keys) if (text.Contains(k, StringComparison.Ordinal)) return k;
        if (recalled is not null)
            foreach (var r in recalled)
            {
                var t = r.Text;
                if (t.Length > 2 && t[0] == '[') { var e = t.IndexOf(']'); if (e > 1) return t[1..e]; }
            }
        return null;
    }

    private static string? FirstLore(IReadOnlyList<RecalledMemory> recalled)
    {
        foreach (var r in recalled)
            if (r.Kind == MemoryKind.Lore)
            {
                var t = r.Text;
                var e = t.StartsWith('[') ? t.IndexOf(']') : -1;
                var body = e > 0 ? t[(e + 1)..].Trim() : t;
                var arrow = body.IndexOf(" → ", StringComparison.Ordinal);
                return arrow > 0 ? body[(arrow + 3)..] : body;
            }
        return null;
    }

    private TutorReply Finish(string learnerId, string concept, TutorMode mode, string text, string analogy, string? check, FacialEmotion emo, IReadOnlyList<RecalledMemory> recalled, string source)
    {
        var cues = LiveVTuberBroadcastingBridge.TranscriptCues(text);
        var dur = cues.Length == 0 ? 1 : cues[^1].End + 0.2;
        return new TutorReply(learnerId, concept, mode, text, analogy, check, emo, recalled, source, cues, dur, DateTimeOffset.UtcNow);
    }

    // ══════════════════════ LLM 재작성 ══════════════════════

    public static bool TryParseTutorJson(string? text, out string explanation, out string analogy, out string? check)
    {
        explanation = ""; analogy = ""; check = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = GeminiClient.StripFence(text);
        var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return false;
        try
        {
            using var doc = JsonDocument.Parse(s[a..(b + 1)]);
            var r = doc.RootElement;
            if (!r.TryGetProperty("explanation", out var ex) || ex.ValueKind != JsonValueKind.String) return false;
            explanation = ex.GetString()!.Trim();
            if (explanation.Length is 0 or > 600) return false;
            analogy = r.TryGetProperty("analogy", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString()!.Trim() : "";
            if (r.TryGetProperty("check", out var ck) && ck.ValueKind == JsonValueKind.String && ck.GetString()!.Trim() is { Length: > 0 } c) check = c.Length > 200 ? c[..200] : c;
            if (analogy.Length > 200) analogy = analogy[..200];
            return true;
        }
        catch (JsonException) { return false; }
    }

    private async Task<TutorReply> RewriteAsync(TutorReply bcl, string task, CancellationToken ct)
    {
        if (_llm is null || !Persona.UseLlm) return bcl;
        var sb = new StringBuilder("You are ").Append(Persona.Name).Append(", a Korean e-learning AI tutor. Speech: ").Append(Persona.Speech).Append(". Style: ").Append(Persona.Style.ToString())
            .Append(". Learner level mode: ").Append(bcl.Mode.ToString()).Append(". Explain using an analogy from the domain '").Append(Persona.AnalogyDomain)
            .Append("'. Use ONLY the course facts below; never invent facts. Reply ONLY JSON {\"explanation\":\"<=400 Korean chars\",\"analogy\":\"<=120 chars\",\"check\":\"one short check question or empty\"}.\nTask: ")
            .Append(task).Append("\nConcept: ").Append(bcl.Concept).Append("\nCourse facts:\n");
        foreach (var r in bcl.Recalled) sb.Append("- ").Append(r.Text).Append('\n');
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(LlmDeadlineMs);
        try
        {
            var text = await _llm(sb.ToString(), d.Token).ConfigureAwait(false);
            if (!TryParseTutorJson(text, out var ex, out var an, out var ck)) return bcl;
            Interlocked.Increment(ref _llmReplies);
            var body = an.Length > 0 && !ex.Contains(an, StringComparison.Ordinal) ? ex + " (비유: " + an + ")" : ex;
            if (ck is not null) body += " " + ck;
            return Finish(bcl.LearnerId, bcl.Concept, bcl.Mode, body, an.Length > 0 ? an : bcl.Analogy, ck, bcl.Emotion, bcl.Recalled, "Gemini");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return bcl; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { PromptFlowStudio.Program.Log("Tutor.Llm", ex); return bcl; }
    }

    // ══════════════════════ 공개 흐름 ══════════════════════

    /// <summary>오답 보충 강의: 학습자 오답 기억 → RAG 회수 → BCL/Gemini → 아바타 발화.</summary>
    public async Task<TutorReply> ExplainAsync(string learnerId, QuizItem q, GradeResult g, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _remediations);
        var course = _quiz?.Course;
        if (course is not null) IndexCourse(course);
        if (_memory is not null && _personaId is not null)
            _memory.AddMemory(_personaId, MemoryKind.Fact, learnerId, learnerId, "[" + q.Concept + "] 학습자 오답: " + q.Prompt + " (점수 " + (g.Score * 100).ToString("0", CultureInfo.InvariantCulture) + "%)", 0, 0.8f);
        var recalled = Recall(q.Concept + " " + q.Prompt, learnerId);
        var mastery = _quiz?.MasteryOf(q.Concept) ?? InteractiveEduVideoQuizEngine.BktPrior;
        var bcl = BuildRemediation(learnerId, q, g, mastery, recalled);
        var reply = await RewriteAsync(bcl, "The learner answered a quiz incorrectly. Quiz: " + q.Prompt + " Correct answer: " + InteractiveEduVideoQuizEngine.AnswerText(q), ct).ConfigureAwait(false);
        _quiz?.Record(q.TimeSec, LearningVerb.Tutored, course?.Quizzes.IndexOf(q) ?? -1, mastery, true, reply.Concept);
        await DeliverAsync(reply, ct).ConfigureAwait(false);
        return reply;
    }

    /// <summary>실시간 Q&amp;A: 질문 기억 → RAG 회수 → BCL/Gemini → 아바타 발화.</summary>
    public async Task<TutorReply> AskAsync(string learnerId, string question, CancellationToken ct = default)
    {
        question = VTuberPersonaMemoryEngine.Clip(question, 300);
        if (question.Length == 0) throw new ArgumentException("질문이 비었습니다.");
        Interlocked.Increment(ref _questions);
        var course = _quiz?.Course;
        if (course is not null) IndexCourse(course);
        var recalled = Recall(question, learnerId);
        if (_memory is not null && _personaId is not null) _memory.AddMemory(_personaId, MemoryKind.Chat, learnerId, learnerId, question, 0, 0.5f);
        var bcl = BuildAnswer(learnerId, question, recalled, course);
        var reply = await RewriteAsync(bcl, "The learner asks: " + question, ct).ConfigureAwait(false);
        _quiz?.Record(_quiz.Position, LearningVerb.Asked, -1, 0, true, question);
        await DeliverAsync(reply, ct).ConfigureAwait(false);
        return reply;
    }

    private async Task DeliverAsync(TutorReply reply, CancellationToken ct)
    {
        lock (_gate)
        {
            _history.Add(reply);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
        }
        if (Persona.UseAvatar && _bridge is not null)
        {
            try
            {
                string? voice = null;
                if (_tts is not null)
                {
                    using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    d.CancelAfter(TimeSpan.FromSeconds(8));
                    try { voice = await _tts.SynthesizeAsync(reply.Text, Persona.Voice, 1.0, ct: d.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { voice = null; }
                }
                var perf = await _bridge.PrepareSpeechAsync(reply.Text, reply.Emotion, voice, ct).ConfigureAwait(false);
                _bridge.Speak(perf);
                Interlocked.Increment(ref _spoken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) { PromptFlowStudio.Program.Log("Tutor.Avatar", ex); }
        }
        LastAction = "🧑‍🏫 " + reply.Concept + " · " + (reply.Source == "Gemini" ? "Gemini" : "BCL") + " · " + ModeLabel(reply.Mode);
        try { OnReply?.Invoke(reply); } catch (Exception ex) { PromptFlowStudio.Program.Log("Tutor.OnReply", ex); }
        Raise();
    }

    private void OnGraded(QuizItem q, GradeResult g, LearnerSession s)
    {
        if (!Persona.AutoRemediate || g.Correct) return;
        PendingRemediation = RemediateSafeAsync(s.LearnerId, q, g);
    }

    private async Task RemediateSafeAsync(string learnerId, QuizItem q, GradeResult g)
    {
        try { await ExplainAsync(learnerId, q, g).ConfigureAwait(false); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("Tutor.Remediate", ex); }
    }

    /// <summary>발화 시각 t(초)의 입 벌림 0~1(이진 탐색 · 무할당).</summary>
    public static float MouthOpenAt(ReadOnlySpan<VisemeCue> cues, double t)
    {
        int lo = 0, hi = cues.Length - 1;
        while (lo <= hi)
        {
            var m = (lo + hi) >> 1;
            if (t < cues[m].Start) hi = m - 1;
            else if (t >= cues[m].End) lo = m + 1;
            else return cues[m].Shape is Viseme.Rest or Viseme.Closed ? 0f : (float)cues[m].Openness;
        }
        return 0f;
    }

    public static string ModeLabel(TutorMode m) => m switch { TutorMode.Foundation => "기초 보충", TutorMode.Advanced => "심화", _ => "표준" };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Tutor.OnChange", ex); } }

    public void Dispose()
    {
        if (_quiz is not null) _quiz.OnGraded -= OnGraded;
    }
}
