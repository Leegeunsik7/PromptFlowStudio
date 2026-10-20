using System.Globalization;
using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Newsroom;

namespace PromptFlowStudio.Services.Accessibility;

public enum SignLanguage : byte { Ksl, Asl }

public enum SignMotion : byte { Still, Tap, Circle, Arc, Shake, Twist }

public enum NonManual : byte { Neutral, Question, Negation, Emphasis }

public enum PipCorner : byte { BottomRight, BottomLeft, TopRight, TopLeft }

/// <summary>손 모양(엄지~새끼 굽힘 0 = 폄 · 1 = 쥠).</summary>
public readonly record struct HandShape(float Thumb, float Index, float Middle, float Ring, float Pinky)
{
    public float this[int f] => f switch { 0 => Thumb, 1 => Index, 2 => Middle, 3 => Ring, _ => Pinky };
}

/// <summary>수어 사전 항목(우세 손 모양/위치 · 보조 손 · 움직임 · 길이).</summary>
public sealed record SignDef(string Gloss, HandShape Right, Vector3 Location, SignMotion Motion, float Duration, HandShape? Left = null, Vector3? LeftLocation = null);

/// <summary>시간 배치된 수어 토큰(지문자 여부 · 비수지 기호).</summary>
public readonly record struct SignToken(string Gloss, SignDef Def, double Start, double Duration, NonManual Face, bool Fingerspelled);

/// <summary>자막 큐.</summary>
public readonly record struct A11yCue(double Start, double End, string Text);

/// <summary>얼굴 비수지 상태(눈썹 · 고개 · 입).</summary>
public struct SignFaceState
{
    public float BrowRaise, HeadYaw, HeadPitch, MouthOpen;
}

/// <summary>PiP 배치(모서리 · 화면 높이 대비 크기 · 여백).</summary>
public readonly record struct PipLayout(PipCorner Corner, float Scale, float Margin)
{
    public static PipLayout Default => new(PipCorner.BottomRight, 0.32f, 0.03f);
}

/// <summary>
/// [PromptFlow Studio v16.47.0] 🤟 AI 수어 번역 &amp; 3D 아바타 PiP 합성기.
///   ▸ 텍스트 → 수어: 한국어 조사/어미 제거 · 영어 관사/be동사 생략(글로스 문법) · 의문(눈썹 올림)/부정(고개 흔듦)/강조(입) 비수지 기호 · 사전 미등재어는 지문자(KSL 자모 분해 · ASL 알파벳).
///   ▸ 자막 큐 시간 맞춤: 큐 구간에 토큰 배치(최대 ×1.6 가속 · 최소 0.2초) — 지연 없이 대사와 동기.
///   ▸ 3D 골격: v16.36.0 디지털 트윈 리그(21 관절 · RigProportions.FromTwin) · 2-본 IK 팔 · 손가락 10개 굽힘 · 토큰 간 이즈 전환 · 움직임(탭/원/호/흔들기/비틀기) — 평가 무할당.
///   ▸ PiP 렌더: 우측 하단(설정 가능) 패널 · 상체 · 머리(트윈 피부색) · 눈썹/입 · 손가락 마이크로 제스처 — RGBA 합성 무할당.
///   ▸ 사전은 시연용 근사 동작이며 방송 송출 전 공인 수어 통역사 검수가 필요합니다.
/// </summary>
public sealed class AiSignLanguageAvatarSynthesizer
{
    public const float MaxSpeedUp = 1.6f, MinTokenSec = 0.2f, BlendSec = 0.15f, FingerspellSec = 0.32f;

    public static readonly HandShape Flat = new(0.2f, 0, 0, 0, 0), Fist = new(0.5f, 1, 1, 1, 1), Point = new(0.7f, 0, 1, 1, 1), Vee = new(0.8f, 0, 0, 1, 1),
        Yshape = new(0, 1, 1, 1, 0), Lshape = new(0, 0, 1, 1, 1), Oshape = new(0.6f, 0.65f, 0.65f, 0.65f, 0.65f), Cshape = new(0.35f, 0.4f, 0.4f, 0.4f, 0.4f),
        Ily = new(0, 0, 1, 1, 0), ThumbUp = new(0, 1, 1, 1, 1), Wshape = new(0.8f, 0, 0, 0, 1), Bshape = new(0.9f, 0, 0, 0, 0), Rest = new(0.3f, 0.3f, 0.35f, 0.4f, 0.45f);

    private static readonly HandShape[] SpellShapes = { Flat, Fist, Point, Vee, Yshape, Lshape, Oshape, Cshape, Ily, ThumbUp, Wshape, Bshape };

    // 위치(가슴 기준 · 헤드 단위 · 우세 손 = −X 측)
    public static readonly Vector3 Forehead = new(-0.2f, 1.75f, 0.5f), Chin = new(-0.1f, 1.05f, 0.55f), Chest = new(-0.25f, 0.05f, 0.65f), Neutral = new(-0.6f, -0.25f, 1.2f),
        Side = new(-1.2f, 0.3f, 0.85f), OppShoulder = new(0.55f, 0.55f, 0.45f), High = new(-0.8f, 1.8f, 0.9f), Low = new(-0.5f, -0.9f, 1.0f);

    private static readonly Dictionary<string, SignDef> Ksl = BuildKsl();
    private static readonly Dictionary<string, SignDef> Asl = BuildAsl();

    private static readonly string[] KoJosa = { "에서는", "에서", "으로", "에게", "한테", "까지", "부터", "하고", "이랑", "은", "는", "이", "가", "을", "를", "에", "의", "도", "로", "와", "과", "랑", "만" };
    private static readonly string[] KoEnding = { "했습니다", "합니다", "입니다", "습니다", "됩니다", "하세요", "해요", "이에요", "예요", "어요", "아요", "했다", "한다", "이다", "니다", "까요", "나요", "죠" };
    private static readonly string[] EnDrop = { "a", "an", "the", "is", "are", "am", "was", "were", "be", "to", "of", "do", "does" };
    private static readonly string[] EnNeg = { "not", "no", "never", "don't", "can't", "won't", "isn't" };
    private static readonly string[] KoQ = { "어디", "무엇", "뭐", "왜", "언제", "누구", "어떻게" };
    private static readonly string[] EnQ = { "what", "where", "why", "when", "who", "how" };

    private readonly object _gate = new();
    private SignToken[] _tokens = Array.Empty<SignToken>();
    private RigProportions _rig = RigProportions.Adult;
    private uint _skin = 0xC8A07F, _shirt = 0x1E3A8A;
    private long _frames, _signed, _spelled;

    public event Action? OnChange;

    public SignLanguage Language { get; set; } = SignLanguage.Ksl;
    public PipLayout Layout { get; set; } = PipLayout.Default;
    public IReadOnlyList<SignToken> Tokens { get { lock (_gate) return _tokens; } }
    public string RigLabel => _rig.Label;
    public uint SkinColor => _skin;
    public long Frames => Interlocked.Read(ref _frames);
    public long SignedTokens => Interlocked.Read(ref _signed);
    public long SpelledTokens => Interlocked.Read(ref _spelled);
    public double TimelineEnd { get { lock (_gate) return _tokens.Length == 0 ? 0 : _tokens[^1].Start + _tokens[^1].Duration; } }
    public string? LastAction { get; private set; }

    // ══════════════════════ 사전 ══════════════════════

    private static Dictionary<string, SignDef> BuildKsl()
    {
        var d = new Dictionary<string, SignDef>(StringComparer.Ordinal);
        void A(string g, HandShape r, Vector3 loc, SignMotion m, float dur, HandShape? l = null) => d[g] = new SignDef(g, r, loc, m, dur, l, l is null ? null : Mirror(loc));
        A("안녕", Flat, Side, SignMotion.Arc, 0.7f);
        A("감사", Flat, Chin, SignMotion.Arc, 0.6f, Flat);
        A("사랑", Fist, Chest, SignMotion.Circle, 0.8f, Fist);
        A("네", Fist, Neutral, SignMotion.Tap, 0.45f);
        A("아니", Flat, Neutral, SignMotion.Shake, 0.55f);
        A("오늘", Point, Low, SignMotion.Tap, 0.5f, Point);
        A("날씨", Flat, High, SignMotion.Arc, 0.7f, Flat);
        A("좋", ThumbUp, Chin, SignMotion.Tap, 0.5f);
        A("나", Point, Chest, SignMotion.Tap, 0.4f);
        A("저", Point, Chest, SignMotion.Tap, 0.4f);
        A("너", Point, Neutral, SignMotion.Still, 0.4f);
        A("우리", Flat, Chest, SignMotion.Circle, 0.6f);
        A("학교", Flat, Neutral, SignMotion.Tap, 0.6f, Flat);
        A("공부", Vee, Forehead, SignMotion.Tap, 0.6f, Bshape);
        A("뉴스", Cshape, Neutral, SignMotion.Arc, 0.6f, Cshape);
        A("지진", Flat, Neutral, SignMotion.Shake, 0.8f, Flat);
        A("비", Wshape, High, SignMotion.Arc, 0.7f, Wshape);
        A("사람", Point, Chest, SignMotion.Arc, 0.5f);
        A("집", Bshape, High, SignMotion.Still, 0.6f, Bshape);
        A("밥", Vee, Chin, SignMotion.Tap, 0.5f, Flat);
        A("물", Wshape, Chin, SignMotion.Tap, 0.5f);
        A("시간", Point, OppShoulder, SignMotion.Tap, 0.5f);
        A("어디", Point, Neutral, SignMotion.Shake, 0.6f);
        A("무엇", Point, Neutral, SignMotion.Shake, 0.6f);
        A("왜", Yshape, Forehead, SignMotion.Twist, 0.6f);
        A("도움", Fist, Neutral, SignMotion.Arc, 0.6f, Flat);
        A("미안", Fist, Chest, SignMotion.Circle, 0.7f);
        A("영상", Lshape, Neutral, SignMotion.Circle, 0.6f, Lshape);
        A("발생", Fist, Low, SignMotion.Arc, 0.6f);
        A("피해", Fist, OppShoulder, SignMotion.Tap, 0.6f);
        return d;
    }

    private static Dictionary<string, SignDef> BuildAsl()
    {
        var d = new Dictionary<string, SignDef>(StringComparer.Ordinal);
        void A(string g, HandShape r, Vector3 loc, SignMotion m, float dur, HandShape? l = null) => d[g] = new SignDef(g, r, loc, m, dur, l, l is null ? null : Mirror(loc));
        A("hello", Bshape, Forehead, SignMotion.Arc, 0.6f);
        A("thank", Flat, Chin, SignMotion.Arc, 0.6f);
        A("you", Point, Neutral, SignMotion.Still, 0.4f);
        A("i", Point, Chest, SignMotion.Tap, 0.4f);
        A("me", Point, Chest, SignMotion.Tap, 0.4f);
        A("love", Fist, Chest, SignMotion.Still, 0.7f, Fist);
        A("yes", Fist, Neutral, SignMotion.Tap, 0.45f);
        A("no", Vee, Neutral, SignMotion.Tap, 0.45f);
        A("today", Yshape, Low, SignMotion.Tap, 0.55f, Yshape);
        A("weather", Wshape, High, SignMotion.Arc, 0.7f, Wshape);
        A("good", Flat, Chin, SignMotion.Arc, 0.55f, Flat);
        A("school", Flat, Neutral, SignMotion.Tap, 0.6f, Flat);
        A("learn", Oshape, Forehead, SignMotion.Arc, 0.6f, Flat);
        A("news", Flat, Neutral, SignMotion.Arc, 0.6f, Flat);
        A("rain", Cshape, High, SignMotion.Arc, 0.7f, Cshape);
        A("people", Point, Neutral, SignMotion.Circle, 0.6f, Point);
        A("home", Oshape, Chin, SignMotion.Tap, 0.55f);
        A("eat", Oshape, Chin, SignMotion.Tap, 0.5f);
        A("water", Wshape, Chin, SignMotion.Tap, 0.5f);
        A("time", Point, OppShoulder, SignMotion.Tap, 0.5f);
        A("where", Point, Neutral, SignMotion.Shake, 0.6f);
        A("what", Flat, Neutral, SignMotion.Shake, 0.6f, Flat);
        A("why", Yshape, Forehead, SignMotion.Twist, 0.6f);
        A("help", ThumbUp, Neutral, SignMotion.Arc, 0.6f, Flat);
        A("sorry", Fist, Chest, SignMotion.Circle, 0.7f);
        A("please", Flat, Chest, SignMotion.Circle, 0.7f);
        A("name", Vee, Neutral, SignMotion.Tap, 0.55f, Vee);
        return d;
    }

    public static Vector3 Mirror(Vector3 v) => new(-v.X, v.Y, v.Z);

    public static bool TryLookup(SignLanguage lang, string gloss, out SignDef def) => (lang == SignLanguage.Ksl ? Ksl : Asl).TryGetValue(gloss, out def!);

    public static int LexiconSize(SignLanguage lang) => (lang == SignLanguage.Ksl ? Ksl : Asl).Count;

    // ══════════════════════ 텍스트 → 글로스 ══════════════════════

    /// <summary>한국어 어절 → 어간(조사 · 종결 어미 제거).</summary>
    public static string StemKo(string word)
    {
        var w = word;
        foreach (var e in KoEnding) if (w.Length > e.Length && w.EndsWith(e, StringComparison.Ordinal)) { w = w[..^e.Length]; break; }
        foreach (var j in KoJosa) if (w.Length > j.Length && w.EndsWith(j, StringComparison.Ordinal)) { w = w[..^j.Length]; break; }
        if (w.Length > 1 && w.EndsWith('하')) w = w[..^1];
        return w;
    }

    /// <summary>사전 최장 접두 일치(어간 변형 흡수 · 예: 좋아요 → 좋 · 감사합니다 → 감사).</summary>
    private static string? MatchLexicon(Dictionary<string, SignDef> lex, string stem)
    {
        if (lex.ContainsKey(stem)) return stem;
        for (var n = stem.Length - 1; n >= 1; n--)
        {
            var p = stem[..n];
            if (lex.ContainsKey(p)) return p;
        }
        return null;
    }

    /// <summary>문장 → 수어 토큰(시작 0 기준 · 자연 길이).</summary>
    public static List<SignToken> Translate(string text, SignLanguage lang)
    {
        var list = new List<SignToken>();
        var lex = lang == SignLanguage.Ksl ? Ksl : Asl;
        var face = NonManual.Neutral;
        var lower = text.ToLowerInvariant();
        if (text.Contains('?') || ContainsWord(lower, lang == SignLanguage.Ksl ? KoQ : EnQ, lang == SignLanguage.Asl)) face = NonManual.Question;
        else if (lang == SignLanguage.Ksl ? HasKoNegation(lower) : ContainsWord(lower, EnNeg, true)) face = NonManual.Negation;
        else if (text.Contains('!')) face = NonManual.Emphasis;
        double t = 0;
        foreach (var raw in lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Strip(raw);
            if (word.Length == 0) continue;
            if (lang == SignLanguage.Asl && Array.IndexOf(EnDrop, word) >= 0) continue;
            var stem = lang == SignLanguage.Ksl ? StemKo(word) : word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5 ? word[..^3] : word.EndsWith('s') && word.Length > 3 && !lex.ContainsKey(word) ? word[..^1] : word;
            var hit = lang == SignLanguage.Ksl ? MatchLexicon(lex, stem) : lex.ContainsKey(stem) ? stem : null;   // 접두 어간 흡수는 교착어(한국어) 전용
            if (hit is not null)
            {
                var def = lex[hit];
                list.Add(new SignToken(hit, def, t, def.Duration, face, false));
                t += def.Duration;
                continue;
            }
            foreach (var letter in Fingerspell(stem, lang))
            {
                list.Add(new SignToken(letter.Gloss, letter, t, letter.Duration, face, true));
                t += letter.Duration;
            }
        }
        return list;
    }

    private static bool ContainsWord(string lower, string[] words, bool wholeWord)
    {
        foreach (var w in words)
        {
            var i = lower.IndexOf(w, StringComparison.Ordinal);
            while (i >= 0)
            {
                if (!wholeWord) return true;
                var okL = i == 0 || !char.IsLetter(lower[i - 1]);
                var e = i + w.Length;
                var okR = e >= lower.Length || !char.IsLetter(lower[e]);
                if (okL && okR) return true;
                i = lower.IndexOf(w, i + 1, StringComparison.Ordinal);
            }
        }
        return false;
    }

    /// <summary>한국어 부정: 독립 부사(안/못) · 어절 어간(없-/않-/아니-) — 부분 문자열 오탐(안녕) 차단.</summary>
    public static bool HasKoNegation(string lower)
    {
        foreach (var raw in lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Strip(raw);
            if (w is "안" or "못") return true;
            if (w.StartsWith("없", StringComparison.Ordinal) || w.StartsWith("아니", StringComparison.Ordinal) || w.Contains("않", StringComparison.Ordinal) || w.Contains("없", StringComparison.Ordinal) && w.Length <= 4) return true;
        }
        return false;
    }

    private static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c) || c == '\'') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>지문자: KSL = 한글 음절 자모 분해(초/중/종) · ASL = 알파벳/숫자. 손 모양은 자모/문자 코드별 결정적 매핑.</summary>
    public static List<SignDef> Fingerspell(string word, SignLanguage lang)
    {
        var list = new List<SignDef>();
        foreach (var c in word)
        {
            if (c >= 0xAC00 && c <= 0xD7A3)
            {
                var s = c - 0xAC00;
                int cho = s / 588, jung = s % 588 / 28, jong = s % 28;
                list.Add(Letter(JamoName(0, cho), cho));
                list.Add(Letter(JamoName(1, jung), 19 + jung));
                if (jong > 0) list.Add(Letter(JamoName(2, jong), 40 + jong));
            }
            else if (char.IsLetterOrDigit(c)) list.Add(Letter("#" + char.ToUpperInvariant(c), c));
        }
        return list;
    }

    private static readonly string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ", Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ", Jong = " ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    private static string JamoName(int part, int i) => "#" + (part == 0 ? Cho[i] : part == 1 ? Jung[i] : Jong[i]).ToString();

    private static SignDef Letter(string gloss, int code)
    {
        var shape = SpellShapes[code % SpellShapes.Length];
        return new SignDef(gloss, shape, Side + new Vector3(0, (code % 3) * 0.08f, 0), SignMotion.Still, FingerspellSec);
    }

    // ══════════════════════ 자막 동기 타임라인 ══════════════════════

    /// <summary>자막 큐 → 큐 구간 내 토큰 배치(가속 상한 · 최소 길이 · 다음 큐 침범 금지).</summary>
    public static SignToken[] BuildTimeline(IReadOnlyList<A11yCue> cues, SignLanguage lang)
    {
        var all = new List<SignToken>();
        for (var c = 0; c < cues.Count; c++)
        {
            var cue = cues[c];
            var toks = Translate(cue.Text, lang);
            if (toks.Count == 0) continue;
            var natural = toks[^1].Start + toks[^1].Duration;
            var limit = c + 1 < cues.Count ? Math.Max(cue.End, Math.Min(cues[c + 1].Start, cue.End + 0.6)) : cue.End + 0.6;
            var window = Math.Max(0.2, limit - cue.Start);
            var scale = natural <= window ? 1.0 : Math.Max(natural / window, 1.0);
            if (scale > MaxSpeedUp) scale = natural / window;   // 상한 초과 → 창에 강제 맞춤(최소 길이 보장)
            var t = cue.Start;
            foreach (var tk in toks)
            {
                var d = Math.Max(MinTokenSec, tk.Duration / scale);
                if (t + d > limit + 1e-6) d = Math.Max(0.05, limit - t);
                if (d <= 0.05 && t >= limit) break;
                all.Add(tk with { Start = t, Duration = d });
                t += d;
            }
        }
        return all.ToArray();
    }

    public void LoadCaptions(IReadOnlyList<A11yCue> cues)
    {
        var tl = BuildTimeline(cues, Language);
        long spelled = 0;
        foreach (var t in tl) if (t.Fingerspelled) spelled++;
        lock (_gate) _tokens = tl;
        Interlocked.Add(ref _signed, tl.Length - spelled);
        Interlocked.Add(ref _spelled, spelled);
        LastAction = "🤟 " + (Language == SignLanguage.Ksl ? "KSL" : "ASL") + " 수어 " + tl.Length.ToString(CultureInfo.InvariantCulture) + "토큰 · 지문자 " + spelled.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>v16.36.0 디지털 트윈 → 아바타 체형(두신 · 어깨) · 피부색.</summary>
    public void BindTwin(DigitalTwinAsset twin)
    {
        _rig = RigProportions.FromTwin(twin);
        _skin = ParseHex(twin.SkinHex, 0xC8A07F);
        LastAction = "🧍 수어 아바타 · " + twin.Name;
        Raise();
    }

    public void SetShirt(uint rgb) => _shirt = rgb;

    public static uint ParseHex(string? hex, uint fallback)
        => hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static int TokenAt(ReadOnlySpan<SignToken> tokens, double t)
    {
        int lo = 0, hi = tokens.Length - 1, ans = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) >> 1;
            if (tokens[m].Start <= t) { ans = m; lo = m + 1; } else hi = m - 1;
        }
        return ans >= 0 && t < tokens[ans].Start + tokens[ans].Duration + 0.25 ? ans : -1;
    }

    // ══════════════════════ 포즈 평가(무할당) ══════════════════════

    private static Vector3 RestTarget(bool right) => right ? new Vector3(-0.55f, -1.15f, 0.35f) : new Vector3(0.55f, -1.15f, 0.35f);

    private static Vector3 MotionOffset(SignMotion m, float p)
    {
        switch (m)
        {
            case SignMotion.Tap: return new Vector3(0, 0, -0.18f * MathF.Abs(MathF.Sin(p * MathF.PI * 2)));
            case SignMotion.Circle: { var a = p * MathF.PI * 2; return new Vector3(0.18f * MathF.Cos(a) - 0.18f, 0.18f * MathF.Sin(a), 0); }
            case SignMotion.Arc: return new Vector3(-0.35f * p, -0.25f * p, 0.35f * p);
            case SignMotion.Shake: return new Vector3(0.14f * MathF.Sin(p * MathF.PI * 6), 0, 0);
            case SignMotion.Twist: return new Vector3(0, 0.06f * MathF.Sin(p * MathF.PI * 4), 0);
            default: return Vector3.Zero;
        }
    }

    private static float Ease(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    /// <summary>시각 t 포즈: 관절 21(헤드 단위 · Y-up) · 손가락 굽힘 10(우 5 · 좌 5) · 얼굴. 무할당.</summary>
    public void EvaluatePose(double t, Span<Vector3> joints, Span<float> curls, out SignFaceState face)
    {
        SignToken[] toks;
        lock (_gate) toks = _tokens;
        EvaluatePose(toks, _rig, t, joints, curls, out face);
    }

    public static void EvaluatePose(ReadOnlySpan<SignToken> toks, RigProportions rig, double t, Span<Vector3> joints, Span<float> curls, out SignFaceState face)
    {
        var L = rig.Length;
        joints[0] = Vector3.Zero;
        joints[1] = new Vector3(0, L[1], 0);
        joints[2] = joints[1] + new Vector3(0, L[2], 0);
        joints[3] = joints[2] + new Vector3(0, L[3], 0);
        joints[4] = joints[3] + new Vector3(0, L[4], 0);
        joints[5] = joints[2] + new Vector3(L[5], 0, 0);
        joints[9] = joints[2] + new Vector3(-L[9], 0, 0);
        joints[13] = new Vector3(L[13], 0, 0); joints[14] = joints[13] - new Vector3(0, L[14], 0); joints[15] = joints[14] - new Vector3(0, L[15], 0); joints[16] = joints[15] + new Vector3(0, 0, L[16]);
        joints[17] = new Vector3(-L[17], 0, 0); joints[18] = joints[17] - new Vector3(0, L[18], 0); joints[19] = joints[18] - new Vector3(0, L[19], 0); joints[20] = joints[19] + new Vector3(0, 0, L[20]);

        face = default;
        var i = TokenAt(toks, t);
        Vector3 rT = RestTarget(true), lT = RestTarget(false);
        HandShape rS = Rest, lS = Rest;
        if (i >= 0)
        {
            var tk = toks[i];
            var p = (float)Math.Clamp((t - tk.Start) / Math.Max(0.01, tk.Duration), 0, 1);
            var def = tk.Def;
            var chest = joints[2];
            var curR = chest + def.Location + MotionOffset(def.Motion, p);
            var curL = def.Left is null ? RestTarget(false) : chest + (def.LeftLocation ?? Mirror(def.Location)) + Mirror(MotionOffset(def.Motion, p));
            Vector3 prevR = RestTarget(true), prevL = RestTarget(false);
            HandShape prevRS = Rest, prevLS = Rest;
            if (i > 0 && toks[i - 1].Start + toks[i - 1].Duration >= tk.Start - 0.3)
            {
                var pd = toks[i - 1].Def;
                prevR = chest + pd.Location + MotionOffset(pd.Motion, 1);
                prevRS = pd.Right;
                if (pd.Left is { } pls) { prevL = chest + (pd.LeftLocation ?? Mirror(pd.Location)) + Mirror(MotionOffset(pd.Motion, 1)); prevLS = pls; }
            }
            var b = Ease((float)((t - tk.Start) / BlendSec));
            var after = t - (tk.Start + tk.Duration);
            if (after > 0) { var r = Ease((float)(after / 0.25)); rT = Vector3.Lerp(curR, RestTarget(true), r); lT = Vector3.Lerp(curL, RestTarget(false), r); rS = Lerp(def.Right, Rest, r); lS = Lerp(def.Left ?? Rest, Rest, r); }
            else { rT = Vector3.Lerp(prevR, curR, b); lT = Vector3.Lerp(prevL, curL, b); rS = Lerp(prevRS, def.Right, b); lS = Lerp(prevLS, def.Left ?? Rest, b); }
            if (after <= 0)
                switch (tk.Face)
                {
                    case NonManual.Question: face.BrowRaise = 1; face.HeadPitch = 0.12f; break;
                    case NonManual.Negation: face.HeadYaw = 0.18f * MathF.Sin((float)(t * 11)); face.BrowRaise = -0.5f; break;
                    case NonManual.Emphasis: face.MouthOpen = 0.6f; face.BrowRaise = 0.4f; break;
                }
            if (tk.Fingerspelled) face.MouthOpen = Math.Max(face.MouthOpen, 0.25f + 0.2f * MathF.Abs(MathF.Sin((float)(t * 9))));
        }
        SolveArm(joints[9], rT, L[10], L[11], new Vector3(-0.3f, -1, -0.25f), out joints[10], out joints[11]);
        SolveArm(joints[5], lT, L[6], L[7], new Vector3(0.3f, -1, -0.25f), out joints[6], out joints[7]);
        joints[12] = joints[11] + SafeDir(joints[11] - joints[10]) * L[12];
        joints[8] = joints[7] + SafeDir(joints[7] - joints[6]) * L[8];
        for (var f = 0; f < 5; f++) { curls[f] = rS[f]; curls[5 + f] = lS[f]; }
    }

    private static HandShape Lerp(HandShape a, HandShape b, float k)
        => new(a.Thumb + (b.Thumb - a.Thumb) * k, a.Index + (b.Index - a.Index) * k, a.Middle + (b.Middle - a.Middle) * k, a.Ring + (b.Ring - a.Ring) * k, a.Pinky + (b.Pinky - a.Pinky) * k);

    private static Vector3 SafeDir(Vector3 v) => v.LengthSquared() < 1e-8f ? -Vector3.UnitY : Vector3.Normalize(v);

    /// <summary>2-본 IK(어깨 → 목표 · 폴 벡터 방향 굽힘). 도달 불가 시 완전 신전.</summary>
    public static void SolveArm(Vector3 shoulder, Vector3 target, float upper, float lower, Vector3 pole, out Vector3 elbow, out Vector3 wrist)
    {
        var to = target - shoulder;
        var d = Math.Clamp(to.Length(), 1e-4f, upper + lower - 1e-4f);
        var dir = SafeDir(to);
        var cosA = Math.Clamp((upper * upper + d * d - lower * lower) / (2 * upper * d), -1f, 1f);
        var sinA = MathF.Sqrt(1 - cosA * cosA);
        var perp = pole - dir * Vector3.Dot(pole, dir);
        perp = perp.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(perp);
        elbow = shoulder + dir * (upper * cosA) + perp * (upper * sinA);
        wrist = shoulder + dir * d;
    }

    // ══════════════════════ PiP 렌더(무할당) ══════════════════════

    public static (int X, int Y, int W, int H) PipRect(PipLayout l, int w, int h)
    {
        var ph = (int)(h * Math.Clamp(l.Scale, 0.12f, 0.6f));
        var pw = ph * 3 / 4;
        var m = (int)(h * Math.Clamp(l.Margin, 0, 0.2f));
        var x = l.Corner is PipCorner.BottomRight or PipCorner.TopRight ? w - pw - m : m;
        var y = l.Corner is PipCorner.BottomRight or PipCorner.BottomLeft ? h - ph - m : m;
        return (x, y, pw, ph);
    }

    /// <summary>프레임 위 수어 아바타 PiP 합성(시각 t). 무할당.</summary>
    public void RenderPip(Span<byte> rgba, int w, int h, int stride, double t)
    {
        Span<Vector3> j = stackalloc Vector3[Rig.Joints];
        Span<float> curls = stackalloc float[10];
        EvaluatePose(t, j, curls, out var face);
        var (px, py, pw, ph) = PipRect(Layout, w, h);
        _clip = (px, py, px + pw, py + ph);
        NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, px, py, pw, ph, 0x0B1220, 230);
        Fill(rgba, w, h, stride, px, py, pw, Math.Max(2, ph / 90), 0x38BDF8, 255);
        var unit = ph / 5.4f;
        var cx = px + pw * 0.5f;
        var by = py + ph * 0.86f;   // 엉덩이 높이(하단 근처 · 상체 프레이밍)
        var thick = Math.Max(2, (int)(unit * 0.22f));
        var yaw = face.HeadYaw;
        // 몸통(셔츠)
        var sL = Proj(j[5], cx, by, unit); var sR = Proj(j[9], cx, by, unit); var hip = Proj(j[0], cx, by, unit);
        Fill(rgba, w, h, stride, (int)MathF.Min(sL.X, sR.X), (int)sL.Y, (int)MathF.Abs(sL.X - sR.X), (int)(hip.Y - sL.Y), _shirt, 255);
        Limb(rgba, w, h, stride, j[3], j[2], cx, by, unit, thick, _skin);
        // 팔(뒤 → 앞: Z 작은 팔 먼저)
        var rightFront = j[11].Z >= j[7].Z;
        for (var pass = 0; pass < 2; pass++)
        {
            var right = (pass == 1) == rightFront;
            int s = right ? 9 : 5, e = right ? 10 : 6, wr = right ? 11 : 7, tip = right ? 12 : 8;
            Limb(rgba, w, h, stride, j[s], j[e], cx, by, unit, thick + 1, _shirt);
            Limb(rgba, w, h, stride, j[e], j[wr], cx, by, unit, thick, _skin);
            DrawHand(rgba, w, h, stride, j[wr], j[tip], curls.Slice(right ? 0 : 5, 5), right, cx, by, unit, thick);
        }
        // 머리 · 얼굴
        var head = Proj(j[4] - new Vector3(0, _rig.Length[4] * 0.45f, 0), cx, by, unit);
        var hx = head.X + yaw * unit * 1.5f; var hy = head.Y + face.HeadPitch * unit;
        var hr = unit * _rig.Length[4] * 0.55f;
        Disc(rgba, w, h, stride, hx, hy, hr, _skin);
        var eyeY = hy - hr * 0.1f; var eo = hr * 0.38f;
        Disc(rgba, w, h, stride, hx - eo + yaw * hr, eyeY, Math.Max(1, hr * 0.1f), 0x111111);
        Disc(rgba, w, h, stride, hx + eo + yaw * hr, eyeY, Math.Max(1, hr * 0.1f), 0x111111);
        var browY = eyeY - hr * (0.28f + 0.14f * face.BrowRaise);
        Fill(rgba, w, h, stride, (int)(hx - eo - hr * 0.2f + yaw * hr), (int)browY, (int)(hr * 0.4f), Math.Max(1, (int)(hr * 0.08f)), 0x3F2A1E, 255);
        Fill(rgba, w, h, stride, (int)(hx + eo - hr * 0.2f + yaw * hr), (int)browY, (int)(hr * 0.4f), Math.Max(1, (int)(hr * 0.08f)), 0x3F2A1E, 255);
        var mh = Math.Max(1, (int)(hr * (0.06f + 0.3f * face.MouthOpen)));
        Fill(rgba, w, h, stride, (int)(hx - hr * 0.25f + yaw * hr), (int)(hy + hr * 0.45f), (int)(hr * 0.5f), mh, 0x7F1D1D, 255);
        Interlocked.Increment(ref _frames);
    }

    private (int X0, int Y0, int X1, int Y1) _clip;

    /// <summary>PiP 영역 클립 사각 채우기.</summary>
    private void Fill(Span<byte> d, int w, int h, int stride, int x, int y, int rw, int rh, uint col, int alpha)
    {
        int x0 = Math.Max(x, _clip.X0), y0 = Math.Max(y, _clip.Y0), x1 = Math.Min(x + rw, _clip.X1), y1 = Math.Min(y + rh, _clip.Y1);
        if (x1 > x0 && y1 > y0) NewsMotionGraphicsOverlayEngine.FillRect(d, w, h, stride, x0, y0, x1 - x0, y1 - y0, col, alpha);
    }

    private static Vector2 Proj(Vector3 p, float cx, float by, float unit)
    {
        var persp = 1 + p.Z * 0.06f;
        return new Vector2(cx + p.X * unit * persp, by - p.Y * unit * persp);
    }

    private void Limb(Span<byte> d, int w, int h, int stride, Vector3 a, Vector3 b, float cx, float by, float unit, int thick, uint col)
    {
        var pa = Proj(a, cx, by, unit); var pb = Proj(b, cx, by, unit);
        Line(d, w, h, stride, pa, pb, thick, col);
    }

    private void Line(Span<byte> d, int w, int h, int stride, Vector2 a, Vector2 b, int thick, uint col)
    {
        var steps = (int)Math.Clamp(Vector2.Distance(a, b), 1, 2048);
        var half = thick / 2;
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            Fill(d, w, h, stride, (int)p.X - half, (int)p.Y - half, thick, thick, col, 255);
        }
    }

    private void Disc(Span<byte> d, int w, int h, int stride, float cx, float cy, float r, uint col)
    {
        var ri = (int)MathF.Ceiling(r);
        for (var y = -ri; y <= ri; y++)
        {
            var span = (int)MathF.Sqrt(MathF.Max(0, r * r - y * y));
            Fill(d, w, h, stride, (int)cx - span, (int)cy + y, span * 2 + 1, 1, col, 255);
        }
    }

    /// <summary>손바닥 + 손가락 5개(굽힘 = 길이 단축 · 엄지는 측면) — 마이크로 제스처.</summary>
    private void DrawHand(Span<byte> d, int w, int h, int stride, Vector3 wrist, Vector3 tip, ReadOnlySpan<float> curl, bool right, float cx, float by, float unit, int thick)
    {
        var pw = Proj(wrist, cx, by, unit); var pt = Proj(tip, cx, by, unit);
        var axis = pt - pw;
        var len = axis.Length();
        if (len < 1e-3f) return;
        var dir = axis / len;
        var side = new Vector2(-dir.Y, dir.X) * (right ? 1 : -1);
        var palm = pw + dir * (len * 0.45f);
        Disc(d, w, h, stride, palm.X, palm.Y, Math.Max(2, len * 0.32f), _skin);
        var fw = Math.Max(1, thick / 2);
        for (var f = 1; f < 5; f++)
        {
            var baseP = palm + dir * (len * 0.25f) + side * ((f - 2.5f) * len * 0.17f);
            var flen = len * 0.62f * (1 - 0.8f * curl[f]);
            Line(d, w, h, stride, baseP, baseP + dir * flen, fw, _skin);
        }
        var tb = palm + side * (len * 0.38f);
        var tdir = Vector2.Normalize(side * 0.7f + dir * 0.7f);
        Line(d, w, h, stride, tb, tb + tdir * (len * 0.45f * (1 - 0.8f * curl[0])), fw, _skin);
    }

    // ══════════════════════ 내보내기 ══════════════════════

    /// <summary>수어 트랙 JSON(토큰 · 글로스 · 비수지 · 배치) — 배리어프리 패키지용.</summary>
    public string ExportTrackJson()
    {
        SignToken[] toks;
        lock (_gate) toks = _tokens;
        using var ms = new MemoryStream();
        using (var j = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("language", Language == SignLanguage.Ksl ? "KSL" : "ASL");
            j.WriteString("rig", _rig.Label);
            j.WriteString("pip", Layout.Corner.ToString());
            j.WriteNumber("pipScale", Math.Round(Layout.Scale, 3));
            j.WriteStartArray("tokens");
            foreach (var t in toks)
            {
                j.WriteStartObject();
                j.WriteString("gloss", t.Gloss);
                j.WriteNumber("start", Math.Round(t.Start, 3));
                j.WriteNumber("duration", Math.Round(t.Duration, 3));
                j.WriteString("motion", t.Def.Motion.ToString());
                j.WriteString("nonManual", t.Face.ToString());
                j.WriteBoolean("fingerspelled", t.Fingerspelled);
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sign.OnChange", ex); } }
}
