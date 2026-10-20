using System.Globalization;
using PromptFlowStudio.Services.Newsroom;

namespace PromptFlowStudio.Services.EduTech;

public enum QuizKind : byte { SingleChoice, MultiChoice, TrueFalse, ShortAnswer, Hotspot }

public enum PlayerAction : byte { Play, PauseForQuiz, Completed }

public enum LearningVerb : byte { Launched, Progressed, Paused, Answered, Skipped, Asked, Tutored, Completed, Passed, Failed }

/// <summary>퀴즈 카드(타임코드 · 유형 · 정답 마스크/허용 답안/핫스팟 · 개념 · 배점).</summary>
public sealed class QuizItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double TimeSec { get; set; }
    public QuizKind Kind { get; set; }
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = new();
    /// <summary>선택형 정답 비트(옵션 i → 1&lt;&lt;i).</summary>
    public int CorrectMask { get; set; }
    public List<string> Answers { get; set; } = new();
    public float HotX { get; set; }
    public float HotY { get; set; }
    public float HotW { get; set; }
    public float HotH { get; set; }
    public string Concept { get; set; } = "";
    public int Points { get; set; } = 10;
    public string Explanation { get; set; } = "";
    public string Hint { get; set; } = "";
    public bool Required { get; set; } = true;
    public int MaxAttempts { get; set; } = 2;
}

public sealed class CourseChapter
{
    public string Title { get; set; } = "";
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public string Concept { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class EduCourse
{
    public string Id { get; set; } = "course-" + Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Language { get; set; } = "ko-KR";
    public string? VideoPath { get; set; }
    public double DurationSec { get; set; }
    public double PassingScore { get; set; } = 0.7;
    public List<CourseChapter> Chapters { get; set; } = new();
    public List<QuizItem> Quizzes { get; set; } = new();
}

/// <summary>학습자 응답(선택 비트 · 텍스트 · 터치 좌표 0~1).</summary>
public readonly record struct QuizResponse(int ChoiceMask, string? Text, float X, float Y);

public readonly record struct GradeResult(bool Correct, float Score, int Attempt, bool Final, string Feedback);

/// <summary>학습 이벤트(xAPI 문장 원천 · 비디오 시각 · 절대 시각).</summary>
public readonly record struct LearningEvent(double VideoSec, DateTimeOffset At, LearningVerb Verb, int QuizIndex, float Score, bool Success, string? Response);

/// <summary>학습자 세션(시청 비트맵 · 퀴즈 시도/최고점 · 개념 숙달도 · 이벤트).</summary>
public sealed class LearnerSession
{
    public const int EventCap = 4096;
    public LearnerSession(string id, string name, int quizzes, int seconds)
    {
        LearnerId = id; LearnerName = name;
        Attempts = new int[quizzes]; Best = new float[quizzes]; Done = new bool[quizzes];
        Watched = new ulong[(Math.Max(1, seconds) + 63) / 64];
        WatchSeconds = Math.Max(1, seconds);
    }
    public string LearnerId { get; }
    public string LearnerName { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public int[] Attempts { get; }
    public float[] Best { get; }
    public bool[] Done { get; }
    public ulong[] Watched { get; }
    public int WatchSeconds { get; }
    public int WatchedCount;
    public Dictionary<string, float> Mastery { get; } = new(StringComparer.Ordinal);
    public List<LearningEvent> Events { get; } = new(256);
}

/// <summary>
/// [PromptFlow Studio v16.46.0] 🎓 인터랙티브 이러닝 비디오 &amp; 동적 퀴즈 오버레이 엔진.
///   ▸ 강좌(챕터 · 퀴즈 타임코드) → Tick(비디오 초): 시청 비트맵 기록 · 퀴즈 타임코드 교차 시 일시정지 + 퀴즈 카드 인젝션 — 재생 경로 무할당.
///   ▸ 필수 퀴즈 게이트: 미응답 필수 퀴즈 너머로 탐색 시 해당 타임코드로 클램프.
///   ▸ 채점: 객관식(단일/복수 부분점수) · OX · 주관식(정규화 + 레벤슈타인 허용 오차 · 무할당) · 터치 핫스팟(정규화 사각형) → 시도/최고점 · 피드백(정답/힌트/해설).
///   ▸ 개념 숙달도 BKT(Bayesian Knowledge Tracing · slip/guess/transit) → AI 튜터 적응형 보충 강의 연동(OnGraded).
///   ▸ 오버레이(RGBA 합성): 디밍 · 퀴즈 카드 · 보기(선택/정오 색) · 피드백 · 진도 바(챕터 · 퀴즈 마커) — 마스크는 퀴즈 활성 시 1회 래스터.
/// </summary>
public sealed class InteractiveEduVideoQuizEngine
{
    public const float BktSlip = 0.1f, BktTransit = 0.15f, BktPrior = 0.3f;

    private readonly object _gate = new();
    private EduCourse _course = new();
    private double[] _cue = Array.Empty<double>();
    private LearnerSession _session = new("guest", "게스트", 0, 1);
    private int _active = -1, _next, _highlight = -1;
    private double _last = -1;
    private bool _completedLogged;
    private GradeResult? _lastGrade;
    private TextMask? _promptMask, _feedbackMask;
    private TextMask?[] _optMasks = Array.Empty<TextMask?>();
    private long _ticks, _pauses;

    public event Action? OnChange;
    /// <summary>채점 직후(퀴즈 · 결과 · 학습자) — 튜터 보충 강의 트리거.</summary>
    public event Action<QuizItem, GradeResult, LearnerSession>? OnGraded;

    public EduCourse Course => _course;
    public LearnerSession Session => _session;
    public int ActiveQuiz => _active;
    public QuizItem? ActiveItem => _active >= 0 ? _course.Quizzes[_active] : null;
    public GradeResult? LastGrade => _lastGrade;
    public double Position => _last;
    public long Ticks => Interlocked.Read(ref _ticks);
    public long Pauses => Interlocked.Read(ref _pauses);
    public string? LastAction { get; private set; }
    public int Highlight { get => _highlight; set => _highlight = value; }

    // ══════════════════════ 강좌 · 세션 ══════════════════════

    public void LoadCourse(EduCourse course)
    {
        lock (_gate)
        {
            course.Quizzes.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
            course.Chapters.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
            if (course.DurationSec <= 0) foreach (var c in course.Chapters) course.DurationSec = Math.Max(course.DurationSec, c.EndSec);
            _course = course;
            _cue = new double[course.Quizzes.Count];
            for (var i = 0; i < _cue.Length; i++) _cue[i] = course.Quizzes[i].TimeSec;
            ResetSessionLocked(_session.LearnerId, _session.LearnerName);
        }
        LastAction = "📚 강좌 로드 · " + course.Title + " · 퀴즈 " + course.Quizzes.Count.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    public LearnerSession StartSession(string learnerId, string learnerName)
    {
        lock (_gate) ResetSessionLocked(learnerId, learnerName);
        Record(0, LearningVerb.Launched, -1, 0, true, null);
        LastAction = "▶ 학습 시작 · " + learnerName;
        Raise();
        return _session;
    }

    private void ResetSessionLocked(string id, string name)
    {
        _session = new LearnerSession(id, name, _course.Quizzes.Count, (int)Math.Ceiling(_course.DurationSec));
        _active = -1; _next = 0; _last = -1; _lastGrade = null; _highlight = -1; _completedLogged = false;
    }

    /// <summary>강좌 재구성 후(퀴즈 추가/수정) 세션 유지 불가 → 새 세션.</summary>
    public void Reindex() => LoadCourse(_course);

    // ══════════════════════ 재생 오케스트레이션(무할당) ══════════════════════

    /// <summary>비디오 시각 진행 → 재생/퀴즈 일시정지/완료. 재생 경로 무할당(퀴즈 활성 시 카드 래스터 1회).</summary>
    public PlayerAction Tick(double videoSec)
    {
        Interlocked.Increment(ref _ticks);
        var trigger = -1;
        var logComplete = false;
        lock (_gate)
        {
            if (_active >= 0) return PlayerAction.PauseForQuiz;
            var s = _session;
            var sec = (int)videoSec;
            if ((uint)sec < (uint)s.WatchSeconds)
            {
                ref var word = ref s.Watched[sec >> 6];
                var bit = 1UL << (sec & 63);
                if ((word & bit) == 0) { word |= bit; s.WatchedCount++; }
            }
            if (videoSec < _last) _next = LowerBound(_cue, videoSec);   // 되감기
            while (_next < _cue.Length && _cue[_next] <= videoSec)
            {
                if (!s.Done[_next] && _cue[_next] > _last - 0.001) { trigger = _next; _next++; break; }
                _next++;
            }
            _last = videoSec;
            if (trigger < 0)
            {
                if (!(videoSec >= _course.DurationSec && _course.DurationSec > 0)) return PlayerAction.Play;
                if (_completedLogged) return PlayerAction.Completed;
                _completedLogged = logComplete = true;
            }
            else { _active = trigger; _highlight = -1; _lastGrade = null; }
        }
        if (logComplete)
        {
            Record(videoSec, LearningVerb.Completed, -1, Score, IsComplete, null);
            if (IsComplete) Record(videoSec, Passed ? LearningVerb.Passed : LearningVerb.Failed, -1, Score, Passed, null);
            LastAction = Passed ? "🏅 수료 · 합격" : "🏁 강좌 끝";
            Raise();
            return PlayerAction.Completed;
        }
        Interlocked.Increment(ref _pauses);
        Activate(trigger);
        Record(videoSec, LearningVerb.Paused, trigger, 0, true, null);
        LastAction = "⏸ 퀴즈 · " + _course.Quizzes[trigger].Prompt;
        Raise();
        return PlayerAction.PauseForQuiz;
    }

    public static int LowerBound(ReadOnlySpan<double> a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var m = (lo + hi) >> 1; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    /// <summary>탐색 — 미응답 필수 퀴즈 너머 이동 시 해당 타임코드로 클램프. 반환 = 실제 위치.</summary>
    public double Seek(double target)
    {
        lock (_gate)
        {
            target = Math.Clamp(target, 0, Math.Max(0, _course.DurationSec));
            var from = Math.Max(0, _last);
            if (target > from)
                for (var i = 0; i < _cue.Length; i++)
                {
                    var q = _course.Quizzes[i];
                    if (_cue[i] > from + 0.001 && _cue[i] <= target && q.Required && !_session.Done[i]) { target = _cue[i]; break; }
                }
            _next = LowerBound(_cue, target);
            _last = target - 0.0005;
            return target;
        }
    }

    // ══════════════════════ 채점 ══════════════════════

    public static float Grade(QuizItem q, QuizResponse r)
    {
        switch (q.Kind)
        {
            case QuizKind.SingleChoice:
            case QuizKind.TrueFalse:
                return r.ChoiceMask != 0 && r.ChoiceMask == q.CorrectMask ? 1f : 0f;
            case QuizKind.MultiChoice:
            {
                var correct = System.Numerics.BitOperations.PopCount((uint)q.CorrectMask);
                if (correct == 0) return 0;
                var hit = System.Numerics.BitOperations.PopCount((uint)(r.ChoiceMask & q.CorrectMask));
                var wrong = System.Numerics.BitOperations.PopCount((uint)(r.ChoiceMask & ~q.CorrectMask));
                return Math.Clamp((hit - wrong) / (float)correct, 0f, 1f);
            }
            case QuizKind.ShortAnswer:
                if (string.IsNullOrWhiteSpace(r.Text)) return 0;
                foreach (var a in q.Answers) if (ShortAnswerMatches(r.Text, a)) return 1f;
                return 0f;
            case QuizKind.Hotspot:
                return r.X >= q.HotX && r.X <= q.HotX + q.HotW && r.Y >= q.HotY && r.Y <= q.HotY + q.HotH ? 1f : 0f;
            default:
                return 0;
        }
    }

    /// <summary>주관식 일치(문자/숫자만 · 소문자 · 레벤슈타인 ≤ max(0, 길이/6)). 128자 이내 무할당.</summary>
    public static bool ShortAnswerMatches(ReadOnlySpan<char> response, ReadOnlySpan<char> answer)
    {
        Span<char> a = stackalloc char[128]; Span<char> b = stackalloc char[128];
        var la = Normalize(response, a); var lb = Normalize(answer, b);
        if (la == 0 || lb == 0) return false;
        var tol = lb / 6;
        return Levenshtein(a[..la], b[..lb], tol + 1) <= tol;
    }

    public static int Normalize(ReadOnlySpan<char> src, Span<char> dst)
    {
        var n = 0;
        foreach (var c in src)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            if (n >= dst.Length) break;
            dst[n++] = char.ToLowerInvariant(c);
        }
        return n;
    }

    /// <summary>레벤슈타인 거리(cap 초과 시 조기 종료 · 길이 ≤ 128 · 무할당).</summary>
    public static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int cap)
    {
        if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;
        if (b.Length > 128) b = b[..128];
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> cur = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > cap) return cap + 1;
            cur.CopyTo(prev);
        }
        return prev[b.Length];
    }

    /// <summary>BKT 사후 갱신 → 학습 전이 적용 숙달 확률.</summary>
    public static float BktUpdate(float pKnown, bool correct, float slip, float guess, float transit)
    {
        var post = correct
            ? pKnown * (1 - slip) / (pKnown * (1 - slip) + (1 - pKnown) * guess)
            : pKnown * slip / (pKnown * slip + (1 - pKnown) * (1 - guess));
        return Math.Clamp(post + (1 - post) * transit, 0.001f, 0.999f);
    }

    public static float GuessRate(QuizItem q) => q.Kind switch
    {
        QuizKind.TrueFalse => 0.5f, QuizKind.SingleChoice => 1f / Math.Max(2, q.Options.Count), QuizKind.MultiChoice => 0.1f, QuizKind.Hotspot => 0.1f, _ => 0.05f,
    };

    /// <summary>활성 퀴즈 응답 제출 → 채점 · 숙달도 · 피드백 · 정답/시도 소진 시 재생 재개.</summary>
    public GradeResult Submit(QuizResponse r)
    {
        QuizItem q; GradeResult g; int idx; LearnerSession s;
        lock (_gate)
        {
            idx = _active;
            if (idx < 0) throw new InvalidOperationException("활성 퀴즈 없음");
            q = _course.Quizzes[idx];
            s = _session;
            var score = Grade(q, r);
            var attempt = ++s.Attempts[idx];
            s.Best[idx] = Math.Max(s.Best[idx], score);
            var correct = score >= 0.999f;
            var final = correct || attempt >= Math.Max(1, q.MaxAttempts);
            var concept = q.Concept.Length > 0 ? q.Concept : q.Id;
            s.Mastery[concept] = BktUpdate(s.Mastery.TryGetValue(concept, out var pk) ? pk : BktPrior, correct, BktSlip, GuessRate(q), BktTransit);
            var fb = correct ? "✅ 정답! " + q.Explanation
                : !final ? "🤔 다시 생각해 보세요." + (q.Hint.Length > 0 ? " 힌트: " + q.Hint : "") + (score > 0 ? " (부분 점수 " + (score * 100).ToString("0", CultureInfo.InvariantCulture) + "%)" : "")
                : "📘 정답: " + AnswerText(q) + " — " + q.Explanation;
            g = new GradeResult(correct, score, attempt, final, fb);
            _lastGrade = g;
            if (final) { s.Done[idx] = true; _active = -1; }
            _feedbackMask = NewsMotionGraphicsOverlayEngine.Rasterize(Trim(fb, 60), 18, false);
        }
        Record(q.TimeSec, LearningVerb.Answered, idx, g.Score, g.Correct, ResponseText(q, r));
        LastAction = (g.Correct ? "✅ " : "❌ ") + q.Prompt;
        try { OnGraded?.Invoke(q, g, s); } catch (Exception ex) { PromptFlowStudio.Program.Log("Edu.OnGraded", ex); }
        Raise();
        return g;
    }

    /// <summary>선택 퀴즈 건너뛰기(필수는 불가).</summary>
    public bool Skip()
    {
        int idx;
        lock (_gate)
        {
            idx = _active;
            if (idx < 0 || _course.Quizzes[idx].Required) return false;
            _session.Done[idx] = true; _active = -1;
        }
        Record(_course.Quizzes[idx].TimeSec, LearningVerb.Skipped, idx, 0, false, null);
        Raise();
        return true;
    }

    public static string AnswerText(QuizItem q)
    {
        switch (q.Kind)
        {
            case QuizKind.ShortAnswer: return q.Answers.Count > 0 ? q.Answers[0] : "";
            case QuizKind.Hotspot: return "표시된 영역";
            default:
                var parts = new List<string>();
                for (var i = 0; i < q.Options.Count; i++) if ((q.CorrectMask & (1 << i)) != 0) parts.Add(q.Options[i]);
                return string.Join(", ", parts);
        }
    }

    private static string ResponseText(QuizItem q, QuizResponse r) => q.Kind switch
    {
        QuizKind.ShortAnswer => r.Text ?? "",
        QuizKind.Hotspot => r.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + r.Y.ToString("0.###", CultureInfo.InvariantCulture),
        _ => r.ChoiceMask.ToString(CultureInfo.InvariantCulture),
    };

    public void Record(double videoSec, LearningVerb verb, int quiz, float score, bool success, string? response)
    {
        var ev = _session.Events;
        lock (ev)
        {
            if (ev.Count >= LearnerSession.EventCap) ev.RemoveRange(0, LearnerSession.EventCap / 8);
            ev.Add(new LearningEvent(videoSec, DateTimeOffset.UtcNow, verb, quiz, score, success, response));
        }
    }

    // ══════════════════════ 진도 · 점수 ══════════════════════

    public float Progress => _session.WatchedCount / (float)_session.WatchSeconds;

    public float Score
    {
        get
        {
            lock (_gate)
            {
                double got = 0, total = 0;
                for (var i = 0; i < _course.Quizzes.Count && i < _session.Best.Length; i++) { total += _course.Quizzes[i].Points; got += _course.Quizzes[i].Points * _session.Best[i]; }
                return total <= 0 ? 1f : (float)(got / total);
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                for (var i = 0; i < _course.Quizzes.Count && i < _session.Done.Length; i++) if (_course.Quizzes[i].Required && !_session.Done[i]) return false;
                return Progress >= 0.9f;
            }
        }
    }

    public bool Passed => IsComplete && Score >= _course.PassingScore;

    public float MasteryOf(string concept) { lock (_gate) return _session.Mastery.TryGetValue(concept, out var p) ? p : BktPrior; }

    /// <summary>숙달도 최저 개념(튜터 보충 우선순위).</summary>
    public string? WeakestConcept()
    {
        lock (_gate)
        {
            string? w = null; var min = float.MaxValue;
            foreach (var kv in _session.Mastery) if (kv.Value < min) { min = kv.Value; w = kv.Key; }
            return w;
        }
    }

    public CourseChapter? ChapterAt(double sec)
    {
        foreach (var c in _course.Chapters) if (sec >= c.StartSec && sec < c.EndSec) return c;
        return null;
    }

    /// <summary>퀴즈 없는 챕터 끝(-3초)에 OX 체크포인트 자동 배치. 반환 = 추가 수.</summary>
    public int AutoPlaceCheckpoints()
    {
        var added = 0;
        foreach (var c in _course.Chapters)
        {
            var has = false;
            foreach (var q in _course.Quizzes) if (q.TimeSec >= c.StartSec && q.TimeSec <= c.EndSec) { has = true; break; }
            if (has || c.Summary.Length == 0) continue;
            _course.Quizzes.Add(new QuizItem
            {
                TimeSec = Math.Max(c.StartSec, c.EndSec - 3), Kind = QuizKind.TrueFalse, Prompt = "다음 설명이 맞나요? " + c.Summary, Options = { "O (맞다)", "X (틀리다)" },
                CorrectMask = 1, Concept = c.Concept, Explanation = c.Summary, Points = 5, Required = false, MaxAttempts = 1,
            });
            added++;
        }
        if (added > 0) Reindex();
        return added;
    }

    // ══════════════════════ 오버레이 ══════════════════════

    private void Activate(int idx)
    {
        var q = _course.Quizzes[idx];
        var pm = NewsMotionGraphicsOverlayEngine.Rasterize(Trim(q.Prompt, 48), 24, true);
        var om = new TextMask?[q.Options.Count];
        for (var i = 0; i < om.Length; i++) om[i] = NewsMotionGraphicsOverlayEngine.Rasterize(((char)('A' + i)).ToString() + ". " + Trim(q.Options[i], 40), 20, false);
        lock (_gate) { _promptMask = pm; _optMasks = om; _feedbackMask = null; }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>RGBA 프레임 위 퀴즈 카드/피드백/진도 바 합성(무할당).</summary>
    public void Render(Span<byte> rgba, int w, int h, int stride)
    {
        lock (_gate)
        {
            var sc = h / 720f;
            int S(float v) => (int)(v * sc);
            // 진도 바(챕터 구분 · 퀴즈 마커)
            var dur = Math.Max(1, _course.DurationSec);
            var by = h - S(10);
            NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, 0, by, w, S(10), 0x1E293B, 220);
            NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, 0, by, (int)(w * Math.Clamp(Math.Max(0, _last) / dur, 0, 1)), S(10), 0x6366F1, 255);
            foreach (var c in _course.Chapters) NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, (int)(w * c.StartSec / dur), by, 2, S(10), 0xFFFFFF, 200);
            for (var i = 0; i < _cue.Length; i++)
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, (int)(w * _cue[i] / dur) - 3, by - S(6), 6, S(6), _session.Done[i] ? 0x22C55Eu : 0xFBBF24u, 255);

            var showFeedback = _active < 0 && _feedbackMask is not null && _lastGrade is { Final: true };
            if (_active < 0 && !showFeedback) return;
            NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, 0, 0, w, by, 0x000000, _active >= 0 ? 140 : 60);
            int cw = Math.Min(w - S(80), S(900)), cx = (w - cw) / 2;
            var optN = _active >= 0 ? _optMasks.Length : 0;
            int ch = S(120) + optN * S(52), cy = Math.Max(S(20), (by - ch) / 2);
            NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx, cy, cw, ch, 0x0F172A, 235);
            NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx, cy, cw, S(6), 0x6366F1, 255);
            if (_active >= 0 && _promptMask is { } pm) NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, pm, cx + S(24), cy + S(24), 0xFFFFFF, 255, 0);
            var q = _active >= 0 ? _course.Quizzes[_active] : null;
            for (var i = 0; i < optN; i++)
            {
                var oy = cy + S(76) + i * S(52);
                var bit = 1 << i;
                uint col = i == _highlight ? 0x4F46E5u : 0x1E293Bu;
                if (_lastGrade is { } lg && !lg.Final && q is not null && (q.CorrectMask & bit) == 0 && i == _highlight) col = 0x7F1D1Du;
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx + S(20), oy, cw - S(40), S(44), col, 255);
                if (_optMasks[i] is { } om) NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, om, cx + S(36), oy + (S(44) - om.Height) / 2, 0xE2E8F0, 255, 0);
            }
            if (_feedbackMask is { } fm)
            {
                var fy = cy + ch - S(40);
                var good = _lastGrade is { Correct: true };
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx + S(20), fy - S(4), cw - S(40), S(36), good ? 0x14532Du : 0x78350Fu, 240);
                NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, fm, cx + S(30), fy, 0xFFFFFF, 255, 0);
            }
        }
    }

    /// <summary>피드백 오버레이 해제(다음 재생 프레임부터 카드 숨김).</summary>
    public void DismissFeedback() { lock (_gate) { _feedbackMask = null; _lastGrade = null; } Raise(); }

    // ══════════════════════ 데모 강좌 ══════════════════════

    public static EduCourse DemoCourse() => new()
    {
        Id = "course-ml101",
        Title = "AI 머신러닝 입문",
        Description = "지도학습 · 과적합 · 경사하강법 · 신경망 기초를 10분 만에 이해하는 인터랙티브 강좌",
        DurationSec = 600,
        Chapters =
        {
            new CourseChapter { Title = "1. 머신러닝이란?", StartSec = 0, EndSec = 140, Concept = "지도학습", Summary = "지도학습은 정답(레이블)이 있는 데이터로 입력과 출력의 관계를 학습한다." },
            new CourseChapter { Title = "2. 과적합과 일반화", StartSec = 140, EndSec = 300, Concept = "과적합", Summary = "과적합은 훈련 데이터에만 지나치게 맞춰져 새로운 데이터에서 성능이 떨어지는 현상이다." },
            new CourseChapter { Title = "3. 경사하강법", StartSec = 300, EndSec = 460, Concept = "경사하강법", Summary = "경사하강법은 손실 함수의 기울기 반대 방향으로 조금씩 이동하며 최솟값을 찾는다." },
            new CourseChapter { Title = "4. 신경망 한눈에", StartSec = 460, EndSec = 600, Concept = "신경망", Summary = "신경망은 가중치와 활성화 함수를 가진 층을 쌓아 복잡한 패턴을 표현한다." },
        },
        Quizzes =
        {
            new QuizItem { Id = "q1", TimeSec = 120, Kind = QuizKind.SingleChoice, Prompt = "지도학습에 반드시 필요한 것은?", Options = { "정답 레이블", "GPU", "강화 보상", "클러스터 수" }, CorrectMask = 1, Concept = "지도학습", Hint = "'지도'는 정답을 알려준다는 뜻이에요.", Explanation = "지도학습은 입력-정답 쌍으로 학습합니다." },
            new QuizItem { Id = "q2", TimeSec = 250, Kind = QuizKind.MultiChoice, Prompt = "과적합을 줄이는 방법을 모두 고르세요.", Options = { "데이터 증강", "규제(정규화)", "모델을 더 크게", "조기 종료" }, CorrectMask = 0b1011, Concept = "과적합", Hint = "모델이 외우지 못하게 하는 방법을 떠올려 보세요.", Explanation = "증강 · 규제 · 조기 종료는 일반화를 돕습니다." },
            new QuizItem { Id = "q3", TimeSec = 380, Kind = QuizKind.ShortAnswer, Prompt = "경사하강법에서 한 번에 이동하는 크기를 무엇이라 하나요?", Answers = { "학습률", "learning rate", "러닝레이트" }, Concept = "경사하강법", Hint = "'학습 ○' 두 글자 + 한 글자", Explanation = "학습률(learning rate)이 보폭을 결정합니다." },
            new QuizItem { Id = "q4", TimeSec = 440, Kind = QuizKind.TrueFalse, Prompt = "학습률이 너무 크면 최솟값을 지나쳐 발산할 수 있다.", Options = { "O", "X" }, CorrectMask = 1, Concept = "경사하강법", Explanation = "보폭이 너무 크면 골짜기를 뛰어넘습니다.", MaxAttempts = 1 },
            new QuizItem { Id = "q5", TimeSec = 560, Kind = QuizKind.Hotspot, Prompt = "화면에서 '은닉층'을 터치하세요.", HotX = 0.4f, HotY = 0.25f, HotW = 0.2f, HotH = 0.5f, Concept = "신경망", Hint = "입력층과 출력층 사이에 있어요.", Explanation = "은닉층은 입력과 출력 사이에서 특징을 추출합니다." },
        },
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Edu.OnChange", ex); } }
}
