using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Virtual;   // CameraRigPreset · CinematicCameraMotionControlEngine.Phrase
using PromptFlowStudio.Services.Wan;       // GeminiClient

namespace PromptFlowStudio.Services.Screenplay;

[JsonConverter(typeof(JsonStringEnumConverter<StoryStructure>))]
public enum StoryStructure { ThreeAct, ShortForm }

[JsonConverter(typeof(JsonStringEnumConverter<ScriptElementKind>))]
public enum ScriptElementKind { Action, Dialogue, Camera, Transition }

/// <summary>비트 1개 — 막 · 위치(0~1) · 목적 · 긴장도(0~100) · 요약 · 등장 인물.</summary>
public sealed class Beat
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Act { get; set; }
    public double Position { get; set; }
    public string Purpose { get; set; } = "";
    public int Tension { get; set; }
    public string Summary { get; set; } = "";
    public List<string> CharacterIds { get; set; } = new();
}

public sealed class ScriptElement
{
    public ScriptElementKind Kind { get; set; }
    public string? Character { get; set; }
    public string? Parenthetical { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ScriptScene
{
    public int Number { get; set; }
    /// <summary>INT./EXT. 장소 - 시간대.</summary>
    public string Heading { get; set; } = "";
    public int BeatIndex { get; set; } = -1;
    public int Tension { get; set; }
    public double Seconds { get; set; }
    public List<ScriptElement> Elements { get; set; } = new();

    public IEnumerable<ScriptElement> Of(ScriptElementKind k) { foreach (var e in Elements) if (e.Kind == k) yield return e; }
}

public sealed class ScreenplayDoc
{
    public string Title { get; set; } = "";
    public StoryStructure Structure { get; set; }
    public string Source { get; set; } = "BCL";
    public List<Beat> Beats { get; set; } = new();
    public List<ScriptScene> Scenes { get; set; } = new();
    public double TotalSeconds { get { double s = 0; foreach (var x in Scenes) s += x.Seconds; return s; } }
}

/// <summary>
/// [PromptFlow Studio v16.35.0] 씬별 비트 시트 &amp; 전문 대본 자동 작성기.
///   ▸ 구조: 할리우드 3막(Save the Cat 15비트 · 위치/긴장 곡선) · 숏폼 하이라이트(훅 3초 → 셋업 → 고조 → 반전 → 페이오프 → CTA).
///   ▸ BCL 작가: 세계관 바이블(인물 Want/Need/Flaw · 장소 · 규칙 · 아크)로 비트 요약 → 씬(INT/EXT 헤딩 · 지문 · 인물 대사 · 괄호 지시 · 카메라 가이드 · 전환) 결정적 생성.
///   ▸ 카메라 가이드: 비트 긴장도/목적 → <see cref="CameraRigPreset"/> → 영문 구문(영상 생성 프롬프트 정합).
///   ▸ Fountain 직렬화/파서: 씬 헤딩(INT./EXT./INT/EXT./EST.) · 캐릭터 큐(대문자 또는 @강제) · 괄호 지시 · 대사 · 전환(…TO:) · 카메라 노트 [[CAM: …]] · 비트 노트 [[BEAT n]] · 길이 [[SEC n]].
///   ▸ Gemini(설정 시) Fountain 대본 → 파서 검증 → 씬 0/파싱 실패 시 BCL 폴백.
/// </summary>
public sealed class SceneBeatSheetScriptGenerator
{
    private readonly GeminiClient? _gemini;
    private int _busy;

    public SceneBeatSheetScriptGenerator(IServiceProvider sp) : this(sp.GetService(typeof(GeminiClient)) as GeminiClient) { }

    public SceneBeatSheetScriptGenerator(GeminiClient? gemini) => _gemini = gemini;

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool LlmAvailable => _gemini?.Enabled == true;
    public ScreenplayDoc? Current { get; private set; }
    public string? LastAction { get; private set; }

    public void SetCurrent(ScreenplayDoc? d) { Current = d; Raise(); }

    // ══════════════════════ 비트 시트 ══════════════════════

    private static readonly (string Name, int Act, double Pos, int Tension, string Purpose)[] SaveTheCat =
    {
        ("오프닝 이미지", 1, 0.00, 20, "주인공의 현재 세계를 한 컷으로 제시"),
        ("주제 제시", 1, 0.05, 25, "누군가 이야기의 주제를 넌지시 말함"),
        ("셋업", 1, 0.08, 30, "주인공의 결핍과 일상 · 주변 인물 소개"),
        ("촉매", 1, 0.10, 50, "일상을 깨뜨리는 사건 발생"),
        ("고민", 1, 0.15, 45, "주인공이 변화 앞에서 망설임"),
        ("2막 진입", 2, 0.20, 55, "주인공이 새로운 세계로 들어서기로 결심"),
        ("B 스토리", 2, 0.22, 40, "주제를 비추는 관계(사랑/우정/사제) 시작"),
        ("재미와 놀이", 2, 0.30, 50, "새 세계의 약속된 재미 · 장르적 쾌감"),
        ("중간점", 2, 0.50, 70, "가짜 승리 혹은 가짜 패배 · 판이 커짐"),
        ("악당이 다가옴", 2, 0.55, 75, "내외부 압박이 조여 옴"),
        ("모든 것을 잃음", 2, 0.75, 85, "가장 큰 상실 · 죽음의 냄새"),
        ("영혼의 어두운 밤", 2, 0.78, 60, "절망 속 진짜 필요를 깨달음"),
        ("3막 진입", 3, 0.80, 75, "A·B 스토리가 합쳐져 해법 발견"),
        ("피날레", 3, 0.85, 95, "새 깨달음으로 최종 대결 · 변화 증명"),
        ("파이널 이미지", 3, 0.99, 35, "오프닝과 대비되는 변화된 세계"),
    };

    private static readonly (string Name, int Act, double Pos, int Tension, string Purpose)[] ShortFormBeats =
    {
        ("훅", 1, 0.00, 80, "첫 3초 시선 고정 — 질문/충격/결과 먼저"),
        ("셋업", 1, 0.12, 40, "누가 · 어디서 · 무엇을 원하는지 한 문장"),
        ("고조", 2, 0.30, 65, "장애물 등장 · 기대감 상승"),
        ("반전", 2, 0.55, 90, "예상을 뒤집는 전환"),
        ("페이오프", 3, 0.78, 75, "감정적 보상 · 결과 공개"),
        ("CTA", 3, 0.95, 50, "여운 · 다음 편/참여 유도"),
    };

    public static List<Beat> BuildBeatSheet(WorldBible bible, StoryStructure structure)
    {
        var src = structure == StoryStructure.ThreeAct ? SaveTheCat : ShortFormBeats;
        var pro = bible.Protagonist ?? (bible.Characters.Count > 0 ? bible.Characters[0] : new CharacterProfile { Id = "c1", Name = "주인공" });
        var antagonist = bible.Characters.Find(static c => c.Role == CharacterRole.Antagonist);
        var mentor = bible.Characters.Find(static c => c.Role == CharacterRole.Mentor);
        var ally = bible.Characters.Find(static c => c.Role is CharacterRole.Ally or CharacterRole.Foil);
        var locs = bible.Lore.Locations.Count > 0 ? bible.Lore.Locations : new List<string> { "어딘가" };
        var beats = new List<Beat>(src.Length);
        for (var i = 0; i < src.Length; i++)
        {
            var (name, act, pos, tension, purpose) = src[i];
            var loc = locs[Math.Min(locs.Count - 1, (int)(pos * locs.Count))];
            var b = new Beat { Index = i, Name = name, Act = act, Position = pos, Tension = tension, Purpose = purpose };
            b.CharacterIds.Add(pro.Id);
            var other = (tension >= 70 ? antagonist : pos < 0.3 ? mentor : ally) ?? ally ?? mentor ?? antagonist;
            if (other is not null && other.Id != pro.Id) b.CharacterIds.Add(other.Id);
            var on = other?.Name ?? "누군가";
            b.Summary = name switch
            {
                "오프닝 이미지" or "훅" => pro.Name + "이(가) " + loc + "에서 " + (structure == StoryStructure.ShortForm ? "믿기 힘든 장면과 마주친다" : "'" + pro.Want + "'만 바라보며 하루를 시작한다"),
                "주제 제시" => on + "이(가) " + pro.Name + "에게 '" + (bible.Lore.Themes.Count > 0 ? bible.Lore.Themes[0] : "용기") + "'에 대해 말한다",
                "셋업" => pro.Name + "의 " + pro.Flaw + "이(가) 드러나고, " + on + "와(과)의 관계가 소개된다",
                "촉매" => (bible.Lore.Rules.Count > 0 ? bible.Lore.Rules[0] : "규칙") + " — 이 사실이 " + pro.Name + "의 일상을 뒤흔든다",
                "고민" => pro.Name + "은(는) " + loc + "에서 떠날지 말지 망설인다",
                "2막 진입" => pro.Name + "이(가) 결심하고 " + loc + "(으)로 향한다",
                "B 스토리" => on + "와(과)의 관계가 주제를 비추며 깊어진다",
                "재미와 놀이" or "고조" => pro.Name + "이(가) " + loc + "에서 새로운 세계의 규칙을 몸으로 익힌다",
                "중간점" => "가짜 승리 — " + pro.Name + "은(는) 원하던 것에 거의 닿지만 대가가 드러난다",
                "악당이 다가옴" => (antagonist?.Name ?? "압박") + "이(가) 조여 오고 동료 사이에 균열이 생긴다",
                "모든 것을 잃음" => pro.Name + "은(는) " + loc + "에서 가장 소중한 것을 잃는다",
                "영혼의 어두운 밤" => pro.Name + "은(는) 진짜 필요한 것이 '" + pro.Need + "'임을 깨닫는다",
                "3막 진입" => (mentor?.Name ?? on) + "의 말이 떠오르며 해법이 보인다",
                "피날레" or "반전" => pro.Name + "이(가) " + loc + "에서 " + (antagonist?.Name ?? on) + "와(과) 맞서며 " + pro.Flaw + "을(를) 극복한다",
                "페이오프" => "변화의 결과 — " + pro.Name + "와(과) " + on + "의 관계가 새로 정의된다",
                "파이널 이미지" => "같은 " + locs[0] + ", 그러나 달라진 " + pro.Name + "의 표정",
                "CTA" => pro.Name + "의 다음 여정을 암시하며 시청자에게 질문을 던진다",
                _ => purpose,
            };
            beats.Add(b);
        }
        return beats;
    }

    /// <summary>비트 긴장도/목적 → 카메라 리그 프리셋.</summary>
    public static CameraRigPreset CameraFor(Beat b) => b.Name switch
    {
        "오프닝 이미지" or "파이널 이미지" => CameraRigPreset.DroneRise,
        "훅" => CameraRigPreset.Handheld,
        "촉매" or "반전" => CameraRigPreset.DollyIn,
        "중간점" => CameraRigPreset.Orbit,
        "모든 것을 잃음" => CameraRigPreset.DollyOut,
        "영혼의 어두운 밤" => CameraRigPreset.StaticLockOff,
        "피날레" => CameraRigPreset.CraneUp,
        "페이오프" => CameraRigPreset.PanavisionReveal,
        _ => b.Tension >= 70 ? CameraRigPreset.GimbalFollow : b.Tension >= 45 ? CameraRigPreset.TruckRight : CameraRigPreset.PanRight,
    };

    // ══════════════════════ 대본 ══════════════════════

    /// <summary>비트 시트 → 대본(씬 = 비트 · 길이 = 목표 길이 × 비트 구간 비율).</summary>
    public static ScreenplayDoc WriteScript(WorldBible bible, List<Beat> beats, StoryStructure structure, double targetSeconds)
    {
        var doc = new ScreenplayDoc { Title = bible.Lore.Title, Structure = structure, Beats = beats };
        var total = Math.Clamp(targetSeconds, 10, 7200);
        var night = bible.Lore.Genre is StoryGenre.Thriller;
        for (var i = 0; i < beats.Count; i++)
        {
            var b = beats[i];
            var next = i + 1 < beats.Count ? beats[i + 1].Position : 1.0;
            var sec = Math.Max(2, Math.Round(total * Math.Max(0.02, next - b.Position), 1));
            var locs = bible.Lore.Locations;
            var loc = locs.Count == 0 ? "어딘가" : locs[Math.Min(locs.Count - 1, (int)(b.Position * locs.Count))];
            var exterior = loc.Contains("숲", StringComparison.Ordinal) || loc.Contains("골목", StringComparison.Ordinal) || loc.Contains("공원", StringComparison.Ordinal)
                           || loc.Contains("언덕", StringComparison.Ordinal) || loc.Contains("협곡", StringComparison.Ordinal) || loc.Contains("옥상", StringComparison.Ordinal) || loc.Contains("야적장", StringComparison.Ordinal);
            var time = night || b.Name is "영혼의 어두운 밤" ? "밤" : b.Position < 0.1 ? "아침" : b.Position > 0.9 ? "해질녘" : "낮";
            var scene = new ScriptScene { Number = i + 1, Heading = (exterior ? "EXT. " : "INT. ") + loc + " - " + time, BeatIndex = b.Index, Tension = b.Tension, Seconds = sec };
            scene.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Action, Text = b.Summary + "." });
            var plan = CameraFor(b);
            scene.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Camera, Text = CinematicCameraMotionControlEngine.Phrase(plan) });
            foreach (var id in b.CharacterIds)
            {
                var c = bible.Find(id);
                if (c is null) continue;
                var (paren, line) = Dialogue(c, b, bible);
                scene.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Dialogue, Character = c.Name, Parenthetical = paren, Text = line });
            }
            if (i + 1 < beats.Count) scene.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Transition, Text = b.Tension >= 80 ? "SMASH CUT TO:" : "CUT TO:" });
            doc.Scenes.Add(scene);
        }
        return doc;
    }

    private static (string? Paren, string Line) Dialogue(CharacterProfile c, Beat b, WorldBible bible)
    {
        var theme = bible.Lore.Themes.Count > 0 ? bible.Lore.Themes[0] : "용기";
        var arcIdx = Math.Clamp((int)(b.Position * 4), 0, 3);
        var mood = c.EmotionArc.Count > arcIdx ? c.EmotionArc[arcIdx] : 0f;
        var paren = mood <= -0.5f ? "떨리는 목소리로" : mood >= 0.6f ? "확신에 차서" : b.Tension >= 80 ? "다급하게" : null;
        var line = c.Role switch
        {
            CharacterRole.Protagonist => b.Name switch
            {
                "훅" or "오프닝 이미지" => "이게… 진짜라고?",
                "고민" => "내가 할 수 있을까. " + c.Flaw + " 때문에 또 망칠지도 몰라.",
                "2막 진입" or "셋업" => "좋아. " + c.Want + ", 이번엔 피하지 않을 거야.",
                "모든 것을 잃음" => "전부… 내 탓이야.",
                "영혼의 어두운 밤" => "내가 원한 건 " + c.Want + "이 아니었어. 필요한 건 " + c.Need + "였어.",
                "피날레" or "반전" => "이제 알아. " + theme + "은(는) 두려움이 없는 게 아니라, 두려워도 나아가는 거야.",
                "CTA" => "다음엔… 너라면 어떻게 할래?",
                _ => "여기서 멈출 순 없어.",
            },
            CharacterRole.Antagonist => b.Tension >= 85 ? "넌 결국 혼자야. 처음부터 그랬지." : "포기해. 네가 원하는 건 이곳에 없어.",
            CharacterRole.Mentor => b.Position < 0.3 ? theme + "은(는) 배우는 게 아니라 선택하는 거란다." : "기억하렴. 네가 가진 답은 이미 네 안에 있어.",
            CharacterRole.Narrator => "그날, 모든 것이 달라지기 시작했다.",
            _ => b.Tension >= 70 ? "내가 옆에 있을게. 같이 가자." : "너답지 않게 왜 그래?",
        };
        return (paren, line);
    }

    // ══════════════════════ Fountain ══════════════════════

    public static string ToFountain(ScreenplayDoc d)
    {
        var sb = new StringBuilder(2048);
        sb.Append("Title: ").Append(d.Title).Append('\n').Append("Credit: PromptFlow Studio v16.35.0\n\n");
        foreach (var s in d.Scenes)
        {
            sb.Append(s.Heading.ToUpperInvariant()).Append('\n');
            if (s.BeatIndex >= 0) sb.Append("[[BEAT ").Append(s.BeatIndex.ToString(CultureInfo.InvariantCulture)).Append("]]\n");
            sb.Append("[[SEC ").Append(s.Seconds.ToString("0.#", CultureInfo.InvariantCulture)).Append("]]\n\n");
            foreach (var e in s.Elements)
            {
                switch (e.Kind)
                {
                    case ScriptElementKind.Action: sb.Append(e.Text).Append("\n\n"); break;
                    case ScriptElementKind.Camera: sb.Append("[[CAM: ").Append(e.Text).Append("]]\n\n"); break;
                    case ScriptElementKind.Transition: sb.Append("> ").Append(e.Text).Append("\n\n"); break;
                    case ScriptElementKind.Dialogue:
                        var name = e.Character ?? "";
                        sb.Append(IsAsciiUpper(name) ? name : "@" + name).Append('\n');
                        if (!string.IsNullOrEmpty(e.Parenthetical)) sb.Append('(').Append(e.Parenthetical).Append(")\n");
                        sb.Append(e.Text).Append("\n\n");
                        break;
                }
            }
        }
        return sb.ToString();
    }

    private static bool IsAsciiUpper(string s)
    {
        if (s.Length == 0) return false;
        var letter = false;
        foreach (var c in s)
        {
            if (c > 127) return false;
            if (char.IsLower(c)) return false;
            letter |= char.IsLetter(c);
        }
        return letter;
    }

    private static bool IsHeading(string t)
    {
        var u = t.ToUpperInvariant();
        return u.StartsWith("INT.", StringComparison.Ordinal) || u.StartsWith("EXT.", StringComparison.Ordinal) || u.StartsWith("INT/EXT", StringComparison.Ordinal)
            || u.StartsWith("I/E", StringComparison.Ordinal) || u.StartsWith("EST.", StringComparison.Ordinal) || (t.Length > 1 && t[0] == '.' && t[1] != '.');
    }

    /// <summary>Fountain → 대본(관용적 파싱 · 제목 페이지/보니야드/섹션 무시).</summary>
    public static ScreenplayDoc ParseFountain(string? text, StoryStructure structure = StoryStructure.ShortForm)
    {
        var doc = new ScreenplayDoc { Structure = structure, Source = "Fountain" };
        if (string.IsNullOrWhiteSpace(text)) return doc;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        ScriptScene? cur = null;
        var i = 0;
        // 제목 페이지
        while (i < lines.Length && lines[i].Contains(':') && !IsHeading(lines[i].Trim()))
        {
            var kv = lines[i];
            var c = kv.IndexOf(':');
            if (kv[..c].Trim().Equals("Title", StringComparison.OrdinalIgnoreCase)) doc.Title = kv[(c + 1)..].Trim();
            i++;
        }
        var inBoneyard = false;
        for (; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t = raw.Trim();
            if (inBoneyard) { if (t.Contains("*/", StringComparison.Ordinal)) inBoneyard = false; continue; }
            if (t.StartsWith("/*", StringComparison.Ordinal)) { inBoneyard = !t.Contains("*/", StringComparison.Ordinal); continue; }
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith('=')) continue;
            if (IsHeading(t))
            {
                cur = new ScriptScene { Number = doc.Scenes.Count + 1, Heading = t[0] == '.' ? t[1..].Trim() : t };
                doc.Scenes.Add(cur);
                continue;
            }
            cur ??= AddImplicit(doc);
            if (t.StartsWith("[[", StringComparison.Ordinal) && t.EndsWith("]]", StringComparison.Ordinal))
            {
                var note = t[2..^2].Trim();
                if (note.StartsWith("CAM:", StringComparison.OrdinalIgnoreCase)) cur.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Camera, Text = note[4..].Trim() });
                else if (note.StartsWith("BEAT ", StringComparison.OrdinalIgnoreCase) && int.TryParse(note.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bi)) cur.BeatIndex = bi;
                else if (note.StartsWith("SEC ", StringComparison.OrdinalIgnoreCase) && double.TryParse(note.AsSpan(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) cur.Seconds = Math.Clamp(sec, 0.5, 600);
                continue;
            }
            if (t.StartsWith('>') && !t.EndsWith('<')) { cur.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Transition, Text = t[1..].Trim() }); continue; }
            if (IsAsciiUpper(t) && t.EndsWith("TO:", StringComparison.Ordinal)) { cur.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Transition, Text = t }); continue; }
            var next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
            var forced = t.StartsWith('@');
            var prevBlank = i == 0 || lines[i - 1].Trim().Length == 0;
            if (prevBlank && next.Length > 0 && (forced || (IsAsciiUpper(StripExt(t)) && !t.StartsWith('!'))))
            {
                var name = StripExt(forced ? t[1..].Trim() : t);
                if (name.EndsWith('^')) name = name[..^1].Trim();   // 듀얼 다이얼로그 표식
                string? paren = null;
                var sb = new StringBuilder();
                i++;
                for (; i < lines.Length; i++)
                {
                    var l = lines[i].Trim();
                    if (l.Length == 0) break;
                    if (l.StartsWith('(') && l.EndsWith(')')) { if (sb.Length == 0) paren = l[1..^1].Trim(); continue; }
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(l);
                }
                cur.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Dialogue, Character = name, Parenthetical = paren, Text = sb.ToString() });
                continue;
            }
            var action = t.StartsWith('!') ? t[1..].Trim() : t;
            if (cur.Elements.Count > 0 && cur.Elements[^1].Kind == ScriptElementKind.Action && !prevBlank) cur.Elements[^1].Text += " " + action;
            else cur.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Action, Text = action });
        }
        foreach (var s in doc.Scenes)
        {
            if (s.Seconds <= 0)
            {
                var words = 0;
                foreach (var e in s.Elements) if (e.Kind is ScriptElementKind.Action or ScriptElementKind.Dialogue) words += e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                s.Seconds = Math.Clamp(Math.Round(words / 2.5, 1), 3, 60);   // 대략 2.5단어/초
            }
        }
        return doc;
    }

    private static ScriptScene AddImplicit(ScreenplayDoc doc)
    {
        var s = new ScriptScene { Number = doc.Scenes.Count + 1, Heading = "INT. 미정 - 낮" };
        doc.Scenes.Add(s);
        return s;
    }

    private static string StripExt(string name)
    {
        var p = name.IndexOf('(');
        return (p > 0 ? name[..p] : name).Trim();
    }

    // ══════════════════════ 생성 진입점 ══════════════════════

    public static string BuildLlmInstruction(WorldBible bible, List<Beat> beats, StoryStructure structure, double seconds)
    {
        var sb = new StringBuilder(2048);
        sb.Append("Write a screenplay in Fountain syntax (Korean dialogue). Title: ").Append(bible.Lore.Title).Append(". Setting: ").Append(bible.Lore.Setting).Append(". Tone: ").Append(bible.Lore.Tone)
          .Append(". Total length ≈ ").Append(seconds.ToString("0", CultureInfo.InvariantCulture)).Append(" seconds. Structure: ").Append(structure == StoryStructure.ThreeAct ? "3-act" : "short-form highlight").Append(".\nCharacters:\n");
        foreach (var c in bible.Characters) sb.Append("- @").Append(c.Name).Append(" (").Append(c.Role).Append("; want ").Append(c.Want).Append("; flaw ").Append(c.Flaw).Append(")\n");
        sb.Append("One scene per beat, in order:\n");
        foreach (var b in beats) sb.Append(b.Index.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(b.Name).Append(" — ").Append(b.Summary).Append('\n');
        sb.Append("For every scene: heading (INT./EXT.), [[BEAT n]], [[SEC seconds]], action, [[CAM: English camera move]], dialogue with @Name cues and (parentheticals). Output Fountain only.");
        return sb.ToString();
    }

    /// <summary>세계관 → 비트 시트 → 대본(LLM 우선 · 파싱 실패/씬 부족 시 BCL).</summary>
    public async Task<ScreenplayDoc> GenerateAsync(WorldBible bible, StoryStructure structure, double targetSeconds, bool useLlm = true, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _busy, 1);
        LastAction = "✍ 대본 작성 중";
        Raise();
        try
        {
            var beats = BuildBeatSheet(bible, structure);
            ScreenplayDoc? doc = null;
            if (useLlm && _gemini is { Enabled: true } g)
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    var r = await g.GenerateAsync(BuildLlmInstruction(bible, beats, structure, targetSeconds), null, 0.85, d.Token).ConfigureAwait(false);
                    if (r.Ok)
                    {
                        var parsed = ParseFountain(GeminiClient.StripFence(r.Text ?? ""), structure);
                        if (parsed.Scenes.Count >= Math.Max(3, beats.Count / 2)) { parsed.Beats = beats; parsed.Title = string.IsNullOrWhiteSpace(parsed.Title) ? bible.Lore.Title : parsed.Title; parsed.Source = "Gemini"; doc = parsed; }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            doc ??= WriteScript(bible, beats, structure, targetSeconds);
            Current = doc;
            LastAction = "✍ 대본 " + doc.Scenes.Count.ToString(CultureInfo.InvariantCulture) + "씬 · " + doc.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "초 · " + doc.Source;
            return doc;
        }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("BeatSheet.OnChange", ex); }
        }
    }
}
