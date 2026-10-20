using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.ProceduralFoley;

namespace PromptFlowStudio.Services.NarrativeTimeline;

public enum ScriptTokenKind : byte { Scene, Section, Beat, Action, Character, Parenthetical, Dialogue, Transition, Branch }
public enum TimelineTrack : byte { Script, Actor, Camera, Foley, Voice }
public enum ActorMotion : byte { Hold, Approach, Retreat, Cross }

/// <summary>각본 토큰 — 원문 버퍼 (Start, Length) 슬라이스 · 배우/섹션/비트 태그.</summary>
public struct ScriptToken
{
    public ScriptTokenKind Kind;
    public int Start, Length;
    public sbyte Actor;
    public short Section, Param;
    public float Tension, Affinity;
}

/// <summary>타임라인 이벤트(트랙 · 시작 s · 길이 s · 토큰 · 배우 · 파라미터 · 값).</summary>
public struct TimelineEvent
{
    public TimelineTrack Track;
    public double StartS;
    public float DurS, Value, Value2;
    public int Token;
    public sbyte Actor;
    public byte Param;
}

/// <summary>
/// [PromptFlow Studio v17.40.0] 📜 실시간 각본 지문 파싱 &amp; 멀티트랙 타임라인 시퀀서 엔진(<see cref="Parse"/> · <see cref="Schedule"/> · <see cref="Tick"/> 무할당).
///   ▸ 토크나이저: Fountain 규칙 — 씬 헤더(INT./EXT./I/E/EST./강제 '.') · 섹션('# 이름' = 분기 세그먼트) · 비트('= 제목 | T=긴장 | A=친밀') ·
///     캐릭터(대문자 단독 행/강제 '@' · 확장 '(V.O.)' 제거) · 괄호 지문 · 대사 · 트랜지션('&gt;'/'… TO:') · 분기점('?? 섹션A | 섹션B') — 원문 char 버퍼 슬라이스만 보관.
///   ▸ 스케줄러: 씬 → 마스터 확립 컷 · 대사 → v17.32 <see cref="RealtimeNeuralVoiceCloningDubbingEngine.Plan"/> 음소 길이 · 화자 전환 OTS 컷(v17.39 Cam-A/B) ·
///     지문 → 배우 언급/동사(다가/물러/가로질러) 모션 큐(v17.33/v17.36) · 의성/소품 어휘 → v17.38 모달 폴리 재질/강도 · 강타 시 Action 컷.
///   ▸ 트랙: 5 트랙 × 512 이벤트 고정 배열 · 트랙별 재생 커서 → 발화 링버퍼(128) — 60 fps 틱은 커서 전진 · 링 적재만(지연 = 프레임 위상).
/// </summary>
public sealed class RealtimeScriptToScreenTimelineSequencerEngine
{
    public const int MaxChars = 32768, MaxTokens = 2048, MaxCast = 4, MaxSections = 16, MaxBranches = 16, MaxOptions = 4, Tracks = 5, TrackCap = 512, FireRing = 128;
    public const float SceneS = 1.6f, TransitionS = 0.8f, ParenS = 0.3f, LineGapS = 0.35f, ActionMinS = 1.2f, ActionMaxS = 6f, CharS = 0.055f;
    public const DubLanguage Lang = DubLanguage.Ko;

    private static readonly string[] ApproachVerbs = { "다가", "좁히", "붙잡", "approach", "steps toward", "moves closer" };
    private static readonly string[] RetreatVerbs = { "물러", "뒷걸음", "돌아서", "멀어", "backs away", "retreat" };
    private static readonly string[] CrossVerbs = { "가로질러", "걸어", "crosses", "walks" };
    private static readonly string[] ConflictWords = { "왜", "거짓", "죽", "싫", "배신", "당장", "그만", "안 돼", "no", "why", "liar", "kill", "stop" };

    private readonly record struct FoleyCue(string Word, FoleyMaterial Material, float Intensity);
    private static readonly FoleyCue[] FoleyCues =
    {
        new("쾅", FoleyMaterial.Wood, 1f), new("내리친", FoleyMaterial.Wood, 0.95f), new("문을 닫", FoleyMaterial.Wood, 0.8f),
        new("총성", FoleyMaterial.Metal, 1f), new("쇠파이프", FoleyMaterial.Metal, 0.75f), new("철제", FoleyMaterial.Metal, 0.85f),
        new("유리", FoleyMaterial.Glass, 0.9f), new("주먹", FoleyMaterial.Flesh, 0.9f), new("발소리", FoleyMaterial.Concrete, 0.35f),
        new("옷깃", FoleyMaterial.Cloth, 0.3f), new("slam", FoleyMaterial.Wood, 1f), new("glass", FoleyMaterial.Glass, 0.9f),
        new("punch", FoleyMaterial.Flesh, 0.9f), new("footsteps", FoleyMaterial.Concrete, 0.35f),
    };

    private readonly char[] _src = new char[MaxChars];
    private readonly ScriptToken[] _tok = new ScriptToken[MaxTokens];
    private readonly int[] _castStart = new int[MaxCast], _castLen = new int[MaxCast];
    private readonly int[] _secStart = new int[MaxSections], _secLen = new int[MaxSections], _secFirst = new int[MaxSections];
    private readonly short[] _branchOpt = new short[MaxBranches * MaxOptions];
    private readonly byte[] _branchOptN = new byte[MaxBranches];
    private readonly int[] _branchTok = new int[MaxBranches];
    private readonly TimelineEvent[] _ev = new TimelineEvent[Tracks * TrackCap];
    private readonly int[] _evN = new int[Tracks], _cursor = new int[Tracks];
    private readonly TimelineEvent[] _fire = new TimelineEvent[FireRing];
    private int _len, _tokN, _castN, _secN, _branchN, _sceneN, _fireHead, _fireTail;

    public int Length => _len;
    public int TokenCount => _tokN;
    public int CastCount => _castN;
    public int SectionCount => _secN;
    public int BranchCount => _branchN;
    public int SceneCount => _sceneN;
    public double LastParseUs { get; private set; }
    public double LastScheduleUs { get; private set; }
    /// <summary>현재 스케줄 섹션 · 시작/끝(s) · 끝 토큰이 분기점이면 그 인덱스(없으면 −1).</summary>
    public int ScheduledSection { get; private set; }
    public double ScheduleStartS { get; private set; }
    public double ScheduleEndS { get; private set; }
    public int PendingBranch { get; private set; } = -1;
    public long Fired { get; private set; }
    public long Dropped { get; private set; }
    /// <summary>발화 지연(틱 시각 − 이벤트 시각) 최대(ms) — 프레임 위상 이하여야 정상.</summary>
    public float MaxLatenessMs { get; private set; }

    public ref readonly ScriptToken Token(int i) => ref _tok[i];
    public ReadOnlySpan<char> Text(int token) => _src.AsSpan(_tok[token].Start, _tok[token].Length);
    public ReadOnlySpan<char> CastName(int i) => _src.AsSpan(_castStart[i], _castLen[i]);
    public ReadOnlySpan<char> SectionName(int i) => _src.AsSpan(_secStart[i], _secLen[i]);
    public int SectionFirstToken(int i) => _secFirst[i];
    public int BranchOptionCount(int b) => _branchOptN[b];
    public int BranchOption(int b, int k) => _branchOpt[b * MaxOptions + k];
    public int BranchToken(int b) => _branchTok[b];
    public int EventCount(TimelineTrack t) => _evN[(int)t];
    public int Cursor(TimelineTrack t) => _cursor[(int)t];
    public ref readonly TimelineEvent Event(TimelineTrack t, int i) => ref _ev[(int)t * TrackCap + i];
    public int PendingFired => _fireHead - _fireTail;

    /// <summary>원문 적재(상한 <see cref="MaxChars"/> 절단) → 파싱.</summary>
    public bool Load(ReadOnlySpan<char> text)
    {
        var n = Math.Min(text.Length, MaxChars);
        text[..n].CopyTo(_src);
        _len = n;
        return Parse();
    }

    // ══════════════════════ 토크나이저(무할당) ══════════════════════

    public bool Parse()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _tokN = _castN = _branchN = _sceneN = 0;
        _secN = 1; _secStart[0] = 0; _secLen[0] = 0; _secFirst[0] = 0;
        var src = _src.AsSpan(0, _len);
        var pos = 0; var prevBlank = true; var inDialogue = false; sbyte speaker = -1; short section = 0;
        while (pos < src.Length && _tokN < MaxTokens)
        {
            var nl = src[pos..].IndexOf('\n');
            var end = nl < 0 ? src.Length : pos + nl;
            Trim(src, pos, end, out var s, out var e);
            var next = end + 1;
            if (s >= e) { prevBlank = true; inDialogue = false; pos = next; continue; }
            var line = src[s..e];
            var nextBlank = NextBlank(src, next);
            if (line.StartsWith("# "))
            {
                if (_secN < MaxSections)
                {
                    Trim(src, s + 2, e, out var ns, out var ne);
                    section = (short)_secN; _secStart[_secN] = ns; _secLen[_secN] = ne - ns; _secFirst[_secN] = _tokN; _secN++;
                    Add(ScriptTokenKind.Section, ns, ne - ns, -1, section);
                }
            }
            else if (line.StartsWith("??"))
            {
                Trim(src, s + 2, e, out var bs, out var be);
                if (_branchN < MaxBranches) { _branchTok[_branchN] = _tokN; _branchOptN[_branchN] = 0; }
                Add(ScriptTokenKind.Branch, bs, be - bs, -1, section).Param = (short)_branchN;
                if (_branchN < MaxBranches) _branchN++;
            }
            else if (line[0] == '=' && (line.Length == 1 || line[1] != '='))
            {
                ref var tk = ref Add(ScriptTokenKind.Beat, s + 1, e - s - 1, -1, section);
                var bar = line.IndexOf('|');
                if (bar > 0) { Trim(src, s + 1, s + bar, out var ts, out var te); tk.Start = ts; tk.Length = te - ts; }
                tk.Tension = Tag(line, "T=", float.NaN);
                tk.Affinity = Tag(line, "A=", 0f);
            }
            else if (IsScene(line))
            {
                var o = line[0] == '.' ? 1 : 0;
                Add(ScriptTokenKind.Scene, s + o, e - s - o, -1, section).Param = (short)_sceneN++;
                inDialogue = false;
            }
            else if (IsTransition(line))
            {
                var o = line[0] == '>' ? 1 : 0;
                Add(ScriptTokenKind.Transition, s + o, e - s - o, -1, section);
            }
            else if (inDialogue)
                Add(line[0] == '(' ? ScriptTokenKind.Parenthetical : ScriptTokenKind.Dialogue, s, e - s, speaker, section);
            else if (prevBlank && !nextBlank && (line[0] == '@' || IsCaps(line)))
            {
                var ns = line[0] == '@' ? s + 1 : s;
                var ne = e;
                var paren = src[ns..e].IndexOf('(');
                if (paren > 0) ne = ns + paren;
                Trim(src, ns, ne, out ns, out ne);
                speaker = (sbyte)Cast(src[ns..ne], ns);
                Add(ScriptTokenKind.Character, ns, ne - ns, speaker, section);
                inDialogue = true;
            }
            else Add(ScriptTokenKind.Action, s, e - s, -1, section);
            prevBlank = false;
            pos = next;
        }
        ResolveBranches();
        LastParseUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return _tokN > 0;
    }

    private ref ScriptToken Add(ScriptTokenKind k, int start, int len, sbyte actor, short section)
    {
        ref var t = ref _tok[_tokN++];
        t = default;
        t.Kind = k; t.Start = start; t.Length = len; t.Actor = actor; t.Section = section; t.Tension = float.NaN;
        return ref t;
    }

    private int Cast(ReadOnlySpan<char> name, int start)
    {
        for (var i = 0; i < _castN; i++)
            if (name.Equals(CastName(i), StringComparison.OrdinalIgnoreCase)) return i;
        if (_castN >= MaxCast) return MaxCast - 1;
        _castStart[_castN] = start; _castLen[_castN] = name.Length;
        return _castN++;
    }

    /// <summary>분기 옵션 이름 → 섹션 인덱스('|' 구분 · 미해결 옵션 무시).</summary>
    private void ResolveBranches()
    {
        for (var b = 0; b < _branchN; b++)
        {
            ref readonly var tk = ref _tok[_branchTok[b]];
            var rest = _src.AsSpan(tk.Start, tk.Length);
            var off = tk.Start; var n = 0;
            while (rest.Length > 0 && n < MaxOptions)
            {
                var bar = rest.IndexOf('|');
                var len = bar < 0 ? rest.Length : bar;
                Trim(_src, off, off + len, out var os, out var oe);
                ReadOnlySpan<char> name = _src.AsSpan(os, oe - os);
                for (var k = 1; k < _secN; k++)
                    if (name.Length > 0 && name.Equals(SectionName(k), StringComparison.OrdinalIgnoreCase)) { _branchOpt[b * MaxOptions + n++] = (short)k; break; }
                if (bar < 0) break;
                rest = rest[(bar + 1)..]; off += bar + 1;
            }
            _branchOptN[b] = (byte)n;
        }
    }

    private static void Trim(ReadOnlySpan<char> src, int s, int e, out int ts, out int te)
    {
        while (s < e && char.IsWhiteSpace(src[s])) s++;
        while (e > s && char.IsWhiteSpace(src[e - 1])) e--;
        ts = s; te = e;
    }

    private static bool NextBlank(ReadOnlySpan<char> src, int pos)
    {
        for (var i = pos; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '\n') return true;
            if (!char.IsWhiteSpace(c)) return false;
        }
        return true;
    }

    private static bool IsScene(ReadOnlySpan<char> l) =>
        l.StartsWith("INT.", StringComparison.OrdinalIgnoreCase) || l.StartsWith("EXT.", StringComparison.OrdinalIgnoreCase) ||
        l.StartsWith("INT/EXT", StringComparison.OrdinalIgnoreCase) || l.StartsWith("I/E", StringComparison.OrdinalIgnoreCase) ||
        l.StartsWith("EST.", StringComparison.OrdinalIgnoreCase) || l.StartsWith("S#") || (l.Length > 1 && l[0] == '.' && l[1] != '.');

    private static bool IsTransition(ReadOnlySpan<char> l) =>
        (l[0] == '>' && l[^1] != '<') || (l.EndsWith("TO:") && IsCaps(l));

    private static bool IsCaps(ReadOnlySpan<char> l)
    {
        var upper = false;
        for (var i = 0; i < l.Length; i++)
        {
            var c = l[i];
            if (c == '(') break;
            if (char.IsLower(c)) return false;
            if (char.IsUpper(c)) upper = true;
        }
        return upper;
    }

    private static float Tag(ReadOnlySpan<char> line, string key, float fallback)
    {
        var i = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return fallback;
        var v = line[(i + key.Length)..];
        var bar = v.IndexOf('|');
        if (bar >= 0) v = v[..bar];
        return float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
    }

    // ══════════════════════ 분석(지문/대사 · 무할당) ══════════════════════

    /// <summary>지문의 배우 언급(가장 앞) · 이동 동사.</summary>
    public ActorMotion Motion(ReadOnlySpan<char> text, out int actor)
    {
        actor = -1; var best = int.MaxValue;
        for (var i = 0; i < _castN; i++)
        {
            var p = text.IndexOf(CastName(i), StringComparison.OrdinalIgnoreCase);
            if (p >= 0 && p < best) { best = p; actor = i; }
        }
        if (actor < 0) return ActorMotion.Hold;
        if (Any(text, ApproachVerbs)) return ActorMotion.Approach;
        if (Any(text, RetreatVerbs)) return ActorMotion.Retreat;
        if (Any(text, CrossVerbs)) return ActorMotion.Cross;
        return ActorMotion.Hold;
    }

    /// <summary>가장 강한 폴리 어휘(재질 · 강도) — 없으면 false.</summary>
    public static bool Foley(ReadOnlySpan<char> text, out FoleyMaterial material, out float intensity)
    {
        material = FoleyMaterial.Wood; intensity = 0f;
        for (var i = 0; i < FoleyCues.Length; i++)
            if (FoleyCues[i].Intensity > intensity && text.IndexOf(FoleyCues[i].Word, StringComparison.OrdinalIgnoreCase) >= 0) { material = FoleyCues[i].Material; intensity = FoleyCues[i].Intensity; }
        return intensity > 0f;
    }

    /// <summary>대사 강도 0..1(갈등 어휘 · 느낌표/물음표 · 짧은 단문 · 말줄임 감쇄).</summary>
    public static float LineIntensity(ReadOnlySpan<char> text)
    {
        var v = 0.15f;
        for (var i = 0; i < ConflictWords.Length; i++)
            if (text.IndexOf(ConflictWords[i], StringComparison.OrdinalIgnoreCase) >= 0) v += 0.12f;
        var bang = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '!') bang += 0.1f; else if (c == '?') v += 0.05f; else if (c == '…') v -= 0.05f;
        }
        if (text.IndexOf("...") >= 0) v -= 0.05f;
        if (text.Length < 8) v += 0.05f;
        return Math.Clamp(v + MathF.Min(bang, 0.3f), 0f, 1f);
    }

    private static bool Any(ReadOnlySpan<char> text, string[] words)
    {
        for (var i = 0; i < words.Length; i++) if (text.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // ══════════════════════ 스케줄(무할당) ══════════════════════

    /// <summary>섹션 시작 토큰부터 다음 섹션/분기점/끝까지 5 트랙 재편성(기존 트랙 폐기 · 발화 링 비움).</summary>
    public void Schedule(int section, double t0)
    {
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        section = Math.Clamp(section, 0, _secN - 1);
        for (var k = 0; k < Tracks; k++) _evN[k] = _cursor[k] = 0;
        _fireTail = _fireHead;
        ScheduledSection = section; ScheduleStartS = t0; PendingBranch = -1;
        var t = t0;
        sbyte lastSpeaker = -1; byte lastCam = 255;
        var i = _secFirst[section];
        if (i < _tokN && _tok[i].Kind == ScriptTokenKind.Section) i++;
        for (; i < _tokN; i++)
        {
            ref readonly var tk = ref _tok[i];
            if (tk.Section != section) break;
            var text = Text(i);
            switch (tk.Kind)
            {
                case ScriptTokenKind.Scene:
                    Push(TimelineTrack.Script, t, SceneS, i, -1, (byte)tk.Kind, tk.Param, 0f);
                    Push(TimelineTrack.Camera, t, SceneS, i, -1, (byte)DirectorCam.Master, 1f, 0f);
                    lastCam = (byte)DirectorCam.Master; lastSpeaker = -1;
                    t += SceneS;
                    break;
                case ScriptTokenKind.Beat:
                    Push(TimelineTrack.Script, t, 0f, i, -1, (byte)tk.Kind, tk.Tension, tk.Affinity);
                    break;
                case ScriptTokenKind.Transition:
                    Push(TimelineTrack.Script, t, TransitionS, i, -1, (byte)tk.Kind, 0f, 0f);
                    t += TransitionS;
                    break;
                case ScriptTokenKind.Parenthetical:
                    t += ParenS;
                    break;
                case ScriptTokenKind.Action:
                {
                    var dur = Math.Clamp(text.Length * CharS, ActionMinS, ActionMaxS);
                    Push(TimelineTrack.Script, t, dur, i, -1, (byte)tk.Kind, 0f, 0f);
                    var m = Motion(text, out var actor);
                    if (m != ActorMotion.Hold) Push(TimelineTrack.Actor, t, dur, i, (sbyte)actor, (byte)m, 1f, 0f);
                    if (Foley(text, out var mat, out var inten))
                    {
                        var ft = t + 0.4f * dur;
                        Push(TimelineTrack.Foley, ft, 0.5f, i, (sbyte)actor, (byte)mat, inten, 0f);
                        if (inten >= 0.75f && lastCam != (byte)DirectorCam.Action)
                        {
                            Push(TimelineTrack.Camera, ft, dur * 0.6f, i, (sbyte)actor, (byte)DirectorCam.Action, inten, 0f);
                            lastCam = (byte)DirectorCam.Action; lastSpeaker = -1;
                        }
                    }
                    t += dur;
                    break;
                }
                case ScriptTokenKind.Dialogue:
                {
                    var dur = (float)(RealtimeNeuralVoiceCloningDubbingEngine.Plan(Lang, text) / (double)RealtimeNeuralVoiceCloningDubbingEngine.Fs);
                    var inten = LineIntensity(text);
                    if (tk.Actor != lastSpeaker)
                    {
                        var cam = (byte)(tk.Actor == 1 ? DirectorCam.CamB : DirectorCam.CamA);
                        Push(TimelineTrack.Camera, t, dur, i, tk.Actor, cam, inten, 0f);
                        lastSpeaker = tk.Actor; lastCam = cam;
                    }
                    Push(TimelineTrack.Voice, t, dur, i, tk.Actor, (byte)Lang, inten, 0f);
                    t += dur + LineGapS;
                    break;
                }
                case ScriptTokenKind.Branch:
                    Push(TimelineTrack.Script, t, 0f, i, -1, (byte)tk.Kind, tk.Param, 0f);
                    PendingBranch = tk.Param;
                    i = _tokN;
                    break;
            }
        }
        ScheduleEndS = t;
        LastScheduleUs = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Push(TimelineTrack track, double t, float dur, int token, sbyte actor, byte param, float value, float value2)
    {
        var k = (int)track;
        if (_evN[k] >= TrackCap) { Dropped++; return; }
        ref var e = ref _ev[k * TrackCap + _evN[k]++];
        e.Track = track; e.StartS = t; e.DurS = dur; e.Token = token; e.Actor = actor; e.Param = param; e.Value = value; e.Value2 = value2;
    }

    // ══════════════════════ 60 fps 틱(무할당) ══════════════════════

    /// <summary>재생 시각까지 도래한 이벤트를 트랙 순서(Script → Actor → Camera → Foley → Voice)로 발화 링에 적재.</summary>
    public int Tick(double now)
    {
        var n = 0;
        for (var k = 0; k < Tracks; k++)
        {
            var c = _cursor[k];
            while (c < _evN[k] && _ev[k * TrackCap + c].StartS <= now + 1e-9)
            {
                ref readonly var e = ref _ev[k * TrackCap + c];
                if (_fireHead - _fireTail >= FireRing) { Dropped++; break; }
                _fire[_fireHead++ % FireRing] = e;
                var late = (float)((now - e.StartS) * 1000.0);
                if (late > MaxLatenessMs) MaxLatenessMs = late;
                c++; n++;
            }
            _cursor[k] = c;
        }
        Fired += n;
        return n;
    }

    public bool TryDequeue(out TimelineEvent e)
    {
        if (_fireTail == _fireHead) { e = default; return false; }
        e = _fire[_fireTail++ % FireRing];
        return true;
    }

    public void ResetMetrics() { Fired = Dropped = 0; MaxLatenessMs = 0f; }

    public static string TrackName(TimelineTrack t) => t switch
    {
        TimelineTrack.Script => "Script", TimelineTrack.Actor => "Actor", TimelineTrack.Camera => "Camera", TimelineTrack.Foley => "Foley", _ => "Voice",
    };

    public static string MotionName(ActorMotion m) => m switch { ActorMotion.Approach => "접근", ActorMotion.Retreat => "후퇴", ActorMotion.Cross => "횡단", _ => "정지" };
}
